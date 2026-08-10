using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Clipper2Lib;

namespace VectorAnimationEngine;

internal sealed partial class VectorScene
{
    public bool HasGradient(int objectIndex)
    {
        return (uint)objectIndex < ObjectCount
            && LinearGradientEnabled.Length > objectIndex
            && LinearGradientEnabled[objectIndex]
            && GradientKinds.Length > objectIndex
            && GradientKinds[objectIndex] is GradientKind.Linear or GradientKind.Radial or GradientKind.ShapeRadial
            && (GradientKinds[objectIndex] != GradientKind.ShapeRadial || ShapeKind[objectIndex] != VectorAnimationEngine.ShapeKind.Line)
            && SupportsGradient(ShapeKind[objectIndex]);
    }

    public bool HasLinearGradient(int objectIndex) => HasGradient(objectIndex) && GradientKinds[objectIndex] == GradientKind.Linear;

    public GradientKind GetGradientKind(int objectIndex) => HasGradient(objectIndex) ? GradientKinds[objectIndex] : GradientKind.Solid;

    public PointF GetGradientStart(int objectIndex) => new(GradientStartX[objectIndex], GradientStartY[objectIndex]);

    public PointF GetGradientEnd(int objectIndex) => new(GradientEndX[objectIndex], GradientEndY[objectIndex]);

    public GradientStop[] GetGradientStops(int objectIndex)
    {
        if (!HasGradient(objectIndex)) return [];
        return _gradientStops.TryGetValue(objectIndex, out var stops)
            ? stops.ToArray()
            : [new GradientStop(0, GradientStartArgb[objectIndex]), new GradientStop(1, GradientEndArgb[objectIndex])];
    }

    public bool HasGradientPath(int objectIndex) => HasGradient(objectIndex)
        && GradientKinds[objectIndex] == GradientKind.Linear
        && _gradientPathLocalPoints.TryGetValue(objectIndex, out var points)
        && points.Length >= 2;

    public bool TryGetGradientPathWorldPoints(int objectIndex, out PointF[] points)
    {
        if (!HasGradientPath(objectIndex) || !_gradientPathLocalPoints.TryGetValue(objectIndex, out var localPoints))
        {
            points = Array.Empty<PointF>();
            return false;
        }

        points = new PointF[localPoints.Length];
        for (var index = 0; index < localPoints.Length; index++)
        {
            points[index] = new PointF(X[objectIndex] + localPoints[index].X, Y[objectIndex] + localPoints[index].Y);
        }

        return true;
    }

    internal bool TryGetGradientPathLocalPoints(int objectIndex, out PointF[] points)
    {
        if (HasGradientPath(objectIndex)
            && _gradientPathLocalPoints.TryGetValue(objectIndex, out var localPoints))
        {
            points = localPoints;
            return true;
        }

        points = Array.Empty<PointF>();
        return false;
    }

    public void SetGradientPath(int objectIndex, IReadOnlyList<PointF> worldPoints)
    {
        SetGradientPathCore(objectIndex, worldPoints, preserveTrajectoryOrder: false);
    }

    public void SetOrderedGradientPath(int objectIndex, IReadOnlyList<PointF> worldPoints)
    {
        SetGradientPathCore(objectIndex, worldPoints, preserveTrajectoryOrder: true);
    }

    private void SetGradientPathCore(
        int objectIndex,
        IReadOnlyList<PointF> worldPoints,
        bool preserveTrajectoryOrder)
    {
        if ((uint)objectIndex >= ObjectCount
            || ShapeKind[objectIndex] != VectorAnimationEngine.ShapeKind.Path
            || !HasGradient(objectIndex)
            || GradientKinds[objectIndex] != GradientKind.Linear)
        {
            ClearGradientPath(objectIndex);
            return;
        }

        var points = NormalizeFreehandPoints(worldPoints);
        if (points.Length < 2)
        {
            ClearGradientPath(objectIndex);
            return;
        }

        if (!preserveTrajectoryOrder
            && GradientPaintUtilities.TryCreateSelfIntersectionFallbackAxis(points, out var fallbackAxis))
        {
            GradientStartX[objectIndex] = VectorUnits.Quantize(fallbackAxis.Start.X);
            GradientStartY[objectIndex] = VectorUnits.Quantize(fallbackAxis.Start.Y);
            GradientEndX[objectIndex] = VectorUnits.Quantize(fallbackAxis.End.X);
            GradientEndY[objectIndex] = VectorUnits.Quantize(fallbackAxis.End.Y);
            ClearGradientPath(objectIndex);
            return;
        }

        _gradientPathLocalPoints[objectIndex] = points
            .Select(point => new PointF(
                VectorUnits.Quantize(point.X - X[objectIndex]),
                VectorUnits.Quantize(point.Y - Y[objectIndex])))
            .ToArray();
    }

