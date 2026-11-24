using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.Direct2D1;
using Vortice.DXGI;
using SizeI = Vortice.Mathematics.SizeI;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunBitmapGpuWarpRegression()
    {
        var source = new BitmapImageRaster(new BitmapImageRasterKey("gpu-warp-2048", 2048, 1536));
        for (var offset = 0; offset < source.Pixels.Length; offset += 4)
        {
            source.Pixels[offset] = 40; source.Pixels[offset + 1] = 70;
            source.Pixels[offset + 2] = 120; source.Pixels[offset + 3] = 200;
        }
        var scene = new VectorScene(); scene.CreateEmpty();
        scene.AddBitmapObject(0, PointF.Empty, new BitmapObjectData
        {
            ImageAssetId = "gpu-warp", PlacedSize = new SizeF(30000, 20000), IsBrokenApart = true,
            VisibleContours = [[new(0, 0), new(1, 0), new(1, 1), new(0, 1)],
                [new(.45f, .45f), new(.55f, .45f), new(.55f, .55f), new(.45f, .55f)]]
        });
        using var form = new Form { ClientSize = new Size(1920, 1080), ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual, Location = new Point(-3000, -3000) };
        using var stage = new StageControl(scene) { Dock = DockStyle.Fill, BitmapImageResolver = _ => source };
        form.Controls.Add(stage); form.Show(); Application.DoEvents();
        stage.ZoomAt(new Point(960, 540), 1 / stage.Zoom);
        PaintStage(stage);
        var renderer = (Direct2DStageRenderer)RequireField(typeof(StageControl), "_direct2DRenderer").GetValue(stage)!;
        AssertTimeline(renderer.ExplicitGpuDeviceActive, "GPU image warp requires a hardware device.");
        var bounds = scene.GetRawObjectWorldBounds(0);
        var frame = TransformOverlayFrame.FromBounds(bounds);
        var initial = DistortEnvelope.FromBounds(bounds);
        var selectionStarted = Stopwatch.GetTimestamp();
        stage.SelectedObject = 0;
        stage.SetDistortOverlay(true, initial);
        PaintStage(stage);
        var selectionMs = Stopwatch.GetElapsedTime(selectionStarted).TotalMilliseconds;
        var anchor = initial.GetAnchors(DistortSide.Top)[0];
        var handle = new DistortHandleRef(DistortSide.Top, anchor.Id, DistortHandleKind.OutgoingControl);
        var revision = scene.GeometryRevision;
        var durations = new List<double>();
        var breakdown = new List<(double Mesh, double Draw, double Present)>();
        DistortWarp last = default;
        double firstMove = 0, firstMesh = 0, firstDraw = 0, firstInput = 0, firstPresent = 0, firstCommands = 0;
        double firstCollect = 0, firstObjects = 0, firstSelection = 0, firstEnvelope = 0, firstPreview = 0;
        for (var step = 0; step < 180; step++)
        {
            var started = Stopwatch.GetTimestamp();
            var envelope = initial.WithHandle(handle, anchor.OutgoingControl,
                new PointF(anchor.OutgoingControl.X + 4000, anchor.OutgoingControl.Y - 5000 - step * 12));
            var envelopeMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            last = new DistortWarp(frame, envelope);
            stage.SetDistortPreview(scene, new Dictionary<int, DistortWarp[]> { [0] = [last] });
            var previewMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds - envelopeMs;
            stage.SetDistortOverlay(true, envelope);
            var inputMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            PaintStage(stage);
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (step == 0)
            {
                firstMove = elapsed;
                firstMesh = renderer.LastBitmapWarpMeshMilliseconds;
                firstDraw = renderer.LastBitmapWarpDrawMilliseconds;
                firstInput = inputMs;
                firstPresent = stage.LastDirect2DPresentMilliseconds;
                firstCommands = renderer.LastCommandMilliseconds;
                firstCollect = renderer.DiagnosticCollectMilliseconds;
                firstObjects = renderer.DiagnosticObjectDrawMilliseconds;
                firstSelection = renderer.DiagnosticSelectionMilliseconds;
                firstEnvelope = envelopeMs;
                firstPreview = previewMs;
            }
            AssertTimeline(stage.LastFrameUsedDirect2D, "A curved image preview fell back to CPU rasterization.");
            if (step >= 30)
            {
                durations.Add(elapsed);
                breakdown.Add((renderer.LastBitmapWarpMeshMilliseconds, renderer.LastBitmapWarpDrawMilliseconds,
                    stage.LastDirect2DPresentMilliseconds));
            }
        }
        var warp = (StageGpuBitmapWarp?)RequireField(typeof(Direct2DStageRenderer), "_bitmapWarp").GetValue(renderer);
        AssertTimeline(warp is not null, "The GPU image preview did not submit its mesh.");
        var bitmap = (ID2D1Bitmap1)RequireField(typeof(StageGpuBitmapWarp), "_bitmap").GetValue(warp)!;
        var host = (StageGpuDevice)RequireField(typeof(Direct2DStageRenderer), "_gpuDevice").GetValue(renderer)!;
        using var readback = host.Context.CreateBitmap(new SizeI(stage.Width, stage.Height), IntPtr.Zero, 0,
            new BitmapProperties1(new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm,
                Vortice.DCommon.AlphaMode.Premultiplied), 96, 96, BitmapOptions.CpuRead | BitmapOptions.CannotDraw));
        readback.CopyFromBitmap(bitmap).CheckError();
        Console.WriteLine($"bitmap_gpu_warp_mesh_ms={breakdown.Average(v => v.Mesh):0.000},draw_ms={breakdown.Average(v => v.Draw):0.000},present_ms={breakdown.Average(v => v.Present):0.000}");
        var mapped = readback.Map(MapOptions.Read);
        try
        {
            foreach (var uv in new[] { new PointF(.2f, .2f), new PointF(.8f, .2f), new PointF(.2f, .8f) })
            {
                var color = Pixel(uv);
                AssertTimeline(Math.Abs(color.A - 200) <= 1 && Math.Abs(color.R - 120) <= 1
                    && Math.Abs(color.G - 70) <= 1 && Math.Abs(color.B - 40) <= 1,
                    $"GPU warp lost premultiplied texture colour/coverage at {uv}: {color}.");
            }
            AssertTimeline(Pixel(new PointF(.5f, .5f)).A == 0, "GPU warp filled a clipped image hole.");

            Color Pixel(PointF uv)
            {
                var world = last.Map(new PointF(bounds.Left + uv.X * bounds.Width, bounds.Top + uv.Y * bounds.Height));
                var screen = stage.WorldToScreen(world.X, world.Y);
                AssertTimeline(screen.X >= 0 && screen.X < stage.Width && screen.Y >= 0 && screen.Y < stage.Height,
                    "GPU warp landmark lies outside the fixture viewport.");
                return Color.FromArgb(Marshal.ReadInt32(mapped.Bits, checked((int)screen.Y * (int)mapped.Pitch + (int)screen.X * 4)));
            }
        }
        finally { readback.Unmap(); }
        AssertTimeline(scene.GeometryRevision == revision && !scene.HasObjectDistortions
            && stage.DistortResultBakeAttemptCount == 0, "A GPU preview mutated or baked the source document.");
        var repeated = new List<double>();
        var firstMoves = new List<double>();
        var releases = new List<double>();
        double firstCommitMs = 0;
        var allocatedBeforeGestures = GC.GetTotalAllocatedBytes();
        var collectionsBeforeGestures = GC.CollectionCount(2);
        for (var gesture = 0; gesture < 12; gesture++)
        {
            var startEnvelope = last.Envelope;
            AssertTimeline(startEnvelope.TryGetHandlePosition(handle, out var start), "Missing repeated-drag handle.");
            for (var move = 1; move <= 15; move++)
            {
                var started = Stopwatch.GetTimestamp();
                var envelope = startEnvelope.WithHandle(handle, start, new PointF(start.X, start.Y - move * 8));
                last = new DistortWarp(frame, envelope);
                stage.SetDistortPreview(scene, new Dictionary<int, DistortWarp[]> { [0] = [last] });
                stage.SetDistortOverlay(true, envelope);
                PaintStage(stage);
                var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                repeated.Add(ms);
                if (move == 1) firstMoves.Add(ms);
                AssertTimeline(stage.LastFrameUsedDirect2D, "A repeated image drag fell back to software.");
            }
            var release = Stopwatch.GetTimestamp();
            var releaseRevision = scene.GeometryRevision;
            var releaseCells = scene.CellStart;
            AssertTimeline(scene.SetObjectDistortionsBatch([(0, new[] { last })]), "Repeated GPU warp did not commit.");
            if (gesture == 0) firstCommitMs = Stopwatch.GetElapsedTime(release).TotalMilliseconds;
            AssertTimeline(scene.GeometryRevision > releaseRevision && ReferenceEquals(releaseCells, scene.CellStart),
                "Bitmap commit did not invalidate geometry queries or rebuilt unchanged spatial cells.");
            var committedBounds = scene.GetObjectWorldBounds(0);
            AssertTimeline(scene.MaxHalfExtent >= Math.Max(Math.Abs(committedBounds.Top - scene.Y[0]),
                Math.Abs(committedBounds.Bottom - scene.Y[0])), "Bitmap commit left its spatial query radius too small.");
            stage.ClearDistortPreview();
            PaintStage(stage);
            releases.Add(Stopwatch.GetElapsedTime(release).TotalMilliseconds);
            AssertTimeline(stage.LastFrameUsedDirect2D && renderer.LastBitmapWarpMeshMilliseconds == 0
                && stage.DistortResultBakeAttemptCount == 0,
                "Releasing the drag rerasterized the unchanged warp or started a native bake.");
        }
        var earlyAverage = repeated.Take(45).Average();
        var lateAverage = repeated.TakeLast(45).Average();
        repeated.Sort();
        var repeatedP95 = repeated[(int)Math.Ceiling(repeated.Count * .95) - 1];
        Console.WriteLine($"bitmap_gpu_warp_repeated=ok,gestures=12,updates={repeated.Count},avg_ms={repeated.Average():0.000}," +
            $"p95_ms={repeatedP95:0.000},early_avg_ms={earlyAverage:0.000},late_avg_ms={lateAverage:0.000}," +
            $"max_ms={repeated[^1]:0.000},max_first_move_ms={firstMoves.Max():0.000},first_commit_ms={firstCommitMs:0.000}," +
            $"first_release_ms={releases[0]:0.000},max_release_ms={releases.Max():0.000}," +
            $"allocated_bytes={GC.GetTotalAllocatedBytes() - allocatedBeforeGestures},gen2_collections={GC.CollectionCount(2) - collectionsBeforeGestures}");
        Console.WriteLine($"bitmap_gpu_warp_repeated_150fps_budget_met={(repeated.Average() <= 1000d / 150 && repeatedP95 <= 1000d / 150 ? "true" : "false")}");
        AssertTimeline(scene.SetObjectDistortionsBatch([(0, Array.Empty<DistortWarp>())])
            && scene.MaxHalfExtent == Math.Max(128, Math.Max(bounds.Width, bounds.Height) / 2),
            "Clearing the largest bitmap warp did not restore the spatial query radius.");
        stage.ClearDistortPreview(); stage.SetDistortOverlay(false, default); PaintStage(stage);
        durations.Sort();
        var average = durations.Average(); var p95 = durations[(int)Math.Ceiling(durations.Count * .95) - 1];
        Console.WriteLine($"bitmap_gpu_warp=ok,adapter={renderer.GpuAdapterName},viewport=1920x1080,source=2048x1536," +
            $"updates=150,warmup=30,selection_ms={selectionMs:0.000},first_move_ms={firstMove:0.000},first_input_ms={firstInput:0.000}," +
            $"first_mesh_ms={firstMesh:0.000},first_draw_ms={firstDraw:0.000},first_commands_ms={firstCommands:0.000},first_present_ms={firstPresent:0.000}," +
            $"avg_ms={average:0.000},p95_ms={p95:0.000}," +
            $"max_ms={durations.Max():0.000},fps={1000 / average:0.0}");
        Console.WriteLine($"bitmap_gpu_warp_150fps_budget_met={((average <= 1000d / 150 && p95 <= 1000d / 150) ? "true" : "false")}");
        Console.WriteLine($"bitmap_gpu_warp_first_move_150fps_budget_met={(firstMove <= 1000d / 150 ? "true" : "false")}");
        Console.WriteLine($"bitmap_gpu_warp_first_move_breakdown=envelope_ms={firstEnvelope:0.000},preview_ms={firstPreview:0.000}," +
            $"collect_ms={firstCollect:0.000},objects_ms={firstObjects:0.000},selection_ms={firstSelection:0.000}");
        var mesh = BitmapDistortRasterizer.BuildPreviewMesh(scene, 0, [last], stage.WorldLengthToScreen(1), new());
        AssertTimeline(mesh is not null, "The reused boundary samples did not build a preview mesh.");
        for (var y = 0; y <= mesh!.Rows; y++)
        for (var x = 0; x <= mesh.Columns; x++)
        {
            var expected = last.Map(new PointF((float)(scene.X[0] + (x / (double)mesh.Columns - .5) * scene.Width[0]),
                (float)(scene.Y[0] + (y / (double)mesh.Rows - .5) * scene.Height[0])));
            var actual = mesh.Points[y * (mesh.Columns + 1) + x];
            AssertTimeline(actual == expected, "The reused boundary samples changed a preview mesh vertex.");
        }
        VerifyMixedBitmapLayer();

        void VerifyMixedBitmapLayer()
        {
            var panel = new BitmapImageRaster(new BitmapImageRasterKey("gpu-warp-affine-cover", 8, 8));
            for (var offset = 0; offset < panel.Pixels.Length; offset += 4)
            {
                panel.Pixels[offset + 1] = 255;
                panel.Pixels[offset + 3] = 255;
            }
            stage.BitmapImageResolver = asset => asset == "gpu-warp-affine-cover" ? panel : source;
            var overlap = last.Map(new PointF(bounds.Left + .2f * bounds.Width, bounds.Top + .2f * bounds.Height));
            scene.AddBitmapObject(0, overlap, new BitmapObjectData
            {
                ImageAssetId = "gpu-warp-affine-cover", PlacedSize = new SizeF(1200, 1200)
            });
            stage.SetDistortOverlay(true, last.Envelope);
            stage.SetDistortPreview(scene, new Dictionary<int, DistortWarp[]> { [0] = [last] });
            PaintStage(stage);
            AssertTimeline(stage.LastFrameUsedDirect2D, "A bitmap layer mixing a warp and an affine placement fell back to software.");
            PaintStage(stage);
            var pixels = CaptureStageGpuPixels(stage);
            var screen = stage.WorldToScreen(overlap.X, overlap.Y);
            var offsetAtOverlap = ((int)screen.Y * stage.Width + (int)screen.X) * 4;
            AssertTimeline(pixels[offsetAtOverlap] == 0 && pixels[offsetAtOverlap + 1] == 255
                && pixels[offsetAtOverlap + 2] == 0,
                $"Unexpected mixed-layer pixel at {screen}: BGRA {pixels[offsetAtOverlap]},{pixels[offsetAtOverlap + 1]}," +
                $"{pixels[offsetAtOverlap + 2]},{pixels[offsetAtOverlap + 3]}; frame {stage.LastStats}.");
            Console.WriteLine("bitmap_gpu_warp_mixed_layer=ok,affine_above_curved=ok");
        }
    }
}
