namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunBitmapLayerHighlightCacheRegression()
    {
        var policy = Environment.GetEnvironmentVariable("VECTOR_DISABLE_BITMAP_HIGHLIGHT_CACHE");
        try
        {
            var scene = new VectorScene(); scene.CreateEmpty();
            var raster = new BitmapImageRaster(new("highlight-test", 32, 32));
            for (var pixel = 0; pixel < raster.Pixels.Length; pixel += 4)
            { raster.Pixels[pixel] = 80; raster.Pixels[pixel + 1] = 120; raster.Pixels[pixel + 2] = 180; raster.Pixels[pixel + 3] = 255; }
            for (var index = 0; index < 96; index++)
                scene.AddBitmapObject(0, new PointF((index % 8 - 4) * 300, (index / 8 - 2) * 220),
                    new BitmapObjectData { ImageAssetId = "highlight", PlacedSize = new SizeF(1600 + index * 11, 900 + index * 7) });
            using var host = new Form { ClientSize = new Size(640, 480), ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual, Location = new Point(-3000, -3000) };
            using var stage = new StageControl(scene) { Dock = DockStyle.Fill, BitmapImageResolver = _ => raster };
            host.Controls.Add(stage); host.Show(); Application.DoEvents();
            stage.SetLayerContentHighlightLayer(0, Color.Cyan);
            var renderer = (Direct2DStageRenderer)RequireField(typeof(StageControl), "_direct2DRenderer").GetValue(stage)!;
            var maxDelta = 0;
            foreach (var zoom in new[] { 0.72f, 6.56f })
            {
                stage.RestoreViewState(stage.CaptureViewState() with { Zoom = zoom });
                Compare();
                var builds = renderer.LayerContentHighlightCommandBuilds;
                stage.RestoreViewState(stage.CaptureViewState() with
                    { CameraX = stage.CameraX + 350, CameraY = stage.CameraY - 180 });
                stage.Invalidate(); PaintStage(stage);
                AssertTimeline(renderer.LayerContentHighlightCommandBuilds > builds,
                    "Bitmap highlight cache did not rebuild after camera movement.");
                Compare();
            }
            scene.Angle[0] = 0.3f; scene.RebuildGeometryIndex(); Compare();
            var colorBuilds = renderer.LayerContentHighlightCommandBuilds;
            stage.SetLayerContentHighlightLayer(0, Color.OrangeRed); PaintStage(stage);
            AssertTimeline(renderer.LayerContentHighlightCommandBuilds > colorBuilds,
                "Bitmap highlight cache did not invalidate its changed color.");
            Compare();
            var geometryBuilds = renderer.LayerContentHighlightCommandBuilds;
            scene.X[0] += 300; scene.RebuildGeometryIndex(); stage.Invalidate(); PaintStage(stage);
            AssertTimeline(renderer.LayerContentHighlightCommandBuilds > geometryBuilds,
                "Bitmap highlight cache did not invalidate changed geometry.");
            Compare();
            stage.ClearLayerContentHighlight(); PaintStage(stage);
            AssertTimeline(RequireField(typeof(Direct2DStageRenderer), "_layerContentHighlightCommands").GetValue(renderer) is null,
                "Clearing bitmap highlight retained its GPU command cache.");
            Console.WriteLine($"bitmap_layer_highlight_cache=pixel_delta:{maxDelta},camera=ok,zoom=ok,color=ok,geometry=ok,cleanup=ok");

            void Compare()
            {
                Environment.SetEnvironmentVariable("VECTOR_DISABLE_BITMAP_HIGHLIGHT_CACHE", "1");
                renderer.InvalidateLayerContentHighlightCommands();
                stage.Invalidate(); PaintStage(stage); PaintStage(stage);
                var expected = CaptureStageGpuPixels(stage);
                Environment.SetEnvironmentVariable("VECTOR_DISABLE_BITMAP_HIGHLIGHT_CACHE", null);
                renderer.InvalidateLayerContentHighlightCommands();
                stage.Invalidate(); PaintStage(stage);
                var beforeReuse = renderer.LayerContentHighlightCommandReuses;
                PaintStage(stage);
                var actual = CaptureStageGpuPixels(stage);
                var delta = actual.Zip(expected, (a, b) => Math.Abs(a - b)).Max();
                maxDelta = Math.Max(maxDelta, delta);
                AssertTimeline(stage.LastFrameUsedDirect2D && delta <= 1,
                    $"Bitmap highlight cache changed original GPU pixels (delta={delta}).");
                AssertTimeline(renderer.LayerContentHighlightCommandReuses > beforeReuse,
                    "Bitmap highlight cache did not reuse the warmed frame.");
            }
        }
        finally { Environment.SetEnvironmentVariable("VECTOR_DISABLE_BITMAP_HIGHLIGHT_CACHE", policy); }
    }
}
