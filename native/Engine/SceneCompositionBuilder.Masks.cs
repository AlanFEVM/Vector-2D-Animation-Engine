using System.Collections.ObjectModel;

namespace VectorAnimationEngine;

// Derived composition indices, never durable source IDs. Each clip entry means
// intersection; each mask's content layers mean union after their own clips.
internal sealed class SceneCompositionMaskPlan
{
    internal static SceneCompositionMaskPlan Empty { get; } = new([], [], []);

    internal SceneCompositionMaskPlan(
        Dictionary<int, IReadOnlyList<int>> clips,
        Dictionary<int, IReadOnlyList<int>> contents,
        Dictionary<int, IReadOnlyList<int>> containers)
    {
        ClipsByLayer = new ReadOnlyDictionary<int, IReadOnlyList<int>>(clips);
        ContentsByMaskLayer = new ReadOnlyDictionary<int, IReadOnlyList<int>>(contents);
        ContainersByLayer = new ReadOnlyDictionary<int, IReadOnlyList<int>>(containers);
    }

    internal IReadOnlyDictionary<int, IReadOnlyList<int>> ClipsByLayer { get; }
    internal IReadOnlyDictionary<int, IReadOnlyList<int>> ContentsByMaskLayer { get; }
    internal IReadOnlyDictionary<int, IReadOnlyList<int>> ContainersByLayer { get; }
}

internal static partial class SceneCompositionBuilder
{
    // CombineOnionSkinPreviews appends every populated source's full layer
    // stack. Remap all three relationships by exactly the same offsets so
    // masks from neighbouring exposures cannot clip each other's artwork.
    internal static SceneCompositionMaskPlan CombineOnionSkinMaskPlans(
        IReadOnlyList<(VectorScene Scene, SceneCompositionMaskPlan Plan)> sources)
    {
        var clips = new Dictionary<int, IReadOnlyList<int>>();
        var contents = new Dictionary<int, IReadOnlyList<int>>();
        var containers = new Dictionary<int, IReadOnlyList<int>>();
        var offset = 0;
        foreach (var (scene, plan) in sources)
        {
            if (scene.ObjectCount == 0) continue;
            Copy(plan.ClipsByLayer, clips);
            Copy(plan.ContentsByMaskLayer, contents);
            Copy(plan.ContainersByLayer, containers);
            offset = checked(offset + scene.LayerCount);
        }
        return contents.Count == 0 ? SceneCompositionMaskPlan.Empty : new(clips, contents, containers);

        void Copy(IReadOnlyDictionary<int, IReadOnlyList<int>> source,
            Dictionary<int, IReadOnlyList<int>> target)
        {
            foreach (var (key, values) in source)
                target.Add(checked(key + offset), values.Select(value => checked(value + offset)).ToArray());
        }
    }

    private readonly record struct CompositionMaskKey(VectorScene Source, string OccurrenceId, string LayerId);

    private static IReadOnlyList<CompositionMaskKey> AppendMaskKey(
        IReadOnlyList<CompositionMaskKey> keys, CompositionMaskKey key)
    {
        var result = new CompositionMaskKey[keys.Count + 1];
        for (var index = 0; index < keys.Count; index++) result[index] = keys[index];
        result[^1] = key;
        return result;
    }

    private static SceneCompositionMaskPlan BuildLayerMaskPlan(IReadOnlyList<CompositionLayer> layers)
    {
        var masks = new Dictionary<CompositionMaskKey, int>();
        for (var index = 0; index < layers.Count; index++)
        {
            var layer = layers[index];
            if (!layer.SyntheticGroup && layer.Source.GetLayerKind(layer.SourceLayer) == DrawingLayerKind.Mask)
                masks.Add(new(layer.Source, layer.SourceGroup.OccurrenceId, layer.Source.LayerIds[layer.SourceLayer]), index);
        }
        if (masks.Count == 0) return SceneCompositionMaskPlan.Empty;

        var clips = new Dictionary<int, IReadOnlyList<int>>();
        var containers = new Dictionary<int, IReadOnlyList<int>>();
        var contents = masks.Values.ToDictionary(index => index, _ => new List<int>());
        for (var index = 0; index < layers.Count; index++)
        {
            var layer = layers[index];
            var bound = Resolve(layer.InheritedMasks);
            // Include direct layers collected outside nested traversal too.
            if (!layer.SyntheticGroup && layer.Source.TryGetMaskLayerIndex(layer.SourceLayer, out var sourceMask)
                && masks.TryGetValue(new(layer.Source, layer.SourceGroup.OccurrenceId,
                    layer.Source.LayerIds[sourceMask]), out var mask) && !bound.Contains(mask))
                bound.Add(mask);
            if (bound.Count > 0) clips.Add(index, bound.AsReadOnly());
            var containing = Resolve(layer.MaskContainers);
            if (containing.Count > 0) containers.Add(index, containing.AsReadOnly());
            if (layer.SyntheticGroup) continue;
            if (layer.Source.ShouldRenderLayerContent(layer.SourceLayer))
                foreach (var container in containing) contents[container].Add(index);
            if (layer.Source.GetLayerKind(layer.SourceLayer) == DrawingLayerKind.Mask)
                contents[index].Add(index);
        }
        return new(clips, contents.ToDictionary(pair => pair.Key,
            pair => (IReadOnlyList<int>)pair.Value.Distinct().ToList().AsReadOnly()), containers);

        List<int> Resolve(IReadOnlyList<CompositionMaskKey> keys)
        {
            var result = new List<int>();
            foreach (var key in keys)
            {
                if (!masks.TryGetValue(key, out var index))
                    throw new InvalidOperationException("A nested mask reference is missing from the composed layer stack.");
                if (!result.Contains(index)) result.Add(index);
            }
            return result;
        }
    }
}
