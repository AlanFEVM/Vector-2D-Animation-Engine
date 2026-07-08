namespace VectorAnimationEngine;

internal enum DrawingElementKind
{
    None,
    Fill,
    Stroke
}

internal readonly record struct DrawingElementKey(int ObjectIndex, DrawingElementKind Kind, int PartIndex)
{
    public static DrawingElementKey None => new(-1, DrawingElementKind.None, -1);
    public bool IsValid => ObjectIndex >= 0 && Kind != DrawingElementKind.None;
}

internal readonly record struct DrawingElementHit(DrawingElementKey Key, float Distance, float StartT, float EndT)
{
    public static DrawingElementHit None => new(DrawingElementKey.None, float.MaxValue, 0, 1);
    public bool IsValid => Key.IsValid;
}

internal readonly record struct DrawingTopologySplit(float T, PointF Point);

internal readonly record struct DrawingUnitCell(int Layer, int X, int Y)
{
    public static DrawingUnitCell FromPoint(int layer, PointF point)
    {
        return new DrawingUnitCell(layer, (int)MathF.Floor(point.X), (int)MathF.Floor(point.Y));
    }
}

internal static class DrawingTopologyRules
{
    public const float MinStrokeSegmentUnits = 1f;
    public const float UnitIntersectionTolerance = 0.001f;
}
