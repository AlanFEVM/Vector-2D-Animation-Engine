using System.Numerics;

namespace VectorAnimationEngine;

/// <summary>
/// Motion-track workbench integration: the timeline toggle, the per-frame anchor selection model,
/// the Stage pointer session that drags selected frames, and the sampling that feeds the Stage
/// overlay.
/// <para>
/// The track is a view over the *selected symbol instance*'s per-frame placement, not over scene
/// geometry. Dragging an anchor therefore edits that instance's state at the dragged frame, which
/// is the same write path the Stage instance move already uses; no drawing geometry is touched.
/// Selection lives here (frame indices), while rendering and hit geometry live in
/// <c>StageControl.MotionTrack.cs</c>.
/// </para>
/// </summary>
internal sealed partial class MainForm
{
    /// <summary>Frames whose anchors are selected. Ordering is irrelevant; the Stage sorts for ink.</summary>
    private readonly HashSet<int> _motionTrackSelectedFrames = [];

    private bool _motionTrackEnabled;

    /// <summary>
    /// Pointer session for dragging selected anchors. Null while no drag is in flight; the captured
    /// instance state lets a cancel restore exactly what the gesture started from.
    /// </summary>
    private MotionTrackDragSession? _motionTrackDragSession;

    private sealed class MotionTrackDragSession
    {
        public required string InstanceId { get; init; }

        /// <summary>Frame the drag writes to. With Auto Key off this may differ from the grabbed frame.</summary>
        public required int EditFrame { get; init; }

        public required PointF StartPointer { get; init; }

        /// <summary>Instance state at the moment the gesture began, keyed by frame.</summary>
        public required Dictionary<int, InstanceFrameState> StartStates { get; init; }

        public bool Changed { get; set; }
    }

    /// <summary>
    /// The symbol instance the motion track follows. The primary Stage selection wins; when nothing
    /// is selected the first instance in the active context is adopted instead, so the toggle is
    /// reachable without the operator first having to hunt for a nested symbol instance on the
    /// Stage. Returns null only when the active context holds no trackable instance at all.
    /// </summary>
    private DrawingObjectInstanceDefinition? MotionTrackInstance()
    {
        if (SelectedSceneInstance() is { } selected && MotionTrackSymbolFor(selected) is not null)
        {
            return selected;
        }

        return ActiveEditableInstances().FirstOrDefault(instance => MotionTrackSymbolFor(instance) is not null);
    }

    private DrawingObjectDefinition? MotionTrackSymbolFor(DrawingObjectInstanceDefinition instance)
    {
        return _project.DrawingObjects.FirstOrDefault(drawingObject =>
            string.Equals(drawingObject.Id, instance.DrawingObjectId, StringComparison.Ordinal));
    }

    /// <summary>
    /// Timeline track of the motion-tracked instance's layer. The host timeline is synchronized
    /// first, because an instance whose layer has no track yet would otherwise report the feature as
    /// unavailable and the toggle could never be switched on.
    /// </summary>
    private AnimationTimelineTrack? MotionTrackTimelineTrack(DrawingObjectInstanceDefinition instance)
    {
        if (IsSceneCompositionContext())
        {
            if (ActiveScene() is not { } scene) return null;
            scene.SynchronizeTimelineTracks();
            return scene.Timeline.FindTrackByTargetId(instance.SceneLayerId);
        }

        if (ActiveDrawingObject() is not { } drawingObject) return null;
        drawingObject.SynchronizeTimelineTracks();
        return drawingObject.Timeline.FindTrackByTargetId(instance.SceneLayerId);
    }

