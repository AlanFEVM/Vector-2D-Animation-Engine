using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Clipper2Lib;

namespace VectorAnimationEngine;

internal sealed partial class MainForm : Form
{
    private bool MergeSelectedFillsAfterGeometryEdit(
        bool connectNearby,
        IReadOnlyCollection<DrawingStackKey>? additionalFillKeys = null)
    {
        var selected = _selectedObjects.Where(index => (uint)index < _scene.ObjectCount).Distinct().ToArray();
        if (selected.Length == 0) return false;
        var retainedKeys = selected
            .Where(index => !IsFillShape(_scene.ShapeKind[index]))
            .Select(index => new DrawingStackKey(_scene.ObjectOrder[index], _scene.ObjectSubOrder[index]))
            .ToHashSet();
        var fillKeys = selected
            .Where(index => IsFillShape(_scene.ShapeKind[index]))
            .Select(index => new DrawingStackKey(_scene.ObjectOrder[index], _scene.ObjectSubOrder[index]))
            .Concat(additionalFillKeys ?? Array.Empty<DrawingStackKey>())
            .Distinct()
            .ToArray();
        var changed = false;
        foreach (var key in fillKeys)
        {
            var source = FindObjectByStackKey(key);
            if (source < 0 || !IsFillShape(_scene.ShapeKind[source])) continue;
            var beforeCount = _scene.ObjectCount;
            var merged = _scene.MergeSameColorFillsAround(source, connectNearby, frame: _frame);
            if ((uint)merged >= _scene.ObjectCount) continue;
            retainedKeys.Add(new DrawingStackKey(_scene.ObjectOrder[merged], _scene.ObjectSubOrder[merged]));
            changed |= _scene.ObjectCount != beforeCount || merged != source;
        }

        if (fillKeys.Length == 0 || !changed) return false;
        var mergedSelection = Enumerable.Range(0, _scene.ObjectCount)
            .Where(index => retainedKeys.Contains(new DrawingStackKey(_scene.ObjectOrder[index], _scene.ObjectSubOrder[index])))
            .ToArray();
        SetSelection(mergedSelection);
        return changed;
    }

    private bool ApplySelectedFillOverwriteAfterGeometryEdit()
    {
        if (IsSceneCompositionContext()) return false;
        var selectedFills = _selectedObjects
            .Where(index => (uint)index < _scene.ObjectCount && IsFillShape(_scene.ShapeKind[index]))
            .Distinct()
            .ToArray();
        if (selectedFills.Length == 0) return false;

        var retainedKeys = _selectedObjects
            .Where(index => (uint)index < _scene.ObjectCount && !IsFillShape(_scene.ShapeKind[index]))
            .Select(index => new DrawingStackKey(_scene.ObjectOrder[index], _scene.ObjectSubOrder[index]))
            .ToHashSet();
        var geometryRevision = _scene.GeometryRevision;
        var overwritten = _scene.ApplyFillOverwriteToNewObjects(selectedFills, _frame);
        if (_scene.GeometryRevision == geometryRevision) return false;

        var retainedSelection = Enumerable.Range(0, _scene.ObjectCount)
            .Where(index => retainedKeys.Contains(new DrawingStackKey(_scene.ObjectOrder[index], _scene.ObjectSubOrder[index])))
            .Concat(overwritten.Where(index => (uint)index < _scene.ObjectCount))
            .Distinct()
            .ToArray();
        SetSelection(retainedSelection);
        return true;
    }

    private bool MergeCompatibleLinesAfterDrawingOperation(IReadOnlyCollection<int>? objectScope = null)
    {
        if (IsSceneCompositionContext()) return false;

        var selectedBefore = _selectedObjects
            .Where(index => (uint)index < _scene.ObjectCount)
            .Distinct()
            .ToArray();
        var primaryBefore = (uint)_selectedObject < _scene.ObjectCount ? _selectedObject : -1;
        var result = _scene.MergeCompatibleLineSegments(_frame, objectScope);
        if (!result.Changed) return false;

        int Remap(int index)
        {
            return (uint)index < result.OldToNewObjectIndex.Length
                ? result.OldToNewObjectIndex[index]
                : -1;
        }

        var primaryAfter = Remap(primaryBefore);
        var selectedAfter = selectedBefore
            .Select(Remap)
            .Where(index => index >= 0 && index != primaryAfter)
            .Distinct()
            .ToList();
        if (primaryAfter >= 0) selectedAfter.Add(primaryAfter);
        SetSelection(selectedAfter);
        return true;
    }

    private bool FinalizeNewTopologyStrokeDrawingOperation(int newObject)
    {
        if ((uint)newObject >= _scene.ObjectCount
            || _scene.ShapeKind[newObject] is not (ShapeKind.Line or ShapeKind.Freeform))
        {
            return false;
        }
        var shape = _scene.ShapeKind[newObject];
        var changed = shape == ShapeKind.Line
            && MergeCompatibleLinesAfterDrawingOperation(LocalLineMergeScope([newObject]));
        var topologySeed = changed
            && (uint)_selectedObject < _scene.ObjectCount
            && _scene.ShapeKind[_selectedObject] == ShapeKind.Line
                ? _selectedObject
                : newObject;
        if ((uint)topologySeed >= _scene.ObjectCount) return changed;

        var selectedBefore = _selectedObjects
            .Where(index => (uint)index < _scene.ObjectCount)
            .Distinct()
            .ToArray();
        var primaryBefore = (uint)_selectedObject < _scene.ObjectCount ? _selectedObject : -1;
        var materialized = _scene.MaterializeLineIntersections([topologySeed], _frame);
        if (!materialized.Success || !materialized.Changed) return changed;

        IEnumerable<int> Remap(int source)
        {
            var splitResults = materialized.Parts
                .Where(part => part.Source.ObjectIndex == source)
                .Select(part => part.Result.ObjectIndex)
                .Distinct()
                .ToArray();
            if (splitResults.Length > 0) return splitResults;
            if ((uint)source < materialized.OldToNewObjectIndex.Length
                && materialized.OldToNewObjectIndex[source] >= 0)
            {
                return [materialized.OldToNewObjectIndex[source]];
            }
            return [];
        }

        if (selectedBefore.Length > 0)
        {
            var remapped = selectedBefore
                .Where(index => index != primaryBefore)
                .SelectMany(Remap)
                .Concat(primaryBefore >= 0 ? Remap(primaryBefore) : [])
                .Distinct()
                .ToArray();
            SetSelection(remapped);
        }
        return true;
    }

    private int[] NormalizeNewPaintObjects(
        IReadOnlyList<int> objectIndices,
        bool enforceComplexityBudget = false)
    {
        return _scene.NormalizePaintForInteractiveCommit(
            objectIndices,
            connectNearby: true,
            frame: _frame,
            enforceComplexityBudget: enforceComplexityBudget);
    }

    private int[] LocalLineMergeScope(IReadOnlyCollection<int> seedObjects)
    {
        var seeds = seedObjects
            .Where(index => (uint)index < _scene.ObjectCount
                && _scene.ShapeKind[index] == ShapeKind.Line
                && _scene.IsObjectActive(index, _frame))
            .Distinct()
            .ToArray();
        if (seeds.Length == 0) return [];
        if (_scene.ObjectCount > MaxInteractiveLineMergeSceneObjects) return seeds;

        var candidates = new HashSet<int>(seeds);
        foreach (var seed in seeds)
        {
            var bounds = _scene.GetObjectWorldBounds(seed);
            bounds.Inflate(6, 6);
            foreach (var candidate in _scene.QueryObjects(bounds, _frame))
            {
                if (_scene.ShapeKind[candidate] != ShapeKind.Line
                    || _scene.ObjectLayer[candidate] != _scene.ObjectLayer[seed]
                    || _scene.ObjectKeyframeFrame[candidate] != _scene.ObjectKeyframeFrame[seed])
                {
                    continue;
                }

                candidates.Add(candidate);
                if (candidates.Count > MaxInteractiveLineMergeCandidates) return seeds;
            }
        }

        return candidates.OrderBy(index => index).ToArray();
    }

    private bool MergeCompatibleLinesAfterMaterialChange()
    {
        if (_materialEditSession is not null)
        {
            _lineMergePendingAfterMaterialEdit = true;
            return false;
        }

        return MergeCompatibleLinesAfterDrawingOperation();
    }

    private bool MergeSelectedFillsAfterMaterialChange()
    {
        if (_materialEditSession is not null)
        {
            _fillMergePendingAfterMaterialEdit = true;
            return false;
        }

        return MergeSelectedFillsAfterGeometryEdit(connectNearby: true);
    }

    private int FindObjectByStackKey(DrawingStackKey key)
    {
        for (var index = 0; index < _scene.ObjectCount; index++)
        {
            if (_scene.ObjectOrder[index] == key.Order && _scene.ObjectSubOrder[index].Equals(key.SubOrder)) return index;
        }

        return -1;
    }

    private int FindActiveObjectByStackKey(DrawingStackKey key)
    {
        for (var index = 0; index < _scene.ObjectCount; index++)
        {
            if (_scene.ObjectOrder[index] == key.Order
                && _scene.ObjectSubOrder[index].Equals(key.SubOrder)
                && _scene.IsObjectActive(index, _frame))
            {
                return index;
            }
        }

        return -1;
    }

