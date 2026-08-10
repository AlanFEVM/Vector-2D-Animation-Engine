using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Clipper2Lib;

namespace VectorAnimationEngine;

internal sealed partial class VectorScene
{
    private DrawingElementHit DetachStrokePart(DrawingElementHit hit, IReadOnlyList<int> candidates)
    {
        var source = hit.Key.ObjectIndex;
        if ((uint)source >= ObjectCount || !IsTopologyStrokeShape(ShapeKind[source]) || !HasStroke(source)) return hit;

        var splits = StrokeSplitParameters(source, candidates);
        if ((uint)hit.Key.PartIndex >= (uint)Math.Max(0, splits.Count - 1)) return DrawingElementHit.None;
        if (splits.Count <= 2) return hit;

        var layer = ObjectLayer[source];
        var order = ObjectOrder[source];
        var stroke = Stroke[source];
        var fillColor = Color.FromArgb(Argb[source]);
        var strokeColor = Color.FromArgb(StrokeArgb[source]);
        var atoms = AtomCount[source];
        var selectedPart = hit.Key.PartIndex;
        var selectedIndex = -1;

        if (ShapeKind[source] == VectorAnimationEngine.ShapeKind.Line)
        {
            var curve = LineCurve(source);
            var segments = BuildCurveParts(curve.Start, curve.Control1, curve.Control2, curve.End, splits);
            RemoveObjectAt(source);
            foreach (var segment in segments)
            {
                var index = AddCubicCurveSegment(
                    layer,
                    segment.Start,
                    segment.Control1,
                    segment.Control2,
                    segment.End,
                    stroke,
                    fillColor,
                    strokeColor,
                    Math.Max(3u, atoms / (uint)Math.Max(1, segments.Count)),
                    segment.PartIndex == 0 ? GetLineEndpointStyle(source, startEndpoint: true) : LineEndpointStyle.Round,
                    segment.PartIndex == segments[^1].PartIndex ? GetLineEndpointStyle(source, startEndpoint: false) : LineEndpointStyle.Round);
                if (index >= 0) ObjectOrder[index] = order;
                if (segment.PartIndex == selectedPart) selectedIndex = index;
            }
        }
        else
        {
            if (!TryGetFreehandWorldPoints(source, out var points)) return hit;
            var segments = BuildPolylinePathParts(points, splits);
            RemoveObjectAt(source);
            foreach (var segment in segments)
            {
                var index = AddFreehandStroke(layer, segment.Points, stroke, strokeColor, brushStroke: false, Math.Max(3u, atoms / (uint)Math.Max(1, segments.Count)));
                if (index >= 0) ObjectOrder[index] = order;
                if (segment.PartIndex == selectedPart) selectedIndex = index;
            }
        }

        if (selectedIndex < 0 && ObjectCount > 0) selectedIndex = ObjectCount - 1;
        return selectedIndex >= 0
            ? new DrawingElementHit(new DrawingElementKey(selectedIndex, DrawingElementKind.Stroke, 0), -1, 0, 1)
            : DrawingElementHit.None;
    }

    private DrawingElementHit DetachBoundaryStrokePart(DrawingElementHit hit, IReadOnlyList<int> candidates)
    {
        var source = hit.Key.ObjectIndex;
        if ((uint)source >= ObjectCount || !HasStroke(source)) return hit;

        var segments = BuildBoundaryStrokeParts(source, candidates);
        if (segments.Count == 0) return hit;
        if (!segments.Any(segment => segment.PartIndex == hit.Key.PartIndex)) return DrawingElementHit.None;

        var layer = ObjectLayer[source];
        var order = ObjectOrder[source];
        var stroke = Stroke[source];
        var fillColor = Color.FromArgb(Argb[source]);
        var strokeColor = Color.FromArgb(StrokeArgb[source]);
        var atoms = AtomCount[source];
        var selectedPart = hit.Key.PartIndex;
        var subOrders = ReplacementSubOrders(source, segments.Count, preserveSourceSubOrder: true);

        Stroke[source] = 0;
        var selectedIndex = -1;
        for (var segmentIndex = 0; segmentIndex < segments.Count; segmentIndex++)
        {
            var segment = segments[segmentIndex];
            var addition = BoundaryMaterialization(
                segment,
                DrawingElementKey.None,
                layer,
                order,
                subOrders[segmentIndex],
                stroke,
                fillColor,
                strokeColor,
                Math.Max(3u, atoms / (uint)Math.Max(1, segments.Count)));
            var index = AppendMaterializedPart(addition);
            if (segment.PartIndex == selectedPart) selectedIndex = index;
        }

        if (!HasFill(source))
        {
            var last = ObjectCount - 1;
            if (selectedIndex == last) selectedIndex = source;
            RemoveObjectAt(source);
        }
        else
        {
            RebuildGeometryIndex();
            RebuildSummaries();
        }

        return selectedIndex >= 0
            ? new DrawingElementHit(new DrawingElementKey(selectedIndex, DrawingElementKind.Stroke, 0), -1, 0, 1)
            : hit;
    }

    private DrawingElementHit DetachFillPart(DrawingElementHit hit, IReadOnlyList<int> candidates)
    {
        var source = hit.Key.ObjectIndex;
        if ((uint)source >= ObjectCount || !HasFill(source)) return hit;

        var regions = BuildFillRegions(source, EditFrame, candidates);
        if ((uint)hit.Key.PartIndex >= regions.Count) return DrawingElementHit.None;
        var detachBoundary = HasStroke(source);
        if (regions.Count <= 1 && !detachBoundary) return hit;

        var layer = ObjectLayer[source];
        var order = ObjectOrder[source];
        var fillColor = Color.FromArgb(Argb[source]);
        var strokeColor = Color.FromArgb(StrokeArgb[source]);
        var atoms = AtomCount[source];

        if (detachBoundary) MaterializeBoundaryStrokes(source, candidates);
        RemoveObjectAt(source);

        var selectedIndex = -1;
        var atomsPerPart = Math.Max(3u, atoms / (uint)Math.Max(1, regions.Count));
        for (var part = 0; part < regions.Count; part++)
        {
            var index = AddPathObjectContours(layer, regions[part].Contours, 0, fillColor, strokeColor, atomsPerPart);
            if (index >= 0) ObjectOrder[index] = order;
            if (part == hit.Key.PartIndex) selectedIndex = index;
        }

        return selectedIndex >= 0
            ? new DrawingElementHit(new DrawingElementKey(selectedIndex, DrawingElementKind.Fill, 0), -1, 0, 1)
            : DrawingElementHit.None;
    }

    private void MaterializeBoundaryStrokes(int source, IReadOnlyList<int> candidates)
    {
        var segments = BuildBoundaryStrokeParts(source, candidates);
        var layer = ObjectLayer[source];
        var order = ObjectOrder[source];
        var stroke = Stroke[source];
        var fillColor = Color.FromArgb(Argb[source]);
        var strokeColor = Color.FromArgb(StrokeArgb[source]);
        var atoms = AtomCount[source];
        var subOrders = ReplacementSubOrders(source, segments.Count);
        for (var segmentIndex = 0; segmentIndex < segments.Count; segmentIndex++)
        {
            AppendMaterializedPart(BoundaryMaterialization(
                segments[segmentIndex],
                DrawingElementKey.None,
                layer,
                order,
                subOrders[segmentIndex],
                stroke,
                fillColor,
                strokeColor,
                Math.Max(3u, atoms / (uint)Math.Max(1, segments.Count))));
        }
    }

    private int AddPolylineStroke(int layer, PointF[] points, float stroke, Color fillColor, Color strokeColor, uint atoms)
    {
        if (points.Length < 2) return -1;
        if (points.Length == 2) return AddLineSegment(layer, points[0], points[1], stroke, fillColor, strokeColor, atoms);
        return AddFreehandStroke(layer, points, stroke, strokeColor, brushStroke: false, atoms);
    }

