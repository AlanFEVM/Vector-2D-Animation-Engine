namespace VectorAnimationEngine;

internal sealed class ExternalSvgAssetDefinition
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; internal set; } = "SVG";
    public string SourcePath { get; internal set; } = "";
    public string ProjectRelativePath { get; internal set; } = "";
    public string LastKnownSha256 { get; internal set; } = "";
    public DateTime CreatedAt { get; init; } = DateTime.Now;
}
