using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using GdiColor = System.Drawing.Color;

namespace VectorAnimationEngine;

internal sealed partial class Direct2DStageRenderer
{
    private const int MaxBatch2DLayerCacheEntries = 8;
    private const float Batch2DMinimumScreenExtent = 0.75f;
    private const float Batch2DMinimumStrokeScreenExtent = 4f;
    private const float Batch2DMinimumStrokePixels = 0.1f;
    private const float Batch2DRealizationTolerancePixels = 0.2f;

    private readonly Dictionary<(VectorScene Scene, int Layer), CachedBatch2DLayer> _batch2DLayerCache = [];
    private readonly List<int> _batch2DObjects = [];
    private ID2D1DeviceContext2? _deviceContext2;

    private readonly record struct Batch2DRunKey(
        SceneRenderPass Pass,
        int BrushArgb,
        float WorldStroke,
        LineEndpointStyle StartStyle,
        LineEndpointStyle EndStyle);

    private sealed class Batch2DRun : IDisposable
    {
        public SceneRenderPass Pass;
        public int BrushArgb;
        public float WorldStroke;
        public ID2D1StrokeStyle? StrokeStyle;
        public ID2D1PathGeometry Geometry = null!;
        public ID2D1GeometryRealization? EdgeRealization;
        public ID2D1GeometryRealization? FilledRealization;
        public ID2D1GeometryRealization? StrokedRealization;

        public void Dispose()
        {
            EdgeRealization?.Dispose();
            FilledRealization?.Dispose();
            StrokedRealization?.Dispose();
            Geometry.Dispose();
        }
    }

    private sealed class CachedBatch2DLayer
    {
        public VectorScene Scene = null!;
        public int Layer;
        public long GeometryRevision;
        public long ActiveContentRevision;
        public int Frame;
        public int ObjectCount;
        public int ScaleKey;
        public Batch2DRun[] Runs = [];

        public bool Matches(VectorScene scene, int layer, int frame, int scaleKey)
        {
            return ReferenceEquals(Scene, scene)
                && Layer == layer
                && GeometryRevision == scene.GeometryRevision
                && ActiveContentRevision == scene.ActiveContentRevision
                && Frame == frame
                && ObjectCount == scene.ObjectCount
                && ScaleKey == scaleKey;
        }

        public void Dispose()
        {
            foreach (var run in Runs) run.Dispose();
            Runs = [];
        }
    }

    /// <summary>
    /// Draws a layer whose objects are all simple, opaque and painted by a small
    /// number of continuous runs. The world-space geometry of those runs is cached
    /// across frames as device-side geometry realizations, so pan/zoom/playback
    /// only re-submit a handful of Direct2D calls while every object keeps its
    /// exact shape, color, edge-coverage pass, stroke width and z-order. Returns
    /// false to keep the per-object renderer for anything not proven equivalent.
    /// </summary>
    private bool TryDrawLayerObjectsBatched(
        StageControl stage,
        VectorScene scene,
        int layer,
        IReadOnlyList<int> objects,
        int start)
    {
        if (start != 0 || objects.Count == 0 || _factory is null || _target is null) return false;
        // Validation-only switch used to compare the batched output against the
        // per-object path pixel for pixel.
        if (Environment.GetEnvironmentVariable("VECTOR_DISABLE_BATCH_2D") == "1") return false;
        if (scene.HasDisplayLayerEffects) return false;
        if (stage.HasSceneCompositionMaskClips(scene)) return false;

        var scaleKey = Batch2DRealizationScaleKey(stage);
        var key = (scene, layer);
        if (!_batch2DLayerCache.TryGetValue(key, out var cached)
            || !cached.Matches(scene, layer, stage.Frame, scaleKey))
        {
            var rebuilt = BuildBatch2DLayer(stage, scene, layer, scaleKey);
            if (rebuilt is null)
            {
                if (_batch2DLayerCache.Remove(key, out var stale)) stale.Dispose();
                return false;
            }

            if (_batch2DLayerCache.Remove(key, out var previous)) previous.Dispose();
            _batch2DLayerCache[key] = rebuilt;
            cached = rebuilt;
            PruneBatch2DLayerCache(scene);
        }

        DrawBatch2DLayer(stage, cached);
        return true;
    }

