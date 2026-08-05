namespace VectorAnimationEngine;

internal readonly record struct PathBezierNode(
    PointF Anchor,
    PointF IncomingControl,
    PointF OutgoingControl);

internal readonly record struct CubicBoundarySegment(
    PointF Start,
    PointF Control1,
    PointF Control2,
    PointF End);

internal readonly record struct PathBezierSegmentPart(
    int PartIndex,
    int ContourIndex,
    int SegmentIndex,
    CubicBoundarySegment Curve,
    PointF[] Samples)
{
    public PointF Start => Curve.Start;
    public PointF Control1 => Curve.Control1;
    public PointF Control2 => Curve.Control2;
    public PointF End => Curve.End;
}

internal readonly record struct FillBezierSegmentPiece(
    int PieceIndex,
    int SourcePartIndex,
    int ContourIndex,
    int SegmentIndex,
    float StartT,
    float EndT,
    PointF Start,
    PointF Control1,
    PointF Control2,
    PointF End,
    bool StartIsVirtualAnchor,
    bool EndIsVirtualAnchor);
