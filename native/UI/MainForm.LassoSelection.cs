using System.Drawing;

namespace VectorAnimationEngine;

internal sealed partial class MainForm : Form
{
    private const int MaximumLassoPoints = 4096;
    private const int FreehandLassoSampleDistancePixels = 3;
    private const int LassoCloseDistancePixels = 10;
    private const double MinimumLassoAreaPixelsSquared = 4;

    private readonly List<Point> _lassoScreenPoints = new(256);
    private int[] _lassoSelectionBase = [];
    private DrawingElementHit[] _lassoElementSelectionBase = [];
    private string[] _lassoInstanceSelectionBase = [];
    private bool _lassoPointerActive;
    private bool _lassoPointerDown;
    private bool _lassoAdditiveSelection;

    private readonly record struct LassoStackIdentity(
        int Layer,
        int Keyframe,
        long Order,
        double SubOrder,
        double LowerSubOrder,
        double UpperSubOrder);

    private readonly record struct LassoBaseObjectSelection(
        int Index,
        LassoStackIdentity Stack,
        PointF[][] BoundaryContours);

    private readonly record struct LassoBaseElementSelection(
        DrawingElementHit Hit,
        LassoStackIdentity Stack,
        PointF[][] FillContours,
        PointF[] Geometry);

    private static bool IsLassoTool(ToolMode tool)
    {
        return tool is ToolMode.PolygonLasso or ToolMode.FreehandLasso;
    }

    private bool CanUseLassoInCurrentContext()
    {
        if (!IsLassoTool(_tool) || IsScene3DView()) return false;
        return _workspaceTabs.SelectedView == WorkspaceView.BasicDrawing
            || IsSceneMaskEditing()
            || IsSceneCompositionContext() && IsScene2DFrontView();
    }

    private bool BeginLassoPointer(MouseEventArgs e)
    {
        if (!CanUseLassoInCurrentContext()) return false;
        FinalizePendingMarqueeSelectionCancellation();
        if (e.Button == MouseButtons.Right)
        {
            var wasActive = _lassoPointerActive;
            if (wasActive) CancelLassoPointer();
            return wasActive;
        }
        if (e.Button != MouseButtons.Left) return false;

        if (!_lassoPointerActive)
        {
            _lassoPointerActive = true;
            _lassoPointerDown = true;
            _lassoAdditiveSelection = IsShiftPressed();
            _lassoSelectionBase = _selectedObjects.ToArray();
            _lassoElementSelectionBase = _selectedElements.ToArray();
            _lassoInstanceSelectionBase = _selectedSceneInstanceIds.ToArray();
            _lassoScreenPoints.Clear();
            _stage.ClearMarquee();
        }
        else
        {
            _lassoPointerDown = true;
        }

        _stage.Capture = true;

        if (_tool == ToolMode.PolygonLasso)
        {
            if (_lassoScreenPoints.Count >= 3 && IsNearLassoStart(e.Location))
            {
                CommitLassoPointer();
                return true;
            }

            AddLassoPoint(e.Location);
            UpdateLassoPreview(e.Location);
            return true;
        }

        _lassoScreenPoints.Clear();
        AddLassoPoint(e.Location);
        UpdateLassoPreview(e.Location);
        return true;
    }

    private void UpdateLassoPointer(Point screen)
    {
        if (!_lassoPointerActive || !CanUseLassoInCurrentContext()) return;
        if (_tool == ToolMode.FreehandLasso)
        {
            if (_lassoPointerDown) AddFreehandLassoPoint(screen);
            return;
        }

        UpdateLassoPreview(screen);
    }

    private bool HandleLassoMouseUp(MouseEventArgs e)
    {
        if (!_lassoPointerActive) return false;
        if (_tool == ToolMode.PolygonLasso)
        {
            if (e.Button == MouseButtons.Left)
            {
                _lassoPointerDown = false;
                UpdateLassoPreview(e.Location);
            }
            return true;
        }

        if (e.Button != MouseButtons.Left) return true;
        AddFreehandLassoPoint(e.Location, force: true);
        _lassoPointerDown = false;
        if (_lassoScreenPoints.Count >= 3 && HasLassoArea(_lassoScreenPoints)) CommitLassoPointer();
        else CancelLassoPointer();
        return true;
    }