    private static int Batch2DRealizationScaleKey(StageControl stage)
    {
        var scale = Math.Max(1e-4f, VectorUnits.PixelsPerUnit * stage.Zoom);
        return (int)MathF.Round(MathF.Log2(scale) * 4f);
    }

    private static ID2D1DeviceContext2? TryQueryDeviceContext2(ID2D1DeviceContext? context)
    {
        if (context is null) return null;
        try
        {
            return context.QueryInterface<ID2D1DeviceContext2>();
        }
        catch (Exception exception)
        {
            AppLog.Info($"Direct2D geometry realizations unavailable; using per-frame geometry: {exception.Message}");
            return null;
        }
    }

    private void PruneBatch2DLayerCache(VectorScene activeScene)
    {
        if (_batch2DLayerCache.Count <= MaxBatch2DLayerCacheEntries) return;
        List<(VectorScene Scene, int Layer)>? stale = null;
        foreach (var key in _batch2DLayerCache.Keys)
        {
            if (ReferenceEquals(key.Scene, activeScene)) continue;
            (stale ??= []).Add(key);
        }

        if (stale is null) return;
        foreach (var key in stale)
        {
            if (_batch2DLayerCache.Remove(key, out var cached)) cached.Dispose();
            if (_batch2DLayerCache.Count <= MaxBatch2DLayerCacheEntries) return;
        }
    }

    private CachedBatch2DLayer? BuildBatch2DLayer(StageControl stage, VectorScene scene, int layer, int scaleKey)
    {
        if (!scene.ShouldRenderLayerContent(layer)) return null;

        _batch2DObjects.Clear();
        foreach (var index in scene.GetActiveObjectIndices(stage.Frame))
        {
            if ((uint)index >= scene.ObjectCount) continue;
            if (scene.ObjectLayer[index] != layer) continue;
            _batch2DObjects.Add(index);
        }

        if (_batch2DObjects.Count == 0) return null;
        _batch2DObjects.Sort((a, b) => SceneRenderOrderBuffer.CompareObjects(scene, a, b));

        var runs = new List<Batch2DRun>();
        if (!AppendBatch2DRuns(stage, scene, SceneRenderPass.Fill, runs)
            || !AppendBatch2DRuns(stage, scene, SceneRenderPass.Stroke, runs))
        {
            DisposeBatch2DRuns(runs);
            return null;
        }

        if (runs.Count == 0)
        {
            DisposeBatch2DRuns(runs);
            return null;
        }

        return new CachedBatch2DLayer
        {
            Scene = scene,
            Layer = layer,
            GeometryRevision = scene.GeometryRevision,
            ActiveContentRevision = scene.ActiveContentRevision,
            Frame = stage.Frame,
            ObjectCount = scene.ObjectCount,
            ScaleKey = scaleKey,
            Runs = runs.ToArray()
        };
    }

    private bool AppendBatch2DRuns(
        StageControl stage,
        VectorScene scene,
        SceneRenderPass pass,
        List<Batch2DRun> runs)
    {
        var currentKey = default(Batch2DRunKey);
        List<int>? currentObjects = null;
        var hasCurrent = false;
        foreach (var index in _batch2DObjects)
        {
            var shape = scene.ShapeKind[index];
            if (pass == SceneRenderPass.Fill)
            {
                if (!SceneRenderOrder.HasFill(shape)) continue;
            }
            else if (!SceneRenderOrder.HasStroke(shape, scene.Stroke[index]))
            {
                continue;
            }

            if (!TryGetBatch2DRunKey(stage, scene, index, pass, out var runKey)) return false;
            if (!hasCurrent || !currentKey.Equals(runKey))
            {
                if (!FlushBatch2DRun(stage, scene, runs, currentKey, currentObjects, hasCurrent)) return false;
                currentKey = runKey;
                currentObjects = [];
                hasCurrent = true;
            }

            currentObjects!.Add(index);
        }

        return FlushBatch2DRun(stage, scene, runs, currentKey, currentObjects, hasCurrent);
    }

