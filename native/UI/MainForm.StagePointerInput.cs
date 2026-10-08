using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

namespace VectorAnimationEngine;

internal sealed partial class MainForm : Form
{
    internal static bool AllowsPressureBrushPointerInput(WorkspaceView workspace, bool sceneMaskEditing)
    {
        return workspace == WorkspaceView.BasicDrawing
            || workspace == WorkspaceView.SceneEditor && sceneMaskEditing;
    }

    private void StagePenPointerInput(object? sender, StageControl.PenPointerEventArgs e)
    {
        if (e.Kind == StageControl.PenPointerEventKind.CaptureLost)
        {
            if (_tabletPressurePointerId != e.PointerId) return;
            e.Handled = true;
            CancelFreehandStroke();
            FinishPointerInteraction();
            _stage.Invalidate();
            return;
        }

        if (e.Kind == StageControl.PenPointerEventKind.Down)
        {
            if (_tool != ToolMode.PressureBrush
                || !AllowsPressureBrushPointerInput(
                    _workspaceTabs.SelectedView,
                    IsSceneMaskEditing())
                || DrawingToolsBlocked()
                || _spacePanHeld
                || _brushColorPaletteActive
                || _freehandDrawing
                || _tabletPressurePointerId is not null
                || IsTextEditActive() && !CommitTextEdit())
            {
                return;
            }

            var firstContactIndex = -1;
            for (var index = 0; index < e.Samples.Count; index++)
            {
                var sample = e.Samples[index];
                if (!sample.IsInContact
                    || sample.IsEraser
                    || sample.HasBarrelButton
                    || !float.IsFinite(sample.Pressure))
                {
                    continue;
                }

                firstContactIndex = index;
                break;
            }

            if (firstContactIndex < 0) return;

            e.Handled = true;
            _tabletPressurePointerId = e.PointerId;
            var firstContact = e.Samples[firstContactIndex];
            _tabletPressureLastValue = Math.Clamp(firstContact.Pressure, 0f, 1f);
            BeginTabletPressureStroke(firstContact.Location, _tabletPressureLastValue);
            AppendTabletPressureSamples(e.Samples, firstContactIndex + 1, updatePreview: true);
            return;
        }

        if (_tabletPressurePointerId != e.PointerId) return;
        e.Handled = true;
        AppendTabletPressureSamples(
            e.Samples,
            0,
            updatePreview: e.Kind != StageControl.PenPointerEventKind.Up);

        if (e.Kind != StageControl.PenPointerEventKind.Up) return;
        if (e.Samples.Count > 0 && float.IsFinite(_tabletPressureLastValue))
        {
            AppendFreehandSample(
                e.Samples[^1].Location,
                force: true,
                tabletPressure: _tabletPressureLastValue,
                updatePreview: false);
        }

        CommitFreehandStroke();
        FinishPointerInteraction();
    }

    private void BeginTabletPressureStroke(Point screen, float pressure)
    {
        _stage.Focus();
        _stage.Capture = true;
        _lastMouse = screen;
        _startScreen = screen;
        _startWorld = _stage.ScreenToWorld(screen);
        _pointerHitWasAlreadySelected = false;
        _independentMarqueeStrokeMove = false;
        _selectionWasEmptyOnPointerDown = _selectedObjects.Count == 0;
        _forceMarqueeOnPointerDown = false;
        _additiveSelection = false;
        _pendingClickSelection = DrawingElementHit.None;
        BeginFreehandStroke(screen, _startWorld.Value, pressure);
        _stage.SetBrushTipCursor(screen, _brushShape, ActiveStrokeUnits(), eraser: false);
    }

    private void AppendTabletPressureSamples(
        IReadOnlyList<StageControl.PenPointerSample> samples,
        int startIndex,
        bool updatePreview)
    {
        var samplesAppended = false;
        for (var index = Math.Max(0, startIndex); index < samples.Count; index++)
        {
            var sample = samples[index];
            if (!sample.IsInContact) continue;
            if (float.IsFinite(sample.Pressure))
            {
                _tabletPressureLastValue = Math.Clamp(sample.Pressure, 0f, 1f);
            }

            if (!float.IsFinite(_tabletPressureLastValue)) continue;
            _lastMouse = sample.Location;
            _stage.SetBrushTipCursor(sample.Location, _brushShape, ActiveStrokeUnits(), eraser: false);
            AppendFreehandSample(
                sample.Location,
                tabletPressure: _tabletPressureLastValue,
                updatePreview: false);
            samplesAppended = true;
        }

        if (samplesAppended && updatePreview) QueueFreehandPreview();
    }

    private void StageMouseDown(object? sender, MouseEventArgs e)
    {
        if (HandleReferenceCameraRightLookMouseDown(e)) return;
        CancelReferenceCameraKeyboardNavigation();
        _pendingHoverScreen = null;
        if (_tabletPressurePointerId is not null && e.Button == MouseButtons.Left) return;
        if (IsTextEditActive() && !CommitTextEdit()) return;
        _stage.Focus();
        FlushDeferredPresentationOverlaysForPointerHitTest();
        if (_brushColorPaletteActive)
        {
            UpdateBrushColorPaletteHover(e.Location);
            return;
        }

        if (!IsShotDirectorContext() && HandleSpatialTransformKeyboardMouseDown(e)) return;

        if (_spatialTransformPointerSession is not null)
        {
            if (e.Button == MouseButtons.Right) CancelSpatialTransformPointer();
            return;
        }

        if (_shotFramingPointerSession is not null)
        {
            if (e.Button == MouseButtons.Right) CancelShotFramingPointer();
            return;
        }

        if (_sceneLightGizmoPointerSession is not null)
        {
            if (e.Button == MouseButtons.Right) CancelSceneLightGizmoPointer();
            return;
        }

        if (_spacePanHeld && e.Button == MouseButtons.Left && BeginTemporaryCanvasPanPointer(e.Location)) return;

        if (IsSceneReferenceView() && MouseButtonDown(e, MouseButtons.Right) && IsAltPressed())
        {
            BeginGlobalViewDrag(e, referencePan: true);
            return;
        }

        if (MouseButtonDown(e, MouseButtons.Middle))
        {
            BeginGlobalViewDrag(e);
            return;
        }

        if (IsShotDirectorContext())
        {
            if (e.Button == MouseButtons.Left && TryBeginShotFramingGizmoPointer(e.Location)) return;
            // The director stage is a camera viewport. Navigation above remains available, while
            // every other click is consumed so scene instances cannot be selected or edited.
            return;
        }

        if (IsLassoTool(_tool) && BeginLassoPointer(e)) return;

        if (BeginMotionTrackPointer(e)) return;

        if (TryBeginMotionTrackMarquee(e.Location)) return;

        if (TryBeginProjectedScenePointer(e)) return;

        if (IsSceneCompositionContext())
        {
            BeginSceneCompositionPointer(e);
            return;
        }

        if (TryBeginNestedDrawingObjectPointer(e)) return;

        if (_tool == ToolMode.SnapPoint)
        {
            BeginSnapPointPointer(e);
            return;
        }

        if (_tool == ToolMode.Pen)
        {
            if (e.Button == MouseButtons.Left)
            {
                if (IsControlPressed() && TryBeginTraditionalPenAnchorEdit(e.Location)) return;
                if (_traditionalPenCurrentAnchor is null)
                {
                    if (TryBeginTraditionalPenSelectedHandleEdit(e.Location)) return;
                    if (TrySelectTraditionalPenPath(e.Location)) return;
                    if (HasTraditionalPenPathSelection()) ClearSelection();
                }
                BeginTraditionalPenPoint(e.Location);
            }
            else if (e.Button == MouseButtons.Right && !CancelTraditionalPenPath()) ShowStageContextMenu(e.Location);
            return;
        }

        if (_tool == ToolMode.SimplePen)
        {
            if (e.Button == MouseButtons.Left)
            {
                if (_penStartWorld is null && IsControlPressed() && TryInsertPenAnchor(e.Location)) return;
                BeginPenSegment(e.Location);
            }
            else if (e.Button == MouseButtons.Right && !CancelPenCurve()) ShowStageContextMenu(e.Location);
            return;
        }

        if (_tool == ToolMode.Select
            && IsControlPressed()
            && TryApplyFillEdgeBezierAnchorGesture(e))
        {
            return;
        }

        if (_tool == ToolMode.Select
            && IsControlPressed()
            && TryBeginCornerDrag(e))
        {
            return;
        }

        if (_tool == ToolMode.Select
            && IsAltPressed()
            && TryBeginLineBranchDrag(e))
        {
            return;
        }

        if (e.Button == MouseButtons.Right)
        {
            ShowStageContextMenu(e.Location);
            return;
        }

        if (_tool == ToolMode.Eyedropper)
        {
            ApplyEyedropper(e);
            return;
        }

        if (_tool == ToolMode.Text)
        {
            BeginTextToolEdit(e);
            return;
        }

        _stage.Capture = true;
        _lastMouse = e.Location;
        _startScreen = e.Location;
        _startWorld = _stage.ScreenToWorld(e.Location);
        if (_tool == ToolMode.Line) _startWorld = ResolveDrawingLineEndpoint(_startWorld.Value);
        _pointerHitWasAlreadySelected = false;
        _independentMarqueeStrokeMove = false;
        _selectionWasEmptyOnPointerDown = _selectedObjects.Count == 0;
        _forceMarqueeOnPointerDown = e.Button == MouseButtons.Left && (ModifierKeys & Keys.Control) == Keys.Control;
        _additiveSelection = SupportsMarqueeSelection(_tool) && e.Button == MouseButtons.Left && IsShiftPressed();
        _pendingClickSelection = DrawingElementHit.None;
        if (_tool is ToolMode.Transform or ToolMode.Distort)
        {
            if (_tool == ToolMode.Transform) BeginTransformPointer(e);
            else BeginDistortPointer(e);
            UpdateInteractionCursor(e.Location);
            return;
        }

        if (_tool == ToolMode.Eraser)
        {
            if (e.Button == MouseButtons.Left && _startWorld is { } eraserStart) BeginFreehandStroke(e.Location, eraserStart);
            else FinishPointerInteraction();
            return;
        }

        if (_tool == ToolMode.Gradient)
        {
            BeginGradientPointer(e);
            return;
        }

        if (_tool == ToolMode.Select
            && e.Button == MouseButtons.Left
            && !_forceMarqueeOnPointerDown
            && !_additiveSelection
            && TryResolveUnselectedFillEdgeCandidate(e.Location, out var candObj, out var candPart))
        {
            // Hovering an unselected fill shape's edge: defer the decision until the
            // pointer moves (bend, no UI) or is released (select, show UI).
            _fillEdgePreSelect = (candObj, candPart);
            UpdateInteractionCursor(e.Location);
            return;
        }

        if (_tool == ToolMode.Select)
        {
            var fillEdgeOverlayHit = _forceMarqueeOnPointerDown || _additiveSelection
                ? FillEdgeBezierOverlayHit.None
                : _stage.HitTestFillEdgeBezierOverlay(e.Location);
            var fillEdgeSceneHit = fillEdgeOverlayHit.IsValid
                && fillEdgeOverlayHit.Handle == EditHandleKind.None
                && _startWorld is { } fillEdgeWorld
                    ? _scene.HitTestElement(fillEdgeWorld, _frame, SelectionToleranceWorld())
                    : DrawingElementHit.None;
            if (FillEdgeOverlayOwnsPointer(fillEdgeOverlayHit, IsSelectableLineHit(fillEdgeSceneHit)))
            {
                _stage.ClearHoveredLineElement();
                BeginFillEdgeBezierPointer(e);
                return;
            }

            var hoveredHandle = _forceMarqueeOnPointerDown || _additiveSelection
                ? EditHandleKind.None
                : _stage.HitTestHoveredLineHandle(e.Location);
            var hoveredLine = _stage.HoveredLineElement;
            if (hoveredHandle != EditHandleKind.None && hoveredLine.IsValid)
            {
                _stage.ClearHoveredLineElement();
                hoveredLine = StageControl.ResolvePresentedBezierSourceHit(hoveredLine);
                SetSelection(hoveredLine, deferPresentation: true);
                _activeHandle = hoveredHandle;
                _forceMarqueeOnPointerDown = false;
                if (_selectedObject >= 0) CaptureEditStart(_selectedObject);
                if (TryResetSelectedBezierCurvature(e.Button, hoveredHandle)) return;
                _stage.InvalidateOverlay();
                UpdateInteractionCursor(e.Location);
                return;
            }

            _stage.ClearHoveredLineElement();
            if (!_additiveSelection && _selectedObject >= 0)
            {
                _activeHandle = _stage.HitTestHandle(e.Location, _selectedObject);
                if (_activeHandle != EditHandleKind.None)
                {
                    _forceMarqueeOnPointerDown = false;
                    CaptureEditStart(_selectedObject);
                    if (TryResetSelectedBezierCurvature(e.Button, _activeHandle)) return;
                    _stage.InvalidateOverlay();
                    UpdateInteractionCursor(e.Location);
                    return;
                }
            }

            if (_forceMarqueeOnPointerDown)
            {
                UpdateInteractionCursor(e.Location);
                return;
            }

            if (_startWorld is not { } startWorld) return;
            if (e.Button == MouseButtons.Left
                && !_additiveSelection
                && !_forceMarqueeOnPointerDown
                && TryBeginSelectedBoundarySegmentDetach(startWorld))
            {
                // Pressing the body of an already-selected boundary segment detaches it
                // as a freely movable stroke (the fill keeps its own outline), matching
                // the overlay body-drag detach. Bezier closest-point matching keeps this
                // working for curved segments where the point-in-shape hit resolves to
                // the fill part instead of the boundary stroke.
                _stage.Capture = true;
                _lastMouse = e.Location;
                _startScreen = e.Location;
                _startWorld = startWorld;
                _forceMarqueeOnPointerDown = false;
                _pendingClickSelection = DrawingElementHit.None;
                _stage.ClearHoveredLineElement();
                UpdateInteractionCursor(e.Location);
                return;
            }

            var hit = _scene.HitTestElement(startWorld, _frame, SelectionToleranceWorld());
            if (hit.IsValid && _scene.IsObjectSelectable(hit.Key.ObjectIndex, _frame))
            {
                // Body drag on an unselected line bends it (arc drag). Once the line is
                // selected — its orange path UI is showing — body drag must move it
                // instead, matching the detach-move rule for selected segments.
                var hitLineAlreadySelected = _selectedElements.Any(selected => selected.Key == hit.Key)
                    || (_selectedElements.Count == 0 && _selectedObjects.Contains(hit.Key.ObjectIndex));
                if (e.Button == MouseButtons.Left
                    && !_additiveSelection
                    && !hitLineAlreadySelected
                    && hit.Key.Kind == DrawingElementKind.Stroke
                    && _scene.ShapeKind[hit.Key.ObjectIndex] == ShapeKind.Line
                    && BeginArcDrag(startWorld, hit.Key.ObjectIndex))
                {
                    _stage.Capture = true;
                    _lastMouse = e.Location;
                    _startScreen = e.Location;
                    _startWorld = startWorld;
                    _forceMarqueeOnPointerDown = false;
                    _pendingClickSelection = DrawingElementHit.None;
                    _stage.ClearHoveredLineElement();
                    UpdateInteractionCursor(e.Location);
                    return;
                }

                if (_selectionWasEmptyOnPointerDown && e.Button == MouseButtons.Left && !_additiveSelection)
                {
                    _pendingClickSelection = hit;
                    _stage.InvalidateOverlay();
                    return;
                }

                var hitObject = hit.Key.ObjectIndex;
                var hitPartWasAlreadySelected = _selectedElements.Any(selected => selected.Key == hit.Key);
                var hitWholeObjectWasAlreadySelected = _selectedElements.Count == 0 && _selectedObjects.Contains(hitObject);
                _pointerHitWasAlreadySelected = hitPartWasAlreadySelected || hitWholeObjectWasAlreadySelected;
                if (_additiveSelection)
                {
                    AddToSelection(hit, deferPresentation: true);
                }
                else if (hitPartWasAlreadySelected)
                {
                    _selectedObject = hitObject;
                    _selectedElement = _selectedElements.First(selected => selected.Key == hit.Key);
                    SyncSelectionToStage(deferPresentation: true);
                }
                else if (hitWholeObjectWasAlreadySelected)
                {
                    _selectedObject = hitObject;
                    _selectedElement = DrawingElementHit.None;
                    SyncSelectionToStage(deferPresentation: true);
                }
                else
                {
                    SetSelection(hit, deferPresentation: true);
                }

                if (_selectedObject >= 0) CaptureEditStart(_selectedObject);
            }
            else
            {
                if (e.Button == MouseButtons.Left) BeginBlankMarqueeSelection(e.Location);
            }

            if (!_stage.MarqueeOverlayActive) _stage.InvalidateOverlay();
            UpdateInteractionCursor(e.Location);
        }
        else if (_tool == ToolMode.Fill)
        {
            if (e.Button != MouseButtons.Left || _startWorld is not { } fillWorld) return;
            var hit = _scene.HitTestElement(_startWorld.Value, _frame, SelectionToleranceWorld());
            if (TryApplyFillToLine(hit))
            {
                return;
            }

            if (hit.IsValid
                && _scene.IsObjectSelectable(hit.Key.ObjectIndex, _frame)
                && _scene.ShapeKind[hit.Key.ObjectIndex] == ShapeKind.Text)
            {
                var color = ActiveColor();
                var textObject = hit.Key.ObjectIndex;
                if (_scene.Argb[textObject] == color.ToArgb()) return;
                var key = new DrawingStackKey(_scene.ObjectOrder[textObject], _scene.ObjectSubOrder[textObject]);
                var snapshot = CreateCanvasMutationSnapshot([textObject]);
                textObject = FindActiveObjectByStackKey(key);
                if (textObject < 0 || _scene.ShapeKind[textObject] != ShapeKind.Text)
                {
                    RestoreCanvasMutationSnapshot(snapshot);
                    return;
                }

                _scene.Argb[textObject] = color.ToArgb();
                _scene.DisableLinearGradient(textObject);
                SetSelection(textObject);
                PushUndoSnapshot(snapshot);
                _hierarchyPanel.RefreshScene();
                UpdateInspector();
                ClearFillHoverPreview();
                _stage.Invalidate();
                return;
            }

            if (hit.IsValid
                && _scene.IsObjectSelectable(hit.Key.ObjectIndex, _frame)
                && hit.Key.Kind == DrawingElementKind.Fill
                && IsFillShape(_scene.ShapeKind[hit.Key.ObjectIndex]))
            {
                var color = ActiveColor();
                if (_scene.Argb[hit.Key.ObjectIndex] == color.ToArgb()) return;
                var snapshot = CreateCanvasMutationSnapshot([hit.Key.ObjectIndex]);
                var materialized = hit.Key.Kind == DrawingElementKind.Fill
                    ? _scene.DetachElementForMove(hit, _frame)
                    : hit;
                if (!materialized.IsValid)
                {
                    RestoreCanvasMutationSnapshot(snapshot);
                    return;
                }

                var hitObject = materialized.Key.ObjectIndex;
                _scene.Argb[hitObject] = color.ToArgb();
                _scene.DisableLinearGradient(hitObject);
                var animationContours = _scene.GetFillPartContours(materialized, _frame);
                var overwritten = NormalizeNewPaintObjects([hitObject]);
                if (overwritten.Length > 0) hitObject = overwritten[0];
                _scene.RefreshTimelineTweenMaterializationsAtEndpointFrame(_frame);
                SetSelection(hitObject);
                PushUndoSnapshot(snapshot);
                _hierarchyPanel.RefreshScene();
                UpdateInspector();
                ClearFillHoverPreview();
                _stage.StartFillAnimation(animationContours, fillWorld, color);
                _stage.Invalidate();
            }
            else
            {
                var color = ActiveColor();
                var snapshot = CreateCanvasMutationSnapshot(affectedLayers: [_scene.ActiveLayer]);
                if (!_scene.TryCreateFillFromClosedStrokeRegion(fillWorld, _frame, color, out var created, out var animationContours))
                {
                    RestoreCanvasMutationSnapshot(snapshot);
                    return;
                }

                var overwritten = NormalizeNewPaintObjects([created]);
                if (overwritten.Length > 0) created = overwritten[0];
                _scene.CanonicalizeFlattenedFillBoundaryCurves(created, _frame);

                _scene.RefreshTimelineTweenMaterializationsAtEndpointFrame(_frame);
                SetSelection(created);
                PushUndoSnapshot(snapshot);
                _hierarchyPanel.RefreshScene();
                UpdateInspector();
                ClearFillHoverPreview();
                _stage.StartFillAnimation(animationContours, fillWorld, color);
                _stage.Invalidate();
            }
        }
        else if (_tool == ToolMode.InkBottle)
        {
            ApplyInkBottle(e);
        }
        else if (IsFreehandTool(_tool) && e.Button == MouseButtons.Left && _startWorld is { } freehandStart)
        {
            BeginFreehandStroke(e.Location, freehandStart);
        }
    }

