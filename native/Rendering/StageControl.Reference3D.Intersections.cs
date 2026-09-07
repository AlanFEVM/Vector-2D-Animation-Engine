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
    private const double Reference3DIntersectionPieceEpsilon = 0.001d;
    private const int Reference3DClipperPrecision = 3;
    private const int Reference3DMaximumConvexOverlapVertices = 1024;
    private const double Reference3DConvexTolerancePixels = 0.001d;
    private const int Reference3DConvexTriangulationReuseThreshold = 8;
    private sealed class Reference3DIntersectionSurface
    {
        public Reference3DIntersectionSurface(
            Reference3DRenderItem fillItem,
            Reference3DProjectedContour[] contours,
            RectangleF bounds)
        {
            FillItem = fillItem;
            Contours = contours;
            Bounds = bounds;
        }

        public Reference3DRenderItem FillItem { get; }

        public Reference3DProjectedContour[] Contours { get; }

        public RectangleF Bounds { get; }

        public PathsD Region { get; set; } = [];

        public bool RegionInitialized { get; set; }

        public PointF[]? ConvexContourPoints { get; set; }

        public int ConvexContourPointCount { get; set; }

        public bool ConvexContourInitialized { get; set; }

        public List<Reference3DIntersectionCut> Cuts { get; } = [];

        public List<Reference3DPathRegion> PartitionRegions { get; } = [];
    }

    private readonly record struct Reference3DPathRegion(
        PathsD Paths,
        RectangleF Bounds,
        Reference3DProjectedContour[]? StableSourceContours = null,
        int StableObjectIndex = -1,
        int StableSurfaceKind = -1,
        int StableSurfaceSlot = -1);

    private readonly record struct Reference3DIntersectionCut(
        Reference3DScreenLine Line,
        Reference3DSurfacePlane OtherPlane,
        int StableObjectIndex,
        int StableSurfaceKind,
        int StableSurfaceSlot);

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

    private readonly record struct Reference3DIntersectionEdgeSegment(
        Reference3DProjectedContour Contour,
        bool StartCap,
        bool EndCap);

    private sealed class Reference3DIntersectionPathCache
    {
        private Dictionary<Reference3DProjectedContour[], PathsD>? _pathsByContours;
        private Dictionary<Reference3DProjectedContour[], PathsD>? _visibleFillPathsByClip;
        private Dictionary<PathsD, Reference3DConvexRegion?>? _convexRegions;
        private Dictionary<PathsD, Reference3DConvexRegion[]?>? _convexRegionParts;
        private Dictionary<PathsD, int>? _convexRegionPartRequests;

        public bool PreferConvexRegionParts { get; set; }

        public PathsD GetOrAddPaths(Reference3DProjectedContour[] contours)
        {
            if (_pathsByContours is not null
                && _pathsByContours.TryGetValue(contours, out var paths))
            {
                return paths;
            }
            paths = Reference3DContoursToPaths(contours);
            (_pathsByContours ??= []).Add(contours, paths);
            return paths;
        }

        public void AddPaths(Reference3DProjectedContour[] contours, PathsD paths)
        {
            (_pathsByContours ??= []).TryAdd(contours, paths);
        }

        public void AddVisibleFillPaths(Reference3DProjectedContour[] clip, PathsD paths)
        {
            (_visibleFillPathsByClip ??= []).TryAdd(clip, paths);
        }

        public bool TryGetVisibleFillPaths(
            Reference3DProjectedContour[] clip,
            out PathsD paths)
        {
            if (_visibleFillPathsByClip is not null
                && _visibleFillPathsByClip.TryGetValue(clip, out paths!))
            {
                return true;
            }
            paths = [];
            return false;
        }

        public bool TryGetConvexRegion(PathsD paths, out Reference3DConvexRegion region)
        {
            if (_convexRegions is not null
                && _convexRegions.TryGetValue(paths, out var cached))
            {
                region = cached.GetValueOrDefault();
                return cached.HasValue;
            }

            var valid = TryAnalyzeReference3DConvexRegion(paths, out region);
            (_convexRegions ??= []).Add(paths, valid ? region : null);
            return valid;
        }

        public void AddKnownConvexRegion(PathsD paths)
        {
            if (paths.Count != 1
                || !TryAnalyzeReference3DConvexPath(
                    paths[0],
                    out var region,
                    validateConvexity: false))
            {
                return;
            }
            (_convexRegions ??= []).TryAdd(paths, region);
        }

        public bool TryGetConvexRegionParts(
            PathsD paths,
            out Reference3DConvexRegion[] regions)
        {
            if (_convexRegionParts is not null
                && _convexRegionParts.TryGetValue(paths, out var cached))
            {
                regions = cached ?? [];
                return cached is not null;
            }
            regions = [];

            if (TryGetConvexRegion(paths, out var singleRegion))
            {
                regions = [singleRegion];
                (_convexRegionParts ??= []).Add(paths, regions);
                return true;
            }

            _convexRegionPartRequests ??= [];
            _convexRegionPartRequests.TryGetValue(paths, out var requestCount);
            requestCount++;
            _convexRegionPartRequests[paths] = requestCount;
            var reuseThreshold = PreferConvexRegionParts
                ? 1
                : Reference3DConvexTriangulationReuseThreshold;
            if (requestCount < reuseThreshold)
            {
                return false;
            }
            if (paths.Count != 1)
            {
                (_convexRegionParts ??= []).Add(paths, null);
                return false;
            }
            PathsD triangles;
            try
            {
                if (Clipper.Triangulate(
                        paths,
                        Reference3DClipperPrecision,
                        out triangles,
                        false) != TriangulateResult.success
                    || triangles.Count == 0
                    || triangles.Count > Reference3DMaximumConvexOverlapVertices)
                {
                    (_convexRegionParts ??= []).Add(paths, null);
                    return false;
                }
            }
            catch (Exception exception) when (Reference3DIsClipperFailure(exception))
            {
                (_convexRegionParts ??= []).Add(paths, null);
                return false;
            }

            var parts = new Reference3DConvexRegion[triangles.Count];
            for (var index = 0; index < triangles.Count; index++)
            {
                if (!TryAnalyzeReference3DConvexPath(triangles[index], out parts[index]))
                {
                    (_convexRegionParts ??= []).Add(paths, null);
                    return false;
                }
            }
            regions = parts;
            (_convexRegionParts ??= []).Add(paths, regions);
            return true;
        }
    }

    private readonly record struct Reference3DRawFragmentPlan(
        PathsD ClipPaths,
        PathsD VisibleFillPaths,
        ulong StableSignature,
        Reference3DProjectedContour[]? ConvexContours = null);

    private readonly record struct Reference3DFragmentPlan(
        Reference3DProjectedContour[] Clip,
        float Depth,
        int Slot,
        ulong StableSignature);

    private readonly record struct Reference3DFragmentBuildResult(
        bool Succeeded,
        Reference3DFragmentPlan[] Plans)
    {
        public static Reference3DFragmentBuildResult Failed { get; } = new(false, []);
    }

    private readonly record struct Reference3DGroupSurfaceRegion(
        Reference3DRenderItem Item,
        PathsD Paths,
        RectangleF Bounds);

    private readonly record struct Reference3DConvexRegion(
        PathD Path,
        int PointCount,
        double Orientation,
        RectangleF Bounds);

    private Reference3DRenderItem[] BuildReference3DIntersectionRenderItems(
        IReadOnlyList<Reference3DRenderItem> source,
        Reference3DIntersectionPathCache pathCache)
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
            if (!TryGetReference3DOcclusionContours(item, out var surfaceContours)
                || !item.Plane.IsValid
                || !TryGetReference3DProjectedBounds(surfaceContours, 0, out var bounds)
                || bounds.Width <= 0
                || bounds.Height <= 0)
            {
                continue;
            }

            surfaces.Add(new Reference3DIntersectionSurface(item, surfaceContours, bounds));
        }
        if (surfaces.Count < 2) return source.ToArray();

        surfaces.Sort((left, right) => CompareReference3DRenderItemStableIdentity(
            left.FillItem,
            right.FillItem));

        // Keep the projected-bounds sweep camera-local, but process its hits by stable surface identity.
        var sweepOrder = Enumerable.Range(0, surfaces.Count)
            .OrderBy(index => surfaces[index].Bounds.Left)
            .ThenBy(index => index)
            .ToArray();
        var candidateRightsByLeft = Enumerable.Range(0, surfaces.Count)
            .Select(_ => new List<int>())
            .ToArray();
        var candidatePairCounts = new int[surfaces.Count];
        var containsExtrusionSurfaces = surfaces.Any(surface =>
            surface.FillItem.Kind is Reference3DRenderKind.Back or Reference3DRenderKind.Side);
        for (var sweepIndex = 0; sweepIndex < sweepOrder.Length; sweepIndex++)
        {
            var firstIndex = sweepOrder[sweepIndex];
            var first = surfaces[firstIndex];
            for (var candidateIndex = sweepIndex + 1;
                 candidateIndex < sweepOrder.Length;
                 candidateIndex++)
            {
                var secondIndex = sweepOrder[candidateIndex];
                var second = surfaces[secondIndex];
                if (second.Bounds.Left
                    > first.Bounds.Right + ReferenceSurfaceOverlapTolerancePixels)
                {
                    break;
                }
                if (Reference3DRenderItemsSharePhysicalShell(
                        first.FillItem,
                        second.FillItem)
                    || first.FillItem.PlaneKey == second.FillItem.PlaneKey
                    || !Reference3DProjectedBoundsOverlap(first.Bounds, second.Bounds))
                {
                    continue;
                }
                var stableLeft = Math.Min(firstIndex, secondIndex);
                var stableRight = Math.Max(firstIndex, secondIndex);
                candidateRightsByLeft[stableLeft].Add(stableRight);
                candidatePairCounts[stableLeft]++;
                candidatePairCounts[stableRight]++;
            }
        }

        IEnumerable<(int Left, int Right)> EnumerateStableCandidatePairs()
        {
            for (var left = 0; left < candidateRightsByLeft.Length; left++)
            {
                var rights = candidateRightsByLeft[left];
                rights.Sort();
                foreach (var right in rights) yield return (left, right);
            }
        }

        var edgePlans = new List<Reference3DIntersectionEdgePlan>();
        foreach (var (leftIndex, rightIndex) in EnumerateStableCandidatePairs())
        {
            var left = surfaces[leftIndex];
            var right = surfaces[rightIndex];
            var producesIntersectionEdge = Reference3DRenderItemProducesIntersectionEdge(left.FillItem)
                && Reference3DRenderItemProducesIntersectionEdge(right.FillItem);
            var line = default(Reference3DScreenLine);
            Reference3DProjectedContour[] segments = [];
            var hasIntersectionSegments = false;
            var convexHandled = false;
            var hasProjectedIntersectionLine = TryGetReference3DProjectedIntersectionLine(
                left.FillItem.Plane,
                right.FillItem.Plane,
                out line);
            if (hasProjectedIntersectionLine)
            {
                var convexOverlap = TryGetReference3DConvexIntersectionInterval(
                    left,
                    right,
                    line,
                    out convexHandled,
                    out var intervalStart,
                    out var intervalEnd);
                if (convexHandled)
                {
                    hasIntersectionSegments = convexOverlap;
                    if (hasIntersectionSegments && producesIntersectionEdge)
                    {
                        var start = line.PointAt(intervalStart);
                        var end = line.PointAt(intervalEnd);
                        var midpoint = line.PointAt((intervalStart + intervalEnd) * 0.5d);
                        if (TryGetReference3DPlaneDepthAtScreen(
                                left.FillItem.Plane,
                                midpoint,
                                out var depth))
                        {
                            segments = [new Reference3DProjectedContour([start, end], false, depth)];
                        }
                        else
                        {
                            hasIntersectionSegments = false;
                        }
                    }
                }
                else
                {
                    hasIntersectionSegments = TryGetReference3DIntersectionSegments(
                            line,
                            left.Contours,
                            right.Contours,
                            left.FillItem.Plane,
                            out segments)
                        && segments.Length > 0;
                }
            }
            var regionsOverlap = hasIntersectionSegments;
            var regionsOverlapInitialized = hasIntersectionSegments;
            var needsDisconnectedDepthCut = false;
            // Disconnected overlap islands can reverse depth even when the equality line crosses only empty space.
            if (!hasIntersectionSegments
                && hasProjectedIntersectionLine
                && !convexHandled)
            {
                var leftRegion = GetReference3DIntersectionSurfaceRegion(left);
                var rightRegion = GetReference3DIntersectionSurfaceRegion(right);
                regionsOverlapInitialized = TryClassifyReference3DRegionOverlapAcrossLine(
                    leftRegion,
                    rightRegion,
                    line,
                    out regionsOverlap,
                    out needsDisconnectedDepthCut);
            }
            var needsProjectedOverlap = producesIntersectionEdge
                || left.FillItem.Kind == Reference3DRenderKind.FrontStroke
                || right.FillItem.Kind == Reference3DRenderKind.FrontStroke
                || needsDisconnectedDepthCut;
            if (!hasIntersectionSegments && !needsProjectedOverlap) continue;
            if (!regionsOverlap && !regionsOverlapInitialized)
            {
                var leftRegion = GetReference3DIntersectionSurfaceRegion(left);
                var rightRegion = GetReference3DIntersectionSurfaceRegion(right);
                regionsOverlap = leftRegion.Count > 0
                    && rightRegion.Count > 0
                    && Reference3DRegionsOverlap(leftRegion, rightRegion);
            }
            if (!regionsOverlap && !producesIntersectionEdge) continue;
            var needsDepthCut = hasIntersectionSegments || needsDisconnectedDepthCut;
            var splitLeft = regionsOverlap
                && (producesIntersectionEdge
                    || left.FillItem.Kind == Reference3DRenderKind.FrontStroke
                    || needsDepthCut
                    && left.FillItem.Kind != Reference3DRenderKind.FrontFill);
            var splitRight = regionsOverlap
                && (producesIntersectionEdge
                    || right.FillItem.Kind == Reference3DRenderKind.FrontStroke
                    || needsDepthCut
                    && right.FillItem.Kind != Reference3DRenderKind.FrontFill);
            if (needsDepthCut
                && splitLeft
                && splitRight
                && (!producesIntersectionEdge || !hasIntersectionSegments))
            {
                var leftIsExtrusion = left.FillItem.Kind is Reference3DRenderKind.Back
                    or Reference3DRenderKind.Side;
                var rightIsExtrusion = right.FillItem.Kind is Reference3DRenderKind.Back
                    or Reference3DRenderKind.Side;
                var preferLeft = leftIsExtrusion && !rightIsExtrusion
                    || leftIsExtrusion
                    && rightIsExtrusion
                    && (left.FillItem.Kind == Reference3DRenderKind.Side
                        || right.FillItem.Kind != Reference3DRenderKind.Side)
                    || !leftIsExtrusion
                    && !rightIsExtrusion
                    && CompareReference3DRenderItemStableIdentity(
                        left.FillItem,
                        right.FillItem) <= 0;
                if (preferLeft
                    && !CanAddReference3DIntersectionCut(left, line)
                    && CanAddReference3DIntersectionCut(right, line))
                {
                    preferLeft = false;
                }
                else if (!preferLeft
                         && !CanAddReference3DIntersectionCut(right, line)
                         && CanAddReference3DIntersectionCut(left, line))
                {
                    preferLeft = true;
                }
                splitLeft = preferLeft;
                splitRight = !preferLeft;
            }
            var canUseIntersectionCut = needsDepthCut
                && (!splitLeft || CanAddReference3DIntersectionCut(left, line))
                && (!splitRight || CanAddReference3DIntersectionCut(right, line));
            var splitOnlyExtrusionSurfaces = (!splitLeft
                    || left.FillItem.Kind is Reference3DRenderKind.Back
                        or Reference3DRenderKind.Side)
                && (!splitRight
                    || right.FillItem.Kind is Reference3DRenderKind.Back
                        or Reference3DRenderKind.Side);
            var usePartitionFreeIntersectionCut = canUseIntersectionCut
                && (containsExtrusionSurfaces
                    || splitOnlyExtrusionSurfaces
                    || producesIntersectionEdge
                    && candidatePairCounts[leftIndex] == 1
                    && candidatePairCounts[rightIndex] == 1
                    && left.Cuts.Count == 0
                    && right.Cuts.Count == 0
                    && left.PartitionRegions.Count == 0
                    && right.PartitionRegions.Count == 0);
            if (canUseIntersectionCut)
            {
                if (splitLeft) AddReference3DIntersectionCut(left, line, right.FillItem);
                if (splitRight) AddReference3DIntersectionCut(right, line, left.FillItem);
            }
            if (!usePartitionFreeIntersectionCut)
            {
                if (splitLeft)
                {
                    left.PartitionRegions.Add(new Reference3DPathRegion(
                        GetReference3DIntersectionSurfaceRegion(right),
                        right.Bounds,
                        right.Contours,
                        right.FillItem.ObjectIndex,
                        Reference3DNormalizedSurfaceKind(right.FillItem.Kind),
                        right.FillItem.SurfaceSlot));
                }
                if (splitRight)
                {
                    right.PartitionRegions.Add(new Reference3DPathRegion(
                        GetReference3DIntersectionSurfaceRegion(left),
                        left.Bounds,
                        left.Contours,
                        left.FillItem.ObjectIndex,
                        Reference3DNormalizedSurfaceKind(left.FillItem.Kind),
                        left.FillItem.SurfaceSlot));
                }
            }

            if (!hasIntersectionSegments) continue;
            if (!producesIntersectionEdge) continue;
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
                Reference3DIntersectionEdgeStableSlot(primary, secondary)));
        }
        edgePlans.Sort(static (left, right) =>
        {
            var comparison = Math.Min(left.Primary.ObjectIndex, left.Secondary.ObjectIndex)
                .CompareTo(Math.Min(right.Primary.ObjectIndex, right.Secondary.ObjectIndex));
            if (comparison != 0) return comparison;
            comparison = Math.Max(left.Primary.ObjectIndex, left.Secondary.ObjectIndex)
                .CompareTo(Math.Max(right.Primary.ObjectIndex, right.Secondary.ObjectIndex));
            if (comparison != 0) return comparison;
            comparison = left.Primary.SurfaceSlot.CompareTo(right.Primary.SurfaceSlot);
            return comparison != 0
                ? comparison
                : left.Secondary.SurfaceSlot.CompareTo(right.Secondary.SurfaceSlot);
        });
        if (edgePlans.Count == 0
            && surfaces.All(surface =>
                surface.Cuts.Count == 0 && surface.PartitionRegions.Count == 0))
        {
            return source.ToArray();
        }

        var fragmentPlans = new Dictionary<
            (int ObjectIndex, int SurfaceKind, int SurfaceSlot),
            Reference3DFragmentPlan[]>();
        foreach (var surface in surfaces)
        {
            if (surface.Cuts.Count == 0 && surface.PartitionRegions.Count == 0) continue;
            var key = (
                surface.FillItem.ObjectIndex,
                Reference3DNormalizedSurfaceKind(surface.FillItem.Kind),
                surface.FillItem.SurfaceSlot);
            if (GetReference3DIntersectionSurfaceRegion(surface).Count == 0) continue;
            var build = BuildReference3DSurfaceFragments(surface, pathCache);
            if (build.Succeeded
                && (build.Plans.Length > 1
                    || surface.FillItem.Kind != Reference3DRenderKind.FrontFill))
            {
                fragmentPlans[key] = build.Plans;
            }
        }

        var result = new List<Reference3DRenderItem>(source.Count + fragmentPlans.Count * 2);
        foreach (var item in source)
        {
            var surfaceKey = (
                item.ObjectIndex,
                Reference3DNormalizedSurfaceKind(item.Kind),
                item.SurfaceSlot);
            if (!fragmentPlans.TryGetValue(surfaceKey, out var fragments))
            {
                result.Add(item);
                continue;
            }

            var occlusionContours = item.Kind == Reference3DRenderKind.FrontStroke
                && item.OcclusionContours is null
                    ? GetReference3DProjectedStrokeOcclusionContours(
                        item.ObjectIndex,
                        item.Contours)
                    : item.OcclusionContours;
            foreach (var fragment in fragments)
            {
                result.Add(item with
                {
                    FragmentClip = fragment.Clip,
                    FragmentSlot = fragment.Slot,
                    StableFragmentIdentity = fragment.StableSignature,
                    AverageDepth = fragment.Depth,
                    OcclusionContours = occlusionContours
                });
            }
        }

        foreach (var edgePlan in edgePlans)
        {
            var edgeColor = GetReference3DIntersectionEdgeColor(edgePlan.Primary);
            var edgeWidth = GetReference3DIntersectionEdgeWidth(edgePlan.Primary, edgePlan.Secondary);
            var segments = SplitReference3DIntersectionEdgeSegments(edgePlan, surfaces);
            for (var segmentIndex = 0; segmentIndex < segments.Length; segmentIndex++)
            {
                var segment = segments[segmentIndex];
                var opticalSurfaceContours = CreateReference3DOpenStrokeOcclusionContours(
                    segment.Contour,
                    edgeWidth,
                    segment.StartCap,
                    segment.EndCap,
                    miterJoin: false);
                result.Add(new Reference3DRenderItem(
                    edgePlan.Primary.ObjectIndex,
                    edgePlan.Primary.LayerIndex,
                    Reference3DRenderKind.IntersectionEdge,
                    [segment.Contour],
                    segment.Contour.AverageDepth,
                    default,
                    edgePlan.Primary.ObjectSlot,
                    edgePlan.StableSlot)
                {
                    Plane = edgePlan.Primary.Plane,
                    SurfacePoint = edgePlan.Primary.SurfacePoint,
                    HasSurfacePoint = edgePlan.Primary.HasSurfacePoint,
                    SecondaryObjectIndex = edgePlan.Secondary.ObjectIndex,
                    EdgeArgb = edgeColor.ToArgb(),
                    EdgeWidth = edgeWidth,
                    EdgeStartCap = segment.StartCap,
                    EdgeEndCap = segment.EndCap,
                    OpticalSurfaceContours = opticalSurfaceContours.Length > 0
                        ? opticalSurfaceContours
                        : null,
                    FragmentSlot = segmentIndex,
                    MaterialOpacity = Math.Min(
                        edgePlan.Primary.MaterialOpacity,
                        edgePlan.Secondary.MaterialOpacity)
                });
            }
        }

        return result.ToArray();

        PathsD GetReference3DIntersectionSurfaceRegion(Reference3DIntersectionSurface surface)
        {
            if (surface.RegionInitialized) return surface.Region;
            surface.Region = pathCache.GetOrAddPaths(surface.Contours);
            surface.RegionInitialized = true;
            return surface.Region;
        }
    }

    private static int Reference3DIntersectionEdgeStableSlot(
        Reference3DRenderItem primary,
        Reference3DRenderItem secondary)
    {
        var first = primary.ObjectIndex <= secondary.ObjectIndex ? primary : secondary;
        var second = primary.ObjectIndex <= secondary.ObjectIndex ? secondary : primary;
        const uint offset = 2166136261;
        const uint prime = 16777619;
        var hash = offset;
        Add(first.ObjectIndex);
        Add(first.SurfaceSlot);
        Add(second.ObjectIndex);
        Add(second.SurfaceSlot);
        return (int)(hash & int.MaxValue);

        void Add(int value)
        {
            unchecked
            {
                hash ^= (uint)value;
                hash *= prime;
            }
        }
    }

    private static bool TryGetReference3DOcclusionContours(
        Reference3DRenderItem item,
        out Reference3DProjectedContour[] contours)
    {
        contours = item.Kind is Reference3DRenderKind.Back
                or Reference3DRenderKind.Side
                or Reference3DRenderKind.FrontFill
            ? item.Contours
            : item.OcclusionContours ?? [];
        return contours.Any(contour => contour.Closed && contour.Points.Length >= 3);
    }

    private static bool Reference3DRenderItemProducesIntersectionEdge(
        Reference3DRenderItem item)
    {
        return item.Kind == Reference3DRenderKind.FrontFill;
    }

    private static bool TryGetReference3DConvexIntersectionInterval(
        Reference3DIntersectionSurface left,
        Reference3DIntersectionSurface right,
        Reference3DScreenLine line,
        out bool handled,
        out double start,
        out double end)
    {
        handled = false;
        start = 0;
        end = 0;
        if (!TryGetReference3DConvexContour(left, out var leftPoints, out var leftCount)
            || !TryGetReference3DConvexContour(right, out var rightPoints, out var rightCount))
        {
            return false;
        }

        handled = true;
        if (!TryGetReference3DConvexLineInterval(
                line,
                leftPoints,
                leftCount,
                out var leftStart,
                out var leftEnd)
            || !TryGetReference3DConvexLineInterval(
                line,
                rightPoints,
                rightCount,
                out var rightStart,
                out var rightEnd))
        {
            return false;
        }

        start = Math.Max(leftStart, rightStart);
        end = Math.Min(leftEnd, rightEnd);
        return end - start > Reference3DIntersectionSegmentEpsilon;
    }

    private static bool TryGetReference3DConvexContour(
        Reference3DIntersectionSurface surface,
        out PointF[] points,
        out int count)
    {
        if (surface.ConvexContourInitialized)
        {
            points = surface.ConvexContourPoints ?? [];
            count = surface.ConvexContourPointCount;
            return count >= 3;
        }

        surface.ConvexContourInitialized = true;
        points = [];
        count = 0;
        if (surface.Contours is not [{ Closed: true, Points: var candidate }]
            || candidate.Length < 3)
        {
            return false;
        }

        count = candidate.Length;
        while (count > 1
               && ReferencePointDistance(candidate[0], candidate[count - 1]) <= 0.000001f)
        {
            count--;
        }
        if (count < 3) return false;

        double twiceArea = 0;
        var previous = candidate[count - 1];
        for (var index = 0; index < count; index++)
        {
            var current = candidate[index];
            if (!float.IsFinite(current.X) || !float.IsFinite(current.Y)) return false;
            twiceArea += (double)previous.X * current.Y - (double)current.X * previous.Y;
            previous = current;
        }
        if (Math.Abs(twiceArea) * 0.5d <= Reference3DIntersectionAreaEpsilon)
        {
            count = 0;
            return false;
        }
        var orientation = Math.Sign(twiceArea);
        for (var edgeIndex = 0; edgeIndex < count; edgeIndex++)
        {
            var start = candidate[edgeIndex];
            var end = candidate[(edgeIndex + 1) % count];
            var edgeX = (double)end.X - start.X;
            var edgeY = (double)end.Y - start.Y;
            var edgeLength = Math.Sqrt(edgeX * edgeX + edgeY * edgeY);
            if (!double.IsFinite(edgeLength) || edgeLength <= Reference3DIntersectionLineEpsilon)
            {
                count = 0;
                return false;
            }
            var sideEpsilon = Reference3DConvexTolerancePixels * Math.Max(1d, edgeLength);
            for (var pointIndex = 0; pointIndex < count; pointIndex++)
            {
                if (pointIndex == edgeIndex || pointIndex == (edgeIndex + 1) % count) continue;
                var point = candidate[pointIndex];
                var cross = edgeX * (point.Y - start.Y) - edgeY * (point.X - start.X);
                if (orientation * cross >= -sideEpsilon) continue;
                count = 0;
                return false;
            }
        }

        surface.ConvexContourPoints = candidate;
        surface.ConvexContourPointCount = count;
        points = candidate;
        return true;
    }

    private static bool TryGetReference3DConvexLineInterval(
        Reference3DScreenLine line,
        IReadOnlyList<PointF> points,
        int count,
        out double start,
        out double end)
    {
        var intervalStart = double.PositiveInfinity;
        var intervalEnd = double.NegativeInfinity;
        start = 0;
        end = 0;
        if (count < 3 || count > points.Count) return false;

        var origin = line.Origin;
        var direction = line.Direction;
        var previous = points[count - 1];
        var previousDistance = line.SignedDistance(previous);
        for (var index = 0; index < count; index++)
        {
            var current = points[index];
            var currentDistance = line.SignedDistance(current);
            if (!double.IsFinite(previousDistance) || !double.IsFinite(currentDistance))
            {
                return false;
            }

            if (Math.Abs(previousDistance) <= Reference3DIntersectionLineEpsilon)
            {
                Include(Reference3DLineParameter(previous, origin, direction));
            }
            if (Math.Abs(currentDistance) <= Reference3DIntersectionLineEpsilon)
            {
                Include(Reference3DLineParameter(current, origin, direction));
            }
            if ((previousDistance < 0 && currentDistance > 0)
                || (previousDistance > 0 && currentDistance < 0))
            {
                var denominator = previousDistance - currentDistance;
                var amount = Math.Abs(denominator) <= Reference3DIntersectionLineEpsilon
                    ? 0d
                    : Math.Clamp(previousDistance / denominator, 0d, 1d);
                var intersection = new PointF(
                    (float)(previous.X + (current.X - previous.X) * amount),
                    (float)(previous.Y + (current.Y - previous.Y) * amount));
                Include(Reference3DLineParameter(intersection, origin, direction));
            }

            previous = current;
            previousDistance = currentDistance;
        }
        if (!double.IsFinite(intervalStart)
            || !double.IsFinite(intervalEnd)
            || intervalEnd < intervalStart)
        {
            return false;
        }
        start = intervalStart;
        end = intervalEnd;
        return true;

        void Include(double value)
        {
            if (!double.IsFinite(value)) return;
            intervalStart = Math.Min(intervalStart, value);
            intervalEnd = Math.Max(intervalEnd, value);
        }
    }

    private static bool CanAddReference3DIntersectionCut(
        Reference3DIntersectionSurface surface,
        Reference3DScreenLine candidate)
    {
        if (surface.Cuts.Any(cut => Reference3DScreenLinesEquivalent(cut.Line, candidate)))
        {
            return true;
        }

        var nextCutCount = surface.Cuts.Count + 1;
        var maximumCellCount = nextCutCount * (nextCutCount + 1) / 2 + 1;
        return nextCutCount <= Reference3DMaximumIntersectionCutsPerSurface
            && maximumCellCount <= Reference3DMaximumFragmentsPerSurface;
    }

    private static void AddReference3DIntersectionCut(
        Reference3DIntersectionSurface surface,
        Reference3DScreenLine candidate,
        Reference3DRenderItem other)
    {
        if (!surface.Cuts.Any(cut => Reference3DScreenLinesEquivalent(cut.Line, candidate)))
        {
            surface.Cuts.Add(new Reference3DIntersectionCut(
                candidate,
                other.Plane,
                other.ObjectIndex,
                Reference3DNormalizedSurfaceKind(other.Kind),
                other.SurfaceSlot));
        }
    }

    private static bool Reference3DScreenLinesEquivalent(
        Reference3DScreenLine left,
        Reference3DScreenLine right)
    {
        var sameDirection = Math.Abs(left.A - right.A) <= 0.0001d
            && Math.Abs(left.B - right.B) <= 0.0001d
            && Math.Abs(left.C - right.C) <= 0.25d;
        var oppositeDirection = Math.Abs(left.A + right.A) <= 0.0001d
            && Math.Abs(left.B + right.B) <= 0.0001d
            && Math.Abs(left.C + right.C) <= 0.25d;
        return sameDirection || oppositeDirection;
    }

    private Reference3DFragmentBuildResult BuildReference3DSurfaceFragments(
        Reference3DIntersectionSurface surface,
        Reference3DIntersectionPathCache pathCache)
    {
        PointF[] convexPoints = [];
        var convexPointCount = 0;
        var usesConvexSurfaceCells = surface.PartitionRegions.Count == 0
            && TryGetReference3DConvexContour(
                surface,
                out convexPoints,
                out convexPointCount);
        List<PointF[]> cells;
        if (usesConvexSurfaceCells)
        {
            var initialCell = new PointF[convexPointCount];
            Array.Copy(convexPoints, initialCell, convexPointCount);
            cells = [initialCell];
        }
        else
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
            cells =
            [
                [
                    new PointF(bounds.Left, bounds.Top),
                    new PointF(bounds.Right, bounds.Top),
                    new PointF(bounds.Right, bounds.Bottom),
                    new PointF(bounds.Left, bounds.Bottom)
                ]
            ];
        }

        foreach (var cut in surface.Cuts)
        {
            var line = cut.Line;
            var next = new List<PointF[]>(Math.Min(cells.Count * 2, Reference3DMaximumFragmentsPerSurface));
            foreach (var cell in cells)
            {
                var hasPositiveDistance = false;
                var hasNegativeDistance = false;
                foreach (var point in cell)
                {
                    var distance = line.SignedDistance(point);
                    hasPositiveDistance |= distance > Reference3DIntersectionLineEpsilon;
                    hasNegativeDistance |= distance < -Reference3DIntersectionLineEpsilon;
                    if (hasPositiveDistance && hasNegativeDistance) break;
                }
                if (!hasPositiveDistance || !hasNegativeDistance)
                {
                    next.Add(cell);
                    continue;
                }

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

        List<Reference3DRawFragmentPlan> fragments;
        if (usesConvexSurfaceCells)
        {
            fragments = new List<Reference3DRawFragmentPlan>(cells.Count);
            foreach (var cell in cells)
            {
                var area = Reference3DPolygonArea(cell);
                if (!double.IsFinite(area)
                    || area <= Reference3DIntersectionAreaEpsilon
                    || !TryFindReference3DConvexComponentPoint(cell, out var sample))
                {
                    continue;
                }

                if (!TryGetReference3DFragmentStableSignature(
                        surface,
                        sample,
                        out var stableSignature))
                {
                    return Reference3DFragmentBuildResult.Failed;
                }
                fragments.Add(new Reference3DRawFragmentPlan(
                    [],
                    [],
                    stableSignature,
                    [new Reference3DProjectedContour(
                        cell,
                        true,
                        0)]));
            }
        }
        else
        {
            var regions = new List<Reference3DPathRegion>(cells.Count);
            foreach (var cell in cells)
            {
                var paths = Reference3DContoursToPaths(
                    [new Reference3DProjectedContour(cell, true, surface.FillItem.AverageDepth)]);
                if (Reference3DPathsHaveArea(paths)
                    && TryGetReference3DPathsBounds(paths, out var pathBounds))
                {
                    regions.Add(new Reference3DPathRegion(paths, pathBounds));
                }
            }
            foreach (var partition in surface.PartitionRegions)
            {
                var next = new List<Reference3DPathRegion>(Math.Min(
                    regions.Count * 2,
                    Reference3DMaximumFragmentsPerSurface));
                foreach (var region in regions)
                {
                    if (!Reference3DProjectedBoundsOverlap(region.Bounds, partition.Bounds))
                    {
                        next.Add(region);
                        continue;
                    }

                    PathsD inside;
                    PathsD outside;
                    try
                    {
                        inside = Clipper.Intersect(
                            region.Paths,
                            partition.Paths,
                            FillRule.EvenOdd,
                            Reference3DClipperPrecision);
                        if (!Reference3DPathsHaveArea(inside))
                        {
                            next.Add(region);
                            continue;
                        }
                        outside = Clipper.Difference(
                            region.Paths,
                            partition.Paths,
                            FillRule.EvenOdd,
                            Reference3DClipperPrecision);
                    }
                    catch (Exception exception) when (Reference3DIsClipperFailure(exception))
                    {
                        return Reference3DFragmentBuildResult.Failed;
                    }

                    var hasOutside = Reference3DPathsHaveArea(outside);
                    if (hasOutside
                        && TryGetReference3DPathsBounds(inside, out var insideBounds)
                        && TryGetReference3DPathsBounds(outside, out var outsideBounds))
                    {
                        next.Add(new Reference3DPathRegion(inside, insideBounds));
                        next.Add(new Reference3DPathRegion(outside, outsideBounds));
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

            fragments = new List<Reference3DRawFragmentPlan>(regions.Count);
            foreach (var region in regions)
            {
                if (!TryFindReference3DRegionPoint(
                         surface.Region,
                         region.Paths,
                         out var sample,
                         out var visibleFillPaths))
                {
                    continue;
                }

                if (!TryGetReference3DFragmentStableSignature(surface, sample, out var stableSignature))
                {
                    return Reference3DFragmentBuildResult.Failed;
                }
                fragments.Add(new Reference3DRawFragmentPlan(
                    region.Paths,
                    visibleFillPaths,
                    stableSignature));
            }
        }
        if (fragments.Count == 0) return Reference3DFragmentBuildResult.Failed;

        var stableFragments = new List<Reference3DFragmentPlan>();
        foreach (var group in fragments
                     .GroupBy(fragment => fragment.StableSignature)
                     .OrderBy(group => group.Key))
        {
            PathsD clipPaths;
            PathsD visibleFillPaths;
            Reference3DProjectedContour[] clip;
            if (usesConvexSurfaceCells)
            {
                clip = group
                    .SelectMany(fragment => fragment.ConvexContours
                        ?? Array.Empty<Reference3DProjectedContour>())
                    .ToArray();
                clipPaths = Reference3DContoursToPaths(clip);
                visibleFillPaths = clipPaths;
                pathCache.AddKnownConvexRegion(clipPaths);
            }
            else
            {
                clipPaths = new PathsD(group.SelectMany(fragment => fragment.ClipPaths));
                visibleFillPaths = new PathsD(group.SelectMany(fragment => fragment.VisibleFillPaths));
                clip = Reference3DPathsToContours(clipPaths);
            }
            var hasSample = usesConvexSurfaceCells
                ? TryFindReference3DConvexComponentPoint(visibleFillPaths, out var sample)
                : TryFindReference3DPointInPaths(visibleFillPaths, out sample);
            if (!hasSample)
            {
                return Reference3DFragmentBuildResult.Failed;
            }
            if (clip.Length == 0) return Reference3DFragmentBuildResult.Failed;
            pathCache.AddPaths(clip, clipPaths);
            pathCache.AddVisibleFillPaths(clip, visibleFillPaths);
            var depth = TryGetReference3DPlaneDepthAtScreen(
                    surface.FillItem.Plane,
                    sample,
                    out var sampledDepth)
                ? sampledDepth
                : surface.FillItem.AverageDepth;
            stableFragments.Add(new Reference3DFragmentPlan(
                clip,
                depth,
                (int)(group.Key & int.MaxValue),
                group.Key));
        }
        return new Reference3DFragmentBuildResult(true, stableFragments.ToArray());
    }

    private bool TryGetReference3DFragmentStableSignature(
        Reference3DIntersectionSurface surface,
        PointF screen,
        out ulong signature)
    {
        signature = 0;
        var hash = 14695981039346656037UL;
        if (!surface.FillItem.Plane.IsValid
            || !TryGetReference3DRay(screen, out var ray))
        {
            return false;
        }
        var denominator = Vector3.Dot(surface.FillItem.Plane.Normal, ray.Direction);
        if (!float.IsFinite(denominator) || Math.Abs(denominator) <= 0.000001f)
        {
            return false;
        }
        var distance = (surface.FillItem.Plane.Distance
                - Vector3.Dot(surface.FillItem.Plane.Normal, ray.Origin))
            / denominator;
        if (!float.IsFinite(distance) || distance <= 0) return false;
        var scenePoint = ray.Origin + ray.Direction * distance;
        if (!Finite(scenePoint)) return false;

        foreach (var cut in surface.Cuts)
        {
            Add(1);
            Add(cut.StableObjectIndex);
            Add(cut.StableSurfaceKind);
            Add(cut.StableSurfaceSlot);
            var side = Vector3.Dot(cut.OtherPlane.Normal, scenePoint) - cut.OtherPlane.Distance;
            Add(side > 0.0001f ? 2 : side < -0.0001f ? 0 : 1);
        }
        foreach (var partition in surface.PartitionRegions)
        {
            Add(2);
            Add(partition.StableObjectIndex);
            Add(partition.StableSurfaceKind);
            Add(partition.StableSurfaceSlot);
            Add(partition.StableSourceContours is { Length: > 0 }
                && PointInProjectedFill(screen, partition.StableSourceContours)
                    ? 1
                    : 0);
        }
        signature = hash;
        return true;

        void Add(int value)
        {
            unchecked
            {
                hash ^= (uint)value;
                hash *= 1099511628211UL;
            }
        }
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

    private static bool TryFindReference3DConvexComponentPoint(
        PathsD paths,
        out PointF point)
    {
        point = PointF.Empty;
        foreach (var path in paths)
        {
            if (path.Count < 3) continue;
            double x = 0;
            double y = 0;
            foreach (var value in path)
            {
                x += value.x;
                y += value.y;
            }
            point = new PointF((float)(x / path.Count), (float)(y / path.Count));
            return float.IsFinite(point.X) && float.IsFinite(point.Y);
        }
        return false;
    }

    private static bool TryFindReference3DConvexComponentPoint(
        IReadOnlyList<PointF> points,
        out PointF point)
    {
        point = PointF.Empty;
        if (points.Count < 3) return false;

        double x = 0;
        double y = 0;
        foreach (var value in points)
        {
            if (!float.IsFinite(value.X) || !float.IsFinite(value.Y)) return false;
            x += value.X;
            y += value.Y;
        }

        point = new PointF((float)(x / points.Count), (float)(y / points.Count));
        return float.IsFinite(point.X) && float.IsFinite(point.Y);
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

    private Reference3DIntersectionEdgeSegment[] SplitReference3DIntersectionEdgeSegments(
        Reference3DIntersectionEdgePlan edgePlan,
        IReadOnlyList<Reference3DIntersectionSurface> surfaces)
    {
        var result = new List<Reference3DIntersectionEdgeSegment>();
        foreach (var segment in edgePlan.Segments)
        {
            if (segment.Points.Length < 2) continue;
            var start = segment.Points[0];
            var end = segment.Points[^1];
            var segmentBounds = RectangleF.FromLTRB(
                Math.Min(start.X, end.X),
                Math.Min(start.Y, end.Y),
                Math.Max(start.X, end.X),
                Math.Max(start.Y, end.Y));
            var parameters = new List<double> { 0d, 1d };
            foreach (var surface in surfaces)
            {
                var surfaceItem = surface.FillItem;
                if (surfaceItem.ObjectIndex == edgePlan.Primary.ObjectIndex
                    || surfaceItem.ObjectIndex == edgePlan.Secondary.ObjectIndex
                    || !Reference3DProjectedBoundsOverlap(segmentBounds, surface.Bounds))
                {
                    continue;
                }

                AppendReference3DSegmentBoundaryParameters(
                    start,
                    end,
                    surface.Contours,
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
                result.Add(new Reference3DIntersectionEdgeSegment(segment, true, true));
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

            var firstPieceIndex = result.Count;
            for (var index = 1; index < unique.Count; index++)
            {
                var from = unique[index - 1];
                var to = unique[index];
                var pieceStart = Reference3DLerp(start, end, from);
                var pieceEnd = Reference3DLerp(start, end, to);
                if (ReferencePointDistance(pieceStart, pieceEnd) <= Reference3DIntersectionPieceEpsilon)
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
                result.Add(new Reference3DIntersectionEdgeSegment(
                    new Reference3DProjectedContour([pieceStart, pieceEnd], false, depth),
                    from <= 0.000001d,
                    to >= 0.999999d));
            }
            if (result.Count > firstPieceIndex)
            {
                result[firstPieceIndex] = result[firstPieceIndex] with { StartCap = true };
                result[^1] = result[^1] with { EndCap = true };
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
        return TryGetReference3DRay(screen, out var ray)
            && TryGetReference3DPlaneDepthAlongRay(left, ray, out var leftDepth)
            && TryGetReference3DPlaneDepthAlongRay(right, ray, out var rightDepth)
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
        return TryGetReference3DRay(screen, out var ray)
            && TryGetReference3DPlaneDepthAlongRay(plane, ray, out depth);
    }

    private bool TryGetReference3DPlaneDepthAlongRay(
        Reference3DSurfacePlane plane,
        SpatialRay ray,
        out float depth)
    {
        depth = 0;
        if (!plane.IsValid) return false;
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
        Reference3DRenderItem right,
        Reference3DIntersectionPathCache pathCache)
    {
        if (Reference3DAreDistinctFragmentsOfSameSurface(left, right))
        {
            return false;
        }
        if (left.FragmentClip is not { Length: > 0 } leftClip
            || right.FragmentClip is not { Length: > 0 } rightClip)
        {
            return true;
        }

        return Reference3DRegionsOverlap(
            pathCache.GetOrAddPaths(leftClip),
            pathCache.GetOrAddPaths(rightClip));
    }

    private static bool Reference3DAreDistinctFragmentsOfSameSurface(
        Reference3DRenderItem left,
        Reference3DRenderItem right)
    {
        return left.ObjectIndex == right.ObjectIndex
            && Reference3DNormalizedSurfaceKind(left.Kind)
                == Reference3DNormalizedSurfaceKind(right.Kind)
            && left.SurfaceSlot == right.SurfaceSlot
            && (left.StableFragmentIdentity != right.StableFragmentIdentity
                || left.FragmentSlot != right.FragmentSlot)
            && left.FragmentClip is { Length: > 0 }
            && right.FragmentClip is { Length: > 0 };
    }

    private Reference3DRenderItem[] SortReference3DIntersectingRenderGroups(
        IReadOnlyList<Reference3DRenderGroup> sourceGroups,
        Reference3DIntersectionPathCache pathCache)
    {
        var groups = sourceGroups.ToArray();
        if (groups.Length <= 1) return groups.SelectMany(group => group.Items).ToArray();

        var surfaceRegions = new Reference3DGroupSurfaceRegion[groups.Length][];
        var edgeStrokeRegions = new PathsD[groups.Length];
        var surfaceRegionsInitialized = new bool[groups.Length];
        var edgeStrokeRegionsInitialized = new bool[groups.Length];
        var strokeOcclusionCache = new Dictionary<
            (int ObjectIndex, int SurfaceSlot),
            Reference3DProjectedContour[]>();
        var bounds = new RectangleF[groups.Length];
        var hasBounds = new bool[groups.Length];
        var singleObjectIndexByGroup = new int[groups.Length];
        var sharedShellOwnerByGroup = new int[groups.Length];
        var containsThicknessSurfaces = false;
        for (var index = 0; index < groups.Length; index++)
        {
            surfaceRegions[index] = [];
            edgeStrokeRegions[index] = [];
            hasBounds[index] = TryGetReference3DGroupBounds(groups[index], out bounds[index]);
            singleObjectIndexByGroup[index] = GetReference3DSingleObjectIndex(groups[index]);
            sharedShellOwnerByGroup[index] = GetReference3DSingleSharedShellOwner(groups[index]);
            containsThicknessSurfaces |= groups[index].Items.Any(item =>
                item.Kind is Reference3DRenderKind.Back or Reference3DRenderKind.Side);
        }
        pathCache.PreferConvexRegionParts = groups.Length > 256 && containsThicknessSurfaces;

        var edges = Enumerable.Range(0, groups.Length).Select(_ => new HashSet<int>()).ToArray();
        var mandatoryEdges = Enumerable.Range(0, groups.Length).Select(_ => new HashSet<int>()).ToArray();
        var indegree = new int[groups.Length];
        foreach (var (leftIndex, rightIndex) in GetReference3DConstraintCandidates(bounds, hasBounds))
        {
            var leftHasEdge = TryGetReference3DGroupEdgeItem(groups[leftIndex], out var leftEdge);
            var rightHasEdge = TryGetReference3DGroupEdgeItem(groups[rightIndex], out var rightEdge);
            if (!leftHasEdge
                && !rightHasEdge
                && singleObjectIndexByGroup[leftIndex] >= 0
                && singleObjectIndexByGroup[leftIndex] == singleObjectIndexByGroup[rightIndex])
            {
                continue;
            }
            if (!leftHasEdge
                && !rightHasEdge
                && sharedShellOwnerByGroup[leftIndex] >= 0
                && sharedShellOwnerByGroup[leftIndex] == sharedShellOwnerByGroup[rightIndex])
            {
                continue;
            }

            EnsureReference3DSurfaceRegions(leftIndex);
            EnsureReference3DSurfaceRegions(rightIndex);
            if (leftHasEdge) EnsureReference3DEdgeStrokeRegion(leftIndex, leftEdge);
            if (rightHasEdge) EnsureReference3DEdgeStrokeRegion(rightIndex, rightEdge);
            if ((leftHasEdge || rightHasEdge)
                && TryAddReference3DIntersectionEdgeConstraint(
                    groups,
                    surfaceRegions,
                    edgeStrokeRegions,
                    leftIndex,
                    rightIndex,
                    edges,
                    indegree,
                    mandatoryEdges))
            {
                continue;
            }

            if (leftHasEdge && rightHasEdge)
            {
                if (!TryFindReference3DRegionOverlapPoint(
                        edgeStrokeRegions[leftIndex],
                        edgeStrokeRegions[rightIndex],
                        out var edgeOverlap,
                        Reference3DIntersectionLineEpsilon)
                    || !TryGetReference3DPlaneDepthAtScreen(
                        leftEdge.Plane,
                        Reference3DClosestPointOnEdge(leftEdge, edgeOverlap),
                        out var leftEdgeDepth)
                    || !TryGetReference3DPlaneDepthAtScreen(
                        rightEdge.Plane,
                        Reference3DClosestPointOnEdge(rightEdge, edgeOverlap),
                        out var rightEdgeDepth))
                {
                    continue;
                }

                if (Math.Abs(leftEdgeDepth - rightEdgeDepth) > 0.01f)
                {
                    if (leftEdgeDepth > rightEdgeDepth)
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
                    var comparison = CompareReference3DRenderGroupStableIdentity(
                        groups[leftIndex],
                        groups[rightIndex]);
                    if (comparison < 0)
                    {
                        AddReference3DRenderConstraint(leftIndex, rightIndex, edges, indegree);
                    }
                    else if (comparison > 0)
                    {
                        AddReference3DRenderConstraint(rightIndex, leftIndex, edges, indegree);
                    }
                }
                continue;
            }
            if (leftHasEdge || rightHasEdge)
            {
                var edgeIndex = leftHasEdge ? leftIndex : rightIndex;
                var surfaceIndex = leftHasEdge ? rightIndex : leftIndex;
                var edge = leftHasEdge ? leftEdge : rightEdge;
                if (!TryFindReference3DEdgeGroupRegionOverlapPoint(
                        edgeStrokeRegions[edgeIndex],
                        surfaceRegions[surfaceIndex],
                        out var surface,
                        out var edgeSample)
                    || !TryGetReference3DPlaneDepthAtScreen(
                        edge.Plane,
                        Reference3DClosestPointOnEdge(edge, edgeSample),
                        out var edgeDepth)
                    || !TryGetReference3DPlaneDepthAtScreen(
                        surface.Item.Plane,
                        edgeSample,
                        out var surfaceDepth))
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

            if (!TryFindReference3DGroupRegionOverlapPoint(
                    surfaceRegions[leftIndex],
                    surfaceRegions[rightIndex],
                    pathCache,
                    out var left,
                    out var right,
                    out var sample)
                || !TryGetReference3DDepthDifference(
                    sample,
                    left.Item.Plane,
                    right.Item.Plane,
                    out var depthDifference))
            {
                continue;
            }

            if (Math.Abs(depthDifference) > 0.01f)
            {
                if (depthDifference > 0)
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
                var comparison = CompareReference3DCoplanarItems(left.Item, right.Item);
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

        var ordered = OrderReference3DConstraintComponents(groups, edges, mandatoryEdges);

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

        void EnsureReference3DSurfaceRegions(int index)
        {
            if (surfaceRegionsInitialized[index]) return;
            surfaceRegions[index] = BuildReference3DGroupSurfaceRegions(
                groups[index],
                pathCache,
                strokeOcclusionCache);
            surfaceRegionsInitialized[index] = true;
        }

        void EnsureReference3DEdgeStrokeRegion(int index, Reference3DRenderItem edge)
        {
            if (edgeStrokeRegionsInitialized[index]) return;
            edgeStrokeRegions[index] = TryBuildReference3DEdgeStrokeRegion(edge, out var strokeRegion)
                ? strokeRegion
                : [];
            edgeStrokeRegionsInitialized[index] = true;
        }
    }

    private static int GetReference3DSingleObjectIndex(Reference3DRenderGroup group)
    {
        if (group.Items.Length == 0) return -1;
        var objectIndex = group.Items[0].ObjectIndex;
        for (var index = 1; index < group.Items.Length; index++)
        {
            if (group.Items[index].ObjectIndex != objectIndex) return -1;
        }
        return objectIndex;
    }

    private static int GetReference3DSingleSharedShellOwner(Reference3DRenderGroup group)
    {
        if (group.Items.Length == 0) return -1;
        var owner = group.Items[0].SharedShellOwnerObjectIndex;
        if (owner < 0) return -1;
        for (var index = 1; index < group.Items.Length; index++)
        {
            if (group.Items[index].SharedShellOwnerObjectIndex != owner) return -1;
        }
        return owner;
    }

    private static int[] OrderReference3DConstraintComponents(
        IReadOnlyList<Reference3DRenderGroup> groups,
        IReadOnlyList<HashSet<int>> edges,
        IReadOnlyList<HashSet<int>> mandatoryEdges)
    {
        var indices = Enumerable.Repeat(-1, groups.Count).ToArray();
        var lowLinks = new int[groups.Count];
        var onStack = new bool[groups.Count];
        var stack = new Stack<int>();
        var components = new List<int[]>();
        var componentByNode = new int[groups.Count];
        var nextIndex = 0;
        for (var node = 0; node < groups.Count; node++)
        {
            if (indices[node] < 0) Visit(node);
        }

        var componentEdges = Enumerable.Range(0, components.Count)
            .Select(_ => new HashSet<int>())
            .ToArray();
        var componentIndegree = new int[components.Count];
        for (var source = 0; source < groups.Count; source++)
        {
            var sourceComponent = componentByNode[source];
            foreach (var destination in edges[source])
            {
                var destinationComponent = componentByNode[destination];
                if (sourceComponent == destinationComponent
                    || !componentEdges[sourceComponent].Add(destinationComponent))
                {
                    continue;
                }
                componentIndegree[destinationComponent]++;
            }
        }

        var stableGroupComparer = Comparer<int>.Create((left, right) =>
        {
            var comparison = CompareReference3DRenderGroupStableIdentity(
                groups[left],
                groups[right]);
            return comparison != 0 ? comparison : left.CompareTo(right);
        });
        var renderGroupComparer = Comparer<int>.Create((left, right) =>
        {
            var comparison = CompareReference3DRenderGroups(groups[left], groups[right]);
            if (comparison == 0) comparison = stableGroupComparer.Compare(left, right);
            return comparison != 0 ? comparison : left.CompareTo(right);
        });
        var representatives = new int[components.Count];
        for (var component = 0; component < components.Count; component++)
        {
            representatives[component] = components[component].Order(renderGroupComparer).First();
        }
        var componentComparer = Comparer<int>.Create((left, right) =>
        {
            var comparison = renderGroupComparer.Compare(
                representatives[left],
                representatives[right]);
            return comparison != 0 ? comparison : left.CompareTo(right);
        });
        var readyComponents = new SortedSet<int>(
            Enumerable.Range(0, components.Count)
                .Where(component => componentIndegree[component] == 0),
            componentComparer);
        var componentOrder = new List<int>(components.Count);
        while (readyComponents.Count > 0)
        {
            var component = readyComponents.Min;
            readyComponents.Remove(component);
            componentOrder.Add(component);
            foreach (var next in componentEdges[component])
            {
                componentIndegree[next]--;
                if (componentIndegree[next] == 0) readyComponents.Add(next);
            }
        }

        var ordered = new List<int>(groups.Count);
        var localIndegree = new int[groups.Count];
        var mandatoryIndegree = new int[groups.Count];
        var emitted = new bool[groups.Count];
        foreach (var component in componentOrder)
        {
            var nodes = components[component];
            if (nodes.Length == 1)
            {
                ordered.Add(nodes[0]);
                continue;
            }

            foreach (var node in nodes)
            {
                localIndegree[node] = 0;
                mandatoryIndegree[node] = 0;
                emitted[node] = false;
            }
            foreach (var node in nodes)
            {
                foreach (var next in edges[node])
                {
                    if (componentByNode[next] == component) localIndegree[next]++;
                }
                foreach (var next in mandatoryEdges[node])
                {
                    if (componentByNode[next] == component) mandatoryIndegree[next]++;
                }
            }
            var readyNodes = new SortedSet<int>(
                nodes.Where(node => localIndegree[node] == 0),
                renderGroupComparer);
            var emittedCount = 0;
            while (emittedCount < nodes.Length)
            {
                while (readyNodes.Count > 0)
                {
                    var node = readyNodes.Min;
                    readyNodes.Remove(node);
                    if (emitted[node]) continue;
                    emitted[node] = true;
                    emittedCount++;
                    ordered.Add(node);
                    foreach (var next in edges[node])
                    {
                        if (componentByNode[next] != component) continue;
                        localIndegree[next]--;
                        if (localIndegree[next] == 0) readyNodes.Add(next);
                    }
                    foreach (var next in mandatoryEdges[node])
                    {
                        if (componentByNode[next] == component) mandatoryIndegree[next]--;
                    }
                }
                if (emittedCount == nodes.Length) break;

                var cycleBreak = -1;
                foreach (var node in nodes)
                {
                    if (emitted[node] || mandatoryIndegree[node] != 0) continue;
                    if (cycleBreak < 0 || renderGroupComparer.Compare(node, cycleBreak) < 0)
                    {
                        cycleBreak = node;
                    }
                }
                if (cycleBreak < 0)
                {
                    cycleBreak = nodes
                        .Where(node => !emitted[node])
                        .Order(renderGroupComparer)
                        .First();
                }
                localIndegree[cycleBreak] = 0;
                mandatoryIndegree[cycleBreak] = 0;
                readyNodes.Add(cycleBreak);
            }
        }
        return ordered.ToArray();

        void Visit(int node)
        {
            indices[node] = nextIndex;
            lowLinks[node] = nextIndex;
            nextIndex++;
            stack.Push(node);
            onStack[node] = true;
            foreach (var next in edges[node])
            {
                if (indices[next] < 0)
                {
                    Visit(next);
                    lowLinks[node] = Math.Min(lowLinks[node], lowLinks[next]);
                }
                else if (onStack[next])
                {
                    lowLinks[node] = Math.Min(lowLinks[node], indices[next]);
                }
            }
            if (lowLinks[node] != indices[node]) return;

            var members = new List<int>();
            while (stack.Count > 0)
            {
                var member = stack.Pop();
                onStack[member] = false;
                componentByNode[member] = components.Count;
                members.Add(member);
                if (member == node) break;
            }
            components.Add(members.ToArray());
        }
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

    private Reference3DGroupSurfaceRegion[] BuildReference3DGroupSurfaceRegions(
        Reference3DRenderGroup group,
        Reference3DIntersectionPathCache pathCache,
        IDictionary<
            (int ObjectIndex, int SurfaceSlot),
            Reference3DProjectedContour[]> strokeOcclusionCache)
    {
        var result = new List<Reference3DGroupSurfaceRegion>();
        foreach (var sourceItem in group.Items)
        {
            var item = sourceItem;
            if (item.Kind == Reference3DRenderKind.FrontStroke
                && item.OcclusionContours is not { Length: > 0 })
            {
                var key = (item.ObjectIndex, item.SurfaceSlot);
                if (!strokeOcclusionCache.TryGetValue(key, out var strokeContours))
                {
                    strokeContours = GetReference3DProjectedStrokeOcclusionContours(
                        item.ObjectIndex,
                        item.Contours);
                    strokeOcclusionCache.Add(key, strokeContours);
                }
                item = item with { OcclusionContours = strokeContours };
            }
            if (!TryGetReference3DOcclusionContours(item, out _)) continue;
            if (!TryBuildReference3DItemRegion(item, pathCache, out var paths))
            {
                continue;
            }

            if (!TryGetReference3DPathsBounds(paths, out var bounds))
            {
                continue;
            }
            result.Add(new Reference3DGroupSurfaceRegion(item, paths, bounds));
        }
        return result.ToArray();
    }

    private static bool TryFindReference3DGroupRegionOverlapPoint(
        IReadOnlyList<Reference3DGroupSurfaceRegion> leftRegions,
        IReadOnlyList<Reference3DGroupSurfaceRegion> rightRegions,
        Reference3DIntersectionPathCache pathCache,
        out Reference3DGroupSurfaceRegion left,
        out Reference3DGroupSurfaceRegion right,
        out PointF point)
    {
        foreach (var leftCandidate in leftRegions)
        {
            foreach (var rightCandidate in rightRegions)
            {
                if (Reference3DRenderItemsSharePhysicalShell(
                        leftCandidate.Item,
                        rightCandidate.Item)
                    || Reference3DAreDistinctFragmentsOfSameSurface(
                        leftCandidate.Item,
                        rightCandidate.Item)
                    || !Reference3DProjectedBoundsOverlap(
                        leftCandidate.Bounds,
                        rightCandidate.Bounds)
                    || !TryFindReference3DCachedRegionOverlapPoint(
                        leftCandidate.Paths,
                        rightCandidate.Paths,
                        pathCache,
                        out point))
                {
                    continue;
                }

                left = leftCandidate;
                right = rightCandidate;
                return true;
            }
        }

        left = default;
        right = default;
        point = PointF.Empty;
        return false;
    }

    private static bool Reference3DRenderItemsSharePhysicalShell(
        Reference3DRenderItem left,
        Reference3DRenderItem right)
    {
        return left.ObjectIndex == right.ObjectIndex
            || left.SharedShellOwnerObjectIndex >= 0
                && left.SharedShellOwnerObjectIndex == right.SharedShellOwnerObjectIndex;
    }

    private static bool TryFindReference3DEdgeGroupRegionOverlapPoint(
        PathsD edgeStrokeRegion,
        IReadOnlyList<Reference3DGroupSurfaceRegion> regions,
        out Reference3DGroupSurfaceRegion surface,
        out PointF point)
    {
        if (edgeStrokeRegion.Count == 0)
        {
            surface = default;
            point = PointF.Empty;
            return false;
        }
        foreach (var candidate in regions)
        {
            if (!TryFindReference3DRegionOverlapPoint(
                    edgeStrokeRegion,
                    candidate.Paths,
                    out point,
                    Reference3DIntersectionLineEpsilon))
            {
                continue;
            }

            surface = candidate;
            return true;
        }

        surface = default;
        point = PointF.Empty;
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
            if (!TryGetReference3DProjectedBounds(
                    item.FragmentClip ?? item.OcclusionContours ?? item.Contours,
                    radius,
                    out var itemBounds))
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
        IReadOnlyList<Reference3DGroupSurfaceRegion[]> surfaceRegions,
        IReadOnlyList<PathsD> edgeStrokeRegions,
        int leftIndex,
        int rightIndex,
        IReadOnlyList<HashSet<int>> edges,
        int[] indegree,
        IReadOnlyList<HashSet<int>> mandatoryEdges)
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
        foreach (var surface in surfaceRegions[surfaceIndex])
        {
            var item = surface.Item;
            if (!TryGetReference3DOcclusionContours(item, out _)
                || (item.ObjectIndex != edge.ObjectIndex
                    && item.ObjectIndex != edge.SecondaryObjectIndex)
                || !TryFindReference3DRegionOverlapPoint(
                    edgeStrokeRegions[edgeIndex],
                    surface.Paths,
                    out _,
                    Reference3DIntersectionLineEpsilon))
            {
                continue;
            }
            belongsToPair = true;
            break;
        }
        if (!belongsToPair) return false;
        AddReference3DRenderConstraint(surfaceIndex, edgeIndex, edges, indegree);
        mandatoryEdges[surfaceIndex].Add(edgeIndex);
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
        Reference3DIntersectionPathCache pathCache,
        out PathsD region)
    {
        if (!TryGetReference3DOcclusionContours(item, out var contours))
        {
            region = [];
            return false;
        }
        if (item.Kind is Reference3DRenderKind.Back
                or Reference3DRenderKind.Side
                or Reference3DRenderKind.FrontFill
            && item.FragmentClip is { Length: > 0 } visibleClip
            && pathCache.TryGetVisibleFillPaths(visibleClip, out region))
        {
            return Reference3DPathsHaveArea(region);
        }

        region = pathCache.GetOrAddPaths(contours);
        if (region.Count == 0) return false;
        if (item.FragmentClip is not { Length: > 0 }) return true;
        try
        {
            region = Clipper.Intersect(
                region,
                pathCache.GetOrAddPaths(item.FragmentClip),
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
        return Reference3DRegionsOverlap(
            Reference3DContoursToPaths(left),
            Reference3DContoursToPaths(right));
    }

    private static bool Reference3DRegionsOverlap(PathsD left, PathsD right)
    {
        try
        {
            var intersection = Clipper.Intersect(
                left,
                right,
                FillRule.EvenOdd,
                Reference3DClipperPrecision);
            return Reference3DPathsHaveArea(intersection);
        }
        catch (Exception exception) when (Reference3DIsClipperFailure(exception))
        {
            return true;
        }
    }

    private static bool TryClassifyReference3DRegionOverlapAcrossLine(
        PathsD left,
        PathsD right,
        Reference3DScreenLine line,
        out bool regionsOverlap,
        out bool overlapsBothSides)
    {
        const double sideTolerance = 0.01d;
        regionsOverlap = false;
        overlapsBothSides = false;
        PathsD intersection;
        try
        {
            intersection = Clipper.Intersect(
                left,
                right,
                FillRule.EvenOdd,
                Reference3DClipperPrecision);
        }
        catch (Exception exception) when (Reference3DIsClipperFailure(exception))
        {
            return false;
        }

        var hasPositive = false;
        var hasNegative = false;
        foreach (var path in intersection)
        {
            if (path.Count < 3
                || Math.Abs(Clipper.Area(path)) <= Reference3DIntersectionAreaEpsilon)
            {
                continue;
            }
            regionsOverlap = true;
            foreach (var point in path)
            {
                if (!double.IsFinite(point.x) || !double.IsFinite(point.y)) continue;
                var distance = line.A * point.x + line.B * point.y + line.C;
                hasPositive |= distance > sideTolerance;
                hasNegative |= distance < -sideTolerance;
                if (!hasPositive || !hasNegative) continue;
                overlapsBothSides = true;
                return true;
            }
        }
        return true;
    }

    private static bool TryFindReference3DRegionOverlapPoint(
        PathsD left,
        PathsD right,
        out PointF point,
        double minimumArea = Reference3DIntersectionAreaEpsilon)
    {
        point = PointF.Empty;
        var convexOverlap = TryFindReference3DConvexRegionOverlapPoint(
            left,
            right,
            minimumArea,
            out var convexHandled,
            out point);
        if (convexHandled)
        {
            return convexOverlap;
        }
        try
        {
            var intersection = Clipper.Intersect(
                left,
                right,
                FillRule.EvenOdd,
                Reference3DClipperPrecision);
            return TryFindReference3DPointInPaths(intersection, out point, minimumArea);
        }
        catch (Exception exception) when (Reference3DIsClipperFailure(exception))
        {
            return false;
        }
    }

    private static bool TryFindReference3DCachedRegionOverlapPoint(
        PathsD left,
        PathsD right,
        Reference3DIntersectionPathCache pathCache,
        out PointF point,
        double minimumArea = Reference3DIntersectionAreaEpsilon)
    {
        if (pathCache.TryGetConvexRegionParts(left, out var leftRegions)
            && pathCache.TryGetConvexRegionParts(right, out var rightRegions))
        {
            var decomposed = leftRegions.Length > 1 || rightRegions.Length > 1;
            double overlapArea = 0;
            var overlapPoint = PointF.Empty;
            foreach (var leftRegion in leftRegions)
            {
                foreach (var rightRegion in rightRegions)
                {
                    if (!Reference3DProjectedBoundsOverlap(
                            leftRegion.Bounds,
                            rightRegion.Bounds))
                    {
                        continue;
                    }
                    var overlap = TryFindReference3DConvexRegionOverlapPoint(
                        leftRegion,
                        rightRegion,
                        decomposed ? 0 : minimumArea,
                        out var handled,
                        out var partPoint,
                        out var partArea);
                    if (!handled) return TryFindReference3DRegionOverlapPoint(
                        left,
                        right,
                        out point,
                        minimumArea);
                    if (!decomposed && overlap)
                    {
                        point = partPoint;
                        return true;
                    }
                    if (!overlap || partArea <= 0) continue;
                    if (overlapArea == 0) overlapPoint = partPoint;
                    overlapArea += partArea;
                    if (overlapArea > minimumArea)
                    {
                        point = overlapPoint;
                        return true;
                    }
                }
            }
            point = PointF.Empty;
            return false;
        }

        return TryFindReference3DRegionOverlapPoint(left, right, out point, minimumArea);
    }

    private static bool TryFindReference3DConvexRegionOverlapPoint(
        PathsD left,
        PathsD right,
        double minimumArea,
        out bool handled,
        out PointF point)
    {
        handled = false;
        point = PointF.Empty;
        return TryAnalyzeReference3DConvexRegion(left, out var leftRegion)
            && TryAnalyzeReference3DConvexRegion(right, out var rightRegion)
            && TryFindReference3DConvexRegionOverlapPoint(
                leftRegion,
                rightRegion,
                minimumArea,
                out handled,
                out point,
                out _);
    }

    private static bool TryFindReference3DConvexRegionOverlapPoint(
        Reference3DConvexRegion subjectRegion,
        Reference3DConvexRegion clipRegion,
        double minimumArea,
        out bool handled,
        out PointF point,
        out double overlapArea)
    {
        const double coordinateEpsilon = 0.000000001d;
        handled = false;
        point = PointF.Empty;
        overlapArea = 0;
        var subject = subjectRegion.Path;
        var clip = clipRegion.Path;
        var subjectCount = subjectRegion.PointCount;
        var clipCount = clipRegion.PointCount;
        var clipOrientation = clipRegion.Orientation;
        var maximumVertices = subjectCount + clipCount;
        if (maximumVertices > Reference3DMaximumConvexOverlapVertices)
        {
            return false;
        }

        handled = true;
        if (!Reference3DProjectedBoundsOverlap(subjectRegion.Bounds, clipRegion.Bounds))
        {
            return false;
        }
        Span<PointD> input = stackalloc PointD[maximumVertices];
        Span<PointD> output = stackalloc PointD[maximumVertices];
        for (var index = 0; index < subjectCount; index++)
        {
            input[index] = subject[index];
        }
        var inputCount = subjectCount;
        for (var edgeIndex = 0; edgeIndex < clipCount && inputCount >= 3; edgeIndex++)
        {
            var edgeStart = clip[edgeIndex];
            var edgeEnd = clip[(edgeIndex + 1) % clipCount];
            var edgeLength = Math.Sqrt(
                (edgeEnd.x - edgeStart.x) * (edgeEnd.x - edgeStart.x)
                + (edgeEnd.y - edgeStart.y) * (edgeEnd.y - edgeStart.y));
            if (!double.IsFinite(edgeLength) || edgeLength <= coordinateEpsilon)
            {
                handled = false;
                return false;
            }
            var sideEpsilon = Reference3DConvexTolerancePixels * Math.Max(1d, edgeLength);
            var hasInside = false;
            var hasOutside = false;
            for (var index = 0; index < inputCount; index++)
            {
                var distance = clipOrientation * Cross(edgeStart, edgeEnd, input[index]);
                hasInside |= distance >= -sideEpsilon;
                hasOutside |= distance < -sideEpsilon;
                if (hasInside && hasOutside) break;
            }
            if (!hasInside) return false;
            if (!hasOutside) continue;

            var previous = input[inputCount - 1];
            var previousDistance = clipOrientation * Cross(edgeStart, edgeEnd, previous);
            var previousInside = previousDistance >= -sideEpsilon;
            var outputCount = 0;
            for (var index = 0; index < inputCount; index++)
            {
                var current = input[index];
                var currentDistance = clipOrientation * Cross(edgeStart, edgeEnd, current);
                var currentInside = currentDistance >= -sideEpsilon;
                if (currentInside != previousInside)
                {
                    var denominator = previousDistance - currentDistance;
                    if (!double.IsFinite(denominator) || Math.Abs(denominator) <= coordinateEpsilon)
                    {
                        handled = false;
                        return false;
                    }
                    var amount = Math.Clamp(previousDistance / denominator, 0d, 1d);
                    if (outputCount >= maximumVertices)
                    {
                        handled = false;
                        return false;
                    }
                    output[outputCount++] = new PointD(
                        previous.x + (current.x - previous.x) * amount,
                        previous.y + (current.y - previous.y) * amount);
                }
                if (currentInside)
                {
                    if (outputCount >= maximumVertices)
                    {
                        handled = false;
                        return false;
                    }
                    output[outputCount++] = current;
                }
                previous = current;
                previousDistance = currentDistance;
                previousInside = currentInside;
            }
            var swap = input;
            input = output;
            output = swap;
            inputCount = outputCount;
        }
        if (inputCount < 3) return false;

        double twiceArea = 0;
        double centerX = 0;
        double centerY = 0;
        var previousPoint = input[inputCount - 1];
        for (var index = 0; index < inputCount; index++)
        {
            var current = input[index];
            twiceArea += previousPoint.x * current.y - current.x * previousPoint.y;
            centerX += current.x;
            centerY += current.y;
            previousPoint = current;
        }
        var area = Math.Abs(twiceArea) * 0.5d;
        if (!double.IsFinite(area)
            || !double.IsFinite(centerX)
            || !double.IsFinite(centerY))
        {
            handled = false;
            return false;
        }
        overlapArea = area;
        if (area <= minimumArea)
        {
            if (area + Math.Max(coordinateEpsilon, minimumArea * 0.000001d) > minimumArea)
            {
                handled = false;
            }
            return false;
        }
        point = new PointF((float)(centerX / inputCount), (float)(centerY / inputCount));
        if (float.IsFinite(point.X) && float.IsFinite(point.Y)) return true;
        handled = false;
        point = PointF.Empty;
        return false;

        static double Cross(PointD start, PointD end, PointD point)
        {
            return (end.x - start.x) * (point.y - start.y)
                - (end.y - start.y) * (point.x - start.x);
        }
    }

    private static bool TryAnalyzeReference3DConvexRegion(
        PathsD paths,
        out Reference3DConvexRegion region)
    {
        region = default;
        if (paths.Count != 1) return false;
        return TryAnalyzeReference3DConvexPath(paths[0], out region);
    }

    private static bool TryAnalyzeReference3DConvexPath(
        PathD path,
        out Reference3DConvexRegion region,
        bool validateConvexity = true)
    {
        const double coordinateEpsilon = 0.000000001d;
        region = default;
        var count = path.Count;
        while (count > 1
               && Math.Abs(path[0].x - path[count - 1].x) <= coordinateEpsilon
               && Math.Abs(path[0].y - path[count - 1].y) <= coordinateEpsilon)
        {
            count--;
        }
        if (count < 3 || count > Reference3DMaximumConvexOverlapVertices) return false;

        double twiceArea = 0;
        var minimumX = double.PositiveInfinity;
        var minimumY = double.PositiveInfinity;
        var maximumX = double.NegativeInfinity;
        var maximumY = double.NegativeInfinity;
        var previous = path[count - 1];
        for (var index = 0; index < count; index++)
        {
            var current = path[index];
            if (!double.IsFinite(current.x)
                || !double.IsFinite(current.y)
                || Math.Abs(previous.x - current.x) <= coordinateEpsilon
                && Math.Abs(previous.y - current.y) <= coordinateEpsilon)
            {
                return false;
            }
            twiceArea += previous.x * current.y - current.x * previous.y;
            minimumX = Math.Min(minimumX, current.x);
            minimumY = Math.Min(minimumY, current.y);
            maximumX = Math.Max(maximumX, current.x);
            maximumY = Math.Max(maximumY, current.y);
            previous = current;
        }
        if (!double.IsFinite(twiceArea) || Math.Abs(twiceArea) <= coordinateEpsilon)
        {
            return false;
        }

        var orientation = Math.Sign(twiceArea);
        if (validateConvexity)
        {
            for (var edgeIndex = 0; edgeIndex < count; edgeIndex++)
            {
                var start = path[edgeIndex];
                var end = path[(edgeIndex + 1) % count];
                var edgeLength = Math.Sqrt(
                    (end.x - start.x) * (end.x - start.x)
                    + (end.y - start.y) * (end.y - start.y));
                if (!double.IsFinite(edgeLength) || edgeLength <= coordinateEpsilon)
                {
                    return false;
                }
                var sideEpsilon = Reference3DConvexTolerancePixels * Math.Max(1d, edgeLength);
                for (var pointIndex = 0; pointIndex < count; pointIndex++)
                {
                    if (pointIndex == edgeIndex || pointIndex == (edgeIndex + 1) % count)
                    {
                        continue;
                    }
                    var point = path[pointIndex];
                    var cross = (end.x - start.x) * (point.y - start.y)
                        - (end.y - start.y) * (point.x - start.x);
                    if (orientation * cross < -sideEpsilon) return false;
                }
            }
        }

        if (!double.IsFinite(minimumX)
            || !double.IsFinite(minimumY)
            || !double.IsFinite(maximumX)
            || !double.IsFinite(maximumY)
            || minimumX < float.MinValue
            || minimumY < float.MinValue
            || maximumX > float.MaxValue
            || maximumY > float.MaxValue)
        {
            return false;
        }
        region = new Reference3DConvexRegion(
            path,
            count,
            orientation,
            RectangleF.FromLTRB(
                (float)minimumX,
                (float)minimumY,
                (float)maximumX,
                (float)maximumY));
        return true;
    }

    private static bool TryBuildReference3DEdgeStrokeRegion(
        Reference3DRenderItem edge,
        out PathsD region)
    {
        region = [];
        var width = Math.Max(
            ReferenceSurfaceOverlapTolerancePixels * 2f,
            edge.EdgeWidth + ReferenceSurfaceOverlapTolerancePixels * 2f);
        if (!float.IsFinite(width) || width <= 0) return false;
        var radius = width * 0.5d;

        try
        {
            foreach (var contour in edge.Contours)
            {
                if (contour.Points.Length < 2
                    || contour.Points.Any(point =>
                        !float.IsFinite(point.X) || !float.IsFinite(point.Y)))
                {
                    continue;
                }
                var centerline = new PathsD
                {
                    new PathD(contour.Points.Select(point => new PointD(point.X, point.Y)))
                };
                var endType = edge.EdgeStartCap && edge.EdgeEndCap
                    ? EndType.Round
                    : EndType.Butt;
                region.AddRange(Clipper.InflatePaths(
                    centerline,
                    radius,
                    JoinType.Round,
                    endType,
                    precision: Reference3DClipperPrecision));
                if (edge.EdgeStartCap != edge.EdgeEndCap)
                {
                    var roundPoint = edge.EdgeStartCap
                        ? contour.Points[0]
                        : contour.Points[^1];
                    region.AddRange(Clipper.InflatePaths(
                        new PathsD
                        {
                            new PathD { new PointD(roundPoint.X, roundPoint.Y) }
                        },
                        radius,
                        JoinType.Round,
                        EndType.Round,
                        precision: Reference3DClipperPrecision));
                }
            }
            if (region.Count == 0) return false;
            if (region.Count > 1)
            {
                region = Clipper.Union(
                    region,
                    new PathsD(),
                    FillRule.NonZero,
                    Reference3DClipperPrecision);
            }
            return region.Any(path => path.Count >= 3
                && Math.Abs(Clipper.Area(path)) > Reference3DIntersectionLineEpsilon);
        }
        catch (Exception exception) when (Reference3DIsClipperFailure(exception))
        {
            region = [];
            return false;
        }
    }

    private static PointF Reference3DClosestPointOnEdge(
        Reference3DRenderItem edge,
        PointF point)
    {
        var bestPoint = point;
        var bestDistanceSquared = float.PositiveInfinity;
        foreach (var contour in edge.Contours)
        {
            for (var index = 1; index < contour.Points.Length; index++)
            {
                var start = contour.Points[index - 1];
                var end = contour.Points[index];
                var dx = end.X - start.X;
                var dy = end.Y - start.Y;
                var lengthSquared = dx * dx + dy * dy;
                var amount = lengthSquared <= Reference3DIntersectionLineEpsilon
                    ? 0f
                    : Math.Clamp(
                        ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared,
                        0f,
                        1f);
                var candidate = new PointF(start.X + dx * amount, start.Y + dy * amount);
                var offsetX = candidate.X - point.X;
                var offsetY = candidate.Y - point.Y;
                var distanceSquared = offsetX * offsetX + offsetY * offsetY;
                if (distanceSquared < bestDistanceSquared)
                {
                    bestDistanceSquared = distanceSquared;
                    bestPoint = candidate;
                }
            }
        }
        return bestPoint;
    }

    private static bool TryFindReference3DRegionPoint(
        PathsD surface,
        PathsD clip,
        out PointF point,
        out PathsD intersection)
    {
        point = PointF.Empty;
        intersection = [];
        try
        {
            intersection = Clipper.Intersect(
                surface,
                clip,
                FillRule.EvenOdd,
                Reference3DClipperPrecision);
            return TryFindReference3DPointInPaths(intersection, out point);
        }
        catch (Exception exception) when (Reference3DIsClipperFailure(exception))
        {
            intersection = [];
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

    private static Reference3DProjectedContour[] CreateReference3DClosedStrokeOcclusionContours(
        Reference3DProjectedContour centerline,
        float width)
    {
        var count = EffectiveReference3DPointCount(centerline.Points, closed: true);
        if (count < 3 || !float.IsFinite(width) || width <= 0) return [];
        try
        {
            var source = new PathsD
            {
                new(centerline.Points
                    .Take(count)
                    .Select(point => new PointD(point.X, point.Y)))
            };
            return Clipper.InflatePaths(
                    source,
                    width * 0.5,
                    JoinType.Round,
                    EndType.Joined,
                    precision: Reference3DClipperPrecision)
                .Where(path => path.Count >= 3)
                .Select(path => new Reference3DProjectedContour(
                    path.Select(point => new PointF((float)point.x, (float)point.y)).ToArray(),
                    true,
                    centerline.AverageDepth))
                .ToArray();
        }
        catch (Exception exception) when (Reference3DIsClipperFailure(exception))
        {
            return [];
        }
    }

    private static Reference3DProjectedContour[] CreateReference3DOpenStrokeOcclusionContours(
        Reference3DProjectedContour centerline,
        float width,
        bool roundStart,
        bool roundEnd,
        bool miterJoin)
    {
        var count = EffectiveReference3DPointCount(centerline.Points, closed: false);
        if (count < 2
            || !float.IsFinite(width)
            || width <= 0
            || centerline.Points.Take(count).Any(point =>
                !float.IsFinite(point.X) || !float.IsFinite(point.Y)))
        {
            return [];
        }
        try
        {
            var source = new PathsD
            {
                new(centerline.Points
                    .Take(count)
                    .Select(point => new PointD(point.X, point.Y)))
            };
            var region = Clipper.InflatePaths(
                    source,
                    width * 0.5,
                    miterJoin ? JoinType.Miter : JoinType.Round,
                    roundStart && roundEnd ? EndType.Round : EndType.Butt,
                    miterLimit: 8d,
                    precision: Reference3DClipperPrecision);
            if (roundStart != roundEnd)
            {
                var roundPoint = roundStart ? centerline.Points[0] : centerline.Points[count - 1];
                region.AddRange(Clipper.InflatePaths(
                    new PathsD
                    {
                        new PathD { new PointD(roundPoint.X, roundPoint.Y) }
                    },
                    width * 0.5,
                    JoinType.Round,
                    EndType.Round,
                    precision: Reference3DClipperPrecision));
            }
            if (region.Count > 1)
            {
                region = Clipper.Union(
                    region,
                    new PathsD(),
                    FillRule.NonZero,
                    Reference3DClipperPrecision);
            }
            return region
                .Where(path => path.Count >= 3)
                .Select(path => new Reference3DProjectedContour(
                    path.Select(point => new PointF((float)point.x, (float)point.y)).ToArray(),
                    true,
                    centerline.AverageDepth))
                .ToArray();
        }
        catch (Exception exception) when (Reference3DIsClipperFailure(exception))
        {
            return [];
        }
    }

    private void AppendReference3DLineEndpointJoinContours(
        int objectIndex,
        IReadOnlyList<Reference3DProjectedContour> centerlines,
        float width,
        ICollection<Reference3DProjectedContour> destination)
    {
        AppendEndpoint(startEndpoint: true);
        AppendEndpoint(startEndpoint: false);

        void AppendEndpoint(bool startEndpoint)
        {
            if (!TryGetReference3DLineEndpointJoin(
                    objectIndex,
                    startEndpoint,
                    centerlines,
                    width,
                    out var joint,
                    out var miters,
                    out var depth))
            {
                return;
            }
            foreach (var miter in miters)
            {
                AddJoinContour(
                    [joint, miter.OuterFirstOffset, miter.OuterMiter, miter.OuterSecondOffset],
                    depth);
                AddJoinContour(
                    [joint, miter.InnerFirstOffset, miter.InnerMiter, miter.InnerSecondOffset],
                    depth);
            }
        }

        void AddJoinContour(PointF[] points, float depth)
        {
            var twiceArea = 0d;
            for (var index = 0; index < points.Length; index++)
            {
                var next = (index + 1) % points.Length;
                twiceArea += points[index].X * points[next].Y - points[next].X * points[index].Y;
            }
            if (Math.Abs(twiceArea) <= Reference3DIntersectionLineEpsilon) return;
            destination.Add(new Reference3DProjectedContour(points, true, depth));
        }
    }

    private static Reference3DProjectedContour[] UnionReference3DStrokeOcclusionContours(
        IReadOnlyList<Reference3DProjectedContour> contours)
    {
        if (contours.Count <= 1) return contours.ToArray();
        var averageDepth = contours.Average(contour => (double)contour.AverageDepth);
        try
        {
            return Clipper.Union(
                    Reference3DContoursToPaths(contours),
                    new PathsD(),
                    FillRule.NonZero,
                    Reference3DClipperPrecision)
                .Where(path => path.Count >= 3)
                .Select(path => new Reference3DProjectedContour(
                    path.Select(point => new PointF((float)point.x, (float)point.y)).ToArray(),
                    true,
                    (float)averageDepth))
                .ToArray();
        }
        catch (Exception exception) when (Reference3DIsClipperFailure(exception))
        {
            return contours.ToArray();
        }
    }

    private static bool Reference3DPathsHaveArea(PathsD paths)
    {
        return paths.Any(path => path.Count >= 3
            && Math.Abs(Clipper.Area(path)) > Reference3DIntersectionAreaEpsilon);
    }

    private static bool TryGetReference3DPathsBounds(PathsD paths, out RectangleF bounds)
    {
        var left = double.PositiveInfinity;
        var top = double.PositiveInfinity;
        var right = double.NegativeInfinity;
        var bottom = double.NegativeInfinity;
        foreach (var path in paths)
        {
            foreach (var point in path)
            {
                if (!double.IsFinite(point.x) || !double.IsFinite(point.y)) continue;
                left = Math.Min(left, point.x);
                top = Math.Min(top, point.y);
                right = Math.Max(right, point.x);
                bottom = Math.Max(bottom, point.y);
            }
        }

        var floatLeft = (float)left;
        var floatTop = (float)top;
        var floatRight = (float)right;
        var floatBottom = (float)bottom;
        if (!float.IsFinite(floatLeft)
            || !float.IsFinite(floatTop)
            || !float.IsFinite(floatRight)
            || !float.IsFinite(floatBottom))
        {
            bounds = RectangleF.Empty;
            return false;
        }

        bounds = RectangleF.FromLTRB(floatLeft, floatTop, floatRight, floatBottom);
        return true;
    }

    private static bool TryFindReference3DPointInPaths(
        PathsD paths,
        out PointF point,
        double minimumArea = Reference3DIntersectionAreaEpsilon)
    {
        point = PointF.Empty;
        if (!paths.Any(path => path.Count >= 3
                && Math.Abs(Clipper.Area(path)) > minimumArea))
        {
            return false;
        }
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
