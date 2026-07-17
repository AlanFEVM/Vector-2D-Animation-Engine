namespace VectorAnimationEngine;

internal sealed class SceneLayerDefinition
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Layer";
    public bool Visible { get; set; } = true;
    public int ColorArgb { get; set; } = Color.FromArgb(79, 195, 247).ToArgb();
}

internal sealed class SceneLayerSnapshot
{
    public SceneLayerSnapshotItem[] Layers { get; init; } = [];
    public string ActiveLayerId { get; init; } = "";
    public Dictionary<string, string> InstanceLayerIds { get; init; } = new(StringComparer.Ordinal);
}

internal sealed record SceneLayerSnapshotItem(string Id, string Name, bool Visible, int ColorArgb);

internal sealed class SceneDefinition : ITimelineContext, ICompositionDefinition
{
    private readonly AnimationTimeline _timeline = new();
    private readonly List<SceneLayerDefinition> _layers = [];
    private readonly List<DrawingObjectInstanceDefinition> _instances = [];
    private readonly IReadOnlyList<SceneLayerDefinition> _layerView;
    private readonly IReadOnlyList<DrawingObjectInstanceDefinition> _instanceView;

    public SceneDefinition(int initialFrameCount = AnimationTimeline.DefaultDuration)
    {
        _layerView = _layers.AsReadOnly();
        _instanceView = _instances.AsReadOnly();
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
        _timeline.SynchronizeTracks(Layers.Select(layer => layer.Id), FrameCount, populateNewTracks: false);
        foreach (var track in _timeline.Tracks)
        {
            if (track.Keyframes.Count == 0 || track.Keyframes[0].Frame > 0)
            {
                _timeline.InsertBlankKeyframe(track.Id, 0);
            }
        }
    }

    public SceneLayerDefinition? FindLayer(string layerId)
    {
        return Layers.FirstOrDefault(layer => string.Equals(layer.Id, layerId, StringComparison.Ordinal));
    }

    public IReadOnlyList<DrawingObjectInstanceDefinition> InstancesInLayer(string layerId)
    {
        return Instances
            .Where(instance => string.Equals(instance.SceneLayerId, layerId, StringComparison.Ordinal))
            .ToArray();
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
            Layers = _layers.Select(layer => new SceneLayerSnapshotItem(layer.Id, layer.Name, layer.Visible, layer.ColorArgb)).ToArray(),
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
                ColorArgb = layer.ColorArgb
            });
        }

        foreach (var instance in _instances)
        {
            instance.SceneLayerId = snapshot.InstanceLayerIds.GetValueOrDefault(instance.Id, "");
        }

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

        instance.SceneLayerId = FindLayer(instance.SceneLayerId)?.Id ?? ActiveLayerId;
        _instances.Add(instance);
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
        if (removed) SynchronizeTimelineTracks();
        return removed;
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

        foreach (var instance in _instances)
        {
            if (FindLayer(instance.SceneLayerId) is not null) continue;

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
        }

        if (_layers.Count == 0) _layers.Add(new SceneLayerDefinition { Name = "Layer 0001" });
        if (FindLayer(ActiveLayerId) is null) ActiveLayerId = _layers[0].Id;
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
