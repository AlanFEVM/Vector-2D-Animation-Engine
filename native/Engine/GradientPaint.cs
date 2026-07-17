namespace VectorAnimationEngine;

internal enum GradientKind : byte
{
    Solid,
    Linear,
    Radial,
    ShapeRadial
}

internal readonly record struct GradientStop(float Position, int Argb)
{
    public GradientStop(float position, Color color) : this(position, color.ToArgb())
    {
    }
}

internal readonly record struct GradientPathSample(PointF Point, float Position);

internal readonly record struct GradientPathGradientSegment(
    PointF Start,
    PointF End,
    float StartPosition,
    float EndPosition,
    int StartArgb,
    int EndArgb);

internal readonly record struct GradientAxis(PointF Start, PointF End);

internal static class GradientPaintUtilities
{
    public static bool TryFindShapeBoundaryPoint(
        IReadOnlyList<PointF[]> contours,
        PointF center,
        PointF direction,
        out PointF boundary)
    {
        boundary = center;
        var directionLength = MathF.Sqrt(direction.X * direction.X + direction.Y * direction.Y);
        if (directionLength <= 0.0001f) return false;

        var dx = direction.X / directionLength;
        var dy = direction.Y / directionLength;
        var farthest = float.NegativeInfinity;
        foreach (var contour in contours)
        {
            if (contour.Length < 2) continue;
            for (var index = 0; index < contour.Length; index++)
            {
                var start = contour[index];
                var end = contour[(index + 1) % contour.Length];
                var sx = end.X - start.X;
                var sy = end.Y - start.Y;
                var denominator = dx * sy - dy * sx;
                if (Math.Abs(denominator) <= 0.000001f) continue;

                var qx = start.X - center.X;
                var qy = start.Y - center.Y;
                var rayDistance = (qx * sy - qy * sx) / denominator;
                var segmentPosition = (qx * dy - qy * dx) / denominator;
                if (rayDistance <= 0.001f
                    || segmentPosition < -0.0001f
                    || segmentPosition > 1.0001f
                    || rayDistance <= farthest)
                {
                    continue;
                }

                farthest = rayDistance;
            }
        }

        if (!float.IsFinite(farthest)) return false;
        boundary = new PointF(center.X + dx * farthest, center.Y + dy * farthest);
        return true;
    }

    public static float ShapeRadialPosition(
        IReadOnlyList<PointF[]> contours,
        PointF center,
        PointF point)
    {
        var direction = new PointF(point.X - center.X, point.Y - center.Y);
        var distance = MathF.Sqrt(direction.X * direction.X + direction.Y * direction.Y);
        if (distance <= 0.0001f) return 0f;
        if (!TryFindShapeBoundaryPoint(contours, center, direction, out var boundary)) return 1f;
        var boundaryX = boundary.X - center.X;
        var boundaryY = boundary.Y - center.Y;
        var boundaryDistance = MathF.Sqrt(boundaryX * boundaryX + boundaryY * boundaryY);
        return boundaryDistance <= 0.0001f ? 1f : Math.Clamp(distance / boundaryDistance, 0f, 1f);
    }

    public static bool AreStopsOpaque(IReadOnlyList<GradientStop> stops)
    {
        return stops.Count > 0 && stops.All(stop => (uint)stop.Argb >> 24 == byte.MaxValue);
    }

    public static GradientStop[] ScaleStopAlpha(IReadOnlyList<GradientStop> stops, float multiplier)
    {
        var scale = Math.Clamp(multiplier, 0f, 1f);
        return stops
            .Select(stop =>
            {
                var color = Color.FromArgb(stop.Argb);
                var alpha = (int)Math.Clamp(Math.Round(color.A * scale), 0, 255);
                return new GradientStop(stop.Position, Color.FromArgb(alpha, color.R, color.G, color.B));
            })
            .ToArray();
    }

    public static float PathLength(IReadOnlyList<PointF> points)
    {
        var length = 0f;
        for (var index = 1; index < points.Count; index++)
        {
            var dx = points[index].X - points[index - 1].X;
            var dy = points[index].Y - points[index - 1].Y;
            length += MathF.Sqrt(dx * dx + dy * dy);
        }

        return length;
    }

    public static GradientPathSample[] CreatePathSamples(IReadOnlyList<PointF> points, int segmentCount)
    {
        var length = PathLength(points);
        if (points.Count < 2 || length <= 0.001f) return [];

        segmentCount = Math.Clamp(segmentCount, 1, 256);
        var samples = new GradientPathSample[segmentCount + 1];
        var sourceIndex = 1;
        var coveredLength = 0f;
        var segmentLength = SegmentLength(points[0], points[1]);
        for (var sampleIndex = 0; sampleIndex <= segmentCount; sampleIndex++)
        {
            var position = sampleIndex / (float)segmentCount;
            var targetLength = length * position;
            while (sourceIndex < points.Count - 1 && coveredLength + segmentLength < targetLength)
            {
                coveredLength += segmentLength;
                sourceIndex++;
                segmentLength = SegmentLength(points[sourceIndex - 1], points[sourceIndex]);
            }

            var local = segmentLength <= 0.001f ? 0f : Math.Clamp((targetLength - coveredLength) / segmentLength, 0f, 1f);
            var start = points[sourceIndex - 1];
            var end = points[sourceIndex];
            samples[sampleIndex] = new GradientPathSample(
                new PointF(start.X + (end.X - start.X) * local, start.Y + (end.Y - start.Y) * local),
                position);
        }

        return samples;
    }

