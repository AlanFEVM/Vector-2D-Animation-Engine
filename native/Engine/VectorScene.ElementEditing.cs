using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Clipper2Lib;

namespace VectorAnimationEngine;

internal sealed partial class VectorScene
{
    public int HitTest(PointF world, int frame, float toleranceWorld = 6)
    {
        var hit = HitTestElement(world, frame, toleranceWorld);
        return hit.IsValid ? hit.Key.ObjectIndex : -1;
    }

    public bool HasSelectableObjectAt(PointF world, int frame, float toleranceWorld = 6)
    {
        GetHitTestRange(world, toleranceWorld, out var minX, out var maxX, out var minY, out var maxY);
        var candidates = CollectHitCandidates(minX, maxX, minY, maxY, frame, sort: false);
        foreach (var objectIndex in candidates)
        {
            if (!IsObjectSelectable(objectIndex, frame)) continue;
            if (!ObjectMayContainHitPoint(objectIndex, world, toleranceWorld)) continue;
            if (!TryInverseMapObjectHitPoint(objectIndex, world, toleranceWorld, out var sourceWorld)) continue;

            var shape = ShapeKind[objectIndex];
            if (IsWholeObjectShape(shape)
                && HitObject(sourceWorld, objectIndex, toleranceWorld))
            {
                return true;
            }
            var hitRadius = Math.Max(Stroke[objectIndex] * 0.5f, 1) + toleranceWorld;
            if (IsTopologyStrokeShape(shape))
            {
                if (HasStroke(objectIndex) && CurveSamplesHit(sourceWorld, StrokeSamples(objectIndex), hitRadius))
                {
                    return true;
                }
                continue;
            }

            if (HasStroke(objectIndex)
                && ShapeBoundaryContours(objectIndex).Any(contour => DistanceToPolyline(sourceWorld, contour) <= hitRadius))
            {
                return true;
            }
            if (HasFill(objectIndex) && FillContainsRawPoint(objectIndex, sourceWorld)) return true;
        }

        return false;
    }

    private static bool CurveSamplesHit(PointF world, IReadOnlyList<CurveSample> samples, float hitRadius)
    {
        if (samples.Count == 0) return false;
        if (samples.Count == 1) return Distance(world, samples[0].Point) <= hitRadius;
        for (var index = 0; index < samples.Count - 1; index++)
        {
            if (DistanceToSegment(world, samples[index].Point, samples[index + 1].Point) <= hitRadius) return true;
        }
        return false;
    }

    public DrawingElementHit HitTestElement(PointF world, int frame, float toleranceWorld = 6)
    {
        return HitTestElementCore(world, frame, toleranceWorld, objectFilter: null);
    }

    internal DrawingElementHit HitTestElement(
        PointF world,
        int frame,
        float toleranceWorld,
        Func<int, bool> objectFilter)
    {
        ArgumentNullException.ThrowIfNull(objectFilter);
        return HitTestElementCore(world, frame, toleranceWorld, objectFilter);
    }

    private DrawingElementHit HitTestElementCore(
        PointF world,
        int frame,
        float toleranceWorld,
        Func<int, bool>? objectFilter)
    {
        GetHitTestRange(world, toleranceWorld, out var minX, out var maxX, out var minY, out var maxY);
        var candidates = CollectHitCandidates(minX, maxX, minY, maxY, frame, sort: false);
        IReadOnlyList<int> eligibleCandidates = objectFilter is null
            ? candidates
            : candidates.Where(objectFilter).ToArray();
        var best = DrawingElementHit.None;

        foreach (var i in eligibleCandidates)
        {
            if (!ObjectMayContainHitPoint(i, world, toleranceWorld)) continue;
            var hit = HitElement(world, i, eligibleCandidates, frame, toleranceWorld);
            if (!hit.IsValid) continue;
            if (IsBetterHit(hit, best)) best = hit;
        }

        return best;
    }

    private bool IsBetterHit(DrawingElementHit hit, DrawingElementHit best)
    {
        if (!best.IsValid) return true;

        var hitLayer = ObjectLayer[hit.Key.ObjectIndex];
        var bestLayer = ObjectLayer[best.Key.ObjectIndex];
        if (hitLayer != bestLayer) return hitLayer < bestLayer;

        var hitIsFill = hit.Key.Kind == DrawingElementKind.Fill;
        var bestIsFill = best.Key.Kind == DrawingElementKind.Fill;
        if (hitIsFill != bestIsFill) return !hitIsFill;
        if (hitIsFill) return CompareObjectStack(hit.Key.ObjectIndex, best.Key.ObjectIndex) > 0;

        if (hit.Distance < best.Distance - 0.001f) return true;
        if (hit.Distance > best.Distance + 0.001f) return false;
        return CompareObjectStack(hit.Key.ObjectIndex, best.Key.ObjectIndex) > 0;
    }

    private int CompareObjectStack(int a, int b)
    {
        var comparison = ObjectOrder[a].CompareTo(ObjectOrder[b]);
        if (comparison != 0) return comparison;
        comparison = ObjectSubOrder[a].CompareTo(ObjectSubOrder[b]);
        return comparison != 0 ? comparison : a.CompareTo(b);
    }

    public PointF[] GetShapeBoundary(int objectIndex)
    {
        return (uint)objectIndex < ObjectCount ? ShapeBoundary(objectIndex) : Array.Empty<PointF>();
    }

    internal PointF[] GetDistortedShapeBoundary(int objectIndex)
    {
        var contours = GetDistortedObjectBoundaryContours(objectIndex);
        return contours.Length > 0 ? contours[0] : Array.Empty<PointF>();
    }

    public PointF[][] GetFillPartContours(DrawingElementHit hit, int frame)
    {
        if (!hit.IsValid || hit.Key.Kind != DrawingElementKind.Fill || (uint)hit.Key.ObjectIndex >= ObjectCount)
        {
            return Array.Empty<PointF[]>();
        }

        foreach (var part in GetFillParts(hit.Key.ObjectIndex, frame))
        {
            if (part.PartIndex == hit.Key.PartIndex) return CloneContours(part.Contours);
        }

        return Array.Empty<PointF[]>();
    }

    public DrawingFillPartGeometry[] GetFillParts(int objectIndex, int frame)
    {
        if ((uint)objectIndex >= ObjectCount) return Array.Empty<DrawingFillPartGeometry>();
        if (ShapeKind[objectIndex] == VectorAnimationEngine.ShapeKind.MixingStroke
            && TryGetMixingBrushLocalRegion(objectIndex, out var mixingRegion))
        {
            var mixingParts = new DrawingFillPartGeometry[mixingRegion.ConnectedComponentCount];
            for (var partIndex = 0; partIndex < mixingParts.Length; partIndex++)
            {
                if (!mixingRegion.TryGetConnectedComponent(partIndex, out var component))
                {
                    return Array.Empty<DrawingFillPartGeometry>();
                }
                var localContours = component.GetBoundaryContours();
                var worldContours = new PointF[localContours.Length][];
                for (var contourIndex = 0; contourIndex < localContours.Length; contourIndex++)
                {
                    worldContours[contourIndex] = localContours[contourIndex]
                        .Select(point => LocalToWorld(objectIndex, point.X, point.Y))
                        .ToArray();
                }
                mixingParts[partIndex] = new DrawingFillPartGeometry(partIndex, worldContours);
            }
            return mixingParts;
        }
        if (!HasFill(objectIndex)) return Array.Empty<DrawingFillPartGeometry>();
        var candidates = CollectTopologyCandidates(objectIndex, frame);
        var regions = BuildFillRegions(objectIndex, frame, candidates);
        var result = new DrawingFillPartGeometry[regions.Count];
        for (var part = 0; part < regions.Count; part++)
        {
            result[part] = new DrawingFillPartGeometry(part, CloneContours(regions[part].Contours));
        }

        return result;
    }

    public PointF[] GetBoundaryPartPoints(DrawingElementHit hit, int frame)
    {
        if (!hit.IsValid || hit.Key.Kind != DrawingElementKind.BoundaryStroke || (uint)hit.Key.ObjectIndex >= ObjectCount)
        {
            return Array.Empty<PointF>();
        }

        foreach (var part in GetBoundaryParts(hit.Key.ObjectIndex, frame))
        {
            if (part.PartIndex == hit.Key.PartIndex) return part.Points.ToArray();
        }

        return Array.Empty<PointF>();
    }

    internal bool TryGetBoundaryBezierSegment(
        DrawingElementHit hit,
        int frame,
        out CubicBoundarySegment segment)
    {
        segment = default;
        if (!hit.IsValid
            || hit.Key.Kind != DrawingElementKind.BoundaryStroke
            || (uint)hit.Key.ObjectIndex >= ObjectCount
            || !IsObjectActive(hit.Key.ObjectIndex, frame)
            || !HasStroke(hit.Key.ObjectIndex))
        {
            return false;
        }

        var candidates = CollectTopologyCandidates(hit.Key.ObjectIndex, frame);
        foreach (var part in HitBoundaryStrokeParts(hit.Key.ObjectIndex, frame, candidates))
        {
            if (part.PartIndex != hit.Key.PartIndex) continue;
            if (part.Curve is { } curve)
            {
                segment = curve;
                return true;
            }

            if (part.Points.Length != 2) return false;
            var start = part.Points[0];
            var end = part.Points[1];
            segment = new CubicBoundarySegment(
                start,
                Lerp(start, end, 1f / 3f),
                Lerp(start, end, 2f / 3f),
                end);
            return true;
        }

        return false;
    }

    public DrawingPolylinePartGeometry[] GetBoundaryParts(int objectIndex, int frame)
    {
        if ((uint)objectIndex >= ObjectCount || !HasStroke(objectIndex)) return Array.Empty<DrawingPolylinePartGeometry>();
        var candidates = CollectTopologyCandidates(objectIndex, frame);
        return BuildBoundaryStrokeParts(objectIndex, candidates)
            .Select(part => new DrawingPolylinePartGeometry(part.PartIndex, part.Points.ToArray()))
            .ToArray();
    }

    public bool TryGetLinePartEndpointStyles(
        DrawingElementKey key,
        int frame,
        out LineEndpointStyle startStyle,
        out LineEndpointStyle endStyle)
    {
        startStyle = LineEndpointStyle.Round;
        endStyle = LineEndpointStyle.Round;
        if (!key.IsValid
            || (uint)key.ObjectIndex >= ObjectCount
            || !IsObjectActive(key.ObjectIndex, frame)
            || !HasStroke(key.ObjectIndex))
        {
            return false;
        }

        var candidates = CollectTopologyCandidates(key.ObjectIndex, frame);
        if (key.Kind == DrawingElementKind.Stroke
            && ShapeKind[key.ObjectIndex] == VectorAnimationEngine.ShapeKind.Line)
        {
            var splits = StrokeSplits(key.ObjectIndex, candidates);
            if (key.PartIndex < 0 || key.PartIndex >= splits.Count - 1) return false;

            if (key.PartIndex == 0)
            {
                startStyle = GetLineEndpointStyle(key.ObjectIndex, startEndpoint: true);
            }
            if (key.PartIndex == splits.Count - 2)
            {
                endStyle = GetLineEndpointStyle(key.ObjectIndex, startEndpoint: false);
            }
            return true;
        }

        if (key.Kind != DrawingElementKind.BoundaryStroke) return false;
        var boundary = BuildBoundaryStrokeParts(key.ObjectIndex, candidates)
            .FirstOrDefault(part => part.PartIndex == key.PartIndex);
        return boundary.Curve is not null || boundary.Points is { Length: 2 };
    }

    public PointF[] GetStrokePartPoints(DrawingElementHit hit, int frame)
    {
        if (!hit.IsValid || hit.Key.Kind != DrawingElementKind.Stroke || (uint)hit.Key.ObjectIndex >= ObjectCount)
        {
            return Array.Empty<PointF>();
        }

        if (ShapeKind[hit.Key.ObjectIndex] == VectorAnimationEngine.ShapeKind.Line
            && TryGetLineBezierPart(
                hit.Key.ObjectIndex,
                hit.StartT,
                hit.EndT,
                out var start,
                out _,
                out _,
                out var end))
        {
            // Endpoint editing needs the topology-part endpoints, not the owning line's outer endpoints.
            return new[] { start, end };
        }

        foreach (var part in GetStrokeParts(hit.Key.ObjectIndex, frame))
        {
            if (part.PartIndex == hit.Key.PartIndex) return part.Points.ToArray();
        }

        return Array.Empty<PointF>();
    }

    public DrawingPolylinePartGeometry[] GetStrokeParts(int objectIndex, int frame)
    {
        if ((uint)objectIndex >= ObjectCount
            || !IsFreehandShape(ShapeKind[objectIndex])
            || !TryGetFreehandWorldPoints(objectIndex, out var points))
        {
            return Array.Empty<DrawingPolylinePartGeometry>();
        }

        var candidates = CollectTopologyCandidates(objectIndex, frame);
        var splits = StrokeSplitParameters(objectIndex, candidates);
        return BuildPolylinePathParts(points, splits)
            .Select(part => new DrawingPolylinePartGeometry(part.PartIndex, part.Points.ToArray()))
            .ToArray();
    }

    public DrawingElementHit[] GetConnectedStrokeElements(DrawingElementHit seed, int frame)
    {
        if (!seed.IsValid
            || seed.Key.Kind is not (DrawingElementKind.Stroke or DrawingElementKind.BoundaryStroke)
            || (uint)seed.Key.ObjectIndex >= ObjectCount
            || !IsObjectActive(seed.Key.ObjectIndex, frame))
        {
            return Array.Empty<DrawingElementHit>();
        }

        var layer = ObjectLayer[seed.Key.ObjectIndex];
        var parts = new List<ConnectedStrokePart>();
        var partIndices = new Dictionary<DrawingElementKey, int>();
        var endpointIndex = new Dictionary<(int X, int Y), List<int>>();
        var indexedObjects = new HashSet<int>();

        void AddEndpoint(PointF point, int partIndex)
        {
            var key = ConnectedEndpointKey(point);
            if (!endpointIndex.TryGetValue(key, out var indexed))
            {
                indexed = new List<int>(4);
                endpointIndex[key] = indexed;
            }

            indexed.Add(partIndex);
        }

        void IndexObject(int objectIndex)
        {
            if (!indexedObjects.Add(objectIndex)
                || (uint)objectIndex >= ObjectCount
                || ObjectLayer[objectIndex] != layer
                || !IsObjectActive(objectIndex, frame))
            {
                return;
            }

            foreach (var part in BuildConnectedStrokeParts(objectIndex, frame))
            {
                if (partIndices.ContainsKey(part.Hit.Key)) continue;
                var partIndex = parts.Count;
                parts.Add(part);
                partIndices[part.Hit.Key] = partIndex;
                AddEndpoint(part.Start, partIndex);
                AddEndpoint(part.End, partIndex);
            }
        }

        IndexObject(seed.Key.ObjectIndex);
        if (!partIndices.TryGetValue(seed.Key, out var seedPartIndex)) return Array.Empty<DrawingElementHit>();

        var connected = new HashSet<int>();
        var connectedOrder = new List<int>();
        var endpoints = new Queue<PointF>();
        var visitedEndpoints = new HashSet<(int X, int Y)>();

        void SelectPart(int partIndex)
        {
            if (!connected.Add(partIndex)) return;
            connectedOrder.Add(partIndex);
            endpoints.Enqueue(parts[partIndex].Start);
            endpoints.Enqueue(parts[partIndex].End);
        }

        SelectPart(seedPartIndex);
        while (endpoints.Count > 0)
        {
            var endpoint = endpoints.Dequeue();
            if (!visitedEndpoints.Add(ConnectedEndpointKey(endpoint))) continue;

            var tolerance = ConnectedStrokeEndpointToleranceUnits;
            var queryBounds = RectangleF.FromLTRB(
                endpoint.X - tolerance,
                endpoint.Y - tolerance,
                endpoint.X + tolerance,
                endpoint.Y + tolerance);
            foreach (var objectIndex in QueryObjects(queryBounds, frame)) IndexObject(objectIndex);

            var endpointKey = ConnectedEndpointKey(endpoint);
            for (var y = endpointKey.Y - 2; y <= endpointKey.Y + 2; y++)
            {
                for (var x = endpointKey.X - 2; x <= endpointKey.X + 2; x++)
                {
                    if (!endpointIndex.TryGetValue((x, y), out var candidates)) continue;
                    foreach (var candidateIndex in candidates)
                    {
                        var candidate = parts[candidateIndex];
                        if (Distance(endpoint, candidate.Start) <= tolerance
                            || Distance(endpoint, candidate.End) <= tolerance)
                        {
                            SelectPart(candidateIndex);
                        }
                    }
                }
            }
        }

        return connectedOrder.Select(index => parts[index].Hit).ToArray();
    }

