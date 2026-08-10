using System.Numerics;
using Clipper2Lib;

namespace VectorAnimationEngine;

internal sealed partial class StageControl
{
    private const int Reference3DMaximumIntersectionCutsPerSurface = 12;
    private const int Reference3DMaximumFragmentsPerSurface = 64;
    private const double Reference3DIntersectionAreaEpsilon = 0.25d;
    private const double Reference3DIntersectionLineEpsilon = 0.000001d;
    private const double Reference3DIntersectionSegmentEpsilon = 0.75d;
    private const int Reference3DClipperPrecision = 3;

    private sealed class Reference3DIntersectionSurface
    {
        public Reference3DIntersectionSurface(Reference3DRenderItem fillItem, RectangleF bounds)
        {
            FillItem = fillItem;
            Bounds = bounds;
        }

        public Reference3DRenderItem FillItem { get; }

        public RectangleF Bounds { get; }

        public List<Reference3DScreenLine> Cuts { get; } = [];

        public List<Reference3DProjectedContour[]> PartitionRegions { get; } = [];
    }

    private readonly record struct Reference3DScreenLine(double A, double B, double C)
    {
        public PointF Origin => new((float)(-A * C), (float)(-B * C));

        public PointF Direction => new((float)-B, (float)A);

        public double SignedDistance(PointF point) => A * point.X + B * point.Y + C;

        public PointF PointAt(double distance)
        {
            var origin = Origin;
            var direction = Direction;
            return new PointF(
                (float)(origin.X + direction.X * distance),
                (float)(origin.Y + direction.Y * distance));
        }
    }

    private readonly record struct Reference3DIntersectionEdgePlan(
        Reference3DRenderItem Primary,
        Reference3DRenderItem Secondary,
        Reference3DProjectedContour[] Segments,
        int StableSlot);

    private readonly record struct Reference3DFragmentPlan(
        Reference3DProjectedContour[] Clip,
        float Depth,
        int Slot);

    private readonly record struct Reference3DFragmentBuildResult(
        bool Succeeded,
        Reference3DFragmentPlan[] Plans)
    {
        public static Reference3DFragmentBuildResult Failed { get; } = new(false, []);
    }

    private Reference3DRenderItem[] BuildReference3DIntersectionRenderItems(
        IReadOnlyList<Reference3DRenderItem> source)
    {
        if (source.Count < 2
            || ClientSize.Width <= 0
            || ClientSize.Height <= 0)
        {
            return source.ToArray();
        }

        var surfaces = new List<Reference3DIntersectionSurface>();
        foreach (var item in source)
        {
            if (item.Kind != Reference3DRenderKind.FrontFill
                || !item.Plane.IsValid
                || item.Contours.Length == 0
                || !TryGetReference3DProjectedBounds(item.Contours, 0, out var bounds)
                || bounds.Width <= 0
                || bounds.Height <= 0)
            {
                continue;
            }

            surfaces.Add(new Reference3DIntersectionSurface(item, bounds));
        }
        if (surfaces.Count < 2) return source.ToArray();

        surfaces.Sort((left, right) =>
        {
            var comparison = left.Bounds.Left.CompareTo(right.Bounds.Left);
            if (comparison != 0) return comparison;
            comparison = left.FillItem.ObjectIndex.CompareTo(right.FillItem.ObjectIndex);
            return comparison != 0
                ? comparison
                : left.FillItem.SurfaceSlot.CompareTo(right.FillItem.SurfaceSlot);
        });

        var edgePlans = new List<Reference3DIntersectionEdgePlan>();
        var stableEdgeSlot = 0;
        for (var leftIndex = 0; leftIndex < surfaces.Count; leftIndex++)
        {
            var left = surfaces[leftIndex];
            for (var rightIndex = leftIndex + 1; rightIndex < surfaces.Count; rightIndex++)
            {
                var right = surfaces[rightIndex];
                if (right.Bounds.Left > left.Bounds.Right + ReferenceSurfaceOverlapTolerancePixels) break;
                if (left.FillItem.ObjectIndex == right.FillItem.ObjectIndex
                    || left.FillItem.PlaneKey == right.FillItem.PlaneKey
                    || !Reference3DProjectedBoundsOverlap(left.Bounds, right.Bounds)
                    || !Reference3DRegionsOverlap(
                        left.FillItem.Contours,
                        right.FillItem.Contours))
                {
                    continue;
                }

                left.PartitionRegions.Add(right.FillItem.Contours);
                right.PartitionRegions.Add(left.FillItem.Contours);
                if (!TryGetReference3DProjectedIntersectionLine(
                        left.FillItem.Plane,
                        right.FillItem.Plane,
                        out var line)
                    || !TryGetReference3DIntersectionSegments(
                        line,
                        left.FillItem.Contours,
                        right.FillItem.Contours,
                        left.FillItem.Plane,
                        out var segments)
                    || segments.Length == 0)
                {
                    continue;
                }

                if (!CanAddReference3DIntersectionCut(left, line)
                    || !CanAddReference3DIntersectionCut(right, line))
                {
                    continue;
                }

                AddReference3DIntersectionCut(left, line);
                AddReference3DIntersectionCut(right, line);
                var primary = CompareReference3DCoplanarItems(left.FillItem, right.FillItem) <= 0
                    ? right.FillItem
                    : left.FillItem;
                var secondary = primary.ObjectIndex == left.FillItem.ObjectIndex
                    ? right.FillItem
                    : left.FillItem;
                edgePlans.Add(new Reference3DIntersectionEdgePlan(
                    primary,
                    secondary,
                    segments,
                    stableEdgeSlot++));
            }
        }
        if (edgePlans.Count == 0
            && surfaces.All(surface => surface.PartitionRegions.Count == 0))
        {
            return source.ToArray();
        }

        var fragmentBuilds = new Dictionary<
            (int ObjectIndex, int SurfaceSlot),
            Reference3DFragmentBuildResult>();
        var fragmentPlans = new Dictionary<(int ObjectIndex, int SurfaceSlot), Reference3DFragmentPlan[]>();
        foreach (var surface in surfaces)
        {
            if (surface.Cuts.Count == 0 && surface.PartitionRegions.Count == 0) continue;
            var key = (surface.FillItem.ObjectIndex, surface.FillItem.SurfaceSlot);
            var build = BuildReference3DSurfaceFragments(surface);
            fragmentBuilds[key] = build;
            if (build.Succeeded && build.Plans.Length > 1)
            {
                fragmentPlans[key] = build.Plans;
            }
        }

        var result = new List<Reference3DRenderItem>(source.Count + fragmentPlans.Count * 2);
        foreach (var item in source)
        {
            var surfaceKey = (item.ObjectIndex, item.SurfaceSlot);
            if (item.Kind is not (Reference3DRenderKind.FrontFill or Reference3DRenderKind.FrontStroke)
                || !fragmentPlans.TryGetValue(surfaceKey, out var fragments))
            {
                result.Add(item);
                continue;
            }

            foreach (var fragment in fragments)
            {
                result.Add(item with
                {
                    FragmentClip = fragment.Clip,
                    FragmentSlot = fragment.Slot,
                    AverageDepth = fragment.Depth
                });
            }
        }

        foreach (var edgePlan in edgePlans)
        {
            if (!fragmentBuilds.TryGetValue(
                    (edgePlan.Primary.ObjectIndex, edgePlan.Primary.SurfaceSlot),
                    out var primaryBuild)
                || !primaryBuild.Succeeded
                || !fragmentBuilds.TryGetValue(
                    (edgePlan.Secondary.ObjectIndex, edgePlan.Secondary.SurfaceSlot),
                    out var secondaryBuild)
                || !secondaryBuild.Succeeded)
            {
                continue;
            }

            var edgeColor = GetReference3DIntersectionEdgeColor(edgePlan.Primary);
            var edgeWidth = GetReference3DIntersectionEdgeWidth(edgePlan.Primary, edgePlan.Secondary);
            foreach (var segment in SplitReference3DIntersectionEdgeSegments(edgePlan, surfaces))
            {
                result.Add(new Reference3DRenderItem(
                    edgePlan.Primary.ObjectIndex,
                    edgePlan.Primary.LayerIndex,
                    Reference3DRenderKind.IntersectionEdge,
                    [segment],
                    segment.AverageDepth,
                    default,
                    edgePlan.Primary.ObjectSlot,
                    edgePlan.StableSlot)
                {
                    Plane = edgePlan.Primary.Plane,
                    SecondaryObjectIndex = edgePlan.Secondary.ObjectIndex,
                    EdgeArgb = edgeColor.ToArgb(),
                    EdgeWidth = edgeWidth
                });
            }
        }

        return result.ToArray();
    }