    /// <summary>
    /// Rebuilds the Stage motion track from the current selection, playhead and onion-skin range.
    /// Safe to call on every onion-skin rebuild; it is a pure resample plus a cheap overlay push.
    /// </summary>
    private void RebuildMotionTrackPreview()
    {
        // Availability drives whether the toggle is *enabled* (it stays visible so it is
        // discoverable). It is a property of the selection, not of _motionTrackEnabled, or the
        // toggle could never be switched on.
        var instance = MotionTrackInstance();
        var available = instance is not null;
        if (!available)
        {
            _motionTrackEnabled = false;
            _motionTrackSelectedFrames.Clear();
        }

        var symbol = instance is null ? null : MotionTrackSymbolFor(instance);
        var track = instance is null ? null : MotionTrackTimelineTrack(instance);

        _timeline.MotionTrackToggleAvailable = available;
        _timeline.MotionTrackEnabled = _motionTrackEnabled;
        _timeline.RefreshMotionTrackControls();

        if (!_motionTrackEnabled || instance is null || symbol is null || track is null)
        {
            if (_stage.MotionTrack is not null)
            {
                _stage.SetMotionTrack(null);
                _stage.SetMotionTrackTransformBoxVisible(false);
            }

            return;
        }

        var (previousFrames, nextFrames) = MotionTrackOnionSkinRange();
        var sampled = DrawingObjectMotionTrackBuilder.Build(
            symbol,
            instance,
            track,
            _frame,
            previousFrames,
            nextFrames);

        // Frames that fell outside the track (or that the pointer range no longer covers) must not
        // stay selected, or a later drag would write to a frame the operator cannot see.
        var sampledFrames = sampled.Anchors.Select(anchor => anchor.Frame).ToHashSet();
        _motionTrackSelectedFrames.RemoveWhere(frame => !sampledFrames.Contains(frame));

        _stage.SetMotionTrack(sampled, _motionTrackSelectedFrames);
        UpdateMotionTrackTransformBox();
    }

    /// <summary>
    /// Onion-skin range the motion track mirrors: the drawing scene's or the scene definition's
    /// range, matching whatever the timeline onion-skin controls currently report.
    /// </summary>
    private (int PreviousFrames, int NextFrames) MotionTrackOnionSkinRange()
    {
        if (TimelineOnionSkinDrawingScene() is { } drawingScene)
        {
            return (drawingScene.OnionSkinPreviousFrames, drawingScene.OnionSkinNextFrames);
        }

        if (_timeline.Context is SceneDefinition sceneDefinition)
        {
            return (sceneDefinition.OnionSkinPreviousFrames, sceneDefinition.OnionSkinNextFrames);
        }

        return (VectorScene.DefaultOnionSkinPreviousFrames, VectorScene.DefaultOnionSkinNextFrames);
    }

    private void ToggleTimelineMotionTrack()
    {
        var instance = MotionTrackInstance();
        if (instance is null) return;

        if (!_motionTrackEnabled
            && !string.Equals(_selectedSceneInstanceId, instance.Id, StringComparison.Ordinal))
        {
            // Adopt the fallback target as the Stage selection so the overlay, the transform box and
            // the instance actually being edited all refer to the same thing.
            SetSceneInstanceSelection(instance, additive: false);
        }

        _motionTrackEnabled = !_motionTrackEnabled;
        if (!_motionTrackEnabled)
        {
            _motionTrackSelectedFrames.Clear();
            _motionTrackDragSession = null;
            _stage.SetMotionTrack(null);
            _stage.SetMotionTrackTransformBoxVisible(false);
        }

        RebuildMotionTrackPreview();
        _timeline.RefreshMotionTrackControls();
        _stage.Invalidate();
    }

    /// <summary>
    /// Keeps the Workbench toggle in sync after workspace, frame, selection or onion-skin changes.
    /// Cheap enough to call from existing refresh paths because it resamples only when enabled.
    /// </summary>
    private void RefreshMotionTrackToggle()
    {
        var available = MotionTrackInstance() is not null;
        if (!available && _motionTrackEnabled)
        {
            // The tracked instance disappeared (deleted, context switch, deselected): drop the
            // overlay rather than leaving a stale track pointing at frames nobody owns.
            _motionTrackEnabled = false;
            _motionTrackSelectedFrames.Clear();
            _motionTrackDragSession = null;
            _stage.SetMotionTrack(null);
            _stage.SetMotionTrackTransformBoxVisible(false);
        }

        _timeline.MotionTrackToggleAvailable = available;
        _timeline.MotionTrackEnabled = _motionTrackEnabled;
        _timeline.RefreshMotionTrackControls();
    }

