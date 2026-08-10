using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using Clipper2Lib;
using Svg;

namespace VectorAnimationEngine;

[Flags]
internal enum ImportedSvgBreakApproximation
{
    None = 0,
    NonZeroFillRule = 1 << 1,
    FlattenedGroupOpacity = 1 << 2,
    NonUniformStrokeScale = 1 << 3,
    FlattenedPaintOrder = 1 << 4,
    StrokeStyle = 1 << 5,
    RasterizedContent = 1 << 6,
    SkippedInvalidGeometry = 1 << 7
}

internal readonly record struct ImportedSvgBreakPaint(Color Color);

internal sealed record ImportedSvgBreakLayer(int Key, int Order, string Name);

internal abstract record ImportedSvgBreakPart(
    int Order,
    int LayerKey,
    string SourceElementName,
    ImportedSvgBreakPaint Paint);

internal sealed record ImportedSvgBreakFill(
    int Order,
    int LayerKey,
    string SourceElementName,
    ImportedSvgBreakPaint Paint,
    PointF[][] Contours,
    bool UsesEvenOddFillRule)
    : ImportedSvgBreakPart(Order, LayerKey, SourceElementName, Paint);

internal sealed record ImportedSvgBreakStroke(
    int Order,
    int LayerKey,
    string SourceElementName,
    ImportedSvgBreakPaint Paint,
    PointF[] Points,
    bool Closed,
    float Width,
    LineEndpointStyle StartEndpointStyle,
    LineEndpointStyle EndEndpointStyle)
    : ImportedSvgBreakPart(Order, LayerKey, SourceElementName, Paint);

internal sealed record ImportedSvgBreakResult(
    SizeF IntrinsicSize,
    ImportedSvgBreakLayer[] Layers,
    ImportedSvgBreakPart[] Parts,
    ImportedSvgBreakApproximation Approximations);

internal static class ImportedSvgBreakApart
{
    private const long MaxSourceBytes = 16L * 1024 * 1024;
    private const int MaxXmlDepth = 256;
    private const int MaxOutputParts = 16_384;
    private const int MaxOutputPoints = 500_000;
    private const int MaxRasterColorParts = 4_096;
    private const float FlatteningTolerance = 0.05f;
    private const double DominantGroupGeometryRatio = 0.9;
    private const int LayerOverlapPrecision = 6;
    private const int LayerBoundsLeafSize = 8;
    private const int MaxOutputLayerNameLength = 80;
    private const float MinimumReliableWidenWidth = 8f;
    private const int RasterLongEdge = 256;
    private const string InkscapeNamespace = "http://www.inkscape.org/namespaces/inkscape";
    private const string InkscapeGroupModeAttribute = InkscapeNamespace + ":groupmode";
    private const string InkscapeLabelAttribute = InkscapeNamespace + ":label";
    private static readonly int[] RasterChannelLevelCandidates = [16, 12, 8];

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    static ImportedSvgBreakApart()
    {
        SvgDocument.DisableDtdProcessing = true;
        SvgDocument.ResolveExternalXmlEntites = ExternalType.None;
        SvgDocument.ResolveExternalImages = ExternalType.None;
        SvgDocument.ResolveExternalElements = ExternalType.None;
    }

    /// <summary>
    /// Converts a self-contained SVG into scene-independent geometry in document-local coordinates.
    /// The method never mutates a scene and only returns after the complete document has converted.
    /// </summary>
    public static ImportedSvgBreakResult Extract(string source) => Extract(source, null);

    /// <summary>
    /// Converts SVG geometry and applies an optional outer transform after the document viewBox.
    /// Composition code can use this overload with the original SVG source instead of decoding an
    /// internal data-image wrapper generated solely for raster composition.
    /// </summary>
    public static ImportedSvgBreakResult Extract(string source, Matrix? outerTransform)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new InvalidDataException("The imported SVG source is empty.");
        var sourceBytes = StrictUtf8.GetBytes(source);
        if (sourceBytes.LongLength > MaxSourceBytes)
        {
            throw new InvalidDataException($"Imported SVG source must not exceed {MaxSourceBytes} bytes.");
        }

        var requiresRasterFallback = ValidateXml(source);
        SvgDocument document;
        try
        {
            using var stream = new MemoryStream(sourceBytes, writable: false);
            document = SvgDocument.Open<SvgDocument>(stream, new SvgOptions())
                ?? throw new InvalidDataException("The imported SVG document is empty.");
        }
        catch (Exception exception) when (exception is not InvalidDataException)
        {
            throw new InvalidDataException("SVG.NET could not parse the imported SVG for break-apart.", exception);
        }

        var intrinsicSize = document.GetDimensions();
        ValidateSize(intrinsicSize);

        using var outerTransformClone = outerTransform is null ? null : outerTransform.Clone();
        if (requiresRasterFallback)
        {
            return RasterizeDocument(document, intrinsicSize, outerTransformClone);
        }

