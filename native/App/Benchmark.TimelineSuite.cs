using System.Diagnostics;
using System.Numerics;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
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

        var groupedLayerPreview = TimelineStrip.ResolveLayerDragPreviewOrder(
            ["source", "source-child", "middle", "target", "target-child", "tail"],
            new HashSet<string>(["source", "source-child"], StringComparer.Ordinal),
            new HashSet<string>(["target", "target-child"], StringComparer.Ordinal),
            "target",
            TimelineLayerDropPlacement.After);
        var maskedLayerPreview = TimelineStrip.ResolveLayerDragPreviewOrder(
            ["top", "mask", "content", "tail"],
            new HashSet<string>(["tail"], StringComparer.Ordinal),
            new HashSet<string>(["mask", "content"], StringComparer.Ordinal),
            "content",
            TimelineLayerDropPlacement.Before);
        var invalidNestedPreview = TimelineStrip.ResolveLayerDragPreviewOrder(
            ["source", "source-child", "tail"],
            new HashSet<string>(["source", "source-child"], StringComparer.Ordinal),
            new HashSet<string>(["source-child"], StringComparer.Ordinal),
            "source-child",
            TimelineLayerDropPlacement.After);
        var moveOutOfMaskPreview = TimelineStrip.ResolveLayerDragPreviewOrder(
            ["mask", "content", "target"],
            new HashSet<string>(["content"], StringComparer.Ordinal),
            new HashSet<string>(["target"], StringComparer.Ordinal),
            "target",
            TimelineLayerDropPlacement.After);
        if (!groupedLayerPreview.SequenceEqual(["middle", "target", "target-child", "source", "source-child", "tail"])
            || !maskedLayerPreview.SequenceEqual(["top", "tail", "mask", "content"])
            || !invalidNestedPreview.SequenceEqual(["source", "source-child", "tail"])
            || !moveOutOfMaskPreview.SequenceEqual(["mask", "target", "content"])
            || !TimelineStrip.ShouldMoveLayerOutOfMaskOnDrag(true, TimelineLayerDropPlacement.Before)
            || !TimelineStrip.ShouldMoveLayerOutOfMaskOnDrag(true, TimelineLayerDropPlacement.Inside)
            || TimelineStrip.ShouldMoveLayerOutOfMaskOnDrag(true, TimelineLayerDropPlacement.Mask)
            || TimelineStrip.ShouldMoveLayerOutOfMaskOnDrag(false, TimelineLayerDropPlacement.After))
        {
            throw new InvalidOperationException(
                "Timeline layer drag previews did not preserve groups or detach masked content at ordinary drop targets.");
        }

        var maskDragScene = new VectorScene();
        maskDragScene.CreateEmpty(layers: 2);
        var maskDragLayer = maskDragScene.AddMaskLayer("Drag mask");
        var maskDragContent = maskDragScene.GetMaskContentLayerIndex(maskDragLayer);
        using (var maskDragTimeline = new TimelineStrip(maskDragScene))
        {
            var maskTrackIndex = maskDragScene.Timeline.Tracks
                .Select((track, index) => (track, index))
                .First(item => string.Equals(
                    item.track.TargetId,
                    maskDragScene.LayerIds[maskDragLayer],
                    StringComparison.Ordinal))
                .index;
            var contentTrackIndex = maskDragScene.Timeline.Tracks
                .Select((track, index) => (track, index))
                .First(item => string.Equals(
                    item.track.TargetId,
                    maskDragScene.LayerIds[maskDragContent],
                    StringComparison.Ordinal))
                .index;
            var maskTrackId = maskDragScene.Timeline.Tracks[maskTrackIndex].Id;
            var contentTrackId = maskDragScene.Timeline.Tracks[contentTrackIndex].Id;
            var contentSourceGroup = maskDragTimeline.ResolveLayerMoveTrackIds(contentTrackIndex);
            var contentTargetGroup = maskDragTimeline.ResolveLayerMoveTrackIds(
                contentTrackIndex,
                includeOwningMask: true);
            var maskSourceGroup = maskDragTimeline.ResolveLayerMoveTrackIds(maskTrackIndex);
            AssertTimeline(
                contentSourceGroup.SetEquals([contentTrackId])
                && contentTargetGroup.SetEquals([maskTrackId, contentTrackId])
                && maskSourceGroup.SetEquals([maskTrackId, contentTrackId]),
                "Timeline mask drag grouping did not distinguish a movable content source from a complete target group.");
        }

        RunFixedStepBatchRegression();
        RunPlaybackSchedulerCoalescingRegression();
        RunDenseTimelinePlaybackUiRegression();
        RunTimelineExposureRegression();
        RunTimelineTweenRegression();
        RunTimelineShortcutAdvanceRegression();
        RunTimelineUndoPlayheadRegression();
        RunTimelineFrameSelectionContentRegression();
        RunTimelineFrameCommandRegression();
        RunTimelineFrameTransformMappingRegression();
        RunTimelineFrameTransformCommandRegression();
        RunTimelineTrackSynchronizationRegression();
        RunTimelineSnapshotRegression();
        RunTimelineTabGroupRegression();
        RunVectorSceneTimelineSnapshotRegression();
        RunVectorSceneSnapshotMemoryEstimateRegression();
        RunEditableTextObjectRegression();
        RunVectorSceneCelOwnershipRegression();
        RunRandomFractureTimelineRegression();
        RunAutoKeyframeMaterializationRegression();
        RunTimelineLayerWorkflowRegression();
        RunTimelineLayerRemovalRegression();
        RunTimelineKeyframePerformanceRegression();
        RunVectorSceneKeyframeBoundaryRegression();
        RunProjectDocumentStructureRegression();
        RunSnapPointModelRegression();
        RunProjectAssetFolderRegression();
        RunAssetTagRegression();
        RunAssetLibraryCategoryRegression();
        RunSceneMaskTimelineRegression();
        RunSceneInstanceTimelineRegression();
        RunLayeredInstanceIndexRegression();
        RunSceneCompositionRegression();
        RunSceneOpticsModelRegression();
        RunSceneOpticsPersistenceRegression();
        RunEditorRestartSnapshotRegression();
        RunProjectVaultPersistenceRegression();
        VerifyProjectCompressionRegression();
        Console.WriteLine("timeline_regression=ok");
    }

    private static void RunTimelineTabGroupRegression()
    {
        var project = VectorProject.CreateEmpty();
        var drawing = project.DrawingObjects[0];
        var scene = drawing.Scene;
        scene.CreateEmpty(2, 16);
        scene.AddObject(0, PointF.Empty, new SizeF(120, 80), 0, 0, Color.Coral, 20, ShapeKind.Rectangle);
        scene.AddObject(1, new PointF(200, 0), new SizeF(120, 80), 0, 0, Color.Teal, 20, ShapeKind.Rectangle);
        var firstTerrain = scene.AddCollisionTerrainLayer();
        var secondTerrain = scene.AddCollisionTerrainLayer();
        AssertTimeline(firstTerrain >= 0 && secondTerrain != firstTerrain,
            "Adding collision terrain reused an existing layer.");
        scene.RenameLayer(secondTerrain, "Moving platform");
        AssertTimeline(scene.IsCollisionTerrainLayer(secondTerrain)
            && scene.GetCollisionTerrainLayers().Length == 2,
            "Renaming a terrain layer lost its collision identity.");

        var timeline = scene.Timeline;
        var track = timeline.FindTrackByTargetId(scene.LayerIds[1])!;
        var group = timeline.CreateTabGroup("Character details and secondary animation");
        AssertTimeline(timeline.SetTrackTabGroup(track.Id, group), "A track could not join its tab group.");
        var before = scene.CreateSnapshot();
        var exposure = timeline.EvaluateExposure(track.Id, 8);
        var geometryRevision = scene.GeometryRevision;
        var contentRevision = scene.ActiveContentRevision;
        var modelChanged = 0;
        timeline.Changed += (_, _) => modelChanged++;
        var groupColor = Color.Crimson.ToArgb();
        var terrainColor = Color.SeaGreen.ToArgb();
        AssertTimeline(timeline.SetTabGroupColor(group, groupColor)
            && timeline.SetTabGroupColor(AnimationTimeline.TerrainTabGroupId, terrainColor)
            && !timeline.SetTabGroupColor(group, groupColor)
            && !timeline.SetTabGroupColor(AnimationTimeline.AllTabGroupId, groupColor)
            && !timeline.SetTabGroupColor("missing-group", groupColor),
            "Tab group color editing failed to preserve valid groups or reject unchanged colors.");
        using (var strip = new TimelineStrip(drawing) { Size = new Size(760, 240) })
        {
            var getRows = RequireMethod(typeof(TimelineStrip), "VisibleTrackIndices", Type.EmptyTypes);
            string[] Rows() => ((IEnumerable<int>)getRows.Invoke(strip, null)!)
                .Select(index => timeline.Tracks[index].TargetId).ToArray();

            timeline.SetActiveTabGroup(group);
            AssertTimeline(Rows().SequenceEqual([track.TargetId]), "Tab group filtering displayed unrelated drawing layers.");
            strip.SelectSingleLayerTarget(track.TargetId);
            strip.SelectSingleFrame(track.Id, 3);
            timeline.SetActiveTabGroup(AnimationTimeline.TerrainTabGroupId);
            AssertTimeline(Rows().ToHashSet(StringComparer.Ordinal).SetEquals(
                    [scene.LayerIds[firstTerrain], scene.LayerIds[secondTerrain]])
                && strip.SelectedFrameCells.All(cell => cell.TrackId != track.Id)
                && !strip.SelectedLayerTargetIds.Contains(track.TargetId),
                "Terrain tab filtering retained ordinary layers or hidden frame selections.");
            timeline.SetActiveTabGroup(AnimationTimeline.AllTabGroupId);
            AssertTimeline(Rows().Length == scene.LayerCount, "All layers tab did not restore the full timeline list.");
        }
        AssertTimeline(modelChanged == 0
            && scene.GeometryRevision == geometryRevision && scene.ActiveContentRevision == contentRevision
            && scene.LayerVisible.SequenceEqual(before.LayerVisible)
            && scene.LayerOpacity.SequenceEqual(before.LayerOpacity)
            && scene.ObjectOrder.Take(scene.ObjectCount).SequenceEqual(before.ObjectOrder.Take(before.ObjectCount))
            && timeline.EvaluateExposure(track.Id, 8) == exposure,
            "Switching timeline tab groups changed render data, exposure, or model revisions.");

        timeline.SetActiveTabGroup(group);
        var snapshot = timeline.CreateSnapshot();
        AssertTimeline(timeline.SetTabGroupColor(group, null)
            && timeline.TabGroups.Single(item => item.Id == group).ColorArgb is null,
            "Resetting a tab group color did not restore its theme default.");
        AssertTimeline(timeline.RenameTabGroup(group, "Characters") && timeline.RemoveTabGroup(group)
            && timeline.FindTrack(track.Id)!.TabGroupId == AnimationTimeline.DefaultTabGroupId,
            "Deleting a tab group did not return its layers to the default group.");
        timeline.RestoreSnapshot(snapshot);
        AssertTimeline(timeline.TabGroups.Any(item => item.Id == group && item.ColorArgb == groupColor)
            && timeline.TabGroups.Single(item => item.Id == AnimationTimeline.TerrainTabGroupId).ColorArgb == terrainColor
            && timeline.ActiveTabGroupId == group && timeline.FindTrack(track.Id)!.TabGroupId == group,
            "Timeline snapshots lost group names, active tab, or membership.");
        var temporaryRoot = CreateTemporaryDirectory("timeline-tab-groups");
        try
        {
            var path = Path.Combine(temporaryRoot, "Groups.v2dProject");
            ProjectVaultStore.Save(project, path);
            var loaded = ProjectVaultStore.Load(path).DrawingObjects[0].Scene;
            AssertTimeline(loaded.Timeline.ActiveTabGroupId == group
                && loaded.Timeline.TabGroups.Any(item => item.Id == group && item.ColorArgb == groupColor)
                && loaded.Timeline.TabGroups.Single(item => item.Id == AnimationTimeline.TerrainTabGroupId).ColorArgb == terrainColor
                && loaded.Timeline.FindTrackByTargetId(track.TargetId)?.TabGroupId == group
                && loaded.GetCollisionTerrainLayers().Length == 2,
                "Project Save/Open lost tab groups or renamed terrain layers.");
        }
        finally { DeleteTemporaryDirectory(temporaryRoot); }

        var legacy = new AnimationTimeline();
        legacy.RestoreSnapshot(new AnimationTimelineSnapshot
        {
            Tracks = [new AnimationTimelineTrackSnapshot
            {
                Id = "legacy-track", TargetId = "legacy-layer", Duration = 8,
                Keyframes = [new TimelineKeyframe(0, TimelineKeyframeKind.Populated)]
            }]
        });
        AssertTimeline(legacy.Tracks[0].TabGroupId == AnimationTimeline.DefaultTabGroupId,
            "Legacy timelines without tab metadata did not use the default group.");
        var oldGroupJson = "{\"Tracks\":[],\"TabGroups\":[{\"Id\":\"old-group\",\"Name\":\"Old group\"}]}";
        legacy.RestoreSnapshot(System.Text.Json.JsonSerializer.Deserialize<AnimationTimelineSnapshot>(oldGroupJson)!);
        AssertTimeline(legacy.TabGroups.All(item => item.ColorArgb is null),
            "Legacy tab groups without color metadata did not retain theme defaults.");

        using var form = new MainForm();
        var editorTimeline = (TimelineStrip)RequireField(typeof(MainForm), "_timeline").GetValue(form)!;
        RequireMethod(typeof(MainForm), "BeginTimelineTabGroupEdit", Type.EmptyTypes).Invoke(form, null);
        var createdGroup = editorTimeline.Context.Timeline.CreateTabGroup("Undo group");
        RequireMethod(typeof(MainForm), "CompleteTimelineTabGroupEdit", Type.EmptyTypes).Invoke(form, null);
        AssertTimeline(RequireMethod(typeof(MainForm), "UndoLastEdit", Type.EmptyTypes).Invoke(form, null) is true
            && editorTimeline.Context.Timeline.TabGroups.All(item => item.Id != createdGroup),
            "Timeline group editing did not create one reversible editor command.");
        RequireMethod(typeof(MainForm), "BeginTimelineTabGroupEdit", Type.EmptyTypes).Invoke(form, null);
        editorTimeline.Context.Timeline.SetTabGroupColor(AnimationTimeline.DefaultTabGroupId, groupColor);
        RequireMethod(typeof(MainForm), "CompleteTimelineTabGroupEdit", Type.EmptyTypes).Invoke(form, null);
        AssertTimeline(RequireMethod(typeof(MainForm), "UndoLastEdit", Type.EmptyTypes).Invoke(form, null) is true
            && editorTimeline.Context.Timeline.TabGroups.Single(item => item.Id == AnimationTimeline.DefaultTabGroupId).ColorArgb is null,
            "Tab group color editing did not undo independently of group creation.");
        Console.WriteLine("timeline_tab_groups=ok");
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
        var repeatedPlaybackFrameRates = MainForm.CalculatePerformanceRateSample(120, 300, 1, playing: true);
        var pausedRates = MainForm.CalculatePerformanceRateSample(60, 300, 1, playing: false);
        if (!isolatedRender.HasRenderRate
            || Math.Abs(isolatedRender.RenderFps - 1) > 0.001
            || isolatedRender.HasUpdateRate
            || !activeRates.HasRenderRate
            || Math.Abs(activeRates.RenderFps - 60) > 0.001
            || !activeRates.HasUpdateRate
            || Math.Abs(activeRates.UpdatesPerSecond - 300) > 0.001
            || !repeatedPlaybackFrameRates.HasRenderRate
            || Math.Abs(repeatedPlaybackFrameRates.RenderFps - 120) > 0.001
            || !pausedRates.HasRenderRate
            || pausedRates.HasUpdateRate)
        {
            throw new InvalidOperationException("Render FPS and UPS telemetry did not preserve completed-presentation, active, isolated, and paused rate semantics.");
        }
    }

    private static void RunPlaybackSchedulerCoalescingRegression()
    {
        AssertTimeline(
            MainForm.PlaybackSchedulerIntervalMilliseconds == 8,
            "Playback scheduler cadence can starve WinForms paint and input messages.");

        var gate = new PlaybackUiTickGate();
        AssertTimeline(gate.TryReserve(), "Playback scheduler did not reserve its first UI tick.");
        var nestedReservationAccepted = true;
        gate.ExecuteReserved(() =>
        {
            nestedReservationAccepted = gate.TryReserve();
            AssertTimeline(
                gate.IsReserved && !nestedReservationAccepted,
                "Playback scheduler released its single-flight gate before the UI tick completed.");
        });
        AssertTimeline(
            !gate.IsReserved && gate.TryReserve(),
            "Playback scheduler did not release its UI tick after completion.");

        var releasedAfterFailure = false;
        try
        {
            gate.ExecuteReserved(() => throw new ApplicationException("playback tick probe"));
        }
        catch (ApplicationException)
        {
            releasedAfterFailure = !gate.IsReserved;
        }
        AssertTimeline(
            releasedAfterFailure && gate.TryReserve(),
            "Playback scheduler kept its UI tick reserved after a callback failure.");
        gate.CancelReservation();

        Console.WriteLine($"timeline_playback_scheduler_interval_ms={MainForm.PlaybackSchedulerIntervalMilliseconds}");
        Console.WriteLine("timeline_playback_tick_gate=ok");
    }

    private static void RunDenseTimelinePlaybackUiRegression()
    {
        const int layerCount = 256;
        const int visibleFrameCapacity = 60;
        const int lastPlaybackFrame = 179;
        var firstVisibleFrame = 0;
        var viewportShiftCount = 0;
        for (var frame = 1; frame <= lastPlaybackFrame; frame++)
        {
            var nextFirstVisibleFrame = TimelineStrip.ResolveCurrentFrameViewportStart(
                frame,
                firstVisibleFrame,
                visibleFrameCapacity,
                startFrame: 0,
                maximumFirstVisibleFrame: 180,
                playing: true);
            if (nextFirstVisibleFrame != firstVisibleFrame) viewportShiftCount++;
            firstVisibleFrame = nextFirstVisibleFrame;
            AssertTimeline(
                frame >= firstVisibleFrame && frame < firstVisibleFrame + visibleFrameCapacity,
                "Dense timeline playback scrolled the current frame outside the visible viewport.");
        }

        AssertTimeline(
            viewportShiftCount == 3
            && TimelineStrip.ResolveCurrentFrameViewportStart(
                currentFrame: 60,
                firstVisibleFrame: 0,
                visibleFrameCapacity,
                startFrame: 0,
                maximumFirstVisibleFrame: 180,
                playing: false) == 1
            && TimelineStrip.ResolveCurrentFrameViewportStart(
                currentFrame: 0,
                firstVisibleFrame: 135,
                visibleFrameCapacity,
                startFrame: 0,
                maximumFirstVisibleFrame: 180,
                playing: true) == 0,
            "Timeline playback viewport following did not page forward or preserve manual reveal behavior.");

        var scene = new VectorScene();
        scene.CreateEmpty(layers: layerCount);
        _ = scene.GetLayerDepth(0);
        _ = scene.IsLayerEffectivelyVisible(0);
        _ = scene.IsLayerEffectivelyLocked(0);
        _ = scene.IsLayerEffectivelyOutlined(0);
        var allocationStart = GC.GetAllocatedBytesForCurrentThread();
        var hierarchyStateChecksum = 0;
        for (var pass = 0; pass < 4; pass++)
        {
            for (var layer = 0; layer < scene.LayerCount; layer++)
            {
                hierarchyStateChecksum += scene.GetLayerDepth(layer);
                if (scene.IsLayerEffectivelyVisible(layer)) hierarchyStateChecksum++;
                if (scene.IsLayerEffectivelyLocked(layer)) hierarchyStateChecksum++;
                if (scene.IsLayerEffectivelyOutlined(layer)) hierarchyStateChecksum++;
            }
        }
        var hierarchyQueryAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
        GC.KeepAlive(hierarchyStateChecksum);
        AssertTimeline(
            hierarchyQueryAllocatedBytes <= 4096,
            $"Dense timeline layer hierarchy queries allocated {hierarchyQueryAllocatedBytes} bytes during playback-state lookup.");
        Console.WriteLine($"timeline_dense_layer_query_allocated_bytes={hierarchyQueryAllocatedBytes}");

        using var timelineStrip = new TimelineStrip(scene)
        {
            Size = new Size(1280, 420),
            IsPlaying = true
        };
        _ = timelineStrip.Handle;
        var selectedFrameCells = scene.Timeline.Tracks
            .Select(track => new TimelineFrameCell(track.Id, 44))
            .ToArray();
        timelineStrip.RestoreSelectionSnapshot(new TimelineSelectionSnapshot(
            selectedFrameCells,
            selectedFrameCells[0],
            scene.Timeline.Tracks[0].Id,
            [],
            null));
        AssertTimeline(
            timelineStrip.SelectedFrameCells.Count == layerCount,
            "Dense timeline setup did not retain the selected frame across all layers.");
        AssertDenseTimelineIncrementalPaint(timelineStrip);
        timelineStrip.CurrentFrame = 0;

        var fullSurfaceInvalidations = 0;
        timelineStrip.Invalidated += (_, args) =>
        {
            if (args.InvalidRect.Width >= timelineStrip.ClientSize.Width
                && args.InvalidRect.Height >= timelineStrip.ClientSize.Height)
            {
                fullSurfaceInvalidations++;
            }
        };

        for (var frame = 1; frame <= lastPlaybackFrame; frame++) timelineStrip.CurrentFrame = frame;

        AssertTimeline(
            fullSurfaceInvalidations <= 3
            && timelineStrip.SelectedFrameCells.Count == layerCount,
            "A dense timeline requested a full-surface repaint for each playback frame after auto-scroll: "
            + $"layers={layerCount}, fullInvalidations={fullSurfaceInvalidations}.");
        Console.WriteLine($"timeline_dense_playback_layers={layerCount}");
        Console.WriteLine($"timeline_dense_playback_selected_cells={timelineStrip.SelectedFrameCells.Count}");
        Console.WriteLine($"timeline_dense_playback_viewport_shifts={viewportShiftCount}");
        Console.WriteLine($"timeline_dense_playback_full_invalidations={fullSurfaceInvalidations}");
    }

    private static void AssertDenseTimelineIncrementalPaint(TimelineStrip timelineStrip)
    {
        timelineStrip.CurrentFrame = 44;
        timelineStrip.PerformLayout();
        var onPaint = RequireMethod(
            typeof(TimelineStrip),
            "OnPaint",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        void PaintTimelineSurface(Bitmap target, Region clip)
        {
            using var graphics = Graphics.FromImage(target);
            var clipBounds = Rectangle.Ceiling(clip.GetBounds(graphics));
            graphics.SetClip(clip, System.Drawing.Drawing2D.CombineMode.Replace);
            using var paintArgs = new PaintEventArgs(graphics, clipBounds);
            onPaint.Invoke(timelineStrip, [paintArgs]);
        }

        using var initialFrame = new Bitmap(timelineStrip.ClientSize.Width, timelineStrip.ClientSize.Height);
        using (var initialBackground = Graphics.FromImage(initialFrame))
        {
            initialBackground.Clear(timelineStrip.BackColor);
        }
        using (var fullRegion = new Region(timelineStrip.ClientRectangle))
        {
            PaintTimelineSurface(initialFrame, fullRegion);
        }

        var invalidatedBounds = new List<Rectangle>();
        InvalidateEventHandler captureInvalidation = (_, args) => invalidatedBounds.Add(args.InvalidRect);
        timelineStrip.Invalidated += captureInvalidation;
        timelineStrip.CurrentFrame = 45;
        timelineStrip.Invalidated -= captureInvalidation;
        AssertTimeline(invalidatedBounds.Count > 0, "Dense timeline frame advance did not invalidate its changed pixels.");

        using var incrementalFrame = (Bitmap)initialFrame.Clone();
        using (var invalidRegion = new Region(invalidatedBounds[0]))
        {
            for (var index = 1; index < invalidatedBounds.Count; index++)
            {
                invalidRegion.Union(invalidatedBounds[index]);
            }
            PaintTimelineSurface(incrementalFrame, invalidRegion);
        }

        using var completeFrame = new Bitmap(timelineStrip.ClientSize.Width, timelineStrip.ClientSize.Height);
        using (var completeBackground = Graphics.FromImage(completeFrame))
        {
            completeBackground.Clear(timelineStrip.BackColor);
        }
        using (var fullRegion = new Region(timelineStrip.ClientRectangle))
        {
            PaintTimelineSurface(completeFrame, fullRegion);
        }
        var pixelCount = checked(completeFrame.Width * completeFrame.Height);
        var incrementalPixels = System.Buffers.ArrayPool<int>.Shared.Rent(pixelCount);
        var completePixels = System.Buffers.ArrayPool<int>.Shared.Rent(pixelCount);
        try
        {
            CopyBitmapPixels(incrementalFrame, incrementalPixels);
            CopyBitmapPixels(completeFrame, completePixels);
            for (var index = 0; index < pixelCount; index++)
            {
                if (incrementalPixels[index] == completePixels[index]) continue;
                var x = index % completeFrame.Width;
                var y = index / completeFrame.Width;
                throw new InvalidOperationException(
                    "Dense timeline incremental painting left stale pixels after a selected-frame advance: "
                    + $"x={x}, y={y}, incremental={incrementalPixels[index]:X8}, "
                    + $"complete={completePixels[index]:X8}, invalidated="
                    + string.Join(";", invalidatedBounds.Select(bounds => bounds.ToString()))
                    + ".");
            }
        }
        finally
        {
            System.Buffers.ArrayPool<int>.Shared.Return(incrementalPixels);
            System.Buffers.ArrayPool<int>.Shared.Return(completePixels);
        }

        Console.WriteLine("timeline_dense_playback_incremental_pixels=ok");
    }

    private static void CopyBitmapPixels(Bitmap bitmap, int[] destination)
    {
        var bounds = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(
            bounds,
            System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < bitmap.Height; y++)
            {
                var sourceRow = data.Stride >= 0
                    ? IntPtr.Add(data.Scan0, y * data.Stride)
                    : IntPtr.Add(data.Scan0, (bitmap.Height - 1 - y) * -data.Stride);
                System.Runtime.InteropServices.Marshal.Copy(
                    sourceRow,
                    destination,
                    y * bitmap.Width,
                    bitmap.Width);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
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

        var reverseCells = new[]
        {
            new TimelineFrameCell("track-a", 2),
            new TimelineFrameCell("track-a", 3),
            new TimelineFrameCell("track-a", 4),
            new TimelineFrameCell("track-b", 6),
            new TimelineFrameCell("track-b", 7)
        };
        var reverseClipboard = reverseCells
            .Select((cell, index) => new TimelineClipboardCell(
                index,
                cell.Frame - 2,
                -1,
                cell.Frame,
                index == 2 ? TimelineKeyframeKind.Populated : TimelineKeyframeKind.Blank,
                new Dictionary<string, InstanceFrameState>()))
            .ToArray();
        var reversedClipboard = MainForm.ReverseTimelineClipboardCells(reverseCells, reverseClipboard);
        AssertTimeline(
            TimelineStrip.CanReverseFrameSelection(reverseCells)
            && !TimelineStrip.CanReverseFrameSelection(
                [new TimelineFrameCell("track-a", 2), new TimelineFrameCell("track-b", 2)])
            && reversedClipboard.Length == reverseClipboard.Length
            && reversedClipboard.Select(cell => cell.FrameOffset).SequenceEqual([2, 1, 0, 5, 4])
            && reversedClipboard[2].Kind == TimelineKeyframeKind.Populated
            && reversedClipboard[3].SourceFrame == reverseClipboard[3].SourceFrame,
            "Reverse Frames did not mirror each track's selected range while preserving source cell data.");

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
        var shortcut = RequireMethod(typeof(MainForm), "HandleTimelineShortcut");
        var undo = RequireMethod(typeof(MainForm), "UndoLastEdit");
        var frameField = RequireField(typeof(MainForm), "_frame");
        var timelineField = RequireField(typeof(MainForm), "_timeline");

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

        RunSceneTweenCurveDeferredCommitRegression(form, timelineStrip, undo);
    }

    private static void RunTimelineFrameSelectionContentRegression()
    {
        var timelineField = RequireField(typeof(MainForm), "_timeline");
        var selectedObjectsField = RequireField(typeof(MainForm), "_selectedObjects");
        var stageField = RequireField(typeof(MainForm), "_stage");
        var frameField = RequireField(typeof(MainForm), "_frame");
        var projectDirtyField = RequireField(typeof(MainForm), "_projectDirty");
        var setProjectDirty = RequireMethod(typeof(MainForm), "SetProjectDirty");
        var undoStackField = RequireField(typeof(MainForm), "_undoStack");
        var playbackSettingsField = RequireField(typeof(MainForm), "_playbackSettings");

        using (var form = new MainForm())
        {
            var timeline = timelineField.GetValue(form) as TimelineStrip
                ?? throw new InvalidOperationException("Frame-content selection regression lost the timeline control.");
            var playbackSettings = playbackSettingsField.GetValue(form) as PlaybackSettingsPanel
                ?? throw new InvalidOperationException("Frame-content selection regression lost playback settings.");
            var drawingObject = timeline.Context as DrawingObjectDefinition
                ?? throw new InvalidOperationException("Frame-content selection regression lost its drawing context.");
            var scene = drawingObject.Scene;
            scene.CreateEmpty(layers: 4);
            scene.EditFrame = 0;
            var firstCelObject = scene.AddObject(
                0,
                new PointF(20, 20),
                new SizeF(40, 40),
                0,
                0,
                Color.Teal,
                4,
                ShapeKind.Rectangle);
            var heldObject = scene.AddObject(
                1,
                new PointF(80, 20),
                new SizeF(40, 40),
                0,
                0,
                Color.CornflowerBlue,
                4,
                ShapeKind.Ellipse);
            scene.AddObject(
                2,
                new PointF(140, 20),
                new SizeF(40, 40),
                0,
                0,
                Color.Orange,
                4,
                ShapeKind.Rectangle);
            scene.AddObject(
                3,
                new PointF(200, 20),
                new SizeF(40, 40),
                0,
                0,
                Color.MediumPurple,
                4,
                ShapeKind.Rectangle);
            AssertTimeline(
                scene.InsertTimelineKeyframe(0, 4)
                && scene.InsertTimelineBlankKeyframe(0, 8)
                && scene.SetLayerVisible(2, false)
                && scene.SetLayerLocked(3, true),
                "Frame-content selection regression could not establish its drawing Cels and layer filters.");
            var secondCelObject = Enumerable.Range(0, scene.ObjectCount)
                .Single(index => scene.ObjectLayer[index] == 0 && scene.ObjectKeyframeFrame[index] == 4);

            timeline.RefreshTimeline();
            playbackSettings.SetFrameRange(0, 8, notifyChanged: false);
            timeline.StartFrame = 0;
            timeline.EndFrame = 8;
            var tracks = scene.LayerIds
                .Select(layerId => scene.Timeline.FindTrackByTargetId(layerId)
                    ?? throw new InvalidOperationException("Frame-content selection regression lost a drawing track."))
                .ToArray();
            var selectedCells = new[]
            {
                new TimelineFrameCell(tracks[0].Id, 0),
                new TimelineFrameCell(tracks[0].Id, 4),
                new TimelineFrameCell(tracks[1].Id, 0),
                new TimelineFrameCell(tracks[1].Id, 3),
                new TimelineFrameCell(tracks[2].Id, 0),
                new TimelineFrameCell(tracks[3].Id, 0),
                new TimelineFrameCell(tracks[0].Id, 8),
                new TimelineFrameCell(tracks[0].Id, tracks[0].Duration + 1)
            };
            var expectedObjects = new[] { firstCelObject, heldObject, secondCelObject };
            var resolved = MainForm.ResolveTimelineFrameContentSelection(drawingObject, scene, selectedCells);
            AssertTimeline(
                resolved.ObjectIndices.ToHashSet().SetEquals(expectedObjects)
                && resolved.ObjectIndices.Length == expectedObjects.Length
                && resolved.Instances.Length == 0
                && !resolved.IncludesLightTrack,
                "Selected drawing frames did not merge independent Cels, deduplicate held exposure, or filter blank, hidden, locked, and out-of-range content.");

            timeline.SelectSingleLayerTarget(scene.LayerIds[0], notifyActiveLayerChanged: false);
            setProjectDirty.Invoke(form, new object[] { false });
            var undoStack = (System.Collections.ICollection)(undoStackField.GetValue(form)
                ?? throw new InvalidOperationException("Frame-content selection regression lost the undo stack."));
            var undoCount = undoStack.Count;
            timeline.SelectFrameCells(selectedCells, selectedCells[0]);
            var selectedObjects = selectedObjectsField.GetValue(form) as List<int>
                ?? throw new InvalidOperationException("Frame-content selection regression lost MainForm selection state.");
            var stage = stageField.GetValue(form) as StageControl
                ?? throw new InvalidOperationException("Frame-content selection regression lost Stage selection state.");
            AssertTimeline(
                selectedObjects.ToHashSet().SetEquals(expectedObjects)
                && stage.SelectedObjects.ToHashSet().SetEquals(expectedObjects)
                && undoStack.Count == undoCount
                && projectDirtyField.GetValue(form) is false,
                "Multi-frame selection did not synchronize MainForm and Stage without creating undo or dirty state.");

            timeline.SelectSingleFrame(tracks[0].Id, 4);
            AssertTimeline(
                frameField.GetValue(form) is 4
                && selectedObjects.SequenceEqual([secondCelObject])
                && stage.SelectedObjects.SequenceEqual([secondCelObject]),
                $"Selecting a frame away from the old playhead did not retain its Cel selection after SetFrame "
                + $"(frame={frameField.GetValue(form)}, expected={secondCelObject}, "
                + $"main=[{string.Join(',', selectedObjects)}], stage=[{string.Join(',', stage.SelectedObjects)}]).");

            timeline.SelectSingleFrame(tracks[0].Id, 8);
            AssertTimeline(
                selectedObjects.Count == 0 && stage.SelectedObjects.Count == 0,
                "Selecting a blank exposure did not clear the drawing selection.");

            var firstCell = new TimelineFrameCell(tracks[0].Id, 0);
            timeline.SelectFrameCells([firstCell], firstCell);
            AssertTimeline(
                selectedObjects.SequenceEqual([firstCelObject]),
                "A selected frame away from the playhead was not retained in the cross-frame selection set.");
            timeline.ClearSelectionFromEmptyArea();
            AssertTimeline(
                selectedObjects.Count == 0 && stage.SelectedObjects.Count == 0,
                "Clearing the timeline frame selection did not clear the Stage selection.");
        }

        var nestedProject = VectorProject.CreateEmpty();
        var container = nestedProject.DrawingObjects[0];
        var child = nestedProject.AddDrawingObject("Frame selection child");
        var nestedLayerId = container.Scene.LayerIds[0];
        AssertTimeline(
            nestedProject.TryAddDrawingObjectInstance(
                container.Id,
                child.Id,
                PointF.Empty,
                nestedLayerId,
                out var nestedInstance)
            && nestedInstance is not null,
            "Frame-content selection regression could not create a nested instance.");
        var nestedTrack = container.Timeline.FindTrackByTargetId(nestedLayerId)
            ?? throw new InvalidOperationException("Frame-content selection regression lost the nested-instance track.");
        var nestedSelection = MainForm.ResolveTimelineFrameContentSelection(
            container,
            container.Scene,
            [new TimelineFrameCell(nestedTrack.Id, 0)]);
        var hiddenNestedState = nestedInstance!.EvaluateState(3) with { Visible = false };
        nestedInstance.SetStateAtFrame(3, hiddenNestedState);
        var hiddenNestedSelection = MainForm.ResolveTimelineFrameContentSelection(
            container,
            container.Scene,
            [new TimelineFrameCell(nestedTrack.Id, 3)]);
        AssertTimeline(
            nestedSelection.Instances.SequenceEqual([nestedInstance])
            && hiddenNestedSelection.Instances.Length == 0,
            "Drawing-frame selection did not include visible nested instances or exclude hidden ones.");

        var sceneProject = VectorProject.CreateEmpty();
        var sceneDefinition = sceneProject.Scenes[0];
        var sourceObject = sceneProject.DrawingObjects[0];
        var sceneLayer = sceneDefinition.Layers[0];
        AssertTimeline(
            sceneProject.TryAddSceneInstance(
                sceneDefinition.Id,
                sourceObject.Id,
                PointF.Empty,
                0,
                sceneLayer.Id,
                out var sceneInstance)
            && sceneInstance is not null,
            "Frame-content selection regression could not create a Scene Building instance.");
        var sceneTrack = sceneDefinition.Timeline.FindTrackByTargetId(sceneLayer.Id)
            ?? throw new InvalidOperationException("Frame-content selection regression lost the scene-layer track.");
        var sceneSelection = MainForm.ResolveTimelineFrameContentSelection(
            sceneDefinition,
            new VectorScene(),
            [new TimelineFrameCell(sceneTrack.Id, 0)]);
        sceneInstance!.SetStateAtFrame(3, sceneInstance.EvaluateState(3) with { Visible = false });
        var hiddenSceneSelection = MainForm.ResolveTimelineFrameContentSelection(
            sceneDefinition,
            new VectorScene(),
            [new TimelineFrameCell(sceneTrack.Id, 3)]);
        sceneDefinition.Timeline.InsertBlankKeyframe(sceneTrack.Id, 5);
        var blankSceneSelection = MainForm.ResolveTimelineFrameContentSelection(
            sceneDefinition,
            new VectorScene(),
            [new TimelineFrameCell(sceneTrack.Id, 5)]);
        AssertTimeline(
            sceneSelection.Instances.SequenceEqual([sceneInstance])
            && hiddenSceneSelection.Instances.Length == 0
            && blankSceneSelection.Instances.Length == 0,
            "Scene-frame selection did not honor exposure and per-frame instance visibility.");

        AssertTimeline(
            sceneProject.TryAddSceneLight(sceneDefinition.Id, SceneLightKind.Point, out var sceneLight)
            && sceneLight is not null,
            "Frame-content selection regression could not create a scene light.");
        var lightTrack = sceneDefinition.Timeline.FindTrackByTargetId(sceneLight!.Id)
            ?? throw new InvalidOperationException("Frame-content selection regression lost the light track.");
        var lightSelection = MainForm.ResolveTimelineFrameContentSelection(
            sceneDefinition,
            new VectorScene(),
            [new TimelineFrameCell(lightTrack.Id, 0)]);
        AssertTimeline(
            lightSelection.IncludesLightTrack
            && lightSelection.ObjectIndices.Length == 0
            && lightSelection.Instances.Length == 0,
            "Light-frame selection was incorrectly converted into drawing or instance selection.");

        var maskProject = VectorProject.CreateEmpty();
        var maskDefinition = maskProject.Scenes[0];
        var maskedContentLayer = maskDefinition.Layers[0];
        AssertTimeline(
            maskProject.TryAddSceneMaskLayer(
                maskDefinition.Id,
                maskedContentLayer.Id,
                out var maskLayer,
                "Frame selection mask")
            && maskLayer is not null,
            "Frame-content selection regression could not create a Scene Mask.");
        var maskScene = maskDefinition.FindMaskScene(maskLayer!.Id)
            ?? throw new InvalidOperationException("Frame-content selection regression lost the mask drawing scene.");
        maskScene.EditFrame = 0;
        var maskObject = maskScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(120, 80),
            0,
            0,
            Color.White,
            0,
            ShapeKind.Rectangle);
        maskDefinition.SynchronizeMaskTimelineContent(maskLayer.Id, 0);
        maskDefinition.SetActiveLayer(maskLayer.Id);
        var maskTrack = maskDefinition.Timeline.FindTrackByTargetId(maskLayer.Id)
            ?? throw new InvalidOperationException("Frame-content selection regression lost the outer mask track.");
        var maskSelection = MainForm.ResolveTimelineFrameContentSelection(
            maskDefinition,
            maskScene,
            [new TimelineFrameCell(maskTrack.Id, 0)]);
        AssertTimeline(
            maskSelection.ObjectIndices.SequenceEqual([maskObject])
            && maskSelection.Instances.Length == 0,
            "Selecting a Scene Mask frame did not resolve its editable inner drawing content.");
    }

    private static void RunSceneTweenCurveDeferredCommitRegression(
        MainForm form,
        TimelineStrip timelineStrip,
        System.Reflection.MethodInfo undo)
    {
        var project = RequireField(typeof(MainForm), "_project").GetValue(form) as VectorProject
            ?? throw new InvalidOperationException("Tween curve gesture regression could not inspect the project.");
        var workspaceTabs = RequireField(typeof(MainForm), "_workspaceTabs").GetValue(form) as WorkspaceTabs
            ?? throw new InvalidOperationException("Tween curve gesture regression could not inspect the workspace tabs.");
        var panel = RequireField(typeof(MainForm), "_tweenCurveEditorPanel").GetValue(form) as TweenCurveEditorPanel
            ?? throw new InvalidOperationException("Tween curve gesture regression could not inspect the curve panel.");
        var scenePage = RequireField(typeof(MainForm), "_sceneEditPage").GetValue(form) as ThemedScrollPanel
            ?? throw new InvalidOperationException("Tween curve gesture regression could not inspect the Scene Building page.");
        var basicPage = RequireField(typeof(MainForm), "_basicInspectorPage").GetValue(form) as ThemedScrollPanel
            ?? throw new InvalidOperationException("Tween curve gesture regression could not inspect the Basic Drawing page.");
        var sceneUndoStack = RequireField(typeof(MainForm), "_sceneTimelineUndoStack").GetValue(form)
            ?? throw new InvalidOperationException("Tween curve gesture regression could not inspect the scene undo stack.");
        var editor = RequireField(typeof(TweenCurveEditorPanel), "_editor").GetValue(panel) as TweenCurveEditor
            ?? throw new InvalidOperationException("Tween curve gesture regression could not inspect the curve editor.");
        var chartBounds = RequireMethod(typeof(TweenCurveEditor), "ChartBounds");
        var displayAnchors = RequireMethod(typeof(TweenCurveEditor), "DisplayAnchors");
        var mouseDown = RequireMethod(typeof(TweenCurveEditor), "OnMouseDown");
        var mouseMove = RequireMethod(typeof(TweenCurveEditor), "OnMouseMove");
        var mouseUp = RequireMethod(typeof(TweenCurveEditor), "OnMouseUp");
        var keyDown = RequireMethod(typeof(TweenCurveEditor), "OnKeyDown");

        var sourceObject = project.DrawingObjects[0];
        sourceObject.Scene.CreateEmpty(1, 9);
        sourceObject.Scene.AddObject(
            0,
            PointF.Empty,
            new SizeF(80, 60),
            0,
            0,
            Color.White,
            Color.Transparent,
            8,
            ShapeKind.Rectangle);
        var scene = project.Scenes[0];
        var layerId = scene.Layers[0].Id;
        var track = scene.Timeline.FindTrackByTargetId(layerId)
            ?? throw new InvalidOperationException("Tween curve gesture regression lost the Scene Building track.");
        scene.Timeline.SetTrackDuration(track.Id, 9);
        AssertTimeline(
            project.TryAddSceneInstance(scene.Id, sourceObject.Id, PointF.Empty, 0, layerId, out var instance)
            && instance is not null,
            "Tween curve gesture regression could not create a Scene Building instance.");
        var sceneInstance = instance
            ?? throw new InvalidOperationException("Tween curve gesture regression lost its Scene Building instance.");
        var source = sceneInstance.EvaluateState(0) with { X = 100 };
        var target = source with { X = 900 };
        var setupReady = sceneInstance.SetStateAtFrame(0, source)
            && scene.Timeline.InsertKeyframe(track.Id, 8)
            && sceneInstance.SetStateAtFrame(8, target);
        var createError = string.Empty;
        var created = setupReady
            && scene.TryCreateTimelineTween(layerId, 0, 8, TimelineTweenKind.Classic, out createError);
        AssertTimeline(
            created,
            $"Tween curve gesture regression could not create its classic tween: {createError}");
        TweenCurveAnchor[] initialAnchors = [new(0, 0), new(0.5f, 0.5f), new(1, 1)];
        AssertTimeline(
            scene.ReplaceTimelineTweenCurve(layerId, 0, 8, initialAnchors),
            "Tween curve gesture regression could not install its editable anchor.");

        if (!form.Visible) form.Show();
        workspaceTabs.SelectedView = WorkspaceView.SceneEditor;
        Application.DoEvents();
        timelineStrip.RefreshTimeline();
        timelineStrip.SelectSingleFrame(track.Id, 4);
        Application.DoEvents();
        AssertTimeline(
            ReferenceEquals(timelineStrip.Context, scene)
            && panel.Enabled
            && panel.Visible
            && ReferenceEquals(panel.Parent, scenePage.Content),
            "Selecting a Scene Building tween did not show the existing curve panel in the scene inspector.");

        editor.Size = new Size(320, 160);
        _ = editor.Handle;
        var events = new List<string>();
        TweenCurveAnchor[]? committedAnchors = null;
        panel.InteractionStarted += (_, _) => events.Add("started");
        panel.CurveCommitted += (_, e) =>
        {
            committedAnchors = e.Anchors;
            events.Add("committed");
        };
        panel.InteractionCompleted += (_, _) => events.Add("completed");
        panel.InteractionCanceled += (_, _) => events.Add("canceled");
        var timelineChangedCount = 0;
        scene.Timeline.Changed += (_, _) => timelineChangedCount++;

        var initialMiddleX = sceneInstance.EvaluateState(4).X;
        var initialUndoCount = CollectionCount(sceneUndoStack);
        var initialPoint = CurrentAnchorPoint(1);
        mouseDown.Invoke(
            editor,
            [new MouseEventArgs(MouseButtons.Left, 1, initialPoint.X, initialPoint.Y, 0)]);
        foreach (var requested in new[]
                 {
                     new TweenCurveAnchor(0.56f, 0.42f),
                     new TweenCurveAnchor(0.63f, 0.31f),
                     new TweenCurveAnchor(0.70f, 0.20f)
                 })
        {
            var point = CurvePoint(requested);
            mouseMove.Invoke(
                editor,
                [new MouseEventArgs(MouseButtons.Left, 0, point.X, point.Y, 0)]);
            var modelAnchors = scene.Timeline.EvaluateTween(track.Id, 4)?.CurveAnchors ?? [];
            AssertTimeline(
                editor.SelectedAnchor is { } previewAnchor
                && !initialAnchors.Contains(previewAnchor)
                && modelAnchors.SequenceEqual(initialAnchors)
                && Math.Abs(sceneInstance.EvaluateState(4).X - initialMiddleX) <= 0.001f
                && timelineChangedCount == 0
                && CollectionCount(sceneUndoStack) == initialUndoCount
                && events.SequenceEqual(["started"]),
                "Dragging a tween curve anchor recalculated or committed the Scene Building tween before pointer release.");
        }

        var committedPoint = CurvePoint(new TweenCurveAnchor(0.70f, 0.20f));
        mouseUp.Invoke(
            editor,
            [new MouseEventArgs(MouseButtons.Left, 1, committedPoint.X, committedPoint.Y, 0)]);
        var committedTween = scene.Timeline.EvaluateTween(track.Id, 4)
            ?? throw new InvalidOperationException("Tween curve gesture regression lost the committed tween.");
        var committedMiddleX = sceneInstance.EvaluateState(4).X;
        AssertTimeline(
            committedAnchors is not null
            && events.SequenceEqual(["started", "committed", "completed"])
            && committedTween.CurveAnchors.SequenceEqual(committedAnchors)
            && !committedTween.CurveAnchors.SequenceEqual(initialAnchors)
            && Math.Abs(committedMiddleX - initialMiddleX) > 1
            && Math.Abs(committedMiddleX - (source.X + (target.X - source.X) * committedTween.ProgressAt(4))) <= 0.01f
            && timelineChangedCount == 1
            && CollectionCount(sceneUndoStack) == initialUndoCount + 1,
            "Pointer release did not commit and recalculate the Scene Building tween exactly once.");

        events.Clear();
        committedAnchors = null;
        var committedModelAnchors = committedTween.CurveAnchors;
        var cancelStart = CurrentAnchorPoint(1);
        mouseDown.Invoke(
            editor,
            [new MouseEventArgs(MouseButtons.Left, 1, cancelStart.X, cancelStart.Y, 0)]);
        foreach (var requested in new[]
                 {
                     new TweenCurveAnchor(0.58f, 0.45f),
                     new TweenCurveAnchor(0.52f, 0.60f)
                 })
        {
            var point = CurvePoint(requested);
            mouseMove.Invoke(
                editor,
                [new MouseEventArgs(MouseButtons.Left, 0, point.X, point.Y, 0)]);
        }
        keyDown.Invoke(editor, [new KeyEventArgs(Keys.Escape)]);
        AssertTimeline(
            committedAnchors is null
            && events.SequenceEqual(["started", "canceled"])
            && editor.SelectedAnchor is { } restoredAnchor
            && restoredAnchor == committedModelAnchors[1]
            && scene.Timeline.EvaluateTween(track.Id, 4)?.CurveAnchors.SequenceEqual(committedModelAnchors) == true
            && Math.Abs(sceneInstance.EvaluateState(4).X - committedMiddleX) <= 0.001f
            && timelineChangedCount == 1
            && CollectionCount(sceneUndoStack) == initialUndoCount + 1,
            "Canceling a tween curve gesture committed model changes or failed to restore the editor preview.");

        AssertTimeline(
            undo.Invoke(form, null) is true
            && scene.Timeline.EvaluateTween(track.Id, 4)?.CurveAnchors.SequenceEqual(initialAnchors) == true
            && Math.Abs(scene.Instances.Single(item => item.Id == sceneInstance.Id).EvaluateState(4).X - initialMiddleX) <= 0.001f
            && CollectionCount(sceneUndoStack) == initialUndoCount,
            "Undoing a tween curve gesture did not restore its curve and materialized Scene Building state as one unit.");
        timelineStrip.ClearSelectionFromEmptyArea();
        Application.DoEvents();
        AssertTimeline(!panel.Visible, "Clearing the tween selection did not hide the Scene Building curve panel.");

        AssertTimeline(
            sourceObject.Scene.InsertTimelineKeyframe(0, 8),
            "Tween curve gesture regression could not create a Basic Drawing endpoint.");
        var drawingTarget = Enumerable.Range(0, sourceObject.Scene.ObjectCount)
            .Single(index => sourceObject.Scene.ObjectKeyframeFrame[index] == 8);
        sourceObject.Scene.X[drawingTarget] = 200;
        AssertTimeline(
            sourceObject.TryCreateTimelineTween(0, 0, 8, TimelineTweenKind.Classic, out var drawingTweenError)
            && sourceObject.ReplaceTimelineTweenCurve(0, 0, 8, initialAnchors),
            $"Tween curve gesture regression could not create its Basic Drawing tween: {drawingTweenError}");
        workspaceTabs.SelectedView = WorkspaceView.BasicDrawing;
        Application.DoEvents();
        var drawingTrack = sourceObject.Timeline.FindTrackByTargetId(sourceObject.Scene.LayerIds[0])
            ?? throw new InvalidOperationException("Tween curve gesture regression lost the Basic Drawing track.");
        timelineStrip.RefreshTimeline();
        timelineStrip.SelectSingleFrame(drawingTrack.Id, 4);
        Application.DoEvents();
        AssertTimeline(
            ReferenceEquals(timelineStrip.Context, sourceObject)
            && panel.Enabled
            && panel.Visible
            && ReferenceEquals(panel.Parent, basicPage.Content),
            "Returning to Basic Drawing did not reattach the shared tween curve panel to its original inspector.");

        RunSceneLightTweenCurveDeferredCommitRegression(form, timelineStrip, undo);

        int CollectionCount(object collection) =>
            (int)(collection.GetType().GetProperty("Count")?.GetValue(collection) ?? -1);

        Point CurrentAnchorPoint(int index)
        {
            var anchors = displayAnchors.Invoke(editor, null) as TweenCurveAnchor[]
                ?? throw new InvalidOperationException("Tween curve gesture regression could not read display anchors.");
            return CurvePoint(anchors[index]);
        }

        Point CurvePoint(TweenCurveAnchor anchor)
        {
            var chart = (Rectangle)(chartBounds.Invoke(editor, null)
                ?? throw new InvalidOperationException("Tween curve gesture regression could not read chart bounds."));
            return new Point(
                (int)Math.Round(chart.Left + anchor.Time * chart.Width),
                (int)Math.Round(chart.Bottom - anchor.Value * chart.Height));
        }
    }

    private static void RunSceneLightTweenCurveDeferredCommitRegression(
        MainForm form,
        TimelineStrip timelineStrip,
        System.Reflection.MethodInfo undo)
    {
        var project = RequireField(typeof(MainForm), "_project").GetValue(form) as VectorProject
            ?? throw new InvalidOperationException("Light tween curve regression could not inspect the project.");
        var workspaceTabs = RequireField(typeof(MainForm), "_workspaceTabs").GetValue(form) as WorkspaceTabs
            ?? throw new InvalidOperationException("Light tween curve regression could not inspect the workspace tabs.");
        var panel = RequireField(typeof(MainForm), "_tweenCurveEditorPanel").GetValue(form) as TweenCurveEditorPanel
            ?? throw new InvalidOperationException("Light tween curve regression could not inspect the curve panel.");
        var sceneUndoStack = RequireField(typeof(MainForm), "_sceneTimelineUndoStack").GetValue(form)
            ?? throw new InvalidOperationException("Light tween curve regression could not inspect the scene undo stack.");
        var editor = RequireField(typeof(TweenCurveEditorPanel), "_editor").GetValue(panel) as TweenCurveEditor
            ?? throw new InvalidOperationException("Light tween curve regression could not inspect the curve editor.");
        var chartBounds = RequireMethod(typeof(TweenCurveEditor), "ChartBounds");
        var displayAnchors = RequireMethod(typeof(TweenCurveEditor), "DisplayAnchors");
        var mouseDown = RequireMethod(typeof(TweenCurveEditor), "OnMouseDown");
        var mouseMove = RequireMethod(typeof(TweenCurveEditor), "OnMouseMove");
        var mouseUp = RequireMethod(typeof(TweenCurveEditor), "OnMouseUp");
        var keyDown = RequireMethod(typeof(TweenCurveEditor), "OnKeyDown");
        var beginOpticsEdit = RequireMethod(typeof(MainForm), "BeginSceneOpticsEdit");
        var applyResolvedLightEdit = RequireMethod(typeof(MainForm), "ApplyResolvedSceneLightEdit");
        var cancelOpticsEdit = RequireMethod(typeof(MainForm), "CancelSceneOpticsEdit");
        var completeOpticsEdit = RequireMethod(typeof(MainForm), "CompleteSceneOpticsEdit");
        var saveProjectTo = RequireMethod(typeof(MainForm), "SaveProjectTo");
        var manifestPathField = RequireField(typeof(MainForm), "_projectManifestPath");
        var projectDirtyField = RequireField(typeof(MainForm), "_projectDirty");
        var opticsSnapshotField = RequireField(typeof(MainForm), "_sceneOpticsEditSnapshot");
        var opticsSavedSnapshotField = RequireField(typeof(MainForm), "_sceneOpticsSavedSnapshot");
        var pendingMaterializationsField = RequireField(
            typeof(MainForm),
            "_pendingSceneLightTweenMaterializations");

        var scene = project.Scenes[0];
        AssertTimeline(
            scene.Dimension == SceneDimension.ThreeD
            || project.TrySetSceneDimension(scene.Id, SceneDimension.ThreeD),
            "Light tween curve regression could not enable the 3D scene.");
        AssertTimeline(
            project.TryAddSceneLight(scene.Id, SceneLightKind.Point, out var addedLight)
            && addedLight is not null,
            "Light tween curve regression could not create a point light.");
        var light = addedLight
            ?? throw new InvalidOperationException("Light tween curve regression lost its point light.");
        var start = light.Settings with
        {
            Intensity = 1.25f,
            Range = 5000,
            Position = new Vector3(120, -60, 240)
        };
        var end = start with
        {
            Intensity = 5.75f,
            Range = 15000,
            Position = new Vector3(900, 420, -180),
            RotationDegrees = new Vector3(15, 210, -20)
        };
        const int endFrame = 8;
        TweenCurveAnchor[] initialAnchors = [new(0, 0), new(0.5f, 0.5f), new(1, 1)];
        var setupError = string.Empty;
        AssertTimeline(
            project.TryUpdateSceneLightAtFrame(scene.Id, light.Id, light.Name, start, 0)
            && scene.InsertLightTimelineKeyframe(light.Id, endFrame)
            && project.TryUpdateSceneLightAtFrame(scene.Id, light.Id, light.Name, end, endFrame)
            && scene.TryCreateTimelineTween(
                light.Id,
                0,
                endFrame,
                TimelineTweenKind.Classic,
                out setupError)
            && scene.ReplaceTimelineTweenCurve(light.Id, 0, endFrame, initialAnchors),
            $"Light tween curve regression could not create its Classic tween: {setupError}");
        var track = scene.Timeline.FindTrackByTargetId(light.Id)
            ?? throw new InvalidOperationException("Light tween curve regression lost the light track.");

        workspaceTabs.SelectedView = WorkspaceView.SceneEditor;
        Application.DoEvents();
        timelineStrip.RefreshTimeline();
        timelineStrip.SelectSingleFrame(track.Id, 4);
        Application.DoEvents();
        AssertTimeline(
            ReferenceEquals(timelineStrip.Context, scene)
            && panel.Enabled
            && panel.Visible,
            "Selecting a light tween did not expose the Scene Building curve editor.");

        editor.Size = new Size(320, 160);
        _ = editor.Handle;
        var events = new List<string>();
        TweenCurveAnchor[]? committedAnchors = null;
        panel.InteractionStarted += (_, _) => events.Add("started");
        panel.CurveCommitted += (_, e) =>
        {
            committedAnchors = e.Anchors;
            events.Add("committed");
        };
        panel.InteractionCompleted += (_, _) => events.Add("completed");
        panel.InteractionCanceled += (_, _) => events.Add("canceled");
        var timelineChangedCount = 0;
        scene.Timeline.Changed += (_, _) => timelineChangedCount++;
        var initialMaterialized = light.EvaluateSettings(4);
        var initialUndoCount = CollectionCount(sceneUndoStack);

        var cancelStart = CurrentAnchorPoint(1);
        mouseDown.Invoke(
            editor,
            [new MouseEventArgs(MouseButtons.Left, 1, cancelStart.X, cancelStart.Y, 0)]);
        foreach (var requested in new[]
                 {
                     new TweenCurveAnchor(0.58f, 0.42f),
                     new TweenCurveAnchor(0.66f, 0.28f)
                 })
        {
            var point = CurvePoint(requested);
            mouseMove.Invoke(
                editor,
                [new MouseEventArgs(MouseButtons.Left, 0, point.X, point.Y, 0)]);
            AssertTimeline(
                scene.Timeline.EvaluateTween(track.Id, 4)?.CurveAnchors.SequenceEqual(initialAnchors) == true
                && light.EvaluateSettings(4) == initialMaterialized
                && timelineChangedCount == 0
                && CollectionCount(sceneUndoStack) == initialUndoCount,
                "Dragging a light tween curve eagerly committed or materialized the span.");
        }
        keyDown.Invoke(editor, [new KeyEventArgs(Keys.Escape)]);
        AssertTimeline(
            committedAnchors is null
            && events.SequenceEqual(["started", "canceled"])
            && scene.Timeline.EvaluateTween(track.Id, 4)?.CurveAnchors.SequenceEqual(initialAnchors) == true
            && light.EvaluateSettings(4) == initialMaterialized
            && timelineChangedCount == 0
            && CollectionCount(sceneUndoStack) == initialUndoCount,
            "Canceling a light tween curve gesture changed the model or materialized light frames.");

        events.Clear();
        var commitStart = CurrentAnchorPoint(1);
        var requestedCommit = new TweenCurveAnchor(0.72f, 0.18f);
        var commitPoint = CurvePoint(requestedCommit);
        mouseDown.Invoke(
            editor,
            [new MouseEventArgs(MouseButtons.Left, 1, commitStart.X, commitStart.Y, 0)]);
        mouseMove.Invoke(
            editor,
            [new MouseEventArgs(MouseButtons.Left, 0, commitPoint.X, commitPoint.Y, 0)]);
        AssertTimeline(
            scene.Timeline.EvaluateTween(track.Id, 4)?.CurveAnchors.SequenceEqual(initialAnchors) == true
            && light.EvaluateSettings(4) == initialMaterialized,
            "A light tween curve changed before pointer release.");
        mouseUp.Invoke(
            editor,
            [new MouseEventArgs(MouseButtons.Left, 1, commitPoint.X, commitPoint.Y, 0)]);
        var committedTween = scene.Timeline.EvaluateTween(track.Id, 4)
            ?? throw new InvalidOperationException("Light tween curve regression lost the committed tween.");
        var committedMaterialized = light.EvaluateSettings(4);
        AssertTimeline(
            committedAnchors is not null
            && events.SequenceEqual(["started", "committed", "completed"])
            && committedTween.CurveAnchors.SequenceEqual(committedAnchors)
            && !committedTween.CurveAnchors.SequenceEqual(initialAnchors)
            && committedMaterialized == SceneLightSettings.Interpolate(
                light.Kind,
                start,
                end,
                committedTween.ProgressAt(4))
            && committedMaterialized != initialMaterialized
            && timelineChangedCount == 1
            && CollectionCount(sceneUndoStack) == initialUndoCount + 1,
            "Pointer release did not commit and materialize the light tween exactly once.");
        AssertTimeline(
            undo.Invoke(form, null) is true
            && scene.Timeline.EvaluateTween(track.Id, 4)?.CurveAnchors.SequenceEqual(initialAnchors) == true
            && scene.FindLight(light.Id)?.EvaluateSettings(4) == initialMaterialized
            && CollectionCount(sceneUndoStack) == initialUndoCount,
            "Undoing a light tween curve did not restore its curve and materialized settings as one unit.");
        light = scene.FindLight(light.Id)
            ?? throw new InvalidOperationException("Light tween curve undo lost the point light.");

        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "Vector2DAnimationEngine",
            $"active-light-save-regression-{Guid.NewGuid():N}");
        var manifestPath = Path.Combine(temporaryRoot, "ActiveLightSave.v2dProject");
        var originalManifestPath = (string?)manifestPathField.GetValue(form) ?? string.Empty;
        try
        {
            var editedStart = start with
            {
                Intensity = start.Intensity + 2.25f,
                Position = start.Position + new Vector3(360, -140, 90)
            };
            var tween = scene.Timeline.FindTrackByTargetId(light.Id)?.EvaluateTween(4)
                ?? throw new InvalidOperationException("Light save regression lost the easing curve.");
            var expectedSavedIntermediate = SceneLightSettings.Interpolate(
                light.Kind,
                editedStart,
                end,
                tween.ProgressAt(4));
            var materializedBeforePreview = light.EvaluateSettings(4);

            beginOpticsEdit.Invoke(form, null);
            AssertTimeline(
                applyResolvedLightEdit.Invoke(
                    form,
                    [scene, light, light.Name, editedStart, 0, start]) is true
                && scene.TryEvaluateLightSettings(light.Id, 4, out var previewed)
                && previewed == expectedSavedIntermediate
                && light.EvaluateSettings(4) == materializedBeforePreview,
                "An active light endpoint edit did not remain deferred before Save Project.");

            AssertTimeline(
                saveProjectTo.Invoke(form, [manifestPath]) is true
                && opticsSnapshotField.GetValue(form) is not null
                && opticsSavedSnapshotField.GetValue(form) is not null
                && projectDirtyField.GetValue(form) is false
                && CollectionCount(pendingMaterializationsField.GetValue(form)!) == 0
                && light.EvaluateSettings(4) == expectedSavedIntermediate,
                "Save Project did not flush the active deferred light tween without ending its edit session.");

            var restored = ProjectVaultStore.Load(manifestPath);
            var restoredScene = restored.Scenes.Single(candidate => candidate.Id == scene.Id);
            var restoredLight = restoredScene.FindLight(light.Id)
                ?? throw new InvalidOperationException("The saved project lost the edited light.");
            var restoredLightTrack = restoredScene.Timeline.FindTrackByTargetId(light.Id)
                ?? throw new InvalidOperationException("The saved project lost the edited light track.");
            AssertTimeline(
                restoredScene.RemoveTimelineTween(light.Id, 0, endFrame)
                && restoredLightTrack.Keyframes.All(keyframe =>
                    keyframe.Frame == 0 || keyframe.Frame == endFrame)
                && restoredLight.StateKeyframes.All(keyframe =>
                    keyframe.Frame == 0 || keyframe.Frame == endFrame)
                && restoredLight.EvaluateSettings(4) == editedStart,
                "Reloading and removing the saved light tween did not restore its endpoint-only state.");

            var undoCountBeforeCompletion = CollectionCount(sceneUndoStack);
            completeOpticsEdit.Invoke(form, null);
            AssertTimeline(
                opticsSnapshotField.GetValue(form) is null
                && opticsSavedSnapshotField.GetValue(form) is null
                && projectDirtyField.GetValue(form) is false
                && CollectionCount(sceneUndoStack) == undoCountBeforeCompletion + 1,
                "Completing an edit immediately after Save Project lost its undo or marked the saved state dirty.");

            light = scene.FindLight(light.Id)
                ?? throw new InvalidOperationException("The saved light disappeared before cancel validation.");
            var canceledStart = editedStart with
            {
                Intensity = editedStart.Intensity + 1.5f,
                Position = editedStart.Position + new Vector3(-220, 180, 60)
            };
            beginOpticsEdit.Invoke(form, null);
            AssertTimeline(
                applyResolvedLightEdit.Invoke(
                    form,
                    [scene, light, light.Name, canceledStart, 0, editedStart]) is true
                && saveProjectTo.Invoke(form, [manifestPath]) is true
                && projectDirtyField.GetValue(form) is false,
                "The light cancel-after-save regression could not save its second endpoint state.");
            cancelOpticsEdit.Invoke(form, null);
            light = scene.FindLight(light.Id)
                ?? throw new InvalidOperationException("Canceling the saved light edit lost the point light.");
            var canceledDiskProject = ProjectVaultStore.Load(manifestPath);
            var canceledDiskScene = canceledDiskProject.Scenes.Single(candidate => candidate.Id == scene.Id);
            var canceledDiskLight = canceledDiskScene.FindLight(light.Id)
                ?? throw new InvalidOperationException("The cancel-after-save project lost the point light.");
            var canceledDiskLightTrack = canceledDiskScene.Timeline.FindTrackByTargetId(light.Id)
                ?? throw new InvalidOperationException("The cancel-after-save project lost the point-light track.");
            AssertTimeline(
                opticsSnapshotField.GetValue(form) is null
                && opticsSavedSnapshotField.GetValue(form) is null
                && projectDirtyField.GetValue(form) is true
                && light.EvaluateSettings(0) == editedStart
                && canceledDiskLight.EvaluateSettings(0) == canceledStart
                && canceledDiskScene.RemoveTimelineTween(light.Id, 0, endFrame)
                && canceledDiskLightTrack.Keyframes.All(keyframe =>
                    keyframe.Frame == 0 || keyframe.Frame == endFrame)
                && canceledDiskLight.StateKeyframes.All(keyframe =>
                    keyframe.Frame == 0 || keyframe.Frame == endFrame)
                && canceledDiskLight.EvaluateSettings(4) == canceledStart,
                "Canceling after Save Project failed to mark the disk-divergent restored state dirty.");
            Console.WriteLine("scene_light_tween_curve_deferred_commit=ok");
            Console.WriteLine("scene_light_active_edit_save_materialization=ok");
            Console.WriteLine("scene_light_active_edit_save_checkpoint=ok");
        }
        finally
        {
            if (opticsSnapshotField.GetValue(form) is not null) cancelOpticsEdit.Invoke(form, null);
            manifestPathField.SetValue(form, originalManifestPath);
            try
            {
                if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
            }
            catch
            {
                // Each regression run uses an isolated temporary directory.
            }
        }

        int CollectionCount(object collection) =>
            (int)(collection.GetType().GetProperty("Count")?.GetValue(collection) ?? -1);

        Point CurrentAnchorPoint(int index)
        {
            var anchors = displayAnchors.Invoke(editor, null) as TweenCurveAnchor[]
                ?? throw new InvalidOperationException("Light tween curve regression could not read display anchors.");
            return CurvePoint(anchors[index]);
        }

        Point CurvePoint(TweenCurveAnchor anchor)
        {
            var chart = (Rectangle)(chartBounds.Invoke(editor, null)
                ?? throw new InvalidOperationException("Light tween curve regression could not read chart bounds."));
            return new Point(
                (int)Math.Round(chart.Left + anchor.Time * chart.Width),
                (int)Math.Round(chart.Bottom - anchor.Value * chart.Height));
        }
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

        var frameContextMenu = RequireField(typeof(TimelineStrip), "_frameContextMenu").GetValue(timelineStrip)
            as ContextMenuStrip
            ?? throw new InvalidOperationException("Timeline frame command regression could not find the frame context menu.");
        var frameContextOpening = RequireMethod(typeof(TimelineStrip), "HandleFrameContextMenuOpening");
        var setFrameSelection = RequireMethod(typeof(TimelineStrip), "SetFrameSelection");
        var reverseMenuItem = frameContextMenu.Items
            .Cast<ToolStripItem>()
            .SingleOrDefault(item => item.Text is "Reverse Frames" or "翻转帧")
            ?? throw new InvalidOperationException("Timeline frame context menu did not expose Reverse Frames.");
        var originalLanguage = UiLocalization.CurrentLanguage;
        try
        {
            UiLocalization.SetLanguage(UiLanguage.English);
            timelineStrip.SelectSingleFrame(emptyTrack.Id, 2);
            frameContextOpening.Invoke(
                timelineStrip,
                new object?[] { frameContextMenu, new System.ComponentModel.CancelEventArgs() });
            var singleFrameEnabled = reverseMenuItem.Enabled;

            setFrameSelection.Invoke(
                timelineStrip,
                new object?[]
                {
                    new[]
                    {
                        new TimelineFrameCell(emptyTrack.Id, 2),
                        new TimelineFrameCell(emptyTrack.Id, 3)
                    },
                    new TimelineFrameCell(emptyTrack.Id, 2)
                });
            frameContextOpening.Invoke(
                timelineStrip,
                new object?[] { frameContextMenu, new System.ComponentModel.CancelEventArgs() });
            var multipleFramesEnabled = reverseMenuItem.Enabled;

            UiLocalization.SetLanguage(UiLanguage.SimplifiedChinese);
            AssertTimeline(
                !singleFrameEnabled
                && multipleFramesEnabled
                && reverseMenuItem.Text == "翻转帧"
                && UiLocalization.T("Reverse Frames") == "翻转帧",
                "Timeline frame context menu did not gate Reverse Frames by selection size or localize it to Simplified Chinese.");
        }
        finally
        {
            UiLocalization.SetLanguage(originalLanguage);
        }

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
        using var layeredTimelineStrip = new TimelineStrip(layeredScene) { Size = new Size(760, 222) };
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

    private static void RunTimelineFrameTransformMappingRegression()
    {
        var original = new TimelineFrameSelectionBounds(2, 4, 1, 2);
        var moved = TimelineStrip.ResolveFrameTransformTargetBounds(
            original,
            TimelineFrameTransformMode.Move,
            pointerFrame: 8,
            pointerTrackPosition: 4,
            moveStartFrame: 2,
            moveStartTrackPosition: 1,
            minimumFrame: 0,
            maximumFrame: 20,
            minimumTrackPosition: 0,
            maximumTrackPosition: 6);
        var clampedToStart = TimelineStrip.ResolveFrameTransformTargetBounds(
            original,
            TimelineFrameTransformMode.Move,
            pointerFrame: -100,
            pointerTrackPosition: -100,
            moveStartFrame: 2,
            moveStartTrackPosition: 1,
            minimumFrame: 0,
            maximumFrame: 20,
            minimumTrackPosition: 0,
            maximumTrackPosition: 6);
        var clampedToEnd = TimelineStrip.ResolveFrameTransformTargetBounds(
            original,
            TimelineFrameTransformMode.Move,
            pointerFrame: 100,
            pointerTrackPosition: 100,
            moveStartFrame: 2,
            moveStartTrackPosition: 1,
            minimumFrame: 0,
            maximumFrame: 20,
            minimumTrackPosition: 0,
            maximumTrackPosition: 6);
        AssertTimeline(
            moved == new TimelineFrameSelectionBounds(8, 10, 4, 5)
            && clampedToStart == new TimelineFrameSelectionBounds(0, 2, 0, 1)
            && clampedToEnd == new TimelineFrameSelectionBounds(18, 20, 5, 6),
            "Timeline frame transform movement did not preserve selection size or clamp to the available grid bounds.");

        var horizontalScale = TimelineStrip.ResolveFrameTransformTargetBounds(
            original,
            TimelineFrameTransformMode.ScaleLeft,
            pointerFrame: 0,
            pointerTrackPosition: 1,
            moveStartFrame: 0,
            moveStartTrackPosition: 0,
            minimumFrame: 0,
            maximumFrame: 20,
            minimumTrackPosition: 0,
            maximumTrackPosition: 6);
        var verticalScale = TimelineStrip.ResolveFrameTransformTargetBounds(
            original,
            TimelineFrameTransformMode.ScaleBottom,
            pointerFrame: 4,
            pointerTrackPosition: 5,
            moveStartFrame: 0,
            moveStartTrackPosition: 0,
            minimumFrame: 0,
            maximumFrame: 20,
            minimumTrackPosition: 0,
            maximumTrackPosition: 6);
        var cornerScale = TimelineStrip.ResolveFrameTransformTargetBounds(
            original,
            TimelineFrameTransformMode.ScaleTopLeft,
            pointerFrame: 0,
            pointerTrackPosition: 0,
            moveStartFrame: 0,
            moveStartTrackPosition: 0,
            minimumFrame: 0,
            maximumFrame: 20,
            minimumTrackPosition: 0,
            maximumTrackPosition: 6);
        AssertTimeline(
            horizontalScale == new TimelineFrameSelectionBounds(0, 4, 1, 2)
            && verticalScale == new TimelineFrameSelectionBounds(2, 4, 1, 5)
            && cornerScale == new TimelineFrameSelectionBounds(0, 4, 0, 2),
            "Timeline frame transform edge and corner scaling did not resize the expected axes.");

        var visibleTrackIds = new[] { "track-a", "track-b", "track-c" };
        var moveOriginal = new TimelineFrameSelectionBounds(2, 4, 0, 1);
        var sourceCells = Enumerable.Range(moveOriginal.FirstTrackPosition, moveOriginal.TrackCount)
            .SelectMany(trackPosition => Enumerable.Range(moveOriginal.FirstFrame, moveOriginal.FrameCount)
                .Select(frame => new TimelineFrameCell(visibleTrackIds[trackPosition], frame)))
            .ToArray();
        var movePairs = TimelineStrip.ResolveFrameTransformCells(
            sourceCells,
            visibleTrackIds,
            moveOriginal,
            new TimelineFrameSelectionBounds(5, 7, 1, 2),
            TimelineFrameTransformMode.Move);
        var expectedMovedCells = new HashSet<TimelineFrameCell>(
        [
            new("track-b", 5), new("track-b", 6), new("track-b", 7),
            new("track-c", 5), new("track-c", 6), new("track-c", 7)
        ]);
        AssertTimeline(
            movePairs.Count == sourceCells.Length
            && movePairs.Select(pair => pair.Destination).ToHashSet().SetEquals(expectedMovedCells)
            && movePairs.All(pair => pair.Destination.Frame == pair.Source.Frame + 3),
            "Timeline frame transform movement did not preserve every source cell's relative offset.");

        var expandedTarget = new TimelineFrameSelectionBounds(2, 8, 0, 2);
        var expandedPairs = TimelineStrip.ResolveFrameTransformCells(
            sourceCells,
            visibleTrackIds,
            moveOriginal,
            expandedTarget,
            TimelineFrameTransformMode.ScaleBottomRight);
        var expandedDestinations = expandedPairs.Select(pair => pair.Destination).ToHashSet();
        var expectedExpandedDestinations = Enumerable.Range(expandedTarget.FirstTrackPosition, expandedTarget.TrackCount)
            .SelectMany(trackPosition => Enumerable.Range(expandedTarget.FirstFrame, expandedTarget.FrameCount)
                .Select(frame => new TimelineFrameCell(visibleTrackIds[trackPosition], frame)))
            .ToHashSet();
        AssertTimeline(
            expandedPairs.Count == expectedExpandedDestinations.Count
            && expandedDestinations.SetEquals(expectedExpandedDestinations)
            && expandedPairs.Select(pair => pair.Source).ToHashSet().SetEquals(sourceCells),
            "Timeline frame transform enlargement did not fill the target rectangle from the selected source cells.");

        var compressedOriginal = new TimelineFrameSelectionBounds(2, 8, 0, 2);
        var compressedSourceCells = Enumerable.Range(compressedOriginal.FirstTrackPosition, compressedOriginal.TrackCount)
            .SelectMany(trackPosition => Enumerable.Range(compressedOriginal.FirstFrame, compressedOriginal.FrameCount)
                .Select(frame => new TimelineFrameCell(visibleTrackIds[trackPosition], frame)))
            .ToArray();
        var compressedTarget = new TimelineFrameSelectionBounds(2, 4, 0, 1);
        var compressedPairs = TimelineStrip.ResolveFrameTransformCells(
            compressedSourceCells,
            visibleTrackIds,
            compressedOriginal,
            compressedTarget,
            TimelineFrameTransformMode.ScaleBottomRight);
        var compressedDestinations = compressedPairs.Select(pair => pair.Destination).ToHashSet();
        AssertTimeline(
            compressedPairs.Count == compressedTarget.FrameCount * compressedTarget.TrackCount
            && compressedDestinations.Count == compressedPairs.Count
            && compressedDestinations.All(cell => cell.Frame is >= 2 and <= 4)
            && compressedDestinations.All(cell => cell.TrackId is "track-a" or "track-b"),
            "Timeline frame transform compression did not resolve source collisions to unique target cells.");
    }

    private static void RunTimelineFrameTransformCommandRegression()
    {
        var transform = RequireMethod(typeof(MainForm), "TransformTimelineFrames");
        var undo = RequireMethod(typeof(MainForm), "UndoLastEdit");
        var timelineField = RequireField(typeof(MainForm), "_timeline");
        var workspaceTabsField = RequireField(typeof(MainForm), "_workspaceTabs");
        var undoStackField = RequireField(typeof(MainForm), "_undoStack");

        using var form = new MainForm();
        var workspaceTabs = workspaceTabsField.GetValue(form) as WorkspaceTabs
            ?? throw new InvalidOperationException("Timeline transform regression did not find the workspace tabs.");
        workspaceTabs.SelectedView = WorkspaceView.BasicDrawing;
        Application.DoEvents();
        var timelineStrip = timelineField.GetValue(form) as TimelineStrip
            ?? throw new InvalidOperationException("Timeline transform regression did not find the timeline control.");
        var scene = timelineStrip.Context switch
        {
            VectorScene vectorScene => vectorScene,
            DrawingObjectDefinition objectDefinition => objectDefinition.Scene,
            _ => null
        } ?? throw new InvalidOperationException("Timeline transform regression did not bind a drawing scene.");
        scene.CreateEmpty(layers: 1);
        scene.EditFrame = 0;
        var objectIndex = scene.AddObject(
            0,
            PointF.Empty,
            new SizeF(20, 20),
            0,
            0,
            Color.Teal,
            6,
            ShapeKind.Rectangle);
        var track = scene.Timeline.FindTrackByTargetId(scene.LayerIds[0])
            ?? throw new InvalidOperationException("Timeline transform regression lost its drawing track.");
        scene.Timeline.SetTrackDuration(track.Id, 8);
        timelineStrip.RefreshTimeline();
        var sourceCell = new TimelineFrameCell(track.Id, 0);
        var destinationCell = new TimelineFrameCell(track.Id, 2);
        timelineStrip.SelectFrameCells([sourceCell], sourceCell);
        var initialUndoCount = ((System.Collections.ICollection)(undoStackField.GetValue(form)
            ?? throw new InvalidOperationException("Timeline transform regression lost the undo stack."))).Count;
        var timelineChangedCount = 0;
        scene.Timeline.Changed += (_, _) => timelineChangedCount++;

        var transformed = transform.Invoke(
            form,
            [new TimelineFrameTransformRequestedEventArgs(
                TimelineFrameTransformMode.Move,
                [new TimelineFrameTransformCell(sourceCell, destinationCell)])]) is true;
        var movedObject = objectIndex < scene.ObjectCount && scene.IsObjectActive(objectIndex, 2);
        var sourceIsBlank = !scene.Timeline.EvaluateExposure(track.Id, 0).HasContent;
        var destinationHasContent = scene.Timeline.EvaluateExposure(track.Id, 2).HasContent;
        var movedSelection = timelineStrip.SelectedFrameCells.SequenceEqual([destinationCell]);
        var undoCountAfterTransform = ((System.Collections.ICollection)undoStackField.GetValue(form)!).Count;
        AssertTimeline(
            transformed
            && movedObject
            && sourceIsBlank
            && destinationHasContent
            && movedSelection
            && timelineChangedCount == 1
            && undoCountAfterTransform == initialUndoCount + 1,
            "Timeline frame transform did not commit its drawing Cel, selection, notification, and undo state as one operation.");

        var undone = undo.Invoke(form, null) is true;
        var restoredObject = objectIndex < scene.ObjectCount && scene.IsObjectActive(objectIndex, 0);
        var restoredSource = scene.Timeline.EvaluateExposure(track.Id, 0).HasContent;
        var restoredDestination = !scene.Timeline.EvaluateExposure(track.Id, 2).IsKeyframe;
        AssertTimeline(
            undone
            && restoredObject
            && restoredSource
            && restoredDestination
            && timelineStrip.SelectedFrameCells.SequenceEqual([sourceCell]),
            "Undoing a timeline frame transform did not restore the source Cel, remove the destination, and recover selection.");

        var drawingObject = timelineStrip.Context as DrawingObjectDefinition
            ?? throw new InvalidOperationException("Timeline transform regression did not bind the drawing-object timeline context.");
        var projectField = RequireField(typeof(MainForm), "_project");
        var project = projectField.GetValue(form) as VectorProject
            ?? throw new InvalidOperationException("Timeline transform regression did not find the project.");
        scene.CreateEmpty(layers: 2);
        drawingObject.SynchronizeTimelineTracks();
        var sourceLayerId = scene.LayerIds[0];
        var targetLayerId = scene.LayerIds[1];
        var sourceTrack = scene.Timeline.FindTrackByTargetId(sourceLayerId)
            ?? throw new InvalidOperationException("Timeline cross-layer transform regression lost its source track.");
        var targetTrack = scene.Timeline.FindTrackByTargetId(targetLayerId)
            ?? throw new InvalidOperationException("Timeline cross-layer transform regression lost its target track.");
        scene.Timeline.SetTrackDuration(sourceTrack.Id, 4);
        scene.Timeline.SetTrackDuration(targetTrack.Id, 4);
        scene.EditFrame = 0;
        scene.AddObject(
            0,
            PointF.Empty,
            new SizeF(20, 20),
            0,
            0,
            Color.Teal,
            6,
            ShapeKind.Rectangle);
        scene.AddObject(
            1,
            new PointF(320, 0),
            new SizeF(20, 20),
            0,
            0,
            Color.Coral,
            6,
            ShapeKind.Ellipse);
        AssertTimeline(
            scene.InsertTimelineKeyframe(1, 2),
            "Timeline cross-layer transform regression could not establish the destination Cel.");
        timelineStrip.RefreshTimeline();
        var celSourceCell = new TimelineFrameCell(sourceTrack.Id, 0);
        var celDestinationCell = new TimelineFrameCell(targetTrack.Id, 2);
        timelineStrip.SelectFrameCells([celSourceCell], celSourceCell);
        var celTransformed = transform.Invoke(
            form,
            [new TimelineFrameTransformRequestedEventArgs(
                TimelineFrameTransformMode.Move,
                [new TimelineFrameTransformCell(celSourceCell, celDestinationCell)])]) is true;
        var activeDestinationObjects = Enumerable.Range(0, scene.ObjectCount)
            .Where(index => scene.IsObjectActive(index, 2))
            .ToArray();
        AssertTimeline(
            celTransformed
            && !scene.Timeline.EvaluateExposure(sourceTrack.Id, 0).HasContent
            && activeDestinationObjects.Length == 1
            && scene.Argb[activeDestinationObjects[0]] == Color.Teal.ToArgb()
            && scene.TimelineObjectCountForKeyframe(1, 2) == 1,
            "Cross-layer timeline frame movement did not overwrite the destination drawing Cel.");

        var child = project.AddDrawingObject("Timeline transform child");
        DrawingObjectInstanceDefinition? sourceInstance = null;
        DrawingObjectInstanceDefinition? targetInstance = null;
        var instancesAdded = project.TryAddDrawingObjectInstance(
                drawingObject.Id,
                child.Id,
                PointF.Empty,
                sourceLayerId,
                out sourceInstance)
            && sourceInstance is not null
            && project.TryAddDrawingObjectInstance(
                drawingObject.Id,
                child.Id,
                PointF.Empty,
                targetLayerId,
                out targetInstance)
            && targetInstance is not null;
        AssertTimeline(
            instancesAdded,
            "Timeline cross-layer transform regression could not create source and target instances.");
        var sourceInstanceState = sourceInstance!.EvaluateState(0) with { X = 120, Y = 30 };
        var targetInstanceState = targetInstance!.EvaluateState(0) with { X = 820, Y = 40 };
        var targetFrameState = targetInstanceState with { X = 920, Y = 50 };
        AssertTimeline(
            sourceInstance.SetStateAtFrame(0, sourceInstanceState)
            && targetInstance.SetStateAtFrame(0, targetInstanceState)
            && targetTrack.EvaluateExposure(2).IsKeyframe
            && targetTrack.EvaluateExposure(2).HasContent
            && targetInstance.SetStateAtFrame(2, targetFrameState),
            "Timeline cross-layer transform regression could not establish distinct instance states.");
        timelineStrip.RefreshTimeline();
        var crossLayerSourceCell = new TimelineFrameCell(sourceTrack.Id, 0);
        var crossLayerDestinationCell = new TimelineFrameCell(targetTrack.Id, 2);
        timelineStrip.SelectFrameCells([crossLayerSourceCell], crossLayerSourceCell);
        var crossLayerTransformed = transform.Invoke(
            form,
            [new TimelineFrameTransformRequestedEventArgs(
                TimelineFrameTransformMode.Move,
                [new TimelineFrameTransformCell(crossLayerSourceCell, crossLayerDestinationCell)])]) is true;
        var movedTargetState = targetInstance.EvaluateState(2);
        AssertTimeline(
            crossLayerTransformed
            && !scene.Timeline.EvaluateExposure(sourceTrack.Id, 0).HasContent
            && scene.Timeline.EvaluateExposure(targetTrack.Id, 2).HasContent
            && NearlyEqual(movedTargetState.X, sourceInstanceState.X)
            && NearlyEqual(movedTargetState.Y, sourceInstanceState.Y),
            "Cross-layer timeline frame movement did not overwrite the target instance state with the source frame.");
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
        static string[] ResolveSceneLayerOrder(
            int sourceIndex,
            int targetIndex,
            TimelineLayerDropPlacement placement)
        {
            var layers = new List<string> { "top", "middle", "bottom" };
            var destinationIndex = MainForm.ResolveSceneLayerDestinationIndex(
                sourceIndex,
                targetIndex,
                placement,
                layers.Count);
            var layer = layers[sourceIndex];
            layers.RemoveAt(sourceIndex);
            layers.Insert(destinationIndex, layer);
            return layers.ToArray();
        }

        AssertTimeline(
            ResolveSceneLayerOrder(1, 0, TimelineLayerDropPlacement.Before)
                .SequenceEqual(["middle", "top", "bottom"])
            && ResolveSceneLayerOrder(1, 2, TimelineLayerDropPlacement.After)
                .SequenceEqual(["top", "bottom", "middle"])
            && ResolveSceneLayerOrder(0, 2, TimelineLayerDropPlacement.Before)
                .SequenceEqual(["middle", "top", "bottom"])
            && ResolveSceneLayerOrder(0, 2, TimelineLayerDropPlacement.After)
                .SequenceEqual(["middle", "bottom", "top"])
            && ResolveSceneLayerOrder(2, 0, TimelineLayerDropPlacement.Before)
                .SequenceEqual(["bottom", "top", "middle"])
            && ResolveSceneLayerOrder(2, 0, TimelineLayerDropPlacement.After)
                .SequenceEqual(["top", "bottom", "middle"]),
            "Scene-layer moves skipped a row or landed on the wrong side of their target after source removal.");

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

        orderingContent = Array.IndexOf(maskOrderingScene.LayerIds, orderingContentId);
        AssertTimeline(
            maskOrderingScene.MoveLayerOutOfMask(orderingContent)
            && !maskOrderingScene.TryGetMaskLayerIndex(
                Array.IndexOf(maskOrderingScene.LayerIds, orderingContentId),
                out _)
            && maskOrderingScene.LayerIds.SequenceEqual([orderingMaskId, orderingContentId, orderingSiblingId])
            && maskOrderingScene.GetLayerDisplayOrder()
                .Select(layer => maskOrderingScene.LayerIds[layer])
                .SequenceEqual([orderingMaskId, orderingContentId, orderingSiblingId]),
            "Moving content out of a mask did not detach it or preserve its visible position after the mask.");

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
            && scene.ObjectLayer.AsSpan(0, scene.ObjectCount).SequenceEqual(new ushort[] { 0, 1 })
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

    private static void RunSceneMaskTimelineRegression()
    {
        const int populatedFrame = 8;
        const int blankFrame = 12;
        const int insertionFrame = 5;
        const int insertedFrameCount = 2;

        var project = VectorProject.CreateEmpty();
        var scene = project.Scenes[0];
        var contentLayer = scene.Layers.First(layer => layer.Kind == SceneLayerKind.Content);
        AssertTimeline(
            project.TryAddSceneMaskLayer(scene.Id, contentLayer.Id, out var maskLayer, "Timeline mask")
            && maskLayer is not null,
            "Scene mask timeline regression could not create a mask layer.");

        var maskLayerId = maskLayer!.Id;
        var maskScene = scene.FindMaskScene(maskLayerId)
            ?? throw new InvalidOperationException("Scene mask timeline regression lost its editable mask scene.");
        var secondInnerLayer = maskScene.AddLayer("Second mask plane");
        scene.SynchronizeTimelineTracks();
        var innerLayerIds = maskScene.LayerIds.ToArray();

        AnimationTimelineTrack OuterTrack()
        {
            return scene.Timeline.FindTrackByTargetId(maskLayerId)
                ?? throw new InvalidOperationException("Scene mask timeline regression lost the outer mask track.");
        }

        AnimationTimelineTrack[] InnerTracks()
        {
            return innerLayerIds.Select(layerId =>
                    maskScene.Timeline.FindTrackByTargetId(layerId)
                    ?? throw new InvalidOperationException("Scene mask timeline regression lost an inner mask track."))
                .ToArray();
        }

        static TimelineKeyframe[] ShiftedKeyframes(
            IEnumerable<TimelineKeyframe> keyframes,
            int insertionPoint,
            int delta)
        {
            return keyframes
                .Select(keyframe => keyframe.Frame > insertionPoint
                    ? keyframe with { Frame = keyframe.Frame + delta }
                    : keyframe)
                .ToArray();
        }

        AssertTimeline(
            maskScene.LayerCount == 2
            && secondInnerLayer == 1
            && InnerTracks().Length == 2,
            "Scene mask timeline regression did not establish two independent inner layers.");

        project.TryInsertSceneMaskTimelineBlankKeyframe(scene.Id, maskLayerId, 0);
        maskScene.EditFrame = 0;
        var firstMaskObject = maskScene.AddObject(
            0,
            new PointF(-20, 0),
            new SizeF(24, 24),
            0,
            0,
            Color.White,
            4,
            ShapeKind.Rectangle);
        var secondMaskObject = maskScene.AddObject(
            secondInnerLayer,
            new PointF(20, 0),
            new SizeF(24, 24),
            0,
            0,
            Color.White,
            4,
            ShapeKind.Ellipse);
        AssertTimeline(
            firstMaskObject >= 0
            && secondMaskObject >= 0
            && project.TrySynchronizeSceneMaskTimelineContent(scene.Id, maskLayerId, 0),
            "Drawing into a scene mask did not synchronize its outer populated exposure.");
        AssertTimeline(
            project.TryInsertSceneMaskTimelineKeyframe(scene.Id, maskLayerId, populatedFrame)
            && project.TryInsertSceneMaskTimelineBlankKeyframe(scene.Id, maskLayerId, blankFrame),
            "Scene mask keyframe setup could not create populated and blank cross-frame exposures.");

        var populatedExposure = OuterTrack().EvaluateExposure(populatedFrame + 2);
        var blankExposure = OuterTrack().EvaluateExposure(blankFrame);
        AssertTimeline(
            populatedExposure.HasContent
            && populatedExposure.SourceKeyframeFrame == populatedFrame
            && blankExposure.IsKeyframe
            && blankExposure.SourceKind == TimelineKeyframeKind.Blank
            && InnerTracks().All(track => track.EvaluateExposure(populatedFrame + 2).HasContent)
            && InnerTracks().All(track => !track.EvaluateExposure(blankFrame).HasContent)
            && scene.ResolveKeyframeKindForLayerContent(
                maskLayerId,
                TimelineKeyframeKind.Blank,
                populatedFrame + 2) == TimelineKeyframeKind.Populated
            && scene.ResolveKeyframeKindForLayerContent(
                maskLayerId,
                TimelineKeyframeKind.Populated,
                blankFrame) == TimelineKeyframeKind.Blank
            && scene.IsMaskLayerActive(maskLayerId, populatedFrame + 2)
            && !scene.IsMaskLayerActive(maskLayerId, blankFrame),
            "Scene mask populated, blank, or held cross-frame semantics diverged between outer and inner tracks.");

        AssertTimeline(
            maskScene.SetLayerVisible(secondInnerLayer, false)
            && scene.IsMaskLayerActive(maskLayerId, populatedFrame + 2)
            && maskScene.SetLayerVisible(secondInnerLayer, true),
            "Hiding one populated inner mask layer incorrectly blanked another visible mask layer.");
        AssertTimeline(
            scene.SetLayerVisible(maskLayerId, false)
            && !scene.IsMaskLayerActive(maskLayerId, populatedFrame + 2)
            && OuterTrack().EvaluateExposure(populatedFrame + 2).SourceKind == TimelineKeyframeKind.Populated
            && scene.ResolveKeyframeKindForLayerContent(
                maskLayerId,
                TimelineKeyframeKind.Populated,
                populatedFrame + 2) == TimelineKeyframeKind.Populated
            && scene.SetLayerVisible(maskLayerId, true)
            && scene.IsMaskLayerActive(maskLayerId, populatedFrame + 2),
            "Scene mask visibility changed stored keyframe content instead of only disabling mask activity.");

        var outerBefore = OuterTrack();
        var innerBefore = InnerTracks();
        var baselineDuration = outerBefore.Duration;
        var outerTrackId = outerBefore.Id;
        var innerTrackIds = innerBefore.Select(track => track.Id).ToArray();
        var outerKeyframesBefore = outerBefore.Keyframes.ToArray();
        var innerKeyframesBefore = innerBefore.Select(track => track.Keyframes.ToArray()).ToArray();
        var objectKeyframesBefore = maskScene.ObjectKeyframeFrame.Take(maskScene.ObjectCount).ToArray();
        AssertTimeline(
            innerBefore.All(track => track.Duration == baselineDuration),
            "Scene mask tracks did not begin the frame insertion regression at one shared duration.");

        AssertTimeline(
            project.TryInsertSceneMaskTimelineFrame(
                scene.Id,
                maskLayerId,
                insertionFrame,
                insertedFrameCount),
            "Scene mask F5-equivalent frame insertion was rejected.");
        var outerAfterInsert = OuterTrack();
        var innerAfterInsert = InnerTracks();
        var shiftedObjectKeyframes = objectKeyframesBefore
            .Select(frame => frame > insertionFrame ? frame + insertedFrameCount : frame)
            .ToArray();
        AssertTimeline(
            outerAfterInsert.Id == outerTrackId
            && innerAfterInsert.Select(track => track.Id).SequenceEqual(innerTrackIds)
            && outerAfterInsert.Duration == baselineDuration + insertedFrameCount
            && innerAfterInsert.All(track => track.Duration == baselineDuration + insertedFrameCount)
            && scene.FrameCount == baselineDuration + insertedFrameCount
            && maskScene.FrameCount == baselineDuration + insertedFrameCount
            && outerAfterInsert.Keyframes.SequenceEqual(ShiftedKeyframes(
                outerKeyframesBefore,
                insertionFrame,
                insertedFrameCount))
            && innerAfterInsert.Select((track, index) => track.Keyframes.SequenceEqual(ShiftedKeyframes(
                    innerKeyframesBefore[index],
                    insertionFrame,
                    insertedFrameCount)))
                .All(matches => matches)
            && maskScene.ObjectKeyframeFrame.Take(maskScene.ObjectCount).SequenceEqual(shiftedObjectKeyframes),
            "Scene mask F5-equivalent insertion changed an inner duration or keyframe more than once.");

        AssertTimeline(
            project.TryRemoveSceneMaskTimelineFrame(
                scene.Id,
                maskLayerId,
                insertionFrame,
                insertedFrameCount),
            "Scene mask Shift+F5-equivalent frame removal was rejected.");
        var outerAfterRemove = OuterTrack();
        var innerAfterRemove = InnerTracks();
        AssertTimeline(
            outerAfterRemove.Id == outerTrackId
            && innerAfterRemove.Select(track => track.Id).SequenceEqual(innerTrackIds)
            && outerAfterRemove.Duration == baselineDuration
            && innerAfterRemove.All(track => track.Duration == baselineDuration)
            && scene.FrameCount == baselineDuration
            && maskScene.FrameCount == baselineDuration
            && outerAfterRemove.Keyframes.SequenceEqual(outerKeyframesBefore)
            && innerAfterRemove.Select((track, index) => track.Keyframes.SequenceEqual(innerKeyframesBefore[index]))
                .All(matches => matches)
            && maskScene.ObjectKeyframeFrame.Take(maskScene.ObjectCount).SequenceEqual(objectKeyframesBefore)
            && scene.ResolveKeyframeKindForLayerContent(
                maskLayerId,
                TimelineKeyframeKind.Blank,
                populatedFrame + 2) == TimelineKeyframeKind.Populated
            && scene.ResolveKeyframeKindForLayerContent(
                maskLayerId,
                TimelineKeyframeKind.Populated,
                blankFrame) == TimelineKeyframeKind.Blank,
            "Scene mask Shift+F5-equivalent removal did not exactly restore both inner timelines.");

        var moveOutProject = VectorProject.CreateEmpty();
        var moveOutScene = moveOutProject.Scenes[0];
        var moveOutContent = moveOutScene.Layers[0];
        AssertTimeline(
            moveOutProject.TryAddSceneLayer(moveOutScene.Id, out var moveOutSibling)
            && moveOutSibling is not null
            && moveOutProject.TryAddSceneMaskLayer(
                moveOutScene.Id,
                moveOutContent.Id,
                out var moveOutMask,
                "Move-out mask")
            && moveOutMask is not null
            && moveOutProject.TryMoveSceneLayer(moveOutScene.Id, moveOutContent.Id, 2)
            && moveOutProject.TryMoveSceneLayerOutOfMask(moveOutScene.Id, moveOutContent.Id)
            && string.IsNullOrWhiteSpace(moveOutContent.MaskLayerId)
            && moveOutScene.Layers.Select(layer => layer.Id).SequenceEqual(
                [moveOutMask.Id, moveOutContent.Id, moveOutSibling.Id]),
            "Moving a scene layer out of its mask did not detach it or restore adjacent layer order.");
    }

    private static void RunSceneInstanceTimelineRegression()
    {
        var project = new VectorProject();
        var scene = project.Scenes[0];
        var firstDrawingObject = project.DrawingObjects[0];
        var secondDrawingObject = project.AddDrawingObject("Second source");
        var thirdDrawingObject = project.AddDrawingObject("Third source");

        var firstLayer = scene.Layers[0];
        var timeline = scene.Timeline;
        var firstTrack = timeline.FindTrackByTargetId(firstLayer.Id)
            ?? throw new InvalidOperationException("Scene timeline did not create a track for its first layer.");
        TimelineKeyframeKind ResolveSceneKeyframeKind(int frame)
        {
            var requestedKind = firstTrack.EvaluateExposure(frame).SourceKind
                ?? TimelineKeyframeKind.Blank;
            return scene.ResolveKeyframeKindForLayerContent(firstTrack.TargetId, requestedKind, frame);
        }

        bool InsertResolvedSceneKeyframe(int frame)
        {
            return ResolveSceneKeyframeKind(frame) == TimelineKeyframeKind.Populated
                ? timeline.InsertKeyframe(firstTrack.Id, frame)
                : timeline.InsertBlankKeyframe(firstTrack.Id, frame);
        }

        AssertTimeline(
            firstTrack.Keyframes.Count == 1
            && firstTrack.Keyframes[0] == new TimelineKeyframe(0, TimelineKeyframeKind.Blank)
            && Enumerable.Range(0, 6).All(frame => !firstTrack.EvaluateExposure(frame).HasContent),
            "A new empty scene layer did not start with a blank held exposure.");

        var emptySceneKeyframeChanges = Enumerable.Range(0, 6).Count(InsertResolvedSceneKeyframe);
        AssertTimeline(
            emptySceneKeyframeChanges == 5
            && Enumerable.Range(0, 6).All(frame => firstTrack.Keyframes.Any(
                keyframe => keyframe.Frame == frame && keyframe.Kind == TimelineKeyframeKind.Blank)),
            "Resolved scene keyframe insertion rendered an empty layer as populated keyframes.");
        foreach (var frame in Enumerable.Range(1, 5)) timeline.ClearKeyframe(firstTrack.Id, frame);

        timeline.InsertKeyframe(firstTrack.Id, 6);
        AssertTimeline(
            TimelineStrip.ResolveDisplayedKeyframeKind(
                scene,
                firstLayer.Id,
                firstTrack.EvaluateExposure(6).SourceKind!.Value) == TimelineKeyframeKind.Blank,
            "An empty scene layer used the populated keyframe appearance before timeline normalization.");
        scene.SynchronizeTimelineTracks();
        AssertTimeline(
            firstTrack.Keyframes.Any(item => item.Frame == 6 && item.Kind == TimelineKeyframeKind.Blank)
            && !firstTrack.EvaluateExposure(6).HasContent,
            "Timeline synchronization retained a populated keyframe on an empty scene layer.");

        var addedFirst = project.TryAddSceneInstance(scene.Id, firstDrawingObject.Id, PointF.Empty, 0, out var first);
        AssertTimeline(
            addedFirst
            && first is not null
            && firstTrack.EvaluateExposure(0).SourceKind == TimelineKeyframeKind.Populated
            && TimelineStrip.ResolveDisplayedKeyframeKind(
                scene,
                firstLayer.Id,
                TimelineKeyframeKind.Populated) == TimelineKeyframeKind.Populated
            && TimelineStrip.ResolveDisplayedKeyframeKind(
                scene,
                firstLayer.Id,
                TimelineKeyframeKind.Blank) == TimelineKeyframeKind.Blank,
            "Adding the first scene instance did not populate its layer's frame-zero keyframe.");

        AssertTimeline(
            InsertResolvedSceneKeyframe(2)
            && firstTrack.Keyframes.Any(item => item.Frame == 2 && item.Kind == TimelineKeyframeKind.Populated),
            "Resolved scene keyframe insertion did not copy a populated held exposure.");

        first!.Visible = false;
        scene.SynchronizeTimelineTracks();
        AssertTimeline(
            firstTrack.EvaluateExposure(0).SourceKind == TimelineKeyframeKind.Populated,
            "Hiding a scene instance incorrectly marked its layer exposure as blank.");
        first.Visible = true;

        AssertTimeline(
            InsertResolvedSceneKeyframe(7)
            && firstTrack.Keyframes.Any(item => item.Frame == 7 && item.Kind == TimelineKeyframeKind.Blank),
            "Resolved scene keyframe insertion populated a blank held exposure.");

        var addedSecond = project.TryAddSceneInstance(scene.Id, secondDrawingObject.Id, PointF.Empty, 1, out var second);
        AssertTimeline(
            addedSecond
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

        AssertTimeline(
            first.SceneLayerId == firstLayer.Id
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
        timeline.InsertKeyframe(firstTrack.Id, 12);
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
            && synchronizedFirst.EvaluateExposure(0).HasContent
            && synchronizedFirst.Keyframes.Any(item => item.Frame == 10 && item.Kind == TimelineKeyframeKind.Blank),
            "Scene timeline synchronization discarded a surviving scene-layer track or blanked it before its last instance was removed.");
        AssertTimeline(
            secondTrack is not null
            && third!.SceneLayerId == secondLayer!.Id
            && secondTrack.EvaluateExposure(0).HasContent,
            "A new scene layer did not expose its grouped drawing-object instance at frame zero.");

        AssertTimeline(
            scene.RemoveInstance(second!)
            && scene.InstancesInLayer(firstLayer.Id).Count == 0
            && synchronizedFirst!.Keyframes.All(item => item.Kind == TimelineKeyframeKind.Blank)
            && !synchronizedFirst.EvaluateExposure(0).HasContent
            && !synchronizedFirst.EvaluateExposure(12).HasContent,
            "Removing the last instance from a scene layer retained populated keyframes.");
    }

}
