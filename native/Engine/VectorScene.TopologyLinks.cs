using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Clipper2Lib;

namespace VectorAnimationEngine;

internal sealed partial class VectorScene
{
    public DrawingElementHit DetachElementForMove(DrawingElementHit hit, int frame)
    {
        if (!hit.IsValid || (uint)hit.Key.ObjectIndex >= ObjectCount) return hit;
        var materialized = MaterializeSelectedParts(new[] { hit.Key }, frame);
        if (!materialized.Success || materialized.Parts.Length != 1) return DrawingElementHit.None;
        if (!materialized.Changed) return hit;
        return new DrawingElementHit(materialized.Parts[0].Result, -1, 0, 1);
    }

    private bool IsLinkedBoundaryStroke(int index) =>
        IsFillBoundaryLinkedStrokeShape(ShapeKind[index])
        || ShapeKind[index] == VectorAnimationEngine.ShapeKind.Path && !HasFill(index) && HasStroke(index);

    private bool CanLinkFillBoundaryLayers(int stroke, int fill) =>
        ObjectLayer[stroke] == ObjectLayer[fill]
        || GetSvgStrokeStyle(stroke) is not null && FillAutoMergeProtected[fill]
            && !string.IsNullOrEmpty(LayerParentIds[ObjectLayer[stroke]])
            && LayerParentIds[ObjectLayer[stroke]] == LayerParentIds[ObjectLayer[fill]];

    private (int SegmentIndex, CubicBoundarySegment Curve)[] LinkedStrokeCurves(int index)
    {
        if (ShapeKind[index] == VectorAnimationEngine.ShapeKind.Line)
            return [(-1, LineCurve(index))];
        if (TryGetFreehandBezierWorldNodes(index, out var nodes))
            return Enumerable.Range(0, nodes.Length - 1).Select(segment => (segment,
                new CubicBoundarySegment(nodes[segment].Anchor, nodes[segment].OutgoingControl,
                    nodes[segment + 1].IncomingControl, nodes[segment + 1].Anchor))).ToArray();
        if (!HasFill(index) && TryGetPathBezierWorldContours(index, out _))
            return GetPathBezierSegmentParts(index).Select(part => (part.PartIndex, part.Curve)).ToArray();
        return [];
    }

    private bool TryGetLinkedStrokeCurve(int index, int segment, out CubicBoundarySegment curve)
    {
        foreach (var item in LinkedStrokeCurves(index))
            if (item.SegmentIndex == segment) { curve = item.Curve; return true; }
        curve = default;
        return false;
    }

    private bool SetLinkedStrokeCurve(int index, int segment, CubicBoundarySegment curve)
    {
        if (segment < 0 && ShapeKind[index] == VectorAnimationEngine.ShapeKind.Line)
        {
            SetLineCurve(index, curve.Start, curve.Control1, curve.Control2, curve.End);
            return true;
        }
        if (TryGetFreehandBezierWorldNodes(index, out var nodes) && (uint)segment < nodes.Length - 1)
        {
            nodes[segment] = nodes[segment] with { Anchor = curve.Start, OutgoingControl = curve.Control1 };
            nodes[segment + 1] = nodes[segment + 1] with { Anchor = curve.End, IncomingControl = curve.Control2 };
            return SetFreehandBezierNodesCore(index, nodes);
        }
        return ShapeKind[index] == VectorAnimationEngine.ShapeKind.Path
            && SetPathBezierSegment(index, segment, curve.Start, curve.Control1, curve.Control2, curve.End,
                rebuildGeometryIndex: false);
    }

    public FillBoundaryLineLink[] CaptureFillBoundaryLineLinks(int lineObjectIndex, int frame)
    {
        if ((uint)lineObjectIndex >= ObjectCount
            || !IsLinkedBoundaryStroke(lineObjectIndex)
            || FillBoundaryLinkDetached[lineObjectIndex]
            || !IsObjectActive(lineObjectIndex, frame))
        {
            return Array.Empty<FillBoundaryLineLink>();
        }

        var linePoints = StrokeSamples(lineObjectIndex)
            .Select(sample => VectorUnits.Quantize(sample.Point))
            .ToArray();
        if (linePoints.Length < 2) return Array.Empty<FillBoundaryLineLink>();
        var reverseLinePoints = linePoints.Reverse().ToArray();

        var links = new List<FillBoundaryLineLink>();
        var queryBounds = GetObjectWorldBounds(lineObjectIndex);
        queryBounds.Inflate(ConnectedStrokeEndpointToleranceUnits, ConnectedStrokeEndpointToleranceUnits);
        foreach (var fillObjectIndex in QueryObjects(queryBounds, frame))
        {
            if (fillObjectIndex == lineObjectIndex || !CanLinkFillBoundaryLayers(lineObjectIndex, fillObjectIndex)
                || ObjectKeyframeFrame[fillObjectIndex] != ObjectKeyframeFrame[lineObjectIndex]
                || !HasFill(fillObjectIndex))
            {
                continue;
            }

            var contours = ShapeBoundaryContours(fillObjectIndex);
            if (!TryGetEditableFillBezierContours(fillObjectIndex, out var originalBezierContours)) continue;
            PointF[][]? originalContours = null;
            var captured = false;
            var exactStrokeCurves = ShapeKind[lineObjectIndex] == VectorAnimationEngine.ShapeKind.Line
                || GetSvgStrokeStyle(lineObjectIndex) is not null
                    ? LinkedStrokeCurves(lineObjectIndex) : [];
            foreach (var (strokeSegment, curve) in exactStrokeCurves)
            {
                if (!TryFindLinkedFillBezierSegment(originalBezierContours, curve,
                    out var exactContourIndex, out var exactBezierSegmentIndex,
                    out var exactSampledSegmentIndex, out var exactSampledSegmentCount, out var exactReversed,
                    out var boundaryStartT, out var boundaryEndT, out var exactBezierSegmentCount)) continue;
                originalContours ??= CloneContours(contours);
                if (exactBezierSegmentCount > 1)
                {
                    // The stroke spans a run of consecutive boundary segments
                    // (e.g. after a fill-edge Bezier edit re-segmented the
                    // shared edge). Store a non-exact link so updates replace
                    // the whole run with the stroke curve.
                    links.Add(new FillBoundaryLineLink(lineObjectIndex, fillObjectIndex, originalContours,
                        CloneBezierContours(originalBezierContours), exactContourIndex,
                        exactSampledSegmentIndex, exactSampledSegmentCount, exactBezierSegmentIndex,
                        exactBezierSegmentCount, exactReversed, strokeSegment));
                    captured = true;
                    continue;
                }
                links.Add(new FillBoundaryLineLink(lineObjectIndex, fillObjectIndex, originalContours,
                    CloneBezierContours(originalBezierContours), exactContourIndex,
                    exactSampledSegmentIndex, exactSampledSegmentCount, exactBezierSegmentIndex, 1,
                    exactReversed, strokeSegment, curve, boundaryStartT, boundaryEndT));
                captured = true;
            }
            // Imported curves must never fall back to sampled endpoint matching:
            // a short or partially split stroke can otherwise replace an unrelated
            // complete fill edge, including when the pointer has not moved.
            if (captured || GetSvgStrokeStyle(lineObjectIndex) is not null) continue;

            var exactMatches = new List<(int ContourIndex, int SegmentIndex, bool Reversed)>();
            for (var contourIndex = 0; contourIndex < contours.Length; contourIndex++)
            {
                var contour = contours[contourIndex];
                for (var segmentIndex = 0; segmentIndex < contour.Length - 1; segmentIndex++)
                {
                    var forward = BoundarySequenceMatches(contour, segmentIndex, linePoints);
                    var reverse = !forward
                        && BoundarySequenceMatches(contour, segmentIndex, reverseLinePoints);
                    if (!forward && !reverse) continue;
                    exactMatches.Add((contourIndex, segmentIndex, reverse));
                }
            }

            if (exactMatches.Count > 0)
            {
                originalContours = CloneContours(contours);
                foreach (var match in exactMatches)
                {
                    var (bezierSegmentIndex, bezierSegmentCount) = ResolveFillBoundaryBezierSpan(
                        originalBezierContours,
                        contours,
                        match.ContourIndex,
                        match.SegmentIndex,
                        linePoints.Length - 1,
                        lineObjectIndex,
                        match.Reversed);
                    links.Add(new FillBoundaryLineLink(
                        lineObjectIndex,
                        fillObjectIndex,
                        originalContours,
                        CloneBezierContours(originalBezierContours),
                        match.ContourIndex,
                        match.SegmentIndex,
                        linePoints.Length - 1,
                        bezierSegmentIndex,
                        bezierSegmentCount,
                        match.Reversed));
                }
                continue;
            }

            var lineStart = linePoints[0];
            var lineEnd = linePoints[^1];
            if (Distance(lineStart, lineEnd) <= ConnectedStrokeEndpointToleranceUnits) continue;
            for (var contourIndex = 0; contourIndex < contours.Length; contourIndex++)
            {
                var contour = contours[contourIndex];
                for (var segmentIndex = 0; segmentIndex < contour.Length - 1; segmentIndex++)
                {
                    var forward = BoundaryEndpointsMatch(
                        contour[segmentIndex],
                        contour[segmentIndex + 1],
                        lineStart,
                        lineEnd);
                    var reverse = !forward && BoundaryEndpointsMatch(
                        contour[segmentIndex],
                        contour[segmentIndex + 1],
                        lineEnd,
                        lineStart);
                    if (!forward && !reverse) continue;

                    originalContours ??= CloneContours(contours);
                    var (bezierSegmentIndex, bezierSegmentCount) = ResolveFillBoundaryBezierSpan(
                        originalBezierContours,
                        contours,
                        contourIndex,
                        segmentIndex,
                        1,
                        lineObjectIndex,
                        reverse);
                    links.Add(new FillBoundaryLineLink(
                        lineObjectIndex,
                        fillObjectIndex,
                        originalContours,
                        CloneBezierContours(originalBezierContours),
                        contourIndex,
                        segmentIndex,
                        SegmentCount: 1,
                        BezierSegmentIndex: bezierSegmentIndex,
                        BezierSegmentCount: bezierSegmentCount,
                        Reversed: reverse));
                }
            }
        }

        return links.ToArray();
    }

