namespace VectorAnimationEngine;

internal enum SceneDimension
{
    TwoD,
    ThreeD
}

internal enum CameraProjection
{
    Orthographic,
    Perspective
}

internal sealed class SceneCameraDefinition
{
    public string Name { get; set; } = "Main Camera";
    public CameraProjection Projection { get; set; } = CameraProjection.Orthographic;
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; } = -1000;
    public float Depth { get; set; } = 1000;
    public float OrthographicSize { get; set; } = 28000;
    public float FieldOfViewDegrees { get; set; } = 60;
}

internal sealed partial class VectorProject
{
    private readonly List<SceneDefinition> _scenes = [];
    private readonly List<DrawingObjectDefinition> _drawingObjects = [];
    private readonly List<ProjectAssetFolder> _assetFolders = [];
    private readonly IReadOnlyList<SceneDefinition> _sceneView;
    private readonly IReadOnlyList<DrawingObjectDefinition> _drawingObjectView;
    private readonly IReadOnlyList<ProjectAssetFolder> _assetFolderView;
    private decimal _playbackFps = 30m;
    private bool _loopPlayback = true;
    private int _playbackStartFrame;
    private int _playbackEndFrame = 239;

    public VectorProject()
    {
        _sceneView = _scenes.AsReadOnly();
        _drawingObjectView = _drawingObjects.AsReadOnly();
        _assetFolderView = _assetFolders.AsReadOnly();
        _assetTagView = _assetTags.AsReadOnly();
        AddScene("Scene 001");
        AddDrawingObject("Drawing Object 001");
    }

    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Untitled Project";
    public decimal PlaybackFps => _playbackFps;
    public bool LoopPlayback => _loopPlayback;
    public int PlaybackStartFrame => _playbackStartFrame;
    public int PlaybackEndFrame => _playbackEndFrame;
    public IReadOnlyList<SceneDefinition> Scenes => _sceneView;
    public IReadOnlyList<DrawingObjectDefinition> DrawingObjects => _drawingObjectView;
    public IReadOnlyList<ProjectAssetFolder> AssetFolders => _assetFolderView;
    public event EventHandler? Changed;

