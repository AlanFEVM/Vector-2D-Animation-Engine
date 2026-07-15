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

internal sealed class VectorProject
{
    private readonly List<SceneDefinition> _scenes = [];
    private readonly List<DrawingObjectDefinition> _drawingObjects = [];
    private readonly IReadOnlyList<SceneDefinition> _sceneView;
    private readonly IReadOnlyList<DrawingObjectDefinition> _drawingObjectView;

    public VectorProject()
    {
        _sceneView = _scenes.AsReadOnly();
        _drawingObjectView = _drawingObjects.AsReadOnly();
        AddScene("Scene 001");
        AddDrawingObject("Drawing Object 001");
    }

    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Untitled Project";
    public IReadOnlyList<SceneDefinition> Scenes => _sceneView;
    public IReadOnlyList<DrawingObjectDefinition> DrawingObjects => _drawingObjectView;
    public event EventHandler? Changed;

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

    public bool TryDuplicateDrawingObject(string drawingObjectId, out DrawingObjectDefinition? duplicate)
    {
        duplicate = null;
        var source = FindDrawingObject(drawingObjectId);
        if (source is null) return false;

        var sceneSnapshot = source.Scene.CreateSnapshot();
        var sourceTimeline = source.Timeline.CreateSnapshot();
        duplicate = new DrawingObjectDefinition
        {
            Name = NextDrawingObjectCopyName(source.Name),
            Kind = source.Kind,
            Detail = source.Detail
        };
        duplicate.Scene.RestoreSnapshot(sceneSnapshot);
        _drawingObjects.Add(duplicate);

        var instanceIdMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var sourceInstance in source.Instances)
        {
            var copy = CloneDrawingObjectInstance(sourceInstance);
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
                    Keyframes = track.Keyframes.ToArray()
                }).ToArray()
            });
            duplicate.SynchronizeInstanceTimelineTracks();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
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

    public bool TrySetSceneLayerColor(string sceneId, string layerId, Color color)
    {
        var scene = FindScene(sceneId);
        if (scene is null || !scene.SetLayerColor(layerId, color)) return false;
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
        out DrawingObjectInstanceDefinition? instance)
    {
        instance = null;
        if (!CanContainDrawingObject(containerId, childId)) return false;

        var container = FindDrawingObject(containerId)!;
        var child = FindDrawingObject(childId)!;
        instance = new DrawingObjectInstanceDefinition
        {
            DrawingObjectId = child.Id,
            Name = $"{child.Name} Instance {container.Instances.Count + 1:000}",
            X = position.X,
            Y = position.Y
        };
        container.AddInstance(this, instance);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public static VectorProject CreateEmpty() => new();

    internal ProjectRestartSnapshot CreateRestartSnapshot()
    {
        return new ProjectRestartSnapshot
        {
            Name = Name,
            DrawingObjects = _drawingObjects
                .Select(drawingObject => new DrawingObjectRestartSnapshot
                {
                    Id = drawingObject.Id,
                    Name = drawingObject.Name,
                    Kind = drawingObject.Kind,
                    Detail = drawingObject.Detail,
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

        var project = new VectorProject();
        project._drawingObjects.Clear();
        project._scenes.Clear();
        project.Name = string.IsNullOrWhiteSpace(snapshot.Name) ? "Untitled Project" : snapshot.Name;

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
            project._drawingObjects.Add(new DrawingObjectDefinition
            {
                Id = item.Id,
                Name = item.Name,
                Kind = item.Kind,
                Detail = item.Detail,
                CreatedAt = item.CreatedAt
            });
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
                if (!TryCreateRestartInstance(instance, out var restored)) continue;
                drawingObject.AddInstance(project, restored);
            }
        }

        for (var index = 0; index < sceneSnapshots.Length; index++)
        {
            var scene = project._scenes[index];
            foreach (var instance in sceneSnapshots[index].Instances)
            {
                if (!TryCreateRestartInstance(instance, out var restored)) continue;
                scene.AddInstance(project, new SceneObjectInstanceDefinition
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
                    ScaleZ = restored.ScaleZ
                });
            }
        }

        for (var index = 0; index < drawingSnapshots.Length; index++)
        {
            project._drawingObjects[index].Scene.RestoreSnapshot(drawingSnapshots[index].Scene);
            project._drawingObjects[index].SynchronizeInstanceTimelineTracks();
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
            ScaleZ = instance.ScaleZ
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
            ScaleZ = snapshot.ScaleZ
        };
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

    private static DrawingObjectInstanceDefinition CloneDrawingObjectInstance(DrawingObjectInstanceDefinition source)
    {
        return new DrawingObjectInstanceDefinition
        {
            DrawingObjectId = source.DrawingObjectId,
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
            ScaleZ = source.ScaleZ
        };
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