    private bool HandleLassoMouseDoubleClick(MouseEventArgs e)
    {
        if (_tool != ToolMode.PolygonLasso
            || !_lassoPointerActive
            || e.Button != MouseButtons.Left)
        {
            return false;
        }

        CommitLassoPointer();
        return true;
    }

    private bool HandleLassoCommandKey(Keys keyData)
    {
        if (!_lassoPointerActive || !IsLassoTool(_tool)) return false;
        var key = keyData & Keys.KeyCode;
        if (key == Keys.Escape)
        {
            CancelLassoPointer();
            return true;
        }

        if (key == Keys.Enter && _tool == ToolMode.PolygonLasso)
        {
            CommitLassoPointer();
            return true;
        }

        return false;
    }

    private void AddLassoPoint(Point point)
    {
        if (_lassoScreenPoints.Count >= MaximumLassoPoints) return;
        if (_lassoScreenPoints.Count > 0 && _lassoScreenPoints[^1] == point) return;
        _lassoScreenPoints.Add(point);
    }

    private void AddFreehandLassoPoint(Point point, bool force = false)
    {
        if (_lassoScreenPoints.Count >= MaximumLassoPoints) return;
        if (!force && _lassoScreenPoints.Count > 0)
        {
            var previous = _lassoScreenPoints[^1];
            var dx = point.X - previous.X;
            var dy = point.Y - previous.Y;
            if (dx * dx + dy * dy < FreehandLassoSampleDistancePixels * FreehandLassoSampleDistancePixels) return;
        }

        AddLassoPoint(point);
        UpdateLassoPreview(point);
    }

    private void UpdateLassoPreview(Point cursor)
    {
        if (!_lassoPointerActive || _lassoScreenPoints.Count == 0) return;
        Point? previewCursor = _tool == ToolMode.PolygonLasso
            && _lassoScreenPoints.Count < MaximumLassoPoints
            && _lassoScreenPoints[^1] != cursor
                ? cursor
                : null;
        var previewPointCount = _lassoScreenPoints.Count + (previewCursor is null ? 0 : 1);
        _stage.SetIncrementalLassoPreview(
            _lassoScreenPoints,
            previewCursor,
            closed: previewPointCount >= 3);
    }

    private bool IsNearLassoStart(Point point)
    {
        if (_lassoScreenPoints.Count == 0) return false;
        var start = _lassoScreenPoints[0];
        var dx = point.X - start.X;
        var dy = point.Y - start.Y;
        return dx * dx + dy * dy <= LassoCloseDistancePixels * LassoCloseDistancePixels;
    }

    private static bool HasLassoArea(IReadOnlyList<Point> points)
    {
        if (points.Count < 3) return false;
        double area = 0;
        for (var index = 0; index < points.Count; index++)
        {
            var next = points[(index + 1) % points.Count];
            area += (double)points[index].X * next.Y - (double)next.X * points[index].Y;
        }

        return Math.Abs(area) * 0.5 > MinimumLassoAreaPixelsSquared;
    }

    private bool TryBuildLassoWorldPolygon(
        IReadOnlyList<Point> screenPoints,
        out PointF[] worldPoints)
    {
        worldPoints = new PointF[screenPoints.Count];
        for (var index = 0; index < screenPoints.Count; index++)
        {
            if (!TryGetSceneBasePlanePoint(screenPoints[index], out worldPoints[index]))
            {
                worldPoints = [];
                return false;
            }
        }

        return worldPoints.Length >= 3;
    }