    public bool CanonicalizeFlattenedFillBoundaryCurves(
        int fillObjectIndex,
        int frame,
        bool rebuildGeometryIndex = true)
    {
        if ((uint)fillObjectIndex >= ObjectCount
            || !HasFill(fillObjectIndex)
            || !IsObjectActive(fillObjectIndex, frame))
        {
            return false;
        }

        var changed = false;
        if (TryCanonicalizeFillBoundaryFromNeighboringStrokes(
                fillObjectIndex,
                frame,
                out var canonicalContours)
            && SetPathBezierContoursCore(fillObjectIndex, canonicalContours))
        {
            changed = true;
        }

        var bounds = GetObjectWorldBounds(fillObjectIndex);
        bounds.Inflate(ConnectedStrokeEndpointToleranceUnits, ConnectedStrokeEndpointToleranceUnits);
        var links = QueryObjects(bounds, frame)
            .Where(candidate => candidate != fillObjectIndex
                && ObjectLayer[candidate] == ObjectLayer[fillObjectIndex]
                && ObjectKeyframeFrame[candidate] == ObjectKeyframeFrame[fillObjectIndex]
                && ShapeKind[candidate] == VectorAnimationEngine.ShapeKind.Line
                && HasStroke(candidate))
            .SelectMany(candidate => CaptureFillBoundaryLineLinks(candidate, frame))
            .Where(link => link.FillObjectIndex == fillObjectIndex
                && IsFlattenedFillBoundaryCurveLink(link))
            .GroupBy(link => (link.ContourIndex, link.BezierSegmentIndex, link.BezierSegmentCount))
            .Select(group => group.First())
            .ToArray();
        if (links.Length > 0
            && UpdateFillBoundaryLineLinks(links, rebuildGeometryIndex: false))
        {
            changed = true;
        }

        if (changed && rebuildGeometryIndex)
        {
            RebuildGeometryIndex();
            RebuildSummaries();
        }

        return changed;
    }

    private bool TryCanonicalizeFillBoundaryFromNeighboringStrokes(
        int fillObjectIndex,
        int frame,
        out PathBezierNode[][] contours)
    {
        contours = [];
        if (!TryGetEditableFillBezierContours(fillObjectIndex, out var currentContours)) return false;
        var sampledContours = ShapeBoundaryContours(fillObjectIndex);
        if (sampledContours.Length == 0) return false;

        var sources = new List<PreparedBezierCurve>();
        var bounds = GetObjectWorldBounds(fillObjectIndex);
        bounds.Inflate(ConnectedStrokeEndpointToleranceUnits, ConnectedStrokeEndpointToleranceUnits);
        foreach (var candidate in QueryObjects(bounds, frame))
        {
            if (candidate == fillObjectIndex
                || ObjectLayer[candidate] != ObjectLayer[fillObjectIndex]
                || ObjectKeyframeFrame[candidate] != ObjectKeyframeFrame[fillObjectIndex]
                || !HasStroke(candidate))
            {
                continue;
            }

            if (ShapeKind[candidate] == VectorAnimationEngine.ShapeKind.Line)
            {
                AddPreparedBezierCurve(sources, LineCurve(candidate));
            }
            else if (ShapeKind[candidate] == VectorAnimationEngine.ShapeKind.Freeform
                     && TryGetFreehandWorldPoints(candidate, out var freehandPoints))
            {
                foreach (var curve in FitPolylineBezierSegments(
                             freehandPoints,
                             ConnectedStrokeEndpointToleranceUnits))
                {
                    AddPreparedBezierCurve(sources, curve);
                }
            }
            else if (TryGetEditableFillBezierContours(candidate, out var boundaryContours))
            {
                foreach (var segment in BuildPathBezierSegmentParts(boundaryContours))
                {
                    AddPreparedBezierCurve(sources, segment.Curve);
                }
            }
        }

        if (sources.Count == 0) return false;
        foreach (var contour in currentContours)
        {
            foreach (var segment in BuildPathBezierSegmentParts([contour]))
            {
                var linearControl1 = Lerp(segment.Start, segment.End, 1f / 3f);
                var linearControl2 = Lerp(segment.Start, segment.End, 2f / 3f);
                if (Math.Max(
                        Distance(segment.Control1, linearControl1),
                        Distance(segment.Control2, linearControl2))
                    <= ConnectedStrokeEndpointToleranceUnits)
                {
                    continue;
                }

                AddPreparedBezierCurve(sources, segment.Curve);
            }
        }

        var rebuilt = new PathBezierNode[sampledContours.Length][];
        for (var contourIndex = 0; contourIndex < sampledContours.Length; contourIndex++)
        {
            if (!TryRebuildBezierContourFromBooleanBoundary(
                    sampledContours[contourIndex],
                    sources,
                    out rebuilt[contourIndex]))
            {
                return false;
            }
        }

        var contoursMatch = BooleanBezierContoursMatch(sampledContours, rebuilt);
        var rebuiltSegmentCount = rebuilt.Sum(contour => contour.Length);
        var currentSegmentCount = currentContours.Sum(contour => contour.Length);
        if (!contoursMatch || rebuiltSegmentCount >= currentSegmentCount)
        {
            return false;
        }

        contours = rebuilt;
        return true;
    }

    private bool IsFlattenedFillBoundaryCurveLink(FillBoundaryLineLink link)
    {
        if (link.BezierSegmentCount <= 1
            || (uint)link.LineObjectIndex >= ObjectCount
            || ShapeKind[link.LineObjectIndex] != VectorAnimationEngine.ShapeKind.Line
            || (uint)link.ContourIndex >= link.OriginalBezierContours.Length)
        {
            return false;
        }

        var line = LineCurve(link.LineObjectIndex);
        if (IsStraightBezierSegment(line.Start, line.Control1, line.Control2, line.End)) return false;

        var contour = link.OriginalBezierContours[link.ContourIndex];
        if (contour.Length < 3
            || link.BezierSegmentIndex < 0
            || link.BezierSegmentCount >= contour.Length)
        {
            return false;
        }

        for (var offset = 0; offset < link.BezierSegmentCount; offset++)
        {
            var segmentIndex = (link.BezierSegmentIndex + offset) % contour.Length;
            var nextIndex = (segmentIndex + 1) % contour.Length;
            if (!IsStraightBezierSegment(
                    contour[segmentIndex].Anchor,
                    contour[segmentIndex].OutgoingControl,
                    contour[nextIndex].IncomingControl,
                    contour[nextIndex].Anchor))
            {
                return false;
            }
        }

        return true;
    }

    private FillBoundaryLineLink[] CaptureLinkedFillBoundariesForTransform(IReadOnlyCollection<int> targets)
    {
        if (targets.Count == 0) return Array.Empty<FillBoundaryLineLink>();

        var transformed = targets.ToHashSet();
        var links = new List<FillBoundaryLineLink>();
        foreach (var lineObjectIndex in targets)
        {
            if (!IsFillBoundaryLinkedStrokeShape(ShapeKind[lineObjectIndex])) continue;
            foreach (var link in CaptureFillBoundaryLineLinks(lineObjectIndex, EditFrame))
            {
                // A selected fill has already received the same affine transform.
                // Replacing it from a pre-transform contour would undo that transform.
                if (!transformed.Contains(link.FillObjectIndex)) links.Add(link);
            }
        }

        return links.ToArray();
    }