    private ConnectedStrokePart[] BuildConnectedStrokeParts(int objectIndex, int frame)
    {
        if ((uint)objectIndex >= ObjectCount || !HasStroke(objectIndex)) return Array.Empty<ConnectedStrokePart>();
        var shape = ShapeKind[objectIndex];
        var candidates = CollectTopologyCandidates(objectIndex, frame);
        var result = new List<ConnectedStrokePart>();

        if (IsTopologyStrokeShape(shape))
        {
            var splits = StrokeSplitParameters(objectIndex, candidates);
            if (shape == VectorAnimationEngine.ShapeKind.Line)
            {
                var curve = LineCurve(objectIndex);
                foreach (var part in BuildCurveParts(curve.Start, curve.Control1, curve.Control2, curve.End, splits))
                {
                    result.Add(new ConnectedStrokePart(
                        new DrawingElementHit(
                            new DrawingElementKey(objectIndex, DrawingElementKind.Stroke, part.PartIndex),
                            0,
                            splits[part.PartIndex],
                            splits[part.PartIndex + 1]),
                        part.Start,
                        part.End));
                }
            }
            else if (TryGetFreehandWorldPoints(objectIndex, out var points))
            {
                foreach (var part in BuildPolylinePathParts(points, splits))
                {
                    result.Add(new ConnectedStrokePart(
                        new DrawingElementHit(
                            new DrawingElementKey(objectIndex, DrawingElementKind.Stroke, part.PartIndex),
                            0,
                            part.StartT,
                            part.EndT),
                        part.Points[0],
                        part.Points[^1]));
                }
            }

            return result.ToArray();
        }

        foreach (var part in BuildBoundaryStrokeParts(objectIndex, candidates))
        {
            result.Add(new ConnectedStrokePart(
                new DrawingElementHit(
                    new DrawingElementKey(objectIndex, DrawingElementKind.BoundaryStroke, part.PartIndex),
                    0,
                    part.StartT,
                    part.EndT),
                part.Points[0],
                part.Points[^1]));
        }

        return result.ToArray();
    }

    public bool TryCreateFillFromClosedStrokeRegion(
        PointF world,
        int frame,
        Color color,
        out int createdObject,
        out PointF[][] animationContours)
    {
        createdObject = -1;
        animationContours = Array.Empty<PointF[]>();
        if (!TryFindClosedStrokeFillRegion(world, frame, out var region)) return false;

        var hasExactBoundary = TryBuildClosedStrokeFillBezierContours(region, out var bezierContours);
        var atoms = (uint)Math.Clamp(
            hasExactBoundary ? bezierContours.Sum(contour => contour.Length) : region.Contour.Length,
            3,
            4096);
        var created = hasExactBoundary
            ? AppendPathBezierObjectContours(
                ActiveLayer,
                bezierContours,
                0,
                color,
                Color.Transparent,
                atoms)
            : AppendPathObjectContours(
                ActiveLayer,
                new[] { region.Contour },
                0,
                color,
                Color.Transparent,
                atoms);
        if (created < 0) return false;

        var firstBoundary = region.BoundaryObjects
            .Where(index => (uint)index < ObjectCount)
            .OrderBy(index => ObjectOrder[index])
            .ThenBy(index => ObjectSubOrder[index])
            .FirstOrDefault(-1);
        if (firstBoundary >= 0)
        {
            // Keep the generated fill beneath every stroke that forms its boundary.
            ObjectOrder[created] = ObjectOrder[firstBoundary];
            ObjectSubOrder[created] = ObjectSubOrder[firstBoundary] - 0.5d;
        }

        RebuildGeometryIndex();
        RebuildSummaries();
        createdObject = created;
        animationContours = new[] { region.Contour.ToArray() };
        return true;
    }

    public bool TryGetClosedStrokeFillRegion(PointF world, int frame, out PointF[][] contours)
    {
        contours = Array.Empty<PointF[]>();
        if (!TryFindClosedStrokeFillRegion(world, frame, out var region)) return false;
        contours = new[] { region.Contour.ToArray() };
        return true;
    }

    private bool TryFindClosedStrokeFillRegion(PointF world, int frame, out ClosedStrokeFillRegion region)
    {
        region = default;
        const int maximumRawSegments = 2048;

        var rawSegments = new List<ClosedFillRawSegment>();
        void AppendRawSegments(IReadOnlyList<PointF> points, int objectIndex)
        {
            if (points.Count < 2 || rawSegments.Count >= maximumRawSegments) return;
            for (var pointIndex = 0;
                 pointIndex < points.Count - 1 && rawSegments.Count < maximumRawSegments;
                 pointIndex++)
            {
                if (Distance(points[pointIndex], points[pointIndex + 1]) <= 0.001f) continue;
                rawSegments.Add(new ClosedFillRawSegment(
                    points[pointIndex],
                    points[pointIndex + 1],
                    objectIndex));
            }
        }

        for (var objectIndex = 0; objectIndex < ObjectCount; objectIndex++)
        {
            if (ObjectLayer[objectIndex] != ActiveLayer
                || !IsObjectActive(objectIndex, frame))
            {
                continue;
            }

            // A visible Fill edge is a paint boundary even when it has no outline.
            // Use its contour instead of its coincident boundary stroke to keep the
            // planar graph free of duplicate edges for outlined Fill objects.
            if (HasFill(objectIndex))
            {
                foreach (var contour in ShapeBoundaryContours(objectIndex))
                {
                    AppendRawSegments(contour, objectIndex);
                    if (rawSegments.Count >= maximumRawSegments) break;
                }
                if (rawSegments.Count >= maximumRawSegments) break;
                continue;
            }

            if (!HasStroke(objectIndex)) continue;
            foreach (var part in BuildConnectedStrokeParts(objectIndex, frame))
            {
                var points = ClosedFillPartPoints(part, frame);
                AppendRawSegments(points, objectIndex);
                if (rawSegments.Count >= maximumRawSegments) break;
            }

            if (rawSegments.Count >= maximumRawSegments) break;
        }

        if (rawSegments.Count < 3) return false;

        var splitParameters = rawSegments
            .Select(_ => new List<float> { 0f, 1f })
            .ToArray();
        for (var left = 0; left < rawSegments.Count; left++)
        {
            var first = rawSegments[left];
            for (var right = left + 1; right < rawSegments.Count; right++)
            {
                var second = rawSegments[right];
                if (!ClosedFillSegmentBoundsOverlap(first.Start, first.End, second.Start, second.End)) continue;
                if (TryClosedFillSegmentIntersection(first.Start, first.End, second.Start, second.End, out var firstT, out var secondT))
                {
                    splitParameters[left].Add(firstT);
                    splitParameters[right].Add(secondT);
                }

                AddClosedFillEndpointProjection(first.Start, second.Start, second.End, splitParameters[right]);
                AddClosedFillEndpointProjection(first.End, second.Start, second.End, splitParameters[right]);
                AddClosedFillEndpointProjection(second.Start, first.Start, first.End, splitParameters[left]);
                AddClosedFillEndpointProjection(second.End, first.Start, first.End, splitParameters[left]);
            }
        }

        var vertices = new List<PointF>();
        var edges = new List<ClosedFillGraphEdge>();
        var uniqueEdges = new HashSet<(int First, int Second)>();
        for (var segmentIndex = 0; segmentIndex < rawSegments.Count; segmentIndex++)
        {
            var segment = rawSegments[segmentIndex];
            var parameters = splitParameters[segmentIndex]
                .OrderBy(value => value)
                .Aggregate(new List<float>(), (result, value) =>
                {
                    if (result.Count == 0 || value - result[^1] > 0.0001f) result.Add(value);
                    return result;
                });
            for (var parameterIndex = 0; parameterIndex < parameters.Count - 1; parameterIndex++)
            {
                var start = Lerp(segment.Start, segment.End, parameters[parameterIndex]);
                var end = Lerp(segment.Start, segment.End, parameters[parameterIndex + 1]);
                if (Distance(start, end) <= 0.001f) continue;
                var from = FindClosedFillVertex(vertices, start);
                var to = FindClosedFillVertex(vertices, end);
                if (from == to) continue;
                var edgeKey = from < to ? (from, to) : (to, from);
                if (!uniqueEdges.Add(edgeKey)) continue;
                edges.Add(new ClosedFillGraphEdge(from, to, new[] { start, end }, segment.ObjectIndex));
            }
        }

        if (edges.Count < 3 || vertices.Count < 3) return false;

        var halfFrom = new List<int>(edges.Count * 2);
        var halfTo = new List<int>(edges.Count * 2);
        var outgoing = Enumerable.Range(0, vertices.Count).Select(_ => new List<int>()).ToArray();
        for (var edgeIndex = 0; edgeIndex < edges.Count; edgeIndex++)
        {
            var edge = edges[edgeIndex];
            var forward = halfFrom.Count;
            halfFrom.Add(edge.From);
            halfTo.Add(edge.To);
            outgoing[edge.From].Add(forward);

            var backward = halfFrom.Count;
            halfFrom.Add(edge.To);
            halfTo.Add(edge.From);
            outgoing[edge.To].Add(backward);
        }

        for (var vertex = 0; vertex < outgoing.Length; vertex++)
        {
            outgoing[vertex].Sort((left, right) =>
            {
                var leftPoint = vertices[halfTo[left]];
                var rightPoint = vertices[halfTo[right]];
                var origin = vertices[vertex];
                var leftAngle = Math.Atan2(leftPoint.Y - origin.Y, leftPoint.X - origin.X);
                var rightAngle = Math.Atan2(rightPoint.Y - origin.Y, rightPoint.X - origin.X);
                return leftAngle.CompareTo(rightAngle);
            });
        }

        var visited = new bool[halfFrom.Count];
        var bestArea = float.MaxValue;
        PointF[]? bestContour = null;
        int[]? bestBoundaryObjects = null;
        for (var start = 0; start < halfFrom.Count; start++)
        {
            if (visited[start]) continue;

            var face = new List<int>();
            var current = start;
            var closed = false;
            for (var step = 0; step <= halfFrom.Count; step++)
            {
                if (current == start && face.Count > 0)
                {
                    closed = true;
                    break;
                }

                if (visited[current]) break;
                visited[current] = true;
                face.Add(current);

                var atVertex = halfTo[current];
                var reverse = current ^ 1;
                var fan = outgoing[atVertex];
                var reversePosition = fan.IndexOf(reverse);
                if (reversePosition < 0 || fan.Count < 2) break;
                current = fan[(reversePosition - 1 + fan.Count) % fan.Count];
            }

            if (!closed || face.Count < 3) continue;
            var facePoints = face.Select(half => vertices[halfFrom[half]]).ToArray();
            var area = PolygonArea(facePoints);
            // The half-edge walk keeps bounded faces counter-clockwise; the outer face is clockwise.
            if (area <= 0.5f || area >= bestArea || !PointInPolygonOrOnBoundary(world, facePoints)) continue;

            var contour = BuildClosedFillContour(face, edges);
            if (contour.Length < 3 || Math.Abs(PolygonArea(contour)) < 0.5f) continue;
            bestArea = area;
            bestContour = contour;
            bestBoundaryObjects = face
                .Select(half => edges[half / 2].ObjectIndex)
                .Distinct()
                .ToArray();
        }

        if (bestContour is null || bestBoundaryObjects is null) return false;
        region = new ClosedStrokeFillRegion(bestContour, bestBoundaryObjects);
        return true;
    }

    private bool TryBuildClosedStrokeFillBezierContours(
        ClosedStrokeFillRegion region,
        out PathBezierNode[][] contours)
    {
        contours = [];
        var sources = new List<PreparedBezierCurve>();
        foreach (var objectIndex in region.BoundaryObjects)
        {
            if ((uint)objectIndex >= ObjectCount) continue;
            if (ShapeKind[objectIndex] == VectorAnimationEngine.ShapeKind.Line)
            {
                AddPreparedBezierCurve(sources, LineCurve(objectIndex));
                continue;
            }

            if (ShapeKind[objectIndex] == VectorAnimationEngine.ShapeKind.Freeform
                && TryGetFreehandWorldPoints(objectIndex, out var freehandPoints))
            {
                foreach (var curve in FitPolylineBezierSegments(
                             freehandPoints,
                             ConnectedStrokeEndpointToleranceUnits))
                {
                    AddPreparedBezierCurve(sources, curve);
                }
                continue;
            }

            if (!TryGetEditableFillBezierContours(objectIndex, out var boundaryContours)) continue;
            foreach (var segment in BuildPathBezierSegmentParts(boundaryContours))
            {
                AddPreparedBezierCurve(sources, segment.Curve);
            }
        }

        if (sources.Count == 0
            || !TryRebuildBezierContourFromBooleanBoundary(region.Contour, sources, out var contour))
        {
            return false;
        }

        var rebuilt = new[] { contour };
        if (!BooleanBezierContoursMatch(new[] { region.Contour }, rebuilt)) return false;
        contours = rebuilt;
        return true;
    }

    private static CubicBoundarySegment[] FitPolylineBezierSegments(
        IReadOnlyList<PointF> source,
        float maximumError)
    {
        if (source.Count < 2) return [];
        var points = new List<PointF>(source.Count);
        foreach (var point in source)
        {
            if (points.Count == 0 || Distance(points[^1], point) > 0.001f)
            {
                points.Add(point);
            }
        }
        if (points.Count < 2) return [];

        maximumError = Math.Max(0.1f, maximumError);
        var result = new List<CubicBoundarySegment>();
        var closed = points.Count > 2
            && Distance(points[0], points[^1]) <= ConnectedStrokeEndpointToleranceUnits;
        if (!closed)
        {
            FitOpenPolylineBezierSegments(points.ToArray(), maximumError, result);
            return result.ToArray();
        }

        if (Distance(points[0], points[^1]) <= 0.001f) points.RemoveAt(points.Count - 1);
        if (points.Count < 3) return [];
        var split = points.Count / 2;
        FitOpenPolylineBezierSegments(points.Take(split + 1).ToArray(), maximumError, result);
        FitOpenPolylineBezierSegments(
            points.Skip(split).Concat([points[0]]).ToArray(),
            maximumError,
            result);
        return result.ToArray();
    }

    private static void FitOpenPolylineBezierSegments(
        PointF[] points,
        float maximumError,
        ICollection<CubicBoundarySegment> result,
        ICollection<(int First, int Last)>? fittedRanges = null)
    {
        if (points.Length < 2) return;
        FitRange(
            0,
            points.Length - 1,
            UnitDirection(points[0], points[1]),
            UnitDirection(points[^1], points[^2]));

        void FitRange(int first, int last, PointF leftTangent, PointF rightTangent)
        {
            if (last - first == 1)
            {
                result.Add(new CubicBoundarySegment(
                    points[first],
                    Lerp(points[first], points[last], 1f / 3f),
                    Lerp(points[first], points[last], 2f / 3f),
                    points[last]));
                fittedRanges?.Add((first, last));
                return;
            }

            var parameters = ChordLengthParameters(points, first, last);
            var curve = GenerateFittedCubic(
                points,
                first,
                last,
                parameters,
                leftTangent,
                rightTangent);
            var samples = SampleCubicSegment(curve);
            var split = (first + last) / 2;
            var maximumPointError = 0f;
            for (var index = first + 1; index < last; index++)
            {
                var error = DistanceToPolyline(points[index], samples);
                if (error <= maximumPointError) continue;
                maximumPointError = error;
                split = index;
            }

            var rangePoints = points.AsSpan(first, last - first + 1).ToArray();
            var followsPolyline = maximumPointError <= maximumError
                && samples.All(point =>
                    DistanceToPolyline(point, rangePoints) <= maximumError);
            if (followsPolyline)
            {
                result.Add(curve);
                fittedRanges?.Add((first, last));
                return;
            }

            if (split <= first || split >= last) split = (first + last) / 2;
            var centerTangent = UnitDirection(points[split + 1], points[split - 1]);
            if (centerTangent == PointF.Empty)
            {
                centerTangent = UnitDirection(points[split], points[split - 1]);
            }
            FitRange(first, split, leftTangent, centerTangent);
            FitRange(split, last, Negate(centerTangent), rightTangent);
        }
    }

