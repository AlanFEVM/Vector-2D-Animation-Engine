namespace VectorAnimationEngine;

internal sealed class SceneLayerDefinition
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Layer";
    public SceneLayerKind Kind { get; internal set; }
    public string MaskLayerId { get; internal set; } = "";
    public bool Visible { get; set; } = true;
    public LayerBlendMode BlendMode { get; set; } = LayerBlendMode.Normal;
    public int ColorArgb { get; set; } = Color.FromArgb(79, 195, 247).ToArgb();
    public bool Outline { get; set; }
    internal VectorScene? MaskScene { get; set; }
}

internal sealed class SceneLayerSnapshot
{
    public SceneLayerSnapshotItem[] Layers { get; init; } = [];
    public string ActiveLayerId { get; init; } = "";
    public Dictionary<string, string> InstanceLayerIds { get; init; } = new(StringComparer.Ordinal);
}

internal sealed record SceneLayerSnapshotItem(
    string Id,
    string Name,
    bool Visible,
    int ColorArgb,
    bool Outline = false,
    LayerBlendMode BlendMode = LayerBlendMode.Normal)
{
    public SceneLayerKind Kind { get; init; }
    public string MaskLayerId { get; init; } = "";
    public VectorSceneSnapshot? MaskScene { get; init; }
}

internal sealed class SceneDefinition : ITimelineContext, ICompositionDefinition
{
    private readonly AnimationTimeline _timeline = new();
    private readonly List<SceneLayerDefinition> _layers = [];
    private readonly List<DrawingObjectInstanceDefinition> _instances = [];
    private readonly IReadOnlyList<SceneLayerDefinition> _layerView;
    private readonly IReadOnlyList<DrawingObjectInstanceDefinition> _instanceView;
    private readonly LayeredInstanceIndex _instanceIndex;

    public SceneDefinition(int initialFrameCount = AnimationTimeline.DefaultDuration)
    {
        _layerView = _layers.AsReadOnly();
        _instanceView = _instances.AsReadOnly();
        _instanceIndex = new LayeredInstanceIndex(_instanceView);
        var firstLayer = new SceneLayerDefinition { Name = "Layer 0001" };
        _layers.Add(firstLayer);
        _timeline.SynchronizeTracks([firstLayer.Id], Math.Max(1, initialFrameCount), populateNewTracks: false);
        _timeline.InsertBlankKeyframe(_timeline.Tracks[0].Id, 0);
    }

    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Scene";
    public string Detail { get; set; } = "Scene composition context";
    public bool CanDraw => false;
    public SceneDimension Dimension { get; set; } = SceneDimension.TwoD;
    public SceneCameraDefinition Camera { get; set; } = new();
    public IReadOnlyList<SceneLayerDefinition> Layers => _layerView;
    public IReadOnlyList<DrawingObjectInstanceDefinition> Instances => _instanceView;
    public string ActiveLayerId { get; private set; } = "";
    public DateTime CreatedAt { get; init; } = DateTime.Now;

    public AnimationTimeline Timeline
    {
        get
        {
            SynchronizeTimelineTracks();
            return _timeline;
        }
    }

    public int FrameCount => _timeline.Tracks.Count == 0 ? AnimationTimeline.DefaultDuration : _timeline.Duration;
    public IReadOnlyList<string> TimelineTargetIds => Layers.Select(layer => layer.Id).ToArray();

    public void SynchronizeTimelineTracks()
    {
        NormalizeLayers();
        using var batchUpdate = _timeline.BeginBatchUpdate();
        _timeline.SynchronizeTracks(Layers.Select(layer => layer.Id), FrameCount, populateNewTracks: false);
        foreach (var track in _timeline.Tracks)
        {
            if (track.Keyframes.Count == 0 || track.Keyframes[0].Frame > 0)
            {
                _timeline.InsertBlankKeyframe(track.Id, 0);
            }

            var emptyKeyframeFrames = track.Keyframes
                .Where(keyframe => ResolveKeyframeKindForLayerContent(
                    track.TargetId,
                    keyframe.Kind,
                    keyframe.Frame) != keyframe.Kind)
                .Select(keyframe => keyframe.Frame)
                .ToArray();
            foreach (var frame in emptyKeyframeFrames)
            {
                _timeline.InsertBlankKeyframe(track.Id, frame);
            }
        }

        SynchronizeMaskSceneDurations(FrameCount);
    }