    private List<PolylinePart> BuildPolylinePathParts(PointF[] polyline, IReadOnlyList<float> splits)
    {
        var result = new List<PolylinePart>();
        for (var i = 0; i < splits.Count - 1; i++)
        {
            var startT = splits[i];
            var endT = splits[i + 1];
            if (endT - startT <= 0.0001f) continue;

            var points = PolylineSlice(polyline, startT, endT);
            if (PolylineLength(points) < DrawingTopologyRules.MinStrokeSegmentUnits) continue;
            result.Add(new PolylinePart(i, startT, endT, points));
        }

        return result;
    }

    private static bool TryBuildOpenBezierRuns(
        IReadOnlyList<PathBezierNode> nodes,
        IReadOnlyList<DrawingTopologySplit> splits,
        out List<OpenBezierRun> runs)
    {
        runs = [];
        if (nodes.Count < 2 || splits.Count < 2) return false;
        var samples = SampleOpenFreehandBezierNodesWithParameters(nodes);
        if (samples.Length < 2) return false;

        var cuts = new OpenBezierCut[splits.Count];
        cuts[0] = new OpenBezierCut(0, 0, nodes[0].Anchor);
        cuts[^1] = new OpenBezierCut(nodes.Count - 2, 1, nodes[^1].Anchor);
        for (var splitIndex = 1; splitIndex < splits.Count - 1; splitIndex++)
        {
            if (!TryResolveOpenBezierCut(nodes, samples, splits[splitIndex], out cuts[splitIndex]))
            {
                runs = [];
                return false;
            }
        }

        for (var partIndex = 0; partIndex < cuts.Length - 1; partIndex++)
        {
            var start = cuts[partIndex];
            var end = cuts[partIndex + 1];
            if (end.PathT - start.PathT <= 0.0001f)
            {
                runs = [];
                return false;
            }
            if (!TryCreateOpenBezierRunNodes(nodes, start, end, out var runNodes))
            {
                runs = [];
                return false;
            }
            runs.Add(new OpenBezierRun(partIndex, start, end, runNodes));
        }

        return runs.Count > 0;
    }

    private static bool TryResolveOpenBezierCut(
        IReadOnlyList<PathBezierNode> nodes,
        IReadOnlyList<OpenBezierSample> samples,
        DrawingTopologySplit split,
        out OpenBezierCut cut)
    {
        cut = default;
        if (nodes.Count < 2 || samples.Count < 2 || !Finite(split.Point)) return false;
        var samplePosition = Math.Clamp(split.T, 0, 1) * (samples.Count - 1);
        var lowerIndex = Math.Clamp((int)MathF.Floor(samplePosition), 0, samples.Count - 1);
        var upperIndex = Math.Clamp(lowerIndex + 1, 0, samples.Count - 1);
        var fraction = samplePosition - lowerIndex;
        var lower = samples[lowerIndex];
        var upper = samples[upperIndex];
        var targetPathT = lower.SegmentIndex + lower.LocalT
            + (upper.SegmentIndex + upper.LocalT - lower.SegmentIndex - lower.LocalT) * fraction;
        var lastSegment = nodes.Count - 2;
        var primarySegment = Math.Clamp((int)MathF.Floor(targetPathT), 0, lastSegment);
        var candidateSegments = new[]
            {
                primarySegment - 1,
                primarySegment,
                primarySegment + 1
            }
            .Where(index => index >= 0 && index <= lastSegment)
            .Distinct();

        var bestDistance = float.MaxValue;
        var bestPathDelta = float.MaxValue;
        var bestSegment = -1;
        var bestLocalT = 0f;
        foreach (var segmentIndex in candidateSegments)
        {
            var current = nodes[segmentIndex];
            var next = nodes[segmentIndex + 1];
            var prepared = PrepareBezierCurve(new CubicBoundarySegment(
                current.Anchor,
                current.OutgoingControl,
                next.IncomingControl,
                next.Anchor));
            if (!TryGetClosestParameterOnCubic(
                    prepared,
                    split.Point,
                    out var localT,
                    out var distance))
            {
                continue;
            }

            var pathDelta = Math.Abs(segmentIndex + localT - targetPathT);
            if (distance > bestDistance + DrawingTopologyRules.UnitIntersectionTolerance
                || Math.Abs(distance - bestDistance) <= DrawingTopologyRules.UnitIntersectionTolerance
                && pathDelta >= bestPathDelta)
            {
                continue;
            }

            bestDistance = distance;
            bestPathDelta = pathDelta;
            bestSegment = segmentIndex;
            bestLocalT = localT;
        }

        if (bestSegment < 0) return false;
        if (bestLocalT <= 0.0001f)
        {
            bestLocalT = 0;
        }
        else if (bestLocalT >= 0.9999f)
        {
            if (bestSegment < lastSegment)
            {
                bestSegment++;
                bestLocalT = 0;
            }
            else
            {
                bestLocalT = 1;
            }
        }

        var point = bestLocalT <= 0
            ? nodes[bestSegment].Anchor
            : bestLocalT >= 1
                ? nodes[bestSegment + 1].Anchor
                : CubicPoint(
                    nodes[bestSegment].Anchor,
                    nodes[bestSegment].OutgoingControl,
                    nodes[bestSegment + 1].IncomingControl,
                    nodes[bestSegment + 1].Anchor,
                    bestLocalT);
        cut = new OpenBezierCut(bestSegment, bestLocalT, point);
        return true;
    }

    private static bool TryCreateOpenBezierRunNodes(
        IReadOnlyList<PathBezierNode> sourceNodes,
        OpenBezierCut start,
        OpenBezierCut end,
        out PathBezierNode[] nodes)
    {
        nodes = [];
        if (sourceNodes.Count < 2
            || start.SegmentIndex < 0
            || end.SegmentIndex < start.SegmentIndex
            || end.SegmentIndex >= sourceNodes.Count - 1
            || end.PathT - start.PathT <= 0.0001f)
        {
            return false;
        }

        var curves = new List<CubicBoundarySegment>(end.SegmentIndex - start.SegmentIndex + 1);
        for (var segmentIndex = start.SegmentIndex; segmentIndex <= end.SegmentIndex; segmentIndex++)
        {
            var localStart = segmentIndex == start.SegmentIndex ? start.LocalT : 0;
            var localEnd = segmentIndex == end.SegmentIndex ? end.LocalT : 1;
            if (localEnd - localStart <= 0.0001f) continue;
            var current = sourceNodes[segmentIndex];
            var next = sourceNodes[segmentIndex + 1];
            curves.Add(CubicSubcurve(
                current.Anchor,
                current.OutgoingControl,
                next.IncomingControl,
                next.Anchor,
                localStart,
                localEnd));
        }
        if (curves.Count == 0) return false;

        var result = new PathBezierNode[curves.Count + 1];
        result[0] = new PathBezierNode(
            curves[0].Start,
            curves[0].Start,
            curves[0].Control1);
        for (var curveIndex = 1; curveIndex < curves.Count; curveIndex++)
        {
            result[curveIndex] = new PathBezierNode(
                curves[curveIndex].Start,
                curves[curveIndex - 1].Control2,
                curves[curveIndex].Control1);
        }
        var last = curves[^1];
        result[^1] = new PathBezierNode(last.End, last.Control2, last.End);
        var sampled = SampleOpenFreehandBezierNodes(result);
        if (sampled.Length < 2
            || PolylineLength(sampled) < DrawingTopologyRules.MinStrokeSegmentUnits)
        {
            return false;
        }

        nodes = result;
        return true;
    }

