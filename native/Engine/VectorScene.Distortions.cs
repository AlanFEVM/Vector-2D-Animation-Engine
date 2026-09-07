using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Clipper2Lib;

namespace VectorAnimationEngine;

internal sealed partial class VectorScene
{
    internal bool HasObjectDistortions => _objectDistortions.Count > 0;

    internal bool TryGetObjectDistortions(int objectIndex, out DistortWarp[] distortions)
    {
        if ((uint)objectIndex >= ObjectCount
            || !_objectDistortions.TryGetValue(objectIndex, out var stored)
            || stored.Length == 0)
        {
            distortions = [];
            return false;
        }

        distortions = CloneDistortions(stored);
        return distortions.Length > 0;
    }

    internal bool TryGetObjectDistortionsView(
        int objectIndex,
        out IReadOnlyList<DistortWarp> distortions)
    {
        if ((uint)objectIndex >= ObjectCount
            || !_objectDistortions.TryGetValue(objectIndex, out var stored)
            || stored.Length == 0)
        {
            distortions = Array.Empty<DistortWarp>();
            return false;
        }

        distortions = stored;
        return true;
    }

    internal bool SetObjectDistortions(int objectIndex, IReadOnlyList<DistortWarp> distortions)
    {
        ArgumentNullException.ThrowIfNull(distortions);
        if ((uint)objectIndex >= ObjectCount) return false;

        if (!TryNormalizeDistortions(distortions, out var normalized)) return false;
        var changed = !_objectDistortions.TryGetValue(objectIndex, out var previous)
            ? normalized.Length > 0
            : !DistortionsEqual(previous, normalized);
        if (!changed) return false;

        if (normalized.Length == 0) _objectDistortions.Remove(objectIndex);
        else _objectDistortions[objectIndex] = normalized;
        if (_deferredAppendKeyframes is null)
        {
            RebuildGeometryIndex();
            InvalidateDeferredTopologyQueries();
        }
        return true;
    }

    internal bool SetObjectDistortionsBatch(
        IReadOnlyList<(int ObjectIndex, IReadOnlyList<DistortWarp> Distortions)> updates)
    {
        ArgumentNullException.ThrowIfNull(updates);
        if (updates.Count == 0) return false;

        var normalizedUpdates = new Dictionary<int, DistortWarp[]>(updates.Count);
        foreach (var (objectIndex, distortions) in updates)
        {
            ArgumentNullException.ThrowIfNull(distortions);
            if ((uint)objectIndex >= ObjectCount
                || !TryNormalizeDistortions(distortions, out var normalized))
            {
                return false;
            }
            normalizedUpdates[objectIndex] = normalized;
        }

        var changed = false;
        foreach (var (objectIndex, normalized) in normalizedUpdates)
        {
            var objectChanged = !_objectDistortions.TryGetValue(objectIndex, out var previous)
                ? normalized.Length > 0
                : !DistortionsEqual(previous, normalized);
            if (!objectChanged) continue;
            if (normalized.Length == 0) _objectDistortions.Remove(objectIndex);
            else _objectDistortions[objectIndex] = normalized;
            changed = true;
        }

        if (!changed || _deferredAppendKeyframes is not null) return changed;
        RebuildGeometryIndex();
        InvalidateDeferredTopologyQueries();
        return true;
    }

    internal bool ClearObjectDistortions(int objectIndex)
    {
        if ((uint)objectIndex >= ObjectCount || !_objectDistortions.Remove(objectIndex)) return false;
        if (_deferredAppendKeyframes is null)
        {
            RebuildGeometryIndex();
            InvalidateDeferredTopologyQueries();
        }
        return true;
    }

    internal bool ClearObjectDistortions()
    {
        if (_objectDistortions.Count == 0) return false;
        _objectDistortions.Clear();
        if (_deferredAppendKeyframes is null)
        {
            RebuildGeometryIndex();
            InvalidateDeferredTopologyQueries();
        }
        return true;
    }

    internal bool AppendObjectDistortion(int objectIndex, DistortWarp distortion)
    {
        if ((uint)objectIndex >= ObjectCount || !distortion.IsValid) return false;
        var next = _objectDistortions.TryGetValue(objectIndex, out var current)
            ? current.Concat([distortion.DeepClone()]).ToArray()
            : [distortion.DeepClone()];
        return SetObjectDistortions(objectIndex, next);
    }

    internal void SetObjectDistortionsForComposition(int objectIndex, IReadOnlyList<DistortWarp> distortions)
    {
        if ((uint)objectIndex >= ObjectCount) return;
        var normalized = NormalizeDistortions(distortions);
        if (normalized.Length == 0) _objectDistortions.Remove(objectIndex);
        else _objectDistortions[objectIndex] = normalized;
    }