    private void CompleteMarqueeSelection(Point endScreen)
    {
        var sceneCompositionContext = IsSceneCompositionContext();
        if (sceneCompositionContext) _marqueeSelectionCancellationPending = false;
        else FinalizePendingMarqueeSelectionCancellation();
        var startScreen = _marqueeStart ?? endScreen;
        var dx = endScreen.X - startScreen.X;
        var dy = endScreen.Y - startScreen.Y;
        if (sceneCompositionContext)
        {
            if (Math.Abs(dx) + Math.Abs(dy) > 6)
            {
                var a = _stage.ScreenToWorld(startScreen);
                var b = _stage.ScreenToWorld(endScreen);
                var bounds = RectangleF.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
                if (!TrySetNestedInstanceMarqueeSelection(bounds)) RestoreMarqueeBaseSelection();
            }
            else
            {
                RestoreMarqueeBaseSelection();
            }

            _marqueeSelecting = false;
            _marqueeStart = null;
            _stage.ClearMarquee();
            UpdateInspector();
            _stage.Invalidate();
            return;
        }

        if (Math.Abs(dx) + Math.Abs(dy) > 6)
        {
            var a = _stage.ScreenToWorld(startScreen);
            var b = _stage.ScreenToWorld(endScreen);
            var bounds = RectangleF.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
            var snapshot = _scene.CreateSnapshot();
            var baseSelectionKeys = _additiveSelection
                ? _marqueeSelectionBase
                    .Where(index => (uint)index < _scene.ObjectCount)
                    .Select(index => (
                        Layer: _scene.ObjectLayer[index],
                        Keyframe: _scene.ObjectKeyframeFrame[index],
                        Order: _scene.ObjectOrder[index]))
                    .Distinct()
                    .ToArray()
                : [];
            var materialized = _scene.MaterializeMarqueeSelectionParts(bounds, _frame);
            var objects = _scene.QueryDrawingObjects(bounds, _frame);
            var selectedObjects = objects
                .Where(index => _scene.IsObjectGeometryInsideBounds(index, bounds))
                .Concat(materialized.SelectedObjects)
                .Distinct()
                .ToArray();
            selectedObjects = OrderMarqueeSelectionForCurveEditing(
                _scene,
                selectedObjects,
                materialized.SelectedObjects);
            if (materialized.Changed)
            {
                PushUndoSnapshot(snapshot);
                var independentStrokes = materialized.SelectedObjects
                    .Where(index => (uint)index < _scene.ObjectCount
                        && _scene.ShapeKind[index] is ShapeKind.Line or ShapeKind.Freeform)
                    .Distinct()
                    .ToArray();
                _marqueeMaterializationSession = new MarqueeMaterializationSession(
                    _scene,
                    snapshot,
                    independentStrokes);
                _hierarchyPanel.RefreshScene();
            }

            var remappedBaseSelection = _marqueeSelectionBase
                .Select(index => (uint)index < materialized.OldToNewObjectIndex.Length
                    ? materialized.OldToNewObjectIndex[index]
                    : -1)
                .Where(index => index >= 0)
                .Concat(baseSelectionKeys.SelectMany(key => Enumerable.Range(0, _scene.ObjectCount)
                    .Where(index => _scene.ObjectLayer[index] == key.Layer
                        && _scene.ObjectKeyframeFrame[index] == key.Keyframe
                        && _scene.ObjectOrder[index] == key.Order)))
                .Distinct()
                .ToArray();

            if (selectedObjects.Length > 0)
            {
                SetMixedSelection(
                    _additiveSelection
                        ? remappedBaseSelection.Concat(selectedObjects)
                        : selectedObjects,
                    ResolveMarqueeInstanceSelection(bounds));
            }
            else if (!TrySetTopologyMarqueeSelection(bounds)
                && !TrySetNestedInstanceMarqueeSelection(bounds))
            {
                RestoreMarqueeBaseSelection();
            }
        }
        else
        {
            RestoreMarqueeBaseSelection();
        }

        _marqueeSelecting = false;
        _marqueeStart = null;
        _stage.ClearMarquee();
        UpdateInspector();
        _stage.Invalidate();
    }

    internal static int[] OrderMarqueeSelectionForCurveEditing(
        VectorScene scene,
        IEnumerable<int> selectedObjects,
        IEnumerable<int> materializedSelectedObjects)
    {
        var ordered = selectedObjects
            .Where(index => (uint)index < scene.ObjectCount)
            .Distinct()
            .ToArray();
        var selectedSet = ordered.ToHashSet();
        var editableCurves = materializedSelectedObjects
            .Where(index => selectedSet.Contains(index)
                && scene.ShapeKind[index] == ShapeKind.Line)
            .Distinct()
            .ToArray();
        var selectedFillOwners = ordered
            .Where(index => scene.ShapeKind[index] == ShapeKind.Path)
            .Select(index => (
                scene.ObjectLayer[index],
                scene.ObjectKeyframeFrame[index],
                scene.ObjectOrder[index]))
            .ToHashSet();
        var editableCurve = editableCurves
            .Where(index => selectedFillOwners.Contains((
                scene.ObjectLayer[index],
                scene.ObjectKeyframeFrame[index],
                scene.ObjectOrder[index])))
            .LastOrDefault(-1);
        if (editableCurve < 0) editableCurve = editableCurves.LastOrDefault(-1);
        return editableCurve >= 0
            ? ordered.Where(index => index != editableCurve).Append(editableCurve).ToArray()
            : ordered;
    }

    private bool TrySetTopologyMarqueeSelection(RectangleF bounds)
    {
        if (bounds.Width <= 0.001f || bounds.Height <= 0.001f) return false;
        var selected = _scene.QueryDrawingElementsInsideBounds(bounds, _frame);
        if (selected.Length == 0) return false;
        var primary = selected
            .OrderBy(hit => _scene.ObjectLayer[hit.Key.ObjectIndex])
            .ThenByDescending(hit => hit.Key.Kind is DrawingElementKind.Stroke or DrawingElementKind.BoundaryStroke ? 1 : 0)
            .ThenByDescending(hit => _scene.ObjectOrder[hit.Key.ObjectIndex])
            .ThenByDescending(hit => _scene.ObjectSubOrder[hit.Key.ObjectIndex])
            .First();
        if (_additiveSelection)
        {
            SetMixedSelection(
                _marqueeSelectionBase.Concat(selected.Select(hit => hit.Key.ObjectIndex)),
                ResolveMarqueeInstanceSelection(bounds));
        }
        else
        {
            SetMixedSelection(selected, primary, ResolveMarqueeInstanceSelection(bounds));
        }
        return true;
    }

    private void SetTopologyMarqueeSelection(IEnumerable<DrawingElementHit> hits, DrawingElementHit primary)
    {
        var selected = hits.Where(hit => hit.IsValid).ToArray();
        if (selected.Length == 0)
        {
            ClearSelection();
            return;
        }

        SetSelection(selected, primary);
    }

    private bool DeleteSelectedObject()
    {
        if (IsSceneCompositionContext()) return DeleteSelectedSceneInstances();

        var container = ActiveDrawingObject();
        var selectedInstances = SelectedSceneInstances().ToArray();
        var targets = _selectedObjects.Where(index => index >= 0 && index < _scene.ObjectCount).ToArray();
        if (targets.Length == 0 && _selectedObject >= 0 && _selectedObject < _scene.ObjectCount) targets = new[] { _selectedObject };
        if (targets.Length == 0 && selectedInstances.Length == 0) return false;
        var snapshot = CreateCanvasMutationSnapshot(targets);
        var instanceSnapshot = selectedInstances.Length > 0 ? container?.CreateInstanceSnapshot() : null;

        if (_selectedElements.Count > 0)
        {
            var materialized = _scene.MaterializeSelectedParts(_selectedElements.Select(hit => hit.Key).ToArray(), _frame);
            if (!materialized.Success)
            {
                RestoreCanvasMutationSnapshot(snapshot);
                return false;
            }

            targets = materialized.Parts.Select(part => part.Result.ObjectIndex).Distinct().ToArray();
        }

        var removedObjects = targets.Length > 0 ? _scene.RemoveObjects(targets) : 0;
        var removedInstances = 0;
        if (container is not null)
        {
            foreach (var selectedInstance in selectedInstances)
            {
                if (_project.TryRemoveDrawingObjectInstance(container.Id, selectedInstance.Id, out _))
                {
                    removedInstances++;
                }
            }
        }

        if (removedObjects != targets.Length
            || removedInstances != selectedInstances.Length)
        {
            if (container is not null && instanceSnapshot is not null)
            {
                container.RestoreInstanceSnapshot(instanceSnapshot);
            }
            RestoreCanvasMutationSnapshot(snapshot);
            return false;
        }

        ClearSelection();
        if (removedObjects > 0) MergeCompatibleLinesAfterDrawingOperation();
        PushUndoSnapshot(snapshot, container, instanceSnapshot);
        if (removedInstances > 0 || IsSceneMaskEditing()) RebuildDrawingObjectUnderlay();

        _geometryDirty = false;
        _stage.ClearDrawingPreview();
        _stage.ClearMarquee();
        _timeline.RefreshTimeline();
        _hierarchyPanel.RefreshScene();
        if (removedInstances > 0) RefreshDrawingObjectAssetPresentation();
        UpdateInspector();
        _stage.Invalidate();
        return true;
    }

    private bool DeleteSelectedSceneInstances()
    {
        var scene = ActiveScene();
        var selected = SelectedSceneInstances().ToArray();
        if (scene is null || selected.Length == 0) return false;

        var timelineSnapshot = scene.Timeline.CreateSnapshot();
        var instanceSnapshot = scene.CreateInstanceSnapshot();
        var timelineSelection = _timeline.CaptureSelectionSnapshot();
        var removed = 0;
        foreach (var instance in selected)
        {
            if (_project.TryRemoveSceneInstance(scene.Id, instance.Id, out _)) removed++;
        }
        if (removed != selected.Length)
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
        ClearSelection();
        _timeline.RefreshTimeline();
        RebuildSceneComposition();
        _hierarchyPanel.RefreshScene();
        _sceneEditorPanel.BindProject(_scenes, _activeSceneIndex, _drawingObjects);
        UpdateInspector();
        _stage.Invalidate();
        AppLog.Info($"Deleted {removed} Scene symbol instance(s)");
        return true;
    }

    private void BeginGlobalViewDrag(MouseEventArgs e, bool referencePan = false)
    {
        _stage.Capture = true;
        _lastMouse = e.Location;
        _startScreen = null;
        _startWorld = null;
        _selectedStart = null;
        _curveControlStart = null;
        _curveControl2Start = null;
        _resizeStartCenter = null;
        _resizeStartSize = null;
        _textAreaResizeStartData = null;
        _activeHandle = EditHandleKind.None;
        _marqueeSelecting = false;
        _marqueeStart = null;
        _pointerHitWasAlreadySelected = false;
        var isReferenceView = IsSceneReferenceView();
        _viewReferencePanning = isReferenceView && (referencePan || IsShiftPressed());
        _viewReferenceZooming = isReferenceView && !referencePan && !IsShiftPressed() && IsControlPressed();
        _viewOrbiting = IsScene3DView() && !_viewReferencePanning && !_viewReferenceZooming;
        _viewZooming = !isReferenceView && IsControlPressed();
        _viewPanning = !isReferenceView && !_viewZooming;
        CancelTraditionalPenPath();
        CancelPenCurve();
        CancelFreehandStroke();
        _stage.ClearMarquee();
        _stage.Cursor = _viewZooming || _viewReferenceZooming ? Cursors.SizeNS : Cursors.SizeAll;
    }

    private void EndGlobalViewDrag()
    {
        _lastMouse = null;
        _viewPanning = false;
        _viewZooming = false;
        _viewOrbiting = false;
        _viewReferencePanning = false;
        _viewReferenceZooming = false;
        _stage.Capture = false;
        RefreshInteractionCursorAtPointer();
    }

    private bool TryHoldTemporaryCanvasPan()
    {
        if (_spacePanKeyDown) return _spacePanHeld;
        _spacePanKeyDown = true;
        var pointer = _stage.PointToClient(Cursor.Position);
        var pointerOverStage = _stage.ClientRectangle.Contains(pointer);
        var pointerInteractionActive = _lastMouse is not null
            || _freehandDrawing
            || _marqueeSelecting
            || _traditionalPenPointerDown
            || _penSegmentDragging
            || _gradientEditSnapshot is not null
            || _spatialTransformPointerSession is not null
            || _viewPanning
            || _viewZooming
            || _viewOrbiting
            || _viewReferencePanning
            || _viewReferenceZooming;
        if (!CanStartTemporaryCanvasPan(
                ContainsFocusedEditor(this),
                _stage.ContainsFocus,
                pointerOverStage,
                pointerInteractionActive)
            || _materialEditSession is not null)
        {
            return false;
        }

        _spacePanHeld = true;
        HideBrushColorPalette();
        ApplyTemporaryCanvasPanCursor();
        return true;
    }