    private static bool CanAddReference3DIntersectionCut(
        Reference3DIntersectionSurface surface,
        Reference3DScreenLine candidate)
    {
        return surface.Cuts.Any(line => Reference3DScreenLinesEquivalent(line, candidate))
            || surface.Cuts.Count < Reference3DMaximumIntersectionCutsPerSurface;
    }

    private static void AddReference3DIntersectionCut(
        Reference3DIntersectionSurface surface,
        Reference3DScreenLine candidate)
    {
        if (!surface.Cuts.Any(line => Reference3DScreenLinesEquivalent(line, candidate)))
        {
            surface.Cuts.Add(candidate);
        }
    }

    private static bool Reference3DScreenLinesEquivalent(
        Reference3DScreenLine left,
        Reference3DScreenLine right)
    {
        return Math.Abs(left.A - right.A) <= 0.0001d
            && Math.Abs(left.B - right.B) <= 0.0001d
            && Math.Abs(left.C - right.C) <= 0.25d;
    }

    private Reference3DFragmentBuildResult BuildReference3DSurfaceFragments(
        Reference3DIntersectionSurface surface)
    {
        var strokeRadius = GetReference3DStrokeWidth(
            surface.FillItem.ObjectIndex,
            Scene.Stroke[surface.FillItem.ObjectIndex]) * 0.5f;
        var margin = Math.Max(2f, strokeRadius + 2f);
        var bounds = RectangleF.FromLTRB(
            surface.Bounds.Left - margin,
            surface.Bounds.Top - margin,
            surface.Bounds.Right + margin,
            surface.Bounds.Bottom + margin);
        var cells = new List<PointF[]>
        {
            new[]
            {
                new PointF(bounds.Left, bounds.Top),
                new PointF(bounds.Right, bounds.Top),
                new PointF(bounds.Right, bounds.Bottom),
                new PointF(bounds.Left, bounds.Bottom)
            }
        };

        foreach (var line in surface.Cuts)
        {
            var next = new List<PointF[]>(Math.Min(cells.Count * 2, Reference3DMaximumFragmentsPerSurface));
            foreach (var cell in cells)
            {
                var positive = ClipReference3DFragmentCell(cell, line, keepPositive: true);
                var negative = ClipReference3DFragmentCell(cell, line, keepPositive: false);
                var hasPositive = Reference3DPolygonArea(positive) > Reference3DIntersectionAreaEpsilon;
                var hasNegative = Reference3DPolygonArea(negative) > Reference3DIntersectionAreaEpsilon;
                if (hasPositive && hasNegative)
                {
                    next.Add(positive);
                    next.Add(negative);
                }
                else
                {
                    next.Add(cell);
                }
            }

            if (next.Count > Reference3DMaximumFragmentsPerSurface)
            {
                return Reference3DFragmentBuildResult.Failed;
            }
            cells = next;
        }

        var regions = cells
            .Select(cell => Reference3DContoursToPaths(
                [new Reference3DProjectedContour(cell, true, surface.FillItem.AverageDepth)]))
            .Where(Reference3DPathsHaveArea)
            .ToList();
        foreach (var partitionContours in surface.PartitionRegions)
        {
            var partition = Reference3DContoursToPaths(partitionContours);
            if (!Reference3DPathsHaveArea(partition)) continue;
            var next = new List<PathsD>(Math.Min(
                regions.Count * 2,
                Reference3DMaximumFragmentsPerSurface));
            foreach (var region in regions)
            {
                PathsD inside;
                PathsD outside;
                try
                {
                    inside = Clipper.Intersect(
                        region,
                        partition,
                        FillRule.EvenOdd,
                        Reference3DClipperPrecision);
                    outside = Clipper.Difference(
                        region,
                        partition,
                        FillRule.EvenOdd,
                        Reference3DClipperPrecision);
                }
                catch (Exception exception) when (Reference3DIsClipperFailure(exception))
                {
                    return Reference3DFragmentBuildResult.Failed;
                }

                var hasInside = Reference3DPathsHaveArea(inside);
                var hasOutside = Reference3DPathsHaveArea(outside);
                if (hasInside && hasOutside)
                {
                    next.Add(inside);
                    next.Add(outside);
                }
                else
                {
                    next.Add(region);
                }
            }
            if (next.Count > Reference3DMaximumFragmentsPerSurface)
            {
                return Reference3DFragmentBuildResult.Failed;
            }
            regions = next;
        }

        var fragments = new List<Reference3DFragmentPlan>(regions.Count);
        foreach (var region in regions)
        {
            var clip = Reference3DPathsToContours(region);
            if (!TryFindReference3DRegionPoint(
                    surface.FillItem.Contours,
                    clip,
                    out var sample))
            {
                continue;
            }

            var depth = TryGetReference3DPlaneDepthAtScreen(
                    surface.FillItem.Plane,
                    sample,
                    out var sampledDepth)
                ? sampledDepth
                : surface.FillItem.AverageDepth;
            fragments.Add(new Reference3DFragmentPlan(clip, depth, fragments.Count));
        }
        return fragments.Count == 0
            ? Reference3DFragmentBuildResult.Failed
            : new Reference3DFragmentBuildResult(true, fragments.ToArray());
    }

