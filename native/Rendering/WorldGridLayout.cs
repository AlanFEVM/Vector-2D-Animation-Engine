namespace VectorAnimationEngine;

internal enum WorldGridType
{
    Cartesian,
    GoldenSpiral,
    Polar
}

internal readonly record struct WorldGridScale(float StepWorld, float StepPixels);

internal readonly record struct WorldGridLineStyle(Color Color, float Width);

internal readonly record struct WorldGridSegment(PointF Start, PointF End);

internal readonly record struct GoldenSpiralGridGeometry(
    RectangleF Bounds,
    PointF[] SpiralPoints,
    WorldGridSegment[] GuideSegments);

internal readonly record struct PolarGridCircle(long Index, float Radius);

internal readonly record struct PolarGridArc(float StartAngle, float SweepAngle);

internal readonly record struct PolarGridGeometry(
    PointF Origin,
    PolarGridCircle[] Circles,
    WorldGridSegment[] DiameterSegments);

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

internal static class PolarGridLayout
{
    public const int DiameterCount = 12;
    public const int MaximumVisibleCircles = 2048;
    public const int MaximumVisibleArcsPerCircle = 4;
    public const int MaximumArcSegments = 128;
    private const float TargetArcSegmentPixels = 24f;
    private const double TwoPi = Math.PI * 2;

    public static PolarGridGeometry Resolve(RectangleF visibleBounds, float adaptiveGridStep)
    {
        if (!float.IsFinite(visibleBounds.X)
            || !float.IsFinite(visibleBounds.Y)
            || !float.IsFinite(visibleBounds.Width)
            || !float.IsFinite(visibleBounds.Height)
            || visibleBounds.Width <= 0
            || visibleBounds.Height <= 0)
        {
            return new PolarGridGeometry(PointF.Empty, [], []);
        }

        var step = float.IsFinite(adaptiveGridStep) && adaptiveGridStep > 0
            ? adaptiveGridStep
            : WorldGridLayout.MinimumStepWorld;
        var minimumRadius = MinimumRectangleRadius(visibleBounds);
        var maximumRadius = MaximumCornerRadius(visibleBounds);
        var first = Math.Max(1L, (long)MathF.Floor(minimumRadius / step) - 1);
        var last = Math.Max(first, (long)MathF.Ceiling(maximumRadius / step) + 1);
        var circleCount = (int)Math.Min(MaximumVisibleCircles, last - first + 1);
        var circles = new PolarGridCircle[circleCount];
        for (var index = 0; index < circles.Length; index++)
        {
            var circleIndex = first + index;
            circles[index] = new PolarGridCircle(circleIndex, circleIndex * step);
        }

        var diameterRadius = maximumRadius + step;
        var diameters = new WorldGridSegment[DiameterCount];
        for (var index = 0; index < diameters.Length; index++)
        {
            var angle = index * MathF.PI / DiameterCount;
            var x = MathF.Cos(angle) * diameterRadius;
            var y = MathF.Sin(angle) * diameterRadius;
            diameters[index] = new WorldGridSegment(new PointF(-x, -y), new PointF(x, y));
        }

        return new PolarGridGeometry(PointF.Empty, circles, diameters);
    }

    public static int ResolveVisibleArcs(
        RectangleF visibleBounds,
        float radius,
        Span<PolarGridArc> destination)
    {
        if (destination.Length == 0
            || !float.IsFinite(radius)
            || radius <= 0
            || !float.IsFinite(visibleBounds.X)
            || !float.IsFinite(visibleBounds.Y)
            || !float.IsFinite(visibleBounds.Width)
            || !float.IsFinite(visibleBounds.Height)
            || visibleBounds.Width <= 0
            || visibleBounds.Height <= 0)
        {
            return 0;
        }

        var r = (double)radius;
        Span<double> angles = stackalloc double[8];
        var angleCount = 0;
        AddVerticalIntersections(visibleBounds, visibleBounds.Left, r, angles, ref angleCount);
        AddVerticalIntersections(visibleBounds, visibleBounds.Right, r, angles, ref angleCount);
        AddHorizontalIntersections(visibleBounds, visibleBounds.Top, r, angles, ref angleCount);
        AddHorizontalIntersections(visibleBounds, visibleBounds.Bottom, r, angles, ref angleCount);

        if (angleCount < 2)
        {
            if (-r >= visibleBounds.Left
                && r <= visibleBounds.Right
                && -r >= visibleBounds.Top
                && r <= visibleBounds.Bottom)
            {
                destination[0] = new PolarGridArc(0, MathF.Tau);
                return 1;
            }

            return 0;
        }

        angles[..angleCount].Sort();
        var resultCount = 0;
        for (var index = 0; index < angleCount && resultCount < destination.Length; index++)
        {
            var start = angles[index];
            var end = index + 1 < angleCount ? angles[index + 1] : angles[0] + TwoPi;
            var sweep = end - start;
            if (sweep <= 1e-8) continue;
            var midpoint = start + sweep * 0.5;
            var x = Math.Cos(midpoint) * r;
            var y = Math.Sin(midpoint) * r;
            if (x < visibleBounds.Left - 0.001
                || x > visibleBounds.Right + 0.001
                || y < visibleBounds.Top - 0.001
                || y > visibleBounds.Bottom + 0.001)
            {
                continue;
            }

            destination[resultCount++] = new PolarGridArc((float)start, (float)sweep);
        }

        return resultCount;
    }

