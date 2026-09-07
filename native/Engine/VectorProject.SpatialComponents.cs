namespace VectorAnimationEngine;

internal sealed partial class VectorProject
{
    public bool TryConvertSceneInstancesToSpatialComponent(
        string sceneId,
        IReadOnlyCollection<string> instanceIds,
        int frame,
        out DrawingObjectDefinition? component,
        out DrawingObjectInstanceDefinition? replacement)
    {
        component = null;
        replacement = null;
        if (frame < 0 || instanceIds is null || instanceIds.Count < 2) return false;

        var scene = _scenes.SingleOrDefault(candidate =>
            string.Equals(candidate.Id, sceneId, StringComparison.Ordinal));
        if (scene is null || scene.Dimension != SceneDimension.ThreeD) return false;

        var requestedIds = instanceIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (requestedIds.Length < 2 || requestedIds.Length != instanceIds.Count) return false;

        var requestedIdSet = requestedIds.ToHashSet(StringComparer.Ordinal);
        var selected = scene.Instances
            .Where(instance => requestedIdSet.Contains(instance.Id))
            .ToArray();
        if (selected.Length != requestedIds.Length) return false;

        var layerId = selected[0].SceneLayerId;
        if (selected.Any(instance => !string.Equals(instance.SceneLayerId, layerId, StringComparison.Ordinal))
            || scene.FindLayer(layerId)?.Kind != SceneLayerKind.Content
            || !scene.Timeline.EvaluateTargetExposure(layerId, frame).HasContent)
        {
            return false;
        }

        var layerInstances = scene.InstancesInLayer(layerId);
        var selectedLayerIndices = layerInstances
            .Select((instance, index) => (instance, index))
            .Where(item => requestedIdSet.Contains(item.instance.Id))
            .Select(item => item.index)
            .ToArray();
        if (selectedLayerIndices.Length != selected.Length
            || selectedLayerIndices[^1] - selectedLayerIndices[0] + 1 != selectedLayerIndices.Length)
        {
            return false;
        }

        var states = selected.Select(instance => instance.EvaluateState(frame)).ToArray();
        if (states.Any(state => !IsFiniteSpatialState(state))) return false;
        var pivot = new Point3(
            Center(states.Min(state => state.X), states.Max(state => state.X)),
            Center(states.Min(state => state.Y), states.Max(state => state.Y)),
            Center(states.Min(state => state.Z), states.Max(state => state.Z)));

        var instanceSnapshot = scene.CreateInstanceSnapshot();
        var timelineSnapshot = scene.Timeline.CreateSnapshot();
        var created = new DrawingObjectDefinition
        {
            Name = $"3D Symbol {_drawingObjects.Count + 1:000}",
            Kind = DrawingObjectAssetKinds.ThreeDimensional,
            Detail = "Reusable 3D symbol"
        };
        created.Scene.CreateEmpty(frameCount: Math.Max(1, scene.FrameCount));
        var memberLayerId = created.Scene.LayerIds[0];
        var root = new SceneObjectInstanceDefinition
        {
            DrawingObjectId = created.Id,
            SceneLayerId = layerId,
            Name = $"{created.Name} Instance {scene.Instances.Count - selected.Length + 1:000}",
            X = pivot.X,
            Y = pivot.Y,
            Z = pivot.Z,
            ScaleX = 1,
            ScaleY = 1,
            ScaleZ = 1,
            PlaybackFps = PlaybackFps
        };

        try
        {
            _drawingObjects.Add(created);
            foreach (var source in selected)
            {
                var member = CreateSpatialComponentMember(source, memberLayerId, pivot);
                created.AddInstance(this, member);
            }

            scene.AddInstance(this, root);
            foreach (var source in selected)
            {
                if (!scene.RemoveInstance(source))
                {
                    throw new InvalidOperationException("A selected scene instance could not be removed.");
                }
            }

            while (scene.MoveInstancesInLayer([root.Id], direction: 1))
            {
                var rootIndex = scene.InstancesInLayer(layerId)
                    .Select((instance, index) => (instance, index))
                    .First(item => string.Equals(item.instance.Id, root.Id, StringComparison.Ordinal))
                    .index;
                if (rootIndex <= selectedLayerIndices[0]) break;
            }
            scene.Timeline.RestoreSnapshot(timelineSnapshot);
            scene.SynchronizeTimelineTracks();
        }
        catch
        {
            scene.RestoreInstanceSnapshot(instanceSnapshot);
            scene.Timeline.RestoreSnapshot(timelineSnapshot);
            scene.SynchronizeTimelineTracks();
            _drawingObjects.Remove(created);
            return false;
        }

        component = created;
        replacement = root;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private static DrawingObjectInstanceDefinition CreateSpatialComponentMember(
        DrawingObjectInstanceDefinition source,
        string layerId,
        Point3 pivot)
    {
        var member = source.Clone();
        member.SceneLayerId = layerId;
        member.X -= pivot.X;
        member.Y -= pivot.Y;
        member.Z -= pivot.Z;
        member.RestoreStateKeyframes(source.StateKeyframes.Select(keyframe => keyframe with
        {
            State = keyframe.State with
            {
                X = keyframe.State.X - pivot.X,
                Y = keyframe.State.Y - pivot.Y,
                Z = keyframe.State.Z - pivot.Z
            }
        }));
        return member;
    }

    private static bool IsFiniteSpatialState(InstanceFrameState state) =>
        float.IsFinite(state.X)
        && float.IsFinite(state.Y)
        && float.IsFinite(state.Z)
        && float.IsFinite(state.RotationX)
        && float.IsFinite(state.RotationY)
        && float.IsFinite(state.RotationZ)
        && float.IsFinite(state.ScaleX)
        && float.IsFinite(state.ScaleY)
        && float.IsFinite(state.ScaleZ)
        && float.IsFinite(state.RotationPivot.X)
        && float.IsFinite(state.RotationPivot.Y)
        && float.IsFinite(state.RotationPivot.Z)
        && float.IsFinite(state.ScalePivot.X)
        && float.IsFinite(state.ScalePivot.Y)
        && float.IsFinite(state.ScalePivot.Z);

    private static float Center(float minimum, float maximum) =>
        VectorUnits.Quantize(minimum + (maximum - minimum) * 0.5f);

    private readonly record struct Point3(float X, float Y, float Z);
}