    private static PointF[] ClipReference3DFragmentCell(
        IReadOnlyList<PointF> source,
        Reference3DScreenLine line,
        bool keepPositive)
    {
        if (source.Count < 3) return [];
        var result = new List<PointF>(source.Count + 1);
        var previous = source[^1];
        var previousDistance = line.SignedDistance(previous) * (keepPositive ? 1d : -1d);
        var previousInside = previousDistance >= -Reference3DIntersectionLineEpsilon;
        foreach (var current in source)
        {
            var currentDistance = line.SignedDistance(current) * (keepPositive ? 1d : -1d);
            var currentInside = currentDistance >= -Reference3DIntersectionLineEpsilon;
            if (currentInside != previousInside)
            {
                var denominator = previousDistance - currentDistance;
                var amount = Math.Abs(denominator) <= Reference3DIntersectionLineEpsilon
                    ? 0d
                    : Math.Clamp(previousDistance / denominator, 0d, 1d);
                result.Add(new PointF(
                    (float)(previous.X + (current.X - previous.X) * amount),
                    (float)(previous.Y + (current.Y - previous.Y) * amount)));
            }
            if (currentInside) result.Add(current);
            previous = current;
            previousDistance = currentDistance;
            previousInside = currentInside;
        }
        return RemoveAdjacentReference3DDuplicatePoints(result);
    }

    private static PointF[] RemoveAdjacentReference3DDuplicatePoints(IReadOnlyList<PointF> source)
    {
        var result = new List<PointF>(source.Count);
        foreach (var point in source)
        {
            if (result.Count == 0 || ReferencePointDistance(result[^1], point) > 0.001f)
            {
                result.Add(point);
            }
        }
        if (result.Count > 1 && ReferencePointDistance(result[0], result[^1]) <= 0.001f)
        {
            result.RemoveAt(result.Count - 1);
        }
        return result.ToArray();
    }

    private static double Reference3DPolygonArea(IReadOnlyList<PointF> polygon)
    {
        if (polygon.Count < 3) return 0;
        double area = 0;
        var previous = polygon[^1];
        foreach (var current in polygon)
        {
            area += (double)previous.X * current.Y - (double)current.X * previous.Y;
            previous = current;
        }
        return Math.Abs(area) * 0.5d;
    }

    private bool TryGetReference3DProjectedIntersectionLine(
        Reference3DSurfacePlane left,
        Reference3DSurfacePlane right,
        out Reference3DScreenLine line)
    {
        line = default;
        var direction = Vector3.Cross(left.Normal, right.Normal);
        var directionLengthSquared = direction.LengthSquared();
        if (!left.IsValid
            || !right.IsValid
            || !Finite(direction)
            || !float.IsFinite(directionLengthSquared)
            || directionLengthSquared <= 0.0000001f)
        {
            return false;
        }

        var point = (left.Distance * Vector3.Cross(right.Normal, direction)
                + right.Distance * Vector3.Cross(direction, left.Normal))
            / directionLengthSquared;
        direction = Vector3.Normalize(direction);
        if (!Finite(point) || !Finite(direction)) return false;

        var first = Reference3DProjectHomogeneous(point);
        var second = Reference3DProjectHomogeneous(point + direction * 1024f);
        var a = first.Y * second.W - first.W * second.Y;
        var b = first.W * second.X - first.X * second.W;
        var c = first.X * second.Y - first.Y * second.X;
        var length = Math.Sqrt(a * a + b * b);
        if (!double.IsFinite(length) || length <= Reference3DIntersectionLineEpsilon) return false;
        a /= length;
        b /= length;
        c /= length;
        if (a < -Reference3DIntersectionLineEpsilon
            || Math.Abs(a) <= Reference3DIntersectionLineEpsilon && b < 0)
        {
            a = -a;
            b = -b;
            c = -c;
        }
        line = new Reference3DScreenLine(a, b, c);
        return double.IsFinite(a) && double.IsFinite(b) && double.IsFinite(c);
    }

    private (double X, double Y, double W) Reference3DProjectHomogeneous(Vector3 scenePoint)
    {
        var camera = CameraSpacePoint(new Point3(scenePoint.X, -scenePoint.Y, scenePoint.Z));
        var blend = ReferenceProjectionBlend;
        var w = ReferencePerspectiveFocalLength * (1d - blend) + blend * camera.Z;
        var scale = 0.035d * _referenceZoomScale * ReferencePerspectiveFocalLength;
        return (
            Width * 0.5d * w + camera.X * scale,
            Height * 0.58d * w - camera.Y * scale,
            w);
    }

    private bool TryGetReference3DIntersectionSegments(
        Reference3DScreenLine line,
        IReadOnlyList<Reference3DProjectedContour> leftContours,
        IReadOnlyList<Reference3DProjectedContour> rightContours,
        Reference3DSurfacePlane depthPlane,
        out Reference3DProjectedContour[] segments)
    {
        segments = [];
        var distances = new List<double>();
        AppendReference3DLineCrossings(line, leftContours, distances);
        AppendReference3DLineCrossings(line, rightContours, distances);
        if (distances.Count < 2) return false;
        distances.Sort();
        var unique = new List<double>(distances.Count);
        foreach (var distance in distances)
        {
            if (!double.IsFinite(distance)) continue;
            if (unique.Count == 0 || Math.Abs(distance - unique[^1]) > 0.01d) unique.Add(distance);
        }
        if (unique.Count < 2) return false;

        var visible = new List<(double Start, double End)>();
        for (var index = 1; index < unique.Count; index++)
        {
            var start = unique[index - 1];
            var end = unique[index];
            if (end - start <= Reference3DIntersectionSegmentEpsilon) continue;
            var midpoint = line.PointAt((start + end) * 0.5d);
            if (!PointInProjectedFill(midpoint, leftContours)
                || !PointInProjectedFill(midpoint, rightContours))
            {
                continue;
            }

            if (visible.Count > 0 && start - visible[^1].End <= 0.5d)
            {
                visible[^1] = (visible[^1].Start, end);
            }
            else
            {
                visible.Add((start, end));
            }
        }
        if (visible.Count == 0) return false;

        var result = new List<Reference3DProjectedContour>(visible.Count);
        foreach (var interval in visible)
        {
            var start = line.PointAt(interval.Start);
            var end = line.PointAt(interval.End);
            var midpoint = line.PointAt((interval.Start + interval.End) * 0.5d);
            if (!TryGetReference3DPlaneDepthAtScreen(depthPlane, midpoint, out var depth)) continue;
            result.Add(new Reference3DProjectedContour([start, end], false, depth));
        }
        segments = result.ToArray();
        return segments.Length > 0;
    }

