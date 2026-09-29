using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
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