    public void ClearGradientPath(int objectIndex) => _gradientPathLocalPoints.Remove(objectIndex);

    public bool TryGetShapeGradientMappingWorldContours(int objectIndex, out PointF[][] contours)
    {
        if ((uint)objectIndex >= ObjectCount
            || GetGradientKind(objectIndex) != GradientKind.ShapeRadial
            || !_shapeGradientMappingLocalContours.TryGetValue(objectIndex, out var localContours))
        {
            contours = Array.Empty<PointF[]>();
            return false;
        }

        contours = new PointF[localContours.Length][];
        for (var contourIndex = 0; contourIndex < localContours.Length; contourIndex++)
        {
            var local = localContours[contourIndex];
            var world = new PointF[local.Length];
            for (var pointIndex = 0; pointIndex < local.Length; pointIndex++)
            {
                world[pointIndex] = LocalToWorld(objectIndex, local[pointIndex].X, local[pointIndex].Y);
            }
            contours[contourIndex] = world;
        }

        return contours.Length > 0;
    }

    internal bool TryGetShapeGradientMappingLocalContours(int objectIndex, out PointF[][] contours)
    {
        if ((uint)objectIndex < ObjectCount
            && GetGradientKind(objectIndex) == GradientKind.ShapeRadial
            && _shapeGradientMappingLocalContours.TryGetValue(objectIndex, out var localContours))
        {
            contours = localContours;
            return contours.Length > 0;
        }

        contours = Array.Empty<PointF[]>();
        return false;
    }

    public PointF[][] GetShapeGradientMappingContours(int objectIndex)
    {
        return TryGetShapeGradientMappingWorldContours(objectIndex, out var contours)
            ? contours
            : GetObjectBoundaryContours(objectIndex);
    }

    public void SetShapeGradientMapping(int objectIndex, IReadOnlyList<PointF[]> worldContours)
    {
        if ((uint)objectIndex >= ObjectCount || GetGradientKind(objectIndex) != GradientKind.ShapeRadial)
        {
            ClearShapeGradientMapping(objectIndex);
            return;
        }

        var contours = NormalizePathContours(worldContours);
        if (contours.Length == 0)
        {
            ClearShapeGradientMapping(objectIndex);
            return;
        }

        _shapeGradientMappingLocalContours[objectIndex] = contours
            .Select(contour => contour
                .Select(point => VectorUnits.Quantize(WorldToLocal(objectIndex, point)))
                .ToArray())
            .ToArray();
    }

    public void ClearShapeGradientMapping(int objectIndex) => _shapeGradientMappingLocalContours.Remove(objectIndex);

    public float EstimateGradientPathStrokeWidth(int objectIndex)
    {
        if (!TryGetGradientPathWorldPoints(objectIndex, out var points)) return 0;
        var length = 0f;
        for (var index = 1; index < points.Length; index++) length += Distance(points[index - 1], points[index]);
        if (length <= 0.001f) return 0;

        var contours = FillWorldContours(objectIndex);
        var areaWidth = Math.Abs(CompoundContourArea(contours)) / length;
        var coverageWidth = MaximumSampledBoundaryDistance(points, contours) * 2f;
        return Math.Max(VectorUnits.FromPixels(0.5f), Math.Max(areaWidth, coverageWidth));
    }

