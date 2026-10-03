using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using GdiColor = System.Drawing.Color;

namespace VectorAnimationEngine;

/// <summary>
/// Direct2D half of the timeline motion-track overlay. The geometry is resolved once by
/// <c>StageControl.MotionTrack.cs</c> and handed over as screen space anchors, so this partial only
/// reproduces the GDI ink and dash rules and never re-derives camera or selection state.
/// </summary>
internal sealed partial class Direct2DStageRenderer
{
    /// <summary>Anchor marker extent in device-independent pixels, matching the GDI overlay.</summary>
    private const float MotionTrackAnchorSizePixels = 9f;

    /// <summary>Hover/selection ring extent in device-independent pixels.</summary>
    private const float MotionTrackHaloSizePixels = 16f;

    /// <summary>
    /// Draws the motion-track overlay for the attached track. Returns immediately when the overlay is
    /// off, and draws nothing when no anchor resolves to a finite screen position.
    /// </summary>
    /// <remarks>
    /// The hover frame number is intentionally not drawn here. The GDI overlay renders it with
    /// <c>TextRenderer</c>; this renderer has no DirectWrite factory, and adding one purely for a label
    /// would introduce a text stack the rest of the Direct2D pipeline does not use. The marker and ring
    /// geometry is identical between the backends, so only the tooltip is GDI-only.
    /// </remarks>
    internal void DrawMotionTrack(StageControl stage)
    {
        if (!stage.MotionTrackVisible) return;
        var anchors = stage.ResolveMotionTrackScreenAnchors();
        if (anchors.Length == 0) return;

        var dpiScale = stage.SpatialGizmoDpiScale;
        var dashed = PreviewBoundsStrokeStyle();
        DrawMotionTrackTransformBox(stage, dpiScale, dashed);
        DrawMotionTrackSegments(anchors, dpiScale, dashed);
        DrawMotionTrackAnchors(stage, anchors, dpiScale);
    }

    /// <summary>Dashed trajectory between consecutive anchors, coloured per tween membership.</summary>
    private void DrawMotionTrackSegments(MotionTrackScreenAnchor[] anchors, float dpiScale, ID2D1StrokeStyle dashed)
    {
        for (var index = 1; index < anchors.Length; index++)
        {
            var start = anchors[index - 1];
            var end = anchors[index];
            // Mirrors the GDI rule: either endpoint inside a tween span makes the whole span tween ink.
            var isTweenSegment = start.IsOnTweenSegment || end.IsOnTweenSegment;
            var brush = isTweenSegment
                ? BrushFor(GdiColor.FromArgb(235, Theme.Accent).ToArgb())
                : BrushFor(GdiColor.FromArgb(190, Theme.Muted).ToArgb());
            _target!.DrawLine(
                new Vector2(start.Screen.X, start.Screen.Y),
                new Vector2(end.Screen.X, end.Screen.Y),
                brush,
                (isTweenSegment ? 1.9f : 1.4f) * dpiScale,
                dashed);
        }
    }

    /// <summary>Anchor markers with their classification ink, plus hover and selection rings.</summary>
    private void DrawMotionTrackAnchors(
        StageControl stage,
        MotionTrackScreenAnchor[] anchors,
        float dpiScale)
    {
        var anchorSize = MotionTrackAnchorSizePixels * dpiScale;
        var haloSize = MotionTrackHaloSizePixels * dpiScale;
        var border = BrushFor(GdiColor.FromArgb(235, Theme.Stage).ToArgb());
        var selection = BrushFor(GdiColor.FromArgb(245, StageControl.MotionTrackSelectionInk).ToArgb());
        var hover = BrushFor(GdiColor.FromArgb(240, StageControl.MotionTrackHoverInk).ToArgb());
        var adjusted = BrushFor(Theme.Warning.ToArgb());
        var tween = BrushFor(Theme.Accent.ToArgb());
        var ordinary = BrushFor(Theme.Text.ToArgb());
        var muted = BrushFor(GdiColor.FromArgb(110, Theme.Text).ToArgb());
        var hoverFrame = stage.MotionTrackHoverFrame;

        foreach (var anchor in anchors)
        {
            var center = new Vector2(anchor.Screen.X, anchor.Screen.Y);
            var isHovered = anchor.Frame == hoverFrame;

            if (anchor.IsSelected)
            {
                _target!.DrawEllipse(new Ellipse(center, haloSize * 0.5f, haloSize * 0.5f), selection, 1.8f * dpiScale);
            }

            if (isHovered)
            {
                _target!.DrawEllipse(new Ellipse(center, haloSize * 0.5f, haloSize * 0.5f), hover, 1.6f * dpiScale);
            }

            // Selection grows the marker, matching the GDI overlay so the two backends agree on size.
            var size = anchor.IsSelected ? anchorSize * 1.15f : anchorSize;
            var half = size * 0.5f;
            var fill = anchor.IsAdjusted
                ? adjusted
                : anchor.IsOnTweenSegment
                    ? tween
                    : anchor.IsMutedInk
                        ? muted
                        : ordinary;
            var rect = Rect(center.X - half, center.Y - half, size, size);
            _target!.FillRectangle(in rect, fill);
            _target.DrawRectangle(in rect, border, 1.2f * dpiScale);

            if (anchor.IsSelected)
            {
                _target.DrawRectangle(
                    Rect(center.X - half - 1.5f * dpiScale, center.Y - half - 1.5f * dpiScale, size + 3f * dpiScale, size + 3f * dpiScale),
                    selection,
                    1.8f * dpiScale);
            }
        }
    }

    /// <summary>Dashed box around the multi-selection while the transform affordance is armed.</summary>
    private void DrawMotionTrackTransformBox(StageControl stage, float dpiScale, ID2D1StrokeStyle dashed)
    {
        if (!stage.TryResolveMotionTrackTransformBoxScreen(out var bounds)) return;
        var rect = Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        _target!.DrawRectangle(
            rect,
            BrushFor(GdiColor.FromArgb(215, StageControl.MotionTrackSelectionInk).ToArgb()),
            1.4f * dpiScale,
            dashed);
    }
}
