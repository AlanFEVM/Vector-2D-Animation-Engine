using System.Drawing.Imaging;
using System.Numerics;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunSceneLightingVisualRegression()
    {
        const int width = 720;
        const int height = 520;
        var background = Color.FromArgb(255, 14, 18, 25);
        var scene = new VectorScene();
        scene.CreateEmpty();
        var centers = new[]
        {
            new PointF(-900, -480), new PointF(0, -480), new PointF(900, -480),
            new PointF(-900, 480), new PointF(0, 480), new PointF(900, 480)
        };
        var colors = new[]
        {
            Color.FromArgb(255, 224, 74, 72), Color.FromArgb(255, 224, 74, 72), Color.FromArgb(255, 224, 74, 72),
            Color.FromArgb(255, 52, 164, 214), Color.FromArgb(255, 52, 164, 214), Color.FromArgb(255, 52, 164, 214)
        };
        var roughness = new[] { 0.2f, 0.5f, 0.85f, 0.2f, 0.5f, 0.85f };
        var metallic = new[] { 0f, 0f, 0f, 1f, 1f, 1f };
        var objectIndices = new int[centers.Length];
        for (var index = 0; index < centers.Length; index++)
        {
            objectIndices[index] = scene.AddObject(
                0,
                centers[index],
                new SizeF(640, 520),
                angle: 0,
                stroke: 0,
                color: colors[index],
                strokeColor: Color.Transparent,
                atoms: 12,
                shapeKind: ShapeKind.Rectangle);
        }
        var directional = SceneLightDefinition.CreateDefaultDirectional();
        directional.TryApply(
            directional.Name,
            directional.Settings with
            {
                ColorArgb = Color.FromArgb(255, 255, 218, 180).ToArgb(),
                Intensity = 1.4f,
                RotationDegrees = new Vector3(148, -28, 0),
                CastsShadows = false,
                ShadowStrength = 0
            });
        var area = SceneLightDefinition.CreateDefault(SceneLightKind.Area);
        area.TryApply(
            area.Name,
            area.Settings with
            {
                ColorArgb = Color.FromArgb(255, 150, 206, 255).ToArgb(),
                Intensity = 2.1f,
                Position = new Vector3(0, 0, -8_000),
                Range = 12_000,
                AreaSize = new Vector2(2_400, 1_800),
                CastsShadows = false,
                ShadowStrength = 0
            });
        var ambient = SceneLightDefinition.CreateDefaultAmbient();
        ambient.TryApply(ambient.Name, ambient.Settings with { Intensity = 0.18f });
        var definition = new SceneDefinition { Dimension = SceneDimension.ThreeD };
        definition.RestoreLights([directional, area, ambient], lightsWerePresent: true);
        var owners = objectIndices
            .Select((_, index) => new SceneCompositionObjectOwner($"lighting-visual-{index}", "lighting-visual"))
            .ToArray();
        var poses = objectIndices
            .Select(_ => new SceneCompositionObjectPose(Matrix4x4.Identity))
            .ToArray();
        var materials = objectIndices
            .Select((_, index) => SpatialOpticalMaterial.Default with
            {
                Metallic = metallic[index],
                Roughness = roughness[index]
            })
            .ToArray();
        var composition = new SceneCompositionResult(owners, poses, materials);
        using var stage = new StageControl(scene)
        {
            ClientSize = new Size(width, height),
            BackColor = background,
            WorldGridOpacity = 0
        };
        stage.ConfigureReferenceView(definition, SceneDimension.ThreeD);
        stage.ResetReferenceCameraView();
        stage.SetReferenceCameraOrientation(0.34f, -0.2f);
        RequireMethod(typeof(StageControl), "ApplyReferenceCameraFrame").Invoke(stage,
            [stage.CaptureReferenceCameraFrameForPersistence() with { ZoomScale = 6f }]);
        stage.SetSceneCompositionResult(composition, scene);
        if (!stage.GetReference3DSceneRenderItems().Any(item => item.Kind == Reference3DRenderKind.FrontFill))
        {
            throw new InvalidOperationException("The scene-lighting visual fixture produced no front-fill render items.");
        }
        var samplePoints = objectIndices.Select(index =>
        {
            if (!stage.TryProjectScenePoint(index, centers[index], out var point, out _)
                || point.X < 0f
                || point.X >= width
                || point.Y < 0f
                || point.Y >= height)
                throw new InvalidOperationException(
                    $"The scene-lighting visual object {index} projected outside the Stage: point={point}, size={width}x{height}.");
            return point;
        }).ToArray();
        var drawGdi = RequireMethod(typeof(StageControl), "DrawGdi");
        using var gdiBitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(gdiBitmap)) drawGdi.Invoke(stage, [graphics]);
        var artifactDirectory = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts", "lighting-upgrade"));
        Directory.CreateDirectory(artifactDirectory);
        var gdiPath = Path.Combine(artifactDirectory, "scene-lighting-gdi.png");
        gdiBitmap.Save(gdiPath, ImageFormat.Png);
        var gdiSamples = samplePoints.Select(point => SampleRegion(gdiBitmap, point, radius: 7)).ToArray();
        AssertLightingSamples(gdiSamples, background, "GDI");
        var screenBounds = SystemInformation.VirtualScreen;
        using var form = new Form
        {
            ShowInTaskbar = false,
            TopMost = true,
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(Math.Max(screenBounds.Left, screenBounds.Right - width - 12), Math.Max(screenBounds.Top, screenBounds.Bottom - height - 48)),
            ClientSize = new Size(width, height),
            BackColor = background
        };
        stage.Dock = DockStyle.Fill;
        form.Controls.Add(stage);
        form.Show();
        form.Activate();
        form.BringToFront();
        Application.DoEvents();
        using var direct2DBitmap = CapturePresentedStage(form, stage, background);
        if (!stage.LastFrameUsedDirect2D || !stage.GpuAccelerationActive)
        {
            throw new InvalidOperationException(
                $"The scene-lighting visual capture did not use Direct2D: direct2d={stage.LastFrameUsedDirect2D}, gpu={stage.GpuAccelerationActive}.");
        }
        Console.WriteLine("scene_lighting_real_hwnd_direct2d=ok");
        if (direct2DBitmap is null)
        {
            Console.WriteLine($"scene_lighting_visual_gdi={gdiPath}");
            Console.WriteLine("scene_lighting_visual=skipped_no_visible_desktop");
            return;
        }
        var direct2DPath = Path.Combine(artifactDirectory, "scene-lighting-direct2d.png");
        direct2DBitmap.Save(direct2DPath, ImageFormat.Png);
        var directSamples = samplePoints
            .Select(point => SampleRegion(direct2DBitmap, point, radius: 7))
            .ToArray();
        AssertLightingSamples(directSamples, background, "Direct2D");
        for (var index = 0; index < gdiSamples.Length; index++)
        {
            if (ColorDistance(gdiSamples[index], directSamples[index]) > 52)
            {
                throw new InvalidOperationException(
                    $"GDI and Direct2D lighting diverged at sample {index}: "
                    + $"gdi={gdiSamples[index].ToArgb():X8}, direct2d={directSamples[index].ToArgb():X8}.");
            }
        }

        Console.WriteLine($"scene_lighting_visual_gdi={gdiPath}");
        Console.WriteLine($"scene_lighting_visual_direct2d={direct2DPath}");
        Console.WriteLine("scene_lighting_visual=ok");

        static Bitmap? CapturePresentedStage(Form form, StageControl stage, Color sentinel)
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                form.Activate();
                form.BringToFront();
                stage.Invalidate();
                stage.Update();
                Application.DoEvents();
                var bounds = stage.RectangleToScreen(stage.ClientRectangle);
                Bitmap? capture = null;
                try
                {
                    capture = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
                    using var graphics = Graphics.FromImage(capture);
                    graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
                    var corners = new[]
                    {
                        new Point(2, 2), new Point(capture.Width - 3, 2),
                        new Point(2, capture.Height - 3), new Point(capture.Width - 3, capture.Height - 3)
                    };
                    if (corners.All(point => ColorDistance(capture.GetPixel(point.X, point.Y), sentinel) <= 12))
                    {
                        return capture;
                    }
                }
                catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
                    or System.Runtime.InteropServices.ExternalException
                    or ArgumentException)
                {
                }
                capture?.Dispose();
                Application.DoEvents();
            }

            return null;
        }

        static void AssertLightingSamples(Color[] samples, Color background, string backend)
        {
            if (samples.All(sample => ColorDistance(sample, background) <= 22))
            {
                throw new InvalidOperationException($"The {backend} scene-lighting capture contained a background sample.");
            }

            for (var row = 0; row < 2; row++)
            {
                var first = samples[row * 3];
                var rowDifference = Math.Max(
                    ColorDistance(first, samples[row * 3 + 1]),
                    ColorDistance(first, samples[row * 3 + 2]));
                if (rowDifference <= 8)
                {
                    throw new InvalidOperationException(
                        $"The {backend} material row did not produce a visible GGX response difference: row={row}, color={first}.");
                }
            }
        }

        static Color SampleRegion(Bitmap bitmap, PointF point, int radius)
        {
            var centerX = Math.Clamp((int)MathF.Round(point.X), 0, bitmap.Width - 1);
            var centerY = Math.Clamp((int)MathF.Round(point.Y), 0, bitmap.Height - 1);
            long red = 0, green = 0, blue = 0, count = 0;
            for (var y = Math.Max(0, centerY - radius); y <= Math.Min(bitmap.Height - 1, centerY + radius); y++)
            {
                for (var x = Math.Max(0, centerX - radius); x <= Math.Min(bitmap.Width - 1, centerX + radius); x++)
                {
                    var color = bitmap.GetPixel(x, y);
                    red += color.R;
                    green += color.G;
                    blue += color.B;
                    count++;
                }
            }
            return Color.FromArgb(255, (int)(red / count), (int)(green / count), (int)(blue / count));
        }

        static int ColorDistance(Color left, Color right) =>
            Math.Max(Math.Abs(left.R - right.R), Math.Max(Math.Abs(left.G - right.G), Math.Abs(left.B - right.B)));
    }
}
