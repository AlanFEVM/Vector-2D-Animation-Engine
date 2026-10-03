using System.Drawing.Drawing2D;

namespace VectorAnimationEngine;

/// <summary>
/// Stage overlay for the timeline motion track: the per-frame anchors of the edited symbol drawn on
/// top of the onion-skin ghosts, joined by a dashed trajectory. This partial owns the overlay state,
/// the screen geometry both render backends consume, hit-testing for the pointer session, and the
/// GDI drawing. The sampler that produces <see cref="DrawingObjectMotionTrack"/> and the pointer
/// session that reacts to hits live outside this file.
/// </summary>
internal sealed partial class StageControl : Control
{
    /// <summary>Anchor marker extent in device-independent pixels, scaled by <see cref="MotionTrackDpiScale"/>.</summary>
    private const float MotionTrackAnchorSizePixels = 9f;

    /// <summary>Hover/selection ring extent in device-independent pixels.</summary>
    private const float MotionTrackHaloSizePixels = 16f;

    /// <summary>Extra room the transform box keeps around the selected anchors, in device-independent pixels.</summary>
    private const float MotionTrackTransformBoxInsetPixels = 7f;

    /// <summary>Default pick radius for <see cref="HitTestMotionTrackAnchor"/>, in device-independent pixels.</summary>
    private const float MotionTrackHitRadiusPixels = 8f;

    /// <summary>
    /// Ink for the hovered anchor ring and its frame-number label, shared by the overlay and the
    /// Direct2D pass so both backends render the same hue.
    /// </summary>
    internal static Color MotionTrackHoverInk => Theme.Accent;

    /// <summary>Ink for the selection ring, brightened from the accent so selection outranks hover.</summary>
    internal static Color MotionTrackSelectionInk => Theme.AccentLabel;

    /// <summary>
    /// GDI resources for the overlay, cached per DPI factor. Pen widths embed the DPI scale, so the
    /// cache is keyed by scale instead of being rebuilt each frame. The set is process-wide and bounded
    /// by the distinct DPI factors a machine can report (see <see cref="MotionTrackDpiScale"/>), which
    /// keeps the overlay from leaking per-control resources it has no disposal hook to release.
    /// </summary>
    private static readonly Dictionary<float, MotionTrackGdiResources> MotionTrackResourcesByScale = [];

    /// <summary>Frames the pointer session currently has selected. Empty when nothing is selected.</summary>
    private HashSet<int> _motionTrackSelectedFrames = [];

    /// <summary>Overlay state mirrored for the Direct2D partial; see the public surface below.</summary>
    private DrawingObjectMotionTrack? _motionTrack;
    private bool _motionTrackTransformBoxVisible;

    /// <summary>The sampled motion track, or null when the overlay is off.</summary>
    public DrawingObjectMotionTrack? MotionTrack => _motionTrack;

    /// <summary>True when a track with at least one anchor is attached, so the overlay has something to draw.</summary>
    public bool MotionTrackVisible => _motionTrack is { HasAnchors: true };

    /// <summary>
    /// True when the caller asked for the transform box and the selection actually has a drawable box
    /// (one to three frames). The overlay checks the frame count again while drawing, so a stale flag
    /// cannot produce a box for an empty or oversized selection.
    /// </summary>
    public bool MotionTrackTransformBoxVisible =>
        _motionTrackTransformBoxVisible && _motionTrackSelectedFrames.Count is >= 1 and <= 3;

    /// <summary>Frame under the pointer, or -1 when the pointer is not over any selectable anchor.</summary>
    public int MotionTrackHoverFrame { get; private set; } = -1;

    /// <summary>DPI factor applied to overlay marker sizes; mirrors the Stage spatial gizmo convention.</summary>
    private float MotionTrackDpiScale => SpatialGizmoDpiScale;

