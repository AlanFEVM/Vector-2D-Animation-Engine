using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Clipper2Lib;

namespace VectorAnimationEngine;

internal sealed partial class VectorScene
{
    private bool SetFreehandBezierNodesCore(int objectIndex, IReadOnlyList<PathBezierNode> worldNodes)
    {
        if ((uint)objectIndex >= ObjectCount
            || ShapeKind[objectIndex] != VectorAnimationEngine.ShapeKind.Freeform
            || !TryPrepareFreehandBezierNodes(worldNodes, out var exactNodes, out var sampledPoints, out var bounds))
        {
            return false;
        }

        var center = VectorUnits.Quantize(new PointF(
            bounds.Left + bounds.Width * 0.5f,
            bounds.Top + bounds.Height * 0.5f));
        StorePreparedFreehandBezierNodes(objectIndex, exactNodes, sampledPoints, bounds, center);
        return true;
    }

    private void StorePreparedFreehandBezierNodes(
        int objectIndex,
        IReadOnlyList<PathBezierNode> exactWorldNodes,
        IReadOnlyList<PointF> sampledWorldPoints,
        RectangleF bounds,
        PointF center)
    {
        X[objectIndex] = center.X;
        Y[objectIndex] = center.Y;
        Width[objectIndex] = Math.Max(
            Stroke[objectIndex],
            VectorUnits.Quantize(bounds.Width + Stroke[objectIndex]));
        Height[objectIndex] = Math.Max(
            Stroke[objectIndex],
            VectorUnits.Quantize(bounds.Height + Stroke[objectIndex]));
        Angle[objectIndex] = 0;
        _freehandBezierLocalNodes[objectIndex] = exactWorldNodes
            .Select(node => new PathBezierNode(
                VectorUnits.Quantize(new PointF(node.Anchor.X - center.X, node.Anchor.Y - center.Y)),
                VectorUnits.Quantize(new PointF(
                    node.IncomingControl.X - center.X,
                    node.IncomingControl.Y - center.Y)),
                VectorUnits.Quantize(new PointF(
                    node.OutgoingControl.X - center.X,
                    node.OutgoingControl.Y - center.Y))))
            .ToArray();
        _legacyFreehandBezierNodeCache.Remove(objectIndex);
        _freehandLocalPoints[objectIndex] = sampledWorldPoints
            .Select(point => VectorUnits.Quantize(new PointF(
                point.X - center.X,
                point.Y - center.Y)))
            .ToArray();
    }

    private void CompleteFreehandBezierMutation(bool rebuildGeometryIndex)
    {
        if (rebuildGeometryIndex)
        {
            RebuildGeometryIndex();
            RebuildSummaries();
            return;
        }

        GeometryRevision++;
        InvalidateDeferredTopologyQueries();
    }

    private static bool TryPrepareFreehandBezierNodes(
        IReadOnlyList<PathBezierNode> worldNodes,
        out PathBezierNode[] exactNodes,
        out PointF[] sampledPoints,
        out RectangleF bounds)
    {
        exactNodes = Array.Empty<PathBezierNode>();
        sampledPoints = Array.Empty<PointF>();
        bounds = RectangleF.Empty;
        if (worldNodes.Count < 2) return false;

        var exact = new PathBezierNode[worldNodes.Count];
        for (var nodeIndex = 0; nodeIndex < worldNodes.Count; nodeIndex++)
        {
            var node = worldNodes[nodeIndex];
            if (!Finite(node.Anchor) || !Finite(node.IncomingControl) || !Finite(node.OutgoingControl))
            {
                return false;
            }

            exact[nodeIndex] = new PathBezierNode(
                VectorUnits.Quantize(node.Anchor),
                VectorUnits.Quantize(node.IncomingControl),
                VectorUnits.Quantize(node.OutgoingControl));
        }

        var samples = SampleOpenFreehandBezierNodes(exact);
        var hasBounds = false;
        for (var segmentIndex = 0; segmentIndex < exact.Length - 1; segmentIndex++)
        {
            var current = exact[segmentIndex];
            var next = exact[segmentIndex + 1];
            var curve = new CubicBoundarySegment(
                current.Anchor,
                current.OutgoingControl,
                next.IncomingControl,
                next.Anchor);
            var curveBounds = CubicCurveBounds(
                curve.Start,
                curve.Control1,
                curve.Control2,
                curve.End);
            bounds = hasBounds ? RectangleF.Union(bounds, curveBounds) : curveBounds;
            hasBounds = true;
        }

        if (!hasBounds || samples.Length < 2) return false;
        exactNodes = exact;
        sampledPoints = samples;
        return true;
    }

    internal static bool TryCreateOpenFreehandBezierNodes(
        IReadOnlyList<PointF> sourcePoints,
        out PathBezierNode[] nodes)
    {
        return TryCreateOpenFreehandBezierNodes(
            sourcePoints,
            DrawingTopologyRules.MinStrokeSegmentUnits,
            out nodes);
    }