    private static float[] ChordLengthParameters(PointF[] points, int first, int last)
    {
        var result = new float[last - first + 1];
        for (var index = first + 1; index <= last; index++)
        {
            result[index - first] = result[index - first - 1] + Distance(points[index - 1], points[index]);
        }

        var length = result[^1];
        if (length <= 0.001f)
        {
            for (var index = 1; index < result.Length; index++)
            {
                result[index] = index / (float)(result.Length - 1);
            }
            return result;
        }

        for (var index = 1; index < result.Length; index++) result[index] /= length;
        return result;
    }

    private static CubicBoundarySegment GenerateFittedCubic(
        PointF[] points,
        int first,
        int last,
        IReadOnlyList<float> parameters,
        PointF leftTangent,
        PointF rightTangent)
    {
        double c00 = 0;
        double c01 = 0;
        double c11 = 0;
        double x0 = 0;
        double x1 = 0;
        for (var index = 0; index < parameters.Count; index++)
        {
            var t = parameters[index];
            var mt = 1f - t;
            var b0 = mt * mt * mt;
            var b1 = 3f * t * mt * mt;
            var b2 = 3f * t * t * mt;
            var b3 = t * t * t;
            var a1 = new PointF(leftTangent.X * b1, leftTangent.Y * b1);
            var a2 = new PointF(rightTangent.X * b2, rightTangent.Y * b2);
            var point = points[first + index];
            var residual = new PointF(
                point.X - points[first].X * (b0 + b1) - points[last].X * (b2 + b3),
                point.Y - points[first].Y * (b0 + b1) - points[last].Y * (b2 + b3));
            c00 += Dot(a1, a1);
            c01 += Dot(a1, a2);
            c11 += Dot(a2, a2);
            x0 += Dot(a1, residual);
            x1 += Dot(a2, residual);
        }

        var determinant = c00 * c11 - c01 * c01;
        var alpha1 = determinant == 0 ? 0 : (x0 * c11 - x1 * c01) / determinant;
        var alpha2 = determinant == 0 ? 0 : (c00 * x1 - c01 * x0) / determinant;
        var chord = Distance(points[first], points[last]);
        var minimumAlpha = chord * 0.0001f;
        if (!double.IsFinite(alpha1)
            || !double.IsFinite(alpha2)
            || alpha1 < minimumAlpha
            || alpha2 < minimumAlpha
            || alpha1 > chord * 10
            || alpha2 > chord * 10)
        {
            alpha1 = chord / 3f;
            alpha2 = chord / 3f;
        }

        return new CubicBoundarySegment(
            points[first],
            AddScaled(points[first], leftTangent, (float)alpha1),
            AddScaled(points[last], rightTangent, (float)alpha2),
            points[last]);
    }

