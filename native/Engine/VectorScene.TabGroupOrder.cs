namespace VectorAnimationEngine;

internal sealed partial class VectorScene
{
    private void AssignTabGroupToLayerBlock(string targetId, string groupId)
    {
        var root = Array.IndexOf(LayerIds, targetId);
        if (root < 0) return;
        var visited = new HashSet<int>();
        while (visited.Add(root))
        {
            var parent = GetLayerParentIndex(root);
            if (parent >= 0) { root = parent; continue; }
            if (TryGetMaskLayerIndex(root, out var mask)) { root = mask; continue; }
            break;
        }
        foreach (var layer in GetLayerMoveSet(root))
        {
            var track = Timeline.FindTrackByTargetId(LayerIds[layer]);
            if (track is not null) Timeline.SetTrackTabGroup(track.Id, groupId);
        }
    }

    private void ApplyTabGroupLayerOrder()
    {
        if (LayerCount == 0) return;
        var visited = new HashSet<int>();
        var blocks = new List<(int Ordinal, int[] Layers)>();
        foreach (var root in GetLayerDisplayOrder())
        {
            if (visited.Contains(root)) continue;
            var block = GetLayerMoveSet(root).Order().ToArray();
            visited.UnionWith(block);
            var groupId = Timeline.FindTrackByTargetId(LayerIds[root])?.TabGroupId
                ?? AnimationTimeline.DefaultTabGroupId;
            // Folder children and linked masks share their owning block's group.
            foreach (var layer in block)
            {
                var track = Timeline.FindTrackByTargetId(LayerIds[layer]);
                if (track is not null) Timeline.SetTrackTabGroup(track.Id, groupId);
            }
            blocks.Add((Timeline.TabGroupOrdinal(groupId), block));
        }
        var order = blocks.OrderByDescending(block => block.Ordinal).SelectMany(block => block.Layers).ToArray();
        if (!order.SequenceEqual(Enumerable.Range(0, LayerCount))) ApplyLayerOrder(order);
    }
}