    internal PointF MapObjectPoint(int objectIndex, PointF point)
    {
        if ((uint)objectIndex >= ObjectCount || !float.IsFinite(point.X) || !float.IsFinite(point.Y)) return point;
        if (!_objectDistortions.TryGetValue(objectIndex, out var distortions)) return point;
        var mapped = point;
        foreach (var distortion in distortions)
        {
            mapped = distortion.Map(mapped);
            if (!float.IsFinite(mapped.X) || !float.IsFinite(mapped.Y)) return point;
        }
        return mapped;
    }

    internal bool TryInverseMapObjectPoint(int objectIndex, PointF point, out PointF sourcePoint)
    {
        sourcePoint = point;
        if ((uint)objectIndex >= ObjectCount || !float.IsFinite(point.X) || !float.IsFinite(point.Y)) return false;
        if (!_objectDistortions.TryGetValue(objectIndex, out var distortions)) return true;
        var current = point;
        for (var index = distortions.Length - 1; index >= 0; index--)
        {
            if (!distortions[index].TryInverseMap(current, out current)
                || !float.IsFinite(current.X)
                || !float.IsFinite(current.Y))
            {
                return false;
            }
        }
        sourcePoint = current;
        return true;
    }

    internal PointF[][] GetDistortedObjectBoundaryContours(int objectIndex)
    {
        if (!_objectDistortions.TryGetValue(objectIndex, out var distortions))
        {
            // Path objects keep both a sampled polygon and the authoring Bezier
            // nodes. Prefer the nodes here so fracture and terrain extraction
            // cannot silently fall back to ShapeBoundary's rectangle when a
            // legacy or partially materialized path is missing its polygon.
            if ((uint)objectIndex < ObjectCount
                && ShapeKind[objectIndex] == VectorAnimationEngine.ShapeKind.Path
                && TryGetPathBezierWorldContours(objectIndex, out var bezierContours))
            {
                var sampledContours = bezierContours
                    .Where(contour => contour is { Length: >= 3 })
                    .Select(contour => ClosePolyline(SamplePathBezierContour(contour)))
                    .Where(contour => contour.Length >= 4)
                    .ToArray();
                if (sampledContours.Length > 0) return sampledContours;
            }

            return ShapeBoundaryContours(objectIndex);
        }
        return GetDistortedObjectBoundaryContours(objectIndex, distortions);
    }

    internal PointF[][] GetDistortedObjectBoundaryContours(
        int objectIndex,
        IReadOnlyList<DistortWarp> distortions)
    {
        if (TryBuildDistortedVectorGeometry(objectIndex, distortions, out var geometry))
        {
            if (geometry.HasClosedContours)
            {
                return geometry.ClosedContours
                    .Select(contour => ClosePolyline(SamplePathBezierContour(contour)))
                    .ToArray();
            }

            if (geometry.HasOpenStroke)
            {
                return [SampleOpenBezierSegments(geometry.OpenStrokeSegments)];
            }
        }

        var raw = ShapeBoundaryContours(objectIndex);
        if (raw.Length == 0) return [];
        return raw
            .Select(contour => MapObjectContour(contour, distortions))
            .ToArray();
    }

    internal bool TryBuildDistortedVectorGeometry(
        int objectIndex,
        IReadOnlyList<DistortWarp> distortions,
        out DistortedVectorGeometry geometry)
    {
        geometry = default;
        if ((uint)objectIndex >= ObjectCount || distortions.Count == 0) return false;
        for (var index = 0; index < distortions.Count; index++)
        {
            if (!distortions[index].IsValid) return false;
        }

        if (ShapeKind[objectIndex] == VectorAnimationEngine.ShapeKind.Line)
        {
            var provenance = BuildDistortedBezierSegmentsWithProvenance([LineCurve(objectIndex)], distortions);
            if (provenance.Length == 0) return false;
            geometry = new DistortedVectorGeometry(
                [],
                provenance.Select(segment => segment.Curve).ToArray(),
                provenance);
            return true;
        }

        if (!TryGetEditableFillBezierContours(objectIndex, out var sourceContours)) return false;
        var contours = new List<PathBezierNode[]>(sourceContours.Length);
        foreach (var sourceContour in sourceContours)
        {
            if (sourceContour.Length < 3) continue;
            var sourceSegments = new CubicBoundarySegment[sourceContour.Length];
            for (var nodeIndex = 0; nodeIndex < sourceContour.Length; nodeIndex++)
            {
                var current = sourceContour[nodeIndex];
                var next = sourceContour[(nodeIndex + 1) % sourceContour.Length];
                sourceSegments[nodeIndex] = new CubicBoundarySegment(
                    current.Anchor,
                    current.OutgoingControl,
                    next.IncomingControl,
                    next.Anchor);
            }

            var distortedSegments = BuildDistortedBezierSegments(sourceSegments, distortions);
            if (distortedSegments.Length < 3) return false;
            contours.Add(CreateBezierContour(distortedSegments));
        }

        if (contours.Count == 0) return false;
        geometry = new DistortedVectorGeometry(contours.ToArray(), [], []);
        return true;
    }