    public bool TrySetPlaybackSettings(
        decimal playbackFps,
        bool loopPlayback,
        int playbackStartFrame,
        int playbackEndFrame)
    {
        var normalized = NormalizePlaybackSettings(
            playbackFps,
            loopPlayback,
            playbackStartFrame,
            playbackEndFrame);
        if (_playbackFps == normalized.PlaybackFps
            && _loopPlayback == normalized.LoopPlayback
            && _playbackStartFrame == normalized.PlaybackStartFrame
            && _playbackEndFrame == normalized.PlaybackEndFrame)
        {
            return false;
        }

        ApplyPlaybackSettings(normalized);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool TrySetPlaybackFps(decimal playbackFps) => TrySetPlaybackSettings(
        playbackFps,
        LoopPlayback,
        PlaybackStartFrame,
        PlaybackEndFrame);

    public bool TrySetLoopPlayback(bool loopPlayback) => TrySetPlaybackSettings(
        PlaybackFps,
        loopPlayback,
        PlaybackStartFrame,
        PlaybackEndFrame);

    public bool TrySetPlaybackRange(int playbackStartFrame, int playbackEndFrame) => TrySetPlaybackSettings(
        PlaybackFps,
        LoopPlayback,
        playbackStartFrame,
        playbackEndFrame);

    public SceneDefinition AddScene(string? name = null)
    {
        var index = _scenes.Count + 1;
        var scene = new SceneDefinition(initialFrameCount: 1)
        {
            Name = string.IsNullOrWhiteSpace(name) ? $"Scene {index:000}" : name,
            Detail = "2D scene composition",
            Dimension = SceneDimension.TwoD,
            Camera = new SceneCameraDefinition
            {
                Projection = CameraProjection.Orthographic,
                Depth = 1000
            }
        };
        _scenes.Add(scene);
        Changed?.Invoke(this, EventArgs.Empty);
        return scene;
    }

    public DrawingObjectDefinition AddDrawingObject(string? name = null)
    {
        var index = _drawingObjects.Count + 1;
        var drawingObject = new DrawingObjectDefinition
        {
            Name = string.IsNullOrWhiteSpace(name) ? $"Drawing Object {index:000}" : name,
            Detail = "Reusable drawing object"
        };
        drawingObject.Scene.CreateEmpty(frameCount: 1);
        _drawingObjects.Add(drawingObject);
        Changed?.Invoke(this, EventArgs.Empty);
        return drawingObject;
    }

    public bool TryRenameDrawingObject(string drawingObjectId, string? name)
    {
        var drawingObject = FindDrawingObject(drawingObjectId);
        var normalized = name?.Trim();
        if (drawingObject is null || string.IsNullOrWhiteSpace(normalized)) return false;
        if (string.Equals(drawingObject.Name, normalized, StringComparison.Ordinal)) return true;

        drawingObject.Name = normalized;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool TrySetDrawingObjectAnchor(string drawingObjectId, PointF anchor)
    {
        var drawingObject = FindDrawingObject(drawingObjectId);
        if (drawingObject is null || !float.IsFinite(anchor.X) || !float.IsFinite(anchor.Y)) return false;

        var normalized = new PointF(
            Math.Clamp(VectorUnits.Quantize(anchor.X), -5_000_000, 5_000_000),
            Math.Clamp(VectorUnits.Quantize(anchor.Y), -5_000_000, 5_000_000));
        var previous = drawingObject.Anchor;
        if (previous == normalized) return true;

        var sourceDelta = new PointF(normalized.X - previous.X, normalized.Y - previous.Y);
        var usages = _scenes
            .SelectMany(scene => scene.Instances)
            .Concat(_drawingObjects.SelectMany(item => item.Instances))
            .Where(instance => string.Equals(instance.DrawingObjectId, drawingObjectId, StringComparison.Ordinal))
            .ToArray();
        var compensation = new InstanceAnchorCompensation[usages.Length];
        for (var index = 0; index < usages.Length; index++)
        {
            if (!usages[index].TryPlanAnchorCompensation(sourceDelta, out compensation[index])) return false;
        }

        for (var index = 0; index < usages.Length; index++)
        {
            usages[index].ApplyAnchorCompensation(compensation[index]);
        }
        drawingObject.SetAnchor(normalized);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool TryDuplicateDrawingObject(string drawingObjectId, out DrawingObjectDefinition? duplicate)
    {
        duplicate = null;
        var source = FindDrawingObject(drawingObjectId);
        if (source is null) return false;

        duplicate = DuplicateDrawingObjectCore(source, source.AssetFolderId);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool TryAddAssetFolder(string? name, string? parentFolderId, out ProjectAssetFolder? folder)
    {
        folder = null;
        var parentId = NormalizeAssetFolderId(parentFolderId);
        if (parentId.Length > 0 && FindAssetFolder(parentId) is null) return false;

        var normalizedName = string.IsNullOrWhiteSpace(name) ? "Folder" : name.Trim();
        folder = new ProjectAssetFolder
        {
            Name = normalizedName,
            ParentFolderId = parentId
        };
        _assetFolders.Add(folder);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool TryRenameAssetFolder(string folderId, string? name)
    {
        var folder = FindAssetFolder(folderId);
        var normalizedName = name?.Trim();
        if (folder is null || string.IsNullOrWhiteSpace(normalizedName)) return false;
        if (string.Equals(folder.Name, normalizedName, StringComparison.Ordinal)) return true;

        folder.Name = normalizedName;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool TryMoveDrawingObjectToAssetFolder(string drawingObjectId, string? folderId)
    {
        var drawingObject = FindDrawingObject(drawingObjectId);
        var targetFolderId = NormalizeAssetFolderId(folderId);
        if (drawingObject is null || targetFolderId.Length > 0 && FindAssetFolder(targetFolderId) is null) return false;
        if (string.Equals(drawingObject.AssetFolderId, targetFolderId, StringComparison.Ordinal)) return true;

        drawingObject.AssetFolderId = targetFolderId;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool TryMoveAssetFolder(string folderId, string? parentFolderId)
    {
        var folder = FindAssetFolder(folderId);
        var targetParentId = NormalizeAssetFolderId(parentFolderId);
        if (folder is null
            || targetParentId.Length > 0 && FindAssetFolder(targetParentId) is null
            || string.Equals(folder.Id, targetParentId, StringComparison.Ordinal)
            || IsAssetFolderDescendant(targetParentId, folder.Id))
        {
            return false;
        }
        if (string.Equals(folder.ParentFolderId, targetParentId, StringComparison.Ordinal)) return true;

        folder.ParentFolderId = targetParentId;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool TryDuplicateAssetFolder(string folderId, out ProjectAssetFolder? duplicateRoot)
    {
        duplicateRoot = null;
        var sourceRoot = FindAssetFolder(folderId);
        if (sourceRoot is null) return false;

        var sourceFolders = AssetFolderSubtree(sourceRoot.Id);
        var folderIdMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var sourceFolder in sourceFolders)
        {
            var copy = new ProjectAssetFolder
            {
                Name = ReferenceEquals(sourceFolder, sourceRoot)
                    ? NextAssetFolderCopyName(sourceFolder.Name, sourceFolder.ParentFolderId)
                    : sourceFolder.Name,
                ParentFolderId = folderIdMap.GetValueOrDefault(sourceFolder.ParentFolderId, sourceFolder.ParentFolderId)
            };
            _assetFolders.Add(copy);
            folderIdMap[sourceFolder.Id] = copy.Id;
            if (ReferenceEquals(sourceFolder, sourceRoot)) duplicateRoot = copy;
        }

        var sourceFolderIds = folderIdMap.Keys.ToHashSet(StringComparer.Ordinal);
        var sourceObjects = _drawingObjects
            .Where(item => sourceFolderIds.Contains(item.AssetFolderId))
            .ToArray();
        var drawingObjectIdMap = sourceObjects.ToDictionary(
            source => source.Id,
            _ => Guid.NewGuid().ToString("N"),
            StringComparer.Ordinal);
        var copies = new DrawingObjectDefinition[sourceObjects.Length];
        for (var index = 0; index < sourceObjects.Length; index++)
        {
            var source = sourceObjects[index];
            copies[index] = CreateDrawingObjectDuplicateShell(
                source,
                folderIdMap[source.AssetFolderId],
                drawingObjectIdMap[source.Id]);
        }
        for (var index = 0; index < sourceObjects.Length; index++)
        {
            PopulateDrawingObjectDuplicate(sourceObjects[index], copies[index], drawingObjectIdMap);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return duplicateRoot is not null;
    }

    private DrawingObjectDefinition DuplicateDrawingObjectCore(
        DrawingObjectDefinition source,
        string assetFolderId,
        string? duplicateId = null,
        IReadOnlyDictionary<string, string>? drawingObjectIdMap = null)
    {
        var duplicate = CreateDrawingObjectDuplicateShell(source, assetFolderId, duplicateId);
        PopulateDrawingObjectDuplicate(source, duplicate, drawingObjectIdMap);
        return duplicate;
    }

    private DrawingObjectDefinition CreateDrawingObjectDuplicateShell(
        DrawingObjectDefinition source,
        string assetFolderId,
        string? duplicateId = null)
    {
        var sceneSnapshot = source.Scene.CreateSnapshot();
        var duplicate = new DrawingObjectDefinition
        {
            Id = duplicateId ?? Guid.NewGuid().ToString("N"),
            Name = NextDrawingObjectCopyName(source.Name),
            Kind = source.Kind,
            Detail = source.Detail,
            AssetFolderId = assetFolderId
        };
        duplicate.SetAnchor(source.Anchor);
        duplicate.ReplaceAssetTagIds(source.AssetTagIds);
        duplicate.Scene.RestoreSnapshot(sceneSnapshot);
        _drawingObjects.Add(duplicate);
        return duplicate;
    }

    private void PopulateDrawingObjectDuplicate(
        DrawingObjectDefinition source,
        DrawingObjectDefinition duplicate,
        IReadOnlyDictionary<string, string>? drawingObjectIdMap)
    {
        var sourceTimeline = source.Timeline.CreateSnapshot();
        var instanceIdMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var sourceInstance in source.Instances)
        {
            var copy = CloneDrawingObjectInstance(sourceInstance, drawingObjectIdMap);
            duplicate.AddInstance(this, copy);
            instanceIdMap[sourceInstance.Id] = copy.Id;
        }

        if (instanceIdMap.Count > 0)
        {
            duplicate.Timeline.RestoreSnapshot(new AnimationTimelineSnapshot
            {
                Tracks = sourceTimeline.Tracks.Select(track => new AnimationTimelineTrackSnapshot
                {
                    Id = track.Id,
                    TargetId = instanceIdMap.GetValueOrDefault(track.TargetId, track.TargetId),
                    Duration = track.Duration,
                    Keyframes = track.Keyframes.ToArray(),
                    Tweens = track.Tweens.ToArray()
                }).ToArray()
            });
            duplicate.SynchronizeTimelineTracks();
        }
    }

    public bool TryRemoveDrawingObject(string drawingObjectId, out DrawingObjectDefinition? removed)
    {
        removed = null;
        if (_drawingObjects.Count <= 1) return false;

        var target = FindDrawingObject(drawingObjectId);
        if (target is null) return false;

        foreach (var scene in _scenes)
        {
            foreach (var instance in scene.Instances
                         .Where(instance => string.Equals(instance.DrawingObjectId, drawingObjectId, StringComparison.Ordinal))
                         .ToArray())
            {
                scene.RemoveInstance(instance);
            }
        }

        foreach (var drawingObject in _drawingObjects)
        {
            drawingObject.RemoveInstancesReferencing(drawingObjectId);
        }

        if (!_drawingObjects.Remove(target)) return false;
        removed = target;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool CanContainDrawingObject(string containerId, string childId)
    {
        if (string.IsNullOrWhiteSpace(containerId)
            || string.IsNullOrWhiteSpace(childId)
            || string.Equals(containerId, childId, StringComparison.Ordinal))
        {
            return false;
        }

        var container = FindDrawingObject(containerId);
        var child = FindDrawingObject(childId);
        return container is not null
            && child is not null
            && !DependsOn(child.Id, container.Id, new HashSet<string>(StringComparer.Ordinal));
    }

    public bool TryAddSceneInstance(
        string sceneId,
        string drawingObjectId,
        PointF position,
        float z,
        string? sceneLayerId,
        out DrawingObjectInstanceDefinition? instance)
    {
        instance = null;
        var scene = FindScene(sceneId);
        var drawingObject = FindDrawingObject(drawingObjectId);
        if (scene is null || drawingObject is null) return false;

        instance = new SceneObjectInstanceDefinition
        {
            DrawingObjectId = drawingObject.Id,
            SceneLayerId = sceneLayerId ?? scene.ActiveLayerId,
            Name = $"{drawingObject.Name} Instance {scene.Instances.Count + 1:000}",
            X = position.X,
            Y = position.Y,
            Z = z
        };
        scene.AddInstance(this, instance);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool TryAddSceneInstance(
        string sceneId,
        string drawingObjectId,
        PointF position,
        float z,
        out DrawingObjectInstanceDefinition? instance)
    {
        return TryAddSceneInstance(sceneId, drawingObjectId, position, z, sceneLayerId: null, out instance);
    }

    public bool TryAddSceneLayer(string sceneId, out SceneLayerDefinition? layer)
    {
        layer = null;
        var scene = FindScene(sceneId);
        if (scene is null) return false;

        layer = scene.AddLayer(this);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool TryMoveSceneLayer(string sceneId, string layerId, int destinationIndex)
    {
        var scene = FindScene(sceneId);
        if (scene is null || !scene.MoveLayer(this, layerId, destinationIndex)) return false;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool TryRemoveSceneLayers(string sceneId, IEnumerable<string> layerIds)
    {
        var scene = FindScene(sceneId);
        if (scene is null || !scene.RemoveLayers(this, layerIds)) return false;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool TrySetSceneLayerColor(string sceneId, string layerId, Color color)
    {
        var scene = FindScene(sceneId);
        if (scene is null || !scene.SetLayerColor(layerId, color)) return false;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool TrySetSceneLayerBlendMode(string sceneId, string layerId, LayerBlendMode blendMode)
    {
        var scene = FindScene(sceneId);
        if (scene is null || !scene.SetLayerBlendMode(layerId, blendMode)) return false;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool TrySetSceneLayerOutline(string sceneId, string layerId, bool outline)
    {
        var scene = FindScene(sceneId);
        if (scene is null || !scene.SetLayerOutline(layerId, outline)) return false;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool TryRenameSceneLayer(string sceneId, string layerId, string? name)
    {
        var scene = FindScene(sceneId);
        if (scene is null || !scene.RenameLayer(this, layerId, name)) return false;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    internal bool OwnsDrawingObject(DrawingObjectDefinition drawingObject)
    {
        ArgumentNullException.ThrowIfNull(drawingObject);
        return _drawingObjects.Count(item => ReferenceEquals(item, drawingObject)) == 1
            && _drawingObjects.Count(item => string.Equals(item.Id, drawingObject.Id, StringComparison.Ordinal)) == 1;
    }

    internal bool OwnsScene(SceneDefinition scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        return _scenes.Count(item => ReferenceEquals(item, scene)) == 1
            && _scenes.Count(item => string.Equals(item.Id, scene.Id, StringComparison.Ordinal)) == 1;
    }

    internal bool ContainsDrawingObject(string drawingObjectId) => FindDrawingObject(drawingObjectId) is not null;

    public bool TryAddDrawingObjectInstance(
        string containerId,
        string childId,
        PointF position,
        string? sceneLayerId,
        out DrawingObjectInstanceDefinition? instance)
    {
        instance = null;
        if (!CanContainDrawingObject(containerId, childId)) return false;

        var container = FindDrawingObject(containerId)!;
        var child = FindDrawingObject(childId)!;
        instance = new DrawingObjectInstanceDefinition
        {
            DrawingObjectId = child.Id,
            SceneLayerId = sceneLayerId ?? container.Scene.ResolveInstanceLayerId(null),
            Name = $"{child.Name} Instance {container.Instances.Count + 1:000}",
            X = position.X,
            Y = position.Y
        };
        container.AddInstance(this, instance);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool TryAddDrawingObjectInstance(
        string containerId,
        string childId,
        PointF position,
        out DrawingObjectInstanceDefinition? instance)
    {
        return TryAddDrawingObjectInstance(containerId, childId, position, sceneLayerId: null, out instance);
    }

    public bool CanMoveDrawingObjectInstancesInLayer(
        string containerId,
        IReadOnlyCollection<string> instanceIds,
        int direction)
    {
        var container = FindDrawingObject(containerId);
        return container is not null && container.CanMoveInstancesInLayer(instanceIds, direction);
    }

    public bool TryMoveDrawingObjectInstancesInLayer(
        string containerId,
        IReadOnlyCollection<string> instanceIds,
        int direction)
    {
        var container = FindDrawingObject(containerId);
        if (container is null || !container.MoveInstancesInLayer(instanceIds, direction)) return false;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool TryRemoveDrawingObjectLayers(string drawingObjectId, IEnumerable<string> layerIds)
    {
        var drawingObject = FindDrawingObject(drawingObjectId);
        if (drawingObject is null || !drawingObject.RemoveLayers(this, layerIds)) return false;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool TryRemoveDrawingObjectInstance(
        string containerId,
        string instanceId,
        out DrawingObjectInstanceDefinition? removed)
    {
        removed = null;
        if (string.IsNullOrWhiteSpace(containerId) || string.IsNullOrWhiteSpace(instanceId)) return false;

        var container = FindDrawingObject(containerId);
        var instance = container?.Instances.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, instanceId, StringComparison.Ordinal));
        if (container is null || instance is null || !container.RemoveInstance(instance)) return false;

        removed = instance;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public static VectorProject CreateEmpty() => new();

    internal ProjectRestartSnapshot CreateRestartSnapshot()
    {
        return new ProjectRestartSnapshot
        {
            Id = Id,
            Name = Name,
            PlaybackFps = PlaybackFps,
            LoopPlayback = LoopPlayback,
            PlaybackStartFrame = PlaybackStartFrame,
            PlaybackEndFrame = PlaybackEndFrame,
            AssetTags = _assetTags
                .Select(tag => new ProjectAssetTagRestartSnapshot
                {
                    Id = tag.Id,
                    Name = tag.Name,
                    ColorArgb = tag.ColorArgb
                })
                .ToArray(),
            AssetFolders = _assetFolders
                .Select(folder => new ProjectAssetFolderRestartSnapshot
                {
                    Id = folder.Id,
                    Name = folder.Name,
                    ParentFolderId = folder.ParentFolderId,
                    CreatedAt = folder.CreatedAt
                })
                .ToArray(),
            DrawingObjects = _drawingObjects
                .Select(drawingObject => new DrawingObjectRestartSnapshot
                {
                    Id = drawingObject.Id,
                    Name = drawingObject.Name,
                    Kind = drawingObject.Kind,
                    Detail = drawingObject.Detail,
                    AssetFolderId = drawingObject.AssetFolderId,
                    AssetTagIds = drawingObject.AssetTagIds.ToArray(),
                    AnchorX = drawingObject.Anchor.X,
                    AnchorY = drawingObject.Anchor.Y,
                    CreatedAt = drawingObject.CreatedAt,
                    Scene = drawingObject.Scene.CreateSnapshot(),
                    Instances = drawingObject.Instances.Select(CreateInstanceRestartSnapshot).ToArray()
                })
                .ToArray(),
            Scenes = _scenes
                .Select(scene => new SceneRestartSnapshot
                {
                    Id = scene.Id,
                    Name = scene.Name,
                    Detail = scene.Detail,
                    Dimension = scene.Dimension,
                    Camera = CloneCamera(scene.Camera),
                    CreatedAt = scene.CreatedAt,
                    Layers = scene.CreateLayerSnapshot(),
                    Instances = scene.Instances.Select(CreateInstanceRestartSnapshot).ToArray(),
                    Timeline = scene.Timeline.CreateSnapshot()
                })
                .ToArray()
        };
    }

    internal static VectorProject RestoreRestartSnapshot(ProjectRestartSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.DrawingObjects.Length == 0 || snapshot.Scenes.Length == 0)
        {
            throw new InvalidOperationException("The editor restart snapshot has no project roots.");
        }

        var project = new VectorProject
        {
            Id = IsValidRestartId(snapshot.Id) ? snapshot.Id : Guid.NewGuid().ToString("N")
        };
        project._drawingObjects.Clear();
        project._scenes.Clear();
        project._assetFolders.Clear();
        project._assetTags.Clear();
        project.Name = string.IsNullOrWhiteSpace(snapshot.Name) ? "Untitled Project" : snapshot.Name;
        project.ApplyPlaybackSettings(NormalizePlaybackSettings(
            snapshot.PlaybackFps,
            snapshot.LoopPlayback,
            snapshot.PlaybackStartFrame,
            snapshot.PlaybackEndFrame));

        project.RestoreAssetFolders(snapshot.AssetFolders ?? []);
        project.RestoreAssetTags(snapshot.AssetTags ?? []);
        var assetTagIds = project._assetTags.Select(tag => tag.Id).ToHashSet(StringComparer.Ordinal);

        var drawingSnapshots = snapshot.DrawingObjects
            .Where(item => IsValidRestartId(item.Id))
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.Single())
            .ToArray();
        var sceneSnapshots = snapshot.Scenes
            .Where(item => IsValidRestartId(item.Id))
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.Single())
            .ToArray();
        if (drawingSnapshots.Length == 0 || sceneSnapshots.Length == 0)
        {
            throw new InvalidOperationException("The editor restart snapshot has invalid root identifiers.");
        }

        foreach (var item in drawingSnapshots)
        {
            var drawingObject = new DrawingObjectDefinition
            {
                Id = item.Id,
                Name = item.Name,
                Kind = item.Kind,
                Detail = item.Detail,
                AssetFolderId = project.FindAssetFolder(item.AssetFolderId) is null ? "" : item.AssetFolderId,
                CreatedAt = item.CreatedAt
            };
            if (float.IsFinite(item.AnchorX) && float.IsFinite(item.AnchorY))
            {
                drawingObject.SetAnchor(new PointF(
                    Math.Clamp(item.AnchorX, -5_000_000, 5_000_000),
                    Math.Clamp(item.AnchorY, -5_000_000, 5_000_000)));
            }
            drawingObject.ReplaceAssetTagIds((item.AssetTagIds ?? []).Where(assetTagIds.Contains));
            project._drawingObjects.Add(drawingObject);
        }

        for (var index = 0; index < drawingSnapshots.Length; index++)
        {
            project._drawingObjects[index].Scene.RestoreSnapshot(drawingSnapshots[index].Scene);
        }

        foreach (var item in sceneSnapshots)
        {
            project._scenes.Add(new SceneDefinition
            {
                Id = item.Id,
                Name = item.Name,
                Detail = item.Detail,
                Dimension = item.Dimension,
                Camera = CloneCamera(item.Camera),
                CreatedAt = item.CreatedAt
            });
        }

        for (var index = 0; index < sceneSnapshots.Length; index++)
        {
            project._scenes[index].RestoreLayerSnapshot(sceneSnapshots[index].Layers);
        }

        for (var index = 0; index < drawingSnapshots.Length; index++)
        {
            var drawingObject = project._drawingObjects[index];
            foreach (var instance in drawingSnapshots[index].Instances)
            {
                if (!TryCreateRestartInstance(instance, out var restored))
                {
                    throw new InvalidOperationException("The editor restart snapshot contains an invalid drawing-object instance.");
                }
                drawingObject.AddInstance(project, restored);
            }
        }

        for (var index = 0; index < sceneSnapshots.Length; index++)
        {
            var scene = project._scenes[index];
            foreach (var instance in sceneSnapshots[index].Instances)
            {
                if (!TryCreateRestartInstance(instance, out var restored))
                {
                    throw new InvalidOperationException("The editor restart snapshot contains an invalid scene instance.");
                }
                var sceneInstance = new SceneObjectInstanceDefinition
                {
                    Id = restored.Id,
                    DrawingObjectId = restored.DrawingObjectId,
                    SceneLayerId = restored.SceneLayerId,
                    Name = restored.Name,
                    Visible = restored.Visible,
                    X = restored.X,
                    Y = restored.Y,
                    Z = restored.Z,
                    RotationX = restored.RotationX,
                    RotationY = restored.RotationY,
                    RotationZ = restored.RotationZ,
                    SkewX = restored.SkewX,
                    SkewY = restored.SkewY,
                    ScaleX = restored.ScaleX,
                    ScaleY = restored.ScaleY,
                    ScaleZ = restored.ScaleZ,
                    Alpha = restored.Alpha,
                    TintArgb = restored.TintArgb,
                    PlaybackFps = restored.PlaybackFps,
                    PlaybackMode = restored.PlaybackMode,
                    HoldFrame = restored.HoldFrame
                };
                sceneInstance.RestoreStateKeyframes(restored.StateKeyframes);
                scene.AddInstance(project, sceneInstance);
            }
        }

        for (var index = 0; index < drawingSnapshots.Length; index++)
        {
            var timeline = drawingSnapshots[index].Scene.Timeline;
            if (timeline is not null)
            {
                project._drawingObjects[index].Timeline.RestoreSnapshot(timeline);
            }
            project._drawingObjects[index].SynchronizeTimelineTracks();
        }

        for (var index = 0; index < sceneSnapshots.Length; index++)
        {
            project._scenes[index].Timeline.RestoreSnapshot(sceneSnapshots[index].Timeline);
            project._scenes[index].SynchronizeTimelineTracks();
        }

        return project;
    }

    private static InstanceRestartSnapshot CreateInstanceRestartSnapshot(DrawingObjectInstanceDefinition instance)
    {
        return new InstanceRestartSnapshot
        {
            Id = instance.Id,
            DrawingObjectId = instance.DrawingObjectId,
            SceneLayerId = instance.SceneLayerId,
            Name = instance.Name,
            Visible = instance.Visible,
            X = instance.X,
            Y = instance.Y,
            Z = instance.Z,
            RotationX = instance.RotationX,
            RotationY = instance.RotationY,
            RotationZ = instance.RotationZ,
            SkewX = instance.SkewX,
            SkewY = instance.SkewY,
            ScaleX = instance.ScaleX,
            ScaleY = instance.ScaleY,
            ScaleZ = instance.ScaleZ,
            Alpha = instance.Alpha,
            TintArgb = instance.TintArgb,
            PlaybackFps = instance.PlaybackFps,
            PlaybackMode = instance.PlaybackMode,
            HoldFrame = instance.HoldFrame,
            StateKeyframes = instance.StateKeyframes.ToArray()
        };
    }

    private static bool TryCreateRestartInstance(InstanceRestartSnapshot snapshot, out DrawingObjectInstanceDefinition instance)
    {
        instance = null!;
        if (!IsValidRestartId(snapshot.Id) || !IsValidRestartId(snapshot.DrawingObjectId)) return false;
        instance = new DrawingObjectInstanceDefinition
        {
            Id = snapshot.Id,
            DrawingObjectId = snapshot.DrawingObjectId,
            SceneLayerId = snapshot.SceneLayerId,
            Name = snapshot.Name,
            Visible = snapshot.Visible,
            X = snapshot.X,
            Y = snapshot.Y,
            Z = snapshot.Z,
            RotationX = snapshot.RotationX,
            RotationY = snapshot.RotationY,
            RotationZ = snapshot.RotationZ,
            SkewX = snapshot.SkewX,
            SkewY = snapshot.SkewY,
            ScaleX = snapshot.ScaleX,
            ScaleY = snapshot.ScaleY,
            ScaleZ = snapshot.ScaleZ,
            Alpha = snapshot.Alpha,
            TintArgb = snapshot.TintArgb,
            PlaybackFps = snapshot.PlaybackFps,
            PlaybackMode = snapshot.PlaybackMode,
            HoldFrame = snapshot.HoldFrame
        };
        if (snapshot.StateKeyframes.Length > 0) instance.RestoreStateKeyframes(snapshot.StateKeyframes);
        else instance.RestorePositionKeyframes(snapshot.PositionKeyframes);
        return true;
    }

    private static SceneCameraDefinition CloneCamera(SceneCameraDefinition source)
    {
        return new SceneCameraDefinition
        {
            Name = source.Name,
            Projection = source.Projection,
            X = source.X,
            Y = source.Y,
            Z = source.Z,
            Depth = source.Depth,
            OrthographicSize = source.OrthographicSize,
            FieldOfViewDegrees = source.FieldOfViewDegrees
        };
    }

    private static bool IsValidRestartId(string? id) => !string.IsNullOrWhiteSpace(id);

    private void ApplyPlaybackSettings(ProjectPlaybackSettings settings)
    {
        _playbackFps = settings.PlaybackFps;
        _loopPlayback = settings.LoopPlayback;
        _playbackStartFrame = settings.PlaybackStartFrame;
        _playbackEndFrame = settings.PlaybackEndFrame;
    }

    private static ProjectPlaybackSettings NormalizePlaybackSettings(
        decimal playbackFps,
        bool loopPlayback,
        int playbackStartFrame,
        int playbackEndFrame)
    {
        var normalizedStartFrame = Math.Max(0, playbackStartFrame);
        return new ProjectPlaybackSettings(
            Math.Clamp(playbackFps, 1m, 120m),
            loopPlayback,
            normalizedStartFrame,
            Math.Max(normalizedStartFrame, playbackEndFrame));
    }

    private readonly record struct ProjectPlaybackSettings(
        decimal PlaybackFps,
        bool LoopPlayback,
        int PlaybackStartFrame,
        int PlaybackEndFrame);

    private bool DependsOn(string sourceId, string targetId, ISet<string> visited)
    {
        if (string.Equals(sourceId, targetId, StringComparison.Ordinal)) return true;
        if (!visited.Add(sourceId)) return false;

        var source = FindDrawingObject(sourceId);
        if (source is null) return false;
        foreach (var instance in source.Instances)
        {
            if (DependsOn(instance.DrawingObjectId, targetId, visited)) return true;
        }

        return false;
    }

    private DrawingObjectDefinition? FindDrawingObject(string id)
    {
        DrawingObjectDefinition? result = null;
        foreach (var drawingObject in _drawingObjects)
        {
            if (!string.Equals(drawingObject.Id, id, StringComparison.Ordinal)) continue;
            if (result is not null) return null;
            result = drawingObject;
        }

        return result;
    }

    private ProjectAssetFolder? FindAssetFolder(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return _assetFolders.SingleOrDefault(folder => string.Equals(folder.Id, id, StringComparison.Ordinal));
    }

    private ProjectAssetFolder[] AssetFolderSubtree(string rootId)
    {
        var result = new List<ProjectAssetFolder>();
        var pending = new Queue<string>();
        pending.Enqueue(rootId);
        while (pending.Count > 0)
        {
            var parentId = pending.Dequeue();
            var folder = FindAssetFolder(parentId);
            if (folder is null) continue;
            result.Add(folder);
            foreach (var child in _assetFolders.Where(item =>
                         string.Equals(item.ParentFolderId, parentId, StringComparison.Ordinal)))
            {
                pending.Enqueue(child.Id);
            }
        }
        return result.ToArray();
    }

    private bool IsAssetFolderDescendant(string candidateId, string ancestorId)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var current = FindAssetFolder(candidateId);
        while (current is not null && visited.Add(current.Id))
        {
            if (string.Equals(current.Id, ancestorId, StringComparison.Ordinal)) return true;
            current = FindAssetFolder(current.ParentFolderId);
        }
        return false;
    }

    private string NextAssetFolderCopyName(string sourceName, string parentFolderId)
    {
        var baseName = string.IsNullOrWhiteSpace(sourceName) ? "Folder" : sourceName.Trim();
        var candidate = $"{baseName} Copy";
        var suffix = 2;
        while (_assetFolders.Any(item =>
                   string.Equals(item.ParentFolderId, parentFolderId, StringComparison.Ordinal)
                   && string.Equals(item.Name, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{baseName} Copy {suffix++}";
        }
        return candidate;
    }

    private void RestoreAssetFolders(IEnumerable<ProjectAssetFolderRestartSnapshot> snapshots)
    {
        var source = snapshots
            .Where(item => IsValidRestartId(item.Id))
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        foreach (var item in source)
        {
            _assetFolders.Add(new ProjectAssetFolder
            {
                Id = item.Id,
                Name = string.IsNullOrWhiteSpace(item.Name) ? "Folder" : item.Name.Trim(),
                CreatedAt = item.CreatedAt
            });
        }
        foreach (var item in source)
        {
            var folder = FindAssetFolder(item.Id)!;
            var parentId = NormalizeAssetFolderId(item.ParentFolderId);
            if (parentId.Length > 0
                && FindAssetFolder(parentId) is not null
                && !string.Equals(folder.Id, parentId, StringComparison.Ordinal)
                && !IsAssetFolderDescendant(parentId, folder.Id))
            {
                folder.ParentFolderId = parentId;
            }
        }
    }

    private static string NormalizeAssetFolderId(string? folderId) => folderId?.Trim() ?? "";

    private string NextDrawingObjectCopyName(string sourceName)
    {
        var baseName = string.IsNullOrWhiteSpace(sourceName) ? "Drawing Object" : sourceName.Trim();
        var candidate = $"{baseName} Copy";
        var suffix = 2;
        while (_drawingObjects.Any(item => string.Equals(item.Name, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{baseName} Copy {suffix++}";
        }

        return candidate;
    }

    private static DrawingObjectInstanceDefinition CloneDrawingObjectInstance(
        DrawingObjectInstanceDefinition source,
        IReadOnlyDictionary<string, string>? drawingObjectIdMap = null)
    {
        var clone = new DrawingObjectInstanceDefinition
        {
            DrawingObjectId = drawingObjectIdMap?.GetValueOrDefault(source.DrawingObjectId, source.DrawingObjectId)
                ?? source.DrawingObjectId,
            SceneLayerId = source.SceneLayerId,
            Name = source.Name,
            Visible = source.Visible,
            X = source.X,
            Y = source.Y,
            Z = source.Z,
            RotationX = source.RotationX,
            RotationY = source.RotationY,
            RotationZ = source.RotationZ,
            SkewX = source.SkewX,
            SkewY = source.SkewY,
            ScaleX = source.ScaleX,
            ScaleY = source.ScaleY,
            ScaleZ = source.ScaleZ,
            Alpha = source.Alpha,
            TintArgb = source.TintArgb,
            PlaybackFps = source.PlaybackFps,
            PlaybackMode = source.PlaybackMode,
            HoldFrame = source.HoldFrame
        };
        clone.RestoreStateKeyframes(source.StateKeyframes);
        return clone;
    }

    private SceneDefinition? FindScene(string id)
    {
        SceneDefinition? result = null;
        foreach (var scene in _scenes)
        {
            if (!string.Equals(scene.Id, id, StringComparison.Ordinal)) continue;
            if (result is not null) return null;
            result = scene;
        }

        return result;
    }
}
