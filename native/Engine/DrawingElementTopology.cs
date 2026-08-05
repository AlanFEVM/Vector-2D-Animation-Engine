namespace VectorAnimationEngine;

internal enum DrawingElementKind
{
    None,
    Fill,
    Stroke,
    BoundaryStroke
}

internal readonly record struct DrawingElementKey(int ObjectIndex, DrawingElementKind Kind, int PartIndex)
{
    public static DrawingElementKey None => new(-1, DrawingElementKind.None, -1);
    public bool IsValid => ObjectIndex >= 0 && Kind != DrawingElementKind.None;
}

internal readonly record struct DrawingElementHit(DrawingElementKey Key, float Distance, float StartT, float EndT)
{
    public int BezierSegmentIndex { get; init; } = -1;
    public static DrawingElementHit None => new(DrawingElementKey.None, float.MaxValue, 0, 1);
    public bool IsValid => Key.IsValid;
}

internal readonly record struct MaterializedPartMapping(DrawingElementKey Source, DrawingElementKey Result);

internal sealed record MaterializeSelectedPartsResult(
    bool Success,
    bool Changed,
    MaterializedPartMapping[] Parts,
    int[] OldToNewObjectIndex);

internal sealed record LineSegmentMergeResult(
    bool Changed,
    int MergeCount,
    int[] OldToNewObjectIndex);

internal readonly record struct DrawingFillPartGeometry(int PartIndex, PointF[][] Contours);

internal readonly record struct DrawingPolylinePartGeometry(int PartIndex, PointF[] Points);

internal sealed record FillBoundaryLineLink(
    int LineObjectIndex,
    int FillObjectIndex,
    PointF[][] OriginalContours,
    PathBezierNode[][] OriginalBezierContours,
    int ContourIndex,
    int SegmentIndex,
    int SegmentCount,
    int BezierSegmentIndex,
    int BezierSegmentCount,
    bool Reversed);

internal readonly record struct FillBoundaryStrokeLink(
    int LineObjectIndex,
    bool Reversed);

internal readonly record struct PathIntersectionAnchor(
    int ObjectIndex,
    int PartIndex,
    bool StartEndpoint,
    PathBezierSegmentPart OriginalSegment);

internal readonly record struct LineIntersectionAnchor(
    int ObjectIndex,
    bool StartEndpoint,
    PointF OriginalEndpoint,
    PointF OppositeEndpoint,
    PointF Control1,
    PointF Control2,
    bool KeepStraight);

internal sealed record FreeformIntersectionAnchor(
    int ObjectIndex,
    int PointIndex,
    bool MirrorsClosedEndpoint,
    PointF[] OriginalPoints);

internal sealed record SharedBoundaryIntersection(
    PointF OriginalAnchor,
    PathIntersectionAnchor[] PathAnchors,
    LineIntersectionAnchor[] LineAnchors,
    FreeformIntersectionAnchor[] FreeformAnchors,
    int OwnerObjectIndex = -1,
    int OwnerPartIndex = -1)
{
    public bool HasTargets => PathAnchors.Length > 0 || LineAnchors.Length > 0 || FreeformAnchors.Length > 0;
}

internal readonly record struct LineEndpointJunction(
    int NeighborCount,
    int OwnerObjectIndex,
    int FirstNeighborObjectIndex,
    bool FirstNeighborStartEndpoint,
    bool AllSharp,
    LineEndpointConnection[] Connections);

internal readonly record struct LineEndpointConnection(int ObjectIndex, bool StartEndpoint);

internal readonly record struct DrawingTopologySplit(float T, PointF Point);

internal readonly record struct CurveSample(float T, PointF Point);

internal readonly record struct CurveSegmentPart(
    int PartIndex,
    PointF Start,
    PointF Control1,
    PointF Control2,
    PointF End);

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