    public static int ResolveArcSegmentCount(float screenRadius, float sweepAngle)
    {
        if (!float.IsFinite(screenRadius) || !float.IsFinite(sweepAngle)) return 2;
        return Math.Clamp(
            (int)MathF.Ceiling(MathF.Abs(screenRadius * sweepAngle) / TargetArcSegmentPixels),
            2,
            MaximumArcSegments);
    }

    public static bool TryClipSegment(
        RectangleF bounds,
        WorldGridSegment segment,
        out WorldGridSegment clipped)
    {
        clipped = default;
        var dx = (double)segment.End.X - segment.Start.X;
        var dy = (double)segment.End.Y - segment.Start.Y;
        var enter = 0d;
        var exit = 1d;
        if (!ClipTest(-dx, segment.Start.X - bounds.Left, ref enter, ref exit)
            || !ClipTest(dx, bounds.Right - segment.Start.X, ref enter, ref exit)
            || !ClipTest(-dy, segment.Start.Y - bounds.Top, ref enter, ref exit)
            || !ClipTest(dy, bounds.Bottom - segment.Start.Y, ref enter, ref exit))
        {
            return false;
        }

        clipped = new WorldGridSegment(
            new PointF((float)(segment.Start.X + enter * dx), (float)(segment.Start.Y + enter * dy)),
            new PointF((float)(segment.Start.X + exit * dx), (float)(segment.Start.Y + exit * dy)));
        return true;
    }

    private static void AddVerticalIntersections(
        RectangleF bounds,
        double x,
        double radius,
        Span<double> angles,
        ref int count)
    {
        var remainder = radius * radius - x * x;
        if (remainder < 0) return;
        var y = Math.Sqrt(Math.Max(0, remainder));
        AddIntersectionAngle(x, y, bounds.Top, bounds.Bottom, angles, ref count);
        if (y > 1e-8) AddIntersectionAngle(x, -y, bounds.Top, bounds.Bottom, angles, ref count);
    }

    private static void AddHorizontalIntersections(
        RectangleF bounds,
        double y,
        double radius,
        Span<double> angles,
        ref int count)
    {
        var remainder = radius * radius - y * y;
        if (remainder < 0) return;
        var x = Math.Sqrt(Math.Max(0, remainder));
        AddIntersectionAngle(x, y, bounds.Left, bounds.Right, angles, ref count, compareX: true);
        if (x > 1e-8) AddIntersectionAngle(-x, y, bounds.Left, bounds.Right, angles, ref count, compareX: true);
    }

    private static void AddIntersectionAngle(
        double x,
        double y,
        double minimum,
        double maximum,
        Span<double> angles,
        ref int count,
        bool compareX = false)
    {
        var coordinate = compareX ? x : y;
        if (coordinate < minimum - 0.001 || coordinate > maximum + 0.001) return;
        var angle = Math.Atan2(y, x);
        if (angle < 0) angle += TwoPi;
        for (var index = 0; index < count; index++)
        {
            if (Math.Abs(angles[index] - angle) <= 1e-7
                || Math.Abs(Math.Abs(angles[index] - angle) - TwoPi) <= 1e-7)
            {
                return;
            }
        }

        if (count < angles.Length) angles[count++] = angle;
    }

    private static bool ClipTest(double p, double q, ref double enter, ref double exit)
    {
        if (Math.Abs(p) <= double.Epsilon) return q >= 0;
        var ratio = q / p;
        if (p < 0)
        {
            if (ratio > exit) return false;
            if (ratio > enter) enter = ratio;
        }
        else
        {
            if (ratio < enter) return false;
            if (ratio < exit) exit = ratio;
        }

        return true;
    }

    private static float MinimumRectangleRadius(RectangleF bounds)
    {
        var horizontal = bounds.Left > 0 ? bounds.Left : bounds.Right < 0 ? -bounds.Right : 0;
        var vertical = bounds.Top > 0 ? bounds.Top : bounds.Bottom < 0 ? -bounds.Bottom : 0;
        return MathF.Sqrt(horizontal * horizontal + vertical * vertical);
    }

    private static float MaximumCornerRadius(RectangleF bounds)
    {
        var horizontal = MathF.Max(MathF.Abs(bounds.Left), MathF.Abs(bounds.Right));
        var vertical = MathF.Max(MathF.Abs(bounds.Top), MathF.Abs(bounds.Bottom));
        return MathF.Sqrt(horizontal * horizontal + vertical * vertical);
    }
}

