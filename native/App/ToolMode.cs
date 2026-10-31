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
    Eraser,
    ObjectSelect
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

// One eyedropper sample: the target material slot, the representative color (alpha included),
// the complete gradient profile when the sampled object is filled with a gradient, and whether a
// sampleable object was hit at all (ValueTuple-free so the regression can compare fields).
internal readonly record struct EyedropperMaterialSample(
    bool StrokeTarget,
    Color Color,
    GradientKind Kind,
    GradientStop[] Stops,
    bool IsValid = true)
{
    public static EyedropperMaterialSample None => new(false, Color.Empty, GradientKind.Solid, [], IsValid: false);
}
