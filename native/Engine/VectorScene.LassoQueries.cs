using System.Drawing;

namespace VectorAnimationEngine;

internal sealed partial class VectorScene
{
    private const float LassoMinimumArea = 0.5f;

    public int[] QueryDrawingObjectsInsidePolygon(
        IReadOnlyList<PointF> worldPolygon,
        int frame,
        int limit = 100_000)
    {
        if (ObjectCount <= 0 || limit <= 0
            || !TryNormalizeLassoPolygon(worldPolygon, out var polygon, out var bounds))
        {
            return Array.Empty<int>();
        }

        // The spatial index is used only for the broad phase. The full object
        // geometry is checked below so concave lassos cannot select by AABB.
        var candidates = QueryObjects(bounds, frame, int.MaxValue);
        var result = new List<int>(Math.Min(candidates.Length, Math.Min(ObjectCount, 1024)));
        foreach (var objectIndex in candidates)
        {
            if (!IsObjectGeometryInsidePolygonCore(objectIndex, polygon)) continue;
            result.Add(objectIndex);
            if (result.Count >= limit) break;
        }

        return result.ToArray();
    }

    public DrawingElementHit[] QueryDrawingElementsInsidePolygon(
        IReadOnlyList<PointF> worldPolygon,
        int frame,
        int limit = 100_000)
    {
        if (ObjectCount <= 0 || limit <= 0
            || !TryNormalizeLassoPolygon(worldPolygon, out var polygon, out var bounds))
        {
            return Array.Empty<DrawingElementHit>();
        }

        var candidates = QueryObjects(bounds, frame, int.MaxValue);
        var result = new List<DrawingElementHit>();
        foreach (var objectIndex in candidates)
        {
            AppendDrawingElementsInsidePolygon(objectIndex, frame, polygon, result);
            if (result.Count >= limit) break;
        }

        return result.Count <= limit
            ? result.ToArray()
            : result.Take(limit).ToArray();
    }

    public bool IsObjectGeometryInsidePolygon(
        int objectIndex,
        IReadOnlyList<PointF> worldPolygon)
    {
        return TryNormalizeLassoPolygon(worldPolygon, out var polygon, out _)
            && IsObjectGeometryInsidePolygonCore(objectIndex, polygon);
    }

    internal static bool AreContoursFullyInsidePolygon(
        IReadOnlyList<PointF[]> contours,
        IReadOnlyList<PointF> worldPolygon,
        bool closeContours)
    {
        return TryNormalizeLassoPolygon(worldPolygon, out var polygon, out _)
            && LassoContoursInsidePolygon(contours, polygon, closeContours);
    }

    private bool IsObjectGeometryInsidePolygonCore(int objectIndex, PointF[] polygon)
    {
        if ((uint)objectIndex >= ObjectCount) return false;

        var shape = ShapeKind[objectIndex];
        if (shape == VectorAnimationEngine.ShapeKind.MixingStroke)
        {
            return IsMixingStrokeInsidePolygon(objectIndex, polygon);
        }

        if (_objectDistortions.ContainsKey(objectIndex))
        {
            var distortedContours = GetDistortedObjectBoundaryContours(objectIndex);
            return IsTopologyStrokeShape(shape)
                ? LassoContoursInsidePolygon(distortedContours, polygon, closeContours: false)
                : LassoContoursInsidePolygon(distortedContours, polygon, closeContours: true);
        }

        if (IsTopologyStrokeShape(shape))
        {
            var samples = StrokeSamples(objectIndex);
            return LassoPolylineInsidePolygon(
                samples.Select(sample => sample.Point).ToArray(),
                polygon,
                close: false);
        }

        return LassoContoursInsidePolygon(
            ShapeBoundaryContours(objectIndex),
            polygon,
            closeContours: true);
    }