internal static class GoldenSpiralGridLayout
{
    public const float GoldenRatio = 1.61803398875f;
    private const int SamplesPerQuarterTurn = 16;
    private const int OverscanQuarterTurns = 4;

    public static GoldenSpiralGridGeometry Resolve(
        RectangleF visibleBounds,
        float adaptiveGridStep,
        float minimumVisibleRadius)
    {
        if (!float.IsFinite(visibleBounds.X)
            || !float.IsFinite(visibleBounds.Y)
            || !float.IsFinite(visibleBounds.Width)
            || !float.IsFinite(visibleBounds.Height)
            || visibleBounds.Width <= 1
            || visibleBounds.Height <= 1)
        {
            return new GoldenSpiralGridGeometry(RectangleF.Empty, [], []);
        }

        adaptiveGridStep = float.IsFinite(adaptiveGridStep) && adaptiveGridStep > 0
            ? adaptiveGridStep
            : WorldGridLayout.MinimumStepWorld;
        minimumVisibleRadius = float.IsFinite(minimumVisibleRadius) && minimumVisibleRadius > 0
            ? minimumVisibleRadius
            : 1f;
        var inverseRatio = 1f / GoldenRatio;
        var inverseRatioSquared = inverseRatio * inverseRatio;
        var requiredHeight = MathF.Max(
            MathF.Max(
                MathF.Max(0, -visibleBounds.Left) / inverseRatio,
                MathF.Max(0, visibleBounds.Right)),
            MathF.Max(
                MathF.Max(0, -visibleBounds.Top) / inverseRatio,
                MathF.Max(0, visibleBounds.Bottom) / inverseRatioSquared));
        var height = MathF.Ceiling(requiredHeight / adaptiveGridStep) * adaptiveGridStep;
        if (!float.IsFinite(height) || height <= 1)
        {
            return new GoldenSpiralGridGeometry(RectangleF.Empty, [], []);
        }

        var width = height * GoldenRatio;
        var bounds = new RectangleF(
            -height * inverseRatio,
            -height * inverseRatio,
            width,
            height);
        var x1 = bounds.Left + bounds.Width * (1f - inverseRatio);
        var x2 = bounds.Left + bounds.Width * inverseRatio;
        var y1 = bounds.Top + bounds.Height * (1f - inverseRatio);
        var y2 = bounds.Top + bounds.Height * inverseRatio;
        var guides = new[]
        {
            new WorldGridSegment(new PointF(bounds.Left, bounds.Top), new PointF(bounds.Right, bounds.Top)),
            new WorldGridSegment(new PointF(bounds.Right, bounds.Top), new PointF(bounds.Right, bounds.Bottom)),
            new WorldGridSegment(new PointF(bounds.Right, bounds.Bottom), new PointF(bounds.Left, bounds.Bottom)),
            new WorldGridSegment(new PointF(bounds.Left, bounds.Bottom), new PointF(bounds.Left, bounds.Top)),
            new WorldGridSegment(new PointF(x1, bounds.Top), new PointF(x1, bounds.Bottom)),
            new WorldGridSegment(new PointF(x2, bounds.Top), new PointF(x2, bounds.Bottom)),
            new WorldGridSegment(new PointF(bounds.Left, y1), new PointF(bounds.Right, y1)),
            new WorldGridSegment(new PointF(bounds.Left, y2), new PointF(bounds.Right, y2))
        };

        var center = PointF.Empty;
        var maximumVisibleRadius = MaximumCornerRadius(visibleBounds);
        var maximumRenderedRadius = maximumVisibleRadius * MathF.Pow(GoldenRatio, OverscanQuarterTurns);
        var beta = Math.Log(GoldenRatio) / (Math.PI * 0.5);
        var thetaStep = Math.PI * 0.5 / SamplesPerQuarterTurn;
        var baseRadius = WorldGridLayout.MinimumStepWorld;
        var firstSample = (int)Math.Floor(Math.Log(minimumVisibleRadius / baseRadius) / beta / thetaStep);
        var lastSample = (int)Math.Ceiling(Math.Log(maximumRenderedRadius / baseRadius) / beta / thetaStep);
        var spiral = new PointF[lastSample - firstSample + 2];
        spiral[0] = center;
        for (var index = 1; index < spiral.Length; index++)
        {
            var theta = (firstSample + index - 1) * thetaStep;
            var radius = baseRadius * Math.Exp(beta * theta);
            spiral[index] = new PointF(
                (float)(Math.Cos(theta) * radius),
                (float)(Math.Sin(theta) * radius));
        }

        return new GoldenSpiralGridGeometry(bounds, spiral, guides);
    }

    private static float MaximumCornerRadius(RectangleF bounds)
    {
        var horizontal = MathF.Max(MathF.Abs(bounds.Left), MathF.Abs(bounds.Right));
        var vertical = MathF.Max(MathF.Abs(bounds.Top), MathF.Abs(bounds.Bottom));
        return MathF.Max(1, MathF.Sqrt(horizontal * horizontal + vertical * vertical));
    }
}
