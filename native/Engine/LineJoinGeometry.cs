namespace VectorAnimationEngine;

internal readonly record struct LineMiterJoin(
    PointF OuterFirstOffset,
    PointF OuterMiter,
    PointF OuterSecondOffset,
    PointF InnerFirstOffset,
    PointF InnerMiter,
    PointF InnerSecondOffset);

internal static class LineJoinGeometry
{
    private const float ParallelTolerance = 0.0001f;
    private const float MiterLimit = 8f;

    public static bool TryCreateMiter(
        PointF joint,
        PointF firstInterior,
        PointF secondInterior,
        float halfWidth,
        out LineMiterJoin miterJoin)
    {
        miterJoin = default;
        if (halfWidth <= 0.01f) return false;

        if (!TryNormalize(joint, firstInterior, out var firstDirection)
            || !TryNormalize(joint, secondInterior, out var secondDirection))
        {
            return false;
        }

        var cross = Cross(firstDirection, secondDirection);
        if (Math.Abs(cross) <= ParallelTolerance) return false;

        var firstNormal = new PointF(-firstDirection.Y, firstDirection.X);
        var secondNormal = new PointF(-secondDirection.Y, secondDirection.X);
        var firstSign = cross > 0 ? -1f : 1f;
        var secondSign = -firstSign;
        var outerFirstOffset = Offset(joint, firstNormal, halfWidth * firstSign);
        var outerSecondOffset = Offset(joint, secondNormal, halfWidth * secondSign);
        var innerFirstOffset = Offset(joint, firstNormal, -halfWidth * firstSign);
        var innerSecondOffset = Offset(joint, secondNormal, -halfWidth * secondSign);
        var outerMiter = IntersectOffsetEdges(outerFirstOffset, firstDirection, outerSecondOffset, secondDirection);
        var innerMiter = IntersectOffsetEdges(innerFirstOffset, firstDirection, innerSecondOffset, secondDirection);
        if (!float.IsFinite(outerMiter.X) || !float.IsFinite(outerMiter.Y)
            || !float.IsFinite(innerMiter.X) || !float.IsFinite(innerMiter.Y))
        {
            return false;
        }
        var miterDistance = MathF.Sqrt(
            (outerMiter.X - joint.X) * (outerMiter.X - joint.X)
            + (outerMiter.Y - joint.Y) * (outerMiter.Y - joint.Y));
        if (!float.IsFinite(miterDistance) || miterDistance > halfWidth * MiterLimit) return false;

        miterJoin = new LineMiterJoin(
            outerFirstOffset,
            outerMiter,
            outerSecondOffset,
            innerFirstOffset,
            innerMiter,
            innerSecondOffset);
        return true;
    }

    private static bool TryNormalize(PointF origin, PointF point, out PointF direction)
    {
        var x = point.X - origin.X;
        var y = point.Y - origin.Y;
        var length = MathF.Sqrt(x * x + y * y);
        if (length <= ParallelTolerance)
        {
            direction = PointF.Empty;
            return false;
        }

        direction = new PointF(x / length, y / length);
        return true;
    }

    private static PointF Offset(PointF point, PointF normal, float distance)
    {
        return new PointF(point.X + normal.X * distance, point.Y + normal.Y * distance);
    }

    private static PointF IntersectOffsetEdges(PointF firstPoint, PointF firstDirection, PointF secondPoint, PointF secondDirection)
    {
        var denominator = Cross(firstDirection, secondDirection);
        var delta = new PointF(secondPoint.X - firstPoint.X, secondPoint.Y - firstPoint.Y);
        var firstDistance = Cross(delta, secondDirection) / denominator;
        return new PointF(
            firstPoint.X + firstDirection.X * firstDistance,
            firstPoint.Y + firstDirection.Y * firstDistance);
    }

    private static float Cross(PointF first, PointF second) => first.X * second.Y - first.Y * second.X;
}
