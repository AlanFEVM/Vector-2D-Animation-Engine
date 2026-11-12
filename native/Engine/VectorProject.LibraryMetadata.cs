namespace VectorAnimationEngine;

internal sealed partial class VectorProject
{
    private string[] ValidateLibraryAssetMetadata(string folderId, IEnumerable<string> tagIds)
    {
        var ids = tagIds?.ToArray() ?? throw new InvalidDataException("Missing asset tags.");
        if (folderId is null || folderId.Length > 0 && FindAssetFolder(folderId) is null
            || ids.Length > MaxAssetTagCount || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length
            || ids.Any(id => !_assetTags.Any(tag => tag.Id == id)))
            throw new InvalidDataException("Invalid library asset folder or tags.");
        return ids;
    }

    public bool TryMoveLibraryAssetToFolder(string assetId, string folderId)
    {
        folderId = NormalizeAssetFolderId(folderId);
        if (folderId.Length > 0 && FindAssetFolder(folderId) is null) return false;
        if (FindImageAsset(assetId) is { } image)
        {
            if (image.AssetFolderId == folderId) return true;
            image.AssetFolderId = folderId;
        }
        else if (FindExternalSvgAsset(assetId) is { } svg)
        {
            if (svg.AssetFolderId == folderId) return true;
            svg.AssetFolderId = folderId;
        }
        else return TryMoveDrawingObjectToAssetFolder(assetId, folderId);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool TrySetLibraryAssetTags(string assetId, IEnumerable<string> tagIds)
    {
        var ids = tagIds?.Distinct(StringComparer.Ordinal).ToArray();
        if (ids is null || ids.Length > MaxAssetTagCount || ids.Any(id => !_assetTags.Any(tag => tag.Id == id))) return false;
        if (FindImageAsset(assetId) is { } image)
        {
            if (image.AssetTagIds.SequenceEqual(ids, StringComparer.Ordinal)) return true;
            image.AssetTagIds = ids;
        }
        else if (FindExternalSvgAsset(assetId) is { } svg)
        {
            if (svg.AssetTagIds.SequenceEqual(ids, StringComparer.Ordinal)) return true;
            svg.AssetTagIds = ids;
        }
        else if (FindDrawingObject(assetId) is { } symbol)
        {
            if (symbol.AssetTagIds.SequenceEqual(ids, StringComparer.Ordinal)) return true;
            symbol.ReplaceAssetTagIds(ids);
        }
        else return false;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool TryRenameExternalSvgAsset(string assetId, string name)
    {
        if (FindExternalSvgAsset(assetId) is not { } asset || !TryNormalizeExternalSvgAssetName(name, out var normalized)) return false;
        if (asset.Name == normalized) return true;
        asset.Name = normalized;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }
}