    private Reference3DProjectedContour[] SplitReference3DIntersectionEdgeSegments(
        Reference3DIntersectionEdgePlan edgePlan,
        IReadOnlyList<Reference3DIntersectionSurface> surfaces)
    {
        var result = new List<Reference3DProjectedContour>();
        foreach (var segment in edgePlan.Segments)
        {
            if (segment.Points.Length < 2) continue;
            var start = segment.Points[0];
            var end = segment.Points[^1];
            var parameters = new List<double> { 0d, 1d };
            foreach (var surface in surfaces)
            {
                var surfaceItem = surface.FillItem;
                if (surfaceItem.ObjectIndex == edgePlan.Primary.ObjectIndex
                    || surfaceItem.ObjectIndex == edgePlan.Secondary.ObjectIndex)
                {
                    continue;
                }

                AppendReference3DSegmentBoundaryParameters(
                    start,
                    end,
                    surfaceItem.Contours,
                    parameters);
                AppendReference3DEdgeDepthCrossingParameter(
                    start,
                    end,
                    edgePlan.Primary.Plane,
                    surfaceItem.Plane,
                    parameters);
            }
            if (parameters.Count > 512)
            {
                result.Add(segment);
                continue;
            }

            parameters.Sort();
            var unique = new List<double>(parameters.Count);
            foreach (var parameter in parameters)
            {
                if (!double.IsFinite(parameter) || parameter < 0 || parameter > 1) continue;
                if (unique.Count == 0 || parameter - unique[^1] > 0.000001d)
                {
                    unique.Add(parameter);
                }
            }

            for (var index = 1; index < unique.Count; index++)
            {
                var from = unique[index - 1];
                var to = unique[index];
                var pieceStart = Reference3DLerp(start, end, from);
                var pieceEnd = Reference3DLerp(start, end, to);
                if (ReferencePointDistance(pieceStart, pieceEnd) <= Reference3DIntersectionSegmentEpsilon)
                {
                    continue;
                }
                var midpoint = Reference3DLerp(start, end, (from + to) * 0.5d);
                var depth = TryGetReference3DPlaneDepthAtScreen(
                        edgePlan.Primary.Plane,
                        midpoint,
                        out var sampledDepth)
                    ? sampledDepth
                    : segment.AverageDepth;
                result.Add(new Reference3DProjectedContour([pieceStart, pieceEnd], false, depth));
            }
        }
        return result.ToArray();
    }

    private void AppendReference3DEdgeDepthCrossingParameter(
        PointF start,
        PointF end,
        Reference3DSurfacePlane edgePlane,
        Reference3DSurfacePlane surfacePlane,
        ICollection<double> destination)
    {
        if (!edgePlane.IsValid
            || !surfacePlane.IsValid
            || !TryGetReference3DDepthDifference(start, edgePlane, surfacePlane, out var startDifference)
            || !TryGetReference3DDepthDifference(end, edgePlane, surfacePlane, out var endDifference))
        {
            return;
        }

        const float depthEpsilon = 0.01f;
        if (Math.Abs(startDifference) <= depthEpsilon) destination.Add(0);
        if (Math.Abs(endDifference) <= depthEpsilon) destination.Add(1);
        if (Math.Sign(startDifference) == Math.Sign(endDifference)) return;

        var low = 0d;
        var high = 1d;
        var lowDifference = startDifference;
        for (var iteration = 0; iteration < 28; iteration++)
        {
            var middle = (low + high) * 0.5d;
            var sample = Reference3DLerp(start, end, middle);
            if (!TryGetReference3DDepthDifference(
                    sample,
                    edgePlane,
                    surfacePlane,
                    out var middleDifference))
            {
                return;
            }
            if (Math.Abs(middleDifference) <= depthEpsilon)
            {
                low = middle;
                high = middle;
                break;
            }
            if (Math.Sign(middleDifference) == Math.Sign(lowDifference))
            {
                low = middle;
                lowDifference = middleDifference;
            }
            else
            {
                high = middle;
            }
        }
        destination.Add((low + high) * 0.5d);
    }

    private bool TryGetReference3DDepthDifference(
        PointF screen,
        Reference3DSurfacePlane left,
        Reference3DSurfacePlane right,
        out float difference)
    {
        difference = 0;
        return TryGetReference3DPlaneDepthAtScreen(left, screen, out var leftDepth)
            && TryGetReference3DPlaneDepthAtScreen(right, screen, out var rightDepth)
            && float.IsFinite(difference = leftDepth - rightDepth);
    }

    private static void AppendReference3DSegmentBoundaryParameters(
        PointF segmentStart,
        PointF segmentEnd,
        IReadOnlyList<Reference3DProjectedContour> contours,
        ICollection<double> destination)
    {
        foreach (var contour in contours)
        {
            if (!contour.Closed || contour.Points.Length < 3) continue;
            var previous = contour.Points[^1];
            foreach (var current in contour.Points)
            {
                AppendReference3DSegmentIntersectionParameters(
                    segmentStart,
                    segmentEnd,
                    previous,
                    current,
                    destination);
                previous = current;
            }
        }
    }