    private List<CurveSegmentPart> BuildCurveParts(
        PointF start,
        PointF control1,
        PointF control2,
        PointF end,
        IReadOnlyList<float> splits)
    {
        var result = new List<CurveSegmentPart>();
        for (var i = 0; i < splits.Count - 1; i++)
        {
            var startT = splits[i];
            var endT = splits[i + 1];
            if (endT - startT <= 0.0001f) continue;
            var segment = CubicSubcurve(start, control1, control2, end, startT, endT);
            if (Distance(segment.Start, segment.End) < DrawingTopologyRules.MinStrokeSegmentUnits) continue;
            result.Add(new CurveSegmentPart(i, segment.Start, segment.Control1, segment.Control2, segment.End));
        }

        return result;
    }

    private List<CurveSegmentPart> BuildCurveParts(
        PointF start,
        PointF control1,
        PointF control2,
        PointF end,
        IReadOnlyList<DrawingTopologySplit> splits)
    {
        var result = new List<CurveSegmentPart>();
        for (var i = 0; i < splits.Count - 1; i++)
        {
            var startSplit = splits[i];
            var endSplit = splits[i + 1];
            if (endSplit.T - startSplit.T <= 0.0001f) continue;

            var segment = CubicSubcurve(
                start,
                control1,
                control2,
                end,
                startSplit.T,
                endSplit.T);
            var startDelta = new PointF(
                startSplit.Point.X - segment.Start.X,
                startSplit.Point.Y - segment.Start.Y);
            var endDelta = new PointF(
                endSplit.Point.X - segment.End.X,
                endSplit.Point.Y - segment.End.Y);
            var adjustedControl1 = new PointF(
                segment.Control1.X + startDelta.X,
                segment.Control1.Y + startDelta.Y);
            var adjustedControl2 = new PointF(
                segment.Control2.X + endDelta.X,
                segment.Control2.Y + endDelta.Y);
            if (Distance(startSplit.Point, endSplit.Point) < DrawingTopologyRules.MinStrokeSegmentUnits) continue;
            result.Add(new CurveSegmentPart(
                i,
                startSplit.Point,
                adjustedControl1,
                adjustedControl2,
                endSplit.Point));
        }

        return result;
    }

    private List<int> CollectHitCandidates(int minX, int maxX, int minY, int maxY, int frame, bool sort = true)
    {
        var result = new List<int>(128);
        PopulateQueryActiveKeyframes(frame);
        // RebuildSpatialIndex assigns every object to exactly one center cell.
        for (var cy = minY; cy <= maxY; cy++)
        {
            for (var cx = minX; cx <= maxX; cx++)
            {
                var cell = CellIndex(cx, cy);
                var start = CellStart[cell];
                var end = CellStart[cell + 1];
                for (var p = start; p < end; p++)
                {
                    var i = CellObjects[p];
                    if ((uint)i >= ObjectCount) continue;
                    if (ObjectKeyframeFrame[i] != _queryActiveKeyframes[ObjectLayer[i]]) continue;
                    result.Add(i);
                }

                foreach (var i in GetPendingSpatialCellObjects(cell))
                {
                    if (ObjectKeyframeFrame[i] != _queryActiveKeyframes[ObjectLayer[i]]) continue;
                    result.Add(i);
                }
            }
        }

        if (sort) result.Sort();
        return result;
    }

    public int[] QueryObjects(RectangleF worldBounds, int frame, int limit = 100_000)
    {
        if (ObjectCount <= 0 || limit <= 0) return Array.Empty<int>();
        var bounds = Normalize(worldBounds);
        if (bounds.Width <= 0.001f || bounds.Height <= 0.001f) return Array.Empty<int>();

        GetIndexRange(bounds, out var minX, out var maxX, out var minY, out var maxY);
        var result = new List<int>(Math.Min(ObjectCount, 1024));
        PopulateQueryActiveKeyframes(frame);

        for (var cy = minY; cy <= maxY; cy++)
        {
            for (var cx = minX; cx <= maxX; cx++)
            {
                var cell = CellIndex(cx, cy);
                var start = CellStart[cell];
                var end = CellStart[cell + 1];
                for (var p = start; p < end; p++)
                {
                    var i = CellObjects[p];
                    if ((uint)i >= ObjectCount) continue;
                    if (ObjectKeyframeFrame[i] != _queryActiveKeyframes[ObjectLayer[i]]) continue;
                    if (!ObjectIntersectsBounds(i, bounds)) continue;

                    result.Add(i);
                    if (result.Count >= limit)
                    {
                        result.Sort();
                        return result.ToArray();
                    }
                }

                foreach (var i in GetPendingSpatialCellObjects(cell))
                {
                    if (ObjectKeyframeFrame[i] != _queryActiveKeyframes[ObjectLayer[i]]) continue;
                    if (!ObjectIntersectsBounds(i, bounds)) continue;

                    result.Add(i);
                    if (result.Count >= limit)
                    {
                        result.Sort();
                        return result.ToArray();
                    }
                }
            }
        }

        result.Sort();
        return result.ToArray();
    }

    private void PopulateQueryActiveKeyframes(int frame)
    {
        if (_queryActiveFrame == frame && _queryActiveKeyframes.Length == LayerCount) return;
        if (_queryActiveKeyframes.Length != LayerCount) Array.Resize(ref _queryActiveKeyframes, LayerCount);
        PopulateActiveKeyframeFrames(frame, _queryActiveKeyframes);
        _queryActiveFrame = frame;
    }

    private void InvalidateQueryActiveKeyframes()
    {
        _queryActiveFrame = int.MinValue;
        InvalidateInteractiveQueryCaches();
    }

    public int[] QueryDrawingObjects(RectangleF worldBounds, int frame, int limit = 100_000)
    {
        var bounds = Normalize(worldBounds);
        return QueryObjects(bounds, frame, limit)
            .Where(index => ObjectGeometryIntersectsBounds(index, bounds))
            .ToArray();
    }

    public DrawingElementHit[] QueryDrawingElementsInsideBounds(RectangleF worldBounds, int frame, int limit = 100_000)
    {
        var bounds = Normalize(worldBounds);
        var result = new List<DrawingElementHit>();
        foreach (var objectIndex in QueryDrawingObjects(bounds, frame, limit))
        {
            var shape = ShapeKind[objectIndex];
            if (shape == VectorAnimationEngine.ShapeKind.MixingStroke)
            {
                if (TryGetMixingBrushLocalRegion(objectIndex, out var mixingRegion))
                {
                    for (var partIndex = 0; partIndex < mixingRegion.ConnectedComponentCount; partIndex++)
                    {
                        if (!mixingRegion.TryGetConnectedComponent(partIndex, out var component)
                            || !component.IntersectsBounds(
                                bounds,
                                point => LocalToWorld(objectIndex, point.X, point.Y)))
                        {
                            continue;
                        }
                        result.Add(new DrawingElementHit(
                            new DrawingElementKey(objectIndex, DrawingElementKind.Fill, partIndex),
                            0,
                            0,
                            1));
                    }
                }
                else if (MixingStrokeIntersectsBounds(objectIndex, bounds))
                {
                    result.Add(new DrawingElementHit(
                        new DrawingElementKey(objectIndex, DrawingElementKind.Fill, 0),
                        0,
                        0,
                        1));
                }
                continue;
            }
            if (IsWholeObjectShape(shape))
            {
                if (IsObjectGeometryInsideBounds(objectIndex, bounds))
                {
                    result.Add(new DrawingElementHit(
                        new DrawingElementKey(objectIndex, DrawingElementKind.Fill, 0),
                        0,
                        0,
                        1));
                }
                continue;
            }
            var candidates = CollectTopologyCandidates(objectIndex, frame);
            if (IsTopologyStrokeShape(shape))
            {
                if (!HasStroke(objectIndex)) continue;
                var splits = StrokeSplitParameters(objectIndex, candidates);
                if (shape == VectorAnimationEngine.ShapeKind.Line)
                {
                    var curve = LineCurve(objectIndex);
                    foreach (var part in BuildCurveParts(curve.Start, curve.Control1, curve.Control2, curve.End, splits))
                    {
                        if (!CubicCurveInsideRectangle(part.Start, part.Control1, part.Control2, part.End, bounds)) continue;

                        result.Add(new DrawingElementHit(
                            new DrawingElementKey(objectIndex, DrawingElementKind.Stroke, part.PartIndex),
                            0,
                            splits[part.PartIndex],
                            splits[part.PartIndex + 1]));
                    }
                }
                else if (TryGetFreehandWorldPoints(objectIndex, out var points))
                {
                    foreach (var part in BuildPolylinePathParts(points, splits))
                    {
                        if (!part.Points.All(point => PointInRectangle(point, bounds))) continue;
                        result.Add(new DrawingElementHit(
                            new DrawingElementKey(objectIndex, DrawingElementKind.Stroke, part.PartIndex),
                            0,
                            part.StartT,
                            part.EndT));
                    }
                }

                continue;
            }

            if (HasFill(objectIndex))
            {
                var regions = BuildFillRegions(objectIndex, frame, candidates);
                for (var part = 0; part < regions.Count; part++)
                {
                    if (!regions[part].Contours.All(contour => contour.All(point => PointInRectangle(point, bounds)))) continue;
                    result.Add(new DrawingElementHit(
                        new DrawingElementKey(objectIndex, DrawingElementKind.Fill, part),
                        0,
                        0,
                        1));
                }
            }

            if (!HasStroke(objectIndex)) continue;
            foreach (var part in BuildBoundaryStrokeParts(objectIndex, candidates))
            {
                if (!part.Points.All(point => PointInRectangle(point, bounds))) continue;
                result.Add(new DrawingElementHit(
                    new DrawingElementKey(objectIndex, DrawingElementKind.BoundaryStroke, part.PartIndex),
                    0,
                    part.StartT,
                    part.EndT));
            }
        }

        return result.ToArray();
    }