    /// <summary>
    /// Attaches a freshly sampled track. <paramref name="selectedFrames"/> replaces the current
    /// selection; pass null to keep it. Untouched state performs no invalidation, and selection or
    /// hover frames the new track no longer contains are dropped so the overlay cannot reference
    /// frames that disappeared when the onion-skin range moved.
    /// </summary>
    public void SetMotionTrack(DrawingObjectMotionTrack? track, IReadOnlyCollection<int>? selectedFrames = null)
    {
        var trackChanged = !MotionTrackEquivalent(_motionTrack, track);
        _motionTrack = track;

        var selectionChanged = false;
        if (selectedFrames is not null)
        {
            selectionChanged = ApplyMotionTrackSelection(selectedFrames);
        }

        if (!trackChanged && !selectionChanged)
        {
            return;
        }

        if (trackChanged)
        {
            // Hover and selection must not survive onto a range that no longer samples those frames.
            if (MotionTrackHoverFrame >= 0 && track?.FindAnchor(MotionTrackHoverFrame) is not { IsSelectable: true })
            {
                MotionTrackHoverFrame = -1;
            }

            if (PruneMotionTrackSelection(track))
            {
                selectionChanged = true;
            }
        }

        InvalidateMotionTrackOverlay();
    }

    /// <summary>Replaces the selected frames. Null or empty clears the selection.</summary>
    public void SetMotionTrackSelection(IReadOnlyCollection<int>? selectedFrames)
    {
        if (!ApplyMotionTrackSelection(selectedFrames)) return;
        InvalidateMotionTrackOverlay();
    }

    /// <summary>
    /// Sets the hovered frame. Values that are not selectable anchors are treated as "no hover", and
    /// an unchanged value skips the invalidate so pointer moves across empty stage stay cheap.
    /// </summary>
    public void SetMotionTrackHover(int frame)
    {
        var next = frame >= 0 && _motionTrack?.FindAnchor(frame) is { IsSelectable: true } ? frame : -1;
        if (MotionTrackHoverFrame == next) return;
        MotionTrackHoverFrame = next;
        InvalidateMotionTrackOverlay();
    }

    /// <summary>Enables the multi-selection transform box; the overlay draws it only for 1..3 selected frames.</summary>
    public void SetMotionTrackTransformBoxVisible(bool visible)
    {
        if (_motionTrackTransformBoxVisible == visible) return;
        _motionTrackTransformBoxVisible = visible;
        InvalidateMotionTrackOverlay();
    }

    /// <summary>
    /// Returns the frame of the nearest selectable anchor within <paramref name="hitRadiusPixels"/>, or
    /// -1 when nothing is in range. An adjusted frame wins over an unadjusted one; equal-priority
    /// candidates resolve towards <see cref="DrawingObjectMotionTrack.CenterFrame"/> because the author
    /// is almost always working at the onion-skin centre.
    /// </summary>
    internal int HitTestMotionTrackAnchor(Point screen, float hitRadiusPixels = MotionTrackHitRadiusPixels)
    {
        if (_motionTrack is not { HasAnchors: true } track) return -1;
        if (hitRadiusPixels <= 0f) return -1;
        // The pick radius follows the pointer, not the zoom, so it is scaled by DPI only.
        var radius = hitRadiusPixels * MotionTrackDpiScale;
        var radiusSquared = radius * radius;
        var bestFrame = -1;
        var bestIsAdjusted = false;
        var bestDistance = float.MaxValue;
        var bestCenterDistance = int.MaxValue;

        foreach (var anchor in track.Anchors)
        {
            if (!anchor.IsSelectable) continue;
            var point = ToMotionTrackScreenPoint(anchor.WorldPosition);
            if (!IsFiniteMotionTrackPoint(point)) continue;
            var dx = point.X - screen.X;
            var dy = point.Y - screen.Y;
            var distance = dx * dx + dy * dy;
            if (distance > radiusSquared) continue;

            var centerDistance = Math.Abs(anchor.Frame - track.CenterFrame);
            if (bestFrame >= 0)
            {
                // Adjusted frames outrank inherited ones even when the inherited anchor is nearer.
                if (bestIsAdjusted && !anchor.IsAdjusted) continue;
                if (bestIsAdjusted == anchor.IsAdjusted)
                {
                    if (distance > bestDistance) continue;
                    if (distance == bestDistance && centerDistance >= bestCenterDistance) continue;
                }
            }

            bestFrame = anchor.Frame;
            bestIsAdjusted = anchor.IsAdjusted;
            bestDistance = distance;
            bestCenterDistance = centerDistance;
        }

        return bestFrame;
    }

