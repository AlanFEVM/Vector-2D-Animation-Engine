using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

namespace VectorAnimationEngine;

internal sealed partial class MainForm : Form
{
    private void StageMouseDoubleClick(object? sender, MouseEventArgs e)
    {
        if (IsLassoTool(_tool) && HandleLassoMouseDoubleClick(e)) return;

        if (_tool == ToolMode.Pen)
        {
            if (_traditionalPenPointerDown) FinishTraditionalPenPoint(e.Location);
            FinishTraditionalPenPointerDrag();
            CancelTraditionalPenPath();
            return;
        }

        if (_tool == ToolMode.SimplePen)
        {
            if (_penSegmentDragging) CommitPenSegment();
            FinishPenSegmentPointerDrag();
            CancelPenCurve();
            return;
        }

        if (e.Button != MouseButtons.Left
            || _forceMarqueeOnPointerDown
            || IsScene3DView())
        {
            return;
        }

        var world = _stage.ScreenToWorld(e.Location);
        var hit = _scene.HitTestElement(world, _frame, SelectionToleranceWorld());
        if (IsSceneCompositionContext())
        {
            if (hit.IsValid && _sceneCompositionResult.TryGetOwner(hit.Key.ObjectIndex, out var owner))
            {
                OpenDrawingObjectEditor(owner.DrawingObjectId);
            }

            return;
        }

        if (_tool is ToolMode.Select or ToolMode.Transform or ToolMode.Distort
            && (_workspaceTabs.SelectedView == WorkspaceView.BasicDrawing || IsSceneMaskEditing())
            && !DrawingToolsBlocked()
            && hit.IsValid
            && (uint)hit.Key.ObjectIndex < _scene.ObjectCount
            && _scene.ShapeKind[hit.Key.ObjectIndex] == ShapeKind.Text
            && _scene.IsObjectSelectable(hit.Key.ObjectIndex, _frame))
        {
            var textObject = hit.Key.ObjectIndex;
            _pendingClickSelection = DrawingElementHit.None;
            _marqueeSelecting = false;
            _marqueeStart = null;
            _stage.ClearMarquee();
            FinishPointerInteraction();
            SetSelection(textObject);
            ActivateTool(ToolMode.Text);
            BeginExistingTextEdit(textObject);
            return;
        }

        if (!hit.IsValid && _stage.UnderlayScene is { } underlay)
        {
            var underlayHit = underlay.HitTestElement(world, 0, SelectionToleranceWorld());
            if (underlayHit.IsValid
                && _drawingObjectUnderlayResult.TryGetOwner(underlayHit.Key.ObjectIndex, out var owner)
                && OpenDrawingObjectEditor(owner.DrawingObjectId))
            {
                return;
            }
        }

        if (_tool != ToolMode.Select) return;
        if (hit.IsValid && IsWholeObjectOnlyObject(hit.Key.ObjectIndex))
        {
            _pendingClickSelection = DrawingElementHit.None;
            _marqueeSelecting = false;
            _marqueeStart = null;
            _stage.ClearMarquee();
            SetSelection(hit.Key.ObjectIndex);
            UpdateInspector();
            _stage.Invalidate();
            return;
        }
        if (!hit.IsValid || hit.Key.Kind is not (DrawingElementKind.Stroke or DrawingElementKind.BoundaryStroke)) return;

        var connected = _scene.GetConnectedStrokeElements(hit, _frame);
        if (connected.Length == 0) return;

        _pendingClickSelection = DrawingElementHit.None;
        _marqueeSelecting = false;
        _marqueeStart = null;
        _stage.ClearMarquee();
        SetSelection(connected, hit);
        _pointerHitWasAlreadySelected = true;
        CaptureEditStart(_selectedObject);
        UpdateInspector();
        _stage.Invalidate();
    }

    private void StageMouseMove(object? sender, MouseEventArgs e)
    {
        if (HandleReferenceCameraRightLookMouseMove(e)) return;

        if (_brushColorPaletteActive)
        {
            UpdateBrushColorPaletteHover(e.Location);
            return;
        }

        if (_tabletPressurePointerId is not null) return;

        if (IsLassoTool(_tool) && _lassoPointerActive)
        {
            UpdateLassoPointer(e.Location);
            return;
        }

        if (UpdateSpatialTransformKeyboardPointer(e.Location)) return;

        if (MotionTrackPointerMove(e)) return;

        if (_motionTrackMarqueeActive)
        {
            UpdateMotionTrackMarquee(e.Location);
            return;
        }

        if (_shotFramingPointerSession is not null)
        {
            if (e.Button == MouseButtons.Left) UpdateShotFramingPointer(e.Location);
            UpdateShotFramingGizmoCursor(e.Location);
            return;
        }

        if (_sceneLightGizmoPointerSession is not null)
        {
            if (e.Button == MouseButtons.Left) QueueSceneLightGizmoPointer(e.Location);
            UpdateSceneLightGizmoCursor(e.Location);
            return;
        }

        if (_fillEdgeNoUiArc.HasValue)
        {
            if (e.Button == MouseButtons.Left) UpdateFillEdgeNoUiArc(_stage.ScreenToWorld(e.Location));
            UpdateInteractionCursor(e.Location);
            return;
        }

        if (_fillEdgeDetachMoving.HasValue)
        {
            if (e.Button == MouseButtons.Left) UpdateFillEdgeDetachMove(_stage.ScreenToWorld(e.Location));
            UpdateInteractionCursor(e.Location);
            return;
        }

        if (_fillEdgeDetachPending.HasValue)
        {
            if (e.Button == MouseButtons.Left && PointerDragExceeded(e.Location))
                BeginFillEdgeDetach(_fillEdgeDetachPending.Value, _stage.ScreenToWorld(e.Location));
            UpdateInteractionCursor(e.Location);
            return;
        }

        if (_fillEdgePreSelect.HasValue)
        {
            if (e.Button == MouseButtons.Left && PointerDragExceeded(e.Location))
                BeginFillEdgeNoUiArc(_fillEdgePreSelect.Value, _stage.ScreenToWorld(e.Location));
            UpdateInteractionCursor(e.Location);
            return;
        }

        if (_tool == ToolMode.SnapPoint)
        {
            if (_snapPointEditSession is not null)
            {
                if (e.Button == MouseButtons.Left) UpdateSnapPointPointer(e.Location);
            }
            else
            {
                UpdateSnapPointHover(e.Location);
            }
            return;
        }

        if (_lastMouse is null)
        {
            if (IsShotDirectorContext())
            {
                UpdateShotFramingGizmoCursor(e.Location);
                return;
            }

            if (_tool == ToolMode.Pen)
            {
                UpdateTraditionalPenHover(e.Location);
                return;
            }

            if (_tool == ToolMode.SimplePen)
            {
                UpdatePenHover(e.Location);
                return;
            }

            if (TryBegin3DViewDragFromMove(e)) return;
            QueueHoverFeedback(e.Location);
            return;
        }

        var dx = e.X - _lastMouse.Value.X;
        var dy = e.Y - _lastMouse.Value.Y;
        _lastMouse = e.Location;
        if (_spatialTransformPointerSession is not null)
        {
            if (e.Button == MouseButtons.Left) QueueSpatialTransformPointer(e.Location);
            UpdateSpatialTransformCursor(e.Location);
            return;
        }
        if (IsBrushTool(_tool) || _tool == ToolMode.Eraser)
        {
            _stage.SetBrushTipCursor(e.Location, _brushShape, ActiveStrokeUnits(), _tool == ToolMode.Eraser);
        }
        else if (_tool == ToolMode.Fill)
        {
            _stage.SetFillToolCursor(e.Location, ActiveColor());
            UpdateFillHoverPreview(e.Location);
        }

        if (_gradientHandle != GradientHandleKind.None && e.Button == MouseButtons.Left)
        {
            UpdateGradientHandle(_stage.ScreenToWorld(e.Location));
            UpdateInteractionCursor(e.Location);
            return;
        }

        if (_fillEdgeBezierArmedSession is { } armedFillEdgeSession)
        {
            if (e.Button == MouseButtons.Left)
            {
                UpdateArmedFillEdgeBezierPreview(
                    armedFillEdgeSession,
                    _stage.ScreenToWorld(e.Location),
                    e.Location,
                    PointerMovedFromStart(e.Location));
            }
            _stage.Cursor = Cursors.SizeAll;
            return;
        }

        if (_fillEdgeBezierEditSession is { } fillEdgeSession)
        {
            if (ShouldUpdateFillEdgeBezierPointer(
                    e.Button,
                    fillEdgeSession.IncludesAnchorInsertion,
                    PointerMovedFromStart(e.Location)))
            {
                QueueFillEdgeBezierPointer(_stage.ScreenToWorld(e.Location));
            }
            _stage.Cursor = fillEdgeSession.Handle == EditHandleKind.None
                ? Cursors.Cross
                : Cursors.SizeAll;
            return;
        }

        if (_lineBranchDragSession is not null)
        {
            if (e.Button == MouseButtons.Left) UpdateLineBranchDrag(e.Location);
            UpdateInteractionCursor(e.Location);
            return;
        }

        if (_arcDragSession is not null)
        {
            if (e.Button == MouseButtons.Left) UpdateArcDrag(e.Location);
            UpdateInteractionCursor(e.Location);
            return;
        }

        if (_cornerDragSession is not null)
        {
            if (e.Button == MouseButtons.Left) UpdateCornerDrag(e.Location);
            UpdateInteractionCursor(e.Location);
            return;
        }

        if (IsShotDirectorContext()
            && !_spacePanPointerActive
            && !_viewPanning
            && !_viewZooming
            && !_viewOrbiting
            && !_viewReferencePanning
            && !_viewReferenceZooming)
        {
            UpdateShotFramingGizmoCursor(e.Location);
            return;
        }

        if (IsSceneCompositionContext())
        {
            HandleSceneCompositionPointerMove(e, dx, dy);
            return;
        }

        if (_sceneInstanceMoveActive
            || SelectedSceneInstance() is not null
                && (_activeTransformHandle != TransformHandleKind.None
                    || _tool == ToolMode.Distort && _distortVisualHandleActive))
        {
            HandleSceneCompositionPointerMove(e, dx, dy);
            return;
        }

        if (_tool == ToolMode.Pen && _traditionalPenAnchorEditing && e.Button == MouseButtons.Left)
        {
            if (PointerDragExceeded(e.Location)) QueueMoveSelectedFromPointer(_stage.ScreenToWorld(e.Location));
            return;
        }

        if (_tool == ToolMode.Pen && _traditionalPenPointerDown && e.Button == MouseButtons.Left)
        {
            UpdateTraditionalPenHandles(e.Location);
            return;
        }

        if (_tool == ToolMode.SimplePen && _penSegmentDragging && e.Button == MouseButtons.Left)
        {
            UpdatePenControl(e.Location);
            return;
        }

        if ((_tool is ToolMode.Transform or ToolMode.Distort)
            && (_activeTransformHandle != TransformHandleKind.None
                || _tool == ToolMode.Distort && _distortVisualHandleActive)
            && e.Button == MouseButtons.Left)
        {
            if (!PointerMovedFromStart(e.Location)) return;
            QueueDrawingTransformPointer(_stage.ScreenToWorld(e.Location));
            return;
        }

        if (TryPromote3DViewDragFromMove(e))
        {
            if (_viewReferencePanning)
            {
                _stage.PanReferenceCamera(dx, dy);
            }
            else if (_viewReferenceZooming)
            {
                _stage.DollyReferenceCameraByPixels(dy);
            }
            else if (_viewOrbiting)
            {
                _stage.RotateReferenceCamera(dx, dy);
            }

            return;
        }

        if (_viewReferencePanning)
        {
            _stage.PanReferenceCamera(dx, dy);
            return;
        }

        if (_viewReferenceZooming)
        {
            _stage.DollyReferenceCameraByPixels(dy);
            return;
        }

        if (_viewPanning)
        {
            _stage.Pan(dx, dy);
            return;
        }

        if (_viewZooming)
        {
            var factor = Math.Clamp(Math.Exp(-dy * 0.01), 0.2, 5.0);
            _stage.ZoomAt(e.Location, (float)factor, interactivePreview: true);
            UpdateStatusBar();
            return;
        }

        if (_viewOrbiting)
        {
            _stage.RotateReferenceCamera(dx, dy);
            return;
        }

        if (_tool == ToolMode.Hand)
        {
            _stage.Pan(dx, dy);
        }
        else if (SupportsMarqueeSelection(_tool) && _marqueeSelecting && _marqueeStart is not null && e.Button == MouseButtons.Left)
        {
            _stage.SetMarquee(_marqueeStart.Value, e.Location);
        }
        else if (_tool == ToolMode.Select && _pendingClickSelection.IsValid && e.Button == MouseButtons.Left)
        {
            if (PointerDragExceeded(e.Location))
            {
                var hit = _pendingClickSelection;
                _pendingClickSelection = DrawingElementHit.None;
                SetSelection(hit, deferPresentation: true);
                _pointerHitWasAlreadySelected = true;
                CaptureEditStart(hit.Key.ObjectIndex);
                QueueMoveSelectedFromPointer(_stage.ScreenToWorld(e.Location));
            }
        }
        else if (_tool == ToolMode.Select && _forceMarqueeOnPointerDown && e.Button == MouseButtons.Left)
        {
            if (PointerDragExceeded(e.Location))
            {
                BeginBlankMarqueeSelection(e.Location);
            }
        }
        else if (_tool == ToolMode.Select && _selectedObject >= 0 && _startWorld is not null && _selectedStart is not null && e.Button == MouseButtons.Left)
        {
            if (!ShouldUpdateSelectionPointer(
                    _activeHandle,
                    PointerMovedFromStart(e.Location),
                    PointerDragExceeded(e.Location)))
            {
                return;
            }
            QueueMoveSelectedFromPointer(_stage.ScreenToWorld(e.Location));
        }
        else if (IsFreehandTool(_tool) && _freehandDrawing && e.Button == MouseButtons.Left)
        {
            AppendFreehandSample(e.Location);
        }
        else if (IsDrawingTool(_tool) && _startWorld is not null && e.Button == MouseButtons.Left)
        {
            UpdateDrawingPreview(_startWorld.Value, _stage.ScreenToWorld(e.Location), _tool);
        }
    }

    private void UpdateHoveredLineControls(Point screen)
    {
        if (_tool != ToolMode.Select || IsSceneCompositionContext() || IsScene3DView())
        {
            _stage.ClearHoveredLineElement();
            return;
        }

        var overlayHit = _stage.HitTestFillEdgeBezierOverlay(screen);
        var hit = _scene.HitTestElement(_stage.ScreenToWorld(screen), _frame, SelectionToleranceWorld());
        if (FillEdgeOverlayOwnsPointer(overlayHit, IsSelectableLineHit(hit)))
        {
            _stage.ClearHoveredLineElement();
            return;
        }

        if (TryFindSelectedLineControlHit(screen, out var selectedLine))
        {
            _stage.SetHoveredLineElement(selectedLine);
            return;
        }

        if (_stage.HitTestHoveredLineHandle(screen) != EditHandleKind.None) return;

        if (hit.IsValid
            && hit.Key.Kind is DrawingElementKind.Stroke or DrawingElementKind.BoundaryStroke
            && (uint)hit.Key.ObjectIndex < _scene.ObjectCount
            && _scene.IsObjectSelectable(hit.Key.ObjectIndex, _frame)
            && _stage.TryResolveEditableBezierHit(screen, hit, out var editableHit))
        {
            _stage.SetHoveredLineElement(editableHit);
            return;
        }

        if (ShouldKeepHoveredPencilSegment(
                _scene,
                _selectedObjects,
                _selectedElements,
                _stage.HoveredLineElement))
        {
            return;
        }

        _stage.ClearHoveredLineElement();
    }

    internal static bool ShouldKeepHoveredPencilSegment(
        VectorScene scene,
        IReadOnlyCollection<int> selectedObjects,
        IReadOnlyCollection<DrawingElementHit> selectedElements,
        DrawingElementHit hovered)
    {
        if (!hovered.IsValid
            || (uint)hovered.Key.ObjectIndex >= scene.ObjectCount
            || scene.ShapeKind[hovered.Key.ObjectIndex] != ShapeKind.Freeform
            || !selectedObjects.Contains(hovered.Key.ObjectIndex))
        {
            return false;
        }

        return !selectedElements.Any(hit => hit.Key.ObjectIndex == hovered.Key.ObjectIndex)
            || selectedElements.Any(hit => hit.Key == hovered.Key);
    }

