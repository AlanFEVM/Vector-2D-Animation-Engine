namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private bool EditStageSelection(Action<int[]>? geometry, Action<DrawingObjectInstanceDefinition, int>? instanceEdit,
        bool rebuildTopology = true)
    {
        if (_playing || IsShotDirectorContext() || HasActiveCanvasPointerInteraction()) return false;
        var instances = SelectedSceneInstances().ToArray();
        var hasGeometry = geometry is not null && !IsSceneCompositionContext() && SelectedActiveDrawingObjectIndices().Length > 0;
        if (!hasGeometry && instances.Length == 0) return false;
        if (hasGeometry && DrawingToolsBlocked()) return false;
        var owner = ActiveDrawingObject();
        var scene = ActiveScene();
        var instanceSnapshot = IsSceneCompositionContext() ? scene?.CreateInstanceSnapshot() : owner?.CreateInstanceSnapshot();
        var timelineSnapshot = IsSceneCompositionContext() ? scene?.Timeline.CreateSnapshot() : null;
        var snapshot = !IsSceneCompositionContext()
            ? hasGeometry ? CreateCanvasMutationSnapshot() : _scene.CreateSnapshot() : null;
        int[] targets = [];
        if (hasGeometry && snapshot is not null && !TryPrepareSelectedDrawingObjectsForCommand(snapshot, out targets)) return false;
        if (targets.Any(i => !_scene.IsObjectSelectable(i, _frame)))
        { if (snapshot is not null) RestoreCanvasMutationSnapshot(snapshot); return false; }
        try
        {
            geometry?.Invoke(targets);
            ClearInstanceTimelineEditTracking();
            if (instanceEdit is not null)
                foreach (var instance in instances) instanceEdit(instance, PrepareInstanceStateTimelineEdit(instance));
            if (hasGeometry)
            {
                _scene.CompleteDeferredBuild();
                SetSelection(targets);
                if (rebuildTopology)
                {
                    ApplySelectedFillOverwriteAfterGeometryEdit();
                    MergeSelectedFillsAfterGeometryEdit(connectNearby: true);
                    MergeCompatibleLinesAfterDrawingOperation(LocalLineMergeScope(_selectedObjects));
                    MaterializeEditedStrokeIntersections(_selectedObjects.ToArray());
                }
            }
            RefreshEditedInstanceTimelineTweens();
            ClearInstanceTimelineEditTracking();
            _sceneInstanceTimelineDirty = false;
            if (IsSceneCompositionContext() && scene is not null && timelineSnapshot is not null)
                PushSceneTimelineUndo(scene, timelineSnapshot, instanceSnapshot: instanceSnapshot);
            else if (snapshot is not null) PushUndoSnapshot(snapshot, owner, instanceSnapshot);
        }
        catch
        {
            if (snapshot is not null) RestoreCanvasMutationSnapshot(snapshot);
            if (instanceSnapshot is not null)
            {
                if (IsSceneCompositionContext()) scene?.RestoreInstanceSnapshot(instanceSnapshot);
                else owner?.RestoreInstanceSnapshot(instanceSnapshot);
            }
            if (timelineSnapshot is not null) scene?.Timeline.RestoreSnapshot(timelineSnapshot);
            ClearInstanceTimelineEditTracking();
            RebuildEditableInstanceComposition();
            throw;
        }
        var retainedGeometry = !IsSceneCompositionContext() ? _selectedObjects.ToArray() : [];
        RebuildEditableInstanceComposition();
        SetMixedSelection(retainedGeometry, instances, instances.LastOrDefault());
        _hierarchyPanel.RefreshScene();
        _timeline.RefreshTimeline();
        UpdateInspector();
        ResetSmartSnapping();
        _stage.Invalidate();
        return true;
    }
}
