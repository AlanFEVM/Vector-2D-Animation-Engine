namespace VectorAnimationEngine;

internal sealed partial class VectorProject
{
    internal sealed record LibraryHistoryState(
        DrawingObjectDefinition[] Symbols,
        (DrawingObjectDefinition Symbol, string Name, string Folder, string[] Tags)[] Metadata,
        ProjectAssetFolderRestartSnapshot[] Folders, ProjectAssetTagRestartSnapshot[] Tags,
        ImageAssetRestartSnapshot[] Images, ExternalSvgAssetRestartSnapshot[] Svg,
        (ITimelineContext Owner, DrawingObjectInstanceDefinition[] Instances, AnimationTimelineSnapshot Timeline)[] References,
        (VectorScene Scene, VectorSceneSnapshot Snapshot)[] Content,
        (SceneDefinition Scene, SceneCanvasSettings? Settings, bool WelcomeDismissed)[] Canvases);

    internal LibraryHistoryState CaptureLibraryHistory(bool includeContent = false)
    {
        var owners = _drawingObjects.Cast<ITimelineContext>().Concat(_scenes).ToArray();
        return new(_drawingObjects.ToArray(),
            _drawingObjects.Select(symbol => (symbol, symbol.Name, symbol.AssetFolderId, symbol.AssetTagIds.ToArray())).ToArray(),
            _assetFolders.Select(folder => new ProjectAssetFolderRestartSnapshot
                { Id = folder.Id, Name = folder.Name, ParentFolderId = folder.ParentFolderId, CreatedAt = folder.CreatedAt }).ToArray(),
            _assetTags.Select(tag => new ProjectAssetTagRestartSnapshot
                { Id = tag.Id, Name = tag.Name, ColorArgb = tag.ColorArgb }).ToArray(),
            CreateImageAssetRestartSnapshots(), CreateExternalSvgAssetRestartSnapshots(),
            includeContent ? owners.Select(owner => (owner,
                owner is SceneDefinition scene ? scene.CreateInstanceSnapshot() : ((DrawingObjectDefinition)owner).CreateInstanceSnapshot(),
                owner.Timeline.CreateSnapshot())).ToArray() : [],
            includeContent ? EnumerateAssetScenes().Select(item => item.Scene).Distinct()
                .Select(scene => (scene, scene.CreateSnapshot())).ToArray() : [],
            includeContent ? _scenes.Select(scene => (scene, scene.Canvas, scene.CanvasWelcomeDismissed)).ToArray() : []);
    }

    internal void RestoreLibraryHistory(LibraryHistoryState state)
    {
        _drawingObjects.Clear();
        _drawingObjects.AddRange(state.Symbols);
        _assetFolders.Clear();
        RestoreAssetFolders(state.Folders);
        RestoreAssetTags(state.Tags);
        foreach (var (symbol, name, folder, tags) in state.Metadata)
        {
            symbol.Name = name;
            symbol.AssetFolderId = folder;
            symbol.ReplaceAssetTagIds(tags);
        }
        RestoreImageAssets(state.Images);
        RestoreExternalSvgAssets(state.Svg);
        foreach (var (scene, settings, dismissed) in state.Canvases) scene.RestoreCanvas(settings, dismissed);
        foreach (var (scene, snapshot) in state.Content) scene.RestoreSnapshot(snapshot);
        foreach (var (owner, instances, timeline) in state.References)
        {
            if (owner is SceneDefinition scene) scene.RestoreInstanceSnapshot(instances);
            else ((DrawingObjectDefinition)owner).RestoreInstanceSnapshot(instances);
            owner.Timeline.RestoreSnapshot(timeline);
            owner.SynchronizeTimelineTracks();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal void RestoreSceneListHistory(SceneDefinition[] scenes)
    {
        if (scenes.Length == 0 || scenes.Select(scene => scene.Id).Distinct().Count() != scenes.Length)
            throw new InvalidOperationException("A project must retain distinct scenes.");
        _scenes.Clear();
        _scenes.AddRange(scenes);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool TryRenameScene(string sceneId, string name)
    {
        var scene = FindScene(sceneId);
        var normalized = name?.Trim() ?? "";
        if (scene is null || normalized.Length is 0 or > 128) return false;
        scene.Name = normalized;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }
}
