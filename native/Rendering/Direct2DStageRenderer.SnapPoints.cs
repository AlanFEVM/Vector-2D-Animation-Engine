using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using GdiColor = System.Drawing.Color;

namespace VectorAnimationEngine;

internal sealed partial class Direct2DStageRenderer
{
    private void DrawSnapPointOverlay(StageControl stage)
    {
        if (stage.SnapPointOverlayPoints.Count == 0
            && stage.SnapPointOverlayCandidate is null
            && stage.SceneSnapIndicatorTarget is null)
        {
            return;
        }
        var outline = BrushFor(GdiColor.FromArgb(245, 10, 24, 30).ToArgb());
        var normal = BrushFor(GdiColor.FromArgb(235, 44, 210, 194).ToArgb());
        var hovered = BrushFor(GdiColor.FromArgb(255, 255, 216, 92).ToArgb());
        var active = BrushFor(GdiColor.FromArgb(255, 255, 126, 74).ToArgb());
        var cross = BrushFor(GdiColor.FromArgb(240, 246, 252, 255).ToArgb());
        foreach (var entry in stage.SnapPointOverlayPoints)
        {
            var screen = WorldToVector(stage, entry.Point);
            var isActive = string.Equals(entry.Id, stage.SnapPointOverlayActiveId, StringComparison.Ordinal);
            var isHovered = string.Equals(entry.Id, stage.SnapPointOverlayHoveredId, StringComparison.Ordinal);
            var radius = isActive || isHovered ? 6.5f : 5f;
            var ellipse = new Ellipse(screen, radius, radius);
            _target!.FillEllipse(ellipse, isActive ? active : isHovered ? hovered : normal);
            _target.DrawEllipse(ellipse, outline, 1.5f);
            _target.DrawLine(new Vector2(screen.X - 2.5f, screen.Y), new Vector2(screen.X + 2.5f, screen.Y), cross, 1.2f);
            _target.DrawLine(new Vector2(screen.X, screen.Y - 2.5f), new Vector2(screen.X, screen.Y + 2.5f), cross, 1.2f);
        }

        if (stage.SnapPointOverlayCandidate is { } candidate)
        {
            var point = WorldToVector(stage, candidate);
            var candidateBrush = BrushFor((stage.SnapPointOverlayCandidateSnapped
                ? GdiColor.FromArgb(245, 255, 216, 92)
                : GdiColor.FromArgb(210, 180, 205, 214)).ToArgb());
            var top = new Vector2(point.X, point.Y - 6);
            var right = new Vector2(point.X + 6, point.Y);
            var bottom = new Vector2(point.X, point.Y + 6);
            var left = new Vector2(point.X - 6, point.Y);
            var width = stage.SnapPointOverlayCandidateSnapped ? 1.8f : 1.2f;
            _target!.DrawLine(top, right, candidateBrush, width);
            _target.DrawLine(right, bottom, candidateBrush, width);
            _target.DrawLine(bottom, left, candidateBrush, width);
            _target.DrawLine(left, top, candidateBrush, width);
        }
        DrawSceneSnapIndicator(stage);
    }

    private void DrawSceneSnapIndicator(StageControl stage)
    {
        if (stage.SceneSnapIndicatorSource is not { } source
            || stage.SceneSnapIndicatorTarget is not { } target
            || !TryProjectSceneSnapPoint(stage, source, out var sourceScreen)
            || !TryProjectSceneSnapPoint(stage, target, out var targetScreen))
        {
            return;
        }
        var guide = BrushFor(GdiColor.FromArgb(220, 255, 216, 92).ToArgb());
        var ring = BrushFor(GdiColor.FromArgb(255, 255, 235, 150).ToArgb());
        _target!.DrawLine(sourceScreen, targetScreen, guide, 1.4f);
        _target.DrawEllipse(new Ellipse(targetScreen, 7, 7), ring, 2f);
        _target.DrawLine(new Vector2(targetScreen.X - 10, targetScreen.Y), new Vector2(targetScreen.X + 10, targetScreen.Y), ring, 2f);
        _target.DrawLine(new Vector2(targetScreen.X, targetScreen.Y - 10), new Vector2(targetScreen.X, targetScreen.Y + 10), ring, 2f);
    }

    private static bool TryProjectSceneSnapPoint(StageControl stage, Vector3 point, out Vector2 screen)
    {
        PointF projected;
        if (stage.UsesReferenceProjection)
        {
            if (!stage.TryProjectScenePosition(point, out projected, out _))
            {
                screen = default;
                return false;
            }
        }
        else
        {
            projected = stage.WorldToScreen(point.X, point.Y);
        }
        screen = new Vector2(projected.X, projected.Y);
        return float.IsFinite(screen.X) && float.IsFinite(screen.Y);
    }
}