    private void AppendDrawingElementsInsidePolygon(
        int objectIndex,
        int frame,
        PointF[] polygon,
        List<DrawingElementHit> result)
    {
        if ((uint)objectIndex >= ObjectCount) return;

        var shape = ShapeKind[objectIndex];
        if (shape == VectorAnimationEngine.ShapeKind.MixingStroke)
        {
            AppendMixingStrokeElementsInsidePolygon(objectIndex, polygon, result);
            return;
        }

        // Distorted geometry is already sampled in display/world coordinates.
        // Keep the element kind stable while avoiding source-space containment.
        if (_objectDistortions.ContainsKey(objectIndex))
        {
            var contours = GetDistortedObjectBoundaryContours(objectIndex);
            var inside = IsTopologyStrokeShape(shape)
                ? LassoContoursInsidePolygon(contours, polygon, closeContours: false)
                : LassoContoursInsidePolygon(contours, polygon, closeContours: true);
            if (!inside) return;

            if (IsTopologyStrokeShape(shape))
            {
                if (HasStroke(objectIndex))
                {
                    result.Add(new DrawingElementHit(
                        new DrawingElementKey(objectIndex, DrawingElementKind.Stroke, 0),
                        0,
                        0,
                        1));
                }
                return;
            }

            if (HasFill(objectIndex))
            {
                result.Add(new DrawingElementHit(
                    new DrawingElementKey(objectIndex, DrawingElementKind.Fill, 0),
                    0,
                    0,
                    1));
            }
            if (HasStroke(objectIndex))
            {
                result.Add(new DrawingElementHit(
                    new DrawingElementKey(objectIndex, DrawingElementKind.BoundaryStroke, 0),
                    0,
                    0,
                    1));
            }
            return;
        }

        if (IsWholeObjectShape(shape))
        {
            if (IsObjectGeometryInsidePolygonCore(objectIndex, polygon))
            {
                result.Add(new DrawingElementHit(
                    new DrawingElementKey(objectIndex, DrawingElementKind.Fill, 0),
                    0,
                    0,
                    1));
            }
            return;
        }

        var topologyCandidates = CollectTopologyCandidates(objectIndex, frame);
        if (IsTopologyStrokeShape(shape))
        {
            if (!HasStroke(objectIndex)) return;
            var splits = StrokeSplitParameters(objectIndex, topologyCandidates);
            if (shape == VectorAnimationEngine.ShapeKind.Line)
            {
                var curve = LineCurve(objectIndex);
                foreach (var part in BuildCurveParts(
                             curve.Start,
                             curve.Control1,
                             curve.Control2,
                             curve.End,
                             splits))
                {
                    var partCurve = new CubicBoundarySegment(
                        part.Start,
                        part.Control1,
                        part.Control2,
                        part.End);
                    if (!LassoCubicCurveInsidePolygon(partCurve, polygon)) continue;

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
                    if (!LassoPolylineInsidePolygon(part.Points, polygon, close: false)) continue;
                    result.Add(new DrawingElementHit(
                        new DrawingElementKey(objectIndex, DrawingElementKind.Stroke, part.PartIndex),
                        0,
                        part.StartT,
                        part.EndT));
                }
            }

            return;
        }

        if (HasFill(objectIndex))
        {
            var regions = BuildFillRegions(objectIndex, frame, topologyCandidates);
            for (var part = 0; part < regions.Count; part++)
            {
                if (!LassoContoursInsidePolygon(regions[part].Contours, polygon, closeContours: true)) continue;
                result.Add(new DrawingElementHit(
                    new DrawingElementKey(objectIndex, DrawingElementKind.Fill, part),
                    0,
                    0,
                    1));
            }
        }

        if (!HasStroke(objectIndex)) return;
        foreach (var part in BuildBoundaryStrokeParts(objectIndex, topologyCandidates))
        {
            if (!LassoPolylineInsidePolygon(part.Points, polygon, close: false)) continue;
            result.Add(new DrawingElementHit(
                new DrawingElementKey(objectIndex, DrawingElementKind.BoundaryStroke, part.PartIndex),
                0,
                part.StartT,
                part.EndT));
        }
    }