    public bool IsObjectGeometryInsideBounds(int objectIndex, RectangleF worldBounds)
    {
        if ((uint)objectIndex >= ObjectCount) return false;
        var bounds = Normalize(worldBounds);
        if (_objectDistortions.ContainsKey(objectIndex))
        {
            var distortedContours = GetDistortedObjectBoundaryContours(objectIndex);
            return distortedContours.Length > 0
                && distortedContours.All(contour => contour.All(point => PointInRectangle(point, bounds)));
        }

        var shape = ShapeKind[objectIndex];
        if (IsTopologyStrokeShape(shape))
        {
            var samples = StrokeSamples(objectIndex);
            return samples.Length > 0 && samples.All(sample => PointInRectangle(sample.Point, bounds));
        }

        var contours = ShapeBoundaryContours(objectIndex);
        return contours.Length > 0
            && contours.All(contour => contour
                .Select(point => MapObjectPoint(objectIndex, point))
                .All(point => PointInRectangle(point, bounds)));
    }

    private bool ObjectGeometryIntersectsBounds(int objectIndex, RectangleF bounds)
    {
        var shape = ShapeKind[objectIndex];
        if (_objectDistortions.ContainsKey(objectIndex))
        {
            if (IsTopologyStrokeShape(shape))
            {
                if (!HasStroke(objectIndex)) return false;
                var radius = Math.Max(Stroke[objectIndex] * 0.5f, 1);
                var distortedStrokeContours = GetDistortedObjectBoundaryContours(objectIndex);
                var points = distortedStrokeContours.Length > 0 ? distortedStrokeContours[0] : [];
                var expanded = bounds;
                expanded.Inflate(radius, radius);
                return PolylineIntersectsRectangle(points, expanded);
            }

            var distortedContours = GetDistortedObjectBoundaryContours(objectIndex);
            if (distortedContours.Any(contour => PolylineIntersectsRectangle(contour, bounds))) return true;
            if (!HasFill(objectIndex)) return false;
            var testCorners = new[]
            {
                new PointF(bounds.Left, bounds.Top),
                new PointF(bounds.Right, bounds.Top),
                new PointF(bounds.Right, bounds.Bottom),
                new PointF(bounds.Left, bounds.Bottom)
            };
            return testCorners.Any(corner => FillContainsPoint(objectIndex, corner));
        }
        if (shape == VectorAnimationEngine.ShapeKind.MixingStroke)
        {
            return MixingStrokeIntersectsBounds(objectIndex, bounds);
        }
        if (IsWholeObjectShape(shape))
        {
            var boundary = ShapeBoundary(objectIndex);
            if (boundary.Any(point => PointInRectangle(point, bounds))
                || PolylineIntersectsRectangle(boundary, bounds))
            {
                return true;
            }

            var importedBoundsCorners = new[]
            {
                new PointF(bounds.Left, bounds.Top),
                new PointF(bounds.Right, bounds.Top),
                new PointF(bounds.Right, bounds.Bottom),
                new PointF(bounds.Left, bounds.Bottom)
            };
            return importedBoundsCorners.Any(point => HitObject(point, objectIndex, toleranceWorld: 0));
        }
        if (IsTopologyStrokeShape(shape))
        {
            if (!HasStroke(objectIndex)) return false;
            var expanded = bounds;
            var radius = Math.Max(Stroke[objectIndex] * 0.5f, 1);
            expanded.Inflate(radius, radius);
            return PolylineIntersectsRectangle(StrokeSamples(objectIndex).Select(sample => sample.Point).ToArray(), expanded);
        }

        if (!HasFill(objectIndex) && !HasStroke(objectIndex)) return false;
        var contours = ShapeBoundaryContours(objectIndex);
        var contourBounds = bounds;
        if (HasStroke(objectIndex))
        {
            var radius = Math.Max(Stroke[objectIndex] * 0.5f, 1);
            contourBounds.Inflate(radius, radius);
        }

        foreach (var contour in contours)
        {
            if (PolylineIntersectsRectangle(contour, contourBounds)) return true;
        }

        if (!HasFill(objectIndex)) return false;
        var corners = new[]
        {
            new PointF(bounds.Left, bounds.Top),
            new PointF(bounds.Right, bounds.Top),
            new PointF(bounds.Right, bounds.Bottom),
            new PointF(bounds.Left, bounds.Bottom)
        };
        return corners.Any(corner => FillContainsPoint(objectIndex, corner));
    }

    private static bool PolylineIntersectsRectangle(IReadOnlyList<PointF> points, RectangleF bounds)
    {
        if (points.Count == 0) return false;
        if (points.Any(point => PointInRectangle(point, bounds))) return true;
        for (var i = 0; i < points.Count - 1; i++)
        {
            if (SegmentIntersectsRectangle(points[i], points[i + 1], bounds)) return true;
        }

        return false;
    }

    private static bool SegmentIntersectsRectangle(PointF start, PointF end, RectangleF bounds)
    {
        var topLeft = new PointF(bounds.Left, bounds.Top);
        var topRight = new PointF(bounds.Right, bounds.Top);
        var bottomRight = new PointF(bounds.Right, bounds.Bottom);
        var bottomLeft = new PointF(bounds.Left, bounds.Bottom);
        return TrySegmentIntersection(start, end, topLeft, topRight, out _)
            || TrySegmentIntersection(start, end, topRight, bottomRight, out _)
            || TrySegmentIntersection(start, end, bottomRight, bottomLeft, out _)
            || TrySegmentIntersection(start, end, bottomLeft, topLeft, out _);
    }

    private static bool PointInRectangle(PointF point, RectangleF bounds)
    {
        return point.X >= bounds.Left - 0.001f
            && point.X <= bounds.Right + 0.001f
            && point.Y >= bounds.Top - 0.001f
            && point.Y <= bounds.Bottom + 0.001f;
    }

