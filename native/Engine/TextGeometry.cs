using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Clipper2Lib;

namespace VectorAnimationEngine;

internal readonly record struct TextAreaResizeResult(
    TextObjectData Data,
    PointF Center,
    SizeF DisplaySize);

internal static class TextGeometry
{
    internal const string FallbackFontFamilyName = "Segoe UI";
    internal const int MaximumContentCharacters = 16_384;
    internal const int MaximumFontFamilyCharacters = 128;
    internal const float MinimumFontSizePoints = 4f;
    internal const float MaximumFontSizePoints = 512f;
    internal const float MaximumLayoutDimension = 1_000_000f;

    private const float TypographyDpi = 96f;
    private const double ClipperCoordinateScale = 1000d;
    private static readonly ConditionalWeakTable<TextObjectData, CanonicalContourCacheEntry> CanonicalContourCache = new();
    private static long _canonicalContourBuildCount;

    internal static long CanonicalContourBuildCount => Interlocked.Read(ref _canonicalContourBuildCount);

    internal static TextObjectData NormalizeForAuthoring(TextObjectData data)
    {
        var normalized = NormalizeStoredData(data);
        var measuredHeight = MeasureLayoutHeight(normalized);
        return normalized with { LayoutSize = new SizeF(normalized.LayoutSize.Width, measuredHeight) };
    }

    internal static TextObjectData NormalizeStoredData(TextObjectData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Content is null) throw new ArgumentException("Text content cannot be null.", nameof(data));

        var content = NormalizeLineEndings(data.Content);
        if (content.Length > MaximumContentCharacters)
        {
            throw new ArgumentException(
                $"Text content cannot exceed {MaximumContentCharacters:N0} characters.",
                nameof(data));
        }

        var familyName = string.IsNullOrWhiteSpace(data.FontFamilyName)
            ? FallbackFontFamilyName
            : data.FontFamilyName.Trim();
        if (familyName.Length > MaximumFontFamilyCharacters)
        {
            throw new ArgumentException(
                $"Font family names cannot exceed {MaximumFontFamilyCharacters:N0} characters.",
                nameof(data));
        }

        if (!float.IsFinite(data.FontSizePoints)
            || data.FontSizePoints < MinimumFontSizePoints
            || data.FontSizePoints > MaximumFontSizePoints)
        {
            throw new ArgumentOutOfRangeException(
                nameof(data),
                $"Text font size must be between {MinimumFontSizePoints:R} and {MaximumFontSizePoints:R} points.");
        }
        if (!Enum.IsDefined(data.FontStyle))
        {
            throw new ArgumentException("Text font style is invalid.", nameof(data));
        }
        if (!Enum.IsDefined(data.Alignment))
        {
            throw new ArgumentException("Text alignment is invalid.", nameof(data));
        }
        ValidateLayoutSize(data.LayoutSize, nameof(data));

