namespace VectorAnimationEngine;

internal sealed partial class LibraryVaultPanel
{
    private void AddBlockChunkNodes(ISet<string> expandedSceneIds)
    {
        if (_project is null || AssetFilterActive) return;
        var kind = UiLocalization.T("MC Chunk");
        foreach (var scene in _project.Scenes)
        {
            if (scene.BlockGrid is not { } grid) continue;
            var name = $"{UiLocalization.T("MC Symbols")} / {scene.Name}";
            var wholeScene = name.Contains(AssetSearchText, StringComparison.CurrentCultureIgnoreCase);
            var root = new TreeNode(name)
            {
                Tag = new VaultRow(new VaultItem { Kind = "Folder", Name = name }, null, false, BlockScene: scene)
            };
            foreach (var slice in SceneBlockChunk.Enumerate(grid).GroupBy(chunk => chunk.Z))
            {
                var sliceName = SceneBlockChunk.OuterLayerName(slice.Key);
                var wholeSlice = wholeScene || sliceName.Contains(AssetSearchText, StringComparison.CurrentCultureIgnoreCase);
                var folder = grid.SectionDepth == 0 ? root : new TreeNode(sliceName)
                {
                    Tag = new VaultRow(new VaultItem { Kind = "Folder", Name = sliceName }, null, false,
                        BlockScene: scene, BlockChunkSlab: slice.Key)
                };
                foreach (var chunk in slice)
                {
                    var label = $"{kind} ({chunk.X}, {chunk.Y})";
                    if (!wholeSlice && !label.Contains(AssetSearchText, StringComparison.CurrentCultureIgnoreCase)) continue;
                    folder.Nodes.Add(new TreeNode(label)
                    {
                        Tag = new VaultRow(new VaultItem
                        {
                            Kind = kind, Name = label, Detail = UiLocalization.T("Terrain with live lighting")
                        }, null, false, BlockScene: scene, BlockChunk: chunk)
                    });
                }
                if (ReferenceEquals(folder, root) || folder.Nodes.Count == 0) continue;
                root.Nodes.Add(folder);
                if (expandedSceneIds.Contains(NodeIdentity(folder)) || AssetSearchText.Length > 0) folder.Expand();
            }
            if (root.Nodes.Count == 0) continue;
            _projectObjects.Nodes.Add(root);
            if (expandedSceneIds.Contains(NodeIdentity(root)) || AssetSearchText.Length > 0) root.Expand();
        }
    }
}
