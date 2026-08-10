using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

namespace VectorAnimationEngine;

internal sealed partial class MainForm : Form
{
    private VectorScene?[] _sceneMaskTimelineClipboardSources = [];
    private readonly Dictionary<object, long> _undoSequenceByEntry = new(ReferenceEqualityComparer.Instance);
    private long _nextUndoSequence;

    internal static bool BlocksModelCommandDuringPointerInteraction(Keys keyData)
    {
        return keyData is (Keys.Control | Keys.N)
            or (Keys.Control | Keys.O)
            or (Keys.Control | Keys.S)
            or (Keys.Control | Keys.Shift | Keys.S)
            or (Keys.Control | Keys.Z)
            or (Keys.Control | Keys.A)
            or (Keys.Control | Keys.X)
            or (Keys.Control | Keys.C)
            or (Keys.Control | Keys.V)
            or (Keys.Control | Keys.Up)
            or (Keys.Control | Keys.Down)
            or Keys.F8
            or Keys.Delete
            || IsTimelineEditShortcut(keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Space)
        {
            if (!TryHoldTemporaryCanvasPan()) return;
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        if (e.KeyCode is Keys.ControlKey or Keys.LControlKey or Keys.RControlKey)
        {
            if (!RefreshPenModifierPreview() && !RefreshTraditionalPenModifierPreview()) return;
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        if (e.KeyCode is not (Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey)) return;
        if (RefreshActiveLineDrawingPreview())
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        if (!ShowBrushColorPalette()) return;
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.KeyCode == Keys.Space)
        {
            var wasHeld = _spacePanHeld;
            _spacePanKeyDown = false;
            ReleaseTemporaryCanvasPan();
            if (!wasHeld) return;
            e.Handled = true;
            return;
        }
        if (e.KeyCode is Keys.ControlKey or Keys.LControlKey or Keys.RControlKey)
        {
            if (!RefreshPenModifierPreview() && !RefreshTraditionalPenModifierPreview()) return;
            e.Handled = true;
            return;
        }
        if (e.KeyCode is not (Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey)) return;
        if ((ModifierKeys & Keys.Shift) != Keys.None) return;
        if (RefreshActiveLineDrawingPreview())
        {
            e.Handled = true;
            return;
        }
        HideBrushColorPalette();
        e.Handled = true;
    }

    private bool RefreshActiveLineDrawingPreview()
    {
        if (_tool != ToolMode.Line || _startWorld is not { } start || _lastMouse is not { } pointer) return false;
        UpdateDrawingPreview(start, _stage.ScreenToWorld(pointer), ToolMode.Line);
        return true;
    }

    private bool RefreshPenModifierPreview()
    {
        if (_tool != ToolMode.SimplePen
            || _penSegmentDragging
            || IsSceneCompositionContext()
            || !_stage.IsHandleCreated)
        {
            return false;
        }
        var pointer = _stage.PointToClient(Cursor.Position);
        if (!_stage.ClientRectangle.Contains(pointer)) return false;
        UpdatePenHover(pointer);
        return true;
    }

    private bool RefreshTraditionalPenModifierPreview()
    {
        if (_tool != ToolMode.Pen
            || _traditionalPenPointerDown
            || _traditionalPenAnchorEditing
            || IsSceneCompositionContext()
            || !_stage.IsHandleCreated)
        {
            return false;
        }

        var pointer = _stage.PointToClient(Cursor.Position);
        if (!_stage.ClientRectangle.Contains(pointer)) return false;
        UpdateTraditionalPenHover(pointer);
        return true;
    }

    private bool ShowBrushColorPalette()
    {
        if (_brushColorPaletteActive) return true;
        if (!IsBrushTool(_tool)
            || _freehandDrawing
            || _lastMouse is not null
            || IsSceneCompositionContext()
            || ContainsFocusedEditor(this))
        {
            return false;
        }

        var center = _stage.PointToClient(Cursor.Position);
        if (!_stage.ClientRectangle.Contains(center)) return false;
        var colors = _materialEditor.RecentColors.Take(8).ToArray();
        if (colors.Length == 0) colors = [ActiveColor()];

        _brushColorPaletteActive = true;
        _brushColorPaletteColors = colors;
        _brushColorPaletteCenter = center;
        _brushColorPaletteHoverIndex = -1;
        _stage.ClearBrushTipCursor();
        _stage.SetBrushColorPalette(center, colors);
        return true;
    }

    private void UpdateBrushColorPaletteHover(Point screen)
    {
        if (!_brushColorPaletteActive) return;
        var hoveredIndex = _stage.HitTestBrushColorPalette(screen);
        if (hoveredIndex == _brushColorPaletteHoverIndex) return;

        _brushColorPaletteHoverIndex = hoveredIndex;
        _stage.SetBrushColorPalette(_brushColorPaletteCenter, _brushColorPaletteColors, hoveredIndex);
        if ((uint)hoveredIndex >= (uint)_brushColorPaletteColors.Length) return;
        _materialEditor.SetFillColorForBrush(_brushColorPaletteColors[hoveredIndex]);
    }

    private void HideBrushColorPalette()
    {
        if (!_brushColorPaletteActive && !_stage.BrushColorPaletteVisible) return;
        _brushColorPaletteActive = false;
        _brushColorPaletteColors = [];
        _brushColorPaletteHoverIndex = -1;
        _stage.ClearBrushColorPalette();
        if (!IsDisposed) RefreshInteractionCursorAtPointer();
    }

    private static bool IsTimelineEditShortcut(Keys keyData)
    {
        return keyData is Keys.F5
            or (Keys.Shift | Keys.F5)
            or Keys.F6
            or (Keys.Shift | Keys.F6)
            or Keys.F7;
    }

    private bool HandleTimelineShortcut(Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Enter:
                TogglePlayback();
                return true;
            case Keys.Oemcomma:
                StopPlayback();
                SetFrame(_frame - 1);
                return true;
            case Keys.OemPeriod:
                StopPlayback();
                SetFrame(_frame + 1);
                return true;
            case Keys.Shift | Keys.Oemcomma:
                StopPlayback();
                SetFrame(_playbackSettings.StartFrame);
                return true;
            case Keys.Shift | Keys.OemPeriod:
                StopPlayback();
                SetFrame(_playbackSettings.EndFrame);
                return true;
            case Keys.F5:
                return ExecuteTimelineEdit(
                    TimelineEditKind.InsertFrame,
                    movePlayheadToInsertedFrame: true);
            case Keys.Shift | Keys.F5:
                return ExecuteTimelineEdit(TimelineEditKind.RemoveFrame);
            case Keys.F6:
                return ExecuteTimelineEdit(TimelineEditKind.InsertKeyframe);
            case Keys.Shift | Keys.F6:
                return ExecuteTimelineEdit(TimelineEditKind.ClearKeyframe);
            case Keys.F7:
                return ExecuteTimelineEdit(TimelineEditKind.InsertBlankKeyframe);
            default:
                return false;
        }
    }

    private bool ExecuteTimelineEdit(
        TimelineEditKind edit,
        IReadOnlyList<TimelineFrameCell>? selectedCells = null,
        bool movePlayheadToInsertedFrame = false)
    {
        if (!CommitTextEdit()) return false;
        var context = _timeline.Context;
        context.SynchronizeTimelineTracks();
        var previousLastFrame = Math.Max(0, context.FrameCount - 1);
        var previousPlayheadFrame = _frame;
        var previousTimelineSelection = edit is TimelineEditKind.InsertKeyframe or TimelineEditKind.InsertBlankKeyframe
            ? _timeline.CaptureSelectionSnapshot()
            : null;
        var timeline = context.Timeline;
        var cells = (selectedCells ?? _timeline.SelectedFrameCells)
            .Where(cell => timeline.FindTrack(cell.TrackId) is not null)
            .Distinct()
            .ToArray();
        if (selectedCells is null
            && edit is TimelineEditKind.InsertKeyframe or TimelineEditKind.InsertBlankKeyframe
            && cells.Length <= 1
            && !string.IsNullOrWhiteSpace(_timeline.ActiveTrackId))
        {
            cells = [ResolveTimelineKeyframeInsertionCell(
                timeline,
                _timeline.ActiveTrackId,
                _frame,
                cells.FirstOrDefault())];
        }
        if (cells.Length == 0 && !string.IsNullOrWhiteSpace(_timeline.ActiveTrackId))
        {
            cells = [new TimelineFrameCell(_timeline.ActiveTrackId, _frame)];
        }
        if (cells.Length == 0) return false;

        var preservedInstanceSelectionIds = edit == TimelineEditKind.InsertKeyframe
            ? _selectedSceneInstanceIds.ToArray()
            : [];
        var preservedPrimaryInstanceId = edit == TimelineEditKind.InsertKeyframe
            ? _selectedSceneInstanceId
            : "";
        var singleKeyframeInsertion = cells.Length == 1
            && edit is TimelineEditKind.InsertKeyframe or TimelineEditKind.InsertBlankKeyframe;
        var singleKeyframeInsertionBeyondTrackEnd = singleKeyframeInsertion
            && TimelineCellIsBeyondTrackEnd(timeline, cells[0]);
        var advanceSingleKeyframeInsertion = singleKeyframeInsertion
            && TimelineKeyframeInsertionShouldAdvance(timeline, cells[0]);
        int? singleKeyframeDestinationFrame = null;
        if (advanceSingleKeyframeInsertion)
        {
            var advancedCell = AdvanceTimelineKeyframeShortcutCell(cells[0]);
            if (advancedCell == cells[0]) return false;
            cells = [advancedCell];
        }

        var drawingScene = context switch
        {
            VectorScene vectorScene => vectorScene,
            DrawingObjectDefinition drawingObject => drawingObject.Scene,
            _ => null
        };
        var vectorSnapshot = drawingScene?.CreateSnapshot();
        var drawingObjectDefinition = context as DrawingObjectDefinition;
        var drawingObjectInstanceSnapshot = drawingObjectDefinition?.CreateInstanceSnapshot();
        var sceneDefinition = context as SceneDefinition;
        var hasSceneMaskCells = sceneDefinition is not null
            && cells.Any(cell => IsSceneMaskTrack(sceneDefinition, timeline.FindTrack(cell.TrackId)));
        var sceneSnapshot = sceneDefinition is null ? null : timeline.CreateSnapshot();
        var sceneLayerSnapshot = hasSceneMaskCells ? sceneDefinition!.CreateLayerSnapshot() : null;
        var changed = false;
        var refreshCurrentComposition = edit != TimelineEditKind.InsertFrame;

        if (singleKeyframeInsertion)
        {
            changed |= EnsureTimelineCellFramesExistForContext(context, timeline, cells);
            ApplyBoundTimelineDuration(previousLastFrame, refreshClampedFrame: false);
            var wasSyncingFrame = _syncingFrame;
            _syncingFrame = true;
            try
            {
                _timeline.SelectSingleFrame(cells[0].TrackId, cells[0].Frame);
                singleKeyframeDestinationFrame = _timeline.CurrentFrame;
            }
            finally
            {
                _syncingFrame = wasSyncingFrame;
            }
        }

        using (timeline.BeginBatchUpdate())
        {
            foreach (var group in cells.GroupBy(cell => cell.TrackId, StringComparer.Ordinal))
            {
                var track = timeline.FindTrack(group.Key);
                if (track is null) continue;
                var layer = drawingScene is null ? -1 : Array.IndexOf(drawingScene.LayerIds, track.TargetId);
                var ranges = TimelineFrameRanges(group.Select(cell => cell.Frame));
                if (edit is TimelineEditKind.InsertFrame or TimelineEditKind.RemoveFrame)
                {
                    foreach (var range in ranges.Reverse())
                    {
                        var rangeChanged = ApplyTimelineRangeEdit(
                            edit,
                            context,
                            timeline,
                            track,
                            drawingScene,
                            layer,
                            range.Frame,
                            range.Count);
                        changed |= rangeChanged;
                        if (rangeChanged
                            && edit == TimelineEditKind.InsertFrame
                            && TimelineInsertRequiresCompositionRefresh(_frame, range.Frame))
                        {
                            refreshCurrentComposition = true;
                        }
                    }
                    continue;
                }

                if (edit == TimelineEditKind.InsertBlankKeyframe)
                {
                    foreach (var plan in ResolveTimelineBlankKeyframeRangePlans(group.Select(cell => cell.Frame)))
                    {
                        foreach (var frame in plan.ExtensionFrames)
                        {
                            changed |= ApplyTimelineCellEdit(
                                TimelineEditKind.ClearKeyframe,
                                context,
                                timeline,
                                track,
                                drawingScene,
                                layer,
                                frame);
                        }

                        changed |= ApplyTimelineCellEdit(
                            TimelineEditKind.InsertBlankKeyframe,
                            context,
                            timeline,
                            track,
                            drawingScene,
                            layer,
                            plan.BlankFrame);
                    }
                    continue;
                }

                foreach (var frame in group.Select(cell => cell.Frame).Distinct().OrderBy(frame => frame))
                {
                    changed |= ApplyTimelineCellEdit(edit, context, timeline, track, drawingScene, layer, frame);
                }
            }
        }

        if (!changed)
        {
            if (singleKeyframeDestinationFrame is { } destinationFrame && destinationFrame != _frame)
            {
                SetFrame(destinationFrame);
            }
            if (preservedInstanceSelectionIds.Length > 0)
            {
                RestoreTimelineInstanceSelection(preservedInstanceSelectionIds, preservedPrimaryInstanceId);
                UpdateInspector();
            }
            if (advanceSingleKeyframeInsertion) PlayTimelineEditFeedback(edit, cells);
            return advanceSingleKeyframeInsertion;
        }
        var undoPlayheadFrame = edit is TimelineEditKind.InsertKeyframe or TimelineEditKind.InsertBlankKeyframe
            ? previousPlayheadFrame
            : (int?)null;
        if (vectorSnapshot is not null)
        {
            PushUndoSnapshot(
                vectorSnapshot,
                drawingObjectDefinition,
                drawingObjectInstanceSnapshot,
                playheadFrame: undoPlayheadFrame,
                timelineSelection: previousTimelineSelection);
        }
        if (sceneDefinition is not null && sceneSnapshot is not null)
        {
            PushSceneTimelineUndo(
                sceneDefinition,
                sceneSnapshot,
                layerSnapshot: sceneLayerSnapshot,
                playheadFrame: undoPlayheadFrame,
                timelineSelection: previousTimelineSelection);
        }

        if (!advanceSingleKeyframeInsertion
            && edit is TimelineEditKind.InsertKeyframe or TimelineEditKind.InsertBlankKeyframe)
        {
            EnsureTimelineCellFramesExistForContext(
                context,
                timeline,
                cells,
                trailingFrames: singleKeyframeInsertionBeyondTrackEnd ? 0 : 1);
        }

        var insertedFrameDestination = movePlayheadToInsertedFrame && edit == TimelineEditKind.InsertFrame
            ? ResolveTimelineInsertPlayheadFrame(timeline, cells, _frame)
            : _frame;

        CompleteTimelineMutation(
            context,
            drawingScene,
            previousLastFrame,
            refreshCurrentComposition,
            preservedInstanceSelectionIds,
            preservedPrimaryInstanceId,
            singleKeyframeDestinationFrame);
        if (movePlayheadToInsertedFrame && edit == TimelineEditKind.InsertFrame)
        {
            SetFrame(insertedFrameDestination);
        }
        PlayTimelineEditFeedback(edit, cells);
        return true;
    }

    private void PlayTimelineEditFeedback(TimelineEditKind edit, IReadOnlyList<TimelineFrameCell> cells)
    {
        var command = edit switch
        {
            TimelineEditKind.InsertFrame => TimelineCommand.InsertFrames,
            TimelineEditKind.RemoveFrame => TimelineCommand.DeleteFrames,
            TimelineEditKind.InsertKeyframe => TimelineCommand.InsertKeyframes,
            TimelineEditKind.InsertBlankKeyframe => TimelineCommand.InsertBlankKeyframes,
            TimelineEditKind.ClearKeyframe => TimelineCommand.ClearKeyframes,
            _ => (TimelineCommand?)null
        };
        if (command is { } value) _timeline.PlayCommandFeedback(value, cells);
    }

    internal static TimelineFrameCell AdvanceTimelineKeyframeShortcutCell(TimelineFrameCell cell)
    {
        return cell.Frame >= int.MaxValue - 1
            ? cell
            : cell with { Frame = cell.Frame + 1 };
    }

    internal static bool TimelineKeyframeInsertionShouldAdvance(
        AnimationTimeline timeline,
        TimelineFrameCell cell)
    {
        var track = timeline.FindTrack(cell.TrackId);
        return track is not null
            && cell.Frame >= 0
            && cell.Frame < track.Duration
            && track.EvaluateExposure(cell.Frame).IsKeyframe;
    }

    internal static bool TimelineCellIsBeyondTrackEnd(AnimationTimeline timeline, TimelineFrameCell cell)
    {
        var track = timeline.FindTrack(cell.TrackId);
        return track is not null && cell.Frame >= track.Duration;
    }

    internal static TimelineFrameCell ResolveTimelineKeyframeInsertionCell(
        AnimationTimeline timeline,
        string activeTrackId,
        int playheadFrame,
        TimelineFrameCell selectedCell = default)
    {
        var playheadCell = new TimelineFrameCell(activeTrackId, Math.Max(0, playheadFrame));
        return string.Equals(selectedCell.TrackId, activeTrackId, StringComparison.Ordinal)
            && TimelineCellIsBeyondTrackEnd(timeline, selectedCell)
                ? selectedCell
                : playheadCell;
    }

