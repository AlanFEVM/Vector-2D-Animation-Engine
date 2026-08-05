namespace VectorAnimationEngine;

internal sealed class ProjectAssetTag
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; internal set; } = "Tag";
    public int ColorArgb { get; internal set; } = Color.FromArgb(66, 165, 245).ToArgb();
}

internal readonly record struct ProjectAssetTagData(string Id, string Name, int ColorArgb);