    private bool IsSelectableLineHit(DrawingElementHit hit)
    {
        return hit.IsValid
            && hit.Key.Kind == DrawingElementKind.Stroke
            && (uint)hit.Key.ObjectIndex < _scene.ObjectCount
            && _scene.ShapeKind[hit.Key.ObjectIndex] == ShapeKind.Line
            && _scene.IsObjectSelectable(hit.Key.ObjectIndex, _frame);
    }

    private void BeginSceneCompositionPointer(MouseEventArgs e)
    {
        if (IsShotDirectorContext()
            || e.Button != MouseButtons.Left
            || IsSceneReferenceView() && !IsProjectedScene2DTransformView())
        {
            return;
        }
        if (_tool is not (ToolMode.Select or ToolMode.Transform or ToolMode.Distort)) return;

        _stage.Capture = true;
        _lastMouse = e.Location;
        _startScreen = e.Location;
        _startWorld = _stage.TransformOverlayScreenToWorld(e.Location);
        _activeTransformHandle = TransformHandleKind.None;
        _sceneInstanceMoveActive = false;
        ClearInstanceTimelineEditTracking();
        _forceMarqueeOnPointerDown = false;
        _additiveSelection = SupportsMarqueeSelection(_tool) && IsShiftPressed();
        _pendingClickSelection = DrawingElementHit.None;

        if (_tool == ToolMode.Transform)
        {
            BeginSceneInstanceTransformPointer(e);
        }
        else if (_tool == ToolMode.Distort)
        {
            BeginDistortPointer(e);
        }
        else
        {
            BeginSceneInstanceMovePointer(_startWorld.Value);
        }

        UpdateInteractionCursor(e.Location);
    }

    private bool TryBeginNestedDrawingObjectPointer(MouseEventArgs e)
    {
        if (IsSceneBuildingContext()
            || e.Button != MouseButtons.Left
            || _tool is not (ToolMode.Select or ToolMode.Transform or ToolMode.Distort)
            || ActiveDrawingObject()?.Instances.Count is not > 0)
        {
            return false;
        }

        var world = _stage.ScreenToWorld(e.Location);
        if ((_tool is ToolMode.Transform or ToolMode.Distort)
            && SelectedSceneInstance() is not null
            && (_tool == ToolMode.Transform
                ? _stage.HitTestTransformHandle(e.Location) != TransformHandleKind.None
                : _stage.HitTestDistortHandle(e.Location) != TransformHandleKind.None))
        {
            BeginSceneCompositionPointer(e);
            return true;
        }

        var localHit = _scene.HitTestElement(world, _frame, SelectionToleranceWorld());
        if (localHit.IsValid && _scene.IsObjectSelectable(localHit.Key.ObjectIndex, _frame)) return false;
        if (!TryResolveSceneInstanceAt(world, out _) && !IsPointerInsideSelectedSceneInstance(world)) return false;

        BeginSceneCompositionPointer(e);
        return true;
    }

    private void BeginSceneInstanceMovePointer(PointF world)
    {
        if (TryResolveSceneInstanceAt(world, out var instance))
        {
            SetSceneInstanceSelection(instance, additive: IsShiftPressed());
            _sceneInstanceMoveActive = true;
            _transformLastPointer = world;
        }
        else if (IsPointerInsideSelectedSceneInstance(world))
        {
            _sceneInstanceMoveActive = true;
            _transformLastPointer = world;
        }
        else
        {
            ClearSelection();
        }

        if (_sceneInstanceMoveActive)
        {
            _sceneSnapMoveStartPointer = world;
            BeginSceneInstanceTransformPreview();
        }
        UpdateInspector();
    }

    private void BeginSceneInstanceTransformPointer(MouseEventArgs e)
    {
        var handle = _stage.HitTestTransformHandle(e.Location);
        if (handle == TransformHandleKind.None)
        {
            var world = _stage.TransformOverlayScreenToWorld(e.Location);
            var hasSelectableTarget = TryResolveSceneTransformInstance(
                e.Location,
                world,
                out var instance);
            if (IsProjectedScene2DTransformView())
            {
                if (hasSelectableTarget) SetSceneInstanceSelection(instance);
                else ClearSelection();
            }
            else if (ShouldBeginTransformMarquee(handle, hasSelectableTarget))
            {
                BeginBlankMarqueeSelection(e.Location);
            }
            else
            {
                SetSceneInstanceSelection(instance);
            }

            UpdateInspector();
            return;
        }

        if (SelectedSceneInstance() is null) return;
        _activeTransformHandle = handle;
        _transformCurrentBounds = _stage.TransformBounds;
        _transformLastPointer = _stage.TransformOverlayScreenToWorld(e.Location);
        if (handle == TransformHandleKind.Focus) return;
        if (handle == TransformHandleKind.Move) _sceneSnapMoveStartPointer = _transformLastPointer;
        BeginSceneInstanceTransformPreview();
        _transformPivot = TransformPivotFor(handle, _stage.TransformFrame);
        _transformLastAngle = TransformPointerAngle(_transformLastPointer, _transformPivot);
        if (IsRotationHandle(handle)) TryBeginRotationTransformFrame();
    }

    private void HandleSceneCompositionPointerMove(MouseEventArgs e, int dx, int dy)
    {
        if (TryPromote3DViewDragFromMove(e))
        {
            if (_viewReferencePanning) _stage.PanReferenceCamera(dx, dy);
            else if (_viewReferenceZooming) _stage.DollyReferenceCameraByPixels(dy);
            else if (_viewOrbiting) _stage.RotateReferenceCamera(dx, dy);
            return;
        }

        if (_viewReferencePanning)
        {
            _stage.PanReferenceCamera(dx, dy);
            return;
        }

        if (_viewReferenceZooming)
        {
            _stage.DollyReferenceCameraByPixels(dy);
            return;
        }

        if (_viewPanning)
        {
            _stage.Pan(dx, dy);
            return;
        }

        if (_viewZooming)
        {
            _stage.ZoomAt(
                e.Location,
                (float)Math.Clamp(Math.Exp(-dy * 0.01), 0.2, 5.0),
                interactivePreview: true);
            UpdateStatusBar();
            return;
        }

        if (_viewOrbiting)
        {
            _stage.RotateReferenceCamera(dx, dy);
            return;
        }

        if (UpdateProjectedSceneMovePointer(e)) return;
        if (IsSceneReferenceView() && !IsProjectedScene2DTransformView()) return;
        if (SupportsMarqueeSelection(_tool)
            && _marqueeSelecting
            && _marqueeStart is not null
            && e.Button == MouseButtons.Left)
        {
            _stage.SetMarquee(_marqueeStart.Value, e.Location);
            return;
        }

        var world = _stage.TransformOverlayScreenToWorld(e.Location);
        if (_tool == ToolMode.Transform
            && _activeTransformHandle != TransformHandleKind.None
            && e.Button == MouseButtons.Left)
        {
            // With a motion track on Stage the same Free Transform gesture edits the selected
            // frames' anchors rather than the instance's current frame.
            if (PointerDragExceeded(e.Location))
            {
                if (ApplyMotionTrackTransform(world)) return;
                ApplySceneInstanceTransform(world);
            }

            return;
        }

        if (_tool == ToolMode.Distort
            && (_activeTransformHandle != TransformHandleKind.None || _distortVisualHandleActive)
            && e.Button == MouseButtons.Left)
        {
            if (PointerDragExceeded(e.Location)) QueueDrawingTransformPointer(world);
            return;
        }

        if (_tool == ToolMode.Select && _sceneInstanceMoveActive && e.Button == MouseButtons.Left)
        {
            if (PointerDragExceeded(e.Location)) MoveSelectedSceneInstance(world);
            return;
        }

        UpdateInteractionCursor(e.Location);
    }

    private void MoveSelectedSceneInstance(PointF world)
    {
        var instances = SelectedSceneInstances();
        if (instances.Count == 0) return;
        var spatialSession = _spatialTransformEditSession;
        if (spatialSession is not null && _sceneSnapMoveStartPointer is { } snapStart)
        {
            var rawDelta = new Vector3(world.X - snapStart.X, world.Y - snapStart.Y, 0);
            var delta = ResolveSceneSnapDelta(rawDelta, correction => new Vector3(correction.X, correction.Y, 0));
            var changed = false;
            foreach (var instance in instances)
            {
                if (!spatialSession.StartStates.TryGetValue(instance.Id, out var start)) continue;
                var next = start with
                {
                    X = VectorUnits.Quantize(start.X + delta.X),
                    Y = VectorUnits.Quantize(start.Y + delta.Y)
                };
                var editFrame = PrepareInstanceStateTimelineEdit(instance);
                changed |= instance.SetStateAtFrame(editFrame, next);
            }
            if (!changed) return;
            spatialSession.Changed = true;
            _sceneInstanceTimelineDirty = true;
            _transformLastPointer = world;
            if (!PreviewSelectedSceneInstanceStates())
            {
                RebuildEditableInstanceComposition();
                UpdateInspector();
            }
            return;
        }

        _stage.ClearSceneSnapIndicator();
        var dx = world.X - _transformLastPointer.X;
        var dy = world.Y - _transformLastPointer.Y;
        if (Math.Abs(dx) <= 0.0001f && Math.Abs(dy) <= 0.0001f) return;

        foreach (var instance in instances)
        {
            var editFrame = PrepareInstanceStateTimelineEdit(instance);
            var position = instance.EvaluatePosition(editFrame);
            instance.SetPositionAtFrame(editFrame, new PointF(
                VectorUnits.Quantize(position.X + dx),
                VectorUnits.Quantize(position.Y + dy)));
        }
        _sceneInstanceTimelineDirty = true;
        TranslateCustomTransformFocus(dx, dy);
        _transformLastPointer = world;
        if (!PreviewSelectedSceneInstanceStates()) PreviewTranslateSelectedSceneInstance(dx, dy);
    }

    private void ApplySceneInstanceTransform(PointF world)
    {
        var instance = SelectedSceneInstance();
        var instances = SelectedSceneInstances();
        if (instance is null || instances.Count == 0 || _activeTransformHandle == TransformHandleKind.None) return;

        if (_activeTransformHandle == TransformHandleKind.Move)
        {
            MoveSelectedSceneInstance(world);
            return;
        }

        if (_activeTransformHandle == TransformHandleKind.Focus)
        {
            _transformFocus = VectorUnits.Quantize(world);
            UpdateTransformOverlay();
            // The motion track traces this anchor, so resample it live while the operator drags it.
            RebuildMotionTrackPreview();
            _stage.Invalidate();
            return;
        }

        var changed = false;
        Func<PointF, PointF>? previewTransform = null;
        if (IsRotationHandle(_activeTransformHandle))
        {
            // A frozen rotation turns around the pivot captured at pointer-down. The live pivot is
            // derived from the box, so using it here would let the pivot chase its own rotation.
            var pivot = _transformFrameFrozenForRotation ? _transformFrozenPivot : _transformPivot;
            var angle = TransformPointerAngle(world, pivot);
            var delta = NormalizeAngle(angle - _transformLastAngle);
            if (Math.Abs(delta) <= 0.0001f) return;
            var cos = MathF.Cos(delta);
            var sin = MathF.Sin(delta);
            foreach (var selectedInstance in instances)
            {
                var editFrame = PrepareInstanceStateTimelineEdit(selectedInstance);
                var state = selectedInstance.EvaluateState(editFrame);
                var operationPivot = DrawingObjectInstanceDefinition.RotationPivotScenePosition(state);
                var rotatedPivot = RotatePointAround(
                    new PointF(operationPivot.X, operationPivot.Y),
                    pivot,
                    delta);
                selectedInstance.SetStateAtFrame(editFrame, state with
                {
                    X = VectorUnits.Quantize(state.X + rotatedPivot.X - operationPivot.X),
                    Y = VectorUnits.Quantize(state.Y + rotatedPivot.Y - operationPivot.Y),
                    RotationZ = NormalizeDegrees(state.RotationZ + delta * 57.29578f)
                });
            }
            _transformLastAngle = angle;
            previewTransform = point => new PointF(
                pivot.X + (point.X - pivot.X) * cos - (point.Y - pivot.Y) * sin,
                pivot.Y + (point.X - pivot.X) * sin + (point.Y - pivot.Y) * cos);
            changed = true;
        }
        else if (IsSkewHandle(_activeTransformHandle))
        {
            var oriented = instances.Count == 1 && _stage.TransformFrame.IsValid;
            var factor = oriented
                ? SkewFactor(_activeTransformHandle, _stage.TransformFrame, _transformLastPointer, world)
                : SkewFactor(_activeTransformHandle, _transformCurrentBounds, _transformLastPointer, world);
            if (Math.Abs(factor) <= 0.0001f) return;
            var degrees = MathF.Atan(factor) * 57.29578f;
            foreach (var selectedInstance in instances)
            {
                var editFrame = PrepareInstanceStateTimelineEdit(selectedInstance);
                var state = selectedInstance.EvaluateState(editFrame);
                InstanceFrameState next;
                if (IsHorizontalSkewHandle(_activeTransformHandle))
                {
                    next = state with { SkewX = Math.Clamp(state.SkewX + degrees, -80, 80) };
                }
                else
                {
                    next = state with { SkewY = Math.Clamp(state.SkewY + degrees, -80, 80) };
                }
                if (oriented) next = KeepWorldPointFixed(state, next, _transformPivot);
                selectedInstance.SetStateAtFrame(editFrame, next);
            }

            changed = true;
        }
        else
        {
            var oriented = instances.Count == 1 && _stage.TransformFrame.IsValid;
            float scaleX;
            float scaleY;
            if (oriented)
            {
                if (!TryGetOrientedScaleFactors(
                        _stage.TransformFrame,
                        _activeTransformHandle,
                        world,
                        out scaleX,
                        out scaleY))
                {
                    return;
                }
            }
            else
            {
                if (!TryGetResizedTransformScaleFactors(
                        _transformCurrentBounds,
                        _transformPivot,
                        _activeTransformHandle,
                        world,
                        out scaleX,
                        out scaleY))
                {
                    return;
                }
            }
            (scaleX, scaleY) = ConstrainTransformScaleFactors(
                _activeTransformHandle,
                scaleX,
                scaleY,
                IsShiftPressed());
            if (Math.Abs(scaleX - 1) <= 0.0001f && Math.Abs(scaleY - 1) <= 0.0001f) return;
            var pivot = _transformPivot;
            foreach (var selectedInstance in instances)
            {
                var editFrame = PrepareInstanceStateTimelineEdit(selectedInstance);
                var state = selectedInstance.EvaluateState(editFrame);
                var next = state with
                {
                    // Preserve the sign so crossing the anchor flips the instance instead of
                    // being clamped back to a positive scale; only the magnitude is bounded.
                    ScaleX = ClampScaleMagnitude(state.ScaleX * scaleX, 0.01f, 1000f),
                    ScaleY = ClampScaleMagnitude(state.ScaleY * scaleY, 0.01f, 1000f)
                };
                next = oriented
                    ? KeepWorldPointFixed(state, next, pivot)
                    : MoveScalePivotWithGroup(state, next, pivot, scaleX, scaleY);
                selectedInstance.SetStateAtFrame(editFrame, next);
            }
            if (!oriented)
            {
                previewTransform = point => new PointF(
                    pivot.X + (point.X - pivot.X) * scaleX,
                    pivot.Y + (point.Y - pivot.Y) * scaleY);
            }
            changed = true;
        }

        if (!changed) return;
        _sceneInstanceTimelineDirty = true;
        _transformLastPointer = world;
        if (PreviewSelectedSceneInstanceStates()
            || previewTransform is not null && PreviewTransformSelectedSceneInstance(previewTransform))
        {
            _transformCurrentBounds = _stage.TransformBounds;
            return;
        }

        RebuildEditableInstanceComposition();
        _transformCurrentBounds = _stage.TransformBounds;
        UpdateInspector();
    }

