namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private void SetDrawingObjectAssetTagAssignment(string drawingObjectId, string tagId, bool assigned)
    {
        var drawingObject = _drawingObjects.FirstOrDefault(item =>
            string.Equals(item.Id, drawingObjectId, StringComparison.Ordinal));
        if (drawingObject is null
            || !_project.AssetTags.Any(tag => string.Equals(tag.Id, tagId, StringComparison.Ordinal)))
        {
            AppLog.Warn($"Unable to set asset tag because the symbol or tag no longer exists: {drawingObjectId}/{tagId}");
            return;
        }

        var assignedTagIds = drawingObject.AssetTagIds.ToHashSet(StringComparer.Ordinal);
        var changed = assigned ? assignedTagIds.Add(tagId) : assignedTagIds.Remove(tagId);
        if (!changed) return;

        var tags = _project.AssetTags
            .Select(tag => new ProjectAssetTagData(tag.Id, tag.Name, tag.ColorArgb))
            .ToArray();
        if (!_project.TryApplyAssetTagEdit(drawingObject.Id, tags, assignedTagIds))
        {
            AppLog.Warn($"Unable to set asset tag assignment for symbol: {drawingObject.Id}");
            return;
        }

        AppLog.Info($"{(assigned ? "Assigned" : "Unassigned")} asset tag for symbol: {drawingObject.Name}");
    }

    private void EditDrawingObjectAssetTags(string drawingObjectId)
    {
        var drawingObject = _drawingObjects.FirstOrDefault(item =>
            string.Equals(item.Id, drawingObjectId, StringComparison.Ordinal));
        if (drawingObject is null)
        {
            AppLog.Warn($"Unable to edit asset tags because the symbol no longer exists: {drawingObjectId}");
            return;
        }

        if (!LibraryVaultPanel.TryEditAssetTags(
                this,
                _project,
                drawingObject,
                out var tags,
                out var assignedTagIds))
        {
            return;
        }

        if (!_project.TryApplyAssetTagEdit(drawingObject.Id, tags, assignedTagIds))
        {
            AppLog.Warn($"Unable to apply asset tag changes for symbol: {drawingObject.Id}");
            ModernMessageDialog.Show(
                this,
                UiLocalization.T("The asset tag changes could not be applied."),
                UiLocalization.T("Asset Tags"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        AppLog.Info($"Updated asset tags for symbol: {drawingObject.Name}");
    }
}
