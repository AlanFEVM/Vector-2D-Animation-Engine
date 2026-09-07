using System.Numerics;

namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private const float SnapPointHitRadiusPixels = 10f;
    private SnapPointEditSession? _snapPointEditSession;
    private string _hoveredSnapPointId = "";
    private PointF? _snapPointCandidate;
    private bool _snapPointCandidateSnapped;
    private PointF? _sceneSnapMoveStartPointer;
    private SceneSnapQueryCache? _sceneSnapQueryCache;
    private int _sceneSnapCandidateCacheBuildCount;
    private int _sceneSnapProjectionCacheBuildCount;
    private int _lastSceneSnapTargetPointCount;
    private int _lastSceneSnapCandidateCheckCount;

    private sealed class SnapPointEditSession
    {
        public required DrawingObjectDefinition DrawingObject { get; init; }
        public required DrawingObjectSnapPointDefinition[] Snapshot { get; init; }
        public required VectorSceneSnapshot CanvasSnapshot { get; init; }
        public required string SnapPointId { get; init; }
        public required bool WasProjectDirty { get; init; }
        public bool Changed { get; set; }
    }

    private sealed class SceneSnapQueryCache
    {
        public required SpatialTransformEditSession Session { get; init; }
        public required int Frame { get; init; }
        public required SceneSnapSourcePoint[] SourcePoints { get; init; }
        public required SceneSnapTargetPoint[] TargetPoints { get; init; }
        public SceneSnapProjectionKey ProjectionKey { get; set; }
        public bool ProjectionReady { get; set; }
        public Dictionary<SceneSnapGridCell, List<SceneSnapProjectedTarget>> TargetBuckets { get; set; } = [];
    }

    private readonly record struct SceneSnapSourcePoint(Vector3 StartWorld);

    private readonly record struct SceneSnapTargetPoint(Vector3 World, int Order);

    private readonly record struct SceneSnapProjectedTarget(Vector3 World, PointF Screen, int Order);

    private readonly record struct SceneSnapGridCell(int X, int Y);

    private readonly record struct SceneSnapProjectionKey(
        int Width,
        int Height,
        int Dpi,
        bool UsesReferenceProjection,
        SceneDimension ReferenceDimension,
        ReferenceViewDirection ReferenceDirection,
        CameraProjection Projection,
        float ProjectionBlend,
        float CameraX,
        float CameraY,
        float Zoom,
        float ReferenceYaw,
        float ReferencePitch,
        float ReferenceDistance,
        float ReferenceZoomScale,
        float ReferenceTargetX,
        float ReferenceTargetY,
        float ReferenceTargetZ);

    private void BeginSnapPointPointer(MouseEventArgs e)
    {
        var drawingObject = ActiveDrawingObject();
        if (_workspaceTabs.SelectedView != WorkspaceView.BasicDrawing
            || drawingObject is null
            || DrawingToolsBlocked())
        {
            return;
        }

        var hit = HitTestSnapPoint(drawingObject, e.Location);
        if (e.Button == MouseButtons.Right)
        {
            if (hit is { } deletePoint)
            {
                var snapshot = drawingObject.CreateSnapPointSnapshot();
                var canvasSnapshot = _scene.CreateSnapshot();
                if (_project.TryRemoveDrawingObjectSnapPoint(drawingObject.Id, deletePoint.Id))
                {
                    PushUndoSnapshot(
                        canvasSnapshot,
                        drawingObject,
                        snapPointSnapshot: snapshot);
                    _hoveredSnapPointId = "";
                    UpdateSnapPointOverlay();
                }
            }
            return;
        }

        if (e.Button != MouseButtons.Left) return;
        var wasProjectDirty = _projectDirty;
        var sharedSnapshot = drawingObject.CreateSnapPointSnapshot();
        var sceneSnapshot = _scene.CreateSnapshot();
        var point = hit;
        var added = false;
        if (point is null)
        {
            var position = ResolveSnapPointPlacement(e.Location, out _);
            if (!_project.TryAddDrawingObjectSnapPoint(
                    drawingObject.Id,
                    position.X,
                    position.Y,
                    0,
                    out var created))
            {
                return;
            }
            point = created;
            added = true;
        }

        _snapPointEditSession = new SnapPointEditSession
        {
            DrawingObject = drawingObject,
            Snapshot = sharedSnapshot,
            CanvasSnapshot = sceneSnapshot,
            SnapPointId = point.Value.Id,
            WasProjectDirty = wasProjectDirty,
            Changed = added
        };
        _hoveredSnapPointId = point.Value.Id;
        _snapPointCandidate = null;
        _lastMouse = e.Location;
        _stage.Capture = true;
        UpdateSnapPointOverlay();
        _stage.Cursor = Cursors.SizeAll;
    }

    private void UpdateSnapPointPointer(Point screen)
    {
        var session = _snapPointEditSession;
        if (session is null) return;
        var position = ResolveSnapPointPlacement(screen, out var snapped);
        var current = session.DrawingObject.SnapPoints.FirstOrDefault(point =>
            string.Equals(point.Id, session.SnapPointId, StringComparison.Ordinal));
        if (string.IsNullOrEmpty(current.Id)) return;
        if (current.X != position.X || current.Y != position.Y || current.Z != 0)
        {
            session.Changed |= _project.TryMoveDrawingObjectSnapPoint(
                session.DrawingObject.Id,
                session.SnapPointId,
                position.X,
                position.Y,
                0);
        }
        _snapPointCandidate = position;
        _snapPointCandidateSnapped = snapped;
        _lastMouse = screen;
        UpdateSnapPointOverlay();
    }

    private void CompleteSnapPointPointer()
    {
        var session = _snapPointEditSession;
        _snapPointEditSession = null;
        if (session is not null && session.Changed)
        {
            PushUndoSnapshot(
                session.CanvasSnapshot,
                session.DrawingObject,
                snapPointSnapshot: session.Snapshot);
        }
        _snapPointCandidate = null;
        _lastMouse = null;
        if (_stage.Capture) _stage.Capture = false;
        UpdateSnapPointOverlay();
    }

    private void CancelSnapPointPointer(bool restore)
    {
        var session = _snapPointEditSession;
        _snapPointEditSession = null;
        if (restore && session is not null && session.Changed)
        {
            session.DrawingObject.RestoreSnapPointSnapshot(session.Snapshot);
            InvalidateSceneCompositionCache();
            SetProjectDirty(session.WasProjectDirty);
        }
        _snapPointCandidate = null;
        _lastMouse = null;
        if (_stage.Capture) _stage.Capture = false;
        UpdateSnapPointOverlay();
    }

    private void UpdateSnapPointHover(Point screen)
    {
        var drawingObject = ActiveDrawingObject();
        if (_tool != ToolMode.SnapPoint || drawingObject is null)
        {
            ClearSnapPointPresentation();
            return;
        }

        var hit = HitTestSnapPoint(drawingObject, screen);
        _hoveredSnapPointId = hit?.Id ?? "";
        if (hit is null)
        {
            _snapPointCandidate = ResolveSnapPointPlacement(screen, out _snapPointCandidateSnapped);
        }
        else
        {
            _snapPointCandidate = null;
            _snapPointCandidateSnapped = false;
        }
        UpdateSnapPointOverlay();
        _stage.Cursor = hit is null ? Cursors.Cross : Cursors.SizeAll;
    }

    private DrawingObjectSnapPointDefinition? HitTestSnapPoint(
        DrawingObjectDefinition drawingObject,
        Point screen)
    {
        DrawingObjectSnapPointDefinition? nearest = null;
        var bestDistanceSquared = SnapPointHitRadiusPixels * SnapPointHitRadiusPixels;
        foreach (var point in drawingObject.SnapPoints)
        {
            var projected = _stage.WorldToScreen(point.X, point.Y);
            var dx = projected.X - screen.X;
            var dy = projected.Y - screen.Y;
            var distanceSquared = dx * dx + dy * dy;
            if (distanceSquared > bestDistanceSquared) continue;
            bestDistanceSquared = distanceSquared;
            nearest = point;
        }
        return nearest;
    }

    private PointF ResolveSnapPointPlacement(Point screen, out bool snapped)
    {
        var world = _stage.ScreenToWorld(screen);
        var tolerance = Math.Max(0.001f, _stage.ScreenLengthToWorld(SnapPointHitRadiusPixels));
        var toleranceSquared = tolerance * tolerance;
        var endpoint = PointF.Empty;
        var endpointDistance = toleranceSquared;
        for (var objectIndex = 0; objectIndex < _scene.ObjectCount; objectIndex++)
        {
            if (!_scene.IsObjectSelectable(objectIndex, _frame)
                || _scene.ShapeKind[objectIndex] != ShapeKind.Line)
            {
                continue;
            }
            ConsiderEndpoint(objectIndex, startEndpoint: true);
            ConsiderEndpoint(objectIndex, startEndpoint: false);
        }
        if (endpointDistance < toleranceSquared)
        {
            snapped = true;
            return VectorUnits.Quantize(endpoint);
        }

        var boundary = PointF.Empty;
        var boundaryDistance = toleranceSquared;
        for (var objectIndex = 0; objectIndex < _scene.ObjectCount; objectIndex++)
        {
            if (!_scene.IsObjectSelectable(objectIndex, _frame) || !_scene.HasFill(objectIndex)) continue;
            foreach (var contour in _scene.GetObjectBoundaryContours(objectIndex))
            {
                if (contour.Length < 2) continue;
                for (var index = 0; index < contour.Length; index++)
                {
                    var candidate = ClosestPointOnSnapSegment(
                        world,
                        contour[index],
                        contour[(index + 1) % contour.Length]);
                    var distance = SnapPointSquaredDistance(world, candidate);
                    if (distance >= boundaryDistance) continue;
                    boundaryDistance = distance;
                    boundary = candidate;
                }
            }
        }
        snapped = boundaryDistance < toleranceSquared;
        return VectorUnits.Quantize(snapped ? boundary : world);

        void ConsiderEndpoint(int objectIndex, bool startEndpoint)
        {
            if (!_scene.TryGetLineEndpoint(objectIndex, startEndpoint, out var candidate)) return;
            var distance = SnapPointSquaredDistance(world, candidate);
            if (distance >= endpointDistance) return;
            endpointDistance = distance;
            endpoint = candidate;
        }
    }

    private static PointF ClosestPointOnSnapSegment(PointF point, PointF start, PointF end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var lengthSquared = dx * dx + dy * dy;
        if (lengthSquared <= 0.000001f) return start;
        var parameter = Math.Clamp(
            ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared,
            0,
            1);
        return new PointF(start.X + dx * parameter, start.Y + dy * parameter);
    }

    private static float SnapPointSquaredDistance(PointF first, PointF second)
    {
        var dx = first.X - second.X;
        var dy = first.Y - second.Y;
        return dx * dx + dy * dy;
    }

    private void UpdateSnapPointOverlay()
    {
        var drawingObject = ActiveDrawingObject();
        if (_tool != ToolMode.SnapPoint
            || _workspaceTabs.SelectedView != WorkspaceView.BasicDrawing
            || drawingObject is null)
        {
            ClearSnapPointPresentation();
            return;
        }

        _stage.SetSnapPointOverlay(
            drawingObject.SnapPoints.Select(point => new SnapPointOverlayEntry(point.Id, point.Position)),
            _hoveredSnapPointId,
            _snapPointEditSession?.SnapPointId,
            _snapPointCandidate,
            _snapPointCandidateSnapped);
    }

    private void ClearSnapPointPresentation()
    {
        _hoveredSnapPointId = "";
        _snapPointCandidate = null;
        _snapPointCandidateSnapped = false;
        _stage.ClearSnapPointOverlay();
    }

    private Vector3 ResolveSceneSnapDelta(
        Vector3 rawDelta,
        Func<Vector3, Vector3> constrainCorrection,
        bool? snapRequested = null)
    {
        ArgumentNullException.ThrowIfNull(constrainCorrection);
        _lastSceneSnapCandidateCheckCount = 0;
        var session = _spatialTransformEditSession;
        if (!(snapRequested ?? IsShiftPressed())
            || session is null
            || session.SelectedInstanceIds.Length == 0)
        {
            _stage.ClearSceneSnapIndicator();
            return rawDelta;
        }

        var cache = ResolveSceneSnapQueryCache(session);
        _lastSceneSnapTargetPointCount = cache.TargetPoints.Length;
        if (cache.SourcePoints.Length == 0 || cache.TargetPoints.Length == 0)
        {
            _stage.ClearSceneSnapIndicator();
            return rawDelta;
        }
        EnsureSceneSnapProjectionCache(cache);
        if (cache.TargetBuckets.Count == 0)
        {
            _stage.ClearSceneSnapIndicator();
            return rawDelta;
        }

        const float thresholdSquared = SnapPointHitRadiusPixels * SnapPointHitRadiusPixels;
        const float constrainedAlignmentSquared = 4f;
        var bestDistanceSquared = thresholdSquared;
        var bestCandidateOrder = -1L;
        var found = false;
        var bestCorrection = Vector3.Zero;
        var bestSource = Vector3.Zero;
        var bestTarget = Vector3.Zero;

        for (var sourceIndex = 0; sourceIndex < cache.SourcePoints.Length; sourceIndex++)
        {
            var sourceWorld = cache.SourcePoints[sourceIndex].StartWorld + rawDelta;
            if (!TryProjectSceneSnapPoint(sourceWorld, out var sourceScreen)
                || !TryResolveSceneSnapGridCell(sourceScreen, out var sourceCell))
            {
                continue;
            }

            for (var cellY = -1; cellY <= 1; cellY++)
            {
                for (var cellX = -1; cellX <= 1; cellX++)
                {
                    var cell = new SceneSnapGridCell(sourceCell.X + cellX, sourceCell.Y + cellY);
                    if (!cache.TargetBuckets.TryGetValue(cell, out var targets)) continue;
                    foreach (var target in targets)
                    {
                        _lastSceneSnapCandidateCheckCount++;
                        var screenDistanceSquared = SnapPointSquaredDistance(sourceScreen, target.Screen);
                        var candidateOrder = (long)sourceIndex * cache.TargetPoints.Length + target.Order;
                        if (screenDistanceSquared > bestDistanceSquared
                            || screenDistanceSquared == bestDistanceSquared
                                && candidateOrder <= bestCandidateOrder)
                        {
                            continue;
                        }

                        var correction = constrainCorrection(target.World - sourceWorld);
                        if (!FiniteSnapVector(correction)) continue;
                        var correctedSource = sourceWorld + correction;
                        if (!TryProjectSceneSnapPoint(correctedSource, out var correctedScreen)
                            || SnapPointSquaredDistance(correctedScreen, target.Screen) > constrainedAlignmentSquared)
                        {
                            continue;
                        }

                        found = true;
                        bestDistanceSquared = screenDistanceSquared;
                        bestCandidateOrder = candidateOrder;
                        bestCorrection = correction;
                        bestSource = sourceWorld;
                        bestTarget = target.World;
                    }
                }
            }
        }

        if (!found)
        {
            _stage.ClearSceneSnapIndicator();
            return rawDelta;
        }
        _stage.SetSceneSnapIndicator(bestSource, bestTarget);
        return rawDelta + bestCorrection;
    }

    private SceneSnapQueryCache ResolveSceneSnapQueryCache(SpatialTransformEditSession session)
    {
        if (_sceneSnapQueryCache is { } cached
            && ReferenceEquals(cached.Session, session)
            && cached.Frame == _frame)
        {
            return cached;
        }

        var selectedIds = session.SelectedInstanceIds.ToHashSet(StringComparer.Ordinal);
        var visibleTargets = FindVisibleCompositionInstances(
                _sceneCompositionResult,
                session.Scene.Instances)
            .Where(instance => !selectedIds.Contains(instance.Id))
            .ToArray();
        var definitionsById = new Dictionary<string, DrawingObjectDefinition>(StringComparer.Ordinal);
        foreach (var definition in _drawingObjects)
        {
            definitionsById[definition.Id] = definition;
        }
        var instancesById = session.Scene.Instances.ToDictionary(instance => instance.Id, StringComparer.Ordinal);
        var sourcePoints = new List<SceneSnapSourcePoint>();
        foreach (var instanceId in session.SelectedInstanceIds)
        {
            if (!instancesById.TryGetValue(instanceId, out var sourceInstance)
                || !definitionsById.TryGetValue(sourceInstance.DrawingObjectId, out var sourceDefinition)
                || !session.StartStates.TryGetValue(instanceId, out var startState))
            {
                continue;
            }

            foreach (var sourcePoint in sourceDefinition.SnapPoints)
            {
                sourcePoints.Add(new SceneSnapSourcePoint(
                    DrawingObjectSnapPointMath.Transform(sourceDefinition, startState, sourcePoint)));
            }
        }

        var targetPoints = new List<SceneSnapTargetPoint>();
        foreach (var targetInstance in visibleTargets)
        {
            if (!definitionsById.TryGetValue(targetInstance.DrawingObjectId, out var targetDefinition)) continue;
            var targetState = targetInstance.EvaluateState(_frame);
            if (!targetState.Visible) continue;
            foreach (var targetPoint in targetDefinition.SnapPoints)
            {
                targetPoints.Add(new SceneSnapTargetPoint(
                    DrawingObjectSnapPointMath.Transform(targetDefinition, targetState, targetPoint),
                    targetPoints.Count));
            }
        }

        cached = new SceneSnapQueryCache
        {
            Session = session,
            Frame = _frame,
            SourcePoints = sourcePoints.ToArray(),
            TargetPoints = targetPoints.ToArray()
        };
        _sceneSnapQueryCache = cached;
        _sceneSnapCandidateCacheBuildCount++;
        return cached;
    }

    private void ClearSceneSnapQueryCache()
    {
        _sceneSnapQueryCache = null;
        _lastSceneSnapTargetPointCount = 0;
        _lastSceneSnapCandidateCheckCount = 0;
    }

    private void EnsureSceneSnapProjectionCache(SceneSnapQueryCache cache)
    {
        var projectionKey = CaptureSceneSnapProjectionKey();
        if (cache.ProjectionReady && cache.ProjectionKey == projectionKey) return;

        var buckets = new Dictionary<SceneSnapGridCell, List<SceneSnapProjectedTarget>>();
        foreach (var target in cache.TargetPoints)
        {
            if (!TryProjectSceneSnapPoint(target.World, out var screen)
                || !TryResolveSceneSnapGridCell(screen, out var cell))
            {
                continue;
            }
            if (!buckets.TryGetValue(cell, out var bucket))
            {
                bucket = [];
                buckets.Add(cell, bucket);
            }
            bucket.Add(new SceneSnapProjectedTarget(target.World, screen, target.Order));
        }

        cache.ProjectionKey = projectionKey;
        cache.ProjectionReady = true;
        cache.TargetBuckets = buckets;
        _sceneSnapProjectionCacheBuildCount++;
    }

    private SceneSnapProjectionKey CaptureSceneSnapProjectionKey()
    {
        return new SceneSnapProjectionKey(
            _stage.Width,
            _stage.Height,
            _stage.DeviceDpi,
            _stage.UsesReferenceProjection,
            _stage.ReferenceDimension,
            _stage.Reference2DViewDirection,
            _stage.EffectiveReferenceProjection,
            _stage.ReferenceProjectionBlend,
            _stage.CameraX,
            _stage.CameraY,
            _stage.Zoom,
            _stage.EffectiveReferenceYaw,
            _stage.EffectiveReferencePitch,
            _stage.ReferenceDistance,
            _stage.ReferenceZoomScale,
            _stage.ReferenceTargetX,
            _stage.ReferenceTargetY,
            _stage.ReferenceTargetZ);
    }

    private static bool TryResolveSceneSnapGridCell(PointF screen, out SceneSnapGridCell cell)
    {
        var x = MathF.Floor(screen.X / SnapPointHitRadiusPixels);
        var y = MathF.Floor(screen.Y / SnapPointHitRadiusPixels);
        if (!float.IsFinite(x)
            || !float.IsFinite(y)
            || x <= int.MinValue + 1f
            || x >= int.MaxValue - 1f
            || y <= int.MinValue + 1f
            || y >= int.MaxValue - 1f)
        {
            cell = default;
            return false;
        }
        cell = new SceneSnapGridCell((int)x, (int)y);
        return true;
    }

    private bool TryProjectSceneSnapPoint(Vector3 world, out PointF screen)
    {
        if (_stage.UsesReferenceProjection)
        {
            return _stage.TryProjectScenePosition(world, out screen, out _);
        }
        screen = _stage.WorldToScreen(world.X, world.Y);
        return float.IsFinite(screen.X) && float.IsFinite(screen.Y);
    }

    private static bool FiniteSnapVector(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
