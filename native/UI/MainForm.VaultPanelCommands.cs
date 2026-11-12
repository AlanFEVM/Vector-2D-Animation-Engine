namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private DrawingObjectDefinition? FindVaultSymbol(string id) => _drawingObjects.FirstOrDefault(symbol => symbol.Id == id);

    private void DeleteAssetFolderFromPanel(string folderId)
    {
        var folder = _project.AssetFolders.FirstOrDefault(item => item.Id == folderId);
        if (folder is null) return;
        if (ModernMessageDialog.Show(this,
                string.Format(UiLocalization.T("Delete folder \"{0}\"? Its assets and child folders will move to its parent. No assets will be deleted."), folder.Name),
                UiLocalization.T("Delete Folder"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        ExecuteLibraryPanelCommand(() => RequireVaultChange(_project.TryRemoveAssetFolder(folderId)));
    }

    private void RenameLibraryAssetFromPanel(string assetId, bool svg)
    {
        var name = svg ? _project.FindExternalSvgAsset(assetId)?.Name : _project.FindImageAsset(assetId)?.Name;
        if (name is null || !DrawingObjectNameDialog.TryAsk(this, "Rename Asset", "Asset name", name, out var next)
            || name == next) return;
        var limit = svg ? VectorProject.MaxExternalSvgAssetNameLength : VectorProject.MaxImageAssetNameLength;
        if (next.Length is 0 || next.Length > limit)
        {
            ShowVaultNotice(string.Format(UiLocalization.T("Asset names must contain between 1 and {0} characters."), limit));
            return;
        }
        ExecuteLibraryPanelCommand(() => RequireVaultChange(svg
            ? _project.TryRenameExternalSvgAsset(assetId, next) : _project.TryRenameImageAsset(assetId, next)));
    }

    private string? LibraryAssetFolderId(string assetId) => FindVaultSymbol(assetId)?.AssetFolderId
        ?? _project.FindImageAsset(assetId)?.AssetFolderId ?? _project.FindExternalSvgAsset(assetId)?.AssetFolderId;

    private void MoveLibraryAssetFromPanel(string assetId, string folderId)
    {
        if (LibraryAssetFolderId(assetId) is not { } current || current == folderId) return;
        ExecuteLibraryPanelCommand(() => RequireVaultChange(_project.TryMoveLibraryAssetToFolder(assetId, folderId)));
    }

    private void HandleVaultBatchAction(VaultBatchActionEventArgs request)
    {
        if (!CommitTextEdit()) return;
        var symbols = request.SymbolIds.Distinct(StringComparer.Ordinal)
            .Where(id => FindVaultSymbol(id) is { IsOpenCanvas: false }).ToArray();
        var images = request.ImageIds.Distinct(StringComparer.Ordinal).Where(id => _project.FindImageAsset(id) is not null).ToArray();
        var svgs = request.SvgIds.Distinct(StringComparer.Ordinal).Where(id => _project.FindExternalSvgAsset(id) is not null).ToArray();
        var folders = request.FolderIds.Distinct(StringComparer.Ordinal)
            .Where(id => _project.AssetFolders.Any(folder => folder.Id == id)).ToArray();
        var assets = symbols.Concat(images).Concat(svgs).ToArray();
        if (assets.Length + folders.Length == 0) return;
        switch (request.Action)
        {
            case "ManageTags":
                EditLibraryAssetTags(assets);
                break;
            case "Move":
                var moved = assets.Where(id => LibraryAssetFolderId(id) != request.TargetFolderId).ToArray();
                if (moved.Length == 0) return;
                ExecuteLibraryPanelCommand(() =>
                {
                    foreach (var id in moved) RequireVaultChange(_project.TryMoveLibraryAssetToFolder(id, request.TargetFolderId));
                });
                break;
            case "Delete":
                DeleteVaultSelection(symbols, images, svgs, folders);
                break;
            case "Duplicate":
                ExecuteLibraryPanelCommand(() => DuplicateVaultSelection(symbols, images, svgs, folders), includeContent: true);
                break;
            case "ExportSvg":
            case "ExportSymbol":
                ExportVaultSelection(symbols, images, svgs, folders, request.Action == "ExportSymbol");
                break;
        }
    }

    private void DeleteVaultSelection(string[] symbols, string[] images, string[] svgs, string[] folders)
    {
        if (_project.DrawingObjects.Count(symbol => !symbol.IsOpenCanvas) - symbols.Length < 1)
        {
            ShowVaultNotice(UiLocalization.T("A project must retain at least one symbol."));
            return;
        }
        var symbolIds = symbols.ToHashSet(StringComparer.Ordinal);
        var instanceCount = _project.Scenes.Sum(scene => scene.Instances.Count(instance => symbolIds.Contains(instance.DrawingObjectId)))
            + _project.DrawingObjects.Sum(symbol => symbol.Instances.Count(instance => symbolIds.Contains(instance.DrawingObjectId)));
        var placements = _project.EnumerateAssetScenes().Sum(item => images.Sum(id => item.Scene.FindBitmapObjectsUsingImageAsset(id).Length)
            + svgs.Sum(id => item.Scene.FindImportedSvgObjectsUsingAsset(id).Length));
        var backgrounds = _project.Scenes.Count(scene => scene.Canvas is { } canvas && images.Contains(canvas.ImageAssetId));
        var prompt = string.Format(UiLocalization.T("Delete {0} asset(s) and remove {1} folder(s)?"),
            symbols.Length + images.Length + svgs.Length, folders.Length)
            + "\r\n\r\n" + string.Format(UiLocalization.T("This also removes {0} symbol instance(s), {1} placement(s) including keyframes, and {2} image background(s). Canvas sizes are preserved."),
                instanceCount, placements, backgrounds)
            + "\r\n\r\n" + UiLocalization.T("Folder contents move to the parent. The whole operation can be undone once.");
        if (ModernMessageDialog.Show(this, prompt, UiLocalization.T("Delete Assets"), MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        if (IsSymbolEditing && !ExitSceneSymbolEditing()) return;
        var activeId = ActiveDrawingObject()?.Id;
        ExecuteLibraryPanelCommand(() =>
        {
            var masks = _project.Scenes.SelectMany(owner => owner.Layers
                .Where(layer => layer.MaskScene is { } mask && (images.Any(id => mask.FindBitmapObjectsUsingImageAsset(id).Length > 0)
                    || svgs.Any(id => mask.FindImportedSvgObjectsUsingAsset(id).Length > 0)))
                .Select(layer => (Owner: owner, Layer: layer, Frames: layer.MaskScene!.CreateSnapshot().ObjectKeyframeFrame
                    .Concat(layer.MaskScene.Timeline.Tracks.SelectMany(track => track.Keyframes.Select(key => key.Frame)))
                    .Append(0).Distinct().ToArray()))).ToArray();
            foreach (var (_, scene) in _project.EnumerateAssetScenes())
            {
                var indices = images.SelectMany(scene.FindBitmapObjectsUsingImageAsset)
                    .Concat(svgs.SelectMany(scene.FindImportedSvgObjectsUsingAsset)).Distinct().ToArray();
                if (indices.Length > 0) scene.RemoveObjects(indices);
            }
            foreach (var owner in _project.Scenes)
                if (owner.Canvas is { } canvas && images.Contains(canvas.ImageAssetId))
                    RequireVaultChange(_project.TrySetSceneCanvas(owner.Id,
                        canvas with { BackgroundKind = SceneBackgroundKind.None, ImageAssetId = "" }));
            foreach (var id in images) RequireVaultChange(_project.TryRemoveImageAsset(id, out _));
            foreach (var id in svgs) RequireVaultChange(_project.TryRemoveExternalSvgAsset(id, out _));
            foreach (var id in symbols) RequireVaultChange(_project.TryRemoveDrawingObject(id, out _));
            foreach (var id in folders) RequireVaultChange(_project.TryRemoveAssetFolder(id));
            foreach (var mask in masks)
                foreach (var frame in mask.Frames) mask.Owner.SynchronizeMaskTimelineContent(mask.Layer.Id, frame);
            var activeIndex = Array.FindIndex(_drawingObjects.ToArray(), symbol => symbol.Id == activeId);
            _activeDrawingObjectIndex = activeIndex >= 0 ? activeIndex : Math.Clamp(_activeDrawingObjectIndex, 0, _drawingObjects.Count - 1);
            if (IsSceneWorkspaceSelected) BindSceneEditStageCore(resetView: false, preserveHistory: true);
            else BindActiveDrawingObjectSceneCore(resetView: false, preserveHistory: true);
            RefreshDrawingObjectAssetPresentation();
        }, includeContent: true);
    }

    private HashSet<string> VaultFolderSubtreeIds(string rootId)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal) { rootId };
        var pending = new Queue<string>();
        pending.Enqueue(rootId);
        while (pending.TryDequeue(out var parent))
            foreach (var child in _project.AssetFolders.Where(folder => folder.ParentFolderId == parent))
                if (ids.Add(child.Id)) pending.Enqueue(child.Id);
        return ids;
    }

    private void DuplicateVaultSelection(string[] symbols, string[] images, string[] svgs, string[] folders)
    {
        var subtrees = folders.ToDictionary(id => id, VaultFolderSubtreeIds, StringComparer.Ordinal);
        var roots = folders.Where(id => !folders.Any(other => other != id && subtrees[other].Contains(id))).ToArray();
        var includedFolders = roots.SelectMany(id => subtrees[id]).ToHashSet(StringComparer.Ordinal);
        foreach (var id in symbols.Where(id => !includedFolders.Contains(LibraryAssetFolderId(id)!)))
            RequireVaultChange(_project.TryDuplicateDrawingObject(id, out _));
        foreach (var id in images.Where(id => !includedFolders.Contains(LibraryAssetFolderId(id)!))) DuplicateSharedAssetCore(id, false);
        foreach (var id in svgs.Where(id => !includedFolders.Contains(LibraryAssetFolderId(id)!))) DuplicateSharedAssetCore(id, true);
        foreach (var id in roots) DuplicateVaultFolder(id, subtrees[id]);
        RefreshDrawingObjectAssetPresentation();
    }

    private void DuplicateVaultFolder(string rootId, HashSet<string> subtree)
    {
        // The domain method appends folders in breadth-first order and symbols in
        // project order. Preserve those explicit correspondences when adding resources.
        var sourceFolders = new List<ProjectAssetFolder>();
        var queue = new Queue<string>();
        queue.Enqueue(rootId);
        while (queue.TryDequeue(out var parent))
        {
            sourceFolders.Add(_project.AssetFolders.First(folder => folder.Id == parent));
            foreach (var child in _project.AssetFolders.Where(folder => folder.ParentFolderId == parent)) queue.Enqueue(child.Id);
        }
        var originalFolderIds = _project.AssetFolders.Select(folder => folder.Id).ToHashSet(StringComparer.Ordinal);
        var originalSymbolIds = _project.DrawingObjects.Select(symbol => symbol.Id).ToHashSet(StringComparer.Ordinal);
        var images = _project.ImageAssets.Where(asset => subtree.Contains(asset.AssetFolderId)).ToArray();
        var svgs = _project.ExternalSvgAssets.Where(asset => subtree.Contains(asset.AssetFolderId)).ToArray();
        RequireVaultChange(_project.TryDuplicateAssetFolder(rootId, out _));
        var copiedFolders = _project.AssetFolders.Where(folder => !originalFolderIds.Contains(folder.Id)).ToArray();
        if (sourceFolders.Count != copiedFolders.Length) throw new InvalidOperationException("The folder copy is incomplete.");
        var folderMap = sourceFolders.Select((source, index) => (source.Id, Copy: copiedFolders[index].Id))
            .ToDictionary(pair => pair.Id, pair => pair.Copy, StringComparer.Ordinal);
        var imageMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var svgMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var image in images)
        {
            var copyId = DuplicateSharedAssetCore(image.Id, false);
            RequireVaultChange(_project.TryMoveLibraryAssetToFolder(copyId, folderMap[image.AssetFolderId]));
            imageMap.Add(image.Id, copyId);
        }
        foreach (var svg in svgs)
        {
            var copyId = DuplicateSharedAssetCore(svg.Id, true);
            RequireVaultChange(_project.TryMoveLibraryAssetToFolder(copyId, folderMap[svg.AssetFolderId]));
            svgMap.Add(svg.Id, copyId);
        }
        foreach (var symbol in _project.DrawingObjects.Where(symbol => !originalSymbolIds.Contains(symbol.Id)))
        {
            foreach (var (source, target) in svgMap)
                foreach (var index in symbol.Scene.FindImportedSvgObjectsUsingAsset(source))
                    RequireVaultChange(symbol.Scene.TrySetImportedSvgAsset(index, target));
            foreach (var (source, target) in imageMap)
                foreach (var index in symbol.Scene.FindBitmapObjectsUsingImageAsset(source))
                    if (symbol.Scene.TryGetBitmapObjectData(index, out var data))
                        RequireVaultChange(symbol.Scene.TryUpdateBitmapObject(index, new BitmapObjectData
                        {
                            ImageAssetId = target,
                            PlacedSize = data.PlacedSize,
                            VisibleContours = data.VisibleContours
                        }));
        }
    }

    private void ExportVaultSelection(string[] symbols, string[] images, string[] svgs, string[] folders, bool symbolPackage)
    {
        if (images.Length > 0 || folders.Length > 0 || symbolPackage && svgs.Length > 0)
        {
            ShowVaultNotice(UiLocalization.T("SVG export supports symbols and SVG assets. Symbol file export supports symbols only. Select compatible assets to continue."));
            return;
        }
        using var dialog = new FolderBrowserDialog
        {
            Description = UiLocalization.T("Choose an export folder"),
            InitialDirectory = ExportInitialDirectory(),
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var errors = new List<string>();
        var exported = 0;
        var previousCursor = Cursor;
        Cursor = Cursors.WaitCursor;
        try
        {
            void Export(string name, string extension, Action<string> write)
            {
                var temporary = Path.Combine(dialog.SelectedPath, $".v2d-export-{Guid.NewGuid():N}{extension}");
                try
                {
                    write(temporary);
                    var stem = SafeSvgFileName(name);
                    for (var suffix = 1; ; suffix++)
                    {
                        var destination = Path.Combine(dialog.SelectedPath, stem + (suffix == 1 ? "" : $" ({suffix})") + extension);
                        try { File.Move(temporary, destination, overwrite: false); break; }
                        catch (IOException) when (File.Exists(destination) || Directory.Exists(destination)) { }
                    }
                    exported++;
                }
                catch (Exception exception)
                {
                    errors.Add(name + ": " + exception.Message);
                    AppLog.Error($"Unable to export library asset: {name}", exception);
                }
                finally
                {
                    try { if (File.Exists(temporary)) File.Delete(temporary); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            foreach (var id in symbols)
            {
                var symbol = FindVaultSymbol(id)!;
                if (symbolPackage) Export(symbol.Name, DrawingObjectSymbolPackage.FileExtension,
                    path => DrawingObjectSymbolPackageService.Export(_project, id, path));
                else Export(symbol.Name, ".svg", path => DrawingObjectSvgExport.Write(path, symbol, _drawingObjects,
                    ResolveDrawingObjectExportFrame(symbol), _playbackSettings.Fps));
            }
            foreach (var id in svgs)
            {
                var svg = _project.FindExternalSvgAsset(id)!;
                Export(svg.Name, ".svg", path => File.Copy(ResolveExternalSvgAssetPath(svg)
                    ?? throw new FileNotFoundException("The SVG resource is unavailable."), path, overwrite: false));
            }
        }
        finally { Cursor = previousCursor; }
        var summary = string.Format(UiLocalization.T("Exported {0} file(s). Existing files were preserved."), exported);
        if (errors.Count > 0)
            summary += "\r\n\r\n" + string.Format(UiLocalization.T("{0} export(s) failed:"), errors.Count)
                + "\r\n" + string.Join("\r\n", errors.Take(12));
        ShowVaultNotice(summary, errors.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
    }

    private static void RequireVaultChange(bool succeeded)
    {
        if (!succeeded) throw new InvalidOperationException("The library asset could not be changed.");
    }

    private void ShowVaultNotice(string text, MessageBoxIcon icon = MessageBoxIcon.Information) =>
        ModernMessageDialog.Show(this, text, UiLocalization.T("Project Assets"), MessageBoxButtons.OK, icon);
}
