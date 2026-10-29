namespace VectorAnimationEngine;

internal sealed partial class VectorScene
{
    internal bool UsePlayheadForBlankExposure { get; set; }
    internal Func<string, bool>? CanMaterializeTimelineTweenLayer { get; set; }
    internal void SynchronizeSceneLayers(IReadOnlyList<SceneLayerDefinition> layers, string activeLayerId)
    {
        var changed = LayerCount != layers.Count
            || !LayerIds.SequenceEqual(layers.Select(layer => layer.Id), StringComparer.Ordinal);
        if (changed)
        {
            var oldIds = LayerIds;
            var oldLocked = LayerLocked;
            var oldOpacity = LayerOpacity;
            var destinationById = layers.Select((layer, index) => (layer.Id, Index: index))
                .ToDictionary(item => item.Id, item => item.Index, StringComparer.Ordinal);
            RemoveObjects(Enumerable.Range(0, ObjectCount).Where(index =>
                ObjectLayer[index] >= oldIds.Length || !destinationById.ContainsKey(oldIds[ObjectLayer[index]])));
            for (var index = 0; index < ObjectCount; index++)
                ObjectLayer[index] = checked((ushort)destinationById[oldIds[ObjectLayer[index]]]);
            var oldIndexById = oldIds.Select((id, index) => (Id: id, Index: index))
                .ToDictionary(item => item.Id, item => item.Index, StringComparer.Ordinal);
            LayerCount = layers.Count;
            LayerIds = layers.Select(layer => layer.Id).ToArray();
            LayerLocked = layers.Select(layer => oldIndexById.TryGetValue(layer.Id, out var previous)
                && previous < oldLocked.Length && oldLocked[previous]).ToArray();
            LayerOpacity = layers.Select(layer => oldIndexById.TryGetValue(layer.Id, out var previous)
                && previous < oldOpacity.Length ? oldOpacity[previous] : 1f).ToArray();
            LayerParentIds = new string[LayerCount];
            Array.Fill(LayerParentIds, "");
            LayerStart = new int[LayerCount];
            LayerEnd = Enumerable.Repeat(-1, LayerCount).ToArray();
        }
        var metadataChanged = changed
            || !LayerNames.SequenceEqual(layers.Select(layer => layer.Name))
            || !LayerKinds.SequenceEqual(layers.Select(layer => layer.Kind == SceneLayerKind.Mask
                ? DrawingLayerKind.Mask : DrawingLayerKind.Drawing))
            || !LayerVisible.SequenceEqual(layers.Select(layer => layer.Visible))
            || !LayerBlendModes.SequenceEqual(layers.Select(layer => layer.BlendMode))
            || !LayerOutline.SequenceEqual(layers.Select(layer => layer.Outline))
            || !LayerColorArgb.SequenceEqual(layers.Select(layer => layer.ColorArgb));
        ActiveLayer = Math.Max(0, Array.IndexOf(LayerIds, activeLayerId));
        if (!metadataChanged) return;
        LayerNames = layers.Select(layer => layer.Name).ToArray();
        LayerKinds = layers.Select(layer => layer.Kind == SceneLayerKind.Mask
            ? DrawingLayerKind.Mask : DrawingLayerKind.Drawing).ToArray();
        LayerMaskIds = Enumerable.Repeat("", LayerCount).ToArray();
        LayerVisible = layers.Select(layer => layer.Visible).ToArray();
        LayerBlendModes = layers.Select(layer => layer.BlendMode).ToArray();
        LayerColorArgb = layers.Select(layer => layer.ColorArgb).ToArray();
        LayerOutline = layers.Select(layer => layer.Outline).ToArray();
        RebuildGeometryIndex();
        RebuildSummaries();
    }
}
