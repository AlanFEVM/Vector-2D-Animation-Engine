namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private sealed record SharedAssetUndoEntry(
        VectorProject Project,
        ExternalSvgAssetRestartSnapshot[] SvgAssets,
        ImageAssetRestartSnapshot[] ImageAssets,
        AssetSceneUndo[] Scenes,
        string? TargetAssetId)
    {
        public HashSet<string>? SvgIds { get; set; }
        public HashSet<string>? ImageIds { get; set; }
    }

    private sealed record AssetSceneUndo(string DrawingId, string SceneId, string LayerId,
        VectorSceneSnapshot Snapshot, AnimationTimelineSnapshot? OuterTimeline = null);

    private readonly Stack<SharedAssetUndoEntry> _sharedAssetUndoStack = new();

    private SharedAssetUndoEntry CaptureSharedAssetUndo(string? assetId = null) => new(
        _project,
        _project.CreateExternalSvgAssetRestartSnapshots(),
        _project.CreateImageAssetRestartSnapshots(),
        CaptureAssetScenes(assetId), assetId);

    private AssetSceneUndo[] CaptureAssetScenes(string? assetId)
    {
        if (assetId is null) return [];
        bool Uses(VectorScene scene) => scene.FindImportedSvgObjectsUsingAsset(assetId).Length > 0
            || scene.FindBitmapObjectsUsingImageAsset(assetId).Length > 0;
        var snapshots = new List<AssetSceneUndo>();
        foreach (var drawing in _project.DrawingObjects)
            if (Uses(drawing.Scene)) snapshots.Add(new(drawing.Id, "", "", drawing.Scene.CreateSnapshot()));
        foreach (var scene in _project.Scenes)
            foreach (var layer in scene.Layers)
                if (layer.MaskScene is { } mask && Uses(mask))
                    snapshots.Add(new("", scene.Id, layer.Id, mask.CreateSnapshot(), scene.Timeline.CreateSnapshot()));
        return snapshots.ToArray();
    }

    private void FreezeSharedAssetUndoIds(SharedAssetUndoEntry entry)
    {
        entry.SvgIds ??= entry.TargetAssetId is { } svgId
            ? entry.SvgAssets.Any(item => item.Id == svgId) ? [svgId] : []
            : _project.ExternalSvgAssets.Select(item => item.Id).Except(entry.SvgAssets.Select(item => item.Id)).ToHashSet(StringComparer.Ordinal);
        entry.ImageIds ??= entry.TargetAssetId is { } imageId
            ? entry.ImageAssets.Any(item => item.Id == imageId) ? [imageId] : []
            : _project.ImageAssets.Select(item => item.Id).Except(entry.ImageAssets.Select(item => item.Id)).ToHashSet(StringComparer.Ordinal);
    }

    private void PushSharedAssetUndo(SharedAssetUndoEntry entry)
    {
        FreezeSharedAssetUndoIds(entry);
        _sharedAssetUndoStack.Push(entry);
        RecordUndoSequence(entry);
        long retainedBytes = 0;
        var retained = _sharedAssetUndoStack.TakeWhile((item, index) =>
        {
            retainedBytes += item.Scenes.Sum(scene => scene.Snapshot.EstimateMemoryBytes());
            return index == 0 || index < MaxUndoSnapshots && retainedBytes <= MaxUndoSnapshotBytes;
        }).Reverse().ToArray();
        _sharedAssetUndoStack.Clear();
        foreach (var item in retained) _sharedAssetUndoStack.Push(item);
        PruneUndoSequenceEntries();
        MarkProjectDirty();
    }

    private void RestoreSharedAssetUndo(SharedAssetUndoEntry entry)
    {
        if (!ReferenceEquals(entry.Project, _project)) return;
        FreezeSharedAssetUndoIds(entry);
        _project.RestoreExternalSvgAssets(_project.CreateExternalSvgAssetRestartSnapshots()
            .Where(item => !entry.SvgIds!.Contains(item.Id))
            .Concat(entry.SvgAssets.Where(item => entry.SvgIds!.Contains(item.Id))));
        _project.RestoreImageAssets(_project.CreateImageAssetRestartSnapshots()
            .Where(item => !entry.ImageIds!.Contains(item.Id))
            .Concat(entry.ImageAssets.Where(item => entry.ImageIds!.Contains(item.Id))));
        foreach (var item in entry.Scenes)
        {
            var scene = item.DrawingId.Length > 0
                ? _project.DrawingObjects.FirstOrDefault(drawing => drawing.Id == item.DrawingId)?.Scene
                : _project.Scenes.FirstOrDefault(owner => owner.Id == item.SceneId)?.FindMaskScene(item.LayerId);
            if (scene is null) throw new InvalidOperationException("The asset undo target no longer exists.");
            scene.RestoreSnapshot(item.Snapshot);
            if (item.OuterTimeline is not null)
                _project.Scenes.First(owner => owner.Id == item.SceneId).Timeline.RestoreSnapshot(item.OuterTimeline);
        }
        RefreshSharedAssets();
    }

    private void RefreshSharedAssets()
    {
        BitmapImageRasterizer.ClearCache();
        MarkProjectDirty();
        ClearSelection();
        _libraryVaultPanel.RefreshProjectObjects();
        _hierarchyPanel.RefreshScene();
        _timeline.RefreshTimeline();
        RebuildEditableInstanceComposition();
        UpdateInspector();
        _stage.Invalidate();
    }

    private string[] AssetUsageDescriptions(string assetId, bool svg)
    {
        bool Uses(VectorScene scene) => svg
            ? scene.FindImportedSvgObjectsUsingAsset(assetId).Length > 0
            : scene.FindBitmapObjectsUsingImageAsset(assetId).Length > 0;
        var descriptions = _project.EnumerateAssetScenes()
            .Select(item => (item.Name, Count: svg
                ? item.Scene.FindImportedSvgObjectsUsingAsset(assetId).Length
                : item.Scene.FindBitmapObjectsUsingImageAsset(assetId).Length))
            .Where(item => item.Count > 0)
            .Select(item => string.Format(UiLocalization.T("{0}: {1} placement(s), including keyframes"), item.Name, item.Count))
            .ToList();
        var affected = _project.DrawingObjects.Where(drawing => Uses(drawing.Scene))
            .Select(drawing => drawing.Id).ToHashSet(StringComparer.Ordinal);
        var expanded = true;
        while (expanded)
        {
            expanded = false;
            foreach (var drawing in _project.DrawingObjects)
                if (!affected.Contains(drawing.Id) && drawing.Instances.Any(instance => affected.Contains(instance.DrawingObjectId)))
                {
                    affected.Add(drawing.Id);
                    descriptions.Add(string.Format(UiLocalization.T("Symbol {0}: affected through nested references"), drawing.Name));
                    expanded = true;
                }
        }
        foreach (var scene in _project.Scenes)
            if (scene.Instances.Any(instance => affected.Contains(instance.DrawingObjectId)))
                descriptions.Add(string.Format(UiLocalization.T("Scene {0}: affected through symbol references"), scene.Name));
        return descriptions.ToArray();
    }

    private void DeleteSharedAsset(string assetId, bool svg)
    {
        var name = svg ? FindExternalSvgAsset(assetId)?.Name : FindImageAsset(assetId)?.Name;
        if (name is null) return;
        var usages = AssetUsageDescriptions(assetId, svg);
        string? replacementId = null;
        if (usages.Length > 0)
        {
            var replacements = svg
                ? _project.ExternalSvgAssets.Where(asset => asset.Id != assetId && ResolveExternalSvgAssetPath(asset) is not null)
                    .Select(asset => (asset.Id, asset.Name)).ToArray()
                : _project.ImageAssets.Where(asset => asset.Id != assetId && ResolveImageAssetPath(asset) is not null)
                    .Select(asset => (asset.Id, asset.Name)).ToArray();
            using var dialog = new AssetDeleteDialog(name, usages, replacements);
            var result = dialog.ShowDialog(this);
            if (result != DialogResult.OK && result != DialogResult.Yes) return;
            if (result == DialogResult.Yes)
            {
                replacementId = dialog.ReplacementAssetId;
                if (string.IsNullOrEmpty(replacementId)) return;
            }
        }
        var before = CaptureSharedAssetUndo(assetId);
        try
        {
            var replacementSource = svg && replacementId is not null
                ? ImportedSvgRasterizer.Load(ResolveExternalSvgAssetPath(FindExternalSvgAsset(replacementId)!)!).Source
                : null;
            foreach (var (_, scene) in _project.EnumerateAssetScenes())
            {
                var objects = svg ? scene.FindImportedSvgObjectsUsingAsset(assetId)
                    : scene.FindBitmapObjectsUsingImageAsset(assetId);
                if (replacementId is null)
                {
                    scene.RemoveObjects(objects);
                    continue;
                }
                foreach (var index in objects.OrderDescending())
                {
                    if (svg)
                    {
                        if (!scene.TrySetImportedSvgAsset(index, replacementId))
                            throw new InvalidOperationException("The SVG reference could not be replaced.");
                    }
                    else if (scene.TryGetBitmapObjectData(index, out var data))
                    {
                        if (!scene.TryUpdateBitmapObject(index, new BitmapObjectData
                        {
                            ImageAssetId = replacementId,
                            PlacedSize = new SizeF(scene.Width[index], scene.Height[index]),
                            VisibleContours = data.VisibleContours
                        })) throw new InvalidOperationException("The image reference could not be replaced.");
                    }
                }
                if (replacementSource is not null) scene.UpdateImportedSvgAssetSource(replacementId!, replacementSource);
            }
            var removed = svg ? _project.TryRemoveExternalSvgAsset(assetId, out _)
                : _project.TryRemoveImageAsset(assetId, out _);
            if (!removed) throw new InvalidOperationException("The library asset could not be removed.");
            foreach (var item in before.Scenes.Where(item => item.SceneId.Length > 0))
            {
                var owner = _project.Scenes.First(scene => scene.Id == item.SceneId);
                var mask = owner.FindMaskScene(item.LayerId)!;
                var frames = item.Snapshot.ObjectKeyframeFrame
                    .Concat(mask.Timeline.Tracks.SelectMany(track => track.Keyframes.Select(key => key.Frame)))
                    .Append(0).Distinct().Order().ToArray();
                foreach (var frame in frames) owner.SynchronizeMaskTimelineContent(item.LayerId, frame);
            }
            PushSharedAssetUndo(before);
            RefreshSharedAssets();
        }
        catch (Exception exception)
        {
            RestoreSharedAssetUndo(before);
            ShowProjectFileError("The library asset could not be changed.", exception);
        }
    }

    private void UpdateSvgAssetFromSource(string assetId)
    {
        var asset = FindExternalSvgAsset(assetId);
        if (asset is not null) UpdateSvgAssetFromPath(asset, asset.OriginalSourcePath);
    }

    private void UpdateSvgAssetFromPath(ExternalSvgAssetDefinition asset, string sourcePath)
    {
        if (!TryValidateExternalSvgAssetFile(sourcePath, out var path, out var sha256, out var error))
        {
            ShowExternalSvgAssetError("The SVG could not be updated.", error);
            return;
        }
        var before = CaptureSharedAssetUndo(asset.Id);
        try
        {
            if (!_project.TryRelocateExternalSvgAsset(asset.Id, path, ProjectRelativeExternalSvgPath(path), sha256))
                throw new InvalidDataException("The selected SVG source is invalid.");
            PushSharedAssetUndo(before);
            RefreshSharedAssets();
        }
        catch (Exception exception)
        {
            RestoreSharedAssetUndo(before);
            ShowExternalSvgAssetError("The SVG could not be updated.", exception.Message);
        }
    }

    private void UpdateImageAssetFromSource(string assetId)
    {
        var asset = FindImageAsset(assetId);
        if (asset is not null) UpdateImageAssetFromPath(asset, asset.OriginalSourcePath);
    }

    private void UpdateImageAssetFromPath(ImageAssetDefinition asset, string sourcePath)
    {
        SharedAssetUndoEntry? before = null;
        try
        {
            var path = Path.GetFullPath(sourcePath);
            ProjectTemporaryResources.RejectReparsePath(path);
            if (!BitmapImageRasterizer.TryProbe(path, out var width, out var height, out _, out var error))
                throw new InvalidDataException(error);
            _ = BitmapImageRasterizer.Decode(path, asset.ImportSettings);
            var hash = BitmapImageRasterizer.ComputeSha256(path);
            before = CaptureSharedAssetUndo(asset.Id);
            if (!_project.TryRelinkImageAsset(asset.Id, path, asset.ProjectRelativePath, hash,
                    width, height, asset.NaturalPixelsPerUnit))
                throw new InvalidDataException("The selected file is not a valid image.");
            PushSharedAssetUndo(before);
            RefreshSharedAssets();
        }
        catch (Exception exception)
        {
            if (before is not null) RestoreSharedAssetUndo(before);
            ShowImageAssetError("The image could not be relinked.", exception.Message);
        }
    }

    private void DuplicateSharedAsset(string assetId, bool svg)
    {
        var before = CaptureSharedAssetUndo();
        try
        {
            bool added;
            if (svg)
            {
                var asset = FindExternalSvgAsset(assetId) ?? throw new InvalidOperationException("Missing SVG asset.");
                var path = ResolveExternalSvgAssetPath(asset) ?? throw new FileNotFoundException(asset.Name);
                added = _project.TryAddExternalSvgAsset(IndependentAssetName(asset.Name), path, "",
                    asset.LastKnownSha256, out var copy);
                if (copy is not null) copy.OriginalSourcePath = asset.OriginalSourcePath;
            }
            else
            {
                var asset = FindImageAsset(assetId) ?? throw new InvalidOperationException("Missing image asset.");
                var path = ResolveImageAssetPath(asset) ?? throw new FileNotFoundException(asset.Name);
                added = _project.TryAddImageAssetFromSource(IndependentAssetName(asset.Name), path, asset.Sha256,
                    asset.PixelWidth, asset.PixelHeight, asset.NaturalPixelsPerUnit, asset.ImportSettings, out var copy);
                if (copy is not null) copy.OriginalSourcePath = asset.OriginalSourcePath;
            }
            if (!added) throw new InvalidOperationException("The library is full or the asset is invalid.");
            PushSharedAssetUndo(before);
            RefreshSharedAssets();
        }
        catch (Exception exception)
        {
            RestoreSharedAssetUndo(before);
            ShowProjectFileError("The library asset could not be changed.", exception);
        }
    }

    private static string IndependentAssetName(string name) =>
        name[..Math.Min(name.Length, 64)] + " " + UiLocalization.T("Copy");

    private ExternalSvgAssetDefinition? RegisterSharedSvgFile(string fileName)
    {
        if (!TryValidateExternalSvgAssetFile(fileName, out var path, out var hash, out var error))
            throw new InvalidDataException(error);
        // Reimporting the same source places the existing project resource. Updating it is
        // an explicit library action, so an external edit cannot silently change a scene.
        var existing = _project.ExternalSvgAssets.FirstOrDefault(asset =>
            string.Equals(asset.OriginalSourcePath, path, StringComparison.OrdinalIgnoreCase)
            || string.Equals(asset.SourcePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;
        if (!_project.TryAddExternalSvgAsset(ImportedSvgDisplayName(path), path,
                ProjectRelativeExternalSvgPath(path), hash, out var added))
            throw new InvalidDataException("The SVG asset could not be registered.");
        _libraryVaultPanel.RefreshProjectObjects();
        return added;
    }
}
