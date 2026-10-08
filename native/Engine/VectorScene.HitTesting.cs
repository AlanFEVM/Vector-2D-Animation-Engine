using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Clipper2Lib;

namespace VectorAnimationEngine;

internal sealed partial class VectorScene
{
    private bool ObjectMayContainHitPoint(int objectIndex, PointF world, float toleranceWorld)
    {
        if ((uint)objectIndex >= ObjectCount) return false;
        if (_objectDistortions.ContainsKey(objectIndex))
        {
            var distortedBounds = GetObjectWorldBounds(objectIndex);
            var distortedTolerance = Math.Max(0, toleranceWorld);
            distortedBounds.Inflate(distortedTolerance, distortedTolerance);
            return world.X >= distortedBounds.Left
                && world.X <= distortedBounds.Right
                && world.Y >= distortedBounds.Top
                && world.Y <= distortedBounds.Bottom;
        }

        var shape = ShapeKind[objectIndex];
        var margin = Math.Max(Stroke[objectIndex] * 0.5f, 1) + Math.Max(0, toleranceWorld);
        if (shape == VectorAnimationEngine.ShapeKind.Line)
        {
            var halfWidth = Width[objectIndex] * 0.5f;
            var angle = Angle[objectIndex];
            var offsetX = Math.Abs(angle) < 0.0001f ? halfWidth : halfWidth * MathF.Cos(angle);
            var offsetY = Math.Abs(angle) < 0.0001f ? 0 : halfWidth * MathF.Sin(angle);
            var start = new PointF(X[objectIndex] - offsetX, Y[objectIndex] - offsetY);
            var end = new PointF(X[objectIndex] + offsetX, Y[objectIndex] + offsetY);
            var control1 = new PointF(CurveControlX[objectIndex], CurveControlY[objectIndex]);
            var control2 = new PointF(CurveControl2X[objectIndex], CurveControl2Y[objectIndex]);
            var left = Math.Min(Math.Min(start.X, end.X), Math.Min(control1.X, control2.X)) - margin;
            var right = Math.Max(Math.Max(start.X, end.X), Math.Max(control1.X, control2.X)) + margin;
            var top = Math.Min(Math.Min(start.Y, end.Y), Math.Min(control1.Y, control2.Y)) - margin;
            var bottom = Math.Max(Math.Max(start.Y, end.Y), Math.Max(control1.Y, control2.Y)) + margin;
            return world.X >= left && world.X <= right && world.Y >= top && world.Y <= bottom;
        }

        var local = WorldToLocal(objectIndex, world);
        if (Math.Abs(local.X) <= Width[objectIndex] * 0.5f + margin
            && Math.Abs(local.Y) <= Height[objectIndex] * 0.5f + margin)
        {
            return true;
        }

        if (shape == VectorAnimationEngine.ShapeKind.Path || IsFreehandShape(shape))
        {
            var bounds = GetObjectWorldBounds(objectIndex);
            var tolerance = Math.Max(0, toleranceWorld);
            bounds.Inflate(tolerance, tolerance);
            return world.X >= bounds.Left
                && world.X <= bounds.Right
                && world.Y >= bounds.Top
                && world.Y <= bounds.Bottom;
        }

        return false;
    }

    private bool TryInverseMapObjectHitPoint(
        int objectIndex,
        PointF world,
        float toleranceWorld,
        out PointF sourceWorld)
    {
        sourceWorld = world;
        if ((uint)objectIndex >= ObjectCount) return false;
        if (!_objectDistortions.TryGetValue(objectIndex, out var distortions)) return true;

        var current = world;
        for (var index = distortions.Length - 1; index >= 0; index--)
        {
            var distortion = distortions[index];
            if (distortion.TryInverseMap(current, out var inverse))
            {
                current = inverse;
                continue;
            }

            var boundaryTolerance = Math.Max(0, toleranceWorld) + Math.Max(Stroke[objectIndex] * 0.5f, 1);
            if (!distortion.Envelope.TryFindNearestBoundary(current, boundaryTolerance, out var boundary)
                || !distortion.TryInverseMap(boundary.Point, out inverse))
            {
                return false;
            }
            current = inverse;
        }

        sourceWorld = current;
        return true;
    }

    private bool HitObject(PointF world, int i, float toleranceWorld)
    {
        var shape = ShapeKind.Length > i ? ShapeKind[i] : VectorAnimationEngine.ShapeKind.Rectangle;
        if (shape == VectorAnimationEngine.ShapeKind.MixingStroke)
        {
            return MixingStrokeContainsPoint(i, world, toleranceWorld);
        }
        if (shape == VectorAnimationEngine.ShapeKind.Line)
        {
            var halfW = Width[i] * 0.5f;
            var start = LocalToWorld(i, -halfW, 0);
            var end = LocalToWorld(i, halfW, 0);
            var control1 = new PointF(CurveControlX[i], CurveControlY[i]);
            var control2 = new PointF(CurveControl2X[i], CurveControl2Y[i]);
            var hitRadius = Math.Max(Height[i] * 0.5f, 1) + toleranceWorld;
            return DistanceToCubic(world, start, control1, control2, end) <= hitRadius;
        }

        if (IsFreehandShape(shape))
        {
            if (!TryGetFreehandWorldPoints(i, out var points) || points.Length == 0) return false;
            var hitRadius = Math.Max(Stroke[i] * 0.5f, 1) + toleranceWorld;
            if (points.Length == 1) return Distance(world, points[0]) <= hitRadius;
            for (var p = 0; p < points.Length - 1; p++)
            {
                if (DistanceToSegment(world, points[p], points[p + 1]) <= hitRadius) return true;
            }

            return false;
        }

        if (shape == VectorAnimationEngine.ShapeKind.Path)
        {
            return TryGetPathWorldContours(i, out var contours) && PointInCompoundPolygon(world, contours);
        }

        var local = WorldToLocal(i, world);
        var halfWidth = Width[i] * 0.5f + toleranceWorld;
        var halfHeight = Height[i] * 0.5f + toleranceWorld;
        if (Math.Abs(local.X) > halfWidth || Math.Abs(local.Y) > halfHeight) return false;

        if (shape == VectorAnimationEngine.ShapeKind.Ellipse)
        {
            var rx = Math.Max(1, Width[i] * 0.5f + toleranceWorld);
            var ry = Math.Max(1, Height[i] * 0.5f + toleranceWorld);
            var normalized = local.X * local.X / (rx * rx) + local.Y * local.Y / (ry * ry);
            return normalized <= 1;
        }

        return true;
    }

    public bool TrySampleMixingStrokeColor(int objectIndex, PointF world, out Color color)
    {
        color = Color.Transparent;
        if ((uint)objectIndex >= ObjectCount
            || ShapeKind[objectIndex] != VectorAnimationEngine.ShapeKind.MixingStroke)
        {
            return false;
        }

        if (!TryInverseMapObjectPoint(objectIndex, world, out var sourceWorld)) return false;
        var local = WorldToLocal(objectIndex, sourceWorld);
        if (_mixingStrokeLocalRegions.TryGetValue(objectIndex, out var region))
        {
            return region.TrySampleColor(local, out color);
        }
        if (!_mixingStrokeLocalSamples.TryGetValue(objectIndex, out var samples)
            || samples.Length == 0)
        {
            return false;
        }

        var cells = MixingStrokeCoverageBuilder.GetLocalCells(samples);
        var hasPaint = false;
        for (var index = cells.Count - 1; index >= 0; index--)
        {
            var cell = cells[index];
            if (!cell.Contains(local)) continue;

            var sampleColor = Color.FromArgb(cell.Argb);
            if (sampleColor.A == 0) continue;
            color = hasPaint
                ? PaintColorMixer.CompositeSourceOver(sampleColor, color)
                : sampleColor;
            hasPaint = true;
            if (color.A == 255) break;
        }
        return hasPaint;
    }

    private bool MixingStrokeContainsPoint(int objectIndex, PointF world, float toleranceWorld)
    {
        if ((uint)objectIndex >= ObjectCount
            || ShapeKind[objectIndex] != VectorAnimationEngine.ShapeKind.MixingStroke)
        {
            return false;
        }

        var local = WorldToLocal(objectIndex, world);
        var tolerance = Math.Max(0, toleranceWorld);
        if (_mixingStrokeLocalRegions.TryGetValue(objectIndex, out var region))
        {
            return region.ContainsPaint(local, tolerance);
        }
        if (!_mixingStrokeLocalSamples.TryGetValue(objectIndex, out var samples)
            || samples.Length == 0)
        {
            return false;
        }

        for (var index = samples.Length - 1; index >= 0; index--)
        {
            var radius = samples[index].Diameter * 0.5f + tolerance;
            var dx = local.X - samples[index].Point.X;
            var dy = local.Y - samples[index].Point.Y;
            if (dx * dx + dy * dy <= radius * radius)
            {
                return true;
            }
        }
        return false;
    }

