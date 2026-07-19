using Clipper2Lib;

namespace VectorAnimationEngine;

internal readonly record struct PressureBrushSample(PointF Point, float SpeedPixelsPerSecond, float HeldSeconds);

internal readonly record struct PressureBrushPoint(PointF Point, float Diameter);

internal static class FreehandStrokeProcessor
{
    private const double BrushCoordinateScale = 1000d;
    private const double BrushArcToleranceUnits = 0.5d;
    private const float VariableWidthCornerAlignment = 0.85f;

    public static PointF[] Process(IReadOnlyList<PointF> samples, int smoothing, float simplifyTolerance)
    {
        if (samples.Count == 0) return Array.Empty<PointF>();

        var points = RemoveDuplicatePoints(samples);
        if (points.Count <= 2) return points.ToArray();

        smoothing = Math.Clamp(smoothing, 0, 100);
        var passes = smoothing >= 72 ? 2 : smoothing >= 18 ? 1 : 0;
        var amount = smoothing / 100f * 0.48f;
        for (var pass = 0; pass < passes; pass++) points = Smooth(points, amount);

        var simplified = Simplify(points, Math.Max(0.25f, simplifyTolerance));
        return RemoveDuplicatePoints(simplified).ToArray();
    }

    public static PressureBrushPoint[] ProcessPressure(
        IReadOnlyList<PressureBrushSample> samples,
        float baseDiameter,
        int smoothing,
        float simplifyTolerance)
    {
        if (samples.Count == 0) return Array.Empty<PressureBrushPoint>();

        var cleaned = RemoveDuplicatePressureSamples(samples);
        if (cleaned.Count == 0) return Array.Empty<PressureBrushPoint>();

        var sourcePoints = cleaned.Select(sample => sample.Point).ToArray();
        var processed = Process(sourcePoints, smoothing, simplifyTolerance);
        if (processed.Length == 0) return Array.Empty<PressureBrushPoint>();

        var sourceDistances = CumulativeDistances(sourcePoints);
        var processedDistances = CumulativeDistances(processed);
        var sourceLength = Math.Max(0.0001f, sourceDistances[^1]);
        var processedLength = Math.Max(0.0001f, processedDistances[^1]);
        var minimumDiameter = VectorUnits.StrokePointsToUnits(0.5f);
        baseDiameter = Math.Max(minimumDiameter, baseDiameter);

        var result = new PressureBrushPoint[processed.Length];
        var sourceIndex = 0;
        var previousDiameter = 0f;
        for (var i = 0; i < processed.Length; i++)
        {
            var targetDistance = processedDistances[i] / processedLength * sourceLength;
            while (sourceIndex < cleaned.Count - 2 && sourceDistances[sourceIndex + 1] < targetDistance) sourceIndex++;

            var start = cleaned[sourceIndex];
            var endIndex = Math.Min(sourceIndex + 1, cleaned.Count - 1);
            var end = cleaned[endIndex];
            var startDistance = sourceDistances[sourceIndex];
            var endDistance = sourceDistances[endIndex];
            var t = endDistance - startDistance <= 0.0001f
                ? 0f
                : Math.Clamp((targetDistance - startDistance) / (endDistance - startDistance), 0f, 1f);
            var speed = start.SpeedPixelsPerSecond + (end.SpeedPixelsPerSecond - start.SpeedPixelsPerSecond) * t;
            var held = start.HeldSeconds + (end.HeldSeconds - start.HeldSeconds) * t;
            var targetDiameter = PressureDiameter(baseDiameter, speed, held, minimumDiameter);
            var onsetTime = 1f - MathF.Exp(-Math.Max(0, held) / 0.18f);
            var onsetTravelRange = Math.Max(minimumDiameter * 2f, baseDiameter * 0.75f);
            var onsetTravel = 1f - MathF.Exp(-Math.Max(0, targetDistance) / onsetTravelRange);
            var onset = Math.Max(onsetTime, onsetTravel);
            var initialDiameter = minimumDiameter * 0.6f;
            targetDiameter = initialDiameter + (targetDiameter - initialDiameter) * onset;
            var diameter = i == 0
                ? targetDiameter
                : previousDiameter + (targetDiameter - previousDiameter) * 0.42f;
            result[i] = new PressureBrushPoint(processed[i], VectorUnits.Quantize(diameter));
            previousDiameter = diameter;
        }

        return result;
    }

