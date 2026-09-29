using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private const int ScenePerformanceLineUnits = 56_000;
    private const int ScenePerformanceRectangleUnits = 56_000;
    private const int ScenePerformanceTotalUnits =
        ScenePerformanceLineUnits + ScenePerformanceRectangleUnits;
    // The sample must measure steady-state presentation. A shorter warmup keeps
    // the GPU in its low-power state and the driver pipelines cold, which showed
    // up as ~4x slower frames for the first ~13 samples even though every frame
    // submitted identical work at identical revisions.
    private const int ScenePerformanceWarmupFrames = 40;
    private const int ScenePerformanceSampleFrames = 30;
    private const double ScenePerformanceFrameBudgetMilliseconds = 1000d / 60d;

    internal static void RunScenePerformanceBenchmark()
    {
        var previousGpuSetting = Environment.GetEnvironmentVariable("VECTOR_DISABLE_EXPLICIT_GPU");
        Environment.SetEnvironmentVariable("VECTOR_DISABLE_EXPLICIT_GPU", null);
        try
        {
            var scene = CreateScenePerformanceFixture();
            if (scene.ObjectCount != ScenePerformanceTotalUnits)
            {
                throw new InvalidOperationException(
                    $"The scene-performance fixture created {scene.ObjectCount} objects instead of "
                    + $"{ScenePerformanceTotalUnits}.");
            }

            using var form = new Form
            {
                AutoScaleMode = AutoScaleMode.None,
                FormBorderStyle = FormBorderStyle.None,
                ShowInTaskbar = false,
                TopMost = true,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(Screen.PrimaryScreen!.WorkingArea.Left, Screen.PrimaryScreen.WorkingArea.Top),
                ClientSize = new Size(1920, 1080),
                BackColor = Color.FromArgb(255, 12, 14, 18)
            };
            using var stage = new StageControl(scene)
            {
                Dock = DockStyle.Fill,
                BackColor = form.BackColor,
                WorldGridOpacity = 0
            };
            form.Controls.Add(stage);
            form.Show();
            form.Activate();
            form.BringToFront();
            Application.DoEvents();

            var renderer = (Direct2DStageRenderer?)RequireField(
                typeof(StageControl),
                "_direct2DRenderer").GetValue(stage);
            if (renderer is null)
            {
                throw new InvalidOperationException("The scene-performance Stage renderer was not initialized.");
            }

            stage.ConfigureReferenceView(null, SceneDimension.TwoD, ReferenceCameraMotion.Immediate);
            stage.SetVisibleWorldWidth(4_000);
            stage.Invalidate();
            stage.Update();
            Application.DoEvents();
            if (!renderer.ExplicitGpuDeviceActive
                || !stage.LastFrameUsedDirect2D
                || !stage.GpuAccelerationActive)
            {
                Console.WriteLine("scene_performance_hardware=unavailable");
                Console.WriteLine(
                    $"scene_performance_gpu_active={renderer.ExplicitGpuDeviceActive.ToString().ToLowerInvariant()}");
                Console.WriteLine("scene_performance_budget_met=false");
                throw new InvalidOperationException("The 60 FPS benchmark requires the explicit hardware GPU.");
            }

            Console.WriteLine($"scene_performance_units={ScenePerformanceTotalUnits}");
            var dimension = Environment.GetEnvironmentVariable("VECTOR_BENCH_SCENE_DIMENSION");
            if (string.Equals(dimension, "2d", StringComparison.OrdinalIgnoreCase))
            {
                var metrics = MeasureTwoD(stage);
                WriteScenePerformanceMetrics("2d", metrics);
                AssertScenePerformanceBatch2DParity(stage);
                RequireScenePerformanceBudget(metrics);
                return;
            }
            if (string.Equals(dimension, "3d", StringComparison.OrdinalIgnoreCase))
            {
                var metrics = MeasureThreeD(stage);
                WriteScenePerformanceMetrics("3d", metrics);
                RequireScenePerformanceBudget(metrics);
                return;
            }
            var twoD = MeasureTwoD(stage);
            WriteScenePerformanceMetrics("2d", twoD);
            var threeD = MeasureThreeD(stage);
            WriteScenePerformanceMetrics("3d", threeD);
            Console.WriteLine($"scene_performance_units={ScenePerformanceTotalUnits}");
            Console.WriteLine($"scene_performance_line_units={ScenePerformanceLineUnits}");
            Console.WriteLine($"scene_performance_rectangle_units={ScenePerformanceRectangleUnits}");
            Console.WriteLine($"scene_performance_2d_gpu={twoD.UsedExplicitGpu.ToString().ToLowerInvariant()}");
            Console.WriteLine($"scene_performance_3d_gpu={threeD.UsedExplicitGpu.ToString().ToLowerInvariant()}");
            Console.WriteLine(
                $"scene_performance_budget_met={(twoD.BudgetMet && threeD.BudgetMet).ToString().ToLowerInvariant()}");
            RequireScenePerformanceBudget(twoD);
            RequireScenePerformanceBudget(threeD);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VECTOR_DISABLE_EXPLICIT_GPU", previousGpuSetting);
        }
    }

    private static VectorScene CreateScenePerformanceFixture()
    {
        const int columns = 400;
        const float left = -1_950;
        const float top = -1_045;
        const float columnStep = 9.75f;
        const float rowStep = 15f;
        var scene = new VectorScene();
        scene.CreateEmpty();
        scene.BeginDeferredAppend(ScenePerformanceTotalUnits, [0]);
        try
        {
            for (var index = 0; index < ScenePerformanceLineUnits; index++)
            {
                var column = index % columns;
                var row = index / columns;
                scene.AppendObject(
                    0,
                    new PointF(left + column * columnStep - 2.1f, top + row * rowStep),
                    new SizeF(6f, 1f),
                    angle: index % 2 == 0 ? -0.08f : 0.08f,
                    stroke: VectorUnits.StrokePointsToUnits(1f),
                    color: Color.Transparent,
                    strokeColor: Color.FromArgb(255, 74, 191, 230),
                    atoms: 3,
                    shapeKind: ShapeKind.Line);
            }

            for (var index = 0; index < ScenePerformanceRectangleUnits; index++)
            {
                var column = index % columns;
                var row = index / columns;
                scene.AppendObject(
                    0,
                    new PointF(left + column * columnStep + 2.1f, top + row * rowStep),
                    new SizeF(5f, 5f),
                    angle: 0,
                    stroke: 0,
                    color: Color.FromArgb(255, 235, 145, 65),
                    strokeColor: Color.Transparent,
                    atoms: 4,
                    shapeKind: ShapeKind.Rectangle);
            }
        }
        finally
        {
            scene.EndDeferredAppend();
        }

        scene.CompleteDeferredBuild();
        return scene;
    }

    private static ScenePerformanceMetrics MeasureTwoD(StageControl stage)
    {
        stage.ConfigureReferenceView(null, SceneDimension.TwoD, ReferenceCameraMotion.Immediate);
        stage.SetVisibleWorldWidth(4_000);
        return MeasureFrames(
            stage,
            frame => PanScenePerformanceView(stage, frame),
            "2d");
    }

    // Every sample must redraw, so the camera keeps moving. A monotonic pan
    // would also drift the fixture out of the viewport over the warmup and
    // sample frames, which would fail the full-object assertion for a camera
    // reason instead of a renderer reason. The pan therefore reverses every 30 frames.
    private static void PanScenePerformanceView(StageControl stage, int frame)
    {
        var direction = frame / 30 % 2 == 0 ? 1f : -1f;
        var step = 0.55f + frame % 3 * 0.04f;
        stage.Pan(step * direction, 0.32f * direction);
    }

    private static ScenePerformanceMetrics MeasureThreeD(StageControl stage)
    {
        var definition = new SceneDefinition { Dimension = SceneDimension.ThreeD };
        stage.ConfigureReferenceView(definition, SceneDimension.ThreeD, ReferenceCameraMotion.Immediate);
        stage.ResetReferenceCameraView(ReferenceCameraMotion.Immediate);
        return MeasureFrames(
            stage,
            frame => stage.SetReferenceCameraOrientation(
                0.02f + frame * 0.0025f,
                -0.015f + MathF.Sin(frame * 0.12f) * 0.006f,
                ReferenceCameraMotion.Immediate),
            "3d");
    }

    private static ScenePerformanceMetrics MeasureFrames(
        StageControl stage,
        Action<int> updateView,
        string label)
    {
        var warmup = new Stopwatch();
        for (var frame = 0; frame < ScenePerformanceWarmupFrames; frame++)
        {
            updateView(frame);
            warmup.Restart();
            PresentPerformanceFrame(stage);
            warmup.Stop();
            Console.WriteLine($"scene_performance_{label}_warmup_{frame}_ms={warmup.Elapsed.TotalMilliseconds:0.###}");
        }

        var samples = new double[ScenePerformanceSampleFrames];
        var visible = new int[ScenePerformanceSampleFrames];
        var drawn = new int[ScenePerformanceSampleFrames];
        var usedExplicitGpu = true;
        var fullObjectRender = true;
        var commandMilliseconds = 0d;
        var presentMilliseconds = 0d;
        var renderMilliseconds = 0d;
        var planMilliseconds = 0d;
        var baseFrameCopyMilliseconds = 0d;
        var cacheMaintenanceMilliseconds = 0d;
        var collectMilliseconds = 0d;
        var objectDrawMilliseconds = 0d;
        var boundsMilliseconds = 0d;
        var collectPhaseMilliseconds = 0d;
        var sortPhaseMilliseconds = 0d;
        var renderer = (Direct2DStageRenderer?)RequireField(
            typeof(StageControl),
            "_direct2DRenderer").GetValue(stage);
        if (renderer is null) throw new InvalidOperationException("The performance renderer disappeared during sampling.");
        var traceSceneStates = Environment.GetEnvironmentVariable("VECTOR_BENCH_SCENE_TRACE") == "1";
        var gc0Before = GC.CollectionCount(0);
        var gc1Before = GC.CollectionCount(1);
        var gc2Before = GC.CollectionCount(2);
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);

        for (var frame = 0; frame < ScenePerformanceSampleFrames; frame++)
        {
            var stopwatch = Stopwatch.StartNew();
            updateView(ScenePerformanceWarmupFrames + frame);
            PresentPerformanceFrame(stage);
            stopwatch.Stop();
            if (traceSceneStates)
            {
                Console.WriteLine(
                    $"scene_performance_{label}_frame_state={frame}:"
                    + $"geometry={stage.Scene.GeometryRevision}:"
                    + $"content={stage.Scene.ActiveContentRevision}:"
                    + $"summary={stage.Scene.SummaryRevision}:"
                    + $"frame={stage.Frame}:"
                    + $"zoom={stage.Zoom:0.####}");
            }
            samples[frame] = stopwatch.Elapsed.TotalMilliseconds;
            visible[frame] = stage.LastStats.VisibleObjects;
            drawn[frame] = stage.LastStats.DrawnObjects;
            commandMilliseconds += renderer.LastCommandMilliseconds;
            presentMilliseconds += renderer.LastPresentMilliseconds;
            renderMilliseconds += stage.LastFrameRenderMilliseconds;
            planMilliseconds += stage.LastReference3DParallelRenderPlanMilliseconds;
            baseFrameCopyMilliseconds += renderer.LastBaseFrameCopyMilliseconds;
            cacheMaintenanceMilliseconds += renderer.LastCacheMaintenanceMilliseconds;
            collectMilliseconds += renderer.DiagnosticCollectMilliseconds;
            objectDrawMilliseconds += renderer.DiagnosticObjectDrawMilliseconds;
            boundsMilliseconds += SceneRenderOrderBuffer.DiagnosticBoundsMs;
            collectPhaseMilliseconds += SceneRenderOrderBuffer.DiagnosticCollectPhaseMs;
            sortPhaseMilliseconds += SceneRenderOrderBuffer.DiagnosticSortPhaseMs;
            fullObjectRender &= !stage.LastStats.TileLod
                && stage.LastStats.DrawnObjects == ScenePerformanceTotalUnits;
            usedExplicitGpu &= renderer.ExplicitGpuDeviceActive
                && stage.LastFrameUsedDirect2D
                && stage.GpuAccelerationActive;
        }

        if (Environment.GetEnvironmentVariable("VECTOR_BENCH_SCENE_TRACE") == "1")
        {
            Console.WriteLine($"scene_performance_{label}_samples_ms={string.Join(",", samples.Select(sample => sample.ToString("0.##")))}");
            Console.WriteLine(
                $"scene_performance_{label}_gc_collections={GC.CollectionCount(0) - gc0Before},"
                + $"{GC.CollectionCount(1) - gc1Before},{GC.CollectionCount(2) - gc2Before}");
            Console.WriteLine(
                $"scene_performance_{label}_allocated_bytes={GC.GetTotalAllocatedBytes(precise: false) - allocatedBefore}");
        }

        Array.Sort(samples);
        var average = samples.Average();
        var p95 = samples[Math.Clamp((int)Math.Ceiling(samples.Length * 0.95) - 1, 0, samples.Length - 1)];
        var maximum = samples[^1];
        Console.WriteLine($"scene_performance_{label}_commands_ms={commandMilliseconds / samples.Length:0.###}");
        Console.WriteLine($"scene_performance_{label}_present_ms={presentMilliseconds / samples.Length:0.###}");
        Console.WriteLine($"scene_performance_{label}_render_ms={renderMilliseconds / samples.Length:0.###}");
        Console.WriteLine($"scene_performance_{label}_plan_ms={planMilliseconds / samples.Length:0.###}");
        Console.WriteLine($"scene_performance_{label}_base_frame_copy_ms={baseFrameCopyMilliseconds / samples.Length:0.###}");
        Console.WriteLine($"scene_performance_{label}_cache_maintenance_ms={cacheMaintenanceMilliseconds / samples.Length:0.###}");
        Console.WriteLine($"scene_performance_{label}_collect_ms={collectMilliseconds / samples.Length:0.###}");
        Console.WriteLine($"scene_performance_{label}_object_draw_ms={objectDrawMilliseconds / samples.Length:0.###}");
        Console.WriteLine($"scene_performance_{label}_bounds_ms={boundsMilliseconds / samples.Length:0.###}");
        Console.WriteLine($"scene_performance_{label}_collect_phase_ms={collectPhaseMilliseconds / samples.Length:0.###}");
        Console.WriteLine($"scene_performance_{label}_sort_phase_ms={sortPhaseMilliseconds / samples.Length:0.###}");
        var budgetMet = average <= ScenePerformanceFrameBudgetMilliseconds
            && p95 <= ScenePerformanceFrameBudgetMilliseconds
            && maximum <= ScenePerformanceFrameBudgetMilliseconds
            && usedExplicitGpu
            && fullObjectRender;
        return new ScenePerformanceMetrics(
            label,
            average,
            p95,
            maximum,
            1000d / Math.Max(0.001, average),
            visible.Average(),
            visible.Min(),
            visible.Max(),
            drawn.Average(),
            usedExplicitGpu,
            fullObjectRender,
            budgetMet);
    }

    private static void PresentPerformanceFrame(StageControl stage)
    {
        if (!stage.Visible || stage.Width != 1920 || stage.Height != 1080
            || stage.FindForm()?.WindowState == FormWindowState.Minimized)
            throw new InvalidOperationException("Scene performance sample invalid: the 1920x1080 Stage must remain visible and unminimized.");
        var rendered = false;
        void OnRendered(object? sender, EventArgs args) => rendered = true;
        stage.FrameRendered += OnRendered;
        try
        {
            stage.Invalidate();
            stage.Update();
            Application.DoEvents();
        }
        finally { stage.FrameRendered -= OnRendered; }
        if (!rendered)
            throw new InvalidOperationException("Scene performance sample invalid: the Stage did not render a new frame.");
        if (stage.Width != 1920 || stage.Height != 1080
            || stage.FindForm()?.WindowState == FormWindowState.Minimized)
            throw new InvalidOperationException("Scene performance sample invalid: the window changed size or was minimized during presentation.");
    }

    // Validation-only comparison of the batched layer geometry against the
    // per-object renderer. Both captures present the exact same world state, so
    // any pixel difference comes from the two Direct2D submission strategies.
    private static void AssertScenePerformanceBatch2DParity(StageControl stage)
    {
        if (Environment.GetEnvironmentVariable("VECTOR_BENCH_BATCH2D_PARITY") != "1") return;
        stage.ConfigureReferenceView(null, SceneDimension.TwoD, ReferenceCameraMotion.Immediate);
        stage.SetVisibleWorldWidth(4_000);
        stage.Pan(0.55f, 0.32f);
        var batched = CaptureScenePerformancePixels(stage);
        Environment.SetEnvironmentVariable("VECTOR_DISABLE_BATCH_2D", "1");
        try
        {
            var perObject = CaptureScenePerformancePixels(stage);
            var differing = 0;
            var maximumDelta = 0;
            for (var index = 0; index < batched.Length; index++)
            {
                if (batched[index] == perObject[index]) continue;
                differing++;
                maximumDelta = Math.Max(maximumDelta, ScenePerformanceChannelDelta(batched[index], perObject[index]));
            }

            Console.WriteLine($"scene_performance_batch2d_parity_differing_pixels={differing}");
            Console.WriteLine($"scene_performance_batch2d_parity_max_channel_delta={maximumDelta}");
            Console.WriteLine($"scene_performance_batch2d_parity={(differing == 0 ? "identical" : "differs")}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("VECTOR_DISABLE_BATCH_2D", null);
        }
    }

    private static int[] CaptureScenePerformancePixels(StageControl stage)
    {
        stage.Invalidate();
        stage.Update();
        Application.DoEvents();
        var size = stage.ClientSize;
        using var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(stage.PointToScreen(Point.Empty), Point.Empty, size);
        }

        var pixels = new int[size.Width * size.Height];
        var data = bitmap.LockBits(
            new Rectangle(0, 0, size.Width, size.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return pixels;
    }

    private static int ScenePerformanceChannelDelta(int first, int second)
    {
        var alpha = Math.Abs(((first >>> 24) & 0xff) - ((second >>> 24) & 0xff));
        var red = Math.Abs(((first >>> 16) & 0xff) - ((second >>> 16) & 0xff));
        var green = Math.Abs(((first >>> 8) & 0xff) - ((second >>> 8) & 0xff));
        var blue = Math.Abs((first & 0xff) - (second & 0xff));
        return Math.Max(Math.Max(alpha, red), Math.Max(green, blue));
    }

    private static void WriteScenePerformanceMetrics(string label, ScenePerformanceMetrics metrics)
    {
        Console.WriteLine($"scene_performance_{label}_gpu={metrics.UsedExplicitGpu.ToString().ToLowerInvariant()}");
        Console.WriteLine($"scene_performance_{label}_avg_ms={metrics.AverageMilliseconds:0.###}");
        Console.WriteLine($"scene_performance_{label}_p95_ms={metrics.P95Milliseconds:0.###}");
        Console.WriteLine($"scene_performance_{label}_max_ms={metrics.MaximumMilliseconds:0.###}");
        Console.WriteLine($"scene_performance_{label}_fps={metrics.FramesPerSecond:0.###}");
        Console.WriteLine($"scene_performance_{label}_visible_avg={metrics.VisibleAverage:0.###}");
        Console.WriteLine($"scene_performance_{label}_visible_min={metrics.VisibleMinimum}");
        Console.WriteLine($"scene_performance_{label}_visible_max={metrics.VisibleMaximum}");
        Console.WriteLine($"scene_performance_{label}_drawn_avg={metrics.DrawnAverage:0.###}");
        Console.WriteLine($"scene_performance_{label}_full_object_render={metrics.FullObjectRender.ToString().ToLowerInvariant()}");
        Console.WriteLine($"scene_performance_{label}_budget_ms={ScenePerformanceFrameBudgetMilliseconds:0.###}");
        Console.WriteLine($"scene_performance_{label}_budget_met={metrics.BudgetMet.ToString().ToLowerInvariant()}");
    }

    private static void RequireScenePerformanceBudget(ScenePerformanceMetrics metrics)
    {
        if (!metrics.BudgetMet)
            throw new InvalidOperationException(
                $"112,000-unit {metrics.Label} rendering did not meet the 60 FPS frame budget: "
                + $"average={metrics.AverageMilliseconds:0.###} ms, P95={metrics.P95Milliseconds:0.###} ms, "
                + $"maximum={metrics.MaximumMilliseconds:0.###} ms.");
    }

    private readonly record struct ScenePerformanceMetrics(
        string Label,
        double AverageMilliseconds,
        double P95Milliseconds,
        double MaximumMilliseconds,
        double FramesPerSecond,
        double VisibleAverage,
        int VisibleMinimum,
        int VisibleMaximum,
        double DrawnAverage,
        bool UsedExplicitGpu,
        bool FullObjectRender,
        bool BudgetMet);
}
