namespace VectorAnimationEngine;

internal sealed partial class VectorScene
{
    internal int[] ExpandMotionTransformObjects(IEnumerable<int> selectedObjects, int frame)
    {
        var selected = selectedObjects.Where(index => (uint)index < ObjectCount).Distinct().ToArray();
        var descendants = Timeline.MotionDescendantTargets(selected.Select(index => LayerIds[ObjectLayer[index]]));
        if (descendants.Count == 0) return selected;
        var result = selected.ToHashSet();
        for (var index = 0; index < ObjectCount; index++)
        {
            var layer = ObjectLayer[index];
            if (!descendants.Contains(LayerIds[layer])) continue;
            var exposure = Timeline.EvaluateTargetExposure(LayerIds[layer], frame);
            if (exposure.HasContent && ObjectKeyframeFrame[index] == exposure.SourceKeyframeFrame) result.Add(index);
        }
        return result.Order().ToArray();
    }
}
