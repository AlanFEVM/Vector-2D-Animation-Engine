namespace VectorAnimationEngine;

internal enum ToolMode
{
    Select,
    Transform,
    Hand,
    Rectangle,
    Ellipse,
    Triangle,
    Polygon,
    Star,
    Line,
    Pen,
    Pencil,
    Brush,
    PressureBrush,
    Fill,
    InkBottle,
    Gradient,
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
