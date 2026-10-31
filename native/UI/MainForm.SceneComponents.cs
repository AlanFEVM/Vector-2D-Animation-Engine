namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private bool ConvertStageSelectionToSymbol()
    {
        if (!CanConvertStageSelectionToSymbol()) return false;
        return SelectedSceneInstances().Count > 0
            ? ConvertSelectedSceneInstancesToSymbol()
            : ConvertSelectedDrawingObjectsToSymbol();
    }

    private bool CanConvertStageSelectionToSymbol()
    {
        if (_playing || IsShotDirectorContext()) return false;
        var instances = SelectedSceneInstances();
        if (instances.Count > 0)
        {
            var scene = ActiveScene();
            if (!IsSceneCompositionContext() || scene is null || _selectedObjects.Count > 0) return false;
            var layerId = instances[0].SceneLayerId;
            if (scene.FindLayer(layerId)?.Kind != SceneLayerKind.Content
                || !scene.Timeline.EvaluateTargetExposure(layerId, _frame).HasContent
                || instances.Any(instance => instance.SceneLayerId != layerId)) return false;
            var drawingLayer = Array.IndexOf(scene.DrawingScene.LayerIds, layerId);
            if (drawingLayer >= 0 && scene.DrawingScene.IsLayerEffectivelyLocked(drawingLayer)) return false;
            var selectedIds = instances.Select(instance => instance.Id).ToHashSet(StringComparer.Ordinal);
            var indices = scene.InstancesInLayer(layerId)
                .Select((instance, index) => (instance, index))
                .Where(item => selectedIds.Contains(item.instance.Id))
                .Select(item => item.index)
                .ToArray();
            return indices.Length == instances.Count
                && indices[^1] - indices[0] + 1 == indices.Length;
        }

        if ((!IsDrawingWorkspaceContext && !IsSceneDrawingContext()) || DrawingToolsBlocked()) return false;
        var targets = SelectedActiveDrawingObjectIndices();
        if (targets.Length == 0) return false;
        var layer = _scene.ObjectLayer[targets[0]];
        return _scene.GetLayerKind(layer) == DrawingLayerKind.Drawing
            && !_scene.IsLayerEffectivelyLocked(layer)
            && targets.All(index => _scene.ObjectLayer[index] == layer);
    }

    private bool SelectSceneContextInstance(Point screen)
    {
        if (!IsSceneCompositionContext()) return false;
        DrawingObjectInstanceDefinition instance;
        var hit = IsScene3DView()
            ? TryResolveProjectedSceneInstance(screen, out instance)
            : TryResolveSceneTransformInstance(screen, _stage.ScreenToWorld(screen), out instance);
        if (!hit) return false;
        if (!_selectedSceneInstanceIds.Contains(instance.Id))
        {
            SetSceneInstanceSelection(instance);
            UpdateInspector();
        }
        return true;
    }

    private bool ConvertSelectedSceneInstancesToSymbol()
    {
        if (!IsSceneCompositionContext() || _playing || IsShotDirectorContext()) return false;

        FinishPointerInteractionForFrameChange();
        var scene = ActiveScene();
        var selected = SelectedSceneInstances().ToArray();
        if (scene is null || selected.Length == 0) return false;

        var timelineSnapshot = scene.Timeline.CreateSnapshot();
        var instanceSnapshot = scene.CreateInstanceSnapshot();
        var timelineSelection = _timeline.CaptureSelectionSnapshot();
        if (!_project.TryConvertSceneInstancesToSymbol(
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
        AppLog.Info($"Converted {selected.Length} scene instances to symbol: {component.Name}");
        return true;
    }
}