    internal static bool TryCreateOpenFreehandBezierNodes(
        IReadOnlyList<PointF> sourcePoints,
        float maximumError,
        out PathBezierNode[] nodes)
    {
        nodes = Array.Empty<PathBezierNode>();
        var points = NormalizeFreehandPoints(sourcePoints);
        if (points.Length < 2 || !float.IsFinite(maximumError)) return false;

        var segments = new List<CubicBoundarySegment>();
        FitOpenPolylineBezierSegments(
            points,
            Math.Max(0.1f, maximumError),
            segments);
        if (segments.Count == 0) return false;

        nodes = new PathBezierNode[segments.Count + 1];
        nodes[0] = new PathBezierNode(
            VectorUnits.Quantize(segments[0].Start),
            VectorUnits.Quantize(segments[0].Start),
            VectorUnits.Quantize(segments[0].Control1));
        for (var segmentIndex = 1; segmentIndex < segments.Count; segmentIndex++)
        {
            var previous = segments[segmentIndex - 1];
            var current = segments[segmentIndex];
            nodes[segmentIndex] = new PathBezierNode(
                VectorUnits.Quantize(current.Start),
                VectorUnits.Quantize(previous.Control2),
                VectorUnits.Quantize(current.Control1));
        }

        var last = segments[^1];
        nodes[^1] = new PathBezierNode(
            VectorUnits.Quantize(last.End),
            VectorUnits.Quantize(last.Control2),
            VectorUnits.Quantize(last.End));
        return true;
    }

    internal static PointF[] SampleOpenFreehandBezierNodes(IReadOnlyList<PathBezierNode> nodes)
    {
        return SampleOpenFreehandBezierNodesWithParameters(nodes)
            .Select(sample => sample.Point)
            .ToArray();
    }

    private static OpenBezierSample[] SampleOpenFreehandBezierNodesWithParameters(
        IReadOnlyList<PathBezierNode> nodes)
    {
        if (nodes.Count < 2) return [];
        var result = new List<OpenBezierSample>(nodes.Count * 4);
        for (var segmentIndex = 0; segmentIndex < nodes.Count - 1; segmentIndex++)
        {
            var current = nodes[segmentIndex];
            var next = nodes[segmentIndex + 1];
            if (!Finite(current.Anchor)
                || !Finite(current.OutgoingControl)
                || !Finite(next.IncomingControl)
                || !Finite(next.Anchor))
            {
                return [];
            }

            var curve = new CubicBoundarySegment(
                current.Anchor,
                current.OutgoingControl,
                next.IncomingControl,
                next.Anchor);
            var linearControl1 = Lerp(curve.Start, curve.End, 1f / 3f);
            var linearControl2 = Lerp(curve.Start, curve.End, 2f / 3f);
            var segmentSamples = Distance(curve.Control1, linearControl1) <= 1f
                && Distance(curve.Control2, linearControl2) <= 1f
                    ? new[] { new CurveSample(0, curve.Start), new CurveSample(1, curve.End) }
                    : SampleCubicSegmentWithParameters(curve);
            for (var sampleIndex = segmentIndex == 0 ? 0 : 1; sampleIndex < segmentSamples.Length; sampleIndex++)
            {
                var sample = segmentSamples[sampleIndex];
                var point = VectorUnits.Quantize(sample.Point);
                if (result.Count > 0 && result[^1].Point == point) continue;
                result.Add(new OpenBezierSample(segmentIndex, sample.T, point));
            }
        }

        return result.ToArray();
    }

    private bool SetPathBezierContoursCore(int objectIndex, IReadOnlyList<PathBezierNode[]> worldContours)
    {
        if ((uint)objectIndex >= ObjectCount
            || !TryPreparePathBezierContours(worldContours, out var exactContours, out var sampledContours, out var bounds))
        {
            return false;
        }

        var gradientPath = TryGetGradientPathWorldPoints(objectIndex, out var existingGradientPath)
            ? existingGradientPath
            : Array.Empty<PointF>();
        var shapeGradientMapping = TryGetShapeGradientMappingWorldContours(objectIndex, out var existingShapeMapping)
            ? existingShapeMapping
            : Array.Empty<PointF[]>();
        var center = VectorUnits.Quantize(new PointF(
            bounds.Left + bounds.Width * 0.5f,
            bounds.Top + bounds.Height * 0.5f));

        ShapeKind[objectIndex] = VectorAnimationEngine.ShapeKind.Path;
        ShapeVertexCounts[objectIndex] = 0;
        StorePreparedPathBezierContours(objectIndex, exactContours, sampledContours, bounds, center);
        if (gradientPath.Length > 1) SetGradientPath(objectIndex, gradientPath);
        if (shapeGradientMapping.Length > 0) SetShapeGradientMapping(objectIndex, shapeGradientMapping);
        return true;
    }