    // ---- Pointer session ---------------------------------------------------------------------

    /// <summary>
    /// Entry point from Stage pointer-down. Claims the press when it lands on a motion-track anchor:
    /// a press on an already-selected frame starts a drag, otherwise the press selects. Ctrl toggles
    /// a frame, Shift extends, and a press on empty Stage space that misses every anchor falls
    /// through so ordinary scene selection still works.
    /// </summary>
    private bool BeginMotionTrackPointer(MouseEventArgs e)
    {
        if (!_motionTrackEnabled || !_stage.MotionTrackVisible) return false;
        if (e.Button != MouseButtons.Left) return false;
        // Only the selection and Free Transform tools interact with anchors; drawing tools must keep
        // receiving every press that lands on the Stage.
        if (_tool is not (ToolMode.Select or ToolMode.Transform)) return false;
        // Free Transform owns its handles: a press on one must start a transform gesture, not a
        // frame drag, even when the handle sits within the anchor hit radius.
        if (_tool == ToolMode.Transform
            && _stage.HitTestTransformHandle(e.Location) != TransformHandleKind.None)
        {
            return false;
        }

        var frame = _stage.HitTestMotionTrackAnchor(e.Location);
        if (frame < 0) return false;

        var additive = IsShiftPressed();
        var toggle = IsControlPressed();
        if (toggle || additive)
        {
            SelectMotionTrackAnchor(frame, additive, toggle);
            return true;
        }

        if (!_motionTrackSelectedFrames.Contains(frame))
        {
            SelectMotionTrackAnchor(frame, additive: false, toggle: false);
            return true;
        }

        // Pressing inside the current selection arms a drag; the playhead follows the frame being
        // edited so the onion skin keeps the edited frame at its centre.
        if (frame != _frame) SetFrame(frame);
        if (!TryBeginMotionTrackDrag(e.Location)) return true;
        _stage.Capture = true;
        _motionTrackMarqueeActive = false;
        return true;
    }

    /// <summary>
    /// Pointer-move routing for the motion track, called before ordinary Stage hover so an anchor
    /// under the cursor reports its frame instead of the underlying object.
    /// </summary>
    private bool MotionTrackPointerMove(MouseEventArgs e)
    {
        if (!_motionTrackEnabled || !_stage.MotionTrackVisible) return false;
        if (_motionTrackDragSession is not null)
        {
            if (e.Button == MouseButtons.Left) UpdateMotionTrackDrag(e.Location);
            return true;
        }

        UpdateMotionTrackHover(e.Location);
        return _stage.MotionTrackHoverFrame >= 0;
    }

    /// <summary>
    /// Pointer-up routing. Completes a drag, or finishes a Ctrl+A / box selection when one is in
    /// flight. Returns true when the motion track consumed the gesture.
    /// </summary>
    private bool MotionTrackPointerUp(MouseEventArgs e)
    {
        if (_motionTrackDragSession is not null)
        {
            if (e.Button == MouseButtons.Left) CompleteMotionTrackDrag();
            else CancelMotionTrackDrag();
            _stage.Capture = false;
            return true;
        }

        if (!_motionTrackMarqueeActive) return false;
        _motionTrackMarqueeActive = false;
        _stage.ClearMarquee();
        ApplyMotionTrackMarqueeSelection(
            new RectangleF(
                Math.Min(_motionTrackMarqueeStart.X, e.X),
                Math.Min(_motionTrackMarqueeStart.Y, e.Y),
                Math.Abs(e.X - _motionTrackMarqueeStart.X),
                Math.Abs(e.Y - _motionTrackMarqueeStart.Y)),
            additive: IsShiftPressed());
        _stage.Capture = false;
        _lastMouse = null;
        return true;
    }