    private void AppendMixingStrokeElementsInsidePolygon(
        int objectIndex,
        PointF[] polygon,
        List<DrawingElementHit> result)
    {
        if (TryGetMixingBrushLocalRegion(objectIndex, out var region))
        {
            for (var partIndex = 0; partIndex < region.ConnectedComponentCount; partIndex++)
            {
                if (!TryGetMixingBrushLocalRegionPart(objectIndex, partIndex, out var component)
                    || !IsMixingComponentInsidePolygon(objectIndex, component, polygon))
                {
                    continue;
                }

                result.Add(new DrawingElementHit(
                    new DrawingElementKey(objectIndex, DrawingElementKind.Fill, partIndex),
                    0,
                    0,
                    1));
            }
            return;
        }

        if (IsMixingStrokeInsidePolygon(objectIndex, polygon))
        {
            result.Add(new DrawingElementHit(
                new DrawingElementKey(objectIndex, DrawingElementKind.Fill, 0),
                0,
                0,
                1));
        }
    }

    private bool IsMixingStrokeInsidePolygon(int objectIndex, PointF[] polygon)
    {
        if (TryGetMixingBrushLocalRegion(objectIndex, out var region))
        {
            var componentCount = region.ConnectedComponentCount;
            if (componentCount <= 0) return false;
            for (var partIndex = 0; partIndex < componentCount; partIndex++)
            {
                if (!region.TryGetConnectedComponent(partIndex, out var component)
                    || !IsMixingComponentInsidePolygon(objectIndex, component, polygon))
                {
                    return false;
                }
            }
            return true;
        }

        if (!_mixingStrokeLocalSamples.TryGetValue(objectIndex, out var samples)
            || samples.Length == 0)
        {
            return false;
        }

        var hasPaint = false;
        foreach (var sample in samples)
        {
            if (((uint)sample.Argb >> 24) == 0
                || !float.IsFinite(sample.Point.X)
                || !float.IsFinite(sample.Point.Y)
                || !float.IsFinite(sample.Diameter)
                || sample.Diameter <= 0)
            {
                continue;
            }

            var center = LocalToWorld(objectIndex, sample.Point.X, sample.Point.Y);
            if (!Finite(center) || !LassoDiskInsidePolygon(center, sample.Diameter * 0.5f, polygon))
            {
                return false;
            }
            hasPaint = true;
        }

        return hasPaint;
    }

    private bool IsMixingComponentInsidePolygon(
        int objectIndex,
        MixingBrushRegionData component,
        PointF[] polygon)
    {
        var localContours = component.GetBoundaryContours();
        if (localContours.Length == 0) return false;
        var distortions = _objectDistortions.TryGetValue(objectIndex, out var values)
            ? values
            : Array.Empty<DistortWarp>();
        var worldContours = new PointF[localContours.Length][];
        for (var contourIndex = 0; contourIndex < localContours.Length; contourIndex++)
        {
            var local = localContours[contourIndex];
            var world = local
                .Select(point => LocalToWorld(objectIndex, point.X, point.Y))
                .ToArray();
            worldContours[contourIndex] = distortions.Length == 0
                ? world
                : MapObjectContour(world, distortions);
        }

        return LassoContoursInsidePolygon(worldContours, polygon, closeContours: true);
    }

    private static bool TryNormalizeLassoPolygon(
        IReadOnlyList<PointF> worldPolygon,
        out PointF[] polygon,
        out RectangleF bounds)
    {
        polygon = Array.Empty<PointF>();
        bounds = RectangleF.Empty;
        if (worldPolygon is null || worldPolygon.Count < 3) return false;

        var points = new List<PointF>(worldPolygon.Count);
        foreach (var source in worldPolygon)
        {
            if (!Finite(source)) return false;
            if (points.Count == 0 || !SameDrawingUnit(points[^1], source)) points.Add(source);
        }

        if (points.Count > 1 && SameDrawingUnit(points[0], points[^1])) points.RemoveAt(points.Count - 1);
        if (points.Count < 3) return false;

        var area = PolygonArea(points);
        if (!float.IsFinite(area) || Math.Abs(area) < LassoMinimumArea) return false;

        var left = points[0].X;
        var right = left;
        var top = points[0].Y;
        var bottom = top;
        for (var index = 1; index < points.Count; index++)
        {
            var point = points[index];
            left = Math.Min(left, point.X);
            right = Math.Max(right, point.X);
            top = Math.Min(top, point.Y);
            bottom = Math.Max(bottom, point.Y);
        }

        bounds = RectangleF.FromLTRB(left, top, right, bottom);
        if (bounds.Width <= 0.001f || bounds.Height <= 0.001f) return false;
        polygon = points.ToArray();
        return true;
    }

