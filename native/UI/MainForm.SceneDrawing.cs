namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private void EnsureSceneDrawingSurface()
    {
        if (!IsSceneDrawingAvailable() || IsSceneDrawingContext()) return;
        var scene = ActiveScene();
        if (scene is null) return;
        FinishPointerInteractionForContextChange();
        _scene = scene.DrawingScene;
        BindSceneEditStage(resetView: false);
    }

    private bool DeleteSceneDrawingSelection()
    {
        var scene = ActiveScene();
        if (scene is null || DrawingToolsBlocked()) return false;
        var targets = SelectedActiveDrawingObjectIndices();
        var instances = SelectedSceneInstances().ToArray();
        if (targets.Length == 0 && instances.Length == 0) return false;
        var layers = scene.CreateLayerSnapshot();
        var timeline = scene.Timeline.CreateSnapshot();
        var instanceSnapshot = scene.CreateInstanceSnapshot();
        if (!TryPrepareSelectedDrawingObjectsForCommand(layers.DrawingScene!, out targets)
            && instances.Length == 0) return false;
        _scene.RemoveObjects(targets);
        foreach (var instance in instances)
        {
            if (_project.TryRemoveSceneInstance(scene.Id, instance.Id, out _)) continue;
            scene.RestoreInstanceSnapshot(instanceSnapshot);
            scene.RestoreLayerSnapshot(layers);
            scene.Timeline.RestoreSnapshot(timeline);
            return false;
        }
        PushSceneTimelineUndo(scene, timeline, layers, instanceSnapshot, playheadFrame: _frame);
        ClearSelection();
        _timeline.RefreshTimeline();
        RebuildSceneComposition();
        UpdateInspector();
        return true;
    }

    private List<DrawingObjectInstanceDefinition> PasteSceneDrawingInstances(SceneDefinition scene)
    {
        var pasted = new List<DrawingObjectInstanceDefinition>();
        foreach (var item in _clipboardDrawingObjectInstances)
        {
            var state = item.BaseState with
            {
                X = VectorUnits.Quantize(item.BaseState.X + PasteOffsetUnits),
                Y = VectorUnits.Quantize(item.BaseState.Y + PasteOffsetUnits)
            };
            if (!_project.TryAddSceneInstance(scene.Id, item.DrawingObjectId, state.Position, state.Z,
                scene.ActiveLayerId, out var instance) || instance is null) continue;
            instance.Name = item.Name;
            instance.SetStateAtFrame(0, state);
            instance.RestoreStateKeyframes(item.StateKeyframes.Select(keyframe => keyframe with
            {
                State = keyframe.State with
                {
                    X = VectorUnits.Quantize(keyframe.State.X + PasteOffsetUnits),
                    Y = VectorUnits.Quantize(keyframe.State.Y + PasteOffsetUnits)
                }
            }));
            pasted.Add(instance);
        }
        return pasted;
    }
}
