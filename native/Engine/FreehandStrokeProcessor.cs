using Clipper2Lib;

namespace VectorAnimationEngine;

internal static class FreehandStrokeProcessor
{
    private const double BrushCoordinateScale = 1000d;
    private const double BrushArcToleranceUnits = 0.5d;

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