    /// <summary>Screen position of one sampled frame's anchor. False when the frame is not sampled.</summary>
    internal bool TryGetMotionTrackAnchorScreenPoint(int frame, out PointF screen)
    {
        if (_motionTrack?.FindAnchor(frame) is { } anchor)
        {
            var point = ToMotionTrackScreenPoint(anchor.WorldPosition);
            if (IsFiniteMotionTrackPoint(point))
            {
                screen = point;
                return true;
            }
        }

        screen = PointF.Empty;
        return false;
    }

    /// <summary>Screen-space bounding box of the selected anchors. False when nothing selectable is selected.</summary>
    internal bool TryGetMotionTrackSelectionScreenBounds(out RectangleF bounds)
    {
        return TryResolveMotionTrackSelectionBounds(out bounds, requireBoxableCount: false);
    }

    /// <summary>Frames whose anchor screen position falls inside a marquee rectangle, ascending by frame.</summary>
    internal int[] HitTestMotionTrackAnchorsInScreenRectangle(RectangleF screenBounds)
    {
        if (_motionTrack is not { HasAnchors: true } track) return [];
        var normalized = NormalizeMotionTrackRectangle(screenBounds);
        if (normalized.Width <= 0f && normalized.Height <= 0f) return [];
        var frames = new List<int>();

        foreach (var anchor in track.Anchors)
        {
            if (!anchor.IsSelectable) continue;
            var point = ToMotionTrackScreenPoint(anchor.WorldPosition);
            if (!IsFiniteMotionTrackPoint(point)) continue;
            // Inclusive edges: a marquee that grazes an anchor still selects it.
            if (point.X < normalized.Left || point.X > normalized.Right) continue;
            if (point.Y < normalized.Top || point.Y > normalized.Bottom) continue;
            frames.Add(anchor.Frame);
        }

        // Anchors are already frame-ascending, but sorting keeps the contract explicit for callers
        // that feed the result straight back into the selection set.
        frames.Sort();
        return [.. frames];
    }

    /// <summary>
    /// Screen geometry of every sampled anchor, in one pass, for the Direct2D overlay. The Direct2D
    /// partial consumes this instead of repeating the world-to-screen and selection bookkeeping.
    /// </summary>
    internal MotionTrackScreenAnchor[] ResolveMotionTrackScreenAnchors()
    {
        if (_motionTrack is not { HasAnchors: true } track) return [];

        var result = new MotionTrackScreenAnchor[track.Anchors.Count];
        var count = 0;
        foreach (var anchor in track.Anchors)
        {
            var point = ToMotionTrackScreenPoint(anchor.WorldPosition);
            if (!IsFiniteMotionTrackPoint(point)) continue;
            result[count++] = new MotionTrackScreenAnchor(
                anchor.Frame,
                point,
                anchor.IsAdjusted,
                anchor.IsMutedInk,
                anchor.IsOnTweenSegment,
                anchor.IsSelectable,
                _motionTrackSelectedFrames.Contains(anchor.Frame));
        }

        if (count == result.Length) return result;
        var trimmed = new MotionTrackScreenAnchor[count];
        Array.Copy(result, trimmed, count);
        return trimmed;
    }

    /// <summary>
    /// Screen geometry of the transform box that surrounds a multi-selection. False when the box is
    /// disabled, or the selection holds zero or more than three frames.
    /// </summary>
    internal bool TryResolveMotionTrackTransformBoxScreen(out RectangleF bounds)
    {
        if (!MotionTrackTransformBoxVisible)
        {
            bounds = RectangleF.Empty;
            return false;
        }

        return TryResolveMotionTrackSelectionBounds(out bounds, requireBoxableCount: true);
    }

