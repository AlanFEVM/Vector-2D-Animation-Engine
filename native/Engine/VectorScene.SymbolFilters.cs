namespace VectorAnimationEngine;

internal sealed partial class VectorScene
{
    // Derived instance groups use stable layer ids, so layer reordering cannot move an effect.
    private readonly Dictionary<string, SymbolFilters> _layerSymbolFilters = new(StringComparer.Ordinal);

    public bool HasSymbolFilters => _layerSymbolFilters.Count != 0;
    public bool RequiresIsolatedLayerCompositing => HasNonNormalLayerBlendModes || HasSymbolFilters;

    internal SymbolFilters GetLayerSymbolFilters(int layer) =>
        (uint)layer < LayerIds.Length && _layerSymbolFilters.TryGetValue(LayerIds[layer], out var filters)
            ? filters : default;

    internal void SetLayerSymbolFilters(int layer, SymbolFilters filters)
    {
        if ((uint)layer >= LayerCount) throw new ArgumentOutOfRangeException(nameof(layer));
        if (!filters.IsValid) throw new ArgumentOutOfRangeException(nameof(filters));
        if (GetLayerSymbolFilters(layer) == filters) return;
        if (filters.HasEnabled) _layerSymbolFilters[LayerIds[layer]] = filters;
        else _layerSymbolFilters.Remove(LayerIds[layer]);
        GeometryRevision++;
    }

    private void RestoreLayerSymbolFilters(VectorSceneSnapshot snapshot)
    {
        _layerSymbolFilters.Clear();
        foreach (var (id, filters) in snapshot.LayerSymbolFilters ?? [])
        {
            if (!filters.IsValid || !LayerIds.Contains(id, StringComparer.Ordinal))
                throw new InvalidDataException("A symbol filter group in the scene snapshot is invalid.");
            if (filters.HasEnabled) _layerSymbolFilters.Add(id, filters);
        }
    }
}