    public static PressureBrushPoint[] CreatePressurePreview(
        IReadOnlyList<PressureBrushSample> samples,
        float baseDiameter,
        int smoothing = 0)
    {
        var cleaned = RemoveDuplicatePressureSamples(samples);
        if (cleaned.Count == 0) return Array.Empty<PressureBrushPoint>();

        var minimumDiameter = VectorUnits.StrokePointsToUnits(0.5f);
        baseDiameter = Math.Max(minimumDiameter, baseDiameter);
        var result = new PressureBrushPoint[cleaned.Count];
        var previousDiameter = 0f;
        var travelled = 0f;
        for (var i = 0; i < cleaned.Count; i++)
        {
            var sample = cleaned[i];
            if (i > 0) travelled += MathF.Sqrt(DistanceSquared(cleaned[i - 1].Point, sample.Point));
            var targetDiameter = PressureDiameter(baseDiameter, sample.SpeedPixelsPerSecond, sample.HeldSeconds, minimumDiameter);
            var onsetTime = 1f - MathF.Exp(-Math.Max(0, sample.HeldSeconds) / 0.18f);
            var onsetTravelRange = Math.Max(minimumDiameter * 2f, baseDiameter * 0.75f);
            var onsetTravel = 1f - MathF.Exp(-Math.Max(0, travelled) / onsetTravelRange);
            var onset = Math.Max(onsetTime, onsetTravel);
            var initialDiameter = minimumDiameter * 0.6f;
            targetDiameter = initialDiameter + (targetDiameter - initialDiameter) * onset;
            var diameter = i == 0
                ? targetDiameter
                : previousDiameter + (targetDiameter - previousDiameter) * 0.42f;
            result[i] = new PressureBrushPoint(sample.Point, VectorUnits.Quantize(diameter));
            previousDiameter = diameter;
        }

        return SmoothPressureProfile(result, smoothing);
    }

    public static PointF[][] CreateVariableWidthBrushOutlines(
        IReadOnlyList<PressureBrushPoint> source,
        float radiusScale)
    {
        if (source.Count == 0) return Array.Empty<PointF[]>();

        var profile = ReduceVariableWidthProfile(RemoveDuplicatePressurePoints(source));
        if (profile.Count == 0) return Array.Empty<PointF[]>();

        radiusScale = Math.Max(0.04f, radiusScale);
        if (profile.Count == 1)
        {
            return [CreateRoundContour(profile[0].Point, Radius(profile[0], radiusScale), 24)];
        }

        try
        {
            var left = new List<PointF>(profile.Count);
            var right = new List<PointF>(profile.Count);
            var normals = new PointF[profile.Count];
            for (var i = 0; i < profile.Count; i++)
            {
                var previous = profile[Math.Max(0, i - 1)].Point;
                var next = profile[Math.Min(profile.Count - 1, i + 1)].Point;
                var dx = next.X - previous.X;
                var dy = next.Y - previous.Y;
                var length = MathF.Sqrt(dx * dx + dy * dy);
                var radius = Radius(profile[i], radiusScale);
                if (length <= 0.0001f)
                {
                    normals[i] = i > 0 ? normals[i - 1] : new PointF(0, 1);
                }
                else
                {
                    normals[i] = new PointF(-dy / length, dx / length);
                }

                left.Add(VectorUnits.Quantize(new PointF(
                    profile[i].Point.X + normals[i].X * radius,
                    profile[i].Point.Y + normals[i].Y * radius)));
                right.Add(VectorUnits.Quantize(new PointF(
                    profile[i].Point.X - normals[i].X * radius,
                    profile[i].Point.Y - normals[i].Y * radius)));
            }

            var contour = new List<PointF>(profile.Count * 2 + 18);
            contour.AddRange(left);
            AppendRoundCap(contour, profile[^1].Point, normals[^1], Radius(profile[^1], radiusScale), forward: true);
            for (var i = right.Count - 1; i >= 0; i--) contour.Add(right[i]);
            AppendRoundCap(contour, profile[0].Point, normals[0], Radius(profile[0], radiusScale), forward: false);

            var path = new Path64(contour.Count);
            foreach (var point in contour)
            {
                path.Add(new Point64(ToClipperCoordinate(point.X), ToClipperCoordinate(point.Y)));
            }

            path = Clipper.StripDuplicates(path, true);
            if (path.Count < 3) return Array.Empty<PointF[]>();
            var paths = new Paths64 { path };
            AddRoundCornerJoins(paths, profile, radiusScale, Clipper.Area(path) < 0);
            var solution = Clipper.Union(paths, FillRule.NonZero);
            return FromClipperPaths(solution);
        }
        catch (Exception ex) when (ex is ClipperLibException or OverflowException)
        {
            return Array.Empty<PointF[]>();
        }
    }

