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

    public Dictionary<int, DrawingObjectInstanceDefinition> CreateMovePlan(
        IReadOnlyCollection<string> instanceIds,
        int direction)
    {
        ArgumentNullException.ThrowIfNull(instanceIds);
        if (instanceIds.Count == 0 || direction == 0) return [];

        var selectedIds = instanceIds.ToHashSet(StringComparer.Ordinal);
        var plan = new Dictionary<int, DrawingObjectInstanceDefinition>();
        foreach (var layerId in _instances
                     .Where(instance => selectedIds.Contains(instance.Id))
                     .Select(instance => instance.SceneLayerId)
                     .Distinct(StringComparer.Ordinal))
        {
            var positions = Enumerable.Range(0, _instances.Count)
                .Where(index => string.Equals(_instances[index].SceneLayerId, layerId, StringComparison.Ordinal))
                .ToArray();
            if (positions.Length < 2) continue;
            var arranged = positions.Select(index => _instances[index]).ToArray();
            var moved = false;
            // Earlier positions become lower, frontmost composition layer indices.
            if (direction > 0)
            {
                for (var index = 1; index < arranged.Length; index++)
                {
                    if (!selectedIds.Contains(arranged[index].Id)
                        || selectedIds.Contains(arranged[index - 1].Id))
                    {
                        continue;
                    }
                    (arranged[index], arranged[index - 1]) = (arranged[index - 1], arranged[index]);
                    moved = true;
                }
            }
            else
            {
                for (var index = arranged.Length - 2; index >= 0; index--)
                {
                    if (!selectedIds.Contains(arranged[index].Id)
                        || selectedIds.Contains(arranged[index + 1].Id))
                    {
                        continue;
                    }
                    (arranged[index], arranged[index + 1]) = (arranged[index + 1], arranged[index]);
                    moved = true;
                }
            }

            if (!moved) continue;
            for (var index = 0; index < positions.Length; index++) plan[positions[index]] = arranged[index];
        }

        return plan;
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
