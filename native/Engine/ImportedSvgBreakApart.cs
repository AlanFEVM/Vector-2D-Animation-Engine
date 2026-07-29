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
    RasterizedContent = 1 << 6
}

internal readonly record struct ImportedSvgBreakPaint(Color Color);

internal abstract record ImportedSvgBreakPart(int Order, string SourceElementName, ImportedSvgBreakPaint Paint);

internal sealed record ImportedSvgBreakFill(
    int Order,
    string SourceElementName,
    ImportedSvgBreakPaint Paint,
    PointF[][] Contours,
    bool UsesEvenOddFillRule)
    : ImportedSvgBreakPart(Order, SourceElementName, Paint);

internal sealed record ImportedSvgBreakStroke(
    int Order,
    string SourceElementName,
    ImportedSvgBreakPaint Paint,
    PointF[] Points,
    bool Closed,
    float Width,
    LineEndpointStyle StartEndpointStyle,
    LineEndpointStyle EndEndpointStyle)
    : ImportedSvgBreakPart(Order, SourceElementName, Paint);

internal sealed record ImportedSvgBreakResult(
    SizeF IntrinsicSize,
    ImportedSvgBreakPart[] Parts,
    ImportedSvgBreakApproximation Approximations);

internal static class ImportedSvgBreakApart
{
    private const long MaxSourceBytes = 16L * 1024 * 1024;
    private const int MaxXmlDepth = 256;
    private const int MaxOutputParts = 16_384;
    private const int MaxOutputPoints = 500_000;
    private const int MaxRasterColorParts = 4_096;
    private const float FlatteningTolerance = 0.25f;
    private const int RasterLongEdge = 256;
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
                ExtractElement(child, rootTransforms, rootOpacity, state, renderer);
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

            return new ImportedSvgBreakResult(intrinsicSize, state.Parts.ToArray(), state.Approximations);
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
        ExtractionState state,
        ISvgRenderer renderer)
    {
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
            foreach (var child in fragment.Children) ExtractElement(child, transforms, opacity, state, renderer);
            return;
        }

        if (IsStructuralContainer(name))
        {
            using var transform = GetElementTransform(element);
            var transforms = ComposeTransformList(transform, null, ancestorTransforms);
            foreach (var child in element.Children) ExtractElement(child, transforms, opacity, state, renderer);
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
        ExtractVisual(visual, name, geometryTransforms, opacity, state, renderer);
    }

    private static void ExtractVisual(
        SvgVisualElement element,
        string elementName,
        IReadOnlyList<Matrix> transforms,
        float opacity,
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

        if (TryResolvePaint(element.Fill, element, opacity * element.FillOpacity, fillDefault: true, out var fillPaint))
        {
            var contours = figures
                .Where(figure => figure.Points.Length >= 3)
                .Select(figure => figure.Points)
                .ToArray();
            if (contours.Length > 0)
            {
                var evenOdd = element.FillRule == SvgFillRule.EvenOdd;
                if (!evenOdd) state.Approximations |= ImportedSvgBreakApproximation.NonZeroFillRule;
                state.AddPart(new ImportedSvgBreakFill(
                    state.NextOrder(),
                    elementName,
                    fillPaint,
                    contours,
                    evenOdd));
            }
        }

        if (!TryResolvePaint(element.Stroke, element, opacity * element.StrokeOpacity, fillDefault: false, out var strokePaint))
        {
            return;
        }

        if (element.StrokeDashArray is { Count: > 0 }) throw Unsupported(elementName, "dashed stroke");
        var sourceWidth = element.StrokeWidth.ToDeviceValue(renderer, UnitRenderingType.Other, element);
        if (!float.IsFinite(sourceWidth) || sourceWidth <= 0f) return;
        var strokeScale = MeasureStrokeScale(transforms, state);
        var width = sourceWidth * strokeScale;
        if (!float.IsFinite(width) || width <= 0f) return;

        var endpointStyle = element.StrokeLineCap == SvgStrokeLineCap.Round
            ? LineEndpointStyle.Round
            : LineEndpointStyle.Sharp;
        if (element.StrokeLineCap != SvgStrokeLineCap.Round
            || element.StrokeLineJoin != SvgStrokeLineJoin.Round)
        {
            state.Approximations |= ImportedSvgBreakApproximation.StrokeStyle;
        }
        foreach (var figure in figures.Where(figure => figure.Points.Length >= 2))
        {
            var points = figure.Closed
                ? ClosePolyline(figure.Points)
                : figure.Points;
            state.AddPart(new ImportedSvgBreakStroke(
                state.NextOrder(),
                elementName,
                strokePaint,
                points,
                figure.Closed,
                width,
                endpointStyle,
                endpointStyle));
        }
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
                    "svg",
                    new ImportedSvgBreakPaint(Color.Transparent),
                    new[] { bounds },
                    UsesEvenOddFillRule: true));
            }

            return new ImportedSvgBreakResult(intrinsicSize, state.Parts.ToArray(), state.Approximations);
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

    private sealed class UnsupportedSvgFeatureException(string message) : Exception(message);

    private sealed class ExtractionState
    {
        private int _pointCount;
        private int _nextOrder;

        public List<ImportedSvgBreakPart> Parts { get; } = new();
        public ImportedSvgBreakApproximation Approximations { get; set; }

        public int NextOrder() => _nextOrder++;

        public void AddPart(ImportedSvgBreakPart part)
        {
            if (Parts.Count >= MaxOutputParts)
            {
                throw new InvalidDataException($"SVG break-apart output must not exceed {MaxOutputParts} parts.");
            }
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
    }
}