    /// <summary>
    /// Ctrl+Shift+drag on blank Stage space box-selects anchors. Kept separate from scene marquee
    /// selection so the two never write to the same selection state.
    /// </summary>
    private bool TryBeginMotionTrackMarquee(Point location)
    {
        if (!_motionTrackEnabled || !_stage.MotionTrackVisible) return false;
        // Box selection belongs to the selection tool; drawing tools keep their own marquee rules.
        if (_tool is not ToolMode.Select) return false;
        if (!IsControlPressed() || !IsShiftPressed()) return false;
        _motionTrackMarqueeActive = true;
        _motionTrackMarqueeStart = location;
        _stage.Capture = true;
        _stage.SetMarquee(location, location);
        return true;
    }

    private void UpdateMotionTrackMarquee(Point location)
    {
        if (!_motionTrackMarqueeActive) return;
        _stage.SetMarquee(_motionTrackMarqueeStart, location);
    }

    /// <summary>Clears the drag session when a frame change, tool switch or workspace switch occurs.</summary>
    private void AbortMotionTrackPointerSession()
    {
        _motionTrackMarqueeActive = false;
        if (_motionTrackDragSession is not null) CancelMotionTrackDrag();
        _stage.ClearMarquee();
        if (_stage.MotionTrackHoverFrame != -1) _stage.SetMotionTrackHover(-1);
    }

    private bool _motionTrackMarqueeActive;
    private Point _motionTrackMarqueeStart;

    // ---- Selection ---------------------------------------------------------------------------

    /// <summary>
    /// Applies a click on an anchor: plain click replaces the selection, Ctrl toggles one frame,
    /// Shift extends. Always moves the playhead to the clicked frame so the onion skin re-centres
    /// and the operator edits the frame they just grabbed.
    /// </summary>
    private void SelectMotionTrackAnchor(int frame, bool additive, bool toggle)
    {
        if (_stage.MotionTrack?.FindAnchor(frame) is not { IsSelectable: true }) return;
        if (toggle)
        {
            if (!_motionTrackSelectedFrames.Remove(frame)) _motionTrackSelectedFrames.Add(frame);
        }
        else if (additive)
        {
            _motionTrackSelectedFrames.Add(frame);
        }
        else
        {
            _motionTrackSelectedFrames.Clear();
            _motionTrackSelectedFrames.Add(frame);
        }

        _stage.SetMotionTrackSelection(_motionTrackSelectedFrames);
        UpdateMotionTrackTransformBox();
        if (frame != _frame) SetFrame(frame);
        _stage.Invalidate();
    }

    private void ClearMotionTrackSelection()
    {
        if (_motionTrackSelectedFrames.Count == 0) return;
        _motionTrackSelectedFrames.Clear();
        _stage.SetMotionTrackSelection(_motionTrackSelectedFrames);
        UpdateMotionTrackTransformBox();
        _stage.Invalidate();
    }

    /// <summary>Selects every anchor the current track exposes.</summary>
    private void SelectAllMotionTrackAnchors()
    {
        if (_stage.MotionTrack is not { } track) return;
        _motionTrackSelectedFrames.Clear();
        foreach (var anchor in track.Anchors.Where(anchor => anchor.IsSelectable))
        {
            _motionTrackSelectedFrames.Add(anchor.Frame);
        }

        _stage.SetMotionTrackSelection(_motionTrackSelectedFrames);
        UpdateMotionTrackTransformBox();
        _stage.Invalidate();
    }

    private void ApplyMotionTrackMarqueeSelection(RectangleF screenBounds, bool additive)
    {
        var frames = _stage.HitTestMotionTrackAnchorsInScreenRectangle(screenBounds);
        if (!additive) _motionTrackSelectedFrames.Clear();
        foreach (var frame in frames) _motionTrackSelectedFrames.Add(frame);
        _stage.SetMotionTrackSelection(_motionTrackSelectedFrames);
        UpdateMotionTrackTransformBox();
        _stage.Invalidate();
    }

