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
        var hit = _scene.HitTestElement(world, _frame, SelectionToleranceWorld());
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
}