    private static void AppendReference3DSegmentIntersectionParameters(
        PointF segmentStart,
        PointF segmentEnd,
        PointF edgeStart,
        PointF edgeEnd,
        ICollection<double> destination)
    {
        var segmentX = (double)segmentEnd.X - segmentStart.X;
        var segmentY = (double)segmentEnd.Y - segmentStart.Y;
        var edgeX = (double)edgeEnd.X - edgeStart.X;
        var edgeY = (double)edgeEnd.Y - edgeStart.Y;
        var offsetX = (double)edgeStart.X - segmentStart.X;
        var offsetY = (double)edgeStart.Y - segmentStart.Y;
        var denominator = segmentX * edgeY - segmentY * edgeX;
        if (Math.Abs(denominator) <= Reference3DIntersectionLineEpsilon)
        {
            if (Math.Abs(offsetX * segmentY - offsetY * segmentX)
                > Reference3DIntersectionLineEpsilon)
            {
                return;
            }

            var lengthSquared = segmentX * segmentX + segmentY * segmentY;
            if (lengthSquared <= Reference3DIntersectionLineEpsilon) return;
            destination.Add((offsetX * segmentX + offsetY * segmentY) / lengthSquared);
            destination.Add(((edgeEnd.X - segmentStart.X) * segmentX
                + (edgeEnd.Y - segmentStart.Y) * segmentY) / lengthSquared);
            return;
        }

        var segmentAmount = (offsetX * edgeY - offsetY * edgeX) / denominator;
        var edgeAmount = (offsetX * segmentY - offsetY * segmentX) / denominator;
        if (segmentAmount >= -0.000001d
            && segmentAmount <= 1.000001d
            && edgeAmount >= -0.000001d
            && edgeAmount <= 1.000001d)
        {
            destination.Add(Math.Clamp(segmentAmount, 0d, 1d));
        }
    }

    private static PointF Reference3DLerp(PointF start, PointF end, double amount)
    {
        return new PointF(
            (float)(start.X + (end.X - start.X) * amount),
            (float)(start.Y + (end.Y - start.Y) * amount));
    }

    private static void AppendReference3DLineCrossings(
        Reference3DScreenLine line,
        IReadOnlyList<Reference3DProjectedContour> contours,
        ICollection<double> destination)
    {
        var origin = line.Origin;
        var direction = line.Direction;
        foreach (var contour in contours)
        {
            if (!contour.Closed || contour.Points.Length < 3) continue;
            var previous = contour.Points[^1];
            var previousDistance = line.SignedDistance(previous);
            foreach (var current in contour.Points)
            {
                var currentDistance = line.SignedDistance(current);
                if (Math.Abs(previousDistance) <= Reference3DIntersectionLineEpsilon
                    && Math.Abs(currentDistance) <= Reference3DIntersectionLineEpsilon)
                {
                    destination.Add(Reference3DLineParameter(previous, origin, direction));
                    destination.Add(Reference3DLineParameter(current, origin, direction));
                }
                else if ((previousDistance <= 0 && currentDistance >= 0)
                    || (previousDistance >= 0 && currentDistance <= 0))
                {
                    var denominator = previousDistance - currentDistance;
                    var amount = Math.Abs(denominator) <= Reference3DIntersectionLineEpsilon
                        ? 0d
                        : Math.Clamp(previousDistance / denominator, 0d, 1d);
                    var intersection = new PointF(
                        (float)(previous.X + (current.X - previous.X) * amount),
                        (float)(previous.Y + (current.Y - previous.Y) * amount));
                    destination.Add(Reference3DLineParameter(intersection, origin, direction));
                }
                previous = current;
                previousDistance = currentDistance;
            }
        }
    }

    private static double Reference3DLineParameter(PointF point, PointF origin, PointF direction)
    {
        return (point.X - origin.X) * direction.X + (point.Y - origin.Y) * direction.Y;
    }

    private bool TryGetReference3DPlaneDepthAtScreen(
        Reference3DSurfacePlane plane,
        PointF screen,
        out float depth)
    {
        depth = 0;
        if (!plane.IsValid || !TryGetReference3DRay(screen, out var ray)) return false;
        var denominator = Vector3.Dot(plane.Normal, ray.Direction);
        if (!float.IsFinite(denominator) || Math.Abs(denominator) <= 0.000001f) return false;
        var distance = (plane.Distance - Vector3.Dot(plane.Normal, ray.Origin)) / denominator;
        if (!float.IsFinite(distance) || distance <= 0) return false;
        var scenePoint = ray.Origin + ray.Direction * distance;
        return Finite(scenePoint) && TryProjectScenePosition(scenePoint, out _, out depth);
    }

    private bool TryGetReference3DRay(PointF screen, out SpatialRay ray)
    {
        ray = default;
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return false;
        var scale = 0.035f * _referenceZoomScale;
        if (scale <= 0.000001f) return false;
        var cameraX = (screen.X - Width * 0.5f) / scale;
        var cameraY = -(screen.Y - Height * 0.58f) / scale;
        var blend = ReferenceProjectionBlend;
        var cameraOrigin = new Vector3(cameraX * (1 - blend), cameraY * (1 - blend), 0);
        var cameraDirection = Vector3.Normalize(new Vector3(
            cameraX * blend / ReferencePerspectiveFocalLength,
            cameraY * blend / ReferencePerspectiveFocalLength,
            1));
        var referenceOrigin = CameraToReference(cameraOrigin, direction: false);
        var referenceDirection = Vector3.Normalize(CameraToReference(cameraDirection, direction: true));
        var sceneOrigin = new Vector3(referenceOrigin.X, -referenceOrigin.Y, referenceOrigin.Z);
        var sceneDirection = Vector3.Normalize(new Vector3(
            referenceDirection.X,
            -referenceDirection.Y,
            referenceDirection.Z));
        if (!Finite(sceneOrigin) || !Finite(sceneDirection)) return false;
        ray = new SpatialRay(sceneOrigin, sceneDirection);
        return true;
    }

    private Color GetReference3DIntersectionEdgeColor(Reference3DRenderItem source)
    {
        var objectIndex = source.ObjectIndex;
        var shape = Scene.ShapeKind[objectIndex];
        if (SceneRenderOrder.HasStroke(shape, Scene.Stroke[objectIndex]))
        {
            var stroke = Color.FromArgb(Scene.StrokeArgb[objectIndex]);
            if (stroke.A > 0) return stroke;
        }

        var layerColor = Scene.GetEffectiveLayerOutlineColor(source.LayerIndex);
        if (!layerColor.IsEmpty && layerColor.A > 0) return layerColor;
        var fill = Color.FromArgb(Scene.Argb[objectIndex]);
        var brightness = (fill.R * 299 + fill.G * 587 + fill.B * 114) / 1000;
        return brightness >= 128
            ? Color.FromArgb(fill.A, fill.R * 3 / 5, fill.G * 3 / 5, fill.B * 3 / 5)
            : Color.FromArgb(
                fill.A,
                fill.R + (255 - fill.R) * 2 / 5,
                fill.G + (255 - fill.G) * 2 / 5,
                fill.B + (255 - fill.B) * 2 / 5);
    }

    private float GetReference3DIntersectionEdgeWidth(
        Reference3DRenderItem left,
        Reference3DRenderItem right)
    {
        var leftWidth = GetReference3DStrokeWidth(left.ObjectIndex, Scene.Stroke[left.ObjectIndex]);
        var rightWidth = GetReference3DStrokeWidth(right.ObjectIndex, Scene.Stroke[right.ObjectIndex]);
        return Math.Max(1f, Math.Max(leftWidth, rightWidth));
    }

