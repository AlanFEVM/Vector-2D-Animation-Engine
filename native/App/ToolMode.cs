namespace VectorAnimationEngine;

internal enum ToolMode
{
    Select,
    PolygonLasso,
    FreehandLasso,
    Transform,
    Transform3D,
    Distort,
    Hand,
    Rectangle,
    Ellipse,
    Triangle,
    Polygon,
    Star,
    Line,
    Pen,
    SimplePen,
    Pencil,
    Brush,
    PressureBrush,
    MixingBrush,
    Text,
    Fill,
    InkBottle,
    Eyedropper,
    Gradient,
    SnapPoint,
    Eraser
}

internal enum GradientHandleKind
{
    None,
    Start,
    End,
    Stop
}

internal readonly record struct GradientOverlayHit(GradientHandleKind Kind, int StopIndex = -1)
{
    public static GradientOverlayHit None => new(GradientHandleKind.None);
}
