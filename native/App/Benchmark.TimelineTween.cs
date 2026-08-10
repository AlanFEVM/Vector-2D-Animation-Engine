namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunTimelineTweenRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        var source = scene.AddObject(
            0,
            new PointF(100, 200),
            new SizeF(120, 80),
            0,
            6,
            Color.FromArgb(255, 220, 40, 30),
            Color.FromArgb(255, 20, 30, 40),
            12,
            ShapeKind.Rectangle);
        AssertTimeline(scene.InsertTimelineKeyframe(0, 4), "Classic tween setup could not create its end keyframe.");
        var target = Enumerable.Range(0, scene.ObjectCount)
            .Single(index => scene.ObjectLayer[index] == 0 && scene.ObjectKeyframeFrame[index] == 4);
        scene.X[target] = 500;
        scene.Y[target] = 600;
        scene.Width[target] = 240;
        scene.Height[target] = 160;
        scene.Angle[target] = MathF.PI * 0.5f;
        scene.Argb[target] = Color.FromArgb(255, 20, 80, 230).ToArgb();

        var setupTrack = scene.Timeline.FindTrackByTargetId(scene.LayerIds[0])
            ?? throw new InvalidOperationException("Classic tween setup lost its timeline track.");
        AssertTimeline(
            setupTrack.TryResolveTweenSpan(2, 2, out var resolvedStart, out var resolvedEnd)
            && resolvedStart == 0
            && resolvedEnd == 4,
            "A frame inside two populated keys did not resolve to its surrounding tween span.");

        AssertTimeline(
            scene.TryCreateTimelineTween(0, 0, 4, TimelineTweenKind.Classic, out var classicError),
            $"Classic tween creation failed: {classicError}");
        var middle = Enumerable.Range(0, scene.ObjectCount)
            .Single(index => scene.ObjectLayer[index] == 0 && scene.ObjectKeyframeFrame[index] == 2);
        AssertTimeline(
            Math.Abs(scene.X[middle] - 300) <= 1
            && Math.Abs(scene.Y[middle] - 400) <= 1
            && Math.Abs(scene.Width[middle] - 180) <= 1
            && Math.Abs(scene.Height[middle] - 120) <= 1
            && Math.Abs(scene.Angle[middle] - MathF.PI * 0.25f) <= 0.0001f
            && scene.Argb[middle] == TimelineTweenArgb(
                Color.FromArgb(255, 220, 40, 30).ToArgb(),
                Color.FromArgb(255, 20, 80, 230).ToArgb(),
                0.5f)
            && scene.IsObjectActive(middle, 2),
            "Classic tween did not interpolate geometry and solid color at the middle frame.");
        var track = scene.Timeline.FindTrackByTargetId(scene.LayerIds[0])
            ?? throw new InvalidOperationException("Classic tween regression lost its timeline track.");
        AssertTimeline(
            track.EvaluateTween(2) is { Kind: TimelineTweenKind.Classic, StartFrame: 0, EndFrame: 4 },
            "Classic tween metadata was not retained across its materialized span.");

        var snapshot = scene.CreateSnapshot();
        var restored = new VectorScene();
        restored.RestoreSnapshot(snapshot);
        var restoredTrack = restored.Timeline.FindTrackByTargetId(restored.LayerIds[0])
            ?? throw new InvalidOperationException("Restored classic tween lost its timeline track.");
        AssertTimeline(
            restoredTrack.EvaluateTween(2) is { Kind: TimelineTweenKind.Classic },
            "Timeline snapshots did not preserve classic tween metadata.");
        AssertTimeline(restored.InsertTimelineFrame(0, 0), "Tween frame insertion did not change the timeline.");
        AssertTimeline(
            restoredTrack.EvaluateTween(3) is { StartFrame: 0, EndFrame: 5 }
            && Enumerable.Range(0, restored.ObjectCount)
                .Any(index => restored.ObjectKeyframeFrame[index] == 5),
            "Inserting a frame inside a tween did not preserve its start and remap its destination.");

        var shapeScene = new VectorScene();
        shapeScene.CreateEmpty();
        shapeScene.AddObject(
            0,
            new PointF(40, 40),
            new SizeF(80, 60),
            0,
            0,
            Color.Red,
            Color.Transparent,
            8,
            ShapeKind.Rectangle);
        shapeScene.AddObject(
            0,
            new PointF(180, 40),
            new SizeF(50, 90),
            0,
            0,
            Color.Yellow,
            Color.Transparent,
            8,
            ShapeKind.Ellipse);
        AssertTimeline(shapeScene.InsertTimelineKeyframe(0, 3), "Shape tween setup could not create its end keyframe.");
        var shapeTargets = Enumerable.Range(0, shapeScene.ObjectCount)
            .Where(index => shapeScene.ObjectKeyframeFrame[index] == 3)
            .OrderBy(index => shapeScene.ObjectOrder[index])
            .ToArray();
        var shapeTarget = shapeTargets[0];
        shapeScene.X[shapeTarget] = 340;
        shapeScene.Width[shapeTarget] = 180;
        shapeScene.Height[shapeTarget] = 180;
        shapeScene.ShapeKind[shapeTarget] = ShapeKind.Ellipse;
        shapeScene.Argb[shapeTarget] = Color.Blue.ToArgb();
        shapeScene.X[shapeTargets[1]] = 520;
        shapeScene.ShapeKind[shapeTargets[1]] = ShapeKind.Rectangle;
        AssertTimeline(
            shapeScene.TryCreateTimelineTween(0, 0, 3, TimelineTweenKind.Shape, out var shapeError),
            $"Shape tween creation failed: {shapeError}");
        AssertTimeline(
            shapeScene.Timeline.FindTrackByTargetId(shapeScene.LayerIds[0])?.EvaluateTween(1)
                is { Kind: TimelineTweenKind.Shape },
            "Shape tween metadata was not assigned to the selected span.");
        AssertTimeline(
            Enumerable.Range(0, shapeScene.ObjectCount).Count(index => shapeScene.ObjectKeyframeFrame[index] == 1) == 2
            && Enumerable.Range(0, shapeScene.ObjectCount)
                .Where(index => shapeScene.ObjectKeyframeFrame[index] == 1)
                .All(index => shapeScene.ShapeKind[index] == ShapeKind.Path),
            "Shape tween did not interpolate multiple or cross-primitive shapes as editable paths.");

        var maskScene = new VectorScene();
        maskScene.CreateEmpty();
        var maskLayer = maskScene.AddMaskLayer("Tween Mask");
        maskScene.AddObject(
            maskLayer,
            new PointF(20, 20),
            new SizeF(80, 80),
            0,
            0,
            Color.White,
            Color.Transparent,
            8,
            ShapeKind.Rectangle);
        AssertTimeline(
            maskScene.InsertTimelineKeyframe(maskLayer, 4),
            "Mask shape tween setup could not create its end keyframe.");
        var maskTarget = Enumerable.Range(0, maskScene.ObjectCount)
            .Single(index => maskScene.ObjectLayer[index] == maskLayer
                && maskScene.ObjectKeyframeFrame[index] == 4);
        maskScene.X[maskTarget] = 260;
        maskScene.Width[maskTarget] = 160;
        maskScene.Height[maskTarget] = 120;
        maskScene.ShapeKind[maskTarget] = ShapeKind.Ellipse;
        AssertTimeline(
            VectorScene.SupportsTimelineTweenLayer(DrawingLayerKind.Mask, TimelineTweenKind.Shape)
            && !VectorScene.SupportsTimelineTweenLayer(DrawingLayerKind.Mask, TimelineTweenKind.Classic),
            "Mask layers did not allow only the shape-tween command.");
        AssertTimeline(
            maskScene.TryCreateTimelineTween(
                maskLayer,
                0,
                4,
                TimelineTweenKind.Shape,
                out var maskTweenError),
            $"Mask layer shape tween creation failed: {maskTweenError}");
        var maskMiddle = Enumerable.Range(0, maskScene.ObjectCount)
            .Single(index => maskScene.ObjectLayer[index] == maskLayer
                && maskScene.ObjectKeyframeFrame[index] == 2);
        AssertTimeline(
            maskScene.GetLayerKind(maskLayer) == DrawingLayerKind.Mask
            && maskScene.ShapeKind[maskMiddle] == ShapeKind.Path
            && maskScene.IsObjectActive(maskMiddle, 2)
            && maskScene.Timeline.FindTrackByTargetId(maskScene.LayerIds[maskLayer])?.EvaluateTween(2)
                is { Kind: TimelineTweenKind.Shape },
            "Mask layer shape tween did not materialize or retain its tween metadata.");

        var rectangleScene = new VectorScene();
        rectangleScene.CreateEmpty();
        rectangleScene.AddObject(
            0,
            new PointF(0, 0),
            new SizeF(100, 60),
            0,
            4,
            Color.Teal,
            Color.White,
            8,
            ShapeKind.Rectangle);
        AssertTimeline(rectangleScene.InsertTimelineKeyframe(0, 4), "Rectangle tween setup could not create its end keyframe.");
        var rectangleTarget = Enumerable.Range(0, rectangleScene.ObjectCount)
            .Single(index => rectangleScene.ObjectKeyframeFrame[index] == 4);
        rectangleScene.Width[rectangleTarget] = 200;
        rectangleScene.Height[rectangleTarget] = 160;
        AssertTimeline(
            rectangleScene.TryCreateTimelineTween(0, 0, 4, TimelineTweenKind.Shape, out var rectangleTweenError),
            $"Rectangle shape tween creation failed: {rectangleTweenError}");
        var rectangleMiddle = Enumerable.Range(0, rectangleScene.ObjectCount)
            .Single(index => rectangleScene.ObjectKeyframeFrame[index] == 2);
        AssertTimeline(
            rectangleScene.TryGetPathWorldContours(rectangleMiddle, out var rectangleContours)
            && rectangleContours.Length == 1
            && TweenContourHasAllBoundsCorners(rectangleContours[0]),
            "Rectangle-to-rectangle shape tween chamfered corners because contour samples were misaligned.");
        rectangleScene.X[rectangleMiddle] = 999;
        var restoredRectangleScene = new VectorScene();
        restoredRectangleScene.RestoreSnapshot(rectangleScene.CreateSnapshot());
        var restoredRectangleMiddle = Enumerable.Range(0, restoredRectangleScene.ObjectCount)
            .Single(index => restoredRectangleScene.ObjectKeyframeFrame[index] == 2);
        AssertTimeline(
            Math.Abs(restoredRectangleScene.X[restoredRectangleMiddle]) <= 0.01f
            && restoredRectangleScene.TryGetPathWorldContours(restoredRectangleMiddle, out var restoredRectangleContours)
            && restoredRectangleContours.Length == 1
            && TweenContourHasAllBoundsCorners(restoredRectangleContours[0]),
            "Restoring a project did not regenerate materialized shape-tween frames from their endpoints.");

        var splitCircleScene = new VectorScene();
        splitCircleScene.CreateEmpty();
        splitCircleScene.AddObject(
            0,
            new PointF(80, 80),
            new SizeF(120, 120),
            0,
            12,
            Color.MediumTurquoise,
            Color.White,
            24,
            ShapeKind.Ellipse);
        AssertTimeline(
            splitCircleScene.InsertTimelineKeyframe(0, 4),
            "Split-circle shape tween setup could not create its end keyframe.");
        var splitCircleTarget = Enumerable.Range(0, splitCircleScene.ObjectCount)
            .Single(index => splitCircleScene.ObjectKeyframeFrame[index] == 4);
        AssertTimeline(
            splitCircleScene.RemoveObjectAt(splitCircleTarget),
            "Split-circle shape tween setup could not replace its end shape.");
        var splitCirclePreviousFrame = splitCircleScene.EditFrame;
        splitCircleScene.EditFrame = 4;
        var splitCircleTargetFill = splitCircleScene.AddObject(
            0,
            new PointF(320, 80),
            new SizeF(120, 120),
            0,
            0,
            Color.MediumTurquoise,
            Color.Transparent,
            24,
            ShapeKind.Ellipse);
        AddTweenCircleOutline(splitCircleScene, 0, new PointF(320, 80), 60, 12, Color.White);
        splitCircleScene.EditFrame = splitCirclePreviousFrame;
        var splitCircleTargetLines = Enumerable.Range(0, splitCircleScene.ObjectCount)
            .Where(index => splitCircleScene.ObjectKeyframeFrame[index] == 4
                && index != splitCircleTargetFill)
            .ToArray();
        var splitCircleTargetLinkCounts = splitCircleTargetLines
            .Select(line => splitCircleScene.CaptureFillBoundaryLineLinks(line, 4)
                .Count(link => link.FillObjectIndex == splitCircleTargetFill))
            .ToArray();
        AssertTimeline(
            Enumerable.Range(0, splitCircleScene.ObjectCount)
                .Count(index => splitCircleScene.ObjectKeyframeFrame[index] == 0) == 1
            && Enumerable.Range(0, splitCircleScene.ObjectCount)
                .Count(index => splitCircleScene.ObjectKeyframeFrame[index] == 4) == 5
            && splitCircleTargetLinkCounts.All(count => count == 1),
            $"Split-circle shape tween setup did not reproduce exact boundary links ({string.Join(',', splitCircleTargetLinkCounts)}).");
        AssertTimeline(
            splitCircleScene.TryCreateTimelineTween(
                0,
                0,
                4,
                TimelineTweenKind.Shape,
                out var splitCircleTweenError),
            $"A circle with materialized boundary strokes could not create a shape tween: {splitCircleTweenError}");
        var splitCircleMiddleObjects = Enumerable.Range(0, splitCircleScene.ObjectCount)
            .Where(index => splitCircleScene.ObjectKeyframeFrame[index] == 2)
            .ToArray();
        var splitCircleMiddleFill = splitCircleMiddleObjects
            .Single(index => splitCircleScene.ShapeKind[index] == ShapeKind.Path);
        var splitCircleBoundaryFrames = Enumerable.Range(1, 3)
            .Select(frame =>
            {
                var frameObjects = Enumerable.Range(0, splitCircleScene.ObjectCount)
                    .Where(index => splitCircleScene.ObjectKeyframeFrame[index] == frame)
                    .ToArray();
                var fills = frameObjects.Where(splitCircleScene.HasFill).ToArray();
                var boundaryLines = frameObjects
                    .Where(index => splitCircleScene.ShapeKind[index] == ShapeKind.Freeform)
                    .ToArray();
                var linkCounts = fills.Length == 1
                    ? boundaryLines.Select(line => splitCircleScene
                        .CaptureFillBoundaryLineLinks(line, frame)
                        .Count(link => link.FillObjectIndex == fills[0])).ToArray()
                    : [];
                return (
                    Frame: frame,
                    FillCount: fills.Length,
                    LineCount: boundaryLines.Length,
                    LinkCounts: linkCounts);
            })
            .ToArray();
        AssertTimeline(
            splitCircleMiddleObjects.Length == 5
            && splitCircleScene.FillContainsPoint(splitCircleMiddleFill, new PointF(200, 80))
            && splitCircleMiddleObjects.Count(index => splitCircleScene.ShapeKind[index] == ShapeKind.Freeform) == 4
            && splitCircleMiddleObjects
                .Where(index => splitCircleScene.ShapeKind[index] == ShapeKind.Freeform)
                .All(index => Color.FromArgb(splitCircleScene.StrokeArgb[index]).A is > 0 and < 255)
            && splitCircleBoundaryFrames.All(item => item.FillCount == 1
                && item.LineCount == 4
                && item.LinkCounts.All(count => count == 1)),
            $"Unequal circle fill/boundary objects detached during a shape tween ({string.Join(';', splitCircleBoundaryFrames.Select(item => $"{item.Frame}:{item.FillCount}/{item.LineCount}/{string.Join(',', item.LinkCounts)}"))}).");
        AssertTimeline(
            splitCircleScene.RefreshTimelineTweenMaterializationsAtEndpointFrame(4)
            && Enumerable.Range(1, 3).All(frame =>
            {
                var frameObjects = Enumerable.Range(0, splitCircleScene.ObjectCount)
                    .Where(index => splitCircleScene.ObjectKeyframeFrame[index] == frame)
                    .ToArray();
                var fills = frameObjects.Where(splitCircleScene.HasFill).ToArray();
                var boundaryLines = frameObjects
                    .Where(index => splitCircleScene.ShapeKind[index] == ShapeKind.Freeform)
                    .ToArray();
                return fills.Length == 1
                    && boundaryLines.Length == 4
                    && boundaryLines.All(line => splitCircleScene
                        .CaptureFillBoundaryLineLinks(line, frame)
                        .Any(link => link.FillObjectIndex == fills[0]));
            }),
            "Refreshing a shape tween endpoint detached its materialized boundary lines.");
        var restoredSplitCircleScene = new VectorScene();
        restoredSplitCircleScene.RestoreSnapshot(splitCircleScene.CreateSnapshot());
        AssertTimeline(
            Enumerable.Range(0, restoredSplitCircleScene.ObjectCount)
                .Count(index => restoredSplitCircleScene.ObjectKeyframeFrame[index] == 2) == 5,
            "Snapshot restore did not rebuild an unequal-object circle shape tween.");

        var liveColorScene = new VectorScene();
        liveColorScene.CreateEmpty();
        var liveColorSource = liveColorScene.AddObject(
            0,
            new PointF(80, 80),
            new SizeF(120, 120),
            0,
            12,
            Color.MediumSeaGreen,
            Color.White,
            24,
            ShapeKind.Ellipse);
        AssertTimeline(
            liveColorScene.InsertTimelineKeyframe(0, 4),
            "Live color shape tween setup could not create its end keyframe.");
        AssertTimeline(
            liveColorScene.TryCreateTimelineTween(
                0,
                0,
                4,
                TimelineTweenKind.Shape,
                out var liveColorTweenError),
            $"Live color shape tween setup could not create its tween: {liveColorTweenError}");
        var liveColorTarget = Enumerable.Range(0, liveColorScene.ObjectCount)
            .Single(index => liveColorScene.ObjectKeyframeFrame[index] == 4);
        var liveColorTargetKey = new DrawingElementKey(
            liveColorTarget,
            DrawingElementKind.Fill,
            0);
        var liveColorMaterialized = liveColorScene.MaterializeSelectedParts(
            [liveColorTargetKey],
            4);
        var liveColorTargetFill = liveColorMaterialized.Parts
            .Where(part => part.Source == liveColorTargetKey)
            .Select(part => part.Result.ObjectIndex)
            .Single();
        liveColorScene.Argb[liveColorTargetFill] = Color.RoyalBlue.ToArgb();
        liveColorScene.DisableLinearGradient(liveColorTargetFill);
        AssertTimeline(
            liveColorMaterialized.Success
            && liveColorMaterialized.Changed
            && liveColorScene.Timeline.FindTrackByTargetId(liveColorScene.LayerIds[0])
                ?.EvaluateTween(2) is { Kind: TimelineTweenKind.Shape }
            && liveColorScene.RefreshTimelineTweenMaterializationsAtEndpointFrame(4),
            "Changing a materialized endpoint fill did not refresh its shape tween.");
        var liveColorMiddleObjects = Enumerable.Range(0, liveColorScene.ObjectCount)
            .Where(index => liveColorScene.ObjectKeyframeFrame[index] == 2)
            .ToArray();
        var liveColorMiddleFill = liveColorMiddleObjects.Single(liveColorScene.HasFill);
        AssertTimeline(
            liveColorMiddleObjects.Length > 1
            && liveColorScene.Argb[liveColorMiddleFill] == TimelineTweenArgb(
                liveColorScene.Argb[liveColorSource],
                Color.RoyalBlue.ToArgb(),
                0.5f),
            "A post-creation endpoint fill color did not interpolate into the shape tween.");

        var strokeScene = new VectorScene();
        strokeScene.CreateEmpty();
        strokeScene.AddFreehandStroke(
            0,
            [new PointF(0, 0), new PointF(80, 20), new PointF(140, 90)],
            12,
            Color.DarkBlue,
            brushStroke: false,
            8);
        AssertTimeline(strokeScene.InsertTimelineKeyframe(0, 4), "Stroke tween setup could not create its end keyframe.");
        var strokeTarget = Enumerable.Range(0, strokeScene.ObjectCount)
            .Single(index => strokeScene.ObjectKeyframeFrame[index] == 4);
        strokeScene.TransformObjects(
            [strokeTarget],
            point => new PointF(point.X + 240, point.Y + 120));
        AssertTimeline(
            strokeScene.TryCreateTimelineTween(0, 0, 4, TimelineTweenKind.Shape, out var strokeTweenError),
            $"Open vector stroke shape tween creation failed: {strokeTweenError}");
        var strokeMiddle = Enumerable.Range(0, strokeScene.ObjectCount)
            .Single(index => strokeScene.ObjectKeyframeFrame[index] == 2);
        AssertTimeline(
            strokeScene.ShapeKind[strokeMiddle] == ShapeKind.Freeform
            && strokeScene.TryGetFreehandWorldPoints(strokeMiddle, out var strokeMiddlePoints)
            && strokeMiddlePoints.Length >= 2,
            "Shape tween did not materialize an editable interpolated vector stroke.");
        RunShapeTweenLineMatchingRegression();
        RunOutlinedShapeTweenBoundaryRegression();

        var invalidScene = new VectorScene();
        invalidScene.CreateEmpty();
        invalidScene.AddObject(0, new PointF(0, 0), new SizeF(20, 20), 0, 0, Color.Red, Color.Transparent, 3);
        invalidScene.AddObject(0, new PointF(40, 0), new SizeF(20, 20), 0, 0, Color.Blue, Color.Transparent, 3);
        AssertTimeline(invalidScene.InsertTimelineKeyframe(0, 2), "Multi-object tween setup could not create its end keyframe.");
        AssertTimeline(
            !invalidScene.TryCreateTimelineTween(0, 0, 2, TimelineTweenKind.Classic, out _),
            "Classic tween accepted a frame containing multiple drawing objects.");

        RunTimelineTweenInstanceRegression();
        RunTimelineTweenGradientRegression();
        RunTimelineTweenComplexTopologyRegression();
        RunTimelineTweenCurveRegression();
    }

    private static void RunShapeTweenLineMatchingRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty(1, 5);
        var stationary = scene.AddLineSegment(
            0,
            new PointF(0, 0),
            new PointF(100, 0),
            8,
            Color.Transparent,
            Color.Black,
            4);
        var moving = scene.AddLineSegment(
            0,
            new PointF(300, 100),
            new PointF(400, 100),
            6,
            Color.Transparent,
            Color.Crimson,
            4);
        var stationaryOrder = scene.ObjectOrder[stationary];
        var movingOrder = scene.ObjectOrder[moving];

        AssertTimeline(
            scene.InsertTimelineKeyframe(0, 4),
            "Line shape tween setup could not create its end keyframe.");
        var movingTarget = Enumerable.Range(0, scene.ObjectCount)
            .Single(index => scene.ObjectKeyframeFrame[index] == 4
                && scene.ObjectOrder[index] == movingOrder);
        scene.SetLineEndpoint(
            movingTarget,
            startEndpoint: true,
            endpoint: new PointF(-200, 100),
            oppositeEndpoint: new PointF(-300, 100),
            control: PointF.Empty,
            keepStraight: true);

        AssertTimeline(
            scene.TryCreateTimelineTween(0, 0, 4, TimelineTweenKind.Shape, out var error),
            $"Line shape tween creation failed: {error}");
        for (var frame = 1; frame < 4; frame++)
        {
            var frameObjects = Enumerable.Range(0, scene.ObjectCount)
                .Where(index => scene.ObjectLayer[index] == 0
                    && scene.ObjectKeyframeFrame[index] == frame)
                .ToArray();
            AssertTimeline(
                frameObjects.Length == 2
                && frameObjects.Select(index => scene.ObjectOrder[index]).Order().SequenceEqual(
                    new[] { stationaryOrder, movingOrder }.Order()),
                "A line shape tween materialized duplicate or mismatched intermediate strokes.");

            var stationaryFrame = frameObjects.Single(index => scene.ObjectOrder[index] == stationaryOrder);
            AssertTimeline(
                scene.ShapeKind[stationaryFrame] == ShapeKind.Line
                && scene.TryGetLineCubic(
                    stationaryFrame,
                    out var start,
                    out var control1,
                    out var control2,
                    out var end)
                && SamePoint(start, new PointF(0, 0))
                && SamePoint(control1, new PointF(33, 0))
                && SamePoint(control2, new PointF(67, 0))
                && SamePoint(end, new PointF(100, 0))
                && Math.Abs(scene.Stroke[stationaryFrame] - 8) <= 0.001f
                && scene.StrokeArgb[stationaryFrame] == Color.Black.ToArgb(),
                "An unchanged line moved, changed appearance, or lost its native curve during a shape tween.");
        }

        var middle = Enumerable.Range(0, scene.ObjectCount)
            .Single(index => scene.ObjectKeyframeFrame[index] == 2
                && scene.ObjectOrder[index] == movingOrder);
        AssertTimeline(
            scene.ShapeKind[middle] == ShapeKind.Line
            && scene.TryGetLineCubic(middle, out var middleStart, out var middleControl1, out var middleControl2, out var middleEnd)
            && SamePoint(middleStart, new PointF(0, 100))
            && SamePoint(middleControl1, new PointF(33, 100))
            && SamePoint(middleControl2, new PointF(67, 100))
            && SamePoint(middleEnd, new PointF(100, 100))
            && Math.Abs(scene.Stroke[middle] - 6) <= 0.001f
            && scene.StrokeArgb[middle] == Color.Crimson.ToArgb(),
            "A reversed line endpoint produced a fold, style blend, or sampled-polyline transition.");

        scene.SetLineEndpoint(
            movingTarget,
            startEndpoint: true,
            endpoint: new PointF(-400, 200),
            oppositeEndpoint: new PointF(-500, 200),
            control: PointF.Empty,
            keepStraight: true);
        AssertTimeline(
            scene.RefreshTimelineTweenMaterializationsAtEndpointFrame(4),
            "Editing a line tween endpoint did not refresh its materialized frames.");
        var refreshedFrameObjects = Enumerable.Range(0, scene.ObjectCount)
            .Where(index => scene.ObjectKeyframeFrame[index] == 2)
            .ToArray();
        var refreshedStationary = refreshedFrameObjects
            .Single(index => scene.ObjectOrder[index] == stationaryOrder);
        AssertTimeline(
            refreshedFrameObjects.Length == 2
            && scene.TryGetLineCubic(
                refreshedStationary,
                out var refreshedStart,
                out _,
                out _,
                out var refreshedEnd)
            && SamePoint(refreshedStart, new PointF(0, 0))
            && SamePoint(refreshedEnd, new PointF(100, 0)),
            "Refreshing an edited line tween rematched or duplicated its unchanged stroke.");

        static bool SamePoint(PointF left, PointF right) =>
            Math.Abs(left.X - right.X) <= 0.001f
            && Math.Abs(left.Y - right.Y) <= 0.001f;
    }

    private static void RunOutlinedShapeTweenBoundaryRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty(1, 5);
        scene.AddObject(
            0,
            new PointF(120, 90),
            new SizeF(240, 180),
            0,
            12,
            Color.MediumTurquoise,
            Color.White,
            40,
            ShapeKind.Rectangle);
        AssertTimeline(
            scene.InsertTimelineKeyframe(0, 4),
            "Outlined shape tween setup could not create its end keyframe.");
        var target = Enumerable.Range(0, scene.ObjectCount)
            .Single(index => scene.ObjectKeyframeFrame[index] == 4);
        scene.TransformObjects(
            [target],
            point =>
            {
                var dx = point.X - 120;
                var dy = point.Y - 90;
                return new PointF(
                    360 + dx * 1.25f + dy * 0.35f,
                    180 + dy * 1.4f);
            });
        var targetBezierPrepared = scene.TryConvertFillToBezierPath(target);
        var targetTopBeforeMaterialization = targetBezierPrepared
            ? scene.GetEditableFillBezierSegmentParts(target)
                .OrderBy(part => (part.Start.Y + part.End.Y) * 0.5f)
                .FirstOrDefault()
            : default;
        var targetTopControl1 = new PointF(
            targetTopBeforeMaterialization.Start.X
                + (targetTopBeforeMaterialization.End.X - targetTopBeforeMaterialization.Start.X) / 3f,
            targetTopBeforeMaterialization.Start.Y - 120);
        var targetTopControl2 = new PointF(
            targetTopBeforeMaterialization.Start.X
                + (targetTopBeforeMaterialization.End.X - targetTopBeforeMaterialization.Start.X) * 2f / 3f,
            targetTopBeforeMaterialization.End.Y - 120);
        var targetTopCurved = targetBezierPrepared
            && scene.SetPathBezierSegment(
                target,
                targetTopBeforeMaterialization.PartIndex,
                targetTopBeforeMaterialization.Start,
                targetTopControl1,
                targetTopControl2,
                targetTopBeforeMaterialization.End);
        var source = Enumerable.Range(0, scene.ObjectCount)
            .Single(index => scene.ObjectKeyframeFrame[index] == 0);
        var sourceKey = new DrawingElementKey(source, DrawingElementKind.Fill, 0);
        var sourceMaterialized = scene.MaterializeSelectedParts([sourceKey], 0);
        target = Enumerable.Range(0, scene.ObjectCount)
            .Single(index => scene.ObjectKeyframeFrame[index] == 4);
        var targetKey = new DrawingElementKey(target, DrawingElementKind.Fill, 0);
        var targetMaterialized = scene.MaterializeSelectedParts([targetKey], 4);
        var sourceObjects = Enumerable.Range(0, scene.ObjectCount)
            .Where(index => scene.ObjectKeyframeFrame[index] == 0)
            .ToArray();
        var targetObjects = Enumerable.Range(0, scene.ObjectCount)
            .Where(index => scene.ObjectKeyframeFrame[index] == 4)
            .ToArray();
        var sourceFill = sourceObjects.SingleOrDefault(scene.HasFill, -1);
        var targetFill = targetObjects.SingleOrDefault(scene.HasFill, -1);
        var sourceLines = sourceObjects
            .Where(index => scene.ShapeKind[index] == ShapeKind.Line)
            .ToArray();
        var targetLines = targetObjects
            .Where(index => scene.ShapeKind[index] == ShapeKind.Line)
            .ToArray();
        AssertTimeline(
            sourceMaterialized.Success
            && sourceMaterialized.Changed
            && targetMaterialized.Success
            && targetMaterialized.Changed
            && sourceFill >= 0
            && targetFill >= 0
            && sourceObjects.Length == 5
            && targetObjects.Length == 5
            && sourceLines.Length == 4
            && targetLines.Length == 4
            && targetTopCurved
            && scene.Stroke[sourceFill] == 0
            && scene.Stroke[targetFill] == 0
            && sourceLines.All(line => scene.CaptureFillBoundaryLineLinks(line, 0)
                .Count(link => link.FillObjectIndex == sourceFill) == 1)
            && targetLines.All(line => scene.CaptureFillBoundaryLineLinks(line, 4)
                .Count(link => link.FillObjectIndex == targetFill) == 1),
            "Outlined shape tween setup did not separate exact boundaries at both endpoints.");
        AssertTimeline(
            scene.TryCreateTimelineTween(0, 0, 4, TimelineTweenKind.Shape, out var error),
            $"Outlined shape tween creation failed: {error}");
        AssertTimeline(
            OutlinedBoundaryFramesAreLinked(scene),
            $"An outlined shape tween separated its materialized boundary lines from the fill ({OutlinedBoundaryDiagnostics(scene)}).");

        scene.TransformObjects(
            targetObjects,
            point => new PointF(point.X + 30, point.Y + 20));
        AssertTimeline(
            targetLines.All(line => scene.CaptureFillBoundaryLineLinks(line, 4)
                .Count(link => link.FillObjectIndex == targetFill) == 1)
            && scene.RefreshTimelineTweenMaterializationsAtEndpointFrame(4)
            && OutlinedBoundaryFramesAreLinked(scene),
            "Refreshing a transformed outlined endpoint detached its boundary lines.");

        static bool OutlinedBoundaryFramesAreLinked(VectorScene tweenScene) =>
            Enumerable.Range(1, 3).All(frame =>
            {
                var frameObjects = Enumerable.Range(0, tweenScene.ObjectCount)
                    .Where(index => tweenScene.ObjectKeyframeFrame[index] == frame)
                    .ToArray();
                var fills = frameObjects.Where(tweenScene.HasFill).ToArray();
                var lines = frameObjects
                    .Where(index => tweenScene.ShapeKind[index] == ShapeKind.Freeform)
                    .ToArray();
                return frameObjects.Length == 5
                    && fills.Length == 1
                    && lines.Length == 4
                    && lines.All(line => Color.FromArgb(tweenScene.StrokeArgb[line]).A == 255)
                    && lines.All(line => MaximumBoundaryDistance(tweenScene, line, fills[0]) <= 2f);
            });

        static string OutlinedBoundaryDiagnostics(VectorScene tweenScene) =>
            string.Join(';', Enumerable.Range(1, 3).Select(frame =>
            {
                var frameObjects = Enumerable.Range(0, tweenScene.ObjectCount)
                    .Where(index => tweenScene.ObjectKeyframeFrame[index] == frame)
                    .ToArray();
                var fills = frameObjects.Where(tweenScene.HasFill).ToArray();
                var lines = frameObjects
                    .Where(index => tweenScene.ShapeKind[index] == ShapeKind.Freeform)
                    .ToArray();
                var distances = fills.Length == 1
                    ? lines.Select(line => MaximumBoundaryDistance(tweenScene, line, fills[0]))
                    : [];
                return $"{frame}:{frameObjects.Length}/{fills.Length}/{lines.Length}/{string.Join(',', distances.Select(distance => distance.ToString("F4")))}";
            }));

        static float MaximumBoundaryDistance(VectorScene tweenScene, int line, int fill)
        {
            if (!tweenScene.TryGetFreehandWorldPoints(line, out var linePoints)
                || !tweenScene.TryGetPathWorldContours(fill, out var fillContours)
                || linePoints.Length < 2
                || fillContours.Length == 0)
            {
                return float.MaxValue;
            }

            var maximum = 0f;
            foreach (var point in linePoints)
            {
                var minimum = float.MaxValue;
                foreach (var contour in fillContours)
                {
                    for (var index = 0; index < contour.Length; index++)
                    {
                        minimum = Math.Min(
                            minimum,
                            PointSegmentDistance(point, contour[index], contour[(index + 1) % contour.Length]));
                    }
                }
                maximum = Math.Max(maximum, minimum);
            }
            return maximum;
        }

        static float PointSegmentDistance(PointF point, PointF start, PointF end)
        {
            var dx = end.X - start.X;
            var dy = end.Y - start.Y;
            var lengthSquared = dx * dx + dy * dy;
            var amount = lengthSquared <= 0.000001f
                ? 0
                : Math.Clamp(
                    ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared,
                    0,
                    1);
            var x = start.X + dx * amount;
            var y = start.Y + dy * amount;
            var distanceX = point.X - x;
            var distanceY = point.Y - y;
            return MathF.Sqrt(distanceX * distanceX + distanceY * distanceY);
        }
    }

    private static void RunTimelineTweenInstanceRegression()
    {
        var project = VectorProject.CreateEmpty();
        var child = project.DrawingObjects[0];
        child.Scene.CreateEmpty(1, 9);
        child.Scene.AddObject(
            0,
            PointF.Empty,
            new SizeF(80, 60),
            0,
            0,
            Color.White,
            Color.Transparent,
            8,
            ShapeKind.Rectangle);

        var host = project.AddDrawingObject("Classic instance tween host");
        host.Scene.CreateEmpty(1, 9);
        AssertTimeline(
            project.TryAddDrawingObjectInstance(host.Id, child.Id, PointF.Empty, out var instance)
            && instance is not null,
            "Classic instance tween setup could not create its nested drawing object instance.");

        var source = instance!.EvaluateState(0) with
        {
            X = 0,
            Y = 20,
            Z = 1,
            RotationX = 170,
            RotationY = 20,
            RotationZ = 350,
            SkewX = 0,
            SkewY = -10,
            ScaleX = 1,
            ScaleY = 1.5f,
            ScaleZ = 1,
            Alpha = 0.25f,
            TintArgb = Color.Red.ToArgb(),
            PlaybackMode = DrawingObjectPlaybackMode.HoldFrame,
            HoldFrame = 0
        };
        AssertTimeline(instance.SetStateAtFrame(0, source), "Classic instance tween could not set its source state.");
        AssertTimeline(
            host.Scene.InsertTimelineKeyframe(0, 8),
            "Classic instance tween could not create its destination keyframe.");
        var target = source with
        {
            X = 800,
            Y = 220,
            Z = 5,
            RotationX = -170,
            RotationY = 100,
            RotationZ = 10,
            SkewX = 20,
            SkewY = 30,
            ScaleX = 3,
            ScaleY = 2.5f,
            ScaleZ = 2,
            Alpha = 0.75f,
            TintArgb = Color.Blue.ToArgb(),
            PlaybackMode = DrawingObjectPlaybackMode.Loop
        };
        AssertTimeline(instance.SetStateAtFrame(8, target), "Classic instance tween could not set its target state.");
        AssertTimeline(
            host.CanCreateTimelineTween(0, 0, 8, TimelineTweenKind.Classic, out var canCreateError),
            $"A single nested drawing object instance was not eligible for a classic tween: {canCreateError}");
        AssertTimeline(
            !host.CanCreateTimelineTween(0, 0, 8, TimelineTweenKind.Shape, out _),
            "A nested drawing object instance was incorrectly eligible for a shape tween.");
        AssertTimeline(
            host.TryCreateTimelineTween(0, 0, 8, TimelineTweenKind.Classic, out var createError),
            $"A single nested drawing object instance could not create a classic tween: {createError}");

        var middle = instance.EvaluateState(4);
        AssertTimeline(
            Math.Abs(middle.X - 400) <= 0.01f
            && Math.Abs(middle.Y - 120) <= 0.01f
            && Math.Abs(middle.Z - 3) <= 0.01f
            && Math.Abs(middle.RotationX - 180) <= 0.01f
            && Math.Abs(middle.RotationY - 60) <= 0.01f
            && Math.Abs(middle.RotationZ - 360) <= 0.01f
            && Math.Abs(middle.SkewX - 10) <= 0.01f
            && Math.Abs(middle.SkewY - 10) <= 0.01f
            && Math.Abs(middle.ScaleX - 2) <= 0.01f
            && Math.Abs(middle.ScaleY - 2) <= 0.01f
            && Math.Abs(middle.ScaleZ - 1.5f) <= 0.01f
            && Math.Abs(middle.Alpha - 0.5f) <= 0.001f
            && middle.TintArgb == Color.FromArgb(255, 128, 0, 128).ToArgb()
            && middle.PlaybackMode == DrawingObjectPlaybackMode.HoldFrame,
            "Classic instance tween did not interpolate its transform and appearance state deterministically.");

        var anchors = new TweenCurveAnchor[]
        {
            new(0, 0),
            new(0.5f, 0.2f),
            new(1, 1)
        };
        AssertTimeline(
            host.ReplaceTimelineTweenCurve(0, 0, 8, anchors)
            && Math.Abs(instance.EvaluateState(4).X - 160) <= 0.01f,
            "A custom curve did not rematerialize the nested instance tween.");

        target = target with { X = 1000, TintArgb = Color.Lime.ToArgb() };
        AssertTimeline(
            instance.SetStateAtFrame(8, target)
            && host.RefreshTimelineTweenMaterializationsAtEndpointFrame(8)
            && Math.Abs(instance.EvaluateState(4).X - 200) <= 0.01f,
            "Editing a nested instance tween endpoint did not refresh its intermediate states.");

        var layerId = host.Scene.LayerIds[0];
        var heldTrack = host.Timeline.FindTrackByTargetId(layerId)
            ?? throw new InvalidOperationException("Nested instance Auto Key regression lost its timeline track.");
        host.Timeline.SetTrackDuration(heldTrack.Id, 10);
        var heldEditFrame = MainForm.ResolveInstanceStateEditFrame(
            host.Timeline,
            layerId,
            9,
            autoKeyframeEnabled: false);
        target = target with { X = 1200 };
        AssertTimeline(
            heldEditFrame == 8
            && MainForm.ResolveInstanceStateEditFrame(
                host.Timeline,
                layerId,
                8,
                autoKeyframeEnabled: false) == 8
            && MainForm.ResolveInstanceStateEditFrame(
                host.Timeline,
                layerId,
                9,
                autoKeyframeEnabled: true) == 9
            && instance.SetStateAtFrame(heldEditFrame, target)
            && host.RefreshTimelineTweenMaterializationsAtEndpointFrame(heldEditFrame)
            && heldTrack.Keyframes.All(keyframe => keyframe.Frame != 9)
            && instance.StateKeyframes.All(keyframe => keyframe.Frame != 9)
            && Math.Abs(instance.EvaluateState(4).X - 240) <= 0.01f
            && Math.Abs(instance.EvaluateState(9).X - 1200) <= 0.01f,
            "Disabling Auto Key edited the playhead instead of the held source key or failed to rematerialize its tween.");

        AssertTimeline(
            host.Scene.InsertTimelineFrame(0, 4),
            "Nested instance tween frame insertion did not change the timeline.");
        host.InsertInstanceStateFrames(layerId, 4, 1);
        AssertTimeline(
            host.RefreshInstanceTimelineTweenMaterializationsInLayer(layerId),
            "Nested instance tween frame insertion did not rematerialize its shifted span.");
        var track = host.Timeline.FindTrackByTargetId(layerId)
            ?? throw new InvalidOperationException("Nested instance tween lost its timeline track.");
        var shiftedTween = track.EvaluateTween(4)
            ?? throw new InvalidOperationException("Nested instance tween lost its shifted metadata.");
        var shiftedExpected = DrawingObjectInstanceDefinition.InterpolateState(
            source,
            target,
            shiftedTween.ProgressAt(4));
        AssertTimeline(
            shiftedTween.EndFrame == 9
            && Math.Abs(instance.EvaluateState(4).X - shiftedExpected.X) <= 0.01f,
            "Nested instance tween did not recompute its timing after a frame insertion.");

        var composition = new VectorScene();
        var compositionResult = SceneCompositionBuilder.BuildDrawingObjectChildren(
            composition,
            host,
            project.DrawingObjects,
            4);
        var compositionBounds = composition.ObjectCount == 1
            ? composition.GetObjectWorldBounds(0)
            : RectangleF.Empty;
        AssertTimeline(
            composition.ObjectCount == 1
            && compositionResult.TryGetOwner(0, out var owner)
            && string.Equals(owner.InstanceId, instance.Id, StringComparison.Ordinal)
            && Math.Abs(compositionBounds.Left + compositionBounds.Width * 0.5f - shiftedExpected.X) <= 1
            && Math.Abs(compositionBounds.Top + compositionBounds.Height * 0.5f - shiftedExpected.Y) <= 1,
            "Scene composition did not render the materialized nested instance tween state.");

        AssertTimeline(
            host.RemoveTimelineTween(0, 0, 9)
            && track.EvaluateTween(4) is null
            && instance.StateKeyframes.Any(keyframe =>
                keyframe.Frame == 4
                && Math.Abs(keyframe.State.X - shiftedExpected.X) <= 0.01f),
            "Removing a nested instance tween discarded its materialized intermediate state.");

        var mixedHost = project.AddDrawingObject("Mixed tween host");
        mixedHost.Scene.CreateEmpty(1, 5);
        mixedHost.Scene.AddObject(
            0,
            PointF.Empty,
            new SizeF(20, 20),
            0,
            0,
            Color.White,
            Color.Transparent,
            4);
        AssertTimeline(
            project.TryAddDrawingObjectInstance(mixedHost.Id, child.Id, PointF.Empty, out _)
            && mixedHost.Scene.InsertTimelineKeyframe(0, 4)
            && !mixedHost.CanCreateTimelineTween(0, 0, 4, TimelineTweenKind.Classic, out _),
            "A layer mixing local vectors and a nested instance was incorrectly eligible for a classic tween.");

        var multipleHost = project.AddDrawingObject("Multiple instance tween host");
        multipleHost.Scene.CreateEmpty(1, 5);
        AssertTimeline(
            project.TryAddDrawingObjectInstance(multipleHost.Id, child.Id, PointF.Empty, out _)
            && project.TryAddDrawingObjectInstance(multipleHost.Id, child.Id, new PointF(40, 0), out _)
            && multipleHost.Scene.InsertTimelineKeyframe(0, 4)
            && !multipleHost.CanCreateTimelineTween(0, 0, 4, TimelineTweenKind.Classic, out _),
            "A layer containing multiple nested instances was incorrectly eligible for a classic tween.");
    }

    private static void RunTimelineTweenCurveRegression()
    {
        var linear = new TimelineTween(0, 8, TimelineTweenKind.Classic);
        AssertTimeline(
            Math.Abs(linear.ProgressAt(4) - 0.5f) <= 0.0001f
            && linear.CurveAnchors.SequenceEqual(new TweenCurveAnchor[] { new(0, 0), new(1, 1) }),
            "A default or legacy tween did not retain linear timing.");

        var curveScene = new VectorScene();
        curveScene.CreateEmpty();
        curveScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(80, 60),
            0,
            0,
            Color.Red,
            Color.Transparent,
            8,
            ShapeKind.Rectangle);
        AssertTimeline(
            curveScene.InsertTimelineKeyframe(0, 8),
            "Tween curve setup could not create its end keyframe.");
        var target = Enumerable.Range(0, curveScene.ObjectCount)
            .Single(index => curveScene.ObjectKeyframeFrame[index] == 8);
        curveScene.X[target] = 800;
        AssertTimeline(
            curveScene.TryCreateTimelineTween(0, 0, 8, TimelineTweenKind.Classic, out var createError),
            $"Tween curve setup could not create a classic tween: {createError}");
        var middle = Enumerable.Range(0, curveScene.ObjectCount)
            .Single(index => curveScene.ObjectKeyframeFrame[index] == 4);
        AssertTimeline(
            Math.Abs(curveScene.X[middle] - 400) <= 1,
            "A newly created tween did not materialize with linear timing.");

        var anchors = new TweenCurveAnchor[]
        {
            new(0, 0),
            new(0.5f, 0.2f),
            new(1, 1)
        };
        var changedCount = 0;
        curveScene.Timeline.Changed += (_, _) => changedCount++;
        AssertTimeline(
            curveScene.ReplaceTimelineTweenCurve(0, 0, 8, anchors)
            && changedCount == 1
            && Math.Abs(curveScene.X[middle] - 160) <= 1,
            "Replacing a tween curve did not raise one change or rematerialize its middle frame.");

        curveScene.X[target] = 1000;
        AssertTimeline(
            curveScene.RefreshTimelineTweenMaterializationsAtEndpointFrame(8)
            && Math.Abs(curveScene.X[middle] - 200) <= 1,
            "Moving a tween endpoint did not rematerialize intermediate frames with its custom curve.");
        curveScene.X[target] = 800;
        AssertTimeline(
            curveScene.RefreshTimelineTweenMaterializationsAtEndpointFrame(8)
            && Math.Abs(curveScene.X[middle] - 160) <= 1,
            "Restoring a tween endpoint did not rematerialize its intermediate frames.");

        var track = curveScene.Timeline.FindTrackByTargetId(curveScene.LayerIds[0])
            ?? throw new InvalidOperationException("Tween curve regression lost its timeline track.");
        var curvedTween = track.EvaluateTween(4)
            ?? throw new InvalidOperationException("Tween curve metadata was not retained on the span.");
        var previousProgress = 0f;
        for (var sample = 0; sample <= 100; sample++)
        {
            var progress = curvedTween.ProgressAtNormalized(sample / 100f);
            AssertTimeline(
                progress >= previousProgress - 0.0001f && progress is >= 0 and <= 1,
                "Monotone tween anchors produced a decreasing or out-of-range progress value.");
            previousProgress = progress;
        }

        var detachedAnchors = curvedTween.CurveAnchors;
        detachedAnchors[1] = new TweenCurveAnchor(0.5f, 0.9f);
        AssertTimeline(
            Math.Abs(curvedTween.ProgressAtNormalized(0.5f) - 0.2f) <= 0.0001f,
            "The tween curve getter exposed mutable internal anchor storage.");
        AssertTimeline(
            !curveScene.Timeline.ReplaceTweenCurve(
                track.Id,
                0,
                8,
                [new TweenCurveAnchor(0, 0), new TweenCurveAnchor(0.5f, 0.8f), new TweenCurveAnchor(1, 0.7f)])
            && changedCount == 1,
            "Tween curve validation accepted decreasing values or raised a spurious change.");

        var resolvedSelection = TimelineStrip.ResolveSingleTweenSelection(
            curveScene.Timeline,
            [new TimelineFrameCell(track.Id, 2), new TimelineFrameCell(track.Id, 4)]);
        AssertTimeline(
            resolvedSelection is { StartFrame: 0, EndFrame: 8 }
            && TimelineStrip.ResolveSingleTweenSelection(
                curveScene.Timeline,
                [new TimelineFrameCell(track.Id, 4), new TimelineFrameCell(track.Id, 9)]) is null,
            "Timeline frame selection did not resolve exactly one tween span.");

        var snapshot = curveScene.CreateSnapshot();
        var restored = new VectorScene();
        restored.RestoreSnapshot(snapshot);
        var restoredTween = restored.Timeline.FindTrackByTargetId(restored.LayerIds[0])?.EvaluateTween(4);
        AssertTimeline(
            restoredTween is { } snapshotTween && snapshotTween.CurveAnchors.SequenceEqual(anchors),
            "Scene snapshot restore lost custom tween curve anchors.");

        var inserted = new VectorScene();
        inserted.RestoreSnapshot(snapshot);
        AssertTimeline(inserted.InsertTimelineFrame(0, 4), "Tween curve frame insertion did not change the timeline.");
        var insertedTween = inserted.Timeline.FindTrackByTargetId(inserted.LayerIds[0])?.EvaluateTween(5);
        AssertTimeline(
            insertedTween is { StartFrame: 0, EndFrame: 9 } shiftedTween
            && shiftedTween.CurveAnchors.SequenceEqual(anchors),
            "Inserting a frame inside a tween did not preserve and shift its custom curve metadata.");

        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "Vector2DAnimationEngine",
            $"tween-curve-regression-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            const string drawingObjectId = "tween-curve-regression";
            var svgPath = Path.Combine(temporaryRoot, "TweenCurve.svg");
            DrawingObjectSvgCodec.Write(svgPath, drawingObjectId, snapshot);
            var persisted = new VectorScene();
            persisted.RestoreSnapshot(DrawingObjectSvgCodec.Read(svgPath, drawingObjectId));
            var persistedTween = persisted.Timeline.FindTrackByTargetId(persisted.LayerIds[0])?.EvaluateTween(4);
            AssertTimeline(
                persistedTween is { } svgTween && svgTween.CurveAnchors.SequenceEqual(anchors),
                "Drawing-object SVG persistence lost custom tween curve anchors.");
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }

        AssertTimeline(
            curveScene.RemoveTimelineTween(0, 0, 8)
            && track.EvaluateTween(4) is null
            && Enumerable.Range(0, curveScene.ObjectCount).Any(index =>
                curveScene.ObjectKeyframeFrame[index] == 4
                && curveScene.IsObjectActive(index, 4)
                && Math.Abs(curveScene.X[index] - 160) <= 1),
            "Removing a tween discarded or changed its materialized intermediate frame.");
    }

    private static void RunTimelineTweenGradientRegression()
    {
        var solidToGradient = new VectorScene();
        solidToGradient.CreateEmpty();
        solidToGradient.AddObject(
            0,
            new PointF(100, 100),
            new SizeF(160, 120),
            0,
            0,
            Color.Red,
            Color.Transparent,
            8,
            ShapeKind.Rectangle);
        AssertTimeline(
            solidToGradient.InsertTimelineKeyframe(0, 4),
            "Solid-to-gradient tween setup could not create its end keyframe.");
        var solidGradientTarget = Enumerable.Range(0, solidToGradient.ObjectCount)
            .Single(index => solidToGradient.ObjectKeyframeFrame[index] == 4);
        var targetGradientStops = new[]
        {
            new GradientStop(0, Color.Blue),
            new GradientStop(0.4f, Color.Lime),
            new GradientStop(1, Color.White)
        };
        var targetGradientStart = new PointF(20, 40);
        var targetGradientEnd = new PointF(220, 160);
        solidToGradient.SetGradientPaint(
            solidGradientTarget,
            GradientKind.Linear,
            targetGradientStops,
            targetGradientStart,
            targetGradientEnd);
        AssertTimeline(
            solidToGradient.TryCreateTimelineTween(
                0,
                0,
                4,
                TimelineTweenKind.Shape,
                out var solidGradientError),
            $"Solid-to-gradient shape tween creation failed: {solidGradientError}");
        var solidGradientMiddle = Enumerable.Range(0, solidToGradient.ObjectCount)
            .Single(index => solidToGradient.ObjectKeyframeFrame[index] == 2);
        var solidGradientMiddleStops = solidToGradient.GetGradientStops(solidGradientMiddle);
        AssertTimeline(
            solidToGradient.GetGradientKind(solidGradientMiddle) == GradientKind.Linear
            && solidGradientMiddleStops.Select(stop => stop.Position).SequenceEqual(new[] { 0f, 0.4f, 1f })
            && GradientPaintUtilities.SampleColor(solidGradientMiddleStops, 0.4f).ToArgb()
                == TimelineTweenArgb(Color.Red.ToArgb(), Color.Lime.ToArgb(), 0.5f)
            && solidToGradient.GetGradientStart(solidGradientMiddle) == targetGradientStart
            && solidToGradient.GetGradientEnd(solidGradientMiddle) == targetGradientEnd,
            "Shape tween did not expand a solid fill into the target gradient stop layout and colors.");

        solidToGradient.SetGradientStops(
            solidGradientMiddle,
            [new GradientStop(0, Color.Black), new GradientStop(1, Color.Black)]);
        var restoredSolidGradient = new VectorScene();
        restoredSolidGradient.RestoreSnapshot(solidToGradient.CreateSnapshot());
        var restoredSolidGradientMiddle = Enumerable.Range(0, restoredSolidGradient.ObjectCount)
            .Single(index => restoredSolidGradient.ObjectKeyframeFrame[index] == 2);
        AssertTimeline(
            GradientPaintUtilities.SampleColor(
                restoredSolidGradient.GetGradientStops(restoredSolidGradientMiddle),
                0.4f).ToArgb()
                == TimelineTweenArgb(Color.Red.ToArgb(), Color.Lime.ToArgb(), 0.5f),
            "Restoring a tween snapshot did not regenerate its interpolated gradient colors.");

        var gradientToSolid = new VectorScene();
        gradientToSolid.CreateEmpty();
        var radialSource = gradientToSolid.AddObject(
            0,
            new PointF(80, 80),
            new SizeF(120, 120),
            0,
            0,
            Color.Blue,
            Color.Transparent,
            8,
            ShapeKind.Rectangle);
        var radialStops = new[]
        {
            new GradientStop(0, Color.Blue),
            new GradientStop(1, Color.Red)
        };
        gradientToSolid.SetGradientPaint(radialSource, GradientKind.Radial, radialStops);
        AssertTimeline(
            gradientToSolid.InsertTimelineKeyframe(0, 4),
            "Gradient-to-solid tween setup could not create its end keyframe.");
        var radialTarget = Enumerable.Range(0, gradientToSolid.ObjectCount)
            .Single(index => gradientToSolid.ObjectKeyframeFrame[index] == 4);
        gradientToSolid.DisableLinearGradient(radialTarget);
        gradientToSolid.Argb[radialTarget] = Color.Yellow.ToArgb();
        AssertTimeline(
            gradientToSolid.TryCreateTimelineTween(
                0,
                0,
                4,
                TimelineTweenKind.Shape,
                out var gradientSolidError),
            $"Gradient-to-solid shape tween creation failed: {gradientSolidError}");
        var gradientSolidMiddle = Enumerable.Range(0, gradientToSolid.ObjectCount)
            .Single(index => gradientToSolid.ObjectKeyframeFrame[index] == 2);
        var sourceMiddleColor = GradientPaintUtilities.SampleColor(radialStops, 0.5f);
        AssertTimeline(
            gradientToSolid.GetGradientKind(gradientSolidMiddle) == GradientKind.Radial
            && GradientPaintUtilities.SampleColor(
                gradientToSolid.GetGradientStops(gradientSolidMiddle),
                0.5f).ToArgb()
                == TimelineTweenArgb(sourceMiddleColor.ToArgb(), Color.Yellow.ToArgb(), 0.5f),
            "Shape tween did not interpolate a radial gradient into a solid fill.");

        var crossGradient = new VectorScene();
        crossGradient.CreateEmpty();
        var linearSource = crossGradient.AddObject(
            0,
            new PointF(100, 100),
            new SizeF(160, 100),
            0,
            0,
            Color.Red,
            Color.Transparent,
            8,
            ShapeKind.Rectangle);
        var linearStops = new[]
        {
            new GradientStop(0, Color.Red),
            new GradientStop(1, Color.Blue)
        };
        crossGradient.SetGradientPaint(linearSource, GradientKind.Linear, linearStops);
        AssertTimeline(
            crossGradient.InsertTimelineKeyframe(0, 4),
            "Cross-gradient tween setup could not create its end keyframe.");
        var radialGradientTarget = Enumerable.Range(0, crossGradient.ObjectCount)
            .Single(index => crossGradient.ObjectKeyframeFrame[index] == 4);
        var targetRadialStops = new[]
        {
            new GradientStop(0, Color.White),
            new GradientStop(0.5f, Color.Lime),
            new GradientStop(1, Color.Black)
        };
        crossGradient.SetGradientPaint(radialGradientTarget, GradientKind.Radial, targetRadialStops);
        AssertTimeline(
            crossGradient.TryCreateTimelineTween(
                0,
                0,
                4,
                TimelineTweenKind.Shape,
                out var crossGradientError),
            $"Cross-gradient shape tween creation failed: {crossGradientError}");
        var crossGradientFirst = Enumerable.Range(0, crossGradient.ObjectCount)
            .Single(index => crossGradient.ObjectKeyframeFrame[index] == 1);
        var crossGradientMiddle = Enumerable.Range(0, crossGradient.ObjectCount)
            .Single(index => crossGradient.ObjectKeyframeFrame[index] == 2);
        var crossGradientMiddleStops = crossGradient.GetGradientStops(crossGradientMiddle);
        var linearMiddleColor = GradientPaintUtilities.SampleColor(linearStops, 0.5f);
        AssertTimeline(
            crossGradient.GetGradientKind(crossGradientFirst) == GradientKind.Linear
            && crossGradient.GetGradientKind(crossGradientMiddle) == GradientKind.Radial
            && crossGradientMiddleStops.Select(stop => stop.Position).SequenceEqual(new[] { 0f, 0.5f, 1f })
            && GradientPaintUtilities.SampleColor(crossGradientMiddleStops, 0.5f).ToArgb()
                == TimelineTweenArgb(linearMiddleColor.ToArgb(), Color.Lime.ToArgb(), 0.5f),
            "Shape tween did not reconcile unequal gradient stops or switch gradient type deterministically.");
    }

    private static void RunTimelineTweenComplexTopologyRegression()
    {
        var holeScene = new VectorScene();
        holeScene.CreateEmpty();
        holeScene.AddPathObjectContours(
            0,
            [
                TweenRectangle(0, 0, 400, 400),
                TweenRectangle(80, 80, 160, 160)
            ],
            0,
            Color.Coral,
            Color.Transparent,
            32);
        AssertTimeline(
            holeScene.InsertTimelineKeyframe(0, 4),
            "Complex topology setup could not create its end keyframe.");
        ReplaceTweenTargetPath(
            holeScene,
            4,
            [
                TweenRectangle(0, 0, 400, 400).Reverse().ToArray(),
                TweenRectangle(340, 340, 260, 260),
                TweenRectangle(160, 160, 80, 80)
            ],
            Color.Coral);
        AssertTimeline(
            holeScene.TryCreateTimelineTween(
                0,
                0,
                4,
                TimelineTweenKind.Shape,
                out var holeTweenError),
            $"Shape tween rejected a compound path with an appearing hole: {holeTweenError}");
        var holeMiddle = Enumerable.Range(0, holeScene.ObjectCount)
            .Single(index => holeScene.ObjectKeyframeFrame[index] == 2);
        AssertTimeline(
            holeScene.TryGetPathWorldContours(holeMiddle, out var holeMiddleContours)
            && holeMiddleContours.Length == 3
            && holeScene.FillContainsPoint(holeMiddle, new PointF(40, 40))
            && !holeScene.FillContainsPoint(holeMiddle, new PointF(120, 120))
            && !holeScene.FillContainsPoint(holeMiddle, new PointF(300, 300))
            && holeMiddleContours.All(contour => contour.Length >= 3),
            "Complex shape tween did not preserve its outer contour and interpolate existing/new holes.");

        var expectedMiddleX = holeScene.X[holeMiddle];
        holeScene.X[holeMiddle] = 9999;
        var restoredHoleScene = new VectorScene();
        restoredHoleScene.RestoreSnapshot(holeScene.CreateSnapshot());
        var restoredHoleMiddle = Enumerable.Range(0, restoredHoleScene.ObjectCount)
            .Single(index => restoredHoleScene.ObjectKeyframeFrame[index] == 2);
        AssertTimeline(
            Math.Abs(restoredHoleScene.X[restoredHoleMiddle] - expectedMiddleX) <= 0.01f
            && !restoredHoleScene.FillContainsPoint(restoredHoleMiddle, new PointF(120, 120))
            && !restoredHoleScene.FillContainsPoint(restoredHoleMiddle, new PointF(300, 300)),
            "Snapshot restore did not deterministically rebuild a complex shape-tween contour plan.");

        var islandScene = new VectorScene();
        islandScene.CreateEmpty();
        islandScene.AddPathObjectContours(
            0,
            [
                TweenRectangle(0, 0, 100, 100),
                TweenRectangle(200, 0, 300, 100)
            ],
            0,
            Color.MediumSeaGreen,
            Color.Transparent,
            24);
        AssertTimeline(
            islandScene.InsertTimelineKeyframe(0, 4),
            "Island merge topology setup could not create its end keyframe.");
        ReplaceTweenTargetPath(
            islandScene,
            4,
            [TweenRectangle(0, 0, 300, 100)],
            Color.MediumSeaGreen);
        AssertTimeline(
            islandScene.TryCreateTimelineTween(
                0,
                0,
                4,
                TimelineTweenKind.Shape,
                out var islandTweenError),
            $"Shape tween rejected an island merge: {islandTweenError}");
        var islandLateFrame = Enumerable.Range(0, islandScene.ObjectCount)
            .Single(index => islandScene.ObjectKeyframeFrame[index] == 3);
        AssertTimeline(
            islandScene.FillContainsPoint(islandLateFrame, new PointF(245, 50))
            && islandScene.TryGetPathWorldContours(islandLateFrame, out var islandContours)
            && islandContours.Length == 1,
            "Overlapping shape-tween islands produced an even-odd hole instead of a merged fill.");
    }

    private static void ReplaceTweenTargetPath(
        VectorScene scene,
        int frame,
        PointF[][] contours,
        Color fill)
    {
        var target = Enumerable.Range(0, scene.ObjectCount)
            .Single(index => scene.ObjectKeyframeFrame[index] == frame);
        AssertTimeline(scene.RemoveObjectAt(target), "Complex topology setup could not replace its end shape.");
        var previousEditFrame = scene.EditFrame;
        scene.EditFrame = frame;
        var replacement = scene.AddPathObjectContours(
            0,
            contours,
            0,
            fill,
            Color.Transparent,
            32);
        scene.EditFrame = previousEditFrame;
        AssertTimeline(
            replacement >= 0 && scene.ObjectKeyframeFrame[replacement] == frame,
            "Complex topology replacement was not assigned to the destination keyframe.");
    }

    private static PointF[] TweenRectangle(float left, float top, float right, float bottom) =>
    [
        new PointF(left, top),
        new PointF(right, top),
        new PointF(right, bottom),
        new PointF(left, bottom)
    ];

    private static PointF[] TweenCircle(PointF center, float radius, int sampleCount = 32) =>
        Enumerable.Range(0, sampleCount)
            .Select(index =>
            {
                var angle = MathF.Tau * index / sampleCount;
                return new PointF(
                    center.X + MathF.Cos(angle) * radius,
                    center.Y + MathF.Sin(angle) * radius);
            })
            .ToArray();

    private static void AddTweenCircleOutline(
        VectorScene scene,
        int layer,
        PointF center,
        float radius,
        float stroke,
        Color color)
    {
        const float kappa = 0.55228475f;
        var k = radius * kappa;
        var right = new PointF(center.X + radius, center.Y);
        var bottom = new PointF(center.X, center.Y + radius);
        var left = new PointF(center.X - radius, center.Y);
        var top = new PointF(center.X, center.Y - radius);
        scene.AddCubicCurveSegment(
            layer, right,
            new PointF(right.X, right.Y + k),
            new PointF(bottom.X + k, bottom.Y),
            bottom,
            stroke, Color.Transparent, color, 12);
        scene.AddCubicCurveSegment(
            layer, bottom,
            new PointF(bottom.X - k, bottom.Y),
            new PointF(left.X, left.Y + k),
            left,
            stroke, Color.Transparent, color, 12);
        scene.AddCubicCurveSegment(
            layer, left,
            new PointF(left.X, left.Y - k),
            new PointF(top.X - k, top.Y),
            top,
            stroke, Color.Transparent, color, 12);
        scene.AddCubicCurveSegment(
            layer, top,
            new PointF(top.X + k, top.Y),
            new PointF(right.X, right.Y - k),
            right,
            stroke, Color.Transparent, color, 12);
    }

    private static bool TweenContourHasAllBoundsCorners(IReadOnlyList<PointF> contour)
    {
        if (contour.Count < 4) return false;
        var left = contour.Min(point => point.X);
        var right = contour.Max(point => point.X);
        var top = contour.Min(point => point.Y);
        var bottom = contour.Max(point => point.Y);
        const float tolerance = 0.01f;
        return HasCorner(left, top)
            && HasCorner(right, top)
            && HasCorner(right, bottom)
            && HasCorner(left, bottom);

        bool HasCorner(float x, float y) => contour.Any(point =>
            Math.Abs(point.X - x) <= tolerance
            && Math.Abs(point.Y - y) <= tolerance);
    }

    private static int TimelineTweenArgb(int sourceArgb, int targetArgb, float progress)
    {
        var source = Color.FromArgb(sourceArgb);
        var target = Color.FromArgb(targetArgb);
        return Color.FromArgb(
            (int)MathF.Round(source.A + (target.A - source.A) * progress),
            (int)MathF.Round(source.R + (target.R - source.R) * progress),
            (int)MathF.Round(source.G + (target.G - source.G) * progress),
            (int)MathF.Round(source.B + (target.B - source.B) * progress)).ToArgb();
    }
}