    private static bool IsPointWithinReference3DFragment(Point point, Reference3DRenderItem item)
    {
        return item.FragmentClip is not { Length: > 0 }
            || PointInProjectedFill(point, item.FragmentClip);
    }

    private static bool Reference3DFragmentClipsOverlap(
        Reference3DRenderItem left,
        Reference3DRenderItem right)
    {
        if (left.ObjectIndex == right.ObjectIndex
            && left.SurfaceSlot == right.SurfaceSlot
            && left.FragmentSlot != right.FragmentSlot
            && left.FragmentClip is { Length: > 0 }
            && right.FragmentClip is { Length: > 0 })
        {
            return false;
        }
        if (left.FragmentClip is not { Length: > 0 }
            || right.FragmentClip is not { Length: > 0 })
        {
            return true;
        }

        return Reference3DRegionsOverlap(left.FragmentClip, right.FragmentClip);
    }

    private Reference3DRenderItem[] SortReference3DIntersectingRenderGroups(
        IReadOnlyList<Reference3DRenderGroup> sourceGroups)
    {
        var groups = sourceGroups.ToArray();
        if (groups.Length <= 1) return groups.SelectMany(group => group.Items).ToArray();

        var representatives = new Reference3DRenderItem?[groups.Length];
        var regions = new PathsD?[groups.Length];
        var bounds = new RectangleF[groups.Length];
        var hasBounds = new bool[groups.Length];
        for (var index = 0; index < groups.Length; index++)
        {
            if (TryGetReference3DGroupSurfaceItem(groups[index], out var representative))
            {
                representatives[index] = representative;
                regions[index] = TryBuildReference3DItemRegion(representative, out var region)
                    ? region
                    : null;
            }
            hasBounds[index] = TryGetReference3DGroupBounds(groups[index], out bounds[index]);
        }

        var edges = Enumerable.Range(0, groups.Length).Select(_ => new HashSet<int>()).ToArray();
        var indegree = new int[groups.Length];
        foreach (var (leftIndex, rightIndex) in GetReference3DConstraintCandidates(bounds, hasBounds))
        {
            if (TryAddReference3DIntersectionEdgeConstraint(
                    groups,
                    leftIndex,
                    rightIndex,
                    edges,
                    indegree))
            {
                continue;
            }

            var leftHasEdge = TryGetReference3DGroupEdgeItem(groups[leftIndex], out var leftEdge);
            var rightHasEdge = TryGetReference3DGroupEdgeItem(groups[rightIndex], out var rightEdge);
            if (leftHasEdge || rightHasEdge)
            {
                if (leftHasEdge == rightHasEdge) continue;
                var edgeIndex = leftHasEdge ? leftIndex : rightIndex;
                var surfaceIndex = leftHasEdge ? rightIndex : leftIndex;
                var edge = leftHasEdge ? leftEdge : rightEdge;
                if (representatives[surfaceIndex] is not { } surface
                    || regions[surfaceIndex] is not { } surfaceRegion
                    || !TryFindReference3DEdgeRegionOverlapPoint(edge, surfaceRegion, out var edgeSample)
                    || !TryGetReference3DPlaneDepthAtScreen(edge.Plane, edgeSample, out var edgeDepth)
                    || !TryGetReference3DPlaneDepthAtScreen(surface.Plane, edgeSample, out var surfaceDepth))
                {
                    continue;
                }

                if (edgeDepth > surfaceDepth + 0.01f)
                {
                    AddReference3DRenderConstraint(edgeIndex, surfaceIndex, edges, indegree);
                }
                else
                {
                    AddReference3DRenderConstraint(surfaceIndex, edgeIndex, edges, indegree);
                }
                continue;
            }

            if (representatives[leftIndex] is not { } left
                || representatives[rightIndex] is not { } right
                || regions[leftIndex] is not { } leftRegion
                || regions[rightIndex] is not { } rightRegion
                || !TryFindReference3DRegionOverlapPoint(leftRegion, rightRegion, out var sample)
                || !TryGetReference3DPlaneDepthAtScreen(left.Plane, sample, out var leftDepth)
                || !TryGetReference3DPlaneDepthAtScreen(right.Plane, sample, out var rightDepth))
            {
                continue;
            }

            if (Math.Abs(leftDepth - rightDepth) > 0.01f)
            {
                if (leftDepth > rightDepth)
                {
                    AddReference3DRenderConstraint(leftIndex, rightIndex, edges, indegree);
                }
                else
                {
                    AddReference3DRenderConstraint(rightIndex, leftIndex, edges, indegree);
                }
            }
            else
            {
                var comparison = CompareReference3DCoplanarItems(left, right);
                if (comparison < 0)
                {
                    AddReference3DRenderConstraint(leftIndex, rightIndex, edges, indegree);
                }
                else if (comparison > 0)
                {
                    AddReference3DRenderConstraint(rightIndex, leftIndex, edges, indegree);
                }
            }
        }

        var readyComparer = Comparer<int>.Create((left, right) =>
        {
            var comparison = CompareReference3DRenderGroups(groups[left], groups[right]);
            return comparison != 0 ? comparison : left.CompareTo(right);
        });
        var ready = new SortedSet<int>(
            Enumerable.Range(0, groups.Length).Where(index => indegree[index] == 0),
            readyComparer);
        var ordered = new List<int>(groups.Length);
        while (ready.Count > 0)
        {
            var current = ready.Min;
            ready.Remove(current);
            ordered.Add(current);
            foreach (var next in edges[current].Order())
            {
                indegree[next]--;
                if (indegree[next] == 0) ready.Add(next);
            }
        }

        if (ordered.Count != groups.Length)
        {
            Array.Sort(groups, CompareReference3DRenderGroups);
            ordered = Enumerable.Range(0, groups.Length).ToList();
        }

        var result = new List<Reference3DRenderItem>();
        foreach (var groupIndex in ordered)
        {
            var items = groups[groupIndex].Items;
            if (groups[groupIndex].PlaneKey.IsValid)
            {
                Array.Sort(items, CompareReference3DCoplanarItems);
            }
            result.AddRange(items);
        }
        return result.ToArray();
    }