    public static GradientPathGradientSegment[] CreatePathGradientSegments(
        IReadOnlyList<PointF> points,
        IReadOnlyList<GradientStop> stops,
        int segmentCount)
    {
        var samples = CreatePathSamples(points, segmentCount);
        if (samples.Length < 2) return [];

        var segments = new GradientPathGradientSegment[samples.Length - 1];
        var count = 0;
        for (var index = 1; index < samples.Length; index++)
        {
            var start = samples[index - 1];
            var end = samples[index];
            if (SegmentLength(start.Point, end.Point) <= 0.001f) continue;

            segments[count++] = new GradientPathGradientSegment(
                start.Point,
                end.Point,
                start.Position,
                end.Position,
                SampleColor(stops, start.Position).ToArgb(),
                SampleColor(stops, end.Position).ToArgb());
        }

        return count == segments.Length ? segments : segments[..count];
    }

    public static bool TryCreateSelfIntersectionFallbackAxis(
        IReadOnlyList<PointF> points,
        out GradientAxis axis)
    {
        axis = default;
        if (points.Count < 4 || !HasSelfIntersection(points)) return false;

        var centerX = points.Average(point => (double)point.X);
        var centerY = points.Average(point => (double)point.Y);
        var xx = 0d;
        var xy = 0d;
        var yy = 0d;
        foreach (var point in points)
        {
            var dx = point.X - centerX;
            var dy = point.Y - centerY;
            xx += dx * dx;
            xy += dx * dy;
            yy += dy * dy;
        }

        var angle = 0.5d * Math.Atan2(2d * xy, xx - yy);
        var directionX = Math.Cos(angle);
        var directionY = Math.Sin(angle);
        var minimum = double.PositiveInfinity;
        var maximum = double.NegativeInfinity;
        foreach (var point in points)
        {
            var projection = (point.X - centerX) * directionX + (point.Y - centerY) * directionY;
            minimum = Math.Min(minimum, projection);
            maximum = Math.Max(maximum, projection);
        }

        if (!double.IsFinite(minimum) || maximum - minimum <= 0.001d) return false;
        var firstProjection = (points[0].X - centerX) * directionX + (points[0].Y - centerY) * directionY;
        if (Math.Abs(firstProjection - maximum) < Math.Abs(firstProjection - minimum))
        {
            (minimum, maximum) = (maximum, minimum);
        }

        axis = new GradientAxis(
            new PointF((float)(centerX + directionX * minimum), (float)(centerY + directionY * minimum)),
            new PointF((float)(centerX + directionX * maximum), (float)(centerY + directionY * maximum)));
        return true;
    }

    public static Color SampleColor(IReadOnlyList<GradientStop> stops, float position)
    {
        if (stops.Count == 0) return Color.White;
        position = Math.Clamp(position, 0f, 1f);
        var previous = stops[0];
        foreach (var current in stops.Skip(1))
        {
            if (position > current.Position)
            {
                previous = current;
                continue;
            }

            var length = Math.Max(0.0001f, current.Position - previous.Position);
            var amount = Math.Clamp((position - previous.Position) / length, 0f, 1f);
            var from = Color.FromArgb(previous.Argb);
            var to = Color.FromArgb(current.Argb);
            return Color.FromArgb(
                (int)MathF.Round(from.A + (to.A - from.A) * amount),
                (int)MathF.Round(from.R + (to.R - from.R) * amount),
                (int)MathF.Round(from.G + (to.G - from.G) * amount),
                (int)MathF.Round(from.B + (to.B - from.B) * amount));
        }

        return Color.FromArgb(stops[^1].Argb);
    }

    private static float SegmentLength(PointF start, PointF end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private static bool HasSelfIntersection(IReadOnlyList<PointF> points)
    {
        for (var firstEnd = 1; firstEnd < points.Count; firstEnd++)
        {
            var firstStart = points[firstEnd - 1];
            var firstFinish = points[firstEnd];
            for (var secondEnd = firstEnd + 2; secondEnd < points.Count; secondEnd++)
            {
                if (SegmentsIntersectInside(
                        firstStart,
                        firstFinish,
                        points[secondEnd - 1],
                        points[secondEnd]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool SegmentsIntersectInside(PointF a, PointF b, PointF c, PointF d)
    {
        const double epsilon = 0.000001d;
        var rx = (double)b.X - a.X;
        var ry = (double)b.Y - a.Y;
        var sx = (double)d.X - c.X;
        var sy = (double)d.Y - c.Y;
        var denominator = Cross(rx, ry, sx, sy);
        var qx = (double)c.X - a.X;
        var qy = (double)c.Y - a.Y;
        if (Math.Abs(denominator) > epsilon)
        {
            var firstPosition = Cross(qx, qy, sx, sy) / denominator;
            var secondPosition = Cross(qx, qy, rx, ry) / denominator;
            return firstPosition > epsilon
                && firstPosition < 1d - epsilon
                && secondPosition > epsilon
                && secondPosition < 1d - epsilon;
        }

        if (Math.Abs(Cross(qx, qy, rx, ry)) > epsilon) return false;
        var useX = Math.Abs(rx) >= Math.Abs(ry);
        var a0 = useX ? a.X : a.Y;
        var a1 = useX ? b.X : b.Y;
        var c0 = useX ? c.X : c.Y;
        var c1 = useX ? d.X : d.Y;
        var overlapStart = Math.Max(Math.Min(a0, a1), Math.Min(c0, c1));
        var overlapEnd = Math.Min(Math.Max(a0, a1), Math.Max(c0, c1));
        return overlapEnd - overlapStart > 0.001f;
    }

    private static double Cross(double ax, double ay, double bx, double by) => ax * by - ay * bx;
}