    public SceneLayerDefinition? FindLayer(string layerId)
    {
        return Layers.FirstOrDefault(layer => string.Equals(layer.Id, layerId, StringComparison.Ordinal));
    }

    public VectorScene? FindMaskScene(string maskLayerId)
    {
        var layer = FindLayer(maskLayerId);
        return layer?.Kind == SceneLayerKind.Mask ? layer.MaskScene : null;
    }

    public VectorScene? ActiveMaskScene() => FindMaskScene(ActiveLayerId);

    public SceneLayerDefinition? GetMaskContentLayer(string maskLayerId)
    {
        return Layers.FirstOrDefault(layer =>
            layer.Kind == SceneLayerKind.Content
            && string.Equals(layer.MaskLayerId, maskLayerId, StringComparison.Ordinal));
    }

    public IReadOnlyList<DrawingObjectInstanceDefinition> InstancesInLayer(string layerId)
    {
        return _instanceIndex.Get(layerId);
    }

    internal TimelineKeyframeKind ResolveKeyframeKindForLayerContent(
        string layerId,
        TimelineKeyframeKind requestedKind)
    {
        return ResolveKeyframeKindForLayerContent(layerId, requestedKind, frame: -1);
    }

    internal TimelineKeyframeKind ResolveKeyframeKindForLayerContent(
        string layerId,
        TimelineKeyframeKind requestedKind,
        int frame)
    {
        if (FindLayer(layerId)?.Kind == SceneLayerKind.Mask)
        {
            return frame < 0
                ? requestedKind
                : MaskSceneHasContent(layerId, frame)
                    ? TimelineKeyframeKind.Populated
                    : TimelineKeyframeKind.Blank;
        }
        return requestedKind == TimelineKeyframeKind.Populated && InstancesInLayer(layerId).Count == 0
            ? TimelineKeyframeKind.Blank
            : requestedKind;
    }

    internal bool InsertMaskTimelineFrame(string maskLayerId, int frame, int count = 1)
    {
        if (frame < 0 || count <= 0) return false;
        return ApplyMaskTimelineMutation(maskLayerId, (maskScene, outerTrack) =>
        {
            var outerChanged = _timeline.InsertFrame(outerTrack.Id, frame, count);
            if (!outerChanged) return (false, false);

            var maskChanged = false;
            for (var layer = 0; layer < maskScene.LayerCount; layer++)
            {
                if (!maskScene.InsertTimelineFrame(layer, frame, count)) return (false, false);
                maskChanged = true;
            }
            return (true, maskChanged);
        });
    }

    internal bool RemoveMaskTimelineFrame(string maskLayerId, int frame, int count = 1)
    {
        if (frame < 0 || count <= 0) return false;
        return ApplyMaskTimelineMutation(maskLayerId, (maskScene, outerTrack) =>
        {
            var outerChanged = _timeline.RemoveFrame(outerTrack.Id, frame, count);
            if (!outerChanged) return (false, false);

            var maskChanged = false;
            for (var layer = 0; layer < maskScene.LayerCount; layer++)
            {
                if (!maskScene.RemoveTimelineFrame(layer, frame, count)) return (false, false);
                maskChanged = true;
            }
            return (true, maskChanged);
        });
    }

    internal bool InsertMaskTimelineKeyframe(string maskLayerId, int frame)
    {
        if (frame < 0) return false;
        return ApplyMaskTimelineMutation(maskLayerId, (maskScene, outerTrack) =>
        {
            var changed = false;
            for (var layer = 0; layer < maskScene.LayerCount; layer++)
            {
                changed |= maskScene.InsertTimelineKeyframe(layer, frame);
            }

            changed |= SetMaskTrackKeyframeKind(
                outerTrack,
                frame,
                MaskSceneHasContent(maskLayerId, frame)
                    ? TimelineKeyframeKind.Populated
                    : TimelineKeyframeKind.Blank);
            return (true, changed);
        });
    }

