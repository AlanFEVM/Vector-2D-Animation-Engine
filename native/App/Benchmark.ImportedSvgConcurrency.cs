using System.Collections.Concurrent;
using System.Drawing.Imaging;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunImportedSvgConcurrencyRegression(string source)
    {
        ImportedSvgRasterizer.ClearCache();
        try
        {
            var retainedRaster = ImportedSvgRasterizer.Rasterize(source, 64, 64);
            for (var index = 0; index < 112; index++)
            {
                _ = ImportedSvgRasterizer.Rasterize(
                    source,
                    32 + index % 16 * 32,
                    32 + index / 16 * 32);
            }

            using (var retainedBitmap = retainedRaster.AcquireBitmap())
            using (var retainedTarget = new Bitmap(
                       retainedRaster.PixelWidth,
                       retainedRaster.PixelHeight,
                       PixelFormat.Format32bppPArgb))
            using (var retainedGraphics = Graphics.FromImage(retainedTarget))
            {
                retainedGraphics.DrawImageUnscaled(retainedBitmap.Bitmap, 0, 0);
            }

            var failures = new ConcurrentQueue<Exception>();
            using var leasesReady = new CountdownEvent(2);
            using var drawGate = new ManualResetEventSlim(false);
            var sharedRaster = ImportedSvgRasterizer.Rasterize(source, 640, 480);
            var workers = Enumerable.Range(0, 2)
                .Select(_ => Task.Run(() =>
                {
                    var signaled = false;
                    try
                    {
                        using var bitmap = sharedRaster.AcquireBitmap();
                        using var target = new Bitmap(
                            sharedRaster.PixelWidth,
                            sharedRaster.PixelHeight,
                            PixelFormat.Format32bppPArgb);
                        using var graphics = Graphics.FromImage(target);
                        leasesReady.Signal();
                        signaled = true;
                        drawGate.Wait();
                        for (var iteration = 0; iteration < 64; iteration++)
                        {
                            graphics.DrawImageUnscaled(bitmap.Bitmap, 0, 0);
                        }
                    }
                    catch (Exception exception)
                    {
                        failures.Enqueue(exception);
                    }
                    finally
                    {
                        if (!signaled) leasesReady.Signal();
                    }
                }))
                .ToArray();

            var cacheChurn = Task.Run(() =>
            {
                try
                {
                    if (!leasesReady.Wait(TimeSpan.FromSeconds(30)))
                    {
                        failures.Enqueue(new InvalidOperationException(
                            "Imported SVG bitmap workers did not acquire their leases."));
                        drawGate.Set();
                        return;
                    }

                    ImportedSvgRasterizer.ClearCache();
                    drawGate.Set();
                    for (var index = 0; index < 48; index++)
                    {
                        var raster = ImportedSvgRasterizer.Rasterize(
                            source,
                            128 + index % 16 * 32,
                            96 + index % 9 * 16);
                        using var bitmap = raster.AcquireBitmap();
                        using var target = new Bitmap(
                            raster.PixelWidth,
                            raster.PixelHeight,
                            PixelFormat.Format32bppPArgb);
                        using var graphics = Graphics.FromImage(target);
                        graphics.DrawImageUnscaled(bitmap.Bitmap, 0, 0);
                        if (index % 3 == 0) ImportedSvgRasterizer.ClearCache();
                    }
                }
                catch (Exception exception)
                {
                    failures.Enqueue(exception);
                    drawGate.Set();
                }
            });

            Task.WaitAll(workers.Append(cacheChurn).ToArray());
            if (!failures.IsEmpty)
            {
                throw new InvalidOperationException(
                    "Imported SVG bitmap leases failed during concurrent drawing or cache churn.",
                    failures.TryPeek(out var failure) ? failure : null);
            }

            Console.WriteLine("imported_svg_bitmap_concurrency_regression=ok");
        }
        finally
        {
            ImportedSvgRasterizer.ClearCache();
        }
    }
}
