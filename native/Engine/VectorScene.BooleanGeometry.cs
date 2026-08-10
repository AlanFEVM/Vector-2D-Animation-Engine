using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Clipper2Lib;

namespace VectorAnimationEngine;

internal sealed partial class VectorScene
{
    private bool TryBuildSameColorFillMerge(
        int a,
        int b,
        bool connectNearby,
        RectangleF mergeBounds,
        out PointF[][] mergedPath)
    {
        mergedPath = Array.Empty<PointF[]>();
        if ((uint)a >= ObjectCount || (uint)b >= ObjectCount) return false;
        if (FillAutoMergeProtected[a] || FillAutoMergeProtected[b]) return false;
        if (ObjectLayer[a] != ObjectLayer[b] || Argb[a] != Argb[b]) return false;
        if (!IsFillShape(ShapeKind[a]) || !IsFillShape(ShapeKind[b])) return false;
        if (HasGradient(a) || HasGradient(b)) return false;

        var mergeDistance = connectNearby ? FillMergeDistanceUnits : 0.001f;
        if (!mergeBounds.IntersectsWith(GetObjectWorldBounds(b))) return false;

        var contoursA = FillWorldContours(a);
        var contoursB = FillWorldContours(b);
        if (contoursA.Length == 0 || contoursB.Length == 0) return false;
        var pathsA = ToClipperPaths(contoursA);
        var pathsB = ToClipperPaths(contoursB);
        if (pathsA.Count == 0 || pathsB.Count == 0) return false;
        if (ClipperPathsIntersect(pathsA, pathsB))
        {
            mergedPath = BuildMergedFillPath(pathsA, pathsB);
            return mergedPath.Length > 0;
        }

        var distance = CompoundPolygonDistance(contoursA, contoursB, out var nearestA, out var nearestB);
        if (distance > mergeDistance) return false;

        mergedPath = BuildMergedFillPath(pathsA, pathsB);
        return mergedPath.Length > 0;
    }

    private static bool ClipperPathsIntersect(Paths64 subject, Paths64 clip)
    {
        try
        {
            var intersection = new Paths64();
            var clipper = new Clipper64 { PreserveCollinear = true };
            clipper.AddSubject(subject);
            clipper.AddClip(clip);
            return clipper.Execute(ClipType.Intersection, FillRule.EvenOdd, intersection)
                && intersection.Count > 0;
        }
        catch (Exception ex) when (ex is ClipperLibException or OverflowException)
        {
            return false;
        }
    }

    private static PointF[][] BuildMergedFillPath(Paths64 subject, Paths64 clip)
    {
        if (subject.Count == 0 || clip.Count == 0) return Array.Empty<PointF[]>();
        try
        {
            var solution = new Paths64();
            var union = new Clipper64();
            union.AddSubject(subject);
            union.AddClip(clip);
            if (!union.Execute(ClipType.Union, FillRule.EvenOdd, solution) || solution.Count == 0)
            {
                return Array.Empty<PointF[]>();
            }

            return FromClipperPaths(solution);
        }
        catch (Exception ex) when (ex is ClipperLibException or OverflowException)
        {
            return Array.Empty<PointF[]>();
        }
    }

    private static bool TryRebuildBezierContoursFromBooleanBoundary(
        IReadOnlyList<PathBezierNode[]> sourceContours,
        IReadOnlyList<PointF[]> booleanContours,
        out PathBezierNode[][] rebuiltContours)
    {
        var sources = new List<PreparedBezierCurve>();
        foreach (var contour in sourceContours)
        {
            if (contour.Length < 3) continue;
            for (var segmentIndex = 0; segmentIndex < contour.Length; segmentIndex++)
            {
                var current = contour[segmentIndex];
                var next = contour[(segmentIndex + 1) % contour.Length];
                var curve = new CubicBoundarySegment(
                    current.Anchor,
                    current.OutgoingControl,
                    next.IncomingControl,
                    next.Anchor);
                AddPreparedBezierCurve(sources, curve);
            }
        }

        return TryRebuildBezierContoursFromBooleanBoundary(sources, booleanContours, out rebuiltContours);
    }

    private static void AddPreparedBezierCurve(
        ICollection<PreparedBezierCurve> sources,
        CubicBoundarySegment curve)
    {
        sources.Add(PrepareBezierCurve(curve));
    }

    private static PreparedBezierCurve PrepareBezierCurve(CubicBoundarySegment curve)
    {
        var samples = SampleCubicSegmentWithParameters(curve);
        return PrepareBezierCurve(curve, samples);
    }

    private static PreparedBezierCurve PrepareBezierCurve(
        CubicBoundarySegment curve,
        CurveSample[] samples)
    {
        var bounds = CurveSampleBounds(samples);
        bounds.Inflate(BooleanBezierMaximumErrorUnits, BooleanBezierMaximumErrorUnits);
        return new PreparedBezierCurve(curve, samples, bounds);
    }

    private static bool TryRebuildBezierContoursFromBooleanBoundary(
        IReadOnlyList<PreparedBezierCurve> sources,
        IReadOnlyList<PointF[]> booleanContours,
        out PathBezierNode[][] rebuiltContours)
    {
        rebuiltContours = [];
        if (sources.Count == 0 || booleanContours.Count == 0) return false;

        var result = new PathBezierNode[booleanContours.Count][];
        for (var contourIndex = 0; contourIndex < booleanContours.Count; contourIndex++)
        {
            if (!TryRebuildBezierContourFromBooleanBoundary(
                    booleanContours[contourIndex],
                    sources,
                    out result[contourIndex]))
            {
                return false;
            }
        }

        if (!BooleanBezierContoursMatch(booleanContours, result)) return false;
        rebuiltContours = result;
        return true;
    }

