namespace VectorAnimationEngine;

internal sealed partial class SceneDefinition
{
    private void AssignTabGroupToLayerBlock(string targetId, string groupId)
    {
        var layer = _layers.FirstOrDefault(item => item.Id == targetId);
        if (layer is null) return;
        var root = _layers.FirstOrDefault(item => item.Id == layer.MaskLayerId) ?? layer;
        foreach (var item in _layers.Where(item => item.Id == root.Id || item.MaskLayerId == root.Id))
        {
            var track = _timeline.FindTrackByTargetId(item.Id);
            if (track is not null) _timeline.SetTrackTabGroup(track.Id, groupId);
        }
    }

    private void ApplyTabGroupLayerOrder()
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var blocks = new List<(int Ordinal, SceneLayerDefinition[] Layers)>();
        foreach (var layer in _layers)
        {
            if (visited.Contains(layer.Id)) continue;
            var mask = _layers.FirstOrDefault(candidate => candidate.Id == layer.MaskLayerId);
            var root = mask ?? layer;
            var block = root.Kind == SceneLayerKind.Mask
                ? new[] { root }.Concat(_layers.Where(candidate => candidate.MaskLayerId == root.Id)).ToArray()
                : new[] { root };
            visited.UnionWith(block.Select(item => item.Id));
            var groupId = _timeline.FindTrackByTargetId(root.Id)?.TabGroupId ?? AnimationTimeline.DefaultTabGroupId;
            foreach (var item in block)
            {
                var track = _timeline.FindTrackByTargetId(item.Id);
                if (track is not null) _timeline.SetTrackTabGroup(track.Id, groupId);
            }
            blocks.Add((_timeline.TabGroupOrdinal(groupId), block));
        }
        var ordered = blocks.OrderByDescending(block => block.Ordinal).SelectMany(block => block.Layers).ToArray();
        if (_layers.SequenceEqual(ordered)) return;
        _layers.Clear();
        _layers.AddRange(ordered);
        _instanceIndex.Invalidate();
        SynchronizeTimelineTracks();
    }
}