    private bool MixingStrokeIntersectsBounds(int objectIndex, RectangleF bounds)
    {
        if ((uint)objectIndex >= ObjectCount
            || ShapeKind[objectIndex] != VectorAnimationEngine.ShapeKind.MixingStroke)
        {
            return false;
        }

        if (_mixingStrokeLocalRegions.TryGetValue(objectIndex, out var region))
        {
            return region.IntersectsBounds(
                bounds,
                point => LocalToWorld(objectIndex, point.X, point.Y));
        }
        if (!_mixingStrokeLocalSamples.TryGetValue(objectIndex, out var samples)
            || samples.Length == 0)
        {
            return false;
        }

        var hasPrevious = false;
        var previousCenter = PointF.Empty;
        var previousRadius = 0f;
        foreach (var sample in samples)
        {
            if (Color.FromArgb(sample.Argb).A == 0
                || !float.IsFinite(sample.Point.X)
                || !float.IsFinite(sample.Point.Y)
                || !float.IsFinite(sample.Diameter)
                || sample.Diameter <= 0)
            {
                hasPrevious = false;
                continue;
            }

            var center = LocalToWorld(objectIndex, sample.Point.X, sample.Point.Y);
            var radius = sample.Diameter * 0.5f;
            if (!float.IsFinite(center.X) || !float.IsFinite(center.Y))
            {
                hasPrevious = false;
                continue;
            }
            if (CircleIntersectsRectangle(center, radius, bounds)
                || (hasPrevious && MixingSegmentIntersectsBounds(
                        previousCenter,
                        previousRadius,
                        center,
                        radius,
                        bounds)
                    && MixingSamplesTouchOrOverlap(previousCenter, previousRadius, center, radius)))
            {
                return true;
            }

            previousCenter = center;
            previousRadius = radius;
            hasPrevious = true;
        }

        return false;
    }

    private static bool MixingSamplesTouchOrOverlap(
        PointF start,
        float startRadius,
        PointF end,
        float endRadius)
    {
        var dx = (double)end.X - start.X;
        var dy = (double)end.Y - start.Y;
        var radius = (double)startRadius + endRadius;
        return dx * dx + dy * dy <= radius * radius;
    }

    private static bool MixingSegmentIntersectsBounds(
        PointF start,
        float startRadius,
        PointF end,
        float endRadius,
        RectangleF bounds)
    {
        if (SegmentIntersectsRectangle(start, end, bounds)) return true;

        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var length = MathF.Sqrt(dx * dx + dy * dy);
        if (length <= Math.Abs(endRadius - startRadius) + 0.001f)
        {
            return false;
        }

        var unitX = dx / length;
        var unitY = dy / length;
        var normalAlong = -(endRadius - startRadius) / length;
        var normalAcross = MathF.Sqrt(Math.Max(0, 1 - normalAlong * normalAlong));
        var firstNormal = new PointF(
            normalAlong * unitX - normalAcross * unitY,
            normalAlong * unitY + normalAcross * unitX);
        var secondNormal = new PointF(
            normalAlong * unitX + normalAcross * unitY,
            normalAlong * unitY - normalAcross * unitX);
        var hull = new[]
        {
            new PointF(start.X + firstNormal.X * startRadius, start.Y + firstNormal.Y * startRadius),
            new PointF(end.X + firstNormal.X * endRadius, end.Y + firstNormal.Y * endRadius),
            new PointF(end.X + secondNormal.X * endRadius, end.Y + secondNormal.Y * endRadius),
            new PointF(start.X + secondNormal.X * startRadius, start.Y + secondNormal.Y * startRadius)
        };
        if (SegmentIntersectsRectangle(hull[0], hull[1], bounds)
            || SegmentIntersectsRectangle(hull[2], hull[3], bounds))
        {
            return true;
        }

        var corners = new[]
        {
            new PointF(bounds.Left, bounds.Top),
            new PointF(bounds.Right, bounds.Top),
            new PointF(bounds.Right, bounds.Bottom),
            new PointF(bounds.Left, bounds.Bottom)
        };
        return corners.Any(corner => PointInPolygonOrOnBoundary(corner, hull));
    }

    private static bool CircleIntersectsRectangle(PointF center, float radius, RectangleF bounds)
    {
        var nearestX = Math.Clamp(center.X, bounds.Left, bounds.Right);
        var nearestY = Math.Clamp(center.Y, bounds.Top, bounds.Bottom);
        var dx = center.X - nearestX;
        var dy = center.Y - nearestY;
        return dx * dx + dy * dy <= radius * radius;
    }

    private DrawingElementHit HitElement(PointF world, int i, IReadOnlyList<int> hitCandidates, int frame, float toleranceWorld)
    {
        if (!TryInverseMapObjectHitPoint(i, world, toleranceWorld, out var sourceWorld)) return DrawingElementHit.None;
        var shape = ShapeKind.Length > i ? ShapeKind[i] : VectorAnimationEngine.ShapeKind.Rectangle;
        if (shape == VectorAnimationEngine.ShapeKind.MixingStroke
            && TryGetMixingBrushLocalRegion(i, out var mixingRegion))
        {
            var local = WorldToLocal(i, sourceWorld);
            return mixingRegion.TryHitConnectedComponent(local, toleranceWorld, out var partIndex)
                ? new DrawingElementHit(new DrawingElementKey(i, DrawingElementKind.Fill, partIndex), 0, 0, 1)
                : DrawingElementHit.None;
        }
        if (IsWholeObjectShape(shape))
        {
            return HitObject(sourceWorld, i, toleranceWorld)
                ? new DrawingElementHit(new DrawingElementKey(i, DrawingElementKind.Fill, 0), 0, 0, 1)
                : DrawingElementHit.None;
        }
        if (IsTopologyStrokeShape(shape))
        {
            if (!HasStroke(i)) return DrawingElementHit.None;
            return HitStrokeElement(sourceWorld, i, frame, CollectTopologyCandidates(i, frame), toleranceWorld);
        }

        IReadOnlyList<int>? topologyCandidates = null;
        if (HasStroke(i))
        {
            var hitRadius = Math.Max(Stroke[i] * 0.5f, 1) + toleranceWorld;
            var nearBoundary = ShapeBoundaryContours(i).Any(contour =>
                DistanceToPolyline(sourceWorld, contour) <= hitRadius + BooleanBezierMaximumErrorUnits);
            if (nearBoundary)
            {
                topologyCandidates = CollectTopologyCandidates(i, frame);
                var boundaryHit = HitBoundaryStrokeElement(sourceWorld, i, frame, topologyCandidates, toleranceWorld);
                if (boundaryHit.IsValid) return boundaryHit;
            }
        }

        if (!HasFill(i) || !FillContainsRawPoint(i, sourceWorld)) return DrawingElementHit.None;
        if (!OwnsFillUnit(world, i, hitCandidates)) return DrawingElementHit.None;
        topologyCandidates ??= CollectTopologyCandidates(i, frame);
        var part = FillPartIndex(sourceWorld, i, frame, topologyCandidates);
        if (part < 0) return DrawingElementHit.None;
        return new DrawingElementHit(new DrawingElementKey(i, DrawingElementKind.Fill, part), 0, 0, 1);
    }

    private bool OwnsFillUnit(PointF world, int fillIndex, IReadOnlyList<int> candidates)
    {
        var layer = ObjectLayer[fillIndex];
        var unit = DrawingUnitCell.FromPoint(layer, world);
        var sample = new PointF(unit.X + 0.5f, unit.Y + 0.5f);
        var owner = -1;

        foreach (var candidate in candidates)
        {
            if (ObjectLayer[candidate] != layer) continue;
            var shape = ShapeKind.Length > candidate ? ShapeKind[candidate] : VectorAnimationEngine.ShapeKind.Rectangle;
            if (!IsFillShape(shape) || !HasFill(candidate)) continue;
            if (!ObjectMayContainHitPoint(candidate, sample, 0)) continue;
            if (!FillContainsPoint(candidate, sample)) continue;
            if (owner < 0 || CompareObjectStack(candidate, owner) > 0)
            {
                owner = candidate;
            }
        }

        return owner == fillIndex;
    }

    private DrawingElementHit HitStrokeElement(
        PointF world,
        int strokeIndex,
        int frame,
        IReadOnlyList<int> candidates,
        float toleranceWorld)
    {
        var samples = StrokeSamples(strokeIndex);
        if (samples.Length == 0) return DrawingElementHit.None;
        var hitRadius = Math.Max(Stroke[strokeIndex] * 0.5f, 1) + toleranceWorld;
        if (samples.Length == 1)
        {
            var distance = Distance(world, samples[0].Point);
            return distance <= hitRadius
                ? new DrawingElementHit(new DrawingElementKey(strokeIndex, DrawingElementKind.Stroke, 0), distance, 0, 1)
                : DrawingElementHit.None;
        }

        if (!CurveSamplesHit(world, samples, hitRadius)) return DrawingElementHit.None;
        return HitCurvePart(
            world,
            strokeIndex,
            DrawingElementKind.Stroke,
            samples,
            HitStrokeSplitParameters(strokeIndex, frame, candidates),
            hitRadius);
    }

    private DrawingElementHit HitBoundaryStrokeElement(
        PointF world,
        int objectIndex,
        int frame,
        IReadOnlyList<int> candidates,
        float toleranceWorld)
    {
        if (!HasStroke(objectIndex)) return DrawingElementHit.None;
        var hitRadius = Math.Max(Stroke[objectIndex] * 0.5f, 1) + toleranceWorld;
        var best = DrawingElementHit.None;
        foreach (var part in HitBoundaryStrokeParts(objectIndex, frame, candidates))
        {
            // Pulled-out (detached) boundary segments are no longer part of the source's
            // editable stroke: they must not be hit-testable again.
            if (IsBoundaryPartHitHidden(objectIndex, part)) continue;
            var distance = DistanceToPolyline(world, part.Points);
            if (distance > hitRadius || (best.IsValid && distance >= best.Distance)) continue;
            best = new DrawingElementHit(
                new DrawingElementKey(objectIndex, DrawingElementKind.BoundaryStroke, part.PartIndex),
                distance,
                part.StartT,
                part.EndT);
        }

        return best;
    }