    private void CommitLassoPointer()
    {
        if (!_lassoPointerActive) return;
        var screenPoints = _lassoScreenPoints.ToArray();
        if (!HasLassoArea(screenPoints)
            || !TryBuildLassoWorldPolygon(screenPoints, out var worldPolygon))
        {
            CancelLassoPointer();
            return;
        }

        _stage.SetLassoPreview(screenPoints, closed: true);
        var sceneCompositionContext = IsSceneCompositionContext();
        if (!_lassoAdditiveSelection
            && _marqueeMaterializationSession is { Scene: var sessionScene }
            && ReferenceEquals(sessionScene, _scene)
            && RestoreUneditedMarqueeMaterialization())
        {
            ClearSelection();
        }

        var baseObjects = _lassoAdditiveSelection
            ? _lassoSelectionBase
                .Where(index => (uint)index < _scene.ObjectCount
                    && !_lassoElementSelectionBase.Any(hit => hit.Key.ObjectIndex == index))
                .Select(CaptureLassoBaseObject)
                .ToArray()
            : Array.Empty<LassoBaseObjectSelection>();
        var baseElements = _lassoAdditiveSelection
            ? _lassoElementSelectionBase
                .Where(hit => hit.IsValid && (uint)hit.Key.ObjectIndex < _scene.ObjectCount)
                .Select(CaptureLassoBaseElement)
                .ToArray()
            : Array.Empty<LassoBaseElementSelection>();
        var snapshot = sceneCompositionContext ? null : _scene.CreateSnapshot();
        var materialized = sceneCompositionContext
            ? new MarqueeMaterializationResult(
                true,
                false,
                Array.Empty<int>(),
                Enumerable.Range(0, _scene.ObjectCount).ToArray())
            : _scene.MaterializeLassoSelectionParts(worldPolygon, _frame);
        if (materialized.Changed && snapshot is not null)
        {
            var independentStrokes = materialized.SelectedObjects
                .Where(index => (uint)index < _scene.ObjectCount
                    && _scene.ShapeKind[index] is ShapeKind.Line or ShapeKind.Freeform)
                .Distinct()
                .ToArray();
            if (_marqueeMaterializationSession is { Scene: var existingSessionScene }
                && ReferenceEquals(existingSessionScene, _scene))
            {
                var remappedIndependentStrokes = _marqueeMaterializationSession.IndependentStrokeObjects
                    .Select(index => (uint)index < materialized.OldToNewObjectIndex.Length
                        ? materialized.OldToNewObjectIndex[index]
                        : -1)
                    .Where(index => (uint)index < _scene.ObjectCount)
                    .Concat(independentStrokes)
                    .Distinct()
                    .ToArray();
                _marqueeMaterializationSession = _marqueeMaterializationSession with
                {
                    IndependentStrokeObjects = remappedIndependentStrokes
                };
            }
            else
            {
                PushUndoSnapshot(snapshot);
                _marqueeMaterializationSession = new MarqueeMaterializationSession(
                    _scene,
                    snapshot,
                    independentStrokes);
            }
            _hierarchyPanel.RefreshScene();
        }

        var remappedBaseObjects = RemapLassoBaseObjects(baseObjects, materialized);
        var remappedBaseElements = RemapLassoBaseElements(baseElements, materialized)
            .Concat(CreateLassoElementHits(remappedBaseObjects))
            .DistinctBy(hit => hit.Key)
            .ToArray();
        var selectedObjects = Array.Empty<int>();
        var selectedElements = Array.Empty<DrawingElementHit>();
        IReadOnlyList<DrawingObjectInstanceDefinition> selectedInstances = [];
        if (sceneCompositionContext)
        {
            selectedInstances = FindLassoInstancesInside(worldPolygon);
        }
        else
        {
            selectedObjects = OrderLassoObjects(
                materialized.SelectedObjects
                    .Concat(_scene.QueryDrawingObjectsInsidePolygon(worldPolygon, _frame))
                    .Where(index => (uint)index < _scene.ObjectCount
                        && _scene.IsObjectSelectable(index, _frame)));
            selectedElements = OrderLassoElements(
                CreateLassoElementHits(materialized.SelectedObjects)
                    .Concat(_scene.QueryDrawingElementsInsidePolygon(worldPolygon, _frame))
                    .Where(hit => hit.IsValid
                        && (uint)hit.Key.ObjectIndex < _scene.ObjectCount
                        && _scene.IsObjectSelectable(hit.Key.ObjectIndex, _frame)));

            selectedInstances = FindLassoInstancesInside(worldPolygon);
        }

        if (selectedObjects.Length > 0 || selectedElements.Length > 0 || selectedInstances.Count > 0)
        {
            ApplyLassoSelection(
                selectedObjects,
                selectedElements,
                selectedInstances,
                remappedBaseObjects,
                remappedBaseElements);
        }
        else if (!_lassoAdditiveSelection)
        {
            ClearSelection();
        }

        EndLassoPointer();
        UpdateInspector();
        _stage.Invalidate();
    }

    private int[] OrderLassoObjects(IEnumerable<int> indices)
    {
        return indices
            .Where(index => (uint)index < _scene.ObjectCount)
            .Distinct()
            .OrderBy(index => _scene.ObjectLayer[index])
            .ThenBy(index => _scene.ObjectOrder[index])
            .ThenBy(index => _scene.ObjectSubOrder[index])
            .ThenBy(index => index)
            .ToArray();
    }

