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
            throw new InvalidOperationException("Stress-scene composition lost layers, objects, ownership, or LOD summaries.");
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
        scene.LayerVisible[1] = false;
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

        var expected = CollectRenderCommandsReference(scene, bounds, frame, out var expectedVisible, out var expectedAtoms);
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
        out long visibleAtoms)
    {
        var layers = new List<int>?[scene.LayerCount];
        visibleCount = 0;
        visibleAtoms = 0;
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
        RunFixedStepBatchRegression();
        RunTimelineExposureRegression();
        RunTimelineShortcutAdvanceRegression();
        RunTimelineFrameCommandRegression();
        RunTimelineTrackSynchronizationRegression();
        RunTimelineSnapshotRegression();
        RunVectorSceneTimelineSnapshotRegression();
        RunVectorSceneSnapshotMemoryEstimateRegression();
        RunVectorSceneCelOwnershipRegression();
        RunTimelineLayerWorkflowRegression();
        RunVectorSceneKeyframeBoundaryRegression();
        RunProjectDocumentStructureRegression();
        RunSceneInstanceTimelineRegression();
        RunSceneCompositionRegression();
        RunEditorRestartSnapshotRegression();
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
        var source = new TimelineFrameCell("layer-a", 12);
        var target = MainForm.AdvanceTimelineKeyframeShortcutCell(source);
        AssertTimeline(
            target.TrackId == source.TrackId && target.Frame == 13,
            "F6/F7 did not advance the playhead cell before choosing the insertion frame.");
        AssertTimeline(
            MainForm.AdvanceTimelineKeyframeShortcutCell(new TimelineFrameCell("layer-a", int.MaxValue - 1))
                == new TimelineFrameCell("layer-a", int.MaxValue - 1),
            "F6/F7 shortcut frame advance overflowed at the maximum supported frame.");
    }

    private static void RunTimelineFrameCommandRegression()
    {
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
        AssertTimeline(scene.ToggleLayerOnionSkin(0), "Timeline layer workflow could not enable onion skin.");
        AssertTimeline(scene.SetOnionSkinRange(0, 0), "Timeline layer workflow could not disable both onion-skin ranges.");
        var emptyOnionPreview = new VectorScene();
        scene.BuildOnionSkinPreview(emptyOnionPreview, 1);
        AssertTimeline(
            emptyOnionPreview.ObjectCount == 0,
            "A zero onion-skin range still rendered neighboring cels.");
        AssertTimeline(scene.SetOnionSkinRange(3, 1), "Timeline layer workflow could not set the onion-skin frame range.");
        var onionPreview = new VectorScene();
        scene.BuildOnionSkinPreview(onionPreview, 1);
        var previousOnionPreview = new VectorScene();
        scene.BuildOnionSkinPreview(previousOnionPreview, 2);
        AssertTimeline(
            onionPreview.ObjectCount > 0
            && scene.ObjectCount > onionPreview.ObjectCount
            && onionPreview.IsObjectActive(0, 1)
            && onionPreview.GetGradientStops(0).All(stop => ((stop.Argb >>> 24) & 0xff) is > 0 and < 160)
            && onionPreview.GetGradientStops(0).All(stop => (stop.Argb & 0x00ffffff) == 0x006fc3da)
            && previousOnionPreview.ObjectCount > 0
            && previousOnionPreview.GetGradientStops(0).All(stop => (stop.Argb & 0x00ffffff) == 0x00e0867e),
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
        scene.ToggleLayerOnionSkin(1);
        scene.SetOnionSkinRange(0, 0);
        scene.RestoreSnapshot(snapshot);
        AssertTimeline(
            scene.GetLayerColor(0).ToArgb() == Color.Orange.ToArgb()
            && scene.LayerOnionSkin[1]
            && scene.OnionSkinPreviousFrames == 3
            && scene.OnionSkinNextFrames == 1,
            "Layer color or onion-skin state did not survive a scene snapshot round trip.");

        var project = VectorProject.CreateEmpty();
        var sceneDefinition = project.Scenes[0];
        AssertTimeline(project.TryAddSceneLayer(sceneDefinition.Id, out var addedLayer) && addedLayer is not null, "Scene-layer workflow could not add a layer.");
        AssertTimeline(
            project.TrySetSceneLayerColor(sceneDefinition.Id, addedLayer!.Id, Color.Coral)
            && project.TryMoveSceneLayer(sceneDefinition.Id, addedLayer.Id, 0)
            && sceneDefinition.Layers[0].Id == addedLayer.Id
            && sceneDefinition.Layers[0].ColorArgb == Color.Coral.ToArgb(),
            "Scene-layer color or reordering did not route through the project model.");
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
            && project.Scenes.Count == 1
            && !project.Scenes[0].CanDraw
            && project.DrawingObjects is not ICollection<DrawingObjectDefinition> { IsReadOnly: false }
            && project.Scenes is not ICollection<SceneDefinition> { IsReadOnly: false },
            "A new project did not create one drawable drawing object and one non-drawable scene root.");

        var first = project.DrawingObjects[0];
        var second = project.AddDrawingObject("Nested B");
        var third = project.AddDrawingObject("Nested C");
        AssertTimeline(
            project.TryAddDrawingObjectInstance(first.Id, second.Id, PointF.Empty, out var firstToSecond)
            && firstToSecond is not null
            && project.TryAddDrawingObjectInstance(second.Id, third.Id, PointF.Empty, out var secondToThird)
            && secondToThird is not null,
            "Valid drawing-object containment was rejected.");
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

        var firstTrack = first.InstanceTimeline.FindTrackByTargetId(firstToSecond!.Id);
        AssertTimeline(
            firstTrack is not null
            && firstTrack.Keyframes.SequenceEqual(new[] { new TimelineKeyframe(0, TimelineKeyframeKind.Populated) })
            && ReferenceEquals(first.Timeline, first.Scene.Timeline)
            && first.TimelineTargetIds.SequenceEqual(first.Scene.LayerIds.Concat(new[] { firstToSecond.Id })),
            "A drawing object did not expose one unified layer-and-instance timeline.");
        AssertTimeline(
            first.Scene.InsertTimelineBlankKeyframe(0, 3)
            && first.Timeline.InsertBlankKeyframe(firstTrack!.Id, 4),
            "The unified drawing-object timeline rejected a layer or nested-instance key edit.");
        first.SynchronizeTimelineTracks();
        AssertTimeline(
            first.Timeline.FindTrackByTargetId(first.Scene.LayerIds[0])?.EvaluateExposure(3).HasContent == false
            && first.Timeline.FindTrackByTargetId(firstToSecond.Id)?.EvaluateExposure(3).HasContent == true
            && first.Timeline.FindTrackByTargetId(firstToSecond.Id)?.EvaluateExposure(4).HasContent == false,
            "Layer synchronization removed or corrupted a nested-instance exposure track.");

        var nestedTrackId = first.Timeline.FindTrackByTargetId(firstToSecond.Id)?.Id;
        first.Scene.Generate(2, 16, 128);
        first.SynchronizeTimelineTracks();
        AssertTimeline(
            first.Instances.Count == 1
            && first.Timeline.FindTrackByTargetId(firstToSecond.Id) is { } generatedNestedTrack
            && generatedNestedTrack.Id == nestedTrackId
            && generatedNestedTrack.EvaluateExposure(4).HasContent == false,
            "Regenerating drawing geometry discarded the nested-instance timeline.");
    }

    private static void RunSceneCompositionRegression()
    {
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
            GradientKind.Linear,
            [
                new GradientStop(0, Color.Teal),
                new GradientStop(0.5f, Color.Gold),
                new GradientStop(1, Color.Coral)
            ],
            new PointF(60, 50),
            new PointF(140, 50));

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
        AssertTimeline(
            project.TryAddDrawingObjectInstance(
                drawingObject.Id,
                childDrawingObject.Id,
                new PointF(20, 0),
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
        AssertTimeline(
            destination.ObjectCount == 2
            && destination.LayerNames[0].StartsWith(instance.Name, StringComparison.Ordinal)
            && NearlyEqual(destination.X[0], 150)
            && NearlyEqual(destination.Y[0], 500)
            && NearlyEqual(destination.Width[0], 160)
            && NearlyEqual(destination.Height[0], 40)
            && NearlyEqual(destination.Angle[0], MathF.PI / 2f)
            && destination.HasLinearGradient(0)
            && destination.GradientStartArgb[0] == Color.Teal.ToArgb()
            && destination.GradientEndArgb[0] == Color.Coral.ToArgb()
            && destination.GetGradientStops(0).Length == 3
            && destination.GetGradientStops(0)[1].Argb == Color.Gold.ToArgb()
            && PointsNear(destination.GetGradientStart(0), new PointF(150, 420))
            && PointsNear(destination.GetGradientEnd(0), new PointF(150, 580))
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

        var childPreview = new VectorScene();
        var childPreviewResult = SceneCompositionBuilder.BuildDrawingObjectChildren(
            childPreview,
            drawingObject,
            project.DrawingObjects,
            0);
        AssertTimeline(
            childPreview.ObjectCount == 1
            && childPreviewResult.TryGetOwner(0, out var previewOwner)
            && previewOwner.DrawingObjectId == childDrawingObject.Id,
            "Drawing-object editing preview did not expose its nested child instance.");

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

        var nestedTrack = drawingObject.InstanceTimeline.FindTrackByTargetId(nestedInstance!.Id)
            ?? throw new InvalidOperationException("Nested composition regression lost its child-instance track.");
        drawingObject.InstanceTimeline.InsertBlankKeyframe(nestedTrack.Id, 9);
        var frameNineComposition = SceneCompositionBuilder.Build(destination, scene, project.DrawingObjects, 9);
        AssertTimeline(
            destination.ObjectCount == 1
            && frameNineComposition.ObjectOwners.Values.All(owner => owner.DrawingObjectId != childDrawingObject.Id),
            "Nested drawing-object instance exposure did not hide only the child branch.");

        RunSparseLayerCompositionRegression();
        RunDegeneratePathCompositionRegression();
        RunChunkedCompositionRegression();
        RunSkewedSceneCompositionRegression();
    }

    private static void RunEditorRestartSnapshotRegression()
    {
        var project = VectorProject.CreateEmpty();
        project.Name = "Restart Snapshot";
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
        if (!project.TryAddDrawingObjectInstance(root.Id, child.Id, new PointF(40, 20), out var nested)
            || nested is null
            || !project.TryAddSceneInstance(project.Scenes[0].Id, root.Id, new PointF(320, 180), 2, out var sceneInstance)
            || sceneInstance is null)
        {
            throw new InvalidOperationException("Editor restart snapshot regression could not create its source project graph.");
        }

        var restored = VectorProject.RestoreRestartSnapshot(
            EditorRestartStore.RoundTripProjectSnapshot(project.CreateRestartSnapshot()));
        var restoredRoot = restored.DrawingObjects.SingleOrDefault(item => item.Id == root.Id);
        var restoredChild = restored.DrawingObjects.SingleOrDefault(item => item.Id == child.Id);
        var restoredScene = restored.Scenes.SingleOrDefault(item => item.Id == project.Scenes[0].Id);
        if (restored.Name != project.Name
            || restored.DrawingObjects.Count != 2
            || restored.Scenes.Count != 1
            || restoredRoot is null
            || restoredChild is null
            || restoredRoot.Instances.Count != 1
            || restoredRoot.Instances[0].Id != nested.Id
            || restoredRoot.Instances[0].DrawingObjectId != child.Id
            || restoredRoot.Scene.ObjectCount != 1
            || restoredRoot.Scene.GetLineEndpointStyle(rootLine, startEndpoint: true) != LineEndpointStyle.Sharp
            || restoredRoot.Scene.GetLineEndpointStyle(rootLine, startEndpoint: false) != LineEndpointStyle.Round
            || restoredScene is null
            || restoredScene.Instances.Count != 1
            || restoredScene.Instances[0].Id != sceneInstance.Id
            || restoredScene.Instances[0].DrawingObjectId != root.Id
            || !restoredScene.Timeline.EvaluateTargetExposure(restoredScene.ActiveLayerId, 0).HasContent)
        {
            throw new InvalidOperationException("Editor restart snapshot did not preserve project geometry, instance ownership, or timeline state.");
        }

        Console.WriteLine("editor_restart_snapshot_regression=ok");
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
        var referenceLine = reference.AddCurveSegment(
            1,
            VectorUnits.Quantize(sourceStart),
            VectorUnits.Quantize(new PointF(
                drawingObject.Scene.CurveControlX[sourceLine],
                drawingObject.Scene.CurveControlY[sourceLine])),
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
            && NearlyEqual(destination.CurveControlY[0], reference.CurveControlY[referenceLine]),
            "Sparse-layer composition populated an empty layer or changed identity-transform line quantization.");
    }

    private static void RunChunkedCompositionRegression()
    {
        var project = VectorProject.CreateEmpty();
        var drawingObject = project.DrawingObjects[0];
        drawingObject.Scene.Generate(8, 8192, 8_192_000);
        drawingObject.Scene.EditFrame = 20;
        drawingObject.Scene.AddPathObject(
            0,
            [new PointF(-80, -40), new PointF(80, -40), new PointF(60, 70), new PointF(-70, 65)],
            0,
            Color.Coral,
            Color.Transparent,
            12);
        var scene = project.Scenes[0];
        AssertTimeline(
            project.TryAddSceneInstance(scene.Id, drawingObject.Id, PointF.Empty, 0, out _),
            "Chunked composition setup rejected a valid drawing-object instance.");

        var destination = new VectorScene();
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

    private static void AssertTimeline(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void RunStageRendererRegression()
    {
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
        var underlay = new VectorScene();
        underlay.CreateEmpty();
        underlay.AddFreehandStroke(
            0,
            [new PointF(-200, -120), new PointF(-120, -40), new PointF(-40, -100)],
            12,
            Color.Coral,
            brushStroke: false,
            12);

        using var form = new Form
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-30_000, -30_000),
            ClientSize = new Size(640, 420)
        };
        using var stage = new StageControl(editable) { Dock = DockStyle.Fill };
        stage.BindUnderlayScene(underlay);
        form.Controls.Add(stage);
        form.Show();
        Application.DoEvents();
        stage.Invalidate();
        stage.Update();
        Application.DoEvents();

        if (!stage.LastFrameUsedDirect2D)
        {
            throw new InvalidOperationException("A nested underlay forced the stage renderer to fall back from Direct2D.");
        }

        if (stage.LastStats.VisibleObjects != 2)
        {
            throw new InvalidOperationException("The Direct2D underlay pass did not include both scene layers.");
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
            || stage.LastStats.VisibleObjects != 1
            || stage.LastStats.DrawnObjects != 1)
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
        RunRenderOrderRegression();
        RunDrawingTopologyRegression();

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
        var scene = new VectorScene();
        scene.CreateEmpty(2);
        var strokeWidth = VectorUnits.StrokePointsToUnits(2);
        var topLine = scene.AddLineSegment(0, new PointF(-100, 0), new PointF(100, 0), strokeWidth, Color.Transparent, Color.White, 6);
        var bottomLine = scene.AddLineSegment(1, new PointF(-100, 0), new PointF(100, 0), strokeWidth, Color.Transparent, Color.White, 6);
        var topFill = scene.AddObject(0, PointF.Empty, new SizeF(200, 120), 0, 0, Color.Teal, Color.Transparent, 12, ShapeKind.Rectangle);
        var bottomFill = scene.AddObject(1, PointF.Empty, new SizeF(200, 120), 0, 0, Color.Teal, Color.Transparent, 12, ShapeKind.Rectangle);
        var topOutlinedFill = scene.AddObject(0, PointF.Empty, new SizeF(160, 90), 0, strokeWidth, Color.Teal, Color.White, 12, ShapeKind.Rectangle);

        var renderOrder = new SceneRenderOrderBuffer();
        renderOrder.Collect(scene, new RectangleF(-500, -500, 1000, 1000), 0);
        var commands = new List<string>();
        var drawn = renderOrder.Draw(
            scene,
            int.MaxValue,
            index => commands.Add($"F{index}"),
            index => commands.Add($"S{index}"));
        var expected = new[] { $"F{bottomFill}", $"S{bottomLine}", $"F{topFill}", $"F{topOutlinedFill}", $"S{topLine}", $"S{topOutlinedFill}" };
        if (drawn != scene.ObjectCount || !commands.SequenceEqual(expected))
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

        var edgeScene = new VectorScene();
        edgeScene.CreateEmpty();
        var edgeLine = edgeScene.AddLineSegment(0, new PointF(2, -200), new PointF(2, 20), strokeWidth, Color.Transparent, Color.White, 6);
        var edgeCurve = edgeScene.AddLineSegment(0, new PointF(-20, -100), new PointF(20, -100), strokeWidth, Color.Transparent, Color.White, 6);
        edgeScene.CurveControlX[edgeCurve] = 0;
        edgeScene.CurveControlY[edgeCurve] = 140;
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
        RunCrossingFillTopologyRegression();
        RunTerminatingLineTopologyRegression();
        RunTerminatingStrokeContactRegression();
        RunCrossingLinesTopologyRegression();
        RunCollinearOverlapTopologyRegression();
        RunOutlinedBoundaryTopologyRegression();
        RunCrossLayerTopologyRegression();
        RunMultipleCutterFillTopologyRegression();
        RunCurvedCutterFillTopologyRegression();
        RunCompoundFillTopologyRegression();
        RunPencilStrokeTopologyRegression();
        RunConnectedCutterNetworkTopologyRegression();
        RunConnectedStrokeSelectionRegression();
        RunMaterializedOrderTopologyRegression();
        RunBatchElementMaterializationRegression();
        RunOutlinedFillMergeRegression();
        RunMarqueeElementQueryRegression();
        RunMarqueeLineMaterializationRegression();
        RunMarqueeFillMaterializationRegression();
        RunMarqueeOutlinedBoundarySelectionRegression();
        RunLineToFillConversionRegression();
        RunLineSegmentMergeRegression();
        Console.WriteLine("drawing_topology_regressions=22");
    }

    private static void RunTransformGeometryRegression()
    {
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
        var lineControl = new PointF(scene.CurveControlX[line], scene.CurveControlY[line]);
        var freehandValid = scene.TryGetFreehandWorldPoints(freehand, out var freehandPoints);
        var pathValid = scene.TryGetPathWorldContours(path, out var pathContours);
        var skewValid = scene.TryGetPathWorldContours(rectangle, out var skewContours);
        if (!PointsNear(lineStart, new PointF(30, -20))
            || !PointsNear(lineEnd, new PointF(130, -20))
            || !PointsNear(lineControl, new PointF(70, 60))
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

        Console.WriteLine("transform_geometry_regression=ok");
    }

    private static void RunGradientPaintRegression()
    {
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
        if (!lineDefaultAxis || !lineGradientMergeProtected || !transformed || !restored || !sheared || !radial || !radialRestored || !lineLinear || !lineRadial || !stopMovePreserved)
        {
            throw new InvalidOperationException(
                $"Gradient paint regression failed: axis={lineDefaultAxis}, merge={lineGradientMergeProtected}, transformed={transformed}, restored={restored}, sheared={sheared}, radial={radial}, radialRestored={radialRestored}, lineLinear={lineLinear}, lineRadial={lineRadial}, stops={stopMovePreserved}.");
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
        if (!stage.LastFrameUsedDirect2D || stage.LastStats.DrawnObjects != 2 || overlayHit.Kind != GradientHandleKind.Stop || overlayHit.StopIndex != 2)
        {
            throw new InvalidOperationException($"The Direct2D radial multi-stop gradient annotator did not produce an interactive stage frame: direct2d={stage.LastFrameUsedDirect2D}, drawn={stage.LastStats.DrawnObjects}, hit={overlayHit.Kind}/{overlayHit.StopIndex}.");
        }
        form.Close();

        Console.WriteLine("gradient_paint_regression=ok");
    }

    private static void RunHotReloadModuleRoutingRegression()
    {
        var rendering = HotReloadModuleResolver.Resolve([typeof(Direct2DStageRenderer)]);
        var timeline = HotReloadModuleResolver.Resolve([typeof(TimelineStrip)]);
        var inspector = HotReloadModuleResolver.Resolve([typeof(MaterialEditorPanel)]);
        var themedScroll = HotReloadModuleResolver.Resolve([typeof(ThemedScrollPanel)]);
        var harmonyWheel = HotReloadModuleResolver.Resolve([typeof(HarmonyColorWheel)]);
        var gradientPreset = HotReloadModuleResolver.Resolve([typeof(GradientPresetGrid)]);
        var engine = HotReloadModuleResolver.Resolve([typeof(VectorScene)]);
        if (rendering.Modules != HotReloadModule.Rendering
            || timeline.Modules != HotReloadModule.Timeline
            || inspector.Modules != HotReloadModule.Inspector
            || themedScroll.Modules != HotReloadModule.Inspector
            || harmonyWheel.Modules != HotReloadModule.Inspector
            || gradientPreset.Modules != HotReloadModule.Inspector
            || engine.Modules != HotReloadModule.Engine
            || rendering.RequiresWorkbenchRebuild
            || !timeline.RequiresWorkbenchRebuild
            || !inspector.RequiresWorkbenchRebuild
            || !themedScroll.RequiresWorkbenchRebuild
            || !harmonyWheel.RequiresWorkbenchRebuild
            || !gradientPreset.RequiresWorkbenchRebuild
            || engine.RequiresWorkbenchRebuild)
        {
            throw new InvalidOperationException(
                $"Module hot reload routing was not scoped: rendering={rendering.Modules}/{rendering.RequiresWorkbenchRebuild}, timeline={timeline.Modules}/{timeline.RequiresWorkbenchRebuild}, inspector={inspector.Modules}/{inspector.RequiresWorkbenchRebuild}, themedScroll={themedScroll.Modules}/{themedScroll.RequiresWorkbenchRebuild}, harmonyWheel={harmonyWheel.Modules}/{harmonyWheel.RequiresWorkbenchRebuild}, gradientPreset={gradientPreset.Modules}/{gradientPreset.RequiresWorkbenchRebuild}, engine={engine.Modules}/{engine.RequiresWorkbenchRebuild}.");
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

        Console.WriteLine("color_harmony_regression=ok");
    }

    private static void RunGradientPresetRegression()
    {
        var presets = MaterialEditorPanel.BuiltInGradientPresets;
        var midpoint = GradientPreviewRenderer.Sample(
            [new GradientStop(0, Color.Black), new GradientStop(1, Color.White)],
            0.5f);
        if (presets.Count != 6
            || presets.Count(preset => preset.Kind == GradientKind.Linear) != 4
            || presets.Count(preset => preset.Kind == GradientKind.Radial) != 2
            || presets.Any(preset => preset.Stops.Length < 2 || preset.Stops[0].Position != 0f || preset.Stops[^1].Position != 1f)
            || midpoint.R is < 126 or > 129 || midpoint.G != midpoint.R || midpoint.B != midpoint.R)
        {
            throw new InvalidOperationException("Built-in gradient presets or their multi-stop preview sampling were invalid.");
        }

        Console.WriteLine("gradient_preset_regression=ok");
    }

    private static void RunSoftBrushRegression()
    {
        RunTraditionalBrushContourRegression();

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
        var minimumDiameter = VectorUnits.StrokePointsToUnits(0.5f);
        if (onsetPreview.Length != onsetSamples.Length
            || onsetPreview[0].Diameter > minimumDiameter * 0.7f
            || onsetPreview[1].Diameter <= onsetPreview[0].Diameter
            || onsetPreview[^1].Diameter <= onsetPreview[1].Diameter * 2f
            || onsetCommit.Length < 2
            || onsetCommit[0].Diameter > minimumDiameter * 0.7f
            || onsetCommit[^1].Diameter <= onsetCommit[0].Diameter * 8f)
        {
            throw new InvalidOperationException("Pressure brush onset did not diffuse from a minimal initial deposit.");
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
        if (softObjects.Length != 3
            || softObjects.Any(index => scene.ShapeKind[index] != ShapeKind.Path)
            || !softObjects.All(index => scene.FillContainsPoint(index, samples[samples.Length / 2].Point)))
        {
            throw new InvalidOperationException("Continuous pressure brush did not commit layered variable-width Path fills.");
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

        Console.WriteLine($"pressure_brush_commit_ms={stopwatch.Elapsed.TotalMilliseconds:0.00}");
    }

    private static void RunBrushWorldScaleRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        using var stage = new StageControl(scene) { Size = new Size(960, 540) };
        stage.SetVisibleWorldWidth(VectorUnits.DefaultVisibleWorldWidth);
        var diameter = VectorUnits.StrokePointsToUnits(18);
        var initialScreenDiameter = stage.WorldLengthToScreen(diameter);
        stage.ZoomAt(new Point(stage.Width / 2, stage.Height / 2), 2);
        var zoomedScreenDiameter = stage.WorldLengthToScreen(diameter);
        var restoredWorldDiameter = stage.ScreenLengthToWorld(zoomedScreenDiameter);
        if (Math.Abs(zoomedScreenDiameter - initialScreenDiameter * 2) > 0.001f
            || Math.Abs(restoredWorldDiameter - diameter) > 0.001f)
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

        var fillScene = new VectorScene();
        fillScene.CreateEmpty();
        fillScene.AddObject(0, PointF.Empty, new SizeF(400, 240), 0, 0, Color.Coral, Color.Transparent, 12, ShapeKind.Rectangle);
        if (!fillScene.EraseWithBrushStroke(0, center, diameter, shape, eraseLines: false, eraseFills: true)
            || Enumerable.Range(0, fillScene.ObjectCount).Any(index => fillScene.FillContainsPoint(index, PointF.Empty)))
        {
            throw new InvalidOperationException("Brush eraser did not punch a local hole through a fill.");
        }

        var lineScene = new VectorScene();
        lineScene.CreateEmpty();
        lineScene.AddLineSegment(0, new PointF(-240, 0), new PointF(240, 0), VectorUnits.StrokePointsToUnits(10), Color.Transparent, Color.Aqua, 12);
        if (!lineScene.EraseWithBrushStroke(0, center, diameter, shape, eraseLines: true, eraseFills: false)
            || !Enumerable.Range(0, lineScene.ObjectCount).Any(lineScene.IsBrushEraserStrokePath)
            || Enumerable.Range(0, lineScene.ObjectCount).Any(index => lineScene.IsBrushEraserStrokePath(index) && lineScene.FillContainsPoint(index, PointF.Empty)))
        {
            throw new InvalidOperationException("Brush eraser did not remove only the contacted portion of a line.");
        }

        var secondContact = new[] { new PointF(-120, 0) };
        if (!lineScene.EraseWithBrushStroke(0, secondContact, diameter, shape, eraseLines: true, eraseFills: false)
            || Enumerable.Range(0, lineScene.ObjectCount).Any(index => lineScene.IsBrushEraserStrokePath(index) && lineScene.FillContainsPoint(index, secondContact[0])))
        {
            throw new InvalidOperationException("A previously erased line could not be erased again as a line.");
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
        nearToleranceChainScene.Y[nearEnd] = nearToleranceOffset * 2;
        nearToleranceChainScene.CurveControlY[nearEnd] = nearToleranceOffset * 2;
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
            throw new InvalidOperationException("Marquee line materialization did not isolate the selected interior segment.");
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

    private static bool NearlyEqual(float actual, float expected) => Math.Abs(actual - expected) <= 0.001f;

    private static bool PointsNear(PointF actual, PointF expected)
    {
        return Math.Abs(actual.X - expected.X) <= 0.1f && Math.Abs(actual.Y - expected.Y) <= 0.1f;
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