    private static bool TryRebuildBezierContourFromBooleanBoundary(
        PointF[] booleanContour,
        IReadOnlyList<PreparedBezierCurve> sources,
        out PathBezierNode[] rebuiltContour)
    {
        rebuiltContour = [];
        var anchors = OpenPolygon(booleanContour);
        if (anchors.Length < 3) return false;

        var labels = Enumerable.Repeat(-1, anchors.Length).ToArray();
        for (var edgeIndex = 0; edgeIndex < anchors.Length; edgeIndex++)
        {
            var start = anchors[edgeIndex];
            var end = anchors[(edgeIndex + 1) % anchors.Length];
            var midpoint = Midpoint(start, end);
            var bestScore = float.MaxValue;
            for (var sourceIndex = 0; sourceIndex < sources.Count; sourceIndex++)
            {
                var source = sources[sourceIndex];
                if (!source.Bounds.Contains(start)
                    || !source.Bounds.Contains(end)
                    || !source.Bounds.Contains(midpoint))
                {
                    continue;
                }

                if (!TryGetClosestParameterOnCubic(
                        source,
                        start,
                        out var startT,
                        out var startDistance)
                    || !TryGetClosestParameterOnCubic(
                        source,
                        end,
                        out var endT,
                        out var endDistance)
                    || !TryGetClosestParameterOnCubic(
                        source,
                        midpoint,
                        out var midpointT,
                        out var midpointDistance)
                    || Math.Max(startDistance, Math.Max(endDistance, midpointDistance))
                    > BooleanBezierMaximumErrorUnits
                    || Math.Abs(endT - startT) <= 0.0001f)
                {
                    continue;
                }

                var minimumT = Math.Min(startT, endT);
                var maximumT = Math.Max(startT, endT);
                if (midpointT < minimumT - 0.001f || midpointT > maximumT + 0.001f) continue;
                var edgeSubcurve = CubicSubcurve(
                    source.Curve.Start,
                    source.Curve.Control1,
                    source.Curve.Control2,
                    source.Curve.End,
                    minimumT,
                    maximumT);
                var edgeCurve = new CubicBoundarySegment(
                    edgeSubcurve.Start,
                    edgeSubcurve.Control1,
                    edgeSubcurve.Control2,
                    edgeSubcurve.End);
                if (SampleCubicSegment(edgeCurve).Any(point =>
                        DistanceToSegment(point, start, end) > BooleanBezierMaximumErrorUnits))
                {
                    continue;
                }

                var score = startDistance + endDistance + midpointDistance;
                if (score >= bestScore) continue;
                bestScore = score;
                labels[edgeIndex] = sourceIndex;
            }

        }
        var startEdge = 0;
        for (var edgeIndex = 0; edgeIndex < labels.Length; edgeIndex++)
        {
            if (labels[edgeIndex] == labels[(edgeIndex - 1 + labels.Length) % labels.Length]) continue;
            startEdge = edgeIndex;
            break;
        }

        var segments = new List<CubicBoundarySegment>();
        var processed = 0;
        while (processed < anchors.Length)
        {
            var edgeIndex = (startEdge + processed) % anchors.Length;
            var label = labels[edgeIndex];
            var groupLength = 1;
            while (processed + groupLength < anchors.Length
                && labels[(edgeIndex + groupLength) % anchors.Length] == label)
            {
                groupLength++;
            }

            var runPoints = new PointF[groupLength + 1];
            for (var pointIndex = 0; pointIndex <= groupLength; pointIndex++)
            {
                runPoints[pointIndex] = anchors[(edgeIndex + pointIndex) % anchors.Length];
            }

            if (label >= 0
                && TryMatchBooleanCurveRun(
                    runPoints,
                    sources[label],
                    out var matchedCurve))
            {
                segments.Add(matchedCurve);
            }
            else
            {
                var fitted = FitPolylineBezierSegments(
                    runPoints,
                    BooleanBezierSimplificationToleranceUnits);
                if (fitted.Length > 0) segments.AddRange(fitted);
                else AddLinearBooleanSegments(runPoints, segments);
            }

            processed += groupLength;
        }

        if (segments.Count == 0) return false;
        var simplifiedAnchors = SimplifyClosedBooleanContour(anchors);
        if (simplifiedAnchors.Length >= 3 && segments.Count > simplifiedAnchors.Length * 2)
        {
            segments.Clear();
            AddLinearBooleanSegments(
                simplifiedAnchors.Concat([simplifiedAnchors[0]]).ToArray(),
                segments);
        }

        while (segments.Count < 3)
        {
            var splitIndex = Enumerable.Range(0, segments.Count)
                .OrderByDescending(index => PolylineLength(SampleCubicSegment(segments[index])))
                .First();
            var source = segments[splitIndex];
            var first = CubicSubcurve(
                source.Start,
                source.Control1,
                source.Control2,
                source.End,
                0,
                0.5f);
            var second = CubicSubcurve(
                source.Start,
                source.Control1,
                source.Control2,
                source.End,
                0.5f,
                1);
            segments[splitIndex] = new CubicBoundarySegment(
                first.Start,
                first.Control1,
                first.Control2,
                first.End);
            segments.Insert(
                splitIndex + 1,
                new CubicBoundarySegment(
                    second.Start,
                    second.Control1,
                    second.Control2,
                    second.End));
        }

        rebuiltContour = CreateBezierContour(segments);
        return rebuiltContour.Length >= 3;
    }

    private static bool TryMatchBooleanCurveRun(
        IReadOnlyList<PointF> runPoints,
        PreparedBezierCurve source,
        out CubicBoundarySegment matched)
    {
        matched = default;
        if (runPoints.Count < 2
            || !TryGetClosestParameterOnCubic(source, runPoints[0], out var startT, out var startDistance)
            || !TryGetClosestParameterOnCubic(source, runPoints[^1], out var endT, out var endDistance)
            || !TryGetClosestParameterOnCubic(
                source,
                runPoints[runPoints.Count / 2],
                out var middleT,
                out var middleDistance)
            || Math.Max(startDistance, Math.Max(endDistance, middleDistance)) > BooleanBezierMaximumErrorUnits
            || Math.Abs(endT - startT) <= 0.0001f)
        {
            return false;
        }

        var forward = startT < endT;
        var minimumT = Math.Min(startT, endT);
        var maximumT = Math.Max(startT, endT);
        if (middleT < minimumT - 0.001f || middleT > maximumT + 0.001f) return false;

        var subcurve = CubicSubcurve(
            source.Curve.Start,
            source.Curve.Control1,
            source.Curve.Control2,
            source.Curve.End,
            minimumT,
            maximumT);
        var curve = forward
            ? subcurve
            : new CubicBoundarySegment(
                subcurve.End,
                subcurve.Control2,
                subcurve.Control1,
                subcurve.Start);
        var startDelta = new PointF(
            runPoints[0].X - curve.Start.X,
            runPoints[0].Y - curve.Start.Y);
        var endDelta = new PointF(
            runPoints[^1].X - curve.End.X,
            runPoints[^1].Y - curve.End.Y);
        matched = new CubicBoundarySegment(
            runPoints[0],
            new PointF(curve.Control1.X + startDelta.X, curve.Control1.Y + startDelta.Y),
            new PointF(curve.Control2.X + endDelta.X, curve.Control2.Y + endDelta.Y),
            runPoints[^1]);

        var matchedSamples = SampleCubicSegment(matched);
        return runPoints.All(point =>
                DistanceToPolyline(point, matchedSamples) <= BooleanBezierMaximumErrorUnits)
            && matchedSamples.All(point =>
                DistanceToPolyline(point, runPoints) <= BooleanBezierMaximumErrorUnits);
    }