    private DrawingElementHit[] OrderLassoElements(IEnumerable<DrawingElementHit> hits)
    {
        return hits
            .Where(hit => hit.IsValid && (uint)hit.Key.ObjectIndex < _scene.ObjectCount)
            .DistinctBy(hit => hit.Key)
            .OrderBy(hit => _scene.ObjectLayer[hit.Key.ObjectIndex])
            .ThenBy(hit => _scene.ObjectOrder[hit.Key.ObjectIndex])
            .ThenBy(hit => _scene.ObjectSubOrder[hit.Key.ObjectIndex])
            .ThenBy(hit => hit.Key.ObjectIndex)
            .ThenBy(hit => hit.Key.Kind)
            .ThenBy(hit => hit.Key.PartIndex)
            .ToArray();
    }

    private LassoBaseObjectSelection CaptureLassoBaseObject(int index)
    {
        return new LassoBaseObjectSelection(
            index,
            CaptureLassoStackIdentity(index),
            _scene.GetObjectBoundaryContours(index));
    }

    private LassoBaseElementSelection CaptureLassoBaseElement(DrawingElementHit hit)
    {
        var fillContours = Array.Empty<PointF[]>();
        var geometry = Array.Empty<PointF>();
        if (hit.Key.Kind == DrawingElementKind.Fill)
        {
            fillContours = _scene.GetFillPartContours(hit, _frame);
        }
        else if (hit.Key.Kind == DrawingElementKind.Stroke)
        {
            geometry = _scene.GetStrokePartPoints(hit, _frame);
        }
        else if (hit.Key.Kind == DrawingElementKind.BoundaryStroke)
        {
            geometry = _scene.GetBoundaryPartPoints(hit, _frame);
        }

        return new LassoBaseElementSelection(
            hit,
            CaptureLassoStackIdentity(hit.Key.ObjectIndex),
            fillContours,
            geometry);
    }

    private LassoStackIdentity CaptureLassoStackIdentity(int objectIndex)
    {
        var layer = _scene.ObjectLayer[objectIndex];
        var keyframe = _scene.ObjectKeyframeFrame[objectIndex];
        var order = _scene.ObjectOrder[objectIndex];
        var subOrder = _scene.ObjectSubOrder[objectIndex];
        var lower = double.NegativeInfinity;
        var upper = double.PositiveInfinity;
        for (var index = 0; index < _scene.ObjectCount; index++)
        {
            if (index == objectIndex || _scene.ObjectOrder[index] != order) continue;
            var candidate = _scene.ObjectSubOrder[index];
            if (candidate < subOrder) lower = Math.Max(lower, candidate);
            else if (candidate > subOrder) upper = Math.Min(upper, candidate);
        }

        return new LassoStackIdentity(
            layer,
            keyframe,
            order,
            subOrder,
            lower,
            upper);
    }

    private int[] RemapLassoBaseObjects(
        IReadOnlyList<LassoBaseObjectSelection> baseObjects,
        MarqueeMaterializationResult materialized)
    {
        if (baseObjects.Count == 0) return [];

        var remapped = new HashSet<int>();
        foreach (var selection in baseObjects)
        {
            var mapped = RemapLassoObjectIndex(selection.Index, materialized);
            if (mapped >= 0)
            {
                remapped.Add(mapped);
                continue;
            }

            var retainedCount = LassoRetainedObjectCount(materialized);
            for (var index = retainedCount; index < _scene.ObjectCount; index++)
            {
                if (!IsLassoReplacementObject(index, retainedCount, selection.Stack)
                    || !LassoObjectGeometryMatches(selection.BoundaryContours, index)) continue;
                remapped.Add(index);
            }
        }

        return remapped.ToArray();
    }

