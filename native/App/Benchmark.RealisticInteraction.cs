using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace VectorAnimationEngine;

/// <summary>
/// Interaction frame costs for day-to-day document sizes rather than a pathological
/// object count. Every tier keeps the same mixed artwork shape (rectangles, ellipses,
/// triangles, stars and lines spread over four layers) and a small amount of real
/// layer effects: one blurred line-art layer and one Multiply layer. Those effects
/// are intentional because they select the isolated layer-compositing path that a
/// normal document with a single filtered layer already uses.
/// </summary>
internal static partial class Benchmark
{
    private const double RealisticInteractionFrameBudgetMilliseconds = 1000d / 60d;
    private const int RealisticInteractionWarmupFrames = 6;
    private const int RealisticInteractionSampleFrames = 40;
    private const int RealisticInteractionPlaybackFrames = 24;
    private const float RealisticInteractionWorldWidth = 4_600f;

    private static readonly int[] RealisticInteractionObjectCounts = [50, 200, 500, 1_000];
    private static readonly Size[] RealisticInteractionViewports = [new(1920, 1080), new(3840, 2160)];

    internal static void RunRealisticInteractionBenchmark()
    {
        var previousGpuPolicy = Environment.GetEnvironmentVariable("VECTOR_DISABLE_EXPLICIT_GPU");
        try
        {
            Environment.SetEnvironmentVariable("VECTOR_DISABLE_EXPLICIT_GPU", null);
            if (Environment.GetEnvironmentVariable("VECTOR_BENCH_LIVE_PAN") == "1")
            {
                var available = RunLiveGdiPanCaptureCheck();
                Console.WriteLine(
                    $"realistic_interaction_live_pan={(available ? "ok" : "capture_unavailable")}");
                return;
            }
            if (Environment.GetEnvironmentVariable("VECTOR_BENCH_LIVE_ZOOM") == "1")
            {
                var available = RunLiveGdiZoomPreviewCaptureCheck();
                Console.WriteLine(
                    $"realistic_interaction_live_zoom={(available ? "ok" : "mismatch")}");
                return;
            }
            if (Environment.GetEnvironmentVariable("VECTOR_BENCH_REALISTIC_PARITY") == "1")
            {
                RunGdiCompositorReuseParityCheck();
                Console.WriteLine("realistic_interaction_parity=ok");
                return;
            }
            var counts = ReadRealisticInteractionCounts();
            var effects = Environment.GetEnvironmentVariable("VECTOR_BENCH_REALISTIC_EFFECTS")?.Trim().ToLowerInvariant()
                ?? "full";
            Console.WriteLine("realistic_interaction_budget_ms=" + RealisticInteractionFrameBudgetMilliseconds.ToString("0.###"));
            Console.WriteLine($"realistic_interaction_effects={effects}");
            foreach (var viewport in RealisticInteractionViewports)
            foreach (var objectCount in counts)
                RunRealisticInteractionTier(viewport, objectCount, effects);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VECTOR_DISABLE_EXPLICIT_GPU", previousGpuPolicy);
        }

        Console.WriteLine("realistic_interaction=ok");
    }

