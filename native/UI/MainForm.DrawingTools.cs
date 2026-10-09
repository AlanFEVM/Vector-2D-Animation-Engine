using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

namespace VectorAnimationEngine;

internal sealed partial class MainForm : Form
{
    private void BeginTraditionalPenPoint(Point screen)
    {
        _stage.SetPenPathHandlesVisible(false);
        var world = _stage.ScreenToWorld(screen);
        var resolution = ResolvePenAnchorPoint(world);
        if (_traditionalPenCurrentAnchor is not { } current)
        {
            _traditionalPenFirstAnchor = resolution.Point;
            _traditionalPenCurrentAnchor = resolution.Point;
            _traditionalPenOutgoingHandle = null;
            BeginTraditionalPenPointerDrag(screen);
            UpdatePenAnchorGuides(resolution);
            UpdateTraditionalPenDrawingPreview(world);
            return;
        }

        if (IsAltPressed()
            && Distance(current, resolution.Point) <= Math.Max(
                DrawingTopologyRules.MinStrokeSegmentUnits,
                _stage.ScreenLengthToWorld(9)))
        {
            _traditionalPenOutgoingHandle = null;
            UpdateTraditionalPenDrawingPreview(world);
            return;
        }

        _traditionalPenClosing = _traditionalPenSegmentCount > 0
            && _traditionalPenFirstAnchor is { } first
            && Distance(world, first) <= Math.Max(
                DrawingTopologyRules.MinStrokeSegmentUnits,
                _stage.ScreenLengthToWorld(10));
        if (_traditionalPenClosing && _traditionalPenFirstAnchor is { } firstAnchor)
        {
            resolution = new PenAnchorSnapResult(firstAnchor, true, false, false);
        }
        else
        {
            resolution = ApplyPenAngleSnap(current, resolution);
            if (IsShiftPressed() && !resolution.ObjectSnapped && !resolution.AlignX && !resolution.AlignY)
            {
                resolution = resolution with
                {
                    Point = ApplyDrawingLineAngleSnap(current, resolution.Point, temporarilySnapAngle: true)
                };
            }
        }

        if (Distance(current, resolution.Point) < DrawingTopologyRules.MinStrokeSegmentUnits)
        {
            _traditionalPenClosing = false;
            return;
        }

        _traditionalPenPendingAnchor = resolution.Point;
        _traditionalPenIncomingHandle = null;
        _traditionalPenPendingOutgoingHandle = null;
        _traditionalPenLockedIncomingHandle = null;
        BeginTraditionalPenPointerDrag(screen);
        UpdatePenAnchorGuides(resolution);
        UpdateTraditionalPenDrawingPreview(world);
    }

    private void BeginTraditionalPenPointerDrag(Point screen)
    {
        _traditionalPenPointerDown = true;
        _traditionalPenPointerDownScreen = screen;
        _stage.Capture = true;
        _lastMouse = screen;
        _startScreen = screen;
        _startWorld = _stage.ScreenToWorld(screen);
    }

    private void UpdateTraditionalPenHandles(Point screen)
    {
        if (!_traditionalPenPointerDown
            || _traditionalPenPointerDownScreen is not { } pointerDown
            || Math.Abs(screen.X - pointerDown.X) + Math.Abs(screen.Y - pointerDown.Y) <= 3)
        {
            return;
        }

        var anchor = _traditionalPenPendingAnchor ?? _traditionalPenCurrentAnchor;
        if (anchor is not { } handleAnchor) return;
        var pointer = VectorUnits.Quantize(SnapDrawingPoint(_stage.ScreenToWorld(screen)));
        if (IsShiftPressed())
        {
            pointer = ApplyDrawingLineAngleSnap(handleAnchor, pointer, temporarilySnapAngle: true);
        }

        if (_traditionalPenPendingAnchor is null)
        {
            _traditionalPenOutgoingHandle = pointer;
            UpdateTraditionalPenDrawingPreview(pointer);
            return;
        }

        var mirrored = VectorUnits.Quantize(new PointF(
            handleAnchor.X * 2 - pointer.X,
            handleAnchor.Y * 2 - pointer.Y));
        if (IsAltPressed())
        {
            _traditionalPenLockedIncomingHandle ??= mirrored;
            _traditionalPenIncomingHandle = _traditionalPenLockedIncomingHandle;
            _traditionalPenPendingOutgoingHandle = pointer;
        }
        else
        {
            _traditionalPenLockedIncomingHandle = null;
            _traditionalPenIncomingHandle = mirrored;
            _traditionalPenPendingOutgoingHandle = pointer;
        }

        UpdateTraditionalPenDrawingPreview(pointer);
    }

    private void FinishTraditionalPenPoint(Point screen)
    {
        if (!_traditionalPenPointerDown) return;
        UpdateTraditionalPenHandles(screen);
        if (_traditionalPenPendingAnchor is not null) CommitTraditionalPenSegment();
    }

    private bool CommitTraditionalPenSegment()
    {
        if (_traditionalPenCurrentAnchor is not { } start
            || _traditionalPenPendingAnchor is not { } end
            || Distance(start, end) < DrawingTopologyRules.MinStrokeSegmentUnits)
        {
            return false;
        }

        var segment = TraditionalPenCubicSegment(
            start,
            _traditionalPenOutgoingHandle,
            _traditionalPenIncomingHandle,
            end);
        CaptureUndoSnapshot(affectedLayers: [_scene.ActiveLayer]);
        var newObject = _scene.AddCubicCurveSegment(
            _scene.ActiveLayer,
            segment.Start,
            segment.Control1,
            segment.Control2,
            segment.End,
            ActiveStrokeUnits(),
            ActiveColor(),
            ActiveStrokeColor(),
            6);
        ClearSelection();
        FinalizeNewTopologyStrokeDrawingOperation(newObject);
        _hierarchyPanel.RefreshScene();
        UpdateInspector();
        _traditionalPenSegmentCount++;
        _traditionalPenCurrentAnchor = end;
        _traditionalPenOutgoingHandle = _traditionalPenPendingOutgoingHandle;
        _traditionalPenPendingAnchor = null;
        _traditionalPenIncomingHandle = null;
        _traditionalPenPendingOutgoingHandle = null;
        _traditionalPenLockedIncomingHandle = null;
        _penAnchorCacheGeometryRevision = -1;
        _stage.Invalidate();

        if (_traditionalPenClosing) CancelTraditionalPenPath();
        return true;
    }

    internal static CubicDrawingPreviewSegment TraditionalPenCubicSegment(
        PointF start,
        PointF? outgoingHandle,
        PointF? incomingHandle,
        PointF end)
    {
        if (outgoingHandle is null && incomingHandle is null)
        {
            return new CubicDrawingPreviewSegment(
                start,
                Lerp(start, end, 1f / 3f),
                Lerp(start, end, 2f / 3f),
                end);
        }

        return new CubicDrawingPreviewSegment(
            start,
            outgoingHandle ?? start,
            incomingHandle ?? end,
            end);
    }

    private static PointF Midpoint(PointF first, PointF second) => new(
        (first.X + second.X) * 0.5f,
        (first.Y + second.Y) * 0.5f);

    private static PointF Lerp(PointF first, PointF second, float amount) => new(
        first.X + (second.X - first.X) * amount,
        first.Y + (second.Y - first.Y) * amount);

    private void UpdateTraditionalPenDrawingPreview(PointF world)
    {
        if (_traditionalPenCurrentAnchor is not { } start)
        {
            _stage.ClearDrawingPreview();
            _stage.ClearPenDirectionHandles();
            UpdatePenAnchorGuides(ResolvePenAnchorPoint(world));
            return;
        }

        if (_traditionalPenPendingAnchor is { } handleAnchor)
        {
            _stage.SetPenDirectionHandles(
                handleAnchor,
                _traditionalPenIncomingHandle,
                _traditionalPenPendingOutgoingHandle);
        }
        else
        {
            _stage.SetPenDirectionHandles(start, incoming: null, _traditionalPenOutgoingHandle);
        }

        var end = _traditionalPenPendingAnchor;
        PenAnchorSnapResult resolution = default;
        if (end is null)
        {
            resolution = ResolvePenAnchorPoint(world);
            if (_traditionalPenSegmentCount > 0
                && _traditionalPenFirstAnchor is { } first
                && Distance(world, first) <= Math.Max(
                    DrawingTopologyRules.MinStrokeSegmentUnits,
                    _stage.ScreenLengthToWorld(10)))
            {
                resolution = new PenAnchorSnapResult(first, true, false, false);
            }
            else
            {
                resolution = ApplyPenAngleSnap(start, resolution);
            }
            end = resolution.Point;
            UpdatePenAnchorGuides(resolution);
        }

        if (Distance(start, end.Value) < DrawingTopologyRules.MinStrokeSegmentUnits)
        {
            _stage.ClearDrawingPreview();
            return;
        }

        var segment = TraditionalPenCubicSegment(
            start,
            _traditionalPenOutgoingHandle,
            _traditionalPenIncomingHandle,
            end.Value);
        var curved = _traditionalPenOutgoingHandle is not null || _traditionalPenIncomingHandle is not null;
        if (curved)
        {
            _stage.SetCubicCurveDrawingPreview(
                segment.Start,
                segment.Control1,
                segment.Control2,
                segment.End,
                ActiveStrokeColor(),
                ActiveStrokeUnits());
        }
        else
        {
            _stage.SetDrawingPreview(start, end.Value, ShapeKind.Line, ActiveStrokeColor(), ActiveStrokeUnits());
        }
    }

    private void UpdateTraditionalPenHover(Point screen)
    {
        var world = _stage.ScreenToWorld(screen);
        var selectedOnly = !IsControlPressed();
        if ((!selectedOnly || HasTraditionalPenPathSelection())
            && TryFindTraditionalPenAnchor(world, selectedOnly, out _, out _, out var anchor))
        {
            _stage.ClearDrawingPreview();
            _stage.SetPenAnchorGuides(anchor, vertical: false, horizontal: false, snapped: true, insertion: false);
            return;
        }

        UpdateTraditionalPenDrawingPreview(world);
    }

    private bool TrySelectTraditionalPenPath(Point screen)
    {
        var hit = _scene.HitTestElement(_stage.ScreenToWorld(screen), _frame, SelectionToleranceWorld());
        var path = TraditionalPenPathElements(_scene, hit, _frame);
        if (path.Length == 0) return false;

        var primary = path.FirstOrDefault(candidate => candidate.Key == hit.Key);
        SetSelection(path, primary.IsValid ? primary : path[0]);
        _stage.ClearDrawingPreview();
        _stage.ClearPenAnchorGuides();
        _stage.ClearPenDirectionHandles();
        UpdateInspector();
        _stage.Invalidate();
        return true;
    }

    internal static DrawingElementHit[] TraditionalPenPathElements(
        VectorScene scene,
        DrawingElementHit seed,
        int frame)
    {
        if (!seed.IsValid
            || seed.Key.Kind != DrawingElementKind.Stroke
            || (uint)seed.Key.ObjectIndex >= scene.ObjectCount
            || scene.ShapeKind[seed.Key.ObjectIndex] != ShapeKind.Line)
        {
            return [];
        }

        return scene.GetConnectedStrokeElements(seed, frame)
            .Where(hit => hit.Key.Kind == DrawingElementKind.Stroke
                && (uint)hit.Key.ObjectIndex < scene.ObjectCount
                && scene.ShapeKind[hit.Key.ObjectIndex] == ShapeKind.Line
                && scene.IsObjectSelectable(hit.Key.ObjectIndex, frame))
            .GroupBy(hit => hit.Key)
            .Select(group => group.First())
            .ToArray();
    }

    private bool HasTraditionalPenPathSelection()
    {
        return _tool == ToolMode.Pen
            && _traditionalPenCurrentAnchor is null
            && _selectedElements.Any(hit => hit.Key.Kind == DrawingElementKind.Stroke
                && (uint)hit.Key.ObjectIndex < _scene.ObjectCount
                && _scene.ShapeKind[hit.Key.ObjectIndex] == ShapeKind.Line);
    }

    private bool TryBeginTraditionalPenAnchorEdit(Point screen)
    {
        return TryBeginTraditionalPenAnchorEdit(screen, selectedOnly: false);
    }

    private bool TryBeginTraditionalPenSelectedHandleEdit(Point screen)
    {
        return HasTraditionalPenPathSelection()
            && TryBeginTraditionalPenAnchorEdit(screen, selectedOnly: true);
    }

    private bool TryBeginTraditionalPenAnchorEdit(Point screen, bool selectedOnly)
    {
        var world = _stage.ScreenToWorld(screen);
        if (!TryFindTraditionalPenAnchor(world, selectedOnly, out var objectIndex, out var startEndpoint, out _))
        {
            if (TryFindTraditionalPenControl(screen, selectedOnly, out objectIndex, out var controlHandle))
            {
                BeginTraditionalPenGeometryEdit(screen, world, objectIndex, controlHandle);
                return true;
            }

            return !selectedOnly && TrySelectTraditionalPenPath(screen);
        }

        BeginTraditionalPenGeometryEdit(
            screen,
            world,
            objectIndex,
            startEndpoint ? EditHandleKind.LineStart : EditHandleKind.LineEnd);
        return true;
    }

    private void BeginTraditionalPenGeometryEdit(
        Point screen,
        PointF world,
        int objectIndex,
        EditHandleKind handle)
    {
        var selectedHit = _selectedElements.FirstOrDefault(hit => hit.Key.ObjectIndex == objectIndex);
        if (selectedHit.IsValid)
        {
            SetSelection(_selectedElements.ToArray(), selectedHit);
        }
        else
        {
            SetSelection(objectIndex);
        }
        _activeHandle = handle;
        _traditionalPenAnchorEditing = true;
        _traditionalPenAnchorEditObject = objectIndex;
        _traditionalPenAnchorEditStartEndpoint = handle == EditHandleKind.LineStart;
        _traditionalPenAnchorEditTracksCurrent = handle is EditHandleKind.LineStart or EditHandleKind.LineEnd
            && _traditionalPenCurrentAnchor is { } current
            && _scene.TryGetLineEndpoint(objectIndex, handle == EditHandleKind.LineStart, out var editAnchor)
            && Distance(current, editAnchor) <= _stage.ScreenLengthToWorld(EndpointConnectionTolerancePixels);
        if (_traditionalPenAnchorEditTracksCurrent)
        {
            _traditionalPenAnchorEditOriginalCurrent = _traditionalPenCurrentAnchor!.Value;
            _traditionalPenAnchorEditOriginalOutgoing = _traditionalPenOutgoingHandle;
        }
        _stage.Capture = true;
        _lastMouse = screen;
        _startScreen = screen;
        _startWorld = world;
        CaptureEditStart(objectIndex);
        UpdateInspector();
        _stage.Invalidate();
    }