    private void ApplyEyedropper(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        var hit = _scene.HitTestElement(_stage.ScreenToWorld(e.Location), _frame, SelectionToleranceWorld());
        if (!hit.IsValid || (uint)hit.Key.ObjectIndex >= _scene.ObjectCount) return;

        var objectIndex = hit.Key.ObjectIndex;
        if (IsImportedSvgObject(objectIndex)) return;
        var strokeTarget = hit.Key.Kind is DrawingElementKind.Stroke or DrawingElementKind.BoundaryStroke;
        var color = Color.FromArgb(strokeTarget ? _scene.StrokeArgb[objectIndex] : _scene.Argb[objectIndex]);
        var samplesGradient = _scene.HasGradient(objectIndex)
            && (strokeTarget ? _scene.ShapeKind[objectIndex] == ShapeKind.Line : IsFillShape(_scene.ShapeKind[objectIndex]));
        var gradientKind = samplesGradient ? _scene.GetGradientKind(objectIndex) : GradientKind.Solid;
        var gradientStops = samplesGradient
            ? _scene.GetGradientStops(objectIndex)
            : [new GradientStop(0, color), new GradientStop(1, color)];
        if (gradientStops.Length > 0) color = Color.FromArgb(gradientStops[0].Argb);

        _materialEditor.SetGradientPreviewTarget(strokeTarget);
        _materialEditor.SetMaterial(
            strokeTarget ? _materialEditor.Fill : color,
            strokeTarget ? color : _materialEditor.Stroke,
            _materialEditor.StrokeWidth,
            color.A / 255f);
        _materialEditor.SetGradient(gradientKind, gradientStops);
    }

    private bool TryApplyFillToLine(DrawingElementHit hit)
    {
        if (!hit.IsValid
            || hit.Key.Kind != DrawingElementKind.Stroke
            || (uint)hit.Key.ObjectIndex >= _scene.ObjectCount
            || !_scene.IsObjectSelectable(hit.Key.ObjectIndex, _frame)
            || _scene.ShapeKind[hit.Key.ObjectIndex] != ShapeKind.Line)
        {
            return false;
        }

        var targets = IsShiftPressed()
            ? _scene.GetConnectedStrokeElements(hit, _frame)
                .Select(connected => connected.Key.ObjectIndex)
                .Where(index => _scene.IsObjectSelectable(index, _frame) && _scene.ShapeKind[index] == ShapeKind.Line)
                .Distinct()
                .ToArray()
            : [hit.Key.ObjectIndex];
        if (targets.Length == 0) return true;

        var color = ActiveColor().ToArgb();
        var changed = targets.Any(index => _scene.StrokeArgb[index] != color || _scene.HasGradient(index));
        if (!changed) return true;

        var snapshot = CreateCanvasMutationSnapshot(targets);
        foreach (var objectIndex in targets)
        {
            _scene.StrokeArgb[objectIndex] = color;
            _scene.DisableLinearGradient(objectIndex);
        }

        _scene.RefreshTimelineTweenMaterializationsAtEndpointFrame(_frame);
        SetSelection(targets);
        PushUndoSnapshot(snapshot);
        _hierarchyPanel.RefreshScene();
        UpdateInspector();
        ClearFillHoverPreview();
        _stage.Invalidate();
        return true;
    }

    private void ApplyInkBottle(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || _startWorld is not { } world) return;
        var hit = _scene.HitTestElement(world, _frame, SelectionToleranceWorld());
        if (!hit.IsValid || !_scene.IsObjectSelectable(hit.Key.ObjectIndex, _frame)) return;
        if (hit.Key.Kind is not DrawingElementKind.Fill and not DrawingElementKind.BoundaryStroke) return;

        var objectIndex = hit.Key.ObjectIndex;
        if (!IsFillShape(_scene.ShapeKind[objectIndex])) return;
        var stroke = ActiveStrokeUnits();
        var strokeColor = ActiveStrokeColor().ToArgb();
        if (Math.Abs(_scene.Stroke[objectIndex] - stroke) < 0.001f
            && _scene.StrokeArgb[objectIndex] == strokeColor)
        {
            return;
        }

