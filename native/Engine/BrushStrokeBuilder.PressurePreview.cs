using Clipper2Lib;

namespace VectorAnimationEngine;

internal static partial class BrushStrokeBuilder
{
    // Cache polygon coverage, not fitted scene objects: fitting/Boolean curve recovery
    // must never be fed the lossy result of the previous preview frame.
    internal sealed class PressurePreview(BrushShape shape)
    {
        private const int SealSpan = 64;
        private readonly Paths64[] _sealed = shape.Layers.Select(_ => new Paths64()).ToArray();
        public int SealedCount { get; private set; }
        internal long SweptSampleCount { get; private set; }
        private PressureBrushPoint[] _lastProfile = [];
        private int[] _lastObjects = [];
        private VectorScene? _lastScene;
        private (int Color, int Smoothing, float Scale) _lastSettings;

        public int[] Build(VectorScene scene, int layer, IReadOnlyList<PressureBrushPoint> profile,
            int stableCount, Color color, int smoothing, float worldPerPixel)
        {
            if (profile.Count == 0 || color.A == 0) return [];
            worldPerPixel = NormalizeWorldPerPixel(worldPerPixel);
            var settings = (color.ToArgb(), smoothing, worldPerPixel);
            if (ReferenceEquals(scene, _lastScene) && scene.ObjectCount == _lastObjects.Length
                && settings == _lastSettings && profile.SequenceEqual(_lastProfile)) return _lastObjects;
            if (SealedCount > Math.Max(0, stableCount - 1)
                || (_lastProfile.Length > 0 && (smoothing != _lastSettings.Smoothing || worldPerPixel != _lastSettings.Scale)))
            {
                foreach (var paths in _sealed) paths.Clear();
                SealedCount = 0;
            }
            // A spline endpoint also needs its next stable neighbour for its tangent.
            var sealTo = SealedCount;
            while (stableCount - 1 - sealTo >= SealSpan) sealTo += SealSpan;
            var error = Math.Max(1, Math.Min(worldPerPixel * 0.15f,
                profile.Min(sample => sample.Diameter) * 0.02f));
            var regions = new List<Region>();
            for (var tipIndex = 0; tipIndex < _sealed.Length; tipIndex++)
            {
                var tip = shape.NormalizedContour(shape.Layers[tipIndex].Threshold);
                if (tip.Length < 3) continue;
                if (sealTo > SealedCount)
                {
                    var completed = SweepRange(Math.Max(0, SealedCount - 1), sealTo, tip);
                    _sealed[tipIndex] = Union(new Paths64(_sealed[tipIndex].Concat(completed)), FillRule.NonZero);
                }
                var tail = SweepRange(Math.Max(0, sealTo - 1), profile.Count, tip);
                var polygons = Union(new Paths64(_sealed[tipIndex].Concat(tail)), FillRule.NonZero);
                var contours = CreatePressureContours(polygons, error, smoothing > 0);
                if (contours.Length == 0) continue;
                var alpha = Math.Clamp((int)Math.Round(color.A * shape.Layers[tipIndex].Opacity), 1, 255);
                regions.Add(new Region(contours, polygons, Color.FromArgb(alpha, color.R, color.G, color.B)));
            }
            scene.CreateEmpty(scene.LayerCount, scene.FrameCount);
            var added = AppendRegions(scene, layer, regions);
            SealedCount = sealTo;
            _lastScene = scene;
            _lastSettings = settings;
            _lastProfile = profile.ToArray();
            _lastObjects = added;
            return added;

            Paths64 SweepRange(int first, int last, PointF[] tip)
            {
                SweptSampleCount += last - first;
                var samples = InterpolatePressureRange(profile, first, last, smoothing, error * 0.5f);
                return SweepPressureSamples(samples, tip, shape);
            }
        }
    }