    private static float MaximumSampledBoundaryDistance(
        IReadOnlyList<PointF> path,
        IReadOnlyList<PointF[]> contours)
    {
        var boundaryPointCount = contours.Sum(contour => contour.Length);
        if (boundaryPointCount == 0) return 0;

        var stride = Math.Max(
            1,
            (int)Math.Ceiling(boundaryPointCount / (double)MaximumGradientPathCoverageSamples));
        var maximumDistance = 0f;
        var globalPointIndex = 0;
        foreach (var contour in contours)
        {
            for (var pointIndex = 0; pointIndex < contour.Length; pointIndex++, globalPointIndex++)
            {
                if (globalPointIndex % stride != 0
                    && pointIndex != 0
                    && pointIndex != contour.Length - 1)
                {
                    continue;
                }

                maximumDistance = Math.Max(
                    maximumDistance,
                    DistanceToPolyline(contour[pointIndex], path));
            }
        }

        return maximumDistance;
    }

    private GradientPaintData? CaptureGradientPaint(int objectIndex)
    {
        return HasGradient(objectIndex)
            ? new GradientPaintData(
                GradientKinds[objectIndex],
                GetGradientStops(objectIndex),
                GetGradientStart(objectIndex),
                GetGradientEnd(objectIndex),
                TryGetGradientPathWorldPoints(objectIndex, out var path) ? path : Array.Empty<PointF>(),
                TryGetShapeGradientMappingWorldContours(objectIndex, out var mappingContours)
                    ? mappingContours
                    : Array.Empty<PointF[]>())
            : null;
    }

    private void ApplyGradientPaint(int objectIndex, GradientPaintData? gradientPaint)
    {
        if (gradientPaint is not { } paint) return;
        SetGradientPaint(objectIndex, paint.Kind, paint.Stops, paint.Start, paint.End);
        if (paint.Path.Length > 1) SetGradientPath(objectIndex, paint.Path);
        if (paint.ShapeMappingContours.Length > 0) SetShapeGradientMapping(objectIndex, paint.ShapeMappingContours);
    }

    public void SetLinearGradient(int objectIndex, Color startColor, Color endColor, PointF? start = null, PointF? end = null)
    {
        SetGradientPaint(
            objectIndex,
            GradientKind.Linear,
            [new GradientStop(0, startColor), new GradientStop(1, endColor)],
            start,
            end);
    }

    public void SetRadialGradient(int objectIndex, Color centerColor, Color edgeColor, PointF? center = null, PointF? radiusPoint = null)
    {
        SetGradientPaint(
            objectIndex,
            GradientKind.Radial,
            [new GradientStop(0, centerColor), new GradientStop(1, edgeColor)],
            center,
            radiusPoint);
    }

    public void SetShapeRadialGradient(int objectIndex, Color centerColor, Color edgeColor, PointF? center = null)
    {
        SetGradientPaint(
            objectIndex,
            GradientKind.ShapeRadial,
            [new GradientStop(0, centerColor), new GradientStop(1, edgeColor)],
            center);
    }

