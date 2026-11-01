using Clipper2Lib;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace VectorAnimationEngine;

internal static class BrushStrokeBuilder
{
    internal const int TrajectoryFitSpan = 128;
    private const int FitSpanLimit = TrajectoryFitSpan;
    private const int SampleLimit = 4096;
    private const int UnionBatchLimit = 2048;
    private const int UnionVertexLimit = 8192;
    private const float CoordinateLimit = 100_000_000;
    private static readonly IComparer<PointF> SweepPointComparer = Comparer<PointF>.Create(CompareSweepPoints);
    private static readonly ConcurrentBag<Clipper64> UnionWorkers = new();
    private static int _pooledUnionWorkerCount;

    private readonly record struct Region(PathBezierNode[][] Contours, Paths64 Fallback, Color Color);

    public static int[] AddStroke(
        VectorScene scene,
        int layer,
        IReadOnlyList<PointF> samples,
        float diameter,
        Color color,
        BrushShape shape,
        int smoothing,
        float worldPerPixel,
        int frequency = 8,
        bool continuous = true)
    {
        Validate(scene, samples, diameter, shape);
        var profile = new List<PressureBrushPoint>(samples.Count);
        foreach (var point in samples)
        {
            ValidatePoint(point);
            if (profile.Count == 0 || profile[^1].Point != point)
                profile.Add(new PressureBrushPoint(point, Math.Max(VectorUnits.MinimumStrokeUnits, diameter)));
        }
        return AddProfile(scene, layer, profile, color, shape, smoothing, worldPerPixel, frequency, continuous);
    }

    public static int[] AddPressureStroke(
        VectorScene scene,
        int layer,
        IReadOnlyList<PressureBrushSample> samples,
        float diameter,
        Color color,
        BrushShape shape,
        int smoothing,
        float worldPerPixel,
        int frequency = 8,
        bool continuous = true)
    {
        Validate(scene, samples, diameter, shape);
        var cleaned = new PressureBrushSample[samples.Count];
        for (var index = 0; index < samples.Count; index++)
        {
            var sample = samples[index];
            ValidatePoint(sample.Point);
            cleaned[index] = sample with
            {
                SpeedPixelsPerSecond = float.IsFinite(sample.SpeedPixelsPerSecond)
                    ? Math.Clamp(sample.SpeedPixelsPerSecond, 0, 5000) : 0,
                HeldSeconds = float.IsFinite(sample.HeldSeconds) ? Math.Max(0, sample.HeldSeconds) : 0
            };
        }
        var profile = new List<PressureBrushPoint>(cleaned.Length);
        FreehandStrokeProcessor.UpdatePressurePreviewProfile(cleaned, diameter, profile, new List<float>(cleaned.Length));
        return AddPressureProfile(scene, layer, profile, color, shape, smoothing, worldPerPixel, frequency, continuous);
    }

