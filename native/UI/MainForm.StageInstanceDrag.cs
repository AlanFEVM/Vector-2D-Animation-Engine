namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private bool _duplicateProjectedDragArmed;

    private void CaptureNestedInstancePointerUndo()
    {
        if (IsSceneCompositionContext() || _undoCapturedForPointerEdit || ActiveDrawingObject() is not { } owner) return;
        _pointerUndoSnapshot = _scene.CreateSnapshot();
        PushUndoSnapshot(_pointerUndoSnapshot, owner, owner.CreateInstanceSnapshot());
        _undoCapturedForPointerEdit = true;
    }

    private bool TryBeginModifiedStageInstanceMarquee(MouseEventArgs e)
    {
        if ((!IsSceneCompositionContext() && ActiveDrawingObject()?.Instances.Count is not > 0)
            || IsScene3DView() || e.Button != MouseButtons.Left
            || _tool != ToolMode.Select || !(IsAltPressed() || IsControlPressed())) return false;
        if (!IsSceneCompositionContext() && IsControlPressed() && IsShiftPressed() && !IsAltPressed()) return false;
        // Alt on an already selected body keeps the duplicate gesture. Elsewhere it subtracts.
        if (IsAltPressed() && !IsShiftPressed())
        {
            var hit = _stage.UsesReferenceProjection
                ? TryResolveProjectedSceneInstance(e.Location, out var projected) ? projected : null
                : TryResolveSceneInstanceAt(_stage.ScreenToWorld(e.Location), out var flat) ? flat : null;
            if (hit is not null && _selectedSceneInstanceIds.Contains(hit.Id)) return false;
            if (!IsSceneCompositionContext())
            {
                var local = _scene.HitTestElement(_stage.ScreenToWorld(e.Location), _frame, SelectionToleranceWorld());
                if (local.IsValid && _selectedObjects.Contains(local.Key.ObjectIndex)) return false;
            }
        }
        _startScreen = e.Location;
        _startWorld = _stage.ScreenToWorld(e.Location);
        _lastMouse = e.Location;
        _subtractMarquee = IsAltPressed();
        _intersectMarquee = _subtractMarquee && IsShiftPressed();
        _additiveSelection = IsShiftPressed() || _subtractMarquee;
        _forceMarqueeOnPointerDown = true;
        _stage.Capture = true;
        BeginMarqueeFromPendingSelection(e.Location);
        return true;
    }

    private void CompleteProjectedStageInstanceMarquee(Point start, Point end)
    {
        if (Math.Abs(end.X - start.X) + Math.Abs(end.Y - start.Y) <= 6)
        { RestoreMarqueeBaseSelection(); return; }
        var bounds = RectangleF.FromLTRB(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y),
            Math.Max(start.X, end.X), Math.Max(start.Y, end.Y));
        var byInstance = new Dictionary<string, RectangleF>(StringComparer.Ordinal);
        foreach (var (index, objectBounds) in _stage.GetProjectedSelectionObjectBounds())
        {
            if (!_sceneCompositionResult.TryGetOwner(index, out var owner)) continue;
            var id = string.IsNullOrWhiteSpace(owner.RootInstanceId) ? owner.InstanceId : owner.RootInstanceId;
            byInstance[id] = byInstance.TryGetValue(id, out var previous)
                ? RectangleF.Union(previous, objectBounds) : objectBounds;
        }
        var hits = byInstance.Where(pair => bounds.Contains(pair.Value)).Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
        var original = _marqueeInstanceSelectionBase.ToHashSet(StringComparer.Ordinal);
        var selection = ActiveEditableInstances().Where(instance => _subtractMarquee
            ? original.Contains(instance.Id) && _intersectMarquee == hits.Contains(instance.Id)
            : hits.Contains(instance.Id) || _additiveSelection && original.Contains(instance.Id)).ToArray();
        SetSceneInstanceSelection(selection, selection.LastOrDefault());
    }

    private bool DuplicateProjectedSceneDrag()
    {
        _duplicateProjectedDragArmed = false;
        var original = _spatialTransformEditSession;
        if (original is null)
        {
            if (IsSceneCompositionContext()) return false;
            _duplicateSelectionKey = null;
            if (!DuplicateStageSelection()) return false;
            _undoCapturedForPointerEdit = true;
            _pointerUndoSnapshot = _undoStack.TryPeek(out var entry) ? entry.Snapshot : null;
            BeginSceneInstanceTransformPreview();
            return true;
        }
        _duplicateSelectionKey = null;
        if (!DuplicateStageSelection()) return false;
        // Paste captures history before creating any copies. Transfer that transaction to the
        // active pointer session so moving and creating the copies have one undo boundary.
        var paste = _sceneTimelineUndoStack.Pop();
        _undoSequenceByEntry.Remove(paste);
        var instances = SelectedSceneInstances();
        var frames = instances.ToDictionary(instance => instance.Id,
            ResolveInstanceStateEditFrameForCurrentContext, StringComparer.Ordinal);
        _spatialTransformEditSession = new SpatialTransformEditSession
        {
            Scene = original.Scene,
            TimelineSnapshot = original.TimelineSnapshot,
            InstanceSnapshot = original.InstanceSnapshot,
            TimelineSelection = original.TimelineSelection,
            SelectedInstanceIds = instances.Select(instance => instance.Id).ToArray(),
            PrimaryInstanceId = _selectedSceneInstanceId,
            EditFrames = frames,
            StartStates = instances.ToDictionary(instance => instance.Id,
                instance => instance.EvaluateState(frames[instance.Id]), StringComparer.Ordinal),
            CreatedDrawingObjectIds = paste.CreatedDrawingObjectIds,
            OriginalSelectedInstanceIds = original.SelectedInstanceIds,
            OriginalPrimaryInstanceId = original.PrimaryInstanceId,
            Changed = true
        };
        ClearInstanceTimelineEditTracking();
        ClearSceneSnapQueryCache();
        BeginSceneInstanceTransformPreview();
        return true;
    }
}