    /// <summary>
    /// Shows the transform box only when the operator asked for it and the selection is small
    /// enough for corner handles to stay meaningful (the anchor contract caps this at 3 frames).
    /// </summary>
    private void UpdateMotionTrackTransformBox()
    {
        var show = _motionTrackEnabled
            && _motionTrackSelectedFrames.Count is > 0 and <= 3
            && _stage.MotionTrack is { HasSelectableAnchors: true };
        _stage.SetMotionTrackTransformBoxVisible(show);
        // The Free Transform handles live in the ordinary transform overlay, so a selection change
        // has to republish it or the handles would keep framing the previous selection.
        if (_tool == ToolMode.Transform) UpdateTransformOverlay();
    }

    /// <summary>
    /// Publishes the anchor selection bounds as the real transform overlay frame so Free Transform's
    /// handles, hit-testing, cursors and pivot all agree with the box drawn around the anchors.
    /// Returns false when the motion track is not driving the transform box, letting the ordinary
    /// instance/geometry overlay logic run unchanged.
    /// </summary>
    private bool TryPublishMotionTrackTransformOverlay(Matrix4x4? referenceTransform)
    {
        if (!_motionTrackEnabled || _tool != ToolMode.Transform) return false;
        if (!_motionTrackSelectedFrames.Any(frame =>
                _stage.MotionTrack?.FindAnchor(frame) is { IsSelectable: true }))
        {
            // Nothing selected on the track: leave the ordinary instance transform box alone so the
            // operator can still transform the instance while the track is visible.
            return false;
        }

        // The anchor contract caps the transform box at three frames. Beyond that, suppress the box
        // entirely rather than falling through to the instance box, which would frame a completely
        // different region than the anchors the operator selected.
        if (_motionTrackSelectedFrames.Count > 3)
        {
            _stage.SetDistortOverlay(false, default);
            _stage.SetTransformOverlay(false, RectangleF.Empty);
            return true;
        }

        if (!TryGetMotionTrackTransformFrame(out var frame))
        {
            _stage.SetDistortOverlay(false, default);
            _stage.SetTransformOverlay(false, RectangleF.Empty);
            return true;
        }

        // A frozen rotation publishes the box captured at pointer-down, matching the instance path:
        // rebuilding it from rotated geometry would let the handle slide under the pointer.
        if (_transformFrameFrozenForRotation)
        {
            var frozenFrame = RotatedFrozenTransformFrame(_drawingTransformAccumulatedAngle);
            _transformCurrentBounds = frozenFrame.Bounds;
            _stage.SetDistortOverlay(false, default);
            _stage.SetTransformOverlay(true, frozenFrame, frozenFrame.Center, referenceTransform);
            return true;
        }

        _transformCurrentBounds = frame.Bounds;
        _stage.SetDistortOverlay(false, default);
        _stage.SetTransformOverlay(true, frame, frame.Center, referenceTransform);
        return true;
    }

    // ---- Free Transform over selected frames -------------------------------------------------

    /// <summary>
    /// The oriented transform frame for the current anchor selection, in world space. The transform
    /// box is axis-aligned around the selected anchors' world positions, which is what the anchor
    /// contract specifies: frames have no orientation of their own, only a placement.
    /// </summary>
    private bool TryGetMotionTrackTransformFrame(out TransformOverlayFrame frame)
    {
        frame = default;
        if (MotionTrackInstance() is not { } instance) return false;
        var points = new List<PointF>();
        foreach (var anchor in _stage.MotionTrack?.Anchors ?? [])
        {
            if (_motionTrackSelectedFrames.Contains(anchor.Frame)) points.Add(anchor.WorldPosition);
        }

        if (points.Count == 0 || points.Count > 3) return false;
        var minX = points.Min(point => point.X);
        var minY = points.Min(point => point.Y);
        var maxX = points.Max(point => point.X);
        var maxY = points.Max(point => point.Y);
        // A single frame (or frames sharing a position) has no area; give it a handle-sized box so
        // the Free Transform handles stay grabbable instead of collapsing to a point.
        if (maxX - minX < 0.5f) { minX -= 8f; maxX += 8f; }
        if (maxY - minY < 0.5f) { minY -= 8f; maxY += 8f; }
        frame = TransformOverlayFrame.FromBounds(RectangleF.FromLTRB(minX, minY, maxX, maxY));
        return frame.IsValid;
    }

