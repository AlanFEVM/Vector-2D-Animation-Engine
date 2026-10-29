namespace VectorAnimationEngine;

internal interface IEditableSnapPointOwner
{
    string Id { get; }
    IReadOnlyList<DrawingObjectSnapPointDefinition> SnapPoints { get; }
    DrawingObjectSnapPointDefinition[] CreateSnapPointSnapshot();
    void RestoreSnapPointSnapshot(IEnumerable<DrawingObjectSnapPointDefinition> points);
}

internal readonly record struct DrawingObjectSnapPointDefinition(
    string Id,
    float X,
    float Y,
    float Z = 0)
{
    public PointF Position => new(X, Y);
}
