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

    public SceneDefinition AddScene(string? name = null)
    {
        var index = _scenes.Count + 1;
        var scene = new SceneDefinition
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
        drawingObject.Scene.CreateEmpty();
        _drawingObjects.Add(drawingObject);
        return drawingObject;
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
        out DrawingObjectInstanceDefinition? instance)
    {
        instance = null;
        var scene = FindScene(sceneId);
        var drawingObject = FindDrawingObject(drawingObjectId);
        if (scene is null || drawingObject is null) return false;

        instance = new SceneObjectInstanceDefinition
        {
            DrawingObjectId = drawingObject.Id,
            Name = $"{drawingObject.Name} Instance {scene.Instances.Count + 1:000}",
            X = position.X,
            Y = position.Y,
            Z = z
        };
        scene.AddInstance(this, instance);
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
        return true;
    }

    public static VectorProject CreateEmpty() => new();

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