    private static RectangleF CurveSampleBounds(IReadOnlyList<CurveSample> samples)
    {
        var first = samples[0].Point;
        var left = first.X;
        var right = first.X;
        var top = first.Y;
        var bottom = first.Y;
        for (var index = 1; index < samples.Count; index++)
        {
            var point = samples[index].Point;
            left = Math.Min(left, point.X);
            right = Math.Max(right, point.X);
            top = Math.Min(top, point.Y);
            bottom = Math.Max(bottom, point.Y);
        }

        return RectangleF.FromLTRB(left, top, right, bottom);
    }

    private static bool TryGetClosestParameterOnCubic(
        PreparedBezierCurve source,
        PointF point,
        out float parameter,
        out float distance)
    {
        parameter = 0;
        distance = float.MaxValue;
        var low = 0f;
        var high = 1f;
        for (var sampleIndex = 1; sampleIndex < source.Samples.Length; sampleIndex++)
        {
            var a = source.Samples[sampleIndex - 1];
            var b = source.Samples[sampleIndex];
            var dx = b.Point.X - a.Point.X;
            var dy = b.Point.Y - a.Point.Y;
            var lengthSquared = dx * dx + dy * dy;
            var localT = lengthSquared <= DrawingTopologyRules.UnitIntersectionTolerance
                ? 0f
                : Math.Clamp(((point.X - a.Point.X) * dx + (point.Y - a.Point.Y) * dy) / lengthSquared, 0f, 1f);
            var candidateT = a.T + (b.T - a.T) * localT;
            var candidate = CubicPoint(
                source.Curve.Start,
                source.Curve.Control1,
                source.Curve.Control2,
                source.Curve.End,
                candidateT);
            var candidateDistance = Distance(point, candidate);
            if (candidateDistance >= distance) continue;
            parameter = candidateT;
            distance = candidateDistance;
            low = a.T;
            high = b.T;
        }

        if (!float.IsFinite(distance)) return false;
        var closestParameter = parameter;
        var closestDistance = distance;
        for (var iteration = 0; iteration < 10; iteration++)
        {
            var first = low + (high - low) / 3f;
            var second = high - (high - low) / 3f;
            var firstPoint = CubicPoint(
                source.Curve.Start,
                source.Curve.Control1,
                source.Curve.Control2,
                source.Curve.End,
                first);
            var secondPoint = CubicPoint(
                source.Curve.Start,
                source.Curve.Control1,
                source.Curve.Control2,
                source.Curve.End,
                second);
            if (Distance(point, firstPoint) <= Distance(point, secondPoint)) high = second;
            else low = first;
        }

        var refinedParameter = (low + high) * 0.5f;
        var refinedDistance = Distance(point, CubicPoint(
            source.Curve.Start,
            source.Curve.Control1,
            source.Curve.Control2,
            source.Curve.End,
            refinedParameter));
        if (refinedDistance < closestDistance)
        {
            closestParameter = refinedParameter;
            closestDistance = refinedDistance;
        }

        parameter = closestParameter;
        distance = closestDistance;
        return true;
    }

    private static float DistanceToCurveSamples(PointF point, IReadOnlyList<CurveSample> samples)
    {
        if (samples.Count == 0) return float.MaxValue;
        if (samples.Count == 1) return Distance(point, samples[0].Point);
        var distance = float.MaxValue;
        for (var index = 1; index < samples.Count; index++)
        {
            distance = Math.Min(distance, DistanceToSegment(point, samples[index - 1].Point, samples[index].Point));
        }
        return distance;
    }

    private static void AddLinearBooleanSegments(
        IReadOnlyList<PointF> points,
        ICollection<CubicBoundarySegment> segments)
    {
        for (var index = 1; index < points.Count; index++)
        {
            var start = points[index - 1];
            var end = points[index];
            if (SameDrawingUnit(start, end)) continue;
            segments.Add(new CubicBoundarySegment(
                start,
                Lerp(start, end, 1f / 3f),
                Lerp(start, end, 2f / 3f),
                end));
        }
    }

    private static PointF[] SimplifyClosedBooleanContour(PointF[] anchors)
    {
        var path = ToClipperPath(anchors, closed: true);
        if (path.Count < 3) return anchors;
        var simplified = Clipper.SimplifyPath(
            path,
            BooleanBezierSimplificationToleranceUnits * ClipperCoordinateScale,
            isClosedPath: true);
        if (simplified.Count < 3) return anchors;
        var result = new PointF[simplified.Count];
        for (var index = 0; index < simplified.Count; index++)
        {
            result[index] = VectorUnits.Quantize(new PointF(
                (float)(simplified[index].X / ClipperCoordinateScale),
                (float)(simplified[index].Y / ClipperCoordinateScale)));
        }
        return result;
    }