    public bool UpdateFillBoundaryLineLinks(
        IReadOnlyList<FillBoundaryLineLink> links,
        bool rebuildGeometryIndex = true)
    {
        if (links.Count == 0) return false;

        var changed = false;
        foreach (var fillLinks in links
                     .GroupBy(link => link.FillObjectIndex)
                     .OrderBy(group => group.Key))
        {
            var fillObjectIndex = fillLinks.Key;
            if ((uint)fillObjectIndex >= ObjectCount
                || !IsFillShape(ShapeKind[fillObjectIndex]))
            {
                continue;
            }

            var linksForFill = fillLinks.ToArray();
            var exactLinks = linksForFill.All(link => link.OriginalStrokeCurve.HasValue);
            if (exactLinks && linksForFill.All(link =>
                TryGetLinkedStrokeCurve(link.LineObjectIndex, link.StrokeSegmentIndex, out var curve)
                && curve == link.OriginalStrokeCurve!.Value))
            {
                var baseline = linksForFill[0].OriginalBezierContours;
                if (TryGetEditableFillBezierContours(fillObjectIndex, out var current)
                    && current.Length == baseline.Length
                    && current.Zip(baseline).All(pair => pair.First.SequenceEqual(pair.Second))) continue;
                changed |= SetPathBezierContoursCore(fillObjectIndex, baseline);
                continue;
            }
            if (TryBuildLinkedFillBezierContours(linksForFill, out var bezierContours)
                && SetPathBezierContoursCore(fillObjectIndex, bezierContours))
            {
                changed = true;
                continue;
            }

            if (exactLinks) continue; // Reject ambiguous overlaps without destructive sampled fallback.
            var template = linksForFill[0];
            var contours = CloneContours(template.OriginalContours);
            var fillChanged = false;
            foreach (var contourLinks in linksForFill
                         .GroupBy(link => link.ContourIndex)
                         .OrderBy(group => group.Key))
            {
                var contourIndex = contourLinks.Key;
                if (contourIndex < 0 || contourIndex >= contours.Length) continue;

                var contour = contours[contourIndex];
                foreach (var link in contourLinks
                             .OrderByDescending(link => link.SegmentIndex)
                             .ThenBy(link => link.LineObjectIndex))
                {
                    if ((uint)link.LineObjectIndex >= ObjectCount
                        || !IsFillBoundaryLinkedStrokeShape(ShapeKind[link.LineObjectIndex])
                        || link.SegmentIndex < 0
                        || link.SegmentCount <= 0
                        || link.SegmentIndex + link.SegmentCount >= contour.Length)
                    {
                        continue;
                    }

                    var linePoints = StrokeSamples(link.LineObjectIndex)
                        .Select(sample => VectorUnits.Quantize(sample.Point))
                        .ToArray();
                    if (linePoints.Length < 2) continue;

                    var replacement = link.Reversed ? linePoints.Reverse().ToArray() : linePoints;
                    var updatedContour = ReplaceBoundarySegment(
                        contour,
                        link.SegmentIndex,
                        link.SegmentCount,
                        replacement);
                    if (updatedContour.Length < 3) continue;
                    contour = updatedContour;
                    fillChanged = true;
                }

                contours[contourIndex] = contour;
            }

            if (!fillChanged) continue;
            ShapeKind[fillObjectIndex] = VectorAnimationEngine.ShapeKind.Path;
            ShapeVertexCounts[fillObjectIndex] = 0;
            SetPathContours(fillObjectIndex, contours);
            changed = true;
        }

        if (changed && rebuildGeometryIndex)
        {
            RebuildGeometryIndex();
            RebuildSummaries();
        }
        else if (changed)
        {
            GeometryRevision++;
        }

        return changed;
    }

    public FillBoundaryStrokeLink[] CaptureFillBoundaryStrokeLinks(
        int fillObjectIndex,
        int partIndex,
        int frame)
    {
        if ((uint)fillObjectIndex >= ObjectCount
            || !IsObjectActive(fillObjectIndex, frame)
            || !TryGetPathBezierSegment(fillObjectIndex, partIndex, out var fillSegment))
        {
            return Array.Empty<FillBoundaryStrokeLink>();
        }

        var bounds = CubicCurveBounds(
            fillSegment.Start,
            fillSegment.Control1,
            fillSegment.Control2,
            fillSegment.End);
        bounds.Inflate(ConnectedStrokeEndpointToleranceUnits, ConnectedStrokeEndpointToleranceUnits);
        var result = new List<FillBoundaryStrokeLink>();
        foreach (var candidate in QueryObjects(bounds, frame))
        {
            if (candidate == fillObjectIndex || !CanLinkFillBoundaryLayers(candidate, fillObjectIndex)
                || ObjectKeyframeFrame[candidate] != ObjectKeyframeFrame[fillObjectIndex]
                || !IsLinkedBoundaryStroke(candidate) || FillBoundaryLinkDetached[candidate] || !HasStroke(candidate)) continue;
            foreach (var (segmentIndex, curve) in LinkedStrokeCurves(candidate))
            {
                if (!CubicCurvesCoincide(fillSegment.Start, fillSegment.Control1, fillSegment.Control2, fillSegment.End,
                    curve.Start, curve.Control1, curve.Control2, curve.End, out var reversed)) continue;
                result.Add(new FillBoundaryStrokeLink(candidate, reversed, segmentIndex));
            }
        }

        return result.ToArray();
    }

    public bool UpdateFillBoundaryStrokeLinks(
        IReadOnlyList<FillBoundaryStrokeLink> links,
        PointF start,
        PointF control1,
        PointF control2,
        PointF end,
        bool rebuildGeometryIndex = true)
    {
        var changed = false;
        foreach (var link in links.OrderBy(link => link.LineObjectIndex))
        {
            if ((uint)link.LineObjectIndex >= ObjectCount
                || !IsLinkedBoundaryStroke(link.LineObjectIndex))
            {
                continue;
            }

            var lineStart = link.Reversed ? end : start;
            var lineControl1 = link.Reversed ? control2 : control1;
            var lineControl2 = link.Reversed ? control1 : control2;
            var lineEnd = link.Reversed ? start : end;
            if (TryGetLinkedStrokeCurve(link.LineObjectIndex, link.StrokeSegmentIndex, out var current)
                && CubicControlPointsMatch(current.Start, current.Control1, current.Control2, current.End,
                    lineStart, lineControl1, lineControl2, lineEnd)) continue;
            if (!SetLinkedStrokeCurve(link.LineObjectIndex, link.StrokeSegmentIndex,
                new CubicBoundarySegment(lineStart, lineControl1, lineControl2, lineEnd))) continue;
            changed = true;
        }

        if (!changed) return false;
        if (rebuildGeometryIndex)
        {
            RebuildGeometryIndex();
            RebuildSummaries();
        }
        else
        {
            GeometryRevision++;
        }

        return true;
    }