    private void PreviewTranslateSelectedSceneInstance(float dx, float dy)
    {
        if (_selectedSceneInstanceObjectIndices.Length == 0) return;
        ActiveInstanceCompositionScene().TranslateObjectsForPreview(_selectedSceneInstanceObjectIndices, dx, dy);
        _sceneInstancePreviewDirty = true;
        UpdateSceneInstanceSelectionOverlay();
        UpdateTransformOverlay();
        _stage.Invalidate();
    }

    private int PrepareInstanceStateTimelineEdit(DrawingObjectInstanceDefinition instance)
    {
        var editFrame = ResolveInstanceStateEditFrameForCurrentContext(instance);
        _sceneInstanceTimelineEditedFrames.Add(editFrame);
        if (!_timeline.AutoKeyframeEnabled
            || !_sceneInstanceTimelineKeyframeEnsuredIds.Add(instance.Id))
        {
            return editFrame;
        }

        if (IsSceneCompositionContext())
        {
            var scene = ActiveScene();
            var track = scene?.Timeline.FindTrackByTargetId(instance.SceneLayerId);
            if (scene is null || track is null) return editFrame;
            if (editFrame >= track.Duration) scene.Timeline.SetTrackDuration(track.Id, editFrame + 1);
            var exposure = track.EvaluateExposure(editFrame);
            if (!exposure.IsKeyframe || !exposure.HasContent)
            {
                _sceneInstanceTimelineDirty |= scene.Timeline.InsertKeyframe(track.Id, editFrame);
            }
            _sceneInstanceTimelineDirty |= CaptureInstanceStateKeyframes(scene, instance.SceneLayerId, editFrame);
            return editFrame;
        }

        var drawingObject = ActiveDrawingObject();
        if (drawingObject is null) return editFrame;
        var layer = Array.IndexOf(drawingObject.Scene.LayerIds, instance.SceneLayerId);
        if (layer >= 0)
        {
            _sceneInstanceTimelineDirty |= drawingObject.Scene.InsertTimelineKeyframe(layer, editFrame);
            _sceneInstanceTimelineDirty |= CaptureInstanceStateKeyframes(drawingObject, instance.SceneLayerId, editFrame);
        }

        return editFrame;
    }

    private int ResolveInstanceStateEditFrameForCurrentContext(DrawingObjectInstanceDefinition instance)
    {
        var timeline = IsSceneCompositionContext()
            ? ActiveScene()?.Timeline
            : ActiveDrawingObject()?.Timeline;
        return timeline is null
            ? Math.Max(0, _frame)
            : ResolveInstanceStateEditFrame(
                timeline,
                instance.SceneLayerId,
                _frame,
                _timeline.AutoKeyframeEnabled);
    }

    internal static int ResolveInstanceStateEditFrame(
        AnimationTimeline timeline,
        string targetId,
        int frame,
        bool autoKeyframeEnabled)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        frame = Math.Max(0, frame);
        if (autoKeyframeEnabled) return frame;

