using System.Numerics;

namespace VectorAnimationEngine;

/// <summary>
/// Regression coverage for <see cref="DrawingObjectMotionTrackBuilder"/>: the four frame
/// classifications, the adjusted-frame rule, tween-segment detection, world-coordinate sampling,
/// onion-skin range clipping, and the null/empty-input contract.
/// </summary>
internal static partial class Benchmark
{
    /// <summary>
    /// Verifies the engine-side motion track sampler. Owns no UI: it builds a project, an instance,
    /// and a timeline track, then asserts the sampled anchors directly.
    /// </summary>
    internal static void RunMotionTrackRegression()
    {
        RunMotionTrackClassificationRegression();
        RunMotionTrackAdjustmentRegression();
        RunMotionTrackWorldPositionRegression();
        RunMotionTrackRangeAndEmptyRegression();
        RunMotionTrackAvailabilityContractRegression();
        RunMotionTrackHeaderLayoutRegression();
        Console.WriteLine("motion_track_regression=ok");
    }

    /// <summary>
    /// Verifies the timeline header really lays the three left-cluster toggles out side by side.
    /// The header pins Auto Key to the track's left edge and caps the onion-skin group's creep, so
    /// the motion-track toggle needs a slot between them; when it does not get one it is pushed
    /// under the onion group and its label is covered. The failure only shows with the longer
    /// Simplified-Chinese labels, so both label sets are measured at several window widths.
    /// </summary>
    private static void RunMotionTrackHeaderLayoutRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty(1, 12);
        using var host = new Form { ClientSize = new Size(1480, 200) };
        using var strip = new TimelineStrip(scene) { Dock = DockStyle.Fill };
        host.Controls.Add(strip);
        host.Show();
        try
        {
            var autoKeyframe = (Control)RequireField(typeof(TimelineStrip), "_autoKeyframeToggle").GetValue(strip)!;
            var motionTrack = (Control)RequireField(typeof(TimelineStrip), "_motionTrackToggle").GetValue(strip)!;
            var onionSkin = (Control)RequireField(typeof(TimelineStrip), "_onionSkinToggle").GetValue(strip)!;
            var refresh = RequireMethod(
                typeof(TimelineStrip),
                "RefreshMotionTrackControls",
                Type.EmptyTypes,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);

            var labelSets = new (string Tag, string AutoKeyframe, string MotionTrack, string OnionSkin)[]
            {
                ("en", "Auto Key", "Motion", "Onion"),
                ("zh", "自动关键帧", "运动轨", "洋葱皮")
            };

            foreach (var labels in labelSets)
            {
                autoKeyframe.Text = labels.AutoKeyframe;
                motionTrack.Text = labels.MotionTrack;
                onionSkin.Text = labels.OnionSkin;

                foreach (var width in new[] { 1480, 1280, 1120, 1024, 900 })
                {
                    host.ClientSize = new Size(width, 200);
                    refresh.Invoke(strip, null);
                    Application.DoEvents();

                    // The toggle is the feature's only entry point, so it must survive every width.
                    AssertTimeline(
                        motionTrack.Visible && !motionTrack.Bounds.IsEmpty,
                        $"The motion-track toggle vanished at width {width} ({labels.Tag}), leaving the "
                        + "feature without a reachable entry point.");

                    var placed = new (string Name, Control Control)[]
                    {
                        ("Auto Key", autoKeyframe),
                        ("motion track", motionTrack),
                        ("onion skin", onionSkin)
                    }.Where(item => item.Control.Visible).ToArray();

                    for (var first = 0; first < placed.Length; first++)
                    {
                        for (var second = first + 1; second < placed.Length; second++)
                        {
                            AssertTimeline(
                                !placed[first].Control.Bounds.IntersectsWith(placed[second].Control.Bounds),
                                $"The {placed[first].Name} toggle overlapped the {placed[second].Name} "
                                + $"toggle at width {width} ({labels.Tag}): "
                                + $"{placed[first].Control.Bounds} versus {placed[second].Control.Bounds}.");
                        }
                    }
                }
            }
        }
        finally
        {
            host.Close();
        }

