using System.Runtime.InteropServices;

namespace VectorAnimationEngine;

internal readonly record struct BrushSoftLayer(float Threshold, float Opacity);

internal enum TraditionalBrushTipKind
{
    Round,
    Square
}

internal sealed class BrushShape
{
    public const int PixelSize = 128;
    private static readonly BrushSoftLayer[] SoftLayers =
    [
        new BrushSoftLayer(0.10f, 0.18f),
        new BrushSoftLayer(0.36f, 0.34f),
        new BrushSoftLayer(0.70f, 0.66f)
    ];
    private static readonly BrushSoftLayer[] TraditionalLayers =
    [
        new BrushSoftLayer(0.50f, 1f)
    ];
    private readonly float[] _mask;
    private readonly BrushSoftLayer[] _layers;
    private readonly Dictionary<int, PointF[]> _contourCache = new();
    private readonly float _centerX;
    private readonly float _centerY;
    private readonly PointF[]? _traditionalContour;

    private BrushShape(
        string name,
        float[] mask,
        bool isDefaultSoftRound = false,
        bool isTraditionalBrush = false,
        bool isRadiallySymmetric = false,
        int hardness = 0,
        TraditionalBrushTipKind traditionalTipKind = TraditionalBrushTipKind.Round,
        int traditionalWidthPercent = 100,
        int traditionalDirectionDegrees = 0)
    {
        Name = name;
        _mask = mask;
        IsDefaultSoftRound = isDefaultSoftRound;
        IsTraditionalBrush = isTraditionalBrush;
        TraditionalTipKind = traditionalTipKind;
        TraditionalWidthPercent = Math.Clamp(traditionalWidthPercent, 25, 400);
        TraditionalDirectionDegrees = NormalizeDegrees(traditionalDirectionDegrees);
        IsRadiallySymmetric = isTraditionalBrush
            ? traditionalTipKind == TraditionalBrushTipKind.Round && TraditionalWidthPercent == 100
            : isRadiallySymmetric;
        _layers = isTraditionalBrush ? TraditionalLayers : SoftLayers;
        Hardness = Math.Clamp(hardness, 0, 100);
        if (isTraditionalBrush)
        {
            _traditionalContour = CreateTraditionalContour(
                TraditionalTipKind,
                TraditionalWidthPercent / 100f,
                TraditionalDirectionDegrees);
        }

        (_centerX, _centerY) = WeightedCenter(mask);
    }

    public string Name { get; }
    public bool IsDefaultSoftRound { get; }
    public bool IsTraditionalBrush { get; }
    public bool IsRadiallySymmetric { get; }
    public int Hardness { get; }
    public TraditionalBrushTipKind TraditionalTipKind { get; }
    public int TraditionalWidthPercent { get; }
    public int TraditionalDirectionDegrees { get; }
    public float StampSpacingScale => IsTraditionalBrush
        ? Math.Min(1f, TraditionalWidthPercent / 100f)
        : 1f;
    public IReadOnlyList<BrushSoftLayer> Layers => _layers;
    public float MeanStrength => _mask.Average();

    public static BrushShape CreateSoftRound(int hardness = 0)
    {
        hardness = Math.Clamp(hardness, 0, 100);
        var coreRadius = hardness / 100f;
        var featherWidth = Math.Max(0.001f, 1 - coreRadius);
        var mask = new float[PixelSize * PixelSize];
        for (var y = 0; y < PixelSize; y++)
        {
            for (var x = 0; x < PixelSize; x++)
            {
                var dx = (x + 0.5f - PixelSize * 0.5f) / (PixelSize * 0.5f);
                var dy = (y + 0.5f - PixelSize * 0.5f) / (PixelSize * 0.5f);
                var distance = MathF.Sqrt(dx * dx + dy * dy);
                var softness = distance <= coreRadius
                    ? 1f
                    : Math.Clamp((1 - distance) / featherWidth, 0, 1);
                mask[y * PixelSize + x] = softness * softness * (3 - 2 * softness);
            }
        }

        return new BrushShape("Soft Round", mask, isDefaultSoftRound: true, isRadiallySymmetric: true, hardness: hardness);
    }