    public static PointF[][] CreateBrushOutlines(IReadOnlyList<PointF> centerline, float width)
    {
        var points = RemoveDuplicatePoints(centerline);
        if (points.Count == 0) return Array.Empty<PointF[]>();

        try
        {
            var path = new Path64(points.Count);
            foreach (var point in points)
            {
                path.Add(new Point64(ToClipperCoordinate(point.X), ToClipperCoordinate(point.Y)));
            }

            path = Clipper.StripDuplicates(path, false);
            if (path.Count == 0) return Array.Empty<PointF[]>();

            var radius = Math.Max(VectorUnits.StrokePointsToUnits(0.5f), width) * 0.5d;
            var solution = new Paths64();
            var offset = new ClipperOffset(
                2d,
                BrushArcToleranceUnits * BrushCoordinateScale,
                preserveCollinear: false,
                reverseSolution: false);
            offset.AddPath(path, JoinType.Round, EndType.Round);
            offset.Execute(radius * BrushCoordinateScale, solution);
            return FromClipperPaths(solution);
        }
        catch (Exception ex) when (ex is ClipperLibException or OverflowException)
        {
            return Array.Empty<PointF[]>();
        }
    }

    private static PointF[][] FromClipperPaths(Paths64 paths)
    {
        var contours = new List<(PointF[] Points, double Area)>(paths.Count);
        foreach (var path in paths)
        {
            var area = Math.Abs(Clipper.Area(path));
            if (path.Count < 3 || area < BrushCoordinateScale * BrushCoordinateScale * 0.5d) continue;
            var contour = new List<PointF>(path.Count);
            foreach (var point in path)
            {
                var converted = VectorUnits.Quantize(new PointF(
                    (float)(point.X / BrushCoordinateScale),
                    (float)(point.Y / BrushCoordinateScale)));
                if (contour.Count == 0 || contour[^1] != converted) contour.Add(converted);
            }

            if (contour.Count > 1 && contour[0] == contour[^1]) contour.RemoveAt(contour.Count - 1);
            if (contour.Count >= 3) contours.Add((contour.ToArray(), area));
        }

        return contours
            .OrderByDescending(contour => contour.Area)
            .Select(contour => contour.Points)
            .ToArray();
    }

