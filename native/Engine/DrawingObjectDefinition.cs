namespace VectorAnimationEngine;

internal sealed record DrawingObjectDragData(string ProjectId, string DrawingObjectId);

internal sealed class DrawingObjectDefinition : ICompositionDefinition
{
    private readonly List<DrawingObjectInstanceDefinition> _instances = [];
    private readonly List<string> _assetTagIds = [];
    private readonly IReadOnlyList<DrawingObjectInstanceDefinition> _instanceView;
    private readonly IReadOnlyList<string> _assetTagIdView;
    private readonly LayeredInstanceIndex _instanceIndex;

    public DrawingObjectDefinition()
    {
        _instanceView = _instances.AsReadOnly();
        _assetTagIdView = _assetTagIds.AsReadOnly();
        _instanceIndex = new LayeredInstanceIndex(_instanceView);
        Scene.ConfigureExternalLayerKeyframeContent(
            (layerId, _) => _instanceIndex.Any(layerId));
    }

    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Drawing Object";
    public string Kind { get; set; } = "Symbol";
    public string Detail { get; set; } = "Reusable drawing object";
    public string AssetFolderId { get; internal set; } = "";
    public IReadOnlyList<string> AssetTagIds => _assetTagIdView;
    public bool CanDraw => true;
    public PointF Anchor { get; private set; }
    public VectorScene Scene { get; } = new();
    public IReadOnlyList<DrawingObjectInstanceDefinition> Instances => _instanceView;
    public DateTime CreatedAt { get; init; } = DateTime.Now;
    public AnimationTimeline Timeline => Scene.Timeline;
    public IReadOnlyList<string> TimelineTargetIds => Scene.TimelineTargetIds;

    public int FrameCount => Scene.FrameCount;

    internal void SetAnchor(PointF anchor)
    {
        Anchor = VectorUnits.Quantize(anchor);
    }

