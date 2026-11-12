namespace VectorAnimationEngine;

internal sealed partial class VectorProject
{
    /// <summary>Removes only the folder. Direct child folders and every asset kind move to its parent.</summary>
    public bool TryRemoveAssetFolder(string folderId)
    {
        var folder = FindAssetFolder(folderId);
        if (folder is null) return false;
        foreach (var child in _assetFolders.Where(item => item.ParentFolderId == folder.Id))
            child.ParentFolderId = folder.ParentFolderId;
        foreach (var drawing in _drawingObjects.Where(item => item.AssetFolderId == folder.Id))
            drawing.AssetFolderId = folder.ParentFolderId;
        foreach (var asset in _imageAssets.Where(item => item.AssetFolderId == folder.Id))
            asset.AssetFolderId = folder.ParentFolderId;
        foreach (var asset in _externalSvgAssets.Where(item => item.AssetFolderId == folder.Id))
            asset.AssetFolderId = folder.ParentFolderId;
        _assetFolders.Remove(folder);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }
}