    /// <summary>
    /// Applies an in-flight Free Transform gesture to the selected frames instead of to the
    /// instance's current frame. Mirrors the existing instance transform maths (rotate about the
    /// box pivot; scale about it while keeping the pivot world-fixed) but writes each selected
    /// frame's own state.
    /// </summary>
    private bool ApplyMotionTrackTransform(PointF world)
    {
        if (!_motionTrackEnabled || _activeTransformHandle == TransformHandleKind.None) return false;
        if (MotionTrackInstance() is not { } instance) return false;
        if (!TryGetMotionTrackTransformFrame(out var frame)) return false;
        if (_motionTrackSelectedFrames.Count == 0) return false;

        var changed = false;
        if (IsRotationHandle(_activeTransformHandle))
        {
            var pivot = _transformFrameFrozenForRotation ? _transformFrozenPivot : _transformPivot;
            var delta = NormalizeAngle(TransformPointerAngle(world, pivot) - _transformLastAngle);
            if (Math.Abs(delta) <= 0.0001f) return true;
            foreach (var frameIndex in _motionTrackSelectedFrames)
            {
                var state = instance.EvaluateState(frameIndex);
                var operationPivot = DrawingObjectInstanceDefinition.RotationPivotScenePosition(state);
                var rotatedPivot = RotatePointAround(
                    new PointF(operationPivot.X, operationPivot.Y),
                    pivot,
                    delta);
                changed |= instance.SetStateAtFrame(frameIndex, state with
                {
                    X = VectorUnits.Quantize(state.X + rotatedPivot.X - operationPivot.X),
                    Y = VectorUnits.Quantize(state.Y + rotatedPivot.Y - operationPivot.Y),
                    RotationZ = NormalizeDegrees(state.RotationZ + delta * 57.29578f)
                });
            }

            _transformLastAngle = TransformPointerAngle(world, pivot);
        }
        else if (_activeTransformHandle == TransformHandleKind.Move)
        {
            var delta2 = new PointF(world.X - _transformLastPointer.X, world.Y - _transformLastPointer.Y);
            if (Math.Abs(delta2.X) <= 0.0001f && Math.Abs(delta2.Y) <= 0.0001f) return true;
            foreach (var frameIndex in _motionTrackSelectedFrames)
            {
                var state = instance.EvaluateState(frameIndex);
                changed |= instance.SetStateAtFrame(frameIndex, state with
                {
                    X = VectorUnits.Quantize(state.X + delta2.X),
                    Y = VectorUnits.Quantize(state.Y + delta2.Y)
                });
            }
        }
        else
        {
            if (!TryGetResizedTransformScaleFactors(
                    _transformCurrentBounds,
                    _transformPivot,
                    _activeTransformHandle,
                    world,
                    out var scaleX,
                    out var scaleY))
            {
                return true;
            }

            (scaleX, scaleY) = ConstrainTransformScaleFactors(
                _activeTransformHandle,
                scaleX,
                scaleY,
                IsShiftPressed());
            if (Math.Abs(scaleX - 1) <= 0.0001f && Math.Abs(scaleY - 1) <= 0.0001f) return true;
            foreach (var frameIndex in _motionTrackSelectedFrames)
            {
                var state = instance.EvaluateState(frameIndex);
                var next = state with
                {
                    ScaleX = ClampScaleMagnitude(state.ScaleX * scaleX, 0.01f, 1000f),
                    ScaleY = ClampScaleMagnitude(state.ScaleY * scaleY, 0.01f, 1000f)
                };
                next = MoveScalePivotWithGroup(state, next, _transformPivot, scaleX, scaleY);
                changed |= instance.SetStateAtFrame(frameIndex, next);
            }
        }

        _transformLastPointer = world;
        if (!changed) return true;
        _sceneInstanceTimelineDirty = true;
        MotionTrackMaterializeInstances(instance);
        UpdateMotionTrackAfterEdit();
        UpdateTransformOverlay();
        return true;
    }

