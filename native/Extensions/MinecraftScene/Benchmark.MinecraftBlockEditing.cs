using Vortice.Direct2D1;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    static partial void RunMinecraftBlockEditingRegression()
    {
        VerifyBiomeTintImport();
        VerifyPropagation();
        VerifyLightingQuality();
        VerifyNightVision();
        var temporary = Directory.CreateTempSubdirectory("v2d-block-grid-");
        try
        {
            var source = Path.Combine(temporary.FullName, "section.json");
            var blocks = from x in Enumerable.Range(0, 6) from z in Enumerable.Range(0, 2)
                select new { x, y = 4, z, id = "test:stone", faceSky = 0, faceBlock = 15 };
            var bound = new { minX = 0, minY = 0, minZ = 0, maxX = 5, maxY = 4, maxZ = 1 };
            File.WriteAllText(source, System.Text.Json.JsonSerializer.Serialize(new
            {
                bounds = bound, front = "maxZ", skyDarken = 11,
                blocks = blocks.Append(new { x = 3, y = 1, z = 0, id = "test:stone", faceSky = 15, faceBlock = 15 })
            }));
            WriteTexture("stone", Color.Gray);
            WriteTexture("torch", Color.Gold);
            VerifyChunks(temporary.FullName);
            var project = new VectorProject();
            const SceneBlockLightQuality quality = SceneBlockLightQuality.HighDetailed;
            var plan = MinecraftSectionImport.Prepare(source, temporary.FullName, "", 16, true, default, editable: true, quality: quality);
            var flat = MinecraftSectionImport.Prepare(source, temporary.FullName, "", 16, true, default, quality: quality);
            var unlit = MinecraftSectionImport.Prepare(source, temporary.FullName, "", 16, false, default, editable: true, quality: quality);
            Require(plan.MissingCount == 0 && plan.Tiles.SequenceEqual(flat.Tiles)
                && unlit.Tiles.All(tile => (tile.ShadowCorners | tile.WarmLightCorners) == 0),
                "Initial lighting should match in both modes and respect its toggle.");

            var (scene, _) = MinecraftSectionImport.CreateScene(project, plan);
            var grid = scene.BlockGrid ?? throw new InvalidOperationException("Missing block grid.");
            var torch = Enumerable.Range(0, grid.Palette.Count).Single(i => grid.Palette[i].Name == MinecraftLightMaterials.Torch);
            var roof = new SceneBlockGrid.Cell(3, 0, 0);
            var front = new SceneBlockGrid.Cell(3, 3, 0);
            var face = front with { Depth = 1 };
            var point = new SceneBlockGrid.Cell(1, 3, 0);
            var torchCell = new SceneBlockGrid.Cell(2, 3, 0);
            var stone = grid.Get(roof);
            var dark = ReadFace(scene, face);
            Require(project.ImageAssets.Count == 2 && grid.Shadows.Count == 0 && dark.ShadowCorners != 0 && dark.WarmLightCorners == 0,
                "Lighting must ignore measured game brightness and must not require shadow assets.");

            Require(MinecraftBlockEditing.Apply(project, scene, front, stone), "Cover block.");
            Require(!TryReadFace(scene, face, out _), "Hidden block should not project.");
            Require(MinecraftBlockEditing.Apply(project, scene, front, -1), "Reveal block.");
            Require(ReadFace(scene, face).ShadowCorners == dark.ShadowCorners, "Revealed block changed its original lighting.");

            Require(MinecraftBlockEditing.Apply(project, scene, roof, -1), "Open the roof.");
            Require(ReadFace(scene, face).ShadowCorners < dark.ShadowCorners, "No skylight entered an edited opening.");
            Require(MinecraftBlockEditing.Apply(project, scene, roof, stone), "Close the roof.");
            Require(ReadFace(scene, face).ShadowCorners == dark.ShadowCorners, "Skylight remained after closing the roof.");

            Require(MinecraftBlockEditing.Apply(project, scene, torchCell, torch), "Stamp torch.");
            var torchLit = ReadFace(scene, face);
            Require(torchLit.WarmLightCorners != 0 && torchLit.ShadowCorners < dark.ShadowCorners,
                "Stamping a torch did not update another visible bitmap.");
            Require(MinecraftBlockEditing.ApplyLight(project, scene, point, 15), "Add a light point.");
            Require(MinecraftBlockEditing.Apply(project, scene, torchCell, -1), "Erase torch.");
            var pointLit = ReadFace(scene, face);
            Require(pointLit.WarmLightCorners != 0 && pointLit.ShadowCorners < dark.ShadowCorners,
                "The other source was lost when the torch was removed.");

            var manifest = ProjectVaultStore.Save(project, Path.Combine(temporary.FullName, "Fixture", "Fixture.v2dProject"));
            var reopened = ProjectVaultStore.Load(manifest);
            var restored = reopened.Scenes.Single(item => item.Id == scene.Id);
            var restoredBitmap = ReadFace(restored, face);
            Require(restored.BlockGrid is { LightQuality: quality } savedGrid && savedGrid.GetLight(point) == 15
                && restoredBitmap.WarmLightCorners == pointLit.WarmLightCorners
                && restoredBitmap.ShadowCorners == pointLit.ShadowCorners, "Light point or rendered values were not preserved on reopen.");
            Require(MinecraftBlockEditing.ApplyLight(reopened, restored, point, 0), "Remove a reopened light point.");
            var unlitBitmap = ReadFace(restored, face);
            Require(unlitBitmap.ShadowCorners == dark.ShadowCorners && unlitBitmap.WarmLightCorners == 0,
                "The last removed source left stale light on the stage.");

            var raster = new BitmapImageRaster(new BitmapImageRasterKey("mc-light-test", 2, 1));
            Array.Fill(raster.Pixels, (byte)128);
            var shaded = BitmapImageRasterizer.ApplyShadow(raster, torchLit.ShadowCorners, torchLit.WarmLightCorners);
            var neutral = BitmapImageRasterizer.ApplyShadow(raster, torchLit.ShadowCorners);
            var region = new Rectangle(0, 0, 1, 1);
            Require(shaded.Pixels[0] < shaded.Pixels[2] && shaded.Pixels[3] == 128 && shaded.Pixels[7] == 128
                && raster.Pixels[0] == 128 && shaded.Key != neutral.Key
                && ReferenceEquals(shaded, BitmapImageRasterizer.ApplyShadow(raster, torchLit.ShadowCorners, torchLit.WarmLightCorners))
                && BitmapImageRasterizer.RegionRasterKey(shaded.Key, region) != BitmapImageRasterizer.RegionRasterKey(neutral.Key, region),
                "Light shading altered alpha or did not isolate warm raster caches.");

            void WriteTexture(string name, Color color)
            {
                using var image = new Bitmap(2, 2);
                using (var graphics = Graphics.FromImage(image)) graphics.Clear(color);
                image.Save(Path.Combine(temporary.FullName, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
            }
        }
        finally { DeleteTemporaryDirectory(temporary.FullName); }
        if (Environment.GetEnvironmentVariable("VECTOR_MC_VERIFY_SECTION") is { Length: > 0 } section)
            VerifyImportedMinecraftSection(section);
        Console.WriteLine("minecraft_block_lighting=sky,around_corners,occlusion,multiple_sources,incremental_full,quality,stage_stamps,raster,save_open");

        static BitmapObjectData ReadFace(SceneDefinition scene, SceneBlockGrid.Cell cell) =>
            TryReadFace(scene, cell, out var data) ? data : throw new InvalidOperationException("Missing projected block.");

        static bool TryReadFace(SceneDefinition scene, SceneBlockGrid.Cell cell, out BitmapObjectData data)
        {
            var sceneGrid = scene.BlockGrid ?? throw new InvalidOperationException("Missing block grid after load.");
            for (var i = 0; i < scene.DrawingScene.ObjectCount; i++)
                if (scene.DrawingScene.TryGetBitmapObjectData(i, out var bitmap) && bitmap.BlockGridKey == sceneGrid.Encode(cell))
                { data = bitmap; return true; }
            data = null!;
            return false;
        }
    }

    private static void VerifyChunks(string directory)
    {
        var path = Path.Combine(directory, "chunks.json");
        var roof = from x in Enumerable.Range(8, 16) from z in Enumerable.Range(62, 2)
            select new { x, y = 31, z, id = "test:stone" };
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new
        {
            bounds = new { minX = 0, minY = 0, minZ = 0, maxX = 31, maxY = 31, maxZ = 63 },
            front = "maxZ",
            blocks = roof.Append(new { x = 16, y = 0, z = 62, id = "test:stone" })
                .Append(new { x = 20, y = 31, z = 0, id = "test:stone" })
                .Append(new { x = 20, y = 5, z = 0, id = "test:stone" })
        }));
        var project = new VectorProject();
        var plan = MinecraftSectionImport.Prepare(path, directory, "", 16, true, default, editable: true, nightVision: true);
        var (scene, _) = MinecraftSectionImport.CreateScene(project, plan);
        var grid = scene.BlockGrid ?? throw new InvalidOperationException("Missing chunk grid.");
        var chunks = SceneBlockChunk.Enumerate(grid).ToArray();
        Require(chunks.Length == 16 && chunks.Take(4).SequenceEqual(new SceneBlockChunk[] { new(0, 0), new(1, 0), new(0, 1), new(1, 1) })
            && SceneBlockChunk.FromCell(15, 31) == chunks[2] && SceneBlockChunk.FromCell(16, 31) == chunks[3]
            && chunks.All(chunk => chunk.Bounds(grid).Width == 16 * grid.CellSize),
            "A 32 by 32 by 64 terrain must have four independently addressable chunks per slab.");
        Require(scene.Layers.Count == 4 && grid.Layers[15] != grid.Layers[16] && grid.Layers[0] == grid.Layers[15],
            "A 64-block deep section must create exactly four actual scene layers, including air.");
        var rear = new SceneBlockGrid.Cell(20, 0, 63);
        Require(!plan.Tiles.Any(tile => tile.Cell == rear) && plan.ChunkTiles?.Any(tile => tile.Cell == rear) == true
            && Warmth(scene, grid, rear) == 0, "The rear slab must keep its projected face behind the front slab.");
        VerifyMinecraftFrontPaintOrder(scene, plan);
        var first = new SceneBlockChunkTimeline(scene, grid, chunks[2]);
        var second = new SceneBlockChunkTimeline(scene, grid, chunks[^1]);
        using var timeline = new TimelineStrip(first);
        second.SynchronizeTimelineTracks();
        Require(timeline.VisibleTrackRowCount == 16 && first.Timeline.Tracks.Select(track => first.LayerName(track.TargetId))
            .SequenceEqual(Enumerable.Range(0, 16).Reverse().Select(SceneBlockChunk.DepthLabel))
            && second.Timeline.Tracks.All(track => second.TryGetDepth(track.TargetId, out var depth) && depth is >= 48 and <= 63)
            && !ReferenceEquals(first.Timeline, second.Timeline),
            "Chunk timelines must present their own depth tracks.");
        var torch = Enumerable.Range(0, grid.Palette.Count).Single(index => grid.Palette[index].Name == MinecraftLightMaterials.Torch);
        var source = new SceneBlockGrid.Cell(15, 31, 0);
        var sample = new SceneBlockGrid.Cell(16, 31, 1);
        var sampleAir = sample with { Depth = 0 };
        Require(grid.Lighting().Shade(sample).Warmth == 0 && MinecraftBlockEditing.Apply(project, scene, source, torch)
            && grid.Lighting().Block(sampleAir) == 13 && Warmth(scene, grid, sample) != 0,
            "The torch should light the adjacent chunk and update the projected block.");
        var manifest = ProjectVaultStore.Save(project, Path.Combine(directory, "Chunks", "Chunks.v2dProject"));
        var restored = ProjectVaultStore.Load(manifest).Scenes.Single(item => item.Id == scene.Id);
        Require(restored.BlockGrid is { NightVision: true } saved && SceneBlockChunk.Enumerate(saved).SequenceEqual(chunks)
            && saved.Get(source) == torch && Warmth(restored, saved, sample) != 0,
            "Chunk identity and lighting must survive reopening.");
        VerifyChunkWorkbench(project, scene, grid, chunks.Take(4).ToArray());
        Console.WriteLine("minecraft_chunks=four_slabs,sixteen_depth_tracks,cross_chunk_lighting,save_open,workbench");

        static uint Warmth(SceneDefinition definition, SceneBlockGrid blocks, SceneBlockGrid.Cell cell)
        {
            for (var i = 0; i < definition.DrawingScene.ObjectCount; i++)
                if (definition.DrawingScene.TryGetBitmapObjectData(i, out var bitmap) && bitmap.BlockGridKey == blocks.Encode(cell))
                    return bitmap.WarmLightCorners;
            throw new InvalidOperationException("Missing chunk block.");
        }
    }

    private static void VerifyChunkWorkbench(VectorProject project, SceneDefinition scene, SceneBlockGrid grid,
        IReadOnlyList<SceneBlockChunk> chunks)
    {
        using var form = new MainForm
        {
            ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000)
        };
        form.Show();
        Application.DoEvents();
        ((System.Windows.Forms.Timer)RequireField(typeof(MainForm), "_timer").GetValue(form)!).Stop();
        RequireMethod(typeof(MainForm), "LoadProjectDocument", [typeof(VectorProject), typeof(string)])
            .Invoke(form, [project, ""]);
        var tabs = (WorkspaceTabs)RequireField(typeof(MainForm), "_workspaceTabs").GetValue(form)!;
        tabs.SelectedView = WorkspaceView.SceneEditor;
        RequireMethod(typeof(MainForm), "SelectScenePreservingHistory", [typeof(int)])
            .Invoke(form, [project.Scenes.ToList().IndexOf(scene)]);
        var stage = (StageControl)RequireField(typeof(MainForm), "_stage").GetValue(form)!;
        stage.SetReferenceViewDirection(ReferenceViewDirection.Front, ReferenceCameraMotion.Immediate);
        stage.Fit();
        RequireMethod(typeof(MainForm), "ActivateTool", [typeof(ToolMode)]).Invoke(form, [ToolMode.Select]);
        var down = RequireMethod(typeof(MainForm), "StageMouseDown", [typeof(object), typeof(MouseEventArgs)]);
        var doubleClick = RequireMethod(typeof(MainForm), "StageMouseDoubleClick", [typeof(object), typeof(MouseEventArgs)]);
        var timeline = (TimelineStrip)RequireField(typeof(MainForm), "_timeline").GetValue(form)!;
        Require(timeline.SelectSingleLayerTarget(grid.Layers[0]), "Could not select the front chunk layer.");
        var front = grid.Center(new(12, 0, 0));
        var rear = grid.Center(new(20, 26, 63));
        Require(stage.GetBlockLayerHighlightRuns().Any(run => run.Contains(front))
            && !stage.GetBlockLayerHighlightRuns().Any(run => run.Contains(rear)),
            "Selecting the outer front slab must highlight only its placed cells.");
        VerifyOuterMinecraftLayerVisibility(timeline, stage, scene, grid);
        var outlines = new HashSet<PointF>();
        var corners = new PointF[4];
        var click = Point.Empty;
        foreach (var chunk in chunks)
        {
            var bounds = chunk.Bounds(grid);
            var x = bounds.X + bounds.Width * .5f;
            var y = bounds.Y + bounds.Height * .5f;
            click = Point.Round(stage.WorldToScreen(x, y));
            if (stage.UsesReferenceProjection
                && stage.TryProjectScenePosition(new System.Numerics.Vector3(x, y, 0), out var projected, out _))
                click = Point.Round(projected);
            down.Invoke(form, [stage, new MouseEventArgs(MouseButtons.Left, 1, click.X, click.Y, 0)]);
            Require(stage.TryGetBlockChunkOutline(corners) && !stage.BlockChunkEditing && outlines.Add(corners[0]),
                "Stage clicks should select four distinct chunk outlines.");
        }
        Require(timeline.SelectSingleLayerTarget(grid.Layers[48]), "Could not select the rear chunk layer.");
        Require(stage.GetBlockLayerHighlightRuns().Any(run => run.Contains(rear))
            && !stage.GetBlockLayerHighlightRuns().Any(run => run.Contains(front)),
            "Selecting another outer slab must replace the stage highlight.");
        doubleClick.Invoke(form, [stage, new MouseEventArgs(MouseButtons.Left, 2, click.X, click.Y, 0)]);
        Require(timeline.Context is SceneBlockChunkTimeline editor
            && editor.Chunk == (chunks[^1] with { Z = 3 })
            && timeline.VisibleTrackRowCount == 16 && stage.BlockChunkIsolationActive,
            "Stage double-click must enter the chunk and show only its depth tracks.");
        var back = (Button)RequireField(typeof(MainForm), "_symbolEditingReturnButton").GetValue(form)!;
        Require(back.Visible && back.Text == UiLocalization.T("Back to scene"),
            "The chunk editor must expose the ordinary symbol breadcrumb return button.");
        Require(stage.TryGetBlockChunkEditingMask(corners), "The active chunk must create an editing mask.");
        Require(timeline.SelectSingleLayerTarget(SceneBlockChunkTimeline.TargetId(63))
            && stage.GetBlockLayerHighlightRuns().Count == 1 && stage.GetBlockLayerHighlightRuns()[0].Contains(rear),
            "Chunk editing must highlight only the selected depth inside the opened chunk.");
        var inside = Point.Round(new PointF((corners[0].X + corners[2].X) / 2,
            (corners[0].Y + corners[2].Y) / 2));
        using (var pixels = new Bitmap(stage.Width, stage.Height))
        {
            using var graphics = Graphics.FromImage(pixels);
            graphics.Clear(Color.Black);
            RequireMethod(typeof(StageControl), "DrawBlockChunkEditingMask", [typeof(Graphics)])
                .Invoke(stage, [graphics]);
            Require(stage.ClientRectangle.Contains(inside) && pixels.GetPixel(inside.X, inside.Y).R == 0
                && pixels.GetPixel(1, 1).R == StageControl.BlockChunkEditingVeil.A,
                "The editing veil must fade the outer stage without changing pixels inside the chunk.");
            graphics.Clear(Color.Black);
            var rearRun = stage.GetBlockLayerHighlightRuns()[0];
            Require(stage.TryProjectBlockBounds(rearRun, corners), "Could not project the selected cell.");
            var sample = Point.Round(new PointF((corners[0].X + corners[2].X) / 2, (corners[0].Y + corners[2].Y) / 2));
            RequireMethod(typeof(StageControl), "DrawBlockLayerHighlight", [typeof(Graphics)]).Invoke(stage, [graphics]);
            Require(stage.ClientRectangle.Contains(sample) && pixels.GetPixel(sample.X, sample.Y).ToArgb() != Color.Black.ToArgb()
                && pixels.GetPixel(1, 1).ToArgb() == Color.Black.ToArgb(), "The GDI overlay did not tint the selected cell.");
            VerifyMinecraftLayerHighlightDirect2D(stage, sample);
        }
        back.PerformClick();
        Require(ReferenceEquals(timeline.Context, scene) && !stage.TryGetBlockChunkEditingMask(corners) && !back.Visible
            && stage.GetBlockLayerHighlightRuns().Any(run => run.Contains(rear)),
            "Leaving the chunk must restore the outer scene timeline.");
    }

    private static void VerifyOuterMinecraftLayerVisibility(TimelineStrip timeline, StageControl stage, SceneDefinition scene,
        SceneBlockGrid grid)
    {
        var frontId = grid.Layers[0];
        var rearId = grid.Layers[48];
        var trackIndex = Array.FindIndex(scene.Timeline.Tracks.ToArray(), track => track.TargetId == frontId);
        var layout = RequireMethod(typeof(TimelineStrip), "CreateLayout").Invoke(timeline, null)!;
        var rowTop = (int)layout.GetType().GetProperty("RowTop")!.GetValue(layout)!;
        var position = (int)RequireMethod(typeof(TimelineStrip), "VisibleTrackPosition", [typeof(int)])
            .Invoke(timeline, [trackIndex])!;
        var scroll = (int)RequireField(typeof(TimelineStrip), "_firstVisibleTrack").GetValue(timeline)!;
        var click = new MouseEventArgs(MouseButtons.Left, 1, 8,
            rowTop + (position - scroll) * timeline.FrameHeight + timeline.FrameHeight / 2, 0);
        var onMouseDown = RequireMethod(typeof(TimelineStrip), "OnMouseDown", [typeof(MouseEventArgs)]);

        Require(ReferenceEquals(stage.Scene, scene.DrawingScene) && StageHasLayer(frontId) && StageHasLayer(rearId),
            "Warm the direct Stage's same-frame object cache with both MC slabs visible.");
        onMouseDown.Invoke(timeline, [click]);
        Require(!scene.IsLayerVisible(frontId) && !StageHasLayer(frontId) && StageHasLayer(rearId)
            && stage.GetBlockLayerHighlightRuns().Count == 0,
            "Hiding the MC slab must remove only that slab and its highlight at the current frame.");
        onMouseDown.Invoke(timeline, [click]);
        Require(scene.IsLayerVisible(frontId) && StageHasLayer(frontId) && StageHasLayer(rearId)
            && stage.GetBlockLayerHighlightRuns().Count > 0,
            "Showing the MC slab must restore its objects and highlight without changing frames.");
        Console.WriteLine("minecraft_outer_layer_visibility=hide,show,same_frame,highlight");

        bool StageHasLayer(string layerId)
        {
            var drawing = stage.Scene;
            var layer = Array.IndexOf(drawing.LayerIds, layerId);
            foreach (var index in drawing.GetActiveObjectIndices(stage.Frame))
                if (drawing.ObjectLayer[index] == layer) return true;
            return false;
        }
    }

    private static void VerifyMinecraftLayerHighlightDirect2D(StageControl stage, Point sample)
    {
        using var bitmap = new Bitmap(stage.Width, stage.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var dc = graphics.GetHdc();
            try
            {
                using var renderer = new Direct2DStageRenderer();
                var factory = D2D1.D2D1CreateFactory<ID2D1Factory1>(FactoryType.SingleThreaded, DebugLevel.None);
                RequireField(typeof(Direct2DStageRenderer), "_factory").SetValue(renderer, factory);
                var target = factory.CreateDCRenderTarget(new RenderTargetProperties(RenderTargetType.Default,
                    new(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                    96, 96, RenderTargetUsage.None, FeatureLevel.Default));
                RequireField(typeof(Direct2DStageRenderer), "_target").SetValue(renderer, target);
                target.BindDC(dc, new Vortice.RawRect(0, 0, stage.Width, stage.Height));
                target.BeginDraw();
                var black = new Vortice.Mathematics.Color(0f, 0f, 0f, 1f);
                target.Clear(in black);
                RequireMethod(typeof(Direct2DStageRenderer), "DrawBlockLayerHighlight", [typeof(StageControl)])
                    .Invoke(renderer, [stage]);
                target.EndDraw().CheckError();
            }
            finally { graphics.ReleaseHdc(dc); }
        }
        Require(bitmap.GetPixel(sample.X, sample.Y).ToArgb() != Color.Black.ToArgb()
            && bitmap.GetPixel(1, 1).ToArgb() == Color.Black.ToArgb(),
            "Direct2D must tint the selected cell and leave unrelated pixels unchanged.");
        Console.WriteLine("minecraft_layer_highlight_pixels=gdi,direct2d_dc");
    }

    private static void VerifyNightVision()
    {
        var normal = new SceneBlockLightVolume(5, 5, 3, SceneBlockLightQuality.HighDetailed);
        var night = new SceneBlockLightVolume(5, 5, 3, SceneBlockLightQuality.HighDetailed, nightVision: true);
        var face = new SceneBlockGrid.Cell(2, 4, 2);
        var light = face with { Depth = 1 };
        foreach (var volume in new[] { normal, night })
        {
            for (var x = 0; x < 5; x++)
                for (var z = 0; z < 3; z++) volume.Set(new(x, 0, z), true, 0, 0, 0);
            volume.Set(face, true, 0, 0, 0);
            volume.Build();
        }
        Require(night.Shade(face).Shadows < normal.Shade(face).Shadows, "Night vision did not brighten a dark face.");
        foreach (var volume in new[] { normal, night }) volume.Relight(light, false, 0, 0, 13);
        Require(night.Sky(light) == normal.Sky(light) && night.Block(light) == normal.Block(light)
            && night.Shade(face).Warmth != 0 && night.Shade(face).Warmth == normal.Shade(face).Warmth,
            "Night vision must not change actual skylight or emissive propagation.");
    }

    private static void VerifyMinecraftFrontPaintOrder(SceneDefinition scene, MinecraftSectionImport.Plan plan)
    {
        var grid = scene.BlockGrid ?? throw new InvalidOperationException("Missing block grid.");
        var drawing = scene.DrawingScene;
        var order = new SceneRenderOrderBuffer();
        var width = grid.Columns * grid.CellSize;
        var height = grid.Rows * grid.CellSize;
        order.Collect(drawing, new(-width / 2, -height / 2, width, height), 0);
        var cells = new List<SceneBlockGrid.Cell>();
        order.Draw(drawing, int.MaxValue, index =>
        {
            if (drawing.TryGetBitmapObjectData(index, out var bitmap) && bitmap.BlockGridKey is >= 0)
            {
                var cell = grid.Decode(bitmap.BlockGridKey.Value);
                Require(drawing.LayerIds[drawing.ObjectLayer[index]] == grid.Layers[cell.Depth],
                    "A projected block was added to a different layer than its grid depth.");
                cells.Add(cell);
            }
        }, _ => { });
        var nearest = cells.GroupBy(cell => (cell.X, cell.Y)).ToDictionary(group => group.Key, group => group.Last().Depth);
        var expected = plan.Tiles.GroupBy(tile => (tile.X, tile.Y))
            .ToDictionary(group => group.Key, group => group.Min(tile => tile.Depth));
        var mismatch = expected.Where(pair => !nearest.TryGetValue(pair.Key, out var hit) || hit != pair.Value).Take(1).ToArray();
        Require(nearest.Count == expected.Count && mismatch.Length == 0,
            $"Grouped front painting: expected {expected.Count} cells, drawn {nearest.Count}; first mismatch: {string.Join(", ", mismatch.Select(pair => $"{pair.Key}: expected {pair.Value}, drew {nearest.GetValueOrDefault(pair.Key, -1)}"))}.");
    }

    private static void VerifyImportedMinecraftSection(string path)
    {
        var textures = MinecraftSectionImport.DefaultTextureDirectory(AppContext.BaseDirectory);
        const SceneBlockLightQuality quality = SceneBlockLightQuality.HighDetailed;
        var flat = MinecraftSectionImport.Prepare(path, textures, "", 16, true, default, quality: quality);
        var plan = MinecraftSectionImport.Prepare(path, textures, "", 16, true, default, editable: true,
            quality: quality, nightVision: true);
        Require(plan.MissingCount == 0, "The reported section is missing textures: " + string.Join(", ", plan.Missing));
        Require(flat.Tiles.Select(tile => tile.Cell).SequenceEqual(plan.Tiles.Select(tile => tile.Cell)),
            "Grouping must preserve the nearest visible cells from the original import.");
        var brighter = plan.Tiles.Zip(flat.Tiles).Count(pair => pair.First.ShadowCorners < pair.Second.ShadowCorners);
        Require(brighter > 0, "Night vision did not brighten the reported section.");
        var project = new VectorProject();
        var (scene, _) = MinecraftSectionImport.CreateScene(project, plan);
        VerifyMinecraftFrontPaintOrder(scene, plan);
        Console.WriteLine($"minecraft_reported_section={plan.Name},blocks:{plan.SourceCount},visible:{plan.Tiles.Length},night_vision_brighter:{brighter},outer_layers:{scene.Layers.Count},front_order:ok");
    }

    private static void VerifyBiomeTintImport()
    {
        var original = new BitmapImageRaster(new BitmapImageRasterKey("biome-tint-regression", 16, 16));
        for (var i = 0; i < original.Pixels.Length; i += 4)
        {
            original.Pixels[i] = 20;
            original.Pixels[i + 1] = 90;
            original.Pixels[i + 2] = 54;
            original.Pixels[i + 3] = 128;
        }
        var tintData = new BitmapObjectData { BiomeTintArgb = unchecked((int)0xff91bd59), BiomeTintGrassSide = true };
        var tintedKey = BitmapImageRasterizer.ObjectAppearanceKey(original.Key, tintData);
        var tinted = BitmapImageRasterizer.ApplyObjectAppearance(original, tintedKey);
        var otherKey = tintedKey with { BiomeTintArgb = unchecked((int)0xff6a7039) };
        var other = BitmapImageRasterizer.ApplyObjectAppearance(original, otherKey);
        const int dirt = 16 * 4 * 6;
        Require(tinted.Pixels[3] == 128 && tinted.Pixels[1] > tinted.Pixels[2]
            && !tinted.Pixels.AsSpan(0, 4).SequenceEqual(other.Pixels.AsSpan(0, 4))
            && tinted.Pixels.AsSpan(dirt, 4).SequenceEqual(original.Pixels.AsSpan(dirt, 4))
            && tinted.Key != other.Key && ReferenceEquals(tinted, BitmapImageRasterizer.ApplyObjectAppearance(original, tintedKey)),
            "Biome tint did not preserve alpha/dirt or isolate the two color variants.");

        var temporary = Directory.CreateTempSubdirectory("v2d-mc-biome-");
        try
        {
            var source = Path.Combine(temporary.FullName, "section.json");
            File.WriteAllText(source, System.Text.Json.JsonSerializer.Serialize(new
            {
                bounds = new { minX = 0, minY = 0, minZ = 0, maxX = 6, maxY = 0, maxZ = 0 },
                front = "maxZ",
                blocks = new object[]
                {
                    new { x = 0, y = 0, z = 0, id = "minecraft:grass_block", biome = "minecraft:plains" },
                    new { x = 1, y = 0, z = 0, id = "minecraft:grass_block", biome = "swamp" },
                    new { x = 2, y = 0, z = 0, id = "minecraft:grass_block" },
                    new { x = 3, y = 0, z = 0, id = "minecraft:oak_leaves", biome = "minecraft:jungle" },
                    new { x = 4, y = 0, z = 0, id = "minecraft:grass_block_top", biome = "plains" },
                    new { x = 5, y = 0, z = 0, id = "minecraft:grass_block", biome = "plains", props = new { snowy = "true" } },
                    new { x = 6, y = 0, z = 0, id = "minecraft:cherry_leaves", biome = "jungle" }
                }
            }));
            var names = new[] { "grass_block_side", "grass_block_top", "grass_block_snow", "oak_leaves", "cherry_leaves" };
            for (var i = 0; i < names.Length; i++)
            {
                using var image = new Bitmap(16, 16);
                image.SetPixel(0, 0, Color.FromArgb(i, 0, 0));
                image.Save(Path.Combine(temporary.FullName, names[i] + ".png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            var plan = MinecraftSectionImport.Prepare(source, temporary.FullName, "", 16, false, default, editable: true);
            var tiles = plan.Tiles.OrderBy(tile => tile.X).ToArray();
            const int plainsGrass = unchecked((int)0xff91bd59), swampGrass = unchecked((int)0xff6a7039),
                jungleFoliage = unchecked((int)0xff30bb0b);
            Require(plan.MissingCount == 0 && plan.Textures.Count == 5
                && Path.GetFileName(plan.Textures[tiles[0].TextureKey].Path) == "grass_block_side.png"
                && Path.GetFileName(plan.Textures[tiles[5].TextureKey].Path) == "grass_block_snow.png"
                && tiles[0] is { BiomeTintArgb: plainsGrass, BiomeTintGrassSide: true }
                && tiles[1] is { BiomeTintArgb: swampGrass, BiomeTintGrassSide: true }
                && tiles[2] is { BiomeTintArgb: null, BiomeTintGrassSide: false }
                && tiles[3] is { BiomeTintArgb: jungleFoliage, BiomeTintGrassSide: false }
                && tiles[4] is { BiomeTintArgb: plainsGrass, BiomeTintGrassSide: false }
                && tiles[5] is { TextureSuffix: "_snow", BiomeTintArgb: null }
                && tiles[6].BiomeTintArgb is null,
                "Biome tint did not distinguish grass sides, tops, snowy grass, foliage and untinted faces.");

            var project = new VectorProject();
            var (scene, assets) = MinecraftSectionImport.CreateScene(project, plan);
            var grid = scene.BlockGrid ?? throw new InvalidOperationException("Missing biome test block grid.");
            var plains = grid.Get(tiles[0].Cell);
            var swamp = grid.Get(tiles[1].Cell);
            var grassBitmap = ReadFace(scene, grid, tiles[0].Cell);
            Require(assets.Length == plan.Textures.Count && plains != swamp
                && grid.Palette[plains].AssetId == grid.Palette[swamp].AssetId
                && grid.Palette[plains].BiomeTintArgb == grassBitmap.BiomeTintArgb
                && grassBitmap.BiomeTintGrassSide,
                "Grass biomes did not share their original asset or preserve tint in the grid and bitmap.");

            Require(MinecraftBlockEditing.Apply(project, scene, tiles[0].Cell, swamp),
                "Could not substitute a same-texture material with a different biome tint.");
            var changed = ReadFace(scene, grid, tiles[0].Cell);
            Require(changed.ImageAssetId == grassBitmap.ImageAssetId && changed.BiomeTintArgb == tiles[1].BiomeTintArgb
                && changed.BiomeTintGrassSide, "A tint-only block substitution did not update its bitmap.");

            var manifest = ProjectVaultStore.Save(project, Path.Combine(temporary.FullName, "Fixture", "Fixture.v2dProject"));
            var restored = ProjectVaultStore.Load(manifest).Scenes.Single(item => item.Id == scene.Id);
            var restoredGrid = restored.BlockGrid ?? throw new InvalidOperationException("Missing reopened biome test block grid.");
            var restoredBitmap = ReadFace(restored, restoredGrid, tiles[0].Cell);
            var restoredMaterial = restoredGrid.Palette[restoredGrid.Get(tiles[0].Cell)];
            Require(restoredMaterial.BiomeTintArgb == changed.BiomeTintArgb && restoredMaterial.BiomeTintGrassSide
                && restoredBitmap.BiomeTintArgb == changed.BiomeTintArgb && restoredBitmap.BiomeTintGrassSide,
                "Reopening did not preserve the side tint in both the grid and bitmap.");
        }
        finally { DeleteTemporaryDirectory(temporary.FullName); }

        static BitmapObjectData ReadFace(SceneDefinition scene, SceneBlockGrid grid, SceneBlockGrid.Cell cell)
        {
            for (var i = 0; i < scene.DrawingScene.ObjectCount; i++)
                if (scene.DrawingScene.TryGetBitmapObjectData(i, out var bitmap) && bitmap.BlockGridKey == grid.Encode(cell))
                    return bitmap;
            throw new InvalidOperationException("Missing biome test bitmap.");
        }
    }

    private static void VerifyLightingQuality()
    {
        var low = Build(SceneBlockLightQuality.Low);
        var medium = Build(SceneBlockLightQuality.Medium);
        var high = Build(SceneBlockLightQuality.High);
        var detailed = Build(SceneBlockLightQuality.HighDetailed);
        var buried = new SceneBlockGrid.Cell(3, 5, 0);
        var edge = new SceneBlockGrid.Cell(3, 2, 0);
        Require((low.Shade(buried).Shadows & 255) == 219
            && low.Shade(buried).Shadows > medium.Shade(buried).Shadows
            && medium.Shade(buried).Shadows > high.Shade(buried).Shadows,
            "Lighting grades did not preserve low or progressively reveal the shaded cut texture.");
        Require(high.Shade(edge).Shadows < high.Shade(buried).Shadows,
            "The cut-face fill removed the terrain edge gradient.");
        var leftFace = high.Shade(edge).Shadows;
        var rightFace = high.Shade(edge with { X = edge.X + 1 }).Shadows;
        Require((leftFace >> 8 & 255) == (rightFace & 255) && (leftFace >> 24) == (rightFace >> 16 & 255),
            "Adjacent cut faces showed a discontinuity at their shared edge.");
        Require(high.Sky(buried with { Depth = 1 }) == 0 && high.Block(buried with { Depth = 1 }) == 0,
            "Display fill propagated into the unlit cave.");
        Require(detailed.Shade(buried) == high.Shade(buried), "Detailed shading changed the cut-face fill.");

        var cave = new SceneBlockLightVolume(8, 8, 4, SceneBlockLightQuality.HighDetailed);
        for (var z = 0; z < 4; z++)
            for (var x = 0; x < 8; x++)
                cave.Set(new(x, 0, z), true, 0, 0, 0);
        var recess = new SceneBlockGrid.Cell(4, 5, 3);
        var ledge = new SceneBlockGrid.Cell(2, 3, 1);
        cave.Set(recess, true, 0, 0, 0);
        cave.Build();
        var clear = cave.Shade(recess);
        cave.Relight(ledge, true, 0, 0, 0);
        var shadow = cave.Shade(recess);
        Require(cave.Sky(recess with { Depth = 2 }) == 0 && (shadow.Shadows & 255) > (clear.Shadows & 255)
            && shadow.Shadows >> 8 == clear.Shadows >> 8,
            "A nearby ledge should shade only the corresponding corner without introducing light.");
        cave.Relight(ledge, false, 0, 0, 0);
        Require(cave.Shade(recess) == clear, "Removing the ledge left stale detail shading.");

        static SceneBlockLightVolume Build(SceneBlockLightQuality quality)
        {
            var volume = new SceneBlockLightVolume(8, 8, 2, quality);
            for (var x = 0; x < 8; x++)
            {
                volume.Set(new(x, 2, 1), true, 0, 0, 0);
                for (var y = 2; y < 8; y++) volume.Set(new(x, y, 0), true, 0, 0, 0);
            }
            volume.Build();
            return volume;
        }
    }

    private static void VerifyPropagation()
    {
        const int width = 51, height = 20, depth = 35;
        var volume = new SceneBlockLightVolume(width, height, depth);
        var states = new Dictionary<SceneBlockGrid.Cell, (bool Opaque, int Filter, int Emission)>();
        for (var z = 0; z < depth; z++)
            for (var x = 0; x < width; x++) Seed(new(x, 0, z));
        for (var y = 1; y < height; y++)
            for (var z = 0; z < depth; z++) Seed(new(24, y, z));
        volume.Build();
        var left = new SceneBlockGrid.Cell(22, 3, 15);
        var right = new SceneBlockGrid.Cell(30, 3, 15);
        var door = new SceneBlockGrid.Cell(24, 3, 17);
        var sample = new SceneBlockGrid.Cell(25, 3, 15);
        Edit(left, emission: 14);
        Require(volume.Block(sample) == 0, "Block light leaked through a sealed solid wall.");
        Edit(door);
        Require(volume.Block(sample) == 7, "Block light did not follow the seven-cell route around the corner.");
        Edit(right, emission: 15);
        Require(volume.Block(sample) == 10, "Overlapping sources should keep the stronger light.");
        Edit(right);
        Require(volume.Block(sample) == 7, "Source removal discarded the remaining lamp.");
        Edit(door, opaque: true);
        Require(volume.Block(sample) == 0, "Closing the opening left stale block light.");
        Edit(new(18, 0, 15));
        Require(volume.Sky(new(18, height - 1, 15)) == 15 && volume.Sky(new(17, height - 1, 15)) == 14,
            "Unobstructed sky should descend without loss and spread sideways with loss.");
        Edit(new(18, 0, 15), filter: 2);
        Require(volume.Sky(new(18, 0, 15)) == 13 && volume.Sky(new(18, 2, 15)) == 11, "Light filter lost its attenuation.");
        Edit(new(18, 0, 15), opaque: true);
        Require(volume.Sky(new(18, height - 1, 15)) == 0, "Closing the roof left stale sky at the bottom.");

        void Seed(SceneBlockGrid.Cell cell)
        {
            states.Add(cell, (true, 0, 0));
            volume.Set(cell, true, 0, 0, 0);
        }

        void Edit(SceneBlockGrid.Cell cell, bool opaque = false, int filter = 0, int emission = 0)
        {
            states[cell] = (opaque, filter, emission);
            volume.Relight(cell, opaque, filter, emission, 0);
            var full = new SceneBlockLightVolume(width, height, depth);
            foreach (var (position, state) in states) full.Set(position, state.Opaque, state.Filter, state.Emission, 0);
            full.Build();
            for (var z = 0; z < depth; z++)
                for (var y = 0; y < height; y++)
                    for (var x = 0; x < width; x++)
                    {
                        var sample = new SceneBlockGrid.Cell(x, y, z);
                        Require(volume.Sky(sample) == full.Sky(sample) && volume.Block(sample) == full.Block(sample),
                            "Incremental light must match a full rebuild.");
                    }
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
