namespace VectorAnimationEngine;

internal class DrawingObjectInstanceDefinition
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string DrawingObjectId { get; init; } = "";
    public string SceneLayerId { get; set; } = "";
    public string Name { get; set; } = "Instance";
    public bool Visible { get; set; } = true;
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float RotationX { get; set; }
    public float RotationY { get; set; }
    public float RotationZ { get; set; }
    public float SkewX { get; set; }
    public float SkewY { get; set; }
    public float ScaleX { get; set; } = 1;
    public float ScaleY { get; set; } = 1;
    public float ScaleZ { get; set; } = 1;
}

internal sealed class SceneObjectInstanceDefinition : DrawingObjectInstanceDefinition;