    private void StorePreparedPathBezierContours(
        int objectIndex,
        PathBezierNode[][] exactWorldContours,
        PointF[][] sampledWorldContours,
        RectangleF bounds,
        PointF center)
    {
        X[objectIndex] = center.X;
        Y[objectIndex] = center.Y;
        Width[objectIndex] = Math.Max(1, VectorUnits.Quantize(bounds.Width));
        Height[objectIndex] = Math.Max(1, VectorUnits.Quantize(bounds.Height));
        Angle[objectIndex] = 0;
        _pathBezierLocalContours[objectIndex] = exactWorldContours
            .Select(contour => contour.Select(node => new PathBezierNode(
                VectorUnits.Quantize(new PointF(node.Anchor.X - center.X, node.Anchor.Y - center.Y)),
                VectorUnits.Quantize(new PointF(
                    node.IncomingControl.X - center.X,
                    node.IncomingControl.Y - center.Y)),
                VectorUnits.Quantize(new PointF(
                    node.OutgoingControl.X - center.X,
                    node.OutgoingControl.Y - center.Y)))).ToArray())
            .ToArray();
        _pathLocalContours[objectIndex] = sampledWorldContours
            .Select(contour => contour.Select(point => VectorUnits.Quantize(new PointF(
                point.X - center.X,
                point.Y - center.Y))).ToArray())
            .ToArray();
    }

    private void CompletePathBezierMutation(bool rebuildGeometryIndex)
    {
        if (rebuildGeometryIndex)
        {
            RebuildGeometryIndex();
            RebuildSummaries();
            return;
        }

        GeometryRevision++;
    }

    private static bool TryPreparePathBezierContours(
        IReadOnlyList<PathBezierNode[]> worldContours,
        out PathBezierNode[][] exactContours,
        out PointF[][] sampledContours,
        out RectangleF bounds)
    {
        exactContours = Array.Empty<PathBezierNode[]>();
        sampledContours = Array.Empty<PointF[]>();
        bounds = RectangleF.Empty;
        if (worldContours.Count == 0) return false;

        var exact = new PathBezierNode[worldContours.Count][];
        var sampled = new PointF[worldContours.Count][];
        var hasBounds = false;
        for (var contourIndex = 0; contourIndex < worldContours.Count; contourIndex++)
        {
            var source = worldContours[contourIndex];
            if (source.Length < 3) return false;
            var contour = new PathBezierNode[source.Length];
            for (var nodeIndex = 0; nodeIndex < source.Length; nodeIndex++)
            {
                var node = source[nodeIndex];
                if (!Finite(node.Anchor) || !Finite(node.IncomingControl) || !Finite(node.OutgoingControl)) return false;
                contour[nodeIndex] = new PathBezierNode(
                    VectorUnits.Quantize(node.Anchor),
                    VectorUnits.Quantize(node.IncomingControl),
                    VectorUnits.Quantize(node.OutgoingControl));
            }

            var sampledContour = SamplePathBezierContour(contour);
            var cleaned = RemoveDuplicatePolygonPoints(sampledContour.Select(VectorUnits.Quantize).ToList());
            if (cleaned.Count < 3 || Math.Abs(PolygonArea(cleaned)) < 0.5f) return false;
            exact[contourIndex] = contour;
            sampled[contourIndex] = cleaned.ToArray();

            for (var segmentIndex = 0; segmentIndex < contour.Length; segmentIndex++)
            {
                var current = contour[segmentIndex];
                var next = contour[(segmentIndex + 1) % contour.Length];
                var curveBounds = CubicCurveBounds(
                    current.Anchor,
                    current.OutgoingControl,
                    next.IncomingControl,
                    next.Anchor);
                bounds = hasBounds ? RectangleF.Union(bounds, curveBounds) : curveBounds;
                hasBounds = true;
            }
        }

        if (!hasBounds) return false;
        exactContours = exact;
        sampledContours = sampled;
        return true;
    }

    private static bool TryNormalizeLocalPathBezierContours(
        IReadOnlyList<PathBezierNode[]> localContours,
        out PathBezierNode[][] exactContours,
        out PointF[][] sampledContours)
    {
        return TryPreparePathBezierContours(localContours, out exactContours, out sampledContours, out _);
    }