    public SharedBoundaryIntersection CaptureStrokeIntersectionsAtFillAnchor(
        int fillObjectIndex,
        int fillPartIndex,
        bool startEndpoint,
        int frame,
        bool rebuildGeometryIndex = true,
        IReadOnlySet<int>? excludedLineObjectIndices = null)
    {
        if ((uint)fillObjectIndex >= ObjectCount
            || !IsObjectActive(fillObjectIndex, frame)
            || !TryGetPathBezierSegment(fillObjectIndex, fillPartIndex, out var fillSegment))
        {
            return EmptySharedBoundaryIntersection();
        }

        var anchor = startEndpoint ? fillSegment.Start : fillSegment.End;
        var bounds = new RectangleF(
            anchor.X - ConnectedStrokeEndpointToleranceUnits,
            anchor.Y - ConnectedStrokeEndpointToleranceUnits,
            ConnectedStrokeEndpointToleranceUnits * 2,
            ConnectedStrokeEndpointToleranceUnits * 2);
        var pathAnchors = new List<PathIntersectionAnchor>();
        var lineAnchors = new List<LineIntersectionAnchor>();
        var freeformAnchors = new List<FreeformIntersectionAnchor>();
        var capturedPathAnchors = new HashSet<(int ObjectIndex, int PartIndex, bool StartEndpoint)>();
        var capturedLineAnchors = new HashSet<(int ObjectIndex, bool StartEndpoint)>();
        var capturedFreeformAnchors = new HashSet<(int ObjectIndex, int PointIndex)>();
        var geometryChanged = false;
        var ownerObjectIndex = fillObjectIndex;
        var ownerPartIndex = fillPartIndex;

        var ownerParts = GetEditableFillBezierSegmentParts(ownerObjectIndex);
        var ownerNodePartIndex = fillPartIndex;
        if (!startEndpoint)
        {
            var ownerContourLength = ownerParts.Count(part => part.ContourIndex == fillSegment.ContourIndex);
            var nextSegmentIndex = (fillSegment.SegmentIndex + 1) % ownerContourLength;
            ownerNodePartIndex = ownerParts.First(part =>
                part.ContourIndex == fillSegment.ContourIndex
                && part.SegmentIndex == nextSegmentIndex).PartIndex;
        }

        geometryChanged |= CapturePathIntersectionAnchors(
            ownerObjectIndex,
            anchor,
            pathAnchors,
            capturedPathAnchors,
            excludedNodePartIndex: ownerNodePartIndex,
            trackedPartIndex: ownerPartIndex,
            out ownerPartIndex);

        foreach (var candidate in QueryObjects(bounds, frame))
        {
            if (candidate == ownerObjectIndex
                || ObjectLayer[candidate] != ObjectLayer[ownerObjectIndex]
                || ObjectKeyframeFrame[candidate] != ObjectKeyframeFrame[ownerObjectIndex])
            {
                continue;
            }

            var siblingFill = HasFill(candidate)
                && ObjectOrder[candidate] == ObjectOrder[ownerObjectIndex];
            if (IsFillShape(ShapeKind[candidate])
                && (HasStroke(candidate) || siblingFill))
            {
                geometryChanged |= CapturePathIntersectionAnchors(
                    candidate,
                    anchor,
                    pathAnchors,
                    capturedPathAnchors);
                continue;
            }

            if (!HasStroke(candidate)) continue;

            if (ShapeKind[candidate] == VectorAnimationEngine.ShapeKind.Line)
            {
                if (excludedLineObjectIndices?.Contains(candidate) == true
                    || FillBoundaryLinkDetached[candidate]
                    || !TryGetLineCubic(candidate, out var lineStart, out _, out _, out var lineEnd))
                {
                    continue;
                }

                var touchesStart = Distance(anchor, lineStart) <= ConnectedStrokeEndpointToleranceUnits;
                var touchesEnd = Distance(anchor, lineEnd) <= ConnectedStrokeEndpointToleranceUnits;
                if (touchesStart || touchesEnd)
                {
                    if (touchesStart) AddLineIntersectionAnchor(candidate, startEndpoint: true);
                    if (touchesEnd) AddLineIntersectionAnchor(candidate, startEndpoint: false);
                    continue;
                }

                if (!TryGetClosestPointOnLine(candidate, anchor, out var parameter, out _, out var distance)
                    || distance > ConnectedStrokeEndpointToleranceUnits
                    || parameter <= 0.001f
                    || parameter >= 0.999f)
                {
                    continue;
                }

                if (SplitLineAt(
                    candidate,
                    parameter,
                    out var split,
                    rebuildGeometryIndex: false))
                {
                    AddLineIntersectionAnchor(split.FirstObjectIndex, startEndpoint: false);
                    AddLineIntersectionAnchor(split.SecondObjectIndex, startEndpoint: true);
                    geometryChanged = true;
                }

                continue;
            }

            if (ShapeKind[candidate] != VectorAnimationEngine.ShapeKind.Freeform
                || !TryMaterializeFreeformIntersectionAnchor(candidate, anchor, out var freeformAnchor, out var inserted))
            {
                continue;
            }

            if (capturedFreeformAnchors.Add((freeformAnchor.ObjectIndex, freeformAnchor.PointIndex)))
            {
                freeformAnchors.Add(freeformAnchor);
            }
            geometryChanged |= inserted;
        }

        if (geometryChanged)
        {
            CompleteSharedBoundaryIntersectionMutation(rebuildGeometryIndex);
        }

        return new SharedBoundaryIntersection(
            anchor,
            pathAnchors.ToArray(),
            lineAnchors.ToArray(),
            freeformAnchors.ToArray(),
            ownerObjectIndex,
            ownerPartIndex);

        void AddLineIntersectionAnchor(int objectIndex, bool startEndpoint)
        {
            if (!capturedLineAnchors.Add((objectIndex, startEndpoint))) return;
            lineAnchors.Add(CaptureLineIntersectionAnchor(objectIndex, startEndpoint));
        }
    }

    public SharedBoundaryIntersection CaptureFillIntersectionsAtLineEndpoint(
        int lineObjectIndex,
        bool startEndpoint,
        int frame,
        bool rebuildGeometryIndex = true,
        IReadOnlySet<int>? excludedFillObjectIndices = null)
    {
        if ((uint)lineObjectIndex >= ObjectCount
            || ShapeKind[lineObjectIndex] != VectorAnimationEngine.ShapeKind.Line
            || FillBoundaryLinkDetached[lineObjectIndex]
            || !IsObjectActive(lineObjectIndex, frame)
            || !TryGetLineEndpoint(lineObjectIndex, startEndpoint, out var anchor))
        {
            return EmptySharedBoundaryIntersection();
        }

        var bounds = new RectangleF(
            anchor.X - ConnectedStrokeEndpointToleranceUnits,
            anchor.Y - ConnectedStrokeEndpointToleranceUnits,
            ConnectedStrokeEndpointToleranceUnits * 2,
            ConnectedStrokeEndpointToleranceUnits * 2);
        var pathAnchors = new List<PathIntersectionAnchor>();
        var lineAnchors = new List<LineIntersectionAnchor>();
        var capturedPathAnchors = new HashSet<(int ObjectIndex, int PartIndex, bool StartEndpoint)>();
        var capturedLineAnchors = new HashSet<(int ObjectIndex, bool StartEndpoint)>();
        var geometryChanged = false;
        foreach (var candidate in QueryObjects(bounds, frame))
        {
            if (candidate == lineObjectIndex
                || ObjectLayer[candidate] != ObjectLayer[lineObjectIndex]
                || ObjectKeyframeFrame[candidate] != ObjectKeyframeFrame[lineObjectIndex])
            {
                continue;
            }

            if (HasFill(candidate))
            {
                if (excludedFillObjectIndices?.Contains(candidate) == true) continue;
                geometryChanged |= CapturePathIntersectionAnchors(
                    candidate,
                    anchor,
                    pathAnchors,
                    capturedPathAnchors);
                continue;
            }

            if (ShapeKind[candidate] != VectorAnimationEngine.ShapeKind.Line) continue;
            CaptureConnectedLineEndpoint(candidate, candidateStartsAtAnchor: true);
            CaptureConnectedLineEndpoint(candidate, candidateStartsAtAnchor: false);
        }

        if (geometryChanged)
        {
            CompleteSharedBoundaryIntersectionMutation(rebuildGeometryIndex);
        }

        return new SharedBoundaryIntersection(
            anchor,
            pathAnchors.ToArray(),
            lineAnchors.ToArray(),
            []);

        void CaptureConnectedLineEndpoint(int objectIndex, bool candidateStartsAtAnchor)
        {
            if (!TryGetLineEndpoint(objectIndex, candidateStartsAtAnchor, out var endpoint)
                || Distance(endpoint, anchor) > ConnectedStrokeEndpointToleranceUnits
                || !capturedLineAnchors.Add((objectIndex, candidateStartsAtAnchor)))
            {
                return;
            }

            lineAnchors.Add(CaptureLineIntersectionAnchor(objectIndex, candidateStartsAtAnchor));
        }
    }

    public bool UpdateSharedBoundaryIntersection(
        SharedBoundaryIntersection intersection,
        PointF anchor,
        bool rebuildGeometryIndex = true)
    {
        if (!intersection.HasTargets) return false;
        anchor = VectorUnits.Quantize(anchor);
        var changed = UpdateCapturedPathAnchors(intersection.PathAnchors, anchor);
        foreach (var line in intersection.LineAnchors)
        {
            if ((uint)line.ObjectIndex >= ObjectCount
                || ShapeKind[line.ObjectIndex] != VectorAnimationEngine.ShapeKind.Line)
            {
                continue;
            }

            var dx = anchor.X - line.OriginalEndpoint.X;
            var dy = anchor.Y - line.OriginalEndpoint.Y;
            var control1 = line.StartEndpoint
                ? new PointF(line.Control1.X + dx, line.Control1.Y + dy)
                : line.Control1;
            var control2 = line.StartEndpoint
                ? line.Control2
                : new PointF(line.Control2.X + dx, line.Control2.Y + dy);
            SetLineEndpoint(
                line.ObjectIndex,
                line.StartEndpoint,
                anchor,
                line.OppositeEndpoint,
                control1,
                control2,
                line.KeepStraight);
            changed = true;
        }

        foreach (var freeform in intersection.FreeformAnchors)
        {
            if ((uint)freeform.ObjectIndex >= ObjectCount
                || ShapeKind[freeform.ObjectIndex] != VectorAnimationEngine.ShapeKind.Freeform
                || (uint)freeform.PointIndex >= freeform.OriginalPoints.Length)
            {
                continue;
            }

            var points = (PointF[])freeform.OriginalPoints.Clone();
            points[freeform.PointIndex] = anchor;
            if (freeform.MirrorsClosedEndpoint)
            {
                points[0] = anchor;
                points[^1] = anchor;
            }

            SetFreehandPoints(freeform.ObjectIndex, points);
            changed = true;
        }

        if (changed) CompleteSharedBoundaryIntersectionMutation(rebuildGeometryIndex);
        return changed;
    }