    internal static bool TimelineInsertRequiresCompositionRefresh(int currentFrame, int insertionFrame)
    {
        return Math.Max(0, insertionFrame) < Math.Max(0, currentFrame);
    }

    internal static int ResolveTimelineInsertPlayheadFrame(
        AnimationTimeline timeline,
        IEnumerable<TimelineFrameCell> cells,
        int fallbackFrame)
    {
        var destination = Math.Max(0, fallbackFrame);
        var found = false;
        foreach (var group in cells
                     .Where(cell => cell.Frame >= 0)
                     .GroupBy(cell => cell.TrackId, StringComparer.Ordinal))
        {
            var track = timeline.FindTrack(group.Key);
            if (track is null) continue;
            foreach (var range in TimelineFrameRanges(group.Select(cell => cell.Frame)))
            {
                var insertedEnd = range.Frame + range.Count - 1;
                var exposure = track.EvaluateExposure(range.Frame);
                var candidate = exposure.SourceKind is null
                    ? insertedEnd
                    : Math.Max(insertedEnd, exposure.EndFrame);
                destination = found ? Math.Max(destination, candidate) : candidate;
                found = true;
            }
        }

        return found ? destination : Math.Max(0, fallbackFrame);
    }

    internal static IReadOnlyList<TimelineBlankKeyframeRangePlan> ResolveTimelineBlankKeyframeRangePlans(
        IEnumerable<int> frames)
    {
        return TimelineFrameRanges(frames)
            .Select(range => new TimelineBlankKeyframeRangePlan(
                Enumerable.Range(range.Frame, Math.Max(0, range.Count - 1))
                    .Where(frame => frame > 0)
                    .ToArray(),
                range.Frame + range.Count - 1))
            .ToArray();
    }

    private bool ApplyTimelineRangeEdit(
        TimelineEditKind edit,
        ITimelineContext context,
        AnimationTimeline timeline,
        AnimationTimelineTrack track,
        VectorScene? drawingScene,
        int layer,
        int frame,
        int count)
    {
        bool changed;
        if (context is SceneDefinition sceneDefinition
            && IsSceneMaskTrack(sceneDefinition, track))
        {
            changed = edit == TimelineEditKind.InsertFrame
                ? _project.TryInsertSceneMaskTimelineFrame(
                    sceneDefinition.Id,
                    track.TargetId,
                    frame,
                    count)
                : _project.TryRemoveSceneMaskTimelineFrame(
                    sceneDefinition.Id,
                    track.TargetId,
                    frame,
                    count);
            return changed;
        }

        if (drawingScene is not null && layer >= 0)
        {
            changed = edit == TimelineEditKind.InsertFrame
                ? drawingScene.InsertTimelineFrame(layer, frame, count)
                : drawingScene.RemoveTimelineFrame(layer, frame, count);
        }
        else
        {
            changed = edit == TimelineEditKind.InsertFrame
                ? timeline.InsertFrame(track.Id, frame, count)
                : timeline.RemoveFrame(track.Id, frame, count);
        }

        if (!changed) return false;
        UpdateInstanceStateFrames(context, track.TargetId, edit, frame, count);
        if (context is DrawingObjectDefinition drawingObject)
        {
            drawingObject.RefreshInstanceTimelineTweenMaterializationsInLayer(track.TargetId);
        }
        return true;
    }

    private bool ApplyTimelineCellEdit(
        TimelineEditKind edit,
        ITimelineContext context,
        AnimationTimeline timeline,
        AnimationTimelineTrack track,
        VectorScene? drawingScene,
        int layer,
        int frame)
    {
        bool changed;
        TimelineKeyframeKind? insertedSceneKeyframeKind = null;
        if (context is SceneDefinition maskSceneDefinition
            && IsSceneMaskTrack(maskSceneDefinition, track))
        {
            changed = edit switch
            {
                TimelineEditKind.InsertKeyframe => _project.TryInsertSceneMaskTimelineKeyframe(
                    maskSceneDefinition.Id,
                    track.TargetId,
                    frame),
                TimelineEditKind.InsertBlankKeyframe => _project.TryInsertSceneMaskTimelineBlankKeyframe(
                    maskSceneDefinition.Id,
                    track.TargetId,
                    frame),
                TimelineEditKind.ClearKeyframe => _project.TryClearSceneMaskTimelineKeyframe(
                    maskSceneDefinition.Id,
                    track.TargetId,
                    frame),
                _ => false
            };
            return changed;
        }

        if (drawingScene is not null && layer >= 0)
        {
            changed = edit switch
            {
                TimelineEditKind.InsertKeyframe => drawingScene.InsertTimelineKeyframe(layer, frame),
                TimelineEditKind.InsertBlankKeyframe => drawingScene.InsertTimelineBlankKeyframe(layer, frame),
                TimelineEditKind.ClearKeyframe => drawingScene.ClearTimelineKeyframe(layer, frame),
                _ => false
            };
        }

        else if (context is SceneDefinition sceneDefinition
                 && edit == TimelineEditKind.InsertKeyframe)
        {
            var requestedKind = track.EvaluateExposure(frame).SourceKind
                ?? TimelineKeyframeKind.Blank;
            insertedSceneKeyframeKind = sceneDefinition.ResolveKeyframeKindForLayerContent(
                track.TargetId,
                requestedKind,
                frame);
            changed = insertedSceneKeyframeKind == TimelineKeyframeKind.Populated
                ? timeline.InsertKeyframe(track.Id, frame)
                : timeline.InsertBlankKeyframe(track.Id, frame);
        }

        else
        {
            changed = edit switch
            {
                TimelineEditKind.InsertKeyframe => timeline.InsertKeyframe(track.Id, frame),
                TimelineEditKind.InsertBlankKeyframe => timeline.InsertBlankKeyframe(track.Id, frame),
                TimelineEditKind.ClearKeyframe => timeline.ClearKeyframe(track.Id, frame),
                _ => false
            };
        }

        if (changed
            && (edit is (TimelineEditKind.InsertBlankKeyframe or TimelineEditKind.ClearKeyframe)
                || insertedSceneKeyframeKind == TimelineKeyframeKind.Blank))
        {
            RemoveInstanceStateKeyframes(context, track.TargetId, frame);
        }
        else if (changed && edit == TimelineEditKind.InsertKeyframe)
        {
            CaptureInstanceStateKeyframes(context, track.TargetId, frame);
        }

        return changed;
    }

    private static bool IsSceneMaskTrack(
        SceneDefinition sceneDefinition,
        AnimationTimelineTrack? track)
    {
        return track is not null
            && sceneDefinition.FindLayer(track.TargetId)?.Kind == SceneLayerKind.Mask;
    }

    private static void UpdateInstanceStateFrames(
        ITimelineContext context,
        string layerId,
        TimelineEditKind edit,
        int frame,
        int count)
    {
        if (context is DrawingObjectDefinition drawingObject)
        {
            if (edit == TimelineEditKind.InsertFrame) drawingObject.InsertInstanceStateFrames(layerId, frame, count);
            else drawingObject.RemoveInstanceStateFrames(layerId, frame, count);
        }
        else if (context is SceneDefinition scene)
        {
            if (edit == TimelineEditKind.InsertFrame) scene.InsertInstanceStateFrames(layerId, frame, count);
            else scene.RemoveInstanceStateFrames(layerId, frame, count);
        }
    }

    private static void RemoveInstanceStateKeyframes(ITimelineContext context, string layerId, int frame)
    {
        if (context is DrawingObjectDefinition drawingObject)
        {
            drawingObject.RemoveInstanceStateKeyframes(layerId, frame);
        }
        else if (context is SceneDefinition scene)
        {
            scene.RemoveInstanceStateKeyframes(layerId, frame);
        }
    }

    private static bool CaptureInstanceStateKeyframes(ITimelineContext context, string layerId, int frame)
    {
        if (frame <= 0) return false;
        var changed = false;
        foreach (var instance in TimelineInstancesInLayer(context, layerId))
        {
            changed |= instance.SetStateAtFrame(frame, instance.EvaluateState(frame));
        }

        return changed;
    }

    private static IReadOnlyList<(int Frame, int Count)> TimelineFrameRanges(IEnumerable<int> frames)
    {
        var ordered = frames.Where(frame => frame >= 0).Distinct().OrderBy(frame => frame).ToArray();
        if (ordered.Length == 0) return [];

        var ranges = new List<(int Frame, int Count)>();
        var start = ordered[0];
        var previous = start;
        for (var index = 1; index < ordered.Length; index++)
        {
            if (ordered[index] == previous + 1)
            {
                previous = ordered[index];
                continue;
            }

            ranges.Add((start, previous - start + 1));
            start = previous = ordered[index];
        }

        ranges.Add((start, previous - start + 1));
        return ranges;
    }

    private void CompleteTimelineMutation(
        ITimelineContext context,
        VectorScene? drawingScene,
        int previousLastFrame,
        bool refreshCurrentComposition = true,
        IReadOnlyCollection<string>? preservedInstanceSelectionIds = null,
        string? preservedPrimaryInstanceId = null,
        int? finalPlayheadFrame = null)
    {
        StopPlayback();
        ClearSelection();
        _timeline.RefreshTimeline();
        var frameRefreshed = ApplyBoundTimelineDuration(
            previousLastFrame,
            refreshClampedFrame: finalPlayheadFrame is null);
        if (finalPlayheadFrame is { } destinationFrame)
        {
            frameRefreshed = SetFrame(destinationFrame);
        }
        if (drawingScene is not null)
        {
            if (refreshCurrentComposition)
            {
                _hierarchyPanel.RefreshScene();
                if (!frameRefreshed) RebuildDrawingObjectUnderlay();
            }
            else if (drawingScene.OnionSkinEnabled)
            {
                RebuildOnionSkinPreview();
            }
            _stage.Invalidate();
        }
        else if (refreshCurrentComposition && !frameRefreshed)
        {
            RebuildSceneComposition();
        }
        else
        {
            _stage.Invalidate();
        }

        if (preservedInstanceSelectionIds is { Count: > 0 })
        {
            RestoreTimelineInstanceSelection(preservedInstanceSelectionIds, preservedPrimaryInstanceId);
        }
        UpdateInspector();
        RefreshTweenCurveInspector();
    }

    private void HandleTimelineCommand(TimelineCommand command, IReadOnlyList<TimelineFrameCell> cells)
    {
        switch (command)
        {
            case TimelineCommand.CopyFrames:
                CopyTimelineFrames(cells);
                break;
            case TimelineCommand.PasteFrames:
                PasteTimelineFrames(cells);
                break;
            case TimelineCommand.InsertFrames:
                ExecuteTimelineEdit(TimelineEditKind.InsertFrame, cells);
                break;
            case TimelineCommand.DeleteFrames:
                ExecuteTimelineEdit(TimelineEditKind.RemoveFrame, cells);
                break;
            case TimelineCommand.InsertKeyframes:
                ExecuteTimelineEdit(TimelineEditKind.InsertKeyframe, cells);
                break;
            case TimelineCommand.InsertBlankKeyframes:
                ExecuteTimelineEdit(TimelineEditKind.InsertBlankKeyframe, cells);
                break;
            case TimelineCommand.ClearKeyframes:
                ExecuteTimelineEdit(TimelineEditKind.ClearKeyframe, cells);
                break;
            case TimelineCommand.ClassicTween:
                ExecuteTimelineTween(cells, TimelineTweenKind.Classic);
                break;
            case TimelineCommand.ShapeTween:
                ExecuteTimelineTween(cells, TimelineTweenKind.Shape);
                break;
            case TimelineCommand.RemoveTween:
                ExecuteRemoveTimelineTween(cells);
                break;
        }
    }

    private bool ExecuteRemoveTimelineTween(IReadOnlyList<TimelineFrameCell> requestedCells)
    {
        var context = _timeline.Context;
        var drawingScene = context switch
        {
            VectorScene vectorScene => vectorScene,
            DrawingObjectDefinition drawingObject => drawingObject.Scene,
            _ => null
        };
        var tweenSelection = TimelineStrip.ResolveSingleTweenSelection(context.Timeline, requestedCells);
        if (drawingScene is null || tweenSelection is not { } selection) return false;

        var track = context.Timeline.FindTrack(selection.TrackId);
        var layer = track is null ? -1 : Array.IndexOf(drawingScene.LayerIds, track.TargetId);
        if ((uint)layer >= drawingScene.LayerCount) return false;

        var previousLastFrame = Math.Max(0, context.FrameCount - 1);
        var snapshot = drawingScene.CreateSnapshot();
        var timelineSelection = _timeline.CaptureSelectionSnapshot();
        var contextDrawingObject = context as DrawingObjectDefinition;
        var removed = contextDrawingObject is not null
            ? contextDrawingObject.RemoveTimelineTween(layer, selection.StartFrame, selection.EndFrame)
            : drawingScene.RemoveTimelineTween(layer, selection.StartFrame, selection.EndFrame);
        if (!removed) return false;

        var preservedInstanceSelectionIds = _selectedSceneInstanceIds.ToArray();
        var preservedPrimaryInstanceId = _selectedSceneInstanceId;

        PushUndoSnapshot(
            snapshot,
            drawingObject: contextDrawingObject,
            playheadFrame: _frame,
            timelineSelection: timelineSelection);
        CompleteTimelineMutation(
            context,
            drawingScene,
            previousLastFrame,
            preservedInstanceSelectionIds: preservedInstanceSelectionIds,
            preservedPrimaryInstanceId: preservedPrimaryInstanceId);
        _timeline.PlayCommandFeedback(TimelineCommand.RemoveTween, requestedCells);
        return true;
    }