    private DrawingElementHit HitPolylinePart(PointF world, int objectIndex, DrawingElementKind kind, PointF[] polyline, List<float> splitPoints, float hitRadius)
    {
        var bestDistance = float.MaxValue;
        var bestT = 0f;
        var segments = Math.Max(1, polyline.Length - 1);

        for (var i = 0; i < polyline.Length - 1; i++)
        {
            var distance = DistanceToSegment(world, polyline[i], polyline[i + 1]);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            bestT = (i + SegmentProjectionT(world, polyline[i], polyline[i + 1])) / segments;
        }

        if (bestDistance > hitRadius) return DrawingElementHit.None;

        var part = 0;
        for (var i = 0; i < splitPoints.Count - 1; i++)
        {
            if (bestT < splitPoints[i] - 0.0001f || bestT > splitPoints[i + 1] + 0.0001f) continue;
            part = i;
            return new DrawingElementHit(new DrawingElementKey(objectIndex, kind, part), bestDistance, splitPoints[i], splitPoints[i + 1]);
        }

        return new DrawingElementHit(new DrawingElementKey(objectIndex, kind, part), bestDistance, 0, 1);
    }

    private DrawingElementHit HitCurvePart(
        PointF world,
        int objectIndex,
        DrawingElementKind kind,
        CurveSample[] samples,
        IReadOnlyList<float> splitPoints,
        float hitRadius)
    {
        var bestDistance = float.MaxValue;
        var bestT = 0f;

        for (var i = 0; i < samples.Length - 1; i++)
        {
            var a = samples[i];
            var b = samples[i + 1];
            var distance = DistanceToSegment(world, a.Point, b.Point);
            if (distance >= bestDistance) continue;
            var localT = SegmentProjectionT(world, a.Point, b.Point);
            bestDistance = distance;
            bestT = a.T + (b.T - a.T) * localT;
        }

        if (bestDistance > hitRadius) return DrawingElementHit.None;

        var part = 0;
        for (var i = 0; i < splitPoints.Count - 1; i++)
        {
            if (bestT < splitPoints[i] - 0.0001f || bestT > splitPoints[i + 1] + 0.0001f) continue;
            part = i;
            return new DrawingElementHit(new DrawingElementKey(objectIndex, kind, part), bestDistance, splitPoints[i], splitPoints[i + 1]);
        }

        return new DrawingElementHit(new DrawingElementKey(objectIndex, kind, part), bestDistance, 0, 1);
    }

    private IReadOnlyList<float> HitStrokeSplitParameters(
        int strokeIndex,
        int frame,
        IReadOnlyList<int> candidates)
    {
        return CachedStrokeSplitEntry(strokeIndex, frame, candidates).Parameters;
    }

    private IReadOnlyList<DrawingTopologySplit> CachedStrokeSplits(
        int strokeIndex,
        int frame,
        IReadOnlyList<int> candidates)
    {
        return CachedStrokeSplitEntry(strokeIndex, frame, candidates).Splits;
    }

    private StrokeSplitCacheEntry CachedStrokeSplitEntry(
        int strokeIndex,
        int frame,
        IReadOnlyList<int> candidates)
    {
        EnsureInteractiveQueryCacheRevision();
        var cacheKey = (strokeIndex, frame);
        if (_hitStrokeSplitCache.TryGetValue(cacheKey, out var cached)) return cached;

        var splits = StrokeSplits(strokeIndex, candidates).ToArray();
        var entry = new StrokeSplitCacheEntry(
            splits,
            splits.Select(split => split.T).ToArray());
        if (_hitStrokeSplitCache.Count >= MaximumTopologyQueryCacheEntries) _hitStrokeSplitCache.Clear();
        _hitStrokeSplitCache[cacheKey] = entry;
        return entry;
    }

    private IReadOnlyList<BoundaryStrokePart> HitBoundaryStrokeParts(
        int objectIndex,
        int frame,
        IReadOnlyList<int> candidates)
    {
        return CachedBoundaryStrokeParts(objectIndex, frame, candidates);
    }

    private IReadOnlyList<BoundaryStrokePart> CachedBoundaryStrokeParts(
        int objectIndex,
        int frame,
        IReadOnlyList<int> candidates)
    {
        EnsureInteractiveQueryCacheRevision();
        var cacheKey = (objectIndex, frame);
        if (_hitBoundaryPartCache.TryGetValue(cacheKey, out var cached)) return cached;

        var parts = BuildBoundaryStrokeParts(objectIndex, candidates).ToArray();
        if (_hitBoundaryPartCache.Count >= MaximumTopologyQueryCacheEntries) _hitBoundaryPartCache.Clear();
        _hitBoundaryPartCache[cacheKey] = parts;
        return parts;
    }

    private List<float> StrokeSplitParameters(int strokeIndex, IReadOnlyList<int> candidates)
    {
        return StrokeSplits(strokeIndex, candidates).Select(split => split.T).ToList();
    }

    private List<DrawingTopologySplit> StrokeSplits(int strokeIndex, IReadOnlyList<int> candidates)
    {
        var splits = new List<DrawingTopologySplit>();
        var source = StrokeSamples(strokeIndex);
        if (source.Length == 0) return splits;
        splits.Add(new DrawingTopologySplit(0, source[0].Point));
        splits.Add(new DrawingTopologySplit(1, source[^1].Point));
        var sourceCurve = ShapeKind[strokeIndex] == VectorAnimationEngine.ShapeKind.Line
            ? PrepareBezierCurve(LineCurve(strokeIndex), source)
            : (PreparedBezierCurve?)null;
        var sourceLayer = ObjectLayer[strokeIndex];
        foreach (var other in candidates)
        {
            if (other == strokeIndex || ObjectLayer[other] != sourceLayer) continue;
            var shape = ShapeKind.Length > other ? ShapeKind[other] : VectorAnimationEngine.ShapeKind.Rectangle;
            if (IsTopologyStrokeShape(shape) && HasStroke(other))
            {
                AddCurveCurveIntersections(splits, source, StrokeSamples(other), includeSourceEndpoints: false);
            }
            else if (IsFillShape(shape) && (HasFill(other) || HasStroke(other)))
            {
                if (sourceCurve is { } preparedSource
                    && TryGetEditableFillBezierContours(other, out var fillContours))
                {
                    foreach (var curve in BuildPathBezierCurves(fillContours))
                    {
                        AddBezierCurveRelationSplits(
                            splits,
                            preparedSource,
                            PrepareBezierCurve(curve),
                            includeSourceEndpoints: false);
                    }
                    continue;
                }

                var contours = ShapeBoundaryContours(other);
                for (var contourIndex = 0; contourIndex < contours.Length; contourIndex++)
                {
                    var contour = contours[contourIndex];
                    if (CurveSamplesFollowBoundary(source, contour)) continue;
                    AddCurvePolylineIntersections(splits, source, contour, includeSourceEndpoints: false);
                }
            }
        }

        return NormalizeStrokeSplits(splits);
    }

    private List<float> BoundarySplitParameters(int objectIndex, IReadOnlyList<int> candidates)
    {
        return BoundarySplitParameters(objectIndex, ShapeBoundary(objectIndex), candidates, contourIndex: -1);
    }

    private List<float> BoundarySplitParameters(
        int objectIndex,
        PointF[] boundary,
        IReadOnlyList<int> candidates,
        int contourIndex)
    {
        var splits = new List<DrawingTopologySplit>();
        var segmentCount = Math.Max(1, boundary.Length - 1);
        if (ShapeKind[objectIndex] == VectorAnimationEngine.ShapeKind.Ellipse)
        {
            splits.Add(new DrawingTopologySplit(0, boundary[0]));
            splits.Add(new DrawingTopologySplit(1, boundary[^1]));
        }
        else if (ShapeKind[objectIndex] == VectorAnimationEngine.ShapeKind.Path)
        {
            AddPathBoundaryCornerSplits(splits, boundary);
        }
        else
        {
            for (var i = 0; i < boundary.Length; i++)
            {
                splits.Add(new DrawingTopologySplit(Math.Clamp(i / (float)segmentCount, 0, 1), boundary[i]));
            }
        }

        var sourceLayer = ObjectLayer[objectIndex];
        foreach (var other in candidates)
        {
            if (other == objectIndex || ObjectLayer[other] != sourceLayer) continue;
            var shape = ShapeKind.Length > other ? ShapeKind[other] : VectorAnimationEngine.ShapeKind.Rectangle;
            if (!HasStroke(other)) continue;
            if (IsTopologyStrokeShape(shape))
            {
                var strokeSamples = StrokeSamples(other);
                if (LineCurveCoincidesWithFillBezierContour(other, objectIndex, contourIndex)) continue;
                if (CurveSamplesFollowBoundary(strokeSamples, boundary)) continue;
                AddPolylineCurveIntersections(splits, boundary, strokeSamples, includeSourceEndpoints: true);
            }
            else if (IsFillShape(shape))
            {
                foreach (var contour in ShapeBoundaryContours(other))
                {
                    AddPolylinePolylineIntersections(splits, boundary, contour, includeSourceEndpoints: true);
                }
            }
        }

        return NormalizeStrokeSplits(splits).Select(split => split.T).ToList();
    }