    internal static CubicBoundarySegment[] BuildDistortedBezierSegments(
        IReadOnlyList<CubicBoundarySegment> sourceSegments,
        IReadOnlyList<DistortWarp> distortions)
    {
        return BuildDistortedBezierSegmentsWithProvenance(sourceSegments, distortions)
            .Select(segment => segment.Curve)
            .ToArray();
    }

    internal static DistortedBezierSegment[] BuildDistortedBezierSegmentsWithProvenance(
        IReadOnlyList<CubicBoundarySegment> sourceSegments,
        IReadOnlyList<DistortWarp> distortions)
    {
        const float fitErrorUnits = 0.2f;
        const int maximumSegments = 4096;
        if (sourceSegments.Count > maximumSegments) return [];
        var result = new List<DistortedBezierSegment>(sourceSegments.Count * 2);
        for (var sourceIndex = 0; sourceIndex < sourceSegments.Count; sourceIndex++)
        {
            var source = sourceSegments[sourceIndex];
            var samples = SampleMappedCubic(source, distortions);
            var fitted = new List<CubicBoundarySegment>();
            var ranges = new List<(int First, int Last)>();
            FitOpenPolylineBezierSegments(
                samples.Select(sample => sample.Point).ToArray(),
                fitErrorUnits,
                fitted,
                ranges);
            if (fitted.Count > 0 && fitted.Count == ranges.Count)
            {
                if (result.Count + fitted.Count > maximumSegments) return [];
                for (var fittedIndex = 0; fittedIndex < fitted.Count; fittedIndex++)
                {
                    var range = ranges[fittedIndex];
                    result.Add(new DistortedBezierSegment(
                        fitted[fittedIndex],
                        sourceIndex,
                        samples[range.First].SourceT,
                        samples[range.Last].SourceT));
                }
                continue;
            }

            var start = MapPoint(source.Start, distortions);
            var end = MapPoint(source.End, distortions);
            if (!Finite(start) || !Finite(end) || Distance(start, end) <= 0.001f) continue;
            result.Add(new DistortedBezierSegment(
                new CubicBoundarySegment(
                    start,
                    Lerp(start, end, 1f / 3f),
                    Lerp(start, end, 2f / 3f),
                    end),
                sourceIndex,
                0,
                1));
            if (result.Count > maximumSegments) return [];
        }
        return result.ToArray();
    }

    private static MappedDistortSample[] SampleMappedCubic(
        CubicBoundarySegment source,
        IReadOnlyList<DistortWarp> distortions)
    {
        var mappedStart = MapPoint(source.Start, distortions);
        var mappedEnd = MapPoint(source.End, distortions);
        if (!Finite(mappedStart) || !Finite(mappedEnd)) return [];
        var result = new List<MappedDistortSample>(16) { new(mappedStart, 0) };
        AppendMappedCubicSamples(
            source,
            distortions,
            0f,
            1f,
            mappedStart,
            mappedEnd,
            result,
            0);
        return result.ToArray();
    }

    private static void AppendMappedCubicSamples(
        CubicBoundarySegment source,
        IReadOnlyList<DistortWarp> distortions,
        float startT,
        float endT,
        PointF mappedStart,
        PointF mappedEnd,
        List<MappedDistortSample> destination,
        int depth)
    {
        const int maximumDepth = 10;
        const float mappingFlatnessUnits = 0.12f;
        var range = endT - startT;
        var firstT = startT + range * 0.25f;
        var middleT = startT + range * 0.5f;
        var thirdT = startT + range * 0.75f;
        var mappedFirst = MapPoint(CubicPoint(source, firstT), distortions);
        var mappedMiddle = MapPoint(CubicPoint(source, middleT), distortions);
        var mappedThird = MapPoint(CubicPoint(source, thirdT), distortions);
        var flatness = Math.Max(
            DistanceToSegment(mappedFirst, mappedStart, mappedEnd),
            Math.Max(
                DistanceToSegment(mappedMiddle, mappedStart, mappedEnd),
                DistanceToSegment(mappedThird, mappedStart, mappedEnd)));
        if (depth >= maximumDepth || flatness <= mappingFlatnessUnits)
        {
            if (destination.Count == 0 || destination[^1].Point != mappedEnd)
            {
                destination.Add(new MappedDistortSample(mappedEnd, endT));
            }
            return;
        }

        AppendMappedCubicSamples(
            source,
            distortions,
            startT,
            middleT,
            mappedStart,
            mappedMiddle,
            destination,
            depth + 1);
        AppendMappedCubicSamples(
            source,
            distortions,
            middleT,
            endT,
            mappedMiddle,
            mappedEnd,
            destination,
            depth + 1);
    }