    public void GetIndexRange(RectangleF worldBounds, out int minX, out int maxX, out int minY, out int maxY)
    {
        var expanded = MaxHalfExtent + 4;
        var cellW = StageWidth / IndexColumns;
        var cellH = StageHeight / IndexRows;
        minX = (int)Math.Clamp((worldBounds.Left - expanded + StageWidth * 0.5f) / cellW, 0, IndexColumns - 1);
        maxX = (int)Math.Clamp((worldBounds.Right + expanded + StageWidth * 0.5f) / cellW, 0, IndexColumns - 1);
        minY = (int)Math.Clamp((worldBounds.Top - expanded + StageHeight * 0.5f) / cellH, 0, IndexRows - 1);
        maxY = (int)Math.Clamp((worldBounds.Bottom + expanded + StageHeight * 0.5f) / cellH, 0, IndexRows - 1);
    }

    public int CellIndex(int x, int y) => y * IndexColumns + x;

    internal int GetSpatialCellObjectCount(int cell)
    {
        if ((uint)cell >= IndexColumns * IndexRows) return 0;
        var count = 0;
        for (var position = CellStart[cell]; position < CellStart[cell + 1]; position++)
        {
            if ((uint)CellObjects[position] < ObjectCount) count++;
        }
        return _spatialAppendCells is not null && _spatialAppendCells.TryGetValue(cell, out var appended)
            ? count + appended.Count
            : count;
    }

    internal ReadOnlySpan<int> GetPendingSpatialCellObjects(int cell)
    {
        return _spatialAppendCells is not null && _spatialAppendCells.TryGetValue(cell, out var appended)
            ? CollectionsMarshal.AsSpan(appended)
            : ReadOnlySpan<int>.Empty;
    }

    private void AppendObjectToSpatialIndex(int objectIndex)
    {
        var cellCount = IndexColumns * IndexRows;
        if (objectIndex != ObjectCount - 1
            || CellStart.Length != cellCount + 1
            || objectIndex != _spatialBaseObjectCount + _spatialPendingObjectCount)
        {
            RebuildGeometryIndex();
            return;
        }

        MaxHalfExtent = Math.Max(MaxHalfExtent, ObjectHalfExtent(objectIndex));
        var cell = CellForWorld(X[objectIndex], Y[objectIndex]);
        if (cell >= 0)
        {
            _spatialAppendCells ??= new Dictionary<int, List<int>>();
            if (!_spatialAppendCells.TryGetValue(cell, out var appended))
            {
                appended = new List<int>(4);
                _spatialAppendCells.Add(cell, appended);
            }

            appended.Add(objectIndex);
        }

        _spatialPendingObjectCount++;
        GeometryRevision++;
        if (_spatialPendingObjectCount >= SpatialIndexAppendRebuildThreshold) RebuildSpatialIndex();
    }

    private bool TryAppendObjectRangeToSpatialIndex(int firstIndex, int count)
    {
        var cellCount = IndexColumns * IndexRows;
        if (count <= 0
            || firstIndex < 0
            || firstIndex + count != ObjectCount
            || CellStart.Length != cellCount + 1
            || firstIndex != _spatialBaseObjectCount + _spatialPendingObjectCount)
        {
            return false;
        }

        _spatialAppendCells ??= new Dictionary<int, List<int>>();
        for (var objectIndex = firstIndex; objectIndex < firstIndex + count; objectIndex++)
        {
            MaxHalfExtent = Math.Max(MaxHalfExtent, ObjectHalfExtent(objectIndex));
            var cell = CellForWorld(X[objectIndex], Y[objectIndex]);
            if (cell < 0) continue;
            if (!_spatialAppendCells.TryGetValue(cell, out var appended))
            {
                appended = new List<int>(4);
                _spatialAppendCells.Add(cell, appended);
            }
            appended.Add(objectIndex);
        }

        _spatialPendingObjectCount += count;
        if (_spatialPendingObjectCount >= SpatialIndexAppendRebuildThreshold) RebuildSpatialIndex();
        return true;
    }

    private bool TryRemapSpatialIndexAfterCompaction(IReadOnlyList<int> oldToNew)
    {
        var cellCount = IndexColumns * IndexRows;
        if (CellStart.Length != cellCount + 1 || oldToNew.Count == 0) return false;

        for (var position = 0; position < CellObjects.Length; position++)
        {
            var oldIndex = CellObjects[position];
            CellObjects[position] = (uint)oldIndex < oldToNew.Count
                ? oldToNew[oldIndex]
                : -1;
        }

        Dictionary<int, List<int>>? remappedPending = null;
        var pendingCount = 0;
        if (_spatialAppendCells is not null)
        {
            foreach (var (cell, objects) in _spatialAppendCells)
            {
                foreach (var oldIndex in objects)
                {
                    if ((uint)oldIndex >= oldToNew.Count || oldToNew[oldIndex] < 0) continue;
                    remappedPending ??= new Dictionary<int, List<int>>();
                    if (!remappedPending.TryGetValue(cell, out var remapped))
                    {
                        remapped = new List<int>(objects.Count);
                        remappedPending.Add(cell, remapped);
                    }
                    remapped.Add(oldToNew[oldIndex]);
                    pendingCount++;
                }
            }
        }

        var baseLimit = Math.Min(_spatialBaseObjectCount, oldToNew.Count);
        var baseCount = 0;
        for (var oldIndex = 0; oldIndex < baseLimit; oldIndex++)
        {
            if (oldToNew[oldIndex] >= 0) baseCount++;
        }

        _spatialBaseObjectCount = baseCount;
        _spatialPendingObjectCount = pendingCount;
        _spatialAppendCells = remappedPending;
        GeometryRevision++;
        return baseCount + pendingCount == ObjectCount;
    }

    private void RebuildSpatialIndex()
    {
        var cellCount = IndexColumns * IndexRows;
        var workers = ParallelBatch.WorkerCount(ObjectCount, 8192);
        if (workers == 1)
        {
            var starts = new int[cellCount + 1];
            var objects = GC.AllocateUninitializedArray<int>(ObjectCount);
            for (var i = 0; i < ObjectCount; i++)
            {
                var cell = CellForWorld(X[i], Y[i]);
                if (cell >= 0) starts[cell + 1]++;
            }

            for (var i = 1; i < starts.Length; i++) starts[i] += starts[i - 1];
            var cursor = new int[cellCount];
            Array.Copy(starts, cursor, cellCount);
            for (var i = 0; i < ObjectCount; i++)
            {
                var cell = CellForWorld(X[i], Y[i]);
                if (cell >= 0) objects[cursor[cell]++] = i;
            }

            CellStart = starts;
            CellObjects = objects;
            ResetSpatialAppendBuffer();
            GeometryRevision++;
            return;
        }

        var localCounts = new int[workers][];
        try
        {
            for (var worker = 0; worker < workers; worker++)
            {
                localCounts[worker] = ArrayPool<int>.Shared.Rent(cellCount);
                Array.Clear(localCounts[worker], 0, cellCount);
            }

            ParallelBatch.For(ObjectCount, 8192, (worker, start, end) =>
            {
                var counts = localCounts[worker];
                for (var i = start; i < end; i++)
                {
                    var cell = CellForWorld(X[i], Y[i]);
                    if (cell >= 0) counts[cell]++;
                }
            }, workers);

            var starts = new int[cellCount + 1];
            for (var cell = 0; cell < cellCount; cell++)
            {
                var count = 0;
                for (var worker = 0; worker < workers; worker++) count += localCounts[worker][cell];
                starts[cell + 1] = count;
            }

            for (var cell = 1; cell < starts.Length; cell++) starts[cell] += starts[cell - 1];
            for (var cell = 0; cell < cellCount; cell++)
            {
                var cursor = starts[cell];
                for (var worker = 0; worker < workers; worker++)
                {
                    var count = localCounts[worker][cell];
                    localCounts[worker][cell] = cursor;
                    cursor += count;
                }
            }

            var objects = GC.AllocateUninitializedArray<int>(ObjectCount);
            ParallelBatch.For(ObjectCount, 8192, (worker, start, end) =>
            {
                var cursors = localCounts[worker];
                for (var i = start; i < end; i++)
                {
                    var cell = CellForWorld(X[i], Y[i]);
                    if (cell >= 0) objects[cursors[cell]++] = i;
                }
            }, workers);

            CellStart = starts;
            CellObjects = objects;
            ResetSpatialAppendBuffer();
            GeometryRevision++;
        }
        finally
        {
            foreach (var counts in localCounts)
            {
                if (counts is not null) ArrayPool<int>.Shared.Return(counts);
            }
        }
    }

