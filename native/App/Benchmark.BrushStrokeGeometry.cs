using System.Diagnostics;
using Clipper2Lib;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    internal static void RunBrushStrokeGeometryRegression()
    {
        RunBrushGeometryCurvedTips();
        RunBrushGeometrySmoothing();
        RunBrushGeometrySinglePoints();
        RunBrushGeometryPressure();
        RunBrushGeometryPreparedProfileAndTrajectory();
        RunBrushGeometryStampFrequency();
        RunBrushGeometryHolesAndSnapshots();
        RunBrushGeometryInvalidInput();
        RunBrushPreviewTrajectoryCacheRegression();
        RunFittedPolylineDistanceIndexRegression();
        RunBrushPolylineDistanceIndexRegression();
        RunBezierPointSamplingEquivalenceRegression();
        RunSerialSummaryPoolRegression();
        RunSpatialIndexResetRegression();
        RunParallelBrushGeometryRegression();
        RunBrushUnionPoolRegression();
    }

    private static void RunBrushUnionPoolRegression()
    {
        var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
        var union = RequireMethod(typeof(BrushStrokeBuilder), "Union", [typeof(Paths64), typeof(FillRule)], flags);
        Parallel.For(0, 32, iteration =>
        {
            var offset = iteration * 4000L;
            var outer = new Path64
            {
                new(offset, 0), new(offset + 1000, 0), new(offset + 1000, 1000), new(offset, 1000)
            };
            var inner = new Path64
            {
                new(offset + 200, 200), new(offset + 800, 200),
                new(offset + 800, 800), new(offset + 200, 800)
            };
            if (iteration % 2 == 0) inner.Reverse();
            var input = new Paths64 { outer, inner };
            foreach (var rule in new[] { FillRule.NonZero, FillRule.EvenOdd })
            {
                var expected = Clipper.Union(input, rule);
                var actual = (Paths64)union.Invoke(null, [input, rule])!;
                AssertBrushGeometry(expected.Count == actual.Count
                    && expected.Zip(actual).All(pair => pair.First.SequenceEqual(pair.Second)),
                    "Reusing a brush union worker changed winding, holes or exact output vertices.");
                var empty = (Paths64)union.Invoke(null, [new Paths64(), rule])!;
                AssertBrushGeometry(empty.Count == 0, "A brush union worker retained another stroke's geometry.");
            }
        });
        var pooled = (int)RequireField(typeof(BrushStrokeBuilder), "_pooledUnionWorkerCount", flags).GetValue(null)!;
        AssertBrushGeometry(pooled >= 0 && pooled <= 8, "The brush union worker pool exceeded its retention limit.");
        Console.WriteLine("brush_union_pool=ok,parallel=isolated,winding=exact,holes=preserved,empty=cleared,retention=bounded");
    }

    private static void RunParallelBrushGeometryRegression()
    {
        var defaultWorkers = ParallelBatch.MaximumWorkerCount;
        using (ParallelBatch.PushWorkerLimit(1))
            AssertBrushGeometry(ParallelBatch.MaximumWorkerCountFor(12) == 1,
                "Brush parallel preparation ignored the enclosing serial worker limit.");
        AssertBrushGeometry(ParallelBatch.MaximumWorkerCount == defaultWorkers
            && ParallelBatch.MaximumWorkerCountFor(12) <= (Environment.ProcessorCount <= 1
                ? 1 : Math.Min(12, Math.Max(2, Environment.ProcessorCount - 1))),
            "Brush parallel preparation changed the default worker policy or exceeded the hardware worker limit.");
        var path = Enumerable.Range(0, 1024).Select(index => new PointF(
            -25000 + index * (50000f / 1023), MathF.Sin(index * MathF.Tau * 18 / 1023) * 750)).ToArray();
        var serial = NewBrushGeometryScene();
        int[] serialObjects;
        using (ParallelBatch.PushWorkerLimit(1))
            serialObjects = BrushStrokeBuilder.AddStroke(serial, 0, path, 200, Color.Coral,
                BrushShape.CreateSoftRound(), 70, 25);
        var parallel = NewBrushGeometryScene();
        var parallelObjects = BrushStrokeBuilder.AddStroke(parallel, 0, path, 200, Color.Coral,
            BrushShape.CreateSoftRound(), 70, 25);
        AssertBrushGeometrySamePaths(serial, serialObjects, parallel, parallelObjects,
            "Parallel brush fitting changed exact editable contours or their ordering.");
        AssertBrushGeometry(serialObjects.Length == 3 && parallelObjects.Length == 3,
            "Parallel brush fitting lost an opacity layer.");
        for (var index = 0; index < serialObjects.Length; index++)
            AssertBrushGeometry(serial.Argb[serialObjects[index]] == parallel.Argb[parallelObjects[index]],
                "Parallel brush fitting changed an opacity layer's color.");
        Console.WriteLine("parallel_brush_geometry=ok,contours=exact,opacity_order=preserved");
    }

    private static void RunSerialSummaryPoolRegression()
    {
        var scene = NewBrushGeometryScene();
        scene.AddObject(scene.ActiveLayer, PointF.Empty, new SizeF(3000, 2000),
            0, 0, Color.Coral, 4, ShapeKind.Rectangle);
        var rebuild = RequireMethod(typeof(VectorScene), "RebuildSummaries", [typeof(bool)]);
        rebuild.Invoke(scene, [false]);
        var tileCounts = scene.TileCount.ToArray();
        var tileAtoms = scene.TileAtoms.ToArray();
        var tileColors = scene.TileArgb.ToArray();
        var overviewCounts = scene.OverviewCount.ToArray();
        var overviewAtoms = scene.OverviewAtoms.ToArray();
        var overviewColors = scene.OverviewArgb.ToArray();
        for (var iteration = 0; iteration < 3; iteration++)
        {
            var other = NewBrushGeometryScene();
            other.AddObject(other.ActiveLayer, PointF.Empty, new SizeF(5000, 6000),
                0, 0, Color.LimeGreen, 7, ShapeKind.Rectangle);
            rebuild.Invoke(other, [false]);
            rebuild.Invoke(scene, [false]);
            AssertBrushGeometry(scene.TileCount.SequenceEqual(tileCounts)
                && scene.TileAtoms.SequenceEqual(tileAtoms) && scene.TileArgb.SequenceEqual(tileColors)
                && scene.OverviewCount.SequenceEqual(overviewCounts)
                && scene.OverviewAtoms.SequenceEqual(overviewAtoms) && scene.OverviewArgb.SequenceEqual(overviewColors),
                "A pooled summary retained another scene's colors, atom totals or cell counts.");
        }
        scene.CreateEmpty();
        AssertBrushGeometry(scene.TileCount.All(count => count == 0) && scene.TileAtoms.All(atoms => atoms == 0)
            && scene.OverviewCount.All(count => count == 0) && scene.OverviewAtoms.All(atoms => atoms == 0),
            "An empty scene retained pooled summary content.");
        Console.WriteLine("serial_summary_pool=ok,colors=isolated,counts=isolated,empty=cleared");
    }

    private static void RunFittedPolylineDistanceIndexRegression()
    {
        var indexType = typeof(VectorScene).GetNestedType("FittedPolylineDistanceIndex",
            System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The fitted polyline distance index is missing.");
        var indexedDistance = indexType.GetMethod("DistanceTo",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
            binder: null, types: [typeof(PointF), typeof(int), typeof(int)], modifiers: null)
            ?? throw new InvalidOperationException("The indexed distance query is missing.");
        var exhaustiveDistance = typeof(VectorScene).GetMethod("DistanceToPolyline",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
            binder: null, types: [typeof(PointF), typeof(IReadOnlyList<PointF>)], modifiers: null)
            ?? throw new InvalidOperationException("The exhaustive distance query is missing.");
        var random = new Random(59173);
        var comparisons = 0;
        foreach (var scale in new[] { 0.00001f, 1f, 100_000_000f, 1e16f, 1e20f })
        foreach (var count in new[] { 0, 1, 8, 9, 17, 129, 513 })
        {
            var points = Enumerable.Range(0, count).Select(index => new PointF(
                index / 2 * scale, MathF.Sin(index * 0.19f) * 6 * scale)).ToArray();
            Check(points);
            if (count > 9)
            {
                points[count / 2] = new PointF(float.NaN, 0);
                Check(points);
            }

            void Check(PointF[] source)
            {
                using var distanceIndex = (IDisposable)Activator.CreateInstance(indexType, [source])!;
                var padded = Enumerable.Repeat(new PointF(float.NaN, float.MaxValue), source.Length + 128).ToArray();
                source.CopyTo(padded, 0);
                using var paddedIndex = (IDisposable)Activator.CreateInstance(indexType, [padded, source.Length])!;
                for (var queryIndex = 0; queryIndex < 64; queryIndex++)
                {
                    var first = count == 0 ? 0 : random.Next(count);
                    var last = count == 0 ? -1 : random.Next(first, count);
                    var query = queryIndex % 11 == 0 ? new PointF(float.NaN, 0)
                        : new PointF((float)random.NextDouble() * count * scale,
                            (float)(random.NextDouble() * 20 - 10) * scale);
                    var range = source.Skip(first).Take(last - first + 1).ToArray();
                    var expected = (float)exhaustiveDistance.Invoke(null, [query, range])!;
                    var actual = (float)indexedDistance.Invoke(distanceIndex, [query, first, last])!;
                    var paddedActual = (float)indexedDistance.Invoke(paddedIndex, [query, first, last])!;
                    AssertBrushGeometry(actual.Equals(expected) && paddedActual.Equals(expected),
                        $"Indexed fitting changed an exhaustive distance: count={count}, range={first}..{last}, "
                        + $"scale={scale}, expected={expected}, actual={actual}.");
                    comparisons += 2;
                }
            }
        }
        Console.WriteLine($"fitted_polyline_distance_index=ok,comparisons={comparisons}");
    }

    private static void RunBrushPolylineDistanceIndexRegression()
    {
        var indexType = typeof(BrushStrokeBuilder).GetNestedType("PolylineDistanceIndex",
            System.Reflection.BindingFlags.NonPublic)!;
        var distanceMethod = indexType.GetMethod("DistanceTo")!;
        var coversMethod = indexType.GetMethod("Covers")!;
        var exhaustiveMethod = typeof(BrushStrokeBuilder).GetMethod("SegmentDistance",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var random = new Random(71429);
        var comparisons = 0;
        foreach (var scale in new[] { 0.00001f, 1f, 100_000_000f })
        foreach (var count in new[] { 0, 1, 2, 9, 17, 129 })
        {
            var target = Enumerable.Range(0, count).Select(index => new PointF(
                (float)(random.NextDouble() * 2 - 1) * scale,
                (float)(random.NextDouble() * 2 - 1) * scale)).ToArray();
            for (var queryIndex = 0; queryIndex < 48; queryIndex++)
            {
                var start = new PointF((float)(random.NextDouble() * 2 - 1) * scale,
                    (float)(random.NextDouble() * 2 - 1) * scale);
                var end = queryIndex % 2 == 0 ? start : new PointF(start.Y, start.X);
                using var distanceIndex = (IDisposable)Activator.CreateInstance(indexType, [new[] { start, end }, target])!;
                var expected = float.MaxValue;
                for (var index = 0; index < count - 1; index++)
                    expected = Math.Min(expected, Exhaustive(start, index));
                var actual = (float)distanceMethod.Invoke(distanceIndex, [start])!;
                AssertBrushGeometry(actual.Equals(expected),
                    $"Brush distance indexing changed the exhaustive result: scale={scale}, count={count}, expected={expected}, actual={actual}.");
                var error = queryIndex % 3 == 0 ? expected : queryIndex % 3 == 1
                    ? MathF.BitDecrement(expected) : MathF.BitIncrement(expected);
                var expectedCoverage = false;
                for (var index = 0; index < count - 1; index++)
                    expectedCoverage |= Exhaustive(start, index) <= error && Exhaustive(end, index) <= error;
                object[] arguments = [start, end, error, 65536];
                var coverage = (bool)coversMethod.Invoke(distanceIndex, arguments)!;
                AssertBrushGeometry(coverage == expectedCoverage,
                    $"Brush coverage indexing changed a tolerance-boundary result: scale={scale}, count={count}, error={error}.");
                comparisons += 2;
            }

            float Exhaustive(PointF point, int index) =>
                (float)exhaustiveMethod.Invoke(null, [point, target[index], target[index + 1]])!;
        }
        Console.WriteLine($"brush_polyline_distance_index=ok,comparisons={comparisons}");
    }

    private static void RunBezierPointSamplingEquivalenceRegression()
    {
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        var reference = typeof(VectorScene).GetMethod("SampleOpenFreehandBezierNodesWithParameters", flags)!;
        var pointProperty = typeof(VectorScene).GetNestedType("OpenBezierSample",
            System.Reflection.BindingFlags.NonPublic)!.GetProperty("Point")!;
        var closedMethod = typeof(VectorScene).GetMethod("SamplePathBezierContour", flags)!;
        var segmentMethod = typeof(VectorScene).GetMethod("SampleCubicSegment", flags)!;
        var random = new Random(82391);
        var comparisons = 0;
        foreach (var scale in new[] { 0.00001f, 1f, 10_000f })
        foreach (var count in new[] { 0, 1, 2, 9, 33 })
        for (var variant = 0; variant < 8; variant++)
        {
            var nodes = Enumerable.Range(0, count).Select(index =>
            {
                var anchor = new PointF(index * scale * 8, MathF.Sin(index * 0.8f) * scale * 8);
                var previous = new PointF((index - 1) * scale * 8, MathF.Sin((index - 1) * 0.8f) * scale * 8);
                var next = new PointF((index + 1) * scale * 8, MathF.Sin((index + 1) * 0.8f) * scale * 8);
                return variant == 0 ? new PathBezierNode(PointF.Empty, PointF.Empty, PointF.Empty)
                    : variant == 1 ? new PathBezierNode(anchor,
                        new PointF(anchor.X + (previous.X - anchor.X) / 3, anchor.Y + (previous.Y - anchor.Y) / 3),
                        new PointF(anchor.X + (next.X - anchor.X) / 3, anchor.Y + (next.Y - anchor.Y) / 3))
                    : new PathBezierNode(anchor,
                        new PointF(anchor.X - scale * (float)random.NextDouble() * 10, anchor.Y + scale * 10),
                        new PointF(anchor.X + scale * (float)random.NextDouble() * 10, anchor.Y - scale * 10));
            }).ToArray();
            if (variant == 6 && count > 0) nodes[^1] = nodes[^1] with { Anchor = new PointF(float.NaN, 0) };
            if (variant == 7 && count > 1) nodes[^1] = nodes[0];
            var samples = (Array)reference.Invoke(null, [nodes])!;
            var expected = samples.Cast<object>().Select(sample => (PointF)pointProperty.GetValue(sample)!).ToArray();
            var actual = VectorScene.SampleOpenFreehandBezierNodes(nodes);
            AssertBrushGeometry(actual.SequenceEqual(expected),
                $"Points-only Bezier sampling changed the parameterized reference: scale={scale}, count={count}, variant={variant}.");
            var closedExpected = new List<PointF>();
            for (var index = 0; index < nodes.Length; index++)
            {
                var current = nodes[index];
                var next = nodes[(index + 1) % nodes.Length];
                var segment = new CubicBoundarySegment(current.Anchor, current.OutgoingControl, next.IncomingControl, next.Anchor);
                var segmentPoints = (PointF[])segmentMethod.Invoke(null, [segment])!;
                closedExpected.AddRange(segmentPoints.Skip(index == 0 ? 0 : 1));
            }
            if (closedExpected.Count > 1 && closedExpected[0] == closedExpected[^1]) closedExpected.RemoveAt(closedExpected.Count - 1);
            var closedActual = (PointF[])closedMethod.Invoke(null, [nodes])!;
            AssertBrushGeometry(closedActual.Length == closedExpected.Count
                && closedActual.Zip(closedExpected).All(pair => pair.First.X.Equals(pair.Second.X) && pair.First.Y.Equals(pair.Second.Y)),
                $"Closed Bezier sampling changed the segment reference: scale={scale}, count={count}, variant={variant}.");
            comparisons += 2;
        }
        Console.WriteLine($"bezier_point_sampling_equivalence=ok,comparisons={comparisons}");
    }

    private static void RunSpatialIndexResetRegression()
    {
        var scene = NewBrushGeometryScene();
        var cellMethod = typeof(VectorScene).GetMethod("CellForWorld",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var random = new Random(92851);
        for (var cycle = 0; cycle < 3; cycle++)
        {
            var previousRevision = scene.GeometryRevision;
            scene.CreateEmpty();
            AssertBrushGeometry(scene.GeometryRevision > previousRevision && scene.CellObjects.Length == 0
                && scene.CellStart.All(value => value == 0), "Empty scene retained stale spatial index entries.");
            var expected = new List<int>[scene.CellStart.Length - 1];
            for (var index = 0; index < 257; index++)
            {
                var center = new PointF((float)(random.NextDouble() - 0.5) * 120_000,
                    (float)(random.NextDouble() - 0.5) * 100_000);
                var owner = scene.AddObject(0, center, new SizeF(100, 80), 0, 1, Color.Teal, Color.Black, 4, ShapeKind.Rectangle);
                var cell = (int)cellMethod.Invoke(scene, [scene.X[owner], scene.Y[owner]])!;
                if (cell >= 0) (expected[cell] ??= []).Add(owner);
            }
            scene.RebuildGeometryIndex();
            var cursor = 0;
            for (var cell = 0; cell < expected.Length; cell++)
            {
                AssertBrushGeometry(scene.CellStart[cell] == cursor, "Serial spatial index changed its cell prefix.");
                foreach (var owner in expected[cell] ?? [])
                {
                    AssertBrushGeometry(scene.CellObjects[cursor] == owner, "Serial spatial index changed stable object ordering.");
                    cursor++;
                }
                AssertBrushGeometry(scene.CellStart[cell + 1] == cursor, "Serial spatial index retained an incorrect cell end.");
            }
        }
        Console.WriteLine("spatial_index_reset=ok,empty=cleared,cell_ranges=exact,object_order=stable,outside=excluded");
    }

    private static void RunBrushToolCommitPerformanceRegression()
    {
        Console.WriteLine($"fitted_brush_workers={ParallelBatch.MaximumWorkerCountFor(12)},logical_processors={Environment.ProcessorCount}");
        var path = Enumerable.Range(0, 256).Select(index => new PointF(
            -1800 + index * (3600f / 255), MathF.Sin(index * MathF.Tau * 3 / 255) * 420)).ToArray();
        var longPath = Enumerable.Range(0, 1024).Select(index => new PointF(
            -25000 + index * (50000f / 1023), MathF.Sin(index * MathF.Tau * 18 / 1023) * 750)).ToArray();
        var pressure = path.Select((point, index) => new PressureBrushPoint(point,
            160 + MathF.Sin(index * MathF.Tau / 255) * 70)).ToArray();
        var tipSetup = Stopwatch.StartNew();
        var roundTip = BrushShape.CreateTraditionalBrush();
        var squareTip = BrushShape.CreateTraditionalBrush(TraditionalBrushTipKind.Square, 150, 23);
        var softTip = BrushShape.CreateSoftRound();
        tipSetup.Stop();
        Console.WriteLine($"fitted_brush_tip_configuration_ms={tipSetup.Elapsed.TotalMilliseconds:0.000}");
        Measure("fitted_round_brush", 10, scene => BrushStrokeBuilder.AddStroke(
            scene, 0, path, 200, Color.Coral, roundTip, 70, 25));
        Measure("fitted_shape_brush", 10, scene => BrushStrokeBuilder.AddStroke(
            scene, 0, path, 200, Color.Coral,
            squareTip, 70, 25));
        Measure("fitted_pressure_brush", 10, scene => BrushStrokeBuilder.AddPressureProfile(
            scene, 0, pressure, Color.Coral, roundTip, 70, 25));
        Measure("fitted_long_soft_brush", 30, scene => BrushStrokeBuilder.AddStroke(
            scene, 0, longPath, 200, Color.Coral, softTip, 70, 25));

        static void Measure(string name, double budget, Func<VectorScene, int[]> build)
        {
            var elapsed = new double[7];
            var collections = new string[elapsed.Length];
            var phaseSamples = new string[elapsed.Length];
            var allocatedBytes = new long[elapsed.Length];
            var scene = NewBrushGeometryScene();
            for (var iteration = -3; iteration < elapsed.Length; iteration++)
            {
                scene.CreateEmpty();
                if (iteration == 0)
                {
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
                    GC.WaitForPendingFinalizers();
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
                }
                var generation0 = GC.CollectionCount(0);
                var generation1 = GC.CollectionCount(1);
                var generation2 = GC.CollectionCount(2);
                var allocationStart = GC.GetTotalAllocatedBytes(precise: false);
                var watch = Stopwatch.StartNew();
                var snapshot = scene.CreateSnapshot();
                var snapshotMilliseconds = watch.Elapsed.TotalMilliseconds;
                var objects = build(scene);
                var buildMilliseconds = watch.Elapsed.TotalMilliseconds - snapshotMilliseconds;
                var retained = scene.NormalizePaintForInteractiveCommit(objects, enforceComplexityBudget: true);
                watch.Stop();
                GC.KeepAlive(snapshot);
                AssertBrushGeometry(objects.Length > 0 && retained.Length > 0,
                    $"{name} performance fixture lost its visible content.");
                if (iteration >= 0)
                {
                    elapsed[iteration] = watch.Elapsed.TotalMilliseconds;
                    allocatedBytes[iteration] = GC.GetTotalAllocatedBytes(precise: false) - allocationStart;
                    phaseSamples[iteration] = $"{snapshotMilliseconds:F3}/{buildMilliseconds:F3}/{watch.Elapsed.TotalMilliseconds - snapshotMilliseconds - buildMilliseconds:F3}";
                    collections[iteration] = $"{GC.CollectionCount(0) - generation0}/{GC.CollectionCount(1) - generation1}/{GC.CollectionCount(2) - generation2}";
                }
            }
            Console.WriteLine($"{name}_samples_ms={string.Join(',', elapsed.Select(milliseconds => milliseconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)))}");
            Console.WriteLine($"{name}_sample_gc_counts={string.Join(',', collections)}");
            Console.WriteLine($"{name}_sample_snapshot_build_normalize_ms={string.Join(',', phaseSamples)}");
            Console.WriteLine($"{name}_sample_allocated_bytes={string.Join(',', allocatedBytes)}");
            Console.WriteLine($"{name}_warmup=3,measured=7,prior_heap=collected,sample_gc=included,scene_setup=reused_reset_outside_commit,brush_tip=reused");
            Array.Sort(elapsed);
            var average = elapsed.Average();
            var maximum = elapsed[^1];
            var percentile95 = elapsed[(int)Math.Ceiling(elapsed.Length * 0.95) - 1];
            Console.WriteLine($"{name}_average_ms={average:0.00}");
            Console.WriteLine($"{name}_p95_ms={percentile95:0.00}");
            Console.WriteLine($"{name}_maximum_ms={maximum:0.00}");
            Console.WriteLine($"{name}_budget_ms={budget:0.00}");
            Console.WriteLine($"{name}_budget_met={(maximum <= budget).ToString().ToLowerInvariant()}");
            AssertBrushGeometry(maximum <= budget,
                $"{name} exceeded the existing brush commit budget: {maximum:0.00} > {budget:0.00} ms.");
        }
    }

    private static void RunBrushGeometryCurvedTips()
    {
        var curve = Enumerable.Range(0, 193).Select(index =>
        {
            var amount = index / 192f;
            return new PointF(-900 + 1800 * amount, 400 * MathF.Sin(amount * MathF.PI * 1.5f));
        }).ToArray();
        var shapes = new[]
        {
            BrushShape.CreateTraditionalBrush(),
            BrushShape.CreateTraditionalBrush(TraditionalBrushTipKind.Round, 225, 37),
            BrushShape.CreateTraditionalBrush(TraditionalBrushTipKind.Square, 150, 23)
        };
        foreach (var shape in shapes)
        {
            var scene = NewBrushGeometryScene();
            var ids = BrushStrokeBuilder.AddStroke(scene, 0, curve, 150, Color.CornflowerBlue, shape, 100, 10);
            AssertBrushGeometry(ids.Length == 1, "A continuous solid tip must produce one compound path.");
            AssertBrushGeometryPath(scene, ids);
            foreach (var point in curve.Where((_, index) => index % 8 == 0))
                AssertBrushGeometry(BrushGeometryContains(scene, ids, point), "A curved tip sweep left a gap along the gesture.");
            AssertBrushGeometry(BrushGeometryContains(scene, ids, curve[0]) && BrushGeometryContains(scene, ids, curve[^1]),
                "A fitted brush trajectory lost a terminal cap.");
            AssertBrushGeometry(scene.TryGetPathBezierWorldContours(ids[0], out var exact)
                && exact.Sum(contour => contour.Length) < curve.Length * 4,
                "A smooth continuous tip did not reduce editable boundary nodes.");
            AssertBrushGeometry(scene.QueryObjects(new RectangleF(-1000, -700, 2200, 1400), 0).Contains(ids[0]),
                "The brush append did not refresh the spatial index.");

            var sparse = NewBrushGeometryScene();
            PointF[] segment = [new(-700, 0), new(700, 0)];
            var swept = BrushStrokeBuilder.AddStroke(sparse, 0, segment, 150, Color.Black, shape, 0, 1, frequency: 1);
            for (var index = 0; index <= 100; index++)
                AssertBrushGeometry(BrushGeometryContains(sparse, swept, new PointF(-700 + index * 14, 0)),
                    "Sparse continuous samples were stamped instead of swept.");
        }

        var longScene = NewBrushGeometryScene();
        var longIds = BrushStrokeBuilder.AddStroke(longScene, 0, [new PointF(0, 0), new PointF(1_000_000, 0)],
            200, Color.Black, shapes[0], 100, 25, frequency: 1);
        AssertBrushGeometry(longScene.TryGetPathBezierWorldContours(longIds[0], out var longContours)
            && longContours.Sum(contour => contour.Length) <= 40
            && BrushGeometryContains(longScene, longIds, new PointF(500_000, 0)),
            "A long straight sweep grew geometry with distance or lost its midpoint.");
    }

    private static void RunBrushGeometrySmoothing()
    {
        var points = Enumerable.Range(0, 129)
            .Select(index => new PointF(index * 32, index == 0 || index == 128 ? 0 : index % 2 == 0 ? -10 : 10))
            .ToArray();
        var shape = BrushShape.CreateTraditionalBrush();
        var detailed = NewBrushGeometryScene();
        var smooth = NewBrushGeometryScene();
        var detailedIds = BrushStrokeBuilder.AddStroke(detailed, 0, points, 100, Color.Black, shape, 0, 25);
        var smoothIds = BrushStrokeBuilder.AddStroke(smooth, 0, points, 100, Color.Black, shape, 100, 25);
        AssertBrushGeometry(detailed.TryGetPathBezierWorldContours(detailedIds[0], out var detailedContours)
            && smooth.TryGetPathBezierWorldContours(smoothIds[0], out var smoothContours)
            && smoothContours.Sum(contour => contour.Length) < detailedContours.Sum(contour => contour.Length),
            "Smoothing 100 must retain fewer Bezier boundary nodes than smoothing 0 on a noisy trajectory.");
        foreach (var point in points)
            AssertBrushGeometry(BrushGeometryContains(detailed, detailedIds, point), "Smoothing 0 discarded a recorded detail.");

        var narrow = NewBrushGeometryScene();
        var zigzag = Enumerable.Range(0, 25).Select(index => new PointF(index * 120, index % 2 == 0 ? 80 : -80)).ToArray();
        var narrowIds = BrushStrokeBuilder.AddStroke(narrow, 0, zigzag, 14, Color.Black, shape, 0, 1);
        foreach (var point in zigzag)
            AssertBrushGeometry(BrushGeometryContains(narrow, narrowIds, point), "The zero-smoothing path rounded away a narrow peak.");

        var otherFrequency = NewBrushGeometryScene();
        var otherIds = BrushStrokeBuilder.AddStroke(otherFrequency, 0, points, 100, Color.Black, shape, 100, 25, frequency: 24);
        AssertBrushGeometrySamePaths(smooth, smoothIds, otherFrequency, otherIds,
            "Continuous swept geometry must not depend on the stamp frequency.");
    }

    private static void RunBrushGeometrySinglePoints()
    {
        var center = new PointF(225, -175);
        var round = NewBrushGeometryScene();
        var roundIds = BrushStrokeBuilder.AddStroke(round, 0, [center], 300, Color.Black,
            BrushShape.CreateTraditionalBrush(), 100, 25);
        AssertBrushGeometry(roundIds.Length == 1 && round.TryGetPathBezierWorldContours(roundIds[0], out var roundContours)
            && roundContours.Length == 1 && roundContours[0].Length <= 16
            && roundContours[0].Any(node => node.Anchor != node.IncomingControl || node.Anchor != node.OutgoingControl),
            "A single round tip must be a compact curved path, not a dense polygon.");
        for (var index = 0; index < 64; index++)
        {
            var angle = index * MathF.Tau / 64;
            var direction = new PointF(MathF.Cos(angle), MathF.Sin(angle));
            AssertBrushGeometry(BrushGeometryContains(round, roundIds,
                    new PointF(center.X + direction.X * 138, center.Y + direction.Y * 138))
                && !BrushGeometryContains(round, roundIds,
                    new PointF(center.X + direction.X * 162, center.Y + direction.Y * 162)),
                "Fitting a round tip exceeded its boundary error allowance.");
        }

        var squareShape = BrushShape.CreateTraditionalBrush(TraditionalBrushTipKind.Square, 175, 31);
        var square = NewBrushGeometryScene();
        var squareIds = BrushStrokeBuilder.AddStroke(square, 0, [center], 200, Color.Black, squareShape, 100, 25);
        AssertBrushGeometry(square.TryGetPathBezierWorldContours(squareIds[0], out var squareContours)
            && squareContours.Length == 1 && squareContours[0].Length == 4,
            "A rotated square tip lost its four real corners.");
        foreach (var corner in squareShape.NormalizedContour(0.5f))
        {
            var expected = VectorUnits.Quantize(new PointF(center.X + corner.X * 100, center.Y + corner.Y * 100));
            AssertBrushGeometry(squareContours[0].Any(node => BrushGeometryDistance(node.Anchor, expected) <= 1.5f),
                "Outline fitting moved a real square corner.");
        }

        var softShape = BrushShape.CreateSoftRound(35);
        var soft = NewBrushGeometryScene();
        var color = Color.FromArgb(91, 60, 110, 190);
        var softIds = BrushStrokeBuilder.AddStroke(soft, 0, [center, center, center], 240, color, softShape, 100, 25);
        AssertBrushGeometry(softIds.Length == softShape.Layers.Count, "A soft tip lost a transparent contour layer.");
        for (var index = 0; index < softIds.Length; index++)
        {
            var actual = Color.FromArgb(soft.Argb[softIds[index]]);
            var alpha = (int)Math.Clamp(Math.Round(color.A * softShape.Layers[index].Opacity), 1, 255);
            AssertBrushGeometry(actual.A == alpha && actual.R == color.R && actual.G == color.G && actual.B == color.B,
                "Soft-layer opacity or RGB changed during brush construction.");
            AssertBrushGeometry(soft.FillContainsPoint(softIds[index], center), "A repeated single-point soft tip was discarded.");
        }
        AssertBrushGeometryPath(soft, softIds);
    }

    private static void RunBrushGeometryPressure()
    {
        var samples = Enumerable.Range(0, 65).Select(index => new PressureBrushSample(
            new PointF(-600 + index * 20, 0), 300, 0.3f, 0.10f + index / 64f * 0.9f)).ToArray();
        foreach (var shape in new[]
        {
            BrushShape.CreateTraditionalBrush(),
            BrushShape.CreateTraditionalBrush(TraditionalBrushTipKind.Round, 200, 25),
            BrushShape.CreateTraditionalBrush(TraditionalBrushTipKind.Square, 150, 0)
        })
        {
            var scene = NewBrushGeometryScene();
            var ids = BrushStrokeBuilder.AddPressureStroke(scene, 0, samples, 200, Color.DarkRed, shape, 100, 10);
            AssertBrushGeometry(ids.Length == 1 && BrushGeometryContains(scene, ids, samples[0].Point)
                && BrushGeometryContains(scene, ids, samples[^1].Point), "Pressure fitting lost a terminal sample.");
            AssertBrushGeometry(!BrushGeometryContains(scene, ids, new PointF(-600, 65))
                && BrushGeometryContains(scene, ids, new PointF(680, 65)),
                "The pressure diameter profile was replaced with constant width.");
            AssertBrushGeometryPath(scene, ids);
        }

        var click = NewBrushGeometryScene();
        var clickIds = BrushStrokeBuilder.AddPressureStroke(click, 0,
            [new PressureBrushSample(new PointF(35, 50), 0, 0, 0)], 80, Color.Black,
            BrushShape.CreateTraditionalBrush(), 0, 25);
        AssertBrushGeometry(clickIds.Length > 0 && BrushGeometryContains(click, clickIds, new PointF(35, 50)),
            "A minimum-pressure single click vanished on the quantized grid.");

        var spikeSamples = Enumerable.Range(0, 33).Select(index => new PressureBrushSample(
            new PointF(index * 20, 0), 200, 0.3f, index is >= 12 and <= 20 ? 1 : 0.1f)).ToArray();
        var spike = NewBrushGeometryScene();
        var spikeIds = BrushStrokeBuilder.AddPressureStroke(spike, 0, spikeSamples, 240, Color.Black,
            BrushShape.CreateTraditionalBrush(), 100, 25);
        AssertBrushGeometry(BrushGeometryContains(spike, spikeIds, new PointF(360, 85))
            && !BrushGeometryContains(spike, spikeIds, new PointF(0, 85)),
            "Trajectory resampling removed a pressure extremum.");
    }

    private static void RunBrushGeometryStampFrequency()
    {
        var shape = BrushShape.CreateTraditionalBrush();
        PointF[] points = [new(0, 0), new(1800, 0)];
        var sparse = NewBrushGeometryScene();
        var dense = NewBrushGeometryScene();
        var sparseIds = BrushStrokeBuilder.AddStroke(sparse, 0, points, 120, Color.Black, shape, 0, 1, 1, false);
        var denseIds = BrushStrokeBuilder.AddStroke(dense, 0, points, 120, Color.Black, shape, 0, 1, 8, false);
        var probe = new PointF(60, 45);
        AssertBrushGeometry(!BrushGeometryContains(sparse, sparseIds, probe) && BrushGeometryContains(dense, denseIds, probe),
            "Independent stamp frequency did not change the distance-based stamp cadence.");
        AssertBrushGeometry(BrushGeometryContains(sparse, sparseIds, points[0]) && BrushGeometryContains(sparse, sparseIds, points[^1]),
            "Independent stamping lost its first or last tip.");

        var pressureSparse = NewBrushGeometryScene();
        var pressureDense = NewBrushGeometryScene();
        PressureBrushSample[] samples = [new(points[0], 0, 1, 1), new(points[1], 0, 1, 1)];
        var pressureSparseIds = BrushStrokeBuilder.AddPressureStroke(pressureSparse, 0, samples, 120, Color.Black,
            shape, 0, 1, 1, false);
        var pressureDenseIds = BrushStrokeBuilder.AddPressureStroke(pressureDense, 0, samples, 120, Color.Black,
            shape, 0, 1, 8, false);
        AssertBrushGeometry(!BrushGeometryContains(pressureSparse, pressureSparseIds, probe)
            && BrushGeometryContains(pressureDense, pressureDenseIds, probe),
            "Pressure stamps ignored their frequency argument.");
    }

    private static void RunBrushGeometryPreparedProfileAndTrajectory()
    {
        var longCurve = Enumerable.Range(0, 769).Select(index =>
            new PointF(index * 10, MathF.Sin(index * MathF.PI / 96) * 300)).ToArray();
        foreach (var smoothing in new[] { 0, 100 })
        {
            var processed = BrushStrokeBuilder.ProcessTrajectory(longCurve, smoothing, 10);
            AssertBrushGeometry(processed.Length > 8 && processed[0] == VectorUnits.Quantize(longCurve[0])
                && processed[^1] == VectorUnits.Quantize(longCurve[^1]),
                "Preview trajectory fitting truncated a long stroke or moved its endpoints.");
            for (var peak = 48; peak < longCurve.Length; peak += 96)
            {
                var expected = longCurve[peak];
                AssertBrushGeometry(BrushGeometryPolylineDistance(expected, processed) < 20,
                    "Chunked preview fitting removed a long-stroke curvature extremum.");
            }
        }
        AssertBrushGeometry(BrushStrokeBuilder.ProcessTrajectory([], 100, 25).Length == 0,
            "Empty preview trajectory must remain empty.");

        var shape = BrushShape.CreateTraditionalBrush();
        PointF[] points = [new(100, 50), new(300, 150), new(600, 180)];
        var constant = points.Select(point => new PressureBrushPoint(point, 160)).ToArray();
        var normal = NewBrushGeometryScene();
        var prepared = NewBrushGeometryScene();
        var processedProfile = BrushStrokeBuilder.ProcessPressureTrajectory(constant, 75);
        var normalIds = BrushStrokeBuilder.AddPressureProfile(normal, 0, processedProfile, Color.Black, shape, 75, 10,
            trajectoryPrepared: true);
        var preparedIds = BrushStrokeBuilder.AddPressureProfile(prepared, 0, constant, Color.Black, shape, 75, 10);
        AssertBrushGeometrySamePaths(normal, normalIds, prepared, preparedIds,
            "Prepared pressure profiles were smoothed twice or remapped along a different trajectory.");

        var tail = NewBrushGeometryScene();
        PressureBrushPoint[] tailProfile = [new(new PointF(0, 0), 240), new(new PointF(500, 0), 80)];
        var tailIds = BrushStrokeBuilder.AddPressureProfile(tail, 0, tailProfile, Color.Black, shape, 100, 25);
        AssertBrushGeometry(BrushGeometryContains(tail, tailIds, new PointF(0, 100))
            && !BrushGeometryContains(tail, tailIds, new PointF(500, 80)),
            "A prepared pressure tail recomputed onset pressure or smoothed its supplied diameters.");
    }

    private static void RunBrushGeometryHolesAndSnapshots()
    {
        var loop = Enumerable.Range(0, 257).Select(index =>
        {
            var angle = index / 256f * MathF.Tau;
            return new PointF(MathF.Cos(angle) * 700, MathF.Sin(angle) * 700);
        }).ToArray();
        loop[^1] = loop[0];
        var scene = NewBrushGeometryScene();
        var shape = BrushShape.CreateTraditionalBrush();
        var ids = BrushStrokeBuilder.AddStroke(scene, 0, loop, 120, Color.FromArgb(120, 180, 90, 30), shape, 100, 25);
        AssertBrushGeometry(scene.TryGetPathBezierWorldContours(ids[0], out var contours) && contours.Length == 2
            && !BrushGeometryContains(scene, ids, PointF.Empty)
            && BrushGeometryContains(scene, ids, new PointF(700, 0)),
            "A closed swept brush filled its even-odd hole or lost its outer region.");
        var snapshot = scene.CreateSnapshot();
        var restored = new VectorScene();
        restored.RestoreSnapshot(snapshot);
        AssertBrushGeometrySamePaths(scene, ids, restored, ids, "Snapshot round-trip changed exact brush contours.");
        AssertBrushGeometry(!BrushGeometryContains(restored, ids, PointF.Empty)
            && restored.Argb[ids[0]] == scene.Argb[ids[0]]
            && restored.ObjectOrder[ids[0]] == scene.ObjectOrder[ids[0]]
            && restored.ObjectKeyframeFrame[ids[0]] == scene.ObjectKeyframeFrame[ids[0]],
            "Snapshot round-trip lost brush fill, order, keyframe, or hole metadata.");
        BrushStrokeBuilder.AddStroke(scene, 0, [new PointF(0, 0)], 300, Color.Black, shape, 100, 25);
        AssertBrushGeometry(restored.ObjectCount == ids.Length && !BrushGeometryContains(restored, ids, PointF.Empty),
            "Appending another brush mutated a captured snapshot.");

        var figureEight = Enumerable.Range(0, 257).Select(index =>
        {
            var angle = index / 256f * MathF.Tau;
            return new PointF(MathF.Sin(angle) * 500, MathF.Sin(angle * 2) * 350);
        }).ToArray();
        figureEight[^1] = figureEight[0];
        var crossing = NewBrushGeometryScene();
        var crossingIds = BrushStrokeBuilder.AddStroke(crossing, 0, figureEight, 60, Color.Black, shape, 100, 10);
        AssertBrushGeometry(crossing.TryGetPathBezierWorldContours(crossingIds[0], out var crossingContours)
            && crossingContours.Length >= 3
            && BrushGeometryContains(crossing, crossingIds, PointF.Empty)
            && !BrushGeometryContains(crossing, crossingIds, new PointF(350, 0))
            && !BrushGeometryContains(crossing, crossingIds, new PointF(-350, 0)),
            "A self-crossing sweep lost its two holes or cancelled its overlapping center.");
    }

    private static void RunBrushGeometryInvalidInput()
    {
        var scene = NewBrushGeometryScene();
        var shape = BrushShape.CreateTraditionalBrush();
        var revision = scene.GeometryRevision;
        AssertBrushGeometry(BrushStrokeBuilder.AddStroke(scene, 0, [], 100, Color.Black, shape, 100, 25).Length == 0
            && BrushStrokeBuilder.AddStroke(scene, 0, [PointF.Empty], 100, Color.Transparent, shape, 100, 25).Length == 0
            && scene.ObjectCount == 0 && scene.GeometryRevision == revision,
            "An empty or fully transparent brush changed the scene.");
        var rejected = false;
        try
        {
            BrushStrokeBuilder.AddStroke(scene, 0, [new PointF(float.NaN, 0)], 100, Color.Black, shape, 0, 25);
        }
        catch (ArgumentOutOfRangeException)
        {
            rejected = true;
        }
        AssertBrushGeometry(rejected && scene.ObjectCount == 0, "Invalid brush input partially mutated the scene.");
    }

    private static VectorScene NewBrushGeometryScene()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        return scene;
    }

    private static void RunBrushPreviewTrajectoryCacheRegression()
    {
        using var form = new MainForm { ShowInTaskbar = false };
        var samples = (List<PointF>)RequireField(typeof(MainForm), "_freehandSamples").GetValue(form)!;
        RequireField(typeof(MainForm), "_freehandBrushSmoothing").SetValue(form, 70);
        RequireField(typeof(MainForm), "_freehandPencilWorldPerPixel").SetValue(form, 25f);
        var process = RequireMethod(typeof(MainForm), "ProcessBrushPreviewTrajectory");
        var stableCount = RequireField(typeof(MainForm), "_brushPreviewStablePointCount");
        var previous = Array.Empty<PointF>();
        var previousStableCount = 0;
        for (var index = 0; index <= 768; index++)
        {
            samples.Add(new PointF(index * 10, MathF.Sin(index / 48f) * 400));
            if (index % 16 != 0) continue;
            var trajectory = (PointF[])process.Invoke(form, null)!;
            AssertBrushGeometry(trajectory.Take(previousStableCount).SequenceEqual(previous.Take(previousStableCount)),
                "Growing a brush preview refitted or moved a completed trajectory span.");
            previous = trajectory;
            previousStableCount = (int)stableCount.GetValue(form)!;
        }
        AssertBrushGeometry(previous.SequenceEqual(BrushStrokeBuilder.ProcessTrajectory(samples, 70, 25))
            && previousStableCount > 0 && previous[^1] == VectorUnits.Quantize(samples[^1]),
            "Incremental preview trajectory cache diverged from the complete committed trajectory.");
        RequireMethod(typeof(MainForm), "ClearBrushPreviewTrajectory").Invoke(form, null);
        AssertBrushGeometry((int)stableCount.GetValue(form)! == 0
            && ((List<PointF>)RequireField(typeof(MainForm), "_brushPreviewTrajectory").GetValue(form)!).Count == 0,
            "Clearing a brush gesture retained trajectory cache state.");
    }

    private static bool BrushGeometryContains(VectorScene scene, IReadOnlyList<int> ids, PointF point) =>
        ids.Any(index => scene.FillContainsPoint(index, point));

    private static void AssertBrushGeometryPath(VectorScene scene, IReadOnlyList<int> ids)
    {
        foreach (var index in ids)
        {
            AssertBrushGeometry(scene.ShapeKind[index] == ShapeKind.Path
                && scene.TryGetPathBezierWorldContours(index, out var contours) && contours.Length > 0
                && contours.All(contour => contour.Length >= 3 && contour.All(node =>
                    float.IsFinite(node.Anchor.X) && float.IsFinite(node.Anchor.Y)
                    && float.IsFinite(node.IncomingControl.X) && float.IsFinite(node.IncomingControl.Y)
                    && float.IsFinite(node.OutgoingControl.X) && float.IsFinite(node.OutgoingControl.Y))),
                "Brush geometry is not a finite editable compound Bezier path.");
        }
    }

    private static void AssertBrushGeometrySamePaths(
        VectorScene first, IReadOnlyList<int> firstIds, VectorScene second, IReadOnlyList<int> secondIds, string message)
    {
        AssertBrushGeometry(firstIds.Count == secondIds.Count, message);
        for (var index = 0; index < firstIds.Count; index++)
        {
            AssertBrushGeometry(first.TryGetPathBezierWorldContours(firstIds[index], out var firstContours)
                && second.TryGetPathBezierWorldContours(secondIds[index], out var secondContours)
                && firstContours.Length == secondContours.Length
                && firstContours.Zip(secondContours).All(pair => pair.First.AsSpan().SequenceEqual(pair.Second)), message);
        }
    }

    private static float BrushGeometryDistance(PointF first, PointF second) =>
        MathF.Sqrt((first.X - second.X) * (first.X - second.X) + (first.Y - second.Y) * (first.Y - second.Y));

    private static float BrushGeometryPolylineDistance(PointF point, IReadOnlyList<PointF> points)
    {
        var minimum = float.MaxValue;
        for (var index = 1; index < points.Count; index++)
        {
            var start = points[index - 1];
            var end = points[index];
            var deltaX = end.X - start.X;
            var deltaY = end.Y - start.Y;
            var squared = deltaX * deltaX + deltaY * deltaY;
            var amount = squared < 0.0001f ? 0
                : Math.Clamp(((point.X - start.X) * deltaX + (point.Y - start.Y) * deltaY) / squared, 0, 1);
            minimum = Math.Min(minimum, BrushGeometryDistance(point,
                new PointF(start.X + deltaX * amount, start.Y + deltaY * amount)));
        }
        return minimum;
    }

    private static void AssertBrushGeometry(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