    private static List<PressureBrushPoint> RemoveDuplicatePressurePoints(IReadOnlyList<PressureBrushPoint> source)
    {
        var result = new List<PressureBrushPoint>(source.Count);
        foreach (var sourcePoint in source)
        {
            var point = sourcePoint with { Point = VectorUnits.Quantize(sourcePoint.Point) };
            if (result.Count > 0 && result[^1].Point == point.Point)
            {
                result[^1] = point;
                continue;
            }

            result.Add(point);
        }

        return result;
    }

    private static PressureBrushPoint[] SmoothPressureProfile(IReadOnlyList<PressureBrushPoint> source, int smoothing)
    {
        if (source.Count <= 2) return source.ToArray();

        smoothing = Math.Clamp(smoothing, 0, 100);
        var passes = smoothing >= 72 ? 3 : smoothing >= 24 ? 2 : smoothing >= 6 ? 1 : 0;
        if (passes == 0) return source.ToArray();

        var amount = smoothing / 100f * 0.42f;
        var current = source.ToArray();
        for (var pass = 0; pass < passes; pass++)
        {
            var next = new PressureBrushPoint[current.Length];
            next[0] = current[0];
            next[^1] = current[^1];
            for (var i = 1; i < current.Length - 1; i++)
            {
                var before = current[i - 1];
                var sample = current[i];
                var after = current[i + 1];
                var incomingX = sample.Point.X - before.Point.X;
                var incomingY = sample.Point.Y - before.Point.Y;
                var outgoingX = after.Point.X - sample.Point.X;
                var outgoingY = after.Point.Y - sample.Point.Y;
                var incomingLength = MathF.Sqrt(incomingX * incomingX + incomingY * incomingY);
                var outgoingLength = MathF.Sqrt(outgoingX * outgoingX + outgoingY * outgoingY);
                var cornerAlignment = incomingLength <= 0.0001f || outgoingLength <= 0.0001f
                    ? 1f
                    : (incomingX * outgoingX + incomingY * outgoingY) / (incomingLength * outgoingLength);
                if (cornerAlignment < 0.82f)
                {
                    next[i] = sample;
                    continue;
                }

                var averagePoint = new PointF(
                    (before.Point.X + after.Point.X) * 0.5f,
                    (before.Point.Y + after.Point.Y) * 0.5f);
                var point = new PointF(
                    sample.Point.X + (averagePoint.X - sample.Point.X) * amount,
                    sample.Point.Y + (averagePoint.Y - sample.Point.Y) * amount);
                var averageDiameter = (before.Diameter + after.Diameter) * 0.5f;
                var diameter = sample.Diameter + (averageDiameter - sample.Diameter) * amount;
                next[i] = new PressureBrushPoint(point, Math.Max(VectorUnits.StrokePointsToUnits(0.3f), diameter));
            }

            current = next;
        }

        return current;
    }

    private static List<PressureBrushPoint> ReduceVariableWidthProfile(IReadOnlyList<PressureBrushPoint> source)
    {
        if (source.Count <= 2) return source.ToList();

        var result = new List<PressureBrushPoint>(source.Count) { source[0] };
        for (var index = 1; index < source.Count - 1; index++)
        {
            var previous = result[^1];
            var current = source[index];
            var next = source[index + 1];
            var dx = current.Point.X - previous.Point.X;
            var dy = current.Point.Y - previous.Point.Y;
            var distance = MathF.Sqrt(dx * dx + dy * dy);
            var spacing = Math.Max(
                VectorUnits.StrokePointsToUnits(0.5f),
                Math.Min(previous.Diameter, current.Diameter) * 0.65f);
            var diameterChange = MathF.Abs(current.Diameter - previous.Diameter);
            var nextDx = next.Point.X - current.Point.X;
            var nextDy = next.Point.Y - current.Point.Y;
            var nextLength = MathF.Sqrt(nextDx * nextDx + nextDy * nextDy);
            var directionAlignment = distance <= 0.0001f || nextLength <= 0.0001f
                ? 1f
                : (dx * nextDx + dy * nextDy) / (distance * nextLength);
            var preservePressure = diameterChange > Math.Max(VectorUnits.StrokePointsToUnits(0.2f), current.Diameter * 0.08f);
            var preserveCorner = directionAlignment < VariableWidthCornerAlignment;
            if (distance < spacing && !preservePressure && !preserveCorner) continue;
            result.Add(current);
        }

        result.Add(source[^1]);
        return result;
    }