    private static PointF[] SamplePathBezierContour(IReadOnlyList<PathBezierNode> contour)
    {
        var result = new List<PointF>(contour.Count * 4);
        for (var segmentIndex = 0; segmentIndex < contour.Count; segmentIndex++)
        {
            var current = contour[segmentIndex];
            var next = contour[(segmentIndex + 1) % contour.Count];
            var samples = SampleCubicSegment(new CubicBoundarySegment(
                current.Anchor,
                current.OutgoingControl,
                next.IncomingControl,
                next.Anchor));
            for (var sampleIndex = segmentIndex == 0 ? 0 : 1; sampleIndex < samples.Length; sampleIndex++)
            {
                result.Add(samples[sampleIndex]);
            }
        }

        if (result.Count > 1 && Distance(result[0], result[^1]) <= 0.001f) result.RemoveAt(result.Count - 1);
        return result.ToArray();
    }

    private static PathBezierNode[] CreateLinearBezierContour(IReadOnlyList<PointF> points)
    {
        var result = new PathBezierNode[points.Count];
        for (var index = 0; index < points.Count; index++)
        {
            var previous = points[(index - 1 + points.Count) % points.Count];
            var current = points[index];
            var next = points[(index + 1) % points.Count];
            result[index] = new PathBezierNode(
                current,
                Lerp(previous, current, 2f / 3f),
                Lerp(current, next, 1f / 3f));
        }

        return result;
    }

    private static PathBezierNode[] CreateBezierContour(IReadOnlyList<CubicBoundarySegment> segments)
    {
        var result = new PathBezierNode[segments.Count];
        for (var index = 0; index < segments.Count; index++)
        {
            var previous = segments[(index - 1 + segments.Count) % segments.Count];
            var current = segments[index];
            result[index] = new PathBezierNode(current.Start, previous.Control2, current.Control1);
        }

        return result;
    }

    private static PathBezierNode[][] CloneBezierContours(PathBezierNode[][] contours)
    {
        var clone = new PathBezierNode[contours.Length][];
        for (var index = 0; index < contours.Length; index++) clone[index] = contours[index].ToArray();
        return clone;
    }

    private static void TransformPathBezierContours(
        IReadOnlyList<PathBezierNode[]> contours,
        Func<PointF, PointF> transform)
    {
        for (var contourIndex = 0; contourIndex < contours.Count; contourIndex++)
        {
            var contour = contours[contourIndex];
            for (var nodeIndex = 0; nodeIndex < contour.Length; nodeIndex++)
            {
                var node = contour[nodeIndex];
                contour[nodeIndex] = new PathBezierNode(
                    transform(node.Anchor),
                    transform(node.IncomingControl),
                    transform(node.OutgoingControl));
            }
        }
    }

    private static void TransformFreehandBezierNodes(
        PathBezierNode[] nodes,
        Func<PointF, PointF> transform)
    {
        for (var nodeIndex = 0; nodeIndex < nodes.Length; nodeIndex++)
        {
            var node = nodes[nodeIndex];
            nodes[nodeIndex] = new PathBezierNode(
                transform(node.Anchor),
                transform(node.IncomingControl),
                transform(node.OutgoingControl));
        }
    }

    private static bool Finite(PointF point) => float.IsFinite(point.X) && float.IsFinite(point.Y);

    private static PointF[][] NormalizePathContours(IReadOnlyList<PointF[]> contours)
    {
        var result = new List<PointF[]>(contours.Count);
        foreach (var contour in contours)
        {
            if (contour.Length < 3) continue;
            var cleaned = RemoveDuplicatePolygonPoints(contour.Select(VectorUnits.Quantize).ToList());
            if (cleaned.Count < 3 || Math.Abs(PolygonArea(cleaned)) < 0.5f) continue;
            result.Add(cleaned.ToArray());
        }

        return result
            .OrderByDescending(contour => Math.Abs(PolygonArea(contour)))
            .ToArray();
    }

    private static PointF[] NormalizeFreehandPoints(IReadOnlyList<PointF> points)
    {
        var result = new List<PointF>(points.Count);
        foreach (var source in points)
        {
            var point = VectorUnits.Quantize(source);
            if (result.Count > 0 && result[^1] == point) continue;
            result.Add(point);
        }

        return result.ToArray();
    }

    private static MixingBrushTrajectorySample[] NormalizeMixingStrokeSamples(
        IReadOnlyList<MixingBrushTrajectorySample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var result = new List<MixingBrushTrajectorySample>(samples.Count);
        foreach (var source in samples)
        {
            if (!float.IsFinite(source.Point.X)
                || !float.IsFinite(source.Point.Y)
                || !float.IsFinite(source.Diameter))
            {
                continue;
            }

            var sample = source with
            {
                Point = VectorUnits.Quantize(source.Point),
                Diameter = Math.Max(
                    VectorUnits.MinimumStrokeUnits,
                    VectorUnits.Quantize(source.Diameter))
            };
            if (result.Count > 0 && result[^1].Point == sample.Point)
            {
                result[^1] = sample;
                continue;
            }
            result.Add(sample);
        }
        return result.ToArray();
    }