    private static IEnumerable<(int Left, int Right)> GetReference3DConstraintCandidates(
        IReadOnlyList<RectangleF> bounds,
        IReadOnlyList<bool> hasBounds)
    {
        var ordered = Enumerable.Range(0, bounds.Count)
            .Where(index => hasBounds[index])
            .OrderBy(index => bounds[index].Left)
            .ThenBy(index => index)
            .ToArray();
        for (var orderIndex = 0; orderIndex < ordered.Length; orderIndex++)
        {
            var leftIndex = ordered[orderIndex];
            var leftBounds = bounds[leftIndex];
            for (var candidateOrder = orderIndex + 1; candidateOrder < ordered.Length; candidateOrder++)
            {
                var rightIndex = ordered[candidateOrder];
                var rightBounds = bounds[rightIndex];
                if (rightBounds.Left > leftBounds.Right + ReferenceSurfaceOverlapTolerancePixels) break;
                if (Reference3DProjectedBoundsOverlap(leftBounds, rightBounds))
                {
                    yield return (leftIndex, rightIndex);
                }
            }
        }
    }

    private static bool TryGetReference3DGroupSurfaceItem(
        Reference3DRenderGroup group,
        out Reference3DRenderItem item)
    {
        foreach (var candidate in group.Items)
        {
            if (candidate.Kind == Reference3DRenderKind.FrontFill && candidate.Plane.IsValid)
            {
                item = candidate;
                return true;
            }
        }
        item = default;
        return false;
    }

    private static bool TryGetReference3DGroupEdgeItem(
        Reference3DRenderGroup group,
        out Reference3DRenderItem item)
    {
        foreach (var candidate in group.Items)
        {
            if (candidate.Kind != Reference3DRenderKind.IntersectionEdge) continue;
            item = candidate;
            return true;
        }
        item = default;
        return false;
    }

    private static bool TryGetReference3DGroupBounds(
        Reference3DRenderGroup group,
        out RectangleF bounds)
    {
        bounds = RectangleF.Empty;
        var hasBounds = false;
        foreach (var item in group.Items)
        {
            var radius = item.Kind == Reference3DRenderKind.IntersectionEdge
                ? Math.Max(0.5f, item.EdgeWidth * 0.5f)
                : 0f;
            if (!TryGetReference3DProjectedBounds(item.FragmentClip ?? item.Contours, radius, out var itemBounds))
            {
                continue;
            }
            bounds = hasBounds ? RectangleF.Union(bounds, itemBounds) : itemBounds;
            hasBounds = true;
        }
        return hasBounds;
    }

    private bool TryAddReference3DIntersectionEdgeConstraint(
        IReadOnlyList<Reference3DRenderGroup> groups,
        int leftIndex,
        int rightIndex,
        IReadOnlyList<HashSet<int>> edges,
        int[] indegree)
    {
        var leftEdge = groups[leftIndex].Items.FirstOrDefault(
            item => item.Kind == Reference3DRenderKind.IntersectionEdge);
        var rightEdge = groups[rightIndex].Items.FirstOrDefault(
            item => item.Kind == Reference3DRenderKind.IntersectionEdge);
        var leftIsEdge = leftEdge.Kind == Reference3DRenderKind.IntersectionEdge;
        var rightIsEdge = rightEdge.Kind == Reference3DRenderKind.IntersectionEdge;
        if (leftIsEdge == rightIsEdge) return false;

        var edge = leftIsEdge ? leftEdge : rightEdge;
        var edgeIndex = leftIsEdge ? leftIndex : rightIndex;
        var surfaceIndex = leftIsEdge ? rightIndex : leftIndex;
        var belongsToPair = false;
        foreach (var item in groups[surfaceIndex].Items)
        {
            if (item.Kind != Reference3DRenderKind.FrontFill
                || (item.ObjectIndex != edge.ObjectIndex
                    && item.ObjectIndex != edge.SecondaryObjectIndex)
                || !TryBuildReference3DItemRegion(item, out var region)
                || !TryFindReference3DEdgeRegionContactPoint(edge, region, out _))
            {
                continue;
            }
            belongsToPair = true;
            break;
        }
        if (!belongsToPair) return false;
        AddReference3DRenderConstraint(surfaceIndex, edgeIndex, edges, indegree);
        return true;
    }

    private static void AddReference3DRenderConstraint(
        int before,
        int after,
        IReadOnlyList<HashSet<int>> edges,
        int[] indegree)
    {
        if (before == after || !edges[before].Add(after)) return;
        indegree[after]++;
    }

    private static bool TryBuildReference3DItemRegion(
        Reference3DRenderItem item,
        out PathsD region)
    {
        region = Reference3DContoursToPaths(item.Contours);
        if (region.Count == 0) return false;
        if (item.FragmentClip is not { Length: > 0 }) return true;
        try
        {
            region = Clipper.Intersect(
                region,
                Reference3DContoursToPaths(item.FragmentClip),
                FillRule.EvenOdd,
                Reference3DClipperPrecision);
            return Reference3DPathsHaveArea(region);
        }
        catch (Exception exception) when (Reference3DIsClipperFailure(exception))
        {
            region = [];
            return false;
        }
    }

    private static bool Reference3DRegionsOverlap(
        IReadOnlyList<Reference3DProjectedContour> left,
        IReadOnlyList<Reference3DProjectedContour> right)
    {
        try
        {
            var intersection = Clipper.Intersect(
                Reference3DContoursToPaths(left),
                Reference3DContoursToPaths(right),
                FillRule.EvenOdd,
                Reference3DClipperPrecision);
            return Reference3DPathsHaveArea(intersection);
        }
        catch (Exception exception) when (Reference3DIsClipperFailure(exception))
        {
            return true;
        }
    }

    private static bool TryFindReference3DRegionOverlapPoint(
        PathsD left,
        PathsD right,
        out PointF point)
    {
        point = PointF.Empty;
        try
        {
            var intersection = Clipper.Intersect(
                left,
                right,
                FillRule.EvenOdd,
                Reference3DClipperPrecision);
            return TryFindReference3DPointInPaths(intersection, out point);
        }
        catch (Exception exception) when (Reference3DIsClipperFailure(exception))
        {
            return false;
        }
    }

    private static bool TryFindReference3DEdgeRegionOverlapPoint(
        Reference3DRenderItem edge,
        PathsD region,
        out PointF point)
    {
        point = PointF.Empty;
        var contours = Reference3DPathsToContours(region);
        foreach (var segment in edge.Contours)
        {
            if (segment.Points.Length < 2) continue;
            var start = segment.Points[0];
            var end = segment.Points[^1];
            var parameters = new List<double> { 0d, 1d };
            AppendReference3DSegmentBoundaryParameters(start, end, contours, parameters);
            parameters.Sort();
            var previous = double.NaN;
            foreach (var parameter in parameters)
            {
                if (!double.IsFinite(parameter)
                    || parameter < 0
                    || parameter > 1
                    || double.IsFinite(previous) && parameter - previous <= 0.000001d)
                {
                    continue;
                }
                if (double.IsFinite(previous))
                {
                    var candidate = Reference3DLerp(start, end, (previous + parameter) * 0.5d);
                    if (PointInProjectedFill(candidate, contours))
                    {
                        point = candidate;
                        return true;
                    }
                }
                previous = parameter;
            }
        }
        return false;
    }

