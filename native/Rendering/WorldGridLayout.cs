namespace VectorAnimationEngine;

internal readonly record struct WorldGridScale(float StepWorld, float StepPixels);

internal readonly record struct WorldGridLineStyle(Color Color, float Width);

internal static class WorldGridLayout
{
    public const float MinimumStepWorld = 10f;
    private const float MinimumScreenSpacing = 6f;

    public static WorldGridScale Resolve(float pixelsPerWorldUnit)
    {
        if (!float.IsFinite(pixelsPerWorldUnit) || pixelsPerWorldUnit <= 0)
        {
            return new WorldGridScale(MinimumStepWorld, 0);
        }

        var requiredStep = Math.Max(MinimumStepWorld, MinimumScreenSpacing / pixelsPerWorldUnit);
        var exponent = Math.Clamp((int)MathF.Ceiling(MathF.Log10(requiredStep)), 1, 8);
        var stepWorld = MathF.Pow(10, exponent);
        return new WorldGridScale(stepWorld, stepWorld * pixelsPerWorldUnit);
    }

    public static WorldGridLineStyle ResolveLineStyle(long gridIndex, WorldGridScale scale, float opacity)
    {
        var levelPixels = scale.StepPixels;
        var rank = Math.Abs(gridIndex);
        while (rank > 0 && rank % 10 == 0)
        {
            rank /= 10;
            levelPixels *= 10;
        }

        var visibility = SmoothStep(5.5f, 15f, levelPixels);
        var major = SmoothStep(28f, 140f, levelPixels);
        var alpha = QuantizeAlpha(opacity * visibility * Lerp(44f, 120f, major));
        var red = (int)MathF.Round(Lerp(58f, 96f, major));
        var green = (int)MathF.Round(Lerp(66f, 108f, major));
        var blue = (int)MathF.Round(Lerp(72f, 116f, major));
        return new WorldGridLineStyle(
            Color.FromArgb(alpha, red, green, blue),
            Lerp(0.7f, 1.5f, major));
    }

    public static (long First, long Last) VisibleIndexRange(float minimum, float maximum, float step)
    {
        if (!float.IsFinite(minimum)
            || !float.IsFinite(maximum)
            || !float.IsFinite(step)
            || step <= 0)
        {
            return (0, -1);
        }

        return (
            (long)MathF.Floor(Math.Min(minimum, maximum) / step) - 1,
            (long)MathF.Ceiling(Math.Max(minimum, maximum) / step) + 1);
    }

    private static int QuantizeAlpha(float alpha)
    {
        var clamped = Math.Clamp((int)MathF.Round(alpha), 0, 255);
        return Math.Clamp((clamped + 2) / 4 * 4, 0, 255);
    }

    private static float SmoothStep(float edge0, float edge1, float value)
    {
        var t = Math.Clamp((value - edge0) / Math.Max(0.0001f, edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static float Lerp(float start, float end, float amount) => start + (end - start) * amount;
}
