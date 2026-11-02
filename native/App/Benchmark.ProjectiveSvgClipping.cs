using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunReferenceSvgRasterExtentRegression()
    {
        const string source = """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 200 100">
              <rect width="100" height="100" fill="#ed3428"/>
              <rect x="100" width="100" height="100" fill="#2879ed"/>
            </svg>
            """;
        // Seed a much larger raster so subsequent views exercise cache reuse,
        // in addition to the ordinary quantization mismatch.
        ImportedSvgRasterizer.Rasterize(source, 1024, 512);
        var scene = new VectorScene();
        scene.CreateEmpty();
        var index = scene.AddImportedSvgObject(0, PointF.Empty, new SizeF(6000, 3000), 0, source, "Extent fixture");
        scene.Argb[index] = Color.White.ToArgb();
        using var stage = new StageControl(scene) { Dock = DockStyle.Fill, WorldGridOpacity = 0, ExperimentalSceneLightingEnabled = false };
        using var form = new Form { ClientSize = new Size(640, 440), TopMost = true,
            StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.None };
        stage.ConfigureReferenceView(null, SceneDimension.TwoD);
        stage.SetReferenceCameraOrientation(0, 0, ReferenceCameraMotion.Immediate);
        stage.SetSceneCompositionResult(SceneCompositionResult.Empty, scene);
        form.Controls.Add(stage);
        form.Show();
        foreach (var zoom in new[] { 1.6f, 0.8f, 0.45f })
        {
            stage.RestoreViewState(stage.CaptureViewState() with { ReferenceZoomScale = zoom });
            form.BringToFront(); stage.Refresh(); Application.DoEvents();
            Thread.Sleep(60); FlushWorkspaceCapture();
            AssertTimeline(stage.LastFrameUsedDirect2D, "SVG extent regression did not use Direct2D.");
            using var capture = CaptureBenchmarkWindow(form);
            capture.Save(Path.Combine("artifacts", $"svg-extent-{zoom:F2}.png"));
            foreach (var (flat, expected) in new[] {
                (new PointF(-1500, 0), Color.FromArgb(237, 52, 40)),
                (new PointF(2400, 0), Color.FromArgb(40, 121, 237)) })
            {
                AssertTimeline(stage.TryProjectScenePoint(index, flat, out var screen, out _), "SVG sample projection failed.");
                var color = capture.GetPixel((int)screen.X, (int)screen.Y);
                AssertTimeline(Math.Abs(color.R - expected.R) < 12 && Math.Abs(color.G - expected.G) < 12
                    && Math.Abs(color.B - expected.B) < 12,
                    $"SVG texture was cropped at zoom {zoom}: sample={flat}, actual={color}, expected={expected}.");
            }
            var output = Path.Combine("artifacts", $"svg-extent-{zoom:F2}.png");
            capture.Save(output);
        }
        scene.SetLayerBlendMode(0, LayerBlendMode.Isolate);
        scene.SetLayerSymbolFilters(0, new SymbolFilters { ColorTransform = new() { Enabled = true,
            RedMultiplier = 0.5f, GreenMultiplier = 0.5f, BlueMultiplier = 0.5f } });
        foreach (var zoom in new[] { 0.45f, 1.6f, 0.8f })
        {
            stage.RestoreViewState(stage.CaptureViewState() with { ReferenceZoomScale = zoom });
            stage.Refresh(); Application.DoEvents(); Thread.Sleep(60); FlushWorkspaceCapture();
            AssertTimeline(stage.LastFrameUsedDirect2D, "RGB SVG group fell back to software.");
            using var capture = CaptureBenchmarkWindow(form);
            foreach (var (flat, expected) in new[] {
                (new PointF(-1500, 0), Color.FromArgb(119, 26, 20)),
                (new PointF(2400, 0), Color.FromArgb(20, 61, 119)) })
            {
                AssertTimeline(stage.TryProjectScenePoint(index, flat, out var screen, out _), "RGB SVG projection failed.");
                var actual = capture.GetPixel((int)screen.X, (int)screen.Y);
                AssertTimeline(Math.Abs(actual.R - expected.R) < 5 && Math.Abs(actual.G - expected.G) < 5
                    && Math.Abs(actual.B - expected.B) < 5, $"RGB SVG changed its extent or colour at zoom {zoom}: {actual}.");
            }
        }
        Console.WriteLine("reference_svg_raster_full_extent=passed");
    }

    private static void RunProjectiveSvgClippingRegression()
    {
        const string source = """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 320 200">
              <rect width="320" height="200" fill="#ff5a4e"/>
            </svg>
            """;
        var scene = new VectorScene();
        scene.CreateEmpty();
        var objectIndex = scene.AddImportedSvgObject(
            0, PointF.Empty, new SizeF(32_000, 20_000), 0, source, "Large perspective SVG");
        scene.Argb[objectIndex] = Color.White.ToArgb();
        using var stage = new StageControl(scene)
        {
            ClientSize = new Size(1280, 800),
            BackColor = Color.FromArgb(17, 19, 21),
            WorldGridOpacity = 0
        };
        var definition = new SceneDefinition { Dimension = SceneDimension.ThreeD };
        definition.Camera.Projection = CameraProjection.Perspective;
        ConfigureNeutralReferenceLighting(definition);
        stage.ConfigureReferenceView(definition, SceneDimension.ThreeD, ReferenceCameraMotion.Immediate);
        stage.RestoreViewState(stage.CaptureViewState() with
        {
            ReferenceYaw = -0.73f,
            ReferencePitch = 0.99f,
            ReferenceDistance = 12_000f,
            ReferenceZoomScale = 0.49964538f,
            ReferenceTargetX = -9421.196f,
            ReferenceTargetY = -997.0125f,
            ReferenceTargetZ = 0f
        });
        AssertTimeline(stage.TryGetReference3DProjectiveMesh(objectIndex, true, out var mesh)
            && mesh.Length > 100, "The large SVG fixture did not exercise a subdivided perspective mesh.");
        var draw = typeof(StageControl).GetMethod("DrawGdi", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using var bitmap = new Bitmap(stage.Width, stage.Height, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);
        draw.Invoke(stage, [graphics]); // Warm source decoding and JIT.
        stage.SetReferenceCameraOrientation(-0.72f, 0.99f, ReferenceCameraMotion.Immediate);
        var watch = Stopwatch.StartNew();
        draw.Invoke(stage, [graphics]);
        watch.Stop();
        AssertTimeline(watch.Elapsed.TotalMilliseconds < 1500,
            $"Large perspective SVG navigation exceeded 1500 ms: {watch.Elapsed.TotalMilliseconds:F1} ms.");

        using var footprint = new GraphicsPath(FillMode.Alternate);
        foreach (var contour in stage.GetReference3DProjectedContours(objectIndex))
        {
            if (contour.Closed && contour.Points.Length >= 3) footprint.AddPolygon(contour.Points);
        }
        using var edge = new Pen(Color.Black, 4f);
        var checkedPixels = 0;
        var incorrectPixels = 0;
        for (var y = 4; y < bitmap.Height; y += 8)
        {
            for (var x = 4; x < bitmap.Width; x += 8)
            {
                var point = new PointF(x + 0.5f, y + 0.5f);
                if (!footprint.IsVisible(point) || footprint.IsOutlineVisible(point, edge)) continue;
                checkedPixels++;
                var color = bitmap.GetPixel(x, y);
                if (Math.Abs(color.R - 255) > 24 || Math.Abs(color.G - 90) > 24 || Math.Abs(color.B - 78) > 24)
                    incorrectPixels++;
            }
        }
        AssertTimeline(checkedPixels > 100 && incorrectPixels == 0,
            $"Large perspective SVG lost interior pixels or developed mesh seams: checked={checkedPixels}, incorrect={incorrectPixels}.");
        Console.WriteLine($"projective_svg_viewport_raster_regression=ok milliseconds={watch.Elapsed.TotalMilliseconds:F1} checked_pixels={checkedPixels}");
    }
}
