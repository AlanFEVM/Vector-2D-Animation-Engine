namespace VectorAnimationEngine;

internal sealed class SceneDefinition
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Scene";
    public string Detail { get; set; } = "Scene composition context";
    public DateTime CreatedAt { get; init; } = DateTime.Now;
}
