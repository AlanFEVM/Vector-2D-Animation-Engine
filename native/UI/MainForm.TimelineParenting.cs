namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private IReadOnlyList<DrawingObjectInstanceDefinition> MotionTransformInstances()
    {
        var selected = SelectedSceneInstances();
        if (IsSceneMaskEditing()
            || IsSceneCompositionContext() && ActiveSceneViewDimension() != SceneDimension.TwoD) return selected;
        var timeline = _timeline.Context.Timeline;
        var descendants = timeline.MotionDescendantTargets(selected.Select(instance => instance.SceneLayerId));
        if (descendants.Count == 0) return selected;
        return selected.Concat(ActiveEditableInstances().Where(instance => descendants.Contains(instance.SceneLayerId)))
            .DistinctBy(instance => instance.Id).ToArray();
    }

    private int[] DrawingMotionEditTargets()
    {
        var selected = _selectedObjects.Concat(_selectedMoveStarts.Keys).Append(_selectedObject);
        return _activeHandle == EditHandleKind.None
            ? _scene.ExpandMotionTransformObjects(selected, _frame)
            : selected.Where(index => (uint)index < _scene.ObjectCount).Distinct().ToArray();
    }

    private void InitializeTimelineParenting()
    {
        _timeline.MotionParentTargetResolver = targetId =>
            _timeline.Context.Timeline.GetMotionParentTargetId(targetId);
        _timeline.CanMotionParent = CanSetTimelineMotionParent;
        _timeline.MotionParentingAvailable = true;
        _timeline.MotionParentingVisible = true;
        _timeline.MotionParentRequested += (_, request) => SetTimelineMotionParent(request);
    }

    private bool IsTimelineMotionContentLayer(string targetId)
    {
        return _timeline.Context switch
        {
            SceneDefinition scene => scene.FindLayer(targetId) is { Kind: SceneLayerKind.Content },
            DrawingObjectDefinition drawing => IsDrawingMotionContentLayer(drawing.Scene, targetId),
            VectorScene drawingScene => IsDrawingMotionContentLayer(drawingScene, targetId),
            _ => false
        };
    }

    private static bool IsDrawingMotionContentLayer(VectorScene scene, string targetId)
    {
        var index = Array.IndexOf(scene.LayerIds, targetId);
        return index >= 0 && scene.GetLayerKind(index) == DrawingLayerKind.Drawing;
    }

    private bool CanSetTimelineMotionParent(string childTargetId, string parentTargetId)
    {
        return !IsShotDirectorContext()
            && IsTimelineMotionContentLayer(childTargetId)
            && (string.IsNullOrEmpty(parentTargetId) || IsTimelineMotionContentLayer(parentTargetId))
            && _timeline.Context.Timeline.CanSetMotionParent(childTargetId, parentTargetId);
    }

    private void SetTimelineMotionParent(TimelineParentRequestedEventArgs request)
    {
        var context = _timeline.Context;
        var child = context.Timeline.FindTrack(request.TrackId);
        var parent = string.IsNullOrEmpty(request.ParentTrackId)
            ? null : context.Timeline.FindTrack(request.ParentTrackId);
        if (child is null || !string.IsNullOrEmpty(request.ParentTrackId) && parent is null) return;
        var parentTargetId = parent?.TargetId ?? "";
        if (!CanSetTimelineMotionParent(child.TargetId, parentTargetId)) return;
        var drawingScene = context switch
        {
            DrawingObjectDefinition drawing => drawing.Scene,
            VectorScene scene => scene,
            _ => null
        };
        if (drawingScene is not null)
        {
            var before = drawingScene.CreateSnapshot();
            if (!context.Timeline.SetMotionParent(child.TargetId, parentTargetId)) return;
            PushUndoSnapshot(before);
        }
        else if (context is SceneDefinition scene)
        {
            var before = scene.Timeline.CreateSnapshot();
            var layers = scene.CreateLayerSnapshot();
            if (!scene.Timeline.SetMotionParent(child.TargetId, parentTargetId)) return;
            PushSceneTimelineUndo(scene, before, layers);
        }
        _timeline.RefreshTimeline();
    }
}