        var exposure = timeline.EvaluateTargetExposure(targetId, frame);
        return exposure.SourceKeyframeFrame >= 0 ? exposure.SourceKeyframeFrame : 0;
    }

    private bool RefreshEditedInstanceTimelineTweens()
    {
        var sceneDefinition = IsSceneCompositionContext() ? ActiveScene() : null;
        var drawingObject = sceneDefinition is null ? ActiveDrawingObject() : null;
        if (sceneDefinition is null && drawingObject is null) return false;

        var changed = false;
        foreach (var editFrame in _sceneInstanceTimelineEditedFrames.Order())
        {
            changed |= sceneDefinition is not null
                ? sceneDefinition.RefreshTimelineTweenMaterializationsAtEndpointFrame(editFrame)
                : drawingObject!.RefreshTimelineTweenMaterializationsAtEndpointFrame(editFrame);
        }

        return changed;
    }

    private void ClearInstanceTimelineEditTracking()
    {
        _sceneInstanceTimelineKeyframeEnsuredIds.Clear();
        _sceneInstanceTimelineEditedFrames.Clear();
    }

    private bool PreviewTransformSelectedSceneInstance(Func<PointF, PointF> transform)
    {
        if (_selectedSceneInstanceObjectIndices.Length == 0) return false;
        ActiveInstanceCompositionScene().TransformObjects(
            _selectedSceneInstanceObjectIndices,
            transform,
            rebuildGeometryIndex: false);
        _sceneInstancePreviewDirty = true;
        UpdateSceneInstanceSelectionOverlay();
        UpdateTransformOverlay();
        _stage.Invalidate();
        return true;
    }

    private void BeginSceneInstanceTransformPreview()
    {
        if (IsSceneCompositionContext()) BeginSpatialTransformEdit(allowScene2D: true);
        _sceneInstanceTransformPreviews.Clear();
        var compositionScene = ActiveInstanceCompositionScene();
        var compositionResult = ActiveInstanceCompositionResult();
        foreach (var instance in SelectedSceneInstances())
        {
            var indices = Enumerable.Range(0, compositionScene.ObjectCount)
                .Where(index =>
                {
                    if (!compositionResult.TryGetOwner(index, out var owner)) return false;
                    var rootInstanceId = string.IsNullOrWhiteSpace(owner.RootInstanceId)
                        ? owner.InstanceId
                        : owner.RootInstanceId;
                    return string.Equals(rootInstanceId, instance.Id, StringComparison.Ordinal);
                })
                .ToArray();
            if (indices.Length == 0) continue;
            _sceneInstanceTransformPreviews[instance.Id] = new SceneInstanceTransformPreview(
                instance.EvaluateState(_frame),
                compositionScene.BeginTransformSession(indices));
        }
    }

    private bool PreviewSelectedSceneInstanceStates()
    {
        if (_sceneInstanceTransformPreviews.Count == 0) return false;
        if (IsProjectedScene2DTransformView())
        {
            RebuildSceneComposition(refreshScenePanels: false);
            return true;
        }
        var compositionScene = ActiveInstanceCompositionScene();
        var instancesById = ActiveEditableInstances().ToDictionary(instance => instance.Id, StringComparer.Ordinal);
        var changed = false;
        foreach (var (instanceId, preview) in _sceneInstanceTransformPreviews)
        {
            if (!instancesById.TryGetValue(instanceId, out var instance)) continue;
            var currentState = instance.EvaluateState(_frame);
            if (!TryCreateInstancePreviewTransform(preview.StartState, currentState, out var transform)) continue;
            // Primitive position/size/rotation cannot represent shear, and cannot represent a
            // mirror either because Width/Height/Angle are always positively oriented. Convert
            // only the composed preview; the transform session retains the source geometry.
            var skewChanged = currentState.SkewX != preview.StartState.SkewX
                || currentState.SkewY != preview.StartState.SkewY;
            var flipped = currentState.ScaleX * preview.StartState.ScaleX < 0f
                || currentState.ScaleY * preview.StartState.ScaleY < 0f;
            compositionScene.ApplyTransformSession(
                preview.Session,
                transform,
                convertPrimitivesToPaths: skewChanged || flipped,
                rebuildGeometryIndex: false);
            changed = true;
        }

        if (!changed) return false;
        _sceneInstancePreviewDirty = true;
        UpdateSceneInstanceSelectionOverlay();
        UpdateTransformOverlay();
        _stage.Invalidate();
        return true;
    }

    private static bool TryCreateInstancePreviewTransform(
        InstanceFrameState start,
        InstanceFrameState current,
        out Func<PointF, PointF> transform)
    {
        var startTransform = DrawingObjectInstanceDefinition.CreatePlanarTransform(start);
        if (!Matrix3x2.Invert(startTransform, out var inverseStart))
        {
            transform = static point => point;
            return false;
        }

        var currentTransform = DrawingObjectInstanceDefinition.CreatePlanarTransform(current);
        transform = point =>
        {
            var local = Vector2.Transform(new Vector2(point.X, point.Y), inverseStart);
            var world = Vector2.Transform(local, currentTransform);
            return new PointF(world.X, world.Y);
        };
        return true;
    }

    private void BeginTransformPointer(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        var handle = _stage.HitTestTransformHandle(e.Location);
        if (handle == TransformHandleKind.None)
        {
            var hit = _scene.HitTestElement(_stage.ScreenToWorld(e.Location), _frame, SelectionToleranceWorld());
            var hasSelectableTarget = hit.IsValid && _scene.IsObjectSelectable(hit.Key.ObjectIndex, _frame);
            if (ShouldBeginTransformMarquee(handle, hasSelectableTarget)) BeginBlankMarqueeSelection(e.Location);
            else if (hit.IsValid
                && _scene.TryGetMixingBrushLocalRegion(hit.Key.ObjectIndex, out _))
            {
                SetSelection(hit, deferPresentation: true);
            }
            else SetSelection(hit.Key.ObjectIndex, deferPresentation: true);
            return;
        }

        _activeTransformHandle = handle;
        _transformCurrentBounds = _stage.TransformBounds;
        _transformLastPointer = _stage.ScreenToWorld(e.Location);
        _drawingTransformSession = null;
        _drawingTransformStartBounds = _transformCurrentBounds;
        _drawingTransformStartPointer = _transformLastPointer;
        _drawingTransformStartFocus = _transformFocus;
        _pointerTopologyQueriesInvalidated = false;
        if (handle == TransformHandleKind.Focus) return;
        _transformPivot = TransformPivotFor(handle, _transformCurrentBounds);
        _transformLastAngle = TransformPointerAngle(_transformLastPointer, _transformPivot);
        _drawingTransformAccumulatedAngle = 0;
        EndRotationTransformFrame();
        if (IsRotationHandle(handle)) TryBeginRotationTransformFrame();
    }

    private void BeginDistortPointer(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        var world = _stage.TransformOverlayScreenToWorld(e.Location);

        if (!TryResolveDistortWarp(out var activeWarp))
        {
            var overlayEnvelope = _stage.DistortFrame;
            var bounds = _stage.DistortFrame.Bounds;
            if (bounds.IsEmpty) bounds = _stage.TransformBounds;
            if (bounds.IsEmpty && _selectedObjects.Count > 0) bounds = _scene.GetObjectWorldBounds(_selectedObjects[0]);
            if (bounds.IsEmpty)
            {
                var hit = _scene.HitTestElement(world, _frame, SelectionToleranceWorld());
                var hasSelectableTarget = hit.IsValid
                    && _scene.IsObjectSelectable(hit.Key.ObjectIndex, _frame);
                if (!IsSceneCompositionContext() && hasSelectableTarget)
                {
                    if (_scene.TryGetMixingBrushLocalRegion(hit.Key.ObjectIndex, out _)) SetSelection(hit, deferPresentation: true);
                    else SetSelection(hit.Key.ObjectIndex, deferPresentation: true);
                }
                else if (TryResolveSceneTransformInstance(e.Location, world, out var instance))
                {
                    SetSceneInstanceSelection(instance, additive: IsShiftPressed());
                }
                else if (!IsProjectedScene2DTransformView())
                {
                    BeginBlankMarqueeSelection(e.Location);
                }
                else ClearSelection();

                UpdateInspector();
                UpdateTransformOverlay();
                return;
            }
            activeWarp = overlayEnvelope.IsValid
                ? new DistortWarp(TransformOverlayFrame.FromBounds(bounds), overlayEnvelope)
                : DistortWarp.FromBounds(bounds);
        }

        _distortSourceFrame = activeWarp.Source;
        _distortStartEnvelope = activeWarp.Envelope;
        _distortCurrentEnvelope = activeWarp.Envelope;
        _distortVisualHandleActive = false;
        _activeDistortVisualHandle = default;
        _distortReplaceExistingWarp = activeWarp.IsValid;

        var hasVisualHandle = _stage.TryHitTestDistortVisualHandle(e.Location, out var visualHandle);
        if (IsControlPressed()
            && !hasVisualHandle
            && _stage.TryHitTestDistortBoundary(e.Location, out var boundaryHit)
            && _distortCurrentEnvelope.TryInsertAnchor(
                boundaryHit.Side,
                boundaryHit.SourceT,
                out var insertedEnvelope,
                out var insertedHandle))
        {
            _activeTransformHandle = TransformHandleKind.None;
            _distortVisualHandleActive = true;
            _activeDistortVisualHandle = insertedHandle;
            _drawingTransformStartPointer = world;
            _drawingTransformSession = null;
            _geometryDirty = false;
            _distortStartEnvelope = insertedEnvelope;
            _distortCurrentEnvelope = insertedEnvelope;
            if (PresentDistortPointerPreview(new DistortWarp(_distortSourceFrame, insertedEnvelope)))
            {
                _stage.SetDistortOverlay(
                    true,
                    insertedEnvelope,
                    ProjectedSceneTransformOverlay());
                _stage.Invalidate();
            }
            return;
        }

        if (hasVisualHandle)
        {
            _activeTransformHandle = TransformHandleKind.None;
            _distortVisualHandleActive = true;
            _activeDistortVisualHandle = visualHandle;
        }
        else
        {
            _activeTransformHandle = _stage.HitTestDistortHandle(e.Location);
            _distortVisualHandleActive = false;
        }

        var handle = _activeTransformHandle;
        if (!HasActiveDistortHandle(handle, _distortVisualHandleActive))
        {
            if (SelectedSceneInstance() is not null
                && IsPointerInsideSelectedSceneTransformInstance(e.Location, world))
            {
                UpdateInspector();
                return;
            }

            var hit = _scene.HitTestElement(world, _frame, SelectionToleranceWorld());
            var hasSelectableTarget = hit.IsValid && _scene.IsObjectSelectable(hit.Key.ObjectIndex, _frame);
            if (ShouldBeginTransformMarquee(handle, hasSelectableTarget)
                && SelectedSceneInstance() is null)
            {
                BeginBlankMarqueeSelection(e.Location);
            }
            else if (hit.IsValid && hasSelectableTarget)
            {
                if (_scene.TryGetMixingBrushLocalRegion(hit.Key.ObjectIndex, out _)) SetSelection(hit, deferPresentation: true);
                else SetSelection(hit.Key.ObjectIndex, deferPresentation: true);
            }
            else if (TryResolveSceneTransformInstance(e.Location, world, out var instance))
            {
                SetSceneInstanceSelection(instance);
            }

            UpdateInspector();
            UpdateTransformOverlay();
            return;
        }

        _drawingTransformStartPointer = _stage.TransformOverlayScreenToWorld(e.Location);
        _drawingTransformSession = null;
        _geometryDirty = false;
        _pointerTopologyQueriesInvalidated = false;
        _stage.SetDistortOverlay(
            true,
            _distortCurrentEnvelope,
            ProjectedSceneTransformOverlay());
    }

    private bool TryResolveSceneTransformInstance(
        Point screen,
        PointF world,
        out DrawingObjectInstanceDefinition instance)
    {
        return IsProjectedScene2DTransformView()
            ? TryResolveProjectedSceneInstance(screen, out instance)
            : TryResolveSceneInstanceAt(world, out instance);
    }

    private bool IsPointerInsideSelectedSceneTransformInstance(Point screen, PointF world)
    {
        if (!IsProjectedScene2DTransformView()) return IsPointerInsideSelectedSceneInstance(world);
        return TryResolveProjectedSceneInstance(screen, out var instance)
            && _selectedSceneInstanceIds.Contains(instance.Id);
    }

    private void ApplyDistortFromPointer(PointF world)
    {
        if (!_distortVisualHandleActive && _activeTransformHandle == TransformHandleKind.None
            || !_distortSourceFrame.IsValid
            || !_distortStartEnvelope.IsValid)
        {
            return;
        }

        var candidate = _distortVisualHandleActive
            ? _distortStartEnvelope.WithHandle(
                _activeDistortVisualHandle,
                _drawingTransformStartPointer,
                world,
                preserveSmoothTangent: IsShiftPressed())
            : _activeTransformHandle == TransformHandleKind.Move
            ? _distortStartEnvelope.WithHandle(
                TransformHandleKind.Move,
                _drawingTransformStartPointer,
                world)
            : _distortStartEnvelope.WithHandle(
                _activeTransformHandle,
                _drawingTransformStartPointer,
                world);
        if (candidate == _distortCurrentEnvelope || !candidate.IsValid) return;
        var warp = new DistortWarp(_distortSourceFrame, candidate);
        if (!PresentDistortPointerPreview(warp)) return;
        _distortCurrentEnvelope = candidate;
        _stage.SetDistortOverlay(
            true,
            candidate,
            ProjectedSceneTransformOverlay());
    }

    private bool PresentDistortPointerPreview(DistortWarp warp)
    {
        if (!warp.IsValid || !EnsureDistortPointerPreviewTargets()) return false;
        var overrides = new Dictionary<int, DistortWarp[]>(_distortPreviewBaseStacks.Count);
        foreach (var (objectIndex, baseline) in _distortPreviewBaseStacks)
        {
            DistortWarp[] next;
            if (_distortPreviewReplaceLast.Contains(objectIndex) && baseline.Length > 0)
            {
                next = baseline.ToArray();
                next[^1] = warp;
            }
            else
            {
                next = new DistortWarp[baseline.Length + 1];
                baseline.CopyTo(next, 0);
                next[^1] = warp;
            }
            overrides[objectIndex] = next;
        }

        if (_distortPreviewScene is null || overrides.Count == 0) return false;
        _stage.SetDistortPreview(_distortPreviewScene, overrides);
        _distortPreviewChanged = true;
        return true;
    }

    private bool EnsureDistortPointerPreviewTargets()
    {
        if (_distortPreviewScene is not null && _distortPreviewBaseStacks.Count > 0) return true;
        _distortPreviewBaseStacks.Clear();
        _distortPreviewReplaceLast.Clear();

        var selectedInstances = SelectedSceneInstances();
        if (selectedInstances.Count > 0)
        {
            if (_selectedSceneInstanceObjectIndices.Length == 0) RefreshSelectedSceneInstanceObjectIndices();
            var instancesById = selectedInstances.ToDictionary(instance => instance.Id, StringComparer.Ordinal);
            var compositionScene = ActiveInstanceCompositionScene();
            var compositionResult = ActiveInstanceCompositionResult();
            foreach (var objectIndex in _selectedSceneInstanceObjectIndices.Distinct())
            {
                if ((uint)objectIndex >= compositionScene.ObjectCount
                    || !compositionResult.TryGetOwner(objectIndex, out var owner)) continue;
                var rootInstanceId = string.IsNullOrWhiteSpace(owner.RootInstanceId)
                    ? owner.InstanceId
                    : owner.RootInstanceId;
                if (!instancesById.TryGetValue(rootInstanceId, out var instance)) continue;
                _distortPreviewBaseStacks[objectIndex] = compositionScene.TryGetObjectDistortionsView(
                    objectIndex,
                    out var existing)
                    ? existing.ToArray()
                    : [];
                if (instance.EvaluateState(_frame).Distortion is { IsValid: true })
                {
                    _distortPreviewReplaceLast.Add(objectIndex);
                }
            }
            _distortPreviewScene = compositionScene;
            return _distortPreviewBaseStacks.Count > 0;
        }

        foreach (var objectIndex in _selectedObjects.Distinct())
        {
            if ((uint)objectIndex >= _scene.ObjectCount) continue;
            var existing = _scene.TryGetObjectDistortionsView(objectIndex, out var stored)
                ? stored.ToArray()
                : [];
            _distortPreviewBaseStacks[objectIndex] = existing;
            if (_distortReplaceExistingWarp && existing.Length > 0)
            {
                _distortPreviewReplaceLast.Add(objectIndex);
            }
        }
        _distortPreviewScene = _scene;
        return _distortPreviewBaseStacks.Count > 0;
    }

    private void CompleteDistortPointerPreview()
    {
        if (!_distortPreviewChanged)
        {
            ClearDistortPointerPreviewState();
            return;
        }

        var warp = new DistortWarp(_distortSourceFrame, _distortCurrentEnvelope);
        _distortPreviewChanged = false;
        try
        {
            if (warp.IsValid) ApplyPersistentDistortWarp(warp);
        }
        finally
        {
            ClearDistortPointerPreviewState();
        }
    }

    private void CancelDistortPointerPreview()
    {
        _distortPreviewChanged = false;
        ClearDistortPointerPreviewState();
    }

    private void ClearDistortPointerPreviewState()
    {
        _stage.ClearDistortPreview();
        _distortPreviewScene = null;
        _distortPreviewBaseStacks.Clear();
        _distortPreviewReplaceLast.Clear();
    }

    private bool TryResolveDistortWarp(out DistortWarp warp)
    {
        warp = default;
        var instance = SelectedSceneInstance();
        if (instance is not null)
        {
            var state = instance.EvaluateState(_frame);
            if (state.Distortion is { IsValid: true } distortion
                && TryGetInstanceWorldTransform(instance, state, out var matrix))
            {
                warp = distortion.AffineTransform(matrix);
                return warp.IsValid;
            }
        }

        if (_selectedObjects.Count == 1
            && _scene.TryGetObjectDistortions(_selectedObjects[0], out var distortions)
            && distortions.Length > 0)
        {
            warp = distortions[^1];
            return warp.IsValid;
        }

        return false;
    }

    private bool TryGetInstanceWorldTransform(
        DrawingObjectInstanceDefinition instance,
        InstanceFrameState state,
        out Matrix3x2 transform)
    {
        var drawingObject = _drawingObjects.FirstOrDefault(item =>
            string.Equals(item.Id, instance.DrawingObjectId, StringComparison.Ordinal));
        if (drawingObject is null)
        {
            transform = Matrix3x2.Identity;
            return false;
        }

        transform = Matrix3x2.CreateTranslation(-drawingObject.Anchor.X, -drawingObject.Anchor.Y)
            * DrawingObjectInstanceDefinition.CreatePlanarTransform(state);
        return Matrix3x2.Invert(transform, out _);
    }

    private static PointF TransformPoint(PointF point, Matrix3x2 transform)
    {
        var mapped = Vector2.Transform(new Vector2(point.X, point.Y), transform);
        return new PointF(mapped.X, mapped.Y);
    }

    private bool ApplyPersistentDistortWarp(DistortWarp warp)
    {
        if (!warp.IsValid) return false;
        if (SelectedSceneInstances() is { Count: > 0 } selectedInstances)
        {
            if (!_undoCapturedForPointerEdit)
            {
                if (_timeline.Context is SceneDefinition activeScene)
                {
                    var timelineSnapshot = activeScene.Timeline.CreateSnapshot();
                    var layerSnapshot = activeScene.CreateLayerSnapshot();
                    var instanceSnapshot = activeScene.CreateInstanceSnapshot();
                    PushSceneTimelineUndo(activeScene, timelineSnapshot, layerSnapshot, instanceSnapshot);
                }
                else if (ActiveDrawingObject() is { } container)
                {
                    var snapshot = _scene.CreateSnapshot();
                    PushUndoSnapshot(snapshot, container, container.CreateInstanceSnapshot());
                    _pointerUndoSnapshot = snapshot;
                }
                else
                {
                    return false;
                }
                _undoCapturedForPointerEdit = true;
            }
            var changed = false;
            foreach (var instance in selectedInstances)
            {
                var editFrame = PrepareInstanceStateTimelineEdit(instance);
                var state = instance.EvaluateState(editFrame);
                if (!TryGetInstanceWorldTransform(instance, state, out var matrix)
                    || !Matrix3x2.Invert(matrix, out var inverse)) continue;
                var localWarp = warp.Transform(point => TransformPoint(point, inverse));
                if (!localWarp.IsValid) continue;
                changed |= instance.SetStateAtFrame(editFrame, state with { Distortion = localWarp });
            }
            if (changed)
            {
                _sceneInstanceTimelineDirty = true;
                _sceneInstancePreviewDirty = true;
            }
            return changed;
        }

        if (_selectedObjects.Count == 0) return false;
        CapturePointerUndoSnapshot();
        var updates = new List<(int ObjectIndex, IReadOnlyList<DistortWarp> Distortions)>();
        foreach (var objectIndex in _selectedObjects.Distinct())
        {
            if ((uint)objectIndex >= _scene.ObjectCount) continue;
            var next = _scene.TryGetObjectDistortionsView(objectIndex, out var existing)
                ? existing.ToList()
                : new List<DistortWarp>();
            if (_distortReplaceExistingWarp && next.Count > 0) next[^1] = warp;
            else if (next.Count == 0 || next[^1] != warp) next.Add(warp);
            updates.Add((objectIndex, next));
        }
        var changedObjects = _scene.SetObjectDistortionsBatch(updates);
        if (changedObjects) MarkProjectDirty();
        return changedObjects;
    }

    private void ApplyTransformFromPointer(PointF world)
    {
        // Basic Drawing routes Free Transform here; when the motion track owns the box the gesture
        // edits the selected frames instead of the selected drawing objects.
        if (ApplyMotionTrackTransform(world)) return;
        if (_selectedObjects.Count == 0 || _activeTransformHandle == TransformHandleKind.None) return;

        if (_activeTransformHandle == TransformHandleKind.Focus)
        {
            _transformFocus = VectorUnits.Quantize(world);
            UpdateTransformOverlay();
            _stage.Invalidate();
            return;
        }

        if (_activeTransformHandle == TransformHandleKind.Move)
        {
            var dx = world.X - _drawingTransformStartPointer.X;
            var dy = world.Y - _drawingTransformStartPointer.Y;
            if (Math.Abs(dx) <= 0.0001f
                && Math.Abs(dy) <= 0.0001f
                && _drawingTransformSession is null)
            {
                return;
            }

            if (!TryEnsureDrawingTransformSession(out var session)) return;
            if (!_scene.ApplyTranslationSessionForPreview(session, dx, dy)) return;
            InvalidatePointerTopologyQueries();
            _transformFocus = _drawingTransformStartFocus is { } focus
                ? VectorUnits.Quantize(new PointF(focus.X + dx, focus.Y + dy))
                : null;
        }
        else if (IsRotationHandle(_activeTransformHandle))
        {
            var angle = TransformPointerAngle(world, _transformPivot);
            var pointerDelta = NormalizeAngle(angle - _transformLastAngle);
            if (Math.Abs(pointerDelta) <= 0.0001f) return;
            _drawingTransformAccumulatedAngle += pointerDelta;
            if (!TryEnsureDrawingTransformSession(out var session)) return;
            var cos = MathF.Cos(_drawingTransformAccumulatedAngle);
            var sin = MathF.Sin(_drawingTransformAccumulatedAngle);
            var pivot = _transformPivot;
            _scene.ApplyTransformSession(
                session,
                point => new PointF(
                    pivot.X + (point.X - pivot.X) * cos - (point.Y - pivot.Y) * sin,
                    pivot.Y + (point.X - pivot.X) * sin + (point.Y - pivot.Y) * cos),
                convertPrimitivesToPaths: false,
                rebuildGeometryIndex: false);
            _transformLastAngle = angle;
        }
        else if (IsSkewHandle(_activeTransformHandle))
        {
            if (_selectedObjects.Any(IsWholeObjectOnlyObject)) return;
            var factor = SkewFactor(
                _activeTransformHandle,
                _drawingTransformStartBounds,
                _drawingTransformStartPointer,
                world);
            if (Math.Abs(factor) <= 0.0001f && _drawingTransformSession is null) return;
            if (!TryEnsureDrawingTransformSession(out var session)) return;
            var convertPrimitivesToPaths = Math.Abs(factor) > 0.0001f;
            if (IsHorizontalSkewHandle(_activeTransformHandle))
            {
                var anchorY = _activeTransformHandle == TransformHandleKind.SkewTop
                    ? _drawingTransformStartBounds.Bottom
                    : _drawingTransformStartBounds.Top;
                _scene.ApplyTransformSession(
                    session,
                    point => new PointF(point.X + (point.Y - anchorY) * factor, point.Y),
                    convertPrimitivesToPaths,
                    rebuildGeometryIndex: false);
            }
            else
            {
                var anchorX = _activeTransformHandle == TransformHandleKind.SkewLeft
                    ? _drawingTransformStartBounds.Right
                    : _drawingTransformStartBounds.Left;
                _scene.ApplyTransformSession(
                    session,
                    point => new PointF(point.X, point.Y + (point.X - anchorX) * factor),
                    convertPrimitivesToPaths,
                    rebuildGeometryIndex: false);
            }
        }
        else
        {
            if (!TryGetResizedTransformScaleFactors(
                    _drawingTransformStartBounds,
                    _transformPivot,
                    _activeTransformHandle,
                    world,
                    out var scaleX,
                    out var scaleY))
            {
                return;
            }

            (scaleX, scaleY) = ConstrainTransformScaleFactors(
                _activeTransformHandle,
                scaleX,
                scaleY,
                IsShiftPressed());
            if (Math.Abs(scaleX - 1) <= 0.0001f
                && Math.Abs(scaleY - 1) <= 0.0001f
                && _drawingTransformSession is null)
            {
                return;
            }

            if (!TryEnsureDrawingTransformSession(out var session)) return;
            var pivot = _transformPivot;
            // Parametric primitives store a positively oriented Width/Height/Angle, so they cannot
            // represent a mirror. Materialize them to paths whenever an axis flips, otherwise the
            // negative factor is silently rounded away and the shape stays unmirrored.
            var flipped = scaleX < 0f || scaleY < 0f;
            _scene.ApplyTransformSession(
                session,
                point => new PointF(
                    pivot.X + (point.X - pivot.X) * scaleX,
                    pivot.Y + (point.Y - pivot.Y) * scaleY),
                convertPrimitivesToPaths: flipped,
                rebuildGeometryIndex: false);
        }

        _transformLastPointer = world;
        _geometryDirty = true;
        UpdateTransformOverlay();
        _transformCurrentBounds = _stage.TransformBounds;
        _stage.Invalidate();
    }

    private bool TryEnsureDrawingTransformSession(out VectorScene.TransformSession session)
    {
        if (_drawingTransformSession is not null)
        {
            session = _drawingTransformSession;
            return true;
        }

        var materializeSelectedParts = _selectedElements.Count > 0;
        CapturePointerUndoSnapshot(
            allowSharedGeometry: _activeTransformHandle == TransformHandleKind.Move
                && !materializeSelectedParts);
        if (materializeSelectedParts)
        {
            var materialized = _pointerUndoSnapshot is { } rollbackSnapshot
                ? _scene.MaterializeSelectedParts(
                    _selectedElements.Select(hit => hit.Key).ToArray(),
                    _frame,
                    rollbackSnapshot)
                : _scene.MaterializeSelectedParts(
                    _selectedElements.Select(hit => hit.Key).ToArray(),
                    _frame);
            if (!materialized.Success || materialized.Parts.Length == 0)
            {
                if (_undoCapturedForPointerEdit && _undoStack.TryPop(out var undo))
                {
                    RestoreCancelledMarqueeHistory(undo);
                    RestoreCanvasMutationSnapshot(undo.Snapshot);
                }
                _undoCapturedForPointerEdit = false;
                _pointerUndoSnapshot = null;
                _pointerUndoSnapshotWithSharedGeometry = null;
                session = null!;
                return false;
            }

            var selectedObjects = materialized.Parts
                .Select(part => part.Result.ObjectIndex)
                .Distinct()
                .ToArray();
            var transformFocus = _transformFocus;
            SetSelection(selectedObjects, deferPresentation: true);
            _transformFocus = transformFocus;
            _geometryDirty |= materialized.Changed;
            if (materialized.Changed) QueueDeferredPresentationRefresh(DeferredPresentationRefresh.Hierarchy);
        }

        _drawingTransformSession = _scene.BeginTransformSession(_selectedObjects);
        session = _drawingTransformSession;
        return true;
    }

    private PointF TransformPivotFor(TransformHandleKind handle, RectangleF bounds)
    {
        return IsRotationHandle(handle) ? ActiveTransformFocus(bounds) : TransformPivot(handle, bounds);
    }

    private PointF TransformPivotFor(TransformHandleKind handle, TransformOverlayFrame frame)
    {
        if (IsRotationHandle(handle)) return _transformFocus ?? frame.Center;
        return handle switch
        {
            TransformHandleKind.TopLeft => frame.BottomRight,
            TransformHandleKind.Top => Midpoint(frame.BottomLeft, frame.BottomRight),
            TransformHandleKind.TopRight => frame.BottomLeft,
            TransformHandleKind.Right => Midpoint(frame.TopLeft, frame.BottomLeft),
            TransformHandleKind.BottomRight => frame.TopLeft,
            TransformHandleKind.Bottom => Midpoint(frame.TopLeft, frame.TopRight),
            TransformHandleKind.BottomLeft => frame.TopRight,
            TransformHandleKind.Left => Midpoint(frame.TopRight, frame.BottomRight),
            _ => frame.Center
        };
    }

    private PointF ActiveTransformFocus(RectangleF bounds)
    {
        return _transformFocus ?? new PointF(bounds.Left + bounds.Width * 0.5f, bounds.Top + bounds.Height * 0.5f);
    }

    private PointF ActiveTransformFocus(TransformOverlayFrame frame)
    {
        return _transformFocus ?? frame.Center;
    }

    private void TranslateCustomTransformFocus(float dx, float dy)
    {
        if (_transformFocus is not { } focus) return;
        _transformFocus = VectorUnits.Quantize(new PointF(focus.X + dx, focus.Y + dy));
    }

    private static bool IsRotationHandle(TransformHandleKind handle)
    {
        return handle is TransformHandleKind.Rotate
            or TransformHandleKind.RotateTopLeft
            or TransformHandleKind.RotateTopRight
            or TransformHandleKind.RotateBottomRight
            or TransformHandleKind.RotateBottomLeft;
    }

    private static bool IsSkewHandle(TransformHandleKind handle)
    {
        return handle is TransformHandleKind.SkewTop
            or TransformHandleKind.SkewRight
            or TransformHandleKind.SkewBottom
            or TransformHandleKind.SkewLeft;
    }

    private static bool IsHorizontalSkewHandle(TransformHandleKind handle)
    {
        return handle is TransformHandleKind.SkewTop or TransformHandleKind.SkewBottom;
    }

    private static float SkewFactor(TransformHandleKind handle, RectangleF bounds, PointF previous, PointF current)
    {
        return handle switch
        {
            TransformHandleKind.SkewTop => -(current.X - previous.X) / Math.Max(1, bounds.Height),
            TransformHandleKind.SkewBottom => (current.X - previous.X) / Math.Max(1, bounds.Height),
            TransformHandleKind.SkewLeft => -(current.Y - previous.Y) / Math.Max(1, bounds.Width),
            TransformHandleKind.SkewRight => (current.Y - previous.Y) / Math.Max(1, bounds.Width),
            _ => 0
        };
    }

    private static float SkewFactor(
        TransformHandleKind handle,
        TransformOverlayFrame frame,
        PointF previous,
        PointF current)
    {
        var delta = new Vector2(current.X - previous.X, current.Y - previous.Y);
        var axisX = new Vector2(frame.AxisX.X, frame.AxisX.Y);
        var axisY = new Vector2(frame.AxisY.X, frame.AxisY.Y);
        var width = Math.Max(1f, axisX.Length());
        var height = Math.Max(1f, axisY.Length());
        var alongX = Vector2.Dot(delta, Vector2.Normalize(axisX));
        var alongY = Vector2.Dot(delta, Vector2.Normalize(axisY));
        return handle switch
        {
            TransformHandleKind.SkewTop => -alongX / height,
            TransformHandleKind.SkewBottom => alongX / height,
            TransformHandleKind.SkewLeft => -alongY / width,
            TransformHandleKind.SkewRight => alongY / width,
            _ => 0
        };
    }

    private static bool TryGetOrientedScaleFactors(
        TransformOverlayFrame frame,
        TransformHandleKind handle,
        PointF pointer,
        out float scaleX,
        out float scaleY)
    {
        scaleX = 1f;
        scaleY = 1f;
        if (!TryGetFrameCoordinates(frame, pointer, out var local)) return false;
        var (handleX, handleY) = TransformHandleCoordinates(handle);
        var (pivotX, pivotY) = TransformHandleCoordinates(OppositeTransformHandle(handle));
        if (ChangesTransformWidth(handle)) scaleX = (local.X - pivotX) / (handleX - pivotX);
        if (ChangesTransformHeight(handle)) scaleY = (local.Y - pivotY) / (handleY - pivotY);
        // The ratios are already signed: dragging past the opposite anchor yields a negative
        // factor that mirrors the instance. Only reject degenerate magnitudes, not the sign.
        return float.IsFinite(scaleX)
            && float.IsFinite(scaleY)
            && Math.Abs(scaleX) > 0.01f
            && Math.Abs(scaleY) > 0.01f;
    }

    private static bool TryGetFrameCoordinates(TransformOverlayFrame frame, PointF point, out PointF local)
    {
        var determinant = frame.AxisX.X * frame.AxisY.Y - frame.AxisX.Y * frame.AxisY.X;
        if (Math.Abs(determinant) <= 0.000001f)
        {
            local = PointF.Empty;
            return false;
        }

        var dx = point.X - frame.Origin.X;
        var dy = point.Y - frame.Origin.Y;
        local = new PointF(
            (dx * frame.AxisY.Y - dy * frame.AxisY.X) / determinant,
            (frame.AxisX.X * dy - frame.AxisX.Y * dx) / determinant);
        return float.IsFinite(local.X) && float.IsFinite(local.Y);
    }

    private static (float X, float Y) TransformHandleCoordinates(TransformHandleKind handle)
    {
        return handle switch
        {
            TransformHandleKind.TopLeft => (0f, 0f),
            TransformHandleKind.Top => (0.5f, 0f),
            TransformHandleKind.TopRight => (1f, 0f),
            TransformHandleKind.Right => (1f, 0.5f),
            TransformHandleKind.BottomRight => (1f, 1f),
            TransformHandleKind.Bottom => (0.5f, 1f),
            TransformHandleKind.BottomLeft => (0f, 1f),
            TransformHandleKind.Left => (0f, 0.5f),
            _ => (0.5f, 0.5f)
        };
    }

    private static TransformHandleKind OppositeTransformHandle(TransformHandleKind handle)
    {
        return handle switch
        {
            TransformHandleKind.TopLeft => TransformHandleKind.BottomRight,
            TransformHandleKind.Top => TransformHandleKind.Bottom,
            TransformHandleKind.TopRight => TransformHandleKind.BottomLeft,
            TransformHandleKind.Right => TransformHandleKind.Left,
            TransformHandleKind.BottomRight => TransformHandleKind.TopLeft,
            TransformHandleKind.Bottom => TransformHandleKind.Top,
            TransformHandleKind.BottomLeft => TransformHandleKind.TopRight,
            TransformHandleKind.Left => TransformHandleKind.Right,
            _ => TransformHandleKind.None
        };
    }

    private static bool ChangesTransformWidth(TransformHandleKind handle)
    {
        return handle is TransformHandleKind.TopLeft
            or TransformHandleKind.TopRight
            or TransformHandleKind.Right
            or TransformHandleKind.BottomRight
            or TransformHandleKind.BottomLeft
            or TransformHandleKind.Left;
    }

    private static bool ChangesTransformHeight(TransformHandleKind handle)
    {
        return handle is TransformHandleKind.TopLeft
            or TransformHandleKind.Top
            or TransformHandleKind.TopRight
            or TransformHandleKind.BottomRight
            or TransformHandleKind.Bottom
            or TransformHandleKind.BottomLeft;
    }

    private static (float ScaleX, float ScaleY) ConstrainTransformScaleFactors(
        TransformHandleKind handle,
        float scaleX,
        float scaleY,
        bool keepAspectRatio)
    {
        if (!keepAspectRatio) return (scaleX, scaleY);

        var changesWidth = ChangesTransformWidth(handle);
        var changesHeight = ChangesTransformHeight(handle);
        if (!changesWidth && !changesHeight) return (scaleX, scaleY);

        // Compare how far each axis moved from its resting magnitude, so a flipped (negative)
        // factor can still win the uniform comparison and keep its sign.
        var uniformScale = changesWidth && changesHeight
            ? Math.Abs(Math.Abs(scaleX) - 1f) >= Math.Abs(Math.Abs(scaleY) - 1f) ? scaleX : scaleY
            : changesWidth ? scaleX : scaleY;
        return (uniformScale, uniformScale);
    }

    private static InstanceFrameState KeepWorldPointFixed(
        InstanceFrameState previous,
        InstanceFrameState next,
        PointF fixedWorldPoint)
    {
        var previousTransform = DrawingObjectInstanceDefinition.CreatePlanarTransform(previous);
        if (!Matrix3x2.Invert(previousTransform, out var inversePrevious)) return next;
        var local = Vector2.Transform(
            new Vector2(fixedWorldPoint.X, fixedWorldPoint.Y),
            inversePrevious);
        var mapped = Vector2.Transform(local, DrawingObjectInstanceDefinition.CreatePlanarTransform(next));
        return next with
        {
            X = VectorUnits.Quantize(next.X + fixedWorldPoint.X - mapped.X),
            Y = VectorUnits.Quantize(next.Y + fixedWorldPoint.Y - mapped.Y)
        };
    }

    private static InstanceFrameState MoveScalePivotWithGroup(
        InstanceFrameState previous,
        InstanceFrameState next,
        PointF groupPivot,
        float scaleX,
        float scaleY)
    {
        var operationPivot = DrawingObjectInstanceDefinition.ScalePivotScenePosition(previous);
        var target = new PointF(
            groupPivot.X + (operationPivot.X - groupPivot.X) * scaleX,
            groupPivot.Y + (operationPivot.Y - groupPivot.Y) * scaleY);
        return next with
        {
            X = VectorUnits.Quantize(previous.X + target.X - operationPivot.X),
            Y = VectorUnits.Quantize(previous.Y + target.Y - operationPivot.Y)
        };
    }

    private static PointF TransformPivot(TransformHandleKind handle, RectangleF bounds)
    {
        return handle switch
        {
            TransformHandleKind.TopLeft => new PointF(bounds.Right, bounds.Bottom),
            TransformHandleKind.Top => new PointF(bounds.Left + bounds.Width * 0.5f, bounds.Bottom),
            TransformHandleKind.TopRight => new PointF(bounds.Left, bounds.Bottom),
            TransformHandleKind.Right => new PointF(bounds.Left, bounds.Top + bounds.Height * 0.5f),
            TransformHandleKind.BottomRight => new PointF(bounds.Left, bounds.Top),
            TransformHandleKind.Bottom => new PointF(bounds.Left + bounds.Width * 0.5f, bounds.Top),
            TransformHandleKind.BottomLeft => new PointF(bounds.Right, bounds.Top),
            TransformHandleKind.Left => new PointF(bounds.Right, bounds.Top + bounds.Height * 0.5f),
            _ => new PointF(bounds.Left + bounds.Width * 0.5f, bounds.Top + bounds.Height * 0.5f)
        };
    }

    // A resize handle anchors the opposite edge or corner, and dragging the pointer across that
    // anchor must yield a mirrored (negative) scale factor. Clamping the moving edge onto the
    // anchor - as the previous bounds builder did - silently swallowed every flip and squashed
    // the selection to a sliver instead.
    //
    // The inward distance is measured from the anchor towards the handle's own starting side, so
    // a handle at rest reports exactly 1 and only a real crossing produces a negative ratio.
    internal static bool TryGetResizedTransformScaleFactors(
        RectangleF startBounds,
        PointF pivot,
        TransformHandleKind handle,
        PointF pointer,
        out float scaleX,
        out float scaleY)
    {
        const float degenerate = 0.0001f;
        scaleX = 1f;
        scaleY = 1f;
        var changesWidth = ChangesTransformWidth(handle);
        var changesHeight = ChangesTransformHeight(handle);
        if (!changesWidth && !changesHeight) return false;

        var (handleX, handleY) = TransformHandleCoordinates(handle);
        if (changesWidth)
        {
            if (startBounds.Width <= 0f) return false;
            var signedWidth = handleX <= 0f ? pivot.X - pointer.X : pointer.X - pivot.X;
            if (Math.Abs(signedWidth) <= degenerate) return false;
            scaleX = ApplyScaleFloor(signedWidth / startBounds.Width, FloorScale(startBounds.Width));
        }

        if (changesHeight)
        {
            if (startBounds.Height <= 0f) return false;
            var signedHeight = handleY <= 0f ? pivot.Y - pointer.Y : pointer.Y - pivot.Y;
            if (Math.Abs(signedHeight) <= degenerate) return false;
            scaleY = ApplyScaleFloor(signedHeight / startBounds.Height, FloorScale(startBounds.Height));
        }

        return float.IsFinite(scaleX) && float.IsFinite(scaleY);
    }

    // Keep a one-unit floor on the resulting edge length, expressed as a scale floor. Capped at 1
    // so a selection already smaller than one unit cannot be forced to grow on grab.
    private static float FloorScale(float startExtent) =>
        Math.Min(1f, 1f / startExtent);

    // Raise a scale factor to at least `minimum` in magnitude while preserving its sign, so a
    // flipped (negative) factor stays negative. Enlargement beyond 1 is never limited here.
    private static float ApplyScaleFloor(float value, float minimum)
    {
        if (!float.IsFinite(value)) return value < 0f ? -minimum : minimum;
        if (Math.Abs(value) >= minimum) return value;
        return value < 0f ? -minimum : minimum;
    }

    // Instance scale limits bound how small or large an instance may become. Bounding the
    // magnitude keeps a flipped (negative) factor negative, so clamping cannot undo a flip.
    private static float ClampScaleMagnitude(float value, float minimum, float maximum)
    {
        if (!float.IsFinite(value)) return value < 0f ? -minimum : minimum;
        var magnitude = Math.Clamp(Math.Abs(value), minimum, maximum);
        return value < 0f ? -magnitude : magnitude;
    }

    private static float TransformPointerAngle(PointF point, PointF pivot) => MathF.Atan2(point.Y - pivot.Y, point.X - pivot.X);

    private static float NormalizeAngle(float angle)
    {
        while (angle > MathF.PI) angle -= MathF.Tau;
        while (angle < -MathF.PI) angle += MathF.Tau;
        return angle;
    }

    private static PointF RotatePointAround(PointF point, PointF pivot, float radians)
    {
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);
        var x = point.X - pivot.X;
        var y = point.Y - pivot.Y;
        return new PointF(pivot.X + x * cos - y * sin, pivot.Y + x * sin + y * cos);
    }

    private static float NormalizeDegrees(float degrees)
    {
        while (degrees > 180) degrees -= 360;
        while (degrees <= -180) degrees += 360;
        return degrees;
    }

    // Captures the transform box at rotation start so the rest of the drag can rotate it rigidly.
    // Returns false when the box is degenerate, in which case rotation falls back to recomputing
    // the frame from the live geometry.
    private bool TryBeginRotationTransformFrame()
    {
        var frame = _stage.TransformFrame;
        if (!frame.IsValid) return false;
        _transformFrozenFrame = frame;
        _transformFrozenPivot = _transformPivot;
        _transformFrozenFocus = _transformFocus;
        _transformFrameFrozenForRotation = true;
        return true;
    }

    private void EndRotationTransformFrame()
    {
        _transformFrameFrozenForRotation = false;
        _transformFrozenFrame = default;
        _transformFrozenPivot = PointF.Empty;
        _transformFrozenFocus = null;
    }

    // The box the overlay should draw for the current rotation drag. While frozen the captured
    // frame is turned by the same angle the geometry received, so the box stays glued to the
    // shape and the handle under the pointer never slides away.
    private TransformOverlayFrame RotatedFrozenTransformFrame(float radians)
    {
        var frame = _transformFrozenFrame;
        var pivot = _transformFrozenPivot;
        return new TransformOverlayFrame(
            RotatePointAround(frame.Origin, pivot, radians),
            RotateVector(frame.AxisX, radians),
            RotateVector(frame.AxisY, radians));
    }

    private static PointF RotateVector(PointF vector, float radians)
    {
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);
        return new PointF(
            vector.X * cos - vector.Y * sin,
            vector.X * sin + vector.Y * cos);
    }

    private bool TryFindSelectedLineControlHit(Point screen, out DrawingElementHit line)
    {
        line = DrawingElementHit.None;
        var hovered = DrawingElementHit.None;
        var seen = new HashSet<DrawingElementKey>();

        bool TryCandidate(DrawingElementHit candidate)
        {
            if (!candidate.IsValid
                || candidate.Key.Kind is not (DrawingElementKind.Stroke or DrawingElementKind.BoundaryStroke)
                || (uint)candidate.Key.ObjectIndex >= _scene.ObjectCount
                || !seen.Add(candidate.Key))
            {
                return false;
            }

            if ((candidate.Key.Kind == DrawingElementKind.BoundaryStroke
                    || _scene.ShapeKind[candidate.Key.ObjectIndex] == ShapeKind.Line)
                && _stage.TryResolveEditableBezierHit(screen, candidate, out var presentedCandidate))
            {
                candidate = presentedCandidate;
            }
            if (_stage.HitTestLineElementHandle(screen, candidate) == EditHandleKind.None) return false;

            hovered = candidate;
            return true;
        }

        if (_selectedElement.IsValid && TryCandidate(_selectedElement))
        {
            line = hovered;
            return true;
        }
        foreach (var candidate in _selectedElements)
        {
            if (!TryCandidate(candidate)) continue;
            line = hovered;
            return true;
        }

        foreach (var objectIndex in _selectedObjects)
        {
            if ((uint)objectIndex >= _scene.ObjectCount || _scene.ShapeKind[objectIndex] != ShapeKind.Line) continue;
            if (TryCandidate(new DrawingElementHit(
                    new DrawingElementKey(objectIndex, DrawingElementKind.Stroke, 0),
                    0,
                    0,
                    1)))
            {
                line = hovered;
                return true;
            }
        }

        return false;
    }

    private void CaptureFillEdgeBezierOverlayBase(int objectIndex)
    {
        if ((uint)objectIndex >= _scene.ObjectCount)
        {
            _fillEdgeBezierOverlayBaseObject = -1;
            _fillEdgeBezierOverlayBasePosition = PointF.Empty;
            return;
        }

        _fillEdgeBezierOverlayBaseObject = objectIndex;
        _fillEdgeBezierOverlayBasePosition = new PointF(_scene.X[objectIndex], _scene.Y[objectIndex]);
    }

    private bool TryBegin3DViewDragFromMove(MouseEventArgs e)
    {
        if (!IsSceneReferenceView()) return false;
        if (MouseButtonDown(e, MouseButtons.Middle))
        {
            BeginGlobalViewDrag(e);
            return true;
        }

        if (MouseButtonDown(e, MouseButtons.Right) && IsAltPressed())
        {
            BeginGlobalViewDrag(e, referencePan: true);
            return true;
        }

        return false;
    }

    private bool TryPromote3DViewDragFromMove(MouseEventArgs e)
    {
        if (!IsSceneReferenceView()) return false;
        if (_viewPanning || _viewZooming || _viewOrbiting || _viewReferencePanning || _viewReferenceZooming) return false;
        if (MouseButtonDown(e, MouseButtons.Middle))
        {
            BeginGlobalViewDrag(e);
            return true;
        }

        if (MouseButtonDown(e, MouseButtons.Right) && IsAltPressed())
        {
            BeginGlobalViewDrag(e, referencePan: true);
            return true;
        }

        return false;
    }

    private bool PointerDragExceeded(Point current)
    {
        if (_startScreen is not { } start) return false;
        return Math.Abs(current.X - start.X) + Math.Abs(current.Y - start.Y) > 6;
    }

    private bool PointerMovedFromStart(Point current)
    {
        return _startScreen is { } start && current != start;
    }

    private void BeginMarqueeFromPendingSelection(Point current)
    {
        _marqueeSelecting = true;
        _marqueeStart = _startScreen ?? current;
        _marqueeSelectionBase = _additiveSelection ? _selectedObjects.ToArray() : [];
        _marqueeInstanceSelectionBase = _additiveSelection ? _selectedSceneInstanceIds.ToArray() : [];
        _pendingClickSelection = DrawingElementHit.None;
        _selectedStart = null;
        _curveControlStart = null;
        _curveControl2Start = null;
        _freehandBezierEditStart = null;
        _freehandBezierEditObject = -1;
        _freehandBezierEditSegment = -1;
        _resizeStartCenter = null;
        _resizeStartSize = null;
        _textAreaResizeStartData = null;
        _activeHandle = EditHandleKind.None;
        _selectedMoveStarts.Clear();
        _selectedCurveStarts.Clear();
        _selectedCurve2Starts.Clear();
        _selectedGradientStarts.Clear();
        _lineEndpointEditStarts.Clear();
        _lineEndpointFillIntersections.Clear();
        _lineEndpointFillIntersectionsCaptured = false;
        _stage.SetMarquee(_marqueeStart.Value, current);
    }

    private void BeginBlankMarqueeSelection(Point current)
    {
        BeginMarqueeFromPendingSelection(current);
        if (_additiveSelection) return;

        _marqueeSelectionCancellationPending = true;
        ClearSelection(deferPresentation: true);
    }

    private void FinalizePendingMarqueeSelectionCancellation()
    {
        if (!_marqueeSelectionCancellationPending) return;
        _marqueeSelectionCancellationPending = false;
        CancelStageSelection();
    }

    private void StageMouseUp(object? sender, MouseEventArgs e)
    {
        if (HandleReferenceCameraRightLookMouseUp(e)) return;

        if (_tabletPressurePointerId is not null && e.Button == MouseButtons.Left) return;
        if (_brushColorPaletteActive)
        {
            UpdateBrushColorPaletteHover(e.Location);
            return;
        }

        if (_fillEdgeNoUiArc.HasValue)
        {
            CompleteFillEdgeNoUiArc();
            return;
        }

        if (_fillEdgeDetachMoving.HasValue)
        {
            CompleteFillEdgeDetach();
            return;
        }

        if (_fillEdgeDetachPending.HasValue)
        {
            var pending = _fillEdgeDetachPending.Value;
            _fillEdgeDetachPending = null;
            SelectPreSelectObject((pending.ObjectIndex, pending.PartIndex), e.Location);
            FinishPointerInteraction();
            return;
        }

        if (_fillEdgePreSelect.HasValue)
        {
            SelectPreSelectObject(_fillEdgePreSelect.Value, e.Location);
            _fillEdgePreSelect = null;
            FinishPointerInteraction();
            return;
        }

        if (IsLassoTool(_tool) && HandleLassoMouseUp(e)) return;

        if (HandleSpatialTransformKeyboardMouseUp(e)) return;

        if (_shotFramingPointerSession is not null)
        {
            if (e.Button == MouseButtons.Left) UpdateShotFramingPointer(e.Location);
            CompleteShotFramingPointer();
            return;
        }

        if (_sceneLightGizmoPointerSession is not null)
        {
            if (e.Button == MouseButtons.Left)
            {
                ClearPendingSceneLightGizmoPointer();
                UpdateSceneLightGizmoPointer(e.Location);
            }
            CompleteSceneLightGizmoPointer();
            return;
        }

        if (_snapPointEditSession is not null)
        {
            if (e.Button == MouseButtons.Left) UpdateSnapPointPointer(e.Location);
            CompleteSnapPointPointer();
            FinishPointerInteraction();
            return;
        }

        if (_spacePanPointerActive)
        {
            EndTemporaryCanvasPanPointer();
            return;
        }

        if (_viewPanning || _viewZooming || _viewOrbiting || _viewReferencePanning || _viewReferenceZooming)
        {
            EndGlobalViewDrag();
            return;
        }

        if (IsShotDirectorContext())
        {
            FinishPointerInteraction();
            return;
        }

        if (_spatialTransformPointerSession is not null)
        {
            if (e.Button == MouseButtons.Left)
            {
                ClearPendingSpatialTransformPointer();
                UpdateSpatialTransformPointer(e.Location);
                CompleteSpatialTransformPointer();
            }
            else if (e.Button == MouseButtons.Right)
            {
                CancelSpatialTransformPointer();
            }
            return;
        }

        if (_projectedSceneMoveStartRayOrigin is not null)
        {
            UpdateProjectedSceneMovePointer(e);
            CompleteProjectedSceneMovePointer();
            return;
        }

        FlushLineDragPreview();

        if (_fillEdgeBezierArmedSession is { } armedFillEdgeSession)
        {
            if (e.Button == MouseButtons.Left)
            {
                UpdateArmedFillEdgeBezierPreview(
                    armedFillEdgeSession,
                    _stage.ScreenToWorld(e.Location),
                    e.Location,
                    PointerMovedFromStart(e.Location));
            }
            if (!armedFillEdgeSession.ResetCurvature && !armedFillEdgeSession.PointerMoved)
            {
                CancelArmedFillEdgeBezierPointer();
                FinishPointerInteraction();
                return;
            }

            armedFillEdgeSession.CompleteAfterPreparation = true;
            PrepareArmedFillEdgeBezierPointer(armedFillEdgeSession.Generation);
            return;
        }

        if (_fillEdgeBezierEditSession is not null)
        {
            CompleteFillEdgeBezierPointer();
            FinishPointerInteraction();
            return;
        }

        if (_lineBranchDragSession is not null)
        {
            CompleteLineBranchDrag(e.Location, e.Button);
            FinishPointerInteraction();
            return;
        }

        if (_arcDragSession is not null)
        {
            CompleteArcDrag(e.Location, e.Button);
            FinishPointerInteraction();
            return;
        }

        if (_cornerDragSession is not null)
        {
            CompleteCornerDrag(e.Location, e.Button);
            FinishPointerInteraction();
            return;
        }

        if (_marqueeSelecting)
        {
            CompleteMarqueeSelection(e.Location);
            _stage.ClearDrawingPreview();
            FinishPointerInteraction();
            return;
        }

        if (MotionTrackPointerUp(e)) return;

        if (_tool == ToolMode.Pen && _traditionalPenAnchorEditing)
        {
            CompleteTraditionalPenAnchorEdit();
            UpdateTraditionalPenHover(e.Location);
            return;
        }

        if (_tool == ToolMode.Pen)
        {
            if (_traditionalPenPointerDown) FinishTraditionalPenPoint(e.Location);
            FinishTraditionalPenPointerDrag();
            if (_traditionalPenCurrentAnchor is not null) UpdateTraditionalPenHover(e.Location);
            return;
        }

        if (_tool == ToolMode.SimplePen)
        {
            if (_penSegmentDragging)
            {
                UpdatePenControl(e.Location);
                CommitPenSegment();
            }
            FinishPenSegmentPointerDrag();
            return;
        }

        if (_tool == ToolMode.Select && _pendingClickSelection.IsValid)
        {
            SetSelection(_pendingClickSelection, deferPresentation: true);
            _stage.InvalidateOverlay();
        }

        if (_freehandDrawing)
        {
            AppendFreehandSample(e.Location, force: true);
            CommitFreehandStroke();
            FinishPointerInteraction();
            return;
        }

        if (IsDrawingTool(_tool) && !IsFreehandTool(_tool) && _startWorld is not null && _startScreen is not null)
        {
            var dx = e.X - _startScreen.Value.X;
            var dy = e.Y - _startScreen.Value.Y;
            if (Math.Abs(dx) + Math.Abs(dy) > 3) AddDrawnObject(_startWorld.Value, _stage.ScreenToWorld(e.Location), _tool);
        }

        _stage.ClearDrawingPreview();
        FinishPointerInteraction();
    }

    private void StageMouseCaptureChanged(object? sender, EventArgs e)
    {
        if (_stage.Capture) return;
        // WinForms releases capture after MouseUp, between polygon vertices.
        if (_lassoPointerActive
            && _tool == ToolMode.PolygonLasso
            && !_lassoPointerDown)
        {
            return;
        }
        FinishLostPointerCapture();
    }

    private void FinishLostPointerCapture()
    {
        if (ReferenceCameraRightLookSessionActive)
        {
            CancelReferenceCameraRightLook();
            return;
        }

        if (CancelLassoPointerForLifecycle()) return;

        FinalizePendingMarqueeSelectionCancellation();
        if (_shotFramingPointerSession is not null)
        {
            CompleteShotFramingPointer();
            return;
        }
        if (_sceneLightGizmoPointerSession is not null)
        {
            CompleteSceneLightGizmoPointer();
            return;
        }
        if (_snapPointEditSession is not null)
        {
            CompleteSnapPointPointer();
            FinishPointerInteraction();
            return;
        }
        if (_spatialTransformKeyboardActive)
        {
            CancelSpatialTransformKeyboard();
            return;
        }
        if (_spatialTransformPointerSession is not null)
        {
            CompleteSpatialTransformPointer();
            return;
        }
        if (_projectedSceneMoveStartRayOrigin is not null)
        {
            CompleteProjectedSceneMovePointer();
            return;
        }
        if (_spacePanPointerActive)
        {
            EndTemporaryCanvasPanPointer();
            return;
        }
        if (_viewPanning || _viewZooming || _viewOrbiting || _viewReferencePanning || _viewReferenceZooming)
        {
            EndGlobalViewDrag();
            return;
        }

        if (_lineBranchDragSession is not null)
        {
            CancelLineBranchDrag(restore: true);
            FinishPointerInteraction();
            return;
        }

        if (_arcDragSession is not null)
        {
            CancelArcDrag(restore: true);
            FinishPointerInteraction();
            return;
        }

        if (_cornerDragSession is not null)
        {
            CancelCornerDrag(restore: true);
            FinishPointerInteraction();
            return;
        }

        FlushLineDragPreview();

        if (_fillEdgeBezierArmedSession is not null)
        {
            CancelArmedFillEdgeBezierPointer();
            FinishPointerInteraction();
            return;
        }

        if (_fillEdgeBezierEditSession is not null)
        {
            CompleteFillEdgeBezierPointer();
            FinishPointerInteraction();
            return;
        }

        if (_traditionalPenAnchorEditing)
        {
            CompleteTraditionalPenAnchorEdit();
            return;
        }

        if (_traditionalPenPointerDown)
        {
            var pointer = _stage.PointToClient(Cursor.Position);
            FinishTraditionalPenPoint(pointer);
            FinishTraditionalPenPointerDrag();
            _stage.ClearPenAnchorGuides();
            if (_traditionalPenCurrentAnchor is not null) UpdateTraditionalPenHover(pointer);
            return;
        }

        if (_penSegmentDragging)
        {
            if (!CommitPenSegment())
            {
                _penEndWorld = null;
                _penControlWorld = null;
            }
            FinishPenSegmentPointerDrag();
            _stage.ClearDrawingPreview();
            _stage.ClearPenAnchorGuides();
            return;
        }

        if (_gradientEditSnapshot is not null)
        {
            CompleteGradientPointer();
            FinishPointerInteraction();
            return;
        }

        if (_lastMouse is null
            && !_freehandDrawing
            && !_marqueeSelecting
            && !_marqueeSelectionCancellationPending)
        {
            return;
        }
        if (_gradientEditSnapshot is not null) CancelGradientPointer(restore: true);
        CancelFreehandStroke();
        _marqueeSelecting = false;
        _marqueeStart = null;
        _stage.ClearDrawingPreview();
        _stage.ClearMarquee();
        FinishPointerInteraction();
        _stage.Invalidate();
    }

    private void FinishPointerInteractionForFrameChange()
    {
        CancelReferenceCameraRightLook();
        CancelTemporaryCanvasPan();
        if (CancelLassoPointerForLifecycle()) return;
        if (_shotFramingPointerSession is not null) CancelShotFramingPointer();
        if (_sceneLightGizmoPointerSession is not null) CancelSceneLightGizmoPointer();
        if (_snapPointEditSession is not null) CancelSnapPointPointer(restore: true);
        if (_projectedSceneMoveStartRayOrigin is not null) CancelProjectedSceneMovePointer();
        else if (_spatialTransformKeyboardActive) CancelSpatialTransformKeyboard();
        else if (_spatialTransformPointerSession is not null) CancelSpatialTransformPointer();
        else if (_spatialTransformEditSession is not null) CancelSpatialTransformEdit();
        if (_lastMouse is not null
            || _freehandDrawing
            || _marqueeSelecting
            || _marqueeSelectionCancellationPending
            || _viewPanning
            || _viewZooming
            || _viewOrbiting
            || _viewReferencePanning
            || _viewReferenceZooming)
        {
            FinishLostPointerCapture();
        }

        // A frame change re-centres the onion skin, so the anchor drag and box selection are no
        // longer anchored to what the operator grabbed.
        AbortMotionTrackPointerSession();

        _pendingClickSelection = DrawingElementHit.None;
        CancelTraditionalPenPath();
        CancelPenCurve();
        _forceMarqueeOnPointerDown = false;
        _additiveSelection = false;
        _marqueeSelecting = false;
        _marqueeStart = null;
        _marqueeSelectionBase = [];
        _marqueeInstanceSelectionBase = [];
        _fillBoundaryLineLinks.Clear();
        _fillBoundaryLineLinksCaptured = false;
        _lineEndpointFillIntersections.Clear();
        _lineEndpointFillIntersectionsCaptured = false;
        _stage.ClearMarquee();
    }

    private bool HasActiveCanvasPointerInteraction(bool includeCapture = true)
    {
        return includeCapture && _stage.Capture
            || ReferenceCameraRightLookSessionActive
            || _lassoPointerActive
            || _lastMouse is not null
            || _freehandDrawing
            || _tabletPressurePointerId is not null
            || _marqueeSelecting
            || _marqueeSelectionCancellationPending
            || _traditionalPenPointerDown
            || _traditionalPenAnchorEditing
            || _penSegmentDragging
            || _gradientEditSnapshot is not null
            || _fillEdgeBezierArmedSession is not null
            || _fillEdgeBezierEditSession is not null
            || _lineBranchDragSession is not null
            || _arcDragSession is not null
            || _cornerDragSession is not null
            || _drawingTransformSession is not null
            || _shotFramingPointerSession is not null
            || _sceneLightGizmoPointerSession is not null
            || _spatialTransformPointerSession is not null
            || _spatialTransformEditSession is not null
            || _sceneInstanceMoveActive
            || _snapPointEditSession is not null
            || _spacePanPointerActive
            || _viewPanning
            || _viewZooming
            || _viewOrbiting
            || _viewReferencePanning
            || _viewReferenceZooming;
    }

    private void FinishPointerInteractionForContextChange()
    {
        CancelReferenceCameraRightLook();
        CancelReferenceCameraKeyboardNavigation();
        CancelTemporaryCanvasPan();
        if (CancelLassoPointerForLifecycle()) return;
        if (_shotFramingPointerSession is not null)
        {
            CancelShotFramingPointer();
            return;
        }
        if (_sceneLightGizmoPointerSession is not null)
        {
            CancelSceneLightGizmoPointer();
            return;
        }
        if (_snapPointEditSession is not null) CancelSnapPointPointer(restore: true);
        if (_projectedSceneMoveStartRayOrigin is not null)
        {
            CancelProjectedSceneMovePointer();
            return;
        }
        if (_spatialTransformKeyboardActive)
        {
            CancelSpatialTransformKeyboard();
            return;
        }
        if (_spatialTransformPointerSession is not null)
        {
            CancelSpatialTransformPointer();
            return;
        }
        if (_spatialTransformEditSession is not null)
        {
            CancelSpatialTransformEdit();
        }
        if (_fillEdgeBezierArmedSession is not null || _fillEdgeBezierEditSession is not null)
        {
            CancelFillEdgeBezierPointer(restore: true);
            FinishPointerInteraction();
            return;
        }
        if (_lineBranchDragSession is not null)
        {
            CancelLineBranchDrag(restore: true);
            FinishPointerInteraction();
            return;
        }
        if (_arcDragSession is not null)
        {
            CancelArcDrag(restore: true);
            FinishPointerInteraction();
            return;
        }

        if (_cornerDragSession is not null)
        {
            CancelCornerDrag(restore: true);
            FinishPointerInteraction();
            return;
        }
        if (_gradientEditSnapshot is not null)
        {
            CancelGradientPointer(restore: true);
            FinishPointerInteraction();
            return;
        }
        if (_traditionalPenPointerDown || _traditionalPenAnchorEditing)
        {
            CancelTraditionalPenPath();
            return;
        }
        if (_penSegmentDragging)
        {
            CancelPenCurve();
            return;
        }
        if (HasActiveCanvasPointerInteraction()) FinishLostPointerCapture();
    }

    private void FinishPointerInteraction()
    {
        CancelReferenceCameraRightLook();
        _sceneSnapMoveStartPointer = null;
        _stage.ClearSceneSnapIndicator();
        CompleteDistortPointerPreview();
        if (_spatialTransformEditSession is not null
            && _spatialTransformPointerSession is null
            && _projectedSceneMoveStartRayOrigin is null)
        {
            CompleteSpatialTransformEdit();
        }
        if (_geometryDirty
            && _freehandBezierEditObject >= 0
            && _freehandBezierEditSegment >= 0)
        {
            _scene.CompleteFreehandBezierPreview(
                _freehandBezierEditObject,
                rebuildGeometryIndex: false);
        }
        FinalizePointerUndoSnapshot();
        _stage.ClearSelectionFillDragFront();
        var lineMergeSeedKeys = _lineEndpointEditStarts
            .Select(edit => edit.ObjectIndex)
            .Concat(_selectedObjects)
            .Where(index => (uint)index < _scene.ObjectCount
                && _scene.ShapeKind[index] == ShapeKind.Line)
            .Select(index => new DrawingStackKey(
                _scene.ObjectOrder[index],
                _scene.ObjectSubOrder[index]))
            .Distinct()
            .ToArray();
        var preserveSharedBoundarySplit = _lineEndpointFillIntersections.Any(intersection =>
            intersection.PathAnchors.Length > 0);
        var linkedFillKeys = _fillBoundaryLineLinks
            .Select(link => link.FillObjectIndex)
            .Where(index => (uint)index < _scene.ObjectCount && _scene.HasFill(index))
            .Select(index => new DrawingStackKey(
                _scene.ObjectOrder[index],
                _scene.ObjectSubOrder[index]))
            .Distinct()
            .ToArray();
        FinalizePendingMarqueeSelectionCancellation();
        if (_lineBranchDragSession is not null) CancelLineBranchDrag(restore: true);
        if (_arcDragSession is not null) CancelArcDrag(restore: true);
        if (_cornerDragSession is not null) CancelCornerDrag(restore: true);
        ResetLineDragPreview();
        _lastMouse = null;
        _startScreen = null;
        _startWorld = null;
        _selectedStart = null;
        _curveControlStart = null;
        _curveControl2Start = null;
        _freehandBezierEditStart = null;
        _freehandBezierEditObject = -1;
        _freehandBezierEditSegment = -1;
        _selectedMoveStarts.Clear();
        _selectedCurveStarts.Clear();
        _selectedCurve2Starts.Clear();
        _lineEndpointEditStarts.Clear();
        _fillBoundaryLineLinks.Clear();
        _fillBoundaryLineLinksCaptured = false;
        _lineEndpointFillIntersections.Clear();
        _lineEndpointFillIntersectionsCaptured = false;
        _detachedSelectionForMove = false;
        _independentMarqueeStrokeMove = false;
        _pointerHitWasAlreadySelected = false;
        _selectionWasEmptyOnPointerDown = false;
        _forceMarqueeOnPointerDown = false;
        _additiveSelection = false;
        _pendingClickSelection = DrawingElementHit.None;
        _marqueeSelectionBase = [];
        _marqueeInstanceSelectionBase = [];
        _undoCapturedForPointerEdit = false;
        _pointerUndoSnapshot = null;
        _pointerUndoSnapshotWithSharedGeometry = null;
        _pointerTopologyQueriesInvalidated = false;
        _resizeStartCenter = null;
        _resizeStartSize = null;
        _textAreaResizeStartData = null;
        _activeHandle = EditHandleKind.None;
        _activeTransformHandle = TransformHandleKind.None;
        _drawingTransformSession = null;
        _pendingDrawingTransformWorld = null;
        EndRotationTransformFrame();
        _distortStartEnvelope = default;
        _distortCurrentEnvelope = default;
        _distortSourceFrame = default;
        _activeDistortVisualHandle = default;
        _distortVisualHandleActive = false;
        _distortReplaceExistingWarp = false;
        _distortPreviewScene = null;
        _distortPreviewBaseStacks.Clear();
        _distortPreviewReplaceLast.Clear();
        _distortPreviewChanged = false;
        _drawingTransformStartBounds = RectangleF.Empty;
        _drawingTransformStartFocus = null;
        _drawingTransformAccumulatedAngle = 0;
        _sceneInstanceMoveActive = false;
        _sceneInstanceTransformPreviews.Clear();
        _sceneInstanceTimelineKeyframeEnsuredIds.Clear();
        if (_sceneInstanceTimelineDirty)
        {
            if (RefreshEditedInstanceTimelineTweens())
            {
                _sceneInstancePreviewDirty = true;
            }
            _sceneInstanceTimelineDirty = false;
            _timeline.RefreshTimeline();
        }
        _sceneInstanceTimelineEditedFrames.Clear();
        if (_sceneInstancePreviewDirty)
        {
            RebuildEditableInstanceComposition();
            UpdateInspector();
            ConsumeDeferredPresentationRefresh(DeferredPresentationRefresh.Inspector);
        }
        if (_geometryDirty)
        {
            _scene.CompleteDeferredBuild();
            var fillsOverwritten = ApplySelectedFillOverwriteAfterGeometryEdit();
            var fillsChanged = MergeSelectedFillsAfterGeometryEdit(
                connectNearby: true,
                additionalFillKeys: linkedFillKeys);
            var lineMergeSeeds = lineMergeSeedKeys
                .Select(FindObjectByStackKey)
                .Where(index => index >= 0)
                .ToArray();
            var linesChanged = !preserveSharedBoundarySplit
                && MergeCompatibleLinesAfterDrawingOperation(LocalLineMergeScope(lineMergeSeeds));
            if (fillsOverwritten || fillsChanged || linesChanged)
            {
                _hierarchyPanel.RefreshScene();
                ConsumeDeferredPresentationRefresh(DeferredPresentationRefresh.Hierarchy);
            }
            if (_scene.RefreshTimelineTweenMaterializationsAtEndpointFrame(_frame))
            {
                RebuildOnionSkinPreview();
            }
            _geometryDirty = false;
            UpdateInspector();
            ConsumeDeferredPresentationRefresh(DeferredPresentationRefresh.Inspector);
        }
        if (_stage.FillEdgeBezierOverlayTranslation != PointF.Empty) UpdateFillEdgeBezierOverlay();
        if (IsSceneMaskEditing()) RebuildDrawingObjectUnderlay();
        UpdateTransformOverlay();
        _stage.ClearSelectionDragPreview();
        _stage.Capture = false;
        _fillEdgeSuppressOverlay = false;
        _fillEdgeNoUiArc = null;
        _fillEdgeDetachPending = null;
        _fillEdgeDetachMoving = null;
        RefreshInteractionCursorAtPointer();
    }

    private void StageDragEnter(object? sender, DragEventArgs e)
    {
        if (IsShotDirectorContext())
        {
            ClearDrawingObjectDragPreview();
            e.Effect = DragDropEffects.None;
            return;
        }

        if (TryResolveDroppedExternalSvgAsset(e.Data, out var externalSvgAsset, out var externalSvgPath))
        {
            UpdateFileDropFeedback(
                e,
                CanImportSvg(),
                externalSvgAsset.Id,
                () => BuildExternalSvgDropPreviewScene(externalSvgPath));
            return;
        }
        if (TryResolveDroppedImageAsset(e.Data, out var imageAsset, out var imagePath))
        {
            UpdateFileDropFeedback(
                e,
                CanPlaceImage(),
                imageAsset.Id,
                () => BuildImageDropPreviewScene(imageAsset, imagePath));
            return;
        }
        if (TryResolveDroppedImageFile(e.Data, out var imageFilePath, out var imageFileAsset))
        {
            UpdateFileDropFeedback(
                e,
                CanPlaceImage(),
                imageFileAsset?.Id ?? imageFilePath,
                () => imageFileAsset is null
                    ? null
                    : BuildImageDropPreviewScene(imageFileAsset, imageFilePath));
            return;
        }
        if (TryResolveDroppedSvgFile(e.Data, out var svgFilePath))
        {
            UpdateFileDropFeedback(
                e,
                CanImportSvg(),
                svgFilePath,
                () => BuildSvgFileDropPreviewScene(svgFilePath));
            return;
        }

        ClearImportedObjectDragPreview();
        if (!TryResolveDroppedDrawingObject(e.Data, out var drawingObject))
        {
            ClearDrawingObjectDragPreview();
            e.Effect = DragDropEffects.None;
            return;
        }

        var canPlace = CanPlaceDroppedDrawingObject(drawingObject);
        e.Effect = canPlace ? DragDropEffects.Copy : DragDropEffects.None;
        if (canPlace && TryDragEventWorldPosition(e, out var world))
        {
            UpdateDrawingObjectDragPreview(drawingObject, world);
        }
        else
        {
            e.Effect = DragDropEffects.None;
            ClearDrawingObjectDragPreview();
        }
    }

    /// <summary>
    /// Reports the effect for a file-backed drop and positions its preview. The preview
    /// scene is built once per distinct dragged file (<paramref name="previewKey"/>) and
    /// only when it is actually needed, so hovering never mutates the project.
    /// </summary>
    private void UpdateFileDropFeedback(
        DragEventArgs e,
        bool canDrop,
        string previewKey,
        Func<VectorScene?> buildPreview)
    {
        ClearDrawingObjectDragPreview();
        if (!canDrop || !TryDragEventWorldPosition(e, out var world))
        {
            ClearImportedObjectDragPreview();
            e.Effect = canDrop ? DragDropEffects.Copy : DragDropEffects.None;
            return;
        }

        e.Effect = DragDropEffects.Copy;
        if (_dragPreviewImportedScene is null
            || !string.Equals(_dragPreviewImportedKey, previewKey, StringComparison.Ordinal))
        {
            var preview = buildPreview();
            if (preview is null)
            {
                ClearImportedObjectDragPreview();
                return;
            }

            _dragPreviewImportedKey = previewKey;
            _dragPreviewImportedScene = preview;
            _dragPreviewPosition = world;
            _stage.BindDragPreviewScene(preview);
            return;
        }

        var dx = world.X - _dragPreviewPosition.X;
        var dy = world.Y - _dragPreviewPosition.Y;
        if (Math.Abs(dx) <= 0.0001f && Math.Abs(dy) <= 0.0001f) return;
        _dragPreviewImportedScene.TranslateAllObjectsForPreview(dx, dy);
        _dragPreviewPosition = world;
        _stage.Invalidate();
    }

    private void StageDragOver(object? sender, DragEventArgs e)
    {
        StageDragEnter(sender, e);
    }

    private void StageDragLeave(object? sender, EventArgs e)
    {
        ClearDrawingObjectDragPreview();
        ClearImportedObjectDragPreview();
    }

    private bool CanPlaceDroppedDrawingObject(DrawingObjectDefinition drawingObject)
    {
        if (IsShotDirectorContext() || IsSceneMaskEditing()) return false;
        return IsSceneCompositionContext()
            ? ActiveScene() is not null
            : ActiveDrawingObject() is { } container && _project.CanContainDrawingObject(container.Id, drawingObject.Id);
    }

    private PointF DragEventWorldPosition(DragEventArgs e)
    {
        var client = _stage.PointToClient(new Point(e.X, e.Y));
        return TryGetSceneBasePlanePoint(client, out var world)
            ? world
            : _stage.ScreenToWorld(client);
    }

    private bool TryDragEventWorldPosition(DragEventArgs e, out PointF world)
    {
        var client = _stage.PointToClient(new Point(e.X, e.Y));
        return TryGetSceneBasePlanePoint(client, out world);
    }

    private void UpdateDrawingObjectDragPreview(DrawingObjectDefinition drawingObject, PointF world)
    {
        if (!string.Equals(_dragPreviewDrawingObjectId, drawingObject.Id, StringComparison.Ordinal)
            || _stage.DragPreviewScene is null)
        {
            _dragPreviewDrawingObjectId = drawingObject.Id;
            _dragPreviewPosition = world;
            SceneCompositionBuilder.BuildDrawingObjectPreview(
                _dragPreviewStage,
                drawingObject,
                _drawingObjects,
                world,
                _frame,
                parentFps: _playbackSettings.Fps);
            _stage.BindDragPreviewScene(_dragPreviewStage);
            return;
        }

        var dx = world.X - _dragPreviewPosition.X;
        var dy = world.Y - _dragPreviewPosition.Y;
        if (Math.Abs(dx) <= 0.0001f && Math.Abs(dy) <= 0.0001f) return;
        _dragPreviewStage.TranslateAllObjectsForPreview(dx, dy);
        _dragPreviewPosition = world;
        _stage.Invalidate();
    }

    private void QueueFillEdgeBezierPointer(PointF world)
    {
        _pendingFillEdgeBezierWorld = world;
        if (_lineDragPreviewTimer.Enabled) return;
        ApplyPendingFillEdgeBezierPreview();
        _lineDragPreviewTimer.Start();
    }

    private void QueueDrawingTransformPointer(PointF world)
    {
        _pendingDrawingTransformWorld = world;
        if (_lineDragPreviewTimer.Enabled) return;
        ApplyPendingDrawingTransformPreview();
        _lineDragPreviewTimer.Start();
    }

    private void QueueHoverFeedback(Point screen)
    {
        _pendingHoverScreen = screen;
        if (_lineDragPreviewTimer.Enabled) return;
        ApplyPendingHoverFeedback();
        _lineDragPreviewTimer.Start();
    }

    private void QueueFreehandPreview()
    {
        if (!_freehandDrawing) return;
        _pendingFreehandPreview = true;
        if (_lineDragPreviewTimer.Enabled) return;
        ApplyPendingFreehandPreview();
        _lineDragPreviewTimer.Start();
    }

    private void TickLineDragPreview()
    {
        var applied = ApplyPendingLineDragPreview();
        applied |= ApplyPendingFillEdgeBezierPreview();
        applied |= ApplyPendingDrawingTransformPreview();
        applied |= ApplyPendingSceneLightGizmoPointer();
        applied |= ApplyPendingSpatialTransformPointer();
        applied |= ApplyPendingHoverFeedback();
        applied |= ApplyPendingFreehandPreview();
        if (!applied) _lineDragPreviewTimer.Stop();
    }

    private void StageFrameRendered(object? sender, EventArgs e)
    {
        if (_playing && _playbackWarmupPending)
        {
            // Start the playback clock after the first frame has actually
            // reached the screen, so its asynchronous preparation and paint
            // latency cannot create a catch-up jump on the next tick.
            _playbackWarmupPending = false;
            _playbackAccumulator = 0;
            _clock.Restart();
        }
        // Render FPS measures completed presentations, including repeated playback frames.
        _rendersThisSample++;
        PostArmedFillEdgeBezierPreparation();
        PostPendingFrameCoalescedWork();
        PostDeferredPresentationRefresh();
    }

    private void AdvanceFrameWorkGeneration()
    {
        unchecked
        {
            _frameWorkGeneration++;
        }
    }

    private void PostPendingFrameCoalescedWork()
    {
        if (!HasPendingFrameCoalescedWork()
            || _postedFrameWorkGeneration == _frameWorkGeneration
            || IsDisposed
            || Disposing
            || !IsHandleCreated)
        {
            return;
        }

        var generation = _frameWorkGeneration;
        _postedFrameWorkGeneration = generation;
        try
        {
            BeginInvoke(() => ApplyPostedFrameCoalescedWork(generation));
        }
        catch (InvalidOperationException)
        {
            if (_postedFrameWorkGeneration == generation) _postedFrameWorkGeneration = -1;
        }
    }

    private void ApplyPostedFrameCoalescedWork(int generation)
    {
        if (_postedFrameWorkGeneration == generation) _postedFrameWorkGeneration = -1;
        if (IsDisposed
            || Disposing
            || generation != _frameWorkGeneration
            || !HasPendingFrameCoalescedWork())
        {
            return;
        }

        TickLineDragPreview();
    }

    private void QueueDeferredPresentationRefresh(
        DeferredPresentationRefresh refreshes,
        bool refreshLineEndpointStyles = true)
    {
        if (refreshes == DeferredPresentationRefresh.None) return;
        ClearDeferredPresentationOverlays(refreshes);
        _pendingPresentationRefresh |= refreshes;
        if ((refreshes & DeferredPresentationRefresh.Inspector) != 0)
        {
            _pendingInspectorRefreshLineEndpointStyles |= refreshLineEndpointStyles;
        }
        unchecked
        {
            _presentationRefreshGeneration++;
        }
        _stage.InvalidateOverlay();
    }

    private void ClearDeferredPresentationOverlays(DeferredPresentationRefresh refreshes)
    {
        if ((refreshes & DeferredPresentationRefresh.PenOverlay) != 0)
        {
            _stage.SetPenPathHandlesVisible(false);
        }
        if ((refreshes & DeferredPresentationRefresh.TransformOverlay) != 0)
        {
            _stage.SetTransformOverlay(
                transformMode: _tool == ToolMode.Transform,
                bounds: RectangleF.Empty);
            _stage.SetDistortOverlay(
                distortMode: _tool == ToolMode.Distort,
                envelope: default);
        }
        if ((refreshes & DeferredPresentationRefresh.GradientOverlay) != 0)
        {
            _stage.ClearGradientOverlay();
        }
        if ((refreshes & DeferredPresentationRefresh.FillEdgeBezierOverlay) != 0)
        {
            ClearFillEdgeBezierOverlayState();
        }
    }

    private void PostDeferredPresentationRefresh()
    {
        if (_pendingPresentationRefresh == DeferredPresentationRefresh.None
            || _postedPresentationRefreshGeneration == _presentationRefreshGeneration
            || IsDisposed
            || Disposing
            || !IsHandleCreated)
        {
            return;
        }
        if (!ShouldPostDeferredPresentationRefresh(
                _stage.Capture,
                _lastMouse is not null,
                _undoCapturedForPointerEdit))
        {
            return;
        }

        var generation = _presentationRefreshGeneration;
        _postedPresentationRefreshGeneration = generation;
        try
        {
            BeginInvoke(() => ApplyDeferredPresentationRefresh(generation));
        }
        catch (InvalidOperationException)
        {
            if (_postedPresentationRefreshGeneration == generation)
            {
                _postedPresentationRefreshGeneration = -1;
            }
        }
    }

    internal static bool ShouldPostDeferredPresentationRefresh(
        bool stageHasCapture,
        bool pointerActive,
        bool firstMutationPrepared)
    {
        return !stageHasCapture || !pointerActive;
    }

    private void ApplyDeferredPresentationRefresh(int generation)
    {
        if (_postedPresentationRefreshGeneration == generation)
        {
            _postedPresentationRefreshGeneration = -1;
        }
        if (IsDisposed || Disposing || generation != _presentationRefreshGeneration) return;

        var refreshes = _pendingPresentationRefresh;
        var refreshLineEndpointStyles = _pendingInspectorRefreshLineEndpointStyles;
        _pendingPresentationRefresh = DeferredPresentationRefresh.None;
        _pendingInspectorRefreshLineEndpointStyles = false;
        ApplyDeferredPresentationOverlays(refreshes);
        if ((refreshes & DeferredPresentationRefresh.Hierarchy) != 0)
        {
            _hierarchyPanel.RefreshScene();
        }
        if ((refreshes & DeferredPresentationRefresh.Inspector) != 0)
        {
            UpdateInspector(refreshLineEndpointStyles);
        }
        if ((refreshes & DeferredPresentationRefresh.Overlays) != 0)
        {
            RefreshInteractionCursorAtPointer();
        }
    }

    private void FlushDeferredPresentationOverlaysForPointerHitTest()
    {
        var refreshes = _pendingPresentationRefresh & DeferredPresentationRefresh.Overlays;
        if (refreshes == DeferredPresentationRefresh.None) return;

        _pendingPresentationRefresh &= ~refreshes;
        ApplyDeferredPresentationOverlays(refreshes);
        RefreshInteractionCursorAtPointer();
    }

    private void ApplyDeferredPresentationOverlays(DeferredPresentationRefresh refreshes)
    {
        if ((refreshes & DeferredPresentationRefresh.PenOverlay) != 0)
        {
            UpdateTraditionalPenPathHandleOverlay();
        }
        if ((refreshes & DeferredPresentationRefresh.TransformOverlay) != 0)
        {
            UpdateTransformOverlay();
        }
        if ((refreshes & DeferredPresentationRefresh.GradientOverlay) != 0)
        {
            UpdateGradientOverlay();
        }
        if ((refreshes & DeferredPresentationRefresh.FillEdgeBezierOverlay) != 0)
        {
            UpdateFillEdgeBezierOverlay();
        }
    }

    private void CancelDeferredPresentationRefresh()
    {
        if (_pendingPresentationRefresh == DeferredPresentationRefresh.None
            && _postedPresentationRefreshGeneration < 0)
        {
            return;
        }

        _pendingPresentationRefresh = DeferredPresentationRefresh.None;
        _pendingInspectorRefreshLineEndpointStyles = false;
        unchecked
        {
            _presentationRefreshGeneration++;
        }
        _postedPresentationRefreshGeneration = -1;
    }

    private void ConsumeDeferredPresentationRefresh(DeferredPresentationRefresh refreshes)
    {
        _pendingPresentationRefresh &= ~refreshes;
        if ((refreshes & DeferredPresentationRefresh.Inspector) != 0)
        {
            _pendingInspectorRefreshLineEndpointStyles = false;
        }
    }

    private bool HasPendingFrameCoalescedWork()
    {
        return _pendingLineDragWorld is not null
            || _pendingFillEdgeBezierWorld is not null
            || _pendingDrawingTransformWorld is not null
            || _pendingSceneLightGizmoScreen is not null
            || _pendingSpatialTransformScreen is not null
            || _pendingHoverScreen is not null
            || _pendingFreehandPreview;
    }

    private bool ApplyPendingFillEdgeBezierPreview()
    {
        if (_pendingFillEdgeBezierWorld is not { } world) return false;
        _pendingFillEdgeBezierWorld = null;
        UpdateFillEdgeBezierPointer(world);
        return true;
    }

    private bool ApplyPendingDrawingTransformPreview()
    {
        if (_pendingDrawingTransformWorld is not { } world) return false;
        _pendingDrawingTransformWorld = null;
        if (_tool == ToolMode.Distort) ApplyDistortFromPointer(world);
        else ApplyTransformFromPointer(world);
        return true;
    }

    private bool ApplyPendingHoverFeedback()
    {
        if (_pendingHoverScreen is not { } screen) return false;
        _pendingHoverScreen = null;
        UpdateHoveredLineControls(screen);
        UpdateInteractionCursor(screen);
        return true;
    }

    private bool ApplyPendingFreehandPreview()
    {
        if (!_pendingFreehandPreview) return false;
        _pendingFreehandPreview = false;
        if (!_freehandDrawing) return false;
        UpdateFreehandPreview();
        return true;
    }

    private void FlushFillEdgeBezierPreview()
    {
        ApplyPendingFillEdgeBezierPreview();
        if (!HasPendingFrameCoalescedWork()) _lineDragPreviewTimer.Stop();
    }

    private void ClearDrawingObjectDragPreview()
    {
        if (string.IsNullOrEmpty(_dragPreviewDrawingObjectId) && _stage.DragPreviewScene is null) return;
        _dragPreviewDrawingObjectId = "";
        _dragPreviewPosition = PointF.Empty;
        _stage.BindDragPreviewScene(null);
    }

    private void ClearImportedObjectDragPreview()
    {
        if (_dragPreviewImportedScene is null) return;
        _dragPreviewImportedKey = "";
        _dragPreviewImportedScene = null;
        _dragPreviewPosition = PointF.Empty;
        _stage.BindDragPreviewScene(null);
    }

    private void StageDragDrop(object? sender, DragEventArgs e)
    {
        if (IsShotDirectorContext())
        {
            ClearDrawingObjectDragPreview();
            ClearImportedObjectDragPreview();
            e.Effect = DragDropEffects.None;
            return;
        }

        if (TryResolveDroppedExternalSvgAsset(e.Data, out var externalSvgAsset, out _))
        {
            var dropPosition = DragEventWorldPosition(e);
            ClearDrawingObjectDragPreview();
            ClearImportedObjectDragPreview();
            UseExternalSvgAssetLink(externalSvgAsset.Id, dropPosition);
            return;
        }
        if (TryResolveDroppedImageAsset(e.Data, out var imageAsset, out _))
        {
            var dropPosition = DragEventWorldPosition(e);
            ClearDrawingObjectDragPreview();
            ClearImportedObjectDragPreview();
            PlaceImageAsset(imageAsset.Id, dropPosition);
            return;
        }
        if (TryResolveDroppedImageFile(e.Data, out var imageFile, out var knownImageAsset))
        {
            var dropPosition = DragEventWorldPosition(e);
            ClearDrawingObjectDragPreview();
            ClearImportedObjectDragPreview();
            PlaceDroppedImageFile(imageFile, knownImageAsset, dropPosition);
            return;
        }
        if (TryResolveDroppedSvgFile(e.Data, out var svgFile))
        {
            var dropPosition = DragEventWorldPosition(e);
            ClearDrawingObjectDragPreview();
            ClearImportedObjectDragPreview();
            ImportSvgFile(svgFile, dropPosition);
            return;
        }

        ClearImportedObjectDragPreview();
        if (!TryResolveDroppedDrawingObject(e.Data, out var drawingObject))
        {
            ClearDrawingObjectDragPreview();
            return;
        }
        ClearDrawingObjectDragPreview();
        var client = _stage.PointToClient(new Point(e.X, e.Y));
        if (!TryGetSceneBasePlanePoint(client, out var world)) return;

        if (IsSceneBuildingContext())
        {
            if (IsSceneMaskEditing()) return;
            var scene = ActiveScene();
            if (scene is null) return;
            if (!_project.TryAddSceneInstance(
                    scene.Id,
                    drawingObject.Id,
                    world,
                    0,
                    out _))
            {
                return;
            }

            _timeline.RefreshTimeline();
            ApplyBoundTimelineDuration();
            RebuildSceneComposition();
            _sceneEditorPanel.BindProject(_scenes, _activeSceneIndex, _drawingObjects);
            AppLog.Info($"Placed symbol in scene: {drawingObject.Name} -> {scene.Name}");
            return;
        }

        var container = ActiveDrawingObject();
        if (container is null) return;
        var targetLayerId = container.Scene.ResolveInstanceLayerId(null);
        if (!_project.TryAddDrawingObjectInstance(
                container.Id,
                drawingObject.Id,
                world,
                targetLayerId,
                out var nestedInstance)
            || nestedInstance is null)
        {
            ModernMessageDialog.Show(
                this,
                UiLocalization.T("A symbol cannot contain itself or create a recursive containment cycle."),
                UiLocalization.T("Symbol"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        RebuildDrawingObjectUnderlay();
        SetSceneInstanceSelection(nestedInstance);
        _sceneEditorPanel.BindProject(_scenes, _activeSceneIndex, _drawingObjects);
        UpdateInspector();
        _stage.Invalidate();
        var targetLayer = Array.IndexOf(container.Scene.LayerIds, nestedInstance.SceneLayerId);
        var targetLayerName = targetLayer >= 0 ? container.Scene.LayerNames[targetLayer] : nestedInstance.SceneLayerId;
        AppLog.Info($"Placed nested symbol: {drawingObject.Name} -> {container.Name} / {targetLayerName}");
    }

    internal static bool TryResolveDroppedSvgFile(IDataObject? data, out string fileName)
    {
        fileName = "";
        if (data?.GetDataPresent(DataFormats.FileDrop) != true
            || data.GetData(DataFormats.FileDrop) is not string[] files
            || files.Length != 1
            || string.IsNullOrWhiteSpace(files[0])
            || !string.Equals(Path.GetExtension(files[0]), ".svg", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        fileName = files[0];
        return true;
    }

    private bool TryResolveDroppedDrawingObject(IDataObject? data, out DrawingObjectDefinition drawingObject)
    {
        drawingObject = null!;
        string? drawingObjectId = null;
        if (data?.GetData(typeof(DrawingObjectDragData)) is DrawingObjectDragData reference
            && string.Equals(reference.ProjectId, _project.Id, StringComparison.Ordinal))
        {
            drawingObjectId = reference.DrawingObjectId;
        }
        else if (data?.GetData(typeof(VaultItem)) is VaultItem item
            && string.Equals(item.ReferenceKind, "DrawingObject", StringComparison.Ordinal))
        {
            drawingObjectId = item.ReferenceId;
        }
        else if (data?.GetData(typeof(VaultItem)) is VaultItem legacyItem)
        {
            const string prefix = "DrawingObjectId:";
            drawingObjectId = legacyItem.Payload
                .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..]
                .Trim();
        }

        drawingObject = _drawingObjects.FirstOrDefault(item => item.Id == drawingObjectId)!;
        return drawingObject is not null;
    }

}
