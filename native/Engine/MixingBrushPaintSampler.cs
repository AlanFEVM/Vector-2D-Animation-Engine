namespace VectorAnimationEngine;

internal static class MixingBrushPaintSampler
{
    // The sampler lives for one stroke. Keep enough tiles for a long drag so
    // crossing the canvas does not repeatedly discard and rebuild candidates.
    private const int MaximumCachedTiles = 4096;

    private readonly record struct SampleTile(long X, long Y);

    internal sealed class ActiveLayerSampler
    {
        private readonly VectorScene _scene;
        private readonly int _frame;
        private readonly int _layer;
        private readonly float _tileSize;
        private readonly Dictionary<SampleTile, int[]> _candidateCache = new();
        private long _geometryRevision = -1;
        private float _layerOpacity;

        public ActiveLayerSampler(VectorScene scene, int frame, int layer, float tileSize)
        {
            ArgumentNullException.ThrowIfNull(scene);
            _scene = scene;
            _frame = frame;
            _layer = layer;
            _tileSize = float.IsFinite(tileSize)
                ? Math.Max(64f, tileSize)
                : 256f;
            RefreshSceneState();
        }

        internal int SpatialQueryCount { get; private set; }

        public Color? Sample(PointF world)
        {
            if ((uint)_layer >= _scene.LayerCount
                || !float.IsFinite(world.X)
                || !float.IsFinite(world.Y))
            {
                return null;
            }

            if (_geometryRevision != _scene.GeometryRevision) RefreshSceneState();
            var candidates = CandidatesAt(world);
            var composite = Color.Transparent;
            var hasPaint = false;
            foreach (var objectIndex in candidates)
            {
                var source = _scene.ShapeKind[objectIndex] == ShapeKind.MixingStroke
                    ? SampleObjectFill(_scene, objectIndex, world)
                    : _scene.FillContainsPoint(objectIndex, world)
                        ? SampleObjectFill(_scene, objectIndex, world)
                        : Color.Transparent;
                if (source.A == 0) continue;
                composite = PaintColorMixer.CompositeSourceOver(composite, source);
                hasPaint = true;
            }

            if (!hasPaint || composite.A == 0) return null;
            var alpha = Math.Clamp((int)MathF.Round(composite.A * _layerOpacity), 0, 255);
            return alpha > 0
                ? Color.FromArgb(alpha, composite.R, composite.G, composite.B)
                : null;
        }

        private int[] CandidatesAt(PointF world)
        {
            var tileXValue = Math.Floor(world.X / _tileSize);
            var tileYValue = Math.Floor(world.Y / _tileSize);
            if (tileXValue < long.MinValue
                || tileXValue > long.MaxValue
                || tileYValue < long.MinValue
                || tileYValue > long.MaxValue)
            {
                return Array.Empty<int>();
            }

            var tileX = (long)tileXValue;
            var tileY = (long)tileYValue;
            var key = new SampleTile(tileX, tileY);
            if (_candidateCache.TryGetValue(key, out var cached)) return cached;

            if (_candidateCache.Count >= MaximumCachedTiles) _candidateCache.Clear();
            var left = (float)(tileX * (double)_tileSize);
            var top = (float)(tileY * (double)_tileSize);
            var queried = _scene.QueryObjects(new RectangleF(left, top, _tileSize, _tileSize), _frame);
            SpatialQueryCount++;
            var candidateCount = 0;
            var previous = -1;
            for (var index = 0; index < queried.Length; index++)
            {
                var objectIndex = queried[index];
                if (objectIndex == previous) continue;
                previous = objectIndex;
                if ((uint)objectIndex >= _scene.ObjectCount
                    || _scene.ObjectLayer[objectIndex] != _layer
                    || !_scene.IsObjectActive(objectIndex, _frame)
                    || _scene.ShapeKind[objectIndex] is ShapeKind.ImportedSvg or ShapeKind.Bitmap)
                {
                    continue;
                }
                queried[candidateCount++] = objectIndex;
            }

            if (candidateCount == 0)
            {
                cached = Array.Empty<int>();
            }
            else if (candidateCount == queried.Length)
            {
                cached = queried;
            }
            else
            {
                cached = new int[candidateCount];
                Array.Copy(queried, cached, candidateCount);
            }
            Array.Sort(cached, ComparePaintOrder);
            _candidateCache[key] = cached;
            return cached;
        }

        private int ComparePaintOrder(int first, int second)
        {
            var comparison = _scene.ObjectOrder[first].CompareTo(_scene.ObjectOrder[second]);
            if (comparison != 0) return comparison;
            comparison = _scene.ObjectSubOrder[first].CompareTo(_scene.ObjectSubOrder[second]);
            return comparison != 0 ? comparison : first.CompareTo(second);
        }

