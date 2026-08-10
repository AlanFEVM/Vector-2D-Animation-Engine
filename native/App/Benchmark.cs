using System.Diagnostics;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private const double TargetRenderFramesPerSecond = 144.0;
    private const double RenderCollectBudgetMilliseconds = 1000.0 / TargetRenderFramesPerSecond;

    public static void RunDefaultStress()
    {
        var project = VectorProject.CreateEmpty();
        var drawingObject = project.DrawingObjects[0];
        var scene = drawingObject.Scene;
        var timelineChanges = 0;
        scene.Timeline.Changed += (_, _) => timelineChanges++;
        var generate = Stopwatch.StartNew();
        scene.Generate(1000, 100000, 100000000);
        generate.Stop();
        AssertStressScene(scene, timelineChanges);

        var duplicate = new VectorScene();
        duplicate.Generate(1000, 100000, 100000000);
        var stressGeometryChecksum = StressGeometryChecksum(scene);
        if (stressGeometryChecksum != StressGeometryChecksum(duplicate)
            || !scene.TileCount.SequenceEqual(duplicate.TileCount)
            || !scene.TileAtoms.SequenceEqual(duplicate.TileAtoms)
            || !scene.TileArgb.SequenceEqual(duplicate.TileArgb)
            || !scene.OverviewCount.SequenceEqual(duplicate.OverviewCount)
            || !scene.OverviewAtoms.SequenceEqual(duplicate.OverviewAtoms)
            || !scene.OverviewArgb.SequenceEqual(duplicate.OverviewArgb))
        {
            throw new InvalidOperationException("Parallel stress generation is not deterministic.");
        }
        RunParallelRenderOrderRegression(duplicate);
        RunInstanceTranslationPreviewRegression();

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

        var renderOrder = new SceneRenderOrderBuffer();
        for (var viewportIndex = 0; viewportIndex < viewports.Length; viewportIndex++)
        {
            var reference = CollectRenderStatsReference(scene, viewports[viewportIndex], 20);
            renderOrder.Collect(scene, viewports[viewportIndex], 20);
            if (renderOrder.VisibleCount != reference.VisibleObjects
                || renderOrder.ScannedCount != reference.ScannedObjects
                || renderOrder.VisibleAtoms != reference.VisibleAtoms
                || (viewportIndex == 0 && ParallelBatch.MaximumWorkerCount > 1 && renderOrder.LastCollectBatchCount <= 1))
            {
                throw new InvalidOperationException($"Render collection diverged from the serial reference for viewport {viewportIndex}.");
            }
        }

        const int renderCollectIterations = 20;
        var collectWatch = Stopwatch.StartNew();
        long collectedObjects = 0;
        var maximumCollectBatches = 1;
        for (var iteration = 0; iteration < renderCollectIterations; iteration++)
        {
            foreach (var viewport in viewports)
            {
                renderOrder.Collect(scene, viewport, 20);
                collectedObjects += renderOrder.VisibleCount;
                maximumCollectBatches = Math.Max(maximumCollectBatches, renderOrder.LastCollectBatchCount);
            }
        }
        collectWatch.Stop();
        var renderCollectSamples = renderCollectIterations * viewports.Length;
        var averageRenderCollectMilliseconds = collectWatch.Elapsed.TotalMilliseconds / renderCollectSamples;
        const int interactiveCollectSamples = 80;
        var interactiveCollectWatch = Stopwatch.StartNew();
        for (var sample = 0; sample < interactiveCollectSamples; sample++)
        {
            renderOrder.Collect(scene, viewports[2], 20);
        }
        interactiveCollectWatch.Stop();
        var interactiveCollectMilliseconds = interactiveCollectWatch.Elapsed.TotalMilliseconds / interactiveCollectSamples;
        var interactiveCollectBudgetMet = interactiveCollectMilliseconds <= RenderCollectBudgetMilliseconds;

        var sceneDefinition = project.Scenes[0];
        var sceneTrack = sceneDefinition.Timeline.Tracks[0];
        sceneDefinition.Timeline.SetTrackDuration(sceneTrack.Id, scene.FrameCount);
        if (!project.TryAddSceneInstance(sceneDefinition.Id, drawingObject.Id, PointF.Empty, 0, out _))
        {
            throw new InvalidOperationException("The stress drawing object could not be instanced for composition benchmarking.");
        }

        var compositionDestination = new VectorScene();
        var compositionWatch = Stopwatch.StartNew();
        var composition = SceneCompositionBuilder.Build(compositionDestination, sceneDefinition, project.DrawingObjects, 20);
        compositionWatch.Stop();
        if (compositionDestination.LayerCount != scene.LayerCount
            || compositionDestination.ObjectCount != scene.ObjectCount
            || composition.ObjectOwners.Count != scene.ObjectCount
            || compositionDestination.TileCount.Sum() <= 0
            || compositionDestination.OverviewCount.Sum() <= 0)
        {
            throw new InvalidOperationException(
                $"Stress-scene composition lost layers, objects, ownership, or LOD summaries: " +
                $"layers={compositionDestination.LayerCount}/{scene.LayerCount}, " +
                $"objects={compositionDestination.ObjectCount}/{scene.ObjectCount}, " +
                $"owners={composition.ObjectOwners.Count}/{scene.ObjectCount}, " +
                $"tiles={compositionDestination.TileCount.Sum()}, overview={compositionDestination.OverviewCount.Sum()}.");
        }

        Console.WriteLine($"generate_ms={generate.Elapsed.TotalMilliseconds:0.0}");
        Console.WriteLine($"batch_workers={ParallelBatch.MaximumWorkerCount}");
        Console.WriteLine($"layers={scene.LayerCount}");
        Console.WriteLine($"objects={scene.ObjectCount}");
        Console.WriteLine($"virtual_atoms={scene.VirtualAtomCount}");
        Console.WriteLine($"timeline_notifications={timelineChanges}");
        Console.WriteLine($"stress_geometry_checksum={stressGeometryChecksum:X16}");
        Console.WriteLine($"avg_scanned_objects={scanned / (double)(viewports.Length * 200):0.0}");
        Console.WriteLine($"avg_cells={cells / (double)(viewports.Length * 200):0.0}");
        Console.WriteLine($"overview_tiles={scene.OverviewCount.Count(count => count > 0)}");
        Console.WriteLine($"detail_tiles={scene.TileCount.Count(count => count > 0)}");
        Console.WriteLine($"query_ms={scanWatch.Elapsed.TotalMilliseconds:0.0}");
        Console.WriteLine($"render_collect_ms={collectWatch.Elapsed.TotalMilliseconds:0.0}");
        Console.WriteLine($"render_collect_mixed_avg_ms={averageRenderCollectMilliseconds:0.000}");
        Console.WriteLine($"render_collect_interactive_avg_ms={interactiveCollectMilliseconds:0.000}");
        Console.WriteLine($"render_collect_budget_ms={RenderCollectBudgetMilliseconds:0.000}");
        Console.WriteLine($"render_collect_budget_met={interactiveCollectBudgetMet.ToString().ToLowerInvariant()}");
        Console.WriteLine($"render_collect_capacity_fps={1000.0 / interactiveCollectMilliseconds:0.0}");
        Console.WriteLine($"render_collect_batches={maximumCollectBatches}");
        Console.WriteLine($"render_collect_objects={collectedObjects}");
        Console.WriteLine($"composition_ms={compositionWatch.Elapsed.TotalMilliseconds:0.0}");
        Console.WriteLine($"composition_bucket_ms={SceneCompositionBuilder.LastBuildMetrics.BucketMilliseconds:0.0}");
        Console.WriteLine($"composition_setup_ms={SceneCompositionBuilder.LastBuildMetrics.SetupMilliseconds:0.0}");
        Console.WriteLine($"composition_append_ms={SceneCompositionBuilder.LastBuildMetrics.AppendMilliseconds:0.0}");
        Console.WriteLine($"composition_finalize_ms={SceneCompositionBuilder.LastBuildMetrics.FinalizeMilliseconds:0.0}");
        Console.WriteLine($"composition_objects={compositionDestination.ObjectCount}");
    }

    private static void AssertStressScene(VectorScene scene, int timelineChanges)
    {
        if (scene.LayerCount != 1000
            || scene.ObjectCount != 100000
            || scene.VirtualAtomCount != 100000000
            || timelineChanges != 1
            || scene.CellStart.Length == 0
            || scene.CellStart[^1] != scene.ObjectCount
            || scene.CellObjects.Length != scene.ObjectCount)
        {
            throw new InvalidOperationException("The generated stress scene did not meet its structural targets.");
        }

        var seen = new bool[scene.ObjectCount];
        for (var cell = 0; cell < scene.CellStart.Length - 1; cell++)
        {
            var previous = -1;
            for (var position = scene.CellStart[cell]; position < scene.CellStart[cell + 1]; position++)
            {
                var objectIndex = scene.CellObjects[position];
                if ((uint)objectIndex >= scene.ObjectCount || seen[objectIndex] || objectIndex <= previous)
                {
                    throw new InvalidOperationException("The parallel spatial index is incomplete, duplicated, or unstable.");
                }

                seen[objectIndex] = true;
                previous = objectIndex;
            }
        }

        if (seen.Any(value => !value)) throw new InvalidOperationException("The parallel spatial index lost one or more objects.");
    }

    private static void RunInstanceTranslationPreviewRegression()
    {
        const int objectCount = 512;
        const int samples = 120;
        var scene = new VectorScene();
        scene.CreateEmpty();
        var indices = new int[objectCount];
        var localPointArrays = new PointF[objectCount][];
        for (var objectIndex = 0; objectIndex < objectCount; objectIndex++)
        {
            var x = (objectIndex % 32) * 40f;
            var y = (objectIndex / 32) * 28f;
            indices[objectIndex] = scene.AddFreehandStroke(
                0,
                [
                    new PointF(x, y),
                    new PointF(x + 8, y + 5),
                    new PointF(x + 16, y - 3),
                    new PointF(x + 24, y + 7),
                    new PointF(x + 32, y)
                ],
                4,
                Color.CornflowerBlue,
                brushStroke: false,
                5);
            if (!scene.TryGetFreehandLocalPoints(indices[objectIndex], out localPointArrays[objectIndex]))
            {
                throw new InvalidOperationException("The instance translation preview regression could not create local stroke geometry.");
            }
        }

        var initialX = scene.X[indices[0]];
        var initialY = scene.Y[indices[0]];
        scene.TranslateObjectsForPreview(indices, 1, 1);
        scene.TranslateObjectsForPreview(indices, -1, -1);
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        for (var sample = 0; sample < samples; sample++)
        {
            scene.TranslateObjectsForPreview(indices, 1, 1);
            scene.TranslateObjectsForPreview(indices, -1, -1);
        }
        watch.Stop();
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        var averageMilliseconds = watch.Elapsed.TotalMilliseconds / (samples * 2);
        var budgetMet = averageMilliseconds <= RenderCollectBudgetMilliseconds && allocatedBytes <= 4096;
        if (!budgetMet
            || Math.Abs(scene.X[indices[0]] - initialX) > 0.001f
            || Math.Abs(scene.Y[indices[0]] - initialY) > 0.001f
            || indices.Where((index, objectIndex) =>
                    !scene.TryGetFreehandLocalPoints(index, out var points)
                    || !ReferenceEquals(points, localPointArrays[objectIndex]))
                .Any())
        {
            throw new InvalidOperationException(
                $"Instance translation preview regressed: avg={averageMilliseconds:0.000} ms, allocated={allocatedBytes} bytes.");
        }

        Console.WriteLine("instance_translation_preview_regression=ok");
        Console.WriteLine($"instance_translation_preview_avg_ms={averageMilliseconds:0.000}");
        Console.WriteLine($"instance_translation_preview_allocated_bytes={allocatedBytes}");
        Console.WriteLine($"instance_translation_preview_budget_met={budgetMet.ToString().ToLowerInvariant()}");

        const int complexPathPointCount = 4096;
        var complexPathContour = Enumerable.Range(0, complexPathPointCount)
            .Select(index =>
            {
                var angle = MathF.Tau * index / complexPathPointCount;
                var radius = 420f + MathF.Sin(angle * 17) * 36f;
                return new PointF(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius);
            })
            .ToArray();
        var complexPath = scene.AddPathObject(
            0,
            complexPathContour,
            0,
            Color.MediumSeaGreen,
            Color.Transparent,
            complexPathPointCount);
        scene.SetGradientPaint(
            complexPath,
            GradientKind.Radial,
            [new GradientStop(0, Color.Gold), new GradientStop(1, Color.RoyalBlue)],
            PointF.Empty,
            new PointF(420, 0));
        if (!scene.TryGetPathLocalContours(complexPath, out var complexPathLocalIdentity))
        {
            throw new InvalidOperationException("The drawing translation preview regression could not create its complex Path.");
        }

        var complexPathInitialX = scene.X[complexPath];
        var complexPathInitialY = scene.Y[complexPath];
        var transformSession = scene.BeginTransformSession([complexPath]);
        scene.ApplyTranslationSessionForPreview(transformSession, 1, 1);
        scene.ApplyTranslationSessionForPreview(transformSession, 0, 0);
        allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        watch.Restart();
        for (var sample = 0; sample < samples; sample++)
        {
            scene.ApplyTranslationSessionForPreview(transformSession, sample + 1, sample + 2);
        }
        watch.Stop();
        var drawingTranslationAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        var drawingTranslationAverageMilliseconds = watch.Elapsed.TotalMilliseconds / samples;
        var drawingTranslationBudgetMet = drawingTranslationAverageMilliseconds <= 1
            && drawingTranslationAllocatedBytes <= 4096;
        var translatedRevision = scene.GeometryRevision;
        var duplicateTranslationChanged = scene.ApplyTranslationSessionForPreview(
            transformSession,
            samples,
            samples + 1);
        if (!drawingTranslationBudgetMet
            || duplicateTranslationChanged
            || scene.GeometryRevision != translatedRevision
            || Math.Abs(scene.X[complexPath] - (complexPathInitialX + samples)) > 0.001f
            || Math.Abs(scene.Y[complexPath] - (complexPathInitialY + samples + 1)) > 0.001f
            || !scene.TryGetPathLocalContours(complexPath, out var translatedPathLocalIdentity)
            || !ReferenceEquals(complexPathLocalIdentity, translatedPathLocalIdentity))
        {
            throw new InvalidOperationException(
                $"Drawing translation preview regressed: avg={drawingTranslationAverageMilliseconds:0.000} ms, allocated={drawingTranslationAllocatedBytes} bytes.");
        }

        Console.WriteLine("drawing_translation_preview_regression=ok");
        Console.WriteLine($"drawing_translation_preview_avg_ms={drawingTranslationAverageMilliseconds:0.000}");
        Console.WriteLine($"drawing_translation_preview_allocated_bytes={drawingTranslationAllocatedBytes}");
        Console.WriteLine($"drawing_translation_preview_budget_met={drawingTranslationBudgetMet.ToString().ToLowerInvariant()}");

        const int translationSnapshotSamples = 32;
        VectorSceneSnapshot? translationSnapshot = scene.CreateWholeObjectTranslationSnapshot();
        allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        watch.Restart();
        for (var sample = 0; sample < translationSnapshotSamples; sample++)
        {
            translationSnapshot = scene.CreateWholeObjectTranslationSnapshot();
        }
        watch.Stop();
        var translationSnapshotAverageMilliseconds = watch.Elapsed.TotalMilliseconds / translationSnapshotSamples;
        var translationSnapshotAllocatedBytesPerSample =
            (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / translationSnapshotSamples;
        var translationSnapshotBudgetMet = translationSnapshotAverageMilliseconds <= 1
            && translationSnapshotAllocatedBytesPerSample <= 512 * 1024;
        if (!ReferenceEquals(
                translationSnapshot!.PathLocalContours[complexPath],
                complexPathLocalIdentity))
        {
            throw new InvalidOperationException("The translation undo snapshot cloned complex local geometry on the pointer-critical path.");
        }
        translationSnapshot.DetachSharedGeometry();
        if (!translationSnapshotBudgetMet
            || ReferenceEquals(
                translationSnapshot.PathLocalContours[complexPath],
                complexPathLocalIdentity)
            || ReferenceEquals(
                translationSnapshot.PathLocalContours[complexPath][0],
                complexPathLocalIdentity[0]))
        {
            throw new InvalidOperationException(
                $"The translation undo snapshot regressed: avg={translationSnapshotAverageMilliseconds:0.000} ms, allocated={translationSnapshotAllocatedBytesPerSample} bytes.");
        }

        Console.WriteLine("drawing_translation_undo_snapshot_regression=ok");
        Console.WriteLine($"drawing_translation_undo_snapshot_avg_ms={translationSnapshotAverageMilliseconds:0.000}");
        Console.WriteLine($"drawing_translation_undo_snapshot_allocated_bytes={translationSnapshotAllocatedBytesPerSample}");
        Console.WriteLine($"drawing_translation_undo_snapshot_budget_met={translationSnapshotBudgetMet.ToString().ToLowerInvariant()}");

        const int geometrySnapshotSamples = 32;
        VectorSceneSnapshot? geometrySnapshot = null;
        allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        watch.Restart();
        for (var sample = 0; sample < geometrySnapshotSamples; sample++)
        {
            geometrySnapshot = scene.CreateWholeObjectTranslationSnapshot();
            geometrySnapshot.DetachSharedGeometry([complexPath]);
        }
        watch.Stop();
        var geometrySnapshotAverageMilliseconds = watch.Elapsed.TotalMilliseconds / geometrySnapshotSamples;
        var geometrySnapshotAllocatedBytesPerSample =
            (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / geometrySnapshotSamples;
        var geometrySnapshotBudgetMet = geometrySnapshotAverageMilliseconds <= 1
            && geometrySnapshotAllocatedBytesPerSample <= 1024 * 1024;
        if (!geometrySnapshotBudgetMet
            || ReferenceEquals(geometrySnapshot!.PathLocalContours[complexPath], complexPathLocalIdentity)
            || !ReferenceEquals(geometrySnapshot.FreehandLocalPoints[indices[0]], localPointArrays[0]))
        {
            throw new InvalidOperationException(
                $"The targeted geometry undo snapshot regressed: avg={geometrySnapshotAverageMilliseconds:0.000} ms, allocated={geometrySnapshotAllocatedBytesPerSample} bytes.");
        }

        if (!scene.TryConvertFillToBezierPath(complexPath)
            || !scene.TryGetPathBezierSegment(complexPath, 0, out var originalBezierSegment))
        {
            throw new InvalidOperationException("The targeted geometry undo snapshot could not prepare a Bezier mutation.");
        }
        var mutationSnapshot = scene.CreateWholeObjectTranslationSnapshot();
        mutationSnapshot.DetachSharedGeometry([complexPath]);
        var movedControl = new PointF(
            originalBezierSegment.Control1.X + 24,
            originalBezierSegment.Control1.Y - 16);
        if (!scene.SetPathBezierSegmentForPreview(
                complexPath,
                0,
                originalBezierSegment.Start,
                movedControl,
                originalBezierSegment.Control2,
                originalBezierSegment.End))
        {
            throw new InvalidOperationException("The targeted geometry undo snapshot could not apply its Bezier preview mutation.");
        }
        scene.RestoreSnapshot(mutationSnapshot);
        if (!scene.TryGetPathBezierSegment(complexPath, 0, out var restoredBezierSegment)
            || restoredBezierSegment.PartIndex != originalBezierSegment.PartIndex
            || restoredBezierSegment.ContourIndex != originalBezierSegment.ContourIndex
            || restoredBezierSegment.SegmentIndex != originalBezierSegment.SegmentIndex
            || !PointsWithin(restoredBezierSegment.Start, originalBezierSegment.Start, 0.01f)
            || !PointsWithin(restoredBezierSegment.Control1, originalBezierSegment.Control1, 0.01f)
            || !PointsWithin(restoredBezierSegment.Control2, originalBezierSegment.Control2, 0.01f)
            || !PointsWithin(restoredBezierSegment.End, originalBezierSegment.End, 0.01f))
        {
            throw new InvalidOperationException("Restoring a targeted geometry undo snapshot retained an in-place Bezier preview mutation.");
        }

        geometrySnapshot.DetachSharedGeometry();
        if (ReferenceEquals(geometrySnapshot.FreehandLocalPoints[indices[0]], localPointArrays[0]))
        {
            throw new InvalidOperationException("Finalizing a targeted geometry undo snapshot left unrelated geometry shared.");
        }

        Console.WriteLine("drawing_geometry_undo_snapshot_regression=ok");
        Console.WriteLine($"drawing_geometry_undo_snapshot_avg_ms={geometrySnapshotAverageMilliseconds:0.000}");
        Console.WriteLine($"drawing_geometry_undo_snapshot_allocated_bytes={geometrySnapshotAllocatedBytesPerSample}");
        Console.WriteLine($"drawing_geometry_undo_snapshot_budget_met={geometrySnapshotBudgetMet.ToString().ToLowerInvariant()}");
    }

    private static ulong StressGeometryChecksum(VectorScene scene)
    {
        var hash = 1469598103934665603UL;
        for (var index = 0; index < scene.ObjectCount; index++)
        {
            hash = HashStressValue(hash, scene.ObjectLayer[index]);
            hash = HashStressValue(hash, scene.ObjectKeyframeFrame[index]);
            hash = HashStressValue(hash, scene.ObjectOrder[index]);
            hash = HashStressValue(hash, BitConverter.SingleToInt32Bits(scene.X[index]));
            hash = HashStressValue(hash, BitConverter.SingleToInt32Bits(scene.Y[index]));
            hash = HashStressValue(hash, BitConverter.SingleToInt32Bits(scene.Width[index]));
            hash = HashStressValue(hash, BitConverter.SingleToInt32Bits(scene.Height[index]));
            hash = HashStressValue(hash, BitConverter.SingleToInt32Bits(scene.Angle[index]));
            hash = HashStressValue(hash, BitConverter.SingleToInt32Bits(scene.Stroke[index]));
            hash = HashStressValue(hash, (int)scene.ShapeKind[index]);
            hash = HashStressValue(hash, scene.AtomCount[index]);
            hash = HashStressValue(hash, scene.Argb[index]);
            hash = HashStressValue(hash, scene.StrokeArgb[index]);
        }

        return hash;
    }

    private static ulong HashStressValue(ulong hash, long value)
    {
        hash ^= unchecked((ulong)value);
        return hash * 1099511628211UL;
    }

    private static RenderStats CollectRenderStatsReference(VectorScene scene, RectangleF bounds, int frame)
    {
        var activeKeyframes = new int[scene.LayerCount];
        scene.PopulateActiveKeyframeFrames(frame, activeKeyframes);
        scene.GetIndexRange(bounds, out var minX, out var maxX, out var minY, out var maxY);
        var visible = 0;
        var scanned = 0;
        long atoms = 0;
        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                var cell = scene.CellIndex(x, y);
                var start = scene.CellStart[cell];
                var end = scene.CellStart[cell + 1];
                scanned += end - start;
                for (var position = start; position < end; position++)
                {
                    var objectIndex = scene.CellObjects[position];
                    var layer = scene.ObjectLayer[objectIndex];
                    if (scene.ObjectKeyframeFrame[objectIndex] != activeKeyframes[layer]) continue;
                    var objectBounds = scene.GetObjectWorldBounds(objectIndex);
                    if (objectBounds.Right < bounds.Left
                        || objectBounds.Left > bounds.Right
                        || objectBounds.Bottom < bounds.Top
                        || objectBounds.Top > bounds.Bottom)
                    {
                        continue;
                    }

                    visible++;
                    atoms += scene.AtomCount[objectIndex];
                }
            }
        }

        return new RenderStats(visible, visible, atoms, 0, scanned, false);
    }

    private static void RunParallelRenderOrderRegression(VectorScene scene)
    {
        const int frame = 20;
        var bounds = new RectangleF(-7000, -4100, 14000, 8200);
        scene.SetLayerVisible(1, false);
        if (!scene.InsertTimelineBlankKeyframe(2, frame))
        {
            throw new InvalidOperationException("The parallel render-order regression could not create a blank exposure.");
        }

        var tiedObjects = Enumerable.Range(0, scene.ObjectCount)
            .Where(index =>
            {
                if (scene.ObjectLayer[index] != 0 || !scene.IsObjectActive(index, frame)) return false;
                var objectBounds = scene.GetObjectWorldBounds(index);
                return objectBounds.Right >= bounds.Left
                    && objectBounds.Left <= bounds.Right
                    && objectBounds.Bottom >= bounds.Top
                    && objectBounds.Top <= bounds.Bottom;
            })
            .Take(3)
            .ToArray();
        if (tiedObjects.Length != 3) throw new InvalidOperationException("The parallel render-order regression has too few tie-break objects.");
        foreach (var objectIndex in tiedObjects) scene.ObjectOrder[objectIndex] = long.MaxValue / 2;
        scene.ObjectSubOrder[tiedObjects[0]] = -0.5;
        scene.ObjectSubOrder[tiedObjects[1]] = 0.25;
        scene.ObjectSubOrder[tiedObjects[2]] = 0.25;

        var expected = CollectRenderCommandsReference(
            scene,
            bounds,
            frame,
            out var expectedVisible,
            out var expectedAtoms,
            out var expectedVisibleBoundsArea);
        var renderOrder = new SceneRenderOrderBuffer();
        renderOrder.Collect(scene, bounds, frame);
        var actual = new List<long>(expected.Length);
        var drawn = renderOrder.Draw(
            scene,
            int.MaxValue,
            index => actual.Add(RenderCommand(index, SceneRenderPass.Fill)),
            index => actual.Add(RenderCommand(index, SceneRenderPass.Stroke)));

        if (renderOrder.VisibleCount != expectedVisible
            || renderOrder.VisibleAtoms != expectedAtoms
            || Math.Abs(renderOrder.VisibleBoundsArea - expectedVisibleBoundsArea) > 0.01d
            || drawn != expectedVisible
            || !actual.SequenceEqual(expected)
            || (ParallelBatch.MaximumWorkerCount > 1 && renderOrder.LastCollectBatchCount <= 1))
        {
            throw new InvalidOperationException("Parallel render collection changed visibility, atoms, or draw-command order.");
        }
    }

    private static long[] CollectRenderCommandsReference(
        VectorScene scene,
        RectangleF bounds,
        int frame,
        out int visibleCount,
        out long visibleAtoms,
        out double visibleBoundsArea)
    {
        var layers = new List<int>?[scene.LayerCount];
        visibleCount = 0;
        visibleAtoms = 0;
        visibleBoundsArea = 0;
        for (var objectIndex = 0; objectIndex < scene.ObjectCount; objectIndex++)
        {
            if (!scene.IsObjectActive(objectIndex, frame)) continue;
            var objectBounds = scene.GetObjectWorldBounds(objectIndex);
            if (objectBounds.Right < bounds.Left
                || objectBounds.Left > bounds.Right
                || objectBounds.Bottom < bounds.Top
                || objectBounds.Top > bounds.Bottom)
            {
                continue;
            }

            var layer = scene.ObjectLayer[objectIndex];
            (layers[layer] ??= new List<int>(64)).Add(objectIndex);
            visibleCount++;
            visibleAtoms += scene.AtomCount[objectIndex];
            var width = Math.Max(0f, Math.Min(objectBounds.Right, bounds.Right) - Math.Max(objectBounds.Left, bounds.Left));
            var height = Math.Max(0f, Math.Min(objectBounds.Bottom, bounds.Bottom) - Math.Max(objectBounds.Top, bounds.Top));
            visibleBoundsArea += width * (double)height;
        }

        var commands = new List<long>(visibleCount * 2);
        for (var layerIndex = scene.LayerCount - 1; layerIndex >= 0; layerIndex--)
        {
            var layer = layers[layerIndex];
            if (layer is not { Count: > 0 }) continue;
            layer.Sort((a, b) =>
            {
                var comparison = scene.ObjectOrder[a].CompareTo(scene.ObjectOrder[b]);
                if (comparison != 0) return comparison;
                comparison = scene.ObjectSubOrder[a].CompareTo(scene.ObjectSubOrder[b]);
                return comparison != 0 ? comparison : a.CompareTo(b);
            });
            foreach (var objectIndex in layer)
            {
                if (SceneRenderOrder.HasFill(scene.ShapeKind[objectIndex])) commands.Add(RenderCommand(objectIndex, SceneRenderPass.Fill));
            }
            foreach (var objectIndex in layer)
            {
                if (SceneRenderOrder.HasStroke(scene.ShapeKind[objectIndex], scene.Stroke[objectIndex])) commands.Add(RenderCommand(objectIndex, SceneRenderPass.Stroke));
            }
        }

        return commands.ToArray();
    }

    private static long RenderCommand(int objectIndex, SceneRenderPass pass)
    {
        return ((long)objectIndex << 1) | (pass == SceneRenderPass.Stroke ? 1L : 0L);
    }

}
