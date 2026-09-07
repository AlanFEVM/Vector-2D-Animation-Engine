using System.Drawing.Drawing2D;
using System.Numerics;

namespace VectorAnimationEngine;

internal readonly record struct SnapPointOverlayEntry(string Id, PointF Point);

internal sealed partial class StageControl
{
    private SnapPointOverlayEntry[] _snapPointOverlayPoints = [];

    internal IReadOnlyList<SnapPointOverlayEntry> SnapPointOverlayPoints => _snapPointOverlayPoints;
    internal string SnapPointOverlayHoveredId { get; private set; } = "";
    internal string SnapPointOverlayActiveId { get; private set; } = "";
    internal PointF? SnapPointOverlayCandidate { get; private set; }
    internal bool SnapPointOverlayCandidateSnapped { get; private set; }
    internal Vector3? SceneSnapIndicatorSource { get; private set; }
    internal Vector3? SceneSnapIndicatorTarget { get; private set; }

    internal void SetSnapPointOverlay(
        IEnumerable<SnapPointOverlayEntry> points,
        string? hoveredId = null,
        string? activeId = null,
        PointF? candidate = null,
        bool candidateSnapped = false)
    {
        ArgumentNullException.ThrowIfNull(points);
        var next = points.ToArray();
        hoveredId ??= "";
        activeId ??= "";
        candidate = candidate is { } point ? VectorUnits.Quantize(point) : null;
        if (_snapPointOverlayPoints.SequenceEqual(next)
            && string.Equals(SnapPointOverlayHoveredId, hoveredId, StringComparison.Ordinal)
            && string.Equals(SnapPointOverlayActiveId, activeId, StringComparison.Ordinal)
            && SnapPointOverlayCandidate == candidate
            && SnapPointOverlayCandidateSnapped == candidateSnapped)
        {
            return;
        }

        _snapPointOverlayPoints = next;
        SnapPointOverlayHoveredId = hoveredId;
        SnapPointOverlayActiveId = activeId;
        SnapPointOverlayCandidate = candidate;
        SnapPointOverlayCandidateSnapped = candidateSnapped;
        InvalidateOverlay();
    }

    internal void ClearSnapPointOverlay()
    {
        if (_snapPointOverlayPoints.Length == 0 && SnapPointOverlayCandidate is null) return;
        _snapPointOverlayPoints = [];
        SnapPointOverlayHoveredId = "";
        SnapPointOverlayActiveId = "";
        SnapPointOverlayCandidate = null;
        SnapPointOverlayCandidateSnapped = false;
        InvalidateOverlay();
    }

    internal void SetSceneSnapIndicator(Vector3 source, Vector3 target)
    {
        if (!FiniteSnapVector(source) || !FiniteSnapVector(target))
        {
            ClearSceneSnapIndicator();
            return;
        }
        if (SceneSnapIndicatorSource == source && SceneSnapIndicatorTarget == target) return;
        SceneSnapIndicatorSource = source;
        SceneSnapIndicatorTarget = target;
        InvalidateOverlay();
    }

    internal void ClearSceneSnapIndicator()
    {
        if (SceneSnapIndicatorSource is null && SceneSnapIndicatorTarget is null) return;
        SceneSnapIndicatorSource = null;
        SceneSnapIndicatorTarget = null;
        InvalidateOverlay();
    }

    private void DrawSnapPointOverlay(Graphics graphics)
    {
        if (_snapPointOverlayPoints.Length == 0
            && SnapPointOverlayCandidate is null
            && SceneSnapIndicatorTarget is null)
        {
            return;
        }
        var oldMode = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var outline = new Pen(Color.FromArgb(245, 10, 24, 30), 1.5f);
        using var normal = new SolidBrush(Color.FromArgb(235, 44, 210, 194));
        using var hovered = new SolidBrush(Color.FromArgb(255, 255, 216, 92));
        using var active = new SolidBrush(Color.FromArgb(255, 255, 126, 74));
        using var cross = new Pen(Color.FromArgb(240, 246, 252, 255), 1.2f);
        foreach (var entry in _snapPointOverlayPoints)
        {
            var screen = WorldToScreen(entry.Point);
            var isActive = string.Equals(entry.Id, SnapPointOverlayActiveId, StringComparison.Ordinal);
            var isHovered = string.Equals(entry.Id, SnapPointOverlayHoveredId, StringComparison.Ordinal);
            var radius = isActive || isHovered ? 6.5f : 5f;
            graphics.FillEllipse(isActive ? active : isHovered ? hovered : normal,
                screen.X - radius,
                screen.Y - radius,
                radius * 2,
                radius * 2);
            graphics.DrawEllipse(outline, screen.X - radius, screen.Y - radius, radius * 2, radius * 2);
            graphics.DrawLine(cross, screen.X - 2.5f, screen.Y, screen.X + 2.5f, screen.Y);
            graphics.DrawLine(cross, screen.X, screen.Y - 2.5f, screen.X, screen.Y + 2.5f);
        }

        if (SnapPointOverlayCandidate is { } candidate)
        {
            var screen = WorldToScreen(candidate);
            var color = SnapPointOverlayCandidateSnapped
                ? Color.FromArgb(245, 255, 216, 92)
                : Color.FromArgb(210, 180, 205, 214);
            using var candidatePen = new Pen(color, SnapPointOverlayCandidateSnapped ? 1.8f : 1.2f)
            {
                DashStyle = SnapPointOverlayCandidateSnapped ? DashStyle.Solid : DashStyle.Dash
            };
            var diamond = new[]
            {
                new PointF(screen.X, screen.Y - 6),
                new PointF(screen.X + 6, screen.Y),
                new PointF(screen.X, screen.Y + 6),
                new PointF(screen.X - 6, screen.Y)
            };
            graphics.DrawPolygon(candidatePen, diamond);
        }
        DrawSceneSnapIndicator(graphics);
        graphics.SmoothingMode = oldMode;
    }

    private void DrawSceneSnapIndicator(Graphics graphics)
    {
        if (SceneSnapIndicatorSource is not { } source || SceneSnapIndicatorTarget is not { } target) return;
        if (!TryProjectSnapVector(source, out var sourceScreen)
            || !TryProjectSnapVector(target, out var targetScreen))
        {
            return;
        }
        using var guide = new Pen(Color.FromArgb(220, 255, 216, 92), 1.4f) { DashStyle = DashStyle.Dash };
        using var ring = new Pen(Color.FromArgb(255, 255, 235, 150), 2f);
        graphics.DrawLine(guide, sourceScreen, targetScreen);
        graphics.DrawEllipse(ring, targetScreen.X - 7, targetScreen.Y - 7, 14, 14);
        graphics.DrawLine(ring, targetScreen.X - 10, targetScreen.Y, targetScreen.X + 10, targetScreen.Y);
        graphics.DrawLine(ring, targetScreen.X, targetScreen.Y - 10, targetScreen.X, targetScreen.Y + 10);
    }

    private bool TryProjectSnapVector(Vector3 point, out PointF screen)
    {
        if (UsesReferenceProjection) return TryProjectScenePosition(point, out screen, out _);
        screen = WorldToScreen(point.X, point.Y);
        return float.IsFinite(screen.X) && float.IsFinite(screen.Y);
    }

    private static bool FiniteSnapVector(Vector3 point) =>
        float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);
}
