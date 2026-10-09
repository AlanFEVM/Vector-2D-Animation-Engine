using System.Diagnostics;

namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private void MoveSelectedFromPointer(
        PointF world,
        bool synchronizeLinkedFills = true,
        bool resetBezierCurvature = false)
    {
        if (_selectedObject < 0 || _startWorld is null || _selectedStart is null) return;
        var firstMove = !_undoCapturedForPointerEdit;
        var firstMoveStartedAt = firstMove ? Stopwatch.GetTimestamp() : 0;
        var firstMovePreviewAttempted = false;
        var firstMovePreviewPresented = false;
        if (ShouldPresentSelectionDragPreview(firstMove, _activeHandle, _selectedElements.Count))
        {
            firstMovePreviewAttempted = true;
            _stage.BeginDragFirstMoveTelemetry(firstMoveStartedAt);
            firstMovePreviewPresented = _stage.PresentSelectionDragPreview(new PointF(
                world.X - _startWorld.Value.X,
                world.Y - _startWorld.Value.Y));
        }
        var snapshotMilliseconds = 0d;
        var materializeMilliseconds = 0d;
        var modelFrameReady = false;
        try
        {
            var snapshotStartedAt = firstMove ? Stopwatch.GetTimestamp() : 0;
            CapturePointerUndoSnapshot(
                allowSharedGeometry: _activeHandle == EditHandleKind.None
                    && _marqueeMaterializationSession is null);
            snapshotMilliseconds = firstMove
                ? Stopwatch.GetElapsedTime(snapshotStartedAt).TotalMilliseconds
                : 0;
            if (_activeHandle != EditHandleKind.None)
            {
                var editsWholePenLine = _tool == ToolMode.Pen && _traditionalPenAnchorEditing;
                var editsFreehandBezier = _freehandBezierEditStart is not null
                    && _freehandBezierEditObject == _selectedObject
                    && _freehandBezierEditSegment >= 0;
                if (!editsWholePenLine
                    && !editsFreehandBezier
                    && !EnsureSelectedElementDetachedForMove(out materializeMilliseconds))
                {
                    return;
                }
                if (!editsFreehandBezier && !_independentMarqueeStrokeMove) CaptureFillBoundaryLineLinks();
                if (!editsFreehandBezier && !_independentMarqueeStrokeMove) CaptureLineEndpointFillIntersections();
                ApplyHandleDrag(world, resetBezierCurvature);
                if (!editsFreehandBezier
                    && synchronizeLinkedFills
                    && !_independentMarqueeStrokeMove)
                {
                    SynchronizeLinkedFillBoundaries();
                }
            }
            else
            {
                if (!EnsureSelectedElementDetachedForMove(out materializeMilliseconds))
                {
                    return;
                }
                if (!_independentMarqueeStrokeMove) CaptureFillBoundaryLineLinks();
                var dxWorld = world.X - _startWorld.Value.X;
                var dyWorld = world.Y - _startWorld.Value.Y;
                // A placement and its distortion must move together from the same
                // pointer-down baseline. Moving only X/Y leaves visible pixels outside
                // the persisted source envelope, where inverse hit testing cannot find them.
                if (_drawingTransformSession is null
                    && _selectedMoveStarts.Keys.Any(index => _scene.TryGetObjectDistortionsView(index, out _)))
                {
                    _drawingTransformSession = _scene.BeginTransformSession(_selectedMoveStarts.Keys);
                }
                if (_drawingTransformSession is { } translationSession)
                {
                    _scene.ApplyTranslationSessionForPreview(translationSession, dxWorld, dyWorld,
                        synchronizeLinkedFills: false);
                }
                else if (_selectedMoveStarts.Count > 1)
                {
                    foreach (var item in _selectedMoveStarts)
                    {
                        var index = item.Key;
                        if ((uint)index >= _scene.ObjectCount) continue;
                        _scene.X[index] = VectorUnits.Quantize(item.Value.X + dxWorld);
                        _scene.Y[index] = VectorUnits.Quantize(item.Value.Y + dyWorld);
                        if (_selectedCurveStarts.TryGetValue(index, out var curveStart))
                        {
                            _scene.CurveControlX[index] = VectorUnits.Quantize(curveStart.X + dxWorld);
                            _scene.CurveControlY[index] = VectorUnits.Quantize(curveStart.Y + dyWorld);
                        }
                        if (_selectedCurve2Starts.TryGetValue(index, out var curve2Start))
                        {
                            _scene.CurveControl2X[index] = VectorUnits.Quantize(curve2Start.X + dxWorld);
                            _scene.CurveControl2Y[index] = VectorUnits.Quantize(curve2Start.Y + dyWorld);
                        }
                        TranslateGradientFromEditStart(index, dxWorld, dyWorld);
                    }
                }
                else
                {
                    _scene.X[_selectedObject] = VectorUnits.Quantize(_selectedStart.Value.X + dxWorld);
                    _scene.Y[_selectedObject] = VectorUnits.Quantize(_selectedStart.Value.Y + dyWorld);
                    // Translate the raw cubic control points by the same delta so the
                    // curve keeps its exact shape. The quadratic-handle capture
                    // (_curveControlStart) is only for handle drags — writing it back
                    // here rewrote the stored controls and warped the arc every move.
                    if (_selectedCurveStarts.TryGetValue(_selectedObject, out var curveStart))
                    {
                        _scene.CurveControlX[_selectedObject] = VectorUnits.Quantize(curveStart.X + dxWorld);
                        _scene.CurveControlY[_selectedObject] = VectorUnits.Quantize(curveStart.Y + dyWorld);
                    }
                    if (_selectedCurve2Starts.TryGetValue(_selectedObject, out var curve2Start))
                    {
                        _scene.CurveControl2X[_selectedObject] = VectorUnits.Quantize(curve2Start.X + dxWorld);
                        _scene.CurveControl2Y[_selectedObject] = VectorUnits.Quantize(curve2Start.Y + dyWorld);
                    }
                    TranslateGradientFromEditStart(_selectedObject, dxWorld, dyWorld);
                }

                if (synchronizeLinkedFills && !_independentMarqueeStrokeMove) SynchronizeLinkedFillBoundaries();
            }

            if (_activeHandle == EditHandleKind.None) InvalidatePointerTopologyQueries();
            else _scene.InvalidateDeferredTopologyQueries();
            _geometryDirty = true;
            if (!synchronizeLinkedFills && _fillBoundaryLineLinks.Count > 0) _linkedFillBoundaryPreviewDirty = true;
            var deferredRefreshes = _pendingPresentationRefresh;
            if ((deferredRefreshes & DeferredPresentationRefresh.GradientOverlay) == 0)
            {
                UpdateGradientOverlay();
            }
            if (!UpdateFillEdgeBezierOverlayTranslationForMove()
                && (deferredRefreshes & DeferredPresentationRefresh.FillEdgeBezierOverlay) == 0)
            {
                UpdateFillEdgeBezierOverlay();
            }
            if (firstMove)
            {
                if (firstMovePreviewAttempted && firstMovePreviewPresented)
                {
                    _stage.RecordDragFirstMovePreparationTimings(
                        snapshotMilliseconds,
                        materializeMilliseconds);
                }
                else
                {
                    _stage.BeginDragFirstMoveTelemetry(
                        firstMoveStartedAt,
                        snapshotMilliseconds,
                        materializeMilliseconds);
                }
            }
            if (firstMovePreviewAttempted) _stage.ClearSelectionDragPreview(invalidate: false);
            _stage.Invalidate();
            modelFrameReady = true;
        }
        finally
        {
            if (firstMovePreviewAttempted && !modelFrameReady)
            {
                if (firstMovePreviewPresented)
                {
                    _stage.RecordDragFirstMovePreparationTimings(
                        snapshotMilliseconds,
                        materializeMilliseconds);
                }
                else
                {
                    _stage.BeginDragFirstMoveTelemetry(
                        firstMoveStartedAt,
                        snapshotMilliseconds,
                        materializeMilliseconds);
                }
                _stage.ClearSelectionDragPreview();
            }
        }
    }

    internal static bool ShouldPresentSelectionDragPreview(
        bool firstMove,
        EditHandleKind handle,
        int selectedElementCount)
    {
        return firstMove
            && handle == EditHandleKind.None
            && selectedElementCount > 0;
    }

    private bool UpdateFillEdgeBezierOverlayTranslationForMove()
    {
        if (_activeHandle != EditHandleKind.None
            || _stage.FillEdgeBezierOverlayTargetObject != _selectedObject
            || _fillEdgeBezierOverlayBaseObject != _selectedObject
            || (uint)_selectedObject >= _scene.ObjectCount)
        {
            return false;
        }

        _stage.SetFillEdgeBezierOverlayTranslation(new PointF(
            _scene.X[_selectedObject] - _fillEdgeBezierOverlayBasePosition.X,
            _scene.Y[_selectedObject] - _fillEdgeBezierOverlayBasePosition.Y));
        return true;
    }

    private void InvalidatePointerTopologyQueries()
    {
        if (_pointerTopologyQueriesInvalidated) return;
        _scene.InvalidateDeferredTopologyQueries();
        _pointerTopologyQueriesInvalidated = true;
    }

    private bool EnsureSelectedElementDetachedForMove(out double materializeMilliseconds)
    {
        materializeMilliseconds = 0;
        if (_detachedSelectionForMove) return _selectedElements.Count == 0;
        _detachedSelectionForMove = true;
        if (_selectedElements.Count == 0) return true;
        if (_activeHandle is EditHandleKind.LineStart or EditHandleKind.LineEnd
            && _selectedElements.All(hit => IsWholeLineEndpointEditHit(_scene, hit)))
        {
            SetSelection(_selectedObject);
            return true;
        }

        var primaryElement = _selectedElement;
        var endpoint = PointF.Empty;
        var materializeEndpointNeighbors = _activeHandle is EditHandleKind.LineStart or EditHandleKind.LineEnd
            && primaryElement.IsValid
            && primaryElement.Key.Kind == DrawingElementKind.Stroke
            && (uint)primaryElement.Key.ObjectIndex < _scene.ObjectCount
            && _scene.ShapeKind[primaryElement.Key.ObjectIndex] == ShapeKind.Line
            && TryGetStrokePartEndpoint(
                primaryElement,
                _activeHandle == EditHandleKind.LineStart,
                out endpoint);
        var selectedKeys = _selectedElements.Select(hit => hit.Key).ToHashSet();
        if (materializeEndpointNeighbors)
        {
            foreach (var connected in _scene.GetConnectedStrokeElements(primaryElement, _frame))
            {
                if (connected.Key.Kind != DrawingElementKind.Stroke
                    || (uint)connected.Key.ObjectIndex >= _scene.ObjectCount
                    || _scene.ShapeKind[connected.Key.ObjectIndex] != ShapeKind.Line
                    || !StrokePartTouchesEndpoint(connected, endpoint))
                {
                    continue;
                }

                selectedKeys.Add(connected.Key);
            }
        }

        var selectedParts = selectedKeys.ToArray();
        var materializeStartedAt = Stopwatch.GetTimestamp();
        var materialized = _pointerUndoSnapshot is { } rollbackSnapshot
            ? _scene.MaterializeSelectedParts(selectedParts, _frame, rollbackSnapshot)
            : _scene.MaterializeSelectedParts(selectedParts, _frame);
        materializeMilliseconds = Stopwatch.GetElapsedTime(materializeStartedAt).TotalMilliseconds;
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
            return false;
        }

        var primaryPart = materialized.Parts.FirstOrDefault(part => part.Source == primaryElement.Key);
        var primaryObject = primaryPart.Source.IsValid
            ? primaryPart.Result.ObjectIndex
            : materialized.Parts[^1].Result.ObjectIndex;
        var selectedObjects = materialized.Parts
            .Select(part => part.Result.ObjectIndex)
            .Distinct()
            .Where(index => index != primaryObject)
            .Append(primaryObject)
            .ToArray();
        if (materializeEndpointNeighbors) SetSelection(primaryObject, deferPresentation: true);
        else SetSelection(selectedObjects, deferPresentation: true);
        if (_activeHandle == EditHandleKind.None)
        {
            _stage.SetSelectionFillDragFrontObjects(
                materialized.Parts
                    .Where(part => part.Source.Kind == DrawingElementKind.Fill)
                    .Select(part => part.Result.ObjectIndex),
                invalidate: false);
        }
        CaptureEditStart(_selectedObject);
        _geometryDirty |= materialized.Changed;
        if (materialized.Changed) QueueDeferredPresentationRefresh(DeferredPresentationRefresh.Hierarchy);
        return true;
    }

    internal static bool IsWholeLineEndpointEditHit(VectorScene scene, DrawingElementHit hit)
    {
        return hit.IsValid
            && hit.Key.Kind == DrawingElementKind.Stroke
            && (uint)hit.Key.ObjectIndex < scene.ObjectCount
            && scene.ShapeKind[hit.Key.ObjectIndex] == ShapeKind.Line
            && hit.StartT <= DrawingTopologyRules.UnitIntersectionTolerance
            && hit.EndT >= 1f - DrawingTopologyRules.UnitIntersectionTolerance;
    }

    private void CaptureFillBoundaryLineLinks()
    {
        if (_fillBoundaryLineLinksCaptured) return;
        _fillBoundaryLineLinksCaptured = true;

        var translatedObjects = _activeHandle == EditHandleKind.None
            ? _selectedMoveStarts.Keys.ToHashSet()
            : null;
        var editedLines = _selectedMoveStarts.Keys
            .Concat(_lineEndpointEditStarts.Select(edit => edit.ObjectIndex))
            .Append(_selectedObject)
            .Where(index => (uint)index < _scene.ObjectCount
                && _scene.ShapeKind[index] is ShapeKind.Line or ShapeKind.Freeform)
            .Distinct();
        foreach (var lineObjectIndex in editedLines)
        {
            var links = _scene.CaptureFillBoundaryLineLinks(lineObjectIndex, _frame);
            _fillBoundaryLineLinks.AddRange(translatedObjects is null
                ? links
                : ExcludeTranslatedFillBoundaryLinks(links, translatedObjects));
        }
    }

    internal static FillBoundaryLineLink[] ExcludeTranslatedFillBoundaryLinks(
        IReadOnlyList<FillBoundaryLineLink> links,
        IReadOnlySet<int> translatedObjects)
    {
        return links
            .Where(link => !translatedObjects.Contains(link.FillObjectIndex))
            .ToArray();
    }

    private void SynchronizeLinkedFillBoundaries()
    {
        if (_selectedObject < 0
            || _selectedObject >= _scene.ObjectCount)
        {
            return;
        }

        if (_fillBoundaryLineLinks.Count > 0
            && _scene.UpdateFillBoundaryLineLinks(_fillBoundaryLineLinks, rebuildGeometryIndex: false))
        {
            _geometryDirty = true;
        }
        if (_activeHandle is EditHandleKind.LineStart or EditHandleKind.LineEnd
            && _scene.TryGetLineEndpoint(
                _selectedObject,
                startEndpoint: _activeHandle == EditHandleKind.LineStart,
                out var anchor))
        {
            SynchronizeLineEndpointFillIntersections(anchor);
        }
        _linkedFillBoundaryPreviewDirty = false;
    }

    private void CaptureLineEndpointFillIntersections()
    {
        if (_lineEndpointFillIntersectionsCaptured) return;
        _lineEndpointFillIntersectionsCaptured = true;
        if (_activeHandle is not (EditHandleKind.LineStart or EditHandleKind.LineEnd)
            || (uint)_selectedObject >= _scene.ObjectCount
            || _scene.ShapeKind[_selectedObject] != ShapeKind.Line)
        {
            return;
        }

        var intersection = _scene.CaptureFillIntersectionsAtLineEndpoint(
            _selectedObject,
            startEndpoint: _activeHandle == EditHandleKind.LineStart,
            _frame,
            rebuildGeometryIndex: false,
            excludedFillObjectIndices: _fillBoundaryLineLinks
                .Select(link => link.FillObjectIndex)
                .ToHashSet());
        if (!intersection.HasTargets) return;
        _lineEndpointFillIntersections.Add(intersection);
        _geometryDirty = true;
    }

    private void SynchronizeLineEndpointFillIntersections(PointF anchor)
    {
        foreach (var intersection in _lineEndpointFillIntersections)
        {
            _scene.UpdateSharedBoundaryIntersection(
                intersection,
                anchor,
                rebuildGeometryIndex: false);
        }
    }

    private void SynchronizeLinkedFillBoundariesForPreview()
    {
        if ((_fillBoundaryLineLinks.Count == 0 || !_linkedFillBoundaryPreviewDirty)
            && _lineEndpointFillIntersections.Count == 0)
        {
            return;
        }
        SynchronizeLinkedFillBoundaries();
    }

    private bool TryGetStrokePartEndpoint(DrawingElementHit hit, bool startEndpoint, out PointF endpoint)
    {
        endpoint = PointF.Empty;
        if (!hit.IsValid || hit.Key.Kind != DrawingElementKind.Stroke) return false;
        var points = _scene.GetStrokePartPoints(hit, _frame);
        if (points.Length == 0) return false;
        endpoint = startEndpoint ? points[0] : points[^1];
        return true;
    }

    private bool StrokePartTouchesEndpoint(DrawingElementHit hit, PointF endpoint)
    {
        if (!TryGetStrokePartEndpoint(hit, startEndpoint: true, out var start)
            || !TryGetStrokePartEndpoint(hit, startEndpoint: false, out var end))
        {
            return false;
        }

        return Distance(start, endpoint) <= _stage.ScreenLengthToWorld(EndpointConnectionTolerancePixels)
            || Distance(end, endpoint) <= _stage.ScreenLengthToWorld(EndpointConnectionTolerancePixels);
    }

    private bool TryResetSelectedBezierCurvature(MouseButtons button, EditHandleKind handle)
    {
        if (!IsBezierCurvatureResetGesture(button, IsAltPressed(), handle)
            || _startWorld is not { } pointer)
        {
            return false;
        }

        MoveSelectedFromPointer(pointer, resetBezierCurvature: true);
        FinishPointerInteraction();
        return true;
    }

    private void QueueMoveSelectedFromPointer(PointF world)
    {
        if (ShouldPresentSelectionDragPreview(
                !_undoCapturedForPointerEdit,
                _activeHandle,
                _selectedElements.Count))
        {
            MoveSelectedFromPointer(world);
            return;
        }

        if (!SelectedGeometryNeedsFrameCoalescing())
        {
            MoveSelectedFromPointer(world);
            return;
        }

        _pendingLineDragWorld = world;
        if (_lineDragPreviewTimer.Enabled) return;
        ApplyPendingLineDragPreview();
        _lineDragPreviewTimer.Start();
    }

    private bool ApplyPendingLineDragPreview() => ApplyPendingLineDragPreview(updateLinkedFillPreview: true);

    private bool ApplyPendingLineDragPreview(bool updateLinkedFillPreview)
    {
        if (_pendingLineDragWorld is not { } world) return false;
        _pendingLineDragWorld = null;
        MoveSelectedFromPointer(world, synchronizeLinkedFills: false);
        if (updateLinkedFillPreview) SynchronizeLinkedFillBoundariesForPreview();
        return true;
    }

    private void FlushLineDragPreview()
    {
        _lineDragPreviewTimer.Stop();
        _pendingHoverScreen = null;
        _pendingFreehandPreview = false;
        ApplyPendingLineDragPreview(updateLinkedFillPreview: false);
        ApplyPendingFillEdgeBezierPreview();
        ApplyPendingDrawingTransformPreview();
        if (_linkedFillBoundaryPreviewDirty || _lineEndpointFillIntersections.Count > 0)
        {
            SynchronizeLinkedFillBoundaries();
        }
    }

    private void ResetLineDragPreview()
    {
        _lineDragPreviewTimer.Stop();
        _pendingLineDragWorld = null;
        _pendingFillEdgeBezierWorld = null;
        _pendingDrawingTransformWorld = null;
        _pendingHoverScreen = null;
        _pendingFreehandPreview = false;
        _linkedFillBoundaryPreviewDirty = false;
        _lineEndpointSnapBuckets.Clear();
        _lineEndpointSnapCacheBounds = RectangleF.Empty;
        _lineEndpointSnapBucketSize = 0;
        _lineEndpointSnapCacheLayer = -1;
        AdvanceFrameWorkGeneration();
    }

    private bool SelectedGeometryNeedsFrameCoalescing()
    {
        return _selectedObjects.Count > 0
            && _selectedObjects.Any(index => (uint)index < _scene.ObjectCount);
    }
}
