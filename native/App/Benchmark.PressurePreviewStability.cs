namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunPressurePreviewStabilityRegression()
    {
        RunPressurePreviewStablePrefixRegression();
        RunPressurePreviewTailReplacementRegression();
        RunPressurePreviewCommitParityRegression();
        Console.WriteLine("pressure_preview_stability=ok");
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
                AssertBrushGeometry(source.Count - previousStableCount <= 9,
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
        AssertBrushGeometry(RequireField(typeof(MainForm), "_brushPreviewPressureSealedScene").GetValue(form) is null,
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
}
