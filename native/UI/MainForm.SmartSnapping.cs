using System.Numerics;

namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private SmartSnapIndex? _smartMoveIndex;
    private RectangleF _smartMoveStartBounds;
    private SmartSnapResult _smartMovePrevious;
    private SmartSnapIndex? _smartPointIndex;
    private VectorScene? _smartPointScene;
    private long _smartPointRevision = -1;
    private int _smartPointFrame = -1;
    private int _smartPointLayer = -1;
    private int _smartPointSelectionHash;
    private RulerGuide[] _smartPointGuides = [];
    private RectangleF? _smartPointCanvas;
    private SmartSnapIndex? _smartSceneIndex;
    private SpatialTransformEditSession? _smartSceneSession;
    private RectangleF _smartSceneStartBounds;
    private SmartSnapResult _smartScenePrevious;
    private PointF[] _smartMoveSourcePoints = [];
    private PointF? _smartPointLock;
    private SmartSnapResult _smartPointPrevious;
    private SmartSnapIndex? _smartResizeIndex;
    private SmartSnapResult _smartResizePrevious;

    private PointF ResolveSmartResizePointer(PointF pointer, PointF pivot, TransformHandleKind handle, bool proportional)
    {
        if (!SmartSnapActive) return pointer;
        _smartResizeIndex ??= BuildDrawingSnapIndex(_selectedObjects.ToHashSet());
        var result = _smartResizeIndex.Resolve(new(pointer, SizeF.Empty), SmartSnapTolerance,
            _drawSettings.AlignmentEnabled, _drawSettings.AdhesionEnabled,
            _drawSettings.SnapToGuides || _drawSettings.SnapToCanvas, false, _smartResizePrevious,
            _drawSettings.SnapToGrid ? _stage.AdaptiveGridSnapStep : null);
        var x = ChangesTransformWidth(handle) ? result.X : null;
        var y = ChangesTransformHeight(handle) ? result.Y : null;
        if (proportional)
        {
            var dx = pointer.X - pivot.X;
            var dy = pointer.Y - pivot.Y;
            if (x is { } sx && y is { } sy)
            {
                if (Math.Abs(sx.Correction) <= Math.Abs(sy.Correction)) y = null; else x = null;
            }
            if (x is { } ax && Math.Abs(dx) > 0.0001f)
            {
                var ratio = (dx + ax.Correction) / dx;
                pointer = new(pointer.X + ax.Correction, pivot.Y + dy * ratio);
            }
            else if (y is { } ay && Math.Abs(dy) > 0.0001f)
            {
                var ratio = (dy + ay.Correction) / dy;
                pointer = new(pivot.X + dx * ratio, pointer.Y + ay.Correction);
            }
        }
        else pointer = new(pointer.X + (x?.Correction ?? 0), pointer.Y + (y?.Correction ?? 0));
        _smartResizePrevious = new(new(x?.Correction ?? 0, y?.Correction ?? 0), x, y);
        ShowSmartSnapResult(_smartResizePrevious);
        return pointer;
    }

    private Dictionary<string, RectangleF> SmartInstanceBounds()
    {
        var scene = ActiveInstanceCompositionScene();
        var result = ActiveInstanceCompositionResult();
        var bounds = new Dictionary<string, RectangleF>(StringComparer.Ordinal);
        for (var i = 0; i < scene.ObjectCount; i++)
        {
            if (!scene.IsObjectActive(i, _frame) || !result.TryGetOwner(i, out var owner)) continue;
            var id = string.IsNullOrEmpty(owner.RootInstanceId) ? owner.InstanceId : owner.RootInstanceId;
            var b = SmartObjectBounds(scene, i);
            if (UsesFlatSceneMaskSelectionClipping(scene)
                && !TryGetSceneMaskSelectionVisibleBounds(scene, i, _stage.SceneCompositionMaskClips, out b)) continue;
            bounds[id] = bounds.TryGetValue(id, out var old) ? RectangleF.Union(old, b) : b;
        }
        return bounds;
    }

    // Layout alignment belongs to the front 2D plane. Other spatial views retain
    // their existing constrained 3D point snapping instead of flattening depth.
    private Vector3 ResolveSmartSceneMove(Vector3 rawDelta, Func<Vector3, Vector3> constrain)
    {
        var session = _spatialTransformEditSession;
        if (rawDelta.LengthSquared() <= 0.000001f)
        { _smartScenePrevious = default; _stage.ClearSmartSnapLines(); return rawDelta; }
        if (session is null || !SmartSnapActive || ActiveSceneViewDimension() != SceneDimension.TwoD
            || _stage.Reference2DViewDirection != ReferenceViewDirection.Front)
        { _stage.ClearSmartSnapLines(); return rawDelta; }
        if (_smartSceneIndex is null || !ReferenceEquals(session, _smartSceneSession))
        {
            var bounds = SmartInstanceBounds();
            var ids = session.SelectedInstanceIds.ToHashSet(StringComparer.Ordinal);
            RectangleF? source = null;
            foreach (var id in ids) if (bounds.TryGetValue(id, out var b))
                source = source is { } old ? RectangleF.Union(old, b) : b;
            if (source is null) return rawDelta;
            _smartSceneStartBounds = source.Value;
            var targetBounds = bounds.Where(pair => !ids.Contains(pair.Key)).Select(pair => pair.Value).ToArray();
            _smartSceneIndex = new SmartSnapIndex(targetBounds,
                _drawSettings.SnapToGuides ? _stage.CurrentRulerGuides : [], SmartSnapCanvas, targetBounds.SelectMany(SmartBoundsPoints));
            _smartSceneSession = session; _smartScenePrevious = default;
        }
        var moving = _smartSceneStartBounds;
        moving.Offset(rawDelta.X, rawDelta.Y);
        var tolerance = SmartSnapTolerance;
        if (_stage.UsesReferenceProjection
            && TryProjectSceneSnapPoint(new(moving.Left, moving.Top, 0), out var origin)
            && TryProjectSceneSnapPoint(new(moving.Left + 1000, moving.Top, 0), out var axis))
        {
            var scale = Math.Abs(axis.X - origin.X) / 1000;
            if (scale > 0.000001f) tolerance = _drawSettings.SnapTolerancePixels * _stage.DeviceDpi / 96f / scale;
        }
        var result = _smartSceneIndex.Resolve(moving, tolerance, _drawSettings.AlignmentEnabled,
            _drawSettings.AdhesionEnabled, _drawSettings.SnapToGuides || _drawSettings.SnapToCanvas,
            _drawSettings.AlignmentEnabled && _drawSettings.SnapToGaps, _smartScenePrevious,
            _drawSettings.SnapToGrid ? _stage.AdaptiveGridSnapStep : null);
        if (_drawSettings.SnapToObjects && TrySmartPointMove(_smartSceneIndex, SmartBoundsPoints(_smartSceneStartBounds).ToArray(),
            new(rawDelta.X, rawDelta.Y), tolerance, out var pointResult, _smartScenePrevious)) result = pointResult;
        var correction = constrain(new(result.Correction.X, result.Correction.Y, 0));
        if (Math.Abs(correction.X - result.Correction.X) > 0.001f || Math.Abs(correction.Y - result.Correction.Y) > 0.001f)
        { _stage.ClearSmartSnapLines(); return rawDelta; }
        _smartScenePrevious = result; ShowSmartSnapResult(result);
        return rawDelta + correction;
    }

    private float SmartSnapTolerance => _stage.ScreenLengthToWorld(_drawSettings.SnapTolerancePixels * _stage.DeviceDpi / 96f);
    private bool SmartSnapActive => _drawSettings.SnapEnabled
        && (_drawSettings.AdhesionEnabled || _drawSettings.AlignmentEnabled || _drawSettings.SnapToObjects
            || _drawSettings.SnapToGuides || _drawSettings.SnapToCanvas || _drawSettings.SnapToGrid);
    private RectangleF? SmartSnapCanvas => _drawSettings.SnapToCanvas && _stage.CanvasPresentation is not null
        ? new RectangleF(-_stage.CanvasWorldWidth / 2, -_stage.CanvasWorldHeight / 2, _stage.CanvasWorldWidth, _stage.CanvasWorldHeight) : null;

    // Visual bounds include the stroke, but remove the query system's artificial 1-vu hit margin.
    private static RectangleF SmartObjectBounds(VectorScene scene, int index)
    {
        var b = scene.GetObjectWorldBounds(index);
        var artificialMargin = Math.Max(scene.Stroke[index] * 0.5f, 1) - scene.Stroke[index] * 0.5f;
        if (artificialMargin > 0 && b.Width >= 2 * artificialMargin && b.Height >= 2 * artificialMargin) b.Inflate(-artificialMargin, -artificialMargin);
        return b;
    }

    private SmartSnapIndex BuildDrawingSnapIndex(HashSet<int> excluded)
    {
        var bounds = new List<RectangleF>();
        var points = new List<PointF>();
        for (var i = 0; i < _scene.ObjectCount; i++)
        {
            if (excluded.Contains(i) || !_scene.IsObjectActive(i, _frame)
                || !_scene.IsLayerEffectivelyVisible(_scene.ObjectLayer[i])) continue;
            var b = SmartObjectBounds(_scene, i);
            bounds.Add(b);
            points.AddRange(SmartBoundsPoints(b));
            if (_scene.TryGetLineEndpoint(i, true, out var start)) points.Add(_scene.MapObjectPoint(i, start));
            if (_scene.TryGetLineEndpoint(i, false, out var end)) points.Add(_scene.MapObjectPoint(i, end));
            if (_scene.TryGetPathBezierWorldContours(i, out var bezier))
                foreach (var contour in bezier) points.AddRange(contour.Select(node => _scene.MapObjectPoint(i, node.Anchor)));
            else if (_scene.TryGetPathWorldContours(i, out var contours))
                foreach (var contour in contours) points.AddRange(contour.Select(point => _scene.MapObjectPoint(i, point)));
            if (_scene.TryGetFreehandBezierWorldNodes(i, out var nodes)) points.AddRange(nodes.Select(node => _scene.MapObjectPoint(i, node.Anchor)));
        }
        return new(bounds, _drawSettings.SnapToGuides ? _stage.CurrentRulerGuides : [], SmartSnapCanvas, points);
    }

    private PointF ResolveSmartDrawingPoint(PointF point)
        => ResolveSmartDrawingPointCore(point, null);

    private PointF ResolveSmartDrawingPointCore(PointF point, HashSet<int>? additionalExcluded)
    {
        if (!SmartSnapActive) { _stage.ClearSmartSnapLines(); return _drawSettings.SnapPoint(point, _stage.AdaptiveGridSnapStep); }
        var excluded = _lastMouse is not null ? _scene.ExpandMotionTransformObjects(_selectedObjects, _frame).ToHashSet() : [];
        if (additionalExcluded is not null) excluded.UnionWith(additionalExcluded);
        var hash = 17;
        foreach (var id in excluded.Order()) hash = unchecked(hash * 31 + id);
        var guides = _stage.CurrentRulerGuides;
        if (_smartPointIndex is null || !ReferenceEquals(_smartPointScene, _scene)
            || _lastMouse is null && _smartPointRevision != _scene.GeometryRevision || _smartPointFrame != _frame
            || _smartPointLayer != _scene.ActiveLayer || _smartPointSelectionHash != hash
            || !_smartPointGuides.SequenceEqual(guides) || _smartPointCanvas != SmartSnapCanvas)
        {
            _smartPointIndex = BuildDrawingSnapIndex(excluded);
            _smartPointScene = _scene; _smartPointRevision = _scene.GeometryRevision;
            _smartPointFrame = _frame; _smartPointLayer = _scene.ActiveLayer; _smartPointSelectionHash = hash;
            _smartPointGuides = guides.ToArray(); _smartPointCanvas = SmartSnapCanvas;
            _smartPointPrevious = default; _smartPointLock = null;
        }
        if (_drawSettings.SnapToObjects && _smartPointLock is { } locked
            && Distance(point, locked) <= SmartSnapTolerance * 1.5f) return locked;
        _smartPointLock = null;
        if (_drawSettings.SnapToObjects && _smartPointIndex.TryPoint(point, SmartSnapTolerance, out var snapped))
        {
            _smartPointLock = snapped;
            var r = SmartSnapTolerance / 2;
            _stage.SetSmartSnapLines([new(new(snapped.X-r, snapped.Y), new(snapped.X+r, snapped.Y)),
                new(new(snapped.X, snapped.Y-r), new(snapped.X, snapped.Y+r))]);
            return snapped;
        }
        var result = _smartPointIndex.Resolve(new(point, SizeF.Empty), SmartSnapTolerance,
            _drawSettings.AlignmentEnabled, _drawSettings.AdhesionEnabled,
            _drawSettings.SnapToGuides || _drawSettings.SnapToCanvas, false, _smartPointPrevious,
            gridStep: _drawSettings.SnapToGrid ? _stage.AdaptiveGridSnapStep : null);
        _smartPointPrevious = result;
        ShowSmartSnapResult(result);
        return VectorUnits.Quantize(new PointF(point.X + result.Correction.X, point.Y + result.Correction.Y));
    }

    private PointF ResolveSmartMovePointer(PointF world, PointF pointerStart, RectangleF? explicitBounds = null)
    {
        if (world == pointerStart) { _smartMovePrevious = default; _stage.ClearSmartSnapLines(); return world; }
        if (!SmartSnapActive && !(_drawSettings.SnapEnabled && _drawSettings.SnapToGrid))
        { _stage.ClearSmartSnapLines(); _smartMovePrevious = default; return world; }
        if (_smartMoveIndex is null)
        {
            var excluded = _scene.ExpandMotionTransformObjects(_selectedObjects, _frame).ToHashSet();
            RectangleF? bounds = explicitBounds;
            if (bounds is null) foreach (var i in excluded)
            {
                if ((uint)i >= _scene.ObjectCount) continue;
                var b = TryGetSelectedMixingPartBounds(i, out var part) ? part : SmartObjectBounds(_scene, i);
                bounds = bounds is { } old ? RectangleF.Union(old, b) : b;
            }
            if (bounds is null) return world;
            _smartMoveStartBounds = bounds.Value;
            var sourcePoints = SmartBoundsPoints(bounds.Value).ToList();
            foreach (var i in excluded)
            {
                if (_scene.TryGetLineEndpoint(i, true, out var start)) sourcePoints.Add(start);
                if (_scene.TryGetLineEndpoint(i, false, out var end)) sourcePoints.Add(end);
            }
            _smartMoveSourcePoints = sourcePoints.Distinct().ToArray();
            _smartMoveIndex = BuildDrawingSnapIndex(excluded);
        }
        var moving = _smartMoveStartBounds;
        moving.Offset(world.X - pointerStart.X, world.Y - pointerStart.Y);
        if (_drawSettings.SnapToObjects && TrySmartPointMove(_smartMoveIndex, _smartMoveSourcePoints,
            new(world.X - pointerStart.X, world.Y - pointerStart.Y), SmartSnapTolerance, out var pointResult, _smartMovePrevious))
        {
            _smartMovePrevious = pointResult;
            ShowSmartSnapResult(pointResult);
            return VectorUnits.Quantize(new PointF(world.X + pointResult.Correction.X, world.Y + pointResult.Correction.Y));
        }
        var result = _smartMoveIndex.Resolve(moving, SmartSnapTolerance,
            _drawSettings.AlignmentEnabled, _drawSettings.AdhesionEnabled,
            _drawSettings.SnapToGuides || _drawSettings.SnapToCanvas,
            _drawSettings.AlignmentEnabled && _drawSettings.SnapToGaps, _smartMovePrevious,
            _drawSettings.SnapToGrid ? _stage.AdaptiveGridSnapStep : null);
        _smartMovePrevious = result;
        ShowSmartSnapResult(result);
        return VectorUnits.Quantize(new PointF(world.X + result.Correction.X, world.Y + result.Correction.Y));
    }

    private void ShowSmartSnapResult(SmartSnapResult result)
    {
        var lines = new List<SmartSnapLine>();
        if (result.X is { Kind: not SmartSnapKind.Grid } x) lines.Add(x.Line);
        if (result.Y is { Kind: not SmartSnapKind.Grid } y) lines.Add(y.Line);
        if (result.X?.Comparison is { } xc) lines.Add(xc);
        if (result.Y?.Comparison is { } yc) lines.Add(yc);
        _stage.SetSmartSnapLines(lines);
    }
    private void ResetSmartSnapping()
    {
        _smartMoveIndex = null; _smartPointIndex = null; _smartMovePrevious = default;
        _smartMoveSourcePoints = []; _smartPointLock = null; _smartPointPrevious = default;
        _smartResizeIndex = null; _smartResizePrevious = default;
        _smartSceneIndex = null; _smartSceneSession = null; _smartScenePrevious = default;
        _stage.ClearSmartSnapLines();
    }

    private static IEnumerable<PointF> SmartBoundsPoints(RectangleF b)
    {
        for (var x = 0; x < 3; x++) for (var y = 0; y < 3; y++)
            yield return new(b.Left + b.Width * x / 2, b.Top + b.Height * y / 2);
    }

    private static bool TrySmartPointMove(SmartSnapIndex index, IReadOnlyList<PointF> sources, PointF delta,
        float tolerance, out SmartSnapResult result, SmartSnapResult previous = default)
    {
        result = default;
        if (previous.X is { Kind: SmartSnapKind.Point } oldX && previous.Y is { Kind: SmartSnapKind.Point } oldY)
        {
            var dx = oldX.Coordinate - oldX.SourceOffset - delta.X;
            var dy = oldY.Coordinate - oldY.SourceOffset - delta.Y;
            if (dx * dx + dy * dy <= tolerance * tolerance * 2.25f)
            {
                result = new(new(dx, dy), oldX with { Correction = dx }, oldY with { Correction = dy });
                return true;
            }
        }
        var best = tolerance;
        foreach (var source in sources)
        {
            var current = new PointF(source.X + delta.X, source.Y + delta.Y);
            if (!index.TryPoint(current, tolerance, out var target)) continue;
            var distance = Distance(current, target);
            if (result.Snapped && distance >= best) continue;
            best = distance;
            var correction = new PointF(target.X - current.X, target.Y - current.Y);
            var r = tolerance / 2;
            result = new(correction, new SmartSnapAxis(correction.X, target.X, source.X, SmartSnapKind.Point, -1,
                new(new(target.X - r, target.Y), new(target.X + r, target.Y))),
                new SmartSnapAxis(correction.Y, target.Y, source.Y, SmartSnapKind.Point, -1,
                new(new(target.X, target.Y - r), new(target.X, target.Y + r))));
        }
        return result.Snapped;
    }
}
