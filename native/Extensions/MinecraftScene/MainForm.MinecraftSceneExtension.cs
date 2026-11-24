namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    partial void AddNativeExtensionCommands(Action<string, Action> addCommand)
    {
        if (MinecraftSectionImport.IsAvailable(AppContext.BaseDirectory))
            addCommand("Minecraft Scene...", OpenMinecraftSceneExtension);
    }

    partial void CreateNativeExtensionInspector(ref Control? panel)
    {
        if (!MinecraftSectionImport.IsAvailable(AppContext.BaseDirectory)) return;
        var generator = new MinecraftSceneExtensionPanel();
        generator.GenerateRequested += plan => GenerateMinecraftScene(generator, plan);
        generator.ExitChunkRequested += () => CloseNativeExtensionChunk(restoreTimeline: true);
        panel = generator;
    }

    private void OpenMinecraftSceneExtension()
    {
        if (!_inspectorPanelOpen) ToggleInspectorPanel();
        RevealInspectorCategory(InspectorCategory.Extension, "Minecraft scene extension");
    }

    partial void RefreshNativeExtensionInspector()
    {
        if (_extensionInspectorPage is not MinecraftSceneExtensionPanel panel) return;
        var scene = IsSceneCompositionContext() ? ActiveScene() : null;
        var (layer, depth) = MinecraftActiveLayer(scene);
        panel.BindBlockEditor(scene?.BlockGrid, layer is null ? null : SceneBlockChunk.DepthLabel(depth));
        if (_minecraftChunkEditor is { } editor && ReferenceEquals(editor.Scene, scene))
            panel.BeginChunkEditing(editor.Chunk);
    }

    partial void HandleNativeExtensionStageClick(MouseEventArgs e, ref bool handled)
    {
        if (!IsSceneCompositionContext() || ActiveScene() is not { BlockGrid: { } grid } scene
            || e.Button is not (MouseButtons.Left or MouseButtons.Right)) return;
        var editor = _minecraftChunkEditor;
        if (editor is null)
        {
            handled = TrySelectMinecraftChunk(e);
            return;
        }
        if (!ReferenceEquals(editor.Scene, scene) || !ReferenceEquals(editor.Grid, grid))
        {
            CloseNativeExtensionChunk(restoreTimeline: true);
            return;
        }
        handled = true;
        if (_extensionInspectorPage is not MinecraftSceneExtensionPanel panel) return;
        if (!_inspectorPanelOpen || _inspectorCategory != InspectorCategory.Extension) OpenMinecraftSceneExtension();
        var (layer, depth) = MinecraftActiveLayer(scene);
        if (e.Button == MouseButtons.Left && panel.Tool != MinecraftSceneExtensionPanel.BlockTool.Locate && layer is null)
            return;
        handled = true;
        if (_playing || _frame != 0 || !IsScene2DFrontView())
        {
            panel.ReportBlockEdit(UiLocalization.T("Block editing requires the front view at frame 0."));
            return;
        }

        if (!TryGetMinecraftCell(e.Location, grid, out var x, out var y, out var world)
            || SceneBlockChunk.FromCell(x, y, editor.Chunk.Z * SceneBlockChunk.CellCount) != editor.Chunk) return;

        if (e.Button == MouseButtons.Right)
        {
            var candidates = grid.Column(x, y, visibleOnly: false)
                .Where(item => editor.Chunk.Contains(grid, item.Cell) && scene.FindLayer(grid.Layers[item.Cell.Depth]) is not null).ToArray();
            var points = panel.Tool == MinecraftSceneExtensionPanel.BlockTool.LightPoint
                ? grid.Layers.Where(item => editor.Chunk.Contains(grid, new(x, y, item.Key))
                    && grid.GetLight(new(x, y, item.Key)) > 0).ToArray() : [];
            if (candidates.Length == 0 && points.Length == 0) return;
            var menu = new AnimatedContextMenuStrip();
            foreach (var (pointDepth, layerId) in points)
            {
                var pointIndex = Array.IndexOf(scene.DrawingScene.LayerIds, layerId);
                if (pointIndex < 0) continue;
                var point = new SceneBlockGrid.Cell(x, y, pointDepth);
                var label = $"{UiLocalization.T("Remove light point")}  |  {SceneBlockChunk.DepthLabel(pointDepth)}  |  {grid.GetLight(point)}";
                var item = menu.Items.Add(label, null, (_, _) => EditMinecraftLightPoint(panel, scene, grid, point, 0));
                item.Enabled = !scene.DrawingScene.IsLayerEffectivelyLocked(pointIndex)
                    && scene.DrawingScene.IsLayerEffectivelyVisible(pointIndex);
            }
            foreach (var (cell, material) in candidates)
            {
                var label = $"{SceneBlockChunk.DepthLabel(cell.Depth)}  |  {grid.Palette[material].Name}";
                menu.Items.Add(label, null, (_, _) => LocateMinecraftBlock(panel, scene, grid, cell, material));
            }
            menu.Closed += (_, _) => menu.Dispose();
            menu.Show(_stage, e.Location);
            return;
        }

        if (panel.Tool == MinecraftSceneExtensionPanel.BlockTool.Locate)
        {
            foreach (var (cell, material) in grid.Column(x, y, visibleOnly: true, chunkDepth: editor.Chunk.Z))
            {
                var layerIndex = Array.IndexOf(scene.DrawingScene.LayerIds, grid.Layers[cell.Depth]);
                if (layerIndex < 0 || !scene.DrawingScene.IsLayerEffectivelyVisible(layerIndex)) continue;
                if (grid.Palette[material].AssetId is { } assetId && ResolveBitmapImageRaster(assetId) is { } raster)
                {
                    var center = grid.Center(cell);
                    var texX = Math.Clamp((int)((0.5f + (world.X - center.X) / grid.CellSize) * raster.PixelWidth), 0, raster.PixelWidth - 1);
                    var texY = Math.Clamp((int)((0.5f + (world.Y - center.Y) / grid.CellSize) * raster.PixelHeight), 0, raster.PixelHeight - 1);
                    if (raster.Pixels[texY * raster.Stride + texX * 4 + 3] == 0) continue;
                }
                LocateMinecraftBlock(panel, scene, grid, cell, material);
                return;
            }
            panel.ReportBlockEdit(UiLocalization.T("No visible block here."));
            return;
        }

        var index = layer is null ? -1 : Array.IndexOf(scene.DrawingScene.LayerIds, layer.Id);
        if (index < 0 || scene.DrawingScene.IsLayerEffectivelyLocked(index) || !scene.DrawingScene.IsLayerEffectivelyVisible(index))
        {
            panel.ReportBlockEdit(UiLocalization.T("Select a visible, unlocked block layer in the timeline."));
            return;
        }
        var target = new SceneBlockGrid.Cell(x, y, depth);
        if (panel.Tool == MinecraftSceneExtensionPanel.BlockTool.LightPoint)
        {
            if (panel.CommitLightLevel() is { } level) EditMinecraftLightPoint(panel, scene, grid, target, level);
            return;
        }
        var previous = grid.Get(target);
        var next = panel.Tool == MinecraftSceneExtensionPanel.BlockTool.Erase ? -1 : panel.Material;
        if (panel.Tool == MinecraftSceneExtensionPanel.BlockTool.Paint && next < 0) return;
        var project = _project;
        try
        {
            if (!MinecraftBlockEditing.Apply(project, scene, target, next)) return;
        }
        catch (InvalidOperationException error)
        {
            panel.ReportBlockEdit(error.Message);
            return;
        }
        PushPanelHistory(new(project, () => Restore(previous), () => Restore(next)));
        RefreshMinecraftBlockEdit(scene);
        ReportMinecraftBlock(panel, grid, target, next);

        void Restore(int material)
        {
            MinecraftBlockEditing.Apply(project, scene, target, material);
            RefreshMinecraftBlockEdit(scene);
            if (!panel.IsDisposed && ReferenceEquals(ActiveScene(), scene)) ReportMinecraftBlock(panel, grid, target, material);
        }
    }

    private void EditMinecraftLightPoint(MinecraftSceneExtensionPanel panel, SceneDefinition scene,
        SceneBlockGrid grid, SceneBlockGrid.Cell cell, int level)
    {
        var project = _project;
        var previous = grid.GetLight(cell);
        try
        {
            if (!MinecraftBlockEditing.ApplyLight(project, scene, cell, level)) return;
        }
        catch (InvalidOperationException error)
        {
            panel.ReportBlockEdit(error.Message);
            return;
        }
        PushPanelHistory(new(project, () => Restore(previous), () => Restore(level)));
        RefreshAndReport(level);

        void Restore(int strength)
        {
            MinecraftBlockEditing.ApplyLight(project, scene, cell, strength, restoring: true);
            RefreshAndReport(strength);
        }

        void RefreshAndReport(int strength)
        {
            RefreshMinecraftBlockEdit(scene);
            if (!panel.IsDisposed && ReferenceEquals(ActiveScene(), scene))
                panel.ReportBlockEdit(string.Format(UiLocalization.T("Light at X {0}, Y {1}, depth {2}: {3}"),
                    cell.X, cell.Y, cell.Depth, strength));
        }
    }

    private (SceneLayerDefinition? Layer, int Depth) MinecraftActiveLayer(SceneDefinition? scene)
    {
        if (scene?.BlockGrid is not { } grid || _timeline.Context is not SceneBlockChunkTimeline editor
            || !ReferenceEquals(editor.Scene, scene)
            || editor.Timeline.FindTrack(_timeline.ActiveTrackId ?? "")?.TargetId is not { } id
            || !editor.TryGetDepth(id, out var depth)
            || !grid.Layers.TryGetValue(depth, out var layerId)) return (null, -1);
        return (scene.FindLayer(layerId), depth);
    }

    private void LocateMinecraftBlock(MinecraftSceneExtensionPanel panel, SceneDefinition scene,
        SceneBlockGrid grid, SceneBlockGrid.Cell cell, int material)
    {
        if (!ReferenceEquals(ActiveScene(), scene) || !ReferenceEquals(scene.BlockGrid, grid)) return;
        var timeline = _timeline.Context.Timeline;
        if (timeline.FindTrackByTargetId(SceneBlockChunkTimeline.TargetId(cell.Depth)) is { } track)
            _timeline.ActiveTrackIndex = TrackIndex(timeline, track.Id);
        panel.SelectMaterial(material);
        RefreshNativeExtensionInspector();
        ReportMinecraftBlock(panel, grid, cell, material);
    }

    private static void ReportMinecraftBlock(MinecraftSceneExtensionPanel panel, SceneBlockGrid grid,
        SceneBlockGrid.Cell cell, int material) => panel.ReportBlockEdit(string.Format(
        UiLocalization.T("X {0}, Y {1}, depth {2}: {3}"), cell.X, cell.Y, cell.Depth,
        material < 0 ? UiLocalization.T("Empty block cell") : grid.Palette[material].Name));

    private void RefreshMinecraftBlockEdit(SceneDefinition scene)
    {
        MarkProjectDirty();
        if (!ReferenceEquals(ActiveScene(), scene) || !IsSceneCompositionContext()) return;
        ClearSelection();
        RebuildSceneComposition();
        _timeline.Invalidate();
        _stage.Invalidate();
    }

    private void GenerateMinecraftScene(MinecraftSceneExtensionPanel panel, MinecraftSectionImport.Plan plan)
    {
        CloseNativeExtensionChunk(restoreTimeline: true);
        if (!CommitTextEdit() || (IsSymbolEditing && !ExitSceneSymbolEditing())) return;
        FinishPointerInteractionForContextChange();
        StopPlayback();

        var before = _scenes.ToArray();
        var previousId = ActiveScene()?.Id ?? before[0].Id;
        SceneDefinition scene;
        ImageAssetRestartSnapshot[] assets;
        try
        {
            (scene, assets) = MinecraftSectionImport.CreateScene(_project, plan);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            AppLog.Error("Minecraft scene generation failed", exception);
            panel.ShowGenerationError(exception.Message);
            return;
        }

        var after = _scenes.ToArray();
        var assetIds = assets.Select(asset => asset.Id).ToHashSet(StringComparer.Ordinal);
        PushPanelHistory(new(_project,
            () =>
            {
                ApplySceneListFromHistory(before, previousId);
                _project.RestoreImageAssets(_project.CreateImageAssetRestartSnapshots().Where(asset => !assetIds.Contains(asset.Id)));
                RefreshSharedAssets();
            },
            () =>
            {
                _project.RestoreImageAssets(_project.CreateImageAssetRestartSnapshots().Where(asset => !assetIds.Contains(asset.Id)).Concat(assets));
                ApplySceneListFromHistory(after, scene.Id);
                RefreshSharedAssets();
            }));
        _workspaceTabs.SelectedView = WorkspaceView.SceneEditor;
        SelectScenePreservingHistory(after.Length - 1);
        _stage.SetReferenceViewDirection(ReferenceViewDirection.Front, ReferenceCameraMotion.Immediate);
        RefreshSharedAssets();
        _stage.Fit();
        panel.ShowGenerated(scene.Name);
    }
}
