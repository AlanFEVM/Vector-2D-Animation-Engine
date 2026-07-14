namespace VectorAnimationEngine;

// This snapshot is an internal handoff format for a development-process restart,
// not a durable project-file contract.
internal sealed class ProjectRestartSnapshot
{
    public string Name { get; init; } = "Untitled Project";
    public DrawingObjectRestartSnapshot[] DrawingObjects { get; init; } = [];
    public SceneRestartSnapshot[] Scenes { get; init; } = [];
}

internal sealed class DrawingObjectRestartSnapshot
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "Drawing Object";
    public string Kind { get; init; } = "Symbol";
    public string Detail { get; init; } = "Reusable drawing object";
    public DateTime CreatedAt { get; init; } = DateTime.Now;
    public VectorSceneSnapshot Scene { get; init; } = new();
    public InstanceRestartSnapshot[] Instances { get; init; } = [];
}

internal sealed class SceneRestartSnapshot
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "Scene";
    public string Detail { get; init; } = "Scene composition context";
    public SceneDimension Dimension { get; init; } = SceneDimension.TwoD;
    public SceneCameraDefinition Camera { get; init; } = new();
    public DateTime CreatedAt { get; init; } = DateTime.Now;
    public SceneLayerSnapshot Layers { get; init; } = new();
    public InstanceRestartSnapshot[] Instances { get; init; } = [];
    public AnimationTimelineSnapshot Timeline { get; init; } = new();
}

internal sealed class InstanceRestartSnapshot
{
    public string Id { get; init; } = "";
    public string DrawingObjectId { get; init; } = "";
    public string SceneLayerId { get; init; } = "";
    public string Name { get; init; } = "Instance";
    public bool Visible { get; init; } = true;
    public float X { get; init; }
    public float Y { get; init; }
    public float Z { get; init; }
    public float RotationX { get; init; }
    public float RotationY { get; init; }
    public float RotationZ { get; init; }
    public float SkewX { get; init; }
    public float SkewY { get; init; }
    public float ScaleX { get; init; } = 1;
    public float ScaleY { get; init; } = 1;
    public float ScaleZ { get; init; } = 1;
}