        var snapshot = CreateCanvasMutationSnapshot([objectIndex]);
        _scene.Stroke[objectIndex] = stroke;
        _scene.StrokeArgb[objectIndex] = strokeColor;
        _scene.RebuildGeometryIndex();
        _scene.RefreshTimelineTweenMaterializationsAtEndpointFrame(_frame);
        SetSelection(objectIndex);
        PushUndoSnapshot(snapshot);
        _hierarchyPanel.RefreshScene();
        UpdateInspector();
        _stage.Invalidate();
    }

    private bool TryApplyFillEdgeBezierAnchorGesture(MouseEventArgs e)
    {
        var overlayHit = _stage.HitTestFillEdgeBezierOverlay(e.Location);
        var insertsAnchor = IsFillEdgeBezierAnchorInsertionGesture(e.Button, controlPressed: true, overlayHit);
        var deletesAnchor = IsFillEdgeBezierAnchorDeletionGesture(e.Button, controlPressed: true, overlayHit);
        if (!insertsAnchor && !deletesAnchor) return false;

        var targetObject = _stage.FillEdgeBezierOverlayTargetObject;
        if (targetObject != _selectedObject
            || (uint)targetObject >= _scene.ObjectCount
            || !TryGetFillEdgeBezierOverlayPiece(overlayHit.PartIndex, out var overlayPiece))
        {
            return false;
        }
        overlayPiece = ResolvePresentedFillEdgeBezierPiece(overlayPiece, overlayHit);

        if (deletesAnchor
            && (overlayHit.Handle == EditHandleKind.LineStart && overlayPiece.StartIsVirtualAnchor
                || overlayHit.Handle == EditHandleKind.LineEnd && overlayPiece.EndIsVirtualAnchor))
        {
            return true;
        }

        var hasSelectedFillPart = TryGetSelectedFillPartIndex(
            targetObject,
            _selectedElements,
            out var selectedFillPartIndex);
        var fillPartSelection = CaptureSelectedFillPartSelection(targetObject);
        var stackKey = new DrawingStackKey(
            _scene.ObjectOrder[targetObject],
            _scene.ObjectSubOrder[targetObject]);
        var snapshot = CreateCanvasGeometryMutationSnapshot([targetObject]);
        targetObject = FindActiveObjectByStackKey(stackKey);
        if (targetObject < 0
            || !CanEditFillEdgeBezierPiece(
                targetObject,
                hasSelectedFillPart ? selectedFillPartIndex : null,
                overlayPiece)
            || !_scene.TryConvertFillToBezierPath(targetObject))
        {
            RestoreCanvasMutationSnapshot(snapshot);
            return true;
        }
        var editStackKey = new DrawingStackKey(
            _scene.ObjectOrder[targetObject],
            _scene.ObjectSubOrder[targetObject]);

        var activePartIndex = -1;
        var changed = false;
        if (insertsAnchor)
        {
            var world = _stage.ScreenToWorld(e.Location);
            if (!_scene.TryInverseMapObjectPoint(targetObject, world, out world))
            {
                RestoreCanvasMutationSnapshot(snapshot);
                return true;
            }
            var tolerance = Math.Max(
                DrawingTopologyRules.MinStrokeSegmentUnits,
                _stage.ScreenLengthToWorld(9));
            changed = _scene.TryMaterializePathBezierSegmentInterval(
                    targetObject,
                    overlayPiece.SourcePartIndex,
                    overlayPiece.StartT,
                    overlayPiece.EndT,
                    out var isolatedPartIndex)
                && _scene.TryGetClosestPointOnPathBezierSegment(
                    targetObject,
                    isolatedPartIndex,
                    world,
                    out var parameter,
                    out _,
                    out var distance)
                && distance <= tolerance
                && parameter > 0.025f
                && parameter < 0.975f
                && _scene.TryInsertPathBezierAnchor(
                    targetObject,
                    isolatedPartIndex,
                    parameter,
                    out activePartIndex,
                    out _);
            if (!changed)
            {
                RestoreCanvasMutationSnapshot(snapshot);
                return true;
            }

            var sharedIntersection = CaptureFillEdgeSharedIntersection(
                targetObject,
                activePartIndex,
                EditHandleKind.LineStart,
                out var linkedStrokes);
            if (!TryResolveCapturedFillEdgeOwner(
                    sharedIntersection,
                    editStackKey,
                    EditHandleKind.LineStart,
                    out targetObject,
                    out activePartIndex,
                    out var insertedSegment))
            {
                RestoreCanvasMutationSnapshot(snapshot);
                return true;
            }

            _fillEdgeBezierActivePartIndex = activePartIndex;
            _fillEdgeBezierActivePieceIndex = -1;
            if (!RestoreFillEdgeSelection(targetObject, fillPartSelection))
            {
                RestoreCanvasMutationSnapshot(snapshot);
                FinishPointerInteraction();
                return true;
            }
            _lastMouse = e.Location;
            _startScreen = e.Location;
            _startWorld = world;
            _pointerHitWasAlreadySelected = true;
            _independentMarqueeStrokeMove = false;
            _selectionWasEmptyOnPointerDown = false;
            _forceMarqueeOnPointerDown = false;
            _additiveSelection = false;
            _pendingClickSelection = DrawingElementHit.None;
            _fillEdgeBezierEditSession = new FillEdgeBezierEditSession
            {
                Scene = _scene,
                Snapshot = snapshot,
                StackKey = stackKey,
                FillPartSelection = fillPartSelection,
                ObjectIndex = targetObject,
                PartIndex = activePartIndex,
                Handle = EditHandleKind.LineStart,
                PointerStart = world,
                Start = insertedSegment.Start,
                Control1 = insertedSegment.Control1,
                Control2 = insertedSegment.Control2,
                End = insertedSegment.End,
                LinkedStrokes = linkedStrokes,
                SharedIntersection = sharedIntersection,
                IncludesAnchorInsertion = true,
                InitialFillContours = _scene.GetObjectBoundaryContours(targetObject),
                LastAppliedSegment = new CubicDrawingPreviewSegment(
                    insertedSegment.Start,
                    insertedSegment.Control1,
                    insertedSegment.Control2,
                    insertedSegment.End),
                Changed = true
            };
            _stage.Focus();
            _stage.Capture = true;
            _stage.SetFillEdgeBezierPointerEditing(true);
            UpdateFillEdgeBezierOverlay();
            UpdateInteractionCursor(e.Location);
            _stage.Invalidate();
            return true;
        }
        else
        {
            changed = _scene.TryDeletePathBezierAnchor(
                targetObject,
                overlayPiece.SourcePartIndex,
                startEndpoint: overlayHit.Handle == EditHandleKind.LineStart,
                out activePartIndex);
        }

        if (!changed)
        {
            RestoreCanvasMutationSnapshot(snapshot);
            return true;
        }

        _fillEdgeBezierActivePartIndex = activePartIndex;
        _fillEdgeBezierActivePieceIndex = -1;
        if (!RestoreFillEdgeSelection(targetObject, fillPartSelection))
        {
            RestoreCanvasMutationSnapshot(snapshot);
            return true;
        }
        snapshot.DetachSharedGeometry();
        PushUndoSnapshot(snapshot);
        _timeline.RefreshTimeline();
        RebuildDrawingObjectUnderlay();
        _hierarchyPanel.RefreshScene();
        UpdateFillEdgeBezierOverlay();
        UpdateInspector();
        _stage.Invalidate();
        return true;
    }

    internal static bool IsFillEdgeBezierAnchorInsertionGesture(
        MouseButtons button,
        bool controlPressed,
        FillEdgeBezierOverlayHit hit)
    {
        return controlPressed
            && button == MouseButtons.Left
            && hit.IsValid
            && hit.Handle == EditHandleKind.None;
    }

    internal static bool IsFillEdgeBezierAnchorDeletionGesture(
        MouseButtons button,
        bool controlPressed,
        FillEdgeBezierOverlayHit hit)
    {
        return controlPressed
            && button == MouseButtons.Right
            && hit.IsValid
            && hit.Handle is EditHandleKind.LineStart or EditHandleKind.LineEnd;
    }

    private bool TryBeginLineBranchDrag(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || !IsAltPressed() || IsScene3DView()) return false;

        var world = _stage.ScreenToWorld(e.Location);
        var tolerance = Math.Max(
            DrawingTopologyRules.MinStrokeSegmentUnits,
            _stage.ScreenLengthToWorld(9));
        var hit = _scene.HitTestElement(world, _frame, SelectionToleranceWorld());
        var sourceObject = -1;
        var sourceParameter = 0f;
        var bestEndpointDistance = float.PositiveInfinity;

        void ConsiderEndpoint(int candidate)
        {
            if ((uint)candidate >= _scene.ObjectCount
                || _scene.ShapeKind[candidate] != ShapeKind.Line
                || !_scene.IsObjectSelectable(candidate, _frame))
            {
                return;
            }

            if (_scene.TryGetLineEndpoint(candidate, startEndpoint: true, out var start))
            {
                var distance = Distance(world, start);
                if (distance <= tolerance && distance <= bestEndpointDistance)
                {
                    sourceObject = candidate;
                    sourceParameter = 0;
                    bestEndpointDistance = distance;
                }
            }

            if (_scene.TryGetLineEndpoint(candidate, startEndpoint: false, out var end))
            {
                var distance = Distance(world, end);
                if (distance <= tolerance && distance < bestEndpointDistance)
                {
                    sourceObject = candidate;
                    sourceParameter = 1;
                    bestEndpointDistance = distance;
                }
            }
        }

        ConsiderEndpoint(_selectedObject);
        if (hit.IsValid && hit.Key.Kind == DrawingElementKind.Stroke) ConsiderEndpoint(hit.Key.ObjectIndex);
        var endpointBounds = RectangleF.FromLTRB(
            world.X - tolerance,
            world.Y - tolerance,
            world.X + tolerance,
            world.Y + tolerance);
        foreach (var candidate in _scene.QueryObjects(endpointBounds, _frame)) ConsiderEndpoint(candidate);

        var endpointGesture = IsLineEndpointBranchGesture(
            e.Button,
            controlPressed: true,
            bestEndpointDistance,
            tolerance);
        if (!endpointGesture)
        {
            return false;
        }

        var stackKey = new DrawingStackKey(
            _scene.ObjectOrder[sourceObject],
            _scene.ObjectSubOrder[sourceObject]);
        var snapshot = CreateCanvasMutationSnapshot([sourceObject]);
        sourceObject = FindActiveObjectByStackKey(stackKey);
        if (sourceObject < 0 || _scene.ShapeKind[sourceObject] != ShapeKind.Line)
        {
            RestoreCanvasMutationSnapshot(snapshot);
            return true;
        }

        if (!_scene.TryGetLineEndpoint(
                sourceObject,
                startEndpoint: sourceParameter <= 0.001f,
                out var anchor))
        {
            RestoreCanvasMutationSnapshot(snapshot);
            return true;
        }

        _stage.Capture = true;
        _lastMouse = e.Location;
        _startScreen = e.Location;
        _startWorld = world;
        _forceMarqueeOnPointerDown = false;
        _additiveSelection = false;
        _pendingClickSelection = DrawingElementHit.None;
        _lineBranchDragSession = new LineBranchDragSession
        {
            Scene = _scene,
            Snapshot = snapshot,
            ObjectIndex = sourceObject,
            Layer = _scene.ObjectLayer[sourceObject],
            SourceParameter = sourceParameter,
            Anchor = anchor,
            End = anchor,
            StrokeColor = Color.FromArgb(_scene.StrokeArgb[sourceObject]),
            Stroke = _scene.Stroke[sourceObject]
        };
        _stage.ClearHoveredLineElement();
        UpdateInteractionCursor(e.Location);
        return true;
    }

    internal static bool IsLineEndpointBranchGesture(
        MouseButtons button,
        bool controlPressed,
        float endpointDistance,
        float tolerance)
    {
        return button == MouseButtons.Left
            && controlPressed
            && float.IsFinite(endpointDistance)
            && endpointDistance <= tolerance;
    }

    internal static bool IsLineInteriorBranchGesture(
        MouseButtons button,
        bool controlPressed,
        bool altPressed,
        float parameter)
    {
        return button == MouseButtons.Left
            && controlPressed
            && altPressed
            && float.IsFinite(parameter)
            && parameter > 0.025f
            && parameter < 0.975f;
    }

    internal static bool ShouldCommitLineBranchDrag(
        MouseButtons button,
        bool dragThresholdExceeded,
        float branchLength)
    {
        return button == MouseButtons.Left
            && dragThresholdExceeded
            && branchLength >= DrawingTopologyRules.MinStrokeSegmentUnits;
    }

    private void UpdateLineBranchDrag(Point screen)
    {
        var session = _lineBranchDragSession;
        if (session is null || !ReferenceEquals(session.Scene, _scene)) return;
        if (!session.DragExceeded && !PointerDragExceeded(screen)) return;

        session.DragExceeded = true;
        session.End = ResolveDrawingLineEnd(
            session.Anchor,
            _stage.ScreenToWorld(screen),
            session.Layer,
            temporarilySnapAngle: IsShiftPressed());
        if (Distance(session.Anchor, session.End) < DrawingTopologyRules.MinStrokeSegmentUnits)
        {
            _stage.ClearDrawingPreview();
            return;
        }

        _stage.SetDrawingPreview(
            session.Anchor,
            session.End,
            ShapeKind.Line,
            session.StrokeColor,
            session.Stroke);
    }

    private void CompleteLineBranchDrag(Point screen, MouseButtons button)
    {
        var session = _lineBranchDragSession;
        _lineBranchDragSession = null;
        _stage.ClearDrawingPreview();
        if (session is null || !ReferenceEquals(session.Scene, _scene)) return;

        var dragExceeded = session.DragExceeded || PointerDragExceeded(screen);
        var end = dragExceeded
            ? ResolveDrawingLineEnd(
                session.Anchor,
                _stage.ScreenToWorld(screen),
                session.Layer,
                temporarilySnapAngle: IsShiftPressed())
            : session.Anchor;
        if (!ShouldCommitLineBranchDrag(button, dragExceeded, Distance(session.Anchor, end))
            || !_scene.AddConnectedLineBranch(
                session.ObjectIndex,
                session.SourceParameter,
                end,
                out var result))
        {
            RestoreCanvasMutationSnapshot(session.Snapshot);
            return;
        }

        PushUndoSnapshot(session.Snapshot);
        SetSelection(result.BranchObjectIndex);
        _hierarchyPanel.RefreshScene();
        UpdateInspector();
        _stage.Invalidate();
    }

    private void CancelLineBranchDrag(bool restore)
    {
        var session = _lineBranchDragSession;
        _lineBranchDragSession = null;
        _stage.ClearDrawingPreview();
        if (restore && session is not null && ReferenceEquals(session.Scene, _scene))
        {
            RestoreCanvasMutationSnapshot(session.Snapshot);
        }
    }

    private bool BeginArcDrag(PointF world, int objectIndex)
    {
        if ((uint)objectIndex >= _scene.ObjectCount
            || _scene.ShapeKind[objectIndex] != ShapeKind.Line
            || !_scene.TryGetLineEndpoint(objectIndex, startEndpoint: true, out var start)
            || !_scene.TryGetLineEndpoint(objectIndex, startEndpoint: false, out var end)
            || !_scene.TryGetClosestPointOnLine(objectIndex, world, out var t, out _, out _))
        {
            return false;
        }

        t = Math.Clamp(t, 0.05f, 0.95f);
        var snapshot = CreateCanvasMutationSnapshot([objectIndex]);
        // Capture while the line is still straight so coincident fill edges
        // are linked and follow the arc while it is dragged.
        var fillBoundaryLinks = _scene.CaptureFillBoundaryLineLinks(objectIndex, _frame);
        _arcDragSession = new ArcDragSession
        {
            Scene = _scene,
            Snapshot = snapshot,
            ObjectIndex = objectIndex,
            Start = start,
            End = end,
            Parameter = t,
            FillBoundaryLinks = fillBoundaryLinks,
            DragExceeded = false
        };
        SetSelection(objectIndex);
        return true;
    }

    private void UpdateArcDrag(Point screen)
    {
        var session = _arcDragSession;
        if (session is null || !ReferenceEquals(session.Scene, _scene)) return;
        if (!session.DragExceeded && !PointerDragExceeded(screen)) return;
        session.DragExceeded = true;

        var world = _stage.ScreenToWorld(screen);
        _scene.SetLineQuadraticControl(session.ObjectIndex, world);
        if (session.FillBoundaryLinks.Length > 0)
        {
            _scene.UpdateFillBoundaryLineLinks(session.FillBoundaryLinks, rebuildGeometryIndex: false);
        }

        _scene.InvalidateDeferredTopologyQueries();
        _geometryDirty = true;
        _stage.Invalidate();
    }

    private void CompleteArcDrag(Point screen, MouseButtons button)
    {
        var session = _arcDragSession;
        _arcDragSession = null;
        if (session is null || !ReferenceEquals(session.Scene, _scene)) return;

        var exceeded = session.DragExceeded || PointerDragExceeded(screen);
        if (exceeded && button == MouseButtons.Left)
        {
            PushUndoSnapshot(session.Snapshot);
        }
        else
        {
            RestoreCanvasMutationSnapshot(session.Snapshot);
        }

        _geometryDirty = true;
        _stage.Invalidate();
        UpdateInteractionCursor(screen);
    }

    private void CancelArcDrag(bool restore)
    {
        var session = _arcDragSession;
        _arcDragSession = null;
        if (restore && session is not null && ReferenceEquals(session.Scene, _scene))
        {
            RestoreCanvasMutationSnapshot(session.Snapshot);
        }
    }

    private bool TryBeginCornerDrag(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || !IsControlPressed() || IsScene3DView()) return false;

        var world = _stage.ScreenToWorld(e.Location);
        var hit = _scene.HitTestElement(world, _frame, SelectionToleranceWorld());
        if (!hit.IsValid
            || hit.Key.Kind != DrawingElementKind.Stroke
            || !_scene.IsObjectSelectable(hit.Key.ObjectIndex, _frame))
        {
            return false;
        }

        var objectIndex = hit.Key.ObjectIndex;
        var shape = _scene.ShapeKind[objectIndex];
        if (shape == ShapeKind.Line)
        {
            if (!_scene.TryGetClosestPointOnLine(objectIndex, world, out var t, out _, out _)
                || t <= 0.06f
                || t >= 0.94f
                || !BeginLineCornerDrag(objectIndex, t))
            {
                return false;
            }
        }
        else if (shape == ShapeKind.Freeform)
        {
            var segmentIndex = hit.BezierSegmentIndex;
            if (segmentIndex < 0
                || !_scene.TryGetFreehandBezierSegment(objectIndex, segmentIndex, out var part))
            {
                return false;
            }

            var t = ClosestCubicParameter(part.Curve, world);
            if (t <= 0.06f || t >= 0.94f || !BeginFreeformCornerDrag(objectIndex, segmentIndex, t))
            {
                return false;
            }
        }
        else
        {
            return false;
        }

        _stage.Capture = true;
        _lastMouse = e.Location;
        _startScreen = e.Location;
        _startWorld = world;
        _forceMarqueeOnPointerDown = false;
        _pendingClickSelection = DrawingElementHit.None;
        _stage.ClearHoveredLineElement();
        UpdateInteractionCursor(e.Location);
        return true;
    }

    private static float ClosestCubicParameter(CubicBoundarySegment curve, PointF world)
    {
        var bestT = 0.5f;
        var bestDistance = float.MaxValue;
        const int steps = 48;
        for (var i = 0; i <= steps; i++)
        {
            var t = i / (float)steps;
            var u = 1f - t;
            var x = u * u * u * curve.Start.X
                + 3f * u * u * t * curve.Control1.X
                + 3f * u * t * t * curve.Control2.X
                + t * t * t * curve.End.X;
            var y = u * u * u * curve.Start.Y
                + 3f * u * u * t * curve.Control1.Y
                + 3f * u * t * t * curve.Control2.Y
                + t * t * t * curve.End.Y;
            var dx = x - world.X;
            var dy = y - world.Y;
            var distance = dx * dx + dy * dy;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestT = t;
            }
        }

        return bestT;
    }

    private bool BeginLineCornerDrag(int objectIndex, float parameter)
    {
        var snapshot = CreateCanvasMutationSnapshot([objectIndex]);
        // Capture while the line is still straight so coincident fill edges
        // follow the corner while it is dragged.
        var fillBoundaryLinks = _scene.CaptureFillBoundaryLineLinks(objectIndex, _frame);
        if (!_scene.TryConvertLineToBezierFreeform(objectIndex, parameter, out _)
            || !_scene.TryGetFreehandBezierWorldNodes(objectIndex, out var nodes)
            || nodes.Length < 3)
        {
            RestoreCanvasMutationSnapshot(snapshot);
            return false;
        }

        _cornerDragSession = new CornerDragSession
        {
            Scene = _scene,
            Snapshot = snapshot,
            ObjectIndex = objectIndex,
            NodeIndex = 1,
            BaseCorner = nodes[1],
            FillBoundaryLinks = fillBoundaryLinks,
            DragExceeded = false
        };
        SetSelection(objectIndex);
        return true;
    }

    private bool BeginFreeformCornerDrag(int objectIndex, int segmentIndex, float parameter)
    {
        var snapshot = CreateCanvasMutationSnapshot([objectIndex]);
        var fillBoundaryLinks = _scene.CaptureFillBoundaryLineLinks(objectIndex, _frame);
        if (!_scene.TryInsertFreehandBezierCorner(objectIndex, segmentIndex, parameter, out var nodeIndex)
            || nodeIndex < 0
            || !_scene.TryGetFreehandBezierWorldNodes(objectIndex, out var nodes)
            || nodeIndex >= nodes.Length)
        {
            RestoreCanvasMutationSnapshot(snapshot);
            return false;
        }

        _cornerDragSession = new CornerDragSession
        {
            Scene = _scene,
            Snapshot = snapshot,
            ObjectIndex = objectIndex,
            NodeIndex = nodeIndex,
            BaseCorner = nodes[nodeIndex],
            FillBoundaryLinks = fillBoundaryLinks,
            DragExceeded = false
        };
        SetSelection(objectIndex);
        return true;
    }

    private void UpdateCornerDrag(Point screen)
    {
        var session = _cornerDragSession;
        if (session is null || !ReferenceEquals(session.Scene, _scene)) return;
        if (!session.DragExceeded && !PointerDragExceeded(screen)) return;
        session.DragExceeded = true;

        var world = _stage.ScreenToWorld(screen);
        if (!_scene.TryGetFreehandBezierWorldNodes(session.ObjectIndex, out var nodes)
            || (uint)session.NodeIndex >= nodes.Length)
        {
            return;
        }

        var baseCorner = session.BaseCorner;
        var deltaX = world.X - baseCorner.Anchor.X;
        var deltaY = world.Y - baseCorner.Anchor.Y;
        nodes[session.NodeIndex] = new PathBezierNode(
            world,
            new PointF(baseCorner.IncomingControl.X + deltaX, baseCorner.IncomingControl.Y + deltaY),
            new PointF(baseCorner.OutgoingControl.X + deltaX, baseCorner.OutgoingControl.Y + deltaY));
        if (!_scene.TrySetFreehandBezierWorldNodes(session.ObjectIndex, nodes)) return;
        if (session.FillBoundaryLinks.Length > 0)
        {
            _scene.UpdateFillBoundaryLineLinks(session.FillBoundaryLinks, rebuildGeometryIndex: false);
        }

        _scene.InvalidateDeferredTopologyQueries();
        _geometryDirty = true;
        _stage.Invalidate();
    }

    private void CompleteCornerDrag(Point screen, MouseButtons button)
    {
        var session = _cornerDragSession;
        _cornerDragSession = null;
        if (session is null || !ReferenceEquals(session.Scene, _scene)) return;

        var exceeded = session.DragExceeded || PointerDragExceeded(screen);
        if (exceeded && button == MouseButtons.Left)
        {
            PushUndoSnapshot(session.Snapshot);
        }
        else
        {
            RestoreCanvasMutationSnapshot(session.Snapshot);
        }

        _geometryDirty = true;
        _stage.Invalidate();
        UpdateInteractionCursor(screen);
    }

    private void CancelCornerDrag(bool restore)
    {
        var session = _cornerDragSession;
        _cornerDragSession = null;
        if (restore && session is not null && ReferenceEquals(session.Scene, _scene))
        {
            RestoreCanvasMutationSnapshot(session.Snapshot);
        }
    }

    private void BeginFillEdgeBezierPointer(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || _startWorld is not { } world)
        {
            FinishPointerInteraction();
            return;
        }

        var overlayHit = _stage.HitTestFillEdgeBezierOverlay(e.Location);
        var targetObject = _stage.FillEdgeBezierOverlayTargetObject;
        if (overlayHit.IsValid
            && targetObject == _selectedObject
            && (uint)targetObject < _scene.ObjectCount
            && TryGetFillEdgeBezierOverlayPiece(overlayHit.PartIndex, out var overlayPiece))
        {
            _fillEdgeBezierActivePartIndex = overlayPiece.SourcePartIndex;
            _fillEdgeBezierActivePieceIndex = overlayPiece.PieceIndex;
            if (overlayHit.Handle == EditHandleKind.None)
            {
                if (!_scene.HasStroke(targetObject)
                    || _scene.IsBoundarySegmentDetached(targetObject, overlayPiece.SourcePartIndex))
                {
                    // A pure fill has no stroke to pull, and an already-detached segment
                    // has none either: body drag bends the fill boundary directly (pure
                    // reshape) instead of spawning a stroke line.
                    BeginFillEdgeNoUiArc((targetObject, overlayPiece.SourcePartIndex), world);
                    UpdateInteractionCursor(e.Location);
                    return;
                }

                // Dragging the segment body of a selected shape detaches it as a new,
                // freely movable stroke object (the original keeps its geometry).
                _fillEdgeDetachPending = (targetObject, overlayPiece.SourcePartIndex);
                UpdateInteractionCursor(e.Location);
                return;
            }

            overlayPiece = ResolvePresentedFillEdgeBezierPiece(overlayPiece, overlayHit);

            ArmFillEdgeBezierPointer(
                targetObject,
                overlayPiece,
                overlayHit.Handle,
                world,
                e.Location,
                resetCurvature: IsBezierCurvatureResetGesture(e.Button, IsAltPressed(), overlayHit.Handle));
            return;
        }

        FinishPointerInteraction();
    }

    private void ArmFillEdgeBezierPointer(
        int targetObject,
        FillBezierSegmentPiece overlayPiece,
        EditHandleKind handle,
        PointF pointer,
        Point screen,
        bool resetCurvature)
    {
        CancelArmedFillEdgeBezierPointer();
        var previewSegments = _fillEdgeBezierOverlaySegments.ToArray();
        var previewSegmentIndex = Array.FindIndex(
            previewSegments,
            segment => segment.PartIndex == overlayPiece.PieceIndex);
        if (previewSegmentIndex < 0)
        {
            FinishPointerInteraction();
            return;
        }

        var generation = unchecked(++_fillEdgeBezierArmGeneration);
        var armed = new FillEdgeBezierArmedSession
        {
            Scene = _scene,
            GeometryRevision = _scene.GeometryRevision,
            StackKey = new DrawingStackKey(
                _scene.ObjectOrder[targetObject],
                _scene.ObjectSubOrder[targetObject]),
            TargetObject = targetObject,
            OverlayPiece = overlayPiece,
            Handle = handle,
            PointerStart = pointer,
            LatestPointer = pointer,
            LatestScreen = screen,
            PreviewSegments = previewSegments,
            PreviewSegmentIndex = previewSegmentIndex,
            Generation = generation,
            ResetCurvature = resetCurvature
        };
        _fillEdgeBezierArmedSession = armed;
        _stage.SetOwnedFillEdgeBezierOverlay(
            targetObject,
            previewSegments,
            overlayPiece.PieceIndex);
        UpdateArmedFillEdgeBezierPreview(armed, pointer, screen, pointerMoved: false);
        UpdateInteractionCursor(screen);
    }

    private void UpdateArmedFillEdgeBezierPreview(
        FillEdgeBezierArmedSession armed,
        PointF pointer,
        Point screen,
        bool pointerMoved)
    {
        if (!ReferenceEquals(_fillEdgeBezierArmedSession, armed)) return;
        armed.LatestPointer = pointer;
        armed.LatestScreen = screen;
        armed.PointerMoved |= pointerMoved;

        if (!_scene.TryInverseMapObjectPoint(armed.TargetObject, armed.PointerStart, out var sourcePointerStart)
            || !_scene.TryInverseMapObjectPoint(armed.TargetObject, pointer, out var sourcePointer))
        {
            return;
        }

        var piece = armed.OverlayPiece;
        var current = armed.ResetCurvature
            ? ResetBezierCurvature(piece.Start, piece.End)
            : AdjustFillEdgeBezierHandle(
                piece.Start,
                piece.Control1,
                piece.Control2,
                piece.End,
                armed.Handle,
                VectorUnits.Quantize(SnapDrawingPoint(MoveHandleWithPointer(
                    armed.Handle switch
                    {
                        EditHandleKind.LineStart => piece.Start,
                        EditHandleKind.BezierControl => piece.Control1,
                        EditHandleKind.BezierControl2 => piece.Control2,
                        EditHandleKind.LineEnd => piece.End,
                        _ => PointF.Empty
                    },
                     sourcePointerStart,
                     sourcePointer))));
        var index = armed.PreviewSegmentIndex;
        var preview = new FillEdgeBezierOverlaySegment(
            piece.PieceIndex,
            current.Start,
            current.Control1,
            current.Control2,
            current.End);
        if (armed.PreviewSegments[index] == preview) return;
        armed.PreviewSegments[index] = preview;
        _stage.NotifyOwnedFillEdgeBezierOverlayChanged(
            armed.TargetObject,
            armed.PreviewSegments,
            piece.PieceIndex);
    }

    private void PostArmedFillEdgeBezierPreparation()
    {
        var armed = _fillEdgeBezierArmedSession;
        if (armed is null
            || armed.PreparationPosted
            || !ShouldPrepareArmedFillEdgeBezierPointer(
                armed.ResetCurvature,
                armed.PointerMoved,
                armed.CompleteAfterPreparation)
            || IsDisposed
            || Disposing
            || !IsHandleCreated)
        {
            return;
        }

        armed.PreparationPosted = true;
        var generation = armed.Generation;
        try
        {
            BeginInvoke(() => PrepareArmedFillEdgeBezierPointer(generation));
        }
        catch (InvalidOperationException)
        {
            if (ReferenceEquals(_fillEdgeBezierArmedSession, armed)) armed.PreparationPosted = false;
        }
    }

    internal static bool ShouldPrepareArmedFillEdgeBezierPointer(
        bool resetCurvature,
        bool pointerMoved,
        bool completeAfterPreparation)
    {
        return resetCurvature || pointerMoved || completeAfterPreparation;
    }

    private void PrepareArmedFillEdgeBezierPointer(int generation)
    {
        var armed = _fillEdgeBezierArmedSession;
        if (armed is null || armed.Generation != generation) return;
        armed.PreparationPosted = false;
        if (!ReferenceEquals(armed.Scene, _scene)
            || armed.GeometryRevision != _scene.GeometryRevision
            || _tool != ToolMode.Select
            || _selectedObject != armed.TargetObject
            || (uint)armed.TargetObject >= _scene.ObjectCount)
        {
            CancelArmedFillEdgeBezierPointer();
            FinishPointerInteraction();
            return;
        }

        _fillEdgeBezierArmedSession = null;
        unchecked
        {
            _fillEdgeBezierArmGeneration++;
        }

        var targetObject = armed.TargetObject;
        var hasSelectedFillPart = TryGetSelectedFillPartIndex(
            targetObject,
            _selectedElements,
            out var selectedFillPartIndex);
        var fillPartSelection = CaptureSelectedFillPartSelection(targetObject);
        var snapshot = CreateCanvasGeometryMutationSnapshot([targetObject]);
        targetObject = FindActiveObjectByStackKey(armed.StackKey);
        if (targetObject < 0
            || !CanEditFillEdgeBezierPiece(
                targetObject,
                hasSelectedFillPart ? selectedFillPartIndex : null,
                armed.OverlayPiece)
            || !_scene.TryConvertFillToBezierPath(targetObject)
            || !TryMaterializeFillEdgeBezierPointerNeighborhood(
                targetObject,
                armed.OverlayPiece,
                armed.Handle,
                out var editablePartIndex))
        {
            AbortArmedFillEdgeBezierPreparation(armed, snapshot, fillPartSelection);
            return;
        }

        var editStackKey = new DrawingStackKey(
            _scene.ObjectOrder[targetObject],
            _scene.ObjectSubOrder[targetObject]);
        var sharedIntersection = CaptureFillEdgeSharedIntersection(
            targetObject,
            editablePartIndex,
            armed.Handle,
            out var linkedStrokes);
        if (!TryResolveCapturedFillEdgeOwner(
                sharedIntersection,
                editStackKey,
                armed.Handle,
                out targetObject,
                out editablePartIndex,
                out var segment))
        {
            AbortArmedFillEdgeBezierPreparation(armed, snapshot, fillPartSelection);
            return;
        }

        if (!_scene.TryInverseMapObjectPoint(
                targetObject,
                armed.PointerStart,
                out var sourcePointerStart))
        {
            AbortArmedFillEdgeBezierPreparation(armed, snapshot, fillPartSelection);
            return;
        }

        _fillEdgeBezierActivePartIndex = editablePartIndex;
        _fillEdgeBezierActivePieceIndex = -1;
        if (!RestoreFillEdgeSelection(targetObject, fillPartSelection))
        {
            AbortArmedFillEdgeBezierPreparation(armed, snapshot, fillPartSelection);
            return;
        }

        _fillEdgeBezierEditSession = new FillEdgeBezierEditSession
        {
            Scene = _scene,
            Snapshot = snapshot,
            StackKey = armed.StackKey,
            FillPartSelection = fillPartSelection,
            ObjectIndex = targetObject,
            PartIndex = editablePartIndex,
            Handle = armed.Handle,
            PointerStart = sourcePointerStart,
            Start = segment.Start,
            Control1 = segment.Control1,
            Control2 = segment.Control2,
            End = segment.End,
            LinkedStrokes = linkedStrokes,
            SharedIntersection = sharedIntersection,
            IncludesAnchorInsertion = false,
            InitialFillContours = _scene.GetObjectBoundaryContours(targetObject),
            LastAppliedSegment = new CubicDrawingPreviewSegment(
                segment.Start,
                segment.Control1,
                segment.Control2,
                segment.End)
        };
        _stage.SetFillEdgeBezierPointerEditing(true);
        UpdateFillEdgeBezierOverlay();
        if (armed.ResetCurvature)
        {
            UpdateFillEdgeBezierPointer(armed.LatestPointer, resetCurvature: true);
        }
        else if (armed.PointerMoved)
        {
            UpdateFillEdgeBezierPointer(armed.LatestPointer);
        }

        if (armed.ResetCurvature || armed.CompleteAfterPreparation)
        {
            CompleteFillEdgeBezierPointer();
            FinishPointerInteraction();
            return;
        }

        UpdateInteractionCursor(armed.LatestScreen);
    }

    private void AbortArmedFillEdgeBezierPreparation(
        FillEdgeBezierArmedSession armed,
        VectorSceneSnapshot snapshot,
        FillPartSelectionIdentity? fillPartSelection)
    {
        RestoreCanvasMutationSnapshot(snapshot);
        var restored = FindActiveObjectByStackKey(armed.StackKey);
        if (!RestoreFillEdgeSelection(restored, fillPartSelection)) ClearSelection();
        UpdateFillEdgeBezierOverlay();
        FinishPointerInteraction();
    }

    private void CancelArmedFillEdgeBezierPointer()
    {
        var armed = _fillEdgeBezierArmedSession;
        if (armed is null) return;
        _fillEdgeBezierArmedSession = null;
        unchecked
        {
            _fillEdgeBezierArmGeneration++;
        }

        if (ReferenceEquals(armed.Scene, _scene)
            && _fillEdgeBezierOverlaySegments.Length > 0
            && (uint)armed.TargetObject < _scene.ObjectCount)
        {
            _stage.SetOwnedFillEdgeBezierOverlay(
                armed.TargetObject,
                _fillEdgeBezierOverlaySegments,
                armed.OverlayPiece.PieceIndex);
        }
    }

    private bool TryMaterializeFillEdgeBezierPointerNeighborhood(
        int objectIndex,
        FillBezierSegmentPiece activePiece,
        EditHandleKind handle,
        out int activePartIndex)
    {
        if (handle is EditHandleKind.LineStart or EditHandleKind.LineEnd)
        {
            return _scene.TryMaterializePathBezierSegmentNeighborhood(
                objectIndex,
                activePiece,
                startEndpoint: handle == EditHandleKind.LineStart,
                _fillEdgeBezierOverlayPieces,
                out activePartIndex);
        }

        return _scene.TryMaterializePathBezierSegmentInterval(
            objectIndex,
            activePiece.SourcePartIndex,
            activePiece.StartT,
            activePiece.EndT,
            out activePartIndex);
    }

    private bool TryResolveCapturedFillEdgeOwner(
        SharedBoundaryIntersection capture,
        DrawingStackKey stackKey,
        EditHandleKind handle,
        out int objectIndex,
        out int partIndex,
        out PathBezierSegmentPart segment)
    {
        objectIndex = capture.OwnerObjectIndex;
        partIndex = capture.OwnerPartIndex;
        segment = default;
        if ((uint)objectIndex >= _scene.ObjectCount
            || partIndex < 0
            || !_scene.IsObjectActive(objectIndex, _frame)
            || !_scene.HasFill(objectIndex)
            || !IsFillShape(_scene.ShapeKind[objectIndex])
            || _scene.ObjectOrder[objectIndex] != stackKey.Order
            || !_scene.ObjectSubOrder[objectIndex].Equals(stackKey.SubOrder)
            || !_scene.TryGetPathBezierSegment(objectIndex, partIndex, out segment))
        {
            return false;
        }

        var currentAnchor = handle switch
        {
            EditHandleKind.LineStart => segment.Start,
            EditHandleKind.LineEnd => segment.End,
            _ => capture.OriginalAnchor
        };
        return handle is not EditHandleKind.LineStart and not EditHandleKind.LineEnd
            || Distance(currentAnchor, capture.OriginalAnchor) <= _stage.ScreenLengthToWorld(EndpointConnectionTolerancePixels);
    }

    private SharedBoundaryIntersection CaptureFillEdgeSharedIntersection(
        int objectIndex,
        int partIndex,
        EditHandleKind handle,
        out FillBoundaryStrokeLink[] linkedStrokes)
    {
        linkedStrokes = _scene.CaptureFillBoundaryStrokeLinks(objectIndex, partIndex, _frame);
        if (handle is not EditHandleKind.LineStart and not EditHandleKind.LineEnd)
        {
            return new SharedBoundaryIntersection(
                PointF.Empty,
                [],
                [],
                [],
                objectIndex,
                partIndex);
        }

        var fullCurveLineObjects = linkedStrokes
            .Select(link => link.LineObjectIndex)
            .ToHashSet();
        return _scene.CaptureStrokeIntersectionsAtFillAnchor(
            objectIndex,
            partIndex,
            startEndpoint: handle == EditHandleKind.LineStart,
            _frame,
            excludedLineObjectIndices: fullCurveLineObjects);
    }

    private void UpdateFillEdgeBezierPointer(PointF world, bool resetCurvature = false)
    {
        var session = _fillEdgeBezierEditSession;
        if (session is null || !ReferenceEquals(session.Scene, _scene)) return;
        if (!_scene.TryInverseMapObjectPoint(session.ObjectIndex, world, out world)) return;

        var start = session.Start;
        var control1 = session.Control1;
        var control2 = session.Control2;
        var end = session.End;
        CubicDrawingPreviewSegment currentSegment;
        if (resetCurvature)
        {
            currentSegment = ResetBezierCurvature(start, end);
        }
        else
        {
            var adjusted = session.Handle switch
            {
                EditHandleKind.LineStart => MoveHandleWithPointer(session.Start, session.PointerStart, world),
                EditHandleKind.BezierControl => MoveHandleWithPointer(session.Control1, session.PointerStart, world),
                EditHandleKind.BezierControl2 => MoveHandleWithPointer(session.Control2, session.PointerStart, world),
                EditHandleKind.LineEnd => MoveHandleWithPointer(session.End, session.PointerStart, world),
                _ => PointF.Empty
            };
            adjusted = VectorUnits.Quantize(SnapDrawingPoint(adjusted));
            currentSegment = AdjustFillEdgeBezierHandle(
                start,
                control1,
                control2,
                end,
                session.Handle,
                adjusted);
        }
        start = currentSegment.Start;
        control1 = currentSegment.Control1;
        control2 = currentSegment.Control2;
        end = currentSegment.End;
        if (!ShouldApplyFillEdgeBezierPointer(session.LastAppliedSegment, currentSegment)) return;

        if (!_scene.SetPathBezierSegmentForPreview(
                session.ObjectIndex,
                session.PartIndex,
                start,
                control1,
                control2,
                end,
                preserveStraightAdjacentSegments: session.Handle is EditHandleKind.LineStart or EditHandleKind.LineEnd,
                handle: session.Handle))
        {
            return;
        }

        _scene.UpdateFillBoundaryStrokeLinks(
            session.LinkedStrokes,
            start,
            control1,
            control2,
            end,
            rebuildGeometryIndex: false);
        if (session.Handle is EditHandleKind.LineStart or EditHandleKind.LineEnd)
        {
            _scene.UpdateSharedBoundaryIntersection(
                session.SharedIntersection,
                session.Handle == EditHandleKind.LineStart ? start : end,
                rebuildGeometryIndex: false);
        }

        session.LastAppliedSegment = currentSegment;
        var initialSegment = new CubicDrawingPreviewSegment(
            session.Start,
            session.Control1,
            session.Control2,
            session.End);
        session.BoundaryGeometryChanged = currentSegment != initialSegment;
        session.Changed = ShouldCommitFillEdgeBezierPointer(
            session.IncludesAnchorInsertion,
            initialSegment,
            currentSegment);
        RefreshFillEdgeBezierOverlayDuringPointer(session);
        _stage.Invalidate();
    }

    private void RefreshFillEdgeBezierOverlayDuringPointer(FillEdgeBezierEditSession session)
    {
        var pieces = _scene.RefreshFillBezierSegmentPiecesForPreview(
            session.ObjectIndex,
            _fillEdgeBezierOverlayPieces,
            session.PartIndex,
            session.Handle);
        if (pieces.Length == 0)
        {
            UpdateFillEdgeBezierOverlay();
            return;
        }

        _fillEdgeBezierOverlayPieces = pieces;
        UpdateFillEdgeBezierOverlaySegmentsInPlace(
            session.ObjectIndex,
            pieces,
            _fillEdgeBezierActivePieceIndex);
    }

    private void UpdateFillEdgeBezierOverlaySegmentsInPlace(
        int objectIndex,
        IReadOnlyList<FillBezierSegmentPiece> pieces,
        int activePieceIndex)
    {
        if (_fillEdgeBezierOverlaySegments.Length != pieces.Count)
        {
            _fillEdgeBezierOverlaySegments = CreateFillEdgeBezierOverlaySegments(pieces);
            CaptureFillEdgeBezierOverlayBase(objectIndex);
            _stage.SetOwnedFillEdgeBezierOverlay(
                objectIndex,
                _fillEdgeBezierOverlaySegments,
                activePieceIndex);
            return;
        }

        var changed = false;
        for (var index = 0; index < pieces.Count; index++)
        {
            var piece = pieces[index];
            var segment = new FillEdgeBezierOverlaySegment(
                piece.PieceIndex,
                piece.Start,
                piece.Control1,
                piece.Control2,
                piece.End);
            if (_fillEdgeBezierOverlaySegments[index] == segment) continue;
            _fillEdgeBezierOverlaySegments[index] = segment;
            changed = true;
        }

        if (changed)
        {
            CaptureFillEdgeBezierOverlayBase(objectIndex);
            _stage.NotifyOwnedFillEdgeBezierOverlayChanged(
                objectIndex,
                _fillEdgeBezierOverlaySegments,
                activePieceIndex);
        }
    }

    private static FillEdgeBezierOverlaySegment[] CreateFillEdgeBezierOverlaySegments(
        IReadOnlyList<FillBezierSegmentPiece> pieces)
    {
        var segments = new FillEdgeBezierOverlaySegment[pieces.Count];
        for (var index = 0; index < pieces.Count; index++)
        {
            var piece = pieces[index];
            segments[index] = new FillEdgeBezierOverlaySegment(
                piece.PieceIndex,
                piece.Start,
                piece.Control1,
                piece.Control2,
                piece.End);
        }

        return segments;
    }

    internal static CubicDrawingPreviewSegment AdjustFillEdgeBezierHandle(
        PointF start,
        PointF control1,
        PointF control2,
        PointF end,
        EditHandleKind handle,
        PointF adjusted)
    {
        var preserveStraight = handle is EditHandleKind.LineStart or EditHandleKind.LineEnd
            && VectorScene.IsStraightBezierSegment(start, control1, control2, end);
        switch (handle)
        {
            case EditHandleKind.LineStart:
                control1 = new PointF(
                    control1.X + adjusted.X - start.X,
                    control1.Y + adjusted.Y - start.Y);
                start = adjusted;
                break;
            case EditHandleKind.BezierControl:
                control1 = adjusted;
                break;
            case EditHandleKind.BezierControl2:
                control2 = adjusted;
                break;
            case EditHandleKind.LineEnd:
                control2 = new PointF(
                    control2.X + adjusted.X - end.X,
                    control2.Y + adjusted.Y - end.Y);
                end = adjusted;
                break;
        }

        if (preserveStraight)
        {
            control1 = Lerp(start, end, 1f / 3f);
            control2 = Lerp(start, end, 2f / 3f);
        }

        return new CubicDrawingPreviewSegment(start, control1, control2, end);
    }

    internal static bool IsBezierCurvatureResetGesture(
        MouseButtons button,
        bool altPressed,
        EditHandleKind handle)
    {
        return button == MouseButtons.Left
            && altPressed
            && handle is EditHandleKind.BezierControl or EditHandleKind.BezierControl2;
    }

    internal static CubicDrawingPreviewSegment ResetBezierCurvature(PointF start, PointF end)
    {
        return new CubicDrawingPreviewSegment(
            start,
            VectorUnits.Quantize(Lerp(start, end, 1f / 3f)),
            VectorUnits.Quantize(Lerp(start, end, 2f / 3f)),
            end);
    }

    internal static EditHandleKind ReverseFillEdgeBezierHandle(EditHandleKind handle)
    {
        return handle switch
        {
            EditHandleKind.LineStart => EditHandleKind.LineEnd,
            EditHandleKind.LineEnd => EditHandleKind.LineStart,
            EditHandleKind.BezierControl => EditHandleKind.BezierControl2,
            EditHandleKind.BezierControl2 => EditHandleKind.BezierControl,
            _ => handle
        };
    }

    internal static bool ShouldCommitFillEdgeBezierPointer(
        bool includesAnchorInsertion,
        CubicDrawingPreviewSegment initial,
        CubicDrawingPreviewSegment current)
    {
        return includesAnchorInsertion || current != initial;
    }

    internal static bool ShouldApplyFillEdgeBezierPointer(
        CubicDrawingPreviewSegment lastApplied,
        CubicDrawingPreviewSegment current)
    {
        return current != lastApplied;
    }

    internal static bool ShouldUpdateFillEdgeBezierPointer(
        MouseButtons button,
        bool includesAnchorInsertion,
        bool pointerMoved)
    {
        return button == MouseButtons.Left
            && (includesAnchorInsertion || pointerMoved);
    }

    internal static bool ShouldUpdateSelectionPointer(
        EditHandleKind handle,
        bool pointerMoved,
        bool dragThresholdExceeded)
    {
        return pointerMoved;
    }

    internal static bool FillEdgeOverlayOwnsPointer(
        FillEdgeBezierOverlayHit overlayHit,
        bool selectableLineHit)
    {
        return overlayHit.IsValid
            && (overlayHit.Handle != EditHandleKind.None || !selectableLineHit);
    }

    internal static PointF MoveHandleWithPointer(
        PointF originalHandle,
        PointF pointerStart,
        PointF pointer)
    {
        return new PointF(
            originalHandle.X + pointer.X - pointerStart.X,
            originalHandle.Y + pointer.Y - pointerStart.Y);
    }

    private void CompleteFillEdgeBezierPointer()
    {
        FlushFillEdgeBezierPreview();
        var session = _fillEdgeBezierEditSession;
        _fillEdgeBezierEditSession = null;
        _stage.SetFillEdgeBezierPointerEditing(false);
        if (session is null || !ReferenceEquals(session.Scene, _scene)) return;

        if (session.Changed)
        {
            if (session.BoundaryGeometryChanged)
            {
                _scene.CompletePathBezierPreview(
                    session.ObjectIndex,
                    rebuildGeometryIndex: false);
                var hasFinalSegment = _scene.TryGetPathBezierSegment(
                    session.ObjectIndex,
                    session.PartIndex,
                    out var finalSegment);
                _scene.NormalizeFillBoundaryOverlaps(
                    session.ObjectIndex,
                    session.InitialFillContours,
                    rebuildGeometryIndex: false);
                if (hasFinalSegment)
                {
                    _scene.UpdateFillBoundaryStrokeLinks(
                        session.LinkedStrokes,
                        finalSegment.Start,
                        finalSegment.Control1,
                        finalSegment.Control2,
                        finalSegment.End,
                        rebuildGeometryIndex: false);
                }
            }
            _scene.CompleteDeferredBuild();
            if (session.BoundaryGeometryChanged)
            {
                MergeSelectedFillsAfterGeometryEdit(connectNearby: true);
            }
            session.Snapshot.DetachSharedGeometry();
            PushUndoSnapshot(session.Snapshot);
            _timeline.RefreshTimeline();
            RebuildDrawingObjectUnderlay();
            _hierarchyPanel.RefreshScene();
        }
        else
        {
            RestoreCanvasMutationSnapshot(session.Snapshot);
        }

        var selected = FindActiveObjectByStackKey(session.StackKey);
        if (selected < 0
            && (uint)_selectedObject < _scene.ObjectCount
            && _scene.HasFill(_selectedObject))
        {
            selected = _selectedObject;
        }
        if (!RestoreFillEdgeSelection(selected, session.FillPartSelection)) ClearSelection();

        UpdateFillEdgeBezierOverlay();
        UpdateInspector();
        _stage.Invalidate();
    }

    private void CompleteFillEdgeBezierSessionForContextChange()
    {
        if (_fillEdgeBezierArmedSession is not null)
        {
            CancelArmedFillEdgeBezierPointer();
            FinishPointerInteraction();
            return;
        }
        if (_fillEdgeBezierEditSession is null) return;
        CompleteFillEdgeBezierPointer();
        FinishPointerInteraction();
    }

    private void CancelFillEdgeBezierPointer(bool restore)
    {
        if (_fillEdgeBezierArmedSession is not null)
        {
            CancelArmedFillEdgeBezierPointer();
            if (_fillEdgeBezierEditSession is null) return;
        }
        _pendingFillEdgeBezierWorld = null;
        if (!HasPendingFrameCoalescedWork()) _lineDragPreviewTimer.Stop();
        var session = _fillEdgeBezierEditSession;
        _fillEdgeBezierEditSession = null;
        _stage.SetFillEdgeBezierPointerEditing(false);
        if (restore && session is not null && ReferenceEquals(session.Scene, _scene))
        {
            RestoreCanvasMutationSnapshot(session.Snapshot);
            var restored = FindActiveObjectByStackKey(session.StackKey);
            if (!RestoreFillEdgeSelection(restored, session.FillPartSelection)) ClearSelection();
        }

        UpdateFillEdgeBezierOverlay();
    }

    private FillPartSelectionIdentity? CaptureSelectedFillPartSelection(int objectIndex)
    {
        if (!TryGetSelectedFillPartIndex(objectIndex, _selectedElements, out _)) return null;
        var selected = _selectedElements[0];
        var contours = _scene.GetFillPartContours(selected, _frame);
        return new FillPartSelectionIdentity(BuildFillPartSelectionProbes(contours));
    }

    private bool RestoreFillEdgeSelection(
        int objectIndex,
        FillPartSelectionIdentity? fillPartSelection)
    {
        if ((uint)objectIndex >= _scene.ObjectCount || !_scene.IsObjectSelectable(objectIndex, _frame))
        {
            return false;
        }

        if (fillPartSelection is null)
        {
            SetSelection(objectIndex);
            return true;
        }

        if (TryResolveFillPartSelection(objectIndex, fillPartSelection, out var resolved))
        {
            SetSelection(resolved);
            return true;
        }

        return false;
    }

    private bool TryResolveFillPartSelection(
        int objectIndex,
        FillPartSelectionIdentity identity,
        out DrawingElementHit hit)
    {
        return TryResolveFillPartSelection(
            _scene,
            _frame,
            objectIndex,
            identity.InteriorProbes,
            out hit);
    }

    internal static bool TryResolveFillPartSelection(
        VectorScene scene,
        int frame,
        int objectIndex,
        IReadOnlyList<PointF> interiorProbes,
        out DrawingElementHit hit)
    {
        hit = DrawingElementHit.None;
        var parts = scene.GetFillParts(objectIndex, frame);
        if (parts.Length == 0) return false;
        if (parts.Length == 1)
        {
            hit = new DrawingElementHit(
                new DrawingElementKey(objectIndex, DrawingElementKind.Fill, parts[0].PartIndex),
                0,
                0,
                1);
            return true;
        }

        var bestPartIndex = -1;
        var bestScore = 0;
        foreach (var part in parts)
        {
            var score = interiorProbes.Count(probe =>
                PointInFillPartContours(probe, part.Contours));
            if (score <= bestScore) continue;

            bestPartIndex = part.PartIndex;
            bestScore = score;
        }

        if (bestPartIndex < 0 || bestScore == 0) return false;
        hit = new DrawingElementHit(
            new DrawingElementKey(objectIndex, DrawingElementKind.Fill, bestPartIndex),
            0,
            0,
            1);
        return true;
    }

    internal static PointF[] BuildFillPartSelectionProbes(IReadOnlyList<PointF[]> contours)
    {
        var points = contours.SelectMany(contour => contour).ToArray();
        if (points.Length < 3) return [];

        var left = points.Min(point => point.X);
        var top = points.Min(point => point.Y);
        var right = points.Max(point => point.X);
        var bottom = points.Max(point => point.Y);
        var width = right - left;
        var height = bottom - top;
        var probes = new List<PointF>(128);

        void TryAdd(PointF candidate)
        {
            if (!float.IsFinite(candidate.X)
                || !float.IsFinite(candidate.Y)
                || !PointInFillPartContours(candidate, contours)
                || probes.Any(existing => Distance(existing, candidate) <= 0.001f))
            {
                return;
            }

            probes.Add(candidate);
        }

        TryAdd(new PointF((left + right) * 0.5f, (top + bottom) * 0.5f));
        const int gridSize = 9;
        for (var y = 0; y < gridSize; y++)
        {
            for (var x = 0; x < gridSize; x++)
            {
                TryAdd(new PointF(
                    left + width * (x + 0.5f) / gridSize,
                    top + height * (y + 0.5f) / gridSize));
            }
        }

        var inwardStep = Math.Max(0.25f, Math.Min(width, height) / 2048f);
        foreach (var contour in contours)
        {
            if (contour.Length < 3) continue;
            var stride = Math.Max(1, contour.Length / 24);
            for (var index = 0; index < contour.Length; index += stride)
            {
                var next = contour[(index + 1) % contour.Length];
                var current = contour[index];
                var midpoint = Lerp(current, next, 0.5f);
                var dx = next.X - current.X;
                var dy = next.Y - current.Y;
                var length = MathF.Sqrt(dx * dx + dy * dy);
                if (length <= 0.001f) continue;
                var normal = new PointF(-dy / length * inwardStep, dx / length * inwardStep);
                TryAdd(new PointF(midpoint.X + normal.X, midpoint.Y + normal.Y));
                TryAdd(new PointF(midpoint.X - normal.X, midpoint.Y - normal.Y));
            }
        }

        return probes.ToArray();
    }

    private static bool PointInFillPartContours(PointF point, IReadOnlyList<PointF[]> contours)
    {
        var inside = false;
        foreach (var contour in contours)
        {
            if (contour.Length < 3) continue;
            var contourInside = false;
            for (var index = 0; index < contour.Length; index++)
            {
                var previous = contour[(index - 1 + contour.Length) % contour.Length];
                var current = contour[index];
                if ((current.Y > point.Y) == (previous.Y > point.Y)) continue;
                var intersectionX = (previous.X - current.X)
                    * (point.Y - current.Y)
                    / (previous.Y - current.Y)
                    + current.X;
                if (point.X < intersectionX) contourInside = !contourInside;
            }

            if (contourInside) inside = !inside;
        }

        return inside;
    }

    private bool TryGetFillEdgeBezierOverlayPiece(
        int pieceIndex,
        out FillBezierSegmentPiece piece)
    {
        foreach (var candidate in _fillEdgeBezierOverlayPieces)
        {
            if (candidate.PieceIndex != pieceIndex) continue;
            piece = candidate;
            return true;
        }

        piece = default;
        return false;
    }

    private static FillBezierSegmentPiece ResolvePresentedFillEdgeBezierPiece(
        FillBezierSegmentPiece piece,
        FillEdgeBezierOverlayHit hit)
    {
        var relativeStart = Math.Clamp(hit.SourceStartT, 0, 1);
        var relativeEnd = Math.Clamp(hit.SourceEndT, relativeStart, 1);
        if (relativeStart <= DrawingTopologyRules.UnitIntersectionTolerance
            && relativeEnd >= 1f - DrawingTopologyRules.UnitIntersectionTolerance)
        {
            return piece;
        }

        var source = VectorScene.CubicSubcurve(
            piece.Start,
            piece.Control1,
            piece.Control2,
            piece.End,
            relativeStart,
            relativeEnd);
        var sourceRange = piece.EndT - piece.StartT;
        return piece with
        {
            StartT = piece.StartT + sourceRange * relativeStart,
            EndT = piece.StartT + sourceRange * relativeEnd,
            Start = source.Start,
            Control1 = source.Control1,
            Control2 = source.Control2,
            End = source.End,
            StartIsVirtualAnchor = piece.StartIsVirtualAnchor
                || relativeStart > DrawingTopologyRules.UnitIntersectionTolerance,
            EndIsVirtualAnchor = piece.EndIsVirtualAnchor
                || relativeEnd < 1f - DrawingTopologyRules.UnitIntersectionTolerance
        };
    }

    private bool CanEditFillEdgeBezierPiece(
        int objectIndex,
        int? selectedFillPartIndex,
        FillBezierSegmentPiece sourcePiece)
    {
        var fillParts = _scene.GetFillParts(objectIndex, _frame);
        if (fillParts.Length <= 1)
        {
            return true;
        }

        if (!_scene.TryResolveFillPartForBezierSegmentPiece(
                objectIndex,
                _frame,
                sourcePiece,
                out var resolvedPartIndex))
        {
            return false;
        }

        return selectedFillPartIndex is null || selectedFillPartIndex == resolvedPartIndex;
    }

    private void ClearFillEdgeBezierOverlayState()
    {
        _fillEdgeBezierActivePartIndex = -1;
        _fillEdgeBezierActivePieceIndex = -1;
        _fillEdgeBezierOverlayPieces = [];
        _fillEdgeBezierOverlaySegments = [];
        _fillEdgeBezierOverlayBaseObject = -1;
        _fillEdgeBezierOverlayBasePosition = PointF.Empty;
        _stage.ClearFillEdgeBezierOverlay();
    }

    // --- Unselected-edge press-drag (no UI) and segment detach -------------------

    private bool TryResolveUnselectedFillEdgeCandidate(Point screen, out int objectIndex, out int partIndex)
    {
        objectIndex = -1;
        partIndex = -1;
        if (_tool != ToolMode.Select || _stage is null) return false;
        var world = _stage.ScreenToWorld(screen);
        var hit = _scene.HitTestElement(world, _frame, SelectionToleranceWorld());
        if (!hit.IsValid) return false;
        var obj = hit.Key.ObjectIndex;
        if (obj < 0 || obj >= _scene.ObjectCount || obj == _selectedObject) return false;
        if (!_scene.HasFill(obj)
            || !IsFillShape(_scene.ShapeKind[obj])
            || IsWholeObjectOnlyObject(obj)) return false;
        if (!_scene.TryConvertFillToBezierPath(obj, rebuildGeometryIndex: false)) return false;
        if (!_scene.TryGetPathBezierWorldContours(obj, out var contours)) return false;

        var tolerance = SelectionToleranceWorld() * 2f;
        var best = float.MaxValue;
        var bestPart = -1;
        var currentPart = 0;
        for (var contourIndex = 0; contourIndex < contours.Length; contourIndex++)
        {
            var contour = contours[contourIndex];
            if (contour.Length < 2) continue;
            for (var segmentIndex = 0; segmentIndex < contour.Length; segmentIndex++)
            {
                // Detached (stroke-hidden) segments are no longer bend candidates: the
                // fill cools its previous outline and the floating line owns the edge.
                if (_scene.IsBoundaryStrokePartHidden(obj, currentPart))
                {
                    currentPart++;
                    continue;
                }

                // Bezier closest-point matching keeps curved segments reachable: the
                // anchor-to-anchor chord distance would miss presses on the arc body.
                var distance = _scene.TryGetClosestPointOnPathBezierSegment(
                    obj,
                    currentPart,
                    world,
                    out _,
                    out _,
                    out var curveDistance)
                    ? curveDistance
                    : DistanceToSegment(world, contour[segmentIndex].Anchor, contour[(segmentIndex + 1) % contour.Length].Anchor);
                if (distance < best)
                {
                    best = distance;
                    bestPart = currentPart;
                }
                currentPart++;
            }
        }

        if (bestPart < 0 || best > tolerance) return false;
        objectIndex = obj;
        partIndex = bestPart;
        return true;
    }

    private static float DistanceToSegment(PointF point, PointF a, PointF b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var lengthSquared = dx * dx + dy * dy;
        if (lengthSquared <= float.Epsilon)
        {
            return MathF.Sqrt((point.X - a.X) * (point.X - a.X) + (point.Y - a.Y) * (point.Y - a.Y));
        }
        var t = ((point.X - a.X) * dx + (point.Y - a.Y) * dy) / lengthSquared;
        t = Math.Clamp(t, 0f, 1f);
        var projX = a.X + t * dx;
        var projY = a.Y + t * dy;
        return MathF.Sqrt((point.X - projX) * (point.X - projX) + (point.Y - projY) * (point.Y - projY));
    }

    private static (PointF? Start, PointF? Control1, PointF? Control2, PointF? End) ResolveWorldSegment(
        PathBezierNode[][] contours, int partIndex)
    {
        var currentPart = 0;
        for (var contourIndex = 0; contourIndex < contours.Length; contourIndex++)
        {
            var contour = contours[contourIndex];
            if (partIndex >= currentPart + contour.Length)
            {
                currentPart += contour.Length;
                continue;
            }
            if (contour.Length < 2) return (null, null, null, null);
            var segmentIndex = partIndex - currentPart;
            var nextIndex = (segmentIndex + 1) % contour.Length;
            var anchor = contour[segmentIndex];
            var next = contour[nextIndex];
            return (anchor.Anchor, anchor.OutgoingControl, next.IncomingControl, next.Anchor);
        }
        return (null, null, null, null);
    }

    private void BeginFillEdgeNoUiArc((int ObjectIndex, int PartIndex) candidate, PointF world)
    {
        _fillEdgePreSelect = null;
        var obj = candidate.ObjectIndex;
        var part = candidate.PartIndex;
        if (!_scene.TryConvertFillToBezierPath(obj, rebuildGeometryIndex: false)
            || !_scene.TryGetPathBezierWorldContours(obj, out var contours))
        {
            return;
        }

        var (start, control1, control2, end) = ResolveWorldSegment(contours, part);
        if (start is null) return;

        var snapshot = CreateCanvasGeometryMutationSnapshot([obj]);
        _fillEdgeSuppressOverlay = true;
        _stage.ClearFillEdgeBezierOverlay();
        _fillEdgeNoUiArc = (obj, part, start.Value, control1!.Value, control2!.Value, end!.Value, world, snapshot);
    }

    private void UpdateFillEdgeNoUiArc(PointF world)
    {
        var arc = _fillEdgeNoUiArc;
        if (arc is null) return;
        var control = VectorUnits.Quantize(SnapDrawingPoint(world));
        _scene.SetPathBezierSegmentForPreview(
            arc.Value.ObjectIndex,
            arc.Value.PartIndex,
            arc.Value.Start,
            control,
            control,
            arc.Value.End,
            preserveStraightAdjacentSegments: false,
            handle: EditHandleKind.BezierControl);
        _stage.Invalidate();
    }

    private void CompleteFillEdgeNoUiArc()
    {
        var arc = _fillEdgeNoUiArc;
        _fillEdgeNoUiArc = null;
        if (arc is null) return;
        _scene.CompletePathBezierPreview(arc.Value.ObjectIndex);
        PushUndoSnapshot(arc.Value.Snapshot);
        _fillEdgeSuppressOverlay = false;
        // Releasing a bend selects the bent boundary segment itself so the orange
        // stroke-path anchor UI shows for that segment (matching line selection),
        // not the whole fill-boundary overlay. Falls back to whole-object selection
        // only when the segment can no longer be resolved to a boundary part.
        if (!TryCreateBoundarySegmentHit(arc.Value.ObjectIndex, arc.Value.PartIndex, out var segmentHit))
        {
            SetSelection(arc.Value.ObjectIndex);
        }
        else
        {
            SetSelection(segmentHit);
        }

        UpdateFillEdgeBezierOverlay();
        _hierarchyPanel.RefreshScene();
        UpdateInspector();
        _stage.Invalidate();
        FinishPointerInteraction();
    }

    private void BeginFillEdgeDetach((int ObjectIndex, int PartIndex) candidate, PointF world)
    {
        var snapshot = CreateCanvasGeometryMutationSnapshot([candidate.ObjectIndex]);
        // Detach genuinely removes the segment's stroke from the source outline (each
        // detached edge leaves its own exposed gap) while the source fill keeps its
        // previous closed outline, and the pulled piece is fully independent. Already
        // detached (stroke-hidden) segments are skipped so dragging their fill-boundary
        // overlay body cannot spawn a duplicate line.
        if (_scene.IsBoundarySegmentDetached(candidate.ObjectIndex, candidate.PartIndex)
            || !_scene.SplitOutSegmentAsNewObject(candidate.ObjectIndex, candidate.PartIndex, out var newObject))
        {
            _fillEdgeDetachPending = null;
            return;
        }

        RebuildDrawingObjectUnderlay();
        _hierarchyPanel.RefreshScene();
        UpdateInspector();
        SetSelection(newObject);
        _fillEdgeDetachPending = null;
        _fillEdgeDetachMoving = (newObject, world, snapshot);
        _stage.Invalidate();
    }

    private void UpdateFillEdgeDetachMove(PointF world)
    {
        var moving = _fillEdgeDetachMoving;
        if (moving is null) return;
        var dx = world.X - moving.Value.LastWorld.X;
        var dy = world.Y - moving.Value.LastWorld.Y;
        _scene.TranslateObjectsForPreview(new[] { moving.Value.NewObjectIndex }, dx, dy);
        _fillEdgeDetachMoving = (moving.Value.NewObjectIndex, world, moving.Value.Snapshot);
        _stage.Invalidate();
    }

    private void CompleteFillEdgeDetach()
    {
        var moving = _fillEdgeDetachMoving;
        _fillEdgeDetachMoving = null;
        if (moving is null) return;
        PushUndoSnapshot(moving.Value.Snapshot);
        RebuildDrawingObjectUnderlay();
        _hierarchyPanel.RefreshScene();
        UpdateInspector();
        _stage.Invalidate();
        FinishPointerInteraction();
    }

    private void SelectPreSelectObject((int ObjectIndex, int PartIndex) candidate, Point screen)
    {
        _fillEdgeSuppressOverlay = false;

        // A plain click (no drag) on a fill boundary edge selects that edge as a
        // BoundaryStroke element so the orange stroke-path UI shows, matching line
        // selection. Falls back to whole-object selection only when the candidate
        // part no longer exists (e.g. topology split changed part numbering).
        var world = _stage.ScreenToWorld(screen);
        var hit = _scene.HitTestElement(world, _frame, SelectionToleranceWorld());
        if (hit.IsValid
            && hit.Key.Kind == DrawingElementKind.BoundaryStroke
            && hit.Key.ObjectIndex == candidate.ObjectIndex)
        {
            SetSelection(hit);
        }
        else if (TryCreateBoundarySegmentHit(candidate.ObjectIndex, candidate.PartIndex, out var candidateHit))
        {
            SetSelection(candidateHit);
        }
        else
        {
            SetSelection(candidate.ObjectIndex);
        }

        UpdateFillEdgeBezierOverlay();
        _hierarchyPanel.RefreshScene();
        UpdateInspector();
        _stage.Invalidate();
    }

    private bool TryCreateBoundarySegmentHit(int objectIndex, int partIndex, out DrawingElementHit hit)
    {
        // BoundaryStroke hit keys carry a split-sequence PartIndex, so the exact bezier
        // contour part index is resolved back through the scene's boundary parts.
        return _scene.TryGetBoundaryStrokeHitForPart(objectIndex, partIndex, _frame, out hit);
    }

    private bool TryBeginSelectedBoundarySegmentDetach(PointF world)
    {
        if (_selectedObject < 0
            || (uint)_selectedObject >= _scene.ObjectCount
            || _selectedElements.Count == 0)
        {
            return false;
        }

        var tolerance = SelectionToleranceWorld() * 2f;
        foreach (var element in _selectedElements)
        {
            if (element.Key.Kind != DrawingElementKind.BoundaryStroke
                || element.Key.ObjectIndex != _selectedObject
                || !_scene.TryGetExactFillBezierSegmentForBoundary(element, _frame, out var segment)
                || _scene.IsBoundarySegmentDetached(_selectedObject, segment.PartIndex))
            {
                continue;
            }

            if (!_scene.TryGetClosestPointOnPathBezierSegment(
                    _selectedObject,
                    segment.PartIndex,
                    world,
                    out _,
                    out _,
                    out var distance))
            {
                continue;
            }

            if (distance > tolerance) continue;

            _fillEdgeDetachPending = (_selectedObject, segment.PartIndex);
            return true;
        }

        return false;
    }

    private void UpdateFillEdgeBezierOverlay()
    {
        if (_fillEdgeSuppressOverlay)
        {
            _stage.ClearFillEdgeBezierOverlay();
            return;
        }

        if (_tool != ToolMode.Select
            || _selectedObjects.Count != 1
            || IsSceneCompositionContext()
            || IsScene3DView()
            || _selectedObject < 0
            || _selectedObjects[0] != _selectedObject
            || !_scene.IsObjectSelectable(_selectedObject, _frame)
            || !_scene.HasFill(_selectedObject)
            || !IsFillShape(_scene.ShapeKind[_selectedObject])
            // Whole-object-only shapes (bitmaps, imported SVG, text, non-region mixing
            // strokes) carry no editable vertex geometry. Offering the fill-boundary
            // bezier overlay for them would let the Select tool write vertex data that
            // their renderers never honour, which visibly corrupts the object.
            || IsWholeObjectOnlyObject(_selectedObject))
        {
            ClearFillEdgeBezierOverlayState();
            return;
        }

        var hasSelectedFillPart = TryGetSelectedFillPartIndex(
            _selectedObject,
            _selectedElements,
            out var selectedFillPartIndex);
        var hasEditableFillSelection = _selectedElements.Count == 0 || hasSelectedFillPart;
        var selectedBoundaryPartIndex = -1;
        var hasSelectedBoundarySegment = !hasEditableFillSelection
            && TryGetSelectedFillBoundaryBezierPart(
                _scene,
                _frame,
                _selectedObject,
                _selectedElements,
                out selectedBoundaryPartIndex);
        if (hasSelectedBoundarySegment)
        {
            // A selected boundary edge presents the orange stroke-path selection UI on
            // its own; the editable fill-boundary overlay returns when the fill or the
            // whole object is selected again.
            ClearFillEdgeBezierOverlayState();
            return;
        }

        if (!hasEditableFillSelection)
        {
            ClearFillEdgeBezierOverlayState();
            return;
        }

        var pieces = _scene.GetExposedFillBezierSegmentPieces(
            _selectedObject,
            _frame,
            hasSelectedFillPart ? selectedFillPartIndex : null,
            includeCoincidentStrokes: true);
        if (pieces.Length == 0)
        {
            ClearFillEdgeBezierOverlayState();
            return;
        }

        var targetChanged = _stage.FillEdgeBezierOverlayTargetObject != _selectedObject;
        var activePieceArrayIndex = -1;
        if (hasSelectedBoundarySegment)
        {
            activePieceArrayIndex = Array.FindIndex(pieces, piece =>
                piece.SourcePartIndex == selectedBoundaryPartIndex);
        }
        else
        {
            if (!targetChanged)
            {
                activePieceArrayIndex = Array.FindIndex(pieces, piece =>
                    piece.PieceIndex == _fillEdgeBezierActivePieceIndex
                    && piece.SourcePartIndex == _fillEdgeBezierActivePartIndex);
            }
            if (activePieceArrayIndex < 0)
            {
                activePieceArrayIndex = Array.FindIndex(pieces, piece =>
                    piece.SourcePartIndex == _fillEdgeBezierActivePartIndex);
            }
        }

        if (activePieceArrayIndex < 0) activePieceArrayIndex = 0;
        var activePiece = pieces[activePieceArrayIndex];

        _fillEdgeBezierActivePartIndex = activePiece.SourcePartIndex;
        _fillEdgeBezierActivePieceIndex = activePiece.PieceIndex;
        _fillEdgeBezierOverlayPieces = pieces;
        _fillEdgeBezierOverlaySegments = CreateFillEdgeBezierOverlaySegments(pieces);
        CaptureFillEdgeBezierOverlayBase(_selectedObject);
        _stage.SetOwnedFillEdgeBezierOverlay(
            _selectedObject,
            _fillEdgeBezierOverlaySegments,
            _fillEdgeBezierActivePieceIndex);
    }

    private void BeginGradientPointer(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || _startWorld is not { } world)
        {
            FinishPointerInteraction();
            return;
        }

        var overlayHit = _stage.HitTestGradientOverlay(e.Location);
        if (overlayHit.Kind != GradientHandleKind.None
            && _selectedObject >= 0
            && _scene.HasGradient(_selectedObject))
        {
            _gradientEditChanged = false;
            _gradientEditSnapshot = CreateCanvasMutationSnapshot([_selectedObject]);
            _gradientHandle = overlayHit.Kind;
            _gradientStopIndex = overlayHit.StopIndex;
            UpdateInteractionCursor(e.Location);
            return;
        }

        var hit = _scene.HitTestElement(world, _frame, SelectionToleranceWorld());
        if (!hit.IsValid
            || !_scene.IsObjectSelectable(hit.Key.ObjectIndex, _frame)
            || !SupportsGradient(_scene.ShapeKind[hit.Key.ObjectIndex]))
        {
            FinishPointerInteraction();
            return;
        }

        var objectIndex = hit.Key.ObjectIndex;
        _gradientEditChanged = false;
        _gradientEditSnapshot = CreateCanvasMutationSnapshot([objectIndex]);
        if (!_scene.HasGradient(objectIndex))
        {
            var kind = _materialEditor.GradientKind == GradientKind.Solid
                ? GradientKind.Linear
                : _materialEditor.GradientKind;
            if (kind == GradientKind.ShapeRadial)
            {
                _scene.SetGradientPaint(objectIndex, kind, _materialEditor.GradientStops, start: world);
            }
            else
            {
                _scene.SetGradientPaint(objectIndex, kind, _materialEditor.GradientStops, world, world);
            }
            _gradientEditChanged = true;
        }

        SetSelection(objectIndex);
        _gradientHandle = _scene.GetGradientKind(objectIndex) == GradientKind.ShapeRadial
            ? GradientHandleKind.Start
            : GradientHandleKind.End;
        _gradientStopIndex = -1;
        UpdateGradientOverlay();
        UpdateInteractionCursor(e.Location);
    }

    private void UpdateGradientHandle(PointF world)
    {
        if (_gradientHandle == GradientHandleKind.None
            || _selectedObject < 0
            || !_scene.HasGradient(_selectedObject))
        {
            return;
        }

        var snapped = VectorUnits.Quantize(SnapDrawingPoint(world));
        var start = _scene.GetGradientStart(_selectedObject);
        var end = _scene.GetGradientEnd(_selectedObject);
        if (_gradientHandle == GradientHandleKind.Stop)
        {
            var stops = _scene.GetGradientStops(_selectedObject);
            if (_gradientStopIndex <= 0 || _gradientStopIndex >= stops.Length - 1) return;
            var axisX = end.X - start.X;
            var axisY = end.Y - start.Y;
            var axisLengthSquared = axisX * axisX + axisY * axisY;
            if (axisLengthSquared < 0.0001f) return;
            var projected = ((snapped.X - start.X) * axisX + (snapped.Y - start.Y) * axisY) / axisLengthSquared;
            var position = Math.Clamp(projected, stops[_gradientStopIndex - 1].Position + 0.01f, stops[_gradientStopIndex + 1].Position - 0.01f);
            if (Math.Abs(position - stops[_gradientStopIndex].Position) <= 0.0001f) return;
            stops[_gradientStopIndex] = new GradientStop(position, stops[_gradientStopIndex].Argb);
            _scene.SetGradientStops(_selectedObject, stops);
            _gradientEditChanged = true;
        }
        else
        {
            if (_gradientHandle == GradientHandleKind.Start)
            {
                if (_scene.GetGradientKind(_selectedObject) is GradientKind.Radial or GradientKind.ShapeRadial)
                {
                    end = new PointF(end.X + snapped.X - start.X, end.Y + snapped.Y - start.Y);
                }
                start = snapped;
                if (_scene.GetGradientKind(_selectedObject) == GradientKind.ShapeRadial
                    && GradientPaintUtilities.TryFindShapeBoundaryPoint(
                        _scene.GetObjectBoundaryContours(_selectedObject),
                        start,
                        new PointF(end.X - start.X, end.Y - start.Y),
                        out var boundary))
                {
                    end = boundary;
                }
            }
            else end = snapped;
            if (start == _scene.GetGradientStart(_selectedObject)
                && end == _scene.GetGradientEnd(_selectedObject)) return;
            _scene.ClearGradientPath(_selectedObject);
            _scene.SetLinearGradientEndpoints(_selectedObject, start, end);
            _gradientEditChanged = true;
        }
        UpdateGradientOverlay();
        _stage.Invalidate();
    }

    private void CompleteGradientPointer()
    {
        if (_gradientEditSnapshot is { } snapshot)
        {
            if (_gradientEditChanged) PushUndoSnapshot(snapshot);
            else RestoreCanvasMutationSnapshot(snapshot);
        }
        _gradientEditSnapshot = null;
        _gradientEditChanged = false;
        _gradientHandle = GradientHandleKind.None;
        _gradientStopIndex = -1;
        UpdateGradientOverlay();
        UpdateInspector();
        _stage.Invalidate();
    }

    private void CancelGradientPointer(bool restore)
    {
        if (restore && _gradientEditSnapshot is { } snapshot)
        {
            RestoreCanvasMutationSnapshot(snapshot);
        }

        _gradientEditSnapshot = null;
        _gradientEditChanged = false;
        _gradientHandle = GradientHandleKind.None;
        _gradientStopIndex = -1;
        UpdateGradientOverlay();
    }

    private void UpdateGradientOverlay()
    {
        if (_tool != ToolMode.Gradient
            || IsSceneCompositionContext()
            || IsScene3DView()
            || _selectedObject < 0
            || !_scene.HasGradient(_selectedObject))
        {
            _stage.ClearGradientOverlay();
            return;
        }

        _stage.SetGradientOverlay(
            _scene.GetGradientStart(_selectedObject),
            _scene.GetGradientEnd(_selectedObject),
            _scene.GetGradientKind(_selectedObject),
            _scene.GetGradientStops(_selectedObject));
    }

}
