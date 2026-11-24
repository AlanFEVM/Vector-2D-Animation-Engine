using System.Runtime.InteropServices;
using Vortice.Direct2D1;
using Vortice.DXGI;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static byte[] CaptureStageGpuPixels(StageControl stage)
    {
        var renderer = RequireField(typeof(StageControl), "_direct2DRenderer").GetValue(stage)!;
        var context = (ID2D1DeviceContext)RequireField(typeof(Direct2DStageRenderer), "_target").GetValue(renderer)!;
        using var image = context.Target;
        using var source = image!.QueryInterface<ID2D1Bitmap>();
        using var readback = context.CreateBitmap(new Vortice.Mathematics.SizeI(stage.Width, stage.Height), IntPtr.Zero, 0,
            new BitmapProperties1(new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm,
                Vortice.DCommon.AlphaMode.Premultiplied), 96, 96, BitmapOptions.CpuRead | BitmapOptions.CannotDraw));
        readback.CopyFromBitmap(source).CheckError();
        var mapped = readback.Map(MapOptions.Read);
        try
        {
            var pixels = new byte[checked(stage.Width * stage.Height * 4)];
            for (var row = 0; row < stage.Height; row++)
                Marshal.Copy(IntPtr.Add(mapped.Bits, checked(row * (int)mapped.Pitch)), pixels,
                    row * stage.Width * 4, stage.Width * 4);
            return pixels;
        }
        finally { readback.Unmap(); }
    }

    private static void RunBitmapOriginCacheRegression()
    {
        var root = CreateTemporaryDirectory("bitmap-origin-cache");
        var policy = Environment.GetEnvironmentVariable("VECTOR_DISABLE_BITMAP_ORIGIN_CACHE");
        try
        {
            var path = Path.Combine(root, "origin.png");
            SaveImage(Color.FromArgb(180, Color.Coral));
            BitmapImageRasterizer.ClearCache();
            var scene = new VectorScene(); scene.CreateEmpty();
            for (var index = 0; index < 3; index++)
                scene.AddBitmapObject(0, new PointF(index * 600 - 600, index * 80),
                    new BitmapObjectData { ImageAssetId = "origin", PlacedSize = new SizeF(1200, 800) });
            using var host = new Form { ClientSize = new Size(640, 480), ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual, Location = new Point(-3000, -3000) };
            using var stage = new StageControl(scene) { Dock = DockStyle.Fill,
                BitmapImageResolver = _ => BitmapImageRasterizer.Decode(path, BitmapImageImportSettings.Default),
                BitmapImageKeyResolver = _ => BitmapImageRasterizer.TryGetSourceKey(new FileInfo(path),
                    BitmapImageImportSettings.Default, out var key) ? key : null };
            host.Controls.Add(stage); host.Show(); Application.DoEvents(); PaintStage(stage);
            stage.BeginSelectionMoveFeedback([0, 1, 2]);
            var session = scene.BeginTransformSession([0, 1, 2]);
            scene.ApplyTranslationSessionForPreview(session, 250, -75);
            var renderer = (Direct2DStageRenderer)RequireField(typeof(StageControl), "_direct2DRenderer").GetValue(stage)!;
            ComparePixels();
            var builds = renderer.BitmapOriginBuilds;
            AssertTimeline(builds > 0 && renderer.BitmapOriginReuses > 0, "Frozen bitmap origins were not cached/reused.");
            SaveImage(Color.FromArgb(130, Color.DodgerBlue));
            File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(2));
            ComparePixels();
            AssertTimeline(renderer.BitmapOriginBuilds > builds, "A changed source did not invalidate frozen bitmap origins.");
            builds = renderer.BitmapOriginBuilds;
            stage.RestoreViewState(stage.CaptureViewState() with { Zoom = stage.Zoom * 1.25f });
            ComparePixels();
            AssertTimeline(renderer.BitmapOriginBuilds > builds, "A changed camera did not invalidate frozen bitmap origins.");
            stage.ClearSelectionFillDragFront(); PaintStage(stage);
            AssertTimeline(RequireField(typeof(Direct2DStageRenderer), "_bitmapOriginTarget").GetValue(renderer) is null,
                "Ending a drag retained its frozen origin render target.");
            Console.WriteLine("bitmap_origin_cache=pixel_identical,source_invalidation=ok,camera_invalidation=ok,cleanup=ok");

            void ComparePixels()
            {
                Environment.SetEnvironmentVariable("VECTOR_DISABLE_BITMAP_ORIGIN_CACHE", "1");
                stage.Invalidate();
                // Populate both swap-chain buffers before reading the HWND target.
                PaintStage(stage); PaintStage(stage); var expected = CaptureStageGpuPixels(stage);
                Environment.SetEnvironmentVariable("VECTOR_DISABLE_BITMAP_ORIGIN_CACHE", null);
                stage.Invalidate();
                PaintStage(stage); PaintStage(stage); var actual = CaptureStageGpuPixels(stage);
                var delta = actual.Zip(expected, (a, b) => Math.Abs(a - b)).Max();
                if (delta != 0)
                {
                    Directory.CreateDirectory("artifacts");
                    SavePixels(expected, "artifacts/bitmap-origin-reference.png");
                    SavePixels(actual, "artifacts/bitmap-origin-actual.png");
                }
                AssertTimeline(stage.LastFrameUsedDirect2D && delta == 0,
                    $"Caching frozen bitmap origins changed GPU pixels (delta={delta}).");
            }

            void SavePixels(byte[] pixels, string file)
            {
                using var image = new Bitmap(stage.Width, stage.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                var data = image.LockBits(stage.ClientRectangle, System.Drawing.Imaging.ImageLockMode.WriteOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                try
                {
                    for (var row = 0; row < stage.Height; row++)
                        Marshal.Copy(pixels, row * stage.Width * 4, IntPtr.Add(data.Scan0, row * data.Stride), stage.Width * 4);
                }
                finally { image.UnlockBits(data); }
                image.Save(file, System.Drawing.Imaging.ImageFormat.Png);
            }

            void SaveImage(Color color)
            {
                using var image = new Bitmap(128, 96);
                using (var graphics = Graphics.FromImage(image))
                { graphics.Clear(color); graphics.FillRectangle(Brushes.Navy, 10, 12, 25, 20); }
                image.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("VECTOR_DISABLE_BITMAP_ORIGIN_CACHE", policy);
            BitmapImageRasterizer.ClearCache(); DeleteTemporaryDirectory(root);
        }
    }
}
