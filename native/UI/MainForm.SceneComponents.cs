namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private bool ConvertSelectedSceneInstancesToSpatialComponent()
    {
        if (!IsScene3DView() || !IsSceneCompositionContext()) return false;

        FinishPointerInteractionForFrameChange();
        var scene = ActiveScene();
        var selected = SelectedSceneInstances().ToArray();
        if (scene is null || selected.Length < 2) return false;

        var timelineSnapshot = scene.Timeline.CreateSnapshot();
        var instanceSnapshot = scene.CreateInstanceSnapshot();
        var timelineSelection = _timeline.CaptureSelectionSnapshot();
        if (!_project.TryConvertSceneInstancesToSpatialComponent(
                scene.Id,
                selected.Select(instance => instance.Id).ToArray(),
                _frame,
                out var component,
                out var replacement)
            || component is null
            || replacement is null)
        {
            return false;
        }

        PushSceneTimelineUndo(
            scene,
            timelineSnapshot,
            instanceSnapshot: instanceSnapshot,
            playheadFrame: _frame,
            timelineSelection: timelineSelection,
            createdDrawingObjectId: component.Id);
        _timeline.RefreshTimeline();
        ApplyBoundTimelineDuration();
        InvalidateSceneCompositionCache();
        RebuildSceneComposition();
        SetSceneInstanceSelection(replacement);
        _hierarchyPanel.RefreshScene();
        _sceneEditorPanel.BindProject(_scenes, _activeSceneIndex, _drawingObjects);
        RefreshDrawingObjectAssetPresentation();
        UpdateInspector();
        _stage.Invalidate();
        AppLog.Info($"Converted {selected.Length} scene instances to 3D symbol: {component.Name}");
        return true;
    }
}
