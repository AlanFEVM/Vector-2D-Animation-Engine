namespace VectorAnimationEngine;

internal sealed record DrawingObjectDragData(string ProjectId, string DrawingObjectId);

internal sealed class DrawingObjectDefinition : ICompositionDefinition
{
    private readonly List<DrawingObjectInstanceDefinition> _instances = [];
    private readonly IReadOnlyList<DrawingObjectInstanceDefinition> _instanceView;
    private readonly LayeredInstanceIndex _instanceIndex;

    public DrawingObjectDefinition()
    {
        _instanceView = _instances.AsReadOnly();
        _instanceIndex = new LayeredInstanceIndex(_instanceView);
        Scene.ConfigureExternalLayerKeyframeContent(
            (layerId, _) => _instanceIndex.Any(layerId));
    }

    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Drawing Object";
    public string Kind { get; set; } = "Symbol";
    public string Detail { get; set; } = "Reusable drawing object";
    public string AssetFolderId { get; internal set; } = "";
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