        try
        {
            var state = new ExtractionState();
            state.PlanDocumentLayers(document);
            var rootOpacity = Math.Clamp(document.Opacity, 0f, 1f);
            if (rootOpacity < 1f) state.Approximations |= ImportedSvgBreakApproximation.FlattenedGroupOpacity;
            using var rootViewBox = CreateFragmentViewBoxTransform(document, intrinsicSize);
            using var rootTransform = GetElementTransform(document);
            IReadOnlyList<Matrix> outerTransforms = outerTransformClone is null
                ? Array.Empty<Matrix>()
                : new Matrix[] { outerTransformClone };
            var rootTransforms = ComposeTransformList(rootViewBox, rootTransform, outerTransforms);
            using var rendererBitmap = new Bitmap(1, 1);
            using var renderer = SvgRenderer.FromImage(rendererBitmap);
            foreach (var child in document.Children)
            {
                ExtractElement(child, rootTransforms, rootOpacity, -1, state, renderer);
            }

            if (state.Parts.Count == 0)
            {
                return RasterizeDocument(document, intrinsicSize, outerTransformClone);
            }
            var strokeSeen = false;
            foreach (var part in state.Parts)
            {
                if (part is ImportedSvgBreakStroke) strokeSeen = true;
                else if (strokeSeen && part is ImportedSvgBreakFill)
                {
                    state.Approximations |= ImportedSvgBreakApproximation.FlattenedPaintOrder;
                    break;
                }
            }
            state.CoalesceNonOverlappingLayers();

            return new ImportedSvgBreakResult(
                intrinsicSize,
                state.ResultLayers(),
                state.Parts.ToArray(),
                state.Approximations);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return RasterizeDocument(document, intrinsicSize, outerTransformClone);
        }
    }

    private static void ExtractElement(
        SvgElement element,
        IReadOnlyList<Matrix> ancestorTransforms,
        float ancestorOpacity,
        int inheritedLayerKey,
        ExtractionState state,
        ISvgRenderer renderer)
    {
        var layerKey = state.ResolveLayerKey(element, inheritedLayerKey);
        var name = ElementTag(element);
        if (element is SvgGradientServer || IsNonRenderingDefinition(name)) return;
        if (string.Equals(element.Display?.Trim(), "none", StringComparison.OrdinalIgnoreCase)) return;

        var opacity = Math.Clamp(ancestorOpacity * element.Opacity, 0f, 1f);
        if (opacity <= 0f) return;
        if (element.Opacity < 1f && element.Children.Count > 0)
        {
            state.Approximations |= ImportedSvgBreakApproximation.FlattenedGroupOpacity;
        }

        if (element is SvgFragment fragment)
        {
            var size = fragment.GetDimensions();
            ValidateSize(size);
            using var viewBox = CreateFragmentViewBoxTransform(fragment, size);
            using var transform = GetElementTransform(fragment);
            var transforms = ComposeTransformList(viewBox, transform, ancestorTransforms);
            foreach (var child in fragment.Children)
            {
                ExtractElement(child, transforms, opacity, layerKey, state, renderer);
            }
            return;
        }

        if (IsStructuralContainer(name))
        {
            using var transform = GetElementTransform(element);
            var transforms = ComposeTransformList(transform, null, ancestorTransforms);
            foreach (var child in element.Children)
            {
                ExtractElement(child, transforms, opacity, layerKey, state, renderer);
            }
            return;
        }

        if (element is SvgTextBase textContainer && textContainer.Children.Count > 0)
        {
            using var transform = GetElementTransform(element);
            var transforms = ComposeTransformList(transform, null, ancestorTransforms);
            ExtractVisual(
                textContainer,
                name,
                transforms,
                opacity,
                state.RequireLayerKey(layerKey),
                state,
                renderer);
            foreach (var child in textContainer.Children)
            {
                ExtractElement(child, transforms, opacity, layerKey, state, renderer);
            }
            return;
        }

        if (element is not SvgVisualElement visual
            || (!IsSupportedGeometry(name) && element is not SvgTextBase))
        {
            throw Unsupported(name, "element");
        }
        if (!IsVisible(visual)) return;

        using var elementTransform = GetElementTransform(element);
        var geometryTransforms = ComposeTransformList(elementTransform, null, ancestorTransforms);
        ExtractVisual(visual, name, geometryTransforms, opacity, state.RequireLayerKey(layerKey), state, renderer);
    }

    private static void ExtractVisual(
        SvgVisualElement element,
        string elementName,
        IReadOnlyList<Matrix> transforms,
        float opacity,
        int layerKey,
        ExtractionState state,
        ISvgRenderer renderer)
    {
        if (!string.IsNullOrWhiteSpace(element.Clip) && !string.Equals(element.Clip, "auto", StringComparison.OrdinalIgnoreCase)
            || element.ClipPath is not null
            || element.Filter is not null
            || element.StrokeDashArray is { Count: > 0 }
            || element is SvgMarkerElement marker
                && (marker.MarkerStart is not null || marker.MarkerMid is not null || marker.MarkerEnd is not null))
        {
            throw Unsupported(elementName, "rendered presentation");
        }

        var sourcePath = element.Path(renderer);
        if (sourcePath is null || sourcePath.PointCount == 0) return;
        using var path = (GraphicsPath)sourcePath.Clone();
        foreach (var transform in transforms) path.Transform(transform);
        path.Flatten(null, FlatteningTolerance);
        var figures = ReadFigures(path, state);
        if (figures.Count == 0) return;

        var fillOpacity = ResolveInheritedPresentation(
            element,
            "fill-opacity",
            visual => visual.FillOpacity,
            1f);
        if (TryResolvePaint(element.Fill, element, opacity * fillOpacity, fillDefault: true, out var fillPaint))
        {
            var sourceContours = figures
                .Where(figure => figure.Points.Length >= 3)
                .Select(figure => figure.Points)
                .ToArray();
            if (sourceContours.Length > 0)
            {
                var fillRule = ResolveInheritedPresentation(
                    element,
                    "fill-rule",
                    visual => visual.FillRule,
                    SvgFillRule.NonZero);
                var evenOdd = fillRule == SvgFillRule.EvenOdd;
                var contours = NormalizePaintContours(
                    sourceContours,
                    evenOdd ? FillRule.EvenOdd : FillRule.NonZero,
                    state);
                if (contours.Length == 0)
                {
                    state.Approximations |= ImportedSvgBreakApproximation.SkippedInvalidGeometry;
                }
                else
                {
                    state.AddPart(new ImportedSvgBreakFill(
                        state.NextOrder(),
                        layerKey,
                        elementName,
                        fillPaint,
                        contours,
                        UsesEvenOddFillRule: true));
                }
            }
        }

        var strokeOpacity = ResolveInheritedPresentation(
            element,
            "stroke-opacity",
            visual => visual.StrokeOpacity,
            1f);
        if (!TryResolvePaint(element.Stroke, element, opacity * strokeOpacity, fillDefault: false, out var strokePaint))
        {
            return;
        }

        if (element.StrokeDashArray is { Count: > 0 }) throw Unsupported(elementName, "dashed stroke");
        var strokeWidth = ResolveInheritedPresentation(
            element,
            "stroke-width",
            visual => visual.StrokeWidth,
            new SvgUnit(SvgUnitType.User, 1f));
        var sourceWidth = strokeWidth.ToDeviceValue(renderer, UnitRenderingType.Other, element);
        if (!float.IsFinite(sourceWidth) || sourceWidth <= 0f) return;
        var lineCap = ResolveInheritedPresentation(
            element,
            "stroke-linecap",
            visual => visual.StrokeLineCap,
            SvgStrokeLineCap.Butt);
        var lineJoin = ResolveInheritedPresentation(
            element,
            "stroke-linejoin",
            visual => visual.StrokeLineJoin,
            SvgStrokeLineJoin.Miter);
        var miterLimit = ResolveInheritedPresentation(
            element,
            "stroke-miterlimit",
            visual => visual.StrokeMiterLimit,
            4f);
        using var strokeOutline = CreateStrokeOutline(
            sourcePath,
            sourceWidth,
            lineCap,
            lineJoin,
            miterLimit,
            transforms,
            state);
        var outlineFigures = ReadFigures(strokeOutline, state);
        var outlineContours = NormalizePaintContours(
            outlineFigures
                .Where(figure => figure.Points.Length >= 3)
                .Select(figure => figure.Points),
            FillRule.NonZero,
            state);
        if (outlineContours.Length == 0)
        {
            state.Approximations |= ImportedSvgBreakApproximation.SkippedInvalidGeometry;
            return;
        }
        state.AddPart(new ImportedSvgBreakFill(
            state.NextOrder(),
            layerKey,
            elementName,
            strokePaint,
            outlineContours,
            UsesEvenOddFillRule: true));
    }

    private static GraphicsPath CreateStrokeOutline(
        GraphicsPath sourcePath,
        float sourceWidth,
        SvgStrokeLineCap lineCap,
        SvgStrokeLineJoin lineJoin,
        float miterLimit,
        IReadOnlyList<Matrix> transforms,
        ExtractionState state)
    {
        var outline = (GraphicsPath)sourcePath.Clone();
        try
        {
            var widenScale = Math.Max(1f, MinimumReliableWidenWidth / sourceWidth);
            if (widenScale > 1f)
            {
                using var scale = new Matrix(widenScale, 0f, 0f, widenScale, 0f, 0f);
                outline.Transform(scale);
            }

            using var pen = new Pen(Color.Black, sourceWidth * widenScale)
            {
                StartCap = StrokeLineCap(lineCap),
                EndCap = StrokeLineCap(lineCap),
                LineJoin = StrokeLineJoin(lineJoin, state),
                MiterLimit = Math.Max(1f, miterLimit)
            };
            outline.Widen(pen);
            if (widenScale > 1f)
            {
                using var inverseScale = new Matrix(1f / widenScale, 0f, 0f, 1f / widenScale, 0f, 0f);
                outline.Transform(inverseScale);
            }
            foreach (var transform in transforms) outline.Transform(transform);
            var tolerance = Math.Clamp(sourceWidth * 0.125f, 0.01f, FlatteningTolerance);
            outline.Flatten(null, tolerance);
            return outline;
        }
        catch
        {
            outline.Dispose();
            throw;
        }
    }

    private static LineCap StrokeLineCap(SvgStrokeLineCap lineCap)
    {
        return lineCap switch
        {
            SvgStrokeLineCap.Round => LineCap.Round,
            SvgStrokeLineCap.Square => LineCap.Square,
            _ => LineCap.Flat
        };
    }

    private static LineJoin StrokeLineJoin(SvgStrokeLineJoin lineJoin, ExtractionState state)
    {
        return lineJoin switch
        {
            SvgStrokeLineJoin.Round => LineJoin.Round,
            SvgStrokeLineJoin.Bevel => LineJoin.Bevel,
            SvgStrokeLineJoin.MiterClip => LineJoin.MiterClipped,
            SvgStrokeLineJoin.Arcs => ApproximateArcsJoin(state),
            _ => LineJoin.Miter
        };
    }

    private static LineJoin ApproximateArcsJoin(ExtractionState state)
    {
        state.Approximations |= ImportedSvgBreakApproximation.StrokeStyle;
        return LineJoin.Round;
    }

    private static PointF[][] NormalizePaintContours(
        IEnumerable<PointF[]> sourceContours,
        FillRule fillRule,
        ExtractionState state)
    {
        var source = new PathsD(sourceContours
            .Where(contour => contour.Length >= 3)
            .Select(contour => new PathD(contour.Select(point => new PointD(point.X, point.Y)))));
        if (source.Count == 0) return [];

        var normalized = Clipper.BooleanOp(
            ClipType.Union,
            source,
            new PathsD(),
            fillRule,
            LayerOverlapPrecision);
        var result = normalized
            .Where(path => path.Count >= 3 && Math.Abs(Clipper.Area(path)) > double.Epsilon)
            .Select(path => path.Select(point => new PointF((float)point.x, (float)point.y)).ToArray())
            .ToArray();
        state.AddPoints(result.Sum(contour => contour.Length));
        return result;
    }

    private static T ResolveInheritedPresentation<T>(
        SvgVisualElement element,
        string attributeName,
        Func<SvgVisualElement, T> value,
        T defaultValue)
    {
        for (SvgElement? current = element; current is not null; current = current.Parent)
        {
            if (current is SvgVisualElement visual && visual.ContainsAttribute(attributeName))
            {
                return value(visual);
            }
        }
        return defaultValue;
    }

    private static ImportedSvgBreakResult RasterizeDocument(
        SvgDocument document,
        SizeF intrinsicSize,
        Matrix? outerTransform)
    {
        var rasterSize = GetRasterSize(intrinsicSize);
        using var rendered = document.Draw(rasterSize.Width, rasterSize.Height);
        Bitmap? normalized = null;
        try
        {
            var raster = rendered;
            var pixelFormat = raster.PixelFormat;
            if (pixelFormat is not PixelFormat.Format32bppArgb and not PixelFormat.Format32bppPArgb)
            {
                normalized = new Bitmap(rasterSize.Width, rasterSize.Height, PixelFormat.Format32bppPArgb);
                using var graphics = Graphics.FromImage(normalized);
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.Clear(Color.Transparent);
                graphics.DrawImageUnscaled(rendered, 0, 0);
                raster = normalized;
                pixelFormat = PixelFormat.Format32bppPArgb;
            }

            Dictionary<int, Paths64>? runsByColor = null;
            foreach (var channelLevels in RasterChannelLevelCandidates)
            {
                runsByColor = ReadRasterRuns(raster, pixelFormat, channelLevels);
                if (runsByColor.Count <= MaxRasterColorParts) break;
            }
            if (runsByColor is null || runsByColor.Count > MaxRasterColorParts)
            {
                throw new InvalidDataException("The rasterized SVG exceeds the supported color complexity.");
            }
            var state = new ExtractionState
            {
                Approximations = ImportedSvgBreakApproximation.RasterizedContent
            };
            var rasterLayerKey = state.CreateLayer("SVG Rasterized");
            var scaleX = intrinsicSize.Width / rasterSize.Width;
            var scaleY = intrinsicSize.Height / rasterSize.Height;
            foreach (var entry in runsByColor.OrderBy(item => unchecked((uint)item.Key)))
            {
                Paths64 union;
                try
                {
                    union = Clipper.Union(entry.Value, FillRule.NonZero);
                }
                catch (Exception exception) when (exception is ClipperLibException or OverflowException)
                {
                    throw new InvalidDataException("The rasterized SVG could not be converted to bounded vector geometry.", exception);
                }

                var contours = union
                    .Where(path => path.Count >= 3)
                    .OrderByDescending(path => Math.Abs(Clipper.Area(path)))
                    .ThenBy(path => path.Min(point => point.Y))
                    .ThenBy(path => path.Min(point => point.X))
                    .Select(path => TransformRasterContour(path, scaleX, scaleY, outerTransform, state))
                    .ToArray();
                if (contours.Length == 0) continue;
                state.AddPart(new ImportedSvgBreakFill(
                    state.NextOrder(),
                    rasterLayerKey,
                    "svg",
                    new ImportedSvgBreakPaint(Color.FromArgb(entry.Key)),
                    contours,
                    UsesEvenOddFillRule: true));
            }

            if (state.Parts.Count == 0)
            {
                var bounds = new[]
                {
                    PointF.Empty,
                    new PointF(intrinsicSize.Width, 0),
                    new PointF(intrinsicSize.Width, intrinsicSize.Height),
                    new PointF(0, intrinsicSize.Height)
                };
                TransformPoints(bounds, outerTransform);
                state.AddPoints(bounds.Length);
                state.AddPart(new ImportedSvgBreakFill(
                    state.NextOrder(),
                    rasterLayerKey,
                    "svg",
                    new ImportedSvgBreakPaint(Color.Transparent),
                    new[] { bounds },
                    UsesEvenOddFillRule: true));
            }

            return new ImportedSvgBreakResult(
                intrinsicSize,
                state.ResultLayers(),
                state.Parts.ToArray(),
                state.Approximations);
        }
        finally
        {
            normalized?.Dispose();
        }
    }

    private static Size GetRasterSize(SizeF intrinsicSize)
    {
        var scale = RasterLongEdge / (double)Math.Max(intrinsicSize.Width, intrinsicSize.Height);
        return new Size(
            Math.Max(1, (int)Math.Round(intrinsicSize.Width * scale)),
            Math.Max(1, (int)Math.Round(intrinsicSize.Height * scale)));
    }

    private static Dictionary<int, Paths64> ReadRasterRuns(
        Bitmap bitmap,
        PixelFormat pixelFormat,
        int channelLevels)
    {
        var result = new Dictionary<int, Paths64>();
        var bounds = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        BitmapData? bitmapData = null;
        try
        {
            bitmapData = bitmap.LockBits(bounds, ImageLockMode.ReadOnly, pixelFormat);
            var row = new byte[checked(bitmap.Width * 4)];
            var premultiplied = pixelFormat == PixelFormat.Format32bppPArgb;
            for (var y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(IntPtr.Add(bitmapData.Scan0, checked(y * bitmapData.Stride)), row, 0, row.Length);
                var x = 0;
                while (x < bitmap.Width)
                {
                    var color = QuantizeArgb(row, x * 4, premultiplied, channelLevels);
                    var runStart = x++;
                    while (x < bitmap.Width
                        && QuantizeArgb(row, x * 4, premultiplied, channelLevels) == color)
                    {
                        x++;
                    }
                    if ((color >>> 24) == 0) continue;

                    if (!result.TryGetValue(color, out var paths))
                    {
                        paths = new Paths64();
                        result.Add(color, paths);
                    }
                    paths.Add(new Path64
                    {
                        new(runStart, y),
                        new(x, y),
                        new(x, y + 1),
                        new(runStart, y + 1)
                    });
                }
            }
        }
        finally
        {
            if (bitmapData is not null) bitmap.UnlockBits(bitmapData);
        }
        return result;
    }

    private static int QuantizeArgb(byte[] row, int offset, bool premultiplied, int channelLevels)
    {
        var blue = row[offset];
        var green = row[offset + 1];
        var red = row[offset + 2];
        var alpha = row[offset + 3];
        if (alpha == 0) return 0;
        if (premultiplied)
        {
            blue = Unpremultiply(blue, alpha);
            green = Unpremultiply(green, alpha);
            red = Unpremultiply(red, alpha);
        }

        var quantizedAlpha = Math.Max(1, QuantizeChannel(alpha, channelLevels));
        return Color.FromArgb(
            ExpandChannel(quantizedAlpha, channelLevels),
            ExpandChannel(QuantizeChannel(red, channelLevels), channelLevels),
            ExpandChannel(QuantizeChannel(green, channelLevels), channelLevels),
            ExpandChannel(QuantizeChannel(blue, channelLevels), channelLevels)).ToArgb();
    }

    private static byte Unpremultiply(byte channel, byte alpha)
    {
        return (byte)Math.Min(255, (channel * 255 + alpha / 2) / alpha);
    }

    private static int QuantizeChannel(byte channel, int channelLevels)
    {
        return (channel * (channelLevels - 1) + 127) / 255;
    }

    private static int ExpandChannel(int channel, int channelLevels)
    {
        return (channel * 255 + (channelLevels - 1) / 2) / (channelLevels - 1);
    }

    private static PointF[] TransformRasterContour(
        Path64 path,
        float scaleX,
        float scaleY,
        Matrix? outerTransform,
        ExtractionState state)
    {
        var contour = path
            .Select(point => new PointF(point.X * scaleX, point.Y * scaleY))
            .ToArray();
        TransformPoints(contour, outerTransform);
        state.AddPoints(contour.Length);
        return contour;
    }

    private static void TransformPoints(PointF[] points, Matrix? transform)
    {
        transform?.TransformPoints(points);
        if (points.Any(point => !float.IsFinite(point.X) || !float.IsFinite(point.Y)))
        {
            throw new InvalidDataException("The SVG produced non-finite rasterized geometry.");
        }
    }

    private static List<FlattenedFigure> ReadFigures(GraphicsPath path, ExtractionState state)
    {
        var points = path.PathPoints;
        var types = path.PathTypes;
        var result = new List<FlattenedFigure>();
        var current = new List<PointF>();
        var closed = false;
        for (var index = 0; index < points.Length; index++)
        {
            var type = (PathPointType)(types[index] & (byte)PathPointType.PathTypeMask);
            if (type == PathPointType.Start && current.Count > 0)
            {
                AddFigure(result, current, closed, state);
                current.Clear();
                closed = false;
            }

            var point = points[index];
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y))
            {
                throw new InvalidDataException("The SVG produced non-finite geometry.");
            }
            if (current.Count == 0 || current[^1] != point) current.Add(point);
            closed |= (types[index] & (byte)PathPointType.CloseSubpath) != 0;
        }
        AddFigure(result, current, closed, state);
        return result;
    }

    private static void AddFigure(
        ICollection<FlattenedFigure> output,
        IReadOnlyList<PointF> points,
        bool closed,
        ExtractionState state)
    {
        if (points.Count < 2) return;
        var normalized = points.ToArray();
        if (closed && normalized.Length > 2 && normalized[^1] == normalized[0]) normalized = normalized[..^1];
        if (normalized.Length < 2) return;
        state.AddPoints(normalized.Length);
        output.Add(new FlattenedFigure(normalized, closed));
    }

    private static bool TryResolvePaint(
        SvgPaintServer? paint,
        SvgVisualElement owner,
        float opacity,
        bool fillDefault,
        out ImportedSvgBreakPaint result)
    {
        result = default;
        var paintText = paint?.ToString()?.Trim();
        if (paint is null
            || ReferenceEquals(paint, SvgPaintServer.None)
            || string.Equals(paintText, "none", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (ReferenceEquals(paint, SvgPaintServer.NotSet)
            || string.IsNullOrWhiteSpace(paintText)
            || string.Equals(paintText, "notSet", StringComparison.OrdinalIgnoreCase))
        {
            if (!fillDefault) return false;
            result = new ImportedSvgBreakPaint(ApplyOpacity(Color.Black, opacity));
            return result.Color.A > 0;
        }

        var colour = SvgDeferredPaintServer.TryGet<SvgColourServer>(paint, owner);
        if (colour is not null)
        {
            result = new ImportedSvgBreakPaint(ApplyOpacity(colour.Colour, opacity));
            return result.Color.A > 0;
        }

        var gradient = SvgDeferredPaintServer.TryGet<SvgGradientServer>(paint, owner);
        if (gradient is null) throw Unsupported(ElementTag(owner), "paint server");
        throw Unsupported(ElementTag(owner), "gradient paint");
    }

    private static Matrix CreateFragmentViewBoxTransform(SvgFragment fragment, SizeF targetSize)
    {
        var viewBox = fragment.ViewBox;
        var x = fragment is SvgDocument
            ? 0f
            : fragment.X.ToDeviceValue(null, UnitRenderingType.Horizontal, fragment);
        var y = fragment is SvgDocument
            ? 0f
            : fragment.Y.ToDeviceValue(null, UnitRenderingType.Vertical, fragment);
        if (viewBox == SvgViewBox.Empty) return new Matrix(1, 0, 0, 1, x, y);
        if (viewBox.Width <= 0 || viewBox.Height <= 0) throw new InvalidDataException("The SVG viewBox is invalid.");

        var scaleX = targetSize.Width / viewBox.Width;
        var scaleY = targetSize.Height / viewBox.Height;
        var aspect = fragment.AspectRatio;
        if (aspect is not null && aspect.Align != SvgPreserveAspectRatio.none)
        {
            var uniform = aspect.Slice ? Math.Max(scaleX, scaleY) : Math.Min(scaleX, scaleY);
            scaleX = uniform;
            scaleY = uniform;
        }

        var extraX = targetSize.Width - viewBox.Width * scaleX;
        var extraY = targetSize.Height - viewBox.Height * scaleY;
        var align = aspect?.Align ?? SvgPreserveAspectRatio.xMidYMid;
        var alignX = align is SvgPreserveAspectRatio.xMidYMin or SvgPreserveAspectRatio.xMidYMid or SvgPreserveAspectRatio.xMidYMax
            ? extraX * 0.5f
            : align is SvgPreserveAspectRatio.xMaxYMin or SvgPreserveAspectRatio.xMaxYMid or SvgPreserveAspectRatio.xMaxYMax
                ? extraX
                : 0f;
        var alignY = align is SvgPreserveAspectRatio.xMinYMid or SvgPreserveAspectRatio.xMidYMid or SvgPreserveAspectRatio.xMaxYMid
            ? extraY * 0.5f
            : align is SvgPreserveAspectRatio.xMinYMax or SvgPreserveAspectRatio.xMidYMax or SvgPreserveAspectRatio.xMaxYMax
                ? extraY
                : 0f;
        return new Matrix(scaleX, 0, 0, scaleY, x + alignX - viewBox.MinX * scaleX, y + alignY - viewBox.MinY * scaleY);
    }

    private static Matrix GetElementTransform(SvgElement element)
    {
        return element.Transforms is { Count: > 0 }
            ? element.Transforms.GetMatrix()
            : new Matrix();
    }

    private static Matrix[] ComposeTransformList(Matrix? first, Matrix? second, IReadOnlyList<Matrix> remainder)
    {
        var count = (first is null ? 0 : 1) + (second is null ? 0 : 1) + remainder.Count;
        var result = new Matrix[count];
        var index = 0;
        if (first is not null) result[index++] = first;
        if (second is not null) result[index++] = second;
        for (var remainderIndex = 0; remainderIndex < remainder.Count; remainderIndex++)
        {
            result[index++] = remainder[remainderIndex];
        }
        return result;
    }

    private static float MeasureStrokeScale(IReadOnlyList<Matrix> transforms, ExtractionState state)
    {
        var basis = new[] { PointF.Empty, new PointF(1, 0), new PointF(0, 1) };
        foreach (var transform in transforms) transform.TransformPoints(basis);
        var scaleX = Distance(basis[0], basis[1]);
        var scaleY = Distance(basis[0], basis[2]);
        if (!float.IsFinite(scaleX) || !float.IsFinite(scaleY) || scaleX <= 0 || scaleY <= 0)
        {
            throw new InvalidDataException("The SVG has a singular stroke transform.");
        }
        if (Math.Abs(scaleX - scaleY) > Math.Max(scaleX, scaleY) * 0.001f)
        {
            state.Approximations |= ImportedSvgBreakApproximation.NonUniformStrokeScale;
        }
        return MathF.Sqrt(scaleX * scaleY);
    }

    private static bool IsVisible(SvgVisualElement element)
    {
        return string.Equals(element.Visibility?.Trim(), "visible", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStructuralContainer(string name) => name is "g" or "a";

    private static bool IsSupportedGeometry(string name)
    {
        return name is "path" or "rect" or "circle" or "ellipse" or "line" or "polyline" or "polygon";
    }

    private static string ElementTag(SvgElement element)
    {
        return element.GetType().Name switch
        {
            "SvgDocument" or "SvgFragment" => "svg",
            "SvgGroup" => "g",
            "SvgAnchor" => "a",
            "SvgPath" => "path",
            "SvgRectangle" => "rect",
            "SvgCircle" => "circle",
            "SvgEllipse" => "ellipse",
            "SvgLine" => "line",
            "SvgPolyline" => "polyline",
            "SvgPolygon" => "polygon",
            "SvgText" => "text",
            "SvgTextSpan" => "tspan",
            "SvgTextPath" => "textPath",
            "SvgDefinitionList" => "defs",
            "SvgStyle" => "style",
            "SvgTitle" => "title",
            "SvgDescription" => "desc",
            "SvgDocumentMetadata" => "metadata",
            var typeName => typeName
        };
    }

    private static bool IsNonRenderingDefinition(string name)
    {
        return name is "defs" or "style" or "title" or "desc" or "metadata";
    }

    private static UnsupportedSvgFeatureException Unsupported(string? elementName, string feature)
    {
        var name = string.IsNullOrWhiteSpace(elementName) ? "unknown" : elementName;
        return new UnsupportedSvgFeatureException($"SVG break-apart does not support {feature} on <{name}>.");
    }

    private static bool ValidateXml(string source)
    {
        var requiresRasterFallback = false;
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxSourceBytes,
            MaxCharactersFromEntities = 0
        };
        using var text = new StringReader(source);
        using var reader = XmlReader.Create(text, settings);
        while (reader.Read())
        {
            if (reader.Depth > MaxXmlDepth) throw new InvalidDataException($"SVG XML depth must not exceed {MaxXmlDepth}.");
            if (reader.NodeType == XmlNodeType.ProcessingInstruction)
            {
                throw new InvalidDataException("SVG processing instructions are not supported for break-apart.");
            }
            if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA
                && ContainsRasterOnlyCss(reader.Value))
            {
                requiresRasterFallback = true;
            }
            if (reader.NodeType != XmlNodeType.Element || !reader.HasAttributes) continue;
            for (var index = 0; index < reader.AttributeCount; index++)
            {
                reader.MoveToAttribute(index);
                if (reader.LocalName is "clip" or "clip-path" or "mask" or "filter" or "marker"
                    or "marker-start" or "marker-mid" or "marker-end" or "vector-effect" or "mix-blend-mode"
                    or "stroke-dasharray" or "stroke-dashoffset" or "paint-order" or "overflow")
                {
                    requiresRasterFallback = true;
                }
                if (reader.LocalName == "style" && ContainsRasterOnlyCss(reader.Value))
                {
                    requiresRasterFallback = true;
                }
            }
            reader.MoveToElement();
        }
        return requiresRasterFallback;
    }

    private static bool ContainsRasterOnlyCss(string value)
    {
        return value.Contains("clip:", StringComparison.OrdinalIgnoreCase)
            || value.Contains("clip-path:", StringComparison.OrdinalIgnoreCase)
            || value.Contains("mask:", StringComparison.OrdinalIgnoreCase)
            || value.Contains("filter:", StringComparison.OrdinalIgnoreCase)
            || value.Contains("marker:", StringComparison.OrdinalIgnoreCase)
            || value.Contains("marker-start:", StringComparison.OrdinalIgnoreCase)
            || value.Contains("marker-mid:", StringComparison.OrdinalIgnoreCase)
            || value.Contains("marker-end:", StringComparison.OrdinalIgnoreCase)
            || value.Contains("vector-effect:", StringComparison.OrdinalIgnoreCase)
            || value.Contains("mix-blend-mode:", StringComparison.OrdinalIgnoreCase)
            || value.Contains("stroke-dasharray:", StringComparison.OrdinalIgnoreCase)
            || value.Contains("stroke-dashoffset:", StringComparison.OrdinalIgnoreCase)
            || value.Contains("paint-order:", StringComparison.OrdinalIgnoreCase)
            || value.Contains("overflow:", StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateSize(SizeF size)
    {
        if (!float.IsFinite(size.Width) || !float.IsFinite(size.Height) || size.Width <= 0 || size.Height <= 0)
        {
            throw new InvalidDataException("The SVG has no finite positive dimensions.");
        }
    }

    private static PointF[] ClosePolyline(PointF[] points)
    {
        if (points.Length == 0 || points[0] == points[^1]) return points;
        var closed = new PointF[points.Length + 1];
        points.CopyTo(closed, 0);
        closed[^1] = points[0];
        return closed;
    }

    private static Color ApplyOpacity(Color color, float opacity)
    {
        var alpha = (int)Math.Clamp(MathF.Round(color.A * Math.Clamp(opacity, 0f, 1f)), 0, 255);
        return Color.FromArgb(alpha, color.R, color.G, color.B);
    }

    private static float Distance(PointF first, PointF second)
    {
        var dx = second.X - first.X;
        var dy = second.Y - first.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private readonly record struct FlattenedFigure(PointF[] Points, bool Closed);

    private readonly record struct GeometryBounds(double Left, double Top, double Right, double Bottom)
    {
        public static GeometryBounds Conservative => new(
            -float.MaxValue,
            -float.MaxValue,
            float.MaxValue,
            float.MaxValue);

        public bool IsFinite => double.IsFinite(Left)
            && double.IsFinite(Top)
            && double.IsFinite(Right)
            && double.IsFinite(Bottom);

        public bool HasArea => IsFinite && Left < Right && Top < Bottom;

        public bool Intersects(GeometryBounds other)
        {
            return Left < other.Right
                && Right > other.Left
                && Top < other.Bottom
                && Bottom > other.Top;
        }
    }

    private sealed record PartOccupancy(GeometryBounds Bounds, PathsD Geometry, bool IsConservative);

    private sealed record LayerOccupancy(
        int SourceIndex,
        ImportedSvgBreakLayer Layer,
        GeometryBounds Bounds,
        PartOccupancy[] Parts);

    private sealed class UnsupportedSvgFeatureException(string message) : Exception(message);

    private sealed class ExtractionState
    {
        private readonly Dictionary<SvgElement, int> _layerOverrides = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<int, ImportedSvgBreakLayer> _plannedLayers = new();
        private readonly HashSet<int> _usedLayerKeys = new();
        private readonly Dictionary<SvgElement, long> _potentialGeometryCounts = new(ReferenceEqualityComparer.Instance);
        private int _pointCount;
        private int _nextLayerKey;
        private int _nextGeneratedLayerName = 1;
        private int _nextGeneratedGroupName = 1;
        private int _nextOrder;
        private int _unplannedLayerKey = -1;

        public List<ImportedSvgBreakPart> Parts { get; } = new();
        public ImportedSvgBreakApproximation Approximations { get; set; }

        public void PlanDocumentLayers(SvgDocument document)
        {
            if (ContainsExplicitLayer(document))
            {
                int? looseLayerKey = null;
                PlanExplicitChildren(document, -1, ref looseLayerKey);
                return;
            }

            PlanOrdinaryContainer(document);
        }

        public int CreateLayer(string name)
        {
            if (_plannedLayers.Count >= MaxOutputParts)
            {
                throw new InvalidDataException($"SVG break-apart output must not exceed {MaxOutputParts} layers.");
            }

            var key = _nextLayerKey++;
            var normalizedName = TruncateLayerName(string.IsNullOrWhiteSpace(name)
                ? NextGeneratedLayerName()
                : name.Trim());
            _plannedLayers.Add(key, new ImportedSvgBreakLayer(key, key, normalizedName));
            return key;
        }

        public int ResolveLayerKey(SvgElement element, int inheritedLayerKey)
        {
            return _layerOverrides.TryGetValue(element, out var layerKey)
                ? layerKey
                : inheritedLayerKey;
        }

        public int RequireLayerKey(int layerKey)
        {
            if (layerKey >= 0) return layerKey;
            if (_unplannedLayerKey < 0) _unplannedLayerKey = CreateLayer(NextGeneratedLayerName());
            return _unplannedLayerKey;
        }

        public ImportedSvgBreakLayer[] ResultLayers()
        {
            return _usedLayerKeys
                .Select(key => _plannedLayers[key])
                .OrderBy(layer => layer.Order)
                .ToArray();
        }

        public void CoalesceNonOverlappingLayers()
        {
            var sourceLayers = ResultLayers();
            if (sourceLayers.Length <= 1) return;

            var partsByLayer = Parts
                .GroupBy(part => part.LayerKey)
                .ToDictionary(group => group.Key, group => group.ToArray());
            var occupancies = sourceLayers
                .Select((layer, sourceIndex) => BuildLayerOccupancy(
                    layer,
                    sourceIndex,
                    partsByLayer[layer.Key]))
                .ToArray();
            var levels = FindMinimumLayerLevels(occupancies);
            var levelCount = levels.Max() + 1;

            if (levelCount == sourceLayers.Length) return;

            var membersByLevel = Enumerable.Range(0, levelCount)
                .Select(_ => new List<ImportedSvgBreakLayer>())
                .ToArray();
            for (var sourceIndex = 0; sourceIndex < sourceLayers.Length; sourceIndex++)
            {
                membersByLevel[levels[sourceIndex]].Add(sourceLayers[sourceIndex]);
            }

            var remappedKeys = new Dictionary<int, int>(sourceLayers.Length);
            var coalescedLayers = new ImportedSvgBreakLayer[levelCount];
            for (var level = 0; level < membersByLevel.Length; level++)
            {
                var members = membersByLevel[level];
                var representative = members[0];
                coalescedLayers[level] = new ImportedSvgBreakLayer(
                    representative.Key,
                    level,
                    CoalescedLayerName(members));
                foreach (var member in members) remappedKeys.Add(member.Key, representative.Key);
            }

            for (var partIndex = 0; partIndex < Parts.Count; partIndex++)
            {
                var part = Parts[partIndex];
                var remappedKey = remappedKeys[part.LayerKey];
                if (remappedKey == part.LayerKey) continue;
                Parts[partIndex] = part switch
                {
                    ImportedSvgBreakFill fill => fill with { LayerKey = remappedKey },
                    ImportedSvgBreakStroke stroke => stroke with { LayerKey = remappedKey },
                    _ => throw new InvalidDataException("SVG break-apart produced an unknown part while coalescing layers.")
                };
            }

            _plannedLayers.Clear();
            _usedLayerKeys.Clear();
            foreach (var layer in coalescedLayers)
            {
                _plannedLayers.Add(layer.Key, layer);
                _usedLayerKeys.Add(layer.Key);
            }
        }

        public int NextOrder() => _nextOrder++;

        public void AddPart(ImportedSvgBreakPart part)
        {
            if (Parts.Count >= MaxOutputParts)
            {
                throw new InvalidDataException($"SVG break-apart output must not exceed {MaxOutputParts} parts.");
            }
            if (!_plannedLayers.ContainsKey(part.LayerKey))
            {
                throw new InvalidDataException("SVG break-apart produced a part without a planned layer.");
            }

            _usedLayerKeys.Add(part.LayerKey);
            Parts.Add(part);
        }

        public void AddPoints(int count)
        {
            _pointCount = checked(_pointCount + count);
            if (_pointCount > MaxOutputPoints)
            {
                throw new InvalidDataException($"SVG break-apart output must not exceed {MaxOutputPoints} points.");
            }
        }

        private static LayerOccupancy BuildLayerOccupancy(
            ImportedSvgBreakLayer layer,
            int sourceIndex,
            IEnumerable<ImportedSvgBreakPart> parts)
        {
            var occupancies = parts
                .Select(TryCreatePartOccupancy)
                .Where(occupancy => occupancy is not null)
                .Select(occupancy => occupancy!)
                .OrderBy(occupancy => occupancy.Bounds.Left)
                .ThenBy(occupancy => occupancy.Bounds.Top)
                .ToArray();
            var bounds = CombineBounds(occupancies.Select(occupancy => occupancy.Bounds));
            return new LayerOccupancy(sourceIndex, layer, bounds, occupancies);
        }

        private static int[] FindMinimumLayerLevels(IReadOnlyList<LayerOccupancy> occupancies)
        {
            var levels = new int[occupancies.Count];
            var maximumEarlierLevel = -1;
            var candidates = new List<LayerOccupancy>();
            var boundsIndex = new LayerBoundsIndex(occupancies);
            for (var sourceIndex = 0; sourceIndex < occupancies.Count; sourceIndex++)
            {
                var current = occupancies[sourceIndex];
                if (!current.Bounds.HasArea)
                {
                    levels[sourceIndex] = 0;
                    maximumEarlierLevel = Math.Max(maximumEarlierLevel, 0);
                    continue;
                }

                candidates.Clear();
                boundsIndex.Query(current.Bounds, sourceIndex, candidates);
                candidates.Sort((first, second) =>
                {
                    var comparison = levels[second.SourceIndex].CompareTo(levels[first.SourceIndex]);
                    return comparison != 0
                        ? comparison
                        : second.SourceIndex.CompareTo(first.SourceIndex);
                });

                var level = 0;
                foreach (var candidate in candidates)
                {
                    var candidateLevel = levels[candidate.SourceIndex];
                    if (candidateLevel < level) break;
                    if (!LayersOverlap(candidate, current)) continue;

                    level = candidateLevel + 1;
                    if (level > maximumEarlierLevel) break;
                }

                levels[sourceIndex] = level;
                maximumEarlierLevel = Math.Max(maximumEarlierLevel, level);
            }
            return levels;
        }

        private static string CoalescedLayerName(IReadOnlyList<ImportedSvgBreakLayer> members)
        {
            var names = members
                .Select(member => member.Name.Trim())
                .Where(name => name.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (names.Length == 0) return "SVG Layer";
            if (names.Length == 1) return TruncateLayerName(names[0]);

            var combined = string.Join(" + ", names);
            if (combined.Length <= MaxOutputLayerNameLength) return combined;

            var suffix = $" + {names.Length - 1}";
            var prefixLength = Math.Max(1, MaxOutputLayerNameLength - suffix.Length);
            return TruncateLayerName(names[0], prefixLength) + suffix;
        }

        private static string TruncateLayerName(string name, int maximumLength = MaxOutputLayerNameLength)
        {
            return name.Length <= maximumLength ? name : name[..maximumLength];
        }

        private readonly record struct LayerBoundsNode(
            GeometryBounds Bounds,
            int Start,
            int Count,
            int FirstChild,
            int SecondChild,
            int MinimumSourceIndex);

        private sealed class LayerBoundsIndex
        {
            private readonly LayerOccupancy[] _items;
            private readonly List<LayerBoundsNode> _nodes = [];
            private readonly int _root;

            public LayerBoundsIndex(IReadOnlyList<LayerOccupancy> occupancies)
            {
                _items = occupancies.Where(occupancy => occupancy.Bounds.HasArea).ToArray();
                _root = _items.Length == 0 ? -1 : Build(0, _items.Length);
            }

            public void Query(
                GeometryBounds bounds,
                int maximumSourceIndex,
                ICollection<LayerOccupancy> result)
            {
                if (_root >= 0) QueryNode(_root, bounds, maximumSourceIndex, result);
            }

            private int Build(int start, int count)
            {
                var bounds = CombineBounds(_items.Skip(start).Take(count).Select(item => item.Bounds));
                var minimumSourceIndex = int.MaxValue;
                for (var index = start; index < start + count; index++)
                {
                    minimumSourceIndex = Math.Min(minimumSourceIndex, _items[index].SourceIndex);
                }

                var nodeIndex = _nodes.Count;
                _nodes.Add(default);
                if (count <= LayerBoundsLeafSize)
                {
                    _nodes[nodeIndex] = new LayerBoundsNode(
                        bounds,
                        start,
                        count,
                        -1,
                        -1,
                        minimumSourceIndex);
                    return nodeIndex;
                }

                var horizontal = bounds.Right - bounds.Left >= bounds.Bottom - bounds.Top;
                Array.Sort(
                    _items,
                    start,
                    count,
                    Comparer<LayerOccupancy>.Create((first, second) =>
                    {
                        var firstCenter = horizontal
                            ? first.Bounds.Left + (first.Bounds.Right - first.Bounds.Left) * 0.5
                            : first.Bounds.Top + (first.Bounds.Bottom - first.Bounds.Top) * 0.5;
                        var secondCenter = horizontal
                            ? second.Bounds.Left + (second.Bounds.Right - second.Bounds.Left) * 0.5
                            : second.Bounds.Top + (second.Bounds.Bottom - second.Bounds.Top) * 0.5;
                        var comparison = firstCenter.CompareTo(secondCenter);
                        return comparison != 0
                            ? comparison
                            : first.SourceIndex.CompareTo(second.SourceIndex);
                    }));
                var firstCount = count / 2;
                var firstChild = Build(start, firstCount);
                var secondChild = Build(start + firstCount, count - firstCount);
                _nodes[nodeIndex] = new LayerBoundsNode(
                    bounds,
                    start,
                    count,
                    firstChild,
                    secondChild,
                    minimumSourceIndex);
                return nodeIndex;
            }

            private void QueryNode(
                int nodeIndex,
                GeometryBounds bounds,
                int maximumSourceIndex,
                ICollection<LayerOccupancy> result)
            {
                var node = _nodes[nodeIndex];
                if (node.MinimumSourceIndex >= maximumSourceIndex || !node.Bounds.Intersects(bounds)) return;
                if (node.FirstChild >= 0)
                {
                    QueryNode(node.FirstChild, bounds, maximumSourceIndex, result);
                    QueryNode(node.SecondChild, bounds, maximumSourceIndex, result);
                    return;
                }

                for (var index = node.Start; index < node.Start + node.Count; index++)
                {
                    var candidate = _items[index];
                    if (candidate.SourceIndex < maximumSourceIndex
                        && candidate.Bounds.Intersects(bounds))
                    {
                        result.Add(candidate);
                    }
                }
            }
        }

        private static bool LayersOverlap(LayerOccupancy first, LayerOccupancy second)
        {
            var firstIndex = 0;
            var secondIndex = 0;
            var activeFirst = new HashSet<int>();
            var activeSecond = new HashSet<int>();
            var firstExpiry = new PriorityQueue<int, double>();
            var secondExpiry = new PriorityQueue<int, double>();
            while (firstIndex < first.Parts.Length || secondIndex < second.Parts.Length)
            {
                var takeFirst = secondIndex >= second.Parts.Length
                    || firstIndex < first.Parts.Length
                    && first.Parts[firstIndex].Bounds.Left <= second.Parts[secondIndex].Bounds.Left;
                var currentLeft = takeFirst
                    ? first.Parts[firstIndex].Bounds.Left
                    : second.Parts[secondIndex].Bounds.Left;
                RemoveExpired(firstExpiry, activeFirst, currentLeft);
                RemoveExpired(secondExpiry, activeSecond, currentLeft);

                if (takeFirst)
                {
                    var current = first.Parts[firstIndex];
                    foreach (var candidateIndex in activeSecond)
                    {
                        var candidate = second.Parts[candidateIndex];
                        if (current.Bounds.Intersects(candidate.Bounds)
                            && PartGeometryOverlaps(current, candidate))
                        {
                            return true;
                        }
                    }
                    activeFirst.Add(firstIndex);
                    firstExpiry.Enqueue(firstIndex, current.Bounds.Right);
                    firstIndex++;
                }
                else
                {
                    var current = second.Parts[secondIndex];
                    foreach (var candidateIndex in activeFirst)
                    {
                        var candidate = first.Parts[candidateIndex];
                        if (current.Bounds.Intersects(candidate.Bounds)
                            && PartGeometryOverlaps(current, candidate))
                        {
                            return true;
                        }
                    }
                    activeSecond.Add(secondIndex);
                    secondExpiry.Enqueue(secondIndex, current.Bounds.Right);
                    secondIndex++;
                }
            }
            return false;
        }

        private static void RemoveExpired(
            PriorityQueue<int, double> expiry,
            HashSet<int> active,
            double currentLeft)
        {
            while (expiry.TryPeek(out var index, out var right) && right <= currentLeft)
            {
                expiry.Dequeue();
                active.Remove(index);
            }
        }

        private static bool PartGeometryOverlaps(PartOccupancy first, PartOccupancy second)
        {
            if (first.IsConservative || second.IsConservative) return true;
            try
            {
                var intersection = Clipper.Intersect(
                    first.Geometry,
                    second.Geometry,
                    FillRule.NonZero,
                    LayerOverlapPrecision);
                return intersection.Any(path => Math.Abs(Clipper.Area(path)) > double.Epsilon);
            }
            catch (Exception exception) when (exception is
                ClipperLibException or OverflowException or ArgumentException or InvalidOperationException)
            {
                return true;
            }
        }

        private static PartOccupancy? TryCreatePartOccupancy(ImportedSvgBreakPart part)
        {
            return part switch
            {
                ImportedSvgBreakFill fill => TryCreateFillOccupancy(fill),
                ImportedSvgBreakStroke stroke => TryCreateStrokeOccupancy(stroke),
                _ => null
            };
        }

        private static PartOccupancy? TryCreateFillOccupancy(ImportedSvgBreakFill fill)
        {
            var bounds = BoundsFromPoints(fill.Contours.SelectMany(contour => contour));
            if (!bounds.IsFinite)
            {
                return new PartOccupancy(GeometryBounds.Conservative, new PathsD(), IsConservative: true);
            }
            if (!bounds.HasArea) return null;
            try
            {
                var source = new PathsD(fill.Contours
                    .Where(contour => contour.Length >= 3)
                    .Select(ToClipperPath));
                var geometry = Clipper.BooleanOp(
                    ClipType.Union,
                    source,
                    new PathsD(),
                    fill.UsesEvenOddFillRule ? FillRule.EvenOdd : FillRule.NonZero,
                    LayerOverlapPrecision);
                return geometry.Count == 0
                    ? new PartOccupancy(bounds, new PathsD(), IsConservative: true)
                    : new PartOccupancy(BoundsFromPaths(geometry), geometry, IsConservative: false);
            }
            catch (Exception exception) when (exception is
                ClipperLibException or OverflowException or ArgumentException or InvalidOperationException)
            {
                return new PartOccupancy(bounds, new PathsD(), IsConservative: true);
            }
        }

        private static PartOccupancy? TryCreateStrokeOccupancy(ImportedSvgBreakStroke stroke)
        {
            var halfWidth = stroke.Width * 0.5;
            var bounds = ExpandBounds(BoundsFromPoints(stroke.Points), halfWidth);
            if (!bounds.IsFinite)
            {
                return new PartOccupancy(GeometryBounds.Conservative, new PathsD(), IsConservative: true);
            }
            if (!bounds.HasArea) return null;
            try
            {
                var sourcePoints = stroke.Closed
                    && stroke.Points.Length > 1
                    && stroke.Points[0] == stroke.Points[^1]
                        ? stroke.Points[..^1]
                        : stroke.Points;
                var source = new PathsD { ToClipperPath(sourcePoints) };
                var endType = stroke.Closed
                    ? EndType.Joined
                    : stroke.StartEndpointStyle == LineEndpointStyle.Round
                        && stroke.EndEndpointStyle == LineEndpointStyle.Round
                            ? EndType.Round
                            : EndType.Butt;
                var geometry = Clipper.InflatePaths(
                    source,
                    halfWidth,
                    JoinType.Round,
                    endType,
                    precision: LayerOverlapPrecision);
                return geometry.Count == 0
                    ? new PartOccupancy(bounds, new PathsD(), IsConservative: true)
                    : new PartOccupancy(BoundsFromPaths(geometry), geometry, IsConservative: false);
            }
            catch (Exception exception) when (exception is
                ClipperLibException or OverflowException or ArgumentException or InvalidOperationException)
            {
                return new PartOccupancy(bounds, new PathsD(), IsConservative: true);
            }
        }

        private static PathD ToClipperPath(IEnumerable<PointF> points)
        {
            return new PathD(points.Select(point => new PointD(point.X, point.Y)));
        }

        private static GeometryBounds BoundsFromPaths(IEnumerable<PathD> paths)
        {
            var left = double.PositiveInfinity;
            var top = double.PositiveInfinity;
            var right = double.NegativeInfinity;
            var bottom = double.NegativeInfinity;
            foreach (var point in paths.SelectMany(path => path))
            {
                left = Math.Min(left, point.x);
                top = Math.Min(top, point.y);
                right = Math.Max(right, point.x);
                bottom = Math.Max(bottom, point.y);
            }
            return new GeometryBounds(left, top, right, bottom);
        }

        private static GeometryBounds BoundsFromPoints(IEnumerable<PointF> points)
        {
            var left = double.PositiveInfinity;
            var top = double.PositiveInfinity;
            var right = double.NegativeInfinity;
            var bottom = double.NegativeInfinity;
            foreach (var point in points)
            {
                left = Math.Min(left, point.X);
                top = Math.Min(top, point.Y);
                right = Math.Max(right, point.X);
                bottom = Math.Max(bottom, point.Y);
            }
            return new GeometryBounds(left, top, right, bottom);
        }

        private static GeometryBounds CombineBounds(IEnumerable<GeometryBounds> bounds)
        {
            var left = double.PositiveInfinity;
            var top = double.PositiveInfinity;
            var right = double.NegativeInfinity;
            var bottom = double.NegativeInfinity;
            foreach (var current in bounds)
            {
                left = Math.Min(left, current.Left);
                top = Math.Min(top, current.Top);
                right = Math.Max(right, current.Right);
                bottom = Math.Max(bottom, current.Bottom);
            }
            return new GeometryBounds(left, top, right, bottom);
        }

        private static GeometryBounds ExpandBounds(GeometryBounds bounds, double amount)
        {
            return new GeometryBounds(
                bounds.Left - amount,
                bounds.Top - amount,
                bounds.Right + amount,
                bounds.Bottom + amount);
        }

        private void PlanExplicitChildren(
            SvgElement container,
            int inheritedLayerKey,
            ref int? looseLayerKey)
        {
            foreach (var child in container.Children)
            {
                if (!IsPlanningContent(child)) continue;

                if (IsExplicitLayer(child))
                {
                    looseLayerKey = null;
                    var explicitLayerKey = CreateLayer(ExplicitLayerName(child));
                    _layerOverrides[child] = explicitLayerKey;
                    int? nestedLooseLayerKey = null;
                    PlanExplicitChildren(child, explicitLayerKey, ref nestedLooseLayerKey);
                    looseLayerKey = null;
                    continue;
                }

                if (inheritedLayerKey >= 0)
                {
                    if (ContainsExplicitLayer(child))
                    {
                        int? nestedLooseLayerKey = null;
                        PlanExplicitChildren(child, inheritedLayerKey, ref nestedLooseLayerKey);
                    }
                    continue;
                }

                if (ContainsExplicitLayer(child))
                {
                    PlanExplicitChildren(child, -1, ref looseLayerKey);
                    continue;
                }

                looseLayerKey ??= CreateLayer(NextGeneratedLayerName());
                _layerOverrides[child] = looseLayerKey.Value;
            }
        }

        private void PlanOrdinaryContainer(SvgElement container)
        {
            var children = container.Children
                .Where(IsPlanningContent)
                .ToArray();
            if (children.Length == 0) return;

            var directGroups = children.Where(IsGroup).ToArray();
            if (directGroups.Length == 1 && ShouldUnwrapDominantGroup(children, directGroups[0]))
            {
                var looseRun = new List<SvgElement>();
                foreach (var child in children)
                {
                    if (ReferenceEquals(child, directGroups[0]))
                    {
                        PlanLooseRun(looseRun);
                        looseRun.Clear();
                        PlanOrdinaryContainer(child);
                    }
                    else
                    {
                        looseRun.Add(child);
                    }
                }
                PlanLooseRun(looseRun);
                return;
            }

            var frontierLooseRun = new List<SvgElement>();
            foreach (var child in children)
            {
                if (IsGroup(child))
                {
                    PlanLooseRun(frontierLooseRun);
                    frontierLooseRun.Clear();
                    _layerOverrides[child] = CreateLayer(OrdinaryGroupName(child));
                }
                else
                {
                    frontierLooseRun.Add(child);
                }
            }
            PlanLooseRun(frontierLooseRun);
        }

        private void PlanLooseRun(IReadOnlyList<SvgElement> elements)
        {
            if (elements.Count == 0) return;
            var layerKey = CreateLayer(NextGeneratedLayerName());
            foreach (var element in elements) _layerOverrides[element] = layerKey;
        }

        private bool ShouldUnwrapDominantGroup(
            IReadOnlyList<SvgElement> children,
            SvgElement group)
        {
            if (TrySourceLayerName(group, out _)) return false;
            var groupGeometry = CountPotentialGeometry(group);
            if (groupGeometry <= 0) return false;

            var totalGeometry = children.Sum(CountPotentialGeometry);
            return groupGeometry == totalGeometry
                || groupGeometry / (double)Math.Max(1L, totalGeometry) >= DominantGroupGeometryRatio;
        }

        private long CountPotentialGeometry(SvgElement element)
        {
            if (_potentialGeometryCounts.TryGetValue(element, out var cached)) return cached;
            if (!IsPlanningContent(element)) return 0;

            var name = ElementTag(element);
            long count;
            if (IsSupportedGeometry(name)
                || element is SvgTextBase
                || element is SvgVisualElement && element is not SvgFragment && !IsStructuralContainer(name))
            {
                count = 1;
            }
            else
            {
                count = 0;
                foreach (var child in element.Children)
                {
                    count = checked(count + CountPotentialGeometry(child));
                }
            }

            _potentialGeometryCounts[element] = count;
            return count;
        }

        private static bool ContainsExplicitLayer(SvgElement element)
        {
            foreach (var child in element.Children)
            {
                if (IsExplicitLayer(child) || ContainsExplicitLayer(child)) return true;
            }
            return false;
        }

        private static bool IsExplicitLayer(SvgElement element)
        {
            return IsGroup(element)
                && element.CustomAttributes.TryGetValue(InkscapeGroupModeAttribute, out var groupMode)
                && string.Equals(groupMode?.Trim(), "layer", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsGroup(SvgElement element) => ElementTag(element) == "g";

        private static bool IsPlanningContent(SvgElement element)
        {
            var name = ElementTag(element);
            return element is not SvgGradientServer
                && !IsNonRenderingDefinition(name)
                && !string.Equals(element.Display?.Trim(), "none", StringComparison.OrdinalIgnoreCase)
                && element.Opacity > 0f;
        }

        private string ExplicitLayerName(SvgElement layer)
        {
            return TrySourceLayerName(layer, out var name) ? name : NextGeneratedLayerName();
        }

        private string OrdinaryGroupName(SvgElement group)
        {
            return TrySourceLayerName(group, out var name)
                ? name
                : $"SVG Group {_nextGeneratedGroupName++:00}";
        }

        private static bool TrySourceLayerName(SvgElement element, out string name)
        {
            if (element.CustomAttributes.TryGetValue(InkscapeLabelAttribute, out var label)
                && !string.IsNullOrWhiteSpace(label))
            {
                name = label.Trim();
                return true;
            }
            if (!string.IsNullOrWhiteSpace(element.ID))
            {
                name = element.ID.Trim();
                return true;
            }

            name = string.Empty;
            return false;
        }

        private string NextGeneratedLayerName() => $"SVG Layer {_nextGeneratedLayerName++:00}";
    }
}
