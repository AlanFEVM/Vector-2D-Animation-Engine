using System.Drawing;
using System.Drawing.Drawing2D;

namespace VectorAnimationEngine;

internal sealed partial class StageControl : Control
{
    private readonly List<Point> _lassoPreviewPointBuffer = new(256);
    private readonly GraphicsPath _lassoPreviewGdiPath = new();
    private IReadOnlyList<Point>? _lassoPreviewReadOnlyPoints;
    private int _lassoPreviewStablePointCount;
    private long _lassoPreviewRevision;

    public bool LassoPreviewVisible { get; private set; }
    public IReadOnlyList<Point> LassoPreviewPoints =>
        _lassoPreviewReadOnlyPoints ??= _lassoPreviewPointBuffer.AsReadOnly();
    public bool LassoPreviewClosed { get; private set; }
    internal long LassoPreviewRevision => _lassoPreviewRevision;

    public void SetLassoPreview(IReadOnlyList<Point> points, bool closed)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 2)
        {
            ClearLassoPreview();
            return;
        }

        if (LassoPreviewVisible
            && LassoPreviewClosed == closed
            && SameLassoPoints(points))
        {
            return;
        }

        _lassoPreviewPointBuffer.Clear();
        for (var index = 0; index < points.Count; index++) _lassoPreviewPointBuffer.Add(points[index]);
        _lassoPreviewStablePointCount = points.Count;
        LassoPreviewVisible = true;
        LassoPreviewClosed = closed;
        UpdateLassoPreviewGeometry();
        InvalidateOverlay();
    }

    internal void SetIncrementalLassoPreview(
        IReadOnlyList<Point> stablePoints,
        Point? cursor,
        bool closed)
    {
        ArgumentNullException.ThrowIfNull(stablePoints);
        if (stablePoints.Count == 0)
        {
            ClearLassoPreview();
            return;
        }

        var changed = false;
        if (_lassoPreviewStablePointCount > stablePoints.Count
            || _lassoPreviewStablePointCount > _lassoPreviewPointBuffer.Count
            || _lassoPreviewStablePointCount > 0
                && (_lassoPreviewPointBuffer[0] != stablePoints[0]
                    || _lassoPreviewPointBuffer[_lassoPreviewStablePointCount - 1]
                        != stablePoints[_lassoPreviewStablePointCount - 1]))
        {
            _lassoPreviewPointBuffer.Clear();
            _lassoPreviewStablePointCount = 0;
            changed = true;
        }

        if (_lassoPreviewPointBuffer.Count > _lassoPreviewStablePointCount)
        {
            _lassoPreviewPointBuffer.RemoveRange(
                _lassoPreviewStablePointCount,
                _lassoPreviewPointBuffer.Count - _lassoPreviewStablePointCount);
            changed = true;
        }

        for (var index = _lassoPreviewStablePointCount; index < stablePoints.Count; index++)
        {
            _lassoPreviewPointBuffer.Add(stablePoints[index]);
            changed = true;
        }
        _lassoPreviewStablePointCount = stablePoints.Count;

        if (cursor is { } cursorPoint
            && (_lassoPreviewPointBuffer.Count == 0 || _lassoPreviewPointBuffer[^1] != cursorPoint))
        {
            _lassoPreviewPointBuffer.Add(cursorPoint);
            changed = true;
        }

        var visible = _lassoPreviewPointBuffer.Count >= 2;
        var nextClosed = visible && closed && _lassoPreviewPointBuffer.Count >= 3;
        if (LassoPreviewVisible != visible || LassoPreviewClosed != nextClosed)
        {
            LassoPreviewVisible = visible;
            LassoPreviewClosed = nextClosed;
            changed = true;
        }
        if (changed)
        {
            UpdateLassoPreviewGeometry();
            InvalidateOverlay();
        }
    }

    public void ClearLassoPreview() => ClearLassoPreviewCore(invalidate: true);

    internal void ClearLassoPreviewForLifecycle() => ClearLassoPreviewCore(invalidate: false);

    private bool ClearLassoPreviewCore(bool invalidate)
    {
        if (!LassoPreviewVisible && _lassoPreviewPointBuffer.Count == 0 && !LassoPreviewClosed)
        {
            return false;
        }

        _lassoPreviewPointBuffer.Clear();
        _lassoPreviewStablePointCount = 0;
        LassoPreviewVisible = false;
        LassoPreviewClosed = false;
        UpdateLassoPreviewGeometry();
        if (invalidate) InvalidateOverlay();
        return true;
    }

    private bool SameLassoPoints(IReadOnlyList<Point> points)
    {
        if (_lassoPreviewPointBuffer.Count != points.Count) return false;
        for (var index = 0; index < _lassoPreviewPointBuffer.Count; index++)
        {
            if (_lassoPreviewPointBuffer[index] != points[index]) return false;
        }

        return true;
    }

    private void UpdateLassoPreviewGeometry()
    {
        unchecked
        {
            _lassoPreviewRevision++;
        }
        _lassoPreviewGdiPath.Reset();
        for (var index = 1; index < _lassoPreviewPointBuffer.Count; index++)
        {
            _lassoPreviewGdiPath.AddLine(
                _lassoPreviewPointBuffer[index - 1],
                _lassoPreviewPointBuffer[index]);
        }
        if (LassoPreviewClosed && _lassoPreviewPointBuffer.Count >= 3)
        {
            _lassoPreviewGdiPath.CloseFigure();
        }
    }

    private void DrawLassoPreview(Graphics graphics)
    {
        if (!LassoPreviewVisible || _lassoPreviewPointBuffer.Count < 2) return;

        var oldSmoothingMode = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        try
        {
            var points = _lassoPreviewPointBuffer;
            var closed = LassoPreviewClosed && points.Count >= 3;
            if (closed)
            {
                graphics.FillPath(_marqueeBrush, _lassoPreviewGdiPath);
            }
            graphics.DrawPath(_guidePen, _lassoPreviewGdiPath);

            // A small ring marks the first vertex without obscuring the artwork.
            const float radius = 4f;
            var first = points[0];
            graphics.DrawEllipse(
                _guidePen,
                first.X - radius,
                first.Y - radius,
                radius * 2,
                radius * 2);
        }
        finally
        {
            graphics.SmoothingMode = oldSmoothingMode;
        }
    }
}