    private DrawingElementHit[] RemapLassoBaseElements(
        IReadOnlyList<LassoBaseElementSelection> baseElements,
        MarqueeMaterializationResult materialized)
    {
        if (baseElements.Count == 0) return [];

        var remapped = new List<DrawingElementHit>(baseElements.Count);
        foreach (var selection in baseElements)
        {
            var mapped = RemapLassoObjectIndex(selection.Hit.Key.ObjectIndex, materialized);
            if (mapped >= 0)
            {
                var candidates = CreateLassoElementHits(mapped)
                    .Where(hit => IsLassoElementKindCompatible(
                        selection.Hit.Key.Kind,
                        hit.Key.Kind));
                if (selection.FillContours.Length > 0 || selection.Geometry.Length > 0)
                {
                    candidates = candidates.Where(hit => LassoElementGeometryMatches(selection, hit));
                }
                else
                {
                    candidates = candidates.Where(hit => hit.Key == (selection.Hit.Key with { ObjectIndex = mapped }));
                }

                remapped.AddRange(candidates);
                continue;
            }

            var retainedCount = LassoRetainedObjectCount(materialized);
            for (var index = retainedCount; index < _scene.ObjectCount; index++)
            {
                if (!IsLassoReplacementObject(index, retainedCount, selection.Stack))
                {
                    continue;
                }

                remapped.AddRange(CreateLassoElementHits(index)
                    .Where(hit => IsLassoElementKindCompatible(
                        selection.Hit.Key.Kind,
                        hit.Key.Kind)
                        && LassoElementGeometryMatches(selection, hit)));
            }
        }

        return remapped
            .Where(hit => hit.IsValid)
            .DistinctBy(hit => hit.Key)
            .ToArray();
    }

    private int RemapLassoObjectIndex(
        int source,
        MarqueeMaterializationResult materialized)
    {
        if ((uint)source >= materialized.OldToNewObjectIndex.Length) return -1;
        var mapped = materialized.OldToNewObjectIndex[source];
        return (uint)mapped < _scene.ObjectCount ? mapped : -1;
    }

    private static int LassoRetainedObjectCount(MarqueeMaterializationResult materialized)
    {
        return materialized.OldToNewObjectIndex.Count(index => index >= 0);
    }

    private bool IsLassoReplacementObject(
        int objectIndex,
        int retainedCount,
        LassoStackIdentity stack)
    {
        if (objectIndex < retainedCount
            || (uint)objectIndex >= _scene.ObjectCount
            || _scene.ObjectLayer[objectIndex] != stack.Layer
            || _scene.ObjectKeyframeFrame[objectIndex] != stack.Keyframe
            || _scene.ObjectOrder[objectIndex] != stack.Order)
        {
            return false;
        }

        var subOrder = _scene.ObjectSubOrder[objectIndex];
        return subOrder > stack.LowerSubOrder && subOrder < stack.UpperSubOrder;
    }

    private bool LassoObjectGeometryMatches(
        IReadOnlyList<PointF[]> sourceContours,
        int objectIndex)
    {
        if (sourceContours.Count == 0) return true;
        var candidateContours = _scene.GetObjectBoundaryContours(objectIndex);
        if (candidateContours.Length == 0) return false;

        var sourceBounds = LassoContoursBounds(sourceContours);
        var candidateBounds = LassoContoursBounds(candidateContours);
        if (sourceBounds.Right < candidateBounds.Left || candidateBounds.Right < sourceBounds.Left
            || sourceBounds.Bottom < candidateBounds.Top || candidateBounds.Bottom < sourceBounds.Top)
        {
            return false;
        }

        if (!sourceContours.Any(contour => contour.Length >= 3)
            || !candidateContours.Any(contour => contour.Length >= 3))
        {
            return true;
        }

        return candidateContours
                .SelectMany(contour => contour)
                .Any(point => PointInFillPartContours(point, sourceContours))
            || sourceContours
                .SelectMany(contour => contour)
                .Any(point => PointInFillPartContours(point, candidateContours));
    }

    private bool LassoElementGeometryMatches(
        LassoBaseElementSelection selection,
        DrawingElementHit candidate)
    {
        if (selection.FillContours.Length > 0)
        {
            var contours = _scene.GetFillPartContours(candidate, _frame);
            return contours.Length > 0
                && BuildFillPartSelectionProbes(contours)
                    .Any(probe => PointInFillPartContours(probe, selection.FillContours));
        }

        if (selection.Geometry.Length == 0) return true;
        var candidateGeometry = candidate.Key.Kind == DrawingElementKind.Stroke
            ? _scene.GetStrokePartPoints(candidate, _frame)
            : _scene.GetBoundaryPartPoints(candidate, _frame);
        return LassoPolylineGeometryMatches(selection.Geometry, candidateGeometry);
    }

    private static bool LassoPolylineGeometryMatches(
        IReadOnlyList<PointF> source,
        IReadOnlyList<PointF> candidate)
    {
        if (source.Count < 2 || candidate.Count < 2) return false;
        const float tolerance = 4f;
        return candidate.All(point => DistanceToLassoPolyline(point, source) <= tolerance);
    }