    private static PointF UnitDirection(PointF from, PointF to)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var length = MathF.Sqrt(dx * dx + dy * dy);
        return length <= 0.0001f ? PointF.Empty : new PointF(dx / length, dy / length);
    }

    private static PointF AddScaled(PointF point, PointF direction, float scale)
    {
        return new PointF(point.X + direction.X * scale, point.Y + direction.Y * scale);
    }

    private static PointF Negate(PointF point) => new(-point.X, -point.Y);

    private static float Dot(PointF first, PointF second)
    {
        return first.X * second.X + first.Y * second.Y;
    }

    private PointF[] ClosedFillPartPoints(ConnectedStrokePart part, int frame)
    {
        var objectIndex = part.Hit.Key.ObjectIndex;
        if ((uint)objectIndex >= ObjectCount) return Array.Empty<PointF>();

        if (ShapeKind[objectIndex] == VectorAnimationEngine.ShapeKind.Line)
        {
            var curve = LineCurve(objectIndex);
            var segment = CubicSubcurve(curve.Start, curve.Control1, curve.Control2, curve.End, part.Hit.StartT, part.Hit.EndT);
            var samples = new List<CurveSample>(12) { new(0, part.Start) };
            AddAdaptiveCubicSamples(samples, segment.Start, segment.Control1, segment.Control2, segment.End, 0, 1, 0);
            samples.Add(new CurveSample(1, part.End));
            return samples.Select(sample => sample.Point).ToArray();
        }

        if (IsFreehandShape(ShapeKind[objectIndex]) && TryGetFreehandWorldPoints(objectIndex, out var freehand))
        {
            return PolylineSlice(freehand, part.Hit.StartT, part.Hit.EndT);
        }

        var boundary = GetBoundaryPartPoints(part.Hit, frame);
        return boundary.Length >= 2 ? boundary : new[] { part.Start, part.End };
    }

    private static bool ClosedFillSegmentBoundsOverlap(PointF firstStart, PointF firstEnd, PointF secondStart, PointF secondEnd)
    {
        var tolerance = ConnectedStrokeEndpointToleranceUnits;
        return Math.Max(firstStart.X, firstEnd.X) + tolerance >= Math.Min(secondStart.X, secondEnd.X)
            && Math.Max(secondStart.X, secondEnd.X) + tolerance >= Math.Min(firstStart.X, firstEnd.X)
            && Math.Max(firstStart.Y, firstEnd.Y) + tolerance >= Math.Min(secondStart.Y, secondEnd.Y)
            && Math.Max(secondStart.Y, secondEnd.Y) + tolerance >= Math.Min(firstStart.Y, firstEnd.Y);
    }

    private static bool TryClosedFillSegmentIntersection(
        PointF firstStart,
        PointF firstEnd,
        PointF secondStart,
        PointF secondEnd,
        out float firstT,
        out float secondT)
    {
        firstT = 0;
        secondT = 0;
        var firstX = firstEnd.X - firstStart.X;
        var firstY = firstEnd.Y - firstStart.Y;
        var secondX = secondEnd.X - secondStart.X;
        var secondY = secondEnd.Y - secondStart.Y;
        var determinant = firstX * secondY - firstY * secondX;
        if (Math.Abs(determinant) <= 0.000001f) return false;

        var offsetX = secondStart.X - firstStart.X;
        var offsetY = secondStart.Y - firstStart.Y;
        var first = (offsetX * secondY - offsetY * secondX) / determinant;
        var second = (offsetX * firstY - offsetY * firstX) / determinant;
        var firstLength = MathF.Sqrt(firstX * firstX + firstY * firstY);
        var secondLength = MathF.Sqrt(secondX * secondX + secondY * secondY);
        var firstTolerance = ConnectedStrokeEndpointToleranceUnits / Math.Max(0.001f, firstLength);
        var secondTolerance = ConnectedStrokeEndpointToleranceUnits / Math.Max(0.001f, secondLength);
        if (first < -firstTolerance || first > 1 + firstTolerance
            || second < -secondTolerance || second > 1 + secondTolerance)
        {
            return false;
        }

        firstT = Math.Clamp(first, 0, 1);
        secondT = Math.Clamp(second, 0, 1);
        return true;
    }

    private static void AddClosedFillEndpointProjection(PointF point, PointF segmentStart, PointF segmentEnd, List<float> parameters)
    {
        var dx = segmentEnd.X - segmentStart.X;
        var dy = segmentEnd.Y - segmentStart.Y;
        var lengthSquared = dx * dx + dy * dy;
        if (lengthSquared <= 0.000001f) return;

        var length = MathF.Sqrt(lengthSquared);
        var parameter = ((point.X - segmentStart.X) * dx + (point.Y - segmentStart.Y) * dy) / lengthSquared;
        var tolerance = ConnectedStrokeEndpointToleranceUnits / length;
        if (parameter < -tolerance || parameter > 1 + tolerance) return;
        var clamped = Math.Clamp(parameter, 0, 1);
        var closest = Lerp(segmentStart, segmentEnd, clamped);
        if (Distance(point, closest) <= ConnectedStrokeEndpointToleranceUnits) parameters.Add(clamped);
    }

    private static int FindClosedFillVertex(List<PointF> vertices, PointF point)
    {
        for (var index = 0; index < vertices.Count; index++)
        {
            if (Distance(vertices[index], point) <= ConnectedStrokeEndpointToleranceUnits) return index;
        }

        vertices.Add(VectorUnits.Quantize(point));
        return vertices.Count - 1;
    }

    private static PointF[] BuildClosedFillContour(IReadOnlyList<int> face, IReadOnlyList<ClosedFillGraphEdge> edges)
    {
        var result = new List<PointF>();
        foreach (var half in face)
        {
            var edge = edges[half / 2];
            var forward = (half & 1) == 0;
            var points = edge.Points;
            for (var offset = 0; offset < points.Length - 1; offset++)
            {
                var index = forward ? offset : points.Length - 1 - offset;
                var point = VectorUnits.Quantize(points[index]);
                if (result.Count == 0 || Distance(result[^1], point) > 0.001f) result.Add(point);
            }
        }

        if (result.Count > 1 && Distance(result[0], result[^1]) <= 0.001f) result.RemoveAt(result.Count - 1);
        return result.ToArray();
    }

    private static (int X, int Y) ConnectedEndpointKey(PointF point)
    {
        return ((int)MathF.Round(point.X), (int)MathF.Round(point.Y));
    }

    public long EstimateElementAtomCount(DrawingElementHit hit, int frame)
    {
        if (!hit.IsValid || (uint)hit.Key.ObjectIndex >= ObjectCount) return 0;
        var atoms = Math.Max(1u, AtomCount[hit.Key.ObjectIndex]);
        if (ShapeKind[hit.Key.ObjectIndex] == VectorAnimationEngine.ShapeKind.MixingStroke
            && hit.Key.Kind == DrawingElementKind.Fill
            && TryGetMixingBrushLocalRegion(hit.Key.ObjectIndex, out var mixingRegion))
        {
            if (!mixingRegion.TryGetConnectedComponent(hit.Key.PartIndex, out var component)) return 0;
            var totalCost = Math.Max(
                1,
                mixingRegion.Vertices.Length + mixingRegion.TriangleIndices.Length / 3);
            var componentCost = Math.Max(
                1,
                component.Vertices.Length + component.TriangleIndices.Length / 3);
            return Math.Max(1, (long)Math.Round(atoms * Math.Clamp(componentCost / (double)totalCost, 0, 1)));
        }
        if (IsWholeObjectShape(ShapeKind[hit.Key.ObjectIndex])) return atoms;
        if (hit.Key.Kind == DrawingElementKind.Fill)
        {
            var candidates = CollectTopologyCandidates(hit.Key.ObjectIndex, frame);
            var regions = BuildFillRegions(hit.Key.ObjectIndex, frame, candidates);
            if ((uint)hit.Key.PartIndex >= regions.Count) return 0;
            var total = regions.Sum(region => Math.Max(0.001f, region.Area));
            var fraction = total > 0 ? regions[hit.Key.PartIndex].Area / total : 1;
            return Math.Max(1, (long)Math.Round(atoms * Math.Clamp(fraction, 0, 1)));
        }

        var parts = hit.Key.Kind == DrawingElementKind.BoundaryStroke
            ? GetBoundaryParts(hit.Key.ObjectIndex, frame)
            : GetStrokeParts(hit.Key.ObjectIndex, frame);
        if (parts.Length > 0)
        {
            var total = parts.Sum(part => Math.Max(0.001f, PolylineLength(part.Points)));
            var selected = parts.FirstOrDefault(part => part.PartIndex == hit.Key.PartIndex);
            if (selected.Points is not null)
            {
                var fraction = total > 0 ? PolylineLength(selected.Points) / total : 1;
                return Math.Max(1, (long)Math.Round(atoms * Math.Clamp(fraction, 0, 1)));
            }
        }

        var parameterFraction = Math.Clamp(hit.EndT - hit.StartT, 0, 1);
        return Math.Max(1, (long)Math.Round(atoms * parameterFraction));
    }

    public bool FillContainsPoint(int objectIndex, PointF world)
    {
        return TryInverseMapObjectPoint(objectIndex, world, out var sourceWorld)
            && FillContainsRawPoint(objectIndex, sourceWorld);
    }

    private bool FillContainsRawPoint(int objectIndex, PointF world)
    {
        if ((uint)objectIndex >= ObjectCount) return false;
        var shape = ShapeKind.Length > objectIndex ? ShapeKind[objectIndex] : VectorAnimationEngine.ShapeKind.Rectangle;
        if (shape == VectorAnimationEngine.ShapeKind.MixingStroke)
        {
            return MixingStrokeContainsPoint(objectIndex, world, 0);
        }
        if (!IsFillShape(shape) || !HasFill(objectIndex)) return false;
        if (shape == VectorAnimationEngine.ShapeKind.Path && TryGetPathWorldContours(objectIndex, out var contours))
        {
            return PointInCompoundPolygonOrOnBoundary(world, contours);
        }

        var polygon = OpenPolygon(ShapeBoundary(objectIndex));
        return polygon.Length >= 3 && PointInPolygonOrOnBoundary(world, polygon);
    }

    public bool TryGetPathWorldPoints(int objectIndex, out PointF[] points)
    {
        if (!TryGetPathWorldContours(objectIndex, out var contours) || contours.Length == 0)
        {
            points = Array.Empty<PointF>();
            return false;
        }

        points = contours[0];
        return true;
    }

    public bool TryGetPathWorldContours(int objectIndex, out PointF[][] contours)
    {
        if ((uint)objectIndex >= ObjectCount || !_pathLocalContours.TryGetValue(objectIndex, out var localContours))
        {
            contours = Array.Empty<PointF[]>();
            return false;
        }

        contours = new PointF[localContours.Length][];
        for (var c = 0; c < localContours.Length; c++)
        {
            var local = localContours[c];
            var world = new PointF[local.Length];
            for (var i = 0; i < local.Length; i++) world[i] = LocalToWorld(objectIndex, local[i].X, local[i].Y);
            contours[c] = world;
        }

        return contours.Length > 0;
    }

    internal bool TryGetPathLocalContours(int objectIndex, out PointF[][] contours)
    {
        if ((uint)objectIndex < ObjectCount && _pathLocalContours.TryGetValue(objectIndex, out var localContours))
        {
            contours = localContours;
            return contours.Length > 0;
        }

        contours = Array.Empty<PointF[]>();
        return false;
    }

    public bool TryGetPathBezierWorldContours(int objectIndex, out PathBezierNode[][] contours)
    {
        if ((uint)objectIndex >= ObjectCount
            || ShapeKind[objectIndex] != VectorAnimationEngine.ShapeKind.Path
            || !_pathBezierLocalContours.TryGetValue(objectIndex, out var localContours))
        {
            contours = Array.Empty<PathBezierNode[]>();
            return false;
        }

        contours = new PathBezierNode[localContours.Length][];
        for (var contourIndex = 0; contourIndex < localContours.Length; contourIndex++)
        {
            var local = localContours[contourIndex];
            var world = new PathBezierNode[local.Length];
            for (var nodeIndex = 0; nodeIndex < local.Length; nodeIndex++)
            {
                var node = local[nodeIndex];
                world[nodeIndex] = new PathBezierNode(
                    LocalToWorld(objectIndex, node.Anchor.X, node.Anchor.Y),
                    LocalToWorld(objectIndex, node.IncomingControl.X, node.IncomingControl.Y),
                    LocalToWorld(objectIndex, node.OutgoingControl.X, node.OutgoingControl.Y));
            }

            contours[contourIndex] = world;
        }

        return contours.Length > 0;
    }

    internal bool TryGetPathBezierLocalContours(int objectIndex, out PathBezierNode[][] contours)
    {
        if ((uint)objectIndex < ObjectCount
            && ShapeKind[objectIndex] == VectorAnimationEngine.ShapeKind.Path
            && _pathBezierLocalContours.TryGetValue(objectIndex, out var localContours))
        {
            contours = localContours;
            return contours.Length > 0;
        }

        contours = Array.Empty<PathBezierNode[]>();
        return false;
    }

    public PathBezierSegmentPart[] GetPathBezierSegmentParts(int objectIndex)
    {
        if (!TryGetPathBezierWorldContours(objectIndex, out var contours)) return Array.Empty<PathBezierSegmentPart>();
        return BuildPathBezierSegmentParts(contours);
    }

    public PathBezierSegmentPart[] GetEditableFillBezierSegmentParts(int objectIndex)
    {
        if (!TryGetEditableFillBezierContours(objectIndex, out var contours))
        {
            return Array.Empty<PathBezierSegmentPart>();
        }

        return BuildPathBezierSegmentParts(contours);
    }

    private bool TryGetEditableFillBezierContours(int objectIndex, out PathBezierNode[][] contours)
    {
        contours = Array.Empty<PathBezierNode[]>();
        if ((uint)objectIndex >= ObjectCount || !IsFillShape(ShapeKind[objectIndex]))
        {
            return false;
        }

        if (TryGetPathBezierWorldContours(objectIndex, out var exactContours))
        {
            contours = exactContours;
            return true;
        }

        if (ShapeKind[objectIndex] == VectorAnimationEngine.ShapeKind.Path)
        {
            if (!TryGetPathWorldContours(objectIndex, out var polygonContours)) return false;
            contours = polygonContours.Select(CreateLinearBezierContour).ToArray();
        }
        else if (ShapeKind[objectIndex] == VectorAnimationEngine.ShapeKind.Ellipse)
        {
            contours = [CreateBezierContour(EllipseBoundaryCurves(objectIndex))];
        }
        else
        {
            var boundary = OpenPolygon(ShapeBoundary(objectIndex));
            if (boundary.Length < 3) return false;
            contours = [CreateLinearBezierContour(boundary)];
        }

        return contours.Length > 0;
    }

    public PathBezierSegmentPart[] GetExposedFillBezierSegmentParts(int objectIndex, int frame)
    {
        var parts = GetEditableFillBezierSegmentParts(objectIndex);
        if (parts.Length == 0 || !IsObjectActive(objectIndex, frame)) return [];

        var queryBounds = GetObjectWorldBounds(objectIndex);
        queryBounds.Inflate(ConnectedStrokeEndpointToleranceUnits, ConnectedStrokeEndpointToleranceUnits);
        var coincidentStrokePaths = new List<PointF[]>();
        foreach (var candidate in QueryObjects(queryBounds, frame))
        {
            if (candidate == objectIndex
                || ObjectLayer[candidate] != ObjectLayer[objectIndex]
                || ObjectKeyframeFrame[candidate] != ObjectKeyframeFrame[objectIndex]
                || !IsFillBoundaryLinkedStrokeShape(ShapeKind[candidate])
                || !HasStroke(candidate))
            {
                continue;
            }

            var points = StrokeSamples(candidate).Select(sample => sample.Point).ToArray();
            if (points.Length >= 2) coincidentStrokePaths.Add(points);
        }

        if (coincidentStrokePaths.Count == 0) return parts;
        return parts
            .Where(part => !coincidentStrokePaths.Any(stroke => FillBezierSegmentFollowsStroke(part, stroke)))
            .ToArray();
    }

    public FillBezierSegmentPiece[] GetExposedFillBezierSegmentPieces(
        int objectIndex,
        int frame,
        int? fillPartIndex = null,
        bool includeCoincidentStrokes = false)
    {
        if (_exposedFillBezierCacheRevision != GeometryRevision)
        {
            _exposedFillBezierCache.Clear();
            _exposedFillBezierCacheRevision = GeometryRevision;
        }

        var cacheKey = (objectIndex, frame, fillPartIndex ?? -1, includeCoincidentStrokes);
        if (_exposedFillBezierCache.TryGetValue(cacheKey, out var cached)) return cached;

        var pieces = BuildExposedFillBezierSegmentPieces(
            objectIndex,
            frame,
            fillPartIndex,
            includeCoincidentStrokes);
        if (_exposedFillBezierCache.Count >= MaximumTopologyQueryCacheEntries) _exposedFillBezierCache.Clear();
        _exposedFillBezierCache[cacheKey] = pieces;
        return pieces;
    }

    private FillBezierSegmentPiece[] BuildExposedFillBezierSegmentPieces(
        int objectIndex,
        int frame,
        int? fillPartIndex,
        bool includeCoincidentStrokes)
    {
        // The fill-boundary overlay edits the fill contour, which keeps its full closed
        // outline even where the stroke was detached (hidden) — so no filtering here.
        var sourceParts = GetEditableFillBezierSegmentParts(objectIndex);
        if (sourceParts.Length == 0 || !IsObjectActive(objectIndex, frame)) return [];

        var candidates = CollectTopologyCandidates(objectIndex, frame);
        PointF[][]? selectedContours = null;
        if (fillPartIndex is { } selectedPart)
        {
            var regions = BuildFillRegions(objectIndex, frame, candidates);
            if ((uint)selectedPart >= regions.Count) return [];
            selectedContours = regions[selectedPart].Contours
                .Where(contour => contour.Length >= 2)
                .Select(ClosePolyline)
                .ToArray();
            if (selectedContours.Length == 0) return [];
        }

        var sourceLayer = ObjectLayer[objectIndex];
        var preparedSourceCurves = sourceParts
            .Select(part => PrepareBezierCurve(part.Curve))
            .ToArray();
        var lineCurves = PrepareTopologyLineCurves(objectIndex, sourceLayer, candidates);
        var coincidentStrokePaths = new List<PointF[]>();
        if (!includeCoincidentStrokes)
        {
            foreach (var candidate in candidates)
            {
                if (candidate == objectIndex
                    || ObjectLayer[candidate] != sourceLayer
                    || ObjectKeyframeFrame[candidate] != ObjectKeyframeFrame[objectIndex]
                    || !IsFillBoundaryLinkedStrokeShape(ShapeKind[candidate])
                    || !HasStroke(candidate))
                {
                    continue;
                }

                var points = StrokeSamples(candidate).Select(sample => sample.Point).ToArray();
                if (points.Length >= 2) coincidentStrokePaths.Add(points);
            }
        }

        var result = new List<FillBezierSegmentPiece>();
        for (var sourcePartIndex = 0; sourcePartIndex < sourceParts.Length; sourcePartIndex++)
        {
            var sourcePart = sourceParts[sourcePartIndex];
            var source = preparedSourceCurves[sourcePartIndex];
            var sourceCurve = source.Curve;
            var normalized = CollectFillBezierTopologySplits(
                objectIndex,
                sourceLayer,
                source,
                preparedSourceCurves,
                candidates,
                lineCurves);
            var retained = new List<(DrawingTopologySplit Start, DrawingTopologySplit End)>();
            for (var splitIndex = 0; splitIndex < normalized.Count - 1; splitIndex++)
            {
                var startSplit = normalized[splitIndex];
                var endSplit = normalized[splitIndex + 1];
                if (endSplit.T - startSplit.T <= 0.0001f) continue;
                var curve = CubicSubcurve(
                    sourcePart.Start,
                    sourcePart.Control1,
                    sourcePart.Control2,
                    sourcePart.End,
                    startSplit.T,
                    endSplit.T);
                var samples = SampleCubicSegment(curve);
                if (Math.Max(Distance(curve.Start, curve.End), PolylineLength(samples))
                    < DrawingTopologyRules.MinStrokeSegmentUnits)
                {
                    continue;
                }

                var bordersSelectedPart = selectedContours is null
                    || FillBezierSegmentBordersFillPart(curve, selectedContours);
                var followsStroke = !includeCoincidentStrokes && coincidentStrokePaths.Any(stroke =>
                    FillBezierSegmentFollowsStroke(samples, stroke));
                if (!bordersSelectedPart || followsStroke) continue;

                // A real topology intersection remains an edit boundary even when
                // both adjacent fill pieces are visible. Merging here makes a drag
                // on one side mutate the source cubic on the other side as well.
                retained.Add((startSplit, endSplit));
            }

            foreach (var interval in retained)
            {
                var subcurve = CubicSubcurve(
                    sourcePart.Start,
                    sourcePart.Control1,
                    sourcePart.Control2,
                    sourcePart.End,
                    interval.Start.T,
                    interval.End.T);
                result.Add(new FillBezierSegmentPiece(
                    result.Count,
                    sourcePart.PartIndex,
                    sourcePart.ContourIndex,
                    sourcePart.SegmentIndex,
                    interval.Start.T,
                    interval.End.T,
                    subcurve.Start,
                    subcurve.Control1,
                    subcurve.Control2,
                    subcurve.End,
                    interval.Start.T > 0.0001f,
                    interval.End.T < 0.9999f));
            }
        }

        return result.ToArray();
    }

    public FillBezierSegmentPiece[] RefreshFillBezierSegmentPieces(
        int objectIndex,
        IReadOnlyList<FillBezierSegmentPiece> pieces)
    {
        return RefreshFillBezierSegmentPieces(objectIndex, EditFrame, pieces);
    }

    public FillBezierSegmentPiece[] RefreshFillBezierSegmentPieces(
        int objectIndex,
        int frame,
        IReadOnlyList<FillBezierSegmentPiece> pieces)
    {
        if ((uint)objectIndex >= ObjectCount || pieces.Count == 0)
        {
            return Array.Empty<FillBezierSegmentPiece>();
        }

        if (!TryGetPathBezierWorldContours(objectIndex, out var contours))
        {
            return Array.Empty<FillBezierSegmentPiece>();
        }

        var sourceCurves = BuildPathBezierCurves(contours);
        var refreshed = new FillBezierSegmentPiece[pieces.Count];
        IReadOnlyList<int>? topologyCandidates = null;
        Dictionary<int, List<DrawingTopologySplit>>? topologySplits = null;
        PreparedBezierCurve[]? topologyCurves = null;
        Dictionary<int, PreparedBezierCurve>? topologyLineCurves = null;
        for (var index = 0; index < pieces.Count; index++)
        {
            var piece = pieces[index];
            if ((uint)piece.SourcePartIndex >= sourceCurves.Length)
            {
                return Array.Empty<FillBezierSegmentPiece>();
            }

            var source = sourceCurves[piece.SourcePartIndex];
            var startT = piece.StartT;
            var endT = piece.EndT;
            var subcurve = CubicSubcurve(
                source.Start,
                source.Control1,
                source.Control2,
                source.End,
                startT,
                endT);
            var refreshStart = piece.StartIsVirtualAnchor
                && Distance(subcurve.Start, piece.Start) > DrawingTopologyRules.UnitIntersectionTolerance;
            var refreshEnd = piece.EndIsVirtualAnchor
                && Distance(subcurve.End, piece.End) > DrawingTopologyRules.UnitIntersectionTolerance;
            if (refreshStart || refreshEnd)
            {
                topologyCandidates ??= CollectTopologyCandidates(objectIndex, frame);
                topologySplits ??= new Dictionary<int, List<DrawingTopologySplit>>();
                topologyCurves ??= sourceCurves.Select(PrepareBezierCurve).ToArray();
                topologyLineCurves ??= PrepareTopologyLineCurves(
                    objectIndex,
                    ObjectLayer[objectIndex],
                    topologyCandidates);
                if (!topologySplits.TryGetValue(piece.SourcePartIndex, out var splits))
                {
                    splits = CollectFillBezierTopologySplits(
                        objectIndex,
                        ObjectLayer[objectIndex],
                        topologyCurves[piece.SourcePartIndex],
                        topologyCurves,
                        topologyCandidates,
                        topologyLineCurves);
                    topologySplits[piece.SourcePartIndex] = splits;
                }

                if (refreshStart)
                {
                    startT = ResolveRefreshedFillBezierSplit(
                        splits,
                        piece.StartT,
                        0,
                        endT);
                }
                if (refreshEnd)
                {
                    endT = ResolveRefreshedFillBezierSplit(
                        splits,
                        piece.EndT,
                        startT,
                        1);
                }
                subcurve = CubicSubcurve(
                    source.Start,
                    source.Control1,
                    source.Control2,
                    source.End,
                    startT,
                    endT);
            }
            refreshed[index] = piece with
            {
                StartT = startT,
                EndT = endT,
                Start = subcurve.Start,
                Control1 = subcurve.Control1,
                Control2 = subcurve.Control2,
                End = subcurve.End
            };
        }

        return refreshed;
    }

    internal FillBezierSegmentPiece[] RefreshFillBezierSegmentPiecesForPreview(
        int objectIndex,
        IReadOnlyList<FillBezierSegmentPiece> pieces,
        int editedPartIndex,
        EditHandleKind handle)
    {
        if ((uint)objectIndex >= ObjectCount
            || pieces.Count == 0
            || !TryGetPathBezierPreviewPart(
                objectIndex,
                editedPartIndex,
                out _,
                out var previousPartIndex,
                out var nextPartIndex))
        {
            return Array.Empty<FillBezierSegmentPiece>();
        }

        var refreshPrevious = handle == EditHandleKind.LineStart;
        var refreshNext = handle == EditHandleKind.LineEnd;
        var refreshed = pieces as FillBezierSegmentPiece[] ?? pieces.ToArray();
        for (var index = 0; index < refreshed.Length; index++)
        {
            var piece = refreshed[index];
            if (piece.SourcePartIndex != editedPartIndex
                && (!refreshPrevious || piece.SourcePartIndex != previousPartIndex)
                && (!refreshNext || piece.SourcePartIndex != nextPartIndex))
            {
                continue;
            }

            if (!TryGetPathBezierPreviewPart(
                    objectIndex,
                    piece.SourcePartIndex,
                    out var source,
                    out _,
                    out _))
            {
                return Array.Empty<FillBezierSegmentPiece>();
            }

            var subcurve = CubicSubcurve(
                source.Start,
                source.Control1,
                source.Control2,
                source.End,
                piece.StartT,
                piece.EndT);
            refreshed[index] = piece with
            {
                Start = subcurve.Start,
                Control1 = subcurve.Control1,
                Control2 = subcurve.Control2,
                End = subcurve.End
            };
        }

        return refreshed;
    }

    private bool TryGetPathBezierPreviewPart(
        int objectIndex,
        int partIndex,
        out CubicBoundarySegment curve,
        out int previousPartIndex,
        out int nextPartIndex)
    {
        curve = default;
        previousPartIndex = -1;
        nextPartIndex = -1;
        if ((uint)objectIndex >= ObjectCount
            || partIndex < 0
            || !_pathBezierLocalContours.TryGetValue(objectIndex, out var contours))
        {
            return false;
        }

        var currentPart = 0;
        for (var contourIndex = 0; contourIndex < contours.Length; contourIndex++)
        {
            var contour = contours[contourIndex];
            if (partIndex >= currentPart + contour.Length)
            {
                currentPart += contour.Length;
                continue;
            }

            var segmentIndex = partIndex - currentPart;
            var nextIndex = (segmentIndex + 1) % contour.Length;
            var current = contour[segmentIndex];
            var next = contour[nextIndex];
            curve = new CubicBoundarySegment(
                LocalToWorld(objectIndex, current.Anchor.X, current.Anchor.Y),
                LocalToWorld(objectIndex, current.OutgoingControl.X, current.OutgoingControl.Y),
                LocalToWorld(objectIndex, next.IncomingControl.X, next.IncomingControl.Y),
                LocalToWorld(objectIndex, next.Anchor.X, next.Anchor.Y));
            previousPartIndex = currentPart + (segmentIndex - 1 + contour.Length) % contour.Length;
            nextPartIndex = currentPart + nextIndex;
            return true;
        }

        return false;
    }

    private List<DrawingTopologySplit> CollectFillBezierTopologySplits(
        int objectIndex,
        int sourceLayer,
        PreparedBezierCurve source,
        IReadOnlyList<PreparedBezierCurve> fillCurves,
        IReadOnlyList<int> candidates,
        IReadOnlyDictionary<int, PreparedBezierCurve> lineCurves)
    {
        var splits = new List<DrawingTopologySplit>
        {
            new(0, source.Curve.Start),
            new(1, source.Curve.End)
        };
        foreach (var fillCurve in fillCurves)
        {
            if (fillCurve.Curve == source.Curve) continue;
            AddBezierCurveRelationSplits(
                splits,
                source,
                fillCurve,
                includeSourceEndpoints: true);
        }

        foreach (var candidate in candidates)
        {
            if (candidate == objectIndex
                || ObjectLayer[candidate] != sourceLayer
                || !HasStroke(candidate))
            {
                continue;
            }

            var shape = ShapeKind.Length > candidate
                ? ShapeKind[candidate]
                : VectorAnimationEngine.ShapeKind.Rectangle;
            if (shape == VectorAnimationEngine.ShapeKind.Line)
            {
                AddBezierCurveRelationSplits(
                    splits,
                    source,
                    lineCurves[candidate],
                    includeSourceEndpoints: true);
            }
            else if (IsTopologyStrokeShape(shape))
            {
                var candidateSamples = StrokeSamples(candidate);
                if (FillBezierSegmentFollowsStroke(source.Samples, candidateSamples)) continue;
                AddCurveCurveIntersections(
                    splits,
                    source.Samples,
                    candidateSamples,
                    includeSourceEndpoints: true);
            }
            else if (IsFillShape(shape))
            {
                foreach (var contour in ShapeBoundaryContours(candidate))
                {
                    if (FillBezierSegmentFollowsStroke(source.Samples, contour)) continue;
                    AddCurvePolylineIntersections(
                        splits,
                        source.Samples,
                        contour,
                        includeSourceEndpoints: true);
                }
            }
        }

        return NormalizeStrokeSplits(
            splits.Select(split => RefineCubicTopologySplit(source.Curve, split)).ToList());
    }

    private Dictionary<int, PreparedBezierCurve> PrepareTopologyLineCurves(
        int objectIndex,
        int sourceLayer,
        IReadOnlyList<int> candidates)
    {
        return candidates
            .Where(candidate => candidate != objectIndex
                && ObjectLayer[candidate] == sourceLayer
                && HasStroke(candidate)
                && ShapeKind[candidate] == VectorAnimationEngine.ShapeKind.Line)
            .ToDictionary(candidate => candidate, candidate => PrepareBezierCurve(LineCurve(candidate)));
    }

    private static float ResolveRefreshedFillBezierSplit(
        IReadOnlyList<DrawingTopologySplit> splits,
        float referenceT,
        float minimumT,
        float maximumT)
    {
        var resolved = referenceT;
        var bestDistance = float.MaxValue;
        foreach (var split in splits)
        {
            if (split.T <= minimumT + 0.0001f || split.T >= maximumT - 0.0001f) continue;
            var distance = Math.Abs(split.T - referenceT);
            if (distance >= bestDistance) continue;
            resolved = split.T;
            bestDistance = distance;
        }

        return resolved;
    }

    private static CubicBoundarySegment[] BuildPathBezierCurves(
        IReadOnlyList<PathBezierNode[]> contours)
    {
        var segmentCount = 0;
        for (var contourIndex = 0; contourIndex < contours.Count; contourIndex++)
        {
            segmentCount += contours[contourIndex].Length;
        }

        var curves = new CubicBoundarySegment[segmentCount];
        var partIndex = 0;
        for (var contourIndex = 0; contourIndex < contours.Count; contourIndex++)
        {
            var contour = contours[contourIndex];
            for (var segmentIndex = 0; segmentIndex < contour.Length; segmentIndex++)
            {
                var current = contour[segmentIndex];
                var next = contour[(segmentIndex + 1) % contour.Length];
                curves[partIndex++] = new CubicBoundarySegment(
                    current.Anchor,
                    current.OutgoingControl,
                    next.IncomingControl,
                    next.Anchor);
            }
        }

        return curves;
    }

    public bool TryResolveFillBezierSegmentPiece(
        int objectIndex,
        FillBezierSegmentPiece reference,
        out FillBezierSegmentPiece resolved,
        out bool reversed)
    {
        resolved = default;
        reversed = false;
        foreach (var candidate in GetEditableFillBezierSegmentParts(objectIndex))
        {
            if (!CubicCurvesCoincide(
                    reference.Start,
                    reference.Control1,
                    reference.Control2,
                    reference.End,
                    candidate.Start,
                    candidate.Control1,
                    candidate.Control2,
                    candidate.End,
                    out reversed))
            {
                continue;
            }

            resolved = new FillBezierSegmentPiece(
                reference.PieceIndex,
                candidate.PartIndex,
                candidate.ContourIndex,
                candidate.SegmentIndex,
                0,
                1,
                candidate.Start,
                candidate.Control1,
                candidate.Control2,
                candidate.End,
                StartIsVirtualAnchor: false,
                EndIsVirtualAnchor: false);
            return true;
        }

        return false;
    }

    public bool TryResolveFillPartForBezierSegmentPiece(
        int objectIndex,
        int frame,
        FillBezierSegmentPiece piece,
        out int fillPartIndex)
    {
        fillPartIndex = -1;
        if ((uint)objectIndex >= ObjectCount
            || !IsObjectActive(objectIndex, frame)
            || !HasFill(objectIndex))
        {
            return false;
        }

        var regions = BuildFillRegions(objectIndex, frame, CollectTopologyCandidates(objectIndex, frame));
        if (regions.Count == 0) return false;
        var curve = new CubicBoundarySegment(
            piece.Start,
            piece.Control1,
            piece.Control2,
            piece.End);
        for (var partIndex = 0; partIndex < regions.Count; partIndex++)
        {
            var closedContours = regions[partIndex].Contours
                .Where(contour => contour.Length >= 2)
                .Select(ClosePolyline)
                .ToArray();
            if (closedContours.Length == 0
                || !FillBezierSegmentBordersFillPart(curve, closedContours))
            {
                continue;
            }

            if (fillPartIndex >= 0) return false;
            fillPartIndex = partIndex;
        }

        return fillPartIndex >= 0;
    }

    private static bool FillBezierSegmentBordersFillPart(
        CubicBoundarySegment segment,
        IReadOnlyList<PointF[]> closedContours)
    {
        var approximateLength = Math.Max(
            Distance(segment.Start, segment.End),
            PolylineLength(SampleCubicSegment(segment)));
        var intervals = Math.Clamp(
            (int)Math.Ceiling(approximateLength / VectorUnits.UnitsPerPixel),
            8,
            128);
        var matchedLength = 0f;
        var previous = segment.Start;
        for (var index = 1; index <= intervals; index++)
        {
            var endT = index / (float)intervals;
            var midpointT = (index - 0.5f) / intervals;
            var current = CubicPoint(
                segment.Start,
                segment.Control1,
                segment.Control2,
                segment.End,
                endT);
            var midpoint = CubicPoint(
                segment.Start,
                segment.Control1,
                segment.Control2,
                segment.End,
                midpointT);
            if (closedContours.Any(contour =>
                    DistanceToPolyline(midpoint, contour) <= ConnectedStrokeEndpointToleranceUnits))
            {
                matchedLength += Distance(previous, current);
            }

            previous = current;
        }

        return matchedLength >= DrawingTopologyRules.MinStrokeSegmentUnits;
    }

    private static bool FillBezierSegmentFollowsStroke(
        PathBezierSegmentPart segment,
        IReadOnlyList<PointF> stroke)
    {
        return FillBezierSegmentFollowsStroke(segment.Samples, stroke);
    }

    private static bool FillBezierSegmentFollowsStroke(
        IReadOnlyList<PointF> samples,
        IReadOnlyList<PointF> stroke)
    {
        if (samples.Count < 2 || stroke.Count < 2) return false;
        return samples.All(sample =>
            DistanceToPolyline(sample, stroke) <= ConnectedStrokeEndpointToleranceUnits);
    }

    private static bool FillBezierSegmentFollowsStroke(
        IReadOnlyList<CurveSample> samples,
        IReadOnlyList<CurveSample> stroke)
    {
        if (samples.Count < 2 || stroke.Count < 2) return false;
        for (var index = 0; index < samples.Count; index++)
        {
            if (DistanceToCurveSamples(samples[index].Point, stroke) > ConnectedStrokeEndpointToleranceUnits)
            {
                return false;
            }
        }

        return true;
    }

    private static bool FillBezierSegmentFollowsStroke(
        IReadOnlyList<CurveSample> samples,
        IReadOnlyList<PointF> stroke)
    {
        if (samples.Count < 2 || stroke.Count < 2) return false;
        for (var index = 0; index < samples.Count; index++)
        {
            if (DistanceToPolyline(samples[index].Point, stroke) > ConnectedStrokeEndpointToleranceUnits)
            {
                return false;
            }
        }

        return true;
    }

    internal bool TryGetExactFillBezierSegmentForBoundary(
        DrawingElementHit hit,
        int frame,
        out PathBezierSegmentPart segment)
    {
        segment = default;
        if (!hit.IsValid
            || hit.Key.Kind != DrawingElementKind.BoundaryStroke
            || (uint)hit.Key.ObjectIndex >= ObjectCount
            || !TryGetPathBezierWorldContours(hit.Key.ObjectIndex, out var exactContours))
        {
            return false;
        }

        BoundaryStrokePart? boundary = null;
        var candidates = CollectTopologyCandidates(hit.Key.ObjectIndex, frame);
        foreach (var part in BuildBoundaryStrokeParts(hit.Key.ObjectIndex, candidates))
        {
            if (part.PartIndex != hit.Key.PartIndex) continue;
            boundary = part;
            break;
        }

        if (boundary is null || boundary.Value.Points.Length < 2) return false;
        var selectedBoundary = boundary.Value;

        const float matchToleranceUnits = DrawingTopologyRules.MinStrokeSegmentUnits;
        var bestMatchedLength = 0f;
        var bestScore = float.MaxValue;
        foreach (var candidate in BuildPathBezierSegmentParts(exactContours))
        {
            if (candidate.ContourIndex != selectedBoundary.ContourIndex) continue;

            var score = 0f;
            var matchedLength = 0f;
            for (var pointIndex = 0; pointIndex < selectedBoundary.Points.Length - 1; pointIndex++)
            {
                var start = selectedBoundary.Points[pointIndex];
                var end = selectedBoundary.Points[pointIndex + 1];
                var length = Distance(start, end);
                if (length <= DrawingTopologyRules.UnitIntersectionTolerance) continue;
                var distance = DistanceToPolyline(Midpoint(start, end), candidate.Samples);
                if (distance > matchToleranceUnits) continue;
                matchedLength += length;
                score += distance * length;
            }

            if (matchedLength < bestMatchedLength - DrawingTopologyRules.UnitIntersectionTolerance
                || Math.Abs(matchedLength - bestMatchedLength) <= DrawingTopologyRules.UnitIntersectionTolerance
                && score >= bestScore)
            {
                continue;
            }

            bestMatchedLength = matchedLength;
            bestScore = score;
            segment = candidate;
        }

        return bestMatchedLength > DrawingTopologyRules.UnitIntersectionTolerance;
    }

    private static PathBezierSegmentPart[] BuildPathBezierSegmentParts(IReadOnlyList<PathBezierNode[]> contours)
    {

        var result = new List<PathBezierSegmentPart>(contours.Sum(contour => contour.Length));
        var partIndex = 0;
        for (var contourIndex = 0; contourIndex < contours.Count; contourIndex++)
        {
            var contour = contours[contourIndex];
            for (var segmentIndex = 0; segmentIndex < contour.Length; segmentIndex++)
            {
                var current = contour[segmentIndex];
                var next = contour[(segmentIndex + 1) % contour.Length];
                var curve = new CubicBoundarySegment(
                    current.Anchor,
                    current.OutgoingControl,
                    next.IncomingControl,
                    next.Anchor);
                result.Add(new PathBezierSegmentPart(
                    partIndex++,
                    contourIndex,
                    segmentIndex,
                    curve,
                    SampleCubicSegment(curve)));
            }
        }

        return result.ToArray();
    }

    public bool TryGetPathBezierSegment(int objectIndex, int partIndex, out PathBezierSegmentPart segment)
    {
        segment = default;
        if (partIndex < 0) return false;
        foreach (var candidate in GetPathBezierSegmentParts(objectIndex))
        {
            if (candidate.PartIndex != partIndex) continue;
            segment = candidate;
            return true;
        }

        return false;
    }

    private static DrawingTopologySplit RefineCubicTopologySplit(
        CubicBoundarySegment curve,
        DrawingTopologySplit split)
    {
        if (split.T <= 0.0001f) return new DrawingTopologySplit(0, VectorUnits.Quantize(curve.Start));
        if (split.T >= 0.9999f) return new DrawingTopologySplit(1, VectorUnits.Quantize(curve.End));

        var samples = SampleCubicSegmentWithParameters(curve);
        var low = Math.Max(0, split.T - 0.01f);
        var high = Math.Min(1, split.T + 0.01f);
        for (var index = 1; index < samples.Length; index++)
        {
            if (samples[index].T + 0.0001f < split.T) continue;
            low = samples[index - 1].T;
            high = samples[index].T;
            break;
        }

        for (var iteration = 0; iteration < 12; iteration++)
        {
            var first = low + (high - low) / 3f;
            var second = high - (high - low) / 3f;
            var firstPoint = CubicPoint(
                curve.Start,
                curve.Control1,
                curve.Control2,
                curve.End,
                first);
            var secondPoint = CubicPoint(
                curve.Start,
                curve.Control1,
                curve.Control2,
                curve.End,
                second);
            if (Distance(split.Point, firstPoint) <= Distance(split.Point, secondPoint)) high = second;
            else low = first;
        }

        var parameter = (low + high) * 0.5f;
        return new DrawingTopologySplit(
            parameter,
            VectorUnits.Quantize(CubicPoint(
                curve.Start,
                curve.Control1,
                curve.Control2,
                curve.End,
                parameter)));
    }

    public bool TryGetClosestPointOnPathBezierSegment(
        int objectIndex,
        int partIndex,
        PointF world,
        out float parameter,
        out PointF point,
        out float distance)
    {
        parameter = 0;
        point = PointF.Empty;
        distance = float.MaxValue;
        if (!TryGetPathBezierSegment(objectIndex, partIndex, out var segment)) return false;

        var curve = new CubicBoundarySegment(
            segment.Start,
            segment.Control1,
            segment.Control2,
            segment.End);
        var samples = SampleCubicSegmentWithParameters(curve);
        if (samples.Length < 2) return false;
        for (var index = 1; index < samples.Length; index++)
        {
            var a = samples[index - 1];
            var b = samples[index];
            var dx = b.Point.X - a.Point.X;
            var dy = b.Point.Y - a.Point.Y;
            var lengthSquared = dx * dx + dy * dy;
            var segmentParameter = lengthSquared <= DrawingTopologyRules.UnitIntersectionTolerance
                ? 0f
                : Math.Clamp(((world.X - a.Point.X) * dx + (world.Y - a.Point.Y) * dy) / lengthSquared, 0f, 1f);
            var candidateParameter = a.T + (b.T - a.T) * segmentParameter;
            var candidate = CubicPoint(
                segment.Start,
                segment.Control1,
                segment.Control2,
                segment.End,
                candidateParameter);
            var candidateDistance = Distance(world, candidate);
            if (candidateDistance >= distance) continue;
            parameter = candidateParameter;
            point = candidate;
            distance = candidateDistance;
        }

        var radius = 1f / Math.Max(8, samples.Length - 1);
        var low = Math.Max(0, parameter - radius);
        var high = Math.Min(1, parameter + radius);
        for (var iteration = 0; iteration < 10; iteration++)
        {
            var first = low + (high - low) / 3f;
            var second = high - (high - low) / 3f;
            var firstPoint = CubicPoint(
                segment.Start,
                segment.Control1,
                segment.Control2,
                segment.End,
                first);
            var secondPoint = CubicPoint(
                segment.Start,
                segment.Control1,
                segment.Control2,
                segment.End,
                second);
            if (Distance(world, firstPoint) <= Distance(world, secondPoint)) high = second;
            else low = first;
        }

        parameter = (low + high) * 0.5f;
        point = VectorUnits.Quantize(CubicPoint(
            segment.Start,
            segment.Control1,
            segment.Control2,
            segment.End,
            parameter));
        distance = Distance(world, point);
        return true;
    }

    public bool TryInsertPathBezierAnchor(
        int objectIndex,
        int partIndex,
        float parameter,
        out int insertedPartIndex,
        out PointF anchor,
        bool rebuildGeometryIndex = true)
    {
        insertedPartIndex = -1;
        anchor = PointF.Empty;
        if (!TryGetPathBezierWorldContours(objectIndex, out var contours)
            || partIndex < 0
            || parameter <= 0.0001f
            || parameter >= 0.9999f)
        {
            return false;
        }

        var currentPart = 0;
        for (var contourIndex = 0; contourIndex < contours.Length; contourIndex++)
        {
            var contour = contours[contourIndex];
            if (partIndex >= currentPart + contour.Length)
            {
                currentPart += contour.Length;
                continue;
            }

            var segmentIndex = partIndex - currentPart;
            var nextIndex = (segmentIndex + 1) % contour.Length;
            var current = contour[segmentIndex];
            var next = contour[nextIndex];
            var sourceIsStraight = IsStraightBezierSegment(
                current.Anchor,
                current.OutgoingControl,
                next.IncomingControl,
                next.Anchor);
            var p01 = VectorUnits.Quantize(Lerp(current.Anchor, current.OutgoingControl, parameter));
            var p12 = VectorUnits.Quantize(Lerp(current.OutgoingControl, next.IncomingControl, parameter));
            var p23 = VectorUnits.Quantize(Lerp(next.IncomingControl, next.Anchor, parameter));
            var p012 = VectorUnits.Quantize(Lerp(p01, p12, parameter));
            var p123 = VectorUnits.Quantize(Lerp(p12, p23, parameter));
            anchor = VectorUnits.Quantize(Lerp(p012, p123, parameter));

            if (sourceIsStraight)
            {
                anchor = VectorUnits.Quantize(CubicPoint(
                    current.Anchor,
                    current.OutgoingControl,
                    next.IncomingControl,
                    next.Anchor,
                    parameter));
                p01 = current.Anchor;
                p012 = anchor;
                p123 = anchor;
                p23 = next.Anchor;
            }

            var nodes = contour.ToList();
            nodes[segmentIndex] = current with { OutgoingControl = p01 };
            nodes[nextIndex] = next with { IncomingControl = p23 };
            nodes.Insert(segmentIndex + 1, new PathBezierNode(anchor, p012, p123));
            contours[contourIndex] = nodes.ToArray();
            if (!SetPathBezierContoursCore(objectIndex, contours)) return false;

            insertedPartIndex = partIndex + 1;
            CompletePathBezierMutation(rebuildGeometryIndex);
            return true;
        }

        return false;
    }

    public bool TryMaterializePathBezierSegmentInterval(
        int objectIndex,
        int sourcePartIndex,
        float startT,
        float endT,
        out int materializedPartIndex,
        bool rebuildGeometryIndex = true)
    {
        materializedPartIndex = -1;
        startT = Math.Clamp(startT, 0, 1);
        endT = Math.Clamp(endT, 0, 1);
        if (endT - startT <= 0.0001f
            || !TryGetPathBezierSegment(objectIndex, sourcePartIndex, out _))
        {
            return false;
        }

        var changed = false;
        if (endT < 0.9999f)
        {
            if (!TryInsertPathBezierAnchor(
                    objectIndex,
                    sourcePartIndex,
                    endT,
                    out _,
                    out _,
                    rebuildGeometryIndex: false))
            {
                return false;
            }

            changed = true;
        }

        materializedPartIndex = sourcePartIndex;
        if (startT > 0.0001f)
        {
            var localStartT = endT < 0.9999f ? startT / endT : startT;
            if (!TryInsertPathBezierAnchor(
                    objectIndex,
                    sourcePartIndex,
                    localStartT,
                    out materializedPartIndex,
                    out _,
                    rebuildGeometryIndex: false))
            {
                return false;
            }

            changed = true;
        }

        if (changed)
        {
            CompletePathBezierMutation(rebuildGeometryIndex);
        }

        return TryGetPathBezierSegment(objectIndex, materializedPartIndex, out _);
    }

    public bool TryMaterializePathBezierSegmentNeighborhood(
        int objectIndex,
        FillBezierSegmentPiece activePiece,
        bool startEndpoint,
        IReadOnlyList<FillBezierSegmentPiece> visiblePieces,
        out int activePartIndex,
        bool rebuildGeometryIndex = true)
    {
        activePartIndex = -1;
        if ((uint)objectIndex >= ObjectCount) return false;

        var activeAnchor = startEndpoint ? activePiece.Start : activePiece.End;
        var activeAnchorIsVirtual = startEndpoint
            ? activePiece.StartIsVirtualAnchor
            : activePiece.EndIsVirtualAnchor;
        var intervals = new List<FillBezierSegmentPiece> { activePiece };
        if (!activeAnchorIsVirtual)
        {
            foreach (var piece in visiblePieces)
            {
                if (piece.PieceIndex == activePiece.PieceIndex
                    || piece.ContourIndex != activePiece.ContourIndex)
                {
                    continue;
                }

                var sharesDurableStart = !piece.StartIsVirtualAnchor
                    && Distance(piece.Start, activeAnchor) <= ConnectedStrokeEndpointToleranceUnits;
                var sharesDurableEnd = !piece.EndIsVirtualAnchor
                    && Distance(piece.End, activeAnchor) <= ConnectedStrokeEndpointToleranceUnits;
                if (!sharesDurableStart && !sharesDurableEnd) continue;
                intervals.Add(piece);
            }
        }

        var activeMaterialized = false;
        foreach (var interval in intervals
                     .GroupBy(piece => piece.SourcePartIndex)
                     .Select(group => group.First())
                     .OrderByDescending(piece => piece.SourcePartIndex))
        {
            var partCountBefore = GetPathBezierSegmentParts(objectIndex).Length;
            if (!TryMaterializePathBezierSegmentInterval(
                    objectIndex,
                    interval.SourcePartIndex,
                    interval.StartT,
                    interval.EndT,
                    out var materializedPartIndex,
                    rebuildGeometryIndex: false))
            {
                return false;
            }

            if (interval.SourcePartIndex == activePiece.SourcePartIndex)
            {
                activePartIndex = materializedPartIndex;
                activeMaterialized = true;
            }
            else if (activeMaterialized)
            {
                activePartIndex += GetPathBezierSegmentParts(objectIndex).Length - partCountBefore;
            }
        }

        if (!activeMaterialized
            || !TryGetPathBezierSegment(objectIndex, activePartIndex, out _))
        {
            return false;
        }

        CompletePathBezierMutation(rebuildGeometryIndex);
        return true;
    }

    public bool TryDeletePathBezierAnchor(
        int objectIndex,
        int partIndex,
        bool startEndpoint,
        out int remainingPartIndex,
        bool rebuildGeometryIndex = true)
    {
        remainingPartIndex = -1;
        if (!TryGetPathBezierWorldContours(objectIndex, out var contours) || partIndex < 0) return false;

        var currentPart = 0;
        for (var contourIndex = 0; contourIndex < contours.Length; contourIndex++)
        {
            var contour = contours[contourIndex];
            if (partIndex >= currentPart + contour.Length)
            {
                currentPart += contour.Length;
                continue;
            }

            if (contour.Length <= 3) return false;
            var segmentIndex = partIndex - currentPart;
            var targetIndex = startEndpoint
                ? segmentIndex
                : (segmentIndex + 1) % contour.Length;
            var previousIndex = (targetIndex - 1 + contour.Length) % contour.Length;
            var nextIndex = (targetIndex + 1) % contour.Length;
            var previous = contour[previousIndex];
            var target = contour[targetIndex];
            var next = contour[nextIndex];
            var joinsStraightSegments = IsStraightBezierSegment(
                    previous.Anchor,
                    previous.OutgoingControl,
                    target.IncomingControl,
                    target.Anchor)
                && IsStraightBezierSegment(
                    target.Anchor,
                    target.OutgoingControl,
                    next.IncomingControl,
                    next.Anchor);

            var nodes = contour.ToList();
            if (joinsStraightSegments)
            {
                nodes[previousIndex] = previous with
                {
                    OutgoingControl = VectorUnits.Quantize(Lerp(previous.Anchor, next.Anchor, 1f / 3f))
                };
                nodes[nextIndex] = next with
                {
                    IncomingControl = VectorUnits.Quantize(Lerp(previous.Anchor, next.Anchor, 2f / 3f))
                };
            }

            nodes.RemoveAt(targetIndex);
            contours[contourIndex] = nodes.ToArray();
            if (!SetPathBezierContoursCore(objectIndex, contours)) return false;

            var newPreviousIndex = targetIndex == 0 ? contour.Length - 2 : targetIndex - 1;
            remainingPartIndex = currentPart + newPreviousIndex;
            CompletePathBezierMutation(rebuildGeometryIndex);
            return true;
        }

        return false;
    }

    public bool TryConvertFillToBezierPath(int objectIndex, bool rebuildGeometryIndex = true)
    {
        if ((uint)objectIndex >= ObjectCount || !IsFillShape(ShapeKind[objectIndex])) return false;
        if (ShapeKind[objectIndex] == VectorAnimationEngine.ShapeKind.Path
            && _pathBezierLocalContours.ContainsKey(objectIndex))
        {
            return true;
        }

        PathBezierNode[][] contours;
        if (ShapeKind[objectIndex] == VectorAnimationEngine.ShapeKind.Path)
        {
            if (!TryGetPathWorldContours(objectIndex, out var polygonContours)) return false;
            contours = polygonContours.Select(CreateLinearBezierContour).ToArray();
        }
        else if (ShapeKind[objectIndex] == VectorAnimationEngine.ShapeKind.Ellipse)
        {
            contours = [CreateBezierContour(EllipseBoundaryCurves(objectIndex))];
        }
        else
        {
            var boundary = OpenPolygon(ShapeBoundary(objectIndex));
            if (boundary.Length < 3) return false;
            contours = [CreateLinearBezierContour(boundary)];
        }

        if (!SetPathBezierContoursCore(objectIndex, contours)) return false;
        CompletePathBezierMutation(rebuildGeometryIndex);
        return true;
    }

    public bool SetPathBezierSegment(
        int objectIndex,
        int partIndex,
        PointF start,
        PointF control1,
        PointF control2,
        PointF end,
        bool rebuildGeometryIndex = true,
        bool preserveStraightAdjacentSegments = false)
    {
        if (!TryGetPathBezierWorldContours(objectIndex, out var contours) || partIndex < 0) return false;

        var currentPart = 0;
        for (var contourIndex = 0; contourIndex < contours.Length; contourIndex++)
        {
            var contour = contours[contourIndex];
            if (partIndex >= currentPart + contour.Length)
            {
                currentPart += contour.Length;
                continue;
            }

            var segmentIndex = partIndex - currentPart;
            var nextIndex = (segmentIndex + 1) % contour.Length;
            var previousIndex = (segmentIndex - 1 + contour.Length) % contour.Length;
            var followingIndex = (nextIndex + 1) % contour.Length;
            var current = contour[segmentIndex];
            var next = contour[nextIndex];
            start = VectorUnits.Quantize(start);
            control1 = VectorUnits.Quantize(control1);
            control2 = VectorUnits.Quantize(control2);
            end = VectorUnits.Quantize(end);
            var startChanged = start != current.Anchor;
            var endChanged = end != next.Anchor;
            if (!startChanged
                && control1 == current.OutgoingControl
                && control2 == next.IncomingControl
                && !endChanged)
            {
                return true;
            }

            var preservePrevious = preserveStraightAdjacentSegments
                && startChanged
                && IsStraightBezierSegment(
                    contour[previousIndex].Anchor,
                    contour[previousIndex].OutgoingControl,
                    current.IncomingControl,
                    current.Anchor);
            var preserveFollowing = preserveStraightAdjacentSegments
                && endChanged
                && IsStraightBezierSegment(
                    next.Anchor,
                    next.OutgoingControl,
                    contour[followingIndex].IncomingControl,
                    contour[followingIndex].Anchor);
            var startDelta = new PointF(start.X - current.Anchor.X, start.Y - current.Anchor.Y);
            var endDelta = new PointF(end.X - next.Anchor.X, end.Y - next.Anchor.Y);
            contour[segmentIndex] = current with
            {
                Anchor = start,
                IncomingControl = startChanged
                    ? VectorUnits.Quantize(new PointF(
                        current.IncomingControl.X + startDelta.X,
                        current.IncomingControl.Y + startDelta.Y))
                    : current.IncomingControl,
                OutgoingControl = control1
            };
            contour[nextIndex] = next with
            {
                Anchor = end,
                IncomingControl = control2,
                OutgoingControl = endChanged
                    ? VectorUnits.Quantize(new PointF(
                        next.OutgoingControl.X + endDelta.X,
                        next.OutgoingControl.Y + endDelta.Y))
                    : next.OutgoingControl
            };
            if (preservePrevious)
            {
                var previousAnchor = contour[previousIndex].Anchor;
                contour[previousIndex] = contour[previousIndex] with
                {
                    OutgoingControl = VectorUnits.Quantize(Lerp(previousAnchor, start, 1f / 3f))
                };
                contour[segmentIndex] = contour[segmentIndex] with
                {
                    IncomingControl = VectorUnits.Quantize(Lerp(previousAnchor, start, 2f / 3f))
                };
            }

            if (preserveFollowing)
            {
                var followingAnchor = contour[followingIndex].Anchor;
                contour[nextIndex] = contour[nextIndex] with
                {
                    OutgoingControl = VectorUnits.Quantize(Lerp(end, followingAnchor, 1f / 3f))
                };
                contour[followingIndex] = contour[followingIndex] with
                {
                    IncomingControl = VectorUnits.Quantize(Lerp(end, followingAnchor, 2f / 3f))
                };
            }

            if (!SetPathBezierContoursCore(objectIndex, contours)) return false;
            CompletePathBezierMutation(rebuildGeometryIndex);
            return true;
        }

        return false;
    }

    internal bool SetPathBezierSegmentForPreview(
        int objectIndex,
        int partIndex,
        PointF start,
        PointF control1,
        PointF control2,
        PointF end,
        bool preserveStraightAdjacentSegments = false,
        EditHandleKind handle = EditHandleKind.BezierControl)
    {
        if ((uint)objectIndex >= ObjectCount
            || partIndex < 0
            || !_pathBezierLocalContours.TryGetValue(objectIndex, out var contours))
        {
            return false;
        }

        var currentPart = 0;
        for (var contourIndex = 0; contourIndex < contours.Length; contourIndex++)
        {
            var contour = contours[contourIndex];
            if (partIndex >= currentPart + contour.Length)
            {
                currentPart += contour.Length;
                continue;
            }

            var segmentIndex = partIndex - currentPart;
            var nextIndex = (segmentIndex + 1) % contour.Length;
            var previousIndex = (segmentIndex - 1 + contour.Length) % contour.Length;
            var followingIndex = (nextIndex + 1) % contour.Length;
            var current = contour[segmentIndex];
            var next = contour[nextIndex];
            start = VectorUnits.Quantize(WorldToLocal(objectIndex, VectorUnits.Quantize(start)));
            control1 = VectorUnits.Quantize(WorldToLocal(objectIndex, VectorUnits.Quantize(control1)));
            control2 = VectorUnits.Quantize(WorldToLocal(objectIndex, VectorUnits.Quantize(control2)));
            end = VectorUnits.Quantize(WorldToLocal(objectIndex, VectorUnits.Quantize(end)));
            var startChanged = start != current.Anchor;
            var endChanged = end != next.Anchor;
            if (!startChanged
                && control1 == current.OutgoingControl
                && control2 == next.IncomingControl
                && !endChanged)
            {
                return true;
            }

            var preservePrevious = preserveStraightAdjacentSegments
                && startChanged
                && IsStraightBezierSegment(
                    contour[previousIndex].Anchor,
                    contour[previousIndex].OutgoingControl,
                    current.IncomingControl,
                    current.Anchor);
            var preserveFollowing = preserveStraightAdjacentSegments
                && endChanged
                && IsStraightBezierSegment(
                    next.Anchor,
                    next.OutgoingControl,
                    contour[followingIndex].IncomingControl,
                    contour[followingIndex].Anchor);
            var startDelta = new PointF(start.X - current.Anchor.X, start.Y - current.Anchor.Y);
            var endDelta = new PointF(end.X - next.Anchor.X, end.Y - next.Anchor.Y);
            contour[segmentIndex] = current with
            {
                Anchor = start,
                IncomingControl = startChanged
                    ? VectorUnits.Quantize(new PointF(
                        current.IncomingControl.X + startDelta.X,
                        current.IncomingControl.Y + startDelta.Y))
                    : current.IncomingControl,
                OutgoingControl = control1
            };
            contour[nextIndex] = next with
            {
                Anchor = end,
                IncomingControl = control2,
                OutgoingControl = endChanged
                    ? VectorUnits.Quantize(new PointF(
                        next.OutgoingControl.X + endDelta.X,
                        next.OutgoingControl.Y + endDelta.Y))
                    : next.OutgoingControl
            };
            if (preservePrevious)
            {
                var previousAnchor = contour[previousIndex].Anchor;
                contour[previousIndex] = contour[previousIndex] with
                {
                    OutgoingControl = VectorUnits.Quantize(Lerp(previousAnchor, start, 1f / 3f))
                };
                contour[segmentIndex] = contour[segmentIndex] with
                {
                    IncomingControl = VectorUnits.Quantize(Lerp(previousAnchor, start, 2f / 3f))
                };
            }

            if (preserveFollowing)
            {
                var followingAnchor = contour[followingIndex].Anchor;
                contour[nextIndex] = contour[nextIndex] with
                {
                    OutgoingControl = VectorUnits.Quantize(Lerp(end, followingAnchor, 1f / 3f))
                };
                contour[followingIndex] = contour[followingIndex] with
                {
                    IncomingControl = VectorUnits.Quantize(Lerp(end, followingAnchor, 2f / 3f))
                };
            }

            // Deep-copy every contour so renderer caches keyed on array
            // identity always observe the edit.
            _pathBezierLocalContours[objectIndex] = contours
                .Select(c => (PathBezierNode[])c.Clone())
                .ToArray();
            if (handle is EditHandleKind.BezierControl or EditHandleKind.BezierControl2)
            {
                // Curvature edits reshape the filled region, so keep the fill
                // contour in sync with the bent edge.
                ResamplePathLocalContours(objectIndex);
            }
            GeometryRevision++;
            InvalidateDeferredTopologyQueries();
            return true;
        }

        return false;
    }

    internal bool CompletePathBezierPreview(int objectIndex, bool rebuildGeometryIndex = true)
    {
        if (!TryGetPathBezierWorldContours(objectIndex, out var contours)
            || !SetPathBezierContoursCore(objectIndex, contours))
        {
            return false;
        }

        CompletePathBezierMutation(rebuildGeometryIndex);
        return true;
    }

    /// <summary>
    /// Pulls a single boundary segment out of a fill shape as an independent, open
    /// stroke object (a Line) that inherits the source stroke style. The source
    /// contour is left closed and untouched — the fill keeps the pre-detach outline
    /// (including any bend) — and the segment's stroke is hidden on the source so
    /// every detached edge leaves its own exposed gap. The pulled piece is marked
    /// fill-boundary-detached so it never drags the fill along afterwards.
    /// </summary>
    internal bool SplitOutSegmentAsNewObject(int objectIndex, int partIndex, out int newObjectIndex)
    {
        newObjectIndex = -1;
        if (!TryConvertFillToBezierPath(objectIndex, rebuildGeometryIndex: false)) return false;
        if (!TryGetPathBezierWorldContours(objectIndex, out var contours)) return false;

        var currentPart = 0;
        for (var contourIndex = 0; contourIndex < contours.Length; contourIndex++)
        {
            var contour = contours[contourIndex];
            if (partIndex >= currentPart + contour.Length)
            {
                currentPart += contour.Length;
                continue;
            }

            if (contour.Length < 2 || (uint)partIndex >= currentPart + contour.Length) return false;

            var segmentIndex = partIndex - currentPart;
            var nextIndex = (segmentIndex + 1) % contour.Length;
            var anchor = contour[segmentIndex];
            var next = contour[nextIndex];
            var start = anchor.Anchor;
            var control1 = anchor.OutgoingControl;
            var control2 = next.IncomingControl;
            var end = next.Anchor;

            // 1) Spawn the pulled segment as a standalone, open Line.
            var layer = ObjectLayer[objectIndex];
            var atoms = AtomCount[objectIndex];
            var strokeWidth = Stroke[objectIndex];
            var strokeColor = Color.FromArgb(StrokeArgb[objectIndex]);
            if (!HasStroke(objectIndex))
            {
                // A fill-only source has no stroke to inherit, and a zero-width transparent
                // line would be invisible and impossible to grab. Fall back to the standard
                // 2pt default so the pulled segment stays visible and draggable.
                strokeWidth = VectorUnits.StrokePointsToUnits(2f);
                strokeColor = Color.Black;
            }

            var created = AddCubicCurveSegment(
                layer,
                start,
                control1,
                control2,
                end,
                strokeWidth,
                Color.Transparent,
                strokeColor,
                atoms,
                LineEndpointStyle.Sharp,
                LineEndpointStyle.Sharp);
            if (created < 0) return false;

            ObjectKeyframeFrame[created] = ObjectKeyframeFrame[objectIndex];
            ObjectOrder[created] = ObjectOrder[objectIndex];
            ObjectSubOrder[created] = ObjectSubOrder[objectIndex];
            // The pulled segment starts coincident with the source boundary; mark it so the
            // fill-boundary link capture skips it and it stays fully independent (Animate
            // behavior: reshaping the fill afterwards never drags the torn piece along).
            FillBoundaryLinkDetached[created] = true;
            newObjectIndex = created;

            // 2) Record this boundary segment as detached (always — the bookkeeping also
            //    prevents detaching the same segment twice) and suppress the source's
            //    stroke for it. The contour stays closed so the fill keeps its previous
            //    outline and multiple gaps coexist.
            HideBoundaryStrokePart(objectIndex, partIndex);

            // When the last boundary segment has been pulled out there is no visible
            // stroke left on the source: promote it to a pure fill (Stroke = 0) so the
            // boundary stops rendering/hit-testing as a stroke. The detached-segment
            // bookkeeping is kept — it is what stops duplicate detaches afterwards.
            if (HasStroke(objectIndex)
                && _hiddenBoundaryStrokeParts.TryGetValue(objectIndex, out var hiddenNow)
                && TryGetPathBezierWorldContours(objectIndex, out var promoteContours)
                && hiddenNow.Count >= promoteContours.Sum(contour => contour.Length))
            {
                Stroke[objectIndex] = 0;
            }

            CompletePathBezierMutation(rebuildGeometryIndex: true);
            return true;
        }

        return false;
    }

    internal bool NormalizeFillBoundaryOverlaps(
        int objectIndex,
        IReadOnlyList<PointF[]> referenceContours,
        bool rebuildGeometryIndex = true)
    {
        if ((uint)objectIndex >= ObjectCount
            || ShapeKind[objectIndex] != VectorAnimationEngine.ShapeKind.Path
            || !HasFill(objectIndex)
            || referenceContours.Count == 0
            || !TryGetPathBezierWorldContours(objectIndex, out var sourceBezierContours)
            || !TryGetPathWorldContours(objectIndex, out var currentContours)
            || currentContours.Length != referenceContours.Count
            || !TryBuildNormalizedFillBoundaryPath(currentContours, referenceContours, out var normalizedContours)
            || !TryRebuildBezierContoursFromBooleanBoundary(
                sourceBezierContours,
                normalizedContours,
                out var normalizedBezierContours)
            || !SetPathBezierContoursCore(objectIndex, normalizedBezierContours))
        {
            return false;
        }

        CompletePathBezierMutation(rebuildGeometryIndex);
        return true;
    }

    internal static bool IsStraightBezierSegment(
        PointF start,
        PointF control1,
        PointF control2,
        PointF end)
    {
        return DistanceToSegment(control1, start, end) <= DrawingTopologyRules.MinStrokeSegmentUnits
            && DistanceToSegment(control2, start, end) <= DrawingTopologyRules.MinStrokeSegmentUnits;
    }

    public bool TryGetLineEndpoint(int objectIndex, bool startEndpoint, out PointF point)
    {
        point = PointF.Empty;
        if ((uint)objectIndex >= ObjectCount || ShapeKind[objectIndex] != VectorAnimationEngine.ShapeKind.Line) return false;
        var halfW = Width[objectIndex] * 0.5f;
        point = LocalToWorld(objectIndex, startEndpoint ? -halfW : halfW, 0);
        return true;
    }

    public bool TryGetLineJoinNeighbor(
        int objectIndex,
        bool startEndpoint,
        int frame,
        out int neighborObjectIndex,
        out bool neighborStartEndpoint)
    {
        neighborObjectIndex = -1;
        neighborStartEndpoint = false;
        if (!TryGetLineEndpointJunction(objectIndex, startEndpoint, frame, out var junction)
            || junction.NeighborCount != 1)
        {
            return false;
        }

        neighborObjectIndex = junction.FirstNeighborObjectIndex;
        neighborStartEndpoint = junction.FirstNeighborStartEndpoint;
        return true;
    }

    internal bool TryGetLineEndpointJunction(
        int objectIndex,
        bool startEndpoint,
        int frame,
        out LineEndpointJunction junction)
    {
        return TryGetLineEndpointJunction(
            objectIndex,
            startEndpoint,
            frame,
            stopWhenNonOwner: false,
            out junction);
    }

    internal bool TryGetLineEndpointJunctionForRender(
        int objectIndex,
        bool startEndpoint,
        int frame,
        out LineEndpointJunction junction)
    {
        return TryGetLineEndpointJunction(
            objectIndex,
            startEndpoint,
            frame,
            stopWhenNonOwner: true,
            out junction);
    }

    private bool TryGetLineEndpointJunction(
        int objectIndex,
        bool startEndpoint,
        int frame,
        bool stopWhenNonOwner,
        out LineEndpointJunction junction)
    {
        junction = default;
        if ((uint)objectIndex >= ObjectCount
            || ShapeKind[objectIndex] != VectorAnimationEngine.ShapeKind.Line
            || !IsObjectActive(objectIndex, frame)
            || !TryGetLineEndpoint(objectIndex, startEndpoint, out var endpoint))
        {
            return false;
        }

        var tolerance = ConnectedStrokeEndpointToleranceUnits;
        var bounds = RectangleF.FromLTRB(
            endpoint.X - tolerance,
            endpoint.Y - tolerance,
            endpoint.X + tolerance,
            endpoint.Y + tolerance);
        var neighborCount = 0;
        var ownerObjectIndex = objectIndex;
        var firstNeighborObjectIndex = -1;
        var firstNeighborStartEndpoint = false;
        var allSharp = GetLineEndpointStyle(objectIndex, startEndpoint) == LineEndpointStyle.Sharp;
        List<LineEndpointConnection>? connections = null;
        foreach (var candidate in QueryObjects(bounds, frame))
        {
            if (candidate == objectIndex
                || ShapeKind[candidate] != VectorAnimationEngine.ShapeKind.Line
                || ObjectLayer[candidate] != ObjectLayer[objectIndex]
                || ObjectKeyframeFrame[candidate] != ObjectKeyframeFrame[objectIndex]
                || StrokeArgb[candidate] != StrokeArgb[objectIndex]
                || Math.Abs(Stroke[candidate] - Stroke[objectIndex]) > 0.001f)
            {
                continue;
            }

            var connectedAtStart = TryGetLineEndpoint(candidate, startEndpoint: true, out var candidateStart)
                && Distance(candidateStart, endpoint) <= tolerance;
            var connectedAtEnd = !connectedAtStart
                && TryGetLineEndpoint(candidate, startEndpoint: false, out var candidateEnd)
                && Distance(candidateEnd, endpoint) <= tolerance;
            if (!connectedAtStart && !connectedAtEnd) continue;

            var candidateSharp = GetLineEndpointStyle(candidate, connectedAtStart) == LineEndpointStyle.Sharp;
            if (stopWhenNonOwner && candidate < objectIndex)
            {
                junction = new LineEndpointJunction(
                    1,
                    candidate,
                    candidate,
                    connectedAtStart,
                    allSharp && candidateSharp,
                    Array.Empty<LineEndpointConnection>());
                return true;
            }

            if (firstNeighborObjectIndex < 0)
            {
                firstNeighborObjectIndex = candidate;
                firstNeighborStartEndpoint = connectedAtStart;
            }

            neighborCount++;
            connections ??= new List<LineEndpointConnection>(2);
            connections.Add(new LineEndpointConnection(candidate, connectedAtStart));
            ownerObjectIndex = Math.Min(ownerObjectIndex, candidate);
            allSharp &= candidateSharp;
        }

        if (neighborCount == 0) return false;
        junction = new LineEndpointJunction(
            neighborCount,
            ownerObjectIndex,
            firstNeighborObjectIndex,
            firstNeighborStartEndpoint,
            allSharp,
            ownerObjectIndex == objectIndex ? connections!.ToArray() : Array.Empty<LineEndpointConnection>());
        return true;
    }

    public bool TryGetLineBezierPart(
        int objectIndex,
        float startT,
        float endT,
        out PointF start,
        out PointF control1,
        out PointF control2,
        out PointF end)
    {
        start = PointF.Empty;
        control1 = PointF.Empty;
        control2 = PointF.Empty;
        end = PointF.Empty;
        if ((uint)objectIndex >= ObjectCount || ShapeKind[objectIndex] != VectorAnimationEngine.ShapeKind.Line) return false;

        var curve = LineCurve(objectIndex);
        (start, control1, control2, end) = CubicSubcurve(
            curve.Start,
            curve.Control1,
            curve.Control2,
            curve.End,
            startT,
            endT);
        return true;
    }

    public bool TryGetClosestPointOnLine(
        int objectIndex,
        PointF world,
        out float parameter,
        out PointF point,
        out float distance)
    {
        parameter = 0;
        point = PointF.Empty;
        distance = float.MaxValue;
        if ((uint)objectIndex >= ObjectCount || ShapeKind[objectIndex] != VectorAnimationEngine.ShapeKind.Line) return false;

        var curve = LineCurve(objectIndex);
        var samples = CurveSamples(objectIndex);
        if (samples.Length < 2) return false;
        for (var index = 1; index < samples.Length; index++)
        {
            var a = samples[index - 1];
            var b = samples[index];
            var dx = b.Point.X - a.Point.X;
            var dy = b.Point.Y - a.Point.Y;
            var lengthSquared = dx * dx + dy * dy;
            var segmentParameter = lengthSquared <= DrawingTopologyRules.UnitIntersectionTolerance
                ? 0f
                : Math.Clamp(((world.X - a.Point.X) * dx + (world.Y - a.Point.Y) * dy) / lengthSquared, 0f, 1f);
            var candidateParameter = a.T + (b.T - a.T) * segmentParameter;
            var candidate = CubicPoint(curve.Start, curve.Control1, curve.Control2, curve.End, candidateParameter);
            var candidateDistance = Distance(world, candidate);
            if (candidateDistance >= distance) continue;
            parameter = candidateParameter;
            point = candidate;
            distance = candidateDistance;
        }

        var radius = 1f / Math.Max(8, samples.Length - 1);
        var low = Math.Max(0, parameter - radius);
        var high = Math.Min(1, parameter + radius);
        const int closestPointRefinementIterations = 24;
        for (var iteration = 0; iteration < closestPointRefinementIterations; iteration++)
        {
            var first = low + (high - low) / 3f;
            var second = high - (high - low) / 3f;
            var firstPoint = CubicPoint(curve.Start, curve.Control1, curve.Control2, curve.End, first);
            var secondPoint = CubicPoint(curve.Start, curve.Control1, curve.Control2, curve.End, second);
            if (Distance(world, firstPoint) <= Distance(world, secondPoint)) high = second;
            else low = first;
        }

        parameter = (low + high) * 0.5f;
        point = CubicPoint(curve.Start, curve.Control1, curve.Control2, curve.End, parameter);
        distance = Distance(world, point);
        var startDistance = Distance(world, curve.Start);
        if (startDistance <= distance)
        {
            parameter = 0;
            point = curve.Start;
            distance = startDistance;
        }
        var endDistance = Distance(world, curve.End);
        if (endDistance < distance)
        {
            parameter = 1;
            point = curve.End;
            distance = endDistance;
        }
        return true;
    }

    public bool SplitLineAt(
        int objectIndex,
        float parameter,
        out LineAnchorSplitResult result,
        bool rebuildGeometryIndex = true)
    {
        result = default;
        if ((uint)objectIndex >= ObjectCount
            || ShapeKind[objectIndex] != VectorAnimationEngine.ShapeKind.Line
            || parameter <= 0.001f
            || parameter >= 0.999f)
        {
            return false;
        }

        var curve = LineCurve(objectIndex);
        var p01 = Lerp(curve.Start, curve.Control1, parameter);
        var p12 = Lerp(curve.Control1, curve.Control2, parameter);
        var p23 = Lerp(curve.Control2, curve.End, parameter);
        var p012 = Lerp(p01, p12, parameter);
        var p123 = Lerp(p12, p23, parameter);
        var anchor = VectorUnits.Quantize(Lerp(p012, p123, parameter));
        p01 = VectorUnits.Quantize(p01);
        p012 = VectorUnits.Quantize(p012);
        p123 = VectorUnits.Quantize(p123);
        p23 = VectorUnits.Quantize(p23);
        if (Distance(curve.Start, anchor) < DrawingTopologyRules.MinStrokeSegmentUnits
            || Distance(anchor, curve.End) < DrawingTopologyRules.MinStrokeSegmentUnits)
        {
            return false;
        }

        var layer = ObjectLayer[objectIndex];
        var keyframeFrame = ObjectKeyframeFrame[objectIndex];
        var order = ObjectOrder[objectIndex];
        var stroke = Stroke[objectIndex];
        var fillColor = Color.FromArgb(Argb[objectIndex]);
        var strokeColor = Color.FromArgb(StrokeArgb[objectIndex]);
        var startStyle = GetLineEndpointStyle(objectIndex, startEndpoint: true);
        var endStyle = GetLineEndpointStyle(objectIndex, startEndpoint: false);
        var gradientPaint = CaptureGradientPaint(objectIndex);
        var secondSubOrder = ReplacementSubOrders(
            objectIndex,
            1,
            preserveSourceSubOrder: true)[0];
        var sourceAtoms = Math.Max(6u, AtomCount[objectIndex]);
        var firstAtoms = Math.Max(3u, (uint)Math.Round(sourceAtoms * parameter));
        var secondAtoms = Math.Max(3u, sourceAtoms - Math.Min(sourceAtoms - 3u, firstAtoms));
        if (firstAtoms + secondAtoms != sourceAtoms)
        {
            firstAtoms = Math.Max(3u, sourceAtoms / 2u);
            secondAtoms = Math.Max(3u, sourceAtoms - firstAtoms);
        }

        var snapshot = CreateSnapshot();
        try
        {
            SetLineCurve(objectIndex, curve.Start, p01, p012, anchor);
            LineEndpointStyles[objectIndex] = startStyle;
            LineEndEndpointStyles[objectIndex] = LineEndpointStyle.Round;
            VirtualAtomCount -= AtomCount[objectIndex];
            AtomCount[objectIndex] = firstAtoms;
            VirtualAtomCount += firstAtoms;

            var secondObject = AppendCubicCurveSegment(
                layer,
                anchor,
                p123,
                p23,
                curve.End,
                stroke,
                fillColor,
                strokeColor,
                secondAtoms,
                LineEndpointStyle.Round,
                endStyle);
            if (secondObject < 0) throw new InvalidOperationException("The second line segment was invalid.");
            ObjectLayer[secondObject] = layer;
            ObjectKeyframeFrame[secondObject] = keyframeFrame;
            ObjectOrder[secondObject] = order;
            ObjectSubOrder[secondObject] = secondSubOrder;
            ApplyGradientPaint(objectIndex, gradientPaint);
            ApplyGradientPaint(secondObject, gradientPaint);
            if (rebuildGeometryIndex)
            {
                RebuildGeometryIndex();
                RebuildSummaries();
            }
            else
            {
                GeometryRevision++;
                InvalidateDeferredTopologyQueries();
            }
            result = new LineAnchorSplitResult(objectIndex, secondObject, anchor);
            return true;
        }
        catch
        {
            RestoreSnapshot(snapshot);
            result = default;
            return false;
        }
    }

    /// <summary>
    /// Converts a straight Line into an open 3-node Freeform stroke split at
    /// <paramref name="parameter"/>. The middle node is a corner anchor whose
    /// incoming/outgoing controls are independent, producing a sharp point.
    /// </summary>
    public bool TryConvertLineToBezierFreeform(int objectIndex, float parameter, out PointF cornerAnchor)
    {
        cornerAnchor = PointF.Empty;
        if ((uint)objectIndex >= ObjectCount
            || ShapeKind[objectIndex] != VectorAnimationEngine.ShapeKind.Line
            || parameter <= 0.001f
            || parameter >= 0.999f)
        {
            return false;
        }

        var curve = LineCurve(objectIndex);
        var p01 = Lerp(curve.Start, curve.Control1, parameter);
        var p12 = Lerp(curve.Control1, curve.Control2, parameter);
        var p23 = Lerp(curve.Control2, curve.End, parameter);
        var p012 = Lerp(p01, p12, parameter);
        var p123 = Lerp(p12, p23, parameter);
        var anchor = VectorUnits.Quantize(Lerp(p012, p123, parameter));
        p01 = VectorUnits.Quantize(p01);
        p012 = VectorUnits.Quantize(p012);
        p123 = VectorUnits.Quantize(p123);
        p23 = VectorUnits.Quantize(p23);
        if (Distance(curve.Start, anchor) < DrawingTopologyRules.MinStrokeSegmentUnits
            || Distance(anchor, curve.End) < DrawingTopologyRules.MinStrokeSegmentUnits)
        {
            return false;
        }

        var nodes = new[]
        {
            new PathBezierNode(curve.Start, curve.Start, p01),
            new PathBezierNode(anchor, p012, p123),
            new PathBezierNode(curve.End, p23, curve.End)
        };

        var snapshot = CreateSnapshot();
        try
        {
            ShapeKind[objectIndex] = VectorAnimationEngine.ShapeKind.Freeform;
            LineEndpointStyles[objectIndex] = LineEndpointStyle.Round;
            LineEndEndpointStyles[objectIndex] = LineEndpointStyle.Round;
            if (!SetFreehandBezierNodesCore(objectIndex, nodes))
            {
                throw new InvalidOperationException("The corner freeform conversion failed.");
            }

            RebuildGeometryIndex();
            RebuildSummaries();
            cornerAnchor = anchor;
            return true;
        }
        catch
        {
            RestoreSnapshot(snapshot);
            cornerAnchor = PointF.Empty;
            return false;
        }
    }

    public bool TrySetFreehandBezierWorldNodes(int objectIndex, IReadOnlyList<PathBezierNode> worldNodes)
    {
        if ((uint)objectIndex >= ObjectCount
            || ShapeKind[objectIndex] != VectorAnimationEngine.ShapeKind.Freeform
            || worldNodes.Count < 2)
        {
            return false;
        }

        return SetFreehandBezierNodesCore(objectIndex, worldNodes);
    }

    /// <summary>
    /// Splits an open Freeform segment with de Casteljau and inserts a corner
    /// anchor (independent in/out controls) at <paramref name="parameter"/>.
    /// </summary>
    public bool TryInsertFreehandBezierCorner(int objectIndex, int segmentIndex, float parameter, out int insertedNodeIndex)
    {
        insertedNodeIndex = -1;
        if ((uint)objectIndex >= ObjectCount
            || ShapeKind[objectIndex] != VectorAnimationEngine.ShapeKind.Freeform
            || segmentIndex < 0
            || parameter <= 0.001f
            || parameter >= 0.999f
            || !TryGetFreehandBezierWorldNodes(objectIndex, out var nodes)
            || segmentIndex + 1 >= nodes.Length)
        {
            return false;
        }

        var p0 = nodes[segmentIndex].Anchor;
        var p1 = nodes[segmentIndex].OutgoingControl;
        var p2 = nodes[segmentIndex + 1].IncomingControl;
        var p3 = nodes[segmentIndex + 1].Anchor;
        var p01 = Lerp(p0, p1, parameter);
        var p12 = Lerp(p1, p2, parameter);
        var p23 = Lerp(p2, p3, parameter);
        var p012 = Lerp(p01, p12, parameter);
        var p123 = Lerp(p12, p23, parameter);
        var m = VectorUnits.Quantize(Lerp(p012, p123, parameter));

        var updated = new List<PathBezierNode>(nodes);
        updated[segmentIndex] = updated[segmentIndex] with { OutgoingControl = VectorUnits.Quantize(p01) };
        updated.Insert(segmentIndex + 1, new PathBezierNode(m, VectorUnits.Quantize(p012), VectorUnits.Quantize(p123)));
        updated[segmentIndex + 2] = updated[segmentIndex + 2] with { IncomingControl = VectorUnits.Quantize(p23) };

        if (!SetFreehandBezierNodesCore(objectIndex, updated)) return false;

        RebuildGeometryIndex();
        RebuildSummaries();
        insertedNodeIndex = segmentIndex + 1;
        return true;
    }

    public bool AddConnectedLineBranch(
        int sourceObjectIndex,
        float sourceParameter,
        PointF end,
        out ConnectedLineBranchResult result)
    {
        result = default;
        if ((uint)sourceObjectIndex >= ObjectCount
            || ShapeKind[sourceObjectIndex] != VectorAnimationEngine.ShapeKind.Line
            || !float.IsFinite(sourceParameter)
            || sourceParameter < 0
            || sourceParameter > 1)
        {
            return false;
        }

        var atStart = sourceParameter <= 0.001f;
        var atEnd = sourceParameter >= 0.999f;
        PointF anchor;
        if (atStart || atEnd)
        {
            if (!TryGetLineEndpoint(sourceObjectIndex, atStart, out anchor)) return false;
        }
        else
        {
            var curve = LineCurve(sourceObjectIndex);
            anchor = VectorUnits.Quantize(CubicPoint(
                curve.Start,
                curve.Control1,
                curve.Control2,
                curve.End,
                sourceParameter));
        }

        end = VectorUnits.Quantize(end);
        if (Distance(anchor, end) < DrawingTopologyRules.MinStrokeSegmentUnits) return false;

        var layer = ObjectLayer[sourceObjectIndex];
        var keyframeFrame = ObjectKeyframeFrame[sourceObjectIndex];
        var order = ObjectOrder[sourceObjectIndex];
        var stroke = Stroke[sourceObjectIndex];
        var fillColor = Color.FromArgb(Argb[sourceObjectIndex]);
        var strokeColor = Color.FromArgb(StrokeArgb[sourceObjectIndex]);
        var freeEndpointStyle = atStart
            ? GetLineEndpointStyle(sourceObjectIndex, startEndpoint: true)
            : atEnd
                ? GetLineEndpointStyle(sourceObjectIndex, startEndpoint: false)
                : LineEndpointStyle.Round;
        var gradientPaint = CaptureGradientPaint(sourceObjectIndex);

        int[] sourceObjects;
        if (atStart || atEnd)
        {
            sourceObjects = [sourceObjectIndex];
        }
        else
        {
            if (!SplitLineAt(
                    sourceObjectIndex,
                    sourceParameter,
                    out var split,
                    rebuildGeometryIndex: false))
            {
                return false;
            }

            anchor = split.Anchor;
            sourceObjects = [split.FirstObjectIndex, split.SecondObjectIndex];
        }

        var branchSubOrder = ReplacementSubOrders(
            sourceObjects[0],
            1,
            preserveSourceSubOrder: true)[0];
        var branchObject = AppendCubicCurveSegment(
            layer,
            anchor,
            Lerp(anchor, end, 1f / 3f),
            Lerp(anchor, end, 2f / 3f),
            end,
            stroke,
            fillColor,
            strokeColor,
            6,
            LineEndpointStyle.Round,
            freeEndpointStyle);
        ObjectLayer[branchObject] = layer;
        ObjectKeyframeFrame[branchObject] = keyframeFrame;
        ObjectOrder[branchObject] = order;
        ObjectSubOrder[branchObject] = branchSubOrder;
        ApplyGradientPaint(branchObject, gradientPaint);
        RebuildGeometryIndex();
        RebuildSummaries();
        result = new ConnectedLineBranchResult(branchObject, sourceObjects, anchor);
        return true;
    }

    public bool IsLineStraight(int objectIndex)
    {
        if ((uint)objectIndex >= ObjectCount || ShapeKind[objectIndex] != VectorAnimationEngine.ShapeKind.Line) return false;
        var halfW = Width[objectIndex] * 0.5f;
        var start = LocalToWorld(objectIndex, -halfW, 0);
        var end = LocalToWorld(objectIndex, halfW, 0);
        return IsStraightBezierSegment(
            start,
            new PointF(CurveControlX[objectIndex], CurveControlY[objectIndex]),
            new PointF(CurveControl2X[objectIndex], CurveControl2Y[objectIndex]),
            end);
    }

    public LineEndpointStyle GetLineEndpointStyle(int objectIndex, bool startEndpoint)
    {
        return (uint)objectIndex < ObjectCount
            && ShapeKind[objectIndex] == VectorAnimationEngine.ShapeKind.Line
            && LineEndpointStyles.Length > objectIndex
            && LineEndEndpointStyles.Length > objectIndex
            ? NormalizeLineEndpointStyle(startEndpoint ? LineEndpointStyles[objectIndex] : LineEndEndpointStyles[objectIndex])
            : LineEndpointStyle.Round;
    }

    public bool SetLineEndpointStyle(int objectIndex, bool startEndpoint, LineEndpointStyle endpointStyle)
    {
        if ((uint)objectIndex >= ObjectCount || ShapeKind[objectIndex] != VectorAnimationEngine.ShapeKind.Line) return false;
        var normalized = NormalizeLineEndpointStyle(endpointStyle);
        var styles = startEndpoint ? LineEndpointStyles : LineEndEndpointStyles;
        if (styles[objectIndex] == normalized) return false;
        styles[objectIndex] = normalized;
        GeometryRevision++;
        return true;
    }

    public LineEndpointStyle GetLineEndpointStyle(int objectIndex) => GetLineEndpointStyle(objectIndex, startEndpoint: true);

    public bool SetLineEndpointStyle(int objectIndex, LineEndpointStyle endpointStyle)
    {
        var startChanged = SetLineEndpointStyle(objectIndex, startEndpoint: true, endpointStyle);
        var endChanged = SetLineEndpointStyle(objectIndex, startEndpoint: false, endpointStyle);
        return startChanged || endChanged;
    }

    public void SetLineEndpoint(
        int objectIndex,
        bool startEndpoint,
        PointF endpoint,
        PointF oppositeEndpoint,
        PointF control,
        bool keepStraight)
    {
        var start = startEndpoint ? endpoint : oppositeEndpoint;
        var end = startEndpoint ? oppositeEndpoint : endpoint;
        SetLineEndpoint(
            objectIndex,
            startEndpoint,
            endpoint,
            oppositeEndpoint,
            Lerp(start, control, 2f / 3f),
            Lerp(end, control, 2f / 3f),
            keepStraight);
    }

    public void SetLineEndpoint(
        int objectIndex,
        bool startEndpoint,
        PointF endpoint,
        PointF oppositeEndpoint,
        PointF control1,
        PointF control2,
        bool keepStraight)
    {
        if ((uint)objectIndex >= ObjectCount || ShapeKind[objectIndex] != VectorAnimationEngine.ShapeKind.Line) return;
        var start = startEndpoint ? endpoint : oppositeEndpoint;
        var end = startEndpoint ? oppositeEndpoint : endpoint;
        var center = Midpoint(start, end);
        X[objectIndex] = center.X;
        Y[objectIndex] = center.Y;
        Width[objectIndex] = Math.Max(DrawingTopologyRules.MinStrokeSegmentUnits, Distance(start, end));
        Height[objectIndex] = Math.Max(VectorUnits.FromPixels(3), Height[objectIndex]);
        Angle[objectIndex] = MathF.Atan2(end.Y - start.Y, end.X - start.X);
        if (keepStraight)
        {
            control1 = Lerp(start, end, 1f / 3f);
            control2 = Lerp(start, end, 2f / 3f);
        }
        CurveControlX[objectIndex] = VectorUnits.Quantize(control1.X);
        CurveControlY[objectIndex] = VectorUnits.Quantize(control1.Y);
        CurveControl2X[objectIndex] = VectorUnits.Quantize(control2.X);
        CurveControl2Y[objectIndex] = VectorUnits.Quantize(control2.Y);
        InvalidateDeferredTopologyQueries();
    }

    public bool TryGetLineCubic(
        int objectIndex,
        out PointF start,
        out PointF control1,
        out PointF control2,
        out PointF end)
    {
        start = control1 = control2 = end = PointF.Empty;
        if ((uint)objectIndex >= ObjectCount || ShapeKind[objectIndex] != VectorAnimationEngine.ShapeKind.Line) return false;
        var curve = LineCurve(objectIndex);
        start = curve.Start;
        control1 = curve.Control1;
        control2 = curve.Control2;
        end = curve.End;
        return true;
    }

    public bool TryGetLineQuadraticControl(int objectIndex, out PointF control)
    {
        control = PointF.Empty;
        if ((uint)objectIndex >= ObjectCount || ShapeKind[objectIndex] != VectorAnimationEngine.ShapeKind.Line) return false;
        if (!TryGetLineEndpoint(objectIndex, startEndpoint: true, out var start)) return false;
        control = new PointF(
            1.5f * CurveControlX[objectIndex] - 0.5f * start.X,
            1.5f * CurveControlY[objectIndex] - 0.5f * start.Y);
        return true;
    }

    public void SetLineQuadraticControl(int objectIndex, PointF control)
    {
        if ((uint)objectIndex >= ObjectCount || ShapeKind[objectIndex] != VectorAnimationEngine.ShapeKind.Line) return;
        if (!TryGetLineEndpoint(objectIndex, startEndpoint: true, out var start)
            || !TryGetLineEndpoint(objectIndex, startEndpoint: false, out var end))
        {
            return;
        }

        CurveControlX[objectIndex] = start.X + (2f / 3f) * (control.X - start.X);
        CurveControlY[objectIndex] = start.Y + (2f / 3f) * (control.Y - start.Y);
        CurveControl2X[objectIndex] = end.X + (2f / 3f) * (control.X - end.X);
        CurveControl2Y[objectIndex] = end.Y + (2f / 3f) * (control.Y - end.Y);
    }

}
