namespace VectorAnimationEngine;

internal sealed record VaultStageDragData(string ProjectId, string[] SymbolIds, string[] ImageIds, string[] SvgIds)
{
    public int Count => SymbolIds.Length + ImageIds.Length + SvgIds.Length;
}

internal sealed partial class LibraryVaultPanel
{
    // Use tree order, rather than HashSet iteration order, for a stable stacking order.
    private VaultStageDragData? SelectedStageAssets()
    {
        if (_project is null) return null;
        var rows = new List<VaultRow>();
        Visit(_projectObjects.Nodes);
        if (rows.Count == 0 && SelectedProjectRow() is { } selected) rows.Add(selected);
        if (rows.Count == 0 || rows.Any(row => row.Folder is not null || row.ImageAssetMissing
            || row.ExternalSvgMissing || row.DrawingObject?.IsOpenCanvas == true)) return null;
        return new(_project.Id,
            rows.Where(row => row.DrawingObject is not null).Select(row => row.DrawingObject!.Id).ToArray(),
            rows.Where(row => row.ImageAsset is not null).Select(row => row.ImageAsset!.Id).ToArray(),
            rows.Where(row => row.ExternalSvgAsset is not null).Select(row => row.ExternalSvgAsset!.Id).ToArray());

        void Visit(TreeNodeCollection nodes)
        {
            foreach (TreeNode node in nodes)
            {
                if (_multiSelectedNodes.Contains(node) && node.Tag is VaultRow row) rows.Add(row);
                Visit(node.Nodes);
            }
        }
    }

    private void PlaceSelectedStageAssets()
    {
        if (SelectedStageAssets() is not { Count: > 0 } assets) return;
        AssetBatchActionRequested?.Invoke(this, new("Place", assets.SymbolIds, assets.ImageIds, assets.SvgIds, []));
    }
}
