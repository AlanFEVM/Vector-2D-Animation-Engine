namespace VectorAnimationEngine;

internal sealed partial class VectorProject
{
    /// <summary>Enumerates owned geometry once, including masks outside the active scene.</summary>
    internal IEnumerable<(string Name, VectorScene Scene)> EnumerateAssetScenes()
    {
        var visited = new HashSet<VectorScene>(ReferenceEqualityComparer.Instance);
        foreach (var drawing in _drawingObjects)
        {
            if (visited.Add(drawing.Scene)) yield return (drawing.Name, drawing.Scene);
        }
        foreach (var scene in _scenes)
        {
            foreach (var layer in scene.Layers)
            {
                if (layer.MaskScene is { } mask && visited.Add(mask))
                    yield return ($"{scene.Name} / {layer.Name}", mask);
            }
        }
    }

    internal void ValidateSvgAssetReferences()
    {
        var assetIds = _externalSvgAssets.Select(asset => asset.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var (_, scene) in EnumerateAssetScenes())
        {
            for (var index = 0; index < scene.ObjectCount; index++)
            {
                if (scene.TryGetImportedSvgAssetId(index, out var assetId) && !assetIds.Contains(assetId))
                    throw new InvalidOperationException("The project contains an SVG placement with a missing library asset.");
            }
        }
    }
}