    private static float Radius(PressureBrushPoint point, float radiusScale)
    {
        return Math.Max(VectorUnits.StrokePointsToUnits(0.5f) * 0.3f, point.Diameter * 0.5f * radiusScale);
    }

    private static PointF[] CreateRoundContour(PointF center, float radius, int segments)
    {
        var contour = new PointF[segments];
        for (var i = 0; i < segments; i++)
        {
            var angle = -MathF.PI / 2 + MathF.Tau * i / segments;
            contour[i] = VectorUnits.Quantize(new PointF(
                center.X + MathF.Cos(angle) * radius,
                center.Y + MathF.Sin(angle) * radius));
        }

        return contour;
    }

    private static void AppendRoundCap(List<PointF> contour, PointF center, PointF normal, float radius, bool forward)
    {
        const int capSegments = 8;
        var startAngle = MathF.Atan2(normal.Y, normal.X);
        for (var i = 1; i < capSegments; i++)
        {
            var angle = forward
                ? startAngle - MathF.PI * i / capSegments
                : startAngle + MathF.PI - MathF.PI * i / capSegments;
            contour.Add(VectorUnits.Quantize(new PointF(
                center.X + MathF.Cos(angle) * radius,
                center.Y + MathF.Sin(angle) * radius)));
        }
    }

    private static void AddRoundCornerJoins(
        Paths64 paths,
        IReadOnlyList<PressureBrushPoint> profile,
        float radiusScale,
        bool clockwise)
    {
        const int joinSegments = 16;
        for (var i = 1; i < profile.Count - 1; i++)
        {
            var previous = profile[i - 1].Point;
            var current = profile[i].Point;
            var next = profile[i + 1].Point;
            var incomingX = current.X - previous.X;
            var incomingY = current.Y - previous.Y;
            var outgoingX = next.X - current.X;
            var outgoingY = next.Y - current.Y;
            var incomingLength = MathF.Sqrt(incomingX * incomingX + incomingY * incomingY);
            var outgoingLength = MathF.Sqrt(outgoingX * outgoingX + outgoingY * outgoingY);
            if (incomingLength <= 0.0001f || outgoingLength <= 0.0001f) continue;

            var alignment = (incomingX * outgoingX + incomingY * outgoingY) / (incomingLength * outgoingLength);
            if (alignment > VariableWidthCornerAlignment) continue;

            var circle = CreateRoundContour(current, Radius(profile[i], radiusScale), joinSegments);
            var path = new Path64(circle.Length);
            foreach (var point in circle)
            {
                path.Add(new Point64(ToClipperCoordinate(point.X), ToClipperCoordinate(point.Y)));
            }

            if (path.Count < 3) continue;
            if ((Clipper.Area(path) < 0) != clockwise) path.Reverse();
            paths.Add(path);
        }
    }

    private static long ToClipperCoordinate(float value)
    {
        return checked((long)Math.Round(value * BrushCoordinateScale, MidpointRounding.AwayFromZero));
    }

    private static List<PointF> Smooth(IReadOnlyList<PointF> source, float amount)
    {
        var result = new List<PointF>(source.Count) { source[0] };
        for (var i = 1; i < source.Count - 1; i++)
        {
            var neighborX = (source[i - 1].X + source[i + 1].X) * 0.5f;
            var neighborY = (source[i - 1].Y + source[i + 1].Y) * 0.5f;
            result.Add(new PointF(
                source[i].X + (neighborX - source[i].X) * amount,
                source[i].Y + (neighborY - source[i].Y) * amount));
        }

        result.Add(source[^1]);
        return result;
    }