        return data with
        {
            Content = content,
            FontFamilyName = familyName
        };
    }

    internal static bool IsValidStoredData(TextObjectData? data)
    {
        if (data is null
            || data.Content is null
            || data.FontFamilyName is null
            || data.Content.Length > MaximumContentCharacters
            || data.Content.Contains('\r')
            || string.IsNullOrWhiteSpace(data.FontFamilyName)
            || data.FontFamilyName.Length > MaximumFontFamilyCharacters
            || !string.Equals(data.FontFamilyName, data.FontFamilyName.Trim(), StringComparison.Ordinal)
            || !float.IsFinite(data.FontSizePoints)
            || data.FontSizePoints < MinimumFontSizePoints
            || data.FontSizePoints > MaximumFontSizePoints
            || !Enum.IsDefined(data.FontStyle)
            || !Enum.IsDefined(data.Alignment))
        {
            return false;
        }

        return IsValidLayoutDimension(data.LayoutSize.Width)
            && IsValidLayoutDimension(data.LayoutSize.Height);
    }

    internal static uint EstimateAtomCount(TextObjectData data)
    {
        var normalized = NormalizeStoredData(data);
        return (uint)Math.Clamp(
            Math.Max(3L, normalized.Content.Length * 4L),
            3L,
            uint.MaxValue);
    }

    internal static TextAreaResizeResult ResizeLayoutWidth(
        TextObjectData data,
        PointF center,
        SizeF displaySize,
        float angle,
        bool resizeLeftEdge,
        PointF pointerWorld,
        float minimumDisplayWidth)
    {
        var normalized = NormalizeStoredData(data);
        if (!float.IsFinite(center.X)
            || !float.IsFinite(center.Y)
            || !float.IsFinite(pointerWorld.X)
            || !float.IsFinite(pointerWorld.Y)
            || !float.IsFinite(displaySize.Width)
            || !float.IsFinite(displaySize.Height)
            || displaySize.Width <= 0
            || displaySize.Height <= 0
            || !float.IsFinite(angle)
            || !float.IsFinite(minimumDisplayWidth)
            || minimumDisplayWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(displaySize), "Text area resize geometry must be finite and positive.");
        }

        var scaleX = displaySize.Width / normalized.LayoutSize.Width;
        var scaleY = displaySize.Height / normalized.LayoutSize.Height;
        if (!float.IsFinite(scaleX)
            || !float.IsFinite(scaleY)
            || scaleX <= 0
            || scaleY <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(displaySize), "Text area resize scale must be finite and positive.");
        }

        var cosine = MathF.Cos(angle);
        var sine = MathF.Sin(angle);
        var pointerDeltaX = pointerWorld.X - center.X;
        var pointerDeltaY = pointerWorld.Y - center.Y;
        var pointerLocalX = pointerDeltaX * cosine + pointerDeltaY * sine;
        var startHalfWidth = displaySize.Width * 0.5f;
        var requestedDisplayWidth = resizeLeftEdge
            ? startHalfWidth - pointerLocalX
            : pointerLocalX + startHalfWidth;
        var maximumDisplayWidth = (float)Math.Min(
            float.MaxValue,
            (double)MaximumLayoutDimension * scaleX);
        var clampedDisplayWidth = Math.Clamp(
            requestedDisplayWidth,
            Math.Min(minimumDisplayWidth, maximumDisplayWidth),
            maximumDisplayWidth);
        var requestedLayoutWidth = Math.Clamp(
            clampedDisplayWidth / scaleX,
            1f,
            MaximumLayoutDimension);
        var resizedData = NormalizeForAuthoring(normalized with
        {
            LayoutSize = new SizeF(requestedLayoutWidth, normalized.LayoutSize.Height)
        });
        var resizedDisplaySize = new SizeF(
            resizedData.LayoutSize.Width * scaleX,
            resizedData.LayoutSize.Height * scaleY);
        if (!float.IsFinite(resizedDisplaySize.Width)
            || !float.IsFinite(resizedDisplaySize.Height)
            || resizedDisplaySize.Width <= 0
            || resizedDisplaySize.Height <= 0)
        {
            throw new InvalidOperationException("Resizing the text area produced an invalid display size.");
        }

        var fixedEdgeX = resizeLeftEdge ? startHalfWidth : -startHalfWidth;
        var centerLocalX = resizeLeftEdge
            ? fixedEdgeX - resizedDisplaySize.Width * 0.5f
            : fixedEdgeX + resizedDisplaySize.Width * 0.5f;
        var centerLocalY = -displaySize.Height * 0.5f + resizedDisplaySize.Height * 0.5f;
        var resizedCenter = new PointF(
            center.X + centerLocalX * cosine - centerLocalY * sine,
            center.Y + centerLocalX * sine + centerLocalY * cosine);
        return new TextAreaResizeResult(resizedData, resizedCenter, resizedDisplaySize);
    }

    internal static bool TryCreateWorldContours(
        TextObjectData data,
        PointF center,
        SizeF displaySize,
        float angle,
        out PointF[][] contours)
    {
        contours = Array.Empty<PointF[]>();
        if (!IsValidStoredData(data)
            || !float.IsFinite(center.X)
            || !float.IsFinite(center.Y)
            || !float.IsFinite(displaySize.Width)
            || !float.IsFinite(displaySize.Height)
            || displaySize.Width <= 0
            || displaySize.Height <= 0
            || !float.IsFinite(angle))
        {
            return false;
        }

        try
        {
            var canonicalContours = GetCanonicalContours(data);
            if (canonicalContours.Length == 0) return false;

            var scaleX = displaySize.Width / data.LayoutSize.Width;
            var scaleY = displaySize.Height / data.LayoutSize.Height;
            if (!float.IsFinite(scaleX)
                || !float.IsFinite(scaleY)
                || scaleX <= 0
                || scaleY <= 0)
            {
                return false;
            }

            var cosine = MathF.Cos(angle);
            var sine = MathF.Sin(angle);
            var halfWidth = data.LayoutSize.Width * 0.5f;
            var halfHeight = data.LayoutSize.Height * 0.5f;
            var result = new List<PointF[]>(canonicalContours.Length);
            foreach (var canonicalContour in canonicalContours)
            {
                var world = new List<PointF>(canonicalContour.Length);
                foreach (var point in canonicalContour)
                {
                    var localX = (point.X - halfWidth) * scaleX;
                    var localY = (point.Y - halfHeight) * scaleY;
                    var transformed = VectorUnits.Quantize(new PointF(
                        center.X + localX * cosine - localY * sine,
                        center.Y + localX * sine + localY * cosine));
                    if (world.Count == 0 || world[^1] != transformed) world.Add(transformed);
                }

                if (world.Count > 2 && world[0] == world[^1]) world.RemoveAt(world.Count - 1);
                if (world.Count >= 3 && Math.Abs(PolygonArea(world)) >= 0.5f) result.Add(world.ToArray());
            }

            contours = result.ToArray();
            return contours.Length > 0;
        }
        catch (Exception exception) when (exception is ArgumentException
            or ExternalException
            or ClipperLibException
            or InvalidDataException
            or InvalidOperationException
            or OverflowException)
        {
            contours = Array.Empty<PointF[]>();
            return false;
        }
    }

    private static PointF[][] GetCanonicalContours(TextObjectData data)
    {
        return CanonicalContourCache.GetValue(
            data,
            static key => new CanonicalContourCacheEntry(key)).Contours;
    }

    private static PointF[][] BuildCanonicalContours(TextObjectData data)
    {
        Interlocked.Increment(ref _canonicalContourBuildCount);
        if (data.Content.Length == 0) return Array.Empty<PointF[]>();

        var style = ToGdiFontStyle(data.FontStyle);
        using var family = ResolveFontFamily(data.FontFamilyName, style);
        using var format = CreateStringFormat(data.Alignment);
        using var path = new GraphicsPath(FillMode.Winding);
        var emSize = PointsToVectorUnits(data.FontSizePoints);
        path.AddString(
            data.Content,
            family,
            (int)style,
            emSize,
            new RectangleF(0, 0, data.LayoutSize.Width, data.LayoutSize.Height),
            format);
        if (path.PointCount == 0) return Array.Empty<PointF[]>();

        var flatteningTolerance = Math.Max(0.5f, emSize / 1024f);
        path.Flatten(null, flatteningTolerance);
        var sourceContours = ReadClosedContours(path);
        if (sourceContours.Count == 0) return Array.Empty<PointF[]>();

        var input = new Paths64(sourceContours.Count);
        foreach (var contour in sourceContours)
        {
            var clipperPath = new Path64(contour.Length);
            foreach (var point in contour)
            {
                clipperPath.Add(new Point64(
                    ToClipperCoordinate(point.X),
                    ToClipperCoordinate(point.Y)));
            }

            clipperPath = Clipper.StripDuplicates(clipperPath, true);
            if (clipperPath.Count >= 3 && Math.Abs(Clipper.Area(clipperPath)) >= 0.5d)
            {
                input.Add(clipperPath);
            }
        }
        if (input.Count == 0) return Array.Empty<PointF[]>();

        var union = Clipper.Union(input, FillRule.NonZero);
        return union
            .Where(path => path.Count >= 3)
            .Select(path => path
                .Select(point => new PointF(
                    (float)(point.X / ClipperCoordinateScale),
                    (float)(point.Y / ClipperCoordinateScale)))
                .ToArray())
            .OrderByDescending(contour => Math.Abs(PolygonArea(contour)))
            .ThenBy(contour => contour.Min(point => point.Y))
            .ThenBy(contour => contour.Min(point => point.X))
            .ToArray();
    }

    private sealed class CanonicalContourCacheEntry
    {
        private readonly Lazy<PointF[][]> _contours;

        public CanonicalContourCacheEntry(TextObjectData data)
        {
            _contours = new Lazy<PointF[][]>(
                () => BuildCanonicalContours(data),
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public PointF[][] Contours => _contours.Value;
    }

    private static float MeasureLayoutHeight(TextObjectData data)
    {
        var style = ToGdiFontStyle(data.FontStyle);
        using var family = ResolveFontFamily(data.FontFamilyName, style);
        using var font = new Font(family, data.FontSizePoints, style, GraphicsUnit.Point);
        using var format = CreateStringFormat(data.Alignment);
        using var bitmap = new Bitmap(1, 1);
        bitmap.SetResolution(TypographyDpi, TypographyDpi);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.PageUnit = GraphicsUnit.Pixel;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        var measuredContent = data.Content.Length == 0 || data.Content.EndsWith('\n')
            ? data.Content + " "
            : data.Content;
        var maximumPixelHeight = MaximumLayoutDimension * VectorUnits.PixelsPerUnit;
        var measured = graphics.MeasureString(
            measuredContent,
            font,
            new SizeF(
                Math.Max(float.Epsilon, data.LayoutSize.Width * VectorUnits.PixelsPerUnit),
                maximumPixelHeight),
            format,
            out var charactersFitted,
            out _);
        if (charactersFitted < measuredContent.Length
            || !float.IsFinite(measured.Height)
            || measured.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(data),
                "The text content exceeds the supported layout height.");
        }

        var height = Math.Max(1, MathF.Ceiling(measured.Height * VectorUnits.UnitsPerPixel));
        if (!IsValidLayoutDimension(height))
        {
            throw new ArgumentOutOfRangeException(
                nameof(data),
                "The text content exceeds the supported layout height.");
        }
        return height;
    }

    private static StringFormat CreateStringFormat(TextHorizontalAlignment alignment)
    {
        var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = alignment switch
            {
                TextHorizontalAlignment.Center => StringAlignment.Center,
                TextHorizontalAlignment.Right => StringAlignment.Far,
                _ => StringAlignment.Near
            },
            LineAlignment = StringAlignment.Near,
            Trimming = StringTrimming.None
        };
        format.FormatFlags &= ~StringFormatFlags.NoWrap;
        format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.LineLimit;
        return format;
    }

    private static FontFamily ResolveFontFamily(string requestedName, FontStyle style)
    {
        if (TryCreateFontFamily(requestedName, style, out var requested)) return requested;
        if (TryCreateFontFamily(FallbackFontFamilyName, style, out var fallback)) return fallback;
        if (TryCreateFontFamily(FontFamily.GenericSansSerif.Name, style, out var generic)) return generic;
        throw new InvalidOperationException("No compatible font family is available for text geometry.");
    }

    private static bool TryCreateFontFamily(string name, FontStyle style, out FontFamily family)
    {
        try
        {
            family = new FontFamily(name);
            if (family.IsStyleAvailable(style)) return true;
            family.Dispose();
        }
        catch (Exception exception) when (exception is ArgumentException or ExternalException)
        {
        }

        family = null!;
        return false;
    }

    private static FontStyle ToGdiFontStyle(TextFontStyle style)
    {
        return style switch
        {
            TextFontStyle.Bold => FontStyle.Bold,
            TextFontStyle.Italic => FontStyle.Italic,
            TextFontStyle.BoldItalic => FontStyle.Bold | FontStyle.Italic,
            _ => FontStyle.Regular
        };
    }

    private static List<PointF[]> ReadClosedContours(GraphicsPath path)
    {
        var points = path.PathPoints;
        var types = path.PathTypes;
        var result = new List<PointF[]>();
        var current = new List<PointF>();
        for (var index = 0; index < points.Length; index++)
        {
            var type = (PathPointType)(types[index] & (byte)PathPointType.PathTypeMask);
            if (type == PathPointType.Start && current.Count > 0)
            {
                AddContour(result, current);
                current.Clear();
            }

            var point = points[index];
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y))
            {
                throw new InvalidDataException("Text layout produced non-finite geometry.");
            }
            if (current.Count == 0 || current[^1] != point) current.Add(point);
            if ((types[index] & (byte)PathPointType.CloseSubpath) == 0) continue;
            AddContour(result, current);
            current.Clear();
        }
        AddContour(result, current);
        return result;
    }

    private static void AddContour(ICollection<PointF[]> output, IReadOnlyList<PointF> source)
    {
        if (source.Count < 3) return;
        var count = source.Count > 3 && source[0] == source[^1] ? source.Count - 1 : source.Count;
        if (count < 3) return;
        var contour = new PointF[count];
        for (var index = 0; index < count; index++) contour[index] = source[index];
        output.Add(contour);
    }

    private static string NormalizeLineEndings(string value)
    {
        return value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    private static void ValidateLayoutSize(SizeF size, string parameterName)
    {
        if (!IsValidLayoutDimension(size.Width) || !IsValidLayoutDimension(size.Height))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"Text layout dimensions must be positive and no greater than {MaximumLayoutDimension:R} vector units.");
        }
    }

    private static bool IsValidLayoutDimension(float value)
    {
        return float.IsFinite(value) && value > 0 && value <= MaximumLayoutDimension;
    }

    private static float PointsToVectorUnits(float points)
    {
        return points * TypographyDpi / 72f * VectorUnits.UnitsPerPixel;
    }

    private static long ToClipperCoordinate(float value)
    {
        return checked((long)Math.Round(value * ClipperCoordinateScale, MidpointRounding.AwayFromZero));
    }

    private static float PolygonArea(IReadOnlyList<PointF> polygon)
    {
        double area = 0;
        for (var index = 0; index < polygon.Count; index++)
        {
            var next = (index + 1) % polygon.Count;
            area += polygon[index].X * polygon[next].Y - polygon[next].X * polygon[index].Y;
        }
        return (float)(area * 0.5d);
    }
}
