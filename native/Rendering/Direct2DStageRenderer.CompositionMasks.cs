using System.Numerics;
using Vortice.Direct2D1;

namespace VectorAnimationEngine;

internal sealed partial class Direct2DStageRenderer
{
    private CompositionMaskGeometries? _retainedCompositionMasks;
    private (VectorScene Scene, SceneCompositionMaskPlan Plan, int Frame, long Geometry, long Active,
        long Summary, System.Drawing.PointF Origin, System.Drawing.PointF Right, System.Drawing.PointF Down) _retainedMaskKey;

    private CompositionMaskGeometries? GetCompositionMasks(StageControl stage, VectorScene scene, SceneCompositionMaskPlan? plan)
    {
        if (plan is null) return null;
        var key = (scene, plan, stage.Frame, scene.GeometryRevision, scene.ActiveContentRevision,
            scene.SummaryRevision, stage.WorldToScreen(0, 0), stage.WorldToScreen(1, 0), stage.WorldToScreen(0, 1));
        if (_retainedCompositionMasks is not null && _retainedMaskKey.Equals(key)) return _retainedCompositionMasks;
        ClearRetainedCompositionMasks();
        _retainedMaskKey = key;
        return _retainedCompositionMasks = new CompositionMaskGeometries(this, stage, scene, plan);
    }

    private void ClearRetainedCompositionMasks()
    {
        _retainedCompositionMasks?.Dispose();
        _retainedCompositionMasks = null;
        _retainedMaskKey = default;
    }

    private sealed class CompositionMaskGeometries : IDisposable
    {
        private readonly Direct2DStageRenderer _renderer;
        private readonly StageControl _stage;
        private readonly VectorScene _scene;
        private readonly SceneCompositionMaskPlan _plan;
        private readonly bool _referenceProjection;
        private readonly Dictionary<int, List<int>> _objects = [];
        private readonly Dictionary<int, ID2D1PathGeometry> _coverage = [];
        private readonly HashSet<int> _visiting = [];

        internal CompositionMaskGeometries(Direct2DStageRenderer renderer, StageControl stage,
            VectorScene scene, SceneCompositionMaskPlan plan, bool referenceProjection = false)
        {
            _renderer = renderer;
            _stage = stage;
            _scene = scene;
            _plan = plan;
            _referenceProjection = referenceProjection;
            for (var index = 0; index < scene.ObjectCount; index++)
            {
                if (!scene.IsObjectActive(index, stage.Frame)) continue;
                var layer = scene.ObjectLayer[index];
                if (!_objects.TryGetValue(layer, out var objects)) _objects.Add(layer, objects = []);
                objects.Add(index);
            }
        }

        private ID2D1PathGeometry Empty()
        {
            var path = _renderer._factory!.CreatePathGeometry();
            try { using var sink = path.Open(); sink.Close(); return path; }
            catch { path.Dispose(); throw; }
        }

        private ID2D1PathGeometry Combine(ID2D1Geometry left, ID2D1Geometry right, CombineMode mode)
        {
            var path = _renderer._factory!.CreatePathGeometry();
            try
            {
                using var sink = path.Open();
                left.CombineWithGeometry(right, mode, sink);
                sink.Close();
                return path;
            }
            catch { path.Dispose(); throw; }
        }

        internal ID2D1PathGeometry? CreateLayerClip(int layer)
        {
            if (!_plan.ClipsByLayer.TryGetValue(layer, out var masks)) return null;
            ID2D1PathGeometry? result = null;
            try
            {
                foreach (var mask in masks)
                {
                    var coverage = Coverage(mask);
                    // Start from an owned copy; cached coverage must remain immutable.
                    using var empty = result is null ? Empty() : null;
                    var next = Combine(result ?? empty!, coverage,
                        result is null ? CombineMode.Union : CombineMode.Intersect);
                    result?.Dispose();
                    result = next;
                }
                return result;
            }
            catch { result?.Dispose(); throw; }
        }

        private ID2D1PathGeometry Coverage(int mask)
        {
            if (_coverage.TryGetValue(mask, out var cached)) return cached;
            if (!_plan.ContentsByMaskLayer.TryGetValue(mask, out var layers) || !_visiting.Add(mask))
                throw new InvalidDataException("Missing or cyclic composed mask coverage.");
            var result = Empty();
            try
            {
                foreach (var layer in layers)
                {
                    if ((uint)layer >= _scene.LayerCount)
                        throw new InvalidDataException("Invalid composed mask layer.");
                    var contents = Empty();
                    try
                    {
                        foreach (var index in _objects.GetValueOrDefault(layer) ?? [])
                        {
                            var paths = _referenceProjection
                                ? _stage.CreateProjectedCompositionMaskObjectPaths(index)
                                : _stage.CreateCompositionMaskObjectPaths(_scene, index);
                            try
                            {
                                foreach (var objectPath in paths)
                                {
                                    using var geometry = _renderer.CreateCompositionMaskGeometry(objectPath);
                                    var next = Combine(contents, geometry, CombineMode.Union);
                                    contents.Dispose();
                                    contents = next;
                                }
                            }
                            finally { foreach (var path in paths) path.Dispose(); }
                        }
                        using var clip = CreateLayerClip(layer);
                        if (clip is not null)
                        {
                            var clipped = Combine(contents, clip, CombineMode.Intersect);
                            contents.Dispose();
                            contents = clipped;
                        }
                        var combined = Combine(result, contents, CombineMode.Union);
                        result.Dispose();
                        result = combined;
                    }
                    finally { contents.Dispose(); }
                }
                _coverage.Add(mask, result);
                return result;
            }
            catch { result.Dispose(); throw; }
            finally { _visiting.Remove(mask); }
        }

        public void Dispose()
        {
            foreach (var geometry in _coverage.Values) geometry.Dispose();
            _coverage.Clear();
        }
    }

    private ID2D1PathGeometry CreateCompositionMaskGeometry(System.Drawing.Drawing2D.GraphicsPath source)
    {
        var geometry = _factory!.CreatePathGeometry();
        try
        {
            using var sink = geometry.Open();
            sink.SetFillMode(source.FillMode == System.Drawing.Drawing2D.FillMode.Alternate
                ? FillMode.Alternate : FillMode.Winding);
            // GDI+ rejects PathPoints/PathTypes on an empty path on the
            // application's .NET runtime. An empty mask is valid coverage.
            var points = source.PointCount == 0 ? Array.Empty<PointF>() : source.PathPoints;
            var types = points.Length == 0 ? Array.Empty<byte>() : source.PathTypes;
            var open = false;
            for (var index = 0; index < points.Length; index++)
            {
                var kind = types[index] & 7;
                var point = new Vector2(points[index].X, points[index].Y);
                if (kind == 0)
                {
                    if (open) sink.EndFigure(FigureEnd.Open);
                    sink.BeginFigure(point, FigureBegin.Filled);
                    open = true;
                }
                else if (kind == 1) sink.AddLine(point);
                else if (kind == 3 && index + 2 < points.Length)
                {
                    sink.AddBezier(new BezierSegment(point,
                        new Vector2(points[index + 1].X, points[index + 1].Y),
                        new Vector2(points[index + 2].X, points[index + 2].Y)));
                    index += 2;
                }
                else throw new InvalidDataException("Invalid composed mask path.");
                if ((types[index] & 128) != 0)
                {
                    sink.EndFigure(FigureEnd.Closed);
                    open = false;
                }
            }
            if (open) sink.EndFigure(FigureEnd.Open);
            sink.Close();
            return geometry;
        }
        catch { geometry.Dispose(); throw; }
    }
}
