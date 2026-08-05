namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunPencilBezierRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        var sourcePoints = new[]
        {
            new PointF(-6000, -1200),
            new PointF(-3000, 2200),
            new PointF(0, -2600),
            new PointF(3200, 1800),
            new PointF(6800, -800)
        };
        var fittedSamples = Enumerable.Range(0, 513)
            .Select(index => new PointF(
                index * 24f,
                MathF.Sin(index * 0.085f) * 720f
                    + MathF.Sin(index * 0.31f) * 120f))
            .ToArray();
        var smoothingLevels = new[] { 0, 25, 50, 75, 100 };
        var fittedResults = smoothingLevels
            .Select(level => FreehandStrokeProcessor.ProcessPencil(
                fittedSamples,
                level,
                VectorUnits.UnitsPerPixel))
            .ToArray();
        if (fittedResults.Any(result => !result.HasGeometry)
            || fittedResults.Where((result, index) => index > 0)
                .Select((result, index) => result.Nodes.Length <= fittedResults[index].Nodes.Length)
                .Any(nonIncreasing => !nonIncreasing)
            || fittedResults[^1].Nodes.Length >= fittedResults[0].Nodes.Length
            || fittedResults.Any(result =>
                result.Nodes[0].Anchor != VectorUnits.Quantize(fittedSamples[0])
                || result.Nodes[^1].Anchor != VectorUnits.Quantize(fittedSamples[^1])
                || result.Nodes.Any(node =>
                    !float.IsFinite(node.Anchor.X)
                    || !float.IsFinite(node.Anchor.Y)
                    || !float.IsFinite(node.IncomingControl.X)
                    || !float.IsFinite(node.IncomingControl.Y)
                    || !float.IsFinite(node.OutgoingControl.X)
                    || !float.IsFinite(node.OutgoingControl.Y))))
        {
            throw new InvalidOperationException(
                $"Pencil smoothing did not produce a stable lower-segment Bezier fit: {string.Join('/', fittedResults.Select(result => Math.Max(0, result.Nodes.Length - 1)))}.");
        }

        var fittedPreviewScene = new VectorScene();
        fittedPreviewScene.CreateEmpty();
        var fittedPreviewObject = fittedPreviewScene.AddFreehandBezierStroke(
            0,
            fittedResults[^1].Nodes,
            VectorUnits.StrokePointsToUnits(2),
            Color.Black,
            (uint)fittedResults[^1].PreviewPoints.Length);
        if (fittedPreviewObject < 0
            || !fittedPreviewScene.TryGetFreehandBezierWorldNodes(fittedPreviewObject, out var committedFit)
            || !committedFit.SequenceEqual(fittedResults[^1].Nodes)
            || !fittedPreviewScene.TryGetFreehandWorldPoints(fittedPreviewObject, out var committedSamples)
            || !committedSamples.SequenceEqual(fittedResults[^1].PreviewPoints))
        {
            throw new InvalidOperationException(
                "Pencil preview and committed Bezier geometry did not use the same processed result.");
        }

        var stroke = VectorUnits.StrokePointsToUnits(3);
        var color = Color.DeepSkyBlue;
        var pencil = scene.AddFreehandStroke(
            0,
            sourcePoints,
            stroke,
            color,
            brushStroke: false,
            atoms: 37);
        if (pencil < 0
            || scene.ShapeKind[pencil] != ShapeKind.Freeform
            || !scene.TryGetFreehandBezierWorldNodes(pencil, out var nodes)
            || nodes.Length < 2
            || !scene.TryGetFreehandBezierSegment(pencil, 0, out var firstSegment)
            || scene.TryGetFreehandBezierSegment(pencil, nodes.Length - 1, out _)
            || !PointsNear(firstSegment.Start, nodes[0].Anchor)
            || !PointsNear(firstSegment.End, nodes[1].Anchor))
        {
            throw new InvalidOperationException("Pencil did not create an editable open Bezier path.");
        }

        var originalOrder = scene.ObjectOrder[pencil];
        var originalSubOrder = scene.ObjectSubOrder[pencil];
        var originalAtoms = scene.AtomCount[pencil];
        var adjustedControl1 = new PointF(
            firstSegment.Control1.X + 900,
            firstSegment.Control1.Y - 1400);
        var adjustedControl2 = new PointF(
            firstSegment.Control2.X - 700,
            firstSegment.Control2.Y + 1100);
        if (!scene.SetFreehandBezierSegmentForPreview(
                pencil,
                0,
                firstSegment.Start,
                adjustedControl1,
                adjustedControl2,
                firstSegment.End,
                rebuildGeometryIndex: false)
            || !scene.CompleteFreehandBezierPreview(pencil)
            || !scene.TryGetFreehandBezierSegment(pencil, 0, out var adjustedSegment)
            || !PointsNear(adjustedSegment.Control1, VectorUnits.Quantize(adjustedControl1))
            || !PointsNear(adjustedSegment.Control2, VectorUnits.Quantize(adjustedControl2))
            || scene.ShapeKind[pencil] != ShapeKind.Freeform
            || scene.Stroke[pencil] != stroke
            || scene.StrokeArgb[pencil] != color.ToArgb()
            || scene.ObjectOrder[pencil] != originalOrder
            || scene.ObjectSubOrder[pencil] != originalSubOrder
            || scene.AtomCount[pencil] != originalAtoms
            || !scene.TryGetFreehandWorldPoints(pencil, out var adjustedSamples)
            || adjustedSamples.Length < 2)
        {
            throw new InvalidOperationException("Pencil Bezier editing did not preserve the open stroke and its metadata.");
        }

        var snapshot = scene.CreateSnapshot();
        scene.SetFreehandBezierSegmentForPreview(
            pencil,
            0,
            adjustedSegment.Start,
            new PointF(adjustedSegment.Control1.X + 2000, adjustedSegment.Control1.Y),
            adjustedSegment.Control2,
            adjustedSegment.End);
        scene.RestoreSnapshot(snapshot);
        if (!scene.TryGetFreehandBezierSegment(pencil, 0, out var restoredSegment)
            || !PointsNear(restoredSegment.Control1, adjustedSegment.Control1)
            || !PointsNear(restoredSegment.Control2, adjustedSegment.Control2))
        {
            throw new InvalidOperationException("Pencil Bezier controls were not preserved by scene snapshots.");
        }

        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "Vector2DAnimationEngine",
            $"pencil-bezier-regression-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            const string drawingObjectId = "pencil-bezier-regression";
            var svgPath = Path.Combine(temporaryRoot, "Pencil.svg");
            DrawingObjectSvgCodec.Write(svgPath, drawingObjectId, snapshot);
            var persisted = DrawingObjectSvgCodec.Read(svgPath, drawingObjectId);
            var persistedScene = new VectorScene();
            persistedScene.RestoreSnapshot(persisted);
            if (!persisted.FreehandBezierLocalNodes.ContainsKey(pencil)
                || !persistedScene.TryGetFreehandBezierSegment(pencil, 0, out var persistedSegment)
                || !PointsNear(persistedSegment.Control1, adjustedSegment.Control1)
                || !PointsNear(persistedSegment.Control2, adjustedSegment.Control2))
            {
                throw new InvalidOperationException("Drawing-object persistence lost Pencil Bezier controls.");
            }
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }

        var legacySnapshot = scene.CreateSnapshot();
        legacySnapshot.FreehandBezierLocalNodes.Clear();
        scene.RestoreSnapshot(legacySnapshot);
        if (!scene.TryGetFreehandBezierLocalNodes(pencil, out var derivedLegacyNodes)
            || !scene.TryGetFreehandBezierLocalNodes(pencil, out var cachedLegacyNodes)
            || !ReferenceEquals(derivedLegacyNodes, cachedLegacyNodes)
            || !scene.TryGetFreehandBezierSegment(pencil, 0, out var legacySegment)
            || scene.CreateSnapshot().FreehandBezierLocalNodes.ContainsKey(pencil))
        {
            throw new InvalidOperationException(
                "Legacy Pencil geometry was not cached transiently or was mutated while only resolving edit handles.");
        }
        if (!scene.SetFreehandBezierSegmentForPreview(
                pencil,
                0,
                legacySegment.Start,
                legacySegment.Control1,
                new PointF(legacySegment.Control2.X, legacySegment.Control2.Y + 750),
                legacySegment.End)
            || !scene.CreateSnapshot().FreehandBezierLocalNodes.ContainsKey(pencil))
        {
            throw new InvalidOperationException("Legacy Pencil geometry did not materialize Bezier data on first edit.");
        }

        if (!scene.CompleteFreehandBezierPreview(pencil))
        {
            throw new InvalidOperationException("Pencil Bezier preview could not be completed.");
        }

        var stageScene = new VectorScene();
        stageScene.CreateEmpty();
        var stagePencil = stageScene.AddFreehandBezierStroke(
            0,
            new[]
            {
                new PathBezierNode(
                    new PointF(-3000, 0),
                    new PointF(-3000, 0),
                    new PointF(-1600, -1800)),
                new PathBezierNode(
                    new PointF(3000, 0),
                    new PointF(1600, 1800),
                    new PointF(3000, 0))
            },
            stroke,
            color,
            atoms: 12);
        if (stagePencil < 0
            || !stageScene.TryGetFreehandBezierSegment(stagePencil, 0, out var stageSegment))
        {
            throw new InvalidOperationException("Pencil Bezier handle regression setup failed.");
        }
        var probe = stageSegment.Samples[stageSegment.Samples.Length / 2];
        var rawHit = stageScene.HitTestElement(probe, frame: 0, toleranceWorld: stroke);
        using var stage = new StageControl(stageScene) { Size = new Size(720, 480) };
        var probeScreen = Point.Round(stage.WorldToScreen(probe.X, probe.Y));
        var resolvedHit = stage.TryResolveEditableBezierHit(probeScreen, rawHit, out var editableHit);
        var handleStart = PointF.Empty;
        var handleControl1 = PointF.Empty;
        var handleControl2 = PointF.Empty;
        var handleEnd = PointF.Empty;
        var exposedPoints = resolvedHit
            && editableHit.BezierSegmentIndex >= 0
            && stage.TryGetEditableBezierWorldPoints(
                editableHit,
                out handleStart,
                out handleControl1,
                out handleControl2,
                out handleEnd);
        var startHandle = exposedPoints
            ? stage.HitTestLineElementHandle(
                Point.Round(stage.WorldToScreen(handleStart.X, handleStart.Y)),
                editableHit)
            : EditHandleKind.None;
        var control1Handle = exposedPoints
            ? stage.HitTestLineElementHandle(
                Point.Round(stage.WorldToScreen(handleControl1.X, handleControl1.Y)),
                editableHit)
            : EditHandleKind.None;
        var control2Handle = exposedPoints
            ? stage.HitTestLineElementHandle(
                Point.Round(stage.WorldToScreen(handleControl2.X, handleControl2.Y)),
                editableHit)
            : EditHandleKind.None;
        var endHandle = exposedPoints
            ? stage.HitTestLineElementHandle(
                Point.Round(stage.WorldToScreen(handleEnd.X, handleEnd.Y)),
                editableHit)
            : EditHandleKind.None;
        if (!rawHit.IsValid
            || !resolvedHit
            || !exposedPoints
            || startHandle != EditHandleKind.LineStart
            || control1Handle != EditHandleKind.BezierControl
            || control2Handle != EditHandleKind.BezierControl2
            || endHandle != EditHandleKind.LineEnd)
        {
            throw new InvalidOperationException(
                $"Stage did not expose the Pencil segment's Bezier handles: raw={rawHit.IsValid}, resolved={resolvedHit}, segment={editableHit.BezierSegmentIndex}, points={exposedPoints}, handles={startHandle}/{control1Handle}/{control2Handle}/{endHandle}.");
        }

        stage.SetSelectionState(
            new[] { stagePencil },
            stagePencil,
            new[] { rawHit },
            rawHit);
        stage.SetHoveredLineElement(editableHit);
        if (!stage.ShouldDrawHoveredLineControls())
        {
            throw new InvalidOperationException(
                "Selecting the Pencil stroke suppressed the hovered segment's Bezier handles.");
        }
        stage.SetSelectionState(
            new[] { stagePencil },
            stagePencil,
            new[] { editableHit },
            editableHit);
        if (stage.ShouldDrawHoveredLineControls())
        {
            throw new InvalidOperationException(
                "The selected Pencil segment rendered a duplicate hovered Bezier handle overlay.");
        }
        if (!MainForm.ShouldKeepHoveredPencilSegment(
                stageScene,
                new[] { stagePencil },
                Array.Empty<DrawingElementHit>(),
                editableHit)
            || !MainForm.ShouldKeepHoveredPencilSegment(
                stageScene,
                new[] { stagePencil },
                new[] { rawHit },
                editableHit))
        {
            throw new InvalidOperationException(
                "Pencil segment handles were not retained while moving from the selected stroke to a control handle.");
        }
        if (MainForm.PencilPreviewMinimumIntervalMilliseconds(1024) != 0
            || MainForm.PencilPreviewMinimumIntervalMilliseconds(2048) < 32
            || MainForm.PencilPreviewMinimumIntervalMilliseconds(100_000) > 160)
        {
            throw new InvalidOperationException("Long Pencil preview fitting was not bounded by the expected update cadence.");
        }

        var marqueeScene = new VectorScene();
        marqueeScene.CreateEmpty();
        var marqueeStroke = marqueeScene.AddFreehandBezierStroke(
            0,
            new[]
            {
                new PathBezierNode(
                    new PointF(-4000, 0),
                    new PointF(-4000, 0),
                    new PointF(-2200, -1800)),
                new PathBezierNode(
                    new PointF(4000, 0),
                    new PointF(2200, 1800),
                    new PointF(4000, 0))
            },
            stroke,
            color,
            atoms: 30);
        var marqueeOrder = marqueeScene.ObjectOrder[marqueeStroke];
        var marqueeResult = marqueeScene.MaterializeMarqueeSelectionParts(
            RectangleF.FromLTRB(-1000, -4000, 1000, 4000),
            frame: 0);
        var marqueePieces = Enumerable.Range(0, marqueeScene.ObjectCount)
            .Where(index => marqueeScene.ShapeKind[index] == ShapeKind.Freeform)
            .Select(index =>
            {
                if (!marqueeScene.TryGetFreehandBezierWorldNodes(index, out var pieceNodes))
                {
                    throw new InvalidOperationException("A marquee Pencil replacement lost its exact Bezier nodes.");
                }
                return (Index: index, Nodes: pieceNodes);
            })
            .OrderBy(piece => piece.Nodes[0].Anchor.X)
            .ToArray();
        if (marqueeStroke < 0
            || !marqueeResult.Success
            || !marqueeResult.Changed
            || marqueeResult.SelectedObjects.Length != 1
            || marqueePieces.Length != 3
            || marqueePieces.Any(piece =>
                piece.Nodes.Length != 2
                || marqueeScene.ObjectLayer[piece.Index] != 0
                || marqueeScene.ObjectOrder[piece.Index] != marqueeOrder
                || marqueeScene.StrokeArgb[piece.Index] != color.ToArgb()
                || marqueeScene.Stroke[piece.Index] != stroke)
            || marqueePieces[0].Nodes[0].Anchor != new PointF(-4000, 0)
            || marqueePieces[^1].Nodes[^1].Anchor != new PointF(4000, 0)
            || !PointsNear(marqueePieces[0].Nodes[^1].Anchor, marqueePieces[1].Nodes[0].Anchor)
            || !PointsNear(marqueePieces[1].Nodes[^1].Anchor, marqueePieces[2].Nodes[0].Anchor)
            || marqueeResult.SelectedObjects[0] != marqueePieces[1].Index)
        {
            throw new InvalidOperationException(
                $"Pencil marquee selection did not preserve three exact open curve runs: success={marqueeResult.Success}, changed={marqueeResult.Changed}, selected={marqueeResult.SelectedObjects.Length}, pieces={marqueePieces.Length}.");
        }

        var intersectionScene = new VectorScene();
        intersectionScene.CreateEmpty();
        var intersectingPencil = intersectionScene.AddFreehandBezierStroke(
            0,
            new[]
            {
                new PathBezierNode(
                    new PointF(-3000, 0),
                    new PointF(-3000, 0),
                    new PointF(-1000, 0)),
                new PathBezierNode(
                    new PointF(3000, 0),
                    new PointF(1000, 0),
                    new PointF(3000, 0))
            },
            stroke,
            color,
            atoms: 20);
        var intersectingLine = intersectionScene.AddLineSegment(
            0,
            new PointF(0, -3000),
            new PointF(0, 3000),
            stroke,
            Color.Transparent,
            Color.Red,
            atoms: 20);
        var intersectionResult = intersectionScene.MaterializeLineIntersections([intersectingPencil], frame: 0);
        var splitPencils = Enumerable.Range(0, intersectionScene.ObjectCount)
            .Where(index => intersectionScene.ShapeKind[index] == ShapeKind.Freeform)
            .ToArray();
        var splitLines = Enumerable.Range(0, intersectionScene.ObjectCount)
            .Where(index => intersectionScene.ShapeKind[index] == ShapeKind.Line)
            .ToArray();
        var pencilTouchesIntersection = splitPencils.All(index =>
            intersectionScene.TryGetFreehandBezierWorldNodes(index, out var splitNodes)
            && splitNodes.Length == 2
            && (PointsNear(splitNodes[0].Anchor, PointF.Empty)
                || PointsNear(splitNodes[^1].Anchor, PointF.Empty)));
        var lineTouchesIntersection = splitLines.All(index =>
            intersectionScene.TryGetLineEndpoint(index, startEndpoint: true, out var start)
            && intersectionScene.TryGetLineEndpoint(index, startEndpoint: false, out var end)
            && (PointsNear(start, PointF.Empty) || PointsNear(end, PointF.Empty)));
        if (intersectingPencil < 0
            || intersectingLine < 0
            || !intersectionResult.Success
            || !intersectionResult.Changed
            || splitPencils.Length != 2
            || splitLines.Length != 2
            || !pencilTouchesIntersection
            || !lineTouchesIntersection)
        {
            throw new InvalidOperationException(
                $"Line/Pencil intersection materialization failed: success={intersectionResult.Success}, changed={intersectionResult.Changed}, pencils={splitPencils.Length}, lines={splitLines.Length}.");
        }

        var legacyPoints = new[]
        {
            new PointF(-4000, 0),
            new PointF(-3950, 650),
            new PointF(-3900, -650),
            new PointF(-3850, 600),
            new PointF(-3800, -600),
            new PointF(-3600, 0),
            new PointF(-1000, 200),
            new PointF(1000, -200),
            new PointF(4000, 0)
        };
        var legacyMarqueeScene = new VectorScene();
        legacyMarqueeScene.CreateEmpty();
        var legacyMarqueePencil = legacyMarqueeScene.AddFreehandStroke(
            0,
            legacyPoints,
            stroke,
            color,
            brushStroke: false,
            atoms: 40);
        var legacyMarqueeSnapshot = legacyMarqueeScene.CreateSnapshot();
        legacyMarqueeSnapshot.FreehandBezierLocalNodes.Remove(legacyMarqueePencil);
        legacyMarqueeSnapshot.FreehandLocalPoints[legacyMarqueePencil] = legacyPoints
            .Select(point => new PointF(
                point.X - legacyMarqueeSnapshot.X[legacyMarqueePencil],
                point.Y - legacyMarqueeSnapshot.Y[legacyMarqueePencil]))
            .ToArray();
        legacyMarqueeScene.RestoreSnapshot(legacyMarqueeSnapshot);

        var legacyMarqueeResult = legacyMarqueeScene.MaterializeMarqueeSelectionParts(
            RectangleF.FromLTRB(-500, -2000, 500, 2000),
            frame: 0);
        if (!legacyMarqueeResult.Success
            || !legacyMarqueeResult.Changed
            || legacyMarqueeResult.SelectedObjects.Length != 1
            || !legacyMarqueeScene.TryGetFreehandBezierWorldNodes(
                legacyMarqueeResult.SelectedObjects[0],
                out var legacySelectedNodes)
            || legacySelectedNodes.Length < 2
            || Math.Abs(legacySelectedNodes[0].Anchor.X + 500) > 1
            || Math.Abs(legacySelectedNodes[^1].Anchor.X - 500) > 1)
        {
            throw new InvalidOperationException(
                "Legacy Pencil marquee materialization did not preserve the original polyline's rectangle cuts.");
        }

        var legacyIntersectionScene = new VectorScene();
        legacyIntersectionScene.CreateEmpty();
        var legacyIntersectingPencil = legacyIntersectionScene.AddFreehandStroke(
            0,
            legacyPoints,
            stroke,
            color,
            brushStroke: false,
            atoms: 40);
        var legacyIntersectionSnapshot = legacyIntersectionScene.CreateSnapshot();
        legacyIntersectionSnapshot.FreehandBezierLocalNodes.Remove(legacyIntersectingPencil);
        legacyIntersectionSnapshot.FreehandLocalPoints[legacyIntersectingPencil] = legacyPoints
            .Select(point => new PointF(
                point.X - legacyIntersectionSnapshot.X[legacyIntersectingPencil],
                point.Y - legacyIntersectionSnapshot.Y[legacyIntersectingPencil]))
            .ToArray();
        legacyIntersectionScene.RestoreSnapshot(legacyIntersectionSnapshot);
        legacyIntersectionScene.AddLineSegment(
            0,
            new PointF(0, -3000),
            new PointF(0, 3000),
            stroke,
            Color.Transparent,
            Color.Red,
            atoms: 20);

        var legacyIntersectionResult = legacyIntersectionScene.MaterializeLineIntersections(
            [legacyIntersectingPencil],
            frame: 0);
        var legacySplitPencils = Enumerable.Range(0, legacyIntersectionScene.ObjectCount)
            .Where(index => legacyIntersectionScene.ShapeKind[index] == ShapeKind.Freeform)
            .ToArray();
        if (!legacyIntersectionResult.Success
            || !legacyIntersectionResult.Changed
            || legacySplitPencils.Length != 2
            || legacySplitPencils.Any(index =>
                !legacyIntersectionScene.TryGetFreehandBezierWorldNodes(index, out var pieceNodes)
                || pieceNodes.Length < 2
                || !PointsNear(pieceNodes[0].Anchor, PointF.Empty)
                    && !PointsNear(pieceNodes[^1].Anchor, PointF.Empty)))
        {
            throw new InvalidOperationException(
                "Legacy Pencil intersection materialization did not preserve the original polyline's intersection cut.");
        }

        var compactionScene = new VectorScene();
        compactionScene.CreateEmpty();
        compactionScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(100, 100),
            0,
            0,
            Color.White,
            Color.Transparent,
            3,
            ShapeKind.Rectangle);
        var compactedPencil = compactionScene.AddFreehandStroke(
            0,
            sourcePoints,
            stroke,
            color,
            brushStroke: false,
            atoms: 37);
        if (compactedPencil != 1
            || !compactionScene.RemoveObjectAt(0)
            || compactionScene.ShapeKind[0] != ShapeKind.Freeform
            || !compactionScene.TryGetFreehandBezierWorldNodes(0, out var compactedNodes)
            || compactedNodes.Length < 2)
        {
            throw new InvalidOperationException("Pencil Bezier data did not follow object-index compaction.");
        }

        Console.WriteLine("pencil_bezier_regression=ok");
    }
}
