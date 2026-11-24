namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private bool CompleteImageBatchPlacement(IReadOnlyList<string> assetIds, int candidateCount)
    {
        if (assetIds.Count == 0 || candidateCount <= 0
            || candidateCount > _applicationSettings.ImageImportAutoPlaceLimit) return false;
        return PlaceLibraryAssets(new(_project.Id, [], assetIds.ToArray(), []), null);
    }

    private bool CanPlaceLibraryAssets(VaultStageDragData data)
    {
        if (data.ProjectId != _project.Id || data.Count == 0 || _playing || IsShotDirectorContext()) return false;
        if (data.ImageIds.Length > 0 && (!CanPlaceImage()
            || data.ImageIds.Any(id => FindImageAsset(id) is not { } asset || ResolveImageAssetPath(asset) is null))) return false;
        if (data.SvgIds.Length > 0 && (!CanImportSvg()
            || data.SvgIds.Any(id => FindExternalSvgAsset(id) is not { } asset || ResolveExternalSvgAssetPath(asset) is null))) return false;
        return data.SymbolIds.All(id => FindVaultSymbol(id) is { IsOpenCanvas: false } symbol && CanPlaceDroppedDrawingObject(symbol));
    }

    private bool TryResolveLibraryStageDrop(IDataObject? data, out VaultStageDragData batch)
    {
        batch = data?.GetData(typeof(VaultStageDragData)) as VaultStageDragData ?? null!;
        return batch is not null && CanPlaceLibraryAssets(batch);
    }

    // Prepare every file before the mutation, then publish one history entry and one UI refresh.
    // Library assets retain their IDs and files; the stage receives placements referencing them.
    private bool PlaceLibraryAssets(VaultStageDragData data, PointF? center)
    {
        if (!CommitTextEdit() || !CanPlaceLibraryAssets(data)) return false;
        if (data.ImageIds.Length + data.SvgIds.Length > 0) EnsureSceneDrawingSurface();
        var viewport = _stage.VisibleWorldBounds();
        var placement = center ?? new PointF(viewport.Left + viewport.Width / 2, viewport.Top + viewport.Height / 2);
        var previousCursor = Cursor;
        Cursor = Cursors.WaitCursor;
        VectorSceneSnapshot? before = null;
        VectorProject.LibraryHistoryState? projectBefore = null;
        try
        {
            var images = data.ImageIds.Distinct(StringComparer.Ordinal).Select(id =>
            {
                var asset = FindImageAsset(id)!;
                var raster = BitmapImageRasterizer.Decode(ResolveImageAssetPath(asset)!, asset.ImportSettings);
                return new BitmapObjectData
                {
                    ImageAssetId = id,
                    PlacedSize = ScalePlacedSizeForDecode(asset.ResolvePlacedSize(), asset, raster)
                };
            }).ToArray();
            var svgs = data.SvgIds.Distinct(StringComparer.Ordinal).Select(id =>
            {
                var asset = FindExternalSvgAsset(id)!;
                var imported = ImportedSvgRasterizer.Load(ResolveExternalSvgAssetPath(asset)!);
                var scale = Math.Min(1f, 512f / Math.Max(imported.IntrinsicSize.Width, imported.IntrinsicSize.Height));
                _ = ImportedSvgRasterizer.Rasterize(imported.Source,
                    Math.Max(1, imported.IntrinsicSize.Width * scale), Math.Max(1, imported.IntrinsicSize.Height * scale));
                return (Asset: asset, Source: imported.Source, Size: ImportedSvgInitialSize(imported.IntrinsicSize, viewport));
            }).ToArray();
            if (!CanPlaceLibraryAssets(data)) return false;
            if (data.SymbolIds.Length > 0) projectBefore = _project.CaptureLibraryHistory(includeContent: true);
            if (images.Length + svgs.Length > 0)
                before = CreateCanvasMutationSnapshot(affectedLayers: [_scene.ActiveLayer]);
            var selected = new List<int>();
            var instances = new List<DrawingObjectInstanceDefinition>();
            foreach (var image in images)
            {
                var index = _scene.AddBitmapObject(_scene.ActiveLayer, placement, image);
                if (index < 0) throw new InvalidOperationException("The active layer rejected the object.");
                selected.Add(index);
            }
            foreach (var svg in svgs)
            {
                var index = _scene.AddImportedSvgObject(_scene.ActiveLayer, placement, svg.Size, svg.Source, svg.Asset.Name);
                if (index < 0 || !_scene.TrySetImportedSvgAsset(index, svg.Asset.Id))
                    throw new InvalidOperationException("The SVG placement could not reference its library asset.");
                selected.Add(index);
            }
            foreach (var id in data.SymbolIds.Distinct(StringComparer.Ordinal))
            {
                DrawingObjectInstanceDefinition? instance;
                var success = IsSceneCompositionContext()
                    ? _project.TryAddSceneInstance(ActiveScene()!.Id, id, placement, 0, out instance)
                    : _project.TryAddDrawingObjectInstance(ActiveDrawingObject()!.Id, id, placement,
                        ActiveDrawingObject()!.Scene.ResolveInstanceLayerId(null), out instance);
                if (!success || instance is null) throw new InvalidOperationException("The symbol could not be placed.");
                instances.Add(instance);
            }
            if (projectBefore is { } captured)
            {
                var after = _project.CaptureLibraryHistory(includeContent: true);
                PushPanelHistory(new(_project, () => RestoreLibraryPlacement(captured), () => RestoreLibraryPlacement(after),
                    captured.Content.Sum(item => item.Snapshot.EstimateMemoryBytes())
                    + after.Content.Sum(item => item.Snapshot.EstimateMemoryBytes())));
            }
            else if (before is not null) PushUndoSnapshot(before);
            if (IsSceneDrawingContext() || IsSceneCompositionContext()) RebuildSceneComposition();
            else if (IsSceneMaskEditing() || data.SymbolIds.Length > 0) RebuildDrawingObjectUnderlay();
            SetSelection(selected);
            if (instances.Count > 0)
                SetSceneInstanceSelectionCore(instances, instances[^1], preserveDrawingSelection: true);
            _hierarchyPanel.RefreshScene();
            _sceneEditorPanel.BindProject(_scenes, _activeSceneIndex, _drawingObjects);
            UpdateInspector();
            _stage.Invalidate();
            return true;
        }
        catch (Exception exception)
        {
            if (projectBefore is not null) RestoreLibraryPlacement(projectBefore);
            else if (before is not null) RestoreCanvasMutationSnapshot(before);
            AppLog.Error("Unable to place selected library assets.", exception);
            ShowProjectFileError("The library asset could not be changed.", exception);
            return false;
        }
        finally { Cursor = previousCursor; }
    }

    private void RestoreLibraryPlacement(VectorProject.LibraryHistoryState state)
    {
        _project.RestoreLibraryHistory(state);
        if (IsSceneWorkspaceSelected) BindSceneEditStageCore(resetView: false, preserveHistory: true);
        else BindActiveDrawingObjectSceneCore(resetView: false, preserveHistory: true);
        RefreshSharedAssets();
    }
}
