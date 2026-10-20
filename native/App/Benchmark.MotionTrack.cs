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
        RunMotionTrackAnimatedContentRegression();
        RunMotionTrackAnchorGlowRegression();
        RunMotionTrackRangeAndEmptyRegression();
        RunMotionTrackLifecycleResilienceRegression();
        RunMotionTrackAvailabilityContractRegression();
        RunMotionTrackHeaderLayoutRegression();
        RunMotionTrackBasicDrawingToggleRegression();
        RunMotionTrackRapidFrameEditingRegression();
        RunMotionTrackSceneInteractionRegression();
        Console.WriteLine("motion_track_regression=ok");
    }

    private static void RunMotionTrackAnchorGlowRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        using var stage = new StageControl(scene) { ClientSize = new Size(160, 100) };
        using var bitmap = new Bitmap(160, 100);
        using var graphics = Graphics.FromImage(bitmap);
        MotionTrackScreenAnchor[] anchors =
        [
            new(0, new PointF(40, 40), true, false, false, true, false) { IsCurrentFrame = true },
            new(1, new PointF(120, 40), true, false, false, true, false)
        ];
        RequireMethod(typeof(StageControl), "DrawMotionTrackSegments").Invoke(stage, [graphics, anchors]);
        RequireMethod(typeof(StageControl), "DrawMotionTrackAnchors").Invoke(stage, [graphics, anchors]);
        AssertTimeline(bitmap.GetPixel(40, 40).ToArgb() == Theme.Accent.ToArgb()
            && bitmap.GetPixel(120, 40).ToArgb() == Theme.Warning.ToArgb()
            && bitmap.GetPixel(40, 48).A > 0
            && bitmap.GetPixel(120, 48).A == 0
            && bitmap.GetPixel(80, 43).A == 0
            && !anchors.Any(anchor => anchor.IsSelected),
            "Motion-track glow did not stay on the current anchor independently of selection.");
        Console.WriteLine("motion_track_anchor_glow=ok");
    }

    private static void RunMotionTrackSceneInteractionRegression()
    {
        using var form = new MainForm
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-30_000, -30_000),
            Size = new Size(1280, 800)
        };
        ConfigureExperimental3DForRegression(form);
        var project = (VectorProject)RequireField(typeof(MainForm), "_project").GetValue(form)!;
        var stage = (StageControl)RequireField(typeof(MainForm), "_stage").GetValue(form)!;
        var workspace = (WorkspaceTabs)RequireField(typeof(MainForm), "_workspaceTabs").GetValue(form)!;
        var timeline = (TimelineStrip)RequireField(typeof(MainForm), "_timeline").GetValue(form)!;
        var toggle = RequireMethod(typeof(MainForm), "ToggleTimelineMotionTrack");
        var setRange = RequireMethod(typeof(MainForm), "SetMotionTrackRange");
        var rebuild = RequireMethod(typeof(MainForm), "RebuildSceneComposition");
        var select = RequireMethod(typeof(MainForm), "SetSceneInstanceSelection",
            [typeof(DrawingObjectInstanceDefinition), typeof(bool)]);
        var pointerDown = RequireMethod(typeof(MainForm), "StageMouseDown");
        var pointerMove = RequireMethod(typeof(MainForm), "StageMouseMove");
        var pointerUp = RequireMethod(typeof(MainForm), "StageMouseUp");
        var undo = RequireMethod(typeof(MainForm), "UndoLastEdit");
        var cancel = RequireMethod(typeof(MainForm), "CancelMotionTrackDrag");
        var symbol = project.DrawingObjects[0];
        symbol.SetAnchor(new PointF(20, -30));
        symbol.Scene.AddObject(0, new PointF(240, 120), new SizeF(320, 180),
            0, 0, Color.CornflowerBlue, 4, ShapeKind.Rectangle);
        var scene = project.Scenes[0];
        AssertTimeline(project.TryAddSceneInstance(scene.Id, symbol.Id,
            new PointF(900, 250), 0, out var created) && created is not null,
            "Scene motion-track fixture could not add its instance.");
        var instanceId = created!.Id;
        created.SetStateAtFrame(0, created.EvaluateState(0) with
        {
            Z = 600,
            RotationX = 18,
            RotationY = -25
        });
        created.SetStateAtFrame(4, created.EvaluateState(0) with { X = 2200 });
        var track = scene.Timeline.FindTrackByTargetId(created.SceneLayerId)!;
        scene.Timeline.InsertKeyframe(track.Id, 4);
        form.Show();
        Application.DoEvents();
        try
        {
            workspace.SelectedView = WorkspaceView.SceneEditor;
            RequireMethod(typeof(MainForm), "ActivateTool", [typeof(ToolMode)])
                .Invoke(form, [ToolMode.Select]);
            timeline.AutoKeyframeEnabled = true;
            foreach (var dimension in new[] { SceneDimension.TwoD, SceneDimension.ThreeD })
            {
                foreach (var projection in new[] { CameraProjection.Orthographic, CameraProjection.Perspective })
                {
                    scene.Camera.Projection = projection;
                    stage.ConfigureReferenceView(scene, dimension);
                    stage.ResetReferenceCameraView();
                    stage.SetReferenceCameraOrientation(dimension == SceneDimension.ThreeD ? 0.7f : 0,
                        dimension == SceneDimension.ThreeD ? 0.35f : 0);
                    rebuild.Invoke(form, [false, false]);
                    var instance = scene.Instances.Single(item => item.Id == instanceId);
                    select.Invoke(form, [instance, false]);
                    var motionToggle = (SvgToggleButton)RequireField(typeof(TimelineStrip), "_motionTrackToggle").GetValue(timeline)!;
                    AssertTimeline(motionToggle.Enabled, "Scene motion-track icon remained disabled after adding an instance.");
                    RequireMethod(typeof(CheckBox), "OnClick", [typeof(EventArgs)]).Invoke(motionToggle, [EventArgs.Empty]);
                    setRange.Invoke(form, [0, 4]);
                    AssertTimeline(timeline.MotionTrackEnabled && stage.MotionTrackVisible,
                        $"Scene motion track did not become available in {dimension}/{projection}.");
                    var original = instance.EvaluateState(0);
                    var selectedFrames = (HashSet<int>)RequireField(typeof(MainForm), "_motionTrackSelectedFrames").GetValue(form)!;
                    selectedFrames.Clear();
                    selectedFrames.Add(0);
                    var setFrame = RequireMethod(typeof(MainForm), "SetFrame");
                    setFrame.Invoke(form, [4, true, false, false]);
                    var frameAnchors = stage.ResolveMotionTrackScreenAnchors();
                    AssertTimeline(frameAnchors.Single(anchor => anchor.Frame == 4).IsCurrentFrame
                        && !frameAnchors.Single(anchor => anchor.Frame == 4).IsSelected
                        && frameAnchors.Single(anchor => anchor.Frame == 0).IsSelected
                        && selectedFrames.SetEquals([0])
                        && (string)RequireField(typeof(MainForm), "_selectedSceneInstanceId").GetValue(form)! == instanceId,
                        "Moving the motion-track playhead changed the Stage selection.");
                    setFrame.Invoke(form, [0, true, false, false]);
                    var local = new Vector3(240 - symbol.Anchor.X, 120 - symbol.Anchor.Y, 0);
                    var position = Vector3.Transform(local,
                        DrawingObjectInstanceDefinition.CreateSpatialTransform(original));
                    AssertTimeline(stage.TryProjectScenePosition(position, out var expected, out _)
                        && stage.TryGetMotionTrackAnchorScreenPoint(0, out var actual)
                        && Vector2.Distance(new Vector2(actual.X, actual.Y),
                            new Vector2(expected.X, expected.Y)) < 0.01f,
                        $"Scene motion-track anchor ignored the spatial camera in {dimension}/{projection}.");
                    var start = Point.Round(expected);
                    pointerMove.Invoke(form, [stage, Mouse(start, MouseButtons.None)]);
                    var hint = (AnimatedToolTip)RequireField(typeof(MainForm), "_toolTip").GetValue(form)!;
                    AssertTimeline(stage.MotionTrackHoverFrame == 0
                        && hint.AccessibleName == $"{UiLocalization.T("Frame")} 0"
                        && hint.Width < 92 * stage.SpatialGizmoDpiScale
                        && hint.Height < 34 * stage.SpatialGizmoDpiScale
                        && selectedFrames.SetEquals([0]),
                        "Motion-track hovering did not show its frame without changing selection.");
                    selectedFrames.Clear();
                    stage.SetMotionTrackSelection(selectedFrames);
                    var target = new Point(start.X + 30, start.Y - 20);
                    pointerDown.Invoke(form, [stage, Mouse(start, MouseButtons.Left)]);
                    pointerDown.Invoke(form, [stage, Mouse(start, MouseButtons.Left)]);
                    RequireMethod(typeof(MainForm), "StageMouseDoubleClick").Invoke(form,
                        [stage, new MouseEventArgs(MouseButtons.Left, 2, start.X, start.Y, 0)]);
                    AssertTimeline(workspace.SelectedView == WorkspaceView.SceneEditor
                        && ReferenceEquals(timeline.Context, scene)
                        && stage.MotionTrackVisible
                        && RequireField(typeof(MainForm), "_motionTrackDragSession").GetValue(form) is not null,
                        $"Rapid anchor clicks entered symbol editing or cancelled the drag in {dimension}/{projection}.");
                    pointerMove.Invoke(form, [stage, Mouse(target, MouseButtons.Left)]);
                    pointerUp.Invoke(form, [stage, Mouse(target, MouseButtons.Left)]);
                    AssertTimeline(stage.TryGetMotionTrackAnchorScreenPoint(0, out var moved)
                        && Math.Abs(moved.X - expected.X - 30) < 1
                        && Math.Abs(moved.Y - expected.Y + 20) < 1,
                        $"Scene motion-track drag did not follow its pointer in {dimension}/{projection}.");
                    AssertTimeline((bool)undo.Invoke(form, null)!
                        && scene.Instances.Single(item => item.Id == instanceId).EvaluateState(0) == original,
                        $"Scene motion-track drag did not undo in {dimension}/{projection}.");
                    instance = scene.Instances.Single(item => item.Id == instanceId);
                    select.Invoke(form, [instance, false]);
                    pointerDown.Invoke(form, [stage, Mouse(start, MouseButtons.Left)]);
                    pointerDown.Invoke(form, [stage, Mouse(start, MouseButtons.Left)]);
                    pointerMove.Invoke(form, [stage, Mouse(target, MouseButtons.Left)]);
                    cancel.Invoke(form, null);
                    stage.Capture = false;
                    AssertTimeline(scene.Instances.Single(item => item.Id == instanceId).EvaluateState(0) == original,
                        $"Scene motion-track cancellation lost the original placement in {dimension}/{projection}.");
                    toggle.Invoke(form, null);
                }
            }
        }
        finally
        {
            RequireField(typeof(MainForm), "_projectDirty").SetValue(form, false);
            form.Hide();
        }
        Console.WriteLine("motion_track_scene_interaction=ok");

        static MouseEventArgs Mouse(Point point, MouseButtons buttons) =>
            new(buttons, 1, point.X, point.Y, 0);
    }

    private static void RunMotionTrackAnimatedContentRegression()
    {
        var project = VectorProject.CreateEmpty();
        var symbol = project.DrawingObjects[0];
        symbol.Scene.CreateEmpty(1, 12);
        symbol.SetAnchor(new PointF(30, -20));
        symbol.Scene.AddObject(0, new PointF(100, 200), new SizeF(80, 60),
            0, 0, Color.Teal, 4, ShapeKind.Rectangle);
        symbol.Scene.InsertTimelineBlankKeyframe(0, 4);
        symbol.Scene.EditFrame = 4;
        symbol.Scene.AddObject(0, new PointF(800, -500), new SizeF(80, 60),
            0, 0, Color.Teal, 4, ShapeKind.Rectangle);
        symbol.Scene.InsertTimelineBlankKeyframe(0, 8);
        symbol.Scene.EditFrame = 8;
        symbol.Scene.AddObject(0, new PointF(1600, 900), new SizeF(80, 60),
            0, 0, Color.Teal, 4, ShapeKind.Rectangle);
        var host = project.AddDrawingObject("Animated motion track host");
        host.Scene.CreateEmpty(1, 24);
        AssertTimeline(project.TryAddDrawingObjectInstance(host.Id, symbol.Id,
            new PointF(50, 70), out var instance) && instance is not null,
            "Animated motion-track fixture could not create its instance.");
        var track = EnsureMotionTrackDuration(host, 24);
        var sampled = DrawingObjectMotionTrackBuilder.BuildForFrames(
            symbol, instance!, track, 4, [0, 3, 4, 7, 8]);
        AssertPosition(sampled, 0, new PointF(120, 290));
        AssertPosition(sampled, 3, new PointF(120, 290));
        AssertPosition(sampled, 4, new PointF(820, -410));
        AssertPosition(sampled, 7, new PointF(820, -410));
        AssertPosition(sampled, 8, new PointF(1620, 990));
        instance!.PlaybackMode = DrawingObjectPlaybackMode.Loop;
        sampled = DrawingObjectMotionTrackBuilder.BuildForFrames(symbol, instance, track, 12, [12, 16]);
        AssertPosition(sampled, 12, new PointF(120, 290));
        AssertPosition(sampled, 16, new PointF(820, -410));
        instance.PlaybackMode = DrawingObjectPlaybackMode.HoldFrame;
        instance.HoldFrame = 4;
        sampled = DrawingObjectMotionTrackBuilder.BuildForFrames(symbol, instance, track, 0, [0, 8]);
        AssertPosition(sampled, 0, new PointF(820, -410));
        AssertPosition(sampled, 8, new PointF(820, -410));
        instance.PlaybackMode = DrawingObjectPlaybackMode.PlayOnce;
        sampled = DrawingObjectMotionTrackBuilder.BuildForFrames(
            symbol, instance, track, 4, [4, 8], parentFps: 60m);
        AssertPosition(sampled, 4, new PointF(120, 290));
        AssertPosition(sampled, 8, new PointF(820, -410));
        Console.WriteLine("motion_track_animated_content=ok");

        static void AssertPosition(DrawingObjectMotionTrack motion, int frame, PointF expected)
        {
            AssertTimeline(motion.FindAnchor(frame) is { } anchor
                && PointsNear(anchor.WorldPosition, expected)
                && anchor.ScenePosition is { } spatial
                && PointsNear(new PointF(spatial.X, spatial.Y), expected),
                $"Motion track sampled inactive Cels at frame {frame}: {DescribeMotionTrackAnchor(motion.FindAnchor(frame))}.");
        }
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
        foreach (var iconSize in new[] { 22, 28, 44, 66 })
        {
            using var motionBitmap = SvgIcons.CreateBitmap(SvgIconKind.MotionTrack,
                new Size(iconSize, iconSize), Color.White);
            using var onionBitmap = SvgIcons.CreateBitmap(SvgIconKind.OnionSkin,
                new Size(iconSize, iconSize), Color.White);
            var motionPixels = VisiblePixels(motionBitmap);
            var onionPixels = VisiblePixels(onionBitmap);
            AssertTimeline(onionPixels.Left > 0 && onionPixels.Top > 0
                && onionPixels.Right < iconSize && onionPixels.Bottom < iconSize
                && Math.Abs(onionPixels.Left + onionPixels.Right - iconSize) <= 1
                && Math.Abs(onionPixels.Top + onionPixels.Bottom - iconSize) <= 1
                && Math.Abs(motionPixels.Left + motionPixels.Right - iconSize) <= 1
                && Math.Abs(motionPixels.Top + motionPixels.Bottom - iconSize) <= 1,
                $"Reference icons are clipped or misaligned at {iconSize}px.");
        }
        Console.WriteLine("reference_icon_visible_size_alignment=ok");
        var originalLanguage = UiLocalization.CurrentLanguage;
        var scene = new VectorScene();
        scene.CreateEmpty(1, 12);
        scene.SetOnionSkinEnabled(false);
        using var host = new Form { ClientSize = new Size(1480, 200) };
        using var strip = new TimelineStrip(scene) { Dock = DockStyle.Fill };
        host.Controls.Add(strip);
        host.Show();
        try
        {
            var autoKeyframe = (Control)RequireField(typeof(TimelineStrip), "_autoKeyframeToggle").GetValue(strip)!;
            var motionTrack = (SvgToggleButton)RequireField(typeof(TimelineStrip), "_motionTrackToggle").GetValue(strip)!;
            var onionSkin = (SvgToggleButton)RequireField(typeof(TimelineStrip), "_onionSkinToggle").GetValue(strip)!;
            var gutterWidth = RequireField(typeof(TimelineStrip), "_gutterWidth");
            AssertTimeline((int)gutterWidth.GetValue(strip)! == 360,
                "Timeline layer column must default to the wider 360px gutter.");
            var originalAutoKeyframeBounds = autoKeyframe.Bounds;
            RequireMethod(typeof(TimelineStrip), "BeginGutterSplitterDrag", [typeof(Point)])
                .Invoke(strip, [new Point(360, 100)]);
            RequireMethod(typeof(TimelineStrip), "UpdateGutterSplitterDrag", [typeof(int)])
                .Invoke(strip, [420]);
            AssertTimeline((int)gutterWidth.GetValue(strip)! == 420
                && autoKeyframe.Bounds == originalAutoKeyframeBounds,
                "Wider default layer column must remain draggable without moving toolbar controls.");
            RequireMethod(typeof(TimelineStrip), "UpdateGutterSplitterDrag", [typeof(int)])
                .Invoke(strip, [360]);
            RequireMethod(typeof(TimelineStrip), "EndGutterSplitterDrag").Invoke(strip, null);
            Console.WriteLine("timeline_default_gutter_width_and_drag=ok");
            AssertTimeline(
                motionTrack.Icon == SvgIconKind.MotionTrack && onionSkin.Icon == SvgIconKind.OnionSkin
                && motionTrack.Text.Length == 0 && onionSkin.Text.Length == 0
                && motionTrack.IconOnly && onionSkin.IconOnly
                && motionTrack.ShowCheckedBackground && onionSkin.ShowCheckedBackground
                && motionTrack.IconSize == 22 && onionSkin.IconSize == 22
                && motionTrack.AccessibleRole == AccessibleRole.CheckButton
                && onionSkin.AccessibleRole == AccessibleRole.CheckButton,
                "Timeline reference toggles must be icon-only accessible check buttons.");
            var onionClicks = 0;
            var motionClicks = 0;
            strip.OnionSkinToggleRequested += (_, _) =>
            {
                onionClicks++;
                scene.ToggleOnionSkin();
                strip.RefreshOnionSkinControls();
            };
            strip.MotionTrackToggleRequested += (_, _) =>
            {
                motionClicks++;
                strip.MotionTrackEnabled = !strip.MotionTrackEnabled;
                strip.MotionTrackRangeVisible = strip.MotionTrackEnabled;
                strip.RefreshMotionTrackControls();
            };
            strip.MotionTrackToggleAvailable = true;
            strip.RefreshOnionSkinControls();
            strip.RefreshMotionTrackControls();
            AssertTimeline(onionClicks == 0 && motionClicks == 0,
                "Refreshing reference icons emitted toggle requests.");
            foreach (var referenceSource in new[] { motionTrack, onionSkin })
            {
                using var referenceToggle = new SvgToggleButton(referenceSource.Icon, referenceSource.AccessibleName!)
                {
                    IconOnly = true,
                    ShowCheckedBackground = true,
                    IconSize = referenceSource.IconSize,
                    Size = referenceSource.Size
                };
                var originalBounds = referenceToggle.Bounds;
                using var uncheckedBitmap = new Bitmap(referenceToggle.Width, referenceToggle.Height);
                referenceToggle.DrawToBitmap(uncheckedBitmap, new Rectangle(Point.Empty, referenceToggle.Size));
                referenceToggle.Checked = true;
                using var checkedBitmap = new Bitmap(referenceToggle.Width, referenceToggle.Height);
                referenceToggle.DrawToBitmap(checkedBitmap, new Rectangle(Point.Empty, referenceToggle.Size));
                var surfaceBottom = new Point(referenceToggle.Width / 2, referenceToggle.Height - 4);
                var surfaceSide = new Point((referenceToggle.Width - referenceToggle.Height) / 2 + 3, referenceToggle.Height / 2);
                AssertTimeline(uncheckedBitmap.GetPixel(surfaceBottom.X, surfaceBottom.Y).ToArgb() == Theme.Panel.ToArgb()
                    && checkedBitmap.GetPixel(surfaceBottom.X, surfaceBottom.Y).ToArgb() != Theme.Panel.ToArgb()
                    && checkedBitmap.GetPixel(surfaceSide.X, surfaceSide.Y).ToArgb() != Theme.Panel.ToArgb()
                    && checkedBitmap.GetPixel(0, referenceToggle.Height / 2).ToArgb() == Theme.Panel.ToArgb()
                    && checkedBitmap.GetPixel(0, 0).ToArgb() == Theme.Panel.ToArgb()
                    && referenceToggle.Bounds == originalBounds,
                    "Checked reference icons must show an inset square background without an underline or layout shift.");
                referenceToggle.Checked = false;
            }
            Console.WriteLine("reference_icon_checked_square_background=ok");
            Click(onionSkin);
            Click(motionTrack);
            AssertTimeline(onionClicks == 1 && motionClicks == 1
                && scene.OnionSkinEnabled && strip.MotionTrackEnabled
                && onionSkin.Checked == scene.OnionSkinEnabled
                && motionTrack.Checked == strip.MotionTrackEnabled,
                "Reference icon clicks did not mirror the editing state.");
            Click(onionSkin);
            Click(motionTrack);
            AssertTimeline(onionClicks == 2 && motionClicks == 2
                && onionSkin.Checked == scene.OnionSkinEnabled
                && motionTrack.Checked == strip.MotionTrackEnabled,
                "Reference icons failed to toggle back.");
            strip.MotionTrackToggleAvailable = false;
            strip.RefreshMotionTrackControls();
            Click(motionTrack);
            AssertTimeline(motionClicks == 2 && !motionTrack.Enabled,
                "Unavailable motion icon emitted a toggle request.");
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
                UiLocalization.SetLanguage(labels.Tag == "zh" ? UiLanguage.SimplifiedChinese : UiLanguage.English);
                UiLocalization.Watch(strip);
                autoKeyframe.Text = labels.AutoKeyframe;
                motionTrack.AccessibleName = labels.MotionTrack;
                onionSkin.AccessibleName = labels.OnionSkin;

                foreach (var width in new[] { 1480, 1280, 1120, 1024, 960, 950, 900, 880, 861, 840, 765, 700, 600, 480, 420, 360 })
                {
                    host.ClientSize = new Size(width, 200);
                    refresh.Invoke(strip, null);
                    Application.DoEvents();
                    var onionBounds = onionSkin.Bounds;
                    var motionBounds = motionTrack.Bounds;
                    var minimumGap = (int)Math.Round(6 * strip.DeviceDpi / 96f);
                    AssertTimeline((!motionTrack.Visible || !autoKeyframe.Visible
                            || motionTrack.Left >= autoKeyframe.Right + minimumGap)
                        && (!onionSkin.Visible || !motionTrack.Visible
                            || onionSkin.Left >= motionTrack.Right),
                        $"Reference toggles did not leave space beside Auto Key at width {width} ({labels.Tag}).");
                    var onionVisible = onionSkin.Visible;
                    var motionVisible = motionTrack.Visible;
                    var rangeInputs = new[] { "_onionPreviousFrames", "_onionNextFrames" }
                        .Select(name => (Control)RequireField(typeof(TimelineStrip), name).GetValue(strip)!).ToArray();
                    var rangeBounds = rangeInputs.Select(input => input.Bounds).ToArray();
                    var rangeVisible = rangeInputs.Select(input => input.Visible).ToArray();
                    foreach (var enabled in new[] { true, false })
                    {
                        strip.MotionTrackEnabled = enabled;
                        strip.MotionTrackRangeVisible = enabled;
                        refresh.Invoke(strip, null);
                        Application.DoEvents();
                        AssertTimeline(onionSkin.Visible == onionVisible && motionTrack.Visible == motionVisible
                            && onionSkin.Bounds == onionBounds
                            && motionTrack.Bounds == motionBounds,
                            $"Reference toggles moved or disappeared after a range-mode change at width {width} ({labels.Tag}).");
                        AssertTimeline(rangeInputs.Select(input => input.Visible).SequenceEqual(rangeVisible)
                            && rangeInputs.Select(input => input.Bounds).SequenceEqual(rangeBounds),
                            $"Reference range inputs moved after a mode change at width {width} ({labels.Tag}).");
                        foreach (var available in new[] { true, false })
                        {
                            strip.MotionTrackToggleAvailable = available;
                            refresh.Invoke(strip, null);
                            AssertTimeline(onionSkin.Bounds == onionBounds && motionTrack.Bounds == motionBounds,
                                $"Reference toggles moved after availability changed at width {width} ({labels.Tag}).");
                        }
                        foreach (var button in new[] { onionSkin, motionTrack }.Where(button => button.Visible))
                        {
                            foreach (var point in new[]
                            {
                                new Point(button.Left + 1, button.Top + button.Height / 2),
                                new Point(button.Left + button.Width / 2, button.Top + button.Height / 2),
                                new Point(button.Right - 2, button.Top + button.Height / 2)
                            })
                            {
                                AssertTimeline(ReferenceEquals(strip.GetChildAtPoint(point), button),
                                    $"The {button.Icon} icon is blocked at width {width} ({labels.Tag}), range mode {enabled}.");
                            }
                        }
                    }

                    AssertTimeline(
                        autoKeyframe.Right + minimumGap + motionTrack.Width > strip.ClientSize.Width
                            || (motionTrack.Visible && !motionTrack.Bounds.IsEmpty),
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
                    var headerControls = strip.Controls.Cast<Control>()
                        .Where(control => control.Visible && control.Top < autoKeyframe.Bottom).ToArray();
                    for (var first = 0; first < headerControls.Length; first++)
                    {
                        AssertTimeline(headerControls[first].Right <= strip.ClientSize.Width,
                            $"A header control overflowed at width {width} ({labels.Tag}).");
                        for (var second = first + 1; second < headerControls.Length; second++)
                            AssertTimeline(!headerControls[first].Bounds.IntersectsWith(headerControls[second].Bounds),
                                $"Header controls overlapped at width {width} ({labels.Tag}).");
                    }
                }
            }
        }
        finally
        {
            host.Close();
            UiLocalization.SetLanguage(originalLanguage);
        }

        Console.WriteLine("motion_track_header_layout=ok");

        static void Click(SvgToggleButton button)
        {
            if (button.Enabled)
                RequireMethod(typeof(CheckBox), "OnClick", [typeof(EventArgs)]).Invoke(button, [EventArgs.Empty]);
        }

        static Rectangle VisiblePixels(Bitmap bitmap)
        {
            var visible = Rectangle.Empty;
            for (var pixelY = 0; pixelY < bitmap.Height; pixelY++)
                for (var pixelX = 0; pixelX < bitmap.Width; pixelX++)
                    if (bitmap.GetPixel(pixelX, pixelY).A >= 32)
                        visible = visible.IsEmpty ? new Rectangle(pixelX, pixelY, 1, 1)
                            : Rectangle.Union(visible, new Rectangle(pixelX, pixelY, 1, 1));
            return visible;
        }
    }

    private static void RunMotionTrackRapidFrameEditingRegression()
    {
        using var form = new MainForm
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-30_000, -30_000),
            Size = new Size(1280, 800)
        };
        var project = (VectorProject)RequireField(typeof(MainForm), "_project").GetValue(form)!;
        var timeline = (TimelineStrip)RequireField(typeof(MainForm), "_timeline").GetValue(form)!;
        var stage = (StageControl)RequireField(typeof(MainForm), "_stage").GetValue(form)!;
        var workspace = (WorkspaceTabs)RequireField(typeof(MainForm), "_workspaceTabs").GetValue(form)!;
        var container = project.DrawingObjects[0];
        var child = project.AddDrawingObject("Rapid motion target");
        child.Scene.AddObject(0, PointF.Empty, new SizeF(600, 400),
            0, 0, Color.CornflowerBlue, 2, ShapeKind.Rectangle);
        AssertTimeline(project.TryAddDrawingObjectInstance(container.Id, child.Id,
            PointF.Empty, out var instance) && instance is not null,
            "Rapid motion-track fixture could not place its instance.");
        EnsureMotionTrackDuration(container, 12);
        var instanceId = instance!.Id;
        instance.SetStateAtFrame(1, instance.EvaluateState(0) with { X = 150, Y = 40 });
        var down = RequireMethod(typeof(MainForm), "StageMouseDown");
        var move = RequireMethod(typeof(MainForm), "StageMouseMove");
        var up = RequireMethod(typeof(MainForm), "StageMouseUp");
        var doubleClick = RequireMethod(typeof(MainForm), "StageMouseDoubleClick");
        var shortcut = RequireMethod(typeof(MainForm), "HandleTimelineShortcut");
        var selectAnchor = RequireMethod(typeof(MainForm), "SelectMotionTrackAnchor");
        var committedStates = new Dictionary<int, InstanceFrameState>();
        form.Show();
        Application.DoEvents();
        try
        {
            RequireMethod(typeof(MainForm), "ActivateTool", [typeof(ToolMode)]).Invoke(form, [ToolMode.Select]);
            RequireMethod(typeof(MainForm), "RebuildDrawingObjectUnderlay").Invoke(form, null);
            ((PlaybackSettingsPanel)RequireField(typeof(MainForm), "_playbackSettings").GetValue(form)!)
                .SetFrameRange(0, 11, notifyChanged: false);
            RequireMethod(typeof(MainForm), "ToggleTimelineMotionTrack").Invoke(form, null);
            RequireMethod(typeof(MainForm), "SetMotionTrackRange").Invoke(form, [0, 4]);
            timeline.AutoKeyframeEnabled = true;
            for (var frame = 0; frame < 2; frame++)
            {
                selectAnchor.Invoke(form, [frame, false, false]);
                AssertTimeline(stage.TryGetMotionTrackAnchorScreenPoint(frame, out var anchor),
                    $"Rapid motion-track fixture cannot locate frame {frame}.");
                var start = Point.Round(anchor);
                down.Invoke(form, [stage, new MouseEventArgs(MouseButtons.Left, 2, start.X, start.Y, 0)]);
                AssertTimeline(ReferenceEquals(timeline.Context, container)
                    && RequireField(typeof(MainForm), "_motionTrackDragSession").GetValue(form) is not null,
                    $"Rapid anchor fixture did not arm frame {frame} before the double-click.");
                doubleClick.Invoke(form, [stage, new MouseEventArgs(MouseButtons.Left, 2, start.X, start.Y, 0)]);
                AssertTimeline(workspace.SelectedView == WorkspaceView.BasicDrawing
                    && ReferenceEquals(timeline.Context, container)
                    && stage.MotionTrackVisible
                    && RequireField(typeof(MainForm), "_motionTrackDragSession").GetValue(form) is not null,
                    $"Rapid anchor double-click entered the nested symbol or cancelled frame {frame}.");
                var end = new Point(start.X + 30, start.Y + 15);
                var before = container.Instances.Single(item => item.Id == instanceId).EvaluateState(frame);
                move.Invoke(form, [stage, new MouseEventArgs(MouseButtons.Left, 1, end.X, end.Y, 0)]);
                up.Invoke(form, [stage, new MouseEventArgs(MouseButtons.Left, 1, end.X, end.Y, 0)]);
                var committed = container.Instances.Single(item => item.Id == instanceId).EvaluateState(frame);
                committedStates.Add(frame, committed);
                AssertTimeline(committed != before,
                    $"Rapid frame {frame} drag did not record its placement.");
                AssertTimeline((bool)shortcut.Invoke(form, [Keys.OemPeriod])!,
                    "The next-frame shortcut was not handled.");
                AssertTimeline(container.Instances.Single(item => item.Id == instanceId).EvaluateState(frame) == committed
                    && (int)RequireField(typeof(MainForm), "_frame").GetValue(form)! == frame + 1
                    && timeline.MotionTrackEnabled && stage.MotionTrackVisible
                    && timeline.MotionTrackRangeFirst == 0 && timeline.MotionTrackRangeLast == 4,
                    $"The next-frame shortcut reset the committed placement or track after frame {frame}.");
            }
            selectAnchor.Invoke(form, [1, false, false]);
            AssertTimeline(stage.TryGetMotionTrackAnchorScreenPoint(1, out var interruptedAnchor),
                "The interrupted rapid drag lost its anchor.");
            var interruptedStart = Point.Round(interruptedAnchor);
            down.Invoke(form, [stage, new MouseEventArgs(MouseButtons.Left, 1, interruptedStart.X, interruptedStart.Y, 0)]);
            move.Invoke(form, [stage, new MouseEventArgs(MouseButtons.Left, 1, interruptedStart.X + 40, interruptedStart.Y + 20, 0)]);
            AssertTimeline(RequireField(typeof(MainForm), "_motionTrackDragSession").GetValue(form) is not null
                && container.Instances.Single(item => item.Id == instanceId).EvaluateState(1) != committedStates[1],
                "The interruption fixture did not move its active drag.");
            shortcut.Invoke(form, [Keys.OemPeriod]);
            up.Invoke(form, [stage, new MouseEventArgs(MouseButtons.Left, 1, interruptedStart.X + 40, interruptedStart.Y + 20, 0)]);
            AssertTimeline(committedStates.All(pair => container.Instances.Single(item => item.Id == instanceId)
                    .EvaluateState(pair.Key) == pair.Value)
                && RequireField(typeof(MainForm), "_motionTrackDragSession").GetValue(form) is null
                && timeline.MotionTrackEnabled && stage.MotionTrackVisible
                && timeline.MotionTrackRangeFirst == 0 && timeline.MotionTrackRangeLast == 4,
                $"An interrupted drag or late mouse-up reset the previously committed two-frame motion: "
                + $"states={string.Join(";", committedStates.Select(pair => $"{pair.Key}:{pair.Value.Position} -> {container.Instances.Single(item => item.Id == instanceId).EvaluateState(pair.Key).Position}"))}, "
                + $"session={RequireField(typeof(MainForm), "_motionTrackDragSession").GetValue(form)}, "
                + $"enabled={timeline.MotionTrackEnabled}, visible={stage.MotionTrackVisible}, range={timeline.MotionTrackRangeFirst}..{timeline.MotionTrackRangeLast}.");
            var currentFrame = (int)RequireField(typeof(MainForm), "_frame").GetValue(form)!;
            AssertRestartPreservesTrack(visible: true);
            var currentState = container.Instances.Single(item => item.Id == instanceId).EvaluateState(currentFrame);
            var body = Point.Round(stage.WorldToScreen(currentState.X + 200, currentState.Y + 100));
            AssertTimeline(stage.HitTestMotionTrackAnchor(body) < 0,
                "The ordinary symbol double-click fixture overlapped a motion-track anchor.");
            doubleClick.Invoke(form, [stage, new MouseEventArgs(MouseButtons.Left, 2, body.X, body.Y, 0)]);
            AssertTimeline(ReferenceEquals(timeline.Context, child) && timeline.MotionTrackEnabled,
                "Protecting motion-track double-clicks blocked ordinary symbol navigation or reset the track.");
            AssertRestartPreservesTrack(visible: false);
        }
        finally
        {
            RequireField(typeof(MainForm), "_projectDirty").SetValue(form, false);
            form.Hide();
        }
        Console.WriteLine("motion_track_rapid_frame_editing=ok");

        void AssertRestartPreservesTrack(bool visible)
        {
            var state = form.CaptureEditorRestartStateForRecovery();
            AssertTimeline(state.MotionTrack is { } motion && motion.InstanceId == instanceId
                && motion.FirstFrame == 0 && motion.LastFrame == 4
                && motion.SelectedFrames.SequenceEqual([1]),
                "Capturing an editor restart lost the motion-track session.");
            using var restored = MainForm.CreateForRestart(state);
            var restoredTimeline = (TimelineStrip)RequireField(typeof(MainForm), "_timeline").GetValue(restored)!;
            var restoredStage = (StageControl)RequireField(typeof(MainForm), "_stage").GetValue(restored)!;
            var restoreTrack = RequireMethod(typeof(MainForm), "RestoreMotionTrackRestartState");
            restoreTrack.Invoke(restored, [state.MotionTrack! with { FirstFrame = 4, LastFrame = 0 }]);
            restoreTrack.Invoke(restored, [state.MotionTrack! with { LastFrame = int.MaxValue }]);
            restoreTrack.Invoke(restored, [state.MotionTrack! with { InstanceId = "" }]);
            restoreTrack.Invoke(restored, [state.MotionTrack! with { SelectedFrames = [-1, 1, 5, int.MaxValue] }]);
            AssertTimeline(restoredTimeline.MotionTrackEnabled && restoredStage.MotionTrackVisible == visible
                && RequireField(typeof(MainForm), "_motionTrackDragSession").GetValue(restored) is null
                && RequireField(typeof(MainForm), "_motionTrackPinnedInstanceId").GetValue(restored) is string target
                && target == instanceId
                && (int)RequireField(typeof(MainForm), "_motionTrackRangeFirst").GetValue(restored)! == 0
                && (int)RequireField(typeof(MainForm), "_motionTrackRangeLast").GetValue(restored)! == 4
                && ((HashSet<int>)RequireField(typeof(MainForm), "_motionTrackSelectedFrames").GetValue(restored)!).SetEquals([1])
                && committedStates.All(pair => container.Instances.Single(item => item.Id == instanceId)
                    .EvaluateState(pair.Key) == pair.Value),
                "Editor restart reset the visible/dormant motion track or resumed a stale drag.");
            RequireField(typeof(MainForm), "_projectDirty").SetValue(restored, false);
        }
    }

    private static void RunMotionTrackBasicDrawingToggleRegression()
    {
        using var form = new MainForm
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-30_000, -30_000),
            Size = new Size(1280, 800)
        };
        var project = (VectorProject)RequireField(typeof(MainForm), "_project").GetValue(form)!;
        var timeline = (TimelineStrip)RequireField(typeof(MainForm), "_timeline").GetValue(form)!;
        var stage = (StageControl)RequireField(typeof(MainForm), "_stage").GetValue(form)!;
        var toggle = (SvgToggleButton)RequireField(typeof(TimelineStrip), "_motionTrackToggle").GetValue(timeline)!;
        var onion = (SvgToggleButton)RequireField(typeof(TimelineStrip), "_onionSkinToggle").GetValue(timeline)!;
        var rebuild = RequireMethod(typeof(MainForm), "RebuildOnionSkinPreview");
        var click = RequireMethod(typeof(CheckBox), "OnClick", [typeof(EventArgs)]);
        form.Show();
        Application.DoEvents();
        try
        {
            rebuild.Invoke(form, null);
            AssertTimeline(!toggle.Enabled && !toggle.Checked
                && toggle.AccessibleDescription == UiLocalization.T("Place a symbol instance from the Vault in the current workspace to enable motion track."),
                "A workspace without instances did not explain why motion track is disabled.");
            var container = project.DrawingObjects[0];
            var child = project.AddDrawingObject("Motion target");
            child.Scene.AddObject(0, PointF.Empty, new SizeF(100, 80), 0, 0, Color.CornflowerBlue, 2, ShapeKind.Rectangle);
            AssertTimeline(project.TryAddDrawingObjectInstance(container.Id, child.Id, new PointF(100, 100), out _),
                "Basic Drawing motion-track fixture could not add an instance.");
            rebuild.Invoke(form, null);
            AssertTimeline(toggle.Enabled && timeline.MotionTrackToggleAvailable,
                "Motion-track icon remained disabled after adding a Basic Drawing instance.");
            click.Invoke(toggle, [EventArgs.Empty]);
            AssertTimeline(toggle.Checked && timeline.MotionTrackEnabled && stage.MotionTrackVisible && onion.Checked,
                "Clicking the Basic Drawing icon did not enable motion track alongside onion skin.");
            click.Invoke(onion, [EventArgs.Empty]);
            AssertTimeline(!onion.Checked && toggle.Checked && timeline.MotionTrackEnabled,
                "Disabling onion skin also disabled motion track.");
            click.Invoke(toggle, [EventArgs.Empty]);
            AssertTimeline(!toggle.Checked && !timeline.MotionTrackEnabled,
                "Basic Drawing motion-track icon did not turn off.");
        }
        finally
        {
            RequireField(typeof(MainForm), "_projectDirty").SetValue(form, false);
            form.Hide();
        }
        Console.WriteLine("motion_track_basic_drawing_toggle=ok");
    }

    /// <summary>
    /// Guards the motion track's lifecycle. An enabled track must survive every refresh path that runs
    /// during ordinary editing — workspace switches, navigation, deselection, rapid rebuilds and
    /// gestures that are interrupted mid-flight — because those refreshes used to derive availability
    /// from the live selection and silently cleared the anchor selection and the frozen frame window
    /// whenever the target happened to be out of the active context. Only an explicit toggle-off
    /// may discard that state, including while deletion can still be undone.
    /// </summary>
    private static void RunMotionTrackLifecycleResilienceRegression()
    {
        using var form = new MainForm
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-30_000, -30_000),
            Size = new Size(1280, 800)
        };
        var project = (VectorProject)RequireField(typeof(MainForm), "_project").GetValue(form)!;
        var stage = (StageControl)RequireField(typeof(MainForm), "_stage").GetValue(form)!;
        var workspace = (WorkspaceTabs)RequireField(typeof(MainForm), "_workspaceTabs").GetValue(form)!;
        var timeline = (TimelineStrip)RequireField(typeof(MainForm), "_timeline").GetValue(form)!;
        var toggleButton = (SvgToggleButton)RequireField(typeof(TimelineStrip), "_motionTrackToggle")
            .GetValue(timeline)!;
        var selectedFrames = (HashSet<int>)RequireField(typeof(MainForm), "_motionTrackSelectedFrames")
            .GetValue(form)!;
        var enabledField = RequireField(typeof(MainForm), "_motionTrackEnabled");
        var pinnedField = RequireField(typeof(MainForm), "_motionTrackPinnedInstanceId");
        var rangeInstanceField = RequireField(typeof(MainForm), "_motionTrackRangeInstanceId");
        var rangeFirstField = RequireField(typeof(MainForm), "_motionTrackRangeFirst");
        var rangeLastField = RequireField(typeof(MainForm), "_motionTrackRangeLast");
        var dragSessionField = RequireField(typeof(MainForm), "_motionTrackDragSession");
        var marqueeField = RequireField(typeof(MainForm), "_motionTrackMarqueeActive");
        var toggle = RequireMethod(typeof(MainForm), "ToggleTimelineMotionTrack");
        var rebuildOnionSkin = RequireMethod(typeof(MainForm), "RebuildOnionSkinPreview");
        var select = RequireMethod(typeof(MainForm), "SetSceneInstanceSelection",
            [typeof(DrawingObjectInstanceDefinition), typeof(bool)]);
        var setRange = RequireMethod(typeof(MainForm), "SetMotionTrackRange");
        var clearSelection = RequireMethod(typeof(MainForm), "ClearSelection", [typeof(bool)]);
        var activateTool = RequireMethod(typeof(MainForm), "ActivateTool", [typeof(ToolMode)]);
        var abortPointerSession = RequireMethod(typeof(MainForm), "AbortMotionTrackPointerSession");
        var beginMarquee = RequireMethod(typeof(MainForm), "TryBeginMotionTrackMarquee", [typeof(Point)]);
        var beginDrag = RequireMethod(typeof(MainForm), "TryBeginMotionTrackDrag", [typeof(Point)]);
        var click = RequireMethod(typeof(CheckBox), "OnClick", [typeof(EventArgs)]);

        // Two instances, so "the selection no longer resolves" can be told apart from "the track
        // silently switched to whatever other instance the context happens to offer first".
        var symbol = project.DrawingObjects[0];
        symbol.SetAnchor(new PointF(20, -30));
        symbol.Scene.AddObject(0, new PointF(240, 120), new SizeF(320, 180),
            0, 0, Color.CornflowerBlue, 4, ShapeKind.Rectangle);
        var scene = project.Scenes[0];
        AssertTimeline(project.TryAddSceneInstance(scene.Id, symbol.Id, new PointF(900, 250), 0, out var first)
            && first is not null, "Motion-track lifecycle fixture could not add its first instance.");
        AssertTimeline(project.TryAddSceneInstance(scene.Id, symbol.Id, new PointF(1400, 420), 0, out var second)
            && second is not null, "Motion-track lifecycle fixture could not add its second instance.");
        var secondId = second!.Id;
        var secondTrack = scene.Timeline.FindTrackByTargetId(second.SceneLayerId)!;
        second.SetStateAtFrame(0, second.EvaluateState(0) with { Z = 600 });
        scene.Timeline.InsertKeyframe(secondTrack.Id, 4);
        // The frozen window is asserted literally below, so it is derived from the track's real length
        // instead of assuming the instance lands on a scene that is at least five frames long.
        var lastValid = Math.Max(0, secondTrack.Duration - 1);
        var frozenFirst = 0;
        var frozenLast = Math.Min(4, lastValid);

        form.Show();
        Application.DoEvents();
        try
        {
            workspace.SelectedView = WorkspaceView.SceneEditor;
            activateTool.Invoke(form, [ToolMode.Select]);
            rebuildOnionSkin.Invoke(form, null);
            select.Invoke(form, [second, false]);
            toggle.Invoke(form, null);
            AssertTimeline((bool)enabledField.GetValue(form)!
                && string.Equals((string?)pinnedField.GetValue(form), secondId, StringComparison.Ordinal)
                && timeline.MotionTrackEnabled && stage.MotionTrackVisible,
                "Motion-track lifecycle fixture could not switch its track on.");

            setRange.Invoke(form, [frozenFirst, frozenLast]);
            selectedFrames.Clear();
            selectedFrames.Add(0);
            stage.SetMotionTrackSelection(selectedFrames);
            rebuildOnionSkin.Invoke(form, null);
            AssertTrackHeld("the initial range and anchor selection");

            // Ordinary editing refreshes: each one of these used to be able to tear the track down.
            for (var index = 0; index < 6; index++)
            {
                rebuildOnionSkin.Invoke(form, null);
                AssertTrackHeld($"ordinary refresh {index + 1}");
            }

            // Rapid consecutive rebuilds must not accumulate a drift into the reset path either.
            for (var index = 0; index < 24; index++) rebuildOnionSkin.Invoke(form, null);
            AssertTrackHeld("24 consecutive onion-skin rebuilds");

            // An interrupted gesture: a box selection that is interrupted by a tool switch, then an
            // anchor drag that is interrupted the same way, must both leave the track untouched.
            AssertTimeline((bool)beginMarquee.Invoke(form, [new Point(40, 40)])!
                && (bool)marqueeField.GetValue(form)!,
                "Motion-track lifecycle fixture could not arm its box selection.");
            abortPointerSession.Invoke(form, null);
            AssertTimeline(!(bool)marqueeField.GetValue(form)!
                && dragSessionField.GetValue(form) is null,
                "An interrupted box selection left the motion-track pointer session armed.");
            AssertTrackHeld("an interrupted box selection");

            AssertTimeline(stage.TryGetMotionTrackAnchorScreenPoint(0, out var anchorPoint),
                "Motion-track interruption fixture has no visible anchor.");
            var dragPoint = Point.Round(anchorPoint);
            AssertTimeline((bool)beginDrag.Invoke(form, [dragPoint])!,
                "Motion-track drag did not arm its pointer session.");
            var activeSession = dragSessionField.GetValue(form);
            AssertTimeline(!(bool)beginDrag.Invoke(form, [dragPoint])!
                && ReferenceEquals(activeSession, dragSessionField.GetValue(form)),
                "A repeated pointer-down replaced the active motion-track drag.");
            var startState = second.EvaluateState(0);
            RequireMethod(typeof(MainForm), "UpdateMotionTrackDrag").Invoke(form,
                [new Point(dragPoint.X + 25, dragPoint.Y + 15)]);
            AssertTimeline(scene.Instances.Single(item => item.Id == secondId).EvaluateState(0) != startState,
                "The interrupted motion-track drag did not mutate its fixture.");
            abortPointerSession.Invoke(form, null);
            AssertTimeline(dragSessionField.GetValue(form) is null,
                "An interrupted anchor drag left the motion-track pointer session armed.");
            AssertTimeline(scene.Instances.Single(item => item.Id == secondId).EvaluateState(0) == startState,
                "An interrupted anchor drag did not restore its original state.");
            AssertTrackHeld("an interrupted anchor drag");

            // Leaving the context that owns the instance: the track goes dormant (nothing to draw)
            // but keeps every bit of state, and the toggle stays clickable so it can still be turned
            // off. This is the case the bug report was about.
            workspace.SelectedView = WorkspaceView.BasicDrawing;
            Application.DoEvents();
            rebuildOnionSkin.Invoke(form, null);
            AssertTrackHeld("switching to a workspace without a trackable instance");
            AssertTimeline(!stage.MotionTrackVisible
                && toggleButton.Enabled && toggleButton.Checked,
                "A dormant motion track either kept drawing or lost its entry point.");

            // Coming back re-attaches the very same track, selection and frozen window.
            workspace.SelectedView = WorkspaceView.SceneEditor;
            Application.DoEvents();
            rebuildOnionSkin.Invoke(form, null);
            AssertTrackHeld("returning to the owning workspace");
            AssertTimeline(stage.MotionTrackVisible,
                "The motion track did not re-attach after returning to its workspace.");

            // Deselecting the tracked instance must not hand the track to the other instance: doing so
            // would re-seed the frozen window around the playhead and move every anchor.
            clearSelection.Invoke(form, [false]);
            rebuildOnionSkin.Invoke(form, null);
            AssertTrackHeld("clearing the Stage selection");
            AssertTimeline(string.Equals((string?)rangeInstanceField.GetValue(form), secondId,
                    StringComparison.Ordinal),
                "Clearing the Stage selection re-targeted the motion track to another instance.");

            select.Invoke(form, [first, false]);
            rebuildOnionSkin.Invoke(form, null);
            AssertTrackHeld("selecting a different instance");

            var instanceSnapshot = scene.CreateInstanceSnapshot();
            var timelineSnapshot = scene.Timeline.CreateSnapshot();
            AssertTimeline(project.TryRemoveSceneInstance(scene.Id, secondId, out _),
                "Motion-track lifecycle fixture could not remove its tracked instance.");
            rebuildOnionSkin.Invoke(form, null);
            AssertTrackHeld("removing the target before undo");
            AssertTimeline(!stage.MotionTrackVisible && toggleButton.Enabled,
                "A missing target left a visible stale track or an unreachable toggle.");
            setRange.Invoke(form, [int.MinValue, int.MaxValue]);
            AssertTrackHeld("an out-of-context range event");
            scene.RestoreInstanceSnapshot(instanceSnapshot);
            scene.Timeline.RestoreSnapshot(timelineSnapshot);
            scene.SynchronizeTimelineTracks();
            rebuildOnionSkin.Invoke(form, null);
            AssertTrackHeld("restoring the removed target");
            AssertTimeline(stage.MotionTrackVisible,
                "Restoring the target did not restore its motion-track overlay.");

            for (var frame = 0; frame <= frozenLast; frame++)
            {
                RequireMethod(typeof(MainForm), "SetFrame",
                    [typeof(int), typeof(bool), typeof(bool), typeof(bool)])
                    .Invoke(form, [frame, true, true, false]);
                AssertTrackHeld($"advancing playback to frame {frame}");
            }

            RequireMethod(typeof(MainForm), "TogglePlayback").Invoke(form, null);
            AssertTimeline((bool)RequireField(typeof(MainForm), "_playing").GetValue(form)!,
                "Motion-track playback fixture did not start playback.");
            rebuildOnionSkin.Invoke(form, null);
            AssertTrackHeld("starting playback");
            RequireMethod(typeof(MainForm), "StopPlayback").Invoke(form, null);
            AssertTrackHeld("stopping playback");

            // Rapid consecutive toggle clicks: the operator's own reset may only happen every other
            // click, and must land the whole feature in a coherent state each time.
            AssertTimeline(toggleButton.Enabled, "The motion-track toggle became unreachable after a reset.");
            for (var index = 0; index < 4; index++)
            {
                click.Invoke(toggleButton, [EventArgs.Empty]);
                var expected = index % 2 != 0;
                AssertTimeline((bool)enabledField.GetValue(form)! == expected
                    && timeline.MotionTrackEnabled == expected
                    && toggleButton.Checked == expected,
                    $"Rapid motion-track toggling desynchronized at click {index + 1}.");
                if (!expected)
                {
                    AssertTimeline(selectedFrames.Count == 0
                        && (int)rangeFirstField.GetValue(form)! == -1
                        && (int)rangeLastField.GetValue(form)! == -1,
                        $"Switching the motion track off left stale state at click {index + 1}.");
                }
            }
        }
        finally
        {
            RequireField(typeof(MainForm), "_projectDirty").SetValue(form, false);
            form.Hide();
        }

        Console.WriteLine("motion_track_lifecycle_resilience=ok");

        void AssertTrackHeld(string context)
        {
            AssertTimeline((bool)enabledField.GetValue(form)!
                && string.Equals((string?)pinnedField.GetValue(form), secondId, StringComparison.Ordinal)
                && string.Equals((string?)rangeInstanceField.GetValue(form), secondId,
                    StringComparison.Ordinal)
                && (int)rangeFirstField.GetValue(form)! == frozenFirst
                && (int)rangeLastField.GetValue(form)! == frozenLast
                && selectedFrames.SetEquals([0]),
                $"Motion track lost its enabled state, frozen range or anchor selection after {context}.");
        }
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
