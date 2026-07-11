namespace VectorAnimationEngine;

internal readonly record struct PackedSceneObject(
    int Layer,
    PointF Center,
    SizeF Size,
    float Angle,
    float Stroke,
    int Argb,
    int StrokeArgb,
    uint Atoms,
    ShapeKind Shape,
    PointF CurveControl);
