using System.Diagnostics;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunBoundaryBezierHandleRegression()
    {
        static PointF Mix(PointF first, PointF second, float amount) => new(
            first.X + (second.X - first.X) * amount,
            first.Y + (second.Y - first.Y) * amount);

        var scene = new VectorScene();
        scene.CreateEmpty();
        var rectangle = scene.AddObject(
            0,
            PointF.Empty,
            new SizeF(5000, 3000),
            0,
            VectorUnits.StrokePointsToUnits(2),
            Color.Teal,
            Color.CornflowerBlue,
            12,
            ShapeKind.Rectangle);
        var boundary = scene.HitTestElement(new PointF(0, -1500), 0, toleranceWorld: 2);
        using var stage = new StageControl(scene) { Size = new Size(640, 420) };
        stage.SetSelection([rectangle], rectangle);
        stage.SetSelectedElement(boundary);
        Point ToScreen(PointF point) => Point.Round(stage.WorldToScreen(point.X, point.Y));
        var start = PointF.Empty;
        var control1 = PointF.Empty;
        var control2 = PointF.Empty;
        var end = PointF.Empty;
        var exposesVirtualBezierHandles = boundary.Key.Kind == DrawingElementKind.BoundaryStroke
            && stage.TryGetEditableBezierWorldPoints(boundary, out start, out control1, out control2, out end)
            && stage.HitTestLineElementHandle(ToScreen(start), boundary) == EditHandleKind.LineStart
            && stage.HitTestLineElementHandle(ToScreen(end), boundary) == EditHandleKind.LineEnd
            && stage.HitTestLineElementHandle(ToScreen(control1), boundary) == EditHandleKind.BezierControl
            && stage.HitTestLineElementHandle(ToScreen(control2), boundary) == EditHandleKind.BezierControl2
            && stage.HitTestHandle(ToScreen(control1), rectangle) == EditHandleKind.BezierControl;
        if (!exposesVirtualBezierHandles)
        {
            throw new InvalidOperationException("A shape boundary did not expose editable endpoint and curve handles before materialization.");
        }

        var rectangleSnapshot = scene.CreateSnapshot();
        var adjustedStart = VectorUnits.Quantize(new PointF(start.X + 420, start.Y + 260));
        var straightEndpointAdjustment = MainForm.AdjustFillEdgeBezierHandle(
            start,
            control1,
            control2,
            end,
            EditHandleKind.LineStart,
            adjustedStart);
        var expectedStraightControl1 = new PointF(
            adjustedStart.X + (end.X - adjustedStart.X) / 3f,
            adjustedStart.Y + (end.Y - adjustedStart.Y) / 3f);
        var expectedStraightControl2 = new PointF(
            adjustedStart.X + (end.X - adjustedStart.X) * 2f / 3f,
            adjustedStart.Y + (end.Y - adjustedStart.Y) * 2f / 3f);
        var adjustedStartApplied = scene.TryConvertFillToBezierPath(rectangle)
            && scene.SetPathBezierSegment(
                rectangle,
                boundary.Key.PartIndex,
                straightEndpointAdjustment.Start,
                straightEndpointAdjustment.Control1,
                straightEndpointAdjustment.Control2,
                straightEndpointAdjustment.End,
                preserveStraightAdjacentSegments: true);
        var hasStoredStraightAdjustment = scene.TryGetPathBezierSegment(
            rectangle,
            boundary.Key.PartIndex,
            out var storedStraightAdjustment);
        var previousStraightParts = scene.GetPathBezierSegmentParts(rectangle)
            .Where(part => part.PartIndex != boundary.Key.PartIndex
                && PointsNear(part.End, adjustedStart))
            .ToArray();
        var previousStraightPart = previousStraightParts.FirstOrDefault();
        var expectedPreviousControl1 = new PointF(
            previousStraightPart.Start.X + (adjustedStart.X - previousStraightPart.Start.X) / 3f,
            previousStraightPart.Start.Y + (adjustedStart.Y - previousStraightPart.Start.Y) / 3f);
        var expectedPreviousControl2 = new PointF(
            previousStraightPart.Start.X + (adjustedStart.X - previousStraightPart.Start.X) * 2f / 3f,
            previousStraightPart.Start.Y + (adjustedStart.Y - previousStraightPart.Start.Y) * 2f / 3f);
        if (!adjustedStartApplied
            || !hasStoredStraightAdjustment
            || !PointsNear(storedStraightAdjustment.Start, adjustedStart)
            || !PointsNear(storedStraightAdjustment.Control1, VectorUnits.Quantize(expectedStraightControl1))
            || !PointsNear(storedStraightAdjustment.Control2, VectorUnits.Quantize(expectedStraightControl2))
            || !PointsNear(storedStraightAdjustment.End, end)
            || previousStraightParts.Length != 1
            || !PointsNear(previousStraightPart.Control1, VectorUnits.Quantize(expectedPreviousControl1))
            || !PointsNear(previousStraightPart.Control2, VectorUnits.Quantize(expectedPreviousControl2)))
        {
            throw new InvalidOperationException(
                "Moving a straight fill-boundary endpoint bent the active or preceding edge away from its new chord.");
        }

        scene.RestoreSnapshot(rectangleSnapshot);
        var adjustedEnd = VectorUnits.Quantize(new PointF(end.X - 240, end.Y + 180));
        var straightEndAdjustment = MainForm.AdjustFillEdgeBezierHandle(
            start,
            control1,
            control2,
            end,
            EditHandleKind.LineEnd,
            adjustedEnd);
        var expectedEndControl1 = new PointF(
            start.X + (adjustedEnd.X - start.X) / 3f,
            start.Y + (adjustedEnd.Y - start.Y) / 3f);
        var expectedEndControl2 = new PointF(
            start.X + (adjustedEnd.X - start.X) * 2f / 3f,
            start.Y + (adjustedEnd.Y - start.Y) * 2f / 3f);
        var adjustedEndApplied = scene.TryConvertFillToBezierPath(rectangle)
            && scene.SetPathBezierSegment(
                rectangle,
                boundary.Key.PartIndex,
                straightEndAdjustment.Start,
                straightEndAdjustment.Control1,
                straightEndAdjustment.Control2,
                straightEndAdjustment.End,
                preserveStraightAdjacentSegments: true);
        var hasStoredEndAdjustment = scene.TryGetPathBezierSegment(
            rectangle,
            boundary.Key.PartIndex,
            out var storedEndAdjustment);
        var followingStraightParts = scene.GetPathBezierSegmentParts(rectangle)
            .Where(part => part.PartIndex != boundary.Key.PartIndex
                && PointsNear(part.Start, adjustedEnd))
            .ToArray();
        var followingStraightPart = followingStraightParts.FirstOrDefault();
        var expectedFollowingControl1 = new PointF(
            adjustedEnd.X + (followingStraightPart.End.X - adjustedEnd.X) / 3f,
            adjustedEnd.Y + (followingStraightPart.End.Y - adjustedEnd.Y) / 3f);
        var expectedFollowingControl2 = new PointF(
            adjustedEnd.X + (followingStraightPart.End.X - adjustedEnd.X) * 2f / 3f,
            adjustedEnd.Y + (followingStraightPart.End.Y - adjustedEnd.Y) * 2f / 3f);
        if (!adjustedEndApplied
            || !hasStoredEndAdjustment
            || !PointsNear(straightEndAdjustment.Start, start)
            || !PointsNear(straightEndAdjustment.Control1, expectedEndControl1)
            || !PointsNear(straightEndAdjustment.Control2, expectedEndControl2)
            || !PointsNear(straightEndAdjustment.End, adjustedEnd)
            || !PointsNear(storedEndAdjustment.Control1, VectorUnits.Quantize(expectedEndControl1))
            || !PointsNear(storedEndAdjustment.Control2, VectorUnits.Quantize(expectedEndControl2))
            || followingStraightParts.Length != 1
            || !PointsNear(followingStraightPart.Control1, VectorUnits.Quantize(expectedFollowingControl1))
            || !PointsNear(followingStraightPart.Control2, VectorUnits.Quantize(expectedFollowingControl2)))
        {
            throw new InvalidOperationException(
                "Moving the opposite straight fill-boundary endpoint bent the active or following edge.");
        }

        var curvedControl1 = new PointF(control1.X, control1.Y - 360);
        var curvedControl2 = new PointF(control2.X, control2.Y + 240);
        var curvedEndpointAdjustment = MainForm.AdjustFillEdgeBezierHandle(
            start,
            curvedControl1,
            curvedControl2,
            end,
            EditHandleKind.LineEnd,
            new PointF(end.X + 180, end.Y + 120));
        var expectedCurvedControl2 = new PointF(
            curvedControl2.X + 180,
            curvedControl2.Y + 120);
        if (!PointsNear(curvedEndpointAdjustment.Control1, curvedControl1)
            || !PointsNear(curvedEndpointAdjustment.Control2, expectedCurvedControl2))
        {
            throw new InvalidOperationException(
                "Moving a curved fill-boundary endpoint did not preserve its attached handle offset.");
        }

        scene.RestoreSnapshot(rectangleSnapshot);
        if (!scene.TryConvertFillToBezierPath(rectangle)
            || !scene.TryGetPathBezierSegment(rectangle, boundary.Key.PartIndex, out var insertionSource))
        {
            throw new InvalidOperationException("Fill-edge anchor insertion regression could not prepare a straight cubic.");
        }

        const float straightInsertionParameter = 0.4f;
        var expectedStraightAnchor = VectorUnits.Quantize(Mix(
            insertionSource.Start,
            insertionSource.End,
            straightInsertionParameter));
        var rectangleOrder = scene.ObjectOrder[rectangle];
        var rectangleArgb = scene.Argb[rectangle];
        var rectangleAtoms = scene.AtomCount[rectangle];
        if (!scene.TryInsertPathBezierAnchor(
                rectangle,
                insertionSource.PartIndex,
                straightInsertionParameter,
                out var insertedStraightPartIndex,
                out var insertedStraightAnchor)
            || scene.ObjectCount != 1
            || scene.GetPathBezierSegmentParts(rectangle).Length != 5
            || !PointsNear(insertedStraightAnchor, expectedStraightAnchor)
            || !scene.TryGetPathBezierSegment(rectangle, insertionSource.PartIndex, out var firstStraightSplit)
            || !scene.TryGetPathBezierSegment(rectangle, insertedStraightPartIndex, out var secondStraightSplit)
            || !VectorScene.IsStraightBezierSegment(
                firstStraightSplit.Start,
                firstStraightSplit.Control1,
                firstStraightSplit.Control2,
                firstStraightSplit.End)
            || !VectorScene.IsStraightBezierSegment(
                secondStraightSplit.Start,
                secondStraightSplit.Control1,
                secondStraightSplit.Control2,
                secondStraightSplit.End)
            || !scene.TryDeletePathBezierAnchor(
                rectangle,
                insertedStraightPartIndex,
                startEndpoint: true,
                out var restoredStraightPartIndex)
            || scene.GetPathBezierSegmentParts(rectangle).Length != 4
            || !scene.TryGetPathBezierSegment(rectangle, restoredStraightPartIndex, out var restoredStraightPart)
            || !PointsNear(restoredStraightPart.Start, insertionSource.Start)
            || !PointsNear(restoredStraightPart.End, insertionSource.End)
            || !VectorScene.IsStraightBezierSegment(
                restoredStraightPart.Start,
                restoredStraightPart.Control1,
                restoredStraightPart.Control2,
                restoredStraightPart.End)
            || scene.ObjectOrder[rectangle] != rectangleOrder
            || scene.Argb[rectangle] != rectangleArgb
            || scene.AtomCount[rectangle] != rectangleAtoms)
        {
            throw new InvalidOperationException(
                "Adding and deleting a straight fill-edge anchor changed its line geometry or object metadata.");
        }

        scene.RestoreSnapshot(rectangleSnapshot);
        if (!scene.TryConvertFillToBezierPath(rectangle)
            || !scene.TryGetPathBezierSegment(rectangle, boundary.Key.PartIndex, out var cornerPrevious))
        {
            throw new InvalidOperationException("Fill-edge anchor deletion regression could not prepare a rectangle corner.");
        }

        var cornerFollowing = scene.GetPathBezierSegmentParts(rectangle)
            .Single(part => part.PartIndex != cornerPrevious.PartIndex
                && PointsNear(part.Start, cornerPrevious.End));
        if (!scene.TryDeletePathBezierAnchor(
                rectangle,
                cornerPrevious.PartIndex,
                startEndpoint: false,
                out var mergedCornerPartIndex)
            || !scene.TryGetPathBezierWorldContours(rectangle, out var triangleContours)
            || triangleContours.Length != 1
            || triangleContours[0].Length != 3
            || !scene.TryGetPathBezierSegment(rectangle, mergedCornerPartIndex, out var mergedCorner)
            || !PointsNear(mergedCorner.Start, cornerPrevious.Start)
            || !PointsNear(mergedCorner.End, cornerFollowing.End)
            || !VectorScene.IsStraightBezierSegment(
                mergedCorner.Start,
                mergedCorner.Control1,
                mergedCorner.Control2,
                mergedCorner.End)
            || scene.TryDeletePathBezierAnchor(
                rectangle,
                mergedCornerPartIndex,
                startEndpoint: false,
                out _))
        {
            throw new InvalidOperationException(
                "Deleting a straight fill corner did not create one straight replacement edge or enforce the three-anchor minimum.");
        }

        var fillScene = new VectorScene();
        fillScene.CreateEmpty();
        var fill = fillScene.AddPathObject(
            0,
            [
                new PointF(-240, -100),
                new PointF(40, -180),
                new PointF(260, -40),
                new PointF(180, 190),
                new PointF(-180, 170)
            ],
            0,
            Color.Teal,
            Color.Transparent,
            12);
        var originalSnapshot = fillScene.CreateSnapshot();
        var originalCount = fillScene.ObjectCount;
        var originalOrder = fillScene.ObjectOrder[fill];
        var originalArgb = fillScene.Argb[fill];
        var originalParts = fillScene.GetEditableFillBezierSegmentParts(fill);
        var wholeFillHit = new DrawingElementHit(
            new DrawingElementKey(fill, DrawingElementKind.Fill, 0),
            0,
            0,
            1);
        if (originalParts.Length != 5
            || !MainForm.IsWholeFillElementSelection(fillScene, 0, fill, [wholeFillHit])
            || MainForm.IsWholeFillElementSelection(fillScene, 0, fill, [DrawingElementHit.None])
            || fillScene.TryGetPathBezierWorldContours(fill, out _)
            || !fillScene.TryConvertFillToBezierPath(fill)
            || fillScene.ObjectCount != originalCount
            || fillScene.ObjectOrder[fill] != originalOrder
            || fillScene.Argb[fill] != originalArgb
            || fillScene.Stroke[fill] != 0
            || !fillScene.TryGetPathBezierSegment(fill, 0, out var topSegment))
        {
            throw new InvalidOperationException("A no-stroke irregular fill did not convert in place to editable cubic edges.");
        }

        var midpoint = new PointF(
            (topSegment.Start.X + topSegment.End.X) * 0.5f,
            (topSegment.Start.Y + topSegment.End.Y) * 0.5f);
        var outwardX = midpoint.X - fillScene.X[fill];
        var outwardY = midpoint.Y - fillScene.Y[fill];
        var outwardLength = MathF.Max(0.001f, MathF.Sqrt(outwardX * outwardX + outwardY * outwardY));
        var bulge = new PointF(outwardX / outwardLength * 320, outwardY / outwardLength * 320);
        var fillControl1 = new PointF(topSegment.Control1.X + bulge.X, topSegment.Control1.Y + bulge.Y);
        var fillControl2 = new PointF(topSegment.Control2.X + bulge.X, topSegment.Control2.Y + bulge.Y);
        if (!fillScene.SetPathBezierSegment(
                fill,
                topSegment.PartIndex,
                topSegment.Start,
                fillControl1,
                fillControl2,
                topSegment.End)
            || !fillScene.TryGetPathBezierSegment(fill, topSegment.PartIndex, out var editedSegment)
            || !PointsNear(editedSegment.Control1, VectorUnits.Quantize(fillControl1))
            || !PointsNear(editedSegment.Control2, VectorUnits.Quantize(fillControl2)))
        {
            throw new InvalidOperationException("Editing an irregular fill edge did not preserve its independent cubic controls.");
        }

        var curvedInsertionSnapshot = fillScene.CreateSnapshot();
        const float curvedQueryParameter = 0.37f;
        var queryP01 = Mix(editedSegment.Start, editedSegment.Control1, curvedQueryParameter);
        var queryP12 = Mix(editedSegment.Control1, editedSegment.Control2, curvedQueryParameter);
        var queryP23 = Mix(editedSegment.Control2, editedSegment.End, curvedQueryParameter);
        var curvedQueryPoint = Mix(
            Mix(queryP01, queryP12, curvedQueryParameter),
            Mix(queryP12, queryP23, curvedQueryParameter),
            curvedQueryParameter);
        if (!fillScene.TryGetClosestPointOnPathBezierSegment(
                fill,
                editedSegment.PartIndex,
                curvedQueryPoint,
                out var curvedInsertionParameter,
                out _,
                out var curvedInsertionDistance)
            || Math.Abs(curvedInsertionParameter - curvedQueryParameter) > 0.01f
            || curvedInsertionDistance > 1.5f)
        {
            throw new InvalidOperationException("Fill-edge click positioning did not recover the nearest cubic parameter.");
        }

        var p01 = VectorUnits.Quantize(Mix(editedSegment.Start, editedSegment.Control1, curvedInsertionParameter));
        var p12 = VectorUnits.Quantize(Mix(editedSegment.Control1, editedSegment.Control2, curvedInsertionParameter));
        var p23 = VectorUnits.Quantize(Mix(editedSegment.Control2, editedSegment.End, curvedInsertionParameter));
        var p012 = VectorUnits.Quantize(Mix(p01, p12, curvedInsertionParameter));
        var p123 = VectorUnits.Quantize(Mix(p12, p23, curvedInsertionParameter));
        var expectedCurvedAnchor = VectorUnits.Quantize(Mix(p012, p123, curvedInsertionParameter));
        if (!fillScene.TryInsertPathBezierAnchor(
                fill,
                editedSegment.PartIndex,
                curvedInsertionParameter,
                out var insertedCurvedPartIndex,
                out var insertedCurvedAnchor)
            || !fillScene.TryGetPathBezierSegment(fill, editedSegment.PartIndex, out var firstCurvedSplit)
            || !fillScene.TryGetPathBezierSegment(fill, insertedCurvedPartIndex, out var secondCurvedSplit)
            || fillScene.GetPathBezierSegmentParts(fill).Length != 6
            || !PointsNear(insertedCurvedAnchor, expectedCurvedAnchor)
            || !PointsNear(firstCurvedSplit.Start, editedSegment.Start)
            || !PointsNear(firstCurvedSplit.Control1, p01)
            || !PointsNear(firstCurvedSplit.Control2, p012)
            || !PointsNear(firstCurvedSplit.End, expectedCurvedAnchor)
            || !PointsNear(secondCurvedSplit.Start, expectedCurvedAnchor)
            || !PointsNear(secondCurvedSplit.Control1, p123)
            || !PointsNear(secondCurvedSplit.Control2, p23)
            || !PointsNear(secondCurvedSplit.End, editedSegment.End))
        {
            throw new InvalidOperationException("Adding a fill-edge anchor did not preserve the exact cubic subdivision.");
        }

        var insertedInitialSegment = new CubicDrawingPreviewSegment(
            secondCurvedSplit.Start,
            secondCurvedSplit.Control1,
            secondCurvedSplit.Control2,
            secondCurvedSplit.End);
        var draggedCurvedAnchor = VectorUnits.Quantize(new PointF(
            insertedCurvedAnchor.X + 85,
            insertedCurvedAnchor.Y + 65));
        var draggedInsertedSegment = MainForm.AdjustFillEdgeBezierHandle(
            insertedInitialSegment.Start,
            insertedInitialSegment.Control1,
            insertedInitialSegment.Control2,
            insertedInitialSegment.End,
            EditHandleKind.LineStart,
            draggedCurvedAnchor);
        var offsetHandle = MainForm.MoveHandleWithPointer(
            new PointF(100, 80),
            new PointF(96, 83),
            new PointF(101, 89));
        var unchangedGeometryRevision = fillScene.GeometryRevision;
        var unchangedSummaryRevision = fillScene.SummaryRevision;
        var subUnitSegment = new CubicDrawingPreviewSegment(
            VectorUnits.Quantize(new PointF(
                insertedInitialSegment.Start.X + 0.49f,
                insertedInitialSegment.Start.Y - 0.49f)),
            VectorUnits.Quantize(new PointF(
                insertedInitialSegment.Control1.X - 0.49f,
                insertedInitialSegment.Control1.Y + 0.49f)),
            VectorUnits.Quantize(new PointF(
                insertedInitialSegment.Control2.X + 0.49f,
                insertedInitialSegment.Control2.Y - 0.49f)),
            VectorUnits.Quantize(new PointF(
                insertedInitialSegment.End.X - 0.49f,
                insertedInitialSegment.End.Y + 0.49f)));
        var nextUnitSegment = subUnitSegment with
        {
            Start = VectorUnits.Quantize(new PointF(
                insertedInitialSegment.Start.X + 0.51f,
                insertedInitialSegment.Start.Y))
        };
        var unchangedSegmentAccepted = fillScene.SetPathBezierSegment(
            fill,
            insertedCurvedPartIndex,
            new PointF(insertedInitialSegment.Start.X + 0.49f, insertedInitialSegment.Start.Y - 0.49f),
            new PointF(insertedInitialSegment.Control1.X - 0.49f, insertedInitialSegment.Control1.Y + 0.49f),
            new PointF(insertedInitialSegment.Control2.X + 0.49f, insertedInitialSegment.Control2.Y - 0.49f),
            new PointF(insertedInitialSegment.End.X - 0.49f, insertedInitialSegment.End.Y + 0.49f));
        var resetCurvature = MainForm.ResetBezierCurvature(
            new PointF(0, 0),
            new PointF(90, 60));
        if (!MainForm.ShouldCommitFillEdgeBezierPointer(
                includesAnchorInsertion: true,
                insertedInitialSegment,
                insertedInitialSegment)
            || MainForm.ShouldCommitFillEdgeBezierPointer(
                includesAnchorInsertion: false,
                insertedInitialSegment,
                insertedInitialSegment)
            || !MainForm.ShouldCommitFillEdgeBezierPointer(
                includesAnchorInsertion: false,
                insertedInitialSegment,
                draggedInsertedSegment)
            || MainForm.ShouldApplyFillEdgeBezierPointer(
                insertedInitialSegment,
                insertedInitialSegment)
            || MainForm.ShouldApplyFillEdgeBezierPointer(
                insertedInitialSegment,
                subUnitSegment)
            || !MainForm.ShouldApplyFillEdgeBezierPointer(
                insertedInitialSegment,
                draggedInsertedSegment)
            || !MainForm.ShouldApplyFillEdgeBezierPointer(
                subUnitSegment,
                nextUnitSegment)
            || MainForm.ShouldApplyFillEdgeBezierPointer(
                nextUnitSegment,
                nextUnitSegment)
            || !MainForm.ShouldUpdateFillEdgeBezierPointer(
                MouseButtons.Left,
                includesAnchorInsertion: true,
                pointerMoved: false)
            || MainForm.ShouldUpdateFillEdgeBezierPointer(
                MouseButtons.Left,
                includesAnchorInsertion: false,
                pointerMoved: false)
            || !MainForm.ShouldUpdateFillEdgeBezierPointer(
                MouseButtons.Left,
                includesAnchorInsertion: false,
                pointerMoved: true)
            || MainForm.ShouldUpdateFillEdgeBezierPointer(
                MouseButtons.Right,
                includesAnchorInsertion: true,
                pointerMoved: true)
            || !MainForm.ShouldUpdateSelectionPointer(
                EditHandleKind.LineStart,
                pointerMoved: true,
                dragThresholdExceeded: false)
            || MainForm.ShouldUpdateSelectionPointer(
                EditHandleKind.LineStart,
                pointerMoved: false,
                dragThresholdExceeded: true)
            || !MainForm.ShouldUpdateSelectionPointer(
                EditHandleKind.None,
                pointerMoved: true,
                dragThresholdExceeded: false)
            || !MainForm.ShouldUpdateSelectionPointer(
                EditHandleKind.None,
                pointerMoved: true,
                dragThresholdExceeded: true)
            || !MainForm.FillEdgeOverlayOwnsPointer(
                new FillEdgeBezierOverlayHit(0, EditHandleKind.BezierControl),
                selectableLineHit: true)
            || MainForm.FillEdgeOverlayOwnsPointer(
                new FillEdgeBezierOverlayHit(0, EditHandleKind.None),
                selectableLineHit: true)
            || !MainForm.FillEdgeOverlayOwnsPointer(
                new FillEdgeBezierOverlayHit(0, EditHandleKind.None),
                selectableLineHit: false)
            || MainForm.FillEdgeOverlayOwnsPointer(
                FillEdgeBezierOverlayHit.None,
                selectableLineHit: false)
            || !MainForm.IsBezierCurvatureResetGesture(
                MouseButtons.Left,
                altPressed: true,
                EditHandleKind.BezierControl)
            || !MainForm.IsBezierCurvatureResetGesture(
                MouseButtons.Left,
                altPressed: true,
                EditHandleKind.BezierControl2)
            || MainForm.IsBezierCurvatureResetGesture(
                MouseButtons.Left,
                altPressed: false,
                EditHandleKind.BezierControl)
            || MainForm.IsBezierCurvatureResetGesture(
                MouseButtons.Right,
                altPressed: true,
                EditHandleKind.BezierControl)
            || MainForm.IsBezierCurvatureResetGesture(
                MouseButtons.Left,
                altPressed: true,
                EditHandleKind.LineStart)
            || !PointsWithin(resetCurvature.Start, new PointF(0, 0), 0.01f)
            || !PointsWithin(resetCurvature.Control1, new PointF(30, 20), 0.01f)
            || !PointsWithin(resetCurvature.Control2, new PointF(60, 40), 0.01f)
            || !PointsWithin(resetCurvature.End, new PointF(90, 60), 0.01f)
            || !PointsWithin(offsetHandle, new PointF(105, 86), 0.01f)
            || !unchangedSegmentAccepted
            || fillScene.GeometryRevision != unchangedGeometryRevision
            || fillScene.SummaryRevision != unchangedSummaryRevision
            || !fillScene.SetPathBezierSegment(
                fill,
                insertedCurvedPartIndex,
                draggedInsertedSegment.Start,
                draggedInsertedSegment.Control1,
                draggedInsertedSegment.Control2,
                draggedInsertedSegment.End,
                preserveStraightAdjacentSegments: true)
            || !fillScene.TryGetPathBezierSegment(fill, editedSegment.PartIndex, out var draggedFirstSplit)
            || !fillScene.TryGetPathBezierSegment(fill, insertedCurvedPartIndex, out var draggedSecondSplit)
            || fillScene.GetPathBezierSegmentParts(fill).Length != 6
            || !PointsNear(draggedFirstSplit.End, draggedCurvedAnchor)
            || !PointsNear(draggedSecondSplit.Start, draggedCurvedAnchor))
        {
            throw new InvalidOperationException(
                "Dragging immediately after fill-edge anchor insertion did not preserve one commit or the shared anchor.");
        }
        fillScene.RestoreSnapshot(curvedInsertionSnapshot);

        var curvedSnapshot = fillScene.CreateSnapshot();
        fillScene.SetPathBezierSegment(
            fill,
            topSegment.PartIndex,
            topSegment.Start,
            new PointF(fillControl1.X + 100, fillControl1.Y),
            fillControl2,
            topSegment.End);
        fillScene.RestoreSnapshot(curvedSnapshot);
        if (!fillScene.TryGetPathBezierSegment(fill, topSegment.PartIndex, out var restoredSegment)
            || !PointsNear(restoredSegment.Control1, editedSegment.Control1)
            || !PointsNear(restoredSegment.Control2, editedSegment.Control2))
        {
            throw new InvalidOperationException("Snapshot restore lost exact fill-edge cubic controls.");
        }

        using var fillStage = new StageControl(fillScene) { Size = new Size(640, 420) };
        fillStage.SetVisibleWorldWidth(800);
        var overlaySegments = fillScene.GetEditableFillBezierSegmentParts(fill)
            .Select(part => new FillEdgeBezierOverlaySegment(
                part.PartIndex,
                part.Start,
                part.Control1,
                part.Control2,
                part.End))
            .ToArray();
        fillStage.SetSelection([fill], fill);
        fillStage.SetFillEdgeBezierOverlay(fill, overlaySegments, restoredSegment.PartIndex);
        var unchangedOverlayRevision = fillStage.FillEdgeBezierOverlayRevision;
        fillStage.SetFillEdgeBezierOverlay(fill, overlaySegments, restoredSegment.PartIndex);
        var controlHit = fillStage.HitTestFillEdgeBezierOverlay(
            Point.Round(fillStage.WorldToScreen(restoredSegment.Control1.X, restoredSegment.Control1.Y)));
        var startHit = fillStage.HitTestFillEdgeBezierOverlay(
            Point.Round(fillStage.WorldToScreen(restoredSegment.Start.X, restoredSegment.Start.Y)));
        var endHit = fillStage.HitTestFillEdgeBezierOverlay(
            Point.Round(fillStage.WorldToScreen(restoredSegment.End.X, restoredSegment.End.Y)));
        var curveP01 = Mix(restoredSegment.Start, restoredSegment.Control1, 0.5f);
        var curveP12 = Mix(restoredSegment.Control1, restoredSegment.Control2, 0.5f);
        var curveP23 = Mix(restoredSegment.Control2, restoredSegment.End, 0.5f);
        var curveMidpoint = Mix(Mix(curveP01, curveP12, 0.5f), Mix(curveP12, curveP23, 0.5f), 0.5f);
        var curveHit = fillStage.HitTestFillEdgeBezierOverlay(
            Point.Round(fillStage.WorldToScreen(curveMidpoint.X, curveMidpoint.Y)));
        var inactiveSegment = overlaySegments.First(segment =>
            segment.PartIndex != restoredSegment.PartIndex
            && !PointsNear(segment.Start, restoredSegment.Start)
            && !PointsNear(segment.Start, restoredSegment.End));
        var inactiveAnchorHit = fillStage.HitTestFillEdgeBezierOverlay(
            Point.Round(fillStage.WorldToScreen(inactiveSegment.Start.X, inactiveSegment.Start.Y)));
        var inactiveControlHit = fillStage.HitTestFillEdgeBezierOverlay(
            Point.Round(fillStage.WorldToScreen(inactiveSegment.Control1.X, inactiveSegment.Control1.Y)));
        if (!fillStage.FillEdgeBezierOverlayVisible
            || fillStage.FillEdgeBezierOverlayTargetObject != fill
            || fillStage.FillEdgeBezierOverlayActivePartIndex != restoredSegment.PartIndex
            || fillStage.FillEdgeBezierOverlayRevision != unchangedOverlayRevision
            || controlHit.PartIndex != restoredSegment.PartIndex
            || controlHit.Handle != EditHandleKind.BezierControl
            || startHit.Handle != EditHandleKind.LineStart
            || endHit.Handle != EditHandleKind.LineEnd
            || curveHit.PartIndex != restoredSegment.PartIndex
            || curveHit.Handle != EditHandleKind.None
            || !inactiveAnchorHit.IsValid
            || inactiveAnchorHit.Handle is not (EditHandleKind.LineStart or EditHandleKind.LineEnd)
            || inactiveControlHit.PartIndex != inactiveSegment.PartIndex
            || inactiveControlHit.Handle != EditHandleKind.BezierControl
            || !MainForm.IsFillEdgeBezierAnchorInsertionGesture(MouseButtons.Left, controlPressed: true, curveHit)
            || MainForm.IsFillEdgeBezierAnchorInsertionGesture(MouseButtons.Left, controlPressed: false, curveHit)
            || MainForm.IsFillEdgeBezierAnchorInsertionGesture(MouseButtons.Right, controlPressed: true, curveHit)
            || MainForm.IsFillEdgeBezierAnchorInsertionGesture(MouseButtons.Left, controlPressed: true, controlHit)
            || MainForm.IsFillEdgeBezierAnchorInsertionGesture(MouseButtons.Left, controlPressed: true, startHit)
            || !MainForm.IsFillEdgeBezierAnchorDeletionGesture(MouseButtons.Right, controlPressed: true, startHit)
            || !MainForm.IsFillEdgeBezierAnchorDeletionGesture(MouseButtons.Right, controlPressed: true, endHit)
            || MainForm.IsFillEdgeBezierAnchorDeletionGesture(MouseButtons.Right, controlPressed: false, startHit)
            || MainForm.IsFillEdgeBezierAnchorDeletionGesture(MouseButtons.Left, controlPressed: true, startHit)
            || MainForm.IsFillEdgeBezierAnchorDeletionGesture(MouseButtons.Right, controlPressed: true, curveHit)
            || MainForm.IsFillEdgeBezierAnchorDeletionGesture(MouseButtons.Right, controlPressed: true, controlHit)
            || fillStage.SelectionHighlightAnimating)
        {
            throw new InvalidOperationException(
                $"The fill-edge cubic overlay or Ctrl anchor gestures were inconsistent: active={restoredSegment.PartIndex}, curve={curveHit}, control={controlHit}, start={startHit}, end={endHit}, inactive={inactiveSegment.PartIndex}, anchor={inactiveAnchorHit}, inactiveControl={inactiveControlHit}.");
        }

        var ownedOverlaySegments = (FillEdgeBezierOverlaySegment[])overlaySegments.Clone();
        fillStage.SetOwnedFillEdgeBezierOverlay(fill, ownedOverlaySegments, restoredSegment.PartIndex);
        var ownedNoOpRevision = fillStage.FillEdgeBezierOverlayRevision;
        for (var iteration = 0; iteration < 16; iteration++)
        {
            fillStage.SetOwnedFillEdgeBezierOverlay(fill, ownedOverlaySegments, restoredSegment.PartIndex);
        }

        var ownedNoOpAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var ownedNoOpWatch = Stopwatch.StartNew();
        for (var iteration = 0; iteration < 1024; iteration++)
        {
            fillStage.SetOwnedFillEdgeBezierOverlay(fill, ownedOverlaySegments, restoredSegment.PartIndex);
        }
        ownedNoOpWatch.Stop();
        var ownedNoOpAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - ownedNoOpAllocatedBefore;
        var ownedNoOpAverageMilliseconds = ownedNoOpWatch.Elapsed.TotalMilliseconds / 1024;
        if (fillStage.FillEdgeBezierOverlayRevision != ownedNoOpRevision
            || ownedNoOpAllocatedBytes > 4096
            || ownedNoOpAverageMilliseconds > 0.05)
        {
            throw new InvalidOperationException(
                $"An unchanged owned fill-edge overlay repeated work: revision={ownedNoOpRevision}->{fillStage.FillEdgeBezierOverlayRevision}, allocated={ownedNoOpAllocatedBytes}, averageMs={ownedNoOpAverageMilliseconds:0.0000}.");
        }

        var changedOwnedOverlaySegments = (FillEdgeBezierOverlaySegment[])overlaySegments.Clone();
        changedOwnedOverlaySegments[0] = changedOwnedOverlaySegments[0] with
        {
            Control1 = new PointF(
                changedOwnedOverlaySegments[0].Control1.X + 1,
                changedOwnedOverlaySegments[0].Control1.Y)
        };
        fillStage.SetOwnedFillEdgeBezierOverlay(fill, changedOwnedOverlaySegments, restoredSegment.PartIndex);
        if (fillStage.FillEdgeBezierOverlayRevision != ownedNoOpRevision + 1
            || !ReferenceEquals(changedOwnedOverlaySegments, fillStage.FillEdgeBezierOverlaySegments))
        {
            throw new InvalidOperationException("The Stage did not adopt one changed owned fill-edge overlay array exactly once.");
        }

        Console.WriteLine($"fill_edge_overlay_noop_allocated_bytes={ownedNoOpAllocatedBytes}");
        Console.WriteLine($"fill_edge_overlay_noop_avg_ms={ownedNoOpAverageMilliseconds:0.0000}");
        var clearOverlayRevision = fillStage.FillEdgeBezierOverlayRevision;
        fillStage.ClearFillEdgeBezierOverlay();
        var clearedOverlayRevision = fillStage.FillEdgeBezierOverlayRevision;
        fillStage.ClearFillEdgeBezierOverlay();
        if (fillStage.FillEdgeBezierOverlayVisible
            || clearedOverlayRevision != clearOverlayRevision + 1
            || fillStage.FillEdgeBezierOverlayRevision != clearedOverlayRevision)
        {
            throw new InvalidOperationException("The fill-edge cubic overlay did not clear its editing state.");
        }

        const int shortPartIndex = 97;
        var shortSegment = new FillEdgeBezierOverlaySegment(
            shortPartIndex,
            new PointF(0, 0),
            new PointF(10, 0),
            new PointF(20, 0),
            new PointF(30, 0));
        fillStage.SetFillEdgeBezierOverlay(fill, [shortSegment], shortPartIndex);
        var shortStartHit = fillStage.HitTestFillEdgeBezierOverlay(
            Point.Round(fillStage.WorldToScreen(shortSegment.Start.X, shortSegment.Start.Y)));
        var shortControlHit = fillStage.HitTestFillEdgeBezierOverlay(
            Point.Round(fillStage.WorldToScreen(shortSegment.Control1.X, shortSegment.Control1.Y)));
        var shortEndHit = fillStage.HitTestFillEdgeBezierOverlay(
            Point.Round(fillStage.WorldToScreen(shortSegment.End.X, shortSegment.End.Y)));
        if (shortStartHit.Handle != EditHandleKind.LineStart
            || shortControlHit.Handle != EditHandleKind.BezierControl
            || shortEndHit.Handle != EditHandleKind.LineEnd)
        {
            throw new InvalidOperationException(
                $"Overlapping short-segment handles did not choose the nearest anchor/control: start={shortStartHit}, control={shortControlHit}, end={shortEndHit}.");
        }
        fillStage.ClearFillEdgeBezierOverlay();

        var coverageScene = new VectorScene();
        coverageScene.CreateEmpty();
        var coverageFill = coverageScene.AddPathObject(
            0,
            [
                new PointF(-220, -120),
                new PointF(220, -120),
                new PointF(220, 120),
                new PointF(-220, 120)
            ],
            0,
            Color.Teal,
            Color.Transparent,
            12);
        var coverageParts = coverageScene.GetEditableFillBezierSegmentParts(coverageFill);
        foreach (var part in coverageParts.Take(2))
        {
            coverageScene.AddCubicCurveSegment(
                0,
                part.Start,
                part.Control1,
                part.Control2,
                part.End,
                VectorUnits.StrokePointsToUnits(3),
                Color.Transparent,
                Color.White,
                6);
        }

        var exposedParts = coverageScene.GetExposedFillBezierSegmentPieces(coverageFill, 0);
        using var coverageStage = new StageControl(coverageScene) { Size = new Size(640, 420) };
        coverageStage.SetVisibleWorldWidth(800);
        coverageStage.SetSelection([coverageFill], coverageFill);
        coverageStage.SetFillEdgeBezierOverlay(
            coverageFill,
            exposedParts.Select(part => new FillEdgeBezierOverlaySegment(
                part.PieceIndex,
                part.Start,
                part.Control1,
                part.Control2,
                part.End)).ToArray(),
            exposedParts.FirstOrDefault().PieceIndex);
        var coveredControlHit = coverageStage.HitTestFillEdgeBezierOverlay(
            Point.Round(coverageStage.WorldToScreen(
                coverageParts[0].Control1.X,
                coverageParts[0].Control1.Y)));
        var exposedControlHit = coverageStage.HitTestFillEdgeBezierOverlay(
            Point.Round(coverageStage.WorldToScreen(
                exposedParts[0].Control1.X,
                exposedParts[0].Control1.Y)));
        if (coverageParts.Length != 4
            || exposedParts.Length != 2
            || exposedParts.Any(part => part.SourcePartIndex is 0 or 1)
            || coveredControlHit.IsValid
            || exposedControlHit.PartIndex != exposedParts[0].PieceIndex
            || exposedControlHit.Handle != EditHandleKind.BezierControl)
        {
            throw new InvalidOperationException(
                $"A partially outlined fill did not expose only its open edges: total={coverageParts.Length}, exposed={string.Join(',', exposedParts.Select(part => part.SourcePartIndex))}, coveredHit={coveredControlHit}, exposedHit={exposedControlHit}.");
        }

        var partitionScene = new VectorScene();
        partitionScene.CreateEmpty();
        var partitionFill = partitionScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(440, 240),
            0,
            0,
            Color.Teal,
            Color.Transparent,
            12,
            ShapeKind.Rectangle);
        var partitionStroke = partitionScene.AddFreehandStroke(
            0,
            [
                new PointF(-340, -240),
                new PointF(60, -240),
                new PointF(60, 40),
                new PointF(-340, 40),
                new PointF(-340, -240)
            ],
            VectorUnits.StrokePointsToUnits(3),
            Color.White,
            brushStroke: false,
            12);
        var partitionSnapshot = partitionScene.CreateSnapshot();
        var wholeBoundaryPieces = partitionScene.GetExposedFillBezierSegmentPieces(partitionFill, 0);
        var wholeLeftPieces = wholeBoundaryPieces
            .Where(part => part.SourcePartIndex == 3)
            .OrderBy(part => part.StartT)
            .ToArray();
        var lowerLeftPiece = wholeLeftPieces.FirstOrDefault(part =>
            PointsWithin(part.Start, new PointF(-220, 120), 1.5f)
            && PointsWithin(part.End, new PointF(-220, 40), 1.5f));
        PathBezierSegmentPart isolatedLowerLeft = default;
        PathBezierSegmentPart unchangedUpperLeft = default;
        PathBezierSegmentPart movedLowerLeft = default;
        PathBezierSegmentPart movedUpperLeft = default;
        if (wholeLeftPieces.Length != 2
            || !lowerLeftPiece.EndIsVirtualAnchor
            || !partitionScene.TryConvertFillToBezierPath(partitionFill)
            || !partitionScene.TryMaterializePathBezierSegmentInterval(
                partitionFill,
                lowerLeftPiece.SourcePartIndex,
                lowerLeftPiece.StartT,
                lowerLeftPiece.EndT,
                out var isolatedLowerLeftPart)
            || !partitionScene.TryGetPathBezierSegment(
                partitionFill,
                isolatedLowerLeftPart,
                out isolatedLowerLeft)
            || !partitionScene.TryGetPathBezierSegment(
                partitionFill,
                isolatedLowerLeftPart + 1,
                out unchangedUpperLeft))
        {
            throw new InvalidOperationException(
                $"A whole-fill boundary edit did not preserve its line-intersection split: leftPieces={string.Join(',', wholeLeftPieces.Select(part => $"{part.StartT:F3}-{part.EndT:F3}"))}.");
        }

        var cachedPointerPieces = partitionScene.GetExposedFillBezierSegmentPieces(partitionFill, 0);
        var cachedLowerPointerPiece = cachedPointerPieces.FirstOrDefault(piece =>
            piece.SourcePartIndex == isolatedLowerLeftPart);
        var movedLowerStart = new PointF(-280, 150);
        var adjustedLowerLeft = MainForm.AdjustFillEdgeBezierHandle(
            isolatedLowerLeft.Start,
            isolatedLowerLeft.Control1,
            isolatedLowerLeft.Control2,
            isolatedLowerLeft.End,
            EditHandleKind.LineStart,
            movedLowerStart);
        var lowerLeftChanged = partitionScene.SetPathBezierSegment(
                partitionFill,
                isolatedLowerLeftPart,
                adjustedLowerLeft.Start,
                adjustedLowerLeft.Control1,
                adjustedLowerLeft.Control2,
                adjustedLowerLeft.End,
                preserveStraightAdjacentSegments: true);
        var refreshedPointerPieces = lowerLeftChanged
            ? partitionScene.RefreshFillBezierSegmentPieces(partitionFill, cachedPointerPieces)
            : [];
        var refreshedLowerPointerPiece = refreshedPointerPieces.FirstOrDefault(piece =>
            piece.PieceIndex == cachedLowerPointerPiece.PieceIndex
            && piece.SourcePartIndex == cachedLowerPointerPiece.SourcePartIndex);
        if (!lowerLeftChanged
            || !partitionScene.TryGetPathBezierSegment(
                partitionFill,
                isolatedLowerLeftPart,
                out movedLowerLeft)
            || !partitionScene.TryGetPathBezierSegment(
                partitionFill,
                isolatedLowerLeftPart + 1,
                out movedUpperLeft)
            || !PointsWithin(movedLowerLeft.Start, movedLowerStart, 0.01f)
            || !PointsWithin(movedLowerLeft.End, isolatedLowerLeft.End, 0.01f)
            || cachedPointerPieces.Length == 0
            || cachedLowerPointerPiece.SourcePartIndex != isolatedLowerLeftPart
            || refreshedPointerPieces.Length != cachedPointerPieces.Length
            || !PointsWithin(refreshedLowerPointerPiece.Start, movedLowerStart, 0.01f)
            || !PointsWithin(movedUpperLeft.Start, unchangedUpperLeft.Start, 0.01f)
            || !PointsWithin(movedUpperLeft.Control1, unchangedUpperLeft.Control1, 0.01f)
            || !PointsWithin(movedUpperLeft.Control2, unchangedUpperLeft.Control2, 0.01f)
            || !PointsWithin(movedUpperLeft.End, unchangedUpperLeft.End, 0.01f))
        {
            throw new InvalidOperationException(
                $"Dragging below a fill/line intersection changed the upper boundary: lower={movedLowerLeft}, upper={movedUpperLeft}, expectedUpper={unchangedUpperLeft}.");
        }

        for (var iteration = 0; iteration < 8; iteration++)
        {
            _ = partitionScene.RefreshFillBezierSegmentPieces(partitionFill, cachedPointerPieces);
        }

        const int fillEdgeRefreshIterations = 256;
        var fillEdgeRefreshAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var fillEdgeRefreshWatch = Stopwatch.StartNew();
        FillBezierSegmentPiece[] measuredRefreshedPieces = [];
        for (var iteration = 0; iteration < fillEdgeRefreshIterations; iteration++)
        {
            measuredRefreshedPieces = partitionScene.RefreshFillBezierSegmentPieces(
                partitionFill,
                cachedPointerPieces);
        }
        fillEdgeRefreshWatch.Stop();
        var fillEdgeRefreshAllocatedBytes = GC.GetAllocatedBytesForCurrentThread()
            - fillEdgeRefreshAllocatedBefore;
        var fillEdgeRefreshAllocatedBytesPerUpdate = fillEdgeRefreshAllocatedBytes
            / (double)fillEdgeRefreshIterations;
        var fillEdgeRefreshAverageMilliseconds = fillEdgeRefreshWatch.Elapsed.TotalMilliseconds
            / fillEdgeRefreshIterations;
        if (!measuredRefreshedPieces.SequenceEqual(refreshedPointerPieces)
            || fillEdgeRefreshAllocatedBytesPerUpdate > 4096
            || fillEdgeRefreshAverageMilliseconds > 0.5)
        {
            throw new InvalidOperationException(
                $"The lightweight fill-edge overlay refresh exceeded its budget or changed geometry: allocatedPerUpdate={fillEdgeRefreshAllocatedBytesPerUpdate:0.0}, averageMs={fillEdgeRefreshAverageMilliseconds:0.0000}.");
        }

        Console.WriteLine($"fill_edge_overlay_refresh_allocated_bytes_per_update={fillEdgeRefreshAllocatedBytesPerUpdate:0.0}");
        Console.WriteLine($"fill_edge_overlay_refresh_avg_ms={fillEdgeRefreshAverageMilliseconds:0.0000}");
        partitionScene.RestoreSnapshot(partitionSnapshot);

        var inwardReferenceContours = partitionScene.GetObjectBoundaryContours(partitionFill);
        var inwardLowerLeftPiece = partitionScene.GetExposedFillBezierSegmentPieces(partitionFill, 0)
            .FirstOrDefault(part =>
                part.SourcePartIndex == 3
                && PointsWithin(part.Start, new PointF(-220, 120), 1.5f)
                && PointsWithin(part.End, new PointF(-220, 40), 1.5f));
        var inwardCorner = new PointF(-100, 150);
        PathBezierSegmentPart inwardSegment = default;
        if (inwardLowerLeftPiece.EndIsVirtualAnchor
            && partitionScene.TryConvertFillToBezierPath(partitionFill)
            && partitionScene.TryMaterializePathBezierSegmentInterval(
                partitionFill,
                inwardLowerLeftPiece.SourcePartIndex,
                inwardLowerLeftPiece.StartT,
                inwardLowerLeftPiece.EndT,
                out var inwardPartIndex)
            && partitionScene.TryGetPathBezierSegment(partitionFill, inwardPartIndex, out var inwardSource))
        {
            var inwardAdjusted = MainForm.AdjustFillEdgeBezierHandle(
                inwardSource.Start,
                inwardSource.Control1,
                inwardSource.Control2,
                inwardSource.End,
                EditHandleKind.LineStart,
                inwardCorner);
            partitionScene.SetPathBezierSegment(
                partitionFill,
                inwardPartIndex,
                inwardAdjusted.Start,
                inwardAdjusted.Control1,
                inwardAdjusted.Control2,
                inwardAdjusted.End,
                preserveStraightAdjacentSegments: true);
            inwardSegment = inwardSource;
        }

        var inwardProbe = new PointF(-180, 0);
        var inwardProbeBeforeNormalization = partitionScene.FillContainsPoint(partitionFill, inwardProbe);
        var inwardNormalized = partitionScene.NormalizeFillBoundaryOverlaps(
            partitionFill,
            inwardReferenceContours);
        var inwardProbeAfterNormalization = partitionScene.FillContainsPoint(partitionFill, inwardProbe);
        if (!inwardLowerLeftPiece.EndIsVirtualAnchor
            || inwardSegment.End == PointF.Empty
            || !inwardProbeBeforeNormalization
            || !inwardProbeAfterNormalization)
        {
            throw new InvalidOperationException(
                $"Moving a split fill corner inward created a triangular hole: before={inwardProbeBeforeNormalization}, normalized={inwardNormalized}, after={inwardProbeAfterNormalization}.");
        }
        partitionScene.RestoreSnapshot(partitionSnapshot);

        var inwardIntersectionSelectedPart = partitionScene.HitTestElement(
            new PointF(160, 80),
            0,
            toleranceWorld: 2);
        var inwardIntersectionPiece = partitionScene.GetExposedFillBezierSegmentPieces(partitionFill, 0)
            .FirstOrDefault(part =>
                part.SourcePartIndex == 3
                && PointsWithin(part.Start, new PointF(-220, 120), 1.5f)
                && PointsWithin(part.End, new PointF(-220, 40), 1.5f));
        var inwardIntersectionAnchor = new PointF(-100, 150);
        var inwardIntersectionPartResolved = partitionScene.TryResolveFillPartForBezierSegmentPiece(
            partitionFill,
            0,
            inwardIntersectionPiece,
            out var inwardIntersectionFillPartIndex);
        var inwardIntersectionTarget = partitionFill;
        var inwardIntersectionHandle = EditHandleKind.LineEnd;
        var inwardIntersectionPartIndex = -1;
        var inwardIntersectionPrepared = inwardIntersectionSelectedPart.Key.Kind == DrawingElementKind.Fill
            && inwardIntersectionPartResolved
            && inwardIntersectionFillPartIndex == inwardIntersectionSelectedPart.Key.PartIndex
            && partitionScene.TryConvertFillToBezierPath(inwardIntersectionTarget)
            && partitionScene.TryMaterializePathBezierSegmentInterval(
                inwardIntersectionTarget,
                inwardIntersectionPiece.SourcePartIndex,
                inwardIntersectionPiece.StartT,
                inwardIntersectionPiece.EndT,
                out inwardIntersectionPartIndex);
        var inwardIntersectionReference = inwardIntersectionPrepared
            ? partitionScene.GetObjectBoundaryContours(inwardIntersectionTarget)
            : [];
        var inwardIntersection = inwardIntersectionPrepared
            ? partitionScene.CaptureStrokeIntersectionsAtFillAnchor(
                inwardIntersectionTarget,
                inwardIntersectionPartIndex,
                startEndpoint: inwardIntersectionHandle == EditHandleKind.LineStart,
                0)
            : new SharedBoundaryIntersection(PointF.Empty, [], [], [], -1, -1);
        PathBezierSegmentPart inwardIntersectionSource = default;
        var inwardIntersectionMoved = inwardIntersection.OwnerPartIndex >= 0
            && partitionScene.TryGetPathBezierSegment(
                inwardIntersection.OwnerObjectIndex,
                inwardIntersection.OwnerPartIndex,
                out inwardIntersectionSource);
        if (inwardIntersectionMoved)
        {
            var adjusted = MainForm.AdjustFillEdgeBezierHandle(
                inwardIntersectionSource.Start,
                inwardIntersectionSource.Control1,
                inwardIntersectionSource.Control2,
                inwardIntersectionSource.End,
                inwardIntersectionHandle,
                inwardIntersectionAnchor);
            inwardIntersectionMoved = partitionScene.SetPathBezierSegment(
                    inwardIntersection.OwnerObjectIndex,
                    inwardIntersection.OwnerPartIndex,
                    adjusted.Start,
                    adjusted.Control1,
                    adjusted.Control2,
                    adjusted.End,
                    rebuildGeometryIndex: false,
                    preserveStraightAdjacentSegments: true)
                && partitionScene.UpdateSharedBoundaryIntersection(
                    inwardIntersection,
                    inwardIntersectionAnchor,
                    rebuildGeometryIndex: false);
        }

        var inwardIntersectionStableMembership = partitionScene.ObjectCount == 2
            && inwardIntersection.PathAnchors.Length == 0
            && inwardIntersection.LineAnchors.Length == 0
            && inwardIntersection.FreeformAnchors.Length == 1;
        var inwardIntersectionProbe = new PointF(-80, 80);
        var inwardIntersectionPreviewFilled = inwardIntersectionMoved
            && Enumerable.Range(0, partitionScene.ObjectCount).Any(index =>
                partitionScene.IsObjectActive(index, 0)
                && partitionScene.HasFill(index)
                && partitionScene.FillContainsPoint(index, inwardIntersectionProbe));
        var inwardIntersectionFillStates = string.Join(
            "; ",
            Enumerable.Range(0, partitionScene.ObjectCount)
                .Where(index => partitionScene.IsObjectActive(index, 0) && partitionScene.HasFill(index))
                .Select(index =>
                    $"{index}/order={partitionScene.ObjectOrder[index]}/sub={partitionScene.ObjectSubOrder[index]:F3}/contains={partitionScene.FillContainsPoint(index, inwardIntersectionProbe)}"));
        var inwardIntersectionNormalized = inwardIntersectionMoved
            && partitionScene.NormalizeFillBoundaryOverlaps(
                inwardIntersection.OwnerObjectIndex,
                inwardIntersectionReference,
                rebuildGeometryIndex: false);
        partitionScene.CompleteDeferredBuild();
        var inwardIntersectionMerged = inwardIntersectionMoved
            ? partitionScene.MergeSameColorFillsAround(
                inwardIntersection.OwnerObjectIndex,
                connectNearby: true,
                frame: 0)
            : -1;
        var inwardIntersectionCommittedFilled = inwardIntersectionMoved
            && (uint)inwardIntersectionMerged < partitionScene.ObjectCount
            && partitionScene.FillContainsPoint(inwardIntersectionMerged, inwardIntersectionProbe);
        if (!inwardIntersectionPrepared
            || !inwardIntersectionMoved
            || !inwardIntersectionStableMembership
            || !inwardIntersectionPreviewFilled
            || !inwardIntersectionCommittedFilled)
        {
            throw new InvalidOperationException(
                $"Moving a local lower fill/stroke intersection inward created extra objects or a triangular hole: prepared={inwardIntersectionPrepared}, target={inwardIntersectionTarget}/{inwardIntersection.OwnerObjectIndex}:{inwardIntersection.OwnerPartIndex}, moved={inwardIntersectionMoved}, stable={inwardIntersectionStableMembership}, paths={inwardIntersection.PathAnchors.Length}, lines={inwardIntersection.LineAnchors.Length}, freeforms={inwardIntersection.FreeformAnchors.Length}, objects={partitionScene.ObjectCount}, preview={inwardIntersectionPreviewFilled}, normalized={inwardIntersectionNormalized}, merged={inwardIntersectionMerged}, committed={inwardIntersectionCommittedFilled}, fillStates=[{inwardIntersectionFillStates}].");
        }
        partitionScene.RestoreSnapshot(partitionSnapshot);

        var outerPartitionHit = partitionScene.HitTestElement(new PointF(160, 80), 0, toleranceWorld: 2);
        var innerPartitionHit = partitionScene.HitTestElement(PointF.Empty, 0, toleranceWorld: 2);
        var outerFillPartProbes = MainForm.BuildFillPartSelectionProbes(
            partitionScene.GetFillPartContours(outerPartitionHit, 0));
        var partitionFillParts = partitionScene.GetFillParts(partitionFill, 0);
        var outerBoundaryParts = outerPartitionHit.Key.Kind == DrawingElementKind.Fill
            ? partitionScene.GetExposedFillBezierSegmentPieces(
                partitionFill,
                0,
                outerPartitionHit.Key.PartIndex)
            : [];
        var innerBoundaryParts = innerPartitionHit.Key.Kind == DrawingElementKind.Fill
            ? partitionScene.GetExposedFillBezierSegmentPieces(
                partitionFill,
                0,
                innerPartitionHit.Key.PartIndex)
            : [];
        var outerTop = outerBoundaryParts.FirstOrDefault(part => part.SourcePartIndex == 0);
        var outerLeft = outerBoundaryParts.FirstOrDefault(part => part.SourcePartIndex == 3);
        var innerTop = innerBoundaryParts.FirstOrDefault(part => part.SourcePartIndex == 0);
        var innerLeft = innerBoundaryParts.FirstOrDefault(part => part.SourcePartIndex == 3);
        using var partitionStage = new StageControl(partitionScene) { Size = new Size(640, 420) };
        partitionStage.SetVisibleWorldWidth(800);
        partitionStage.SetSelection([partitionFill], partitionFill);
        partitionStage.SetFillEdgeBezierOverlay(
            partitionFill,
            outerBoundaryParts.Select(part => new FillEdgeBezierOverlaySegment(
                part.PieceIndex,
                part.Start,
                part.Control1,
                part.Control2,
                part.End)).ToArray(),
            outerBoundaryParts.FirstOrDefault().PieceIndex);
        var partitionControlHit = outerBoundaryParts.Length > 0
            ? partitionStage.HitTestFillEdgeBezierOverlay(Point.Round(partitionStage.WorldToScreen(
                outerBoundaryParts[0].Control1.X,
                outerBoundaryParts[0].Control1.Y)))
            : FillEdgeBezierOverlayHit.None;
        var outerTopIntersectionHit = partitionStage.HitTestFillEdgeBezierOverlay(
            Point.Round(partitionStage.WorldToScreen(60, -120)));
        var outerLeftIntersectionHit = partitionStage.HitTestFillEdgeBezierOverlay(
            Point.Round(partitionStage.WorldToScreen(-220, 40)));
        var omittedTopHit = partitionStage.HitTestFillEdgeBezierOverlay(
            Point.Round(partitionStage.WorldToScreen(-100, -120)));
        var omittedLeftHit = partitionStage.HitTestFillEdgeBezierOverlay(
            Point.Round(partitionStage.WorldToScreen(-220, -40)));
        var internalBoundaryHit = partitionStage.HitTestFillEdgeBezierOverlay(
            Point.Round(partitionStage.WorldToScreen(60, 0)));
        if (partitionFillParts.Length != 2
            || outerPartitionHit.Key.ObjectIndex != partitionFill
            || innerPartitionHit.Key.ObjectIndex != partitionFill
            || outerPartitionHit.Key.PartIndex == innerPartitionHit.Key.PartIndex
            || !MainForm.TryGetSelectedFillPartIndex(
                partitionFill,
                [outerPartitionHit],
                out var selectedPartitionPartIndex)
            || selectedPartitionPartIndex != outerPartitionHit.Key.PartIndex
            || MainForm.IsWholeFillElementSelection(
                partitionScene,
                0,
                partitionFill,
                [outerPartitionHit])
            || !MainForm.ShouldShowFillEdgeBezierOverlay(
                ToolMode.Select,
                1,
                hasEditableFillBoundarySelection: true)
            || outerBoundaryParts.Length != 4
            || innerBoundaryParts.Length != 2
            || outerTop.SourcePartIndex != 0
            || outerLeft.SourcePartIndex != 3
            || innerTop.SourcePartIndex != 0
            || innerLeft.SourcePartIndex != 3
            || !PointsWithin(outerTop.Start, new PointF(60, -120), 1.5f)
            || !PointsWithin(outerTop.End, new PointF(220, -120), 1.5f)
            || !PointsWithin(outerLeft.Start, new PointF(-220, 120), 1.5f)
            || !PointsWithin(outerLeft.End, new PointF(-220, 40), 1.5f)
            || !PointsWithin(innerTop.Start, new PointF(-220, -120), 1.5f)
            || !PointsWithin(innerTop.End, new PointF(60, -120), 1.5f)
            || !PointsWithin(innerLeft.Start, new PointF(-220, 40), 1.5f)
            || !PointsWithin(innerLeft.End, new PointF(-220, -120), 1.5f)
            || !outerTop.StartIsVirtualAnchor
            || !outerLeft.EndIsVirtualAnchor
            || !partitionStage.FillEdgeBezierOverlayVisible
            || partitionControlHit.Handle != EditHandleKind.BezierControl
            || outerTopIntersectionHit.PartIndex != outerTop.PieceIndex
            || outerTopIntersectionHit.Handle != EditHandleKind.LineStart
            || outerLeftIntersectionHit.PartIndex != outerLeft.PieceIndex
            || outerLeftIntersectionHit.Handle != EditHandleKind.LineEnd
            || omittedTopHit.IsValid
            || omittedLeftHit.IsValid
            || internalBoundaryHit.IsValid)
        {
            throw new InvalidOperationException(
                $"A fill part cut by an intersecting closed stroke lost its clipped source controls: regions={partitionFillParts.Length}, outerHit={outerPartitionHit}, innerHit={innerPartitionHit}, outer={string.Join(',', outerBoundaryParts.Select(part => $"{part.SourcePartIndex}:{part.StartT:F3}-{part.EndT:F3}"))}, inner={string.Join(',', innerBoundaryParts.Select(part => $"{part.SourcePartIndex}:{part.StartT:F3}-{part.EndT:F3}"))}, control={partitionControlHit}, topAnchor={outerTopIntersectionHit}, leftAnchor={outerLeftIntersectionHit}, omittedTop={omittedTopHit}, omittedLeft={omittedLeftHit}, internal={internalBoundaryHit}.");
        }

        if (!partitionScene.TryConvertFillToBezierPath(partitionFill)
            || !partitionScene.TryMaterializePathBezierSegmentInterval(
                partitionFill,
                outerTop.SourcePartIndex,
                outerTop.StartT,
                outerTop.EndT,
                out var materializedTopPart)
            || !partitionScene.TryGetPathBezierSegment(
                partitionFill,
                materializedTopPart,
                out var materializedTop)
            || partitionScene.GetPathBezierSegmentParts(partitionFill).Length != 5)
        {
            throw new InvalidOperationException("The top virtual fill-edge intersection could not be materialized as an exact editable segment.");
        }

        var materializedOuterHit = partitionScene.HitTestElement(new PointF(160, 80), 0, toleranceWorld: 2);
        var resolvedOuterSelection = MainForm.TryResolveFillPartSelection(
            partitionScene,
            0,
            partitionFill,
            outerFillPartProbes,
            out var remappedOuterSelection);
        var materializedOuterParts = resolvedOuterSelection
            ? partitionScene.GetExposedFillBezierSegmentPieces(
                partitionFill,
                0,
                remappedOuterSelection.Key.PartIndex)
            : [];
        var remappedOuterLeftIndex = Array.FindIndex(materializedOuterParts, part =>
            PointsWithin(part.Start, new PointF(-220, 120), 1.5f)
            && PointsWithin(part.End, new PointF(-220, 40), 1.5f));
        var remappedOuterLeft = remappedOuterLeftIndex >= 0
            ? materializedOuterParts[remappedOuterLeftIndex]
            : default;
        PathBezierSegmentPart materializedLeft = default;
        if (!resolvedOuterSelection
            || remappedOuterSelection.Key.Kind != DrawingElementKind.Fill
            || remappedOuterSelection.Key.ObjectIndex != partitionFill
            || remappedOuterSelection.Key.PartIndex != materializedOuterHit.Key.PartIndex
            || remappedOuterLeftIndex < 0
            || !partitionScene.TryMaterializePathBezierSegmentInterval(
                partitionFill,
                remappedOuterLeft.SourcePartIndex,
                remappedOuterLeft.StartT,
                remappedOuterLeft.EndT,
                out var materializedLeftPart)
            || !partitionScene.TryGetPathBezierSegment(
                partitionFill,
                materializedLeftPart,
                out materializedLeft)
            || partitionScene.GetPathBezierSegmentParts(partitionFill).Length != 6
            || !PointsWithin(materializedTop.Start, new PointF(60, -120), 1.5f)
            || !PointsWithin(materializedTop.End, new PointF(220, -120), 1.5f)
            || !PointsWithin(materializedLeft.Start, new PointF(-220, 120), 1.5f)
            || !PointsWithin(materializedLeft.End, new PointF(-220, 40), 1.5f))
        {
            throw new InvalidOperationException(
                $"Virtual fill-edge intersections were not isolated into exact source cubics: top={materializedTop}, left={materializedLeft}, count={partitionScene.GetPathBezierSegmentParts(partitionFill).Length}.");
        }

        partitionScene.RestoreSnapshot(partitionSnapshot);
        if (!partitionScene.TryConvertFillToBezierPath(partitionFill)
            || !partitionScene.TryMaterializePathBezierSegmentInterval(
                partitionFill,
                outerTop.SourcePartIndex,
                outerTop.StartT,
                outerTop.EndT,
                out var sharedTopPart)
            || !partitionScene.TryGetPathBezierSegment(partitionFill, sharedTopPart, out var sharedTopSegment))
        {
            throw new InvalidOperationException("The shared fill/freeform intersection could not materialize its fill anchor.");
        }

        var freeformIntersection = partitionScene.CaptureStrokeIntersectionsAtFillAnchor(
            partitionFill,
            sharedTopPart,
            startEndpoint: true,
            0);
        if (freeformIntersection.LineAnchors.Length != 0
            || freeformIntersection.FreeformAnchors.Length != 1
            || freeformIntersection.FreeformAnchors[0].ObjectIndex != partitionStroke
            || partitionScene.ObjectCount != 2
            || partitionScene.ShapeKind[partitionStroke] != ShapeKind.Freeform
            || !partitionScene.TryGetFreehandWorldPoints(partitionStroke, out var insertedFreeformPoints)
            || insertedFreeformPoints.Length != 6
            || insertedFreeformPoints[0] != insertedFreeformPoints[^1]
            || !insertedFreeformPoints.Any(point => PointsWithin(point, new PointF(60, -120), 1.5f)))
        {
            throw new InvalidOperationException(
                $"A fill-side intersection did not materialize inside the original closed Freeform: lines={freeformIntersection.LineAnchors.Length}, freeforms={freeformIntersection.FreeformAnchors.Length}, objects={partitionScene.ObjectCount}.");
        }

        var movedIntersection = new PointF(80, -140);
        var movedTopSegment = MainForm.AdjustFillEdgeBezierHandle(
            sharedTopSegment.Start,
            sharedTopSegment.Control1,
            sharedTopSegment.Control2,
            sharedTopSegment.End,
            EditHandleKind.LineStart,
            movedIntersection);
        PathBezierSegmentPart movedTop = default;
        PointF[] movedFreeformPoints = [];
        if (!partitionScene.SetPathBezierSegment(
                partitionFill,
                sharedTopPart,
                movedTopSegment.Start,
                movedTopSegment.Control1,
                movedTopSegment.Control2,
                movedTopSegment.End,
                rebuildGeometryIndex: false,
                preserveStraightAdjacentSegments: true)
            || !partitionScene.UpdateSharedBoundaryIntersection(
                freeformIntersection,
                movedIntersection)
            || !partitionScene.TryGetPathBezierSegment(partitionFill, sharedTopPart, out movedTop)
            || !partitionScene.TryGetFreehandWorldPoints(partitionStroke, out movedFreeformPoints)
            || !PointsWithin(movedTop.Start, movedIntersection, 0.01f)
            || movedFreeformPoints.Length != 6
            || movedFreeformPoints[0] != movedFreeformPoints[^1]
            || !movedFreeformPoints.Any(point => PointsWithin(point, movedIntersection, 0.01f))
            || movedFreeformPoints.Any(point => PointsWithin(point, new PointF(60, -120), 0.01f))
            || new[]
            {
                new PointF(-340, -240),
                new PointF(60, -240),
                new PointF(60, 40),
                new PointF(-340, 40)
            }.Any(corner => !movedFreeformPoints.Any(point => PointsWithin(point, corner, 0.01f))))
        {
            throw new InvalidOperationException(
                $"Dragging a fill intersection did not move the closed Freeform and fill anchor together: fill={movedTop}, freeform={string.Join(';', movedFreeformPoints.Select(point => $"{point.X:F0},{point.Y:F0}"))}.");
        }

        var repeatedFreeformIntersection = partitionScene.CaptureStrokeIntersectionsAtFillAnchor(
            partitionFill,
            sharedTopPart,
            startEndpoint: true,
            0,
            rebuildGeometryIndex: false);
        var repeatedFreeformTarget = new PointF(90, -150);
        var repeatedFreeformMoved = false;
        if (repeatedFreeformIntersection.FreeformAnchors.Length == 1
            && partitionScene.TryGetPathBezierSegment(partitionFill, sharedTopPart, out var repeatedFreeformFill))
        {
            var repeatedFreeformAdjusted = MainForm.AdjustFillEdgeBezierHandle(
                repeatedFreeformFill.Start,
                repeatedFreeformFill.Control1,
                repeatedFreeformFill.Control2,
                repeatedFreeformFill.End,
                EditHandleKind.LineStart,
                repeatedFreeformTarget);
            repeatedFreeformMoved = partitionScene.SetPathBezierSegment(
                    partitionFill,
                    sharedTopPart,
                    repeatedFreeformAdjusted.Start,
                    repeatedFreeformAdjusted.Control1,
                    repeatedFreeformAdjusted.Control2,
                    repeatedFreeformAdjusted.End,
                    rebuildGeometryIndex: false,
                    preserveStraightAdjacentSegments: true)
                && partitionScene.UpdateSharedBoundaryIntersection(
                    repeatedFreeformIntersection,
                    repeatedFreeformTarget,
                    rebuildGeometryIndex: false);
        }

        if (!repeatedFreeformMoved
            || partitionScene.ObjectCount != 2
            || !partitionScene.TryGetFreehandWorldPoints(partitionStroke, out var repeatedFreeformPoints)
            || repeatedFreeformPoints.Length != 6
            || repeatedFreeformPoints.Count(point => PointsWithin(point, repeatedFreeformTarget, 0.01f)) != 1)
        {
            throw new InvalidOperationException(
                $"Repeated fill/freeform shared-focus editing added topology members: moved={repeatedFreeformMoved}, links={repeatedFreeformIntersection.FreeformAnchors.Length}, objects={partitionScene.ObjectCount}.");
        }

        partitionScene.RestoreSnapshot(partitionSnapshot);
        if (partitionScene.ObjectCount != 2
            || partitionScene.ShapeKind[partitionFill] != ShapeKind.Rectangle
            || partitionScene.ShapeKind[partitionStroke] != ShapeKind.Freeform
            || !partitionScene.TryGetFreehandWorldPoints(partitionStroke, out var restoredFreeformPoints)
            || restoredFreeformPoints.Length != 5
            || !PointsWithin(restoredFreeformPoints[1], new PointF(60, -240), 0.01f)
            || !PointsWithin(restoredFreeformPoints[2], new PointF(60, 40), 0.01f))
        {
            throw new InvalidOperationException("Restoring a shared fill/freeform edit retained its materialized intersection topology.");
        }

        var cubicIntersectionScene = new VectorScene();
        cubicIntersectionScene.CreateEmpty();
        var cubicIntersectionFill = cubicIntersectionScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(440, 240),
            0,
            0,
            Color.Teal,
            Color.Transparent,
            12,
            ShapeKind.Rectangle);
        var cubicIntersectionLine = cubicIntersectionScene.AddCubicCurveSegment(
            0,
            new PointF(60, -240),
            new PointF(140, -180),
            new PointF(-20, -73),
            new PointF(60, 40),
            VectorUnits.StrokePointsToUnits(3),
            Color.Transparent,
            Color.White,
            12,
            LineEndpointStyle.Sharp,
            LineEndpointStyle.Sharp);
        var cubicIntersectionSnapshot = cubicIntersectionScene.CreateSnapshot();
        var cubicTopPart = cubicIntersectionScene.GetEditableFillBezierSegmentParts(cubicIntersectionFill)
            .First(part => PointsWithin(part.Start, new PointF(-220, -120), 0.01f));
        if (!cubicIntersectionScene.TryConvertFillToBezierPath(cubicIntersectionFill)
            || !cubicIntersectionScene.TryGetClosestPointOnPathBezierSegment(
                cubicIntersectionFill,
                cubicTopPart.PartIndex,
                new PointF(60, -120),
                out var cubicFillParameter,
                out _,
                out var cubicFillDistance)
            || cubicFillDistance > 1.5f
            || !cubicIntersectionScene.TryInsertPathBezierAnchor(
                cubicIntersectionFill,
                cubicTopPart.PartIndex,
                cubicFillParameter,
                out var cubicFillAnchorPart,
                out var cubicFillAnchor)
            || !PointsWithin(cubicFillAnchor, new PointF(60, -120), 1.5f)
            || !cubicIntersectionScene.TryGetPathBezierSegment(
                cubicIntersectionFill,
                cubicFillAnchorPart,
                out var cubicFillSegment))
        {
            throw new InvalidOperationException("The cubic-Line shared intersection could not materialize its fill anchor.");
        }

        var cubicIntersection = cubicIntersectionScene.CaptureStrokeIntersectionsAtFillAnchor(
            cubicIntersectionFill,
            cubicFillAnchorPart,
            startEndpoint: true,
            0);
        var firstCubicHalf = cubicIntersection.LineAnchors.FirstOrDefault(anchor => !anchor.StartEndpoint);
        var secondCubicHalf = cubicIntersection.LineAnchors.FirstOrDefault(anchor => anchor.StartEndpoint);
        var cubicMovedAnchor = new PointF(80, -140);
        var cubicMovedFill = MainForm.AdjustFillEdgeBezierHandle(
            cubicFillSegment.Start,
            cubicFillSegment.Control1,
            cubicFillSegment.Control2,
            cubicFillSegment.End,
            EditHandleKind.LineStart,
            cubicMovedAnchor);
        var firstEnd = PointF.Empty;
        var secondStart = PointF.Empty;
        if (cubicIntersection.LineAnchors.Length != 2
            || cubicIntersectionScene.ObjectCount != 3
            || firstCubicHalf.ObjectIndex != cubicIntersectionLine
            || secondCubicHalf.ObjectIndex == cubicIntersectionLine
            || !cubicIntersectionScene.SetPathBezierSegment(
                cubicIntersectionFill,
                cubicFillAnchorPart,
                cubicMovedFill.Start,
                cubicMovedFill.Control1,
                cubicMovedFill.Control2,
                cubicMovedFill.End,
                rebuildGeometryIndex: false,
                preserveStraightAdjacentSegments: true)
            || !cubicIntersectionScene.UpdateSharedBoundaryIntersection(
                cubicIntersection,
                cubicMovedAnchor)
            || !cubicIntersectionScene.TryGetLineCubic(
                firstCubicHalf.ObjectIndex,
                out _,
                out var firstControl1,
                out var firstControl2,
                out firstEnd)
            || !cubicIntersectionScene.TryGetLineCubic(
                secondCubicHalf.ObjectIndex,
                out secondStart,
                out var secondControl1,
                out var secondControl2,
                out _)
            || !PointsWithin(firstEnd, cubicMovedAnchor, 0.01f)
            || !PointsWithin(secondStart, cubicMovedAnchor, 0.01f)
            || !PointsWithin(firstControl1, firstCubicHalf.Control1, 0.01f)
            || !PointsWithin(
                firstControl2,
                new PointF(firstCubicHalf.Control2.X + 20, firstCubicHalf.Control2.Y - 20),
                0.01f)
            || !PointsWithin(
                secondControl1,
                new PointF(secondCubicHalf.Control1.X + 20, secondCubicHalf.Control1.Y - 20),
                0.01f)
            || !PointsWithin(secondControl2, secondCubicHalf.Control2, 0.01f)
            || cubicIntersectionScene.GetLineEndpointStyle(firstCubicHalf.ObjectIndex, startEndpoint: true) != LineEndpointStyle.Sharp
            || cubicIntersectionScene.GetLineEndpointStyle(secondCubicHalf.ObjectIndex, startEndpoint: false) != LineEndpointStyle.Sharp)
        {
            throw new InvalidOperationException(
                $"A cubic Line crossing was not split and moved as one shared fill/stroke anchor: links={cubicIntersection.LineAnchors.Length}, objects={cubicIntersectionScene.ObjectCount}, first={firstEnd}, second={secondStart}.");
        }

        var lineSideMovedAnchor = new PointF(40, -100);
        var lineSideIntersection = cubicIntersectionScene.CaptureFillIntersectionsAtLineEndpoint(
            firstCubicHalf.ObjectIndex,
            startEndpoint: false,
            0,
            rebuildGeometryIndex: false);
        var lineSideFillAnchor = lineSideIntersection.PathAnchors.FirstOrDefault();
        var lineSidePeer = lineSideIntersection.LineAnchors.FirstOrDefault();
        if (!cubicIntersectionScene.TryGetLineCubic(
                firstCubicHalf.ObjectIndex,
                out var lineSideOwnerStart,
                out var lineSideOwnerControl1,
                out var lineSideOwnerControl2,
                out var lineSideOwnerEnd))
        {
            throw new InvalidOperationException("The naturally split Line lost its cubic owner before reverse editing.");
        }

        var lineSideDx = lineSideMovedAnchor.X - lineSideOwnerEnd.X;
        var lineSideDy = lineSideMovedAnchor.Y - lineSideOwnerEnd.Y;
        cubicIntersectionScene.SetLineEndpoint(
            firstCubicHalf.ObjectIndex,
            startEndpoint: false,
            lineSideMovedAnchor,
            lineSideOwnerStart,
            lineSideOwnerControl1,
            new PointF(lineSideOwnerControl2.X + lineSideDx, lineSideOwnerControl2.Y + lineSideDy),
            keepStraight: false);
        PathBezierSegmentPart lineSideMovedFill = default;
        var lineSideMovedPeer = PointF.Empty;
        var lineSideMoved = lineSideIntersection.PathAnchors.Length == 1
            && lineSideIntersection.LineAnchors.Length == 1
            && cubicIntersectionScene.UpdateSharedBoundaryIntersection(
                lineSideIntersection,
                lineSideMovedAnchor,
                rebuildGeometryIndex: false)
            && cubicIntersectionScene.TryGetPathBezierSegment(
                lineSideFillAnchor.ObjectIndex,
                lineSideFillAnchor.PartIndex,
                out lineSideMovedFill)
            && cubicIntersectionScene.TryGetLineEndpoint(
                lineSidePeer.ObjectIndex,
                lineSidePeer.StartEndpoint,
                out lineSideMovedPeer)
            && PointsWithin(
                lineSideFillAnchor.StartEndpoint ? lineSideMovedFill.Start : lineSideMovedFill.End,
                lineSideMovedAnchor,
                0.01f)
            && PointsWithin(lineSideMovedPeer, lineSideMovedAnchor, 0.01f)
            && cubicIntersectionScene.ObjectCount == 3;
        cubicIntersectionScene.CompleteDeferredBuild();

        var repeatedFillIntersection = lineSideMoved
            ? cubicIntersectionScene.CaptureStrokeIntersectionsAtFillAnchor(
                lineSideFillAnchor.ObjectIndex,
                lineSideFillAnchor.PartIndex,
                startEndpoint: lineSideFillAnchor.StartEndpoint,
                0,
                rebuildGeometryIndex: false)
            : new SharedBoundaryIntersection(PointF.Empty, [], [], [], -1, -1);
        var repeatedFillTarget = new PointF(60, -120);
        var repeatedFillMoved = false;
        if (repeatedFillIntersection.LineAnchors.Length == 2
            && cubicIntersectionScene.TryGetPathBezierSegment(
                repeatedFillIntersection.OwnerObjectIndex,
                repeatedFillIntersection.OwnerPartIndex,
                out var repeatedFillSource))
        {
            var repeatedFillAdjusted = MainForm.AdjustFillEdgeBezierHandle(
                repeatedFillSource.Start,
                repeatedFillSource.Control1,
                repeatedFillSource.Control2,
                repeatedFillSource.End,
                lineSideFillAnchor.StartEndpoint ? EditHandleKind.LineStart : EditHandleKind.LineEnd,
                repeatedFillTarget);
            repeatedFillMoved = cubicIntersectionScene.SetPathBezierSegment(
                    repeatedFillIntersection.OwnerObjectIndex,
                    repeatedFillIntersection.OwnerPartIndex,
                    repeatedFillAdjusted.Start,
                    repeatedFillAdjusted.Control1,
                    repeatedFillAdjusted.Control2,
                    repeatedFillAdjusted.End,
                    rebuildGeometryIndex: false,
                    preserveStraightAdjacentSegments: true)
                && cubicIntersectionScene.UpdateSharedBoundaryIntersection(
                    repeatedFillIntersection,
                    repeatedFillTarget,
                    rebuildGeometryIndex: false);
        }
        cubicIntersectionScene.CompleteDeferredBuild();

        var roundTripFirstEnd = PointF.Empty;
        var roundTripSecondStart = PointF.Empty;
        if (!lineSideMoved
            || !repeatedFillMoved
            || cubicIntersectionScene.ObjectCount != 3
            || cubicIntersectionScene.GetPathBezierSegmentParts(cubicIntersectionFill).Length != 5
            || !cubicIntersectionScene.TryGetLineCubic(
                firstCubicHalf.ObjectIndex,
                out _,
                out _,
                out var repeatedFirstControl2,
                out roundTripFirstEnd)
            || !cubicIntersectionScene.TryGetLineCubic(
                secondCubicHalf.ObjectIndex,
                out roundTripSecondStart,
                out var repeatedSecondControl1,
                out _,
                out _)
            || !PointsWithin(roundTripFirstEnd, repeatedFillTarget, 0.01f)
            || !PointsWithin(roundTripSecondStart, repeatedFillTarget, 0.01f)
            || !PointsWithin(repeatedFirstControl2, firstCubicHalf.Control2, 0.01f)
            || !PointsWithin(repeatedSecondControl1, secondCubicHalf.Control1, 0.01f))
        {
            throw new InvalidOperationException(
                $"Repeated Fill/Line shared-focus edits changed membership or drifted cubic controls: lineMoved={lineSideMoved}, fillMoved={repeatedFillMoved}, pathLinks={lineSideIntersection.PathAnchors.Length}, peerLinks={lineSideIntersection.LineAnchors.Length}, repeatLinks={repeatedFillIntersection.LineAnchors.Length}, objects={cubicIntersectionScene.ObjectCount}, first={roundTripFirstEnd}, second={roundTripSecondStart}.");
        }

        cubicIntersectionScene.RestoreSnapshot(cubicIntersectionSnapshot);
        if (cubicIntersectionScene.ObjectCount != 2
            || cubicIntersectionScene.ShapeKind[cubicIntersectionFill] != ShapeKind.Rectangle
            || !cubicIntersectionScene.TryGetLineCubic(
                cubicIntersectionLine,
                out var restoredCubicStart,
                out _,
                out _,
                out var restoredCubicEnd)
            || !PointsWithin(restoredCubicStart, new PointF(60, -240), 0.01f)
            || !PointsWithin(restoredCubicEnd, new PointF(60, 40), 0.01f))
        {
            throw new InvalidOperationException("Restoring a shared cubic-Line edit retained split Line objects.");
        }

        foreach (var outlinedFillColor in new[] { Color.Transparent, Color.FromArgb(255, 24, 24, 24) })
        {
            var outlinedIntersectionScene = new VectorScene();
            outlinedIntersectionScene.CreateEmpty();
            var outlinedStroke = VectorUnits.StrokePointsToUnits(3);
            var outlinedRectangle = outlinedIntersectionScene.AddObject(
                0,
                PointF.Empty,
                new SizeF(440, 240),
                0,
                outlinedStroke,
                outlinedFillColor,
                Color.White,
                12,
                ShapeKind.Rectangle);
            var outlinedIntersectionFill = outlinedIntersectionScene.AddPathObject(
                0,
                [
                    new PointF(-240, -200),
                    new PointF(60, -120),
                    new PointF(220, 40),
                    new PointF(20, 200),
                    new PointF(-260, 20)
                ],
                0,
                Color.Teal,
                Color.Transparent,
                12);
            var outlinedSnapshot = outlinedIntersectionScene.CreateSnapshot();
            var outlinedAnchor = new PointF(60, -120);
            if (!outlinedIntersectionScene.TryConvertFillToBezierPath(outlinedIntersectionFill))
            {
                throw new InvalidOperationException("The outlined-shape intersection fill could not become an editable path.");
            }

            var outlinedAnchorPart = outlinedIntersectionScene.GetPathBezierSegmentParts(outlinedIntersectionFill)
                .First(part => PointsWithin(part.Start, outlinedAnchor, 0.01f));
            var outlinedIntersection = outlinedIntersectionScene.CaptureStrokeIntersectionsAtFillAnchor(
                outlinedIntersectionFill,
                outlinedAnchorPart.PartIndex,
                startEndpoint: true,
                0);
            var outlinedStrokePathAnchor = outlinedIntersection.PathAnchors.FirstOrDefault(anchor =>
                anchor.ObjectIndex == outlinedRectangle);
            PathBezierSegmentPart outlinedFillSegment = default;
            PathBezierSegmentPart outlinedStrokeSegment = default;
            if (outlinedIntersection.OwnerObjectIndex != outlinedIntersectionFill
                || outlinedIntersection.OwnerPartIndex < 0
                || outlinedIntersection.PathAnchors.Length != 1
                || outlinedStrokePathAnchor.ObjectIndex != outlinedRectangle
                || outlinedIntersection.LineAnchors.Length != 0
                || outlinedIntersection.FreeformAnchors.Length != 0
                || outlinedIntersectionScene.ObjectCount != 2
                || outlinedIntersectionScene.ShapeKind[outlinedRectangle] != ShapeKind.Path
                || outlinedIntersectionScene.Stroke[outlinedRectangle] <= 0
                || outlinedIntersectionScene.StrokeArgb[outlinedRectangle] != Color.White.ToArgb()
                || Color.FromArgb(outlinedIntersectionScene.Argb[outlinedRectangle]).A != outlinedFillColor.A
                || !outlinedIntersectionScene.TryGetPathBezierSegment(
                    outlinedIntersection.OwnerObjectIndex,
                    outlinedIntersection.OwnerPartIndex,
                    out outlinedFillSegment)
                || !PointsWithin(outlinedFillSegment.Start, outlinedAnchor, 0.01f)
                || !outlinedIntersectionScene.TryGetPathBezierSegment(
                    outlinedStrokePathAnchor.ObjectIndex,
                    outlinedStrokePathAnchor.PartIndex,
                    out outlinedStrokeSegment)
                || !PointsWithin(outlinedStrokeSegment.Start, outlinedAnchor, 0.01f))
            {
                throw new InvalidOperationException(
                    $"An outlined Rectangle intersection did not remain one path-backed stroke: fillAlpha={outlinedFillColor.A}, owner={outlinedIntersection.OwnerObjectIndex}:{outlinedIntersection.OwnerPartIndex}/{outlinedFillSegment}, pathAnchor={outlinedStrokePathAnchor}/{outlinedStrokeSegment}, shape={outlinedIntersectionScene.ShapeKind[outlinedRectangle]}, stroke={outlinedIntersectionScene.Stroke[outlinedRectangle]:F2}/{outlinedStroke:F2}, strokeArgb={outlinedIntersectionScene.StrokeArgb[outlinedRectangle]}/{Color.White.ToArgb()}, actualFillAlpha={Color.FromArgb(outlinedIntersectionScene.Argb[outlinedRectangle]).A}, paths={outlinedIntersection.PathAnchors.Length}, lines={outlinedIntersection.LineAnchors.Length}, freeforms={outlinedIntersection.FreeformAnchors.Length}, objects={outlinedIntersectionScene.ObjectCount}.");
            }

            var outlinedMovedAnchor = new PointF(90, -150);
            var outlinedMovedFill = MainForm.AdjustFillEdgeBezierHandle(
                outlinedFillSegment.Start,
                outlinedFillSegment.Control1,
                outlinedFillSegment.Control2,
                outlinedFillSegment.End,
                EditHandleKind.LineStart,
                outlinedMovedAnchor);
            if (!outlinedIntersectionScene.SetPathBezierSegment(
                    outlinedIntersection.OwnerObjectIndex,
                    outlinedIntersection.OwnerPartIndex,
                    outlinedMovedFill.Start,
                    outlinedMovedFill.Control1,
                    outlinedMovedFill.Control2,
                    outlinedMovedFill.End,
                    rebuildGeometryIndex: false,
                    preserveStraightAdjacentSegments: true)
                || !outlinedIntersectionScene.UpdateSharedBoundaryIntersection(
                    outlinedIntersection,
                    outlinedMovedAnchor)
                || !outlinedIntersectionScene.TryGetPathBezierSegment(
                    outlinedStrokePathAnchor.ObjectIndex,
                    outlinedStrokePathAnchor.PartIndex,
                    out var movedStrokeSegment)
                || !PointsWithin(
                    outlinedStrokePathAnchor.StartEndpoint
                        ? movedStrokeSegment.Start
                        : movedStrokeSegment.End,
                    outlinedMovedAnchor,
                    0.01f)
                || outlinedIntersectionScene.ObjectCount != 2)
            {
                throw new InvalidOperationException(
                    $"Dragging a fill anchor did not move the outlined Rectangle path node in place: fillAlpha={outlinedFillColor.A}.");
            }

            outlinedIntersectionScene.RestoreSnapshot(outlinedSnapshot);
            if (outlinedIntersectionScene.ObjectCount != 2
                || outlinedIntersectionScene.ShapeKind[outlinedRectangle] != ShapeKind.Rectangle
                || outlinedIntersectionScene.Stroke[outlinedRectangle] <= 0
                || outlinedIntersectionScene.StrokeArgb[outlinedRectangle] != Color.White.ToArgb()
                || outlinedIntersectionScene.ShapeKind[outlinedIntersectionFill] != ShapeKind.Path)
            {
                throw new InvalidOperationException(
                    $"Restoring an outlined Rectangle intersection retained materialized stroke objects: fillAlpha={outlinedFillColor.A}.");
            }
        }

        var reverseIntersectionScene = new VectorScene();
        reverseIntersectionScene.CreateEmpty();
        var reverseIntersectionFill = reverseIntersectionScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(440, 240),
            0,
            0,
            Color.Teal,
            Color.Transparent,
            12,
            ShapeKind.Rectangle);
        var reverseIntersectionLine = reverseIntersectionScene.AddCubicCurveSegment(
            0,
            new PointF(60, -120),
            new PointF(60, -67),
            new PointF(60, -13),
            new PointF(60, 40),
            VectorUnits.StrokePointsToUnits(3),
            Color.Transparent,
            Color.White,
            12);
        var reverseIntersectionSnapshot = reverseIntersectionScene.CreateSnapshot();
        var reverseIntersection = reverseIntersectionScene.CaptureFillIntersectionsAtLineEndpoint(
            reverseIntersectionLine,
            startEndpoint: true,
            0);
        var reverseMovedAnchor = new PointF(80, -140);
        if (!reverseIntersectionScene.TryGetLineCubic(
                reverseIntersectionLine,
                out _,
                out var reverseControl1,
                out var reverseControl2,
                out var reverseEnd))
        {
            throw new InvalidOperationException("The reverse shared-intersection Line lost its cubic geometry.");
        }

        reverseIntersectionScene.SetLineEndpoint(
            reverseIntersectionLine,
            startEndpoint: true,
            reverseMovedAnchor,
            reverseEnd,
            new PointF(reverseControl1.X + 20, reverseControl1.Y - 20),
            reverseControl2,
            keepStraight: true);
        PathBezierSegmentPart reverseMovedFill = default;
        var reverseMovedLine = PointF.Empty;
        if (reverseIntersection.PathAnchors.Length != 1
            || !reverseIntersectionScene.UpdateSharedBoundaryIntersection(
                reverseIntersection,
                reverseMovedAnchor)
            || !reverseIntersectionScene.TryGetPathBezierSegment(
                reverseIntersection.PathAnchors[0].ObjectIndex,
                reverseIntersection.PathAnchors[0].PartIndex,
                out reverseMovedFill)
            || !PointsWithin(reverseMovedFill.Start, reverseMovedAnchor, 0.01f)
            || !VectorScene.IsStraightBezierSegment(
                reverseMovedFill.Start,
                reverseMovedFill.Control1,
                reverseMovedFill.Control2,
                reverseMovedFill.End)
            || !reverseIntersectionScene.TryGetLineEndpoint(
                reverseIntersectionLine,
                startEndpoint: true,
                out reverseMovedLine)
            || !PointsWithin(reverseMovedLine, reverseMovedAnchor, 0.01f))
        {
            throw new InvalidOperationException(
                $"Dragging a Line endpoint did not move the intersecting fill boundary anchor: paths={reverseIntersection.PathAnchors.Length}, fill={reverseMovedFill}, line={reverseMovedLine}.");
        }

        reverseIntersectionScene.RestoreSnapshot(reverseIntersectionSnapshot);
        if (reverseIntersectionScene.ObjectCount != 2
            || reverseIntersectionScene.ShapeKind[reverseIntersectionFill] != ShapeKind.Rectangle
            || !reverseIntersectionScene.TryGetLineEndpoint(
                reverseIntersectionLine,
                startEndpoint: true,
                out var restoredReverseLine)
            || !PointsWithin(restoredReverseLine, new PointF(60, -120), 0.01f))
        {
            throw new InvalidOperationException("Restoring a reverse shared-intersection edit retained its fill anchor materialization.");
        }

        var combinedIntersectionScene = new VectorScene();
        combinedIntersectionScene.CreateEmpty();
        var combinedIntersectionFill = combinedIntersectionScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(440, 240),
            0,
            0,
            Color.Teal,
            Color.Transparent,
            12,
            ShapeKind.Rectangle);
        var combinedRightEdge = combinedIntersectionScene.GetEditableFillBezierSegmentParts(combinedIntersectionFill)
            .First(part => PointsWithin(part.Start, new PointF(220, -120), 0.01f));
        var combinedBoundaryLine = combinedIntersectionScene.AddCubicCurveSegment(
            0,
            combinedRightEdge.Start,
            combinedRightEdge.Control1,
            combinedRightEdge.Control2,
            combinedRightEdge.End,
            VectorUnits.StrokePointsToUnits(3),
            Color.Transparent,
            Color.White,
            12);
        var combinedCrossingLine = combinedIntersectionScene.AddCubicCurveSegment(
            0,
            new PointF(60, -120),
            new PointF(60, -67),
            new PointF(60, -13),
            new PointF(60, 40),
            VectorUnits.StrokePointsToUnits(3),
            Color.Transparent,
            Color.White,
            12);
        var combinedIntersection = combinedIntersectionScene.CaptureFillIntersectionsAtLineEndpoint(
            combinedCrossingLine,
            startEndpoint: true,
            0,
            rebuildGeometryIndex: false);
        var combinedBoundaryLinks = combinedIntersectionScene.CaptureFillBoundaryLineLinks(
            combinedBoundaryLine,
            0);
        var combinedMovedAnchor = new PointF(80, -140);
        combinedIntersectionScene.SetLineEndpoint(
            combinedCrossingLine,
            startEndpoint: true,
            combinedMovedAnchor,
            new PointF(60, 40),
            new PointF(80, -87),
            new PointF(60, -13),
            keepStraight: true);
        PathBezierSegmentPart combinedMovedFill = default;
        if (combinedIntersection.PathAnchors.Length != 1
            || combinedBoundaryLinks.Length != 1
            || !combinedIntersectionScene.UpdateFillBoundaryLineLinks(
                combinedBoundaryLinks,
                rebuildGeometryIndex: false)
            || !combinedIntersectionScene.UpdateSharedBoundaryIntersection(
                combinedIntersection,
                combinedMovedAnchor)
            || !combinedIntersectionScene.TryGetPathBezierSegment(
                combinedIntersection.PathAnchors[0].ObjectIndex,
                combinedIntersection.PathAnchors[0].PartIndex,
                out combinedMovedFill)
            || !PointsWithin(combinedMovedFill.Start, combinedMovedAnchor, 0.01f)
            || combinedIntersectionScene.GetPathBezierSegmentParts(combinedIntersectionFill).Length != 5)
        {
            throw new InvalidOperationException(
                $"A coincident fill link overwrote the later shared intersection anchor: shared={combinedIntersection.PathAnchors.Length}, coincident={combinedBoundaryLinks.Length}, segment={combinedMovedFill}.");
        }

        var compoundIntersectionScene = new VectorScene();
        compoundIntersectionScene.CreateEmpty();
        var compoundIntersectionFill = compoundIntersectionScene.AddPathObjectContours(
            0,
            [
                [
                    new PointF(0, 0),
                    new PointF(100, 0),
                    new PointF(100, 100),
                    new PointF(0, 100)
                ],
                [
                    new PointF(0, 0),
                    new PointF(-100, 0),
                    new PointF(-100, -100),
                    new PointF(0, -100)
                ]
            ],
            0,
            Color.Teal,
            Color.Transparent,
            12);
        var compoundIntersectionLine = compoundIntersectionScene.AddCubicCurveSegment(
            0,
            PointF.Empty,
            new PointF(17, -17),
            new PointF(33, -33),
            new PointF(50, -50),
            VectorUnits.StrokePointsToUnits(3),
            Color.Transparent,
            Color.White,
            12);
        var compoundIntersection = compoundIntersectionScene.CaptureFillIntersectionsAtLineEndpoint(
            compoundIntersectionLine,
            startEndpoint: true,
            0);
        var compoundMovedAnchor = new PointF(20, 20);
        if (compoundIntersection.PathAnchors.Length != 2
            || !compoundIntersectionScene.UpdateSharedBoundaryIntersection(
                compoundIntersection,
                compoundMovedAnchor)
            || !compoundIntersectionScene.TryGetPathBezierWorldContours(
                compoundIntersectionFill,
                out var compoundContours)
            || compoundContours.Length != 2
            || compoundContours.Sum(contour => contour.Count(node =>
                PointsWithin(node.Anchor, compoundMovedAnchor, 0.01f))) != 2
            || compoundContours.Any(contour => contour.Any(node =>
                PointsWithin(node.Anchor, PointF.Empty, 0.01f))))
        {
            throw new InvalidOperationException(
                $"A compound fill did not move every contour node at a shared Line endpoint: links={compoundIntersection.PathAnchors.Length}.");
        }

        var repeatedSplitScene = new VectorScene();
        repeatedSplitScene.CreateEmpty();
        var repeatedSplitLine = repeatedSplitScene.AddCubicCurveSegment(
            0,
            new PointF(0, 0),
            new PointF(33, -40),
            new PointF(67, 40),
            new PointF(100, 0),
            VectorUnits.StrokePointsToUnits(3),
            Color.Transparent,
            Color.White,
            12);
        if (!repeatedSplitScene.SplitLineAt(
                repeatedSplitLine,
                0.5f,
                out var firstRepeatedSplit,
                rebuildGeometryIndex: false)
            || !repeatedSplitScene.SplitLineAt(
                repeatedSplitLine,
                0.5f,
                out var secondRepeatedSplit,
                rebuildGeometryIndex: false))
        {
            throw new InvalidOperationException("The same cubic Line could not be split twice with deferred rebuilds.");
        }

        var repeatedSplitObjects = new[]
        {
            repeatedSplitLine,
            secondRepeatedSplit.SecondObjectIndex,
            firstRepeatedSplit.SecondObjectIndex
        };
        var repeatedSplitSubOrders = repeatedSplitObjects
            .Select(index => repeatedSplitScene.ObjectSubOrder[index])
            .ToArray();
        if (repeatedSplitObjects.Distinct().Count() != 3
            || repeatedSplitSubOrders.Distinct().Count() != 3
            || !(repeatedSplitSubOrders[0] < repeatedSplitSubOrders[1]
                && repeatedSplitSubOrders[1] < repeatedSplitSubOrders[2])
            || !repeatedSplitScene.TryGetLineEndpoint(
                repeatedSplitLine,
                startEndpoint: false,
                out var repeatedFirstEnd)
            || !repeatedSplitScene.TryGetLineEndpoint(
                secondRepeatedSplit.SecondObjectIndex,
                startEndpoint: true,
                out var repeatedSecondStart)
            || !repeatedSplitScene.TryGetLineEndpoint(
                secondRepeatedSplit.SecondObjectIndex,
                startEndpoint: false,
                out var repeatedSecondEnd)
            || !repeatedSplitScene.TryGetLineEndpoint(
                firstRepeatedSplit.SecondObjectIndex,
                startEndpoint: true,
                out var repeatedThirdStart)
            || !PointsWithin(repeatedFirstEnd, repeatedSecondStart, 1.5f)
            || !PointsWithin(repeatedSecondEnd, repeatedThirdStart, 1.5f))
        {
            throw new InvalidOperationException(
                $"Repeated Line splits produced duplicate sub-orders or disconnected segments: suborders={string.Join(',', repeatedSplitSubOrders)}.");
        }

        var fillEdgeMergeScene = new VectorScene();
        fillEdgeMergeScene.CreateEmpty();
        var fillEdgeMergeSource = fillEdgeMergeScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(100, 100),
            0,
            0,
            Color.Teal,
            Color.Transparent,
            12,
            ShapeKind.Rectangle);
        fillEdgeMergeScene.AddObject(
            0,
            new PointF(120, 0),
            new SizeF(100, 100),
            0,
            0,
            Color.Teal,
            Color.Transparent,
            12,
            ShapeKind.Rectangle);
        var fillEdgeMergeRight = fillEdgeMergeScene.GetEditableFillBezierSegmentParts(fillEdgeMergeSource)
            .First(part => PointsWithin(part.Start, new PointF(50, -50), 0.01f));
        if (!fillEdgeMergeScene.TryConvertFillToBezierPath(fillEdgeMergeSource)
            || !fillEdgeMergeScene.TryGetPathBezierSegment(
                fillEdgeMergeSource,
                fillEdgeMergeRight.PartIndex,
                out var fillEdgeMergeSegment))
        {
            throw new InvalidOperationException("The fill-edge merge regression could not materialize its editable boundary.");
        }

        var overlappingFillEdge = MainForm.AdjustFillEdgeBezierHandle(
            fillEdgeMergeSegment.Start,
            fillEdgeMergeSegment.Control1,
            fillEdgeMergeSegment.Control2,
            fillEdgeMergeSegment.End,
            EditHandleKind.LineStart,
            new PointF(80, -50));
        if (!fillEdgeMergeScene.SetPathBezierSegment(
                fillEdgeMergeSource,
                fillEdgeMergeRight.PartIndex,
                overlappingFillEdge.Start,
                overlappingFillEdge.Control1,
                overlappingFillEdge.Control2,
                overlappingFillEdge.End,
                preserveStraightAdjacentSegments: true))
        {
            throw new InvalidOperationException("The fill-edge merge regression could not move its boundary endpoint.");
        }

        var fillEdgeMerged = fillEdgeMergeScene.MergeSameColorFillsAround(
            fillEdgeMergeSource,
            connectNearby: false,
            frame: 0);
        if (fillEdgeMergeScene.ObjectCount != 1
            || (uint)fillEdgeMerged >= fillEdgeMergeScene.ObjectCount
            || !fillEdgeMergeScene.FillContainsPoint(fillEdgeMerged, new PointF(-40, 0))
            || !fillEdgeMergeScene.FillContainsPoint(fillEdgeMerged, new PointF(160, 0))
            || !fillEdgeMergeScene.FillContainsPoint(fillEdgeMerged, new PointF(75, -45)))
        {
            throw new InvalidOperationException(
                $"A committed fill-edge adjustment did not merge the newly overlapping same-color fills: objects={fillEdgeMergeScene.ObjectCount}, merged={fillEdgeMerged}.");
        }

        coverageScene.AddFreehandStroke(
            0,
            [
                coverageParts[0].Start,
                coverageParts[0].End,
                coverageParts[1].End,
                coverageParts[2].End,
                coverageParts[3].End
            ],
            VectorUnits.StrokePointsToUnits(3),
            Color.White,
            brushStroke: false,
            12);
        var fullyCoveredParts = coverageScene.GetExposedFillBezierSegmentPieces(coverageFill, 0);
        var editableCoveredParts = coverageScene.GetExposedFillBezierSegmentPieces(
            coverageFill,
            0,
            includeCoincidentStrokes: true);
        coverageStage.SetFillEdgeBezierOverlay(
            coverageFill,
            fullyCoveredParts.Select(part => new FillEdgeBezierOverlaySegment(
                part.PieceIndex,
                part.Start,
                part.Control1,
                part.Control2,
                part.End)).ToArray());
        if (fullyCoveredParts.Length != 0 || coverageStage.FillEdgeBezierOverlayVisible)
        {
            throw new InvalidOperationException("A fill enclosed by a coincident closed stroke retained fill-edge controls.");
        }

        coverageStage.SetFillEdgeBezierOverlay(
            coverageFill,
            editableCoveredParts.Select(part => new FillEdgeBezierOverlaySegment(
                part.PieceIndex,
                part.Start,
                part.Control1,
                part.Control2,
                part.End)).ToArray(),
            editableCoveredParts.FirstOrDefault().PieceIndex);
        var editableCoveredControlHit = editableCoveredParts.Length > 0
            ? coverageStage.HitTestFillEdgeBezierOverlay(Point.Round(coverageStage.WorldToScreen(
                editableCoveredParts[0].Control1.X,
                editableCoveredParts[0].Control1.Y)))
            : FillEdgeBezierOverlayHit.None;
        if (editableCoveredParts.Length != coverageParts.Length
            || !coverageStage.FillEdgeBezierOverlayVisible
            || editableCoveredControlHit.Handle != EditHandleKind.BezierControl)
        {
            throw new InvalidOperationException(
                "Selecting a fill did not restore Bezier controls hidden by its synchronized coincident stroke.");
        }

        fillScene.RestoreSnapshot(originalSnapshot);
        if (fillScene.ObjectCount != originalCount
            || fillScene.ShapeKind[fill] != ShapeKind.Path
            || fillScene.TryGetPathBezierWorldContours(fill, out _))
        {
            throw new InvalidOperationException("Restoring a pre-conversion fill snapshot retained stale cubic payload.");
        }

        var outlinedScene = new VectorScene();
        outlinedScene.CreateEmpty();
        var outlinedFill = outlinedScene.AddPathObject(
            0,
            [
                new PointF(-260, -120),
                new PointF(20, -210),
                new PointF(280, -60),
                new PointF(190, 210),
                new PointF(-210, 170)
            ],
            VectorUnits.StrokePointsToUnits(3),
            Color.Coral,
            Color.White,
            18);
        outlinedScene.SetLinearGradient(outlinedFill, Color.Coral, Color.RoyalBlue);
        outlinedScene.FillAutoMergeProtected[outlinedFill] = true;
        if (!outlinedScene.TryConvertFillToBezierPath(outlinedFill)
            || !outlinedScene.TryGetPathBezierSegment(outlinedFill, 1, out var outlinedSegment)
            || !outlinedScene.SetPathBezierSegment(
                outlinedFill,
                outlinedSegment.PartIndex,
                outlinedSegment.Start,
                new PointF(outlinedSegment.Control1.X + 90, outlinedSegment.Control1.Y - 140),
                new PointF(outlinedSegment.Control2.X - 70, outlinedSegment.Control2.Y + 110),
                outlinedSegment.End)
            || !outlinedScene.TryGetPathBezierSegment(outlinedFill, outlinedSegment.PartIndex, out var editedOutlinedSegment)
            || !outlinedScene.TryGetPathBezierWorldContours(outlinedFill, out var exactBeforeMaterialization))
        {
            throw new InvalidOperationException("The outlined fill could not be prepared with exact cubic geometry.");
        }

        var boundaryProbe = editedOutlinedSegment.Samples[editedOutlinedSegment.Samples.Length / 2];
        var curvedBoundary = outlinedScene.HitTestElement(boundaryProbe, 0, toleranceWorld: 2);
        var curvedBoundaryPoints = outlinedScene.GetBoundaryPartPoints(curvedBoundary, 0);
        if (curvedBoundary.Key.Kind != DrawingElementKind.BoundaryStroke
            || curvedBoundaryPoints.Length <= 2
            || !outlinedScene.TryGetExactFillBezierSegmentForBoundary(curvedBoundary, 0, out var resolvedBoundarySegment)
            || resolvedBoundarySegment.PartIndex != editedOutlinedSegment.PartIndex
            || !MainForm.TryGetSelectedFillBoundaryBezierPart(
                outlinedScene,
                0,
                outlinedFill,
                [curvedBoundary],
                out var selectedBoundaryPartIndex)
            || selectedBoundaryPartIndex != editedOutlinedSegment.PartIndex
            || !MainForm.ShouldShowFillEdgeBezierOverlay(
                ToolMode.Select,
                1,
                hasEditableFillBoundarySelection: selectedBoundaryPartIndex >= 0))
        {
            throw new InvalidOperationException("A selected curved fill boundary did not resolve back to its exact editable cubic segment.");
        }

        using (var outlinedStage = new StageControl(outlinedScene) { Size = new Size(640, 420) })
        {
            outlinedStage.SetSelection([outlinedFill], outlinedFill);
            outlinedStage.SetSelectedElement(curvedBoundary);
            outlinedStage.SetFillEdgeBezierOverlay(
                outlinedFill,
                outlinedScene.GetEditableFillBezierSegmentParts(outlinedFill)
                    .Select(part => new FillEdgeBezierOverlaySegment(
                        part.PartIndex,
                        part.Start,
                        part.Control1,
                        part.Control2,
                        part.End))
                    .ToArray(),
                selectedBoundaryPartIndex);
            var boundaryControlHit = outlinedStage.HitTestFillEdgeBezierOverlay(
                Point.Round(outlinedStage.WorldToScreen(
                    editedOutlinedSegment.Control1.X,
                    editedOutlinedSegment.Control1.Y)));
            if (boundaryControlHit.PartIndex != editedOutlinedSegment.PartIndex
                || boundaryControlHit.Handle != EditHandleKind.BezierControl
                || outlinedStage.SuppressFillEdgeBezierSelectionOutline(outlinedFill))
            {
                throw new InvalidOperationException("A selected curved outline did not expose its exact fill-edge control handles.");
            }
        }

        var outlinedOrder = outlinedScene.ObjectOrder[outlinedFill];
        var outlinedSubOrder = outlinedScene.ObjectSubOrder[outlinedFill];
        var outlinedObjectCount = outlinedScene.ObjectCount;
        var outlinedFillKey = new DrawingElementKey(outlinedFill, DrawingElementKind.Fill, 0);
        var outlinedResult = outlinedScene.MaterializeSelectedParts([outlinedFillKey], 0);
        var retainedFill = outlinedResult.Success
            ? outlinedResult.Parts.Single(part => part.Source == outlinedFillKey).Result.ObjectIndex
            : -1;
        if (!outlinedResult.Success
            || !outlinedResult.Changed
            || retainedFill != outlinedResult.OldToNewObjectIndex[outlinedFill]
            || retainedFill < 0
            || outlinedScene.ObjectCount <= outlinedObjectCount
            || outlinedScene.Stroke[retainedFill] != 0
            || outlinedScene.ObjectOrder[retainedFill] != outlinedOrder
            || !outlinedScene.ObjectSubOrder[retainedFill].Equals(outlinedSubOrder)
            || !outlinedScene.HasGradient(retainedFill)
            || !outlinedScene.FillAutoMergeProtected[retainedFill]
            || !outlinedScene.TryGetPathBezierWorldContours(retainedFill, out var exactAfterMaterialization)
            || exactAfterMaterialization.Length != exactBeforeMaterialization.Length
            || exactAfterMaterialization.Where((contour, index) =>
                    !contour.SequenceEqual(exactBeforeMaterialization[index]))
                .Any())
        {
            throw new InvalidOperationException("Separating an outlined fill flattened or replaced its exact cubic contour.");
        }

        var detachedOutlineLines = Enumerable.Range(0, outlinedScene.ObjectCount)
            .Where(index => outlinedScene.ShapeKind[index] == ShapeKind.Line
                && outlinedScene.Stroke[index] > 0
                && outlinedScene.ObjectOrder[index] == outlinedOrder)
            .ToArray();
        var retainedFreehandOutline = Enumerable.Range(0, outlinedScene.ObjectCount)
            .Any(index => outlinedScene.ShapeKind[index] == ShapeKind.Freeform
                && outlinedScene.Stroke[index] > 0
                && outlinedScene.ObjectOrder[index] == outlinedOrder);
        var expectedOutlineSegments = exactBeforeMaterialization
            .SelectMany(contour => contour.Select((node, index) =>
            {
                var next = contour[(index + 1) % contour.Length];
                return (Start: node.Anchor,
                    Control1: node.OutgoingControl,
                    Control2: next.IncomingControl,
                    End: next.Anchor);
            }))
            .ToArray();
        var exactOutlinePreserved = expectedOutlineSegments.All(expected =>
            detachedOutlineLines.Any(line =>
                outlinedScene.TryGetLineBezierPart(
                    line,
                    0,
                    1,
                    out var start,
                    out var control1,
                    out var control2,
                    out var end)
                && (PointsNear(start, expected.Start)
                    && PointsNear(control1, expected.Control1)
                    && PointsNear(control2, expected.Control2)
                    && PointsNear(end, expected.End)
                    || PointsNear(start, expected.End)
                    && PointsNear(control1, expected.Control2)
                    && PointsNear(control2, expected.Control1)
                    && PointsNear(end, expected.Start))));
        var detachedOutlineChain = detachedOutlineLines.Length > 0
            ? outlinedScene.GetConnectedStrokeElements(
                    new DrawingElementHit(
                        new DrawingElementKey(detachedOutlineLines[0], DrawingElementKind.Stroke, 0),
                        0,
                        0,
                        1),
                    0)
                .Where(hit => outlinedScene.ObjectOrder[hit.Key.ObjectIndex] == outlinedOrder)
                .Select(hit => hit.Key.ObjectIndex)
                .Distinct()
                .ToHashSet()
            : [];
        if (retainedFreehandOutline
            || detachedOutlineLines.Length != expectedOutlineSegments.Length
            || !exactOutlinePreserved
            || !detachedOutlineChain.SetEquals(detachedOutlineLines))
        {
            throw new InvalidOperationException(
                $"An exact outlined Path did not remain one connected cubic stroke chain after materialization: expected={expectedOutlineSegments.Length}, lines={detachedOutlineLines.Length}, chain={detachedOutlineChain.Count}, freeform={retainedFreehandOutline}.");
        }

        static PointF SkewRotate(PointF point) => new(
            -0.35f * point.X - point.Y + 45,
            point.X - 0.2f * point.Y - 30);
        outlinedScene.TransformObjects([retainedFill], SkewRotate);
        if (!outlinedScene.TryGetPathBezierWorldContours(retainedFill, out var exactAfterTransform)
            || exactAfterTransform.Length != exactAfterMaterialization.Length
            || exactAfterTransform.Where((contour, contourIndex) =>
                    contour.Length != exactAfterMaterialization[contourIndex].Length
                    || contour.Where((node, nodeIndex) =>
                    {
                        var before = exactAfterMaterialization[contourIndex][nodeIndex];
                        return !PointsNear(node.Anchor, VectorUnits.Quantize(SkewRotate(before.Anchor)))
                            || !PointsNear(node.IncomingControl, VectorUnits.Quantize(SkewRotate(before.IncomingControl)))
                            || !PointsNear(node.OutgoingControl, VectorUnits.Quantize(SkewRotate(before.OutgoingControl)));
                    }).Any())
                .Any())
        {
            throw new InvalidOperationException("A skewed and rotated detached fill changed its exact cubic node topology.");
        }

        RunVirtualFillIntersectionRefreshRegression();
        RunRepeatedFillAnchorLineEndpointRegression();
        RunCoincidentFillBoundaryLineRegression();
        RunSelfIntersectingFillBoundaryRegression();
        RunFlipCompoundFillHoleRegression();
    }

    // FlipObjects mirrors coordinates straight through SetPathBezierContoursCore, which stores the
    // exact cubic nodes without any orientation pass. A mirror has determinant -1, so every contour
    // reverses its winding. Point-in-polygon hit testing and the Direct2D Alternate fill mode are
    // orientation independent, but a compound fill's solid area depends on contour nesting, so the
    // mirrored winding must not turn the ring into a hole (or the hole into a solid island).
    //
    // The neighbour shares layer/cel and material so the same-color merge path has to reason about
    // the flipped winding as well: that path rebuilds its boundary through Clipper and reassigns
    // contour depth from Clipper.IsPositive.
    private static void RunFlipCompoundFillHoleRegression()
    {
        static PointF[] Rect(float left, float top, float right, float bottom) =>
        [
            new PointF(left, top),
            new PointF(right, top),
            new PointF(right, bottom),
            new PointF(left, bottom)
        ];

        static VectorScene CreateCompoundScene(out int donut, out int neighbour)
        {
            var scene = new VectorScene();
            scene.CreateEmpty();
            donut = scene.AddPathObjectContours(
                0,
                [Rect(-160, -100, 160, 100), Rect(-50, -40, 50, 40)],
                0,
                Color.Teal,
                Color.Transparent,
                8);
            neighbour = scene.AddPathObjectContours(
                0,
                [Rect(300, -60, 420, 60)],
                0,
                Color.Teal,
                Color.Transparent,
                8);
            scene.CompleteDeferredBuild();
            scene.TryConvertFillToBezierPath(donut);
            scene.TryConvertFillToBezierPath(neighbour);
            return scene;
        }

        var scene = CreateCompoundScene(out var donut, out var neighbour);
        if (donut < 0
            || neighbour < 0
            || scene.FillContainsPoint(donut, PointF.Empty)
            || !scene.FillContainsPoint(donut, new PointF(120, 0))
            || !scene.TryGetPathBezierWorldContours(donut, out var originalContours)
            || originalContours.Length != 2)
        {
            throw new InvalidOperationException(
                $"The compound fill fixture did not start as a holed ring: donut={donut}, "
                + $"neighbour={neighbour}, objects={scene.ObjectCount}, "
                + $"hole={scene.FillContainsPoint(donut, PointF.Empty)}.");
        }

        var originalSigns = originalContours.Select(SignedContourArea).ToArray();
        if (!scene.FlipObjects([donut, neighbour], horizontal: true)
            || !scene.TryGetPathBezierWorldContours(donut, out var flippedContours)
            || flippedContours.Length != 2)
        {
            throw new InvalidOperationException("Flipping a compound fill selection was refused or changed its contour count.");
        }

        // FlipObjects mirrors around the selection union center: the donut spans x=-160..160 and the
        // neighbour x=300..420, so the union center is x=130 and the donut lands on x=-30..290.
        const float mirrorAxisX = 130f;
        var mirroredRing = new PointF(mirrorAxisX * 2 - 120, 0);
        var mirroredHole = PointF.Empty;
        var ringSolid = scene.FillContainsPoint(donut, mirroredRing);
        var holeVoid = !scene.FillContainsPoint(donut, mirroredHole);
        var windingReversed = originalSigns
            .Zip(flippedContours.Select(SignedContourArea), (before, after) => before * after < 0f)
            .All(reversed => reversed);
        if (!ringSolid || !holeVoid || !windingReversed)
        {
            throw new InvalidOperationException(
                $"A flipped compound fill lost its solid ring or its hole: ringSolid={ringSolid}, "
                + $"holeVoid={holeVoid}, windingReversed={windingReversed}, "
                + $"originalSigns=[{string.Join(',', originalSigns.Select(value => value.ToString("0.0")))}], "
                + $"flippedSigns=[{string.Join(',', flippedContours.Select(contour => SignedContourArea(contour).ToString("0.0")))}].");
        }

        // The merge path must keep the flipped hole open and must not invent filled area.
        var mergeScene = CreateCompoundScene(out var mergeDonut, out var mergeNeighbour);
        if (!mergeScene.FlipObjects([mergeDonut, mergeNeighbour], horizontal: true)
            || !mergeScene.TryGetPathWorldContours(mergeDonut, out var mergeContours)
            || mergeContours.Length != 2)
        {
            throw new InvalidOperationException("The flipped merge fixture lost its compound contours.");
        }

        // Derive probes from the actual flipped geometry so this survives a mirror-axis change.
        var outerContour = mergeContours.OrderByDescending(contour => Math.Abs(SignedContourArea(contour))).First();
        var innerContour = mergeContours.OrderBy(contour => Math.Abs(SignedContourArea(contour))).First();
        var holeProbe = new PointF(innerContour.Average(point => point.X), innerContour.Average(point => point.Y));
        var ringProbe = new PointF(
            (holeProbe.X + outerContour.Max(point => point.X)) * 0.5f,
            outerContour.Average(point => point.Y));

        var mergedIndex = mergeScene.MergeSameColorFillsAround(mergeDonut, connectNearby: false);
        var mergedHolePreserved = !mergeScene.FillContainsPoint(mergedIndex, holeProbe);
        var mergedRingPreserved = mergeScene.FillContainsPoint(mergedIndex, ringProbe);
        var mergedContourCount = mergeScene.TryGetPathWorldContours(mergedIndex, out var mergedContours)
            ? mergedContours.Length
            : -1;
        if (!mergedHolePreserved || !mergedRingPreserved || mergedContourCount < 2)
        {
            throw new InvalidOperationException(
                $"Merging a flipped compound fill did not preserve its hole and ring: "
                + $"merged={mergedIndex}/{mergeScene.ObjectCount}, hole={mergedHolePreserved}, "
                + $"ring={mergedRingPreserved}, contours={mergedContourCount}.");
        }

        Console.WriteLine("flip_compound_fill_hole_regression=ok");

        RunFlipCompoundFillGapRegression();
    }

    // Companion coverage for the flip paths that the single horizontal rectangular case above does
    // not reach: vertical mirroring, a Bezier (curved) hole, a mirrored gradient path, and a flip
    // applied to an object that already carries a Distort envelope.
    private static void RunFlipCompoundFillGapRegression()
    {
        static PathBezierNode[] Circle(float centerX, float centerY, float radius)
        {
            var handle = radius * 0.55228475f;
            return
            [
                new PathBezierNode(
                    new PointF(centerX + radius, centerY),
                    new PointF(centerX + radius, centerY - handle),
                    new PointF(centerX + radius, centerY + handle)),
                new PathBezierNode(
                    new PointF(centerX, centerY + radius),
                    new PointF(centerX + handle, centerY + radius),
                    new PointF(centerX - handle, centerY + radius)),
                new PathBezierNode(
                    new PointF(centerX - radius, centerY),
                    new PointF(centerX - radius, centerY + handle),
                    new PointF(centerX - radius, centerY - handle)),
                new PathBezierNode(
                    new PointF(centerX, centerY - radius),
                    new PointF(centerX + handle, centerY - radius),
                    new PointF(centerX - handle, centerY - radius))
            ];
        }

        // 1. Vertical flip of a compound fill whose hole is a true cubic circle.
        var curvedScene = new VectorScene();
        curvedScene.CreateEmpty();
        var curvedDonut = curvedScene.AppendPathBezierObjectContours(
            0,
            [Circle(0, 0, 160), Circle(0, 0, 60)],
            0,
            Color.Teal,
            Color.Transparent,
            16);
        curvedScene.CompleteDeferredBuild();
        if (curvedDonut < 0
            || curvedScene.FillContainsPoint(curvedDonut, PointF.Empty)
            || !curvedScene.FillContainsPoint(curvedDonut, new PointF(110, 0)))
        {
            throw new InvalidOperationException("The curved compound fill fixture did not start as a holed ring.");
        }

        var curvedBounds = curvedScene.GetObjectWorldBounds(curvedDonut);
        var curvedAxisY = curvedBounds.Top + curvedBounds.Bottom;
        var ringBefore = new PointF(0, 110);
        var mirroredRingY = curvedAxisY - ringBefore.Y;
        curvedScene.FlipObjects([curvedDonut], horizontal: false);
        var curvedHoleVoid = !curvedScene.FillContainsPoint(curvedDonut, PointF.Empty);
        var curvedRingSolid = curvedScene.FillContainsPoint(curvedDonut, new PointF(0, mirroredRingY));
        if (!curvedHoleVoid || !curvedRingSolid)
        {
            throw new InvalidOperationException(
                $"A vertically flipped curved compound fill lost its hole or ring: "
                + $"holeVoid={curvedHoleVoid}, ringSolid={curvedRingSolid}@{mirroredRingY}.");
        }

        // 2. A gradient path must mirror with its geometry rather than keep its original side.
        var gradientScene = new VectorScene();
        gradientScene.CreateEmpty();
        var gradientPath = gradientScene.AddPathObjectContours(
            0,
            [
                [
                    new PointF(-200, -80),
                    new PointF(200, -80),
                    new PointF(200, 80),
                    new PointF(-200, 80)
                ]
            ],
            0,
            Color.Teal,
            Color.Transparent,
            8);
        gradientScene.CompleteDeferredBuild();
        gradientScene.TryConvertFillToBezierPath(gradientPath);
        gradientScene.SetLinearGradient(
            gradientPath,
            Color.Teal,
            Color.RoyalBlue,
            new PointF(-150, 0),
            new PointF(150, 0));
        // A gently curved multi-point path so the self-intersection fallback cannot collapse it.
        var sourceGradientPath = new[]
        {
            new PointF(-150, -40),
            new PointF(0, -60),
            new PointF(150, -40)
        };
        gradientScene.SetOrderedGradientPath(gradientPath, sourceGradientPath);
        if (!gradientScene.TryGetGradientPathWorldPoints(gradientPath, out var pathBefore)
            || pathBefore.Length != sourceGradientPath.Length)
        {
            throw new InvalidOperationException("The gradient path fixture did not retain its ordered trajectory.");
        }

        var gradientBounds = gradientScene.GetObjectWorldBounds(gradientPath);
        var gradientAxis = gradientBounds.Left + gradientBounds.Right;
        gradientScene.FlipObjects([gradientPath], horizontal: true);
        if (!gradientScene.TryGetGradientPathWorldPoints(gradientPath, out var pathAfter)
            || pathAfter.Length != pathBefore.Length)
        {
            throw new InvalidOperationException("Flipping a gradient path dropped or resized its trajectory.");
        }

        // Horizontal mirror reverses X about the axis while preserving the point order.
        var mirroredGradientPath = pathBefore
            .Select(point => new PointF(gradientAxis - point.X, point.Y))
            .ToArray();
        if (!pathAfter.Select((point, index) => PointsNear(point, mirroredGradientPath[index])).All(match => match))
        {
            throw new InvalidOperationException(
                $"A flipped gradient path did not mirror its trajectory: "
                + $"before=[{string.Join('|', pathBefore.Select(point => $"{point.X:0.#},{point.Y:0.#}"))}], "
                + $"after=[{string.Join('|', pathAfter.Select(point => $"{point.X:0.#},{point.Y:0.#}"))}], "
                + $"expected=[{string.Join('|', mirroredGradientPath.Select(point => $"{point.X:0.#},{point.Y:0.#}"))}].");
        }

        // 3. A flip must carry an existing Distort envelope along with the geometry.
        var warpScene = new VectorScene();
        warpScene.CreateEmpty();
        var warped = warpScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(400, 240),
            0,
            0,
            Color.Teal,
            Color.Transparent,
            8,
            ShapeKind.Rectangle);
        var warpSource = TransformOverlayFrame.FromBounds(warpScene.GetObjectWorldBounds(warped));
        var baseEnvelope = DistortEnvelope.FromBounds(warpScene.GetObjectWorldBounds(warped));
        var draggedEnvelope = baseEnvelope.WithHandle(
            new DistortHandleRef(
                DistortSide.Top,
                baseEnvelope.TopRight == baseEnvelope.TopLeft
                    ? baseEnvelope.GetAnchors(DistortSide.Top)[0].Id
                    : baseEnvelope.GetAnchors(DistortSide.Top)[^1].Id,
                DistortHandleKind.Anchor),
            baseEnvelope.BottomRight,
            new PointF(baseEnvelope.BottomRight.X + 90, baseEnvelope.BottomRight.Y - 70));
        if (draggedEnvelope == baseEnvelope)
        {
            throw new InvalidOperationException("The distort fixture could not move an envelope corner.");
        }

        warpScene.SetObjectDistortions(warped, [new DistortWarp(warpSource, draggedEnvelope)]);
        if (!warpScene.TryGetObjectDistortions(warped, out var distortionsBefore) || distortionsBefore.Length != 1)
        {
            throw new InvalidOperationException("The distort fixture did not persist its envelope.");
        }

        var warpedBounds = warpScene.GetObjectWorldBounds(warped);
        var warpedAxis = warpedBounds.Left + warpedBounds.Right;
        warpScene.FlipObjects([warped], horizontal: true);
        if (!warpScene.TryGetObjectDistortions(warped, out var distortionsAfter) || distortionsAfter.Length != 1)
        {
            throw new InvalidOperationException("Flipping an object dropped its Distort envelope.");
        }

        // Every envelope handle must mirror about the same axis as the geometry.
        var beforeHandles = distortionsBefore[0].Envelope.GetVisualHandles()
            .Where(handle => handle.Reference.Kind == DistortHandleKind.Anchor)
            .ToArray();
        var afterHandles = distortionsAfter[0].Envelope.GetVisualHandles()
            .Where(handle => handle.Reference.Kind == DistortHandleKind.Anchor)
            .ToArray();
        var handlesMirrored = beforeHandles.Length == afterHandles.Length
            && beforeHandles.Length > 0
            && beforeHandles
                .Select(handle => new PointF(warpedAxis - handle.Position.X, handle.Position.Y))
                .Zip(afterHandles, (expected, actual) => PointsNear(actual.Position, expected))
                .All(match => match);
        if (!handlesMirrored)
        {
            throw new InvalidOperationException(
                $"A flipped object did not mirror its Distort envelope handles: "
                + $"before={beforeHandles.Length}, after={afterHandles.Length}, axis={warpedAxis}.");
        }

        Console.WriteLine("flip_compound_fill_gap_regression=ok");

        RunCrossLayerFlipCenterRegression();
        RunDistortSingleAnchorRegression();
    }

    // FlipObjects mirrors around the union of GetObjectWorldBounds across every target, regardless
    // of layer. A cross-layer selection must therefore share one mirror axis rather than flipping
    // each layer about its own center, which would silently tear the selection apart.
    private static void RunCrossLayerFlipCenterRegression()
    {
        static PointF[] Rect(float left, float top, float right, float bottom) =>
        [
            new PointF(left, top),
            new PointF(right, top),
            new PointF(right, bottom),
            new PointF(left, bottom)
        ];

        static PointF ContourCenter(int sceneObject, VectorScene scene)
        {
            var contour = scene.GetShapeBoundary(sceneObject);
            return new PointF(
                contour.Average(point => point.X),
                contour.Average(point => point.Y));
        }

        var scene = new VectorScene();
        scene.CreateEmpty();
        var secondLayer = scene.AddLayer("Second");
        if (secondLayer <= 0)
        {
            throw new InvalidOperationException("The cross-layer flip fixture could not add a second layer.");
        }

        // Left object on layer 0, right object on layer 1, deliberately far apart so a per-layer
        // center and the shared union center produce clearly different results.
        var left = scene.AddObject(
            0,
            new PointF(-400, 0),
            new SizeF(120, 80),
            0,
            0,
            Color.Teal,
            Color.Transparent,
            8,
            ShapeKind.Rectangle);
        var right = scene.AddObject(
            secondLayer,
            new PointF(600, 0),
            new SizeF(80, 60),
            0,
            0,
            Color.Coral,
            Color.Transparent,
            8,
            ShapeKind.Rectangle);
        if (left < 0 || right < 0)
        {
            throw new InvalidOperationException("The cross-layer flip fixture could not create its objects.");
        }

        var leftBounds = scene.GetObjectWorldBounds(left);
        var rightBounds = scene.GetObjectWorldBounds(right);
        var union = RectangleF.Union(leftBounds, rightBounds);
        var axis = union.Left + union.Right;
        if (axis <= 0f || union.Width <= leftBounds.Width)
        {
            throw new InvalidOperationException(
                $"The cross-layer flip fixture did not span two layers: "
                + $"union=[{union.Left},{union.Right}], left=[{leftBounds.Left},{leftBounds.Right}], "
                + $"right=[{rightBounds.Left},{rightBounds.Right}].");
        }

        var leftCenterBefore = ContourCenter(left, scene);
        var rightCenterBefore = ContourCenter(right, scene);
        if (!scene.FlipObjects([left, right], horizontal: true))
        {
            throw new InvalidOperationException("Flipping a cross-layer selection was refused.");
        }

        // Both objects must land at the mirror of their original position about the SHARED axis,
        // and each must keep its own layer.
        var leftExpected = new PointF(axis - leftCenterBefore.X, leftCenterBefore.Y);
        var rightExpected = new PointF(axis - rightCenterBefore.X, rightCenterBefore.Y);
        var leftCenterAfter = ContourCenter(left, scene);
        var rightCenterAfter = ContourCenter(right, scene);
        var swappedSides = leftCenterAfter.X > rightCenterAfter.X;
        var sharedAxisRespected = PointsWithin(leftCenterAfter, leftExpected, 1f)
            && PointsWithin(rightCenterAfter, rightExpected, 1f);
        var layersPreserved = scene.ObjectLayer[left] != scene.ObjectLayer[right];
        if (!sharedAxisRespected || !swappedSides || !layersPreserved)
        {
            throw new InvalidOperationException(
                $"A cross-layer flip did not mirror both objects about the shared selection axis: "
                + $"left {leftCenterBefore.X:0.#}->{leftCenterAfter.X:0.#} (expected {leftExpected.X:0.#}), "
                + $"right {rightCenterBefore.X:0.#}->{rightCenterAfter.X:0.#} (expected {rightExpected.X:0.#}), "
                + $"axis={axis:0.#}, swappedSides={swappedSides}, layersPreserved={layersPreserved}.");
        }

        Console.WriteLine("cross_layer_flip_center_regression=ok");
    }

    // The Distort tool exposes exactly one inserted anchor across the whole envelope. Corners stay
    // structural, a second insertion is refused, and legacy envelopes that already carry several
    // inserted anchors collapse to one on the next write instead of being rejected.
    private static void RunDistortSingleAnchorRegression()
    {
        var bounds = RectangleF.FromLTRB(0, 0, 400, 240);
        var identity = DistortEnvelope.FromBounds(bounds);
        if (identity.InsertedAnchorCount != 0 || identity.TryGetInsertedAnchor(out _))
        {
            throw new InvalidOperationException("A fresh Distort envelope reported an inserted anchor.");
        }

        var firstInserted = identity.TryInsertAnchor(DistortSide.Top, 0.5f, out var oneAnchor, out var firstHandle)
            && oneAnchor.InsertedAnchorCount == 1
            && oneAnchor.TryGetInsertedAnchor(out var reported)
            && reported.Side == DistortSide.Top
            && reported.AnchorId == firstHandle.AnchorId;
        if (!firstInserted)
        {
            throw new InvalidOperationException(
                $"The first Distort anchor insertion did not take effect: count={oneAnchor.InsertedAnchorCount}.");
        }

        // A second insertion anywhere on the envelope must be refused while one anchor exists.
        var secondRefused = !oneAnchor.TryInsertAnchor(DistortSide.Top, 0.25f, out _, out _)
            && !oneAnchor.TryInsertAnchor(DistortSide.Right, 0.5f, out _, out _)
            && !oneAnchor.TryInsertAnchor(DistortSide.Bottom, 0.6f, out _, out _)
            && !oneAnchor.TryInsertAnchor(DistortSide.Left, 0.4f, out _, out _);
        if (!secondRefused)
        {
            throw new InvalidOperationException(
                $"The Distort tool accepted a second inserted anchor: count={oneAnchor.InsertedAnchorCount}.");
        }

        // The two remaining corners on the split side stay structural, so the side still has 3
        // anchors total (2 corners + 1 inserted).
        var splitSideAnchors = oneAnchor.GetAnchors(DistortSide.Top).Length;
        if (splitSideAnchors != 3)
        {
            throw new InvalidOperationException(
                $"A single inserted Distort anchor produced {splitSideAnchors} anchors on its side instead of 3.");
        }

        // Apply the single-anchor envelope to a scene, then re-write it with a legacy multi-anchor
        // envelope to confirm the write path collapses instead of failing.
        var scene = new VectorScene();
        scene.CreateEmpty();
        var target = scene.AddObject(
            0,
            new PointF(200, 120),
            new SizeF(400, 240),
            0,
            0,
            Color.Teal,
            Color.Transparent,
            8,
            ShapeKind.Rectangle);
        var sourceFrame = TransformOverlayFrame.FromBounds(scene.GetObjectWorldBounds(target));
        if (target < 0
            || !scene.SetObjectDistortions(target, [new DistortWarp(sourceFrame, oneAnchor)])
            || !scene.TryGetObjectDistortions(target, out var stored)
            || stored.Length != 1
            || stored[0].Envelope.InsertedAnchorCount != 1)
        {
            throw new InvalidOperationException("A single-anchor Distort envelope did not persist through the scene.");
        }

        // Build a legacy envelope carrying three inserted anchors by splitting repeatedly, which the
        // public API still permits on a freshly built envelope through the internal path.
        var legacy = identity;
        var insertedCount = 0;
        if (legacy.TryInsertAnchor(DistortSide.Top, 0.25f, out legacy, out _)) insertedCount++;
        if (legacy.TryInsertAnchor(DistortSide.Top, 0.5f, out legacy, out _)) insertedCount++;
        if (legacy.TryInsertAnchor(DistortSide.Bottom, 0.5f, out legacy, out _)) insertedCount++;
        if (insertedCount == 0)
        {
            throw new InvalidOperationException("The legacy multi-anchor fixture could not insert any anchor.");
        }

        var collapsed = legacy.NormalizeInsertedAnchors();
        if (collapsed.InsertedAnchorCount > 1)
        {
            throw new InvalidOperationException(
                $"Normalizing a legacy envelope left {collapsed.InsertedAnchorCount} inserted anchors.");
        }

        if (!scene.SetObjectDistortions(target, [new DistortWarp(sourceFrame, legacy)])
            || !scene.TryGetObjectDistortions(target, out var normalizedStored)
            || normalizedStored.Length != 1
            || normalizedStored[0].Envelope.InsertedAnchorCount > 1)
        {
            throw new InvalidOperationException(
                $"Writing a legacy multi-anchor envelope did not collapse it to one anchor: "
                + $"count={(scene.TryGetObjectDistortions(target, out var probe) ? probe[0].Envelope.InsertedAnchorCount : -1)}.");
        }

        Console.WriteLine("distort_single_anchor_regression=ok");
    }

    private static float SignedContourArea(IReadOnlyList<PathBezierNode> contour) =>
        SignedContourArea(contour.Select(node => node.Anchor).ToArray());

    private static float SignedContourArea(IReadOnlyList<PointF> polygon)
    {
        if (polygon.Count < 3) return 0f;
        var origin = polygon[0];
        var area = 0d;
        for (var index = 1; index < polygon.Count - 1; index++)
        {
            var ax = (double)polygon[index].X - origin.X;
            var ay = (double)polygon[index].Y - origin.Y;
            var bx = (double)polygon[index + 1].X - origin.X;
            var by = (double)polygon[index + 1].Y - origin.Y;
            area += ax * by - bx * ay;
        }

        return (float)(area * 0.5d);
    }

}