    private static PointF[][] CloneContours(PointF[][] contours)
    {
        var clone = new PointF[contours.Length][];
        for (var i = 0; i < contours.Length; i++) clone[i] = contours[i].ToArray();
        return clone;
    }

    private static float SegmentProjectionT(PointF point, PointF start, PointF end)
    {
        var vx = end.X - start.X;
        var vy = end.Y - start.Y;
        var lengthSq = vx * vx + vy * vy;
        if (lengthSq <= 0.0001f) return 0;
        return Math.Clamp(((point.X - start.X) * vx + (point.Y - start.Y) * vy) / lengthSq, 0, 1);
    }

    private bool ObjectIntersectsBounds(int i, RectangleF bounds)
    {
        var objectBounds = GetObjectWorldBounds(i);
        return objectBounds.Left <= bounds.Right
            && objectBounds.Right >= bounds.Left
            && objectBounds.Top <= bounds.Bottom
            && objectBounds.Bottom >= bounds.Top;
    }

    internal RectangleF GetObjectWorldBounds(int i)
    {
        var bounds = GetObjectWorldBoundsCore(i);
        if ((uint)i >= ObjectCount
            || bounds.IsEmpty
            || !_objectDistortions.TryGetValue(i, out var distortions)
            || distortions.Length == 0)
        {
            return bounds;
        }

        foreach (var distortion in distortions)
        {
            bounds = MapBoundsThroughDistortion(bounds, distortion);
        }
        return bounds;
    }

    private static RectangleF MapBoundsThroughDistortion(RectangleF sourceBounds, DistortWarp distortion)
    {
        if (!distortion.IsValid || sourceBounds.IsEmpty) return sourceBounds;
        const int divisions = 8;
        var samples = new PointF[divisions + 1, divisions + 1];
        var hasPoint = false;
        var mappedBounds = RectangleF.Empty;
        for (var y = 0; y <= divisions; y++)
        {
            var sourceY = sourceBounds.Top + sourceBounds.Height * y / divisions;
            for (var x = 0; x <= divisions; x++)
            {
                var sourceX = sourceBounds.Left + sourceBounds.Width * x / divisions;
                var mapped = distortion.Map(new PointF(sourceX, sourceY));
                if (!float.IsFinite(mapped.X) || !float.IsFinite(mapped.Y)) continue;
                samples[x, y] = mapped;
                var pointBounds = new RectangleF(mapped.X, mapped.Y, 0, 0);
                mappedBounds = hasPoint ? RectangleF.Union(mappedBounds, pointBounds) : pointBounds;
                hasPoint = true;
            }
        }
        if (!hasPoint) return sourceBounds;

        var maximumDeviation = 0f;
        for (var y = 0; y < divisions; y++)
        {
            for (var x = 0; x < divisions; x++)
            {
                var center = distortion.Map(new PointF(
                    sourceBounds.Left + sourceBounds.Width * (x + 0.5f) / divisions,
                    sourceBounds.Top + sourceBounds.Height * (y + 0.5f) / divisions));
                var bilinearCenter = new PointF(
                    (samples[x, y].X + samples[x + 1, y].X + samples[x, y + 1].X + samples[x + 1, y + 1].X) * 0.25f,
                    (samples[x, y].Y + samples[x + 1, y].Y + samples[x, y + 1].Y + samples[x + 1, y + 1].Y) * 0.25f);
                maximumDeviation = Math.Max(maximumDeviation, Distance(center, bilinearCenter));
                mappedBounds = RectangleF.Union(mappedBounds, new RectangleF(center.X, center.Y, 0, 0));
            }
        }

        var sourceFrameBounds = distortion.Source.Bounds;
        if (sourceBounds.Left <= sourceFrameBounds.Left
            && sourceBounds.Top <= sourceFrameBounds.Top
            && sourceBounds.Right >= sourceFrameBounds.Right
            && sourceBounds.Bottom >= sourceFrameBounds.Bottom)
        {
            mappedBounds = RectangleF.Union(mappedBounds, distortion.Bounds);
        }
        var safetyMargin = Math.Max(0.5f, maximumDeviation * 2f);
        mappedBounds.Inflate(safetyMargin, safetyMargin);
        return mappedBounds;
    }