    /// <summary>
    /// GDI overlay pass. Draws the dashed trajectory, the anchors and their ink, the selection, the
    /// hover ring with its frame label, and the multi-selection transform box. The method keeps no
    /// state beyond its own GDI caches, so the caller may run it any number of times per frame.
    /// </summary>
    private void DrawMotionTrack(Graphics g)
    {
        if (!MotionTrackVisible) return;
        var anchors = ResolveMotionTrackScreenAnchors();
        if (anchors.Length == 0) return;

        var oldMode = g.SmoothingMode;
        var oldPixelOffset = g.PixelOffsetMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        try
        {
            DrawMotionTrackTransformBox(g);
            DrawMotionTrackSegments(g, anchors);
            DrawMotionTrackAnchors(g, anchors);
            DrawMotionTrackHoverLabel(g, anchors);
        }
        finally
        {
            g.SmoothingMode = oldMode;
            g.PixelOffsetMode = oldPixelOffset;
        }
    }

    /// <summary>Joins every consecutive pair of anchors so blank and held frames still sit on one trajectory.</summary>
    private void DrawMotionTrackSegments(Graphics g, MotionTrackScreenAnchor[] anchors)
    {
        var resources = MotionTrackResources();
        for (var index = 1; index < anchors.Length; index++)
        {
            var start = anchors[index - 1];
            var end = anchors[index];
            // A span is a tween segment when either endpoint reports it, so the dash flips exactly at
            // the tween boundaries the sampler marked.
            var isTweenSegment = start.IsOnTweenSegment || end.IsOnTweenSegment;
            g.DrawLine(isTweenSegment ? resources.TweenPen : resources.PathPen, start.Screen, end.Screen);
        }
    }

    /// <summary>Draws each anchor marker with the ink its timeline classification asks for.</summary>
    private void DrawMotionTrackAnchors(Graphics g, MotionTrackScreenAnchor[] anchors)
    {
        var scale = MotionTrackDpiScale;
        var anchorSize = MotionTrackAnchorSizePixels * scale;
        var haloSize = MotionTrackHaloSizePixels * scale;
        var resources = MotionTrackResources();

        foreach (var anchor in anchors)
        {
            var isHovered = anchor.Frame == MotionTrackHoverFrame;

            if (MotionTrackHaloVisible(anchor.IsSelected, isHovered))
            {
                // Rings sit behind the marker so the marker edge stays crisp.
                var haloRadius = haloSize * 0.5f;
                if (anchor.IsSelected)
                {
                    g.DrawEllipse(
                        resources.SelectionPen,
                        anchor.Screen.X - haloRadius,
                        anchor.Screen.Y - haloRadius,
                        haloSize,
                        haloSize);
                }

                if (isHovered)
                {
                    g.DrawEllipse(
                        resources.HoverPen,
                        anchor.Screen.X - haloRadius,
                        anchor.Screen.Y - haloRadius,
                        haloSize,
                        haloSize);
                }
            }

            var size = anchor.IsSelected ? anchorSize * 1.15f : anchorSize;
            var half = size * 0.5f;
            var bounds = new RectangleF(anchor.Screen.X - half, anchor.Screen.Y - half, size, size);
            var fill = anchor.IsAdjusted
                ? resources.AdjustedBrush
                : anchor.IsOnTweenSegment
                    ? resources.TweenBrush
                    : anchor.IsMutedInk
                        ? resources.MutedBrush
                        : resources.AnchorBrush;
            g.FillRectangle(fill, bounds);
            g.DrawRectangle(resources.AnchorBorderPen, bounds.X, bounds.Y, bounds.Width, bounds.Height);

            if (anchor.IsSelected)
            {
                // A thick ring in addition to the larger marker keeps the selection readable when the
                // trajectory passes over a busy onion-skin ghost.
                g.DrawEllipse(resources.SelectionPen, bounds.X - 1.5f * scale, bounds.Y - 1.5f * scale, bounds.Width + 3f * scale, bounds.Height + 3f * scale);
            }
        }
    }