    private bool FlushBatch2DRun(
        StageControl stage,
        VectorScene scene,
        List<Batch2DRun> runs,
        Batch2DRunKey key,
        List<int>? objects,
        bool hasRun)
    {
        if (!hasRun || objects is null || objects.Count == 0) return true;
        var geometry = BuildBatch2DRunGeometry(scene, objects);
        if (geometry is null) return false;

        ID2D1StrokeStyle? strokeStyle = null;
        if (key.Pass == SceneRenderPass.Stroke)
        {
            var miterJoin = key.StartStyle == LineEndpointStyle.Sharp || key.EndStyle == LineEndpointStyle.Sharp;
            strokeStyle = LineStrokeStyle(
                LineCapForEndpoint(key.StartStyle),
                LineCapForEndpoint(key.EndStyle),
                miterJoin);
        }

        var run = new Batch2DRun
        {
            Pass = key.Pass,
            BrushArgb = key.BrushArgb,
            WorldStroke = key.WorldStroke,
            StrokeStyle = strokeStyle,
            Geometry = geometry
        };

        if (_deviceContext2 is not null)
        {
            var tolerance = Math.Max(1e-5f, stage.ScreenLengthToWorld(Batch2DRealizationTolerancePixels));
            try
            {
                if (key.Pass == SceneRenderPass.Fill)
                {
                    // The sub-pixel edge-coverage pass strokes the fill material at
                    // 0.8 screen pixels; its realization width follows the current zoom.
                    run.EdgeRealization = _deviceContext2.CreateStrokedGeometryRealization(
                        geometry,
                        tolerance,
                        Math.Max(key.WorldStroke, stage.ScreenLengthToWorld(FillEdgeCoverageWidthPixels)),
                        strokeStyle: null);
                    run.FilledRealization = _deviceContext2.CreateFilledGeometryRealization(geometry, tolerance);
                }
                else
                {
                    run.StrokedRealization = _deviceContext2.CreateStrokedGeometryRealization(
                        geometry,
                        tolerance,
                        Math.Max(key.WorldStroke, stage.ScreenLengthToWorld(Batch2DMinimumStrokePixels)),
                        strokeStyle);
                }
            }
            catch (Exception exception)
            {
                AppLog.Warn($"Geometry realization unavailable; using per-frame geometry: {exception.Message}");
                _deviceContext2 = null;
                run.EdgeRealization?.Dispose();
                run.EdgeRealization = null;
                run.FilledRealization?.Dispose();
                run.FilledRealization = null;
                run.StrokedRealization?.Dispose();
                run.StrokedRealization = null;
            }
        }

        runs.Add(run);
        return true;
    }

    private static void DisposeBatch2DRuns(List<Batch2DRun> runs)
    {
        foreach (var run in runs) run.Dispose();
        runs.Clear();
    }

    private bool TryGetBatch2DRunKey(
        StageControl stage,
        VectorScene scene,
        int index,
        SceneRenderPass pass,
        out Batch2DRunKey key)
    {
        key = default;
        if (stage.IsObjectHiddenForRendering(scene, index)) return false;
        if (scene.HasGradient(index)) return false;
        if (scene.TryGetObjectDistortions(index, out _)) return false;

        var argb = pass == SceneRenderPass.Fill ? scene.Argb[index] : scene.StrokeArgb[index];
        // Merging semi-transparent runs would double-blend the overlap, so those
        // objects keep the per-object pass.
        if (GdiColor.FromArgb(argb).A != 255) return false;

        var shape = scene.ShapeKind[index];
        var startStyle = LineEndpointStyle.Round;
        var endStyle = LineEndpointStyle.Round;
        var worldStroke = 0f;
        switch (shape)
        {
            case ShapeKind.Line:
                if (pass != SceneRenderPass.Stroke) return false;
                if (!scene.IsLineStraight(index)) return false;
                startStyle = scene.GetLineEndpointStyle(index, startEndpoint: true);
                endStyle = scene.GetLineEndpointStyle(index, startEndpoint: false);
                // Sharp endpoints may require junction patches whose geometry
                // depends on neighboring lines; those stay on the per-object path.
                if (startStyle == LineEndpointStyle.Sharp || endStyle == LineEndpointStyle.Sharp) return false;
                worldStroke = scene.Stroke[index];
                break;
            case ShapeKind.Rectangle:
            case ShapeKind.Triangle:
            case ShapeKind.Polygon:
            case ShapeKind.Star:
                var screenWidth = stage.WorldLengthToScreen(scene.Width[index]);
                var screenHeight = stage.WorldLengthToScreen(scene.Height[index]);
                if (pass == SceneRenderPass.Fill)
                {
                    // The per-object path clamps each screen extent to 0.75 px;
                    // very small objects keep that path so the clamp still applies.
                    if (screenWidth < Batch2DMinimumScreenExtent || screenHeight < Batch2DMinimumScreenExtent) return false;
                }
                else
                {
                    if (scene.Stroke[index] <= 0) return false;
                    // The per-object path skips strokes on shapes below 4 screen pixels.
                    if (!(screenWidth > Batch2DMinimumStrokeScreenExtent && screenHeight > Batch2DMinimumStrokeScreenExtent)) return false;
                    worldStroke = scene.Stroke[index];
                }

                break;
            default:
                return false;
        }

        key = new Batch2DRunKey(pass, argb, worldStroke, startStyle, endStyle);
        return true;
    }

