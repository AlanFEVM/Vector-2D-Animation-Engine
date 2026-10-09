using System.Diagnostics;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunLinkedFillBoundaryRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        var outlinedFill = scene.AddObject(
            0,
            PointF.Empty,
            new SizeF(200, 100),
            0,
            VectorUnits.StrokePointsToUnits(2),
            Color.Teal,
            Color.White,
            12,
            ShapeKind.Rectangle);

        var boundary = scene.HitTestElement(new PointF(0, -50), 0, toleranceWorld: 2);
        var detached = scene.DetachElementForMove(boundary, 0);
        if (!boundary.IsValid
            || boundary.Key.Kind != DrawingElementKind.BoundaryStroke
            || !detached.IsValid
            || detached.Key.Kind != DrawingElementKind.Stroke
            || scene.ShapeKind[detached.Key.ObjectIndex] != ShapeKind.Line)
        {
            throw new InvalidOperationException("An outlined fill boundary could not be detached into an editable line.");
        }

        var links = scene.CaptureFillBoundaryLineLinks(detached.Key.ObjectIndex, 0);
        var fillObjectIndex = links.Length == 1 ? links[0].FillObjectIndex : -1;
        var expandedPoint = new PointF(0, -80);
        if (links.Length != 1
            || fillObjectIndex != outlinedFill
            || scene.FillContainsPoint(fillObjectIndex, expandedPoint))
        {
            throw new InvalidOperationException("A detached fill boundary did not retain its linked fill contour.");
        }

        scene.SetLineEndpoint(
            detached.Key.ObjectIndex,
            startEndpoint: true,
            endpoint: new PointF(-100, -50),
            oppositeEndpoint: new PointF(100, -50),
            control: new PointF(0, -140),
            keepStraight: false);
        if (!scene.UpdateFillBoundaryLineLinks(links)
            || scene.ShapeKind[fillObjectIndex] != ShapeKind.Path
            || !scene.FillContainsPoint(fillObjectIndex, expandedPoint))
        {
            throw new InvalidOperationException("Editing a linked fill boundary did not expand the fill with the line curve.");
        }

        var refreshedLinks = scene.CaptureFillBoundaryLineLinks(detached.Key.ObjectIndex, 0);
        var movedStart = new PointF(-130, -70);
        scene.SetLineEndpoint(
            detached.Key.ObjectIndex,
            startEndpoint: true,
            endpoint: movedStart,
            oppositeEndpoint: new PointF(100, -50),
            control: new PointF(0, -140),
            keepStraight: false);
        var boundaryMatchesMovedEndpoint = refreshedLinks.Length == 1
            && scene.UpdateFillBoundaryLineLinks(refreshedLinks)
            && scene.GetObjectBoundaryContours(fillObjectIndex)
                .SelectMany(contour => contour)
                .Any(point => MathF.Abs(point.X - movedStart.X) <= DrawingTopologyRules.UnitIntersectionTolerance
                    && MathF.Abs(point.Y - movedStart.Y) <= DrawingTopologyRules.UnitIntersectionTolerance);
        if (!boundaryMatchesMovedEndpoint)
        {
            throw new InvalidOperationException("Editing a linked fill-boundary endpoint left the fill contour at the old endpoint.");
        }

        scene.TryGetLineEndpoint(detached.Key.ObjectIndex, startEndpoint: true, out var transformStart);
        var translatedStart = new PointF(transformStart.X + 40, transformStart.Y - 20);
        scene.TransformObjects(
            [detached.Key.ObjectIndex],
            point => new PointF(point.X + 40, point.Y - 20));
        var transformKeepsFillLinked = scene.GetObjectBoundaryContours(fillObjectIndex)
            .SelectMany(contour => contour)
            .Any(point => MathF.Abs(point.X - translatedStart.X) <= DrawingTopologyRules.UnitIntersectionTolerance
                && MathF.Abs(point.Y - translatedStart.Y) <= DrawingTopologyRules.UnitIntersectionTolerance);
        if (!transformKeepsFillLinked)
        {
            throw new InvalidOperationException("Free Transform moved a fill boundary line without moving the linked fill contour.");
        }

        var shearLinks = scene.CaptureFillBoundaryLineLinks(detached.Key.ObjectIndex, 0);
        scene.ShearObjects(
            [detached.Key.ObjectIndex],
            point => new PointF(point.X + point.Y * 0.2f, point.Y));
        scene.TryGetLineEndpoint(detached.Key.ObjectIndex, startEndpoint: true, out var shearedLineStart);
        var shearedStart = VectorUnits.Quantize(shearedLineStart);
        var shearKeepsFillLinked = shearLinks.Length == 1
            && scene.GetObjectBoundaryContours(fillObjectIndex)
            .SelectMany(contour => contour)
                .Any(point => MathF.Abs(point.X - shearedStart.X) <= DrawingTopologyRules.UnitIntersectionTolerance
                    && MathF.Abs(point.Y - shearedStart.Y) <= DrawingTopologyRules.UnitIntersectionTolerance);
        if (!shearKeepsFillLinked)
        {
            var closestBoundaryDistance = scene.GetObjectBoundaryContours(fillObjectIndex)
                .SelectMany(contour => contour)
                .Select(point => MathF.Sqrt(
                    (point.X - shearedStart.X) * (point.X - shearedStart.X)
                    + (point.Y - shearedStart.Y) * (point.Y - shearedStart.Y)))
                .DefaultIfEmpty(float.MaxValue)
                .Min();
            throw new InvalidOperationException(
                $"Free Transform skewed a fill boundary line without moving the linked fill contour: links={shearLinks.Length}, lineStart={shearedStart}, closestBoundary={closestBoundaryDistance:0.###}.");
        }

        var closedBoundaryScene = new VectorScene();
        closedBoundaryScene.CreateEmpty();
        var closedFill = closedBoundaryScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(180, 120),
            0,
            0,
            Color.Teal,
            Color.Transparent,
            12,
            ShapeKind.Rectangle);
        var closedBoundary = closedBoundaryScene.AddFreehandStroke(
            0,
            closedBoundaryScene.GetShapeBoundary(closedFill),
            VectorUnits.StrokePointsToUnits(2),
            Color.CornflowerBlue,
            brushStroke: false,
            12);
        var closedLinks = closedBoundaryScene.CaptureFillBoundaryLineLinks(closedBoundary, 0);
        closedBoundaryScene.ShearObjects(
            [closedBoundary],
            point => new PointF(point.X + point.Y * 0.35f, point.Y));
        var closedBoundaryMatchesFill = closedBoundaryScene.ShapeKind[closedFill] == ShapeKind.Path
            && closedLinks.Length == 1
            && closedBoundaryScene.TryGetFreehandWorldPoints(closedBoundary, out var closedPoints)
            && closedBoundaryScene.GetObjectBoundaryContours(closedFill)
                .SelectMany(contour => contour)
                .Any(point => MathF.Abs(point.X - VectorUnits.Quantize(closedPoints[0].X)) <= DrawingTopologyRules.UnitIntersectionTolerance
                    && MathF.Abs(point.Y - VectorUnits.Quantize(closedPoints[0].Y)) <= DrawingTopologyRules.UnitIntersectionTolerance);
        if (!closedBoundaryMatchesFill)
        {
            throw new InvalidOperationException("Free Transform skewed a closed fill boundary stroke without moving the linked fill contour.");
        }

        AssertMovingStraightBoundaryEndpointPreservesAdjacentFillEdge(startEndpoint: true);
        AssertMovingStraightBoundaryEndpointPreservesAdjacentFillEdge(startEndpoint: false);
        AssertCurvedLineExpandsStraightFillBoundary(reverse: false);
        AssertCurvedLineExpandsStraightFillBoundary(reverse: true);

        Console.WriteLine("fill_boundary_line_link_regression=ok");

        static void AssertMovingStraightBoundaryEndpointPreservesAdjacentFillEdge(bool startEndpoint)
        {
            var linkedScene = new VectorScene();
            linkedScene.CreateEmpty();
            var linkedFill = linkedScene.AddObject(
                0,
                PointF.Empty,
                new SizeF(200, 100),
                0,
                VectorUnits.StrokePointsToUnits(2),
                Color.Teal,
                Color.White,
                12,
                ShapeKind.Rectangle);
            var boundaryHit = linkedScene.HitTestElement(new PointF(0, 50), 0, toleranceWorld: 2);
            var detachedBoundary = linkedScene.DetachElementForMove(boundaryHit, 0);
            var boundaryLinks = detachedBoundary.IsValid
                ? linkedScene.CaptureFillBoundaryLineLinks(detachedBoundary.Key.ObjectIndex, 0)
                : Array.Empty<FillBoundaryLineLink>();
            if (boundaryLinks.Length != 1
                || boundaryLinks[0].FillObjectIndex != linkedFill
                || !linkedScene.TryGetLineCubic(
                    detachedBoundary.Key.ObjectIndex,
                    out var lineStart,
                    out var lineControl1,
                    out var lineControl2,
                    out var lineEnd))
            {
                throw new InvalidOperationException("A straight detached boundary could not establish its linked fill regression setup.");
            }

            var originalEndpoint = startEndpoint ? lineStart : lineEnd;
            var oppositeEndpoint = startEndpoint ? lineEnd : lineStart;
            var movedEndpoint = VectorUnits.Quantize(new PointF(
                originalEndpoint.X + (startEndpoint ? -70 : 85),
                originalEndpoint.Y - 65));
            linkedScene.SetLineEndpoint(
                detachedBoundary.Key.ObjectIndex,
                startEndpoint,
                movedEndpoint,
                oppositeEndpoint,
                lineControl1,
                lineControl2,
                keepStraight: true);
            if (!linkedScene.UpdateFillBoundaryLineLinks(boundaryLinks))
            {
                throw new InvalidOperationException("Moving a straight detached boundary endpoint did not update its linked fill.");
            }

            var linkedPartIndex = boundaryLinks[0].BezierSegmentIndex;
            var fillParts = linkedScene.GetPathBezierSegmentParts(linkedFill);
            var activeParts = fillParts.Where(part => part.PartIndex == linkedPartIndex).ToArray();
            var adjacentParts = fillParts
                .Where(part => part.PartIndex != linkedPartIndex
                    && (PointsNear(part.Start, movedEndpoint) || PointsNear(part.End, movedEndpoint)))
                .ToArray();
            if (activeParts.Length != 1
                || adjacentParts.Length != 1
                || !VectorScene.IsStraightBezierSegment(
                    activeParts[0].Start,
                    activeParts[0].Control1,
                    activeParts[0].Control2,
                    activeParts[0].End)
                || !VectorScene.IsStraightBezierSegment(
                    adjacentParts[0].Start,
                    adjacentParts[0].Control1,
                    adjacentParts[0].Control2,
                    adjacentParts[0].End))
            {
                throw new InvalidOperationException(
                    $"Moving a straight detached boundary {(startEndpoint ? "start" : "end")} bent the linked fill's adjacent edge.");
            }

            var adjacent = adjacentParts[0];
            var expectedControl1 = VectorUnits.Quantize(new PointF(
                adjacent.Start.X + (adjacent.End.X - adjacent.Start.X) / 3f,
                adjacent.Start.Y + (adjacent.End.Y - adjacent.Start.Y) / 3f));
            var expectedControl2 = VectorUnits.Quantize(new PointF(
                adjacent.Start.X + (adjacent.End.X - adjacent.Start.X) * 2f / 3f,
                adjacent.Start.Y + (adjacent.End.Y - adjacent.Start.Y) * 2f / 3f));
            if (!PointsNear(adjacent.Control1, expectedControl1)
                || !PointsNear(adjacent.Control2, expectedControl2))
            {
                throw new InvalidOperationException("The linked fill's straight adjacent edge did not receive linear cubic controls.");
            }
        }

        static void AssertCurvedLineExpandsStraightFillBoundary(bool reverse)
        {
            var curvedScene = new VectorScene();
            curvedScene.CreateEmpty(2);
            var curvedFill = curvedScene.AddObject(
                0,
                PointF.Empty,
                new SizeF(200, 100),
                0,
                0,
                Color.Teal,
                Color.Transparent,
                12,
                ShapeKind.Rectangle);
            var otherLayerFill = curvedScene.AddObject(
                1,
                PointF.Empty,
                new SizeF(200, 100),
                0,
                0,
                Color.Coral,
                Color.Transparent,
                12,
                ShapeKind.Rectangle);
            var left = new PointF(-100, -50);
            var right = new PointF(100, -50);
            var curvedLine = curvedScene.AddCubicCurveSegment(
                0,
                reverse ? right : left,
                reverse ? new PointF(60, -140) : new PointF(-60, -140),
                reverse ? new PointF(-60, -140) : new PointF(60, -140),
                reverse ? left : right,
                VectorUnits.StrokePointsToUnits(2),
                Color.Transparent,
                Color.White,
                12);
            var expandedPoint = new PointF(0, -80);
            var curvedLinks = curvedScene.CaptureFillBoundaryLineLinks(curvedLine, 0);
            if (curvedLinks.Length != 1
                || curvedLinks[0].FillObjectIndex != curvedFill
                || curvedLinks[0].SegmentCount != 1
                || curvedLinks[0].Reversed != reverse
                || curvedScene.FillContainsPoint(curvedFill, expandedPoint)
                || !curvedScene.UpdateFillBoundaryLineLinks(curvedLinks)
                || !curvedScene.FillContainsPoint(curvedFill, expandedPoint)
                || curvedScene.FillContainsPoint(otherLayerFill, expandedPoint)
                || curvedScene.ShapeKind[otherLayerFill] != ShapeKind.Rectangle
                || curvedScene.ObjectLayer[curvedFill] != 0
                || curvedScene.ObjectKeyframeFrame[curvedFill] != 0)
            {
                throw new InvalidOperationException(
                    $"A {(reverse ? "reversed " : string.Empty)}curved line did not expand its matching straight fill boundary.");
            }

            var preservedFillEdges = curvedScene.GetPathBezierSegmentParts(curvedFill)
                .Where(part => PointsNear(part.Start, left)
                    && PointsNear(part.End, right)
                    && PointsNear(part.Control1, new PointF(-60, -140))
                    && PointsNear(part.Control2, new PointF(60, -140)))
                .ToArray();
            if (!curvedScene.TryGetPathBezierWorldContours(curvedFill, out var exactFillContours)
                || exactFillContours.Length != 1
                || exactFillContours[0].Length != 4
                || preservedFillEdges.Length != 1)
            {
                throw new InvalidOperationException(
                    "Updating a linked fill boundary flattened its cubic edge into sampled line segments.");
            }

            var updatedPoints = curvedScene.GetObjectBoundaryContours(curvedFill).SelectMany(contour => contour).ToArray();
            if (new[]
                {
                    new PointF(-100, -50),
                    new PointF(100, -50),
                    new PointF(100, 50),
                    new PointF(-100, 50)
                }.Any(corner => !updatedPoints.Any(point => PointsNear(point, corner))))
            {
                throw new InvalidOperationException("Expanding a curved fill boundary lost an unchanged contour corner.");
            }

            var curveHit = curvedScene.HitTestElement(new PointF(0, -117.5f), 0, toleranceWorld: 2);
            var selectedPath = MainForm.TraditionalPenPathElements(curvedScene, curveHit, 0);
            if (!curveHit.IsValid
                || curveHit.Key.ObjectIndex != curvedLine
                || curveHit.Key.Kind != DrawingElementKind.Stroke
                || curveHit.StartT > DrawingTopologyRules.UnitIntersectionTolerance
                || curveHit.EndT < 1 - DrawingTopologyRules.UnitIntersectionTolerance
                || selectedPath.Length != 1
                || selectedPath[0].Key.ObjectIndex != curvedLine
                || selectedPath[0].StartT > DrawingTopologyRules.UnitIntersectionTolerance
                || selectedPath[0].EndT < 1 - DrawingTopologyRules.UnitIntersectionTolerance)
            {
                throw new InvalidOperationException(
                    $"A linked curved line was split by its own expanded fill boundary: hit={curveHit}, path={selectedPath.Length}.");
            }

            var refreshedLinks = curvedScene.CaptureFillBoundaryLineLinks(curvedLine, 0);
            if (refreshedLinks.Length != 1
                || refreshedLinks[0].SegmentCount <= 1
                || refreshedLinks[0].BezierSegmentCount != 1)
            {
                throw new InvalidOperationException("Exact curved-boundary matching did not take priority after the fill contour was expanded.");
            }


            var fillEdge = preservedFillEdges[0];
            var strokeLinks = curvedScene.CaptureFillBoundaryStrokeLinks(
                curvedFill,
                fillEdge.PartIndex,
                0);
            var updatedControl1 = new PointF(-85, -175);
            var updatedControl2 = new PointF(75, -105);
            var objectCountBeforeFillEdit = curvedScene.ObjectCount;
            var fillEditInitialContours = curvedScene.GetObjectBoundaryContours(curvedFill);
            if (strokeLinks.Length != 1
                || strokeLinks[0].LineObjectIndex != curvedLine
                || strokeLinks[0].Reversed != reverse
                || !curvedScene.SetPathBezierSegment(
                    curvedFill,
                    fillEdge.PartIndex,
                    fillEdge.Start,
                    updatedControl1,
                    updatedControl2,
                    fillEdge.End,
                    rebuildGeometryIndex: false)
                || !curvedScene.UpdateFillBoundaryStrokeLinks(
                    strokeLinks,
                    fillEdge.Start,
                    updatedControl1,
                    updatedControl2,
                    fillEdge.End,
                    rebuildGeometryIndex: false))
            {
                throw new InvalidOperationException(
                    "Editing a fill boundary did not update its coincident cubic line.");
            }

            curvedScene.NormalizeFillBoundaryOverlaps(
                curvedFill,
                fillEditInitialContours,
                rebuildGeometryIndex: false);
            if (!curvedScene.TryGetPathBezierSegment(curvedFill, fillEdge.PartIndex, out var finalFillEdge))
            {
                throw new InvalidOperationException("A normalized fill-boundary edit lost its active cubic segment.");
            }
            curvedScene.UpdateFillBoundaryStrokeLinks(
                strokeLinks,
                finalFillEdge.Start,
                finalFillEdge.Control1,
                finalFillEdge.Control2,
                finalFillEdge.End,
                rebuildGeometryIndex: false);
            curvedScene.CompleteDeferredBuild();

            var expectedLineStart = reverse ? finalFillEdge.End : finalFillEdge.Start;
            var expectedLineControl1 = reverse ? finalFillEdge.Control2 : finalFillEdge.Control1;
            var expectedLineControl2 = reverse ? finalFillEdge.Control1 : finalFillEdge.Control2;
            var expectedLineEnd = reverse ? finalFillEdge.Start : finalFillEdge.End;
            var updatedFillEdges = curvedScene.GetPathBezierSegmentParts(curvedFill);
            var updatedEdge = updatedFillEdges.Single(part => part.PartIndex == fillEdge.PartIndex);
            var updatedProbe = updatedEdge.Samples[updatedEdge.Samples.Length / 2];
            var updatedHit = curvedScene.HitTestElement(updatedProbe, 0, toleranceWorld: 2);
            if (curvedScene.ObjectCount != objectCountBeforeFillEdit
                || curvedScene.ShapeKind[curvedLine] != ShapeKind.Line
                || updatedFillEdges.Length != 4
                || !curvedScene.TryGetLineCubic(
                    curvedLine,
                    out var actualLineStart,
                    out var actualLineControl1,
                    out var actualLineControl2,
                    out var actualLineEnd)
                || !PointsNear(actualLineStart, expectedLineStart)
                || !PointsNear(actualLineControl1, expectedLineControl1)
                || !PointsNear(actualLineControl2, expectedLineControl2)
                || !PointsNear(actualLineEnd, expectedLineEnd)
                || updatedHit.Key.ObjectIndex != curvedLine
                || updatedHit.Key.Kind != DrawingElementKind.Stroke
                || updatedHit.StartT > DrawingTopologyRules.UnitIntersectionTolerance
                || updatedHit.EndT < 1 - DrawingTopologyRules.UnitIntersectionTolerance)
            {
                throw new InvalidOperationException(
                    $"A fill-boundary edit flattened or split its linked {(reverse ? "reversed " : string.Empty)}cubic line: hit={updatedHit}, objects={curvedScene.ObjectCount}.");
            }


            var endpointLinks = curvedScene.CaptureFillBoundaryLineLinks(curvedLine, 0);
            var excludedLinkedFills = endpointLinks
                .Select(link => link.FillObjectIndex)
                .ToHashSet();
            var endpointIntersection = curvedScene.CaptureFillIntersectionsAtLineEndpoint(
                curvedLine,
                startEndpoint: true,
                0,
                rebuildGeometryIndex: false,
                excludedFillObjectIndices: excludedLinkedFills);
            var movedLineStart = VectorUnits.Quantize(new PointF(actualLineStart.X - 25, actualLineStart.Y - 15));
            var movedLineControl1 = VectorUnits.Quantize(new PointF(actualLineControl1.X - 25, actualLineControl1.Y - 15));
            curvedScene.SetLineEndpoint(
                curvedLine,
                startEndpoint: true,
                movedLineStart,
                actualLineEnd,
                movedLineControl1,
                actualLineControl2,
                keepStraight: false);
            if (endpointLinks.Length != 1
                || endpointIntersection.PathAnchors.Any(anchor => anchor.ObjectIndex == curvedFill)
                || !curvedScene.UpdateFillBoundaryLineLinks(endpointLinks, rebuildGeometryIndex: false))
            {
                throw new InvalidOperationException(
                    "A curved Line endpoint edit did not reserve its coincident fill edge for full-curve synchronization.");
            }
            curvedScene.UpdateSharedBoundaryIntersection(
                endpointIntersection,
                movedLineStart,
                rebuildGeometryIndex: false);
            curvedScene.CompleteDeferredBuild();

            var endpointFillEdge = curvedScene.GetPathBezierSegmentParts(curvedFill)
                .Single(part => part.PartIndex == fillEdge.PartIndex);
            var endpointLink = endpointLinks[0];
            var expectedFillStart = endpointLink.Reversed ? actualLineEnd : movedLineStart;
            var expectedFillControl1 = endpointLink.Reversed ? actualLineControl2 : movedLineControl1;
            var expectedFillControl2 = endpointLink.Reversed ? movedLineControl1 : actualLineControl2;
            var expectedFillEnd = endpointLink.Reversed ? movedLineStart : actualLineEnd;
            if (!PointsNear(endpointFillEdge.Start, expectedFillStart)
                || !PointsNear(endpointFillEdge.Control1, expectedFillControl1)
                || !PointsNear(endpointFillEdge.Control2, expectedFillControl2)
                || !PointsNear(endpointFillEdge.End, expectedFillEnd))
            {
                throw new InvalidOperationException(
                    "Moving a curved Line endpoint allowed shared-anchor synchronization to overwrite its linked fill curve.");
            }

            var resetLine = MainForm.ResetBezierCurvature(movedLineStart, actualLineEnd);
            curvedScene.SetLineEndpoint(
                curvedLine,
                startEndpoint: true,
                resetLine.Start,
                resetLine.End,
                resetLine.Control1,
                resetLine.Control2,
                keepStraight: false);
            if (!curvedScene.UpdateFillBoundaryLineLinks(endpointLinks, rebuildGeometryIndex: false))
            {
                throw new InvalidOperationException("Resetting a Line's Bezier controls did not update its linked fill edge.");
            }
            curvedScene.CompleteDeferredBuild();

            var resetFillEdge = curvedScene.GetPathBezierSegmentParts(curvedFill)
                .Single(part => part.PartIndex == fillEdge.PartIndex);
            if (!curvedScene.TryGetLineCubic(
                    curvedLine,
                    out var resetLineStart,
                    out var resetLineControl1,
                    out var resetLineControl2,
                    out var resetLineEnd)
                || !VectorScene.IsStraightBezierSegment(
                    resetLineStart,
                    resetLineControl1,
                    resetLineControl2,
                    resetLineEnd)
                || !VectorScene.IsStraightBezierSegment(
                    resetFillEdge.Start,
                    resetFillEdge.Control1,
                    resetFillEdge.Control2,
                    resetFillEdge.End))
            {
                throw new InvalidOperationException(
                    "Alt-resetting both Bezier controls did not straighten the Line and linked fill edge together.");
            }
        }
    }

    private static void RunConnectedLineRecolorRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        var stroke = VectorUnits.StrokePointsToUnits(4);
        var first = scene.AddLineSegment(0, new PointF(0, 0), new PointF(100, 0), stroke, Color.Transparent, Color.Coral, 6);
        var second = scene.AddLineSegment(0, new PointF(100, 0), new PointF(200, 0), stroke, Color.Transparent, Color.Coral, 6);
        var third = scene.AddLineSegment(0, new PointF(200, 0), new PointF(300, 0), stroke, Color.Transparent, Color.Coral, 6);
        var unconnected = scene.AddLineSegment(0, new PointF(0, 100), new PointF(100, 100), stroke, Color.Transparent, Color.RoyalBlue, 6);
        scene.SetGradientPaint(
            first,
            GradientKind.Linear,
            [new GradientStop(0, Color.Coral), new GradientStop(1, Color.Gold)]);

        var hit = scene.HitTestElement(new PointF(40, 0), 0, toleranceWorld: 12);
        var targets = scene.GetConnectedStrokeElements(hit, 0)
            .Select(element => element.Key.ObjectIndex)
            .Where(index => scene.ShapeKind[index] == ShapeKind.Line)
            .Distinct()
            .ToArray();
        var fillColor = Color.Teal.ToArgb();
        foreach (var index in targets)
        {
            scene.StrokeArgb[index] = fillColor;
            scene.DisableLinearGradient(index);
        }

        var recolored = targets.OrderBy(index => index).SequenceEqual(new[] { first, second, third })
            && targets.All(index => scene.StrokeArgb[index] == fillColor && !scene.HasGradient(index))
            && scene.StrokeArgb[unconnected] == Color.RoyalBlue.ToArgb()
            && !scene.HasGradient(unconnected);
        if (!recolored)
        {
            throw new InvalidOperationException("Connected line recoloring did not limit the solid Fill color to the clicked stroke network.");
        }

        Console.WriteLine("connected_line_recolor_regression=ok");
    }

    private static void RunTransformGeometryRegression()
    {
        RunInstanceSkewPreviewRegression();
        var overlayFrame = new TransformOverlayFrame(
            new PointF(20, 30),
            new PointF(80, 60),
            new PointF(-30, 40));
        if (!overlayFrame.IsValid
            || !PointsNear(overlayFrame.TopRight, new PointF(100, 90))
            || !PointsNear(overlayFrame.BottomLeft, new PointF(-10, 70))
            || !PointsNear(overlayFrame.BottomRight, new PointF(70, 130))
            || !PointsNear(overlayFrame.Center, new PointF(45, 80))
            || overlayFrame.Bounds != RectangleF.FromLTRB(-10, 30, 100, 130))
        {
            throw new InvalidOperationException("The oriented Free Transform overlay frame lost its rotated or skewed geometry.");
        }

        var scene = new VectorScene();
        scene.CreateEmpty();
        var line = scene.AddCurveSegment(0, new PointF(0, 0), new PointF(40, 80), new PointF(100, 0), 6, Color.Transparent, Color.Coral, 6);
        var freehand = scene.AddFreehandStroke(0, new[] { new PointF(-80, -40), new PointF(-20, 60) }, 6, Color.White, brushStroke: false, 6);
        var path = scene.AddPathObjectContours(0, new[] { new[] { new PointF(140, 0), new PointF(180, 0), new PointF(160, 40), new PointF(140, 0) } }, 0, Color.Teal, Color.Transparent, 6);
        var rectangle = scene.AddObject(0, PointF.Empty, new SizeF(100, 60), 0, 4, Color.Teal, Color.White, 6, ShapeKind.Rectangle);
        scene.TransformObjects(new[] { line, freehand, path }, point => new PointF(point.X + 30, point.Y - 20));
        scene.ShearObjects(new[] { rectangle }, point => new PointF(point.X + (point.Y - 30) * 0.5f, point.Y));

        scene.TryGetLineEndpoint(line, startEndpoint: true, out var lineStart);
        scene.TryGetLineEndpoint(line, startEndpoint: false, out var lineEnd);
        var lineControl1 = new PointF(scene.CurveControlX[line], scene.CurveControlY[line]);
        var lineControl2 = new PointF(scene.CurveControl2X[line], scene.CurveControl2Y[line]);
        var freehandValid = scene.TryGetFreehandWorldPoints(freehand, out var freehandPoints);
        var pathValid = scene.TryGetPathWorldContours(path, out var pathContours);
        var skewValid = scene.TryGetPathWorldContours(rectangle, out var skewContours);
        if (!PointsNear(lineStart, new PointF(30, -20))
            || !PointsNear(lineEnd, new PointF(130, -20))
            || !PointsNear(lineControl1, new PointF(57, 33))
            || !PointsNear(lineControl2, new PointF(90, 33))
            || !freehandValid
            || freehandPoints.Length != 2
            || !PointsNear(freehandPoints[0], new PointF(-50, -60))
            || !pathValid
            || pathContours.Length != 1
            || !PointsNear(pathContours[0][0], new PointF(170, -20))
            || scene.ShapeKind[rectangle] != ShapeKind.Path
            || !skewValid
            || skewContours.Length != 1
            || !PointsNear(skewContours[0][0], new PointF(-80, -30)))
        {
            throw new InvalidOperationException("Free transform did not preserve line, freehand, path, and skew geometry.");
        }

        var irregularScene = new VectorScene();
        irregularScene.CreateEmpty();
        var irregularFill = irregularScene.AddPathObjectContours(
            0,
            [
                [
                    new PointF(-170, -55),
                    new PointF(-115, -140),
                    new PointF(-35, -105),
                    new PointF(20, -165),
                    new PointF(95, -90),
                    new PointF(165, -45),
                    new PointF(125, 35),
                    new PointF(175, 105),
                    new PointF(55, 135),
                    new PointF(-15, 95),
                    new PointF(-105, 155),
                    new PointF(-145, 45),
                    new PointF(-170, -55)
                ]
            ],
            0,
            Color.MediumSeaGreen,
            Color.Transparent,
            24);
        var irregularSnapshot = irregularScene.CreateSnapshot();
        var rotationSession = irregularScene.BeginTransformSession([irregularFill]);
        const float finalRotation = 0.73f;
        for (var step = 1; step <= 180; step++)
        {
            var angle = finalRotation * step / 180f;
            var cos = MathF.Cos(angle);
            var sin = MathF.Sin(angle);
            irregularScene.ApplyTransformSession(
                rotationSession,
                point => new PointF(point.X * cos - point.Y * sin, point.X * sin + point.Y * cos),
                convertPrimitivesToPaths: false,
                rebuildGeometryIndex: false);
        }

        irregularScene.TryGetPathWorldContours(irregularFill, out var previewRotationContours);
        irregularScene.RestoreSnapshot(irregularSnapshot);
        var finalCos = MathF.Cos(finalRotation);
        var finalSin = MathF.Sin(finalRotation);
        irregularScene.TransformObjects(
            [irregularFill],
            point => new PointF(
                point.X * finalCos - point.Y * finalSin,
                point.X * finalSin + point.Y * finalCos));
        irregularScene.TryGetPathWorldContours(irregularFill, out var directRotationContours);
        if (!ContoursNear(previewRotationContours, directRotationContours))
        {
            throw new InvalidOperationException("Repeated Free Transform rotation previews distorted an irregular fill edge.");
        }

        irregularScene.RestoreSnapshot(irregularSnapshot);
        var skewSession = irregularScene.BeginTransformSession([irregularFill]);
        const float finalSkew = 0.47f;
        for (var step = 1; step <= 180; step++)
        {
            var factor = finalSkew * step / 180f;
            irregularScene.ApplyTransformSession(
                skewSession,
                point => new PointF(point.X + point.Y * factor, point.Y),
                convertPrimitivesToPaths: true,
                rebuildGeometryIndex: false);
        }

        irregularScene.TryGetPathWorldContours(irregularFill, out var previewSkewContours);
        irregularScene.RestoreSnapshot(irregularSnapshot);
        irregularScene.ShearObjects(
            [irregularFill],
            point => new PointF(point.X + point.Y * finalSkew, point.Y));
        irregularScene.TryGetPathWorldContours(irregularFill, out var directSkewContours);
        if (!ContoursNear(previewSkewContours, directSkewContours))
        {
            throw new InvalidOperationException("Repeated Free Transform skew previews distorted an irregular fill edge.");
        }

        var deferredScene = new VectorScene();
        deferredScene.CreateEmpty();
        var deferredObject = deferredScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(100, 80),
            0,
            0,
            Color.Coral,
            Color.Transparent,
            6,
            ShapeKind.Rectangle);
        var initialGeometryRevision = deferredScene.GeometryRevision;
        var initialSummaryRevision = deferredScene.SummaryRevision;
        for (var step = 0; step < 8; step++)
        {
            deferredScene.TransformObjects(
                [deferredObject],
                point => new PointF(point.X + 20, point.Y),
                rebuildGeometryIndex: false);
        }

        deferredScene.ShearObjects(
            [deferredObject],
            point => new PointF(point.X + point.Y * 0.25f, point.Y),
            rebuildGeometryIndex: false);
        if (deferredScene.GeometryRevision != initialGeometryRevision + 9
            || deferredScene.SummaryRevision != initialSummaryRevision)
        {
            throw new InvalidOperationException("Deferred free transform rebuilt derived geometry state during pointer updates.");
        }

        var deferredGeometryRevision = deferredScene.GeometryRevision;
        deferredScene.CompleteDeferredBuild();
        if (deferredScene.GeometryRevision <= deferredGeometryRevision
            || deferredScene.SummaryRevision <= initialSummaryRevision
            || deferredScene.HitTest(new PointF(160, 0), 0) != deferredObject)
        {
            throw new InvalidOperationException("Completing a deferred free transform did not refresh spatial queries and summaries.");
        }

        Console.WriteLine("transform_geometry_regression=ok");
    }

    private static void RunShapeToolRegression()
    {
        var drawSettings = new DrawSettings { AngleSnapDegrees = 15 };
        var inputAngle = 22f * MathF.PI / 180f;
        var unsnappedAngle = drawSettings.SnapAngle(inputAngle);
        var shiftSnappedAngle = drawSettings.SnapAngle(inputAngle, temporarilyEnabled: true);
        drawSettings.SnapEnabled = true;
        drawSettings.AngleSnapEnabled = true;
        var configuredSnappedAngle = drawSettings.SnapAngle(inputAngle);
        drawSettings.SnapToGrid = true;
        drawSettings.SnapToObjects = true;
        drawSettings.GridSize = 100;
        var pointer = new PointF(94, 106);
        var objectCandidate = new PointF(91, 109);
        var objectPriorityPoint = drawSettings.ResolvePointSnap(pointer, objectCandidate);
        var gridFallbackPoint = drawSettings.ResolvePointSnap(pointer);
        var adaptiveGridPoint = drawSettings.ResolvePointSnap(
            new PointF(1494, 506),
            gridStep: 1000);
        var adaptiveObjectPriorityPoint = drawSettings.ResolvePointSnap(
            new PointF(1494, 506),
            objectCandidate,
            gridStep: 1000);
        drawSettings.SnapToObjects = false;
        var disabledObjectPoint = drawSettings.ResolvePointSnap(pointer, objectCandidate);
        var temporaryObjectPoint = drawSettings.ResolvePointSnap(
            pointer,
            objectCandidate,
            temporarilySnapToObjects: true);
        var shifted = MainForm.ResolveShapeDragBounds(
            PointF.Empty,
            new PointF(120, 40),
            keepAspectRatio: true,
            fromCenter: false);
        var centered = MainForm.ResolveShapeDragBounds(
            new PointF(80, 60),
            new PointF(130, 80),
            keepAspectRatio: true,
            fromCenter: true);
        var previewFillColor = Color.FromArgb(255, 214, 74, 82);
        var previewStrokeColor = Color.FromArgb(255, 34, 126, 214);
        var previewStrokeUnits = VectorUnits.StrokePointsToUnits(7.5f);
        var linePreviewMaterial = MainForm.ResolveDrawingPreviewMaterial(
            ShapeKind.Line,
            previewFillColor,
            previewStrokeColor,
            previewStrokeUnits);
        var shapePreviewMaterial = MainForm.ResolveDrawingPreviewMaterial(
            ShapeKind.Rectangle,
            previewFillColor,
            previewStrokeColor,
            previewStrokeUnits);
        if (Math.Abs(unsnappedAngle - inputAngle) > 0.0001f
            || Math.Abs(shiftSnappedAngle - 15f * MathF.PI / 180f) > 0.0001f
            || Math.Abs(configuredSnappedAngle - shiftSnappedAngle) > 0.0001f
            || !PointsNear(objectPriorityPoint, objectCandidate)
            || !PointsNear(gridFallbackPoint, new PointF(100, 100))
            || !PointsNear(adaptiveGridPoint, new PointF(1000, 1000))
            || !PointsNear(adaptiveObjectPriorityPoint, objectCandidate)
            || !PointsNear(disabledObjectPoint, gridFallbackPoint)
            || !PointsNear(temporaryObjectPoint, objectCandidate)
            || !PointsNear(shifted.Start, PointF.Empty)
            || !PointsNear(shifted.End, new PointF(120, 120))
            || !PointsNear(centered.Start, new PointF(30, 10))
            || !PointsNear(centered.End, new PointF(130, 110))
            || linePreviewMaterial.Color != previewStrokeColor
            || Math.Abs(linePreviewMaterial.Stroke - previewStrokeUnits) > 0.001f
            || shapePreviewMaterial.Color != previewFillColor
            || Math.Abs(shapePreviewMaterial.Stroke - previewStrokeUnits) > 0.001f)
        {
            throw new InvalidOperationException(
                "Shape modifiers, snapping priority, or Fill/Stroke drawing-preview material resolution was incorrect.");
        }

        var anchorSnap = MainForm.ResolvePenAnchorSnap(
            new PointF(94, 106),
            [new PointF(90, 108), new PointF(200, 104)],
            10,
            new PointF(100, 100),
            objectSnapping: true,
            alignment: true);
        var anchorAlignment = MainForm.ResolvePenAnchorSnap(
            new PointF(94, 106),
            [new PointF(92, 180), new PointF(200, 104)],
            10,
            new PointF(100, 100),
            objectSnapping: false,
            alignment: true);
        if (!anchorSnap.ObjectSnapped
            || !PointsNear(anchorSnap.Point, new PointF(90, 108))
            || anchorSnap.AlignX
            || anchorSnap.AlignY
            || anchorAlignment.ObjectSnapped
            || !anchorAlignment.AlignX
            || !anchorAlignment.AlignY
            || !PointsNear(anchorAlignment.Point, new PointF(92, 104)))
        {
            throw new InvalidOperationException("Pen anchor snapping did not preserve object priority or independent X/Y alignment.");
        }

        var splitScene = new VectorScene();
        splitScene.CreateEmpty();
        var splitSource = splitScene.AddCurveSegment(
            0,
            new PointF(0, 0),
            new PointF(50, 100),
            new PointF(100, 0),
            8,
            Color.Transparent,
            Color.Coral,
            12,
            LineEndpointStyle.Sharp,
            LineEndpointStyle.Sharp);
        splitScene.SetGradientPaint(
            splitSource,
            GradientKind.Linear,
            [new GradientStop(0, Color.Coral), new GradientStop(1, Color.Gold)]);
        splitScene.TryGetLineBezierPart(splitSource, 0, 1, out var originalStart, out _, out _, out var originalEnd);
        var originalOrder = splitScene.ObjectOrder[splitSource];
        var originalKeyframe = splitScene.ObjectKeyframeFrame[splitSource];
        var closestPointFound = splitScene.TryGetClosestPointOnLine(
            splitSource,
            new PointF(50, 50),
            out var closestParameter,
            out var closestPoint,
            out var closestDistance);
        var split = splitScene.SplitLineAt(splitSource, closestParameter, out var splitResult);
        splitScene.TryGetLineBezierPart(
            splitResult.FirstObjectIndex,
            0,
            1,
            out var firstStart,
            out var firstControl1,
            out var firstControl2,
            out var firstEnd);
        splitScene.TryGetLineBezierPart(
            splitResult.SecondObjectIndex,
            0,
            1,
            out var secondStart,
            out var secondControl1,
            out var secondControl2,
            out var secondEnd);
        if (!closestPointFound
            || closestDistance > 0.5f
            || Math.Abs(closestParameter - 0.5f) > 0.01f
            || !split
            || splitScene.ObjectCount != 2
            || Math.Abs(firstStart.X - originalStart.X) > 0.5f
            || Math.Abs(firstStart.Y - originalStart.Y) > 0.5f
            || !PointsNear(firstControl1, new PointF(16, 33))
            || !PointsNear(firstControl2, new PointF(33, 50))
            || Math.Abs(firstEnd.X - secondStart.X) > DrawingTopologyRules.MinStrokeSegmentUnits
            || Math.Abs(firstEnd.Y - secondStart.Y) > DrawingTopologyRules.MinStrokeSegmentUnits
            || Math.Abs(firstEnd.X - splitResult.Anchor.X) > 0.5f
            || Math.Abs(firstEnd.Y - splitResult.Anchor.Y) > 0.5f
            || Math.Abs(secondStart.X - splitResult.Anchor.X) > 0.5f
            || Math.Abs(secondStart.Y - splitResult.Anchor.Y) > 0.5f
            || !PointsNear(secondControl1, new PointF(67, 50))
            || !PointsNear(secondControl2, new PointF(83, 34))
            || Math.Abs(secondEnd.X - originalEnd.X) > 0.5f
            || Math.Abs(secondEnd.Y - originalEnd.Y) > 0.5f
            || splitScene.GetLineEndpointStyle(splitResult.FirstObjectIndex, startEndpoint: true) != LineEndpointStyle.Sharp
            || splitScene.GetLineEndpointStyle(splitResult.FirstObjectIndex, startEndpoint: false) != LineEndpointStyle.Round
            || splitScene.GetLineEndpointStyle(splitResult.SecondObjectIndex, startEndpoint: true) != LineEndpointStyle.Round
            || splitScene.GetLineEndpointStyle(splitResult.SecondObjectIndex, startEndpoint: false) != LineEndpointStyle.Sharp
            || !splitScene.HasGradient(splitResult.FirstObjectIndex)
            || !splitScene.HasGradient(splitResult.SecondObjectIndex)
            || splitScene.ObjectOrder[splitResult.FirstObjectIndex] != originalOrder
            || splitScene.ObjectOrder[splitResult.SecondObjectIndex] != originalOrder
            || splitScene.ObjectSubOrder[splitResult.SecondObjectIndex] <= splitScene.ObjectSubOrder[splitResult.FirstObjectIndex]
            || splitScene.ObjectKeyframeFrame[splitResult.FirstObjectIndex] != originalKeyframe
            || splitScene.ObjectKeyframeFrame[splitResult.SecondObjectIndex] != originalKeyframe
            || splitScene.AtomCount[splitResult.FirstObjectIndex] + splitScene.AtomCount[splitResult.SecondObjectIndex] != 12)
        {
            throw new InvalidOperationException(
                $"Pen anchor insertion did not preserve continuous curve geometry, material, ownership, or order: "
                + $"closest={closestParameter:0.###}/{closestPoint}/{closestDistance:0.###}, "
                + $"first={firstStart}/{firstControl1}/{firstControl2}/{firstEnd}, "
                + $"second={secondStart}/{secondControl1}/{secondControl2}/{secondEnd}, "
                + $"anchor={splitResult.Anchor}, objects={splitScene.ObjectCount}, "
                + $"styles={splitScene.GetLineEndpointStyle(splitResult.FirstObjectIndex, true)}/{splitScene.GetLineEndpointStyle(splitResult.FirstObjectIndex, false)}"
                + $"-{splitScene.GetLineEndpointStyle(splitResult.SecondObjectIndex, true)}/{splitScene.GetLineEndpointStyle(splitResult.SecondObjectIndex, false)}, "
                + $"gradient={splitScene.HasGradient(splitResult.FirstObjectIndex)}/{splitScene.HasGradient(splitResult.SecondObjectIndex)}, "
                + $"order={splitScene.ObjectOrder[splitResult.FirstObjectIndex]}/{splitScene.ObjectOrder[splitResult.SecondObjectIndex]}, "
                + $"sub={splitScene.ObjectSubOrder[splitResult.FirstObjectIndex]}/{splitScene.ObjectSubOrder[splitResult.SecondObjectIndex]}, "
                + $"key={splitScene.ObjectKeyframeFrame[splitResult.FirstObjectIndex]}/{splitScene.ObjectKeyframeFrame[splitResult.SecondObjectIndex]}, "
                + $"atoms={splitScene.AtomCount[splitResult.FirstObjectIndex]}+{splitScene.AtomCount[splitResult.SecondObjectIndex]}.");
        }

        var isolatedPenLine = splitScene.AddLineSegment(
            0,
            new PointF(240, 0),
            new PointF(320, 0),
            8,
            Color.Transparent,
            Color.Coral,
            6);
        var penPathSeed = new DrawingElementHit(
            new DrawingElementKey(splitResult.FirstObjectIndex, DrawingElementKind.Stroke, 0),
            0,
            0,
            1);
        var selectedPenPath = MainForm.TraditionalPenPathElements(splitScene, penPathSeed, 0);
        if (selectedPenPath.Length != 2
            || selectedPenPath.Any(hit => hit.Key.ObjectIndex == isolatedPenLine))
        {
            throw new InvalidOperationException("Traditional Pen one-click path selection did not include only the connected line segments.");
        }

        var straightPenSegment = MainForm.TraditionalPenCubicSegment(
            new PointF(0, 0),
            outgoingHandle: null,
            incomingHandle: null,
            new PointF(100, 0));
        var tangentPenSegment = MainForm.TraditionalPenCubicSegment(
            new PointF(0, 0),
            new PointF(50, 0),
            new PointF(100, 50),
            new PointF(100, 100));
        var inflectedPenSegment = MainForm.TraditionalPenCubicSegment(
            new PointF(0, 0),
            new PointF(40, 100),
            new PointF(60, -100),
            new PointF(100, 0));
        var nearParallelPenSegment = MainForm.TraditionalPenCubicSegment(
            new PointF(0, 0),
            new PointF(100, 0),
            new PointF(200, 0.01f),
            new PointF(300, 0));
        var perturbedParallelPenSegment = MainForm.TraditionalPenCubicSegment(
            new PointF(0, 0),
            new PointF(100, 0),
            new PointF(200, 0.02f),
            new PointF(300, 0));
        if (Math.Abs(straightPenSegment.Control1.X - 100f / 3f) > 0.001f
            || Math.Abs(straightPenSegment.Control2.X - 200f / 3f) > 0.001f
            || Math.Abs(straightPenSegment.Control1.Y) > 0.001f
            || Math.Abs(straightPenSegment.Control2.Y) > 0.001f
            || !PointsNear(tangentPenSegment.Control1, new PointF(50, 0))
            || !PointsNear(tangentPenSegment.Control2, new PointF(100, 50))
            || inflectedPenSegment.Control1.Y <= 0
            || inflectedPenSegment.Control2.Y >= 0
            || Math.Abs(nearParallelPenSegment.Control2.X - perturbedParallelPenSegment.Control2.X) > 0.1f
            || Math.Abs(nearParallelPenSegment.Control2.Y - perturbedParallelPenSegment.Control2.Y) > 0.1f
            || !float.IsFinite(inflectedPenSegment.Control1.X)
            || !float.IsFinite(inflectedPenSegment.Control1.Y)
            || !float.IsFinite(inflectedPenSegment.Control2.X)
            || !float.IsFinite(inflectedPenSegment.Control2.Y))
        {
            throw new InvalidOperationException("Traditional Pen cubic controls did not preserve straight, stable, or inflected curve behavior.");
        }

        using (var penGuideStage = new StageControl(splitScene))
        {
            penGuideStage.SetSelection(
                selectedPenPath.Select(hit => hit.Key.ObjectIndex),
                selectedPenPath[0].Key.ObjectIndex);
            penGuideStage.SetSelectedElements(selectedPenPath, selectedPenPath[0]);
            penGuideStage.SetPenPathHandlesVisible(true);
            if (!penGuideStage.PenPathHandlesVisible || penGuideStage.SelectedElements.Count != 2)
            {
                throw new InvalidOperationException("Traditional Pen connected-path handles were not exposed to the Stage renderers.");
            }
            penGuideStage.SetPenAnchorGuides(splitResult.Anchor, vertical: true, horizontal: false, snapped: true, insertion: false);
            if (!penGuideStage.PenAnchorGuidesVisible
                || !penGuideStage.PenAnchorGuideVertical
                || penGuideStage.PenAnchorGuideHorizontal
                || !penGuideStage.PenAnchorGuideSnapped)
            {
                throw new InvalidOperationException("Pen anchor guides did not expose their alignment and snap state.");
            }
            penGuideStage.ClearPenAnchorGuides();
            if (penGuideStage.PenAnchorGuidesVisible)
            {
                throw new InvalidOperationException("Pen anchor guides were not cleared with the pen interaction.");
            }
            penGuideStage.SetPenDirectionHandles(
                splitResult.Anchor,
                new PointF(splitResult.Anchor.X - 20, splitResult.Anchor.Y),
                new PointF(splitResult.Anchor.X + 20, splitResult.Anchor.Y));
            if (!penGuideStage.PenDirectionHandlesVisible
                || penGuideStage.PenDirectionIncoming is null
                || penGuideStage.PenDirectionOutgoing is null)
            {
                throw new InvalidOperationException("Traditional Pen direction handles were not exposed to the Stage renderers.");
            }
            penGuideStage.ClearPenDirectionHandles();
            if (penGuideStage.PenDirectionHandlesVisible)
            {
                throw new InvalidOperationException("Traditional Pen direction handles were not cleared with the path session.");
            }
            penGuideStage.SetPenPathHandlesVisible(false);
        }

        var scene = new VectorScene();
        scene.CreateEmpty();
        var polygon = scene.AddObject(
            0,
            new PointF(-120, 0),
            new SizeF(100, 100),
            0,
            0,
            Color.Teal,
            12,
            ShapeKind.Polygon,
            shapeVertexCount: 7);
        var star = scene.AddObject(
            0,
            new PointF(120, 0),
            new SizeF(100, 100),
            0,
            0,
            Color.Coral,
            12,
            ShapeKind.Star,
            shapeVertexCount: 8);
        var polygonBoundary = scene.GetShapeBoundary(polygon);
        var starBoundary = scene.GetShapeBoundary(star);
        var snapshot = scene.CreateSnapshot();
        var restored = new VectorScene();
        restored.RestoreSnapshot(snapshot);
        if (scene.GetShapeVertexCount(polygon) != 7
            || scene.GetShapeVertexCount(star) != 8
            || polygonBoundary.Length != 8
            || starBoundary.Length != 17
            || restored.GetShapeVertexCount(polygon) != 7
            || restored.GetShapeVertexCount(star) != 8
            || !ShapeSettingsPanel.SupportsShape(ShapeKind.Polygon)
            || !ShapeSettingsPanel.SupportsShape(ShapeKind.Star)
            || ShapeSettingsPanel.SupportsShape(ShapeKind.Rectangle)
            || !MainForm.ShouldShowShapeSettings(WorkspaceView.BasicDrawing, ToolMode.Polygon)
            || !MainForm.ShouldShowShapeSettings(WorkspaceView.BasicDrawing, ToolMode.Star)
            || MainForm.ShouldShowShapeSettings(WorkspaceView.BasicDrawing, ToolMode.Rectangle)
            || MainForm.ShouldShowShapeSettings(WorkspaceView.SceneEditor, ToolMode.Polygon))
        {
            throw new InvalidOperationException("Polygon sides or star points were not retained or exposed in their drawing-tool context.");
        }

        var flipScene = new VectorScene();
        flipScene.CreateEmpty();
        var triangle = flipScene.AddObject(
            0,
            new PointF(40, 20),
            new SizeF(120, 80),
            0,
            0,
            Color.Coral,
            Color.Transparent,
            8,
            ShapeKind.Triangle);
        flipScene.SetLinearGradient(
            triangle,
            Color.Coral,
            Color.RoyalBlue,
            new PointF(10, 20),
            new PointF(90, 20));
        var originalTriangle = flipScene.GetShapeBoundary(triangle)[..^1];
        var horizontalBounds = flipScene.GetObjectWorldBounds(triangle);
        var horizontalAxis = horizontalBounds.Left + horizontalBounds.Right;
        if (!flipScene.FlipObjects([triangle], horizontal: true)
            || flipScene.ShapeKind[triangle] != ShapeKind.Path
            || !flipScene.TryGetPathWorldContours(triangle, out var horizontalContours)
            || horizontalContours.Length != 1
            || !horizontalContours[0].Select((point, index) =>
                    PointsNear(point, new PointF(horizontalAxis - originalTriangle[index].X, originalTriangle[index].Y)))
                .All(match => match)
            || !PointsNear(flipScene.GetGradientStart(triangle), new PointF(horizontalAxis - 10, 20))
            || !PointsNear(flipScene.GetGradientEnd(triangle), new PointF(horizontalAxis - 90, 20)))
        {
            throw new InvalidOperationException("Horizontal flip did not mirror asymmetric geometry and its gradient around the selection bounds.");
        }

        var horizontalTriangle = horizontalContours[0].ToArray();
        var verticalBounds = flipScene.GetObjectWorldBounds(triangle);
        var verticalAxis = verticalBounds.Top + verticalBounds.Bottom;
        if (!flipScene.FlipObjects([triangle], horizontal: false)
            || !flipScene.TryGetPathWorldContours(triangle, out var verticalContours)
            || !verticalContours[0].Select((point, index) =>
                    PointsNear(point, new PointF(horizontalTriangle[index].X, verticalAxis - horizontalTriangle[index].Y)))
                .All(match => match))
        {
            throw new InvalidOperationException("Vertical flip did not mirror path geometry around the selection bounds.");
        }

        RunTransformDragFlipRegression();
        RunBitmapSelectAspectRegression();

        RunBoundaryBezierHandleRegression();

        Console.WriteLine("shape_tool_regression=ok");
    }

    // A free-transform resize handle anchors the opposite corner. Dragging the pointer across that
    // anchor must mirror the selection, which the tool expresses as a negative scale factor. The
    // previous bounds builder clamped the moving edge onto the anchor, so the factors stayed
    // positive and every flip collapsed into a one-unit sliver instead.
    private static void RunTransformDragFlipRegression()
    {
        var bounds = RectangleF.FromLTRB(0, 0, 100, 50);

        // At rest, a handle must report exactly 1 so grabbing it does not jump the selection.
        var bottomRightPivot = new PointF(bounds.Left, bounds.Top);
        foreach (var handle in new[]
                 {
                     TransformHandleKind.BottomRight,
                     TransformHandleKind.TopLeft,
                     TransformHandleKind.Right,
                     TransformHandleKind.Bottom
                 })
        {
            var (restPivotX, restPivotY) = handle switch
            {
                TransformHandleKind.BottomRight => (bounds.Left, bounds.Top),
                TransformHandleKind.TopLeft => (bounds.Right, bounds.Bottom),
                TransformHandleKind.Right => (bounds.Left, bounds.Top + bounds.Height * 0.5f),
                _ => (bounds.Left + bounds.Width * 0.5f, bounds.Top)
            };
            var restPointer = handle switch
            {
                TransformHandleKind.BottomRight => new PointF(bounds.Right, bounds.Bottom),
                TransformHandleKind.TopLeft => new PointF(bounds.Left, bounds.Top),
                TransformHandleKind.Right => new PointF(bounds.Right, bounds.Top + bounds.Height * 0.5f),
                _ => new PointF(bounds.Left + bounds.Width * 0.5f, bounds.Bottom)
            };
            if (!MainForm.TryGetResizedTransformScaleFactors(
                    bounds,
                    new PointF(restPivotX, restPivotY),
                    handle,
                    restPointer,
                    out var restX,
                    out var restY)
                || !PointsNear(new PointF(restX, restY), new PointF(1f, 1f)))
            {
                throw new InvalidOperationException(
                    $"Transform handle {handle} did not rest at a unit scale (got {restX}, {restY}).");
            }
        }

        // Dragging the bottom-right handle past its top-left anchor flips both axes.
        if (!MainForm.TryGetResizedTransformScaleFactors(
                bounds,
                bottomRightPivot,
                TransformHandleKind.BottomRight,
                new PointF(-40, -20),
                out var flippedX,
                out var flippedY)
            || flippedX >= 0f
            || flippedY >= 0f
            || !PointsNear(new PointF(flippedX, flippedY), new PointF(-0.4f, -0.4f)))
        {
            throw new InvalidOperationException(
                $"Crossing the anchor did not produce a mirrored scale factor (got {flippedX}, {flippedY}).");
        }

        // A horizontal-only flip must leave the other axis at rest rather than scaling it too.
        if (!MainForm.TryGetResizedTransformScaleFactors(
                bounds,
                bottomRightPivot,
                TransformHandleKind.Right,
                new PointF(-100, 25),
                out var singleAxisX,
                out var singleAxisY)
            || singleAxisX >= 0f
            || !NearlyEqual(singleAxisY, 1f))
        {
            throw new InvalidOperationException(
                $"A width-only flip disturbed the height axis (got {singleAxisX}, {singleAxisY}).");
        }

        // Remaining on the original side keeps the factor positive, so ordinary resizing is intact.
        if (!MainForm.TryGetResizedTransformScaleFactors(
                bounds,
                bottomRightPivot,
                TransformHandleKind.BottomRight,
                new PointF(150, 100),
                out var enlargedX,
                out var enlargedY)
            || enlargedX <= 1f
            || enlargedY <= 1f)
        {
            throw new InvalidOperationException(
                $"Enlarging inside the anchor quadrant regressed (got {enlargedX}, {enlargedY}).");
        }
    }

    private static void RunBitmapSelectAspectRegression()
    {
        var start = new SizeF(300, 200);
        var minimum = VectorUnits.FromPixels(4);

        if (!MainForm.TryGetBitmapCornerResizeSize(
                start,
                EditHandleKind.BoundsBottomRight,
                new PointF(240, 190),
                minimum,
                out var bottomRightWidth,
                out var bottomRightHeight)
            || Math.Abs(bottomRightWidth / bottomRightHeight - start.Width / start.Height) > 0.0001f
            || Math.Abs(bottomRightWidth - 435f) > 0.01f
            || Math.Abs(bottomRightHeight - 290f) > 0.01f)
        {
            throw new InvalidOperationException(
                $"Bitmap Select bottom-right resize did not preserve its aspect ratio ({bottomRightWidth} x {bottomRightHeight}).");
        }

        if (!MainForm.TryGetBitmapCornerResizeSize(
                start,
                EditHandleKind.BoundsTopLeft,
                new PointF(-240, -190),
                minimum,
                out var topLeftWidth,
                out var topLeftHeight)
            || Math.Abs(topLeftWidth / topLeftHeight - start.Width / start.Height) > 0.0001f
            || Math.Abs(topLeftWidth - 435f) > 0.01f
            || Math.Abs(topLeftHeight - 290f) > 0.01f)
        {
            throw new InvalidOperationException(
                $"Bitmap Select top-left resize did not preserve its aspect ratio ({topLeftWidth} x {topLeftHeight}).");
        }
    }

    private static void RunPencilSmoothingRegression()
    {
        var settings = new DrawSettings();
        settings.PencilSmoothing = -1;
        if (settings.PencilSmoothing != 0)
        {
            throw new InvalidOperationException("Pencil smoothing did not clamp its lower bound.");
        }
        settings.PencilSmoothing = 101;
        if (settings.PencilSmoothing != 100)
        {
            throw new InvalidOperationException("Pencil smoothing did not clamp its upper bound.");
        }

        using (var panel = new DrawSettingsPanel(settings))
        {
            panel.SetPencilPresentation();
            panel.ClientSize = new Size(248, panel.PreferredHeight);
            panel.PerformLayout();
            var privateInstance = System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic;
            var slider = typeof(DrawSettingsPanel).GetField("_pencilSmoothing", privateInstance)?.GetValue(panel)
                as ModernSlider;
            var shape = typeof(DrawSettingsPanel).GetField("_shape", privateInstance)?.GetValue(panel)
                as Control;
            var aspect = typeof(DrawSettingsPanel).GetField("_keepRatio", privateInstance)?.GetValue(panel)
                as Control;
            if (slider is null
                || shape is null
                || aspect is null
                || slider.Minimum != 0
                || slider.Maximum != 100
                || shape.Visible
                || aspect.Visible
                || panel.PreferredHeight != 78
                || panel.MinimumSize.Height != panel.PreferredHeight)
            {
                throw new InvalidOperationException("The compact Pencil smoothing panel layout or range was incorrect.");
            }

            slider.Value = 37;
            if (settings.PencilSmoothing != 37)
            {
                throw new InvalidOperationException("The Pencil smoothing slider did not update DrawSettings.");
            }
            settings.PencilSmoothing = 73;
            settings.NotifyChanged();
            if (slider.Value != 73)
            {
                throw new InvalidOperationException("DrawSettings did not refresh the Pencil smoothing slider.");
            }
        }

        var samples = new PointF[15];
        for (var index = 0; index < samples.Length; index++)
        {
            samples[index] = new PointF(index * 100, index % 2 == 0 ? 80 : -80);
        }

        var unsmoothed = FreehandStrokeProcessor.Process(samples, smoothing: 0, simplifyTolerance: 0);
        var medium = FreehandStrokeProcessor.Process(samples, smoothing: 50, simplifyTolerance: 0);
        var maximum = FreehandStrokeProcessor.Process(samples, smoothing: 100, simplifyTolerance: 0);
        ValidatePath(unsmoothed);
        ValidatePath(medium);
        ValidatePath(maximum);
        var unsmoothedExcess = ExcessPathLength(unsmoothed);
        var mediumExcess = ExcessPathLength(medium);
        var maximumExcess = ExcessPathLength(maximum);
        if (!(unsmoothedExcess > mediumExcess && mediumExcess > maximumExcess))
        {
            throw new InvalidOperationException("Increasing Pencil smoothing did not progressively reduce path jitter.");
        }

        if (!FreehandStrokeProcessor.Process(samples, smoothing: -1, simplifyTolerance: 0).SequenceEqual(unsmoothed)
            || !FreehandStrokeProcessor.Process(samples, smoothing: 101, simplifyTolerance: 0).SequenceEqual(maximum))
        {
            throw new InvalidOperationException("Pencil path processing did not clamp smoothing to 0..100.");
        }

        if (!MainForm.ShouldShowPencilSettings(WorkspaceView.BasicDrawing, ToolMode.Pencil)
            || MainForm.ShouldShowPencilSettings(WorkspaceView.BasicDrawing, ToolMode.Pen)
            || MainForm.ShouldShowPencilSettings(WorkspaceView.BasicDrawing, ToolMode.Brush)
            || MainForm.ShouldShowPencilSettings(WorkspaceView.BasicDrawing, ToolMode.Eraser)
            || MainForm.ShouldShowPencilSettings(WorkspaceView.SceneEditor, ToolMode.Pencil)
            || MainForm.ResolveSimpleFreehandSmoothing(ToolMode.Pencil, -1) != 0
            || MainForm.ResolveSimpleFreehandSmoothing(ToolMode.Pencil, 101) != 100
            || MainForm.ResolveSimpleFreehandSmoothing(ToolMode.Brush, 0) != 64
            || MainForm.ResolveSimpleFreehandSmoothing(ToolMode.Brush, 100) != 64
            || MainForm.ResolveSimpleFreehandSmoothing(ToolMode.Eraser, 0) != 52
            || MainForm.ResolveSimpleFreehandSmoothing(ToolMode.Eraser, 100) != 52)
        {
            throw new InvalidOperationException("Pencil settings visibility or tool-specific smoothing isolation was incorrect.");
        }

        Console.WriteLine("pencil_smoothing_regression=ok");
        return;

        void ValidatePath(IReadOnlyList<PointF> points)
        {
            if (points.Count < 2
                || points[0] != samples[0]
                || points[^1] != samples[^1]
                || points.Any(point => !float.IsFinite(point.X) || !float.IsFinite(point.Y)))
            {
                throw new InvalidOperationException("Pencil smoothing did not preserve a finite path and its endpoints.");
            }
        }

        static double ExcessPathLength(IReadOnlyList<PointF> points)
        {
            double pathLength = 0;
            for (var index = 1; index < points.Count; index++)
            {
                var dx = points[index].X - points[index - 1].X;
                var dy = points[index].Y - points[index - 1].Y;
                pathLength += Math.Sqrt(dx * dx + dy * dy);
            }

            var directX = points[^1].X - points[0].X;
            var directY = points[^1].Y - points[0].Y;
            return pathLength - Math.Sqrt(directX * directX + directY * directY);
        }
    }

    // Ctrl+dragging a Line produces two independent Line objects that share the corner vertex, and
    // dragging moves that shared endpoint under the pointer. It must not turn the line into a single
    // freeform path, and neither segment may bend: the result has to read as two straight segments.
    private static void RunLineCornerDragRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        var line = scene.AddLineSegment(
            scene.ActiveLayer,
            new PointF(0, 0),
            new PointF(400, 0),
            VectorUnits.StrokePointsToUnits(2),
            Color.Transparent,
            Color.Black,
            6);
        if (!scene.SplitLineAt(line, 0.5f, out var split)
            || scene.ObjectCount != 2
            || scene.ShapeKind[split.FirstObjectIndex] != ShapeKind.Line
            || scene.ShapeKind[split.SecondObjectIndex] != ShapeKind.Line)
        {
            throw new InvalidOperationException("The Line corner gesture did not split the line into two Line objects.");
        }

        if (!scene.TryGetLineEndpoint(split.FirstObjectIndex, startEndpoint: false, out var firstShared)
            || !scene.TryGetLineEndpoint(split.SecondObjectIndex, startEndpoint: true, out var secondShared)
            || !PointsWithin(firstShared, new PointF(200, 0), 0.01f)
            || !PointsWithin(secondShared, new PointF(200, 0), 0.01f)
            || !scene.TryGetLineEndpoint(split.FirstObjectIndex, startEndpoint: true, out var firstOpposite)
            || !scene.TryGetLineEndpoint(split.SecondObjectIndex, startEndpoint: false, out var secondOpposite)
            || !PointsWithin(firstOpposite, new PointF(0, 0), 0.01f)
            || !PointsWithin(secondOpposite, new PointF(400, 0), 0.01f))
        {
            throw new InvalidOperationException("The Line corner split did not keep the grabbed point as the shared endpoint.");
        }

        var anchorMethod = typeof(MainForm).GetMethod(
            "SetSplitLineCornerAnchor",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
            ?? throw new InvalidOperationException("The Line corner drag no longer exposes its shared-anchor update.");
        var dragged = new PointF(200, 150);
        anchorMethod.Invoke(null, [
            scene,
            split.FirstObjectIndex,
            false,
            firstOpposite,
            split.SecondObjectIndex,
            true,
            secondOpposite,
            (PointF)dragged]);

        if (!scene.TryGetLineEndpoint(split.FirstObjectIndex, startEndpoint: false, out firstShared)
            || !scene.TryGetLineEndpoint(split.SecondObjectIndex, startEndpoint: true, out secondShared)
            || !PointsWithin(firstShared, dragged, 0.01f)
            || !PointsWithin(secondShared, dragged, 0.01f))
        {
            throw new InvalidOperationException("Ctrl+dragging a Line corner did not move both segments onto the pointer.");
        }

        if (!scene.IsLineStraight(split.FirstObjectIndex)
            || !scene.IsLineStraight(split.SecondObjectIndex))
        {
            throw new InvalidOperationException("Ctrl+dragging a Line corner bent a segment instead of keeping two straight segments.");
        }

        // The fixed endpoints have to stay put, otherwise the corner gesture would drag the whole line.
        if (!scene.TryGetLineEndpoint(split.FirstObjectIndex, startEndpoint: true, out var firstAfter)
            || !scene.TryGetLineEndpoint(split.SecondObjectIndex, startEndpoint: false, out var secondAfter)
            || !PointsWithin(firstAfter, firstOpposite, 0.01f)
            || !PointsWithin(secondAfter, secondOpposite, 0.01f))
        {
            throw new InvalidOperationException("Ctrl+dragging a Line corner moved the segments' opposite endpoints.");
        }

        Console.WriteLine("line_corner_drag_regression=ok");
    }

    // Dragging a line's own endpoint handle converts the line into a cubic Bezier: the endpoint
    // follows the pointer while its control handle travels with it, so the segment bends instead of
    // staying a rubber band. An already-curved line only moves, and a line that shares the endpoint
    // with another line stays straight so the connection is not broken.
    private static void RunLineEndpointCurveRegression()
    {
        var start = new PointF(0, 0);
        var control1 = new PointF(100f / 3f, 0);
        var control2 = new PointF(200f / 3f, 0);
        var end = new PointF(100, 0);

        // Straight line, start endpoint dragged to (0, 60): control1 follows the endpoint and
        // control2 mirrors the same offset, which makes the segment curved.
        var (draggedControl, oppositeControl) = MainForm.ResolveLineEndpointDragControls(
            endpointIsStart: true,
            curveOnDrag: true,
            originalEndpoint: start,
            endpoint: new PointF(0, 60),
            oppositeEndpoint: end,
            translatedControl: new PointF(100f / 3f, 60),
            oppositeControl: control2);
        if (PointsWithin(draggedControl, new PointF(100f / 3f, 60), 0.01f) == false
            || PointsWithin(oppositeControl, new PointF(200f / 3f, 60), 0.01f) == false)
        {
            throw new InvalidOperationException("Dragging a line endpoint did not carry its control handles with the pointer.");
        }

        if (VectorScene.IsStraightBezierSegment(start, draggedControl, oppositeControl, new PointF(0, 60)))
        {
            throw new InvalidOperationException("Dragging a line endpoint handle did not convert the segment into a cubic Bezier.");
        }

        // The same drag with the conversion disabled keeps the original straight chord.
        var (keptDragged, keptOpposite) = MainForm.ResolveLineEndpointDragControls(
            endpointIsStart: true,
            curveOnDrag: false,
            originalEndpoint: start,
            endpoint: new PointF(0, 60),
            oppositeEndpoint: end,
            translatedControl: new PointF(100f / 3f, 60),
            oppositeControl: control2);
        if (!PointsWithin(keptOpposite, control2, 0.01f))
        {
            throw new InvalidOperationException("A connected line endpoint drag introduced curvature instead of keeping the chord straight.");
        }

        // An already-curved line keeps its own shape and only moves the dragged endpoint.
        var curvedControl1 = new PointF(20, 40);
        var (curvedDragged, curvedOpposite) = MainForm.ResolveLineEndpointDragControls(
            endpointIsStart: true,
            curveOnDrag: true,
            originalEndpoint: start,
            endpoint: new PointF(0, 60),
            oppositeEndpoint: end,
            translatedControl: new PointF(20, 100),
            oppositeControl: curvedControl1);
        if (!PointsWithin(curvedDragged, new PointF(20, 100), 0.01f)
            || !PointsWithin(curvedOpposite, curvedControl1, 0.01f)
            || VectorScene.IsStraightBezierSegment(start, curvedDragged, curvedOpposite, new PointF(0, 60)))
        {
            throw new InvalidOperationException("Dragging an endpoint of an already-curved line changed its curve instead of only moving the endpoint.");
        }

        // The shape has to survive a real scene mutation, not just the pure helper.
        var scene = new VectorScene();
        scene.CreateEmpty();
        var line = scene.AddLineSegment(
            scene.ActiveLayer,
            start,
            end,
            VectorUnits.StrokePointsToUnits(2),
            Color.Transparent,
            Color.Black,
            6);
        var moved = new PointF(0, 60);
        scene.SetLineEndpoint(line, startEndpoint: true, moved, end, draggedControl, oppositeControl, keepStraight: false);
        if (scene.IsLineStraight(line)
            || !scene.TryGetLineEndpoint(line, startEndpoint: true, out var movedStart)
            || !PointsWithin(movedStart, moved, 0.01f)
            || !scene.TryGetLineEndpoint(line, startEndpoint: false, out var keptEnd)
            || !PointsWithin(keptEnd, end, 0.01f))
        {
            throw new InvalidOperationException("The line endpoint curve drag did not produce a curved line with a fixed opposite endpoint.");
        }

        Console.WriteLine("line_endpoint_curve_regression=ok");
    }

}