    private static bool LassoContoursInsidePolygon(
        IReadOnlyList<PointF[]> contours,
        PointF[] polygon,
        bool closeContours)
    {
        if (contours is null || contours.Count == 0) return false;
        foreach (var contour in contours)
        {
            if (!LassoPolylineInsidePolygon(contour, polygon, closeContours)) return false;
        }
        return true;
    }

    private static bool LassoPolylineInsidePolygon(
        IReadOnlyList<PointF> points,
        PointF[] polygon,
        bool close)
    {
        if (points is null || points.Count == 0) return false;
        for (var index = 0; index < points.Count; index++)
        {
            if (!Finite(points[index]) || !PointInPolygonOrOnBoundary(points[index], polygon)) return false;
        }

        for (var index = 0; index + 1 < points.Count; index++)
        {
            if (!LassoSegmentInsidePolygon(points[index], points[index + 1], polygon)) return false;
        }

        if (close && points.Count > 2
            && !LassoSegmentInsidePolygon(points[^1], points[0], polygon))
        {
            return false;
        }

        return true;
    }

    private static bool LassoCubicCurveInsidePolygon(
        CubicBoundarySegment curve,
        PointF[] polygon)
    {
        if (!Finite(curve.Start)
            || !Finite(curve.Control1)
            || !Finite(curve.Control2)
            || !Finite(curve.End))
        {
            return false;
        }

        return LassoPolylineInsidePolygon(
            SampleCubicSegmentWithParameters(curve)
                .Select(sample => sample.Point)
                .ToArray(),
            polygon,
            close: false);
    }

    private static bool LassoSegmentInsidePolygon(
        PointF start,
        PointF end,
        PointF[] polygon)
    {
        if (!PointInPolygonOrOnBoundary(start, polygon)
            || !PointInPolygonOrOnBoundary(end, polygon))
        {
            return false;
        }

        var parameters = new List<float>(polygon.Length + 2) { 0, 1 };
        for (var index = 0; index < polygon.Length; index++)
        {
            var edgeStart = polygon[index];
            var edgeEnd = polygon[(index + 1) % polygon.Length];
            var intersections = SegmentIntersectionParameters(
                start,
                end,
                edgeStart,
                edgeEnd,
                out var first,
                out var second);
            if (intersections <= 0) continue;
            parameters.Add(Math.Clamp(first, 0, 1));
            if (intersections > 1) parameters.Add(Math.Clamp(second, 0, 1));
        }

        parameters.Sort();
        var unique = new List<float>(parameters.Count);
        foreach (var parameter in parameters)
        {
            if (unique.Count == 0 || Math.Abs(unique[^1] - parameter) > 0.00001f)
            {
                unique.Add(parameter);
            }
        }

        for (var index = 0; index + 1 < unique.Count; index++)
        {
            var first = unique[index];
            var second = unique[index + 1];
            if (second - first <= 0.00001f) continue;
            var middle = Lerp(start, end, (first + second) * 0.5f);
            if (!PointInPolygonOrOnBoundary(middle, polygon)) return false;
        }

        return true;
    }

    private static bool LassoDiskInsidePolygon(
        PointF center,
        float radius,
        PointF[] polygon)
    {
        if (!PointInPolygonOrOnBoundary(center, polygon)) return false;
        if (!float.IsFinite(radius) || radius <= 0) return true;

        const int samples = 16;
        var boundary = new PointF[samples];
        for (var index = 0; index < samples; index++)
        {
            var angle = MathF.Tau * index / samples;
            boundary[index] = new PointF(
                center.X + MathF.Cos(angle) * radius,
                center.Y + MathF.Sin(angle) * radius);
        }

        return LassoPolylineInsidePolygon(boundary, polygon, close: true);
    }
}