    private void ResetSpatialAppendBuffer()
    {
        _spatialAppendCells = null;
        _spatialBaseObjectCount = ObjectCount;
        _spatialPendingObjectCount = 0;
    }

    private void ClearSummaries()
    {
        Array.Clear(TileCount);
        Array.Clear(TileAtoms);
        Array.Clear(TileArgb);
        Array.Clear(OverviewCount);
        Array.Clear(OverviewAtoms);
        Array.Clear(OverviewArgb);
        SummaryRevision++;
    }

    private void RebuildSummaries(bool useFillColorForStrokeShapes = false)
    {
        var workers = ParallelBatch.WorkerCount(ObjectCount, 20_000, maxWorkers: 6);
        if (workers > 1)
        {
            RebuildSummariesParallel(workers, useFillColorForStrokeShapes);
            return;
        }

        ClearSummaries();
        var tileR = new long[TileCount.Length];
        var tileG = new long[TileCount.Length];
        var tileB = new long[TileCount.Length];
        var overviewR = new long[OverviewCount.Length];
        var overviewG = new long[OverviewCount.Length];
        var overviewB = new long[OverviewCount.Length];

        for (var i = 0; i < ObjectCount; i++)
        {
            var shape = ShapeKind.Length > i ? ShapeKind[i] : VectorAnimationEngine.ShapeKind.Rectangle;
            var color = Color.FromArgb(useFillColorForStrokeShapes
                ? Argb[i]
                : shape is VectorAnimationEngine.ShapeKind.Line or VectorAnimationEngine.ShapeKind.Freeform ? StrokeArgb[i] : Argb[i]);
            if (IsFreehandShape(shape))
            {
                AddObjectBoundsToSummary(i, color, TileColumns, TileRows, TileCount, TileAtoms, tileR, tileG, tileB);
                AddObjectBoundsToSummary(i, color, OverviewColumns, OverviewRows, OverviewCount, OverviewAtoms, overviewR, overviewG, overviewB);
            }
            else
            {
                AddObjectToTileSummary(i, color, tileR, tileG, tileB);
                AddObjectToOverviewSummary(i, color, overviewR, overviewG, overviewB);
            }
        }

        FinalizeTileSummary(tileR, tileG, tileB);
        FinalizeOverviewSummary(overviewR, overviewG, overviewB);
    }

    private void RebuildSummariesParallel(int workers, bool useFillColorForStrokeShapes)
    {
        var tileLength = TileCount.Length;
        var overviewLength = OverviewCount.Length;
        var tileCounts = new int[workers][];
        var tileAtoms = new long[workers][];
        var tileR = new long[workers][];
        var tileG = new long[workers][];
        var tileB = new long[workers][];
        var overviewCounts = new int[workers][];
        var overviewAtoms = new long[workers][];
        var overviewR = new long[workers][];
        var overviewG = new long[workers][];
        var overviewB = new long[workers][];

        try
        {
            for (var worker = 0; worker < workers; worker++)
            {
                tileCounts[worker] = RentCleared<int>(tileLength);
                tileAtoms[worker] = RentCleared<long>(tileLength);
                tileR[worker] = RentCleared<long>(tileLength);
                tileG[worker] = RentCleared<long>(tileLength);
                tileB[worker] = RentCleared<long>(tileLength);
                overviewCounts[worker] = RentCleared<int>(overviewLength);
                overviewAtoms[worker] = RentCleared<long>(overviewLength);
                overviewR[worker] = RentCleared<long>(overviewLength);
                overviewG[worker] = RentCleared<long>(overviewLength);
                overviewB[worker] = RentCleared<long>(overviewLength);
            }

            ParallelBatch.For(ObjectCount, 20_000, (worker, start, end) =>
            {
                for (var i = start; i < end; i++)
                {
                    var shape = ShapeKind.Length > i ? ShapeKind[i] : VectorAnimationEngine.ShapeKind.Rectangle;
                    var color = Color.FromArgb(useFillColorForStrokeShapes
                        ? Argb[i]
                        : shape is VectorAnimationEngine.ShapeKind.Line or VectorAnimationEngine.ShapeKind.Freeform ? StrokeArgb[i] : Argb[i]);
                    if (IsFreehandShape(shape))
                    {
                        AddObjectBoundsToSummary(i, color, TileColumns, TileRows, tileCounts[worker], tileAtoms[worker], tileR[worker], tileG[worker], tileB[worker]);
                        AddObjectBoundsToSummary(i, color, OverviewColumns, OverviewRows, overviewCounts[worker], overviewAtoms[worker], overviewR[worker], overviewG[worker], overviewB[worker]);
                    }
                    else
                    {
                        AddObjectCenterToSummary(i, color, TileColumns, TileRows, tileCounts[worker], tileAtoms[worker], tileR[worker], tileG[worker], tileB[worker]);
                        AddObjectCenterToSummary(i, color, OverviewColumns, OverviewRows, overviewCounts[worker], overviewAtoms[worker], overviewR[worker], overviewG[worker], overviewB[worker]);
                    }
                }
            }, workers);

            MergeSummaryBatches(tileLength, workers, tileCounts, tileAtoms, tileR, tileG, tileB, TileCount, TileAtoms, TileArgb, overview: false);
            MergeSummaryBatches(overviewLength, workers, overviewCounts, overviewAtoms, overviewR, overviewG, overviewB, OverviewCount, OverviewAtoms, OverviewArgb, overview: true);
            SummaryRevision++;
        }
        finally
        {
            ReturnBatches(tileCounts);
            ReturnBatches(tileAtoms);
            ReturnBatches(tileR);
            ReturnBatches(tileG);
            ReturnBatches(tileB);
            ReturnBatches(overviewCounts);
            ReturnBatches(overviewAtoms);
            ReturnBatches(overviewR);
            ReturnBatches(overviewG);
            ReturnBatches(overviewB);
        }
    }

    private static void MergeSummaryBatches(
        int length,
        int workers,
        int[][] batchCounts,
        long[][] batchAtoms,
        long[][] batchR,
        long[][] batchG,
        long[][] batchB,
        int[] counts,
        long[] atoms,
        int[] argb,
        bool overview)
    {
        Parallel.For(0, length, new ParallelOptions { MaxDegreeOfParallelism = workers }, cell =>
        {
            var count = 0;
            long atomCount = 0;
            long red = 0;
            long green = 0;
            long blue = 0;
            for (var worker = 0; worker < workers; worker++)
            {
                count += batchCounts[worker][cell];
                atomCount += batchAtoms[worker][cell];
                red += batchR[worker][cell];
                green += batchG[worker][cell];
                blue += batchB[worker][cell];
            }

            counts[cell] = count;
            atoms[cell] = atomCount;
            if (count == 0)
            {
                argb[cell] = Color.FromArgb(24, 36, 40, 42).ToArgb();
                return;
            }

            var alpha = overview
                ? Math.Clamp(58 + count * 6, 64, 230)
                : Math.Clamp(44 + count * 9, 48, 230);
            argb[cell] = Color.FromArgb(alpha, (int)(red / count), (int)(green / count), (int)(blue / count)).ToArgb();
        });
    }

    private static T[] RentCleared<T>(int length)
    {
        var buffer = ArrayPool<T>.Shared.Rent(length);
        Array.Clear(buffer, 0, length);
        return buffer;
    }