    private RectangleF GetObjectWorldBoundsCore(int i)
    {
        var shape = ShapeKind.Length > i ? ShapeKind[i] : VectorAnimationEngine.ShapeKind.Rectangle;
        var margin = Math.Max(Stroke[i] * 0.5f, 1);
        if (shape == VectorAnimationEngine.ShapeKind.Path
            && _pathBezierLocalContours.TryGetValue(i, out var bezierContours)
            && bezierContours.Length > 0)
        {
            var hasCurve = false;
            var bounds = RectangleF.Empty;
            foreach (var contour in bezierContours)
            {
                for (var segmentIndex = 0; segmentIndex < contour.Length; segmentIndex++)
                {
                    var current = contour[segmentIndex];
                    var next = contour[(segmentIndex + 1) % contour.Length];
                    var curveBounds = CubicCurveBounds(
                        LocalToWorld(i, current.Anchor.X, current.Anchor.Y),
                        LocalToWorld(i, current.OutgoingControl.X, current.OutgoingControl.Y),
                        LocalToWorld(i, next.IncomingControl.X, next.IncomingControl.Y),
                        LocalToWorld(i, next.Anchor.X, next.Anchor.Y));
                    bounds = hasCurve ? RectangleF.Union(bounds, curveBounds) : curveBounds;
                    hasCurve = true;
                }
            }

            if (hasCurve)
            {
                bounds.Inflate(margin, margin);
                return bounds;
            }
        }

        if (shape == VectorAnimationEngine.ShapeKind.Path
            && _pathLocalContours.TryGetValue(i, out var pathContours)
            && pathContours.Length > 0)
        {
            var hasPoint = false;
            var left = 0f;
            var right = 0f;
            var top = 0f;
            var bottom = 0f;
            foreach (var contour in pathContours)
            {
                foreach (var local in contour)
                {
                    var point = LocalToWorld(i, local.X, local.Y);
                    if (!hasPoint)
                    {
                        left = right = point.X;
                        top = bottom = point.Y;
                        hasPoint = true;
                        continue;
                    }

                    left = Math.Min(left, point.X);
                    right = Math.Max(right, point.X);
                    top = Math.Min(top, point.Y);
                    bottom = Math.Max(bottom, point.Y);
                }
            }

            if (hasPoint) return RectangleF.FromLTRB(left - margin, top - margin, right + margin, bottom + margin);
        }

        if (shape == VectorAnimationEngine.ShapeKind.Line)
        {
            var halfW = Width[i] * 0.5f;
            var start = LocalToWorld(i, -halfW, 0);
            var end = LocalToWorld(i, halfW, 0);
            var control1 = new PointF(CurveControlX[i], CurveControlY[i]);
            var control2 = new PointF(CurveControl2X[i], CurveControl2Y[i]);
            var bounds = CubicCurveBounds(start, control1, control2, end);
            bounds.Inflate(margin, margin);
            return bounds;
        }

        if (shape == VectorAnimationEngine.ShapeKind.Freeform
            && _freehandBezierLocalNodes.TryGetValue(i, out var freehandBezierNodes)
            && freehandBezierNodes.Length >= 2)
        {
            var bounds = RectangleF.Empty;
            var hasCurve = false;
            for (var segmentIndex = 0; segmentIndex < freehandBezierNodes.Length - 1; segmentIndex++)
            {
                var current = freehandBezierNodes[segmentIndex];
                var next = freehandBezierNodes[segmentIndex + 1];
                var curveBounds = CubicCurveBounds(
                    LocalToWorld(i, current.Anchor.X, current.Anchor.Y),
                    LocalToWorld(i, current.OutgoingControl.X, current.OutgoingControl.Y),
                    LocalToWorld(i, next.IncomingControl.X, next.IncomingControl.Y),
                    LocalToWorld(i, next.Anchor.X, next.Anchor.Y));
                bounds = hasCurve ? RectangleF.Union(bounds, curveBounds) : curveBounds;
                hasCurve = true;
            }

            if (hasCurve)
            {
                bounds.Inflate(margin, margin);
                return bounds;
            }
        }

        if (IsFreehandShape(shape)
            && _freehandLocalPoints.TryGetValue(i, out var freehandPoints)
            && freehandPoints.Length > 0)
        {
            var first = LocalToWorld(i, freehandPoints[0].X, freehandPoints[0].Y);
            var left = first.X;
            var right = first.X;
            var top = first.Y;
            var bottom = first.Y;
            for (var p = 1; p < freehandPoints.Length; p++)
            {
                var point = LocalToWorld(i, freehandPoints[p].X, freehandPoints[p].Y);
                left = Math.Min(left, point.X);
                right = Math.Max(right, point.X);
                top = Math.Min(top, point.Y);
                bottom = Math.Max(bottom, point.Y);
            }

            return RectangleF.FromLTRB(left - margin, top - margin, right + margin, bottom + margin);
        }

        var halfWShape = Width[i] * 0.5f;
        var halfHShape = Height[i] * 0.5f;
        if (Math.Abs(Angle[i]) < 0.0001f)
        {
            return RectangleF.FromLTRB(X[i] - halfWShape - margin, Y[i] - halfHShape - margin, X[i] + halfWShape + margin, Y[i] + halfHShape + margin);
        }

        var p0 = LocalToWorld(i, -halfWShape, -halfHShape);
        var p1 = LocalToWorld(i, halfWShape, -halfHShape);
        var p2 = LocalToWorld(i, halfWShape, halfHShape);
        var p3 = LocalToWorld(i, -halfWShape, halfHShape);
        var leftRotated = Math.Min(Math.Min(p0.X, p1.X), Math.Min(p2.X, p3.X)) - margin;
        var rightRotated = Math.Max(Math.Max(p0.X, p1.X), Math.Max(p2.X, p3.X)) + margin;
        var topRotated = Math.Min(Math.Min(p0.Y, p1.Y), Math.Min(p2.Y, p3.Y)) - margin;
        var bottomRotated = Math.Max(Math.Max(p0.Y, p1.Y), Math.Max(p2.Y, p3.Y)) + margin;
        return RectangleF.FromLTRB(leftRotated, topRotated, rightRotated, bottomRotated);
    }