        Console.WriteLine("motion_track_header_layout=ok");
    }

    /// <summary>
    /// Guards the workbench entry-point contract without constructing a <see cref="MainForm"/>: the
    /// timeline toggle's *availability* must be a property of the selection, never of the feature
    /// already being enabled. Deriving it from the enabled flag (false by default) made the toggle
    /// permanently unreachable, so the source is asserted directly here.
    /// </summary>
    private static void RunMotionTrackAvailabilityContractRegression()
    {
        var source = File.ReadAllText(FindMotionTrackIntegrationSource());
        AssertTimeline(
            source.Contains("var available = instance is not null;", StringComparison.Ordinal),
            "Motion track availability is no longer derived from the selected instance, so the "
            + "timeline toggle may be unreachable.");

        // The toggle must stay laid out (visible) so the feature is discoverable, and must not be
        // gated on MotionTrackToggleAvailable when computing its bounds.
        var strip = File.ReadAllText(FindTimelineStripSource());
        AssertTimeline(
            strip.Contains("if (!IsOnionSkinControlsAvailable()) return Rectangle.Empty;", StringComparison.Ordinal),
            "The motion-track toggle is no longer laid out independently of its enabled state, so it "
            + "would be hidden and impossible to discover.");

        // An empty rectangle is the only thing that hides the toggle, so the bounds must never be
        // dropped merely because the header is crowded; the control slides left instead.
        AssertTimeline(
            !strip.Contains("if (right - layout.TrackLeft < controlsWidth + ScaleTimelineMetric(60)) return Rectangle.Empty;", StringComparison.Ordinal),
            "The motion-track toggle can be dropped from the timeline when the header is crowded, "
            + "which would hide the feature's only entry point.");

        // Auto Key is pinned to the left edge of the header, so the motion-track toggle's leftward
        // slide must stop short of it. Without that floor both toggles resolve to the same rectangle
        // and Auto Key, laid out last, draws over the motion-track switch.
        AssertTimeline(
            strip.Contains("AutoKeyframeControlsBounds(layout);", StringComparison.Ordinal)
            && strip.Contains("autoKeyframeBounds.Right + ScaleTimelineMetric(8)", StringComparison.Ordinal),
            "The motion-track toggle no longer avoids the Auto Key cluster, so Auto Key can cover "
            + "the motion-track switch.");
    }

    private static string FindMotionTrackIntegrationSource()
    {
        return FindRepositorySource("native", "UI", "MainForm.MotionTrack.cs");
    }

    private static string FindTimelineStripSource()
    {
        return FindRepositorySource("native", "UI", "TimelineStrip.cs");
    }

    /// <summary>
    /// Resolves a source file by walking up from the running assembly to the repository root, so the
    /// contract assertions work regardless of the benchmark's working directory.
    /// </summary>
    private static string FindRepositorySource(params string[] relativeParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. relativeParts]);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Motion track regression could not locate '{Path.Combine(relativeParts)}' from "
            + $"'{AppContext.BaseDirectory}'.");
    }

    /// <summary>Coverage for the four <see cref="MotionTrackFrameKind"/> outcomes.</summary>
    private static void RunMotionTrackClassificationRegression()
    {
        var project = VectorProject.CreateEmpty();
        var symbol = project.DrawingObjects[0];
        symbol.SetAnchor(new PointF(12, -8));
        symbol.Scene.CreateEmpty(1, 12);
        var host = project.AddDrawingObject("Motion track classification host");
        host.Scene.CreateEmpty(1, 12);
        AssertTimeline(
            project.TryAddDrawingObjectInstance(host.Id, symbol.Id, PointF.Empty, out var instance)
            && instance is not null,
            "Motion track classification setup could not create its nested instance.");
        // Adding the instance re-synchronizes the track duration from the scene frame count, so pin
        // the duration before placing keys across the full sampled range.
        var track = EnsureMotionTrackDuration(host, 12);

        // 0 = populated key, 4 = blank key, 8 = populated key. No tween: frame 2 is held.
        // The timeline resolves tracks by track id, so the layer index is mapped through the track.
        AssertTimeline(host.Timeline.InsertBlankKeyframe(track.Id, 4),
            "Motion track classification could not insert its blank keyframe.");
        AssertTimeline(host.Timeline.InsertKeyframe(track.Id, 8),
            "Motion track classification could not insert its trailing keyframe.");

        // Timeline synchronization can replace the track instance, so re-resolve before sampling.
        track = ResolveMotionTrackLayerTrack(host);
        AssertTimeline(track.Duration >= 12,
            $"Motion track classification unexpectedly shrank its track to {track.Duration} frames.");

        var motion = DrawingObjectMotionTrackBuilder.Build(symbol, instance!, track, 4, 4, 4);

        // The onion-skin range is centre +/- 4, i.e. frames 0..8 inclusive.
        AssertTimeline(
            motion.FirstFrame == 0 && motion.LastFrame == 8 && motion.Anchors.Count == 9,
            $"Motion track sampled the wrong onion-skin range: first={motion.FirstFrame}, "
            + $"last={motion.LastFrame}, count={motion.Anchors.Count}.");

        AssertMotionTrackKind(motion, 0, MotionTrackFrameKind.Keyframe);
        AssertMotionTrackKind(motion, 2, MotionTrackFrameKind.HeldFrame);
        AssertMotionTrackKind(motion, 4, MotionTrackFrameKind.BlankKeyframe);
        AssertMotionTrackKind(motion, 6, MotionTrackFrameKind.BlankKeyframe);
        AssertMotionTrackKind(motion, 8, MotionTrackFrameKind.Keyframe);
        AssertMotionTrackKind(motion, 7, MotionTrackFrameKind.BlankKeyframe);

        // Held and blank frames take the muted ink; populated keys do not. Blank frames are the only
        // ones hit-testing rejects.
        AssertTimeline(
            motion.FindAnchor(2) is { IsMutedInk: true, IsSelectable: true }
            && motion.FindAnchor(4) is { IsMutedInk: true, IsSelectable: false }
            && motion.FindAnchor(0) is { IsMutedInk: false, IsSelectable: true }
            && motion.HasSelectableAnchors,
            "Motion track anchors did not expose the expected muted-ink and selectable contract.");

        // A classic tween turns the span interior into tween frames and leaves its endpoints as keys.
        // Both endpoints must be populated keyframes for the span to validate.
        AssertTimeline(host.Timeline.InsertKeyframe(track.Id, 11),
            "Motion track classification could not insert its tween destination keyframe.");
        AssertTimeline(
            host.TryCreateTimelineTween(0, 8, 11, TimelineTweenKind.Classic, out var tweenError),
            $"Motion track classification could not create its classic tween: {tweenError}");
        var tweened = DrawingObjectMotionTrackBuilder.Build(symbol, instance!, track, 9, 3, 3);

        AssertMotionTrackKind(tweened, 8, MotionTrackFrameKind.Keyframe);
        AssertMotionTrackKind(tweened, 9, MotionTrackFrameKind.TweenFrame);
        AssertMotionTrackKind(tweened, 10, MotionTrackFrameKind.TweenFrame);
        AssertMotionTrackKind(tweened, 11, MotionTrackFrameKind.Keyframe);

        // Creating a tween materializes a populated keyframe at every interior frame, so those frames
        // report IsKeyframe == true. The classification must still call them tween frames rather than
        // authored keys, otherwise TweenFrame would be unreachable for instance tweens.
        AssertTimeline(
            track.EvaluateExposure(9).IsKeyframe && track.EvaluateExposure(10).IsKeyframe,
            "The tween span did not materialize interior keyframes; this regression no longer "
            + "exercises the tween-versus-authored-key precedence.");
        AssertTimeline(
            tweened.FindAnchor(8) is { IsOnTweenSegment: false }
            && tweened.FindAnchor(9) is { IsOnTweenSegment: true }
            && tweened.FindAnchor(10) is { IsOnTweenSegment: true }
            && tweened.FindAnchor(11) is { IsOnTweenSegment: false },
            "Motion track tween-segment flags did not exclude the span endpoints.");
    }

    /// <summary>Coverage for the "this frame carries its own placement" rule.</summary>
    private static void RunMotionTrackAdjustmentRegression()
    {
        var project = VectorProject.CreateEmpty();
        var symbol = project.DrawingObjects[0];
        symbol.SetAnchor(new PointF(0, 0));
        symbol.Scene.CreateEmpty(1, 10);
        var host = project.AddDrawingObject("Motion track adjustment host");
        host.Scene.CreateEmpty(1, 10);
        AssertTimeline(
            project.TryAddDrawingObjectInstance(host.Id, symbol.Id, PointF.Empty, out var instance)
            && instance is not null,
            "Motion track adjustment setup could not create its nested instance.");
        var track = EnsureMotionTrackDuration(host, 10);

        // An untouched instance inherits its placement everywhere, including frame 0.
        var inherited = DrawingObjectMotionTrackBuilder.Build(symbol, instance!, track, 3, 3, 3);
        AssertTimeline(
            inherited.Anchors.All(anchor => !anchor.IsAdjusted),
            "Motion track marked inherited frames as adjusted: "
            + string.Join(',', inherited.Anchors.Select(anchor => $"{anchor.Frame}:{anchor.IsAdjusted}")));

        AssertTimeline(instance!.SetPositionAtFrame(5, new PointF(140, -30)),
            "Motion track adjustment could not move the instance at frame 5.");

        var adjusted = DrawingObjectMotionTrackBuilder.Build(symbol, instance, track, 5, 5, 4);
        AssertTimeline(
            adjusted.FindAnchor(5) is { IsAdjusted: true },
            "Motion track did not mark the explicitly keyed frame as adjusted.");
        AssertTimeline(
            adjusted.FindAnchor(0) is { IsAdjusted: false }
            && adjusted.FindAnchor(4) is { IsAdjusted: false }
            && adjusted.FindAnchor(9) is { IsAdjusted: false },
            "Motion track marked frames without their own state key as adjusted.");

        // Frame 0 lives in the base state, so it only counts as adjusted once moved off the origin.
        var originFrameZero = DrawingObjectMotionTrackBuilder.Build(symbol, instance, track, 0, 0, 1);
        AssertTimeline(
            originFrameZero.FindAnchor(0) is { IsAdjusted: false },
            "Motion track treated an origin frame-0 base state as adjusted.");
        AssertTimeline(instance.SetPositionAtFrame(0, new PointF(25, 9)),
            "Motion track adjustment could not move the frame-0 base state.");
        var movedFrameZero = DrawingObjectMotionTrackBuilder.Build(symbol, instance, track, 0, 0, 1);
        AssertTimeline(
            movedFrameZero.FindAnchor(0) is { IsAdjusted: true },
            "Motion track did not treat a moved frame-0 base state as adjusted.");

        // A blank frame never counts as adjusted, even though the instance still holds a state key.
        var blankTrack = project.AddDrawingObject("Motion track blank adjustment host");
        blankTrack.Scene.CreateEmpty(1, 10);
        AssertTimeline(
            project.TryAddDrawingObjectInstance(blankTrack.Id, symbol.Id, PointF.Empty, out var blankInstance)
            && blankInstance is not null,
            "Motion track blank-adjustment setup could not create its nested instance.");
        var blankLayerTrack = EnsureMotionTrackDuration(blankTrack, 10);
        AssertTimeline(
            blankTrack.Timeline.InsertBlankKeyframe(blankLayerTrack.Id, 6),
            "Motion track blank-adjustment setup could not insert its blank keyframe.");
        AssertTimeline(blankInstance!.SetPositionAtFrame(6, new PointF(60, 60)),
            "Motion track blank-adjustment setup could not key the instance.");
        var blankMotion = DrawingObjectMotionTrackBuilder.Build(
            symbol,
            blankInstance,
            ResolveMotionTrackLayerTrack(blankTrack),
            6,
            6,
            3);
        AssertTimeline(
            blankMotion.FindAnchor(6) is { Kind: MotionTrackFrameKind.BlankKeyframe, IsAdjusted: false },
            "Motion track reported a blank frame as adjusted.");
    }

    /// <summary>Coverage for anchor-resolved world coordinates.</summary>
    private static void RunMotionTrackWorldPositionRegression()
    {
        var project = VectorProject.CreateEmpty();
        var symbol = project.DrawingObjects[0];
        // SetAnchor quantizes to whole vector units.
        symbol.SetAnchor(new PointF(30, -20));
        symbol.Scene.CreateEmpty(1, 10);
        var host = project.AddDrawingObject("Motion track world position host");
        host.Scene.CreateEmpty(1, 10);
        AssertTimeline(
            project.TryAddDrawingObjectInstance(
                host.Id,
                symbol.Id,
                new PointF(200, 120),
                out var instance)
            && instance is not null,
            "Motion track world-position setup could not create its nested instance.");
        var track = EnsureMotionTrackDuration(host, 10);

        // The trail traces the free-transform anchor, defaulting to the element centre. These test
        // symbols carry no geometry, so the centre falls back to the registration anchor and the world
        // position of an unmoved instance therefore resolves to its own placement.
        var placed = DrawingObjectMotionTrackBuilder.Build(symbol, instance!, track, 0, 0, 2);
        AssertTimeline(
            placed.FindAnchor(0) is { } frameZero && PointsNear(frameZero.WorldPosition, new PointF(200, 120)),
            $"Motion track anchor did not resolve the instance placement: "
            + $"actual={DescribeMotionTrackAnchor(placed.FindAnchor(0))}.");

        AssertTimeline(instance!.SetPositionAtFrame(3, new PointF(-75, 40)),
            "Motion track world-position setup could not key the instance.");
        var moved = DrawingObjectMotionTrackBuilder.Build(symbol, instance, track, 3, 3, 1);
        AssertTimeline(
            moved.FindAnchor(3) is { } keyed && PointsNear(keyed.WorldPosition, new PointF(-75, 40)),
            $"Motion track anchor did not follow the keyed placement: "
            + $"actual={DescribeMotionTrackAnchor(moved.FindAnchor(3))}.");

        // Frames held from the key repeat its placement; frames before it keep the original one.
        AssertTimeline(
            moved.FindAnchor(4) is { } held && PointsNear(held.WorldPosition, new PointF(-75, 40))
            && moved.FindAnchor(2) is { } earlier && PointsNear(earlier.WorldPosition, new PointF(200, 120)),
            "Motion track anchor did not hold the keyed placement across adjacent frames.");

        // Rotation and scale fold into the planar transform, but the registration-anchor fallback still
        // sits at the instance placement no matter how the symbol is turned or scaled.
        var rotatedSymbol = project.AddDrawingObject("Motion track rotation source");
        rotatedSymbol.SetAnchor(new PointF(0, 0));
        rotatedSymbol.Scene.CreateEmpty(1, 6);
        var rotationHost = project.AddDrawingObject("Motion track rotation host");
        rotationHost.Scene.CreateEmpty(1, 6);
        AssertTimeline(
            project.TryAddDrawingObjectInstance(
                rotationHost.Id,
                rotatedSymbol.Id,
                new PointF(50, 60),
                out var rotationInstance)
            && rotationInstance is not null,
            "Motion track rotation setup could not create its nested instance.");
        rotationInstance!.RotationZ = 90;
        rotationInstance.ScaleX = 2.5f;
        rotationInstance.ScaleY = 0.4f;
        var rotated = DrawingObjectMotionTrackBuilder.Build(
            rotatedSymbol,
            rotationInstance,
            ResolveMotionTrackLayerTrack(rotationHost),
            0,
            0,
            1);
        AssertTimeline(
            rotated.FindAnchor(0) is { } rotatedAnchor
            && PointsNear(rotatedAnchor.WorldPosition, new PointF(50, 60)),
            $"Motion track anchor drifted under rotation and scale: "
            + $"actual={DescribeMotionTrackAnchor(rotated.FindAnchor(0))}.");

        // A symbol with real geometry traces its element centre by default, which is exactly where the
        // free-transform tool parks its anchor when it has not been moved.
        var centreSymbol = project.AddDrawingObject("Motion track centre source");
        centreSymbol.SetAnchor(new PointF(0, 0));
        centreSymbol.Scene.CreateEmpty(1, 4);
        centreSymbol.Scene.AddObject(
            0,
            new PointF(70, -30),
            new SizeF(120, 80),
            0,
            0,
            Color.Teal,
            12,
            ShapeKind.Rectangle);
        var centreHost = project.AddDrawingObject("Motion track centre host");
        centreHost.Scene.CreateEmpty(1, 4);
        AssertTimeline(
            project.TryAddDrawingObjectInstance(
                centreHost.Id,
                centreSymbol.Id,
                new PointF(10, 20),
                out var centreInstance)
            && centreInstance is not null,
            "Motion track centre setup could not create its nested instance.");
        var centred = DrawingObjectMotionTrackBuilder.Build(
            centreSymbol,
            centreInstance!,
            ResolveMotionTrackLayerTrack(centreHost),
            0,
            0,
            1);
        AssertTimeline(
            centred.FindAnchor(0) is { } centredAnchor
            && PointsNear(centredAnchor.WorldPosition, new PointF(80, -10)),
            $"Motion track anchor did not resolve the element centre: "
            + $"actual={DescribeMotionTrackAnchor(centred.FindAnchor(0))}.");

        // The defining behavior: the trail traces the free-transform anchor, so an explicit anchor
        // (the operator's dragged handle, in symbol-local space) offsets every sampled point by that
        // same local offset, independent of where the element centre sits.
        var explicitAnchor = new Vector2(symbol.Anchor.X + 40, symbol.Anchor.Y + 25);
        var anchored = DrawingObjectMotionTrackBuilder.Build(symbol, instance!, track, 0, 0, 1, explicitAnchor);
        AssertTimeline(
            anchored.FindAnchor(0) is { } anchoredFrame
            && PointsNear(anchoredFrame.WorldPosition, new PointF(240, 145)),
            $"Motion track anchor did not follow the explicit free-transform anchor: "
            + $"actual={DescribeMotionTrackAnchor(anchored.FindAnchor(0))}.");
    }

    /// <summary>Coverage for range clipping, non-finite rejection, and empty inputs.</summary>
    private static void RunMotionTrackRangeAndEmptyRegression()
    {
        var project = VectorProject.CreateEmpty();
        var symbol = project.DrawingObjects[0];
        symbol.SetAnchor(new PointF(5, 5));
        symbol.Scene.CreateEmpty(1, 10);
        var host = project.AddDrawingObject("Motion track range host");
        host.Scene.CreateEmpty(1, 10);
        AssertTimeline(
            project.TryAddDrawingObjectInstance(host.Id, symbol.Id, PointF.Empty, out var instance)
            && instance is not null,
            "Motion track range setup could not create its nested instance.");
        // The clipping assertions below name frames 0..9, so the track must span exactly that range.
        var track = EnsureMotionTrackDuration(host, 10);

        // Previous frames running past the start are dropped rather than wrapped to negative frames.
        var leadingEdge = DrawingObjectMotionTrackBuilder.Build(symbol, instance!, track, 1, 8, 2);
        AssertTimeline(
            leadingEdge.FirstFrame == 0 && leadingEdge.LastFrame == 3 && leadingEdge.Anchors.Count == 4,
            $"Motion track did not clip the leading edge: first={leadingEdge.FirstFrame}, "
            + $"last={leadingEdge.LastFrame}, count={leadingEdge.Anchors.Count}.");
        AssertTimeline(
            leadingEdge.Anchors.All(anchor => anchor.Frame is >= 0 and < 10),
            "Motion track emitted a negative or out-of-range leading frame.");

        // Next frames running past the end are dropped symmetrically.
        var trailingEdge = DrawingObjectMotionTrackBuilder.Build(symbol, instance!, track, 8, 2, 8);
        AssertTimeline(
            trailingEdge.FirstFrame == 6 && trailingEdge.LastFrame == 9 && trailingEdge.Anchors.Count == 4,
            $"Motion track did not clip the trailing edge: first={trailingEdge.FirstFrame}, "
            + $"last={trailingEdge.LastFrame}, count={trailingEdge.Anchors.Count}.");
        AssertTimeline(
            trailingEdge.Anchors.All(anchor => anchor.Frame is >= 0 and < 10),
            "Motion track emitted an out-of-range trailing frame.");

        // A zero-width side contributes no anchors on that side.
        var keysOnly = DrawingObjectMotionTrackBuilder.Build(symbol, instance!, track, 4, 0, 0);
        AssertTimeline(
            keysOnly.Anchors.Count == 1 && keysOnly.Anchors[0].Frame == 4,
            $"Motion track with both onion-skin sides disabled emitted "
            + $"{keysOnly.Anchors.Count} anchors.");

        // A range entirely outside the track yields the empty result rather than a partial track.
        var outside = DrawingObjectMotionTrackBuilder.Build(symbol, instance!, track, 40, 2, 2);
        AssertTimeline(
            !outside.HasAnchors && !outside.HasSelectableAnchors,
            "Motion track sampled a range lying entirely beyond the track duration.");

        // Explicit frames are normalized, deduplicated, and clipped to the track.
        var explicitFrames = DrawingObjectMotionTrackBuilder.BuildForFrames(
            symbol,
            instance!,
            track,
            3,
            [7, 2, 2, 99, -4, 3]);
        AssertTimeline(
            explicitFrames.Anchors.Select(anchor => anchor.Frame).SequenceEqual([2, 3, 7]),
            "Motion track explicit frame sampling did not sort, dedupe, and clip its frames: "
            + string.Join(',', explicitFrames.Anchors.Select(anchor => anchor.Frame)));

        // Null and empty inputs return Empty instead of throwing on the per-frame repaint path.
        AssertTimeline(
            ReferenceEquals(DrawingObjectMotionTrackBuilder.Build(null!, instance!, track, 3, 2, 2),
                DrawingObjectMotionTrack.Empty)
            && ReferenceEquals(DrawingObjectMotionTrackBuilder.Build(symbol, null!, track, 3, 2, 2),
                DrawingObjectMotionTrack.Empty)
            && ReferenceEquals(DrawingObjectMotionTrackBuilder.Build(symbol, instance!, null!, 3, 2, 2),
                DrawingObjectMotionTrack.Empty)
            && ReferenceEquals(
                DrawingObjectMotionTrackBuilder.BuildForFrames(symbol, instance!, track, 3, []),
                DrawingObjectMotionTrack.Empty)
            && ReferenceEquals(
                DrawingObjectMotionTrackBuilder.BuildForFrames(symbol, instance!, track, 3, null!),
                DrawingObjectMotionTrack.Empty)
            && !DrawingObjectMotionTrack.Empty.HasAnchors
            && !DrawingObjectMotionTrack.Empty.HasSelectableAnchors,
            "Motion track did not return Empty for missing or empty inputs.");
    }

    private static string FindMotionTrackLayerId(DrawingObjectDefinition drawingObject)
    {
        drawingObject.SynchronizeTimelineTracks();
        return drawingObject.Scene.LayerIds[0];
    }

    private static AnimationTimelineTrack ResolveMotionTrackLayerTrack(DrawingObjectDefinition drawingObject)
    {
        return drawingObject.Timeline.FindTrackByTargetId(FindMotionTrackLayerId(drawingObject))
            ?? throw new InvalidOperationException(
                $"Motion track setup lost the timeline track for '{drawingObject.Name}'.");
    }

    /// <summary>
    /// Ensures the layer track spans at least <paramref name="duration"/> frames. Adding an instance
    /// re-synchronizes the track duration from the scene frame count, and <c>SetTrackDuration</c>
    /// reports "no change" rather than failing, so the check is on the resulting duration.
    /// </summary>
    private static AnimationTimelineTrack EnsureMotionTrackDuration(
        DrawingObjectDefinition drawingObject,
        int duration)
    {
        var track = ResolveMotionTrackLayerTrack(drawingObject);
        if (track.Duration < duration)
        {
            drawingObject.Timeline.SetTrackDuration(track.Id, duration);
            track = ResolveMotionTrackLayerTrack(drawingObject);
        }

        if (track.Duration < duration)
        {
            throw new InvalidOperationException(
                $"Motion track setup could not extend '{drawingObject.Name}' to {duration} frames; "
                + $"duration={track.Duration}.");
        }

        return track;
    }

    private static void AssertMotionTrackKind(
        DrawingObjectMotionTrack motion,
        int frame,
        MotionTrackFrameKind expected)
    {
        var anchor = motion.FindAnchor(frame);
        AssertTimeline(
            anchor is { } value && value.Kind == expected,
            $"Motion track classified frame {frame} as "
            + $"{(anchor is { } present ? present.Kind.ToString() : "<missing>")} instead of {expected} "
            + $"(sampled {motion.FirstFrame}..{motion.LastFrame}, {motion.Anchors.Count} anchors).");
    }

    private static string DescribeMotionTrackAnchor(MotionTrackAnchor? anchor)
    {
        return anchor is { } value
            ? $"({value.WorldPosition.X}, {value.WorldPosition.Y}) kind={value.Kind}"
            : "<missing>";
    }
}