    private static List<PointF> Simplify(IReadOnlyList<PointF> source, float tolerance)
    {
        if (source.Count <= 2) return source.ToList();

        var keep = new bool[source.Count];
        keep[0] = true;
        keep[^1] = true;
        var stack = new Stack<(int Start, int End)>();
        stack.Push((0, source.Count - 1));
        var toleranceSquared = tolerance * tolerance;

        while (stack.Count > 0)
        {
            var (start, end) = stack.Pop();
            var furthest = -1;
            var furthestDistanceSquared = toleranceSquared;
            for (var i = start + 1; i < end; i++)
            {
                var distanceSquared = DistanceToSegmentSquared(source[i], source[start], source[end]);
                if (distanceSquared <= furthestDistanceSquared) continue;
                furthest = i;
                furthestDistanceSquared = distanceSquared;
            }

            if (furthest < 0) continue;
            keep[furthest] = true;
            stack.Push((start, furthest));
            stack.Push((furthest, end));
        }

        var result = new List<PointF>();
        for (var i = 0; i < source.Count; i++)
        {
            if (keep[i]) result.Add(source[i]);
        }

        return result;
    }

    private static List<PointF> RemoveDuplicatePoints(IReadOnlyList<PointF> source)
    {
        var result = new List<PointF>(source.Count);
        foreach (var sourcePoint in source)
        {
            var point = VectorUnits.Quantize(sourcePoint);
            if (result.Count > 0 && result[^1] == point) continue;
            result.Add(point);
        }

        return result;
    }

    private static List<PressureBrushSample> RemoveDuplicatePressureSamples(IReadOnlyList<PressureBrushSample> source)
    {
        var result = new List<PressureBrushSample>(source.Count);
        foreach (var sourceSample in source)
        {
            var sample = sourceSample with
            {
                Point = VectorUnits.Quantize(sourceSample.Point),
                SpeedPixelsPerSecond = Math.Clamp(sourceSample.SpeedPixelsPerSecond, 0, 5000),
                HeldSeconds = Math.Max(0, sourceSample.HeldSeconds)
            };
            if (result.Count > 0 && result[^1].Point == sample.Point)
            {
                result[^1] = sample;
                continue;
            }

            result.Add(sample);
        }

        return result;
    }

    private static float[] CumulativeDistances(IReadOnlyList<PointF> points)
    {
        var distances = new float[points.Count];
        for (var i = 1; i < points.Count; i++) distances[i] = distances[i - 1] + MathF.Sqrt(DistanceSquared(points[i - 1], points[i]));
        return distances;
    }

    private static float PressureDiameter(float baseDiameter, float speedPixelsPerSecond, float heldSeconds, float minimumDiameter)
    {
        var normalizedSpeed = Math.Clamp(speedPixelsPerSecond / 1800f, 0f, 1f);
        var speedPressure = 0.38f + 0.62f * MathF.Pow(1f - normalizedSpeed, 0.65f);
        var holdPressure = 0.55f + 0.45f * (1f - MathF.Exp(-heldSeconds / 0.15f));
        return Math.Clamp(baseDiameter * speedPressure * holdPressure, minimumDiameter * 0.6f, baseDiameter * 1.2f);
    }

    private static float DistanceToSegmentSquared(PointF point, PointF start, PointF end)
    {
        var vx = end.X - start.X;
        var vy = end.Y - start.Y;
        var lengthSquared = vx * vx + vy * vy;
        if (lengthSquared <= 0.0001f) return DistanceSquared(point, start);

        var t = ((point.X - start.X) * vx + (point.Y - start.Y) * vy) / lengthSquared;
        t = Math.Clamp(t, 0, 1);
        return DistanceSquared(point, new PointF(start.X + vx * t, start.Y + vy * t));
    }

    private static float DistanceSquared(PointF a, PointF b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }
}
