namespace VectorAnimationEngine;

internal sealed class ProjectAssetFolder
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; internal set; } = "Folder";
    public string ParentFolderId { get; internal set; } = "";
    public DateTime CreatedAt { get; init; } = DateTime.Now;
}