    internal static int[] AddPressureProfile(
        VectorScene scene,
        int layer,
        IReadOnlyList<PressureBrushPoint> profile,
        Color color,
        BrushShape shape,
        int smoothing,
        float worldPerPixel,
        int frequency = 8,
        bool continuous = true,
        bool trajectoryPrepared = false)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(shape);
        foreach (var sample in profile)
        {
            ValidatePoint(sample.Point);
            if (!float.IsFinite(sample.Diameter) || sample.Diameter <= 0 || sample.Diameter > CoordinateLimit)
                throw new ArgumentOutOfRangeException(nameof(profile));
        }
        var prepared = trajectoryPrepared ? profile : ProcessPressureTrajectory(profile, smoothing);
        return AddProfile(scene, layer, prepared, color, shape, smoothing, worldPerPixel, frequency, continuous,
            trajectoryPrepared: true);
    }

    internal static PointF[] ProcessTrajectory(IReadOnlyList<PointF> samples, int smoothing, float worldPerPixel)
    {
        ArgumentNullException.ThrowIfNull(samples);
        smoothing = Math.Clamp(smoothing, 0, 100);
        worldPerPixel = NormalizeWorldPerPixel(worldPerPixel);
        var amount = smoothing / 100f;
        var error = 1f + worldPerPixel * (0.01f + amount * amount * 0.8f);
        var raw = new List<PointF>(samples.Count);
        foreach (var sample in samples)
        {
            ValidatePoint(sample);
            var point = VectorUnits.Quantize(sample);
            if (raw.Count == 0 || raw[^1] != point) raw.Add(point);
        }
        if (raw.Count < 2) return raw.ToArray();

        var spanCount = (raw.Count - 2) / FitSpanLimit + 1;
        var sampledSpans = new PointF[spanCount][];
        ParallelBatch.For(spanCount, 1, (_, firstSpan, lastSpan) =>
        {
            for (var spanIndex = firstSpan; spanIndex < lastSpan; spanIndex++)
            {
                var first = spanIndex * FitSpanLimit;
                var span = raw.GetRange(first, Math.Min(FitSpanLimit + 1, raw.Count - first)).ToArray();
                var processed = smoothing == 0 ? span
                    : FreehandStrokeProcessor.Process(span, smoothing, Math.Max(0.25f, error * 0.1f));
                if (processed.Length < 2) processed = span;
                var nodes = FitOpen(processed, error);
                var sampled = TrySample(nodes, false, Math.Max(0.25f, error * 0.125f), out var adaptive)
                    ? adaptive : span;
                sampledSpans[spanIndex] = sampled.Length < 2 ? span : sampled;
            }
        }, maxWorkers: raw.Count >= 512 ? ParallelBatch.MaximumWorkerCount : 1);
        var trajectory = new List<PointF>();
        foreach (var sampled in sampledSpans)
            foreach (var point in sampled)
                if (trajectory.Count == 0 || trajectory[^1] != point) trajectory.Add(point);
        return trajectory.ToArray();
    }

    private static float NormalizeWorldPerPixel(float worldPerPixel) =>
        float.IsFinite(worldPerPixel) && worldPerPixel > 0
            ? Math.Min(worldPerPixel, CoordinateLimit) : VectorUnits.UnitsPerPixel;

    internal static int PressureSmoothingSupport(int smoothing)
    {
        var radius = (int)MathF.Round(Math.Clamp(smoothing, 0, 100) / 100f * 4f);
        return radius == 0 ? 0 : 2 * Math.Max(radius, 2);
    }

    internal static PressureBrushPoint[] ProcessPressureTrajectory(
        IReadOnlyList<PressureBrushPoint> profile, int smoothing) =>
        FreehandStrokeProcessor.SmoothPressureProfile(profile, smoothing);

    private static void Validate<T>(VectorScene scene, IReadOnlyList<T> samples, float diameter, BrushShape shape)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(shape);
        if (!float.IsFinite(diameter) || diameter <= 0 || diameter > CoordinateLimit)
            throw new ArgumentOutOfRangeException(nameof(diameter));
    }

    private static void ValidatePoint(PointF point)
    {
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y)
            || Math.Abs(point.X) > CoordinateLimit || Math.Abs(point.Y) > CoordinateLimit)
            throw new ArgumentOutOfRangeException(nameof(point), "Brush coordinates must be finite and within the supported range.");
    }

    private static int[] AddProfile(
        VectorScene scene, int layer, IReadOnlyList<PressureBrushPoint> source,
        Color color, BrushShape shape, int smoothing, float worldPerPixel, int frequency, bool continuous,
        bool trajectoryPrepared = false)
    {
        if (source.Count == 0 || color.A == 0) return [];
        smoothing = Math.Clamp(smoothing, 0, 100);
        worldPerPixel = NormalizeWorldPerPixel(worldPerPixel);
        var minimumDiameter = source.Min(sample => sample.Diameter);
        var amount = smoothing / 100f;
        var outlineError = 1.5f + Math.Min(worldPerPixel * (0.01f + amount * amount * 0.25f), minimumDiameter * 0.04f);
        PressureBrushPoint[] profile;
        if (trajectoryPrepared) profile = source.ToArray();
        else
        {
            var trajectory = ProcessTrajectory(source.Select(sample => sample.Point).ToArray(), smoothing, worldPerPixel);
            profile = trajectory.Length == 1
                ? [new PressureBrushPoint(trajectory[0], source.Max(sample => sample.Diameter))]
                : MapPressure(source, trajectory);
        }
        IEnumerable<PressureBrushPoint> sweepProfile = continuous
            ? profile : CreateStamps(profile, frequency, shape.StampSpacingScale);

        var tips = shape.Layers.Select(softLayer => shape.NormalizedContour(softLayer.Threshold)).ToArray();
        var layerRegions = new List<Region>[tips.Length];
        var maximumWorkers = ParallelBatch.MaximumWorkerCountFor(12);
        var layerWorkers = profile.Length >= 512 ? Math.Min(tips.Length, maximumWorkers) : 1;
        ParallelBatch.For(tips.Length, 1, (_, first, last) =>
        {
            for (var layerIndex = first; layerIndex < last; layerIndex++)
            {
                var workersForLayer = maximumWorkers / layerWorkers
                    + (layerIndex % layerWorkers < maximumWorkers % layerWorkers ? 1 : 0);
                using var workerLimit = ParallelBatch.PushWorkerLimit(Math.Max(1, workersForLayer));
                var regionsForLayer = new List<Region>();
                layerRegions[layerIndex] = regionsForLayer;
                var tip = tips[layerIndex];
                if (tip.Length < 3) continue;
                var softLayer = shape.Layers[layerIndex];
                var alpha = Math.Clamp((int)Math.Round(color.A * softLayer.Opacity), 1, 255);
                var layerColor = Color.FromArgb(alpha, color.R, color.G, color.B);
                foreach (var polygons in BuildSweep(profile, sweepProfile, tip, shape.IsRadiallySymmetric, continuous))
                {
                    var contours = FitContours(polygons, outlineError);
                    if (contours.Length > 0) regionsForLayer.Add(new Region(contours, polygons, layerColor));
                }
            }
        }, maxWorkers: layerWorkers);
        var regions = layerRegions.SelectMany(regionsForLayer => regionsForLayer).ToList();
        if (regions.Count == 0) return [];

        var added = new List<int>(regions.Count);
        scene.BeginDeferredAppend(regions.Count, [layer]);
        try
        {
            foreach (var region in regions)
            {
                var atoms = (uint)Math.Max(3, region.Contours.Sum(contour => contour.Length));
                var objectIndex = scene.AppendPathBezierObjectContours(
                    layer, region.Contours, 0, region.Color, Color.Transparent, atoms);
                if (objectIndex < 0)
                {
                    var fallback = region.Fallback.Select(path => Clean(path.Select(point => new PointF(point.X, point.Y))))
                        .Where(contour => contour.Length >= 3).ToArray();
                    objectIndex = scene.AppendPathBezierObjectContours(
                        layer, fallback.Select(LinearClosed).ToArray(), 0, region.Color, Color.Transparent, atoms);
                    if (objectIndex < 0)
                        objectIndex = scene.AppendPathObjectContours(
                            layer, fallback, 0, region.Color, Color.Transparent, atoms);
                }
                if (objectIndex < 0)
                    throw new InvalidOperationException("The brush region could not be stored as a valid path.");
                added.Add(objectIndex);
            }
        }
        finally
        {
            scene.EndDeferredAppend();
            if (added.Count > 0) scene.CompleteDeferredBuild();
        }
        return added.ToArray();
    }

    private static List<Paths64> BuildSweep(
        IReadOnlyList<PressureBrushPoint> profile, IEnumerable<PressureBrushPoint> sweepProfile,
        PointF[] tip, bool radial, bool continuous)
    {
        if (continuous && radial && profile.All(sample => sample.Diameter == profile[0].Diameter))
        {
            var radiusScale = tip.Average(point => MathF.Sqrt(point.X * point.X + point.Y * point.Y));
            var width = profile[0].Diameter * radiusScale;
            if (width >= VectorUnits.MinimumStrokeUnits)
            {
                var outlines = FreehandStrokeProcessor.CreateBrushOutlines(
                    profile.Select(sample => sample.Point).ToArray(), width);
                var polygons = new Paths64(outlines.Select(ToPath));
                if (polygons.Count > 0 && polygons.All(path => path.Count >= 3 && Math.Abs(Clipper.Area(path)) >= 0.5))
                    return [polygons];
            }
        }
        return Sweep(sweepProfile, tip, continuous);
    }

    private static PressureBrushPoint[] MapPressure(
        IReadOnlyList<PressureBrushPoint> source, IReadOnlyList<PointF> trajectory)
    {
        var sourceDistances = new double[source.Count];
        for (var index = 1; index < source.Count; index++)
            sourceDistances[index] = sourceDistances[index - 1] + Distance(source[index - 1].Point, source[index].Point);
        var distances = new double[trajectory.Count];
        for (var index = 1; index < trajectory.Count; index++)
            distances[index] = distances[index - 1] + Distance(trajectory[index - 1], trajectory[index]);
        if (distances[^1] < 0.001 || sourceDistances[^1] < 0.001)
            return [new PressureBrushPoint(trajectory[0], source.Max(sample => sample.Diameter))];

        var result = new List<PressureBrushPoint>(trajectory.Count + source.Count);
        var sourceIndex = 0;
        var knotIndex = 1;
        var scale = distances[^1] / sourceDistances[^1];
        for (var index = 0; index < trajectory.Count; index++)
        {
            if (index > 0)
            {
                while (knotIndex < source.Count - 1 && sourceDistances[knotIndex] * scale < distances[index])
                {
                    var target = sourceDistances[knotIndex] * scale;
                    if (target > distances[index - 1]
                        && (source[knotIndex].Diameter != source[knotIndex - 1].Diameter
                            || source[knotIndex].Diameter != source[knotIndex + 1].Diameter))
                    {
                        var fraction = (float)((target - distances[index - 1]) / (distances[index] - distances[index - 1]));
                        result.Add(new PressureBrushPoint(
                            Lerp(trajectory[index - 1], trajectory[index], fraction), source[knotIndex].Diameter));
                    }
                    knotIndex++;
                }
            }
            var sourceDistance = distances[index] / scale;
            while (sourceIndex < source.Count - 2 && sourceDistances[sourceIndex + 1] < sourceDistance) sourceIndex++;
            var next = Math.Min(sourceIndex + 1, source.Count - 1);
            var length = sourceDistances[next] - sourceDistances[sourceIndex];
            var amount = length < 0.001 ? 1f : (float)Math.Clamp((sourceDistance - sourceDistances[sourceIndex]) / length, 0, 1);
            result.Add(new PressureBrushPoint(trajectory[index],
                source[sourceIndex].Diameter + (source[next].Diameter - source[sourceIndex].Diameter) * amount));
        }
        return result.ToArray();
    }

    private static IEnumerable<PressureBrushPoint> CreateStamps(
        IReadOnlyList<PressureBrushPoint> profile, int frequency, float spacingScale)
    {
        frequency = Math.Clamp(frequency, 1, 24);
        spacingScale = Math.Clamp(spacingScale, 0.1f, 1f);
        var lastStamp = profile[0];
        yield return lastStamp;
        double remaining = Spacing(profile[0]);
        for (var index = 1; index < profile.Count; index++)
        {
            var start = profile[index - 1];
            var end = profile[index];
            var length = Distance(start.Point, end.Point);
            double consumed = 0;
            while (remaining <= length - consumed && length > 0.0001)
            {
                consumed += remaining;
                var amount = (float)(consumed / length);
                var stamp = new PressureBrushPoint(Lerp(start.Point, end.Point, amount),
                    start.Diameter + (end.Diameter - start.Diameter) * amount);
                yield return stamp;
                lastStamp = stamp;
                remaining = Spacing(stamp);
            }
            remaining -= length - consumed;
        }
        if (lastStamp != profile[^1]) yield return profile[^1];

        double Spacing(PressureBrushPoint sample) => Math.Max(1, sample.Diameter / frequency * spacingScale);
    }

    private static List<Paths64> Sweep(
        IEnumerable<PressureBrushPoint> profile, IReadOnlyList<PointF> tip, bool continuous)
    {
        var result = new List<Paths64>();
        var region = new Paths64();
        var batch = new Paths64();
        var batchVertices = 0;
        var convex = IsConvex(tip);
        var capacity = Math.Max(4, tip.Count);
        var polygon = new PointF[capacity];
        var previous = new PointF[capacity];
        var sortedPolygon = new PointF[capacity];
        var sortedPrevious = new PointF[capacity];
        var sorted = new PointF[capacity * 2];
        var hull = new PointF[capacity * 4];
        var stamp = new Path64(capacity);
        var previousCount = 0;
        var previousIsSorted = false;
        foreach (var sample in profile)
        {
            var radius = Math.Max(VectorUnits.MinimumStrokeUnits * 0.6f, sample.Diameter) * 0.5f;
            var polygonCount = tip.Count;
            for (var index = 0; index < polygonCount; index++)
            {
                var point = tip[index];
                polygon[index] = VectorUnits.Quantize(new PointF(
                    sample.Point.X + point.X * radius, sample.Point.Y + point.Y * radius));
            }
            SetQuantizedPath(stamp, polygon.AsSpan(0, polygonCount));
            if (stamp.Count < 3 || Math.Abs(Clipper.Area(stamp)) < 0.5)
            {
                var center = VectorUnits.Quantize(sample.Point);
                polygonCount = 4;
                polygon[0] = new PointF(center.X - 1, center.Y - 1);
                polygon[1] = new PointF(center.X + 1, center.Y - 1);
                polygon[2] = new PointF(center.X + 1, center.Y + 1);
                polygon[3] = new PointF(center.X - 1, center.Y + 1);
                SetQuantizedPath(stamp, polygon.AsSpan(0, polygonCount));
            }
            var polygonIsSorted = false;
            if (continuous && previousCount > 0 && (convex || previousCount != polygonCount))
            {
                if (!previousIsSorted)
                {
                    Array.Copy(previous, sortedPrevious, previousCount);
                    Array.Sort(sortedPrevious, 0, previousCount, SweepPointComparer);
                }
                Array.Copy(polygon, sortedPolygon, polygonCount);
                Array.Sort(sortedPolygon, 0, polygonCount, SweepPointComparer);
                polygonIsSorted = true;
                var hullCount = ConvexHull(
                    sortedPrevious.AsSpan(0, previousCount), sortedPolygon.AsSpan(0, polygonCount), sorted, hull);
                AddPiece(QuantizedPath(hull.AsSpan(0, hullCount)));
            }
            else
            {
                AddPiece(new Path64(stamp));
                if (continuous && previousCount > 0)
                {
                    for (var edge = 0; edge < polygonCount; edge++)
                    {
                        var next = (edge + 1) % polygonCount;
                        AddPiece(QuantizedPath([previous[edge], previous[next], polygon[next]]));
                        AddPiece(QuantizedPath([previous[edge], polygon[next], polygon[edge]]));
                    }
                }
            }
            (previous, polygon) = (polygon, previous);
            (sortedPrevious, sortedPolygon) = (sortedPolygon, sortedPrevious);
            previousCount = polygonCount;
            previousIsSorted = polygonIsSorted;
        }
        Flush();
        if (region.Count > 0) result.Add(region);
        return result;

        void AddPiece(Path64 path)
        {
            if (path.Count < 3) return;
            var area = Clipper.Area(path);
            if (Math.Abs(area) < 0.5) return;
            if (area < 0) path.Reverse();
            batch.Add(path);
            batchVertices += path.Count;
            if (batch.Count >= UnionBatchLimit || batchVertices >= UnionVertexLimit) Flush();
        }

        void Flush()
        {
            if (batch.Count == 0) return;
            try
            {
                var input = new Paths64(region);
                input.AddRange(batch);
                var merged = Union(input, FillRule.NonZero);
                if (merged.Count == 0) throw new InvalidOperationException("The brush union unexpectedly produced no geometry.");
                region = merged;
            }
            catch (Exception exception) when (exception is ClipperLibException or OverflowException or InvalidOperationException)
            {
                throw new InvalidOperationException("The continuous brush sweep could not be combined without changing opacity.", exception);
            }
            batch.Clear();
            batchVertices = 0;
        }
    }

    private static bool IsConvex(IReadOnlyList<PointF> contour)
    {
        var sign = 0;
        for (var index = 0; index < contour.Count; index++)
        {
            var cross = Cross(contour[index], contour[(index + 1) % contour.Count], contour[(index + 2) % contour.Count]);
            if (Math.Abs(cross) < 0.000001) continue;
            var currentSign = Math.Sign(cross);
            if (sign != 0 && sign != currentSign) return false;
            sign = currentSign;
        }
        return sign != 0;
    }

    private static Paths64 Union(Paths64 paths, FillRule fillRule)
    {
        if (UnionWorkers.TryTake(out var worker)) Interlocked.Decrement(ref _pooledUnionWorkerCount);
        else worker = new Clipper64();
        var result = new Paths64();
        var reusable = false;
        try
        {
            worker.AddSubject(paths);
            if (!worker.Execute(ClipType.Union, fillRule, result))
                throw new ClipperLibException("The brush union could not be completed.");
            reusable = paths.Sum(path => (long)path.Count) <= UnionVertexLimit * 2
                && result.Sum(path => (long)path.Count) <= UnionVertexLimit * 2;
            return result;
        }
        finally
        {
            worker.Clear();
            if (reusable)
            {
                if (Interlocked.Increment(ref _pooledUnionWorkerCount) <= 8) UnionWorkers.Add(worker);
                else Interlocked.Decrement(ref _pooledUnionWorkerCount);
            }
        }
    }

    private static int CompareSweepPoints(PointF first, PointF second)
    {
        var comparison = first.X.CompareTo(second.X);
        return comparison != 0 ? comparison : first.Y.CompareTo(second.Y);
    }

    private static int ConvexHull(
        ReadOnlySpan<PointF> previous, ReadOnlySpan<PointF> current, PointF[] sorted, PointF[] hull)
    {
        var previousIndex = 0;
        var currentIndex = 0;
        var sortedCount = 0;
        while (previousIndex < previous.Length || currentIndex < current.Length)
        {
            var point = currentIndex >= current.Length
                || previousIndex < previous.Length && CompareSweepPoints(previous[previousIndex], current[currentIndex]) <= 0
                ? previous[previousIndex++] : current[currentIndex++];
            if (sortedCount == 0 || sorted[sortedCount - 1] != point) sorted[sortedCount++] = point;
        }
        if (sortedCount < 3)
        {
            Array.Copy(sorted, hull, sortedCount);
            return sortedCount;
        }
        var hullCount = 0;
        for (var index = 0; index < sortedCount; index++)
        {
            var point = sorted[index];
            while (hullCount > 1 && Cross(hull[hullCount - 2], hull[hullCount - 1], point) <= 0) hullCount--;
            hull[hullCount++] = point;
        }
        var lowerCount = hullCount;
        for (var index = sortedCount - 2; index >= 0; index--)
        {
            var point = sorted[index];
            while (hullCount > lowerCount && Cross(hull[hullCount - 2], hull[hullCount - 1], point) <= 0) hullCount--;
            hull[hullCount++] = point;
        }
        return hullCount - 1;
    }

    private static Path64 QuantizedPath(ReadOnlySpan<PointF> points)
    {
        var path = new Path64(points.Length);
        SetQuantizedPath(path, points);
        return path;
    }

    private static void SetQuantizedPath(Path64 path, ReadOnlySpan<PointF> points)
    {
        path.Clear();
        for (var index = 0; index < points.Length; index++)
        {
            var point = points[index];
            if (index == 0 || points[index - 1] != point)
                path.Add(new Point64((long)point.X, (long)point.Y));
        }
        if (path.Count > 1 && points[0] == points[^1]) path.RemoveAt(path.Count - 1);
    }

    private static double Cross(PointF first, PointF second, PointF third) =>
        ((double)second.X - first.X) * (third.Y - first.Y) - ((double)second.Y - first.Y) * (third.X - first.X);

    private static PathBezierNode[][] FitContours(Paths64 polygons, float error)
    {
        var source = polygons.Select(path => Clean(path.Select(point => new PointF(point.X, point.Y))))
            .Where(contour => contour.Length >= 3).ToArray();
        if (source.Length == 0) return [];
        if (source.Length > 256) return LinearFallback();
        var fitted = source.Select(contour => FitClosed(contour, error)).ToArray();
        try
        {
            var sampledPoints = fitted.Select(SampleClosed).ToArray();
            var sampled = sampledPoints.Select(ToPath).ToArray();
            if (sampled.Any(path => path.Count < 3 || Math.Abs(Clipper.Area(path)) < 0.5)) return LinearFallback();
            var operands = new[] { polygons, new Paths64(sampled) };
            var normalized = new Paths64[2];
            ParallelBatch.For(2, 1, (_, first, last) =>
            {
                for (var index = first; index < last; index++)
                    normalized[index] = Union(operands[index], FillRule.EvenOdd);
            }, maxWorkers: source.Sum(contour => contour.Length) >= 2048 ? Math.Min(2, ParallelBatch.MaximumWorkerCount) : 1);
            var normalizedSource = normalized[0];
            var normalizedFit = normalized[1];
            if (normalizedSource.Count != normalizedFit.Count
                || normalizedSource.Count(path => Clipper.Area(path) < 0) != normalizedFit.Count(path => Clipper.Area(path) < 0))
                return LinearFallback();
            for (var inner = 0; inner < source.Length; inner++)
            {
                if (Math.Sign(Clipper.Area(ToPath(source[inner]))) != Math.Sign(Clipper.Area(sampled[inner]))) return LinearFallback();
                for (var outer = 0; outer < source.Length; outer++)
                {
                    if (inner == outer) continue;
                    if (Inside(source[inner][0], source[outer]) != Inside(fitted[inner][0].Anchor, sampledPoints[outer]))
                        return LinearFallback();
                }
            }
            return fitted;

        }
        catch (Exception exception) when (exception is ClipperLibException or OverflowException)
        {
            return LinearFallback();
        }

        PathBezierNode[][] LinearFallback() => source.Select(LinearClosed).ToArray();
    }

    private static PathBezierNode[] FitClosed(PointF[] contour, float error)
    {
        var cuts = new SortedSet<int> { 0 };
        var minimumX = 0;
        var maximumX = 0;
        var minimumY = 0;
        var maximumY = 0;
        for (var index = 1; index < contour.Length; index++)
        {
            if (contour[index].X < contour[minimumX].X) minimumX = index;
            if (contour[index].X > contour[maximumX].X) maximumX = index;
            if (contour[index].Y < contour[minimumY].Y) minimumY = index;
            if (contour[index].Y > contour[maximumY].Y) maximumY = index;
        }
        cuts.Add(minimumX);
        cuts.Add(maximumX);
        cuts.Add(minimumY);
        cuts.Add(maximumY);
        for (var index = 0; index < contour.Length; index++)
        {
            var previous = contour[(index + contour.Length - 1) % contour.Length];
            var next = contour[(index + 1) % contour.Length];
            var immediate = DirectionCosine(previous, contour[index], next);
            if (immediate > 0.7072f) continue;
            var before = Neighbor(index, -1);
            var after = Neighbor(index, 1);
            if (DirectionCosine(before, contour[index], after) <= 0.7072f) cuts.Add(index);
        }
        if (cuts.Count < 3)
        {
            cuts.Add(contour.Length / 3);
            cuts.Add(contour.Length * 2 / 3);
        }
        var anchors = cuts.ToArray();
        var spans = new List<PointF[]>();
        for (var cut = 0; cut < anchors.Length; cut++)
        {
            var first = anchors[cut];
            var last = cut + 1 < anchors.Length ? anchors[cut + 1] : contour.Length;
            for (var start = first; start < last; start += FitSpanLimit)
            {
                var count = Math.Min(FitSpanLimit, last - start);
                var span = new PointF[count + 1];
                for (var offset = 0; offset <= count; offset++) span[offset] = contour[(start + offset) % contour.Length];
                spans.Add(span);
            }
        }
        var fittedSpans = new PathBezierNode[spans.Count][];
        var nextSpan = -1;
        ParallelBatch.For(spans.Count, 4, (_, first, last) =>
        {
            for (var spanIndex = Interlocked.Increment(ref nextSpan); spanIndex < spans.Count;
                spanIndex = Interlocked.Increment(ref nextSpan))
                fittedSpans[spanIndex] = FitOpen(spans[spanIndex], error);
        });
        var result = new List<PathBezierNode>();
        foreach (var fittedSpan in fittedSpans) Join(result, fittedSpan);
        if (result.Count > 1 && result[0].Anchor == result[^1].Anchor)
        {
            result[0] = result[0] with { IncomingControl = result[^1].IncomingControl };
            result.RemoveAt(result.Count - 1);
        }
        return result.Count >= 3 ? result.ToArray() : LinearClosed(contour);

        PointF Neighbor(int origin, int direction)
        {
            var cursor = origin;
            float travelled = 0;
            for (var step = 0; step < Math.Min(8, contour.Length / 2); step++)
            {
                var next = (cursor + direction + contour.Length) % contour.Length;
                travelled += Distance(contour[cursor], contour[next]);
                cursor = next;
                if (travelled >= Math.Max(3, error * 2)) break;
            }
            return contour[cursor];
        }
    }

    private static PathBezierNode[] FitOpen(PointF[] points, float error)
    {
        if (points.Length < 2) return points.Select(point => new PathBezierNode(point, point, point)).ToArray();
        if (VectorScene.TryCreateOpenFreehandBezierNodes(points, Math.Max(0.1f, error * 0.35f), out var fitted)
            && fitted.Length <= points.Length
            && ControlsBounded(fitted, points, error)
            && TrySample(fitted, false, Math.Max(0.125f, error * 0.125f), out var sampled)
            && PolylineNear(points, sampled, error * 0.75f)
            && PolylineNear(sampled, points, error * 0.75f))
            return fitted;
        if (points.Length > 2)
        {
            var split = points.Length / 2;
            var result = new List<PathBezierNode>();
            Join(result, FitOpen(points[..(split + 1)], error));
            Join(result, FitOpen(points[split..], error));
            return result.ToArray();
        }
        return points.Select(point => new PathBezierNode(point, point, point)).ToArray();
    }

    private static bool ControlsBounded(IReadOnlyList<PathBezierNode> nodes, PointF[] source, float error)
    {
        var left = source[0].X;
        var right = left;
        var top = source[0].Y;
        var bottom = top;
        for (var index = 1; index < source.Length; index++)
        {
            left = Math.Min(left, source[index].X);
            right = Math.Max(right, source[index].X);
            top = Math.Min(top, source[index].Y);
            bottom = Math.Max(bottom, source[index].Y);
        }
        left -= error;
        right += error;
        top -= error;
        bottom += error;
        foreach (var node in nodes)
            if (!InBounds(node.Anchor) || !InBounds(node.IncomingControl) || !InBounds(node.OutgoingControl)) return false;
        return true;

        bool InBounds(PointF point) => float.IsFinite(point.X) && float.IsFinite(point.Y)
            && point.X >= left && point.X <= right && point.Y >= top && point.Y <= bottom;
    }

    private static bool PolylineNear(PointF[] source, PointF[] target, float error)
    {
        if (source.Length < 2) return true;
        using var targetIndex = new PolylineDistanceIndex(source, target);
        var budget = 65536;
        for (var index = 1; index < source.Length; index++)
            if (!Covered(source[index - 1], source[index], 0)) return false;
        return true;

        bool Covered(PointF start, PointF end, int depth)
        {
            if (targetIndex.Covers(start, end, error, ref budget)) return true;
            if (budget < 0) return false;
            budget -= Math.Max(0, target.Length - 1);
            if (budget < 0) return false;
            var midpoint = Lerp(start, end, 0.5f);
            var distance = targetIndex.DistanceTo(midpoint);
            if (distance > error) return false;
            if (Distance(start, end) * 0.5f + distance <= error) return true;
            return depth < 12 && Covered(start, midpoint, depth + 1) && Covered(midpoint, end, depth + 1);
        }
    }

    private sealed class PolylineDistanceIndex : IDisposable
    {
        private const int LeafSize = 8;
        private readonly PointF[] _points;
        private readonly DistanceNode[] _nodes;
        private readonly double _roundingPadding;
        private readonly DistanceSegment[] _segments;

        private readonly record struct DistanceNode(float Left, float Top, float Right, float Bottom, int First, int Count);
        private readonly record struct DistanceSegment(PointF Start, double DeltaX, double DeltaY,
            double LengthSquared, DistanceNode Bounds);

        public PolylineDistanceIndex(PointF[] source, PointF[] target)
        {
            _points = target;
            var magnitude = 0f;
            foreach (var point in source)
                magnitude = Math.Max(magnitude, Math.Max(Math.Abs(point.X), Math.Abs(point.Y)));
            foreach (var point in target)
                magnitude = Math.Max(magnitude, Math.Max(Math.Abs(point.X), Math.Abs(point.Y)));
            _roundingPadding = ((double)MathF.BitIncrement(magnitude) - magnitude) * 4;
            var segmentCount = Math.Max(0, target.Length - 1);
            _segments = segmentCount > 0 ? ArrayPool<DistanceSegment>.Shared.Rent(segmentCount) : [];
            for (var index = 0; index < segmentCount; index++)
            {
                var start = target[index];
                var end = target[index + 1];
                var deltaX = (double)end.X - start.X;
                var deltaY = (double)end.Y - start.Y;
                _segments[index] = new DistanceSegment(start, deltaX, deltaY, deltaX * deltaX + deltaY * deltaY,
                    new DistanceNode(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y),
                        Math.Max(start.X, end.X), Math.Max(start.Y, end.Y), index, 1));
            }
            if (segmentCount == 0)
            {
                _nodes = [];
                return;
            }
            var nodeCount = 1;
            for (var count = segmentCount; count > LeafSize; count = (count + 1) / 2)
                nodeCount = nodeCount * 2 + 1;
            _nodes = ArrayPool<DistanceNode>.Shared.Rent(nodeCount);
            Build(0, 0, segmentCount);
        }

        public void Dispose()
        {
            if (_nodes.Length > 0) ArrayPool<DistanceNode>.Shared.Return(_nodes);
            if (_segments.Length > 0) ArrayPool<DistanceSegment>.Shared.Return(_segments);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float DistanceToSegment(PointF point, int index)
        {
            ref readonly var segment = ref _segments[index];
            var amount = segment.LengthSquared < 0.0000001 ? 0
                : Math.Clamp(((point.X - segment.Start.X) * segment.DeltaX
                    + (point.Y - segment.Start.Y) * segment.DeltaY) / segment.LengthSquared, 0, 1);
            var offsetX = point.X - segment.Start.X - segment.DeltaX * amount;
            var offsetY = point.Y - segment.Start.Y - segment.DeltaY * amount;
            return (float)Math.Sqrt(offsetX * offsetX + offsetY * offsetY);
        }

        private void Build(int nodeIndex, int first, int count)
        {
            if (count > LeafSize)
            {
                var middle = count / 2;
                var leftIndex = nodeIndex * 2 + 1;
                var rightIndex = leftIndex + 1;
                Build(leftIndex, first, middle);
                Build(rightIndex, first + middle, count - middle);
                var leftChild = _nodes[leftIndex];
                var rightChild = _nodes[rightIndex];
                _nodes[nodeIndex] = new DistanceNode(
                    Math.Min(leftChild.Left, rightChild.Left), Math.Min(leftChild.Top, rightChild.Top),
                    Math.Max(leftChild.Right, rightChild.Right), Math.Max(leftChild.Bottom, rightChild.Bottom), first, count);
                return;
            }
            var start = _points[first];
            var left = start.X;
            var right = start.X;
            var top = start.Y;
            var bottom = start.Y;
            for (var index = first + 1; index <= first + count; index++)
            {
                var point = _points[index];
                left = Math.Min(left, point.X);
                right = Math.Max(right, point.X);
                top = Math.Min(top, point.Y);
                bottom = Math.Max(bottom, point.Y);
            }
            _nodes[nodeIndex] = new DistanceNode(left, top, right, bottom, first, count);
        }

        public bool Covers(PointF start, PointF end, float error, ref int budget) =>
            _nodes.Length > 0 && Covers(0, start, end, error, ref budget);

        private bool Covers(int nodeIndex, PointF start, PointF end, float error, ref int budget)
        {
            ref readonly var node = ref _nodes[nodeIndex];
            if (Outside(node, start, error) || Outside(node, end, error))
            {
                budget -= node.Count;
                return false;
            }
            if (node.Count > LeafSize)
            {
                if (Covers(nodeIndex * 2 + 1, start, end, error, ref budget)) return true;
                return budget >= 0 && Covers(nodeIndex * 2 + 2, start, end, error, ref budget);
            }
            for (var index = node.First; index < node.First + node.Count; index++)
            {
                if (--budget < 0) return false;
                if (Outside(_segments[index].Bounds, start, error) || Outside(_segments[index].Bounds, end, error)) continue;
                if (DistanceToSegment(start, index) <= error
                    && DistanceToSegment(end, index) <= error)
                    return true;
            }
            return false;
        }

        public float DistanceTo(PointF point)
        {
            var distance = float.MaxValue;
            if (_nodes.Length > 0) FindDistance(0, point, ref distance);
            return distance;
        }

        private void FindDistance(int nodeIndex, PointF point, ref float distance)
        {
            ref readonly var node = ref _nodes[nodeIndex];
            if (Outside(node, point, distance)) return;
            if (node.Count > LeafSize)
            {
                var first = nodeIndex * 2 + 1;
                var second = first + 1;
                if (BoundsDistanceSquared(_nodes[second], point) < BoundsDistanceSquared(_nodes[first], point))
                    (first, second) = (second, first);
                FindDistance(first, point, ref distance);
                FindDistance(second, point, ref distance);
                return;
            }
            for (var index = node.First; index < node.First + node.Count; index++)
            {
                if (Outside(_segments[index].Bounds, point, distance)) continue;
                distance = Math.Min(distance, DistanceToSegment(point, index));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool Outside(in DistanceNode node, PointF point, float distance)
        {
            var radius = (double)MathF.BitIncrement(distance) + _roundingPadding;
            return BoundsDistanceSquared(node, point) > radius * radius;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double BoundsDistanceSquared(in DistanceNode node, PointF point)
        {
            var deltaX = point.X < node.Left ? (double)node.Left - point.X
                : point.X > node.Right ? (double)point.X - node.Right : 0;
            var deltaY = point.Y < node.Top ? (double)node.Top - point.Y
                : point.Y > node.Bottom ? (double)point.Y - node.Bottom : 0;
            return deltaX * deltaX + deltaY * deltaY;
        }
    }

    private static bool TrySample(
        IReadOnlyList<PathBezierNode> nodes, bool closed, float error, out PointF[] points)
    {
        var result = new List<PointF> { nodes[0].Anchor };
        var segmentCount = closed ? nodes.Count : nodes.Count - 1;
        for (var index = 0; index < segmentCount; index++)
        {
            var start = nodes[index];
            var end = nodes[(index + 1) % nodes.Count];
            if (!Flatten(start.Anchor, start.OutgoingControl, end.IncomingControl, end.Anchor, 0))
            {
                points = [];
                return false;
            }
        }
        points = result.ToArray();
        return true;

        bool Flatten(PointF start, PointF control1, PointF control2, PointF end, int depth)
        {
            if (result.Count >= SampleLimit) return false;
            if (Math.Max(SegmentDistance(control1, start, end), SegmentDistance(control2, start, end)) <= error)
            {
                if (result[^1] != end) result.Add(end);
                return true;
            }
            if (depth >= 12) return false;
            var first = Lerp(start, control1, 0.5f);
            var middle = Lerp(control1, control2, 0.5f);
            var last = Lerp(control2, end, 0.5f);
            var left = Lerp(first, middle, 0.5f);
            var right = Lerp(middle, last, 0.5f);
            var split = Lerp(left, right, 0.5f);
            return Flatten(start, first, left, split, depth + 1)
                && Flatten(split, right, last, end, depth + 1);
        }
    }

    private static PointF[] SampleClosed(PathBezierNode[] contour)
    {
        var closed = new PathBezierNode[contour.Length + 1];
        contour.CopyTo(closed, 0);
        closed[^1] = contour[0];
        return Clean(VectorScene.SampleOpenFreehandBezierNodes(closed));
    }

    private static PathBezierNode[] LinearClosed(PointF[] contour) =>
        contour.Select(point => new PathBezierNode(point, point, point)).ToArray();

    private static void Join(List<PathBezierNode> target, PathBezierNode[] addition)
    {
        if (target.Count > 0 && addition.Length > 0 && target[^1].Anchor == addition[0].Anchor)
        {
            target[^1] = target[^1] with { OutgoingControl = addition[0].OutgoingControl };
            for (var index = 1; index < addition.Length; index++) target.Add(addition[index]);
        }
        else target.AddRange(addition);
    }

    private static PointF[] Clean(IEnumerable<PointF> points)
    {
        if (points is PointF[] array)
        {
            var normalized = true;
            for (var index = 0; index < array.Length; index++)
            {
                if (array[index] == VectorUnits.Quantize(array[index])
                    && (index == 0 || array[index - 1] != array[index])) continue;
                normalized = false;
                break;
            }
            if (normalized) return array.Length > 1 && array[0] == array[^1] ? array[..^1] : array;
        }
        var result = new List<PointF>();
        foreach (var point in points)
        {
            var quantized = VectorUnits.Quantize(point);
            if (result.Count == 0 || result[^1] != quantized) result.Add(quantized);
        }
        if (result.Count > 1 && result[0] == result[^1]) result.RemoveAt(result.Count - 1);
        return result.ToArray();
    }

    private static Path64 ToPath(IEnumerable<PointF> points)
    {
        var result = new Path64(points.TryGetNonEnumeratedCount(out var count) ? count : 0);
        PointF? previous = null;
        foreach (var source in points)
        {
            var point = VectorUnits.Quantize(source);
            if (previous.HasValue && previous.Value == point) continue;
            result.Add(new Point64((long)point.X, (long)point.Y));
            previous = point;
        }
        if (result.Count > 1 && result[0] == result[^1]) result.RemoveAt(result.Count - 1);
        return result;
    }

    private static bool Inside(PointF point, IReadOnlyList<PointF> contour)
    {
        var inside = false;
        for (int current = 0, previous = contour.Count - 1; current < contour.Count; previous = current++)
        {
            var start = contour[previous];
            var end = contour[current];
            if ((start.Y > point.Y) != (end.Y > point.Y)
                && point.X < (double)(end.X - start.X) * (point.Y - start.Y) / (end.Y - start.Y) + start.X)
                inside = !inside;
        }
        return inside;
    }

    private static float DirectionCosine(PointF previous, PointF current, PointF next)
    {
        var first = Distance(previous, current);
        var second = Distance(current, next);
        return first * second <= 0.0001f ? 1
            : ((current.X - previous.X) * (next.X - current.X)
                + (current.Y - previous.Y) * (next.Y - current.Y)) / (first * second);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SegmentDistance(PointF point, PointF start, PointF end)
    {
        var deltaX = (double)end.X - start.X;
        var deltaY = (double)end.Y - start.Y;
        var lengthSquared = deltaX * deltaX + deltaY * deltaY;
        var amount = lengthSquared < 0.0000001 ? 0
            : Math.Clamp(((point.X - start.X) * deltaX + (point.Y - start.Y) * deltaY) / lengthSquared, 0, 1);
        var offsetX = point.X - start.X - deltaX * amount;
        var offsetY = point.Y - start.Y - deltaY * amount;
        return (float)Math.Sqrt(offsetX * offsetX + offsetY * offsetY);
    }

    private static PointF Lerp(PointF start, PointF end, float amount) =>
        new(start.X + (end.X - start.X) * amount, start.Y + (end.Y - start.Y) * amount);

    private static float Distance(PointF start, PointF end) =>
        (float)Math.Sqrt(((double)end.X - start.X) * (end.X - start.X)
            + ((double)end.Y - start.Y) * (end.Y - start.Y));
}
