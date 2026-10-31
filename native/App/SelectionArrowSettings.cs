namespace VectorAnimationEngine;

internal enum SelectionArrowBehavior
{
    Object,
    Path
}

internal sealed record SelectionArrowSettings
{
    public SelectionArrowBehavior BlackArrow { get; init; } = SelectionArrowBehavior.Object;
    public SelectionArrowBehavior WhiteArrow { get; init; } = SelectionArrowBehavior.Path;
    public bool DoubleClickEntersSymbols { get; init; } = true;
    public bool ShowCornerMarks { get; init; }
    public bool ShowSelectionLabel { get; init; }
    public float OutlineWidth { get; init; } = 1.5f;
    public int HitTolerancePixels { get; init; } = 10;

    public SelectionArrowBehavior BehaviorFor(ToolMode tool) =>
        tool == ToolMode.ObjectSelect ? BlackArrow : WhiteArrow;

    public SelectionArrowSettings Normalized() => this with
    {
        BlackArrow = Enum.IsDefined(BlackArrow) ? BlackArrow : SelectionArrowBehavior.Object,
        WhiteArrow = Enum.IsDefined(WhiteArrow) ? WhiteArrow : SelectionArrowBehavior.Path,
        OutlineWidth = float.IsFinite(OutlineWidth) ? Math.Clamp(OutlineWidth, 0.75f, 3f) : 1.5f,
        HitTolerancePixels = Math.Clamp(HitTolerancePixels, 4, 20)
    };
}