    // ---- Hover -------------------------------------------------------------------------------

    private void UpdateMotionTrackHover(Point location)
    {
        if (!_motionTrackEnabled
            || _stage.MotionTrack is null
            || _tool is not (ToolMode.Select or ToolMode.Transform))
        {
            if (_stage.MotionTrackHoverFrame != -1) _stage.SetMotionTrackHover(-1);
            return;
        }

        var frame = _stage.HitTestMotionTrackAnchor(location);
        if (frame != _stage.MotionTrackHoverFrame) _stage.SetMotionTrackHover(frame);
    }

    // ---- Drag --------------------------------------------------------------------------------

    /// <summary>
    /// Starts dragging the pressed anchor when it belongs to the current selection; otherwise the
    /// press is treated as a selection click. Returns true when the pointer session was claimed.
    /// </summary>
    private bool TryBeginMotionTrackDrag(Point location)
    {
        if (!_motionTrackEnabled || MotionTrackInstance() is not { } instance) return false;
        var frame = _stage.HitTestMotionTrackAnchor(location);
        if (frame < 0) return false;
        if (!_motionTrackSelectedFrames.Contains(frame)) return false;

        var editFrame = ResolveInstanceStateEditFrameForCurrentContext(instance);
        return BeginMotionTrackDragCore(instance, editFrame, location);
    }

    private bool BeginMotionTrackDragCore(
        DrawingObjectInstanceDefinition instance,
        int editFrame,
        Point location)
    {
        // One undo unit for the whole gesture: capturing here (not per move) is what makes a drag
        // that touches several frames revert in a single step.
        CapturePointerUndoSnapshot(allowSharedGeometry: true);
        _motionTrackDragSession = new MotionTrackDragSession
        {
            InstanceId = instance.Id,
            EditFrame = editFrame,
            StartPointer = _stage.ScreenToWorld(location),
            StartStates = _motionTrackSelectedFrames
                .Where(frame => frame >= 0)
                .ToDictionary(frame => frame, instance.EvaluateState),
            Changed = false
        };
        return true;
    }

    /// <summary>
    /// Applies a live drag delta to every selected frame. With Auto Key enabled each selected frame
    /// receives its own key; without it the edit follows the established held-exposure rule and
    /// writes through <see cref="PrepareInstanceStateTimelineEdit"/>.
    /// </summary>
    private void UpdateMotionTrackDrag(Point location)
    {
        if (_motionTrackDragSession is not { } session) return;
        if (MotionTrackInstance() is not { } instance
            || !string.Equals(instance.Id, session.InstanceId, StringComparison.Ordinal))
        {
            CancelMotionTrackDrag();
            return;
        }

        var pointer = _stage.ScreenToWorld(location);
        var dx = pointer.X - session.StartPointer.X;
        var dy = pointer.Y - session.StartPointer.Y;
        if (Math.Abs(dx) <= 0.0001f && Math.Abs(dy) <= 0.0001f) return;

        var autoKeyframe = _timeline.AutoKeyframeEnabled;
        var changed = false;
        // With Auto Key off every selected anchor writes through the one frame that owns the
        // exposure. Its state must be read once, before the loop: re-reading it per iteration would
        // fold this gesture's own delta into the next read and multiply the move by the selection
        // size.
        var sharedTarget = autoKeyframe ? (InstanceFrameState?)null : instance.EvaluateState(session.EditFrame);
        foreach (var (frame, startState) in session.StartStates)
        {
            // Auto Key writes each dragged frame itself; otherwise the edit lands on the frame that
            // actually owns the exposure, matching how the Stage instance move behaves.
            var editFrame = autoKeyframe ? frame : session.EditFrame;
            PrepareInstanceStateTimelineEdit(instance);
            var target = autoKeyframe ? startState : sharedTarget!.Value;
            changed |= instance.SetStateAtFrame(editFrame, target with
            {
                X = VectorUnits.Quantize(target.X + dx),
                Y = VectorUnits.Quantize(target.Y + dy)
            });
        }

        if (!changed) return;
        session.Changed = true;
        _sceneInstanceTimelineDirty = true;
        MotionTrackMaterializeInstances(instance);
        UpdateMotionTrackAfterEdit();
    }