        private void RefreshSceneState()
        {
            _candidateCache.Clear();
            _geometryRevision = _scene.GeometryRevision;
            _layerOpacity = EffectiveLayerOpacity(_scene, _layer);
        }
    }

    internal static ActiveLayerSampler CreateActiveLayerSampler(
        VectorScene scene,
        int frame,
        int layer,
        float tileSize = 256f) =>
        new(scene, frame, layer, tileSize);

    public static Color? SampleActiveLayerFill(
        VectorScene scene,
        PointF world,
        int frame,
        int layer)
    {
        return CreateActiveLayerSampler(scene, frame, layer).Sample(world);
    }

    internal static Color SampleObjectFill(VectorScene scene, int objectIndex, PointF world)
    {
        if (scene.ShapeKind[objectIndex] == ShapeKind.MixingStroke)
        {
            return scene.TrySampleMixingStrokeColor(objectIndex, world, out var mixingColor)
                ? mixingColor
                : Color.Transparent;
        }
        if (!scene.HasGradient(objectIndex)) return Color.FromArgb(scene.Argb[objectIndex]);

        var kind = scene.GetGradientKind(objectIndex);
        var position = kind switch
        {
            GradientKind.Radial => RadialPosition(
                scene.GetGradientStart(objectIndex),
                scene.GetGradientEnd(objectIndex),
                world),
            GradientKind.ShapeRadial => GradientPaintUtilities.ShapeRadialPosition(
                scene.GetShapeGradientMappingContours(objectIndex),
                scene.GetGradientStart(objectIndex),
                world),
            _ when scene.TryGetGradientPathWorldPoints(objectIndex, out var path) =>
                PathPosition(path, world),
            _ => LinearPosition(
                scene.GetGradientStart(objectIndex),
                scene.GetGradientEnd(objectIndex),
                world)
        };
        return GradientPaintUtilities.SampleColor(scene.GetGradientStops(objectIndex), position);
    }

    private static float LinearPosition(PointF start, PointF end, PointF point)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var lengthSquared = dx * dx + dy * dy;
        if (lengthSquared <= 0.0001f) return 1f;
        return Math.Clamp(((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared, 0f, 1f);
    }

    private static float RadialPosition(PointF center, PointF radiusPoint, PointF point)
    {
        var radius = Distance(center, radiusPoint);
        return radius <= 0.0001f ? 1f : Math.Clamp(Distance(center, point) / radius, 0f, 1f);
    }

    private static float PathPosition(IReadOnlyList<PointF> path, PointF point)
    {
        if (path.Count < 2) return 1f;
        var totalLength = GradientPaintUtilities.PathLength(path);
        if (totalLength <= 0.0001f) return 1f;

        var accumulated = 0f;
        var nearestDistanceSquared = double.PositiveInfinity;
        var nearestPathDistance = totalLength;
        for (var index = 1; index < path.Count; index++)
        {
            var start = path[index - 1];
            var end = path[index];
            var dx = end.X - start.X;
            var dy = end.Y - start.Y;
            var segmentLengthSquared = dx * dx + dy * dy;
            var segmentLength = MathF.Sqrt(Math.Max(0f, segmentLengthSquared));
            if (segmentLength <= 0.0001f) continue;

            var amount = Math.Clamp(
                ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / segmentLengthSquared,
                0f,
                1f);
            var projectedX = start.X + dx * amount;
            var projectedY = start.Y + dy * amount;
            var distanceX = (double)point.X - projectedX;
            var distanceY = (double)point.Y - projectedY;
            var distanceSquared = distanceX * distanceX + distanceY * distanceY;
            // At a self-intersection the renderer paints later trajectory segments last.
            if (distanceSquared <= nearestDistanceSquared + 0.000001d)
            {
                nearestDistanceSquared = distanceSquared;
                nearestPathDistance = accumulated + segmentLength * amount;
            }
            accumulated += segmentLength;
        }

        return Math.Clamp(nearestPathDistance / totalLength, 0f, 1f);
    }

    private static float EffectiveLayerOpacity(VectorScene scene, int layer)
    {
        var opacity = 1f;
        var current = layer;
        for (var depth = 0; depth < scene.LayerCount && (uint)current < scene.LayerCount; depth++)
        {
            opacity *= Math.Clamp(scene.LayerOpacity[current], 0f, 1f);
            current = scene.GetLayerParentIndex(current);
        }
        return opacity;
    }

    private static float Distance(PointF start, PointF end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
