using System.Diagnostics;

namespace VectorAnimationEngine;

internal static class Benchmark
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

    public static void RunTimelineRegression()
    {
        if (Math.Abs(TimelineStrip.CursorTimeSeconds(13, 30) - 13d / 30d) > 0.000001
            || TimelineStrip.FormatCursorTimeSeconds(13, 30) != "0.433 s"
            || TimelineStrip.FormatCursorTimeSeconds(0, 24) != "0.000 s"
            || TimelineStrip.FormatCursorTimeSeconds(120, 60) != "2.000 s"
            || Math.Abs(TimelineStrip.CursorTimeSeconds(23976, 23.976m) - 1000d) > 0.000001
            || TimelineStrip.FormatCursorTimeSeconds(24, 23.976m) != "1.001 s"
            || Math.Abs(MainForm.PlaybackFrameStepSeconds(23.976m) - 1d / 23.976d) > 0.000000001)
        {
            throw new InvalidOperationException("Timeline cursor time did not follow the active playback FPS in seconds.");
        }

        if (ModernNumericUpDown.ResolveWheelPlaceValue("23.976", 0, ".", 0.001m) != 10m
            || ModernNumericUpDown.ResolveWheelPlaceValue("23.976", 1, ".", 0.001m) != 1m
            || ModernNumericUpDown.ResolveWheelPlaceValue("23.976", 3, ".", 0.001m) != 0.1m
            || ModernNumericUpDown.ResolveWheelPlaceValue("23.976", 4, ".", 0.001m) != 0.01m
            || ModernNumericUpDown.ResolveWheelPlaceValue("23.976", 5, ".", 0.001m) != 0.001m
            || ModernNumericUpDown.ResolveWheelPlaceValue("-23,976", 2, ",", 0.001m) != 1m
            || ModernNumericUpDown.ResolveWheelPlaceValue("23.976", 2, ".", 0.001m) != 0.001m)
        {
            throw new InvalidOperationException("Numeric mouse-wheel digit targeting did not resolve decimal place values correctly.");
        }

        var selectionBlocks = TimelineStrip.CoalesceFrameSelectionCells(
        [
            new Point(2, 1), new Point(3, 1), new Point(4, 1),
            new Point(2, 2), new Point(3, 2), new Point(4, 2)
        ]);
        if (selectionBlocks.Count != 1 || selectionBlocks[0] != new Rectangle(2, 1, 3, 2))
        {
            throw new InvalidOperationException("Timeline frame selection cells were not coalesced into one rectangular overlay.");
        }
        var disjointSelectionBlocks = TimelineStrip.CoalesceFrameSelectionCells(
            [new Point(1, 0), new Point(2, 0), new Point(4, 0)]);
        if (disjointSelectionBlocks.Count != 2
            || !disjointSelectionBlocks.Contains(new Rectangle(1, 0, 2, 1))
            || !disjointSelectionBlocks.Contains(new Rectangle(4, 0, 1, 1)))
        {
            throw new InvalidOperationException("Timeline frame selection coalescing bridged a gap between disjoint cells.");
        }

        var verticalWheel = TimelineStrip.ResolveWheelScrollDeltas(-120, Keys.None);
        var horizontalWheel = TimelineStrip.ResolveWheelScrollDeltas(-240, Keys.Shift);
        var shiftedControlWheel = TimelineStrip.ResolveWheelScrollDeltas(120, Keys.Shift | Keys.Control);
        if (verticalWheel != (3, 0)
            || horizontalWheel != (0, 6)
            || shiftedControlWheel != (0, -3)
            || TimelineStrip.ResolveWheelScrollDeltas(0, Keys.Shift) != (0, 0))
        {
            throw new InvalidOperationException(
                "Timeline wheel input did not route vertical scrolling by default and horizontal scrolling through Shift.");
        }

        RunFixedStepBatchRegression();
        RunTimelineExposureRegression();
        RunTimelineShortcutAdvanceRegression();
        RunTimelineUndoPlayheadRegression();
        RunTimelineFrameCommandRegression();
        RunTimelineTrackSynchronizationRegression();
        RunTimelineSnapshotRegression();
        RunVectorSceneTimelineSnapshotRegression();
        RunVectorSceneSnapshotMemoryEstimateRegression();
        RunEditableTextObjectRegression();
        RunVectorSceneCelOwnershipRegression();
        RunAutoKeyframeMaterializationRegression();
        RunTimelineLayerWorkflowRegression();
        RunTimelineLayerRemovalRegression();
        RunTimelineKeyframePerformanceRegression();
        RunVectorSceneKeyframeBoundaryRegression();
        RunProjectDocumentStructureRegression();
        RunProjectAssetFolderRegression();
        RunSceneInstanceTimelineRegression();
        RunLayeredInstanceIndexRegression();
        RunSceneCompositionRegression();
        RunEditorRestartSnapshotRegression();
        RunProjectVaultPersistenceRegression();
        Console.WriteLine("timeline_regression=ok");
    }

    private static void RunFixedStepBatchRegression()
    {
        var batcher = new FixedStepBatcher(300);
        var steps = 0;
        for (var tick = 0; tick < 625; tick++) steps += batcher.Consume(0.008);
        if (steps is < 1499 or > 1500)
        {
            throw new InvalidOperationException($"The batched fixed-step scheduler produced {steps} updates instead of approximately 1500.");
        }

        batcher.Reset();
        if (batcher.Consume(0.001) != 0) throw new InvalidOperationException("The fixed-step scheduler did not reset its remainder.");

        var isolatedRender = MainForm.CalculatePerformanceRateSample(1, 0, 1, playing: false);
        var activeRates = MainForm.CalculatePerformanceRateSample(60, 300, 1, playing: true);
        var pausedRates = MainForm.CalculatePerformanceRateSample(60, 300, 1, playing: false);
        if (isolatedRender.HasRenderRate
            || isolatedRender.HasUpdateRate
            || !activeRates.HasRenderRate
            || Math.Abs(activeRates.RenderFps - 60) > 0.001
            || !activeRates.HasUpdateRate
            || Math.Abs(activeRates.UpdatesPerSecond - 300) > 0.001
            || !pausedRates.HasRenderRate
            || pausedRates.HasUpdateRate)
        {
            throw new InvalidOperationException("Render FPS and UPS telemetry did not preserve active, isolated, and paused rate semantics.");
        }
    }

    private static void RunTimelineExposureRegression()
    {
        var timeline = new AnimationTimeline();
        timeline.SynchronizeTracks(["layer-a"], defaultDuration: 12);
        var track = timeline.FindTrackByTargetId("layer-a")
            ?? throw new InvalidOperationException("Timeline did not create the requested track.");

        var first = timeline.EvaluateExposure(track.Id, 0);
        var held = timeline.EvaluateTargetExposure("layer-a", 5);
        AssertTimeline(
            first.IsKeyframe
            && first.HasContent
            && first.SourceKind == TimelineKeyframeKind.Populated
            && first.SourceKeyframeFrame == 0
            && held.HasContent
            && !held.IsKeyframe
            && held.SourceKeyframeFrame == 0
            && held.EndFrame == 11,
            "Populated keyframe content was not held through the track duration.");

        AssertTimeline(timeline.InsertBlankKeyframe(track.Id, 6), "F7 did not insert a blank keyframe.");
        var beforeBlank = timeline.EvaluateExposure(track.Id, 5);
        var blank = timeline.EvaluateExposure(track.Id, 6);
        var heldBlank = timeline.EvaluateExposure(track.Id, 8);
        AssertTimeline(
            beforeBlank.HasContent
            && beforeBlank.EndFrame == 5
            && blank.IsKeyframe
            && !blank.HasContent
            && blank.SourceKind == TimelineKeyframeKind.Blank
            && heldBlank.SourceKeyframeFrame == 6
            && !heldBlank.HasContent,
            "Blank keyframe exposure did not replace and hold over populated content.");

        AssertTimeline(timeline.InsertKeyframe(track.Id, 9), "F6 did not insert a populated keyframe.");
        var populated = timeline.EvaluateExposure(track.Id, 9);
        AssertTimeline(
            populated.IsKeyframe
            && populated.HasContent
            && populated.SourceKind == TimelineKeyframeKind.Populated,
            "F6 keyframe did not evaluate as populated content.");

        AssertTimeline(timeline.InsertKeyframe(track.Id, 6), "F6 did not replace a blank keyframe.");
        AssertTimeline(timeline.EvaluateExposure(track.Id, 6).HasContent, "F6 replacement remained blank.");
        AssertTimeline(timeline.InsertBlankKeyframe(track.Id, 6), "F7 did not replace a populated keyframe.");
        AssertTimeline(!timeline.EvaluateExposure(track.Id, 6).HasContent, "F7 replacement remained populated.");

        AssertTimeline(timeline.ClearKeyframe(track.Id, 6), "Shift+F6 did not clear the keyframe marker.");
        var cleared = timeline.EvaluateExposure(track.Id, 6);
        AssertTimeline(
            cleared.HasContent
            && !cleared.IsKeyframe
            && cleared.SourceKeyframeFrame == 0
            && cleared.EndFrame == 8,
            "Clearing a keyframe did not restore the preceding held exposure.");
        AssertTimeline(!timeline.ClearKeyframe(track.Id, 6), "Clearing a missing keyframe reported a change.");
    }

    private static void RunTimelineShortcutAdvanceRegression()
    {
        var timeline = new AnimationTimeline();
        timeline.SynchronizeTracks(["layer-a"], defaultDuration: 20);
        var track = timeline.FindTrackByTargetId("layer-a")
            ?? throw new InvalidOperationException("Timeline shortcut advance setup lost its track.");
        timeline.InsertBlankKeyframe(track.Id, 8);
        var source = new TimelineFrameCell("layer-a", 12);
        var target = MainForm.AdvanceTimelineKeyframeShortcutCell(source);
        AssertTimeline(
            target.TrackId == source.TrackId && target.Frame == 13,
            "Timeline keyframe insertion could not advance the playhead cell.");
        AssertTimeline(
            MainForm.AdvanceTimelineKeyframeShortcutCell(new TimelineFrameCell("layer-a", int.MaxValue - 1))
                == new TimelineFrameCell("layer-a", int.MaxValue - 1),
            "F6/F7 shortcut frame advance overflowed at the maximum supported frame.");
        AssertTimeline(
            MainForm.TimelineKeyframeInsertionShouldAdvance(timeline, new TimelineFrameCell(track.Id, 0))
            && !MainForm.TimelineKeyframeInsertionShouldAdvance(timeline, new TimelineFrameCell(track.Id, 4))
            && MainForm.TimelineKeyframeInsertionShouldAdvance(timeline, new TimelineFrameCell(track.Id, 8))
            && !MainForm.TimelineKeyframeInsertionShouldAdvance(timeline, new TimelineFrameCell(track.Id, 20))
            && MainForm.TimelineCellIsBeyondTrackEnd(timeline, new TimelineFrameCell(track.Id, 20))
            && !MainForm.TimelineKeyframeInsertionShouldAdvance(timeline, new TimelineFrameCell("missing", 0)),
            "F6/F7 did not distinguish keyframes, held exposure, and space beyond the track end before moving the playhead.");
        AssertTimeline(
            MainForm.ResolveTimelineKeyframeInsertionCell(
                timeline,
                track.Id,
                19,
                new TimelineFrameCell(track.Id, 53)) == new TimelineFrameCell(track.Id, 53)
            && MainForm.ResolveTimelineKeyframeInsertionCell(
                timeline,
                track.Id,
                4,
                new TimelineFrameCell(track.Id, 12)) == new TimelineFrameCell(track.Id, 4)
            && MainForm.ResolveTimelineKeyframeInsertionCell(
                timeline,
                track.Id,
                7,
                new TimelineFrameCell("other-track", 53)) == new TimelineFrameCell(track.Id, 7),
            "F6/F7 did not preserve an intentional active-layer selection beyond the track end or reject a stale selection.");

        var independentTimeline = new AnimationTimeline();
        independentTimeline.SynchronizeTracks(["edited", "selected", "untouched"], defaultDuration: 4);
        var editedTrack = independentTimeline.FindTrackByTargetId("edited")
            ?? throw new InvalidOperationException("Independent keyframe duration setup lost its edited track.");
        var selectedTrack = independentTimeline.FindTrackByTargetId("selected")
            ?? throw new InvalidOperationException("Independent keyframe duration setup lost its second selected track.");
        var untouchedTrack = independentTimeline.FindTrackByTargetId("untouched")
            ?? throw new InvalidOperationException("Independent keyframe duration setup lost its untouched track.");
        MainForm.EnsureTimelineCellFramesExist(
            independentTimeline,
            [new TimelineFrameCell(editedTrack.Id, 8)]);
        AssertTimeline(
            editedTrack.Duration == 9
            && selectedTrack.Duration == 4
            && untouchedTrack.Duration == 4,
            "Single-layer keyframe insertion forced unrelated timeline tracks to the same duration.");
        MainForm.EnsureTimelineCellFramesExist(
            independentTimeline,
            [new TimelineFrameCell(editedTrack.Id, 10), new TimelineFrameCell(selectedTrack.Id, 6)],
            trailingFrames: 1);
        AssertTimeline(
            editedTrack.Duration == 12
            && selectedTrack.Duration == 8
            && untouchedTrack.Duration == 4,
            "Multi-layer keyframe insertion extended an unselected timeline track.");
        var feedbackStyles = Enum.GetValues<TimelineCommand>()
            .Select(TimelineStrip.ResolveCommandFeedbackStyle)
            .ToArray();
        AssertTimeline(
            feedbackStyles.Select(style => style.Motion).Distinct().Count() == feedbackStyles.Length
            && feedbackStyles.All(style => style.DurationMilliseconds is >= 180 and <= 500)
            && feedbackStyles.All(style => style.Color.A == 255),
            "Timeline commands did not retain distinct, bounded UI feedback motions.");
        var addedLayerFeedback = TimelineStrip.ResolveLayerFeedbackStyle(TimelineLayerFeedbackKind.Add);
        var removedLayerFeedback = TimelineStrip.ResolveLayerFeedbackStyle(TimelineLayerFeedbackKind.Remove);
        AssertTimeline(
            addedLayerFeedback.Color != removedLayerFeedback.Color
            && addedLayerFeedback.DurationMilliseconds is >= 250 and <= 500
            && removedLayerFeedback.DurationMilliseconds is >= 250 and <= 500,
            "Timeline layer addition and removal did not retain distinct, bounded UI feedback styles.");

        var blankRangePlans = MainForm.ResolveTimelineBlankKeyframeRangePlans([0, 1, 2, 5, 7, 8]);
        AssertTimeline(
            blankRangePlans.Count == 3
            && blankRangePlans[0].ExtensionFrames.SequenceEqual([1])
            && blankRangePlans[0].BlankFrame == 2
            && blankRangePlans[1].ExtensionFrames.Length == 0
            && blankRangePlans[1].BlankFrame == 5
            && blankRangePlans[2].ExtensionFrames.SequenceEqual([7])
            && blankRangePlans[2].BlankFrame == 8,
            "Multi-frame F7 selection did not reserve only the final frame of each range for a blank keyframe.");

        var firstInstance = new DrawingObjectInstanceDefinition { Id = "instance-a" };
        var primaryInstance = new DrawingObjectInstanceDefinition { Id = "instance-b" };
        var restoredSelection = MainForm.ResolveTimelineInstanceSelection(
            [firstInstance, primaryInstance],
            ["missing-instance", primaryInstance.Id, firstInstance.Id],
            primaryInstance.Id);
        AssertTimeline(
            restoredSelection.Instances.SequenceEqual([firstInstance, primaryInstance])
            && ReferenceEquals(restoredSelection.Primary, primaryInstance),
            "F6 did not restore the selected drawing-object instances by stable ID after advancing the frame.");
    }

    private static void RunTimelineUndoPlayheadRegression()
    {
        const System.Reflection.BindingFlags privateInstance =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var shortcut = typeof(MainForm).GetMethod("HandleTimelineShortcut", privateInstance)
            ?? throw new InvalidOperationException("Timeline undo regression could not find the shortcut handler.");
        var undo = typeof(MainForm).GetMethod("UndoLastEdit", privateInstance)
            ?? throw new InvalidOperationException("Timeline undo regression could not find the undo handler.");
        var frameField = typeof(MainForm).GetField("_frame", privateInstance)
            ?? throw new InvalidOperationException("Timeline undo regression could not inspect the playhead.");
        var timelineField = typeof(MainForm).GetField("_timeline", privateInstance)
            ?? throw new InvalidOperationException("Timeline undo regression could not inspect the timeline.");

        using var form = new MainForm();
        var timelineStrip = timelineField.GetValue(form) as TimelineStrip
            ?? throw new InvalidOperationException("Timeline undo regression did not find the timeline control.");
        var trackId = timelineStrip.Context.Timeline.Tracks[0].Id;
        timelineStrip.SelectSingleFrame(trackId, 0);
        var initialFrame = (int)(frameField.GetValue(form) ?? -1);
        var initialSelection = timelineStrip.SelectedFrameCells.ToArray();
        var initialKeyframeCount = timelineStrip.Context.Timeline.Tracks[0].Keyframes.Count;
        var inserted = shortcut.Invoke(form, new object[] { Keys.F6 }) is true;
        var insertedFrame = (int)(frameField.GetValue(form) ?? -1);
        var insertedSelection = timelineStrip.SelectedFrameCells.ToArray();
        var insertedKeyframeCount = timelineStrip.Context.Timeline.Tracks[0].Keyframes.Count;
        var undone = undo.Invoke(form, null) is true;
        var restoredFrame = (int)(frameField.GetValue(form) ?? -1);
        var restoredSelection = timelineStrip.SelectedFrameCells.ToArray();
        var restoredKeyframeCount = timelineStrip.Context.Timeline.Tracks[0].Keyframes.Count;

        AssertTimeline(
            inserted
            && insertedFrame == initialFrame + 1
            && insertedSelection.SequenceEqual([new TimelineFrameCell(trackId, initialFrame + 1)])
            && insertedKeyframeCount == initialKeyframeCount + 1,
            "F6 undo regression did not insert a keyframe and advance the playhead selection.");
        AssertTimeline(
            undone
            && restoredFrame == initialFrame
            && restoredSelection.SequenceEqual(initialSelection)
            && restoredKeyframeCount == initialKeyframeCount,
            "Undoing F6 did not restore the inserted keyframe, playhead, and frame selection.");

        var blankInserted = shortcut.Invoke(form, new object[] { Keys.F7 }) is true;
        var blankInsertedFrame = (int)(frameField.GetValue(form) ?? -1);
        var blankInsertedSelection = timelineStrip.SelectedFrameCells.ToArray();
        var blankInsertedKeyframeCount = timelineStrip.Context.Timeline.Tracks[0].Keyframes.Count;
        var blankUndone = undo.Invoke(form, null) is true;
        var blankRestoredFrame = (int)(frameField.GetValue(form) ?? -1);
        var blankRestoredSelection = timelineStrip.SelectedFrameCells.ToArray();
        var blankRestoredKeyframeCount = timelineStrip.Context.Timeline.Tracks[0].Keyframes.Count;
        AssertTimeline(
            blankInserted
            && blankInsertedFrame == initialFrame + 1
            && blankInsertedSelection.SequenceEqual([new TimelineFrameCell(trackId, initialFrame + 1)])
            && blankInsertedKeyframeCount == initialKeyframeCount + 1,
            "F7 undo regression did not insert a blank keyframe and advance the playhead selection.");
        AssertTimeline(
            blankUndone
            && blankRestoredFrame == initialFrame
            && blankRestoredSelection.SequenceEqual(initialSelection)
            && blankRestoredKeyframeCount == initialKeyframeCount,
            "Undoing F7 did not restore the blank keyframe, playhead, and frame selection.");
    }

    private static void RunTimelineFrameCommandRegression()
    {
        AssertTimeline(
            !MainForm.TimelineInsertRequiresCompositionRefresh(12, 12)
            && !MainForm.TimelineInsertRequiresCompositionRefresh(12, 18)
            && MainForm.TimelineInsertRequiresCompositionRefresh(12, 6),
            "F5 composition refresh routing did not preserve the unchanged current-frame fast path.");

        var cursorTimeline = new AnimationTimeline();
        cursorTimeline.SynchronizeTracks(["cursor-layer"], defaultDuration: 25);
        var cursorTrack = cursorTimeline.FindTrackByTargetId("cursor-layer")
            ?? throw new InvalidOperationException("F5 playhead regression setup lost its track.");
        var cursorCell = new TimelineFrameCell(cursorTrack.Id, 0);
        AssertTimeline(cursorTimeline.InsertFrame(cursorTrack.Id, cursorCell.Frame), "F5 playhead regression did not insert its frame.");
        AssertTimeline(
            MainForm.ResolveTimelineInsertPlayheadFrame(cursorTimeline, [cursorCell], 0) == 25,
            "F5 did not move the playhead to the newly extended exposure end.");

        var timeline = new AnimationTimeline();
        timeline.SynchronizeTracks(["layer-a"], defaultDuration: 10);
        var track = timeline.FindTrackByTargetId("layer-a")
            ?? throw new InvalidOperationException("Timeline frame command setup failed.");
        timeline.InsertBlankKeyframe(track.Id, 4);
        timeline.InsertKeyframe(track.Id, 7);

        AssertTimeline(timeline.InsertFrame(track.Id, 4, 2), "F5 did not insert frames.");
        AssertTimeline(track.Duration == 12, "F5 did not grow the track duration.");
        AssertTimeline(
            track.Keyframes.Select(item => (item.Frame, item.Kind)).SequenceEqual(new[]
            {
                (0, TimelineKeyframeKind.Populated),
                (4, TimelineKeyframeKind.Blank),
                (9, TimelineKeyframeKind.Populated)
            }),
            "F5 moved the selected key instead of extending its exposure and shifting later keys.");
        AssertTimeline(
            !timeline.EvaluateExposure(track.Id, 4).HasContent
            && !timeline.EvaluateExposure(track.Id, 5).HasContent,
            "Inserted frames did not extend the selected blank exposure.");

        AssertTimeline(timeline.RemoveFrame(track.Id, 3, 4), "Shift+F5 did not remove frames.");
        AssertTimeline(track.Duration == 8, "Shift+F5 did not shrink the track duration.");
        AssertTimeline(
            track.Keyframes.Select(item => (item.Frame, item.Kind)).SequenceEqual(new[]
            {
                (0, TimelineKeyframeKind.Populated),
                (3, TimelineKeyframeKind.Blank),
                (5, TimelineKeyframeKind.Populated)
            }),
            "Shift+F5 did not preserve the surviving blank exposure while shifting later keys left.");
        AssertTimeline(
            timeline.EvaluateExposure(track.Id, 4).SourceKeyframeFrame == 3
            && !timeline.EvaluateExposure(track.Id, 4).HasContent
            && timeline.EvaluateExposure(track.Id, 5).IsKeyframe,
            "Frame removal replaced surviving blank frames with preceding content.");

        var emptyScene = new VectorScene();
        emptyScene.CreateEmpty();
        var emptyTrack = emptyScene.Timeline.FindTrackByTargetId(emptyScene.LayerIds[0])
            ?? throw new InvalidOperationException("Empty timeline cell setup failed.");
        emptyScene.Timeline.SetTrackDuration(emptyTrack.Id, 6);
        using var timelineStrip = new TimelineStrip(emptyScene) { Size = new Size(760, 192) };
        timelineStrip.FrameWidth = 4;
        AssertTimeline(timelineStrip.FrameWidth == 8, "Timeline frame width did not clamp to its compact minimum.");
        timelineStrip.FrameWidth = 64;
        AssertTimeline(timelineStrip.FrameWidth == 32, "Timeline frame width did not clamp to its readable maximum.");
        timelineStrip.FrameWidth = 14;
        timelineStrip.FrameHeightPreset = TimelineFrameHeightPreset.Low;
        AssertTimeline(timelineStrip.FrameHeight == 16, "Low timeline frame height did not use the compact Adobe-style row metric.");
        timelineStrip.FrameHeightPreset = TimelineFrameHeightPreset.High;
        AssertTimeline(timelineStrip.FrameHeight == 28, "High timeline frame height did not use the expanded Adobe-style row metric.");
        timelineStrip.FrameHeightPreset = TimelineFrameHeightPreset.Medium;
        AssertTimeline(
            timelineStrip.FrameHeight == 21
            && TimelineStrip.RowHeightFor(TimelineFrameHeightPreset.Medium) == 21,
            "Medium timeline frame height did not retain the default row metric.");
        var dragGrid = new Rectangle(10, 20, 100, 60);
        AssertTimeline(
            TimelineStrip.ResolveFrameSelectionDragOffset(
                new Point(-200, -200),
                dragGrid,
                rowHeight: 20,
                frameCellWidth: 10,
                visibleTrackCount: 3) == (0, 0)
            && TimelineStrip.ResolveFrameSelectionDragOffset(
                new Point(1000, 1000),
                dragGrid,
                rowHeight: 20,
                frameCellWidth: 10,
                visibleTrackCount: 3) == (2, 9)
            && TimelineStrip.ResolveFrameSelectionDragOffset(
                new Point(35, 45),
                dragGrid,
                rowHeight: 20,
                frameCellWidth: 10,
                visibleTrackCount: 3) == (1, 2),
            "Timeline frame marquee drag did not clamp pointers outside the frame grid to an updateable endpoint.");
        const int emptyFrame = 8;
        timelineStrip.SelectSingleFrame(emptyTrack.Id, emptyFrame);
        AssertTimeline(
            timelineStrip.SelectedFrameCells.SequenceEqual([new TimelineFrameCell(emptyTrack.Id, emptyFrame)]),
            "The timeline did not retain a selection beyond the current last frame.");
        AssertTimeline(
            emptyScene.InsertTimelineFrame(0, emptyFrame)
            && emptyTrack.Duration == emptyFrame + 1,
            "Inserting an empty timeline cell did not extend the target track through the selected frame.");
        AssertTimeline(
            timelineStrip.SelectedFrameCells.SequenceEqual([new TimelineFrameCell(emptyTrack.Id, emptyFrame)]),
            "Extending the timeline discarded the selected empty frame cell.");
        var emptyAreaSelectionChanges = 0;
        timelineStrip.FrameSelectionChanged += (_, _) => emptyAreaSelectionChanges++;
        timelineStrip.ClearSelectionFromEmptyArea();
        AssertTimeline(
            !timelineStrip.HasFrameSelection
            && timelineStrip.SelectedFrameCells.Count == 0
            && emptyAreaSelectionChanges == 1,
            "Clicking empty timeline space did not clear the active frame selection.");

        var layeredScene = new VectorScene();
        layeredScene.CreateEmpty(5, 20);
        using var layeredTimelineStrip = new TimelineStrip(layeredScene) { Size = new Size(760, 192) };
        var targetTrack = layeredScene.Timeline.Tracks[3];
        layeredTimelineStrip.SelectSingleFrame(targetTrack.Id, 9);
        AssertTimeline(
            layeredTimelineStrip.ActiveTrackIndex == 3
            && layeredTimelineStrip.SelectedLayerCount == 1
            && layeredTimelineStrip.SelectedLayerTargetIds.SequenceEqual([targetTrack.TargetId])
            && layeredTimelineStrip.SelectedFrameCells.SequenceEqual([new TimelineFrameCell(targetTrack.Id, 9)]),
            "Selecting a frame left the previous layer highlighted beside the newly active layer.");
        layeredTimelineStrip.UpdateFrameSelectionFromPointer(new Point(-200, -200));
        AssertTimeline(
            layeredTimelineStrip.SelectedFrameCells.Contains(new TimelineFrameCell(layeredScene.Timeline.Tracks[0].Id, 0))
            && layeredTimelineStrip.SelectedFrameCells.Contains(new TimelineFrameCell(targetTrack.Id, 9)),
            "Dragging outside the timeline's upper-left corner did not extend the frame selection to the nearest grid cell.");
        layeredTimelineStrip.UpdateFrameSelectionFromPointer(new Point(2000, 2000));
        AssertTimeline(
            layeredTimelineStrip.SelectedFrameCells.Any(cell =>
                cell.TrackId == layeredScene.Timeline.Tracks[^1].Id
                && cell.Frame > 9)
            && layeredTimelineStrip.SelectedFrameCells.Contains(new TimelineFrameCell(targetTrack.Id, 9)),
            "Dragging outside the timeline's lower-right corner did not extend the frame selection to the nearest grid cell.");

        var scrollScene = new VectorScene();
        scrollScene.CreateEmpty(12, 20);
        using var scrollTimelineStrip = new TimelineStrip(scrollScene) { Size = new Size(760, 192) };
        var scrollAnchor = scrollScene.Timeline.Tracks[0];
        scrollTimelineStrip.SelectSingleFrame(scrollAnchor.Id, 4);
        AssertTimeline(
            TimelineStrip.ResolveFrameSelectionVerticalScrollDelta(-100, dragGrid) == -1
            && TimelineStrip.ResolveFrameSelectionVerticalScrollDelta(45, dragGrid) == 0
            && TimelineStrip.ResolveFrameSelectionVerticalScrollDelta(1000, dragGrid) == 1,
            "Timeline frame marquee vertical auto-scroll did not preserve its viewport-edge activation zones.");
        for (var index = 0; index < 12; index++)
        {
            scrollTimelineStrip.AutoScrollFrameSelection(new Point(290, 1000));
        }
        AssertTimeline(
            scrollTimelineStrip.SelectedFrameCells.Any(cell =>
                cell.TrackId == scrollScene.Timeline.Tracks[^1].Id
                && cell.Frame == 4),
            "Timeline frame marquee auto-scroll did not extend the selection through layers below the viewport.");
        for (var index = 0; index < 12; index++)
        {
            scrollTimelineStrip.AutoScrollFrameSelection(new Point(290, -1000));
        }
        AssertTimeline(
            scrollTimelineStrip.SelectedFrameCells.SequenceEqual([new TimelineFrameCell(scrollAnchor.Id, 4)]),
            "Timeline frame marquee auto-scroll did not return the selection endpoint to the first layer.");

        var resizeScene = new VectorScene();
        resizeScene.CreateEmpty(3, 20);
        using var resizeTimelineStrip = new TimelineStrip(resizeScene) { Size = new Size(760, 192) };
        resizeScene.Timeline.Clear();
        resizeTimelineStrip.RefreshTimelineForHeightResize();
        var latestResizeTrack = resizeScene.Timeline.FindTrackByTargetId(resizeScene.LayerIds[^1]);
        if (latestResizeTrack is not null) resizeTimelineStrip.SelectSingleFrame(latestResizeTrack.Id, 6);
        AssertTimeline(
            resizeScene.Timeline.Tracks.Select(track => track.TargetId).SequenceEqual(resizeScene.LayerIds)
            && latestResizeTrack is not null
            && resizeTimelineStrip.ActiveTrackId == latestResizeTrack.Id
            && resizeTimelineStrip.SelectedFrameCells.SequenceEqual([new TimelineFrameCell(latestResizeTrack.Id, 6)]),
            "Refreshing during a timeline height resize did not restore the latest layer tracks and selection state.");
    }

    private static void RunTimelineTrackSynchronizationRegression()
    {
        var timeline = new AnimationTimeline();
        timeline.SynchronizeTracks(["layer-a", "layer-b"], defaultDuration: 12);
        var trackA = timeline.FindTrackByTargetId("layer-a")
            ?? throw new InvalidOperationException("Timeline synchronization did not create layer-a.");
        var trackB = timeline.FindTrackByTargetId("layer-b")
            ?? throw new InvalidOperationException("Timeline synchronization did not create layer-b.");
        timeline.InsertBlankKeyframe(trackA.Id, 4);
        timeline.SetTrackDuration(trackB.Id, 6);

        timeline.SynchronizeTracks(["layer-b", "layer-c", "layer-a", "layer-b"], defaultDuration: 30);
        var synchronizedA = timeline.FindTrackByTargetId("layer-a");
        var synchronizedB = timeline.FindTrackByTargetId("layer-b");
        var synchronizedC = timeline.FindTrackByTargetId("layer-c");
        AssertTimeline(
            timeline.Tracks.Select(item => item.TargetId).SequenceEqual(new[] { "layer-b", "layer-c", "layer-a" }),
            "Track synchronization did not follow target order or remove duplicate target IDs.");
        AssertTimeline(
            ReferenceEquals(trackA, synchronizedA)
            && ReferenceEquals(trackB, synchronizedB)
            && synchronizedA?.Id == trackA.Id
            && synchronizedB?.Id == trackB.Id,
            "Track synchronization replaced existing tracks instead of preserving stable IDs and data.");
        AssertTimeline(
            synchronizedA is not null
            && synchronizedB is not null
            && synchronizedA.Duration == 12
            && synchronizedB.Duration == 6
            && synchronizedA.Keyframes.Any(item => item.Frame == 4 && item.Kind == TimelineKeyframeKind.Blank),
            "Track synchronization discarded existing duration or keyframe data.");
        AssertTimeline(
            synchronizedC is not null
            && synchronizedC.Duration == 30
            && synchronizedC.Keyframes.Count == 1
            && synchronizedC.Keyframes[0] == new TimelineKeyframe(0, TimelineKeyframeKind.Populated),
            "New synchronized track did not receive the requested populated default exposure.");
    }

    private static void RunTimelineSnapshotRegression()
    {
        var timeline = new AnimationTimeline();
        timeline.SynchronizeTracks(["layer-a", "layer-b"], defaultDuration: 10);
        var trackA = timeline.FindTrackByTargetId("layer-a")
            ?? throw new InvalidOperationException("Timeline snapshot setup did not create layer-a.");
        var trackB = timeline.FindTrackByTargetId("layer-b")
            ?? throw new InvalidOperationException("Timeline snapshot setup did not create layer-b.");
        timeline.InsertBlankKeyframe(trackA.Id, 3);
        timeline.SetTrackDuration(trackB.Id, 7);
        timeline.InsertKeyframe(trackB.Id, 5);
        var expectedAId = trackA.Id;
        var expectedBId = trackB.Id;
        var snapshot = timeline.CreateSnapshot();

        timeline.ClearKeyframe(trackA.Id, 3);
        timeline.InsertFrame(trackB.Id, 2, 4);
        timeline.SynchronizeTracks(["replacement"], defaultDuration: 2);
        timeline.RestoreSnapshot(snapshot);

        var restoredA = timeline.FindTrackByTargetId("layer-a");
        var restoredB = timeline.FindTrackByTargetId("layer-b");
        AssertTimeline(
            timeline.Tracks.Select(item => item.TargetId).SequenceEqual(new[] { "layer-a", "layer-b" })
            && restoredA?.Id == expectedAId
            && restoredB?.Id == expectedBId,
            "Timeline snapshot restore did not recover track order and stable IDs.");
        AssertTimeline(
            restoredA is not null
            && restoredB is not null
            && restoredA.Duration == 10
            && restoredA.Keyframes.SequenceEqual(new[]
            {
                new TimelineKeyframe(0, TimelineKeyframeKind.Populated),
                new TimelineKeyframe(3, TimelineKeyframeKind.Blank)
            })
            && restoredB.Duration == 7
            && restoredB.Keyframes.SequenceEqual(new[]
            {
                new TimelineKeyframe(0, TimelineKeyframeKind.Populated),
                new TimelineKeyframe(5, TimelineKeyframeKind.Populated)
            }),
            "Timeline snapshot restore did not recover durations and keyframes.");
    }

    private static void RunVectorSceneTimelineSnapshotRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty(layers: 2);
        scene.EditFrame = 0;
        scene.AddObject(0, PointF.Empty, new SizeF(20, 20), 0, 0, Color.Teal, 6, ShapeKind.Rectangle);
        var firstLayerId = scene.LayerIds[0];
        var secondLayerId = scene.LayerIds[1];
        var firstTrack = scene.Timeline.FindTrackByTargetId(firstLayerId)
            ?? throw new InvalidOperationException("Vector scene did not create a timeline track for its first layer.");
        var secondTrack = scene.Timeline.FindTrackByTargetId(secondLayerId)
            ?? throw new InvalidOperationException("Vector scene did not create a timeline track for its second layer.");
        scene.Timeline.InsertBlankKeyframe(firstTrack.Id, 4);
        scene.Timeline.InsertBlankKeyframe(firstTrack.Id, 8);
        scene.EditFrame = 8;
        scene.AddObject(0, new PointF(40, 0), new SizeF(20, 20), 0, 0, Color.Coral, 6, ShapeKind.Ellipse);
        scene.Timeline.SetTrackDuration(secondTrack.Id, 16);
        var expectedFirstTrackId = firstTrack.Id;
        var expectedSecondTrackId = secondTrack.Id;
        var snapshot = scene.CreateSnapshot();

        scene.Timeline.ClearKeyframe(firstTrack.Id, 4);
        scene.Timeline.InsertFrame(secondTrack.Id, 2, 5);
        scene.RestoreSnapshot(snapshot);

        var restoredFirst = scene.Timeline.FindTrackByTargetId(firstLayerId);
        var restoredSecond = scene.Timeline.FindTrackByTargetId(secondLayerId);
        AssertTimeline(
            scene.LayerIds.SequenceEqual(new[] { firstLayerId, secondLayerId })
            && restoredFirst?.Id == expectedFirstTrackId
            && restoredSecond?.Id == expectedSecondTrackId,
            "Vector scene snapshot did not restore stable layer and timeline track IDs.");
        AssertTimeline(
            scene.IsLayerActive(0, 3)
            && !scene.IsLayerActive(0, 4)
            && !scene.IsLayerActive(0, 7)
            && scene.IsLayerActive(0, 8)
            && restoredSecond?.Duration == 16,
            "Vector scene snapshot did not restore timeline-backed layer exposure.");
    }

    private static void RunVectorSceneSnapshotMemoryEstimateRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        var baseline = scene.CreateSnapshot().EstimateMemoryBytes();
        for (var index = 0; index < 32; index++)
        {
            var offset = index * 24f;
            var path = scene.AddPathObjectContours(
                0,
                [[
                    new PointF(offset, 0),
                    new PointF(offset + 16, 0),
                    new PointF(offset + 8, 16),
                    new PointF(offset, 0)
                ]],
                0,
                Color.Teal,
                Color.Transparent,
                8);
            scene.SetLinearGradient(path, Color.Teal, Color.Coral);
            scene.AddFreehandStroke(
                0,
                [
                    new PointF(offset, 32),
                    new PointF(offset + 8, 48),
                    new PointF(offset + 16, 32)
                ],
                6,
                Color.White,
                brushStroke: false,
                8);
        }

        var estimate = scene.CreateSnapshot().EstimateMemoryBytes();
        AssertTimeline(
            estimate > baseline + 16_000,
            "Vector scene snapshot memory estimation did not include geometry, gradients, and freehand point buffers.");
    }

    private static void RunEditableTextObjectRegression()
    {
        static TextObjectData CreateTextData(
            string content,
            TextFontStyle style = TextFontStyle.Regular,
            TextHorizontalAlignment alignment = TextHorizontalAlignment.Left,
            string familyName = TextGeometry.FallbackFontFamilyName)
        {
            return TextGeometry.NormalizeForAuthoring(new TextObjectData(
                content,
                familyName,
                36,
                style,
                alignment,
                new SizeF(9_000, 5_000)));
        }

        static RectangleF BoundsOf(IReadOnlyList<PointF[]> contours)
        {
            var points = contours.SelectMany(contour => contour).ToArray();
            if (points.Length == 0) return RectangleF.Empty;
            var left = points.Min(point => point.X);
            var top = points.Min(point => point.Y);
            return RectangleF.FromLTRB(
                left,
                top,
                points.Max(point => point.X),
                points.Max(point => point.Y));
        }

        var alignmentBounds = new Dictionary<TextHorizontalAlignment, RectangleF>();
        foreach (var alignment in Enum.GetValues<TextHorizontalAlignment>())
        {
            var data = CreateTextData("Align", alignment: alignment);
            AssertTimeline(
                TextGeometry.TryCreateWorldContours(data, PointF.Empty, data.LayoutSize, 0, out var contours)
                && contours.Length > 0,
                $"Text geometry did not create {alignment} aligned outlines.");
            alignmentBounds[alignment] = BoundsOf(contours);
        }
        AssertTimeline(
            alignmentBounds[TextHorizontalAlignment.Left].Left
                < alignmentBounds[TextHorizontalAlignment.Center].Left
            && alignmentBounds[TextHorizontalAlignment.Center].Left
                < alignmentBounds[TextHorizontalAlignment.Right].Left,
            "Text alignment did not move glyph outlines across the fixed layout width.");

        foreach (var style in Enum.GetValues<TextFontStyle>())
        {
            var data = CreateTextData("Vector \u6587\u672c\n\u7b2c\u4e8c\u884c", style);
            AssertTimeline(
                TextGeometry.TryCreateWorldContours(data, new PointF(120, -80), data.LayoutSize, 0.2f, out var contours)
                && contours.Length > 0
                && contours.All(contour => contour.Length >= 3),
                $"Multiline CJK text did not create valid {style} outline contours.");
        }

        var cachedTextData = CreateTextData(
            "Cached vector text \u6587\u672c",
            TextFontStyle.Bold,
            TextHorizontalAlignment.Center);
        var cacheBuildsBefore = TextGeometry.CanonicalContourBuildCount;
        AssertTimeline(
            TextGeometry.TryCreateWorldContours(
                cachedTextData,
                new PointF(240, -160),
                cachedTextData.LayoutSize,
                0.15f,
                out var firstCachedContours)
            && firstCachedContours.Length > 0,
            "Text canonical contour cache regression could not create its initial geometry.");
        var expectedFirstPoint = firstCachedContours[0][0];
        firstCachedContours[0][0] = new PointF(123_456, -654_321);

        const int warmTextGeometrySamples = 32;
        var warmAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var warmTextGeometryWatch = Stopwatch.StartNew();
        PointF[][] warmCachedContours = [];
        for (var sample = 0; sample < warmTextGeometrySamples; sample++)
        {
            if (!TextGeometry.TryCreateWorldContours(
                    cachedTextData,
                    new PointF(240, -160),
                    cachedTextData.LayoutSize,
                    0.15f,
                    out warmCachedContours))
            {
                throw new InvalidOperationException("Warm text geometry cache lookup failed.");
            }
        }
        warmTextGeometryWatch.Stop();
        var warmAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - warmAllocatedBefore;
        var warmAverageMilliseconds = warmTextGeometryWatch.Elapsed.TotalMilliseconds / warmTextGeometrySamples;
        var cacheBuildsAfterWarm = TextGeometry.CanonicalContourBuildCount;
        var editedCachedTextData = cachedTextData with { Content = cachedTextData.Content + " edited" };
        AssertTimeline(
            TextGeometry.TryCreateWorldContours(
                editedCachedTextData,
                PointF.Empty,
                editedCachedTextData.LayoutSize,
                0,
                out var editedCachedContours)
            && editedCachedContours.Length > 0
            && cacheBuildsAfterWarm == cacheBuildsBefore + 1
            && TextGeometry.CanonicalContourBuildCount == cacheBuildsBefore + 2
            && warmCachedContours.Length > 0
            && warmCachedContours[0][0] == expectedFirstPoint
            && !ReferenceEquals(firstCachedContours, warmCachedContours)
            && !ReferenceEquals(firstCachedContours[0], warmCachedContours[0]),
            "Text canonical contours were rebuilt on a warm lookup, reused mutable world arrays, or ignored an edited payload.");
        var warmTextGeometryBudgetMet = warmAverageMilliseconds <= RenderCollectBudgetMilliseconds;
        Console.WriteLine($"text_geometry_warm_avg_ms={warmAverageMilliseconds:0.000}");
        Console.WriteLine($"text_geometry_warm_allocated_bytes={warmAllocatedBytes}");
        Console.WriteLine($"text_geometry_warm_canonical_rebuilds={cacheBuildsAfterWarm - cacheBuildsBefore - 1}");
        Console.WriteLine($"text_geometry_warm_budget_ms={RenderCollectBudgetMilliseconds:0.000}");
        Console.WriteLine($"text_geometry_warm_budget_met={warmTextGeometryBudgetMet.ToString().ToLowerInvariant()}");

        var missingFontData = CreateTextData(
            "Fallback",
            familyName: "V2D Missing Font Family 9E737EA7");
        AssertTimeline(
            TextGeometry.TryCreateWorldContours(
                missingFontData,
                PointF.Empty,
                missingFontData.LayoutSize,
                0,
                out var fallbackContours)
            && fallbackContours.Length > 0,
            "A missing text font did not fall back to an installed family.");

        var scene = new VectorScene();
        scene.CreateEmpty();
        var leading = scene.AddObject(
            0,
            new PointF(-12_000, 0),
            new SizeF(400, 400),
            0,
            0,
            Color.Teal,
            3,
            ShapeKind.Rectangle);
        var firstText = scene.AddTextObject(0, PointF.Empty, CreateTextData("First"), Color.Coral);
        scene.AddObject(
            0,
            new PointF(12_000, 0),
            new SizeF(400, 400),
            0,
            0,
            Color.Gold,
            3,
            ShapeKind.Ellipse);
        var secondText = scene.AddTextObject(
            0,
            new PointF(0, 5_000),
            CreateTextData("Second", TextFontStyle.Bold, TextHorizontalAlignment.Right),
            Color.RoyalBlue);
        var hasStoredFirst = scene.TryGetTextObjectData(firstText, out var storedFirst);
        var hasStoredSecond = scene.TryGetTextObjectData(secondText, out var storedSecond);
        AssertTimeline(
            hasStoredFirst && hasStoredSecond,
            "Editable text payloads were not stored with their objects.");
        var snapshot = scene.CreateSnapshot();
        AssertTimeline(
            snapshot.TextObjects.Count == 2
            && snapshot.TextObjects[firstText] == storedFirst
            && snapshot.TextObjects[secondText] == storedSecond,
            "Vector scene snapshots did not capture editable text payloads.");

        AssertTimeline(
            scene.UpdateTextObjectData(firstText, storedFirst with { Content = "First edited" })
            && scene.RemoveObjectAt(leading)
            && scene.TryGetTextObjectData(0, out var swappedSecond)
            && swappedSecond == storedSecond
            && scene.TryGetTextObjectData(1, out var retainedFirst)
            && retainedFirst.Content == "First edited",
            "Sparse editable text payloads were not remapped after swap removal.");
        scene.RestoreSnapshot(snapshot);
        AssertTimeline(
            scene.UpdateTextObjectData(firstText, storedFirst with { Content = "First edited" })
            && scene.RemoveObjects([leading]) == 1
            && scene.TryGetTextObjectData(0, out var remappedFirst)
            && remappedFirst.Content == "First edited"
            && scene.TryGetTextObjectData(2, out var remappedSecond)
            && remappedSecond == storedSecond,
            "Sparse editable text payloads were not remapped after stable compaction.");
        scene.RestoreSnapshot(snapshot);
        AssertTimeline(
            scene.TryGetTextObjectData(firstText, out var restoredFirst)
            && restoredFirst == storedFirst
            && scene.TryGetTextObjectData(secondText, out var restoredSecond)
            && restoredSecond == storedSecond,
            "Snapshot restore did not recover editable text payloads and sparse indices.");

        var celScene = new VectorScene();
        celScene.CreateEmpty();
        var originalText = celScene.AddTextObject(0, PointF.Empty, CreateTextData("Frame 0"), Color.White);
        AssertTimeline(celScene.InsertTimelineKeyframe(0, 10), "Text regression could not clone its held cel.");
        var clonedText = Enumerable.Range(0, celScene.ObjectCount)
            .Single(index => celScene.ObjectKeyframeFrame[index] == 10);
        AssertTimeline(
            celScene.TryGetTextObjectData(clonedText, out var clonedData)
            && celScene.UpdateTextObjectData(clonedText, clonedData with { Content = "Frame 10" })
            && celScene.TryGetTextObjectData(originalText, out var originalData)
            && originalData.Content == "Frame 0"
            && celScene.TryGetTextObjectData(clonedText, out var editedClone)
            && editedClone.Content == "Frame 10",
            "Editing an F6 text clone changed the source cel payload.");

        var duplicateProject = VectorProject.CreateEmpty();
        var duplicateSource = duplicateProject.DrawingObjects[0];
        duplicateSource.Scene.CreateEmpty();
        duplicateSource.Scene.AddTextObject(0, PointF.Empty, CreateTextData("Source"), Color.LimeGreen);
        AssertTimeline(
            duplicateProject.TryDuplicateDrawingObject(duplicateSource.Id, out var duplicate)
            && duplicate is not null
            && duplicate.Scene.TryGetTextObjectData(0, out var duplicateData)
            && duplicate.Scene.UpdateTextObjectData(0, duplicateData with { Content = "Duplicate" })
            && duplicateSource.Scene.TryGetTextObjectData(0, out var unchangedSource)
            && unchangedSource.Content == "Source",
            "Duplicated drawing objects did not isolate editable text payloads.");

        var selectionScene = new VectorScene();
        selectionScene.CreateEmpty();
        var selectedText = selectionScene.AddTextObject(
            0,
            new PointF(500, -300),
            CreateTextData("Select me", TextFontStyle.Italic, TextHorizontalAlignment.Center),
            Color.White);
        var selectionBounds = selectionScene.GetObjectWorldBounds(selectedText);
        selectionBounds.Inflate(20, 20);
        var marqueeMaterialization = selectionScene.MaterializeMarqueeSelectionParts(selectionBounds, 0);
        var marqueeElements = selectionScene.QueryDrawingElementsInsideBounds(selectionBounds, 0);
        var partialSelectionBounds = RectangleF.FromLTRB(
            selectionBounds.Left + selectionBounds.Width * 0.5f,
            selectionBounds.Top,
            selectionBounds.Right,
            selectionBounds.Bottom);
        var partialMarqueeMaterialization = selectionScene.MaterializeMarqueeSelectionParts(partialSelectionBounds, 0);
        var partialMarqueeElements = selectionScene.QueryDrawingElementsInsideBounds(partialSelectionBounds, 0);
        AssertTimeline(
            selectionScene.HitTest(new PointF(500, -300), 0) == selectedText
            && selectionScene.QueryDrawingObjects(selectionBounds, 0).SequenceEqual([selectedText])
            && selectionScene.IsObjectGeometryInsideBounds(selectedText, selectionBounds)
            && marqueeMaterialization.Success
            && !marqueeMaterialization.Changed
            && marqueeElements.Length == 1
            && marqueeElements[0].Key.ObjectIndex == selectedText
            && marqueeElements[0].Key.Kind == DrawingElementKind.Fill,
            "Editable text did not participate in whole-object hit testing and marquee selection.");
        AssertTimeline(
            selectionScene.QueryDrawingObjects(partialSelectionBounds, 0).SequenceEqual([selectedText])
            && !selectionScene.IsObjectGeometryInsideBounds(selectedText, partialSelectionBounds)
            && partialMarqueeMaterialization.Success
            && !partialMarqueeMaterialization.Changed
            && partialMarqueeMaterialization.SelectedObjects.Length == 0
            && partialMarqueeElements.Length == 0
            && selectionScene.ObjectCount == 1
            && selectionScene.ShapeKind[selectedText] == ShapeKind.Text
            && selectionScene.TryGetTextObjectData(selectedText, out var partialSelectionText)
            && partialSelectionText.Content == "Select me",
            "A partially overlapping marquee split or partially selected an editable text object.");

        var resizeData = CreateTextData(
            "Resize this editable text area across several wrapped words and CJK \u6587\u672c",
            TextFontStyle.Bold,
            TextHorizontalAlignment.Left);
        var resizeCenter = new PointF(1_400, -900);
        var resizeDisplaySize = new SizeF(
            resizeData.LayoutSize.Width * 1.25f,
            resizeData.LayoutSize.Height * 0.8f);
        const float resizeAngle = 0.37f;
        PointF ResizeLocalToWorld(PointF center, PointF local)
        {
            var cosine = MathF.Cos(resizeAngle);
            var sine = MathF.Sin(resizeAngle);
            return new PointF(
                center.X + local.X * cosine - local.Y * sine,
                center.Y + local.X * sine + local.Y * cosine);
        }

        var narrowedDisplayWidth = resizeDisplaySize.Width * 0.52f;
        var narrowedPointer = ResizeLocalToWorld(
            resizeCenter,
            new PointF(-resizeDisplaySize.Width * 0.5f + narrowedDisplayWidth, 0));
        var narrowed = TextGeometry.ResizeLayoutWidth(
            resizeData,
            resizeCenter,
            resizeDisplaySize,
            resizeAngle,
            resizeLeftEdge: false,
            pointerWorld: narrowedPointer,
            minimumDisplayWidth: 100);
        var fixedTopLeft = ResizeLocalToWorld(
            resizeCenter,
            new PointF(-resizeDisplaySize.Width * 0.5f, -resizeDisplaySize.Height * 0.5f));
        var narrowedTopLeft = ResizeLocalToWorld(
            narrowed.Center,
            new PointF(-narrowed.DisplaySize.Width * 0.5f, -narrowed.DisplaySize.Height * 0.5f));
        AssertTimeline(
            Math.Abs(narrowed.Data.LayoutSize.Width - narrowedDisplayWidth / 1.25f) < 0.1f
            && Math.Abs(narrowed.DisplaySize.Width / narrowed.Data.LayoutSize.Width - 1.25f) < 0.001f
            && Math.Abs(narrowed.DisplaySize.Height / narrowed.Data.LayoutSize.Height - 0.8f) < 0.001f
            && PointsNear(fixedTopLeft, narrowedTopLeft)
            && narrowed.Data.LayoutSize.Height >= resizeData.LayoutSize.Height,
            "Right-edge text area resizing changed text scale, moved its fixed top-left corner, or failed to reflow content.");

        var widenedDisplayWidth = resizeDisplaySize.Width * 1.4f;
        var widenedPointer = ResizeLocalToWorld(
            resizeCenter,
            new PointF(resizeDisplaySize.Width * 0.5f - widenedDisplayWidth, 0));
        var widened = TextGeometry.ResizeLayoutWidth(
            resizeData,
            resizeCenter,
            resizeDisplaySize,
            resizeAngle,
            resizeLeftEdge: true,
            pointerWorld: widenedPointer,
            minimumDisplayWidth: 100);
        var fixedTopRight = ResizeLocalToWorld(
            resizeCenter,
            new PointF(resizeDisplaySize.Width * 0.5f, -resizeDisplaySize.Height * 0.5f));
        var widenedTopRight = ResizeLocalToWorld(
            widened.Center,
            new PointF(widened.DisplaySize.Width * 0.5f, -widened.DisplaySize.Height * 0.5f));
        AssertTimeline(
            PointsNear(fixedTopRight, widenedTopRight),
            "Left-edge text area resizing moved its fixed top-right corner.");

        var resizeScene = new VectorScene();
        resizeScene.CreateEmpty();
        var resizeText = resizeScene.AddTextObject(0, resizeCenter, resizeData, Color.MediumSeaGreen);
        resizeScene.Width[resizeText] = resizeDisplaySize.Width;
        resizeScene.Height[resizeText] = resizeDisplaySize.Height;
        resizeScene.Angle[resizeText] = resizeAngle;
        resizeScene.CompleteDeferredBuild();
        var resizeSnapshot = resizeScene.CreateSnapshot();
        AssertTimeline(
            resizeScene.UpdateTextObjectData(resizeText, narrowed.Data),
            "Text area resize regression could not apply the reflowed text payload.");
        resizeScene.X[resizeText] = narrowed.Center.X;
        resizeScene.Y[resizeText] = narrowed.Center.Y;
        resizeScene.Width[resizeText] = narrowed.DisplaySize.Width;
        resizeScene.Height[resizeText] = narrowed.DisplaySize.Height;
        resizeScene.CompleteDeferredBuild();
        AssertTimeline(
            resizeScene.TryGetTextObjectData(resizeText, out var resizedStoredData)
            && resizedStoredData == narrowed.Data
            && Math.Abs(resizeScene.Width[resizeText] - narrowed.DisplaySize.Width) < 0.1f,
            "Text area resize did not preserve its editable payload and display width.");
        resizeScene.RestoreSnapshot(resizeSnapshot);

        using (var textAreaStage = new StageControl(resizeScene) { Size = new Size(640, 420) })
        {
            textAreaStage.SetSelection([resizeText], resizeText);
            var rightHandle = textAreaStage.GetTextAreaHandleWorldPoint(resizeText, EditHandleKind.TextAreaRight);
            var rightHandleScreen = Point.Round(textAreaStage.WorldToScreen(rightHandle.X, rightHandle.Y));
            AssertTimeline(
                textAreaStage.TextAreaOverlayVisible(resizeText)
                && textAreaStage.TextAreaResizeHandlesVisible(resizeText)
                && textAreaStage.HitTestHandle(rightHandleScreen, resizeText) == EditHandleKind.TextAreaRight,
                "A selected text object did not expose its green text-area resize handles.");
            textAreaStage.SetEditingTextObject(resizeText);
            AssertTimeline(
                !textAreaStage.TextAreaOverlayVisible(resizeText)
                && textAreaStage.HitTestHandle(rightHandleScreen, resizeText) == EditHandleKind.None,
                "Text area handles remained active while the in-place text editor was open.");
            textAreaStage.ClearEditingTextObject();
        }

        var compositionProject = VectorProject.CreateEmpty();
        var compositionSource = compositionProject.DrawingObjects[0];
        compositionSource.Scene.CreateEmpty();
        compositionSource.Scene.AddTextObject(
            0,
            PointF.Empty,
            CreateTextData("Composed", TextFontStyle.BoldItalic, TextHorizontalAlignment.Center),
            Color.CornflowerBlue);
        var compositionScene = compositionProject.Scenes[0];
        AssertTimeline(
            compositionProject.TryAddSceneInstance(
                compositionScene.Id,
                compositionSource.Id,
                new PointF(600, 400),
                0,
                out var textInstance)
            && textInstance is not null,
            "Text composition regression could not create its source instance.");
        textInstance!.RotationZ = 25;
        textInstance.ScaleX = 1.4f;
        textInstance.ScaleY = 0.8f;
        var compositionDestination = new VectorScene();
        SceneCompositionBuilder.Build(
            compositionDestination,
            compositionScene,
            compositionProject.DrawingObjects,
            0);
        AssertTimeline(
            compositionDestination.ObjectCount == 1
            && compositionDestination.ShapeKind[0] == ShapeKind.Text
            && compositionDestination.TryGetTextObjectData(0, out var composedData)
            && composedData.Content == "Composed",
            "Orthogonal composition did not preserve editable text and its payload.");

        textInstance.SkewX = 20;
        SceneCompositionBuilder.Build(
            compositionDestination,
            compositionScene,
            compositionProject.DrawingObjects,
            0);
        AssertTimeline(
            compositionDestination.ObjectCount == 1
            && compositionDestination.ShapeKind[0] == ShapeKind.Path
            && compositionDestination.TryGetPathWorldContours(0, out var skewedContours)
            && skewedContours.Length > 0
            && !compositionDestination.TryGetTextObjectData(0, out _),
            "Skewed composition did not materialize text as outline path geometry.");

        textInstance.SkewX = 0;
        textInstance.ScaleX = -1;
        SceneCompositionBuilder.Build(
            compositionDestination,
            compositionScene,
            compositionProject.DrawingObjects,
            0);
        AssertTimeline(
            compositionDestination.ObjectCount == 1
            && compositionDestination.ShapeKind[0] == ShapeKind.Path
            && compositionDestination.TryGetPathWorldContours(0, out var reflectedContours)
            && reflectedContours.Length > 0,
            "Reflected composition did not materialize text as outline path geometry.");

        var breakApartScene = new VectorScene();
        breakApartScene.CreateEmpty();
        breakApartScene.AddObject(
            0,
            new PointF(-10_000, 0),
            new SizeF(300, 300),
            0,
            0,
            Color.Teal,
            3,
            ShapeKind.Rectangle);
        var breakApartText = breakApartScene.AddTextObject(
            0,
            PointF.Empty,
            CreateTextData("Break \u62c6\u6563", TextFontStyle.Bold),
            Color.FromArgb(210, Color.Coral));
        var breakApartSnapshot = breakApartScene.CreateSnapshot();
        var expectedOrder = breakApartScene.ObjectOrder[breakApartText];
        var brokenObjects = breakApartScene.BreakApartTextObjects([breakApartText]);
        AssertTimeline(
            brokenObjects.Length == 1
            && breakApartScene.ObjectCount == 2
            && breakApartScene.ShapeKind[brokenObjects[0]] == ShapeKind.Path
            && breakApartScene.ObjectOrder[brokenObjects[0]] == expectedOrder
            && breakApartScene.Argb[brokenObjects[0]] == Color.FromArgb(210, Color.Coral).ToArgb()
            && breakApartScene.TryGetPathWorldContours(brokenObjects[0], out var brokenContours)
            && brokenContours.Length > 0
            && !breakApartScene.TryGetTextObjectData(brokenObjects[0], out _),
            "Break Apart did not replace editable text with one compound path while preserving material and order.");
        breakApartScene.RestoreSnapshot(breakApartSnapshot);
        AssertTimeline(
            breakApartScene.ShapeKind[breakApartText] == ShapeKind.Text
            && breakApartScene.TryGetTextObjectData(breakApartText, out var restoredBreakApart)
            && restoredBreakApart.Content == "Break \u62c6\u6563",
            "Undo snapshot did not restore editable text after Break Apart.");

        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "Vector2DAnimationEngine",
            $"text-svg-regression-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            const string drawingObjectId = "text-regression";
            var svgPath = Path.Combine(temporaryRoot, "Text.svg");
            DrawingObjectSvgCodec.Write(svgPath, drawingObjectId, breakApartSnapshot);
            var restoredSvg = DrawingObjectSvgCodec.Read(svgPath, drawingObjectId);
            AssertTimeline(
                restoredSvg.TextObjects.TryGetValue(breakApartText, out var restoredSvgText)
                && restoredSvgText.Content == "Break \u62c6\u6563"
                && File.ReadAllText(svgPath).Contains("fill-rule=\"evenodd\"", StringComparison.Ordinal),
                "Drawing-object SVG persistence did not preserve editable text metadata and outline preview.");
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    private static void RunTimelineLayerWorkflowRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty(layers: 2);
        scene.EditFrame = 0;
        scene.AddObject(0, PointF.Empty, new SizeF(40, 40), 0, 0, Color.Teal, 6, ShapeKind.Rectangle);
        scene.SetLinearGradient(0, Color.Black, Color.White);
        AssertTimeline(scene.InsertTimelineKeyframe(0, 2), "Timeline layer workflow could not create a neighboring drawing cel.");

        var clipboardSource = new VectorScene();
        clipboardSource.RestoreSnapshot(scene.CreateSnapshot());
        AssertTimeline(
            scene.CopyTimelineFrameFrom(clipboardSource, 0, 2, 1, 4),
            "Timeline layer workflow could not copy a drawing cel between layers.");
        AssertTimeline(
            scene.Timeline.EvaluateTargetExposure(scene.LayerIds[1], 4).HasContent
            && Enumerable.Range(0, scene.ObjectCount).Any(index => scene.ObjectLayer[index] == 1 && scene.ObjectKeyframeFrame[index] == 4),
            "Timeline cel copy did not materialize independent destination content.");

        AssertTimeline(scene.SetLayerColor(1, Color.Orange), "Timeline layer workflow could not set a layer color.");
        AssertTimeline(
            scene.SetLayerColor(0, Color.DeepSkyBlue)
            && scene.SetLayerOutline(0, true)
            && scene.HasLayerOutline
            && scene.HasDisplayLayerEffects
            && !scene.HasLayerEffects,
            "Timeline layer workflow could not enable the non-destructive layer Outline display state.");
        var blendModes = Enum.GetValues<LayerBlendMode>();
        var blendScene = new VectorScene();
        blendScene.CreateEmpty(blendModes.Length);
        var expectedBlendModes = new Dictionary<string, LayerBlendMode>(StringComparer.Ordinal);
        for (var layer = 0; layer < blendModes.Length; layer++)
        {
            AssertTimeline(
                blendScene.SetLayerBlendMode(layer, blendModes[layer]) == (blendModes[layer] != LayerBlendMode.Normal),
                $"Layer blend mode {blendModes[layer]} did not apply with the expected change result.");
            expectedBlendModes[blendScene.LayerIds[layer]] = blendModes[layer];
        }
        var removedBlendIds = new[] { blendScene.LayerIds[3], blendScene.LayerIds[17] };
        AssertTimeline(
            blendModes.Length == 27
            && blendModes.Distinct().Count() == blendModes.Length
            && blendScene.MoveLayer(0, blendScene.LayerCount - 1)
            && blendScene.RemoveLayers(removedBlendIds),
            "Layer blend modes could not survive layer reordering and removal setup.");
        var blendSnapshot = blendScene.CreateSnapshot();
        var restoredBlendScene = new VectorScene();
        restoredBlendScene.RestoreSnapshot(blendSnapshot);
        AssertTimeline(
            restoredBlendScene.LayerBlendModes.Length == restoredBlendScene.LayerCount
            && Enumerable.Range(0, restoredBlendScene.LayerCount).All(layer =>
                expectedBlendModes[restoredBlendScene.LayerIds[layer]] == restoredBlendScene.LayerBlendModes[layer]),
            "Layer blend modes lost stable layer-ID alignment during reorder, remove, or snapshot restore.");
        AssertTimeline(
            Enumerable.Range(0, scene.LayerCount).All(layer => scene.SetLayerVisible(layer, false))
            && scene.LayerVisible.All(visible => !visible)
            && Enumerable.Range(0, scene.LayerCount).All(layer => scene.SetLayerLocked(layer, true))
            && scene.LayerLocked.All(locked => locked)
            && Enumerable.Range(0, scene.LayerCount).All(layer => scene.SetLayerVisible(layer, true))
            && Enumerable.Range(0, scene.LayerCount).All(layer => scene.SetLayerLocked(layer, false)),
            "Layer batch visibility or locking state did not apply consistently.");
        AssertTimeline(scene.ToggleOnionSkin(), "Timeline workflow could not globally enable onion skin.");
        AssertTimeline(scene.SetOnionSkinRange(0, 0), "Timeline layer workflow could not disable both onion-skin ranges.");
        var emptyOnionPreview = new VectorScene();
        scene.BuildOnionSkinPreview(emptyOnionPreview, 1);
        AssertTimeline(
            !scene.HasOnionSkinPreviewEnabled
            && emptyOnionPreview.ObjectCount == 0,
            "A zero onion-skin range still rendered neighboring cels.");
        AssertTimeline(scene.SetOnionSkinRange(3, 1), "Timeline layer workflow could not set the onion-skin frame range.");
        var onionPreview = new VectorScene();
        scene.BuildOnionSkinPreview(onionPreview, 1);
        AssertTimeline(scene.SetLayerLocked(0, true), "Timeline workflow could not lock the onion-skin source layer.");
        var lockedOnionPreview = new VectorScene();
        scene.BuildOnionSkinPreview(lockedOnionPreview, 1);
        AssertTimeline(
            scene.OnionSkinEnabled
            && lockedOnionPreview.ObjectCount == 0,
            "A locked drawing layer still produced onion-skin content or disabled the global toggle.");
        AssertTimeline(scene.SetLayerLocked(0, false), "Timeline workflow could not unlock the onion-skin source layer.");
        var previousOnionPreview = new VectorScene();
        scene.BuildOnionSkinPreview(previousOnionPreview, 2);
        AssertTimeline(
            scene.HasOnionSkinPreviewEnabled
            && onionPreview.ObjectCount > 0
            && scene.ObjectCount > onionPreview.ObjectCount
            && onionPreview.IsObjectActive(0, 1)
            && onionPreview.GetGradientStops(0).All(stop => ((stop.Argb >>> 24) & 0xff) is > 0 and < 160)
            && onionPreview.GetGradientStops(0).All(stop => (stop.Argb & 0x00ffffff) == 0x006fc3da)
            && onionPreview.LayerOutline.Any(outlined => outlined)
            && onionPreview.LayerColorArgb.All(argb => (argb & 0x00ffffff) == 0x006fc3da)
            && previousOnionPreview.ObjectCount > 0
            && previousOnionPreview.GetGradientStops(0).All(stop => (stop.Argb & 0x00ffffff) == 0x00e0867e)
            && previousOnionPreview.LayerOutline.Any(outlined => outlined)
            && previousOnionPreview.LayerColorArgb.All(argb => (argb & 0x00ffffff) == 0x00e0867e),
            "Onion skin preview did not build a visible non-destructive neighboring-frame scene.");

        var sourceLayerId = scene.LayerIds[0];
        var sourceTrackId = scene.Timeline.FindTrackByTargetId(sourceLayerId)?.Id;
        AssertTimeline(scene.MoveLayer(0, 1), "Timeline layer workflow could not reorder drawing layers.");
        AssertTimeline(
            scene.LayerIds[1] == sourceLayerId
            && scene.Timeline.FindTrackByTargetId(sourceLayerId)?.Id == sourceTrackId
            && Enumerable.Range(0, scene.ObjectCount).Any(index => scene.ObjectLayer[index] == 1),
            "Drawing-layer reorder changed stable track identity or object ownership.");

        var snapshot = scene.CreateSnapshot();
        scene.SetLayerColor(0, Color.Black);
        scene.SetLayerOutline(1, false);
        scene.ToggleOnionSkin();
        scene.SetOnionSkinRange(0, 0);
        scene.RestoreSnapshot(snapshot);
        AssertTimeline(
            scene.GetLayerColor(0).ToArgb() == Color.Orange.ToArgb()
            && scene.LayerOutline[1]
            && scene.OnionSkinEnabled
            && scene.CreateSnapshot().LayerOnionSkin.All(enabled => enabled)
            && scene.OnionSkinPreviousFrames == 3
            && scene.OnionSkinNextFrames == 1,
            "Layer color or global onion-skin state did not survive a scene snapshot round trip.");

        var groupedScene = new VectorScene();
        groupedScene.CreateEmpty();
        groupedScene.EditFrame = 0;
        var contentObject = groupedScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(80, 80),
            0,
            0,
            Color.Teal,
            8,
            ShapeKind.Rectangle);
        var contentId = groupedScene.LayerIds[groupedScene.ObjectLayer[contentObject]];
        var folderLayer = groupedScene.AddFolderLayer();
        var contentLayer = Array.IndexOf(groupedScene.LayerIds, contentId);
        var maskLayer = groupedScene.AddMaskLayer();
        contentLayer = Array.IndexOf(groupedScene.LayerIds, contentId);
        var maskObject = groupedScene.AddObject(
            maskLayer,
            PointF.Empty,
            new SizeF(40, 40),
            0,
            0,
            Color.White,
            8,
            ShapeKind.Rectangle);
        var folderId = groupedScene.LayerIds[folderLayer];
        var maskId = groupedScene.LayerIds[maskLayer];
        AssertTimeline(
            groupedScene.GetLayerKind(folderLayer) == DrawingLayerKind.Folder
            && groupedScene.GetLayerKind(maskLayer) == DrawingLayerKind.Mask
            && groupedScene.GetLayerKind(contentLayer) == DrawingLayerKind.Drawing
            && groupedScene.LayerParentIds[maskLayer] == folderId
            && groupedScene.LayerParentIds[contentLayer] == folderId
            && groupedScene.TryGetMaskLayerIndex(contentLayer, out var linkedMaskLayer)
            && linkedMaskLayer == maskLayer
            && groupedScene.IsObjectActive(maskObject, 0),
            "Folder or mask creation did not retain layer kind, parent, or mask ownership.");

        AssertTimeline(
            groupedScene.SetLayerOutline(folderLayer, true)
            && groupedScene.IsLayerEffectivelyOutlined(contentLayer)
            && groupedScene.GetEffectiveLayerOutlineColor(contentLayer).ToArgb() == groupedScene.GetLayerColor(contentLayer).ToArgb(),
            "Folder Outline did not propagate while retaining the concrete content layer color.");

        AssertTimeline(
            groupedScene.InsertTimelineKeyframe(contentLayer, 2)
            && groupedScene.SetOnionSkinEnabled(true)
            && groupedScene.SetOnionSkinRange(0, 1),
            "Folder onion-skin regression could not create a neighboring cel.");
        var unlockedFolderOnionPreview = new VectorScene();
        groupedScene.BuildOnionSkinPreview(unlockedFolderOnionPreview, 1);
        AssertTimeline(
            unlockedFolderOnionPreview.ObjectCount > 0
            && groupedScene.SetLayerLocked(folderLayer, true),
            "An unlocked folder descendant did not participate in the global onion skin.");
        var lockedFolderOnionPreview = new VectorScene();
        groupedScene.BuildOnionSkinPreview(lockedFolderOnionPreview, 1);
        AssertTimeline(
            groupedScene.OnionSkinEnabled
            && !groupedScene.HasOnionSkinPreviewEnabled
            && lockedFolderOnionPreview.ObjectCount == 0
            && groupedScene.SetLayerLocked(folderLayer, false),
            "Locking a folder did not exclude its descendants from the global onion skin.");

        groupedScene.ToggleLayer(folderLayer);
        AssertTimeline(!groupedScene.IsObjectActive(contentObject, 0), "Hiding a folder did not hide its child drawing layer.");
        groupedScene.ToggleLayer(folderLayer);
        AssertTimeline(groupedScene.MoveLayer(maskLayer, 0), "Mask-layer reorder was rejected.");
        var movedContentLayer = Array.IndexOf(groupedScene.LayerIds, contentId);
        var movedMaskLayer = Array.IndexOf(groupedScene.LayerIds, maskId);
        var groupedSnapshot = groupedScene.CreateSnapshot();
        var restoredGroupedScene = new VectorScene();
        restoredGroupedScene.RestoreSnapshot(groupedSnapshot);
        AssertTimeline(
            movedContentLayer >= 0
            && movedMaskLayer >= 0
            && groupedScene.LayerParentIds[movedContentLayer] == folderId
            && groupedScene.LayerMaskIds[movedContentLayer] == maskId
            && restoredGroupedScene.GetLayerKind(Array.IndexOf(restoredGroupedScene.LayerIds, folderId)) == DrawingLayerKind.Folder
            && restoredGroupedScene.TryGetMaskLayerIndex(Array.IndexOf(restoredGroupedScene.LayerIds, contentId), out var restoredLinkedMaskLayer)
            && restoredGroupedScene.LayerIds[restoredLinkedMaskLayer] == maskId,
            "Folder or mask ID relationships did not survive layer reordering and snapshot restore.");

        folderLayer = Array.IndexOf(groupedScene.LayerIds, folderId);
        AssertTimeline(
            groupedScene.RenameLayer(movedContentLayer, "Masked Content")
            && groupedScene.ToggleLayerLocked(folderLayer)
            && groupedScene.IsLayerEffectivelyLocked(movedContentLayer)
            && !groupedScene.IsObjectSelectable(contentObject, 0),
            "Renaming or locking a folder did not update descendant selection state.");
        groupedScene.ToggleLayerLocked(folderLayer);
        AssertTimeline(
            groupedScene.ClearMaskLayerLinks(movedMaskLayer)
            && !groupedScene.TryGetMaskLayerIndex(movedContentLayer, out _),
            "A mask layer could not be detached from its content layer.");
        AssertTimeline(
            groupedScene.MoveLayerAfter(movedMaskLayer, movedContentLayer),
            "A detached mask layer could not be repositioned after its content layer.");
        movedContentLayer = Array.IndexOf(groupedScene.LayerIds, contentId);
        movedMaskLayer = Array.IndexOf(groupedScene.LayerIds, maskId);
        AssertTimeline(
            groupedScene.MoveLayerBefore(movedMaskLayer, movedContentLayer),
            "A detached mask layer could not be repositioned before its content layer.");
        movedContentLayer = Array.IndexOf(groupedScene.LayerIds, contentId);
        movedMaskLayer = Array.IndexOf(groupedScene.LayerIds, maskId);
        AssertTimeline(
            groupedScene.SetLayerMask(movedContentLayer, movedMaskLayer)
            && groupedScene.ToggleLayerLocked(movedMaskLayer)
            && !groupedScene.IsObjectSelectable(maskObject, 0)
            && !groupedScene.ShouldRenderLayerContent(movedMaskLayer),
            "A repositioned mask layer could not be rebound and locked.");
        var lockedSnapshot = groupedScene.CreateSnapshot();
        var restoredLockedScene = new VectorScene();
        restoredLockedScene.RestoreSnapshot(lockedSnapshot);
        var restoredMaskLayer = Array.IndexOf(restoredLockedScene.LayerIds, maskId);
        var restoredContentLayer = Array.IndexOf(restoredLockedScene.LayerIds, contentId);
        AssertTimeline(
            restoredMaskLayer >= 0
            && restoredContentLayer >= 0
            && restoredLockedScene.LayerNames[restoredContentLayer] == "Masked Content"
            && restoredLockedScene.LayerLocked[restoredMaskLayer]
            && restoredLockedScene.ToggleLayerLocked(restoredMaskLayer)
            && restoredLockedScene.ShouldRenderLayerContent(restoredMaskLayer)
            && restoredLockedScene.TryGetMaskLayerIndex(restoredContentLayer, out var restoredLinkedMask)
            && restoredLinkedMask == restoredMaskLayer,
            "Layer rename, lock, or rebinding did not survive a scene snapshot round trip.");

        var hierarchyScene = new VectorScene();
        hierarchyScene.CreateEmpty(layers: 2);
        var hierarchyFolder = hierarchyScene.AddFolderLayer();
        var hierarchyChild = hierarchyScene.ActiveLayer;
        var hierarchySibling = Array.IndexOf(hierarchyScene.LayerIds, hierarchyScene.LayerIds[^1]);
        AssertTimeline(
            hierarchyScene.SetLayerParent(hierarchySibling, hierarchyFolder)
            && hierarchyScene.GetLayerParentIndex(hierarchySibling) == hierarchyFolder
            && hierarchyScene.GetLayerDepth(hierarchySibling) == 1
            && !hierarchyScene.CanSetLayerParent(hierarchyFolder, hierarchySibling)
            && hierarchyScene.SetLayerParent(hierarchySibling, -1)
            && hierarchyScene.GetLayerParentIndex(hierarchySibling) < 0
            && hierarchyScene.GetLayerParentIndex(hierarchyChild) == hierarchyFolder,
            "Folder parenting did not support child assignment, removal, or cycle rejection.");

        var maskOrderingScene = new VectorScene();
        maskOrderingScene.CreateEmpty(layers: 2);
        var orderingMask = maskOrderingScene.AddMaskLayer("Ordering Mask");
        var orderingContent = maskOrderingScene.GetMaskContentLayerIndex(orderingMask);
        var orderingSibling = Enumerable.Range(0, maskOrderingScene.LayerCount)
            .First(layer => layer != orderingMask && layer != orderingContent);
        var orderingMaskId = maskOrderingScene.LayerIds[orderingMask];
        var orderingContentId = maskOrderingScene.LayerIds[orderingContent];
        var orderingSiblingId = maskOrderingScene.LayerIds[orderingSibling];
        AssertTimeline(
            maskOrderingScene.MoveLayerBefore(orderingSibling, orderingContent)
            && maskOrderingScene.LayerIds.SequenceEqual([orderingSiblingId, orderingMaskId, orderingContentId]),
            "Moving a layer before masked content split the mask group.");

        orderingMask = Array.IndexOf(maskOrderingScene.LayerIds, orderingMaskId);
        orderingSibling = Array.IndexOf(maskOrderingScene.LayerIds, orderingSiblingId);
        AssertTimeline(
            maskOrderingScene.MoveLayerAfter(orderingSibling, orderingMask)
            && maskOrderingScene.LayerIds.SequenceEqual([orderingMaskId, orderingContentId, orderingSiblingId])
            && maskOrderingScene.GetLayerDisplayOrder()
                .Select(layer => maskOrderingScene.LayerIds[layer])
                .SequenceEqual([orderingMaskId, orderingContentId, orderingSiblingId]),
            "Moving a layer after a mask did not preserve the mask-content group boundary.");

        var duplicateMaskSnapshot = maskOrderingScene.CreateSnapshot();
        duplicateMaskSnapshot.LayerMaskIds[Array.IndexOf(duplicateMaskSnapshot.LayerIds, orderingSiblingId)] = orderingMaskId;
        var normalizedMaskScene = new VectorScene();
        normalizedMaskScene.RestoreSnapshot(duplicateMaskSnapshot);
        AssertTimeline(
            normalizedMaskScene.LayerMaskIds.Count(maskId => string.Equals(maskId, orderingMaskId, StringComparison.Ordinal)) == 1
            && normalizedMaskScene.GetLayerDisplayOrder()
                .Select(layer => normalizedMaskScene.LayerIds[layer])
                .SequenceEqual([orderingMaskId, orderingContentId, orderingSiblingId]),
            "Snapshot restore did not normalize duplicate mask content or its display order.");

        orderingContent = Array.IndexOf(maskOrderingScene.LayerIds, orderingContentId);
        orderingSibling = Array.IndexOf(maskOrderingScene.LayerIds, orderingSiblingId);
        AssertTimeline(
            maskOrderingScene.MoveLayer(orderingSibling, orderingContent)
            && maskOrderingScene.LayerIds.SequenceEqual([orderingMaskId, orderingSiblingId, orderingContentId])
            && maskOrderingScene.GetLayerDisplayOrder()
                .Select(layer => maskOrderingScene.LayerIds[layer])
                .SequenceEqual([orderingMaskId, orderingContentId, orderingSiblingId]),
            "Layer display order did not keep masked content adjacent to its mask after raw layer reordering.");

        var project = VectorProject.CreateEmpty();
        var sceneDefinition = project.Scenes[0];
        AssertTimeline(project.TryAddSceneLayer(sceneDefinition.Id, out var addedLayer) && addedLayer is not null, "Scene-layer workflow could not add a layer.");
        AssertTimeline(
            project.TrySetSceneLayerColor(sceneDefinition.Id, addedLayer!.Id, Color.Coral)
            && project.TryMoveSceneLayer(sceneDefinition.Id, addedLayer.Id, 0)
            && project.TryRenameSceneLayer(sceneDefinition.Id, addedLayer.Id, "Foreground")
            && sceneDefinition.Layers[0].Id == addedLayer.Id
            && sceneDefinition.Layers[0].ColorArgb == Color.Coral.ToArgb()
            && sceneDefinition.Layers[0].Name == "Foreground",
            "Scene-layer color, rename, or reordering did not route through the project model.");
        var projectChanged = 0;
        project.Changed += (_, _) => projectChanged++;
        var sceneLayerSnapshot = sceneDefinition.CreateLayerSnapshot();
        AssertTimeline(
            project.TrySetSceneLayerBlendMode(sceneDefinition.Id, addedLayer.Id, LayerBlendMode.Overlay)
            && projectChanged == 1
            && sceneDefinition.FindLayer(addedLayer.Id)?.BlendMode == LayerBlendMode.Overlay,
            "Scene-layer blend mode did not route through the project model with one change notification.");
        sceneDefinition.RestoreLayerSnapshot(sceneLayerSnapshot);
        AssertTimeline(
            sceneDefinition.FindLayer(addedLayer.Id)?.BlendMode == LayerBlendMode.Normal,
            "Scene-layer blend mode did not restore through a scene-layer snapshot.");
    }

    private static void RunTimelineLayerRemovalRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty(layers: 4);
        for (var layer = 0; layer < scene.LayerCount; layer++)
        {
            scene.AddObject(
                layer,
                new PointF(layer * 32, 0),
                new SizeF(24, 24),
                0,
                0,
                Color.Teal,
                4,
                ShapeKind.Rectangle);
        }

        var survivingLayerIds = new[] { scene.LayerIds[0], scene.LayerIds[2] };
        var removedLayerIds = new[] { scene.LayerIds[1], scene.LayerIds[3] };
        AssertTimeline(
            scene.RemoveLayers(removedLayerIds)
            && scene.LayerCount == 2
            && scene.LayerIds.SequenceEqual(survivingLayerIds)
            && scene.ObjectCount == 2
            && scene.ObjectLayer.SequenceEqual(new ushort[] { 0, 1 })
            && scene.Timeline.Tracks.Select(track => track.TargetId).SequenceEqual(survivingLayerIds),
            "Batch drawing-layer deletion did not remove content or compact surviving layer ownership and tracks.");
        AssertTimeline(
            !scene.RemoveLayers(scene.LayerIds.ToArray()) && scene.LayerCount == 2,
            "Drawing-layer deletion allowed the final layer set to be removed.");

        var groupedScene = new VectorScene();
        groupedScene.CreateEmpty(layers: 2);
        var untouchedLayerId = groupedScene.LayerIds[1];
        var folderLayer = groupedScene.AddFolderLayer("Delete Group");
        var contentLayer = groupedScene.ActiveLayer;
        var maskLayer = groupedScene.AddMaskLayer("Delete Mask");
        var folderRemoval = groupedScene.ResolveLayerRemovalIndices([groupedScene.LayerIds[folderLayer]]);
        AssertTimeline(
            folderRemoval.Length == 3
            && folderRemoval.Contains(contentLayer)
            && folderRemoval.Contains(maskLayer)
            && groupedScene.RemoveLayers([groupedScene.LayerIds[folderLayer]])
            && groupedScene.LayerIds.SequenceEqual([untouchedLayerId]),
            "Folder deletion did not expand through child and linked mask layers.");

        var nestedProject = VectorProject.CreateEmpty();
        var container = nestedProject.DrawingObjects[0];
        var child = nestedProject.AddDrawingObject("Layer Removal Child");
        var nestedLayer = container.Scene.AddLayer("Nested Layer");
        var nestedLayerId = container.Scene.LayerIds[nestedLayer];
        AssertTimeline(
            nestedProject.TryAddDrawingObjectInstance(
                container.Id,
                child.Id,
                PointF.Empty,
                nestedLayerId,
                out var nestedInstance)
            && nestedInstance is not null,
            "Drawing-layer deletion regression could not create its nested instance.");
        var drawingSnapshot = container.Scene.CreateSnapshot();
        var nestedSnapshot = container.CreateInstanceSnapshot();
        AssertTimeline(
            nestedProject.TryRemoveDrawingObjectLayers(container.Id, [nestedLayerId])
            && container.Scene.LayerCount == 1
            && container.Instances.Count == 0,
            "Drawing-layer deletion left nested instances owned by a deleted layer.");
        container.Scene.RestoreSnapshot(drawingSnapshot);
        container.RestoreInstanceSnapshot(nestedSnapshot);
        AssertTimeline(
            container.Scene.LayerCount == 2
            && container.Instances.Count == 1
            && container.Instances[0].Id == nestedInstance!.Id
            && container.Instances[0].SceneLayerId == nestedLayerId,
            "Drawing-layer deletion snapshots did not restore nested instance ownership.");

        var composition = nestedProject.Scenes[0];
        DrawingObjectInstanceDefinition? sceneInstance = null;
        AssertTimeline(
            nestedProject.TryAddSceneLayer(composition.Id, out var compositionLayer)
            && compositionLayer is not null
            && nestedProject.TryAddSceneInstance(
                composition.Id,
                child.Id,
                PointF.Empty,
                0,
                compositionLayer.Id,
                out sceneInstance)
            && sceneInstance is not null,
            "Scene-layer deletion regression could not create its scene instance.");
        var sceneLayerSnapshot = composition.CreateLayerSnapshot();
        var sceneInstanceSnapshot = composition.CreateInstanceSnapshot();
        AssertTimeline(
            nestedProject.TryRemoveSceneLayers(composition.Id, [compositionLayer!.Id])
            && composition.Layers.Count == 1
            && composition.Instances.Count == 0,
            "Scene-layer deletion left instances owned by a deleted layer.");
        composition.RestoreLayerSnapshot(sceneLayerSnapshot);
        composition.RestoreInstanceSnapshot(sceneInstanceSnapshot);
        AssertTimeline(
            composition.Layers.Count == 2
            && composition.Instances.Count == 1
            && composition.Instances[0].Id == sceneInstance!.Id
            && composition.Instances[0].SceneLayerId == compositionLayer.Id,
            "Scene-layer deletion snapshots did not restore instance ownership.");
    }

    private static void RunTimelineKeyframePerformanceRegression()
    {
        const int layerCount = 120;
        const int objectCount = 30_000;
        const double budgetMilliseconds = 45;
        var scene = new VectorScene();
        scene.Generate(layerCount, objectCount, objectCount * 120L);
        var sourceObjectCount = Enumerable.Range(0, scene.ObjectCount)
            .Count(index => scene.ObjectLayer[index] == 0);
        var initialSnapshot = scene.CreateSnapshot();
        var queryBounds = new RectangleF(
            -scene.StageWidth * 0.5f,
            -scene.StageHeight * 0.5f,
            scene.StageWidth,
            scene.StageHeight);
        var visibleObjectCountBefore = scene.QueryObjects(queryBounds, 100).Length;
        var geometryRevisionBefore = scene.GeometryRevision;

        var watch = Stopwatch.StartNew();
        var changed = scene.InsertTimelineKeyframe(0, 100);
        watch.Stop();
        var clonedObjectCount = Enumerable.Range(0, scene.ObjectCount)
            .Count(index => scene.ObjectLayer[index] == 0 && scene.ObjectKeyframeFrame[index] == 100);
        var visibleObjects = scene.QueryObjects(queryBounds, 100);
        var rebuiltReference = new VectorScene();
        rebuiltReference.RestoreSnapshot(initialSnapshot);
        rebuiltReference.InsertTimelineKeyframe(0, 100);
        rebuiltReference.CompleteDeferredBuild();
        var budgetMet = watch.Elapsed.TotalMilliseconds <= budgetMilliseconds;

        AssertTimeline(
            changed
            && sourceObjectCount == objectCount / layerCount
            && clonedObjectCount == sourceObjectCount
            && visibleObjects.Length == visibleObjectCountBefore
            && scene.TileCount.SequenceEqual(rebuiltReference.TileCount)
            && scene.TileAtoms.SequenceEqual(rebuiltReference.TileAtoms)
            && scene.OverviewCount.SequenceEqual(rebuiltReference.OverviewCount)
            && scene.OverviewAtoms.SequenceEqual(rebuiltReference.OverviewAtoms)
            && scene.GeometryRevision <= geometryRevisionBefore + 2,
            "Incremental keyframe cloning lost cel objects, spatial-query visibility, summaries, or bounded index revisions: "
            + $"changed={changed}, source={sourceObjectCount}, cloned={clonedObjectCount}, visible={visibleObjects.Length}, "
            + $"visibleBefore={visibleObjectCountBefore}, "
            + $"geometry={geometryRevisionBefore}->{scene.GeometryRevision}.");
        Console.WriteLine($"timeline_keyframe_model_ms={watch.Elapsed.TotalMilliseconds:0.000}");
        Console.WriteLine($"timeline_keyframe_model_budget_ms={budgetMilliseconds:0.000}");
        Console.WriteLine($"timeline_keyframe_model_budget_met={budgetMet.ToString().ToLowerInvariant()}");
    }

    private static void RunVectorSceneCelOwnershipRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        var initialTrack = scene.Timeline.FindTrackByTargetId(scene.LayerIds[0])
            ?? throw new InvalidOperationException("Drawing cel regression lost its initial track.");
        AssertTimeline(
            initialTrack.Keyframes.SequenceEqual(new[] { new TimelineKeyframe(0, TimelineKeyframeKind.Blank) })
            && !scene.IsLayerActive(0, 0),
            "A new empty drawing layer did not start with a hollow blank keyframe.");
        scene.EditFrame = 0;
        var original = scene.AddObject(
            0,
            new PointF(40, 60),
            new SizeF(120, 80),
            0,
            0,
            Color.Teal,
            12,
            ShapeKind.Rectangle);
        var originalX = scene.X[original];
        AssertTimeline(
            scene.ObjectKeyframeFrame[original] == 0
            && scene.IsObjectActive(original, 0)
            && scene.IsObjectActive(original, 9)
            && initialTrack.EvaluateExposure(0).SourceKind == TimelineKeyframeKind.Populated,
            "Frame-zero drawing was not owned by and held from the initial populated cel.");

        AssertTimeline(scene.InsertTimelineKeyframe(0, 10), "F6 did not create an independent drawing cel.");
        var clone = Enumerable.Range(0, scene.ObjectCount)
            .Single(index => scene.ObjectKeyframeFrame[index] == 10);
        AssertTimeline(
            scene.ObjectCount == 2
            && scene.ObjectKeyframeFrame[original] == 0
            && scene.IsObjectActive(original, 0)
            && !scene.IsObjectActive(original, 10)
            && !scene.IsObjectActive(clone, 0)
            && scene.IsObjectActive(clone, 10),
            "F6 did not clone held content into a separately owned frame-ten cel.");

        scene.X[clone] = originalX + 320;
        scene.RebuildGeometryIndex();
        AssertTimeline(
            NearlyEqual(scene.X[original], originalX)
            && NearlyEqual(scene.X[clone], originalX + 320),
            "Editing the F6 clone changed the frame-zero source cel.");

        AssertTimeline(scene.InsertTimelineBlankKeyframe(0, 15), "F7 did not create a blank drawing cel.");
        var track = scene.Timeline.FindTrackByTargetId(scene.LayerIds[0])
            ?? throw new InvalidOperationException("Drawing cel regression lost its layer track.");
        AssertTimeline(
            scene.IsObjectActive(clone, 14)
            && !scene.IsObjectActive(clone, 15)
            && track.EvaluateExposure(15).SourceKind == TimelineKeyframeKind.Blank,
            "F7 blank exposure did not hide the previously held cel.");
        var countBeforeBlankF6 = scene.ObjectCount;
        AssertTimeline(
            !scene.InsertTimelineKeyframe(0, 15)
            && scene.ObjectCount == countBeforeBlankF6
            && track.EvaluateExposure(15).SourceKind == TimelineKeyframeKind.Blank,
            "F6 on an existing blank keyframe resurrected preceding artwork.");

        scene.EditFrame = 15;
        var promoted = scene.AddObject(
            0,
            new PointF(-180, 25),
            new SizeF(70, 70),
            0,
            0,
            Color.Coral,
            9,
            ShapeKind.Ellipse);
        AssertTimeline(
            scene.ObjectKeyframeFrame[promoted] == 15
            && scene.IsObjectActive(promoted, 15)
            && track.EvaluateExposure(15).SourceKind == TimelineKeyframeKind.Populated,
            "Drawing on a blank exposure did not promote it to a populated owned cel.");

        AssertTimeline(scene.RemoveObjectAt(clone), "Removing the independent F6 clone failed.");
        promoted = Enumerable.Range(0, scene.ObjectCount)
            .Single(index => scene.ObjectKeyframeFrame[index] == 15);
        AssertTimeline(
            scene.ObjectCount == 2
            && NearlyEqual(scene.X[original], originalX)
            && scene.IsObjectActive(original, 0)
            && scene.IsObjectActive(promoted, 15),
            "Removing the frame-ten clone damaged frame-zero or later cel content.");

        AssertTimeline(scene.InsertTimelineFrame(0, 5, 2), "F5 did not shift drawing cel ownership.");
        AssertTimeline(
            scene.ObjectKeyframeFrame[original] == 0
            && scene.ObjectKeyframeFrame[promoted] == 17,
            "F5 did not shift object ownership with its keyframe.");
        var snapshot = scene.CreateSnapshot();
        AssertTimeline(
            snapshot.ObjectKeyframeFrame.SequenceEqual(new[] { 0, 17 }),
            "Vector scene snapshot did not capture drawing cel ownership.");

        AssertTimeline(scene.RemoveTimelineFrame(0, 5, 2), "Shift+F5 did not shift drawing cel ownership left.");
        AssertTimeline(
            scene.ObjectKeyframeFrame[original] == 0
            && scene.ObjectKeyframeFrame[promoted] == 15,
            "Shift+F5 did not move surviving object ownership with its keyframe.");

        scene.RestoreSnapshot(snapshot);
        AssertTimeline(
            scene.ObjectKeyframeFrame.SequenceEqual(new[] { 0, 17 })
            && scene.IsObjectActive(0, 0)
            && scene.IsObjectActive(1, 17),
            "Vector scene snapshot restore did not recover independent cel ownership.");
    }

    private static void RunAutoKeyframeMaterializationRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        scene.EditFrame = 0;
        var editedObject = scene.AddObject(
            0,
            new PointF(24, 36),
            new SizeF(80, 48),
            0,
            0,
            Color.Teal,
            6,
            ShapeKind.Rectangle);
        var originalX = scene.X[editedObject];
        var originalOrder = scene.ObjectOrder[editedObject];
        var track = scene.Timeline.FindTrackByTargetId(scene.LayerIds[0])
            ?? throw new InvalidOperationException("Auto-key regression lost its drawing track.");
        var trackId = track.Id;
        var undoSnapshot = scene.CreateSnapshot();

        AssertTimeline(
            scene.MaterializeAutoKeyframeInPlace(0, 8)
            && scene.ObjectCount == 2
            && scene.ObjectKeyframeFrame[editedObject] == 8
            && scene.ObjectKeyframeFrame[1] == 0
            && scene.ObjectOrder[editedObject] == originalOrder
            && scene.ObjectOrder[1] == originalOrder
            && track.EvaluateExposure(8) is
            {
                IsKeyframe: true,
                HasContent: true,
                SourceKeyframeFrame: 8
            },
            "Auto Key did not preserve the live object index while isolating the held source cel at the playhead.");

        scene.X[editedObject] = originalX + 200;
        scene.RebuildGeometryIndex();
        AssertTimeline(
            NearlyEqual(scene.X[1], originalX)
            && NearlyEqual(scene.X[editedObject], originalX + 200)
            && !scene.MaterializeAutoKeyframeInPlace(0, 8)
            && scene.ObjectCount == 2
            && scene.Timeline.FindTrack(trackId) is not null,
            "Auto Key changed the historical cel, duplicated an existing playhead key, or replaced stable track identity.");

        scene.RestoreSnapshot(undoSnapshot);
        track = scene.Timeline.FindTrack(trackId)
            ?? throw new InvalidOperationException("Auto-key snapshot restore replaced stable track identity.");
        AssertTimeline(
            scene.ObjectCount == 1
            && scene.ObjectKeyframeFrame[editedObject] == 0
            && NearlyEqual(scene.X[editedObject], originalX)
            && !track.EvaluateExposure(8).IsKeyframe,
            "One snapshot restore did not remove both the auto-generated cel and its canvas edit.");

        var blankScene = new VectorScene();
        blankScene.CreateEmpty();
        AssertTimeline(
            blankScene.MaterializeAutoKeyframeInPlace(0, 7),
            "Auto Key did not place an explicit blank key at the playhead before drawing on a blank hold.");
        var blankTrack = blankScene.Timeline.FindTrackByTargetId(blankScene.LayerIds[0])
            ?? throw new InvalidOperationException("Blank auto-key regression lost its drawing track.");
        blankScene.EditFrame = 7;
        var drawnObject = blankScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(24, 24),
            0,
            0,
            Color.Coral,
            6,
            ShapeKind.Ellipse);
        AssertTimeline(
            blankScene.ObjectKeyframeFrame[drawnObject] == 7
            && blankTrack.EvaluateExposure(7) is
            {
                IsKeyframe: true,
                HasContent: true,
                SourceKeyframeFrame: 7
            },
            "Drawing after Auto Key wrote into the held blank source instead of the playhead cel.");
    }

    private static void RunVectorSceneKeyframeBoundaryRegression()
    {
        var heldBlankScene = new VectorScene();
        heldBlankScene.CreateEmpty();
        heldBlankScene.EditFrame = 0;
        heldBlankScene.AddObject(0, PointF.Empty, new SizeF(20, 20), 0, 0, Color.Teal, 6, ShapeKind.Rectangle);
        AssertTimeline(heldBlankScene.InsertTimelineBlankKeyframe(0, 5), "Held-blank setup failed.");
        heldBlankScene.EditFrame = 7;
        var blankDrawing = heldBlankScene.AddObject(0, new PointF(40, 0), new SizeF(20, 20), 0, 0, Color.Coral, 6, ShapeKind.Ellipse);
        var heldBlankTrack = heldBlankScene.Timeline.FindTrackByTargetId(heldBlankScene.LayerIds[0])
            ?? throw new InvalidOperationException("Held-blank regression lost its track.");
        AssertTimeline(
            heldBlankScene.ObjectKeyframeFrame[blankDrawing] == 5
            && heldBlankTrack.Keyframes.All(keyframe => keyframe.Frame != 7)
            && heldBlankScene.IsObjectActive(blankDrawing, 5),
            "Drawing inside a held blank exposure created a hidden playhead key instead of populating its source cel.");

        var deletionScene = new VectorScene();
        deletionScene.CreateEmpty();
        deletionScene.EditFrame = 0;
        var onlyObject = deletionScene.AddObject(0, PointF.Empty, new SizeF(20, 20), 0, 0, Color.Teal, 6, ShapeKind.Rectangle);
        AssertTimeline(deletionScene.RemoveObjectAt(onlyObject), "Last-cel-object deletion failed.");
        var deletionTrack = deletionScene.Timeline.FindTrackByTargetId(deletionScene.LayerIds[0])
            ?? throw new InvalidOperationException("Deletion regression lost its track.");
        AssertTimeline(
            deletionTrack.EvaluateExposure(0).SourceKind == TimelineKeyframeKind.Blank
            && !deletionScene.IsLayerActive(0, 0),
            "Deleting the last object left a solid populated keyframe marker.");

        var removeScene = new VectorScene();
        removeScene.CreateEmpty();
        removeScene.EditFrame = 0;
        removeScene.AddObject(0, PointF.Empty, new SizeF(20, 20), 0, 0, Color.Teal, 6, ShapeKind.Rectangle);
        AssertTimeline(removeScene.InsertTimelineKeyframe(0, 10), "Removal carry setup did not create frame-ten cel.");
        var frameTenObject = Enumerable.Range(0, removeScene.ObjectCount)
            .Single(index => removeScene.ObjectKeyframeFrame[index] == 10);
        removeScene.X[frameTenObject] = 240;
        AssertTimeline(removeScene.InsertTimelineBlankKeyframe(0, 20), "Removal carry setup did not create its ending key.");
        AssertTimeline(removeScene.RemoveTimelineFrame(0, 10), "Shift+F5 did not remove the first frame of a held cel.");
        var carriedObject = Enumerable.Range(0, removeScene.ObjectCount)
            .Single(index => Math.Abs(removeScene.X[index] - 240) < 0.001f);
        AssertTimeline(
            removeScene.ObjectKeyframeFrame[carriedObject] == 10
            && removeScene.IsObjectActive(carriedObject, 10),
            "Shift+F5 deleted a cel whose later held frames should have survived.");

        var frameZeroScene = new VectorScene();
        frameZeroScene.CreateEmpty();
        frameZeroScene.EditFrame = 0;
        var frameZeroObject = frameZeroScene.AddObject(0, PointF.Empty, new SizeF(20, 20), 0, 0, Color.Teal, 6, ShapeKind.Rectangle);
        AssertTimeline(frameZeroScene.InsertTimelineFrame(0, 0), "F5 at frame zero failed.");
        AssertTimeline(
            frameZeroScene.ObjectKeyframeFrame[frameZeroObject] == 0
            && frameZeroScene.IsObjectActive(frameZeroObject, 0),
            "F5 at frame zero shifted away the mandatory first keyframe.");
        AssertTimeline(frameZeroScene.RemoveTimelineFrame(0, 0), "Shift+F5 at frame zero failed.");
        AssertTimeline(
            frameZeroScene.ObjectKeyframeFrame[frameZeroObject] == 0
            && frameZeroScene.IsObjectActive(frameZeroObject, 0),
            "Shift+F5 at frame zero deleted content whose held exposure survived.");
        AssertTimeline(frameZeroScene.ClearTimelineKeyframe(0, 0), "Shift+F6 at frame zero failed.");
        var frameZeroTrack = frameZeroScene.Timeline.FindTrackByTargetId(frameZeroScene.LayerIds[0])
            ?? throw new InvalidOperationException("Frame-zero regression lost its track.");
        AssertTimeline(
            frameZeroTrack.Keyframes[0] == new TimelineKeyframe(0, TimelineKeyframeKind.Blank)
            && frameZeroScene.ObjectCount == 0,
            "Clearing frame zero removed the mandatory blank keyframe marker.");

        var shortTrackScene = new VectorScene();
        shortTrackScene.CreateEmpty();
        var shortTrack = shortTrackScene.Timeline.FindTrackByTargetId(shortTrackScene.LayerIds[0])
            ?? throw new InvalidOperationException("Short-track regression lost its track.");
        shortTrackScene.Timeline.SetTrackDuration(shortTrack.Id, 5);
        shortTrackScene.EditFrame = 20;
        var lateObject = shortTrackScene.AddObject(0, PointF.Empty, new SizeF(20, 20), 0, 0, Color.Teal, 6, ShapeKind.Rectangle);
        AssertTimeline(
            shortTrack.Duration == 21
            && shortTrackScene.ObjectKeyframeFrame[lateObject] == 20
            && shortTrackScene.IsObjectActive(lateObject, 20),
            "Drawing beyond a shorter track silently wrote into an earlier cel.");

        var shortF6Scene = new VectorScene();
        shortF6Scene.CreateEmpty();
        shortF6Scene.EditFrame = 0;
        shortF6Scene.AddObject(0, PointF.Empty, new SizeF(20, 20), 0, 0, Color.Teal, 6, ShapeKind.Rectangle);
        var shortF6Track = shortF6Scene.Timeline.FindTrackByTargetId(shortF6Scene.LayerIds[0])
            ?? throw new InvalidOperationException("Short F6 regression lost its track.");
        shortF6Scene.Timeline.SetTrackDuration(shortF6Track.Id, 5);
        AssertTimeline(
            shortF6Scene.InsertTimelineKeyframe(0, 4),
            "Short F6 regression did not create a populated key at the old track end.");
        AssertTimeline(shortF6Scene.InsertTimelineKeyframe(0, 20), "F6 beyond a shorter track failed.");
        var lateClone = Enumerable.Range(0, shortF6Scene.ObjectCount)
            .Single(index => shortF6Scene.ObjectKeyframeFrame[index] == 20);
        AssertTimeline(
            shortF6Track.Duration == 21 && shortF6Scene.IsObjectActive(lateClone, 20),
            "F6 beyond a shorter track did not extend the track and clone its terminal cel at the visible playhead.");

        shortF6Scene.Timeline.SetTrackDuration(shortF6Track.Id, 5);
        AssertTimeline(shortF6Scene.InsertTimelineFrame(0, 20), "F5 beyond a shorter track failed.");
        AssertTimeline(shortF6Track.Duration == 21, "F5 beyond a shorter track acted at its old end instead of the visible playhead.");

        var mergeScene = new VectorScene();
        mergeScene.CreateEmpty();
        mergeScene.EditFrame = 0;
        var originalFill = mergeScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(80, 80),
            0,
            0,
            Color.Teal,
            6,
            ShapeKind.Rectangle);
        AssertTimeline(mergeScene.InsertTimelineKeyframe(0, 10), "Cross-cel merge setup did not create frame ten.");
        mergeScene.EditFrame = 10;
        var overlappingFill = mergeScene.AddObject(
            0,
            new PointF(20, 0),
            new SizeF(80, 80),
            0,
            0,
            Color.Teal,
            6,
            ShapeKind.Rectangle);
        var mergedFill = mergeScene.MergeSameColorFillsAround(overlappingFill, connectNearby: false, frame: 10);
        var mergeTrack = mergeScene.Timeline.FindTrackByTargetId(mergeScene.LayerIds[0])
            ?? throw new InvalidOperationException("Cross-cel merge regression lost its track.");
        AssertTimeline(
            mergeScene.IsObjectActive(originalFill, 0)
            && !mergeScene.IsObjectActive(originalFill, 10)
            && mergeScene.IsObjectActive(mergedFill, 10)
            && mergeTrack.EvaluateExposure(0).SourceKind == TimelineKeyframeKind.Populated
            && Enumerable.Range(0, mergeScene.ObjectCount).Count(index => mergeScene.ObjectKeyframeFrame[index] == 0) == 1,
            "Merging fills in one cel deleted content or desynchronized the marker in another cel.");
    }

    private static void RunSceneInstanceTimelineRegression()
    {
        var project = new VectorProject();
        var scene = project.Scenes[0];
        var firstDrawingObject = project.DrawingObjects[0];
        var secondDrawingObject = project.AddDrawingObject("Second source");
        var thirdDrawingObject = project.AddDrawingObject("Third source");
        var addedFirst = project.TryAddSceneInstance(scene.Id, firstDrawingObject.Id, PointF.Empty, 0, out var first);
        var addedSecond = project.TryAddSceneInstance(scene.Id, secondDrawingObject.Id, PointF.Empty, 1, out var second);
        AssertTimeline(
            addedFirst
            && first is not null
            && addedSecond
            && second is not null,
            "Validated scene-instance insertion was rejected.");

        var rejectedDuplicateSceneInstanceId = false;
        try
        {
            scene.AddInstance(project, new DrawingObjectInstanceDefinition
            {
                Id = first!.Id,
                DrawingObjectId = thirdDrawingObject.Id
            });
        }
        catch (InvalidOperationException)
        {
            rejectedDuplicateSceneInstanceId = true;
        }

        AssertTimeline(
            rejectedDuplicateSceneInstanceId && scene.Instances.Count == 2,
            "A scene accepted duplicate instance IDs.");

        var timeline = scene.Timeline;
        var firstLayer = scene.Layers[0];
        var firstTrack = timeline.FindTrackByTargetId(firstLayer.Id)
            ?? throw new InvalidOperationException("Scene timeline did not create a track for its first layer.");
        AssertTimeline(
            first!.SceneLayerId == firstLayer.Id
            && second!.SceneLayerId == firstLayer.Id
            && scene.InstancesInLayer(firstLayer.Id).Count == 2
            && scene.TimelineTargetIds.SequenceEqual(new[] { firstLayer.Id })
            && timeline.Tracks.Select(item => item.TargetId).SequenceEqual(scene.TimelineTargetIds)
            && timeline.EvaluateTargetExposure(firstLayer.Id, 0).HasContent,
            "Scene instances in one layer did not share a populated frame-zero layer track.");

        firstDrawingObject.Scene.AddObject(
            0,
            new PointF(-20, 0),
            new SizeF(20, 20),
            0,
            0,
            Color.Teal,
            2,
            ShapeKind.Rectangle);
        secondDrawingObject.Scene.AddObject(
            0,
            new PointF(20, 0),
            new SizeF(20, 20),
            0,
            0,
            Color.Coral,
            2,
            ShapeKind.Rectangle);
        var groupedDestination = new VectorScene();
        var groupedComposition = SceneCompositionBuilder.Build(groupedDestination, scene, project.DrawingObjects, 0);
        AssertTimeline(
            groupedDestination.ObjectCount == 2
            && groupedComposition.ObjectOwners.Values.Select(owner => owner.RootInstanceId).Distinct().Count() == 2,
            "Multiple drawing objects in one scene layer were not composed together.");

        timeline.InsertBlankKeyframe(firstTrack.Id, 10);
        var expectedFirstTrackId = firstTrack.Id;
        var removedFirst = scene.RemoveInstance(first);
        var addedLayer = project.TryAddSceneLayer(scene.Id, out var secondLayer);
        DrawingObjectInstanceDefinition? third = null;
        var addedThird = secondLayer is not null
            && project.TryAddSceneInstance(scene.Id, thirdDrawingObject.Id, PointF.Empty, 2, secondLayer.Id, out third);
        AssertTimeline(
            removedFirst
            && addedThird
            && third is not null,
            "Scene-instance removal, scene-layer insertion, or reinsertion failed during timeline synchronization.");

        var synchronizedFirst = timeline.FindTrackByTargetId(firstLayer.Id);
        var secondTrack = secondLayer is null ? null : timeline.FindTrackByTargetId(secondLayer.Id);
        AssertTimeline(
            scene.Layers.Count == 2
            && timeline.Tracks.Select(item => item.TargetId).SequenceEqual(scene.TimelineTargetIds)
            && scene.InstancesInLayer(firstLayer.Id).Count == 1
            && scene.InstancesInLayer(secondLayer!.Id).Count == 1,
            "Scene timeline tracks did not synchronize to scene layers or retain grouped instances.");
        AssertTimeline(
            synchronizedFirst is not null
            && synchronizedFirst.Id == expectedFirstTrackId
            && synchronizedFirst.Keyframes.Any(item => item.Frame == 10 && item.Kind == TimelineKeyframeKind.Blank),
            "Scene timeline synchronization discarded a surviving scene-layer track.");
        AssertTimeline(
            secondTrack is not null
            && third!.SceneLayerId == secondLayer!.Id
            && secondTrack.EvaluateExposure(0).HasContent,
            "A new scene layer did not expose its grouped drawing-object instance at frame zero.");
    }

    private static void RunProjectDocumentStructureRegression()
    {
        var project = new VectorProject();
        AssertTimeline(
            project.DrawingObjects.Count == 1
            && project.DrawingObjects[0].CanDraw
            && project.DrawingObjects[0].Scene.LayerCount == 1
            && project.DrawingObjects[0].FrameCount == 1
            && project.DrawingObjects[0].Timeline.Tracks.Count == 1
            && project.DrawingObjects[0].Timeline.Tracks[0].Duration == 1
            && project.Scenes.Count == 1
            && !project.Scenes[0].CanDraw
            && project.Scenes[0].Layers.Count == 1
            && project.Scenes[0].FrameCount == 1
            && project.Scenes[0].Timeline.Tracks.Count == 1
            && project.Scenes[0].Timeline.Tracks[0].Duration == 1
            && project.DrawingObjects is not ICollection<DrawingObjectDefinition> { IsReadOnly: false }
            && project.Scenes is not ICollection<SceneDefinition> { IsReadOnly: false },
            "A new project did not create one layer and one frame for its drawing and scene timelines.");

        var first = project.DrawingObjects[0];
        var second = project.AddDrawingObject("Nested B");
        var third = project.AddDrawingObject("Nested C");
        second.Scene.AddObject(0, PointF.Empty, new SizeF(80, 60), 0, 0, Color.Teal, 8, ShapeKind.Rectangle);
        third.Scene.AddObject(0, PointF.Empty, new SizeF(80, 60), 0, 0, Color.Coral, 8, ShapeKind.Rectangle);
        AssertTimeline(
            project.TryAddDrawingObjectInstance(first.Id, second.Id, PointF.Empty, out var firstToSecond)
            && firstToSecond is not null
            && project.TryAddDrawingObjectInstance(second.Id, third.Id, PointF.Empty, out var secondToThird)
            && secondToThird is not null,
            "Valid drawing-object containment was rejected.");
        AssertTimeline(
            project.TryAddDrawingObjectInstance(first.Id, third.Id, new PointF(120, 0), out var firstToThird)
            && firstToThird is not null
            && firstToSecond!.SceneLayerId == first.Scene.LayerIds[0]
            && firstToThird.SceneLayerId == first.Scene.LayerIds[0]
            && first.InstancesInLayer(first.Scene.LayerIds[0]).Count == 2,
            "A drawing layer did not retain multiple nested drawing-object instances.");
        var initialNestedStackScene = new VectorScene();
        var initialNestedStack = SceneCompositionBuilder.BuildDrawingObjectChildren(
            initialNestedStackScene,
            first,
            project.DrawingObjects,
            0);
        var initialNestedRootIds = initialNestedStack.ObjectOwners.Values.Select(owner =>
                string.IsNullOrWhiteSpace(owner.RootInstanceId) ? owner.InstanceId : owner.RootInstanceId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        AssertTimeline(
            initialNestedRootIds.SequenceEqual([firstToSecond!.Id, firstToThird!.Id]),
            $"Nested drawing-object composition did not expose its initial same-layer stack order: {string.Join(',', initialNestedRootIds)}.");
        AssertTimeline(
            project.TryMoveDrawingObjectInstancesInLayer(first.Id, [firstToSecond.Id], 1),
            "Nested drawing-object stacking rejected a valid forward move.");
        var movedNestedStackScene = new VectorScene();
        var movedNestedStack = SceneCompositionBuilder.BuildDrawingObjectChildren(
            movedNestedStackScene,
            first,
            project.DrawingObjects,
            0);
        var movedNestedRootIds = movedNestedStack.ObjectOwners.Values.Select(owner =>
                string.IsNullOrWhiteSpace(owner.RootInstanceId) ? owner.InstanceId : owner.RootInstanceId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        AssertTimeline(
            movedNestedRootIds.SequenceEqual([firstToThird.Id, firstToSecond.Id])
            && project.TryMoveDrawingObjectInstancesInLayer(first.Id, [firstToSecond.Id], -1),
            $"Nested drawing-object stacking did not change the flattened composition order: {string.Join(',', movedNestedRootIds)}.");
        var nestedLayer = first.Scene.AddLayer("Nested Layer");
        AssertTimeline(
            project.TryAddDrawingObjectInstance(
                first.Id,
                third.Id,
                new PointF(240, 0),
                first.Scene.LayerIds[nestedLayer],
                out var layeredNested)
            && layeredNested is not null
            && layeredNested.SceneLayerId == first.Scene.LayerIds[nestedLayer]
            && first.InstancesInLayer(first.Scene.LayerIds[nestedLayer]).Count == 1,
            "A nested drawing-object instance did not retain its selected host layer.");
        AssertTimeline(
            project.CanMoveDrawingObjectInstancesInLayer(first.Id, [firstToSecond!.Id], 1)
            && project.TryMoveDrawingObjectInstancesInLayer(first.Id, [firstToSecond.Id], 1)
            && first.InstancesInLayer(first.Scene.LayerIds[0]).SequenceEqual([firstToThird!, firstToSecond])
            && first.InstancesInLayer(first.Scene.LayerIds[nestedLayer]).SequenceEqual([layeredNested!])
            && project.TryMoveDrawingObjectInstancesInLayer(first.Id, [firstToSecond.Id], -1)
            && first.InstancesInLayer(first.Scene.LayerIds[0]).SequenceEqual([firstToSecond, firstToThird!]),
            "Nested drawing-object stacking did not move one level within its host layer.");
        AssertTimeline(
            !project.CanContainDrawingObject(first.Id, first.Id)
            && !project.TryAddDrawingObjectInstance(first.Id, first.Id, PointF.Empty, out _),
            "A drawing object was allowed to contain itself.");
        AssertTimeline(
            !project.CanContainDrawingObject(third.Id, first.Id)
            && !project.TryAddDrawingObjectInstance(third.Id, first.Id, PointF.Empty, out _),
            "An indirect drawing-object containment cycle was allowed.");

        var validInstanceCount = first.Instances.Count;
        var rejectedDirectMutation = false;
        try
        {
            first.AddInstance(project, new DrawingObjectInstanceDefinition { DrawingObjectId = first.Id });
        }
        catch (InvalidOperationException)
        {
            rejectedDirectMutation = true;
        }

        var rejectedLayerIdCollision = false;
        var rejectedInstanceIdCollision = false;
        try
        {
            first.AddInstance(project, new DrawingObjectInstanceDefinition
            {
                Id = first.Scene.LayerIds[0],
                DrawingObjectId = third.Id
            });
        }
        catch (InvalidOperationException)
        {
            rejectedLayerIdCollision = true;
        }

        try
        {
            first.AddInstance(project, new DrawingObjectInstanceDefinition
            {
                Id = firstToSecond!.Id,
                DrawingObjectId = third.Id
            });
        }
        catch (InvalidOperationException)
        {
            rejectedInstanceIdCollision = true;
        }

        AssertTimeline(
            rejectedDirectMutation
            && rejectedLayerIdCollision
            && rejectedInstanceIdCollision
            && first.Instances.Count == validInstanceCount
            && first.Instances.All(instance => !string.Equals(instance.DrawingObjectId, first.Id, StringComparison.Ordinal))
            && first.Instances is ICollection<DrawingObjectInstanceDefinition> { IsReadOnly: true },
            "The drawing-object model exposed a mutation path that bypassed containment validation.");

        var firstTrack = first.Timeline.FindTrackByTargetId(first.Scene.LayerIds[0]);
        var nestedLayerTrack = first.Timeline.FindTrackByTargetId(first.Scene.LayerIds[nestedLayer]);
        AssertTimeline(
            firstTrack is not null
            && firstTrack.Keyframes.SequenceEqual(new[] { new TimelineKeyframe(0, TimelineKeyframeKind.Populated) })
            && nestedLayerTrack is not null
            && first.Timeline.FindTrackByTargetId(firstToSecond!.Id) is null
            && first.Timeline.FindTrackByTargetId(firstToThird!.Id) is null
            && ReferenceEquals(first.Timeline, first.Scene.Timeline)
            && first.TimelineTargetIds.SequenceEqual(first.Scene.LayerIds),
            "A drawing object exposed nested instances as generated timeline layers.");
        AssertTimeline(
            first.Scene.InsertTimelineBlankKeyframe(0, 3)
            && first.Scene.InsertTimelineBlankKeyframe(nestedLayer, 4),
            "The drawing-object timeline rejected a host-layer key edit.");
        first.SynchronizeTimelineTracks();
        AssertTimeline(
            first.Timeline.FindTrackByTargetId(first.Scene.LayerIds[0])?.EvaluateExposure(3).HasContent == false
            && first.Timeline.FindTrackByTargetId(first.Scene.LayerIds[nestedLayer])?.EvaluateExposure(3).HasContent == true
            && first.Timeline.FindTrackByTargetId(first.Scene.LayerIds[nestedLayer])?.EvaluateExposure(4).HasContent == false,
            "Nested-instance exposure did not follow its host drawing layer.");

        var nestedKeyframeProject = VectorProject.CreateEmpty();
        var nestedKeyframeRoot = nestedKeyframeProject.DrawingObjects[0];
        var nestedKeyframeChild = nestedKeyframeProject.AddDrawingObject("Nested keyframe child");
        nestedKeyframeChild.Scene.AddObject(
            0,
            new PointF(24, 18),
            new SizeF(80, 48),
            0,
            0,
            Color.CadetBlue,
            8,
            ShapeKind.Rectangle);
        AssertTimeline(
            nestedKeyframeProject.TryAddDrawingObjectInstance(
                nestedKeyframeRoot.Id,
                nestedKeyframeChild.Id,
                PointF.Empty,
                out var nestedKeyframeInstance)
            && nestedKeyframeInstance is not null,
            "Nested-keyframe regression could not create its child instance.");
        var nestedKeyframeTrack = nestedKeyframeRoot.Timeline.FindTrackByTargetId(
            nestedKeyframeRoot.Scene.LayerIds[0])
            ?? throw new InvalidOperationException("Nested-keyframe regression lost its host-layer track.");
        AssertTimeline(
            nestedKeyframeRoot.Scene.InsertTimelineKeyframe(0, 5)
            && nestedKeyframeTrack.EvaluateExposure(5) is
            {
                IsKeyframe: true,
                HasContent: true,
                SourceKind: TimelineKeyframeKind.Populated
            }
            && nestedKeyframeRoot.Instances.Contains(nestedKeyframeInstance!),
            "F6 converted a nested-instance-only drawing layer into a blank keyframe.");
        AssertTimeline(
            nestedKeyframeInstance!.SetPositionAtFrame(5, new PointF(120, 60))
            && PointsNear(nestedKeyframeInstance.EvaluatePosition(4), PointF.Empty)
            && PointsNear(nestedKeyframeInstance.EvaluatePosition(5), new PointF(120, 60)),
            "A nested instance did not retain a held position key on its host timeline.");
        nestedKeyframeInstance.InsertPositionFrames(4, 2);
        AssertTimeline(
            PointsNear(nestedKeyframeInstance.EvaluatePosition(6), PointF.Empty)
            && PointsNear(nestedKeyframeInstance.EvaluatePosition(7), new PointF(120, 60)),
            "Inserting timeline frames did not shift a nested instance position key.");
        nestedKeyframeInstance.RemovePositionFrames(4, 2);
        AssertTimeline(
            nestedKeyframeInstance.PositionKeyframes.Select(keyframe => keyframe.Frame).SequenceEqual([5])
            && PointsNear(nestedKeyframeInstance.EvaluatePosition(5), new PointF(120, 60)),
            "Removing timeline frames did not restore the nested instance position-key timing.");
        var leadingFrameRemovalInstance = new DrawingObjectInstanceDefinition();
        leadingFrameRemovalInstance.SetPositionAtFrame(2, new PointF(48, 24));
        leadingFrameRemovalInstance.RemovePositionFrames(0, 2);
        AssertTimeline(
            leadingFrameRemovalInstance.PositionKeyframes.Count == 0
            && PointsNear(leadingFrameRemovalInstance.EvaluatePosition(0), new PointF(48, 24)),
            "Removing leading frames did not fold a shifted position key into the base instance position.");
        var stateFrameEditInstance = new DrawingObjectInstanceDefinition();
        var shiftedState = stateFrameEditInstance.EvaluateState(3) with
        {
            RotationZ = 42,
            SkewX = 9,
            ScaleX = 1.8f,
            PlaybackFps = 20,
            PlaybackMode = DrawingObjectPlaybackMode.HoldFrame,
            HoldFrame = 2
        };
        stateFrameEditInstance.SetStateAtFrame(3, shiftedState);
        stateFrameEditInstance.InsertStateFrames(1, 2);
        AssertTimeline(
            stateFrameEditInstance.StateKeyframes.Select(keyframe => keyframe.Frame).SequenceEqual([5])
            && stateFrameEditInstance.EvaluateState(4).RotationZ == 0
            && stateFrameEditInstance.EvaluateState(5) == shiftedState,
            "Inserting frames did not move the complete drawing-object instance state.");
        stateFrameEditInstance.RemoveStateFrames(1, 2);
        AssertTimeline(
            stateFrameEditInstance.StateKeyframes.Select(keyframe => keyframe.Frame).SequenceEqual([3])
            && stateFrameEditInstance.EvaluateState(3) == shiftedState
            && stateFrameEditInstance.SetStateAtFrame(6, stateFrameEditInstance.EvaluateState(6))
            && stateFrameEditInstance.SetStateAtFrame(3, shiftedState with { RotationZ = 18 })
            && stateFrameEditInstance.EvaluateState(6) == shiftedState
            && stateFrameEditInstance.RemoveStateKeyframe(3)
            && stateFrameEditInstance.EvaluateState(3).RotationZ == 0
            && stateFrameEditInstance.EvaluateState(6) == shiftedState,
            "Removing frames or clearing a keyframe left partial drawing-object instance state behind.");
        var disabledNestedOnionSkinPreview = new VectorScene();
        SceneCompositionBuilder.BuildDrawingObjectOnionSkin(
            disabledNestedOnionSkinPreview,
            nestedKeyframeRoot,
            nestedKeyframeProject.DrawingObjects,
            4);
        AssertTimeline(
            !nestedKeyframeRoot.Scene.HasOnionSkinPreviewEnabled
            && disabledNestedOnionSkinPreview.ObjectCount == 0,
            "A disabled nested-instance onion skin still built a preview scene.");
        AssertTimeline(
            nestedKeyframeRoot.Scene.ToggleOnionSkin()
            && nestedKeyframeRoot.Scene.SetOnionSkinRange(1, 1),
            "Nested-instance onion-skin regression could not enable the drawing timeline.");
        var nestedOnionSkinPreview = new VectorScene();
        SceneCompositionBuilder.BuildDrawingObjectOnionSkin(
            nestedOnionSkinPreview,
            nestedKeyframeRoot,
            nestedKeyframeProject.DrawingObjects,
            4);
        AssertTimeline(
            nestedKeyframeRoot.Scene.HasOnionSkinPreviewEnabled
            && nestedOnionSkinPreview.ObjectCount == 1
            && NearlyEqual(nestedOnionSkinPreview.X[0], 144)
            && NearlyEqual(nestedOnionSkinPreview.Y[0], 78)
            && Color.FromArgb(nestedOnionSkinPreview.Argb[0]).A is > 0 and < 255,
            "Drawing-object onion skin did not show the nested instance at its neighboring keyed position.");
        AssertTimeline(
            nestedKeyframeChild.Scene.SetLayerLocked(0, true),
            "Nested-instance onion-skin regression could not lock the child layer.");
        var lockedChildOnionSkinPreview = new VectorScene();
        SceneCompositionBuilder.BuildDrawingObjectOnionSkin(
            lockedChildOnionSkinPreview,
            nestedKeyframeRoot,
            nestedKeyframeProject.DrawingObjects,
            4);
        AssertTimeline(
            lockedChildOnionSkinPreview.ObjectCount == 0
            && nestedKeyframeChild.Scene.SetLayerLocked(0, false)
            && nestedKeyframeRoot.Scene.SetLayerLocked(0, true),
            "A locked layer inside a nested drawing object still produced onion-skin content.");
        var lockedHostOnionSkinPreview = new VectorScene();
        SceneCompositionBuilder.BuildDrawingObjectOnionSkin(
            lockedHostOnionSkinPreview,
            nestedKeyframeRoot,
            nestedKeyframeProject.DrawingObjects,
            4);
        AssertTimeline(
            nestedKeyframeRoot.Scene.OnionSkinEnabled
            && lockedHostOnionSkinPreview.ObjectCount == 0
            && nestedKeyframeRoot.Scene.SetLayerLocked(0, false),
            "A locked nested-instance host layer still produced onion-skin content or disabled the global toggle.");
        var nestedKeyframePreview = new VectorScene();
        var nestedKeyframeComposition = SceneCompositionBuilder.BuildDrawingObjectChildren(
            nestedKeyframePreview,
            nestedKeyframeRoot,
            nestedKeyframeProject.DrawingObjects,
            5);
        AssertTimeline(
            nestedKeyframePreview.ObjectCount == 1
            && NearlyEqual(nestedKeyframePreview.X[0], 144)
            && NearlyEqual(nestedKeyframePreview.Y[0], 78)
            && nestedKeyframeComposition.ObjectOwners.Values.Any(owner =>
                owner.RootInstanceId == nestedKeyframeInstance!.Id),
            "Creating a keyframe removed the nested object or lost its keyed position in composition.");
        var heldInstanceState = nestedKeyframeInstance!.EvaluateState(5);
        var keyedInstanceState = heldInstanceState with
        {
            Z = 7,
            RotationX = 11,
            RotationY = 13,
            RotationZ = 90,
            SkewX = 7,
            SkewY = 4,
            ScaleX = 2,
            ScaleY = 1.5f,
            ScaleZ = 3,
            PlaybackFps = 12,
            PlaybackMode = DrawingObjectPlaybackMode.Loop,
            HoldFrame = 3
        };
        AssertTimeline(
            nestedKeyframeRoot.Scene.InsertTimelineKeyframe(0, 6)
            && nestedKeyframeInstance.SetStateAtFrame(6, keyedInstanceState)
            && nestedKeyframeInstance.EvaluateState(5) == heldInstanceState
            && nestedKeyframeInstance.EvaluateState(6) == keyedInstanceState,
            "Changing a nested instance transform shared rotation or other state with the previous keyframe.");
        SceneCompositionBuilder.BuildDrawingObjectChildren(
            nestedKeyframePreview,
            nestedKeyframeRoot,
            nestedKeyframeProject.DrawingObjects,
            6);
        var expectedKeyedCenter = System.Numerics.Vector2.Transform(
            new System.Numerics.Vector2(24, 18),
            System.Numerics.Matrix3x2.CreateScale(keyedInstanceState.ScaleX, keyedInstanceState.ScaleY)
            * System.Numerics.Matrix3x2.CreateSkew(
                keyedInstanceState.SkewX * MathF.PI / 180f,
                keyedInstanceState.SkewY * MathF.PI / 180f)
            * System.Numerics.Matrix3x2.CreateRotation(keyedInstanceState.RotationZ * MathF.PI / 180f)
            * System.Numerics.Matrix3x2.CreateTranslation(keyedInstanceState.X, keyedInstanceState.Y));
        var keyedBounds = nestedKeyframePreview.GetObjectWorldBounds(0);
        AssertTimeline(
            nestedKeyframePreview.ObjectCount == 1
            && Math.Abs(keyedBounds.Left + keyedBounds.Width * 0.5f - expectedKeyedCenter.X) <= 0.51f
            && Math.Abs(keyedBounds.Top + keyedBounds.Height * 0.5f - expectedKeyedCenter.Y) <= 0.51f,
            $"Nested-instance composition ignored the transform state stored at its current keyframe: " +
            $"expected={expectedKeyedCenter.X:0.###},{expectedKeyedCenter.Y:0.###}, " +
            $"actual={keyedBounds.Left + keyedBounds.Width * 0.5f:0.###},{keyedBounds.Top + keyedBounds.Height * 0.5f:0.###}, " +
            $"shape={nestedKeyframePreview.ShapeKind[0]}.");
        AssertTimeline(
            nestedKeyframeProject.TryDuplicateDrawingObject(nestedKeyframeRoot.Id, out var nestedKeyframeDuplicate)
            && nestedKeyframeDuplicate is not null
            && PointsNear(nestedKeyframeDuplicate.Instances.Single().EvaluatePosition(5), new PointF(120, 60))
            && nestedKeyframeDuplicate.Instances.Single().EvaluateState(6) == keyedInstanceState,
            "Duplicating a drawing object discarded its nested instance keyframe state.");
        AssertTimeline(
            nestedKeyframeRoot.Scene.InsertTimelineBlankKeyframe(0, 8)
            && nestedKeyframeTrack.EvaluateExposure(8).HasContent == false,
            "An explicit blank keyframe was repopulated by nested layer content.");
        SceneCompositionBuilder.BuildDrawingObjectChildren(
            nestedKeyframePreview,
            nestedKeyframeRoot,
            nestedKeyframeProject.DrawingObjects,
            8);
        AssertTimeline(
            nestedKeyframePreview.ObjectCount == 0,
            "An explicit blank keyframe did not hide its nested drawing objects.");
        var nestedKeyframeProjectChanges = 0;
        nestedKeyframeProject.Changed += (_, _) => nestedKeyframeProjectChanges++;
        AssertTimeline(
            nestedKeyframeProject.TryRemoveDrawingObjectInstance(
                nestedKeyframeRoot.Id,
                nestedKeyframeInstance!.Id,
                out var removedNestedKeyframeInstance)
            && ReferenceEquals(removedNestedKeyframeInstance, nestedKeyframeInstance)
            && nestedKeyframeRoot.Instances.Count == 0
            && nestedKeyframeTrack.EvaluateExposure(0).HasContent == false
            && nestedKeyframeTrack.EvaluateExposure(5).HasContent == false
            && !nestedKeyframeProject.TryRemoveDrawingObjectInstance(
                nestedKeyframeRoot.Id,
                nestedKeyframeInstance.Id,
                out _)
            && nestedKeyframeProjectChanges == 1,
            "Deleting a nested instance did not update its owner and host-layer exposure exactly once.");
        SceneCompositionBuilder.BuildDrawingObjectChildren(
            nestedKeyframePreview,
            nestedKeyframeRoot,
            nestedKeyframeProject.DrawingObjects,
            5);
        AssertTimeline(
            nestedKeyframePreview.ObjectCount == 0,
            "A deleted nested instance remained in drawing-object composition.");

        first.Scene.Generate(2, 16, 128);
        first.SynchronizeTimelineTracks();
        AssertTimeline(
            first.Instances.Count == validInstanceCount
            && first.Timeline.Tracks.Select(track => track.TargetId).SequenceEqual(first.Scene.LayerIds)
            && first.Instances.All(instance => first.Scene.LayerIds.Contains(instance.SceneLayerId, StringComparer.Ordinal)),
            "Regenerating drawing geometry did not migrate nested instances onto valid drawing layers.");
    }

    private static void RunProjectAssetFolderRegression()
    {
        var project = new VectorProject();
        var rootObject = project.DrawingObjects[0];
        rootObject.Name = "Root Asset";
        rootObject.Scene.AddObject(0, PointF.Empty, new SizeF(80, 60), 0, 0, Color.Coral, 6, ShapeKind.Rectangle);
        var childObject = project.AddDrawingObject("Child Asset");
        childObject.Scene.AddObject(0, PointF.Empty, new SizeF(40, 30), 0, 0, Color.Teal, 4, ShapeKind.Ellipse);
        ProjectAssetFolder? rootFolder = null;
        ProjectAssetFolder? childFolder = null;
        AssertTimeline(
            project.TryAddAssetFolder("Characters", "", out rootFolder)
            && rootFolder is not null
            && project.TryAddAssetFolder("Heads", rootFolder.Id, out childFolder)
            && childFolder is not null
            && project.TryMoveDrawingObjectToAssetFolder(rootObject.Id, rootFolder.Id)
            && project.TryMoveDrawingObjectToAssetFolder(childObject.Id, childFolder.Id)
            && project.TryAddDrawingObjectInstance(rootObject.Id, childObject.Id, PointF.Empty, out var nested)
            && nested is not null,
            "The project asset folder model could not create a nested source tree.");

        var changeCount = 0;
        project.Changed += (_, _) => changeCount++;
        AssertTimeline(
            project.TryDuplicateAssetFolder(rootFolder!.Id, out var duplicateRoot)
            && duplicateRoot is not null
            && changeCount == 1,
            "Duplicating a project asset folder did not complete as one project mutation.");
        var duplicateChild = project.AssetFolders.SingleOrDefault(folder =>
            string.Equals(folder.ParentFolderId, duplicateRoot!.Id, StringComparison.Ordinal));
        var duplicateRootObject = project.DrawingObjects.SingleOrDefault(item =>
            string.Equals(item.AssetFolderId, duplicateRoot!.Id, StringComparison.Ordinal));
        var duplicateChildObject = duplicateChild is null
            ? null
            : project.DrawingObjects.SingleOrDefault(item =>
                string.Equals(item.AssetFolderId, duplicateChild.Id, StringComparison.Ordinal));
        AssertTimeline(
            duplicateRoot!.Name == "Characters Copy"
            && duplicateChild is { Name: "Heads" }
            && duplicateRootObject is not null
            && duplicateChildObject is not null
            && duplicateRootObject.Id != rootObject.Id
            && duplicateChildObject.Id != childObject.Id
            && duplicateRootObject.Scene.ObjectCount == rootObject.Scene.ObjectCount
            && duplicateChildObject.Scene.ObjectCount == childObject.Scene.ObjectCount
            && duplicateRootObject.Instances.Count == 1
            && duplicateRootObject.Instances[0].DrawingObjectId == duplicateChildObject.Id
            && rootObject.Instances[0].DrawingObjectId == childObject.Id,
            "Folder duplication did not preserve its subtree or remap internal drawing-object references.");
        AssertTimeline(
            !project.TryMoveAssetFolder(rootFolder.Id, childFolder!.Id)
            && project.TryRenameAssetFolder(duplicateRoot.Id, "Characters Variant")
            && duplicateRoot.Name == "Characters Variant"
            && project.TryMoveAssetFolder(childFolder.Id, "")
            && string.IsNullOrEmpty(childFolder.ParentFolderId)
            && project.TryMoveDrawingObjectToAssetFolder(rootObject.Id, "")
            && string.IsNullOrEmpty(rootObject.AssetFolderId)
            && project.AssetFolders is not ICollection<ProjectAssetFolder> { IsReadOnly: false },
            "Project asset folder rename, root move, cycle rejection, or read-only ownership failed.");
    }

    private static void RunLayeredInstanceIndexRegression()
    {
        const int layerCount = 16;
        const int instancesPerLayer = 16;
        const int queryIterations = 256;
        const double lookupBudgetMilliseconds = 25;

        var project = VectorProject.CreateEmpty();
        var scene = project.Scenes[0];
        var container = project.DrawingObjects[0];
        var child = project.AddDrawingObject("Indexed child");
        for (var layer = scene.Layers.Count; layer < layerCount; layer++)
        {
            AssertTimeline(
                project.TryAddSceneLayer(scene.Id, out _),
                "Layered instance index regression could not add a scene layer.");
        }
        container.Scene.CreateEmpty(layerCount);
        container.SynchronizeTimelineTracks();

        for (var index = 0; index < layerCount * instancesPerLayer; index++)
        {
            var sceneLayerId = scene.Layers[index % layerCount].Id;
            var drawingLayerId = container.Scene.LayerIds[index % layerCount];
            AssertTimeline(
                project.TryAddSceneInstance(
                    scene.Id,
                    child.Id,
                    new PointF(index, -index),
                    index,
                    sceneLayerId,
                    out _)
                && project.TryAddDrawingObjectInstance(
                    container.Id,
                    child.Id,
                    new PointF(-index, index),
                    drawingLayerId,
                    out _),
                "Layered instance index regression could not populate its scene and drawing layers.");
        }

        var sceneLayerIds = scene.Layers.Select(layer => layer.Id).ToArray();
        var drawingLayerIds = container.Scene.LayerIds.ToArray();
        foreach (var layerId in sceneLayerIds) _ = scene.InstancesInLayer(layerId).Count;
        foreach (var layerId in drawingLayerIds) _ = container.InstancesInLayer(layerId).Count;
        var cachedSceneLayer = scene.InstancesInLayer(sceneLayerIds[0]);
        var cachedDrawingLayer = container.InstancesInLayer(drawingLayerIds[0]);

        var queryWatch = new Stopwatch();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        queryWatch.Start();
        var queriedInstances = 0;
        for (var iteration = 0; iteration < queryIterations; iteration++)
        {
            foreach (var layerId in sceneLayerIds) queriedInstances += scene.InstancesInLayer(layerId).Count;
            foreach (var layerId in drawingLayerIds) queriedInstances += container.InstancesInLayer(layerId).Count;
        }
        queryWatch.Stop();
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        var lookupBudgetMet = queryWatch.Elapsed.TotalMilliseconds <= lookupBudgetMilliseconds
            && allocatedBytes <= 4096;
        AssertTimeline(
            queriedInstances == queryIterations * layerCount * instancesPerLayer * 2
            && ReferenceEquals(cachedSceneLayer, scene.InstancesInLayer(sceneLayerIds[0]))
            && ReferenceEquals(cachedDrawingLayer, container.InstancesInLayer(drawingLayerIds[0]))
            && lookupBudgetMet,
            "Warm layered instance lookup changed membership, rebuilt cached views, or allocated per query: "
            + $"elapsed={queryWatch.Elapsed.TotalMilliseconds:0.000} ms, allocated={allocatedBytes} bytes.");

        var sceneSnapshot = scene.CreateInstanceSnapshot();
        var removedSceneInstance = cachedSceneLayer[0];
        AssertTimeline(
            scene.RemoveInstance(removedSceneInstance)
            && scene.InstancesInLayer(sceneLayerIds[0]).Count == instancesPerLayer - 1
            && cachedSceneLayer.Count == instancesPerLayer,
            "Removing a scene instance did not invalidate the layered index or preserve its prior read-only view.");
        scene.RestoreInstanceSnapshot(sceneSnapshot);
        AssertTimeline(
            scene.InstancesInLayer(sceneLayerIds[0]).Count == instancesPerLayer,
            "Restoring scene instances did not rebuild the layered index.");

        var drawingSnapshot = container.CreateInstanceSnapshot();
        var firstDrawingId = cachedDrawingLayer[0].Id;
        var secondDrawingId = cachedDrawingLayer[1].Id;
        AssertTimeline(
            project.TryMoveDrawingObjectInstancesInLayer(container.Id, [firstDrawingId], 1),
            "Layered instance index regression could not reorder nested instances.");
        var reorderedDrawingLayer = container.InstancesInLayer(drawingLayerIds[0]);
        AssertTimeline(
            !ReferenceEquals(cachedDrawingLayer, reorderedDrawingLayer)
            && reorderedDrawingLayer[0].Id == secondDrawingId
            && reorderedDrawingLayer[1].Id == firstDrawingId
            && cachedDrawingLayer[0].Id == firstDrawingId,
            "Reordering nested instances did not invalidate the layered index or preserve stack order.");
        AssertTimeline(
            project.TryRemoveDrawingObjectInstance(container.Id, secondDrawingId, out _)
            && container.InstancesInLayer(drawingLayerIds[0]).Count == instancesPerLayer - 1,
            "Removing a nested instance did not invalidate the layered index.");
        container.RestoreInstanceSnapshot(drawingSnapshot);
        AssertTimeline(
            container.InstancesInLayer(drawingLayerIds[0]).Count == instancesPerLayer
            && container.InstancesInLayer(drawingLayerIds[0])[0].Id == firstDrawingId,
            "Restoring nested instances did not recover layered membership and order.");

        Console.WriteLine($"layered_instance_lookup_ms={queryWatch.Elapsed.TotalMilliseconds:0.000}");
        Console.WriteLine($"layered_instance_lookup_allocated_bytes={allocatedBytes}");
        Console.WriteLine($"layered_instance_lookup_budget_ms={lookupBudgetMilliseconds:0.000}");
        Console.WriteLine($"layered_instance_lookup_budget_met={lookupBudgetMet.ToString().ToLowerInvariant()}");
    }

    private static void RunSceneCompositionRegression()
    {
        RunFillEdgeBezierCompositionRegression();
        var project = VectorProject.CreateEmpty();
        var drawingObject = project.DrawingObjects[0];
        drawingObject.Name = "Composition source";
        drawingObject.Scene.CreateEmpty();
        var sourceGradientObject = drawingObject.Scene.AddObject(
            0,
            new PointF(100, 50),
            new SizeF(80, 40),
            0,
            0,
            Color.Teal,
            12,
            ShapeKind.Rectangle);
        drawingObject.Scene.SetGradientPaint(
            sourceGradientObject,
            GradientKind.ShapeRadial,
            [
                new GradientStop(0, Color.Teal),
                new GradientStop(0.5f, Color.Gold),
                new GradientStop(1, Color.Coral)
            ],
            new PointF(60, 50),
            new PointF(140, 50));
        var sourceShapeGradientMapping = new[]
        {
            new[]
            {
                new PointF(60, 30),
                new PointF(140, 30),
                new PointF(140, 70),
                new PointF(60, 70)
            }
        };
        drawingObject.Scene.SetShapeGradientMapping(sourceGradientObject, sourceShapeGradientMapping);

        var childDrawingObject = project.AddDrawingObject("Nested source");
        childDrawingObject.Scene.CreateEmpty();
        childDrawingObject.Scene.AddObject(
            0,
            new PointF(10, 0),
            new SizeF(20, 20),
            0,
            0,
            Color.Coral,
            6,
            ShapeKind.Ellipse);
        var nestedHostLayer = drawingObject.Scene.AddLayer("Nested instances");
        AssertTimeline(
            project.TryAddDrawingObjectInstance(
                drawingObject.Id,
                childDrawingObject.Id,
                new PointF(20, 0),
                drawingObject.Scene.LayerIds[nestedHostLayer],
                out var nestedInstance)
            && nestedInstance is not null,
            "Validated nested drawing-object insertion was rejected during composition setup.");

        var scene = project.Scenes[0];
        scene.Name = "Composition scene";
        AssertTimeline(
            project.TryAddSceneInstance(scene.Id, drawingObject.Id, new PointF(200, 300), 0, out var instance)
            && instance is not null,
            "Validated scene-instance insertion was rejected during composition setup.");
        instance!.Name = "Transformed source";
        instance.RotationZ = 90;
        instance.ScaleX = 2;
        instance.ScaleY = 1;

        var destination = new VectorScene();
        var composition = SceneCompositionBuilder.Build(destination, scene, project.DrawingObjects, 0);
        var compositionHasShapeMapping = destination.TryGetShapeGradientMappingWorldContours(0, out var compositionShapeMapping);
        AssertTimeline(
            destination.ObjectCount == 2
            && destination.LayerNames[0].StartsWith(instance.Name, StringComparison.Ordinal)
            && NearlyEqual(destination.X[0], 150)
            && NearlyEqual(destination.Y[0], 500)
            && NearlyEqual(destination.Width[0], 160)
            && NearlyEqual(destination.Height[0], 40)
            && NearlyEqual(destination.Angle[0], MathF.PI / 2f)
            && destination.GetGradientKind(0) == GradientKind.ShapeRadial
            && destination.GradientStartArgb[0] == Color.Teal.ToArgb()
            && destination.GradientEndArgb[0] == Color.Coral.ToArgb()
            && destination.GetGradientStops(0).Length == 3
            && destination.GetGradientStops(0)[1].Argb == Color.Gold.ToArgb()
            && PointsNear(destination.GetGradientStart(0), new PointF(150, 420))
            && PointsNear(destination.GetGradientEnd(0), new PointF(150, 580))
            && compositionHasShapeMapping
            && compositionShapeMapping.Length == 1
            && PointsNear(compositionShapeMapping[0][0], new PointF(170, 420))
            && NearlyEqual(destination.X[1], 200)
            && NearlyEqual(destination.Y[1], 360)
            && destination.GeometryRevision <= 2
            && destination.TileCount.Sum() > 0
            && destination.OverviewCount.Sum() > 0,
            "Scene composition did not resolve and transform the drawing-object instance.");
        AssertTimeline(
            composition.TryGetOwner(0, out var rootOwner)
            && rootOwner.InstanceId == instance.Id
            && rootOwner.DrawingObjectId == drawingObject.Id
            && rootOwner.RootInstanceId == instance.Id
            && composition.TryGetOwner(1, out var nestedOwner)
            && nestedOwner.InstanceId == nestedInstance!.Id
            && nestedOwner.DrawingObjectId == childDrawingObject.Id
            && nestedOwner.RootInstanceId == instance.Id,
            "Scene composition did not preserve drawing-object provenance for editor navigation.");

        var sourceGradientStops = drawingObject.Scene.GetGradientStops(sourceGradientObject);
        var rootTint = Color.FromArgb(255, 128, 255, 128).ToArgb();
        var nestedTint = Color.FromArgb(255, 255, 128, 255).ToArgb();
        instance.Alpha = 0.5f;
        instance.TintArgb = rootTint;
        nestedInstance!.Alpha = 0.5f;
        nestedInstance.TintArgb = nestedTint;
        SceneCompositionBuilder.Build(destination, scene, project.DrawingObjects, 0);
        var tintedStops = destination.GetGradientStops(0);
        AssertTimeline(
            destination.ObjectCount == 2
            && destination.Argb[0] == ApplyInstanceAppearanceForRegression(Color.Teal.ToArgb(), 0.5f, rootTint)
            && destination.Argb[1] == ApplyInstanceAppearanceForRegression(
                Color.Coral.ToArgb(),
                0.25f,
                Color.FromArgb(255, 128, 128, 128).ToArgb())
            && tintedStops.Length == sourceGradientStops.Length
            && tintedStops.Select(stop => stop.Argb).SequenceEqual(sourceGradientStops.Select(stop =>
                ApplyInstanceAppearanceForRegression(stop.Argb, 0.5f, rootTint)))
            && drawingObject.Scene.Argb[sourceGradientObject] == Color.Teal.ToArgb()
            && drawingObject.Scene.GetGradientStops(sourceGradientObject).SequenceEqual(sourceGradientStops),
            "Nested instance Alpha or multiply tint did not compose independently across fills and gradient stops.");
        instance.Alpha = 1f;
        instance.TintArgb = Color.White.ToArgb();
        nestedInstance.Alpha = 1f;
        nestedInstance.TintArgb = Color.White.ToArgb();

        var childPreview = new VectorScene();
        var childPreviewResult = SceneCompositionBuilder.BuildDrawingObjectChildren(
            childPreview,
            drawingObject,
            project.DrawingObjects,
            0);
        AssertTimeline(
            childPreview.ObjectCount == 1
            && childPreviewResult.TryGetOwner(0, out var previewOwner)
            && previewOwner.DrawingObjectId == childDrawingObject.Id
            && MainForm.TryResolveCompositionInstance(
                childPreview,
                childPreviewResult,
                drawingObject.Instances,
                new PointF(30, 0),
                0,
                2,
                out var resolvedNested)
            && ReferenceEquals(resolvedNested, nestedInstance),
            "Drawing-object editing preview did not expose its nested child instance.");
        nestedInstance!.X += 40;
        childPreviewResult = SceneCompositionBuilder.BuildDrawingObjectChildren(
            childPreview,
            drawingObject,
            project.DrawingObjects,
            0);
        AssertTimeline(
            MainForm.TryResolveCompositionInstance(
                childPreview,
                childPreviewResult,
                drawingObject.Instances,
                new PointF(70, 0),
                0,
                2,
                out var movedNested)
            && ReferenceEquals(movedNested, nestedInstance),
            "Moving a nested drawing-object instance did not move its selectable composition geometry.");
        nestedInstance.X -= 40;

        var dragPreview = new VectorScene();
        var dragPreviewResult = SceneCompositionBuilder.BuildDrawingObjectPreview(
            dragPreview,
            drawingObject,
            project.DrawingObjects,
            new PointF(320, 180),
            0);
        AssertTimeline(
            dragPreview.ObjectCount == 2
            && NearlyEqual(dragPreview.X[0], 420)
            && NearlyEqual(dragPreview.Y[0], 230)
            && NearlyEqual(dragPreview.X[1], 350)
            && NearlyEqual(dragPreview.Y[1], 180)
            && Color.FromArgb(dragPreview.Argb[0]).A is > 0 and < 255
            && dragPreviewResult.TryGetOwner(1, out var dragPreviewOwner)
            && dragPreviewOwner.DrawingObjectId == childDrawingObject.Id,
            "Drag preview did not preserve nested drawing-object geometry, placement, or opacity.");

        var track = scene.Timeline.FindTrackByTargetId(instance.SceneLayerId)
            ?? throw new InvalidOperationException("Scene composition regression lost its scene-layer track.");
        scene.Timeline.InsertBlankKeyframe(track.Id, 5);
        SceneCompositionBuilder.Build(destination, scene, project.DrawingObjects, 5);
        var placeholderTrack = destination.Timeline.FindTrackByTargetId(destination.LayerIds[0]);
        AssertTimeline(
            destination.ObjectCount == 0
            && destination.LayerCount == 1
            && placeholderTrack is not null
            && !placeholderTrack.EvaluateExposure(0).HasContent,
            "Blank scene-layer exposure remained visible or populated the composition placeholder layer.");

        scene.Timeline.InsertKeyframe(track.Id, 8);
        SceneCompositionBuilder.Build(destination, scene, project.DrawingObjects, 8);
        AssertTimeline(destination.ObjectCount == 2, "Populated scene-layer exposure did not resume composition rendering.");

        scene.Timeline.SetTrackDuration(track.Id, 10);
        var nestedTrack = drawingObject.Timeline.FindTrackByTargetId(drawingObject.Scene.LayerIds[nestedHostLayer])
            ?? throw new InvalidOperationException("Nested composition regression lost its host-layer track.");
        drawingObject.Timeline.InsertBlankKeyframe(nestedTrack.Id, 9);
        var frameNineComposition = SceneCompositionBuilder.Build(destination, scene, project.DrawingObjects, 9);
        AssertTimeline(
            destination.ObjectCount == 1
            && frameNineComposition.ObjectOwners.Values.All(owner => owner.DrawingObjectId != childDrawingObject.Id),
            "Blanking a nested instance host layer did not hide only that child branch.");

        AssertTimeline(
            project.TryAddDrawingObjectInstance(
                drawingObject.Id,
                childDrawingObject.Id,
                new PointF(120, 0),
                drawingObject.Scene.LayerIds[nestedHostLayer],
                out var secondNested)
            && secondNested is not null,
            "A second nested drawing-object instance was rejected from the same host layer.");
        var multiInstancePreview = SceneCompositionBuilder.BuildDrawingObjectChildren(
            childPreview,
            drawingObject,
            project.DrawingObjects,
            0);
        AssertTimeline(
            childPreview.ObjectCount == 2
            && multiInstancePreview.ObjectOwners.Values
                .Select(owner => owner.RootInstanceId)
                .Distinct(StringComparer.Ordinal)
                .Count() == 2,
            "Multiple nested drawing objects in one drawing layer were not composed independently.");
        var marqueeInstances = MainForm.FindCompositionInstancesInsideBounds(
            childPreview,
            multiInstancePreview,
            drawingObject.Instances,
            new RectangleF(-10_000, -10_000, 20_000, 20_000));
        var partialMarqueeInstances = MainForm.FindCompositionInstancesInsideBounds(
            childPreview,
            multiInstancePreview,
            drawingObject.Instances,
            new RectangleF(120, 0, 1, 1));
        AssertTimeline(
            marqueeInstances.Select(instance => instance.Id).ToHashSet(StringComparer.Ordinal)
                .SetEquals([nestedInstance!.Id, secondNested!.Id])
            && partialMarqueeInstances.Count == 0,
            "Drawing-object marquee selection did not require and return fully enclosed nested instances.");

        RunDrawingObjectInstancePlaybackRegression();
        RunDrawingObjectAnchorRegression();
        RunSparseLayerCompositionRegression();
        RunLayerEffectCompositionRegression();
        RunDegeneratePathCompositionRegression();
        RunChunkedCompositionRegression();
        RunSkewedSceneCompositionRegression();
    }

    private static void RunDrawingObjectAnchorRegression()
    {
        var project = VectorProject.CreateEmpty();
        var source = project.DrawingObjects[0];
        source.Scene.CreateEmpty();
        source.Scene.AddObject(
            0,
            new PointF(140, 60),
            new SizeF(80, 40),
            0,
            0,
            Color.Teal,
            8,
            ShapeKind.Rectangle);

        var container = project.AddDrawingObject("Anchor container");
        container.Scene.CreateEmpty(1, 8);
        AssertTimeline(
            project.TryAddDrawingObjectInstance(container.Id, source.Id, new PointF(80, 40), out var nested)
            && nested is not null,
            "Anchor regression could not create its nested instance.");
        nested!.RotationZ = 22;
        nested.SkewX = 8;
        nested.ScaleX = 1.4f;
        nested.ScaleY = 0.7f;
        nested.SetStateAtFrame(6, nested.EvaluateState(0) with
        {
            X = 130,
            Y = 75,
            RotationZ = -18,
            SkewY = 11,
            ScaleX = 0.85f,
            ScaleY = 1.25f
        });

        var scene = project.Scenes[0];
        var sceneTrack = scene.Timeline.FindTrackByTargetId(scene.ActiveLayerId)
            ?? throw new InvalidOperationException("Anchor regression lost its scene track.");
        scene.Timeline.SetTrackDuration(sceneTrack.Id, 8);
        DrawingObjectInstanceDefinition? direct = null;
        DrawingObjectInstanceDefinition? containerInstance = null;
        AssertTimeline(
            project.TryAddSceneInstance(scene.Id, source.Id, new PointF(420, 180), 0, out direct)
            && direct is not null
            && project.TryAddSceneInstance(scene.Id, container.Id, new PointF(120, 240), 1, out containerInstance)
            && containerInstance is not null,
            "Anchor regression could not create its direct and container instances.");
        direct!.RotationZ = 35;
        direct.SkewX = 12;
        direct.ScaleX = 1.6f;
        direct.ScaleY = 0.75f;
        direct.SetStateAtFrame(6, direct.EvaluateState(0) with
        {
            X = 500,
            Y = 220,
            RotationZ = -20,
            SkewY = 9,
            ScaleX = 0.8f,
            ScaleY = 1.3f
        });
        containerInstance!.RotationZ = -12;
        containerInstance.ScaleX = 1.15f;
        containerInstance.ScaleY = 0.9f;

        var directBaseBefore = direct.EvaluateState(0);
        var directKeyBefore = direct.EvaluateState(6);
        var nestedBaseBefore = nested.EvaluateState(0);
        var nestedKeyBefore = nested.EvaluateState(6);
        var beforeFrameZero = CapturePositions(0);
        var beforeFrameSix = CapturePositions(6);
        var anchor = new PointF(35, -20);
        AssertTimeline(
            project.TrySetDrawingObjectAnchor(source.Id, anchor),
            "A valid drawing-object anchor change was rejected.");
        var afterFrameZero = CapturePositions(0);
        var afterFrameSix = CapturePositions(6);

        AssertTimeline(
            source.Anchor == anchor
            && beforeFrameZero.Count == 2
            && beforeFrameSix.Count == 2
            && beforeFrameZero.All(item => afterFrameZero.TryGetValue(item.Key, out var position)
                && PointsNear(position, item.Value))
            && beforeFrameSix.All(item => afterFrameSix.TryGetValue(item.Key, out var position)
                && PointsNear(position, item.Value))
            && direct.EvaluateState(0).Position != directBaseBefore.Position
            && direct.EvaluateState(6).Position != directKeyBefore.Position
            && nested.EvaluateState(0).Position != nestedBaseBefore.Position
            && nested.EvaluateState(6).Position != nestedKeyBefore.Position
            && NearlyEqual(direct.EvaluateState(0).RotationZ, directBaseBefore.RotationZ)
            && NearlyEqual(direct.EvaluateState(6).SkewY, directKeyBefore.SkewY)
            && NearlyEqual(nested.EvaluateState(0).ScaleX, nestedBaseBefore.ScaleX)
            && NearlyEqual(nested.EvaluateState(6).ScaleY, nestedKeyBefore.ScaleY),
            "Changing a shared drawing-object anchor moved rendered instances or lost keyframed transform state.");

        AssertTimeline(
            project.TryDuplicateDrawingObject(source.Id, out var duplicate)
            && duplicate is not null
            && duplicate.Anchor == anchor
            && !project.TrySetDrawingObjectAnchor(source.Id, new PointF(float.NaN, 0))
            && source.Anchor == anchor,
            "Drawing-object duplication or invalid-anchor rejection did not preserve the shared anchor.");

        Dictionary<string, PointF> CapturePositions(int frame)
        {
            var destination = new VectorScene();
            var composition = SceneCompositionBuilder.Build(destination, scene, project.DrawingObjects, frame);
            var positions = new Dictionary<string, PointF>(StringComparer.Ordinal);
            for (var index = 0; index < destination.ObjectCount; index++)
            {
                if (!composition.TryGetOwner(index, out var owner)
                    || owner.DrawingObjectId != source.Id)
                {
                    continue;
                }

                positions[owner.RootInstanceId] = new PointF(destination.X[index], destination.Y[index]);
            }
            return positions;
        }
    }

    private static void RunEditorRestartSnapshotRegression()
    {
        var project = VectorProject.CreateEmpty();
        project.Name = "Restart Snapshot";
        AssertTimeline(
            project.TrySetPlaybackSettings(30m, loopPlayback: true, playbackStartFrame: 100, playbackEndFrame: 200)
            && project.TrySetPlaybackFps(29.97m)
            && project.TrySetLoopPlayback(false)
            && project.PlaybackStartFrame == 100
            && project.PlaybackEndFrame == 200
            && project.TrySetPlaybackRange(2, 18),
            "Project playback setting updates overwrote an unrelated saved setting.");
        var root = project.DrawingObjects[0];
        root.Scene.CreateEmpty();
        var rootLine = root.Scene.AddLineSegment(
            0,
            new PointF(-120, 0),
            new PointF(120, 0),
            VectorUnits.StrokePointsToUnits(2),
            Color.Transparent,
            Color.Coral,
            6,
            LineEndpointStyle.Sharp,
            LineEndpointStyle.Round);
        var child = project.AddDrawingObject("Restart Child");
        child.Scene.AddObject(0, new PointF(24, 12), new SizeF(48, 36), 0, 0, Color.Teal, 6, ShapeKind.Rectangle);
        if (!project.TryAddAssetFolder("Restart Assets", "", out var restartFolder)
            || restartFolder is null
            || !project.TryAddAssetFolder("Restart Children", restartFolder.Id, out var restartChildFolder)
            || restartChildFolder is null
            || !project.TryMoveDrawingObjectToAssetFolder(root.Id, restartFolder.Id)
            || !project.TryMoveDrawingObjectToAssetFolder(child.Id, restartChildFolder.Id))
        {
            throw new InvalidOperationException("Editor restart snapshot regression could not create project asset folders.");
        }
        if (!project.TrySetDrawingObjectAnchor(root.Id, new PointF(18, -12))
            || !project.TrySetDrawingObjectAnchor(child.Id, new PointF(-9, 7)))
        {
            throw new InvalidOperationException("Editor restart snapshot regression could not set drawing-object anchors.");
        }
        if (!project.TryAddDrawingObjectInstance(root.Id, child.Id, new PointF(40, 20), out var nested)
            || nested is null
            || !project.TryAddSceneInstance(project.Scenes[0].Id, root.Id, new PointF(320, 180), 2, out var sceneInstance)
            || sceneInstance is null)
        {
            throw new InvalidOperationException("Editor restart snapshot regression could not create its source project graph.");
        }
        var nestedRestartState = nested.EvaluateState(5) with
        {
            X = 140,
            Y = 70,
            RotationZ = 35,
            ScaleX = 1.75f,
            Alpha = 0.65f,
            TintArgb = Color.FromArgb(255, 180, 220, 96).ToArgb(),
            PlaybackFps = 18,
            PlaybackMode = DrawingObjectPlaybackMode.Loop,
            HoldFrame = 4
        };
        var sceneRestartState = sceneInstance.EvaluateState(6) with
        {
            X = 420,
            Y = 240,
            SkewY = 16,
            ScaleY = 0.8f,
            Alpha = 0.4f,
            TintArgb = Color.FromArgb(255, 96, 160, 240).ToArgb(),
            PlaybackFps = 24,
            PlaybackMode = DrawingObjectPlaybackMode.HoldFrame,
            HoldFrame = 6
        };
        nested.SetStateAtFrame(5, nestedRestartState);
        sceneInstance.SetStateAtFrame(6, sceneRestartState);

        var restored = VectorProject.RestoreRestartSnapshot(
            EditorRestartStore.RoundTripProjectSnapshot(project.CreateRestartSnapshot()));
        var restoredRoot = restored.DrawingObjects.SingleOrDefault(item => item.Id == root.Id);
        var restoredChild = restored.DrawingObjects.SingleOrDefault(item => item.Id == child.Id);
        var restoredScene = restored.Scenes.SingleOrDefault(item => item.Id == project.Scenes[0].Id);
        if (restored.Name != project.Name
            || restored.Id != project.Id
            || restored.PlaybackFps != 29.97m
            || restored.LoopPlayback
            || restored.PlaybackStartFrame != 2
            || restored.PlaybackEndFrame != 18
            || restored.DrawingObjects.Count != 2
            || restored.AssetFolders.Count != 2
            || restored.Scenes.Count != 1
            || restoredRoot is null
            || restoredChild is null
            || restoredRoot.Anchor != root.Anchor
            || restoredChild.Anchor != child.Anchor
            || restoredRoot.AssetFolderId != restartFolder.Id
            || restoredChild.AssetFolderId != restartChildFolder.Id
            || restored.AssetFolders.Single(folder => folder.Id == restartChildFolder.Id).ParentFolderId != restartFolder.Id
            || restoredRoot.Instances.Count != 1
            || restoredRoot.Instances[0].Id != nested.Id
            || restoredRoot.Instances[0].DrawingObjectId != child.Id
            || restoredRoot.Instances[0].EvaluateState(5) != nestedRestartState
            || restoredRoot.Scene.ObjectCount != 1
            || restoredRoot.Scene.GetLineEndpointStyle(rootLine, startEndpoint: true) != LineEndpointStyle.Sharp
            || restoredRoot.Scene.GetLineEndpointStyle(rootLine, startEndpoint: false) != LineEndpointStyle.Round
            || restoredScene is null
            || restoredScene.Instances.Count != 1
            || restoredScene.Instances[0].Id != sceneInstance.Id
            || restoredScene.Instances[0].DrawingObjectId != root.Id
            || restoredScene.Instances[0].EvaluateState(6) != sceneRestartState
            || !restoredScene.Timeline.EvaluateTargetExposure(restoredScene.ActiveLayerId, 0).HasContent)
        {
            throw new InvalidOperationException("Editor restart snapshot did not preserve project geometry, instance ownership, or timeline state.");
        }

        var editorState = new EditorRestartState
        {
            Project = project,
            ProjectManifestPath = @"C:\Projects\Restart Snapshot.v2dProject",
            ProjectDirty = true,
            ActiveSceneIndex = 0,
            ActiveDrawingObjectIndex = 0,
            Workspace = WorkspaceView.BasicDrawing,
            Frame = 5,
            SelectedObjects = [rootLine],
            StageView = new StageViewState(
                12,
                -8,
                1.25f,
                0.2f,
                -0.1f,
                2400,
                0.9f,
                3,
                4,
                5,
                0.2f,
                WorldGridType.Cartesian),
            WindowBounds = new Rectangle(40, 60, 1280, 800),
            WindowState = FormWindowState.Normal
        };
        EditorRestartStore.DeletePending();
        try
        {
            var pendingPaths = EditorRestartStore.GetPendingPathsForRegression();
            AssertTimeline(
                EditorRestartStore.TrySave(editorState)
                && File.Exists(pendingPaths.StatePath)
                && File.Exists(pendingPaths.TokenSidecarPath)
                && !EditorRestartStore.TryConsume(requestedToken: null, out _)
                && !File.Exists(pendingPaths.StatePath)
                && !File.Exists(pendingPaths.TokenSidecarPath),
                "A normal startup did not reject and remove an unrequested editor restart state.");

            AssertTimeline(
                EditorRestartStore.TrySave(editorState)
                && EditorRestartStore.TryGetPendingTokenForRegression(out var rejectedToken)
                && !EditorRestartStore.TryConsume(Guid.NewGuid().ToString("N"), out _)
                && !File.Exists(pendingPaths.StatePath)
                && !File.Exists(pendingPaths.TokenSidecarPath)
                && rejectedToken.Length == 32,
                "An incorrect editor restart token did not reject and remove the pending state.");

            AssertTimeline(
                EditorRestartStore.TrySave(editorState)
                && EditorRestartStore.TryGetPendingTokenForRegression(out var restartToken)
                && EditorRestartStore.GetRequestedToken(
                    ["--unrelated", EditorRestartStore.TokenArgumentPrefix + restartToken]) == restartToken
                && EditorRestartStore.TryConsume(restartToken, out var consumedState)
                && consumedState is not null
                && consumedState.Project.Id == project.Id
                && consumedState.Project.PlaybackFps == project.PlaybackFps
                && consumedState.ProjectManifestPath == editorState.ProjectManifestPath
                && consumedState.ProjectDirty
                && consumedState.Frame == editorState.Frame
                && !File.Exists(pendingPaths.StatePath)
                && !File.Exists(pendingPaths.TokenSidecarPath)
                && !EditorRestartStore.TryConsume(restartToken, out _),
                "The editor restart token did not authorize exactly one state restoration.");
        }
        finally
        {
            EditorRestartStore.DeletePending();
        }

        Console.WriteLine("editor_restart_snapshot_regression=ok");
    }

    private static void RunProjectVaultPersistenceRegression()
    {
        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "Vector2DAnimationEngine",
            $"project-vault-regression-{Guid.NewGuid():N}");
        var manifestPath = Path.Combine(temporaryRoot, "Library.v2dProject");
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            var project = VectorProject.CreateEmpty();
            project.Name = "Library & 资产";
            var playbackChangeCount = 0;
            project.Changed += (_, _) => playbackChangeCount++;
            AssertTimeline(
                project.TrySetPlaybackSettings(23.976m, loopPlayback: false, playbackStartFrame: 3, playbackEndFrame: 9)
                && project.PlaybackFps == 23.976m
                && !project.LoopPlayback
                && project.PlaybackStartFrame == 3
                && project.PlaybackEndFrame == 9
                && playbackChangeCount == 1
                && !project.TrySetPlaybackSettings(23.976m, loopPlayback: false, playbackStartFrame: 3, playbackEndFrame: 9)
                && playbackChangeCount == 1,
                "Project playback settings did not normalize or raise one change notification.");
            var root = project.DrawingObjects[0];
            root.Scene.CreateEmpty(2, 12);
            root.Scene.ActiveLayer = 0;
            var nestedLayerId = root.Scene.LayerIds[1];
            var line = root.Scene.AddLineSegment(
                0,
                new PointF(-120, -30),
                new PointF(180, 90),
                VectorUnits.StrokePointsToUnits(2.5f),
                Color.Transparent,
                Color.FromArgb(210, 24, 170, 220),
                18,
                LineEndpointStyle.Round,
                LineEndpointStyle.Sharp);
            var path = root.Scene.AddPathObject(
                1,
                [new PointF(-40, -30), new PointF(70, -20), new PointF(55, 80), new PointF(-65, 60)],
                0,
                Color.Teal,
                Color.Coral,
                12);
            root.Scene.SetLinearGradient(
                path,
                Color.FromArgb(180, Color.Gold),
                Color.FromArgb(220, Color.RoyalBlue),
                new PointF(-65, -30),
                new PointF(70, 80));
            root.Scene.SetGradientStops(path, [
                new GradientStop(0, Color.FromArgb(180, Color.Gold)),
                new GradientStop(0.45f, Color.FromArgb(210, Color.Coral)),
                new GradientStop(1, Color.FromArgb(220, Color.RoyalBlue))]);
            var pathBezierPrepared = root.Scene.TryConvertFillToBezierPath(path)
                && root.Scene.TryGetPathBezierSegment(path, 0, out var pathSegment)
                && root.Scene.SetPathBezierSegment(
                    path,
                    pathSegment.PartIndex,
                    pathSegment.Start,
                    new PointF(pathSegment.Control1.X, pathSegment.Control1.Y - 70),
                    new PointF(pathSegment.Control2.X, pathSegment.Control2.Y - 110),
                    pathSegment.End);
            var hasSavedPathBezier = root.Scene.TryGetPathBezierSegment(path, 0, out var savedPathBezier);
            AssertTimeline(
                pathBezierPrepared && hasSavedPathBezier,
                "Project Vault regression could not prepare exact fill-edge Bezier geometry.");
            const string importedSvgSource = """
                <svg xmlns="http://www.w3.org/2000/svg" width="120" height="80" viewBox="0 0 120 80">
                  <defs><linearGradient id="g"><stop stop-color="#ff3355"/><stop offset="1" stop-color="#2277ee" stop-opacity=".55"/></linearGradient></defs>
                  <rect width="120" height="80" rx="12" fill="url(#g)"/>
                </svg>
                """;
            var importedSvg = root.Scene.AddImportedSvgObject(
                1,
                new PointF(160, 40),
                new SizeF(3000, 2000),
                0.25f,
                importedSvgSource);
            var text = root.Scene.AddTextObject(
                1,
                new PointF(-320, 180),
                new TextObjectData(
                    "Vault \u6587\u672c\nEditable",
                    TextGeometry.FallbackFontFamilyName,
                    28,
                    TextFontStyle.BoldItalic,
                    TextHorizontalAlignment.Right,
                    new SizeF(4_000, 2_000)),
                Color.FromArgb(220, Color.MediumPurple));
            AssertTimeline(
                root.Scene.InsertTimelineBlankKeyframe(0, 4),
                "Project Vault regression could not create a blank drawing cel.");

            var child = project.AddDrawingObject("Nested <Child>");
            child.Scene.AddObject(0, new PointF(24, 18), new SizeF(90, 60), 0.2f, 0, Color.LimeGreen, 8, ShapeKind.Ellipse);
            DrawingObjectInstanceDefinition? nested = null;
            DrawingObjectInstanceDefinition? sceneInstance = null;
            AssertTimeline(
                project.TryAddAssetFolder("Characters", "", out var folder)
                && folder is not null
                && project.TryMoveDrawingObjectToAssetFolder(child.Id, folder.Id)
                && project.TryAddDrawingObjectInstance(
                    root.Id,
                    child.Id,
                    new PointF(48, 36),
                    nestedLayerId,
                    out nested)
                && nested is not null
                && project.TryAddSceneInstance(project.Scenes[0].Id, root.Id, new PointF(320, 180), 2, out sceneInstance)
                && sceneInstance is not null,
                "Project Vault regression could not create its asset graph.");
            nested!.Alpha = 0.75f;
            nested.TintArgb = Color.FromArgb(255, 220, 180, 120).ToArgb();
            sceneInstance!.Alpha = 0.6f;
            sceneInstance.TintArgb = Color.FromArgb(255, 120, 200, 240).ToArgb();
            nested!.SetStateAtFrame(6, nested.EvaluateState(6) with
            {
                X = 140,
                RotationZ = 32,
                Alpha = 0.35f,
                TintArgb = Color.FromArgb(255, 96, 220, 160).ToArgb(),
                PlaybackFps = 23.976m,
                PlaybackMode = DrawingObjectPlaybackMode.Loop
            });
            sceneInstance!.SetStateAtFrame(7, sceneInstance.EvaluateState(7) with
            {
                Y = 260,
                ScaleX = 1.4f,
                Alpha = 0.45f,
                TintArgb = Color.FromArgb(255, 180, 96, 240).ToArgb(),
                PlaybackFps = 24m,
                PlaybackMode = DrawingObjectPlaybackMode.HoldFrame,
                HoldFrame = 3
            });

            var drawingTrackId = root.Scene.Timeline.Tracks[0].Id;
            var savedManifest = ProjectVaultStore.Save(project, manifestPath);
            var svgPath = Path.Combine(temporaryRoot, ".Vault", $"{root.Id}.svg");
            var drawingTimelinePath = Path.Combine(temporaryRoot, ".TimeLine", "Drawings", $"{root.Id}.json");
            var sceneTimelinePath = Path.Combine(temporaryRoot, ".TimeLine", "Scenes", $"{project.Scenes[0].Id}.json");
            var svg = File.ReadAllText(svgPath);
            AssertTimeline(
                savedManifest == Path.GetFullPath(manifestPath)
                && File.Exists(manifestPath)
                && File.Exists(svgPath)
                && File.Exists(drawingTimelinePath)
                && File.Exists(sceneTimelinePath)
                && svg.Contains("http://www.w3.org/2000/svg", StringComparison.Ordinal)
                && svg.Contains("v2d-metadata", StringComparison.Ordinal)
                && svg.Contains("<path", StringComparison.Ordinal)
                && svg.Contains("<image", StringComparison.Ordinal)
                && svg.Contains("data:image/svg+xml;base64,", StringComparison.Ordinal),
                "Project Vault did not write the manifest, SVG assets, timeline library, or standard SVG preview.");

            var restored = ProjectVaultStore.Load(manifestPath);
            var restoredRoot = restored.DrawingObjects.Single(item => item.Id == root.Id);
            var restoredChild = restored.DrawingObjects.Single(item => item.Id == child.Id);
            var restoredScene = restored.Scenes.Single(item => item.Id == project.Scenes[0].Id);
            var restoredRootSnapshot = restoredRoot.Scene.CreateSnapshot();
            AssertTimeline(
                restored.Id == project.Id
                && restored.Name == project.Name
                && restored.PlaybackFps == 23.976m
                && !restored.LoopPlayback
                && restored.PlaybackStartFrame == 3
                && restored.PlaybackEndFrame == 9
                && restoredRoot.Scene.ObjectCount == root.Scene.ObjectCount
                && restoredRoot.Scene.ShapeKind[line] == ShapeKind.Line
                && restoredRoot.Scene.GetLineEndpointStyle(line, startEndpoint: true) == LineEndpointStyle.Round
                && restoredRoot.Scene.GetLineEndpointStyle(line, startEndpoint: false) == LineEndpointStyle.Sharp
                && restoredRoot.Scene.ShapeKind[importedSvg] == ShapeKind.ImportedSvg
                && restoredRoot.Scene.TryGetImportedSvgSource(importedSvg, out var restoredImportedSvgSource)
                && restoredImportedSvgSource == importedSvgSource
                && restoredRoot.Scene.ShapeKind[text] == ShapeKind.Text
                && restoredRoot.Scene.TryGetTextObjectData(text, out var restoredText)
                && restoredText.Content == "Vault \u6587\u672c\nEditable"
                && restoredText.FontStyle == TextFontStyle.BoldItalic
                && restoredText.Alignment == TextHorizontalAlignment.Right
                && restoredRootSnapshot.GradientStops.TryGetValue(path, out var restoredStops)
                && restoredStops.Length == 3
                && restoredRoot.Scene.TryGetPathBezierSegment(path, 0, out var restoredPathBezier)
                && PointsNear(restoredPathBezier.Control1, savedPathBezier.Control1)
                && PointsNear(restoredPathBezier.Control2, savedPathBezier.Control2)
                && restoredRoot.Scene.Timeline.Tracks[0].Id == drawingTrackId
                && restoredRoot.Scene.Timeline.Tracks[0].Keyframes.Any(key =>
                    key.Frame == 4 && key.Kind == TimelineKeyframeKind.Blank)
                && restoredRoot.Instances.Single().SceneLayerId == nestedLayerId
                && NearlyEqual(restoredRoot.Instances.Single().EvaluateState(0).Alpha, 0.75f)
                && restoredRoot.Instances.Single().EvaluateState(0).TintArgb == Color.FromArgb(255, 220, 180, 120).ToArgb()
                && NearlyEqual(restoredRoot.Instances.Single().EvaluateState(6).Alpha, 0.35f)
                && restoredRoot.Instances.Single().EvaluateState(6).TintArgb == Color.FromArgb(255, 96, 220, 160).ToArgb()
                && restoredRoot.Instances.Single().EvaluateState(6).PlaybackFps == 23.976m
                && restoredChild.AssetFolderId == folder!.Id
                && NearlyEqual(restoredScene.Instances.Single().EvaluateState(0).Alpha, 0.6f)
                && restoredScene.Instances.Single().EvaluateState(0).TintArgb == Color.FromArgb(255, 120, 200, 240).ToArgb()
                && NearlyEqual(restoredScene.Instances.Single().EvaluateState(7).Alpha, 0.45f)
                && restoredScene.Instances.Single().EvaluateState(7).TintArgb == Color.FromArgb(255, 180, 96, 240).ToArgb()
                && restoredScene.Instances.Single().EvaluateState(7).HoldFrame == 3,
                "Project Vault load did not restore project identity, SVG geometry, layers, frames, or animated instances.");

            var unrelatedPath = Path.Combine(temporaryRoot, "notes.txt");
            File.WriteAllText(unrelatedPath, "preserve me");
            var stalePath = Path.Combine(temporaryRoot, ".Vault", "stale.svg");
            File.WriteAllText(stalePath, "stale");
            ProjectVaultStore.Save(project, manifestPath);
            AssertTimeline(
                File.Exists(unrelatedPath) && !File.Exists(stalePath),
                "Project Vault overwrite removed an unrelated root file or retained a stale managed asset.");

            File.AppendAllText(svgPath, "<!-- tampered -->");
            var checksumRejected = false;
            try
            {
                _ = ProjectVaultStore.Load(manifestPath);
            }
            catch (InvalidDataException)
            {
                checksumRejected = true;
            }
            AssertTimeline(checksumRejected, "Project Vault accepted a drawing SVG whose checksum no longer matched the manifest.");

            ProjectVaultStore.Save(project, manifestPath);
            var secondManifestRejected = false;
            try
            {
                _ = ProjectVaultStore.Save(project, Path.Combine(temporaryRoot, "Second.v2dProject"));
            }
            catch (InvalidOperationException)
            {
                secondManifestRejected = true;
            }
            AssertTimeline(secondManifestRejected, "One asset-library folder accepted two project manifests that share managed directories.");

            var unmanagedRoot = Path.Combine(temporaryRoot, "unmanaged-target");
            var unmanagedVault = Path.Combine(unmanagedRoot, ".Vault");
            var unmanagedTimeline = Path.Combine(unmanagedRoot, ".TimeLine");
            Directory.CreateDirectory(unmanagedVault);
            Directory.CreateDirectory(unmanagedTimeline);
            var unmanagedVaultSentinel = Path.Combine(unmanagedVault, "keep.bin");
            var unmanagedTimelineSentinel = Path.Combine(unmanagedTimeline, "keep.json");
            File.WriteAllBytes(unmanagedVaultSentinel, [1, 2, 3, 4]);
            File.WriteAllText(unmanagedTimelineSentinel, "preserve me");
            var unmanagedTargetRejected = false;
            try
            {
                _ = ProjectVaultStore.Save(project, Path.Combine(unmanagedRoot, "Library.v2dProject"));
            }
            catch (InvalidOperationException)
            {
                unmanagedTargetRejected = true;
            }
            AssertTimeline(
                unmanagedTargetRejected
                && File.ReadAllBytes(unmanagedVaultSentinel).SequenceEqual(new byte[] { 1, 2, 3, 4 })
                && File.ReadAllText(unmanagedTimelineSentinel) == "preserve me",
                "Project Vault replaced unmanaged .Vault or .TimeLine data during a first save.");

            var foreignRoot = Path.Combine(temporaryRoot, "foreign-project");
            var foreignManifest = Path.Combine(foreignRoot, "Library.v2dProject");
            var foreignProject = VectorProject.CreateEmpty();
            ProjectVaultStore.Save(foreignProject, foreignManifest);
            var foreignProjectRejected = false;
            try
            {
                _ = ProjectVaultStore.Save(project, foreignManifest);
            }
            catch (InvalidOperationException)
            {
                foreignProjectRejected = true;
            }
            var preservedForeignProject = ProjectVaultStore.Load(foreignManifest);
            AssertTimeline(
                foreignProjectRejected && preservedForeignProject.Id == foreignProject.Id,
                "Project Vault overwrote a same-name manifest that belongs to a different project.");

            var recoveryRoot = Path.Combine(temporaryRoot, "interrupted-save");
            var recoveryManifest = Path.Combine(recoveryRoot, "Recovery.v2dProject");
            var recoveryProject = VectorProject.CreateEmpty();
            recoveryProject.Name = "Before interruption";
            ProjectVaultStore.Save(recoveryProject, recoveryManifest);

            var preparedOperationId = Guid.NewGuid().ToString("N");
            var preparedPaths = ProjectVaultStore.GetSaveRecoveryPathsForRegression(
                recoveryManifest,
                preparedOperationId);
            Directory.CreateDirectory(preparedPaths.BackupRoot);
            Directory.Move(
                Path.Combine(recoveryRoot, ".Vault"),
                Path.Combine(preparedPaths.BackupRoot, ".Vault"));
            Directory.Move(
                Path.Combine(recoveryRoot, ".TimeLine"),
                Path.Combine(preparedPaths.BackupRoot, ".TimeLine"));
            File.Move(
                recoveryManifest,
                Path.Combine(preparedPaths.BackupRoot, Path.GetFileName(recoveryManifest)));
            Directory.CreateDirectory(preparedPaths.StagingRoot);
            Directory.CreateDirectory(Path.Combine(recoveryRoot, ".Vault"));
            File.WriteAllText(Path.Combine(recoveryRoot, ".Vault", "partial.svg"), "partial");
            ProjectVaultStore.WriteSaveJournalForRegression(
                recoveryManifest,
                preparedOperationId,
                recoveryProject.Id,
                hadManagedProject: true,
                installed: false);

            var rolledBackProject = ProjectVaultStore.Load(recoveryManifest);
            AssertTimeline(
                rolledBackProject.Id == recoveryProject.Id
                && rolledBackProject.Name == recoveryProject.Name
                && File.Exists(recoveryManifest)
                && Directory.Exists(Path.Combine(recoveryRoot, ".Vault"))
                && Directory.Exists(Path.Combine(recoveryRoot, ".TimeLine"))
                && !File.Exists(Path.Combine(recoveryRoot, ".Vault", "partial.svg"))
                && !Directory.Exists(preparedPaths.StagingRoot)
                && !Directory.Exists(preparedPaths.BackupRoot)
                && !File.Exists(preparedPaths.JournalPath),
                "Prepared project-save recovery did not restore the previous complete managed file set.");

            var damagedOperationId = Guid.NewGuid().ToString("N");
            var damagedPaths = ProjectVaultStore.GetSaveRecoveryPathsForRegression(
                recoveryManifest,
                damagedOperationId);
            Directory.CreateDirectory(damagedPaths.BackupRoot);
            Directory.Move(
                Path.Combine(recoveryRoot, ".Vault"),
                Path.Combine(damagedPaths.BackupRoot, ".Vault"));
            Directory.Move(
                Path.Combine(recoveryRoot, ".TimeLine"),
                Path.Combine(damagedPaths.BackupRoot, ".TimeLine"));
            File.Move(
                recoveryManifest,
                Path.Combine(damagedPaths.BackupRoot, Path.GetFileName(recoveryManifest)));

            rolledBackProject.Name = "After interruption";
            ProjectVaultStore.Save(rolledBackProject, recoveryManifest);
            var damagedSvgPath = Directory.GetFiles(Path.Combine(recoveryRoot, ".Vault"), "*.svg").Single();
            File.AppendAllText(damagedSvgPath, "<!-- incomplete installed file -->");
            ProjectVaultStore.WriteSaveJournalForRegression(
                recoveryManifest,
                damagedOperationId,
                recoveryProject.Id,
                hadManagedProject: true,
                installed: true);

            var recoveredDamagedInstall = ProjectVaultStore.Load(recoveryManifest);
            AssertTimeline(
                recoveredDamagedInstall.Id == recoveryProject.Id
                && recoveredDamagedInstall.Name == recoveryProject.Name
                && !File.ReadAllText(
                    Directory.GetFiles(Path.Combine(recoveryRoot, ".Vault"), "*.svg").Single())
                    .Contains("incomplete installed file", StringComparison.Ordinal)
                && !Directory.Exists(damagedPaths.StagingRoot)
                && !Directory.Exists(damagedPaths.BackupRoot)
                && !File.Exists(damagedPaths.JournalPath),
                "Installed project-save recovery deleted the valid backup instead of rolling back damaged files.");

            var missingTargetOperationId = Guid.NewGuid().ToString("N");
            var missingTargetPaths = ProjectVaultStore.GetSaveRecoveryPathsForRegression(
                recoveryManifest,
                missingTargetOperationId);
            Directory.CreateDirectory(missingTargetPaths.BackupRoot);
            Directory.Move(
                Path.Combine(recoveryRoot, ".Vault"),
                Path.Combine(missingTargetPaths.BackupRoot, ".Vault"));
            Directory.Move(
                Path.Combine(recoveryRoot, ".TimeLine"),
                Path.Combine(missingTargetPaths.BackupRoot, ".TimeLine"));
            File.Move(
                recoveryManifest,
                Path.Combine(missingTargetPaths.BackupRoot, Path.GetFileName(recoveryManifest)));

            recoveredDamagedInstall.Name = "Installed with missing target";
            ProjectVaultStore.Save(recoveredDamagedInstall, recoveryManifest);
            Directory.Delete(Path.Combine(recoveryRoot, ".TimeLine"), recursive: true);
            ProjectVaultStore.WriteSaveJournalForRegression(
                recoveryManifest,
                missingTargetOperationId,
                recoveryProject.Id,
                hadManagedProject: true,
                installed: true);

            var recoveredMissingTarget = ProjectVaultStore.Load(recoveryManifest);
            AssertTimeline(
                recoveredMissingTarget.Id == recoveryProject.Id
                && recoveredMissingTarget.Name == recoveryProject.Name
                && Directory.Exists(Path.Combine(recoveryRoot, ".Vault"))
                && Directory.Exists(Path.Combine(recoveryRoot, ".TimeLine"))
                && File.Exists(recoveryManifest)
                && !Directory.Exists(missingTargetPaths.StagingRoot)
                && !Directory.Exists(missingTargetPaths.BackupRoot)
                && !File.Exists(missingTargetPaths.JournalPath),
                "Installed project-save recovery did not restore a valid backup when a target was missing.");

            var installedOperationId = Guid.NewGuid().ToString("N");
            var installedPaths = ProjectVaultStore.GetSaveRecoveryPathsForRegression(
                recoveryManifest,
                installedOperationId);
            Directory.CreateDirectory(installedPaths.StagingRoot);
            Directory.CreateDirectory(installedPaths.BackupRoot);
            File.WriteAllText(Path.Combine(installedPaths.StagingRoot, "stale.tmp"), "stale");
            File.WriteAllText(Path.Combine(installedPaths.BackupRoot, "stale.tmp"), "stale");
            ProjectVaultStore.WriteSaveJournalForRegression(
                recoveryManifest,
                installedOperationId,
                recoveryProject.Id,
                hadManagedProject: true,
                installed: true);

            var installedProject = ProjectVaultStore.Load(recoveryManifest);
            AssertTimeline(
                installedProject.Id == recoveryProject.Id
                && installedProject.Name == recoveryProject.Name
                && !Directory.Exists(installedPaths.StagingRoot)
                && !Directory.Exists(installedPaths.BackupRoot)
                && !File.Exists(installedPaths.JournalPath),
                "Installed project-save recovery did not preserve the complete new file set and clean recovery files.");
        }
        finally
        {
            try
            {
                if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
            }
            catch
            {
                // A later regression run uses a unique directory.
            }
        }

        Console.WriteLine("project_vault_persistence_regression=ok");
    }

    private static void RunDrawingObjectInstancePlaybackRegression()
    {
        var project = VectorProject.CreateEmpty();
        var root = project.DrawingObjects[0];
        root.Scene.CreateEmpty(1, 20);
        var animated = project.AddDrawingObject("Animated child");
        animated.Scene.CreateEmpty(1, 4);
        animated.Scene.EditFrame = 0;
        animated.Scene.AddObject(0, PointF.Empty, new SizeF(24, 24), 0, 0, Color.Coral, 4, ShapeKind.Rectangle);
        AssertTimeline(
            animated.Scene.InsertTimelineBlankKeyframe(0, 1),
            "Instance playback regression could not create its second source cel.");
        animated.Scene.EditFrame = 1;
        animated.Scene.AddObject(0, PointF.Empty, new SizeF(24, 24), 0, 0, Color.CornflowerBlue, 4, ShapeKind.Rectangle);
        AssertTimeline(
            animated.Scene.InsertTimelineBlankKeyframe(0, 2),
            "Instance playback regression could not terminate its second source cel.");
        animated.Scene.EditFrame = 0;

        AssertTimeline(
            project.TryAddDrawingObjectInstance(root.Id, animated.Id, PointF.Empty, out var nested)
            && nested is not null,
            "Instance playback regression could not create its nested instance.");
        nested!.PlaybackMode = DrawingObjectPlaybackMode.HoldFrame;
        nested.HoldFrame = 1;

        var scene = project.Scenes[0];
        var sceneTrack = scene.Timeline.FindTrackByTargetId(scene.ActiveLayerId)
            ?? throw new InvalidOperationException("Instance playback regression lost its scene track.");
        scene.Timeline.SetTrackDuration(sceneTrack.Id, 20);
        DrawingObjectInstanceDefinition? rootInstance = null;
        DrawingObjectInstanceDefinition? direct = null;
        AssertTimeline(
            project.TryAddSceneInstance(scene.Id, root.Id, new PointF(100, 0), 0, out rootInstance)
            && rootInstance is not null
            && project.TryAddSceneInstance(scene.Id, animated.Id, new PointF(200, 0), 1, out direct)
            && direct is not null,
            "Instance playback regression could not create its scene instances.");
        direct!.PlaybackMode = DrawingObjectPlaybackMode.HoldFrame;
        direct.HoldFrame = 0;
        var defaultAppearanceState = direct.EvaluateState(0);
        var legacyStateNode = System.Text.Json.JsonSerializer.SerializeToNode(defaultAppearanceState)!.AsObject();
        legacyStateNode.Remove(nameof(InstanceFrameState.Alpha));
        legacyStateNode.Remove(nameof(InstanceFrameState.TintArgb));
        var legacyAppearanceState = System.Text.Json.JsonSerializer.Deserialize<InstanceFrameState>(
            legacyStateNode.ToJsonString());
        AssertTimeline(
            NearlyEqual(defaultAppearanceState.Alpha, 1f)
            && defaultAppearanceState.TintArgb == Color.White.ToArgb()
            && NearlyEqual(legacyAppearanceState.Alpha, 1f)
            && legacyAppearanceState.TintArgb == Color.White.ToArgb(),
            "Instance appearance defaults or legacy state migration did not preserve the original rendering.");

        var destination = new VectorScene();
        var result = SceneCompositionBuilder.Build(destination, scene, project.DrawingObjects, 10, parentFps: 30);
        AssertTimeline(
            destination.ObjectCount == 2
            && destination.Argb[0] == Color.CornflowerBlue.ToArgb()
            && destination.Argb[1] == Color.Coral.ToArgb()
            && NearlyEqual(destination.X[0], 100)
            && NearlyEqual(destination.X[1], 200)
            && result.TryGetOwner(0, out var nestedOwner)
            && nestedOwner.RootInstanceId == rootInstance!.Id
            && result.TryGetOwner(1, out var directOwner)
            && directOwner.RootInstanceId == direct.Id,
            "Composition did not evaluate two instances of one source at independent held frames.");

        direct.PlaybackMode = DrawingObjectPlaybackMode.PlayOnce;
        direct.PlaybackFps = 24m;
        AssertTimeline(
            direct.ResolvePlaybackFrame(999, 23.976m, 2000) == 1000,
            "Fractional parent FPS was truncated while resolving an instance playback frame.");

        direct.PlaybackMode = DrawingObjectPlaybackMode.Loop;
        direct.PlaybackFps = 15;
        SceneCompositionBuilder.Build(destination, scene, project.DrawingObjects, 10, parentFps: 30);
        var loopFrame = direct.ResolvePlaybackFrame(10, 30, animated.FrameCount);
        AssertTimeline(
            destination.ObjectCount == 2
            && destination.Argb.Take(destination.ObjectCount).All(argb => argb == Color.CornflowerBlue.ToArgb())
            && loopFrame == 1,
            $"Looping instance playback did not wrap its FPS-scaled local frame: frame={loopFrame}, " +
            $"frameCount={animated.FrameCount}, objects={destination.ObjectCount}, " +
            $"colors={string.Join(',', destination.Argb.Take(destination.ObjectCount).Select(argb => argb.ToString("X8")))}.");

        direct.PlaybackMode = DrawingObjectPlaybackMode.PlayOnce;
        direct.PlaybackFps = 30;
        SceneCompositionBuilder.Build(destination, scene, project.DrawingObjects, 10, parentFps: 30);
        AssertTimeline(
            destination.ObjectCount == 1
            && direct.ResolvePlaybackFrame(10, 30, animated.FrameCount) == 3,
            "Play-once instance playback did not stop on its terminal local frame.");

        var previousFrameState = direct.EvaluateState(9);
        var resizedState = direct.EvaluateState(10) with
        {
            ScaleX = 2.5f,
            ScaleY = 0.4f,
            RotationZ = 25f,
            SkewX = 12f,
            Alpha = 0.5f,
            TintArgb = Color.FromArgb(255, 128, 200, 64).ToArgb(),
            PlaybackFps = 15,
            PlaybackMode = DrawingObjectPlaybackMode.Loop,
            HoldFrame = 1
        };
        direct.SetStateAtFrame(10, resizedState);
        SceneCompositionBuilder.Build(destination, scene, project.DrawingObjects, 9, parentFps: 30);
        var previousFrameObjectCount = destination.ObjectCount;
        SceneCompositionBuilder.Build(destination, scene, project.DrawingObjects, 10, parentFps: 30);
        AssertTimeline(
            previousFrameState.PlaybackFps == 30
            && previousFrameState.PlaybackMode == DrawingObjectPlaybackMode.PlayOnce
            && previousFrameObjectCount == 1
            && destination.ObjectCount == 2
            && destination.Argb[0] == Color.CornflowerBlue.ToArgb()
            && destination.Argb[1] == ApplyInstanceAppearanceForRegression(
                Color.CornflowerBlue.ToArgb(),
                0.5f,
                Color.FromArgb(255, 128, 200, 64).ToArgb())
            && direct.EvaluateState(10).PlaybackFps == 15
            && direct.EvaluateState(10).PlaybackMode == DrawingObjectPlaybackMode.Loop
            && direct.EvaluateState(10).HoldFrame == 1
            && MainForm.RestoreDrawingObjectOriginalSize(direct, 10)
            && NearlyEqual(direct.EvaluateState(10).ScaleX, 1)
            && NearlyEqual(direct.EvaluateState(10).ScaleY, 1)
            && NearlyEqual(direct.EvaluateState(10).RotationZ, 25)
            && NearlyEqual(direct.EvaluateState(10).SkewX, 12)
            && NearlyEqual(direct.EvaluateState(10).Alpha, 0.5f)
            && direct.EvaluateState(10).TintArgb == Color.FromArgb(255, 128, 200, 64).ToArgb()
            && direct.EvaluateState(10).PlaybackFps == 15
            && direct.EvaluateState(10).PlaybackMode == DrawingObjectPlaybackMode.Loop
            && direct.EvaluateState(10).HoldFrame == 1
            && direct.EvaluateState(9) == previousFrameState
            && !MainForm.CanRestoreDrawingObjectOriginalSize(direct.EvaluateState(10))
            && !MainForm.RestoreDrawingObjectOriginalSize(direct, 10),
            "Playback controls or restoring original size changed another keyframe or unrelated instance state.");
    }

    private static void RunSkewedSceneCompositionRegression()
    {
        var project = VectorProject.CreateEmpty();
        var drawingObject = project.DrawingObjects[0];
        drawingObject.Scene.CreateEmpty();
        drawingObject.Scene.AddObject(
            0,
            PointF.Empty,
            new SizeF(120, 80),
            0,
            6,
            Color.Teal,
            Color.White,
            6,
            ShapeKind.Rectangle);
        var scene = project.Scenes[0];
        AssertTimeline(
            project.TryAddSceneInstance(scene.Id, drawingObject.Id, new PointF(180, 120), 0, out var instance)
            && instance is not null,
            "Validated skewed scene-instance insertion was rejected.");
        instance!.SkewX = 28;

        var destination = new VectorScene();
        SceneCompositionBuilder.Build(destination, scene, project.DrawingObjects, 0);
        AssertTimeline(
            destination.ObjectCount == 1
            && destination.ShapeKind[0] == ShapeKind.Path
            && destination.TryGetPathWorldContours(0, out var contours)
            && contours.Length == 1
            && contours[0].Length >= 4,
            "Scene composition did not preserve a skewed drawing-object instance as real path geometry.");
    }

    private static void RunLayerEffectCompositionRegression()
    {
        var project = VectorProject.CreateEmpty();
        var drawingObject = project.DrawingObjects[0];
        drawingObject.Scene.CreateEmpty();
        drawingObject.Scene.EditFrame = 0;
        drawingObject.Scene.AddObject(
            0,
            PointF.Empty,
            new SizeF(120, 80),
            0,
            0,
            Color.Coral,
            8,
            ShapeKind.Rectangle);
        drawingObject.Scene.AddFolderLayer("Mask Group");
        var sourceMask = drawingObject.Scene.AddMaskLayer("Mask Shape");
        drawingObject.Scene.AddObject(
            sourceMask,
            PointF.Empty,
            new SizeF(60, 40),
            0,
            0,
            Color.White,
            8,
            ShapeKind.Rectangle);
        var scene = project.Scenes[0];
        AssertTimeline(
            project.TryAddSceneInstance(scene.Id, drawingObject.Id, PointF.Empty, 0, out _),
            "Layer-effect composition setup rejected a valid drawing-object instance.");

        var sourceContentLayer = drawingObject.Scene.ObjectLayer[0];
        drawingObject.Scene.SetLayerColor(sourceContentLayer, Color.DeepSkyBlue);
        drawingObject.Scene.SetLayerOutline(sourceContentLayer, true);
        var destination = new VectorScene();
        SceneCompositionBuilder.Build(destination, scene, project.DrawingObjects, 0);
        var folder = Array.FindIndex(destination.LayerKinds, kind => kind == DrawingLayerKind.Folder);
        var mask = Array.FindIndex(destination.LayerKinds, kind => kind == DrawingLayerKind.Mask);
        var content = Array.FindIndex(destination.LayerKinds, kind => kind == DrawingLayerKind.Drawing);
        AssertTimeline(
            destination.ObjectCount == 2
            && folder >= 0
            && mask >= 0
            && content >= 0
            && destination.GetLayerParentIndex(mask) == folder
            && destination.GetLayerParentIndex(content) == folder
            && destination.TryGetMaskLayerIndex(content, out var linkedMask)
            && linkedMask == mask
            && destination.LayerOutline[content]
            && destination.GetLayerColor(content).ToArgb() == Color.DeepSkyBlue.ToArgb()
            && destination.HasLayerEffects,
            $"Flattened composition lost folder, mask, or Outline relationships: objects={destination.ObjectCount}, folder={folder}, mask={mask}, content={content}, parentMask={destination.GetLayerParentIndex(mask)}, parentContent={destination.GetLayerParentIndex(content)}, outlined={(content >= 0 && destination.LayerOutline[content])}, color={(content >= 0 ? destination.GetLayerColor(content).ToArgb() : 0):X8}.");

        var sceneLayer = scene.Layers[0];
        scene.SetLayerColor(sceneLayer.Id, Color.Magenta);
        scene.SetLayerOutline(sceneLayer.Id, true);
        SceneCompositionBuilder.Build(destination, scene, project.DrawingObjects, 0);
        AssertTimeline(
            destination.LayerOutline.All(outlined => outlined)
            && destination.LayerColorArgb.All(argb => argb == Color.Magenta.ToArgb()),
            "Scene-layer Outline did not override every nested instance layer with the scene layer color.");
    }

    private static void RunSparseLayerCompositionRegression()
    {
        var project = VectorProject.CreateEmpty();
        var drawingObject = project.DrawingObjects[0];
        drawingObject.Scene.CreateEmpty(3);
        var sourceLine = drawingObject.Scene.AddCurveSegment(
            1,
            new PointF(10.4f, 20.6f),
            new PointF(43.7f, -18.2f),
            new PointF(98.3f, 61.9f),
            VectorUnits.StrokePointsToUnits(2),
            Color.Transparent,
            Color.White,
            8);
        var scene = project.Scenes[0];
        AssertTimeline(
            project.TryAddSceneInstance(scene.Id, drawingObject.Id, PointF.Empty, 0, out _),
            "Sparse-layer composition setup rejected a valid drawing-object instance.");

        var destination = new VectorScene();
        var result = SceneCompositionBuilder.Build(destination, scene, project.DrawingObjects, 0);
        var tracks = destination.LayerIds
            .Select(destination.Timeline.FindTrackByTargetId)
            .ToArray();
        drawingObject.Scene.TryGetLineEndpoint(sourceLine, startEndpoint: true, out var sourceStart);
        drawingObject.Scene.TryGetLineEndpoint(sourceLine, startEndpoint: false, out var sourceEnd);
        var reference = new VectorScene();
        reference.CreateEmpty(3);
        var referenceLine = reference.AddCubicCurveSegment(
            1,
            VectorUnits.Quantize(sourceStart),
            VectorUnits.Quantize(new PointF(
                drawingObject.Scene.CurveControlX[sourceLine],
                drawingObject.Scene.CurveControlY[sourceLine])),
            VectorUnits.Quantize(new PointF(
                drawingObject.Scene.CurveControl2X[sourceLine],
                drawingObject.Scene.CurveControl2Y[sourceLine])),
            VectorUnits.Quantize(sourceEnd),
            drawingObject.Scene.Stroke[sourceLine],
            Color.FromArgb(drawingObject.Scene.Argb[sourceLine]),
            Color.FromArgb(drawingObject.Scene.StrokeArgb[sourceLine]),
            drawingObject.Scene.AtomCount[sourceLine]);
        AssertTimeline(
            destination.LayerCount == 3
            && destination.ObjectCount == 1
            && result.ObjectOwners.Count == 1
            && tracks[0] is not null
            && !tracks[0]!.EvaluateExposure(0).HasContent
            && tracks[1] is not null
            && tracks[1]!.EvaluateExposure(0).HasContent
            && tracks[2] is not null
            && !tracks[2]!.EvaluateExposure(0).HasContent
            && NearlyEqual(destination.X[0], reference.X[referenceLine])
            && NearlyEqual(destination.Y[0], reference.Y[referenceLine])
            && NearlyEqual(destination.Width[0], reference.Width[referenceLine])
            && NearlyEqual(destination.Height[0], reference.Height[referenceLine])
            && NearlyEqual(destination.Angle[0], reference.Angle[referenceLine])
            && NearlyEqual(destination.CurveControlX[0], reference.CurveControlX[referenceLine])
            && NearlyEqual(destination.CurveControlY[0], reference.CurveControlY[referenceLine])
            && NearlyEqual(destination.CurveControl2X[0], reference.CurveControl2X[referenceLine])
            && NearlyEqual(destination.CurveControl2Y[0], reference.CurveControl2Y[referenceLine]),
            "Sparse-layer composition populated an empty layer or changed identity-transform line quantization.");
    }

    private static void RunChunkedCompositionRegression()
    {
        var project = VectorProject.CreateEmpty();
        var drawingObject = project.DrawingObjects[0];
        drawingObject.Scene.Generate(8, 8192, 8_192_000);
        var scene = project.Scenes[0];
        AssertTimeline(
            project.TryAddSceneInstance(scene.Id, drawingObject.Id, PointF.Empty, 0, out var instance)
            && instance is not null,
            "Chunked composition setup rejected a valid drawing-object instance.");
        var sceneTrack = scene.Timeline.FindTrackByTargetId(scene.Layers[0].Id)
            ?? throw new InvalidOperationException("Chunked composition regression lost its scene-layer track.");
        scene.Timeline.SetTrackDuration(sceneTrack.Id, 21);
        instance!.Alpha = 0.5f;
        instance.TintArgb = Color.FromArgb(255, 128, 192, 224).ToArgb();
        var sourceArgb = drawingObject.Scene.Argb.Take(drawingObject.Scene.ObjectCount).ToArray();
        var sourceFirstObject = Enumerable.Range(0, drawingObject.Scene.ObjectCount)
            .OrderBy(index => drawingObject.Scene.ObjectLayer[index])
            .ThenBy(index => drawingObject.Scene.ObjectOrder[index])
            .ThenBy(index => drawingObject.Scene.ObjectSubOrder[index])
            .ThenBy(index => index)
            .First();
        var destination = new VectorScene();
        var packedResult = SceneCompositionBuilder.Build(destination, scene, project.DrawingObjects, 20);
        var evaluatedAppearance = instance.EvaluateState(20);
        var expectedFirstArgb = ApplyInstanceAppearanceForRegression(
            sourceArgb[sourceFirstObject],
            evaluatedAppearance.Alpha,
            evaluatedAppearance.TintArgb);
        var sourceUnchanged = drawingObject.Scene.Argb
            .Take(drawingObject.Scene.ObjectCount)
            .SequenceEqual(sourceArgb);
        AssertTimeline(
            destination.ObjectCount == drawingObject.Scene.ObjectCount
            && packedResult.ObjectOwners.Count == destination.ObjectCount
            && destination.Argb[0] == expectedFirstArgb
            && sourceUnchanged,
            "Packed instance composition did not apply Alpha and multiply tint without mutating its source: "
            + $"objects={destination.ObjectCount}/{drawingObject.Scene.ObjectCount}, owners={packedResult.ObjectOwners.Count}, "
            + $"source={sourceFirstObject}, actual=0x{(destination.ObjectCount > 0 ? destination.Argb[0] : 0):X8}, expected=0x{expectedFirstArgb:X8}, "
            + $"alpha={evaluatedAppearance.Alpha:R}, tint=0x{evaluatedAppearance.TintArgb:X8}, sourceUnchanged={sourceUnchanged}.");

        drawingObject.Scene.EditFrame = 20;
        drawingObject.Scene.AddPathObject(
            0,
            [new PointF(-80, -40), new PointF(80, -40), new PointF(60, 70), new PointF(-70, 65)],
            0,
            Color.Coral,
            Color.Transparent,
            12);
        var result = SceneCompositionBuilder.Build(destination, scene, project.DrawingObjects, 20);
        var destinationPaths = Enumerable.Range(0, destination.ObjectCount)
            .Where(index => destination.ShapeKind[index] == ShapeKind.Path)
            .ToArray();
        AssertTimeline(
            destination.ObjectCount == drawingObject.Scene.ObjectCount
            && result.ObjectOwners.Count == destination.ObjectCount
            && destinationPaths.Length == 1
            && destination.TryGetPathWorldContours(destinationPaths[0], out var contours)
            && contours.Length == 1
            && contours[0].Length == 4,
            "Chunked mixed-geometry composition lost objects, owners, or path geometry.");
    }

    private static void RunDegeneratePathCompositionRegression()
    {
        var project = VectorProject.CreateEmpty();
        var drawingObject = project.DrawingObjects[0];
        drawingObject.Scene.CreateEmpty();
        drawingObject.Scene.AddPathObject(
            0,
            [new PointF(-40, -40), new PointF(40, -40), new PointF(40, 40), new PointF(-40, 40)],
            0,
            Color.Teal,
            Color.Transparent,
            8);
        var scene = project.Scenes[0];
        AssertTimeline(
            project.TryAddSceneInstance(scene.Id, drawingObject.Id, PointF.Empty, 0, out var instance)
            && instance is not null,
            "Degenerate-path composition setup rejected a valid drawing-object instance.");
        instance!.ScaleX = 0;
        instance.ScaleY = 0;

        var destination = new VectorScene();
        var result = SceneCompositionBuilder.Build(destination, scene, project.DrawingObjects, 0);
        var track = destination.Timeline.FindTrackByTargetId(destination.LayerIds[0]);
        AssertTimeline(
            destination.ObjectCount == 0
            && result.ObjectOwners.Count == 0
            && track is not null
            && !track.EvaluateExposure(0).HasContent,
            "A path discarded by a degenerate transform left a populated composition keyframe.");
    }

    private static void RunFillEdgeBezierCompositionRegression()
    {
        var project = VectorProject.CreateEmpty();
        var drawingObject = project.DrawingObjects[0];
        drawingObject.Scene.CreateEmpty();
        var fill = drawingObject.Scene.AddPathObject(
            0,
            [
                new PointF(-120, -80),
                new PointF(140, -60),
                new PointF(160, 100),
                new PointF(-100, 120)
            ],
            0,
            Color.Coral,
            Color.Transparent,
            8);
        var prepared = drawingObject.Scene.TryConvertFillToBezierPath(fill)
            && drawingObject.Scene.TryGetPathBezierSegment(fill, 0, out var segment)
            && drawingObject.Scene.SetPathBezierSegment(
                fill,
                segment.PartIndex,
                segment.Start,
                new PointF(segment.Control1.X, segment.Control1.Y - 90),
                new PointF(segment.Control2.X, segment.Control2.Y - 140),
                segment.End);
        var hasSourceContours = drawingObject.Scene.TryGetPathBezierWorldContours(fill, out var sourceContours);
        AssertTimeline(
            prepared && hasSourceContours,
            "Fill-edge Bezier composition setup did not retain exact source curves.");

        var scene = project.Scenes[0];
        AssertTimeline(
            project.TryAddSceneInstance(scene.Id, drawingObject.Id, new PointF(400, 300), 0, out var instance)
            && instance is not null,
            "Fill-edge Bezier composition setup rejected a valid instance.");
        instance!.RotationZ = 90;

        var destination = new VectorScene();
        var result = SceneCompositionBuilder.Build(destination, scene, project.DrawingObjects, 0);
        var hasDestinationContours = destination.TryGetPathBezierWorldContours(0, out var destinationContours);
        AssertTimeline(
            destination.ObjectCount == 1
            && result.TryGetOwner(0, out var owner)
            && owner.DrawingObjectId == drawingObject.Id
            && hasDestinationContours
            && destinationContours.Length == sourceContours.Length
            && destinationContours[0].Length == sourceContours[0].Length,
            "Scene composition flattened or dropped exact fill-edge Bezier geometry.");

        static PointF RotateTranslate90(PointF point) => new(400 - point.Y, 300 + point.X);
        for (var contourIndex = 0; contourIndex < sourceContours.Length; contourIndex++)
        {
            for (var nodeIndex = 0; nodeIndex < sourceContours[contourIndex].Length; nodeIndex++)
            {
                var source = sourceContours[contourIndex][nodeIndex];
                var composed = destinationContours[contourIndex][nodeIndex];
                AssertTimeline(
                    PointsNear(composed.Anchor, RotateTranslate90(source.Anchor))
                    && PointsNear(composed.IncomingControl, RotateTranslate90(source.IncomingControl))
                    && PointsNear(composed.OutgoingControl, RotateTranslate90(source.OutgoingControl)),
                    "Scene composition did not apply the instance transform equally to fill-edge anchors and controls.");
            }
        }
    }

    private static void AssertTimeline(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void RunLayerBlendRegression()
    {
        var backdrop = Color.FromArgb(255, 64, 128, 192);
        var source = Color.FromArgb(255, 128, 64, 32);
        AssertColorNear(
            LayerBlendCompositor.CompositeColorForRegression(backdrop, source, LayerBlendMode.Normal),
            source,
            "Normal");
        AssertColorNear(
            LayerBlendCompositor.CompositeColorForRegression(backdrop, source, LayerBlendMode.Multiply),
            Color.FromArgb(255, 32, 32, 24),
            "Multiply");
        AssertColorNear(
            LayerBlendCompositor.CompositeColorForRegression(backdrop, source, LayerBlendMode.Screen),
            Color.FromArgb(255, 160, 160, 200),
            "Screen");
        AssertColorNear(
            LayerBlendCompositor.CompositeColorForRegression(backdrop, source, LayerBlendMode.Difference),
            Color.FromArgb(255, 64, 64, 160),
            "Difference");

        var translucentBackdrop = Color.FromArgb(120, 40, 100, 180);
        var translucentSource = Color.FromArgb(180, 220, 80, 30);
        var sourceAlpha = translucentSource.A / 255f * 0.75f;
        var backdropAlpha = translucentBackdrop.A / 255f;
        var expectedAlpha = (int)MathF.Round((sourceAlpha + backdropAlpha * (1f - sourceAlpha)) * 255f);
        foreach (var mode in Enum.GetValues<LayerBlendMode>())
        {
            var result = LayerBlendCompositor.CompositeColorForRegression(
                translucentBackdrop,
                translucentSource,
                mode,
                opacity: 0.75f,
                dissolveSeed: 0x12345678u,
                x: 11,
                y: 7);
            if (mode != LayerBlendMode.Dissolve && Math.Abs(result.A - expectedAlpha) > 1)
            {
                throw new InvalidOperationException(
                    $"Layer blend mode {mode} produced alpha {result.A}; expected {expectedAlpha}.");
            }
        }

        var dissolveA = LayerBlendCompositor.CompositeColorForRegression(
            translucentBackdrop,
            translucentSource,
            LayerBlendMode.Dissolve,
            opacity: 0.55f,
            dissolveSeed: 0x5a17c9e3u,
            x: 29,
            y: 31);
        var dissolveB = LayerBlendCompositor.CompositeColorForRegression(
            translucentBackdrop,
            translucentSource,
            LayerBlendMode.Dissolve,
            opacity: 0.55f,
            dissolveSeed: 0x5a17c9e3u,
            x: 29,
            y: 31);
        if (dissolveA.ToArgb() != dissolveB.ToArgb())
        {
            throw new InvalidOperationException("Dissolve blend mode changed between identical samples.");
        }

        var layeredScene = new VectorScene();
        layeredScene.CreateEmpty();
        var lowerLayer = layeredScene.AddLayer("Backdrop");
        layeredScene.SetLayerBlendMode(0, LayerBlendMode.Multiply);
        using (var layeredBitmap = new Bitmap(12, 8, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
        using (var layeredGraphics = Graphics.FromImage(layeredBitmap))
        using (var compositor = new LayerBlendCompositor(layeredBitmap.Size))
        {
            compositor.CompositeTo(layeredGraphics, layeredScene, (graphics, layer) =>
            {
                using var brush = new SolidBrush(layer == lowerLayer ? Color.CornflowerBlue : Color.Coral);
                graphics.FillRectangle(brush, layer == lowerLayer ? new Rectangle(0, 0, 12, 8) : new Rectangle(0, 0, 6, 8));
            });

            AssertColorNear(layeredBitmap.GetPixel(9, 4), Color.CornflowerBlue, "Multiply lower-layer preservation");
        }

        var stageScene = new VectorScene();
        stageScene.CreateEmpty();
        var stageLowerLayer = stageScene.AddLayer("Stage Backdrop");
        stageScene.AddObject(0, PointF.Empty, new SizeF(2_000, 2_000), 0, 0, Color.Coral, Color.Transparent, 12, ShapeKind.Rectangle);
        stageScene.AddObject(stageLowerLayer, PointF.Empty, new SizeF(4_000, 3_000), 0, 0, Color.CornflowerBlue, Color.Transparent, 12, ShapeKind.Rectangle);
        stageScene.SetLayerBlendMode(0, LayerBlendMode.Multiply);
        using (var stage = new StageControl(stageScene) { ClientSize = new Size(320, 240), WorldGridOpacity = 0 })
        using (var stageBitmap = new Bitmap(stage.ClientSize.Width, stage.ClientSize.Height))
        using (var stageGraphics = Graphics.FromImage(stageBitmap))
        {
            var drawGdi = typeof(StageControl).GetMethod(
                "DrawGdi",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("The GDI layer-blend regression entry point could not be located.");
            drawGdi.Invoke(stage, [stageGraphics]);
            var lowerOnlyPoint = Point.Round(stage.WorldToScreen(1_500, 0));
            AssertColorNear(
                stageBitmap.GetPixel(lowerOnlyPoint.X, lowerOnlyPoint.Y),
                Color.CornflowerBlue,
                "Stage lower-layer preservation");
        }

        static void AssertColorNear(Color actual, Color expected, string mode)
        {
            if (Math.Abs(actual.A - expected.A) <= 1
                && Math.Abs(actual.R - expected.R) <= 1
                && Math.Abs(actual.G - expected.G) <= 1
                && Math.Abs(actual.B - expected.B) <= 1)
            {
                return;
            }

            throw new InvalidOperationException(
                $"{mode} blend equation returned {actual.ToArgb():X8}; expected {expected.ToArgb():X8}.");
        }
    }

    public static void RunStageRendererRegression()
    {
        RunLayerBlendRegression();
        RunImportedSvgRasterizerRegression();
        RunImportedSvgBreakApartRegression();
        RunSelectionHighlightStyleRegression();
        RunTemporaryCanvasPanRegression();
        RunImmediateMarqueeOverlayRegression();
        RunMarqueeToolPolicyRegression();
        RunWorkspacePanelAnimationRegression();
        RunWorldGridRegression();
        RunDenseOverlapLodRegression();
        RunLinkedFillBoundaryRegression();
        RunShapeToolRegression();
        RunReleaseNotesRegression();
        RunHotReloadModuleRoutingRegression();
        RunColorHarmonyRegression();
        RunGradientPresetRegression();
        RunGradientPaintRegression();
        RunLineSegmentMergeRegression();
        RunClosedFillRegionRegression();
        RunTransformGeometryRegression();
        RunSoftBrushRegression();
        RunBrushFrequencyRegression();
        RunPressureBrushRegression();
        RunBrushWorldScaleRegression();
        RunBrushEraserRegression();

        var editable = new VectorScene();
        editable.CreateEmpty();
        var editableObject = editable.AddObject(0, new PointF(120, 80), new SizeF(180, 120), 0, 0, Color.Teal, 12, ShapeKind.Rectangle);
        editable.SetLinearGradient(editableObject, Color.Teal, Color.Coral);
        var editableText = editable.AddTextObject(
            0,
            new PointF(1_000, -800),
            new TextObjectData(
                "T",
                TextGeometry.FallbackFontFamilyName,
                24,
                TextFontStyle.Bold,
                TextHorizontalAlignment.Center,
                new SizeF(800, 1_000)),
            Color.White);
        var underlay = new VectorScene();
        underlay.CreateEmpty();
        underlay.AddFreehandStroke(
            0,
            [new PointF(-200, -120), new PointF(-120, -40), new PointF(-40, -100)],
            12,
            Color.Coral,
            brushStroke: false,
            12);
        var underlayOverlap = new PointF(-120, 100);
        underlay.AddObject(0, underlayOverlap, new SizeF(120, 90), 0, 0, Color.LimeGreen, 13, ShapeKind.Rectangle);
        var onionSkin = new VectorScene();
        onionSkin.CreateEmpty();
        onionSkin.AddObject(0, underlayOverlap, new SizeF(120, 90), 0, 0, Color.FromArgb(96, Color.CornflowerBlue), 12, ShapeKind.Rectangle);

        using var form = new Form
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-30_000, -30_000),
            ClientSize = new Size(640, 420)
        };
        using var stage = new StageControl(editable) { Dock = DockStyle.Fill };
        stage.BindUnderlayScene(underlay);
        stage.BindOnionSkinScene(onionSkin);
        form.Controls.Add(stage);
        form.Show();
        Application.DoEvents();
        stage.Invalidate();
        stage.Update();
        Application.DoEvents();

        if (!stage.LastFrameUsedDirect2D
            || !stage.GpuAccelerationActive
            || !stage.ImmediateGpuPresentationEnabled)
        {
            throw new InvalidOperationException("The Stage did not activate the immediate Direct2D GPU hardware target.");
        }

        AssertTimeline(
            editable.SetLayerBlendMode(0, LayerBlendMode.Overlay),
            "Renderer regression could not enable a non-Normal layer blend mode.");
        stage.Invalidate();
        stage.Update();
        Application.DoEvents();
        if (stage.LastFrameUsedDirect2D)
        {
            throw new InvalidOperationException("A blended frame bypassed the authoritative software layer compositor.");
        }
        AssertTimeline(
            editable.SetLayerBlendMode(0, LayerBlendMode.Normal),
            "Renderer regression could not restore the Normal layer blend mode.");
        stage.Invalidate();
        stage.Update();
        Application.DoEvents();
        if (!stage.LastFrameUsedDirect2D)
        {
            throw new InvalidOperationException("Direct2D did not resume after restoring all layers to Normal mode.");
        }

        var originalLayerColor = editable.GetLayerColor(0);
        AssertTimeline(
            editable.SetLayerColor(0, Color.DeepPink)
            && editable.SetLayerOutline(0, true),
            "Renderer regression could not enable a colored Outline layer.");
        stage.WorldGridOpacity = 0;
        stage.Invalidate();
        stage.Update();
        Application.DoEvents();
        if (!stage.LastFrameUsedDirect2D || stage.LastStats.TileLod)
        {
            throw new InvalidOperationException("An outlined editable layer did not use the Direct2D object path.");
        }

        using (var outlineBitmap = new Bitmap(stage.ClientSize.Width, stage.ClientSize.Height))
        using (var outlineGraphics = Graphics.FromImage(outlineBitmap))
        {
            var drawGdi = typeof(StageControl).GetMethod(
                "DrawGdi",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("The GDI Outline fallback entry point could not be located.");
            drawGdi.Invoke(stage, [outlineGraphics]);
            var center = Point.Round(stage.WorldToScreen(editable.X[editableObject], editable.Y[editableObject]));
            var leftEdge = Point.Round(stage.WorldToScreen(
                editable.X[editableObject] - editable.Width[editableObject] * 0.5f,
                editable.Y[editableObject]));
            var centerPixel = outlineBitmap.GetPixel(
                Math.Clamp(center.X, 0, outlineBitmap.Width - 1),
                Math.Clamp(center.Y, 0, outlineBitmap.Height - 1));
            var foundOutlinePixel = false;
            for (var y = Math.Max(0, leftEdge.Y - 3); y <= Math.Min(outlineBitmap.Height - 1, leftEdge.Y + 3); y++)
            {
                for (var x = Math.Max(0, leftEdge.X - 3); x <= Math.Min(outlineBitmap.Width - 1, leftEdge.X + 3); x++)
                {
                    var pixel = outlineBitmap.GetPixel(x, y);
                    if (pixel.R >= 180 && pixel.B >= 80 && pixel.G <= 100)
                    {
                        foundOutlinePixel = true;
                        break;
                    }
                }
                if (foundOutlinePixel) break;
            }

            if (!foundOutlinePixel
                || centerPixel.R >= 180 && centerPixel.B >= 80 && centerPixel.G <= 100)
            {
                throw new InvalidOperationException(
                    $"The GDI Outline path did not keep the fill hollow while drawing the layer-color edge: center={centerPixel.ToArgb():X8}, edge={foundOutlinePixel}.");
            }
        }
        editable.SetLayerOutline(0, false);
        editable.SetLayerColor(0, originalLayerColor);
        stage.WorldGridOpacity = 1;
        stage.Invalidate();
        stage.Update();
        Application.DoEvents();

        var polarViewState = stage.CaptureViewState();
        double polarDirect2DCommandMilliseconds;
        double polarDirect2DFrameMilliseconds;
        double polarGdiFrameMilliseconds;
        try
        {
            stage.WorldGridType = WorldGridType.Polar;
            stage.SetVisibleWorldWidth(250);
            stage.Pan(
                -VectorUnits.ToPixels(5_000_000) * stage.Zoom,
                -VectorUnits.ToPixels(3_000_000) * stage.Zoom);
            for (var warmup = 0; warmup < 3; warmup++)
            {
                stage.Invalidate();
                stage.Update();
            }

            const int polarRenderSamples = 12;
            var polarCommandTotal = 0d;
            var polarFrameWatch = Stopwatch.StartNew();
            for (var sample = 0; sample < polarRenderSamples; sample++)
            {
                stage.Invalidate();
                stage.Update();
                polarCommandTotal += stage.LastDirect2DCommandMilliseconds;
            }
            polarFrameWatch.Stop();
            polarDirect2DCommandMilliseconds = polarCommandTotal / polarRenderSamples;
            polarDirect2DFrameMilliseconds = polarFrameWatch.Elapsed.TotalMilliseconds / polarRenderSamples;

            using var polarGdiBitmap = new Bitmap(stage.ClientSize.Width, stage.ClientSize.Height);
            using var polarGdiGraphics = Graphics.FromImage(polarGdiBitmap);
            var drawPolarGdi = typeof(StageControl).GetMethod(
                "DrawGdi",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("The GDI polar-grid fallback entry point could not be located.");
            for (var warmup = 0; warmup < 2; warmup++) drawPolarGdi.Invoke(stage, [polarGdiGraphics]);
            const int polarGdiSamples = 6;
            var polarGdiWatch = Stopwatch.StartNew();
            for (var sample = 0; sample < polarGdiSamples; sample++) drawPolarGdi.Invoke(stage, [polarGdiGraphics]);
            polarGdiWatch.Stop();
            polarGdiFrameMilliseconds = polarGdiWatch.Elapsed.TotalMilliseconds / polarGdiSamples;

            if (!stage.LastFrameUsedDirect2D
                || polarDirect2DCommandMilliseconds > RenderCollectBudgetMilliseconds
                || polarDirect2DFrameMilliseconds > 1000d / 60d
                || polarGdiFrameMilliseconds > 50d)
            {
                throw new InvalidOperationException(
                    $"The zoomed distant polar grid exceeded its bounded render budget: " +
                    $"direct2d={stage.LastFrameUsedDirect2D}, commands={polarDirect2DCommandMilliseconds:0.000}ms, " +
                    $"frame={polarDirect2DFrameMilliseconds:0.000}ms, gdi={polarGdiFrameMilliseconds:0.000}ms.");
            }
        }
        finally
        {
            stage.RestoreViewState(polarViewState);
            stage.Invalidate();
            stage.Update();
        }

        if (stage.LastStats.VisibleObjects != 5)
        {
            throw new InvalidOperationException("The Direct2D scene passes did not include the onion skin, underlay, and editable scene.");
        }

        if (stage.LastOnionSkinScenePassOrder <= 0
            || stage.LastOnionSkinScenePassOrder >= stage.LastUnderlayScenePassOrder
            || stage.LastUnderlayScenePassOrder >= stage.LastEditableScenePassOrder)
        {
            throw new InvalidOperationException("The onion-skin pass did not render below the drawing-object underlay and editable scene.");
        }

        using (var gdiBitmap = new Bitmap(stage.ClientSize.Width, stage.ClientSize.Height))
        using (var gdiGraphics = Graphics.FromImage(gdiBitmap))
        {
            var drawGdi = typeof(StageControl).GetMethod(
                "DrawGdi",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("The GDI fallback entry point could not be located.");
            drawGdi.Invoke(stage, [gdiGraphics]);
            if (stage.LastOnionSkinScenePassOrder <= 0
                || stage.LastOnionSkinScenePassOrder >= stage.LastUnderlayScenePassOrder
                || stage.LastUnderlayScenePassOrder >= stage.LastEditableScenePassOrder)
            {
                throw new InvalidOperationException("The GDI onion-skin pass did not render below the drawing-object underlay and editable scene.");
            }

            var overlapSample = stage.WorldToScreen(underlayOverlap.X, underlayOverlap.Y);
            var overlapPixel = gdiBitmap.GetPixel(
                Math.Clamp((int)MathF.Round(overlapSample.X), 0, gdiBitmap.Width - 1),
                Math.Clamp((int)MathF.Round(overlapSample.Y), 0, gdiBitmap.Height - 1));
            if (overlapPixel.ToArgb() != Color.LimeGreen.ToArgb())
            {
                throw new InvalidOperationException("The GDI onion skin covered the drawing-object underlay at their overlap.");
            }
        }

        if (!ReferenceEquals(stage.Scene, editable))
        {
            throw new InvalidOperationException("The renderer did not restore the editable scene after drawing its underlay.");
        }

        if (!stage.HasCachedDirect2DFreehandGeometry(underlay))
        {
            throw new InvalidOperationException("The Direct2D underlay pass did not populate the freehand geometry cache.");
        }

        var layeredVisibleObjects = stage.LastStats.VisibleObjects;
        stage.BindOnionSkinScene(null);
        stage.SetSelection([editableObject], editableObject);
        if (!stage.SelectionHighlightAnimating
            || stage.SelectionHighlightPulse < 0f
            || stage.SelectionHighlightPulse > 1f)
        {
            throw new InvalidOperationException("Selecting a fill did not start its bounded highlight animation.");
        }

        var fillEdgeViewState = stage.CaptureViewState();
        stage.SetVisibleWorldWidth(800);
        var fillEdgeOverlaySegments = editable.GetEditableFillBezierSegmentParts(editableObject)
            .Select(part => new FillEdgeBezierOverlaySegment(
                part.PartIndex,
                part.Start,
                part.Control1,
                part.Control2,
                part.End))
            .ToArray();
        stage.SetFillEdgeBezierOverlay(editableObject, fillEdgeOverlaySegments, fillEdgeOverlaySegments[0].PartIndex);
        stage.Invalidate();
        stage.Update();
        Application.DoEvents();
        if (!stage.LastFrameUsedDirect2D
            || !stage.FillEdgeBezierOverlayVisible
            || stage.SelectionHighlightAnimating)
        {
            throw new InvalidOperationException("The real-HWND Direct2D Stage did not render the non-glowing fill-edge Bezier overlay.");
        }

        using (var fillEdgeBitmap = new Bitmap(stage.ClientSize.Width, stage.ClientSize.Height))
        using (var fillEdgeGraphics = Graphics.FromImage(fillEdgeBitmap))
        {
            var drawGdi = typeof(StageControl).GetMethod(
                "DrawGdi",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("The GDI fill-edge Bezier overlay entry point could not be located.");
            drawGdi.Invoke(stage, [fillEdgeGraphics]);
            var limePixels = 0;
            for (var y = 0; y < fillEdgeBitmap.Height; y++)
            {
                for (var x = 0; x < fillEdgeBitmap.Width; x++)
                {
                    var pixel = fillEdgeBitmap.GetPixel(x, y);
                    if (pixel.R <= 20 && pixel.G >= 235 && pixel.B <= 20) limePixels++;
                }
            }

            if (limePixels < 20)
            {
                throw new InvalidOperationException("The GDI fallback did not draw a crisp lime fill-edge Bezier overlay.");
            }

            var inactiveAnchor = fillEdgeOverlaySegments[2].Start;
            var inactiveAnchorScreen = Point.Round(stage.WorldToScreen(inactiveAnchor.X, inactiveAnchor.Y));
            var inactiveAnchorHasDarkCore = false;
            var inactiveAnchorHasLimeBorder = false;
            for (var y = Math.Max(0, inactiveAnchorScreen.Y - 1);
                 y <= Math.Min(fillEdgeBitmap.Height - 1, inactiveAnchorScreen.Y + 1);
                 y++)
            {
                for (var x = Math.Max(0, inactiveAnchorScreen.X - 1);
                     x <= Math.Min(fillEdgeBitmap.Width - 1, inactiveAnchorScreen.X + 1);
                     x++)
                {
                    var pixel = fillEdgeBitmap.GetPixel(x, y);
                    if (Math.Abs(pixel.R - stage.BackColor.R) <= 20
                        && Math.Abs(pixel.G - stage.BackColor.G) <= 20
                        && Math.Abs(pixel.B - stage.BackColor.B) <= 20)
                    {
                        inactiveAnchorHasDarkCore = true;
                        break;
                    }
                }
                if (inactiveAnchorHasDarkCore) break;
            }

            for (var y = Math.Max(0, inactiveAnchorScreen.Y - 5);
                 y <= Math.Min(fillEdgeBitmap.Height - 1, inactiveAnchorScreen.Y + 5);
                 y++)
            {
                for (var x = Math.Max(0, inactiveAnchorScreen.X - 5);
                     x <= Math.Min(fillEdgeBitmap.Width - 1, inactiveAnchorScreen.X + 5);
                     x++)
                {
                    var pixel = fillEdgeBitmap.GetPixel(x, y);
                    if (pixel.G >= 160
                        && pixel.G >= pixel.R + 80
                        && pixel.G >= pixel.B + 80)
                    {
                        inactiveAnchorHasLimeBorder = true;
                        break;
                    }
                }
                if (inactiveAnchorHasLimeBorder) break;
            }

            if (!inactiveAnchorHasDarkCore || !inactiveAnchorHasLimeBorder)
            {
                throw new InvalidOperationException("The GDI fill-edge overlay did not draw an inactive segment anchor handle.");
            }
        }
        stage.ClearFillEdgeBezierOverlay();
        stage.RestoreViewState(fillEdgeViewState);

        stage.SetSelection([editableText], editableText);
        var textAreaRightWorld = stage.GetTextAreaHandleWorldPoint(editableText, EditHandleKind.TextAreaRight);
        var textAreaRightScreen = Point.Round(stage.WorldToScreen(textAreaRightWorld.X, textAreaRightWorld.Y));
        if (!stage.TextAreaOverlayVisible(editableText)
            || !stage.TextAreaResizeHandlesVisible(editableText)
            || stage.HitTestHandle(textAreaRightScreen, editableText) != EditHandleKind.TextAreaRight)
        {
            throw new InvalidOperationException("The renderer Stage did not expose the selected text area's resize overlay.");
        }
        stage.Invalidate();
        stage.Update();
        Application.DoEvents();
        if (!stage.LastFrameUsedDirect2D)
        {
            throw new InvalidOperationException("Direct2D did not render the selected text-area overlay.");
        }

        using (var textAreaBitmap = new Bitmap(stage.ClientSize.Width, stage.ClientSize.Height))
        using (var textAreaGraphics = Graphics.FromImage(textAreaBitmap))
        {
            var drawGdi = typeof(StageControl).GetMethod(
                "DrawGdi",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("The GDI text-area overlay entry point could not be located.");
            drawGdi.Invoke(stage, [textAreaGraphics]);
            var foundGreenHandlePixel = false;
            for (var y = Math.Max(0, textAreaRightScreen.Y - 4); y <= Math.Min(textAreaBitmap.Height - 1, textAreaRightScreen.Y + 4); y++)
            {
                for (var x = Math.Max(0, textAreaRightScreen.X - 4); x <= Math.Min(textAreaBitmap.Width - 1, textAreaRightScreen.X + 4); x++)
                {
                    var pixel = textAreaBitmap.GetPixel(x, y);
                    if (pixel.G >= 140 && pixel.B >= 120 && pixel.R <= 130)
                    {
                        foundGreenHandlePixel = true;
                        break;
                    }
                }
                if (foundGreenHandlePixel) break;
            }
            if (!foundGreenHandlePixel)
            {
                throw new InvalidOperationException("The GDI fallback did not draw the green text-area resize handle.");
            }
        }

        var selectedLine = editable.AddLineSegment(
            0,
            new PointF(-120, -60),
            new PointF(80, 40),
            VectorUnits.StrokePointsToUnits(3),
            Color.Transparent,
            Color.Gold,
            8);
        stage.SetSelection([selectedLine], selectedLine);
        if (!stage.SelectionHighlightAnimating)
        {
            throw new InvalidOperationException("Selecting a line did not retain the highlight animation.");
        }

        stage.SetSelection([], -1);
        if (stage.SelectionHighlightAnimating)
        {
            throw new InvalidOperationException("Clearing the Stage selection did not stop the highlight animation.");
        }
        if (editable.RemoveObjects([selectedLine]) != 1)
        {
            throw new InvalidOperationException("Selection animation setup did not restore the renderer regression scene.");
        }

        var tiledUnderlay = new VectorScene();
        tiledUnderlay.CreateEmpty();
        const int tiledUnderlayObjectCount = 56_000;
        for (var index = 0; index < tiledUnderlayObjectCount; index++)
        {
            var x = (index % 1000) * 2 - 1000;
            var y = (index / 1000) * 2 - 56;
            tiledUnderlay.AppendObject(
                0,
                new PointF(x, y),
                new SizeF(18, 18),
                0,
                0,
                Color.Coral,
                Color.Transparent,
                3,
                ShapeKind.Rectangle);
        }
        tiledUnderlay.CompleteDeferredBuild();

        stage.BindUnderlayScene(tiledUnderlay);
        stage.ZoomAt(new Point(stage.Width / 2, stage.Height / 2), 0.1f);
        stage.Update();
        Application.DoEvents();

        if (!stage.LastFrameUsedDirect2D
            || !stage.LastStats.TileLod
            || stage.LastStats.TileDraws <= 0
            || stage.LastStats.VisibleObjects != 2
            || stage.LastStats.DrawnObjects != 2)
        {
            throw new InvalidOperationException("Mixed object and tile LOD rendering produced inconsistent statistics.");
        }
        var mixedLodTileDraws = stage.LastStats.TileDraws;

        if (stage.HasCachedDirect2DFreehandGeometry(underlay))
        {
            throw new InvalidOperationException("Switching the underlay retained stale Direct2D freehand geometry.");
        }

        stage.ZoomAt(new Point(stage.Width / 2, stage.Height / 2), 50f);
        stage.Update();
        Application.DoEvents();
        var expectedObjectDraws = tiledUnderlayObjectCount + editable.ObjectCount;
        if (stage.LastStats.TileLod
            || stage.LastStats.VisibleObjects != expectedObjectDraws
            || stage.LastStats.DrawnObjects != expectedObjectDraws)
        {
            throw new InvalidOperationException("The shared object budget did not return unused editable capacity to the underlay.");
        }

        var stressScene = new VectorScene();
        stressScene.Generate(1000, 100000, 100000000);
        stage.BindScene(stressScene);
        stage.Fit();
        stage.Update();
        Application.DoEvents();
        if (!stage.LastFrameUsedDirect2D
            || !stage.LastStats.TileLod
            || stage.LastStats.TileDraws <= 0
            || stage.LastStats.DrawnObjects != 0
            || stage.LastDirect2DLodBitmapSubmissions != 1)
        {
            throw new InvalidOperationException("Fit Stage did not submit the stress scene as one cached LOD bitmap.");
        }

        var fillEdgeDragSummaryRevision = stressScene.SummaryRevision;
        stage.SetFillEdgeBezierPointerEditing(true);
        stage.Update();
        Application.DoEvents();
        if (!stage.LastFrameUsedDirect2D
            || stage.LastStats.TileLod
            || stage.LastDirect2DLodBitmapSubmissions != 0
            || stressScene.SummaryRevision != fillEdgeDragSummaryRevision)
        {
            throw new InvalidOperationException("Active fill-edge dragging reused a stale LOD summary instead of current object geometry.");
        }
        stage.SetFillEdgeBezierPointerEditing(false);
        stage.Update();
        Application.DoEvents();
        if (!stage.LastFrameUsedDirect2D || !stage.LastStats.TileLod)
        {
            throw new InvalidOperationException("The Stage did not restore normal LOD rendering after fill-edge dragging ended.");
        }

        const int renderSamples = 24;
        for (var warmup = 0; warmup < 4; warmup++)
        {
            stage.Invalidate();
            stage.Update();
        }
        var completedFrames = 0;
        EventHandler rendered = (_, _) => completedFrames++;
        stage.FrameRendered += rendered;
        var renderWatch = Stopwatch.StartNew();
        double commandMilliseconds = 0;
        double presentMilliseconds = 0;
        var lodBitmapBuilds = 0;
        for (var sample = 0; sample < renderSamples; sample++)
        {
            stage.Invalidate();
            stage.Update();
            commandMilliseconds += stage.LastDirect2DCommandMilliseconds;
            presentMilliseconds += stage.LastDirect2DPresentMilliseconds;
            lodBitmapBuilds += stage.LastDirect2DLodBitmapBuilds;
        }
        renderWatch.Stop();
        stage.FrameRendered -= rendered;
        if (completedFrames != renderSamples
            || !stage.LastFrameUsedDirect2D
            || !stage.LastStats.TileLod
            || stage.LastDirect2DLodBitmapSubmissions != 1
            || lodBitmapBuilds != 0)
        {
            throw new InvalidOperationException("The repeated stress-scene render sample did not complete through the Direct2D LOD path.");
        }
        var fitStageRenderFps = renderSamples / renderWatch.Elapsed.TotalSeconds;
        var averageCommandMilliseconds = commandMilliseconds / renderSamples;
        var commandBudgetMet = averageCommandMilliseconds <= RenderCollectBudgetMilliseconds;

        form.Close();
        Console.WriteLine("underlay_direct2d=ok");
        Console.WriteLine("onion_skin_below_drawing_objects=ok");
        Console.WriteLine("onion_skin_below_drawing_objects_gdi=ok");
        Console.WriteLine("layer_outline_gdi_direct2d=ok");
        Console.WriteLine("gpu_hardware_target=ok");
        Console.WriteLine("gpu_immediate_present=ok");
        Console.WriteLine($"polar_grid_zoomed_direct2d_command_ms={polarDirect2DCommandMilliseconds:0.000}");
        Console.WriteLine($"polar_grid_zoomed_direct2d_frame_ms={polarDirect2DFrameMilliseconds:0.000}");
        Console.WriteLine($"polar_grid_zoomed_gdi_frame_ms={polarGdiFrameMilliseconds:0.000}");
        Console.WriteLine("polar_grid_zoomed_budget_met=true");
        Console.WriteLine($"visible_objects={layeredVisibleObjects}");
        Console.WriteLine($"mixed_lod_tiles={mixedLodTileDraws}");
        Console.WriteLine("freehand_cache_switch=ok");
        Console.WriteLine($"shared_object_draws={expectedObjectDraws}");
        Console.WriteLine($"fit_stage_lod_tiles={stage.LastStats.TileDraws}");
        Console.WriteLine($"fit_stage_lod_bitmap_submissions={stage.LastDirect2DLodBitmapSubmissions}");
        Console.WriteLine($"fit_stage_lod_bitmap_rebuilds={lodBitmapBuilds}");
        Console.WriteLine($"fit_stage_offscreen_present_fps={fitStageRenderFps:0.0}");
        Console.WriteLine($"fit_stage_command_avg_ms={averageCommandMilliseconds:0.000}");
        Console.WriteLine($"fit_stage_command_budget_ms={RenderCollectBudgetMilliseconds:0.000}");
        Console.WriteLine($"fit_stage_command_budget_met={commandBudgetMet.ToString().ToLowerInvariant()}");
        Console.WriteLine($"fit_stage_command_capacity_fps={1000.0 / averageCommandMilliseconds:0.0}");
        Console.WriteLine($"fit_stage_present_avg_ms={presentMilliseconds / renderSamples:0.000}");
    }

    private static void RunSelectionHighlightStyleRegression()
    {
        const float pulse = 0.5f;
        var fillLine = StageControl.SelectionLineColor(SelectionHighlightKind.Fill, primary: true);
        var strokeLine = StageControl.SelectionLineColor(SelectionHighlightKind.Stroke, primary: true);
        var fillGlow = StageControl.SelectionGlowColor(SelectionHighlightKind.Fill, primary: true, pulse);
        var strokeGlow = StageControl.SelectionGlowColor(SelectionHighlightKind.Stroke, primary: true, pulse);
        var fillWidth = StageControl.SelectionLineWidth(SelectionHighlightKind.Fill, primary: true, pulse);
        var strokeWidth = StageControl.SelectionLineWidth(SelectionHighlightKind.Stroke, primary: true, pulse);
        AssertTimeline(
            StageControl.SelectionHighlightForShape(ShapeKind.Rectangle) == SelectionHighlightKind.Fill
            && StageControl.SelectionHighlightForShape(ShapeKind.Path) == SelectionHighlightKind.Fill
            && StageControl.SelectionHighlightForShape(ShapeKind.BrushStroke) == SelectionHighlightKind.Fill
            && StageControl.SelectionHighlightForShape(ShapeKind.Line) == SelectionHighlightKind.Stroke
            && StageControl.SelectionHighlightForShape(ShapeKind.Freeform) == SelectionHighlightKind.Stroke
            && fillLine.ToArgb() != strokeLine.ToArgb()
            && fillGlow.B > fillGlow.R
            && strokeGlow.R > strokeGlow.B
            && strokeWidth > fillWidth
            && StageControl.SelectionOuterGlowWidth(SelectionHighlightKind.Fill, primary: true, pulse: 1)
                > StageControl.SelectionOuterGlowWidth(SelectionHighlightKind.Fill, primary: true, pulse: 0)
            && StageControl.SelectionOuterGlowWidth(SelectionHighlightKind.Stroke, primary: true, pulse: 1)
                > StageControl.SelectionOuterGlowWidth(SelectionHighlightKind.Stroke, primary: true, pulse: 0),
            "Fill and stroke selection highlights did not retain distinct cool/thin and warm/strong styles.");
        Console.WriteLine("selection_highlight_style_regression=ok");
    }

    private static void RunMarqueeToolPolicyRegression()
    {
        var supportedTools = Enum.GetValues<ToolMode>()
            .Where(MainForm.SupportsMarqueeSelection)
            .ToHashSet();
        AssertTimeline(
            supportedTools.SetEquals([ToolMode.Select, ToolMode.Transform])
            && MainForm.ShouldShowFillEdgeBezierOverlay(ToolMode.Select, 1, hasEditableFillBoundarySelection: true)
            && !MainForm.ShouldShowFillEdgeBezierOverlay(ToolMode.Select, 2, hasEditableFillBoundarySelection: true)
            && !MainForm.ShouldShowFillEdgeBezierOverlay(ToolMode.Select, 1, hasEditableFillBoundarySelection: false)
            && !MainForm.ShouldShowFillEdgeBezierOverlay(ToolMode.Transform, 1, hasEditableFillBoundarySelection: true)
            && !ShortcutProfiles.Commands.Any(command =>
                string.Equals(command.Id, "tool.fill-edge-bezier", StringComparison.Ordinal))
            && MainForm.ShouldBeginTransformMarquee(TransformHandleKind.None, hasSelectableTarget: false)
            && !MainForm.ShouldBeginTransformMarquee(TransformHandleKind.None, hasSelectableTarget: true)
            && !MainForm.ShouldBeginTransformMarquee(TransformHandleKind.Move, hasSelectableTarget: false)
            && !MainForm.ShouldBeginTransformMarquee(TransformHandleKind.TopLeft, hasSelectableTarget: false),
            "Selection tools did not preserve marquee priority or the integrated fill-edge Bezier policy.");
    }

    private static void RunImportedSvgRasterizerRegression()
    {
        const string source = """
            <svg xmlns="http://www.w3.org/2000/svg" width="120" height="80" viewBox="0 0 120 80">
              <defs><linearGradient id="g"><stop stop-color="#ff3355"/><stop offset="1" stop-color="#2277ee" stop-opacity=".45"/></linearGradient></defs>
              <rect x="4" y="4" width="112" height="72" rx="10" fill="url(#g)"/>
            </svg>
            """;
        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "Vector2DAnimationEngine",
            $"imported-svg-regression-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            var validPath = Path.Combine(temporaryRoot, "valid.svg");
            File.WriteAllText(validPath, source);
            var droppedSvg = new DataObject(DataFormats.FileDrop, new[] { validPath });
            var droppedMultiple = new DataObject(DataFormats.FileDrop, new[] { validPath, validPath });
            var droppedNonSvg = new DataObject(DataFormats.FileDrop, new[] { Path.Combine(temporaryRoot, "valid.png") });
            AssertTimeline(
                MainForm.TryResolveDroppedSvgFile(droppedSvg, out var droppedPath)
                && droppedPath == validPath
                && !MainForm.TryResolveDroppedSvgFile(droppedMultiple, out _)
                && !MainForm.TryResolveDroppedSvgFile(droppedNonSvg, out _),
                "Stage file-drop routing did not accept exactly one SVG file.");
            var loaded = ImportedSvgRasterizer.Load(validPath);
            var raster = ImportedSvgRasterizer.Rasterize(loaded.Source, 240, 160);
            foreach (var requestedSize in new[]
                     {
                         new SizeF(15, 10),
                         new SizeF(60, 40),
                         new SizeF(300, 200),
                         new SizeF(12_000, 8_000)
                     })
            {
                var zoomRaster = ImportedSvgRasterizer.Rasterize(
                    loaded.Source,
                    requestedSize.Width,
                    requestedSize.Height);
                var aspectCrossError = Math.Abs(
                    zoomRaster.PixelWidth * (double)requestedSize.Height
                    - zoomRaster.PixelHeight * (double)requestedSize.Width);
                AssertTimeline(
                    aspectCrossError <= Math.Max(requestedSize.Width, requestedSize.Height)
                    && zoomRaster.PixelWidth <= ImportedSvgRasterizer.MaxRasterDimension
                    && zoomRaster.PixelHeight <= ImportedSvgRasterizer.MaxRasterDimension
                    && (long)zoomRaster.PixelWidth * zoomRaster.PixelHeight <= ImportedSvgRasterizer.MaxRasterPixels,
                    $"Imported SVG raster quantization changed the target aspect ratio at {requestedSize.Width}x{requestedSize.Height}: " +
                    $"{zoomRaster.PixelWidth}x{zoomRaster.PixelHeight}.");
            }
            var visiblePixels = 0;
            var visibleLeft = raster.PixelWidth;
            var visibleTop = raster.PixelHeight;
            var visibleRight = -1;
            var visibleBottom = -1;
            for (var y = 0; y < raster.PixelHeight; y++)
            {
                for (var x = 0; x < raster.PixelWidth; x++)
                {
                    var alphaOffset = y * raster.Stride + x * 4 + 3;
                    if (raster.Pixels[alphaOffset] == 0) continue;
                    visiblePixels++;
                    visibleLeft = Math.Min(visibleLeft, x);
                    visibleTop = Math.Min(visibleTop, y);
                    visibleRight = Math.Max(visibleRight, x);
                    visibleBottom = Math.Max(visibleBottom, y);
                }
            }
            var visibleWidth = Math.Max(0, visibleRight - visibleLeft + 1);
            var visibleHeight = Math.Max(0, visibleBottom - visibleTop + 1);

            var scene = new VectorScene();
            scene.CreateEmpty();
            scene.AddObject(0, PointF.Empty, new SizeF(20, 20), 0, 0, Color.Black, 3, ShapeKind.Rectangle);
            var imported = scene.AddImportedSvgObject(0, new PointF(30, 40), new SizeF(120, 80), 0.2f, loaded.Source);
            var snapshot = scene.CreateSnapshot();
            AssertTimeline(
                imported == 1
                && scene.ObjectCount == 2
                && scene.ShapeKind[imported] == ShapeKind.ImportedSvg
                && scene.TryGetImportedSvgSource(imported, out var addedSource)
                && addedSource == loaded.Source
                && visiblePixels > 0
                && visibleWidth >= raster.PixelWidth * 0.85f
                && visibleHeight >= raster.PixelHeight * 0.85f,
                $"Imported SVG load did not create one opaque object or a target-sized visible raster: " +
                $"raster={raster.PixelWidth}x{raster.PixelHeight}, visible={visibleWidth}x{visibleHeight}.");

            scene.RemoveObjectAt(0);
            AssertTimeline(
                scene.ObjectCount == 1
                && scene.ShapeKind[0] == ShapeKind.ImportedSvg
                && scene.TryGetImportedSvgSource(0, out var remappedSource)
                && remappedSource == loaded.Source,
                "Imported SVG payload did not follow object-index compaction.");
            scene.RestoreSnapshot(snapshot);
            AssertTimeline(
                scene.TryGetImportedSvgSource(imported, out var restoredSource)
                && restoredSource == loaded.Source,
                "Imported SVG payload did not survive snapshot restore.");

            var project = VectorProject.CreateEmpty();
            var drawingObject = project.DrawingObjects[0];
            drawingObject.Scene.CreateEmpty();
            drawingObject.Scene.AddImportedSvgObject(
                0,
                new PointF(20, -10),
                new SizeF(120, 80),
                0.2f,
                loaded.Source);
            AssertTimeline(
                project.TryAddSceneInstance(project.Scenes[0].Id, drawingObject.Id, PointF.Empty, 0, out var instance)
                && instance is not null,
                "Imported SVG composition setup rejected a valid instance.");
            var instanceState = instance!.EvaluateState(0);
            instance.SetStateAtFrame(0, instanceState with
            {
                ScaleX = -1.2f,
                ScaleY = 0.8f,
                SkewX = 24
            });
            var compositionScene = new VectorScene();
            var composition = SceneCompositionBuilder.Build(
                compositionScene,
                project.Scenes[0],
                project.DrawingObjects,
                0);
            var composedImported = Enumerable.Range(0, compositionScene.ObjectCount)
                .Single(index => compositionScene.ShapeKind[index] == ShapeKind.ImportedSvg);
            AssertTimeline(
                compositionScene.TryGetImportedSvgSource(composedImported, out var composedSource),
                "Imported SVG composition lost its transformed payload.");
            var transformedRaster = ImportedSvgRasterizer.Rasterize(composedSource, 240, 160);
            AssertTimeline(
                composition.ObjectOwners.Count == 1
                && composedSource != loaded.Source
                && composedSource.Contains("matrix(", StringComparison.Ordinal)
                && transformedRaster.Pixels
                    .Where((_, offset) => offset % 4 == 3)
                    .Any(alpha => alpha > 0),
                "Imported SVG composition did not preserve a reflected skew transform as visible content.");

            var tint = Color.FromArgb(255, 128, 192, 224);
            instance.SetStateAtFrame(0, instance.EvaluateState(0) with
            {
                Alpha = 0.5f,
                TintArgb = tint.ToArgb()
            });
            var appearanceScene = new VectorScene();
            SceneCompositionBuilder.Build(
                appearanceScene,
                project.Scenes[0],
                project.DrawingObjects,
                0);
            var appearanceImported = Enumerable.Range(0, appearanceScene.ObjectCount)
                .Single(index => appearanceScene.ShapeKind[index] == ShapeKind.ImportedSvg);
            AssertTimeline(
                appearanceScene.TryGetImportedSvgSource(appearanceImported, out var appearanceSource),
                "Imported SVG appearance composition lost its wrapped payload.");
            var appearanceRaster = ImportedSvgRasterizer.Rasterize(appearanceSource, 240, 160);
            var comparisonPixel = -1;
            for (var pixel = 0; pixel < transformedRaster.PixelWidth * transformedRaster.PixelHeight; pixel++)
            {
                if (transformedRaster.Pixels[pixel * 4 + 3] < 240) continue;
                comparisonPixel = pixel;
                break;
            }

            var appearanceMatches = comparisonPixel >= 0
                && appearanceRaster.PixelWidth == transformedRaster.PixelWidth
                && appearanceRaster.PixelHeight == transformedRaster.PixelHeight;
            var actualAppearance = new byte[4];
            var expectedAppearance = new int[4];
            if (appearanceMatches)
            {
                var offset = comparisonPixel * 4;
                for (var channel = 0; channel < 4; channel++) actualAppearance[channel] = appearanceRaster.Pixels[offset + channel];
                expectedAppearance[0] = (transformedRaster.Pixels[offset] * tint.B + 127) / 255;
                expectedAppearance[1] = (transformedRaster.Pixels[offset + 1] * tint.G + 127) / 255;
                expectedAppearance[2] = (transformedRaster.Pixels[offset + 2] * tint.R + 127) / 255;
                expectedAppearance[3] = transformedRaster.Pixels[offset + 3];
                appearanceMatches = Math.Abs(actualAppearance[3] - expectedAppearance[3]) <= 2
                    && Math.Abs(actualAppearance[0] - expectedAppearance[0]) <= 3
                    && Math.Abs(actualAppearance[1] - expectedAppearance[1]) <= 3
                    && Math.Abs(actualAppearance[2] - expectedAppearance[2]) <= 3;
            }
            var expectedObjectArgb = ApplyInstanceAppearanceForRegression(
                Color.FromArgb(128, 128, 128).ToArgb(),
                0.5f,
                tint.ToArgb());
            AssertTimeline(
                appearanceMatches
                && appearanceScene.Argb[appearanceImported] == expectedObjectArgb,
                "Imported SVG composition did not apply instance Alpha and multiply tint to its rendered appearance: "
                + $"pixel={comparisonPixel}, actualBGRA=[{string.Join(',', actualAppearance)}], "
                + $"expectedBGRA=[{string.Join(',', expectedAppearance)}], "
                + $"object=0x{appearanceScene.Argb[appearanceImported]:X8}, expectedObject=0x{expectedObjectArgb:X8}.");

            var dtdPath = Path.Combine(temporaryRoot, "dtd.svg");
            File.WriteAllText(
                dtdPath,
                "<!DOCTYPE svg [<!ENTITY xxe SYSTEM 'file:///does-not-exist'>]><svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'><text>&xxe;</text></svg>");
            var dtdRejected = false;
            try
            {
                _ = ImportedSvgRasterizer.Load(dtdPath);
            }
            catch (InvalidDataException)
            {
                dtdRejected = true;
            }

            var malformedPath = Path.Combine(temporaryRoot, "malformed.svg");
            File.WriteAllText(malformedPath, "<svg xmlns='http://www.w3.org/2000/svg'><path>");
            var malformedRejected = false;
            try
            {
                _ = ImportedSvgRasterizer.Load(malformedPath);
            }
            catch (InvalidDataException)
            {
                malformedRejected = true;
            }

            AssertTimeline(
                dtdRejected && malformedRejected,
                "Imported SVG validation accepted DTD or malformed XML content.");
            Console.WriteLine("imported_svg_file_drop_regression=ok");
            Console.WriteLine("imported_svg_zoom_alignment_regression=ok");
            Console.WriteLine("imported_svg_target_size_regression=ok");
            Console.WriteLine("imported_svg_rasterizer_regression=ok");
        }
        finally
        {
            ImportedSvgRasterizer.ClearCache();
            if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    private static void RunImportedSvgBreakApartRegression()
    {
        const string source = """
            <svg xmlns="http://www.w3.org/2000/svg" width="120" height="80" viewBox="0 0 120 80">
              <path fill="#d93654" fill-rule="evenodd" d="M5 5 H115 V75 H5 Z M35 25 H85 V55 H35 Z"/>
              <path d="M10 65 C35 10 85 10 110 65" fill="none" stroke="#2474c6" stroke-width="4" stroke-linecap="round" stroke-linejoin="round"/>
            </svg>
            """;
        var scene = new VectorScene();
        scene.CreateEmpty();
        var imported = scene.AddImportedSvgObject(0, new PointF(80, 40), new SizeF(240, 160), 0.2f, source);
        var original = scene.CreateSnapshot();
        var result = scene.BreakApartImportedSvgObjects([imported]);
        if (result.ProducedObjects.Length != 2)
        {
            throw new InvalidOperationException(
                $"SVG Break Apart produced unexpected parts: {string.Join(',', result.ProducedObjects.Select(index => scene.ShapeKind[index]))}.");
        }
        var fill = result.ProducedObjects.Single(index => scene.ShapeKind[index] == ShapeKind.Path);
        var stroke = result.ProducedObjects.Single(index => scene.ShapeKind[index] == ShapeKind.Freeform);
        AssertTimeline(
            result.ProducedObjects.Length == 2
            && result.Approximations == ImportedSvgBreakApproximation.None
            && !Enumerable.Range(0, scene.ObjectCount).Any(index => scene.ShapeKind[index] == ShapeKind.ImportedSvg)
            && scene.TryGetPathWorldContours(fill, out var contours)
            && contours.Length == 2
            && scene.TryGetFreehandWorldPoints(stroke, out var points)
            && points.Length > 3
            && scene.Argb[fill] == Color.FromArgb(unchecked((int)0xffd93654)).ToArgb()
            && scene.StrokeArgb[stroke] == Color.FromArgb(unchecked((int)0xff2474c6)).ToArgb()
            && scene.FillAutoMergeProtected[fill]
            && Math.Abs(scene.Stroke[stroke] - 8) <= 0.05f,
            "SVG Break Apart did not produce editable compound fill and stroke geometry.");

        scene.RestoreSnapshot(original);
        AssertTimeline(
            scene.ObjectCount == 1
            && scene.ShapeKind[0] == ShapeKind.ImportedSvg
            && scene.TryGetImportedSvgSource(0, out var restoredSource)
            && restoredSource == source,
            "Undo snapshot did not restore an opaque imported SVG after Break Apart.");

        const string rootGradientSource = """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100" viewBox="0 0 100 100">
              <linearGradient id="paint" gradientUnits="userSpaceOnUse" x1="10" y1="50" x2="90" y2="50">
                <stop offset="0%" stop-color="#ff0000"/>
                <stop offset="100%" stop-color="#0000ff"/>
              </linearGradient>
              <circle cx="50" cy="50" r="38" fill="none" stroke="url(#paint)" stroke-width="12"/>
            </svg>
            """;
        scene.CreateEmpty();
        scene.AddImportedSvgObject(0, PointF.Empty, new SizeF(100, 100), rootGradientSource);
        var gradientResult = scene.BreakApartImportedSvgObjects([0]);
        var gradientColors = gradientResult.ProducedObjects
            .Select(index => Color.FromArgb(scene.Argb[index]))
            .Where(color => color.A > 0)
            .Distinct()
            .ToArray();
        AssertTimeline(
            gradientResult.ProducedObjects.Length >= 8
            && gradientResult.Approximations == ImportedSvgBreakApproximation.RasterizedContent
            && gradientResult.ProducedObjects.All(index => scene.ShapeKind[index] == ShapeKind.Path)
            && gradientResult.ProducedObjects.All(index => scene.FillAutoMergeProtected[index])
            && gradientColors.Length >= 8
            && gradientColors.Any(color => color.R >= 204 && color.B <= 68)
            && gradientColors.Any(color => color.B >= 204 && color.R <= 68),
            "SVG Break Apart collapsed a gradient stroke to a representative solid color.");

        const string adjacentFillSource = """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="40" viewBox="0 0 100 40">
              <rect x="0" y="0" width="50" height="40" fill="#d93654"/>
              <rect x="50" y="0" width="50" height="40" fill="#d93654"/>
            </svg>
            """;
        scene.CreateEmpty();
        scene.AddImportedSvgObject(0, PointF.Empty, new SizeF(100, 40), adjacentFillSource);
        var adjacentResult = scene.BreakApartImportedSvgObjects([0]);
        var adjacentFills = adjacentResult.ProducedObjects
            .Where(index => scene.ShapeKind[index] == ShapeKind.Path)
            .ToArray();
        var protectedMergeCount = scene.ObjectCount;
        var protectedMerge = scene.MergeSameColorFillsAround(adjacentFills[0], connectNearby: true, frame: 0);
        var ordinaryFill = scene.AddPathObjectContours(
            0,
            [
                [
                    new PointF(50, -20),
                    new PointF(70, -20),
                    new PointF(70, 20),
                    new PointF(50, 20)
                ]
            ],
            0,
            Color.FromArgb(unchecked((int)0xffd93654)),
            Color.Transparent,
            4);
        var reverseMergeCount = scene.ObjectCount;
        var reverseMerge = scene.MergeSameColorFillsAround(ordinaryFill, connectNearby: true, frame: 0);
        var protectedSnapshot = scene.CreateSnapshot();
        scene.RestoreSnapshot(protectedSnapshot);
        AssertTimeline(
            adjacentFills.Length == 2
            && adjacentFills.All(index => scene.FillAutoMergeProtected[index])
            && protectedMerge == adjacentFills[0]
            && protectedMergeCount == adjacentFills.Length
            && reverseMerge == ordinaryFill
            && scene.ObjectCount == reverseMergeCount
            && protectedSnapshot.FillAutoMergeProtected.Length == protectedSnapshot.ObjectCount
            && adjacentFills.All(index => protectedSnapshot.FillAutoMergeProtected[index]),
            "SVG Break Apart fills were unexpectedly merged during later geometry or material normalization.");

        const string textSource = """
            <svg xmlns="http://www.w3.org/2000/svg" width="120" height="48" viewBox="0 0 120 48">
              <text x="6" y="36" font-family="Arial" font-size="32" fill="#3a7bd5">SVG</text>
            </svg>
            """;
        scene.CreateEmpty();
        scene.AddImportedSvgObject(0, PointF.Empty, new SizeF(120, 48), textSource);
        var textResult = scene.BreakApartImportedSvgObjects([0]);
        AssertTimeline(
            textResult.ProducedObjects.Length > 0
            && !textResult.Approximations.HasFlag(ImportedSvgBreakApproximation.RasterizedContent)
            && textResult.ProducedObjects.All(index => scene.ShapeKind[index] == ShapeKind.Path)
            && textResult.ProducedObjects.All(index => scene.TryGetPathWorldContours(index, out var glyphs) && glyphs.Length > 0),
            "SVG Break Apart did not preserve text as editable vector glyph outlines.");

        var embeddedImage = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("""
            <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24">
              <rect width="24" height="24" rx="5" fill="#f05a47"/>
              <circle cx="12" cy="12" r="6" fill="#ffd166"/>
            </svg>
            """));
        var complexSource = $"""
            <svg xmlns="http://www.w3.org/2000/svg" width="120" height="80" viewBox="0 0 120 80">
              <defs>
                <path id="tile" d="M0 0 H28 V28 H0 Z"/>
                <clipPath id="crop"><ellipse cx="60" cy="40" rx="54" ry="34"/></clipPath>
              </defs>
              <g clip-path="url(#crop)">
                <use href="#tile" x="8" y="8" fill="#4e79a7"/>
                <path d="M8 64 C36 8 84 8 112 64" fill="none" stroke="#59a14f" stroke-width="6" stroke-dasharray="9 5"/>
                <image x="72" y="18" width="36" height="36" href="data:image/svg+xml;base64,{embeddedImage}"/>
              </g>
            </svg>
            """;
        scene.CreateEmpty();
        scene.AddImportedSvgObject(0, PointF.Empty, new SizeF(240, 160), complexSource);
        var fallbackResult = scene.BreakApartImportedSvgObjects([0]);
        AssertTimeline(
            fallbackResult.ProducedObjects.Length > 0
            && fallbackResult.Approximations.HasFlag(ImportedSvgBreakApproximation.RasterizedContent)
            && !Enumerable.Range(0, scene.ObjectCount).Any(index => scene.ShapeKind[index] == ShapeKind.ImportedSvg)
            && fallbackResult.ProducedObjects.All(index => scene.ShapeKind[index] == ShapeKind.Path)
            && fallbackResult.ProducedObjects.Any(index => Color.FromArgb(scene.Argb[index]).A > 0)
            && fallbackResult.ProducedObjects.All(index => scene.TryGetPathWorldContours(index, out var regions) && regions.Length > 0),
            "Complex SVG content did not fall back to editable raster-traced compound paths.");

        const string transparentSource = """
            <svg xmlns="http://www.w3.org/2000/svg" width="40" height="20">
              <rect width="40" height="20" fill="none"/>
            </svg>
            """;
        scene.CreateEmpty();
        scene.AddImportedSvgObject(0, PointF.Empty, new SizeF(40, 20), transparentSource);
        var transparentResult = scene.BreakApartImportedSvgObjects([0]);
        AssertTimeline(
            transparentResult.ProducedObjects.Length == 1
            && transparentResult.Approximations.HasFlag(ImportedSvgBreakApproximation.RasterizedContent)
            && scene.ShapeKind[transparentResult.ProducedObjects[0]] == ShapeKind.Path
            && Color.FromArgb(scene.Argb[transparentResult.ProducedObjects[0]]).A == 0,
            "A valid transparent SVG could not complete Break Apart.");

        var project = VectorProject.CreateEmpty();
        var svgAsset = project.DrawingObjects[0];
        svgAsset.Scene.CreateEmpty();
        svgAsset.Scene.LayerOpacity[0] = 0.5f;
        svgAsset.Scene.AddImportedSvgObject(0, PointF.Empty, new SizeF(120, 80), source);
        var nestedContainer = project.AddDrawingObject("Nested opacity host");
        nestedContainer.Scene.CreateEmpty();
        nestedContainer.Scene.LayerOpacity[0] = 0.5f;
        AssertTimeline(
            project.TryAddDrawingObjectInstance(nestedContainer.Id, svgAsset.Id, PointF.Empty, out _),
            "Break Apart regression could not create its nested opacity source.");
        var container = project.AddDrawingObject("Break Apart host");
        container.Scene.CreateEmpty();
        AssertTimeline(
            project.TryAddDrawingObjectInstance(container.Id, nestedContainer.Id, new PointF(300, 180), out var instance)
            && instance is not null,
            "Break Apart regression could not create a nested SVG instance.");
        instance!.ScaleX = 1.5f;
        instance.ScaleY = 0.75f;
        instance.RotationZ = 18;

        var flattened = new VectorScene();
        SceneCompositionBuilder.BuildDrawingObjectInstanceForBreakApart(
            flattened,
            container,
            instance,
            project.DrawingObjects,
            0);
        var existing = container.Scene.AddObject(
            0,
            PointF.Empty,
            new SizeF(20, 20),
            0,
            0,
            Color.Black,
            3,
            ShapeKind.Rectangle);
        var materialized = container.Scene.AppendFlattenedSceneToLayer(flattened, 0, 0);
        AssertTimeline(
            flattened.ObjectCount == 2
            && !Enumerable.Range(0, flattened.ObjectCount).Any(index => flattened.ShapeKind[index] == ShapeKind.ImportedSvg)
            && Enumerable.Range(0, flattened.ObjectCount)
                .Where(index => flattened.ShapeKind[index] == ShapeKind.Path)
                .All(index => flattened.FillAutoMergeProtected[index])
            && materialized.Length == 2
            && materialized.All(index => container.Scene.ObjectLayer[index] == 0)
            && materialized.All(index => container.Scene.ObjectOrder[index] < container.Scene.ObjectOrder[existing])
            && materialized
                .Where(index => container.Scene.ShapeKind[index] == ShapeKind.Path)
                .All(index => container.Scene.FillAutoMergeProtected[index])
            && materialized.Any(index => ((container.Scene.Argb[index] >>> 24) & 0xff) is >= 62 and <= 64)
            && materialized.Select(container.Scene.GetObjectWorldBounds).Aggregate(RectangleF.Union).Contains(300, 180),
            "Nested-instance Break Apart did not preserve transform, opacity, host layer, or underlay stack placement.");

        var frameSafetySceneSnapshot = container.Scene.CreateSnapshot();
        var frameSafetyInstanceSnapshot = container.CreateInstanceSnapshot();
        var objectCountBeforeFrameSafety = container.Scene.ObjectCount;
        var hostTrack = container.Scene.Timeline.FindTrackByTargetId(container.Scene.LayerIds[0])
            ?? throw new InvalidOperationException("Break Apart frame-safety regression lost its host track.");
        container.Scene.Timeline.SetTrackDuration(hostTrack.Id, 8);
        AssertTimeline(
            container.Scene.MaterializeAutoKeyframeInPlace(0, 5),
            "Break Apart did not materialize an isolated current-frame host cel.");
        var currentFrameObjects = container.Scene.AppendFlattenedSceneToLayer(flattened, 0, 5);
        AssertTimeline(
            instance.SetStateAtFrame(5, instance.EvaluateState(5) with { Visible = false })
            && instance.EvaluateState(4).Visible
            && !instance.EvaluateState(5).Visible
            && currentFrameObjects.All(index => container.Scene.ObjectKeyframeFrame[index] == 5),
            "Current-frame Break Apart changed an earlier instance frame or wrote into its held source cel.");
        container.RestoreInstanceSnapshot(frameSafetyInstanceSnapshot);
        container.Scene.RestoreSnapshot(frameSafetySceneSnapshot);
        AssertTimeline(
            container.Instances.Single().EvaluateState(5).Visible
            && container.Scene.ObjectCount == objectCountBeforeFrameSafety,
            "Break Apart undo snapshots did not restore instance visibility and current-frame geometry atomically.");
        Console.WriteLine("imported_svg_break_apart_regression=ok");
        Console.WriteLine("nested_instance_break_apart_regression=ok");
    }

    private static void RunTemporaryCanvasPanRegression()
    {
        AssertTimeline(
            MainForm.CanStartTemporaryCanvasPan(
                editorFocused: false,
                stageFocused: false,
                pointerOverStage: true,
                pointerInteractionActive: false),
            "Temporary canvas pan did not activate while the pointer was over the stage.");
        AssertTimeline(
            MainForm.CanStartTemporaryCanvasPan(
                editorFocused: false,
                stageFocused: true,
                pointerOverStage: false,
                pointerInteractionActive: false),
            "Temporary canvas pan did not activate for a focused stage.");
        AssertTimeline(
            !MainForm.CanStartTemporaryCanvasPan(true, true, true, false)
            && !MainForm.CanStartTemporaryCanvasPan(false, false, false, false)
            && !MainForm.CanStartTemporaryCanvasPan(false, true, true, true),
            "Temporary canvas pan captured space from an editor or active pointer interaction.");

        var scene = new VectorScene();
        scene.CreateEmpty();
        using var stage = new StageControl(scene)
        {
            Size = new Size(640, 480)
        };
        var originBefore = stage.WorldToScreen(0, 0);
        stage.Pan(37, -19);
        var originAfter = stage.WorldToScreen(0, 0);
        AssertTimeline(
            Math.Abs(originAfter.X - originBefore.X - 37) < 0.01f
            && Math.Abs(originAfter.Y - originBefore.Y + 19) < 0.01f,
            "Stage pan did not preserve the pointer drag delta in screen space.");
    }

    private static void RunImmediateMarqueeOverlayRegression()
    {
        AssertTimeline(
            !StageControl.ShouldUseMarqueeLodPreview(4, 120, 40, 20, 0)
            && StageControl.ShouldUseMarqueeLodPreview(9, 1, 0, 0, 0)
            && StageControl.ShouldUseMarqueeLodPreview(2, 1_000, 600, 400, 0),
            "Marquee fallback LOD did not distinguish light, slow, and dense Stage frames.");

        var scene = new VectorScene();
        scene.Generate(40, 2_400, 240_000);
        using var form = new Form
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-30_000, -30_000),
            ClientSize = new Size(640, 420)
        };
        using var stage = new StageControl(scene) { Dock = DockStyle.Fill };
        form.Controls.Add(stage);
        form.Show();
        Application.DoEvents();
        stage.SetVisibleWorldWidth(1_600);
        stage.Update();
        Application.DoEvents();
        if (!stage.LastFrameUsedDirect2D || stage.LastStats.TileLod)
        {
            throw new InvalidOperationException("Marquee preview setup did not begin on the detailed Direct2D object path.");
        }

        var renderedFrames = 0;
        EventHandler rendered = (_, _) => renderedFrames++;
        stage.FrameRendered += rendered;
        stage.Capture = true;
        var overlayUpdatesBefore = stage.MarqueeOverlayUpdateCount;
        var initialWatch = Stopwatch.StartNew();
        stage.SetMarquee(new Point(40, 50), new Point(420, 300));
        initialWatch.Stop();
        var expectedTopLeft = stage.PointToScreen(new Point(40, 50));
        var expectedBounds = new Rectangle(expectedTopLeft, new Size(380, 250));
        if (!stage.MarqueeOverlayActive
            || stage.MarqueeLodPreviewActive
            || stage.MarqueeOverlayScreenBounds != expectedBounds
            || stage.MarqueeOverlayUpdateCount != overlayUpdatesBefore + 1
            || renderedFrames != 0)
        {
            throw new InvalidOperationException("The marquee did not enter the synchronous independent overlay path.");
        }

        const int samples = 24;
        var updateWatch = Stopwatch.StartNew();
        for (var sample = 0; sample < samples; sample++)
        {
            stage.SetMarquee(
                new Point(40, 50),
                new Point(420 + (sample + 1) * 3, 300 + (sample + 1) % 5));
        }
        updateWatch.Stop();
        var averageUpdateMilliseconds = updateWatch.Elapsed.TotalMilliseconds / samples;
        const double updateBudgetMilliseconds = 3;
        if (renderedFrames != 0
            || stage.MarqueeOverlayUpdateCount != overlayUpdatesBefore + samples + 1
            || averageUpdateMilliseconds > updateBudgetMilliseconds)
        {
            throw new InvalidOperationException(
                $"Marquee overlay updates were not immediate: frames={renderedFrames}, " +
                $"averageMs={averageUpdateMilliseconds:0.000}, updates={stage.MarqueeOverlayUpdateCount - overlayUpdatesBefore}.");
        }

        stage.SetSelection([0], 0);
        if (renderedFrames != 0)
        {
            throw new InvalidOperationException("A selection change forced a Stage frame while the immediate marquee overlay was active.");
        }
        stage.ClearMarquee();
        stage.Update();
        Application.DoEvents();
        stage.FrameRendered -= rendered;
        stage.Capture = false;
        if (stage.MarqueeOverlayActive
            || stage.MarqueeOverlayScreenBounds != Rectangle.Empty
            || stage.MarqueeLodPreviewActive
            || stage.LastStats.TileLod
            || renderedFrames != 1)
        {
            throw new InvalidOperationException(
                $"Completing a marquee did not remove its overlay and flush exactly one final Stage frame: frames={renderedFrames}.");
        }

        using var fallbackStage = new StageControl(scene) { Size = new Size(640, 420) };
        fallbackStage.SetMarquee(new Point(40, 50), new Point(420, 300));
        using (var gdiBitmap = new Bitmap(fallbackStage.ClientSize.Width, fallbackStage.ClientSize.Height))
        using (var gdiGraphics = Graphics.FromImage(gdiBitmap))
        {
            var drawGdi = typeof(StageControl).GetMethod(
                "DrawGdi",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("The GDI marquee fallback entry point could not be located.");
            drawGdi.Invoke(fallbackStage, [gdiGraphics]);
            if (fallbackStage.MarqueeOverlayActive
                || !fallbackStage.MarqueeLodPreviewActive
                || !fallbackStage.LastStats.TileLod)
            {
                throw new InvalidOperationException("An unavailable marquee overlay did not preserve the GDI fallback.");
            }
        }
        fallbackStage.ClearMarquee();
        form.Close();
        Console.WriteLine("marquee_immediate_overlay=ok");
        Console.WriteLine("marquee_overlay_stage_renders=0");
        Console.WriteLine($"marquee_overlay_initial_ms={initialWatch.Elapsed.TotalMilliseconds:0.000}");
        Console.WriteLine($"marquee_overlay_update_avg_ms={averageUpdateMilliseconds:0.000}");
        Console.WriteLine($"marquee_overlay_update_budget_ms={updateBudgetMilliseconds:0.000}");
        Console.WriteLine("marquee_overlay_update_budget_met=true");
        Console.WriteLine("marquee_overlay_gdi_fallback=ok");
    }

    private static void RunWorkspacePanelAnimationRegression()
    {
        var start = MainForm.ResolveWorkspacePanelAnimationValue(324, 0, 0);
        var quarter = MainForm.ResolveWorkspacePanelAnimationValue(324, 0, 0.25);
        var midpoint = MainForm.ResolveWorkspacePanelAnimationValue(324, 0, 0.5);
        var end = MainForm.ResolveWorkspacePanelAnimationValue(324, 0, 1);
        var reversedMidpoint = MainForm.ResolveWorkspacePanelAnimationValue(midpoint, 324, 0.5);
        AssertTimeline(
            start == 324
            && quarter < start
            && midpoint < quarter
            && midpoint > end
            && end == 0
            && reversedMidpoint > midpoint
            && reversedMidpoint < 324,
            "Workspace panel animation did not preserve bounded easing or mid-animation reversal.");
    }

    private static void RunWorldGridRegression()
    {
        var emptyScene = new VectorScene();
        emptyScene.CreateEmpty();
        using var defaultStage = new StageControl(emptyScene);
        using var workspaceTabs = new WorkspaceTabs();
        var defaultStageGridType = defaultStage.WorldGridType;
        var defaultTabGridType = workspaceTabs.WorldGridType;
        var gridTypeChanges = 0;
        workspaceTabs.WorldGridTypeChanged += (_, _) => gridTypeChanges++;
        workspaceTabs.WorldGridType = WorldGridType.GoldenSpiral;
        defaultStage.WorldGridType = WorldGridType.GoldenSpiral;
        using var restoredGridStage = new StageControl(emptyScene);
        restoredGridStage.RestoreViewState(defaultStage.CaptureViewState());
        const float goldenGridSnapStep = 1000;
        var goldenViewport = new RectangleF(
            -emptyScene.StageWidth * 0.5f,
            -emptyScene.StageHeight * 0.5f,
            emptyScene.StageWidth,
            emptyScene.StageHeight);
        var goldenGrid = GoldenSpiralGridLayout.Resolve(goldenViewport, goldenGridSnapStep, 1);
        var distantGoldenViewport = new RectangleF(
            goldenViewport.Left * 50,
            goldenViewport.Top * 50,
            goldenViewport.Width * 50,
            goldenViewport.Height * 50);
        var distantGoldenGrid = GoldenSpiralGridLayout.Resolve(
            distantGoldenViewport,
            goldenGridSnapStep * 10,
            500);
        var polarGrid = PolarGridLayout.Resolve(goldenViewport, goldenGridSnapStep);
        var distantPolarViewport = new RectangleF(
            1_000_000,
            800_000,
            goldenViewport.Width,
            goldenViewport.Height);
        var distantPolarGrid = PolarGridLayout.Resolve(distantPolarViewport, goldenGridSnapStep);
        var zoomedDistantPolarViewport = new RectangleF(4_999_875, 2_999_918, 250, 164);
        var zoomedDistantPolarGrid = PolarGridLayout.Resolve(
            zoomedDistantPolarViewport,
            WorldGridLayout.MinimumStepWorld);
        Span<PolarGridArc> visiblePolarArcs = stackalloc PolarGridArc[PolarGridLayout.MaximumVisibleArcsPerCircle];
        var visiblePolarArcCount = 0;
        var maximumPolarArcSegments = 0;
        foreach (var circle in zoomedDistantPolarGrid.Circles)
        {
            var arcCount = PolarGridLayout.ResolveVisibleArcs(
                zoomedDistantPolarViewport,
                circle.Radius,
                visiblePolarArcs);
            visiblePolarArcCount += arcCount;
            for (var arcIndex = 0; arcIndex < arcCount; arcIndex++)
            {
                maximumPolarArcSegments = Math.Max(
                    maximumPolarArcSegments,
                    PolarGridLayout.ResolveArcSegmentCount(
                        circle.Radius * 2.56f,
                        visiblePolarArcs[arcIndex].SweepAngle));
            }
        }
        var clippedPolarDiameterCount = zoomedDistantPolarGrid.DiameterSegments.Count(segment =>
            PolarGridLayout.TryClipSegment(zoomedDistantPolarViewport, segment, out _));
        workspaceTabs.WorldGridType = WorldGridType.Polar;
        defaultStage.WorldGridType = WorldGridType.Polar;
        using var restoredPolarStage = new StageControl(emptyScene);
        restoredPolarStage.RestoreViewState(defaultStage.CaptureViewState());
        var goldenCenter = new PointF(
            goldenGrid.Bounds.Left + goldenGrid.Bounds.Width * (1f - 1f / GoldenSpiralGridLayout.GoldenRatio),
            goldenGrid.Bounds.Top + goldenGrid.Bounds.Height / GoldenSpiralGridLayout.GoldenRatio);
        var goldenStart = goldenGrid.SpiralPoints[0];
        var outerRadius = RadiusFromCenter(goldenGrid.SpiralPoints[^1]);
        var priorQuarterRadius = RadiusFromCenter(goldenGrid.SpiralPoints[^(16 + 1)]);
        var distantOuterRadius = RadiusFromCenter(distantGoldenGrid.SpiralPoints[^1]);
        var firstRenderedRadius = RadiusFromCenter(goldenGrid.SpiralPoints[1]);
        float RadiusFromCenter(PointF point)
        {
            var dx = point.X - goldenCenter.X;
            var dy = point.Y - goldenCenter.Y;
            return MathF.Sqrt(dx * dx + dy * dy);
        }
        using var zoomGridStage = new StageControl(emptyScene) { Size = new Size(760, 520) };
        var zoomPointA = goldenGrid.SpiralPoints[160];
        var zoomPointB = goldenGrid.SpiralPoints[176];
        zoomGridStage.SetVisibleWorldWidth(emptyScene.StageWidth);
        var normalDistance = ScreenDistance(zoomPointA, zoomPointB);
        zoomGridStage.SetVisibleWorldWidth(emptyScene.StageWidth * 0.5f);
        var zoomedDistance = ScreenDistance(zoomPointA, zoomPointB);
        float ScreenDistance(PointF first, PointF second)
        {
            var firstScreen = zoomGridStage.WorldToScreen(first.X, first.Y);
            var secondScreen = zoomGridStage.WorldToScreen(second.X, second.Y);
            var dx = firstScreen.X - secondScreen.X;
            var dy = firstScreen.Y - secondScreen.Y;
            return MathF.Sqrt(dx * dx + dy * dy);
        }
        var closeScale = WorldGridLayout.Resolve(2.56f);
        var defaultScale = WorldGridLayout.Resolve(VectorUnits.PixelsPerUnit);
        var distantScale = WorldGridLayout.Resolve(VectorUnits.PixelsPerUnit * 0.02f);
        var defaultSnapStep = defaultStage.AdaptiveGridSnapStep;
        defaultStage.RestoreViewState(defaultStage.CaptureViewState() with { Zoom = 64f });
        var closeSnapStep = defaultStage.AdaptiveGridSnapStep;
        defaultStage.RestoreViewState(defaultStage.CaptureViewState() with { Zoom = 0.02f });
        var distantSnapStep = defaultStage.AdaptiveGridSnapStep;
        var beforeTransition = new WorldGridScale(100, 5.9f);
        var afterTransition = new WorldGridScale(1000, 59f);
        var carriedMajor = WorldGridLayout.ResolveLineStyle(10, beforeTransition, 1);
        var promotedMinor = WorldGridLayout.ResolveLineStyle(1, afterTransition, 1);
        var fineLine = WorldGridLayout.ResolveLineStyle(1, defaultScale, 1);
        var majorLine = WorldGridLayout.ResolveLineStyle(10, defaultScale, 1);
        if (Math.Abs(defaultStage.WorldGridOpacity - 0.1f) > 0.0001f
            || workspaceTabs.WorldGridOpacity != 10
            || defaultStageGridType != WorldGridType.Cartesian
            || defaultTabGridType != WorldGridType.Cartesian
            || workspaceTabs.WorldGridType != WorldGridType.Polar
            || restoredGridStage.WorldGridType != WorldGridType.GoldenSpiral
            || restoredPolarStage.WorldGridType != WorldGridType.Polar
            || gridTypeChanges != 2
            || polarGrid.Origin != PointF.Empty
            || polarGrid.DiameterSegments.Length != PolarGridLayout.DiameterCount
            || polarGrid.Circles.Length < 2
            || Math.Abs(polarGrid.Circles[0].Radius - goldenGridSnapStep) > 0.001f
            || Math.Abs(polarGrid.Circles[1].Radius - polarGrid.Circles[0].Radius - goldenGridSnapStep) > 0.001f
            || distantPolarGrid.Circles.Length > 256
            || distantPolarGrid.Circles.Length < 2
            || distantPolarGrid.Circles[0].Radius < 1_000_000
            || zoomedDistantPolarGrid.Circles.Length > PolarGridLayout.MaximumVisibleCircles
            || visiblePolarArcCount <= 0
            || visiblePolarArcCount > zoomedDistantPolarGrid.Circles.Length * PolarGridLayout.MaximumVisibleArcsPerCircle
            || maximumPolarArcSegments <= 0
            || maximumPolarArcSegments > PolarGridLayout.MaximumArcSegments
            || clippedPolarDiameterCount > PolarGridLayout.DiameterCount
            || goldenGrid.GuideSegments.Length != 8
            || goldenGrid.SpiralPoints.Length < 300
            || distantGoldenGrid.SpiralPoints.Length < 250
            || Math.Abs(goldenGrid.Bounds.Width / goldenGrid.Bounds.Height - GoldenSpiralGridLayout.GoldenRatio) > 0.001f
            || Math.Abs(goldenCenter.X) > 0.01f
            || Math.Abs(goldenCenter.Y) > 0.01f
            || Math.Abs(goldenStart.X) > 0.001f
            || Math.Abs(goldenStart.Y) > 0.001f
            || firstRenderedRadius > 1.001f
            || goldenGrid.Bounds.Left > goldenViewport.Left
            || goldenGrid.Bounds.Right < goldenViewport.Right
            || goldenGrid.Bounds.Top > goldenViewport.Top
            || goldenGrid.Bounds.Bottom < goldenViewport.Bottom
            || distantGoldenGrid.Bounds.Left > distantGoldenViewport.Left
            || distantGoldenGrid.Bounds.Right < distantGoldenViewport.Right
            || distantGoldenGrid.Bounds.Top > distantGoldenViewport.Top
            || distantGoldenGrid.Bounds.Bottom < distantGoldenViewport.Bottom
            || Math.Abs(goldenGrid.Bounds.Right / goldenGridSnapStep
                - MathF.Round(goldenGrid.Bounds.Right / goldenGridSnapStep)) > 0.001f
            || distantOuterRadius <= outerRadius * 25
            || Math.Abs(outerRadius / priorQuarterRadius - GoldenSpiralGridLayout.GoldenRatio) > 0.001f
            || Math.Abs(zoomedDistance / normalDistance - 2f) > 0.001f
            || closeScale.StepWorld != 10
            || defaultScale.StepWorld != 1000
            || distantScale.StepWorld != 10000
            || closeSnapStep != closeScale.StepWorld
            || defaultSnapStep != defaultScale.StepWorld
            || distantSnapStep != distantScale.StepWorld
            || Math.Abs(carriedMajor.Width - promotedMinor.Width) > 0.001f
            || carriedMajor.Color != promotedMinor.Color
            || majorLine.Width <= fineLine.Width
            || majorLine.Color.A <= fineLine.Color.A
            || WorldGridLayout.MinimumStepWorld != 10)
        {
            throw new InvalidOperationException("The world-grid modes did not preserve their defaults, shared world origin, bounded and clipped polar layout, unbounded golden-ratio layout, adaptive snapping scale, persistence, decimal hierarchy, or smooth zoom transition.");
        }
    }

    private static void RunDenseOverlapLodRegression()
    {
        const int triangleCount = 832;
        var scene = new VectorScene();
        scene.CreateEmpty();
        for (var index = 0; index < triangleCount; index++)
        {
            var offsetX = (index % 13 - 6) * 18f;
            var offsetY = ((index / 13) % 11 - 5) * 18f;
            var fill = Color.FromArgb(210, 64 + index % 96, 124 + index % 72, 194 - index % 80);
            scene.AppendObject(
                0,
                new PointF(offsetX, offsetY),
                new SizeF(2900 + index % 5 * 110, 2500 + index % 7 * 95),
                (index % 9 - 4) * 0.035f,
                VectorUnits.StrokePointsToUnits(1.5f),
                fill,
                Color.FromArgb(244, 232, 242, 250),
                6,
                ShapeKind.Triangle);
        }
        scene.CompleteDeferredBuild();

        using var form = new Form
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-30_000, -30_000),
            ClientSize = new Size(640, 420)
        };
        using var stage = new StageControl(scene) { Dock = DockStyle.Fill };
        form.Controls.Add(stage);
        form.Show();
        Application.DoEvents();
        stage.SetVisibleWorldWidth(4500);
        stage.SelectedObject = triangleCount - 1;
        stage.Invalidate();
        stage.Update();
        Application.DoEvents();

        if (!stage.LastFrameUsedDirect2D
            || !stage.LastStats.TileLod
            || stage.LastStats.TileDraws <= 0
            || stage.LastStats.DrawnObjects != 0
            || stage.LastDirect2DLodBitmapSubmissions != 1
            || stage.LastDirect2DLodDetailObjectDraws != 1)
        {
            throw new InvalidOperationException("Dense overlapping triangles did not use cached LOD while preserving the selected object's exact draw.");
        }

        const int samples = 16;
        for (var warmup = 0; warmup < 3; warmup++)
        {
            stage.Invalidate();
            stage.Update();
            Application.DoEvents();
        }

        double commandMilliseconds = 0;
        var lodBitmapBuilds = 0;
        for (var sample = 0; sample < samples; sample++)
        {
            stage.Invalidate();
            stage.Update();
            Application.DoEvents();
            commandMilliseconds += stage.LastDirect2DCommandMilliseconds;
            lodBitmapBuilds += stage.LastDirect2DLodBitmapBuilds;
            if (!stage.LastStats.TileLod
                || stage.LastDirect2DLodBitmapSubmissions != 1
                || stage.LastDirect2DLodDetailObjectDraws != 1)
            {
                throw new InvalidOperationException("Dense overlapping triangle LOD regressed during repeated rendering.");
            }
        }

        var averageCommandMilliseconds = commandMilliseconds / samples;
        var commandBudgetMet = averageCommandMilliseconds <= RenderCollectBudgetMilliseconds;
        if (lodBitmapBuilds != 0)
        {
            throw new InvalidOperationException("Dense overlapping triangle LOD rebuilt its cached bitmap during repeated rendering.");
        }

        form.Close();
        Console.WriteLine("dense_overlap_lod=ok");
        Console.WriteLine($"dense_overlap_lod_visible_objects={triangleCount}");
        Console.WriteLine($"dense_overlap_lod_command_avg_ms={averageCommandMilliseconds:0.000}");
        Console.WriteLine($"dense_overlap_lod_command_budget_ms={RenderCollectBudgetMilliseconds:0.000}");
        Console.WriteLine($"dense_overlap_lod_command_budget_met={commandBudgetMet.ToString().ToLowerInvariant()}");
    }

    private static void RunFillOverwriteRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        var older = scene.AddObject(0, PointF.Empty, new SizeF(200, 160), 0, 0, Color.Teal, Color.Transparent, 12, ShapeKind.Rectangle);
        var newer = scene.AddObject(0, new PointF(50, 0), new SizeF(200, 160), 0, 0, Color.Coral, Color.Transparent, 12, ShapeKind.Rectangle);
        var overwritten = scene.ApplyFillOverwriteToNewObjects([newer]);
        if (overwritten.Length != 1
            || !scene.FillContainsPoint(overwritten[0], new PointF(50, 0))
            || !scene.FillContainsPoint(overwritten[0], new PointF(120, 0)))
        {
            throw new InvalidOperationException("The newest fill was not retained after overpaint resolution.");
        }

        var olderRemainder = Enumerable.Range(0, scene.ObjectCount)
            .FirstOrDefault(index => index != overwritten[0] && scene.Argb[index] == Color.Teal.ToArgb());
        if (olderRemainder < 0
            || scene.ShapeKind[olderRemainder] != ShapeKind.Path
            || !scene.FillContainsPoint(olderRemainder, new PointF(-80, 0))
            || scene.FillContainsPoint(olderRemainder, new PointF(50, 0)))
        {
            throw new InvalidOperationException("A later fill did not subtract its overlap from the older fill.");
        }

        var movedScene = new VectorScene();
        movedScene.CreateEmpty();
        var stationary = movedScene.AddObject(0, PointF.Empty, new SizeF(200, 160), 0, 0, Color.Teal, Color.Transparent, 12, ShapeKind.Rectangle);
        var moved = movedScene.AddObject(0, new PointF(-220, 0), new SizeF(120, 160), 0, 0, Color.Coral, Color.Transparent, 12, ShapeKind.Rectangle);
        movedScene.TransformObjects([moved], point => new PointF(point.X + 220, point.Y));
        var movedResult = movedScene.ApplyFillOverwriteToNewObjects([moved]);
        if (movedResult.Length != 1
            || !movedScene.FillContainsPoint(movedResult[0], PointF.Empty)
            || Enumerable.Range(0, movedScene.ObjectCount)
                .Any(index => index != movedResult[0] && movedScene.FillContainsPoint(index, PointF.Empty))
            || !Enumerable.Range(0, movedScene.ObjectCount)
                .Any(index => index != movedResult[0]
                    && movedScene.Argb[index] == Color.Teal.ToArgb()
                    && movedScene.FillContainsPoint(index, new PointF(-80, 0))))
        {
            throw new InvalidOperationException("Moving a fill did not overwrite the stationary fill in its new overlap.");
        }

        var alphaScene = new VectorScene();
        alphaScene.CreateEmpty();
        var translucentOld = Color.FromArgb(96, 68, 172, 196);
        var translucentNew = Color.FromArgb(210, 68, 172, 196);
        alphaScene.AddObject(0, PointF.Empty, new SizeF(160, 120), 0, 0, translucentOld, Color.Transparent, 12, ShapeKind.Rectangle);
        var alphaOverpaint = alphaScene.AddObject(0, new PointF(35, 0), new SizeF(160, 120), 0, 0, translucentNew, Color.Transparent, 12, ShapeKind.Rectangle);
        var alphaNew = alphaScene.ApplyFillOverwriteToNewObjects([alphaOverpaint]);
        if (alphaNew.Length != 1
            || Enumerable.Range(0, alphaScene.ObjectCount)
                .Any(index => index != alphaNew[0]
                    && alphaScene.Argb[index] == translucentOld.ToArgb()
                    && alphaScene.FillContainsPoint(index, new PointF(35, 0))))
        {
            throw new InvalidOperationException("Fill overwrite treated different alpha values as the same fill material.");
        }

        var gradientScene = new VectorScene();
        gradientScene.CreateEmpty();
        var gradient = gradientScene.AddObject(0, PointF.Empty, new SizeF(220, 160), 0, 0, Color.Teal, Color.Transparent, 12, ShapeKind.Rectangle);
        var gradientStops = new[] { new GradientStop(0, Color.Teal), new GradientStop(0.45f, Color.Gold), new GradientStop(1, Color.MediumPurple) };
        var gradientStart = new PointF(-110, 0);
        var gradientEnd = new PointF(110, 0);
        gradientScene.SetGradientPaint(gradient, GradientKind.Linear, gradientStops, gradientStart, gradientEnd);
        var gradientOverpaint = gradientScene.AddObject(0, new PointF(55, 0), new SizeF(180, 160), 0, 0, Color.Coral, Color.Transparent, 12, ShapeKind.Rectangle);
        var gradientNew = gradientScene.ApplyFillOverwriteToNewObjects([gradientOverpaint]);
        var gradientRemainder = Enumerable.Range(0, gradientScene.ObjectCount)
            .FirstOrDefault(index => index != gradientNew[0] && gradientScene.Argb[index] == Color.Teal.ToArgb());
        if (gradientNew.Length != 1
            || gradientRemainder < 0
            || !gradientScene.HasGradient(gradientRemainder)
            || gradientScene.GetGradientKind(gradientRemainder) != GradientKind.Linear
            || gradientScene.GetGradientStart(gradientRemainder) != gradientStart
            || gradientScene.GetGradientEnd(gradientRemainder) != gradientEnd
            || !gradientScene.GetGradientStops(gradientRemainder).SequenceEqual(gradientStops)
            || gradientScene.FillContainsPoint(gradientRemainder, new PointF(55, 0)))
        {
            throw new InvalidOperationException("Fill overwrite did not retain the remaining gradient's material or geometry.");
        }

        var gradientCoverageScene = new VectorScene();
        gradientCoverageScene.CreateEmpty();
        var gradientCoverageStops = new[]
        {
            new GradientStop(0, Color.Teal),
            new GradientStop(0.5f, Color.Gold),
            new GradientStop(1, Color.MediumPurple)
        };
        var gradientCoverageStart = new PointF(-90, 0);
        var gradientCoverageEnd = new PointF(90, 0);
        var olderGradient = gradientCoverageScene.AddObject(0, PointF.Empty, new SizeF(180, 120), 0, 0, Color.Teal, Color.Transparent, 12, ShapeKind.Rectangle);
        gradientCoverageScene.SetGradientPaint(olderGradient, GradientKind.Linear, gradientCoverageStops, gradientCoverageStart, gradientCoverageEnd);
        var newerGradient = gradientCoverageScene.AddObject(0, PointF.Empty, new SizeF(180, 120), 0, 0, Color.Teal, Color.Transparent, 12, ShapeKind.Rectangle);
        gradientCoverageScene.SetGradientPaint(newerGradient, GradientKind.Linear, gradientCoverageStops, gradientCoverageStart, gradientCoverageEnd);
        var retainedGradient = gradientCoverageScene.ApplyFillOverwriteToNewObjects([newerGradient]);
        if (retainedGradient.Length != 1
            || gradientCoverageScene.ObjectCount != 1
            || !gradientCoverageScene.HasGradient(retainedGradient[0])
            || !gradientCoverageScene.GetGradientStops(retainedGradient[0]).SequenceEqual(gradientCoverageStops)
            || !gradientCoverageScene.FillContainsPoint(retainedGradient[0], PointF.Empty))
        {
            throw new InvalidOperationException("A newer gradient fill did not cover an identical older gradient fill.");
        }

        var gradientMergeScene = new VectorScene();
        gradientMergeScene.CreateEmpty();
        var firstGradient = gradientMergeScene.AddObject(0, new PointF(-45, 0), new SizeF(120, 120), 0, 0, Color.Teal, Color.Transparent, 12, ShapeKind.Rectangle);
        var secondGradient = gradientMergeScene.AddObject(0, new PointF(45, 0), new SizeF(120, 120), 0, 0, Color.Teal, Color.Transparent, 12, ShapeKind.Rectangle);
        gradientMergeScene.SetGradientPaint(firstGradient, GradientKind.Linear, gradientCoverageStops, gradientCoverageStart, gradientCoverageEnd);
        gradientMergeScene.SetGradientPaint(secondGradient, GradientKind.Linear, gradientCoverageStops, gradientCoverageStart, gradientCoverageEnd);
        var preservedGradient = gradientMergeScene.MergeSameColorFillsAround(secondGradient, frame: 0);
        if (preservedGradient != secondGradient
            || gradientMergeScene.ObjectCount != 2
            || !gradientMergeScene.HasGradient(firstGradient)
            || !gradientMergeScene.HasGradient(secondGradient))
        {
            throw new InvalidOperationException("Gradient fills participated in same-color fill merging.");
        }

        var borderedScene = new VectorScene();
        borderedScene.CreateEmpty();
        borderedScene.AddObject(0, PointF.Empty, new SizeF(160, 120), 0, VectorUnits.StrokePointsToUnits(2), Color.Teal, Color.White, 12, ShapeKind.Rectangle);
        var covering = borderedScene.AddObject(0, PointF.Empty, new SizeF(240, 180), 0, 0, Color.Coral, Color.Transparent, 12, ShapeKind.Rectangle);
        borderedScene.ApplyFillOverwriteToNewObjects([covering]);
        if (Enumerable.Range(0, borderedScene.ObjectCount).Any(index => borderedScene.ShapeKind[index] != ShapeKind.Freeform && borderedScene.Argb[index] == Color.Teal.ToArgb())
            || !Enumerable.Range(0, borderedScene.ObjectCount).Any(index => borderedScene.ShapeKind[index] == ShapeKind.Freeform
                && borderedScene.Stroke[index] > 0
                && borderedScene.StrokeArgb[index] == Color.White.ToArgb()))
        {
            throw new InvalidOperationException("Fill overwrite removed an existing shape boundary instead of preserving it as a stroke.");
        }

        var isolatedScene = new VectorScene();
        isolatedScene.CreateEmpty();
        var baseFill = isolatedScene.AddObject(0, PointF.Empty, new SizeF(160, 120), 0, 0, Color.Teal, Color.Transparent, 12, ShapeKind.Rectangle);
        var secondLayer = isolatedScene.AddLayer();
        isolatedScene.ActiveLayer = secondLayer;
        var secondLayerFill = isolatedScene.AddObject(secondLayer, PointF.Empty, new SizeF(160, 120), 0, 0, Color.Coral, Color.Transparent, 12, ShapeKind.Rectangle);
        isolatedScene.ApplyFillOverwriteToNewObjects([secondLayerFill]);
        if (!isolatedScene.FillContainsPoint(baseFill, PointF.Empty))
        {
            throw new InvalidOperationException("Fill overwrite crossed drawing-layer boundaries.");
        }

        var celScene = new VectorScene();
        celScene.CreateEmpty();
        var frameZeroFill = celScene.AddObject(0, PointF.Empty, new SizeF(160, 120), 0, 0, Color.Teal, Color.Transparent, 12, ShapeKind.Rectangle);
        if (!celScene.InsertTimelineKeyframe(0, 10)) throw new InvalidOperationException("Fill overwrite cel setup failed.");
        celScene.EditFrame = 10;
        var frameTenFill = celScene.AddObject(0, PointF.Empty, new SizeF(160, 120), 0, 0, Color.Coral, Color.Transparent, 12, ShapeKind.Rectangle);
        celScene.ApplyFillOverwriteToNewObjects([frameTenFill], 10);
        if (!celScene.FillContainsPoint(frameZeroFill, PointF.Empty))
        {
            throw new InvalidOperationException("Fill overwrite crossed keyframe boundaries.");
        }

        Console.WriteLine("fill_overwrite_regression=ok");
    }

    private static void RunClosedFillRegionRegression()
    {
        var stroke = VectorUnits.StrokePointsToUnits(2);
        var color = Color.FromArgb(79, 179, 162);
        var scene = new VectorScene();
        scene.CreateEmpty();
        scene.AddLineSegment(0, new PointF(0, 0), new PointF(200, 0), stroke, Color.Transparent, Color.White, 6);
        scene.AddLineSegment(0, new PointF(200, 0), new PointF(200, 120), stroke, Color.Transparent, Color.White, 6);
        scene.AddLineSegment(0, new PointF(200, 120), new PointF(0, 120), stroke, Color.Transparent, Color.White, 6);
        scene.AddLineSegment(0, new PointF(0, 120), new PointF(0, 0), stroke, Color.Transparent, Color.White, 6);
        scene.AddLineSegment(0, new PointF(100, 0), new PointF(100, 120), stroke, Color.Transparent, Color.White, 6);

        if (!scene.TryCreateFillFromClosedStrokeRegion(new PointF(50, 60), 0, color, out var created, out var contours)
            || created < 0
            || scene.ShapeKind[created] != ShapeKind.Path
            || scene.Argb[created] != color.ToArgb()
            || scene.Stroke[created] != 0
            || contours.Length != 1
            || !scene.FillContainsPoint(created, new PointF(50, 60))
            || scene.FillContainsPoint(created, new PointF(150, 60)))
        {
            throw new InvalidOperationException("Closed stroke fill did not create only the clicked bounded region.");
        }

        if (scene.TryCreateFillFromClosedStrokeRegion(new PointF(260, 60), 0, color, out _, out _))
        {
            throw new InvalidOperationException("Closed stroke fill created geometry outside every bounded region.");
        }

        var curvedScene = new VectorScene();
        curvedScene.CreateEmpty();
        var curvedBoundary = curvedScene.AddCubicCurveSegment(
            0,
            new PointF(0, 120),
            new PointF(-80, 90),
            new PointF(-80, 30),
            new PointF(0, 0),
            stroke,
            Color.Transparent,
            Color.White,
            12);
        curvedScene.AddLineSegment(0, new PointF(0, 0), new PointF(200, 0), stroke, Color.Transparent, Color.White, 6);
        curvedScene.AddLineSegment(0, new PointF(200, 0), new PointF(200, 120), stroke, Color.Transparent, Color.White, 6);
        curvedScene.AddLineSegment(0, new PointF(200, 120), new PointF(0, 120), stroke, Color.Transparent, Color.White, 6);
        var curvedCreated = curvedScene.TryCreateFillFromClosedStrokeRegion(
            new PointF(100, 60),
            0,
            Color.Teal,
            out var curvedFill,
            out var curvedAnimationContours);
        PathBezierNode[][] curvedFillContours = [];
        var hasCurvedBezier = curvedCreated
            && curvedFill >= 0
            && curvedScene.TryGetPathBezierWorldContours(curvedFill, out curvedFillContours);
        var curvedBezierParts = hasCurvedBezier
            ? curvedScene.GetPathBezierSegmentParts(curvedFill)
            : [];
        var curvedBoundaryLinks = curvedCreated
            ? curvedScene.CaptureFillBoundaryLineLinks(curvedBoundary, 0)
            : [];
        if (!curvedCreated
            || curvedFill < 0
            || !hasCurvedBezier
            || curvedFillContours.Length != 1
            || curvedFillContours[0].Length != 4
            || curvedBezierParts.Length != 4
            || curvedBoundaryLinks is not [{ BezierSegmentCount: 1 }]
            || curvedAnimationContours.Length != 1
            || curvedAnimationContours[0].Length <= curvedFillContours[0].Length
            || !curvedScene.FillContainsPoint(curvedFill, new PointF(100, 60)))
        {
            throw new InvalidOperationException(
                $"Closed-stroke filling flattened an adjacent cubic boundary into sampled linear Bezier anchors: created={curvedCreated}/{curvedFill}, exact={hasCurvedBezier}, contours={curvedFillContours.Length}, nodes={(curvedFillContours.Length > 0 ? curvedFillContours[0].Length : 0)}, parts={curvedBezierParts.Length}, links={string.Join(',', curvedBoundaryLinks.Select(link => $"{link.BezierSegmentIndex}+{link.BezierSegmentCount}"))}, animation={(curvedAnimationContours.Length > 0 ? curvedAnimationContours[0].Length : 0)}.");
        }

        var shallowCurveScene = new VectorScene();
        shallowCurveScene.CreateEmpty();
        shallowCurveScene.AddLineSegment(0, new PointF(0, 0), new PointF(240, 0), stroke, Color.Transparent, Color.White, 6);
        shallowCurveScene.AddLineSegment(0, new PointF(240, 0), new PointF(240, 140), stroke, Color.Transparent, Color.White, 6);
        var shallowBoundary = shallowCurveScene.AddCubicCurveSegment(
            0,
            new PointF(240, 140),
            new PointF(165, 145),
            new PointF(75, 75),
            new PointF(0, 140),
            stroke,
            Color.Transparent,
            Color.White,
            12);
        shallowCurveScene.AddLineSegment(0, new PointF(0, 140), new PointF(0, 0), stroke, Color.Transparent, Color.White, 6);
        if (!shallowCurveScene.TryCreateFillFromClosedStrokeRegion(
                new PointF(120, 60),
                0,
                Color.Teal,
                out var shallowFill,
                out _)
            || !shallowCurveScene.TryGetPathBezierWorldContours(shallowFill, out var shallowContours)
            || shallowContours is not [{ Length: 4 }])
        {
            throw new InvalidOperationException("A shallow cubic closed-stroke boundary was flattened into sampled Fill anchors.");
        }

        var shallowOverlayPieces = shallowCurveScene.GetExposedFillBezierSegmentPieces(
            shallowFill,
            0,
            includeCoincidentStrokes: true);
        if (shallowOverlayPieces.Length != 4)
        {
            throw new InvalidOperationException(
                $"A coincident cubic Line split the Fill edit overlay into sampled handles: pieces={shallowOverlayPieces.Length}.");
        }

        var shallowLineLinks = shallowCurveScene.CaptureFillBoundaryLineLinks(shallowBoundary, 0);
        if (shallowLineLinks is not [{ BezierSegmentCount: 1 }])
        {
            throw new InvalidOperationException("A shallow cubic Fill boundary did not retain its editable Line link.");
        }

        var fillSegmentIndex = shallowLineLinks[0].BezierSegmentIndex;
        var fillStrokeLinks = shallowCurveScene.CaptureFillBoundaryStrokeLinks(shallowFill, fillSegmentIndex, 0);
        if (!shallowCurveScene.TryGetPathBezierSegment(shallowFill, fillSegmentIndex, out var shallowFillSegment))
        {
            throw new InvalidOperationException("The shallow cubic Fill boundary segment could not be resolved.");
        }
        if (fillStrokeLinks.Length != 1)
        {
            throw new InvalidOperationException("A shallow cubic Fill boundary did not retain its editable Line link.");
        }

        var adjustedControl1 = new PointF(shallowFillSegment.Control1.X, shallowFillSegment.Control1.Y - 20);
        var adjustedControl2 = new PointF(shallowFillSegment.Control2.X, shallowFillSegment.Control2.Y - 20);
        if (!shallowCurveScene.SetPathBezierSegment(
                shallowFill,
                fillSegmentIndex,
                shallowFillSegment.Start,
                adjustedControl1,
                adjustedControl2,
                shallowFillSegment.End,
                rebuildGeometryIndex: false)
            || !shallowCurveScene.UpdateFillBoundaryStrokeLinks(
                fillStrokeLinks,
                shallowFillSegment.Start,
                adjustedControl1,
                adjustedControl2,
                shallowFillSegment.End)
            || shallowCurveScene.CaptureFillBoundaryLineLinks(shallowBoundary, 0) is not [{ BezierSegmentCount: 1 }])
        {
            throw new InvalidOperationException("Editing a shallow cubic Fill boundary separated it from the adjacent Line.");
        }

        var legacyCurveScene = new VectorScene();
        legacyCurveScene.CreateEmpty();
        legacyCurveScene.AddLineSegment(0, new PointF(0, 0), new PointF(240, 0), stroke, Color.Transparent, Color.White, 6);
        legacyCurveScene.AddLineSegment(0, new PointF(240, 0), new PointF(240, 140), stroke, Color.Transparent, Color.White, 6);
        var legacyBoundary = legacyCurveScene.AddCubicCurveSegment(
            0,
            new PointF(240, 140),
            new PointF(165, 145),
            new PointF(75, 75),
            new PointF(0, 140),
            stroke,
            Color.Transparent,
            Color.White,
            12);
        legacyCurveScene.AddLineSegment(0, new PointF(0, 140), new PointF(0, 0), stroke, Color.Transparent, Color.White, 6);
        if (!legacyCurveScene.TryGetClosedStrokeFillRegion(new PointF(120, 60), 0, out var legacyContours))
        {
            throw new InvalidOperationException("The legacy cubic Fill regression could not resolve its closed region.");
        }

        var legacyFill = legacyCurveScene.AddPathObjectContours(
            0,
            legacyContours,
            0,
            Color.Teal,
            Color.Transparent,
            (uint)legacyContours.Sum(contour => contour.Length));
        var legacyPartsBefore = legacyContours.Sum(contour => contour.Length);
        var legacyCanonicalized = legacyCurveScene.CanonicalizeFlattenedFillBoundaryCurves(legacyFill, 0);
        var legacyPartsAfter = legacyCurveScene.GetPathBezierSegmentParts(legacyFill);
        var legacyLinksAfter = legacyCurveScene.CaptureFillBoundaryLineLinks(legacyBoundary, 0);
        var legacyOverlayAfter = legacyCurveScene.GetExposedFillBezierSegmentPieces(
            legacyFill,
            0,
            includeCoincidentStrokes: true);
        if (legacyPartsBefore <= 4
            || !legacyCanonicalized
            || legacyPartsAfter.Length != 4
            || legacyLinksAfter is not [{ BezierSegmentCount: 1 }]
            || legacyOverlayAfter.Length != 4)
        {
            throw new InvalidOperationException(
                $"A sampled legacy Fill boundary was not restored to its neighboring cubic Line: before={legacyPartsBefore}, canonicalized={legacyCanonicalized}, after={legacyPartsAfter.Length}, links={string.Join(',', legacyLinksAfter.Select(link => $"{link.BezierSegmentIndex}+{link.BezierSegmentCount}"))}, overlay={legacyOverlayAfter.Length}.");
        }

        var mergedCurveScene = new VectorScene();
        mergedCurveScene.CreateEmpty();
        mergedCurveScene.AddObject(
            0,
            new PointF(160, -5),
            new SizeF(240, 70),
            0,
            0,
            Color.Teal,
            Color.Transparent,
            12,
            ShapeKind.Rectangle);
        mergedCurveScene.AddLineSegment(0, new PointF(0, 0), new PointF(240, 0), stroke, Color.Transparent, Color.White, 6);
        mergedCurveScene.AddLineSegment(0, new PointF(240, 0), new PointF(240, 140), stroke, Color.Transparent, Color.White, 6);
        var mergedBoundary = mergedCurveScene.AddCubicCurveSegment(
            0,
            new PointF(240, 140),
            new PointF(165, 145),
            new PointF(75, 75),
            new PointF(0, 140),
            stroke,
            Color.Transparent,
            Color.White,
            12);
        mergedCurveScene.AddLineSegment(0, new PointF(0, 140), new PointF(0, 0), stroke, Color.Transparent, Color.White, 6);
        if (!mergedCurveScene.TryCreateFillFromClosedStrokeRegion(
                new PointF(120, 80),
                0,
                Color.Teal,
                out var mergedCurveFill,
                out _))
        {
            throw new InvalidOperationException("The shallow cubic merge regression could not create its Fill region.");
        }

        var normalizedCurveFills = mergedCurveScene.NormalizePaintForInteractiveCommit([mergedCurveFill], frame: 0);
        var normalizedCurveFill = normalizedCurveFills.FirstOrDefault(index =>
            (uint)index < mergedCurveScene.ObjectCount
            && mergedCurveScene.FillContainsPoint(index, new PointF(120, 80)), -1);
        var normalizedCurveParts = normalizedCurveFill >= 0
            ? mergedCurveScene.GetPathBezierSegmentParts(normalizedCurveFill)
            : [];
        var normalizedCurveLinks = mergedCurveScene.CaptureFillBoundaryLineLinks(mergedBoundary, 0);
        if (normalizedCurveFill < 0
            || normalizedCurveParts.Length > 10
            || normalizedCurveLinks is not [{ BezierSegmentCount: 1 }])
        {
            throw new InvalidOperationException(
                $"Interactive Fill normalization flattened a neighboring shallow cubic: fill={normalizedCurveFill}, parts={normalizedCurveParts.Length}, links={string.Join(',', normalizedCurveLinks.Select(link => $"{link.BezierSegmentIndex}+{link.BezierSegmentCount}"))}.");
        }

        var pencilScene = new VectorScene();
        pencilScene.CreateEmpty();
        pencilScene.AddFreehandStroke(
            0,
            new[]
            {
                new PointF(0, 0),
                new PointF(100, 100),
                new PointF(0, 100),
                new PointF(100, 0)
            },
            stroke,
            Color.White,
            brushStroke: false,
            12);
        if (!pencilScene.TryCreateFillFromClosedStrokeRegion(new PointF(50, 78), 0, Color.Coral, out var pencilFill, out _)
            || pencilFill < 0
            || !pencilScene.FillContainsPoint(pencilFill, new PointF(50, 78))
            || pencilScene.FillContainsPoint(pencilFill, new PointF(50, 22)))
        {
            throw new InvalidOperationException("A self-intersecting pencil stroke did not expose an independently fillable loop.");
        }

        Console.WriteLine("closed_fill_region_regression=ok");
    }

    public static void RunFreehandStress()
    {
        RunLinkedFillBoundaryRegression();
        RunShapeToolRegression();
        RunRenderOrderRegression();
        RunDrawingTopologyRegression();
        RunTraditionalBrushContourRegression();
        RunBrushGradientRegression();
        RunComplexBrushCommitPerformanceRegression();

        const int strokeCount = 512;
        const int samplesPerStroke = 256;
        var scene = new VectorScene();
        scene.CreateEmpty();
        var raw = new PointF[samplesPerStroke];
        var retainedPoints = 0;
        var build = Stopwatch.StartNew();

        for (var stroke = 0; stroke < strokeCount; stroke++)
        {
            var baseY = -10_000 + stroke * 38f;
            for (var sample = 0; sample < samplesPerStroke; sample++)
            {
                var x = -18_000 + sample * 140f;
                var y = baseY + MathF.Sin(sample * 0.18f + stroke * 0.07f) * 90f;
                raw[sample] = new PointF(x, y);
            }

            var points = FreehandStrokeProcessor.Process(raw, 58, 12);
            retainedPoints += points.Length;
            scene.AddFreehandStroke(
                0,
                points,
                VectorUnits.StrokePointsToUnits(stroke % 2 == 0 ? 2 : 8),
                stroke % 2 == 0 ? Color.White : Color.FromArgb(79, 179, 162),
                brushStroke: stroke % 2 != 0,
                (uint)Math.Max(3, points.Length));
        }

        build.Stop();
        if (scene.ObjectCount != strokeCount) throw new InvalidOperationException("Freehand benchmark did not retain every stroke.");
        const int pencilIndex = strokeCount - 2;
        if (scene.ShapeKind[pencilIndex] != ShapeKind.Freeform
            || !scene.TryGetFreehandWorldPoints(pencilIndex, out var pencilPoints)
            || pencilPoints.Length < 2)
        {
            throw new InvalidOperationException("Pencil geometry was not retained as an open freehand path.");
        }

        var hit = scene.HitTest(pencilPoints[pencilPoints.Length / 2], 0, VectorUnits.StrokePointsToUnits(2));
        if (hit < 0) throw new InvalidOperationException("Freehand hit testing failed.");

        var snapshot = scene.CreateSnapshot();
        scene.RemoveObjectAt(100);
        if (scene.ObjectCount != strokeCount - 1
            || scene.ShapeKind[100] != ShapeKind.Path
            || scene.TryGetFreehandWorldPoints(100, out _)
            || !scene.TryGetFreehandWorldPoints(pencilIndex, out _))
        {
            throw new InvalidOperationException("Freehand compact removal failed.");
        }

        scene.RestoreSnapshot(snapshot);
        if (scene.ObjectCount != strokeCount || !scene.TryGetFreehandWorldPoints(pencilIndex, out _)) throw new InvalidOperationException("Freehand snapshot restore failed.");

        var mergeScene = new VectorScene();
        mergeScene.CreateEmpty();
        var brushColor = Color.FromArgb(79, 179, 162);
        var brushWidth = VectorUnits.StrokePointsToUnits(8);
        var firstBrush = mergeScene.AddFreehandStroke(
            0,
            new[] { new PointF(-160, 0), new PointF(160, 0) },
            brushWidth,
            brushColor,
            brushStroke: true,
            32);
        var secondBrush = mergeScene.AddFreehandStroke(
            0,
            new[] { new PointF(0, -160), new PointF(0, 160) },
            brushWidth,
            brushColor,
            brushStroke: true,
            32);
        if (firstBrush < 0 || secondBrush < 0 || mergeScene.ObjectCount != 2) throw new InvalidOperationException("Brush fill setup failed.");

        var mergedBrush = mergeScene.MergeSameColorFillsAround(secondBrush);
        if (mergeScene.ObjectCount != 1 || (uint)mergedBrush >= mergeScene.ObjectCount)
        {
            throw new InvalidOperationException($"Intersecting same-color brush fills did not merge: objects={mergeScene.ObjectCount}, result={mergedBrush}.");
        }

        if (mergeScene.ShapeKind[mergedBrush] != ShapeKind.Path
            || mergeScene.Argb[mergedBrush] != brushColor.ToArgb()
            || mergeScene.Stroke[mergedBrush] != 0)
        {
            throw new InvalidOperationException(
                $"Merged brush fill was not an unstroked path: shape={mergeScene.ShapeKind[mergedBrush]}, fill={mergeScene.Argb[mergedBrush]}, stroke={mergeScene.Stroke[mergedBrush]}.");
        }

        var nearbyScene = new VectorScene();
        nearbyScene.CreateEmpty();
        nearbyScene.AddFreehandStroke(
            0,
            new[] { new PointF(-160, 0), new PointF(160, 0) },
            brushWidth,
            brushColor,
            brushStroke: true,
            32);
        var nearbyBrush = nearbyScene.AddFreehandStroke(
            0,
            new[] { new PointF(265, 0), new PointF(585, 0) },
            brushWidth,
            brushColor,
            brushStroke: true,
            32);
        nearbyScene.MergeSameColorFillsAround(nearbyBrush);
        if (nearbyScene.ObjectCount != 1)
        {
            throw new InvalidOperationException($"Same-color brush fills separated by less than 10 vu did not merge: objects={nearbyScene.ObjectCount}.");
        }

        var foldbackScene = new VectorScene();
        foldbackScene.CreateEmpty();
        var foldbackCenterline = new[]
        {
            new PointF(0, 0),
            new PointF(240, 0),
            new PointF(20, 10),
            new PointF(240, 20),
            new PointF(0, 30)
        };
        var firstFoldback = foldbackScene.AddFreehandStroke(0, foldbackCenterline, brushWidth, brushColor, brushStroke: true, 32);
        AssertBrushCoversCenterline(foldbackScene, firstFoldback, foldbackCenterline);

        var overpaintCenterline = foldbackCenterline
            .Select(point => new PointF(point.X, point.Y + 18))
            .ToArray();
        var overpaintBrush = foldbackScene.AddFreehandStroke(0, overpaintCenterline, brushWidth, brushColor, brushStroke: true, 32);
        var mergedOverpaint = foldbackScene.MergeSameColorFillsAround(overpaintBrush);
        if (foldbackScene.ObjectCount != 1 || (uint)mergedOverpaint >= foldbackScene.ObjectCount)
        {
            throw new InvalidOperationException($"Repeated foldback brush painting did not merge: objects={foldbackScene.ObjectCount}, result={mergedOverpaint}.");
        }

        AssertBrushCoversCenterline(foldbackScene, mergedOverpaint, foldbackCenterline);
        AssertBrushCoversCenterline(foldbackScene, mergedOverpaint, overpaintCenterline);

        var crossingScene = new VectorScene();
        crossingScene.CreateEmpty();
        var crossingCenterline = new[]
        {
            new PointF(-160, -160),
            new PointF(160, 160),
            new PointF(-160, 160),
            new PointF(160, -160)
        };
        var crossingBrush = -1;
        for (var pass = 0; pass < 3; pass++)
        {
            crossingBrush = crossingScene.AddFreehandStroke(0, crossingCenterline, brushWidth, brushColor, brushStroke: true, 32);
            crossingBrush = crossingScene.MergeSameColorFillsAround(crossingBrush);
            if (crossingScene.ObjectCount != 1 || (uint)crossingBrush >= crossingScene.ObjectCount)
            {
                throw new InvalidOperationException($"Repeated crossing brush painting did not remain one fill: pass={pass}, objects={crossingScene.ObjectCount}.");
            }

            AssertBrushCoversCenterline(crossingScene, crossingBrush, crossingCenterline);
        }

        Console.WriteLine($"strokes={strokeCount}");
        Console.WriteLine($"raw_points={strokeCount * samplesPerStroke}");
        Console.WriteLine($"retained_points={retainedPoints}");
        Console.WriteLine($"brush_merge_objects={mergeScene.ObjectCount}");
        Console.WriteLine($"nearby_brush_merge_objects={nearbyScene.ObjectCount}");
        Console.WriteLine($"foldback_overpaint_objects={foldbackScene.ObjectCount}");
        Console.WriteLine($"crossing_overpaint_objects={crossingScene.ObjectCount}");
        Console.WriteLine($"build_ms={build.Elapsed.TotalMilliseconds:0.0}");
        Console.WriteLine($"avg_commit_ms={build.Elapsed.TotalMilliseconds / strokeCount:0.000}");
    }

    private static void RunRenderOrderRegression()
    {
        var outlineScene = new VectorScene();
        outlineScene.CreateEmpty();
        outlineScene.AddObject(0, PointF.Empty, new SizeF(80, 60), 0, 0, Color.Coral, 4, ShapeKind.Rectangle);
        outlineScene.SetLayerOutline(0, true);
        if (!outlineScene.HasLayerOutline
            || outlineScene.HasLayerEffects
            || !outlineScene.HasDisplayLayerEffects
            || !SceneRenderOrder.RequiresObjectRenderer(outlineScene))
        {
            throw new InvalidOperationException("Outline-only layers did not force object rendering without becoming geometry layer effects.");
        }

        var scene = new VectorScene();
        scene.CreateEmpty(2);
        var strokeWidth = VectorUnits.StrokePointsToUnits(2);
        var topLine = scene.AddLineSegment(0, new PointF(-100, 0), new PointF(100, 0), strokeWidth, Color.Transparent, Color.White, 6);
        var bottomLine = scene.AddLineSegment(1, new PointF(-100, 0), new PointF(100, 0), strokeWidth, Color.Transparent, Color.White, 6);
        var topFill = scene.AddObject(0, PointF.Empty, new SizeF(200, 120), 0, 0, Color.Teal, Color.Transparent, 12, ShapeKind.Rectangle);
        var bottomFill = scene.AddObject(1, PointF.Empty, new SizeF(200, 120), 0, 0, Color.Teal, Color.Transparent, 12, ShapeKind.Rectangle);
        var topOutlinedFill = scene.AddObject(0, PointF.Empty, new SizeF(160, 90), 0, strokeWidth, Color.Teal, Color.White, 12, ShapeKind.Rectangle);
        var topText = scene.AddTextObject(
            0,
            PointF.Empty,
            new TextObjectData(
                "T",
                TextGeometry.FallbackFontFamilyName,
                12,
                TextFontStyle.Regular,
                TextHorizontalAlignment.Center,
                new SizeF(300, 600)),
            Color.Gold);

        var renderOrder = new SceneRenderOrderBuffer();
        renderOrder.Collect(scene, new RectangleF(-500, -500, 1000, 1000), 0);
        var commands = new List<string>();
        var drawn = renderOrder.Draw(
            scene,
            int.MaxValue,
            index => commands.Add($"F{index}"),
            index => commands.Add($"S{index}"));
        var expected = new[]
        {
            $"F{bottomFill}", $"S{bottomLine}",
            $"F{topFill}", $"F{topOutlinedFill}", $"F{topText}", $"S{topLine}", $"S{topOutlinedFill}"
        };
        if (drawn != scene.ObjectCount
            || !commands.SequenceEqual(expected)
            || !SceneRenderOrder.RequiresObjectRenderer(scene)
            || !SceneRenderOrder.HasFill(ShapeKind.Text)
            || SceneRenderOrder.HasStroke(ShapeKind.Text, strokeWidth))
        {
            throw new InvalidOperationException($"Render priority order is invalid: {string.Join(',', commands)}.");
        }

        var coveredLineScene = new VectorScene();
        coveredLineScene.CreateEmpty(2);
        var coveredLine = coveredLineScene.AddLineSegment(1, new PointF(-100, 0), new PointF(100, 0), strokeWidth, Color.Transparent, Color.White, 6);
        var coveringFill = coveredLineScene.AddObject(0, PointF.Empty, new SizeF(200, 120), 0, 0, Color.Teal, Color.Transparent, 12, ShapeKind.Rectangle);
        var coveredHit = coveredLineScene.HitTestElement(PointF.Empty, 0, 0);
        if (!coveredHit.IsValid || coveredHit.Key.ObjectIndex != coveringFill || coveredHit.Key.Kind != DrawingElementKind.Fill)
        {
            throw new InvalidOperationException($"Higher-layer fill did not outrank lower-layer stroke: line={coveredLine}, hit={coveredHit.Key}.");
        }

        var sameLayerScene = new VectorScene();
        sameLayerScene.CreateEmpty();
        sameLayerScene.AddObject(0, PointF.Empty, new SizeF(200, 120), 0, 0, Color.Teal, Color.Transparent, 12, ShapeKind.Rectangle);
        var foregroundLine = sameLayerScene.AddLineSegment(0, new PointF(-100, 0), new PointF(100, 0), strokeWidth, Color.Transparent, Color.White, 6);
        var sameLayerHit = sameLayerScene.HitTestElement(PointF.Empty, 0, 0);
        if (!sameLayerHit.IsValid || sameLayerHit.Key.ObjectIndex != foregroundLine || sameLayerHit.Key.Kind != DrawingElementKind.Stroke)
        {
            throw new InvalidOperationException($"Same-layer stroke did not outrank fill: hit={sameLayerHit.Key}.");
        }

        var stackScene = new VectorScene();
        stackScene.CreateEmpty(2);
        var stackBack = stackScene.AddObject(0, PointF.Empty, new SizeF(160, 100), 0, 0, Color.Teal, Color.Transparent, 8, ShapeKind.Rectangle);
        var stackMiddle = stackScene.AddObject(0, PointF.Empty, new SizeF(140, 90), 0, 0, Color.Coral, Color.Transparent, 8, ShapeKind.Rectangle);
        var stackFront = stackScene.AddObject(0, PointF.Empty, new SizeF(120, 80), 0, 0, Color.Gold, Color.Transparent, 8, ShapeKind.Rectangle);
        var otherLayer = stackScene.AddObject(1, PointF.Empty, new SizeF(100, 70), 0, 0, Color.White, Color.Transparent, 8, ShapeKind.Rectangle);
        var otherLayerOrder = stackScene.ObjectOrder[otherLayer];
        if (stackScene.HitTestElement(PointF.Empty, 0, 0).Key.ObjectIndex != stackFront
            || stackScene.CanMoveObjectsInLayerStack([stackFront], 1, 0)
            || !stackScene.MoveObjectsInLayerStack([stackFront], -1, 0)
            || stackScene.HitTestElement(PointF.Empty, 0, 0).Key.ObjectIndex != stackMiddle
            || !stackScene.MoveObjectsInLayerStack([stackBack, stackFront], 1, 0)
            || stackScene.HitTestElement(PointF.Empty, 0, 0).Key.ObjectIndex != stackFront
            || stackScene.ObjectOrder[otherLayer] != otherLayerOrder)
        {
            throw new InvalidOperationException("Single-layer object stacking did not move the selection by one level without crossing layers.");
        }

        var edgeScene = new VectorScene();
        edgeScene.CreateEmpty();
        var edgeLine = edgeScene.AddLineSegment(0, new PointF(2, -200), new PointF(2, 20), strokeWidth, Color.Transparent, Color.White, 6);
        var edgeCurve = edgeScene.AddLineSegment(0, new PointF(-20, -100), new PointF(20, -100), strokeWidth, Color.Transparent, Color.White, 6);
        edgeScene.CurveControlX[edgeCurve] = 0;
        edgeScene.CurveControlY[edgeCurve] = 140;
        edgeScene.CurveControl2X[edgeCurve] = 0;
        edgeScene.CurveControl2Y[edgeCurve] = 140;
        var edgeRotated = edgeScene.AddObject(0, new PointF(12, 5), new SizeF(4, 20), MathF.PI / 4, 0, Color.Teal, Color.Transparent, 12, ShapeKind.Rectangle);
        var edgeOutlined = edgeScene.AddObject(0, new PointF(12, 5), new SizeF(4, 4), 0, 16, Color.Teal, Color.White, 12, ShapeKind.Rectangle);
        edgeScene.RebuildGeometryIndex();

        renderOrder.Collect(edgeScene, new RectangleF(0, 0, 5, 10), 0);
        var edgeCommands = new List<string>();
        var edgeDrawn = renderOrder.Draw(
            edgeScene,
            int.MaxValue,
            index => edgeCommands.Add($"F{index}"),
            index => edgeCommands.Add($"S{index}"));
        var expectedEdgeCommands = new[] { $"F{edgeRotated}", $"F{edgeOutlined}", $"S{edgeLine}", $"S{edgeCurve}", $"S{edgeOutlined}" };
        if (renderOrder.VisibleCount != edgeScene.ObjectCount
            || edgeDrawn != edgeScene.ObjectCount
            || !edgeCommands.SequenceEqual(expectedEdgeCommands))
        {
            throw new InvalidOperationException($"World-bounds render culling is invalid: {string.Join(',', edgeCommands)}.");
        }

        Console.WriteLine($"render_order_commands={commands.Count}");
    }

    private static void RunDrawingTopologyRegression()
    {
        RunIncrementalAppendRegression();
        RunQueryActiveFrameRegression();
        RunBrushBatchAppendRegression();
        RunLocalizedSpatialAlgorithmRegression();
        RunFillOverwriteRegression();
        RunFastSelectionProbeRegression();
        RunCrossingFillTopologyRegression();
        RunTerminatingLineTopologyRegression();
        RunTerminatingStrokeContactRegression();
        RunCommittedTerminatingCurveSplitRegression();
        RunCrossingLinesTopologyRegression();
        RunCollinearOverlapTopologyRegression();
        RunOutlinedBoundaryTopologyRegression();
        RunSmoothPathBoundaryGroupingRegression();
        RunCrossLayerTopologyRegression();
        RunMultipleCutterFillTopologyRegression();
        RunCurvedCutterFillTopologyRegression();
        RunCompoundFillTopologyRegression();
        RunPencilStrokeTopologyRegression();
        RunConnectedCutterNetworkTopologyRegression();
        RunConnectedStrokeSelectionRegression();
        RunConnectedLineRecolorRegression();
        RunMaterializedOrderTopologyRegression();
        RunBatchElementMaterializationRegression();
        RunOutlinedFillMergeRegression();
        RunFillBoundaryOverlapNormalizationRegression();
        RunMarqueeElementQueryRegression();
        RunMarqueeLineMaterializationRegression();
        RunMarqueeFillMaterializationRegression();
        RunMovedFillIsolationRegression();
        RunMarqueeOutlinedBoundarySelectionRegression();
        RunLineToFillConversionRegression();
        RunConnectedLineBranchRegression();
        RunLineSegmentMergeRegression();
        Console.WriteLine("drawing_topology_regressions=34");
    }

    private static void RunFastSelectionProbeRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        scene.AddObject(
            0,
            PointF.Empty,
            new SizeF(200, 120),
            0,
            0,
            Color.Teal,
            Color.Transparent,
            8,
            ShapeKind.Rectangle);
        scene.AddLineSegment(
            0,
            new PointF(-160, 180),
            new PointF(160, 180),
            12,
            Color.Transparent,
            Color.White,
            8);
        if (!scene.HasSelectableObjectAt(PointF.Empty, 0, 4)
            || !scene.HasSelectableObjectAt(new PointF(0, 180), 0, 4)
            || scene.HasSelectableObjectAt(new PointF(400, 400), 0, 4))
        {
            throw new InvalidOperationException("The fast selection probe did not distinguish fills, strokes, and empty canvas space.");
        }

        const int denseLineCount = 4_000;
        var denseScene = new VectorScene();
        denseScene.CreateEmpty();
        denseScene.BeginDeferredAppend(denseLineCount, [0]);
        try
        {
            for (var index = 0; index < denseLineCount; index++)
            {
                var y = 100 + index % 3;
                denseScene.AppendCubicCurveSegment(
                    0,
                    new PointF(-140, y),
                    new PointF(-45, y),
                    new PointF(45, y),
                    new PointF(140, y),
                    4,
                    Color.Transparent,
                    Color.White,
                    6);
            }
        }
        finally
        {
            denseScene.EndDeferredAppend();
        }
        denseScene.CompleteDeferredBuild();

        const int samples = 64;
        _ = denseScene.HasSelectableObjectAt(PointF.Empty, 0, 4);
        var watch = Stopwatch.StartNew();
        for (var sample = 0; sample < samples; sample++)
        {
            if (denseScene.HasSelectableObjectAt(PointF.Empty, 0, 4))
            {
                throw new InvalidOperationException("The fast selection probe reported a distant dense stroke as a hit.");
            }
        }
        watch.Stop();
        var averageMilliseconds = watch.Elapsed.TotalMilliseconds / samples;
        const double budgetMilliseconds = 5;
        if (averageMilliseconds > budgetMilliseconds)
        {
            throw new InvalidOperationException(
                $"The fast blank-canvas selection probe exceeded its budget: averageMs={averageMilliseconds:0.000}.");
        }
        Console.WriteLine($"selection_probe_avg_ms={averageMilliseconds:0.000}");
        Console.WriteLine($"selection_probe_budget_ms={budgetMilliseconds:0.000}");
        Console.WriteLine("selection_probe_budget_met=true");
    }

    private static void RunIncrementalAppendRegression()
    {
        const int objectCount = 1_024;
        var scene = new VectorScene();
        scene.CreateEmpty();
        var appendWatch = Stopwatch.StartNew();
        for (var index = 0; index < objectCount; index++)
        {
            var x = -20_000 + (index * 97 % 180) * 220;
            var y = -10_000 + (index * 53 % 80) * 220;
            scene.AddObject(0, new PointF(x, y), new SizeF(48, 48), 0, 0, Color.Teal, Color.Transparent, 6, ShapeKind.Rectangle);
        }
        appendWatch.Stop();

        var allObjects = scene.QueryObjects(new RectangleF(-24_000, -14_000, 48_000, 28_000), 0);
        var indexedObjects = 0;
        for (var cell = 0; cell < scene.IndexColumnCount * scene.IndexRowCount; cell++)
        {
            indexedObjects += scene.GetSpatialCellObjectCount(cell);
        }

        if (indexedObjects != objectCount || !allObjects.SequenceEqual(Enumerable.Range(0, objectCount)))
        {
            throw new InvalidOperationException("Incremental append produced an incomplete or unstable spatial index.");
        }

        Console.WriteLine($"incremental_append_avg_ms={appendWatch.Elapsed.TotalMilliseconds / objectCount:0.000}");
        Console.WriteLine($"incremental_append_objects={objectCount}");
    }

    private static void RunQueryActiveFrameRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty(layers: 2);
        var active = scene.AddObject(0, PointF.Empty, new SizeF(80, 80), 0, 0, Color.Teal, Color.Transparent, 6, ShapeKind.Rectangle);
        scene.AddObject(1, PointF.Empty, new SizeF(80, 80), 0, 0, Color.Coral, Color.Transparent, 6, ShapeKind.Rectangle);
        scene.SetLayerVisible(1, false);
        var bounds = new RectangleF(-100, -100, 200, 200);
        if (!scene.QueryObjects(bounds, 0).SequenceEqual([active])
            || !scene.SetLayerVisible(0, false)
            || scene.QueryObjects(bounds, 0).Length != 0
            || !scene.SetLayerVisible(0, true)
            || !scene.InsertTimelineBlankKeyframe(0, 5)
            || scene.QueryObjects(bounds, 5).Length != 0)
        {
            throw new InvalidOperationException("Spatial query did not honor layer visibility or the active keyframe exposure.");
        }

        Console.WriteLine("query_active_frame_cache=ok");
    }

    private static void RunBrushBatchAppendRegression()
    {
        var centerline = new[] { new PointF(-160, 0), PointF.Empty, new PointF(160, 0) };
        var brushShape = BrushShape.CreateSoftRound();
        var softScene = new VectorScene();
        softScene.CreateEmpty();
        var softGeometryRevision = softScene.GeometryRevision;
        var softSummaryRevision = softScene.SummaryRevision;
        var softObjects = softScene.AddSoftBrushStroke(
            0,
            centerline,
            VectorUnits.StrokePointsToUnits(16),
            Color.Coral,
            brushShape,
            32);
        if (softObjects.Length != brushShape.Layers.Count
            || softScene.GeometryRevision != softGeometryRevision + 1
            || softScene.SummaryRevision != softSummaryRevision + 1)
        {
            throw new InvalidOperationException("Soft brush layers did not share one final geometry and summary rebuild.");
        }

        var pressureScene = new VectorScene();
        pressureScene.CreateEmpty();
        var pressureGeometryRevision = pressureScene.GeometryRevision;
        var pressureSummaryRevision = pressureScene.SummaryRevision;
        var pressureObjects = pressureScene.AddPressureBrushStroke(
            0,
            [
                new PressureBrushSample(new PointF(-160, 0), 240, 0),
                new PressureBrushSample(PointF.Empty, 180, 0.2f),
                new PressureBrushSample(new PointF(160, 0), 240, 0.4f)
            ],
            VectorUnits.StrokePointsToUnits(16),
            Color.Coral,
            brushShape,
            32,
            smoothing: 58,
            simplifyTolerance: 1);
        if (pressureObjects.Length != brushShape.Layers.Count
            || pressureScene.GeometryRevision != pressureGeometryRevision + 1
            || pressureScene.SummaryRevision != pressureSummaryRevision + 1)
        {
            throw new InvalidOperationException("Pressure brush layers did not share one final geometry and summary rebuild.");
        }

        Console.WriteLine("brush_batch_append_rebuilds=ok");
    }

    private static void RunComplexBrushCommitPerformanceRegression()
    {
        const int existingStrokeCount = 64;
        const int samplesPerStroke = 96;
        var scene = new VectorScene();
        scene.CreateEmpty();
        var brushShape = BrushShape.CreateTraditionalBrush();
        var diameter = VectorUnits.StrokePointsToUnits(16);
        var stops = new[]
        {
            new GradientStop(0, Color.Teal),
            new GradientStop(0.5f, Color.Gold),
            new GradientStop(1, Color.MediumPurple)
        };
        var points = new PointF[samplesPerStroke];
        for (var stroke = 0; stroke < existingStrokeCount; stroke++)
        {
            var baseY = -1_600 + stroke * 50f;
            for (var sample = 0; sample < samplesPerStroke; sample++)
            {
                var x = -1_800 + sample * (3_600f / (samplesPerStroke - 1));
                points[sample] = new PointF(x, baseY + MathF.Sin(sample * 0.31f + stroke * 0.17f) * 34f);
            }

            var objects = scene.AddSoftBrushStroke(0, points, diameter, Color.Teal, brushShape, samplesPerStroke);
            if (objects.Length != 1) throw new InvalidOperationException("Complex brush commit setup did not create one traditional brush object.");
            scene.SetGradientPaint(objects[0], GradientKind.Linear, stops, points[0], points[^1]);
            scene.SetGradientPath(objects[0], points);
        }

        var commitPath = new PointF[256];
        for (var sample = 0; sample < commitPath.Length; sample++)
        {
            var amount = sample / (float)(commitPath.Length - 1);
            commitPath[sample] = new PointF(
                MathF.Sin(amount * MathF.Tau * 5f) * 620f,
                -1_720 + amount * 3_440f);
        }

        var watch = Stopwatch.StartNew();
        var stageWatch = Stopwatch.StartNew();
        var snapshot = scene.CreateSnapshot();
        stageWatch.Stop();
        var snapshotMilliseconds = stageWatch.Elapsed.TotalMilliseconds;
        stageWatch.Restart();
        var committed = scene.AddSoftBrushStroke(
            0,
            commitPath,
            diameter,
            Color.Coral,
            brushShape,
            (uint)commitPath.Length);
        stageWatch.Stop();
        var geometryMilliseconds = stageWatch.Elapsed.TotalMilliseconds;
        stageWatch.Restart();
        foreach (var objectIndex in committed)
        {
            scene.SetGradientPaint(objectIndex, GradientKind.Linear, stops, commitPath[0], commitPath[^1]);
            scene.SetGradientPath(objectIndex, commitPath);
        }
        stageWatch.Stop();
        var gradientMilliseconds = stageWatch.Elapsed.TotalMilliseconds;
        stageWatch.Restart();
        var normalizeInteractively = scene.CanNormalizePaintInteractively(committed, 0);
        var retained = normalizeInteractively ? scene.ApplyFillOverwriteToNewObjects(committed) : committed;
        stageWatch.Stop();
        var overwriteMilliseconds = stageWatch.Elapsed.TotalMilliseconds;
        stageWatch.Restart();
        if (normalizeInteractively) scene.MergeSameColorFillsAroundNewObjects(retained);
        stageWatch.Stop();
        var mergeMilliseconds = stageWatch.Elapsed.TotalMilliseconds;
        watch.Stop();
        GC.KeepAlive(snapshot);

        const double budgetMilliseconds = 10;
        var budgetMet = committed.Length == 1
            && retained.Length == 1
            && !normalizeInteractively
            && watch.Elapsed.TotalMilliseconds <= budgetMilliseconds;
        Console.WriteLine($"complex_brush_commit_ms={watch.Elapsed.TotalMilliseconds:0.00}");
        Console.WriteLine($"complex_brush_snapshot_ms={snapshotMilliseconds:0.00}");
        Console.WriteLine($"complex_brush_geometry_ms={geometryMilliseconds:0.00}");
        Console.WriteLine($"complex_brush_gradient_ms={gradientMilliseconds:0.00}");
        Console.WriteLine($"complex_brush_overwrite_ms={overwriteMilliseconds:0.00}");
        Console.WriteLine($"complex_brush_merge_ms={mergeMilliseconds:0.00}");
        Console.WriteLine($"complex_brush_commit_budget_ms={budgetMilliseconds:0.00}");
        Console.WriteLine($"complex_brush_commit_budget_met={budgetMet.ToString().ToLowerInvariant()}");
        if (!budgetMet)
        {
            throw new InvalidOperationException(
                $"Complex brush commit exceeded its budget or lost the committed object: elapsed={watch.Elapsed.TotalMilliseconds:0.00}, committed={committed.Length}, retained={retained.Length}.");
        }

        var solidScene = new VectorScene();
        solidScene.CreateEmpty();
        var solidPath = new PointF[512];
        for (var sample = 0; sample < solidPath.Length; sample++)
        {
            var amount = sample / (float)(solidPath.Length - 1);
            solidPath[sample] = new PointF(
                -2_400 + amount * 4_800,
                MathF.Sin(amount * MathF.Tau * 8f) * 520f);
        }

        var firstSolid = solidScene.AddSoftBrushStroke(
            0,
            solidPath,
            diameter,
            Color.Coral,
            brushShape,
            (uint)solidPath.Length);
        var shiftedSolidPath = solidPath.Select(point => new PointF(point.X, point.Y + 30)).ToArray();
        var processedSolidPath = FreehandStrokeProcessor.Process(
            shiftedSolidPath,
            smoothing: 12,
            simplifyTolerance: VectorUnits.FromPixels(0.9f));
        var solidWatch = Stopwatch.StartNew();
        var solidSnapshot = solidScene.CreateSnapshot();
        var secondSolid = solidScene.AddSoftBrushStroke(
            0,
            processedSolidPath,
            diameter,
            Color.Coral,
            brushShape,
            (uint)processedSolidPath.Length);
        var solidNormalizeInteractively = solidScene.CanNormalizePaintInteractively(secondSolid, 0);
        var solidRetained = solidNormalizeInteractively
            ? solidScene.ApplyFillOverwriteToNewObjects(secondSolid)
            : secondSolid;
        var solidMerged = solidNormalizeInteractively
            ? solidScene.MergeSameColorFillsAroundNewObjects(solidRetained)
            : solidRetained;
        solidWatch.Stop();
        GC.KeepAlive(solidSnapshot);

        const double solidBudgetMilliseconds = 10;
        var solidBudgetMet = firstSolid.Length == 1
            && secondSolid.Length == 1
            && solidMerged.Length == 1
            && !solidNormalizeInteractively
            && solidScene.ObjectCount == 2
            && solidWatch.Elapsed.TotalMilliseconds <= solidBudgetMilliseconds;
        Console.WriteLine($"complex_solid_brush_commit_ms={solidWatch.Elapsed.TotalMilliseconds:0.00}");
        Console.WriteLine($"complex_solid_brush_commit_budget_ms={solidBudgetMilliseconds:0.00}");
        Console.WriteLine($"complex_solid_brush_commit_budget_met={solidBudgetMet.ToString().ToLowerInvariant()}");
        if (!solidBudgetMet)
        {
            throw new InvalidOperationException(
                $"Complex solid brush commit exceeded its budget or failed to merge: elapsed={solidWatch.Elapsed.TotalMilliseconds:0.00}, objects={solidScene.ObjectCount}, merged={solidMerged.Length}.");
        }
    }

    private static void RunLocalizedSpatialAlgorithmRegression()
    {
        const int distantObjectCount = 8_192;
        var scene = new VectorScene();
        scene.CreateEmpty();
        scene.BeginDeferredAppend(distantObjectCount + 2, [0]);
        int nearFirst;
        int nearSecond;
        try
        {
            for (var index = 0; index < distantObjectCount; index++)
            {
                scene.AppendObject(
                    0,
                    new PointF(-22_000 + index % 128 * 100, -12_000 + index / 128 * 100),
                    new SizeF(40, 40),
                    0,
                    0,
                    Color.Teal,
                    Color.Transparent,
                    6,
                    ShapeKind.Rectangle);
            }

            nearFirst = scene.AppendObject(0, PointF.Empty, new SizeF(96, 96), 0, 0, Color.Teal, Color.Transparent, 12, ShapeKind.Rectangle);
            nearSecond = scene.AppendObject(0, new PointF(20, 0), new SizeF(96, 96), 0, 0, Color.Teal, Color.Transparent, 12, ShapeKind.Rectangle);
        }
        finally
        {
            scene.EndDeferredAppend();
        }
        scene.CompleteDeferredBuild();

        var localBounds = new RectangleF(-120, -120, 240, 240);
        var localObjects = scene.QueryObjects(localBounds, 0);
        if (!localObjects.SequenceEqual([nearFirst, nearSecond]))
        {
            throw new InvalidOperationException("Spatial query did not return the unique local objects in stable order.");
        }

        const int querySamples = 512;
        var queryWatch = Stopwatch.StartNew();
        for (var sample = 0; sample < querySamples; sample++)
        {
            var result = scene.QueryObjects(localBounds, 0);
            if (result.Length != 2) throw new InvalidOperationException("Repeated local spatial query changed its result.");
        }
        queryWatch.Stop();

        var mergeWatch = Stopwatch.StartNew();
        var merged = scene.MergeSameColorFillsAround(nearSecond, connectNearby: false, frame: 0);
        mergeWatch.Stop();
        if (scene.ObjectCount != distantObjectCount + 1
            || (uint)merged >= scene.ObjectCount
            || !scene.FillContainsPoint(merged, PointF.Empty))
        {
            throw new InvalidOperationException("Localized fill merge changed distant objects or lost its merged region.");
        }

        Console.WriteLine($"local_spatial_query_avg_ms={queryWatch.Elapsed.TotalMilliseconds / querySamples:0.000}");
        Console.WriteLine($"local_fill_merge_ms={mergeWatch.Elapsed.TotalMilliseconds:0.000}");
        Console.WriteLine($"local_fill_merge_distant_objects={distantObjectCount}");
    }

    private static void RunLinkedFillBoundaryRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        var outlinedFill = scene.AddObject(
            0,
            PointF.Empty,
            new SizeF(200, 100),
            0,
            VectorUnits.StrokePointsToUnits(2),
            Color.Teal,
            Color.White,
            12,
            ShapeKind.Rectangle);

        var boundary = scene.HitTestElement(new PointF(0, -50), 0, toleranceWorld: 2);
        var detached = scene.DetachElementForMove(boundary, 0);
        if (!boundary.IsValid
            || boundary.Key.Kind != DrawingElementKind.BoundaryStroke
            || !detached.IsValid
            || detached.Key.Kind != DrawingElementKind.Stroke
            || scene.ShapeKind[detached.Key.ObjectIndex] != ShapeKind.Line)
        {
            throw new InvalidOperationException("An outlined fill boundary could not be detached into an editable line.");
        }

        var links = scene.CaptureFillBoundaryLineLinks(detached.Key.ObjectIndex, 0);
        var fillObjectIndex = links.Length == 1 ? links[0].FillObjectIndex : -1;
        var expandedPoint = new PointF(0, -80);
        if (links.Length != 1
            || fillObjectIndex != outlinedFill
            || scene.FillContainsPoint(fillObjectIndex, expandedPoint))
        {
            throw new InvalidOperationException("A detached fill boundary did not retain its linked fill contour.");
        }

        scene.SetLineEndpoint(
            detached.Key.ObjectIndex,
            startEndpoint: true,
            endpoint: new PointF(-100, -50),
            oppositeEndpoint: new PointF(100, -50),
            control: new PointF(0, -140),
            keepStraight: false);
        if (!scene.UpdateFillBoundaryLineLinks(links)
            || scene.ShapeKind[fillObjectIndex] != ShapeKind.Path
            || !scene.FillContainsPoint(fillObjectIndex, expandedPoint))
        {
            throw new InvalidOperationException("Editing a linked fill boundary did not expand the fill with the line curve.");
        }

        var refreshedLinks = scene.CaptureFillBoundaryLineLinks(detached.Key.ObjectIndex, 0);
        var movedStart = new PointF(-130, -70);
        scene.SetLineEndpoint(
            detached.Key.ObjectIndex,
            startEndpoint: true,
            endpoint: movedStart,
            oppositeEndpoint: new PointF(100, -50),
            control: new PointF(0, -140),
            keepStraight: false);
        var boundaryMatchesMovedEndpoint = refreshedLinks.Length == 1
            && scene.UpdateFillBoundaryLineLinks(refreshedLinks)
            && scene.GetObjectBoundaryContours(fillObjectIndex)
                .SelectMany(contour => contour)
                .Any(point => MathF.Abs(point.X - movedStart.X) <= DrawingTopologyRules.UnitIntersectionTolerance
                    && MathF.Abs(point.Y - movedStart.Y) <= DrawingTopologyRules.UnitIntersectionTolerance);
        if (!boundaryMatchesMovedEndpoint)
        {
            throw new InvalidOperationException("Editing a linked fill-boundary endpoint left the fill contour at the old endpoint.");
        }

        scene.TryGetLineEndpoint(detached.Key.ObjectIndex, startEndpoint: true, out var transformStart);
        var translatedStart = new PointF(transformStart.X + 40, transformStart.Y - 20);
        scene.TransformObjects(
            [detached.Key.ObjectIndex],
            point => new PointF(point.X + 40, point.Y - 20));
        var transformKeepsFillLinked = scene.GetObjectBoundaryContours(fillObjectIndex)
            .SelectMany(contour => contour)
            .Any(point => MathF.Abs(point.X - translatedStart.X) <= DrawingTopologyRules.UnitIntersectionTolerance
                && MathF.Abs(point.Y - translatedStart.Y) <= DrawingTopologyRules.UnitIntersectionTolerance);
        if (!transformKeepsFillLinked)
        {
            throw new InvalidOperationException("Free Transform moved a fill boundary line without moving the linked fill contour.");
        }

        var shearLinks = scene.CaptureFillBoundaryLineLinks(detached.Key.ObjectIndex, 0);
        scene.ShearObjects(
            [detached.Key.ObjectIndex],
            point => new PointF(point.X + point.Y * 0.2f, point.Y));
        scene.TryGetLineEndpoint(detached.Key.ObjectIndex, startEndpoint: true, out var shearedLineStart);
        var shearedStart = VectorUnits.Quantize(shearedLineStart);
        var shearKeepsFillLinked = shearLinks.Length == 1
            && scene.GetObjectBoundaryContours(fillObjectIndex)
            .SelectMany(contour => contour)
                .Any(point => MathF.Abs(point.X - shearedStart.X) <= DrawingTopologyRules.UnitIntersectionTolerance
                    && MathF.Abs(point.Y - shearedStart.Y) <= DrawingTopologyRules.UnitIntersectionTolerance);
        if (!shearKeepsFillLinked)
        {
            var closestBoundaryDistance = scene.GetObjectBoundaryContours(fillObjectIndex)
                .SelectMany(contour => contour)
                .Select(point => MathF.Sqrt(
                    (point.X - shearedStart.X) * (point.X - shearedStart.X)
                    + (point.Y - shearedStart.Y) * (point.Y - shearedStart.Y)))
                .DefaultIfEmpty(float.MaxValue)
                .Min();
            throw new InvalidOperationException(
                $"Free Transform skewed a fill boundary line without moving the linked fill contour: links={shearLinks.Length}, lineStart={shearedStart}, closestBoundary={closestBoundaryDistance:0.###}.");
        }

        var closedBoundaryScene = new VectorScene();
        closedBoundaryScene.CreateEmpty();
        var closedFill = closedBoundaryScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(180, 120),
            0,
            0,
            Color.Teal,
            Color.Transparent,
            12,
            ShapeKind.Rectangle);
        var closedBoundary = closedBoundaryScene.AddFreehandStroke(
            0,
            closedBoundaryScene.GetShapeBoundary(closedFill),
            VectorUnits.StrokePointsToUnits(2),
            Color.CornflowerBlue,
            brushStroke: false,
            12);
        var closedLinks = closedBoundaryScene.CaptureFillBoundaryLineLinks(closedBoundary, 0);
        closedBoundaryScene.ShearObjects(
            [closedBoundary],
            point => new PointF(point.X + point.Y * 0.35f, point.Y));
        var closedBoundaryMatchesFill = closedBoundaryScene.ShapeKind[closedFill] == ShapeKind.Path
            && closedLinks.Length == 1
            && closedBoundaryScene.TryGetFreehandWorldPoints(closedBoundary, out var closedPoints)
            && closedBoundaryScene.GetObjectBoundaryContours(closedFill)
                .SelectMany(contour => contour)
                .Any(point => MathF.Abs(point.X - VectorUnits.Quantize(closedPoints[0].X)) <= DrawingTopologyRules.UnitIntersectionTolerance
                    && MathF.Abs(point.Y - VectorUnits.Quantize(closedPoints[0].Y)) <= DrawingTopologyRules.UnitIntersectionTolerance);
        if (!closedBoundaryMatchesFill)
        {
            throw new InvalidOperationException("Free Transform skewed a closed fill boundary stroke without moving the linked fill contour.");
        }

        AssertMovingStraightBoundaryEndpointPreservesAdjacentFillEdge(startEndpoint: true);
        AssertMovingStraightBoundaryEndpointPreservesAdjacentFillEdge(startEndpoint: false);
        AssertCurvedLineExpandsStraightFillBoundary(reverse: false);
        AssertCurvedLineExpandsStraightFillBoundary(reverse: true);

        Console.WriteLine("fill_boundary_line_link_regression=ok");

        static void AssertMovingStraightBoundaryEndpointPreservesAdjacentFillEdge(bool startEndpoint)
        {
            var linkedScene = new VectorScene();
            linkedScene.CreateEmpty();
            var linkedFill = linkedScene.AddObject(
                0,
                PointF.Empty,
                new SizeF(200, 100),
                0,
                VectorUnits.StrokePointsToUnits(2),
                Color.Teal,
                Color.White,
                12,
                ShapeKind.Rectangle);
            var boundaryHit = linkedScene.HitTestElement(new PointF(0, 50), 0, toleranceWorld: 2);
            var detachedBoundary = linkedScene.DetachElementForMove(boundaryHit, 0);
            var boundaryLinks = detachedBoundary.IsValid
                ? linkedScene.CaptureFillBoundaryLineLinks(detachedBoundary.Key.ObjectIndex, 0)
                : Array.Empty<FillBoundaryLineLink>();
            if (boundaryLinks.Length != 1
                || boundaryLinks[0].FillObjectIndex != linkedFill
                || !linkedScene.TryGetLineCubic(
                    detachedBoundary.Key.ObjectIndex,
                    out var lineStart,
                    out var lineControl1,
                    out var lineControl2,
                    out var lineEnd))
            {
                throw new InvalidOperationException("A straight detached boundary could not establish its linked fill regression setup.");
            }

            var originalEndpoint = startEndpoint ? lineStart : lineEnd;
            var oppositeEndpoint = startEndpoint ? lineEnd : lineStart;
            var movedEndpoint = VectorUnits.Quantize(new PointF(
                originalEndpoint.X + (startEndpoint ? -70 : 85),
                originalEndpoint.Y - 65));
            linkedScene.SetLineEndpoint(
                detachedBoundary.Key.ObjectIndex,
                startEndpoint,
                movedEndpoint,
                oppositeEndpoint,
                lineControl1,
                lineControl2,
                keepStraight: true);
            if (!linkedScene.UpdateFillBoundaryLineLinks(boundaryLinks))
            {
                throw new InvalidOperationException("Moving a straight detached boundary endpoint did not update its linked fill.");
            }

            var linkedPartIndex = boundaryLinks[0].BezierSegmentIndex;
            var fillParts = linkedScene.GetPathBezierSegmentParts(linkedFill);
            var activeParts = fillParts.Where(part => part.PartIndex == linkedPartIndex).ToArray();
            var adjacentParts = fillParts
                .Where(part => part.PartIndex != linkedPartIndex
                    && (PointsNear(part.Start, movedEndpoint) || PointsNear(part.End, movedEndpoint)))
                .ToArray();
            if (activeParts.Length != 1
                || adjacentParts.Length != 1
                || !VectorScene.IsStraightBezierSegment(
                    activeParts[0].Start,
                    activeParts[0].Control1,
                    activeParts[0].Control2,
                    activeParts[0].End)
                || !VectorScene.IsStraightBezierSegment(
                    adjacentParts[0].Start,
                    adjacentParts[0].Control1,
                    adjacentParts[0].Control2,
                    adjacentParts[0].End))
            {
                throw new InvalidOperationException(
                    $"Moving a straight detached boundary {(startEndpoint ? "start" : "end")} bent the linked fill's adjacent edge.");
            }

            var adjacent = adjacentParts[0];
            var expectedControl1 = VectorUnits.Quantize(new PointF(
                adjacent.Start.X + (adjacent.End.X - adjacent.Start.X) / 3f,
                adjacent.Start.Y + (adjacent.End.Y - adjacent.Start.Y) / 3f));
            var expectedControl2 = VectorUnits.Quantize(new PointF(
                adjacent.Start.X + (adjacent.End.X - adjacent.Start.X) * 2f / 3f,
                adjacent.Start.Y + (adjacent.End.Y - adjacent.Start.Y) * 2f / 3f));
            if (!PointsNear(adjacent.Control1, expectedControl1)
                || !PointsNear(adjacent.Control2, expectedControl2))
            {
                throw new InvalidOperationException("The linked fill's straight adjacent edge did not receive linear cubic controls.");
            }
        }

        static void AssertCurvedLineExpandsStraightFillBoundary(bool reverse)
        {
            var curvedScene = new VectorScene();
            curvedScene.CreateEmpty(2);
            var curvedFill = curvedScene.AddObject(
                0,
                PointF.Empty,
                new SizeF(200, 100),
                0,
                0,
                Color.Teal,
                Color.Transparent,
                12,
                ShapeKind.Rectangle);
            var otherLayerFill = curvedScene.AddObject(
                1,
                PointF.Empty,
                new SizeF(200, 100),
                0,
                0,
                Color.Coral,
                Color.Transparent,
                12,
                ShapeKind.Rectangle);
            var left = new PointF(-100, -50);
            var right = new PointF(100, -50);
            var curvedLine = curvedScene.AddCubicCurveSegment(
                0,
                reverse ? right : left,
                reverse ? new PointF(60, -140) : new PointF(-60, -140),
                reverse ? new PointF(-60, -140) : new PointF(60, -140),
                reverse ? left : right,
                VectorUnits.StrokePointsToUnits(2),
                Color.Transparent,
                Color.White,
                12);
            var expandedPoint = new PointF(0, -80);
            var curvedLinks = curvedScene.CaptureFillBoundaryLineLinks(curvedLine, 0);
            if (curvedLinks.Length != 1
                || curvedLinks[0].FillObjectIndex != curvedFill
                || curvedLinks[0].SegmentCount != 1
                || curvedLinks[0].Reversed != reverse
                || curvedScene.FillContainsPoint(curvedFill, expandedPoint)
                || !curvedScene.UpdateFillBoundaryLineLinks(curvedLinks)
                || !curvedScene.FillContainsPoint(curvedFill, expandedPoint)
                || curvedScene.FillContainsPoint(otherLayerFill, expandedPoint)
                || curvedScene.ShapeKind[otherLayerFill] != ShapeKind.Rectangle
                || curvedScene.ObjectLayer[curvedFill] != 0
                || curvedScene.ObjectKeyframeFrame[curvedFill] != 0)
            {
                throw new InvalidOperationException(
                    $"A {(reverse ? "reversed " : string.Empty)}curved line did not expand its matching straight fill boundary.");
            }

            var preservedFillEdges = curvedScene.GetPathBezierSegmentParts(curvedFill)
                .Where(part => PointsNear(part.Start, left)
                    && PointsNear(part.End, right)
                    && PointsNear(part.Control1, new PointF(-60, -140))
                    && PointsNear(part.Control2, new PointF(60, -140)))
                .ToArray();
            if (!curvedScene.TryGetPathBezierWorldContours(curvedFill, out var exactFillContours)
                || exactFillContours.Length != 1
                || exactFillContours[0].Length != 4
                || preservedFillEdges.Length != 1)
            {
                throw new InvalidOperationException(
                    "Updating a linked fill boundary flattened its cubic edge into sampled line segments.");
            }

            var updatedPoints = curvedScene.GetObjectBoundaryContours(curvedFill).SelectMany(contour => contour).ToArray();
            if (new[]
                {
                    new PointF(-100, -50),
                    new PointF(100, -50),
                    new PointF(100, 50),
                    new PointF(-100, 50)
                }.Any(corner => !updatedPoints.Any(point => PointsNear(point, corner))))
            {
                throw new InvalidOperationException("Expanding a curved fill boundary lost an unchanged contour corner.");
            }

            var curveHit = curvedScene.HitTestElement(new PointF(0, -117.5f), 0, toleranceWorld: 2);
            var selectedPath = MainForm.TraditionalPenPathElements(curvedScene, curveHit, 0);
            if (!curveHit.IsValid
                || curveHit.Key.ObjectIndex != curvedLine
                || curveHit.Key.Kind != DrawingElementKind.Stroke
                || curveHit.StartT > DrawingTopologyRules.UnitIntersectionTolerance
                || curveHit.EndT < 1 - DrawingTopologyRules.UnitIntersectionTolerance
                || selectedPath.Length != 1
                || selectedPath[0].Key.ObjectIndex != curvedLine
                || selectedPath[0].StartT > DrawingTopologyRules.UnitIntersectionTolerance
                || selectedPath[0].EndT < 1 - DrawingTopologyRules.UnitIntersectionTolerance)
            {
                throw new InvalidOperationException(
                    $"A linked curved line was split by its own expanded fill boundary: hit={curveHit}, path={selectedPath.Length}.");
            }

            var refreshedLinks = curvedScene.CaptureFillBoundaryLineLinks(curvedLine, 0);
            if (refreshedLinks.Length != 1
                || refreshedLinks[0].SegmentCount <= 1
                || refreshedLinks[0].BezierSegmentCount != 1)
            {
                throw new InvalidOperationException("Exact curved-boundary matching did not take priority after the fill contour was expanded.");
            }


            var fillEdge = preservedFillEdges[0];
            var strokeLinks = curvedScene.CaptureFillBoundaryStrokeLinks(
                curvedFill,
                fillEdge.PartIndex,
                0);
            var updatedControl1 = new PointF(-85, -175);
            var updatedControl2 = new PointF(75, -105);
            var objectCountBeforeFillEdit = curvedScene.ObjectCount;
            var fillEditInitialContours = curvedScene.GetObjectBoundaryContours(curvedFill);
            if (strokeLinks.Length != 1
                || strokeLinks[0].LineObjectIndex != curvedLine
                || strokeLinks[0].Reversed != reverse
                || !curvedScene.SetPathBezierSegment(
                    curvedFill,
                    fillEdge.PartIndex,
                    fillEdge.Start,
                    updatedControl1,
                    updatedControl2,
                    fillEdge.End,
                    rebuildGeometryIndex: false)
                || !curvedScene.UpdateFillBoundaryStrokeLinks(
                    strokeLinks,
                    fillEdge.Start,
                    updatedControl1,
                    updatedControl2,
                    fillEdge.End,
                    rebuildGeometryIndex: false))
            {
                throw new InvalidOperationException(
                    "Editing a fill boundary did not update its coincident cubic line.");
            }

            curvedScene.NormalizeFillBoundaryOverlaps(
                curvedFill,
                fillEditInitialContours,
                rebuildGeometryIndex: false);
            if (!curvedScene.TryGetPathBezierSegment(curvedFill, fillEdge.PartIndex, out var finalFillEdge))
            {
                throw new InvalidOperationException("A normalized fill-boundary edit lost its active cubic segment.");
            }
            curvedScene.UpdateFillBoundaryStrokeLinks(
                strokeLinks,
                finalFillEdge.Start,
                finalFillEdge.Control1,
                finalFillEdge.Control2,
                finalFillEdge.End,
                rebuildGeometryIndex: false);
            curvedScene.CompleteDeferredBuild();

            var expectedLineStart = reverse ? finalFillEdge.End : finalFillEdge.Start;
            var expectedLineControl1 = reverse ? finalFillEdge.Control2 : finalFillEdge.Control1;
            var expectedLineControl2 = reverse ? finalFillEdge.Control1 : finalFillEdge.Control2;
            var expectedLineEnd = reverse ? finalFillEdge.Start : finalFillEdge.End;
            var updatedFillEdges = curvedScene.GetPathBezierSegmentParts(curvedFill);
            var updatedEdge = updatedFillEdges.Single(part => part.PartIndex == fillEdge.PartIndex);
            var updatedProbe = updatedEdge.Samples[updatedEdge.Samples.Length / 2];
            var updatedHit = curvedScene.HitTestElement(updatedProbe, 0, toleranceWorld: 2);
            if (curvedScene.ObjectCount != objectCountBeforeFillEdit
                || curvedScene.ShapeKind[curvedLine] != ShapeKind.Line
                || updatedFillEdges.Length != 4
                || !curvedScene.TryGetLineCubic(
                    curvedLine,
                    out var actualLineStart,
                    out var actualLineControl1,
                    out var actualLineControl2,
                    out var actualLineEnd)
                || !PointsNear(actualLineStart, expectedLineStart)
                || !PointsNear(actualLineControl1, expectedLineControl1)
                || !PointsNear(actualLineControl2, expectedLineControl2)
                || !PointsNear(actualLineEnd, expectedLineEnd)
                || updatedHit.Key.ObjectIndex != curvedLine
                || updatedHit.Key.Kind != DrawingElementKind.Stroke
                || updatedHit.StartT > DrawingTopologyRules.UnitIntersectionTolerance
                || updatedHit.EndT < 1 - DrawingTopologyRules.UnitIntersectionTolerance)
            {
                throw new InvalidOperationException(
                    $"A fill-boundary edit flattened or split its linked {(reverse ? "reversed " : string.Empty)}cubic line: hit={updatedHit}, objects={curvedScene.ObjectCount}.");
            }


            var endpointLinks = curvedScene.CaptureFillBoundaryLineLinks(curvedLine, 0);
            var excludedLinkedFills = endpointLinks
                .Select(link => link.FillObjectIndex)
                .ToHashSet();
            var endpointIntersection = curvedScene.CaptureFillIntersectionsAtLineEndpoint(
                curvedLine,
                startEndpoint: true,
                0,
                rebuildGeometryIndex: false,
                excludedFillObjectIndices: excludedLinkedFills);
            var movedLineStart = VectorUnits.Quantize(new PointF(actualLineStart.X - 25, actualLineStart.Y - 15));
            var movedLineControl1 = VectorUnits.Quantize(new PointF(actualLineControl1.X - 25, actualLineControl1.Y - 15));
            curvedScene.SetLineEndpoint(
                curvedLine,
                startEndpoint: true,
                movedLineStart,
                actualLineEnd,
                movedLineControl1,
                actualLineControl2,
                keepStraight: false);
            if (endpointLinks.Length != 1
                || endpointIntersection.PathAnchors.Any(anchor => anchor.ObjectIndex == curvedFill)
                || !curvedScene.UpdateFillBoundaryLineLinks(endpointLinks, rebuildGeometryIndex: false))
            {
                throw new InvalidOperationException(
                    "A curved Line endpoint edit did not reserve its coincident fill edge for full-curve synchronization.");
            }
            curvedScene.UpdateSharedBoundaryIntersection(
                endpointIntersection,
                movedLineStart,
                rebuildGeometryIndex: false);
            curvedScene.CompleteDeferredBuild();

            var endpointFillEdge = curvedScene.GetPathBezierSegmentParts(curvedFill)
                .Single(part => part.PartIndex == fillEdge.PartIndex);
            var endpointLink = endpointLinks[0];
            var expectedFillStart = endpointLink.Reversed ? actualLineEnd : movedLineStart;
            var expectedFillControl1 = endpointLink.Reversed ? actualLineControl2 : movedLineControl1;
            var expectedFillControl2 = endpointLink.Reversed ? movedLineControl1 : actualLineControl2;
            var expectedFillEnd = endpointLink.Reversed ? movedLineStart : actualLineEnd;
            if (!PointsNear(endpointFillEdge.Start, expectedFillStart)
                || !PointsNear(endpointFillEdge.Control1, expectedFillControl1)
                || !PointsNear(endpointFillEdge.Control2, expectedFillControl2)
                || !PointsNear(endpointFillEdge.End, expectedFillEnd))
            {
                throw new InvalidOperationException(
                    "Moving a curved Line endpoint allowed shared-anchor synchronization to overwrite its linked fill curve.");
            }

            var resetLine = MainForm.ResetBezierCurvature(movedLineStart, actualLineEnd);
            curvedScene.SetLineEndpoint(
                curvedLine,
                startEndpoint: true,
                resetLine.Start,
                resetLine.End,
                resetLine.Control1,
                resetLine.Control2,
                keepStraight: false);
            if (!curvedScene.UpdateFillBoundaryLineLinks(endpointLinks, rebuildGeometryIndex: false))
            {
                throw new InvalidOperationException("Resetting a Line's Bezier controls did not update its linked fill edge.");
            }
            curvedScene.CompleteDeferredBuild();

            var resetFillEdge = curvedScene.GetPathBezierSegmentParts(curvedFill)
                .Single(part => part.PartIndex == fillEdge.PartIndex);
            if (!curvedScene.TryGetLineCubic(
                    curvedLine,
                    out var resetLineStart,
                    out var resetLineControl1,
                    out var resetLineControl2,
                    out var resetLineEnd)
                || !VectorScene.IsStraightBezierSegment(
                    resetLineStart,
                    resetLineControl1,
                    resetLineControl2,
                    resetLineEnd)
                || !VectorScene.IsStraightBezierSegment(
                    resetFillEdge.Start,
                    resetFillEdge.Control1,
                    resetFillEdge.Control2,
                    resetFillEdge.End))
            {
                throw new InvalidOperationException(
                    "Alt-resetting both Bezier controls did not straighten the Line and linked fill edge together.");
            }
        }
    }

    private static void RunConnectedLineRecolorRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        var stroke = VectorUnits.StrokePointsToUnits(4);
        var first = scene.AddLineSegment(0, new PointF(0, 0), new PointF(100, 0), stroke, Color.Transparent, Color.Coral, 6);
        var second = scene.AddLineSegment(0, new PointF(100, 0), new PointF(200, 0), stroke, Color.Transparent, Color.Coral, 6);
        var third = scene.AddLineSegment(0, new PointF(200, 0), new PointF(300, 0), stroke, Color.Transparent, Color.Coral, 6);
        var unconnected = scene.AddLineSegment(0, new PointF(0, 100), new PointF(100, 100), stroke, Color.Transparent, Color.RoyalBlue, 6);
        scene.SetGradientPaint(
            first,
            GradientKind.Linear,
            [new GradientStop(0, Color.Coral), new GradientStop(1, Color.Gold)]);

        var hit = scene.HitTestElement(new PointF(40, 0), 0, toleranceWorld: 12);
        var targets = scene.GetConnectedStrokeElements(hit, 0)
            .Select(element => element.Key.ObjectIndex)
            .Where(index => scene.ShapeKind[index] == ShapeKind.Line)
            .Distinct()
            .ToArray();
        var fillColor = Color.Teal.ToArgb();
        foreach (var index in targets)
        {
            scene.StrokeArgb[index] = fillColor;
            scene.DisableLinearGradient(index);
        }

        var recolored = targets.OrderBy(index => index).SequenceEqual(new[] { first, second, third })
            && targets.All(index => scene.StrokeArgb[index] == fillColor && !scene.HasGradient(index))
            && scene.StrokeArgb[unconnected] == Color.RoyalBlue.ToArgb()
            && !scene.HasGradient(unconnected);
        if (!recolored)
        {
            throw new InvalidOperationException("Connected line recoloring did not limit the solid Fill color to the clicked stroke network.");
        }

        Console.WriteLine("connected_line_recolor_regression=ok");
    }

    private static void RunTransformGeometryRegression()
    {
        var overlayFrame = new TransformOverlayFrame(
            new PointF(20, 30),
            new PointF(80, 60),
            new PointF(-30, 40));
        if (!overlayFrame.IsValid
            || !PointsNear(overlayFrame.TopRight, new PointF(100, 90))
            || !PointsNear(overlayFrame.BottomLeft, new PointF(-10, 70))
            || !PointsNear(overlayFrame.BottomRight, new PointF(70, 130))
            || !PointsNear(overlayFrame.Center, new PointF(45, 80))
            || overlayFrame.Bounds != RectangleF.FromLTRB(-10, 30, 100, 130))
        {
            throw new InvalidOperationException("The oriented Free Transform overlay frame lost its rotated or skewed geometry.");
        }

        var scene = new VectorScene();
        scene.CreateEmpty();
        var line = scene.AddCurveSegment(0, new PointF(0, 0), new PointF(40, 80), new PointF(100, 0), 6, Color.Transparent, Color.Coral, 6);
        var freehand = scene.AddFreehandStroke(0, new[] { new PointF(-80, -40), new PointF(-20, 60) }, 6, Color.White, brushStroke: false, 6);
        var path = scene.AddPathObjectContours(0, new[] { new[] { new PointF(140, 0), new PointF(180, 0), new PointF(160, 40), new PointF(140, 0) } }, 0, Color.Teal, Color.Transparent, 6);
        var rectangle = scene.AddObject(0, PointF.Empty, new SizeF(100, 60), 0, 4, Color.Teal, Color.White, 6, ShapeKind.Rectangle);
        scene.TransformObjects(new[] { line, freehand, path }, point => new PointF(point.X + 30, point.Y - 20));
        scene.ShearObjects(new[] { rectangle }, point => new PointF(point.X + (point.Y - 30) * 0.5f, point.Y));

        scene.TryGetLineEndpoint(line, startEndpoint: true, out var lineStart);
        scene.TryGetLineEndpoint(line, startEndpoint: false, out var lineEnd);
        var lineControl1 = new PointF(scene.CurveControlX[line], scene.CurveControlY[line]);
        var lineControl2 = new PointF(scene.CurveControl2X[line], scene.CurveControl2Y[line]);
        var freehandValid = scene.TryGetFreehandWorldPoints(freehand, out var freehandPoints);
        var pathValid = scene.TryGetPathWorldContours(path, out var pathContours);
        var skewValid = scene.TryGetPathWorldContours(rectangle, out var skewContours);
        if (!PointsNear(lineStart, new PointF(30, -20))
            || !PointsNear(lineEnd, new PointF(130, -20))
            || !PointsNear(lineControl1, new PointF(57, 33))
            || !PointsNear(lineControl2, new PointF(90, 33))
            || !freehandValid
            || freehandPoints.Length != 2
            || !PointsNear(freehandPoints[0], new PointF(-50, -60))
            || !pathValid
            || pathContours.Length != 1
            || !PointsNear(pathContours[0][0], new PointF(170, -20))
            || scene.ShapeKind[rectangle] != ShapeKind.Path
            || !skewValid
            || skewContours.Length != 1
            || !PointsNear(skewContours[0][0], new PointF(-80, -30)))
        {
            throw new InvalidOperationException("Free transform did not preserve line, freehand, path, and skew geometry.");
        }

        var irregularScene = new VectorScene();
        irregularScene.CreateEmpty();
        var irregularFill = irregularScene.AddPathObjectContours(
            0,
            [
                [
                    new PointF(-170, -55),
                    new PointF(-115, -140),
                    new PointF(-35, -105),
                    new PointF(20, -165),
                    new PointF(95, -90),
                    new PointF(165, -45),
                    new PointF(125, 35),
                    new PointF(175, 105),
                    new PointF(55, 135),
                    new PointF(-15, 95),
                    new PointF(-105, 155),
                    new PointF(-145, 45),
                    new PointF(-170, -55)
                ]
            ],
            0,
            Color.MediumSeaGreen,
            Color.Transparent,
            24);
        var irregularSnapshot = irregularScene.CreateSnapshot();
        var rotationSession = irregularScene.BeginTransformSession([irregularFill]);
        const float finalRotation = 0.73f;
        for (var step = 1; step <= 180; step++)
        {
            var angle = finalRotation * step / 180f;
            var cos = MathF.Cos(angle);
            var sin = MathF.Sin(angle);
            irregularScene.ApplyTransformSession(
                rotationSession,
                point => new PointF(point.X * cos - point.Y * sin, point.X * sin + point.Y * cos),
                convertPrimitivesToPaths: false,
                rebuildGeometryIndex: false);
        }

        irregularScene.TryGetPathWorldContours(irregularFill, out var previewRotationContours);
        irregularScene.RestoreSnapshot(irregularSnapshot);
        var finalCos = MathF.Cos(finalRotation);
        var finalSin = MathF.Sin(finalRotation);
        irregularScene.TransformObjects(
            [irregularFill],
            point => new PointF(
                point.X * finalCos - point.Y * finalSin,
                point.X * finalSin + point.Y * finalCos));
        irregularScene.TryGetPathWorldContours(irregularFill, out var directRotationContours);
        if (!ContoursNear(previewRotationContours, directRotationContours))
        {
            throw new InvalidOperationException("Repeated Free Transform rotation previews distorted an irregular fill edge.");
        }

        irregularScene.RestoreSnapshot(irregularSnapshot);
        var skewSession = irregularScene.BeginTransformSession([irregularFill]);
        const float finalSkew = 0.47f;
        for (var step = 1; step <= 180; step++)
        {
            var factor = finalSkew * step / 180f;
            irregularScene.ApplyTransformSession(
                skewSession,
                point => new PointF(point.X + point.Y * factor, point.Y),
                convertPrimitivesToPaths: true,
                rebuildGeometryIndex: false);
        }

        irregularScene.TryGetPathWorldContours(irregularFill, out var previewSkewContours);
        irregularScene.RestoreSnapshot(irregularSnapshot);
        irregularScene.ShearObjects(
            [irregularFill],
            point => new PointF(point.X + point.Y * finalSkew, point.Y));
        irregularScene.TryGetPathWorldContours(irregularFill, out var directSkewContours);
        if (!ContoursNear(previewSkewContours, directSkewContours))
        {
            throw new InvalidOperationException("Repeated Free Transform skew previews distorted an irregular fill edge.");
        }

        var deferredScene = new VectorScene();
        deferredScene.CreateEmpty();
        var deferredObject = deferredScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(100, 80),
            0,
            0,
            Color.Coral,
            Color.Transparent,
            6,
            ShapeKind.Rectangle);
        var initialGeometryRevision = deferredScene.GeometryRevision;
        var initialSummaryRevision = deferredScene.SummaryRevision;
        for (var step = 0; step < 8; step++)
        {
            deferredScene.TransformObjects(
                [deferredObject],
                point => new PointF(point.X + 20, point.Y),
                rebuildGeometryIndex: false);
        }

        deferredScene.ShearObjects(
            [deferredObject],
            point => new PointF(point.X + point.Y * 0.25f, point.Y),
            rebuildGeometryIndex: false);
        if (deferredScene.GeometryRevision != initialGeometryRevision + 9
            || deferredScene.SummaryRevision != initialSummaryRevision)
        {
            throw new InvalidOperationException("Deferred free transform rebuilt derived geometry state during pointer updates.");
        }

        var deferredGeometryRevision = deferredScene.GeometryRevision;
        deferredScene.CompleteDeferredBuild();
        if (deferredScene.GeometryRevision <= deferredGeometryRevision
            || deferredScene.SummaryRevision <= initialSummaryRevision
            || deferredScene.HitTest(new PointF(160, 0), 0) != deferredObject)
        {
            throw new InvalidOperationException("Completing a deferred free transform did not refresh spatial queries and summaries.");
        }

        Console.WriteLine("transform_geometry_regression=ok");
    }

    private static void RunShapeToolRegression()
    {
        var drawSettings = new DrawSettings { AngleSnapDegrees = 15 };
        var inputAngle = 22f * MathF.PI / 180f;
        var unsnappedAngle = drawSettings.SnapAngle(inputAngle);
        var shiftSnappedAngle = drawSettings.SnapAngle(inputAngle, temporarilyEnabled: true);
        drawSettings.SnapEnabled = true;
        drawSettings.AngleSnapEnabled = true;
        var configuredSnappedAngle = drawSettings.SnapAngle(inputAngle);
        drawSettings.SnapToGrid = true;
        drawSettings.SnapToObjects = true;
        drawSettings.GridSize = 100;
        var pointer = new PointF(94, 106);
        var objectCandidate = new PointF(91, 109);
        var objectPriorityPoint = drawSettings.ResolvePointSnap(pointer, objectCandidate);
        var gridFallbackPoint = drawSettings.ResolvePointSnap(pointer);
        var adaptiveGridPoint = drawSettings.ResolvePointSnap(
            new PointF(1494, 506),
            gridStep: 1000);
        var adaptiveObjectPriorityPoint = drawSettings.ResolvePointSnap(
            new PointF(1494, 506),
            objectCandidate,
            gridStep: 1000);
        drawSettings.SnapToObjects = false;
        var disabledObjectPoint = drawSettings.ResolvePointSnap(pointer, objectCandidate);
        var temporaryObjectPoint = drawSettings.ResolvePointSnap(
            pointer,
            objectCandidate,
            temporarilySnapToObjects: true);
        var shifted = MainForm.ResolveShapeDragBounds(
            PointF.Empty,
            new PointF(120, 40),
            keepAspectRatio: true,
            fromCenter: false);
        var centered = MainForm.ResolveShapeDragBounds(
            new PointF(80, 60),
            new PointF(130, 80),
            keepAspectRatio: true,
            fromCenter: true);
        if (Math.Abs(unsnappedAngle - inputAngle) > 0.0001f
            || Math.Abs(shiftSnappedAngle - 15f * MathF.PI / 180f) > 0.0001f
            || Math.Abs(configuredSnappedAngle - shiftSnappedAngle) > 0.0001f
            || !PointsNear(objectPriorityPoint, objectCandidate)
            || !PointsNear(gridFallbackPoint, new PointF(100, 100))
            || !PointsNear(adaptiveGridPoint, new PointF(1000, 1000))
            || !PointsNear(adaptiveObjectPriorityPoint, objectCandidate)
            || !PointsNear(disabledObjectPoint, gridFallbackPoint)
            || !PointsNear(temporaryObjectPoint, objectCandidate)
            || !PointsNear(shifted.Start, PointF.Empty)
            || !PointsNear(shifted.End, new PointF(120, 120))
            || !PointsNear(centered.Start, new PointF(30, 10))
            || !PointsNear(centered.End, new PointF(130, 110)))
        {
            throw new InvalidOperationException("Shape modifiers or object/grid/angle snapping priority were not resolved correctly.");
        }

        var anchorSnap = MainForm.ResolvePenAnchorSnap(
            new PointF(94, 106),
            [new PointF(90, 108), new PointF(200, 104)],
            10,
            new PointF(100, 100),
            objectSnapping: true,
            alignment: true);
        var anchorAlignment = MainForm.ResolvePenAnchorSnap(
            new PointF(94, 106),
            [new PointF(92, 180), new PointF(200, 104)],
            10,
            new PointF(100, 100),
            objectSnapping: false,
            alignment: true);
        if (!anchorSnap.ObjectSnapped
            || !PointsNear(anchorSnap.Point, new PointF(90, 108))
            || anchorSnap.AlignX
            || anchorSnap.AlignY
            || anchorAlignment.ObjectSnapped
            || !anchorAlignment.AlignX
            || !anchorAlignment.AlignY
            || !PointsNear(anchorAlignment.Point, new PointF(92, 104)))
        {
            throw new InvalidOperationException("Pen anchor snapping did not preserve object priority or independent X/Y alignment.");
        }

        var splitScene = new VectorScene();
        splitScene.CreateEmpty();
        var splitSource = splitScene.AddCurveSegment(
            0,
            new PointF(0, 0),
            new PointF(50, 100),
            new PointF(100, 0),
            8,
            Color.Transparent,
            Color.Coral,
            12,
            LineEndpointStyle.Sharp,
            LineEndpointStyle.Sharp);
        splitScene.SetGradientPaint(
            splitSource,
            GradientKind.Linear,
            [new GradientStop(0, Color.Coral), new GradientStop(1, Color.Gold)]);
        splitScene.TryGetLineBezierPart(splitSource, 0, 1, out var originalStart, out _, out _, out var originalEnd);
        var originalOrder = splitScene.ObjectOrder[splitSource];
        var originalKeyframe = splitScene.ObjectKeyframeFrame[splitSource];
        var closestPointFound = splitScene.TryGetClosestPointOnLine(
            splitSource,
            new PointF(50, 50),
            out var closestParameter,
            out var closestPoint,
            out var closestDistance);
        var split = splitScene.SplitLineAt(splitSource, closestParameter, out var splitResult);
        splitScene.TryGetLineBezierPart(
            splitResult.FirstObjectIndex,
            0,
            1,
            out var firstStart,
            out var firstControl1,
            out var firstControl2,
            out var firstEnd);
        splitScene.TryGetLineBezierPart(
            splitResult.SecondObjectIndex,
            0,
            1,
            out var secondStart,
            out var secondControl1,
            out var secondControl2,
            out var secondEnd);
        if (!closestPointFound
            || closestDistance > 0.5f
            || Math.Abs(closestParameter - 0.5f) > 0.01f
            || !split
            || splitScene.ObjectCount != 2
            || Math.Abs(firstStart.X - originalStart.X) > 0.5f
            || Math.Abs(firstStart.Y - originalStart.Y) > 0.5f
            || !PointsNear(firstControl1, new PointF(16, 33))
            || !PointsNear(firstControl2, new PointF(33, 50))
            || Math.Abs(firstEnd.X - secondStart.X) > DrawingTopologyRules.MinStrokeSegmentUnits
            || Math.Abs(firstEnd.Y - secondStart.Y) > DrawingTopologyRules.MinStrokeSegmentUnits
            || Math.Abs(firstEnd.X - splitResult.Anchor.X) > 0.5f
            || Math.Abs(firstEnd.Y - splitResult.Anchor.Y) > 0.5f
            || Math.Abs(secondStart.X - splitResult.Anchor.X) > 0.5f
            || Math.Abs(secondStart.Y - splitResult.Anchor.Y) > 0.5f
            || !PointsNear(secondControl1, new PointF(67, 50))
            || !PointsNear(secondControl2, new PointF(83, 34))
            || Math.Abs(secondEnd.X - originalEnd.X) > 0.5f
            || Math.Abs(secondEnd.Y - originalEnd.Y) > 0.5f
            || splitScene.GetLineEndpointStyle(splitResult.FirstObjectIndex, startEndpoint: true) != LineEndpointStyle.Sharp
            || splitScene.GetLineEndpointStyle(splitResult.FirstObjectIndex, startEndpoint: false) != LineEndpointStyle.Round
            || splitScene.GetLineEndpointStyle(splitResult.SecondObjectIndex, startEndpoint: true) != LineEndpointStyle.Round
            || splitScene.GetLineEndpointStyle(splitResult.SecondObjectIndex, startEndpoint: false) != LineEndpointStyle.Sharp
            || !splitScene.HasGradient(splitResult.FirstObjectIndex)
            || !splitScene.HasGradient(splitResult.SecondObjectIndex)
            || splitScene.ObjectOrder[splitResult.FirstObjectIndex] != originalOrder
            || splitScene.ObjectOrder[splitResult.SecondObjectIndex] != originalOrder
            || splitScene.ObjectSubOrder[splitResult.SecondObjectIndex] <= splitScene.ObjectSubOrder[splitResult.FirstObjectIndex]
            || splitScene.ObjectKeyframeFrame[splitResult.FirstObjectIndex] != originalKeyframe
            || splitScene.ObjectKeyframeFrame[splitResult.SecondObjectIndex] != originalKeyframe
            || splitScene.AtomCount[splitResult.FirstObjectIndex] + splitScene.AtomCount[splitResult.SecondObjectIndex] != 12)
        {
            throw new InvalidOperationException(
                $"Pen anchor insertion did not preserve continuous curve geometry, material, ownership, or order: "
                + $"closest={closestParameter:0.###}/{closestPoint}/{closestDistance:0.###}, "
                + $"first={firstStart}/{firstControl1}/{firstControl2}/{firstEnd}, "
                + $"second={secondStart}/{secondControl1}/{secondControl2}/{secondEnd}, "
                + $"anchor={splitResult.Anchor}, objects={splitScene.ObjectCount}, "
                + $"styles={splitScene.GetLineEndpointStyle(splitResult.FirstObjectIndex, true)}/{splitScene.GetLineEndpointStyle(splitResult.FirstObjectIndex, false)}"
                + $"-{splitScene.GetLineEndpointStyle(splitResult.SecondObjectIndex, true)}/{splitScene.GetLineEndpointStyle(splitResult.SecondObjectIndex, false)}, "
                + $"gradient={splitScene.HasGradient(splitResult.FirstObjectIndex)}/{splitScene.HasGradient(splitResult.SecondObjectIndex)}, "
                + $"order={splitScene.ObjectOrder[splitResult.FirstObjectIndex]}/{splitScene.ObjectOrder[splitResult.SecondObjectIndex]}, "
                + $"sub={splitScene.ObjectSubOrder[splitResult.FirstObjectIndex]}/{splitScene.ObjectSubOrder[splitResult.SecondObjectIndex]}, "
                + $"key={splitScene.ObjectKeyframeFrame[splitResult.FirstObjectIndex]}/{splitScene.ObjectKeyframeFrame[splitResult.SecondObjectIndex]}, "
                + $"atoms={splitScene.AtomCount[splitResult.FirstObjectIndex]}+{splitScene.AtomCount[splitResult.SecondObjectIndex]}.");
        }

        var isolatedPenLine = splitScene.AddLineSegment(
            0,
            new PointF(240, 0),
            new PointF(320, 0),
            8,
            Color.Transparent,
            Color.Coral,
            6);
        var penPathSeed = new DrawingElementHit(
            new DrawingElementKey(splitResult.FirstObjectIndex, DrawingElementKind.Stroke, 0),
            0,
            0,
            1);
        var selectedPenPath = MainForm.TraditionalPenPathElements(splitScene, penPathSeed, 0);
        if (selectedPenPath.Length != 2
            || selectedPenPath.Any(hit => hit.Key.ObjectIndex == isolatedPenLine))
        {
            throw new InvalidOperationException("Traditional Pen one-click path selection did not include only the connected line segments.");
        }

        var straightPenSegment = MainForm.TraditionalPenCubicSegment(
            new PointF(0, 0),
            outgoingHandle: null,
            incomingHandle: null,
            new PointF(100, 0));
        var tangentPenSegment = MainForm.TraditionalPenCubicSegment(
            new PointF(0, 0),
            new PointF(50, 0),
            new PointF(100, 50),
            new PointF(100, 100));
        var inflectedPenSegment = MainForm.TraditionalPenCubicSegment(
            new PointF(0, 0),
            new PointF(40, 100),
            new PointF(60, -100),
            new PointF(100, 0));
        var nearParallelPenSegment = MainForm.TraditionalPenCubicSegment(
            new PointF(0, 0),
            new PointF(100, 0),
            new PointF(200, 0.01f),
            new PointF(300, 0));
        var perturbedParallelPenSegment = MainForm.TraditionalPenCubicSegment(
            new PointF(0, 0),
            new PointF(100, 0),
            new PointF(200, 0.02f),
            new PointF(300, 0));
        if (Math.Abs(straightPenSegment.Control1.X - 100f / 3f) > 0.001f
            || Math.Abs(straightPenSegment.Control2.X - 200f / 3f) > 0.001f
            || Math.Abs(straightPenSegment.Control1.Y) > 0.001f
            || Math.Abs(straightPenSegment.Control2.Y) > 0.001f
            || !PointsNear(tangentPenSegment.Control1, new PointF(50, 0))
            || !PointsNear(tangentPenSegment.Control2, new PointF(100, 50))
            || inflectedPenSegment.Control1.Y <= 0
            || inflectedPenSegment.Control2.Y >= 0
            || Math.Abs(nearParallelPenSegment.Control2.X - perturbedParallelPenSegment.Control2.X) > 0.1f
            || Math.Abs(nearParallelPenSegment.Control2.Y - perturbedParallelPenSegment.Control2.Y) > 0.1f
            || !float.IsFinite(inflectedPenSegment.Control1.X)
            || !float.IsFinite(inflectedPenSegment.Control1.Y)
            || !float.IsFinite(inflectedPenSegment.Control2.X)
            || !float.IsFinite(inflectedPenSegment.Control2.Y))
        {
            throw new InvalidOperationException("Traditional Pen cubic controls did not preserve straight, stable, or inflected curve behavior.");
        }

        using (var penGuideStage = new StageControl(splitScene))
        {
            penGuideStage.SetSelection(
                selectedPenPath.Select(hit => hit.Key.ObjectIndex),
                selectedPenPath[0].Key.ObjectIndex);
            penGuideStage.SetSelectedElements(selectedPenPath, selectedPenPath[0]);
            penGuideStage.SetPenPathHandlesVisible(true);
            if (!penGuideStage.PenPathHandlesVisible || penGuideStage.SelectedElements.Count != 2)
            {
                throw new InvalidOperationException("Traditional Pen connected-path handles were not exposed to the Stage renderers.");
            }
            penGuideStage.SetPenAnchorGuides(splitResult.Anchor, vertical: true, horizontal: false, snapped: true, insertion: false);
            if (!penGuideStage.PenAnchorGuidesVisible
                || !penGuideStage.PenAnchorGuideVertical
                || penGuideStage.PenAnchorGuideHorizontal
                || !penGuideStage.PenAnchorGuideSnapped)
            {
                throw new InvalidOperationException("Pen anchor guides did not expose their alignment and snap state.");
            }
            penGuideStage.ClearPenAnchorGuides();
            if (penGuideStage.PenAnchorGuidesVisible)
            {
                throw new InvalidOperationException("Pen anchor guides were not cleared with the pen interaction.");
            }
            penGuideStage.SetPenDirectionHandles(
                splitResult.Anchor,
                new PointF(splitResult.Anchor.X - 20, splitResult.Anchor.Y),
                new PointF(splitResult.Anchor.X + 20, splitResult.Anchor.Y));
            if (!penGuideStage.PenDirectionHandlesVisible
                || penGuideStage.PenDirectionIncoming is null
                || penGuideStage.PenDirectionOutgoing is null)
            {
                throw new InvalidOperationException("Traditional Pen direction handles were not exposed to the Stage renderers.");
            }
            penGuideStage.ClearPenDirectionHandles();
            if (penGuideStage.PenDirectionHandlesVisible)
            {
                throw new InvalidOperationException("Traditional Pen direction handles were not cleared with the path session.");
            }
            penGuideStage.SetPenPathHandlesVisible(false);
        }

        var scene = new VectorScene();
        scene.CreateEmpty();
        var polygon = scene.AddObject(
            0,
            new PointF(-120, 0),
            new SizeF(100, 100),
            0,
            0,
            Color.Teal,
            12,
            ShapeKind.Polygon,
            shapeVertexCount: 7);
        var star = scene.AddObject(
            0,
            new PointF(120, 0),
            new SizeF(100, 100),
            0,
            0,
            Color.Coral,
            12,
            ShapeKind.Star,
            shapeVertexCount: 8);
        var polygonBoundary = scene.GetShapeBoundary(polygon);
        var starBoundary = scene.GetShapeBoundary(star);
        var snapshot = scene.CreateSnapshot();
        var restored = new VectorScene();
        restored.RestoreSnapshot(snapshot);
        if (scene.GetShapeVertexCount(polygon) != 7
            || scene.GetShapeVertexCount(star) != 8
            || polygonBoundary.Length != 8
            || starBoundary.Length != 17
            || restored.GetShapeVertexCount(polygon) != 7
            || restored.GetShapeVertexCount(star) != 8
            || !ShapeSettingsPanel.SupportsShape(ShapeKind.Polygon)
            || !ShapeSettingsPanel.SupportsShape(ShapeKind.Star)
            || ShapeSettingsPanel.SupportsShape(ShapeKind.Rectangle)
            || !MainForm.ShouldShowShapeSettings(WorkspaceView.BasicDrawing, ToolMode.Polygon)
            || !MainForm.ShouldShowShapeSettings(WorkspaceView.BasicDrawing, ToolMode.Star)
            || MainForm.ShouldShowShapeSettings(WorkspaceView.BasicDrawing, ToolMode.Rectangle)
            || MainForm.ShouldShowShapeSettings(WorkspaceView.SceneEditor, ToolMode.Polygon))
        {
            throw new InvalidOperationException("Polygon sides or star points were not retained or exposed in their drawing-tool context.");
        }

        var flipScene = new VectorScene();
        flipScene.CreateEmpty();
        var triangle = flipScene.AddObject(
            0,
            new PointF(40, 20),
            new SizeF(120, 80),
            0,
            0,
            Color.Coral,
            Color.Transparent,
            8,
            ShapeKind.Triangle);
        flipScene.SetLinearGradient(
            triangle,
            Color.Coral,
            Color.RoyalBlue,
            new PointF(10, 20),
            new PointF(90, 20));
        var originalTriangle = flipScene.GetShapeBoundary(triangle)[..^1];
        var horizontalBounds = flipScene.GetObjectWorldBounds(triangle);
        var horizontalAxis = horizontalBounds.Left + horizontalBounds.Right;
        if (!flipScene.FlipObjects([triangle], horizontal: true)
            || flipScene.ShapeKind[triangle] != ShapeKind.Path
            || !flipScene.TryGetPathWorldContours(triangle, out var horizontalContours)
            || horizontalContours.Length != 1
            || !horizontalContours[0].Select((point, index) =>
                    PointsNear(point, new PointF(horizontalAxis - originalTriangle[index].X, originalTriangle[index].Y)))
                .All(match => match)
            || !PointsNear(flipScene.GetGradientStart(triangle), new PointF(horizontalAxis - 10, 20))
            || !PointsNear(flipScene.GetGradientEnd(triangle), new PointF(horizontalAxis - 90, 20)))
        {
            throw new InvalidOperationException("Horizontal flip did not mirror asymmetric geometry and its gradient around the selection bounds.");
        }

        var horizontalTriangle = horizontalContours[0].ToArray();
        var verticalBounds = flipScene.GetObjectWorldBounds(triangle);
        var verticalAxis = verticalBounds.Top + verticalBounds.Bottom;
        if (!flipScene.FlipObjects([triangle], horizontal: false)
            || !flipScene.TryGetPathWorldContours(triangle, out var verticalContours)
            || !verticalContours[0].Select((point, index) =>
                    PointsNear(point, new PointF(horizontalTriangle[index].X, verticalAxis - horizontalTriangle[index].Y)))
                .All(match => match))
        {
            throw new InvalidOperationException("Vertical flip did not mirror path geometry around the selection bounds.");
        }

        RunBoundaryBezierHandleRegression();

        Console.WriteLine("shape_tool_regression=ok");
    }

    private static void RunBoundaryBezierHandleRegression()
    {
        static PointF Mix(PointF first, PointF second, float amount) => new(
            first.X + (second.X - first.X) * amount,
            first.Y + (second.Y - first.Y) * amount);

        var scene = new VectorScene();
        scene.CreateEmpty();
        var rectangle = scene.AddObject(
            0,
            PointF.Empty,
            new SizeF(5000, 3000),
            0,
            VectorUnits.StrokePointsToUnits(2),
            Color.Teal,
            Color.CornflowerBlue,
            12,
            ShapeKind.Rectangle);
        var boundary = scene.HitTestElement(new PointF(0, -1500), 0, toleranceWorld: 2);
        using var stage = new StageControl(scene) { Size = new Size(640, 420) };
        stage.SetSelection([rectangle], rectangle);
        stage.SetSelectedElement(boundary);
        Point ToScreen(PointF point) => Point.Round(stage.WorldToScreen(point.X, point.Y));
        var start = PointF.Empty;
        var control1 = PointF.Empty;
        var control2 = PointF.Empty;
        var end = PointF.Empty;
        var exposesVirtualBezierHandles = boundary.Key.Kind == DrawingElementKind.BoundaryStroke
            && stage.TryGetEditableBezierWorldPoints(boundary, out start, out control1, out control2, out end)
            && stage.HitTestLineElementHandle(ToScreen(start), boundary) == EditHandleKind.LineStart
            && stage.HitTestLineElementHandle(ToScreen(end), boundary) == EditHandleKind.LineEnd
            && stage.HitTestLineElementHandle(ToScreen(control1), boundary) == EditHandleKind.BezierControl
            && stage.HitTestLineElementHandle(ToScreen(control2), boundary) == EditHandleKind.BezierControl2
            && stage.HitTestHandle(ToScreen(control1), rectangle) == EditHandleKind.BezierControl;
        if (!exposesVirtualBezierHandles)
        {
            throw new InvalidOperationException("A shape boundary did not expose editable endpoint and curve handles before materialization.");
        }

        var rectangleSnapshot = scene.CreateSnapshot();
        var adjustedStart = VectorUnits.Quantize(new PointF(start.X + 420, start.Y + 260));
        var straightEndpointAdjustment = MainForm.AdjustFillEdgeBezierHandle(
            start,
            control1,
            control2,
            end,
            EditHandleKind.LineStart,
            adjustedStart);
        var expectedStraightControl1 = new PointF(
            adjustedStart.X + (end.X - adjustedStart.X) / 3f,
            adjustedStart.Y + (end.Y - adjustedStart.Y) / 3f);
        var expectedStraightControl2 = new PointF(
            adjustedStart.X + (end.X - adjustedStart.X) * 2f / 3f,
            adjustedStart.Y + (end.Y - adjustedStart.Y) * 2f / 3f);
        var adjustedStartApplied = scene.TryConvertFillToBezierPath(rectangle)
            && scene.SetPathBezierSegment(
                rectangle,
                boundary.Key.PartIndex,
                straightEndpointAdjustment.Start,
                straightEndpointAdjustment.Control1,
                straightEndpointAdjustment.Control2,
                straightEndpointAdjustment.End,
                preserveStraightAdjacentSegments: true);
        var hasStoredStraightAdjustment = scene.TryGetPathBezierSegment(
            rectangle,
            boundary.Key.PartIndex,
            out var storedStraightAdjustment);
        var previousStraightParts = scene.GetPathBezierSegmentParts(rectangle)
            .Where(part => part.PartIndex != boundary.Key.PartIndex
                && PointsNear(part.End, adjustedStart))
            .ToArray();
        var previousStraightPart = previousStraightParts.FirstOrDefault();
        var expectedPreviousControl1 = new PointF(
            previousStraightPart.Start.X + (adjustedStart.X - previousStraightPart.Start.X) / 3f,
            previousStraightPart.Start.Y + (adjustedStart.Y - previousStraightPart.Start.Y) / 3f);
        var expectedPreviousControl2 = new PointF(
            previousStraightPart.Start.X + (adjustedStart.X - previousStraightPart.Start.X) * 2f / 3f,
            previousStraightPart.Start.Y + (adjustedStart.Y - previousStraightPart.Start.Y) * 2f / 3f);
        if (!adjustedStartApplied
            || !hasStoredStraightAdjustment
            || !PointsNear(storedStraightAdjustment.Start, adjustedStart)
            || !PointsNear(storedStraightAdjustment.Control1, VectorUnits.Quantize(expectedStraightControl1))
            || !PointsNear(storedStraightAdjustment.Control2, VectorUnits.Quantize(expectedStraightControl2))
            || !PointsNear(storedStraightAdjustment.End, end)
            || previousStraightParts.Length != 1
            || !PointsNear(previousStraightPart.Control1, VectorUnits.Quantize(expectedPreviousControl1))
            || !PointsNear(previousStraightPart.Control2, VectorUnits.Quantize(expectedPreviousControl2)))
        {
            throw new InvalidOperationException(
                "Moving a straight fill-boundary endpoint bent the active or preceding edge away from its new chord.");
        }

        scene.RestoreSnapshot(rectangleSnapshot);
        var adjustedEnd = VectorUnits.Quantize(new PointF(end.X - 240, end.Y + 180));
        var straightEndAdjustment = MainForm.AdjustFillEdgeBezierHandle(
            start,
            control1,
            control2,
            end,
            EditHandleKind.LineEnd,
            adjustedEnd);
        var expectedEndControl1 = new PointF(
            start.X + (adjustedEnd.X - start.X) / 3f,
            start.Y + (adjustedEnd.Y - start.Y) / 3f);
        var expectedEndControl2 = new PointF(
            start.X + (adjustedEnd.X - start.X) * 2f / 3f,
            start.Y + (adjustedEnd.Y - start.Y) * 2f / 3f);
        var adjustedEndApplied = scene.TryConvertFillToBezierPath(rectangle)
            && scene.SetPathBezierSegment(
                rectangle,
                boundary.Key.PartIndex,
                straightEndAdjustment.Start,
                straightEndAdjustment.Control1,
                straightEndAdjustment.Control2,
                straightEndAdjustment.End,
                preserveStraightAdjacentSegments: true);
        var hasStoredEndAdjustment = scene.TryGetPathBezierSegment(
            rectangle,
            boundary.Key.PartIndex,
            out var storedEndAdjustment);
        var followingStraightParts = scene.GetPathBezierSegmentParts(rectangle)
            .Where(part => part.PartIndex != boundary.Key.PartIndex
                && PointsNear(part.Start, adjustedEnd))
            .ToArray();
        var followingStraightPart = followingStraightParts.FirstOrDefault();
        var expectedFollowingControl1 = new PointF(
            adjustedEnd.X + (followingStraightPart.End.X - adjustedEnd.X) / 3f,
            adjustedEnd.Y + (followingStraightPart.End.Y - adjustedEnd.Y) / 3f);
        var expectedFollowingControl2 = new PointF(
            adjustedEnd.X + (followingStraightPart.End.X - adjustedEnd.X) * 2f / 3f,
            adjustedEnd.Y + (followingStraightPart.End.Y - adjustedEnd.Y) * 2f / 3f);
        if (!adjustedEndApplied
            || !hasStoredEndAdjustment
            || !PointsNear(straightEndAdjustment.Start, start)
            || !PointsNear(straightEndAdjustment.Control1, expectedEndControl1)
            || !PointsNear(straightEndAdjustment.Control2, expectedEndControl2)
            || !PointsNear(straightEndAdjustment.End, adjustedEnd)
            || !PointsNear(storedEndAdjustment.Control1, VectorUnits.Quantize(expectedEndControl1))
            || !PointsNear(storedEndAdjustment.Control2, VectorUnits.Quantize(expectedEndControl2))
            || followingStraightParts.Length != 1
            || !PointsNear(followingStraightPart.Control1, VectorUnits.Quantize(expectedFollowingControl1))
            || !PointsNear(followingStraightPart.Control2, VectorUnits.Quantize(expectedFollowingControl2)))
        {
            throw new InvalidOperationException(
                "Moving the opposite straight fill-boundary endpoint bent the active or following edge.");
        }

        var curvedControl1 = new PointF(control1.X, control1.Y - 360);
        var curvedControl2 = new PointF(control2.X, control2.Y + 240);
        var curvedEndpointAdjustment = MainForm.AdjustFillEdgeBezierHandle(
            start,
            curvedControl1,
            curvedControl2,
            end,
            EditHandleKind.LineEnd,
            new PointF(end.X + 180, end.Y + 120));
        if (!PointsNear(curvedEndpointAdjustment.Control1, curvedControl1)
            || !PointsNear(curvedEndpointAdjustment.Control2, curvedControl2))
        {
            throw new InvalidOperationException(
                "Moving a curved fill-boundary endpoint incorrectly flattened its existing controls.");
        }

        scene.RestoreSnapshot(rectangleSnapshot);
        if (!scene.TryConvertFillToBezierPath(rectangle)
            || !scene.TryGetPathBezierSegment(rectangle, boundary.Key.PartIndex, out var insertionSource))
        {
            throw new InvalidOperationException("Fill-edge anchor insertion regression could not prepare a straight cubic.");
        }

        const float straightInsertionParameter = 0.4f;
        var expectedStraightAnchor = VectorUnits.Quantize(Mix(
            insertionSource.Start,
            insertionSource.End,
            straightInsertionParameter));
        var rectangleOrder = scene.ObjectOrder[rectangle];
        var rectangleArgb = scene.Argb[rectangle];
        var rectangleAtoms = scene.AtomCount[rectangle];
        if (!scene.TryInsertPathBezierAnchor(
                rectangle,
                insertionSource.PartIndex,
                straightInsertionParameter,
                out var insertedStraightPartIndex,
                out var insertedStraightAnchor)
            || scene.ObjectCount != 1
            || scene.GetPathBezierSegmentParts(rectangle).Length != 5
            || !PointsNear(insertedStraightAnchor, expectedStraightAnchor)
            || !scene.TryGetPathBezierSegment(rectangle, insertionSource.PartIndex, out var firstStraightSplit)
            || !scene.TryGetPathBezierSegment(rectangle, insertedStraightPartIndex, out var secondStraightSplit)
            || !VectorScene.IsStraightBezierSegment(
                firstStraightSplit.Start,
                firstStraightSplit.Control1,
                firstStraightSplit.Control2,
                firstStraightSplit.End)
            || !VectorScene.IsStraightBezierSegment(
                secondStraightSplit.Start,
                secondStraightSplit.Control1,
                secondStraightSplit.Control2,
                secondStraightSplit.End)
            || !scene.TryDeletePathBezierAnchor(
                rectangle,
                insertedStraightPartIndex,
                startEndpoint: true,
                out var restoredStraightPartIndex)
            || scene.GetPathBezierSegmentParts(rectangle).Length != 4
            || !scene.TryGetPathBezierSegment(rectangle, restoredStraightPartIndex, out var restoredStraightPart)
            || !PointsNear(restoredStraightPart.Start, insertionSource.Start)
            || !PointsNear(restoredStraightPart.End, insertionSource.End)
            || !VectorScene.IsStraightBezierSegment(
                restoredStraightPart.Start,
                restoredStraightPart.Control1,
                restoredStraightPart.Control2,
                restoredStraightPart.End)
            || scene.ObjectOrder[rectangle] != rectangleOrder
            || scene.Argb[rectangle] != rectangleArgb
            || scene.AtomCount[rectangle] != rectangleAtoms)
        {
            throw new InvalidOperationException(
                "Adding and deleting a straight fill-edge anchor changed its line geometry or object metadata.");
        }

        scene.RestoreSnapshot(rectangleSnapshot);
        if (!scene.TryConvertFillToBezierPath(rectangle)
            || !scene.TryGetPathBezierSegment(rectangle, boundary.Key.PartIndex, out var cornerPrevious))
        {
            throw new InvalidOperationException("Fill-edge anchor deletion regression could not prepare a rectangle corner.");
        }

        var cornerFollowing = scene.GetPathBezierSegmentParts(rectangle)
            .Single(part => part.PartIndex != cornerPrevious.PartIndex
                && PointsNear(part.Start, cornerPrevious.End));
        if (!scene.TryDeletePathBezierAnchor(
                rectangle,
                cornerPrevious.PartIndex,
                startEndpoint: false,
                out var mergedCornerPartIndex)
            || !scene.TryGetPathBezierWorldContours(rectangle, out var triangleContours)
            || triangleContours.Length != 1
            || triangleContours[0].Length != 3
            || !scene.TryGetPathBezierSegment(rectangle, mergedCornerPartIndex, out var mergedCorner)
            || !PointsNear(mergedCorner.Start, cornerPrevious.Start)
            || !PointsNear(mergedCorner.End, cornerFollowing.End)
            || !VectorScene.IsStraightBezierSegment(
                mergedCorner.Start,
                mergedCorner.Control1,
                mergedCorner.Control2,
                mergedCorner.End)
            || scene.TryDeletePathBezierAnchor(
                rectangle,
                mergedCornerPartIndex,
                startEndpoint: false,
                out _))
        {
            throw new InvalidOperationException(
                "Deleting a straight fill corner did not create one straight replacement edge or enforce the three-anchor minimum.");
        }

        var fillScene = new VectorScene();
        fillScene.CreateEmpty();
        var fill = fillScene.AddPathObject(
            0,
            [
                new PointF(-240, -100),
                new PointF(40, -180),
                new PointF(260, -40),
                new PointF(180, 190),
                new PointF(-180, 170)
            ],
            0,
            Color.Teal,
            Color.Transparent,
            12);
        var originalSnapshot = fillScene.CreateSnapshot();
        var originalCount = fillScene.ObjectCount;
        var originalOrder = fillScene.ObjectOrder[fill];
        var originalArgb = fillScene.Argb[fill];
        var originalParts = fillScene.GetEditableFillBezierSegmentParts(fill);
        var wholeFillHit = new DrawingElementHit(
            new DrawingElementKey(fill, DrawingElementKind.Fill, 0),
            0,
            0,
            1);
        if (originalParts.Length != 5
            || !MainForm.IsWholeFillElementSelection(fillScene, 0, fill, [wholeFillHit])
            || MainForm.IsWholeFillElementSelection(fillScene, 0, fill, [DrawingElementHit.None])
            || fillScene.TryGetPathBezierWorldContours(fill, out _)
            || !fillScene.TryConvertFillToBezierPath(fill)
            || fillScene.ObjectCount != originalCount
            || fillScene.ObjectOrder[fill] != originalOrder
            || fillScene.Argb[fill] != originalArgb
            || fillScene.Stroke[fill] != 0
            || !fillScene.TryGetPathBezierSegment(fill, 0, out var topSegment))
        {
            throw new InvalidOperationException("A no-stroke irregular fill did not convert in place to editable cubic edges.");
        }

        var midpoint = new PointF(
            (topSegment.Start.X + topSegment.End.X) * 0.5f,
            (topSegment.Start.Y + topSegment.End.Y) * 0.5f);
        var outwardX = midpoint.X - fillScene.X[fill];
        var outwardY = midpoint.Y - fillScene.Y[fill];
        var outwardLength = MathF.Max(0.001f, MathF.Sqrt(outwardX * outwardX + outwardY * outwardY));
        var bulge = new PointF(outwardX / outwardLength * 320, outwardY / outwardLength * 320);
        var fillControl1 = new PointF(topSegment.Control1.X + bulge.X, topSegment.Control1.Y + bulge.Y);
        var fillControl2 = new PointF(topSegment.Control2.X + bulge.X, topSegment.Control2.Y + bulge.Y);
        if (!fillScene.SetPathBezierSegment(
                fill,
                topSegment.PartIndex,
                topSegment.Start,
                fillControl1,
                fillControl2,
                topSegment.End)
            || !fillScene.TryGetPathBezierSegment(fill, topSegment.PartIndex, out var editedSegment)
            || !PointsNear(editedSegment.Control1, VectorUnits.Quantize(fillControl1))
            || !PointsNear(editedSegment.Control2, VectorUnits.Quantize(fillControl2)))
        {
            throw new InvalidOperationException("Editing an irregular fill edge did not preserve its independent cubic controls.");
        }

        var curvedInsertionSnapshot = fillScene.CreateSnapshot();
        const float curvedQueryParameter = 0.37f;
        var queryP01 = Mix(editedSegment.Start, editedSegment.Control1, curvedQueryParameter);
        var queryP12 = Mix(editedSegment.Control1, editedSegment.Control2, curvedQueryParameter);
        var queryP23 = Mix(editedSegment.Control2, editedSegment.End, curvedQueryParameter);
        var curvedQueryPoint = Mix(
            Mix(queryP01, queryP12, curvedQueryParameter),
            Mix(queryP12, queryP23, curvedQueryParameter),
            curvedQueryParameter);
        if (!fillScene.TryGetClosestPointOnPathBezierSegment(
                fill,
                editedSegment.PartIndex,
                curvedQueryPoint,
                out var curvedInsertionParameter,
                out _,
                out var curvedInsertionDistance)
            || Math.Abs(curvedInsertionParameter - curvedQueryParameter) > 0.01f
            || curvedInsertionDistance > 1.5f)
        {
            throw new InvalidOperationException("Fill-edge click positioning did not recover the nearest cubic parameter.");
        }

        var p01 = VectorUnits.Quantize(Mix(editedSegment.Start, editedSegment.Control1, curvedInsertionParameter));
        var p12 = VectorUnits.Quantize(Mix(editedSegment.Control1, editedSegment.Control2, curvedInsertionParameter));
        var p23 = VectorUnits.Quantize(Mix(editedSegment.Control2, editedSegment.End, curvedInsertionParameter));
        var p012 = VectorUnits.Quantize(Mix(p01, p12, curvedInsertionParameter));
        var p123 = VectorUnits.Quantize(Mix(p12, p23, curvedInsertionParameter));
        var expectedCurvedAnchor = VectorUnits.Quantize(Mix(p012, p123, curvedInsertionParameter));
        if (!fillScene.TryInsertPathBezierAnchor(
                fill,
                editedSegment.PartIndex,
                curvedInsertionParameter,
                out var insertedCurvedPartIndex,
                out var insertedCurvedAnchor)
            || !fillScene.TryGetPathBezierSegment(fill, editedSegment.PartIndex, out var firstCurvedSplit)
            || !fillScene.TryGetPathBezierSegment(fill, insertedCurvedPartIndex, out var secondCurvedSplit)
            || fillScene.GetPathBezierSegmentParts(fill).Length != 6
            || !PointsNear(insertedCurvedAnchor, expectedCurvedAnchor)
            || !PointsNear(firstCurvedSplit.Start, editedSegment.Start)
            || !PointsNear(firstCurvedSplit.Control1, p01)
            || !PointsNear(firstCurvedSplit.Control2, p012)
            || !PointsNear(firstCurvedSplit.End, expectedCurvedAnchor)
            || !PointsNear(secondCurvedSplit.Start, expectedCurvedAnchor)
            || !PointsNear(secondCurvedSplit.Control1, p123)
            || !PointsNear(secondCurvedSplit.Control2, p23)
            || !PointsNear(secondCurvedSplit.End, editedSegment.End))
        {
            throw new InvalidOperationException("Adding a fill-edge anchor did not preserve the exact cubic subdivision.");
        }

        var insertedInitialSegment = new CubicDrawingPreviewSegment(
            secondCurvedSplit.Start,
            secondCurvedSplit.Control1,
            secondCurvedSplit.Control2,
            secondCurvedSplit.End);
        var draggedCurvedAnchor = VectorUnits.Quantize(new PointF(
            insertedCurvedAnchor.X + 85,
            insertedCurvedAnchor.Y + 65));
        var draggedInsertedSegment = MainForm.AdjustFillEdgeBezierHandle(
            insertedInitialSegment.Start,
            insertedInitialSegment.Control1,
            insertedInitialSegment.Control2,
            insertedInitialSegment.End,
            EditHandleKind.LineStart,
            draggedCurvedAnchor);
        var offsetHandle = MainForm.MoveHandleWithPointer(
            new PointF(100, 80),
            new PointF(96, 83),
            new PointF(101, 89));
        var unchangedGeometryRevision = fillScene.GeometryRevision;
        var unchangedSummaryRevision = fillScene.SummaryRevision;
        var subUnitSegment = new CubicDrawingPreviewSegment(
            VectorUnits.Quantize(new PointF(
                insertedInitialSegment.Start.X + 0.49f,
                insertedInitialSegment.Start.Y - 0.49f)),
            VectorUnits.Quantize(new PointF(
                insertedInitialSegment.Control1.X - 0.49f,
                insertedInitialSegment.Control1.Y + 0.49f)),
            VectorUnits.Quantize(new PointF(
                insertedInitialSegment.Control2.X + 0.49f,
                insertedInitialSegment.Control2.Y - 0.49f)),
            VectorUnits.Quantize(new PointF(
                insertedInitialSegment.End.X - 0.49f,
                insertedInitialSegment.End.Y + 0.49f)));
        var nextUnitSegment = subUnitSegment with
        {
            Start = VectorUnits.Quantize(new PointF(
                insertedInitialSegment.Start.X + 0.51f,
                insertedInitialSegment.Start.Y))
        };
        var unchangedSegmentAccepted = fillScene.SetPathBezierSegment(
            fill,
            insertedCurvedPartIndex,
            new PointF(insertedInitialSegment.Start.X + 0.49f, insertedInitialSegment.Start.Y - 0.49f),
            new PointF(insertedInitialSegment.Control1.X - 0.49f, insertedInitialSegment.Control1.Y + 0.49f),
            new PointF(insertedInitialSegment.Control2.X + 0.49f, insertedInitialSegment.Control2.Y - 0.49f),
            new PointF(insertedInitialSegment.End.X - 0.49f, insertedInitialSegment.End.Y + 0.49f));
        var resetCurvature = MainForm.ResetBezierCurvature(
            new PointF(0, 0),
            new PointF(90, 60));
        if (!MainForm.ShouldCommitFillEdgeBezierPointer(
                includesAnchorInsertion: true,
                insertedInitialSegment,
                insertedInitialSegment)
            || MainForm.ShouldCommitFillEdgeBezierPointer(
                includesAnchorInsertion: false,
                insertedInitialSegment,
                insertedInitialSegment)
            || !MainForm.ShouldCommitFillEdgeBezierPointer(
                includesAnchorInsertion: false,
                insertedInitialSegment,
                draggedInsertedSegment)
            || MainForm.ShouldApplyFillEdgeBezierPointer(
                insertedInitialSegment,
                insertedInitialSegment)
            || MainForm.ShouldApplyFillEdgeBezierPointer(
                insertedInitialSegment,
                subUnitSegment)
            || !MainForm.ShouldApplyFillEdgeBezierPointer(
                insertedInitialSegment,
                draggedInsertedSegment)
            || !MainForm.ShouldApplyFillEdgeBezierPointer(
                subUnitSegment,
                nextUnitSegment)
            || MainForm.ShouldApplyFillEdgeBezierPointer(
                nextUnitSegment,
                nextUnitSegment)
            || !MainForm.ShouldUpdateFillEdgeBezierPointer(
                MouseButtons.Left,
                includesAnchorInsertion: true,
                pointerMoved: false)
            || MainForm.ShouldUpdateFillEdgeBezierPointer(
                MouseButtons.Left,
                includesAnchorInsertion: false,
                pointerMoved: false)
            || !MainForm.ShouldUpdateFillEdgeBezierPointer(
                MouseButtons.Left,
                includesAnchorInsertion: false,
                pointerMoved: true)
            || MainForm.ShouldUpdateFillEdgeBezierPointer(
                MouseButtons.Right,
                includesAnchorInsertion: true,
                pointerMoved: true)
            || !MainForm.ShouldUpdateSelectionPointer(
                EditHandleKind.LineStart,
                pointerMoved: true,
                dragThresholdExceeded: false)
            || MainForm.ShouldUpdateSelectionPointer(
                EditHandleKind.LineStart,
                pointerMoved: false,
                dragThresholdExceeded: true)
            || MainForm.ShouldUpdateSelectionPointer(
                EditHandleKind.None,
                pointerMoved: true,
                dragThresholdExceeded: false)
            || !MainForm.ShouldUpdateSelectionPointer(
                EditHandleKind.None,
                pointerMoved: true,
                dragThresholdExceeded: true)
            || !MainForm.FillEdgeOverlayOwnsPointer(
                new FillEdgeBezierOverlayHit(0, EditHandleKind.BezierControl),
                selectableLineHit: true)
            || MainForm.FillEdgeOverlayOwnsPointer(
                new FillEdgeBezierOverlayHit(0, EditHandleKind.None),
                selectableLineHit: true)
            || !MainForm.FillEdgeOverlayOwnsPointer(
                new FillEdgeBezierOverlayHit(0, EditHandleKind.None),
                selectableLineHit: false)
            || MainForm.FillEdgeOverlayOwnsPointer(
                FillEdgeBezierOverlayHit.None,
                selectableLineHit: false)
            || !MainForm.IsBezierCurvatureResetGesture(
                MouseButtons.Left,
                altPressed: true,
                EditHandleKind.BezierControl)
            || !MainForm.IsBezierCurvatureResetGesture(
                MouseButtons.Left,
                altPressed: true,
                EditHandleKind.BezierControl2)
            || MainForm.IsBezierCurvatureResetGesture(
                MouseButtons.Left,
                altPressed: false,
                EditHandleKind.BezierControl)
            || MainForm.IsBezierCurvatureResetGesture(
                MouseButtons.Right,
                altPressed: true,
                EditHandleKind.BezierControl)
            || MainForm.IsBezierCurvatureResetGesture(
                MouseButtons.Left,
                altPressed: true,
                EditHandleKind.LineStart)
            || !PointsWithin(resetCurvature.Start, new PointF(0, 0), 0.01f)
            || !PointsWithin(resetCurvature.Control1, new PointF(30, 20), 0.01f)
            || !PointsWithin(resetCurvature.Control2, new PointF(60, 40), 0.01f)
            || !PointsWithin(resetCurvature.End, new PointF(90, 60), 0.01f)
            || !PointsWithin(offsetHandle, new PointF(105, 86), 0.01f)
            || !unchangedSegmentAccepted
            || fillScene.GeometryRevision != unchangedGeometryRevision
            || fillScene.SummaryRevision != unchangedSummaryRevision
            || !fillScene.SetPathBezierSegment(
                fill,
                insertedCurvedPartIndex,
                draggedInsertedSegment.Start,
                draggedInsertedSegment.Control1,
                draggedInsertedSegment.Control2,
                draggedInsertedSegment.End,
                preserveStraightAdjacentSegments: true)
            || !fillScene.TryGetPathBezierSegment(fill, editedSegment.PartIndex, out var draggedFirstSplit)
            || !fillScene.TryGetPathBezierSegment(fill, insertedCurvedPartIndex, out var draggedSecondSplit)
            || fillScene.GetPathBezierSegmentParts(fill).Length != 6
            || !PointsNear(draggedFirstSplit.End, draggedCurvedAnchor)
            || !PointsNear(draggedSecondSplit.Start, draggedCurvedAnchor))
        {
            throw new InvalidOperationException(
                "Dragging immediately after fill-edge anchor insertion did not preserve one commit or the shared anchor.");
        }
        fillScene.RestoreSnapshot(curvedInsertionSnapshot);

        var curvedSnapshot = fillScene.CreateSnapshot();
        fillScene.SetPathBezierSegment(
            fill,
            topSegment.PartIndex,
            topSegment.Start,
            new PointF(fillControl1.X + 100, fillControl1.Y),
            fillControl2,
            topSegment.End);
        fillScene.RestoreSnapshot(curvedSnapshot);
        if (!fillScene.TryGetPathBezierSegment(fill, topSegment.PartIndex, out var restoredSegment)
            || !PointsNear(restoredSegment.Control1, editedSegment.Control1)
            || !PointsNear(restoredSegment.Control2, editedSegment.Control2))
        {
            throw new InvalidOperationException("Snapshot restore lost exact fill-edge cubic controls.");
        }

        using var fillStage = new StageControl(fillScene) { Size = new Size(640, 420) };
        fillStage.SetVisibleWorldWidth(800);
        var overlaySegments = fillScene.GetEditableFillBezierSegmentParts(fill)
            .Select(part => new FillEdgeBezierOverlaySegment(
                part.PartIndex,
                part.Start,
                part.Control1,
                part.Control2,
                part.End))
            .ToArray();
        fillStage.SetSelection([fill], fill);
        fillStage.SetFillEdgeBezierOverlay(fill, overlaySegments, restoredSegment.PartIndex);
        var unchangedOverlayRevision = fillStage.FillEdgeBezierOverlayRevision;
        fillStage.SetFillEdgeBezierOverlay(fill, overlaySegments, restoredSegment.PartIndex);
        var controlHit = fillStage.HitTestFillEdgeBezierOverlay(
            Point.Round(fillStage.WorldToScreen(restoredSegment.Control1.X, restoredSegment.Control1.Y)));
        var startHit = fillStage.HitTestFillEdgeBezierOverlay(
            Point.Round(fillStage.WorldToScreen(restoredSegment.Start.X, restoredSegment.Start.Y)));
        var endHit = fillStage.HitTestFillEdgeBezierOverlay(
            Point.Round(fillStage.WorldToScreen(restoredSegment.End.X, restoredSegment.End.Y)));
        var curveP01 = Mix(restoredSegment.Start, restoredSegment.Control1, 0.5f);
        var curveP12 = Mix(restoredSegment.Control1, restoredSegment.Control2, 0.5f);
        var curveP23 = Mix(restoredSegment.Control2, restoredSegment.End, 0.5f);
        var curveMidpoint = Mix(Mix(curveP01, curveP12, 0.5f), Mix(curveP12, curveP23, 0.5f), 0.5f);
        var curveHit = fillStage.HitTestFillEdgeBezierOverlay(
            Point.Round(fillStage.WorldToScreen(curveMidpoint.X, curveMidpoint.Y)));
        var inactiveSegment = overlaySegments.First(segment =>
            segment.PartIndex != restoredSegment.PartIndex
            && !PointsNear(segment.Start, restoredSegment.Start)
            && !PointsNear(segment.Start, restoredSegment.End));
        var inactiveAnchorHit = fillStage.HitTestFillEdgeBezierOverlay(
            Point.Round(fillStage.WorldToScreen(inactiveSegment.Start.X, inactiveSegment.Start.Y)));
        var inactiveControlHit = fillStage.HitTestFillEdgeBezierOverlay(
            Point.Round(fillStage.WorldToScreen(inactiveSegment.Control1.X, inactiveSegment.Control1.Y)));
        if (!fillStage.FillEdgeBezierOverlayVisible
            || fillStage.FillEdgeBezierOverlayTargetObject != fill
            || fillStage.FillEdgeBezierOverlayActivePartIndex != restoredSegment.PartIndex
            || fillStage.FillEdgeBezierOverlayRevision != unchangedOverlayRevision
            || controlHit.PartIndex != restoredSegment.PartIndex
            || controlHit.Handle != EditHandleKind.BezierControl
            || startHit.Handle != EditHandleKind.LineStart
            || endHit.Handle != EditHandleKind.LineEnd
            || curveHit.PartIndex != restoredSegment.PartIndex
            || curveHit.Handle != EditHandleKind.None
            || !inactiveAnchorHit.IsValid
            || inactiveAnchorHit.Handle is not (EditHandleKind.LineStart or EditHandleKind.LineEnd)
            || inactiveControlHit.PartIndex != inactiveSegment.PartIndex
            || inactiveControlHit.Handle != EditHandleKind.BezierControl
            || !MainForm.IsFillEdgeBezierAnchorInsertionGesture(MouseButtons.Left, controlPressed: true, curveHit)
            || MainForm.IsFillEdgeBezierAnchorInsertionGesture(MouseButtons.Left, controlPressed: false, curveHit)
            || MainForm.IsFillEdgeBezierAnchorInsertionGesture(MouseButtons.Right, controlPressed: true, curveHit)
            || MainForm.IsFillEdgeBezierAnchorInsertionGesture(MouseButtons.Left, controlPressed: true, controlHit)
            || MainForm.IsFillEdgeBezierAnchorInsertionGesture(MouseButtons.Left, controlPressed: true, startHit)
            || !MainForm.IsFillEdgeBezierAnchorDeletionGesture(MouseButtons.Right, controlPressed: true, startHit)
            || !MainForm.IsFillEdgeBezierAnchorDeletionGesture(MouseButtons.Right, controlPressed: true, endHit)
            || MainForm.IsFillEdgeBezierAnchorDeletionGesture(MouseButtons.Right, controlPressed: false, startHit)
            || MainForm.IsFillEdgeBezierAnchorDeletionGesture(MouseButtons.Left, controlPressed: true, startHit)
            || MainForm.IsFillEdgeBezierAnchorDeletionGesture(MouseButtons.Right, controlPressed: true, curveHit)
            || MainForm.IsFillEdgeBezierAnchorDeletionGesture(MouseButtons.Right, controlPressed: true, controlHit)
            || fillStage.SelectionHighlightAnimating)
        {
            throw new InvalidOperationException(
                $"The fill-edge cubic overlay or Ctrl anchor gestures were inconsistent: active={restoredSegment.PartIndex}, curve={curveHit}, control={controlHit}, start={startHit}, end={endHit}, inactive={inactiveSegment.PartIndex}, anchor={inactiveAnchorHit}, inactiveControl={inactiveControlHit}.");
        }

        var ownedOverlaySegments = (FillEdgeBezierOverlaySegment[])overlaySegments.Clone();
        fillStage.SetOwnedFillEdgeBezierOverlay(fill, ownedOverlaySegments, restoredSegment.PartIndex);
        var ownedNoOpRevision = fillStage.FillEdgeBezierOverlayRevision;
        for (var iteration = 0; iteration < 16; iteration++)
        {
            fillStage.SetOwnedFillEdgeBezierOverlay(fill, ownedOverlaySegments, restoredSegment.PartIndex);
        }

        var ownedNoOpAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var ownedNoOpWatch = Stopwatch.StartNew();
        for (var iteration = 0; iteration < 1024; iteration++)
        {
            fillStage.SetOwnedFillEdgeBezierOverlay(fill, ownedOverlaySegments, restoredSegment.PartIndex);
        }
        ownedNoOpWatch.Stop();
        var ownedNoOpAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - ownedNoOpAllocatedBefore;
        var ownedNoOpAverageMilliseconds = ownedNoOpWatch.Elapsed.TotalMilliseconds / 1024;
        if (fillStage.FillEdgeBezierOverlayRevision != ownedNoOpRevision
            || ownedNoOpAllocatedBytes > 4096
            || ownedNoOpAverageMilliseconds > 0.05)
        {
            throw new InvalidOperationException(
                $"An unchanged owned fill-edge overlay repeated work: revision={ownedNoOpRevision}->{fillStage.FillEdgeBezierOverlayRevision}, allocated={ownedNoOpAllocatedBytes}, averageMs={ownedNoOpAverageMilliseconds:0.0000}.");
        }

        var changedOwnedOverlaySegments = (FillEdgeBezierOverlaySegment[])overlaySegments.Clone();
        changedOwnedOverlaySegments[0] = changedOwnedOverlaySegments[0] with
        {
            Control1 = new PointF(
                changedOwnedOverlaySegments[0].Control1.X + 1,
                changedOwnedOverlaySegments[0].Control1.Y)
        };
        fillStage.SetOwnedFillEdgeBezierOverlay(fill, changedOwnedOverlaySegments, restoredSegment.PartIndex);
        if (fillStage.FillEdgeBezierOverlayRevision != ownedNoOpRevision + 1
            || !ReferenceEquals(changedOwnedOverlaySegments, fillStage.FillEdgeBezierOverlaySegments))
        {
            throw new InvalidOperationException("The Stage did not adopt one changed owned fill-edge overlay array exactly once.");
        }

        Console.WriteLine($"fill_edge_overlay_noop_allocated_bytes={ownedNoOpAllocatedBytes}");
        Console.WriteLine($"fill_edge_overlay_noop_avg_ms={ownedNoOpAverageMilliseconds:0.0000}");
        var clearOverlayRevision = fillStage.FillEdgeBezierOverlayRevision;
        fillStage.ClearFillEdgeBezierOverlay();
        var clearedOverlayRevision = fillStage.FillEdgeBezierOverlayRevision;
        fillStage.ClearFillEdgeBezierOverlay();
        if (fillStage.FillEdgeBezierOverlayVisible
            || clearedOverlayRevision != clearOverlayRevision + 1
            || fillStage.FillEdgeBezierOverlayRevision != clearedOverlayRevision)
        {
            throw new InvalidOperationException("The fill-edge cubic overlay did not clear its editing state.");
        }

        const int shortPartIndex = 97;
        var shortSegment = new FillEdgeBezierOverlaySegment(
            shortPartIndex,
            new PointF(0, 0),
            new PointF(10, 0),
            new PointF(20, 0),
            new PointF(30, 0));
        fillStage.SetFillEdgeBezierOverlay(fill, [shortSegment], shortPartIndex);
        var shortStartHit = fillStage.HitTestFillEdgeBezierOverlay(
            Point.Round(fillStage.WorldToScreen(shortSegment.Start.X, shortSegment.Start.Y)));
        var shortControlHit = fillStage.HitTestFillEdgeBezierOverlay(
            Point.Round(fillStage.WorldToScreen(shortSegment.Control1.X, shortSegment.Control1.Y)));
        var shortEndHit = fillStage.HitTestFillEdgeBezierOverlay(
            Point.Round(fillStage.WorldToScreen(shortSegment.End.X, shortSegment.End.Y)));
        if (shortStartHit.Handle != EditHandleKind.LineStart
            || shortControlHit.Handle != EditHandleKind.BezierControl
            || shortEndHit.Handle != EditHandleKind.LineEnd)
        {
            throw new InvalidOperationException(
                $"Overlapping short-segment handles did not choose the nearest anchor/control: start={shortStartHit}, control={shortControlHit}, end={shortEndHit}.");
        }
        fillStage.ClearFillEdgeBezierOverlay();

        var coverageScene = new VectorScene();
        coverageScene.CreateEmpty();
        var coverageFill = coverageScene.AddPathObject(
            0,
            [
                new PointF(-220, -120),
                new PointF(220, -120),
                new PointF(220, 120),
                new PointF(-220, 120)
            ],
            0,
            Color.Teal,
            Color.Transparent,
            12);
        var coverageParts = coverageScene.GetEditableFillBezierSegmentParts(coverageFill);
        foreach (var part in coverageParts.Take(2))
        {
            coverageScene.AddCubicCurveSegment(
                0,
                part.Start,
                part.Control1,
                part.Control2,
                part.End,
                VectorUnits.StrokePointsToUnits(3),
                Color.Transparent,
                Color.White,
                6);
        }

        var exposedParts = coverageScene.GetExposedFillBezierSegmentPieces(coverageFill, 0);
        using var coverageStage = new StageControl(coverageScene) { Size = new Size(640, 420) };
        coverageStage.SetVisibleWorldWidth(800);
        coverageStage.SetSelection([coverageFill], coverageFill);
        coverageStage.SetFillEdgeBezierOverlay(
            coverageFill,
            exposedParts.Select(part => new FillEdgeBezierOverlaySegment(
                part.PieceIndex,
                part.Start,
                part.Control1,
                part.Control2,
                part.End)).ToArray(),
            exposedParts.FirstOrDefault().PieceIndex);
        var coveredControlHit = coverageStage.HitTestFillEdgeBezierOverlay(
            Point.Round(coverageStage.WorldToScreen(
                coverageParts[0].Control1.X,
                coverageParts[0].Control1.Y)));
        var exposedControlHit = coverageStage.HitTestFillEdgeBezierOverlay(
            Point.Round(coverageStage.WorldToScreen(
                exposedParts[0].Control1.X,
                exposedParts[0].Control1.Y)));
        if (coverageParts.Length != 4
            || exposedParts.Length != 2
            || exposedParts.Any(part => part.SourcePartIndex is 0 or 1)
            || coveredControlHit.IsValid
            || exposedControlHit.PartIndex != exposedParts[0].PieceIndex
            || exposedControlHit.Handle != EditHandleKind.BezierControl)
        {
            throw new InvalidOperationException(
                $"A partially outlined fill did not expose only its open edges: total={coverageParts.Length}, exposed={string.Join(',', exposedParts.Select(part => part.SourcePartIndex))}, coveredHit={coveredControlHit}, exposedHit={exposedControlHit}.");
        }

        var partitionScene = new VectorScene();
        partitionScene.CreateEmpty();
        var partitionFill = partitionScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(440, 240),
            0,
            0,
            Color.Teal,
            Color.Transparent,
            12,
            ShapeKind.Rectangle);
        var partitionStroke = partitionScene.AddFreehandStroke(
            0,
            [
                new PointF(-340, -240),
                new PointF(60, -240),
                new PointF(60, 40),
                new PointF(-340, 40),
                new PointF(-340, -240)
            ],
            VectorUnits.StrokePointsToUnits(3),
            Color.White,
            brushStroke: false,
            12);
        var partitionSnapshot = partitionScene.CreateSnapshot();
        var wholeBoundaryPieces = partitionScene.GetExposedFillBezierSegmentPieces(partitionFill, 0);
        var wholeLeftPieces = wholeBoundaryPieces
            .Where(part => part.SourcePartIndex == 3)
            .OrderBy(part => part.StartT)
            .ToArray();
        var lowerLeftPiece = wholeLeftPieces.FirstOrDefault(part =>
            PointsWithin(part.Start, new PointF(-220, 120), 1.5f)
            && PointsWithin(part.End, new PointF(-220, 40), 1.5f));
        PathBezierSegmentPart isolatedLowerLeft = default;
        PathBezierSegmentPart unchangedUpperLeft = default;
        PathBezierSegmentPart movedLowerLeft = default;
        PathBezierSegmentPart movedUpperLeft = default;
        if (wholeLeftPieces.Length != 2
            || !lowerLeftPiece.EndIsVirtualAnchor
            || !partitionScene.TryConvertFillToBezierPath(partitionFill)
            || !partitionScene.TryMaterializePathBezierSegmentInterval(
                partitionFill,
                lowerLeftPiece.SourcePartIndex,
                lowerLeftPiece.StartT,
                lowerLeftPiece.EndT,
                out var isolatedLowerLeftPart)
            || !partitionScene.TryGetPathBezierSegment(
                partitionFill,
                isolatedLowerLeftPart,
                out isolatedLowerLeft)
            || !partitionScene.TryGetPathBezierSegment(
                partitionFill,
                isolatedLowerLeftPart + 1,
                out unchangedUpperLeft))
        {
            throw new InvalidOperationException(
                $"A whole-fill boundary edit did not preserve its line-intersection split: leftPieces={string.Join(',', wholeLeftPieces.Select(part => $"{part.StartT:F3}-{part.EndT:F3}"))}.");
        }

        var cachedPointerPieces = partitionScene.GetExposedFillBezierSegmentPieces(partitionFill, 0);
        var cachedLowerPointerPiece = cachedPointerPieces.FirstOrDefault(piece =>
            piece.SourcePartIndex == isolatedLowerLeftPart);
        var movedLowerStart = new PointF(-280, 150);
        var adjustedLowerLeft = MainForm.AdjustFillEdgeBezierHandle(
            isolatedLowerLeft.Start,
            isolatedLowerLeft.Control1,
            isolatedLowerLeft.Control2,
            isolatedLowerLeft.End,
            EditHandleKind.LineStart,
            movedLowerStart);
        var lowerLeftChanged = partitionScene.SetPathBezierSegment(
                partitionFill,
                isolatedLowerLeftPart,
                adjustedLowerLeft.Start,
                adjustedLowerLeft.Control1,
                adjustedLowerLeft.Control2,
                adjustedLowerLeft.End,
                preserveStraightAdjacentSegments: true);
        var refreshedPointerPieces = lowerLeftChanged
            ? partitionScene.RefreshFillBezierSegmentPieces(partitionFill, cachedPointerPieces)
            : [];
        var refreshedLowerPointerPiece = refreshedPointerPieces.FirstOrDefault(piece =>
            piece.PieceIndex == cachedLowerPointerPiece.PieceIndex
            && piece.SourcePartIndex == cachedLowerPointerPiece.SourcePartIndex);
        if (!lowerLeftChanged
            || !partitionScene.TryGetPathBezierSegment(
                partitionFill,
                isolatedLowerLeftPart,
                out movedLowerLeft)
            || !partitionScene.TryGetPathBezierSegment(
                partitionFill,
                isolatedLowerLeftPart + 1,
                out movedUpperLeft)
            || !PointsWithin(movedLowerLeft.Start, movedLowerStart, 0.01f)
            || !PointsWithin(movedLowerLeft.End, isolatedLowerLeft.End, 0.01f)
            || cachedPointerPieces.Length == 0
            || cachedLowerPointerPiece.SourcePartIndex != isolatedLowerLeftPart
            || refreshedPointerPieces.Length != cachedPointerPieces.Length
            || !PointsWithin(refreshedLowerPointerPiece.Start, movedLowerStart, 0.01f)
            || !PointsWithin(movedUpperLeft.Start, unchangedUpperLeft.Start, 0.01f)
            || !PointsWithin(movedUpperLeft.Control1, unchangedUpperLeft.Control1, 0.01f)
            || !PointsWithin(movedUpperLeft.Control2, unchangedUpperLeft.Control2, 0.01f)
            || !PointsWithin(movedUpperLeft.End, unchangedUpperLeft.End, 0.01f))
        {
            throw new InvalidOperationException(
                $"Dragging below a fill/line intersection changed the upper boundary: lower={movedLowerLeft}, upper={movedUpperLeft}, expectedUpper={unchangedUpperLeft}.");
        }

        for (var iteration = 0; iteration < 8; iteration++)
        {
            _ = partitionScene.RefreshFillBezierSegmentPieces(partitionFill, cachedPointerPieces);
        }

        const int fillEdgeRefreshIterations = 256;
        var fillEdgeRefreshAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var fillEdgeRefreshWatch = Stopwatch.StartNew();
        FillBezierSegmentPiece[] measuredRefreshedPieces = [];
        for (var iteration = 0; iteration < fillEdgeRefreshIterations; iteration++)
        {
            measuredRefreshedPieces = partitionScene.RefreshFillBezierSegmentPieces(
                partitionFill,
                cachedPointerPieces);
        }
        fillEdgeRefreshWatch.Stop();
        var fillEdgeRefreshAllocatedBytes = GC.GetAllocatedBytesForCurrentThread()
            - fillEdgeRefreshAllocatedBefore;
        var fillEdgeRefreshAllocatedBytesPerUpdate = fillEdgeRefreshAllocatedBytes
            / (double)fillEdgeRefreshIterations;
        var fillEdgeRefreshAverageMilliseconds = fillEdgeRefreshWatch.Elapsed.TotalMilliseconds
            / fillEdgeRefreshIterations;
        if (!measuredRefreshedPieces.SequenceEqual(refreshedPointerPieces)
            || fillEdgeRefreshAllocatedBytesPerUpdate > 4096
            || fillEdgeRefreshAverageMilliseconds > 0.5)
        {
            throw new InvalidOperationException(
                $"The lightweight fill-edge overlay refresh exceeded its budget or changed geometry: allocatedPerUpdate={fillEdgeRefreshAllocatedBytesPerUpdate:0.0}, averageMs={fillEdgeRefreshAverageMilliseconds:0.0000}.");
        }

        Console.WriteLine($"fill_edge_overlay_refresh_allocated_bytes_per_update={fillEdgeRefreshAllocatedBytesPerUpdate:0.0}");
        Console.WriteLine($"fill_edge_overlay_refresh_avg_ms={fillEdgeRefreshAverageMilliseconds:0.0000}");
        partitionScene.RestoreSnapshot(partitionSnapshot);

        var inwardReferenceContours = partitionScene.GetObjectBoundaryContours(partitionFill);
        var inwardLowerLeftPiece = partitionScene.GetExposedFillBezierSegmentPieces(partitionFill, 0)
            .FirstOrDefault(part =>
                part.SourcePartIndex == 3
                && PointsWithin(part.Start, new PointF(-220, 120), 1.5f)
                && PointsWithin(part.End, new PointF(-220, 40), 1.5f));
        var inwardCorner = new PointF(-100, 150);
        PathBezierSegmentPart inwardSegment = default;
        if (inwardLowerLeftPiece.EndIsVirtualAnchor
            && partitionScene.TryConvertFillToBezierPath(partitionFill)
            && partitionScene.TryMaterializePathBezierSegmentInterval(
                partitionFill,
                inwardLowerLeftPiece.SourcePartIndex,
                inwardLowerLeftPiece.StartT,
                inwardLowerLeftPiece.EndT,
                out var inwardPartIndex)
            && partitionScene.TryGetPathBezierSegment(partitionFill, inwardPartIndex, out var inwardSource))
        {
            var inwardAdjusted = MainForm.AdjustFillEdgeBezierHandle(
                inwardSource.Start,
                inwardSource.Control1,
                inwardSource.Control2,
                inwardSource.End,
                EditHandleKind.LineStart,
                inwardCorner);
            partitionScene.SetPathBezierSegment(
                partitionFill,
                inwardPartIndex,
                inwardAdjusted.Start,
                inwardAdjusted.Control1,
                inwardAdjusted.Control2,
                inwardAdjusted.End,
                preserveStraightAdjacentSegments: true);
            inwardSegment = inwardSource;
        }

        var inwardProbe = new PointF(-180, 0);
        var inwardProbeBeforeNormalization = partitionScene.FillContainsPoint(partitionFill, inwardProbe);
        var inwardNormalized = partitionScene.NormalizeFillBoundaryOverlaps(
            partitionFill,
            inwardReferenceContours);
        var inwardProbeAfterNormalization = partitionScene.FillContainsPoint(partitionFill, inwardProbe);
        if (!inwardLowerLeftPiece.EndIsVirtualAnchor
            || inwardSegment.End == PointF.Empty
            || !inwardProbeBeforeNormalization
            || !inwardProbeAfterNormalization)
        {
            throw new InvalidOperationException(
                $"Moving a split fill corner inward created a triangular hole: before={inwardProbeBeforeNormalization}, normalized={inwardNormalized}, after={inwardProbeAfterNormalization}.");
        }
        partitionScene.RestoreSnapshot(partitionSnapshot);

        var inwardIntersectionSelectedPart = partitionScene.HitTestElement(
            new PointF(160, 80),
            0,
            toleranceWorld: 2);
        var inwardIntersectionPiece = partitionScene.GetExposedFillBezierSegmentPieces(partitionFill, 0)
            .FirstOrDefault(part =>
                part.SourcePartIndex == 3
                && PointsWithin(part.Start, new PointF(-220, 120), 1.5f)
                && PointsWithin(part.End, new PointF(-220, 40), 1.5f));
        var inwardIntersectionAnchor = new PointF(-100, 150);
        var inwardIntersectionPartResolved = partitionScene.TryResolveFillPartForBezierSegmentPiece(
            partitionFill,
            0,
            inwardIntersectionPiece,
            out var inwardIntersectionFillPartIndex);
        var inwardIntersectionTarget = partitionFill;
        var inwardIntersectionHandle = EditHandleKind.LineEnd;
        var inwardIntersectionPartIndex = -1;
        var inwardIntersectionPrepared = inwardIntersectionSelectedPart.Key.Kind == DrawingElementKind.Fill
            && inwardIntersectionPartResolved
            && inwardIntersectionFillPartIndex == inwardIntersectionSelectedPart.Key.PartIndex
            && partitionScene.TryConvertFillToBezierPath(inwardIntersectionTarget)
            && partitionScene.TryMaterializePathBezierSegmentInterval(
                inwardIntersectionTarget,
                inwardIntersectionPiece.SourcePartIndex,
                inwardIntersectionPiece.StartT,
                inwardIntersectionPiece.EndT,
                out inwardIntersectionPartIndex);
        var inwardIntersectionReference = inwardIntersectionPrepared
            ? partitionScene.GetObjectBoundaryContours(inwardIntersectionTarget)
            : [];
        var inwardIntersection = inwardIntersectionPrepared
            ? partitionScene.CaptureStrokeIntersectionsAtFillAnchor(
                inwardIntersectionTarget,
                inwardIntersectionPartIndex,
                startEndpoint: inwardIntersectionHandle == EditHandleKind.LineStart,
                0)
            : new SharedBoundaryIntersection(PointF.Empty, [], [], [], -1, -1);
        PathBezierSegmentPart inwardIntersectionSource = default;
        var inwardIntersectionMoved = inwardIntersection.OwnerPartIndex >= 0
            && partitionScene.TryGetPathBezierSegment(
                inwardIntersection.OwnerObjectIndex,
                inwardIntersection.OwnerPartIndex,
                out inwardIntersectionSource);
        if (inwardIntersectionMoved)
        {
            var adjusted = MainForm.AdjustFillEdgeBezierHandle(
                inwardIntersectionSource.Start,
                inwardIntersectionSource.Control1,
                inwardIntersectionSource.Control2,
                inwardIntersectionSource.End,
                inwardIntersectionHandle,
                inwardIntersectionAnchor);
            inwardIntersectionMoved = partitionScene.SetPathBezierSegment(
                    inwardIntersection.OwnerObjectIndex,
                    inwardIntersection.OwnerPartIndex,
                    adjusted.Start,
                    adjusted.Control1,
                    adjusted.Control2,
                    adjusted.End,
                    rebuildGeometryIndex: false,
                    preserveStraightAdjacentSegments: true)
                && partitionScene.UpdateSharedBoundaryIntersection(
                    inwardIntersection,
                    inwardIntersectionAnchor,
                    rebuildGeometryIndex: false);
        }

        var inwardIntersectionStableMembership = partitionScene.ObjectCount == 2
            && inwardIntersection.PathAnchors.Length == 0
            && inwardIntersection.LineAnchors.Length == 0
            && inwardIntersection.FreeformAnchors.Length == 1;
        var inwardIntersectionProbe = new PointF(-80, 80);
        var inwardIntersectionPreviewFilled = inwardIntersectionMoved
            && Enumerable.Range(0, partitionScene.ObjectCount).Any(index =>
                partitionScene.IsObjectActive(index, 0)
                && partitionScene.HasFill(index)
                && partitionScene.FillContainsPoint(index, inwardIntersectionProbe));
        var inwardIntersectionFillStates = string.Join(
            "; ",
            Enumerable.Range(0, partitionScene.ObjectCount)
                .Where(index => partitionScene.IsObjectActive(index, 0) && partitionScene.HasFill(index))
                .Select(index =>
                    $"{index}/order={partitionScene.ObjectOrder[index]}/sub={partitionScene.ObjectSubOrder[index]:F3}/contains={partitionScene.FillContainsPoint(index, inwardIntersectionProbe)}"));
        var inwardIntersectionNormalized = inwardIntersectionMoved
            && partitionScene.NormalizeFillBoundaryOverlaps(
                inwardIntersection.OwnerObjectIndex,
                inwardIntersectionReference,
                rebuildGeometryIndex: false);
        partitionScene.CompleteDeferredBuild();
        var inwardIntersectionMerged = inwardIntersectionMoved
            ? partitionScene.MergeSameColorFillsAround(
                inwardIntersection.OwnerObjectIndex,
                connectNearby: true,
                frame: 0)
            : -1;
        var inwardIntersectionCommittedFilled = inwardIntersectionMoved
            && (uint)inwardIntersectionMerged < partitionScene.ObjectCount
            && partitionScene.FillContainsPoint(inwardIntersectionMerged, inwardIntersectionProbe);
        if (!inwardIntersectionPrepared
            || !inwardIntersectionMoved
            || !inwardIntersectionStableMembership
            || !inwardIntersectionPreviewFilled
            || !inwardIntersectionCommittedFilled)
        {
            throw new InvalidOperationException(
                $"Moving a local lower fill/stroke intersection inward created extra objects or a triangular hole: prepared={inwardIntersectionPrepared}, target={inwardIntersectionTarget}/{inwardIntersection.OwnerObjectIndex}:{inwardIntersection.OwnerPartIndex}, moved={inwardIntersectionMoved}, stable={inwardIntersectionStableMembership}, paths={inwardIntersection.PathAnchors.Length}, lines={inwardIntersection.LineAnchors.Length}, freeforms={inwardIntersection.FreeformAnchors.Length}, objects={partitionScene.ObjectCount}, preview={inwardIntersectionPreviewFilled}, normalized={inwardIntersectionNormalized}, merged={inwardIntersectionMerged}, committed={inwardIntersectionCommittedFilled}, fillStates=[{inwardIntersectionFillStates}].");
        }
        partitionScene.RestoreSnapshot(partitionSnapshot);

        var outerPartitionHit = partitionScene.HitTestElement(new PointF(160, 80), 0, toleranceWorld: 2);
        var innerPartitionHit = partitionScene.HitTestElement(PointF.Empty, 0, toleranceWorld: 2);
        var outerFillPartProbes = MainForm.BuildFillPartSelectionProbes(
            partitionScene.GetFillPartContours(outerPartitionHit, 0));
        var partitionFillParts = partitionScene.GetFillParts(partitionFill, 0);
        var outerBoundaryParts = outerPartitionHit.Key.Kind == DrawingElementKind.Fill
            ? partitionScene.GetExposedFillBezierSegmentPieces(
                partitionFill,
                0,
                outerPartitionHit.Key.PartIndex)
            : [];
        var innerBoundaryParts = innerPartitionHit.Key.Kind == DrawingElementKind.Fill
            ? partitionScene.GetExposedFillBezierSegmentPieces(
                partitionFill,
                0,
                innerPartitionHit.Key.PartIndex)
            : [];
        var outerTop = outerBoundaryParts.FirstOrDefault(part => part.SourcePartIndex == 0);
        var outerLeft = outerBoundaryParts.FirstOrDefault(part => part.SourcePartIndex == 3);
        var innerTop = innerBoundaryParts.FirstOrDefault(part => part.SourcePartIndex == 0);
        var innerLeft = innerBoundaryParts.FirstOrDefault(part => part.SourcePartIndex == 3);
        using var partitionStage = new StageControl(partitionScene) { Size = new Size(640, 420) };
        partitionStage.SetVisibleWorldWidth(800);
        partitionStage.SetSelection([partitionFill], partitionFill);
        partitionStage.SetFillEdgeBezierOverlay(
            partitionFill,
            outerBoundaryParts.Select(part => new FillEdgeBezierOverlaySegment(
                part.PieceIndex,
                part.Start,
                part.Control1,
                part.Control2,
                part.End)).ToArray(),
            outerBoundaryParts.FirstOrDefault().PieceIndex);
        var partitionControlHit = outerBoundaryParts.Length > 0
            ? partitionStage.HitTestFillEdgeBezierOverlay(Point.Round(partitionStage.WorldToScreen(
                outerBoundaryParts[0].Control1.X,
                outerBoundaryParts[0].Control1.Y)))
            : FillEdgeBezierOverlayHit.None;
        var outerTopIntersectionHit = partitionStage.HitTestFillEdgeBezierOverlay(
            Point.Round(partitionStage.WorldToScreen(60, -120)));
        var outerLeftIntersectionHit = partitionStage.HitTestFillEdgeBezierOverlay(
            Point.Round(partitionStage.WorldToScreen(-220, 40)));
        var omittedTopHit = partitionStage.HitTestFillEdgeBezierOverlay(
            Point.Round(partitionStage.WorldToScreen(-100, -120)));
        var omittedLeftHit = partitionStage.HitTestFillEdgeBezierOverlay(
            Point.Round(partitionStage.WorldToScreen(-220, -40)));
        var internalBoundaryHit = partitionStage.HitTestFillEdgeBezierOverlay(
            Point.Round(partitionStage.WorldToScreen(60, 0)));
        if (partitionFillParts.Length != 2
            || outerPartitionHit.Key.ObjectIndex != partitionFill
            || innerPartitionHit.Key.ObjectIndex != partitionFill
            || outerPartitionHit.Key.PartIndex == innerPartitionHit.Key.PartIndex
            || !MainForm.TryGetSelectedFillPartIndex(
                partitionFill,
                [outerPartitionHit],
                out var selectedPartitionPartIndex)
            || selectedPartitionPartIndex != outerPartitionHit.Key.PartIndex
            || MainForm.IsWholeFillElementSelection(
                partitionScene,
                0,
                partitionFill,
                [outerPartitionHit])
            || !MainForm.ShouldShowFillEdgeBezierOverlay(
                ToolMode.Select,
                1,
                hasEditableFillBoundarySelection: true)
            || outerBoundaryParts.Length != 4
            || innerBoundaryParts.Length != 2
            || outerTop.SourcePartIndex != 0
            || outerLeft.SourcePartIndex != 3
            || innerTop.SourcePartIndex != 0
            || innerLeft.SourcePartIndex != 3
            || !PointsWithin(outerTop.Start, new PointF(60, -120), 1.5f)
            || !PointsWithin(outerTop.End, new PointF(220, -120), 1.5f)
            || !PointsWithin(outerLeft.Start, new PointF(-220, 120), 1.5f)
            || !PointsWithin(outerLeft.End, new PointF(-220, 40), 1.5f)
            || !PointsWithin(innerTop.Start, new PointF(-220, -120), 1.5f)
            || !PointsWithin(innerTop.End, new PointF(60, -120), 1.5f)
            || !PointsWithin(innerLeft.Start, new PointF(-220, 40), 1.5f)
            || !PointsWithin(innerLeft.End, new PointF(-220, -120), 1.5f)
            || !outerTop.StartIsVirtualAnchor
            || !outerLeft.EndIsVirtualAnchor
            || !partitionStage.FillEdgeBezierOverlayVisible
            || partitionControlHit.Handle != EditHandleKind.BezierControl
            || outerTopIntersectionHit.PartIndex != outerTop.PieceIndex
            || outerTopIntersectionHit.Handle != EditHandleKind.LineStart
            || outerLeftIntersectionHit.PartIndex != outerLeft.PieceIndex
            || outerLeftIntersectionHit.Handle != EditHandleKind.LineEnd
            || omittedTopHit.IsValid
            || omittedLeftHit.IsValid
            || internalBoundaryHit.IsValid)
        {
            throw new InvalidOperationException(
                $"A fill part cut by an intersecting closed stroke lost its clipped source controls: regions={partitionFillParts.Length}, outerHit={outerPartitionHit}, innerHit={innerPartitionHit}, outer={string.Join(',', outerBoundaryParts.Select(part => $"{part.SourcePartIndex}:{part.StartT:F3}-{part.EndT:F3}"))}, inner={string.Join(',', innerBoundaryParts.Select(part => $"{part.SourcePartIndex}:{part.StartT:F3}-{part.EndT:F3}"))}, control={partitionControlHit}, topAnchor={outerTopIntersectionHit}, leftAnchor={outerLeftIntersectionHit}, omittedTop={omittedTopHit}, omittedLeft={omittedLeftHit}, internal={internalBoundaryHit}.");
        }

        if (!partitionScene.TryConvertFillToBezierPath(partitionFill)
            || !partitionScene.TryMaterializePathBezierSegmentInterval(
                partitionFill,
                outerTop.SourcePartIndex,
                outerTop.StartT,
                outerTop.EndT,
                out var materializedTopPart)
            || !partitionScene.TryGetPathBezierSegment(
                partitionFill,
                materializedTopPart,
                out var materializedTop)
            || partitionScene.GetPathBezierSegmentParts(partitionFill).Length != 5)
        {
            throw new InvalidOperationException("The top virtual fill-edge intersection could not be materialized as an exact editable segment.");
        }

        var materializedOuterHit = partitionScene.HitTestElement(new PointF(160, 80), 0, toleranceWorld: 2);
        var resolvedOuterSelection = MainForm.TryResolveFillPartSelection(
            partitionScene,
            0,
            partitionFill,
            outerFillPartProbes,
            out var remappedOuterSelection);
        var materializedOuterParts = resolvedOuterSelection
            ? partitionScene.GetExposedFillBezierSegmentPieces(
                partitionFill,
                0,
                remappedOuterSelection.Key.PartIndex)
            : [];
        var remappedOuterLeftIndex = Array.FindIndex(materializedOuterParts, part =>
            PointsWithin(part.Start, new PointF(-220, 120), 1.5f)
            && PointsWithin(part.End, new PointF(-220, 40), 1.5f));
        var remappedOuterLeft = remappedOuterLeftIndex >= 0
            ? materializedOuterParts[remappedOuterLeftIndex]
            : default;
        PathBezierSegmentPart materializedLeft = default;
        if (!resolvedOuterSelection
            || remappedOuterSelection.Key.Kind != DrawingElementKind.Fill
            || remappedOuterSelection.Key.ObjectIndex != partitionFill
            || remappedOuterSelection.Key.PartIndex != materializedOuterHit.Key.PartIndex
            || remappedOuterLeftIndex < 0
            || !partitionScene.TryMaterializePathBezierSegmentInterval(
                partitionFill,
                remappedOuterLeft.SourcePartIndex,
                remappedOuterLeft.StartT,
                remappedOuterLeft.EndT,
                out var materializedLeftPart)
            || !partitionScene.TryGetPathBezierSegment(
                partitionFill,
                materializedLeftPart,
                out materializedLeft)
            || partitionScene.GetPathBezierSegmentParts(partitionFill).Length != 6
            || !PointsWithin(materializedTop.Start, new PointF(60, -120), 1.5f)
            || !PointsWithin(materializedTop.End, new PointF(220, -120), 1.5f)
            || !PointsWithin(materializedLeft.Start, new PointF(-220, 120), 1.5f)
            || !PointsWithin(materializedLeft.End, new PointF(-220, 40), 1.5f))
        {
            throw new InvalidOperationException(
                $"Virtual fill-edge intersections were not isolated into exact source cubics: top={materializedTop}, left={materializedLeft}, count={partitionScene.GetPathBezierSegmentParts(partitionFill).Length}.");
        }

        partitionScene.RestoreSnapshot(partitionSnapshot);
        if (!partitionScene.TryConvertFillToBezierPath(partitionFill)
            || !partitionScene.TryMaterializePathBezierSegmentInterval(
                partitionFill,
                outerTop.SourcePartIndex,
                outerTop.StartT,
                outerTop.EndT,
                out var sharedTopPart)
            || !partitionScene.TryGetPathBezierSegment(partitionFill, sharedTopPart, out var sharedTopSegment))
        {
            throw new InvalidOperationException("The shared fill/freeform intersection could not materialize its fill anchor.");
        }

        var freeformIntersection = partitionScene.CaptureStrokeIntersectionsAtFillAnchor(
            partitionFill,
            sharedTopPart,
            startEndpoint: true,
            0);
        if (freeformIntersection.LineAnchors.Length != 0
            || freeformIntersection.FreeformAnchors.Length != 1
            || freeformIntersection.FreeformAnchors[0].ObjectIndex != partitionStroke
            || partitionScene.ObjectCount != 2
            || partitionScene.ShapeKind[partitionStroke] != ShapeKind.Freeform
            || !partitionScene.TryGetFreehandWorldPoints(partitionStroke, out var insertedFreeformPoints)
            || insertedFreeformPoints.Length != 6
            || insertedFreeformPoints[0] != insertedFreeformPoints[^1]
            || !insertedFreeformPoints.Any(point => PointsWithin(point, new PointF(60, -120), 1.5f)))
        {
            throw new InvalidOperationException(
                $"A fill-side intersection did not materialize inside the original closed Freeform: lines={freeformIntersection.LineAnchors.Length}, freeforms={freeformIntersection.FreeformAnchors.Length}, objects={partitionScene.ObjectCount}.");
        }

        var movedIntersection = new PointF(80, -140);
        var movedTopSegment = MainForm.AdjustFillEdgeBezierHandle(
            sharedTopSegment.Start,
            sharedTopSegment.Control1,
            sharedTopSegment.Control2,
            sharedTopSegment.End,
            EditHandleKind.LineStart,
            movedIntersection);
        PathBezierSegmentPart movedTop = default;
        PointF[] movedFreeformPoints = [];
        if (!partitionScene.SetPathBezierSegment(
                partitionFill,
                sharedTopPart,
                movedTopSegment.Start,
                movedTopSegment.Control1,
                movedTopSegment.Control2,
                movedTopSegment.End,
                rebuildGeometryIndex: false,
                preserveStraightAdjacentSegments: true)
            || !partitionScene.UpdateSharedBoundaryIntersection(
                freeformIntersection,
                movedIntersection)
            || !partitionScene.TryGetPathBezierSegment(partitionFill, sharedTopPart, out movedTop)
            || !partitionScene.TryGetFreehandWorldPoints(partitionStroke, out movedFreeformPoints)
            || !PointsWithin(movedTop.Start, movedIntersection, 0.01f)
            || movedFreeformPoints.Length != 6
            || movedFreeformPoints[0] != movedFreeformPoints[^1]
            || !movedFreeformPoints.Any(point => PointsWithin(point, movedIntersection, 0.01f))
            || movedFreeformPoints.Any(point => PointsWithin(point, new PointF(60, -120), 0.01f))
            || new[]
            {
                new PointF(-340, -240),
                new PointF(60, -240),
                new PointF(60, 40),
                new PointF(-340, 40)
            }.Any(corner => !movedFreeformPoints.Any(point => PointsWithin(point, corner, 0.01f))))
        {
            throw new InvalidOperationException(
                $"Dragging a fill intersection did not move the closed Freeform and fill anchor together: fill={movedTop}, freeform={string.Join(';', movedFreeformPoints.Select(point => $"{point.X:F0},{point.Y:F0}"))}.");
        }

        var repeatedFreeformIntersection = partitionScene.CaptureStrokeIntersectionsAtFillAnchor(
            partitionFill,
            sharedTopPart,
            startEndpoint: true,
            0,
            rebuildGeometryIndex: false);
        var repeatedFreeformTarget = new PointF(90, -150);
        var repeatedFreeformMoved = false;
        if (repeatedFreeformIntersection.FreeformAnchors.Length == 1
            && partitionScene.TryGetPathBezierSegment(partitionFill, sharedTopPart, out var repeatedFreeformFill))
        {
            var repeatedFreeformAdjusted = MainForm.AdjustFillEdgeBezierHandle(
                repeatedFreeformFill.Start,
                repeatedFreeformFill.Control1,
                repeatedFreeformFill.Control2,
                repeatedFreeformFill.End,
                EditHandleKind.LineStart,
                repeatedFreeformTarget);
            repeatedFreeformMoved = partitionScene.SetPathBezierSegment(
                    partitionFill,
                    sharedTopPart,
                    repeatedFreeformAdjusted.Start,
                    repeatedFreeformAdjusted.Control1,
                    repeatedFreeformAdjusted.Control2,
                    repeatedFreeformAdjusted.End,
                    rebuildGeometryIndex: false,
                    preserveStraightAdjacentSegments: true)
                && partitionScene.UpdateSharedBoundaryIntersection(
                    repeatedFreeformIntersection,
                    repeatedFreeformTarget,
                    rebuildGeometryIndex: false);
        }

        if (!repeatedFreeformMoved
            || partitionScene.ObjectCount != 2
            || !partitionScene.TryGetFreehandWorldPoints(partitionStroke, out var repeatedFreeformPoints)
            || repeatedFreeformPoints.Length != 6
            || repeatedFreeformPoints.Count(point => PointsWithin(point, repeatedFreeformTarget, 0.01f)) != 1)
        {
            throw new InvalidOperationException(
                $"Repeated fill/freeform shared-focus editing added topology members: moved={repeatedFreeformMoved}, links={repeatedFreeformIntersection.FreeformAnchors.Length}, objects={partitionScene.ObjectCount}.");
        }

        partitionScene.RestoreSnapshot(partitionSnapshot);
        if (partitionScene.ObjectCount != 2
            || partitionScene.ShapeKind[partitionFill] != ShapeKind.Rectangle
            || partitionScene.ShapeKind[partitionStroke] != ShapeKind.Freeform
            || !partitionScene.TryGetFreehandWorldPoints(partitionStroke, out var restoredFreeformPoints)
            || restoredFreeformPoints.Length != 5
            || !PointsWithin(restoredFreeformPoints[1], new PointF(60, -240), 0.01f)
            || !PointsWithin(restoredFreeformPoints[2], new PointF(60, 40), 0.01f))
        {
            throw new InvalidOperationException("Restoring a shared fill/freeform edit retained its materialized intersection topology.");
        }

        var cubicIntersectionScene = new VectorScene();
        cubicIntersectionScene.CreateEmpty();
        var cubicIntersectionFill = cubicIntersectionScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(440, 240),
            0,
            0,
            Color.Teal,
            Color.Transparent,
            12,
            ShapeKind.Rectangle);
        var cubicIntersectionLine = cubicIntersectionScene.AddCubicCurveSegment(
            0,
            new PointF(60, -240),
            new PointF(140, -180),
            new PointF(-20, -73),
            new PointF(60, 40),
            VectorUnits.StrokePointsToUnits(3),
            Color.Transparent,
            Color.White,
            12,
            LineEndpointStyle.Sharp,
            LineEndpointStyle.Sharp);
        var cubicIntersectionSnapshot = cubicIntersectionScene.CreateSnapshot();
        var cubicTopPart = cubicIntersectionScene.GetEditableFillBezierSegmentParts(cubicIntersectionFill)
            .First(part => PointsWithin(part.Start, new PointF(-220, -120), 0.01f));
        if (!cubicIntersectionScene.TryConvertFillToBezierPath(cubicIntersectionFill)
            || !cubicIntersectionScene.TryGetClosestPointOnPathBezierSegment(
                cubicIntersectionFill,
                cubicTopPart.PartIndex,
                new PointF(60, -120),
                out var cubicFillParameter,
                out _,
                out var cubicFillDistance)
            || cubicFillDistance > 1.5f
            || !cubicIntersectionScene.TryInsertPathBezierAnchor(
                cubicIntersectionFill,
                cubicTopPart.PartIndex,
                cubicFillParameter,
                out var cubicFillAnchorPart,
                out var cubicFillAnchor)
            || !PointsWithin(cubicFillAnchor, new PointF(60, -120), 1.5f)
            || !cubicIntersectionScene.TryGetPathBezierSegment(
                cubicIntersectionFill,
                cubicFillAnchorPart,
                out var cubicFillSegment))
        {
            throw new InvalidOperationException("The cubic-Line shared intersection could not materialize its fill anchor.");
        }

        var cubicIntersection = cubicIntersectionScene.CaptureStrokeIntersectionsAtFillAnchor(
            cubicIntersectionFill,
            cubicFillAnchorPart,
            startEndpoint: true,
            0);
        var firstCubicHalf = cubicIntersection.LineAnchors.FirstOrDefault(anchor => !anchor.StartEndpoint);
        var secondCubicHalf = cubicIntersection.LineAnchors.FirstOrDefault(anchor => anchor.StartEndpoint);
        var cubicMovedAnchor = new PointF(80, -140);
        var cubicMovedFill = MainForm.AdjustFillEdgeBezierHandle(
            cubicFillSegment.Start,
            cubicFillSegment.Control1,
            cubicFillSegment.Control2,
            cubicFillSegment.End,
            EditHandleKind.LineStart,
            cubicMovedAnchor);
        var firstEnd = PointF.Empty;
        var secondStart = PointF.Empty;
        if (cubicIntersection.LineAnchors.Length != 2
            || cubicIntersectionScene.ObjectCount != 3
            || firstCubicHalf.ObjectIndex != cubicIntersectionLine
            || secondCubicHalf.ObjectIndex == cubicIntersectionLine
            || !cubicIntersectionScene.SetPathBezierSegment(
                cubicIntersectionFill,
                cubicFillAnchorPart,
                cubicMovedFill.Start,
                cubicMovedFill.Control1,
                cubicMovedFill.Control2,
                cubicMovedFill.End,
                rebuildGeometryIndex: false,
                preserveStraightAdjacentSegments: true)
            || !cubicIntersectionScene.UpdateSharedBoundaryIntersection(
                cubicIntersection,
                cubicMovedAnchor)
            || !cubicIntersectionScene.TryGetLineCubic(
                firstCubicHalf.ObjectIndex,
                out _,
                out var firstControl1,
                out var firstControl2,
                out firstEnd)
            || !cubicIntersectionScene.TryGetLineCubic(
                secondCubicHalf.ObjectIndex,
                out secondStart,
                out var secondControl1,
                out var secondControl2,
                out _)
            || !PointsWithin(firstEnd, cubicMovedAnchor, 0.01f)
            || !PointsWithin(secondStart, cubicMovedAnchor, 0.01f)
            || !PointsWithin(firstControl1, firstCubicHalf.Control1, 0.01f)
            || !PointsWithin(
                firstControl2,
                new PointF(firstCubicHalf.Control2.X + 20, firstCubicHalf.Control2.Y - 20),
                0.01f)
            || !PointsWithin(
                secondControl1,
                new PointF(secondCubicHalf.Control1.X + 20, secondCubicHalf.Control1.Y - 20),
                0.01f)
            || !PointsWithin(secondControl2, secondCubicHalf.Control2, 0.01f)
            || cubicIntersectionScene.GetLineEndpointStyle(firstCubicHalf.ObjectIndex, startEndpoint: true) != LineEndpointStyle.Sharp
            || cubicIntersectionScene.GetLineEndpointStyle(secondCubicHalf.ObjectIndex, startEndpoint: false) != LineEndpointStyle.Sharp)
        {
            throw new InvalidOperationException(
                $"A cubic Line crossing was not split and moved as one shared fill/stroke anchor: links={cubicIntersection.LineAnchors.Length}, objects={cubicIntersectionScene.ObjectCount}, first={firstEnd}, second={secondStart}.");
        }

        var lineSideMovedAnchor = new PointF(40, -100);
        var lineSideIntersection = cubicIntersectionScene.CaptureFillIntersectionsAtLineEndpoint(
            firstCubicHalf.ObjectIndex,
            startEndpoint: false,
            0,
            rebuildGeometryIndex: false);
        var lineSideFillAnchor = lineSideIntersection.PathAnchors.FirstOrDefault();
        var lineSidePeer = lineSideIntersection.LineAnchors.FirstOrDefault();
        if (!cubicIntersectionScene.TryGetLineCubic(
                firstCubicHalf.ObjectIndex,
                out var lineSideOwnerStart,
                out var lineSideOwnerControl1,
                out var lineSideOwnerControl2,
                out var lineSideOwnerEnd))
        {
            throw new InvalidOperationException("The naturally split Line lost its cubic owner before reverse editing.");
        }

        var lineSideDx = lineSideMovedAnchor.X - lineSideOwnerEnd.X;
        var lineSideDy = lineSideMovedAnchor.Y - lineSideOwnerEnd.Y;
        cubicIntersectionScene.SetLineEndpoint(
            firstCubicHalf.ObjectIndex,
            startEndpoint: false,
            lineSideMovedAnchor,
            lineSideOwnerStart,
            lineSideOwnerControl1,
            new PointF(lineSideOwnerControl2.X + lineSideDx, lineSideOwnerControl2.Y + lineSideDy),
            keepStraight: false);
        PathBezierSegmentPart lineSideMovedFill = default;
        var lineSideMovedPeer = PointF.Empty;
        var lineSideMoved = lineSideIntersection.PathAnchors.Length == 1
            && lineSideIntersection.LineAnchors.Length == 1
            && cubicIntersectionScene.UpdateSharedBoundaryIntersection(
                lineSideIntersection,
                lineSideMovedAnchor,
                rebuildGeometryIndex: false)
            && cubicIntersectionScene.TryGetPathBezierSegment(
                lineSideFillAnchor.ObjectIndex,
                lineSideFillAnchor.PartIndex,
                out lineSideMovedFill)
            && cubicIntersectionScene.TryGetLineEndpoint(
                lineSidePeer.ObjectIndex,
                lineSidePeer.StartEndpoint,
                out lineSideMovedPeer)
            && PointsWithin(
                lineSideFillAnchor.StartEndpoint ? lineSideMovedFill.Start : lineSideMovedFill.End,
                lineSideMovedAnchor,
                0.01f)
            && PointsWithin(lineSideMovedPeer, lineSideMovedAnchor, 0.01f)
            && cubicIntersectionScene.ObjectCount == 3;
        cubicIntersectionScene.CompleteDeferredBuild();

        var repeatedFillIntersection = lineSideMoved
            ? cubicIntersectionScene.CaptureStrokeIntersectionsAtFillAnchor(
                lineSideFillAnchor.ObjectIndex,
                lineSideFillAnchor.PartIndex,
                startEndpoint: lineSideFillAnchor.StartEndpoint,
                0,
                rebuildGeometryIndex: false)
            : new SharedBoundaryIntersection(PointF.Empty, [], [], [], -1, -1);
        var repeatedFillTarget = new PointF(60, -120);
        var repeatedFillMoved = false;
        if (repeatedFillIntersection.LineAnchors.Length == 2
            && cubicIntersectionScene.TryGetPathBezierSegment(
                repeatedFillIntersection.OwnerObjectIndex,
                repeatedFillIntersection.OwnerPartIndex,
                out var repeatedFillSource))
        {
            var repeatedFillAdjusted = MainForm.AdjustFillEdgeBezierHandle(
                repeatedFillSource.Start,
                repeatedFillSource.Control1,
                repeatedFillSource.Control2,
                repeatedFillSource.End,
                lineSideFillAnchor.StartEndpoint ? EditHandleKind.LineStart : EditHandleKind.LineEnd,
                repeatedFillTarget);
            repeatedFillMoved = cubicIntersectionScene.SetPathBezierSegment(
                    repeatedFillIntersection.OwnerObjectIndex,
                    repeatedFillIntersection.OwnerPartIndex,
                    repeatedFillAdjusted.Start,
                    repeatedFillAdjusted.Control1,
                    repeatedFillAdjusted.Control2,
                    repeatedFillAdjusted.End,
                    rebuildGeometryIndex: false,
                    preserveStraightAdjacentSegments: true)
                && cubicIntersectionScene.UpdateSharedBoundaryIntersection(
                    repeatedFillIntersection,
                    repeatedFillTarget,
                    rebuildGeometryIndex: false);
        }
        cubicIntersectionScene.CompleteDeferredBuild();

        var roundTripFirstEnd = PointF.Empty;
        var roundTripSecondStart = PointF.Empty;
        if (!lineSideMoved
            || !repeatedFillMoved
            || cubicIntersectionScene.ObjectCount != 3
            || cubicIntersectionScene.GetPathBezierSegmentParts(cubicIntersectionFill).Length != 5
            || !cubicIntersectionScene.TryGetLineCubic(
                firstCubicHalf.ObjectIndex,
                out _,
                out _,
                out var repeatedFirstControl2,
                out roundTripFirstEnd)
            || !cubicIntersectionScene.TryGetLineCubic(
                secondCubicHalf.ObjectIndex,
                out roundTripSecondStart,
                out var repeatedSecondControl1,
                out _,
                out _)
            || !PointsWithin(roundTripFirstEnd, repeatedFillTarget, 0.01f)
            || !PointsWithin(roundTripSecondStart, repeatedFillTarget, 0.01f)
            || !PointsWithin(repeatedFirstControl2, firstCubicHalf.Control2, 0.01f)
            || !PointsWithin(repeatedSecondControl1, secondCubicHalf.Control1, 0.01f))
        {
            throw new InvalidOperationException(
                $"Repeated Fill/Line shared-focus edits changed membership or drifted cubic controls: lineMoved={lineSideMoved}, fillMoved={repeatedFillMoved}, pathLinks={lineSideIntersection.PathAnchors.Length}, peerLinks={lineSideIntersection.LineAnchors.Length}, repeatLinks={repeatedFillIntersection.LineAnchors.Length}, objects={cubicIntersectionScene.ObjectCount}, first={roundTripFirstEnd}, second={roundTripSecondStart}.");
        }

        cubicIntersectionScene.RestoreSnapshot(cubicIntersectionSnapshot);
        if (cubicIntersectionScene.ObjectCount != 2
            || cubicIntersectionScene.ShapeKind[cubicIntersectionFill] != ShapeKind.Rectangle
            || !cubicIntersectionScene.TryGetLineCubic(
                cubicIntersectionLine,
                out var restoredCubicStart,
                out _,
                out _,
                out var restoredCubicEnd)
            || !PointsWithin(restoredCubicStart, new PointF(60, -240), 0.01f)
            || !PointsWithin(restoredCubicEnd, new PointF(60, 40), 0.01f))
        {
            throw new InvalidOperationException("Restoring a shared cubic-Line edit retained split Line objects.");
        }

        foreach (var outlinedFillColor in new[] { Color.Transparent, Color.FromArgb(255, 24, 24, 24) })
        {
            var outlinedIntersectionScene = new VectorScene();
            outlinedIntersectionScene.CreateEmpty();
            var outlinedStroke = VectorUnits.StrokePointsToUnits(3);
            var outlinedRectangle = outlinedIntersectionScene.AddObject(
                0,
                PointF.Empty,
                new SizeF(440, 240),
                0,
                outlinedStroke,
                outlinedFillColor,
                Color.White,
                12,
                ShapeKind.Rectangle);
            var outlinedIntersectionFill = outlinedIntersectionScene.AddPathObject(
                0,
                [
                    new PointF(-240, -200),
                    new PointF(60, -120),
                    new PointF(220, 40),
                    new PointF(20, 200),
                    new PointF(-260, 20)
                ],
                0,
                Color.Teal,
                Color.Transparent,
                12);
            var outlinedSnapshot = outlinedIntersectionScene.CreateSnapshot();
            var outlinedAnchor = new PointF(60, -120);
            if (!outlinedIntersectionScene.TryConvertFillToBezierPath(outlinedIntersectionFill))
            {
                throw new InvalidOperationException("The outlined-shape intersection fill could not become an editable path.");
            }

            var outlinedAnchorPart = outlinedIntersectionScene.GetPathBezierSegmentParts(outlinedIntersectionFill)
                .First(part => PointsWithin(part.Start, outlinedAnchor, 0.01f));
            var outlinedIntersection = outlinedIntersectionScene.CaptureStrokeIntersectionsAtFillAnchor(
                outlinedIntersectionFill,
                outlinedAnchorPart.PartIndex,
                startEndpoint: true,
                0);
            var outlinedStrokePathAnchor = outlinedIntersection.PathAnchors.FirstOrDefault(anchor =>
                anchor.ObjectIndex == outlinedRectangle);
            PathBezierSegmentPart outlinedFillSegment = default;
            PathBezierSegmentPart outlinedStrokeSegment = default;
            if (outlinedIntersection.OwnerObjectIndex != outlinedIntersectionFill
                || outlinedIntersection.OwnerPartIndex < 0
                || outlinedIntersection.PathAnchors.Length != 1
                || outlinedStrokePathAnchor.ObjectIndex != outlinedRectangle
                || outlinedIntersection.LineAnchors.Length != 0
                || outlinedIntersection.FreeformAnchors.Length != 0
                || outlinedIntersectionScene.ObjectCount != 2
                || outlinedIntersectionScene.ShapeKind[outlinedRectangle] != ShapeKind.Path
                || outlinedIntersectionScene.Stroke[outlinedRectangle] <= 0
                || outlinedIntersectionScene.StrokeArgb[outlinedRectangle] != Color.White.ToArgb()
                || Color.FromArgb(outlinedIntersectionScene.Argb[outlinedRectangle]).A != outlinedFillColor.A
                || !outlinedIntersectionScene.TryGetPathBezierSegment(
                    outlinedIntersection.OwnerObjectIndex,
                    outlinedIntersection.OwnerPartIndex,
                    out outlinedFillSegment)
                || !PointsWithin(outlinedFillSegment.Start, outlinedAnchor, 0.01f)
                || !outlinedIntersectionScene.TryGetPathBezierSegment(
                    outlinedStrokePathAnchor.ObjectIndex,
                    outlinedStrokePathAnchor.PartIndex,
                    out outlinedStrokeSegment)
                || !PointsWithin(outlinedStrokeSegment.Start, outlinedAnchor, 0.01f))
            {
                throw new InvalidOperationException(
                    $"An outlined Rectangle intersection did not remain one path-backed stroke: fillAlpha={outlinedFillColor.A}, owner={outlinedIntersection.OwnerObjectIndex}:{outlinedIntersection.OwnerPartIndex}/{outlinedFillSegment}, pathAnchor={outlinedStrokePathAnchor}/{outlinedStrokeSegment}, shape={outlinedIntersectionScene.ShapeKind[outlinedRectangle]}, stroke={outlinedIntersectionScene.Stroke[outlinedRectangle]:F2}/{outlinedStroke:F2}, strokeArgb={outlinedIntersectionScene.StrokeArgb[outlinedRectangle]}/{Color.White.ToArgb()}, actualFillAlpha={Color.FromArgb(outlinedIntersectionScene.Argb[outlinedRectangle]).A}, paths={outlinedIntersection.PathAnchors.Length}, lines={outlinedIntersection.LineAnchors.Length}, freeforms={outlinedIntersection.FreeformAnchors.Length}, objects={outlinedIntersectionScene.ObjectCount}.");
            }

            var outlinedMovedAnchor = new PointF(90, -150);
            var outlinedMovedFill = MainForm.AdjustFillEdgeBezierHandle(
                outlinedFillSegment.Start,
                outlinedFillSegment.Control1,
                outlinedFillSegment.Control2,
                outlinedFillSegment.End,
                EditHandleKind.LineStart,
                outlinedMovedAnchor);
            if (!outlinedIntersectionScene.SetPathBezierSegment(
                    outlinedIntersection.OwnerObjectIndex,
                    outlinedIntersection.OwnerPartIndex,
                    outlinedMovedFill.Start,
                    outlinedMovedFill.Control1,
                    outlinedMovedFill.Control2,
                    outlinedMovedFill.End,
                    rebuildGeometryIndex: false,
                    preserveStraightAdjacentSegments: true)
                || !outlinedIntersectionScene.UpdateSharedBoundaryIntersection(
                    outlinedIntersection,
                    outlinedMovedAnchor)
                || !outlinedIntersectionScene.TryGetPathBezierSegment(
                    outlinedStrokePathAnchor.ObjectIndex,
                    outlinedStrokePathAnchor.PartIndex,
                    out var movedStrokeSegment)
                || !PointsWithin(
                    outlinedStrokePathAnchor.StartEndpoint
                        ? movedStrokeSegment.Start
                        : movedStrokeSegment.End,
                    outlinedMovedAnchor,
                    0.01f)
                || outlinedIntersectionScene.ObjectCount != 2)
            {
                throw new InvalidOperationException(
                    $"Dragging a fill anchor did not move the outlined Rectangle path node in place: fillAlpha={outlinedFillColor.A}.");
            }

            outlinedIntersectionScene.RestoreSnapshot(outlinedSnapshot);
            if (outlinedIntersectionScene.ObjectCount != 2
                || outlinedIntersectionScene.ShapeKind[outlinedRectangle] != ShapeKind.Rectangle
                || outlinedIntersectionScene.Stroke[outlinedRectangle] <= 0
                || outlinedIntersectionScene.StrokeArgb[outlinedRectangle] != Color.White.ToArgb()
                || outlinedIntersectionScene.ShapeKind[outlinedIntersectionFill] != ShapeKind.Path)
            {
                throw new InvalidOperationException(
                    $"Restoring an outlined Rectangle intersection retained materialized stroke objects: fillAlpha={outlinedFillColor.A}.");
            }
        }

        var reverseIntersectionScene = new VectorScene();
        reverseIntersectionScene.CreateEmpty();
        var reverseIntersectionFill = reverseIntersectionScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(440, 240),
            0,
            0,
            Color.Teal,
            Color.Transparent,
            12,
            ShapeKind.Rectangle);
        var reverseIntersectionLine = reverseIntersectionScene.AddCubicCurveSegment(
            0,
            new PointF(60, -120),
            new PointF(60, -67),
            new PointF(60, -13),
            new PointF(60, 40),
            VectorUnits.StrokePointsToUnits(3),
            Color.Transparent,
            Color.White,
            12);
        var reverseIntersectionSnapshot = reverseIntersectionScene.CreateSnapshot();
        var reverseIntersection = reverseIntersectionScene.CaptureFillIntersectionsAtLineEndpoint(
            reverseIntersectionLine,
            startEndpoint: true,
            0);
        var reverseMovedAnchor = new PointF(80, -140);
        if (!reverseIntersectionScene.TryGetLineCubic(
                reverseIntersectionLine,
                out _,
                out var reverseControl1,
                out var reverseControl2,
                out var reverseEnd))
        {
            throw new InvalidOperationException("The reverse shared-intersection Line lost its cubic geometry.");
        }

        reverseIntersectionScene.SetLineEndpoint(
            reverseIntersectionLine,
            startEndpoint: true,
            reverseMovedAnchor,
            reverseEnd,
            new PointF(reverseControl1.X + 20, reverseControl1.Y - 20),
            reverseControl2,
            keepStraight: true);
        PathBezierSegmentPart reverseMovedFill = default;
        var reverseMovedLine = PointF.Empty;
        if (reverseIntersection.PathAnchors.Length != 1
            || !reverseIntersectionScene.UpdateSharedBoundaryIntersection(
                reverseIntersection,
                reverseMovedAnchor)
            || !reverseIntersectionScene.TryGetPathBezierSegment(
                reverseIntersection.PathAnchors[0].ObjectIndex,
                reverseIntersection.PathAnchors[0].PartIndex,
                out reverseMovedFill)
            || !PointsWithin(reverseMovedFill.Start, reverseMovedAnchor, 0.01f)
            || !VectorScene.IsStraightBezierSegment(
                reverseMovedFill.Start,
                reverseMovedFill.Control1,
                reverseMovedFill.Control2,
                reverseMovedFill.End)
            || !reverseIntersectionScene.TryGetLineEndpoint(
                reverseIntersectionLine,
                startEndpoint: true,
                out reverseMovedLine)
            || !PointsWithin(reverseMovedLine, reverseMovedAnchor, 0.01f))
        {
            throw new InvalidOperationException(
                $"Dragging a Line endpoint did not move the intersecting fill boundary anchor: paths={reverseIntersection.PathAnchors.Length}, fill={reverseMovedFill}, line={reverseMovedLine}.");
        }

        reverseIntersectionScene.RestoreSnapshot(reverseIntersectionSnapshot);
        if (reverseIntersectionScene.ObjectCount != 2
            || reverseIntersectionScene.ShapeKind[reverseIntersectionFill] != ShapeKind.Rectangle
            || !reverseIntersectionScene.TryGetLineEndpoint(
                reverseIntersectionLine,
                startEndpoint: true,
                out var restoredReverseLine)
            || !PointsWithin(restoredReverseLine, new PointF(60, -120), 0.01f))
        {
            throw new InvalidOperationException("Restoring a reverse shared-intersection edit retained its fill anchor materialization.");
        }

        var combinedIntersectionScene = new VectorScene();
        combinedIntersectionScene.CreateEmpty();
        var combinedIntersectionFill = combinedIntersectionScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(440, 240),
            0,
            0,
            Color.Teal,
            Color.Transparent,
            12,
            ShapeKind.Rectangle);
        var combinedRightEdge = combinedIntersectionScene.GetEditableFillBezierSegmentParts(combinedIntersectionFill)
            .First(part => PointsWithin(part.Start, new PointF(220, -120), 0.01f));
        var combinedBoundaryLine = combinedIntersectionScene.AddCubicCurveSegment(
            0,
            combinedRightEdge.Start,
            combinedRightEdge.Control1,
            combinedRightEdge.Control2,
            combinedRightEdge.End,
            VectorUnits.StrokePointsToUnits(3),
            Color.Transparent,
            Color.White,
            12);
        var combinedCrossingLine = combinedIntersectionScene.AddCubicCurveSegment(
            0,
            new PointF(60, -120),
            new PointF(60, -67),
            new PointF(60, -13),
            new PointF(60, 40),
            VectorUnits.StrokePointsToUnits(3),
            Color.Transparent,
            Color.White,
            12);
        var combinedIntersection = combinedIntersectionScene.CaptureFillIntersectionsAtLineEndpoint(
            combinedCrossingLine,
            startEndpoint: true,
            0,
            rebuildGeometryIndex: false);
        var combinedBoundaryLinks = combinedIntersectionScene.CaptureFillBoundaryLineLinks(
            combinedBoundaryLine,
            0);
        var combinedMovedAnchor = new PointF(80, -140);
        combinedIntersectionScene.SetLineEndpoint(
            combinedCrossingLine,
            startEndpoint: true,
            combinedMovedAnchor,
            new PointF(60, 40),
            new PointF(80, -87),
            new PointF(60, -13),
            keepStraight: true);
        PathBezierSegmentPart combinedMovedFill = default;
        if (combinedIntersection.PathAnchors.Length != 1
            || combinedBoundaryLinks.Length != 1
            || !combinedIntersectionScene.UpdateFillBoundaryLineLinks(
                combinedBoundaryLinks,
                rebuildGeometryIndex: false)
            || !combinedIntersectionScene.UpdateSharedBoundaryIntersection(
                combinedIntersection,
                combinedMovedAnchor)
            || !combinedIntersectionScene.TryGetPathBezierSegment(
                combinedIntersection.PathAnchors[0].ObjectIndex,
                combinedIntersection.PathAnchors[0].PartIndex,
                out combinedMovedFill)
            || !PointsWithin(combinedMovedFill.Start, combinedMovedAnchor, 0.01f)
            || combinedIntersectionScene.GetPathBezierSegmentParts(combinedIntersectionFill).Length != 5)
        {
            throw new InvalidOperationException(
                $"A coincident fill link overwrote the later shared intersection anchor: shared={combinedIntersection.PathAnchors.Length}, coincident={combinedBoundaryLinks.Length}, segment={combinedMovedFill}.");
        }

        var compoundIntersectionScene = new VectorScene();
        compoundIntersectionScene.CreateEmpty();
        var compoundIntersectionFill = compoundIntersectionScene.AddPathObjectContours(
            0,
            [
                [
                    new PointF(0, 0),
                    new PointF(100, 0),
                    new PointF(100, 100),
                    new PointF(0, 100)
                ],
                [
                    new PointF(0, 0),
                    new PointF(-100, 0),
                    new PointF(-100, -100),
                    new PointF(0, -100)
                ]
            ],
            0,
            Color.Teal,
            Color.Transparent,
            12);
        var compoundIntersectionLine = compoundIntersectionScene.AddCubicCurveSegment(
            0,
            PointF.Empty,
            new PointF(17, -17),
            new PointF(33, -33),
            new PointF(50, -50),
            VectorUnits.StrokePointsToUnits(3),
            Color.Transparent,
            Color.White,
            12);
        var compoundIntersection = compoundIntersectionScene.CaptureFillIntersectionsAtLineEndpoint(
            compoundIntersectionLine,
            startEndpoint: true,
            0);
        var compoundMovedAnchor = new PointF(20, 20);
        if (compoundIntersection.PathAnchors.Length != 2
            || !compoundIntersectionScene.UpdateSharedBoundaryIntersection(
                compoundIntersection,
                compoundMovedAnchor)
            || !compoundIntersectionScene.TryGetPathBezierWorldContours(
                compoundIntersectionFill,
                out var compoundContours)
            || compoundContours.Length != 2
            || compoundContours.Sum(contour => contour.Count(node =>
                PointsWithin(node.Anchor, compoundMovedAnchor, 0.01f))) != 2
            || compoundContours.Any(contour => contour.Any(node =>
                PointsWithin(node.Anchor, PointF.Empty, 0.01f))))
        {
            throw new InvalidOperationException(
                $"A compound fill did not move every contour node at a shared Line endpoint: links={compoundIntersection.PathAnchors.Length}.");
        }

        var repeatedSplitScene = new VectorScene();
        repeatedSplitScene.CreateEmpty();
        var repeatedSplitLine = repeatedSplitScene.AddCubicCurveSegment(
            0,
            new PointF(0, 0),
            new PointF(33, -40),
            new PointF(67, 40),
            new PointF(100, 0),
            VectorUnits.StrokePointsToUnits(3),
            Color.Transparent,
            Color.White,
            12);
        if (!repeatedSplitScene.SplitLineAt(
                repeatedSplitLine,
                0.5f,
                out var firstRepeatedSplit,
                rebuildGeometryIndex: false)
            || !repeatedSplitScene.SplitLineAt(
                repeatedSplitLine,
                0.5f,
                out var secondRepeatedSplit,
                rebuildGeometryIndex: false))
        {
            throw new InvalidOperationException("The same cubic Line could not be split twice with deferred rebuilds.");
        }

        var repeatedSplitObjects = new[]
        {
            repeatedSplitLine,
            secondRepeatedSplit.SecondObjectIndex,
            firstRepeatedSplit.SecondObjectIndex
        };
        var repeatedSplitSubOrders = repeatedSplitObjects
            .Select(index => repeatedSplitScene.ObjectSubOrder[index])
            .ToArray();
        if (repeatedSplitObjects.Distinct().Count() != 3
            || repeatedSplitSubOrders.Distinct().Count() != 3
            || !(repeatedSplitSubOrders[0] < repeatedSplitSubOrders[1]
                && repeatedSplitSubOrders[1] < repeatedSplitSubOrders[2])
            || !repeatedSplitScene.TryGetLineEndpoint(
                repeatedSplitLine,
                startEndpoint: false,
                out var repeatedFirstEnd)
            || !repeatedSplitScene.TryGetLineEndpoint(
                secondRepeatedSplit.SecondObjectIndex,
                startEndpoint: true,
                out var repeatedSecondStart)
            || !repeatedSplitScene.TryGetLineEndpoint(
                secondRepeatedSplit.SecondObjectIndex,
                startEndpoint: false,
                out var repeatedSecondEnd)
            || !repeatedSplitScene.TryGetLineEndpoint(
                firstRepeatedSplit.SecondObjectIndex,
                startEndpoint: true,
                out var repeatedThirdStart)
            || !PointsWithin(repeatedFirstEnd, repeatedSecondStart, 1.5f)
            || !PointsWithin(repeatedSecondEnd, repeatedThirdStart, 1.5f))
        {
            throw new InvalidOperationException(
                $"Repeated Line splits produced duplicate sub-orders or disconnected segments: suborders={string.Join(',', repeatedSplitSubOrders)}.");
        }

        var fillEdgeMergeScene = new VectorScene();
        fillEdgeMergeScene.CreateEmpty();
        var fillEdgeMergeSource = fillEdgeMergeScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(100, 100),
            0,
            0,
            Color.Teal,
            Color.Transparent,
            12,
            ShapeKind.Rectangle);
        fillEdgeMergeScene.AddObject(
            0,
            new PointF(120, 0),
            new SizeF(100, 100),
            0,
            0,
            Color.Teal,
            Color.Transparent,
            12,
            ShapeKind.Rectangle);
        var fillEdgeMergeRight = fillEdgeMergeScene.GetEditableFillBezierSegmentParts(fillEdgeMergeSource)
            .First(part => PointsWithin(part.Start, new PointF(50, -50), 0.01f));
        if (!fillEdgeMergeScene.TryConvertFillToBezierPath(fillEdgeMergeSource)
            || !fillEdgeMergeScene.TryGetPathBezierSegment(
                fillEdgeMergeSource,
                fillEdgeMergeRight.PartIndex,
                out var fillEdgeMergeSegment))
        {
            throw new InvalidOperationException("The fill-edge merge regression could not materialize its editable boundary.");
        }

        var overlappingFillEdge = MainForm.AdjustFillEdgeBezierHandle(
            fillEdgeMergeSegment.Start,
            fillEdgeMergeSegment.Control1,
            fillEdgeMergeSegment.Control2,
            fillEdgeMergeSegment.End,
            EditHandleKind.LineStart,
            new PointF(80, -50));
        if (!fillEdgeMergeScene.SetPathBezierSegment(
                fillEdgeMergeSource,
                fillEdgeMergeRight.PartIndex,
                overlappingFillEdge.Start,
                overlappingFillEdge.Control1,
                overlappingFillEdge.Control2,
                overlappingFillEdge.End,
                preserveStraightAdjacentSegments: true))
        {
            throw new InvalidOperationException("The fill-edge merge regression could not move its boundary endpoint.");
        }

        var fillEdgeMerged = fillEdgeMergeScene.MergeSameColorFillsAround(
            fillEdgeMergeSource,
            connectNearby: false,
            frame: 0);
        if (fillEdgeMergeScene.ObjectCount != 1
            || (uint)fillEdgeMerged >= fillEdgeMergeScene.ObjectCount
            || !fillEdgeMergeScene.FillContainsPoint(fillEdgeMerged, new PointF(-40, 0))
            || !fillEdgeMergeScene.FillContainsPoint(fillEdgeMerged, new PointF(160, 0))
            || !fillEdgeMergeScene.FillContainsPoint(fillEdgeMerged, new PointF(75, -45)))
        {
            throw new InvalidOperationException(
                $"A committed fill-edge adjustment did not merge the newly overlapping same-color fills: objects={fillEdgeMergeScene.ObjectCount}, merged={fillEdgeMerged}.");
        }

        coverageScene.AddFreehandStroke(
            0,
            [
                coverageParts[0].Start,
                coverageParts[0].End,
                coverageParts[1].End,
                coverageParts[2].End,
                coverageParts[3].End
            ],
            VectorUnits.StrokePointsToUnits(3),
            Color.White,
            brushStroke: false,
            12);
        var fullyCoveredParts = coverageScene.GetExposedFillBezierSegmentPieces(coverageFill, 0);
        var editableCoveredParts = coverageScene.GetExposedFillBezierSegmentPieces(
            coverageFill,
            0,
            includeCoincidentStrokes: true);
        coverageStage.SetFillEdgeBezierOverlay(
            coverageFill,
            fullyCoveredParts.Select(part => new FillEdgeBezierOverlaySegment(
                part.PieceIndex,
                part.Start,
                part.Control1,
                part.Control2,
                part.End)).ToArray());
        if (fullyCoveredParts.Length != 0 || coverageStage.FillEdgeBezierOverlayVisible)
        {
            throw new InvalidOperationException("A fill enclosed by a coincident closed stroke retained fill-edge controls.");
        }

        coverageStage.SetFillEdgeBezierOverlay(
            coverageFill,
            editableCoveredParts.Select(part => new FillEdgeBezierOverlaySegment(
                part.PieceIndex,
                part.Start,
                part.Control1,
                part.Control2,
                part.End)).ToArray(),
            editableCoveredParts.FirstOrDefault().PieceIndex);
        var editableCoveredControlHit = editableCoveredParts.Length > 0
            ? coverageStage.HitTestFillEdgeBezierOverlay(Point.Round(coverageStage.WorldToScreen(
                editableCoveredParts[0].Control1.X,
                editableCoveredParts[0].Control1.Y)))
            : FillEdgeBezierOverlayHit.None;
        if (editableCoveredParts.Length != coverageParts.Length
            || !coverageStage.FillEdgeBezierOverlayVisible
            || editableCoveredControlHit.Handle != EditHandleKind.BezierControl)
        {
            throw new InvalidOperationException(
                "Selecting a fill did not restore Bezier controls hidden by its synchronized coincident stroke.");
        }

        fillScene.RestoreSnapshot(originalSnapshot);
        if (fillScene.ObjectCount != originalCount
            || fillScene.ShapeKind[fill] != ShapeKind.Path
            || fillScene.TryGetPathBezierWorldContours(fill, out _))
        {
            throw new InvalidOperationException("Restoring a pre-conversion fill snapshot retained stale cubic payload.");
        }

        var outlinedScene = new VectorScene();
        outlinedScene.CreateEmpty();
        var outlinedFill = outlinedScene.AddPathObject(
            0,
            [
                new PointF(-260, -120),
                new PointF(20, -210),
                new PointF(280, -60),
                new PointF(190, 210),
                new PointF(-210, 170)
            ],
            VectorUnits.StrokePointsToUnits(3),
            Color.Coral,
            Color.White,
            18);
        outlinedScene.SetLinearGradient(outlinedFill, Color.Coral, Color.RoyalBlue);
        outlinedScene.FillAutoMergeProtected[outlinedFill] = true;
        if (!outlinedScene.TryConvertFillToBezierPath(outlinedFill)
            || !outlinedScene.TryGetPathBezierSegment(outlinedFill, 1, out var outlinedSegment)
            || !outlinedScene.SetPathBezierSegment(
                outlinedFill,
                outlinedSegment.PartIndex,
                outlinedSegment.Start,
                new PointF(outlinedSegment.Control1.X + 90, outlinedSegment.Control1.Y - 140),
                new PointF(outlinedSegment.Control2.X - 70, outlinedSegment.Control2.Y + 110),
                outlinedSegment.End)
            || !outlinedScene.TryGetPathBezierSegment(outlinedFill, outlinedSegment.PartIndex, out var editedOutlinedSegment)
            || !outlinedScene.TryGetPathBezierWorldContours(outlinedFill, out var exactBeforeMaterialization))
        {
            throw new InvalidOperationException("The outlined fill could not be prepared with exact cubic geometry.");
        }

        var boundaryProbe = editedOutlinedSegment.Samples[editedOutlinedSegment.Samples.Length / 2];
        var curvedBoundary = outlinedScene.HitTestElement(boundaryProbe, 0, toleranceWorld: 2);
        var curvedBoundaryPoints = outlinedScene.GetBoundaryPartPoints(curvedBoundary, 0);
        if (curvedBoundary.Key.Kind != DrawingElementKind.BoundaryStroke
            || curvedBoundaryPoints.Length <= 2
            || !outlinedScene.TryGetExactFillBezierSegmentForBoundary(curvedBoundary, 0, out var resolvedBoundarySegment)
            || resolvedBoundarySegment.PartIndex != editedOutlinedSegment.PartIndex
            || !MainForm.TryGetSelectedFillBoundaryBezierPart(
                outlinedScene,
                0,
                outlinedFill,
                [curvedBoundary],
                out var selectedBoundaryPartIndex)
            || selectedBoundaryPartIndex != editedOutlinedSegment.PartIndex
            || !MainForm.ShouldShowFillEdgeBezierOverlay(
                ToolMode.Select,
                1,
                hasEditableFillBoundarySelection: selectedBoundaryPartIndex >= 0))
        {
            throw new InvalidOperationException("A selected curved fill boundary did not resolve back to its exact editable cubic segment.");
        }

        using (var outlinedStage = new StageControl(outlinedScene) { Size = new Size(640, 420) })
        {
            outlinedStage.SetSelection([outlinedFill], outlinedFill);
            outlinedStage.SetSelectedElement(curvedBoundary);
            outlinedStage.SetFillEdgeBezierOverlay(
                outlinedFill,
                outlinedScene.GetEditableFillBezierSegmentParts(outlinedFill)
                    .Select(part => new FillEdgeBezierOverlaySegment(
                        part.PartIndex,
                        part.Start,
                        part.Control1,
                        part.Control2,
                        part.End))
                    .ToArray(),
                selectedBoundaryPartIndex);
            var boundaryControlHit = outlinedStage.HitTestFillEdgeBezierOverlay(
                Point.Round(outlinedStage.WorldToScreen(
                    editedOutlinedSegment.Control1.X,
                    editedOutlinedSegment.Control1.Y)));
            if (boundaryControlHit.PartIndex != editedOutlinedSegment.PartIndex
                || boundaryControlHit.Handle != EditHandleKind.BezierControl
                || !outlinedStage.SuppressFillEdgeBezierSelectionOutline(outlinedFill))
            {
                throw new InvalidOperationException("A selected curved outline did not expose its exact fill-edge control handles.");
            }
        }

        var outlinedOrder = outlinedScene.ObjectOrder[outlinedFill];
        var outlinedSubOrder = outlinedScene.ObjectSubOrder[outlinedFill];
        var outlinedObjectCount = outlinedScene.ObjectCount;
        var outlinedFillKey = new DrawingElementKey(outlinedFill, DrawingElementKind.Fill, 0);
        var outlinedResult = outlinedScene.MaterializeSelectedParts([outlinedFillKey], 0);
        var retainedFill = outlinedResult.Success
            ? outlinedResult.Parts.Single(part => part.Source == outlinedFillKey).Result.ObjectIndex
            : -1;
        if (!outlinedResult.Success
            || !outlinedResult.Changed
            || retainedFill != outlinedResult.OldToNewObjectIndex[outlinedFill]
            || retainedFill < 0
            || outlinedScene.ObjectCount <= outlinedObjectCount
            || outlinedScene.Stroke[retainedFill] != 0
            || outlinedScene.ObjectOrder[retainedFill] != outlinedOrder
            || !outlinedScene.ObjectSubOrder[retainedFill].Equals(outlinedSubOrder)
            || !outlinedScene.HasGradient(retainedFill)
            || !outlinedScene.FillAutoMergeProtected[retainedFill]
            || !outlinedScene.TryGetPathBezierWorldContours(retainedFill, out var exactAfterMaterialization)
            || exactAfterMaterialization.Length != exactBeforeMaterialization.Length
            || exactAfterMaterialization.Where((contour, index) =>
                    !contour.SequenceEqual(exactBeforeMaterialization[index]))
                .Any())
        {
            throw new InvalidOperationException("Separating an outlined fill flattened or replaced its exact cubic contour.");
        }

        var detachedOutlineLines = Enumerable.Range(0, outlinedScene.ObjectCount)
            .Where(index => outlinedScene.ShapeKind[index] == ShapeKind.Line
                && outlinedScene.Stroke[index] > 0
                && outlinedScene.ObjectOrder[index] == outlinedOrder)
            .ToArray();
        var retainedFreehandOutline = Enumerable.Range(0, outlinedScene.ObjectCount)
            .Any(index => outlinedScene.ShapeKind[index] == ShapeKind.Freeform
                && outlinedScene.Stroke[index] > 0
                && outlinedScene.ObjectOrder[index] == outlinedOrder);
        var expectedOutlineSegments = exactBeforeMaterialization
            .SelectMany(contour => contour.Select((node, index) =>
            {
                var next = contour[(index + 1) % contour.Length];
                return (Start: node.Anchor,
                    Control1: node.OutgoingControl,
                    Control2: next.IncomingControl,
                    End: next.Anchor);
            }))
            .ToArray();
        var exactOutlinePreserved = expectedOutlineSegments.All(expected =>
            detachedOutlineLines.Any(line =>
                outlinedScene.TryGetLineBezierPart(
                    line,
                    0,
                    1,
                    out var start,
                    out var control1,
                    out var control2,
                    out var end)
                && (PointsNear(start, expected.Start)
                    && PointsNear(control1, expected.Control1)
                    && PointsNear(control2, expected.Control2)
                    && PointsNear(end, expected.End)
                    || PointsNear(start, expected.End)
                    && PointsNear(control1, expected.Control2)
                    && PointsNear(control2, expected.Control1)
                    && PointsNear(end, expected.Start))));
        var detachedOutlineChain = detachedOutlineLines.Length > 0
            ? outlinedScene.GetConnectedStrokeElements(
                    new DrawingElementHit(
                        new DrawingElementKey(detachedOutlineLines[0], DrawingElementKind.Stroke, 0),
                        0,
                        0,
                        1),
                    0)
                .Where(hit => outlinedScene.ObjectOrder[hit.Key.ObjectIndex] == outlinedOrder)
                .Select(hit => hit.Key.ObjectIndex)
                .Distinct()
                .ToHashSet()
            : [];
        if (retainedFreehandOutline
            || detachedOutlineLines.Length != expectedOutlineSegments.Length
            || !exactOutlinePreserved
            || !detachedOutlineChain.SetEquals(detachedOutlineLines))
        {
            throw new InvalidOperationException(
                $"An exact outlined Path did not remain one connected cubic stroke chain after materialization: expected={expectedOutlineSegments.Length}, lines={detachedOutlineLines.Length}, chain={detachedOutlineChain.Count}, freeform={retainedFreehandOutline}.");
        }

        static PointF SkewRotate(PointF point) => new(
            -0.35f * point.X - point.Y + 45,
            point.X - 0.2f * point.Y - 30);
        outlinedScene.TransformObjects([retainedFill], SkewRotate);
        if (!outlinedScene.TryGetPathBezierWorldContours(retainedFill, out var exactAfterTransform)
            || exactAfterTransform.Length != exactAfterMaterialization.Length
            || exactAfterTransform.Where((contour, contourIndex) =>
                    contour.Length != exactAfterMaterialization[contourIndex].Length
                    || contour.Where((node, nodeIndex) =>
                    {
                        var before = exactAfterMaterialization[contourIndex][nodeIndex];
                        return !PointsNear(node.Anchor, VectorUnits.Quantize(SkewRotate(before.Anchor)))
                            || !PointsNear(node.IncomingControl, VectorUnits.Quantize(SkewRotate(before.IncomingControl)))
                            || !PointsNear(node.OutgoingControl, VectorUnits.Quantize(SkewRotate(before.OutgoingControl)));
                    }).Any())
                .Any())
        {
            throw new InvalidOperationException("A skewed and rotated detached fill changed its exact cubic node topology.");
        }

        RunVirtualFillIntersectionRefreshRegression();
        RunRepeatedFillAnchorLineEndpointRegression();
        RunCoincidentFillBoundaryLineRegression();
        RunSelfIntersectingFillBoundaryRegression();
    }

    private static void RunSelfIntersectingFillBoundaryRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        var fill = scene.AddPathObject(
            0,
            [
                new PointF(-300, -100),
                new PointF(300, -100),
                new PointF(300, 200),
                new PointF(0, -300),
                new PointF(-300, 200)
            ],
            0,
            Color.Teal,
            Color.Transparent,
            15);
        var fillParts = scene.GetFillParts(fill, 0);
        var pieces = scene.GetExposedFillBezierSegmentPieces(fill, 0);
        var middleTop = pieces.FirstOrDefault(piece =>
            piece.SourcePartIndex == 0
            && piece.StartIsVirtualAnchor
            && piece.EndIsVirtualAnchor);
        var allPiecesResolve = pieces.All(piece =>
            scene.TryResolveFillPartForBezierSegmentPiece(fill, 0, piece, out _));
        var leftIntersection = middleTop.Start;
        var crossingPiece = pieces.FirstOrDefault(piece =>
            piece.SourcePartIndex != middleTop.SourcePartIndex
            && ((piece.StartIsVirtualAnchor && PointsWithin(piece.Start, leftIntersection, 1.5f))
                || (piece.EndIsVirtualAnchor && PointsWithin(piece.End, leftIntersection, 1.5f))));
        var startEndpoint = crossingPiece.StartIsVirtualAnchor
            && PointsWithin(crossingPiece.Start, leftIntersection, 1.5f);
        var activePart = -1;
        var prepared = middleTop.SourcePartIndex == 0
            && allPiecesResolve
            && crossingPiece.SourcePartIndex != middleTop.SourcePartIndex
            && scene.TryConvertFillToBezierPath(fill)
            && scene.TryMaterializePathBezierSegmentNeighborhood(
                fill,
                crossingPiece,
                startEndpoint,
                pieces,
                out activePart);
        var shared = prepared
            ? scene.CaptureStrokeIntersectionsAtFillAnchor(
                fill,
                activePart,
                startEndpoint,
                0,
                rebuildGeometryIndex: false)
            : new SharedBoundaryIntersection(PointF.Empty, [], [], []);
        var linkedBranch = shared.PathAnchors.FirstOrDefault(path =>
            path.ObjectIndex == fill
            && path.PartIndex != shared.OwnerPartIndex);
        var target = new PointF(-150, -130);
        var synchronized = shared.OwnerPartIndex == activePart + 1
            && shared.PathAnchors.Length == 1
            && linkedBranch.ObjectIndex == fill
            && scene.TryGetPathBezierSegment(fill, shared.OwnerPartIndex, out var activeSegment)
            && MainForm.AdjustFillEdgeBezierHandle(
                activeSegment.Start,
                activeSegment.Control1,
                activeSegment.Control2,
                activeSegment.End,
                startEndpoint ? EditHandleKind.LineStart : EditHandleKind.LineEnd,
                target) is var adjusted
            && scene.SetPathBezierSegment(
                fill,
                shared.OwnerPartIndex,
                adjusted.Start,
                adjusted.Control1,
                adjusted.Control2,
                adjusted.End,
                rebuildGeometryIndex: false,
                preserveStraightAdjacentSegments: true)
            && scene.UpdateSharedBoundaryIntersection(shared, target, rebuildGeometryIndex: false)
            && scene.TryGetPathBezierSegment(fill, shared.OwnerPartIndex, out var movedActive)
            && scene.TryGetPathBezierSegment(fill, linkedBranch.PartIndex, out var movedLinked)
            && PointsWithin(startEndpoint ? movedActive.Start : movedActive.End, target, 0.01f)
            && PointsWithin(movedLinked.Start, target, 0.01f);
        if (fillParts.Length != 3
            || pieces.Length != 9
            || !allPiecesResolve
            || !prepared
            || !synchronized)
        {
            throw new InvalidOperationException(
                $"A self-intersecting Fill knot did not keep both crossing branches draggable: regions={fillParts.Length}, pieces={pieces.Length}, allResolve={allPiecesResolve}, crossing={crossingPiece}, prepared={prepared}, owner={shared.OwnerPartIndex}, linked=[{string.Join(';', shared.PathAnchors)}], synchronized={synchronized}.");
        }
    }

    private static void RunVirtualFillIntersectionRefreshRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        scene.AddObject(
            0,
            new PointF(0, -280),
            new SizeF(1000, 800),
            0,
            VectorUnits.StrokePointsToUnits(3),
            Color.Transparent,
            Color.White,
            12,
            ShapeKind.Rectangle);
        var fill = scene.AddPathObject(
            0,
            [
                new PointF(-300, -200),
                new PointF(300, -200),
                new PointF(0, 200)
            ],
            0,
            Color.Teal,
            Color.Transparent,
            12);
        var lowerHit = scene.HitTestElement(new PointF(0, 160), 0, toleranceWorld: 2);
        var lowerPieces = lowerHit.Key.Kind == DrawingElementKind.Fill
            ? scene.GetExposedFillBezierSegmentPieces(fill, 0, lowerHit.Key.PartIndex)
            : [];
        var bottom = new PointF(0, 200);
        var activePiece = lowerPieces.FirstOrDefault(piece =>
            PointsWithin(piece.Start, bottom, 0.01f)
            || PointsWithin(piece.End, bottom, 0.01f));
        var handle = PointsWithin(activePiece.Start, bottom, 0.01f)
            ? EditHandleKind.LineStart
            : EditHandleKind.LineEnd;
        if (lowerPieces.Length != 2
            || activePiece.SourcePartIndex < 0
            || !scene.TryConvertFillToBezierPath(fill)
            || !scene.TryMaterializePathBezierSegmentNeighborhood(
                fill,
                activePiece,
                startEndpoint: handle == EditHandleKind.LineStart,
                lowerPieces,
                out var activePartIndex)
            || !scene.TryGetPathBezierSegment(fill, activePartIndex, out var activeSegment))
        {
            throw new InvalidOperationException(
                $"The two-intersection fill refresh case could not materialize its active lower edge: hit={lowerHit}, pieces={lowerPieces.Length}, active={activePiece}.");
        }

        lowerHit = scene.HitTestElement(new PointF(0, 160), 0, toleranceWorld: 2);
        var cachedPieces = scene.GetExposedFillBezierSegmentPieces(fill, 0, lowerHit.Key.PartIndex);
        var bottomPieces = cachedPieces.Where(piece =>
            PointsWithin(piece.Start, bottom, 0.01f)
            || PointsWithin(piece.End, bottom, 0.01f)).ToArray();
        if (bottomPieces.Length != 2
            || bottomPieces.Any(piece => piece.StartIsVirtualAnchor || piece.EndIsVirtualAnchor))
        {
            throw new InvalidOperationException(
                $"The shared fill vertex did not materialize both lower intervals: before=[{string.Join(';', lowerPieces)}], after=[{string.Join(';', bottomPieces)}].");
        }
        var adjacentLowerPiece = cachedPieces.First(piece =>
            piece.SourcePartIndex != activePartIndex
            && (PointsWithin(piece.Start, bottom, 0.01f)
                || PointsWithin(piece.End, bottom, 0.01f)));
        var adjacentIntersection = PointsWithin(adjacentLowerPiece.Start, bottom, 0.01f)
            ? adjacentLowerPiece.End
            : adjacentLowerPiece.Start;
        var upperLeft = new PointF(-300, -200);
        var protectedUpperPiece = scene.GetExposedFillBezierSegmentPieces(fill, 0)
            .First(piece =>
                (PointsWithin(piece.Start, upperLeft, 0.01f)
                    && PointsWithin(piece.End, adjacentIntersection, 1.5f))
                || (PointsWithin(piece.End, upperLeft, 0.01f)
                    && PointsWithin(piece.Start, adjacentIntersection, 1.5f)));
        foreach (var movedBottom in new[]
                 {
                     new PointF(-20, 220),
                     new PointF(-40, 240),
                     new PointF(-60, 260),
                     new PointF(-80, 280)
                 })
        {
            var adjusted = MainForm.AdjustFillEdgeBezierHandle(
                activeSegment.Start,
                activeSegment.Control1,
                activeSegment.Control2,
                activeSegment.End,
                handle,
                movedBottom);
            var refreshed = scene.SetPathBezierSegment(
                    fill,
                    activePartIndex,
                    adjusted.Start,
                    adjusted.Control1,
                    adjusted.Control2,
                    adjusted.End,
                    rebuildGeometryIndex: false,
                    preserveStraightAdjacentSegments: true)
                ? scene.RefreshFillBezierSegmentPieces(fill, cachedPieces)
                : [];
            var refreshedLowerPieces = refreshed
                .Where(piece =>
                    PointsWithin(piece.Start, movedBottom, 0.01f)
                    || PointsWithin(piece.End, movedBottom, 0.01f))
                .ToArray();
            var retainedIntersections = refreshedLowerPieces
                .Select(piece => PointsWithin(piece.Start, movedBottom, 0.01f)
                    ? piece.End
                    : piece.Start)
                .ToArray();
            var refreshedUpperPiece = scene.GetExposedFillBezierSegmentPieces(fill, 0)
                .First(piece =>
                    piece.SourcePartIndex == protectedUpperPiece.SourcePartIndex);
            if (refreshed.Length != cachedPieces.Length
                || refreshedLowerPieces.Any(piece => piece.StartIsVirtualAnchor || piece.EndIsVirtualAnchor)
                || retainedIntersections.Length != 2
                || retainedIntersections.Any(point => Math.Abs(point.Y - 120) > 0.01f)
                || !PointsWithin(refreshedUpperPiece.Start, protectedUpperPiece.Start, 0.01f)
                || !PointsWithin(refreshedUpperPiece.Control1, protectedUpperPiece.Control1, 0.01f)
                || !PointsWithin(refreshedUpperPiece.Control2, protectedUpperPiece.Control2, 0.01f)
                || !PointsWithin(refreshedUpperPiece.End, protectedUpperPiece.End, 0.01f))
            {
                throw new InvalidOperationException(
                    $"Dragging a shared fill vertex changed the protected boundary above one of two intersections: target={movedBottom}, intersections=[{string.Join(';', retainedIntersections)}], protected={refreshedUpperPiece}/{protectedUpperPiece}, pieces={refreshed.Length}/{cachedPieces.Length}.");
            }

            cachedPieces = refreshed;
        }
    }

    private static void RunRepeatedFillAnchorLineEndpointRegression()
    {
        RunCase(-1200, 1200, "long");
        RunCase(-250, -100, "short");

        static void RunCase(float lineStartY, float lineEndY, string label)
        {
            var scene = new VectorScene();
            scene.CreateEmpty();
            var fillBottom = lineEndY + 100;
            var fill = scene.AddObject(
                0,
                new PointF(0, (-200 + fillBottom) * 0.5f),
                new SizeF(2000, fillBottom + 200),
                0,
                0,
                Color.Teal,
                Color.Transparent,
                12,
                ShapeKind.Rectangle);
            var lineDelta = lineEndY - lineStartY;
            var line = scene.AddCubicCurveSegment(
                0,
                new PointF(0, lineStartY),
                new PointF(0, lineStartY + lineDelta / 3f),
                new PointF(0, lineStartY + lineDelta * 2f / 3f),
                new PointF(0, lineEndY),
                VectorUnits.StrokePointsToUnits(3),
                Color.Transparent,
                Color.White,
                12,
                LineEndpointStyle.Sharp,
                LineEndpointStyle.Sharp);
            var initialAnchor = new PointF(0, -200);
            var offsetProbe = new PointF(1, -200);
            var expectedProbeParameter = (-200 - lineStartY) / lineDelta;
            if (!scene.TryGetClosestPointOnLine(
                    line,
                    offsetProbe,
                    out var probeParameter,
                    out var probePoint,
                    out var probeDistance)
                || Math.Abs(probeParameter - expectedProbeParameter) > 0.0001f
                || !PointsWithin(probePoint, initialAnchor, 0.1f)
                || Math.Abs(probeDistance - 1) > 0.05f)
            {
                throw new InvalidOperationException(
                    $"The {label} Line closest-point refinement could not resolve a quantized fill anchor: parameter={probeParameter:F6}/{expectedProbeParameter:F6}, point={probePoint}, distance={probeDistance:F3}.");
            }

            var top = scene.GetEditableFillBezierSegmentParts(fill)
                .First(part => PointsWithin(part.Start, new PointF(-1000, -200), 0.01f));
            if (!scene.TryConvertFillToBezierPath(fill)
                || !scene.TryGetClosestPointOnPathBezierSegment(
                    fill,
                    top.PartIndex,
                    initialAnchor,
                    out var parameter,
                    out _,
                    out var distance)
                || distance > 1.5f
                || !scene.TryInsertPathBezierAnchor(
                    fill,
                    top.PartIndex,
                    parameter,
                    out var anchorPart,
                    out var insertedAnchor)
                || !PointsWithin(insertedAnchor, initialAnchor, 1.5f)
                || !scene.TryGetPathBezierSegment(fill, anchorPart, out var fillSegment))
            {
                throw new InvalidOperationException($"The {label} repeated fill-side Line case could not prepare its shared anchor.");
            }

            var firstBoundaryLinks = scene.CaptureFillBoundaryStrokeLinks(fill, anchorPart, 0);
            var first = scene.CaptureStrokeIntersectionsAtFillAnchor(
                fill,
                anchorPart,
                startEndpoint: true,
                0,
                rebuildGeometryIndex: false,
                excludedLineObjectIndices: firstBoundaryLinks
                    .Select(link => link.LineObjectIndex)
                    .ToHashSet());
            var firstTarget = new PointF(40, -240);
            var firstFill = MainForm.AdjustFillEdgeBezierHandle(
                fillSegment.Start,
                fillSegment.Control1,
                fillSegment.Control2,
                fillSegment.End,
                EditHandleKind.LineStart,
                firstTarget);
            if (firstBoundaryLinks.Length != 0
                || first.LineAnchors.Length != 2
                || scene.ObjectCount != 3
                || !scene.SetPathBezierSegment(
                    fill,
                    anchorPart,
                    firstFill.Start,
                    firstFill.Control1,
                    firstFill.Control2,
                    firstFill.End,
                    rebuildGeometryIndex: false,
                    preserveStraightAdjacentSegments: true)
                || !scene.UpdateSharedBoundaryIntersection(first, firstTarget, rebuildGeometryIndex: false))
            {
                scene.TryGetClosestPointOnLine(
                    line,
                    fillSegment.Start,
                    out var diagnosticParameter,
                    out var diagnosticPoint,
                    out var diagnosticDistance);
                throw new InvalidOperationException(
                    $"The {label} Line did not complete its first natural split: boundaryLinks={firstBoundaryLinks.Length}, endpointLinks={first.LineAnchors.Length}, objects={scene.ObjectCount}, anchor={fillSegment.Start}, closest={diagnosticParameter:F6}/{diagnosticPoint}/{diagnosticDistance:F3}.");
            }
            scene.CompleteDeferredBuild();

            var secondBoundaryLinks = scene.CaptureFillBoundaryStrokeLinks(fill, anchorPart, 0);
            var second = scene.CaptureStrokeIntersectionsAtFillAnchor(
                fill,
                anchorPart,
                startEndpoint: true,
                0,
                rebuildGeometryIndex: false,
                excludedLineObjectIndices: secondBoundaryLinks
                    .Select(link => link.LineObjectIndex)
                    .ToHashSet());
            var secondTarget = new PointF(-60, -160);
            if (!scene.TryGetPathBezierSegment(fill, anchorPart, out var secondFillSource))
            {
                throw new InvalidOperationException($"The {label} repeated fill-side Line case lost its fill segment.");
            }
            var secondFill = MainForm.AdjustFillEdgeBezierHandle(
                secondFillSource.Start,
                secondFillSource.Control1,
                secondFillSource.Control2,
                secondFillSource.End,
                EditHandleKind.LineStart,
                secondTarget);
            if (secondBoundaryLinks.Length != 0
                || second.LineAnchors.Length != 2
                || scene.ObjectCount != 3
                || !scene.SetPathBezierSegment(
                    fill,
                    anchorPart,
                    secondFill.Start,
                    secondFill.Control1,
                    secondFill.Control2,
                    secondFill.End,
                    rebuildGeometryIndex: false,
                    preserveStraightAdjacentSegments: true)
                || !scene.UpdateSharedBoundaryIntersection(second, secondTarget, rebuildGeometryIndex: false))
            {
                throw new InvalidOperationException(
                    $"The {label} Line was missed or split again on the second fill-side edit: boundaryLinks={secondBoundaryLinks.Length}, endpointLinks={second.LineAnchors.Length}, objects={scene.ObjectCount}.");
            }
            scene.CompleteDeferredBuild();

            var lineObjects = Enumerable.Range(0, scene.ObjectCount)
                .Where(index => scene.ShapeKind[index] == ShapeKind.Line)
                .ToArray();
            if (lineObjects.Length != 2
                || second.LineAnchors.Any(anchor =>
                    !scene.TryGetLineEndpoint(anchor.ObjectIndex, anchor.StartEndpoint, out var endpoint)
                    || !PointsWithin(endpoint, secondTarget, 0.01f)))
            {
                throw new InvalidOperationException(
                    $"The {label} repeated fill-side edit left a stale or ghost Line: lines={lineObjects.Length}, objects={scene.ObjectCount}.");
            }
        }
    }

    private static void RunCoincidentFillBoundaryLineRegression()
    {
        RunCase(reversed: false);
        RunCase(reversed: true);

        static void RunCase(bool reversed)
        {
            var scene = new VectorScene();
            scene.CreateEmpty();
            var fill = scene.AddObject(
                0,
                PointF.Empty,
                new SizeF(600, 300),
                0,
                0,
                Color.Teal,
                Color.Transparent,
                12,
                ShapeKind.Rectangle);
            if (!scene.TryConvertFillToBezierPath(fill))
            {
                throw new InvalidOperationException("The coincident fill/Line case could not convert its fill to a path.");
            }

            var top = scene.GetPathBezierSegmentParts(fill)
                .First(part => PointsWithin(part.Start, new PointF(-300, -150), 0.01f));
            var curved = new CubicDrawingPreviewSegment(
                top.Start,
                new PointF(-160, -250),
                new PointF(120, -40),
                top.End);
            if (!scene.SetPathBezierSegment(
                    fill,
                    top.PartIndex,
                    curved.Start,
                    curved.Control1,
                    curved.Control2,
                    curved.End)
                || !scene.TryGetPathBezierSegment(fill, top.PartIndex, out var source))
            {
                throw new InvalidOperationException("The coincident fill/Line case could not curve its source boundary.");
            }

            var line = scene.AddCubicCurveSegment(
                0,
                reversed ? source.End : source.Start,
                reversed ? source.Control2 : source.Control1,
                reversed ? source.Control1 : source.Control2,
                reversed ? source.Start : source.End,
                VectorUnits.StrokePointsToUnits(3),
                Color.Transparent,
                Color.White,
                12,
                LineEndpointStyle.Sharp,
                LineEndpointStyle.Sharp);
            var firstLinks = scene.CaptureFillBoundaryStrokeLinks(fill, top.PartIndex, 0);
            var firstExcluded = firstLinks.Select(link => link.LineObjectIndex).ToHashSet();
            var firstShared = scene.CaptureStrokeIntersectionsAtFillAnchor(
                fill,
                top.PartIndex,
                startEndpoint: true,
                0,
                rebuildGeometryIndex: false,
                excludedLineObjectIndices: firstExcluded);
            var firstTarget = new PointF(-260, -190);
            var firstFill = MainForm.AdjustFillEdgeBezierHandle(
                source.Start,
                source.Control1,
                source.Control2,
                source.End,
                EditHandleKind.LineStart,
                firstTarget);
            if (firstLinks.Length != 1
                || firstLinks[0].LineObjectIndex != line
                || firstShared.LineAnchors.Length != 0
                || !scene.SetPathBezierSegment(
                    fill,
                    top.PartIndex,
                    firstFill.Start,
                    firstFill.Control1,
                    firstFill.Control2,
                    firstFill.End,
                    rebuildGeometryIndex: false,
                    preserveStraightAdjacentSegments: true)
                || !scene.UpdateFillBoundaryStrokeLinks(
                    firstLinks,
                    firstFill.Start,
                    firstFill.Control1,
                    firstFill.Control2,
                    firstFill.End,
                    rebuildGeometryIndex: false))
            {
                throw new InvalidOperationException(
                    $"The {(reversed ? "reversed" : "forward")} coincident Line received overlapping synchronization roles.");
            }
            scene.CompleteDeferredBuild();

            var secondLinks = scene.CaptureFillBoundaryStrokeLinks(fill, top.PartIndex, 0);
            var secondExcluded = secondLinks.Select(link => link.LineObjectIndex).ToHashSet();
            var secondShared = scene.CaptureStrokeIntersectionsAtFillAnchor(
                fill,
                top.PartIndex,
                startEndpoint: true,
                0,
                rebuildGeometryIndex: false,
                excludedLineObjectIndices: secondExcluded);
            if (!scene.TryGetPathBezierSegment(fill, top.PartIndex, out var secondSource))
            {
                throw new InvalidOperationException("The coincident fill/Line case lost its second source segment.");
            }
            var secondTarget = new PointF(-280, -130);
            var secondFill = MainForm.AdjustFillEdgeBezierHandle(
                secondSource.Start,
                secondSource.Control1,
                secondSource.Control2,
                secondSource.End,
                EditHandleKind.LineStart,
                secondTarget);
            if (secondLinks.Length != 1
                || secondShared.LineAnchors.Length != 0
                || !scene.SetPathBezierSegment(
                    fill,
                    top.PartIndex,
                    secondFill.Start,
                    secondFill.Control1,
                    secondFill.Control2,
                    secondFill.End,
                    rebuildGeometryIndex: false,
                    preserveStraightAdjacentSegments: true)
                || !scene.UpdateFillBoundaryStrokeLinks(
                    secondLinks,
                    secondFill.Start,
                    secondFill.Control1,
                    secondFill.Control2,
                    secondFill.End,
                    rebuildGeometryIndex: false)
                || !scene.TryGetLineCubic(line, out var lineStart, out var lineControl1, out var lineControl2, out var lineEnd)
                || scene.ObjectCount != 2
                || !PointsWithin(lineStart, reversed ? secondFill.End : secondFill.Start, 0.25f)
                || !PointsWithin(lineControl1, reversed ? secondFill.Control2 : secondFill.Control1, 0.25f)
                || !PointsWithin(lineControl2, reversed ? secondFill.Control1 : secondFill.Control2, 0.25f)
                || !PointsWithin(lineEnd, reversed ? secondFill.Start : secondFill.End, 0.25f))
            {
                scene.TryGetLineCubic(
                    line,
                    out var diagnosticStart,
                    out var diagnosticControl1,
                    out var diagnosticControl2,
                    out var diagnosticEnd);
                scene.TryGetPathBezierSegment(fill, top.PartIndex, out var diagnosticFill);
                throw new InvalidOperationException(
                    $"The {(reversed ? "reversed" : "forward")} coincident Line diverged or produced a ghost on its second fill-side edit: links={secondLinks.Length}, shared={secondShared.LineAnchors.Length}, objects={scene.ObjectCount}, fill={diagnosticFill}, line={diagnosticStart}/{diagnosticControl1}/{diagnosticControl2}/{diagnosticEnd}, expected={secondFill}.");
            }
        }
    }

    private static void RunGradientPaintRegression()
    {
        RunFillEdgeAntialiasingRegression();
        var lineAxisScene = new VectorScene();
        lineAxisScene.CreateEmpty();
        var verticalLine = lineAxisScene.AddLineSegment(
            0,
            new PointF(240, 40),
            new PointF(240, 180),
            VectorUnits.StrokePointsToUnits(12),
            Color.Transparent,
            Color.White,
            8);
        lineAxisScene.SetGradientPaint(
            verticalLine,
            GradientKind.Linear,
            [new GradientStop(0, Color.Coral), new GradientStop(1, Color.RoyalBlue)]);
        var lineDefaultAxis = lineAxisScene.HasGradient(verticalLine)
            && lineAxisScene.GetGradientKind(verticalLine) == GradientKind.Linear
            && PointsNear(lineAxisScene.GetGradientStart(verticalLine), new PointF(240, 40))
            && PointsNear(lineAxisScene.GetGradientEnd(verticalLine), new PointF(240, 180));
        var adjacentLine = lineAxisScene.AddLineSegment(
            0,
            new PointF(240, 184),
            new PointF(240, 324),
            VectorUnits.StrokePointsToUnits(12),
            Color.Transparent,
            Color.White,
            8);
        var gradientLineMerge = lineAxisScene.MergeCompatibleLineSegments(0);
        var lineGradientMergeProtected = !gradientLineMerge.Changed
            && lineAxisScene.ObjectCount == 2
            && lineAxisScene.HasGradient(verticalLine)
            && !lineAxisScene.HasGradient(adjacentLine);

        var scene = new VectorScene();
        scene.CreateEmpty();
        var rectangle = scene.AddObject(
            0,
            new PointF(100, 50),
            new SizeF(80, 40),
            0,
            0,
            Color.Teal,
            12,
            ShapeKind.Rectangle);
        var line = scene.AddLineSegment(
            0,
            new PointF(20, 150),
            new PointF(180, 150),
            VectorUnits.StrokePointsToUnits(18),
            Color.Transparent,
            Color.White,
            12);
        scene.SetLinearGradient(
            rectangle,
            Color.Coral,
            Color.RoyalBlue,
            new PointF(60, 50),
            new PointF(140, 50));
        var snapshot = scene.CreateSnapshot();
        scene.TransformObjects([rectangle], point => new PointF(point.X + 25, point.Y - 10));
        var transformed = scene.HasLinearGradient(rectangle)
            && scene.GradientStartArgb[rectangle] == Color.Coral.ToArgb()
            && scene.GradientEndArgb[rectangle] == Color.RoyalBlue.ToArgb()
            && PointsNear(scene.GetGradientStart(rectangle), new PointF(85, 40))
            && PointsNear(scene.GetGradientEnd(rectangle), new PointF(165, 40));
        scene.RestoreSnapshot(snapshot);
        var restored = scene.HasLinearGradient(rectangle)
            && PointsNear(scene.GetGradientStart(rectangle), new PointF(60, 50))
            && PointsNear(scene.GetGradientEnd(rectangle), new PointF(140, 50));
        scene.ShearObjects([rectangle], point => new PointF(point.X + point.Y * 0.5f, point.Y));
        var sheared = scene.HasLinearGradient(rectangle)
            && PointsNear(scene.GetGradientStart(rectangle), new PointF(85, 50))
            && PointsNear(scene.GetGradientEnd(rectangle), new PointF(165, 50));
        scene.RestoreSnapshot(snapshot);
        scene.SetGradientPaint(
            rectangle,
            GradientKind.Radial,
            [
                new GradientStop(0, Color.Coral),
                new GradientStop(0.45f, Color.Gold),
                new GradientStop(1, Color.RoyalBlue)
            ],
            new PointF(100, 50),
            new PointF(160, 50));
        var radialSnapshot = scene.CreateSnapshot();
        scene.TransformObjects([rectangle], point => new PointF(point.X - 10, point.Y + 30));
        var radial = scene.GetGradientKind(rectangle) == GradientKind.Radial
            && scene.GetGradientStops(rectangle).Length == 3
            && scene.GetGradientStops(rectangle)[1].Argb == Color.Gold.ToArgb()
            && PointsNear(scene.GetGradientStart(rectangle), new PointF(90, 80))
            && PointsNear(scene.GetGradientEnd(rectangle), new PointF(150, 80));
        scene.RestoreSnapshot(radialSnapshot);
        var radialRestored = scene.GetGradientKind(rectangle) == GradientKind.Radial
            && scene.GetGradientStops(rectangle).Length == 3
            && PointsNear(scene.GetGradientStart(rectangle), new PointF(100, 50));
        scene.SetGradientPaint(
            line,
            GradientKind.Linear,
            [
                new GradientStop(0, Color.Coral),
                new GradientStop(0.5f, Color.Gold),
                new GradientStop(1, Color.RoyalBlue)
            ],
            new PointF(20, 150),
            new PointF(180, 150));
        var lineSnapshot = scene.CreateSnapshot();
        scene.TransformObjects([line], point => new PointF(point.X + 20, point.Y - 10));
        var lineLinear = scene.HasGradient(line)
            && scene.GetGradientKind(line) == GradientKind.Linear
            && scene.GetGradientStops(line).Length == 3
            && PointsNear(scene.GetGradientStart(line), new PointF(40, 140))
            && PointsNear(scene.GetGradientEnd(line), new PointF(200, 140));
        scene.RestoreSnapshot(lineSnapshot);
        scene.SetGradientPaint(
            line,
            GradientKind.Radial,
            [
                new GradientStop(0, Color.Coral),
                new GradientStop(1, Color.RoyalBlue)
            ],
            new PointF(100, 150),
            new PointF(180, 150));
        var lineRadial = scene.HasGradient(line)
            && scene.GetGradientKind(line) == GradientKind.Radial
            && scene.GetGradientStops(line).Length == 2;
        var sharpGradientFirst = scene.AddLineSegment(
            0,
            new PointF(-120, -60),
            new PointF(-40, -60),
            VectorUnits.StrokePointsToUnits(16),
            Color.Transparent,
            Color.Coral,
            12,
            LineEndpointStyle.Round,
            LineEndpointStyle.Sharp);
        var sharpGradientSecond = scene.AddLineSegment(
            0,
            new PointF(-40, -60),
            new PointF(-40, 20),
            VectorUnits.StrokePointsToUnits(16),
            Color.Transparent,
            Color.Coral,
            12,
            LineEndpointStyle.Sharp,
            LineEndpointStyle.Round);
        scene.SetGradientPaint(
            sharpGradientFirst,
            GradientKind.Linear,
            [new GradientStop(0, Color.RoyalBlue), new GradientStop(1, Color.Coral)]);
        scene.SetGradientPaint(
            sharpGradientSecond,
            GradientKind.Radial,
            [new GradientStop(0, Color.Coral), new GradientStop(1, Color.RoyalBlue)],
            new PointF(-40, -60),
            new PointF(40, -60));
        var gradientSharpJoin = scene.TryGetLineJoinNeighbor(sharpGradientFirst, startEndpoint: false, 0, out var sharpNeighbor, out var sharpNeighborStarts)
            && sharpNeighbor == sharpGradientSecond
            && sharpNeighborStarts
            && scene.HasGradient(sharpGradientFirst)
            && scene.HasGradient(sharpGradientSecond)
            && LineJoinGeometry.TryCreateMiter(
                new PointF(-40, -60),
                new PointF(-120, -60),
                new PointF(-40, 20),
                VectorUnits.StrokePointsToUnits(8),
                out _);

        var materializedScene = new VectorScene();
        materializedScene.CreateEmpty();
        var outlinedGradientFill = materializedScene.AddObject(
            0,
            new PointF(300, 120),
            new SizeF(120, 80),
            0,
            VectorUnits.StrokePointsToUnits(6),
            Color.Coral,
            Color.White,
            16,
            ShapeKind.Rectangle);
        var outlinedGradientStops = new[]
        {
            new GradientStop(0, Color.Coral),
            new GradientStop(0.4f, Color.Gold),
            new GradientStop(1, Color.RoyalBlue)
        };
        materializedScene.SetGradientPaint(
            outlinedGradientFill,
            GradientKind.Radial,
            outlinedGradientStops,
            new PointF(300, 120),
            new PointF(360, 120));
        var sourceFillKey = new DrawingElementKey(outlinedGradientFill, DrawingElementKind.Fill, 0);
        var materialization = materializedScene.MaterializeSelectedParts([sourceFillKey], 0);
        var materializedFill = materialization.Parts
            .Where(part => part.Source == sourceFillKey)
            .Select(part => part.Result.ObjectIndex)
            .FirstOrDefault(-1);
        materializedScene.TransformObjects([materializedFill], point => new PointF(point.X + 45, point.Y - 20));
        var materializedFillGradient = materialization.Success
            && materialization.Changed
            && materializedFill >= 0
            && materializedScene.HasGradient(materializedFill)
            && materializedScene.GetGradientKind(materializedFill) == GradientKind.Radial
            && materializedScene.GetGradientStops(materializedFill).SequenceEqual(outlinedGradientStops)
            && PointsNear(materializedScene.GetGradientStart(materializedFill), new PointF(345, 100))
            && PointsNear(materializedScene.GetGradientEnd(materializedFill), new PointF(405, 100));

        scene.SetGradientStops(
            rectangle,
            [
                new GradientStop(0, Color.Coral),
                new GradientStop(0.2f, Color.Gold),
                new GradientStop(0.65f, Color.MediumPurple),
                new GradientStop(1, Color.RoyalBlue)
            ]);
        var editableStops = scene.GetGradientStops(rectangle);
        editableStops[2] = new GradientStop(0.72f, editableStops[2].Argb);
        scene.SetGradientStops(rectangle, editableStops);
        var stopMovePreserved = scene.GetGradientStops(rectangle).Length == 4
            && Math.Abs(scene.GetGradientStops(rectangle)[2].Position - 0.72f) < 0.0001f;
        var shapeRadialScene = new VectorScene();
        shapeRadialScene.CreateEmpty();
        var irregularContour = new[]
        {
            new PointF(-140, -30),
            new PointF(-70, -110),
            new PointF(90, -80),
            new PointF(170, 10),
            new PointF(55, 135),
            new PointF(-125, 80)
        };
        var shapeRadialObject = shapeRadialScene.AddPathObject(
            0,
            irregularContour,
            0,
            Color.Coral,
            Color.Transparent,
            24);
        var shapeRadialStops = new[]
        {
            new GradientStop(0, Color.Gold),
            new GradientStop(0.5f, Color.Coral),
            new GradientStop(1, Color.RoyalBlue)
        };
        shapeRadialScene.SetShapeRadialGradient(shapeRadialObject, Color.Gold, Color.RoyalBlue, PointF.Empty);
        shapeRadialScene.SetGradientStops(shapeRadialObject, shapeRadialStops);
        var shapeContours = shapeRadialScene.GetObjectBoundaryContours(shapeRadialObject);
        shapeRadialScene.SetShapeGradientMapping(shapeRadialObject, shapeContours);
        shapeRadialScene.TryGetShapeGradientMappingWorldContours(shapeRadialObject, out var appliedShapeMapping);
        var foundShapeBoundary = GradientPaintUtilities.TryFindShapeBoundaryPoint(
            shapeContours,
            PointF.Empty,
            new PointF(1, 0),
            out var shapeBoundary);
        var shapeHalfway = new PointF(shapeBoundary.X * 0.5f, shapeBoundary.Y * 0.5f);
        var shapeEdgePosition = GradientPaintUtilities.ShapeRadialPosition(shapeContours, PointF.Empty, shapeBoundary);
        var shapeHalfPosition = GradientPaintUtilities.ShapeRadialPosition(shapeContours, PointF.Empty, shapeHalfway);
        var shapeGuideEnd = shapeRadialScene.GetGradientEnd(shapeRadialObject);
        var disjointRayContours = new[]
        {
            new[]
            {
                new PointF(-20, -20), new PointF(30, -20), new PointF(30, 20), new PointF(-20, 20)
            },
            new[]
            {
                new PointF(80, -20), new PointF(150, -20), new PointF(150, 20), new PointF(80, 20)
            }
        };
        var shapeUsesOutermostRayBoundary = GradientPaintUtilities.TryFindShapeBoundaryPoint(
                disjointRayContours,
                PointF.Empty,
                new PointF(1, 0),
                out var outerRayBoundary)
            && Math.Abs(outerRayBoundary.X - 150f) < 0.001f;
        var shapeRadialMapping = foundShapeBoundary
            && Math.Abs(shapeEdgePosition - 1f) < 0.001f
            && Math.Abs(shapeHalfPosition - 0.5f) < 0.01f
            && Math.Abs(shapeGuideEnd.X - shapeBoundary.X) <= 0.5f
            && Math.Abs(shapeGuideEnd.Y - shapeBoundary.Y) <= 0.5f
            && shapeUsesOutermostRayBoundary;
        var shapeRadialSnapshot = shapeRadialScene.CreateSnapshot();
        shapeRadialScene.DisableLinearGradient(shapeRadialObject);
        shapeRadialScene.RestoreSnapshot(shapeRadialSnapshot);
        var shapeRadialRestored = shapeRadialScene.GetGradientKind(shapeRadialObject) == GradientKind.ShapeRadial
            && shapeRadialScene.GetGradientStops(shapeRadialObject).SequenceEqual(shapeRadialStops)
            && shapeRadialScene.TryGetShapeGradientMappingWorldContours(shapeRadialObject, out var restoredShapeMapping)
            && restoredShapeMapping.SelectMany(contour => contour).SequenceEqual(appliedShapeMapping.SelectMany(contour => contour));
        if (!lineDefaultAxis || !lineGradientMergeProtected || !transformed || !restored || !sheared || !radial || !radialRestored || !lineLinear || !lineRadial || !gradientSharpJoin || !materializedFillGradient || !stopMovePreserved || !shapeRadialMapping || !shapeRadialRestored)
        {
            throw new InvalidOperationException(
                $"Gradient paint regression failed: axis={lineDefaultAxis}, merge={lineGradientMergeProtected}, transformed={transformed}, restored={restored}, sheared={sheared}, radial={radial}, radialRestored={radialRestored}, lineLinear={lineLinear}, lineRadial={lineRadial}, sharpJoin={gradientSharpJoin}, materializedFill={materializedFillGradient}, stops={stopMovePreserved}, shapeMapping={shapeRadialMapping}, foundBoundary={foundShapeBoundary}, edgePosition={shapeEdgePosition:0.###}, halfPosition={shapeHalfPosition:0.###}, boundary={shapeBoundary}, guide={shapeGuideEnd}, shapeRestored={shapeRadialRestored}.");
        }

        using var material = new MaterialEditorPanel();
        material.SetMaterial(Color.Gold, Color.Coral, 2f, 1f);
        material.SetGradient(GradientKind.Solid, [new GradientStop(0, Color.Coral), new GradientStop(1, Color.Coral)]);
        material.SetGradientPreviewTarget(strokeTarget: true);
        material.SetGradientKind(GradientKind.Linear);
        var lineGradientEditingStops = material.GradientKind == GradientKind.Linear
            && material.EditingGradientStop
            && material.GradientStops.All(stop => stop.Argb == Color.Coral.ToArgb());
        material.SetGradientKind(GradientKind.Solid);
        var gradientSettingsCollapsed = !material.GradientSettingsExpanded;
        material.SetGradientKind(GradientKind.Linear);
        var gradientSettingsExpanded = material.GradientSettingsExpanded;
        material.SetGradientKind(GradientKind.Solid);
        if (!lineGradientEditingStops || material.EditingGradientStop || !gradientSettingsCollapsed || !gradientSettingsExpanded)
        {
            throw new InvalidOperationException("Switching line paint modes did not route the color editor to gradient stops, expand gradient settings, and restore the solid state.");
        }

        var gradientMaterialChanged = false;
        var gradientChanged = false;
        material.MaterialChanged += (_, _) => gradientMaterialChanged = true;
        material.GradientChanged += (_, _) => gradientChanged = true;
        material.SetGradient(GradientKind.Linear, [new GradientStop(0, Color.Coral), new GradientStop(1, Color.RoyalBlue)]);
        material.Opacity = 0.5f;
        var gradientAlphaApplied = gradientChanged
            && !gradientMaterialChanged
            && material.Fill.A == 128
            && material.Stroke.A == 128
            && material.GradientStops.All(stop => Color.FromArgb(stop.Argb).A == 128);
        if (!gradientAlphaApplied)
        {
            throw new InvalidOperationException("Uniform alpha did not update all gradient stops without falling back to the solid material path.");
        }

        material.SetGradient(GradientKind.Solid, [new GradientStop(0, Color.White), new GradientStop(1, Color.White)]);
        material.SetGradientPreviewTarget(strokeTarget: false);
        material.SetGradient(GradientKind.Linear, [new GradientStop(0, Color.Coral), new GradientStop(1, Color.RoyalBlue)]);
        material.SetGradientPreviewTarget(strokeTarget: true);
        var strokeGradientInitiallySolid = material.GradientKind == GradientKind.Solid;
        material.SetGradient(GradientKind.Radial, [new GradientStop(0, Color.Gold), new GradientStop(1, Color.Teal)]);
        material.SetGradientPreviewTarget(strokeTarget: false);
        var fillGradientRetained = material.GradientKind == GradientKind.Linear
            && material.GradientStops.SequenceEqual([new GradientStop(0, Color.Coral), new GradientStop(1, Color.RoyalBlue)]);
        material.SetGradientPreviewTarget(strokeTarget: true);
        var strokeGradientRetained = material.GradientKind == GradientKind.Radial
            && material.GradientStops.SequenceEqual([new GradientStop(0, Color.Gold), new GradientStop(1, Color.Teal)]);
        if (!strokeGradientInitiallySolid || !fillGradientRetained || !strokeGradientRetained)
        {
            throw new InvalidOperationException("Independent Fill and Stroke gradient palettes were not retained while no object was selected.");
        }

        material.SetGradientPreviewTarget(strokeTarget: false);
        material.SetGradientKind(GradientKind.ShapeRadial);
        var shapeRadialMaterialMode = material.GradientKind == GradientKind.ShapeRadial
            && material.EditingGradientStop;
        using var shapePreview = new Bitmap(96, 48);
        using (var graphics = Graphics.FromImage(shapePreview))
        {
            graphics.Clear(Color.Transparent);
            GradientPreviewRenderer.Draw(graphics, new Rectangle(0, 0, shapePreview.Width, shapePreview.Height), GradientKind.ShapeRadial, shapeRadialStops);
        }
        var shapePreviewColors = new HashSet<int>();
        for (var y = 0; y < shapePreview.Height; y += 2)
        {
            for (var x = 0; x < shapePreview.Width; x += 2)
            {
                var color = shapePreview.GetPixel(x, y);
                if (color.A > 0) shapePreviewColors.Add(color.ToArgb());
            }
        }
        if (!shapeRadialMaterialMode || shapePreviewColors.Count < 4)
        {
            throw new InvalidOperationException($"The shape radial material mode or irregular preview was unavailable: mode={shapeRadialMaterialMode}, colors={shapePreviewColors.Count}.");
        }

        var selectionShortcut = ToolShortcutMap.ResolveTool(
            ToolShortcutPreset.NumberKeys,
            Keys.D1,
            ToolMode.Transform,
            ToolMode.Rectangle,
            ToolMode.Pen,
            ToolMode.PressureBrush,
            ToolMode.InkBottle);
        var fillShortcut = ToolShortcutMap.ResolveTool(
            ToolShortcutPreset.NumberKeys,
            Keys.NumPad5,
            ToolMode.Select,
            ToolMode.Rectangle,
            ToolMode.Line,
            ToolMode.Brush,
            ToolMode.InkBottle);
        var traditionalSelection = ToolShortcutMap.ResolveTool(
            ToolShortcutPreset.TraditionalFlash,
            Keys.V,
            ToolMode.Transform,
            ToolMode.Star,
            ToolMode.Pencil,
            ToolMode.PressureBrush,
            ToolMode.InkBottle);
        var traditionalInkBottle = ToolShortcutMap.ResolveTraditionalFlashTool(Keys.S);
        var traditionalPen = ToolShortcutMap.ResolveTraditionalFlashTool(Keys.P);
        var traditionalText = ToolShortcutMap.ResolveTraditionalFlashTool(Keys.T);
        var defaultSettings = new ApplicationSettings();
        using var localizedLabel = new Label { Text = "Settings" };
        using var localizedMenu = new AnimatedContextMenuStrip();
        var localizedMenuItem = new ToolStripMenuItem("Rename");
        localizedMenu.Items.Add(localizedMenuItem);
        UiLocalization.Watch(localizedLabel);
        UiLocalization.SetLanguage(UiLanguage.SimplifiedChinese);
        var chineseLocalizationApplied = localizedLabel.Text == "设置"
            && localizedMenuItem.Text == "重命名"
            && UiLocalization.T("Selected: 2 objects") == "已选择：2 个对象";
        UiLocalization.SetLanguage(UiLanguage.English);
        var englishLocalizationRestored = localizedLabel.Text == "Settings" && localizedMenuItem.Text == "Rename";
        var legacySettings = System.Text.Json.JsonSerializer.Deserialize<ApplicationSettings>("{}");
        var normalizedHues = ApplicationSettingsStore.Normalize(new ApplicationSettings
        {
            ColorTheme = (ApplicationColorTheme)999,
            ThemeHueDegrees = -1,
            ThemeSaturationPercent = -1,
            ThemeBrightnessPercent = 999,
            AccentHueDegrees = 360,
            AccentSaturationPercent = 999,
            AccentBrightnessPercent = -1
        });
        var serializedHues = new ApplicationSettings
        {
            ColorTheme = ApplicationColorTheme.White,
            ThemeHueDegrees = 27,
            ThemeSaturationPercent = 84,
            ThemeBrightnessPercent = 112,
            AccentHueDegrees = 314,
            AccentSaturationPercent = 136,
            AccentBrightnessPercent = 92
        };
        var roundTripHues = System.Text.Json.JsonSerializer.Deserialize<ApplicationSettings>(
            System.Text.Json.JsonSerializer.Serialize(serializedHues));
        var defaultPalette = Theme.PaletteForHues(
            ApplicationSettings.DefaultThemeHueDegrees,
            ApplicationSettings.DefaultAccentHueDegrees);
        var themeShiftedPalette = Theme.PaletteForHues(
            ApplicationSettings.DefaultThemeHueDegrees + 120,
            ApplicationSettings.DefaultAccentHueDegrees);
        var accentShiftedPalette = Theme.PaletteForHues(
            ApplicationSettings.DefaultThemeHueDegrees,
            ApplicationSettings.DefaultAccentHueDegrees + 120);
        var whitePalette = Theme.PaletteFor(
            ApplicationColorTheme.White,
            ApplicationSettings.DefaultThemeHueDegrees,
            ApplicationSettings.DefaultAccentHueDegrees);
        var whiteShiftedPalette = Theme.PaletteFor(
            ApplicationColorTheme.White,
            ApplicationSettings.DefaultThemeHueDegrees + 120,
            ApplicationSettings.DefaultAccentHueDegrees);
        var themeAdjustedPalette = Theme.PaletteForAdjustments(
            ApplicationColorTheme.Dark,
            ApplicationSettings.DefaultThemeHueDegrees,
            0,
            150,
            ApplicationSettings.DefaultAccentHueDegrees,
            ApplicationSettings.DefaultAccentSaturationPercent,
            ApplicationSettings.DefaultAccentBrightnessPercent);
        var accentAdjustedPalette = Theme.PaletteForAdjustments(
            ApplicationColorTheme.Dark,
            ApplicationSettings.DefaultThemeHueDegrees,
            ApplicationSettings.DefaultThemeSaturationPercent,
            ApplicationSettings.DefaultThemeBrightnessPercent,
            ApplicationSettings.DefaultAccentHueDegrees,
            0,
            50);
        if (legacySettings is null
            || legacySettings.ColorTheme != ApplicationColorTheme.Dark
            || legacySettings.ThemeHueDegrees != ApplicationSettings.DefaultThemeHueDegrees
            || legacySettings.ThemeSaturationPercent != ApplicationSettings.DefaultThemeSaturationPercent
            || legacySettings.ThemeBrightnessPercent != ApplicationSettings.DefaultThemeBrightnessPercent
            || legacySettings.AccentHueDegrees != ApplicationSettings.DefaultAccentHueDegrees
            || legacySettings.AccentSaturationPercent != ApplicationSettings.DefaultAccentSaturationPercent
            || legacySettings.AccentBrightnessPercent != ApplicationSettings.DefaultAccentBrightnessPercent
            || normalizedHues.ColorTheme != ApplicationColorTheme.Dark
            || normalizedHues.ThemeHueDegrees != 359
            || normalizedHues.ThemeSaturationPercent != ApplicationSettings.MinimumSaturationPercent
            || normalizedHues.ThemeBrightnessPercent != ApplicationSettings.MaximumBrightnessPercent
            || normalizedHues.AccentHueDegrees != 0
            || normalizedHues.AccentSaturationPercent != ApplicationSettings.MaximumSaturationPercent
            || normalizedHues.AccentBrightnessPercent != ApplicationSettings.MinimumBrightnessPercent
            || roundTripHues is null
            || roundTripHues.ColorTheme != ApplicationColorTheme.White
            || roundTripHues.ThemeHueDegrees != serializedHues.ThemeHueDegrees
            || roundTripHues.ThemeSaturationPercent != serializedHues.ThemeSaturationPercent
            || roundTripHues.ThemeBrightnessPercent != serializedHues.ThemeBrightnessPercent
            || roundTripHues.AccentHueDegrees != serializedHues.AccentHueDegrees
            || roundTripHues.AccentSaturationPercent != serializedHues.AccentSaturationPercent
            || roundTripHues.AccentBrightnessPercent != serializedHues.AccentBrightnessPercent
            || defaultPalette.App != Color.FromArgb(18, 20, 22)
            || defaultPalette.Accent != Color.FromArgb(79, 179, 162)
            || themeShiftedPalette.Panel == defaultPalette.Panel
            || themeShiftedPalette.Accent != defaultPalette.Accent
            || accentShiftedPalette.Panel != defaultPalette.Panel
            || accentShiftedPalette.Accent == defaultPalette.Accent
            || themeShiftedPalette.Warning != defaultPalette.Warning
            || accentShiftedPalette.Danger != defaultPalette.Danger
            || whitePalette.App != Color.FromArgb(242, 244, 245)
            || whitePalette.Text != Color.FromArgb(31, 39, 43)
            || whitePalette.App.GetBrightness() <= whitePalette.Text.GetBrightness()
            || whitePalette.Accent == defaultPalette.Accent
            || whiteShiftedPalette.Panel == whitePalette.Panel
            || whiteShiftedPalette.Accent != whitePalette.Accent
            || themeAdjustedPalette.Panel.GetSaturation() > 0.001f
            || themeAdjustedPalette.Panel.GetBrightness() <= defaultPalette.Panel.GetBrightness()
            || themeAdjustedPalette.Accent != defaultPalette.Accent
            || accentAdjustedPalette.Panel != defaultPalette.Panel
            || accentAdjustedPalette.Accent.GetSaturation() > 0.001f
            || accentAdjustedPalette.Accent.GetBrightness() >= defaultPalette.Accent.GetBrightness())
        {
            throw new InvalidOperationException("Application theme settings were not compatible, normalized, or independently applied.");
        }

        using (var themePreviewDialog = new SettingsDialog(
            ShortcutProfiles.TraditionalFlashProfileId,
            [],
            UiLanguage.English,
            ApplicationColorTheme.Dark,
            ApplicationSettings.DefaultThemeHueDegrees,
            ApplicationSettings.DefaultThemeSaturationPercent,
            ApplicationSettings.DefaultThemeBrightnessPercent,
            ApplicationSettings.DefaultAccentHueDegrees,
            ApplicationSettings.DefaultAccentSaturationPercent,
            ApplicationSettings.DefaultAccentBrightnessPercent))
        {
            var previewEvents = 0;
            themePreviewDialog.ThemePreviewChanged += (_, _) => previewEvents++;
            var adjustmentSliders = new Dictionary<string, ColorComponentSlider>(StringComparer.Ordinal);
            var pendingControls = new Stack<Control>();
            pendingControls.Push(themePreviewDialog);
            while (pendingControls.Count > 0)
            {
                var control = pendingControls.Pop();
                if (control is ColorComponentSlider slider
                    && !string.IsNullOrWhiteSpace(slider.AccessibleName))
                {
                    adjustmentSliders[slider.AccessibleName] = slider;
                }
                foreach (Control child in control.Controls) pendingControls.Push(child);
            }

            string[] requiredAdjustmentSliders =
            [
                "Application theme color hue in degrees",
                "Application theme color saturation in percent",
                "Application theme color brightness in percent",
                "Application highlight color hue in degrees",
                "Application highlight color saturation in percent",
                "Application highlight color brightness in percent"
            ];
            if (requiredAdjustmentSliders.Any(name => !adjustmentSliders.ContainsKey(name)))
            {
                throw new InvalidOperationException("The application theme H/S/B sliders were not all available for live preview.");
            }

            adjustmentSliders[requiredAdjustmentSliders[0]].Value = ApplicationSettings.DefaultThemeHueDegrees + 1;
            adjustmentSliders[requiredAdjustmentSliders[1]].Value = 120;
            adjustmentSliders[requiredAdjustmentSliders[2]].Value = 110;
            adjustmentSliders[requiredAdjustmentSliders[3]].Value = ApplicationSettings.DefaultAccentHueDegrees + 2;
            adjustmentSliders[requiredAdjustmentSliders[4]].Value = 130;
            adjustmentSliders[requiredAdjustmentSliders[5]].Value = 90;
            if (previewEvents != requiredAdjustmentSliders.Length
                || themePreviewDialog.SelectedThemeHueDegrees != ApplicationSettings.DefaultThemeHueDegrees + 1
                || themePreviewDialog.SelectedThemeSaturationPercent != 120
                || themePreviewDialog.SelectedThemeBrightnessPercent != 110
                || themePreviewDialog.SelectedAccentHueDegrees != ApplicationSettings.DefaultAccentHueDegrees + 2
                || themePreviewDialog.SelectedAccentSaturationPercent != 130
                || themePreviewDialog.SelectedAccentBrightnessPercent != 90)
            {
                throw new InvalidOperationException("Application theme H/S/B changes did not emit independent live-preview updates.");
            }
        }

        var originalTheme = (
            Theme.ColorTheme,
            Theme.ThemeHueDegrees,
            Theme.ThemeSaturationPercent,
            Theme.ThemeBrightnessPercent,
            Theme.AccentHueDegrees,
            Theme.AccentSaturationPercent,
            Theme.AccentBrightnessPercent);
        var themeRefreshApplied = false;
        try
        {
            Theme.ConfigureColorAdjustments(
                ApplicationColorTheme.Dark,
                ApplicationSettings.DefaultThemeHueDegrees,
                ApplicationSettings.DefaultThemeSaturationPercent,
                ApplicationSettings.DefaultThemeBrightnessPercent,
                ApplicationSettings.DefaultAccentHueDegrees,
                ApplicationSettings.DefaultAccentSaturationPercent,
                ApplicationSettings.DefaultAccentBrightnessPercent);
            var previousPalette = Theme.CurrentPalette;
            using var previewSurface = new Panel { BackColor = previousPalette.Panel };
            var previewLabel = new Label
            {
                BackColor = previousPalette.Panel,
                ForeColor = previousPalette.Muted
            };
            var previewInput = new TextBox
            {
                BackColor = previousPalette.Field,
                ForeColor = previousPalette.Text
            };
            var previewButton = new Button();
            Theme.StyleActiveButton(previewButton);
            var workspaceColor = previousPalette.Stage;
            var previewStage = new StageControl(scene) { BackColor = workspaceColor };
            previewSurface.Controls.Add(previewLabel);
            previewSurface.Controls.Add(previewInput);
            previewSurface.Controls.Add(previewButton);
            previewSurface.Controls.Add(previewStage);

            Theme.ConfigureColorAdjustments(
                ApplicationColorTheme.White,
                ApplicationSettings.DefaultThemeHueDegrees + 120,
                125,
                110,
                ApplicationSettings.DefaultAccentHueDegrees + 60,
                135,
                90);
            var nextPalette = Theme.CurrentPalette;
            Theme.RefreshControlTree(previewSurface, previousPalette);
            themeRefreshApplied = previewSurface.BackColor == nextPalette.Panel
                && previewLabel.BackColor == nextPalette.Panel
                && previewLabel.ForeColor == nextPalette.Muted
                && previewInput.BackColor == nextPalette.Field
                && previewInput.ForeColor == nextPalette.Text
                && previewButton.ForeColor == nextPalette.AccentLabel
                && UiMotion.IsActive(previewButton)
                && previewStage.BackColor == workspaceColor;
        }
        finally
        {
            Theme.ConfigureColorAdjustments(
                originalTheme.ColorTheme,
                originalTheme.ThemeHueDegrees,
                originalTheme.ThemeSaturationPercent,
                originalTheme.ThemeBrightnessPercent,
                originalTheme.AccentHueDegrees,
                originalTheme.AccentSaturationPercent,
                originalTheme.AccentBrightnessPercent);
        }
        if (!themeRefreshApplied)
        {
            throw new InvalidOperationException("Live application theme refresh did not recolor existing controls or preserve the workspace color.");
        }

        var migratedNumberSettings = ApplicationSettingsStore.Normalize(new ApplicationSettings
        {
            ToolShortcutPreset = ToolShortcutPreset.NumberKeys
        });
        var numberProfile = ShortcutProfiles.GetBuiltInProfile(ToolShortcutPreset.NumberKeys);
        var numberCustomProfile = ShortcutProfiles.CreateCustomProfile("Number Editing", numberProfile);
        var nestedNumberCustomProfile = ShortcutProfiles.CreateCustomProfile(
            "Number Editing Copy",
            numberCustomProfile,
            [numberCustomProfile]);
        var customProfile = ShortcutProfiles.CreateCustomProfile(
            "Animation Tools",
            ShortcutProfiles.GetBuiltInProfile(ToolShortcutPreset.TraditionalFlash));
        var customGestureCreated = ShortcutProfiles.TryCreateGesture(
            Keys.Control | Keys.Alt | Keys.D3,
            out var customGesture);
        var customBindingSet = ShortcutProfiles.TrySetBinding(
            customProfile,
            ShortcutCommandIds.ToolTriangle,
            [customGesture],
            replaceConflicts: false,
            out var triangleProfile,
            out _,
            out _);
        var conflictRejected = !ShortcutProfiles.TrySetBinding(
            triangleProfile,
            ShortcutCommandIds.ToolPolygon,
            [customGesture],
            replaceConflicts: false,
            out _,
            out var conflictCommandId,
            out _);
        var conflictReplaced = ShortcutProfiles.TrySetBinding(
            triangleProfile,
            ShortcutCommandIds.ToolPolygon,
            [customGesture],
            replaceConflicts: true,
            out var polygonProfile,
            out _,
            out _);
        ShortcutProfiles.TryCreateGesture(Keys.Control | Keys.S, out var reservedGesture);
        var reservedRejected = !ShortcutProfiles.TrySetBinding(
            triangleProfile,
            ShortcutCommandIds.ToolStar,
            [reservedGesture],
            replaceConflicts: false,
            out _,
            out _,
            out _);
        var builtInRejected = !ShortcutProfiles.TrySetBinding(
            numberProfile,
            ShortcutCommandIds.ToolStar,
            [customGesture],
            replaceConflicts: false,
            out _,
            out _,
            out _);
        var customSettings = new ApplicationSettings
        {
            ActiveShortcutProfileId = polygonProfile.Id,
            CustomShortcutProfiles = [polygonProfile]
        };
        var roundTripShortcutSettings = ApplicationSettingsStore.Normalize(
            System.Text.Json.JsonSerializer.Deserialize<ApplicationSettings>(
                System.Text.Json.JsonSerializer.Serialize(customSettings))!);
        if (migratedNumberSettings.ActiveShortcutProfileId != ShortcutProfiles.NumberKeysProfileId
            || ApplicationSettingsStore.Normalize(new ApplicationSettings
            {
                ActiveShortcutProfileId = ShortcutProfiles.NumberKeysProfileId.ToUpperInvariant()
            }).ActiveShortcutProfileId != ShortcutProfiles.NumberKeysProfileId
            || ShortcutProfiles.LegacyPresetForProfile(
                nestedNumberCustomProfile.Id,
                [numberCustomProfile, nestedNumberCustomProfile]) != ToolShortcutPreset.NumberKeys
            || !customGestureCreated
            || !customBindingSet
            || !conflictRejected
            || conflictCommandId != ShortcutCommandIds.ToolTriangle
            || !conflictReplaced
            || !reservedRejected
            || !ShortcutProfiles.IsReservedGesture(Keys.Alt | Keys.F4)
            || !builtInRejected
            || ToolShortcutMap.ResolveTool(
                triangleProfile,
                Keys.Control | Keys.Alt | Keys.D3,
                ToolMode.Select,
                ToolMode.Rectangle,
                ToolMode.Line,
                ToolMode.Brush,
                ToolMode.Fill) != ToolMode.Triangle
            || ToolShortcutMap.ResolveTool(
                polygonProfile,
                Keys.Control | Keys.Alt | Keys.D3,
                ToolMode.Select,
                ToolMode.Rectangle,
                ToolMode.Line,
                ToolMode.Brush,
                ToolMode.Fill) != ToolMode.Polygon
            || ToolShortcutMap.ResolveTool(
                numberProfile,
                Keys.D1,
                ToolMode.Transform,
                ToolMode.Rectangle,
                ToolMode.Line,
                ToolMode.Brush,
                ToolMode.Fill) != ToolMode.Transform
            || ToolShortcutMap.ResolveTool(
                numberProfile,
                Keys.NumPad1,
                ToolMode.Transform,
                ToolMode.Rectangle,
                ToolMode.Line,
                ToolMode.Brush,
                ToolMode.Fill) != ToolMode.Transform
            || roundTripShortcutSettings.ActiveShortcutProfileId != polygonProfile.Id
            || roundTripShortcutSettings.CustomShortcutProfiles.Length != 1
            || ToolShortcutMap.ResolveCommandId(
                roundTripShortcutSettings.CustomShortcutProfiles[0],
                Keys.Control | Keys.Alt | Keys.D3) != ShortcutCommandIds.ToolPolygon)
        {
            throw new InvalidOperationException("Custom shortcut profiles did not migrate, clone, resolve, reject conflicts, or round-trip correctly.");
        }

        var toolCycleForward = MainForm.CycleToolGroupMember([ToolMode.Select, ToolMode.Transform], ToolMode.Select, reverse: false);
        var toolCycleBackward = MainForm.CycleToolGroupMember([ToolMode.Fill, ToolMode.InkBottle], ToolMode.Fill, reverse: true);
        var lineToolCycle = MainForm.CycleToolGroupMember(
            [ToolMode.Line, ToolMode.Pen, ToolMode.SimplePen, ToolMode.Pencil],
            ToolMode.Pen,
            reverse: false);
        if (selectionShortcut != ToolMode.Transform
            || fillShortcut != ToolMode.InkBottle
            || ToolShortcutMap.ResolveNumberKeyTool(Keys.D8, ToolMode.Select, ToolMode.Rectangle, ToolMode.Line, ToolMode.Brush, ToolMode.Fill) != ToolMode.Eyedropper
            || traditionalSelection != ToolMode.Select
            || traditionalInkBottle != ToolMode.InkBottle
            || traditionalPen != ToolMode.Pen
            || traditionalText != ToolMode.Text
            || defaultSettings.ToolShortcutPreset != ToolShortcutPreset.TraditionalFlash
            || defaultSettings.Language != UiLanguage.English
            || defaultSettings.ColorTheme != ApplicationColorTheme.Dark
            || defaultSettings.ThemeHueDegrees != ApplicationSettings.DefaultThemeHueDegrees
            || defaultSettings.ThemeSaturationPercent != ApplicationSettings.DefaultThemeSaturationPercent
            || defaultSettings.ThemeBrightnessPercent != ApplicationSettings.DefaultThemeBrightnessPercent
            || defaultSettings.AccentHueDegrees != ApplicationSettings.DefaultAccentHueDegrees
            || defaultSettings.AccentSaturationPercent != ApplicationSettings.DefaultAccentSaturationPercent
            || defaultSettings.AccentBrightnessPercent != ApplicationSettings.DefaultAccentBrightnessPercent
            || defaultSettings.TimelineFrameWidth != 14
            || defaultSettings.TimelineFrameHeight != TimelineFrameHeightPreset.Medium
            || defaultSettings.WorkspaceColorArgb != ApplicationSettings.DefaultWorkspaceColorArgb
            || !chineseLocalizationApplied
            || !englishLocalizationRestored
            || !ToolShortcutMap.IsVaultShortcut(ToolShortcutPreset.NumberKeys, Keys.D9)
            || ToolShortcutMap.IsVaultShortcut(ToolShortcutPreset.TraditionalFlash, Keys.D9)
            || toolCycleForward != ToolMode.Transform
            || toolCycleBackward != ToolMode.InkBottle
            || lineToolCycle != ToolMode.SimplePen)
        {
            throw new InvalidOperationException("Tool shortcut presets, localization, or grouped Tab-cycle routing did not resolve correctly.");
        }

        using var form = new Form
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-30_000, -30_000),
            ClientSize = new Size(320, 220)
        };
        using var stage = new StageControl(scene) { Dock = DockStyle.Fill };
        form.Controls.Add(stage);
        form.Show();
        Application.DoEvents();
        stage.SetGradientOverlay(
            new PointF(0, 0),
            new PointF(1_000, 0),
            scene.GetGradientKind(rectangle),
            scene.GetGradientStops(rectangle));
        var overlayStop = stage.WorldToScreen(720, 0);
        var overlayHit = stage.HitTestGradientOverlay(Point.Round(overlayStop));
        stage.Invalidate();
        stage.Update();
        Application.DoEvents();
        if (!stage.LastFrameUsedDirect2D || stage.LastStats.DrawnObjects != 4 || overlayHit.Kind != GradientHandleKind.Stop || overlayHit.StopIndex != 2)
        {
            throw new InvalidOperationException($"The Direct2D radial multi-stop gradient annotator did not produce an interactive stage frame: direct2d={stage.LastFrameUsedDirect2D}, drawn={stage.LastStats.DrawnObjects}, hit={overlayHit.Kind}/{overlayHit.StopIndex}.");
        }

        stage.Invalidate();
        stage.Update();
        var stableGradientCache = stage.LastDirect2DGradientBrushCacheBuilds == 0
            && stage.LastDirect2DGradientBrushCacheReuses == 4;
        var updatedRectangleStops = scene.GetGradientStops(rectangle);
        updatedRectangleStops[1] = new GradientStop(updatedRectangleStops[1].Position, Color.LimeGreen);
        scene.SetGradientStops(rectangle, updatedRectangleStops);
        stage.Invalidate();
        stage.Update();
        var editedGradientCache = stage.LastDirect2DGradientBrushCacheBuilds == 1
            && stage.LastDirect2DGradientBrushCacheReuses == 3;
        if (!stableGradientCache || !editedGradientCache)
        {
            throw new InvalidOperationException(
                $"Direct2D fill/line gradient brushes were not reused or selectively refreshed: " +
                $"stable={stableGradientCache}, edited={editedGradientCache}, " +
                $"builds={stage.LastDirect2DGradientBrushCacheBuilds}, reuses={stage.LastDirect2DGradientBrushCacheReuses}.");
        }

        var lineCacheScene = new VectorScene();
        lineCacheScene.CreateEmpty();
        var cachedLineA = lineCacheScene.AddCubicCurveSegment(
            0,
            new PointF(-400, -100),
            new PointF(-280, -260),
            new PointF(-120, 160),
            PointF.Empty,
            16,
            Color.Transparent,
            Color.White,
            6);
        lineCacheScene.AddCubicCurveSegment(
            0,
            PointF.Empty,
            new PointF(120, 160),
            new PointF(280, -260),
            new PointF(400, -100),
            16,
            Color.Transparent,
            Color.White,
            6);
        lineCacheScene.AddCubicCurveSegment(
            0,
            PointF.Empty,
            new PointF(-120, 160),
            new PointF(120, 260),
            new PointF(0, 400),
            16,
            Color.Transparent,
            Color.White,
            6);
        stage.BindScene(lineCacheScene);
        stage.SetVisibleWorldWidth(1_200);
        stage.ClearGradientOverlay();
        stage.Invalidate();
        stage.Update();
        var lineCacheFirstFrame = stage.LastFrameUsedDirect2D
            && stage.LastDirect2DLineGeometryCacheBuilds == 3;
        stage.Invalidate();
        stage.Update();
        var lineCacheStableFrame = stage.LastDirect2DLineGeometryCacheBuilds == 0
            && stage.LastDirect2DLineGeometryCacheReuses >= 3;
        lineCacheScene.CurveControlY[cachedLineA] += 40;
        stage.Invalidate();
        stage.Update();
        var lineCacheEditedFrame = stage.LastDirect2DLineGeometryCacheBuilds == 1
            && stage.LastDirect2DLineGeometryCacheReuses >= 2;
        if (!lineCacheFirstFrame || !lineCacheStableFrame || !lineCacheEditedFrame)
        {
            throw new InvalidOperationException(
                $"Direct2D line geometry caching did not reuse stable paths or selectively rebuild an edited line: "
                + $"first={lineCacheFirstFrame}, stable={lineCacheStableFrame}, edited={lineCacheEditedFrame}, "
                + $"builds={stage.LastDirect2DLineGeometryCacheBuilds}, reuses={stage.LastDirect2DLineGeometryCacheReuses}.");
        }


        stage.BindScene(shapeRadialScene);
        stage.SetVisibleWorldWidth(500);
        stage.ClearGradientOverlay();
        stage.Invalidate();
        stage.Update();
        Application.DoEvents();
        var shapeGradientFirstFrame = stage.LastFrameUsedDirect2D
            && stage.LastStats.DrawnObjects == 1
            && stage.LastDirect2DShapeGradientBitmapCacheBuilds == 1;
        stage.Invalidate();
        stage.Update();
        var shapeGradientStableFrame = stage.LastDirect2DShapeGradientBitmapCacheBuilds == 0
            && stage.LastDirect2DShapeGradientBitmapCacheReuses == 1;
        var recoloredShapeStops = shapeRadialScene.GetGradientStops(shapeRadialObject);
        recoloredShapeStops[1] = new GradientStop(recoloredShapeStops[1].Position, Color.LimeGreen);
        shapeRadialScene.SetGradientStops(shapeRadialObject, recoloredShapeStops);
        stage.Invalidate();
        stage.Update();
        var shapeGradientRecoloredFrame = stage.LastDirect2DShapeGradientBitmapCacheBuilds == 1;
        if (!shapeGradientFirstFrame || !shapeGradientStableFrame || !shapeGradientRecoloredFrame)
        {
            throw new InvalidOperationException(
                $"Shape radial Direct2D rendering or caching failed: first={shapeGradientFirstFrame}, stable={shapeGradientStableFrame}, recolored={shapeGradientRecoloredFrame}, builds={stage.LastDirect2DShapeGradientBitmapCacheBuilds}, reuses={stage.LastDirect2DShapeGradientBitmapCacheReuses}.");
        }

        const int denseShapeGradientCount = 48;
        var denseShapeGradientScene = new VectorScene();
        denseShapeGradientScene.CreateEmpty();
        for (var index = 0; index < denseShapeGradientCount; index++)
        {
            var center = new PointF((index % 8 - 3.5f) * 48, (index / 8 - 2.5f) * 48);
            var contour = irregularContour
                .Select(point => new PointF(center.X + point.X * 0.12f, center.Y + point.Y * 0.12f))
                .ToArray();
            var gradientObject = denseShapeGradientScene.AddPathObject(
                0,
                contour,
                0,
                Color.Coral,
                Color.Transparent,
                24);
            denseShapeGradientScene.SetShapeRadialGradient(gradientObject, Color.Gold, Color.RoyalBlue, center);
            denseShapeGradientScene.SetGradientStops(gradientObject, shapeRadialStops);
            denseShapeGradientScene.SetShapeGradientMapping(
                gradientObject,
                denseShapeGradientScene.GetObjectBoundaryContours(gradientObject));
        }

        stage.BindScene(denseShapeGradientScene);
        stage.SetVisibleWorldWidth(500);
        stage.Invalidate();
        stage.Update();
        Application.DoEvents();
        var denseShapeGradientFirstFrame = stage.LastFrameUsedDirect2D
            && stage.LastStats.DrawnObjects == denseShapeGradientCount
            && stage.LastDirect2DShapeGradientBitmapCacheBuilds == denseShapeGradientCount
            && stage.LastDirect2DShapeGradientMaskGeometryCacheBuilds == denseShapeGradientCount;
        stage.Invalidate();
        stage.Update();
        var denseShapeGradientStableFrame = stage.LastDirect2DShapeGradientBitmapCacheBuilds == 0
            && stage.LastDirect2DShapeGradientBitmapCacheReuses == denseShapeGradientCount
            && stage.LastDirect2DShapeGradientMaskGeometryCacheBuilds == 0
            && stage.LastDirect2DShapeGradientMaskGeometryCacheReuses == denseShapeGradientCount;
        if (!denseShapeGradientFirstFrame || !denseShapeGradientStableFrame)
        {
            throw new InvalidOperationException(
                $"Dense shape radial cache exceeded its stable-frame capacity: first={denseShapeGradientFirstFrame}, stable={denseShapeGradientStableFrame}, bitmapBuilds={stage.LastDirect2DShapeGradientBitmapCacheBuilds}, bitmapReuses={stage.LastDirect2DShapeGradientBitmapCacheReuses}, maskBuilds={stage.LastDirect2DShapeGradientMaskGeometryCacheBuilds}, maskReuses={stage.LastDirect2DShapeGradientMaskGeometryCacheReuses}.");
        }

        var curvedBrushScene = new VectorScene();
        curvedBrushScene.CreateEmpty();
        var brushPath = new[]
        {
            new PointF(-1_200, 640),
            new PointF(-820, -720),
            new PointF(260, -1_000),
            new PointF(1_160, -260),
            new PointF(860, 860),
            new PointF(-180, 1_120),
            new PointF(-900, 520)
        };
        var curvedBrushObjects = curvedBrushScene.AddSoftBrushStroke(
            0,
            brushPath,
            VectorUnits.StrokePointsToUnits(28),
            Color.MediumPurple,
            BrushShape.CreateTraditionalBrush(),
            64);
        var curvedStops = new[]
        {
            new GradientStop(0, Color.SpringGreen),
            new GradientStop(0.44f, Color.DeepSkyBlue),
            new GradientStop(1, Color.MediumPurple)
        };
        foreach (var objectIndex in curvedBrushObjects)
        {
            curvedBrushScene.SetGradientPaint(objectIndex, GradientKind.Linear, curvedStops, brushPath[0], brushPath[^1]);
            curvedBrushScene.SetGradientPath(objectIndex, brushPath);
        }

        stage.BindScene(curvedBrushScene);
        stage.SetVisibleWorldWidth(5_000);
        stage.Invalidate();
        stage.Update();
        Application.DoEvents();
        if (!stage.LastFrameUsedDirect2D || stage.LastStats.DrawnObjects != curvedBrushObjects.Length)
        {
            throw new InvalidOperationException("The Direct2D renderer did not draw the trajectory-gradient brush path.");
        }
        const int trajectoryGradientSamples = 16;
        for (var warmup = 0; warmup < 3; warmup++)
        {
            stage.Invalidate();
            stage.Update();
        }
        var trajectoryGradientCommandMilliseconds = 0d;
        var trajectoryGradientCacheBuilds = 0;
        var trajectoryGradientCacheReuses = 0;
        for (var sample = 0; sample < trajectoryGradientSamples; sample++)
        {
            stage.Invalidate();
            stage.Update();
            trajectoryGradientCommandMilliseconds += stage.LastDirect2DCommandMilliseconds;
            trajectoryGradientCacheBuilds += stage.LastDirect2DPathGradientBrushCacheBuilds;
            trajectoryGradientCacheReuses += stage.LastDirect2DPathGradientBrushCacheReuses;
        }
        var trajectoryGradientAverageCommandMilliseconds = trajectoryGradientCommandMilliseconds / trajectoryGradientSamples;
        if (!stage.LastFrameUsedDirect2D
            || trajectoryGradientAverageCommandMilliseconds > RenderCollectBudgetMilliseconds
            || trajectoryGradientCacheBuilds != 0
            || trajectoryGradientCacheReuses != trajectoryGradientSamples * curvedBrushObjects.Length)
        {
            throw new InvalidOperationException(
                $"Trajectory-gradient brush rendering exceeded its command budget: direct2d={stage.LastFrameUsedDirect2D}, " +
                $"averageMs={trajectoryGradientAverageCommandMilliseconds:0.000}, budgetMs={RenderCollectBudgetMilliseconds:0.000}, " +
                $"cacheBuilds={trajectoryGradientCacheBuilds}, cacheReuses={trajectoryGradientCacheReuses}.");
        }
        form.Close();

        Console.WriteLine($"trajectory_gradient_command_avg_ms={trajectoryGradientAverageCommandMilliseconds:0.000}");
        Console.WriteLine($"trajectory_gradient_cache_reuses={trajectoryGradientCacheReuses}");
        Console.WriteLine("gradient_paint_regression=ok");
    }

    private static void RunFillEdgeAntialiasingRegression()
    {
        using var antialiasedBitmap = new Bitmap(40, 40);
        using var hardenedBitmap = new Bitmap(40, 40);
        using var singlePassBitmap = new Bitmap(40, 40);
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddPolygon(
        [
            new PointF(4.25f, 4.5f),
            new PointF(35.25f, 11.75f),
            new PointF(9.5f, 35.25f)
        ]);
        using var fill = new SolidBrush(Color.White);

        using (var graphics = Graphics.FromImage(antialiasedBitmap))
        {
            graphics.Clear(Color.Black);
            StageControl.FillPathAntialiased(graphics, path, fill);
        }

        using (var graphics = Graphics.FromImage(hardenedBitmap))
        {
            graphics.Clear(Color.Black);
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
            graphics.FillPath(fill, path);
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            graphics.FillPath(fill, path);
        }

        using (var graphics = Graphics.FromImage(singlePassBitmap))
        {
            graphics.Clear(Color.Black);
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            graphics.FillPath(fill, path);
        }

        var softenedInnerEdgePixel = false;
        var reinforcedEdgePixels = 0;
        var transitionalPixels = 0;
        for (var y = 0; y < antialiasedBitmap.Height; y++)
        {
            for (var x = 0; x < antialiasedBitmap.Width; x++)
            {
                var smooth = antialiasedBitmap.GetPixel(x, y).R;
                var hardened = hardenedBitmap.GetPixel(x, y).R;
                var singlePass = singlePassBitmap.GetPixel(x, y).R;
                if (smooth is > 0 and < 255) transitionalPixels++;
                if (smooth is > 0 and < 255 && hardened == 255) softenedInnerEdgePixel = true;
                if (singlePass is > 0 and < 255 && smooth > singlePass) reinforcedEdgePixels++;
            }
        }

        if (!softenedInnerEdgePixel || transitionalPixels < 20 || reinforcedEdgePixels < 20)
        {
            throw new InvalidOperationException(
                $"Fill-edge antialiasing did not preserve fractional geometry coverage: " +
                $"softenedInnerEdge={softenedInnerEdgePixel}, transitionalPixels={transitionalPixels}, " +
                $"reinforcedEdgePixels={reinforcedEdgePixels}.");
        }

        Console.WriteLine($"fill_edge_antialias_pixels={transitionalPixels}");
        Console.WriteLine($"fill_edge_reinforced_pixels={reinforcedEdgePixels}");
    }

    private static void RunReleaseNotesRegression()
    {
        var entries = ReleaseNotesCatalog.Entries;
        if (entries.Count == 0
            || entries is not IList<ReleaseNoteEntry> readOnlyEntries
            || !readOnlyEntries.IsReadOnly
            || entries.Select(entry => entry.Version).Distinct().Count() != entries.Count
            || !entries.Select(entry => entry.Version).SequenceEqual(
                entries.Select(entry => entry.Version).OrderByDescending(version => version)))
        {
            throw new InvalidOperationException("The release-notes catalog was empty, mutable, duplicated, or not newest-first.");
        }

        var informationalVersion = typeof(Benchmark).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), inherit: false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>()
            .SingleOrDefault()
            ?.InformationalVersion
            ?.Split('+', 2)[0];
        if (!Version.TryParse(informationalVersion, out var assemblyVersion)
            || ReleaseNotesCatalog.LatestVersion != assemblyVersion)
        {
            throw new InvalidOperationException(
                $"The latest release note version {ReleaseNotesCatalog.LatestVersion} did not match assembly version {informationalVersion ?? "missing"}.");
        }

        foreach (var entry in entries)
        {
            if (entry.English.Sections.Count != entry.SimplifiedChinese.Sections.Count
                || entry.English.Sections is not IList<ReleaseNoteSection> englishSections
                || entry.SimplifiedChinese.Sections is not IList<ReleaseNoteSection> chineseSections
                || !englishSections.IsReadOnly
                || !chineseSections.IsReadOnly
                || entry.English.Sections
                    .Zip(entry.SimplifiedChinese.Sections)
                    .Any(pair => pair.First.Items.Count != pair.Second.Items.Count
                        || pair.First.Items.Count == 0
                        || pair.First.Items is not IList<string> englishItems
                        || pair.Second.Items is not IList<string> chineseItems
                        || !englishItems.IsReadOnly
                        || !chineseItems.IsReadOnly))
            {
                throw new InvalidOperationException(
                    $"Release note {entry.Version} did not preserve immutable, structurally aligned English and Chinese content.");
            }
        }

        static IEnumerable<Control> Descendants(Control root)
        {
            foreach (Control child in root.Controls)
            {
                yield return child;
                foreach (var descendant in Descendants(child)) yield return descendant;
            }
        }

        var originalLanguage = UiLocalization.CurrentLanguage;
        try
        {
            foreach (var language in new[] { UiLanguage.English, UiLanguage.SimplifiedChinese })
            {
                UiLocalization.SetLanguage(language);
                using var panel = new ReleaseNotesPanel();
                panel.CreateControl();
                panel.PerformLayout();

                var controls = Descendants(panel).ToArray();
                var expectedVersion = string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    UiLocalization.T("Version {0}", language),
                    ReleaseNotesCatalog.LatestVersion);
                if (panel.AccessibleRole != AccessibleRole.Document
                    || panel.AccessibleName != UiLocalization.T("Release Notes", language)
                    || !controls.OfType<ThemedScrollPanel>().Any(scroll =>
                        scroll.TabStop && scroll.AccessibleRole == AccessibleRole.Document)
                    || !controls.OfType<Label>().Any(label => label.Text == expectedVersion)
                    || !controls.OfType<Label>().Any(label => !string.IsNullOrWhiteSpace(label.Text)))
                {
                    throw new InvalidOperationException(
                        $"The {language} release-notes panel did not expose its current version, content, keyboard scrolling, and document accessibility.");
                }
            }
        }
        finally
        {
            UiLocalization.SetLanguage(originalLanguage);
        }

        Console.WriteLine("release_notes_regression=ok");
    }

    private static void RunHotReloadModuleRoutingRegression()
    {
        var rendering = HotReloadModuleResolver.Resolve([typeof(Direct2DStageRenderer)]);
        var worldGridRendering = HotReloadModuleResolver.Resolve([typeof(WorldGridLayout)]);
        var polarGridRendering = HotReloadModuleResolver.Resolve([typeof(PolarGridLayout)]);
        var timeline = HotReloadModuleResolver.Resolve([typeof(TimelineStrip)]);
        var inspector = HotReloadModuleResolver.Resolve([typeof(MaterialEditorPanel)]);
        var instanceInspector = HotReloadModuleResolver.Resolve([typeof(DrawingObjectInstancePanel)]);
        var themedScroll = HotReloadModuleResolver.Resolve([typeof(ThemedScrollPanel)]);
        var harmonyWheel = HotReloadModuleResolver.Resolve([typeof(HarmonyColorWheel)]);
        var gradientPreset = HotReloadModuleResolver.Resolve([typeof(GradientPresetGrid)]);
        var paletteIcon = HotReloadModuleResolver.Resolve([typeof(SvgIconButton), typeof(SvgIcons)]);
        var paletteStore = HotReloadModuleResolver.Resolve([typeof(MaterialPaletteStore)]);
        var settingsDialog = HotReloadModuleResolver.Resolve([
            typeof(SettingsDialog),
            typeof(ShortcutProfileEditorPanel),
            typeof(ModernDialogForm),
            typeof(ModernMessageDialog),
            typeof(Theme),
            typeof(ApplicationColorTheme),
            typeof(ShortcutProfiles),
            typeof(ShortcutProfileRecord),
            typeof(ToolShortcutMap),
            typeof(UiLocalization)]);
        var releaseNotes = HotReloadModuleResolver.Resolve([
            typeof(ReleaseNotesCatalog),
            typeof(ReleaseNotesDialog),
            typeof(ReleaseNotesPanel)]);
        var engine = HotReloadModuleResolver.Resolve([typeof(VectorScene), typeof(DrawingObjectPlaybackMode)]);
        var projectAssetFolder = HotReloadModuleResolver.Resolve([typeof(ProjectAssetFolder)]);
        var unknown = HotReloadModuleResolver.Resolve([typeof(Benchmark)]);
        var merged = rendering.Merge(engine).Merge(rendering);
        HotReloadBatch? dispatchedBatch = null;
        var unavailableTargetChecks = 0;
        using var dispatchForm = new Form
        {
            ShowInTaskbar = false,
            Location = new Point(-30_000, -30_000),
            ClientSize = new Size(80, 60)
        };
        dispatchForm.Show();
        Application.DoEvents();
        using (var coordinator = new HotReloadCoordinator(
                   () => dispatchForm,
                   batch => dispatchedBatch = batch,
                   coalesceMilliseconds: 10))
        {
            coordinator.Enqueue(rendering);
            coordinator.Enqueue(engine);
            var timeout = Stopwatch.StartNew();
            while (dispatchedBatch is null && timeout.Elapsed < TimeSpan.FromSeconds(2))
            {
                Application.DoEvents();
                Thread.Sleep(1);
            }
        }
        dispatchForm.Close();
        using (var unavailableCoordinator = new HotReloadCoordinator(
                   () =>
                   {
                       Interlocked.Increment(ref unavailableTargetChecks);
                       return null;
                   },
                   _ => throw new InvalidOperationException("An unavailable hot-reload target unexpectedly dispatched."),
                   coalesceMilliseconds: 2))
        {
            unavailableCoordinator.Enqueue(rendering);
            var unavailableTimeout = Stopwatch.StartNew();
            while (Volatile.Read(ref unavailableTargetChecks) < 16
                && unavailableTimeout.Elapsed < TimeSpan.FromSeconds(2))
            {
                Thread.Sleep(10);
            }
            var settledChecks = Volatile.Read(ref unavailableTargetChecks);
            Thread.Sleep(80);
            if (settledChecks != 16
                || Volatile.Read(ref unavailableTargetChecks) != settledChecks)
            {
                throw new InvalidOperationException(
                    $"An unavailable hot-reload target did not stop its bounded retry loop: checks={unavailableTargetChecks}.");
            }
        }
        var coordinatorMergedBatch = dispatchedBatch is { Generation: 2 } batch
            && batch.Plan.Modules == (HotReloadModule.Rendering | HotReloadModule.Engine);
        if (rendering.Modules != HotReloadModule.Rendering
            || worldGridRendering.Modules != HotReloadModule.Rendering
            || polarGridRendering.Modules != HotReloadModule.Rendering
            || timeline.Modules != HotReloadModule.Timeline
            || inspector.Modules != HotReloadModule.Inspector
            || instanceInspector.Modules != HotReloadModule.Inspector
            || themedScroll.Modules != HotReloadModule.Inspector
            || harmonyWheel.Modules != HotReloadModule.Inspector
            || gradientPreset.Modules != HotReloadModule.Inspector
            || paletteIcon.Modules != HotReloadModule.Inspector
            || paletteStore.Modules != HotReloadModule.Inspector
            || settingsDialog.Modules != HotReloadModule.Shell
            || releaseNotes.Modules != HotReloadModule.Shell
            || engine.Modules != HotReloadModule.Engine
            || projectAssetFolder.Modules != HotReloadModule.Engine
            || unknown.Modules != HotReloadModule.All
            || merged.Modules != (HotReloadModule.Rendering | HotReloadModule.Engine)
            || merged.UpdatedTypes.Split(',', StringSplitOptions.RemoveEmptyEntries).Length != 3
            || !coordinatorMergedBatch
            || !rendering.RequiresProcessRestart
            || !timeline.RequiresProcessRestart
            || !inspector.RequiresProcessRestart
            || !instanceInspector.RequiresProcessRestart
            || !engine.RequiresProcessRestart
            || !projectAssetFolder.RequiresProcessRestart
            || !unknown.RequiresProcessRestart
            || worldGridRendering.RequiresProcessRestart
            || polarGridRendering.RequiresProcessRestart
            || rendering.RequiresWorkbenchRebuild
            || worldGridRendering.RequiresWorkbenchRebuild
            || polarGridRendering.RequiresWorkbenchRebuild
            || timeline.RequiresWorkbenchRebuild
            || inspector.RequiresWorkbenchRebuild
            || instanceInspector.RequiresWorkbenchRebuild
            || themedScroll.RequiresWorkbenchRebuild
            || harmonyWheel.RequiresWorkbenchRebuild
            || gradientPreset.RequiresWorkbenchRebuild
            || paletteIcon.RequiresWorkbenchRebuild
            || paletteStore.RequiresWorkbenchRebuild
            || settingsDialog.RequiresWorkbenchRebuild
            || releaseNotes.RequiresWorkbenchRebuild
            || engine.RequiresWorkbenchRebuild
            || projectAssetFolder.RequiresWorkbenchRebuild)
        {
            throw new InvalidOperationException(
                    $"Module hot reload routing was not scoped: rendering={rendering.Modules}/{rendering.RequiresWorkbenchRebuild}, worldGrid={worldGridRendering.Modules}/{worldGridRendering.RequiresWorkbenchRebuild}, polarGrid={polarGridRendering.Modules}/{polarGridRendering.RequiresWorkbenchRebuild}, timeline={timeline.Modules}/{timeline.RequiresWorkbenchRebuild}, inspector={inspector.Modules}/{inspector.RequiresWorkbenchRebuild}, instanceInspector={instanceInspector.Modules}/{instanceInspector.RequiresWorkbenchRebuild}, themedScroll={themedScroll.Modules}/{themedScroll.RequiresWorkbenchRebuild}, harmonyWheel={harmonyWheel.Modules}/{harmonyWheel.RequiresWorkbenchRebuild}, gradientPreset={gradientPreset.Modules}/{gradientPreset.RequiresWorkbenchRebuild}, paletteIcon={paletteIcon.Modules}/{paletteIcon.RequiresWorkbenchRebuild}, paletteStore={paletteStore.Modules}/{paletteStore.RequiresWorkbenchRebuild}, settingsDialog={settingsDialog.Modules}/{settingsDialog.RequiresWorkbenchRebuild}, releaseNotes={releaseNotes.Modules}/{releaseNotes.RequiresWorkbenchRebuild}, engine={engine.Modules}/{engine.RequiresWorkbenchRebuild}, projectAssetFolder={projectAssetFolder.Modules}/{projectAssetFolder.RequiresWorkbenchRebuild}, unknown={unknown.Modules}, merged={merged.Modules}/{merged.UpdatedTypes}, coordinator={dispatchedBatch}.");
        }

        Console.WriteLine("module_reload_routing_regression=ok");
    }

    private static void RunColorHarmonyRegression()
    {
        var red = Color.FromArgb(255, 255, 0, 0);
        var complementary = HarmonyColorWheel.CreateHarmonyColors(red, ColorHarmonyMode.Complementary);
        var analogous = HarmonyColorWheel.CreateHarmonyColors(red, ColorHarmonyMode.Analogous);
        var triadic = HarmonyColorWheel.CreateHarmonyColors(red, ColorHarmonyMode.Triadic);
        var splitComplementary = HarmonyColorWheel.CreateHarmonyColors(red, ColorHarmonyMode.SplitComplementary);
        var tetradic = HarmonyColorWheel.CreateHarmonyColors(red, ColorHarmonyMode.Tetradic);
        if (complementary.Length != 2
            || complementary[0].ToArgb() != red.ToArgb()
            || complementary[1].R > 2 || complementary[1].G < 253 || complementary[1].B < 253
            || analogous.Length != 3
            || triadic.Length != 3 || triadic[1].G < 253 || triadic[2].B < 253
            || splitComplementary.Length != 3
            || tetradic.Length != 4)
        {
            throw new InvalidOperationException("Color harmony rules did not generate the expected complementary, analogous, triadic, split-complementary, and tetradic palettes.");
        }

        var wheelBounds = new Rectangle(4, 4, 88, 88);
        var center = new Point(48, 48);
        if (!HarmonyColorWheel.TryResolvePointerHue(new Point(48, 5), wheelBounds, requireHotZone: true, out var topHue)
            || !HarmonyColorWheel.TryResolvePointerHue(new Point(66, 48), wheelBounds, requireHotZone: true, out _)
            || !HarmonyColorWheel.TryResolvePointerHue(new Point(95, 48), wheelBounds, requireHotZone: true, out _)
            || HarmonyColorWheel.TryResolvePointerHue(center, wheelBounds, requireHotZone: true, out _)
            || HarmonyColorWheel.TryResolvePointerHue(new Point(140, 48), wheelBounds, requireHotZone: true, out _)
            || !HarmonyColorWheel.TryResolvePointerHue(new Point(140, 48), wheelBounds, requireHotZone: false, out var capturedHue)
            || Math.Abs(topHue) > 2f
            || Math.Abs(capturedHue - 90f) > 2f)
        {
            throw new InvalidOperationException("Color harmony wheel pointer hot zones or captured drag hue resolution were invalid.");
        }

        const float wheelRadius = 44f;
        var peakRadius = HarmonyColorWheel.ResolvePointerSurfaceRadius(wheelRadius, 30f, 30f);
        var shoulderRadius = HarmonyColorWheel.ResolvePointerSurfaceRadius(wheelRadius, 42f, 30f);
        var edgeRadius = HarmonyColorWheel.ResolvePointerSurfaceRadius(wheelRadius, 48f, 30f);
        var wrappedRadius = HarmonyColorWheel.ResolvePointerSurfaceRadius(wheelRadius, 359f, 1f);
        if (HarmonyColorWheel.InteractionRefreshIntervalMilliseconds > 8
            || Math.Abs(peakRadius - 47f) > 0.001f
            || shoulderRadius <= wheelRadius || shoulderRadius >= peakRadius
            || Math.Abs(edgeRadius - wheelRadius) > 0.001f
            || wrappedRadius <= shoulderRadius)
        {
            throw new InvalidOperationException("Color harmony wheel pointer surface shader did not peak, decay, or wrap hue angles correctly.");
        }

        var sliderPeak = ColorComponentSlider.ResolvePointerSurfaceOffset(48f, 48f);
        var sliderShoulder = ColorComponentSlider.ResolvePointerSurfaceOffset(55f, 48f);
        var sliderEdge = ColorComponentSlider.ResolvePointerSurfaceOffset(62f, 48f);
        if (ColorComponentSlider.InteractionRefreshIntervalMilliseconds > 8
            || Math.Abs(sliderPeak - 3f) > 0.001f
            || sliderShoulder <= 0f || sliderShoulder >= sliderPeak
            || Math.Abs(sliderEdge) > 0.001f)
        {
            throw new InvalidOperationException("Color component slider pointer surface did not peak, decay, or refresh at interactive frequency.");
        }

        Console.WriteLine("color_harmony_regression=ok");
    }

    private static void RunGradientPresetRegression()
    {
        var presets = MaterialEditorPanel.BuiltInGradientPresets;
        var midpoint = GradientPreviewRenderer.Sample(
            [new GradientStop(0, Color.Black), new GradientStop(1, Color.White)],
            0.5f);
        var savedGradient = new GradientPreset(
            "Saved radial",
            GradientKind.Radial,
            [new GradientStop(0, Color.Coral), new GradientStop(0.4f, Color.Gold), new GradientStop(1, Color.RoyalBlue)],
            isUserSaved: true);
        var restoredSavedGradients = MaterialPaletteStore.Deserialize(MaterialPaletteStore.Serialize([savedGradient]));
        var persistenceRoundTrip = restoredSavedGradients.Length == 1
            && restoredSavedGradients[0].IsUserSaved
            && restoredSavedGradients[0].Name == savedGradient.Name
            && restoredSavedGradients[0].Kind == savedGradient.Kind
            && restoredSavedGradients[0].Stops.SequenceEqual(savedGradient.Stops);
        if (presets.Count != 6
            || presets.Count(preset => preset.Kind == GradientKind.Linear) != 4
            || presets.Count(preset => preset.Kind == GradientKind.Radial) != 2
            || presets.Any(preset => preset.Stops.Length < 2 || preset.Stops[0].Position != 0f || preset.Stops[^1].Position != 1f)
            || midpoint.R is < 126 or > 129 || midpoint.G != midpoint.R || midpoint.B != midpoint.R
            || !persistenceRoundTrip)
        {
            throw new InvalidOperationException("Gradient presets, persistence, or multi-stop preview sampling were invalid.");
        }

        Console.WriteLine("gradient_preset_regression=ok");
    }

    private static void RunSoftBrushRegression()
    {
        RunTraditionalBrushContourRegression();
        RunBrushGradientRegression();

        var scene = new VectorScene();
        scene.CreateEmpty();
        var centerline = new[] { new PointF(-180, 0), new PointF(0, 0), new PointF(180, 0) };
        var objects = scene.AddSoftBrushStroke(
            0,
            centerline,
            VectorUnits.StrokePointsToUnits(16),
            Color.Coral,
            BrushShape.CreateSoftRound(),
            32);
        if (objects.Length != 3
            || objects.Any(index => (uint)index >= scene.ObjectCount || scene.ShapeKind[index] != ShapeKind.Path)
            || objects.Any(index => !scene.FillContainsPoint(index, PointF.Empty)))
        {
            throw new InvalidOperationException("RGB soft brush did not create layered Path fills over its centerline.");
        }

        var mergeScene = new VectorScene();
        mergeScene.CreateEmpty();
        var traditional = BrushShape.CreateTraditionalBrush();
        var first = mergeScene.AddSoftBrushStroke(0, centerline, VectorUnits.StrokePointsToUnits(16), Color.Coral, traditional, 32);
        var second = mergeScene.AddSoftBrushStroke(
            0,
            centerline.Select(point => new PointF(point.X, point.Y + 8)).ToArray(),
            VectorUnits.StrokePointsToUnits(16),
            Color.Coral,
            traditional,
            32);
        var merged = mergeScene.MergeSameColorFillsAroundNewObjects(second, connectNearby: true);
        if (first.Length != 1
            || second.Length != 1
            || mergeScene.ObjectCount != 1
            || merged.Length != 1
            || !mergeScene.FillContainsPoint(merged[0], PointF.Empty))
        {
            throw new InvalidOperationException("A newly committed brush fill did not immediately merge with the matching prior brush fill.");
        }

        Console.WriteLine("soft_brush_regression=ok");
    }

    private static void RunBrushGradientRegression()
    {
        var color = Color.FromArgb(200, Color.Coral);
        var stops = new[]
        {
            new GradientStop(0, Color.FromArgb(240, Color.Coral)),
            new GradientStop(0.45f, Color.FromArgb(160, Color.Gold)),
            new GradientStop(1, Color.FromArgb(80, Color.RoyalBlue))
        };
        if (!GradientPaintUtilities.AreStopsOpaque([new GradientStop(0, Color.Coral), new GradientStop(1, Color.RoyalBlue)])
            || GradientPaintUtilities.AreStopsOpaque(stops))
        {
            throw new InvalidOperationException("Gradient opacity detection did not distinguish seam-safe opaque fills from translucent brush layers.");
        }
        var irregularPath = new[]
        {
            new PointF(-180, 10),
            new PointF(-120, -150),
            new PointF(-10, -20),
            new PointF(80, 145),
            new PointF(180, -50)
        };
        var gradientSegments = GradientPaintUtilities.CreatePathGradientSegments(irregularPath, stops, 24);
        if (gradientSegments.Length != 24
            || gradientSegments[0].StartArgb != stops[0].Argb
            || gradientSegments[^1].EndArgb != stops[^1].Argb
            || !gradientSegments.Any(segment => segment.StartArgb != segment.EndArgb)
            || gradientSegments.Zip(gradientSegments.Skip(1)).Any(pair => pair.First.EndArgb != pair.Second.StartArgb)
            || GradientPaintUtilities.TryCreateSelfIntersectionFallbackAxis(irregularPath, out _))
        {
            throw new InvalidOperationException("Curved brush gradient segments did not retain continuous endpoint colors along the accumulated path length.");
        }

        var selfCrossingPath = new[]
        {
            new PointF(-180, -120),
            new PointF(180, 120),
            new PointF(-180, 120),
            new PointF(180, -120)
        };
        if (!GradientPaintUtilities.TryCreateSelfIntersectionFallbackAxis(selfCrossingPath, out var fallbackAxis)
            || fallbackAxis.Start.X >= fallbackAxis.End.X
            || Math.Abs(fallbackAxis.End.X - fallbackAxis.Start.X) < 300
            || Math.Abs(fallbackAxis.End.Y - fallbackAxis.Start.Y) > 1)
        {
            throw new InvalidOperationException("A self-crossing brush path did not resolve to a stable, start-oriented spatial gradient axis.");
        }

        var selfCrossingScene = new VectorScene();
        selfCrossingScene.CreateEmpty();
        var selfCrossingObjects = selfCrossingScene.AddSoftBrushStroke(
            0,
            selfCrossingPath,
            VectorUnits.StrokePointsToUnits(16),
            color,
            BrushShape.CreateTraditionalBrush(),
            32);
        ApplyBrushGradient(
            selfCrossingScene,
            selfCrossingObjects,
            color,
            GradientKind.Linear,
            stops,
            selfCrossingPath[0],
            selfCrossingPath[^1]);
        foreach (var objectIndex in selfCrossingObjects) selfCrossingScene.SetGradientPath(objectIndex, selfCrossingPath);
        if (selfCrossingObjects.Length != 1
            || selfCrossingScene.HasGradientPath(selfCrossingObjects[0])
            || !PointsNear(selfCrossingScene.GetGradientStart(selfCrossingObjects[0]), fallbackAxis.Start)
            || !PointsNear(selfCrossingScene.GetGradientEnd(selfCrossingObjects[0]), fallbackAxis.End))
        {
            throw new InvalidOperationException("A self-crossing gradient brush retained conflicting trajectory colors at its intersection.");
        }

        var centerline = new[] { new PointF(-180, 0), PointF.Empty, new PointF(180, 0) };
        var softScene = new VectorScene();
        softScene.CreateEmpty();
        var softObjects = softScene.AddSoftBrushStroke(
            0,
            centerline,
            VectorUnits.StrokePointsToUnits(16),
            color,
            BrushShape.CreateSoftRound(),
            32);
        ApplyBrushGradient(softScene, softObjects, color, GradientKind.Linear, stops, centerline[0], centerline[^1]);
        foreach (var objectIndex in softObjects) softScene.SetGradientPath(objectIndex, centerline);
        if (softObjects.Length < 2
            || !softObjects.All(index => softScene.HasGradient(index)
                && softScene.GetGradientKind(index) == GradientKind.Linear
                && softScene.GetGradientStops(index).Length == stops.Length
                && softScene.GetGradientStart(index) == centerline[0]
                && softScene.GetGradientEnd(index) == centerline[^1]
                && softScene.TryGetGradientPathWorldPoints(index, out var gradientPath)
                && gradientPath.SequenceEqual(centerline))
            || Color.FromArgb(softScene.GetGradientStops(softObjects[0])[0].Argb).A
                >= Color.FromArgb(softScene.GetGradientStops(softObjects[^1])[0].Argb).A)
        {
            throw new InvalidOperationException("Soft brush gradient material did not preserve its layered alpha falloff.");
        }

        var gradientPathSnapshot = softScene.CreateSnapshot();
        softScene.TransformObjects([softObjects[0]], point => new PointF(point.X + 37, point.Y - 19));
        if (!softScene.TryGetGradientPathWorldPoints(softObjects[0], out var transformedGradientPath)
            || !transformedGradientPath.SequenceEqual(centerline.Select(point => new PointF(point.X + 37, point.Y - 19))))
        {
            throw new InvalidOperationException("Transforming a brush fill did not keep its path-gradient trajectory aligned.");
        }
        softScene.RestoreSnapshot(gradientPathSnapshot);

        var shapeBrushScene = new VectorScene();
        shapeBrushScene.CreateEmpty();
        var loopingPath = new[]
        {
            new PointF(-190, 0),
            new PointF(-90, -120),
            new PointF(90, -120),
            new PointF(180, 0),
            new PointF(90, 110),
            new PointF(-80, 95),
            new PointF(-130, 10),
            new PointF(80, -10)
        };
        var shapeBrushObjects = shapeBrushScene.AddSoftBrushStroke(
            0,
            loopingPath,
            VectorUnits.StrokePointsToUnits(20),
            color,
            BrushShape.CreateSoftRound(),
            48);
        var sharedShapeMapping = shapeBrushObjects.Length > 0
            ? shapeBrushScene.GetObjectBoundaryContours(shapeBrushObjects[0])
            : Array.Empty<PointF[]>();
        var sharedShapePoints = sharedShapeMapping.SelectMany(contour => contour).ToArray();
        var sharedShapeCenter = sharedShapePoints.Length > 0
            ? VectorUnits.Quantize(new PointF(
                (sharedShapePoints.Min(point => point.X) + sharedShapePoints.Max(point => point.X)) * 0.5f,
                (sharedShapePoints.Min(point => point.Y) + sharedShapePoints.Max(point => point.Y)) * 0.5f))
            : PointF.Empty;
        GradientPaintUtilities.TryFindShapeBoundaryPoint(
            sharedShapeMapping,
            sharedShapeCenter,
            new PointF(1, 0),
            out var sharedShapeEnd);
        foreach (var objectIndex in shapeBrushObjects)
        {
            var layerAlpha = Color.FromArgb(shapeBrushScene.Argb[objectIndex]).A;
            shapeBrushScene.SetGradientPaint(
                objectIndex,
                GradientKind.ShapeRadial,
                GradientPaintUtilities.ScaleStopAlpha(stops, layerAlpha / (float)Math.Max(1, (int)color.A)),
                sharedShapeCenter,
                sharedShapeEnd);
            shapeBrushScene.SetShapeGradientMapping(objectIndex, sharedShapeMapping);
        }
        PointF[][] normalizedSharedShapeMapping = [];
        var hasNormalizedSharedShapeMapping = shapeBrushObjects.Length > 0
            && shapeBrushScene.TryGetShapeGradientMappingWorldContours(
                shapeBrushObjects[0],
                out normalizedSharedShapeMapping);
        var normalizedSharedShapePoints = hasNormalizedSharedShapeMapping
            ? normalizedSharedShapeMapping.SelectMany(contour => contour).ToArray()
            : Array.Empty<PointF>();
        var shapeBrushSnapshot = shapeBrushScene.CreateSnapshot();
        var shapeBrushMappingShared = shapeBrushObjects.Length == 3
            && hasNormalizedSharedShapeMapping
            && shapeBrushObjects.All(index =>
                shapeBrushScene.GetGradientKind(index) == GradientKind.ShapeRadial
                && shapeBrushScene.GetGradientStart(index) == sharedShapeCenter
                && shapeBrushScene.TryGetShapeGradientMappingWorldContours(index, out var mapping)
                && mapping.SelectMany(contour => contour).SequenceEqual(normalizedSharedShapePoints));
        shapeBrushScene.TransformObjects([shapeBrushObjects[0]], point => new PointF(point.X + 31, point.Y - 17));
        var shapeBrushMappingTransformed = shapeBrushScene.TryGetShapeGradientMappingWorldContours(
                shapeBrushObjects[0],
                out var transformedShapeMapping)
            && transformedShapeMapping.SelectMany(contour => contour).SequenceEqual(
                normalizedSharedShapePoints.Select(point => new PointF(point.X + 31, point.Y - 17)));
        shapeBrushScene.RestoreSnapshot(shapeBrushSnapshot);
        if (!shapeBrushMappingShared || !shapeBrushMappingTransformed)
        {
            throw new InvalidOperationException(
                $"Layered irregular brush shape gradients did not share or transform one outer mapping: shared={shapeBrushMappingShared}, transformed={shapeBrushMappingTransformed}.");
        }

        var matchingShapeScene = new VectorScene();
        matchingShapeScene.CreateEmpty();
        var firstShapePath = new[] { new PointF(-220, 0), new PointF(-120, 0), new PointF(-20, 0) };
        var secondShapePath = new[] { new PointF(-60, 0), new PointF(50, 0), new PointF(160, 0) };
        var firstShapeBrush = matchingShapeScene.AddSoftBrushStroke(
            0,
            firstShapePath,
            VectorUnits.StrokePointsToUnits(16),
            color,
            BrushShape.CreateTraditionalBrush(),
            24);
        ApplyShapeBrushGradient(matchingShapeScene, firstShapeBrush, color, stops);
        var firstShapeCenter = matchingShapeScene.GetGradientStart(firstShapeBrush[0]);

        var shapePreviewScene = new VectorScene();
        shapePreviewScene.CreateEmpty();
        var previewShapeBrush = shapePreviewScene.AddSoftBrushStroke(
            0,
            secondShapePath,
            VectorUnits.StrokePointsToUnits(16),
            color,
            BrushShape.CreateTraditionalBrush(),
            24);
        ApplyShapeBrushGradient(shapePreviewScene, previewShapeBrush, color, stops);
        var matchingPreviewSources = matchingShapeScene.FindIntersectingMatchingShapeGradientFills(
            shapePreviewScene,
            previewShapeBrush,
            0);

        var secondShapeBrush = matchingShapeScene.AddSoftBrushStroke(
            0,
            secondShapePath,
            VectorUnits.StrokePointsToUnits(16),
            color,
            BrushShape.CreateTraditionalBrush(),
            24);
        ApplyShapeBrushGradient(matchingShapeScene, secondShapeBrush, color, stops);
        var mergedShapeBrush = matchingShapeScene.ApplyFillOverwriteToNewObjects(secondShapeBrush, 0);
        var mergedShapeMapping = mergedShapeBrush.Length == 1
            && matchingShapeScene.TryGetShapeGradientMappingWorldContours(mergedShapeBrush[0], out var combinedMapping)
                ? combinedMapping
                : Array.Empty<PointF[]>();
        var mergedShapeCenter = mergedShapeBrush.Length == 1
            ? matchingShapeScene.GetGradientStart(mergedShapeBrush[0])
            : PointF.Empty;
        if (firstShapeBrush.Length != 1
            || previewShapeBrush.Length != 1
            || matchingPreviewSources.Length != 1
            || mergedShapeBrush.Length != 1
            || matchingShapeScene.ObjectCount != 1
            || matchingShapeScene.GetGradientKind(mergedShapeBrush[0]) != GradientKind.ShapeRadial
            || !matchingShapeScene.GetGradientStops(mergedShapeBrush[0]).SequenceEqual(stops)
            || mergedShapeCenter == firstShapeCenter
            || !matchingShapeScene.FillContainsPoint(mergedShapeBrush[0], firstShapePath[1])
            || !matchingShapeScene.FillContainsPoint(mergedShapeBrush[0], secondShapePath[1])
            || mergedShapeMapping.SelectMany(contour => contour).Count() < 3)
        {
            throw new InvalidOperationException(
                $"Matching shape-gradient brush strokes did not preview, union, and recalculate one shared gradient: " +
                $"previewMatches={matchingPreviewSources.Length}, merged={mergedShapeBrush.Length}, " +
                $"objects={matchingShapeScene.ObjectCount}, center={mergedShapeCenter}.");
        }

        var budgetShapeScene = new VectorScene();
        budgetShapeScene.CreateEmpty();
        var softShapeBrush = BrushShape.CreateSoftRound();
        var budgetFirstShapeBrush = budgetShapeScene.AddSoftBrushStroke(
            0,
            firstShapePath,
            VectorUnits.StrokePointsToUnits(16),
            color,
            softShapeBrush,
            1_024);
        ApplyShapeBrushGradient(budgetShapeScene, budgetFirstShapeBrush, color, stops);
        var budgetFirstRetained = budgetShapeScene.NormalizePaintForInteractiveCommit(
            budgetFirstShapeBrush,
            connectNearby: true,
            frame: 0);
        var budgetSecondShapeBrush = budgetShapeScene.AddSoftBrushStroke(
            0,
            secondShapePath,
            VectorUnits.StrokePointsToUnits(16),
            color,
            softShapeBrush,
            1_024);
        ApplyShapeBrushGradient(budgetShapeScene, budgetSecondShapeBrush, color, stops);
        var rejectedByGeneralInteractiveBudget = !budgetShapeScene.CanNormalizePaintInteractively(
            budgetSecondShapeBrush,
            0);
        var budgetMergedShapeBrush = budgetShapeScene.NormalizePaintForInteractiveCommit(
            budgetSecondShapeBrush,
            connectNearby: true,
            frame: 0);
        var budgetMergedLayerStates = budgetMergedShapeBrush.Select(index =>
        {
            var hasMapping = budgetShapeScene.TryGetShapeGradientMappingWorldContours(index, out var mapping);
            return (
                Index: index,
                Kind: budgetShapeScene.GetGradientKind(index),
                ContainsFirst: budgetShapeScene.FillContainsPoint(index, firstShapePath[1]),
                ContainsSecond: budgetShapeScene.FillContainsPoint(index, secondShapePath[1]),
                MappingPoints: hasMapping ? mapping.SelectMany(contour => contour).Count() : 0);
        }).ToArray();
        var budgetMergedLayersValid = budgetMergedLayerStates.All(state =>
            state.Kind == GradientKind.ShapeRadial
            && state.ContainsFirst
            && state.ContainsSecond
            && state.MappingPoints >= 3);
        if (budgetFirstShapeBrush.Length != 3
            || budgetFirstRetained.Length != 3
            || budgetSecondShapeBrush.Length != 3
            || !rejectedByGeneralInteractiveBudget
            || budgetMergedShapeBrush.Length != 3
            || budgetShapeScene.ObjectCount != 3
            || !budgetMergedLayersValid)
        {
            throw new InvalidOperationException(
                "Matching shape-gradient brush fills stopped participating in union after exceeding the general interactive normalization budget: "
                + $"first={budgetFirstShapeBrush.Length}/{budgetFirstRetained.Length}, "
                + $"second={budgetSecondShapeBrush.Length}, rejected={rejectedByGeneralInteractiveBudget}, "
                + $"merged={budgetMergedShapeBrush.Length}, objects={budgetShapeScene.ObjectCount}, "
                + $"layersValid={budgetMergedLayersValid}, states=[{string.Join(';', budgetMergedLayerStates)}].");
        }

        var hiddenFillScene = new VectorScene();
        hiddenFillScene.CreateEmpty();
        var hiddenFirstColor = Color.FromArgb(color.A, 24, 96, 180);
        var hiddenSecondColor = Color.FromArgb(color.A, 210, 72, 36);
        var hiddenFirst = hiddenFillScene.AddSoftBrushStroke(
            0,
            firstShapePath,
            VectorUnits.StrokePointsToUnits(16),
            hiddenFirstColor,
            BrushShape.CreateTraditionalBrush(),
            24);
        ApplyShapeBrushGradient(hiddenFillScene, hiddenFirst, hiddenFirstColor, stops);

        var hiddenPreviewScene = new VectorScene();
        hiddenPreviewScene.CreateEmpty();
        var hiddenPreview = hiddenPreviewScene.AddSoftBrushStroke(
            0,
            secondShapePath,
            VectorUnits.StrokePointsToUnits(16),
            hiddenSecondColor,
            BrushShape.CreateTraditionalBrush(),
            24);
        ApplyShapeBrushGradient(hiddenPreviewScene, hiddenPreview, hiddenSecondColor, stops);
        var hiddenPreviewMatches = hiddenFillScene.FindIntersectingMatchingShapeGradientFills(
            hiddenPreviewScene,
            hiddenPreview,
            0);

        var hiddenSecond = hiddenFillScene.AddSoftBrushStroke(
            0,
            secondShapePath,
            VectorUnits.StrokePointsToUnits(16),
            hiddenSecondColor,
            BrushShape.CreateTraditionalBrush(),
            24);
        ApplyShapeBrushGradient(hiddenFillScene, hiddenSecond, hiddenSecondColor, stops);
        var hiddenMerged = hiddenFillScene.NormalizePaintForInteractiveCommit(hiddenSecond, frame: 0);
        if (hiddenPreviewMatches.Length != 1
            || hiddenMerged.Length != 1
            || hiddenFillScene.ObjectCount != 1
            || !hiddenFillScene.GetGradientStops(hiddenMerged[0]).SequenceEqual(stops)
            || !hiddenFillScene.FillContainsPoint(hiddenMerged[0], firstShapePath[1])
            || !hiddenFillScene.FillContainsPoint(hiddenMerged[0], secondShapePath[1]))
        {
            throw new InvalidOperationException(
                "Matching shape-gradient brushes depended on hidden fallback fill RGB: "
                + $"preview={hiddenPreviewMatches.Length}, merged={hiddenMerged.Length}, objects={hiddenFillScene.ObjectCount}.");
        }

        var alphaScene = new VectorScene();
        alphaScene.CreateEmpty();
        var alphaFirstColor = Color.FromArgb(220, 24, 96, 180);
        var alphaSecondColor = Color.FromArgb(160, 210, 72, 36);
        var alphaFirst = alphaScene.AddSoftBrushStroke(
            0,
            firstShapePath,
            VectorUnits.StrokePointsToUnits(16),
            alphaFirstColor,
            BrushShape.CreateTraditionalBrush(),
            24);
        ApplyShapeBrushGradient(alphaScene, alphaFirst, alphaFirstColor, stops);
        var alphaPreviewScene = new VectorScene();
        alphaPreviewScene.CreateEmpty();
        var alphaPreview = alphaPreviewScene.AddSoftBrushStroke(
            0,
            secondShapePath,
            VectorUnits.StrokePointsToUnits(16),
            alphaSecondColor,
            BrushShape.CreateTraditionalBrush(),
            24);
        ApplyShapeBrushGradient(alphaPreviewScene, alphaPreview, alphaSecondColor, stops);
        var alphaMatches = alphaScene.FindIntersectingMatchingShapeGradientFills(
            alphaPreviewScene,
            alphaPreview,
            0);
        if (alphaMatches.Length != 0)
        {
            throw new InvalidOperationException("Shape-gradient brushes with different layer alpha incorrectly matched.");
        }

        var crossLayerShapeScene = new VectorScene();
        crossLayerShapeScene.CreateEmpty(2);
        var crossLayerFirst = crossLayerShapeScene.AddSoftBrushStroke(
            0,
            firstShapePath,
            VectorUnits.StrokePointsToUnits(16),
            color,
            BrushShape.CreateTraditionalBrush(),
            24);
        ApplyShapeBrushGradient(crossLayerShapeScene, crossLayerFirst, color, stops);
        var crossLayerSecond = crossLayerShapeScene.AddSoftBrushStroke(
            1,
            secondShapePath,
            VectorUnits.StrokePointsToUnits(16),
            color,
            BrushShape.CreateTraditionalBrush(),
            24);
        ApplyShapeBrushGradient(crossLayerShapeScene, crossLayerSecond, color, stops);
        crossLayerShapeScene.ObjectOrder[crossLayerSecond[0]] = crossLayerShapeScene.ObjectOrder[crossLayerFirst[0]];
        crossLayerShapeScene.ObjectSubOrder[crossLayerSecond[0]] = crossLayerShapeScene.ObjectSubOrder[crossLayerFirst[0]];
        var crossLayerRetained = crossLayerShapeScene.NormalizePaintForInteractiveCommit(crossLayerSecond, frame: 0);
        var crossLayerValid = crossLayerShapeScene.ObjectCount == 2
            && crossLayerRetained.Length == 1
            && crossLayerShapeScene.ObjectLayer[crossLayerRetained[0]] == 1
            && crossLayerShapeScene.ObjectLayer[crossLayerFirst[0]] == 0;
        if (!crossLayerValid)
        {
            throw new InvalidOperationException(
                "Matching shape-gradient brushes crossed a layer or resolved a colliding stack key incorrectly: "
                + $"retained={crossLayerRetained.Length}, objects={crossLayerShapeScene.ObjectCount}.");
        }

        var mismatchedShapeScene = new VectorScene();
        mismatchedShapeScene.CreateEmpty();
        var mismatchedFirst = mismatchedShapeScene.AddSoftBrushStroke(
            0,
            firstShapePath,
            VectorUnits.StrokePointsToUnits(16),
            color,
            BrushShape.CreateTraditionalBrush(),
            24);
        ApplyShapeBrushGradient(mismatchedShapeScene, mismatchedFirst, color, stops);
        var changedShapeStops = stops.ToArray();
        changedShapeStops[1] = new GradientStop(changedShapeStops[1].Position, Color.FromArgb(160, Color.LimeGreen));
        var mismatchedSecond = mismatchedShapeScene.AddSoftBrushStroke(
            0,
            secondShapePath,
            VectorUnits.StrokePointsToUnits(16),
            color,
            BrushShape.CreateTraditionalBrush(),
            24);
        ApplyShapeBrushGradient(mismatchedShapeScene, mismatchedSecond, color, changedShapeStops);
        var mismatchedRetained = mismatchedShapeScene.ApplyFillOverwriteToNewObjects(mismatchedSecond, 0);
        if (mismatchedRetained.Length != 1
            || mismatchedShapeScene.ObjectCount <= 1
            || !mismatchedShapeScene.GetGradientStops(mismatchedRetained[0]).SequenceEqual(changedShapeStops)
            || Enumerable.Range(0, mismatchedShapeScene.ObjectCount)
                .Count(index => mismatchedShapeScene.FillContainsPoint(index, PointF.Empty)) != 1)
        {
            throw new InvalidOperationException("Different shape-gradient brush materials incorrectly entered the matching-gradient union path.");
        }

        var gradientOverlapScene = new VectorScene();
        gradientOverlapScene.CreateEmpty();
        var horizontalPath = new[] { new PointF(-200, 0), PointF.Empty, new PointF(200, 0) };
        var verticalPath = new[] { new PointF(0, -200), PointF.Empty, new PointF(0, 200) };
        var firstGradientBrush = gradientOverlapScene.AddSoftBrushStroke(
            0,
            horizontalPath,
            VectorUnits.StrokePointsToUnits(16),
            color,
            BrushShape.CreateTraditionalBrush(),
            32);
        ApplyBrushGradient(gradientOverlapScene, firstGradientBrush, color, GradientKind.Linear, stops, horizontalPath[0], horizontalPath[^1]);
        foreach (var objectIndex in firstGradientBrush) gradientOverlapScene.SetGradientPath(objectIndex, horizontalPath);

        var secondGradientBrush = gradientOverlapScene.AddSoftBrushStroke(
            0,
            verticalPath,
            VectorUnits.StrokePointsToUnits(16),
            color,
            BrushShape.CreateTraditionalBrush(),
            32);
        ApplyBrushGradient(gradientOverlapScene, secondGradientBrush, color, GradientKind.Linear, stops, verticalPath[0], verticalPath[^1]);
        foreach (var objectIndex in secondGradientBrush) gradientOverlapScene.SetGradientPath(objectIndex, verticalPath);

        var retainedGradientBrush = gradientOverlapScene.ApplyFillOverwriteToNewObjects(secondGradientBrush);
        if (firstGradientBrush.Length != 1
            || secondGradientBrush.Length != 1
            || retainedGradientBrush.Length != 1
            || gradientOverlapScene.ObjectCount != 3
            || !gradientOverlapScene.TryGetGradientPathWorldPoints(retainedGradientBrush[0], out var retainedSecondPath)
            || !retainedSecondPath.SequenceEqual(verticalPath)
            || !gradientOverlapScene.FillContainsPoint(retainedGradientBrush[0], PointF.Empty)
            || Enumerable.Range(0, gradientOverlapScene.ObjectCount)
                .Count(index => gradientOverlapScene.FillContainsPoint(index, PointF.Empty)) != 1
            || Enumerable.Range(0, gradientOverlapScene.ObjectCount)
                .Where(index => index != retainedGradientBrush[0])
                .Any(index => !gradientOverlapScene.HasGradient(index)
                    || gradientOverlapScene.HasGradientPath(index)
                    || !gradientOverlapScene.GetGradientStops(index).SequenceEqual(stops)))
        {
            throw new InvalidOperationException("Overlapping gradient brush strokes did not remove the covered fill or safely materialize the remaining gradient fragments.");
        }

        var pressureScene = new VectorScene();
        pressureScene.CreateEmpty();
        var pressureSamples = new[]
        {
            new PressureBrushSample(new PointF(-160, 0), 200, 0),
            new PressureBrushSample(PointF.Empty, 160, 0.2f),
            new PressureBrushSample(new PointF(160, 0), 220, 0.4f)
        };
        var pressureObjects = pressureScene.AddPressureBrushStroke(
            0,
            pressureSamples,
            VectorUnits.StrokePointsToUnits(16),
            color,
            BrushShape.CreateTraditionalBrush(),
            32,
            smoothing: 58,
            simplifyTolerance: 1);
        ApplyBrushGradient(
            pressureScene,
            pressureObjects,
            color,
            GradientKind.Radial,
            stops,
            pressureSamples[0].Point,
            pressureSamples[^1].Point);
        if (pressureObjects.Length != 1
            || !pressureScene.HasGradient(pressureObjects[0])
            || pressureScene.GetGradientKind(pressureObjects[0]) != GradientKind.Radial
            || !pressureScene.GetGradientStops(pressureObjects[0]).SequenceEqual(stops)
            || pressureScene.GetGradientStart(pressureObjects[0]) != pressureSamples[0].Point
            || pressureScene.GetGradientEnd(pressureObjects[0]) != pressureSamples[^1].Point)
        {
            throw new InvalidOperationException("Pressure brush did not retain the active gradient material.");
        }

        Console.WriteLine("brush_gradient_regression=ok");

        static void ApplyBrushGradient(
            VectorScene scene,
            IReadOnlyList<int> objects,
            Color sourceColor,
            GradientKind kind,
            IReadOnlyList<GradientStop> gradientStops,
            PointF start,
            PointF end)
        {
            var sourceAlpha = Math.Max(1, (int)sourceColor.A);
            foreach (var objectIndex in objects)
            {
                var layerAlpha = Color.FromArgb(scene.Argb[objectIndex]).A;
                scene.SetGradientPaint(
                    objectIndex,
                    kind,
                    GradientPaintUtilities.ScaleStopAlpha(gradientStops, layerAlpha / (float)sourceAlpha),
                    start,
                    end);
            }
        }

        static void ApplyShapeBrushGradient(
            VectorScene scene,
            IReadOnlyList<int> objects,
            Color sourceColor,
            IReadOnlyList<GradientStop> gradientStops)
        {
            if (objects.Count == 0) return;
            var mapping = scene.GetObjectBoundaryContours(objects[0]);
            var points = mapping.SelectMany(contour => contour).ToArray();
            if (points.Length == 0) return;
            var center = VectorUnits.Quantize(new PointF(
                (points.Min(point => point.X) + points.Max(point => point.X)) * 0.5f,
                (points.Min(point => point.Y) + points.Max(point => point.Y)) * 0.5f));
            var end = GradientPaintUtilities.TryFindShapeBoundaryPoint(mapping, center, new PointF(1, 0), out var boundary)
                ? boundary
                : new PointF(points.Max(point => point.X), center.Y);
            ApplyBrushGradient(scene, objects, sourceColor, GradientKind.ShapeRadial, gradientStops, center, end);
            foreach (var objectIndex in objects) scene.SetShapeGradientMapping(objectIndex, mapping);
        }
    }

    private static void RunBrushColorPaletteRegression()
    {
        using var material = new MaterialEditorPanel();
        material.Fill = Color.FromArgb(96, 20, 30, 40);
        var materialChanged = false;
        material.MaterialChanged += (_, _) => materialChanged = true;
        material.SetFillColorForBrush(Color.Coral);
        if (materialChanged
            || material.Fill.A != 96
            || material.Fill.R != Color.Coral.R
            || material.Fill.G != Color.Coral.G
            || material.Fill.B != Color.Coral.B
            || material.RecentColors.Count == 0)
        {
            throw new InvalidOperationException("Brush palette color selection altered scene material state or did not preserve the brush alpha.");
        }

        material.SetGradient(GradientKind.Linear, [new GradientStop(0, Color.Coral), new GradientStop(1, Color.RoyalBlue)]);
        material.SetGradientPreviewTarget(strokeTarget: true);
        var retainedFillGradient = material.GetGradientPaintForTarget(strokeTarget: false);
        if (!material.GradientPreviewOnStroke
            || !material.EditingStroke
            || retainedFillGradient.Kind != GradientKind.Linear
            || !retainedFillGradient.Stops.SequenceEqual([new GradientStop(0, Color.Coral), new GradientStop(1, Color.RoyalBlue)]))
        {
            throw new InvalidOperationException("Line gradient material controls did not preserve the Fill gradient while selecting the Stroke target.");
        }
        material.SetGradientPreviewTarget(strokeTarget: false);
        if (material.GradientPreviewOnStroke || material.EditingStroke)
        {
            throw new InvalidOperationException("Fill gradient material controls did not restore the fill target.");
        }

        var scene = new VectorScene();
        scene.CreateEmpty();
        using var stage = new StageControl(scene) { Size = new Size(640, 420) };
        var colors = new[] { Color.Coral, Color.Teal, Color.Gold, Color.MediumPurple };
        stage.SetBrushColorPalette(new Point(320, 210), colors);
        if (!stage.BrushColorPaletteVisible || stage.BrushColorPaletteColors.Count != colors.Length)
        {
            throw new InvalidOperationException("Brush recent-color palette did not become visible with its requested colors.");
        }

        for (var index = 0; index < colors.Length; index++)
        {
            var bounds = stage.BrushColorPaletteSwatchBounds(index);
            if (bounds.IsEmpty || stage.HitTestBrushColorPalette(new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2)) != index)
            {
                throw new InvalidOperationException("Brush recent-color palette swatch layout or hit testing was invalid.");
            }
        }

        stage.SetBrushColorPalette(new Point(320, 210), colors, hoveredIndex: 2);
        if (stage.BrushColorPaletteHoveredIndex != 2)
        {
            throw new InvalidOperationException("Brush recent-color palette did not retain its hovered swatch.");
        }

        stage.ClearBrushColorPalette();
        if (stage.BrushColorPaletteVisible)
        {
            throw new InvalidOperationException("Brush recent-color palette did not clear after its Shift interaction ended.");
        }

        Console.WriteLine("brush_color_palette_regression=ok");
    }

    private static void RunTraditionalBrushContourRegression()
    {
        var traditionalContour = BrushShape.CreateTraditionalBrush().NormalizedContour(0.5f);
        var radii = traditionalContour.Select(point => MathF.Sqrt(point.X * point.X + point.Y * point.Y)).ToArray();
        var traditionalRadiusSpread = radii.Max() - radii.Min();
        if (traditionalRadiusSpread > 0.0001f)
        {
            throw new InvalidOperationException("Traditional brush contour introduced radial spikes instead of a circular hard edge.");
        }

        var squareContour = BrushShape.CreateTraditionalBrush(
                TraditionalBrushTipKind.Square,
                widthPercent: 50,
                directionDegrees: 0)
            .NormalizedContour(0.5f);
        var squareWidth = squareContour.Max(point => point.X) - squareContour.Min(point => point.X);
        var squareHeight = squareContour.Max(point => point.Y) - squareContour.Min(point => point.Y);
        if (squareContour.Length != 4
            || Math.Abs(squareWidth - 1f) > 0.0001f
            || Math.Abs(squareHeight - 2f) > 0.0001f)
        {
            throw new InvalidOperationException("Traditional square brush did not retain its requested shape width.");
        }

        var rotatedSquare = BrushShape.CreateTraditionalBrush(
                TraditionalBrushTipKind.Square,
                widthPercent: 50,
                directionDegrees: 90)
            .NormalizedContour(0.5f);
        var rotatedWidth = rotatedSquare.Max(point => point.X) - rotatedSquare.Min(point => point.X);
        var rotatedHeight = rotatedSquare.Max(point => point.Y) - rotatedSquare.Min(point => point.Y);
        if (Math.Abs(rotatedWidth - 2f) > 0.0001f
            || Math.Abs(rotatedHeight - 1f) > 0.0001f)
        {
            throw new InvalidOperationException("Traditional brush direction did not rotate the square tip.");
        }

        var scene = new VectorScene();
        scene.CreateEmpty();
        var diameter = VectorUnits.StrokePointsToUnits(16);
        var objects = scene.AddSoftBrushStroke(
            0,
            [new PointF(-180, 0), new PointF(0, 0), new PointF(180, 0)],
            diameter,
            Color.Coral,
            BrushShape.CreateTraditionalBrush(),
            32,
            frequency: 8,
            continuous: true);
        if (objects.Length != 1
            || scene.ShapeKind[objects[0]] != ShapeKind.Path
            || !scene.FillContainsPoint(objects[0], PointF.Empty))
        {
            throw new InvalidOperationException("Traditional brush did not create one continuous rounded stroke contour.");
        }

        var squareScene = new VectorScene();
        squareScene.CreateEmpty();
        var squareObjects = squareScene.AddSoftBrushStroke(
            0,
            [PointF.Empty],
            diameter,
            Color.Coral,
            BrushShape.CreateTraditionalBrush(TraditionalBrushTipKind.Square, widthPercent: 50),
            8,
            frequency: 8,
            continuous: true);
        if (squareObjects.Length != 1
            || !squareScene.FillContainsPoint(squareObjects[0], new PointF(0, diameter * 0.35f))
            || squareScene.FillContainsPoint(squareObjects[0], new PointF(diameter * 0.32f, 0)))
        {
            throw new InvalidOperationException("Traditional square brush geometry did not use its configured width.");
        }
    }

    private static void RunBrushFrequencyRegression()
    {
        var diameter = VectorUnits.StrokePointsToUnits(24);
        var centerline = new[] { new PointF(-diameter * 4, 0), new PointF(diameter * 4, 0) };
        var continuous = new VectorScene();
        continuous.CreateEmpty();
        var continuousObjects = continuous.AddSoftBrushStroke(
            0,
            centerline,
            diameter,
            Color.Coral,
            BrushShape.CreateSoftRound(),
            32,
            frequency: 8,
            continuous: true);
        if (continuousObjects.Length == 0 || !continuousObjects.Any(index => continuous.FillContainsPoint(index, PointF.Empty)))
        {
            throw new InvalidOperationException("Continuous brush spacing left a gap between distant pointer samples.");
        }

        var stamped = new VectorScene();
        stamped.CreateEmpty();
        var stampedObjects = stamped.AddSoftBrushStroke(
            0,
            centerline,
            diameter,
            Color.Coral,
            BrushShape.CreateSoftRound(),
            32,
            frequency: 8,
            continuous: false);
        if (stampedObjects.Length == 0 || stampedObjects.Any(index => stamped.FillContainsPoint(index, PointF.Empty)))
        {
            throw new InvalidOperationException("Non-continuous brush mode did not retain stamp spacing.");
        }

        var softRadius = BrushShape.CreateSoftRound(0).NormalizedContour(0.70f)
            .Select(point => MathF.Sqrt(point.X * point.X + point.Y * point.Y))
            .Average();
        var hardRadius = BrushShape.CreateSoftRound(80).NormalizedContour(0.70f)
            .Select(point => MathF.Sqrt(point.X * point.X + point.Y * point.Y))
            .Average();
        if (hardRadius <= softRadius + 0.1f)
        {
            throw new InvalidOperationException("Default soft round brush hardness did not widen its opaque core.");
        }

        Console.WriteLine("brush_frequency_regression=ok");
    }

    public static void RunPressureBrushRegression()
    {
        RunSoftBrushRegression();
        RunBrushColorPaletteRegression();

        var onsetSamples = new[]
        {
            new PressureBrushSample(new PointF(0, 0), 0, 0),
            new PressureBrushSample(new PointF(20, 0), 0, 0.02f),
            new PressureBrushSample(new PointF(1000, 0), 0, 0.8f)
        };
        var onsetDiameter = VectorUnits.StrokePointsToUnits(18);
        var onsetPreview = FreehandStrokeProcessor.CreatePressurePreview(onsetSamples, onsetDiameter);
        var onsetCommit = FreehandStrokeProcessor.ProcessPressure(onsetSamples, onsetDiameter, smoothing: 0, simplifyTolerance: 0.25f);
        var minimumDiameter = VectorUnits.MinimumStrokeUnits;
        var quantizedMinimumOnset = VectorUnits.Quantize(minimumDiameter * 0.6f);
        if (onsetPreview.Length != onsetSamples.Length
            || onsetPreview[0].Diameter != quantizedMinimumOnset
            || onsetPreview[1].Diameter <= onsetPreview[0].Diameter
            || onsetPreview[^1].Diameter <= onsetPreview[1].Diameter * 2f
            || onsetCommit.Length < 2
            || onsetCommit[0].Diameter != quantizedMinimumOnset
            || onsetCommit[^1].Diameter <= onsetCommit[0].Diameter * 8f)
        {
            throw new InvalidOperationException("Pressure brush onset did not diffuse from a minimal initial deposit.");
        }

        var tabletBaseDiameter = VectorUnits.StrokePointsToUnits(20);
        var tabletMinimumVisibleDiameter = minimumDiameter * 0.6f;
        var tabletMappingCases = new[]
        {
            (Pressure: -0.5f, ExpectedPressure: 0f),
            (Pressure: 0f, ExpectedPressure: 0f),
            (Pressure: 0.5f, ExpectedPressure: 0.5f),
            (Pressure: 1f, ExpectedPressure: 1f),
            (Pressure: 1.5f, ExpectedPressure: 1f)
        };
        foreach (var mapping in tabletMappingCases)
        {
            var tabletSample = new PressureBrushSample(PointF.Empty, 5000, 0, mapping.Pressure);
            var tabletPreview = FreehandStrokeProcessor.CreatePressurePreview([tabletSample], tabletBaseDiameter);
            var tabletProcess = FreehandStrokeProcessor.ProcessPressure(
                [tabletSample],
                tabletBaseDiameter,
                smoothing: 0,
                simplifyTolerance: 0.25f);
            var incrementalTabletProfile = new List<PressureBrushPoint>();
            var incrementalTabletDistances = new List<float>();
            FreehandStrokeProcessor.UpdatePressurePreviewProfile(
                [tabletSample],
                tabletBaseDiameter,
                incrementalTabletProfile,
                incrementalTabletDistances);
            var expectedDiameter = VectorUnits.Quantize(Math.Clamp(
                tabletBaseDiameter * mapping.ExpectedPressure,
                tabletMinimumVisibleDiameter,
                tabletBaseDiameter * 1.2f));
            if (tabletPreview.Length != 1
                || tabletProcess.Length != 1
                || incrementalTabletProfile.Count != 1
                || tabletPreview[0].Diameter != expectedDiameter
                || tabletProcess[0].Diameter != expectedDiameter
                || incrementalTabletProfile[0].Diameter != expectedDiameter)
            {
                throw new InvalidOperationException(
                    "Tablet pressure did not map and clamp consistently across pressure-brush profiles: "
                    + $"pressure={mapping.Pressure}, expected={expectedDiameter}, "
                    + $"preview={tabletPreview.FirstOrDefault().Diameter}, "
                    + $"process={tabletProcess.FirstOrDefault().Diameter}, "
                    + $"incremental={incrementalTabletProfile.FirstOrDefault().Diameter}.");
            }
        }

        var tabletPrecedenceDiameters = new[]
        {
            new PressureBrushSample(PointF.Empty, 0, 12, 0.5f),
            new PressureBrushSample(PointF.Empty, 5000, 0, 0.5f)
        }.Select(sample => FreehandStrokeProcessor.CreatePressurePreview([sample], tabletBaseDiameter)[0].Diameter)
            .ToArray();
        if (tabletPrecedenceDiameters[0] != tabletPrecedenceDiameters[1]
            || tabletPrecedenceDiameters[0] != VectorUnits.Quantize(tabletBaseDiameter * 0.5f))
        {
            throw new InvalidOperationException("Tablet pressure did not take precedence over speed and hold simulation.");
        }

        const float legacySpeed = 720f;
        const float legacyHeldSeconds = 0.42f;
        var implicitLegacySample = new PressureBrushSample(PointF.Empty, legacySpeed, legacyHeldSeconds);
        var explicitLegacySample = implicitLegacySample with { TabletPressure = float.NaN };
        var implicitLegacyPreview = FreehandStrokeProcessor.CreatePressurePreview(
            [implicitLegacySample],
            tabletBaseDiameter);
        var explicitLegacyPreview = FreehandStrokeProcessor.CreatePressurePreview(
            [explicitLegacySample],
            tabletBaseDiameter);
        var implicitLegacyProcess = FreehandStrokeProcessor.ProcessPressure(
            [implicitLegacySample],
            tabletBaseDiameter,
            smoothing: 0,
            simplifyTolerance: 0.25f);
        var legacyNormalizedSpeed = Math.Clamp(legacySpeed / 1800f, 0f, 1f);
        var legacySpeedPressure = 0.38f + 0.62f * MathF.Pow(1f - legacyNormalizedSpeed, 0.65f);
        var legacyHoldPressure = 0.55f + 0.45f * (1f - MathF.Exp(-legacyHeldSeconds / 0.15f));
        var legacyTargetDiameter = Math.Clamp(
            tabletBaseDiameter * legacySpeedPressure * legacyHoldPressure,
            tabletMinimumVisibleDiameter,
            tabletBaseDiameter * 1.2f);
        var legacyOnset = 1f - MathF.Exp(-legacyHeldSeconds / 0.18f);
        var expectedLegacyDiameter = VectorUnits.Quantize(
            tabletMinimumVisibleDiameter
                + (legacyTargetDiameter - tabletMinimumVisibleDiameter) * legacyOnset);
        if (!float.IsNaN(implicitLegacySample.TabletPressure)
            || implicitLegacyPreview.Length != 1
            || explicitLegacyPreview.Length != 1
            || implicitLegacyProcess.Length != 1
            || implicitLegacyPreview[0].Diameter != expectedLegacyDiameter
            || explicitLegacyPreview[0].Diameter != expectedLegacyDiameter
            || implicitLegacyProcess[0].Diameter != expectedLegacyDiameter)
        {
            throw new InvalidOperationException("NaN tablet pressure did not preserve the legacy speed and hold fallback.");
        }

        var tabletProfileSamples = new[]
        {
            new PressureBrushSample(new PointF(0, 0), 5000, 0, 0.2f),
            new PressureBrushSample(new PointF(100, 80), 0, 8, 0.8f),
            new PressureBrushSample(new PointF(200, -80), 5000, 0, 0.35f),
            new PressureBrushSample(new PointF(300, 80), 0, 8, 1f),
            new PressureBrushSample(new PointF(400, 0), 5000, 0, 0.5f)
        };
        var tabletPreviewProfile = FreehandStrokeProcessor.CreatePressurePreview(
            tabletProfileSamples,
            tabletBaseDiameter,
            smoothing: 0);
        var tabletProcessProfile = FreehandStrokeProcessor.ProcessPressure(
            tabletProfileSamples,
            tabletBaseDiameter,
            smoothing: 0,
            simplifyTolerance: 0.25f);
        var incrementalTabletPreview = new List<PressureBrushPoint>();
        var incrementalTabletTravel = new List<float>();
        FreehandStrokeProcessor.UpdatePressurePreviewProfile(
            tabletProfileSamples,
            tabletBaseDiameter,
            incrementalTabletPreview,
            incrementalTabletTravel);
        var smoothedTabletPreview = FreehandStrokeProcessor.CreatePressurePreview(
            tabletProfileSamples,
            tabletBaseDiameter,
            smoothing: 70);
        var smoothedIncrementalTabletPreview = FreehandStrokeProcessor.SmoothPressureProfile(
            incrementalTabletPreview,
            smoothing: 70);
        var previewProcessMismatch = tabletPreviewProfile.Length != tabletProcessProfile.Length
            || tabletPreviewProfile.Zip(tabletProcessProfile).Any(pair =>
                pair.First.Point != pair.Second.Point
                || MathF.Abs(pair.First.Diameter - pair.Second.Diameter) > 1);
        var previewIncrementalMismatch = tabletPreviewProfile.Length != incrementalTabletPreview.Count
            || tabletPreviewProfile.Zip(incrementalTabletPreview).Any(pair =>
                pair.First.Point != pair.Second.Point
                || MathF.Abs(pair.First.Diameter - pair.Second.Diameter) > 2);
        var smoothedPreviewMismatch = smoothedTabletPreview.Length != smoothedIncrementalTabletPreview.Length
            || smoothedTabletPreview.Zip(smoothedIncrementalTabletPreview).Any(pair =>
                pair.First.Point != pair.Second.Point
                || MathF.Abs(pair.First.Diameter - pair.Second.Diameter) > 2);
        if (previewProcessMismatch
            || previewIncrementalMismatch
            || smoothedPreviewMismatch
            || tabletPreviewProfile[1].Diameter >= VectorUnits.Quantize(tabletBaseDiameter * 0.8f))
        {
            throw new InvalidOperationException(
                "Tablet pressure diverged between preview, incremental preview, and processed profiles or bypassed smoothing.");
        }

        var samples = new PressureBrushSample[513];
        for (var i = 0; i < samples.Length; i++)
        {
            var x = -640 + i * 2.5f;
            samples[i] = new PressureBrushSample(
                new PointF(x, MathF.Sin(i * 0.08f) * 18),
                120 + i % 9 * 40,
                i / 120f);
        }

        var diameter = VectorUnits.StrokePointsToUnits(18);
        var incrementalPressureSamples = new List<PressureBrushSample>();
        var incrementalPressureProfile = new List<PressureBrushPoint>();
        var incrementalPressureDistances = new List<float>();
        for (var start = 0; start < samples.Length; start += 37)
        {
            incrementalPressureSamples.AddRange(samples.Skip(start).Take(Math.Min(37, samples.Length - start)));
            FreehandStrokeProcessor.UpdatePressurePreviewProfile(
                incrementalPressureSamples,
                diameter,
                incrementalPressureProfile,
                incrementalPressureDistances);
        }
        var completePressureProfile = FreehandStrokeProcessor.CreatePressurePreview(samples, diameter, smoothing: 0);
        var maximumIncrementalDiameterError = incrementalPressureProfile
            .Zip(completePressureProfile)
            .Max(pair => MathF.Abs(pair.First.Diameter - pair.Second.Diameter));
        incrementalPressureSamples[^1] = incrementalPressureSamples[^1] with
        {
            HeldSeconds = incrementalPressureSamples[^1].HeldSeconds + 0.5f
        };
        FreehandStrokeProcessor.UpdatePressurePreviewProfile(
            incrementalPressureSamples,
            diameter,
            incrementalPressureProfile,
            incrementalPressureDistances);
        var heldPressureProfile = FreehandStrokeProcessor.CreatePressurePreview(
            incrementalPressureSamples,
            diameter,
            smoothing: 0);
        if (incrementalPressureProfile.Count != completePressureProfile.Length
            || incrementalPressureDistances.Count != incrementalPressureProfile.Count
            || maximumIncrementalDiameterError > 2
            || MathF.Abs(incrementalPressureProfile[^1].Diameter - heldPressureProfile[^1].Diameter) > 2)
        {
            throw new InvalidOperationException(
                "Incremental pressure preview diverged from the complete pressure profile: "
                + $"points={incrementalPressureProfile.Count}, diameter_error={maximumIncrementalDiameterError:0.00}.");
        }

        var scene = new VectorScene();
        scene.CreateEmpty();
        var stopwatch = Stopwatch.StartNew();
        var softObjects = scene.AddPressureBrushStroke(
            0,
            samples,
            diameter,
            Color.Coral,
            BrushShape.CreateSoftRound(),
            (uint)samples.Length,
            smoothing: 58,
            simplifyTolerance: 1,
            frequency: 8,
            continuous: true);
        stopwatch.Stop();
        const double pressureBrushCommitBudgetMilliseconds = 10;
        var pressureBrushCommitBudgetMet = stopwatch.Elapsed.TotalMilliseconds <= pressureBrushCommitBudgetMilliseconds;
        if (softObjects.Length != 3
            || softObjects.Any(index => scene.ShapeKind[index] != ShapeKind.Path)
            || !softObjects.All(index => scene.FillContainsPoint(index, samples[samples.Length / 2].Point))
            || !pressureBrushCommitBudgetMet)
        {
            throw new InvalidOperationException(
                $"Continuous pressure brush exceeded its interaction budget or lost layered Path fills: "
                + $"elapsed={stopwatch.Elapsed.TotalMilliseconds:0.00} ms.");
        }

        var traditional = new VectorScene();
        traditional.CreateEmpty();
        var traditionalObjects = traditional.AddPressureBrushStroke(
            0,
            samples,
            diameter,
            Color.Coral,
            BrushShape.CreateTraditionalBrush(),
            (uint)samples.Length,
            smoothing: 58,
            simplifyTolerance: 1,
            frequency: 8,
            continuous: true);
        if (traditionalObjects.Length != 1
            || traditional.ShapeKind[traditionalObjects[0]] != ShapeKind.Path
            || !traditional.FillContainsPoint(traditionalObjects[0], samples[samples.Length / 2].Point))
        {
            throw new InvalidOperationException("Traditional pressure brush did not commit a solid Path fill.");
        }

        var cornerSamples = new[]
        {
            new PressureBrushSample(new PointF(-120, -80), 240, 0),
            new PressureBrushSample(new PointF(0, 80), 160, 0.3f),
            new PressureBrushSample(new PointF(120, -80), 240, 0.6f)
        };
        var cornerScene = new VectorScene();
        cornerScene.CreateEmpty();
        var cornerObjects = cornerScene.AddPressureBrushStroke(
            0,
            cornerSamples,
            diameter,
            Color.Coral,
            BrushShape.CreateTraditionalBrush(),
            12,
            smoothing: 70,
            simplifyTolerance: 1,
            frequency: 8,
            continuous: true);
        if (cornerObjects.Length != 1 || !cornerScene.FillContainsPoint(cornerObjects[0], cornerSamples[1].Point))
        {
            throw new InvalidOperationException("Pressure brush left an unfilled gap at a sharp corner.");
        }

        var longPreviewSamples = new PressureBrushSample[16_384];
        for (var index = 0; index < longPreviewSamples.Length; index++)
        {
            longPreviewSamples[index] = new PressureBrushSample(
                new PointF(index * 2, MathF.Sin(index * 0.03f) * 80),
                180 + index % 17 * 20,
                index / 240f);
        }
        const int previewIterations = 64;
        var previewWatch = Stopwatch.StartNew();
        IReadOnlyList<PressureBrushSample> boundedPreview = [];
        PressureBrushPoint[] previewProfile = [];
        for (var iteration = 0; iteration < previewIterations; iteration++)
        {
            boundedPreview = MainForm.LimitDrawingPreview(longPreviewSamples, 256);
            previewProfile = FreehandStrokeProcessor.CreatePressurePreview(boundedPreview, diameter, smoothing: 58);
        }
        previewWatch.Stop();
        var pressurePreviewAverageMilliseconds = previewWatch.Elapsed.TotalMilliseconds / previewIterations;
        if (boundedPreview.Count != 256
            || previewProfile.Length != 256
            || boundedPreview[0].Point != longPreviewSamples[0].Point
            || boundedPreview[^1].Point != longPreviewSamples[^1].Point
            || pressurePreviewAverageMilliseconds > pressureBrushCommitBudgetMilliseconds)
        {
            throw new InvalidOperationException(
                $"Long pressure-brush preview exceeded its interaction budget or lost trajectory endpoints: "
                + $"points={boundedPreview.Count}, profile={previewProfile.Length}, avg={pressurePreviewAverageMilliseconds:0.000} ms.");
        }

        var longTrajectory = new PointF[32_768];
        for (var index = 0; index < longTrajectory.Length; index++)
        {
            longTrajectory[index] = new PointF(
                index * 2,
                MathF.Sin(index * 0.011f) * 160 + MathF.Sin(index * 0.037f) * 35);
        }
        var processedLongTrajectory = FreehandStrokeProcessor.Process(longTrajectory, smoothing: 30, simplifyTolerance: 1);
        var trajectoryStart = processedLongTrajectory[0];
        var trajectoryEnd = processedLongTrajectory[^1];
        var trajectoryDx = trajectoryEnd.X - trajectoryStart.X;
        var trajectoryDy = trajectoryEnd.Y - trajectoryStart.Y;
        var trajectoryLength = Math.Max(0.0001f, MathF.Sqrt(trajectoryDx * trajectoryDx + trajectoryDy * trajectoryDy));
        var maximumCurveDistance = processedLongTrajectory.Max(point => MathF.Abs(
            trajectoryDy * point.X - trajectoryDx * point.Y
            + trajectoryEnd.X * trajectoryStart.Y - trajectoryEnd.Y * trajectoryStart.X) / trajectoryLength);
        if (processedLongTrajectory.Length < 64 || maximumCurveDistance < 120)
        {
            throw new InvalidOperationException(
                "Long brush processing collapsed a curved trajectory into a straight line: "
                + $"source={longTrajectory.Length}, processed={processedLongTrajectory.Length}, curve={maximumCurveDistance:0.00}.");
        }

        var incrementalPreviewPath = new PointF[769];
        for (var index = 0; index < incrementalPreviewPath.Length; index++)
        {
            incrementalPreviewPath[index] = new PointF(
                index * 12,
                MathF.Sin(index * 0.075f) * 150 + MathF.Sin(index * 0.19f) * 28);
        }
        var incrementalPreviewScene = new VectorScene();
        incrementalPreviewScene.CreateEmpty();
        var incrementalBrush = BrushShape.CreateSoftRound();
        var stableCoverage = new List<PointF>();
        var processedPreviewSamples = 0;
        while (processedPreviewSamples < incrementalPreviewPath.Length)
        {
            var nextSample = Math.Min(incrementalPreviewPath.Length, processedPreviewSamples + 32);
            var segmentStart = processedPreviewSamples == 0 ? 0 : processedPreviewSamples - 1;
            var additions = incrementalPreviewScene.AddSoftBrushStroke(
                0,
                incrementalPreviewPath[segmentStart..nextSample],
                diameter,
                Color.Teal,
                incrementalBrush,
                (uint)Math.Max(3, nextSample - segmentStart),
                frequency: 8,
                continuous: true);
            incrementalPreviewScene.MergeSameColorFillsAroundNewObjects(additions, connectNearby: false, frame: 0);
            processedPreviewSamples = nextSample;

            bool PreviewContains(PointF point) => Enumerable.Range(0, incrementalPreviewScene.ObjectCount)
                .Any(objectIndex => incrementalPreviewScene.FillContainsPoint(objectIndex, point));

            if (stableCoverage.Count == 0)
            {
                var bounds = incrementalPreviewScene.GetObjectWorldBounds(0);
                var gridStep = Math.Max(8, diameter / 6);
                for (var y = bounds.Top + gridStep * 0.5f; y < bounds.Bottom; y += gridStep)
                {
                    for (var x = bounds.Left + gridStep * 0.5f; x < bounds.Right; x += gridStep)
                    {
                        var probe = new PointF(x, y);
                        if (PreviewContains(probe)) stableCoverage.Add(probe);
                    }
                }
            }
            else if (stableCoverage.Any(point => !PreviewContains(point)))
            {
                throw new InvalidOperationException("Incremental brush preview changed an already covered area.");
            }
        }

        if (stableCoverage.Count < 12
            || incrementalPreviewScene.ObjectCount != incrementalBrush.Layers.Count
            || incrementalPreviewPath.Where((_, index) => index % 48 == 0)
                .Any(point => !Enumerable.Range(0, incrementalPreviewScene.ObjectCount)
                    .Any(objectIndex => incrementalPreviewScene.FillContainsPoint(objectIndex, point))))
        {
            throw new InvalidOperationException("Incremental brush preview did not preserve and extend its accumulated area.");
        }

        Console.WriteLine($"pressure_brush_commit_ms={stopwatch.Elapsed.TotalMilliseconds:0.00}");
        Console.WriteLine($"pressure_brush_commit_budget_ms={pressureBrushCommitBudgetMilliseconds:0.00}");
        Console.WriteLine($"pressure_brush_commit_budget_met={pressureBrushCommitBudgetMet.ToString().ToLowerInvariant()}");
        Console.WriteLine($"pressure_brush_preview_source_points={longPreviewSamples.Length}");
        Console.WriteLine($"pressure_brush_preview_points={boundedPreview.Count}");
        Console.WriteLine($"pressure_brush_preview_avg_ms={pressurePreviewAverageMilliseconds:0.000}");
        Console.WriteLine($"pressure_brush_preview_budget_met={(pressurePreviewAverageMilliseconds <= pressureBrushCommitBudgetMilliseconds).ToString().ToLowerInvariant()}");
    }

    private static void RunBrushWorldScaleRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        using var stage = new StageControl(scene) { Size = new Size(960, 540) };
        stage.SetVisibleWorldWidth(VectorUnits.DefaultVisibleWorldWidth);
        var diameter = VectorUnits.StrokePointsToUnits(18);
        var initialScreenDiameter = stage.WorldLengthToScreen(diameter);
        stage.SetBrushTipCursor(new Point(stage.Width / 2, stage.Height / 2), BrushShape.CreateSoftRound(), diameter, eraser: false);
        var initialCursorRadius = stage.BrushTipCursorRadiusPixels;
        stage.ZoomAt(new Point(stage.Width / 2, stage.Height / 2), 2);
        var zoomedScreenDiameter = stage.WorldLengthToScreen(diameter);
        var zoomedCursorRadius = stage.BrushTipCursorRadiusPixels;
        var restoredWorldDiameter = stage.ScreenLengthToWorld(zoomedScreenDiameter);
        if (Math.Abs(zoomedScreenDiameter - initialScreenDiameter * 2) > 0.001f
            || Math.Abs(restoredWorldDiameter - diameter) > 0.001f
            || Math.Abs(zoomedCursorRadius - initialCursorRadius * 2) > 0.001f)
        {
            throw new InvalidOperationException("Brush size did not retain its world-unit diameter across stage zoom changes.");
        }

        Console.WriteLine("brush_world_scale_regression=ok");
    }

    private static void RunBrushEraserRegression()
    {
        var shape = BrushShape.CreateSoftRound();
        var diameter = VectorUnits.StrokePointsToUnits(24);
        var center = new[] { PointF.Empty };

        static bool LineTouchesPoint(VectorScene scene, PointF point)
        {
            for (var index = 0; index < scene.ObjectCount; index++)
            {
                if (scene.ShapeKind[index] != ShapeKind.Line
                    || !scene.TryGetClosestPointOnLine(index, point, out _, out _, out var distance))
                {
                    continue;
                }

                if (distance <= scene.Stroke[index] * 0.5f + DrawingTopologyRules.UnitIntersectionTolerance) return true;
            }

            return false;
        }

        static PointF EvaluateCubic(PointF start, PointF control1, PointF control2, PointF end, float amount)
        {
            var inverse = 1f - amount;
            var inverseSquared = inverse * inverse;
            var amountSquared = amount * amount;
            return new PointF(
                inverseSquared * inverse * start.X
                    + 3f * inverseSquared * amount * control1.X
                    + 3f * inverse * amountSquared * control2.X
                    + amountSquared * amount * end.X,
                inverseSquared * inverse * start.Y
                    + 3f * inverseSquared * amount * control1.Y
                    + 3f * inverse * amountSquared * control2.Y
                    + amountSquared * amount * end.Y);
        }

        static VectorScene CreateTargetOptionScene(float stroke)
        {
            var scene = new VectorScene();
            scene.CreateEmpty();
            scene.AddObject(
                0,
                PointF.Empty,
                new SizeF(600, 400),
                0,
                0,
                Color.Coral,
                Color.Transparent,
                12,
                ShapeKind.Rectangle);
            scene.AddLineSegment(
                0,
                new PointF(-300, 0),
                new PointF(300, 0),
                stroke,
                Color.Transparent,
                Color.Aqua,
                12);
            return scene;
        }

        var fillScene = new VectorScene();
        fillScene.CreateEmpty();
        fillScene.AddObject(0, PointF.Empty, new SizeF(400, 240), 0, 0, Color.Coral, Color.Transparent, 12, ShapeKind.Rectangle);
        if (!fillScene.EraseWithBrushStroke(0, center, diameter, shape, eraseLines: false, eraseFills: true)
            || Enumerable.Range(0, fillScene.ObjectCount).Any(index => fillScene.FillContainsPoint(index, PointF.Empty)))
        {
            throw new InvalidOperationException("Brush eraser did not punch a local hole through a fill.");
        }

        var shapeGradientScene = new VectorScene();
        shapeGradientScene.CreateEmpty();
        var shapeGradient = shapeGradientScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(400, 240),
            0,
            0,
            Color.Coral,
            Color.Transparent,
            12,
            ShapeKind.Rectangle);
        var shapeGradientStops = new[]
        {
            new GradientStop(0, Color.Gold),
            new GradientStop(0.5f, Color.Coral),
            new GradientStop(1, Color.RoyalBlue)
        };
        var originalShapeMapping = shapeGradientScene.GetObjectBoundaryContours(shapeGradient);
        shapeGradientScene.SetGradientPaint(
            shapeGradient,
            GradientKind.ShapeRadial,
            shapeGradientStops,
            PointF.Empty,
            new PointF(200, 0));
        shapeGradientScene.SetShapeGradientMapping(shapeGradient, originalShapeMapping);
        var erasedShapeGradient = shapeGradientScene.EraseWithBrushStroke(
            0,
            center,
            diameter,
            shape,
            eraseLines: false,
            eraseFills: true);
        var shapeGradientFragments = Enumerable.Range(0, shapeGradientScene.ObjectCount)
            .Where(index => shapeGradientScene.HasGradient(index)
                && shapeGradientScene.GetGradientKind(index) == GradientKind.ShapeRadial)
            .ToArray();
        var shapeGradientStopsPreserved = shapeGradientFragments.All(index =>
            shapeGradientScene.GetGradientStops(index).SequenceEqual(shapeGradientStops));
        var shapeGradientCentersMoved = shapeGradientFragments.All(index =>
            shapeGradientScene.GetGradientStart(index) != PointF.Empty);
        var shapeGradientCentersInside = shapeGradientFragments.All(index =>
            shapeGradientScene.FillContainsPoint(index, shapeGradientScene.GetGradientStart(index)));
        var originalShapeMappingPoints = originalShapeMapping
            .SelectMany(contour => contour)
            .ToHashSet();
        var shapeGradientMappingsUpdated = shapeGradientFragments.All(index =>
        {
            if (!shapeGradientScene.TryGetShapeGradientMappingWorldContours(index, out var mapping)) return false;
            var mappingPoints = mapping.SelectMany(contour => contour).ToHashSet();
            var boundaryPoints = shapeGradientScene.GetObjectBoundaryContours(index)
                .SelectMany(contour => contour)
                .ToHashSet();
            return mappingPoints.SetEquals(boundaryPoints)
                && !mappingPoints.SetEquals(originalShapeMappingPoints);
        });
        var updatedShapeGradients = shapeGradientFragments.Length > 0
            && shapeGradientStopsPreserved
            && shapeGradientCentersMoved
            && shapeGradientCentersInside
            && shapeGradientMappingsUpdated;
        if (!erasedShapeGradient || !updatedShapeGradients)
        {
            throw new InvalidOperationException(
                $"Erasing a shape-gradient fill did not recalculate each remaining gradient: erased={erasedShapeGradient}, "
                + $"fragments={shapeGradientFragments.Length}, stops={shapeGradientStopsPreserved}, moved={shapeGradientCentersMoved}, "
                + $"inside={shapeGradientCentersInside}, mappings={shapeGradientMappingsUpdated}.");
        }

        var lineScene = new VectorScene();
        lineScene.CreateEmpty(2);
        var lineStroke = VectorUnits.StrokePointsToUnits(10);
        var lineStops = new[]
        {
            new GradientStop(0, Color.Aqua),
            new GradientStop(0.5f, Color.Gold),
            new GradientStop(1, Color.Coral)
        };
        var lineGradientStart = new PointF(-240, 0);
        var lineGradientEnd = new PointF(240, 0);
        var lineSource = lineScene.AddCubicCurveSegment(
            1,
            new PointF(-240, 0),
            new PointF(-160, -180),
            new PointF(160, 180),
            new PointF(240, 0),
            lineStroke,
            Color.Transparent,
            Color.Aqua,
            17,
            LineEndpointStyle.Sharp,
            LineEndpointStyle.Sharp);
        lineScene.SetGradientPaint(
            lineSource,
            GradientKind.Linear,
            lineStops,
            lineGradientStart,
            lineGradientEnd);
        var lineOrder = lineScene.ObjectOrder[lineSource];
        var lineKeyframe = lineScene.ObjectKeyframeFrame[lineSource];
        if (!lineScene.EraseWithBrushStroke(0, center, diameter, shape, eraseLines: true, eraseFills: false))
        {
            throw new InvalidOperationException("Brush eraser did not remove the contacted portion of a curved line.");
        }

        var lineFragments = Enumerable.Range(0, lineScene.ObjectCount)
            .Where(index => lineScene.ShapeKind[index] == ShapeKind.Line)
            .OrderBy(index => lineScene.ObjectSubOrder[index])
            .ToArray();
        var lineTopologyPreserved = lineFragments.Length == 2
            && lineFragments.Length == lineScene.ObjectCount
            && !LineTouchesPoint(lineScene, PointF.Empty)
            && lineFragments.All(index => lineScene.ObjectLayer[index] == 1
                && lineScene.Stroke[index] == lineStroke
                && lineScene.StrokeArgb[index] == Color.Aqua.ToArgb()
                && lineScene.ObjectOrder[index] == lineOrder
                && lineScene.ObjectKeyframeFrame[index] == lineKeyframe
                && lineScene.HasGradient(index)
                && lineScene.GetGradientKind(index) == GradientKind.Linear
                && lineScene.GetGradientStops(index).SequenceEqual(lineStops)
                && lineScene.GetGradientStart(index) == lineGradientStart
                && lineScene.GetGradientEnd(index) == lineGradientEnd)
            && lineScene.GetLineEndpointStyle(lineFragments[0], startEndpoint: true) == LineEndpointStyle.Sharp
            && lineScene.GetLineEndpointStyle(lineFragments[0], startEndpoint: false) == LineEndpointStyle.Round
            && lineScene.GetLineEndpointStyle(lineFragments[1], startEndpoint: true) == LineEndpointStyle.Round
            && lineScene.GetLineEndpointStyle(lineFragments[1], startEndpoint: false) == LineEndpointStyle.Sharp
            && lineScene.ObjectSubOrder[lineFragments[1]] > lineScene.ObjectSubOrder[lineFragments[0]]
            && lineFragments.Sum(index => (long)lineScene.AtomCount[index]) == 17;
        if (!lineTopologyPreserved)
        {
            throw new InvalidOperationException(
                "Brush eraser converted or lost curved-line topology, material, endpoints, ownership, order, or atoms.");
        }

        lineScene.TryGetLineBezierPart(
            lineFragments[0],
            0,
            1,
            out var firstFragmentStart,
            out var firstFragmentControl1,
            out var firstFragmentControl2,
            out var firstFragmentEnd);
        var secondContactPoint = EvaluateCubic(
            firstFragmentStart,
            firstFragmentControl1,
            firstFragmentControl2,
            firstFragmentEnd,
            0.5f);
        var secondContact = new[] { secondContactPoint };
        if (!lineScene.EraseWithBrushStroke(0, secondContact, diameter, shape, eraseLines: true, eraseFills: false)
            || Enumerable.Range(0, lineScene.ObjectCount).Any(index => lineScene.ShapeKind[index] != ShapeKind.Line)
            || LineTouchesPoint(lineScene, secondContactPoint))
        {
            throw new InvalidOperationException("A previously erased line could not be erased again while remaining a line.");
        }

        var freeformScene = new VectorScene();
        freeformScene.CreateEmpty();
        var freeformStroke = VectorUnits.StrokePointsToUnits(4);
        freeformScene.AddFreehandStroke(
            0,
            [
                new PointF(-420, -80),
                new PointF(-260, 70),
                new PointF(-120, -60),
                PointF.Empty,
                new PointF(120, 60),
                new PointF(260, -70),
                new PointF(420, 80)
            ],
            freeformStroke,
            Color.MediumPurple,
            brushStroke: false,
            23);
        if (!freeformScene.EraseWithBrushStroke(0, center, diameter, shape, eraseLines: true, eraseFills: false)
            || freeformScene.ObjectCount != 2
            || Enumerable.Range(0, freeformScene.ObjectCount).Any(index =>
                freeformScene.ShapeKind[index] != ShapeKind.Freeform
                || freeformScene.Stroke[index] != freeformStroke
                || freeformScene.StrokeArgb[index] != Color.MediumPurple.ToArgb())
            || freeformScene.HitTestElement(PointF.Empty, 0, toleranceWorld: 0.1f).IsValid)
        {
            throw new InvalidOperationException("Brush eraser converted or lost editable freeform stroke topology.");
        }

        var outlinedRectangleScene = new VectorScene();
        outlinedRectangleScene.CreateEmpty(3);
        if (!outlinedRectangleScene.InsertTimelineBlankKeyframe(2, 9))
        {
            throw new InvalidOperationException("Brush eraser regression could not create an outlined-shape keyframe.");
        }

        outlinedRectangleScene.EditFrame = 9;
        var boundaryStroke = VectorUnits.StrokePointsToUnits(6);
        var boundaryEraserDiameter = VectorUnits.StrokePointsToUnits(4);
        var boundarySource = outlinedRectangleScene.AddObject(
            2,
            PointF.Empty,
            new SizeF(600, 400),
            0,
            boundaryStroke,
            Color.Coral,
            Color.Teal,
            60,
            ShapeKind.Rectangle);
        outlinedRectangleScene.ObjectSubOrder[boundarySource] = 0.375;
        var boundaryOrder = outlinedRectangleScene.ObjectOrder[boundarySource];
        var boundaryContact = new PointF(0, -200);
        if (!outlinedRectangleScene.EraseWithBrushStroke(
                9,
                [boundaryContact],
                boundaryEraserDiameter,
                shape,
                eraseLines: true,
                eraseFills: false))
        {
            throw new InvalidOperationException("Brush eraser did not remove the contacted outlined-rectangle boundary.");
        }

        var rectangleFill = Enumerable.Range(0, outlinedRectangleScene.ObjectCount)
            .SingleOrDefault(index => outlinedRectangleScene.HasFill(index));
        var rectangleLines = Enumerable.Range(0, outlinedRectangleScene.ObjectCount)
            .Where(index => outlinedRectangleScene.ShapeKind[index] == ShapeKind.Line)
            .ToArray();
        var rectangleRoundEndpoints = rectangleLines.Sum(index =>
            (outlinedRectangleScene.GetLineEndpointStyle(index, startEndpoint: true) == LineEndpointStyle.Round ? 1 : 0)
            + (outlinedRectangleScene.GetLineEndpointStyle(index, startEndpoint: false) == LineEndpointStyle.Round ? 1 : 0));
        var rectangleBoundaryPreserved = rectangleFill >= 0
            && outlinedRectangleScene.ObjectCount == 6
            && rectangleLines.Length == 5
            && outlinedRectangleScene.FillContainsPoint(rectangleFill, PointF.Empty)
            && outlinedRectangleScene.Argb[rectangleFill] == Color.Coral.ToArgb()
            && outlinedRectangleScene.Stroke[rectangleFill] == 0
            && outlinedRectangleScene.ObjectLayer[rectangleFill] == 2
            && outlinedRectangleScene.ObjectKeyframeFrame[rectangleFill] == 9
            && outlinedRectangleScene.ObjectOrder[rectangleFill] == boundaryOrder
            && outlinedRectangleScene.ObjectSubOrder[rectangleFill] == 0.375
            && rectangleLines.All(index => outlinedRectangleScene.ObjectLayer[index] == 2
                && outlinedRectangleScene.ObjectKeyframeFrame[index] == 9
                && outlinedRectangleScene.ObjectOrder[index] == boundaryOrder
                && outlinedRectangleScene.ObjectSubOrder[index] > outlinedRectangleScene.ObjectSubOrder[rectangleFill]
                && outlinedRectangleScene.Stroke[index] == boundaryStroke
                && outlinedRectangleScene.StrokeArgb[index] == Color.Teal.ToArgb())
            && rectangleRoundEndpoints == 2
            && !LineTouchesPoint(outlinedRectangleScene, boundaryContact)
            && LineTouchesPoint(outlinedRectangleScene, new PointF(0, 200))
            && !Enumerable.Range(0, outlinedRectangleScene.ObjectCount).Any(index =>
                outlinedRectangleScene.HasFill(index)
                && outlinedRectangleScene.Argb[index] == Color.Teal.ToArgb())
            && Enumerable.Range(0, outlinedRectangleScene.ObjectCount)
                .Sum(index => (long)outlinedRectangleScene.AtomCount[index]) == 60;
        if (!rectangleBoundaryPreserved)
        {
            throw new InvalidOperationException(
                "Brush eraser converted an outlined-rectangle boundary to fill or lost its fill, style, cel, order, endpoints, or atoms.");
        }

        var secondBoundaryContact = new PointF(0, 200);
        if (!outlinedRectangleScene.EraseWithBrushStroke(
                9,
                [secondBoundaryContact],
                boundaryEraserDiameter,
                shape,
                eraseLines: true,
                eraseFills: false)
            || Enumerable.Range(0, outlinedRectangleScene.ObjectCount).Any(index =>
                !outlinedRectangleScene.HasFill(index)
                && outlinedRectangleScene.ShapeKind[index] != ShapeKind.Line)
            || LineTouchesPoint(outlinedRectangleScene, secondBoundaryContact))
        {
            throw new InvalidOperationException(
                "A materialized outlined-shape boundary could not be erased again while remaining editable lines.");
        }

        var outlinedEllipseScene = new VectorScene();
        outlinedEllipseScene.CreateEmpty(2);
        var ellipseStroke = VectorUnits.StrokePointsToUnits(5);
        var ellipseSource = outlinedEllipseScene.AddObject(
            1,
            PointF.Empty,
            new SizeF(640, 360),
            0,
            ellipseStroke,
            Color.Gold,
            Color.RoyalBlue,
            72,
            ShapeKind.Ellipse);
        outlinedEllipseScene.ObjectSubOrder[ellipseSource] = 0.625;
        var ellipseOrder = outlinedEllipseScene.ObjectOrder[ellipseSource];
        var ellipseParts = outlinedEllipseScene.GetEditableFillBezierSegmentParts(ellipseSource);
        var ellipseContact = EvaluateCubic(
            ellipseParts[0].Start,
            ellipseParts[0].Control1,
            ellipseParts[0].Control2,
            ellipseParts[0].End,
            0.5f);
        if (!outlinedEllipseScene.EraseWithBrushStroke(
                0,
                [ellipseContact],
                boundaryEraserDiameter,
                shape,
                eraseLines: true,
                eraseFills: false))
        {
            throw new InvalidOperationException("Brush eraser did not remove the contacted curved boundary.");
        }

        var ellipseFill = Enumerable.Range(0, outlinedEllipseScene.ObjectCount)
            .SingleOrDefault(index => outlinedEllipseScene.HasFill(index));
        var ellipseLines = Enumerable.Range(0, outlinedEllipseScene.ObjectCount)
            .Where(index => outlinedEllipseScene.ShapeKind[index] == ShapeKind.Line)
            .ToArray();
        var ellipseBoundaryPreserved = ellipseFill >= 0
            && outlinedEllipseScene.ObjectCount == 6
            && ellipseLines.Length == 5
            && outlinedEllipseScene.FillContainsPoint(ellipseFill, PointF.Empty)
            && outlinedEllipseScene.TryGetPathBezierWorldContours(ellipseFill, out var ellipseContours)
            && ellipseContours.Length == 1
            && ellipseContours[0].Length == 4
            && ellipseLines.Any(index => !outlinedEllipseScene.IsLineStraight(index))
            && ellipseLines.All(index => outlinedEllipseScene.ObjectLayer[index] == 1
                && outlinedEllipseScene.ObjectKeyframeFrame[index] == 0
                && outlinedEllipseScene.ObjectOrder[index] == ellipseOrder
                && outlinedEllipseScene.ObjectSubOrder[index] > outlinedEllipseScene.ObjectSubOrder[ellipseFill]
                && outlinedEllipseScene.Stroke[index] == ellipseStroke
                && outlinedEllipseScene.StrokeArgb[index] == Color.RoyalBlue.ToArgb())
            && !LineTouchesPoint(outlinedEllipseScene, ellipseContact)
            && !Enumerable.Range(0, outlinedEllipseScene.ObjectCount).Any(index =>
                outlinedEllipseScene.HasFill(index)
                && outlinedEllipseScene.Argb[index] == Color.RoyalBlue.ToArgb())
            && Enumerable.Range(0, outlinedEllipseScene.ObjectCount)
                .Sum(index => (long)outlinedEllipseScene.AtomCount[index]) == 72;
        if (!ellipseBoundaryPreserved)
        {
            throw new InvalidOperationException(
                "Brush eraser flattened or converted an outlined ellipse boundary, or lost its exact fill and metadata.");
        }

        var targetStroke = VectorUnits.StrokePointsToUnits(8);
        var neitherScene = CreateTargetOptionScene(targetStroke);
        if (neitherScene.EraseWithBrushStroke(0, center, diameter, shape, eraseLines: false, eraseFills: false)
            || neitherScene.ObjectCount != 2
            || !neitherScene.FillContainsPoint(0, PointF.Empty)
            || !LineTouchesPoint(neitherScene, PointF.Empty))
        {
            throw new InvalidOperationException("Disabling both eraser targets modified the scene.");
        }

        var fillOnlyScene = CreateTargetOptionScene(targetStroke);
        if (!fillOnlyScene.EraseWithBrushStroke(0, center, diameter, shape, eraseLines: false, eraseFills: true)
            || Enumerable.Range(0, fillOnlyScene.ObjectCount).Any(index => fillOnlyScene.FillContainsPoint(index, PointF.Empty))
            || !LineTouchesPoint(fillOnlyScene, PointF.Empty))
        {
            throw new InvalidOperationException("Fill-only erasing did not preserve the contacted stroke.");
        }

        var strokeOnlyScene = CreateTargetOptionScene(targetStroke);
        if (!strokeOnlyScene.EraseWithBrushStroke(0, center, diameter, shape, eraseLines: true, eraseFills: false)
            || !Enumerable.Range(0, strokeOnlyScene.ObjectCount).Any(index => strokeOnlyScene.FillContainsPoint(index, PointF.Empty))
            || LineTouchesPoint(strokeOnlyScene, PointF.Empty)
            || Enumerable.Range(0, strokeOnlyScene.ObjectCount).Any(index =>
                strokeOnlyScene.Stroke[index] > 0 && strokeOnlyScene.ShapeKind[index] is not ShapeKind.Line))
        {
            throw new InvalidOperationException("Stroke-only erasing did not preserve the contacted fill or editable line type.");
        }

        var bothScene = CreateTargetOptionScene(targetStroke);
        if (!bothScene.EraseWithBrushStroke(0, center, diameter, shape, eraseLines: true, eraseFills: true)
            || Enumerable.Range(0, bothScene.ObjectCount).Any(index => bothScene.FillContainsPoint(index, PointF.Empty))
            || LineTouchesPoint(bothScene, PointF.Empty))
        {
            throw new InvalidOperationException("Combined stroke-and-fill erasing did not erase both selected targets.");
        }

        if (VectorUnits.MinimumStrokePoints != 0.1f
            || VectorUnits.MinimumStrokeUnits != VectorUnits.StrokePointsToUnits(0.1f))
        {
            throw new InvalidOperationException("The shared minimum stroke width is not 0.1 pt.");
        }

        using (var material = new MaterialEditorPanel())
        using (var brushTip = new BrushTipPanel())
        {
            var materialWidth = typeof(MaterialEditorPanel).GetField(
                "_strokeWidth",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(material)
                as ModernNumericUpDown;
            var brushSize = typeof(BrushTipPanel).GetField(
                "_size",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(brushTip)
                as ModernNumericUpDown;
            material.StrokeWidth = 0;
            brushTip.SetBrushStrokeSettings(8, continuous: true, sizePoints: 0, pressureSmoothing: 70);
            if (materialWidth is null
                || brushSize is null
                || materialWidth.Minimum != 0.1m
                || materialWidth.Increment != 0.1m
                || brushSize.Minimum != 0.1m
                || brushSize.Increment != 0.1m
                || material.StrokeWidth != 0.1f
                || brushTip.SizePoints != 0.1f)
            {
                throw new InvalidOperationException("Stroke-width controls did not clamp or step at 0.1 pt.");
            }
        }

        var minimumScene = new VectorScene();
        minimumScene.CreateEmpty();
        var minimumFreeform = minimumScene.AddFreehandStroke(
            0,
            [new PointF(-20, 0), new PointF(20, 0)],
            0,
            Color.White,
            brushStroke: false,
            4);
        using (var previewStage = new StageControl(minimumScene))
        {
            previewStage.SetFreehandPreview([PointF.Empty], Color.White, 0);
            if (minimumFreeform < 0
                || minimumScene.Stroke[minimumFreeform] != VectorUnits.MinimumStrokeUnits
                || previewStage.FreehandPreviewStroke != VectorUnits.MinimumStrokeUnits)
            {
                throw new InvalidOperationException("Freeform storage or preview did not preserve the 0.1 pt minimum stroke.");
            }
        }

        var minimumEraserScene = new VectorScene();
        minimumEraserScene.CreateEmpty();
        minimumEraserScene.AddLineSegment(
            0,
            new PointF(-20, 0),
            new PointF(20, 0),
            VectorUnits.MinimumStrokeUnits,
            Color.Transparent,
            Color.White,
            6);
        var minimumErased = minimumEraserScene.EraseWithBrushStroke(
            0,
            center,
            VectorUnits.MinimumStrokeUnits,
            shape,
            eraseLines: true,
            eraseFills: false);
        var minimumFragmentsValid = Enumerable.Range(0, minimumEraserScene.ObjectCount).All(index =>
            minimumEraserScene.ShapeKind[index] == ShapeKind.Line
            && minimumEraserScene.Stroke[index] == VectorUnits.MinimumStrokeUnits);
        var minimumCenterTouched = LineTouchesPoint(minimumEraserScene, PointF.Empty);
        if (!minimumErased
            || minimumEraserScene.ObjectCount != 2
            || !minimumFragmentsValid
            || minimumCenterTouched)
        {
            var minimumFragments = string.Join(",", Enumerable.Range(0, minimumEraserScene.ObjectCount)
                .Select(index => $"{minimumEraserScene.ShapeKind[index]}:{minimumEraserScene.Stroke[index]:0.###}"));
            throw new InvalidOperationException(
                "The 0.1 pt eraser path was discontinuous or converted thin line fragments: "
                + $"erased={minimumErased}, count={minimumEraserScene.ObjectCount}, fragments={minimumFragments}, "
                + $"centerTouched={minimumCenterTouched}.");
        }

        Console.WriteLine("brush_eraser_regression=ok");
    }

    private static void RunLineToFillConversionRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty(2);
        var preservedLine = scene.AddLineSegment(
            1,
            new PointF(-200, 0),
            new PointF(-100, 0),
            VectorUnits.StrokePointsToUnits(2),
            Color.Transparent,
            Color.Teal,
            6);
        if (!scene.InsertTimelineBlankKeyframe(1, 10))
        {
            throw new InvalidOperationException("Line-to-fill conversion regression could not create an independent cel.");
        }

        scene.EditFrame = 10;
        var source = scene.AddCurveSegment(
            1,
            new PointF(0, 0),
            new PointF(50, 60),
            new PointF(100, 0),
            VectorUnits.StrokePointsToUnits(4),
            Color.Transparent,
            Color.Coral,
            17);
        scene.ObjectSubOrder[source] = 0.375;
        var secondSource = scene.AddLineSegment(
            1,
            new PointF(0, 100),
            new PointF(100, 100),
            VectorUnits.StrokePointsToUnits(3),
            Color.Transparent,
            Color.Gold,
            13);
        scene.ObjectSubOrder[secondSource] = 0.625;
        var unrelated = scene.AddObject(0, new PointF(180, 0), new SizeF(40, 40), 0, 0, Color.Aqua, Color.Transparent, 6, ShapeKind.Rectangle);
        var sourceLayer = scene.ObjectLayer[source];
        var sourceKeyframe = scene.ObjectKeyframeFrame[source];
        var sourceOrder = scene.ObjectOrder[source];
        var sourceSubOrder = scene.ObjectSubOrder[source];
        var sourceAtoms = scene.AtomCount[source];
        var sourceStrokeColor = scene.StrokeArgb[source];
        var snapshot = scene.CreateSnapshot();

        if (!scene.CanConvertLineToFill(source, 10)
            || scene.CanConvertLineToFill(source, 0)
            || scene.CanConvertLineToFill(unrelated, 10)
            || !scene.TryConvertLineToFill(source, 10, out var fill))
        {
            throw new InvalidOperationException("A visible line could not be converted to a fill.");
        }

        if (scene.ObjectCount != 4
            || scene.ShapeKind[fill] != ShapeKind.Path
            || scene.ObjectLayer[fill] != sourceLayer
            || scene.ObjectKeyframeFrame[fill] != sourceKeyframe
            || scene.ObjectOrder[fill] != sourceOrder
            || scene.ObjectSubOrder[fill] != sourceSubOrder
            || scene.AtomCount[fill] != sourceAtoms
            || scene.Argb[fill] != sourceStrokeColor
            || scene.Stroke[fill] != 0
            || scene.StrokeArgb[fill] != Color.Transparent.ToArgb()
            || !scene.TryGetPathWorldContours(fill, out var contours)
            || contours.Length == 0
            || !scene.FillContainsPoint(fill, new PointF(50, 30))
            || Enumerable.Range(0, scene.ObjectCount).Count(index => scene.ShapeKind[index] == ShapeKind.Line) != 2
            || Enumerable.Range(0, scene.ObjectCount).Count(index => scene.ShapeKind[index] == ShapeKind.Rectangle) != 1)
        {
            throw new InvalidOperationException("Line-to-fill conversion did not preserve the source cel, style, order, or outline geometry.");
        }

        scene.RestoreSnapshot(snapshot);
        if (scene.ObjectCount != 4
            || scene.ShapeKind[source] != ShapeKind.Line
            || scene.ShapeKind[secondSource] != ShapeKind.Line
            || !scene.IsObjectActive(source, 10)
            || scene.IsObjectActive(source, 0)
            || !scene.CanConvertLineToFill(source, 10))
        {
            throw new InvalidOperationException("Line-to-fill conversion snapshot restoration did not recover the source line.");
        }

        var batchSources = new[] { source, secondSource };
        var batchSourceProperties = batchSources
            .Select(index => (
                Layer: scene.ObjectLayer[index],
                KeyframeFrame: scene.ObjectKeyframeFrame[index],
                Order: scene.ObjectOrder[index],
                SubOrder: scene.ObjectSubOrder[index],
                Atoms: scene.AtomCount[index],
                StrokeColor: scene.StrokeArgb[index]))
            .ToArray();
        var batchFills = new List<int>(batchSources.Length);
        foreach (var batchSource in batchSources)
        {
            if (scene.TryConvertLineToFill(batchSource, 10, out var batchFill))
            {
                batchFills.Add(batchFill);
                continue;
            }

            scene.RestoreSnapshot(snapshot);
            throw new InvalidOperationException("Multiple selected lines could not be converted to fills as one operation.");
        }

        if (batchFills.Count != batchSources.Length
            || scene.ObjectCount != 4
            || Enumerable.Range(0, scene.ObjectCount).Count(index => scene.ShapeKind[index] == ShapeKind.Path) != 2
            || Enumerable.Range(0, scene.ObjectCount).Any(index => scene.ShapeKind[index] == ShapeKind.Line && scene.IsObjectActive(index, 10)))
        {
            throw new InvalidOperationException("Multiple selected lines could not be converted to fills as one operation.");
        }

        for (var index = 0; index < batchFills.Count; index++)
        {
            var fillIndex = batchFills[index];
            var sourceProperties = batchSourceProperties[index];
            if ((uint)fillIndex >= scene.ObjectCount
                || scene.ShapeKind[fillIndex] != ShapeKind.Path
                || scene.ObjectLayer[fillIndex] != sourceProperties.Layer
                || scene.ObjectKeyframeFrame[fillIndex] != sourceProperties.KeyframeFrame
                || scene.ObjectOrder[fillIndex] != sourceProperties.Order
                || scene.ObjectSubOrder[fillIndex] != sourceProperties.SubOrder
                || scene.AtomCount[fillIndex] != sourceProperties.Atoms
                || scene.Argb[fillIndex] != sourceProperties.StrokeColor
                || scene.Stroke[fillIndex] != 0
                || scene.StrokeArgb[fillIndex] != Color.Transparent.ToArgb())
            {
                throw new InvalidOperationException("Multiple line-to-fill conversion did not preserve source metadata.");
            }
        }

        if (!scene.FillContainsPoint(batchFills[0], new PointF(50, 30))
            || !scene.FillContainsPoint(batchFills[1], new PointF(50, 100)))
        {
            throw new InvalidOperationException("Multiple line-to-fill conversion did not retain each stroke outline.");
        }

        Console.WriteLine("line_to_fill_conversion_regression=ok");
    }

    private static void RunConnectedLineBranchRegression()
    {
        var stroke = VectorUnits.StrokePointsToUnits(2.5f);
        var endpointScene = new VectorScene();
        endpointScene.CreateEmpty(layers: 2);
        var source = endpointScene.AddLineSegment(
            1,
            PointF.Empty,
            new PointF(200, 0),
            stroke,
            Color.FromArgb(96, 20, 40, 60),
            Color.FromArgb(180, 220, 80, 40),
            6,
            LineEndpointStyle.Sharp,
            LineEndpointStyle.Round);
        var gradientStops = new[]
        {
            new GradientStop(0, Color.FromArgb(180, 240, 20, 10)),
            new GradientStop(0.45f, Color.FromArgb(150, 30, 220, 80)),
            new GradientStop(1, Color.FromArgb(120, 20, 60, 240))
        };
        var gradientStart = new PointF(-20, -10);
        var gradientEnd = new PointF(220, 10);
        endpointScene.SetGradientPaint(source, GradientKind.Linear, gradientStops, gradientStart, gradientEnd);
        var sourceLayer = endpointScene.ObjectLayer[source];
        var sourceKeyframe = endpointScene.ObjectKeyframeFrame[source];
        var sourceOrder = endpointScene.ObjectOrder[source];
        var sourceFillArgb = endpointScene.Argb[source];
        var sourceStrokeArgb = endpointScene.StrokeArgb[source];

        if (!endpointScene.AddConnectedLineBranch(
                source,
                0,
                new PointF(-80, 80),
                out var endpointBranch)
            || endpointScene.ObjectCount != 2
            || !endpointBranch.SourceObjectIndices.SequenceEqual([source])
            || !PointsNear(endpointBranch.Anchor, PointF.Empty)
            || !endpointScene.TryGetLineEndpoint(endpointBranch.BranchObjectIndex, true, out var endpointBranchStart)
            || !endpointScene.TryGetLineEndpoint(endpointBranch.BranchObjectIndex, false, out var endpointBranchEnd)
            || !PointsNear(endpointBranchStart, PointF.Empty)
            || !PointsNear(endpointBranchEnd, new PointF(-80, 80))
            || endpointScene.ObjectLayer[endpointBranch.BranchObjectIndex] != sourceLayer
            || endpointScene.ObjectKeyframeFrame[endpointBranch.BranchObjectIndex] != sourceKeyframe
            || endpointScene.ObjectOrder[endpointBranch.BranchObjectIndex] != sourceOrder
            || endpointScene.Stroke[endpointBranch.BranchObjectIndex] != stroke
            || endpointScene.Argb[endpointBranch.BranchObjectIndex] != sourceFillArgb
            || endpointScene.StrokeArgb[endpointBranch.BranchObjectIndex] != sourceStrokeArgb
            || endpointScene.GetLineEndpointStyle(endpointBranch.BranchObjectIndex, true) != LineEndpointStyle.Round
            || endpointScene.GetLineEndpointStyle(endpointBranch.BranchObjectIndex, false) != LineEndpointStyle.Sharp
            || endpointScene.GetGradientKind(endpointBranch.BranchObjectIndex) != GradientKind.Linear
            || !endpointScene.GetGradientStops(endpointBranch.BranchObjectIndex).SequenceEqual(gradientStops)
            || !PointsNear(endpointScene.GetGradientStart(endpointBranch.BranchObjectIndex), gradientStart)
            || !PointsNear(endpointScene.GetGradientEnd(endpointBranch.BranchObjectIndex), gradientEnd))
        {
            throw new InvalidOperationException("An endpoint line branch did not preserve its connection, material, or timeline ownership.");
        }

        var curvedScene = new VectorScene();
        curvedScene.CreateEmpty();
        var curve = curvedScene.AddCubicCurveSegment(
            0,
            new PointF(0, 0),
            new PointF(60, 120),
            new PointF(140, 120),
            new PointF(200, 0),
            stroke,
            Color.Transparent,
            Color.Coral,
            12,
            LineEndpointStyle.Sharp,
            LineEndpointStyle.Sharp);
        var expectedAnchor = new PointF(100, 90);
        if (!curvedScene.AddConnectedLineBranch(
                curve,
                0.5f,
                new PointF(100, 190),
                out var interiorBranch)
            || curvedScene.ObjectCount != 3
            || interiorBranch.SourceObjectIndices.Length != 2
            || !PointsNear(interiorBranch.Anchor, expectedAnchor)
            || !curvedScene.TryGetLineCubic(
                interiorBranch.SourceObjectIndices[0],
                out var firstStart,
                out var firstControl1,
                out var firstControl2,
                out var firstEnd)
            || !PointsWithin(firstStart, new PointF(0, 0), 0.75f)
            || !PointsNear(firstControl1, new PointF(30, 60))
            || !PointsNear(firstControl2, new PointF(65, 90))
            || !PointsWithin(firstEnd, expectedAnchor, 0.75f)
            || !curvedScene.TryGetLineCubic(
                interiorBranch.SourceObjectIndices[1],
                out var secondStart,
                out var secondControl1,
                out var secondControl2,
                out var secondEnd)
            || !PointsWithin(secondStart, expectedAnchor, 0.75f)
            || !PointsNear(secondControl1, new PointF(135, 90))
            || !PointsNear(secondControl2, new PointF(170, 60))
            || !PointsWithin(secondEnd, new PointF(200, 0), 0.75f)
            || !curvedScene.TryGetLineEndpoint(interiorBranch.BranchObjectIndex, true, out var interiorBranchStart)
            || !curvedScene.TryGetLineEndpoint(interiorBranch.BranchObjectIndex, false, out var interiorBranchEnd)
            || !PointsNear(interiorBranchStart, expectedAnchor)
            || !PointsNear(interiorBranchEnd, new PointF(100, 190)))
        {
            throw new InvalidOperationException("An interior line branch did not preserve the source cubic through an exact split.");
        }

        var branchHit = new DrawingElementHit(
            new DrawingElementKey(interiorBranch.BranchObjectIndex, DrawingElementKind.Stroke, 0),
            0,
            0,
            1);
        var connectedObjects = curvedScene.GetConnectedStrokeElements(branchHit, 0)
            .Select(connected => connected.Key.ObjectIndex)
            .Distinct()
            .ToHashSet();
        if (!interiorBranch.SourceObjectIndices.All(connectedObjects.Contains)
            || !connectedObjects.Contains(interiorBranch.BranchObjectIndex))
        {
            throw new InvalidOperationException("An interior line branch was not connected to both split source segments.");
        }

        if (!MainForm.IsLineEndpointBranchGesture(MouseButtons.Left, true, 4, 5)
            || !MainForm.IsLineEndpointBranchGesture(MouseButtons.Left, true, 0, 5)
            || MainForm.IsLineEndpointBranchGesture(MouseButtons.Left, false, 0, 5)
            || MainForm.IsLineEndpointBranchGesture(MouseButtons.Right, true, 0, 5)
            || MainForm.IsLineEndpointBranchGesture(MouseButtons.Left, true, 6, 5)
            || MainForm.IsLineEndpointBranchGesture(MouseButtons.Left, true, float.PositiveInfinity, 5)
            || !MainForm.IsLineInteriorBranchGesture(MouseButtons.Left, true, true, 0.5f)
            || MainForm.IsLineInteriorBranchGesture(MouseButtons.Left, true, false, 0.5f)
            || MainForm.IsLineInteriorBranchGesture(MouseButtons.Left, false, true, 0.5f)
            || MainForm.IsLineInteriorBranchGesture(MouseButtons.Left, true, true, 0.02f)
            || MainForm.IsLineInteriorBranchGesture(MouseButtons.Left, true, true, 0.98f)
            || MainForm.ShouldCommitLineBranchDrag(MouseButtons.Left, false, 100)
            || MainForm.ShouldCommitLineBranchDrag(MouseButtons.Left, true, 0.5f)
            || !MainForm.ShouldCommitLineBranchDrag(MouseButtons.Left, true, 100))
        {
            throw new InvalidOperationException("Line branch gesture modifiers or drag commitment thresholds regressed.");
        }

        Console.WriteLine("connected_line_branch_regression=ok");
    }

    private static void RunLineSegmentMergeRegression()
    {
        var stroke = VectorUnits.StrokePointsToUnits(2);
        var endpointStyleScene = new VectorScene();
        endpointStyleScene.CreateEmpty();
        endpointStyleScene.AddLineSegment(
            0,
            new PointF(0, 0),
            new PointF(100, 0),
            stroke,
            Color.Transparent,
            Color.Coral,
            6,
            LineEndpointStyle.Sharp);
        endpointStyleScene.AddLineSegment(
            0,
            new PointF(104, 0),
            new PointF(204, 0),
            stroke,
            Color.Transparent,
            Color.Coral,
            6,
            LineEndpointStyle.Sharp);
        if (!endpointStyleScene.MergeCompatibleLineSegments(0).Changed
            || endpointStyleScene.ObjectCount != 1
            || endpointStyleScene.GetLineEndpointStyle(0) != LineEndpointStyle.Sharp)
        {
            throw new InvalidOperationException("Merged sharp-end lines did not retain their endpoint style.");
        }

        var independentEndpointScene = new VectorScene();
        independentEndpointScene.CreateEmpty();
        independentEndpointScene.AddLineSegment(
            0,
            new PointF(0, 0),
            new PointF(100, 0),
            stroke,
            Color.Transparent,
            Color.Coral,
            6,
            LineEndpointStyle.Sharp,
            LineEndpointStyle.Round);
        var independentEndpointSnapshot = independentEndpointScene.CreateSnapshot();
        independentEndpointScene.RestoreSnapshot(independentEndpointSnapshot);
        if (independentEndpointScene.GetLineEndpointStyle(0, startEndpoint: true) != LineEndpointStyle.Sharp
            || independentEndpointScene.GetLineEndpointStyle(0, startEndpoint: false) != LineEndpointStyle.Round)
        {
            throw new InvalidOperationException("Independent start and end line styles were not preserved by snapshot restoration.");
        }

        var boundaryEndpointScene = new VectorScene();
        boundaryEndpointScene.CreateEmpty();
        var outlinedRectangle = boundaryEndpointScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(240, 160),
            0,
            stroke,
            Color.CornflowerBlue,
            Color.Coral,
            12,
            ShapeKind.Rectangle);
        var boundaryPart = boundaryEndpointScene.GetBoundaryParts(outlinedRectangle, 0)
            .First(part => part.Points.Length == 2);
        var boundaryKey = new DrawingElementKey(
            outlinedRectangle,
            DrawingElementKind.BoundaryStroke,
            boundaryPart.PartIndex);
        var boundarySnapshot = boundaryEndpointScene.CreateSnapshot();
        if (!boundaryEndpointScene.TryGetLinePartEndpointStyles(boundaryKey, 0, out var boundaryStartStyle, out var boundaryEndStyle)
            || boundaryStartStyle != LineEndpointStyle.Round
            || boundaryEndStyle != LineEndpointStyle.Round)
        {
            throw new InvalidOperationException("A selected fill-boundary segment did not expose editable line endpoint styles.");
        }

        var materializedBoundary = boundaryEndpointScene.MaterializeSelectedParts([boundaryKey], 0);
        var materializedBoundaryLine = materializedBoundary.Parts.FirstOrDefault().Result.ObjectIndex;
        if (!materializedBoundary.Success
            || !materializedBoundary.Changed
            || materializedBoundary.Parts.Length != 1
            || materializedBoundaryLine < 0
            || boundaryEndpointScene.ShapeKind[materializedBoundaryLine] != ShapeKind.Line
            || !boundaryEndpointScene.SetLineEndpointStyle(materializedBoundaryLine, startEndpoint: true, LineEndpointStyle.Sharp)
            || boundaryEndpointScene.GetLineEndpointStyle(materializedBoundaryLine, startEndpoint: true) != LineEndpointStyle.Sharp
            || Enumerable.Range(0, boundaryEndpointScene.ObjectCount)
                .Where(index => index != materializedBoundaryLine && boundaryEndpointScene.ShapeKind[index] == ShapeKind.Line)
                .Any(index => boundaryEndpointScene.GetLineEndpointStyle(index, startEndpoint: true) != LineEndpointStyle.Round
                    || boundaryEndpointScene.GetLineEndpointStyle(index, startEndpoint: false) != LineEndpointStyle.Round))
        {
            throw new InvalidOperationException("Editing a fill-boundary endpoint style did not isolate the selected segment as a Line.");
        }

        boundaryEndpointScene.RestoreSnapshot(boundarySnapshot);
        if (boundaryEndpointScene.ObjectCount != 1
            || boundaryEndpointScene.ShapeKind[outlinedRectangle] != ShapeKind.Rectangle
            || boundaryEndpointScene.Stroke[outlinedRectangle] != stroke)
        {
            throw new InvalidOperationException("Restoring a fill-boundary endpoint edit did not recover the original outlined fill.");
        }

        var mixedEndpointStyleScene = new VectorScene();
        mixedEndpointStyleScene.CreateEmpty();
        mixedEndpointStyleScene.AddLineSegment(
            0,
            new PointF(0, 0),
            new PointF(100, 0),
            stroke,
            Color.Transparent,
            Color.Coral,
            6,
            LineEndpointStyle.Sharp);
        mixedEndpointStyleScene.AddLineSegment(
            0,
            new PointF(104, 0),
            new PointF(204, 0),
            stroke,
            Color.Transparent,
            Color.Coral,
            6,
            LineEndpointStyle.Round);
        var mixedStyleSnapshot = mixedEndpointStyleScene.CreateSnapshot();
        mixedEndpointStyleScene.RestoreSnapshot(mixedStyleSnapshot);
        if (mixedEndpointStyleScene.GetLineEndpointStyle(0) != LineEndpointStyle.Sharp
            || mixedEndpointStyleScene.GetLineEndpointStyle(1) != LineEndpointStyle.Round
            || mixedEndpointStyleScene.MergeCompatibleLineSegments(0).Changed)
        {
            throw new InvalidOperationException("Different line endpoint styles were not preserved or were merged together.");
        }

        var miterJoinScene = new VectorScene();
        miterJoinScene.CreateEmpty();
        var miterFirst = miterJoinScene.AddLineSegment(
            0,
            new PointF(-100, 0),
            PointF.Empty,
            stroke,
            Color.Transparent,
            Color.Coral,
            6,
            LineEndpointStyle.Sharp);
        var miterSecond = miterJoinScene.AddLineSegment(
            0,
            PointF.Empty,
            new PointF(0, 100),
            stroke,
            Color.Transparent,
            Color.Coral,
            6,
            LineEndpointStyle.Round);
        if (!miterJoinScene.TryGetLineJoinNeighbor(miterFirst, startEndpoint: false, 0, out var joinNeighbor, out var neighborStarts)
            || joinNeighbor != miterSecond
            || !neighborStarts
            || !LineJoinGeometry.TryCreateMiter(PointF.Empty, new PointF(-100, 0), new PointF(0, 100), 10, out var miter)
            || Math.Abs(miter.OuterMiter.X - 10) > 0.001f
            || Math.Abs(miter.OuterMiter.Y + 10) > 0.001f
            || Math.Abs(miter.InnerMiter.X + 10) > 0.001f
            || Math.Abs(miter.InnerMiter.Y - 10) > 0.001f)
        {
            throw new InvalidOperationException("Sharp line joins did not resolve the width-offset miter intersection.");
        }

        var junctionScene = new VectorScene();
        junctionScene.CreateEmpty();
        var junctionFirst = junctionScene.AddLineSegment(
            0,
            new PointF(-400, -100),
            PointF.Empty,
            stroke,
            Color.Transparent,
            Color.Coral,
            6,
            LineEndpointStyle.Sharp);
        var junctionSecond = junctionScene.AddLineSegment(
            0,
            PointF.Empty,
            new PointF(-400, 0),
            stroke,
            Color.Transparent,
            Color.Coral,
            6,
            LineEndpointStyle.Sharp);
        var junctionThird = junctionScene.AddLineSegment(
            0,
            PointF.Empty,
            new PointF(-400, 100),
            stroke,
            Color.Transparent,
            Color.Coral,
            6,
            LineEndpointStyle.Sharp);
        if (!junctionScene.TryGetLineEndpointJunction(junctionFirst, startEndpoint: false, 0, out var junction)
            || junction.NeighborCount != 2
            || junction.OwnerObjectIndex != Math.Min(junctionFirst, Math.Min(junctionSecond, junctionThird))
            || !junction.AllSharp
            || junctionScene.TryGetLineJoinNeighbor(junctionFirst, startEndpoint: false, 0, out _, out _))
        {
            throw new InvalidOperationException("Multi-line sharp junctions did not expose all connected endpoint directions.");
        }
        if (!junctionScene.TryGetLineEndpointJunctionForRender(junctionThird, startEndpoint: true, 0, out var renderJunction)
            || renderJunction.OwnerObjectIndex >= junctionThird
            || renderJunction.Connections.Length != 0)
        {
            throw new InvalidOperationException("Non-owner sharp junction rendering did not stop after resolving an earlier owner.");
        }

        var wholeLineHit = new DrawingElementHit(
            new DrawingElementKey(junctionFirst, DrawingElementKind.Stroke, 0),
            0,
            0,
            1);
        var splitLineHit = wholeLineHit with { StartT = 0.25f, EndT = 1 };
        if (!MainForm.IsWholeLineEndpointEditHit(junctionScene, wholeLineHit)
            || MainForm.IsWholeLineEndpointEditHit(junctionScene, splitLineHit))
        {
            throw new InvalidOperationException("Whole-line endpoint edits did not bypass only redundant topology materialization.");
        }

        var junctionMiters = LineJoinGeometry.CreateJunctionMiters(
            PointF.Empty,
            [new PointF(-400, -100), new PointF(-400, 0), new PointF(-400, 100)],
            stroke * 0.5f);
        var sharpExtension = junctionMiters
            .SelectMany(miter => new[] { miter.OuterMiter.X, miter.InnerMiter.X })
            .DefaultIfEmpty(0)
            .Max();
        if (junctionMiters.Length != 1 || sharpExtension <= stroke * 0.625f)
        {
            throw new InvalidOperationException("Multi-line sharp junctions did not extend a pointed outer miter beyond the endpoint.");
        }

        var reverseScene = new VectorScene();
        reverseScene.CreateEmpty();
        var first = reverseScene.AddLineSegment(0, new PointF(0, 0), new PointF(100, 0), stroke, Color.Transparent, Color.Coral, 6);
        var reversed = reverseScene.AddLineSegment(0, new PointF(204, 0), new PointF(104, 0), stroke, Color.Transparent, Color.Coral, 6);
        var reverseMerge = reverseScene.MergeCompatibleLineSegments(0);
        if (!reverseMerge.Changed
            || reverseMerge.MergeCount != 1
            || reverseScene.ObjectCount != 1
            || reverseMerge.OldToNewObjectIndex[first] != reverseMerge.OldToNewObjectIndex[reversed]
            || !LineMatches(reverseScene, 0, new PointF(0, 0), new PointF(204, 0)))
        {
            throw new InvalidOperationException("Reverse-oriented line endpoints under 5 vu did not merge into one straight segment.");
        }

        var quantizedStraightScene = new VectorScene();
        quantizedStraightScene.CreateEmpty();
        quantizedStraightScene.AddLineSegment(0, new PointF(0, 0), new PointF(101, 0), stroke, Color.Transparent, Color.Coral, 6);
        quantizedStraightScene.AddLineSegment(0, new PointF(105, 0), new PointF(205, 0), stroke, Color.Transparent, Color.Coral, 6);
        if (!quantizedStraightScene.MergeCompatibleLineSegments(0).Changed
            || quantizedStraightScene.ObjectCount != 1
            || !quantizedStraightScene.IsLineStraight(0)
            || !quantizedStraightScene.TryGetLineEndpoint(0, startEndpoint: true, out var quantizedStart)
            || !quantizedStraightScene.TryGetLineEndpoint(0, startEndpoint: false, out var quantizedEnd)
            || quantizedStart.X > 1
            || quantizedEnd.X < 204)
        {
            throw new InvalidOperationException("A quantized odd-length straight segment did not merge despite zero curvature and endpoints under 5 vu apart.");
        }

        var marqueeSplitScene = new VectorScene();
        marqueeSplitScene.CreateEmpty();
        marqueeSplitScene.AddLineSegment(0, new PointF(-200, 0), new PointF(200, 0), stroke, Color.Transparent, Color.Coral, 6);
        var marqueeMaterialization = marqueeSplitScene.MaterializeMarqueeLineParts(new RectangleF(-20, -40, 40, 80), 0);
        if (!marqueeMaterialization.Changed
            || marqueeSplitScene.ObjectCount < 2
            || !marqueeSplitScene.MergeCompatibleLineSegments(0).Changed
            || marqueeSplitScene.ObjectCount != 1
            || !LineMatches(marqueeSplitScene, 0, new PointF(-200, 0), new PointF(200, 0)))
        {
            throw new InvalidOperationException("A marquee-materialized straight line did not restore to one compatible segment.");
        }

        var scopedMarqueeScene = new VectorScene();
        scopedMarqueeScene.CreateEmpty();
        scopedMarqueeScene.AddLineSegment(0, new PointF(-200, 0), new PointF(200, 0), stroke, Color.Transparent, Color.Coral, 6);
        var scopedMarqueeMaterialization = scopedMarqueeScene.MaterializeMarqueeLineParts(new RectangleF(-20, -40, 40, 80), 0);
        var selectedMarqueeSegment = scopedMarqueeMaterialization.SelectedObjects.Single();
        var siblingScope = Enumerable.Range(0, scopedMarqueeScene.ObjectCount)
            .Where(index => scopedMarqueeScene.ShapeKind[index] == ShapeKind.Line
                && scopedMarqueeScene.ObjectOrder[index] == scopedMarqueeScene.ObjectOrder[selectedMarqueeSegment])
            .ToArray();
        if (!scopedMarqueeMaterialization.Changed
            || siblingScope.Length < 2
            || !scopedMarqueeScene.MergeCompatibleLineSegments(0, siblingScope).Changed
            || scopedMarqueeScene.ObjectCount != 1
            || !LineMatches(scopedMarqueeScene, 0, new PointF(-200, 0), new PointF(200, 0)))
        {
            throw new InvalidOperationException("A selected marquee segment did not simplify with all of its materialized line siblings.");
        }

        var scopedMergeScene = new VectorScene();
        scopedMergeScene.CreateEmpty();
        var scopedFirst = scopedMergeScene.AddLineSegment(0, new PointF(0, 0), new PointF(100, 0), stroke, Color.Transparent, Color.Coral, 6);
        var scopedSecond = scopedMergeScene.AddLineSegment(0, new PointF(104, 0), new PointF(204, 0), stroke, Color.Transparent, Color.Coral, 6);
        scopedMergeScene.AddLineSegment(0, new PointF(0, 100), new PointF(100, 100), stroke, Color.Transparent, Color.Coral, 6);
        scopedMergeScene.AddLineSegment(0, new PointF(104, 100), new PointF(204, 100), stroke, Color.Transparent, Color.Coral, 6);
        if (!scopedMergeScene.MergeCompatibleLineSegments(0, new[] { scopedFirst, scopedSecond }).Changed
            || scopedMergeScene.ObjectCount != 3
            || !Enumerable.Range(0, scopedMergeScene.ObjectCount).Any(index => LineMatches(scopedMergeScene, index, new PointF(0, 100), new PointF(100, 100)))
            || !Enumerable.Range(0, scopedMergeScene.ObjectCount).Any(index => LineMatches(scopedMergeScene, index, new PointF(104, 100), new PointF(204, 100))))
        {
            throw new InvalidOperationException("Scoped line merge changed segments outside its requested line chain.");
        }

        var exactThresholdScene = new VectorScene();
        exactThresholdScene.CreateEmpty();
        AddTopologyLine(exactThresholdScene, 0, new PointF(0, 0), new PointF(100, 0));
        AddTopologyLine(exactThresholdScene, 0, new PointF(105, 0), new PointF(205, 0));
        if (exactThresholdScene.MergeCompatibleLineSegments(0).Changed || exactThresholdScene.ObjectCount != 2)
        {
            throw new InvalidOperationException("Line endpoints exactly 5 vu apart were merged despite the strict distance boundary.");
        }

        var offsetParallelScene = new VectorScene();
        offsetParallelScene.CreateEmpty();
        AddTopologyLine(offsetParallelScene, 0, new PointF(0, 0), new PointF(100, 0));
        AddTopologyLine(offsetParallelScene, 0, new PointF(100, 4), new PointF(200, 4));
        if (offsetParallelScene.MergeCompatibleLineSegments(0).Changed || offsetParallelScene.ObjectCount != 2)
        {
            throw new InvalidOperationException("Parallel but non-collinear line segments were merged into distorted geometry.");
        }

        var accumulatedOffsetScene = new VectorScene();
        accumulatedOffsetScene.CreateEmpty();
        AddTopologyLine(accumulatedOffsetScene, 0, new PointF(0, 0), new PointF(100, 0));
        AddTopologyLine(accumulatedOffsetScene, 0, new PointF(104, 1), new PointF(204, 1));
        AddTopologyLine(accumulatedOffsetScene, 0, new PointF(208, 2), new PointF(308, 2));
        if (accumulatedOffsetScene.MergeCompatibleLineSegments(0).Changed || accumulatedOffsetScene.ObjectCount != 3)
        {
            throw new InvalidOperationException("Line merge accumulated a one-unit parallel offset through a transitive group.");
        }

        var nearToleranceChainScene = new VectorScene();
        nearToleranceChainScene.CreateEmpty();
        var nearToleranceOffset = DrawingTopologyRules.UnitIntersectionTolerance * 0.75f;
        AddTopologyLine(nearToleranceChainScene, 0, new PointF(0, 0), new PointF(100, 0));
        var nearMiddle = AddTopologyLine(nearToleranceChainScene, 0, new PointF(104, 0), new PointF(204, 0));
        var nearEnd = AddTopologyLine(nearToleranceChainScene, 0, new PointF(208, 0), new PointF(308, 0));
        // Keep sub-vu offsets that AddLineSegment normally quantizes so the transitive grouping path is exercised.
        nearToleranceChainScene.Y[nearMiddle] = nearToleranceOffset;
        nearToleranceChainScene.CurveControlY[nearMiddle] = nearToleranceOffset;
        nearToleranceChainScene.CurveControl2Y[nearMiddle] = nearToleranceOffset;
        nearToleranceChainScene.Y[nearEnd] = nearToleranceOffset * 2;
        nearToleranceChainScene.CurveControlY[nearEnd] = nearToleranceOffset * 2;
        nearToleranceChainScene.CurveControl2Y[nearEnd] = nearToleranceOffset * 2;
        if (nearToleranceChainScene.MergeCompatibleLineSegments(0).Changed || nearToleranceChainScene.ObjectCount != 3)
        {
            throw new InvalidOperationException("Line merge accumulated near-tolerance offsets through a transitive group.");
        }

        var directionScene = new VectorScene();
        directionScene.CreateEmpty();
        AddTopologyLine(directionScene, 0, new PointF(0, 0), new PointF(100, 0));
        AddTopologyLine(directionScene, 0, new PointF(100, 4), new PointF(100, 104));
        if (directionScene.MergeCompatibleLineSegments(0).Changed || directionScene.ObjectCount != 2)
        {
            throw new InvalidOperationException("Line segments with different directions were merged.");
        }

        var colorScene = new VectorScene();
        colorScene.CreateEmpty();
        colorScene.AddLineSegment(0, new PointF(0, 0), new PointF(100, 0), stroke, Color.Transparent, Color.Coral, 6);
        colorScene.AddLineSegment(0, new PointF(104, 0), new PointF(204, 0), stroke, Color.Transparent, Color.Aqua, 6);
        if (colorScene.MergeCompatibleLineSegments(0).Changed || colorScene.ObjectCount != 2)
        {
            throw new InvalidOperationException("Lines with different stroke ARGB values were merged.");
        }

        var fillColorScene = new VectorScene();
        fillColorScene.CreateEmpty();
        fillColorScene.AddLineSegment(0, new PointF(0, 0), new PointF(100, 0), stroke, Color.Coral, Color.White, 6);
        fillColorScene.AddLineSegment(0, new PointF(104, 0), new PointF(204, 0), stroke, Color.Aqua, Color.White, 6);
        if (fillColorScene.MergeCompatibleLineSegments(0).Changed || fillColorScene.ObjectCount != 2)
        {
            throw new InvalidOperationException("Lines with different fill ARGB values were merged.");
        }

        var widthScene = new VectorScene();
        widthScene.CreateEmpty();
        widthScene.AddLineSegment(0, new PointF(0, 0), new PointF(100, 0), stroke, Color.Transparent, Color.Coral, 6);
        widthScene.AddLineSegment(0, new PointF(104, 0), new PointF(204, 0), stroke + 1, Color.Transparent, Color.Coral, 6);
        if (widthScene.MergeCompatibleLineSegments(0).Changed || widthScene.ObjectCount != 2)
        {
            throw new InvalidOperationException("Lines with different stroke widths were merged.");
        }

        var curvedScene = new VectorScene();
        curvedScene.CreateEmpty();
        curvedScene.AddLineSegment(0, new PointF(0, 0), new PointF(100, 0), stroke, Color.Transparent, Color.Coral, 6);
        curvedScene.AddCurveSegment(0, new PointF(104, 0), new PointF(154, 40), new PointF(204, 0), stroke, Color.Transparent, Color.Coral, 6);
        if (curvedScene.MergeCompatibleLineSegments(0).Changed || curvedScene.ObjectCount != 2)
        {
            throw new InvalidOperationException("A curved line was treated as a mergeable straight segment.");
        }

        var layerScene = new VectorScene();
        layerScene.CreateEmpty(2);
        layerScene.AddLineSegment(0, new PointF(0, 0), new PointF(100, 0), stroke, Color.Transparent, Color.Coral, 6);
        layerScene.AddLineSegment(1, new PointF(104, 0), new PointF(204, 0), stroke, Color.Transparent, Color.Coral, 6);
        if (layerScene.MergeCompatibleLineSegments(0).Changed || layerScene.ObjectCount != 2)
        {
            throw new InvalidOperationException("Compatible lines on different layers were merged.");
        }

        var frameScene = new VectorScene();
        frameScene.CreateEmpty();
        frameScene.AddLineSegment(0, new PointF(0, 0), new PointF(100, 0), stroke, Color.Transparent, Color.Coral, 6);
        if (!frameScene.InsertTimelineBlankKeyframe(0, 10))
        {
            throw new InvalidOperationException("Line merge frame regression could not create an independent blank cel.");
        }

        frameScene.EditFrame = 10;
        frameScene.AddLineSegment(0, new PointF(104, 0), new PointF(204, 0), stroke, Color.Transparent, Color.Coral, 6);
        if (frameScene.MergeCompatibleLineSegments(0).Changed
            || frameScene.MergeCompatibleLineSegments(10).Changed
            || frameScene.ObjectCount != 2)
        {
            throw new InvalidOperationException("Line merge crossed active keyframe ownership.");
        }

        Console.WriteLine("line_segment_merge_regression=ok");
    }

    private static void RunCrossingFillTopologyRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        var fill = AddTopologyFill(scene, 0, 0);
        var line = AddTopologyLine(scene, 0, new PointF(-320, 0), new PointF(320, 0));

        AssertTopologyHit(scene, new PointF(-260, 0), line, DrawingElementKind.Stroke, 0, 0, 0.1875f, "crossing line left");
        var middleHit = AssertTopologyHit(scene, PointF.Empty, line, DrawingElementKind.Stroke, 1, 0.1875f, 0.8125f, "crossing line middle");
        AssertTopologyHit(scene, new PointF(260, 0), line, DrawingElementKind.Stroke, 2, 0.8125f, 1, "crossing line right");
        AssertTopologyHit(scene, new PointF(0, -80), fill, DrawingElementKind.Fill, 0, 0, 1, "crossing fill upper region");
        AssertTopologyHit(scene, new PointF(0, 80), fill, DrawingElementKind.Fill, 1, 0, 1, "crossing fill lower region");

        var detachedMiddle = scene.DetachElementForMove(middleHit, 0);
        AssertShapeCounts(scene, 4, lines: 3, paths: 0, rectangles: 1, "crossing line detach");
        AssertDetachedLine(scene, detachedMiddle, new PointF(-200, 0), new PointF(200, 0), "crossing line middle detach");
        AssertLineSet(
            scene,
            new[]
            {
                (new PointF(-320, 0), new PointF(-200, 0)),
                (new PointF(-200, 0), new PointF(200, 0)),
                (new PointF(200, 0), new PointF(320, 0))
            },
            "crossing line detach");

        var fillScene = new VectorScene();
        fillScene.CreateEmpty();
        var splitFill = AddTopologyFill(fillScene, 0, 0);
        AddTopologyLine(fillScene, 0, new PointF(-320, 0), new PointF(320, 0));
        var lowerHit = AssertTopologyHit(fillScene, new PointF(0, 80), splitFill, DrawingElementKind.Fill, 1, 0, 1, "crossing fill lower detach target");
        var detachedLower = fillScene.DetachElementForMove(lowerHit, 0);
        AssertShapeCounts(fillScene, 3, lines: 1, paths: 2, rectangles: 0, "crossing fill detach");
        AssertSplitPaths(fillScene, detachedLower, new PointF(0, 80), new PointF(0, -80), "crossing fill detach");
        AssertLineSet(fillScene, new[] { (new PointF(-320, 0), new PointF(320, 0)) }, "crossing fill detach");
    }

    private static void RunTerminatingLineTopologyRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        var fill = AddTopologyFill(scene, 0, 0);
        var line = AddTopologyLine(scene, 0, new PointF(-320, 0), PointF.Empty);

        AssertTopologyHit(scene, new PointF(-260, 0), line, DrawingElementKind.Stroke, 0, 0, 0.375f, "terminating line exterior");
        var interiorHit = AssertTopologyHit(scene, new PointF(-100, 0), line, DrawingElementKind.Stroke, 1, 0.375f, 1, "terminating line interior");
        AssertTopologyHit(scene, new PointF(0, -80), fill, DrawingElementKind.Fill, 0, 0, 1, "terminating line upper fill");
        var lowerFillHit = AssertTopologyHit(scene, new PointF(0, 80), fill, DrawingElementKind.Fill, 0, 0, 1, "terminating line lower fill");

        var unchangedFill = scene.DetachElementForMove(lowerFillHit, 0);
        if (scene.ObjectCount != 2 || unchangedFill.Key != lowerFillHit.Key)
        {
            throw new InvalidOperationException($"A line terminating inside a fill incorrectly split the fill: objects={scene.ObjectCount}, before={lowerFillHit.Key}, after={unchangedFill.Key}.");
        }

        var detachedInterior = scene.DetachElementForMove(interiorHit, 0);
        AssertShapeCounts(scene, 3, lines: 2, paths: 0, rectangles: 1, "terminating line detach");
        AssertDetachedLine(scene, detachedInterior, new PointF(-200, 0), PointF.Empty, "terminating line interior detach");
        AssertLineSet(
            scene,
            new[]
            {
                (new PointF(-320, 0), new PointF(-200, 0)),
                (new PointF(-200, 0), PointF.Empty)
            },
            "terminating line detach");
    }

    private static void RunTerminatingStrokeContactRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        var terminating = AddTopologyLine(scene, 0, new PointF(-200, 0), PointF.Empty);
        var through = AddTopologyLine(scene, 0, new PointF(0, -200), new PointF(0, 200));

        var upper = AssertTopologyHit(scene, new PointF(0, -100), through, DrawingElementKind.Stroke, 0, 0, 0.5f, "terminating contact upper");
        var lower = AssertTopologyHit(scene, new PointF(0, 100), through, DrawingElementKind.Stroke, 1, 0.5f, 1, "terminating contact lower");
        var connected = scene.GetConnectedStrokeElements(upper, 0);
        var materialized = scene.MaterializeSelectedParts(connected.Select(hit => hit.Key).ToArray(), 0);
        var contactEndpoints = Enumerable.Range(0, scene.ObjectCount)
            .Sum(index =>
            {
                scene.TryGetLineEndpoint(index, startEndpoint: true, out var start);
                scene.TryGetLineEndpoint(index, startEndpoint: false, out var end);
                return (PointsNear(start, PointF.Empty) ? 1 : 0) + (PointsNear(end, PointF.Empty) ? 1 : 0);
            });

        if (lower.Key.PartIndex != 1
            || connected.Length != 3
            || !connected.Select(hit => hit.Key.ObjectIndex).ToHashSet().SetEquals(new[] { terminating, through })
            || !materialized.Success
            || !materialized.Changed
            || scene.ObjectCount != 3
            || contactEndpoints != 3)
        {
            throw new InvalidOperationException("A terminating stroke contact did not preserve editable endpoints on the joined node.");
        }
    }

    private static void RunCommittedTerminatingCurveSplitRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        var start = new PointF(-200, 100);
        var control1 = new PointF(-50, -200);
        var control2 = new PointF(80, 200);
        var end = new PointF(220, -80);
        var through = scene.AddCubicCurveSegment(
            0,
            start,
            control1,
            control2,
            end,
            18,
            Color.Transparent,
            Color.CornflowerBlue,
            12,
            LineEndpointStyle.Sharp,
            LineEndpointStyle.Sharp);
        scene.SetLinearGradient(through, Color.CornflowerBlue, Color.Gold);
        var exactContact = CubicPoint(start, control1, control2, end, 0.5f);
        var quantizedContact = VectorUnits.Quantize(exactContact);
        var terminating = scene.AddLineSegment(
            0,
            new PointF(-180, -120),
            quantizedContact,
            24,
            Color.Transparent,
            Color.White,
            8);

        var result = scene.MaterializeLineIntersections([terminating, through], 0);
        var splitCurves = result.Parts
            .Where(part => part.Source.ObjectIndex == through)
            .Select(part => part.Result.ObjectIndex)
            .Distinct()
            .ToArray();
        var contactEndpoints = Enumerable.Range(0, scene.ObjectCount)
            .Sum(index =>
            {
                scene.TryGetLineEndpoint(index, startEndpoint: true, out var lineStart);
                scene.TryGetLineEndpoint(index, startEndpoint: false, out var lineEnd);
                return (PointsNear(lineStart, quantizedContact) ? 1 : 0)
                    + (PointsNear(lineEnd, quantizedContact) ? 1 : 0);
            });
        var outerSharpEndpoints = splitCurves.Sum(index =>
            (scene.GetLineEndpointStyle(index, startEndpoint: true) == LineEndpointStyle.Sharp ? 1 : 0)
            + (scene.GetLineEndpointStyle(index, startEndpoint: false) == LineEndpointStyle.Sharp ? 1 : 0));
        if (!result.Success
            || !result.Changed
            || scene.ObjectCount != 3
            || splitCurves.Length != 2
            || contactEndpoints != 3
            || outerSharpEndpoints != 2
            || splitCurves.Any(index => !scene.HasGradient(index)))
        {
            throw new InvalidOperationException(
                $"A quantized line endpoint did not materialize its terminating cubic contact: " +
                $"success={result.Success}, changed={result.Changed}, objects={scene.ObjectCount}, " +
                $"splits={splitCurves.Length}, endpoints={contactEndpoints}, sharp={outerSharpEndpoints}.");
        }

        static PointF CubicPoint(PointF p0, PointF p1, PointF p2, PointF p3, float t)
        {
            var inverse = 1f - t;
            var a = inverse * inverse * inverse;
            var b = 3f * inverse * inverse * t;
            var c = 3f * inverse * t * t;
            var d = t * t * t;
            return new PointF(
                p0.X * a + p1.X * b + p2.X * c + p3.X * d,
                p0.Y * a + p1.Y * b + p2.Y * c + p3.Y * d);
        }
    }

    private static void RunCrossingLinesTopologyRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        var horizontal = AddTopologyLine(scene, 0, new PointF(-320, 0), new PointF(320, 0));
        var vertical = AddTopologyLine(scene, 0, new PointF(0, -240), new PointF(0, 240));

        AssertTopologyHit(scene, new PointF(-160, 0), horizontal, DrawingElementKind.Stroke, 0, 0, 0.5f, "crossing lines horizontal left");
        var horizontalRight = AssertTopologyHit(scene, new PointF(160, 0), horizontal, DrawingElementKind.Stroke, 1, 0.5f, 1, "crossing lines horizontal right");
        AssertTopologyHit(scene, new PointF(0, -120), vertical, DrawingElementKind.Stroke, 0, 0, 0.5f, "crossing lines vertical upper");
        AssertTopologyHit(scene, new PointF(0, 120), vertical, DrawingElementKind.Stroke, 1, 0.5f, 1, "crossing lines vertical lower");

        var detachedRight = scene.DetachElementForMove(horizontalRight, 0);
        AssertShapeCounts(scene, 3, lines: 3, paths: 0, rectangles: 0, "crossing lines detach");
        AssertDetachedLine(scene, detachedRight, PointF.Empty, new PointF(320, 0), "crossing lines horizontal right detach");
        AssertLineSet(
            scene,
            new[]
            {
                (new PointF(-320, 0), PointF.Empty),
                (PointF.Empty, new PointF(320, 0)),
                (new PointF(0, -240), new PointF(0, 240))
            },
            "crossing lines detach");
    }

    private static void RunCollinearOverlapTopologyRegression()
    {
        var firstScene = new VectorScene();
        firstScene.CreateEmpty();
        var first = AddTopologyLine(firstScene, 0, new PointF(-100, 0), new PointF(100, 0));
        var firstOverlap = AddTopologyLine(firstScene, 0, PointF.Empty, new PointF(200, 0));

        var firstLeft = AssertTopologyHit(firstScene, new PointF(-50, 0), first, DrawingElementKind.Stroke, 0, 0, 0.5f, "collinear first line left");
        AssertTopologyHit(firstScene, new PointF(50, 0), firstOverlap, DrawingElementKind.Stroke, 0, 0, 0.5f, "collinear overlap priority");
        var detachedFirst = firstScene.DetachElementForMove(firstLeft, 0);
        AssertShapeCounts(firstScene, 3, lines: 3, paths: 0, rectangles: 0, "collinear first line detach");
        AssertDetachedLine(firstScene, detachedFirst, new PointF(-100, 0), PointF.Empty, "collinear first line detach");
        AssertLineSet(
            firstScene,
            new[]
            {
                (new PointF(-100, 0), PointF.Empty),
                (PointF.Empty, new PointF(100, 0)),
                (PointF.Empty, new PointF(200, 0))
            },
            "collinear first line detach");

        var secondScene = new VectorScene();
        secondScene.CreateEmpty();
        var second = AddTopologyLine(secondScene, 0, PointF.Empty, new PointF(200, 0));
        var secondOverlap = AddTopologyLine(secondScene, 0, new PointF(-100, 0), new PointF(100, 0));

        AssertTopologyHit(secondScene, new PointF(50, 0), secondOverlap, DrawingElementKind.Stroke, 1, 0.5f, 1, "collinear reverse overlap priority");
        var secondRight = AssertTopologyHit(secondScene, new PointF(150, 0), second, DrawingElementKind.Stroke, 1, 0.5f, 1, "collinear second line right");
        var detachedSecond = secondScene.DetachElementForMove(secondRight, 0);
        AssertShapeCounts(secondScene, 3, lines: 3, paths: 0, rectangles: 0, "collinear second line detach");
        AssertDetachedLine(secondScene, detachedSecond, new PointF(100, 0), new PointF(200, 0), "collinear second line detach");
        AssertLineSet(
            secondScene,
            new[]
            {
                (new PointF(-100, 0), new PointF(100, 0)),
                (PointF.Empty, new PointF(100, 0)),
                (new PointF(100, 0), new PointF(200, 0))
            },
            "collinear second line detach");

        var diagonalScene = new VectorScene();
        diagonalScene.CreateEmpty();
        var diagonalFirst = AddTopologyLine(diagonalScene, 0, new PointF(-100, -50), new PointF(100, 50));
        var diagonalSecond = AddTopologyLine(diagonalScene, 0, PointF.Empty, new PointF(200, 100));
        AssertTopologyHit(diagonalScene, new PointF(-50, -25), diagonalFirst, DrawingElementKind.Stroke, 0, 0, 0.5f, "diagonal collinear first line");
        AssertTopologyHit(diagonalScene, new PointF(150, 75), diagonalSecond, DrawingElementKind.Stroke, 1, 0.5f, 1, "diagonal collinear second line");
    }

    private static void RunOutlinedBoundaryTopologyRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        var outlinedFill = AddTopologyFill(scene, 0, VectorUnits.StrokePointsToUnits(2));
        var crossingLine = AddTopologyLine(scene, 0, new PointF(-320, 0), new PointF(320, 0));

        var topHit = AssertTopologyHit(scene, new PointF(0, -120), outlinedFill, DrawingElementKind.BoundaryStroke, 0, 0, 0.25f, "outlined boundary top");
        AssertTopologyHit(scene, new PointF(200, -60), outlinedFill, DrawingElementKind.BoundaryStroke, 1, 0.25f, 0.375f, "outlined boundary upper right");
        AssertTopologyHit(scene, new PointF(200, 60), outlinedFill, DrawingElementKind.BoundaryStroke, 2, 0.375f, 0.5f, "outlined boundary lower right");
        AssertTopologyHit(scene, new PointF(0, 120), outlinedFill, DrawingElementKind.BoundaryStroke, 3, 0.5f, 0.75f, "outlined boundary bottom");
        AssertTopologyHit(scene, new PointF(-200, 60), outlinedFill, DrawingElementKind.BoundaryStroke, 4, 0.75f, 0.875f, "outlined boundary lower left");
        AssertTopologyHit(scene, new PointF(-200, -60), outlinedFill, DrawingElementKind.BoundaryStroke, 5, 0.875f, 1, "outlined boundary upper left");
        AssertTopologyHit(scene, new PointF(-260, 0), crossingLine, DrawingElementKind.Stroke, 0, 0, 0.1875f, "outlined crossing line left");
        AssertTopologyHit(scene, PointF.Empty, crossingLine, DrawingElementKind.Stroke, 1, 0.1875f, 0.8125f, "outlined crossing line middle");
        AssertTopologyHit(scene, new PointF(260, 0), crossingLine, DrawingElementKind.Stroke, 2, 0.8125f, 1, "outlined crossing line right");

        var detachedTop = scene.DetachElementForMove(topHit, 0);
        AssertShapeCounts(scene, 8, lines: 7, paths: 0, rectangles: 1, "outlined boundary detach");
        if ((uint)outlinedFill >= scene.ObjectCount || scene.Stroke[outlinedFill] != 0)
        {
            throw new InvalidOperationException("Detaching an outlined boundary segment did not remove the original object's compound boundary stroke.");
        }

        AssertDetachedLine(scene, detachedTop, new PointF(-200, -120), new PointF(200, -120), "outlined boundary top detach");
        AssertLineSet(scene, OutlinedBoundaryLineSegments(), "outlined boundary detach");

        var fillScene = new VectorScene();
        fillScene.CreateEmpty();
        var fillWithOutline = AddTopologyFill(fillScene, 0, VectorUnits.StrokePointsToUnits(2));
        AddTopologyLine(fillScene, 0, new PointF(-320, 0), new PointF(320, 0));
        var lowerFillHit = AssertTopologyHit(fillScene, new PointF(0, 80), fillWithOutline, DrawingElementKind.Fill, 1, 0, 1, "outlined fill lower detach target");
        var detachedLower = fillScene.DetachElementForMove(lowerFillHit, 0);
        AssertShapeCounts(fillScene, 9, lines: 7, paths: 2, rectangles: 0, "outlined fill detach");
        AssertSplitPaths(fillScene, detachedLower, new PointF(0, 80), new PointF(0, -80), "outlined fill detach");
        AssertLineSet(fillScene, OutlinedBoundaryLineSegments(), "outlined fill detach");
    }

    private static void RunSmoothPathBoundaryGroupingRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        var contour = new List<PointF>
        {
            new(-240, -140),
            new(160, -140)
        };
        var curveStart = contour[^1];
        var control1 = new PointF(160, -40);
        var control2 = new PointF(260, 40);
        var curveEnd = new PointF(160, 140);
        for (var sample = 1; sample <= 16; sample++)
        {
            var t = sample / 16f;
            var inverse = 1 - t;
            contour.Add(new PointF(
                curveStart.X * inverse * inverse * inverse
                    + 3 * control1.X * inverse * inverse * t
                    + 3 * control2.X * inverse * t * t
                    + curveEnd.X * t * t * t,
                curveStart.Y * inverse * inverse * inverse
                    + 3 * control1.Y * inverse * inverse * t
                    + 3 * control2.Y * inverse * t * t
                    + curveEnd.Y * t * t * t));
        }
        contour.Add(new PointF(-200, 140));
        contour.Add(contour[0]);

        var outlinedPath = scene.AddPathObjectContours(
            0,
            [contour.ToArray()],
            VectorUnits.StrokePointsToUnits(2),
            Color.Teal,
            Color.White,
            32);
        var boundaryParts = scene.GetBoundaryParts(outlinedPath, 0);
        var smoothPart = boundaryParts.FirstOrDefault(part => part.Points.Length >= 12);
        if (boundaryParts.Length != 4
            || smoothPart.Points is null
            || smoothPart.Points.Length < 12)
        {
            throw new InvalidOperationException(
                $"A smooth outlined Path boundary was split at its sampling points: parts={boundaryParts.Length}, "
                + $"longest={boundaryParts.Select(part => part.Points.Length).DefaultIfEmpty(0).Max()}.");
        }
    }

    private static void RunCrossLayerTopologyRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty(2);
        var fill = AddTopologyFill(scene, 1, 0);
        var line = AddTopologyLine(scene, 0, new PointF(-320, 0), new PointF(320, 0));

        var lineHit = AssertTopologyHit(scene, PointF.Empty, line, DrawingElementKind.Stroke, 0, 0, 1, "cross-layer line");
        AssertTopologyHit(scene, new PointF(0, -80), fill, DrawingElementKind.Fill, 0, 0, 1, "cross-layer upper fill");
        AssertTopologyHit(scene, new PointF(0, 80), fill, DrawingElementKind.Fill, 0, 0, 1, "cross-layer lower fill");

        var unchangedLine = scene.DetachElementForMove(lineHit, 0);
        if (scene.ObjectCount != 2 || unchangedLine.Key != lineHit.Key)
        {
            throw new InvalidOperationException($"Objects on different layers incorrectly participated in topology splitting: objects={scene.ObjectCount}, before={lineHit.Key}, after={unchangedLine.Key}.");
        }
    }

    private static void RunMultipleCutterFillTopologyRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        var fill = AddTopologyFill(scene, 0, 0);
        AddTopologyLine(scene, 0, new PointF(-320, 0), new PointF(320, 0));
        AddTopologyLine(scene, 0, new PointF(0, -240), new PointF(0, 240));

        var points = new[]
        {
            new PointF(-100, -70),
            new PointF(100, -70),
            new PointF(-100, 70),
            new PointF(100, 70)
        };
        var hits = points.Select(point => scene.HitTestElement(point, 0, 0)).ToArray();
        if (hits.Any(hit => !hit.IsValid || hit.Key.ObjectIndex != fill || hit.Key.Kind != DrawingElementKind.Fill)
            || hits.Select(hit => hit.Key.PartIndex).Distinct().Count() != 4)
        {
            throw new InvalidOperationException("Two crossing cutters did not expose four independently selectable fill regions.");
        }

        var detached = scene.DetachElementForMove(hits[3], 0);
        AssertShapeCounts(scene, 6, lines: 2, paths: 4, rectangles: 0, "multiple cutter fill detach");
        if (!detached.IsValid || detached.Key.Kind != DrawingElementKind.Fill || !scene.FillContainsPoint(detached.Key.ObjectIndex, points[3]))
        {
            throw new InvalidOperationException("Detaching one quadrant did not return the selected fill region.");
        }

        foreach (var point in points)
        {
            if (CountPathFillsContaining(scene, point) != 1)
            {
                throw new InvalidOperationException($"Multiple-cutter materialization did not preserve exactly one fill at ({point.X},{point.Y}).");
            }
        }
    }

    private static void RunCurvedCutterFillTopologyRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        var fill = AddTopologyFill(scene, 0, 0);
        scene.AddCurveSegment(
            0,
            new PointF(-320, 0),
            new PointF(0, 200),
            new PointF(320, 0),
            VectorUnits.StrokePointsToUnits(2),
            Color.Transparent,
            Color.White,
            12);

        var aboveCurve = new PointF(0, 45);
        var belowCurve = new PointF(0, 118);
        var upperHit = scene.HitTestElement(aboveCurve, 0, 0);
        var lowerHit = scene.HitTestElement(belowCurve, 0, 0);
        if (!upperHit.IsValid
            || !lowerHit.IsValid
            || upperHit.Key.ObjectIndex != fill
            || lowerHit.Key.ObjectIndex != fill
            || upperHit.Key.Kind != DrawingElementKind.Fill
            || lowerHit.Key.Kind != DrawingElementKind.Fill
            || upperHit.Key.PartIndex == lowerHit.Key.PartIndex)
        {
            throw new InvalidOperationException("A curved stroke did not split the fill along its rendered curve.");
        }

        var detached = scene.DetachElementForMove(lowerHit, 0);
        AssertShapeCounts(scene, 3, lines: 1, paths: 2, rectangles: 0, "curved cutter fill detach");
        AssertSplitPaths(scene, detached, belowCurve, aboveCurve, "curved cutter fill detach");
    }

    private static void RunCompoundFillTopologyRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        var fill = scene.AddPathObjectContours(
            0,
            new[]
            {
                new[] { new PointF(-200, -120), new PointF(200, -120), new PointF(200, 120), new PointF(-200, 120) },
                new[] { new PointF(-60, -40), new PointF(60, -40), new PointF(60, 40), new PointF(-60, 40) },
                new[] { new PointF(260, -40), new PointF(340, -40), new PointF(340, 40), new PointF(260, 40) }
            },
            0,
            Color.Teal,
            Color.White,
            24);
        AddTopologyLine(scene, 0, new PointF(-320, 80), new PointF(240, 80));

        var upperPoint = new PointF(-120, 0);
        var lowerPoint = new PointF(0, 100);
        var islandPoint = new PointF(300, 0);
        var hits = new[]
        {
            scene.HitTestElement(upperPoint, 0, 0),
            scene.HitTestElement(lowerPoint, 0, 0),
            scene.HitTestElement(islandPoint, 0, 0)
        };
        if (hits.Any(hit => !hit.IsValid || hit.Key.ObjectIndex != fill || hit.Key.Kind != DrawingElementKind.Fill)
            || hits.Select(hit => hit.Key.PartIndex).Distinct().Count() != 3)
        {
            throw new InvalidOperationException("Compound Path fill regions were not independently selectable.");
        }

        var detached = scene.DetachElementForMove(hits[1], 0);
        AssertShapeCounts(scene, 4, lines: 1, paths: 3, rectangles: 0, "compound fill detach");
        if (!detached.IsValid || !scene.FillContainsPoint(detached.Key.ObjectIndex, lowerPoint))
        {
            throw new InvalidOperationException("Compound fill detach did not return the selected lower region.");
        }

        var upperPath = FindPathFillContaining(scene, upperPoint);
        var islandPath = FindPathFillContaining(scene, islandPoint);
        if (upperPath < 0 || islandPath < 0 || scene.FillContainsPoint(upperPath, PointF.Empty))
        {
            throw new InvalidOperationException("Compound fill detach lost its hole or secondary island.");
        }
    }

    private static void RunPencilStrokeTopologyRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        var pencil = scene.AddFreehandStroke(
            0,
            new[] { new PointF(-200, 0), PointF.Empty, new PointF(200, 0) },
            VectorUnits.StrokePointsToUnits(2),
            Color.White,
            brushStroke: false,
            9);
        AddTopologyLine(scene, 0, new PointF(0, -200), new PointF(0, 200));

        AssertTopologyHit(scene, new PointF(-100, 0), pencil, DrawingElementKind.Stroke, 0, 0, 0.5f, "pencil left segment");
        var rightHit = AssertTopologyHit(scene, new PointF(100, 0), pencil, DrawingElementKind.Stroke, 1, 0.5f, 1, "pencil right segment");
        var detached = scene.DetachElementForMove(rightHit, 0);
        var freeforms = Enumerable.Range(0, scene.ObjectCount).Where(index => scene.ShapeKind[index] == ShapeKind.Freeform).ToArray();
        if (scene.ObjectCount != 3
            || freeforms.Length != 2
            || !detached.IsValid
            || scene.ShapeKind[detached.Key.ObjectIndex] != ShapeKind.Freeform
            || !scene.TryGetFreehandWorldPoints(detached.Key.ObjectIndex, out var detachedPoints)
            || detachedPoints.Length < 2
            || !PointsNear(detachedPoints[0], PointF.Empty)
            || !PointsNear(detachedPoints[^1], new PointF(200, 0)))
        {
            throw new InvalidOperationException("Pencil stroke intersection did not materialize the selected local segment.");
        }

        var fillScene = new VectorScene();
        fillScene.CreateEmpty();
        var fill = AddTopologyFill(fillScene, 0, 0);
        fillScene.AddFreehandStroke(
            0,
            new[] { new PointF(-320, 0), PointF.Empty, new PointF(320, 0) },
            VectorUnits.StrokePointsToUnits(2),
            Color.White,
            brushStroke: false,
            9);
        var upper = fillScene.HitTestElement(new PointF(0, -80), 0, 0);
        var lower = fillScene.HitTestElement(new PointF(0, 80), 0, 0);
        if (!upper.IsValid
            || !lower.IsValid
            || upper.Key.ObjectIndex != fill
            || lower.Key.ObjectIndex != fill
            || upper.Key.PartIndex == lower.Key.PartIndex)
        {
            throw new InvalidOperationException("A Pencil stroke traversing a fill did not create selectable fill regions.");
        }
    }

    private static void RunConnectedCutterNetworkTopologyRegression()
    {
        var vScene = new VectorScene();
        vScene.CreateEmpty();
        var vFill = AddTopologyFill(vScene, 0, 0);
        AddTopologyLine(vScene, 0, new PointF(-200, -80), PointF.Empty);
        AddTopologyLine(vScene, 0, new PointF(200, -70), PointF.Empty);
        var vUpper = vScene.HitTestElement(new PointF(0, -60), 0, 0);
        var vLower = vScene.HitTestElement(new PointF(0, 80), 0, 0);
        if (!vUpper.IsValid
            || !vLower.IsValid
            || vUpper.Key.ObjectIndex != vFill
            || vLower.Key.ObjectIndex != vFill
            || vUpper.Key.Kind != DrawingElementKind.Fill
            || vLower.Key.Kind != DrawingElementKind.Fill
            || vUpper.Key.PartIndex == vLower.Key.PartIndex)
        {
            throw new InvalidOperationException("Two snapped Line objects did not form one continuous fill cutter.");
        }

        var tScene = new VectorScene();
        tScene.CreateEmpty();
        var tFill = AddTopologyFill(tScene, 0, 0);
        AddTopologyLine(tScene, 0, new PointF(-320, 0), new PointF(320, 0));
        AddTopologyLine(tScene, 0, PointF.Empty, new PointF(0, 80));
        var lowerHit = tScene.HitTestElement(new PointF(100, 80), 0, 0);
        if (!lowerHit.IsValid || lowerHit.Key.ObjectIndex != tFill || lowerHit.Key.Kind != DrawingElementKind.Fill)
        {
            throw new InvalidOperationException("T-junction fill setup did not expose the lower region.");
        }

        var detached = tScene.DetachElementForMove(lowerHit, 0);
        var selected = detached.Key.ObjectIndex;
        if (!detached.IsValid
            || detached.Key.Kind != DrawingElementKind.Fill
            || !tScene.FillContainsPoint(selected, new PointF(0, 10))
            || !tScene.FillContainsPoint(selected, new PointF(-1, 40))
            || !tScene.FillContainsPoint(selected, new PointF(1, 40))
            || CountPathFillsContaining(tScene, new PointF(0, 40)) != 1)
        {
            throw new InvalidOperationException("A dangling T-junction branch left a false notch in the materialized fill.");
        }

        var collinearScene = new VectorScene();
        collinearScene.CreateEmpty();
        var collinearFill = AddTopologyFill(collinearScene, 0, 0);
        AddTopologyLine(collinearScene, 0, new PointF(-320, -80), PointF.Empty);
        AddTopologyLine(collinearScene, 0, PointF.Empty, new PointF(320, 80));
        var collinearUpper = collinearScene.HitTestElement(new PointF(0, -60), 0, 0);
        var collinearLower = collinearScene.HitTestElement(new PointF(0, 60), 0, 0);
        if (!collinearUpper.IsValid
            || !collinearLower.IsValid
            || collinearUpper.Key.ObjectIndex != collinearFill
            || collinearLower.Key.ObjectIndex != collinearFill
            || collinearUpper.Key.Kind != DrawingElementKind.Fill
            || collinearLower.Key.Kind != DrawingElementKind.Fill
            || collinearUpper.Key.PartIndex == collinearLower.Key.PartIndex)
        {
            throw new InvalidOperationException("Two collinear Line objects sharing one endpoint did not form a continuous fill cutter.");
        }

        var collinearDetached = collinearScene.DetachElementForMove(collinearLower, 0);
        AssertShapeCounts(collinearScene, 4, lines: 2, paths: 2, rectangles: 0, "collinear connected fill detach");
        AssertSplitPaths(collinearScene, collinearDetached, new PointF(0, 60), new PointF(0, -60), "collinear connected fill detach");
    }

    private static void RunConnectedStrokeSelectionRegression()
    {
        var chainScene = new VectorScene();
        chainScene.CreateEmpty(2);
        var first = AddTopologyLine(chainScene, 0, new PointF(-200, 0), PointF.Empty);
        var second = AddTopologyLine(chainScene, 0, PointF.Empty, new PointF(200, 0));
        var third = AddTopologyLine(chainScene, 0, new PointF(200, 0), new PointF(200, 120));
        var disconnected = AddTopologyLine(chainScene, 0, new PointF(-200, 40), new PointF(0, 40));
        var otherLayer = AddTopologyLine(chainScene, 1, PointF.Empty, new PointF(0, -120));
        var chainSeed = chainScene.HitTestElement(new PointF(-100, 0), 0, 0);
        var chainSelection = chainScene.GetConnectedStrokeElements(chainSeed, 0);
        var chainObjects = chainSelection.Select(hit => hit.Key.ObjectIndex).ToHashSet();
        if (!chainSeed.IsValid
            || chainSelection.Length != 3
            || !chainObjects.SetEquals(new[] { first, second, third })
            || chainObjects.Contains(disconnected)
            || chainObjects.Contains(otherLayer))
        {
            throw new InvalidOperationException("Connected stroke selection did not follow endpoint links within the active layer.");
        }

        var crossingScene = new VectorScene();
        crossingScene.CreateEmpty();
        var horizontal = AddTopologyLine(crossingScene, 0, new PointF(-200, 0), new PointF(200, 0));
        var vertical = AddTopologyLine(crossingScene, 0, new PointF(0, -150), new PointF(0, 150));
        var crossingSeed = crossingScene.HitTestElement(new PointF(-100, 0), 0, 0);
        var crossingSelection = crossingScene.GetConnectedStrokeElements(crossingSeed, 0);
        if (crossingSelection.Length != 4
            || crossingSelection.Count(hit => hit.Key.ObjectIndex == horizontal) != 2
            || crossingSelection.Count(hit => hit.Key.ObjectIndex == vertical) != 2)
        {
            throw new InvalidOperationException("Connected stroke selection did not traverse a split crossing network.");
        }

        var crossingNodeParts = crossingSelection
            .Where(hit =>
            {
                var points = crossingScene.GetStrokePartPoints(hit, 0);
                return points.Length > 0
                    && (Math.Abs(points[0].X) <= 0.001f && Math.Abs(points[0].Y) <= 0.001f
                        || Math.Abs(points[^1].X) <= 0.001f && Math.Abs(points[^1].Y) <= 0.001f);
            })
            .ToArray();
        var crossingNodeMaterialized = crossingScene.MaterializeSelectedParts(
            crossingNodeParts.Select(hit => hit.Key).ToArray(),
            0);
        var crossingNodeEndpoints = Enumerable.Range(0, crossingScene.ObjectCount)
            .Sum(index =>
            {
                crossingScene.TryGetLineEndpoint(index, startEndpoint: true, out var start);
                crossingScene.TryGetLineEndpoint(index, startEndpoint: false, out var end);
                return (Math.Abs(start.X) <= 0.001f && Math.Abs(start.Y) <= 0.001f ? 1 : 0)
                    + (Math.Abs(end.X) <= 0.001f && Math.Abs(end.Y) <= 0.001f ? 1 : 0);
            });
        if (!crossingNodeMaterialized.Success
            || !crossingNodeMaterialized.Changed
            || crossingNodeParts.Length != 4
            || crossingScene.ObjectCount != 4
            || crossingNodeEndpoints != 4)
        {
            throw new InvalidOperationException(
                $"Crossing endpoint materialization did not preserve all node-linked line endpoints: success={crossingNodeMaterialized.Success}, changed={crossingNodeMaterialized.Changed}, parts={crossingNodeParts.Length}, objects={crossingScene.ObjectCount}, endpoints={crossingNodeEndpoints}.");
        }

        var boundaryScene = new VectorScene();
        boundaryScene.CreateEmpty();
        var outlined = AddTopologyFill(boundaryScene, 0, VectorUnits.StrokePointsToUnits(2));
        var attachedLine = AddTopologyLine(boundaryScene, 0, new PointF(-320, 0), new PointF(-200, 0));
        var boundarySeed = boundaryScene.HitTestElement(new PointF(-260, 0), 0, 0);
        var boundarySelection = boundaryScene.GetConnectedStrokeElements(boundarySeed, 0);
        var selectedKeys = boundarySelection.Select(hit => hit.Key).ToHashSet();
        var boundaryParts = boundaryScene.GetBoundaryParts(outlined, 0);
        if (!selectedKeys.Any(key => key.ObjectIndex == attachedLine && key.Kind == DrawingElementKind.Stroke)
            || boundaryParts.Length == 0
            || boundaryParts.Any(part => !selectedKeys.Contains(new DrawingElementKey(outlined, DrawingElementKind.BoundaryStroke, part.PartIndex))))
        {
            throw new InvalidOperationException("Connected stroke selection did not traverse an attached Fill boundary.");
        }
    }

    private static void RunMaterializedOrderTopologyRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        var lower = AddTopologyLine(scene, 0, new PointF(-200, 0), new PointF(200, 0));
        var upper = AddTopologyLine(scene, 0, new PointF(0, -200), new PointF(0, 200));
        var lowerOrder = scene.ObjectOrder[lower];
        var upperOrder = scene.ObjectOrder[upper];
        var rightHit = scene.HitTestElement(new PointF(100, 0), 0, 0);
        if (!rightHit.IsValid || rightHit.Key.ObjectIndex != lower || rightHit.Key.PartIndex != 1)
        {
            throw new InvalidOperationException("Materialized order regression could not select the lower line segment.");
        }

        scene.DetachElementForMove(rightHit, 0);
        var crossingHit = scene.HitTestElement(PointF.Empty, 0, 0);
        if (!crossingHit.IsValid || scene.ObjectOrder[crossingHit.Key.ObjectIndex] != upperOrder)
        {
            throw new InvalidOperationException("Materializing a lower stroke changed same-layer stroke priority.");
        }

        var lowerParts = Enumerable.Range(0, scene.ObjectCount).Count(index => scene.ObjectOrder[index] == lowerOrder);
        if (lowerParts != 2)
        {
            throw new InvalidOperationException($"Materialized stroke parts did not inherit the source draw order: parts={lowerParts}.");
        }

        var recursiveScene = new VectorScene();
        recursiveScene.CreateEmpty();
        var recursiveSource = AddTopologyLine(recursiveScene, 0, new PointF(-200, 0), new PointF(200, 0));
        var recursiveOrder = recursiveScene.ObjectOrder[recursiveSource];
        AddTopologyLine(recursiveScene, 0, new PointF(0, -160), new PointF(0, 160));
        var recursiveRight = recursiveScene.HitTestElement(new PointF(100, 0), 0, 0);
        recursiveScene.DetachElementForMove(recursiveRight, 0);
        var siblings = Enumerable.Range(0, recursiveScene.ObjectCount)
            .Where(index => recursiveScene.ObjectOrder[index] == recursiveOrder)
            .OrderBy(index => recursiveScene.ObjectSubOrder[index])
            .ToArray();
        if (siblings.Length != 2)
        {
            throw new InvalidOperationException("Recursive materialized order regression did not create two sibling segments.");
        }

        var lowerSibling = siblings[0];
        var upperSibling = siblings[1];
        var dx = recursiveScene.X[upperSibling] - recursiveScene.X[lowerSibling];
        recursiveScene.X[lowerSibling] += dx;
        recursiveScene.CurveControlX[lowerSibling] += dx;
        recursiveScene.CurveControl2X[lowerSibling] += dx;
        recursiveScene.RebuildGeometryIndex();
        AddTopologyLine(recursiveScene, 0, new PointF(100, -160), new PointF(100, 160));
        var beforeRecursiveSplit = recursiveScene.HitTestElement(new PointF(50, 0), 0, 0);
        if (!beforeRecursiveSplit.IsValid || beforeRecursiveSplit.Key.ObjectIndex != upperSibling)
        {
            throw new InvalidOperationException("Recursive materialized order setup did not place the higher sibling on top.");
        }

        var recursiveResult = recursiveScene.MaterializeSelectedParts(
            new[] { new DrawingElementKey(lowerSibling, DrawingElementKind.Stroke, 0) },
            0);
        var mappedUpperSibling = recursiveResult.Success
            ? recursiveResult.OldToNewObjectIndex[upperSibling]
            : -1;
        var afterRecursiveSplit = recursiveScene.HitTestElement(new PointF(50, 0), 0, 0);
        if (!recursiveResult.Success
            || !recursiveResult.Changed
            || !afterRecursiveSplit.IsValid
            || afterRecursiveSplit.Key.ObjectIndex != mappedUpperSibling)
        {
            throw new InvalidOperationException("Re-splitting a lower sibling changed same-order stroke priority.");
        }
    }

    private static void RunBatchElementMaterializationRegression()
    {
        var strokeScene = new VectorScene();
        strokeScene.CreateEmpty();
        var horizontal = AddTopologyLine(strokeScene, 0, new PointF(-200, 0), new PointF(200, 0));
        AddTopologyLine(strokeScene, 0, new PointF(0, -160), new PointF(0, 160));
        var left = AssertTopologyHit(strokeScene, new PointF(-100, 0), horizontal, DrawingElementKind.Stroke, 0, 0, 0.5f, "batch stroke left");
        var right = AssertTopologyHit(strokeScene, new PointF(100, 0), horizontal, DrawingElementKind.Stroke, 1, 0.5f, 1, "batch stroke right");
        var strokeOrder = strokeScene.ObjectOrder[horizontal];
        var strokeResult = strokeScene.MaterializeSelectedParts(new[] { left.Key, right.Key }, 0);
        if (!strokeResult.Success
            || !strokeResult.Changed
            || strokeResult.Parts.Length != 2
            || strokeResult.Parts.Select(part => part.Result.ObjectIndex).Distinct().Count() != 2
            || strokeResult.Parts.Any(part => part.Result.Kind != DrawingElementKind.Stroke))
        {
            throw new InvalidOperationException("Batch stroke materialization did not return two independent selected segments.");
        }

        AssertShapeCounts(strokeScene, 3, lines: 3, paths: 0, rectangles: 0, "batch stroke materialization");
        AssertLineSet(
            strokeScene,
            new[]
            {
                (new PointF(-200, 0), PointF.Empty),
                (PointF.Empty, new PointF(200, 0)),
                (new PointF(0, -160), new PointF(0, 160))
            },
            "batch stroke materialization");
        if (strokeResult.Parts.Any(part => strokeScene.ObjectOrder[part.Result.ObjectIndex] != strokeOrder))
        {
            throw new InvalidOperationException("Batch stroke materialization changed the source draw order.");
        }

        var multiHostScene = new VectorScene();
        multiHostScene.CreateEmpty();
        var upperStroke = AddTopologyLine(multiHostScene, 0, new PointF(-200, -40), new PointF(200, -40));
        var lowerStroke = AddTopologyLine(multiHostScene, 0, new PointF(-200, 40), new PointF(200, 40));
        var multiHostCutter = AddTopologyLine(multiHostScene, 0, new PointF(0, -160), new PointF(0, 160));
        var upperRight = AssertTopologyHit(multiHostScene, new PointF(100, -40), upperStroke, DrawingElementKind.Stroke, 1, 0.5f, 1, "batch multi-host upper");
        var lowerRight = AssertTopologyHit(multiHostScene, new PointF(100, 40), lowerStroke, DrawingElementKind.Stroke, 1, 0.5f, 1, "batch multi-host lower");
        var multiHostResult = multiHostScene.MaterializeSelectedParts(new[] { upperRight.Key, lowerRight.Key }, 0);
        if (!multiHostResult.Success
            || !multiHostResult.Changed
            || multiHostResult.Parts.Select(part => part.Result.ObjectIndex).Distinct().Count() != 2
            || multiHostResult.OldToNewObjectIndex[multiHostCutter] != 0)
        {
            throw new InvalidOperationException("Batch materialization across multiple hosts produced an invalid compacted index mapping.");
        }

        AssertShapeCounts(multiHostScene, 5, lines: 5, paths: 0, rectangles: 0, "batch multi-host materialization");
        AssertLineSet(
            multiHostScene,
            new[]
            {
                (new PointF(-200, -40), new PointF(0, -40)),
                (new PointF(0, -40), new PointF(200, -40)),
                (new PointF(-200, 40), new PointF(0, 40)),
                (new PointF(0, 40), new PointF(200, 40)),
                (new PointF(0, -160), new PointF(0, 160))
            },
            "batch multi-host materialization");

        var fillScene = new VectorScene();
        fillScene.CreateEmpty();
        var fill = AddTopologyFill(fillScene, 0, 0);
        AddTopologyLine(fillScene, 0, new PointF(-320, 0), new PointF(320, 0));
        var upper = AssertTopologyHit(fillScene, new PointF(0, -80), fill, DrawingElementKind.Fill, 0, 0, 1, "batch fill upper");
        var lower = AssertTopologyHit(fillScene, new PointF(0, 80), fill, DrawingElementKind.Fill, 1, 0, 1, "batch fill lower");
        var fillResult = fillScene.MaterializeSelectedParts(new[] { upper.Key, lower.Key }, 0);
        if (!fillResult.Success
            || !fillResult.Changed
            || fillResult.Parts.Length != 2
            || fillResult.Parts.Select(part => part.Result.ObjectIndex).Distinct().Count() != 2
            || fillResult.Parts.Any(part => part.Result.Kind != DrawingElementKind.Fill))
        {
            throw new InvalidOperationException("Batch fill materialization did not return two independent selected regions.");
        }

        AssertShapeCounts(fillScene, 3, lines: 1, paths: 2, rectangles: 0, "batch fill materialization");

        var curvedFillScene = new VectorScene();
        curvedFillScene.CreateEmpty();
        var curvedFill = curvedFillScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(400, 240),
            0,
            0,
            Color.Teal,
            Color.Transparent,
            16,
            ShapeKind.Ellipse);
        AddTopologyLine(curvedFillScene, 0, new PointF(-260, 0), new PointF(260, 0));
        var curvedUpper = curvedFillScene.HitTestElement(new PointF(0, -60), 0, toleranceWorld: 2);
        var curvedUpperPieces = curvedUpper.Key.Kind == DrawingElementKind.Fill
            ? curvedFillScene.GetExposedFillBezierSegmentPieces(
                curvedFill,
                0,
                curvedUpper.Key.PartIndex)
            : [];
        var curvedUpperPiece = curvedUpperPieces.FirstOrDefault(piece =>
            !VectorScene.IsStraightBezierSegment(
                piece.Start,
                piece.Control1,
                piece.Control2,
                piece.End));
        var curvedFillResult = curvedUpper.Key.Kind == DrawingElementKind.Fill
            ? curvedFillScene.MaterializeSelectedParts([curvedUpper.Key], 0)
            : new MaterializeSelectedPartsResult(false, false, [], []);
        var curvedFillTarget = curvedFillResult.Parts
            .Where(mapping => mapping.Source == curvedUpper.Key)
            .Select(mapping => mapping.Result.ObjectIndex)
            .FirstOrDefault(-1);
        if (curvedUpperPieces.Length == 0
            || curvedFillTarget < 0
            || !curvedFillResult.Success
            || !curvedFillResult.Changed
            || !curvedFillScene.TryResolveFillBezierSegmentPiece(
                curvedFillTarget,
                curvedUpperPiece,
                out var resolvedCurvedPiece,
                out _)
            || VectorScene.IsStraightBezierSegment(
                resolvedCurvedPiece.Start,
                resolvedCurvedPiece.Control1,
                resolvedCurvedPiece.Control2,
                resolvedCurvedPiece.End))
        {
            throw new InvalidOperationException(
                "Materializing a curved Fill part flattened its source cubic boundary.");
        }

        var compoundScene = new VectorScene();
        compoundScene.CreateEmpty();
        var outlined = AddTopologyFill(compoundScene, 0, VectorUnits.StrokePointsToUnits(2));
        AddTopologyLine(compoundScene, 0, new PointF(-320, 0), new PointF(320, 0));
        var boundary = AssertTopologyHit(compoundScene, new PointF(0, -120), outlined, DrawingElementKind.BoundaryStroke, 0, 0, 0.25f, "batch boundary top");
        var fillPart = AssertTopologyHit(compoundScene, new PointF(0, 80), outlined, DrawingElementKind.Fill, 1, 0, 1, "batch outlined fill lower");
        var outlinedOrder = compoundScene.ObjectOrder[outlined];
        var compoundResult = compoundScene.MaterializeSelectedParts(new[] { boundary.Key, fillPart.Key }, 0);
        if (!compoundResult.Success
            || !compoundResult.Changed
            || compoundResult.Parts.Length != 2
            || compoundResult.Parts.Count(part => part.Result.Kind == DrawingElementKind.Fill) != 1
            || compoundResult.Parts.Count(part => part.Result.Kind == DrawingElementKind.Stroke) != 1)
        {
            throw new InvalidOperationException("Batch Fill and Boundary materialization did not preserve both selected element types.");
        }

        AssertShapeCounts(compoundScene, 9, lines: 7, paths: 2, rectangles: 0, "batch Fill and Boundary materialization");
        if (Enumerable.Range(0, compoundScene.ObjectCount).Count(index => compoundScene.ObjectOrder[index] == outlinedOrder) != 8)
        {
            throw new InvalidOperationException("Batch Fill and Boundary materialization duplicated or lost boundary parts.");
        }

        var preservedHostScene = new VectorScene();
        preservedHostScene.CreateEmpty();
        var outlinedEllipse = preservedHostScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(200, 120),
            0,
            VectorUnits.StrokePointsToUnits(2),
            Color.Teal,
            Color.White,
            12,
            ShapeKind.Ellipse);
        var preservedOrder = preservedHostScene.ObjectOrder[outlinedEllipse];
        var preservedSubOrder = preservedHostScene.ObjectSubOrder[outlinedEllipse];
        var ellipseBoundary = preservedHostScene.HitTestElement(new PointF(0, -60), 0, 0);
        var preservedHostResult = preservedHostScene.MaterializeSelectedParts(new[] { ellipseBoundary.Key }, 0);
        var preservedHost = preservedHostResult.Success
            ? preservedHostResult.OldToNewObjectIndex[outlinedEllipse]
            : -1;
        if (!ellipseBoundary.IsValid
            || ellipseBoundary.Key.Kind != DrawingElementKind.BoundaryStroke
            || !preservedHostResult.Success
            || !preservedHostResult.Changed
            || preservedHost < 0
            || preservedHostScene.ObjectOrder[preservedHost] != preservedOrder
            || !preservedHostScene.ObjectSubOrder[preservedHost].Equals(preservedSubOrder)
            || Enumerable.Range(0, preservedHostScene.ObjectCount)
                .GroupBy(index => (preservedHostScene.ObjectOrder[index], preservedHostScene.ObjectSubOrder[index]))
                .Any(group => group.Count() > 1))
        {
            throw new InvalidOperationException("Boundary-only materialization reused the preserved Fill drawing stack key.");
        }

        var staleScene = new VectorScene();
        staleScene.CreateEmpty();
        var staleFill = AddTopologyFill(staleScene, 0, 0);
        AddTopologyLine(staleScene, 0, new PointF(-320, 0), new PointF(320, 0));
        var staleCount = staleScene.ObjectCount;
        var staleResult = staleScene.MaterializeSelectedParts(
            new[] { new DrawingElementKey(staleFill, DrawingElementKind.Fill, 99) },
            0);
        if (staleResult.Success || staleScene.ObjectCount != staleCount || staleScene.ShapeKind[staleFill] != ShapeKind.Rectangle)
        {
            throw new InvalidOperationException("A stale selected PartIndex partially modified the scene.");
        }
    }

    private static void RunOutlinedFillMergeRegression()
    {
        var nearbyScene = new VectorScene();
        nearbyScene.CreateEmpty();
        nearbyScene.AddObject(
            0,
            new PointF(-54, 0),
            new SizeF(100, 80),
            0,
            0,
            Color.Teal,
            Color.Transparent,
            12,
            ShapeKind.Rectangle);
        var nearbySecond = nearbyScene.AddObject(
            0,
            new PointF(54, 0),
            new SizeF(100, 80),
            0,
            0,
            Color.Teal,
            Color.Transparent,
            12,
            ShapeKind.Rectangle);
        var nearbyMerged = nearbyScene.MergeSameColorFillsAround(nearbySecond, connectNearby: true, frame: 0);
        if (nearbyScene.ObjectCount != 1
            || (uint)nearbyMerged >= nearbyScene.ObjectCount
            || !nearbyScene.FillContainsPoint(nearbyMerged, new PointF(-54, 0))
            || !nearbyScene.FillContainsPoint(nearbyMerged, new PointF(54, 0)))
        {
            throw new InvalidOperationException("Same-color shape fills separated by 8 vu did not merge under the documented 10 vu rule.");
        }

        var translatedScene = new VectorScene();
        translatedScene.CreateEmpty();
        translatedScene.AddObject(
            0,
            new PointF(-54, 0),
            new SizeF(100, 80),
            0,
            0,
            Color.Teal,
            Color.Transparent,
            12,
            ShapeKind.Rectangle);
        var translatedSecond = translatedScene.AddObject(
            0,
            new PointF(54, 0),
            new SizeF(100, 80),
            0,
            0,
            Color.Teal,
            Color.Transparent,
            12,
            ShapeKind.Rectangle);
        translatedScene.MergeSameColorFillsAround(translatedSecond, connectNearby: false, frame: 0);
        if (translatedScene.ObjectCount != 2)
        {
            throw new InvalidOperationException("The translated-fill exception merged shapes that were separated by 8 vu.");
        }

        var scene = new VectorScene();
        scene.CreateEmpty();
        var outlined = AddTopologyFill(scene, 0, VectorUnits.StrokePointsToUnits(2));
        var outlinedOrder = scene.ObjectOrder[outlined];
        var overlappingFill = scene.AddObject(
            0,
            new PointF(160, 0),
            new SizeF(160, 100),
            0,
            0,
            Color.Teal,
            Color.Transparent,
            12,
            ShapeKind.Rectangle);
        var merged = scene.MergeSameColorFillsAround(overlappingFill, frame: 0);
        AssertShapeCounts(scene, 5, lines: 4, paths: 1, rectangles: 0, "outlined fill merge");
        if ((uint)merged >= scene.ObjectCount
            || scene.ShapeKind[merged] != ShapeKind.Path
            || scene.Stroke[merged] != 0
            || !scene.FillContainsPoint(merged, new PointF(-150, 0))
            || !scene.FillContainsPoint(merged, new PointF(220, 0)))
        {
            throw new InvalidOperationException("Merging an outlined fill did not preserve the expected union geometry.");
        }

        AssertLineSet(
            scene,
            new[]
            {
                (new PointF(-200, -120), new PointF(200, -120)),
                (new PointF(200, -120), new PointF(200, 120)),
                (new PointF(200, 120), new PointF(-200, 120)),
                (new PointF(-200, 120), new PointF(-200, -120))
            },
            "outlined fill merge");
        if (Enumerable.Range(0, scene.ObjectCount)
            .Where(index => scene.ShapeKind[index] == ShapeKind.Line)
            .Any(index => scene.ObjectOrder[index] != outlinedOrder))
        {
            throw new InvalidOperationException("Preserved fill boundaries did not inherit their original draw order.");
        }

        var preservedHostScene = new VectorScene();
        preservedHostScene.CreateEmpty();
        preservedHostScene.AddObject(
            0,
            new PointF(80, 0),
            new SizeF(160, 100),
            0,
            0,
            Color.Teal,
            Color.Transparent,
            12,
            ShapeKind.Rectangle);
        var topOutlinedEllipse = preservedHostScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(200, 120),
            0,
            VectorUnits.StrokePointsToUnits(2),
            Color.Teal,
            Color.White,
            12,
            ShapeKind.Ellipse);
        var preservedOrder = preservedHostScene.ObjectOrder[topOutlinedEllipse];
        var preservedSubOrder = preservedHostScene.ObjectSubOrder[topOutlinedEllipse];
        var preservedMerge = preservedHostScene.MergeSameColorFillsAround(topOutlinedEllipse, frame: 0);
        if ((uint)preservedMerge >= preservedHostScene.ObjectCount
            || preservedHostScene.ObjectOrder[preservedMerge] != preservedOrder
            || !preservedHostScene.ObjectSubOrder[preservedMerge].Equals(preservedSubOrder)
            || Enumerable.Range(0, preservedHostScene.ObjectCount)
                .GroupBy(index => (preservedHostScene.ObjectOrder[index], preservedHostScene.ObjectSubOrder[index]))
                .Any(group => group.Count() > 1))
        {
            throw new InvalidOperationException("Outlined Fill merge reused the preserved merged Fill drawing stack key.");
        }
    }

    private static void RunFillBoundaryOverlapNormalizationRegression()
    {
        static PathBezierNode[] CircleContour(float centerX, float radius)
        {
            var handle = radius * 0.55228475f;
            return
            [
                new PathBezierNode(
                    new PointF(centerX + radius, 0),
                    new PointF(centerX + radius, -handle),
                    new PointF(centerX + radius, handle)),
                new PathBezierNode(
                    new PointF(centerX, radius),
                    new PointF(centerX + handle, radius),
                    new PointF(centerX - handle, radius)),
                new PathBezierNode(
                    new PointF(centerX - radius, 0),
                    new PointF(centerX - radius, handle),
                    new PointF(centerX - radius, -handle)),
                new PathBezierNode(
                    new PointF(centerX, -radius),
                    new PointF(centerX - handle, -radius),
                    new PointF(centerX + handle, -radius))
            ];
        }

        var scene = new VectorScene();
        scene.CreateEmpty();
        var fill = scene.AddPathObjectContours(
            0,
            [
                [
                    new PointF(-240, -120),
                    new PointF(0, -120),
                    new PointF(0, 120),
                    new PointF(-240, 120)
                ],
                [
                    new PointF(-180, -50),
                    new PointF(-100, -50),
                    new PointF(-100, 50),
                    new PointF(-180, 50)
                ],
                [
                    new PointF(40, -80),
                    new PointF(240, -80),
                    new PointF(240, 80),
                    new PointF(40, 80)
                ]
            ],
            0,
            Color.Teal,
            Color.Transparent,
            12);
        if (fill < 0 || !scene.TryConvertFillToBezierPath(fill))
        {
            throw new InvalidOperationException("The Fill overlap regression could not create editable compound geometry.");
        }

        var referenceContours = scene.GetObjectBoundaryContours(fill);
        var movedStart = new PointF(-40, 80);
        var movedEnd = new PointF(-40, -80);
        if (!scene.SetPathBezierSegment(
                fill,
                7,
                movedStart,
                new PointF(-40, 80 - 160f / 3f),
                new PointF(-40, -80 + 160f / 3f),
                movedEnd,
                preserveStraightAdjacentSegments: true)
            || scene.FillContainsPoint(fill, new PointF(-20, 0))
            || !scene.NormalizeFillBoundaryOverlaps(fill, referenceContours)
            || scene.ObjectCount != 1
            || !scene.FillContainsPoint(fill, new PointF(-20, 0))
            || scene.FillContainsPoint(fill, new PointF(-140, 0))
            || !scene.FillContainsPoint(fill, new PointF(120, 0))
            || !scene.TryGetPathBezierWorldContours(fill, out var normalizedLinearContours)
            || normalizedLinearContours.Sum(contour => contour.Length) > 16)
        {
            throw new InvalidOperationException("Overlapping solid Fill contours were not united while preserving the existing hole.");
        }

        var holeScene = new VectorScene();
        holeScene.CreateEmpty();
        var holedFill = holeScene.AddPathObjectContours(
            0,
            [
                [
                    new PointF(-160, -100),
                    new PointF(160, -100),
                    new PointF(160, 100),
                    new PointF(-160, 100)
                ],
                [
                    new PointF(-50, -40),
                    new PointF(50, -40),
                    new PointF(50, 40),
                    new PointF(-50, 40)
                ]
            ],
            0,
            Color.Teal,
            Color.Transparent,
            8);
        holeScene.TryConvertFillToBezierPath(holedFill);
        var holeReference = holeScene.GetObjectBoundaryContours(holedFill);
        if (holeScene.NormalizeFillBoundaryOverlaps(holedFill, holeReference)
            || !holeScene.TryGetPathBezierWorldContours(holedFill, out _)
            || holeScene.FillContainsPoint(holedFill, PointF.Empty))
        {
            throw new InvalidOperationException("A valid compound Fill hole was normalized or flattened without an overlap.");
        }

        var curvedScene = new VectorScene();
        curvedScene.CreateEmpty();
        var leftCircle = CircleContour(-60, 120);
        var rightCircle = CircleContour(60, 120);
        var curvedFill = curvedScene.AppendPathBezierObjectContours(
            0,
            [leftCircle, rightCircle],
            0,
            Color.Teal,
            Color.Transparent,
            16);
        curvedScene.CompleteDeferredBuild();
        var curvedReference = curvedScene.GetObjectBoundaryContours(curvedFill);
        if (curvedFill < 0
            || curvedScene.FillContainsPoint(curvedFill, PointF.Empty)
            || !curvedScene.NormalizeFillBoundaryOverlaps(curvedFill, curvedReference)
            || !curvedScene.FillContainsPoint(curvedFill, PointF.Empty)
            || !curvedScene.FillContainsPoint(curvedFill, new PointF(-150, 0))
            || !curvedScene.FillContainsPoint(curvedFill, new PointF(150, 0))
            || !curvedScene.TryGetPathBezierWorldContours(curvedFill, out var normalizedCurves)
            || normalizedCurves.Sum(contour => contour.Length) > 12)
        {
            throw new InvalidOperationException(
                $"Curved overlap normalization flattened exact cubics or retained sampled nodes: nodes={(curvedScene.TryGetPathBezierWorldContours(curvedFill, out var failedCurves) ? failedCurves.Sum(contour => contour.Length) : -1)}.");
        }

        var expectedHandle = 120 * 0.55228475f;
        var preservedOuterNode = normalizedCurves
            .SelectMany(contour => contour)
            .FirstOrDefault(node => PointsWithin(node.Anchor, new PointF(180, 0), 1f));
        var controlsPreserved = PointsWithin(
                preservedOuterNode.IncomingControl,
                new PointF(180, -expectedHandle),
                1f)
            && PointsWithin(
                preservedOuterNode.OutgoingControl,
                new PointF(180, expectedHandle),
                1f)
            || PointsWithin(
                preservedOuterNode.IncomingControl,
                new PointF(180, expectedHandle),
                1f)
            && PointsWithin(
                preservedOuterNode.OutgoingControl,
                new PointF(180, -expectedHandle),
                1f);
        var normalizedNodeCount = normalizedCurves.Sum(contour => contour.Length);
        if (!controlsPreserved
            || curvedScene.NormalizeFillBoundaryOverlaps(curvedFill, curvedReference)
            || !curvedScene.TryGetPathBezierWorldContours(curvedFill, out var idempotentCurves)
            || idempotentCurves.Sum(contour => contour.Length) != normalizedNodeCount)
        {
            throw new InvalidOperationException(
                "Curved overlap normalization did not preserve an untouched cubic or was not idempotent.");
        }

        var curvedMergeScene = new VectorScene();
        curvedMergeScene.CreateEmpty();
        curvedMergeScene.AppendPathBezierObjectContours(
            0,
            [leftCircle],
            0,
            Color.Teal,
            Color.Transparent,
            8);
        var curvedMergeSource = curvedMergeScene.AppendPathBezierObjectContours(
            0,
            [rightCircle],
            0,
            Color.Teal,
            Color.Transparent,
            8);
        curvedMergeScene.CompleteDeferredBuild();
        var curvedMerged = curvedMergeScene.MergeSameColorFillsAround(
            curvedMergeSource,
            connectNearby: false,
            frame: 0);
        if (curvedMergeScene.ObjectCount != 1
            || (uint)curvedMerged >= curvedMergeScene.ObjectCount
            || !curvedMergeScene.FillContainsPoint(curvedMerged, PointF.Empty)
            || !curvedMergeScene.TryGetPathBezierWorldContours(curvedMerged, out var mergedCurves)
            || mergedCurves.Sum(contour => contour.Length) > 12)
        {
            throw new InvalidOperationException(
                "Same-color curved Fill merging flattened exact cubics into sampled path nodes.");
        }
    }

    private static void RunMarqueeElementQueryRegression()
    {
        var partialScene = new VectorScene();
        partialScene.CreateEmpty();
        AddTopologyFill(partialScene, 0, 0);
        var partialHits = partialScene.QueryDrawingElementsInsideBounds(new RectangleF(-40, -40, 80, 80), 0);
        if (partialHits.Length != 0)
        {
            throw new InvalidOperationException("A marquee contained inside an unsplit fill selected the whole fill region.");
        }

        var splitFillScene = new VectorScene();
        splitFillScene.CreateEmpty();
        var splitFill = AddTopologyFill(splitFillScene, 0, 0);
        AddTopologyLine(splitFillScene, 0, new PointF(-320, 0), new PointF(320, 0));
        var upperHits = splitFillScene.QueryDrawingElementsInsideBounds(new RectangleF(-210, -130, 420, 140), 0);
        if (upperHits.Count(hit => hit.Key.ObjectIndex == splitFill && hit.Key.Kind == DrawingElementKind.Fill) != 1
            || upperHits.Any(hit => hit.Key.ObjectIndex == splitFill && hit.Key.Kind == DrawingElementKind.Fill && hit.Key.PartIndex != 0))
        {
            throw new InvalidOperationException("Geometry-driven marquee selection did not isolate the fully enclosed fill region.");
        }

        var strokeScene = new VectorScene();
        strokeScene.CreateEmpty();
        var horizontal = AddTopologyLine(strokeScene, 0, new PointF(-200, 0), new PointF(200, 0));
        AddTopologyLine(strokeScene, 0, new PointF(0, -160), new PointF(0, 160));
        var strokeHits = strokeScene.QueryDrawingElementsInsideBounds(new RectangleF(-5, -10, 210, 20), 0);
        if (strokeHits.Count(hit => hit.Key.ObjectIndex == horizontal && hit.Key.Kind == DrawingElementKind.Stroke) != 1
            || strokeHits.Any(hit => hit.Key.ObjectIndex == horizontal && hit.Key.PartIndex != 1))
        {
            throw new InvalidOperationException("Geometry-driven marquee selection missed a fully enclosed thin stroke segment.");
        }

        var curvedScene = new VectorScene();
        curvedScene.CreateEmpty();
        var curvedStroke = curvedScene.AddCurveSegment(
            0,
            new PointF(-80, 0),
            new PointF(0, 120),
            new PointF(80, 0),
            VectorUnits.StrokePointsToUnits(2),
            Color.Transparent,
            Color.White,
            6);
        var curvedBounds = new RectangleF(-100, -100, 200, 200);
        var curvedHits = curvedScene.QueryDrawingElementsInsideBounds(curvedBounds, 0);
        if (!curvedScene.IsObjectGeometryInsideBounds(curvedStroke, curvedBounds)
            || curvedHits.Count(hit => hit.Key.ObjectIndex == curvedStroke && hit.Key.Kind == DrawingElementKind.Stroke) != 1)
        {
            throw new InvalidOperationException("Geometry-driven marquee selection rejected a contained curve because its control point was outside the marquee.");
        }
    }

    private static void RunMarqueeLineMaterializationRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        var source = AddTopologyLine(scene, 0, new PointF(-200, 0), new PointF(200, 0));
        var sourceOrder = scene.ObjectOrder[source];
        var bounds = new RectangleF(-50, -20, 100, 40);
        var materialized = scene.MaterializeMarqueeLineParts(bounds, 0);

        if (!materialized.Changed
            || materialized.SelectedObjects.Length != 1
            || !LineMatches(scene, materialized.SelectedObjects[0], new PointF(-50, 0), new PointF(50, 0)))
        {
            var selectedDescription = string.Join(
                "; ",
                materialized.SelectedObjects.Select(index =>
                {
                    scene.TryGetLineEndpoint(index, true, out var start);
                    scene.TryGetLineEndpoint(index, false, out var end);
                    return $"{index}:{start}->{end}";
                }));
            throw new InvalidOperationException(
                $"Marquee line materialization did not isolate the selected interior segment: "
                + $"changed={materialized.Changed}, selected={selectedDescription}, objects={scene.ObjectCount}, "
                + $"candidates={scene.QueryObjects(bounds, 0).Length}, straight={scene.IsLineStraight(source)}, "
                + $"controls={scene.CurveControlX[source]},{scene.CurveControlY[source]}"
                + $"/{scene.CurveControl2X[source]},{scene.CurveControl2Y[source]}.");
        }

        AssertLineSet(
            scene,
            new[]
            {
                (new PointF(-200, 0), new PointF(-50, 0)),
                (new PointF(-50, 0), new PointF(50, 0)),
                (new PointF(50, 0), new PointF(200, 0))
            },
            "marquee line materialization");

        var replacementLines = Enumerable.Range(0, scene.ObjectCount)
            .Where(index => scene.ShapeKind[index] == ShapeKind.Line)
            .ToArray();
        if (replacementLines.Length != 3
            || replacementLines.Any(index => scene.ObjectOrder[index] != sourceOrder)
            || replacementLines.Select(index => scene.ObjectSubOrder[index]).Distinct().Count() != 3
            || replacementLines.Any(index => scene.ObjectKeyframeFrame[index] != 0))
        {
            throw new InvalidOperationException("Marquee line materialization did not preserve drawing order or keyframe ownership.");
        }
    }

    private static void RunMarqueeFillMaterializationRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        scene.AddObject(0, PointF.Empty, new SizeF(240, 160), 0, 0, Color.Teal, Color.Transparent, 12, ShapeKind.Rectangle);
        var materialized = scene.MaterializeMarqueeFillParts(new RectangleF(-40, -30, 80, 60), 0);
        var selected = materialized.SelectedObjects.SingleOrDefault(-1);
        var outside = Enumerable.Range(0, scene.ObjectCount).FirstOrDefault(index => index != selected, -1);
        if (!materialized.Changed
            || materialized.SelectedObjects.Length != 1
            || (uint)selected >= scene.ObjectCount
            || (uint)outside >= scene.ObjectCount
            || scene.ObjectCount != 2
            || scene.ShapeKind[selected] != ShapeKind.Path
            || scene.ShapeKind[outside] != ShapeKind.Path
            || !scene.FillContainsPoint(selected, PointF.Empty)
            || scene.FillContainsPoint(selected, new PointF(80, 0))
            || scene.FillContainsPoint(outside, PointF.Empty)
            || !scene.FillContainsPoint(outside, new PointF(80, 0)))
        {
            throw new InvalidOperationException("Marquee fill materialization did not preserve the outside region as one compound path with a marquee hole.");
        }

        var gradientScene = new VectorScene();
        gradientScene.CreateEmpty();
        var gradientSource = gradientScene.AddObject(0, PointF.Empty, new SizeF(240, 160), 0, 0, Color.Teal, Color.Transparent, 12, ShapeKind.Rectangle);
        var gradientStops = new[]
        {
            new GradientStop(0, Color.Teal),
            new GradientStop(0.4f, Color.Gold),
            new GradientStop(1, Color.MediumPurple)
        };
        var gradientStart = new PointF(0, 0);
        var gradientEnd = new PointF(120, 0);
        gradientScene.SetGradientPaint(gradientSource, GradientKind.Radial, gradientStops, gradientStart, gradientEnd);
        var gradientMaterialization = gradientScene.MaterializeMarqueeFillParts(new RectangleF(-40, -30, 80, 60), 0);
        var selectedGradient = gradientMaterialization.SelectedObjects.SingleOrDefault(-1);
        var outsideGradient = Enumerable.Range(0, gradientScene.ObjectCount).FirstOrDefault(index => index != selectedGradient, -1);
        if (!gradientMaterialization.Changed
            || gradientScene.ObjectCount != 2
            || (uint)selectedGradient >= gradientScene.ObjectCount
            || (uint)outsideGradient >= gradientScene.ObjectCount
            || !gradientScene.HasGradient(selectedGradient)
            || !gradientScene.HasGradient(outsideGradient)
            || gradientScene.GetGradientKind(selectedGradient) != GradientKind.Radial
            || gradientScene.GetGradientKind(outsideGradient) != GradientKind.Radial
            || !gradientScene.GetGradientStops(selectedGradient).SequenceEqual(gradientStops)
            || !gradientScene.GetGradientStops(outsideGradient).SequenceEqual(gradientStops)
            || gradientScene.GetGradientStart(selectedGradient) != gradientStart
            || gradientScene.GetGradientEnd(outsideGradient) != gradientEnd)
        {
            throw new InvalidOperationException("Marquee fill materialization discarded the gradient material of an extracted fill.");
        }

        var curvedScene = new VectorScene();
        curvedScene.CreateEmpty();
        var curvedFill = curvedScene.AddPathObject(
            0,
            [
                new PointF(-200, -80),
                new PointF(200, -80),
                new PointF(200, 160),
                new PointF(-200, 160)
            ],
            0,
            Color.MediumAquamarine,
            Color.Transparent,
            12);
        if (!curvedScene.TryConvertFillToBezierPath(curvedFill)
            || !curvedScene.TryGetPathBezierSegment(curvedFill, 0, out var curvedTop)
            || !curvedScene.SetPathBezierSegment(
                curvedFill,
                curvedTop.PartIndex,
                curvedTop.Start,
                new PointF(-80, -190),
                new PointF(80, -40),
                curvedTop.End))
        {
            throw new InvalidOperationException("The curved marquee Fill regression could not prepare an exact cubic edge.");
        }

        var curvedMarqueeBounds = new RectangleF(-70, -220, 140, 260);
        var curvedMaterialization = curvedScene.MaterializeMarqueeFillParts(curvedMarqueeBounds, 0);
        var selectedCurveFill = curvedMaterialization.SelectedObjects.SingleOrDefault(-1);
        var outsideCurveFill = Enumerable.Range(0, curvedScene.ObjectCount)
            .FirstOrDefault(index => index != selectedCurveFill, -1);
        var selectedCurveParts = (uint)selectedCurveFill < curvedScene.ObjectCount
            ? curvedScene.GetEditableFillBezierSegmentParts(selectedCurveFill)
            : [];
        var activeCurvePart = selectedCurveParts.FirstOrDefault();
        var activeLinearControl1 = new PointF(
            activeCurvePart.Start.X + (activeCurvePart.End.X - activeCurvePart.Start.X) / 3f,
            activeCurvePart.Start.Y + (activeCurvePart.End.Y - activeCurvePart.Start.Y) / 3f);
        var activeLinearControl2 = new PointF(
            activeCurvePart.Start.X + (activeCurvePart.End.X - activeCurvePart.Start.X) * 2f / 3f,
            activeCurvePart.Start.Y + (activeCurvePart.End.Y - activeCurvePart.Start.Y) * 2f / 3f);
        var activeCurveDeviation = selectedCurveParts.Length == 0
            ? 0
            : Math.Max(
                Math.Abs(activeCurvePart.Control1.X - activeLinearControl1.X)
                    + Math.Abs(activeCurvePart.Control1.Y - activeLinearControl1.Y),
                Math.Abs(activeCurvePart.Control2.X - activeLinearControl2.X)
                    + Math.Abs(activeCurvePart.Control2.Y - activeLinearControl2.Y));
        if (!curvedMaterialization.Changed
            || curvedMaterialization.SelectedObjects.Length != 1
            || curvedScene.ObjectCount != 2
            || (uint)selectedCurveFill >= curvedScene.ObjectCount
            || (uint)outsideCurveFill >= curvedScene.ObjectCount
            || !curvedScene.TryGetPathBezierWorldContours(selectedCurveFill, out _)
            || !curvedScene.TryGetPathBezierWorldContours(outsideCurveFill, out _)
            || selectedCurveParts.Length != 4
            || activeCurveDeviation <= 1
            || activeCurvePart.Start.X < curvedMarqueeBounds.Left - 2
            || activeCurvePart.End.X > curvedMarqueeBounds.Right + 2)
        {
            throw new InvalidOperationException(
                "Marquee Fill materialization flattened or failed to prioritize the selected cubic subcurve.");
        }

        var adjustedControl = new PointF(activeCurvePart.Control1.X, activeCurvePart.Control1.Y - 24);
        if (!curvedScene.SetPathBezierSegment(
                selectedCurveFill,
                activeCurvePart.PartIndex,
                activeCurvePart.Start,
                adjustedControl,
                activeCurvePart.Control2,
                activeCurvePart.End)
            || !curvedScene.TryGetPathBezierSegment(selectedCurveFill, activeCurvePart.PartIndex, out var adjustedCurvePart)
            || !PointsNear(adjustedCurvePart.Control1, VectorUnits.Quantize(adjustedControl)))
        {
            throw new InvalidOperationException("A marquee-selected cubic Fill subcurve was not directly editable.");
        }

        using (var curvedStage = new StageControl(curvedScene) { Size = new Size(640, 420) })
        {
            var overlayParts = curvedScene.GetEditableFillBezierSegmentParts(selectedCurveFill);
            curvedStage.RestoreViewState(curvedStage.CaptureViewState() with { Zoom = 32f });
            curvedStage.SetSelection([selectedCurveFill], selectedCurveFill);
            curvedStage.SetFillEdgeBezierOverlay(
                selectedCurveFill,
                overlayParts.Select(part => new FillEdgeBezierOverlaySegment(
                    part.PartIndex,
                    part.Start,
                    part.Control1,
                    part.Control2,
                    part.End)).ToArray(),
                overlayParts[0].PartIndex);
            var firstControlHit = curvedStage.HitTestFillEdgeBezierOverlay(
                Point.Round(curvedStage.WorldToScreen(overlayParts[0].Control1.X, overlayParts[0].Control1.Y)));
            var secondControlHit = curvedStage.HitTestFillEdgeBezierOverlay(
                Point.Round(curvedStage.WorldToScreen(overlayParts[0].Control2.X, overlayParts[0].Control2.Y)));
            if (firstControlHit.Handle != EditHandleKind.BezierControl
                || secondControlHit.Handle != EditHandleKind.BezierControl2)
            {
                throw new InvalidOperationException("A marquee-selected cubic Fill subcurve did not expose two distinct Stage controls.");
            }
        }

        var outlinedCurveScene = new VectorScene();
        outlinedCurveScene.CreateEmpty();
        var outlinedCurveFill = outlinedCurveScene.AddPathObject(
            0,
            [
                new PointF(-200, -80),
                new PointF(200, -80),
                new PointF(200, 160),
                new PointF(-200, 160)
            ],
            VectorUnits.StrokePointsToUnits(2),
            Color.MediumAquamarine,
            Color.White,
            12);
        if (!outlinedCurveScene.TryConvertFillToBezierPath(outlinedCurveFill)
            || !outlinedCurveScene.TryGetPathBezierSegment(outlinedCurveFill, 0, out var outlinedTop)
            || !outlinedCurveScene.SetPathBezierSegment(
                outlinedCurveFill,
                outlinedTop.PartIndex,
                outlinedTop.Start,
                new PointF(-80, -190),
                new PointF(80, -40),
                outlinedTop.End))
        {
            throw new InvalidOperationException("The outlined marquee regression could not prepare an exact cubic edge.");
        }

        var outlinedMaterialization = outlinedCurveScene.MaterializeMarqueeSelectionParts(curvedMarqueeBounds, 0);
        var selectedOutlineCurves = outlinedMaterialization.SelectedObjects
            .Where(index => (uint)index < outlinedCurveScene.ObjectCount
                && outlinedCurveScene.ShapeKind[index] == ShapeKind.Line)
            .ToArray();
        var retainedFreehandOutlines = Enumerable.Range(0, outlinedCurveScene.ObjectCount)
            .Any(index => outlinedCurveScene.ShapeKind[index] == ShapeKind.Freeform
                && outlinedCurveScene.Stroke[index] > 0);
        var selectedOutlineIsCurved = selectedOutlineCurves.Any(index =>
            outlinedCurveScene.TryGetLineBezierPart(
                index,
                0,
                1,
                out var start,
                out var control1,
                out var control2,
                out var end)
            && (Math.Abs(control1.Y - (start.Y + (end.Y - start.Y) / 3f)) > 1
                || Math.Abs(control2.Y - (start.Y + (end.Y - start.Y) * 2f / 3f)) > 1));
        if (!outlinedMaterialization.Changed
            || selectedOutlineCurves.Length == 0
            || retainedFreehandOutlines
            || !selectedOutlineIsCurved)
        {
            throw new InvalidOperationException("Marquee materialization flattened an outlined Path cubic subcurve.");
        }
    }

    private static void RunMovedFillIsolationRegression()
    {
        var mergeScene = new VectorScene();
        mergeScene.CreateEmpty();
        mergeScene.AddObject(
            0,
            new PointF(-54, 0),
            new SizeF(100, 80),
            0,
            0,
            Color.Teal,
            Color.Transparent,
            12,
            ShapeKind.Rectangle);
        var movingFill = mergeScene.AddObject(
            0,
            new PointF(154, 0),
            new SizeF(100, 80),
            0,
            0,
            Color.Teal,
            Color.Transparent,
            12,
            ShapeKind.Rectangle);
        mergeScene.TranslateObjectsForPreview([movingFill], -100, 0);
        mergeScene.CompleteDeferredBuild();
        movingFill = mergeScene.ApplyFillOverwriteToNewObjects([movingFill], 0).SingleOrDefault(-1);
        movingFill = mergeScene.MergeSameColorFillsAround(movingFill, connectNearby: true, frame: 0);
        if (mergeScene.ObjectCount != 1
            || (uint)movingFill >= mergeScene.ObjectCount
            || !mergeScene.FillContainsPoint(movingFill, new PointF(-54, 0))
            || !mergeScene.FillContainsPoint(movingFill, new PointF(54, 0)))
        {
            throw new InvalidOperationException("Moving a same-color fill to within 8 vu of another fill did not merge them.");
        }

        var scene = new VectorScene();
        scene.CreateEmpty();
        scene.AddObject(
            0,
            PointF.Empty,
            new SizeF(240, 160),
            0,
            VectorUnits.StrokePointsToUnits(2),
            Color.Teal,
            Color.White,
            12,
            ShapeKind.Rectangle);
        var materialized = scene.MaterializeMarqueeFillParts(new RectangleF(40, -30, 120, 60), 0);
        var movedObjects = materialized.SelectedObjects.ToHashSet();
        var moved = movedObjects.SingleOrDefault(
            index => (uint)index < scene.ObjectCount && scene.ShapeKind[index] == ShapeKind.Path,
            -1);
        var movedBoundaryLines = movedObjects
            .Where(index => (uint)index < scene.ObjectCount && scene.ShapeKind[index] == ShapeKind.Line)
            .ToArray();
        if (!materialized.Changed
            || (uint)moved >= scene.ObjectCount
            || movedBoundaryLines.Length != 1)
        {
            throw new InvalidOperationException(
                $"Moved fill isolation regression could not materialize an outlined rectangle selection: moved={moved}, boundaries={movedBoundaryLines.Length}.");
        }

        var boundaryLinks = movedBoundaryLines
            .SelectMany(index => scene.CaptureFillBoundaryLineLinks(index, 0))
            .ToArray();
        var retainedLinks = MainForm.ExcludeTranslatedFillBoundaryLinks(boundaryLinks, movedObjects);
        if (!boundaryLinks.Any(link => link.FillObjectIndex == moved)
            || retainedLinks.Any(link => link.FillObjectIndex == moved))
        {
            throw new InvalidOperationException("A marquee-selected fill retained a boundary link that would reapply its translation as a contour edit.");
        }

        var movedBoundsBefore = scene.GetObjectWorldBounds(moved);
        var unfilteredSnapshot = scene.CreateSnapshot();
        scene.TranslateObjectsForPreview(materialized.SelectedObjects, 100, 0);
        scene.UpdateFillBoundaryLineLinks(boundaryLinks, rebuildGeometryIndex: false);
        scene.CompleteDeferredBuild();
        var unfilteredBounds = scene.GetObjectWorldBounds(moved);
        scene.RestoreSnapshot(unfilteredSnapshot);
        scene.EditFrame = 0;
        if (unfilteredBounds.Width <= movedBoundsBefore.Width + DrawingTopologyRules.UnitIntersectionTolerance)
        {
            var movedLinkSummary = string.Join(
                ",",
                boundaryLinks
                    .Select(link => $"line={link.LineObjectIndex}/fill={link.FillObjectIndex}/segment={link.SegmentIndex}+{link.SegmentCount}/reverse={link.Reversed}"));
            throw new InvalidOperationException(
                $"Moved fill isolation regression did not reproduce the unfiltered boundary-link extension: before={movedBoundsBefore}, after={unfilteredBounds}, links={movedLinkSummary}.");
        }

        scene.TranslateObjectsForPreview(materialized.SelectedObjects, 100, 0);
        scene.UpdateFillBoundaryLineLinks(retainedLinks, rebuildGeometryIndex: false);
        scene.CompleteDeferredBuild();
        moved = scene.ApplyFillOverwriteToNewObjects([moved], 0).SingleOrDefault(-1);
        moved = scene.MergeSameColorFillsAround(moved, connectNearby: true, frame: 0);

        var outside = Enumerable.Range(0, scene.ObjectCount).SingleOrDefault(
            index => index != moved && scene.ShapeKind[index] == ShapeKind.Path,
            -1);
        var movedBounds = (uint)moved < scene.ObjectCount
            ? scene.GetObjectWorldBounds(moved)
            : RectangleF.Empty;
        if ((uint)moved >= scene.ObjectCount
            || (uint)outside >= scene.ObjectCount
            || Math.Abs(movedBounds.Left - (movedBoundsBefore.Left + 100)) > DrawingTopologyRules.UnitIntersectionTolerance
            || Math.Abs(movedBounds.Top - movedBoundsBefore.Top) > DrawingTopologyRules.UnitIntersectionTolerance
            || Math.Abs(movedBounds.Width - movedBoundsBefore.Width) > DrawingTopologyRules.UnitIntersectionTolerance
            || Math.Abs(movedBounds.Height - movedBoundsBefore.Height) > DrawingTopologyRules.UnitIntersectionTolerance
            || !scene.FillContainsPoint(moved, new PointF(160, 0))
            || scene.FillContainsPoint(moved, new PointF(60, 0))
            || scene.FillContainsPoint(outside, new PointF(60, 0))
            || !scene.FillContainsPoint(outside, new PointF(-60, 0)))
        {
            throw new InvalidOperationException(
                $"A translated outlined marquee fill extended back into its source region: objects={scene.ObjectCount}, moved={moved}, outside={outside}, bounds={movedBounds}.");
        }

        Console.WriteLine("moved_fill_isolation_regression=ok");
    }

    private static void RunMarqueeOutlinedBoundarySelectionRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        var stroke = VectorUnits.StrokePointsToUnits(2);
        scene.AddObject(0, PointF.Empty, new SizeF(240, 160), 0, stroke, Color.Teal, Color.White, 12, ShapeKind.Rectangle);

        var materialized = scene.MaterializeMarqueeFillParts(new RectangleF(-40, -100, 80, 200), 0);
        var selectedLines = materialized.SelectedObjects
            .Where(index => (uint)index < scene.ObjectCount && scene.ShapeKind[index] == ShapeKind.Line)
            .ToArray();
        var selectedFill = materialized.SelectedObjects
            .SingleOrDefault(index => (uint)index < scene.ObjectCount && scene.ShapeKind[index] == ShapeKind.Path, -1);

        if (!materialized.Changed
            || materialized.SelectedObjects.Length != 3
            || selectedLines.Length != 2
            || selectedFill < 0
            || !scene.FillContainsPoint(selectedFill, PointF.Empty)
            || !selectedLines.Any(index => LineMatches(scene, index, new PointF(-40, -80), new PointF(40, -80)))
            || !selectedLines.Any(index => LineMatches(scene, index, new PointF(-40, 80), new PointF(40, 80))))
        {
            throw new InvalidOperationException("Marquee fill materialization did not split and select the enclosed outlined boundary segments.");
        }

        var outlinedSelection = selectedLines.Append(selectedFill).ToArray();
        if (!MainForm.ShouldMoveMarqueeStrokeIndependently(outlinedSelection, selectedFill, selectedLines)
            || !MainForm.ShouldMoveMarqueeStrokeIndependently(selectedLines, selectedLines[0], selectedLines)
            || MainForm.ShouldMoveMarqueeStrokeIndependently([selectedFill], selectedFill, selectedLines))
        {
            throw new InvalidOperationException("Marquee-selected boundary strokes were not isolated from linked fill deformation during drag.");
        }

        var ellipseScene = new VectorScene();
        ellipseScene.CreateEmpty();
        var untouched = ellipseScene.AddLineSegment(
            0,
            new PointF(-120, 1_000),
            new PointF(120, 1_000),
            stroke,
            Color.Transparent,
            Color.White,
            6);
        var ellipse = ellipseScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(2_000, 1_200),
            0,
            stroke,
            Color.Teal,
            Color.White,
            48,
            ShapeKind.Ellipse);
        var crossingLine = ellipseScene.AddLineSegment(
            0,
            new PointF(-1_200, 0),
            new PointF(1_200, 0),
            stroke,
            Color.Transparent,
            Color.Gold,
            12);
        var ellipseBoundary = ellipseScene.GetShapeBoundary(ellipse);
        var cardinalParts = ellipseScene.GetBoundaryParts(ellipse, 0)
            .OrderBy(part => part.PartIndex)
            .ToArray();
        var expectedCardinalEndpoints = new[]
        {
            (new PointF(0, -600), new PointF(1_000, 0)),
            (new PointF(1_000, 0), new PointF(0, 600)),
            (new PointF(0, 600), new PointF(-1_000, 0)),
            (new PointF(-1_000, 0), new PointF(0, -600))
        };
        var hasCardinalCubicParts = cardinalParts.Length == expectedCardinalEndpoints.Length
            && cardinalParts.Zip(expectedCardinalEndpoints).All(pair =>
                pair.First.Points.Length > 2
                && PointsNear(pair.First.Points[0], pair.Second.Item1)
                && PointsNear(pair.First.Points[^1], pair.Second.Item2));
        if (ellipseBoundary.Length <= 65
            || ellipseBoundary[0] != ellipseBoundary[^1]
            || !PointsNear(ellipseBoundary[0], new PointF(0, -600))
            || !hasCardinalCubicParts)
        {
            throw new InvalidOperationException("Ellipse topology did not use four closed top/right/bottom/left cubic boundary segments.");
        }

        var combined = ellipseScene.MaterializeMarqueeSelectionParts(new RectangleF(-200, -700, 400, 1_400), 0);
        var remappedUntouched = (uint)untouched < combined.OldToNewObjectIndex.Length
            ? combined.OldToNewObjectIndex[untouched]
            : -1;
        var ellipseStrokes = Enumerable.Range(0, ellipseScene.ObjectCount)
            .Where(index => index != remappedUntouched
                && ellipseScene.ShapeKind[index] == ShapeKind.Line
                && ellipseScene.Stroke[index] > 0
                && ellipseScene.StrokeArgb[index] == Color.White.ToArgb())
            .ToArray();
        var flattenedEllipseStrokes = Enumerable.Range(0, ellipseScene.ObjectCount)
            .Where(index => ellipseScene.ShapeKind[index] == ShapeKind.Freeform
                && ellipseScene.Stroke[index] > 0
                && ellipseScene.StrokeArgb[index] == Color.White.ToArgb())
            .ToArray();
        var selectedEllipseStrokes = combined.SelectedObjects
            .Where(index => ellipseStrokes.Contains(index))
            .ToArray();
        var ellipseCurves = ellipseStrokes
            .Select(index => ellipseScene.TryGetLineBezierPart(
                index,
                0,
                1,
                out var start,
                out var control1,
                out var control2,
                out var end)
                ? (Valid: true, Start: start, Control1: control1, Control2: control2, End: end)
                : default)
            .ToArray();
        var ellipsePoints = ellipseCurves
            .Where(curve => curve.Valid)
            .SelectMany(curve => Enumerable.Range(0, 17).Select(sample => EvaluateCubic(
                curve.Start,
                curve.Control1,
                curve.Control2,
                curve.End,
                sample / 16f)))
            .ToArray();
        var maximumEllipseEquationError = ellipsePoints.Length == 0
            ? float.PositiveInfinity
            : ellipsePoints.Max(point => Math.Abs(
                point.X * point.X / (1_000f * 1_000f)
                + point.Y * point.Y / (600f * 600f)
                - 1));
        var selectedCurvesRemainEditable = selectedEllipseStrokes.All(index =>
            ellipseScene.TryGetLineBezierPart(index, 0, 1, out var start, out var control1, out var control2, out var end)
            && (DistanceFromChord(control1, start, end) > 0.1f
                || DistanceFromChord(control2, start, end) > 0.1f));
        var selectedEllipseFill = combined.SelectedObjects.Any(index =>
            (uint)index < ellipseScene.ObjectCount
            && ellipseScene.ShapeKind[index] == ShapeKind.Path
            && ellipseScene.FillContainsPoint(index, PointF.Empty));
        var orderedMarqueeSelection = MainForm.OrderMarqueeSelectionForCurveEditing(
            ellipseScene,
            combined.SelectedObjects,
            combined.SelectedObjects);
        var exposesCurveAdjustmentTools = orderedMarqueeSelection.Length > 0
            && selectedEllipseStrokes.Contains(orderedMarqueeSelection[^1]);
        if (!combined.Success
            || !combined.Changed
            || (uint)untouched >= combined.OldToNewObjectIndex.Length
            || combined.OldToNewObjectIndex[untouched] < 0
            || combined.OldToNewObjectIndex[ellipse] != -1
            || combined.OldToNewObjectIndex[crossingLine] != -1
            || ellipseStrokes.Length < 8
            || flattenedEllipseStrokes.Length != 0
            || selectedEllipseStrokes.Length != 4
            || ellipseCurves.Any(curve => !curve.Valid)
            || !selectedCurvesRemainEditable
            || !selectedEllipseFill
            || !exposesCurveAdjustmentTools
            || maximumEllipseEquationError > 0.005f)
        {
            throw new InvalidOperationException(
                $"Combined marquee materialization lost cubic ellipse geometry or fill boundaries: "
                + $"success={combined.Success}, changed={combined.Changed}, strokes={ellipseStrokes.Length}, "
                + $"flattened={flattenedEllipseStrokes.Length}, selected={selectedEllipseStrokes.Length}, "
                + $"editable={selectedCurvesRemainEditable}, fill={selectedEllipseFill}, "
                + $"handles={exposesCurveAdjustmentTools}, error={maximumEllipseEquationError:0.######}.");
        }

        static PointF EvaluateCubic(PointF start, PointF control1, PointF control2, PointF end, float t)
        {
            var inverse = 1 - t;
            return new PointF(
                inverse * inverse * inverse * start.X
                    + 3 * inverse * inverse * t * control1.X
                    + 3 * inverse * t * t * control2.X
                    + t * t * t * end.X,
                inverse * inverse * inverse * start.Y
                    + 3 * inverse * inverse * t * control1.Y
                    + 3 * inverse * t * t * control2.Y
                    + t * t * t * end.Y);
        }

        static float DistanceFromChord(PointF point, PointF start, PointF end)
        {
            var dx = end.X - start.X;
            var dy = end.Y - start.Y;
            var length = MathF.Sqrt(dx * dx + dy * dy);
            return length <= 0.0001f
                ? 0
                : Math.Abs((point.X - start.X) * dy - (point.Y - start.Y) * dx) / length;
        }
    }

    private static int AddTopologyFill(VectorScene scene, int layer, float stroke)
    {
        return scene.AddObject(layer, PointF.Empty, new SizeF(400, 240), 0, stroke, Color.Teal, Color.White, 12, ShapeKind.Rectangle);
    }

    private static int AddTopologyLine(VectorScene scene, int layer, PointF start, PointF end)
    {
        return scene.AddLineSegment(layer, start, end, VectorUnits.StrokePointsToUnits(2), Color.Transparent, Color.White, 6);
    }

    private static DrawingElementHit AssertTopologyHit(
        VectorScene scene,
        PointF point,
        int objectIndex,
        DrawingElementKind kind,
        int partIndex,
        float startT,
        float endT,
        string context)
    {
        var hit = scene.HitTestElement(point, 0, 0);
        if (!hit.IsValid
            || hit.Key.ObjectIndex != objectIndex
            || hit.Key.Kind != kind
            || hit.Key.PartIndex != partIndex
            || !NearlyEqual(hit.StartT, startT)
            || !NearlyEqual(hit.EndT, endT))
        {
            throw new InvalidOperationException(
                $"{context} hit mismatch at ({point.X:0.###},{point.Y:0.###}): expected object={objectIndex}, kind={kind}, part={partIndex}, t={startT:0.####}-{endT:0.####}; actual key={hit.Key}, t={hit.StartT:0.####}-{hit.EndT:0.####}.");
        }

        return hit;
    }

    private static void AssertShapeCounts(VectorScene scene, int objects, int lines, int paths, int rectangles, string context)
    {
        var actualLines = 0;
        var actualPaths = 0;
        var actualRectangles = 0;
        for (var i = 0; i < scene.ObjectCount; i++)
        {
            switch (scene.ShapeKind[i])
            {
                case ShapeKind.Line:
                    actualLines++;
                    break;
                case ShapeKind.Path:
                    actualPaths++;
                    break;
                case ShapeKind.Rectangle:
                    actualRectangles++;
                    break;
            }
        }

        if (scene.ObjectCount != objects || actualLines != lines || actualPaths != paths || actualRectangles != rectangles)
        {
            throw new InvalidOperationException(
                $"{context} shape counts mismatch: expected objects={objects}, lines={lines}, paths={paths}, rectangles={rectangles}; actual objects={scene.ObjectCount}, lines={actualLines}, paths={actualPaths}, rectangles={actualRectangles}.");
        }
    }

    private static void AssertDetachedLine(VectorScene scene, DrawingElementHit detached, PointF start, PointF end, string context)
    {
        if (!detached.IsValid
            || detached.Key.Kind != DrawingElementKind.Stroke
            || (uint)detached.Key.ObjectIndex >= scene.ObjectCount
            || scene.ShapeKind[detached.Key.ObjectIndex] != ShapeKind.Line
            || !LineMatches(scene, detached.Key.ObjectIndex, start, end))
        {
            throw new InvalidOperationException($"{context} did not return the expected materialized line segment: key={detached.Key}.");
        }
    }

    private static void AssertLineSet(VectorScene scene, IReadOnlyList<(PointF Start, PointF End)> expected, string context)
    {
        var remaining = expected.ToList();
        for (var i = 0; i < scene.ObjectCount; i++)
        {
            if (scene.ShapeKind[i] != ShapeKind.Line) continue;
            var match = remaining.FindIndex(segment => LineMatches(scene, i, segment.Start, segment.End));
            if (match < 0)
            {
                scene.TryGetLineEndpoint(i, true, out var actualStart);
                scene.TryGetLineEndpoint(i, false, out var actualEnd);
                throw new InvalidOperationException($"{context} contains an unexpected line ({actualStart.X:0.###},{actualStart.Y:0.###})->({actualEnd.X:0.###},{actualEnd.Y:0.###}).");
            }

            remaining.RemoveAt(match);
        }

        if (remaining.Count > 0)
        {
            throw new InvalidOperationException($"{context} is missing {remaining.Count} expected line segment(s).");
        }
    }

    private static bool LineMatches(VectorScene scene, int objectIndex, PointF expectedStart, PointF expectedEnd)
    {
        if (!scene.TryGetLineEndpoint(objectIndex, true, out var actualStart)
            || !scene.TryGetLineEndpoint(objectIndex, false, out var actualEnd))
        {
            return false;
        }

        return PointsNear(actualStart, expectedStart) && PointsNear(actualEnd, expectedEnd)
            || PointsNear(actualStart, expectedEnd) && PointsNear(actualEnd, expectedStart);
    }

    private static int CountPathFillsContaining(VectorScene scene, PointF point)
    {
        return Enumerable.Range(0, scene.ObjectCount)
            .Count(index => scene.ShapeKind[index] == ShapeKind.Path && scene.FillContainsPoint(index, point));
    }

    private static int FindPathFillContaining(VectorScene scene, PointF point)
    {
        return Enumerable.Range(0, scene.ObjectCount)
            .FirstOrDefault(index => scene.ShapeKind[index] == ShapeKind.Path && scene.FillContainsPoint(index, point), -1);
    }

    private static void AssertSplitPaths(VectorScene scene, DrawingElementHit detached, PointF selectedPoint, PointF otherPoint, string context)
    {
        var paths = Enumerable.Range(0, scene.ObjectCount).Where(index => scene.ShapeKind[index] == ShapeKind.Path).ToArray();
        if (!detached.IsValid
            || detached.Key.Kind != DrawingElementKind.Fill
            || !paths.Contains(detached.Key.ObjectIndex)
            || paths.Length != 2
            || paths.Any(index => scene.Stroke[index] != 0))
        {
            throw new InvalidOperationException($"{context} did not produce two zero-stroke Path fills: key={detached.Key}, paths={paths.Length}.");
        }

        var selected = detached.Key.ObjectIndex;
        var other = paths[0] == selected ? paths[1] : paths[0];
        if (!scene.FillContainsPoint(selected, selectedPoint)
            || scene.FillContainsPoint(selected, otherPoint)
            || !scene.FillContainsPoint(other, otherPoint)
            || scene.FillContainsPoint(other, selectedPoint))
        {
            throw new InvalidOperationException($"{context} produced incorrect fill-region geometry.");
        }
    }

    private static (PointF Start, PointF End)[] OutlinedBoundaryLineSegments()
    {
        return new[]
        {
            (new PointF(-320, 0), new PointF(320, 0)),
            (new PointF(-200, -120), new PointF(200, -120)),
            (new PointF(200, -120), new PointF(200, 0)),
            (new PointF(200, 0), new PointF(200, 120)),
            (new PointF(200, 120), new PointF(-200, 120)),
            (new PointF(-200, 120), new PointF(-200, 0)),
            (new PointF(-200, 0), new PointF(-200, -120))
        };
    }

    private static int ApplyInstanceAppearanceForRegression(int argb, float alpha, int tintArgb)
    {
        static int Multiply(int first, int second) => (first * second + 127) / 255;
        var resultAlpha = (int)Math.Clamp(((argb >>> 24) & 0xff) * alpha, 0f, 255f);
        return (resultAlpha << 24)
            | (Multiply((argb >>> 16) & 0xff, (tintArgb >>> 16) & 0xff) << 16)
            | (Multiply((argb >>> 8) & 0xff, (tintArgb >>> 8) & 0xff) << 8)
            | Multiply(argb & 0xff, tintArgb & 0xff);
    }

    private static bool NearlyEqual(float actual, float expected) => Math.Abs(actual - expected) <= 0.001f;

    private static bool PointsNear(PointF actual, PointF expected)
    {
        return Math.Abs(actual.X - expected.X) <= 0.1f && Math.Abs(actual.Y - expected.Y) <= 0.1f;
    }

    private static bool PointsWithin(PointF actual, PointF expected, float tolerance)
    {
        var dx = actual.X - expected.X;
        var dy = actual.Y - expected.Y;
        return dx * dx + dy * dy <= tolerance * tolerance;
    }

    private static bool ContoursNear(IReadOnlyList<PointF[]> actual, IReadOnlyList<PointF[]> expected)
    {
        if (actual.Count != expected.Count) return false;
        for (var contourIndex = 0; contourIndex < actual.Count; contourIndex++)
        {
            var actualContour = actual[contourIndex];
            var expectedContour = expected[contourIndex];
            if (actualContour.Length != expectedContour.Length) return false;
            for (var pointIndex = 0; pointIndex < actualContour.Length; pointIndex++)
            {
                if (!PointsNear(actualContour[pointIndex], expectedContour[pointIndex])) return false;
            }
        }

        return true;
    }

    private static void AssertBrushCoversCenterline(VectorScene scene, int objectIndex, IReadOnlyList<PointF> centerline)
    {
        if ((uint)objectIndex >= scene.ObjectCount
            || scene.ShapeKind[objectIndex] != ShapeKind.Path
            || scene.Stroke[objectIndex] != 0)
        {
            throw new InvalidOperationException("Brush did not commit as an unstroked Path fill.");
        }

        const int samplesPerSegment = 8;
        for (var segment = 0; segment < centerline.Count - 1; segment++)
        {
            var start = centerline[segment];
            var end = centerline[segment + 1];
            for (var sample = 0; sample <= samplesPerSegment; sample++)
            {
                var t = sample / (float)samplesPerSegment;
                var point = new PointF(
                    start.X + (end.X - start.X) * t,
                    start.Y + (end.Y - start.Y) * t);
                if (!scene.FillContainsPoint(objectIndex, point))
                {
                    throw new InvalidOperationException($"Brush fill contains a false hole on centerline segment {segment} at t={t:0.###}.");
                }
            }
        }
    }
}