    private bool ConvertSelectedDrawingObjectsToSymbol()
    {
        if (_workspaceTabs.SelectedView != WorkspaceView.BasicDrawing
            || DrawingToolsBlocked()
            || IsSceneCompositionContext()
            || SelectedSceneInstances().Count > 0)
        {
            return false;
        }

        FinishPointerInteractionForFrameChange();
        var initialTargets = SelectedActiveDrawingObjectIndices();
        var container = ActiveDrawingObject();
        if (container is null || initialTargets.Length == 0) return false;

        var layers = initialTargets.Select(index => (int)_scene.ObjectLayer[index]).Distinct().ToArray();
        if (layers.Length != 1
            || _scene.GetLayerKind(layers[0]) != DrawingLayerKind.Drawing
            || _scene.IsLayerEffectivelyLocked(layers[0]))
        {
            return false;
        }

        var wasProjectDirty = _projectDirty;
        var sceneSnapshot = CreateCanvasMutationSnapshot(initialTargets, layers);
        var instanceSnapshot = container.CreateInstanceSnapshot();
        if (!TryPrepareSelectedDrawingObjectsForCommand(sceneSnapshot, out var targets)
            || !_project.TryConvertDrawingObjectsToSymbol(
                container.Id,
                targets,
                _frame,
                out var symbol,
                out var instance)
            || symbol is null
            || instance is null)
        {
            container.RestoreInstanceSnapshot(instanceSnapshot);
            RestoreCanvasMutationSnapshot(sceneSnapshot);
            SetProjectDirty(wasProjectDirty);
            return false;
        }

        PushUndoSnapshot(
            sceneSnapshot,
            container,
            instanceSnapshot,
            createdDrawingObjectId: symbol.Id);
        SetSceneInstanceSelection(instance);
        _timeline.RefreshTimeline();
        _timeline.SelectModelActiveTrack();
        _hierarchyPanel.RefreshScene();
        RefreshDrawingObjectAssetPresentation();
        AppLog.Info($"Converted {targets.Length} drawing object(s) to nested symbol: {symbol.Name}");
        return true;
    }

    internal static bool CanStartTemporaryCanvasPan(
        bool editorFocused,
        bool stageFocused,
        bool pointerOverStage,
        bool pointerInteractionActive)
    {
        return !editorFocused
            && !pointerInteractionActive
            && (stageFocused || pointerOverStage);
    }

    private bool BeginTemporaryCanvasPanPointer(Point location)
    {
        if (!_spacePanHeld || _spacePanPointerActive) return false;
        _spacePanPointerActive = true;
        _stage.Capture = true;
        _lastMouse = location;
        _viewReferencePanning = IsSceneReferenceView();
        _viewPanning = !_viewReferencePanning;
        _stage.ClearBrushTipCursor();
        _stage.ClearFillToolCursor();
        ClearFillHoverPreview();
        _stage.Cursor = Cursors.SizeAll;
        return true;
    }

    private void EndTemporaryCanvasPanPointer()
    {
        if (!_spacePanPointerActive) return;
        _spacePanPointerActive = false;
        _lastMouse = null;
        _viewPanning = false;
        _viewReferencePanning = false;
        if (_stage.Capture) _stage.Capture = false;
        RefreshInteractionCursorAtPointer();
    }

    private void ReleaseTemporaryCanvasPan()
    {
        if (!_spacePanHeld && !_spacePanPointerActive) return;
        _spacePanHeld = false;
        EndTemporaryCanvasPanPointer();
        RefreshInteractionCursorAtPointer();
    }

    private void CancelTemporaryCanvasPan()
    {
        _spacePanKeyDown = false;
        ReleaseTemporaryCanvasPan();
    }

    private bool ApplyTemporaryCanvasPanCursor()
    {
        if (!_spacePanHeld && !_spacePanPointerActive) return false;
        _stage.ClearBrushTipCursor();
        _stage.ClearFillToolCursor();
        ClearFillHoverPreview();
        _stage.Cursor = _spacePanPointerActive ? Cursors.SizeAll : Cursors.Hand;
        return true;
    }

    private DrawingObjectInstanceDefinition? SelectedSceneInstance()
    {
        if (string.IsNullOrWhiteSpace(_selectedSceneInstanceId)) return null;
        return ActiveEditableInstances().FirstOrDefault(instance =>
            string.Equals(instance.Id, _selectedSceneInstanceId, StringComparison.Ordinal));
    }

    private IReadOnlyList<DrawingObjectInstanceDefinition> SelectedSceneInstances()
    {
        if (_selectedSceneInstanceIds.Count == 0) return [];
        return ActiveEditableInstances()
            .Where(instance => _selectedSceneInstanceIds.Contains(instance.Id))
            .ToArray();
    }

    private IReadOnlyList<DrawingObjectInstanceDefinition> ActiveEditableInstances()
    {
        if (IsSceneMaskEditing()) return [];
        return IsSceneCompositionContext()
            ? ActiveScene()?.Instances ?? []
            : ActiveDrawingObject()?.Instances ?? [];
    }

    private void RestoreTimelineInstanceSelection(
        IReadOnlyCollection<string> instanceIds,
        string? primaryInstanceId)
    {
        var resolved = ResolveTimelineInstanceSelection(ActiveEditableInstances(), instanceIds, primaryInstanceId);
        if (resolved.Instances.Length == 0) return;
        SetSceneInstanceSelection(resolved.Instances, resolved.Primary);
    }

    internal static (DrawingObjectInstanceDefinition[] Instances, DrawingObjectInstanceDefinition? Primary)
        ResolveTimelineInstanceSelection(
            IReadOnlyList<DrawingObjectInstanceDefinition> availableInstances,
            IReadOnlyCollection<string> instanceIds,
            string? primaryInstanceId)
    {
        ArgumentNullException.ThrowIfNull(availableInstances);
        ArgumentNullException.ThrowIfNull(instanceIds);
        if (instanceIds.Count == 0) return ([], null);

        var selectedIds = instanceIds.ToHashSet(StringComparer.Ordinal);
        var instances = availableInstances
            .Where(instance => selectedIds.Contains(instance.Id))
            .ToArray();
        var primary = instances.FirstOrDefault(instance =>
            string.Equals(instance.Id, primaryInstanceId, StringComparison.Ordinal));
        return (instances, primary ?? instances.LastOrDefault());
    }

    private VectorScene ActiveInstanceCompositionScene()
    {
        return IsSceneCompositionContext() ? _scene : _drawingObjectUnderlayStage;
    }

    private SceneCompositionResult ActiveInstanceCompositionResult()
    {
        return IsSceneCompositionContext() ? _sceneCompositionResult : _drawingObjectUnderlayResult;
    }

    private bool TryResolveSceneInstanceAt(PointF world, out DrawingObjectInstanceDefinition instance)
    {
        var compositionScene = ActiveInstanceCompositionScene();
        return TryResolveCompositionInstance(
            compositionScene,
            ActiveInstanceCompositionResult(),
            ActiveEditableInstances(),
            world,
            IsSceneCompositionContext() ? _frame : 0,
            SelectionToleranceWorld(),
            out instance,
            UsesFlatSceneMaskSelectionClipping(compositionScene)
                ? _stage.SceneCompositionMaskClips
                : null);
    }

    internal static bool TryResolveCompositionInstance(
        VectorScene compositionScene,
        SceneCompositionResult compositionResult,
        IReadOnlyList<DrawingObjectInstanceDefinition> instances,
        PointF world,
        int frame,
        float toleranceWorld,
        out DrawingObjectInstanceDefinition instance,
        IReadOnlyList<SceneCompositionMaskClip>? maskClips = null)
    {
        ArgumentNullException.ThrowIfNull(compositionScene);
        ArgumentNullException.ThrowIfNull(compositionResult);
        ArgumentNullException.ThrowIfNull(instances);
        instance = null!;
        if (!SceneMaskSelectionHasClips(compositionScene, maskClips))
        {
            var hit = compositionScene.HitTestElement(world, frame, toleranceWorld);
            return hit.IsValid
                && TryResolveCompositionObjectOwner(
                    compositionResult,
                    instances,
                    hit.Key.ObjectIndex,
                    out instance);
        }

        var visibleHit = compositionScene.HitTestElement(
            world,
            frame,
            toleranceWorld,
            objectIndex => SceneMaskSelectionPointVisible(
                compositionScene,
                objectIndex,
                world,
                maskClips!));
        return visibleHit.IsValid
            && TryResolveCompositionObjectOwner(
                compositionResult,
                instances,
                visibleHit.Key.ObjectIndex,
                out instance);
    }

    internal static IReadOnlyList<DrawingObjectInstanceDefinition> FindCompositionInstancesInsideBounds(
        VectorScene compositionScene,
        SceneCompositionResult compositionResult,
        IReadOnlyList<DrawingObjectInstanceDefinition> instances,
        RectangleF bounds,
        IReadOnlyList<SceneCompositionMaskClip>? maskClips = null)
    {
        ArgumentNullException.ThrowIfNull(compositionScene);
        ArgumentNullException.ThrowIfNull(compositionResult);
        ArgumentNullException.ThrowIfNull(instances);
        if (bounds.Width <= 0.001f || bounds.Height <= 0.001f) return [];

        var applyMaskClips = SceneMaskSelectionHasClips(compositionScene, maskClips);
        var boundsByInstanceId = new Dictionary<string, RectangleF>(StringComparer.Ordinal);
        for (var objectIndex = 0; objectIndex < compositionScene.ObjectCount; objectIndex++)
        {
            if (!compositionResult.TryGetOwner(objectIndex, out var owner)) continue;
            var rootInstanceId = string.IsNullOrWhiteSpace(owner.RootInstanceId) ? owner.InstanceId : owner.RootInstanceId;
            var objectBounds = RectangleF.Empty;
            if (applyMaskClips
                && !TryGetSceneMaskSelectionVisibleBounds(
                    compositionScene,
                    objectIndex,
                    maskClips!,
                    out objectBounds))
            {
                continue;
            }
            if (!applyMaskClips) objectBounds = compositionScene.GetObjectWorldBounds(objectIndex);
            boundsByInstanceId[rootInstanceId] = boundsByInstanceId.TryGetValue(rootInstanceId, out var current)
                ? RectangleF.Union(current, objectBounds)
                : objectBounds;
        }

        return instances
            .Where(instance => boundsByInstanceId.TryGetValue(instance.Id, out var instanceBounds)
                && bounds.Contains(instanceBounds.Left, instanceBounds.Top)
                && bounds.Contains(instanceBounds.Right, instanceBounds.Bottom))
            .ToArray();
    }

    private bool UsesFlatSceneMaskSelectionClipping(VectorScene compositionScene)
    {
        return IsSceneCompositionContext()
            && !IsSceneMaskEditing()
            && !_stage.UsesReferenceProjection
            && ReferenceEquals(compositionScene, _scene)
            && _stage.HasSceneCompositionMaskClips(compositionScene);
    }

