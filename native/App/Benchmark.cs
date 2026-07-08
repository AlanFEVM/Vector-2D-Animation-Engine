using System.Diagnostics;

namespace VectorAnimationEngine;

internal static class Benchmark
{
    public static void RunDefaultStress()
    {
        var scene = new VectorScene();
        var generate = Stopwatch.StartNew();
        scene.Generate(1000, 100000, 100000000);
        generate.Stop();

        var viewports = new[]
        {
            new RectangleF(-24000, -14000, 48000, 28000),
            new RectangleF(-7000, -4100, 14000, 8200),
            new RectangleF(-2500, -1500, 5000, 3000),
            new RectangleF(-900, -540, 1800, 1080)
        };

        var scanWatch = Stopwatch.StartNew();
        long scanned = 0;
        long cells = 0;
        for (var iteration = 0; iteration < 200; iteration++)
        {
            foreach (var viewport in viewports)
            {
                scene.GetIndexRange(viewport, out var minX, out var maxX, out var minY, out var maxY);
                for (var y = minY; y <= maxY; y++)
                {
                    for (var x = minX; x <= maxX; x++)
                    {
                        var cell = scene.CellIndex(x, y);
                        scanned += scene.CellStart[cell + 1] - scene.CellStart[cell];
                        cells++;
                    }
                }
            }
        }
        scanWatch.Stop();

        Console.WriteLine($"generate_ms={generate.Elapsed.TotalMilliseconds:0.0}");
        Console.WriteLine($"objects={scene.ObjectCount}");
        Console.WriteLine($"avg_scanned_objects={scanned / (double)(viewports.Length * 200):0.0}");
        Console.WriteLine($"avg_cells={cells / (double)(viewports.Length * 200):0.0}");
        Console.WriteLine($"overview_tiles={scene.OverviewCount.Count(count => count > 0)}");
        Console.WriteLine($"detail_tiles={scene.TileCount.Count(count => count > 0)}");
        Console.WriteLine($"query_ms={scanWatch.Elapsed.TotalMilliseconds:0.0}");
    }
}