    private ID2D1PathGeometry? BuildBatch2DRunGeometry(VectorScene scene, List<int> objects)
    {
        if (_factory is null) return null;
        var geometry = _factory.CreatePathGeometry();
        var hasFigures = false;
        using (var sink = geometry.Open())
        {
            // Winding keeps overlapping and coincident figures filled exactly like
            // the per-object passes, while Alternate would punch holes into them.
            sink.SetFillMode(FillMode.Winding);
            foreach (var index in objects)
            {
                hasFigures |= AppendBatch2DObjectFigure(sink, scene, index);
            }

            sink.Close();
        }

        if (hasFigures) return geometry;
        geometry.Dispose();
        return null;
    }

    private static bool AppendBatch2DObjectFigure(ID2D1GeometrySink sink, VectorScene scene, int index)
    {
        if (scene.ShapeKind[index] == ShapeKind.Line)
        {
            if (!scene.TryGetLineCubic(index, out var start, out var control1, out var control2, out var end)) return false;
            sink.BeginFigure(new Vector2(start.X, start.Y), FigureBegin.Hollow);
            sink.AddBezier(new BezierSegment(
                new Vector2(control1.X, control1.Y),
                new Vector2(control2.X, control2.Y),
                new Vector2(end.X, end.Y)));
            sink.EndFigure(FigureEnd.Open);
            return true;
        }

        return AppendPolygonFigures(sink, stage: null, scene.GetObjectBoundaryContours(index));
    }

    private void DrawBatch2DLayer(StageControl stage, CachedBatch2DLayer cached)
    {
        var old = _target!.Transform;
        try
        {
            _target.Transform = WorldToScreenTransform(stage);
            var edgeWorld = stage.ScreenLengthToWorld(FillEdgeCoverageWidthPixels);
            var minimumStrokeWorld = stage.ScreenLengthToWorld(Batch2DMinimumStrokePixels);
            foreach (var run in cached.Runs)
            {
                var brush = BrushFor(run.BrushArgb);
                if (run.Pass == SceneRenderPass.Fill)
                {
                    if (run.FilledRealization is { } filled)
                    {
                        if (run.EdgeRealization is { } edge) _deviceContext2!.DrawGeometryRealization(edge, brush);
                        _deviceContext2!.DrawGeometryRealization(filled, brush);
                        continue;
                    }

                    // Mirrors the per-object FillRectangle pair: the sub-pixel edge
                    // coverage pass first, then the regular fill with the same brush.
                    _target.DrawGeometry(run.Geometry, brush, edgeWorld);
                    _target.FillGeometry(run.Geometry, brush);
                    continue;
                }

                if (run.StrokedRealization is { } stroked)
                {
                    _deviceContext2!.DrawGeometryRealization(stroked, brush);
                    continue;
                }

                _target.DrawGeometry(
                    run.Geometry,
                    brush,
                    Math.Max(run.WorldStroke, minimumStrokeWorld),
                    run.StrokeStyle);
            }
        }
        finally
        {
            _target.Transform = old;
        }
    }

    private void ClearBatch2DLayerCache()
    {
        foreach (var cached in _batch2DLayerCache.Values) cached.Dispose();
        _batch2DLayerCache.Clear();
    }
}