    private bool UpdateCapturedPathAnchors(
        IReadOnlyList<PathIntersectionAnchor> pathAnchors,
        PointF anchor)
    {
        var changed = false;
        foreach (var path in pathAnchors)
        {
            var segment = path.OriginalSegment;
            var start = path.StartEndpoint ? anchor : segment.Start;
            var end = path.StartEndpoint ? segment.End : anchor;
            var control1 = segment.Control1;
            var control2 = segment.Control2;
            if (IsStraightBezierSegment(
                    segment.Start,
                    segment.Control1,
                    segment.Control2,
                    segment.End))
            {
                control1 = Lerp(start, end, 1f / 3f);
                control2 = Lerp(start, end, 2f / 3f);
            }

            changed |= SetPathBezierSegment(
                path.ObjectIndex,
                path.PartIndex,
                start,
                control1,
                control2,
                end,
                rebuildGeometryIndex: false,
                preserveStraightAdjacentSegments: true);
        }

        return changed;
    }

    private static SharedBoundaryIntersection EmptySharedBoundaryIntersection()
    {
        return new SharedBoundaryIntersection(PointF.Empty, [], [], []);
    }

    private bool CapturePathIntersectionAnchors(
        int objectIndex,
        PointF anchor,
        ICollection<PathIntersectionAnchor> pathAnchors,
        ISet<(int ObjectIndex, int PartIndex, bool StartEndpoint)> capturedPathAnchors)
    {
        return CapturePathIntersectionAnchors(
            objectIndex,
            anchor,
            pathAnchors,
            capturedPathAnchors,
            excludedNodePartIndex: -1,
            trackedPartIndex: -1,
            out _);
    }

    private bool CapturePathIntersectionAnchors(
        int objectIndex,
        PointF anchor,
        ICollection<PathIntersectionAnchor> pathAnchors,
        ISet<(int ObjectIndex, int PartIndex, bool StartEndpoint)> capturedPathAnchors,
        int excludedNodePartIndex,
        int trackedPartIndex,
        out int remappedTrackedPartIndex)
    {
        remappedTrackedPartIndex = trackedPartIndex;
        if ((uint)objectIndex >= ObjectCount || !IsFillShape(ShapeKind[objectIndex])) return false;
        var parts = GetEditableFillBezierSegmentParts(objectIndex);
        if (parts.Length == 0) return false;

        var partIndices = parts.ToDictionary(
            part => (part.ContourIndex, part.SegmentIndex),
            part => part.PartIndex);
        var contourLengths = parts
            .GroupBy(part => part.ContourIndex)
            .ToDictionary(group => group.Key, group => group.Count());
        var existingNodes = new HashSet<(int ContourIndex, int NodeIndex)>();
        var intersections = new List<(int PartIndex, float Parameter)>();
        foreach (var part in parts)
        {
            if (!TryGetClosestPointOnBezierSegment(part, anchor, out var parameter, out _, out var distance)
                || distance > ConnectedStrokeEndpointToleranceUnits)
            {
                continue;
            }

            var startDistance = Distance(anchor, part.Start);
            var endDistance = Distance(anchor, part.End);
            if (Math.Min(startDistance, endDistance) > ConnectedStrokeEndpointToleranceUnits)
            {
                intersections.Add((part.PartIndex, parameter));
                continue;
            }

            var contourLength = contourLengths[part.ContourIndex];
            var nodeIndex = startDistance <= endDistance
                ? part.SegmentIndex
                : (part.SegmentIndex + 1) % contourLength;
            var nodePartIndex = partIndices[(part.ContourIndex, nodeIndex)];
            if (nodePartIndex == excludedNodePartIndex) continue;
            if (!existingNodes.Add((part.ContourIndex, nodeIndex))) continue;
            intersections.Add((nodePartIndex, 0));
        }

        if (intersections.Count == 0) return false;
        var alreadyBezierPath = ShapeKind[objectIndex] == VectorAnimationEngine.ShapeKind.Path
            && _pathBezierLocalContours.ContainsKey(objectIndex);
        if (!TryConvertFillToBezierPath(objectIndex, rebuildGeometryIndex: false)) return false;

        var geometryChanged = !alreadyBezierPath;
        var insertionOffset = 0;
        foreach (var intersection in intersections
                     .OrderBy(intersection => intersection.PartIndex)
                     .ThenBy(intersection => intersection.Parameter))
        {
            var partIndex = intersection.PartIndex + insertionOffset;
            if (intersection.Parameter > 0.0001f)
            {
                if (!TryInsertPathBezierAnchor(
                        objectIndex,
                        partIndex,
                        intersection.Parameter,
                        out partIndex,
                        out _,
                        rebuildGeometryIndex: false))
                {
                    continue;
                }

                if (partIndex <= remappedTrackedPartIndex)
                {
                    remappedTrackedPartIndex++;
                }
                insertionOffset++;
                geometryChanged = true;
            }

            if (!TryGetPathBezierSegment(objectIndex, partIndex, out var materializedSegment))
            {
                continue;
            }

            if (Distance(materializedSegment.Start, anchor) > DrawingTopologyRules.UnitIntersectionTolerance)
            {
                var control1 = materializedSegment.Control1;
                var control2 = materializedSegment.Control2;
                if (IsStraightBezierSegment(
                        materializedSegment.Start,
                        control1,
                        control2,
                        materializedSegment.End))
                {
                    control1 = Lerp(anchor, materializedSegment.End, 1f / 3f);
                    control2 = Lerp(anchor, materializedSegment.End, 2f / 3f);
                }

                if (!SetPathBezierSegment(
                        objectIndex,
                        partIndex,
                        anchor,
                        control1,
                        control2,
                        materializedSegment.End,
                        rebuildGeometryIndex: false,
                        preserveStraightAdjacentSegments: true)
                    || !TryGetPathBezierSegment(objectIndex, partIndex, out materializedSegment))
                {
                    continue;
                }

                geometryChanged = true;
            }

            if (!capturedPathAnchors.Add((objectIndex, partIndex, true))) continue;

            pathAnchors.Add(new PathIntersectionAnchor(
                objectIndex,
                partIndex,
                StartEndpoint: true,
                materializedSegment));
        }

        return geometryChanged;
    }

    private LineIntersectionAnchor CaptureLineIntersectionAnchor(int objectIndex, bool startEndpoint)
    {
        TryGetLineCubic(objectIndex, out var start, out var control1, out var control2, out var end);
        return new LineIntersectionAnchor(
            objectIndex,
            startEndpoint,
            startEndpoint ? start : end,
            startEndpoint ? end : start,
            control1,
            control2,
            IsLineStraight(objectIndex));
    }