    internal bool InsertMaskTimelineBlankKeyframe(string maskLayerId, int frame)
    {
        if (frame < 0) return false;
        return ApplyMaskTimelineMutation(maskLayerId, (maskScene, outerTrack) =>
        {
            var changed = false;
            for (var layer = 0; layer < maskScene.LayerCount; layer++)
            {
                changed |= maskScene.InsertTimelineBlankKeyframe(layer, frame);
            }
            changed |= SetMaskTrackKeyframeKind(outerTrack, frame, TimelineKeyframeKind.Blank);
            return (true, changed);
        });
    }

    internal bool ClearMaskTimelineKeyframe(string maskLayerId, int frame)
    {
        if (frame < 0) return false;
        return ApplyMaskTimelineMutation(maskLayerId, (maskScene, outerTrack) =>
        {
            var changed = false;
            for (var layer = 0; layer < maskScene.LayerCount; layer++)
            {
                changed |= maskScene.ClearTimelineKeyframe(layer, frame);
            }
            changed |= _timeline.ClearKeyframe(outerTrack.Id, frame);
            return (true, changed);
        });
    }

    internal bool CopyMaskTimelineFrameFrom(
        SceneDefinition sourceScene,
        string sourceMaskLayerId,
        int sourceFrame,
        string destinationMaskLayerId,
        int destinationFrame)
    {
        ArgumentNullException.ThrowIfNull(sourceScene);
        if (sourceFrame < 0 || destinationFrame < 0) return false;

        sourceScene.SynchronizeTimelineTracks();
        var sourceMaskScene = sourceScene.FindMaskScene(sourceMaskLayerId);
        if (sourceMaskScene is null) return false;
        if (ReferenceEquals(sourceScene, this)
            && string.Equals(sourceMaskLayerId, destinationMaskLayerId, StringComparison.Ordinal)
            && sourceFrame == destinationFrame)
        {
            return false;
        }

        return CopyMaskTimelineFrameFrom(
            sourceMaskScene,
            sourceFrame,
            destinationMaskLayerId,
            destinationFrame);
    }

    internal bool CopyMaskTimelineFrameFrom(
        VectorScene sourceMaskScene,
        int sourceFrame,
        string destinationMaskLayerId,
        int destinationFrame)
    {
        ArgumentNullException.ThrowIfNull(sourceMaskScene);
        if (sourceFrame < 0
            || sourceFrame >= sourceMaskScene.FrameCount
            || destinationFrame < 0)
        {
            return false;
        }

        return ApplyMaskTimelineMutation(destinationMaskLayerId, (destinationMaskScene, outerTrack) =>
        {
            if (sourceMaskScene.LayerCount != destinationMaskScene.LayerCount) return (false, false);

            var changed = false;
            for (var layer = 0; layer < destinationMaskScene.LayerCount; layer++)
            {
                changed |= destinationMaskScene.CopyTimelineFrameFrom(
                    sourceMaskScene,
                    layer,
                    sourceFrame,
                    layer,
                    destinationFrame);
            }

            changed |= SetMaskTrackKeyframeKind(
                outerTrack,
                destinationFrame,
                MaskSceneHasContent(destinationMaskLayerId, destinationFrame)
                    ? TimelineKeyframeKind.Populated
                    : TimelineKeyframeKind.Blank);
            return (true, changed);
        });
    }

    internal bool SynchronizeMaskTimelineContent(string maskLayerId, int frame)
    {
        if (frame < 0) return false;
        return ApplyMaskTimelineMutation(maskLayerId, (maskScene, outerTrack) =>
        {
            var sourceFrames = Enumerable.Range(0, maskScene.LayerCount)
                .Select(layer => maskScene.Timeline.EvaluateTargetExposure(maskScene.LayerIds[layer], frame))
                .Where(exposure => exposure.SourceKind is not null)
                .Select(exposure => exposure.SourceKeyframeFrame)
                .Distinct()
                .ToArray();
            var keyframeFrame = sourceFrames.Length == 1 ? sourceFrames[0] : frame;
            var kind = MaskSceneHasContent(maskLayerId, frame)
                ? TimelineKeyframeKind.Populated
                : TimelineKeyframeKind.Blank;
            return (true, SetMaskTrackKeyframeKind(outerTrack, keyframeFrame, kind));
        });
    }

