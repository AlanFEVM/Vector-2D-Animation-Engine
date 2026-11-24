using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Diagnostics;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunPressurePreviewStabilityRegression()
    {
        RunPressureInkRegression();
        RunPressurePreviewStablePrefixRegression();
        RunPressurePreviewTailReplacementRegression();
        RunPressurePreviewCommitParityRegression();
        RunPressureOptionsRegression();
        RunPressurePreviewReleaseRegression();
        RunPressurePathCurvatureAndWorkRegression();
        RunPressureInputKnotsRegression();
        Console.WriteLine("pressure_preview_stability=ok");
    }

    private static void RunPressureInputKnotsRegression()
    {
        using var form = new MainForm { ShowInTaskbar = false };
        var tabs = (WorkspaceTabs)RequireField(typeof(MainForm), "_workspaceTabs").GetValue(form)!;
        tabs.SelectedView = WorkspaceView.BasicDrawing;
        RequireMethod(typeof(MainForm), "SwitchDrawingScope").Invoke(form, [true]);
        var settings = (DrawSettings)RequireField(typeof(MainForm), "_drawSettings").GetValue(form)!;
        settings.PressureBrushOptions = PressureBrushOptions.Default;
        RequireMethod(typeof(MainForm), "ActivateTool", [typeof(ToolMode)]).Invoke(form, [ToolMode.PressureBrush]);
        var stage = (StageControl)RequireField(typeof(MainForm), "_stage").GetValue(form)!;
        Point[] events = [new(100, 300), new(180, 180), new(300, 100), new(420, 180), new(500, 300)];
        RequireMethod(typeof(MainForm), "BeginFreehandStroke").Invoke(form,
            [events[0], stage.ScreenToWorld(events[0]), 0.7f]);
        RequireField(typeof(MainForm), "_freehandStrokeUnits").SetValue(form, 200f);
        var append = RequireMethod(typeof(MainForm), "AppendFreehandSample");
        foreach (var location in events.Skip(1)) append.Invoke(form, [location, false, 0.7f, false]);
        var samples = (List<PressureBrushSample>)RequireField(typeof(MainForm), "_pressureBrushSamples").GetValue(form)!;
        AssertBrushGeometry(samples.Count == events.Length,
            "Pressure pointer input was inflated into synthetic points along straight event chords.");
        RequireMethod(typeof(MainForm), "BuildPressureBrushAreaPreview").Invoke(form, null);
        var raw = (List<PressureBrushPoint>)RequireField(typeof(MainForm), "_brushAreaPressureProfile").GetValue(form)!;
        var scale = (float)RequireField(typeof(MainForm), "_freehandPencilWorldPerPixel").GetValue(form)!;
        var smoothed = FreehandStrokeProcessor.SmoothPressureProfileSpatial(raw, 100, scale);
        AssertBrushGeometry(smoothed.SequenceEqual(raw),
            "Sparse pressure events were pulled toward distant neighbours outside the smoothing radius.");
        var interpolate = RequireMethod(typeof(BrushStrokeBuilder), "InterpolatePressureRange",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        var curved = (PressureBrushPoint[])interpolate.Invoke(null, [smoothed, 0, smoothed.Length, 100, 1f])!;
        var probe = curved.MaxBy(sample => DistanceFromChords(sample.Point));
        var curveDeviation = DistanceFromChords(probe.Point);
        Console.WriteLine($"pressure_input_knots_curve_deviation={curveDeviation:0.00}");
        AssertBrushGeometry(curved.Length > raw.Count && curveDeviation > 5,
            "Sparse pressure knots were still connected by long straight chords with smoothing enabled.");
        var preview = (VectorScene)RequireField(typeof(MainForm), "_brushAreaPreviewStage").GetValue(form)!;
        AssertBrushGeometry(Enumerable.Range(0, preview.ObjectCount).Any(index => preview.FillContainsPoint(index, probe.Point)),
            "Pressure sweep ignored the interpolated arc between real pointer knots.");
        RequireMethod(typeof(MainForm), "CancelFreehandStroke").Invoke(form, null);
        Console.WriteLine("pressure_input_knots=ok");

        float DistanceFromChords(PointF point)
        {
            var distance = float.MaxValue;
            for (var index = 1; index < raw.Count; index++)
            {
                var start = raw[index - 1].Point;
                var end = raw[index].Point;
                var dx = end.X - start.X;
                var dy = end.Y - start.Y;
                var squared = dx * dx + dy * dy;
                var amount = squared <= 0 ? 0 : Math.Clamp(((point.X - start.X) * dx + (point.Y - start.Y) * dy) / squared, 0, 1);
                var offsetX = point.X - start.X - amount * dx;
                var offsetY = point.Y - start.Y - amount * dy;
                distance = Math.Min(distance, MathF.Sqrt(offsetX * offsetX + offsetY * offsetY));
            }
            return distance;
        }
    }

    private static void RunPressurePathCurvatureAndWorkRegression()
    {
        foreach (var roundness in new[] { 100, 35 })
        {
            using var form = new MainForm { ShowInTaskbar = false };
            var tabs = (WorkspaceTabs)RequireField(typeof(MainForm), "_workspaceTabs").GetValue(form)!;
            tabs.SelectedView = WorkspaceView.BasicDrawing;
            RequireMethod(typeof(MainForm), "SwitchDrawingScope").Invoke(form, [true]);
            var settings = (DrawSettings)RequireField(typeof(MainForm), "_drawSettings").GetValue(form)!;
            settings.PressureBrushOptions = PressureBrushOptions.Default with { Roundness = roundness, Angle = 32 };
            RequireMethod(typeof(MainForm), "ActivateTool", [typeof(ToolMode)]).Invoke(form, [ToolMode.PressureBrush]);
            var host = (VectorScene)RequireField(typeof(MainForm), "_scene").GetValue(form)!;
            host.CreateEmpty();
            RequireMethod(typeof(MainForm), "BeginFreehandStroke").Invoke(form,
                [new Point(0, 0), new PointF(5000, 0), 0.7f]);
            var input = (List<PressureBrushSample>)RequireField(typeof(MainForm), "_pressureBrushSamples").GetValue(form)!;
            input.Clear();
            RequireField(typeof(MainForm), "_freehandColor").SetValue(form, Color.FromArgb(120, Color.Coral));
            RequireField(typeof(MainForm), "_freehandStrokeUnits").SetValue(form, 300f);
            var preview = (VectorScene)RequireField(typeof(MainForm), "_brushAreaPreviewStage").GetValue(form)!;
            RequireMethod(typeof(MainForm), "ResetBrushAreaPreview").Invoke(form, null);
            var build = RequireMethod(typeof(MainForm), "BuildPressureBrushAreaPreview");
            var timings = new List<double>();
            const int count = 1025;
            for (var last = 1; last <= count; last = Math.Min(count, last + 32))
            {
                while (input.Count < last)
                {
                    var angle = input.Count * MathF.Tau / (count - 1);
                    input.Add(new PressureBrushSample(new PointF(MathF.Cos(angle) * 5000,
                        MathF.Sin(angle) * 5000), 200, input.Count * 0.01f, 0.7f));
                }
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                build.Invoke(form, null);
                timings.Add(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                if (last == count) break;
            }
            var cache = (BrushStrokeBuilder.PressurePreview)RequireField(typeof(MainForm), "_brushPressureGeometry").GetValue(form)!;
            AssertBrushGeometry(cache.SweptSampleCount < count * 7L && cache.SealedCount >= count - 100,
                "Pressure preview swept its growing prefix again instead of retaining completed coverage.");
            AssertBrushGeometry(preview.ObjectCount == 1 && Color.FromArgb(preview.Argb[0]).A == 120
                && preview.FillContainsPoint(0, new PointF(5000, 0)) && !preview.FillContainsPoint(0, PointF.Empty),
                "A closed pressure loop lost its hole, accumulated alpha or split into overlapping path chunks.");
            AssertBrushGeometry(preview.TryGetPathBezierWorldContours(0, out var contours)
                && contours.Sum(contour => contour.Count(node => node.IncomingControl != node.Anchor
                    || node.OutgoingControl != node.Anchor)) >= 8,
                "A smooth pressure arc fell back to a straight-sided polygon.");
            AssertBrushGeometry(preview.LastFillMergeTotalMilliseconds == 0,
                "Pressure preview still invoked editable-fill merge for its own chunks.");
            var revision = preview.GeometryRevision;
            var work = cache.SweptSampleCount;
            build.Invoke(form, null);
            AssertBrushGeometry(preview.GeometryRevision == revision && cache.SweptSampleCount == work,
                "Unchanged pressure input rebuilt the displayed geometry at release.");
            var committed = NewBrushGeometryScene();
            var added = BrushStrokeBuilder.AddPreparedBrushStroke(committed, 0, preview);
            AssertBrushGeometrySamePaths(preview, [0], committed, added,
                "Pressure curvature changed when transferring the preview for commit.");
            timings.Sort();
            var p95 = timings[(int)Math.Ceiling(timings.Count * 0.95) - 1];
            Console.WriteLine($"pressure_path_preview_{roundness}_p95_ms={p95:0.00}");
            Console.WriteLine($"pressure_path_preview_{roundness}_max_ms={timings[^1]:0.00}");
            Console.WriteLine($"pressure_path_preview_{roundness}_budget_met={(p95 <= 33).ToString().ToLowerInvariant()}");
            RequireMethod(typeof(MainForm), "CancelFreehandStroke").Invoke(form, null);
        }
        Console.WriteLine("pressure_path_curvature_and_work=ok");
    }

    private static void RunPressurePreviewReleaseRegression()
    {
        foreach (var options in new[]
        {
            PressureBrushOptions.Default,
            PressureBrushOptions.Default with { Roundness = 35, Angle = 32, Smoothing = 70 },
            PressureBrushOptions.Default with { Taper = 60, Velocity = 30, Pressure = 80 }
        })
        {
            using var form = new MainForm { ShowInTaskbar = false };
            var tabs = (WorkspaceTabs)RequireField(typeof(MainForm), "_workspaceTabs").GetValue(form)!;
            tabs.SelectedView = WorkspaceView.BasicDrawing;
            RequireMethod(typeof(MainForm), "SwitchDrawingScope").Invoke(form, [true]);
            var stage = (StageControl)RequireField(typeof(MainForm), "_stage").GetValue(form)!;
            var host = (VectorScene)RequireField(typeof(MainForm), "_scene").GetValue(form)!;
            var settings = (DrawSettings)RequireField(typeof(MainForm), "_drawSettings").GetValue(form)!;
            settings.PressureBrushOptions = options;
            RequireMethod(typeof(MainForm), "ActivateTool", [typeof(ToolMode)]).Invoke(form, [ToolMode.PressureBrush]);
            RequireField(typeof(MainForm), "_objectDrawingEnabled").SetValue(form, false);
            host.CreateEmpty();
            var start = new PointF(-2000, 0);
            RequireMethod(typeof(MainForm), "BeginFreehandStroke").Invoke(form,
                [Point.Round(stage.WorldToScreen(start.X, start.Y)), start, 0.7f]);
            RequireField(typeof(MainForm), "_freehandColor").SetValue(form, Color.FromArgb(120, Color.Coral));
            var samples = (List<PressureBrushSample>)RequireField(typeof(MainForm), "_pressureBrushSamples").GetValue(form)!;
            var points = (List<PointF>)RequireField(typeof(MainForm), "_freehandSamples").GetValue(form)!;
            var update = RequireMethod(typeof(MainForm), "UpdateFreehandPreview");
            var preview = (VectorScene)RequireField(typeof(MainForm), "_brushAreaPreviewStage").GetValue(form)!;
            foreach (var count in new[] { 31, 97, 161, 193 })
            {
                for (var index = samples.Count; index < count; index++)
                {
                    var point = new PointF(-2000 + index * 30, MathF.Sin(index * 0.055f) * 260);
                    points.Add(point);
                    samples.Add(new PressureBrushSample(point, 180 + index % 7 * 30,
                        index * 0.01f, 0.6f + MathF.Sin(index * 0.035f) * 0.2f));
                }
                RequireField(typeof(MainForm), "_shapeGradientPreviewUpdatedAt").SetValue(form, 0L);
                update.Invoke(form, null);
                AssertBrushGeometry(stage.PressureInkPieces.Count > 0
                    && stage.DragPreviewScene is null && stage.FreehandPreviewVisible
                    && preview.ObjectCount == 0 && host.ObjectCount == 0,
                    "Pressure input rebuilt editable scene geometry instead of incremental ink.");
            }
            var shown = NewBrushGeometryScene();
            var ink = (BrushStrokeBuilder.PressureInk)RequireField(typeof(MainForm), "_pressureInk").GetValue(form)!;
            ink.Commit(shown, 0, Color.FromArgb(120, Color.Coral));
            var shownIds = Enumerable.Range(0, shown.ObjectCount).ToArray();
            RequireMethod(typeof(MainForm), "CommitFreehandStroke").Invoke(form, null);
            var committedIds = Enumerable.Range(0, host.ObjectCount).ToArray();
            AssertBrushGeometrySamePaths(shown, shownIds, host, committedIds,
                "Releasing a pressure stroke refitted its displayed contours or changed its thickness.");
            AssertBrushGeometry(committedIds.All(index => Color.FromArgb(host.Argb[index]).A == 120)
                && stage.DragPreviewScene is null && !stage.FreehandPreviewVisible,
                "Pressure release changed alpha or retained a transient preview.");
            var expected = host.CreateSnapshot();
            RequireMethod(typeof(MainForm), "ResetBrushAreaPreview").Invoke(form, null);
            var restored = NewBrushGeometryScene();
            restored.RestoreSnapshot(expected);
            AssertBrushGeometrySamePaths(restored, committedIds, host, committedIds,
                "Resetting the brush preview changed the committed stroke.");
            AssertBrushGeometry(RequireMethod(typeof(MainForm), "UndoLastEdit").Invoke(form, null) is true
                && host.ObjectCount == 0,
                "The reused pressure preview did not retain one-stroke undo semantics.");
        }
        Console.WriteLine("pressure_preview_release_parity=ok");
    }

    private static void RunPressureOptionsRegression()
    {
        var options = PressureBrushOptions.Default;
        var mouse = new PressureBrushSample(PointF.Empty, 1800, 0);
        var lightPen = mouse with { TabletPressure = 0.2f };
        AssertBrushGeometry(options.Diameter(400, mouse, 0) == 400
            && Math.Abs(options.Diameter(400, lightPen, 0) - 80) < 0.001f
            && (options with { Pressure = 0 }).Diameter(400, lightPen, 0) == 400,
            "Pressure influence or full-pressure mouse fallback is incorrect.");
        AssertBrushGeometry((options with { Velocity = 100 }).Diameter(400, mouse, 100) < 400
            && (options with { Velocity = -100 }).Diameter(400, mouse, 100) > 400
            && (options with { Taper = 100 }).Diameter(400, mouse, 0)
                < (options with { Taper = 100 }).Diameter(400, mouse, 1600),
            "Velocity direction or distance-based leading taper is incorrect.");
        var target = new PointF(100, 0);
        AssertBrushGeometry(options.Stabilize(PointF.Empty, target, 1) == target
            && (options with { Stabilizer = 100 }).Stabilize(PointF.Empty, target, 1) == new PointF(75, 0)
            && (options with { Stabilizer = 100 }).Stabilize(PointF.Empty, target, 2) == new PointF(50, 0),
            "Stabilizer distance does not respect the Stage scale or its zero setting.");
        var ellipse = BrushShape.CreateTraditionalBrush(TraditionalBrushTipKind.Round, 5, 0)
            .NormalizedContour(0.5f);
        AssertBrushGeometry(ellipse.Max(point => Math.Abs(point.X)) < 0.06f,
            "Low roundness was clamped to the ordinary brush UI's 25 percent minimum.");

        var samples = Enumerable.Range(0, 145).Select(index => new PressureBrushSample(
            new PointF(index * 30, MathF.Sin(index * 0.04f) * 260), index * 10, index * 0.01f,
            0.6f + MathF.Sin(index * 0.025f) * 0.2f)).ToArray();
        options = options with { Taper = 60, Velocity = 40, Pressure = 75, Smoothing = 70 };
        var complete = new List<PressureBrushPoint>();
        FreehandStrokeProcessor.UpdatePressurePreviewProfile(samples, 300, complete, [], options);
        var incremental = new List<PressureBrushPoint>();
        var distances = new List<float>();
        for (var count = 1; count <= samples.Length; count++)
            FreehandStrokeProcessor.UpdatePressurePreviewProfile(samples[..count], 300, incremental, distances, options);
        AssertBrushGeometry(complete.SequenceEqual(incremental),
            "Pressure options changed the completed prefix when new samples arrived.");
        var shape = BrushShape.CreateTraditionalBrush(TraditionalBrushTipKind.Round, 40, 32);
        var committed = NewBrushGeometryScene();
        var preview = NewBrushGeometryScene();
        var committedObjects = BrushStrokeBuilder.AddPressureStroke(
            committed, 0, samples, 300, Color.Coral, shape, 70, 25, options: options);
        var tapered = BrushStrokeBuilder.ProcessPressureTrajectory(incremental, 70);
        options.ApplyEndTaper(tapered, 300);
        AssertBrushGeometry(tapered[^1].Diameter < tapered[tapered.Length / 2].Diameter,
            "Taper did not narrow the end of the stroke.");
        var previewObjects = BrushStrokeBuilder.AddPressureProfile(preview, 0,
            tapered, Color.Coral, shape,
            70, 25, trajectoryPrepared: true);
        AssertBrushGeometrySamePaths(committed, committedObjects, preview, previewObjects,
            "Pressure options produced different preview and committed paths.");
        Console.WriteLine("pressure_options_regression=ok");
    }

    private static void RunPressurePreviewStablePrefixRegression()
    {
        using var form = new MainForm { ShowInTaskbar = false };
        var source = (List<PressureBrushPoint>)RequireField(typeof(MainForm), "_brushAreaPressureProfile").GetValue(form)!;
        var process = RequireMethod(typeof(MainForm), "ProcessBrushPressurePreview");
        var stable = RequireField(typeof(MainForm), "_brushPreviewPressureStableCount");
        var clear = RequireMethod(typeof(MainForm), "ClearBrushPreviewTrajectory");
        var samples = Enumerable.Range(0, 1537).Select(index => new PressureBrushPoint(
            new PointF(index * 50, MathF.Sin(index * 0.013f) * 200 + (index % 2 == 0 ? 12 : -12)),
            300 + MathF.Sin(index * 0.021f) * 40 + (index % 2 == 0 ? 30 : -30))).ToArray();
        int[] batches = [1, 7, 31, 129];
        foreach (var smoothing in new[] { 0, 12, 13, 38, 63, 88, 100 })
        {
            clear.Invoke(form, null);
            source.Clear();
            RequireField(typeof(MainForm), "_freehandBrushSmoothing").SetValue(form, smoothing);
            var previous = Array.Empty<PressureBrushPoint>();
            var previousStableCount = 0;
            var batch = 0;
            while (source.Count < samples.Length)
            {
                source.AddRange(samples.Skip(source.Count).Take(batches[batch++ % batches.Length]));
                var actual = (PressureBrushPoint[])process.Invoke(form, null)!;
                var expected = BrushStrokeBuilder.ProcessPressureTrajectory(source, smoothing);
                AssertBrushGeometry(actual.AsSpan().SequenceEqual(expected),
                    "Incremental pressure smoothing differed from complete processing at a window boundary.");
                AssertBrushGeometry(actual.Take(previousStableCount).SequenceEqual(previous.Take(previousStableCount)),
                    "Extending a continuous pressure stroke moved an already completed position or width.");
                AssertBrushGeometry(actual.Length == source.Count && actual[0] == source[0] && actual[^1] == source[^1],
                    "Pressure smoothing dropped a sample, shifted its pressure knot or delayed the current endpoint.");
                previous = actual;
                previousStableCount = (int)stable.GetValue(form)!;
                AssertBrushGeometry(source.Count - previousStableCount <= 25,
                    "Pressure preview retained an unbounded refitting tail.");
            }
            for (var change = 0; change < 8; change++)
            {
                source[^1] = source[^1] with { Diameter = change % 2 == 0 ? 90 : 420 };
                var actual = (PressureBrushPoint[])process.Invoke(form, null)!;
                AssertBrushGeometry(actual.Take(previousStableCount).SequenceEqual(previous.Take(previousStableCount))
                    && actual.AsSpan().SequenceEqual(BrushStrokeBuilder.ProcessPressureTrajectory(source, smoothing)),
                    "Changing pressure without moving the pen made completed stroke samples oscillate.");
                previous = actual;
            }
            if (smoothing == 100)
            {
                var rawEnergy = PressureProfileNoise(samples);
                var smoothEnergy = PressureProfileNoise(BrushStrokeBuilder.ProcessPressureTrajectory(samples, smoothing));
                AssertBrushGeometry(smoothEnergy.Position < rawEnergy.Position * 0.25
                    && smoothEnergy.Width < rawEnergy.Width * 0.25,
                    "Pressure smoothing did not attenuate alternating centreline and width jitter.");
            }
        }
        clear.Invoke(form, null);
        source.Clear();
        source.Add(new PressureBrushPoint(new PointF(-5000, 8000), 120));
        AssertBrushGeometry(((PressureBrushPoint[])process.Invoke(form, null)!).SequenceEqual(source)
            && (int)stable.GetValue(form)! == 0,
            "Starting another pressure stroke retained the previous stroke's filtered prefix.");

        static (double Position, double Width) PressureProfileNoise(IReadOnlyList<PressureBrushPoint> profile)
        {
            double position = 0;
            double width = 0;
            for (var index = 16; index < profile.Count - 16; index++)
            {
                var positionDelta = profile[index - 1].Point.Y - 2 * profile[index].Point.Y + profile[index + 1].Point.Y;
                var widthDelta = profile[index - 1].Diameter - 2 * profile[index].Diameter + profile[index + 1].Diameter;
                position += positionDelta * positionDelta;
                width += widthDelta * widthDelta;
            }
            return (position, width);
        }
    }

    private static void RunPressurePreviewTailReplacementRegression()
    {
        using var form = new MainForm { ShowInTaskbar = false };
        var samples = (List<PressureBrushSample>)RequireField(typeof(MainForm), "_pressureBrushSamples").GetValue(form)!;
        var preview = (VectorScene)RequireField(typeof(MainForm), "_brushAreaPreviewStage").GetValue(form)!;
        var host = (VectorScene)RequireField(typeof(MainForm), "_scene").GetValue(form)!;
        var build = RequireMethod(typeof(MainForm), "BuildPressureBrushAreaPreview");
        var clear = RequireMethod(typeof(MainForm), "ClearBrushPreviewTrajectory");
        foreach (var shape in new[]
        {
            BrushShape.CreateTraditionalBrush(TraditionalBrushTipKind.Round, 225, 37),
            BrushShape.CreateTraditionalBrush(TraditionalBrushTipKind.Square, 150, 23)
        })
        {
            clear.Invoke(form, null);
            RequireMethod(typeof(MainForm), "ResetBrushAreaPreview").Invoke(form, null);
            RequireField(typeof(MainForm), "_brushShape").SetValue(form, shape);
            RequireField(typeof(MainForm), "_freehandStrokeUnits").SetValue(form, 400f);
            RequireField(typeof(MainForm), "_freehandBrushSmoothing").SetValue(form, 0);
            RequireField(typeof(MainForm), "_freehandPencilWorldPerPixel").SetValue(form, 25f);
            RequireField(typeof(MainForm), "_freehandColor").SetValue(form, Color.FromArgb(120, Color.Coral));
            samples.Clear();
            samples.AddRange(Enumerable.Range(0, 140).Select(index => new PressureBrushSample(
                new PointF(index == 139 ? 20000 : index * 100, 0), 200, index * 0.01f, index == 139 ? 1 : 0.2f)));
            var hostRevision = host.GeometryRevision;
            var thick = (int[])build.Invoke(form, null)!;
            preview.MergeSameColorFillsAroundNewObjects(thick, connectNearby: false, frame: 0);
            var tip = shape.NormalizedContour(0.5f).MaxBy(point => point.X);
            var probe = new PointF(samples[^1].Point.X + tip.X * 75, tip.Y * 75);
            AssertBrushGeometry(BrushGeometryContains(preview, Enumerable.Range(0, preview.ObjectCount).ToArray(), probe),
                "The pressure-tail fixture did not include its initial broad tip.");
            samples[^1] = samples[^1] with { TabletPressure = 0.2f };
            var thin = (int[])build.Invoke(form, null)!;
            preview.MergeSameColorFillsAroundNewObjects(thin, connectNearby: false, frame: 0);
            var objects = Enumerable.Range(0, preview.ObjectCount).ToArray();
            AssertBrushGeometry(!BrushGeometryContains(preview, objects, probe)
                && BrushGeometryContains(preview, objects, samples[12].Point)
                && host.GeometryRevision == hostRevision
                && (int)RequireField(typeof(MainForm), "_brushPreviewPressureSealedCount").GetValue(form)! >= 64,
                "Replacing a pressure tail kept an obsolete wide imprint, lost the sealed prefix or mutated artwork.");
            AssertBrushGeometry(objects.All(index => Color.FromArgb(preview.Argb[index]).A == 120),
                "Replacing a shape-tip preview changed its intended opacity.");
        }
        RequireMethod(typeof(MainForm), "CancelFreehandStroke").Invoke(form, null);
        AssertBrushGeometry(RequireField(typeof(MainForm), "_brushPressureGeometry").GetValue(form) is null,
            "Cancelling a pressure stroke retained its sealed preview scene.");
    }

    private static void RunPressurePreviewCommitParityRegression()
    {
        var samples = Enumerable.Range(0, 193).Select(index => new PressureBrushSample(
            new PointF(index * 30, MathF.Sin(index * 0.04f) * 260), 200 + index % 7 * 15, index * 0.01f,
            0.6f + MathF.Sin(index * 0.025f) * 0.2f + (index % 2 == 0 ? 0.06f : -0.06f))).ToArray();
        var raw = new List<PressureBrushPoint>();
        FreehandStrokeProcessor.UpdatePressurePreviewProfile(samples, 300, raw, []);
        var smoothed = BrushStrokeBuilder.ProcessPressureTrajectory(raw, 70);
        foreach (var shape in new[] { BrushShape.CreateTraditionalBrush(), BrushShape.CreateSoftRound(),
            BrushShape.CreateTraditionalBrush(TraditionalBrushTipKind.Square, 150, 23) })
        {
            var committed = NewBrushGeometryScene();
            var prepared = NewBrushGeometryScene();
            var committedObjects = BrushStrokeBuilder.AddPressureStroke(
                committed, 0, samples, 300, Color.Coral, shape, 70, 25);
            var preparedObjects = BrushStrokeBuilder.AddPressureProfile(
                prepared, 0, smoothed, Color.Coral, shape, 70, 25, trajectoryPrepared: true);
            AssertBrushGeometrySamePaths(committed, committedObjects, prepared, preparedObjects,
                "Releasing a pressure stroke recomputed its width history or remapped its smoothed pressure knots.");
        }
    }
    private static void RunPressureInkRegression()
    {
        foreach (var roundness in new[] { 100, 35 })
        foreach (var varying in new[] { false, true })
        {
            var shape = BrushShape.CreateTraditionalBrush(TraditionalBrushTipKind.Round, roundness, 32);
            var ink = new BrushStrokeBuilder.PressureInk(shape);
            var input = new List<PressureBrushPoint>();
            var timings = new List<double>();
            PathBezierNode[][]? firstPiece = null;
            for (var count = 1; count <= 1025; count = Math.Min(1025, count + 16))
            {
                while (input.Count < count)
                {
                    var index = input.Count;
                    var angle = index * MathF.Tau / 1024;
                    // Closed loop and variable pressure include a hole and narrow tip.
                    input.Add(new(new PointF(MathF.Cos(angle) * 4000, MathF.Sin(angle) * 4000),
                        varying ? 240 + 140 * MathF.Sin(angle * 3) : 300));
                }
                var start = Stopwatch.GetTimestamp();
                ink.Update(input, Math.Max(0, count - 25), 100, 20);
                timings.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                if (firstPiece is not null)
                    AssertBrushGeometry(ReferenceEquals(firstPiece, ink.Pieces[0]), "Completed pressure ink was rebuilt.");
                if (ink.SealedCount > 0) firstPiece ??= ink.Pieces[0];
                if (count == 1025) break;
            }
            var work = ink.SweptSampleCount;
            var last = ink.Pieces[^1];
            ink.Update(input, 1000, 100, 20);
            AssertBrushGeometry(ink.SweptSampleCount == work && ReferenceEquals(last, ink.Pieces[^1])
                && work < input.Count * 8L, "Pressure ink repeated unchanged input or swept its completed prefix.");
            var scene = NewBrushGeometryScene();
            var ids = ink.Commit(scene, 0, Color.FromArgb(120, Color.Coral));
            AssertBrushGeometry(ids.Length == 1 && !scene.FillContainsPoint(ids[0], PointF.Empty),
                "Pressure ink commit lost its hole or split the fill.");
            scene.TryGetPathBezierWorldContours(ids[0], out var committed);
            using var before = Raster(ink.Pieces.SelectMany(piece => piece));
            using var after = Raster(committed);
            var changed = 0;
            for (var y = 1; y < before.Height - 1; y++)
            for (var x = 1; x < before.Width - 1; x++)
            {
                var a = before.GetPixel(x, y).A;
                var b = after.GetPixel(x, y).A;
                AssertBrushGeometry(a <= 122 && b <= 122, "Overlapping pressure pieces accumulated alpha.");
                if ((a > 60) == (b > 60)) continue;
                changed++;
                var matched = false;
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                    matched |= (before.GetPixel(x + dx, y + dy).A > 60) == (b > 60);
                var reverseMatched = false;
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                    reverseMatched |= (after.GetPixel(x + dx, y + dy).A > 60) == (a > 60);
                AssertBrushGeometry(matched && reverseMatched, "Pressure release moved a visible edge by more than one screen pixel.");
            }
            AssertBrushGeometry(before.GetPixel(300, 300).A == 0 && after.GetPixel(300, 300).A == 0,
                "Pressure winding preview filled the loop's hole.");
            timings.Sort();
            var p95 = timings[(int)Math.Ceiling(timings.Count * .95) - 1];
            Console.WriteLine($"pressure_ink_{roundness}_{varying}_p95_ms={p95:0.00},max_ms={timings[^1]:0.00},edge_pixels={changed}");
            Console.WriteLine($"pressure_ink_{roundness}_{varying}_budget_met={(p95 <= 16.67).ToString().ToLowerInvariant()}");
        }
        // Reverse turns and crossing strokes must retain a single alpha deposit.
        var crossing = new BrushStrokeBuilder.PressureInk(BrushShape.CreateTraditionalBrush());
        var crossProfile = Enumerable.Range(0, 513).Select(i =>
        {
            var angle = i * MathF.Tau / 512;
            return new PressureBrushPoint(new PointF(MathF.Sin(angle) * 4000, MathF.Sin(angle * 2) * 3000), 400);
        }).ToArray();
        for (var count = 1; count <= crossProfile.Length; count = Math.Min(crossProfile.Length, count + 16))
        {
            crossing.Update(crossProfile.Take(count).ToArray(), Math.Max(0, count - 25), 100, 20);
            if (count == crossProfile.Length) break;
        }
        using (var bitmap = Raster(crossing.Pieces.SelectMany(piece => piece)))
            AssertBrushGeometry(Math.Abs(bitmap.GetPixel(300, 300).A - 120) <= 2,
                "A self-crossing pressure stroke accumulated alpha or cut a hole at the crossing.");
        RunPressureInkRenderingRegression(crossing);
        Console.WriteLine("pressure_incremental_ink=ok,alpha=single_fill,release_edge_error=1px,crossing=preserved");

        static Bitmap Raster(IEnumerable<PathBezierNode[]> contours)
        {
            var bitmap = new Bitmap(600, 600, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = new GraphicsPath(FillMode.Winding);
            foreach (var contour in contours)
            {
                path.StartFigure();
                for (var i = 0; i < contour.Length; i++)
                {
                    var current = contour[i];
                    var next = contour[(i + 1) % contour.Length];
                    path.AddBezier(Screen(current.Anchor), Screen(current.OutgoingControl),
                        Screen(next.IncomingControl), Screen(next.Anchor));
                }
                path.CloseFigure();
            }
            using var brush = new SolidBrush(Color.FromArgb(120, Color.Coral));
            graphics.FillPath(brush, path);
            return bitmap;
            static PointF Screen(PointF p) => new(p.X / 20 + 300, p.Y / 20 + 300);
        }
    }
    private static void RunPressureInkRenderingRegression(BrushStrokeBuilder.PressureInk ink)
    {
        using var stage = new StageControl(NewBrushGeometryScene()) { Size = new Size(600, 600) };
        stage.RestoreViewState(stage.CaptureViewState() with { CameraX = 0, CameraY = 0, Zoom = 1, CanvasRotation = 0 });
        stage.SetPressureInkPreview(ink.Pieces, Color.FromArgb(120, Color.Coral));
        using var renderer = new Direct2DStageRenderer();
        var factory = Vortice.Direct2D1.D2D1.D2D1CreateFactory<Vortice.Direct2D1.ID2D1Factory1>(
            Vortice.Direct2D1.FactoryType.SingleThreaded, Vortice.Direct2D1.DebugLevel.None);
        RequireField(typeof(Direct2DStageRenderer), "_factory").SetValue(renderer, factory);
        var target = factory.CreateDCRenderTarget(new Vortice.Direct2D1.RenderTargetProperties(
            Vortice.Direct2D1.RenderTargetType.Software,
            new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Ignore),
            96, 96, Vortice.Direct2D1.RenderTargetUsage.None, Vortice.Direct2D1.FeatureLevel.Default));
        RequireField(typeof(Direct2DStageRenderer), "_target").SetValue(renderer, target);
        var committedScene = stage.Scene;
        var committedIds = ink.Commit(committedScene, 0, Color.FromArgb(120, Color.Coral));
        var drawObject = RequireMethod(typeof(Direct2DStageRenderer), "DrawObject",
            [typeof(StageControl), typeof(int), typeof(SceneRenderPass)]);
        var draw = RequireMethod(typeof(Direct2DStageRenderer), "DrawPressureInkPreview");
        var gdiDraw = RequireMethod(typeof(StageControl), "DrawPressureInkPreview");
        foreach (var angle in new[] { 0f, 0f, .7f })
        {
            stage.SetCanvasRotation(angle);
            using var d2d = new Bitmap(600, 600);
            using (var graphics = Graphics.FromImage(d2d))
            {
                graphics.Clear(Color.White);
                var hdc = graphics.GetHdc();
                try
                {
                    target.BindDC(hdc, new Vortice.RawRect(0, 0, 600, 600));
                    target.BeginDraw();
                    target.Clear(new Vortice.Mathematics.Color4(1, 1, 1, 1));
                    AssertBrushGeometry(draw.Invoke(renderer, [stage]) is true, "Direct2D pressure ink was not drawn.");
                    target.EndDraw().CheckError();
                }
                finally { graphics.ReleaseHdc(hdc); }
            }
            using var gdi = new Bitmap(600, 600);
            using (var graphics = Graphics.FromImage(gdi))
            {
                graphics.Clear(Color.White);
                gdiDraw.Invoke(stage, [graphics]);
            }
            using var final = new Bitmap(600, 600);
            using (var graphics = Graphics.FromImage(final))
            {
                var hdc = graphics.GetHdc();
                try
                {
                    target.BindDC(hdc, new Vortice.RawRect(0, 0, 600, 600));
                    target.BeginDraw();
                    target.Clear(new Vortice.Mathematics.Color4(1, 1, 1, 1));
                    foreach (var index in committedIds) drawObject.Invoke(renderer, [stage, index, SceneRenderPass.Fill]);
                    target.EndDraw().CheckError();
                }
                finally { graphics.ReleaseHdc(hdc); }
            }
            for (var y = 1; y < 599; y++)
            for (var x = 1; x < 599; x++)
            {
                var liveInside = d2d.GetPixel(x, y).G < 225;
                var finalInside = final.GetPixel(x, y).G < 225;
                if (liveInside == finalInside) continue;
                var beforeMatch = false;
                var afterMatch = false;
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    beforeMatch |= (d2d.GetPixel(x + dx, y + dy).G < 225) == finalInside;
                    afterMatch |= (final.GetPixel(x + dx, y + dy).G < 225) == liveInside;
                }
                AssertBrushGeometry(beforeMatch && afterMatch, "Native committed Path moved the pressure edge beyond one pixel.");
            }
            foreach (var point in new[] { PointF.Empty, new PointF(4000, 0), new PointF(-4000, 0) })
            {
                var screen = stage.WorldToScreen(point.X, point.Y);
                var a = d2d.GetPixel((int)screen.X, (int)screen.Y);
                var b = gdi.GetPixel((int)screen.X, (int)screen.Y);
                AssertBrushGeometry(Math.Abs(a.G - b.G) <= 3 && a.G < 220,
                    $"Direct2D/GDI pressure coverage diverged at {point}, rotation={angle}: D2D={a}, GDI={b}, screen={screen}.");
            }
        }
        stage.ClearFreehandPreview();
        AssertBrushGeometry(draw.Invoke(renderer, [stage]) is false && stage.PressureInkPieces.Count == 0,
            "Cancel retained pressure ink or renderer resources.");
        Console.WriteLine("pressure_ink_rendering=ok,direct2d_dc=gdi,rotation=invalidated,cancel=cleared");
    }

}