    private static Paths64 SweepPressureSamples(PressureBrushPoint[] samples, PointF[] tip, BrushShape shape)
    {
        // An affine ellipse has the same swept coverage as a circular tip in
        // inverse tip coordinates. Constant width needs one offset, not a
        // 64-vertex convex hull and union at every pressure sample.
        if (shape.IsTraditionalBrush && shape.TraditionalTipKind == TraditionalBrushTipKind.Round
            && samples.All(sample => sample.Diameter == samples[0].Diameter))
        {
            var angle = shape.TraditionalDirectionDegrees * MathF.PI / 180;
            var cos = MathF.Cos(angle);
            var sin = MathF.Sin(angle);
            var width = shape.TraditionalWidthPercent / 100f;
            var centers = samples.Select(sample => new PointF(
                (sample.Point.X * cos + sample.Point.Y * sin) / width,
                -sample.Point.X * sin + sample.Point.Y * cos)).ToArray();
            var outlines = FreehandStrokeProcessor.CreateBrushOutlines(centers, samples[0].Diameter);
            if (outlines.Length > 0)
            {
                var polygons = new Paths64(outlines.Select(contour => ToPath(contour.Select(point => new PointF(
                    point.X * width * cos - point.Y * sin,
                    point.X * width * sin + point.Y * cos)))));
                if (polygons.All(path => path.Count >= 3 && Math.Abs(Clipper.Area(path)) >= 0.5))
                    return polygons;
            }
        }
        return new Paths64(BuildSweep(samples, samples, tip, shape.IsRadiallySymmetric, true)
            .SelectMany(paths => paths));
    }

    // Pressure outlines are already sampled from a smooth trajectory. Construct their
    // shared tangents directly instead of running recursive least-squares fitting and
    // polyline-distance validation over the growing stroke on every pointer update.
    private static PathBezierNode[][] CreatePressureContours(Paths64 polygons, float error, bool smooth)
    {
        var source = polygons.Select(path => Clean(path.Select(point => new PointF(point.X, point.Y))))
            .Where(contour => contour.Length >= 3).ToArray();
        if (!smooth || source.Length == 0) return source.Select(LinearClosed).ToArray();
        if (source.Length != polygons.Count || source.Length > 256)
            return FitContours(polygons, error, smoothJoins: true);
        var curves = source.Select(CreateContour).ToArray();
        var sampled = curves.Select(SampleClosed).ToArray();
        var paths = new Paths64(sampled.Select(ToPath));
        var normalized = Union(paths, FillRule.EvenOdd);
        if (normalized.Count != polygons.Count
            || normalized.Count(path => Clipper.Area(path) < 0) != polygons.Count(path => Clipper.Area(path) < 0))
            return FitContours(polygons, error, smoothJoins: true);
        for (var inner = 0; inner < source.Length; inner++)
        {
            if (paths[inner].Count < 3 || Math.Abs(Clipper.Area(paths[inner])) < 0.5
                || Math.Sign(Clipper.Area(paths[inner])) != Math.Sign(Clipper.Area(polygons[inner])))
                return FitContours(polygons, error, smoothJoins: true);
            for (var outer = 0; outer < source.Length; outer++)
                if (inner != outer && Inside(source[inner][0], source[outer]) != Inside(sampled[inner][0], sampled[outer]))
                    return FitContours(polygons, error, smoothJoins: true);
        }
        return curves;

        PathBezierNode[] CreateContour(PointF[] points)
        {
            var nodes = new PathBezierNode[points.Length];
            for (var index = 0; index < points.Length; index++)
            {
                var previous = points[(index + points.Length - 1) % points.Length];
                var anchor = points[index];
                var next = points[(index + 1) % points.Length];
                var beforeLength = Distance(previous, anchor);
                var afterLength = Distance(anchor, next);
                if (beforeLength < 0.0001f || afterLength < 0.0001f
                    || DirectionCosine(previous, anchor, next) <= 0.7072f)
                {
                    nodes[index] = new PathBezierNode(anchor, anchor, anchor);
                    continue;
                }
                var before = new PointF((anchor.X - previous.X) / beforeLength, (anchor.Y - previous.Y) / beforeLength);
                var after = new PointF((next.X - anchor.X) / afterLength, (next.Y - anchor.Y) / afterLength);
                var tangent = new PointF(before.X + after.X, before.Y + after.Y);
                var tangentLength = Distance(PointF.Empty, tangent);
                tangent = new PointF(tangent.X / tangentLength, tangent.Y / tangentLength);
                // Each cubic's controls remain in a narrow strip around its source
                // edge. Their projections lie within its first/last thirds, bounding
                // curve deviation without an iterative fitter or a global point budget.
                var incoming = HandleLength(beforeLength, before);
                var outgoing = HandleLength(afterLength, after);
                nodes[index] = new PathBezierNode(anchor,
                    new PointF(anchor.X - tangent.X * incoming, anchor.Y - tangent.Y * incoming),
                    new PointF(anchor.X + tangent.X * outgoing, anchor.Y + tangent.Y * outgoing));

                float HandleLength(float length, PointF direction) => Math.Min(length / 3,
                    error * 0.5f / Math.Max(0.0001f, Math.Abs(tangent.X * direction.Y - tangent.Y * direction.X)));
            }
            return nodes;
        }
    }

