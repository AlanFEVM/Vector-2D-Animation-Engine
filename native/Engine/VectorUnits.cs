namespace VectorAnimationEngine;

internal static class VectorUnits
{
    public const float UnitsPerPixel = 25f;
    public const float PixelsPerUnit = 1f / UnitsPerPixel;
    public const float PixelsPerStrokePoint = 0.5f;
    public const float UnitsPerStrokePoint = UnitsPerPixel * PixelsPerStrokePoint;

    public static float ToPixels(float vectorUnits) => vectorUnits * PixelsPerUnit;

    public static float FromPixels(float pixels) => pixels * UnitsPerPixel;

    public static float StrokePointsToUnits(float points) => Math.Max(0, points * UnitsPerStrokePoint);

    public static float UnitsToStrokePoints(float vectorUnits) => Math.Max(0, vectorUnits / UnitsPerStrokePoint);

    public static float Quantize(float vectorUnits) => MathF.Round(vectorUnits);

    public static PointF Quantize(PointF point) => new(Quantize(point.X), Quantize(point.Y));

    public static SizeF Quantize(SizeF size) => new(Quantize(size.Width), Quantize(size.Height));
}
