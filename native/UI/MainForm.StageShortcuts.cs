using System.Numerics;

namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private bool _duplicateDragArmed;
    private bool _subtractMarquee;
    private bool _intersectMarquee;
    private (bool Inspector, bool Timeline)? _savedPanelGroup;
    private string? _duplicateSelectionKey;
    private PointF _duplicateOrigin;
    private PointF _duplicateStep;
    private string? _occludedCycleStartInstanceId;
    private int _occludedCycleStartObject = -1;

    private string StageSelectionKey() => $"{_project.Id}:{ActiveScene()?.Id}:{ActiveDrawingObject()?.Id}:{_frame}:"
        + string.Join(",", _selectedObjects) + ":" + string.Join(",", _selectedSceneInstanceIds);

    private RectangleF? StageSelectionBounds()
    {
        RectangleF? bounds = null;
        if (!IsSceneCompositionContext())
            foreach (var i in SelectedActiveDrawingObjectIndices())
            {
                var b = SmartObjectBounds(_scene, i);
                bounds = bounds is { } old ? RectangleF.Union(old, b) : b;
            }
        var instanceBounds = SmartInstanceBounds();
        foreach (var instance in SelectedSceneInstances())
            if (instanceBounds.TryGetValue(instance.Id, out var b))
                bounds = bounds is { } old ? RectangleF.Union(old, b) : b;
        return bounds;
    }

    private bool DuplicateStageSelection(bool linked = false)
    {
        var bounds = StageSelectionBounds();
        if (bounds is null) return false;
        var sameSelection = _duplicateSelectionKey == StageSelectionKey();
        var step = sameSelection ? _duplicateStep : PointF.Empty;
        if (sameSelection && bounds.Value.Location != _duplicateOrigin)
            step = new PointF(bounds.Value.X - _duplicateOrigin.X, bounds.Value.Y - _duplicateOrigin.Y);
        // Duplication must not replace the user's internal clipboard.
        var objects = _clipboardObjects.ToArray();
        var instances = _clipboardDrawingObjectInstances.ToArray();
        var sequence = _internalClipboardSequence;
        var createdIds = new List<string>();
        var completed = false;
        try
        {
            if (!CopySelectedObjects()) return false;
            if (!linked)
            {
                var remap = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var sourceId in _clipboardDrawingObjectInstances.Select(item => item.DrawingObjectId).Distinct().ToArray())
                {
                    if (!_project.TryDuplicateDrawingObject(sourceId, out var copy) || copy is null) return false;
                    remap[sourceId] = copy.Id;
                    createdIds.Add(copy.Id);
                }
                for (var i = 0; i < _clipboardDrawingObjectInstances.Count; i++)
                    _clipboardDrawingObjectInstances[i] = _clipboardDrawingObjectInstances[i] with
                        { DrawingObjectId = remap[_clipboardDrawingObjectInstances[i].DrawingObjectId] };
            }
            if (!PasteCopiedObjectsWithOffset(step, preserveOverlap: true)) return false;
            if (createdIds.Count > 0)
            {
                if (IsSceneCompositionContext())
                {
                    var entry = _sceneTimelineUndoStack.Pop();
                    _undoSequenceByEntry.Remove(entry);
                    var replacement = entry with { CreatedDrawingObjectIds = createdIds.ToArray() };
                    _sceneTimelineUndoStack.Push(replacement);
                    RecordUndoSequence(replacement);
                }
                else
                {
                    var entry = _undoStack.Pop();
                    _undoSequenceByEntry.Remove(entry);
                    var replacement = entry with { CreatedDrawingObjectIds = createdIds.ToArray() };
                    _undoStack.Push(replacement);
                    RecordUndoSequence(replacement);
                }
                RefreshDrawingObjectAssetPresentation();
            }
            completed = true;
            _duplicateSelectionKey = StageSelectionKey();
            _duplicateOrigin = StageSelectionBounds()?.Location ?? bounds.Value.Location;
            _duplicateStep = step;
            return true;
        }
        finally
        {
            if (!completed) foreach (var id in createdIds) _project.TryRemoveDrawingObject(id, out _);
            _clipboardObjects.Clear();
            _clipboardObjects.AddRange(objects);
            _clipboardDrawingObjectInstances.Clear();
            _clipboardDrawingObjectInstances.AddRange(instances);
            _internalClipboardSequence = sequence;
        }
    }

    private static PointF ConstrainStageMove(PointF world, PointF start)
    {
        if ((ModifierKeys & Keys.Shift) == 0) return world;
        return Math.Abs(world.X - start.X) >= Math.Abs(world.Y - start.Y)
            ? new PointF(world.X, start.Y) : new PointF(start.X, world.Y);
    }

    private bool CycleOccludedStageSelection(Point screen)
    {
        if (IsSceneCompositionContext()) return CycleOccludedStageInstances(screen);
        if (ActiveDrawingObject()?.Instances.Count > 0) return CycleOccludedMixedStageSelection(screen);
        var point = _stage.ScreenToWorld(screen);
        var hits = new List<DrawingElementHit>();
        var excluded = new HashSet<int>();
        while (true)
        {
            var hit = _scene.HitTestOccludedElement(point, _frame, SelectionToleranceWorld(), i => !excluded.Contains(i));
            if (!hit.IsValid) break;
            excluded.Add(hit.Key.ObjectIndex);
            hits.Add(hit);
        }
        if (hits.Count == 0) return false;
        var current = hits.FindIndex(hit => hit.Key.ObjectIndex == _occludedCycleStartObject);
        SetSelection(hits[(current + 1) % hits.Count]);
        UpdateInspector();
        return true;
    }

    private bool CycleOccludedStageInstances(Point screen)
    {
        var instances = ActiveEditableInstances().ToDictionary(instance => instance.Id, StringComparer.Ordinal);
        var excluded = new HashSet<string>(StringComparer.Ordinal);
        var hits = new List<DrawingObjectInstanceDefinition>();
        bool Candidate(int index) => _sceneCompositionResult.TryGetOwner(index, out var owner)
            && instances.ContainsKey(string.IsNullOrWhiteSpace(owner.RootInstanceId) ? owner.InstanceId : owner.RootInstanceId)
            && !excluded.Contains(string.IsNullOrWhiteSpace(owner.RootInstanceId) ? owner.InstanceId : owner.RootInstanceId);
        while (true)
        {
            int index;
            if (_stage.UsesReferenceProjection)
            {
                if (!_stage.TryHitTestProjectedObjectFiltered(screen, 7, out index, Candidate)) break;
            }
            else
            {
                var world = _stage.ScreenToWorld(screen);
                var hit = _scene.HitTestOccludedElement(world, _frame, SelectionToleranceWorld(),
                    i => Candidate(i) && (!UsesFlatSceneMaskSelectionClipping(_scene)
                        || SceneMaskSelectionPointVisible(_scene, i, world, _stage.SceneCompositionMaskClips)));
                if (!hit.IsValid) break;
                index = hit.Key.ObjectIndex;
            }
            if (!_sceneCompositionResult.TryGetOwner(index, out var owner)) break;
            var id = string.IsNullOrWhiteSpace(owner.RootInstanceId) ? owner.InstanceId : owner.RootInstanceId;
            if (!excluded.Add(id)) break;
            hits.Add(instances[id]);
        }
        if (hits.Count == 0) return false;
        var current = hits.FindIndex(instance => instance.Id == _occludedCycleStartInstanceId);
        SetSceneInstanceSelection(hits[(current + 1) % hits.Count]);
        UpdateInspector();
        return true;
    }

    private bool CycleOccludedMixedStageSelection(Point screen)
    {
        var point = _stage.ScreenToWorld(screen);
        var candidates = new List<(DrawingElementHit Hit, DrawingObjectInstanceDefinition? Instance, int Layer, long Order)>();
        var excluded = new HashSet<int>();
        while (true)
        {
            var hit = _scene.HitTestOccludedElement(point, _frame, SelectionToleranceWorld(), i => !excluded.Contains(i));
            if (!hit.IsValid || !excluded.Add(hit.Key.ObjectIndex)) break;
            candidates.Add((hit, null, _scene.ObjectLayer[hit.Key.ObjectIndex], _scene.ObjectOrder[hit.Key.ObjectIndex]));
        }
        var composition = ActiveInstanceCompositionScene();
        var result = ActiveInstanceCompositionResult();
        var available = ActiveEditableInstances().ToDictionary(instance => instance.Id, StringComparer.Ordinal);
        var excludedIds = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            bool Filter(int i) => result.TryGetOwner(i, out var owner)
                && available.ContainsKey(string.IsNullOrWhiteSpace(owner.RootInstanceId) ? owner.InstanceId : owner.RootInstanceId)
                && !excludedIds.Contains(string.IsNullOrWhiteSpace(owner.RootInstanceId) ? owner.InstanceId : owner.RootInstanceId);
            var hit = composition.HitTestOccludedElement(point, 0, SelectionToleranceWorld(), Filter);
            if (!hit.IsValid || !result.TryGetOwner(hit.Key.ObjectIndex, out var owner)) break;
            var id = string.IsNullOrWhiteSpace(owner.RootInstanceId) ? owner.InstanceId : owner.RootInstanceId;
            if (!excludedIds.Add(id)) break;
            var instance = available[id];
            candidates.Add((DrawingElementHit.None, instance, Array.IndexOf(_scene.LayerIds, instance.SceneLayerId),
                composition.ObjectOrder[hit.Key.ObjectIndex]));
        }
        var ordered = candidates.OrderBy(item => item.Layer).ThenByDescending(item => item.Order).ToArray();
        if (ordered.Length == 0) return false;
        var current = Array.FindIndex(ordered, item => item.Instance is { } instance
            ? instance.Id == _occludedCycleStartInstanceId : item.Hit.Key.ObjectIndex == _occludedCycleStartObject);
        var next = ordered[(current + 1) % ordered.Length];
        if (next.Instance is { } selected) SetSceneInstanceSelection(selected);
        else SetSelection(next.Hit);
        UpdateInspector();
        return true;
    }

    private bool HandleConfiguredStageShortcut(ShortcutProfileRecord profile, Keys physicalKey)
    {
        var command = ToolShortcutMap.ResolveCommandId(profile, physicalKey);
        if (command == ShortcutCommandIds.NumericScale) return BeginStageNumericTransform('S');
        if (command == ShortcutCommandIds.LockSelected) return ChangeStageObjectVisibility(locked: true);
        if (command == ShortcutCommandIds.UnlockAll) return ChangeStageObjectVisibility(locked: false);
        if (command == ShortcutCommandIds.FlipHorizontal) return FlipSelectedDrawingObjects(horizontal: true);
        if (command == ShortcutCommandIds.FlipVertical) return FlipSelectedDrawingObjects(horizontal: false);
        if (command == ShortcutCommandIds.ConvertLineToFill)
        {
            _stageContextMenuLineObject = _selectedObjects.FirstOrDefault(i => _scene.CanConvertLineToFill(i, _frame), -1);
            if (_stageContextMenuLineObject < 0) return false;
            ConvertStageContextLineToFill();
            return true;
        }
        if (command == ShortcutCommandIds.MergeLines)
        {
            MergeAndSimplifyLineSegments(new Point(-30_000, -30_000));
            return true;
        }
        return false;
    }

    private bool HandleAdditionalStageShortcut(Keys keyData)
    {
        if (keyData == Keys.H) return ChangeStageObjectVisibility();
        if (keyData == (Keys.Alt | Keys.H)) return ChangeStageObjectVisibility(showAll: true);
        if (keyData == (Keys.Shift | Keys.H)) return ChangeStageObjectVisibility(isolate: true);
        if (keyData == (Keys.Alt | Keys.A)) { ClearSelection(); UpdateInspector(); return true; }
        if (keyData == (Keys.Control | Keys.Shift | Keys.I)) return SelectCurrentFrameObjects(invert: true);
        if (keyData == (Keys.Control | Keys.Shift | Keys.H))
        {
            if (_savedPanelGroup is { } saved)
            {
                if (_inspectorPanelOpen != saved.Inspector) ToggleInspectorPanel();
                if (_timelinePanelOpen != saved.Timeline) ToggleTimelinePanel();
                _savedPanelGroup = null;
            }
            else
            {
                _savedPanelGroup = (_inspectorPanelOpen, _timelinePanelOpen);
                if (_inspectorPanelOpen) ToggleInspectorPanel();
                if (_timelinePanelOpen) ToggleTimelinePanel();
            }
            return true;
        }
        if (keyData == Keys.PageUp) return ReturnToStageParentSymbol();
        if (keyData == Keys.PageDown) return EnterSelectedStageSymbol();
        if (keyData == (Keys.Control | Keys.Alt | Keys.C)) return CopyStageAppearance();
        if (keyData == (Keys.Control | Keys.Alt | Keys.V)) return PasteStageAppearance();
        if (keyData == (Keys.Shift | Keys.G)) { ShowSelectSameMenu(); return true; }
        if (keyData == (Keys.Control | Keys.Add)) return ChangeStageTopologySelection(shrink: false);
        if (keyData == (Keys.Control | Keys.Subtract)) return ChangeStageTopologySelection(shrink: true);
        if (keyData == (Keys.Alt | Keys.D) && _selectedObjects.Count == 0 && SelectedSceneInstances().Count > 0)
        { _duplicateSelectionKey = null; return DuplicateStageSelection(linked: true); }
        if (keyData == (Keys.Control | Keys.D)) return DuplicateStageSelection();
        if (keyData == (Keys.Control | Keys.Shift | Keys.V)) return PasteCopiedObjectsWithOffset(PointF.Empty, preserveOverlap: true);
        if (keyData == (Keys.Control | Keys.B))
        {
            if (!IsDrawingWorkspaceContext || DrawingToolsBlocked() || IsSceneCompositionContext()) return false;
            _stageContextMenuBreakApartObjects = SelectedActiveDrawingObjectIndices();
            _stageContextMenuBreakApartInstances = SelectedSceneInstances().Select(instance => instance.Id).ToArray();
            if (_stageContextMenuBreakApartObjects.Any(i => !CanBreakApartObject(i))
                || SelectedSceneInstances().Any(instance => !CanBreakApartInstance(instance))) return false;
            BreakApartStageSelection();
            return true;
        }
        if (IsScene3DView()) return false;
        if (keyData == (Keys.Control | Keys.Shift | Keys.G)) return BeginStageNumericTransform('G');
        if (keyData == (Keys.Control | Keys.Shift | Keys.R)) return BeginStageNumericTransform('R');
        if (keyData == (Keys.Control | Keys.Shift | Keys.S)) return BeginStageNumericTransform('S');
        if (keyData == Keys.Home)
        {
            if (IsSceneReferenceView()) FitStageBounds(new RectangleF(-_stage.CanvasWorldWidth / 2, -_stage.CanvasWorldHeight / 2,
                _stage.CanvasWorldWidth, _stage.CanvasWorldHeight));
            else _stage.Fit();
            return true;
        }
        if (keyData == (Keys.Control | Keys.Oemplus) || keyData == (Keys.Control | Keys.OemMinus))
        {
            var factor = keyData == (Keys.Control | Keys.Oemplus) ? 1.2f : 1f / 1.2f;
            if (IsSceneReferenceView()) _stage.ZoomReferenceCamera(factor);
            else _stage.ZoomAt(new Point(_stage.Width / 2, _stage.Height / 2), factor);
            return true;
        }
        if (keyData == (Keys.Control | Keys.D0))
        {
            if (IsSceneReferenceView())
            {
                if (_stage.TryProjectScenePosition(Vector3.Zero, out var origin, out _)
                    && _stage.TryProjectScenePosition(new Vector3(VectorUnits.FromPixels(100), 0, 0), out var axis, out _))
                    _stage.ZoomReferenceCamera(100f / Math.Max(0.001f,
                        MathF.Sqrt((axis.X - origin.X) * (axis.X - origin.X) + (axis.Y - origin.Y) * (axis.Y - origin.Y))));
            }
            else _stage.ZoomAt(new Point(_stage.Width / 2, _stage.Height / 2), 1f / _stage.Zoom);
            return true;
        }
        if (keyData == (Keys.Shift | Keys.D2) && StageSelectionBounds() is { } bounds)
        { FitStageBounds(bounds); return true; }
        var key = keyData & Keys.KeyCode;
        if ((keyData & Keys.Modifiers) is Keys.None or Keys.Shift && key is Keys.Left or Keys.Right or Keys.Up or Keys.Down)
        {
            var amount = VectorUnits.FromPixels((keyData & Keys.Shift) != 0 ? 10 : 1);
            return NudgeStageSelection(new PointF(key == Keys.Left ? -amount : key == Keys.Right ? amount : 0,
                (key == Keys.Up ? -amount : key == Keys.Down ? amount : 0) * (IsSceneReferenceView() ? -1 : 1)));
        }
        return false;
    }

    private bool ChangeStageTopologySelection(bool shrink)
    {
        if (IsSceneCompositionContext()) return false;
        var selection = _selectedElements.Where(hit => hit.Key.Kind != DrawingElementKind.Fill).ToArray();
        if (selection.Length == 0)
        {
            selection = _selectedObjects.SelectMany(index =>
            {
                var bounds = _scene.GetObjectWorldBounds(index);
                bounds.Inflate(1, 1);
                return _scene.QueryDrawingElementsInsideBounds(bounds, _frame)
                    .Where(hit => hit.Key.ObjectIndex == index && hit.Key.Kind != DrawingElementKind.Fill);
            }).DistinctBy(hit => hit.Key).ToArray();
        }
        if (selection.Length == 0) return false;
        var result = _scene.ChangeStrokeSelectionRing(selection, _frame, shrink);
        if (result.Length == 0) ClearSelection(); else SetSelection(result, result[^1]);
        UpdateInspector();
        return true;
    }

    private void FitStageBounds(RectangleF bounds)
    {
        if (IsSceneReferenceView())
        {
            var points = new List<PointF>();
            foreach (var corner in new[] { new Vector3(bounds.Left, bounds.Top, 0), new Vector3(bounds.Right, bounds.Top, 0),
                new Vector3(bounds.Right, bounds.Bottom, 0), new Vector3(bounds.Left, bounds.Bottom, 0) })
            {
                if (!_stage.TryProjectScenePosition(corner, out var projected, out _)) return;
                points.Add(projected);
            }
            _stage.ZoomReferenceCamera(Math.Min(_stage.Width / Math.Max(1, points.Max(p => p.X) - points.Min(p => p.X)),
                _stage.Height / Math.Max(1, points.Max(p => p.Y) - points.Min(p => p.Y))) * 0.85f);
            if (_stage.TryProjectScenePosition(new Vector3(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2, 0), out var center, out _))
                _stage.PanReferenceCamera(_stage.Width / 2f - center.X, _stage.Height / 2f - center.Y);
            return;
        }
        var screenCenter = _stage.WorldToScreen(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        _stage.Pan(_stage.Width / 2f - screenCenter.X, _stage.Height / 2f - screenCenter.Y);
        var cosine = Math.Abs(MathF.Cos(_stage.CanvasRotation));
        var sine = Math.Abs(MathF.Sin(_stage.CanvasRotation));
        var zoom = Math.Clamp(Math.Min(_stage.Width / Math.Max(1f, VectorUnits.ToPixels(bounds.Width * cosine + bounds.Height * sine)),
            _stage.Height / Math.Max(1f, VectorUnits.ToPixels(bounds.Width * sine + bounds.Height * cosine))) * 0.85f, 0.02f, 64f);
        _stage.ZoomAt(new Point(_stage.Width / 2, _stage.Height / 2), zoom / _stage.Zoom);
    }

    private bool JumpStageTimelineKeyframe(bool forward)
    {
        if (_timeline.ActiveTrackId is not { } trackId) return false;
        var track = _timeline.Context.Timeline.FindTrack(trackId);
        if (track is null) return false;
        var frames = track.Keyframes.Select(key => key.Frame).Where(frame => forward ? frame > _frame : frame < _frame);
        var target = forward ? frames.DefaultIfEmpty(_frame).Min() : frames.DefaultIfEmpty(_frame).Max();
        StopPlayback();
        SetFrame(target);
        return true;
    }

    private bool NudgeStageSelection(PointF delta) => EditStageSelection(
        targets => _scene.TranslateObjectsForPreview(_scene.ExpandMotionTransformObjects(targets, _frame)
            .Where(i => !_scene.ObjectLocked[i]).ToArray(), delta.X, delta.Y),
        (instance, frame) =>
        {
            var state = instance.EvaluateState(frame);
            instance.SetStateAtFrame(frame, state with
                { X = VectorUnits.Quantize(state.X + delta.X), Y = VectorUnits.Quantize(state.Y + delta.Y) });
        });
}