    private static void ReturnBatches<T>(IEnumerable<T[]?> batches)
    {
        foreach (var batch in batches)
        {
            if (batch is not null) ArrayPool<T>.Shared.Return(batch);
        }
    }

    private void AddObjectToSummariesIncremental(int objectIndex, Color color)
    {
        if (IsFreehandShape(ShapeKind[objectIndex]))
        {
            AddObjectToSummaryRange(
                objectIndex,
                color,
                TileColumns,
                TileRows,
                TileCount,
                TileAtoms,
                TileArgb,
                44,
                9);
            AddObjectToSummaryRange(
                objectIndex,
                color,
                OverviewColumns,
                OverviewRows,
                OverviewCount,
                OverviewAtoms,
                OverviewArgb,
                58,
                6);
        }
        else
        {
            AddObjectToSummaryCell(
                objectIndex,
                color,
                TileColumns,
                TileRows,
                TileCount,
                TileAtoms,
                TileArgb,
                44,
                9);
            AddObjectToSummaryCell(
                objectIndex,
                color,
                OverviewColumns,
                OverviewRows,
                OverviewCount,
                OverviewAtoms,
                OverviewArgb,
                58,
                6);
        }
        SummaryRevision++;
    }

    private int SummaryCellForObject(int objectIndex, int columns, int rows)
    {
        var x = (int)Math.Clamp(
            (X[objectIndex] + StageWidth * 0.5f) / StageWidth * columns,
            0,
            columns - 1);
        var y = (int)Math.Clamp(
            (Y[objectIndex] + StageHeight * 0.5f) / StageHeight * rows,
            0,
            rows - 1);
        return y * columns + x;
    }

