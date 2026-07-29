namespace VectorAnimationEngine;

internal readonly record struct PathBezierNode(
    PointF Anchor,
    PointF IncomingControl,
    PointF OutgoingControl);

internal readonly record struct PathBezierSegmentPart(
    int PartIndex,
    int ContourIndex,
    int SegmentIndex,
    PointF Start,
    PointF Control1,
    PointF Control2,
    PointF End,
    PointF[] Samples);

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