    private static PointF MapPoint(PointF point, IReadOnlyList<DistortWarp> distortions)
    {
        var mapped = point;
        for (var index = 0; index < distortions.Count; index++)
        {
            mapped = distortions[index].Map(mapped);
            if (!Finite(mapped)) return point;
        }
        return mapped;
    }

    private static PointF CubicPoint(CubicBoundarySegment curve, float t) =>
        CubicPoint(curve.Start, curve.Control1, curve.Control2, curve.End, t);

    private static PointF[] SampleOpenBezierSegments(IReadOnlyList<CubicBoundarySegment> segments)
    {
        var points = new List<PointF>(segments.Count * 4);
        for (var segmentIndex = 0; segmentIndex < segments.Count; segmentIndex++)
        {
            var samples = SampleCubicSegment(segments[segmentIndex]);
            for (var sampleIndex = segmentIndex == 0 ? 0 : 1; sampleIndex < samples.Length; sampleIndex++)
            {
                points.Add(samples[sampleIndex]);
            }
        }
        return points.ToArray();
    }

    private static PointF[] MapObjectContour(
        IReadOnlyList<PointF> contour,
        IReadOnlyList<DistortWarp> distortions)
    {
        if (contour.Count == 0) return [];
        var result = new List<PointF>(contour.Count * 2)
        {
            MapPoint(contour[0], distortions)
        };
        for (var index = 1; index < contour.Count; index++)
        {
            AppendMappedSegment(
                contour[index - 1],
                contour[index],
                result[^1],
                MapPoint(contour[index], distortions),
                distortions,
                result,
                0);
        }
        return result.ToArray();
    }

    private static void AppendMappedSegment(
        PointF sourceStart,
        PointF sourceEnd,
        PointF mappedStart,
        PointF mappedEnd,
        IReadOnlyList<DistortWarp> distortions,
        List<PointF> destination,
        int depth)
    {
        const int maximumDepth = 10;
        const float flatnessUnits = 0.5f;
        var sourceMiddle = Midpoint(sourceStart, sourceEnd);
        var mappedMiddle = MapPoint(sourceMiddle, distortions);
        if (depth >= maximumDepth
            || DistanceToSegment(mappedMiddle, mappedStart, mappedEnd) <= flatnessUnits)
        {
            if (destination[^1] != mappedEnd) destination.Add(mappedEnd);
            return;
        }

        AppendMappedSegment(
            sourceStart,
            sourceMiddle,
            mappedStart,
            mappedMiddle,
            distortions,
            destination,
            depth + 1);
        AppendMappedSegment(
            sourceMiddle,
            sourceEnd,
            mappedMiddle,
            mappedEnd,
            distortions,
            destination,
            depth + 1);
    }

    internal RectangleF GetRawObjectWorldBounds(int objectIndex) => GetObjectWorldBoundsCore(objectIndex);

    private static bool TryNormalizeDistortions(
        IReadOnlyList<DistortWarp> distortions,
        out DistortWarp[] normalized)
    {
        if (distortions.Count == 0)
        {
            normalized = [];
            return true;
        }
        if (distortions.Count > MaximumObjectDistortions)
        {
            normalized = [];
            return false;
        }
        normalized = new DistortWarp[distortions.Count];
        for (var index = 0; index < distortions.Count; index++)
        {
            if (!distortions[index].IsValid)
            {
                normalized = [];
                return false;
            }
            normalized[index] = distortions[index].DeepClone();
        }
        return true;
    }

    private static DistortWarp[] NormalizeDistortions(IReadOnlyList<DistortWarp> distortions)
    {
        if (!TryNormalizeDistortions(distortions, out var normalized))
        {
            throw new ArgumentException("Distortion stacks contain invalid warp data.", nameof(distortions));
        }
        return normalized;
    }

    private static DistortWarp[] CloneDistortions(IReadOnlyList<DistortWarp> distortions)
        => distortions.Select(distortion => distortion.DeepClone()).ToArray();

    private static bool DistortionsEqual(IReadOnlyList<DistortWarp> left, IReadOnlyList<DistortWarp> right)
        => left.Count == right.Count && left.SequenceEqual(right);

}
