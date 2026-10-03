namespace VectorAnimationEngine;

/// <summary>
/// Classifies how one frame of the motion track relates to the timeline. The Stage overlay uses
/// this to pick its ink: ordinary frames draw solid, held/blank frames draw lighter, and frames
/// inside a tween span draw as a differently coloured dash.
/// </summary>
internal enum MotionTrackFrameKind : byte
{
    /// <summary>A populated keyframe that owns its own content.</summary>
    Keyframe,

    /// <summary>A blank keyframe: the exposure exists but carries no content.</summary>
    BlankKeyframe,

    /// <summary>
    /// A frame interpolated inside a classic tween span. Creating an instance tween materializes a
    /// populated keyframe at every interior frame, so the tween test must outrank the bare
    /// keyframe test; otherwise this value is unreachable for exactly the instance tweens the
    /// motion track exists to show.
    /// </summary>
    TweenFrame,

    /// <summary>A frame held from an earlier populated keyframe; it has no key of its own.</summary>
    HeldFrame
}

/// <summary>
/// One sampled anchor of the motion track: where the symbol anchor lands on a given frame, plus the
/// timeline classification the overlay needs. <see cref="WorldPosition"/> is the anchor's position
/// in the edited composition's own coordinate space, so the Stage can convert it with its ordinary
/// world-to-screen API and stay correct under camera navigation.
/// </summary>
/// <param name="Frame">Absolute frame index the anchor was sampled at.</param>
/// <param name="WorldPosition">
/// Anchor position for this frame. For a symbol instance this is the anchor point after the frame's
/// instance transform, which resolves to the instance's evaluated position.
/// </param>
/// <param name="Kind">Timeline classification of the frame.</param>
/// <param name="IsAdjusted">
/// True when the frame carries its own explicit placement instead of inheriting one, so the overlay
/// renders it in the "adjusted" colour. Frame 0 counts as adjusted only once it was moved off the
/// origin.
/// </param>
/// <param name="IsOnTweenSegment">
/// True when this anchor sits strictly between the two endpoints of a classic tween span, so the
/// segment leading to it is drawn as a differently coloured dash.
/// </param>
internal readonly record struct MotionTrackAnchor(
    int Frame,
    PointF WorldPosition,
    MotionTrackFrameKind Kind,
    bool IsAdjusted,
    bool IsOnTweenSegment)
{
    /// <summary>Populated and tween frames accept hover, click and drag; held frames do too.</summary>
    public bool IsSelectable => Kind != MotionTrackFrameKind.BlankKeyframe;

    /// <summary>Blank and held frames are drawn with the lighter ink.</summary>
    public bool IsMutedInk => Kind is MotionTrackFrameKind.BlankKeyframe or MotionTrackFrameKind.HeldFrame;
}

/// <summary>
/// Immutable sample of the selected symbol's motion over the onion-skin pointer range. Built by
/// <c>DrawingObjectMotionTrackBuilder</c> and consumed by the Stage overlay and hit-testing; it
/// carries no editor state of its own.
/// </summary>
internal sealed class DrawingObjectMotionTrack
{
    /// <summary>An empty track: the overlay draws nothing and hit-testing never matches.</summary>
    public static DrawingObjectMotionTrack Empty { get; } = new();

    /// <summary>Frame the onion skin is centred on when this track was sampled.</summary>
    public int CenterFrame { get; init; }

    /// <summary>First sampled frame, inclusive.</summary>
    public int FirstFrame { get; init; }

    /// <summary>Last sampled frame, inclusive.</summary>
    public int LastFrame { get; init; }

    /// <summary>Anchors ordered by ascending <see cref="MotionTrackAnchor.Frame"/>.</summary>
    public IReadOnlyList<MotionTrackAnchor> Anchors { get; init; } = [];

    public bool HasAnchors => Anchors.Count > 0;

    /// <summary>True when at least one anchor can be hovered, selected or dragged.</summary>
    public bool HasSelectableAnchors => Anchors.Any(anchor => anchor.IsSelectable);

    /// <summary>Returns the anchor sampled for <paramref name="frame"/>, or null when absent.</summary>
    public MotionTrackAnchor? FindAnchor(int frame)
    {
        foreach (var anchor in Anchors)
        {
            if (anchor.Frame == frame) return anchor;
        }

        return null;
    }

    /// <summary>
    /// The initial selection produced by a click: the clicked frame alone. Kept on the contract so
    /// the Stage overlay and the MainForm session agree on what a plain click selects.
    /// </summary>
    public int[] SelectSingle(int frame) => FindAnchor(frame) is { IsSelectable: true } ? [frame] : [];
}