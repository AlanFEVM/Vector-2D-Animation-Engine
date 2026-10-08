using System.Diagnostics;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunSelfIntersectingFillBoundaryRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        var fill = scene.AddPathObject(
            0,
            [
                new PointF(-300, -100),
                new PointF(300, -100),
                new PointF(300, 200),
                new PointF(0, -300),
                new PointF(-300, 200)
            ],
            0,
            Color.Teal,
            Color.Transparent,
            15);
        var fillParts = scene.GetFillParts(fill, 0);
        var pieces = scene.GetExposedFillBezierSegmentPieces(fill, 0);
        var middleTop = pieces.FirstOrDefault(piece =>
            piece.SourcePartIndex == 0
            && piece.StartIsVirtualAnchor
            && piece.EndIsVirtualAnchor);
        var allPiecesResolve = pieces.All(piece =>
            scene.TryResolveFillPartForBezierSegmentPiece(fill, 0, piece, out _));
        var leftIntersection = middleTop.Start;
        var crossingPiece = pieces.FirstOrDefault(piece =>
            piece.SourcePartIndex != middleTop.SourcePartIndex
            && ((piece.StartIsVirtualAnchor && PointsWithin(piece.Start, leftIntersection, 1.5f))
                || (piece.EndIsVirtualAnchor && PointsWithin(piece.End, leftIntersection, 1.5f))));
        var startEndpoint = crossingPiece.StartIsVirtualAnchor
            && PointsWithin(crossingPiece.Start, leftIntersection, 1.5f);
        var activePart = -1;
        var prepared = middleTop.SourcePartIndex == 0
            && allPiecesResolve
            && crossingPiece.SourcePartIndex != middleTop.SourcePartIndex
            && scene.TryConvertFillToBezierPath(fill)
            && scene.TryMaterializePathBezierSegmentNeighborhood(
                fill,
                crossingPiece,
                startEndpoint,
                pieces,
                out activePart);
        var shared = prepared
            ? scene.CaptureStrokeIntersectionsAtFillAnchor(
                fill,
                activePart,
                startEndpoint,
                0,
                rebuildGeometryIndex: false)
            : new SharedBoundaryIntersection(PointF.Empty, [], [], []);
        var linkedBranch = shared.PathAnchors.FirstOrDefault(path =>
            path.ObjectIndex == fill
            && path.PartIndex != shared.OwnerPartIndex);
        var target = new PointF(-150, -130);
        var synchronized = shared.OwnerPartIndex == activePart + 1
            && shared.PathAnchors.Length == 1
            && linkedBranch.ObjectIndex == fill
            && scene.TryGetPathBezierSegment(fill, shared.OwnerPartIndex, out var activeSegment)
            && MainForm.AdjustFillEdgeBezierHandle(
                activeSegment.Start,
                activeSegment.Control1,
                activeSegment.Control2,
                activeSegment.End,
                startEndpoint ? EditHandleKind.LineStart : EditHandleKind.LineEnd,
                target) is var adjusted
            && scene.SetPathBezierSegment(
                fill,
                shared.OwnerPartIndex,
                adjusted.Start,
                adjusted.Control1,
                adjusted.Control2,
                adjusted.End,
                rebuildGeometryIndex: false,
                preserveStraightAdjacentSegments: true)
            && scene.UpdateSharedBoundaryIntersection(shared, target, rebuildGeometryIndex: false)
            && scene.TryGetPathBezierSegment(fill, shared.OwnerPartIndex, out var movedActive)
            && scene.TryGetPathBezierSegment(fill, linkedBranch.PartIndex, out var movedLinked)
            && PointsWithin(startEndpoint ? movedActive.Start : movedActive.End, target, 0.01f)
            && PointsWithin(movedLinked.Start, target, 0.01f);
        if (fillParts.Length != 3
            || pieces.Length != 9
            || !allPiecesResolve
            || !prepared
            || !synchronized)
        {
            throw new InvalidOperationException(
                $"A self-intersecting Fill knot did not keep both crossing branches draggable: regions={fillParts.Length}, pieces={pieces.Length}, allResolve={allPiecesResolve}, crossing={crossingPiece}, prepared={prepared}, owner={shared.OwnerPartIndex}, linked=[{string.Join(';', shared.PathAnchors)}], synchronized={synchronized}.");
        }
    }

    private static void RunVirtualFillIntersectionRefreshRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        scene.AddObject(
            0,
            new PointF(0, -280),
            new SizeF(1000, 800),
            0,
            VectorUnits.StrokePointsToUnits(3),
            Color.Transparent,
            Color.White,
            12,
            ShapeKind.Rectangle);
        var fill = scene.AddPathObject(
            0,
            [
                new PointF(-300, -200),
                new PointF(300, -200),
                new PointF(0, 200)
            ],
            0,
            Color.Teal,
            Color.Transparent,
            12);
        var lowerHit = scene.HitTestElement(new PointF(0, 160), 0, toleranceWorld: 2);
        var lowerPieces = lowerHit.Key.Kind == DrawingElementKind.Fill
            ? scene.GetExposedFillBezierSegmentPieces(fill, 0, lowerHit.Key.PartIndex)
            : [];
        var bottom = new PointF(0, 200);
        var activePiece = lowerPieces.FirstOrDefault(piece =>
            PointsWithin(piece.Start, bottom, 0.01f)
            || PointsWithin(piece.End, bottom, 0.01f));
        var handle = PointsWithin(activePiece.Start, bottom, 0.01f)
            ? EditHandleKind.LineStart
            : EditHandleKind.LineEnd;
        if (lowerPieces.Length != 2
            || activePiece.SourcePartIndex < 0
            || !scene.TryConvertFillToBezierPath(fill)
            || !scene.TryMaterializePathBezierSegmentNeighborhood(
                fill,
                activePiece,
                startEndpoint: handle == EditHandleKind.LineStart,
                lowerPieces,
                out var activePartIndex)
            || !scene.TryGetPathBezierSegment(fill, activePartIndex, out var activeSegment))
        {
            throw new InvalidOperationException(
                $"The two-intersection fill refresh case could not materialize its active lower edge: hit={lowerHit}, pieces={lowerPieces.Length}, active={activePiece}.");
        }

        lowerHit = scene.HitTestElement(new PointF(0, 160), 0, toleranceWorld: 2);
        var cachedPieces = scene.GetExposedFillBezierSegmentPieces(fill, 0, lowerHit.Key.PartIndex);
        var bottomPieces = cachedPieces.Where(piece =>
            PointsWithin(piece.Start, bottom, 0.01f)
            || PointsWithin(piece.End, bottom, 0.01f)).ToArray();
        if (bottomPieces.Length != 2
            || bottomPieces.Any(piece => piece.StartIsVirtualAnchor || piece.EndIsVirtualAnchor))
        {
            throw new InvalidOperationException(
                $"The shared fill vertex did not materialize both lower intervals: before=[{string.Join(';', lowerPieces)}], after=[{string.Join(';', bottomPieces)}].");
        }
        var adjacentLowerPiece = cachedPieces.First(piece =>
            piece.SourcePartIndex != activePartIndex
            && (PointsWithin(piece.Start, bottom, 0.01f)
                || PointsWithin(piece.End, bottom, 0.01f)));
        var adjacentIntersection = PointsWithin(adjacentLowerPiece.Start, bottom, 0.01f)
            ? adjacentLowerPiece.End
            : adjacentLowerPiece.Start;
        var upperLeft = new PointF(-300, -200);
        var protectedUpperPiece = scene.GetExposedFillBezierSegmentPieces(fill, 0)
            .First(piece =>
                (PointsWithin(piece.Start, upperLeft, 0.01f)
                    && PointsWithin(piece.End, adjacentIntersection, 1.5f))
                || (PointsWithin(piece.End, upperLeft, 0.01f)
                    && PointsWithin(piece.Start, adjacentIntersection, 1.5f)));
        foreach (var movedBottom in new[]
                 {
                     new PointF(-20, 220),
                     new PointF(-40, 240),
                     new PointF(-60, 260),
                     new PointF(-80, 280)
                 })
        {
            var adjusted = MainForm.AdjustFillEdgeBezierHandle(
                activeSegment.Start,
                activeSegment.Control1,
                activeSegment.Control2,
                activeSegment.End,
                handle,
                movedBottom);
            var refreshed = scene.SetPathBezierSegment(
                    fill,
                    activePartIndex,
                    adjusted.Start,
                    adjusted.Control1,
                    adjusted.Control2,
                    adjusted.End,
                    rebuildGeometryIndex: false,
                    preserveStraightAdjacentSegments: true)
                ? scene.RefreshFillBezierSegmentPieces(fill, cachedPieces)
                : [];
            var refreshedLowerPieces = refreshed
                .Where(piece =>
                    PointsWithin(piece.Start, movedBottom, 0.01f)
                    || PointsWithin(piece.End, movedBottom, 0.01f))
                .ToArray();
            var retainedIntersections = refreshedLowerPieces
                .Select(piece => PointsWithin(piece.Start, movedBottom, 0.01f)
                    ? piece.End
                    : piece.Start)
                .ToArray();
            var refreshedUpperPiece = scene.GetExposedFillBezierSegmentPieces(fill, 0)
                .First(piece =>
                    piece.SourcePartIndex == protectedUpperPiece.SourcePartIndex);
            if (refreshed.Length != cachedPieces.Length
                || refreshedLowerPieces.Any(piece => piece.StartIsVirtualAnchor || piece.EndIsVirtualAnchor)
                || retainedIntersections.Length != 2
                || retainedIntersections.Any(point => Math.Abs(point.Y - 120) > 0.01f)
                || !PointsWithin(refreshedUpperPiece.Start, protectedUpperPiece.Start, 0.01f)
                || !PointsWithin(refreshedUpperPiece.Control1, protectedUpperPiece.Control1, 0.01f)
                || !PointsWithin(refreshedUpperPiece.Control2, protectedUpperPiece.Control2, 0.01f)
                || !PointsWithin(refreshedUpperPiece.End, protectedUpperPiece.End, 0.01f))
            {
                throw new InvalidOperationException(
                    $"Dragging a shared fill vertex changed the protected boundary above one of two intersections: target={movedBottom}, intersections=[{string.Join(';', retainedIntersections)}], protected={refreshedUpperPiece}/{protectedUpperPiece}, pieces={refreshed.Length}/{cachedPieces.Length}.");
            }

            cachedPieces = refreshed;
        }
    }

    private static void RunRepeatedFillAnchorLineEndpointRegression()
    {
        RunCase(-1200, 1200, "long");
        RunCase(-250, -100, "short");

        static void RunCase(float lineStartY, float lineEndY, string label)
        {
            var scene = new VectorScene();
            scene.CreateEmpty();
            var fillBottom = lineEndY + 100;
            var fill = scene.AddObject(
                0,
                new PointF(0, (-200 + fillBottom) * 0.5f),
                new SizeF(2000, fillBottom + 200),
                0,
                0,
                Color.Teal,
                Color.Transparent,
                12,
                ShapeKind.Rectangle);
            var lineDelta = lineEndY - lineStartY;
            var line = scene.AddCubicCurveSegment(
                0,
                new PointF(0, lineStartY),
                new PointF(0, lineStartY + lineDelta / 3f),
                new PointF(0, lineStartY + lineDelta * 2f / 3f),
                new PointF(0, lineEndY),
                VectorUnits.StrokePointsToUnits(3),
                Color.Transparent,
                Color.White,
                12,
                LineEndpointStyle.Sharp,
                LineEndpointStyle.Sharp);
            var initialAnchor = new PointF(0, -200);
            var offsetProbe = new PointF(1, -200);
            var expectedProbeParameter = (-200 - lineStartY) / lineDelta;
            if (!scene.TryGetClosestPointOnLine(
                    line,
                    offsetProbe,
                    out var probeParameter,
                    out var probePoint,
                    out var probeDistance)
                || Math.Abs(probeParameter - expectedProbeParameter) > 0.0001f
                || !PointsWithin(probePoint, initialAnchor, 0.1f)
                || Math.Abs(probeDistance - 1) > 0.05f)
            {
                throw new InvalidOperationException(
                    $"The {label} Line closest-point refinement could not resolve a quantized fill anchor: parameter={probeParameter:F6}/{expectedProbeParameter:F6}, point={probePoint}, distance={probeDistance:F3}.");
            }

            var top = scene.GetEditableFillBezierSegmentParts(fill)
                .First(part => PointsWithin(part.Start, new PointF(-1000, -200), 0.01f));
            if (!scene.TryConvertFillToBezierPath(fill)
                || !scene.TryGetClosestPointOnPathBezierSegment(
                    fill,
                    top.PartIndex,
                    initialAnchor,
                    out var parameter,
                    out _,
                    out var distance)
                || distance > 1.5f
                || !scene.TryInsertPathBezierAnchor(
                    fill,
                    top.PartIndex,
                    parameter,
                    out var anchorPart,
                    out var insertedAnchor)
                || !PointsWithin(insertedAnchor, initialAnchor, 1.5f)
                || !scene.TryGetPathBezierSegment(fill, anchorPart, out var fillSegment))
            {
                throw new InvalidOperationException($"The {label} repeated fill-side Line case could not prepare its shared anchor.");
            }

            var firstBoundaryLinks = scene.CaptureFillBoundaryStrokeLinks(fill, anchorPart, 0);
            var first = scene.CaptureStrokeIntersectionsAtFillAnchor(
                fill,
                anchorPart,
                startEndpoint: true,
                0,
                rebuildGeometryIndex: false,
                excludedLineObjectIndices: firstBoundaryLinks
                    .Select(link => link.LineObjectIndex)
                    .ToHashSet());
            var firstTarget = new PointF(40, -240);
            var firstFill = MainForm.AdjustFillEdgeBezierHandle(
                fillSegment.Start,
                fillSegment.Control1,
                fillSegment.Control2,
                fillSegment.End,
                EditHandleKind.LineStart,
                firstTarget);
            if (firstBoundaryLinks.Length != 0
                || first.LineAnchors.Length != 2
                || scene.ObjectCount != 3
                || !scene.SetPathBezierSegment(
                    fill,
                    anchorPart,
                    firstFill.Start,
                    firstFill.Control1,
                    firstFill.Control2,
                    firstFill.End,
                    rebuildGeometryIndex: false,
                    preserveStraightAdjacentSegments: true)
                || !scene.UpdateSharedBoundaryIntersection(first, firstTarget, rebuildGeometryIndex: false))
            {
                scene.TryGetClosestPointOnLine(
                    line,
                    fillSegment.Start,
                    out var diagnosticParameter,
                    out var diagnosticPoint,
                    out var diagnosticDistance);
                throw new InvalidOperationException(
                    $"The {label} Line did not complete its first natural split: boundaryLinks={firstBoundaryLinks.Length}, endpointLinks={first.LineAnchors.Length}, objects={scene.ObjectCount}, anchor={fillSegment.Start}, closest={diagnosticParameter:F6}/{diagnosticPoint}/{diagnosticDistance:F3}.");
            }
            scene.CompleteDeferredBuild();

            var secondBoundaryLinks = scene.CaptureFillBoundaryStrokeLinks(fill, anchorPart, 0);
            var second = scene.CaptureStrokeIntersectionsAtFillAnchor(
                fill,
                anchorPart,
                startEndpoint: true,
                0,
                rebuildGeometryIndex: false,
                excludedLineObjectIndices: secondBoundaryLinks
                    .Select(link => link.LineObjectIndex)
                    .ToHashSet());
            var secondTarget = new PointF(-60, -160);
            if (!scene.TryGetPathBezierSegment(fill, anchorPart, out var secondFillSource))
            {
                throw new InvalidOperationException($"The {label} repeated fill-side Line case lost its fill segment.");
            }
            var secondFill = MainForm.AdjustFillEdgeBezierHandle(
                secondFillSource.Start,
                secondFillSource.Control1,
                secondFillSource.Control2,
                secondFillSource.End,
                EditHandleKind.LineStart,
                secondTarget);
            if (secondBoundaryLinks.Length != 0
                || second.LineAnchors.Length != 2
                || scene.ObjectCount != 3
                || !scene.SetPathBezierSegment(
                    fill,
                    anchorPart,
                    secondFill.Start,
                    secondFill.Control1,
                    secondFill.Control2,
                    secondFill.End,
                    rebuildGeometryIndex: false,
                    preserveStraightAdjacentSegments: true)
                || !scene.UpdateSharedBoundaryIntersection(second, secondTarget, rebuildGeometryIndex: false))
            {
                throw new InvalidOperationException(
                    $"The {label} Line was missed or split again on the second fill-side edit: boundaryLinks={secondBoundaryLinks.Length}, endpointLinks={second.LineAnchors.Length}, objects={scene.ObjectCount}.");
            }
            scene.CompleteDeferredBuild();

            var lineObjects = Enumerable.Range(0, scene.ObjectCount)
                .Where(index => scene.ShapeKind[index] == ShapeKind.Line)
                .ToArray();
            if (lineObjects.Length != 2
                || second.LineAnchors.Any(anchor =>
                    !scene.TryGetLineEndpoint(anchor.ObjectIndex, anchor.StartEndpoint, out var endpoint)
                    || !PointsWithin(endpoint, secondTarget, 0.01f)))
            {
                throw new InvalidOperationException(
                    $"The {label} repeated fill-side edit left a stale or ghost Line: lines={lineObjects.Length}, objects={scene.ObjectCount}.");
            }
        }
    }

    private static void RunCoincidentFillBoundaryLineRegression()
    {
        RunCase(reversed: false);
        RunCase(reversed: true);

        static void RunCase(bool reversed)
        {
            var scene = new VectorScene();
            scene.CreateEmpty();
            var fill = scene.AddObject(
                0,
                PointF.Empty,
                new SizeF(600, 300),
                0,
                0,
                Color.Teal,
                Color.Transparent,
                12,
                ShapeKind.Rectangle);
            if (!scene.TryConvertFillToBezierPath(fill))
            {
                throw new InvalidOperationException("The coincident fill/Line case could not convert its fill to a path.");
            }

            var top = scene.GetPathBezierSegmentParts(fill)
                .First(part => PointsWithin(part.Start, new PointF(-300, -150), 0.01f));
            var curved = new CubicDrawingPreviewSegment(
                top.Start,
                new PointF(-160, -250),
                new PointF(120, -40),
                top.End);
            if (!scene.SetPathBezierSegment(
                    fill,
                    top.PartIndex,
                    curved.Start,
                    curved.Control1,
                    curved.Control2,
                    curved.End)
                || !scene.TryGetPathBezierSegment(fill, top.PartIndex, out var source))
            {
                throw new InvalidOperationException("The coincident fill/Line case could not curve its source boundary.");
            }

            var line = scene.AddCubicCurveSegment(
                0,
                reversed ? source.End : source.Start,
                reversed ? source.Control2 : source.Control1,
                reversed ? source.Control1 : source.Control2,
                reversed ? source.Start : source.End,
                VectorUnits.StrokePointsToUnits(3),
                Color.Transparent,
                Color.White,
                12,
                LineEndpointStyle.Sharp,
                LineEndpointStyle.Sharp);
            var firstLinks = scene.CaptureFillBoundaryStrokeLinks(fill, top.PartIndex, 0);
            var firstExcluded = firstLinks.Select(link => link.LineObjectIndex).ToHashSet();
            var firstShared = scene.CaptureStrokeIntersectionsAtFillAnchor(
                fill,
                top.PartIndex,
                startEndpoint: true,
                0,
                rebuildGeometryIndex: false,
                excludedLineObjectIndices: firstExcluded);
            var firstTarget = new PointF(-260, -190);
            var firstFill = MainForm.AdjustFillEdgeBezierHandle(
                source.Start,
                source.Control1,
                source.Control2,
                source.End,
                EditHandleKind.LineStart,
                firstTarget);
            if (firstLinks.Length != 1
                || firstLinks[0].LineObjectIndex != line
                || firstShared.LineAnchors.Length != 0
                || !scene.SetPathBezierSegment(
                    fill,
                    top.PartIndex,
                    firstFill.Start,
                    firstFill.Control1,
                    firstFill.Control2,
                    firstFill.End,
                    rebuildGeometryIndex: false,
                    preserveStraightAdjacentSegments: true)
                || !scene.UpdateFillBoundaryStrokeLinks(
                    firstLinks,
                    firstFill.Start,
                    firstFill.Control1,
                    firstFill.Control2,
                    firstFill.End,
                    rebuildGeometryIndex: false))
            {
                throw new InvalidOperationException(
                    $"The {(reversed ? "reversed" : "forward")} coincident Line received overlapping synchronization roles.");
            }
            scene.CompleteDeferredBuild();

            var secondLinks = scene.CaptureFillBoundaryStrokeLinks(fill, top.PartIndex, 0);
            var secondExcluded = secondLinks.Select(link => link.LineObjectIndex).ToHashSet();
            var secondShared = scene.CaptureStrokeIntersectionsAtFillAnchor(
                fill,
                top.PartIndex,
                startEndpoint: true,
                0,
                rebuildGeometryIndex: false,
                excludedLineObjectIndices: secondExcluded);
            if (!scene.TryGetPathBezierSegment(fill, top.PartIndex, out var secondSource))
            {
                throw new InvalidOperationException("The coincident fill/Line case lost its second source segment.");
            }
            var secondTarget = new PointF(-280, -130);
            var secondFill = MainForm.AdjustFillEdgeBezierHandle(
                secondSource.Start,
                secondSource.Control1,
                secondSource.Control2,
                secondSource.End,
                EditHandleKind.LineStart,
                secondTarget);
            if (secondLinks.Length != 1
                || secondShared.LineAnchors.Length != 0
                || !scene.SetPathBezierSegment(
                    fill,
                    top.PartIndex,
                    secondFill.Start,
                    secondFill.Control1,
                    secondFill.Control2,
                    secondFill.End,
                    rebuildGeometryIndex: false,
                    preserveStraightAdjacentSegments: true)
                || !scene.UpdateFillBoundaryStrokeLinks(
                    secondLinks,
                    secondFill.Start,
                    secondFill.Control1,
                    secondFill.Control2,
                    secondFill.End,
                    rebuildGeometryIndex: false)
                || !scene.TryGetLineCubic(line, out var lineStart, out var lineControl1, out var lineControl2, out var lineEnd)
                || scene.ObjectCount != 2
                || !PointsWithin(lineStart, reversed ? secondFill.End : secondFill.Start, 0.25f)
                || !PointsWithin(lineControl1, reversed ? secondFill.Control2 : secondFill.Control1, 0.25f)
                || !PointsWithin(lineControl2, reversed ? secondFill.Control1 : secondFill.Control2, 0.25f)
                || !PointsWithin(lineEnd, reversed ? secondFill.Start : secondFill.End, 0.25f))
            {
                scene.TryGetLineCubic(
                    line,
                    out var diagnosticStart,
                    out var diagnosticControl1,
                    out var diagnosticControl2,
                    out var diagnosticEnd);
                scene.TryGetPathBezierSegment(fill, top.PartIndex, out var diagnosticFill);
                throw new InvalidOperationException(
                    $"The {(reversed ? "reversed" : "forward")} coincident Line diverged or produced a ghost on its second fill-side edit: links={secondLinks.Length}, shared={secondShared.LineAnchors.Length}, objects={scene.ObjectCount}, fill={diagnosticFill}, line={diagnosticStart}/{diagnosticControl1}/{diagnosticControl2}/{diagnosticEnd}, expected={secondFill}.");
            }
        }
    }

    private static void RunGradientPaintRegression()
    {
        RunFillEdgeAntialiasingRegression();
        var lineAxisScene = new VectorScene();
        lineAxisScene.CreateEmpty();
        var verticalLine = lineAxisScene.AddLineSegment(
            0,
            new PointF(240, 40),
            new PointF(240, 180),
            VectorUnits.StrokePointsToUnits(12),
            Color.Transparent,
            Color.White,
            8);
        lineAxisScene.SetGradientPaint(
            verticalLine,
            GradientKind.Linear,
            [new GradientStop(0, Color.Coral), new GradientStop(1, Color.RoyalBlue)]);
        var lineDefaultAxis = lineAxisScene.HasGradient(verticalLine)
            && lineAxisScene.GetGradientKind(verticalLine) == GradientKind.Linear
            && PointsNear(lineAxisScene.GetGradientStart(verticalLine), new PointF(240, 40))
            && PointsNear(lineAxisScene.GetGradientEnd(verticalLine), new PointF(240, 180));
        var adjacentLine = lineAxisScene.AddLineSegment(
            0,
            new PointF(240, 184),
            new PointF(240, 324),
            VectorUnits.StrokePointsToUnits(12),
            Color.Transparent,
            Color.White,
            8);
        var gradientLineMerge = lineAxisScene.MergeCompatibleLineSegments(0);
        var lineGradientMergeProtected = !gradientLineMerge.Changed
            && lineAxisScene.ObjectCount == 2
            && lineAxisScene.HasGradient(verticalLine)
            && !lineAxisScene.HasGradient(adjacentLine);

        var scene = new VectorScene();
        scene.CreateEmpty();
        var rectangle = scene.AddObject(
            0,
            new PointF(100, 50),
            new SizeF(80, 40),
            0,
            0,
            Color.Teal,
            12,
            ShapeKind.Rectangle);
        var line = scene.AddLineSegment(
            0,
            new PointF(20, 150),
            new PointF(180, 150),
            VectorUnits.StrokePointsToUnits(18),
            Color.Transparent,
            Color.White,
            12);
        scene.SetLinearGradient(
            rectangle,
            Color.Coral,
            Color.RoyalBlue,
            new PointF(60, 50),
            new PointF(140, 50));
        var snapshot = scene.CreateSnapshot();
        scene.TransformObjects([rectangle], point => new PointF(point.X + 25, point.Y - 10));
        var transformed = scene.HasLinearGradient(rectangle)
            && scene.GradientStartArgb[rectangle] == Color.Coral.ToArgb()
            && scene.GradientEndArgb[rectangle] == Color.RoyalBlue.ToArgb()
            && PointsNear(scene.GetGradientStart(rectangle), new PointF(85, 40))
            && PointsNear(scene.GetGradientEnd(rectangle), new PointF(165, 40));
        scene.RestoreSnapshot(snapshot);
        var restored = scene.HasLinearGradient(rectangle)
            && PointsNear(scene.GetGradientStart(rectangle), new PointF(60, 50))
            && PointsNear(scene.GetGradientEnd(rectangle), new PointF(140, 50));
        scene.ShearObjects([rectangle], point => new PointF(point.X + point.Y * 0.5f, point.Y));
        var sheared = scene.HasLinearGradient(rectangle)
            && PointsNear(scene.GetGradientStart(rectangle), new PointF(85, 50))
            && PointsNear(scene.GetGradientEnd(rectangle), new PointF(165, 50));
        scene.RestoreSnapshot(snapshot);
        scene.SetGradientPaint(
            rectangle,
            GradientKind.Radial,
            [
                new GradientStop(0, Color.Coral),
                new GradientStop(0.45f, Color.Gold),
                new GradientStop(1, Color.RoyalBlue)
            ],
            new PointF(100, 50),
            new PointF(160, 50));
        var radialSnapshot = scene.CreateSnapshot();
        scene.TransformObjects([rectangle], point => new PointF(point.X - 10, point.Y + 30));
        var radial = scene.GetGradientKind(rectangle) == GradientKind.Radial
            && scene.GetGradientStops(rectangle).Length == 3
            && scene.GetGradientStops(rectangle)[1].Argb == Color.Gold.ToArgb()
            && PointsNear(scene.GetGradientStart(rectangle), new PointF(90, 80))
            && PointsNear(scene.GetGradientEnd(rectangle), new PointF(150, 80));
        scene.RestoreSnapshot(radialSnapshot);
        var radialRestored = scene.GetGradientKind(rectangle) == GradientKind.Radial
            && scene.GetGradientStops(rectangle).Length == 3
            && PointsNear(scene.GetGradientStart(rectangle), new PointF(100, 50));
        scene.SetGradientPaint(
            line,
            GradientKind.Linear,
            [
                new GradientStop(0, Color.Coral),
                new GradientStop(0.5f, Color.Gold),
                new GradientStop(1, Color.RoyalBlue)
            ],
            new PointF(20, 150),
            new PointF(180, 150));
        var lineSnapshot = scene.CreateSnapshot();
        scene.TransformObjects([line], point => new PointF(point.X + 20, point.Y - 10));
        var lineLinear = scene.HasGradient(line)
            && scene.GetGradientKind(line) == GradientKind.Linear
            && scene.GetGradientStops(line).Length == 3
            && PointsNear(scene.GetGradientStart(line), new PointF(40, 140))
            && PointsNear(scene.GetGradientEnd(line), new PointF(200, 140));
        scene.RestoreSnapshot(lineSnapshot);
        scene.SetGradientPaint(
            line,
            GradientKind.Radial,
            [
                new GradientStop(0, Color.Coral),
                new GradientStop(1, Color.RoyalBlue)
            ],
            new PointF(100, 150),
            new PointF(180, 150));
        var lineRadial = scene.HasGradient(line)
            && scene.GetGradientKind(line) == GradientKind.Radial
            && scene.GetGradientStops(line).Length == 2;
        var sharpGradientFirst = scene.AddLineSegment(
            0,
            new PointF(-120, -60),
            new PointF(-40, -60),
            VectorUnits.StrokePointsToUnits(16),
            Color.Transparent,
            Color.Coral,
            12,
            LineEndpointStyle.Round,
            LineEndpointStyle.Sharp);
        var sharpGradientSecond = scene.AddLineSegment(
            0,
            new PointF(-40, -60),
            new PointF(-40, 20),
            VectorUnits.StrokePointsToUnits(16),
            Color.Transparent,
            Color.Coral,
            12,
            LineEndpointStyle.Sharp,
            LineEndpointStyle.Round);
        scene.SetGradientPaint(
            sharpGradientFirst,
            GradientKind.Linear,
            [new GradientStop(0, Color.RoyalBlue), new GradientStop(1, Color.Coral)]);
        scene.SetGradientPaint(
            sharpGradientSecond,
            GradientKind.Radial,
            [new GradientStop(0, Color.Coral), new GradientStop(1, Color.RoyalBlue)],
            new PointF(-40, -60),
            new PointF(40, -60));
        var gradientSharpJoin = scene.TryGetLineJoinNeighbor(sharpGradientFirst, startEndpoint: false, 0, out var sharpNeighbor, out var sharpNeighborStarts)
            && sharpNeighbor == sharpGradientSecond
            && sharpNeighborStarts
            && scene.HasGradient(sharpGradientFirst)
            && scene.HasGradient(sharpGradientSecond)
            && LineJoinGeometry.TryCreateMiter(
                new PointF(-40, -60),
                new PointF(-120, -60),
                new PointF(-40, 20),
                VectorUnits.StrokePointsToUnits(8),
                out _);

        var materializedScene = new VectorScene();
        materializedScene.CreateEmpty();
        var outlinedGradientFill = materializedScene.AddObject(
            0,
            new PointF(300, 120),
            new SizeF(120, 80),
            0,
            VectorUnits.StrokePointsToUnits(6),
            Color.Coral,
            Color.White,
            16,
            ShapeKind.Rectangle);
        var outlinedGradientStops = new[]
        {
            new GradientStop(0, Color.Coral),
            new GradientStop(0.4f, Color.Gold),
            new GradientStop(1, Color.RoyalBlue)
        };
        materializedScene.SetGradientPaint(
            outlinedGradientFill,
            GradientKind.Radial,
            outlinedGradientStops,
            new PointF(300, 120),
            new PointF(360, 120));
        var sourceFillKey = new DrawingElementKey(outlinedGradientFill, DrawingElementKind.Fill, 0);
        var materialization = materializedScene.MaterializeSelectedParts([sourceFillKey], 0);
        var materializedFill = materialization.Parts
            .Where(part => part.Source == sourceFillKey)
            .Select(part => part.Result.ObjectIndex)
            .FirstOrDefault(-1);
        materializedScene.TransformObjects([materializedFill], point => new PointF(point.X + 45, point.Y - 20));
        var materializedFillGradient = materialization.Success
            && materialization.Changed
            && materializedFill >= 0
            && materializedScene.HasGradient(materializedFill)
            && materializedScene.GetGradientKind(materializedFill) == GradientKind.Radial
            && materializedScene.GetGradientStops(materializedFill).SequenceEqual(outlinedGradientStops)
            && PointsNear(materializedScene.GetGradientStart(materializedFill), new PointF(345, 100))
            && PointsNear(materializedScene.GetGradientEnd(materializedFill), new PointF(405, 100));

        scene.SetGradientStops(
            rectangle,
            [
                new GradientStop(0, Color.Coral),
                new GradientStop(0.2f, Color.Gold),
                new GradientStop(0.65f, Color.MediumPurple),
                new GradientStop(1, Color.RoyalBlue)
            ]);
        var editableStops = scene.GetGradientStops(rectangle);
        editableStops[2] = new GradientStop(0.72f, editableStops[2].Argb);
        scene.SetGradientStops(rectangle, editableStops);
        var stopMovePreserved = scene.GetGradientStops(rectangle).Length == 4
            && Math.Abs(scene.GetGradientStops(rectangle)[2].Position - 0.72f) < 0.0001f;
        var shapeRadialScene = new VectorScene();
        shapeRadialScene.CreateEmpty();
        var irregularContour = new[]
        {
            new PointF(-140, -30),
            new PointF(-70, -110),
            new PointF(90, -80),
            new PointF(170, 10),
            new PointF(55, 135),
            new PointF(-125, 80)
        };
        var shapeRadialObject = shapeRadialScene.AddPathObject(
            0,
            irregularContour,
            0,
            Color.Coral,
            Color.Transparent,
            24);
        var shapeRadialStops = new[]
        {
            new GradientStop(0, Color.Gold),
            new GradientStop(0.5f, Color.Coral),
            new GradientStop(1, Color.RoyalBlue)
        };
        shapeRadialScene.SetShapeRadialGradient(shapeRadialObject, Color.Gold, Color.RoyalBlue, PointF.Empty);
        shapeRadialScene.SetGradientStops(shapeRadialObject, shapeRadialStops);
        var shapeContours = shapeRadialScene.GetObjectBoundaryContours(shapeRadialObject);
        shapeRadialScene.SetShapeGradientMapping(shapeRadialObject, shapeContours);
        shapeRadialScene.TryGetShapeGradientMappingWorldContours(shapeRadialObject, out var appliedShapeMapping);
        var foundShapeBoundary = GradientPaintUtilities.TryFindShapeBoundaryPoint(
            shapeContours,
            PointF.Empty,
            new PointF(1, 0),
            out var shapeBoundary);
        var shapeHalfway = new PointF(shapeBoundary.X * 0.5f, shapeBoundary.Y * 0.5f);
        var shapeEdgePosition = GradientPaintUtilities.ShapeRadialPosition(shapeContours, PointF.Empty, shapeBoundary);
        var shapeHalfPosition = GradientPaintUtilities.ShapeRadialPosition(shapeContours, PointF.Empty, shapeHalfway);
        var shapeGuideEnd = shapeRadialScene.GetGradientEnd(shapeRadialObject);
        var disjointRayContours = new[]
        {
            new[]
            {
                new PointF(-20, -20), new PointF(30, -20), new PointF(30, 20), new PointF(-20, 20)
            },
            new[]
            {
                new PointF(80, -20), new PointF(150, -20), new PointF(150, 20), new PointF(80, 20)
            }
        };
        var shapeUsesOutermostRayBoundary = GradientPaintUtilities.TryFindShapeBoundaryPoint(
                disjointRayContours,
                PointF.Empty,
                new PointF(1, 0),
                out var outerRayBoundary)
            && Math.Abs(outerRayBoundary.X - 150f) < 0.001f;
        var shapeRadialMapping = foundShapeBoundary
            && Math.Abs(shapeEdgePosition - 1f) < 0.001f
            && Math.Abs(shapeHalfPosition - 0.5f) < 0.01f
            && Math.Abs(shapeGuideEnd.X - shapeBoundary.X) <= 0.5f
            && Math.Abs(shapeGuideEnd.Y - shapeBoundary.Y) <= 0.5f
            && shapeUsesOutermostRayBoundary;
        var shapeRadialSnapshot = shapeRadialScene.CreateSnapshot();
        shapeRadialScene.DisableLinearGradient(shapeRadialObject);
        shapeRadialScene.RestoreSnapshot(shapeRadialSnapshot);
        var shapeRadialRestored = shapeRadialScene.GetGradientKind(shapeRadialObject) == GradientKind.ShapeRadial
            && shapeRadialScene.GetGradientStops(shapeRadialObject).SequenceEqual(shapeRadialStops)
            && shapeRadialScene.TryGetShapeGradientMappingWorldContours(shapeRadialObject, out var restoredShapeMapping)
            && restoredShapeMapping.SelectMany(contour => contour).SequenceEqual(appliedShapeMapping.SelectMany(contour => contour));
        if (!lineDefaultAxis || !lineGradientMergeProtected || !transformed || !restored || !sheared || !radial || !radialRestored || !lineLinear || !lineRadial || !gradientSharpJoin || !materializedFillGradient || !stopMovePreserved || !shapeRadialMapping || !shapeRadialRestored)
        {
            throw new InvalidOperationException(
                $"Gradient paint regression failed: axis={lineDefaultAxis}, merge={lineGradientMergeProtected}, transformed={transformed}, restored={restored}, sheared={sheared}, radial={radial}, radialRestored={radialRestored}, lineLinear={lineLinear}, lineRadial={lineRadial}, sharpJoin={gradientSharpJoin}, materializedFill={materializedFillGradient}, stops={stopMovePreserved}, shapeMapping={shapeRadialMapping}, foundBoundary={foundShapeBoundary}, edgePosition={shapeEdgePosition:0.###}, halfPosition={shapeHalfPosition:0.###}, boundary={shapeBoundary}, guide={shapeGuideEnd}, shapeRestored={shapeRadialRestored}.");
        }

        using var material = new MaterialEditorPanel();
        material.SetMaterial(Color.Gold, Color.Coral, 2f, 1f);
        material.SetGradient(GradientKind.Solid, [new GradientStop(0, Color.Coral), new GradientStop(1, Color.Coral)]);
        material.SetGradientPreviewTarget(strokeTarget: true);
        material.SetGradientKind(GradientKind.Linear);
        var lineGradientEditingStops = material.GradientKind == GradientKind.Linear
            && material.EditingGradientStop
            && material.GradientStops.All(stop => stop.Argb == Color.Coral.ToArgb());
        material.SetGradientKind(GradientKind.Solid);
        var gradientSettingsCollapsed = !material.GradientSettingsExpanded;
        material.SetGradientKind(GradientKind.Linear);
        var gradientSettingsExpanded = material.GradientSettingsExpanded;
        material.SetGradientKind(GradientKind.Solid);
        if (!lineGradientEditingStops || material.EditingGradientStop || !gradientSettingsCollapsed || !gradientSettingsExpanded)
        {
            throw new InvalidOperationException("Switching line paint modes did not route the color editor to gradient stops, expand gradient settings, and restore the solid state.");
        }

        var gradientMaterialChanged = false;
        var gradientChanged = false;
        material.MaterialChanged += (_, _) => gradientMaterialChanged = true;
        material.GradientChanged += (_, _) => gradientChanged = true;
        material.SetGradient(GradientKind.Linear, [new GradientStop(0, Color.Coral), new GradientStop(1, Color.RoyalBlue)]);
        material.Opacity = 0.5f;
        var gradientAlphaApplied = gradientChanged
            && !gradientMaterialChanged
            && material.Fill.A == 128
            && material.Stroke.A == 128
            && material.GradientStops.All(stop => Color.FromArgb(stop.Argb).A == 128);
        if (!gradientAlphaApplied)
        {
            throw new InvalidOperationException("Uniform alpha did not update all gradient stops without falling back to the solid material path.");
        }

        material.SetGradient(GradientKind.Solid, [new GradientStop(0, Color.White), new GradientStop(1, Color.White)]);
        material.SetGradientPreviewTarget(strokeTarget: false);
        material.SetGradient(GradientKind.Linear, [new GradientStop(0, Color.Coral), new GradientStop(1, Color.RoyalBlue)]);
        material.SetGradientPreviewTarget(strokeTarget: true);
        var strokeGradientInitiallySolid = material.GradientKind == GradientKind.Solid;
        material.SetGradient(GradientKind.Radial, [new GradientStop(0, Color.Gold), new GradientStop(1, Color.Teal)]);
        material.SetGradientPreviewTarget(strokeTarget: false);
        var fillGradientRetained = material.GradientKind == GradientKind.Linear
            && material.GradientStops.SequenceEqual([new GradientStop(0, Color.Coral), new GradientStop(1, Color.RoyalBlue)]);
        material.SetGradientPreviewTarget(strokeTarget: true);
        var strokeGradientRetained = material.GradientKind == GradientKind.Radial
            && material.GradientStops.SequenceEqual([new GradientStop(0, Color.Gold), new GradientStop(1, Color.Teal)]);
        if (!strokeGradientInitiallySolid || !fillGradientRetained || !strokeGradientRetained)
        {
            throw new InvalidOperationException("Independent Fill and Stroke gradient palettes were not retained while no object was selected.");
        }

        material.SetGradientPreviewTarget(strokeTarget: false);
        material.SetGradientKind(GradientKind.ShapeRadial);
        var shapeRadialMaterialMode = material.GradientKind == GradientKind.ShapeRadial
            && material.EditingGradientStop;
        AssertFillEditSurvivesHostEcho(material);
        using var shapePreview = new Bitmap(96, 48);
        using (var graphics = Graphics.FromImage(shapePreview))
        {
            graphics.Clear(Color.Transparent);
            GradientPreviewRenderer.Draw(graphics, new Rectangle(0, 0, shapePreview.Width, shapePreview.Height), GradientKind.ShapeRadial, shapeRadialStops);
        }
        var shapePreviewColors = new HashSet<int>();
        for (var y = 0; y < shapePreview.Height; y += 2)
        {
            for (var x = 0; x < shapePreview.Width; x += 2)
            {
                var color = shapePreview.GetPixel(x, y);
                if (color.A > 0) shapePreviewColors.Add(color.ToArgb());
            }
        }
        if (!shapeRadialMaterialMode || shapePreviewColors.Count < 4)
        {
            throw new InvalidOperationException($"The shape radial material mode or irregular preview was unavailable: mode={shapeRadialMaterialMode}, colors={shapePreviewColors.Count}.");
        }

        var selectionShortcut = ToolShortcutMap.ResolveTool(
            ToolShortcutPreset.NumberKeys,
            Keys.D1,
            ToolMode.Transform,
            ToolMode.Rectangle,
            ToolMode.Pen,
            ToolMode.PressureBrush,
            ToolMode.InkBottle);
        var fillShortcut = ToolShortcutMap.ResolveTool(
            ToolShortcutPreset.NumberKeys,
            Keys.NumPad5,
            ToolMode.Select,
            ToolMode.Rectangle,
            ToolMode.Line,
            ToolMode.Brush,
            ToolMode.InkBottle);
        var traditionalSelection = ToolShortcutMap.ResolveTool(
            ToolShortcutPreset.TraditionalFlash,
            Keys.V,
            ToolMode.Transform,
            ToolMode.Star,
            ToolMode.Pencil,
            ToolMode.PressureBrush,
            ToolMode.InkBottle);
        var traditionalInkBottle = ToolShortcutMap.ResolveTraditionalFlashTool(Keys.S);
        var traditionalPen = ToolShortcutMap.ResolveTraditionalFlashTool(Keys.P);
        var traditionalText = ToolShortcutMap.ResolveTraditionalFlashTool(Keys.T);
        var defaultSettings = new ApplicationSettings();
        using var localizedLabel = new Label { Text = "Settings" };
        using var localizedSearch = new TextBox { PlaceholderText = "Search assets or tags..." };
        using var localizedMenu = new AnimatedContextMenuStrip();
        var localizedMenuItem = new ToolStripMenuItem("Rename");
        localizedMenu.Items.Add(localizedMenuItem);
        UiLocalization.Watch(localizedLabel);
        UiLocalization.Watch(localizedSearch);
        UiLocalization.SetLanguage(UiLanguage.SimplifiedChinese);
        var chineseLocalizationApplied = localizedLabel.Text == "设置"
            && localizedSearch.PlaceholderText == "搜索素材或标签..."
            && localizedMenuItem.Text == "重命名"
            && UiLocalization.T("Selected: 2 objects") == "已选择：2 个对象";
        UiLocalization.SetLanguage(UiLanguage.English);
        var englishLocalizationRestored = localizedLabel.Text == "Settings"
            && localizedSearch.PlaceholderText == "Search assets or tags..."
            && localizedMenuItem.Text == "Rename";
        var legacySettings = System.Text.Json.JsonSerializer.Deserialize<ApplicationSettings>("{}");
        var normalizedHues = ApplicationSettingsStore.Normalize(new ApplicationSettings
        {
            ColorTheme = (ApplicationColorTheme)999,
            ThemeHueDegrees = -1,
            ThemeSaturationPercent = -1,
            ThemeBrightnessPercent = 999,
            AccentHueDegrees = 360,
            AccentSaturationPercent = 999,
            AccentBrightnessPercent = -1
        });
        var serializedHues = new ApplicationSettings
        {
            ColorTheme = ApplicationColorTheme.White,
            ThemeHueDegrees = 27,
            ThemeSaturationPercent = 84,
            ThemeBrightnessPercent = 112,
            AccentHueDegrees = 314,
            AccentSaturationPercent = 136,
            AccentBrightnessPercent = 92
        };
        var roundTripHues = System.Text.Json.JsonSerializer.Deserialize<ApplicationSettings>(
            System.Text.Json.JsonSerializer.Serialize(serializedHues));
        var defaultPalette = Theme.PaletteForHues(
            ApplicationSettings.DefaultThemeHueDegrees,
            ApplicationSettings.DefaultAccentHueDegrees);
        var themeShiftedPalette = Theme.PaletteForHues(
            ApplicationSettings.DefaultThemeHueDegrees + 120,
            ApplicationSettings.DefaultAccentHueDegrees);
        var accentShiftedPalette = Theme.PaletteForHues(
            ApplicationSettings.DefaultThemeHueDegrees,
            ApplicationSettings.DefaultAccentHueDegrees + 120);
        var whitePalette = Theme.PaletteFor(
            ApplicationColorTheme.White,
            ApplicationSettings.DefaultThemeHueDegrees,
            ApplicationSettings.DefaultAccentHueDegrees);
        var whiteShiftedPalette = Theme.PaletteFor(
            ApplicationColorTheme.White,
            ApplicationSettings.DefaultThemeHueDegrees + 120,
            ApplicationSettings.DefaultAccentHueDegrees);
        var themeAdjustedPalette = Theme.PaletteForAdjustments(
            ApplicationColorTheme.Dark,
            ApplicationSettings.DefaultThemeHueDegrees,
            0,
            150,
            ApplicationSettings.DefaultAccentHueDegrees,
            ApplicationSettings.DefaultAccentSaturationPercent,
            ApplicationSettings.DefaultAccentBrightnessPercent);
        var accentAdjustedPalette = Theme.PaletteForAdjustments(
            ApplicationColorTheme.Dark,
            ApplicationSettings.DefaultThemeHueDegrees,
            ApplicationSettings.DefaultThemeSaturationPercent,
            ApplicationSettings.DefaultThemeBrightnessPercent,
            ApplicationSettings.DefaultAccentHueDegrees,
            0,
            50);
        var paletteContrastValid = new[]
        {
            defaultPalette,
            whitePalette,
            themeAdjustedPalette,
            accentAdjustedPalette
        }.All(palette =>
            Theme.ContrastRatio(palette.Text, palette.Panel) >= 4.5d
            && Theme.ContrastRatio(palette.Text, palette.Field) >= 4.5d
            && Theme.ContrastRatio(palette.Muted, palette.Panel) >= 4.5d
            && Theme.ContrastRatio(palette.DisabledText, palette.DisabledSurface) >= 4.5d
            && Theme.ContrastRatio(palette.AccentText, palette.Accent) >= 4.5d
            && Theme.ContrastRatio(palette.AccentLabel, palette.AccentSurface) >= 4.5d);
        if (legacySettings is null
            || legacySettings.ColorTheme != ApplicationColorTheme.Dark
            || legacySettings.ThemeHueDegrees != ApplicationSettings.DefaultThemeHueDegrees
            || legacySettings.ThemeSaturationPercent != ApplicationSettings.DefaultThemeSaturationPercent
            || legacySettings.ThemeBrightnessPercent != ApplicationSettings.DefaultThemeBrightnessPercent
            || legacySettings.AccentHueDegrees != ApplicationSettings.DefaultAccentHueDegrees
            || legacySettings.AccentSaturationPercent != ApplicationSettings.DefaultAccentSaturationPercent
            || legacySettings.AccentBrightnessPercent != ApplicationSettings.DefaultAccentBrightnessPercent
            || normalizedHues.ColorTheme != ApplicationColorTheme.Dark
            || normalizedHues.ThemeHueDegrees != 359
            || normalizedHues.ThemeSaturationPercent != ApplicationSettings.MinimumSaturationPercent
            || normalizedHues.ThemeBrightnessPercent != ApplicationSettings.MaximumBrightnessPercent
            || normalizedHues.AccentHueDegrees != 0
            || normalizedHues.AccentSaturationPercent != ApplicationSettings.MaximumSaturationPercent
            || normalizedHues.AccentBrightnessPercent != ApplicationSettings.MinimumBrightnessPercent
            || roundTripHues is null
            || roundTripHues.ColorTheme != ApplicationColorTheme.White
            || roundTripHues.ThemeHueDegrees != serializedHues.ThemeHueDegrees
            || roundTripHues.ThemeSaturationPercent != serializedHues.ThemeSaturationPercent
            || roundTripHues.ThemeBrightnessPercent != serializedHues.ThemeBrightnessPercent
            || roundTripHues.AccentHueDegrees != serializedHues.AccentHueDegrees
            || roundTripHues.AccentSaturationPercent != serializedHues.AccentSaturationPercent
            || roundTripHues.AccentBrightnessPercent != serializedHues.AccentBrightnessPercent
            || defaultPalette.App != Color.FromArgb(18, 20, 22)
            || defaultPalette.Accent != Color.FromArgb(79, 179, 162)
            || themeShiftedPalette.Panel == defaultPalette.Panel
            || themeShiftedPalette.Accent != defaultPalette.Accent
            || accentShiftedPalette.Panel != defaultPalette.Panel
            || accentShiftedPalette.Accent == defaultPalette.Accent
            || themeShiftedPalette.Warning != defaultPalette.Warning
            || accentShiftedPalette.Danger != defaultPalette.Danger
            || whitePalette.App != Color.FromArgb(242, 244, 245)
            || whitePalette.Text != Color.FromArgb(31, 39, 43)
            || whitePalette.App.GetBrightness() <= whitePalette.Text.GetBrightness()
            || whitePalette.Accent == defaultPalette.Accent
            || whiteShiftedPalette.Panel == whitePalette.Panel
            || whiteShiftedPalette.Accent != whitePalette.Accent
            || themeAdjustedPalette.Panel.GetSaturation() > 0.001f
            || themeAdjustedPalette.Panel.GetBrightness() <= defaultPalette.Panel.GetBrightness()
            || themeAdjustedPalette.Accent != defaultPalette.Accent
            || accentAdjustedPalette.Panel != defaultPalette.Panel
            || accentAdjustedPalette.Accent.GetSaturation() > 0.001f
            || accentAdjustedPalette.Accent.GetBrightness() >= defaultPalette.Accent.GetBrightness()
            || !paletteContrastValid)
        {
            throw new InvalidOperationException("Application theme settings were not compatible, normalized, or independently applied.");
        }

        using (var themePreviewDialog = new SettingsDialog(
            ShortcutProfiles.TraditionalFlashProfileId,
            [],
            UiLanguage.English,
            ApplicationColorTheme.Dark,
            ApplicationSettings.DefaultThemeHueDegrees,
            ApplicationSettings.DefaultThemeSaturationPercent,
            ApplicationSettings.DefaultThemeBrightnessPercent,
            ApplicationSettings.DefaultAccentHueDegrees,
            ApplicationSettings.DefaultAccentSaturationPercent,
            ApplicationSettings.DefaultAccentBrightnessPercent))
        {
            var previewEvents = 0;
            themePreviewDialog.ThemePreviewChanged += (_, _) => previewEvents++;
            var adjustmentSliders = new Dictionary<string, ColorComponentSlider>(StringComparer.Ordinal);
            var pendingControls = new Stack<Control>();
            pendingControls.Push(themePreviewDialog);
            while (pendingControls.Count > 0)
            {
                var control = pendingControls.Pop();
                if (control is ColorComponentSlider slider
                    && !string.IsNullOrWhiteSpace(slider.AccessibleName))
                {
                    adjustmentSliders[slider.AccessibleName] = slider;
                }
                foreach (Control child in control.Controls) pendingControls.Push(child);
            }

            string[] requiredAdjustmentSliders =
            [
                "Application theme color hue in degrees",
                "Application theme color saturation in percent",
                "Application theme color brightness in percent",
                "Application highlight color hue in degrees",
                "Application highlight color saturation in percent",
                "Application highlight color brightness in percent"
            ];
            if (requiredAdjustmentSliders.Any(name => !adjustmentSliders.ContainsKey(name)))
            {
                throw new InvalidOperationException("The application theme H/S/B sliders were not all available for live preview.");
            }

            adjustmentSliders[requiredAdjustmentSliders[0]].Value = ApplicationSettings.DefaultThemeHueDegrees + 1;
            adjustmentSliders[requiredAdjustmentSliders[1]].Value = 120;
            adjustmentSliders[requiredAdjustmentSliders[2]].Value = 110;
            adjustmentSliders[requiredAdjustmentSliders[3]].Value = ApplicationSettings.DefaultAccentHueDegrees + 2;
            adjustmentSliders[requiredAdjustmentSliders[4]].Value = 130;
            adjustmentSliders[requiredAdjustmentSliders[5]].Value = 90;
            if (previewEvents != requiredAdjustmentSliders.Length
                || themePreviewDialog.SelectedThemeHueDegrees != ApplicationSettings.DefaultThemeHueDegrees + 1
                || themePreviewDialog.SelectedThemeSaturationPercent != 120
                || themePreviewDialog.SelectedThemeBrightnessPercent != 110
                || themePreviewDialog.SelectedAccentHueDegrees != ApplicationSettings.DefaultAccentHueDegrees + 2
                || themePreviewDialog.SelectedAccentSaturationPercent != 130
                || themePreviewDialog.SelectedAccentBrightnessPercent != 90)
            {
                throw new InvalidOperationException("Application theme H/S/B changes did not emit independent live-preview updates.");
            }

            if (!themePreviewDialog.FreeTransformShiftProportionalEnabled)
            {
                throw new InvalidOperationException("The Free Transform Shift aspect-ratio preference must default to checked.");
            }
        }

        using (var freeTransformPreferenceDialog = new SettingsDialog(
            ShortcutProfiles.TraditionalFlashProfileId,
            [],
            UiLanguage.English,
            ApplicationColorTheme.Dark,
            ApplicationSettings.DefaultThemeHueDegrees,
            ApplicationSettings.DefaultThemeSaturationPercent,
            ApplicationSettings.DefaultThemeBrightnessPercent,
            ApplicationSettings.DefaultAccentHueDegrees,
            ApplicationSettings.DefaultAccentSaturationPercent,
            ApplicationSettings.DefaultAccentBrightnessPercent,
            freeTransformShiftProportionalEnabled: false))
        {
            if (freeTransformPreferenceDialog.FreeTransformShiftProportionalEnabled)
            {
                throw new InvalidOperationException("The Free Transform Shift aspect-ratio preference did not reach the settings dialog.");
            }
        }

        var originalTheme = (
            Theme.ColorTheme,
            Theme.ThemeHueDegrees,
            Theme.ThemeSaturationPercent,
            Theme.ThemeBrightnessPercent,
            Theme.AccentHueDegrees,
            Theme.AccentSaturationPercent,
            Theme.AccentBrightnessPercent);
        var themeRefreshApplied = false;
        try
        {
            Theme.ConfigureColorAdjustments(
                ApplicationColorTheme.Dark,
                ApplicationSettings.DefaultThemeHueDegrees,
                ApplicationSettings.DefaultThemeSaturationPercent,
                ApplicationSettings.DefaultThemeBrightnessPercent,
                ApplicationSettings.DefaultAccentHueDegrees,
                ApplicationSettings.DefaultAccentSaturationPercent,
                ApplicationSettings.DefaultAccentBrightnessPercent);
            var previousPalette = Theme.CurrentPalette;
            using var previewSurface = new Panel { BackColor = previousPalette.Panel };
            var previewLabel = new Label
            {
                BackColor = previousPalette.Panel,
                ForeColor = previousPalette.Muted
            };
            var previewInput = new TextBox
            {
                BackColor = previousPalette.Field,
                ForeColor = previousPalette.Text
            };
            var previewButton = new Button();
            Theme.StyleActiveButton(previewButton);
            var workspaceColor = previousPalette.Stage;
            var previewStage = new StageControl(scene) { BackColor = workspaceColor };
            previewSurface.Controls.Add(previewLabel);
            previewSurface.Controls.Add(previewInput);
            previewSurface.Controls.Add(previewButton);
            previewSurface.Controls.Add(previewStage);

            Theme.ConfigureColorAdjustments(
                ApplicationColorTheme.White,
                ApplicationSettings.DefaultThemeHueDegrees + 120,
                125,
                110,
                ApplicationSettings.DefaultAccentHueDegrees + 60,
                135,
                90);
            var nextPalette = Theme.CurrentPalette;
            Theme.RefreshControlTree(previewSurface, previousPalette);
            themeRefreshApplied = previewSurface.BackColor == nextPalette.Panel
                && previewLabel.BackColor == nextPalette.Panel
                && previewLabel.ForeColor == nextPalette.Muted
                && previewInput.BackColor == nextPalette.Field
                && previewInput.ForeColor == nextPalette.Text
                && previewButton.ForeColor == nextPalette.AccentLabel
                && UiMotion.IsActive(previewButton)
                && previewStage.BackColor == workspaceColor;
        }
        finally
        {
            Theme.ConfigureColorAdjustments(
                originalTheme.ColorTheme,
                originalTheme.ThemeHueDegrees,
                originalTheme.ThemeSaturationPercent,
                originalTheme.ThemeBrightnessPercent,
                originalTheme.AccentHueDegrees,
                originalTheme.AccentSaturationPercent,
                originalTheme.AccentBrightnessPercent);
        }
        if (!themeRefreshApplied)
        {
            throw new InvalidOperationException("Live application theme refresh did not recolor existing controls or preserve the workspace color.");
        }

        var migratedNumberSettings = ApplicationSettingsStore.Normalize(new ApplicationSettings
        {
            ToolShortcutPreset = ToolShortcutPreset.NumberKeys
        });
        var numberProfile = ShortcutProfiles.GetBuiltInProfile(ToolShortcutPreset.NumberKeys);
        var numberCustomProfile = ShortcutProfiles.CreateCustomProfile("Number Editing", numberProfile);
        var nestedNumberCustomProfile = ShortcutProfiles.CreateCustomProfile(
            "Number Editing Copy",
            numberCustomProfile,
            [numberCustomProfile]);
        var customProfile = ShortcutProfiles.CreateCustomProfile(
            "Animation Tools",
            ShortcutProfiles.GetBuiltInProfile(ToolShortcutPreset.TraditionalFlash));
        var customGestureCreated = ShortcutProfiles.TryCreateGesture(
            Keys.Control | Keys.Alt | Keys.D3,
            out var customGesture);
        var customBindingSet = ShortcutProfiles.TrySetBinding(
            customProfile,
            ShortcutCommandIds.ToolTriangle,
            [customGesture],
            replaceConflicts: false,
            out var triangleProfile,
            out _,
            out _);
        var conflictRejected = !ShortcutProfiles.TrySetBinding(
            triangleProfile,
            ShortcutCommandIds.ToolPolygon,
            [customGesture],
            replaceConflicts: false,
            out _,
            out var conflictCommandId,
            out _);
        var conflictReplaced = ShortcutProfiles.TrySetBinding(
            triangleProfile,
            ShortcutCommandIds.ToolPolygon,
            [customGesture],
            replaceConflicts: true,
            out var polygonProfile,
            out _,
            out _);
        ShortcutProfiles.TryCreateGesture(Keys.Control | Keys.S, out var reservedGesture);
        var reservedRejected = !ShortcutProfiles.TrySetBinding(
            triangleProfile,
            ShortcutCommandIds.ToolStar,
            [reservedGesture],
            replaceConflicts: false,
            out _,
            out _,
            out _);
        var builtInRejected = !ShortcutProfiles.TrySetBinding(
            numberProfile,
            ShortcutCommandIds.ToolStar,
            [customGesture],
            replaceConflicts: false,
            out _,
            out _,
            out _);
        var customSettings = new ApplicationSettings
        {
            ActiveShortcutProfileId = polygonProfile.Id,
            CustomShortcutProfiles = [polygonProfile]
        };
        var roundTripShortcutSettings = ApplicationSettingsStore.Normalize(
            System.Text.Json.JsonSerializer.Deserialize<ApplicationSettings>(
                System.Text.Json.JsonSerializer.Serialize(customSettings))!);
        if (migratedNumberSettings.ActiveShortcutProfileId != ShortcutProfiles.NumberKeysProfileId
            || ApplicationSettingsStore.Normalize(new ApplicationSettings
            {
                ActiveShortcutProfileId = ShortcutProfiles.NumberKeysProfileId.ToUpperInvariant()
            }).ActiveShortcutProfileId != ShortcutProfiles.NumberKeysProfileId
            || ShortcutProfiles.LegacyPresetForProfile(
                nestedNumberCustomProfile.Id,
                [numberCustomProfile, nestedNumberCustomProfile]) != ToolShortcutPreset.NumberKeys
            || !customGestureCreated
            || !customBindingSet
            || !conflictRejected
            || conflictCommandId != ShortcutCommandIds.ToolTriangle
            || !conflictReplaced
            || !reservedRejected
            || !ShortcutProfiles.IsReservedGesture(Keys.Alt | Keys.F4)
            || !builtInRejected
            || ToolShortcutMap.ResolveTool(
                triangleProfile,
                Keys.Control | Keys.Alt | Keys.D3,
                ToolMode.Select,
                ToolMode.Rectangle,
                ToolMode.Line,
                ToolMode.Brush,
                ToolMode.Fill) != ToolMode.Triangle
            || ToolShortcutMap.ResolveTool(
                polygonProfile,
                Keys.Control | Keys.Alt | Keys.D3,
                ToolMode.Select,
                ToolMode.Rectangle,
                ToolMode.Line,
                ToolMode.Brush,
                ToolMode.Fill) != ToolMode.Polygon
            || ToolShortcutMap.ResolveTool(
                numberProfile,
                Keys.D1,
                ToolMode.Transform,
                ToolMode.Rectangle,
                ToolMode.Line,
                ToolMode.Brush,
                ToolMode.Fill) != ToolMode.Transform
            || ToolShortcutMap.ResolveTool(
                numberProfile,
                Keys.NumPad1,
                ToolMode.Transform,
                ToolMode.Rectangle,
                ToolMode.Line,
                ToolMode.Brush,
                ToolMode.Fill) != ToolMode.Transform
            || roundTripShortcutSettings.ActiveShortcutProfileId != polygonProfile.Id
            || roundTripShortcutSettings.CustomShortcutProfiles.Length != 1
            || ToolShortcutMap.ResolveCommandId(
                roundTripShortcutSettings.CustomShortcutProfiles[0],
                Keys.Control | Keys.Alt | Keys.D3) != ShortcutCommandIds.ToolPolygon)
        {
            throw new InvalidOperationException("Custom shortcut profiles did not migrate, clone, resolve, reject conflicts, or round-trip correctly.");
        }

        var toolCycleForward = MainForm.CycleToolGroupMember(
            [ToolMode.Select, ToolMode.Transform, ToolMode.Distort],
            ToolMode.Select,
            reverse: false);
        var toolCycleDistort = MainForm.CycleToolGroupMember(
            [ToolMode.Select, ToolMode.Transform, ToolMode.Distort],
            ToolMode.Distort,
            reverse: false);
        var toolCycleBackward = MainForm.CycleToolGroupMember([ToolMode.Fill, ToolMode.InkBottle], ToolMode.Fill, reverse: true);
        var lineToolCycle = MainForm.CycleToolGroupMember(
            [ToolMode.Line, ToolMode.Pen, ToolMode.SimplePen, ToolMode.Pencil],
            ToolMode.Pen,
            reverse: false);
        var pointerCommandPolicy = MainForm.BlocksModelCommandDuringPointerInteraction(Keys.Delete)
            && MainForm.BlocksModelCommandDuringPointerInteraction(Keys.Control | Keys.Z)
            && MainForm.BlocksModelCommandDuringPointerInteraction(Keys.F6)
            && !MainForm.BlocksModelCommandDuringPointerInteraction(Keys.B);
        var responsiveFillPreparationPolicy = !MainForm.ShouldPrepareArmedFillEdgeBezierPointer(
                resetCurvature: false,
                pointerMoved: false,
                completeAfterPreparation: false)
            && MainForm.ShouldPrepareArmedFillEdgeBezierPointer(
                resetCurvature: true,
                pointerMoved: false,
                completeAfterPreparation: false)
            && MainForm.ShouldPrepareArmedFillEdgeBezierPointer(
                resetCurvature: false,
                pointerMoved: true,
                completeAfterPreparation: false)
            && MainForm.ShouldPrepareArmedFillEdgeBezierPointer(
                resetCurvature: false,
                pointerMoved: false,
                completeAfterPreparation: true);
        var deferredPointerPresentationPolicy = !MainForm.ShouldPostDeferredPresentationRefresh(
                stageHasCapture: true,
                pointerActive: true,
                firstMutationPrepared: false)
            && !MainForm.ShouldPostDeferredPresentationRefresh(
                stageHasCapture: true,
                pointerActive: true,
                firstMutationPrepared: true)
            && MainForm.ShouldPostDeferredPresentationRefresh(
                stageHasCapture: false,
                pointerActive: true,
                firstMutationPrepared: false)
            && MainForm.ShouldPostDeferredPresentationRefresh(
                stageHasCapture: true,
                pointerActive: false,
                firstMutationPrepared: false);
        var selectionDragPreviewPolicy = MainForm.ShouldPresentSelectionDragPreview(
                firstMove: true,
                handle: EditHandleKind.None,
                selectedElementCount: 1)
            && !MainForm.ShouldPresentSelectionDragPreview(
                firstMove: false,
                handle: EditHandleKind.None,
                selectedElementCount: 1)
            && !MainForm.ShouldPresentSelectionDragPreview(
                firstMove: true,
                handle: EditHandleKind.LineStart,
                selectedElementCount: 1)
            && !MainForm.ShouldPresentSelectionDragPreview(
                firstMove: true,
                handle: EditHandleKind.None,
                selectedElementCount: 0);
        if (selectionShortcut != ToolMode.Transform
            || fillShortcut != ToolMode.InkBottle
            || ToolShortcutMap.ResolveNumberKeyTool(Keys.D8, ToolMode.Select, ToolMode.Rectangle, ToolMode.Line, ToolMode.Brush, ToolMode.Fill) != ToolMode.Eyedropper
            || traditionalSelection != ToolMode.Select
            || traditionalInkBottle != ToolMode.InkBottle
            || traditionalPen != ToolMode.Pen
            || traditionalText != ToolMode.Text
            || !pointerCommandPolicy
            || !responsiveFillPreparationPolicy
            || !deferredPointerPresentationPolicy
            || !selectionDragPreviewPolicy
            || defaultSettings.ToolShortcutPreset != ToolShortcutPreset.TraditionalFlash
            || defaultSettings.Language != UiLanguage.English
            || defaultSettings.ColorTheme != ApplicationColorTheme.Dark
            || defaultSettings.ThemeHueDegrees != ApplicationSettings.DefaultThemeHueDegrees
            || defaultSettings.ThemeSaturationPercent != ApplicationSettings.DefaultThemeSaturationPercent
            || defaultSettings.ThemeBrightnessPercent != ApplicationSettings.DefaultThemeBrightnessPercent
            || defaultSettings.AccentHueDegrees != ApplicationSettings.DefaultAccentHueDegrees
            || defaultSettings.AccentSaturationPercent != ApplicationSettings.DefaultAccentSaturationPercent
            || defaultSettings.AccentBrightnessPercent != ApplicationSettings.DefaultAccentBrightnessPercent
            || defaultSettings.TimelineFrameWidth != 14
            || defaultSettings.TimelineFrameHeight != TimelineFrameHeightPreset.Medium
            || defaultSettings.WorkspaceColorArgb != ApplicationSettings.DefaultWorkspaceColorArgb
            || !chineseLocalizationApplied
            || !englishLocalizationRestored
            || !ToolShortcutMap.IsVaultShortcut(ToolShortcutPreset.NumberKeys, Keys.D9)
            || ToolShortcutMap.IsVaultShortcut(ToolShortcutPreset.TraditionalFlash, Keys.D9)
            || toolCycleForward != ToolMode.Transform
            || toolCycleDistort != ToolMode.Select
            || toolCycleBackward != ToolMode.InkBottle
            || lineToolCycle != ToolMode.SimplePen)
        {
            throw new InvalidOperationException(
                "Tool shortcut, active-pointer command, responsive fill preparation, localization, or grouped Tab-cycle routing did not resolve correctly.");
        }

        using var form = new Form
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-30_000, -30_000),
            ClientSize = new Size(320, 220)
        };
        using var stage = new StageControl(scene) { Dock = DockStyle.Fill };
        form.Controls.Add(stage);
        form.Show();
        Application.DoEvents();
        stage.SetGradientOverlay(
            new PointF(0, 0),
            new PointF(1_000, 0),
            scene.GetGradientKind(rectangle),
            scene.GetGradientStops(rectangle));
        var overlayStop = stage.WorldToScreen(720, 0);
        var overlayHit = stage.HitTestGradientOverlay(Point.Round(overlayStop));
        stage.Invalidate();
        stage.Update();
        Application.DoEvents();
        if (!stage.LastFrameUsedDirect2D || stage.LastStats.DrawnObjects != 4 || overlayHit.Kind != GradientHandleKind.Stop || overlayHit.StopIndex != 2)
        {
            throw new InvalidOperationException($"The Direct2D radial multi-stop gradient annotator did not produce an interactive stage frame: direct2d={stage.LastFrameUsedDirect2D}, drawn={stage.LastStats.DrawnObjects}, hit={overlayHit.Kind}/{overlayHit.StopIndex}.");
        }

        stage.Invalidate();
        stage.Update();
        var stableGradientCache = stage.LastDirect2DGradientBrushCacheBuilds == 0
            && stage.LastDirect2DGradientBrushCacheReuses == 4;
        var updatedRectangleStops = scene.GetGradientStops(rectangle);
        updatedRectangleStops[1] = new GradientStop(updatedRectangleStops[1].Position, Color.LimeGreen);
        scene.SetGradientStops(rectangle, updatedRectangleStops);
        stage.Invalidate();
        stage.Update();
        var editedGradientCache = stage.LastDirect2DGradientBrushCacheBuilds == 1
            && stage.LastDirect2DGradientBrushCacheReuses == 3;
        if (!stableGradientCache || !editedGradientCache)
        {
            throw new InvalidOperationException(
                $"Direct2D fill/line gradient brushes were not reused or selectively refreshed: " +
                $"stable={stableGradientCache}, edited={editedGradientCache}, " +
                $"builds={stage.LastDirect2DGradientBrushCacheBuilds}, reuses={stage.LastDirect2DGradientBrushCacheReuses}.");
        }

        var rectangleGradientStart = scene.GetGradientStart(rectangle);
        var rectangleGradientEnd = scene.GetGradientEnd(rectangle);
        scene.X[rectangle] += 48;
        scene.Y[rectangle] += 32;
        scene.SetLinearGradientEndpoints(
            rectangle,
            new PointF(rectangleGradientStart.X + 48, rectangleGradientStart.Y + 32),
            new PointF(rectangleGradientEnd.X + 48, rectangleGradientEnd.Y + 32));
        stage.Invalidate();
        stage.Update();
        var translatedGradientMaskCache = stage.LastDirect2DShapeGradientMaskGeometryCacheBuilds == 0
            && stage.LastDirect2DShapeGradientMaskGeometryCacheReuses >= 1
            && stage.LastDirect2DGradientBrushCacheBuilds == 0
            && stage.LastDirect2DGradientBrushCacheReuses == 4;
        if (!translatedGradientMaskCache)
        {
            throw new InvalidOperationException(
                $"Dragging a gradient primitive rebuilt Direct2D resources: "
                + $"masks={stage.LastDirect2DShapeGradientMaskGeometryCacheBuilds}/{stage.LastDirect2DShapeGradientMaskGeometryCacheReuses}, "
                + $"brushes={stage.LastDirect2DGradientBrushCacheBuilds}/{stage.LastDirect2DGradientBrushCacheReuses}.");
        }

        stage.ReloadRenderingModuleForHotReload();
        stage.Update();
        var reloadedGradientMaskCache = stage.LastFrameUsedDirect2D
            && stage.LastDirect2DShapeGradientMaskGeometryCacheBuilds >= 1
            && stage.LastDirect2DShapeGradientMaskGeometryCacheReuses == 0;
        if (!reloadedGradientMaskCache)
        {
            throw new InvalidOperationException(
                $"Rendering hot reload reused shape-gradient geometry from the retired Direct2D factory: "
                + $"direct2d={stage.LastFrameUsedDirect2D}, "
                + $"masks={stage.LastDirect2DShapeGradientMaskGeometryCacheBuilds}/{stage.LastDirect2DShapeGradientMaskGeometryCacheReuses}.");
        }

        var lineCacheScene = new VectorScene();
        lineCacheScene.CreateEmpty();
        var cachedLineA = lineCacheScene.AddCubicCurveSegment(
            0,
            new PointF(-400, -100),
            new PointF(-280, -260),
            new PointF(-120, 160),
            PointF.Empty,
            16,
            Color.Transparent,
            Color.White,
            6);
        lineCacheScene.AddCubicCurveSegment(
            0,
            PointF.Empty,
            new PointF(120, 160),
            new PointF(280, -260),
            new PointF(400, -100),
            16,
            Color.Transparent,
            Color.White,
            6);
        lineCacheScene.AddCubicCurveSegment(
            0,
            PointF.Empty,
            new PointF(-120, 160),
            new PointF(120, 260),
            new PointF(0, 400),
            16,
            Color.Transparent,
            Color.White,
            6);
        stage.BindScene(lineCacheScene);
        stage.SetVisibleWorldWidth(1_200);
        stage.ClearGradientOverlay();
        stage.Invalidate();
        stage.Update();
        var lineCacheFirstFrame = stage.LastFrameUsedDirect2D
            && stage.LastDirect2DLineGeometryCacheBuilds == 3;
        stage.Invalidate();
        stage.Update();
        var lineCacheStableFrame = stage.LastDirect2DLineGeometryCacheBuilds == 0
            && stage.LastDirect2DLineGeometryCacheReuses >= 3;
        lineCacheScene.X[cachedLineA] += 48;
        lineCacheScene.Y[cachedLineA] += 32;
        lineCacheScene.CurveControlX[cachedLineA] += 48;
        lineCacheScene.CurveControlY[cachedLineA] += 32;
        lineCacheScene.CurveControl2X[cachedLineA] += 48;
        lineCacheScene.CurveControl2Y[cachedLineA] += 32;
        stage.Invalidate();
        stage.Update();
        var lineCacheTranslatedFrame = stage.LastDirect2DLineGeometryCacheBuilds == 0
            && stage.LastDirect2DLineGeometryCacheReuses >= 3;
        lineCacheScene.CurveControlY[cachedLineA] += 40;
        stage.Invalidate();
        stage.Update();
        var lineCacheEditedFrame = stage.LastDirect2DLineGeometryCacheBuilds == 1
            && stage.LastDirect2DLineGeometryCacheReuses >= 2;
        if (!lineCacheFirstFrame
            || !lineCacheStableFrame
            || !lineCacheTranslatedFrame
            || !lineCacheEditedFrame)
        {
            throw new InvalidOperationException(
                $"Direct2D line geometry caching did not reuse stable paths or selectively rebuild an edited line: "
                + $"first={lineCacheFirstFrame}, stable={lineCacheStableFrame}, translated={lineCacheTranslatedFrame}, edited={lineCacheEditedFrame}, "
                + $"builds={stage.LastDirect2DLineGeometryCacheBuilds}, reuses={stage.LastDirect2DLineGeometryCacheReuses}.");
        }


        stage.BindScene(shapeRadialScene);
        stage.SetVisibleWorldWidth(500);
        stage.ClearGradientOverlay();
        stage.Invalidate();
        stage.Update();
        Application.DoEvents();
        var shapeGradientFirstFrame = stage.LastFrameUsedDirect2D
            && stage.LastStats.DrawnObjects == 1
            && stage.LastDirect2DShapeGradientBitmapCacheBuilds == 1;
        stage.Invalidate();
        stage.Update();
        var shapeGradientStableFrame = stage.LastDirect2DShapeGradientBitmapCacheBuilds == 0
            && stage.LastDirect2DShapeGradientBitmapCacheReuses == 1;
        var recoloredShapeStops = shapeRadialScene.GetGradientStops(shapeRadialObject);
        recoloredShapeStops[1] = new GradientStop(recoloredShapeStops[1].Position, Color.LimeGreen);
        shapeRadialScene.SetGradientStops(shapeRadialObject, recoloredShapeStops);
        stage.Invalidate();
        stage.Update();
        var shapeGradientRecoloredFrame = stage.LastDirect2DShapeGradientBitmapCacheBuilds == 1;
        if (!shapeGradientFirstFrame || !shapeGradientStableFrame || !shapeGradientRecoloredFrame)
        {
            throw new InvalidOperationException(
                $"Shape radial Direct2D rendering or caching failed: first={shapeGradientFirstFrame}, stable={shapeGradientStableFrame}, recolored={shapeGradientRecoloredFrame}, builds={stage.LastDirect2DShapeGradientBitmapCacheBuilds}, reuses={stage.LastDirect2DShapeGradientBitmapCacheReuses}.");
        }

        const int denseShapeGradientCount = 48;
        var denseShapeGradientScene = new VectorScene();
        denseShapeGradientScene.CreateEmpty();
        for (var index = 0; index < denseShapeGradientCount; index++)
        {
            var center = new PointF((index % 8 - 3.5f) * 48, (index / 8 - 2.5f) * 48);
            var contour = irregularContour
                .Select(point => new PointF(center.X + point.X * 0.12f, center.Y + point.Y * 0.12f))
                .ToArray();
            var gradientObject = denseShapeGradientScene.AddPathObject(
                0,
                contour,
                0,
                Color.Coral,
                Color.Transparent,
                24);
            denseShapeGradientScene.SetShapeRadialGradient(gradientObject, Color.Gold, Color.RoyalBlue, center);
            denseShapeGradientScene.SetGradientStops(gradientObject, shapeRadialStops);
            denseShapeGradientScene.SetShapeGradientMapping(
                gradientObject,
                denseShapeGradientScene.GetObjectBoundaryContours(gradientObject));
        }

        stage.BindScene(denseShapeGradientScene);
        stage.SetVisibleWorldWidth(500);
        stage.Invalidate();
        stage.Update();
        var denseShapeGradientFirstFrame = stage.LastFrameUsedDirect2D
            && stage.LastStats.DrawnObjects == denseShapeGradientCount
            && stage.LastDirect2DShapeGradientBitmapCacheBuilds == denseShapeGradientCount
            && stage.LastDirect2DShapeGradientMaskGeometryCacheBuilds == 0
            && stage.LastDirect2DObjectPathGeometryCacheBuilds >= 1
            && stage.LastDirect2DObjectPathGeometryCacheBuilds
                + stage.LastDirect2DObjectPathGeometryCacheReuses >= denseShapeGradientCount;
        stage.Invalidate();
        stage.Update();
        var denseShapeGradientStableFrame = stage.LastDirect2DShapeGradientBitmapCacheBuilds == 0
            && stage.LastDirect2DShapeGradientBitmapCacheReuses == denseShapeGradientCount
            && stage.LastDirect2DShapeGradientMaskGeometryCacheBuilds == 0
            && stage.LastDirect2DShapeGradientMaskGeometryCacheReuses == 0
            && stage.LastDirect2DObjectPathGeometryCacheBuilds == 0
            && stage.LastDirect2DObjectPathGeometryCacheReuses >= denseShapeGradientCount;
        if (!denseShapeGradientFirstFrame || !denseShapeGradientStableFrame)
        {
            throw new InvalidOperationException(
                $"Dense shape radial cache exceeded its stable-frame capacity: first={denseShapeGradientFirstFrame}, stable={denseShapeGradientStableFrame}, bitmapBuilds={stage.LastDirect2DShapeGradientBitmapCacheBuilds}, bitmapReuses={stage.LastDirect2DShapeGradientBitmapCacheReuses}, maskBuilds={stage.LastDirect2DShapeGradientMaskGeometryCacheBuilds}, maskReuses={stage.LastDirect2DShapeGradientMaskGeometryCacheReuses}, objectPathBuilds={stage.LastDirect2DObjectPathGeometryCacheBuilds}, objectPathReuses={stage.LastDirect2DObjectPathGeometryCacheReuses}.");
        }

        var curvedBrushScene = new VectorScene();
        curvedBrushScene.CreateEmpty();
        var brushPath = new[]
        {
            new PointF(-1_200, 640),
            new PointF(-820, -720),
            new PointF(260, -1_000),
            new PointF(1_160, -260),
            new PointF(860, 860),
            new PointF(-180, 1_120),
            new PointF(-900, 520)
        };
        var curvedBrushObjects = curvedBrushScene.AddSoftBrushStroke(
            0,
            brushPath,
            VectorUnits.StrokePointsToUnits(28),
            Color.MediumPurple,
            BrushShape.CreateTraditionalBrush(),
            64);
        var curvedStops = new[]
        {
            new GradientStop(0, Color.SpringGreen),
            new GradientStop(0.44f, Color.DeepSkyBlue),
            new GradientStop(1, Color.MediumPurple)
        };
        foreach (var objectIndex in curvedBrushObjects)
        {
            curvedBrushScene.SetGradientPaint(objectIndex, GradientKind.Linear, curvedStops, brushPath[0], brushPath[^1]);
            curvedBrushScene.SetGradientPath(objectIndex, brushPath);
        }

        stage.BindScene(curvedBrushScene);
        stage.SetVisibleWorldWidth(5_000);
        stage.Invalidate();
        stage.Update();
        Application.DoEvents();
        if (!stage.LastFrameUsedDirect2D || stage.LastStats.DrawnObjects != curvedBrushObjects.Length)
        {
            throw new InvalidOperationException("The Direct2D renderer did not draw the trajectory-gradient brush path.");
        }
        const int trajectoryGradientSamples = 16;
        for (var warmup = 0; warmup < 3; warmup++)
        {
            stage.Invalidate();
            stage.Update();
        }
        var trajectoryGradientCommandMilliseconds = 0d;
        var trajectoryGradientCacheBuilds = 0;
        var trajectoryGradientCacheReuses = 0;
        for (var sample = 0; sample < trajectoryGradientSamples; sample++)
        {
            stage.Invalidate();
            stage.Update();
            trajectoryGradientCommandMilliseconds += stage.LastDirect2DCommandMilliseconds;
            trajectoryGradientCacheBuilds += stage.LastDirect2DPathGradientBrushCacheBuilds;
            trajectoryGradientCacheReuses += stage.LastDirect2DPathGradientBrushCacheReuses;
        }
        var trajectoryGradientAverageCommandMilliseconds = trajectoryGradientCommandMilliseconds / trajectoryGradientSamples;
        if (!stage.LastFrameUsedDirect2D
            || trajectoryGradientAverageCommandMilliseconds > RenderCollectBudgetMilliseconds
            || trajectoryGradientCacheBuilds != 0
            || trajectoryGradientCacheReuses != trajectoryGradientSamples * curvedBrushObjects.Length)
        {
            throw new InvalidOperationException(
                $"Trajectory-gradient brush rendering exceeded its command budget: direct2d={stage.LastFrameUsedDirect2D}, " +
                $"averageMs={trajectoryGradientAverageCommandMilliseconds:0.000}, budgetMs={RenderCollectBudgetMilliseconds:0.000}, " +
                $"cacheBuilds={trajectoryGradientCacheBuilds}, cacheReuses={trajectoryGradientCacheReuses}.");
        }
        form.Close();

        Console.WriteLine($"trajectory_gradient_command_avg_ms={trajectoryGradientAverageCommandMilliseconds:0.000}");
        Console.WriteLine($"trajectory_gradient_cache_reuses={trajectoryGradientCacheReuses}");
        Console.WriteLine("gradient_paint_regression=ok");
    }

    /// <summary>
    /// Changing the fill color must not be reverted by the host echoing the scene value back
    /// through SetMaterial while the edit is still in flight.
    /// </summary>
    internal static void AssertFillEditSurvivesHostEcho(MaterialEditorPanel material)
    {
        material.SetGradient(GradientKind.Solid, [new GradientStop(0, Color.Black), new GradientStop(1, Color.Black)]);
        material.SetGradientPreviewTarget(strokeTarget: false);
        material.SetMaterial(Color.White, Color.Coral, 2f, 1f);

        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var editingFill = typeof(MaterialEditorPanel).GetField("_editingFill", flags);
        var handlingColorChange = typeof(MaterialEditorPanel).GetField("_handlingColorChange", flags);
        if (editingFill is null || handlingColorChange is null)
        {
            throw new InvalidOperationException("The fill-edit echo regression could not read the material editor state.");
        }

        // Simulate the in-flight window: the editor is on the fill target and a color change is
        // being raised, while the host writes the still-stale scene fill back into the panel.
        var previousEditingFill = editingFill.GetValue(material);
        var previousHandling = handlingColorChange.GetValue(material);
        editingFill.SetValue(material, true);
        handlingColorChange.SetValue(material, true);
        try
        {
            material.Fill = Color.DeepSkyBlue;
            material.SetMaterial(Color.White, material.Stroke, 2f, 1f);
            if (material.Fill.ToArgb() != Color.DeepSkyBlue.ToArgb())
            {
                throw new InvalidOperationException($"The host echo reverted an in-flight fill edit to {material.Fill}.");
            }
        }
        finally
        {
            editingFill.SetValue(material, previousEditingFill);
            handlingColorChange.SetValue(material, previousHandling);
        }
    }

    private static void RunFillEdgeAntialiasingRegression()
    {
        using var antialiasedBitmap = new Bitmap(40, 40);
        using var hardenedBitmap = new Bitmap(40, 40);
        using var singlePassBitmap = new Bitmap(40, 40);
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddPolygon(
        [
            new PointF(4.25f, 4.5f),
            new PointF(35.25f, 11.75f),
            new PointF(9.5f, 35.25f)
        ]);
        using var fill = new SolidBrush(Color.White);

        using (var graphics = Graphics.FromImage(antialiasedBitmap))
        {
            graphics.Clear(Color.Black);
            StageControl.FillPathAntialiased(graphics, path, fill);
        }

        using (var graphics = Graphics.FromImage(hardenedBitmap))
        {
            graphics.Clear(Color.Black);
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
            graphics.FillPath(fill, path);
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            graphics.FillPath(fill, path);
        }

        using (var graphics = Graphics.FromImage(singlePassBitmap))
        {
            graphics.Clear(Color.Black);
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            graphics.FillPath(fill, path);
        }

        var softenedInnerEdgePixel = false;
        var reinforcedEdgePixels = 0;
        var transitionalPixels = 0;
        for (var y = 0; y < antialiasedBitmap.Height; y++)
        {
            for (var x = 0; x < antialiasedBitmap.Width; x++)
            {
                var smooth = antialiasedBitmap.GetPixel(x, y).R;
                var hardened = hardenedBitmap.GetPixel(x, y).R;
                var singlePass = singlePassBitmap.GetPixel(x, y).R;
                if (smooth is > 0 and < 255) transitionalPixels++;
                if (smooth is > 0 and < 255 && hardened == 255) softenedInnerEdgePixel = true;
                if (singlePass is > 0 and < 255 && smooth > singlePass) reinforcedEdgePixels++;
            }
        }

        if (!softenedInnerEdgePixel || transitionalPixels < 20 || reinforcedEdgePixels < 20)
        {
            throw new InvalidOperationException(
                $"Fill-edge antialiasing did not preserve fractional geometry coverage: " +
                $"softenedInnerEdge={softenedInnerEdgePixel}, transitionalPixels={transitionalPixels}, " +
                $"reinforcedEdgePixels={reinforcedEdgePixels}.");
        }

        Console.WriteLine($"fill_edge_antialias_pixels={transitionalPixels}");
        Console.WriteLine($"fill_edge_reinforced_pixels={reinforcedEdgePixels}");
    }

}