    private bool TryFindTraditionalPenControl(
        Point screen,
        bool selectedOnly,
        out int objectIndex,
        out EditHandleKind handle)
    {
        objectIndex = -1;
        handle = EditHandleKind.None;
        var bestDistance = 12f;
        var bestObject = -1;
        var bestHandle = EditHandleKind.None;
        for (var index = 0; index < _scene.ObjectCount; index++)
        {
            if (_scene.ShapeKind[index] != ShapeKind.Line
                || _scene.ObjectLayer[index] != _scene.ActiveLayer
                || !_scene.IsObjectActive(index, _frame)
                || selectedOnly && !_selectedObjects.Contains(index))
            {
                continue;
            }

            if (_scene.IsLineStraight(index)) continue;

            if (_scene.IsQuadraticLine(index))
            {
                if (_scene.TryGetLineQuadraticControl(index, out var quadratic))
                    ConsiderControl(index, quadratic, EditHandleKind.BezierControl);
            }
            else
            {
                ConsiderControl(index, new PointF(_scene.CurveControlX[index], _scene.CurveControlY[index]), EditHandleKind.BezierControl);
                ConsiderControl(index, new PointF(_scene.CurveControl2X[index], _scene.CurveControl2Y[index]), EditHandleKind.BezierControl2);
            }
        }

        objectIndex = bestObject;
        handle = bestHandle;
        return objectIndex >= 0;

        void ConsiderControl(int index, PointF worldControl, EditHandleKind candidate)
        {
            var control = _stage.WorldToScreen(worldControl.X, worldControl.Y);
            var dx = control.X - screen.X;
            var dy = control.Y - screen.Y;
            var distance = MathF.Sqrt(dx * dx + dy * dy);
            if (distance >= bestDistance) return;
            bestDistance = distance;
            bestObject = index;
            bestHandle = candidate;
        }
    }