    private static int[] ReadRealisticInteractionCounts()
    {
        var requested = Environment.GetEnvironmentVariable("VECTOR_BENCH_REALISTIC_TIERS");
        if (string.IsNullOrWhiteSpace(requested)) return RealisticInteractionObjectCounts;
        var counts = requested
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => int.TryParse(value, out var count) && count > 0 ? count : -1)
            .Where(count => count > 0)
            .Distinct()
            .ToArray();
        return counts.Length > 0 ? counts : RealisticInteractionObjectCounts;
    }

    private static void RunRealisticInteractionTier(Size viewport, int objectCount, string effects)
    {
        var scene = CreateRealisticInteractionScene(objectCount, effects);
        using var host = new Form
        {
            AutoScaleMode = AutoScaleMode.None,
            FormBorderStyle = FormBorderStyle.None,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(0, 0),
            ClientSize = viewport
        };
        using var stage = new StageControl(scene) { Dock = DockStyle.Fill, WorldGridOpacity = 0 };
        host.Controls.Add(stage);
        host.Show();
        Application.DoEvents();
        stage.SetVisibleWorldWidth(RealisticInteractionWorldWidth);
        stage.Refresh();
        Application.DoEvents();

        var renderer = (Direct2DStageRenderer?)RequireField(typeof(StageControl), "_direct2DRenderer").GetValue(stage);
        var isolation = scene.RequiresIsolatedLayerCompositing;
        var label = $"{viewport.Width}x{viewport.Height}_{objectCount}";
        Console.WriteLine(
            $"realistic_interaction_tier={label},objects={scene.ObjectCount},layers={scene.LayerCount},"
            + $"effects={effects},isolated_compositing={isolation.ToString().ToLowerInvariant()},"
            + $"gpu={renderer?.ExplicitGpuDeviceActive.ToString().ToLowerInvariant() ?? "missing"},"
            + $"filter_padding={LayerFilterBounds.GetPadding(scene, stage.ActiveViewZoom)}");

        for (var warmup = 0; warmup < RealisticInteractionWarmupFrames; warmup++)
        {
            stage.Pan(warmup % 2 == 0 ? 3 : -3, 0);
            PaintStage(stage);
        }

        var pan = MeasureRealisticAction(stage, "pan", frame =>
            stage.Pan(frame % 2 == 0 ? 3 : -3, 0));
        WriteRealisticInteractionMetrics(label, pan);

        var zoomAnchor = new Point(viewport.Width / 2 + 40, viewport.Height / 2 - 20);
        for (var warmup = 0; warmup < RealisticInteractionWarmupFrames; warmup++)
        {
            stage.ZoomAt(zoomAnchor, warmup % 2 == 0 ? 1.02f : 1f / 1.02f, interactivePreview: true);
            PaintStage(stage);
        }

        var zoom = MeasureRealisticAction(stage, "zoom", frame =>
            stage.ZoomAt(zoomAnchor, frame % 2 == 0 ? 1.02f : 1f / 1.02f, interactivePreview: true));
        RequireMethod(typeof(StageControl), "EndZoomLodPreview").Invoke(stage, [false]);
        stage.SetVisibleWorldWidth(RealisticInteractionWorldWidth);
        stage.Refresh();
        WriteRealisticInteractionMetrics(label, zoom);

        var selectionCount = Math.Clamp(objectCount / 10, 3, 24);
        var owners = Enumerable.Range(0, selectionCount).ToArray();
        var hits = new List<DrawingElementHit>(selectionCount * 2);
        foreach (var owner in owners)
        {
            hits.Add(new DrawingElementHit(new DrawingElementKey(owner, DrawingElementKind.Fill, 0), 0, 0, 1));
            hits.Add(new DrawingElementHit(new DrawingElementKey(owner, DrawingElementKind.BoundaryStroke, 0), 0, 0, 1));
        }

        stage.SetSelectionState(owners, owners[^1], hits, hits[^1]);
        stage.Refresh();
        for (var warmup = 0; warmup < RealisticInteractionWarmupFrames; warmup++)
        {
            stage.SetSelectionDragPreviewOffset(new PointF(warmup % 2 == 0 ? 2 : -2, 0));
            PaintStage(stage);
        }

        var drag = MeasureRealisticAction(stage, "drag", frame =>
            stage.SetSelectionDragPreviewOffset(new PointF(frame % 2 == 0 ? 2 : -2, 0)));
        stage.ClearSelectionDragPreview();
        WriteRealisticInteractionMetrics(label, drag);

        stage.SetSelectionState([], -1, []);
        stage.Refresh();
        for (var warmup = 0; warmup < RealisticInteractionWarmupFrames; warmup++)
        {
            stage.Frame = (stage.Frame + 1) % RealisticInteractionPlaybackFrames;
            PaintStage(stage);
        }

        var playback = MeasureRealisticAction(stage, "playback", _ =>
            stage.Frame = (stage.Frame + 1) % RealisticInteractionPlaybackFrames);
        WriteRealisticInteractionMetrics(label, playback);

        host.Close();
    }

    private static RealisticInteractionMetrics MeasureRealisticAction(
        StageControl stage,
        string action,
        Action<int> update)
    {
        var samples = new double[RealisticInteractionSampleFrames];
        var paths = new Dictionary<string, int>(StringComparer.Ordinal);
        var compositorField = RequireField(typeof(StageControl), "_layerBlendCompositor");
        var compositor = compositorField.GetValue(stage);
        var compositorRebuilds = 0;
        var direct2D = true;
        var drawn = 0;
        var paintMilliseconds = 0d;
        var baseFrameCopyMilliseconds = 0d;
        var baseFrameBuilds = 0;
        var baseFrameReuses = 0;
        for (var frame = 0; frame < RealisticInteractionSampleFrames; frame++)
        {
            var started = Stopwatch.GetTimestamp();
            update(frame);
            PaintStage(stage);
            samples[frame] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            paintMilliseconds += stage.LastFrameRenderMilliseconds;
            baseFrameCopyMilliseconds += stage.LastGdiBaseFrameCopyMilliseconds;
            baseFrameBuilds += stage.LastGdiBaseFrameCacheBuilds;
            baseFrameReuses += stage.LastGdiBaseFrameCacheReuses;
            var currentCompositor = compositorField.GetValue(stage);
            if (!ReferenceEquals(currentCompositor, compositor))
            {
                compositorRebuilds++;
                compositor = currentCompositor;
            }

            var path = stage.LastFrameUsedDirect2D
                ? string.IsNullOrEmpty(stage.LastDirect2DSceneDrawPath) ? "direct2d" : stage.LastDirect2DSceneDrawPath
                : "gdi-composited";
            paths[path] = paths.GetValueOrDefault(path) + 1;
            direct2D &= stage.LastFrameUsedDirect2D;
            drawn += stage.LastStats.DrawnObjects;
        }

        Array.Sort(samples);
        var average = samples.Average();
        var p95 = samples[Math.Clamp((int)Math.Ceiling(samples.Length * 0.95) - 1, 0, samples.Length - 1)];
        return new RealisticInteractionMetrics(
            action,
            average,
            p95,
            samples[^1],
            1000d / Math.Max(0.001, average),
            paintMilliseconds / RealisticInteractionSampleFrames,
            baseFrameCopyMilliseconds / RealisticInteractionSampleFrames,
            baseFrameBuilds,
            baseFrameReuses,
            drawn / RealisticInteractionSampleFrames,
            string.Join("|", paths.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value}")),
            direct2D,
            compositorRebuilds,
            average <= RealisticInteractionFrameBudgetMilliseconds
                && p95 <= RealisticInteractionFrameBudgetMilliseconds);
    }

    private static void WriteRealisticInteractionMetrics(string tier, RealisticInteractionMetrics metrics)
    {
        Console.WriteLine(
            $"realistic_interaction_{tier}_{metrics.Action}:"
            + $"avg_ms={metrics.AverageMilliseconds:0.###},p95_ms={metrics.P95Milliseconds:0.###},"
            + $"max_ms={metrics.MaximumMilliseconds:0.###},fps={metrics.FramesPerSecond:0.##},"
            + $"paint_ms={metrics.PaintMilliseconds:0.###},gdi_base_copy_ms={metrics.BaseFrameCopyMilliseconds:0.###},"
            + $"gdi_base_builds={metrics.BaseFrameBuilds},gdi_base_reuses={metrics.BaseFrameReuses},"
            + $"drawn={metrics.DrawnObjects},paths={metrics.Paths},"
            + $"direct2d={metrics.Direct2D.ToString().ToLowerInvariant()},"
            + $"compositor_rebuilds={metrics.CompositorRebuilds},"
            + $"budget_met={metrics.BudgetMet.ToString().ToLowerInvariant()}");
    }

    private static void RunGdiCompositorReuseParityCheck()
    {
        var effects = Environment.GetEnvironmentVariable("VECTOR_BENCH_REALISTIC_EFFECTS")?.Trim().ToLowerInvariant()
            ?? "full";
        var scene = CreateRealisticInteractionScene(500, effects);
        var viewport = new Size(1920, 1080);
        using var host = new Form
        {
            AutoScaleMode = AutoScaleMode.None,
            FormBorderStyle = FormBorderStyle.None,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(0, 0),
            ClientSize = viewport
        };
        using var stage = new StageControl(scene) { Dock = DockStyle.Fill, WorldGridOpacity = 0 };
        host.Controls.Add(stage);
        host.Show();
        Application.DoEvents();
        stage.SetVisibleWorldWidth(RealisticInteractionWorldWidth * 2.6f);
        stage.Refresh();
        Application.DoEvents();

        var drawGdi = RequireMethod(typeof(StageControl), "DrawGdi", [typeof(Graphics)]);
        var compositorField = RequireField(typeof(StageControl), "_layerBlendCompositor");
        var underlayCompositorField = RequireField(typeof(StageControl), "_underlayLayerBlendCompositor");
        var initial = RenderStagePixels(stage, drawGdi, viewport);
        Console.WriteLine($"parity_base_zoom={stage.Zoom:0.####},hash={initial.Hash}");

        var anchor = new Point(viewport.Width / 2 + 40, viewport.Height / 2 - 20);
        for (var step = 0; step < 3; step++)
        {
            stage.ZoomAt(anchor, 2f);
            var reused = RenderStagePixels(stage, drawGdi, viewport);
            DropGdiCompositor(stage, compositorField, underlayCompositorField);
            var rebuilt = RenderStagePixels(stage, drawGdi, viewport);
            WriteCompositorReuseParity($"zoom={Math.Pow(2, step + 1):0}x", stage.Zoom, reused, rebuilt);
        }

        for (var step = 0; step < 3; step++)
        {
            stage.ZoomAt(anchor, 0.5f);
            var reused = RenderStagePixels(stage, drawGdi, viewport);
            DropGdiCompositor(stage, compositorField, underlayCompositorField);
            var rebuilt = RenderStagePixels(stage, drawGdi, viewport);
            WriteCompositorReuseParity($"zoom_out={Math.Pow(2, 2 - step):0}x", stage.Zoom, reused, rebuilt);
        }

        RunGdiBaseFramePanParityCheck(stage, viewport, drawGdi);
        RunGdiRasterZoomPreviewParityCheck(stage, viewport, drawGdi);
        host.Close();
    }

    private static void RunGdiRasterZoomPreviewParityCheck(
        StageControl stage,
        Size viewport,
        MethodInfo drawGdi)
    {
        var drawFrame = RequireMethod(typeof(StageControl), "DrawGdiFrame", [typeof(Graphics)]);
        var clearBaseFrame = RequireMethod(typeof(StageControl), "ClearGdiBaseFrameCache");
        var endZoomLodPreview = RequireMethod(typeof(StageControl), "EndZoomLodPreview");
        var beginRasterPreview = RequireMethod(typeof(StageControl), "BeginGdiRasterZoomPreview");
        var anchor = new Point(viewport.Width / 2 + 30, viewport.Height / 2 - 10);
        // Force a slow rebuild first so the frame-time gate enables the preview,
        // then use real paints because only OnPaint updates that gate.
        clearBaseFrame.Invoke(stage, null);
        PaintStage(stage);

        // At the base-frame camera the preview must be a pure 1:1 copy, so it has to
        // match the direct render exactly. A resampling offset would show up here.
        beginRasterPreview.Invoke(stage, null);
        var identityPreview = RenderStagePixels(stage, drawFrame, viewport);
        var identityDirect = RenderStagePixels(stage, drawGdi, viewport);
        var identityDiff = CompareStagePixels(identityPreview, identityDirect);
        endZoomLodPreview.Invoke(stage, [false]);
        Console.WriteLine(
            $"zoom_preview_identity:equal={identityDiff.Equal.ToString().ToLowerInvariant()},"
            + $"diff_pixels={identityDiff.DifferentPixels},diff_bounds={identityDiff.Bounds},"
            + $"identity_hash={identityPreview.Hash},direct_hash={identityDirect.Hash}");

        var previewFrames = 0;
        var previewBuilds = 0;
        for (var step = 0; step < 6; step++)
        {
            stage.ZoomAt(anchor, step % 2 == 0 ? 1.05f : 1f / 1.05f, interactivePreview: true);
            PaintStage(stage);
            previewFrames += stage.LastGdiBaseFrameZoomPreviews;
            previewBuilds += stage.LastGdiBaseFrameCacheBuilds;
        }

        endZoomLodPreview.Invoke(stage, [false]);
        clearBaseFrame.Invoke(stage, null);
        var previewFinal = RenderStagePixels(stage, drawFrame, viewport);
        var directFinal = RenderStagePixels(stage, drawGdi, viewport);
        var diff = CompareStagePixels(previewFinal, directFinal);
        Console.WriteLine(
            $"zoom_preview_preview_frames={previewFrames},preview_base_builds={previewBuilds},"
            + $"final_equal={diff.Equal.ToString().ToLowerInvariant()},diff_pixels={diff.DifferentPixels},"
            + $"diff_bounds={diff.Bounds},preview_final_hash={previewFinal.Hash},direct_final_hash={directFinal.Hash}");
    }

    private static void RunGdiBaseFramePanParityCheck(StageControl stage, Size viewport, MethodInfo drawGdi)
    {
        var drawFrame = RequireMethod(typeof(StageControl), "DrawGdiFrame", [typeof(Graphics)]);
        var clearBaseFrame = RequireMethod(typeof(StageControl), "ClearGdiBaseFrameCache");
        RenderStagePixels(stage, drawFrame, viewport);

        // The enlarged, composed base frame must be identical to a direct uncached
        // render of the same camera, which is the pre-optimization output.
        var direct = RenderStagePixels(stage, drawGdi, viewport);
        clearBaseFrame.Invoke(stage, null);
        var directCached = RenderStagePixels(stage, drawFrame, viewport);
        WriteBaseFramePanParity(
            "direct_vs_cached",
            stage.LastGdiBaseFrameCacheBuilds,
            stage.LastGdiBaseFrameCacheReuses,
            direct,
            directCached);

        foreach (var (dx, dy) in new[] { (3, 0), (0, 3), (7, -5), (24, 0), (48, 12) })
        {
            stage.Pan(dx, dy);
            var reused = RenderStagePixels(stage, drawFrame, viewport);
            var reuseBuilds = stage.LastGdiBaseFrameCacheBuilds;
            var reuseCount = stage.LastGdiBaseFrameCacheReuses;
            clearBaseFrame.Invoke(stage, null);
            var rebuilt = RenderStagePixels(stage, drawFrame, viewport);
            WriteBaseFramePanParity($"pan={dx},{dy}", reuseBuilds, reuseCount, reused, rebuilt);
        }

        // Cumulative pans stay inside the cached margin and must keep sampling.
        clearBaseFrame.Invoke(stage, null);
        RenderStagePixels(stage, drawFrame, viewport);
        StagePixelSnapshot cumulative = default;
        var cumulativeBuilds = 0;
        var cumulativeReuses = 0;
        for (var frame = 0; frame < 6; frame++)
        {
            stage.Pan(3, 3);
            cumulative = RenderStagePixels(stage, drawFrame, viewport);
            cumulativeBuilds += stage.LastGdiBaseFrameCacheBuilds;
            cumulativeReuses += stage.LastGdiBaseFrameCacheReuses;
        }
        clearBaseFrame.Invoke(stage, null);
        var cumulativeRebuilt = RenderStagePixels(stage, drawFrame, viewport);
        WriteBaseFramePanParity(
            "pan_cumulative=18,18",
            cumulativeBuilds,
            cumulativeReuses,
            cumulative,
            cumulativeRebuilt);

        // Beyond the margin a rebuild is expected and the result must still match.
        stage.Pan(400, 0);
        var beyondReuse = RenderStagePixels(stage, drawFrame, viewport);
        var beyondBuilds = stage.LastGdiBaseFrameCacheBuilds;
        var beyondReuses = stage.LastGdiBaseFrameCacheReuses;
        clearBaseFrame.Invoke(stage, null);
        var beyondRebuilt = RenderStagePixels(stage, drawFrame, viewport);
        WriteBaseFramePanParity("pan=400,0", beyondBuilds, beyondReuses, beyondReuse, beyondRebuilt);

        // Sub-pixel pans cannot be reproduced by an integer raster shift.
        stage.Pan(0.5f, 0);
        var fractionalReuse = RenderStagePixels(stage, drawFrame, viewport);
        var fractionalBuilds = stage.LastGdiBaseFrameCacheBuilds;
        var fractionalReuses = stage.LastGdiBaseFrameCacheReuses;
        clearBaseFrame.Invoke(stage, null);
        var fractionalRebuilt = RenderStagePixels(stage, drawFrame, viewport);
        WriteBaseFramePanParity("pan=0.5,0", fractionalBuilds, fractionalReuses, fractionalReuse, fractionalRebuilt);
    }

    private static void WriteBaseFramePanParity(
        string label,
        int builds,
        int reuses,
        StagePixelSnapshot reused,
        StagePixelSnapshot rebuilt)
    {
        var diff = CompareStagePixels(reused, rebuilt);
        Console.WriteLine(
            $"base_frame_{label}:base_builds={builds},base_reuses={reuses},"
            + $"equal={diff.Equal.ToString().ToLowerInvariant()},diff_pixels={diff.DifferentPixels},"
            + $"diff_bounds={diff.Bounds},reuse_hash={reused.Hash},rebuild_hash={rebuilt.Hash}");
    }

    /// <summary>
    /// Drives continuous pans in a real, presented window and compares every captured
    /// frame with a forced direct render of the same camera state. This is the automated
    /// stand-in for dragging the Stage by hand: a seam at the cached margin, a jump on the
    /// rebuild frame or a flashed frame all surface as a non-zero pixel difference.
    /// Presentation tearing cannot be judged from completed frames and is not covered.
    /// </summary>
    private static bool RunLiveGdiPanCaptureCheck()
    {
        var viewport = new Size(1600, 900);
        var scene = CreateRealisticInteractionScene(500, "full");
        using var host = new Form
        {
            AutoScaleMode = AutoScaleMode.None,
            FormBorderStyle = FormBorderStyle.None,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(0, 0),
            ClientSize = viewport,
            TopMost = true
        };
        using var stage = new StageControl(scene) { Dock = DockStyle.Fill, WorldGridOpacity = 0 };
        host.Controls.Add(stage);
        host.Show();
        PositionWorkspaceCaptureWindow(host.Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0043);
        host.Activate();
        host.BringToFront();
        Application.DoEvents();
        stage.SetVisibleWorldWidth(RealisticInteractionWorldWidth);
        stage.Refresh();
        Application.DoEvents();

        var drawGdi = RequireMethod(typeof(StageControl), "DrawGdi", [typeof(Graphics)]);
        var endZoomLodPreview = RequireMethod(typeof(StageControl), "EndZoomLodPreview");
        var overscanField = RequireField(typeof(StageControl), "_gdiBaseFrameOverscan");

        Bitmap CapturePresentedBitmap(out int baseBuilds, out int baseReuses)
        {
            ShowWorkspaceCaptureWindow(host.Handle, 5);
            PositionWorkspaceCaptureWindow(host.Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0043);
            host.Activate();
            host.BringToFront();
            stage.Refresh();
            // Read immediately: a later paint in the same state would reset telemetry to reuse.
            baseBuilds = stage.LastGdiBaseFrameCacheBuilds;
            baseReuses = stage.LastGdiBaseFrameCacheReuses;
            Application.DoEvents();
            FlushWorkspaceCapture();
            System.Threading.Thread.Sleep(20);
            Application.DoEvents();
            // A foreground full-screen launcher owns this desktop, so CopyFromScreen only
            // shows that application. PrintWindow asks this window to render itself through
            // the same WM_PAINT/OnPaint path without depending on desktop visibility.
            using var captured = CaptureBenchmarkWindow(host);
            var screen = new Bitmap(captured.Width, captured.Height, PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(screen))
            {
                graphics.DrawImageUnscaled(captured, 0, 0);
            }
            return screen;
        }

        StagePixelSnapshot CapturePresented(out int baseBuilds, out int baseReuses)
        {
            using var bitmap = CapturePresentedBitmap(out baseBuilds, out baseReuses);
            return SnapshotBitmap(bitmap, bitmap.Size);
        }

        stage.Refresh();
        Application.DoEvents();
        using var baselineCapture = CapturePresentedBitmap(out _, out _);
        var baselineReference = RenderStagePixels(stage, drawGdi, viewport);
        var baseline = CompareStagePixels(
            SnapshotBitmap(baselineCapture, baselineCapture.Size), baselineReference);
        var overscan = (int)overscanField.GetValue(stage)!;
        Console.WriteLine(
            $"live_pan_fixture=objects:{scene.ObjectCount},isolated_compositing:"
            + $"{scene.RequiresIsolatedLayerCompositing.ToString().ToLowerInvariant()},"
            + $"gdi_path:{(!stage.LastFrameUsedDirect2D).ToString().ToLowerInvariant()},overscan={overscan}");
        if (!baseline.Equal
            && (baseline.Bounds == "size"
                || baseline.DifferentPixels > viewport.Width * viewport.Height * 0.25))
        {
            var directory = Path.Combine(Environment.CurrentDirectory, "artifacts", "validation");
            Directory.CreateDirectory(directory);
            baselineCapture.Save(Path.Combine(directory, "live-pan-capture.png"));
            using (var referenceBitmap = BitmapFromSnapshot(baselineReference))
                referenceBitmap.Save(Path.Combine(directory, "live-pan-reference.png"));
            Console.WriteLine(
                $"live_pan_capture=unavailable,baseline_diff_pixels={baseline.DifferentPixels},"
                + $"baseline_diff_bounds={baseline.Bounds},capture={baselineCapture.Size},stage={stage.ClientSize}"
                + $",screen={stage.RectangleToScreen(stage.ClientRectangle)}");
            host.Close();
            return false;
        }

        Console.WriteLine(
            $"live_pan_capture=available,baseline_diff_pixels={baseline.DifferentPixels},"
            + $"baseline_diff_bounds={baseline.Bounds}");
        var mismatches = new List<string>();
        int Probe(string phase, int frame)
        {
            var presented = CapturePresented(out var builds, out var reuses);
            var reference = RenderStagePixels(stage, drawGdi, viewport);
            var diff = CompareStagePixels(presented, reference);
            if (!diff.Equal)
            {
                mismatches.Add(
                    $"{phase}#{frame}:diff_pixels={diff.DifferentPixels},bounds={diff.Bounds},"
                    + $"builds={builds},reuses={reuses}");
            }
            return builds;
        }

        string Summary(string phase, int frames, IReadOnlyList<int> rebuildFrames) =>
            $"live_pan_phase={phase},frames={frames},rebuild_frames=[{string.Join(',', rebuildFrames)}],"
            + $"mismatches={mismatches.Count(entry => entry.StartsWith(phase + "#", StringComparison.Ordinal))}";

        var slowBuildFrames = new List<int>();
        for (var frame = 0; frame < 30; frame++)
        {
            stage.Pan(3, 0);
            if (Probe("slow_pan", frame) > 0) slowBuildFrames.Add(frame);
        }
        Console.WriteLine(Summary("slow_pan", 30, slowBuildFrames));

        var flingBuildFrames = new List<int>();
        for (var frame = 0; frame < 12; frame++)
        {
            stage.Pan(40, 0);
            if (Probe("fling", frame) > 0) flingBuildFrames.Add(frame);
        }
        Console.WriteLine(Summary("fling", 12, flingBuildFrames));

        var zoomAnchor = new Point(viewport.Width / 2 + 40, viewport.Height / 2 - 20);
        var previewFrames = 0;
        for (var step = 0; step < 5; step++)
        {
            stage.ZoomAt(zoomAnchor, step % 2 == 0 ? 1.06f : 1f / 1.06f, interactivePreview: true);
            stage.Refresh();
            Application.DoEvents();
            previewFrames += stage.LastGdiBaseFrameZoomPreviews;
        }
        endZoomLodPreview.Invoke(stage, [true]);
        var settledBuild = Probe("zoom_settled", 0);
        Console.WriteLine(Summary("zoom_settled", 1, [settledBuild]));

        var afterZoomBuildFrames = new List<int>();
        for (var frame = 0; frame < 12; frame++)
        {
            stage.Pan(3, 0);
            if (Probe("pan_after_zoom", frame) > 0) afterZoomBuildFrames.Add(frame);
        }
        Console.WriteLine(Summary("pan_after_zoom", 12, afterZoomBuildFrames));
        Console.WriteLine($"live_pan_zoom_preview_frames={previewFrames}");

        foreach (var mismatch in mismatches.Take(10)) Console.WriteLine($"live_pan_mismatch={mismatch}");
        host.Close();
        return true;
    }

    /// <summary>
    /// Drives interactive zoom in a real, presented window. Every preview capture must match the
    /// offscreen preview composition exactly (the user sees what the preview path computes) and
    /// the settled capture must match the direct render exactly. The content mask centroid and box
    /// of the preview and settled captures must agree within a pixel, so the preview-to-final
    /// switch cannot show a geometric jump. Brush/eraser overlay previews are painted here too and
    /// must never engage the raster zoom preview.
    /// </summary>
    private static bool RunLiveGdiZoomPreviewCaptureCheck()
    {
        var viewport = new Size(1600, 900);
        var scene = CreateRealisticInteractionScene(500, "full");
        using var host = new Form
        {
            AutoScaleMode = AutoScaleMode.None,
            FormBorderStyle = FormBorderStyle.None,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(0, 0),
            ClientSize = viewport,
            TopMost = true
        };
        using var stage = new StageControl(scene) { Dock = DockStyle.Fill, WorldGridOpacity = 0 };
        host.Controls.Add(stage);
        host.Show();
        PositionWorkspaceCaptureWindow(host.Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0043);
        host.Activate();
        host.BringToFront();
        Application.DoEvents();
        stage.SetVisibleWorldWidth(RealisticInteractionWorldWidth);
        stage.Refresh();
        Application.DoEvents();

        var drawGdi = RequireMethod(typeof(StageControl), "DrawGdi", [typeof(Graphics)]);
        var drawGdiFrame = RequireMethod(typeof(StageControl), "DrawGdiFrame");
        var beginRasterPreview = RequireMethod(typeof(StageControl), "BeginGdiRasterZoomPreview");
        var endRasterPreview = RequireMethod(typeof(StageControl), "EndGdiRasterZoomPreview");
        var previewActive = RequireField(typeof(StageControl), "_gdiRasterZoomPreviewActive");

        Bitmap Capture()
        {
            ShowWorkspaceCaptureWindow(host.Handle, 5);
            PositionWorkspaceCaptureWindow(host.Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0043);
            host.Activate();
            host.BringToFront();
            stage.Refresh();
            Application.DoEvents();
            FlushWorkspaceCapture();
            System.Threading.Thread.Sleep(20);
            Application.DoEvents();
            using var captured = CaptureBenchmarkWindow(host);
            var screen = new Bitmap(captured.Width, captured.Height, PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(screen))
            {
                graphics.DrawImageUnscaled(captured, 0, 0);
            }

            return screen;
        }

        using var baselineCapture = Capture();
        var baselineDiff = CompareStagePixels(
            SnapshotBitmap(baselineCapture, baselineCapture.Size),
            RenderStagePixels(stage, drawGdi, viewport));
        if (!baselineDiff.Equal && baselineDiff.DifferentPixels > viewport.Width * viewport.Height * 0.25)
        {
            Console.WriteLine(
                $"live_zoom_capture=unavailable,baseline_diff_pixels={baselineDiff.DifferentPixels},"
                + $"baseline_diff_bounds={baselineDiff.Bounds}");
            host.Close();
            return false;
        }

        // Store a base frame at the current camera, then let interactive zoom previews scale it.
        stage.Refresh();
        Application.DoEvents();
        beginRasterPreview.Invoke(stage, null);
        var zoomAnchor = new Point(viewport.Width / 2 + 40, viewport.Height / 2 - 20);
        var previewFrames = 0;
        for (var step = 0; step < 4; step++)
        {
            stage.ZoomAt(zoomAnchor, 1.03f, interactivePreview: true);
            stage.Refresh();
            Application.DoEvents();
            previewFrames += stage.LastGdiBaseFrameZoomPreviews;
        }

        using var previewCapture = Capture();
        var previewSnapshot = SnapshotBitmap(previewCapture, previewCapture.Size);
        var previewFrameActive = (bool)previewActive.GetValue(stage)!;
        var previewReference = RenderStagePixels(stage, drawGdiFrame, viewport);
        var previewDiff = CompareStagePixels(previewSnapshot, previewReference);

        endRasterPreview.Invoke(stage, null);
        using var settledCapture = Capture();
        var settledSnapshot = SnapshotBitmap(settledCapture, settledCapture.Size);
        var settledDiff = CompareStagePixels(
            settledSnapshot,
            RenderStagePixels(stage, drawGdi, viewport));

        var previewContent = ContentStats(previewSnapshot);
        var settledContent = ContentStats(settledSnapshot);
        var centroidDelta = Math.Sqrt(
            Math.Pow(previewContent.CenterX - settledContent.CenterX, 2)
            + Math.Pow(previewContent.CenterY - settledContent.CenterY, 2));
        var boxDelta = Math.Max(
            Math.Max(Math.Abs(previewContent.Left - settledContent.Left), Math.Abs(previewContent.Top - settledContent.Top)),
            Math.Max(Math.Abs(previewContent.Right - settledContent.Right), Math.Abs(previewContent.Bottom - settledContent.Bottom)));

        // Brush/eraser overlay previews share SetFreehandPreview; they must stay out of the zoom preview.
        var brushPoints = new List<PointF>();
        var brushDiameters = new List<float>();
        for (var index = 0; index <= 48; index++)
        {
            brushPoints.Add(new PointF(280 + index * 18, 360 + MathF.Sin(index * 0.35f) * 60));
            brushDiameters.Add(VectorUnits.StrokePointsToUnits(4 + index % 9));
        }

        stage.SetFreehandPreview(brushPoints, Color.OrangeRed, VectorUnits.StrokePointsToUnits(4), brushDiameters);
        stage.Refresh();
        Application.DoEvents();
        var brushPreviewActive = (bool)previewActive.GetValue(stage)!;
        var brushZoomPreviews = stage.LastGdiBaseFrameZoomPreviews;
        var brushBaseBuilds = stage.LastGdiBaseFrameCacheBuilds;
        stage.ClearFreehandPreview();
        stage.Refresh();
        Application.DoEvents();

        var passed = previewFrameActive
            && previewFrames > 0
            && previewDiff.Equal
            && settledDiff.Equal
            && centroidDelta <= 0.5
            && boxDelta <= 2
            && !brushPreviewActive
            && brushZoomPreviews == 0;
        Console.WriteLine(
            $"live_zoom_preview={(passed ? "ok" : "mismatch")},"
            + $"preview_frames={previewFrames},preview_active={previewFrameActive.ToString().ToLowerInvariant()},"
            + $"preview_live_equal={previewDiff.Equal.ToString().ToLowerInvariant()},"
            + $"preview_live_diff_pixels={previewDiff.DifferentPixels},"
            + $"settled_live_equal={settledDiff.Equal.ToString().ToLowerInvariant()},"
            + $"settled_live_diff_pixels={settledDiff.DifferentPixels},"
            + $"centroid_delta_px={centroidDelta:0.###},box_delta_px={boxDelta},"
            + $"preview_box={previewContent.Box},settled_box={settledContent.Box},"
            + $"brush_preview_active={brushPreviewActive.ToString().ToLowerInvariant()},"
            + $"brush_zoom_previews={brushZoomPreviews},brush_base_builds={brushBaseBuilds}");
        if (!previewDiff.Equal)
        {
            var directory = Path.Combine(Environment.CurrentDirectory, "artifacts", "validation");
            Directory.CreateDirectory(directory);
            previewCapture.Save(Path.Combine(directory, "live-zoom-preview.png"));
            using var reference = BitmapFromSnapshot(previewReference);
            reference.Save(Path.Combine(directory, "live-zoom-preview-reference.png"));
        }

        if (!settledDiff.Equal)
        {
            var directory = Path.Combine(Environment.CurrentDirectory, "artifacts", "validation");
            Directory.CreateDirectory(directory);
            settledCapture.Save(Path.Combine(directory, "live-zoom-settled.png"));
        }

        host.Close();
        return passed;
    }

    private static StageContentStats ContentStats(StagePixelSnapshot snapshot)
    {
        var width = snapshot.Stride / 4;
        var backgroundOffset = 0;
        long count = 0;
        double sumX = 0;
        double sumY = 0;
        var left = width;
        var top = snapshot.Height;
        var right = -1;
        var bottom = -1;
        for (var y = 0; y < snapshot.Height; y++)
        {
            var row = y * snapshot.Stride;
            for (var x = 0; x < width; x++)
            {
                var offset = row + x * 4;
                if (Math.Abs(snapshot.Pixels[offset] - snapshot.Pixels[backgroundOffset]) <= 8
                    && Math.Abs(snapshot.Pixels[offset + 1] - snapshot.Pixels[backgroundOffset + 1]) <= 8
                    && Math.Abs(snapshot.Pixels[offset + 2] - snapshot.Pixels[backgroundOffset + 2]) <= 8)
                {
                    continue;
                }

                count++;
                sumX += x;
                sumY += y;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }

        return count == 0
            ? new StageContentStats(0, 0, 0, "empty")
            : new StageContentStats(
                count,
                sumX / count,
                sumY / count,
                $"{left},{top}-{right},{bottom}");
    }

    private readonly record struct StageContentStats(
        long Pixels,
        double CenterX,
        double CenterY,
        string Box)
    {
        public int Left => ParseBox(0);
        public int Top => ParseBox(1);
        public int Right => ParseBox(2);
        public int Bottom => ParseBox(3);

        private int ParseBox(int index)
        {
            if (Box == "empty") return 0;
            var parts = Box.Split('-');
            var coordinates = parts[0].Split(',');
            return index switch
            {
                0 => int.Parse(coordinates[0]),
                1 => int.Parse(coordinates[1]),
                2 => int.Parse(parts[1].Split(',')[0]),
                _ => int.Parse(parts[1].Split(',')[1])
            };
        }
    }

    private static StagePixelSnapshot SnapshotBitmap(Bitmap bitmap, Size size)
    {
        var data = bitmap.LockBits(
            new Rectangle(Point.Empty, size),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppPArgb);
        try
        {
            var stride = Math.Abs(data.Stride);
            var pixels = new byte[stride * size.Height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            return new StagePixelSnapshot(pixels, stride, size.Height, Convert.ToHexString(SHA256.HashData(pixels)));
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static Bitmap BitmapFromSnapshot(StagePixelSnapshot snapshot)
    {
        var width = snapshot.Stride / 4;
        var bitmap = new Bitmap(width, snapshot.Height, PixelFormat.Format32bppPArgb);
        var data = bitmap.LockBits(
            new Rectangle(Point.Empty, new Size(width, snapshot.Height)),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppPArgb);
        try
        {
            Marshal.Copy(snapshot.Pixels, 0, data.Scan0, snapshot.Pixels.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return bitmap;
    }

    private static void DropGdiCompositor(StageControl stage, params FieldInfo[] fields)
    {
        foreach (var field in fields)
        {
            if (field.GetValue(stage) is IDisposable disposable) disposable.Dispose();
            field.SetValue(stage, null);
        }
    }

    private static void WriteCompositorReuseParity(
        string label,
        float zoom,
        StagePixelSnapshot reused,
        StagePixelSnapshot rebuilt)
    {
        var diff = CompareStagePixels(reused, rebuilt);
        Console.WriteLine(
            $"parity_{label}:zoom={zoom:0.####},equal={diff.Equal.ToString().ToLowerInvariant()},"
            + $"diff_pixels={diff.DifferentPixels},diff_bounds={diff.Bounds},"
            + $"reuse_hash={reused.Hash},rebuild_hash={rebuilt.Hash}");
    }

    private static StagePixelSnapshot RenderStagePixels(StageControl stage, MethodInfo drawGdi, Size viewport)
    {
        using var bitmap = new Bitmap(viewport.Width, viewport.Height, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            drawGdi.Invoke(stage, [graphics]);
            graphics.Flush(FlushIntention.Sync);
        }

        var data = bitmap.LockBits(
            new Rectangle(Point.Empty, viewport),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppPArgb);
        try
        {
            var stride = Math.Abs(data.Stride);
            var pixels = new byte[stride * viewport.Height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            return new StagePixelSnapshot(pixels, stride, viewport.Height, Convert.ToHexString(SHA256.HashData(pixels)));
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static StagePixelDiff CompareStagePixels(StagePixelSnapshot left, StagePixelSnapshot right)
    {
        if (left.Stride != right.Stride || left.Height != right.Height) return new StagePixelDiff(false, -1, "size");
        var different = 0;
        var minimumX = int.MaxValue;
        var minimumY = int.MaxValue;
        var maximumX = -1;
        var maximumY = -1;
        var width = left.Stride / 4;
        for (var y = 0; y < left.Height; y++)
        {
            var row = y * left.Stride;
            for (var x = 0; x < width; x++)
            {
                var offset = row + x * 4;
                if (left.Pixels[offset] == right.Pixels[offset]
                    && left.Pixels[offset + 1] == right.Pixels[offset + 1]
                    && left.Pixels[offset + 2] == right.Pixels[offset + 2]
                    && left.Pixels[offset + 3] == right.Pixels[offset + 3])
                {
                    continue;
                }

                different++;
                minimumX = Math.Min(minimumX, x);
                minimumY = Math.Min(minimumY, y);
                maximumX = Math.Max(maximumX, x);
                maximumY = Math.Max(maximumY, y);
            }
        }

        return different == 0
            ? new StagePixelDiff(true, 0, "none")
            : new StagePixelDiff(false, different, $"{minimumX},{minimumY}-{maximumX},{maximumY}");
    }

    private readonly record struct StagePixelSnapshot(byte[] Pixels, int Stride, int Height, string Hash);

    private readonly record struct StagePixelDiff(bool Equal, int DifferentPixels, string Bounds);

    private static VectorScene CreateRealisticInteractionScene(int objectCount, string effectsMode)
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        var background = scene.AddLayer("Background");
        var midground = scene.AddLayer("Midground");
        var lineArt = scene.AddLayer("Line Art");
        var effects = scene.AddLayer("Effects");
        var layers = new[] { background, midground, lineArt, effects };
        var shapeKinds = new[]
        {
            ShapeKind.Rectangle, ShapeKind.Ellipse, ShapeKind.Triangle,
            ShapeKind.Star, ShapeKind.Line, ShapeKind.Polygon
        };
        var colors = new[]
        {
            Color.FromArgb(255, 84, 132, 196), Color.FromArgb(255, 196, 142, 84),
            Color.FromArgb(255, 118, 176, 112), Color.FromArgb(255, 168, 108, 156),
            Color.FromArgb(255, 92, 158, 168), Color.FromArgb(255, 176, 176, 96)
        };

        var columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(objectCount * 1.6)));
        var rows = Math.Max(1, (objectCount + columns - 1) / columns);
        var cellWidth = 3_600f / columns;
        var cellHeight = 2_100f / rows;
        var objectSize = Math.Max(12f, Math.Min(cellWidth, cellHeight) * 0.5f);
        var effectCount = Math.Max(2, objectCount / 50);

        scene.BeginDeferredAppend(objectCount, layers);
        try
        {
            for (var index = 0; index < objectCount; index++)
            {
                var layer = index >= objectCount - effectCount
                    ? effects
                    : index % 10 == 9 ? lineArt
                    : index % 3 == 2 ? midground
                    : background;
                var column = index % columns;
                var row = index / columns;
                var location = new PointF(
                    -1_800f + (column + 0.5f) * cellWidth,
                    -1_050f + (row + 0.5f) * cellHeight);
                var isLine = index % 7 == 5;
                var kind = isLine ? ShapeKind.Line : shapeKinds[index % shapeKinds.Length];
                scene.AppendObject(
                    layer,
                    location,
                    isLine ? new SizeF(objectSize * 1.6f, 1f) : new SizeF(objectSize, objectSize),
                    angle: index % 5 == 0 ? 0.12f : 0f,
                    stroke: isLine ? VectorUnits.StrokePointsToUnits(1.5f) : 0f,
                    color: isLine ? Color.Transparent : colors[index % colors.Length],
                    strokeColor: isLine ? colors[index % colors.Length] : Color.FromArgb(255, 36, 42, 52),
                    atoms: (uint)(kind is ShapeKind.Star or ShapeKind.Polygon ? 10 : 4),
                    shapeKind: kind);
            }
        }
        finally
        {
            scene.EndDeferredAppend();
        }

        scene.CompleteDeferredBuild();
        // One blurred line-art layer and one Multiply layer: exactly the amount of real
        // layer effects that makes the Stage compose through isolated layer surfaces.
        var includeBlur = effectsMode is "full" or "blur";
        var includeBlend = effectsMode is "full" or "blend";
        if (includeBlur)
        {
            scene.SetLayerSymbolFilters(
                lineArt,
                new SymbolFilters { Blur = SymbolBlurFilter.Default with { BlurX = 4, BlurY = 4 } });
        }
        if (includeBlend) scene.LayerBlendModes[effects] = LayerBlendMode.Multiply;
        return scene;
    }

    private readonly record struct RealisticInteractionMetrics(
        string Action,
        double AverageMilliseconds,
        double P95Milliseconds,
        double MaximumMilliseconds,
        double FramesPerSecond,
        double PaintMilliseconds,
        double BaseFrameCopyMilliseconds,
        int BaseFrameBuilds,
        int BaseFrameReuses,
        int DrawnObjects,
        string Paths,
        bool Direct2D,
        int CompositorRebuilds,
        bool BudgetMet);
}
