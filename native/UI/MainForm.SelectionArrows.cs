namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    internal static bool IsSelectionArrow(ToolMode tool) => tool is ToolMode.Select or ToolMode.ObjectSelect;
    private bool UsesObjectSelection => IsSelectionArrow(_tool)
        && _applicationSettings.SelectionArrows.BehaviorFor(_tool) == SelectionArrowBehavior.Object;
    private bool UsesSelectionGeometryEditing => IsSelectionArrow(_tool);
    private bool _selectionArrowGeometryFeedback;
    private bool ShowsSelectionGeometry => UsesSelectionGeometryEditing
        && (!UsesObjectSelection || _selectionArrowGeometryFeedback);

    private void RefreshSelectionArrowFeedback()
    {
        if (!UsesSelectionGeometryEditing) _selectionArrowGeometryFeedback = false;
        else if (_activeHandle != EditHandleKind.None || _arcDragSession is not null
            || _cornerDragSession is not null || _lineBranchDragSession is not null
            || _fillEdgeBezierArmedSession is not null || _fillEdgeBezierEditSession is not null
            || _fillEdgeNoUiArc.HasValue)
            _selectionArrowGeometryFeedback = true;
        else if (_selectedObjects.Count == 0) _selectionArrowGeometryFeedback = false;
        _stage.ConfigureSelectionArrows(_applicationSettings.SelectionArrows,
            UsesObjectSelection, _selectionArrowGeometryFeedback);
    }

    private void BeginSelectionArrowGeometryFeedback()
    {
        _selectionArrowGeometryFeedback = true;
        _stage.ConfigureSelectionArrows(_applicationSettings.SelectionArrows,
            UsesObjectSelection, suppressGeometryFeedback: true);
    }

    private bool TryGetObjectArrowMoveTarget(Point screen, out int objectIndex)
    {
        objectIndex = -1;
        if (!UsesObjectSelection || _selectionArrowGeometryFeedback
            || !IsEditableDrawingContext() || IsSceneReferenceView()
            || _selectedObjects.Count == 0) return false;
        var world = _stage.ScreenToWorld(screen);
        var tolerance = SelectionToleranceWorld();
        var hit = HitTestSelectionArrow(_scene, world, _frame, tolerance);
        if (hit.IsValid && _scene.IsObjectSelectable(hit.Key.ObjectIndex, _frame)
            && !_stage.ObjectArrowUsesBounds(hit.Key.ObjectIndex)
            && (_selectedElements.Any(selected => selected.Key == hit.Key)
                || _selectedElements.Count == 0 && _selectedObjects.Contains(hit.Key.ObjectIndex)))
        {
            objectIndex = hit.Key.ObjectIndex;
            return true;
        }
        foreach (var region in _stage.ObjectArrowSelectedFills())
        {
            if (!_scene.IsObjectSelectable(region.ObjectIndex, _frame)
                || SelectionPickingFillDistanceSquared(world, region.Contours) > (double)tolerance * tolerance) continue;
            objectIndex = region.ObjectIndex;
            return true;
        }
        foreach (var region in _stage.ObjectArrowSelectedStrokes())
        {
            if (!_scene.IsObjectSelectable(region.ObjectIndex, _frame)) continue;
            var strokeTolerance = tolerance + _scene.Stroke[region.ObjectIndex] * 0.5f;
            foreach (var contour in region.Contours)
            {
                for (var pointIndex = 0; pointIndex < contour.Length; pointIndex++)
                {
                    var next = Math.Min(pointIndex + 1, contour.Length - 1);
                    if (SelectionPickingSegmentDistanceSquared(world, contour[pointIndex], contour[next])
                        > (double)strokeTolerance * strokeTolerance) continue;
                    objectIndex = region.ObjectIndex;
                    return true;
                }
            }
        }
        return false;
    }

    private bool TryBeginObjectArrowSelectionMove(MouseEventArgs eventArgs)
    {
        if (eventArgs.Button != MouseButtons.Left
            || !TryGetObjectArrowMoveTarget(eventArgs.Location, out var objectIndex)) return false;
        _stage.Capture = true;
        _lastMouse = eventArgs.Location;
        _startScreen = eventArgs.Location;
        _startWorld = _stage.ScreenToWorld(eventArgs.Location);
        _pointerHitWasAlreadySelected = true;
        _selectionWasEmptyOnPointerDown = false;
        _independentMarqueeStrokeMove = false;
        _forceMarqueeOnPointerDown = false;
        _additiveSelection = false;
        _pendingClickSelection = DrawingElementHit.None;
        _activeHandle = EditHandleKind.None;
        _selectedObject = objectIndex;
        _selectedElement = _selectedElements.FirstOrDefault(
            selected => selected.Key.ObjectIndex == objectIndex, DrawingElementHit.None);
        _stage.ClearHoveredLineElement();
        SyncSelectionToStage(deferPresentation: true);
        CaptureEditStart(objectIndex);
        UpdateInteractionCursor(eventArgs.Location);
        return true;
    }

    private void RefreshSelectionArrowPresentation()
    {
        RefreshSelectionArrowFeedback();
        if (!UsesSelectionGeometryEditing) _stage.ClearHoveredLineElement();
        UpdateFillEdgeBezierOverlay();
    }

    private void BeginObjectArrowPointer(MouseEventArgs eventArgs)
    {
        if (_forceMarqueeOnPointerDown || _startWorld is not { } world) return;
        _selectionArrowGeometryFeedback = false;
        RefreshSelectionArrowPresentation();
        var hit = HitTestSelectionArrow(_scene, world, _frame, SelectionToleranceWorld());
        if (!hit.IsValid || !_scene.IsObjectSelectable(hit.Key.ObjectIndex, _frame))
        {
            BeginBlankMarqueeSelection(eventArgs.Location);
            return;
        }
        var objectIndex = hit.Key.ObjectIndex;
        var alreadySelected = _selectedElements.Any(selected => selected.Key == hit.Key)
            || (_selectedElements.Count == 0 && _selectedObjects.Contains(objectIndex));
        _pointerHitWasAlreadySelected = alreadySelected;
        if (!_additiveSelection && IsArrowArcDragHit(hit) && BeginArcDrag(world, objectIndex))
        {
            _stage.ClearHoveredLineElement();
            UpdateInteractionCursor(eventArgs.Location);
            return;
        }
        if (!_additiveSelection && alreadySelected && _selectedElements.Count == 0
            && !IsWholeObjectOnlyObject(objectIndex))
        {
            _pendingClickSelection = hit;
            CaptureEditStart(objectIndex);
            _stage.InvalidateOverlay();
            return;
        }
        if (_additiveSelection)
        {
            if (_selectedObjects.Count == 0) SetSelection(hit, deferPresentation: true);
            else AddToSelection(hit, deferPresentation: true);
        }
        else if (!alreadySelected)
        {
            SetSelection(hit, deferPresentation: true);
        }
        else
        {
            _selectedObject = objectIndex;
            _selectedElement = _selectedElements.FirstOrDefault(selected => selected.Key == hit.Key, DrawingElementHit.None);
            SyncSelectionToStage(deferPresentation: true);
        }
        if (_selectedObjects.Contains(objectIndex)) CaptureEditStart(objectIndex);
        else _startWorld = null;
        _stage.InvalidateOverlay();
        UpdateInteractionCursor(eventArgs.Location);
    }

    private void ResolvePendingArrowDragSelection(DrawingElementHit hit)
    {
        if (!(UsesObjectSelection && _pointerHitWasAlreadySelected && _selectedElements.Count == 0
            && _selectedObjects.Contains(hit.Key.ObjectIndex)))
            SetSelection(hit, deferPresentation: true);
        _pointerHitWasAlreadySelected = true;
        CaptureEditStart(hit.Key.ObjectIndex);
    }

    private bool IsArrowArcDragHit(DrawingElementHit hit) => hit.IsValid
        && hit.Key.Kind == DrawingElementKind.Stroke
        && _scene.IsObjectSelectable(hit.Key.ObjectIndex, _frame)
        && _scene.ShapeKind[hit.Key.ObjectIndex] == ShapeKind.Line
        && !_selectedElements.Any(selected => selected.Key == hit.Key)
        && !(_selectedElements.Count == 0 && _selectedObjects.Contains(hit.Key.ObjectIndex));

    private void ApplySelectionArrowCursor(StageControl.SelectionArrowCursorFeedback feedback)
        => _stage.Cursor = _stage.SelectionArrowCursorFor(_tool == ToolMode.Select, feedback);

    private bool CanInsertSelectionArrowCorner(DrawingElementHit hit, PointF world)
    {
        if (!hit.IsValid || hit.Key.Kind != DrawingElementKind.Stroke
            || !_scene.IsObjectSelectable(hit.Key.ObjectIndex, _frame) || IsScene3DView()) return false;
        var objectIndex = hit.Key.ObjectIndex;
        if (_scene.ShapeKind[objectIndex] == ShapeKind.Line)
            return _scene.TryGetClosestPointOnLine(objectIndex, world, out var parameter, out _, out _)
                && parameter > 0.06f && parameter < 0.94f;
        if (_scene.ShapeKind[objectIndex] == ShapeKind.Freeform && hit.BezierSegmentIndex >= 0
            && _scene.TryGetFreehandBezierSegment(objectIndex, hit.BezierSegmentIndex, out var part))
        {
            var parameter = ClosestCubicParameter(part.Curve, world);
            return parameter > 0.06f && parameter < 0.94f;
        }
        return false;
    }

    private void ApplySelectionArrowHandleCursor(EditHandleKind handle)
    {
        if (handle is EditHandleKind.BoundsTopLeft or EditHandleKind.BoundsTopRight
            or EditHandleKind.BoundsBottomLeft or EditHandleKind.BoundsBottomRight
            or EditHandleKind.TextAreaLeft or EditHandleKind.TextAreaRight)
        {
            _stage.Cursor = CursorForEditHandle(handle);
            return;
        }
        ApplySelectionArrowCursor(handle switch
        {
            EditHandleKind.LineStart or EditHandleKind.LineEnd => StageControl.SelectionArrowCursorFeedback.Node,
            EditHandleKind.BezierControl or EditHandleKind.BezierControl2 => StageControl.SelectionArrowCursorFeedback.Bend,
            _ => StageControl.SelectionArrowCursorFeedback.None
        });
    }
}