    private static float DistanceToLassoPolyline(
        PointF point,
        IReadOnlyList<PointF> polyline)
    {
        var best = float.PositiveInfinity;
        for (var index = 1; index < polyline.Count; index++)
        {
            var start = polyline[index - 1];
            var end = polyline[index];
            var dx = end.X - start.X;
            var dy = end.Y - start.Y;
            var lengthSquared = dx * dx + dy * dy;
            var t = lengthSquared > 0.0001f
                ? ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared
                : 0;
            t = Math.Clamp(t, 0, 1);
            var projected = new PointF(start.X + dx * t, start.Y + dy * t);
            var distanceX = point.X - projected.X;
            var distanceY = point.Y - projected.Y;
            best = Math.Min(best, MathF.Sqrt(distanceX * distanceX + distanceY * distanceY));
        }

        return best;
    }

    private static RectangleF LassoContoursBounds(IReadOnlyList<PointF[]> contours)
    {
        var hasPoint = false;
        var left = float.PositiveInfinity;
        var top = float.PositiveInfinity;
        var right = float.NegativeInfinity;
        var bottom = float.NegativeInfinity;
        foreach (var contour in contours)
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

        return hasPoint ? RectangleF.FromLTRB(left, top, right, bottom) : RectangleF.Empty;
    }

    private IEnumerable<DrawingElementHit> CreateLassoElementHits(
        IEnumerable<int> objectIndices)
    {
        foreach (var index in objectIndices.Distinct())
        {
            foreach (var hit in CreateLassoElementHits(index)) yield return hit;
        }
    }

    private IEnumerable<DrawingElementHit> CreateLassoElementHits(int objectIndex)
    {
        if ((uint)objectIndex >= _scene.ObjectCount
            || !_scene.IsObjectActive(objectIndex, _frame))
        {
            yield break;
        }

        var bounds = _scene.GetObjectWorldBounds(objectIndex);
        bounds.Inflate(1, 1);
        PointF[] polygon =
        [
            new(bounds.Left, bounds.Top), new(bounds.Right, bounds.Top),
            new(bounds.Right, bounds.Bottom), new(bounds.Left, bounds.Bottom)
        ];
        foreach (var hit in _scene.QueryDrawingElementsInsidePolygon(polygon, _frame)
                     .Where(hit => hit.Key.ObjectIndex == objectIndex))
            yield return hit;
    }

    private static bool IsLassoElementKindCompatible(
        DrawingElementKind source,
        DrawingElementKind candidate)
    {
        return source switch
        {
            DrawingElementKind.Fill => candidate == DrawingElementKind.Fill,
            DrawingElementKind.Stroke => candidate == DrawingElementKind.Stroke,
            DrawingElementKind.BoundaryStroke => candidate is DrawingElementKind.BoundaryStroke or DrawingElementKind.Stroke,
            _ => false
        };
    }

    private IReadOnlyList<DrawingObjectInstanceDefinition> FindLassoInstancesInside(
        IReadOnlyList<PointF> worldPolygon)
    {
        if (worldPolygon.Count < 3) return [];
        var compositionScene = ActiveInstanceCompositionScene();
        var compositionResult = ActiveInstanceCompositionResult();
        var instances = ActiveEditableInstances();
        if (instances.Count == 0 || compositionScene.ObjectCount == 0) return [];

        var useMaskClips = UsesFlatSceneMaskSelectionClipping(compositionScene);
        var rootInside = new Dictionary<string, bool>(StringComparer.Ordinal);
        var rootHasVisibleGeometry = new HashSet<string>(StringComparer.Ordinal);
        for (var objectIndex = 0; objectIndex < compositionScene.ObjectCount; objectIndex++)
        {
            if (!compositionResult.TryGetOwner(objectIndex, out var owner)) continue;
            var rootId = string.IsNullOrWhiteSpace(owner.RootInstanceId)
                ? owner.InstanceId
                : owner.RootInstanceId;
            if (string.IsNullOrWhiteSpace(rootId)) continue;

            bool inside;
            if (useMaskClips)
            {
                // A fully clipped object contributes no visible geometry to the
                // instance aggregate and must not make its root appear outside.
                if (!TryGetSceneMaskSelectionVisibleContours(
                        compositionScene,
                        objectIndex,
                        _stage.SceneCompositionMaskClips,
                        out var visibleContours))
                {
                    continue;
                }

                inside = VectorScene.AreContoursFullyInsidePolygon(
                    visibleContours,
                    worldPolygon,
                    closeContours: true);
            }
            else
            {
                inside = compositionScene.IsObjectGeometryInsidePolygon(objectIndex, worldPolygon);
            }
            rootHasVisibleGeometry.Add(rootId);
            rootInside[rootId] = rootInside.TryGetValue(rootId, out var current)
                ? current && inside
                : inside;
        }

        var selected = instances
            .Where(instance => rootHasVisibleGeometry.Contains(instance.Id)
                && rootInside.TryGetValue(instance.Id, out var inside)
                && inside)
            .ToArray();
        if (_lassoAdditiveSelection && _lassoInstanceSelectionBase.Length > 0)
        {
            var baseIds = _lassoInstanceSelectionBase.ToHashSet(StringComparer.Ordinal);
            selected = instances
                .Where(instance => baseIds.Contains(instance.Id) || selected.Contains(instance))
                .ToArray();
        }

        return selected;
    }

