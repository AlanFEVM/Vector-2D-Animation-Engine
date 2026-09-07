namespace VectorAnimationEngine;

internal readonly record struct DrawingObjectSnapPointDefinition(
    string Id,
    float X,
    float Y,
    float Z = 0)
{
    public PointF Position => new(X, Y);
}