    private static RectangleF Normalize(RectangleF rect)
    {
        return RectangleF.FromLTRB(
            Math.Min(rect.Left, rect.Right),
            Math.Min(rect.Top, rect.Bottom),
            Math.Max(rect.Left, rect.Right),
            Math.Max(rect.Top, rect.Bottom));
    }

    private PointF WorldToLocal(int i, PointF world)
    {
        var dx = world.X - X[i];
        var dy = world.Y - Y[i];
        var angle = Angle[i];
        var cos = MathF.Cos(angle);
        var sin = MathF.Sin(angle);
        return new PointF(dx * cos + dy * sin, -dx * sin + dy * cos);
    }

    private PointF LocalToWorld(int i, float localX, float localY)
    {
        var angle = Angle[i];
        var cos = MathF.Cos(angle);
        var sin = MathF.Sin(angle);
        return new PointF(X[i] + localX * cos - localY * sin, Y[i] + localX * sin + localY * cos);
    }

    private static float DistanceToCubic(PointF point, PointF start, PointF control1, PointF control2, PointF end)
    {
        var best = float.MaxValue;
        var previous = start;
        const int segments = 24;
        for (var s = 1; s <= segments; s++)
        {
            var t = s / (float)segments;
            var current = CubicPoint(start, control1, control2, end, t);
            best = Math.Min(best, DistanceToSegment(point, previous, current));
            previous = current;
        }

        return best;
    }

    private static bool CubicCurveInsideRectangle(PointF start, PointF control1, PointF control2, PointF end, RectangleF bounds)
    {
        var curveBounds = CubicCurveBounds(start, control1, control2, end);
        return curveBounds.Left >= bounds.Left - DrawingTopologyRules.UnitIntersectionTolerance
            && curveBounds.Right <= bounds.Right + DrawingTopologyRules.UnitIntersectionTolerance
            && curveBounds.Top >= bounds.Top - DrawingTopologyRules.UnitIntersectionTolerance
            && curveBounds.Bottom <= bounds.Bottom + DrawingTopologyRules.UnitIntersectionTolerance;
    }

    private static RectangleF CubicCurveBounds(PointF start, PointF control1, PointF control2, PointF end)
    {
        var left = Math.Min(start.X, end.X);
        var right = Math.Max(start.X, end.X);
        var top = Math.Min(start.Y, end.Y);
        var bottom = Math.Max(start.Y, end.Y);
        IncludeCubicExtrema(start.X, control1.X, control2.X, end.X, ref left, ref right);
        IncludeCubicExtrema(start.Y, control1.Y, control2.Y, end.Y, ref top, ref bottom);
        return RectangleF.FromLTRB(left, top, right, bottom);
    }

    private static void IncludeCubicExtrema(
        float start,
        float control1,
        float control2,
        float end,
        ref float minimum,
        ref float maximum)
    {
        var a = -start + 3 * control1 - 3 * control2 + end;
        var b = 2 * (start - 2 * control1 + control2);
        var c = control1 - start;
        if (Math.Abs(a) <= 0.000001f)
        {
            if (Math.Abs(b) <= 0.000001f) return;
            IncludeCubicExtremumAt(start, control1, control2, end, -c / b, ref minimum, ref maximum);
            return;
        }

        var discriminant = b * b - 4 * a * c;
        if (discriminant < 0) return;
        var root = MathF.Sqrt(discriminant);
        IncludeCubicExtremumAt(start, control1, control2, end, (-b + root) / (2 * a), ref minimum, ref maximum);
        IncludeCubicExtremumAt(start, control1, control2, end, (-b - root) / (2 * a), ref minimum, ref maximum);
    }

    private static void IncludeCubicExtremumAt(
        float start,
        float control1,
        float control2,
        float end,
        float t,
        ref float minimum,
        ref float maximum)
    {
        if (t <= 0 || t >= 1) return;
        var value = CubicCoordinate(start, control1, control2, end, t);
        minimum = Math.Min(minimum, value);
        maximum = Math.Max(maximum, value);
    }