    private bool LineCurveCoincidesWithFillBezierContour(
        int lineObjectIndex,
        int fillObjectIndex,
        int contourIndex)
    {
        if (!TryGetLineCubic(
                lineObjectIndex,
                out var lineStart,
                out var lineControl1,
                out var lineControl2,
                out var lineEnd)
            || !TryGetPathBezierWorldContours(fillObjectIndex, out var contours))
        {
            return false;
        }

        var firstContour = contourIndex < 0 ? 0 : contourIndex;
        var lastContour = contourIndex < 0 ? contours.Length - 1 : contourIndex;
        if (firstContour < 0 || lastContour >= contours.Length) return false;
        for (var candidateContour = firstContour; candidateContour <= lastContour; candidateContour++)
        {
            var contour = contours[candidateContour];
            for (var segmentIndex = 0; segmentIndex < contour.Length; segmentIndex++)
            {
                var current = contour[segmentIndex];
                var next = contour[(segmentIndex + 1) % contour.Length];
                if (CubicCurvesCoincide(
                        current.Anchor,
                        current.OutgoingControl,
                        next.IncomingControl,
                        next.Anchor,
                        lineStart,
                        lineControl1,
                        lineControl2,
                        lineEnd,
                        out _))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool CurveSamplesFollowBoundary(CurveSample[] samples, PointF[] boundary)
    {
        if (samples.Length < 2 || samples.Length > boundary.Length) return false;
        for (var segmentIndex = 0; segmentIndex + samples.Length <= boundary.Length; segmentIndex++)
        {
            var forward = true;
            var reverse = true;
            for (var sampleIndex = 0; sampleIndex < samples.Length; sampleIndex++)
            {
                var forwardSample = VectorUnits.Quantize(samples[sampleIndex].Point);
                var reverseSample = VectorUnits.Quantize(samples[^(sampleIndex + 1)].Point);
                forward &= SameDrawingUnit(boundary[segmentIndex + sampleIndex], forwardSample);
                reverse &= SameDrawingUnit(boundary[segmentIndex + sampleIndex], reverseSample);
                if (!forward && !reverse) break;
            }

            if (forward || reverse) return true;
        }

        return false;
    }

    private static void AddPathBoundaryCornerSplits(List<DrawingTopologySplit> splits, PointF[] boundary)
    {
        if (boundary.Length == 0) return;
        var segmentCount = Math.Max(1, boundary.Length - 1);
        splits.Add(new DrawingTopologySplit(0, boundary[0]));
        if (boundary.Length == 1) return;

        const float hardCornerCosine = 0.8660254f; // 30 degrees.
        for (var index = 1; index < boundary.Length - 1; index++)
        {
            var previous = boundary[index - 1];
            var current = boundary[index];
            var next = boundary[index + 1];
            var incomingX = current.X - previous.X;
            var incomingY = current.Y - previous.Y;
            var outgoingX = next.X - current.X;
            var outgoingY = next.Y - current.Y;
            var incomingLength = MathF.Sqrt(incomingX * incomingX + incomingY * incomingY);
            var outgoingLength = MathF.Sqrt(outgoingX * outgoingX + outgoingY * outgoingY);
            if (incomingLength < DrawingTopologyRules.MinStrokeSegmentUnits
                || outgoingLength < DrawingTopologyRules.MinStrokeSegmentUnits)
            {
                continue;
            }

            var cosine = (incomingX * outgoingX + incomingY * outgoingY) / (incomingLength * outgoingLength);
            if (cosine > hardCornerCosine) continue;
            splits.Add(new DrawingTopologySplit(index / (float)segmentCount, current));
        }

        splits.Add(new DrawingTopologySplit(1, boundary[^1]));
    }

    private List<BoundaryStrokePart> BuildBoundaryStrokeParts(int objectIndex, IReadOnlyList<int> candidates)
    {
        if (TryGetPathBezierWorldContours(objectIndex, out var bezierContours))
        {
            return BuildPathBezierBoundaryStrokeParts(objectIndex, bezierContours, candidates);
        }

        if (ShapeKind[objectIndex] == VectorAnimationEngine.ShapeKind.Ellipse)
        {
            return BuildEllipseBoundaryStrokeParts(objectIndex, candidates);
        }

        var result = new List<BoundaryStrokePart>();
        var partIndex = 0;
        var contours = ShapeBoundaryContours(objectIndex);
        for (var contourIndex = 0; contourIndex < contours.Length; contourIndex++)
        {
            var contour = contours[contourIndex];
            var splits = BoundarySplitParameters(objectIndex, contour, candidates, contourIndex);
            foreach (var part in BuildPolylinePathParts(contour, splits))
            {
                result.Add(new BoundaryStrokePart(partIndex++, contourIndex, part.StartT, part.EndT, part.Points));
            }
        }

        return result;
    }

    private List<BoundaryStrokePart> BuildPathBezierBoundaryStrokeParts(
        int objectIndex,
        IReadOnlyList<PathBezierNode[]> contours,
        IReadOnlyList<int> candidates)
    {
        var result = new List<BoundaryStrokePart>();
        var sourceLayer = ObjectLayer[objectIndex];
        var lineCurves = PrepareTopologyLineCurves(objectIndex, sourceLayer, candidates);
        for (var contourIndex = 0; contourIndex < contours.Count; contourIndex++)
        {
            var contour = contours[contourIndex];
            if (contour.Length == 0) continue;

            for (var segmentIndex = 0; segmentIndex < contour.Length; segmentIndex++)
            {
                var current = contour[segmentIndex];
                var next = contour[(segmentIndex + 1) % contour.Length];
                var sourceCurve = new CubicBoundarySegment(
                    current.Anchor,
                    current.OutgoingControl,
                    next.IncomingControl,
                    next.Anchor);
                var sourceSamples = SampleCubicSegmentWithParameters(sourceCurve);
                var preparedSource = PrepareBezierCurve(sourceCurve, sourceSamples);
                var sourceBoundary = sourceSamples.Select(sample => sample.Point).ToArray();
                var splits = new List<DrawingTopologySplit>
                {
                    new(0, sourceCurve.Start),
                    new(1, sourceCurve.End)
                };

                foreach (var other in candidates)
                {
                    if (other == objectIndex || ObjectLayer[other] != sourceLayer || !HasStroke(other)) continue;
                    var shape = ShapeKind.Length > other
                        ? ShapeKind[other]
                        : VectorAnimationEngine.ShapeKind.Rectangle;
                    if (shape == VectorAnimationEngine.ShapeKind.Line)
                    {
                        AddBezierCurveRelationSplits(
                            splits,
                            preparedSource,
                            lineCurves[other],
                            includeSourceEndpoints: true);
                    }
                    else if (IsTopologyStrokeShape(shape))
                    {
                        var strokeSamples = StrokeSamples(other);
                        if (CurveSamplesFollowBoundary(strokeSamples, sourceBoundary))
                        {
                            continue;
                        }

                        AddCurveCurveIntersections(
                            splits,
                            sourceSamples,
                            strokeSamples,
                            includeSourceEndpoints: true);
                    }
                    else if (IsFillShape(shape))
                    {
                        foreach (var cutter in ShapeBoundaryContours(other))
                        {
                            AddCurvePolylineIntersections(
                                splits,
                                sourceSamples,
                                cutter,
                                includeSourceEndpoints: true);
                        }
                    }
                }

                var normalized = NormalizeStrokeSplits(
                    splits.Select(split => RefineCubicTopologySplit(sourceCurve, split)).ToList());
                foreach (var segment in BuildCurveParts(
                             sourceCurve.Start,
                             sourceCurve.Control1,
                             sourceCurve.Control2,
                             sourceCurve.End,
                             normalized))
                {
                    var curve = new CubicBoundarySegment(
                        segment.Start,
                        segment.Control1,
                        segment.Control2,
                        segment.End);
                    var localStartT = normalized[segment.PartIndex].T;
                    var localEndT = normalized[segment.PartIndex + 1].T;
                    result.Add(new BoundaryStrokePart(
                        result.Count,
                        contourIndex,
                        (segmentIndex + localStartT) / contour.Length,
                        (segmentIndex + localEndT) / contour.Length,
                        SampleCubicSegment(curve),
                        curve));
                }
            }
        }

        return result;
    }

    private List<BoundaryStrokePart> BuildEllipseBoundaryStrokeParts(
        int objectIndex,
        IReadOnlyList<int> candidates)
    {
        var curves = EllipseBoundaryCurves(objectIndex);
        if (curves.Length != 4) return new List<BoundaryStrokePart>();

        var samples = CubicBoundarySamples(curves);
        var splits = new List<DrawingTopologySplit>
        {
            new(0, curves[0].Start),
            new(0.25f, curves[0].End),
            new(0.5f, curves[1].End),
            new(0.75f, curves[2].End),
            new(1, curves[3].End)
        };
        var boundary = samples.Select(sample => sample.Point).ToArray();
        var sourceLayer = ObjectLayer[objectIndex];
        foreach (var other in candidates)
        {
            if (other == objectIndex || ObjectLayer[other] != sourceLayer || !HasStroke(other)) continue;
            var shape = ShapeKind.Length > other ? ShapeKind[other] : VectorAnimationEngine.ShapeKind.Rectangle;
            if (IsTopologyStrokeShape(shape))
            {
                var strokeSamples = StrokeSamples(other);
                if (CurveSamplesFollowBoundary(strokeSamples, boundary)) continue;
                AddCurveCurveIntersections(splits, samples, strokeSamples, includeSourceEndpoints: true);
            }
            else if (IsFillShape(shape))
            {
                foreach (var contour in ShapeBoundaryContours(other))
                {
                    AddCurvePolylineIntersections(splits, samples, contour, includeSourceEndpoints: true);
                }
            }
        }

        var normalized = NormalizeStrokeSplits(splits);
        var result = new List<BoundaryStrokePart>(Math.Max(0, normalized.Count - 1));
        var partIndex = 0;
        for (var splitIndex = 0; splitIndex < normalized.Count - 1; splitIndex++)
        {
            var startSplit = normalized[splitIndex];
            var endSplit = normalized[splitIndex + 1];
            if (endSplit.T - startSplit.T <= 0.0001f) continue;

            var middleT = (startSplit.T + endSplit.T) * 0.5f;
            var quarter = Math.Clamp((int)MathF.Floor(middleT * 4), 0, 3);
            var localStartT = Math.Clamp(startSplit.T * 4 - quarter, 0, 1);
            var localEndT = Math.Clamp(endSplit.T * 4 - quarter, localStartT, 1);
            var sourceCurve = curves[quarter];
            var segment = CubicSubcurve(
                sourceCurve.Start,
                sourceCurve.Control1,
                sourceCurve.Control2,
                sourceCurve.End,
                localStartT,
                localEndT);
            var startDelta = new PointF(
                startSplit.Point.X - segment.Start.X,
                startSplit.Point.Y - segment.Start.Y);
            var endDelta = new PointF(
                endSplit.Point.X - segment.End.X,
                endSplit.Point.Y - segment.End.Y);
            var curve = new CubicBoundarySegment(
                startSplit.Point,
                new PointF(segment.Control1.X + startDelta.X, segment.Control1.Y + startDelta.Y),
                new PointF(segment.Control2.X + endDelta.X, segment.Control2.Y + endDelta.Y),
                endSplit.Point);
            result.Add(new BoundaryStrokePart(
                partIndex++,
                0,
                startSplit.T,
                endSplit.T,
                SampleCubicSegment(curve),
                curve));
        }

        return result;
    }

    private List<DrawingTopologySplit> NormalizeStrokeSplits(List<DrawingTopologySplit> splits)
    {
        splits.Sort((a, b) => a.T.CompareTo(b.T));
        var unique = new List<DrawingTopologySplit>(splits.Count);
        foreach (var split in splits)
        {
            var quantized = VectorUnits.Quantize(split.Point);
            if (unique.Count > 0
                && Math.Abs(unique[^1].T - split.T) <= 0.0001f
                && SameDrawingUnit(unique[^1].Point, quantized))
            {
                continue;
            }

            unique.Add(new DrawingTopologySplit(Math.Clamp(split.T, 0, 1), quantized));
        }

        if (unique.Count <= 2) return unique;

        var filtered = new List<DrawingTopologySplit> { unique[0] };
        for (var i = 1; i < unique.Count - 1; i++)
        {
            var previous = filtered[^1];
            var next = unique[i + 1];
            var current = unique[i];
            if (Distance(previous.Point, current.Point) < DrawingTopologyRules.MinStrokeSegmentUnits) continue;
            if (Distance(current.Point, next.Point) < DrawingTopologyRules.MinStrokeSegmentUnits) continue;
            filtered.Add(current);
        }

        if (Distance(filtered[^1].Point, unique[^1].Point) >= DrawingTopologyRules.MinStrokeSegmentUnits || filtered.Count == 1)
        {
            filtered.Add(unique[^1]);
        }
        else
        {
            filtered[^1] = unique[^1];
        }

        return filtered;
    }

    private int FillPartIndex(PointF world, int fillIndex, int frame, IReadOnlyList<int> candidates)
    {
        var regions = BuildFillRegions(fillIndex, frame, candidates);
        for (var part = 0; part < regions.Count; part++)
        {
            if (PointInCompoundPolygonOrOnBoundary(world, regions[part].Contours)) return part;
        }

        return -1;
    }

    private List<DrawingTopologySplit> CollectCurvePolylineIntersections(CurveSample[] source, PointF[] cutter)
    {
        var result = new List<DrawingTopologySplit>();
        AddCurvePolylineIntersections(result, source, cutter, includeSourceEndpoints: false);
        return result;
    }

    private void AddCurveCurveIntersections(List<DrawingTopologySplit> result, CurveSample[] source, CurveSample[] cutter, bool includeSourceEndpoints)
    {
        for (var i = 0; i < source.Length - 1; i++)
        {
            for (var j = 0; j < cutter.Length - 1; j++)
            {
                AddTopologySegmentIntersections(result, source[i], source[i + 1], cutter[j].Point, cutter[j + 1].Point, includeSourceEndpoints);
            }
        }
    }

    private void AddCurvePolylineIntersections(List<DrawingTopologySplit> result, CurveSample[] source, PointF[] cutter, bool includeSourceEndpoints)
    {
        for (var i = 0; i < source.Length - 1; i++)
        {
            for (var j = 0; j < cutter.Length - 1; j++)
            {
                AddTopologySegmentIntersections(result, source[i], source[i + 1], cutter[j], cutter[j + 1], includeSourceEndpoints);
            }
        }
    }

    private void AddPolylineCurveIntersections(List<DrawingTopologySplit> result, PointF[] source, CurveSample[] cutter, bool includeSourceEndpoints)
    {
        var sourceSegments = Math.Max(1, source.Length - 1);
        for (var i = 0; i < source.Length - 1; i++)
        {
            var sourceStart = new CurveSample(i / (float)sourceSegments, source[i]);
            var sourceEnd = new CurveSample((i + 1) / (float)sourceSegments, source[i + 1]);
            for (var j = 0; j < cutter.Length - 1; j++)
            {
                AddTopologySegmentIntersections(result, sourceStart, sourceEnd, cutter[j].Point, cutter[j + 1].Point, includeSourceEndpoints);
            }
        }
    }

    private void AddPolylinePolylineIntersections(List<DrawingTopologySplit> result, PointF[] source, PointF[] cutter, bool includeSourceEndpoints)
    {
        var sourceSegments = Math.Max(1, source.Length - 1);
        for (var i = 0; i < source.Length - 1; i++)
        {
            var sourceStart = new CurveSample(i / (float)sourceSegments, source[i]);
            var sourceEnd = new CurveSample((i + 1) / (float)sourceSegments, source[i + 1]);
            for (var j = 0; j < cutter.Length - 1; j++)
            {
                AddTopologySegmentIntersections(result, sourceStart, sourceEnd, cutter[j], cutter[j + 1], includeSourceEndpoints);
            }
        }
    }

    private void AddTopologySegmentIntersections(
        List<DrawingTopologySplit> result,
        CurveSample sourceStart,
        CurveSample sourceEnd,
        PointF cutterStart,
        PointF cutterEnd,
        bool includeSourceEndpoints)
    {
        var count = SegmentIntersectionParameters(sourceStart.Point, sourceEnd.Point, cutterStart, cutterEnd, out var first, out var second);
        if (count >= 1)
        {
            AddTopologySegmentIntersection(result, sourceStart, sourceEnd, first, includeSourceEndpoints);
            if (count >= 2) AddTopologySegmentIntersection(result, sourceStart, sourceEnd, second, includeSourceEndpoints);
            return;
        }

        // A terminating stroke can meet another stroke without producing a crossing.
        // Keep that contact as a topology node so the endpoint can be selected and edited.
        if (TryEndpointContactParameter(
                sourceStart.Point,
                sourceEnd.Point,
                cutterStart,
                cutterEnd,
                out var contact,
                out var contactPoint))
        {
            AddTopologySegmentIntersection(
                result,
                sourceStart,
                sourceEnd,
                contact,
                includeSourceEndpoints,
                contactPoint);
        }
    }

    private static bool TryEndpointContactParameter(
        PointF sourceStart,
        PointF sourceEnd,
        PointF cutterStart,
        PointF cutterEnd,
        out float parameter,
        out PointF contactPoint)
    {
        parameter = 0;
        contactPoint = PointF.Empty;
        if (TryPointOnSegment(cutterStart, sourceStart, sourceEnd, out parameter))
        {
            contactPoint = cutterStart;
            return true;
        }
        if (TryPointOnSegment(cutterEnd, sourceStart, sourceEnd, out parameter))
        {
            contactPoint = cutterEnd;
            return true;
        }
        if (TryPointOnSegment(sourceStart, cutterStart, cutterEnd, out _))
        {
            contactPoint = sourceStart;
            return true;
        }
        if (!TryPointOnSegment(sourceEnd, cutterStart, cutterEnd, out _)) return false;
        parameter = 1;
        contactPoint = sourceEnd;
        return true;
    }

    private static bool TryPointOnSegment(PointF point, PointF start, PointF end, out float parameter)
    {
        parameter = 0;
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var lengthSquared = dx * dx + dy * dy;
        // Curve evaluation can land between integer vector units while committed endpoints are quantized.
        var tolerance = Math.Max(DrawingTopologyRules.UnitIntersectionTolerance, ConnectedStrokeEndpointToleranceUnits);
        if (lengthSquared <= tolerance * tolerance)
        {
            return Distance(point, start) <= tolerance;
        }

        var projected = ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared;
        var length = MathF.Sqrt(lengthSquared);
        var parameterTolerance = tolerance / length;
        if (projected < -parameterTolerance || projected > 1 + parameterTolerance) return false;

        parameter = Math.Clamp(projected, 0, 1);
        var closest = Lerp(start, end, parameter);
        return Distance(point, closest) <= tolerance;
    }

    private static void AddTopologySegmentIntersection(
        List<DrawingTopologySplit> result,
        CurveSample sourceStart,
        CurveSample sourceEnd,
        float localT,
        bool includeSourceEndpoints,
        PointF? intersectionPoint = null)
    {
        var globalT = sourceStart.T + (sourceEnd.T - sourceStart.T) * localT;
        var point = intersectionPoint ?? Lerp(sourceStart.Point, sourceEnd.Point, localT);
        if (!includeSourceEndpoints
            && (sourceStart.T <= 0 && Distance(point, sourceStart.Point) <= DrawingTopologyRules.UnitIntersectionTolerance
                || sourceEnd.T >= 1 && Distance(point, sourceEnd.Point) <= DrawingTopologyRules.UnitIntersectionTolerance))
        {
            return;
        }

        result.Add(new DrawingTopologySplit(globalT, point));
    }

    private CubicBoundarySegment LineCurve(int i)
    {
        var halfW = Width[i] * 0.5f;
        var start = LocalToWorld(i, -halfW, 0);
        var end = LocalToWorld(i, halfW, 0);
        var control1 = new PointF(CurveControlX[i], CurveControlY[i]);
        var control2 = new PointF(CurveControl2X[i], CurveControl2Y[i]);
        return new CubicBoundarySegment(start, control1, control2, end);
    }

    private CurveSample[] CurveSamples(int i) => SampleCubicSegmentWithParameters(LineCurve(i));

    private CurveSample[] StrokeSamples(int objectIndex)
    {
        if ((uint)objectIndex >= ObjectCount) return Array.Empty<CurveSample>();
        if (ShapeKind[objectIndex] == VectorAnimationEngine.ShapeKind.Line) return CurveSamples(objectIndex);
        if (!IsFreehandShape(ShapeKind[objectIndex]) || !TryGetFreehandWorldPoints(objectIndex, out var points) || points.Length == 0)
        {
            return Array.Empty<CurveSample>();
        }

        if (points.Length == 1) return new[] { new CurveSample(0, points[0]) };
        var samples = new CurveSample[points.Length];
        var segmentCount = points.Length - 1;
        for (var i = 0; i < points.Length; i++) samples[i] = new CurveSample(i / (float)segmentCount, points[i]);
        return samples;
    }

    private static void AddAdaptiveCubicSamples(
        List<CurveSample> samples,
        PointF start,
        PointF control1,
        PointF control2,
        PointF end,
        float startT,
        float endT,
        int depth)
    {
        const int maxDepth = 9;
        const float flatnessUnits = 0.35f;
        if (depth >= maxDepth
            || Math.Max(DistanceToSegment(control1, start, end), DistanceToSegment(control2, start, end)) <= flatnessUnits)
        {
            return;
        }

        var p01 = Midpoint(start, control1);
        var p12 = Midpoint(control1, control2);
        var p23 = Midpoint(control2, end);
        var p012 = Midpoint(p01, p12);
        var p123 = Midpoint(p12, p23);
        var middle = Midpoint(p012, p123);
        var middleT = (startT + endT) * 0.5f;
        AddAdaptiveCubicSamples(samples, start, p01, p012, middle, startT, middleT, depth + 1);
        samples.Add(new CurveSample(middleT, middle));
        AddAdaptiveCubicSamples(samples, middle, p123, p23, end, middleT, endT, depth + 1);
    }

    private PointF[] ShapeBoundary(int i)
    {
        var shape = ShapeKind.Length > i ? ShapeKind[i] : VectorAnimationEngine.ShapeKind.Rectangle;
        if (IsFreehandShape(shape) && TryGetFreehandWorldPoints(i, out var freehandPoints))
        {
            return freehandPoints;
        }

        if (shape == VectorAnimationEngine.ShapeKind.Path && TryGetPathWorldContours(i, out var pathContours) && pathContours.Length > 0)
        {
            var pathPoints = pathContours
                .OrderByDescending(contour => Math.Abs(PolygonArea(contour)))
                .First();
            var boundary = new PointF[pathPoints.Length + 1];
            Array.Copy(pathPoints, boundary, pathPoints.Length);
            boundary[^1] = pathPoints[0];
            return boundary;
        }

        var halfW = Width[i] * 0.5f;
        var halfH = Height[i] * 0.5f;
        if (shape == VectorAnimationEngine.ShapeKind.Ellipse)
        {
            return CubicBoundarySamples(EllipseBoundaryCurves(i))
                .Select(sample => sample.Point)
                .ToArray();
        }

        var local = shape switch
        {
            VectorAnimationEngine.ShapeKind.Triangle => RegularBoundary(3, halfW, halfH, -MathF.PI / 2),
            VectorAnimationEngine.ShapeKind.Polygon => RegularBoundary(GetShapeVertexCount(i), halfW, halfH, -MathF.PI / 2),
            VectorAnimationEngine.ShapeKind.Star => StarBoundary(GetShapeVertexCount(i), halfW, halfH),
            _ => new[]
            {
                new PointF(-halfW, -halfH),
                new PointF(halfW, -halfH),
                new PointF(halfW, halfH),
                new PointF(-halfW, halfH),
                new PointF(-halfW, -halfH)
            }
        };

        var result = new PointF[local.Length];
        for (var p = 0; p < local.Length; p++) result[p] = LocalToWorld(i, local[p].X, local[p].Y);
        return result;
    }

    private PointF[][] ShapeBoundaryContours(int objectIndex)
    {
        if ((uint)objectIndex >= ObjectCount) return Array.Empty<PointF[]>();
        if (ShapeKind[objectIndex] == VectorAnimationEngine.ShapeKind.Path
            && TryGetPathWorldContours(objectIndex, out var pathContours))
        {
            var result = new List<PointF[]>(pathContours.Length);
            foreach (var contour in pathContours)
            {
                var closed = ClosePolyline(contour);
                if (closed.Length >= 4) result.Add(closed);
            }

            return result.ToArray();
        }

        var boundary = ShapeBoundary(objectIndex);
        return boundary.Length >= 2 ? new[] { boundary } : Array.Empty<PointF[]>();
    }

    internal PointF[][] GetObjectBoundaryContours(int objectIndex) => ShapeBoundaryContours(objectIndex);

    private CubicBoundarySegment[] EllipseBoundaryCurves(int objectIndex)
    {
        var local = EllipseBoundaryCurves(Width[objectIndex] * 0.5f, Height[objectIndex] * 0.5f);
        var result = new CubicBoundarySegment[local.Length];
        for (var index = 0; index < local.Length; index++)
        {
            var curve = local[index];
            result[index] = new CubicBoundarySegment(
                LocalToWorld(objectIndex, curve.Start.X, curve.Start.Y),
                LocalToWorld(objectIndex, curve.Control1.X, curve.Control1.Y),
                LocalToWorld(objectIndex, curve.Control2.X, curve.Control2.Y),
                LocalToWorld(objectIndex, curve.End.X, curve.End.Y));
        }

        return result;
    }

    private static CubicBoundarySegment[] EllipseBoundaryCurves(float halfW, float halfH)
    {
        const float kappa = 0.5522847498307936f;
        halfW = Math.Abs(halfW);
        halfH = Math.Abs(halfH);
        var top = new PointF(0, -halfH);
        var right = new PointF(halfW, 0);
        var bottom = new PointF(0, halfH);
        var left = new PointF(-halfW, 0);
        return new[]
        {
            new CubicBoundarySegment(
                top,
                new PointF(kappa * halfW, -halfH),
                new PointF(halfW, -kappa * halfH),
                right),
            new CubicBoundarySegment(
                right,
                new PointF(halfW, kappa * halfH),
                new PointF(kappa * halfW, halfH),
                bottom),
            new CubicBoundarySegment(
                bottom,
                new PointF(-kappa * halfW, halfH),
                new PointF(-halfW, kappa * halfH),
                left),
            new CubicBoundarySegment(
                left,
                new PointF(-halfW, -kappa * halfH),
                new PointF(-kappa * halfW, -halfH),
                top)
        };
    }

    private static CurveSample[] CubicBoundarySamples(IReadOnlyList<CubicBoundarySegment> curves)
    {
        if (curves.Count == 0) return Array.Empty<CurveSample>();
        var result = new List<CurveSample>(128);
        for (var curveIndex = 0; curveIndex < curves.Count; curveIndex++)
        {
            var segmentSamples = SampleCubicSegmentWithParameters(curves[curveIndex]);
            for (var sampleIndex = curveIndex == 0 ? 0 : 1; sampleIndex < segmentSamples.Length; sampleIndex++)
            {
                var sample = segmentSamples[sampleIndex];
                result.Add(new CurveSample((curveIndex + sample.T) / curves.Count, sample.Point));
            }
        }

        return result.ToArray();
    }

    private static PointF[] SampleCubicSegment(CubicBoundarySegment curve)
    {
        return SampleCubicSegmentWithParameters(curve).Select(sample => sample.Point).ToArray();
    }

    private static CurveSample[] SampleCubicSegmentWithParameters(CubicBoundarySegment curve)
    {
        var samples = new List<CurveSample>(32) { new(0, curve.Start) };
        AddAdaptiveCubicSamples(
            samples,
            curve.Start,
            curve.Control1,
            curve.Control2,
            curve.End,
            0,
            1,
            0);
        samples.Add(new CurveSample(1, curve.End));
        return samples.ToArray();
    }

    private static PointF[] RegularBoundary(int sides, float halfW, float halfH, float startAngle)
    {
        var points = new PointF[sides + 1];
        for (var i = 0; i < sides; i++)
        {
            var angle = startAngle + i * MathF.Tau / sides;
            points[i] = new PointF(MathF.Cos(angle) * halfW, MathF.Sin(angle) * halfH);
        }

        points[^1] = points[0];
        return points;
    }

    private static PointF[] StarBoundary(int points, float halfW, float halfH)
    {
        var result = new PointF[points * 2 + 1];
        for (var i = 0; i < points * 2; i++)
        {
            var radius = i % 2 == 0 ? 1f : 0.46f;
            var angle = -MathF.PI / 2 + i * MathF.Tau / (points * 2);
            result[i] = new PointF(MathF.Cos(angle) * halfW * radius, MathF.Sin(angle) * halfH * radius);
        }

        result[^1] = result[0];
        return result;
    }

    private static bool TrySegmentIntersection(PointF a, PointF b, PointF c, PointF d, out float t)
    {
        return SegmentIntersectionParameters(a, b, c, d, out t, out _) > 0;
    }

    private static int SegmentIntersectionParameters(
        PointF a,
        PointF b,
        PointF c,
        PointF d,
        out float first,
        out float second)
    {
        first = 0;
        second = 0;
        var tolerance = (double)DrawingTopologyRules.UnitIntersectionTolerance;
        var toleranceSquared = tolerance * tolerance;
        var rX = (double)b.X - a.X;
        var rY = (double)b.Y - a.Y;
        var sX = (double)d.X - c.X;
        var sY = (double)d.Y - c.Y;
        var rLengthSquared = rX * rX + rY * rY;
        var sLengthSquared = sX * sX + sY * sY;

        if (rLengthSquared <= toleranceSquared)
        {
            if (DistanceToSegment(a, c, d) > tolerance) return 0;
            return 1;
        }

        var rLength = Math.Sqrt(rLengthSquared);
        var cax = (double)c.X - a.X;
        var cay = (double)c.Y - a.Y;
        if (sLengthSquared <= toleranceSquared)
        {
            var projected = (cax * rX + cay * rY) / rLengthSquared;
            var parameterTolerance = tolerance / rLength;
            if (projected < -parameterTolerance || projected > 1 + parameterTolerance) return 0;

            var clamped = Math.Clamp(projected, 0, 1);
            var pointX = a.X + rX * clamped;
            var pointY = a.Y + rY * clamped;
            var dx = c.X - pointX;
            var dy = c.Y - pointY;
            if (dx * dx + dy * dy > toleranceSquared) return 0;

            first = second = (float)clamped;
            return 1;
        }

        var sLength = Math.Sqrt(sLengthSquared);
        var dax = (double)d.X - a.X;
        var day = (double)d.Y - a.Y;
        var collinearTolerance = tolerance * rLength;
        if (Math.Abs(cax * rY - cay * rX) <= collinearTolerance
            && Math.Abs(dax * rY - day * rX) <= collinearTolerance)
        {
            var cParameter = (cax * rX + cay * rY) / rLengthSquared;
            var dParameter = (dax * rX + day * rY) / rLengthSquared;
            var overlapStart = Math.Min(cParameter, dParameter);
            var overlapEnd = Math.Max(cParameter, dParameter);
            var overlapTolerance = tolerance / rLength;
            if (overlapEnd < -overlapTolerance || overlapStart > 1 + overlapTolerance) return 0;

            overlapStart = Math.Clamp(overlapStart, 0, 1);
            overlapEnd = Math.Clamp(overlapEnd, 0, 1);
            if ((overlapEnd - overlapStart) * rLength <= tolerance)
            {
                first = second = (float)((overlapStart + overlapEnd) * 0.5);
                return 1;
            }

            first = (float)overlapStart;
            second = (float)overlapEnd;
            return 2;
        }

        var denominator = rX * sY - rY * sX;
        var parallelTolerance = toleranceSquared * tolerance * Math.Max(1, rLength * sLength);
        if (Math.Abs(denominator) <= parallelTolerance) return 0;

        var t = (cax * sY - cay * sX) / denominator;
        var u = (cax * rY - cay * rX) / denominator;
        if (t < -tolerance / rLength
            || t > 1 + tolerance / rLength
            || u < -tolerance / sLength
            || u > 1 + tolerance / sLength)
        {
            return 0;
        }

        first = second = (float)Math.Clamp(t, 0, 1);
        return 1;
    }

    private static bool SameDrawingUnit(PointF a, PointF b)
    {
        return MathF.Floor(a.X) == MathF.Floor(b.X) && MathF.Floor(a.Y) == MathF.Floor(b.Y);
    }

    private static PointF Lerp(PointF a, PointF b, float t)
    {
        return new PointF(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
    }

    private static PointF Midpoint(PointF a, PointF b)
    {
        return new PointF((a.X + b.X) * 0.5f, (a.Y + b.Y) * 0.5f);
    }

    private static PointF PolylinePointAt(PointF[] points, float t)
    {
        if (points.Length == 0) return PointF.Empty;
        if (points.Length == 1) return points[0];
        t = Math.Clamp(t, 0, 1);
        var segments = points.Length - 1;
        var scaled = t * segments;
        var index = Math.Min(segments - 1, (int)MathF.Floor(scaled));
        return Lerp(points[index], points[index + 1], scaled - index);
    }

    private static PointF[] PolylineSlice(PointF[] points, float startT, float endT)
    {
        if (points.Length == 0) return Array.Empty<PointF>();
        if (points.Length == 1) return new[] { points[0] };

        startT = Math.Clamp(startT, 0, 1);
        endT = Math.Clamp(endT, startT, 1);
        var segmentCount = points.Length - 1;
        var startScaled = startT * segmentCount;
        var endScaled = endT * segmentCount;
        var firstVertex = Math.Clamp((int)MathF.Floor(startScaled) + 1, 1, points.Length - 1);
        var lastVertex = Math.Clamp((int)MathF.Ceiling(endScaled) - 1, 0, points.Length - 2);
        var result = new List<PointF> { VectorUnits.Quantize(PolylinePointAt(points, startT)) };

        for (var i = firstVertex; i <= lastVertex; i++)
        {
            var point = VectorUnits.Quantize(points[i]);
            if (result[^1] != point) result.Add(point);
        }

        var end = VectorUnits.Quantize(PolylinePointAt(points, endT));
        if (result[^1] != end) result.Add(end);
        return result.ToArray();
    }

    private static float PolylineLength(IReadOnlyList<PointF> points)
    {
        var length = 0f;
        for (var i = 0; i < points.Count - 1; i++) length += Distance(points[i], points[i + 1]);
        return length;
    }

    private static float DistanceToPolyline(PointF point, IReadOnlyList<PointF> polyline)
    {
        if (polyline.Count == 0) return float.MaxValue;
        if (polyline.Count == 1) return Distance(point, polyline[0]);
        var distance = float.MaxValue;
        for (var i = 0; i < polyline.Count - 1; i++)
        {
            distance = Math.Min(distance, DistanceToSegment(point, polyline[i], polyline[i + 1]));
        }

        return distance;
    }

    private static PointF[] ClosePolyline(PointF[] points)
    {
        if (points.Length == 0) return Array.Empty<PointF>();
        if (points.Length > 1 && SameDrawingUnit(points[0], points[^1])) return points.ToArray();
        var result = new PointF[points.Length + 1];
        Array.Copy(points, result, points.Length);
        result[^1] = points[0];
        return result;
    }

    private static PointF[] ReplaceBoundarySegment(
        IReadOnlyList<PointF> closedContour,
        int segmentIndex,
        int segmentCount,
        IReadOnlyList<PointF> replacement)
    {
        if (closedContour.Count < 4
            || segmentIndex < 0
            || segmentCount <= 0
            || segmentIndex + segmentCount >= closedContour.Count
            || replacement.Count < 2)
        {
            return Array.Empty<PointF>();
        }

        var result = new List<PointF>(closedContour.Count + replacement.Count);
        void Add(PointF point)
        {
            point = VectorUnits.Quantize(point);
            if (result.Count == 0 || !SameDrawingUnit(result[^1], point)) result.Add(point);
        }

        for (var index = 0; index < segmentIndex; index++) Add(closedContour[index]);
        for (var index = 0; index < replacement.Count; index++) Add(replacement[index]);
        for (var index = segmentIndex + segmentCount + 1; index < closedContour.Count; index++) Add(closedContour[index]);
        if (result.Count > 1 && SameDrawingUnit(result[0], result[^1])) result.RemoveAt(result.Count - 1);
        return result.Count >= 3 ? result.ToArray() : Array.Empty<PointF>();
    }

    private static bool BoundarySequenceMatches(
        IReadOnlyList<PointF> contour,
        int segmentIndex,
        IReadOnlyList<PointF> linePoints)
    {
        if (linePoints.Count < 2 || segmentIndex < 0 || segmentIndex + linePoints.Count > contour.Count) return false;
        for (var pointIndex = 0; pointIndex < linePoints.Count; pointIndex++)
        {
            if (!SameDrawingUnit(contour[segmentIndex + pointIndex], linePoints[pointIndex])) return false;
        }

        return true;
    }

    private static bool BoundaryEndpointsMatch(
        PointF boundaryStart,
        PointF boundaryEnd,
        PointF lineStart,
        PointF lineEnd)
    {
        return Distance(boundaryStart, lineStart) <= ConnectedStrokeEndpointToleranceUnits
            && Distance(boundaryEnd, lineEnd) <= ConnectedStrokeEndpointToleranceUnits;
    }

    private List<float> LineRectSplitParameters(int lineIndex, RectangleF bounds)
    {
        if (IsLineStraight(lineIndex)
            && TryGetLineEndpoint(lineIndex, true, out var lineStart)
            && TryGetLineEndpoint(lineIndex, false, out var lineEnd)
            && TryClipSegmentToRectangle(lineStart, lineEnd, bounds, out var enter, out var exit))
        {
            var straightCurve = LineCurve(lineIndex);
            var chordX = lineEnd.X - lineStart.X;
            var chordY = lineEnd.Y - lineStart.Y;
            var chordLengthSquared = chordX * chordX + chordY * chordY;
            var straightSplits = new List<DrawingTopologySplit>
            {
                new(0, lineStart),
                new(1, lineEnd)
            };
            AddStraightSplit(enter);
            AddStraightSplit(exit);
            return NormalizeStrokeSplits(straightSplits).Select(split => split.T).ToList();

            void AddStraightSplit(float chordParameter)
            {
                if (chordParameter <= 0.0001f || chordParameter >= 0.9999f) return;
                var intersection = Lerp(lineStart, lineEnd, chordParameter);
                var low = 0f;
                var high = 1f;
                for (var iteration = 0; iteration < 24; iteration++)
                {
                    var middle = (low + high) * 0.5f;
                    var point = CubicPoint(
                        straightCurve.Start,
                        straightCurve.Control1,
                        straightCurve.Control2,
                        straightCurve.End,
                        middle);
                    var projected = chordLengthSquared <= DrawingTopologyRules.UnitIntersectionTolerance
                        ? middle
                        : ((point.X - lineStart.X) * chordX + (point.Y - lineStart.Y) * chordY) / chordLengthSquared;
                    if (projected < chordParameter) low = middle;
                    else high = middle;
                }
                var parameter = (low + high) * 0.5f;
                straightSplits.Add(new DrawingTopologySplit(parameter, intersection));
            }
        }

        var splits = new List<DrawingTopologySplit>();
        var samples = CurveSamples(lineIndex);
        splits.Add(new DrawingTopologySplit(0, samples[0].Point));
        splits.Add(new DrawingTopologySplit(1, samples[^1].Point));

        for (var i = 0; i < samples.Length - 1; i++)
        {
            AddSegmentRectIntersections(splits, samples[i], samples[i + 1], bounds);
        }

        return NormalizeStrokeSplits(splits).Select(split => split.T).ToList();
    }

    private List<DrawingTopologySplit> CubicRectSplits(CubicBoundarySegment curve, RectangleF bounds)
    {
        var samples = SampleCubicSegmentWithParameters(curve);
        var splits = new List<DrawingTopologySplit>
        {
            new(0, curve.Start),
            new(1, curve.End)
        };
        for (var index = 0; index < samples.Length - 1; index++)
        {
            AddSegmentRectIntersections(splits, samples[index], samples[index + 1], bounds);
        }

        return NormalizeStrokeSplits(splits);
    }

    private static List<float> SegmentRectSplitParameters(PointF start, PointF end, RectangleF bounds)
    {
        var splits = new List<float> { 0, 1 };
        if (!TryClipSegmentToRectangle(start, end, bounds, out var enter, out var exit)) return splits;

        if (enter > 0.0001f && enter < 0.9999f) splits.Add(enter);
        if (exit > 0.0001f && exit < 0.9999f) splits.Add(exit);
        splits.Sort();

        var write = 1;
        for (var read = 1; read < splits.Count; read++)
        {
            if (splits[read] - splits[write - 1] <= 0.0001f) continue;
            splits[write++] = splits[read];
        }

        if (write < splits.Count) splits.RemoveRange(write, splits.Count - write);
        return splits;
    }

    private static List<float> PolylineRectSplitParameters(PointF[] points, RectangleF bounds)
    {
        var splits = new List<float> { 0, 1 };
        var segmentCount = points.Length - 1;
        if (segmentCount <= 0) return splits;

        for (var segment = 0; segment < segmentCount; segment++)
        {
            if (!TryClipSegmentToRectangle(points[segment], points[segment + 1], bounds, out var enter, out var exit))
            {
                continue;
            }

            var startInside = PointInRectangle(points[segment], bounds);
            var endInside = PointInRectangle(points[segment + 1], bounds);
            if (!startInside) AddSplit(enter);
            if (!endInside) AddSplit(exit);

            void AddSplit(float segmentParameter)
            {
                var parameter = (segment + Math.Clamp(segmentParameter, 0, 1)) / segmentCount;
                if (parameter <= 0.0001f || parameter >= 0.9999f) return;
                splits.Add(parameter);
            }
        }

        splits.Sort();
        var write = 1;
        for (var read = 1; read < splits.Count; read++)
        {
            if (splits[read] - splits[write - 1] <= 0.0001f) continue;
            splits[write++] = splits[read];
        }

        if (write < splits.Count) splits.RemoveRange(write, splits.Count - write);
        return splits;
    }

    private static bool TryClipSegmentToRectangle(PointF start, PointF end, RectangleF bounds, out float enter, out float exit)
    {
        enter = 0;
        exit = 1;
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;

        return Clip(-dx, start.X - bounds.Left, ref enter, ref exit)
            && Clip(dx, bounds.Right - start.X, ref enter, ref exit)
            && Clip(-dy, start.Y - bounds.Top, ref enter, ref exit)
            && Clip(dy, bounds.Bottom - start.Y, ref enter, ref exit);
    }

    private static bool Clip(float direction, float distance, ref float enter, ref float exit)
    {
        if (Math.Abs(direction) <= 0.000001f) return distance >= 0;

        var t = distance / direction;
        if (direction < 0)
        {
            if (t > exit) return false;
            if (t > enter) enter = t;
        }
        else
        {
            if (t < enter) return false;
            if (t < exit) exit = t;
        }

        return true;
    }

    private static void AddSegmentRectIntersections(List<DrawingTopologySplit> splits, CurveSample a, CurveSample b, RectangleF bounds)
    {
        var topLeft = new PointF(bounds.Left, bounds.Top);
        var topRight = new PointF(bounds.Right, bounds.Top);
        var bottomRight = new PointF(bounds.Right, bounds.Bottom);
        var bottomLeft = new PointF(bounds.Left, bounds.Bottom);
        AddSegmentRectIntersection(splits, a, b, topLeft, topRight);
        AddSegmentRectIntersection(splits, a, b, topRight, bottomRight);
        AddSegmentRectIntersection(splits, a, b, bottomRight, bottomLeft);
        AddSegmentRectIntersection(splits, a, b, bottomLeft, topLeft);
    }

    private static void AddSegmentRectIntersection(List<DrawingTopologySplit> splits, CurveSample a, CurveSample b, PointF edgeStart, PointF edgeEnd)
    {
        if (!TrySegmentIntersection(a.Point, b.Point, edgeStart, edgeEnd, out var localT)) return;
        var globalT = a.T + (b.T - a.T) * localT;
        if (globalT <= 0.0001f || globalT >= 0.9999f) return;
        splits.Add(new DrawingTopologySplit(globalT, Lerp(a.Point, b.Point, localT)));
    }

    private static PointF[] OpenPolygon(PointF[] polygon)
    {
        if (polygon.Length > 1 && SameDrawingUnit(polygon[0], polygon[^1])) return polygon[..^1];
        return polygon;
    }

    private static float PolygonArea(IReadOnlyList<PointF> polygon)
    {
        if (polygon.Count < 3) return 0;
        var origin = polygon[0];
        var area = 0d;
        for (var i = 1; i < polygon.Count - 1; i++)
        {
            var ax = (double)polygon[i].X - origin.X;
            var ay = (double)polygon[i].Y - origin.Y;
            var bx = (double)polygon[i + 1].X - origin.X;
            var by = (double)polygon[i + 1].Y - origin.Y;
            area += ax * by - bx * ay;
        }

        return (float)(area * 0.5d);
    }

    private static RectangleF NormalizeToDrawingUnits(RectangleF rect)
    {
        var normalized = Normalize(rect);
        var left = MathF.Floor(normalized.Left);
        var top = MathF.Floor(normalized.Top);
        var right = MathF.Ceiling(normalized.Right);
        var bottom = MathF.Ceiling(normalized.Bottom);
        return RectangleF.FromLTRB(left, top, Math.Max(left + 1, right), Math.Max(top + 1, bottom));
    }

    private static List<PointF> RemoveDuplicatePolygonPoints(List<PointF> points)
    {
        var result = new List<PointF>(points.Count);
        foreach (var point in points)
        {
            if (result.Count > 0 && SameDrawingUnit(result[^1], point)) continue;
            result.Add(point);
        }

        if (result.Count > 1 && SameDrawingUnit(result[0], result[^1])) result.RemoveAt(result.Count - 1);
        return result;
    }

    private static bool PointInPolygon(PointF point, PointF[] polygon)
    {
        var inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            var pi = polygon[i];
            var pj = polygon[j];
            if ((pi.Y > point.Y) == (pj.Y > point.Y)) continue;
            var denominator = pj.Y - pi.Y;
            if (Math.Abs(denominator) < 0.0001f) continue;
            var x = (pj.X - pi.X) * (point.Y - pi.Y) / denominator + pi.X;
            if (point.X < x) inside = !inside;
        }

        return inside;
    }

    private static bool PointInCompoundPolygon(PointF point, PointF[][] contours)
    {
        var inside = false;
        foreach (var contour in contours)
        {
            if (contour.Length < 3) continue;
            if (PointInPolygon(point, contour)) inside = !inside;
        }

        return inside;
    }

    private static bool PointInCompoundPolygonOrOnBoundary(PointF point, PointF[][] contours)
    {
        if (PointInCompoundPolygon(point, contours)) return true;
        foreach (var contour in contours)
        {
            if (contour.Length < 3) continue;
            for (var i = 0; i < contour.Length; i++)
            {
                if (DistanceToSegment(point, contour[i], contour[(i + 1) % contour.Length]) <= 0.75f) return true;
            }
        }

        return false;
    }

    private static float DistanceToCompoundPolygonBoundary(PointF point, PointF[][] contours)
    {
        var distance = float.MaxValue;
        foreach (var contour in contours)
        {
            if (contour.Length < 2) continue;
            for (var index = 0; index < contour.Length; index++)
            {
                distance = Math.Min(
                    distance,
                    DistanceToSegment(point, contour[index], contour[(index + 1) % contour.Length]));
            }
        }

        return distance;
    }

}