    public static BrushShape CreateTraditionalBrush(
        TraditionalBrushTipKind tipKind = TraditionalBrushTipKind.Round,
        int widthPercent = 100,
        int directionDegrees = 0)
    {
        var mask = new float[PixelSize * PixelSize];
        for (var y = 0; y < PixelSize; y++)
        {
            for (var x = 0; x < PixelSize; x++)
            {
                var dx = (x + 0.5f - PixelSize * 0.5f) / (PixelSize * 0.5f);
                var dy = (y + 0.5f - PixelSize * 0.5f) / (PixelSize * 0.5f);
                var distance = MathF.Sqrt(dx * dx + dy * dy);
                mask[y * PixelSize + x] = Math.Clamp((1.015f - distance) / 0.03f, 0, 1);
            }
        }

        return new BrushShape(
            "Traditional Brush",
            mask,
            isTraditionalBrush: true,
            traditionalTipKind: tipKind,
            traditionalWidthPercent: widthPercent,
            traditionalDirectionDegrees: directionDegrees);
    }

    public static bool TryLoad(string path, out BrushShape? brushShape, out string error)
    {
        brushShape = null;
        error = string.Empty;
        try
        {
            using var image = new Bitmap(path);
            if (image.Width != PixelSize || image.Height != PixelSize)
            {
                error = "Brush tips must be exactly 128 x 128 pixels.";
                return false;
            }

            var colors = new Color[PixelSize * PixelSize];
            var hasTransparency = false;
            for (var y = 0; y < PixelSize; y++)
            {
                for (var x = 0; x < PixelSize; x++)
                {
                    var color = image.GetPixel(x, y);
                    colors[y * PixelSize + x] = color;
                    hasTransparency |= color.A < 255;
                }
            }

            var mask = new float[PixelSize * PixelSize];
            for (var index = 0; index < colors.Length; index++)
            {
                var color = colors[index];
                var rgb = (color.R * 0.2126f + color.G * 0.7152f + color.B * 0.0722f) / 255f;
                mask[index] = hasTransparency ? color.A / 255f : rgb;
            }

            mask = BlurRgbMask(mask);
            if (mask.Max() < 0.04f)
            {
                error = "The imported brush tip does not contain a visible RGB or alpha mask.";
                return false;
            }

            brushShape = new BrushShape(Path.GetFileNameWithoutExtension(path), mask);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or OutOfMemoryException)
        {
            error = "The brush tip image could not be loaded.";
            return false;
        }
    }

    public PointF[] NormalizedContour(float threshold)
    {
        if (IsTraditionalBrush) return _traditionalContour ?? Array.Empty<PointF>();

        var key = (int)MathF.Round(Math.Clamp(threshold, 0.01f, 0.99f) * 1000);
        if (_contourCache.TryGetValue(key, out var cached)) return cached;

        const int segments = 64;
        const float maximumRadius = 1.42f;
        const float step = 1f / 96f;
        var contour = new PointF[segments];
        var minimumRadius = 0.04f;
        for (var segment = 0; segment < segments; segment++)
        {
            var angle = -MathF.PI / 2 + segment * MathF.Tau / segments;
            var cos = MathF.Cos(angle);
            var sin = MathF.Sin(angle);
            var radius = 0f;
            var found = false;
            for (var sample = 0f; sample <= maximumRadius; sample += step)
            {
                var x = _centerX + cos * sample * PixelSize * 0.5f;
                var y = _centerY + sin * sample * PixelSize * 0.5f;
                if (SampleMask(x, y) < threshold)
                {
                    if (found) break;
                    continue;
                }

                found = true;
                radius = sample;
            }

            radius = Math.Max(minimumRadius, radius);
            contour[segment] = new PointF(cos * radius, sin * radius);
        }

        _contourCache[key] = contour;
        return contour;
    }