    private bool TryMaterializeFreeformIntersectionAnchor(
        int objectIndex,
        PointF anchor,
        out FreeformIntersectionAnchor result,
        out bool inserted)
    {
        result = null!;
        inserted = false;
        if ((uint)objectIndex >= ObjectCount
            || ShapeKind[objectIndex] != VectorAnimationEngine.ShapeKind.Freeform
            || !TryGetFreehandWorldPoints(objectIndex, out var originalPoints)
            || originalPoints.Length < 2)
        {
            return false;
        }

        var pointIndex = -1;
        var bestDistance = ConnectedStrokeEndpointToleranceUnits;
        for (var index = 0; index < originalPoints.Length; index++)
        {
            var distance = Distance(anchor, originalPoints[index]);
            if (distance > bestDistance) continue;
            pointIndex = index;
            bestDistance = distance;
        }

        var segmentIndex = -1;
        var segmentParameter = 0f;
        for (var index = 0; index < originalPoints.Length - 1; index++)
        {
            var start = originalPoints[index];
            var end = originalPoints[index + 1];
            var dx = end.X - start.X;
            var dy = end.Y - start.Y;
            var lengthSquared = dx * dx + dy * dy;
            var parameter = lengthSquared <= DrawingTopologyRules.UnitIntersectionTolerance
                ? 0f
                : Math.Clamp(((anchor.X - start.X) * dx + (anchor.Y - start.Y) * dy) / lengthSquared, 0f, 1f);
            var distance = Distance(anchor, Lerp(start, end, parameter));
            if (distance >= bestDistance - DrawingTopologyRules.UnitIntersectionTolerance) continue;
            segmentIndex = index;
            segmentParameter = parameter;
            bestDistance = distance;
        }

        var points = originalPoints;
        if (segmentIndex >= 0)
        {
            if (segmentParameter <= 0.0001f) pointIndex = segmentIndex;
            else if (segmentParameter >= 0.9999f) pointIndex = segmentIndex + 1;
            else
            {
                pointIndex = segmentIndex + 1;
                points = new PointF[originalPoints.Length + 1];
                Array.Copy(originalPoints, 0, points, 0, pointIndex);
                points[pointIndex] = VectorUnits.Quantize(anchor);
                Array.Copy(
                    originalPoints,
                    pointIndex,
                    points,
                    pointIndex + 1,
                    originalPoints.Length - pointIndex);
                SetFreehandPoints(objectIndex, points);
                inserted = true;
            }
        }

        if (pointIndex < 0) return false;

        var closed = points.Length > 2 && points[0] == points[^1];
        var mirrorsClosedEndpoint = closed && (pointIndex == 0 || pointIndex == points.Length - 1);
        result = new FreeformIntersectionAnchor(
            objectIndex,
            pointIndex,
            mirrorsClosedEndpoint,
            (PointF[])points.Clone());
        return true;
    }