    private void CompleteMotionTrackDrag()
    {
        var session = _motionTrackDragSession;
        _motionTrackDragSession = null;
        if (session is null) return;
        if (session.Changed)
        {
            _sceneInstanceTimelineDirty = true;
            FinishMotionTrackEdit();
            FinalizePointerUndoSnapshot();
            _undoCapturedForPointerEdit = false;
            _pointerUndoSnapshot = null;
            return;
        }

        // Nothing moved: drop the undo unit captured at pointer-down so a plain click does not
        // leave an empty history entry behind.
        if (_undoCapturedForPointerEdit && _undoStack.TryPop(out var undo))
        {
            RestoreCancelledMarqueeHistory(undo);
            RestoreCanvasMutationSnapshot(undo.Snapshot);
        }

        _undoCapturedForPointerEdit = false;
        _pointerUndoSnapshot = null;
        _pointerUndoSnapshotWithSharedGeometry = null;
    }

    private void CancelMotionTrackDrag()
    {
        var session = _motionTrackDragSession;
        _motionTrackDragSession = null;
        if (session is null || !session.Changed) return;
        if (MotionTrackInstance() is { } instance
            && string.Equals(instance.Id, session.InstanceId, StringComparison.Ordinal))
        {
            foreach (var (frame, startState) in session.StartStates) instance.SetStateAtFrame(frame, startState);
        }

        if (_undoCapturedForPointerEdit && _undoStack.TryPop(out var undo))
        {
            RestoreCancelledMarqueeHistory(undo);
            RestoreCanvasMutationSnapshot(undo.Snapshot);
        }

        _undoCapturedForPointerEdit = false;
        _pointerUndoSnapshot = null;
        _pointerUndoSnapshotWithSharedGeometry = null;
        ClearInstanceTimelineEditTracking();
        _sceneInstanceTimelineDirty = false;
        RebuildEditableInstanceComposition();
        RebuildMotionTrackPreview();
        UpdateInspector();
        _stage.Invalidate();
    }

    /// <summary>
    /// Rebuilds the composed scene so the moved instance actually renders, then resamples the track.
    /// The instance composition is shared with the Stage instance move, so this reuses its rebuild.
    /// </summary>
    private void MotionTrackMaterializeInstances(DrawingObjectInstanceDefinition instance)
    {
        if (!PreviewSelectedSceneInstanceStates()) RebuildEditableInstanceComposition();
    }

    /// <summary>Refreshes overlay, tween materializations and the sampled track after an edit.</summary>
    private void UpdateMotionTrackAfterEdit()
    {
        RefreshEditedInstanceTimelineTweens();
        RebuildMotionTrackPreview();
        UpdateMotionTrackTransformBox();
        UpdateInspector();
        _stage.Invalidate();
    }

    /// <summary>
    /// Commits the drag's side effects once: tween spans re-materialize, the timeline refreshes and
    /// the instance composition is rebuilt so the Stage shows the committed result.
    /// </summary>
    private void FinishMotionTrackEdit()
    {
        RefreshEditedInstanceTimelineTweens();
        _sceneInstanceTimelineDirty = false;
        ClearInstanceTimelineEditTracking();
        RebuildEditableInstanceComposition();
        RebuildMotionTrackPreview();
        UpdateMotionTrackTransformBox();
        UpdateInspector();
        _stage.Invalidate();
    }
}