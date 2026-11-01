using System.Diagnostics;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunMarqueeCurvePerformanceRegression(bool verifyRendering = false)
    {
        RunPolylineMatchIndexRegression();
        RunDenseCurveMarqueeMaterializationRegression();
        RunDenseCurveMarqueePointerRegression(verifyRendering);
        Console.WriteLine("dense_curve_partial_marquee=ok");
    }

    private static void RunPolylineMatchIndexRegression()
    {
        const float tolerance = 2;
        var polylines = Enumerable.Range(0, 1024).Select(index => Enumerable.Range(0, 5)
            .Select(sample => new PointF(index % 32 * 60 + sample * 8,
                index / 32 * 60 + MathF.Sin(index * 0.17f + sample * 0.4f) * 5)).ToArray()).ToArray();
        polylines[1] = polylines[0].Reverse().ToArray();
        var matchIndex = new PolylineMatchIndex(polylines, tolerance);
        var candidates = new List<int>();
        long candidateCount = 0;
        var random = new Random(7139);
        for (var query = 0; query < 512; query++)
        {
            var source = polylines[query % polylines.Length];
            var offset = query % 3 == 0 ? tolerance : (float)(random.NextDouble() * 4.1 - 2.05);
            var start = new PointF(source[0].X, source[0].Y + offset);
            var end = query % 7 == 0 ? start : new PointF(source[1].X, source[1].Y + offset);
            matchIndex.FindCandidates(start, end, candidates);
            AssertTimeline(candidates.SequenceEqual(candidates.Order()), "The curve index changed equal-score source ordering.");
            AssertTimeline(Resolve(Enumerable.Range(0, polylines.Length)) == Resolve(candidates),
                "Spatial curve matching disagreed with the original exhaustive distance search.");
            candidateCount += candidates.Count;

            int Resolve(IEnumerable<int> sources)
            {
                var midpoint = new PointF((start.X + end.X) * 0.5f, (start.Y + end.Y) * 0.5f);
                var best = -1;
                var bestScore = float.MaxValue;
                foreach (var sourceIndex in sources)
                {
                    var startDistance = MarqueeMatchDistance(start, polylines[sourceIndex]);
                    var endDistance = MarqueeMatchDistance(end, polylines[sourceIndex]);
                    var midpointDistance = MarqueeMatchDistance(midpoint, polylines[sourceIndex]);
                    if (Math.Max(startDistance, Math.Max(endDistance, midpointDistance)) > tolerance) continue;
                    var score = startDistance + endDistance + midpointDistance;
                    if (score >= bestScore) continue;
                    best = sourceIndex;
                    bestScore = score;
                }
                return best;
            }
        }
        AssertTimeline(candidateCount < 512L * polylines.Length / 20,
            "Curve matching still scanned most distant source curves.");
        matchIndex.FindCandidates(new PointF(float.NaN, 0), PointF.Empty, candidates);
        AssertTimeline(candidates.Count == polylines.Length, "Nonfinite queries did not preserve the exhaustive fallback.");
        new PolylineMatchIndex([], tolerance).FindCandidates(PointF.Empty, PointF.Empty, candidates);
        AssertTimeline(candidates.Count == 0, "An empty curve index retained stale candidates.");

        PointF[][] boundaries = [[new(0, 0), new(10, 0)], [new(10, 0), new(0, 0)], [new(20, 20)], []];
        var boundaryIndex = new PolylineMatchIndex(boundaries, tolerance);
        boundaryIndex.FindCandidates(new PointF(10, 2), new PointF(0, 2), candidates);
        AssertTimeline(candidates.Contains(0) && candidates.Contains(1),
            "The curve index rejected an inclusive tolerance boundary or reversed edge.");
        boundaryIndex.FindCandidates(new PointF(22, 20), new PointF(22, 20), candidates);
        AssertTimeline(candidates.Contains(2), "A single-point polyline disappeared at the tolerance boundary.");
        PointF[][] translated = [[new(100000000, 100000000), new(100000032, 100000000)]];
        new PolylineMatchIndex(translated, tolerance).FindCandidates(translated[0][0], translated[0][1], candidates);
        AssertTimeline(candidates.SequenceEqual(new[] { 0 }), "Large-coordinate curve matching lost a valid edge.");
        Console.WriteLine($"marquee_curve_index_candidates={candidateCount},exhaustive_candidates={512L * polylines.Length}");
    }

    private static float MarqueeMatchDistance(PointF point, IReadOnlyList<PointF> polyline)
    {
        var distance = float.MaxValue;
        for (var segment = 0; segment < Math.Max(1, polyline.Count - 1) && polyline.Count > 0; segment++)
        {
            var start = polyline[segment];
            var end = polyline[Math.Min(segment + 1, polyline.Count - 1)];
            var deltaX = end.X - start.X;
            var deltaY = end.Y - start.Y;
            var squared = deltaX * deltaX + deltaY * deltaY;
            var amount = squared <= 0.0001f ? 0
                : Math.Clamp(((point.X - start.X) * deltaX + (point.Y - start.Y) * deltaY) / squared, 0, 1);
            var offsetX = point.X - (start.X + deltaX * amount);
            var offsetY = point.Y - (start.Y + deltaY * amount);
            distance = Math.Min(distance, MathF.Sqrt(offsetX * offsetX + offsetY * offsetY));
        }
        return distance;
    }

    private static void RunDenseCurveMarqueeMaterializationRegression()
    {
        var scene = new VectorScene();
        var source = BuildDenseMarqueeCurveScene(scene);
        var original = scene.CreateSnapshot();
        var color = scene.Argb[source];
        var order = scene.ObjectOrder[source];
        var bounds = new RectangleF(-8000, -240, 14000, 480);
        var samples = Enumerable.Range(0, 91).SelectMany(index => new[] { -180, -80, 0, 80, 180 }
            .Select(offset => new PointF(-11500 + index * 253, MathF.Sin((-11500 + index * 253) * 0.004f) * 220 + offset)))
            .Where(point => Math.Abs(point.Y - bounds.Top) > 4 && Math.Abs(point.Y - bounds.Bottom) > 4)
            .Select(point => (Point: point, Inside: scene.FillContainsPoint(source, point))).ToArray();
        var times = new List<double>();
        for (var iteration = 0; iteration < 3; iteration++)
        {
            scene.RestoreSnapshot(original);
            var started = Stopwatch.GetTimestamp();
            var result = scene.MaterializeMarqueeSelectionParts(bounds, 0);
            times.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            AssertTimeline(result.Success && result.Changed && result.SelectedObjects.Length > 0
                && result.OldToNewObjectIndex[source] == -1 && result.OldToNewObjectIndex[1] >= 0,
                "Dense partial marquee selection did not retain its exact split and index remap.");
            var fills = Enumerable.Range(0, scene.ObjectCount).Where(owner => scene.ObjectOrder[owner] == order).ToArray();
            AssertTimeline(fills.Length == 2 && fills.All(owner => scene.Argb[owner] == color
                && scene.ObjectLayer[owner] == 0 && scene.ObjectKeyframeFrame[owner] == 0
                && scene.TryGetPathBezierWorldContours(owner, out _)
                && scene.GetGradientKind(owner) == GradientKind.Linear),
                "Dense partial selection lost Beziers, alpha, gradient, stack, layer or Cel ownership.");
            foreach (var sample in samples)
            {
                AssertTimeline(fills.Any(owner => scene.FillContainsPoint(owner, sample.Point)) == sample.Inside,
                    "Indexed marquee materialization changed visible coverage.");
                if (sample.Inside)
                    AssertTimeline(result.SelectedObjects.Any(owner => scene.FillContainsPoint(owner, sample.Point))
                        == bounds.Contains(sample.Point), "Partial selection selected geometry outside the marquee.");
            }
            AssertTimeline(!fills.Any(owner => scene.FillContainsPoint(owner, PointF.Empty)),
                "Dense partial selection filled an existing compound-path hole.");
        }
        times.Sort();
        Console.WriteLine($"dense_marquee_materialize_average_ms={times.Average():F3},p95_ms={times[^1]:F3},maximum_ms={times[^1]:F3},budget_ms={LargeMarqueeQueryBudgetMilliseconds:F3}");
        AssertTimeline(times[^1] <= LargeMarqueeQueryBudgetMilliseconds,
            $"Dense partial materialization exceeded the existing marquee budget: {times[^1]:F1} ms.");
    }

    private static int BuildDenseMarqueeCurveScene(VectorScene scene)
    {
        const int segments = 1536;
        const float step = 24000f / segments;
        scene.CreateEmpty();
        var contour = new List<PathBezierNode>();
        foreach (var direction in new[] { 1, -1 })
        for (var sample = 0; sample <= segments; sample++)
        {
            var position = direction > 0 ? sample : segments - sample;
            var horizontal = -12000 + position * step;
            var vertical = MathF.Sin(horizontal * 0.004f) * 220 + direction * 120;
            var anchor = new PointF(horizontal, vertical);
            var tangent = new PointF(direction * step / 3, direction * step / 3 * 0.88f * MathF.Cos(horizontal * 0.004f));
            contour.Add(new PathBezierNode(anchor,
                sample == 0 ? anchor : new PointF(anchor.X - tangent.X, anchor.Y - tangent.Y),
                sample == segments ? anchor : new PointF(anchor.X + tangent.X, anchor.Y + tangent.Y)));
        }
        PointF[] hole = [new(-20, -20), new(20, -20), new(20, 20), new(-20, 20)];
        var source = scene.AppendPathBezierObjectContours(0,
            [contour.ToArray(), hole.Select(point => new PathBezierNode(point, point, point)).ToArray()],
            0, Color.FromArgb(160, Color.MediumTurquoise), Color.Transparent, (uint)contour.Count);
        scene.AddObject(0, new PointF(30000, 0), new SizeF(400, 400), 0, 0, Color.Gold, Color.Transparent, 4, ShapeKind.Rectangle);
        scene.SetLinearGradient(source, Color.FromArgb(160, Color.MediumTurquoise), Color.FromArgb(160, Color.CornflowerBlue),
            new PointF(-12000, 0), new PointF(12000, 0));
        scene.CompleteDeferredBuild();
        return source;
    }

    private static void RunDenseCurveMarqueePointerRegression(bool verifyRendering)
    {
        using var form = new MainForm { ShowInTaskbar = false, Size = new Size(1280, 800) };
        var tabs = (WorkspaceTabs)RequireField(typeof(MainForm), "_workspaceTabs").GetValue(form)!;
        tabs.SelectedView = WorkspaceView.BasicDrawing;
        var scene = (VectorScene)RequireField(typeof(MainForm), "_scene").GetValue(form)!;
        var stage = (StageControl)RequireField(typeof(MainForm), "_stage").GetValue(form)!;
        var source = BuildDenseMarqueeCurveScene(scene);
        var originalCount = scene.ObjectCount;
        var originalContours = scene.TryGetPathBezierWorldContours(source, out var contours) ? contours : [];
        RequireMethod(typeof(MainForm), "ResetCanvasUndoHistory").Invoke(form, null);
        RequireMethod(typeof(MainForm), "ActivateTool", [typeof(ToolMode)]).Invoke(form, [ToolMode.Select]);
        if (verifyRendering)
        {
            form.Show();
            Application.DoEvents();
        }
        else stage.Size = new Size(1000, 600);
        stage.SetVisibleWorldWidth(30000);
        var start = Point.Round(stage.WorldToScreen(-8000, -240));
        var end = Point.Round(stage.WorldToScreen(6000, 240));
        RequireField(typeof(MainForm), "_marqueeStart").SetValue(form, (Point?)start);
        RequireField(typeof(MainForm), "_marqueeSelecting").SetValue(form, true);
        var started = Stopwatch.GetTimestamp();
        RequireMethod(typeof(MainForm), "CompleteMarqueeSelection").Invoke(form, [end]);
        var selectionMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (verifyRendering) PaintStage(stage);
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        AssertTimeline(((List<int>)RequireField(typeof(MainForm), "_selectedObjects").GetValue(form)!).Count > 0
            && !(bool)RequireField(typeof(MainForm), "_marqueeSelecting").GetValue(form)!,
            "Dense partial marquee did not present a completed selection.");
        Console.WriteLine($"dense_marquee_ui_ms={elapsed:F3},selection_ms={selectionMilliseconds:F3},paint_ms={elapsed - selectionMilliseconds:F3},includes_present={verifyRendering},d2d={stage.LastFrameUsedDirect2D},budget_ms={LargeMarqueeQueryBudgetMilliseconds:F3}");
        AssertTimeline(elapsed <= LargeMarqueeQueryBudgetMilliseconds,
            $"Partial brush selection feedback exceeded the existing marquee budget: {elapsed:F1} ms.");
        if (verifyRendering)
        {
            Application.DoEvents();
            var pending = Convert.ToInt32(RequireField(typeof(MainForm), "_pendingPresentationRefresh").GetValue(form));
            AssertTimeline((pending & 1) == 0
                && !(bool)RequireField(typeof(MainForm), "_objectInspectorSelectionPending").GetValue(form)!,
                "Dense partial selection did not complete its inspector refresh after presenting the selection.");
        }
        AssertTimeline(RequireMethod(typeof(MainForm), "UndoLastEdit").Invoke(form, null) is true
            && scene.ObjectCount == originalCount && scene.TryGetPathBezierWorldContours(source, out var restored)
            && restored.Length == originalContours.Length
            && restored.Zip(originalContours).All(pair => pair.First.AsSpan().SequenceEqual(pair.Second)),
            "Accelerated partial selection changed the existing single-step geometry undo.");
    }
}