    private static bool TryGetClosestPointOnBezierSegment(
        PathBezierSegmentPart segment,
        PointF world,
        out float parameter,
        out PointF point,
        out float distance)
    {
        parameter = 0;
        point = PointF.Empty;
        distance = float.MaxValue;
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
            var localParameter = lengthSquared <= DrawingTopologyRules.UnitIntersectionTolerance
                ? 0f
                : Math.Clamp(((world.X - a.Point.X) * dx + (world.Y - a.Point.Y) * dy) / lengthSquared, 0f, 1f);
            var candidateParameter = a.T + (b.T - a.T) * localParameter;
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
            var firstPoint = CubicPoint(segment.Start, segment.Control1, segment.Control2, segment.End, first);
            var secondPoint = CubicPoint(segment.Start, segment.Control1, segment.Control2, segment.End, second);
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

    private void CompleteSharedBoundaryIntersectionMutation(bool rebuildGeometryIndex)
    {
        if (rebuildGeometryIndex)
        {
            RebuildGeometryIndex();
            RebuildSummaries();
        }
        else
        {
            GeometryRevision++;
        }
    }

    private const float FillBezierRunParameterTolerance = 0.01f;

    private static bool TryFindLinkedFillBezierSegment(
        IReadOnlyList<PathBezierNode[]> contours, CubicBoundarySegment stroke,
        out int contourIndex, out int segmentIndex, out int sampleIndex, out int sampleCount,
        out bool reversed, out float startT, out float endT, out int bezierSegmentCount)
    {
        startT = 0;
        endT = 1;
        if (TryFindCoincidentFillBezierSegment(contours, stroke.Start, stroke.Control1, stroke.Control2, stroke.End,
            out contourIndex, out segmentIndex, out sampleIndex, out sampleCount, out reversed))
        {
            bezierSegmentCount = 1;
            return true;
        }

        var preparedStroke = PrepareBezierCurve(stroke);
        for (var ci = 0; ci < contours.Count; ci++)
        {
            var contour = contours[ci];
            var nodeCount = contour.Length;
            var curves = new CubicBoundarySegment[nodeCount];
            var sampleCounts = new int[nodeCount];
            var sampleOffsets = new int[nodeCount];
            var totalSamples = 0;
            for (var si = 0; si < nodeCount; si++)
            {
                var current = contour[si];
                var next = contour[(si + 1) % nodeCount];
                curves[si] = new CubicBoundarySegment(
                    current.Anchor,
                    current.OutgoingControl,
                    next.IncomingControl,
                    next.Anchor);
                sampleCounts[si] = Math.Max(1, SampleCubicSegment(curves[si]).Length - 1);
                sampleOffsets[si] = totalSamples;
                totalSamples += sampleCounts[si];
            }

            for (var si = 0; si < nodeCount; si++)
            {
                if (!TryGetBezierSubcurveInterval(PrepareBezierCurve(curves[si]), stroke, out startT, out endT)) continue;
                var part = CubicSubcurve(curves[si].Start, curves[si].Control1, curves[si].Control2, curves[si].End, startT, endT);
                reversed = Distance(stroke.Start, part.End) < Distance(stroke.Start, part.Start);
                contourIndex = ci;
                segmentIndex = si;
                sampleIndex = sampleOffsets[si];
                sampleCount = sampleCounts[si];
                bezierSegmentCount = 1;
                return true;
            }

            // A stroke may span several consecutive boundary segments after a
            // fill-edge Bezier edit re-segmented the shared edge with extra
            // anchors. Match a whole-segment run that tiles the stroke from
            // end to end so the link survives the re-segmentation.
            if (nodeCount >= 2
                && TryFindLinkedFillBezierSegmentRun(preparedStroke, contour, curves, sampleCounts, sampleOffsets,
                    out var runSegmentIndex, out var runLength, out var runSampleIndex,
                    out var runSampleCount, out var runReversed))
            {
                contourIndex = ci;
                segmentIndex = runSegmentIndex;
                sampleIndex = runSampleIndex;
                sampleCount = runSampleCount;
                reversed = runReversed;
                startT = 0;
                endT = 1;
                bezierSegmentCount = runLength;
                return true;
            }
        }

        contourIndex = -1;
        segmentIndex = -1;
        sampleIndex = -1;
        sampleCount = 0;
        reversed = false;
        bezierSegmentCount = 0;
        return false;
    }

    private static bool TryFindLinkedFillBezierSegmentRun(
        PreparedBezierCurve stroke,
        IReadOnlyList<PathBezierNode> contour,
        CubicBoundarySegment[] curves,
        int[] sampleCounts,
        int[] sampleOffsets,
        out int segmentIndex,
        out int segmentRunLength,
        out int sampleIndex,
        out int sampleCount,
        out bool reversed)
    {
        var nodeCount = contour.Count;
        segmentIndex = -1;
        segmentRunLength = 0;
        sampleIndex = -1;
        sampleCount = 0;
        reversed = false;
        for (var si = 0; si < nodeCount - 1; si++)
        {
            if (!TryGetBezierSubcurveInterval(stroke, curves[si], out var from, out var to)) continue;
            bool forward;
            if (from <= FillBezierRunParameterTolerance) forward = true;
            else if (to >= 1f - FillBezierRunParameterTolerance) forward = false;
            else continue;

            var cursor = forward ? to : from;
            var runLength = 1;
            var complete = forward
                ? to >= 1f - FillBezierRunParameterTolerance
                : from <= FillBezierRunParameterTolerance;
            while (!complete && si + runLength < nodeCount)
            {
                if (!TryGetBezierSubcurveInterval(stroke, curves[si + runLength], out var nextFrom, out var nextTo)
                    || nextTo <= nextFrom)
                {
                    break;
                }

                if (forward)
                {
                    if (Math.Abs(nextFrom - cursor) > FillBezierRunParameterTolerance) break;
                    cursor = nextTo;
                }
                else
                {
                    if (Math.Abs(nextTo - cursor) > FillBezierRunParameterTolerance) break;
                    cursor = nextFrom;
                }

                runLength++;
                complete = forward
                    ? cursor >= 1f - FillBezierRunParameterTolerance
                    : cursor <= FillBezierRunParameterTolerance;
            }

            if (!complete) continue;
            segmentIndex = si;
            segmentRunLength = runLength;
            sampleIndex = sampleOffsets[si];
            sampleCount = 0;
            for (var offset = 0; offset < runLength; offset++) sampleCount += sampleCounts[si + offset];
            var startAnchor = contour[si].Anchor;
            var endAnchor = contour[(si + runLength) % nodeCount].Anchor;
            reversed = Distance(stroke.Curve.Start, endAnchor) < Distance(stroke.Curve.Start, startAnchor);
            return true;
        }

        return false;
    }

    private bool TryBuildExactLinkedFillBezierContours(
        IReadOnlyList<FillBoundaryLineLink> links, out PathBezierNode[][] contours)
    {
        contours = CloneBezierContours(links[0].OriginalBezierContours);
        foreach (var group in links.GroupBy(link => (link.ContourIndex, link.BezierSegmentIndex))
                     .OrderByDescending(group => group.Key.ContourIndex)
                     .ThenByDescending(group => group.Key.BezierSegmentIndex))
        {
            var (ci, si) = group.Key;
            if ((uint)ci >= contours.Length || (uint)si >= contours[ci].Length) return false;
            var contour = contours[ci];
            var baselineContour = links[0].OriginalBezierContours[ci];
            var current = baselineContour[si];
            var next = baselineContour[(si + 1) % baselineContour.Length];
            var source = new CubicBoundarySegment(current.Anchor, current.OutgoingControl, next.IncomingControl, next.Anchor);
            var runs = new List<CubicBoundarySegment>();
            var cursor = 0f;
            foreach (var link in group.OrderBy(link => link.BoundaryStartT).ThenBy(link => link.LineObjectIndex))
            {
                if (!TryGetLinkedStrokeCurve(link.LineObjectIndex, link.StrokeSegmentIndex, out var edited)
                    || link.OriginalStrokeCurve is not { } original || link.BoundaryStartT < cursor - 0.0001f) return false;
                if (link.Reversed)
                {
                    edited = new(edited.End, edited.Control2, edited.Control1, edited.Start);
                    original = new(original.End, original.Control2, original.Control1, original.Start);
                }
                if (link.BoundaryStartT > cursor + 0.0001f)
                    runs.Add(CubicSubcurve(source.Start, source.Control1, source.Control2, source.End, cursor, link.BoundaryStartT));
                var baseline = CubicSubcurve(source.Start, source.Control1, source.Control2, source.End,
                    link.BoundaryStartT, link.BoundaryEndT);
                static PointF Move(PointF value, PointF before, PointF after) =>
                    new(value.X + after.X - before.X, value.Y + after.Y - before.Y);
                var movedStart = Move(baseline.Start, original.Start, edited.Start);
                if (runs.Count > 0 && runs[^1].End != movedStart)
                {
                    var previous = runs[^1];
                    runs[^1] = previous with { End = movedStart,
                        Control2 = new(previous.Control2.X + movedStart.X - previous.End.X,
                            previous.Control2.Y + movedStart.Y - previous.End.Y) };
                }
                runs.Add(new(movedStart,
                    Move(baseline.Control1, original.Control1, edited.Control1),
                    Move(baseline.Control2, original.Control2, edited.Control2),
                    Move(baseline.End, original.End, edited.End)));
                cursor = link.BoundaryEndT;
            }
            if (cursor < 1 - 0.0001f)
            {
                var tail = CubicSubcurve(source.Start, source.Control1, source.Control2, source.End, cursor, 1);
                var join = runs[^1].End;
                tail = tail with { Start = join,
                    Control1 = new(tail.Control1.X + join.X - tail.Start.X, tail.Control1.Y + join.Y - tail.Start.Y) };
                runs.Add(tail);
            }
            // Retain adjacent native boundaries, then insert only the necessary
            // subcurve anchors. Every preview starts from the pointer-down contour.
            contour = ReplaceBezierContourSegment(contour, si, 1,
                new(runs[0].Start, runs[0].Control1, runs[^1].Control2, runs[^1].End));
            if (contour.Length < 3) return false;
            var nodes = contour.ToList();
            for (var ri = runs.Count - 1; ri > 0; ri--)
                nodes.Insert(si + 1, new(runs[ri].Start, runs[ri - 1].Control2, runs[ri].Control1));
            contours[ci] = nodes.ToArray();
        }
        return true;
    }

    private static bool TryFindCoincidentFillBezierSegment(
        IReadOnlyList<PathBezierNode[]> contours,
        PointF lineStart,
        PointF lineControl1,
        PointF lineControl2,
        PointF lineEnd,
        out int contourIndex,
        out int bezierSegmentIndex,
        out int sampledSegmentIndex,
        out int sampledSegmentCount,
        out bool reversed)
    {
        contourIndex = -1;
        bezierSegmentIndex = -1;
        sampledSegmentIndex = -1;
        sampledSegmentCount = 0;
        reversed = false;
        for (var candidateContourIndex = 0; candidateContourIndex < contours.Count; candidateContourIndex++)
        {
            var contour = contours[candidateContourIndex];
            var sampleOffset = 0;
            for (var candidateSegmentIndex = 0; candidateSegmentIndex < contour.Length; candidateSegmentIndex++)
            {
                var current = contour[candidateSegmentIndex];
                var next = contour[(candidateSegmentIndex + 1) % contour.Length];
                var curve = new CubicBoundarySegment(
                    current.Anchor,
                    current.OutgoingControl,
                    next.IncomingControl,
                    next.Anchor);
                var sampleCount = Math.Max(1, SampleCubicSegment(curve).Length - 1);
                if (CubicCurvesCoincide(
                        curve.Start,
                        curve.Control1,
                        curve.Control2,
                        curve.End,
                        lineStart,
                        lineControl1,
                        lineControl2,
                        lineEnd,
                        out reversed))
                {
                    contourIndex = candidateContourIndex;
                    bezierSegmentIndex = candidateSegmentIndex;
                    sampledSegmentIndex = sampleOffset;
                    sampledSegmentCount = sampleCount;
                    return true;
                }

                sampleOffset += sampleCount;
            }
        }

        return false;
    }

    private (int SegmentIndex, int SegmentCount) ResolveFillBoundaryBezierSpan(
        IReadOnlyList<PathBezierNode[]> bezierContours,
        IReadOnlyList<PointF[]> sampledContours,
        int contourIndex,
        int sampledSegmentIndex,
        int sampledSegmentCount,
        int lineObjectIndex,
        bool reversed)
    {
        if ((uint)contourIndex >= bezierContours.Count
            || (uint)contourIndex >= sampledContours.Count
            || !TryGetLineCubic(lineObjectIndex, out var start, out _, out _, out var end))
        {
            return (-1, 0);
        }

        var contour = bezierContours[contourIndex];
        var fillStart = reversed ? end : start;
        var fillEnd = reversed ? start : end;
        for (var segmentIndex = 0; segmentIndex < contour.Length; segmentIndex++)
        {
            if (BoundaryEndpointsMatch(
                    contour[segmentIndex].Anchor,
                    contour[(segmentIndex + 1) % contour.Length].Anchor,
                    fillStart,
                    fillEnd))
            {
                return (segmentIndex, 1);
            }
        }

        var sampledContour = sampledContours[contourIndex];
        if (sampledSegmentIndex >= 0
            && sampledSegmentCount > 0
            && sampledContour.Length > 0)
        {
            // Map the matched sampled span onto whole Bezier segments by
            // accumulating per-segment sample counts. Re-segmented contours no
            // longer map 1:1 onto sampled segments, so require the span to
            // align with segment boundaries and reject partial overlaps.
            var totalSamples = 0;
            for (var si = 0; si < contour.Length; si++)
            {
                var current = contour[si];
                var next = contour[(si + 1) % contour.Length];
                totalSamples += Math.Max(1, SampleCubicSegment(new CubicBoundarySegment(
                    current.Anchor,
                    current.OutgoingControl,
                    next.IncomingControl,
                    next.Anchor)).Length - 1);
            }

            if (sampledContour.Length - 1 == totalSamples)
            {
                var target = sampledSegmentIndex + sampledSegmentCount;
                var offset = 0;
                var startSegment = -1;
                for (var si = 0; si < contour.Length; si++)
                {
                    if (offset == sampledSegmentIndex) startSegment = si;
                    var current = contour[si];
                    var next = contour[(si + 1) % contour.Length];
                    offset += Math.Max(1, SampleCubicSegment(new CubicBoundarySegment(
                        current.Anchor,
                        current.OutgoingControl,
                        next.IncomingControl,
                        next.Anchor)).Length - 1);
                    if (startSegment >= 0)
                    {
                        if (offset == target) return (startSegment, si - startSegment + 1);
                        if (offset > target) break;
                    }
                }
            }
        }

        return (-1, 0);
    }

    private bool TryBuildLinkedFillBezierContours(
        IReadOnlyList<FillBoundaryLineLink> links,
        out PathBezierNode[][] contours)
    {
        contours = Array.Empty<PathBezierNode[]>();
        if (links.Count == 0
            || links.Any(link => (uint)link.LineObjectIndex >= ObjectCount
                || !TryGetLinkedStrokeCurve(link.LineObjectIndex, link.StrokeSegmentIndex, out _)
                || link.BezierSegmentIndex < 0
                || link.BezierSegmentCount <= 0))
        {
            return false;
        }

        if (links.All(link => link.OriginalStrokeCurve.HasValue))
            return TryBuildExactLinkedFillBezierContours(links, out contours);
        contours = CloneBezierContours(links[0].OriginalBezierContours);
        foreach (var contourLinks in links
                     .GroupBy(link => link.ContourIndex)
                     .OrderBy(group => group.Key))
        {
            var contourIndex = contourLinks.Key;
            if ((uint)contourIndex >= contours.Length) return false;
            var contour = contours[contourIndex];
            foreach (var link in contourLinks
                         .OrderByDescending(link => link.BezierSegmentIndex)
                         .ThenBy(link => link.LineObjectIndex))
            {
                if (!TryGetLinkedStrokeCurve(link.LineObjectIndex, link.StrokeSegmentIndex, out var line)) return false;
                var replacement = link.Reversed
                    ? new CubicBoundarySegment(line.End, line.Control2, line.Control1, line.Start)
                    : new CubicBoundarySegment(line.Start, line.Control1, line.Control2, line.End);
                contour = ReplaceBezierContourSegment(
                    contour,
                    link.BezierSegmentIndex,
                    link.BezierSegmentCount,
                    replacement);
                if (contour.Length < 3) return false;
            }

            contours[contourIndex] = contour;
        }

        return contours.Length > 0;
    }

    private static PathBezierNode[] ReplaceBezierContourSegment(
        IReadOnlyList<PathBezierNode> contour,
        int segmentIndex,
        int segmentCount,
        CubicBoundarySegment replacement)
    {
        if (contour.Count < 3
            || segmentIndex < 0
            || segmentIndex >= contour.Count
            || segmentCount <= 0
            || segmentCount > contour.Count)
        {
            return Array.Empty<PathBezierNode>();
        }

        var endIndex = (segmentIndex + segmentCount) % contour.Count;
        var removed = new HashSet<int>();
        for (var offset = 1; offset < segmentCount; offset++)
        {
            removed.Add((segmentIndex + offset) % contour.Count);
        }
        if (contour.Count - removed.Count < 3) return Array.Empty<PathBezierNode>();

        var previousIndex = (segmentIndex - 1 + contour.Count) % contour.Count;
        var followingIndex = (endIndex + 1) % contour.Count;
        var startNode = contour[segmentIndex];
        var endNode = contour[endIndex];
        var preservePrevious = replacement.Start != startNode.Anchor
            && IsStraightBezierSegment(
                contour[previousIndex].Anchor,
                contour[previousIndex].OutgoingControl,
                startNode.IncomingControl,
                startNode.Anchor);
        var preserveFollowing = replacement.End != endNode.Anchor
            && IsStraightBezierSegment(
                endNode.Anchor,
                endNode.OutgoingControl,
                contour[followingIndex].IncomingControl,
                contour[followingIndex].Anchor);
        var updated = new PathBezierNode[contour.Count];
        for (var index = 0; index < contour.Count; index++) updated[index] = contour[index];
        updated[segmentIndex] = startNode with
        {
            Anchor = VectorUnits.Quantize(replacement.Start),
            OutgoingControl = VectorUnits.Quantize(replacement.Control1)
        };
        updated[endIndex] = endNode with
        {
            Anchor = VectorUnits.Quantize(replacement.End),
            IncomingControl = VectorUnits.Quantize(replacement.Control2)
        };
        if (preservePrevious)
        {
            var previousAnchor = updated[previousIndex].Anchor;
            var replacementStart = updated[segmentIndex].Anchor;
            updated[previousIndex] = updated[previousIndex] with
            {
                OutgoingControl = VectorUnits.Quantize(Lerp(previousAnchor, replacementStart, 1f / 3f))
            };
            updated[segmentIndex] = updated[segmentIndex] with
            {
                IncomingControl = VectorUnits.Quantize(Lerp(previousAnchor, replacementStart, 2f / 3f))
            };
        }

        if (preserveFollowing)
        {
            var replacementEnd = updated[endIndex].Anchor;
            var followingAnchor = updated[followingIndex].Anchor;
            updated[endIndex] = updated[endIndex] with
            {
                OutgoingControl = VectorUnits.Quantize(Lerp(replacementEnd, followingAnchor, 1f / 3f))
            };
            updated[followingIndex] = updated[followingIndex] with
            {
                IncomingControl = VectorUnits.Quantize(Lerp(replacementEnd, followingAnchor, 2f / 3f))
            };
        }

        return updated.Where((_, index) => !removed.Contains(index)).ToArray();
    }

    private static bool CubicCurvesCoincide(
        PointF firstStart,
        PointF firstControl1,
        PointF firstControl2,
        PointF firstEnd,
        PointF secondStart,
        PointF secondControl1,
        PointF secondControl2,
        PointF secondEnd,
        out bool reversed)
    {
        reversed = false;
        if (CubicControlPointsMatch(
                firstStart,
                firstControl1,
                firstControl2,
                firstEnd,
                secondStart,
                secondControl1,
                secondControl2,
                secondEnd,
                ConnectedStrokeEndpointToleranceUnits))
        {
            return true;
        }
        if (CubicControlPointsMatch(
                firstStart,
                firstControl1,
                firstControl2,
                firstEnd,
                secondEnd,
                secondControl2,
                secondControl1,
                secondStart,
                ConnectedStrokeEndpointToleranceUnits))
        {
            reversed = true;
            return true;
        }

        var firstSamples = SampleCubicSegment(new CubicBoundarySegment(
            firstStart,
            firstControl1,
            firstControl2,
            firstEnd));
        var secondSamples = SampleCubicSegment(new CubicBoundarySegment(
            secondStart,
            secondControl1,
            secondControl2,
            secondEnd));
        if (firstSamples.Length != secondSamples.Length) return false;
        var forward = true;
        var reverse = true;
        for (var index = 0; index < firstSamples.Length; index++)
        {
            forward &= SameDrawingUnit(firstSamples[index], secondSamples[index]);
            reverse &= SameDrawingUnit(firstSamples[index], secondSamples[^(index + 1)]);
            if (!forward && !reverse) return false;
        }

        reversed = reverse;
        return true;
    }

    private static bool CubicControlPointsMatch(
        PointF firstStart,
        PointF firstControl1,
        PointF firstControl2,
        PointF firstEnd,
        PointF secondStart,
        PointF secondControl1,
        PointF secondControl2,
        PointF secondEnd,
        float tolerance = DrawingTopologyRules.UnitIntersectionTolerance)
    {
        return Distance(firstStart, secondStart) <= tolerance
            && Distance(firstControl1, secondControl1) <= tolerance
            && Distance(firstControl2, secondControl2) <= tolerance
            && Distance(firstEnd, secondEnd) <= tolerance;
    }

    public MaterializeSelectedPartsResult MaterializeLineIntersections(
        IReadOnlyCollection<int> seedObjects,
        int frame)
    {
        var seeds = seedObjects
            .Where(index => (uint)index < ObjectCount
                && (ShapeKind[index] == VectorAnimationEngine.ShapeKind.Line
                    || ShapeKind[index] == VectorAnimationEngine.ShapeKind.Freeform)
                && HasStroke(index)
                && IsObjectActive(index, frame))
            .Distinct()
            .ToArray();
        if (seeds.Length == 0)
        {
            return new MaterializeSelectedPartsResult(
                true,
                false,
                Array.Empty<MaterializedPartMapping>(),
                Array.Empty<int>());
        }

        var impacted = new HashSet<int>();
        foreach (var seed in seeds)
        {
            var strokeCandidates = CollectTopologyCandidates(seed, frame)
                .Where(candidate => candidate != seed
                    && (ShapeKind[candidate] == VectorAnimationEngine.ShapeKind.Line
                        || ShapeKind[candidate] == VectorAnimationEngine.ShapeKind.Freeform)
                    && HasStroke(candidate)
                    && ObjectLayer[candidate] == ObjectLayer[seed])
                .ToArray();
            if (StrokeSplitParameters(seed, strokeCandidates).Count > 2) impacted.Add(seed);
            foreach (var candidate in strokeCandidates)
            {
                if (StrokeSplitParameters(candidate, [seed]).Count > 2) impacted.Add(candidate);
            }
        }

        var keys = new List<DrawingElementKey>();
        foreach (var source in impacted.OrderBy(index => index))
        {
            var strokeCandidates = CollectTopologyCandidates(source, frame)
                .Where(candidate => candidate != source
                    && (ShapeKind[candidate] == VectorAnimationEngine.ShapeKind.Line
                        || ShapeKind[candidate] == VectorAnimationEngine.ShapeKind.Freeform)
                    && HasStroke(candidate))
                .ToArray();
            var splits = StrokeSplitParameters(source, strokeCandidates);
            for (var part = 0; part < splits.Count - 1; part++)
            {
                keys.Add(new DrawingElementKey(source, DrawingElementKind.Stroke, part));
            }
        }

        if (keys.Count == 0)
        {
            return new MaterializeSelectedPartsResult(
                true,
                false,
                Array.Empty<MaterializedPartMapping>(),
                Array.Empty<int>());
        }

        return MaterializeSelectedParts(keys, frame, strokeIntersectionsOnly: true);
    }

}