    private static float CubicCoordinate(float start, float control1, float control2, float end, float t)
    {
        var inv = 1 - t;
        return inv * inv * inv * start
            + 3 * inv * inv * t * control1
            + 3 * inv * t * t * control2
            + t * t * t * end;
    }

    private static PointF CubicPoint(PointF start, PointF control1, PointF control2, PointF end, float t)
    {
        return new PointF(
            CubicCoordinate(start.X, control1.X, control2.X, end.X, t),
            CubicCoordinate(start.Y, control1.Y, control2.Y, end.Y, t));
    }

    internal static CubicBoundarySegment CubicSubcurve(
        PointF start,
        PointF control1,
        PointF control2,
        PointF end,
        float startT,
        float endT)
    {
        startT = Math.Clamp(startT, 0, 1);
        endT = Math.Clamp(endT, startT, 1);
        var p0 = CubicPoint(start, control1, control2, end, startT);
        var p3 = CubicPoint(start, control1, control2, end, endT);
        if (endT - startT <= 0.0001f) return new CubicBoundarySegment(p0, p0, p3, p3);

        var derivativeStart = CubicDerivative(start, control1, control2, end, startT);
        var derivativeEnd = CubicDerivative(start, control1, control2, end, endT);
        var duration = endT - startT;
        var c1 = new PointF(p0.X + derivativeStart.X * duration / 3f, p0.Y + derivativeStart.Y * duration / 3f);
        var c2 = new PointF(p3.X - derivativeEnd.X * duration / 3f, p3.Y - derivativeEnd.Y * duration / 3f);
        return new CubicBoundarySegment(p0, c1, c2, p3);
    }

    private static PointF CubicDerivative(PointF start, PointF control1, PointF control2, PointF end, float t)
    {
        var inv = 1 - t;
        return new PointF(
            3 * (inv * inv * (control1.X - start.X)
                + 2 * inv * t * (control2.X - control1.X)
                + t * t * (end.X - control2.X)),
            3 * (inv * inv * (control1.Y - start.Y)
                + 2 * inv * t * (control2.Y - control1.Y)
                + t * t * (end.Y - control2.Y)));
    }

    private static float DistanceToSegment(PointF point, PointF start, PointF end)
    {
        var vx = end.X - start.X;
        var vy = end.Y - start.Y;
        var lengthSq = vx * vx + vy * vy;
        if (lengthSq <= 0.0001f) return Distance(point, start);

        var t = ((point.X - start.X) * vx + (point.Y - start.Y) * vy) / lengthSq;
        t = Math.Clamp(t, 0, 1);
        return Distance(point, new PointF(start.X + vx * t, start.Y + vy * t));
    }

    private static float Distance(PointF a, PointF b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private static bool IsFreehandShape(ShapeKind shape)
    {
        return shape is VectorAnimationEngine.ShapeKind.Freeform or VectorAnimationEngine.ShapeKind.BrushStroke;
    }

    private static bool IsWholeObjectShape(ShapeKind shape)
    {
        return shape is VectorAnimationEngine.ShapeKind.ImportedSvg
            or VectorAnimationEngine.ShapeKind.Text
            or VectorAnimationEngine.ShapeKind.MixingStroke;
    }

    private static bool IsTopologyStrokeShape(ShapeKind shape)
    {
        return shape == VectorAnimationEngine.ShapeKind.Line || IsFreehandShape(shape);
    }

    private static bool IsFillBoundaryLinkedStrokeShape(ShapeKind shape)
    {
        return shape is VectorAnimationEngine.ShapeKind.Line or VectorAnimationEngine.ShapeKind.Freeform;
    }

    internal bool HasFill(int objectIndex)
    {
        return (uint)objectIndex < ObjectCount
            && IsFillShape(ShapeKind[objectIndex])
            && Color.FromArgb(Argb[objectIndex]).A > 0;
    }

    private bool HasStroke(int objectIndex)
    {
        return (uint)objectIndex < ObjectCount
            && !IsWholeObjectShape(ShapeKind[objectIndex])
            && Stroke[objectIndex] > 0
            && Color.FromArgb(StrokeArgb[objectIndex]).A > 0;
    }

    private static bool IsFillShape(ShapeKind shape)
    {
        return shape is not VectorAnimationEngine.ShapeKind.Line
            and not VectorAnimationEngine.ShapeKind.Freeform
            and not VectorAnimationEngine.ShapeKind.BrushStroke
            and not VectorAnimationEngine.ShapeKind.ImportedSvg
            and not VectorAnimationEngine.ShapeKind.Text
            and not VectorAnimationEngine.ShapeKind.MixingStroke;
    }

    private static bool SupportsGradient(ShapeKind shape) => IsFillShape(shape) || shape == VectorAnimationEngine.ShapeKind.Line;

}