    public bool IsMaskLayerActive(string maskLayerId, int frame)
    {
        var layer = FindLayer(maskLayerId);
        return layer?.Kind == SceneLayerKind.Mask
            && layer.Visible
            && _timeline.EvaluateTargetExposure(layer.Id, frame).HasContent
            && MaskSceneHasContent(layer.Id, frame);
    }

    public void SetActiveLayer(string layerId)
    {
        NormalizeLayers();
        if (FindLayer(layerId) is not null) ActiveLayerId = layerId;
    }

    public bool IsLayerVisible(string layerId) => FindLayer(layerId)?.Visible == true;

    public void ToggleLayer(string layerId)
    {
        var layer = FindLayer(layerId);
        if (layer is not null) layer.Visible = !layer.Visible;
    }

    public void SoloLayer(string layerId)
    {
        foreach (var layer in _layers) layer.Visible = string.Equals(layer.Id, layerId, StringComparison.Ordinal);
    }

    public void ShowAllLayers()
    {
        foreach (var layer in _layers) layer.Visible = true;
    }

    public bool SetLayerVisible(string layerId, bool visible)
    {
        var layer = FindLayer(layerId);
        if (layer is null || layer.Visible == visible) return false;
        layer.Visible = visible;
        return true;
    }

    public bool SetLayerColor(string layerId, Color color)
    {
        var layer = FindLayer(layerId);
        if (layer is null || layer.ColorArgb == color.ToArgb()) return false;
        layer.ColorArgb = color.ToArgb();
        return true;
    }

    public bool SetLayerBlendMode(string layerId, LayerBlendMode blendMode)
    {
        var layer = FindLayer(layerId);
        if (layer is null || !Enum.IsDefined(blendMode) || layer.BlendMode == blendMode) return false;
        layer.BlendMode = blendMode;
        return true;
    }

    public bool SetLayerOutline(string layerId, bool outline)
    {
        var layer = FindLayer(layerId);
        if (layer is null || layer.Outline == outline) return false;
        layer.Outline = outline;
        return true;
    }

