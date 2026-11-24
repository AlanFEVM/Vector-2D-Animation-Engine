namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunHighlightOcclusionRegression()
    {
        var types = new[] { ShapeKind.Rectangle, ShapeKind.Ellipse, ShapeKind.Path, ShapeKind.Line,
            ShapeKind.Text, ShapeKind.ImportedSvg, ShapeKind.Bitmap };
        foreach (var shape in types)
        {
            var scene = new VectorScene(); scene.CreateEmpty();
            const string svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 10 10\"><rect width=\"10\" height=\"10\"/></svg>";
            var selected = shape switch
            {
                ShapeKind.Text => scene.AddTextObject(0, PointF.Empty,
                    new TextObjectData("Test", "Arial", 24, TextFontStyle.Regular,
                        TextHorizontalAlignment.Left, new SizeF(1800, 1000)), Color.Red),
                ShapeKind.ImportedSvg => scene.AddImportedSvgObject(0, PointF.Empty, new SizeF(1800, 1000), svg, "SVG"),
                ShapeKind.Bitmap => scene.AddBitmapObject(0, PointF.Empty,
                    new BitmapObjectData { ImageAssetId = "test", PlacedSize = new SizeF(1800, 1000) }),
                ShapeKind.Path => scene.AppendPathObjectContours(0,
                    [[new(-900, -500), new(900, -500), new(900, 500), new(-900, 500)]], 0, Color.Red, Color.Transparent, 4),
                _ => scene.AddObject(0, PointF.Empty, new SizeF(1800, 1000), 0, 0, Color.Red, 4, shape)
            };
            var cover = scene.AddObject(0, PointF.Empty, new SizeF(7000, 7000), 0, 0, Color.Blue, 0, ShapeKind.Rectangle);
            scene.RebuildGeometryIndex();
            using var stage = new StageControl(scene) { Size = new Size(400, 280) };
            stage.SetVisibleWorldWidth(8000);
            stage.SetLayerContentHighlightObjects([selected], Color.Cyan);
            AssertTimeline(stage.ClipHighlightPolylines(selected, stage.GetLayerContentHighlightWorldContours(selected), shape != ShapeKind.Line).Length == 0,
                $"A covered {shape} retained its highlight outline.");
            using (var bitmap = new Bitmap(stage.Width, stage.Height))
            {
                using var graphics = Graphics.FromImage(bitmap);
                graphics.Clear(Color.Transparent);
                RequireMethod(typeof(StageControl), "DrawLayerContentHighlight", [typeof(Graphics)]).Invoke(stage, [graphics]);
                AssertTimeline(!Enumerable.Range(0, bitmap.Height).Any(y => Enumerable.Range(0, bitmap.Width).Any(x => bitmap.GetPixel(x, y).A != 0)),
                    $"GDI drew the highlight of a fully covered {shape}.");
            }
            scene.Argb[cover] = Color.FromArgb(100, Color.Blue).ToArgb();
            AssertTimeline(stage.ClipHighlightPolylines(selected, stage.GetLayerContentHighlightWorldContours(selected), shape != ShapeKind.Line).Length > 0,
                $"Transparent content hid the {shape} highlight.");
            scene.Argb[cover] = Color.Blue.ToArgb();
            scene.TransformObjects([cover], point => new PointF(point.X + 10000, point.Y));
            scene.RebuildGeometryIndex();
            AssertTimeline(stage.ClipHighlightPolylines(selected, stage.GetLayerContentHighlightWorldContours(selected), shape != ShapeKind.Line).Length > 0,
                $"Moving the covering object left a stale {shape} highlight clip.");
        }

        var bitmapScene = new VectorScene(); bitmapScene.CreateEmpty();
        var lower = bitmapScene.AddObject(0, PointF.Empty, new SizeF(1600, 1000), 0, 0, Color.Red, 0, ShapeKind.Rectangle);
        var upper = bitmapScene.AddBitmapObject(0, PointF.Empty,
            new BitmapObjectData { ImageAssetId = "opaque", PlacedSize = new SizeF(3000, 2000) });
        bitmapScene.RebuildGeometryIndex();
        var raster = new BitmapImageRaster(new("opaque-highlight", 16, 16));
        for (var pixel = 0; pixel < raster.Pixels.Length; pixel += 4)
        { raster.Pixels[pixel] = 200; raster.Pixels[pixel + 1] = 80; raster.Pixels[pixel + 2] = 40; raster.Pixels[pixel + 3] = 255; }
        using var host = new Form { ClientSize = new Size(400, 280), ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual, Location = new Point(-3000, -3000) };
        var opaque = true;
        using var gpuStage = new StageControl(bitmapScene)
        {
            Dock = DockStyle.Fill, BitmapImageResolver = _ => raster, BitmapImageKeyResolver = _ => raster.Key,
            BitmapImageOpacityProvider = (_, _) => opaque
        };
        host.Controls.Add(gpuStage); host.Show(); Application.DoEvents(); gpuStage.SetVisibleWorldWidth(8000);
        gpuStage.SetLayerContentHighlightObjects([lower], Color.Cyan);
        PaintStage(gpuStage); PaintStage(gpuStage);
        var highlighted = CaptureStageGpuPixels(gpuStage);
        gpuStage.ClearLayerContentHighlight(); PaintStage(gpuStage); PaintStage(gpuStage);
        var plain = CaptureStageGpuPixels(gpuStage);
        AssertTimeline(gpuStage.LastFrameUsedDirect2D && highlighted.SequenceEqual(plain),
            "Direct2D showed a lower vector highlight through an opaque bitmap.");
        AssertTimeline(gpuStage.ClipHighlightPolylines(lower, gpuStage.GetLayerContentHighlightWorldContours(lower), true).Length == 0,
            "An opaque bitmap did not hide a lower vector highlight.");
        opaque = false;
        gpuStage.Invalidate(); PaintStage(gpuStage);
        AssertTimeline(gpuStage.ClipHighlightPolylines(lower, gpuStage.GetLayerContentHighlightWorldContours(lower), true).Length > 0,
            "A transparent raster was treated as an opaque rectangle.");
        opaque = true;
        gpuStage.Invalidate(); PaintStage(gpuStage);
        bitmapScene.TransformObjects([upper], point => new PointF(point.X + 1400, point.Y));
        bitmapScene.RebuildGeometryIndex();
        var partial = gpuStage.ClipHighlightPolylines(lower, gpuStage.GetLayerContentHighlightWorldContours(lower), true);
        AssertTimeline(partial.Length > 0 && partial.SelectMany(contour => contour).All(point => point.X <= -99.9f),
            "Partially covered highlights crossed into an opaque bitmap.");
        // Camera-only changes must retain exact world-space occlusion, while
        // source opacity and placement changes must invalidate it.
        var cacheScene = new VectorScene(); cacheScene.CreateEmpty();
        for (var index = 0; index < 80; index++)
            cacheScene.AddBitmapObject(0, PointF.Empty, new BitmapObjectData
                { ImageAssetId = "opaque", PlacedSize = new SizeF(3000, 2000) });
        cacheScene.RebuildGeometryIndex();
        using var cachedStage = new StageControl(cacheScene) { Size = new Size(400, 280),
            BitmapImageKeyResolver = _ => raster.Key, BitmapImageOpacityProvider = (_, _) => opaque };
        cachedStage.SetVisibleWorldWidth(8000);
        var cached = cachedStage.GetHighlightOcclusionContours(0);
        var builds = cachedStage.HighlightCoverageBuilds;
        for (var step = 0; step < 8; step++)
        {
            cachedStage.ZoomAt(new Point(100, 100), 1.06f, interactivePreview: true);
            cachedStage.Pan(3, -2);
            AssertTimeline(ReferenceEquals(cached, cachedStage.GetHighlightOcclusionContours(0))
                && cachedStage.HighlightCoverageBuilds == builds, "Camera movement rebuilt bitmap world coverage.");
        }
        opaque = false;
        cachedStage.ZoomAt(Point.Empty, 1.01f);
        AssertTimeline(cachedStage.GetHighlightOcclusionContours(0).Length == 0,
            "Changing bitmap opacity reused opaque world coverage.");
        opaque = true;
        cacheScene.TransformObjects(Enumerable.Range(1, 79).ToArray(), point => new PointF(point.X + 10000, point.Y));
        cacheScene.RebuildGeometryIndex();
        AssertTimeline(cachedStage.GetHighlightOcclusionContours(0).SelectMany(contour => contour).All(point => point.X > 8000),
            "Moving bitmap occluders reused old world coverage.");
        Console.WriteLine("bitmap_highlight_world_cache=passed,zoom_pan_reuse=ok,opacity=ok,movement=ok");
        Console.WriteLine("highlight_occlusion=passed,types=rectangle/ellipse/path/line/text/svg/bitmap,gdi=ok,direct2d=exact,opaque_bitmap=ok,transparent=ok,partial=ok,movement=ok");
    }
}
