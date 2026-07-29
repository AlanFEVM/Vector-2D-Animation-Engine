namespace VectorAnimationEngine;

internal sealed class LayeredInstanceIndex
{
    private static readonly IReadOnlyList<DrawingObjectInstanceDefinition> Empty =
        Array.AsReadOnly(Array.Empty<DrawingObjectInstanceDefinition>());

    private readonly IReadOnlyList<DrawingObjectInstanceDefinition> _instances;
    private IReadOnlyDictionary<string, IReadOnlyList<DrawingObjectInstanceDefinition>>? _instancesByLayer;

    public LayeredInstanceIndex(IReadOnlyList<DrawingObjectInstanceDefinition> instances)
    {
        ArgumentNullException.ThrowIfNull(instances);
        _instances = instances;
    }

    public IReadOnlyList<DrawingObjectInstanceDefinition> Get(string? layerId)
    {
        EnsureCurrent();
        return layerId is not null && _instancesByLayer!.TryGetValue(layerId, out var instances)
            ? instances
            : Empty;
    }

    public bool Any(string? layerId)
    {
        EnsureCurrent();
        return layerId is not null && _instancesByLayer!.ContainsKey(layerId);
    }

    public void Invalidate()
    {
        _instancesByLayer = null;
    }

    private void EnsureCurrent()
    {
        if (_instancesByLayer is not null) return;

        var groups = new Dictionary<string, List<DrawingObjectInstanceDefinition>>(StringComparer.Ordinal);
        foreach (var instance in _instances)
        {
            if (instance.SceneLayerId is not { } layerId) continue;
            if (!groups.TryGetValue(layerId, out var group))
            {
                group = [];
                groups.Add(layerId, group);
            }

            group.Add(instance);
        }

        var rebuilt = new Dictionary<string, IReadOnlyList<DrawingObjectInstanceDefinition>>(
            groups.Count,
            StringComparer.Ordinal);
        foreach (var (layerId, group) in groups)
        {
            rebuilt.Add(layerId, Array.AsReadOnly(group.ToArray()));
        }

        _instancesByLayer = rebuilt;
    }
}