    private static PointF[] CreateTraditionalContour(
        TraditionalBrushTipKind tipKind,
        float widthScale,
        int directionDegrees)
    {
        widthScale = Math.Clamp(widthScale, 0.25f, 4f);
        PointF[] contour;
        if (tipKind == TraditionalBrushTipKind.Square)
        {
            contour =
            [
                new PointF(-widthScale, -1),
                new PointF(widthScale, -1),
                new PointF(widthScale, 1),
                new PointF(-widthScale, 1)
            ];
        }
        else
        {
            const int segments = 64;
            contour = new PointF[segments];
            for (var segment = 0; segment < segments; segment++)
            {
                var angle = -MathF.PI / 2 + segment * MathF.Tau / segments;
                contour[segment] = new PointF(MathF.Cos(angle) * widthScale, MathF.Sin(angle));
            }
        }

        if (directionDegrees == 0) return contour;

        var radians = directionDegrees * MathF.PI / 180f;
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);
        for (var index = 0; index < contour.Length; index++)
        {
            var point = contour[index];
            contour[index] = new PointF(
                point.X * cos - point.Y * sin,
                point.X * sin + point.Y * cos);
        }

        return contour;
    }

    private static int NormalizeDegrees(int degrees)
    {
        degrees %= 360;
        return degrees < 0 ? degrees + 360 : degrees;
    }

    private float SampleMask(float x, float y)
    {
        var left = Math.Clamp((int)MathF.Floor(x), 0, PixelSize - 1);
        var top = Math.Clamp((int)MathF.Floor(y), 0, PixelSize - 1);
        var right = Math.Min(PixelSize - 1, left + 1);
        var bottom = Math.Min(PixelSize - 1, top + 1);
        var tx = Math.Clamp(x - left, 0, 1);
        var ty = Math.Clamp(y - top, 0, 1);
        var topValue = _mask[top * PixelSize + left] + (_mask[top * PixelSize + right] - _mask[top * PixelSize + left]) * tx;
        var bottomValue = _mask[bottom * PixelSize + left] + (_mask[bottom * PixelSize + right] - _mask[bottom * PixelSize + left]) * tx;
        return topValue + (bottomValue - topValue) * ty;
    }

    private static (float X, float Y) WeightedCenter(IReadOnlyList<float> mask)
    {
        var total = 0f;
        var x = 0f;
        var y = 0f;
        for (var index = 0; index < mask.Count; index++)
        {
            var weight = mask[index];
            total += weight;
            x += (index % PixelSize + 0.5f) * weight;
            y += (index / PixelSize + 0.5f) * weight;
        }

        return total <= 0.0001f
            ? (PixelSize * 0.5f, PixelSize * 0.5f)
            : (x / total, y / total);
    }

    private static float[] BlurRgbMask(IReadOnlyList<float> source)
    {
        var horizontal = new float[source.Count];
        var result = new float[source.Count];
        Span<float> weights = [1f, 4f, 6f, 4f, 1f];
        for (var y = 0; y < PixelSize; y++)
        {
            for (var x = 0; x < PixelSize; x++)
            {
                var sum = 0f;
                for (var i = -2; i <= 2; i++) sum += source[y * PixelSize + Math.Clamp(x + i, 0, PixelSize - 1)] * weights[i + 2];
                horizontal[y * PixelSize + x] = sum / 16f;
            }
        }

        for (var y = 0; y < PixelSize; y++)
        {
            for (var x = 0; x < PixelSize; x++)
            {
                var sum = 0f;
                for (var i = -2; i <= 2; i++) sum += horizontal[Math.Clamp(y + i, 0, PixelSize - 1) * PixelSize + x] * weights[i + 2];
                result[y * PixelSize + x] = Math.Clamp(sum / 16f, 0, 1);
            }
        }

        return result;
    }
}