    /// <summary>Frame number next to the hovered anchor, clamped so the label never leaves the control.</summary>
    private void DrawMotionTrackHoverLabel(Graphics g, MotionTrackScreenAnchor[] anchors)
    {
        if (MotionTrackHoverFrame < 0) return;
        var hovered = default(MotionTrackScreenAnchor);
        var found = false;
        foreach (var anchor in anchors)
        {
            if (anchor.Frame != MotionTrackHoverFrame) continue;
            hovered = anchor;
            found = true;
            break;
        }

        if (!found) return;

        var text = MotionTrackFormatFrameLabel(hovered.Frame);
        var textSize = TextRenderer.MeasureText(
            g,
            text,
            Font,
            Size.Empty,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        var scale = MotionTrackDpiScale;
        var padding = 5f * scale;
        var gap = 12f * scale;
        var width = textSize.Width + padding * 2f;
        var height = textSize.Height + padding;
        var left = hovered.Screen.X + gap;
        var top = hovered.Screen.Y - height - gap * 0.5f;
        // Keep the label inside the control: flip it to the other side of the anchor before clamping.
        if (left + width > Width - 2f) left = hovered.Screen.X - gap - width;
        if (top < 2f) top = hovered.Screen.Y + gap * 0.5f;
        left = Math.Clamp(left, 2f, Math.Max(2f, Width - width - 2f));
        top = Math.Clamp(top, 2f, Math.Max(2f, Height - height - 2f));
        var bounds = Rectangle.Round(new RectangleF(left, top, width, height));
        var resources = MotionTrackResources();

        g.FillRectangle(resources.LabelBackgroundBrush, bounds);
        g.DrawRectangle(resources.HoverPen, bounds.X, bounds.Y, bounds.Width, bounds.Height);
        TextRenderer.DrawText(
            g,
            text,
            Font,
            bounds,
            MotionTrackHoverInk,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    /// <summary>Dashed box around the selected anchors while the transform affordance is armed.</summary>
    private void DrawMotionTrackTransformBox(Graphics g)
    {
        if (!TryResolveMotionTrackTransformBoxScreen(out var bounds)) return;
        g.DrawRectangle(
            MotionTrackResources().TransformBoxPen,
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height);
    }

    /// <summary>
    /// Screen bounding box of the selected anchors. <paramref name="requireBoxableCount"/> rejects
    /// selections the transform box cannot represent, which is zero frames or more than three.
    /// </summary>
    private bool TryResolveMotionTrackSelectionBounds(out RectangleF bounds, bool requireBoxableCount)
    {
        bounds = RectangleF.Empty;
        if (_motionTrack is not { HasAnchors: true } track) return false;
        if (_motionTrackSelectedFrames.Count == 0) return false;
        if (requireBoxableCount && _motionTrackSelectedFrames.Count > 3) return false;

        var left = float.MaxValue;
        var top = float.MaxValue;
        var right = float.MinValue;
        var bottom = float.MinValue;
        var found = false;
        foreach (var anchor in track.Anchors)
        {
            if (!_motionTrackSelectedFrames.Contains(anchor.Frame)) continue;
            var point = ToMotionTrackScreenPoint(anchor.WorldPosition);
            if (!IsFiniteMotionTrackPoint(point)) continue;
            left = Math.Min(left, point.X);
            top = Math.Min(top, point.Y);
            right = Math.Max(right, point.X);
            bottom = Math.Max(bottom, point.Y);
            found = true;
        }

        if (!found) return false;
        bounds = RectangleF.FromLTRB(left, top, right, bottom);
        if (!requireBoxableCount) return true;
        // Inflate so the box clears the markers themselves instead of clipping them.
        var inset = MotionTrackTransformBoxInsetPixels * MotionTrackDpiScale;
        bounds.Inflate(inset, inset);
        return true;
    }

    /// <summary>Replaces the selection set. Reports whether anything actually changed.</summary>
    private bool ApplyMotionTrackSelection(IReadOnlyCollection<int>? selectedFrames)
    {
        if (selectedFrames is null || selectedFrames.Count == 0)
        {
            if (_motionTrackSelectedFrames.Count == 0) return false;
            _motionTrackSelectedFrames.Clear();
            return true;
        }

        var next = new HashSet<int>(selectedFrames.Count);
        foreach (var frame in selectedFrames)
        {
            if (frame >= 0) next.Add(frame);
        }

        if (next.SetEquals(_motionTrackSelectedFrames)) return false;
        _motionTrackSelectedFrames = next;
        return true;
    }

    /// <summary>Drops selected frames the attached track no longer samples.</summary>
    private bool PruneMotionTrackSelection(DrawingObjectMotionTrack? track)
    {
        if (_motionTrackSelectedFrames.Count == 0) return false;
        var removed = _motionTrackSelectedFrames.RemoveWhere(frame => track?.FindAnchor(frame) is not { IsSelectable: true });
        return removed > 0;
    }

    /// <summary>
    /// Overlay-only refresh. The motion track is transient editor chrome drawn after the scene, so it
    /// never invalidates the cached base presentation the way <c>Invalidate()</c> would.
    /// </summary>
    private void InvalidateMotionTrackOverlay() => InvalidateSelectionState();

    /// <summary>True when the two tracks would draw identically, so an attach can skip the repaint.</summary>
    private static bool MotionTrackEquivalent(DrawingObjectMotionTrack? left, DrawingObjectMotionTrack? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;
        if (left.CenterFrame != right.CenterFrame
            || left.FirstFrame != right.FirstFrame
            || left.LastFrame != right.LastFrame
            || left.Anchors.Count != right.Anchors.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Anchors.Count; index++)
        {
            var a = left.Anchors[index];
            var b = right.Anchors[index];
            if (a.Frame != b.Frame
                || a.WorldPosition != b.WorldPosition
                || a.Kind != b.Kind
                || a.IsAdjusted != b.IsAdjusted
                || a.IsOnTweenSegment != b.IsOnTweenSegment)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The hover label text. Kept in one place so the Direct2D side can match it if it ever draws text.</summary>
    private static string MotionTrackFormatFrameLabel(int frame) => frame.ToString();

    private PointF ToMotionTrackScreenPoint(PointF world) => WorldToScreen(world.X, world.Y);

    private static bool IsFiniteMotionTrackPoint(PointF point) =>
        float.IsFinite(point.X) && float.IsFinite(point.Y);

    private static RectangleF NormalizeMotionTrackRectangle(RectangleF bounds) => RectangleF.FromLTRB(
        Math.Min(bounds.Left, bounds.Right),
        Math.Min(bounds.Top, bounds.Bottom),
        Math.Max(bounds.Left, bounds.Right),
        Math.Max(bounds.Top, bounds.Bottom));

    /// <summary>Halos are drawn for selection, hover, or both; this keeps the condition in one place.</summary>
    private static bool MotionTrackHaloVisible(bool isSelected, bool isHovered) => isSelected || isHovered;

    /// <summary>Returns the cached GDI resource set for the current DPI factor, building it on first use.</summary>
    private MotionTrackGdiResources MotionTrackResources()
    {
        var scale = MotionTrackDpiScale;
        if (MotionTrackResourcesByScale.TryGetValue(scale, out var cached)) return cached;
        var created = new MotionTrackGdiResources(scale);
        MotionTrackResourcesByScale[scale] = created;
        return created;
    }
}

/// <summary>
/// GDI pens and brushes for the motion-track overlay, built once per DPI factor. Every resource is
/// immutable for the lifetime of the set, so one instance can be shared by all Stage instances that
/// report the same DPI factor.
/// </summary>
internal sealed class MotionTrackGdiResources
{
    /// <summary>
    /// Ink for a frame that carries its own placement. Reuses the theme warning colour because the
    /// palette already reserves it for "this differs from the inherited default"; the overlay adds no
    /// magic hues of its own.
    /// </summary>
    private static Color AdjustedInk => Theme.Warning;

    /// <summary>
    /// Ink for anchors and segments inside a classic tween span. The accent hue reads as "interpolated",
    /// distinct from both the adjusted amber and the neutral trajectory.
    /// </summary>
    private static Color TweenInk => Theme.Accent;

    /// <summary>Ink for the trajectory dash: deliberately the quietest colour in the overlay.</summary>
    private static Color PathInk => Theme.Muted;

    /// <summary>Ink for an ordinary anchor marker.</summary>
    private static Color AnchorInk => Theme.Text;

    /// <summary>Trajectory dash between two ordinary anchors.</summary>
    internal Pen PathPen { get; }

    /// <summary>Dash for a segment that lies inside a classic tween span.</summary>
    internal Pen TweenPen { get; }

    /// <summary>Outline that keeps a marker readable over a busy onion-skin ghost.</summary>
    internal Pen AnchorBorderPen { get; }

    /// <summary>Ring that marks a selected anchor.</summary>
    internal Pen SelectionPen { get; }

    /// <summary>Ring and label outline for the hovered anchor.</summary>
    internal Pen HoverPen { get; }

    /// <summary>Dashed box around a multi-selection.</summary>
    internal Pen TransformBoxPen { get; }

    internal SolidBrush PathBrush { get; }

    /// <summary>Fill for a frame that carries its own placement.</summary>
    internal SolidBrush AdjustedBrush { get; }

    /// <summary>Fill for a frame inside a tween span.</summary>
    internal SolidBrush TweenBrush { get; }

    /// <summary>Fill for an ordinary anchor.</summary>
    internal SolidBrush AnchorBrush { get; }

    /// <summary>Fill for blank and held frames: same hue, much lower opacity.</summary>
    internal SolidBrush MutedBrush { get; }

    /// <summary>Fill for the hovered anchor marker.</summary>
    internal SolidBrush HoverBrush { get; }

    /// <summary>Backdrop for the frame-number label, so the text stays legible over the artwork.</summary>
    internal SolidBrush LabelBackgroundBrush { get; }

    internal MotionTrackGdiResources(float dpiScale)
    {
        PathPen = DashedPen(WithAlpha(PathInk, 190), 1.4f, dpiScale);
        TweenPen = DashedPen(WithAlpha(TweenInk, 235), 1.9f, dpiScale);
        AnchorBorderPen = new Pen(WithAlpha(Theme.Stage, 235), 1.2f * dpiScale);
        SelectionPen = new Pen(WithAlpha(StageControl.MotionTrackSelectionInk, 245), 1.8f * dpiScale);
        HoverPen = new Pen(WithAlpha(StageControl.MotionTrackHoverInk, 240), 1.6f * dpiScale);
        TransformBoxPen = DashedPen(WithAlpha(StageControl.MotionTrackSelectionInk, 215), 1.4f, dpiScale);
        PathBrush = new SolidBrush(WithAlpha(PathInk, 200));
        AdjustedBrush = new SolidBrush(AdjustedInk);
        TweenBrush = new SolidBrush(TweenInk);
        AnchorBrush = new SolidBrush(AnchorInk);
        MutedBrush = new SolidBrush(WithAlpha(AnchorInk, 110));
        HoverBrush = new SolidBrush(StageControl.MotionTrackHoverInk);
        LabelBackgroundBrush = new SolidBrush(WithAlpha(Theme.PanelStrong, 214));
    }

    /// <summary>Applies an overlay opacity to a theme colour without introducing a new hue.</summary>
    private static Color WithAlpha(Color color, int alpha) =>
        Color.FromArgb(Math.Clamp(alpha, 0, 255), color);

    private static Pen DashedPen(Color color, float widthPixels, float dpiScale)
    {
        var pen = new Pen(color, widthPixels * dpiScale)
        {
            DashStyle = System.Drawing.Drawing2D.DashStyle.Dash,
            DashCap = System.Drawing.Drawing2D.DashCap.Flat,
            StartCap = System.Drawing.Drawing2D.LineCap.Flat,
            EndCap = System.Drawing.Drawing2D.LineCap.Flat
        };
        return pen;
    }
}

/// <summary>
/// One motion-track anchor resolved to screen space, carrying the flags the overlays need so the
/// Direct2D pass draws from the same geometry the GDI pass and hit-testing use.
/// </summary>
internal readonly record struct MotionTrackScreenAnchor(
    int Frame,
    PointF Screen,
    bool IsAdjusted,
    bool IsMutedInk,
    bool IsOnTweenSegment,
    bool IsSelectable,
    bool IsSelected);