    internal void ReplaceAssetTagIds(IEnumerable<string> tagIds)
    {
        ArgumentNullException.ThrowIfNull(tagIds);
        _assetTagIds.Clear();
        _assetTagIds.AddRange(tagIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal));
    }

    public void SynchronizeTimelineTracks()
    {
        NormalizeInstanceLayers();
        Scene.SynchronizeTimelineTracks();
    }

    public IReadOnlyList<DrawingObjectInstanceDefinition> InstancesInLayer(string layerId)
    {
        return _instanceIndex.Get(layerId);
    }

    internal void AddInstance(VectorProject project, DrawingObjectInstanceDefinition instance)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(instance);
        if (!project.OwnsDrawingObject(this)
            || !project.CanContainDrawingObject(Id, instance.DrawingObjectId)
            || string.IsNullOrWhiteSpace(instance.Id)
            || Scene.LayerIds.Contains(instance.Id, StringComparer.Ordinal)
            || _instances.Any(item => string.Equals(item.Id, instance.Id, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "The drawing-object instance would create an invalid or recursive containment relationship.");
        }

        if (Scene.LayerCount > 0)
        {
            instance.SceneLayerId = Scene.ResolveInstanceLayerId(instance.SceneLayerId);
        }
        _instances.Add(instance);
        _instanceIndex.Invalidate();
        SynchronizeTimelineTracks();
        var track = Scene.Timeline.FindTrackByTargetId(instance.SceneLayerId);
        if (track is not null && !track.EvaluateExposure(Scene.EditFrame).HasContent)
        {
            Scene.Timeline.InsertKeyframe(track.Id, Scene.EditFrame);
        }
    }

    internal int RemoveInstancesReferencing(string drawingObjectId)
    {
        if (string.IsNullOrWhiteSpace(drawingObjectId)) return 0;
        var removed = _instances.RemoveAll(instance =>
            string.Equals(instance.DrawingObjectId, drawingObjectId, StringComparison.Ordinal));
        if (removed > 0)
        {
            _instanceIndex.Invalidate();
            SynchronizeTimelineTracks();
            Scene.SynchronizeExternalLayerKeyframeContent();
        }
        return removed;
    }

    internal bool RemoveInstance(DrawingObjectInstanceDefinition instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (!_instances.Remove(instance)) return false;

        _instanceIndex.Invalidate();
        SynchronizeTimelineTracks();
        Scene.SynchronizeExternalLayerKeyframeContent();
        return true;
    }

    internal DrawingObjectInstanceDefinition[] CreateInstanceSnapshot()
    {
        return _instances.Select(instance => instance.Clone()).ToArray();
    }

    internal void RestoreInstanceSnapshot(IEnumerable<DrawingObjectInstanceDefinition> instances)
    {
        ArgumentNullException.ThrowIfNull(instances);
        _instances.Clear();
        _instances.AddRange(instances.Select(instance => instance.Clone()));
        _instanceIndex.Invalidate();
        SynchronizeTimelineTracks();
        Scene.SynchronizeExternalLayerKeyframeContent();
    }

    internal bool CanCreateTimelineTween(
        int layer,
        int startFrame,
        int endFrame,
        TimelineTweenKind kind,
        out string error)
    {
        if ((uint)layer >= Scene.LayerCount)
        {
            error = "Select a drawing layer.";
            return false;
        }

        return InstancesInLayer(Scene.LayerIds[layer]).Count == 0
            ? Scene.CanCreateTimelineTween(layer, startFrame, endFrame, kind, out error)
            : TryResolveInstanceTimelineTween(
                layer,
                startFrame,
                endFrame,
                kind,
                out _,
                out _,
                out error);
    }

    internal bool TryCreateTimelineTween(
        int layer,
        int startFrame,
        int endFrame,
        TimelineTweenKind kind,
        out string error)
    {
        if ((uint)layer >= Scene.LayerCount)
        {
            error = "Select a drawing layer.";
            return false;
        }

        if (InstancesInLayer(Scene.LayerIds[layer]).Count == 0)
        {
            return Scene.TryCreateTimelineTween(layer, startFrame, endFrame, kind, out error);
        }

        if (!TryResolveInstanceTimelineTween(
                layer,
                startFrame,
                endFrame,
                kind,
                out var track,
                out var instance,
                out error))
        {
            return false;
        }

        startFrame = Math.Max(0, startFrame);
        endFrame = Math.Min(track.Duration - 1, endFrame);
        var source = instance.EvaluateState(startFrame);
        var target = instance.EvaluateState(endFrame);
        var sceneSnapshot = Scene.CreateSnapshot();
        var instanceSnapshot = CreateInstanceSnapshot();
        try
        {
            using var batch = Timeline.BeginBatchUpdate();
            for (var frame = startFrame + 1; frame < endFrame; frame++)
            {
                if (!Timeline.InsertKeyframe(track.Id, frame))
                {
                    throw new InvalidOperationException("The tween span could not create an intermediate keyframe.");
                }

                instance.SetStateAtFrame(
                    frame,
                    DrawingObjectInstanceDefinition.InterpolateState(
                        source,
                        target,
                        (float)(frame - startFrame) / (endFrame - startFrame)));
            }

            if (!Timeline.TryCreateTween(track.Id, startFrame, endFrame, kind, out var validation))
            {
                throw new InvalidOperationException($"The tween span is invalid ({validation}).");
            }

            Scene.SynchronizeExternalLayerKeyframeContent();
            return true;
        }
        catch (Exception exception)
        {
            Scene.RestoreSnapshot(sceneSnapshot);
            RestoreInstanceSnapshot(instanceSnapshot);
            error = exception.Message;
            return false;
        }
    }

    internal bool ReplaceTimelineTweenCurve(
        int layer,
        int startFrame,
        int endFrame,
        IEnumerable<TweenCurveAnchor> anchors)
    {
        if (!Scene.ReplaceTimelineTweenCurve(layer, startFrame, endFrame, anchors)) return false;
        RefreshInstanceTimelineTweenMaterializations(layer);
        return true;
    }

    internal bool RemoveTimelineTween(int layer, int startFrame, int endFrame) =>
        Scene.RemoveTimelineTween(layer, startFrame, endFrame);

    internal bool RefreshTimelineTweenMaterializationsAtEndpointFrame(int frame)
    {
        var geometryChanged = Scene.RefreshTimelineTweenMaterializationsAtEndpointFrame(frame);
        var instanceChanged = RefreshInstanceTimelineTweenMaterializations(endpointFrame: frame);
        return geometryChanged || instanceChanged;
    }

    internal bool RefreshInstanceTimelineTweenMaterializationsInLayer(string layerId)
    {
        var layer = Array.IndexOf(Scene.LayerIds, layerId);
        return layer >= 0 && RefreshInstanceTimelineTweenMaterializations(layer);
    }

    private bool TryResolveInstanceTimelineTween(
        int layer,
        int startFrame,
        int endFrame,
        TimelineTweenKind kind,
        out AnimationTimelineTrack track,
        out DrawingObjectInstanceDefinition instance,
        out string error)
    {
        track = null!;
        instance = null!;
        error = string.Empty;
        if ((uint)layer >= Scene.LayerCount)
        {
            error = "Select a drawing layer.";
            return false;
        }

        SynchronizeTimelineTracks();
        var resolvedTrack = Timeline.FindTrackByTargetId(Scene.LayerIds[layer]);
        if (resolvedTrack is null)
        {
            error = "The selected layer has no timeline track.";
            return false;
        }
        track = resolvedTrack;

        startFrame = Math.Max(0, startFrame);
        endFrame = Math.Min(track.Duration - 1, endFrame);
        if (endFrame <= startFrame)
        {
            error = "Select a span containing a start and end frame.";
            return false;
        }

        var startExposure = track.EvaluateExposure(startFrame);
        var endExposure = track.EvaluateExposure(endFrame);
        if (!startExposure.IsKeyframe || !endExposure.IsKeyframe
            || !startExposure.HasContent || !endExposure.HasContent)
        {
            error = "Both ends of the span must be populated keyframes.";
            return false;
        }

        if (track.Keyframes.Any(keyframe =>
                keyframe.Frame > startFrame
                && keyframe.Frame < endFrame))
        {
            error = "Remove intermediate keyframes before creating a tween.";
            return false;
        }

        if (!track.CanCreateTween(startFrame, endFrame, kind, out var validation))
        {
            error = $"The tween span is invalid ({validation}).";
            return false;
        }

        var instances = InstancesInLayer(Scene.LayerIds[layer]);
        if (kind == TimelineTweenKind.Shape)
        {
            error = "Shape tweens do not support drawing object instances.";
            return false;
        }

        var sourceObjectCount = Scene.TimelineObjectCountForKeyframe(layer, startExposure.SourceKeyframeFrame);
        var targetObjectCount = Scene.TimelineObjectCountForKeyframe(layer, endExposure.SourceKeyframeFrame);
        if (sourceObjectCount > 0 || targetObjectCount > 0)
        {
            error = "Tween layers cannot mix vector shapes and drawing object instances.";
            return false;
        }

        if (instances.Count != 1)
        {
            error = "Classic tweens require exactly one drawing object instance on an instance layer.";
            return false;
        }

        instance = instances[0];
        return true;
    }

    private bool RefreshInstanceTimelineTweenMaterializations(
        int? layerFilter = null,
        int? endpointFrame = null)
    {
        var changed = false;
        for (var layer = 0; layer < Scene.LayerCount; layer++)
        {
            if (layerFilter is { } requiredLayer && layer != requiredLayer) continue;
            var track = Timeline.FindTrackByTargetId(Scene.LayerIds[layer]);
            if (track is null) continue;

            var instances = InstancesInLayer(Scene.LayerIds[layer]);
            if (instances.Count != 1) continue;
            foreach (var tween in track.Tweens)
            {
                if (tween.Kind != TimelineTweenKind.Classic
                    || endpointFrame is { } endpoint
                        && tween.StartFrame != endpoint
                        && tween.EndFrame != endpoint
                    || Scene.TimelineObjectCountForKeyframe(layer, tween.StartFrame) > 0
                    || Scene.TimelineObjectCountForKeyframe(layer, tween.EndFrame) > 0)
                {
                    continue;
                }

                var instance = instances[0];
                var source = instance.EvaluateState(tween.StartFrame);
                var target = instance.EvaluateState(tween.EndFrame);
                for (var frame = tween.StartFrame + 1; frame < tween.EndFrame; frame++)
                {
                    changed |= instance.SetStateAtFrame(
                        frame,
                        DrawingObjectInstanceDefinition.InterpolateState(
                            source,
                            target,
                            tween.ProgressAt(frame)));
                }
            }
        }

        return changed;
    }

    internal bool RemoveLayers(VectorProject project, IEnumerable<string> layerIds)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(layerIds);
        if (!project.OwnsDrawingObject(this)) throw new InvalidOperationException("The drawing object is not owned by this project.");

        var removal = Scene.ResolveLayerRemovalIndices(layerIds);
        if (removal.Length == 0 || removal.Length >= Scene.LayerCount) return false;
        var removedIds = removal.Select(layer => Scene.LayerIds[layer]).ToHashSet(StringComparer.Ordinal);
        if (_instances.RemoveAll(instance => removedIds.Contains(instance.SceneLayerId)) > 0)
        {
            _instanceIndex.Invalidate();
        }
        if (!Scene.RemoveLayers(removedIds)) return false;
        Scene.SynchronizeExternalLayerKeyframeContent();
        return true;
    }

    internal bool CanMoveInstancesInLayer(IReadOnlyCollection<string> instanceIds, int direction)
    {
        return CreateInstanceMovePlan(instanceIds, direction).Count > 0;
    }

    internal bool MoveInstancesInLayer(IReadOnlyCollection<string> instanceIds, int direction)
    {
        var plan = CreateInstanceMovePlan(instanceIds, direction);
        if (plan.Count == 0) return false;
        foreach (var (index, instance) in plan) _instances[index] = instance;
        _instanceIndex.Invalidate();
        return true;
    }

    private Dictionary<int, DrawingObjectInstanceDefinition> CreateInstanceMovePlan(
        IReadOnlyCollection<string> instanceIds,
        int direction)
    {
        ArgumentNullException.ThrowIfNull(instanceIds);
        if (instanceIds.Count == 0 || direction == 0) return [];

        var selectedIds = instanceIds.ToHashSet(StringComparer.Ordinal);
        var plan = new Dictionary<int, DrawingObjectInstanceDefinition>();
        foreach (var layerId in _instances
                     .Where(instance => selectedIds.Contains(instance.Id))
                     .Select(instance => instance.SceneLayerId)
                     .Distinct(StringComparer.Ordinal))
        {
            var positions = Enumerable.Range(0, _instances.Count)
                .Where(index => string.Equals(_instances[index].SceneLayerId, layerId, StringComparison.Ordinal))
                .ToArray();
            if (positions.Length < 2) continue;
            var arranged = positions.Select(index => _instances[index]).ToArray();
            var moved = false;
            if (direction > 0)
            {
                for (var index = arranged.Length - 2; index >= 0; index--)
                {
                    if (!selectedIds.Contains(arranged[index].Id)
                        || selectedIds.Contains(arranged[index + 1].Id))
                    {
                        continue;
                    }
                    (arranged[index], arranged[index + 1]) = (arranged[index + 1], arranged[index]);
                    moved = true;
                }
            }
            else
            {
                for (var index = 1; index < arranged.Length; index++)
                {
                    if (!selectedIds.Contains(arranged[index].Id)
                        || selectedIds.Contains(arranged[index - 1].Id))
                    {
                        continue;
                    }
                    (arranged[index], arranged[index - 1]) = (arranged[index - 1], arranged[index]);
                    moved = true;
                }
            }

            if (!moved) continue;
            for (var index = 0; index < positions.Length; index++) plan[positions[index]] = arranged[index];
        }

        return plan;
    }

    internal void InsertInstanceStateFrames(string layerId, int frame, int count)
    {
        foreach (var instance in InstancesInLayer(layerId)) instance.InsertStateFrames(frame, count);
    }

    internal void RemoveInstanceStateFrames(string layerId, int frame, int count)
    {
        foreach (var instance in InstancesInLayer(layerId)) instance.RemoveStateFrames(frame, count);
    }

    internal void RemoveInstanceStateKeyframes(string layerId, int frame)
    {
        foreach (var instance in InstancesInLayer(layerId)) instance.RemoveStateKeyframe(frame);
    }

    private void NormalizeInstanceLayers()
    {
        if (Scene.LayerCount == 0) return;
        var reassignedInstance = false;
        foreach (var instance in _instances)
        {
            var layerId = Scene.ResolveInstanceLayerId(instance.SceneLayerId);
            if (string.Equals(instance.SceneLayerId, layerId, StringComparison.Ordinal)) continue;
            instance.SceneLayerId = layerId;
            reassignedInstance = true;
        }

        if (reassignedInstance) _instanceIndex.Invalidate();
    }

    public VaultItem ToVaultItem()
    {
        var payload = string.Join(Environment.NewLine, new[]
        {
            $"DrawingObjectId: {Id}",
            $"Name: {Name}",
            $"Kind: {Kind}",
            $"CreatedAt: {CreatedAt:O}",
            Detail
        });

        return new VaultItem
        {
            Kind = "Drawing Object",
            Name = Name,
            Detail = Detail,
            Payload = payload,
            ReferenceKind = "DrawingObject",
            ReferenceId = Id
        };
    }
}