    private bool TryFindTraditionalPenAnchor(
        PointF world,
        bool selectedOnly,
        out int objectIndex,
        out bool startEndpoint,
        out PointF anchor)
    {
        objectIndex = -1;
        startEndpoint = false;
        anchor = PointF.Empty;
        var tolerance = Math.Max(DrawingTopologyRules.MinStrokeSegmentUnits, _stage.ScreenLengthToWorld(10));
        var bestDistance = tolerance;
        for (var index = 0; index < _scene.ObjectCount; index++)
        {
            if (_scene.ShapeKind[index] != ShapeKind.Line
                || _scene.ObjectLayer[index] != _scene.ActiveLayer
                || !_scene.IsObjectActive(index, _frame)
                || selectedOnly && !_selectedObjects.Contains(index))
            {
                continue;
            }

            if (_scene.TryGetLineEndpoint(index, startEndpoint: true, out var startCandidate))
            {
                var distance = Distance(world, startCandidate);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    objectIndex = index;
                    startEndpoint = true;
                    anchor = startCandidate;
                }
            }

            if (_scene.TryGetLineEndpoint(index, startEndpoint: false, out var endCandidate))
            {
                var distance = Distance(world, endCandidate);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    objectIndex = index;
                    startEndpoint = false;
                    anchor = endCandidate;
                }
            }
        }

        return objectIndex >= 0;
    }

    private void CompleteTraditionalPenAnchorEdit()
    {
        FlushLineDragPreview();
        if (_traditionalPenAnchorEditTracksCurrent
            && (uint)_traditionalPenAnchorEditObject < _scene.ObjectCount
            && _scene.TryGetLineEndpoint(
                _traditionalPenAnchorEditObject,
                _traditionalPenAnchorEditStartEndpoint,
                out var movedAnchor))
        {
            var dx = movedAnchor.X - _traditionalPenAnchorEditOriginalCurrent.X;
            var dy = movedAnchor.Y - _traditionalPenAnchorEditOriginalCurrent.Y;
            _traditionalPenCurrentAnchor = movedAnchor;
            if (_traditionalPenFirstAnchor is { } first
                && Distance(first, _traditionalPenAnchorEditOriginalCurrent) <= _stage.ScreenLengthToWorld(EndpointConnectionTolerancePixels))
            {
                _traditionalPenFirstAnchor = movedAnchor;
            }
            if (_traditionalPenAnchorEditOriginalOutgoing is { } outgoing)
            {
                _traditionalPenOutgoingHandle = VectorUnits.Quantize(new PointF(outgoing.X + dx, outgoing.Y + dy));
            }
        }

        _traditionalPenAnchorEditing = false;
        _traditionalPenAnchorEditTracksCurrent = false;
        _traditionalPenAnchorEditObject = -1;
        _traditionalPenAnchorEditOriginalOutgoing = null;
        _penAnchorCacheGeometryRevision = -1;
        FinishPointerInteraction();
    }

    private void FinishTraditionalPenPointerDrag()
    {
        _traditionalPenPointerDown = false;
        _traditionalPenPointerDownScreen = null;
        _traditionalPenClosing = false;
        _lastMouse = null;
        _startScreen = null;
        _startWorld = null;
        _stage.Capture = false;
    }

    private bool CancelTraditionalPenPath()
    {
        var hadPath = _traditionalPenCurrentAnchor is not null
            || _traditionalPenPendingAnchor is not null
            || _traditionalPenAnchorEditing;
        var hadPointer = _traditionalPenPointerDown;
        var hadAnchorEdit = _traditionalPenAnchorEditing;
        _traditionalPenFirstAnchor = null;
        _traditionalPenCurrentAnchor = null;
        _traditionalPenOutgoingHandle = null;
        _traditionalPenPendingAnchor = null;
        _traditionalPenIncomingHandle = null;
        _traditionalPenPendingOutgoingHandle = null;
        _traditionalPenLockedIncomingHandle = null;
        _traditionalPenPointerDownScreen = null;
        _traditionalPenPointerDown = false;
        _traditionalPenClosing = false;
        _traditionalPenAnchorEditing = false;
        _traditionalPenAnchorEditTracksCurrent = false;
        _traditionalPenAnchorEditObject = -1;
        _traditionalPenAnchorEditOriginalOutgoing = null;
        _traditionalPenSegmentCount = 0;
        _stage.ClearDrawingPreview();
        _stage.ClearPenAnchorGuides();
        _stage.ClearPenDirectionHandles();
        if (hadAnchorEdit)
        {
            FlushLineDragPreview();
            FinishPointerInteraction();
        }
        else if (hadPointer)
        {
            FinishTraditionalPenPointerDrag();
        }
        UpdateTraditionalPenPathHandleOverlay();
        return hadPath;
    }

    private void BeginPenSegment(Point screen)
    {
        var world = _stage.ScreenToWorld(screen);
        var resolution = ResolvePenAnchorPoint(world);
        if (_penStartWorld is null)
        {
            _penStartWorld = resolution.Point;
            UpdatePenAnchorGuides(resolution);
            UpdatePenDrawingPreview(world);
            return;
        }

        resolution = ApplyPenAngleSnap(_penStartWorld.Value, resolution);
        var end = resolution.Point;
        if (Distance(_penStartWorld.Value, end) < DrawingTopologyRules.MinStrokeSegmentUnits) return;
        _penEndWorld = end;
        _penControlWorld = null;
        _penEndpointScreen = screen;
        _penSegmentDragging = true;
        UpdatePenAnchorGuides(resolution);
        _stage.Capture = true;
        _lastMouse = screen;
        UpdatePenDrawingPreview(world);
    }

    private void UpdatePenControl(Point screen)
    {
        if (_penEndpointScreen is not { } anchor) return;
        if (Math.Abs(screen.X - anchor.X) + Math.Abs(screen.Y - anchor.Y) <= 3) return;
        _penControlWorld = VectorUnits.Quantize(SnapDrawingPoint(_stage.ScreenToWorld(screen)));
        UpdatePenDrawingPreview(_penControlWorld.Value);
    }

    private bool CommitPenSegment()
    {
        if (_penStartWorld is not { } start || _penEndWorld is not { } end) return false;
        if (Distance(start, end) < DrawingTopologyRules.MinStrokeSegmentUnits) return false;
        var control = _penControlWorld ?? new PointF((start.X + end.X) * 0.5f, (start.Y + end.Y) * 0.5f);
        CaptureUndoSnapshot(affectedLayers: [_scene.ActiveLayer]);
        var newObject = _scene.AddCurveSegment(
            _scene.ActiveLayer,
            start,
            control,
            end,
            ActiveStrokeUnits(),
            ActiveColor(),
            ActiveStrokeColor(),
            6);
        _scene.QuadraticLineEditing[newObject] = true;
        SetSelection(newObject);
        FinalizeNewTopologyStrokeDrawingOperation(newObject);
        _hierarchyPanel.RefreshScene();
        UpdateInspector();
        _penStartWorld = end;
        _penEndWorld = null;
        _penControlWorld = null;
        _stage.Invalidate();
        return true;
    }

    private void UpdatePenDrawingPreview(PointF world)
    {
        if (_penStartWorld is not { } start)
        {
            _stage.ClearDrawingPreview();
            UpdatePenAnchorGuides(ResolvePenAnchorPoint(world));
            return;
        }

        var previewMaterial = ActiveDrawingPreviewMaterial(ShapeKind.Line);
        if (_penEndWorld is not { } end)
        {
            var resolution = ResolvePenAnchorPoint(world);
            resolution = ApplyPenAngleSnap(start, resolution);
            end = resolution.Point;
            UpdatePenAnchorGuides(resolution);
            _stage.SetDrawingPreview(start, end, ShapeKind.Line, previewMaterial.Color, previewMaterial.Stroke);
            return;
        }

        if (_penControlWorld is { } control)
        {
            _stage.SetCurveDrawingPreview(start, control, end, previewMaterial.Color, previewMaterial.Stroke);
            return;
        }

        _stage.SetDrawingPreview(start, end, ShapeKind.Line, previewMaterial.Color, previewMaterial.Stroke);
    }

    private void FinishPenSegmentPointerDrag()
    {
        _penSegmentDragging = false;
        _penEndpointScreen = null;
        _lastMouse = null;
        _startScreen = null;
        _startWorld = null;
        _stage.Capture = false;
    }

    private bool CancelPenCurve()
    {
        var hadCurve = _penStartWorld is not null || _penEndWorld is not null;
        var hadPointerDrag = _penSegmentDragging;
        _penStartWorld = null;
        _penEndWorld = null;
        _penControlWorld = null;
        _penEndpointScreen = null;
        _penSegmentDragging = false;
        _stage.ClearDrawingPreview();
        _stage.ClearPenAnchorGuides();
        if (hadPointerDrag) FinishPenSegmentPointerDrag();
        return hadCurve;
    }

    private void UpdatePenHover(Point screen)
    {
        var world = _stage.ScreenToWorld(screen);
        if (_penStartWorld is null
            && IsControlPressed()
            && TryGetPenAnchorInsertion(world, out _, out _, out var insertionPoint))
        {
            _stage.ClearDrawingPreview();
            _stage.SetPenAnchorGuides(insertionPoint, vertical: false, horizontal: false, snapped: false, insertion: true);
            return;
        }

        UpdatePenDrawingPreview(world);
    }

    private bool TryInsertPenAnchor(Point screen)
    {
        var world = _stage.ScreenToWorld(screen);
        if (!TryGetPenAnchorInsertion(world, out var objectIndex, out var parameter, out _)) return false;

        var undoSnapshot = CreateCanvasMutationSnapshot([objectIndex]);
        if (!_scene.SplitLineAt(objectIndex, parameter, out var split))
        {
            RestoreCanvasMutationSnapshot(undoSnapshot);
            return false;
        }
        PushUndoSnapshot(undoSnapshot);
        _penAnchorCacheGeometryRevision = -1;
        SetSelection([split.FirstObjectIndex, split.SecondObjectIndex]);
        _hierarchyPanel.RefreshScene();
        UpdateInspector();
        _stage.SetPenAnchorGuides(split.Anchor, vertical: false, horizontal: false, snapped: true, insertion: true);
        _stage.Invalidate();
        return true;
    }

    private bool TryGetPenAnchorInsertion(
        PointF world,
        out int objectIndex,
        out float parameter,
        out PointF insertionPoint)
    {
        objectIndex = -1;
        parameter = 0;
        insertionPoint = PointF.Empty;
        var tolerance = Math.Max(DrawingTopologyRules.MinStrokeSegmentUnits, _stage.ScreenLengthToWorld(9));
        var hit = _scene.HitTestElement(world, _frame, tolerance);
        if (!hit.IsValid
            || hit.Key.Kind != DrawingElementKind.Stroke
            || (uint)hit.Key.ObjectIndex >= _scene.ObjectCount
            || _scene.ShapeKind[hit.Key.ObjectIndex] != ShapeKind.Line
            || _scene.ObjectLayer[hit.Key.ObjectIndex] != _scene.ActiveLayer
            || !_scene.TryGetClosestPointOnLine(hit.Key.ObjectIndex, world, out parameter, out insertionPoint, out var distance)
            || distance > tolerance
            || parameter <= 0.025f
            || parameter >= 0.975f)
        {
            return false;
        }

        objectIndex = hit.Key.ObjectIndex;
        return true;
    }

    private PenAnchorSnapResult ResolvePenAnchorPoint(PointF world)
    {
        var objectSnapping = EndpointSnappingRequested();
        var alignment = _drawSettings.SnapEnabled && _drawSettings.AlignmentEnabled;
        var gridPoint = VectorUnits.Quantize(SnapDrawingPoint(world));
        if (!objectSnapping && !alignment) return new PenAnchorSnapResult(gridPoint, false, false, false);
        return ResolvePenAnchorSnap(
            world,
            PenAnchorCandidates(),
            Math.Max(EndpointConnectionToleranceUnits, _stage.ScreenLengthToWorld(9)),
            gridPoint,
            objectSnapping,
            alignment);
    }

    private PenAnchorSnapResult ApplyPenAngleSnap(PointF start, PenAnchorSnapResult resolution)
    {
        if (resolution.ObjectSnapped || resolution.AlignX || resolution.AlignY) return resolution;
        return resolution with { Point = ApplyDrawingLineAngleSnap(start, resolution.Point, temporarilySnapAngle: false) };
    }

    internal static PenAnchorSnapResult ResolvePenAnchorSnap(
        PointF world,
        IReadOnlyList<PointF> anchors,
        float tolerance,
        PointF gridPoint,
        bool objectSnapping,
        bool alignment)
    {
        tolerance = Math.Max(0, tolerance);
        if (objectSnapping)
        {
            var bestDistance = tolerance;
            PointF? best = null;
            foreach (var anchor in anchors)
            {
                var distance = Distance(world, anchor);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = anchor;
            }

            if (best is { } snapped)
            {
                return new PenAnchorSnapResult(VectorUnits.Quantize(snapped), true, false, false);
            }
        }

        if (!alignment) return new PenAnchorSnapResult(VectorUnits.Quantize(gridPoint), false, false, false);
        var bestXDistance = tolerance;
        var bestYDistance = tolerance;
        var alignedX = gridPoint.X;
        var alignedY = gridPoint.Y;
        var alignX = false;
        var alignY = false;
        foreach (var anchor in anchors)
        {
            var xDistance = Math.Abs(world.X - anchor.X);
            if (xDistance < bestXDistance)
            {
                bestXDistance = xDistance;
                alignedX = anchor.X;
                alignX = true;
            }

            var yDistance = Math.Abs(world.Y - anchor.Y);
            if (yDistance >= bestYDistance) continue;
            bestYDistance = yDistance;
            alignedY = anchor.Y;
            alignY = true;
        }

        return new PenAnchorSnapResult(
            VectorUnits.Quantize(new PointF(alignedX, alignedY)),
            false,
            alignX,
            alignY);
    }

    private IReadOnlyList<PointF> PenAnchorCandidates()
    {
        if (ReferenceEquals(_penAnchorCacheScene, _scene)
            && _penAnchorCacheGeometryRevision == _scene.GeometryRevision
            && _penAnchorCacheLayer == _scene.ActiveLayer
            && _penAnchorCacheFrame == _frame)
        {
            return _penAnchorCache;
        }

        var anchors = new List<PointF>();
        for (var objectIndex = 0; objectIndex < _scene.ObjectCount; objectIndex++)
        {
            if (_scene.ShapeKind[objectIndex] != ShapeKind.Line
                || _scene.ObjectLayer[objectIndex] != _scene.ActiveLayer
                || !_scene.IsObjectActive(objectIndex, _frame))
            {
                continue;
            }

            if (_scene.TryGetLineEndpoint(objectIndex, startEndpoint: true, out var start)) anchors.Add(start);
            if (_scene.TryGetLineEndpoint(objectIndex, startEndpoint: false, out var end)) anchors.Add(end);
        }

        _penAnchorCacheScene = _scene;
        _penAnchorCacheGeometryRevision = _scene.GeometryRevision;
        _penAnchorCacheLayer = _scene.ActiveLayer;
        _penAnchorCacheFrame = _frame;
        _penAnchorCache = anchors.ToArray();
        return _penAnchorCache;
    }

    private void UpdatePenAnchorGuides(PenAnchorSnapResult resolution)
    {
        if (!resolution.ObjectSnapped && !resolution.AlignX && !resolution.AlignY)
        {
            _stage.ClearPenAnchorGuides();
            return;
        }

        _stage.SetPenAnchorGuides(
            resolution.Point,
            vertical: resolution.AlignX,
            horizontal: resolution.AlignY,
            snapped: resolution.ObjectSnapped,
            insertion: false);
    }

    private void BeginFreehandStroke(Point screen, PointF world, float tabletPressure = float.NaN)
    {
        _stage.Focus();
        _freehandSamples.Clear();
        _freehandSamples.Add(VectorUnits.Quantize(world));
        _pressureBrushSamples.Clear();
        _mixingBrushRegionAccumulator = null;
        _freehandLastScreen = screen;
        _freehandDrawing = true;
        _freehandBrushStroke = IsBrushTool(_tool);
        _freehandPressureBrush = _tool == ToolMode.PressureBrush;
        _freehandMixingBrush = _tool == ToolMode.MixingBrush;
        _freehandErasing = _tool == ToolMode.Eraser;
        _freehandPencilSmoothing = _drawSettings.PencilSmoothing;
        _freehandPencilWorldPerPixel = _stage.ScreenLengthToWorld(1);
        _freehandPencilPreviewSampleCount = 0;
        _freehandPencilPreviewUpdatedAt = 0;
        _freehandStartedTimestamp = Stopwatch.GetTimestamp();
        _freehandLastSampleTimestamp = _freehandStartedTimestamp;
        if (_freehandPressureBrush)
        {
            var sample = new PressureBrushSample(
                _freehandSamples[0],
                0,
                0,
                NormalizeTabletPressure(tabletPressure));
            _pressureBrushSamples.Add(sample);
        }
        ClearShapeGradientBrushPreview();
        _brushAreaPreviewSampleCount = 0;
        _brushAreaPressureProfile.Clear();
        _brushAreaPressureDistances.Clear();
        _freehandStrokeUnits = Math.Max(VectorUnits.MinimumStrokeUnits, ActiveStrokeUnits());
        _freehandColor = _freehandErasing
            ? Color.FromArgb(150, 255, 120, 120)
            : _freehandBrushStroke
            ? ActiveColor()
            : ActiveStrokeColor();
        if (_freehandMixingBrush)
        {
            var activeLayer = _scene.ActiveLayer;
            _mixingBrushLayer = activeLayer;
            _mixingBrushFrame = _frame;
            var paintSampler = MixingBrushPaintSampler.CreateActiveLayerSampler(
                _scene,
                _frame,
                activeLayer,
                tileSize: Math.Max(128f, _freehandStrokeUnits * 2f));
            _mixingBrushRegionAccumulator = new MixingBrushRegionAccumulator(
                _freehandColor,
                _drawSettings.MixingBrushSettings,
                _freehandStrokeUnits,
                paintSampler.Sample);
            _mixingBrushRegionAccumulator.Append(_freehandSamples[0]);
        }
        UpdateFreehandPreview();
    }

    private void AppendFreehandSample(
        Point screen,
        bool force = false,
        float tabletPressure = float.NaN,
        bool updatePreview = true)
    {
        if (!_freehandDrawing || _freehandLastScreen is not { } previousScreen || _freehandSamples.Count == 0) return;
        tabletPressure = NormalizeTabletPressure(tabletPressure);
        var sampleTimestamp = Stopwatch.GetTimestamp();
        var dx = screen.X - previousScreen.X;
        var dy = screen.Y - previousScreen.Y;
        var distancePixels = MathF.Sqrt(dx * dx + dy * dy);
        if (!force && distancePixels < FreehandSampleSpacingPixels)
        {
            UpdatePressureBrushHoldTime(sampleTimestamp);
            UpdateLatestTabletPressure(tabletPressure);
            if (updatePreview) QueueFreehandPreview();
            return;
        }

        var start = _freehandSamples[^1];
        var end = _stage.ScreenToWorld(screen);
        var steps = Math.Max(1, (int)MathF.Ceiling(distancePixels / 2f));
        var sampleSeconds = Math.Max(0.001, Stopwatch.GetElapsedTime(_freehandLastSampleTimestamp, sampleTimestamp).TotalSeconds);
        var speed = Math.Clamp((float)(distancePixels / sampleSeconds), 0, 5000);
        var previousHeldSeconds = (float)Stopwatch.GetElapsedTime(_freehandStartedTimestamp, _freehandLastSampleTimestamp).TotalSeconds;
        var currentHeldSeconds = (float)Stopwatch.GetElapsedTime(_freehandStartedTimestamp, sampleTimestamp).TotalSeconds;
        var previousTabletPressure = _pressureBrushSamples.Count > 0
            ? _pressureBrushSamples[^1].TabletPressure
            : float.NaN;
        for (var step = 1; step <= steps; step++)
        {
            var t = step / (float)steps;
            var point = VectorUnits.Quantize(new PointF(
                start.X + (end.X - start.X) * t,
                start.Y + (end.Y - start.Y) * t));
            if (_freehandSamples[^1] == point) continue;
            _freehandSamples.Add(point);
            if (_freehandMixingBrush && _mixingBrushRegionAccumulator is not null)
            {
                _mixingBrushRegionAccumulator.Append(point);
            }
            if (_freehandPressureBrush)
            {
                var heldSeconds = previousHeldSeconds + (currentHeldSeconds - previousHeldSeconds) * t;
                var interpolatedPressure = InterpolateTabletPressure(
                    previousTabletPressure,
                    tabletPressure,
                    t);
                _pressureBrushSamples.Add(new PressureBrushSample(
                    point,
                    speed,
                    heldSeconds,
                    interpolatedPressure));
            }
        }

        _freehandLastScreen = screen;
        _freehandLastSampleTimestamp = sampleTimestamp;
        UpdatePressureBrushHoldTime(sampleTimestamp);
        UpdateLatestTabletPressure(tabletPressure);
        if (updatePreview) QueueFreehandPreview();
    }

    private static float NormalizeTabletPressure(float pressure)
    {
        return float.IsFinite(pressure) ? Math.Clamp(pressure, 0f, 1f) : float.NaN;
    }

    private static float InterpolateTabletPressure(float start, float end, float amount)
    {
        if (float.IsFinite(start) && float.IsFinite(end)) return start + (end - start) * amount;
        return float.IsFinite(end) ? end : float.NaN;
    }

    private void UpdateLatestTabletPressure(float pressure)
    {
        if (!_freehandPressureBrush
            || _pressureBrushSamples.Count == 0
            || !float.IsFinite(pressure))
        {
            return;
        }

        var index = _pressureBrushSamples.Count - 1;
        _pressureBrushSamples[index] = _pressureBrushSamples[index] with { TabletPressure = pressure };
    }

    private void UpdatePressureBrushHoldTime(long timestamp)
    {
        if (!_freehandPressureBrush || _pressureBrushSamples.Count == 0) return;
        var index = _pressureBrushSamples.Count - 1;
        var sample = _pressureBrushSamples[index];
        _pressureBrushSamples[index] = sample with
        {
            HeldSeconds = (float)Stopwatch.GetElapsedTime(_freehandStartedTimestamp, timestamp).TotalSeconds
        };
    }

    private void UpdateFreehandPreview()
    {
        if (_freehandMixingBrush && TryUpdateMixingBrushPreview()) return;

        var tipShape = _freehandBrushStroke || _freehandPressureBrush || _freehandErasing
            ? _brushShape
            : null;
        var previewLimit = tipShape is { IsTraditionalBrush: true, IsRadiallySymmetric: false }
            ? MaxStampBrushPreviewPoints
            : MaxFreehandPreviewPoints;
        if (_freehandPressureBrush)
        {
            var previewSamples = LimitDrawingPreview(_pressureBrushSamples, previewLimit);
            var profile = FreehandStrokeProcessor.CreatePressurePreview(
                previewSamples,
                _freehandStrokeUnits,
                _drawSettings.PressureBrushSmoothing);
            var pressurePreviewPoints = new PointF[profile.Length];
            var previewDiameters = new float[profile.Length];
            for (var index = 0; index < profile.Length; index++)
            {
                pressurePreviewPoints[index] = profile[index].Point;
                previewDiameters[index] = profile[index].Diameter;
            }
            _stage.SetFreehandPreview(
                pressurePreviewPoints,
                _freehandColor,
                _freehandStrokeUnits,
                previewDiameters,
                tipShape);
            return;
        }

        if (_tool == ToolMode.Pencil)
        {
            var now = Stopwatch.GetTimestamp();
            var minimumInterval = PencilPreviewMinimumIntervalMilliseconds(_freehandSamples.Count);
            if (_freehandPencilPreviewSampleCount > 0
                && _freehandSamples.Count > _freehandPencilPreviewSampleCount
                && Stopwatch.GetElapsedTime(_freehandPencilPreviewUpdatedAt, now).TotalMilliseconds < minimumInterval)
            {
                return;
            }
            var pencil = FreehandStrokeProcessor.ProcessPencil(
                _freehandSamples,
                _freehandPencilSmoothing,
                _freehandPencilWorldPerPixel);
            _freehandPencilPreviewSampleCount = _freehandSamples.Count;
            _freehandPencilPreviewUpdatedAt = now;
            _stage.SetFreehandPreview(
                pencil.PreviewPoints,
                _freehandColor,
                _freehandStrokeUnits,
                brushShape: tipShape);
            return;
        }

        IReadOnlyList<PointF> previewPoints = LimitDrawingPreview(_freehandSamples, previewLimit);
        _stage.SetFreehandPreview(previewPoints, _freehandColor, _freehandStrokeUnits, brushShape: tipShape);
    }

    private bool TryUpdateMixingBrushPreview()
    {
        if (_mixingBrushRegionAccumulator is null) return false;
        var now = Stopwatch.GetTimestamp();
        var minimumInterval = MixingBrushPreviewMinimumIntervalMilliseconds(
            _mixingBrushRegionAccumulator.WorkingVertexCount);
        if (IsBrushAreaPreviewBound
            && Stopwatch.GetElapsedTime(_shapeGradientPreviewUpdatedAt, now).TotalMilliseconds
                < minimumInterval)
        {
            return true;
        }
        if (!_mixingBrushRegionAccumulator.TryCreatePreviewRegion(out var region)) return false;

        ResetBrushAreaPreview();
        var previewLayer = (uint)_mixingBrushLayer < _brushAreaPreviewStage.LayerCount
            ? _mixingBrushLayer
            : _scene.ActiveLayer;
        var objectIndex = _brushAreaPreviewStage.AppendMixingBrushPreviewRegion(
            previewLayer,
            region,
            (uint)Math.Max(3, region.Vertices.Length));
        if (objectIndex < 0) return false;

        _shapeGradientPreviewUpdatedAt = now;
        _stage.ClearFreehandPreview();
        _stage.BindDragPreviewScene(_brushAreaPreviewStage);
        return true;
    }

    internal static IReadOnlyList<T> LimitDrawingPreview<T>(IReadOnlyList<T> source, int maximumCount)
    {
        ArgumentNullException.ThrowIfNull(source);
        maximumCount = Math.Max(2, maximumCount);
        if (source.Count <= maximumCount) return source;

        var result = new T[maximumCount];
        var scale = (source.Count - 1d) / (maximumCount - 1d);
        for (var index = 0; index < maximumCount; index++)
        {
            result[index] = source[(int)Math.Round(index * scale)];
        }
        return result;
    }

    internal static int ResolveSimpleFreehandSmoothing(ToolMode tool, int pencilSmoothing)
    {
        return tool switch
        {
            ToolMode.Pencil => Math.Clamp(pencilSmoothing, 0, 100),
            ToolMode.Brush => 64,
            ToolMode.Eraser => 52,
            _ => 0
        };
    }

    private bool TryUpdateBrushAreaPreview()
    {
        if ((!_freehandBrushStroke && !_freehandPressureBrush)
            || _freehandErasing
            || _freehandSamples.Count == 0)
        {
            ClearShapeGradientBrushPreview();
            return false;
        }

        var now = Stopwatch.GetTimestamp();
        if (IsBrushAreaPreviewBound
            && Stopwatch.GetElapsedTime(_shapeGradientPreviewUpdatedAt, now).TotalMilliseconds
                < ShapeGradientPreviewIntervalMilliseconds)
        {
            return true;
        }

        for (var objectIndex = 0; objectIndex < _brushAreaPreviewStage.ObjectCount; objectIndex++)
        {
            _brushAreaPreviewStage.DisableLinearGradient(objectIndex);
        }

        var sourceCount = _freehandPressureBrush ? _pressureBrushSamples.Count : _freehandSamples.Count;
        var extendPreview = sourceCount > _brushAreaPreviewSampleCount || _freehandPressureBrush;
        if (extendPreview)
        {
            int[] additions;
            if (_freehandPressureBrush)
            {
                FreehandStrokeProcessor.UpdatePressurePreviewProfile(
                    _pressureBrushSamples,
                    _freehandStrokeUnits,
                    _brushAreaPressureProfile,
                    _brushAreaPressureDistances);
                if (_brushAreaPressureProfile.Count == 0)
                {
                    ClearShapeGradientBrushPreview();
                    return false;
                }

                var startIndex = _brushAreaPreviewSampleCount <= 0
                    ? 0
                    : Math.Min(_brushAreaPressureProfile.Count - 1, _brushAreaPreviewSampleCount - 1);
                var contextStart = Math.Max(0, startIndex - 3);
                var smoothedTail = FreehandStrokeProcessor.SmoothPressureProfile(
                    _brushAreaPressureProfile.GetRange(
                        contextStart,
                        _brushAreaPressureProfile.Count - contextStart),
                    _drawSettings.PressureBrushSmoothing);
                additions = _brushAreaPreviewStage.AddPressureBrushProfile(
                    _scene.ActiveLayer,
                    smoothedTail.AsSpan(startIndex - contextStart).ToArray(),
                    _freehandColor,
                    _brushShape,
                    (uint)Math.Max(3, _brushAreaPressureProfile.Count - startIndex),
                    _drawSettings.BrushFrequency,
                    _drawSettings.BrushContinuous);
            }
            else
            {
                var startIndex = _brushAreaPreviewSampleCount <= 0
                    ? 0
                    : Math.Min(_freehandSamples.Count - 1, _brushAreaPreviewSampleCount - 1);
                var segment = _freehandSamples.GetRange(startIndex, _freehandSamples.Count - startIndex);
                additions = _brushAreaPreviewStage.AddSoftBrushStroke(
                    _scene.ActiveLayer,
                    segment,
                    _freehandStrokeUnits,
                    _freehandColor,
                    _brushShape,
                    (uint)Math.Max(3, segment.Count),
                    _drawSettings.BrushFrequency,
                    _drawSettings.BrushContinuous);
            }

            if (additions.Length > 0)
            {
                _brushAreaPreviewStage.MergeSameColorFillsAroundNewObjects(
                    additions,
                    connectNearby: false,
                    frame: 0);
                _brushAreaPreviewSampleCount = sourceCount;
            }
        }

        var previewObjects = Enumerable.Range(0, _brushAreaPreviewStage.ObjectCount)
            .Where(index => IsFillShape(_brushAreaPreviewStage.ShapeKind[index]))
            .ToArray();
        if (previewObjects.Length == 0)
        {
            ClearShapeGradientBrushPreview();
            return false;
        }

        IReadOnlyList<PointF> gradientPath = _freehandPressureBrush
            ? _pressureBrushSamples.Select(sample => sample.Point).ToArray()
            : _freehandSamples;
        var gradient = _materialEditor.GetGradientPaintForTarget(strokeTarget: false);
        if (gradient.Kind != GradientKind.ShapeRadial)
        {
            ApplyFillGradientToBrushObjects(
                _brushAreaPreviewStage,
                previewObjects,
                gradientPath,
                gradientPath[0],
                gradientPath[^1]);
            _shapeGradientPreviewUpdatedAt = now;
            _stage.ClearFreehandPreview();
            _stage.BindDragPreviewScene(_brushAreaPreviewStage);
            return true;
        }

        _shapeGradientBrushPreviewStage.RestoreSnapshot(_brushAreaPreviewStage.CreateSnapshot());
        _shapeGradientBrushPreviewStage.EditFrame = 0;
        ApplyFillGradientToBrushObjects(
            _shapeGradientBrushPreviewStage,
            previewObjects,
            gradientPath,
            gradientPath[0],
            gradientPath[^1]);

        var matchingSources = _scene.FindIntersectingMatchingShapeGradientFills(
            _shapeGradientBrushPreviewStage,
            previewObjects,
            _frame,
            maximumResults: 3);
        if (matchingSources.Length == 0 || matchingSources.Length > 2)
        {
            _shapeGradientPreviewUpdatedAt = now;
            _stage.ClearFreehandPreview();
            _stage.BindDragPreviewScene(_shapeGradientBrushPreviewStage);
            return true;
        }

        foreach (var source in matchingSources)
        {
            var contours = _scene.GetObjectBoundaryContours(source);
            var copy = _shapeGradientBrushPreviewStage.AddPathObjectContours(
                _scene.ObjectLayer[source],
                contours,
                0,
                Color.FromArgb(_scene.Argb[source]),
                Color.Transparent,
                Math.Max(3u, _scene.AtomCount[source]));
            if (copy < 0) continue;
            _shapeGradientBrushPreviewStage.SetGradientPaint(
                copy,
                GradientKind.ShapeRadial,
                _scene.GetGradientStops(source),
                _scene.GetGradientStart(source),
                _scene.GetGradientEnd(source));
            _shapeGradientBrushPreviewStage.SetShapeGradientMapping(copy, contours);
        }

        var mergedPreview = _shapeGradientBrushPreviewStage.ApplyFillOverwriteToNewObjects(previewObjects, 0);
        if (mergedPreview.Length == 0)
        {
            _shapeGradientPreviewUpdatedAt = now;
            _stage.ClearFreehandPreview();
            _stage.BindDragPreviewScene(_shapeGradientBrushPreviewStage);
            return true;
        }

        _shapeGradientPreviewUpdatedAt = now;
        _stage.ClearFreehandPreview();
        _stage.BindDragPreviewScene(_shapeGradientBrushPreviewStage, _scene, matchingSources);
        return true;
    }

    private bool IsBrushAreaPreviewBound =>
        ReferenceEquals(_stage.DragPreviewScene, _brushAreaPreviewStage)
        || ReferenceEquals(_stage.DragPreviewScene, _shapeGradientBrushPreviewStage);

    private void ResetBrushAreaPreview()
    {
        ClearShapeGradientBrushPreview();
        _brushAreaPreviewSampleCount = 0;
        _brushAreaPressureProfile.Clear();
        _brushAreaPressureDistances.Clear();
        var layerCount = Math.Max(1, _scene.LayerCount);
        var frameCount = Math.Max(1, _scene.FrameCount);
        _brushAreaPreviewStage.CreateEmpty(layerCount, frameCount);
        _brushAreaPreviewStage.EditFrame = 0;
        _shapeGradientBrushPreviewStage.CreateEmpty(layerCount, frameCount);
        _shapeGradientBrushPreviewStage.EditFrame = 0;
    }

    private void ClearShapeGradientBrushPreview()
    {
        _shapeGradientPreviewUpdatedAt = 0;
        if (IsBrushAreaPreviewBound)
        {
            _stage.BindDragPreviewScene(null);
        }
    }

    private void CommitFreehandStroke()
    {
        if (!_freehandDrawing || _freehandSamples.Count == 0)
        {
            CancelFreehandStroke();
            return;
        }

        if (_freehandBrushStroke)
        {
            _stage.DeferCurrentInteractiveSynchronousPresent();
        }

        if (_freehandPressureBrush)
        {
            UpdatePressureBrushHoldTime(Stopwatch.GetTimestamp());
            var snapshot = CreateCanvasMutationSnapshot(affectedLayers: [_scene.ActiveLayer]);
            var objects = _scene.AddPressureBrushStroke(
                _scene.ActiveLayer,
                _pressureBrushSamples,
                _freehandStrokeUnits,
                _freehandColor,
                _brushShape,
                (uint)Math.Max(3, _pressureBrushSamples.Count),
                _drawSettings.PressureBrushSmoothing,
                _stage.ScreenLengthToWorld(0.9f),
                _drawSettings.BrushFrequency,
                _drawSettings.BrushContinuous);
            if (objects.Length > 0)
            {
                PushUndoSnapshot(snapshot);
                var gradientPath = _pressureBrushSamples.Select(sample => sample.Point).ToArray();
                ApplyFillGradientToBrushObjects(
                    objects,
                    gradientPath,
                    _pressureBrushSamples[0].Point,
                    _pressureBrushSamples[^1].Point);
                NormalizeNewPaintObjects(objects, enforceComplexityBudget: true);
                _materialEditor.RecordRecentFillColor(_freehandColor);
                ClearSelection();
                _hierarchyPanel.RefreshScene();
                UpdateInspector();
            }
            else
            {
                RestoreCanvasMutationSnapshot(snapshot);
            }

            CancelFreehandStroke();
            _stage.Invalidate();
            return;
        }

        if (_freehandMixingBrush)
        {
            if (_mixingBrushRegionAccumulator is not null)
            {
                _mixingBrushRegionAccumulator.Complete();
            }
            if (_mixingBrushRegionAccumulator is null
                || !_mixingBrushRegionAccumulator.TryCreateRegion(out var region))
            {
                CancelFreehandStroke();
                _stage.Invalidate();
                return;
            }

            var targetLayer = (uint)_mixingBrushLayer < _scene.LayerCount
                ? _mixingBrushLayer
                : _scene.ActiveLayer;
            var targetFrame = _mixingBrushFrame >= 0 ? _mixingBrushFrame : _frame;
            var snapshot = CreateCanvasMutationSnapshot(affectedLayers: [targetLayer]);
            var mergeResult = _scene.AddOrMergeMixingBrushRegion(
                targetLayer,
                targetFrame,
                region,
                (uint)Math.Max(3, region.Vertices.Length));
            if (mergeResult.Success)
            {
                PushUndoSnapshot(snapshot);
                _materialEditor.RecordRecentFillColor(_freehandColor);
                ClearSelection();
                _hierarchyPanel.RefreshScene();
                UpdateInspector();
            }
            else
            {
                RestoreCanvasMutationSnapshot(snapshot);
            }

            CancelFreehandStroke();
            _stage.Invalidate();
            return;
        }

        if (_tool == ToolMode.Pencil)
        {
            var pencil = FreehandStrokeProcessor.ProcessPencil(
                _freehandSamples,
                _freehandPencilSmoothing,
                _freehandPencilWorldPerPixel);
            if (!pencil.HasGeometry)
            {
                CancelFreehandStroke();
                return;
            }

            var snapshot = CreateCanvasMutationSnapshot(affectedLayers: [_scene.ActiveLayer]);
            var newObject = _scene.AddFreehandBezierStroke(
                _scene.ActiveLayer,
                pencil.Nodes,
                _freehandStrokeUnits,
                _freehandColor,
                (uint)Math.Max(3, pencil.PreviewPoints.Length));
            if (newObject >= 0)
            {
                PushUndoSnapshot(snapshot);
                SetSelection(newObject);
                FinalizeNewTopologyStrokeDrawingOperation(newObject);
                _hierarchyPanel.RefreshScene();
                UpdateInspector();
            }
            else
            {
                RestoreCanvasMutationSnapshot(snapshot);
            }

            CancelFreehandStroke();
            _stage.Invalidate();
            return;
        }

        var smoothing = ResolveSimpleFreehandSmoothing(_tool, _freehandPencilSmoothing);
        var tolerancePixels = _freehandBrushStroke ? 0.9f : 0.65f;
        var points = FreehandStrokeProcessor.Process(
            _freehandSamples,
            smoothing,
            _stage.ScreenLengthToWorld(tolerancePixels));
        if (points.Length == 0)
        {
            CancelFreehandStroke();
            return;
        }

        if (_freehandErasing)
        {
            EraseAlongBrushStroke(points);
            CancelFreehandStroke();
            _stage.Invalidate();
            return;
        }

        CaptureUndoSnapshot(affectedLayers: [_scene.ActiveLayer]);
        if (_freehandBrushStroke)
        {
            var objects = _scene.AddSoftBrushStroke(
                _scene.ActiveLayer,
                points,
                _freehandStrokeUnits,
                _freehandColor,
                _brushShape,
                (uint)Math.Max(3, points.Length),
                _drawSettings.BrushFrequency,
                _drawSettings.BrushContinuous);
            if (objects.Length > 0)
            {
                ApplyFillGradientToBrushObjects(objects, points, points[0], points[^1]);
                NormalizeNewPaintObjects(objects, enforceComplexityBudget: true);
                _materialEditor.RecordRecentFillColor(_freehandColor);
                ClearSelection();
                _hierarchyPanel.RefreshScene();
                UpdateInspector();
            }
        }
        else
        {
            var newObject = _scene.AddFreehandStroke(
                _scene.ActiveLayer,
                points,
                _freehandStrokeUnits,
                _freehandColor,
                brushStroke: false,
                (uint)Math.Max(3, points.Length));
            if (newObject >= 0)
            {
                SetSelection(newObject);
                _hierarchyPanel.RefreshScene();
                UpdateInspector();
            }
        }

        CancelFreehandStroke();
        _stage.Invalidate();
    }

    private void ApplyFillGradientToBrushObjects(
        IReadOnlyList<int> objects,
        IReadOnlyList<PointF> brushPath,
        PointF brushStart,
        PointF brushEnd)
    {
        ApplyFillGradientToBrushObjects(_scene, objects, brushPath, brushStart, brushEnd);
    }

    private void ApplyFillGradientToBrushObjects(
        VectorScene targetScene,
        IReadOnlyList<int> objects,
        IReadOnlyList<PointF> brushPath,
        PointF brushStart,
        PointF brushEnd)
    {
        var gradient = _materialEditor.GetGradientPaintForTarget(strokeTarget: false);
        if (gradient.Kind == GradientKind.Solid) return;

        var sourceAlpha = Math.Max(1, (int)_freehandColor.A);
        var deltaX = brushEnd.X - brushStart.X;
        var deltaY = brushEnd.Y - brushStart.Y;
        PointF? gradientStart = deltaX * deltaX + deltaY * deltaY > 0.0001f ? brushStart : null;
        PointF? gradientEnd = gradientStart is null ? null : brushEnd;
        PointF[][] shapeMappingContours = [];
        if (gradient.Kind == GradientKind.ShapeRadial)
        {
            var mappingSource = objects.FirstOrDefault(index =>
                (uint)index < targetScene.ObjectCount && IsFillShape(targetScene.ShapeKind[index]), -1);
            if (mappingSource >= 0)
            {
                shapeMappingContours = targetScene.GetObjectBoundaryContours(mappingSource);
                var mappingPoints = shapeMappingContours.SelectMany(contour => contour).ToArray();
                if (mappingPoints.Length > 0)
                {
                    var left = mappingPoints.Min(point => point.X);
                    var right = mappingPoints.Max(point => point.X);
                    var top = mappingPoints.Min(point => point.Y);
                    var bottom = mappingPoints.Max(point => point.Y);
                    var center = VectorUnits.Quantize(new PointF((left + right) * 0.5f, (top + bottom) * 0.5f));
                    gradientStart = center;
                    gradientEnd = GradientPaintUtilities.TryFindShapeBoundaryPoint(
                        shapeMappingContours,
                        center,
                        new PointF(1f, 0f),
                        out var boundary)
                        ? boundary
                        : new PointF(right, center.Y);
                }
            }
        }

        foreach (var objectIndex in objects)
        {
            if ((uint)objectIndex >= targetScene.ObjectCount || !IsFillShape(targetScene.ShapeKind[objectIndex])) continue;
            var layerAlpha = Color.FromArgb(targetScene.Argb[objectIndex]).A;
            var stops = GradientPaintUtilities.ScaleStopAlpha(gradient.Stops, layerAlpha / (float)sourceAlpha);
            targetScene.SetGradientPaint(objectIndex, gradient.Kind, stops, gradientStart, gradientEnd);
            if (gradient.Kind == GradientKind.Linear) targetScene.SetGradientPath(objectIndex, brushPath);
            else targetScene.ClearGradientPath(objectIndex);
            if (gradient.Kind == GradientKind.ShapeRadial && shapeMappingContours.Length > 0)
            {
                targetScene.SetShapeGradientMapping(objectIndex, shapeMappingContours);
            }
        }
    }

    private void EraseAlongBrushStroke(IReadOnlyList<PointF> points)
    {
        if (points.Count == 0) return;
        var snapshot = _scene.CreateSnapshot();
        var autoKeyframe = AutomaticKeyframesEnabledForCurrentScene();
        var previousLastFrame = Math.Max(0, _timeline.Context.FrameCount - 1);
        if (!_scene.EraseWithBrushStroke(
                _frame,
                points,
                _freehandStrokeUnits,
                _brushShape,
                _drawSettings.EraseLines,
                _drawSettings.EraseFills,
                _drawSettings.BrushFrequency,
                _drawSettings.BrushContinuous,
                materializeAutoKeyframes: autoKeyframe)) return;

        ClearSelection();
        _stage.ClearHoveredLineElement();
        PushUndoSnapshot(snapshot);
        if (autoKeyframe) RefreshAutomaticKeyframePresentation(previousLastFrame);
        _hierarchyPanel.RefreshScene();
        UpdateInspector();
    }

    private void CancelFreehandStroke()
    {
        _freehandDrawing = false;
        _freehandBrushStroke = false;
        _freehandPressureBrush = false;
        _freehandMixingBrush = false;
        _freehandErasing = false;
        _freehandPencilSmoothing = 0;
        _freehandPencilWorldPerPixel = 0;
        _freehandPencilPreviewSampleCount = 0;
        _freehandPencilPreviewUpdatedAt = 0;
        _tabletPressurePointerId = null;
        _tabletPressureLastValue = float.NaN;
        _freehandLastScreen = null;
        _freehandStartedTimestamp = 0;
        _freehandLastSampleTimestamp = 0;
        _freehandSamples.Clear();
        _pressureBrushSamples.Clear();
        _mixingBrushRegionAccumulator = null;
        _mixingBrushLayer = -1;
        _mixingBrushFrame = -1;
        _pendingFreehandPreview = false;
        ClearShapeGradientBrushPreview();
        _brushAreaPreviewSampleCount = 0;
        _brushAreaPressureProfile.Clear();
        _brushAreaPressureDistances.Clear();
        _stage.ClearFreehandPreview();
    }

    internal static double PencilPreviewMinimumIntervalMilliseconds(int sampleCount)
    {
        if (sampleCount <= 1024) return 0;
        return Math.Min(160, 16 + (sampleCount - 1024) / 32d);
    }

    internal static double MixingBrushPreviewMinimumIntervalMilliseconds(int workingVertexCount)
    {
        if (workingVertexCount <= 1024) return ShapeGradientPreviewIntervalMilliseconds;
        return Math.Min(160, ShapeGradientPreviewIntervalMilliseconds + (workingVertexCount - 1024) / 128d);
    }

    private float SelectionToleranceWorld() => Math.Max(4, _stage.ScreenLengthToWorld(10));

    internal static bool ShouldMoveMarqueeStrokeIndependently(
        IReadOnlyCollection<int> selectedObjects,
        int primaryObject,
        IReadOnlyCollection<int> independentStrokeObjects)
    {
        if (independentStrokeObjects.Count == 0) return false;
        if (independentStrokeObjects.Contains(primaryObject)) return true;
        return selectedObjects.Any(independentStrokeObjects.Contains);
    }

    private void CaptureEditStart(int objectIndex)
    {
        if (_tool == ToolMode.Select) _drawingTransformSession = null;
        _pointerTopologyQueriesInvalidated = false;
        if (!_independentMarqueeStrokeMove
            && _marqueeMaterializationSession is { } marqueeSession
            && ReferenceEquals(marqueeSession.Scene, _scene))
        {
            _independentMarqueeStrokeMove = ShouldMoveMarqueeStrokeIndependently(
                _selectedObjects,
                objectIndex,
                marqueeSession.IndependentStrokeObjects);
        }
        _selectedStart = new PointF(_scene.X[objectIndex], _scene.Y[objectIndex]);
        if (_scene.IsQuadraticLine(objectIndex)
            && _scene.TryGetLineQuadraticControl(objectIndex, out var lineQuadratic))
        {
            // The quadratic-handle position: consumed by BezierControl handle drags.
            _curveControlStart = lineQuadratic;
            _curveControl2Start = lineQuadratic;
        }
        else
        {
            _curveControlStart = new PointF(_scene.CurveControlX[objectIndex], _scene.CurveControlY[objectIndex]);
            _curveControl2Start = new PointF(_scene.CurveControl2X[objectIndex], _scene.CurveControl2Y[objectIndex]);
        }
        _freehandBezierEditStart = null;
        _freehandBezierEditObject = -1;
        _freehandBezierEditSegment = -1;
        if (_scene.ShapeKind[objectIndex] == ShapeKind.Freeform
            && _selectedElement.IsValid
            && _selectedElement.Key.ObjectIndex == objectIndex
            && _selectedElement.Key.Kind == DrawingElementKind.Stroke
            && _selectedElement.BezierSegmentIndex >= 0
            && _scene.TryGetFreehandBezierSegment(
                objectIndex,
                _selectedElement.BezierSegmentIndex,
                out var freehandSegment))
        {
            _freehandBezierEditStart = freehandSegment;
            _freehandBezierEditObject = objectIndex;
            _freehandBezierEditSegment = _selectedElement.BezierSegmentIndex;
        }
        _resizeStartCenter = _selectedStart;
        _resizeStartSize = new SizeF(_scene.Width[objectIndex], _scene.Height[objectIndex]);
        _resizeStartAngle = _scene.Angle[objectIndex];
        _textAreaResizeStartData = _scene.ShapeKind[objectIndex] == ShapeKind.Text
            && _scene.TryGetTextObjectData(objectIndex, out var textData)
                ? textData
                : null;
        _selectedMoveStarts.Clear();
        _selectedCurveStarts.Clear();
        _selectedCurve2Starts.Clear();
        _lineEndpointEditStarts.Clear();
        _lineEndpointFillIntersections.Clear();
        _lineEndpointFillIntersectionsCaptured = false;
        foreach (var index in _selectedObjects)
        {
            if ((uint)index >= _scene.ObjectCount) continue;
            _selectedMoveStarts[index] = new PointF(_scene.X[index], _scene.Y[index]);
            _selectedCurveStarts[index] = new PointF(_scene.CurveControlX[index], _scene.CurveControlY[index]);
            _selectedCurve2Starts[index] = new PointF(_scene.CurveControl2X[index], _scene.CurveControl2Y[index]);
            if (_scene.HasGradient(index))
            {
                _selectedGradientStarts[index] = (_scene.GetGradientStart(index), _scene.GetGradientEnd(index));
            }
        }

        if (!_selectedMoveStarts.ContainsKey(objectIndex))
        {
            _selectedMoveStarts[objectIndex] = _selectedStart.Value;
            // Move translation must use the raw cubic control points, never the derived
            // quadratic-handle position (which would warp the arc on every move).
            _selectedCurveStarts[objectIndex] = new PointF(_scene.CurveControlX[objectIndex], _scene.CurveControlY[objectIndex]);
            _selectedCurve2Starts[objectIndex] = new PointF(_scene.CurveControl2X[objectIndex], _scene.CurveControl2Y[objectIndex]);
            if (_scene.HasGradient(objectIndex))
            {
                _selectedGradientStarts[objectIndex] = (_scene.GetGradientStart(objectIndex), _scene.GetGradientEnd(objectIndex));
            }
        }

        CaptureLineEndpointEditStart(objectIndex);
    }

    private void ApplyHandleDrag(PointF world, bool resetBezierCurvature = false)
    {
        if (_selectedObject < 0 || _resizeStartCenter is null || _resizeStartSize is null) return;
        if (_startWorld is not { } pointerStart
            || !_scene.TryInverseMapObjectPoint(_selectedObject, pointerStart, out var sourcePointerStart)
            || !_scene.TryInverseMapObjectPoint(_selectedObject, world, out world))
        {
            return;
        }
        if (ApplyFreehandBezierHandleDrag(world, sourcePointerStart, resetBezierCurvature)) return;
        if (resetBezierCurvature
            && _activeHandle is EditHandleKind.BezierControl or EditHandleKind.BezierControl2)
        {
            if (_scene.TryGetLineCubic(
                    _selectedObject,
                    out var start,
                    out _,
                    out _,
                    out var end))
            {
                var reset = ResetBezierCurvature(start, end);
                _scene.CurveControlX[_selectedObject] = reset.Control1.X;
                _scene.CurveControlY[_selectedObject] = reset.Control1.Y;
                _scene.CurveControl2X[_selectedObject] = reset.Control2.X;
                _scene.CurveControl2Y[_selectedObject] = reset.Control2.Y;
            }
            return;
        }

        if (_activeHandle is EditHandleKind.TextAreaLeft or EditHandleKind.TextAreaRight)
        {
            ApplyTextAreaHandleDrag(world);
            return;
        }
        if (world == sourcePointerStart
            && _scene.IsQuadraticLine(_selectedObject)
            && _activeHandle is EditHandleKind.BezierControl or EditHandleKind.BezierControl2
            && _selectedCurveStarts.TryGetValue(_selectedObject, out var originalFirst)
            && _selectedCurve2Starts.TryGetValue(_selectedObject, out var originalSecond))
        {
            _scene.CurveControlX[_selectedObject] = originalFirst.X;
            _scene.CurveControlY[_selectedObject] = originalFirst.Y;
            _scene.CurveControl2X[_selectedObject] = originalSecond.X;
            _scene.CurveControl2Y[_selectedObject] = originalSecond.Y;
            return;
        }
        if (_activeHandle == EditHandleKind.BezierControl)
        {
            if (_curveControlStart is not { } originalControl) return;
            var snapped = world == sourcePointerStart
                ? originalControl
                : VectorUnits.Quantize(SnapDrawingPoint(MoveHandleWithPointer(
                    originalControl, sourcePointerStart, world)));
            if (_scene.IsQuadraticLine(_selectedObject))
            {
                _scene.SetLineQuadraticControl(_selectedObject, snapped);
            }
            else
            {
                _scene.CurveControlX[_selectedObject] = snapped.X;
                _scene.CurveControlY[_selectedObject] = snapped.Y;
            }

            return;
        }

        if (_activeHandle == EditHandleKind.BezierControl2)
        {
            if (_curveControl2Start is not { } originalControl) return;
            var snapped = world == sourcePointerStart
                ? originalControl
                : VectorUnits.Quantize(SnapDrawingPoint(MoveHandleWithPointer(
                    originalControl, sourcePointerStart, world)));
            if (_scene.IsQuadraticLine(_selectedObject))
            {
                _scene.SetLineQuadraticControl(_selectedObject, snapped);
            }
            else
            {
                _scene.CurveControl2X[_selectedObject] = snapped.X;
                _scene.CurveControl2Y[_selectedObject] = snapped.Y;
            }
            return;
        }

        if (_activeHandle is EditHandleKind.LineStart or EditHandleKind.LineEnd)
        {
            ApplyLineEndpointDrag(world, sourcePointerStart);
            return;
        }

        var draggedLocal = WorldToLocalFromEditStart(world);
        var bitmapCornerResize = _tool == ToolMode.Select
            && _scene.ShapeKind[_selectedObject] == ShapeKind.Bitmap
            && (_activeHandle is EditHandleKind.BoundsTopLeft
                or EditHandleKind.BoundsTopRight
                or EditHandleKind.BoundsBottomRight
                or EditHandleKind.BoundsBottomLeft);
        if (bitmapCornerResize)
        {
            // Hit testing allows a small tolerance around the visible handle. Apply the
            // pointer delta to the exact corner so the first drag sample cannot jump.
            var pointerStartLocal = WorldToLocalFromEditStart(sourcePointerStart);
            var originalCorner = BitmapCornerLocal(_activeHandle, _resizeStartSize.Value);
            draggedLocal = MoveHandleWithPointer(originalCorner, pointerStartLocal, draggedLocal);
        }
        var anchor = OppositeCorner(_activeHandle, _resizeStartSize.Value);
        var minSize = VectorUnits.FromPixels(4);
        var width = Math.Max(minSize, Math.Abs(draggedLocal.X - anchor.X));
        var height = Math.Max(minSize, Math.Abs(draggedLocal.Y - anchor.Y));

        // Bitmap placements are whole image objects. Their Select-tool corner handles
        // preserve the source aspect ratio regardless of the Free Transform Shift
        // preference (which applies to the separate Transform tool). Keep the opposite
        // corner fixed and use the dominant drag axis to choose one scale factor.
        if (bitmapCornerResize)
        {
            if (!TryGetBitmapCornerResizeSize(
                    _resizeStartSize.Value,
                    _activeHandle,
                    draggedLocal,
                    minSize,
                    out width,
                    out height))
            {
                return;
            }
            var (signX, signY) = BitmapCornerSigns(_activeHandle);
            var startWidth = Math.Max(1f, _resizeStartSize.Value.Width);
            var startHeight = Math.Max(1f, _resizeStartSize.Value.Height);
            var anchorScaleX = (draggedLocal.X - anchor.X) * signX / startWidth;
            var anchorScaleY = (draggedLocal.Y - anchor.Y) * signY / startHeight;
            var selectedScale = Math.Abs(Math.Abs(anchorScaleX) - 1f)
                >= Math.Abs(Math.Abs(anchorScaleY) - 1f)
                ? anchorScaleX
                : anchorScaleY;
            var orientation = selectedScale < 0f ? -1f : 1f;

            // Rebuild the dragged corner from the constrained size. This keeps the
            // opposite corner fixed even when the pointer moved more on the other axis.
            draggedLocal = new PointF(
                anchor.X + signX * orientation * width,
                anchor.Y + signY * orientation * height);
            var constrainedCenter = new PointF(
                (draggedLocal.X + anchor.X) * 0.5f,
                (draggedLocal.Y + anchor.Y) * 0.5f);
            var constrainedCenterWorld = LocalToWorldFromEditStart(constrainedCenter);
            _scene.X[_selectedObject] = VectorUnits.Quantize(constrainedCenterWorld.X);
            _scene.Y[_selectedObject] = VectorUnits.Quantize(constrainedCenterWorld.Y);
            _scene.Width[_selectedObject] = Math.Max(1, VectorUnits.Quantize(width));
            _scene.Height[_selectedObject] = Math.Max(1, VectorUnits.Quantize(height));
            if (_scene.TryGetBitmapObjectData(_selectedObject, out var bitmapData))
            {
                _scene.TryUpdateBitmapObject(
                    _selectedObject,
                    bitmapData.WithPlacedSize(new SizeF(
                        _scene.Width[_selectedObject],
                        _scene.Height[_selectedObject])),
                    rebuildSpatialIndex: false);
            }
            ResizeGradientFromEditStart(_selectedObject, constrainedCenterWorld, width, height);
            UpdateGradientOverlay();
            return;
        }
        var centerLocal = new PointF((draggedLocal.X + anchor.X) * 0.5f, (draggedLocal.Y + anchor.Y) * 0.5f);
        var centerWorld = LocalToWorldFromEditStart(centerLocal);

        _scene.X[_selectedObject] = VectorUnits.Quantize(centerWorld.X);
        _scene.Y[_selectedObject] = VectorUnits.Quantize(centerWorld.Y);
        _scene.Width[_selectedObject] = Math.Max(1, VectorUnits.Quantize(width));
        _scene.Height[_selectedObject] = Math.Max(1, VectorUnits.Quantize(height));
        ResizeGradientFromEditStart(_selectedObject, centerWorld, width, height);
        UpdateGradientOverlay();
    }

    private bool ApplyFreehandBezierHandleDrag(
        PointF world,
        PointF pointerStart,
        bool resetBezierCurvature)
    {
        if (_freehandBezierEditStart is not { } source
            || _freehandBezierEditObject != _selectedObject
            || _freehandBezierEditSegment < 0)
        {
            return false;
        }

        CubicDrawingPreviewSegment adjusted;
        if (resetBezierCurvature
            && _activeHandle is EditHandleKind.BezierControl or EditHandleKind.BezierControl2)
        {
            adjusted = ResetBezierCurvature(source.Start, source.End);
        }
        else
        {
            var handleStart = _activeHandle switch
            {
                EditHandleKind.LineStart => source.Start,
                EditHandleKind.BezierControl => source.Control1,
                EditHandleKind.BezierControl2 => source.Control2,
                EditHandleKind.LineEnd => source.End,
                _ => PointF.Empty
            };
            if (_activeHandle is not (EditHandleKind.LineStart
                or EditHandleKind.BezierControl
                or EditHandleKind.BezierControl2
                or EditHandleKind.LineEnd))
            {
                return true;
            }

            var handlePoint = VectorUnits.Quantize(SnapDrawingPoint(MoveHandleWithPointer(
                handleStart,
                pointerStart,
                world)));
            adjusted = AdjustFillEdgeBezierHandle(
                source.Start,
                source.Control1,
                source.Control2,
                source.End,
                _activeHandle,
                handlePoint);
        }

        _scene.SetFreehandBezierSegmentForPreview(
            _freehandBezierEditObject,
            _freehandBezierEditSegment,
            adjusted.Start,
            adjusted.Control1,
            adjusted.Control2,
            adjusted.End,
            rebuildGeometryIndex: false);
        return true;
    }

    private void ApplyTextAreaHandleDrag(PointF world)
    {
        if (_textAreaResizeStartData is null
            || _resizeStartCenter is not { } startCenter
            || _resizeStartSize is not { } startSize
            || (uint)_selectedObject >= _scene.ObjectCount
            || _scene.ShapeKind[_selectedObject] != ShapeKind.Text)
        {
            return;
        }

        TextAreaResizeResult resized;
        try
        {
            resized = TextGeometry.ResizeLayoutWidth(
                _textAreaResizeStartData,
                startCenter,
                startSize,
                _resizeStartAngle,
                resizeLeftEdge: _activeHandle == EditHandleKind.TextAreaLeft,
                pointerWorld: world,
                minimumDisplayWidth: Math.Max(1f, _stage.ScreenLengthToWorld(36)));
        }
        catch (ArgumentException)
        {
            return;
        }
        catch (InvalidOperationException)
        {
            return;
        }
        catch (OverflowException)
        {
            return;
        }

        if (!_scene.UpdateTextObjectData(_selectedObject, resized.Data)) return;
        _scene.X[_selectedObject] = VectorUnits.Quantize(resized.Center.X);
        _scene.Y[_selectedObject] = VectorUnits.Quantize(resized.Center.Y);
        _scene.Width[_selectedObject] = Math.Max(1, VectorUnits.Quantize(resized.DisplaySize.Width));
        _scene.Height[_selectedObject] = Math.Max(1, VectorUnits.Quantize(resized.DisplaySize.Height));
    }

    private void TranslateGradientFromEditStart(int objectIndex, float dx, float dy)
    {
        if (!_selectedGradientStarts.TryGetValue(objectIndex, out var gradient)) return;
        _scene.SetLinearGradientEndpoints(
            objectIndex,
            new PointF(gradient.Start.X + dx, gradient.Start.Y + dy),
            new PointF(gradient.End.X + dx, gradient.End.Y + dy));
    }

    private void ResizeGradientFromEditStart(int objectIndex, PointF center, float width, float height)
    {
        if (!_selectedGradientStarts.TryGetValue(objectIndex, out var gradient)
            || _resizeStartCenter is not { } startCenter
            || _resizeStartSize is not { } startSize)
        {
            return;
        }

        var scaleX = width / Math.Max(1f, startSize.Width);
        var scaleY = height / Math.Max(1f, startSize.Height);
        PointF ResizePoint(PointF point)
        {
            var local = new PointF(point.X - startCenter.X, point.Y - startCenter.Y);
            var cos = MathF.Cos(_resizeStartAngle);
            var sin = MathF.Sin(_resizeStartAngle);
            var localX = local.X * cos + local.Y * sin;
            var localY = -local.X * sin + local.Y * cos;
            localX *= scaleX;
            localY *= scaleY;
            return new PointF(
                center.X + localX * cos - localY * sin,
                center.Y + localX * sin + localY * cos);
        }

        _scene.SetLinearGradientEndpoints(objectIndex, ResizePoint(gradient.Start), ResizePoint(gradient.End));
    }

    private void CaptureLineEndpointEditStart(int objectIndex)
    {
        if (_activeHandle is not (EditHandleKind.LineStart or EditHandleKind.LineEnd)) return;
        var startEndpoint = _activeHandle == EditHandleKind.LineStart;
        if (!_scene.TryGetLineEndpoint(objectIndex, startEndpoint, out var anchor)) return;
        var layer = _scene.ObjectLayer[objectIndex];

        var queryBounds = new RectangleF(
            anchor.X - EndpointConnectionToleranceUnits,
            anchor.Y - EndpointConnectionToleranceUnits,
            EndpointConnectionToleranceUnits * 2,
            EndpointConnectionToleranceUnits * 2);
        foreach (var i in _scene.QueryObjects(queryBounds, _frame))
        {
            if (_scene.ShapeKind[i] != ShapeKind.Line
                || _scene.ObjectLayer[i] != layer
                || !_scene.IsObjectActive(i, _frame))
            {
                continue;
            }

            CaptureConnectedEndpoint(i, startEndpoint: true, anchor);
            CaptureConnectedEndpoint(i, startEndpoint: false, anchor);
        }
    }

    private void BuildLineEndpointSnapCache(int layer)
    {
        _lineEndpointSnapBuckets.Clear();
        _lineEndpointSnapCacheLayer = layer;
        _lineEndpointSnapBucketSize = Math.Max(EndpointConnectionToleranceUnits, _stage.ScreenLengthToWorld(LineEndpointSnapRadiusPixels));
        var first = _stage.ScreenToWorld(Point.Empty);
        var second = _stage.ScreenToWorld(new Point(Math.Max(1, _stage.ClientSize.Width), Math.Max(1, _stage.ClientSize.Height)));
        _lineEndpointSnapCacheBounds = RectangleF.FromLTRB(
            Math.Min(first.X, second.X) - _lineEndpointSnapBucketSize,
            Math.Min(first.Y, second.Y) - _lineEndpointSnapBucketSize,
            Math.Max(first.X, second.X) + _lineEndpointSnapBucketSize,
            Math.Max(first.Y, second.Y) + _lineEndpointSnapBucketSize);
        var editedLines = _lineEndpointEditStarts.Select(edit => edit.ObjectIndex).ToHashSet();
        foreach (var candidate in _scene.QueryObjects(_lineEndpointSnapCacheBounds, _frame))
        {
            if (_scene.ShapeKind[candidate] != ShapeKind.Line
                || _scene.ObjectLayer[candidate] != layer
                || editedLines.Contains(candidate))
            {
                continue;
            }

            CacheLineEndpoint(candidate, startEndpoint: true);
            CacheLineEndpoint(candidate, startEndpoint: false);
        }
    }

    private void CacheLineEndpoint(int objectIndex, bool startEndpoint)
    {
        if (!_scene.TryGetLineEndpoint(objectIndex, startEndpoint, out var endpoint)) return;
        var cell = LineEndpointSnapCell(endpoint);
        if (!_lineEndpointSnapBuckets.TryGetValue(cell, out var candidates))
        {
            candidates = new List<LineEndpointSnapCandidate>(2);
            _lineEndpointSnapBuckets.Add(cell, candidates);
        }

        candidates.Add(new LineEndpointSnapCandidate(objectIndex, endpoint));
    }

    private (int X, int Y) LineEndpointSnapCell(PointF point)
    {
        var size = Math.Max(0.001f, _lineEndpointSnapBucketSize);
        return ((int)MathF.Floor(point.X / size), (int)MathF.Floor(point.Y / size));
    }

    private void CaptureConnectedEndpoint(int objectIndex, bool startEndpoint, PointF anchor)
    {
        if (!_scene.TryGetLineEndpoint(objectIndex, startEndpoint, out var endpoint)) return;
        if (Distance(endpoint, anchor) > _stage.ScreenLengthToWorld(EndpointConnectionTolerancePixels)) return;
        if (!_scene.TryGetLineEndpoint(objectIndex, !startEndpoint, out var opposite)) return;
        var control1 = new PointF(_scene.CurveControlX[objectIndex], _scene.CurveControlY[objectIndex]);
        var control2 = new PointF(_scene.CurveControl2X[objectIndex], _scene.CurveControl2Y[objectIndex]);
        _lineEndpointEditStarts.Add(new LineEndpointEditStart(
            objectIndex,
            startEndpoint,
            endpoint,
            opposite,
            control1,
            control2,
            _scene.IsLineStraight(objectIndex)));
    }

    private void ApplyLineEndpointDrag(PointF world, PointF pointerStart)
    {
        if (_lineEndpointEditStarts.Count == 0) return;
        var layer = _selectedObject >= 0 && _selectedObject < _scene.ObjectCount
            ? _scene.ObjectLayer[_selectedObject]
            : _scene.ActiveLayer;
        var selectedStarts = _activeHandle == EditHandleKind.LineStart;
        var primaryEditIndex = _lineEndpointEditStarts.FindIndex(edit =>
            edit.ObjectIndex == _selectedObject && edit.StartEndpoint == selectedStarts);
        var originalAnchor = _lineEndpointEditStarts[
            primaryEditIndex >= 0 ? primaryEditIndex : 0].OriginalEndpoint;
        var adjusted = MoveHandleWithPointer(originalAnchor, pointerStart, world);
        var (snapped, _) = ResolveLineEndpointSnap(adjusted, layer, excludeEditedLines: true);

        foreach (var edit in _lineEndpointEditStarts)
        {
            var dx = snapped.X - edit.OriginalEndpoint.X;
            var dy = snapped.Y - edit.OriginalEndpoint.Y;
            var control1 = edit.StartEndpoint
                ? new PointF(edit.Control1.X + dx, edit.Control1.Y + dy)
                : edit.Control1;
            var control2 = edit.StartEndpoint
                ? edit.Control2
                : new PointF(edit.Control2.X + dx, edit.Control2.Y + dy);
            var curved = ShouldCurveDraggedLineEndpoint(edit, primaryEditIndex);
            if (_scene.IsQuadraticLine(edit.ObjectIndex))
            {
                var originalStart = edit.StartEndpoint ? edit.OriginalEndpoint : edit.OppositeEndpoint;
                var quadratic = new PointF(
                    1.5f * edit.Control1.X - 0.5f * originalStart.X,
                    1.5f * edit.Control1.Y - 0.5f * originalStart.Y);
                var control = new PointF(quadratic.X + dx * (curved ? 1f : 0.5f), quadratic.Y + dy * (curved ? 1f : 0.5f));
                _scene.SetLineEndpoint(edit.ObjectIndex, edit.StartEndpoint, snapped, edit.OppositeEndpoint,
                    control, edit.KeepStraight && !curved);
                continue;
            }
            var (draggedControl, oppositeControl) = ResolveLineEndpointDragControls(
                edit.StartEndpoint,
                curved,
                edit.OriginalEndpoint,
                snapped,
                edit.OppositeEndpoint,
                edit.StartEndpoint ? control1 : control2,
                edit.StartEndpoint ? control2 : control1);
            _scene.SetLineEndpoint(
                edit.ObjectIndex,
                edit.StartEndpoint,
                snapped,
                edit.OppositeEndpoint,
                edit.StartEndpoint ? draggedControl : oppositeControl,
                edit.StartEndpoint ? oppositeControl : draggedControl,
                edit.KeepStraight && !curved);
        }
    }

    /// <summary>
    /// Dragging the endpoint handle of a line that owns that endpoint alone converts the line into a
    /// cubic Bezier: the endpoint follows the pointer while its own control handle travels with it, so
    /// the segment curves instead of staying a rubber band. An endpoint shared with another line's
    /// endpoint is excluded, because bending it would also have to bend the neighbour that is being
    /// dragged along.
    /// </summary>
    private bool ShouldCurveDraggedLineEndpoint(LineEndpointEditStart edit, int primaryEditIndex)
    {
        if (!edit.KeepStraight || _lineEndpointEditStarts.Count != 1 || primaryEditIndex < 0)
        {
            return false;
        }

        return _selectedObject == edit.ObjectIndex
            && _scene.ShapeKind[edit.ObjectIndex] == ShapeKind.Line;
    }

    /// <summary>
    /// Applies a dragged line endpoint plus the control point that should travel with it. Converting
    /// only happens while the line still has its straight control points on the chord: the control
    /// handle takes the pointer offset and the offset is mirrored onto the opposite control, which
    /// turns the straight chord into a curve that bends with the dragged handle. A line that is
    /// already curved keeps its own shape and only moves its endpoint.
    /// </summary>
    internal static (PointF DraggedControl, PointF OppositeControl) ResolveLineEndpointDragControls(
        bool endpointIsStart,
        bool curveOnDrag,
        PointF originalEndpoint,
        PointF endpoint,
        PointF oppositeEndpoint,
        PointF translatedControl,
        PointF oppositeControl)
    {
        if (!curveOnDrag) return (translatedControl, oppositeControl);

        var start = endpointIsStart ? originalEndpoint : oppositeEndpoint;
        var end = endpointIsStart ? oppositeEndpoint : originalEndpoint;
        var dx = endpoint.X - originalEndpoint.X;
        var dy = endpoint.Y - originalEndpoint.Y;
        var originalDraggedControl = new PointF(translatedControl.X - dx, translatedControl.Y - dy);
        if (!VectorScene.IsStraightBezierSegment(start,
            endpointIsStart ? originalDraggedControl : oppositeControl,
            endpointIsStart ? oppositeControl : originalDraggedControl, end))
        {
            return (translatedControl, oppositeControl);
        }

        return (
            translatedControl,
            new PointF(oppositeControl.X + dx, oppositeControl.Y + dy));
    }

    private PointF ResolveDrawingLineEndpoint(PointF world)
    {
        return ResolveLineEndpointSnap(world, _scene.ActiveLayer, excludeEditedLines: false).Point;
    }

    private PointF ResolveDrawingLineEnd(PointF start, PointF world, bool temporarilySnapAngle = false)
    {
        return ResolveDrawingLineEnd(start, world, _scene.ActiveLayer, temporarilySnapAngle);
    }

    private PointF ResolveDrawingLineEnd(
        PointF start,
        PointF world,
        int layer,
        bool temporarilySnapAngle = false)
    {
        var (candidate, snappedToObject) = ResolveLineEndpointSnap(
            world,
            layer,
            excludeEditedLines: false);
        return snappedToObject
            ? candidate
            : ApplyDrawingLineAngleSnap(start, candidate, temporarilySnapAngle);
    }

    private (PointF Point, bool SnappedToObject) ResolveLineEndpointSnap(
        PointF world,
        int layer,
        bool excludeEditedLines)
    {
        var temporarilySnapToObjects = IsControlPressed();
        PointF? objectCandidate = null;
        if (EndpointSnappingRequested())
        {
            if (TrySnapToNearbyLineEndpoint(world, layer, excludeEditedLines, out var nearbyEndpoint))
                objectCandidate = nearbyEndpoint;
            if (TrySnapToNearbyPathPoint(world, layer, excludeEditedLines, out var nearbyPath)
                && (objectCandidate is null || Distance(world, nearbyPath) < Distance(world, objectCandidate.Value)))
            {
                objectCandidate = nearbyPath;
            }
        }

        return (
            _drawSettings.ResolvePointSnap(
                world,
                objectCandidate,
                temporarilySnapToObjects,
                _stage.AdaptiveGridSnapStep),
            objectCandidate.HasValue);
    }

    private PointF ApplyDrawingLineAngleSnap(PointF start, PointF end, bool temporarilySnapAngle)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var length = MathF.Sqrt(dx * dx + dy * dy);
        if (length <= DrawingTopologyRules.UnitIntersectionTolerance) return end;
        var angle = _drawSettings.SnapAngle(MathF.Atan2(dy, dx), temporarilySnapAngle);
        return VectorUnits.Quantize(new PointF(
            start.X + MathF.Cos(angle) * length,
            start.Y + MathF.Sin(angle) * length));
    }

    private bool EndpointSnappingRequested()
    {
        return IsControlPressed() || (_drawSettings.SnapEnabled && _drawSettings.SnapToObjects);
    }

    private bool TrySnapToNearbyLineEndpoint(
        PointF world,
        int layer,
        bool excludeEditedLines,
        out PointF snapped)
    {
        var tolerance = Math.Max(EndpointConnectionToleranceUnits, _stage.ScreenLengthToWorld(LineEndpointSnapRadiusPixels));
        snapped = world;
        var bestDistance = tolerance;
        if (excludeEditedLines
            && layer == _lineEndpointSnapCacheLayer
            && _lineEndpointSnapBucketSize > 0
            && _lineEndpointSnapCacheBounds.Contains(world))
        {
            var cell = LineEndpointSnapCell(world);
            for (var y = cell.Y - 1; y <= cell.Y + 1; y++)
            {
                for (var x = cell.X - 1; x <= cell.X + 1; x++)
                {
                    if (!_lineEndpointSnapBuckets.TryGetValue((x, y), out var candidates)) continue;
                    foreach (var candidate in candidates)
                    {
                        TrySnapToCachedEndpoint(candidate, world, ref snapped, ref bestDistance);
                    }
                }
            }

            return bestDistance < tolerance;
        }

        var queryBounds = new RectangleF(
            world.X - tolerance,
            world.Y - tolerance,
            tolerance * 2,
            tolerance * 2);
        var editedLines = excludeEditedLines
            ? _lineEndpointEditStarts.Select(edit => edit.ObjectIndex).ToHashSet()
            : null;
        foreach (var i in _scene.QueryObjects(queryBounds, _frame))
        {
            if (_scene.ShapeKind[i] != ShapeKind.Line
                || _scene.ObjectLayer[i] != layer
                || !_scene.IsObjectActive(i, _frame)
                || editedLines?.Contains(i) == true)
            {
                continue;
            }

            TrySnapToEndpoint(i, startEndpoint: true, world, ref snapped, ref bestDistance);
            TrySnapToEndpoint(i, startEndpoint: false, world, ref snapped, ref bestDistance);
        }

        return bestDistance < tolerance;
    }

    private bool TrySnapToNearbyPathPoint(
        PointF world,
        int layer,
        bool excludeEditedLines,
        out PointF snapped)
    {
        var tolerance = Math.Max(EndpointConnectionToleranceUnits, _stage.ScreenLengthToWorld(LineEndpointSnapRadiusPixels));
        snapped = world;
        var bestDistance = tolerance;
        var queryBounds = new RectangleF(
            world.X - tolerance,
            world.Y - tolerance,
            tolerance * 2,
            tolerance * 2);
        var editedLines = excludeEditedLines
            ? _lineEndpointEditStarts.Select(edit => edit.ObjectIndex).ToHashSet()
            : null;
        foreach (var i in _scene.QueryObjects(queryBounds, _frame))
        {
            if (_scene.ObjectLayer[i] != layer
                || !_scene.IsObjectActive(i, _frame)
                || editedLines?.Contains(i) == true)
            {
                continue;
            }

            foreach (var part in _scene.GetStrokeParts(i, _frame))
                AccumulateClosestOnPolyline(part.Points, world, ref snapped, ref bestDistance);
            foreach (var part in _scene.GetBoundaryParts(i, _frame))
                AccumulateClosestOnPolyline(part.Points, world, ref snapped, ref bestDistance);
        }

        return bestDistance < tolerance;
    }

    private static void AccumulateClosestOnPolyline(
        PointF[] points,
        PointF world,
        ref PointF best,
        ref float bestDistance)
    {
        for (var index = 1; index < points.Length; index++)
        {
            var projected = ProjectPointOntoSegment(world, points[index - 1], points[index]);
            var distance = Distance(world, projected);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = projected;
        }
    }

    private static PointF ProjectPointOntoSegment(PointF point, PointF start, PointF end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var lengthSquared = dx * dx + dy * dy;
        if (lengthSquared <= float.Epsilon) return start;
        var t = ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared;
        t = Math.Clamp(t, 0f, 1f);
        return new PointF(start.X + dx * t, start.Y + dy * t);
    }

    private static void TrySnapToCachedEndpoint(
        LineEndpointSnapCandidate candidate,
        PointF world,
        ref PointF best,
        ref float bestDistance)
    {
        var distance = Distance(world, candidate.Point);
        if (distance >= bestDistance) return;
        bestDistance = distance;
        best = candidate.Point;
    }

    private void TrySnapToEndpoint(int objectIndex, bool startEndpoint, PointF world, ref PointF best, ref float bestDistance)
    {
        if (!_scene.TryGetLineEndpoint(objectIndex, startEndpoint, out var endpoint)) return;
        var distance = Distance(world, endpoint);
        if (distance >= bestDistance) return;
        bestDistance = distance;
        best = endpoint;
    }

    private PointF WorldToLocalFromEditStart(PointF world)
    {
        var center = _resizeStartCenter!.Value;
        var dx = world.X - center.X;
        var dy = world.Y - center.Y;
        var cos = MathF.Cos(_resizeStartAngle);
        var sin = MathF.Sin(_resizeStartAngle);
        return new PointF(dx * cos + dy * sin, -dx * sin + dy * cos);
    }

    private PointF LocalToWorldFromEditStart(PointF local)
    {
        var center = _resizeStartCenter!.Value;
        var cos = MathF.Cos(_resizeStartAngle);
        var sin = MathF.Sin(_resizeStartAngle);
        return new PointF(center.X + local.X * cos - local.Y * sin, center.Y + local.X * sin + local.Y * cos);
    }

    private static PointF OppositeCorner(EditHandleKind handle, SizeF size)
    {
        var halfW = size.Width * 0.5f;
        var halfH = size.Height * 0.5f;
        return handle switch
        {
            EditHandleKind.BoundsTopLeft => new PointF(halfW, halfH),
            EditHandleKind.BoundsTopRight => new PointF(-halfW, halfH),
            EditHandleKind.BoundsBottomRight => new PointF(-halfW, -halfH),
            EditHandleKind.BoundsBottomLeft => new PointF(halfW, -halfH),
            _ => PointF.Empty
        };
    }

    private static PointF BitmapCornerLocal(EditHandleKind handle, SizeF size)
    {
        var halfW = size.Width * 0.5f;
        var halfH = size.Height * 0.5f;
        var (signX, signY) = BitmapCornerSigns(handle);
        return new PointF(signX * halfW, signY * halfH);
    }

    private static (float X, float Y) BitmapCornerSigns(EditHandleKind handle)
    {
        return handle switch
        {
            EditHandleKind.BoundsTopLeft => (-1f, -1f),
            EditHandleKind.BoundsTopRight => (1f, -1f),
            EditHandleKind.BoundsBottomRight => (1f, 1f),
            EditHandleKind.BoundsBottomLeft => (-1f, 1f),
            _ => (0f, 0f)
        };
    }

    internal static bool TryGetBitmapCornerResizeSize(
        SizeF startSize,
        EditHandleKind handle,
        PointF draggedLocal,
        float minimumSize,
        out float width,
        out float height)
    {
        width = height = 0f;
        if (startSize.Width <= 0f
            || startSize.Height <= 0f
            || !float.IsFinite(minimumSize)
            || minimumSize <= 0f)
        {
            return false;
        }

        var (signX, signY) = BitmapCornerSigns(handle);
        if (signX == 0f || signY == 0f) return false;
        var anchor = OppositeCorner(handle, startSize);
        var startWidth = Math.Max(1f, startSize.Width);
        var startHeight = Math.Max(1f, startSize.Height);
        var scaleX = (draggedLocal.X - anchor.X) * signX / startWidth;
        var scaleY = (draggedLocal.Y - anchor.Y) * signY / startHeight;
        var scale = Math.Abs(Math.Abs(scaleX) - 1f) >= Math.Abs(Math.Abs(scaleY) - 1f)
            ? scaleX
            : scaleY;
        var minimumScale = Math.Max(minimumSize / startWidth, minimumSize / startHeight);
        if (Math.Abs(scale) < minimumScale) scale = scale < 0f ? -minimumScale : minimumScale;
        width = Math.Max(minimumSize, Math.Abs(startWidth * scale));
        height = Math.Max(minimumSize, Math.Abs(startHeight * scale));
        return float.IsFinite(width) && float.IsFinite(height);
    }

    private void AddDrawnObject(PointF start, PointF end, ToolMode tool)
    {
        if (DrawingToolsBlocked()) return;
        CaptureUndoSnapshot(affectedLayers: [_scene.ActiveLayer]);
        var shape = ToolShapeKind(tool) ?? _drawSettings.ShapeKind;
        int newObject;
        if (tool == ToolMode.Line)
        {
            end = ResolveDrawingLineEnd(start, end, temporarilySnapAngle: IsShiftPressed());
            newObject = _scene.AddLineSegment(
                _scene.ActiveLayer,
                start,
                end,
                ActiveStrokeUnits(),
                ActiveColor(),
                ActiveStrokeColor(),
                6);
            _scene.QuadraticLineEditing[newObject] = true;
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
            var center = new PointF((start.X + end.X) * 0.5f, (start.Y + end.Y) * 0.5f);
            var width = Math.Max(VectorUnits.FromPixels(4), Math.Abs(end.X - start.X));
            var height = Math.Max(VectorUnits.FromPixels(4), Math.Abs(end.Y - start.Y));
            newObject = _scene.AddObject(
                _scene.ActiveLayer,
                center,
                new SizeF(width, height),
                0,
                ActiveStrokeUnits(),
                ActiveColor(),
                ActiveStrokeColor(),
                24,
                shape,
                ShapeVertexCountFor(shape));
        }

        if (newObject >= 0 && _materialEditor.GradientEnabled)
        {
            _scene.SetGradientPaint(newObject, _materialEditor.GradientKind, _materialEditor.GradientStops);
        }
        if (newObject >= 0 && tool != ToolMode.Line)
        {
            var overwritten = NormalizeNewPaintObjects([newObject]);
            if (overwritten.Length > 0) newObject = overwritten[0];
        }

        SetSelection(newObject);
        if (tool == ToolMode.Line) FinalizeNewTopologyStrokeDrawingOperation(newObject);
        _hierarchyPanel.RefreshScene();
        UpdateInspector();
        _stage.Invalidate();
    }

    private void ApplyDrawingObjectPlaybackSettings(DrawingObjectPlaybackSettingsChangedEventArgs settings)
    {
        var instances = SelectedSceneInstances();
        var changed = false;
        ClearInstanceTimelineEditTracking();
        foreach (var instance in instances)
        {
            var editFrame = ResolveInstanceStateEditFrameForCurrentContext(instance);
            var state = instance.EvaluateState(editFrame);
            if (state.PlaybackFps == settings.PlaybackFps
                && state.PlaybackMode == settings.PlaybackMode
                && state.HoldFrame == settings.HoldFrame)
            {
                continue;
            }

            editFrame = PrepareInstanceStateTimelineEdit(instance);
            changed |= instance.SetStateAtFrame(editFrame, state with
            {
                PlaybackFps = settings.PlaybackFps,
                PlaybackMode = settings.PlaybackMode,
                HoldFrame = settings.HoldFrame
            });
        }
        if (!changed)
        {
            ClearInstanceTimelineEditTracking();
            return;
        }

        RefreshEditedInstanceTimelineTweens();
        ClearInstanceTimelineEditTracking();
        _sceneInstanceTimelineDirty = true;
        _timeline.RefreshTimeline();
        _sceneInstanceTimelineDirty = false;
        RebuildEditableInstanceComposition();
        UpdateInspector();
    }

    private void BeginInstanceAppearanceEdit()
    {
        if (_instanceAppearanceEditSession is not null) return;
        var instances = SelectedSceneInstances();
        if (instances.Count == 0) return;

        var selectedIds = instances.Select(instance => instance.Id).ToArray();
        ClearInstanceTimelineEditTracking();
        if (IsSceneCompositionContext())
        {
            var scene = ActiveScene();
            if (scene is null) return;
            _instanceAppearanceEditSession = new InstanceAppearanceEditSession
            {
                Scene = scene,
                TimelineSnapshot = scene.Timeline.CreateSnapshot(),
                InstanceSnapshot = scene.CreateInstanceSnapshot(),
                SelectedInstanceIds = selectedIds,
                PrimaryInstanceId = _selectedSceneInstanceId
            };
            return;
        }

        var drawingObject = ActiveDrawingObject();
        if (drawingObject is null) return;
        _instanceAppearanceEditSession = new InstanceAppearanceEditSession
        {
            DrawingObject = drawingObject,
            DrawingSceneSnapshot = _scene.CreateSnapshot(),
            InstanceSnapshot = drawingObject.CreateInstanceSnapshot(),
            SelectedInstanceIds = selectedIds,
            PrimaryInstanceId = _selectedSceneInstanceId
        };
    }

    private void ApplySelectedDrawingObjectAppearance(DrawingObjectAppearanceChangedEventArgs appearance)
    {
        BeginInstanceAppearanceEdit();
        var session = _instanceAppearanceEditSession;
        if (session is null) return;

        var changed = false;
        foreach (var instance in ActiveEditableInstances().Where(instance =>
                     session.SelectedInstanceIds.Contains(instance.Id, StringComparer.Ordinal)))
        {
            var editFrame = ResolveInstanceStateEditFrameForCurrentContext(instance);
            var state = instance.EvaluateState(editFrame);
            var next = state with
            {
                Alpha = appearance.AlphaChanged ? appearance.Alpha : state.Alpha,
                TintArgb = appearance.TintChanged ? appearance.TintArgb : state.TintArgb
            };
            if (next == state) continue;
            editFrame = PrepareInstanceStateTimelineEdit(instance);
            changed |= instance.SetStateAtFrame(editFrame, next);
        }

        if (!changed) return;
        session.Changed = true;
        _sceneInstanceTimelineDirty = true;
        QueueInstanceAppearancePreview();
    }

    private void QueueInstanceAppearancePreview()
    {
        _instanceAppearancePreviewPending = true;
        if (!_instanceAppearancePreviewTimer.Enabled) _instanceAppearancePreviewTimer.Start();
    }

    private void FlushInstanceAppearancePreview()
    {
        _instanceAppearancePreviewTimer.Stop();
        if (!_instanceAppearancePreviewPending) return;
        _instanceAppearancePreviewPending = false;
        RebuildEditableInstanceComposition();
        _stage.Invalidate();
    }

    private void CompleteInstanceAppearanceEdit()
    {
        var session = _instanceAppearanceEditSession;
        if (session is null) return;
        FlushInstanceAppearancePreview();
        _instanceAppearanceEditSession = null;
        if (!session.Changed)
        {
            ClearInstanceTimelineEditTracking();
            _sceneInstanceTimelineDirty = false;
            UpdateInspector();
            return;
        }

        RefreshEditedInstanceTimelineTweens();
        ClearInstanceTimelineEditTracking();

        if (session.Scene is { } scene && session.TimelineSnapshot is { } timelineSnapshot)
        {
            PushSceneTimelineUndo(scene, timelineSnapshot, instanceSnapshot: session.InstanceSnapshot);
        }
        else if (session.DrawingObject is { } drawingObject
                 && session.DrawingSceneSnapshot is { } drawingSceneSnapshot)
        {
            PushUndoSnapshot(drawingSceneSnapshot, drawingObject, session.InstanceSnapshot);
        }

        _timeline.RefreshTimeline();
        _sceneInstanceTimelineDirty = false;
        UpdateInspector();
    }

    private void CancelInstanceAppearanceEdit()
    {
        var session = _instanceAppearanceEditSession;
        if (session is null) return;
        _instanceAppearanceEditSession = null;
        _instanceAppearancePreviewTimer.Stop();
        _instanceAppearancePreviewPending = false;
        ClearInstanceTimelineEditTracking();
        _sceneInstanceTimelineDirty = false;

        if (session.Scene is { } scene && session.TimelineSnapshot is { } timelineSnapshot)
        {
            scene.RestoreInstanceSnapshot(session.InstanceSnapshot);
            scene.Timeline.RestoreSnapshot(timelineSnapshot);
            scene.SynchronizeTimelineTracks();
        }
        else if (session.DrawingObject is { } drawingObject
                 && session.DrawingSceneSnapshot is { } drawingSceneSnapshot)
        {
            drawingObject.RestoreInstanceSnapshot(session.InstanceSnapshot);
            _scene.RestoreSnapshot(drawingSceneSnapshot);
            _scene.EditFrame = _frame;
        }

        _timeline.RefreshTimeline();
        RebuildEditableInstanceComposition();
        RestoreTimelineInstanceSelection(session.SelectedInstanceIds, session.PrimaryInstanceId);
        UpdateInspector();
        _stage.Invalidate();
    }

    private void RestoreSelectedDrawingObjectOriginalSize()
    {
        CompleteInstanceAppearanceEdit();
        BeginInstanceAppearanceEdit();
        var session = _instanceAppearanceEditSession;
        if (session is null) return;

        var changed = false;
        foreach (var instance in SelectedSceneInstances())
        {
            var editFrame = ResolveInstanceStateEditFrameForCurrentContext(instance);
            if (!CanRestoreDrawingObjectOriginalSize(instance.EvaluateState(editFrame))) continue;
            editFrame = PrepareInstanceStateTimelineEdit(instance);
            changed |= RestoreDrawingObjectOriginalSize(instance, editFrame);
        }
        if (changed)
        {
            session.Changed = true;
            _sceneInstanceTimelineDirty = true;
        }
        CompleteInstanceAppearanceEdit();
        if (!changed) return;
        RebuildEditableInstanceComposition();
        UpdateInspector();
        _stage.Invalidate();
    }

    private void ApplySelectedDrawingObjectAnchor(PointF anchor)
    {
        var selected = SelectedSceneInstance();
        if (selected is null
            || !_project.TrySetDrawingObjectAnchor(selected.DrawingObjectId, anchor))
        {
            UpdateInspector();
            return;
        }

        RebuildEditableInstanceComposition();
        UpdateInspector();
    }

    internal static bool RestoreDrawingObjectOriginalSize(DrawingObjectInstanceDefinition instance, int frame = 0)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var state = instance.EvaluateState(frame);
        if (!CanRestoreDrawingObjectOriginalSize(state)) return false;

        return instance.SetStateAtFrame(frame, state with
        {
            ScaleX = 1f, ScaleY = 1f, ScaleZ = 1f,
            RotationX = 0f, RotationY = 0f, RotationZ = 0f,
            SkewX = 0f, SkewY = 0f
        });
    }

    internal static bool CanRestoreDrawingObjectOriginalSize(InstanceFrameState state)
    {
        return Math.Abs(state.ScaleX - 1f) > 0.0001f
            || Math.Abs(state.ScaleY - 1f) > 0.0001f
            || Math.Abs(state.ScaleZ - 1f) > 0.0001f
            || Math.Abs(state.RotationX) > 0.0001f
            || Math.Abs(state.RotationY) > 0.0001f
            || Math.Abs(state.RotationZ) > 0.0001f
            || Math.Abs(state.SkewX) > 0.0001f
            || Math.Abs(state.SkewY) > 0.0001f;
    }

}