    private void ApplyLassoSelection(
        IReadOnlyList<int> objectIndices,
        IReadOnlyList<DrawingElementHit> elements,
        IReadOnlyList<DrawingObjectInstanceDefinition> instances,
        IReadOnlyList<int> baseObjects,
        IReadOnlyList<DrawingElementHit> baseElements)
    {
        var objects = (_lassoAdditiveSelection
                ? baseObjects.Concat(objectIndices)
                : objectIndices)
            .Where(index => (uint)index < _scene.ObjectCount
                && _scene.IsObjectSelectable(index, _frame))
            .Distinct()
            .ToArray();
        var hits = (_lassoAdditiveSelection
                ? baseElements.Concat(elements)
                : elements)
            .Where(hit => hit.IsValid
                && (uint)hit.Key.ObjectIndex < _scene.ObjectCount
                && _scene.IsObjectSelectable(hit.Key.ObjectIndex, _frame)
                && !IsWholeObjectOnlyObject(hit.Key.ObjectIndex))
            .DistinctBy(hit => hit.Key)
            .ToArray();
        var selectedInstances = instances;
        if (_lassoAdditiveSelection && _lassoInstanceSelectionBase.Length > 0)
        {
            var baseIds = _lassoInstanceSelectionBase.ToHashSet(StringComparer.Ordinal);
            selectedInstances = ActiveEditableInstances()
                .Where(instance => baseIds.Contains(instance.Id) || instances.Contains(instance))
                .ToArray();
        }

        ClearSceneInstanceSelection();
        _transformFocus = null;
        _selectedObjects.Clear();
        _selectedElements.Clear();
        _selectedElement = DrawingElementHit.None;
        foreach (var index in objects)
        {
            if (_selectedObjects.Contains(index)) continue;
            _selectedObjects.Add(index);
        }

        foreach (var hit in hits)
        {
            if (!_selectedObjects.Contains(hit.Key.ObjectIndex)) _selectedObjects.Add(hit.Key.ObjectIndex);
            _selectedElements.Add(hit);
        }

        _selectedElement = hits.Length > 0 ? hits[^1] : DrawingElementHit.None;
        _selectedObject = _selectedElement.IsValid
            ? _selectedElement.Key.ObjectIndex
            : _selectedObjects.Count > 0 ? _selectedObjects[^1] : -1;
        SyncSelectionToStage();
        if (selectedInstances.Count > 0)
        {
            SetSceneInstanceSelectionCore(
                selectedInstances,
                selectedInstances[^1],
                preserveDrawingSelection: true);
        }
    }

    private void CancelLassoPointer()
    {
        if (!_lassoPointerActive) return;
        EndLassoPointer();
        _stage.Invalidate();
    }

    private void EndLassoPointer()
    {
        _lassoPointerActive = false;
        _lassoPointerDown = false;
        _lassoScreenPoints.Clear();
        _lassoSelectionBase = [];
        _lassoElementSelectionBase = [];
        _lassoInstanceSelectionBase = [];
        _lassoAdditiveSelection = false;
        _stage.ClearLassoPreview();
        _lastMouse = null;
        _startScreen = null;
        _startWorld = null;
        if (_stage.Capture) _stage.Capture = false;
        RefreshInteractionCursorAtPointer();
    }

    private bool CancelLassoPointerForLifecycle()
    {
        if (!_lassoPointerActive) return false;
        CancelLassoPointer();
        return true;
    }
}