    private static bool TryResolveCompositionObjectOwner(
        SceneCompositionResult compositionResult,
        IReadOnlyList<DrawingObjectInstanceDefinition> instances,
        int objectIndex,
        out DrawingObjectInstanceDefinition instance)
    {
        instance = null!;
        if (!compositionResult.TryGetOwner(objectIndex, out var owner)) return false;
        var rootInstanceId = string.IsNullOrWhiteSpace(owner.RootInstanceId)
            ? owner.InstanceId
            : owner.RootInstanceId;
        instance = instances.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, rootInstanceId, StringComparison.Ordinal))!;
        return instance is not null;
    }

    private static bool SceneMaskSelectionHasClips(
        VectorScene compositionScene,
        IReadOnlyList<SceneCompositionMaskClip>? maskClips)
    {
        return maskClips is { Count: > 0 }
            && maskClips.Any(clip => ReferenceEquals(clip.TargetScene, compositionScene));
    }

    private static bool SceneMaskSelectionPointVisible(
        VectorScene compositionScene,
        int objectIndex,
        PointF world,
        IReadOnlyList<SceneCompositionMaskClip> maskClips)
    {
        foreach (var clip in maskClips)
        {
            if (!ReferenceEquals(clip.TargetScene, compositionScene)
                || !clip.TargetObjectIndices.Contains(objectIndex))
            {
                continue;
            }

            var contours = SceneMaskSelectionMaskContours(clip);
            if (contours.Length == 0 || !SceneMaskSelectionPointInContours(world, contours)) return false;
        }
        return true;
    }

    private static bool TryGetSceneMaskSelectionVisibleBounds(
        VectorScene compositionScene,
        int objectIndex,
        IReadOnlyList<SceneCompositionMaskClip> maskClips,
        out RectangleF bounds)
    {
        bounds = RectangleF.Empty;
        var relevantClips = maskClips
            .Where(clip => ReferenceEquals(clip.TargetScene, compositionScene)
                && clip.TargetObjectIndices.Contains(objectIndex))
            .ToArray();
        if (relevantClips.Length == 0)
        {
            bounds = compositionScene.GetObjectWorldBounds(objectIndex);
            return !bounds.IsEmpty;
        }

        try
        {
            var visible = SceneMaskSelectionToClipperPaths(
                SceneMaskSelectionObjectContours(compositionScene, objectIndex));
            if (visible.Count == 0) return false;
            foreach (var clip in relevantClips)
            {
                var mask = SceneMaskSelectionToClipperPaths(SceneMaskSelectionMaskContours(clip));
                if (mask.Count == 0) return false;
                visible = SceneMaskSelectionIntersect(visible, mask);
                if (visible.Count == 0) return false;
            }

            return TryGetSceneMaskSelectionClipperBounds(visible, out bounds);
        }
        catch (Exception exception) when (exception is
            ClipperLibException or OverflowException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static PointF[][] SceneMaskSelectionMaskContours(SceneCompositionMaskClip clip)
    {
        if (clip.Frame < 0) return [];
        var maskScene = clip.MaskScene;
        var contours = new List<PointF[]>();
        for (var objectIndex = 0; objectIndex < maskScene.ObjectCount; objectIndex++)
        {
            var layer = maskScene.ObjectLayer[objectIndex];
            if (!maskScene.IsObjectActive(objectIndex, clip.Frame)
                || !maskScene.ShouldRenderLayerContent(layer))
            {
                continue;
            }
            contours.AddRange(SceneMaskSelectionObjectContours(maskScene, objectIndex));
        }
        return contours.ToArray();
    }

    private static PointF[][] SceneMaskSelectionObjectContours(VectorScene scene, int objectIndex)
    {
        if ((uint)objectIndex >= scene.ObjectCount) return [];
        var shape = scene.ShapeKind[objectIndex];
        PointF[][] contours;
        if (shape == ShapeKind.Text && scene.TryGetTextWorldContours(objectIndex, out var textContours))
        {
            contours = textContours;
        }
        else if (shape is ShapeKind.Line or ShapeKind.Freeform or ShapeKind.BrushStroke)
        {
            contours = scene.GetStrokeOutlineContours(objectIndex);
        }
        else
        {
            contours = scene.GetDistortedObjectBoundaryContours(objectIndex);
        }

        if (contours.Length == 0)
        {
            if (shape is ShapeKind.Line or ShapeKind.Freeform or ShapeKind.BrushStroke) return [];
            var objectBounds = scene.GetObjectWorldBounds(objectIndex);
            if (objectBounds.IsEmpty) return [];
            contours = [[
                new PointF(objectBounds.Left, objectBounds.Top),
                new PointF(objectBounds.Right, objectBounds.Top),
                new PointF(objectBounds.Right, objectBounds.Bottom),
                new PointF(objectBounds.Left, objectBounds.Bottom)
            ]];
        }
        else if ((shape is ShapeKind.Line or ShapeKind.Freeform or ShapeKind.BrushStroke)
            && scene.TryGetObjectDistortionsView(objectIndex, out var distortions))
        {
            contours = contours
                .Select(contour => contour
                    .Select(point =>
                    {
                        var mapped = point;
                        foreach (var distortion in distortions) mapped = distortion.Map(mapped);
                        return mapped;
                    })
                    .ToArray())
                .ToArray();
        }

        return contours
            .Where(contour => contour.Length >= 3
                && contour.All(point => float.IsFinite(point.X) && float.IsFinite(point.Y)))
            .ToArray();
    }

    private const double SceneMaskSelectionClipperScale = 1000d;

    private static Paths64 SceneMaskSelectionToClipperPaths(IEnumerable<PointF[]> contours)
    {
        var result = new Paths64();
        foreach (var contour in contours)
        {
            if (contour.Length < 3) continue;
            var path = new Path64(contour.Length);
            foreach (var point in contour)
            {
                path.Add(new Point64(
                    checked((long)Math.Round(
                        point.X * SceneMaskSelectionClipperScale,
                        MidpointRounding.AwayFromZero)),
                    checked((long)Math.Round(
                        point.Y * SceneMaskSelectionClipperScale,
                        MidpointRounding.AwayFromZero))));
            }
            path = Clipper.StripDuplicates(path, true);
            if (path.Count >= 3 && Math.Abs(Clipper.Area(path)) > double.Epsilon) result.Add(path);
        }
        return result;
    }

    private static Paths64 SceneMaskSelectionIntersect(Paths64 subject, Paths64 clip)
    {
        var result = new Paths64();
        var clipper = new Clipper64 { PreserveCollinear = true };
        clipper.AddSubject(subject);
        clipper.AddClip(clip);
        if (!clipper.Execute(ClipType.Intersection, FillRule.EvenOdd, result)) result.Clear();
        return result;
    }

    private static bool TryGetSceneMaskSelectionClipperBounds(Paths64 paths, out RectangleF bounds)
    {
        bounds = RectangleF.Empty;
        if (paths.Count == 0) return false;
        var left = long.MaxValue;
        var top = long.MaxValue;
        var right = long.MinValue;
        var bottom = long.MinValue;
        foreach (var point in paths.SelectMany(path => path))
        {
            left = Math.Min(left, point.X);
            top = Math.Min(top, point.Y);
            right = Math.Max(right, point.X);
            bottom = Math.Max(bottom, point.Y);
        }
        if (left == long.MaxValue || right <= left || bottom <= top) return false;
        bounds = RectangleF.FromLTRB(
            (float)(left / SceneMaskSelectionClipperScale),
            (float)(top / SceneMaskSelectionClipperScale),
            (float)(right / SceneMaskSelectionClipperScale),
            (float)(bottom / SceneMaskSelectionClipperScale));
        return !bounds.IsEmpty;
    }

    private static bool SceneMaskSelectionPointInContours(
        PointF point,
        IReadOnlyList<PointF[]> contours)
    {
        var inside = false;
        foreach (var contour in contours)
        {
            if (SceneMaskSelectionPointOnBoundary(point, contour)) return true;
            if (SceneMaskSelectionPointInPolygon(point, contour)) inside = !inside;
        }
        return inside;
    }

    private static bool SceneMaskSelectionPointInPolygon(PointF point, IReadOnlyList<PointF> polygon)
    {
        if (polygon.Count < 3) return false;
        var inside = false;
        for (int current = 0, previous = polygon.Count - 1;
             current < polygon.Count;
             previous = current++)
        {
            var a = polygon[current];
            var b = polygon[previous];
            if ((a.Y > point.Y) == (b.Y > point.Y)) continue;
            var crossingX = (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X;
            if (point.X < crossingX) inside = !inside;
        }
        return inside;
    }

    private static bool SceneMaskSelectionPointOnBoundary(PointF point, IReadOnlyList<PointF> polygon)
    {
        if (polygon.Count < 2) return false;
        const double epsilonSquared = 0.000001d;
        for (var index = 0; index < polygon.Count; index++)
        {
            var start = polygon[index];
            var end = polygon[(index + 1) % polygon.Count];
            var dx = (double)end.X - start.X;
            var dy = (double)end.Y - start.Y;
            var lengthSquared = dx * dx + dy * dy;
            var parameter = lengthSquared <= epsilonSquared
                ? 0d
                : Math.Clamp(
                    ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared,
                    0d,
                    1d);
            var offsetX = point.X - (start.X + dx * parameter);
            var offsetY = point.Y - (start.Y + dy * parameter);
            if (offsetX * offsetX + offsetY * offsetY <= epsilonSquared) return true;
        }
        return false;
    }

    private static IReadOnlyList<DrawingObjectInstanceDefinition> FindVisibleCompositionInstances(
        SceneCompositionResult compositionResult,
        IReadOnlyList<DrawingObjectInstanceDefinition> instances)
    {
        ArgumentNullException.ThrowIfNull(compositionResult);
        ArgumentNullException.ThrowIfNull(instances);

        var visibleIds = compositionResult.ObjectOwners.Values
            .Select(owner => string.IsNullOrWhiteSpace(owner.RootInstanceId) ? owner.InstanceId : owner.RootInstanceId)
            .ToHashSet(StringComparer.Ordinal);
        return instances.Where(instance => visibleIds.Contains(instance.Id)).ToArray();
    }

    private bool TryGetSelectedSceneInstanceBounds(out RectangleF bounds)
    {
        bounds = RectangleF.Empty;
        if (SelectedSceneInstances().Count == 0) return false;
        if (_selectedSceneInstanceObjectIndices.Length == 0) RefreshSelectedSceneInstanceObjectIndices();
        var compositionScene = ActiveInstanceCompositionScene();
        var found = false;
        foreach (var index in _selectedSceneInstanceObjectIndices)
        {
            if ((uint)index >= compositionScene.ObjectCount) continue;
            var objectBounds = compositionScene.GetObjectWorldBounds(index);
            bounds = found ? RectangleF.Union(bounds, objectBounds) : objectBounds;
            found = true;
        }

        return found && bounds.Width > 0.001f && bounds.Height > 0.001f;
    }

    private bool TryGetSelectedSceneInstanceFrame(out TransformOverlayFrame frame)
    {
        frame = default;
        var instances = SelectedSceneInstances();
        if (instances.Count != 1) return false;
        if (_selectedSceneInstanceObjectIndices.Length == 0) RefreshSelectedSceneInstanceObjectIndices();

        var state = instances[0].EvaluateState(_frame);
        var linear = DrawingObjectInstanceDefinition.CreateLinearTransform(state);
        if (!Matrix3x2.Invert(linear, out var inverse)) return false;

        var compositionScene = ActiveInstanceCompositionScene();
        var minimum = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
        var maximum = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
        foreach (var objectIndex in _selectedSceneInstanceObjectIndices)
        {
            if ((uint)objectIndex >= compositionScene.ObjectCount) continue;
            var contours = compositionScene.GetObjectBoundaryContours(objectIndex);
            if (contours.Length == 0)
            {
                var bounds = compositionScene.GetObjectWorldBounds(objectIndex);
                contours = [[
                    new PointF(bounds.Left, bounds.Top),
                    new PointF(bounds.Right, bounds.Top),
                    new PointF(bounds.Right, bounds.Bottom),
                    new PointF(bounds.Left, bounds.Bottom)
                ]];
            }

            foreach (var point in contours.SelectMany(contour => contour))
            {
                var local = Vector2.Transform(new Vector2(point.X - state.X, point.Y - state.Y), inverse);
                minimum = Vector2.Min(minimum, local);
                maximum = Vector2.Max(maximum, local);
            }
        }

        if (!float.IsFinite(minimum.X)
            || !float.IsFinite(minimum.Y)
            || !float.IsFinite(maximum.X)
            || !float.IsFinite(maximum.Y)
            || maximum.X - minimum.X <= 0.001f
            || maximum.Y - minimum.Y <= 0.001f)
        {
            return false;
        }

        var origin = Vector2.Transform(minimum, linear) + new Vector2(state.X, state.Y);
        var right = Vector2.Transform(new Vector2(maximum.X, minimum.Y), linear) + new Vector2(state.X, state.Y);
        var bottom = Vector2.Transform(new Vector2(minimum.X, maximum.Y), linear) + new Vector2(state.X, state.Y);
        frame = new TransformOverlayFrame(
            new PointF(origin.X, origin.Y),
            new PointF(right.X - origin.X, right.Y - origin.Y),
            new PointF(bottom.X - origin.X, bottom.Y - origin.Y));
        return frame.IsValid;
    }

    private void RefreshSelectedSceneInstanceObjectIndices()
    {
        if (_selectedSceneInstanceIds.Count == 0)
        {
            _selectedSceneInstanceObjectIndices = [];
            return;
        }

        var compositionScene = ActiveInstanceCompositionScene();
        var compositionResult = ActiveInstanceCompositionResult();
        var indices = new List<int>();
        for (var index = 0; index < compositionScene.ObjectCount; index++)
        {
            if (!compositionResult.TryGetOwner(index, out var owner)) continue;
            var rootInstanceId = string.IsNullOrWhiteSpace(owner.RootInstanceId) ? owner.InstanceId : owner.RootInstanceId;
            if (_selectedSceneInstanceIds.Contains(rootInstanceId)) indices.Add(index);
        }

        _selectedSceneInstanceObjectIndices = indices.ToArray();
    }

    private bool IsPointerInsideSelectedSceneInstance(PointF world)
    {
        if (!TryGetSelectedSceneInstanceBounds(out var bounds)) return false;
        var tolerance = SelectionToleranceWorld();
        bounds.Inflate(tolerance, tolerance);
        if (!bounds.Contains(world)) return false;

        var compositionScene = ActiveInstanceCompositionScene();
        if (!UsesFlatSceneMaskSelectionClipping(compositionScene)) return true;
        if (_selectedSceneInstanceObjectIndices.Length == 0) RefreshSelectedSceneInstanceObjectIndices();
        foreach (var objectIndex in _selectedSceneInstanceObjectIndices)
        {
            if (!TryGetSceneMaskSelectionVisibleBounds(
                    compositionScene,
                    objectIndex,
                    _stage.SceneCompositionMaskClips,
                    out var visibleBounds))
            {
                continue;
            }
            visibleBounds.Inflate(tolerance, tolerance);
            if (visibleBounds.Contains(world)
                && SceneMaskSelectionPointVisible(
                    compositionScene,
                    objectIndex,
                    world,
                    _stage.SceneCompositionMaskClips))
            {
                return true;
            }
        }
        return false;
    }

    private void SetSceneInstanceSelection(DrawingObjectInstanceDefinition instance, bool additive = false)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (!string.Equals(_selectedSceneInstanceId, instance.Id, StringComparison.Ordinal)) _transformFocus = null;
        if (!additive) _selectedSceneInstanceIds.Clear();
        _selectedSceneInstanceIds.Add(instance.Id);
        _selectedSceneInstanceId = instance.Id;
        if (!additive)
        {
            _selectedObjects.Clear();
            _selectedElements.Clear();
            _selectedObject = -1;
            _selectedElement = DrawingElementHit.None;
        }
        SyncSelectionToStage();
        RefreshSelectedSceneInstanceObjectIndices();
        UpdateSceneInstanceSelectionOverlay();
    }

    private void SetSceneInstanceSelection(
        IEnumerable<DrawingObjectInstanceDefinition> instances,
        DrawingObjectInstanceDefinition? primary = null)
    {
        SetSceneInstanceSelectionCore(instances, primary, preserveDrawingSelection: false);
    }

    private void SetSceneInstanceSelectionCore(
        IEnumerable<DrawingObjectInstanceDefinition> instances,
        DrawingObjectInstanceDefinition? primary,
        bool preserveDrawingSelection)
    {
        var selected = instances
            .Where(instance => instance is not null)
            .DistinctBy(instance => instance.Id, StringComparer.Ordinal)
            .ToArray();
        if (selected.Length == 0)
        {
            ClearSelection();
            return;
        }

        _selectedSceneInstanceIds.Clear();
        foreach (var instance in selected) _selectedSceneInstanceIds.Add(instance.Id);
        var nextPrimary = primary is not null && _selectedSceneInstanceIds.Contains(primary.Id)
            ? primary
            : selected[^1];
        if (!string.Equals(_selectedSceneInstanceId, nextPrimary.Id, StringComparison.Ordinal)) _transformFocus = null;
        _selectedSceneInstanceId = nextPrimary.Id;
        if (!preserveDrawingSelection)
        {
            _selectedObjects.Clear();
            _selectedElements.Clear();
            _selectedObject = -1;
            _selectedElement = DrawingElementHit.None;
        }
        SyncSelectionToStage();
        RefreshSelectedSceneInstanceObjectIndices();
        UpdateSceneInstanceSelectionOverlay();
    }

    private void SetMixedSelection(
        IEnumerable<int> objectIndices,
        IEnumerable<DrawingObjectInstanceDefinition> instances,
        DrawingObjectInstanceDefinition? primaryInstance = null)
    {
        SetSelection(objectIndices);
        var selectedInstances = instances.DistinctBy(instance => instance.Id, StringComparer.Ordinal).ToArray();
        if (selectedInstances.Length > 0)
        {
            SetSceneInstanceSelectionCore(
                selectedInstances,
                primaryInstance,
                preserveDrawingSelection: true);
        }
    }

    private void SetMixedSelection(
        IEnumerable<DrawingElementHit> hits,
        DrawingElementHit primary,
        IEnumerable<DrawingObjectInstanceDefinition> instances,
        DrawingObjectInstanceDefinition? primaryInstance = null)
    {
        SetSelection(hits, primary);
        var selectedInstances = instances.DistinctBy(instance => instance.Id, StringComparer.Ordinal).ToArray();
        if (selectedInstances.Length > 0)
        {
            SetSceneInstanceSelectionCore(
                selectedInstances,
                primaryInstance,
                preserveDrawingSelection: true);
        }
    }

    private void ClearSceneInstanceSelection()
    {
        if (_spatialTransformKeyboardActive) CancelSpatialTransformKeyboard();
        _selectedSceneInstanceId = "";
        _selectedSceneInstanceIds.Clear();
        _selectedSceneInstanceObjectIndices = [];
        _sceneInstanceMoveActive = false;
        _transformFocus = null;
        _stage.SetDrawingObjectSelectionOverlay(RectangleF.Empty);
        _stage.ClearReference3DSelection();
        _stage.ClearSpatialTransformGizmo();
    }

    private void UpdateSceneInstanceSelectionOverlay()
    {
        if (UpdateProjectedSceneSelectionPresentation()) return;
        if (!TryGetSelectedSceneInstanceBounds(out var bounds))
        {
            _stage.SetDrawingObjectSelectionOverlay(RectangleF.Empty);
            return;
        }

        _stage.SetDrawingObjectSelectionOverlay(
            bounds,
            SelectedSceneInstance()?.EvaluatePosition(_frame));
    }

    private void SetSelection(int objectIndex, bool deferPresentation = false)
    {
        ClearSceneInstanceSelection();
        _transformFocus = null;
        _selectedObjects.Clear();
        _selectedElements.Clear();
        _selectedObject = _scene.IsObjectSelectable(objectIndex, _frame) ? objectIndex : -1;
        _selectedElement = DrawingElementHit.None;
        if (_selectedObject >= 0) _selectedObjects.Add(_selectedObject);
        SyncSelectionToStage(deferPresentation);
    }

    private void SetSelection(DrawingElementHit hit, bool deferPresentation = false)
    {
        ClearSceneInstanceSelection();
        _transformFocus = null;
        if (!hit.IsValid
            || (uint)hit.Key.ObjectIndex >= _scene.ObjectCount
            || !_scene.IsObjectSelectable(hit.Key.ObjectIndex, _frame))
        {
            ClearSelection(deferPresentation);
            return;
        }

        if (IsWholeObjectOnlyObject(hit.Key.ObjectIndex))
        {
            SetSelection(hit.Key.ObjectIndex, deferPresentation);
            return;
        }

        _selectedObjects.Clear();
        _selectedElements.Clear();
        _selectedObject = hit.Key.ObjectIndex;
        _selectedElement = hit;
        _selectedObjects.Add(_selectedObject);
        _selectedElements.Add(hit);
        SyncSelectionToStage(deferPresentation);
    }

    private void AddToSelection(DrawingElementHit hit, bool deferPresentation = false)
    {
        if (!hit.IsValid || (uint)hit.Key.ObjectIndex >= _scene.ObjectCount) return;
        var retainedInstances = SelectedSceneInstances().ToArray();
        var retainedPrimaryInstance = SelectedSceneInstance();
        if (IsWholeObjectOnlyObject(hit.Key.ObjectIndex))
        {
            var importedObjects = _selectedObjects.ToList();
            if (!importedObjects.Remove(hit.Key.ObjectIndex)) importedObjects.Add(hit.Key.ObjectIndex);
            SetMixedSelection(importedObjects, retainedInstances, retainedPrimaryInstance);
            return;
        }
        if (_selectedElements.Count > 0)
        {
            var elements = _selectedElements.ToList();
            var existing = elements.FindIndex(selected => selected.Key == hit.Key);
            if (existing >= 0) elements.RemoveAt(existing);
            else elements.Add(hit);

            if (elements.Count == 0)
            {
                SetMixedSelection([], retainedInstances, retainedPrimaryInstance);
                return;
            }

            SetMixedSelection(
                elements,
                existing >= 0 ? elements[^1] : hit,
                retainedInstances,
                retainedPrimaryInstance);
            return;
        }

        var objects = _selectedObjects.ToList();
        var objectPosition = objects.IndexOf(hit.Key.ObjectIndex);
        if (objectPosition >= 0) objects.RemoveAt(objectPosition);
        else objects.Add(hit.Key.ObjectIndex);

        if (objects.Count == 0)
        {
            SetMixedSelection([], retainedInstances, retainedPrimaryInstance);
            return;
        }

        SetMixedSelection(objects, retainedInstances, retainedPrimaryInstance);
    }

    private void SetSelection(IEnumerable<int> objectIndices, bool deferPresentation = false)
    {
        ClearSceneInstanceSelection();
        _transformFocus = null;
        _selectedObjects.Clear();
        _selectedElements.Clear();
        _selectedElement = DrawingElementHit.None;
        var seen = new HashSet<int>();
        foreach (var index in objectIndices)
        {
            if (!_scene.IsObjectSelectable(index, _frame) || !seen.Add(index)) continue;
            _selectedObjects.Add(index);
        }

        _selectedObject = _selectedObjects.Count > 0 ? _selectedObjects[_selectedObjects.Count - 1] : -1;
        SyncSelectionToStage(deferPresentation);
    }

    private bool SelectAllObjectsInCurrentFrame()
    {
        var objectIndices = Array.Empty<int>();
        if (!IsSceneCompositionContext())
        {
            objectIndices = FindSelectableObjectIndices(_scene, _frame);
        }

        var editableInstances = ActiveEditableInstances();
        if (!IsSceneCompositionContext())
        {
            editableInstances = editableInstances
                .Where(instance =>
                {
                    var layer = Array.IndexOf(_scene.LayerIds, instance.SceneLayerId);
                    return layer >= 0 && !_scene.IsLayerEffectivelyLocked(layer);
                })
                .ToArray();
        }

        var visibleInstances = FindVisibleCompositionInstances(
            ActiveInstanceCompositionResult(),
            editableInstances);
        if (visibleInstances.Count > 0)
        {
            SetMixedSelection(objectIndices, visibleInstances, visibleInstances[^1]);
        }
        else if (objectIndices.Length > 0)
        {
            SetSelection(objectIndices);
        }
        else
        {
            ClearSelection();
        }

        UpdateInspector();
        _stage.Invalidate();
        return true;
    }

    private static int[] FindSelectableObjectIndices(VectorScene scene, int frame)
    {
        ArgumentNullException.ThrowIfNull(scene);
        return Enumerable.Range(0, scene.ObjectCount)
            .Where(index => scene.IsObjectSelectable(index, frame))
            .ToArray();
    }

    private void SetSelection(
        IEnumerable<DrawingElementHit> hits,
        DrawingElementHit primary = default,
        bool deferPresentation = false)
    {
        ClearSceneInstanceSelection();
        _transformFocus = null;
        _selectedObjects.Clear();
        _selectedElements.Clear();
        var seenKeys = new HashSet<DrawingElementKey>();
        var seenObjects = new HashSet<int>();
        foreach (var hit in hits)
        {
            if (!hit.IsValid
                || (uint)hit.Key.ObjectIndex >= _scene.ObjectCount
                || !_scene.IsObjectSelectable(hit.Key.ObjectIndex, _frame)
                || !seenKeys.Add(hit.Key))
            {
                continue;
            }

            if (seenObjects.Add(hit.Key.ObjectIndex)) _selectedObjects.Add(hit.Key.ObjectIndex);
            if (!IsWholeObjectOnlyObject(hit.Key.ObjectIndex)) _selectedElements.Add(hit);
        }

        _selectedElement = primary.IsValid
            && !IsWholeObjectOnlyObject(primary.Key.ObjectIndex)
            && _selectedElements.Any(hit => hit.Key == primary.Key)
            ? _selectedElements.First(hit => hit.Key == primary.Key)
            : _selectedElements.Count > 0 ? _selectedElements[^1] : DrawingElementHit.None;
        _selectedObject = primary.IsValid && _selectedObjects.Contains(primary.Key.ObjectIndex)
            ? primary.Key.ObjectIndex
            : _selectedElement.IsValid
                ? _selectedElement.Key.ObjectIndex
                : _selectedObjects.Count > 0 ? _selectedObjects[^1] : -1;
        SyncSelectionToStage(deferPresentation);
    }

    private bool IsImportedSvgObject(int objectIndex)
    {
        return (uint)objectIndex < _scene.ObjectCount
            && _scene.ShapeKind[objectIndex] == ShapeKind.ImportedSvg;
    }

    private bool IsWholeObjectOnlyObject(int objectIndex)
    {
        if ((uint)objectIndex >= _scene.ObjectCount) return false;
        var shape = _scene.ShapeKind[objectIndex];
        return shape is ShapeKind.ImportedSvg or ShapeKind.Text
            || shape == ShapeKind.MixingStroke
                && !_scene.TryGetMixingBrushLocalRegion(objectIndex, out _);
    }

    private void SyncSelectionToStage(bool deferPresentation = false)
    {
        var selectionChanged = _stage.SetSelectionState(
            _selectedObjects,
            _selectedObject,
            _selectedElements,
            _selectedElement);
        if (!IsSceneBuildingContext())
        {
            SyncTimelineLayerToPrimarySelection(_scene, _timeline, _selectedObject);
        }
        if (deferPresentation)
        {
            if (selectionChanged) QueueDeferredPresentationRefresh(DeferredPresentationRefresh.All);
            return;
        }

        CancelDeferredPresentationRefresh();
        UpdateTraditionalPenPathHandleOverlay();
        UpdateTransformOverlay();
        UpdateGradientOverlay();
        UpdateFillEdgeBezierOverlay();
    }

    internal static bool SyncTimelineLayerToPrimarySelection(
        VectorScene scene,
        TimelineStrip timeline,
        int primaryObject)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(timeline);
        if ((uint)primaryObject >= scene.ObjectCount) return false;

        var layer = (int)scene.ObjectLayer[primaryObject];
        if ((uint)layer >= scene.LayerCount) return false;
        return timeline.SelectSingleLayerTarget(scene.LayerIds[layer]);
    }

    private void UpdateTraditionalPenPathHandleOverlay()
    {
        _stage.SetPenPathHandlesVisible(HasTraditionalPenPathSelection());
    }

    private void UpdateTransformOverlay()
    {
        Matrix4x4? referenceTransform = null;
        if (IsSceneReferenceView())
        {
            UpdateProjectedSceneSelectionPresentation();
            if (!IsProjectedScene2DTransformView())
            {
                _stage.SetTransformOverlay(false, RectangleF.Empty);
                _stage.SetDistortOverlay(false, default);
                return;
            }
            referenceTransform = ProjectedSceneTransformOverlay();
        }

        var bounds = RectangleF.Empty;
        TransformOverlayFrame? orientedFrame = null;
        if (_tool is ToolMode.Transform or ToolMode.Distort)
        {
            if (SelectedSceneInstance() is not null)
            {
                if (TryGetSelectedSceneInstanceFrame(out var frame))
                {
                    orientedFrame = frame;
                    bounds = frame.Bounds;
                }
                else
                {
                    TryGetSelectedSceneInstanceBounds(out bounds);
                }
            }
            else foreach (var objectIndex in _selectedObjects)
            {
                if ((uint)objectIndex >= _scene.ObjectCount || !_scene.IsObjectActive(objectIndex, _frame)) continue;
                var objectBounds = TryGetSelectedMixingPartBounds(objectIndex, out var partBounds)
                    ? partBounds
                    : _scene.GetObjectWorldBounds(objectIndex);
                bounds = bounds.IsEmpty ? objectBounds : RectangleF.Union(bounds, objectBounds);
            }
        }

        if (_tool == ToolMode.Distort)
        {
            var envelope = _activeTransformHandle != TransformHandleKind.None
                || _distortVisualHandleActive
                ? _distortCurrentEnvelope
                : TryResolveDistortWarp(out var persistedWarp)
                    ? persistedWarp.Envelope
                    : bounds.IsEmpty ? default : DistortEnvelope.FromBounds(bounds);
            _stage.SetTransformOverlay(false, RectangleF.Empty);
            _stage.SetDistortOverlay(
                !bounds.IsEmpty && envelope.IsValid,
                envelope,
                referenceTransform);
            return;
        }

        _stage.SetDistortOverlay(false, default);
        if (orientedFrame is { } selectedFrame)
        {
            _stage.SetTransformOverlay(
                _tool == ToolMode.Transform,
                selectedFrame,
                ActiveTransformFocus(selectedFrame),
                referenceTransform);
        }
        else
        {
            _stage.SetTransformOverlay(
                _tool == ToolMode.Transform,
                bounds,
                bounds.IsEmpty ? null : ActiveTransformFocus(bounds),
                referenceTransform);
        }
    }

    private bool TryGetSelectedMixingPartBounds(int objectIndex, out RectangleF bounds)
    {
        bounds = RectangleF.Empty;
        if (!_scene.TryGetMixingBrushLocalRegion(objectIndex, out _)) return false;

        var hasPoint = false;
        var left = float.MaxValue;
        var top = float.MaxValue;
        var right = float.MinValue;
        var bottom = float.MinValue;
        foreach (var hit in _selectedElements)
        {
            if (hit.Key.ObjectIndex != objectIndex || hit.Key.Kind != DrawingElementKind.Fill) continue;
            foreach (var contour in _scene.GetFillPartContours(hit, _frame))
            {
                foreach (var point in contour)
                {
                    if (!float.IsFinite(point.X) || !float.IsFinite(point.Y)) continue;
                    hasPoint = true;
                    left = Math.Min(left, point.X);
                    top = Math.Min(top, point.Y);
                    right = Math.Max(right, point.X);
                    bottom = Math.Max(bottom, point.Y);
                }
            }
        }

        if (!hasPoint) return false;
        bounds = RectangleF.FromLTRB(left, top, right, bottom);
        return !bounds.IsEmpty;
    }

    private void CancelStageSelection()
    {
        if (RestoreUneditedMarqueeMaterialization())
        {
            ClearSelection();
            _hierarchyPanel.RefreshScene();
            return;
        }

        var linesChanged = MergeCompatibleLinesAfterDrawingOperation();
        ClearSelection();
        if (linesChanged) _hierarchyPanel.RefreshScene();
    }

    private bool RestoreUneditedMarqueeMaterialization()
    {
        var session = _marqueeMaterializationSession;
        _marqueeMaterializationSession = null;
        if (session is null || !ReferenceEquals(session.Scene, _scene)) return false;

        if (_undoStack.TryPeek(out var undo) && ReferenceEquals(undo.Snapshot, session.Snapshot)) _undoStack.Pop();
        _scene.RestoreSnapshot(session.Snapshot);
        _scene.EditFrame = _frame;
        _geometryDirty = false;
        _stage.ClearHoveredLineElement();
        return true;
    }

    private bool CanBreakApartObject(int objectIndex)
    {
        if ((uint)objectIndex >= _scene.ObjectCount || !_scene.IsObjectActive(objectIndex, _frame))
        {
            return false;
        }
        var shape = _scene.ShapeKind[objectIndex];
        if (shape is not (ShapeKind.ImportedSvg or ShapeKind.Text)) return false;
        if (shape == ShapeKind.Text
            && (!_scene.TryGetTextWorldContours(objectIndex, out var contours) || contours.Length == 0))
        {
            return false;
        }

        var layer = _scene.ObjectLayer[objectIndex];
        return _scene.GetLayerKind(layer) == DrawingLayerKind.Drawing
            && !_scene.IsLayerEffectivelyLocked(layer);
    }

    private bool CanBreakApartInstance(DrawingObjectInstanceDefinition instance)
    {
        var container = ActiveDrawingObject();
        if (container is null || !container.Instances.Any(candidate => candidate.Id == instance.Id)) return false;
        var layer = Array.IndexOf(container.Scene.LayerIds, instance.SceneLayerId);
        return layer >= 0
            && container.Scene.GetLayerKind(layer) == DrawingLayerKind.Drawing
            && !container.Scene.IsLayerEffectivelyLocked(layer)
            && CanFlattenDrawingObject(instance.DrawingObjectId, new HashSet<string>(StringComparer.Ordinal));
    }

    private bool CanFlattenDrawingObject(string drawingObjectId, ISet<string> visited)
    {
        if (!visited.Add(drawingObjectId)) return false;
        try
        {
            var drawingObject = _drawingObjects.FirstOrDefault(item => item.Id == drawingObjectId);
            return drawingObject is not null
                && !drawingObject.Scene.HasLayerEffects
                && drawingObject.Instances.All(instance => CanFlattenDrawingObject(instance.DrawingObjectId, visited));
        }
        finally
        {
            visited.Remove(drawingObjectId);
        }
    }

    private void BreakApartStageSelection()
    {
        if (_workspaceTabs.SelectedView != WorkspaceView.BasicDrawing
            || DrawingToolsBlocked()
            || IsSceneCompositionContext())
        {
            return;
        }
        FinishPointerInteractionForFrameChange();
        if (_stageContextMenuBreakApartInstances.Length > 0) BreakApartNestedInstances();
        else if (_stageContextMenuBreakApartObjects.Length > 0) BreakApartDrawingObjects();
    }

    private void BreakApartDrawingObjects()
    {
        var targets = _stageContextMenuBreakApartObjects
            .Where(CanBreakApartObject)
            .Distinct()
            .ToArray();
        if (targets.Length != _stageContextMenuBreakApartObjects.Length || targets.Length == 0) return;

        var targetDescriptors = targets
            .Select(index => (
                Key: new DrawingStackKey(_scene.ObjectOrder[index], _scene.ObjectSubOrder[index]),
                Shape: _scene.ShapeKind[index]))
            .ToArray();
        var layers = targets.Select(index => (int)_scene.ObjectLayer[index]).Distinct().ToArray();
        var wasProjectDirty = _projectDirty;
        var snapshot = CreateCanvasMutationSnapshot(targets, layers);
        var producedKeys = new List<DrawingStackKey>(targets.Length);
        var approximations = ImportedSvgBreakApproximation.None;
        try
        {
            var materializedTargets = targetDescriptors
                .Select(item => FindActiveObjectByStackKey(item.Key))
                .Where(index => CanBreakApartObject(index))
                .ToArray();
            if (materializedTargets.Length != targets.Length)
            {
                throw new InvalidOperationException("The selected objects changed before Break Apart could run.");
            }

            var importedTargets = targetDescriptors
                .Where(item => item.Shape == ShapeKind.ImportedSvg)
                .Select(item => FindActiveObjectByStackKey(item.Key))
                .ToArray();
            if (importedTargets.Length > 0)
            {
                var result = _scene.BreakApartImportedSvgObjects(importedTargets);
                producedKeys.AddRange(result.ProducedObjects.Select(index =>
                    new DrawingStackKey(_scene.ObjectOrder[index], _scene.ObjectSubOrder[index])));
                approximations |= result.Approximations;
            }

            var textTargets = targetDescriptors
                .Where(item => item.Shape == ShapeKind.Text)
                .Select(item => FindActiveObjectByStackKey(item.Key))
                .ToArray();
            if (textTargets.Length > 0)
            {
                var textObjects = _scene.BreakApartTextObjects(textTargets);
                if (textObjects.Length != textTargets.Length)
                {
                    throw new InvalidOperationException("One or more text objects could not be broken apart.");
                }
                producedKeys.AddRange(textObjects.Select(index =>
                    new DrawingStackKey(_scene.ObjectOrder[index], _scene.ObjectSubOrder[index])));
            }
        }
        catch (Exception exception)
        {
            RestoreCanvasMutationSnapshot(snapshot);
            SetProjectDirty(wasProjectDirty);
            ShowBreakApartError(exception);
            return;
        }

        PushUndoSnapshot(snapshot);
        SetSelection(producedKeys.Select(FindActiveObjectByStackKey).Where(index => index >= 0));
        RefreshBreakApartPresentation();
        if (approximations != ImportedSvgBreakApproximation.None)
        {
            AppLog.Info($"Break Apart SVG approximations: {approximations}");
        }
    }

    private void BreakApartNestedInstances()
    {
        var container = ActiveDrawingObject();
        if (container is null) return;
        var requestedIds = _stageContextMenuBreakApartInstances.ToHashSet(StringComparer.Ordinal);
        var instances = container.Instances
            .Where(instance => requestedIds.Contains(instance.Id))
            .ToArray();
        if (instances.Length != requestedIds.Count || instances.Length == 0 || instances.Any(instance => !CanBreakApartInstance(instance)))
        {
            return;
        }

        var prepared = new List<(DrawingObjectInstanceDefinition Instance, int HostLayer, VectorScene Scene)>(instances.Length);
        try
        {
            foreach (var instance in instances)
            {
                var hostLayer = Array.IndexOf(container.Scene.LayerIds, instance.SceneLayerId);
                var flattened = new VectorScene();
                SceneCompositionBuilder.BuildDrawingObjectInstanceForBreakApart(
                    flattened,
                    container,
                    instance,
                    _drawingObjects,
                    _frame,
                    _playbackSettings.Fps);
                if (flattened.ObjectCount == 0) throw new InvalidDataException("The selected instance has no visible geometry at the current frame.");
                prepared.Add((instance, hostLayer, flattened));
            }
        }
        catch (Exception exception)
        {
            ShowBreakApartError(exception);
            return;
        }

        var instanceSnapshot = container.CreateInstanceSnapshot();
        var wasProjectDirty = _projectDirty;
        var hostLayers = prepared.Select(item => item.HostLayer).Distinct().ToArray();
        var sceneSnapshot = CreateCanvasMutationSnapshot(affectedLayers: hostLayers);
        int[] produced;
        try
        {
            using (_scene.Timeline.BeginBatchUpdate())
            {
                foreach (var hostLayer in hostLayers) _scene.MaterializeAutoKeyframeInPlace(hostLayer, _frame);
            }

            var producedObjects = new List<int>();
            foreach (var item in prepared)
            {
                producedObjects.AddRange(_scene.AppendFlattenedSceneToLayer(item.Scene, item.HostLayer, _frame));
            }
            foreach (var item in prepared)
            {
                var hasFutureVisibleState = item.Instance.StateKeyframes.Any(keyframe =>
                    keyframe.Frame > 0 && keyframe.State.Visible);
                if (_frame == 0 && !hasFutureVisibleState)
                {
                    if (!_project.TryRemoveDrawingObjectInstance(container.Id, item.Instance.Id, out _))
                    {
                        throw new InvalidOperationException("A selected symbol instance could not be removed.");
                    }
                }
                else if (!item.Instance.SetStateAtFrame(
                             _frame,
                             item.Instance.EvaluateState(_frame) with { Visible = false }))
                {
                    throw new InvalidOperationException("A selected symbol instance could not be hidden at the current frame.");
                }
            }
            produced = producedObjects.ToArray();
        }
        catch (Exception exception)
        {
            container.RestoreInstanceSnapshot(instanceSnapshot);
            _scene.RestoreSnapshot(sceneSnapshot);
            _scene.EditFrame = _frame;
            SetProjectDirty(wasProjectDirty);
            var restored = container.Instances.Where(instance => requestedIds.Contains(instance.Id)).ToArray();
            if (restored.Length > 0) SetSceneInstanceSelection(restored, restored[^1]);
            RefreshBreakApartPresentation();
            ShowBreakApartError(exception);
            return;
        }

        PushUndoSnapshot(sceneSnapshot, container, instanceSnapshot);
        SetSelection(produced);
        RefreshBreakApartPresentation();
        AppLog.Info($"Broke apart {prepared.Count} nested symbol instance(s) into {produced.Length} local object(s)");
    }

    private void RefreshBreakApartPresentation()
    {
        _timeline.RefreshTimeline();
        _timeline.SelectModelActiveTrack();
        _hierarchyPanel.RefreshScene();
        RefreshDrawingObjectAssetPresentation();
        _stage.ClearHoveredLineElement();
        UpdateInspector();
        UpdateStatusBar();
        _stage.Invalidate();
    }

    private void ShowBreakApartError(Exception exception)
    {
        AppLog.Error($"Break Apart failed: {exception}");
        var message = UiLocalization.CurrentLanguage == UiLanguage.SimplifiedChinese
            ? $"无法拆散所选对象。\r\n\r\n{exception.Message}"
            : $"The selected objects could not be broken apart.\r\n\r\n{exception.Message}";
        ModernMessageDialog.Show(
            this,
            message,
            UiLocalization.T("Break Apart"),
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }

    private bool DeleteNestedDrawingObjectInstance(string instanceId)
    {
        if (IsSceneCompositionContext() || string.IsNullOrWhiteSpace(instanceId)) return false;
        var container = ActiveDrawingObject();
        if (container is null
            || !_project.TryRemoveDrawingObjectInstance(container.Id, instanceId, out var removed)
            || removed is null)
        {
            return false;
        }

        ClearSelection();
        _timeline.RefreshTimeline();
        RefreshDrawingObjectAssetPresentation();
        AppLog.Info($"Deleted nested symbol instance: {removed.Name}");
        return true;
    }

    private bool DeleteSelectedNestedDrawingObjectInstances()
    {
        if (IsSceneCompositionContext()) return false;
        var container = ActiveDrawingObject();
        var selected = SelectedSceneInstances();
        if (container is null || selected.Count == 0) return false;

        var removed = new List<DrawingObjectInstanceDefinition>(selected.Count);
        foreach (var instance in selected)
        {
            if (_project.TryRemoveDrawingObjectInstance(container.Id, instance.Id, out var deleted)
                && deleted is not null)
            {
                removed.Add(deleted);
            }
        }
        if (removed.Count == 0) return false;

        ClearSelection();
        _timeline.RefreshTimeline();
        RefreshDrawingObjectAssetPresentation();
        AppLog.Info($"Deleted {removed.Count} nested symbol instance(s)");
        return true;
    }

    private void ShowStageContextMenu(Point screen)
    {
        _stageContextMenuLocation = screen;
        _stageContextMenu.Show(_stage, screen);
    }

    private bool TryGetStageContextDrawingElement(Point screen, out DrawingElementHit hit)
    {
        hit = DrawingElementHit.None;
        if (DrawingToolsBlocked()) return false;
        var candidate = _scene.HitTestElement(
            _stage.ScreenToWorld(screen),
            _frame,
            SelectionToleranceWorld());
        if (!candidate.IsValid || !_scene.IsObjectSelectable(candidate.Key.ObjectIndex, _frame)) return false;
        hit = candidate;
        return true;
    }

    private bool TryGetStageContextLineObject(Point screen, out int objectIndex)
    {
        objectIndex = -1;
        if (DrawingToolsBlocked()) return false;

        var world = _stage.ScreenToWorld(screen);
        var hit = _scene.HitTestElement(world, _frame, SelectionToleranceWorld());
        if (hit.IsValid
            && hit.Key.Kind == DrawingElementKind.Stroke
            && _scene.CanConvertLineToFill(hit.Key.ObjectIndex, _frame))
        {
            objectIndex = hit.Key.ObjectIndex;
            return true;
        }

        objectIndex = _selectedObjects.FirstOrDefault(index => _scene.CanConvertLineToFill(index, _frame), -1);
        return objectIndex >= 0;
    }

    private bool TryGetStageContextNestedInstance(
        Point screen,
        out DrawingObjectInstanceDefinition instance)
    {
        instance = null!;
        if (IsSceneCompositionContext() || ActiveDrawingObject()?.Instances.Count is not > 0) return false;

        var world = _stage.ScreenToWorld(screen);
        var localHit = _scene.HitTestElement(world, _frame, SelectionToleranceWorld());
        if (localHit.IsValid && _scene.IsObjectSelectable(localHit.Key.ObjectIndex, _frame)) return false;
        return TryResolveSceneInstanceAt(world, out instance);
    }

    private void ConvertStageContextLineToFill()
    {
        if (DrawingToolsBlocked() || _stageContextMenuLineObject < 0) return;

        var selectedLines = _selectedObjects
            .Where(index => _scene.CanConvertLineToFill(index, _frame))
            .Distinct()
            .ToArray();
        var targets = selectedLines.Contains(_stageContextMenuLineObject)
            ? selectedLines
            : new[] { _stageContextMenuLineObject };
        if (targets.Length == 0) return;

        var snapshot = CreateCanvasMutationSnapshot(targets);
        var fillIndices = new List<int>(targets.Length);
        foreach (var target in targets)
        {
            if (_scene.TryConvertLineToFill(target, _frame, out var fillIndex))
            {
                fillIndices.Add(fillIndex);
                continue;
            }

            RestoreCanvasMutationSnapshot(snapshot);
            return;
        }

        PushUndoSnapshot(snapshot);
        SetSelection(fillIndices);
        _stage.ClearHoveredLineElement();
        _hierarchyPanel.RefreshScene();
        UpdateInspector();
        _stage.Invalidate();
    }

    private bool TrySetNestedInstanceMarqueeSelection(RectangleF bounds)
    {
        if (bounds.Width <= 0.001f || bounds.Height <= 0.001f) return false;
        var selected = ResolveMarqueeInstanceSelection(bounds);
        if (selected.Count == 0) return false;
        SetMixedSelection(
            _additiveSelection ? _marqueeSelectionBase : [],
            selected,
            selected[^1]);
        return true;
    }

    private IReadOnlyList<DrawingObjectInstanceDefinition> ResolveMarqueeInstanceSelection(RectangleF bounds)
    {
        var compositionScene = ActiveInstanceCompositionScene();
        var selected = FindCompositionInstancesInsideBounds(
            compositionScene,
            ActiveInstanceCompositionResult(),
            ActiveEditableInstances(),
            bounds,
            UsesFlatSceneMaskSelectionClipping(compositionScene)
                ? _stage.SceneCompositionMaskClips
                : null);
        if (_additiveSelection && _marqueeInstanceSelectionBase.Length > 0)
        {
            var baseIds = _marqueeInstanceSelectionBase.ToHashSet(StringComparer.Ordinal);
            selected = ActiveEditableInstances()
                .Where(instance => baseIds.Contains(instance.Id) || selected.Contains(instance))
                .ToArray();
        }
        return selected;
    }

    private void RestoreMarqueeBaseSelection()
    {
        if (!_additiveSelection)
        {
            ClearSelection();
            return;
        }

        var baseIds = _marqueeInstanceSelectionBase.ToHashSet(StringComparer.Ordinal);
        var instances = ActiveEditableInstances().Where(instance => baseIds.Contains(instance.Id)).ToArray();
        SetMixedSelection(
            _marqueeSelectionBase,
            instances,
            instances.Length > 0 ? instances[^1] : null);
    }

    private void MergeAndSimplifyLineSegments(Point screen)
    {
        if (DrawingToolsBlocked()) return;

        var targets = CollectLineMergeTargets(screen, out var lineWasHit);
        if (targets.Count < 2) return;
        if (lineWasHit) SetSelection(targets);

        var snapshot = CreateCanvasMutationSnapshot(targets);
        if (!MergeCompatibleLinesAfterDrawingOperation(targets))
        {
            RestoreCanvasMutationSnapshot(snapshot);
            return;
        }

        PushUndoSnapshot(snapshot);
        _hierarchyPanel.RefreshScene();
        UpdateInspector();
        _stage.Invalidate();
    }

    private HashSet<int> CollectLineMergeTargets(Point screen, out bool lineWasHit)
    {
        var world = _stage.ScreenToWorld(screen);
        var hit = _scene.HitTestElement(world, _frame, SelectionToleranceWorld());
        var targets = new HashSet<int>();
        lineWasHit = hit.IsValid
            && hit.Key.Kind == DrawingElementKind.Stroke
            && (uint)hit.Key.ObjectIndex < _scene.ObjectCount
            && _scene.ShapeKind[hit.Key.ObjectIndex] == ShapeKind.Line;
        if (lineWasHit)
        {
            foreach (var connected in _scene.GetConnectedStrokeElements(hit, _frame))
            {
                var objectIndex = connected.Key.ObjectIndex;
                if ((uint)objectIndex < _scene.ObjectCount && _scene.ShapeKind[objectIndex] == ShapeKind.Line)
                {
                    targets.Add(objectIndex);
                }
            }
        }
        else
        {
            foreach (var objectIndex in _selectedObjects)
            {
                if ((uint)objectIndex < _scene.ObjectCount && _scene.ShapeKind[objectIndex] == ShapeKind.Line)
                {
                    targets.Add(objectIndex);
                }
            }
        }

        IncludeMaterializedLineSiblings(targets);
        return targets;
    }

    private void IncludeMaterializedLineSiblings(HashSet<int> targets)
    {
        if (targets.Count == 0) return;
        var sourceOrders = targets
            .Where(index => (uint)index < _scene.ObjectCount)
            .Select(index => _scene.ObjectOrder[index])
            .ToHashSet();
        if (sourceOrders.Count == 0) return;

        for (var index = 0; index < _scene.ObjectCount; index++)
        {
            if (_scene.ShapeKind[index] != ShapeKind.Line
                || !_scene.IsObjectActive(index, _frame)
                || !sourceOrders.Contains(_scene.ObjectOrder[index]))
            {
                continue;
            }

            targets.Add(index);
        }
    }

    private void ClearSelection(bool deferPresentation = false) => SetSelection(-1, deferPresentation);

    private void ClearInactiveSelection()
    {
        if (_selectedObjects.Any(index => !_scene.IsObjectSelectable(index, _frame)))
        {
            ClearSelection();
        }
    }

    private PointF SnapDrawingPoint(PointF point)
    {
        return _drawSettings.SnapPoint(point, _stage.AdaptiveGridSnapStep);
    }

    private void UpdateDrawingPreview(PointF start, PointF end, ToolMode tool)
    {
        var shape = ToolShapeKind(tool) ?? _drawSettings.ShapeKind;
        if (tool == ToolMode.Line)
        {
            end = ResolveDrawingLineEnd(start, end, temporarilySnapAngle: IsShiftPressed());
        }
        else
        {
            start = VectorUnits.Quantize(SnapDrawingPoint(start));
            end = VectorUnits.Quantize(SnapDrawingPoint(end));
            (start, end) = ResolveShapeDragBounds(
                start,
                end,
                _drawSettings.KeepAspectRatio || IsShiftPressed(),
                IsControlPressed());
        }

        var previewMaterial = ActiveDrawingPreviewMaterial(shape);
        _stage.SetDrawingPreview(
            start,
            end,
            shape,
            previewMaterial.Color,
            previewMaterial.Stroke,
            ShapeVertexCountFor(shape));
    }

    private (Color Color, float Stroke) ActiveDrawingPreviewMaterial(ShapeKind shape) =>
        ResolveDrawingPreviewMaterial(
            shape,
            ActiveColor(),
            ActiveStrokeColor(),
            ActiveStrokeUnits());

    internal static (Color Color, float Stroke) ResolveDrawingPreviewMaterial(
        ShapeKind shape,
        Color fillColor,
        Color strokeColor,
        float strokeUnits) =>
        (shape == ShapeKind.Line ? strokeColor : fillColor, strokeUnits);

    internal static (PointF Start, PointF End) ResolveShapeDragBounds(
        PointF anchor,
        PointF pointer,
        bool keepAspectRatio,
        bool fromCenter)
    {
        var dx = pointer.X - anchor.X;
        var dy = pointer.Y - anchor.Y;
        if (keepAspectRatio)
        {
            var side = Math.Max(Math.Abs(dx), Math.Abs(dy));
            dx = MathF.CopySign(side, dx == 0 ? 1 : dx);
            dy = MathF.CopySign(side, dy == 0 ? 1 : dy);
        }

        if (!fromCenter) return (anchor, new PointF(anchor.X + dx, anchor.Y + dy));
        return (
            new PointF(anchor.X - dx, anchor.Y - dy),
            new PointF(anchor.X + dx, anchor.Y + dy));
    }

    private int ShapeVertexCountFor(ShapeKind shape)
    {
        return shape switch
        {
            ShapeKind.Polygon => _drawSettings.PolygonSides,
            ShapeKind.Star => _drawSettings.StarPoints,
            _ => 0
        };
    }

}
