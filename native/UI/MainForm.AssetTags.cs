namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private IReadOnlyList<string>? LibraryAssetTagIds(string assetId) =>
        FindVaultSymbol(assetId)?.AssetTagIds
        ?? _project.FindImageAsset(assetId)?.AssetTagIds
        ?? _project.FindExternalSvgAsset(assetId)?.AssetTagIds;

    private void SetDrawingObjectAssetTagAssignment(string drawingObjectId, string tagId, bool assigned)
    {
        var current = LibraryAssetTagIds(drawingObjectId);
        if (current is null || !_project.AssetTags.Any(tag => tag.Id == tagId)) return;
        var next = current.ToHashSet(StringComparer.Ordinal);
        if (!(assigned ? next.Add(tagId) : next.Remove(tagId))) return;
        ExecuteLibraryPanelCommand(() =>
        {
            if (!_project.TrySetLibraryAssetTags(drawingObjectId, next))
                throw new InvalidOperationException("The asset tag changes could not be applied.");
        });
    }

    private void EditDrawingObjectAssetTags(string drawingObjectId) => EditLibraryAssetTags([drawingObjectId]);

    private void EditLibraryAssetTags(IEnumerable<string> assetIds)
    {
        var ids = assetIds.Distinct(StringComparer.Ordinal).Where(id => LibraryAssetTagIds(id) is not null).ToArray();
        if (ids.Length == 0) return;
        var common = LibraryAssetTagIds(ids[0])!.ToHashSet(StringComparer.Ordinal);
        foreach (var id in ids.Skip(1)) common.IntersectWith(LibraryAssetTagIds(id)!);
        if (!LibraryVaultPanel.TryEditAssetTags(this, _project, common.ToArray(), out var tags,
                out var assigned, ids.Length)) return;
        var definitionsUnchanged = tags.SequenceEqual(_project.AssetTags.Select(tag =>
            new ProjectAssetTagData(tag.Id, tag.Name, tag.ColorArgb)));
        if (definitionsUnchanged && ids.All(id => LibraryAssetTagIds(id)!.SequenceEqual(assigned))) return;
        ExecuteLibraryPanelCommand(() =>
        {
            if (!_project.TryApplyLibraryAssetTagEdit(ids, tags, assigned))
                throw new InvalidOperationException("The asset tag changes could not be applied.");
        });
    }
}