    // Smooth positions already retain pressure knots. Interpolate between those knots
    // with shared tangents before sweeping, so sparse input does not become long chords.
    // Tangents use chord lengths, avoiding overshoot at uneven pointer-event cadence.
    private static PressureBrushPoint[] InterpolatePressureRange(IReadOnlyList<PressureBrushPoint> source,
        int first, int last, int smoothing, float error)
    {
        if (smoothing <= 0 || last - first <= 1)
            return Enumerable.Range(first, last - first).Select(index => source[index]).ToArray();
        var result = new List<PressureBrushPoint> { source[first] };
        for (var index = first; index < last - 1; index++)
        {
            var start = source[index];
            var end = source[index + 1];
            var distance = Distance(start.Point, end.Point);
            var before = Tangent(index);
            var after = Tangent(index + 1);
            var control1 = new PointF(start.Point.X + before.X * distance / 3,
                start.Point.Y + before.Y * distance / 3);
            var control2 = new PointF(end.Point.X - after.X * distance / 3,
                end.Point.Y - after.Y * distance / 3);
            Flatten(start.Point, control1, control2, end.Point, 0, 1, 0);

            void Flatten(PointF a, PointF b, PointF c, PointF d, float from, float to, int depth)
            {
                if (depth >= 12 || Math.Max(SegmentDistance(b, a, d), SegmentDistance(c, a, d)) <= error)
                {
                    result.Add(new PressureBrushPoint(d, start.Diameter + (end.Diameter - start.Diameter) * to));
                    return;
                }
                var ab = Lerp(a, b, 0.5f);
                var bc = Lerp(b, c, 0.5f);
                var cd = Lerp(c, d, 0.5f);
                var abc = Lerp(ab, bc, 0.5f);
                var bcd = Lerp(bc, cd, 0.5f);
                var midpoint = Lerp(abc, bcd, 0.5f);
                var middle = (from + to) * 0.5f;
                Flatten(a, ab, abc, midpoint, from, middle, depth + 1);
                Flatten(midpoint, bcd, cd, d, middle, to, depth + 1);
            }
        }
        return result.ToArray();

        PointF Tangent(int index)
        {
            var before = source[Math.Max(0, index - 1)].Point;
            var current = source[index].Point;
            var after = source[Math.Min(source.Count - 1, index + 1)].Point;
            if (index > 0 && index < source.Count - 1 && DirectionCosine(before, current, after) < 0.3f)
                return PointF.Empty;
            var distance = Distance(before, current) + Distance(current, after);
            return distance <= 0.0001f ? PointF.Empty
                : new PointF((after.X - before.X) / distance, (after.Y - before.Y) / distance);
        }
    }
}