    internal bool RenameLayer(VectorProject project, string layerId, string? name)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!project.OwnsScene(this)) throw new InvalidOperationException("The scene is not owned by this project.");
        var layer = FindLayer(layerId);
        var normalized = name?.Trim();
        if (layer is null || string.IsNullOrWhiteSpace(normalized)) return false;
        normalized = normalized.Length <= 80 ? normalized : normalized[..80];
        if (string.Equals(layer.Name, normalized, StringComparison.Ordinal)) return false;
        layer.Name = normalized;
        return true;
    }

    internal SceneLayerSnapshot CreateLayerSnapshot()
    {
        NormalizeLayers();
        return new SceneLayerSnapshot
        {
            Layers = _layers.Select(layer => new SceneLayerSnapshotItem(
                layer.Id,
                layer.Name,
                layer.Visible,
                layer.ColorArgb,
                layer.Outline,
                layer.BlendMode)
            {
                Kind = layer.Kind,
                MaskLayerId = layer.MaskLayerId,
                MaskScene = layer.MaskScene?.CreateSnapshot()
            }).ToArray(),
            ActiveLayerId = ActiveLayerId,
            InstanceLayerIds = _instances.ToDictionary(instance => instance.Id, instance => instance.SceneLayerId, StringComparer.Ordinal)
        };
    }

    internal void RestoreLayerSnapshot(SceneLayerSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _layers.Clear();
        foreach (var layer in snapshot.Layers)
        {
            if (string.IsNullOrWhiteSpace(layer.Id) || _layers.Any(item => string.Equals(item.Id, layer.Id, StringComparison.Ordinal))) continue;
            _layers.Add(new SceneLayerDefinition
            {
                Id = layer.Id,
                Name = layer.Name,
                Visible = layer.Visible,
                BlendMode = Enum.IsDefined(layer.BlendMode) ? layer.BlendMode : LayerBlendMode.Normal,
                ColorArgb = layer.ColorArgb,
                Outline = layer.Outline,
                Kind = Enum.IsDefined(layer.Kind) ? layer.Kind : SceneLayerKind.Content,
                MaskLayerId = layer.MaskLayerId ?? "",
                MaskScene = RestoreMaskScene(layer.Kind, layer.MaskScene)
            });
        }

        foreach (var instance in _instances)
        {
            instance.SceneLayerId = snapshot.InstanceLayerIds.GetValueOrDefault(instance.Id, "");
        }
        _instanceIndex.Invalidate();

        ActiveLayerId = snapshot.ActiveLayerId;
        NormalizeLayers();
    }

    internal SceneLayerDefinition AddLayer(VectorProject project, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!project.OwnsScene(this)) throw new InvalidOperationException("The scene is not owned by this project.");

        NormalizeLayers();
        var layer = new SceneLayerDefinition
        {
            Name = string.IsNullOrWhiteSpace(name) ? $"Layer {_layers.Count + 1:0000}" : name.Trim(),
            ColorArgb = DefaultLayerColor(_layers.Count).ToArgb()
        };
        _layers.Add(layer);
        ActiveLayerId = layer.Id;
        SynchronizeTimelineTracks();
        return layer;
    }

    internal SceneLayerDefinition? AddMaskLayer(
        VectorProject project,
        string contentLayerId,
        string? name = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!project.OwnsScene(this)) throw new InvalidOperationException("The scene is not owned by this project.");

        NormalizeLayers();
        var contentLayer = FindLayer(contentLayerId);
        if (contentLayer?.Kind != SceneLayerKind.Content) return null;

        if (!string.IsNullOrWhiteSpace(contentLayer.MaskLayerId))
        {
            var existing = FindLayer(contentLayer.MaskLayerId);
            if (existing?.Kind == SceneLayerKind.Mask)
            {
                return null;
            }
            contentLayer.MaskLayerId = "";
        }

        var contentIndex = _layers.IndexOf(contentLayer);
        var maskLayer = new SceneLayerDefinition
        {
            Name = string.IsNullOrWhiteSpace(name) ? $"Mask {_layers.Count + 1:0000}" : name.Trim(),
            Kind = SceneLayerKind.Mask,
            ColorArgb = DefaultLayerColor(contentIndex).ToArgb(),
            MaskScene = CreateMaskScene(FrameCount)
        };
        _layers.Insert(Math.Max(0, contentIndex), maskLayer);
        contentLayer.MaskLayerId = maskLayer.Id;
        ActiveLayerId = maskLayer.Id;
        SynchronizeTimelineTracks();
        var track = _timeline.FindTrackByTargetId(maskLayer.Id);
        if (track is not null) _timeline.InsertBlankKeyframe(track.Id, 0);
        return maskLayer;
    }

    internal bool SetLayerMask(string contentLayerId, string maskLayerId)
    {
        NormalizeLayers();
        var contentLayer = FindLayer(contentLayerId);
        var maskLayer = FindLayer(maskLayerId);
        if (contentLayer?.Kind != SceneLayerKind.Content || maskLayer?.Kind != SceneLayerKind.Mask) return false;

        var changed = false;
        foreach (var layer in _layers)
        {
            if (ReferenceEquals(layer, contentLayer)
                || layer.Kind != SceneLayerKind.Content
                || !string.Equals(layer.MaskLayerId, maskLayer.Id, StringComparison.Ordinal))
            {
                continue;
            }

            layer.MaskLayerId = "";
            changed = true;
        }

        if (!string.Equals(contentLayer.MaskLayerId, maskLayer.Id, StringComparison.Ordinal))
        {
            contentLayer.MaskLayerId = maskLayer.Id;
            changed = true;
        }
        return changed;
    }

    internal bool ClearLayerMask(string contentLayerId)
    {
        NormalizeLayers();
        var contentLayer = FindLayer(contentLayerId);
        if (contentLayer?.Kind != SceneLayerKind.Content || string.IsNullOrWhiteSpace(contentLayer.MaskLayerId)) return false;
        contentLayer.MaskLayerId = "";
        return true;
    }

    internal bool MoveLayerOutOfMask(VectorProject project, string contentLayerId)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!project.OwnsScene(this)) throw new InvalidOperationException("The scene is not owned by this project.");

        NormalizeLayers();
        var contentLayer = FindLayer(contentLayerId);
        var maskLayer = contentLayer?.Kind == SceneLayerKind.Content
            ? FindLayer(contentLayer.MaskLayerId)
            : null;
        if (contentLayer is null || maskLayer?.Kind != SceneLayerKind.Mask) return false;

        contentLayer.MaskLayerId = "";
        _layers.Remove(contentLayer);
        var maskIndex = _layers.IndexOf(maskLayer);
        _layers.Insert(Math.Clamp(maskIndex + 1, 0, _layers.Count), contentLayer);
        SynchronizeTimelineTracks();
        return true;
    }

    internal bool MoveLayer(VectorProject project, string layerId, int destinationIndex)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!project.OwnsScene(this)) throw new InvalidOperationException("The scene is not owned by this project.");

        NormalizeLayers();
        var sourceIndex = _layers.FindIndex(layer => string.Equals(layer.Id, layerId, StringComparison.Ordinal));
        destinationIndex = Math.Clamp(destinationIndex, 0, _layers.Count - 1);
        if (sourceIndex < 0 || sourceIndex == destinationIndex) return false;

        var layer = _layers[sourceIndex];
        _layers.RemoveAt(sourceIndex);
        _layers.Insert(destinationIndex, layer);
        SynchronizeTimelineTracks();
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
    }

    internal bool RemoveLayers(VectorProject project, IEnumerable<string> layerIds)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(layerIds);
        if (!project.OwnsScene(this)) throw new InvalidOperationException("The scene is not owned by this project.");

        NormalizeLayers();
        var removedIds = layerIds
            .Where(layerId => FindLayer(layerId) is not null)
            .ToHashSet(StringComparer.Ordinal);
        if (removedIds.Count == 0) return false;

        foreach (var contentLayer in _layers.Where(layer =>
                     layer.Kind == SceneLayerKind.Content
                     && removedIds.Contains(layer.Id)))
        {
            if (!string.IsNullOrWhiteSpace(contentLayer.MaskLayerId)) removedIds.Add(contentLayer.MaskLayerId);
        }

        if (removedIds.Count >= _layers.Count
            || !_layers.Any(layer => layer.Kind == SceneLayerKind.Content && !removedIds.Contains(layer.Id)))
        {
            return false;
        }

        var oldLayers = _layers.ToArray();
        var oldActiveIndex = Array.FindIndex(oldLayers, layer => string.Equals(layer.Id, ActiveLayerId, StringComparison.Ordinal));
        if (_instances.RemoveAll(instance => removedIds.Contains(instance.SceneLayerId)) > 0)
        {
            _instanceIndex.Invalidate();
        }
        _layers.RemoveAll(layer => removedIds.Contains(layer.Id));
        foreach (var contentLayer in _layers.Where(layer => layer.Kind == SceneLayerKind.Content))
        {
            if (removedIds.Contains(contentLayer.MaskLayerId)) contentLayer.MaskLayerId = "";
        }
        if (FindLayer(ActiveLayerId) is null)
        {
            var nearest = oldLayers
                .Select((layer, index) => (layer, index))
                .Where(item => !removedIds.Contains(item.layer.Id))
                .MinBy(item => Math.Abs(item.index - Math.Max(0, oldActiveIndex)));
            ActiveLayerId = nearest.layer.Id;
        }
        SynchronizeTimelineTracks();
        return true;
    }

    internal void AddInstance(VectorProject project, DrawingObjectInstanceDefinition instance)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(instance);
        NormalizeLayers();
        if (!project.OwnsScene(this)
            || !project.ContainsDrawingObject(instance.DrawingObjectId)
            || string.IsNullOrWhiteSpace(instance.Id)
            || _instances.Any(item => string.Equals(item.Id, instance.Id, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("The scene instance reference or identifier is invalid for this project.");
        }

        instance.SceneLayerId = ResolveContentLayerId(instance.SceneLayerId);
        _instances.Add(instance);
        _instanceIndex.Invalidate();
        SynchronizeTimelineTracks();
        var track = _timeline.FindTrackByTargetId(instance.SceneLayerId);
        if (track is not null && !track.EvaluateExposure(0).HasContent)
        {
            _timeline.InsertKeyframe(track.Id, 0);
        }
    }

    internal bool RemoveInstance(DrawingObjectInstanceDefinition instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var removed = _instances.Remove(instance);
        if (removed)
        {
            _instanceIndex.Invalidate();
            SynchronizeTimelineTracks();
        }
        return removed;
    }

    internal bool CanMoveInstancesInLayer(IReadOnlyCollection<string> instanceIds, int direction)
    {
        return _instanceIndex.CreateMovePlan(instanceIds, direction).Count > 0;
    }

    internal bool MoveInstancesInLayer(IReadOnlyCollection<string> instanceIds, int direction)
    {
        var plan = _instanceIndex.CreateMovePlan(instanceIds, direction);
        if (plan.Count == 0) return false;
        foreach (var (index, instance) in plan) _instances[index] = instance;
        _instanceIndex.Invalidate();
        return true;
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

    private void NormalizeLayers()
    {
        var validLayers = new HashSet<string>(StringComparer.Ordinal);
        for (var index = _layers.Count - 1; index >= 0; index--)
        {
            var layer = _layers[index];
            if (string.IsNullOrWhiteSpace(layer.Id) || !validLayers.Add(layer.Id)) _layers.RemoveAt(index);
        }

        if (_layers.Count == 0) _layers.Add(new SceneLayerDefinition { Name = "Layer 0001" });
        foreach (var layer in _layers)
        {
            if (!Enum.IsDefined(layer.Kind)) layer.Kind = SceneLayerKind.Content;
            if (layer.Kind == SceneLayerKind.Mask)
            {
                layer.MaskLayerId = "";
                layer.MaskScene ??= CreateMaskScene(FrameCount);
            }
            else
            {
                layer.MaskScene = null;
                layer.MaskLayerId ??= "";
            }
        }

        if (!_layers.Any(layer => layer.Kind == SceneLayerKind.Content))
        {
            _layers[0].Kind = SceneLayerKind.Content;
            _layers[0].MaskLayerId = "";
            _layers[0].MaskScene = null;
        }

        var maskIds = _layers
            .Where(layer => layer.Kind == SceneLayerKind.Mask)
            .Select(layer => layer.Id)
            .ToHashSet(StringComparer.Ordinal);
        var claimedMasks = new HashSet<string>(StringComparer.Ordinal);
        foreach (var layer in _layers.Where(layer => layer.Kind == SceneLayerKind.Content))
        {
            if (string.IsNullOrWhiteSpace(layer.MaskLayerId)
                || !maskIds.Contains(layer.MaskLayerId)
                || !claimedMasks.Add(layer.MaskLayerId))
            {
                layer.MaskLayerId = "";
            }
        }

        var reassignedInstance = false;
        foreach (var instance in _instances)
        {
            var assignedLayer = FindLayer(instance.SceneLayerId);
            if (assignedLayer?.Kind == SceneLayerKind.Content) continue;

            if (assignedLayer?.Kind == SceneLayerKind.Mask)
            {
                instance.SceneLayerId = ResolveContentLayerId(assignedLayer.Id);
                reassignedInstance = true;
                continue;
            }

            // Legacy scene tracks were keyed by instance ID. Preserve those keys
            // by promoting each unassigned instance to a distinct scene layer.
            var legacyLayer = FindLayer(instance.Id);
            if (legacyLayer is null)
            {
                legacyLayer = new SceneLayerDefinition
                {
                    Id = instance.Id,
                    Name = $"Layer {_layers.Count + 1:0000}"
                };
                _layers.Add(legacyLayer);
            }
            instance.SceneLayerId = legacyLayer.Id;
            reassignedInstance = true;
        }

        if (reassignedInstance) _instanceIndex.Invalidate();

        if (FindLayer(ActiveLayerId) is null) ActiveLayerId = _layers[0].Id;
    }

    private string ResolveContentLayerId(string? requestedLayerId)
    {
        var requested = FindLayer(requestedLayerId ?? "");
        if (requested?.Kind == SceneLayerKind.Content) return requested.Id;
        if (requested?.Kind == SceneLayerKind.Mask && GetMaskContentLayer(requested.Id) is { } linkedContent)
        {
            return linkedContent.Id;
        }

        var requestedIndex = requested is null ? -1 : _layers.IndexOf(requested);
        var active = FindLayer(ActiveLayerId);
        if (active?.Kind == SceneLayerKind.Content) return active.Id;
        if (active?.Kind == SceneLayerKind.Mask && GetMaskContentLayer(active.Id) is { } activeContent)
        {
            return activeContent.Id;
        }

        return _layers
            .Select((layer, index) => (layer, index))
            .Where(item => item.layer.Kind == SceneLayerKind.Content)
            .MinBy(item => requestedIndex < 0 ? item.index : Math.Abs(item.index - requestedIndex))
            .layer.Id;
    }

    private bool MaskSceneHasContent(string maskLayerId, int frame)
    {
        var maskScene = FindMaskScene(maskLayerId);
        if (maskScene is null || frame < 0 || frame >= maskScene.FrameCount) return false;
        for (var objectIndex = 0; objectIndex < maskScene.ObjectCount; objectIndex++)
        {
            if (maskScene.IsObjectActive(objectIndex, frame)) return true;
        }
        return false;
    }

    private bool ApplyMaskTimelineMutation(
        string maskLayerId,
        Func<VectorScene, AnimationTimelineTrack, (bool Success, bool Changed)> mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        SynchronizeTimelineTracks();
        var maskScene = FindMaskScene(maskLayerId);
        var outerTrack = _timeline.FindTrackByTargetId(maskLayerId);
        if (maskScene is null || outerTrack is null || maskScene.LayerCount <= 0) return false;

        var outerSnapshot = _timeline.CreateSnapshot();
        var maskSnapshot = maskScene.CreateSnapshot();
        try
        {
            (bool Success, bool Changed) result;
            using (_timeline.BeginBatchUpdate())
            using (maskScene.Timeline.BeginBatchUpdate())
            {
                result = mutation(maskScene, outerTrack);
            }

            if (result.Success) return result.Changed;
        }
        catch
        {
            _timeline.RestoreSnapshot(outerSnapshot);
            maskScene.RestoreSnapshot(maskSnapshot);
            throw;
        }

        _timeline.RestoreSnapshot(outerSnapshot);
        maskScene.RestoreSnapshot(maskSnapshot);
        return false;
    }

    private bool SetMaskTrackKeyframeKind(
        AnimationTimelineTrack outerTrack,
        int frame,
        TimelineKeyframeKind kind)
    {
        return kind == TimelineKeyframeKind.Populated
            ? _timeline.InsertKeyframe(outerTrack.Id, frame)
            : _timeline.InsertBlankKeyframe(outerTrack.Id, frame);
    }

    private static VectorScene CreateMaskScene(int frameCount)
    {
        var scene = new VectorScene();
        scene.CreateEmpty(frameCount: Math.Max(1, frameCount));
        scene.LayerNames[0] = "Mask";
        return scene;
    }

    private static VectorScene? RestoreMaskScene(SceneLayerKind kind, VectorSceneSnapshot? snapshot)
    {
        if (kind != SceneLayerKind.Mask) return null;
        var scene = CreateMaskScene(AnimationTimeline.DefaultDuration);
        if (snapshot is not null) scene.RestoreSnapshot(snapshot);
        return scene;
    }

    private void SynchronizeMaskSceneDurations(int frameCount)
    {
        frameCount = Math.Max(1, frameCount);
        foreach (var layer in _layers)
        {
            if (layer.Kind != SceneLayerKind.Mask) continue;
            var maskScene = layer.MaskScene ??= CreateMaskScene(frameCount);
            maskScene.SynchronizeTimelineTracks();
            using var batchUpdate = maskScene.Timeline.BeginBatchUpdate();
            foreach (var track in maskScene.Timeline.Tracks)
            {
                maskScene.Timeline.SetTrackDuration(track.Id, frameCount);
            }
            maskScene.EditFrame = Math.Clamp(maskScene.EditFrame, 0, frameCount - 1);
        }
    }

    private static Color DefaultLayerColor(int index)
    {
        var colors = new[]
        {
            Color.FromArgb(79, 195, 247),
            Color.FromArgb(255, 183, 77),
            Color.FromArgb(129, 199, 132),
            Color.FromArgb(244, 143, 177),
            Color.FromArgb(179, 157, 219),
            Color.FromArgb(128, 203, 196),
            Color.FromArgb(255, 138, 128),
            Color.FromArgb(255, 241, 118)
        };
        return colors[Math.Abs(index) % colors.Length];
    }
}
