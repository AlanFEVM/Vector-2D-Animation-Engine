namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private bool _canvasRotationHeld;
    private Point? _canvasRotationPointer;
    private float _canvasRotationStart;

    private void CompleteRotatedStageMarquee(Point start, Point end)
    {
        var corners = new[] { start, new Point(end.X, start.Y), end, new Point(start.X, end.Y) };
        if (_subtractMarquee)
        {
            var polygon = corners.Select(_stage.ScreenToWorld).ToArray();
            var objects = _scene.QueryDrawingElementsInsidePolygon(polygon, _frame)
                .Select(hit => hit.Key.ObjectIndex).ToHashSet();
            var instanceIds = FindLassoInstancesInside(polygon).Select(instance => instance.Id).ToHashSet(StringComparer.Ordinal);
            var baseIds = _marqueeInstanceSelectionBase.ToHashSet(StringComparer.Ordinal);
            var instances = ActiveEditableInstances().Where(instance => baseIds.Contains(instance.Id)
                && _intersectMarquee == instanceIds.Contains(instance.Id)).ToArray();
            SetMixedSelection(_marqueeSelectionBase.Where(index => _intersectMarquee == objects.Contains(index)),
                instances, instances.LastOrDefault());
        }
        else
        {
            // A rotated screen rectangle is a world-space polygon. Reuse lasso
            // materialization so partial curves and fills retain the same topology rules.
            _lassoPointerActive = true;
            _lassoAdditiveSelection = _additiveSelection;
            _lassoSelectionBase = _marqueeSelectionBase;
            _lassoElementSelectionBase = _selectedElements.ToArray();
            _lassoInstanceSelectionBase = _marqueeInstanceSelectionBase;
            _lassoScreenPoints.Clear();
            _lassoScreenPoints.AddRange(corners);
            CommitLassoPointer();
        }
        _marqueeSelecting = false;
        _marqueeStart = null;
        _stage.ClearMarquee();
        UpdateInspector();
        _stage.Invalidate();
    }

    private bool HoldCanvasRotation()
    {
        if (!_stage.ContainsFocus || ContainsFocusedEditor(this) || IsScene3DView() || IsShotDirectorContext()
            || HasActiveCanvasPointerInteraction()) return false;
        _canvasRotationHeld = true;
        _stage.Cursor = Cursors.SizeAll;
        return true;
    }

    private bool BeginCanvasRotationPointer(MouseEventArgs e)
    {
        if (!_canvasRotationHeld || e.Button != MouseButtons.Left) return false;
        if (e.Clicks >= 2) { _stage.SetCanvasRotation(0); return true; }
        _canvasRotationPointer = e.Location;
        _canvasRotationStart = _stage.CanvasRotation;
        _stage.Capture = true;
        return true;
    }

    private bool UpdateCanvasRotationPointer(MouseEventArgs e)
    {
        if (_canvasRotationPointer is not { } start) return false;
        var center = new PointF(_stage.Width * 0.5f, _stage.Height * 0.5f);
        var first = MathF.Atan2(start.Y - center.Y, start.X - center.X);
        var current = MathF.Atan2(e.Y - center.Y, e.X - center.X);
        var rotation = _canvasRotationStart + current - first;
        if (IsControlPressed()) rotation = MathF.Round(rotation / (MathF.PI / 12)) * (MathF.PI / 12);
        _stage.SetCanvasRotation(rotation);
        return true;
    }

    private void EndCanvasRotation(bool cancel = false)
    {
        if (!_canvasRotationHeld && _canvasRotationPointer is null) return;
        _canvasRotationHeld = false;
        if (_canvasRotationPointer is not null)
        {
            if (cancel) _stage.SetCanvasRotation(_canvasRotationStart);
            _canvasRotationPointer = null;
            if (_stage.Capture) _stage.Capture = false;
        }
        RefreshInteractionCursorAtPointer();
    }
}
