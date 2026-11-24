namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunBitmapLayerCommandsRegression()
    {
        var root = CreateTemporaryDirectory("bitmap-layer-commands");
        var previous = Environment.GetEnvironmentVariable("VECTOR_DISABLE_BITMAP_LAYER_COMMANDS");
        var previousChunks = Environment.GetEnvironmentVariable("VECTOR_DISABLE_BLOCK_TILE_CACHE");
        try
        {
            Environment.SetEnvironmentVariable("VECTOR_DISABLE_BLOCK_TILE_CACHE", "1");
            var path = Path.Combine(root, "tile.png");
            using (var image = new Bitmap(16, 16))
            {
                using var graphics = Graphics.FromImage(image);
                graphics.Clear(Color.Coral);
                graphics.FillRectangle(Brushes.DarkGreen, 0, 0, 8, 8);
                image.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            var scene = new VectorScene();
            scene.CreateEmpty();
            for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++)
                scene.AddBitmapObject(0, new PointF((x - 3.5f) * 400, (y - 3.5f) * 400),
                    new BitmapObjectData
                    {
                        ImageAssetId = "tile", PlacedSize = new SizeF(400, 400),
                        ShadowCorners = (x & 1) == 0 ? 0u : 0x30406080u,
                        WarmLightCorners = (y & 1) == 0 ? 0u : 0x08102030u
                    });
            using var host = new Form { ClientSize = new Size(640, 480), ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual, Location = new Point(-3000, -3000) };
            using var stage = new StageControl(scene)
            {
                Dock = DockStyle.Fill,
                BitmapImageResolver = _ => BitmapImageRasterizer.Decode(path,
                    BitmapImageImportSettings.Default with { FilterMode = ImageFilterMode.Point }),
                BitmapImageKeyResolver = _ => BitmapImageRasterizer.TryGetSourceKey(new FileInfo(path),
                    BitmapImageImportSettings.Default with { FilterMode = ImageFilterMode.Point }, out var key) ? key : null,
                BitmapImageSamplingProvider = _ => BitmapSampling.Point
            };
            host.Controls.Add(stage);
            host.Show();
            Application.DoEvents();
            stage.Fit();
            var renderer = (Direct2DStageRenderer)RequireField(typeof(StageControl), "_direct2DRenderer").GetValue(stage)!;
            var clearBaseFrame = RequireMethod(typeof(Direct2DStageRenderer), "ClearBaseFrameCache");
            foreach (var zoom in new[] { 0.75f, 1f, 1.3f, 2f })
            {
                stage.RestoreViewState(stage.CaptureViewState() with { Zoom = zoom, CameraX = 37, CameraY = -19 });
                Environment.SetEnvironmentVariable("VECTOR_DISABLE_BITMAP_LAYER_COMMANDS", "1");
                clearBaseFrame.Invoke(renderer, null);
                stage.Invalidate(); PaintStage(stage); PaintStage(stage);
                var reference = CaptureStageGpuPixels(stage);
                Environment.SetEnvironmentVariable("VECTOR_DISABLE_BITMAP_LAYER_COMMANDS", null);
                clearBaseFrame.Invoke(renderer, null);
                stage.Invalidate(); PaintStage(stage); PaintStage(stage);
                var actual = CaptureStageGpuPixels(stage);
                AssertTimeline(stage.LastFrameUsedDirect2D, "Bitmap layer comparison did not use Direct2D.");
                for (var offset = 0; offset < actual.Length; offset += 4)
                    if (!actual.AsSpan(offset, 4).SequenceEqual(reference.AsSpan(offset, 4)))
                        throw new InvalidOperationException(
                            $"Bitmap layer cache changed pixel ({offset / 4 % stage.Width}, {offset / 4 / stage.Width}) at zoom {zoom}.");
            }
            AssertTimeline(renderer.BitmapLayerCommandBuilds >= 4,
                "Bitmap layer commands were not rebuilt at the settled zoom levels.");

            // Activate the large-scene preview and place one tile at the viewport edge.
            scene.AddBitmapObject(0, new PointF(4100, 0),
                new BitmapObjectData { ImageAssetId = "tile", PlacedSize = new SizeF(400, 400) });
            for (var index = 0; index < 320; index++)
                scene.AddBitmapObject(0, new PointF(50000 + index * 400, 50000),
                    new BitmapObjectData { ImageAssetId = "tile", PlacedSize = new SizeF(400, 400) });
            PaintStage(stage);
            var builds = renderer.BitmapLayerCommandBuilds;
            var reuses = renderer.BitmapLayerCommandReuses;
            var visible = stage.LastStats.VisibleObjects;
            stage.ZoomAt(new Point(320, 240), 1.06f, interactivePreview: true);
            PaintStage(stage);
            AssertTimeline(stage.LastStats.VisibleObjects == visible - 1,
                "The edge tile did not leave the viewport during zoom.");
            AssertTimeline(renderer.BitmapLayerCommandBuilds == builds && renderer.BitmapLayerCommandReuses > reuses,
                "Bitmap layer commands were not reused during the zoom preview.");
            RequireMethod(typeof(StageControl), "EndZoomLodPreview").Invoke(stage, [true]);
            PaintStage(stage);
            AssertTimeline(renderer.BitmapLayerCommandBuilds > builds,
                "Bitmap layer commands were not refreshed when the zoom preview settled.");
            renderer.ReloadRuntimeResources();
            AssertTimeline(((System.Collections.IDictionary)RequireField(typeof(Direct2DStageRenderer),
                "_bitmapLayerCommands").GetValue(renderer)!).Count == 0, "Device reset retained bitmap layer commands.");
            Console.WriteLine("bitmap_layer_commands=point_pixels,zoom_pan,preview_reuse,settled_rebuild,device_reset");
            VerifyBlockTileSeams();
            VerifyBlockTileNavigation();

            void VerifyBlockTileSeams()
            {
                var tilePath = Path.Combine(root, "opaque-block.png");
                using (var tile = new Bitmap(16, 16))
                {
                    using (var graphics = Graphics.FromImage(tile)) graphics.Clear(Color.FromArgb(45, 62, 79));
                    tile.Save(tilePath, System.Drawing.Imaging.ImageFormat.Png);
                }
                var blocks = new VectorScene();
                blocks.CreateEmpty();
                for (var y = 0; y < 6; y++)
                    for (var x = 0; x < 6; x++)
                        blocks.AddBitmapObject(0, new PointF((x - 2.5f) * 400, (y - 2.5f) * 400),
                            new BitmapObjectData
                            {
                                ImageAssetId = "block", PlacedSize = new SizeF(400, 400), BlockGridKey = y * 6 + x
                            });
                var settings = BitmapImageImportSettings.Default with { FilterMode = ImageFilterMode.Point };
                stage.BitmapImageResolver = _ => BitmapImageRasterizer.Decode(tilePath, settings);
                stage.BitmapImageKeyResolver = _ => BitmapImageRasterizer.TryGetSourceKey(new FileInfo(tilePath), settings, out var key)
                    ? key : null;
                stage.WorldGridOpacity = 0;
                stage.BindScene(blocks);
                stage.RestoreViewState(stage.CaptureViewState() with { Zoom = 3.4563f, CameraX = 37, CameraY = -19 });
                var a = stage.WorldToScreen(-1190, -1190);
                var b = stage.WorldToScreen(1190, 1190);
                var left = Math.Max(0, (int)MathF.Ceiling(a.X) + 1);
                var top = Math.Max(0, (int)MathF.Ceiling(a.Y) + 1);
                var right = Math.Min(stage.Width, (int)MathF.Floor(b.X) - 1);
                var bottom = Math.Min(stage.Height, (int)MathF.Floor(b.Y) - 1);
                AssertTimeline(right - left > 200 && bottom - top > 200, "The block bitmap fixture was outside the viewport.");
                foreach (var policy in new[] { "1", null })
                {
                    Environment.SetEnvironmentVariable("VECTOR_DISABLE_BITMAP_LAYER_COMMANDS", policy);
                    clearBaseFrame.Invoke(renderer, null);
                    PaintStage(stage); PaintStage(stage);
                    var pixels = CaptureStageGpuPixels(stage);
                    for (var y = top; y < bottom; y++)
                        for (var x = left; x < right; x++)
                        {
                            var offset = (y * stage.Width + x) * 4;
                            if (pixels[offset] != 79 || pixels[offset + 1] != 62 || pixels[offset + 2] != 45)
                                throw new InvalidOperationException($"Block tile seam leaked at ({x}, {y}), cache disabled={policy is not null}.");
                        }
                }
                using var frame = new Bitmap(stage.Width, stage.Height);
                using (var graphics = Graphics.FromImage(frame)) RequireMethod(typeof(StageControl), "DrawGdiFrame").Invoke(stage, [graphics]);
                for (var y = top; y < bottom; y++)
                    for (var x = left; x < right; x++)
                        if (frame.GetPixel(x, y).ToArgb() != Color.FromArgb(45, 62, 79).ToArgb())
                            throw new InvalidOperationException($"GDI block tile seam leaked at ({x}, {y}).");
                Console.WriteLine("block_bitmap_seams=direct2d,cached,software,point_sampling");
            }

            void VerifyBlockTileNavigation()
            {
                const float cell = 400;
                var grid = new VectorScene();
                grid.CreateEmpty(2, 1);
                var opaque = new BitmapImageRaster(new("opaque-block-navigation", 16, 16));
                var leaf = new BitmapImageRaster(new("alpha-block-navigation", 16, 16));
                for (var offset = 0; offset < opaque.Pixels.Length; offset += 4)
                {
                    opaque.Pixels[offset] = 79;
                    opaque.Pixels[offset + 1] = 62;
                    opaque.Pixels[offset + 2] = 45;
                    opaque.Pixels[offset + 3] = 255;
                    leaf.Pixels[offset + 2] = leaf.Pixels[offset + 3] = 128;
                }
                for (var y = 0; y < 12; y++)
                    for (var x = 0; x < 24; x++)
                        grid.AddBitmapObject(1, new PointF((x - 11.5f) * cell, (y - 5.5f) * cell),
                            new BitmapObjectData { ImageAssetId = "stone", PlacedSize = new(cell, cell) });
                grid.AddBitmapObject(0, new PointF(-.5f * cell, -.5f * cell),
                    new BitmapObjectData { ImageAssetId = "leaf", PlacedSize = new(cell, cell) });
                stage.BitmapImageResolver = asset => asset == "stone" ? opaque : leaf;
                stage.BitmapImageKeyResolver = asset => asset == "stone" ? opaque.Key : leaf.Key;
                stage.BindScene(grid);
                // Cross the dense zoom threshold while preserving the regular lattice.
                for (var x = 0; x < 32; x++)
                    grid.AddBitmapObject(1, new PointF((x + 25.5f) * cell, .5f * cell),
                        new BitmapObjectData { ImageAssetId = "stone", PlacedSize = new(cell, cell) });
                stage.RestoreViewState(stage.CaptureViewState() with { Zoom = 1.307f, CameraX = 31, CameraY = -11 });
                var previousBitmapLayer = Environment.GetEnvironmentVariable("VECTOR_DISABLE_BITMAP_LAYER");
                try
                {
                    Environment.SetEnvironmentVariable("VECTOR_DISABLE_BLOCK_TILE_CACHE", "1");
                    Environment.SetEnvironmentVariable("VECTOR_DISABLE_BITMAP_LAYER_COMMANDS", "1");
                    Environment.SetEnvironmentVariable("VECTOR_DISABLE_BITMAP_LAYER", "1");
                    clearBaseFrame.Invoke(renderer, null);
                    PaintStage(stage); PaintStage(stage);
                    var expected = CaptureStageGpuPixels(stage);
                    Environment.SetEnvironmentVariable("VECTOR_DISABLE_BLOCK_TILE_CACHE", null);
                    clearBaseFrame.Invoke(renderer, null);
                    var startingBuilds = renderer.BlockTileCacheBuilds;
                    PaintStage(stage); PaintStage(stage);
                    var actual = CaptureStageGpuPixels(stage);
                    AssertTimeline(stage.LastFrameUsedDirect2D && renderer.BlockTileCacheBuilds == startingBuilds + 1
                        && stage.LastStats.TileDraws is >= 2 and <= 4, "Regular bitmap terrain did not use sparse block chunks.");
                    PixelAgrees(expected, actual, stage.WorldToScreen(-.5f * cell, -.5f * cell));
                    PixelAgrees(expected, actual, stage.WorldToScreen(3.5f * cell, .5f * cell));
                    PixelAgrees(expected, actual, stage.WorldToScreen(4.5f * cell, .5f * cell));

                    var startingDraws = renderer.BlockTileCacheDraws;
                    for (var step = 0; step < 8; step++)
                    {
                        stage.Pan(7.4f, -3.7f);
                        stage.ZoomAt(new Point(350, 220), 1.017f, interactivePreview: true);
                        PaintStage(stage);
                    }
                    AssertTimeline(renderer.BlockTileCacheBuilds == startingBuilds + 1
                        && renderer.BlockTileCacheDraws >= startingDraws + 8,
                        "Pan/zoom rebuilt the world-space chunk cache.");

                    RequireMethod(typeof(StageControl), "EndZoomLodPreview").Invoke(stage, [true]);
                    stage.SetLayerContentHighlightLayer(1, Color.Cyan);
                    var highlightedCount = stage.LayerContentHighlightObjects.Count;
                    AssertTimeline(highlightedCount >= 24 && stage.LayerContentHighlightVisible,
                        "Dense tile layer was not highlighted at rest.");
                    stage.ZoomAt(new Point(310, 230), 1.017f, interactivePreview: true);
                    AssertTimeline(stage.ZoomLodPreviewActive && !stage.LayerContentHighlightVisible,
                        "Dense bitmap layer highlight ran during interactive zoom.");
                    PaintStage(stage);
                    RequireMethod(typeof(StageControl), "EndZoomLodPreview").Invoke(stage, [true]);
                    AssertTimeline(stage.LayerContentHighlightVisible && stage.LayerContentHighlightObjects.Count == highlightedCount,
                        "Zoom settlement did not restore the layer highlight.");
                    stage.Pan(3, -2);
                    AssertTimeline(!stage.LayerContentHighlightVisible, "Dense bitmap layer highlight ran during panning.");
                    RequireMethod(typeof(StageControl), "EndZoomLodPreview").Invoke(stage, [true]);
                    AssertTimeline(stage.LayerContentHighlightVisible, "Pan settlement did not restore the layer highlight.");
                    stage.ClearLayerContentHighlight();

                    var beforeRelightUploads = renderer.BlockTileBitmapUploads;
                    grid.UpdateBlockLighting([(0, 0x70707070u, 0u)]);
                    PaintStage(stage);
                    AssertTimeline(renderer.BlockTileCacheBuilds == startingBuilds + 2
                        && renderer.BlockTileBitmapUploads == beforeRelightUploads + 1,
                        "Relighting one chunk must rebuild only its bitmap.");
                    var beforePathChangeDraws = renderer.BlockTileCacheDraws;
                    stage.BitmapImageKeyResolver = _ => null;
                    PaintStage(stage);
                    AssertTimeline(renderer.BlockTileCacheBuilds == startingBuilds + 2
                        && renderer.BlockTileCacheDraws == beforePathChangeDraws + 1,
                        "Cold path stamps with unchanged image bytes discarded the block chunks.");
                    opaque = new BitmapImageRaster(new("changed-block-navigation", 16, 16));
                    PaintStage(stage);
                    AssertTimeline(renderer.BlockTileCacheBuilds == startingBuilds + 3, "A changed source did not update the block chunks.");
                    var drawsBeforeOffGrid = renderer.BlockTileCacheDraws;
                    grid.AddBitmapObject(0, new PointF(41, 73),
                        new BitmapObjectData { ImageAssetId = "leaf", PlacedSize = new(cell, cell) });
                    PaintStage(stage);
                    AssertTimeline(renderer.BlockTileCacheDraws == drawsBeforeOffGrid,
                        "An untagged bitmap outside the grid was batched as terrain.");
                    renderer.ReloadRuntimeResources();
                    AssertTimeline(RequireField(typeof(Direct2DStageRenderer), "_blockTiles").GetValue(renderer) is null,
                        "Device reset retained GPU block chunks.");
                }
                finally
                {
                    Environment.SetEnvironmentVariable("VECTOR_DISABLE_BITMAP_LAYER_COMMANDS", null);
                    Environment.SetEnvironmentVariable("VECTOR_DISABLE_BITMAP_LAYER", previousBitmapLayer);
                    Environment.SetEnvironmentVariable("VECTOR_DISABLE_BLOCK_TILE_CACHE", previousChunks);
                }
                Console.WriteLine("block_tile_navigation=legacy_grid,point_pixels,alpha,painter_order,camera_reuse,relight,asset_change,off_grid,device_reset");

                void PixelAgrees(byte[] expected, byte[] actual, PointF sample)
                {
                    var offset = ((int)sample.Y * stage.Width + (int)sample.X) * 4;
                    for (var channel = 0; channel < 4; channel++)
                        if (Math.Abs(expected[offset + channel] - actual[offset + channel]) > 1)
                            throw new InvalidOperationException(
                                $"Block chunk changed a cell center at {sample}: BGRA expected={Convert.ToHexString(expected.AsSpan(offset, 4))}, actual={Convert.ToHexString(actual.AsSpan(offset, 4))}.");
                }
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("VECTOR_DISABLE_BITMAP_LAYER_COMMANDS", previous);
            Environment.SetEnvironmentVariable("VECTOR_DISABLE_BLOCK_TILE_CACHE", previousChunks);
            BitmapImageRasterizer.ClearCache();
            DeleteTemporaryDirectory(root);
        }
    }
}