    private static bool TryFindReference3DEdgeRegionContactPoint(
        Reference3DRenderItem edge,
        PathsD region,
        out PointF point)
    {
        if (TryFindReference3DEdgeRegionOverlapPoint(edge, region, out point)) return true;
        var contours = Reference3DPathsToContours(region);
        const float contactOffset = 0.5f;
        foreach (var segment in edge.Contours)
        {
            if (segment.Points.Length < 2) continue;
            var start = segment.Points[0];
            var end = segment.Points[^1];
            var dx = end.X - start.X;
            var dy = end.Y - start.Y;
            var length = MathF.Sqrt(dx * dx + dy * dy);
            if (!float.IsFinite(length) || length <= 0.000001f) continue;
            var normalX = -dy / length * contactOffset;
            var normalY = dx / length * contactOffset;
            for (var sampleIndex = 1; sampleIndex < 8; sampleIndex++)
            {
                var amount = sampleIndex / 8f;
                var candidate = new PointF(start.X + dx * amount, start.Y + dy * amount);
                var positive = new PointF(candidate.X + normalX, candidate.Y + normalY);
                var negative = new PointF(candidate.X - normalX, candidate.Y - normalY);
                if (!PointInProjectedFill(candidate, contours)
                    && !PointInProjectedFill(positive, contours)
                    && !PointInProjectedFill(negative, contours))
                {
                    continue;
                }
                point = candidate;
                return true;
            }
        }
        point = PointF.Empty;
        return false;
    }

    private static bool TryFindReference3DRegionPoint(
        IReadOnlyList<Reference3DProjectedContour> surface,
        IReadOnlyList<Reference3DProjectedContour> clip,
        out PointF point)
    {
        point = PointF.Empty;
        try
        {
            var intersection = Clipper.Intersect(
                Reference3DContoursToPaths(surface),
                Reference3DContoursToPaths(clip),
                FillRule.EvenOdd,
                Reference3DClipperPrecision);
            return TryFindReference3DPointInPaths(intersection, out point);
        }
        catch (Exception exception) when (Reference3DIsClipperFailure(exception))
        {
            return false;
        }
    }

    private static PathsD Reference3DContoursToPaths(
        IReadOnlyList<Reference3DProjectedContour> contours)
    {
        return new PathsD(contours
            .Where(contour => contour.Closed
                && contour.Points.Length >= 3
                && contour.Points.All(point => float.IsFinite(point.X) && float.IsFinite(point.Y)))
            .Select(contour => new PathD(
                contour.Points.Select(point => new PointD(point.X, point.Y)))));
    }

    private static bool Reference3DPathsHaveArea(PathsD paths)
    {
        return paths.Any(path => path.Count >= 3
            && Math.Abs(Clipper.Area(path)) > Reference3DIntersectionAreaEpsilon);
    }

    private static bool TryFindReference3DPointInPaths(PathsD paths, out PointF point)
    {
        point = PointF.Empty;
        if (!Reference3DPathsHaveArea(paths)) return false;
        var contours = Reference3DPathsToContours(paths);
        if (!TryGetReference3DProjectedBounds(contours, 0, out var bounds)) return false;

        var boundsCenter = new PointF(bounds.Left + bounds.Width * 0.5f, bounds.Top + bounds.Height * 0.5f);
        if (PointInProjectedFill(boundsCenter, contours))
        {
            point = boundsCenter;
            return true;
        }

        foreach (var contour in contours.OrderByDescending(contour =>
                     Reference3DPolygonArea(contour.Points)))
        {
            double signedArea = 0;
            double centerX = 0;
            double centerY = 0;
            var previous = contour.Points[^1];
            foreach (var current in contour.Points)
            {
                var cross = (double)previous.X * current.Y - (double)current.X * previous.Y;
                signedArea += cross;
                centerX += (previous.X + current.X) * cross;
                centerY += (previous.Y + current.Y) * cross;
                previous = current;
            }
            if (Math.Abs(signedArea) <= 0.000001d) continue;
            var candidate = new PointF(
                (float)(centerX / (3d * signedArea)),
                (float)(centerY / (3d * signedArea)));
            if (!PointInProjectedFill(candidate, contours)) continue;
            point = candidate;
            return true;
        }

        var yLevels = contours
            .SelectMany(contour => contour.Points)
            .Select(value => value.Y)
            .Where(float.IsFinite)
            .Distinct()
            .Order()
            .ToArray();
        var scanLines = new List<(float Y, float Span)>(Math.Max(0, yLevels.Length - 1));
        for (var index = 1; index < yLevels.Length; index++)
        {
            var span = yLevels[index] - yLevels[index - 1];
            if (span > 0.000001f)
            {
                scanLines.Add(((yLevels[index] + yLevels[index - 1]) * 0.5f, span));
            }
        }

        foreach (var scanLine in scanLines.OrderByDescending(value => value.Span))
        {
            var crossings = new List<float>();
            foreach (var contour in contours)
            {
                var previous = contour.Points[^1];
                foreach (var current in contour.Points)
                {
                    if ((current.Y > scanLine.Y) != (previous.Y > scanLine.Y))
                    {
                        crossings.Add(current.X
                            + (previous.X - current.X) * (scanLine.Y - current.Y)
                            / (previous.Y - current.Y));
                    }
                    previous = current;
                }
            }
            crossings.Sort();
            for (var index = 1; index < crossings.Count; index++)
            {
                if (crossings[index] - crossings[index - 1] <= 0.000001f) continue;
                var candidate = new PointF(
                    (crossings[index] + crossings[index - 1]) * 0.5f,
                    scanLine.Y);
                if (!PointInProjectedFill(candidate, contours)) continue;
                point = candidate;
                return true;
            }
        }
        return false;
    }

    private static Reference3DProjectedContour[] Reference3DPathsToContours(PathsD paths)
    {
        return paths
            .Where(path => path.Count >= 3)
            .Select(path => new Reference3DProjectedContour(
                path.Select(value => new PointF((float)value.x, (float)value.y)).ToArray(),
                true,
                0))
            .ToArray();
    }

    private static bool Reference3DIsClipperFailure(Exception exception)
    {
        return exception is ClipperLibException
            or OverflowException
            or ArgumentException
            or InvalidOperationException;
    }
}
