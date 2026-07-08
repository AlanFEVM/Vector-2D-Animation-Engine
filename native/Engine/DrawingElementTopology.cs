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