    private bool ExecuteTimelineTween(
        IReadOnlyList<TimelineFrameCell> requestedCells,
        TimelineTweenKind kind)
    {
        var context = _timeline.Context;
        var drawingScene = context switch
        {
            VectorScene vectorScene => vectorScene,
            DrawingObjectDefinition drawingObject => drawingObject.Scene,
            _ => null
        };
        if (drawingScene is null || requestedCells.Count == 0) return false;

        var cells = requestedCells
            .Where(cell => context.Timeline.FindTrack(cell.TrackId) is not null)
            .Distinct()
            .ToArray();
        if (cells.Length == 0 || cells.Any(cell => !string.Equals(cell.TrackId, cells[0].TrackId, StringComparison.Ordinal)))
        {
            return false;
        }

        var track = context.Timeline.FindTrack(cells[0].TrackId);
        if (track is null) return false;
        var layer = Array.IndexOf(drawingScene.LayerIds, track.TargetId);
        if ((uint)layer >= drawingScene.LayerCount
            || !VectorScene.SupportsTimelineTweenLayer(drawingScene.GetLayerKind(layer), kind))
        {
            return false;
        }

        if (!track.TryResolveTweenSpan(
                cells.Min(cell => cell.Frame),
                cells.Max(cell => cell.Frame),
                out var startFrame,
                out var endFrame))
        {
            return false;
        }

        var previousLastFrame = Math.Max(0, context.FrameCount - 1);
        var snapshot = drawingScene.CreateSnapshot();
        var contextDrawingObject = context as DrawingObjectDefinition;
        var instanceSnapshot = contextDrawingObject?.CreateInstanceSnapshot();
        var selection = _timeline.CaptureSelectionSnapshot();
        var created = contextDrawingObject is not null
            ? contextDrawingObject.TryCreateTimelineTween(layer, startFrame, endFrame, kind, out var error)
            : drawingScene.TryCreateTimelineTween(layer, startFrame, endFrame, kind, out error);
        if (!created)
        {
            if (!string.IsNullOrWhiteSpace(error))
            {
                ModernMessageDialog.Show(
                    this,
                    UiLocalization.T(error),
                    kind == TimelineTweenKind.Classic ? "Classic Tween" : "Shape Tween",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            return false;
        }

        PushUndoSnapshot(
            snapshot,
            drawingObject: contextDrawingObject,
            instanceSnapshot: instanceSnapshot,
            playheadFrame: _frame,
            timelineSelection: selection);
        var preservedInstanceSelectionIds = _selectedSceneInstanceIds.ToArray();
        var preservedPrimaryInstanceId = _selectedSceneInstanceId;
        CompleteTimelineMutation(
            context,
            drawingScene,
            previousLastFrame,
            preservedInstanceSelectionIds: preservedInstanceSelectionIds,
            preservedPrimaryInstanceId: preservedPrimaryInstanceId);
        _timeline.PlayCommandFeedback(
            kind == TimelineTweenKind.Classic ? TimelineCommand.ClassicTween : TimelineCommand.ShapeTween,
            cells);
        return true;
    }

    private void RefreshTweenCurveInspector()
    {
        var selection = default(TimelineTweenSelection);
        var tween = default(TimelineTween);
        var visible = _workspaceTabs.SelectedView == WorkspaceView.BasicDrawing
            && TryResolveSelectedTimelineTween(out selection, out tween, out _, out _, out _);
        var visibilityChanged = _tweenCurveEditorPanel.Visible != visible;
        if (!visible)
        {
            if (_tweenCurveEditorPanel.Visible) _tweenCurveEditorPanel.Visible = false;
            _tweenCurveEditorPanel.ClearTween();
        }
        else
        {
            _tweenCurveEditorPanel.SetTween(selection, tween);
            _tweenCurveEditorPanel.Visible = true;
            _tweenCurveEditorPanel.BringToFront();
        }

        if (visibilityChanged) _basicInspectorPage.Content.PerformLayout();
    }

    private bool TryResolveSelectedTimelineTween(
        out TimelineTweenSelection selection,
        out TimelineTween tween,
        out VectorScene drawingScene,
        out DrawingObjectDefinition? drawingObject,
        out int layer)
    {
        selection = default;
        tween = default;
        drawingScene = null!;
        drawingObject = null;
        layer = -1;
        if (_timeline.SelectedTween is not { } selected) return false;

        var context = _timeline.Context;
        drawingScene = context switch
        {
            VectorScene vectorScene => vectorScene,
            DrawingObjectDefinition definition => definition.Scene,
            _ => null!
        };
        drawingObject = context as DrawingObjectDefinition;
        if (drawingScene is null) return false;

        var resolved = context.Timeline.EvaluateTween(selected.TrackId, selected.StartFrame);
        if (resolved is not { } span
            || span.StartFrame != selected.StartFrame
            || span.EndFrame != selected.EndFrame)
        {
            return false;
        }

        var track = context.Timeline.FindTrack(selected.TrackId);
        layer = track is null ? -1 : Array.IndexOf(drawingScene.LayerIds, track.TargetId);
        if ((uint)layer >= drawingScene.LayerCount) return false;
        selection = selected;
        tween = span;
        return true;
    }

    private void BeginTweenCurveEdit()
    {
        if (_tweenCurveEditSession is not null
            || !TryResolveSelectedTimelineTween(
                out var selection,
                out _,
                out var drawingScene,
                out var drawingObject,
                out _))
        {
            return;
        }

        _tweenCurveEditSession = new TweenCurveEditSession
        {
            Scene = drawingScene,
            Snapshot = drawingScene.CreateSnapshot(),
            TimelineSelection = _timeline.CaptureSelectionSnapshot(),
            TweenSelection = selection,
            DrawingObject = drawingObject,
            InstanceSnapshot = drawingObject?.CreateInstanceSnapshot()
        };
    }

    private void ApplyTweenCurve(TweenCurveAnchor[] anchors)
    {
        var session = _tweenCurveEditSession;
        if (session is null
            || !TryResolveSelectedTimelineTween(
                out var selection,
                out _,
                out var drawingScene,
                out _,
                out var layer)
            || selection != session.TweenSelection
            || !ReferenceEquals(drawingScene, session.Scene)
            || !(session.DrawingObject is not null
                ? session.DrawingObject.ReplaceTimelineTweenCurve(
                    layer,
                    selection.StartFrame,
                    selection.EndFrame,
                    anchors)
                : drawingScene.ReplaceTimelineTweenCurve(
                    layer,
                    selection.StartFrame,
                    selection.EndFrame,
                    anchors)))
        {
            return;
        }

        session.Changed = true;
        drawingScene.EditFrame = _frame;
        RebuildDrawingObjectUnderlay();
        if (drawingScene.OnionSkinEnabled) RebuildOnionSkinPreview();
        _stage.Invalidate();
    }

    private void CompleteTweenCurveEdit()
    {
        var session = _tweenCurveEditSession;
        _tweenCurveEditSession = null;
        if (session is null || !session.Changed) return;
        PushUndoSnapshot(
            session.Snapshot,
            drawingObject: session.DrawingObject,
            instanceSnapshot: session.InstanceSnapshot,
            playheadFrame: _frame,
            timelineSelection: session.TimelineSelection);
        RefreshTweenCurveInspector();
    }

    private void CancelTweenCurveEdit()
    {
        var session = _tweenCurveEditSession;
        _tweenCurveEditSession = null;
        if (session is null || !session.Changed)
        {
            RefreshTweenCurveInspector();
            return;
        }

        session.Scene.RestoreSnapshot(session.Snapshot);
        if (session.DrawingObject is not null && session.InstanceSnapshot is not null)
        {
            session.DrawingObject.RestoreInstanceSnapshot(session.InstanceSnapshot);
        }
        session.Scene.EditFrame = _frame;
        _timeline.RefreshTimeline();
        _timeline.RestoreSelectionSnapshot(session.TimelineSelection);
        _hierarchyPanel.RefreshScene();
        RebuildDrawingObjectUnderlay();
        SyncSelectionToStage();
        RefreshTweenCurveInspector();
        _stage.Invalidate();
    }

    private bool CopyTimelineFrames(IReadOnlyList<TimelineFrameCell>? requestedCells = null)
    {
        var context = _timeline.Context;
        context.SynchronizeTimelineTracks();
        var timeline = context.Timeline;
        var cells = (requestedCells ?? _timeline.SelectedFrameCells)
            .Where(cell => timeline.FindTrack(cell.TrackId) is not null)
            .Distinct()
            .ToArray();
        if (cells.Length == 0 && !string.IsNullOrWhiteSpace(_timeline.ActiveTrackId))
        {
            cells = [new TimelineFrameCell(_timeline.ActiveTrackId, _frame)];
        }
        if (cells.Length == 0) return false;

        var drawingScene = context switch
        {
            VectorScene vectorScene => vectorScene,
            DrawingObjectDefinition drawingObject => drawingObject.Scene,
            _ => null
        };
        VectorScene? drawingSource = null;
        if (drawingScene is not null)
        {
            drawingSource = new VectorScene();
            drawingSource.RestoreSnapshot(drawingScene.CreateSnapshot());
        }
        var sceneDefinition = context as SceneDefinition;
        var sceneMaskSources = CaptureSceneMaskTimelineClipboardSources(sceneDefinition, timeline, cells);

        var anchorTrack = cells.Min(cell => TrackIndex(timeline, cell.TrackId));
        var anchorFrame = cells.Min(cell => cell.Frame);
        _timelineClipboard = new TimelineFrameClipboard
        {
            DrawingSource = drawingSource,
            Cells = cells.Select(cell =>
            {
                var track = timeline.FindTrack(cell.TrackId)!;
                var exposure = track.EvaluateExposure(Math.Min(cell.Frame, track.Duration - 1));
                var sourceLayer = drawingScene is null ? -1 : Array.IndexOf(drawingScene.LayerIds, track.TargetId);
                var sourceKind = sceneDefinition?.ResolveKeyframeKindForLayerContent(
                    track.TargetId,
                    exposure.SourceKind ?? TimelineKeyframeKind.Blank,
                    cell.Frame) ?? exposure.SourceKind ?? TimelineKeyframeKind.Blank;
                return new TimelineClipboardCell(
                    TrackIndex(timeline, cell.TrackId) - anchorTrack,
                    cell.Frame - anchorFrame,
                    sourceLayer,
                    cell.Frame,
                    sourceKind,
                    exposure.HasContent
                        ? TimelineInstancesInLayer(context, track.TargetId).ToDictionary(
                            instance => instance.Id,
                            instance => instance.EvaluateState(cell.Frame),
                            StringComparer.Ordinal)
                        : new Dictionary<string, InstanceFrameState>(StringComparer.Ordinal));
            }).ToArray()
        };
        _sceneMaskTimelineClipboardSources = sceneMaskSources;
        _timeline.PlayCommandFeedback(TimelineCommand.CopyFrames, cells);
        return true;
    }

    private static VectorScene?[] CaptureSceneMaskTimelineClipboardSources(
        SceneDefinition? sceneDefinition,
        AnimationTimeline timeline,
        IReadOnlyList<TimelineFrameCell> cells)
    {
        var sources = new VectorScene?[cells.Count];
        if (sceneDefinition is null || cells.Count == 0) return sources;

        var frozenScenes = new Dictionary<string, VectorScene>(StringComparer.Ordinal);
        for (var index = 0; index < cells.Count; index++)
        {
            var track = timeline.FindTrack(cells[index].TrackId);
            if (!IsSceneMaskTrack(sceneDefinition, track)
                || sceneDefinition.FindMaskScene(track!.TargetId) is not { } maskScene)
            {
                continue;
            }

            if (!frozenScenes.TryGetValue(track.TargetId, out var frozenScene))
            {
                frozenScene = new VectorScene();
                frozenScene.RestoreSnapshot(maskScene.CreateSnapshot());
                frozenScenes.Add(track.TargetId, frozenScene);
            }
            sources[index] = frozenScene;
        }

        return sources;
    }

    private bool PasteTimelineFrames(IReadOnlyList<TimelineFrameCell>? requestedCells = null)
    {
        var clipboard = _timelineClipboard;
        if (clipboard is null || clipboard.Cells.Length == 0) return false;

        var context = _timeline.Context;
        context.SynchronizeTimelineTracks();
        var timeline = context.Timeline;
        var destinationCells = (requestedCells ?? _timeline.SelectedFrameCells)
            .Where(cell => timeline.FindTrack(cell.TrackId) is not null)
            .ToArray();
        var anchorTrack = destinationCells.Length > 0
            ? destinationCells.Min(cell => TrackIndex(timeline, cell.TrackId))
            : TrackIndex(timeline, _timeline.ActiveTrackId);
        var anchorFrame = destinationCells.Length > 0 ? destinationCells.Min(cell => cell.Frame) : _frame;
        if (anchorTrack < 0) return false;

        var previousLastFrame = Math.Max(0, context.FrameCount - 1);
        var drawingScene = context switch
        {
            VectorScene vectorScene => vectorScene,
            DrawingObjectDefinition drawingObject => drawingObject.Scene,
            _ => null
        };
        var vectorSnapshot = drawingScene?.CreateSnapshot();
        var sceneDefinition = context as SceneDefinition;
        var sceneSnapshot = sceneDefinition is null ? null : timeline.CreateSnapshot();
        var pastesSceneMask = false;
        if (sceneDefinition is not null)
        {
            for (var sourceIndex = 0; sourceIndex < clipboard.Cells.Length; sourceIndex++)
            {
                var destinationTrackIndex = anchorTrack + clipboard.Cells[sourceIndex].TrackOffset;
                if (destinationTrackIndex < 0 || destinationTrackIndex >= timeline.Tracks.Count) continue;
                if (!IsSceneMaskTrack(sceneDefinition, timeline.Tracks[destinationTrackIndex])) continue;
                if (sourceIndex >= _sceneMaskTimelineClipboardSources.Length
                    || _sceneMaskTimelineClipboardSources[sourceIndex] is null)
                {
                    continue;
                }
                pastesSceneMask = true;
                break;
            }
        }
        var sceneLayerSnapshot = pastesSceneMask ? sceneDefinition!.CreateLayerSnapshot() : null;
        var changed = false;
        var pastedCells = new List<TimelineFrameCell>();

        using (timeline.BeginBatchUpdate())
        {
            for (var sourceIndex = 0; sourceIndex < clipboard.Cells.Length; sourceIndex++)
            {
                var source = clipboard.Cells[sourceIndex];
                var destinationTrackIndex = anchorTrack + source.TrackOffset;
                var destinationFrame = Math.Max(0, anchorFrame + source.FrameOffset);
                if (destinationTrackIndex < 0 || destinationTrackIndex >= timeline.Tracks.Count) continue;
                var track = timeline.Tracks[destinationTrackIndex];
                if (sceneDefinition is not null && IsSceneMaskTrack(sceneDefinition, track))
                {
                    var maskSource = sourceIndex < _sceneMaskTimelineClipboardSources.Length
                        ? _sceneMaskTimelineClipboardSources[sourceIndex]
                        : null;
                    if (maskSource is null) continue;
                    pastedCells.Add(new TimelineFrameCell(track.Id, destinationFrame));
                    changed |= _project.TryPasteSceneMaskTimelineFrame(
                        sceneDefinition.Id,
                        track.TargetId,
                        maskSource,
                        Math.Clamp(source.SourceFrame, 0, Math.Max(0, maskSource.FrameCount - 1)),
                        destinationFrame);
                    continue;
                }

                var effectiveKind = sceneDefinition?.ResolveKeyframeKindForLayerContent(
                    track.TargetId,
                    source.Kind,
                    destinationFrame) ?? source.Kind;
                pastedCells.Add(new TimelineFrameCell(track.Id, destinationFrame));
                var destinationLayer = drawingScene is null ? -1 : Array.IndexOf(drawingScene.LayerIds, track.TargetId);
                if (drawingScene is not null
                    && destinationLayer >= 0
                    && source.SourceLayer >= 0
                    && clipboard.DrawingSource is not null)
                {
                    changed |= drawingScene.CopyTimelineFrameFrom(
                        clipboard.DrawingSource,
                        source.SourceLayer,
                        source.SourceFrame,
                        destinationLayer,
                        destinationFrame);
                    if (effectiveKind == TimelineKeyframeKind.Populated && source.InstanceStates.Count > 0)
                    {
                        changed |= timeline.InsertKeyframe(track.Id, destinationFrame);
                    }
                    changed |= ApplyClipboardInstanceStates(
                        context,
                        track.TargetId,
                        destinationFrame,
                        effectiveKind,
                        source.InstanceStates);
                    continue;
                }

                if (destinationFrame >= track.Duration) timeline.SetTrackDuration(track.Id, destinationFrame + 1);
                changed |= effectiveKind == TimelineKeyframeKind.Populated
                    ? timeline.InsertKeyframe(track.Id, destinationFrame)
                    : timeline.InsertBlankKeyframe(track.Id, destinationFrame);
                changed |= ApplyClipboardInstanceStates(
                    context,
                    track.TargetId,
                    destinationFrame,
                    effectiveKind,
                    source.InstanceStates);
            }
        }

        if (!changed) return false;
        if (vectorSnapshot is not null) PushUndoSnapshot(vectorSnapshot);
        if (sceneDefinition is not null && sceneSnapshot is not null)
        {
            PushSceneTimelineUndo(sceneDefinition, sceneSnapshot, layerSnapshot: sceneLayerSnapshot);
        }
        CompleteTimelineMutation(context, drawingScene, previousLastFrame);
        _timeline.PlayCommandFeedback(TimelineCommand.PasteFrames, pastedCells);
        return true;
    }

    private static IReadOnlyList<DrawingObjectInstanceDefinition> TimelineInstancesInLayer(
        ITimelineContext context,
        string layerId)
    {
        return context switch
        {
            DrawingObjectDefinition drawingObject => drawingObject.InstancesInLayer(layerId),
            SceneDefinition scene => scene.InstancesInLayer(layerId),
            _ => []
        };
    }

    private static bool ApplyClipboardInstanceStates(
        ITimelineContext context,
        string layerId,
        int frame,
        TimelineKeyframeKind kind,
        IReadOnlyDictionary<string, InstanceFrameState> states)
    {
        var changed = false;
        foreach (var instance in TimelineInstancesInLayer(context, layerId))
        {
            if (kind == TimelineKeyframeKind.Blank)
            {
                changed |= instance.RemoveStateKeyframe(frame);
            }
            else if (states.TryGetValue(instance.Id, out var state))
            {
                changed |= instance.SetStateAtFrame(frame, state);
            }
        }

        return changed;
    }

    private void MoveTimelineLayerOutOfMask()
    {
        var context = _timeline.Context;
        context.SynchronizeTimelineTracks();
        var track = context.Timeline.FindTrack(_timeline.ActiveTrackId ?? "");
        if (track is null) return;

        var drawingScene = context switch
        {
            VectorScene vectorScene => vectorScene,
            DrawingObjectDefinition drawingObject => drawingObject.Scene,
            _ => null
        };
        if (drawingScene is not null)
        {
            var contentLayer = Array.IndexOf(drawingScene.LayerIds, track.TargetId);
            if (contentLayer < 0) return;
            var snapshot = drawingScene.CreateSnapshot();
            if (!drawingScene.MoveLayerOutOfMask(contentLayer)) return;
            PushUndoSnapshot(snapshot);
            _timeline.RefreshTimeline();
            RebuildDrawingObjectUnderlay();
            _hierarchyPanel.RefreshScene();
            UpdateInspector();
            _stage.Invalidate();
            return;
        }

        if (context is not SceneDefinition sceneDefinition) return;
        var contentSceneLayer = sceneDefinition.FindLayer(track.TargetId);
        if (contentSceneLayer?.Kind != SceneLayerKind.Content) return;
        var timelineSnapshot = sceneDefinition.Timeline.CreateSnapshot();
        var layerSnapshot = sceneDefinition.CreateLayerSnapshot();
        if (!_project.TryMoveSceneLayerOutOfMask(sceneDefinition.Id, contentSceneLayer.Id)) return;
        PushSceneTimelineUndo(sceneDefinition, timelineSnapshot, layerSnapshot);
        _timeline.RefreshTimeline();
        RebuildSceneComposition();
        UpdateInspector();
    }

    private void MoveTimelineLayer(
        string trackId,
        string targetTrackId,
        TimelineLayerDropPlacement placement,
        bool moveOutOfMask = false)
    {
        var context = _timeline.Context;
        context.SynchronizeTimelineTracks();
        var track = context.Timeline.FindTrack(trackId);
        var targetTrack = context.Timeline.FindTrack(targetTrackId);
        if (track is null || targetTrack is null) return;

        var drawingScene = context switch
        {
            VectorScene vectorScene => vectorScene,
            DrawingObjectDefinition drawingObject => drawingObject.Scene,
            _ => null
        };
        if (drawingScene is not null)
        {
            var sourceLayer = Array.IndexOf(drawingScene.LayerIds, track.TargetId);
            var targetLayer = Array.IndexOf(drawingScene.LayerIds, targetTrack.TargetId);
            if (sourceLayer < 0 || targetLayer < 0 || sourceLayer == targetLayer) return;
            var snapshot = drawingScene.CreateSnapshot();
            var changed = placement == TimelineLayerDropPlacement.Mask
                ? LinkTimelineMaskLayers(drawingScene, track.TargetId, targetTrack.TargetId)
                : MoveTimelineDrawingLayer(
                    drawingScene,
                    track.TargetId,
                    targetTrack.TargetId,
                    placement,
                    moveOutOfMask);
            if (!changed) return;
            PushUndoSnapshot(snapshot);
            _timeline.RefreshTimeline();
            RebuildDrawingObjectUnderlay();
            _hierarchyPanel.RefreshScene();
            UpdateInspector();
            _stage.Invalidate();
            return;
        }

        if (context is not SceneDefinition sceneDefinition) return;
        var sceneSourceLayer = sceneDefinition.FindLayer(track.TargetId);
        var sceneTargetLayer = sceneDefinition.FindLayer(targetTrack.TargetId);
        if (sceneSourceLayer is null || sceneTargetLayer is null) return;
        var timelineSnapshot = sceneDefinition.Timeline.CreateSnapshot();
        var layerSnapshot = sceneDefinition.CreateLayerSnapshot();
        var changedScene = moveOutOfMask
            && _project.TryMoveSceneLayerOutOfMask(sceneDefinition.Id, sceneSourceLayer.Id);
        var sourceIndex = sceneDefinition.Layers
            .Select((layer, index) => (layer, index))
            .First(item => string.Equals(item.layer.Id, sceneSourceLayer.Id, StringComparison.Ordinal))
            .index;
        var targetIndex = sceneDefinition.Layers
            .Select((layer, index) => (layer, index))
            .First(item => string.Equals(item.layer.Id, sceneTargetLayer.Id, StringComparison.Ordinal))
            .index;
        var sceneDestinationLayer = ResolveSceneLayerDestinationIndex(
            sourceIndex,
            targetIndex,
            placement,
            sceneDefinition.Layers.Count);
        if (sceneDestinationLayer >= 0)
        {
            changedScene |= _project.TryMoveSceneLayer(sceneDefinition.Id, sceneSourceLayer.Id, sceneDestinationLayer);
        }
        if (!changedScene) return;
        PushSceneTimelineUndo(sceneDefinition, timelineSnapshot, layerSnapshot);
        _timeline.RefreshTimeline();
        RebuildSceneComposition();
        UpdateInspector();
    }

    internal static int ResolveSceneLayerDestinationIndex(
        int sourceIndex,
        int targetIndex,
        TimelineLayerDropPlacement placement,
        int layerCount)
    {
        if (layerCount <= 0
            || (uint)sourceIndex >= layerCount
            || (uint)targetIndex >= layerCount
            || sourceIndex == targetIndex
            || placement is not TimelineLayerDropPlacement.Before and not TimelineLayerDropPlacement.After)
        {
            return -1;
        }

        var insertionIndex = placement == TimelineLayerDropPlacement.Before
            ? targetIndex
            : targetIndex + 1;
        if (sourceIndex < insertionIndex) insertionIndex--;
        return Math.Clamp(insertionIndex, 0, layerCount - 1);
    }

    private static bool MoveTimelineDrawingLayer(
        VectorScene drawingScene,
        string sourceLayerId,
        string targetLayerId,
        TimelineLayerDropPlacement placement,
        bool moveOutOfMask = false)
    {
        var sourceLayer = Array.IndexOf(drawingScene.LayerIds, sourceLayerId);
        var targetLayer = Array.IndexOf(drawingScene.LayerIds, targetLayerId);
        if (sourceLayer < 0 || targetLayer < 0 || sourceLayer == targetLayer) return false;
        var changed = moveOutOfMask && drawingScene.MoveLayerOutOfMask(sourceLayer);

        sourceLayer = Array.IndexOf(drawingScene.LayerIds, sourceLayerId);
        targetLayer = Array.IndexOf(drawingScene.LayerIds, targetLayerId);
        if (sourceLayer < 0 || targetLayer < 0) return changed;
        if (drawingScene.IsLayerDescendantOf(targetLayer, sourceLayer)) return changed;

        var requestedParent = placement == TimelineLayerDropPlacement.Inside
            ? targetLayer
            : drawingScene.GetLayerParentIndex(targetLayer);
        if (!drawingScene.CanSetLayerParent(sourceLayer, requestedParent)) return changed;

        changed |= drawingScene.SetLayerParent(sourceLayer, requestedParent);
        sourceLayer = Array.IndexOf(drawingScene.LayerIds, sourceLayerId);
        targetLayer = Array.IndexOf(drawingScene.LayerIds, targetLayerId);
        if (sourceLayer < 0 || targetLayer < 0) return changed;
        changed |= placement == TimelineLayerDropPlacement.Before
            ? drawingScene.MoveLayerBefore(sourceLayer, targetLayer)
            : drawingScene.MoveLayerAfter(sourceLayer, targetLayer);
        return changed;
    }

    private static bool LinkTimelineMaskLayers(VectorScene drawingScene, string sourceLayerId, string targetLayerId)
    {
        var sourceLayer = Array.IndexOf(drawingScene.LayerIds, sourceLayerId);
        var targetLayer = Array.IndexOf(drawingScene.LayerIds, targetLayerId);
        if (sourceLayer < 0 || targetLayer < 0) return false;

        var sourceKind = drawingScene.GetLayerKind(sourceLayer);
        var targetKind = drawingScene.GetLayerKind(targetLayer);
        var sourceIsMask = sourceKind == DrawingLayerKind.Mask && targetKind == DrawingLayerKind.Drawing;
        var targetIsMask = sourceKind == DrawingLayerKind.Drawing && targetKind == DrawingLayerKind.Mask;
        if (!sourceIsMask && !targetIsMask) return false;

        var maskLayerId = sourceIsMask ? sourceLayerId : targetLayerId;
        var contentLayerId = sourceIsMask ? targetLayerId : sourceLayerId;
        var maskLayer = Array.IndexOf(drawingScene.LayerIds, maskLayerId);
        var contentLayer = Array.IndexOf(drawingScene.LayerIds, contentLayerId);
        if (maskLayer < 0 || contentLayer < 0) return false;

        var changed = sourceIsMask
            ? drawingScene.ClearMaskLayerLinks(maskLayer)
            : drawingScene.ClearLayerMask(contentLayer);
        maskLayer = Array.IndexOf(drawingScene.LayerIds, maskLayerId);
        contentLayer = Array.IndexOf(drawingScene.LayerIds, contentLayerId);
        if (maskLayer < 0 || contentLayer < 0) return changed;

        var requestedParent = sourceIsMask
            ? drawingScene.GetLayerParentIndex(contentLayer)
            : drawingScene.GetLayerParentIndex(maskLayer);
        changed |= drawingScene.SetLayerParent(sourceIsMask ? maskLayer : contentLayer, requestedParent);

        maskLayer = Array.IndexOf(drawingScene.LayerIds, maskLayerId);
        contentLayer = Array.IndexOf(drawingScene.LayerIds, contentLayerId);
        if (maskLayer < 0 || contentLayer < 0) return changed;
        changed |= sourceIsMask
            ? drawingScene.MoveLayerBefore(maskLayer, contentLayer)
            : drawingScene.MoveLayerAfter(contentLayer, maskLayer);

        maskLayer = Array.IndexOf(drawingScene.LayerIds, maskLayerId);
        contentLayer = Array.IndexOf(drawingScene.LayerIds, contentLayerId);
        return maskLayer >= 0 && contentLayer >= 0 && (drawingScene.SetLayerMask(contentLayer, maskLayer) || changed);
    }

    private IReadOnlyList<string> SelectedTimelineLayerTargetIds()
    {
        var selected = _timeline.SelectedLayerTargetIds;
        if (selected.Count > 0) return selected;
        var active = _timeline.Context.Timeline.FindTrack(_timeline.ActiveTrackId ?? "");
        return active is null ? [] : [active.TargetId];
    }

    private void RefreshLayerBlendModePanel()
    {
        var context = _timeline.Context;
        var activeTargetId = context.Timeline.FindTrack(_timeline.ActiveTrackId ?? "")?.TargetId;
        if (string.IsNullOrWhiteSpace(activeTargetId))
        {
            _layerBlendModePanel.ClearLayer();
            return;
        }

        var drawingScene = context switch
        {
            VectorScene vectorScene => vectorScene,
            DrawingObjectDefinition drawingObject => drawingObject.Scene,
            _ => null
        };
        if (drawingScene is not null)
        {
            var layer = Array.IndexOf(drawingScene.LayerIds, activeTargetId);
            if ((uint)layer < drawingScene.LayerCount && layer < drawingScene.LayerBlendModes.Length)
            {
                _layerBlendModePanel.SetLayer(drawingScene.LayerNames[layer], drawingScene.LayerBlendModes[layer]);
                return;
            }
        }
        else if (context is SceneDefinition sceneDefinition
            && sceneDefinition.FindLayer(activeTargetId) is { } sceneLayer)
        {
            _layerBlendModePanel.SetLayer(sceneLayer.Name, sceneLayer.BlendMode);
            return;
        }

        _layerBlendModePanel.ClearLayer();
    }

    private void ApplySelectedLayerBlendMode(LayerBlendMode blendMode)
    {
        if (!Enum.IsDefined(blendMode)) return;
        var context = _timeline.Context;
        var targetIds = SelectedTimelineLayerTargetIds();
        if (targetIds.Count == 0) return;

        var drawingScene = context switch
        {
            VectorScene vectorScene => vectorScene,
            DrawingObjectDefinition drawingObject => drawingObject.Scene,
            _ => null
        };
        if (drawingScene is not null)
        {
            var layers = targetIds
                .Select(targetId => Array.IndexOf(drawingScene.LayerIds, targetId))
                .Where(layer => layer >= 0)
                .Distinct()
                .ToArray();
            if (layers.Length == 0) return;

            var snapshot = drawingScene.CreateSnapshot();
            var changed = false;
            foreach (var layer in layers) changed |= drawingScene.SetLayerBlendMode(layer, blendMode);
            if (!changed)
            {
                RefreshLayerBlendModePanel();
                return;
            }

            PushUndoSnapshot(snapshot);
            _timeline.Invalidate();
            if (context is DrawingObjectDefinition) RebuildDrawingObjectUnderlay();
            _hierarchyPanel.RefreshScene();
            RefreshLayerBlendModePanel();
            _stage.Invalidate();
            return;
        }

        if (context is not SceneDefinition sceneDefinition) return;
        var layerIds = targetIds
            .Where(targetId => sceneDefinition.FindLayer(targetId) is not null)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (layerIds.Length == 0) return;

        var timelineSnapshot = sceneDefinition.Timeline.CreateSnapshot();
        var layerSnapshot = sceneDefinition.CreateLayerSnapshot();
        var sceneChanged = false;
        foreach (var layerId in layerIds)
        {
            sceneChanged |= _project.TrySetSceneLayerBlendMode(sceneDefinition.Id, layerId, blendMode);
        }
        if (!sceneChanged)
        {
            RefreshLayerBlendModePanel();
            return;
        }

        PushSceneTimelineUndo(sceneDefinition, timelineSnapshot, layerSnapshot);
        RebuildSceneComposition();
        _timeline.Invalidate();
        RefreshLayerBlendModePanel();
        _stage.Invalidate();
    }

    private void RemoveTimelineLayers()
    {
        var context = _timeline.Context;
        var targetIds = SelectedTimelineLayerTargetIds();
        if (targetIds.Count == 0) return;

        var drawingScene = context switch
        {
            VectorScene vectorScene => vectorScene,
            DrawingObjectDefinition drawingObject => drawingObject.Scene,
            _ => null
        };
        if (drawingScene is not null)
        {
            var removal = drawingScene.ResolveLayerRemovalIndices(targetIds);
            if (!ConfirmTimelineLayerRemoval(removal.Length, drawingScene.LayerCount)) return;

            var sceneSnapshot = drawingScene.CreateSnapshot();
            DrawingObjectInstanceDefinition[]? instanceSnapshot = null;
            bool changed;
            if (context is DrawingObjectDefinition drawingObject)
            {
                instanceSnapshot = drawingObject.CreateInstanceSnapshot();
                changed = _project.TryRemoveDrawingObjectLayers(drawingObject.Id, targetIds);
            }
            else
            {
                changed = drawingScene.RemoveLayers(targetIds);
            }
            if (!changed) return;

            PushUndoSnapshot(sceneSnapshot, context as DrawingObjectDefinition, instanceSnapshot);
            ClearSelection();
            _timeline.RefreshTimeline();
            _timeline.SelectModelActiveTrack();
            RebuildDrawingObjectUnderlay();
            _hierarchyPanel.RefreshScene();
            UpdateInspector();
            _stage.Invalidate();
            return;
        }

        if (context is not SceneDefinition sceneDefinition) return;
        var sceneRemovalCount = targetIds
            .Distinct(StringComparer.Ordinal)
            .Count(targetId => sceneDefinition.FindLayer(targetId) is not null);
        if (!ConfirmTimelineLayerRemoval(sceneRemovalCount, sceneDefinition.Layers.Count)) return;

        var timelineSnapshot = sceneDefinition.Timeline.CreateSnapshot();
        var layerSnapshot = sceneDefinition.CreateLayerSnapshot();
        var sceneInstanceSnapshot = sceneDefinition.CreateInstanceSnapshot();
        if (!_project.TryRemoveSceneLayers(sceneDefinition.Id, targetIds)) return;

        PushSceneTimelineUndo(sceneDefinition, timelineSnapshot, layerSnapshot, sceneInstanceSnapshot);
        ClearSelection();
        _timeline.RefreshTimeline();
        _timeline.SelectModelActiveTrack();
        RebuildSceneComposition();
        _hierarchyPanel.RefreshScene();
        UpdateInspector();
        _stage.Invalidate();
    }

    private bool ConfirmTimelineLayerRemoval(int removalCount, int layerCount)
    {
        if (removalCount <= 0) return false;
        if (removalCount >= layerCount)
        {
            ModernMessageDialog.Show(
                this,
                UiLocalization.T("At least one layer must remain."),
                UiLocalization.T("Delete Layers"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return false;
        }
        if (removalCount == 1) return true;

        var prompt = string.Format(
            UiLocalization.T("Delete {0} layers and all of their contents?"),
            removalCount);
        return ModernMessageDialog.Show(
            this,
            prompt,
            UiLocalization.T("Delete Layers"),
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    private void ChooseTimelineLayerColor()
    {
        var context = _timeline.Context;
        var targetIds = SelectedTimelineLayerTargetIds();
        if (targetIds.Count == 0) return;

        var drawingScene = context switch
        {
            VectorScene vectorScene => vectorScene,
            DrawingObjectDefinition drawingObject => drawingObject.Scene,
            _ => null
        };
        var activeTargetId = context.Timeline.FindTrack(_timeline.ActiveTrackId ?? "")?.TargetId;
        if (drawingScene is not null)
        {
            var layers = targetIds
                .Select(targetId => Array.IndexOf(drawingScene.LayerIds, targetId))
                .Where(layer => layer >= 0)
                .Distinct()
                .ToArray();
            if (layers.Length == 0) return;
            var activeLayer = string.IsNullOrWhiteSpace(activeTargetId)
                ? -1
                : Array.IndexOf(drawingScene.LayerIds, activeTargetId);
            var snapshot = drawingScene.CreateSnapshot();
            var drawingOriginalColors = layers.Select(drawingScene.GetLayerColor).Select(color => color.ToArgb()).ToArray();
            var drawingCurrentColor = drawingScene.GetLayerColor(activeLayer >= 0 && layers.Contains(activeLayer) ? activeLayer : layers[0]);
            using var picker = new LayerColorDialog(drawingCurrentColor);
            picker.ColorChanged += (_, _) =>
            {
                foreach (var layer in layers) drawingScene.SetLayerColor(layer, picker.Color);
                RefreshTimelineLayerDisplay(context);
            };
            if (picker.ShowDialog(this) != DialogResult.OK)
            {
                drawingScene.RestoreSnapshot(snapshot);
                RefreshTimelineLayerDisplay(context);
                return;
            }

            foreach (var layer in layers) drawingScene.SetLayerColor(layer, picker.Color);
            if (!layers.Select((layer, index) => drawingScene.GetLayerColor(layer).ToArgb() != drawingOriginalColors[index]).Any(changed => changed)) return;
            PushUndoSnapshot(snapshot);
            RefreshTimelineLayerDisplay(context);
            return;
        }

        if (context is not SceneDefinition sceneDefinition) return;
        var sceneLayerIds = targetIds
            .Where(targetId => sceneDefinition.FindLayer(targetId) is not null)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (sceneLayerIds.Length == 0) return;
        var activeSceneLayer = !string.IsNullOrWhiteSpace(activeTargetId)
            ? sceneDefinition.FindLayer(activeTargetId)
            : null;
        var current = activeSceneLayer is not null && sceneLayerIds.Contains(activeSceneLayer.Id, StringComparer.Ordinal)
            ? Color.FromArgb(activeSceneLayer.ColorArgb)
            : Color.FromArgb(sceneDefinition.FindLayer(sceneLayerIds[0])!.ColorArgb);
        var timelineSnapshot = sceneDefinition.Timeline.CreateSnapshot();
        var layerSnapshot = sceneDefinition.CreateLayerSnapshot();
        var originalColors = sceneLayerIds.Select(layerId => sceneDefinition.FindLayer(layerId)!.ColorArgb).ToArray();
        using (var picker = new LayerColorDialog(current))
        {
            picker.ColorChanged += (_, _) =>
            {
                foreach (var layerId in sceneLayerIds) sceneDefinition.SetLayerColor(layerId, picker.Color);
                RefreshTimelineLayerDisplay(context);
            };
            if (picker.ShowDialog(this) != DialogResult.OK)
            {
                sceneDefinition.RestoreLayerSnapshot(layerSnapshot);
                RefreshTimelineLayerDisplay(context);
                return;
            }

            foreach (var layerId in sceneLayerIds) sceneDefinition.SetLayerColor(layerId, picker.Color);
            var changed = sceneLayerIds
                .Select((layerId, index) => sceneDefinition.FindLayer(layerId)!.ColorArgb != originalColors[index])
                .Any(value => value);
            if (!changed) return;
            PushSceneTimelineUndo(sceneDefinition, timelineSnapshot, layerSnapshot);
        }
        RefreshTimelineLayerDisplay(context);
    }

    private void RefreshTimelineLayerDisplay(ITimelineContext context)
    {
        _timeline.Invalidate();
        if (context is SceneDefinition) RebuildSceneComposition();
        else if (context is DrawingObjectDefinition) RebuildDrawingObjectUnderlay();
        else RebuildOnionSkinPreview();
        _stage.Invalidate();
    }

    private void RenameTimelineLayer()
    {
        var context = _timeline.Context;
        var track = context.Timeline.FindTrack(_timeline.ActiveTrackId ?? "");
        if (track is null) return;

        var drawingScene = context switch
        {
            VectorScene vectorScene => vectorScene,
            DrawingObjectDefinition drawingObject => drawingObject.Scene,
            _ => null
        };
        if (drawingScene is not null)
        {
            var layer = Array.IndexOf(drawingScene.LayerIds, track.TargetId);
            if (layer < 0
                || !DrawingObjectNameDialog.TryAsk(this, "Rename Layer", "Layer name", drawingScene.LayerNames[layer], out var name))
            {
                return;
            }

            var snapshot = drawingScene.CreateSnapshot();
            if (!drawingScene.RenameLayer(layer, name)) return;
            PushUndoSnapshot(snapshot);
            _timeline.Invalidate();
            _hierarchyPanel.RefreshScene();
            UpdateInspector();
            return;
        }

        if (context is not SceneDefinition sceneDefinition || sceneDefinition.FindLayer(track.TargetId) is not { } sceneLayer) return;
        if (!DrawingObjectNameDialog.TryAsk(this, "Rename Layer", "Layer name", sceneLayer.Name, out var sceneName)) return;
        var timelineSnapshot = sceneDefinition.Timeline.CreateSnapshot();
        var layerSnapshot = sceneDefinition.CreateLayerSnapshot();
        if (!_project.TryRenameSceneLayer(sceneDefinition.Id, sceneLayer.Id, sceneName)) return;
        PushSceneTimelineUndo(sceneDefinition, timelineSnapshot, layerSnapshot);
        _timeline.Invalidate();
        UpdateInspector();
    }

    private void ToggleTimelineLayerLock()
    {
        var context = _timeline.Context;
        var drawingScene = context switch
        {
            VectorScene vectorScene => vectorScene,
            DrawingObjectDefinition drawingObject => drawingObject.Scene,
            _ => null
        };
        if (drawingScene is null) return;
        var layers = SelectedTimelineLayerTargetIds()
            .Select(targetId => Array.IndexOf(drawingScene.LayerIds, targetId))
            .Where(layer => layer >= 0)
            .Distinct()
            .ToArray();
        if (layers.Length == 0) return;
        var activeTargetId = context.Timeline.FindTrack(_timeline.ActiveTrackId ?? "")?.TargetId;
        var activeLayer = string.IsNullOrWhiteSpace(activeTargetId)
            ? -1
            : Array.IndexOf(drawingScene.LayerIds, activeTargetId);
        var lockLayers = !drawingScene.LayerLocked[layers.Contains(activeLayer) ? activeLayer : layers[0]];

        var snapshot = drawingScene.CreateSnapshot();
        var changed = false;
        foreach (var layer in layers) changed |= drawingScene.SetLayerLocked(layer, lockLayers);
        if (!changed) return;
        PushUndoSnapshot(snapshot);
        ClearInactiveSelection();
        _timeline.Invalidate();
        if (context is DrawingObjectDefinition) RebuildDrawingObjectUnderlay();
        else RebuildOnionSkinPreview();
        _hierarchyPanel.RefreshScene();
        UpdateInspector();
        RefreshToolButtons();
        _stage.Invalidate();
    }

    private void ToggleAllTimelineLayerVisibility()
    {
        var context = _timeline.Context;
        var drawingScene = context switch
        {
            VectorScene vectorScene => vectorScene,
            DrawingObjectDefinition drawingObject => drawingObject.Scene,
            _ => null
        };
        if (drawingScene is not null)
        {
            var layers = Enumerable.Range(0, drawingScene.LayerCount).ToArray();
            if (layers.Length == 0) return;

            var drawingObject = context as DrawingObjectDefinition;
            var showLayers = layers.Any(layer => !drawingScene.LayerVisible[layer])
                || drawingObject?.Instances.Any(instance => !instance.Visible) == true;
            var snapshot = drawingScene.CreateSnapshot();
            var instanceSnapshot = drawingObject?.CreateInstanceSnapshot();
            var changed = false;
            foreach (var layer in layers) changed |= drawingScene.SetLayerVisible(layer, showLayers);
            if (drawingObject is not null)
            {
                foreach (var instance in drawingObject.Instances)
                {
                    if (instance.Visible == showLayers) continue;
                    instance.Visible = showLayers;
                    changed = true;
                }
            }

            if (!changed) return;
            PushUndoSnapshot(snapshot, drawingObject, instanceSnapshot);
            ClearInactiveSelection();
            RefreshTimelineLayerDisplay(context);
            _hierarchyPanel.RefreshScene();
            UpdateInspector();
            return;
        }

        if (context is not SceneDefinition sceneDefinition || sceneDefinition.Layers.Count == 0) return;
        var showSceneLayers = sceneDefinition.Layers.Any(layer => !layer.Visible);
        var timelineSnapshot = sceneDefinition.Timeline.CreateSnapshot();
        var layerSnapshot = sceneDefinition.CreateLayerSnapshot();
        var sceneChanged = false;
        foreach (var layer in sceneDefinition.Layers)
        {
            sceneChanged |= sceneDefinition.SetLayerVisible(layer.Id, showSceneLayers);
        }

        if (!sceneChanged) return;
        PushSceneTimelineUndo(sceneDefinition, timelineSnapshot, layerSnapshot);
        ClearInactiveSelection();
        RefreshTimelineLayerDisplay(context);
        UpdateInspector();
    }

    private void ToggleAllTimelineLayerLocks()
    {
        var context = _timeline.Context;
        var drawingScene = context switch
        {
            VectorScene vectorScene => vectorScene,
            DrawingObjectDefinition drawingObject => drawingObject.Scene,
            _ => null
        };
        if (drawingScene is null || drawingScene.LayerCount == 0) return;

        var layers = Enumerable.Range(0, drawingScene.LayerCount).ToArray();
        var lockLayers = layers.Any(layer => !drawingScene.LayerLocked[layer]);
        var snapshot = drawingScene.CreateSnapshot();
        var changed = false;
        foreach (var layer in layers) changed |= drawingScene.SetLayerLocked(layer, lockLayers);
        if (!changed) return;

        PushUndoSnapshot(snapshot);
        ClearInactiveSelection();
        _timeline.Invalidate();
        if (context is DrawingObjectDefinition) RebuildDrawingObjectUnderlay();
        else RebuildOnionSkinPreview();
        _hierarchyPanel.RefreshScene();
        UpdateInspector();
        RefreshToolButtons();
        _stage.Invalidate();
    }

    private void ToggleTimelineLayerOutline()
    {
        SetTimelineLayerOutline(SelectedTimelineLayerTargetIds(), outline: null);
    }

    private void ToggleAllTimelineLayerOutlines()
    {
        var context = _timeline.Context;
        var targetIds = context switch
        {
            VectorScene vectorScene => vectorScene.LayerIds,
            DrawingObjectDefinition drawingObject => drawingObject.Scene.LayerIds,
            SceneDefinition sceneDefinition => sceneDefinition.Layers.Select(layer => layer.Id).ToArray(),
            _ => []
        };
        SetTimelineLayerOutline(targetIds, outline: null);
    }

    private void SetTimelineLayerOutline(IReadOnlyList<string> targetIds, bool? outline)
    {
        if (targetIds.Count == 0) return;
        var context = _timeline.Context;
        var drawingScene = context switch
        {
            VectorScene vectorScene => vectorScene,
            DrawingObjectDefinition drawingObject => drawingObject.Scene,
            _ => null
        };
        if (drawingScene is not null)
        {
            var layers = targetIds
                .Select(targetId => Array.IndexOf(drawingScene.LayerIds, targetId))
                .Where(layer => layer >= 0)
                .Distinct()
                .ToArray();
            if (layers.Length == 0) return;
            var activeTargetId = context.Timeline.FindTrack(_timeline.ActiveTrackId ?? "")?.TargetId;
            var activeLayer = string.IsNullOrWhiteSpace(activeTargetId)
                ? layers[0]
                : Array.IndexOf(drawingScene.LayerIds, activeTargetId);
            if (!layers.Contains(activeLayer)) activeLayer = layers[0];
            var next = outline ?? !drawingScene.LayerOutline[activeLayer];
            if (targetIds.Count == drawingScene.LayerCount) next = outline ?? layers.Any(layer => !drawingScene.LayerOutline[layer]);

            var snapshot = drawingScene.CreateSnapshot();
            var changed = false;
            foreach (var layer in layers) changed |= drawingScene.SetLayerOutline(layer, next);
            if (!changed) return;
            PushUndoSnapshot(snapshot);
            RefreshTimelineLayerDisplay(context);
            return;
        }

        if (context is not SceneDefinition sceneDefinition) return;
        var layerIds = targetIds
            .Where(targetId => sceneDefinition.FindLayer(targetId) is not null)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (layerIds.Length == 0) return;
        var activeSceneTargetId = context.Timeline.FindTrack(_timeline.ActiveTrackId ?? "")?.TargetId;
        var activeSceneLayer = !string.IsNullOrWhiteSpace(activeSceneTargetId)
            ? sceneDefinition.FindLayer(activeSceneTargetId)
            : null;
        activeSceneLayer ??= sceneDefinition.FindLayer(layerIds[0]);
        var sceneNext = outline ?? !(activeSceneLayer?.Outline == true);
        if (layerIds.Length == sceneDefinition.Layers.Count)
        {
            sceneNext = outline ?? layerIds.Any(layerId => sceneDefinition.FindLayer(layerId)?.Outline != true);
        }

        var timelineSnapshot = sceneDefinition.Timeline.CreateSnapshot();
        var layerSnapshot = sceneDefinition.CreateLayerSnapshot();
        var sceneChanged = false;
        foreach (var layerId in layerIds) sceneChanged |= sceneDefinition.SetLayerOutline(layerId, sceneNext);
        if (!sceneChanged) return;
        PushSceneTimelineUndo(sceneDefinition, timelineSnapshot, layerSnapshot);
        RefreshTimelineLayerDisplay(context);
    }

    private void ToggleTimelineOnionSkin()
    {
        var drawingScene = _timeline.Context switch
        {
            VectorScene vectorScene => vectorScene,
            DrawingObjectDefinition drawingObject => drawingObject.Scene,
            _ => null
        };
        if (drawingScene is null) return;
        var snapshot = drawingScene.CreateSnapshot();
        if (!drawingScene.ToggleOnionSkin()) return;
        PushUndoSnapshot(snapshot);
        RebuildOnionSkinPreview();
        _timeline.RefreshOnionSkinControls();
        _stage.Invalidate();
    }

    private void SetTimelineOnionSkinRange(int previousFrames, int nextFrames)
    {
        var drawingScene = _timeline.Context switch
        {
            VectorScene vectorScene => vectorScene,
            DrawingObjectDefinition drawingObject => drawingObject.Scene,
            _ => null
        };
        if (drawingScene is null) return;

        var enableOnionSkin = !drawingScene.OnionSkinEnabled
            && (previousFrames > 0 || nextFrames > 0);
        var session = _onionSkinRangeEditSession;
        var snapshot = session is null || !ReferenceEquals(session.Scene, drawingScene)
            ? drawingScene.CreateSnapshot()
            : null;
        var rangeChanged = drawingScene.SetOnionSkinRange(previousFrames, nextFrames);
        if (!rangeChanged && !enableOnionSkin) return;
        if (enableOnionSkin) drawingScene.SetOnionSkinEnabled(true);
        if (session is not null && ReferenceEquals(session.Scene, drawingScene)) session.Changed = true;
        else if (snapshot is not null) PushUndoSnapshot(snapshot);
        RebuildOnionSkinPreview();
        _timeline.RefreshOnionSkinControls();
        _stage.Invalidate();
    }

    private void BeginTimelineOnionSkinRangeEdit()
    {
        if (_onionSkinRangeEditSession is not null) return;
        var drawingScene = _timeline.Context switch
        {
            VectorScene vectorScene => vectorScene,
            DrawingObjectDefinition drawingObject => drawingObject.Scene,
            _ => null
        };
        if (drawingScene is null) return;

        _onionSkinRangeEditSession = new OnionSkinRangeEditSession
        {
            Scene = drawingScene,
            Snapshot = drawingScene.CreateSnapshot()
        };
    }

    private void CompleteTimelineOnionSkinRangeEdit()
    {
        var session = _onionSkinRangeEditSession;
        _onionSkinRangeEditSession = null;
        if (session is not null && session.Changed) PushUndoSnapshot(session.Snapshot);
    }

    private void CancelTimelineOnionSkinRangeEdit()
    {
        var session = _onionSkinRangeEditSession;
        _onionSkinRangeEditSession = null;
        if (session is null || !session.Changed) return;

        session.Scene.RestoreSnapshot(session.Snapshot);
        RebuildOnionSkinPreview();
        _timeline.RefreshOnionSkinControls();
        _stage.Invalidate();
    }

    private static int TrackIndex(AnimationTimeline timeline, string? trackId)
    {
        if (string.IsNullOrWhiteSpace(trackId)) return -1;
        for (var index = 0; index < timeline.Tracks.Count; index++)
        {
            if (string.Equals(timeline.Tracks[index].Id, trackId, StringComparison.Ordinal)) return index;
        }

        return -1;
    }

    private static void EnsureTimelineFrameExists(AnimationTimeline timeline, int frame)
    {
        if (frame < 0) return;
        using var batchUpdate = timeline.BeginBatchUpdate();
        foreach (var track in timeline.Tracks)
        {
            if (track.Duration <= frame) timeline.SetTrackDuration(track.Id, frame + 1);
        }
    }

    internal static void EnsureTimelineCellFramesExist(
        AnimationTimeline timeline,
        IEnumerable<TimelineFrameCell> cells,
        int trailingFrames = 0)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(cells);
        trailingFrames = Math.Max(0, trailingFrames);
        using var batchUpdate = timeline.BeginBatchUpdate();
        foreach (var group in cells
                     .Where(cell => cell.Frame >= 0)
                     .GroupBy(cell => cell.TrackId, StringComparer.Ordinal))
        {
            var track = timeline.FindTrack(group.Key);
            if (track is null) continue;
            var lastFrame = group.Max(cell => cell.Frame);
            var requiredDuration = lastFrame >= int.MaxValue - trailingFrames
                ? int.MaxValue
                : lastFrame + trailingFrames + 1;
            if (track.Duration < requiredDuration) timeline.SetTrackDuration(track.Id, requiredDuration);
        }
    }

    private bool EnsureTimelineCellFramesExistForContext(
        ITimelineContext context,
        AnimationTimeline timeline,
        IEnumerable<TimelineFrameCell> cells,
        int trailingFrames = 0)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(cells);
        trailingFrames = Math.Max(0, trailingFrames);
        var maskChanged = false;
        using var batchUpdate = timeline.BeginBatchUpdate();
        foreach (var group in cells
                     .Where(cell => cell.Frame >= 0)
                     .GroupBy(cell => cell.TrackId, StringComparer.Ordinal))
        {
            var track = timeline.FindTrack(group.Key);
            if (track is null) continue;
            var lastFrame = group.Max(cell => cell.Frame);
            var requiredDuration = lastFrame >= int.MaxValue - trailingFrames
                ? int.MaxValue
                : lastFrame + trailingFrames + 1;
            if (track.Duration >= requiredDuration) continue;

            if (context is SceneDefinition sceneDefinition
                && IsSceneMaskTrack(sceneDefinition, track))
            {
                maskChanged |= _project.TryInsertSceneMaskTimelineFrame(
                    sceneDefinition.Id,
                    track.TargetId,
                    track.Duration,
                    requiredDuration - track.Duration);
                continue;
            }

            timeline.SetTrackDuration(track.Id, requiredDuration);
        }

        return maskChanged;
    }

    private void AddTimelineLayer()
    {
        var context = _timeline.Context;
        var drawingScene = context switch
        {
            VectorScene vectorScene => vectorScene,
            DrawingObjectDefinition drawingObject => drawingObject.Scene,
            _ => null
        };

        if (drawingScene is not null)
        {
            var snapshot = drawingScene.CreateSnapshot();
            drawingScene.AddLayer();
            PushUndoSnapshot(snapshot);
            _timeline.RefreshTimeline();
            _timeline.SelectModelActiveTrack();
            if (context is DrawingObjectDefinition) RebuildDrawingObjectUnderlay();
            _hierarchyPanel.RefreshScene();
            UpdateInspector();
            _stage.Invalidate();
            return;
        }

        if (context is not SceneDefinition sceneDefinition) return;
        var timelineSnapshot = sceneDefinition.Timeline.CreateSnapshot();
        var layerSnapshot = sceneDefinition.CreateLayerSnapshot();
        if (!_project.TryAddSceneLayer(sceneDefinition.Id, out _)) return;

        PushSceneTimelineUndo(sceneDefinition, timelineSnapshot, layerSnapshot);
        _timeline.RefreshTimeline();
        _timeline.SelectModelActiveTrack();
        RebuildSceneComposition();
        UpdateInspector();
        _stage.Invalidate();
    }

    private void AddTimelineFolderLayer()
    {
        var context = _timeline.Context;
        var drawingScene = context switch
        {
            VectorScene vectorScene => vectorScene,
            DrawingObjectDefinition drawingObject => drawingObject.Scene,
            _ => null
        };
        if (drawingScene is null) return;

        var snapshot = drawingScene.CreateSnapshot();
        drawingScene.AddFolderLayer();
        PushUndoSnapshot(snapshot);
        _timeline.RefreshTimeline();
        _timeline.SelectModelActiveTrack();
        if (context is DrawingObjectDefinition) RebuildDrawingObjectUnderlay();
        _hierarchyPanel.RefreshScene();
        UpdateInspector();
        _stage.Invalidate();
    }

    private void AddTimelineMaskLayer()
    {
        var context = _timeline.Context;
        var drawingScene = context switch
        {
            VectorScene vectorScene => vectorScene,
            DrawingObjectDefinition drawingObject => drawingObject.Scene,
            _ => null
        };
        if (drawingScene is not null)
        {
            var snapshot = drawingScene.CreateSnapshot();
            drawingScene.AddMaskLayer();
            PushUndoSnapshot(snapshot);
            _timeline.RefreshTimeline();
            _timeline.SelectModelActiveTrack();
            if (context is DrawingObjectDefinition) RebuildDrawingObjectUnderlay();
            _hierarchyPanel.RefreshScene();
            UpdateInspector();
            _stage.Invalidate();
            return;
        }

        if (context is not SceneDefinition sceneDefinition
            || sceneDefinition.FindLayer(sceneDefinition.ActiveLayerId) is not
            {
                Kind: SceneLayerKind.Content,
                MaskLayerId: ""
            } contentLayer)
        {
            return;
        }

        var timelineSnapshot = sceneDefinition.Timeline.CreateSnapshot();
        var layerSnapshot = sceneDefinition.CreateLayerSnapshot();
        var timelineSelection = _timeline.CaptureSelectionSnapshot();
        if (!_project.TryAddSceneMaskLayer(
                sceneDefinition.Id,
                contentLayer.Id,
                out var maskLayer)
            || maskLayer?.MaskScene is not { } maskScene)
        {
            return;
        }

        PushSceneTimelineUndo(
            sceneDefinition,
            timelineSnapshot,
            layerSnapshot,
            playheadFrame: _frame,
            timelineSelection: timelineSelection);
        _timeline.RefreshTimeline();
        _timeline.SelectModelActiveTrack();
        if (!ReferenceEquals(_scene, maskScene)) HandleSceneLayerEditingContextChanged();
        _hierarchyPanel.RefreshScene();
        UpdateInspector();
        _stage.Invalidate();
    }

    private enum TimelineEditKind
    {
        InsertFrame,
        RemoveFrame,
        InsertKeyframe,
        InsertBlankKeyframe,
        ClearKeyframe
    }

    private bool ActivateDrawingShortcut(ToolMode tool)
    {
        if (tool == ToolMode.Transform && IsScene3DView()) tool = ToolMode.Transform3D;
        if (!CanActivateTool(tool)) return false;
        ActivateTool(tool);
        return true;
    }

    private bool TryActivateConfiguredToolShortcut(Keys keyData)
    {
        var profile = ShortcutProfiles.ResolveProfile(
            _applicationSettings.ActiveShortcutProfileId,
            _applicationSettings.CustomShortcutProfiles);
        if (ToolShortcutMap.IsVaultShortcut(profile, keyData))
        {
            ToggleVaultDrawer();
            return true;
        }

        var tool = ToolShortcutMap.ResolveTool(
            profile,
            keyData,
            _selectionToolGroup.ActiveTool,
            _activeShapeTool,
            _activeLineTool,
            _activeBrushTool,
            _paintToolGroup.ActiveTool);
        return tool is { } selected && ActivateDrawingShortcut(selected);
    }

    private bool AdjustFreehandWidth(bool increase)
    {
        if (!IsFreehandTool(_tool)) return false;
        var current = Math.Max(VectorUnits.MinimumStrokePoints, _materialEditor.StrokeWidth);
        var next = increase ? current * 1.25f : current / 1.25f;
        _materialEditor.StrokeWidth = Math.Clamp(next, VectorUnits.MinimumStrokePoints, 32f);
        return true;
    }

    private bool HandleBlender3DShortcut(Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        var control = (keyData & Keys.Control) == Keys.Control;
        switch (key)
        {
            case Keys.NumPad1:
                _stage.SetReferenceCameraOrientation(
                    control ? MathF.PI : 0,
                    0,
                    ReferenceCameraMotion.Animated);
                UpdateStatusBar();
                return true;
            case Keys.NumPad3:
                _stage.SetReferenceCameraOrientation(
                    control ? -MathF.PI / 2f : MathF.PI / 2f,
                    0,
                    ReferenceCameraMotion.Animated);
                UpdateStatusBar();
                return true;
            case Keys.NumPad7:
                _stage.SetReferenceCameraOrientation(
                    0,
                    control ? -1.5f : 1.5f,
                    ReferenceCameraMotion.Animated);
                UpdateStatusBar();
                return true;
            case Keys.NumPad5:
                ToggleActiveSceneProjection();
                return true;
            case Keys.Home:
                _stage.ResetReferenceCameraView(ReferenceCameraMotion.Animated);
                UpdateStatusBar();
                return true;
            default:
                return false;
        }
    }

    private void ToggleActiveSceneProjection()
    {
        if (_workspaceTabs.SelectedView != WorkspaceView.SceneEditor || !IsSceneCompositionContext()) return;
        var scene = ActiveScene();
        if (scene is null) return;
        scene.Camera.Projection = scene.Camera.Projection == CameraProjection.Perspective
            ? CameraProjection.Orthographic
            : CameraProjection.Perspective;
        if (ActiveSceneViewDimension() == SceneDimension.TwoD
            && scene.Camera.Projection == CameraProjection.Perspective)
        {
            scene.Camera.Depth = Math.Max(scene.Camera.Depth, 1000);
        }
        MarkProjectDirty();
        _stage.ConfigureReferenceView(
            scene,
            ActiveSceneViewDimension(),
            ReferenceCameraMotion.Animated);
        _sceneEditorPanel.BindProject(_scenes, _activeSceneIndex, _drawingObjects);
        UpdateSceneDimensionButton();
        UpdateStatusBar();
    }

    private bool CycleActiveToolGroup(bool reverse)
    {
        if (_selectionToolGroup.Contains(_tool)) return CycleToolPairGroup(_selectionToolGroup, reverse);
        if (_paintToolGroup.Contains(_tool)) return CycleToolPairGroup(_paintToolGroup, reverse);
        if (IsSceneCompositionContext()) return false;

        if (IsLineTool(_tool))
        {
            var index = Array.IndexOf(_lineTools, _activeLineTool);
            if (index < 0) index = 0;
            var next = reverse
                ? (index - 1 + _lineTools.Length) % _lineTools.Length
                : (index + 1) % _lineTools.Length;
            ActivateTool(_lineTools[next]);
            ShowLineToolFlyout();
            ScheduleLineToolFlyoutHideAfter(2000);
            return true;
        }

        if (IsBrushTool(_tool))
        {
            var index = Array.IndexOf(_brushTools, _activeBrushTool);
            if (index < 0) index = 0;
            var next = reverse
                ? (index - 1 + _brushTools.Length) % _brushTools.Length
                : (index + 1) % _brushTools.Length;
            ActivateTool(_brushTools[next]);
            ShowBrushToolFlyout();
            ScheduleBrushToolFlyoutHideAfter(2000);
            return true;
        }

        var shapeIndex = Array.IndexOf(_shapeTools, _activeShapeTool);
        if (shapeIndex < 0) shapeIndex = 0;
        var shapeNext = reverse
            ? (shapeIndex - 1 + _shapeTools.Length) % _shapeTools.Length
            : (shapeIndex + 1) % _shapeTools.Length;
        ActivateTool(_shapeTools[shapeNext]);
        ShowShapeToolFlyout();
        ScheduleShapeToolFlyoutHideAfter(2000);
        return true;
    }

    private bool CycleToolPairGroup(ToolPairGroup group, bool reverse)
    {
        var availableTools = group.Tools.Where(CanActivateTool).ToArray();
        if (availableTools.Length == 0) return false;
        var next = CycleToolGroupMember(availableTools, group.ActiveTool, reverse);
        ActivateTool(next);
        ShowToolPairFlyout(group);
        ScheduleToolPairFlyoutHideAfter(group, 2000);
        return true;
    }

    internal static ToolMode CycleToolGroupMember(IReadOnlyList<ToolMode> tools, ToolMode activeTool, bool reverse)
    {
        if (tools.Count == 0) return activeTool;
        var index = -1;
        for (var candidate = 0; candidate < tools.Count; candidate++)
        {
            if (tools[candidate] != activeTool) continue;
            index = candidate;
            break;
        }
        if (index < 0) index = 0;
        return reverse
            ? tools[(index - 1 + tools.Count) % tools.Count]
            : tools[(index + 1) % tools.Count];
    }

    private void SyncTimelineFrameRange()
    {
        _syncingFrame = true;
        try
        {
            _timeline.StartFrame = _playbackSettings.StartFrame;
            _timeline.EndFrame = _playbackSettings.EndFrame;
        }
        finally
        {
            _syncingFrame = false;
        }
    }

    private void ApplyProjectPlaybackSettingsToCurrentContext()
    {
        var timelineLastFrame = Math.Max(0, _timeline.Context.FrameCount - 1);
        var endFrame = Math.Clamp(_project.PlaybackEndFrame, 0, timelineLastFrame);
        var startFrame = Math.Clamp(_project.PlaybackStartFrame, 0, endFrame);

        _syncingProjectPlaybackSettings = true;
        try
        {
            _playbackSettings.Fps = _project.PlaybackFps;
            _playbackSettings.LoopPlayback = _project.LoopPlayback;
            _playbackSettings.SetFrameRange(startFrame, endFrame, notifyChanged: false);
        }
        finally
        {
            _syncingProjectPlaybackSettings = false;
        }
    }

    private void PersistProjectPlaybackFps()
    {
        if (_syncingProjectPlaybackSettings) return;
        _project.TrySetPlaybackFps(_playbackSettings.Fps);
    }

    private void PersistProjectLoopPlayback()
    {
        if (_syncingProjectPlaybackSettings) return;
        _project.TrySetLoopPlayback(_playbackSettings.LoopPlayback);
    }

    private void PersistProjectPlaybackRange()
    {
        if (_syncingProjectPlaybackSettings) return;
        _project.TrySetPlaybackRange(_playbackSettings.StartFrame, _playbackSettings.EndFrame);
    }

    private bool ApplyBoundTimelineDuration(
        int previousLastFrame = -1,
        bool refreshClampedFrame = true)
    {
        var lastFrame = Math.Max(0, _timeline.Context.FrameCount - 1);
        var followTimelineEnd = previousLastFrame >= 0 && _project.PlaybackEndFrame == previousLastFrame;
        var endFrame = followTimelineEnd
            ? lastFrame
            : Math.Clamp(_project.PlaybackEndFrame, 0, lastFrame);
        var startFrame = Math.Clamp(_project.PlaybackStartFrame, 0, endFrame);
        if (startFrame != _playbackSettings.StartFrame || endFrame != _playbackSettings.EndFrame)
        {
            _playbackSettings.SetFrameRange(startFrame, endFrame, notifyChanged: false);
            if (followTimelineEnd) PersistProjectPlaybackRange();
        }

        SyncTimelineFrameRange();
        var clampedFrame = Math.Clamp(_frame, startFrame, endFrame);
        if (!refreshClampedFrame || clampedFrame == _frame) return false;
        return SetFrame(clampedFrame);
    }

    private void BeginMaterialContinuousEdit()
    {
        if (_materialEditSession is not null || IsSceneCompositionContext() || IsTextEditActive()) return;
        _fillMergePendingAfterMaterialEdit = false;
        _lineMergePendingAfterMaterialEdit = false;
        _materialEditSession = new MaterialEditSession
        {
            Scene = _scene,
            Snapshot = _scene.CreateSnapshot(),
            SelectedObjects = _selectedObjects.ToArray(),
            SelectedElements = _selectedElements.ToArray(),
            PrimaryElement = _selectedElement
        };
    }

    private void CompleteMaterialContinuousEdit()
    {
        var session = _materialEditSession;
        var mergeFills = _fillMergePendingAfterMaterialEdit;
        var mergeLines = _lineMergePendingAfterMaterialEdit;
        _fillMergePendingAfterMaterialEdit = false;
        _lineMergePendingAfterMaterialEdit = false;
        _materialEditSession = null;
        if (session is { AutoKeyframeChanged: true, UndoPushed: false })
        {
            RestoreCanvasMutationSnapshot(session.Snapshot);
            return;
        }
        var hierarchyChanged = session?.HierarchyDirty == true;
        var inspectorChanged = session?.InspectorDirty == true;
        if (mergeFills) hierarchyChanged |= MergeSelectedFillsAfterGeometryEdit(connectNearby: true);
        if (mergeLines) hierarchyChanged |= MergeCompatibleLinesAfterDrawingOperation();
        if (hierarchyChanged) _hierarchyPanel.RefreshScene();
        if (hierarchyChanged || inspectorChanged) UpdateInspector();
        if (hierarchyChanged || inspectorChanged) _stage.Invalidate();
    }

    private void CancelMaterialContinuousEdit()
    {
        var session = _materialEditSession;
        _fillMergePendingAfterMaterialEdit = false;
        _lineMergePendingAfterMaterialEdit = false;
        _materialEditSession = null;
        if (session is null || !ReferenceEquals(session.Scene, _scene)) return;

        if (session.UndoPushed
            && _undoStack.TryPeek(out var undo)
            && ReferenceEquals(undo.Snapshot, session.Snapshot))
        {
            _undoStack.Pop();
        }

        _scene.RestoreSnapshot(session.Snapshot);
        if (session.SelectedElements.Length > 0)
        {
            SetSelection(session.SelectedElements, session.PrimaryElement);
        }
        else if (session.SelectedObjects.Length > 0)
        {
            SetSelection(session.SelectedObjects);
        }
        else
        {
            ClearSelection();
        }

        _hierarchyPanel.RefreshScene();
        UpdateInspector();
        _stage.Invalidate();
    }

    private void PushMaterialUndoSnapshot(VectorSceneSnapshot fallbackSnapshot)
    {
        var session = _materialEditSession;
        if (session is null || !ReferenceEquals(session.Scene, _scene))
        {
            PushUndoSnapshot(fallbackSnapshot);
            return;
        }

        if (session.UndoPushed) return;
        PushUndoSnapshot(session.Snapshot);
        session.UndoPushed = true;
    }

    private VectorSceneSnapshot CreateMaterialUndoSnapshot()
    {
        var session = _materialEditSession;
        if (session is not null && ReferenceEquals(session.Scene, _scene))
        {
            if (!session.AutoKeyframePreparationAttempted)
            {
                session.AutoKeyframePreparationAttempted = true;
                session.AutoKeyframeChanged = MaterializeAutomaticKeyframesForCanvasEdit();
            }
            return session.Snapshot;
        }

        var snapshot = _scene.CreateSnapshot();
        MaterializeAutomaticKeyframesForCanvasEdit();
        return snapshot;
    }

    private void RefreshHierarchyAfterMaterialChange(bool hierarchyChanged)
    {
        if (!hierarchyChanged) return;
        var session = _materialEditSession;
        if (session is not null && ReferenceEquals(session.Scene, _scene))
        {
            session.HierarchyDirty = true;
            return;
        }

        _hierarchyPanel.RefreshScene();
    }

    private void RefreshInspectorAfterMaterialChange()
    {
        _scene.RefreshTimelineTweenMaterializationsAtEndpointFrame(_frame);
        var session = _materialEditSession;
        if (session is not null && ReferenceEquals(session.Scene, _scene))
        {
            session.InspectorDirty = true;
            return;
        }

        UpdateInspector();
    }

    private void CaptureUndoSnapshot(
        IEnumerable<int>? affectedObjects = null,
        IEnumerable<int>? affectedLayers = null)
    {
        var snapshot = _scene.CreateSnapshot();
        PushUndoSnapshot(snapshot);
        MaterializeAutomaticKeyframesForCanvasEdit(affectedObjects, affectedLayers);
    }

    private VectorSceneSnapshot CreateCanvasMutationSnapshot(
        IEnumerable<int>? affectedObjects = null,
        IEnumerable<int>? affectedLayers = null)
    {
        var snapshot = _scene.CreateSnapshot();
        MaterializeAutomaticKeyframesForCanvasEdit(affectedObjects, affectedLayers);
        return snapshot;
    }

    private VectorSceneSnapshot CreateCanvasGeometryMutationSnapshot(IEnumerable<int> affectedObjects)
    {
        var targets = affectedObjects.Distinct().ToArray();
        if (AutomaticKeyframesEnabledForCurrentScene())
        {
            return CreateCanvasMutationSnapshot(targets);
        }

        var snapshot = _scene.CreateWholeObjectTranslationSnapshot();
        snapshot.DetachSharedGeometry(targets);
        return snapshot;
    }

    private bool MaterializeAutomaticKeyframesForCanvasEdit(
        IEnumerable<int>? affectedObjects = null,
        IEnumerable<int>? affectedLayers = null)
    {
        if (!AutomaticKeyframesEnabledForCurrentScene()) return false;
        var previousLastFrame = Math.Max(0, _timeline.Context.FrameCount - 1);
        var layers = ResolveAutomaticKeyframeLayers(affectedObjects, affectedLayers);
        if (layers.Count == 0) return false;

        var changed = false;
        using (_scene.Timeline.BeginBatchUpdate())
        {
            foreach (var layer in layers)
            {
                changed |= _scene.MaterializeAutoKeyframeInPlace(layer, _frame);
            }
        }
        if (!changed) return false;

        RefreshAutomaticKeyframePresentation(previousLastFrame);
        return true;
    }

    private bool AutomaticKeyframesEnabledForCurrentScene()
    {
        if (!_timeline.AutoKeyframeEnabled || IsSceneCompositionContext()) return false;
        if (IsSceneMaskEditing())
        {
            return ReferenceEquals(ActiveScene()?.ActiveMaskScene(), _scene);
        }
        var drawingScene = _timeline.Context switch
        {
            VectorScene vectorScene => vectorScene,
            DrawingObjectDefinition drawingObject => drawingObject.Scene,
            _ => null
        };
        return ReferenceEquals(drawingScene, _scene);
    }

    private IReadOnlyList<int> ResolveAutomaticKeyframeLayers(
        IEnumerable<int>? affectedObjects,
        IEnumerable<int>? affectedLayers)
    {
        var layers = new HashSet<int>();
        if (affectedLayers is not null)
        {
            foreach (var layer in affectedLayers)
            {
                if ((uint)layer < _scene.LayerCount) layers.Add(layer);
            }
        }

        var objects = affectedObjects?.ToArray()
            ?? _selectedObjects
                .Concat(_selectedElements.Select(hit => hit.Key.ObjectIndex))
                .Append(_selectedObject)
                .ToArray();
        foreach (var objectIndex in objects)
        {
            if ((uint)objectIndex < _scene.ObjectCount) layers.Add(_scene.ObjectLayer[objectIndex]);
        }
        return layers.OrderBy(layer => layer).ToArray();
    }

    private void RefreshAutomaticKeyframePresentation(int previousLastFrame)
    {
        _timeline.RefreshTimeline();
        if (_timeline.Context.FrameCount - 1 != previousLastFrame) ApplyBoundTimelineDuration(previousLastFrame);
        _hierarchyPanel.RefreshScene();
        RebuildDrawingObjectUnderlay();
        _stage.Invalidate();
    }

    private void RestoreCanvasMutationSnapshot(VectorSceneSnapshot snapshot)
    {
        _scene.RestoreSnapshot(snapshot);
        _scene.EditFrame = _frame;
        _timeline.RefreshTimeline();
        _hierarchyPanel.RefreshScene();
        RebuildDrawingObjectUnderlay();
        SyncSelectionToStage();
        UpdateInspector();
        _stage.Invalidate();
    }

    private void PushUndoSnapshot(
        VectorSceneSnapshot snapshot,
        DrawingObjectDefinition? drawingObject = null,
        DrawingObjectInstanceDefinition[]? instanceSnapshot = null,
        int? playheadFrame = null,
        TimelineSelectionSnapshot? timelineSelection = null,
        string? drawingObjectName = null,
        string? createdDrawingObjectId = null)
    {
        MarkProjectDirty();
        _marqueeMaterializationSession = null;
        PruneUndoSequenceEntries();
        var entry = new DrawingUndoEntry(
            snapshot,
            drawingObject,
            instanceSnapshot,
            playheadFrame,
            timelineSelection,
            drawingObjectName,
            createdDrawingObjectId,
            snapshot.EstimateMemoryBytes()
                + ((drawingObjectName?.Length ?? 0) + (createdDrawingObjectId?.Length ?? 0)) * sizeof(char));
        _undoStack.Push(entry);
        RecordUndoSequence(entry);
        var snapshots = _undoStack.ToArray();
        var retainedCount = 0;
        long retainedBytes = 0;
        foreach (var item in snapshots)
        {
            var itemBytes = item.MemoryBytes;
            if (retainedCount > 0
                && (retainedCount >= MaxUndoSnapshots || retainedBytes + itemBytes > MaxUndoSnapshotBytes))
            {
                break;
            }

            retainedBytes += itemBytes;
            retainedCount++;
        }

        if (retainedCount == snapshots.Length) return;
        _undoStack.Clear();
        for (var index = retainedCount - 1; index >= 0; index--) _undoStack.Push(snapshots[index]);
        PruneUndoSequenceEntries();
    }

    private void PushSceneTimelineUndo(
        SceneDefinition scene,
        AnimationTimelineSnapshot snapshot,
        SceneLayerSnapshot? layerSnapshot = null,
        DrawingObjectInstanceDefinition[]? instanceSnapshot = null,
        int? playheadFrame = null,
        TimelineSelectionSnapshot? timelineSelection = null)
    {
        MarkProjectDirty();
        PruneUndoSequenceEntries();
        var entry = new SceneTimelineUndoEntry(
            scene,
            snapshot,
            layerSnapshot,
            instanceSnapshot,
            playheadFrame,
            timelineSelection);
        _sceneTimelineUndoStack.Push(entry);
        RecordUndoSequence(entry);
        while (_sceneTimelineUndoStack.Count > MaxUndoSnapshots)
        {
            var snapshots = _sceneTimelineUndoStack.Take(MaxUndoSnapshots).Reverse().ToArray();
            _sceneTimelineUndoStack.Clear();
            foreach (var item in snapshots) _sceneTimelineUndoStack.Push(item);
        }
        PruneUndoSequenceEntries();
    }

    private void RecordUndoSequence(object entry)
    {
        _undoSequenceByEntry[entry] = ++_nextUndoSequence;
    }

    private long UndoSequence(object entry)
    {
        return _undoSequenceByEntry.GetValueOrDefault(entry);
    }

    private void PruneUndoSequenceEntries()
    {
        if (_undoSequenceByEntry.Count == 0) return;
        var retained = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var entry in _undoStack) retained.Add(entry);
        foreach (var entry in _sceneTimelineUndoStack) retained.Add(entry);
        foreach (var entry in _undoSequenceByEntry.Keys.Where(entry => !retained.Contains(entry)).ToArray())
        {
            _undoSequenceByEntry.Remove(entry);
        }
    }

    private void CapturePointerUndoSnapshot(bool allowSharedGeometry = false)
    {
        if (_undoCapturedForPointerEdit) return;
        var shareGeometry = allowSharedGeometry && !AutomaticKeyframesEnabledForCurrentScene();
        var snapshot = shareGeometry
            ? _scene.CreateWholeObjectTranslationSnapshot()
            : _scene.CreateSnapshot();
        PushUndoSnapshot(snapshot);
        MaterializeAutomaticKeyframesForCanvasEdit();
        _undoCapturedForPointerEdit = true;
        _pointerUndoSnapshot = snapshot;
        _pointerUndoSnapshotWithSharedGeometry = shareGeometry ? snapshot : null;
    }

    private void FinalizePointerUndoSnapshot()
    {
        var snapshot = _pointerUndoSnapshotWithSharedGeometry;
        if (snapshot is null) return;
        _pointerUndoSnapshotWithSharedGeometry = null;
        snapshot.DetachSharedGeometry();
    }

    private bool UndoLastEdit()
    {
        _marqueeMaterializationSession = null;
        PruneUndoSequenceEntries();
        SceneTimelineUndoEntry? timelineUndo = null;
        var canUndoSceneTimeline = IsSceneBuildingContext()
            && _timeline.Context is SceneDefinition activeTimelineScene
            && _sceneTimelineUndoStack.TryPeek(out timelineUndo)
            && ReferenceEquals(timelineUndo.Scene, activeTimelineScene);
        DrawingUndoEntry? drawingUndo = null;
        var canUndoDrawing = !IsSceneCompositionContext()
            && _undoStack.TryPeek(out drawingUndo);
        var undoSceneTimeline = canUndoSceneTimeline
            && (!IsSceneMaskEditing()
                || !canUndoDrawing
                || UndoSequence(timelineUndo!) >= UndoSequence(drawingUndo!));
        if (undoSceneTimeline)
        {
            var sceneUndo = timelineUndo!;
            var activeScene = sceneUndo.Scene;
            var wasSceneMaskEditing = IsSceneMaskEditing();
            var previousLastFrame = Math.Max(0, activeScene.FrameCount - 1);
            _sceneTimelineUndoStack.Pop();
            _undoSequenceByEntry.Remove(sceneUndo);
            if (sceneUndo.LayerSnapshot is not null) activeScene.RestoreLayerSnapshot(sceneUndo.LayerSnapshot);
            if (sceneUndo.InstanceSnapshot is not null) activeScene.RestoreInstanceSnapshot(sceneUndo.InstanceSnapshot);
            activeScene.Timeline.RestoreSnapshot(sceneUndo.Snapshot);
            activeScene.SynchronizeTimelineTracks();
            if (sceneUndo.LayerSnapshot is not null
                && (wasSceneMaskEditing || IsSceneMaskEditing()))
            {
                var retainedCanvasUndo = _undoStack.ToArray();
                HandleSceneLayerEditingContextChanged();
                for (var index = retainedCanvasUndo.Length - 1; index >= 0; index--)
                {
                    _undoStack.Push(retainedCanvasUndo[index]);
                }
            }
            _timeline.RefreshTimeline();
            ApplyBoundTimelineDuration(previousLastFrame, refreshClampedFrame: false);
            SetFrame(sceneUndo.PlayheadFrame ?? _frame);
            ClearSelection();
            if (sceneUndo.TimelineSelection is { } sceneTimelineSelection)
            {
                _timeline.RestoreSelectionSnapshot(sceneTimelineSelection);
            }
            UpdateInspector();
            MarkProjectDirty();
            return true;
        }

        if (IsSceneCompositionContext()) return false;
        if (!canUndoDrawing || drawingUndo is null) return false;
        var previousDrawingLastFrame = Math.Max(0, _scene.FrameCount - 1);
        _undoStack.Pop();
        _undoSequenceByEntry.Remove(drawingUndo);
        var createdDrawingObjectRemoved = drawingUndo.CreatedDrawingObjectId is not null
            && _project.TryRemoveDrawingObject(drawingUndo.CreatedDrawingObjectId, out _);
        if (drawingUndo.DrawingObject is not null && drawingUndo.InstanceSnapshot is not null)
        {
            drawingUndo.DrawingObject.RestoreInstanceSnapshot(drawingUndo.InstanceSnapshot);
        }
        var drawingObjectNameRestored = drawingUndo.DrawingObject is not null
            && drawingUndo.DrawingObjectName is not null
            && _project.TryRenameDrawingObject(
                drawingUndo.DrawingObject.Id,
                drawingUndo.DrawingObjectName);
        _scene.RestoreSnapshot(drawingUndo.Snapshot);
        _scene.EditFrame = _frame;
        SynchronizeActiveSceneMaskTimelineContent(refreshTimeline: false);
        ClearSelection();
        _geometryDirty = false;
        CancelTraditionalPenPath();
        CancelPenCurve();
        CancelFreehandStroke();
        _stage.ClearDrawingPreview();
        _stage.ClearMarquee();
        _hierarchyPanel.RefreshScene();
        _timeline.RefreshTimeline();
        ApplyBoundTimelineDuration(previousDrawingLastFrame, refreshClampedFrame: false);
        SetFrame(drawingUndo.PlayheadFrame ?? _frame);
        if (drawingUndo.TimelineSelection is { } drawingTimelineSelection)
        {
            _timeline.RestoreSelectionSnapshot(drawingTimelineSelection);
        }
        UpdateInspector();
        RefreshTweenCurveInspector();
        if (drawingObjectNameRestored || createdDrawingObjectRemoved) RefreshDrawingObjectAssetPresentation();
        _stage.Invalidate();
        MarkProjectDirty();
        return true;
    }

    private bool CutSelectedObjects()
    {
        return CopySelectedObjects() && DeleteSelectedObject();
    }

    private bool CopySelectedObjects()
    {
        var selectedInstances = SelectedSceneInstances();
        var targets = _selectedObjects
            .Where(index => index >= 0 && index < _scene.ObjectCount && _scene.IsObjectActive(index, _frame))
            .ToArray();
        if (targets.Length == 0
            && _selectedObject >= 0
            && _selectedObject < _scene.ObjectCount
            && _scene.IsObjectActive(_selectedObject, _frame))
        {
            targets = new[] { _selectedObject };
        }
        if (targets.Length == 0 && selectedInstances.Count == 0) return false;

        VectorSceneSnapshot? restoreAfterCopy = null;
        if (targets.Length > 0 && _selectedElements.Count > 0)
        {
            restoreAfterCopy = _scene.CreateSnapshot();
            var materialized = _scene.MaterializeSelectedParts(_selectedElements.Select(hit => hit.Key).ToArray(), _frame);
            if (!materialized.Success)
            {
                _scene.RestoreSnapshot(restoreAfterCopy);
                return false;
            }

            targets = materialized.Parts.Select(part => part.Result.ObjectIndex).Distinct().ToArray();
        }

        try
        {
            _clipboardObjects.Clear();
            _clipboardDrawingObjectInstances.Clear();
            foreach (var instance in selectedInstances)
            {
                _clipboardDrawingObjectInstances.Add(new ClipboardDrawingObjectInstance(
                    instance.DrawingObjectId,
                    instance.SceneLayerId,
                    instance.Name,
                    instance.EvaluateState(0),
                    instance.StateKeyframes.ToArray()));
            }
            foreach (var index in targets)
            {
                PointF[][]? pathContours = null;
                if (_scene.ShapeKind[index] == ShapeKind.Path && _scene.TryGetPathWorldContours(index, out var contours)) pathContours = contours;
                else if (IsFreehandShape(_scene.ShapeKind[index]) && _scene.TryGetFreehandWorldPoints(index, out var freehandPoints)) pathContours = new[] { freehandPoints };
                var freehandBezierNodes = _scene.ShapeKind[index] == ShapeKind.Freeform
                    && _scene.TryGetFreehandBezierWorldNodes(index, out var worldBezierNodes)
                        ? worldBezierNodes
                        : null;
                var mixingRegion = _scene.TryGetMixingBrushWorldRegion(index, out var worldRegion)
                    ? worldRegion
                    : null;
                var mixingSamples = _scene.TryGetMixingStrokeWorldSamples(index, out var worldSamples)
                    ? worldSamples
                    : null;
                _clipboardObjects.Add(new ClipboardObject(
                    _scene.ObjectLayer[index],
                    new PointF(_scene.X[index], _scene.Y[index]),
                    new SizeF(_scene.Width[index], _scene.Height[index]),
                    _scene.Angle[index],
                    _scene.Stroke[index],
                    Color.FromArgb(_scene.Argb[index]),
                    Color.FromArgb(_scene.StrokeArgb[index]),
                    _scene.AtomCount[index],
                    _scene.ShapeKind[index],
                    _scene.GetShapeVertexCount(index),
                    new PointF(_scene.CurveControlX[index], _scene.CurveControlY[index]),
                    new PointF(_scene.CurveControl2X[index], _scene.CurveControl2Y[index]),
                    pathContours,
                    freehandBezierNodes,
                    _scene.GetLineEndpointStyle(index, startEndpoint: true),
                    _scene.GetLineEndpointStyle(index, startEndpoint: false),
                    _scene.TryGetImportedSvgSource(index, out var importedSvgSource) ? importedSvgSource : null,
                    _scene.TryGetImportedSvgName(index, out var importedSvgName) ? importedSvgName : null,
                    _scene.TryGetTextObjectData(index, out var textData) ? textData : null,
                    mixingRegion,
                    mixingSamples));
            }

            return _clipboardObjects.Count > 0 || _clipboardDrawingObjectInstances.Count > 0;
        }
        finally
        {
            if (restoreAfterCopy is not null)
            {
                _scene.RestoreSnapshot(restoreAfterCopy);
                SyncSelectionToStage();
                _stage.Invalidate();
            }
        }
    }

    private bool PasteCopiedObjects()
    {
        if (IsSceneCompositionContext()) return PasteCopiedSceneInstances();
        if (DrawingToolsBlocked()) return false;
        if (_clipboardObjects.Count == 0 && _clipboardDrawingObjectInstances.Count == 0) return false;
        var container = ActiveDrawingObject();
        if (container is null) return false;
        var snapshot = CreateCanvasMutationSnapshot(
            affectedLayers: _clipboardObjects.Select(item =>
                Math.Clamp(item.Layer, 0, Math.Max(0, _scene.LayerCount - 1))));
        var instanceSnapshot = _clipboardDrawingObjectInstances.Count > 0
            ? container.CreateInstanceSnapshot()
            : null;
        var pasted = new List<int>(_clipboardObjects.Count);
        foreach (var item in _clipboardObjects)
        {
            var offset = new PointF(PasteOffsetUnits, PasteOffsetUnits);
            var layer = Math.Clamp(item.Layer, 0, Math.Max(0, _scene.LayerCount - 1));
            int index;
            if (item.Shape == ShapeKind.ImportedSvg && !string.IsNullOrWhiteSpace(item.ImportedSvgSource))
            {
                var center = new PointF(item.Center.X + offset.X, item.Center.Y + offset.Y);
                index = _scene.AddImportedSvgObject(
                    layer,
                    center,
                    item.Size,
                    item.Angle,
                    item.ImportedSvgSource,
                    item.ImportedSvgName);
            }
            else if (item.Shape == ShapeKind.Text && item.TextData is { } textData)
            {
                var center = new PointF(item.Center.X + offset.X, item.Center.Y + offset.Y);
                index = _scene.AppendTextObject(
                    layer,
                    center,
                    item.Size,
                    item.Angle,
                    textData,
                    item.FillColor.ToArgb());
            }
            else if (item.Shape == ShapeKind.Text)
            {
                index = -1;
            }
            else if (item.Shape == ShapeKind.MixingStroke && item.MixingRegion is { } mixingRegion)
            {
                var shiftedVertices = mixingRegion.Vertices
                    .Select(vertex => vertex with
                    {
                        Point = new PointF(vertex.Point.X + offset.X, vertex.Point.Y + offset.Y)
                    })
                    .ToArray();
                index = _scene.AppendMixingBrushRegion(
                    layer,
                    new MixingBrushRegionData(shiftedVertices, mixingRegion.TriangleIndices.ToArray()),
                    item.Atoms);
            }
            else if (item.Shape == ShapeKind.MixingStroke && item.MixingSamples is { Length: > 0 } mixingSamples)
            {
                var shiftedSamples = mixingSamples
                    .Select(sample => sample with
                    {
                        Point = new PointF(sample.Point.X + offset.X, sample.Point.Y + offset.Y)
                    })
                    .ToArray();
                index = _scene.AppendMixingBrushStroke(layer, shiftedSamples, item.Atoms);
            }
            else if (item.Shape == ShapeKind.MixingStroke)
            {
                index = -1;
            }
            else if (item.Shape == ShapeKind.Path && item.PathWorldContours is { Length: > 0 } pathContours)
            {
                var shifted = pathContours
                    .Select(contour => contour.Select(point => new PointF(point.X + offset.X, point.Y + offset.Y)).ToArray())
                    .ToArray();
                index = _scene.AddPathObjectContours(layer, shifted, item.Stroke, item.FillColor, item.StrokeColor, item.Atoms);
            }
            else if (item.Shape == ShapeKind.Freeform
                && item.FreehandBezierWorldNodes is { Length: >= 2 } freehandBezierNodes)
            {
                var shifted = freehandBezierNodes
                    .Select(node => new PathBezierNode(
                        new PointF(node.Anchor.X + offset.X, node.Anchor.Y + offset.Y),
                        new PointF(node.IncomingControl.X + offset.X, node.IncomingControl.Y + offset.Y),
                        new PointF(node.OutgoingControl.X + offset.X, node.OutgoingControl.Y + offset.Y)))
                    .ToArray();
                index = _scene.AddFreehandBezierStroke(
                    layer,
                    shifted,
                    item.Stroke,
                    item.StrokeColor,
                    item.Atoms);
            }
            else if (IsFreehandShape(item.Shape) && item.PathWorldContours is { Length: > 0 } freehandContours)
            {
                var shifted = freehandContours[0]
                    .Select(point => new PointF(point.X + offset.X, point.Y + offset.Y))
                    .ToArray();
                var color = item.Shape == ShapeKind.BrushStroke ? item.FillColor : item.StrokeColor;
                index = _scene.AddFreehandStroke(layer, shifted, item.Stroke, color, item.Shape == ShapeKind.BrushStroke, item.Atoms);
            }
            else
            {
                var center = new PointF(item.Center.X + offset.X, item.Center.Y + offset.Y);
                index = _scene.AddObject(
                    layer,
                    center,
                    item.Size,
                    item.Angle,
                    item.Stroke,
                    item.FillColor,
                    item.StrokeColor,
                    item.Atoms,
                    item.Shape,
                    item.ShapeVertexCount);
                if (item.Shape == ShapeKind.Line)
                {
                    _scene.CurveControlX[index] = item.CurveControl.X + offset.X;
                    _scene.CurveControlY[index] = item.CurveControl.Y + offset.Y;
                    _scene.CurveControl2X[index] = item.CurveControl2.X + offset.X;
                    _scene.CurveControl2Y[index] = item.CurveControl2.Y + offset.Y;
                    _scene.SetLineEndpointStyle(index, startEndpoint: true, item.StartEndpointStyle);
                    _scene.SetLineEndpointStyle(index, startEndpoint: false, item.EndEndpointStyle);
                }
            }

            if (index >= 0) pasted.Add(index);
        }

        var pastedInstances = PasteCopiedDrawingObjectInstances(container);
        if (pasted.Count == 0 && pastedInstances.Count == 0)
        {
            if (instanceSnapshot is not null) container.RestoreInstanceSnapshot(instanceSnapshot);
            RestoreCanvasMutationSnapshot(snapshot);
            return false;
        }
        PushUndoSnapshot(snapshot, container, instanceSnapshot);
        SetMixedSelection(
            pasted,
            pastedInstances,
            pastedInstances.Count > 0 ? pastedInstances[^1] : null);
        if (pasted.Count > 0)
        {
            MergeSelectedFillsAfterGeometryEdit(connectNearby: true);
            MergeCompatibleLinesAfterDrawingOperation();
        }
        if (pastedInstances.Count > 0)
        {
            SetSceneInstanceSelectionCore(
                pastedInstances,
                pastedInstances[^1],
                preserveDrawingSelection: true);
        }
        _hierarchyPanel.RefreshScene();
        _timeline.RefreshTimeline();
        if (pastedInstances.Count > 0 || IsSceneMaskEditing()) RebuildDrawingObjectUnderlay();
        if (pastedInstances.Count > 0) RefreshDrawingObjectAssetPresentation();
        UpdateInspector();
        _stage.Invalidate();
        return true;
    }

    private int[] SelectedActiveDrawingObjectIndices()
    {
        var targets = _selectedObjects
            .Where(index => (uint)index < _scene.ObjectCount && _scene.IsObjectActive(index, _frame))
            .Distinct()
            .ToArray();
        if (targets.Length == 0
            && (uint)_selectedObject < _scene.ObjectCount
            && _scene.IsObjectActive(_selectedObject, _frame))
        {
            targets = [_selectedObject];
        }
        return targets;
    }

    private bool TryPrepareSelectedDrawingObjectsForCommand(
        VectorSceneSnapshot snapshot,
        out int[] targets)
    {
        targets = SelectedActiveDrawingObjectIndices();
        if (targets.Length == 0) return false;
        if (_selectedElements.Count == 0) return true;

        var materialized = _scene.MaterializeSelectedParts(
            _selectedElements.Select(hit => hit.Key).ToArray(),
            _frame);
        if (materialized.Success && materialized.Parts.Length > 0)
        {
            targets = materialized.Parts
                .Select(part => part.Result.ObjectIndex)
                .Distinct()
                .ToArray();
            return targets.Length > 0;
        }

        _scene.RestoreSnapshot(snapshot);
        _scene.EditFrame = _frame;
        SyncSelectionToStage();
        return false;
    }

    private bool FlipSelectedDrawingObjects(bool horizontal)
    {
        if (DrawingToolsBlocked()) return false;
        if (SelectedSceneInstances().Count > 0)
        {
            return FlipSelectedNestedDrawingObjects(horizontal);
        }
        var snapshot = CreateCanvasMutationSnapshot();
        if (!TryPrepareSelectedDrawingObjectsForCommand(snapshot, out var targets)
            || targets.Any(IsWholeObjectOnlyObject)
            || !_scene.FlipObjects(targets, horizontal))
        {
            RestoreCanvasMutationSnapshot(snapshot);
            return false;
        }

        SetSelection(targets);
        ApplySelectedFillOverwriteAfterGeometryEdit();
        MergeSelectedFillsAfterGeometryEdit(connectNearby: true);
        MergeCompatibleLinesAfterDrawingOperation();
        PushUndoSnapshot(snapshot);
        _hierarchyPanel.RefreshScene();
        UpdateInspector();
        _stage.Invalidate();
        return true;
    }

    private List<DrawingObjectInstanceDefinition> PasteCopiedDrawingObjectInstances(
        DrawingObjectDefinition container)
    {
        var offset = new PointF(PasteOffsetUnits, PasteOffsetUnits);
        var pasted = new List<DrawingObjectInstanceDefinition>(_clipboardDrawingObjectInstances.Count);
        foreach (var item in _clipboardDrawingObjectInstances)
        {
            var layerId = container.Scene.ResolveInstanceLayerId(item.SceneLayerId);
            var baseState = item.BaseState with
            {
                X = VectorUnits.Quantize(item.BaseState.X + offset.X),
                Y = VectorUnits.Quantize(item.BaseState.Y + offset.Y)
            };
            if (!_project.TryAddDrawingObjectInstance(
                    container.Id,
                    item.DrawingObjectId,
                    baseState.Position,
                    layerId,
                    out var instance)
                || instance is null)
            {
                continue;
            }

            instance.Name = item.Name;
            instance.SetStateAtFrame(0, baseState);
            instance.RestoreStateKeyframes(item.StateKeyframes.Select(keyframe => keyframe with
            {
                State = keyframe.State with
                {
                    X = VectorUnits.Quantize(keyframe.State.X + offset.X),
                    Y = VectorUnits.Quantize(keyframe.State.Y + offset.Y)
                }
            }));
            pasted.Add(instance);
        }

        return pasted;
    }

    private bool PasteCopiedSceneInstances()
    {
        var scene = ActiveScene();
        if (scene is null || _clipboardDrawingObjectInstances.Count == 0) return false;

        var timelineSnapshot = scene.Timeline.CreateSnapshot();
        var instanceSnapshot = scene.CreateInstanceSnapshot();
        var timelineSelection = _timeline.CaptureSelectionSnapshot();
        var offset = new PointF(PasteOffsetUnits, PasteOffsetUnits);
        var pasted = new List<DrawingObjectInstanceDefinition>(_clipboardDrawingObjectInstances.Count);
        foreach (var item in _clipboardDrawingObjectInstances)
        {
            var baseState = item.BaseState with
            {
                X = VectorUnits.Quantize(item.BaseState.X + offset.X),
                Y = VectorUnits.Quantize(item.BaseState.Y + offset.Y)
            };
            if (!_project.TryAddSceneInstance(
                    scene.Id,
                    item.DrawingObjectId,
                    baseState.Position,
                    baseState.Z,
                    item.SceneLayerId,
                    out var instance)
                || instance is null)
            {
                continue;
            }

            instance.Name = item.Name;
            instance.SetStateAtFrame(0, baseState);
            instance.RestoreStateKeyframes(item.StateKeyframes.Select(keyframe => keyframe with
            {
                State = keyframe.State with
                {
                    X = VectorUnits.Quantize(keyframe.State.X + offset.X),
                    Y = VectorUnits.Quantize(keyframe.State.Y + offset.Y)
                }
            }));
            pasted.Add(instance);
        }

        if (pasted.Count == 0)
        {
            scene.RestoreInstanceSnapshot(instanceSnapshot);
            scene.Timeline.RestoreSnapshot(timelineSnapshot);
            scene.SynchronizeTimelineTracks();
            return false;
        }

        PushSceneTimelineUndo(
            scene,
            timelineSnapshot,
            instanceSnapshot: instanceSnapshot,
            playheadFrame: _frame,
            timelineSelection: timelineSelection);
        _timeline.RefreshTimeline();
        SetSceneInstanceSelection(pasted, pasted[^1]);
        RebuildSceneComposition();
        _hierarchyPanel.RefreshScene();
        _sceneEditorPanel.BindProject(_scenes, _activeSceneIndex, _drawingObjects);
        UpdateInspector();
        _stage.Invalidate();
        return true;
    }

    private bool MoveSelectedDrawingObjectsInStack(int direction)
    {
        if (direction == 0) return false;
        if (IsSceneCompositionContext()) return MoveSelectedSceneInstancesInStack(direction);
        if (DrawingToolsBlocked()) return false;
        if (SelectedSceneInstances().Count > 0)
        {
            return MoveSelectedNestedDrawingObjectsInStack(direction);
        }
        var snapshot = CreateCanvasMutationSnapshot();
        if (!TryPrepareSelectedDrawingObjectsForCommand(snapshot, out var targets)
            || !_scene.MoveObjectsInLayerStack(targets, direction, _frame))
        {
            RestoreCanvasMutationSnapshot(snapshot);
            return false;
        }

        SetSelection(targets);
        PushUndoSnapshot(snapshot);
        _hierarchyPanel.RefreshScene();
        UpdateInspector();
        _stage.Invalidate();
        return true;
    }

    private bool FlipSelectedNestedDrawingObjects(bool horizontal)
    {
        if (IsSceneCompositionContext()) return false;
        var instances = SelectedSceneInstances();
        if (instances.Count == 0 || !TryGetSelectedSceneInstanceBounds(out var bounds)) return false;

        var centerX = bounds.Left + bounds.Width * 0.5f;
        var centerY = bounds.Top + bounds.Height * 0.5f;
        var changed = false;
        _sceneInstanceTimelineKeyframeEnsuredIds.Clear();
        _sceneInstanceTimelineEditedFrames.Clear();
        foreach (var instance in instances)
        {
            var editFrame = PrepareInstanceStateTimelineEdit(instance);
            var state = instance.EvaluateState(editFrame);
            changed |= instance.SetStateAtFrame(
                editFrame,
                horizontal
                    ? state with
                    {
                        X = VectorUnits.Quantize(centerX * 2 - state.X),
                        ScaleX = -state.ScaleX
                    }
                    : state with
                    {
                        Y = VectorUnits.Quantize(centerY * 2 - state.Y),
                        ScaleY = -state.ScaleY
                    });
        }
        if (!changed)
        {
            ClearInstanceTimelineEditTracking();
            return false;
        }

        RefreshEditedInstanceTimelineTweens();
        ClearInstanceTimelineEditTracking();
        _sceneInstanceTimelineDirty = false;
        _timeline.RefreshTimeline();
        RebuildDrawingObjectUnderlay();
        SetSceneInstanceSelection(instances, instances.FirstOrDefault(instance =>
            string.Equals(instance.Id, _selectedSceneInstanceId, StringComparison.Ordinal)));
        UpdateInspector();
        _stage.Invalidate();
        return true;
    }

    private bool CanMoveSelectedInstancesInStack(int direction)
    {
        var instanceIds = SelectedSceneInstances().Select(instance => instance.Id).ToArray();
        if (direction == 0 || instanceIds.Length == 0) return false;
        if (IsSceneCompositionContext())
        {
            var scene = ActiveScene();
            return scene is not null
                && !IsScene3DView()
                && _project.CanMoveSceneInstancesInLayer(scene.Id, instanceIds, direction);
        }

        var container = ActiveDrawingObject();
        return container is not null
            && _project.CanMoveDrawingObjectInstancesInLayer(container.Id, instanceIds, direction);
    }

    private bool MoveSelectedSceneInstancesInStack(int direction)
    {
        if (!IsSceneCompositionContext() || IsScene3DView() || direction == 0) return false;
        var scene = ActiveScene();
        var instanceIds = SelectedSceneInstances().Select(instance => instance.Id).ToArray();
        if (scene is null
            || instanceIds.Length == 0
            || !_project.CanMoveSceneInstancesInLayer(scene.Id, instanceIds, direction))
        {
            return false;
        }

        var timelineSnapshot = scene.Timeline.CreateSnapshot();
        var instanceSnapshot = scene.CreateInstanceSnapshot();
        var playheadFrame = _frame;
        var timelineSelection = _timeline.CaptureSelectionSnapshot();
        var primaryInstanceId = _selectedSceneInstanceId;
        if (!_project.TryMoveSceneInstancesInLayer(scene.Id, instanceIds, direction)) return false;

        PushSceneTimelineUndo(
            scene,
            timelineSnapshot,
            instanceSnapshot: instanceSnapshot,
            playheadFrame: playheadFrame,
            timelineSelection: timelineSelection);
        _timeline.RefreshTimeline();
        RebuildSceneComposition();
        RestoreTimelineInstanceSelection(instanceIds, primaryInstanceId);
        _hierarchyPanel.RefreshScene();
        _sceneEditorPanel.BindProject(_scenes, _activeSceneIndex, _drawingObjects);
        UpdateInspector();
        _stage.Invalidate();
        return true;
    }

    private bool MoveSelectedNestedDrawingObjectsInStack(int direction)
    {
        if (IsSceneCompositionContext() || direction == 0) return false;
        var container = ActiveDrawingObject();
        var instances = SelectedSceneInstances();
        var instanceIds = instances.Select(instance => instance.Id).ToArray();
        if (container is null
            || instanceIds.Length == 0
            || !_project.TryMoveDrawingObjectInstancesInLayer(container.Id, instanceIds, direction))
        {
            return false;
        }

        RebuildDrawingObjectUnderlay();
        SetSceneInstanceSelection(instances, instances.FirstOrDefault(instance =>
            string.Equals(instance.Id, _selectedSceneInstanceId, StringComparison.Ordinal)));
        UpdateInspector();
        _stage.Invalidate();
        return true;
    }

}
