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
    PointF CurveControl,
    PointF CurveControl2,
    LineEndpointStyle StartEndpointStyle = LineEndpointStyle.Round,
    LineEndpointStyle EndEndpointStyle = LineEndpointStyle.Round,
    bool LinearGradientEnabled = false,
    GradientKind GradientKind = GradientKind.Solid,
    int GradientStartArgb = 0,
    int GradientEndArgb = 0,
    PointF GradientStart = default,
    PointF GradientEnd = default,
    GradientStop[]? GradientStops = null,
    int ShapeVertexCount = 0);