    private void AddObjectSummaryCells(
        int objectIndex,
        ISet<int> cells,
        int columns,
        int rows)
    {
        if (!IsFreehandShape(ShapeKind[objectIndex]))
        {
            cells.Add(SummaryCellForObject(objectIndex, columns, rows));
            return;
        }

        GetObjectSummaryCellRange(objectIndex, columns, rows, out var minX, out var maxX, out var minY, out var maxY);
        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++) cells.Add(y * columns + x);
        }
    }

    private void RebuildSummaryCells(IReadOnlyCollection<int> tileCells, IReadOnlyCollection<int> overviewCells)
    {
        RebuildSummaryCells(
            tileCells,
            TileColumns,
            TileRows,
            TileCount,
            TileAtoms,
            TileArgb,
            alphaBase: 44,
            alphaStep: 9);
        RebuildSummaryCells(
            overviewCells,
            OverviewColumns,
            OverviewRows,
            OverviewCount,
            OverviewAtoms,
            OverviewArgb,
            alphaBase: 58,
            alphaStep: 6);
        SummaryRevision++;
    }

    private void RebuildSummaryCells(
        IReadOnlyCollection<int> targetCells,
        int columns,
        int rows,
        int[] counts,
        long[] atoms,
        int[] colors,
        int alphaBase,
        int alphaStep)
    {
        if (targetCells.Count == 0) return;
        var summaryCellWidth = StageWidth / columns;
        var summaryCellHeight = StageHeight / rows;
        var hasSpatialIndex = CellStart.Length == IndexColumns * IndexRows + 1;
        foreach (var cell in targetCells)
        {
            if ((uint)cell >= columns * rows) continue;
            var targetX = cell % columns;
            var targetY = cell / columns;
            var count = 0;
            long atomCount = 0;
            long red = 0;
            long green = 0;
            long blue = 0;

            if (!hasSpatialIndex
                || targetX == 0
                || targetX == columns - 1
                || targetY == 0
                || targetY == rows - 1)
            {
                for (var objectIndex = 0; objectIndex < ObjectCount; objectIndex++)
                {
                    AccumulateObject(objectIndex);
                }
            }
            else
            {
                var worldBounds = new RectangleF(
                    -StageWidth * 0.5f + targetX * summaryCellWidth,
                    -StageHeight * 0.5f + targetY * summaryCellHeight,
                    summaryCellWidth,
                    summaryCellHeight);
                GetIndexRange(worldBounds, out var minX, out var maxX, out var minY, out var maxY);
                for (var spatialY = minY; spatialY <= maxY; spatialY++)
                {
                    for (var spatialX = minX; spatialX <= maxX; spatialX++)
                    {
                        var spatialCell = CellIndex(spatialX, spatialY);
                        for (var position = CellStart[spatialCell]; position < CellStart[spatialCell + 1]; position++)
                        {
                            var objectIndex = CellObjects[position];
                            if ((uint)objectIndex < ObjectCount) AccumulateObject(objectIndex);
                        }

                        foreach (var objectIndex in GetPendingSpatialCellObjects(spatialCell))
                        {
                            AccumulateObject(objectIndex);
                        }
                    }
                }
            }

            counts[cell] = count;
            atoms[cell] = atomCount;
            colors[cell] = count == 0
                ? Color.FromArgb(24, 36, 40, 42).ToArgb()
                : Color.FromArgb(
                    Math.Clamp(alphaBase + count * alphaStep, alphaBase + alphaStep, 230),
                    (int)(red / count),
                    (int)(green / count),
                    (int)(blue / count)).ToArgb();

            void AccumulateObject(int objectIndex)
            {
                var shape = ShapeKind[objectIndex];
                if (IsFreehandShape(shape))
                {
                    GetObjectSummaryCellRange(
                        objectIndex,
                        columns,
                        rows,
                        out var objectMinX,
                        out var objectMaxX,
                        out var objectMinY,
                        out var objectMaxY);
                    if (targetX < objectMinX
                        || targetX > objectMaxX
                        || targetY < objectMinY
                        || targetY > objectMaxY)
                    {
                        return;
                    }
                }
                else if (SummaryCellForObject(objectIndex, columns, rows) != cell)
                {
                    return;
                }

                var color = Color.FromArgb(
                    shape is VectorAnimationEngine.ShapeKind.Line or VectorAnimationEngine.ShapeKind.Freeform
                        ? StrokeArgb[objectIndex]
                        : Argb[objectIndex]);
                count++;
                atomCount += AtomCount[objectIndex];
                red += color.R;
                green += color.G;
                blue += color.B;
            }
        }
    }

    private void GetObjectSummaryCellRange(
        int objectIndex,
        int columns,
        int rows,
        out int minX,
        out int maxX,
        out int minY,
        out int maxY)
    {
        var halfWidth = Width[objectIndex] * 0.5f;
        var halfHeight = Height[objectIndex] * 0.5f;
        var cellWidth = StageWidth / columns;
        var cellHeight = StageHeight / rows;
        minX = (int)Math.Clamp((X[objectIndex] - halfWidth + StageWidth * 0.5f) / cellWidth, 0, columns - 1);
        maxX = (int)Math.Clamp((X[objectIndex] + halfWidth + StageWidth * 0.5f) / cellWidth, 0, columns - 1);
        minY = (int)Math.Clamp((Y[objectIndex] - halfHeight + StageHeight * 0.5f) / cellHeight, 0, rows - 1);
        maxY = (int)Math.Clamp((Y[objectIndex] + halfHeight + StageHeight * 0.5f) / cellHeight, 0, rows - 1);
    }

    private void AddObjectToSummaryCell(
        int objectIndex,
        Color color,
        int columns,
        int rows,
        int[] counts,
        long[] atoms,
        int[] colors,
        int alphaBase,
        int alphaStep)
    {
        var x = (int)Math.Clamp((X[objectIndex] + StageWidth * 0.5f) / StageWidth * columns, 0, columns - 1);
        var y = (int)Math.Clamp((Y[objectIndex] + StageHeight * 0.5f) / StageHeight * rows, 0, rows - 1);
        AddObjectToSummaryCell(objectIndex, color, y * columns + x, counts, atoms, colors, alphaBase, alphaStep);
    }

    private void AddObjectToSummaryCell(
        int objectIndex,
        Color color,
        int cell,
        int[] counts,
        long[] atoms,
        int[] colors,
        int alphaBase,
        int alphaStep)
    {
        var oldCount = counts[cell];
        var oldColor = oldCount > 0 ? Color.FromArgb(colors[cell]) : Color.Empty;
        var nextCount = oldCount + 1;
        counts[cell] = nextCount;
        atoms[cell] += AtomCount[objectIndex];
        var red = (oldColor.R * oldCount + color.R) / nextCount;
        var green = (oldColor.G * oldCount + color.G) / nextCount;
        var blue = (oldColor.B * oldCount + color.B) / nextCount;
        var alpha = Math.Clamp(alphaBase + nextCount * alphaStep, alphaBase + alphaStep, 230);
        colors[cell] = Color.FromArgb(alpha, red, green, blue).ToArgb();
    }

    private void AddObjectToSummaryRange(
        int objectIndex,
        Color color,
        int columns,
        int rows,
        int[] counts,
        long[] atoms,
        int[] colors,
        int alphaBase,
        int alphaStep)
    {
        var halfWidth = Width[objectIndex] * 0.5f;
        var halfHeight = Height[objectIndex] * 0.5f;
        var cellWidth = StageWidth / columns;
        var cellHeight = StageHeight / rows;
        var minX = (int)Math.Clamp((X[objectIndex] - halfWidth + StageWidth * 0.5f) / cellWidth, 0, columns - 1);
        var maxX = (int)Math.Clamp((X[objectIndex] + halfWidth + StageWidth * 0.5f) / cellWidth, 0, columns - 1);
        var minY = (int)Math.Clamp((Y[objectIndex] - halfHeight + StageHeight * 0.5f) / cellHeight, 0, rows - 1);
        var maxY = (int)Math.Clamp((Y[objectIndex] + halfHeight + StageHeight * 0.5f) / cellHeight, 0, rows - 1);

        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                var cell = y * columns + x;
                AddObjectToSummaryCell(objectIndex, color, cell, counts, atoms, colors, alphaBase, alphaStep);
            }
        }
    }

    private void AddObjectBoundsToSummary(
        int objectIndex,
        Color color,
        int columns,
        int rows,
        int[] counts,
        long[] atoms,
        long[] red,
        long[] green,
        long[] blue)
    {
        var halfWidth = Width[objectIndex] * 0.5f;
        var halfHeight = Height[objectIndex] * 0.5f;
        var cellWidth = StageWidth / columns;
        var cellHeight = StageHeight / rows;
        var minX = (int)Math.Clamp((X[objectIndex] - halfWidth + StageWidth * 0.5f) / cellWidth, 0, columns - 1);
        var maxX = (int)Math.Clamp((X[objectIndex] + halfWidth + StageWidth * 0.5f) / cellWidth, 0, columns - 1);
        var minY = (int)Math.Clamp((Y[objectIndex] - halfHeight + StageHeight * 0.5f) / cellHeight, 0, rows - 1);
        var maxY = (int)Math.Clamp((Y[objectIndex] + halfHeight + StageHeight * 0.5f) / cellHeight, 0, rows - 1);

        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                var cell = y * columns + x;
                counts[cell]++;
                atoms[cell] += AtomCount[objectIndex];
                red[cell] += color.R;
                green[cell] += color.G;
                blue[cell] += color.B;
            }
        }
    }

    private void AddObjectCenterToSummary(
        int objectIndex,
        Color color,
        int columns,
        int rows,
        int[] counts,
        long[] atoms,
        long[] red,
        long[] green,
        long[] blue)
    {
        var x = (int)Math.Clamp((X[objectIndex] + StageWidth * 0.5f) / StageWidth * columns, 0, columns - 1);
        var y = (int)Math.Clamp((Y[objectIndex] + StageHeight * 0.5f) / StageHeight * rows, 0, rows - 1);
        var cell = y * columns + x;
        counts[cell]++;
        atoms[cell] += AtomCount[objectIndex];
        red[cell] += color.R;
        green[cell] += color.G;
        blue[cell] += color.B;
    }

    private int CellForWorld(float x, float y)
    {
        var ix = (int)((x + StageWidth * 0.5f) / StageWidth * IndexColumns);
        var iy = (int)((y + StageHeight * 0.5f) / StageHeight * IndexRows);
        if ((uint)ix >= IndexColumns || (uint)iy >= IndexRows) return -1;
        return iy * IndexColumns + ix;
    }

    private void GetHitTestRange(PointF world, float toleranceWorld, out int minX, out int maxX, out int minY, out int maxY)
    {
        var expanded = MaxHalfExtent + Math.Max(4, toleranceWorld);
        var cellW = StageWidth / IndexColumns;
        var cellH = StageHeight / IndexRows;
        minX = (int)Math.Clamp((world.X - expanded + StageWidth * 0.5f) / cellW, 0, IndexColumns - 1);
        maxX = (int)Math.Clamp((world.X + expanded + StageWidth * 0.5f) / cellW, 0, IndexColumns - 1);
        minY = (int)Math.Clamp((world.Y - expanded + StageHeight * 0.5f) / cellH, 0, IndexRows - 1);
        maxY = (int)Math.Clamp((world.Y + expanded + StageHeight * 0.5f) / cellH, 0, IndexRows - 1);
    }

    private void AddObjectToTileSummary(int i, Color color, long[] tileR, long[] tileG, long[] tileB)
    {
        var tx = (int)Math.Clamp((X[i] + StageWidth * 0.5f) / StageWidth * TileColumns, 0, TileColumns - 1);
        var ty = (int)Math.Clamp((Y[i] + StageHeight * 0.5f) / StageHeight * TileRows, 0, TileRows - 1);
        var tile = ty * TileColumns + tx;
        TileCount[tile]++;
        TileAtoms[tile] += AtomCount[i];
        tileR[tile] += color.R;
        tileG[tile] += color.G;
        tileB[tile] += color.B;
    }

    private void AddObjectToOverviewSummary(int i, Color color, long[] overviewR, long[] overviewG, long[] overviewB)
    {
        var tx = (int)Math.Clamp((X[i] + StageWidth * 0.5f) / StageWidth * OverviewColumns, 0, OverviewColumns - 1);
        var ty = (int)Math.Clamp((Y[i] + StageHeight * 0.5f) / StageHeight * OverviewRows, 0, OverviewRows - 1);
        var tile = ty * OverviewColumns + tx;
        OverviewCount[tile]++;
        OverviewAtoms[tile] += AtomCount[i];
        overviewR[tile] += color.R;
        overviewG[tile] += color.G;
        overviewB[tile] += color.B;
    }

    private void FinalizeTileSummary(long[] tileR, long[] tileG, long[] tileB)
    {
        for (var i = 0; i < TileCount.Length; i++)
        {
            if (TileCount[i] == 0)
            {
                TileArgb[i] = Color.FromArgb(24, 36, 40, 42).ToArgb();
                continue;
            }

            var count = TileCount[i];
            var alpha = Math.Clamp(44 + count * 9, 48, 230);
            TileArgb[i] = Color.FromArgb(alpha, (int)(tileR[i] / count), (int)(tileG[i] / count), (int)(tileB[i] / count)).ToArgb();
        }
    }

    private void FinalizeOverviewSummary(long[] overviewR, long[] overviewG, long[] overviewB)
    {
        for (var i = 0; i < OverviewCount.Length; i++)
        {
            if (OverviewCount[i] == 0)
            {
                OverviewArgb[i] = Color.FromArgb(24, 36, 40, 42).ToArgb();
                continue;
            }

            var count = OverviewCount[i];
            var alpha = Math.Clamp(58 + count * 6, 64, 230);
            OverviewArgb[i] = Color.FromArgb(alpha, (int)(overviewR[i] / count), (int)(overviewG[i] / count), (int)(overviewB[i] / count)).ToArgb();
        }
    }

}
