namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private bool _inspectorGeometryGesture;
    private object? _inspectorGeometryUndo;
    private bool _inspectorGestureWasDirty;
    private float _inspectorGeometryLastRotation;
    private VectorScene? _inspectorGeometryScene;
    private SymbolDeletionHistory? _anchorGestureUndo;
    private bool _anchorGesture;

    private void BeginInspectorGeometryEdit()
    {
        CompleteInspectorGeometryEdit();
        _inspectorGeometryGesture = true;
        _inspectorGeometryScene = _scene;
        _inspectorGeometryLastRotation = 0;
        _inspectorGestureWasDirty = _projectDirty;
    }

    private void CompleteInspectorGeometryEdit()
    {
        if (!_inspectorGeometryGesture && _inspectorGeometryUndo is null) return;
        _inspectorGeometryGesture = false;
        var history = _inspectorGeometryUndo;
        _inspectorGeometryUndo = null;
        if (history is not null) PushHistoryEntry(history);
        UpdateInspector();
    }

    private void CancelInspectorGeometryEdit()
    {
        _inspectorGeometryGesture = false;
        var history = _inspectorGeometryUndo;
        _inspectorGeometryUndo = null;
        if (history is DrawingUndoEntry drawing)
        {
            if (drawing.DrawingObject is { } owner && drawing.InstanceSnapshot is { } instances)
                owner.RestoreInstanceSnapshot(instances);
            (_inspectorGeometryScene ?? _scene).RestoreSnapshot(drawing.Snapshot);
        }
        else if (history is SceneTimelineUndoEntry timeline)
        {
            if (timeline.InstanceSnapshot is { } instances) timeline.Scene.RestoreInstanceSnapshot(instances);
            timeline.Scene.Timeline.RestoreSnapshot(timeline.Snapshot);
            timeline.Scene.SynchronizeTimelineTracks();
        }
        RebuildEditableInstanceComposition();
        RebuildDrawingObjectUnderlay();
        InvalidatePointerTopologyQueries();
        ClearSceneSnapQueryCache();
        SyncSelectionToStage();
        _timeline.RefreshTimeline();
        _hierarchyPanel.RefreshScene();
        UpdateTransformOverlay();
        UpdateInspector();
        _stage.Invalidate();
        SetProjectDirty(_inspectorGestureWasDirty);
    }

    private void BeginAnchorEdit()
    {
        CompleteAnchorEdit();
        _anchorGesture = true;
        _inspectorGestureWasDirty = _projectDirty;
    }

    private void CompleteAnchorEdit()
    {
        _anchorGesture = false;
        var history = _anchorGestureUndo;
        _anchorGestureUndo = null;
        if (history is not null) PushSymbolDeletionHistory(history);
    }

    private void CancelAnchorEdit()
    {
        _anchorGesture = false;
        var history = _anchorGestureUndo;
        _anchorGestureUndo = null;
        if (history is not null) RestoreSymbolDeletionHistory(history);
        SetProjectDirty(_inspectorGestureWasDirty);
    }
}