    private static bool BooleanBezierContoursMatch(
        IReadOnlyList<PointF[]> booleanContours,
        IReadOnlyList<PathBezierNode[]> rebuiltContours)
    {
        if (booleanContours.Count != rebuiltContours.Count
            || !TryPreparePathBezierContours(
                rebuiltContours,
                out _,
                out var sampledContours,
                out _)
            || sampledContours.Length != booleanContours.Count)
        {
            return false;
        }

        for (var contourIndex = 0; contourIndex < booleanContours.Count; contourIndex++)
        {
            var expected = ClosePolyline(booleanContours[contourIndex]);
            var actual = ClosePolyline(sampledContours[contourIndex]);
            if (expected.Length < 4
                || actual.Length < 4
                || expected.Any(point =>
                    DistanceToPolyline(point, actual) > BooleanBezierMaximumErrorUnits)
                || actual.Any(point =>
                    DistanceToPolyline(point, expected) > BooleanBezierMaximumErrorUnits))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryBuildNormalizedFillBoundaryPath(
        IReadOnlyList<PointF[]> currentContours,
        IReadOnlyList<PointF[]> referenceContours,
        out PointF[][] normalizedContours)
    {
        normalizedContours = Array.Empty<PointF[]>();
        if (currentContours.Count == 0 || currentContours.Count != referenceContours.Count) return false;

        try
        {
            var currentPaths = new Path64[currentContours.Count];
            for (var index = 0; index < currentContours.Count; index++)
            {
                var path = ToClipperPath(currentContours[index], closed: true);
                if (path.Count < 3 || Math.Abs(Clipper.Area(path)) < 0.5d * ClipperCoordinateScale * ClipperCoordinateScale)
                {
                    return false;
                }

                currentPaths[index] = path;
            }

            var depths = DetermineReferenceContourDepths(referenceContours);
            var desired = new Paths64();
            for (var depth = 0; depth <= depths.Max(); depth++)
            {
                var levelPaths = new Paths64();
                for (var index = 0; index < currentPaths.Length; index++)
                {
                    if (depths[index] != depth) continue;
                    var path = new Path64(currentPaths[index]);
                    if (!Clipper.IsPositive(path)) path.Reverse();
                    levelPaths.Add(path);
                }

                if (levelPaths.Count == 0) continue;
                var levelRegion = Clipper.Union(levelPaths, FillRule.NonZero);
                if (levelRegion.Count == 0) continue;
                if (desired.Count == 0)
                {
                    if ((depth & 1) == 0) desired = levelRegion;
                    continue;
                }

                var combined = new Paths64();
                var clipper = new Clipper64 { PreserveCollinear = true };
                clipper.AddSubject(desired);
                clipper.AddClip(levelRegion);
                var clipType = (depth & 1) == 0 ? ClipType.Union : ClipType.Difference;
                if (!clipper.Execute(clipType, FillRule.NonZero, combined)) return false;
                desired = combined;
            }

            if (desired.Count == 0) return false;

            var currentEvenOdd = new Paths64();
            var currentClipper = new Clipper64 { PreserveCollinear = true };
            currentClipper.AddSubject(new Paths64(currentPaths));
            if (!currentClipper.Execute(ClipType.Union, FillRule.EvenOdd, currentEvenOdd)) return false;

            var difference = new Paths64();
            var comparison = new Clipper64 { PreserveCollinear = true };
            comparison.AddSubject(currentEvenOdd);
            comparison.AddClip(desired);
            if (!comparison.Execute(ClipType.Xor, FillRule.NonZero, difference)
                || difference.Sum(path => Math.Abs(Clipper.Area(path))) < 0.5d * ClipperCoordinateScale * ClipperCoordinateScale)
            {
                return false;
            }

            normalizedContours = FromClipperPaths(desired);
            return normalizedContours.Length > 0;
        }
        catch (Exception ex) when (ex is ClipperLibException or OverflowException)
        {
            return false;
        }
    }

    private static int[] DetermineReferenceContourDepths(IReadOnlyList<PointF[]> contours)
    {
        var depths = new int[contours.Count];
        for (var index = 0; index < contours.Count; index++)
        {
            var contour = contours[index];
            if (contour.Length < 3) continue;
            for (var candidateIndex = 0; candidateIndex < contours.Count; candidateIndex++)
            {
                if (candidateIndex == index) continue;
                var candidate = contours[candidateIndex];
                if (candidate.Length < 3
                    || PolygonBoundariesIntersect(contour, candidate)
                    || !PointInPolygon(contour[0], candidate))
                {
                    continue;
                }

                depths[index]++;
            }
        }

        return depths;
    }

    private static bool PolygonBoundariesIntersect(IReadOnlyList<PointF> a, IReadOnlyList<PointF> b)
    {
        for (var aIndex = 0; aIndex < a.Count; aIndex++)
        {
            var aStart = a[aIndex];
            var aEnd = a[(aIndex + 1) % a.Count];
            for (var bIndex = 0; bIndex < b.Count; bIndex++)
            {
                if (TrySegmentIntersection(aStart, aEnd, b[bIndex], b[(bIndex + 1) % b.Count], out _))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static Paths64 ToClipperPaths(
        IReadOnlyList<PointF[]> contours,
        double minimumAreaUnitsSquared = 0.5d)
    {
        var result = new Paths64(contours.Count);
        var minimumArea = ClipperCoordinateScale * ClipperCoordinateScale * Math.Max(0, minimumAreaUnitsSquared);
        foreach (var contour in contours)
        {
            if (contour.Length < 3) continue;
            var path = new Path64(contour.Length);
            foreach (var point in contour)
            {
                path.Add(new Point64(ToClipperCoordinate(point.X), ToClipperCoordinate(point.Y)));
            }

            path = Clipper.StripDuplicates(path, true);
            if (path.Count >= 3 && Math.Abs(Clipper.Area(path)) >= minimumArea)
            {
                result.Add(path);
            }
        }

        return result;
    }

    private static PointF[][] FromClipperPaths(Paths64 paths, bool normalizeContours = true)
    {
        var contours = new List<PointF[]>(paths.Count);
        foreach (var path in paths)
        {
            if (path.Count < 3) continue;
            var contour = new PointF[path.Count];
            for (var i = 0; i < path.Count; i++)
            {
                contour[i] = new PointF(
                    (float)(path[i].X / ClipperCoordinateScale),
                    (float)(path[i].Y / ClipperCoordinateScale));
            }

            contours.Add(contour);
        }

        return normalizeContours ? NormalizePathContours(contours) : contours.ToArray();
    }

    private static long ToClipperCoordinate(float value)
    {
        return checked((long)Math.Round(value * ClipperCoordinateScale, MidpointRounding.AwayFromZero));
    }

    private PointF[][] FillWorldContours(int objectIndex)
    {
        var shape = ShapeKind.Length > objectIndex ? ShapeKind[objectIndex] : VectorAnimationEngine.ShapeKind.Rectangle;
        if (shape == VectorAnimationEngine.ShapeKind.Path && TryGetPathWorldContours(objectIndex, out var contours))
        {
            return contours;
        }

        var polygon = OpenPolygon(ShapeBoundary(objectIndex));
        return polygon.Length >= 3 ? new[] { polygon } : Array.Empty<PointF[]>();
    }

    private List<FillRegion> BuildFillRegions(int fillIndex, int frame, IReadOnlyList<int> candidates)
    {
        return BuildFillPartition(fillIndex, frame, candidates).Regions;
    }

    private FillPartition BuildFillPartition(
        int fillIndex,
        int frame,
        IReadOnlyList<int> candidates)
    {
        if (_fillPartitionCacheRevision != GeometryRevision)
        {
            _fillPartitionCache.Clear();
            _fillPartitionCacheRevision = GeometryRevision;
        }

        var cacheKey = (fillIndex, frame);
        if (_fillPartitionCache.TryGetValue(cacheKey, out var cached)) return cached;

        var partition = BuildFillPartitionCore(fillIndex, candidates);
        if (_fillPartitionCache.Count >= MaximumTopologyQueryCacheEntries) _fillPartitionCache.Clear();
        _fillPartitionCache[cacheKey] = partition;
        return partition;
    }

    private FillPartition BuildFillPartitionCore(
        int fillIndex,
        IReadOnlyList<int> candidates)
    {
        if ((uint)fillIndex >= ObjectCount || !HasFill(fillIndex)) return new FillPartition([], []);
        PreparedBezierCurve[] fillBoundaryCurves = [];

        try
        {
            var sourceContours = FillWorldContours(fillIndex);
            var source = ToClipperPaths(sourceContours);
            if (source.Count == 0) return new FillPartition([], []);

            fillBoundaryCurves = TryGetEditableFillBezierContours(fillIndex, out var fillBezierContours)
                ? BuildPathBezierCurves(fillBezierContours)
                    .Select(PrepareBezierCurve)
                    .ToArray()
                : [];
            var curves = new List<PreparedBezierCurve>(fillBoundaryCurves);
            var cutterPaths = MergeConnectedOpenCutterPaths(BuildTopologyCutterPaths(
                fillIndex,
                candidates,
                sourceContours,
                fillBoundaryCurves,
                curves));
            if (cutterPaths.Count == 0)
            {
                return new FillPartition(
                    ExecuteFillRegions(source, new Paths64()),
                    curves.ToArray());
            }

            var cutRegions = ExecuteFillRegions(source, BuildTopologyCutterAreas(cutterPaths));
            var fallbackRegions = FallbackFillRegions(fillIndex);
            if (cutRegions.Count > fallbackRegions.Count)
            {
                return new FillPartition(cutRegions, curves.ToArray());
            }

            var baseRegions = ExecuteFillRegions(source, new Paths64());
            var regions = cutRegions.Count > baseRegions.Count ? cutRegions : baseRegions;
            return new FillPartition(regions, curves.ToArray());
        }
        catch (Exception ex) when (ex is ClipperLibException or OverflowException)
        {
            return new FillPartition(
                FallbackFillRegions(fillIndex),
                fillBoundaryCurves);
        }
    }

    private List<TopologyCutterPath> BuildTopologyCutterPaths(
        int fillIndex,
        IReadOnlyList<int> candidates,
        PointF[][] sourceContours,
        IReadOnlyList<PreparedBezierCurve> fillBoundaryCurves,
        ICollection<PreparedBezierCurve>? curves)
    {
        var layer = ObjectLayer[fillIndex];
        var result = new List<TopologyCutterPath>();
        foreach (var candidate in candidates)
        {
            if (candidate == fillIndex || ObjectLayer[candidate] != layer || !HasStroke(candidate)) continue;
            var shape = ShapeKind.Length > candidate ? ShapeKind[candidate] : VectorAnimationEngine.ShapeKind.Rectangle;
            if (shape == VectorAnimationEngine.ShapeKind.Line)
            {
                var curve = PrepareBezierCurve(LineCurve(candidate));
                var paths = BuildInteriorTopologyCutterPaths(
                    curve,
                    fillBoundaryCurves,
                    sourceContours);
                if (paths.Count == 0) continue;
                result.AddRange(paths);
                curves?.Add(curve);
            }
            else if (IsTopologyStrokeShape(shape))
            {
                var samples = StrokeSamples(candidate);
                var closed = samples.Length > 2 && SameDrawingUnit(samples[0].Point, samples[^1].Point);
                var path = ToClipperPath(samples.Select(sample => sample.Point), closed);
                if (path.Count < 2) continue;
                result.Add(new TopologyCutterPath(path, closed));
                if (curves is not null && TryGetFreehandWorldPoints(candidate, out var freehandPoints))
                {
                    foreach (var curve in FitPolylineBezierSegments(
                                 freehandPoints,
                                 ConnectedStrokeEndpointToleranceUnits))
                    {
                        curves.Add(PrepareBezierCurve(curve));
                    }
                }
            }
            else if (IsFillShape(shape))
            {
                foreach (var contour in ShapeBoundaryContours(candidate))
                {
                    var path = ToClipperPath(contour, closed: true);
                    if (path.Count >= 3) result.Add(new TopologyCutterPath(path, Closed: true));
                }
                if (curves is not null
                    && TryGetEditableFillBezierContours(candidate, out var cutterContours))
                {
                    foreach (var curve in BuildPathBezierCurves(cutterContours))
                    {
                        curves.Add(PrepareBezierCurve(curve));
                    }
                }
            }
        }

        return result;
    }

    private List<TopologyCutterPath> BuildInteriorTopologyCutterPaths(
        PreparedBezierCurve cutter,
        IReadOnlyList<PreparedBezierCurve> fillBoundaryCurves,
        PointF[][] sourceContours)
    {
        var splits = new List<DrawingTopologySplit>
        {
            new(0, cutter.Curve.Start),
            new(1, cutter.Curve.End)
        };
        var overlapIntervals = new List<(float StartT, float EndT)>();
        foreach (var boundary in fillBoundaryCurves)
        {
            AddBezierCurveRelationSplits(
                splits,
                cutter,
                boundary,
                includeSourceEndpoints: true,
                overlapIntervals: overlapIntervals);
        }

        if (overlapIntervals.Count == 0)
        {
            // Join Pen segments at their exact anchors before Clipper trims them to the Fill.
            var fullPath = ToClipperPath(cutter.Samples.Select(sample => sample.Point), closed: false);
            return fullPath.Count >= 2
                ? [new TopologyCutterPath(fullPath, Closed: false)]
                : [];
        }

        var normalized = NormalizeStrokeSplits(
            splits.Select(split => RefineCubicTopologySplit(cutter.Curve, split)).ToList());
        var result = new List<TopologyCutterPath>();
        for (var index = 0; index < normalized.Count - 1; index++)
        {
            var startT = normalized[index].T;
            var endT = normalized[index + 1].T;
            if (endT - startT <= 0.0001f) continue;
            var middleT = (startT + endT) * 0.5f;
            var middle = CubicPoint(
                cutter.Curve.Start,
                cutter.Curve.Control1,
                cutter.Curve.Control2,
                cutter.Curve.End,
                middleT);
            if (!PointInCompoundPolygon(middle, sourceContours)
                || overlapIntervals.Any(overlap =>
                    middleT >= overlap.StartT - 0.0001f
                    && middleT <= overlap.EndT + 0.0001f))
            {
                continue;
            }

            var curve = CubicSubcurve(
                cutter.Curve.Start,
                cutter.Curve.Control1,
                cutter.Curve.Control2,
                cutter.Curve.End,
                startT,
                endT);
            var samples = SampleCubicSegment(curve);
            if (PolylineLength(samples) < DrawingTopologyRules.MinStrokeSegmentUnits) continue;
            var path = ToClipperPath(samples, closed: false);
            if (path.Count >= 2) result.Add(new TopologyCutterPath(path, Closed: false));
        }
        return result;
    }

    private void AddBezierCurveRelationSplits(
        List<DrawingTopologySplit> splits,
        PreparedBezierCurve source,
        PreparedBezierCurve other,
        bool includeSourceEndpoints,
        ICollection<(float StartT, float EndT)>? overlapIntervals = null)
    {
        if (!source.Bounds.IntersectsWith(other.Bounds)) return;
        if (TryGetBezierSubcurveInterval(source, other.Curve, out var startT, out var endT))
        {
            overlapIntervals?.Add((startT, endT));
            splits.Add(new DrawingTopologySplit(startT, CubicPoint(
                source.Curve.Start,
                source.Curve.Control1,
                source.Curve.Control2,
                source.Curve.End,
                startT)));
            splits.Add(new DrawingTopologySplit(endT, CubicPoint(
                source.Curve.Start,
                source.Curve.Control1,
                source.Curve.Control2,
                source.Curve.End,
                endT)));
            return;
        }

        if (TryGetBezierSubcurveInterval(other, source.Curve, out _, out _))
        {
            overlapIntervals?.Add((0, 1));
            return;
        }
        AddCurveCurveIntersections(
            splits,
            source.Samples,
            other.Samples,
            includeSourceEndpoints);
    }

    private static bool TryGetBezierSubcurveInterval(
        PreparedBezierCurve source,
        CubicBoundarySegment candidate,
        out float startT,
        out float endT)
    {
        startT = 0;
        endT = 0;
        if (!TryGetClosestParameterOnCubic(
                source,
                candidate.Start,
                out var candidateStartT,
                out var startDistance)
            || !TryGetClosestParameterOnCubic(
                source,
                candidate.End,
                out var candidateEndT,
                out var endDistance)
            || Math.Max(startDistance, endDistance) > BezierCoincidenceToleranceUnits)
        {
            return false;
        }

        var reversed = candidateStartT > candidateEndT;
        startT = Math.Min(candidateStartT, candidateEndT);
        endT = Math.Max(candidateStartT, candidateEndT);
        if (endT - startT <= 0.0001f) return false;
        var subcurve = CubicSubcurve(
            source.Curve.Start,
            source.Curve.Control1,
            source.Curve.Control2,
            source.Curve.End,
            startT,
            endT);
        if (reversed)
        {
            subcurve = new CubicBoundarySegment(
                subcurve.End,
                subcurve.Control2,
                subcurve.Control1,
                subcurve.Start);
        }

        return CubicControlPointsMatch(
            subcurve.Start,
            subcurve.Control1,
            subcurve.Control2,
            subcurve.End,
            candidate.Start,
            candidate.Control1,
            candidate.Control2,
            candidate.End,
            ConnectedStrokeEndpointToleranceUnits);
    }

    private static Paths64 BuildTopologyCutterAreas(IReadOnlyList<TopologyCutterPath> paths)
    {
        if (paths.Count == 0) return new Paths64();
        var offset = new ClipperOffset(2d, 0d, preserveCollinear: true, reverseSolution: false)
        {
            MergeGroups = true
        };
        foreach (var path in paths)
        {
            var offsetPath = path.Closed ? path.Path : ExtendOpenClipperPath(path.Path);
            offset.AddPath(offsetPath, JoinType.Miter, path.Closed ? EndType.Joined : EndType.Square);
        }

        var cutterAreas = new Paths64();
        offset.Execute(TopologyCutterHalfWidthClipper, cutterAreas);
        return cutterAreas.Count > 1 ? Clipper.Union(cutterAreas, FillRule.NonZero) : cutterAreas;
    }

    private static Path64 ExtendOpenClipperPath(Path64 source)
    {
        if (source.Count < 2) return source;
        var result = new Path64(source.Count);
        foreach (var point in source) result.Add(point);

        var extension = DrawingTopologyRules.MinStrokeSegmentUnits * ClipperCoordinateScale;
        result[0] = ExtendClipperEndpoint(result[0], result[1], extension);
        result[^1] = ExtendClipperEndpoint(result[^1], result[^2], extension);
        return result;
    }

    private static Point64 ExtendClipperEndpoint(Point64 endpoint, Point64 neighbor, double extension)
    {
        var dx = (double)endpoint.X - neighbor.X;
        var dy = (double)endpoint.Y - neighbor.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length <= 0.001d) return endpoint;
        return new Point64(
            checked(endpoint.X + (long)Math.Round(dx / length * extension, MidpointRounding.AwayFromZero)),
            checked(endpoint.Y + (long)Math.Round(dy / length * extension, MidpointRounding.AwayFromZero)));
    }

    private static List<TopologyCutterPath> MergeConnectedOpenCutterPaths(List<TopologyCutterPath> source)
    {
        var result = source.ToList();
        var endpoints = new List<Point64>();
        foreach (var path in source)
        {
            if (path.Closed || path.Path.Count < 2) continue;
            AddClipperEndpoint(endpoints, path.Path[0]);
            AddClipperEndpoint(endpoints, path.Path[^1]);
        }

        var joinable = endpoints
            .Where(point => source.Count(path => ClipperPathTouchesPoint(path.Path, point, path.Closed)) == 2)
            .ToArray();
        var merged = true;
        while (merged)
        {
            merged = false;
            for (var a = 0; a < result.Count && !merged; a++)
            {
                if (result[a].Closed) continue;
                for (var b = a + 1; b < result.Count; b++)
                {
                    if (result[b].Closed || !TryJoinClipperPaths(result[a].Path, result[b].Path, joinable, out var joined)) continue;
                    var closed = joined.Count > 2 && joined[0] == joined[^1];
                    if (closed) joined = Clipper.StripDuplicates(joined, true);
                    result[a] = new TopologyCutterPath(joined, closed);
                    result.RemoveAt(b);
                    merged = true;
                    break;
                }
            }
        }

        return result;
    }

    private static bool TryJoinClipperPaths(Path64 a, Path64 b, IReadOnlyList<Point64> joinable, out Path64 joined)
    {
        joined = new Path64();
        if (a.Count < 2 || b.Count < 2) return false;

        if (ClipperPointsNear(a[^1], b[0]) && IsJoinableClipperEndpoint(a[^1], joinable))
        {
            joined = ConcatClipperPaths(a, reverseA: false, b, reverseB: false);
            return true;
        }

        if (ClipperPointsNear(a[^1], b[^1]) && IsJoinableClipperEndpoint(a[^1], joinable))
        {
            joined = ConcatClipperPaths(a, reverseA: false, b, reverseB: true);
            return true;
        }

        if (ClipperPointsNear(a[0], b[^1]) && IsJoinableClipperEndpoint(a[0], joinable))
        {
            joined = ConcatClipperPaths(b, reverseA: false, a, reverseB: false);
            return true;
        }

        if (ClipperPointsNear(a[0], b[0]) && IsJoinableClipperEndpoint(a[0], joinable))
        {
            joined = ConcatClipperPaths(a, reverseA: true, b, reverseB: false);
            return true;
        }

        return false;
    }

    private static void AddClipperEndpoint(List<Point64> endpoints, Point64 point)
    {
        foreach (var endpoint in endpoints)
        {
            if (ClipperPointsNear(endpoint, point)) return;
        }

        endpoints.Add(point);
    }

    private static bool IsJoinableClipperEndpoint(Point64 point, IReadOnlyList<Point64> joinable)
    {
        foreach (var candidate in joinable)
        {
            if (ClipperPointsNear(point, candidate)) return true;
        }

        return false;
    }

    private static bool ClipperPathTouchesPoint(Path64 path, Point64 point, bool closed)
    {
        if (path.Count == 0) return false;
        if (path.Count == 1) return ClipperPointsNear(path[0], point);
        for (var i = 0; i < path.Count - 1; i++)
        {
            if (DistanceToClipperSegment(point, path[i], path[i + 1]) <= DrawingTopologyRules.MinStrokeSegmentUnits * ClipperCoordinateScale)
            {
                return true;
            }
        }

        if (closed && DistanceToClipperSegment(point, path[^1], path[0]) <= DrawingTopologyRules.MinStrokeSegmentUnits * ClipperCoordinateScale)
        {
            return true;
        }

        return false;
    }

    private static double DistanceToClipperSegment(Point64 point, Point64 start, Point64 end)
    {
        var vx = (double)end.X - start.X;
        var vy = (double)end.Y - start.Y;
        var lengthSquared = vx * vx + vy * vy;
        if (lengthSquared <= 0.001d)
        {
            var dx = (double)point.X - start.X;
            var dy = (double)point.Y - start.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        var t = Math.Clamp(((point.X - start.X) * vx + (point.Y - start.Y) * vy) / lengthSquared, 0d, 1d);
        var projectedX = start.X + vx * t;
        var projectedY = start.Y + vy * t;
        var offsetX = point.X - projectedX;
        var offsetY = point.Y - projectedY;
        return Math.Sqrt(offsetX * offsetX + offsetY * offsetY);
    }

    private static bool ClipperPointsNear(Point64 a, Point64 b)
    {
        var tolerance = DrawingTopologyRules.MinStrokeSegmentUnits * ClipperCoordinateScale;
        var dx = (double)a.X - b.X;
        var dy = (double)a.Y - b.Y;
        return dx * dx + dy * dy <= tolerance * tolerance;
    }

    private static Path64 ConcatClipperPaths(Path64 a, bool reverseA, Path64 b, bool reverseB)
    {
        var result = new Path64(a.Count + b.Count - 1);
        AddClipperPath(result, a, reverseA, skipFirst: false);
        AddClipperPath(result, b, reverseB, skipFirst: true);
        return result;
    }

    private static void AddClipperPath(Path64 destination, Path64 source, bool reverse, bool skipFirst)
    {
        for (var i = skipFirst ? 1 : 0; i < source.Count; i++)
        {
            destination.Add(reverse ? source[source.Count - 1 - i] : source[i]);
        }
    }

    private static Path64 ToClipperPath(IEnumerable<PointF> points, bool closed)
    {
        var result = new Path64();
        foreach (var point in points)
        {
            result.Add(new Point64(ToClipperCoordinate(point.X), ToClipperCoordinate(point.Y)));
        }

        return Clipper.StripDuplicates(result, closed);
    }

    private static List<FillRegion> ExecuteFillRegions(Paths64 source, Paths64 cutters)
    {
        var tree = new PolyTree64();
        var clipper = new Clipper64 { PreserveCollinear = true };
        clipper.AddSubject(source);
        if (cutters.Count > 0) clipper.AddClip(cutters);
        var clipType = cutters.Count > 0 ? ClipType.Difference : ClipType.Union;
        if (!clipper.Execute(clipType, FillRule.EvenOdd, tree)) return new List<FillRegion>();

        var regions = new List<FillRegion>();
        for (var i = 0; i < tree.Count; i++) CollectFillRegions(tree[i], regions);
        regions.Sort(CompareFillRegions);
        return regions;
    }

    private static void CollectFillRegions(PolyPath64 node, List<FillRegion> regions)
    {
        if (!node.IsHole)
        {
            var paths = new Paths64 { node.Polygon! };
            for (var i = 0; i < node.Count; i++)
            {
                var child = node[i];
                if (child.IsHole) paths.Add(child.Polygon!);
            }

            var contours = FromClipperPaths(paths);
            if (contours.Length > 0)
            {
                regions.Add(new FillRegion(
                    contours,
                    ContourBounds(contours),
                    CompoundContourArea(contours)));
            }
        }

        for (var i = 0; i < node.Count; i++) CollectFillRegions(node[i], regions);
    }

    private static List<FillRegion> NormalizeFillRegionContours(PointF[][] contours)
    {
        if (contours.Length == 0) return new List<FillRegion>();
        var paths = ToClipperPaths(contours);
        if (paths.Count == 0) return new List<FillRegion>();

        var tree = new PolyTree64();
        var clipper = new Clipper64 { PreserveCollinear = true };
        clipper.AddSubject(paths);
        if (!clipper.Execute(ClipType.Union, FillRule.EvenOdd, tree)) return new List<FillRegion>();

        var regions = new List<FillRegion>();
        for (var i = 0; i < tree.Count; i++) CollectNormalizedFillRegions(tree[i], regions);
        return regions;
    }

    private static void CollectNormalizedFillRegions(PolyPath64 node, List<FillRegion> regions)
    {
        if (!node.IsHole)
        {
            var paths = new Paths64 { node.Polygon! };
            for (var i = 0; i < node.Count; i++)
            {
                var child = node[i];
                if (child.IsHole) paths.Add(child.Polygon!);
            }

            var contours = FromClipperPaths(paths);
            if (contours.Length > 0)
            {
                regions.Add(new FillRegion(contours, ContourBounds(contours), CompoundContourArea(contours)));
            }
        }

        for (var i = 0; i < node.Count; i++) CollectNormalizedFillRegions(node[i], regions);
    }

    private List<FillRegion> FallbackFillRegions(int fillIndex)
    {
        var contours = FillWorldContours(fillIndex);
        return contours.Length > 0
            ? new List<FillRegion> { new(contours, ContourBounds(contours), CompoundContourArea(contours)) }
            : new List<FillRegion>();
    }

    private static int CompareFillRegions(FillRegion a, FillRegion b)
    {
        var comparison = a.Bounds.Top.CompareTo(b.Bounds.Top);
        if (comparison != 0) return comparison;
        comparison = a.Bounds.Left.CompareTo(b.Bounds.Left);
        if (comparison != 0) return comparison;
        comparison = b.Area.CompareTo(a.Area);
        if (comparison != 0) return comparison;
        comparison = a.Bounds.Bottom.CompareTo(b.Bounds.Bottom);
        return comparison != 0 ? comparison : a.Bounds.Right.CompareTo(b.Bounds.Right);
    }

    private static RectangleF ContourBounds(PointF[][] contours)
    {
        var first = contours[0][0];
        var left = first.X;
        var right = first.X;
        var top = first.Y;
        var bottom = first.Y;
        foreach (var contour in contours)
        {
            foreach (var point in contour)
            {
                left = Math.Min(left, point.X);
                right = Math.Max(right, point.X);
                top = Math.Min(top, point.Y);
                bottom = Math.Max(bottom, point.Y);
            }
        }

        return RectangleF.FromLTRB(left, top, right, bottom);
    }

    private static float CompoundContourArea(PointF[][] contours)
    {
        var area = 0f;
        foreach (var contour in contours) area += Math.Abs(PolygonArea(contour));
        return area;
    }

    private static float CompoundPolygonDistance(PointF[][] a, PointF[][] b, out PointF nearestA, out PointF nearestB)
    {
        nearestA = a[0][0];
        nearestB = b[0][0];
        if (a.Any(contour => contour.Any(point => PointInCompoundPolygon(point, b)))
            || b.Any(contour => contour.Any(point => PointInCompoundPolygon(point, a))))
        {
            nearestA = nearestB = a[0][0];
            return 0;
        }

        var best = float.MaxValue;
        foreach (var contourA in a)
        {
            foreach (var contourB in b)
            {
                var distance = PolygonDistance(contourA, contourB, out var candidateA, out var candidateB);
                if (distance >= best) continue;
                best = distance;
                nearestA = candidateA;
                nearestB = candidateB;
            }
        }

        return best;
    }

    private static float PolygonDistance(PointF[] a, PointF[] b, out PointF nearestA, out PointF nearestB)
    {
        nearestA = a[0];
        nearestB = b[0];
        if (a.Any(point => PointInPolygon(point, b)) || b.Any(point => PointInPolygon(point, a)))
        {
            nearestA = nearestB = a[0];
            return 0;
        }

        var best = float.MaxValue;
        for (var i = 0; i < a.Length; i++)
        {
            var a0 = a[i];
            var a1 = a[(i + 1) % a.Length];
            for (var j = 0; j < b.Length; j++)
            {
                var b0 = b[j];
                var b1 = b[(j + 1) % b.Length];
                if (TrySegmentIntersection(a0, a1, b0, b1, out _))
                {
                    nearestA = nearestB = a0;
                    return 0;
                }

                TryUpdateNearest(a0, b0, b1, ref best, ref nearestA, ref nearestB);
                TryUpdateNearest(a1, b0, b1, ref best, ref nearestA, ref nearestB);
                TryUpdateNearest(b0, a0, a1, ref best, ref nearestB, ref nearestA);
                TryUpdateNearest(b1, a0, a1, ref best, ref nearestB, ref nearestA);
            }
        }

        return best;
    }

    private static void TryUpdateNearest(PointF point, PointF segmentStart, PointF segmentEnd, ref float best, ref PointF nearestPoint, ref PointF nearestOnSegment)
    {
        var t = SegmentProjectionT(point, segmentStart, segmentEnd);
        var projected = Lerp(segmentStart, segmentEnd, t);
        var distance = Distance(point, projected);
        if (distance >= best) return;
        best = distance;
        nearestPoint = point;
        nearestOnSegment = projected;
    }

    private static bool PointInPolygonOrOnBoundary(PointF point, PointF[] polygon)
    {
        if (PointInPolygon(point, polygon)) return true;
        for (var i = 0; i < polygon.Length; i++)
        {
            if (DistanceToSegment(point, polygon[i], polygon[(i + 1) % polygon.Length]) <= 0.75f) return true;
        }

        return false;
    }

    private List<int> CollectActiveCandidates(int frame)
    {
        var result = new List<int>(ObjectCount);
        for (var i = 0; i < ObjectCount; i++)
        {
            if (IsObjectActive(i, frame)) result.Add(i);
        }

        return result;
    }

    private IReadOnlyList<int> CollectTopologyCandidates(int objectIndex, int frame)
    {
        if ((uint)objectIndex >= ObjectCount) return Array.Empty<int>();

        EnsureInteractiveQueryCacheRevision();
        var cacheKey = (objectIndex, frame);
        if (_topologyCandidateCache.TryGetValue(cacheKey, out var cached)) return cached;

        var layer = ObjectLayer[objectIndex];
        var bounds = GetObjectWorldBounds(objectIndex);
        bounds.Inflate(DrawingTopologyRules.MinStrokeSegmentUnits, DrawingTopologyRules.MinStrokeSegmentUnits);
        var candidates = QueryObjects(bounds, frame)
            .Where(index => ObjectLayer[index] == layer)
            .ToArray();
        if (_topologyCandidateCache.Count >= MaximumTopologyQueryCacheEntries) _topologyCandidateCache.Clear();
        _topologyCandidateCache[cacheKey] = candidates;
        return candidates;
    }

    private void EnsureInteractiveQueryCacheRevision()
    {
        if (_interactiveQueryCacheRevision == GeometryRevision) return;
        _topologyCandidateCache.Clear();
        _hitStrokeSplitCache.Clear();
        _hitBoundaryPartCache.Clear();
        _interactiveQueryCacheRevision = GeometryRevision;
    }

    private void InvalidateInteractiveQueryCaches()
    {
        _topologyCandidateCache.Clear();
        _hitStrokeSplitCache.Clear();
        _hitBoundaryPartCache.Clear();
        _interactiveQueryCacheRevision = -1;
    }

}