    public void SetGradientPaint(
        int objectIndex,
        GradientKind kind,
        IReadOnlyList<GradientStop> stops,
        PointF? start = null,
        PointF? end = null)
    {
        if ((uint)objectIndex >= ObjectCount
            || !SupportsGradient(ShapeKind[objectIndex])
            || kind == GradientKind.ShapeRadial && ShapeKind[objectIndex] == VectorAnimationEngine.ShapeKind.Line)
        {
            return;
        }
        if (kind == GradientKind.Solid)
        {
            DisableLinearGradient(objectIndex);
            return;
        }

        var normalizedStops = NormalizeGradientStops(stops);
        PointF defaultStart;
        PointF defaultEnd;
        if (ShapeKind[objectIndex] == VectorAnimationEngine.ShapeKind.Line)
        {
            // A line's width/angle describe its bounds, not the direction of a
            // curved stroke. Use its real endpoints so vertical and diagonal
            // lines do not collapse a default linear gradient into one color.
            var curve = LineCurve(objectIndex);
            if (kind == GradientKind.Radial)
            {
                defaultStart = CubicPoint(curve.Start, curve.Control1, curve.Control2, curve.End, 0.5f);
                defaultEnd = curve.End;
            }
            else
            {
                defaultStart = curve.Start;
                defaultEnd = curve.End;
            }
        }
        else if (kind is GradientKind.Radial or GradientKind.ShapeRadial)
        {
            defaultStart = new PointF(X[objectIndex], Y[objectIndex]);
            defaultEnd = new PointF(X[objectIndex] + Math.Max(Width[objectIndex], Height[objectIndex]) * 0.5f, Y[objectIndex]);
        }
        else
        {
            defaultStart = new PointF(X[objectIndex] - Width[objectIndex] * 0.5f, Y[objectIndex]);
            defaultEnd = new PointF(X[objectIndex] + Width[objectIndex] * 0.5f, Y[objectIndex]);
        }

        var resolvedStart = start ?? defaultStart;
        var resolvedEnd = end ?? defaultEnd;
        if (kind == GradientKind.ShapeRadial
            && end is null
            && GradientPaintUtilities.TryFindShapeBoundaryPoint(
                GetObjectBoundaryContours(objectIndex),
                resolvedStart,
                new PointF(1f, 0f),
                out var shapeBoundary))
        {
            resolvedEnd = shapeBoundary;
        }

        LinearGradientEnabled[objectIndex] = true;
        GradientKinds[objectIndex] = kind;
        if (kind != GradientKind.Linear) ClearGradientPath(objectIndex);
        if (kind != GradientKind.ShapeRadial) ClearShapeGradientMapping(objectIndex);
        _gradientStops[objectIndex] = normalizedStops;
        GradientStartArgb[objectIndex] = normalizedStops[0].Argb;
        GradientEndArgb[objectIndex] = normalizedStops[^1].Argb;
        SetLinearGradientEndpoints(objectIndex, resolvedStart, resolvedEnd);
    }

    public void SetLinearGradientColors(int objectIndex, Color startColor, Color endColor)
    {
        if (!HasGradient(objectIndex)) return;
        var stops = GetGradientStops(objectIndex);
        stops[0] = new GradientStop(stops[0].Position, startColor);
        stops[^1] = new GradientStop(stops[^1].Position, endColor);
        SetGradientStops(objectIndex, stops);
    }

    public void SetGradientStops(int objectIndex, IReadOnlyList<GradientStop> stops)
    {
        if (!HasGradient(objectIndex)) return;
        var normalizedStops = NormalizeGradientStops(stops);
        _gradientStops[objectIndex] = normalizedStops;
        GradientStartArgb[objectIndex] = normalizedStops[0].Argb;
        GradientEndArgb[objectIndex] = normalizedStops[^1].Argb;
    }

    public void SetLinearGradientEndpoints(int objectIndex, PointF start, PointF end)
    {
        if ((uint)objectIndex >= ObjectCount) return;
        GradientStartX[objectIndex] = VectorUnits.Quantize(start.X);
        GradientStartY[objectIndex] = VectorUnits.Quantize(start.Y);
        GradientEndX[objectIndex] = VectorUnits.Quantize(end.X);
        GradientEndY[objectIndex] = VectorUnits.Quantize(end.Y);
    }

    public void DisableLinearGradient(int objectIndex)
    {
        if ((uint)objectIndex >= ObjectCount) return;
        LinearGradientEnabled[objectIndex] = false;
        GradientKinds[objectIndex] = GradientKind.Solid;
        _gradientStops.Remove(objectIndex);
        ClearGradientPath(objectIndex);
        ClearShapeGradientMapping(objectIndex);
    }

    private static GradientStop[] NormalizeGradientStops(IReadOnlyList<GradientStop> stops)
    {
        ArgumentNullException.ThrowIfNull(stops);
        var normalized = stops
            .Select(stop => new GradientStop(Math.Clamp(stop.Position, 0f, 1f), stop.Argb))
            .OrderBy(stop => stop.Position)
            .GroupBy(stop => stop.Position)
            .Select(group => group.Last())
            .ToList();
        if (normalized.Count == 0) normalized.Add(new GradientStop(0, Color.White));
        if (normalized.Count == 1) normalized.Add(new GradientStop(1, normalized[0].Argb));
        if (normalized[0].Position > 0) normalized.Insert(0, new GradientStop(0, normalized[0].Argb));
        if (normalized[^1].Position < 1) normalized.Add(new GradientStop(1, normalized[^1].Argb));
        return normalized.ToArray();
    }

}
