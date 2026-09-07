using System.Diagnostics;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunMarqueeUndoTopologyRegression()
    {
        foreach (var line in new[] { false, true })
        {
            using var form = new MainForm();
            var scene = (VectorScene)RequireField(typeof(MainForm), "_scene").GetValue(form)!;
            var stage = (StageControl)RequireField(typeof(MainForm), "_stage").GetValue(form)!;
            scene.CreateEmpty(2);
            stage.ClientSize = new Size(640, 420);
            stage.RestoreViewState(stage.CaptureViewState() with { Zoom = 1f });
            Call("ResetCanvasUndoHistory");
            var source = line
                ? scene.AddLineSegment(0, new PointF(-3_000, 0), new PointF(3_000, 0),
                    25, Color.Transparent, Color.Teal, 8)
                : scene.AddObject(0, PointF.Empty, new SizeF(6_000, 4_000),
                    0, 0, Color.Teal, Color.Transparent, 8, ShapeKind.Rectangle);
            if (!line)
                scene.SetLinearGradient(source, Color.Teal, Color.Gold,
                    new PointF(-3_000, 0), new PointF(3_000, 0));
            scene.AddObject(0, new PointF(10_000, 0), new SizeF(1_000, 1_000),
                0, 0, Color.Teal, Color.Transparent, 8, ShapeKind.Rectangle);
            scene.AddObject(0, new PointF(11_000, 0), new SizeF(1_000, 1_000),
                0, 0, Color.Teal, Color.Transparent, 8, ShapeKind.Rectangle);
            scene.InsertTimelineBlankKeyframe(1, 10);
            scene.EditFrame = 10;
            scene.AddObject(1, PointF.Empty, new SizeF(6_000, 4_000),
                0, 0, Color.Teal, Color.Transparent, 8, ShapeKind.Rectangle);
            scene.EditFrame = 0;
            var original = scene.CreateSnapshot();

            SelectPart();
            AssertTimeline(Call("UndoLastEdit") is true, "Marquee selection was not undoable.");
            AssertRestored(original, "selection undo");

            SelectPart();
            MovePart(7_000);
            var firstMove = scene.CreateSnapshot();
            MovePart(1_000);
            AssertTimeline(Call("UndoLastEdit") is true, "Second partial move was not undoable.");
            AssertRestored(firstMove, "second move undo must retain the first separation");
            AssertTimeline(Call("UndoLastEdit") is true, "First partial move was not undoable.");
            AssertRestored(original, "first move undo must restore unsplit topology");
            AssertTimeline(Call("UndoLastEdit") is false, "Temporary selection left an extra undo step.");

            SelectPart();
            CancelMaterialChange();
            Call("CancelStageSelection");
            AssertRestored(original, "cancel after material rollback");

            SelectPart();
            CancelMaterialChange();
            MovePart(7_000);
            AssertTimeline(Call("UndoLastEdit") is true, "Move after material cancellation was not undoable.");
            AssertRestored(original, "move undo after material rollback");

            SelectPart();
            SelectPart(additive: true);
            MovePart(7_000);
            AssertTimeline(Call("UndoLastEdit") is true, "Additive marquee move was not undoable.");
            AssertRestored(original, "additive selection undo");
            AssertTimeline(Call("UndoLastEdit") is false, "Additive selection left temporary undo steps.");

            object? Call(string name, params object?[] args) =>
                RequireMethod(typeof(MainForm), name).Invoke(form, args);

            int Selected() => ((IEnumerable<int>)RequireField(typeof(MainForm), "_selectedObjects")
                .GetValue(form)!).First(index => scene.ShapeKind[index] == (line ? ShapeKind.Line : ShapeKind.Path));

            void SelectPart(bool additive = false)
            {
                var selected = ((IEnumerable<int>)RequireField(typeof(MainForm), "_selectedObjects")
                    .GetValue(form)!).ToArray();
                RequireField(typeof(MainForm), "_additiveSelection").SetValue(form, additive);
                RequireField(typeof(MainForm), "_marqueeSelectionBase").SetValue(form, additive ? selected : Array.Empty<int>());
                RequireField(typeof(MainForm), "_marqueeStart").SetValue(form,
                    Point.Round(stage.WorldToScreen(additive ? 0 : -1_000, -3_000)));
                Call("CompleteMarqueeSelection", Point.Round(stage.WorldToScreen(additive ? 2_000 : 1_000, 3_000)));
                AssertTimeline(scene.ObjectCount > original.ObjectCount, "Marquee fixture did not split its source.");
                _ = Selected();
            }

            void MovePart(float dx)
            {
                var selected = Selected();
                Call("CaptureUndoSnapshot", new[] { selected }, null);
                scene.X[selected] += dx;
            }

            void CancelMaterialChange()
            {
                var selected = Selected();
                Call("BeginMaterialContinuousEdit");
                Call("PushMaterialUndoSnapshot", scene.CreateSnapshot());
                if (line) scene.StrokeArgb[selected] = Color.Coral.ToArgb();
                else scene.Argb[selected] = Color.Coral.ToArgb();
                Call("CancelMaterialContinuousEdit");
            }

            void AssertRestored(VectorSceneSnapshot expected, string label)
            {
                var actual = scene.CreateSnapshot();
                AssertTimeline(actual.ObjectCount == expected.ObjectCount
                    && actual.ShapeKind.SequenceEqual(expected.ShapeKind)
                    && actual.X.SequenceEqual(expected.X) && actual.Y.SequenceEqual(expected.Y)
                    && actual.Width.SequenceEqual(expected.Width) && actual.Height.SequenceEqual(expected.Height)
                    && actual.Argb.SequenceEqual(expected.Argb) && actual.StrokeArgb.SequenceEqual(expected.StrokeArgb)
                    && actual.Stroke.SequenceEqual(expected.Stroke)
                    && actual.ObjectLayer.SequenceEqual(expected.ObjectLayer)
                    && actual.ObjectKeyframeFrame.SequenceEqual(expected.ObjectKeyframeFrame)
                    && actual.ObjectOrder.SequenceEqual(expected.ObjectOrder)
                    && actual.ObjectSubOrder.SequenceEqual(expected.ObjectSubOrder)
                    && actual.LinearGradientEnabled.SequenceEqual(expected.LinearGradientEnabled)
                    && actual.GradientStartArgb.SequenceEqual(expected.GradientStartArgb)
                    && actual.GradientEndArgb.SequenceEqual(expected.GradientEndArgb)
                    && actual.GradientStartX.SequenceEqual(expected.GradientStartX)
                    && actual.GradientEndX.SequenceEqual(expected.GradientEndX)
                    && actual.LineEndpointStyles.SequenceEqual(expected.LineEndpointStyles)
                    && actual.LineEndEndpointStyles.SequenceEqual(expected.LineEndEndpointStyles),
                    $"Marquee {(line ? "line" : "fill")} {label} changed geometry, material, layer, cel, or stack order.");
            }
        }
        Console.WriteLine("marquee_undo_topology=ok");
    }

    private static void RunLineToFillConversionRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty(2);
        var preservedLine = scene.AddLineSegment(
            1,
            new PointF(-200, 0),
            new PointF(-100, 0),
            VectorUnits.StrokePointsToUnits(2),
            Color.Transparent,
            Color.Teal,
            6);
        if (!scene.InsertTimelineBlankKeyframe(1, 10))
        {
            throw new InvalidOperationException("Line-to-fill conversion regression could not create an independent cel.");
        }

        scene.EditFrame = 10;
        var source = scene.AddCurveSegment(
            1,
            new PointF(0, 0),
            new PointF(50, 60),
            new PointF(100, 0),
            VectorUnits.StrokePointsToUnits(4),
            Color.Transparent,
            Color.Coral,
            17);
        scene.ObjectSubOrder[source] = 0.375;
        var secondSource = scene.AddLineSegment(
            1,
            new PointF(0, 100),
            new PointF(100, 100),
            VectorUnits.StrokePointsToUnits(3),
            Color.Transparent,
            Color.Gold,
            13);
        scene.ObjectSubOrder[secondSource] = 0.625;
        var unrelated = scene.AddObject(0, new PointF(180, 0), new SizeF(40, 40), 0, 0, Color.Aqua, Color.Transparent, 6, ShapeKind.Rectangle);
        var sourceLayer = scene.ObjectLayer[source];
        var sourceKeyframe = scene.ObjectKeyframeFrame[source];
        var sourceOrder = scene.ObjectOrder[source];
        var sourceSubOrder = scene.ObjectSubOrder[source];
        var sourceAtoms = scene.AtomCount[source];
        var sourceStrokeColor = scene.StrokeArgb[source];
        var snapshot = scene.CreateSnapshot();

        if (!scene.CanConvertLineToFill(source, 10)
            || scene.CanConvertLineToFill(source, 0)
            || scene.CanConvertLineToFill(unrelated, 10)
            || !scene.TryConvertLineToFill(source, 10, out var fill))
        {
            throw new InvalidOperationException("A visible line could not be converted to a fill.");
        }

        if (scene.ObjectCount != 4
            || scene.ShapeKind[fill] != ShapeKind.Path
            || scene.ObjectLayer[fill] != sourceLayer
            || scene.ObjectKeyframeFrame[fill] != sourceKeyframe
            || scene.ObjectOrder[fill] != sourceOrder
            || scene.ObjectSubOrder[fill] != sourceSubOrder
            || scene.AtomCount[fill] != sourceAtoms
            || scene.Argb[fill] != sourceStrokeColor
            || scene.Stroke[fill] != 0
            || scene.StrokeArgb[fill] != Color.Transparent.ToArgb()
            || !scene.TryGetPathWorldContours(fill, out var contours)
            || contours.Length == 0
            || !scene.FillContainsPoint(fill, new PointF(50, 30))
            || Enumerable.Range(0, scene.ObjectCount).Count(index => scene.ShapeKind[index] == ShapeKind.Line) != 2
            || Enumerable.Range(0, scene.ObjectCount).Count(index => scene.ShapeKind[index] == ShapeKind.Rectangle) != 1)
        {
            throw new InvalidOperationException("Line-to-fill conversion did not preserve the source cel, style, order, or outline geometry.");
        }

        scene.RestoreSnapshot(snapshot);
        if (scene.ObjectCount != 4
            || scene.ShapeKind[source] != ShapeKind.Line
            || scene.ShapeKind[secondSource] != ShapeKind.Line
            || !scene.IsObjectActive(source, 10)
            || scene.IsObjectActive(source, 0)
            || !scene.CanConvertLineToFill(source, 10))
        {
            throw new InvalidOperationException("Line-to-fill conversion snapshot restoration did not recover the source line.");
        }

        var batchSources = new[] { source, secondSource };
        var batchSourceProperties = batchSources
            .Select(index => (
                Layer: scene.ObjectLayer[index],
                KeyframeFrame: scene.ObjectKeyframeFrame[index],
                Order: scene.ObjectOrder[index],
                SubOrder: scene.ObjectSubOrder[index],
                Atoms: scene.AtomCount[index],
                StrokeColor: scene.StrokeArgb[index]))
            .ToArray();
        var batchFills = new List<int>(batchSources.Length);
        foreach (var batchSource in batchSources)
        {
            if (scene.TryConvertLineToFill(batchSource, 10, out var batchFill))
            {
                batchFills.Add(batchFill);
                continue;
            }

            scene.RestoreSnapshot(snapshot);
            throw new InvalidOperationException("Multiple selected lines could not be converted to fills as one operation.");
        }

        if (batchFills.Count != batchSources.Length
            || scene.ObjectCount != 4
            || Enumerable.Range(0, scene.ObjectCount).Count(index => scene.ShapeKind[index] == ShapeKind.Path) != 2
            || Enumerable.Range(0, scene.ObjectCount).Any(index => scene.ShapeKind[index] == ShapeKind.Line && scene.IsObjectActive(index, 10)))
        {
            throw new InvalidOperationException("Multiple selected lines could not be converted to fills as one operation.");
        }

        for (var index = 0; index < batchFills.Count; index++)
        {
            var fillIndex = batchFills[index];
            var sourceProperties = batchSourceProperties[index];
            if ((uint)fillIndex >= scene.ObjectCount
                || scene.ShapeKind[fillIndex] != ShapeKind.Path
                || scene.ObjectLayer[fillIndex] != sourceProperties.Layer
                || scene.ObjectKeyframeFrame[fillIndex] != sourceProperties.KeyframeFrame
                || scene.ObjectOrder[fillIndex] != sourceProperties.Order
                || scene.ObjectSubOrder[fillIndex] != sourceProperties.SubOrder
                || scene.AtomCount[fillIndex] != sourceProperties.Atoms
                || scene.Argb[fillIndex] != sourceProperties.StrokeColor
                || scene.Stroke[fillIndex] != 0
                || scene.StrokeArgb[fillIndex] != Color.Transparent.ToArgb())
            {
                throw new InvalidOperationException("Multiple line-to-fill conversion did not preserve source metadata.");
            }
        }

        if (!scene.FillContainsPoint(batchFills[0], new PointF(50, 30))
            || !scene.FillContainsPoint(batchFills[1], new PointF(50, 100)))
        {
            throw new InvalidOperationException("Multiple line-to-fill conversion did not retain each stroke outline.");
        }

        Console.WriteLine("line_to_fill_conversion_regression=ok");
    }

    private static void RunConnectedLineBranchRegression()
    {
        var stroke = VectorUnits.StrokePointsToUnits(2.5f);
        var endpointScene = new VectorScene();
        endpointScene.CreateEmpty(layers: 2);
        var source = endpointScene.AddLineSegment(
            1,
            PointF.Empty,
            new PointF(200, 0),
            stroke,
            Color.FromArgb(96, 20, 40, 60),
            Color.FromArgb(180, 220, 80, 40),
            6,
            LineEndpointStyle.Sharp,
            LineEndpointStyle.Round);
        var gradientStops = new[]
        {
            new GradientStop(0, Color.FromArgb(180, 240, 20, 10)),
            new GradientStop(0.45f, Color.FromArgb(150, 30, 220, 80)),
            new GradientStop(1, Color.FromArgb(120, 20, 60, 240))
        };
        var gradientStart = new PointF(-20, -10);
        var gradientEnd = new PointF(220, 10);
        endpointScene.SetGradientPaint(source, GradientKind.Linear, gradientStops, gradientStart, gradientEnd);
        var sourceLayer = endpointScene.ObjectLayer[source];
        var sourceKeyframe = endpointScene.ObjectKeyframeFrame[source];
        var sourceOrder = endpointScene.ObjectOrder[source];
        var sourceFillArgb = endpointScene.Argb[source];
        var sourceStrokeArgb = endpointScene.StrokeArgb[source];

        if (!endpointScene.AddConnectedLineBranch(
                source,
                0,
                new PointF(-80, 80),
                out var endpointBranch)
            || endpointScene.ObjectCount != 2
            || !endpointBranch.SourceObjectIndices.SequenceEqual([source])
            || !PointsNear(endpointBranch.Anchor, PointF.Empty)
            || !endpointScene.TryGetLineEndpoint(endpointBranch.BranchObjectIndex, true, out var endpointBranchStart)
            || !endpointScene.TryGetLineEndpoint(endpointBranch.BranchObjectIndex, false, out var endpointBranchEnd)
            || !PointsNear(endpointBranchStart, PointF.Empty)
            || !PointsNear(endpointBranchEnd, new PointF(-80, 80))
            || endpointScene.ObjectLayer[endpointBranch.BranchObjectIndex] != sourceLayer
            || endpointScene.ObjectKeyframeFrame[endpointBranch.BranchObjectIndex] != sourceKeyframe
            || endpointScene.ObjectOrder[endpointBranch.BranchObjectIndex] != sourceOrder
            || endpointScene.Stroke[endpointBranch.BranchObjectIndex] != stroke
            || endpointScene.Argb[endpointBranch.BranchObjectIndex] != sourceFillArgb
            || endpointScene.StrokeArgb[endpointBranch.BranchObjectIndex] != sourceStrokeArgb
            || endpointScene.GetLineEndpointStyle(endpointBranch.BranchObjectIndex, true) != LineEndpointStyle.Round
            || endpointScene.GetLineEndpointStyle(endpointBranch.BranchObjectIndex, false) != LineEndpointStyle.Sharp
            || endpointScene.GetGradientKind(endpointBranch.BranchObjectIndex) != GradientKind.Linear
            || !endpointScene.GetGradientStops(endpointBranch.BranchObjectIndex).SequenceEqual(gradientStops)
            || !PointsNear(endpointScene.GetGradientStart(endpointBranch.BranchObjectIndex), gradientStart)
            || !PointsNear(endpointScene.GetGradientEnd(endpointBranch.BranchObjectIndex), gradientEnd))
        {
            throw new InvalidOperationException("An endpoint line branch did not preserve its connection, material, or timeline ownership.");
        }

        var curvedScene = new VectorScene();
        curvedScene.CreateEmpty();
        var curve = curvedScene.AddCubicCurveSegment(
            0,
            new PointF(0, 0),
            new PointF(60, 120),
            new PointF(140, 120),
            new PointF(200, 0),
            stroke,
            Color.Transparent,
            Color.Coral,
            12,
            LineEndpointStyle.Sharp,
            LineEndpointStyle.Sharp);
        var expectedAnchor = new PointF(100, 90);
        if (!curvedScene.AddConnectedLineBranch(
                curve,
                0.5f,
                new PointF(100, 190),
                out var interiorBranch)
            || curvedScene.ObjectCount != 3
            || interiorBranch.SourceObjectIndices.Length != 2
            || !PointsNear(interiorBranch.Anchor, expectedAnchor)
            || !curvedScene.TryGetLineCubic(
                interiorBranch.SourceObjectIndices[0],
                out var firstStart,
                out var firstControl1,
                out var firstControl2,
                out var firstEnd)
            || !PointsWithin(firstStart, new PointF(0, 0), 0.75f)
            || !PointsNear(firstControl1, new PointF(30, 60))
            || !PointsNear(firstControl2, new PointF(65, 90))
            || !PointsWithin(firstEnd, expectedAnchor, 0.75f)
            || !curvedScene.TryGetLineCubic(
                interiorBranch.SourceObjectIndices[1],
                out var secondStart,
                out var secondControl1,
                out var secondControl2,
                out var secondEnd)
            || !PointsWithin(secondStart, expectedAnchor, 0.75f)
            || !PointsNear(secondControl1, new PointF(135, 90))
            || !PointsNear(secondControl2, new PointF(170, 60))
            || !PointsWithin(secondEnd, new PointF(200, 0), 0.75f)
            || !curvedScene.TryGetLineEndpoint(interiorBranch.BranchObjectIndex, true, out var interiorBranchStart)
            || !curvedScene.TryGetLineEndpoint(interiorBranch.BranchObjectIndex, false, out var interiorBranchEnd)
            || !PointsNear(interiorBranchStart, expectedAnchor)
            || !PointsNear(interiorBranchEnd, new PointF(100, 190)))
        {
            throw new InvalidOperationException("An interior line branch did not preserve the source cubic through an exact split.");
        }

        var branchHit = new DrawingElementHit(
            new DrawingElementKey(interiorBranch.BranchObjectIndex, DrawingElementKind.Stroke, 0),
            0,
            0,
            1);
        var connectedObjects = curvedScene.GetConnectedStrokeElements(branchHit, 0)
            .Select(connected => connected.Key.ObjectIndex)
            .Distinct()
            .ToHashSet();
        if (!interiorBranch.SourceObjectIndices.All(connectedObjects.Contains)
            || !connectedObjects.Contains(interiorBranch.BranchObjectIndex))
        {
            throw new InvalidOperationException("An interior line branch was not connected to both split source segments.");
        }

        if (!MainForm.IsLineEndpointBranchGesture(MouseButtons.Left, true, 4, 5)
            || !MainForm.IsLineEndpointBranchGesture(MouseButtons.Left, true, 0, 5)
            || MainForm.IsLineEndpointBranchGesture(MouseButtons.Left, false, 0, 5)
            || MainForm.IsLineEndpointBranchGesture(MouseButtons.Right, true, 0, 5)
            || MainForm.IsLineEndpointBranchGesture(MouseButtons.Left, true, 6, 5)
            || MainForm.IsLineEndpointBranchGesture(MouseButtons.Left, true, float.PositiveInfinity, 5)
            || !MainForm.IsLineInteriorBranchGesture(MouseButtons.Left, true, true, 0.5f)
            || MainForm.IsLineInteriorBranchGesture(MouseButtons.Left, true, false, 0.5f)
            || MainForm.IsLineInteriorBranchGesture(MouseButtons.Left, false, true, 0.5f)
            || MainForm.IsLineInteriorBranchGesture(MouseButtons.Left, true, true, 0.02f)
            || MainForm.IsLineInteriorBranchGesture(MouseButtons.Left, true, true, 0.98f)
            || MainForm.ShouldCommitLineBranchDrag(MouseButtons.Left, false, 100)
            || MainForm.ShouldCommitLineBranchDrag(MouseButtons.Left, true, 0.5f)
            || !MainForm.ShouldCommitLineBranchDrag(MouseButtons.Left, true, 100))
        {
            throw new InvalidOperationException("Line branch gesture modifiers or drag commitment thresholds regressed.");
        }

        Console.WriteLine("connected_line_branch_regression=ok");
    }

    private static void RunLineSegmentMergeRegression()
    {
        var stroke = VectorUnits.StrokePointsToUnits(2);
        var endpointStyleScene = new VectorScene();
        endpointStyleScene.CreateEmpty();
        endpointStyleScene.AddLineSegment(
            0,
            new PointF(0, 0),
            new PointF(100, 0),
            stroke,
            Color.Transparent,
            Color.Coral,
            6,
            LineEndpointStyle.Sharp);
        endpointStyleScene.AddLineSegment(
            0,
            new PointF(104, 0),
            new PointF(204, 0),
            stroke,
            Color.Transparent,
            Color.Coral,
            6,
            LineEndpointStyle.Sharp);
        if (!endpointStyleScene.MergeCompatibleLineSegments(0).Changed
            || endpointStyleScene.ObjectCount != 1
            || endpointStyleScene.GetLineEndpointStyle(0) != LineEndpointStyle.Sharp)
        {
            throw new InvalidOperationException("Merged sharp-end lines did not retain their endpoint style.");
        }

        var independentEndpointScene = new VectorScene();
        independentEndpointScene.CreateEmpty();
        independentEndpointScene.AddLineSegment(
            0,
            new PointF(0, 0),
            new PointF(100, 0),
            stroke,
            Color.Transparent,
            Color.Coral,
            6,
            LineEndpointStyle.Sharp,
            LineEndpointStyle.Round);
        var independentEndpointSnapshot = independentEndpointScene.CreateSnapshot();
        independentEndpointScene.RestoreSnapshot(independentEndpointSnapshot);
        if (independentEndpointScene.GetLineEndpointStyle(0, startEndpoint: true) != LineEndpointStyle.Sharp
            || independentEndpointScene.GetLineEndpointStyle(0, startEndpoint: false) != LineEndpointStyle.Round)
        {
            throw new InvalidOperationException("Independent start and end line styles were not preserved by snapshot restoration.");
        }

        var boundaryEndpointScene = new VectorScene();
        boundaryEndpointScene.CreateEmpty();
        var outlinedRectangle = boundaryEndpointScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(240, 160),
            0,
            stroke,
            Color.CornflowerBlue,
            Color.Coral,
            12,
            ShapeKind.Rectangle);
        var boundaryPart = boundaryEndpointScene.GetBoundaryParts(outlinedRectangle, 0)
            .First(part => part.Points.Length == 2);
        var boundaryKey = new DrawingElementKey(
            outlinedRectangle,
            DrawingElementKind.BoundaryStroke,
            boundaryPart.PartIndex);
        var boundarySnapshot = boundaryEndpointScene.CreateSnapshot();
        if (!boundaryEndpointScene.TryGetLinePartEndpointStyles(boundaryKey, 0, out var boundaryStartStyle, out var boundaryEndStyle)
            || boundaryStartStyle != LineEndpointStyle.Round
            || boundaryEndStyle != LineEndpointStyle.Round)
        {
            throw new InvalidOperationException("A selected fill-boundary segment did not expose editable line endpoint styles.");
        }

        var materializedBoundary = boundaryEndpointScene.MaterializeSelectedParts([boundaryKey], 0);
        var materializedBoundaryLine = materializedBoundary.Parts.FirstOrDefault().Result.ObjectIndex;
        if (!materializedBoundary.Success
            || !materializedBoundary.Changed
            || materializedBoundary.Parts.Length != 1
            || materializedBoundaryLine < 0
            || boundaryEndpointScene.ShapeKind[materializedBoundaryLine] != ShapeKind.Line
            || !boundaryEndpointScene.SetLineEndpointStyle(materializedBoundaryLine, startEndpoint: true, LineEndpointStyle.Sharp)
            || boundaryEndpointScene.GetLineEndpointStyle(materializedBoundaryLine, startEndpoint: true) != LineEndpointStyle.Sharp
            || Enumerable.Range(0, boundaryEndpointScene.ObjectCount)
                .Where(index => index != materializedBoundaryLine && boundaryEndpointScene.ShapeKind[index] == ShapeKind.Line)
                .Any(index => boundaryEndpointScene.GetLineEndpointStyle(index, startEndpoint: true) != LineEndpointStyle.Round
                    || boundaryEndpointScene.GetLineEndpointStyle(index, startEndpoint: false) != LineEndpointStyle.Round))
        {
            throw new InvalidOperationException("Editing a fill-boundary endpoint style did not isolate the selected segment as a Line.");
        }

        boundaryEndpointScene.RestoreSnapshot(boundarySnapshot);
        if (boundaryEndpointScene.ObjectCount != 1
            || boundaryEndpointScene.ShapeKind[outlinedRectangle] != ShapeKind.Rectangle
            || boundaryEndpointScene.Stroke[outlinedRectangle] != stroke)
        {
            throw new InvalidOperationException("Restoring a fill-boundary endpoint edit did not recover the original outlined fill.");
        }

        var mixedEndpointStyleScene = new VectorScene();
        mixedEndpointStyleScene.CreateEmpty();
        mixedEndpointStyleScene.AddLineSegment(
            0,
            new PointF(0, 0),
            new PointF(100, 0),
            stroke,
            Color.Transparent,
            Color.Coral,
            6,
            LineEndpointStyle.Sharp);
        mixedEndpointStyleScene.AddLineSegment(
            0,
            new PointF(104, 0),
            new PointF(204, 0),
            stroke,
            Color.Transparent,
            Color.Coral,
            6,
            LineEndpointStyle.Round);
        var mixedStyleSnapshot = mixedEndpointStyleScene.CreateSnapshot();
        mixedEndpointStyleScene.RestoreSnapshot(mixedStyleSnapshot);
        if (mixedEndpointStyleScene.GetLineEndpointStyle(0) != LineEndpointStyle.Sharp
            || mixedEndpointStyleScene.GetLineEndpointStyle(1) != LineEndpointStyle.Round
            || mixedEndpointStyleScene.MergeCompatibleLineSegments(0).Changed)
        {
            throw new InvalidOperationException("Different line endpoint styles were not preserved or were merged together.");
        }

        var miterJoinScene = new VectorScene();
        miterJoinScene.CreateEmpty();
        var miterFirst = miterJoinScene.AddLineSegment(
            0,
            new PointF(-100, 0),
            PointF.Empty,
            stroke,
            Color.Transparent,
            Color.Coral,
            6,
            LineEndpointStyle.Sharp);
        var miterSecond = miterJoinScene.AddLineSegment(
            0,
            PointF.Empty,
            new PointF(0, 100),
            stroke,
            Color.Transparent,
            Color.Coral,
            6,
            LineEndpointStyle.Round);
        if (!miterJoinScene.TryGetLineJoinNeighbor(miterFirst, startEndpoint: false, 0, out var joinNeighbor, out var neighborStarts)
            || joinNeighbor != miterSecond
            || !neighborStarts
            || !LineJoinGeometry.TryCreateMiter(PointF.Empty, new PointF(-100, 0), new PointF(0, 100), 10, out var miter)
            || Math.Abs(miter.OuterMiter.X - 10) > 0.001f
            || Math.Abs(miter.OuterMiter.Y + 10) > 0.001f
            || Math.Abs(miter.InnerMiter.X + 10) > 0.001f
            || Math.Abs(miter.InnerMiter.Y - 10) > 0.001f)
        {
            throw new InvalidOperationException("Sharp line joins did not resolve the width-offset miter intersection.");
        }

        var junctionScene = new VectorScene();
        junctionScene.CreateEmpty();
        var junctionFirst = junctionScene.AddLineSegment(
            0,
            new PointF(-400, -100),
            PointF.Empty,
            stroke,
            Color.Transparent,
            Color.Coral,
            6,
            LineEndpointStyle.Sharp);
        var junctionSecond = junctionScene.AddLineSegment(
            0,
            PointF.Empty,
            new PointF(-400, 0),
            stroke,
            Color.Transparent,
            Color.Coral,
            6,
            LineEndpointStyle.Sharp);
        var junctionThird = junctionScene.AddLineSegment(
            0,
            PointF.Empty,
            new PointF(-400, 100),
            stroke,
            Color.Transparent,
            Color.Coral,
            6,
            LineEndpointStyle.Sharp);
        if (!junctionScene.TryGetLineEndpointJunction(junctionFirst, startEndpoint: false, 0, out var junction)
            || junction.NeighborCount != 2
            || junction.OwnerObjectIndex != Math.Min(junctionFirst, Math.Min(junctionSecond, junctionThird))
            || !junction.AllSharp
            || junctionScene.TryGetLineJoinNeighbor(junctionFirst, startEndpoint: false, 0, out _, out _))
        {
            throw new InvalidOperationException("Multi-line sharp junctions did not expose all connected endpoint directions.");
        }
        if (!junctionScene.TryGetLineEndpointJunctionForRender(junctionThird, startEndpoint: true, 0, out var renderJunction)
            || renderJunction.OwnerObjectIndex >= junctionThird
            || renderJunction.Connections.Length != 0)
        {
            throw new InvalidOperationException("Non-owner sharp junction rendering did not stop after resolving an earlier owner.");
        }

        var wholeLineHit = new DrawingElementHit(
            new DrawingElementKey(junctionFirst, DrawingElementKind.Stroke, 0),
            0,
            0,
            1);
        var splitLineHit = wholeLineHit with { StartT = 0.25f, EndT = 1 };
        if (!MainForm.IsWholeLineEndpointEditHit(junctionScene, wholeLineHit)
            || MainForm.IsWholeLineEndpointEditHit(junctionScene, splitLineHit))
        {
            throw new InvalidOperationException("Whole-line endpoint edits did not bypass only redundant topology materialization.");
        }

        var junctionMiters = LineJoinGeometry.CreateJunctionMiters(
            PointF.Empty,
            [new PointF(-400, -100), new PointF(-400, 0), new PointF(-400, 100)],
            stroke * 0.5f);
        var sharpExtension = junctionMiters
            .SelectMany(miter => new[] { miter.OuterMiter.X, miter.InnerMiter.X })
            .DefaultIfEmpty(0)
            .Max();
        if (junctionMiters.Length != 1 || sharpExtension <= stroke * 0.625f)
        {
            throw new InvalidOperationException("Multi-line sharp junctions did not extend a pointed outer miter beyond the endpoint.");
        }

        var reverseScene = new VectorScene();
        reverseScene.CreateEmpty();
        var first = reverseScene.AddLineSegment(0, new PointF(0, 0), new PointF(100, 0), stroke, Color.Transparent, Color.Coral, 6);
        var reversed = reverseScene.AddLineSegment(0, new PointF(204, 0), new PointF(104, 0), stroke, Color.Transparent, Color.Coral, 6);
        var reverseMerge = reverseScene.MergeCompatibleLineSegments(0);
        if (!reverseMerge.Changed
            || reverseMerge.MergeCount != 1
            || reverseScene.ObjectCount != 1
            || reverseMerge.OldToNewObjectIndex[first] != reverseMerge.OldToNewObjectIndex[reversed]
            || !LineMatches(reverseScene, 0, new PointF(0, 0), new PointF(204, 0)))
        {
            throw new InvalidOperationException("Reverse-oriented line endpoints under 5 vu did not merge into one straight segment.");
        }

        var quantizedStraightScene = new VectorScene();
        quantizedStraightScene.CreateEmpty();
        quantizedStraightScene.AddLineSegment(0, new PointF(0, 0), new PointF(101, 0), stroke, Color.Transparent, Color.Coral, 6);
        quantizedStraightScene.AddLineSegment(0, new PointF(105, 0), new PointF(205, 0), stroke, Color.Transparent, Color.Coral, 6);
        if (!quantizedStraightScene.MergeCompatibleLineSegments(0).Changed
            || quantizedStraightScene.ObjectCount != 1
            || !quantizedStraightScene.IsLineStraight(0)
            || !quantizedStraightScene.TryGetLineEndpoint(0, startEndpoint: true, out var quantizedStart)
            || !quantizedStraightScene.TryGetLineEndpoint(0, startEndpoint: false, out var quantizedEnd)
            || quantizedStart.X > 1
            || quantizedEnd.X < 204)
        {
            throw new InvalidOperationException("A quantized odd-length straight segment did not merge despite zero curvature and endpoints under 5 vu apart.");
        }

        var marqueeSplitScene = new VectorScene();
        marqueeSplitScene.CreateEmpty();
        marqueeSplitScene.AddLineSegment(0, new PointF(-200, 0), new PointF(200, 0), stroke, Color.Transparent, Color.Coral, 6);
        var marqueeMaterialization = marqueeSplitScene.MaterializeMarqueeLineParts(new RectangleF(-20, -40, 40, 80), 0);
        if (!marqueeMaterialization.Changed
            || marqueeSplitScene.ObjectCount < 2
            || !marqueeSplitScene.MergeCompatibleLineSegments(0).Changed
            || marqueeSplitScene.ObjectCount != 1
            || !LineMatches(marqueeSplitScene, 0, new PointF(-200, 0), new PointF(200, 0)))
        {
            throw new InvalidOperationException("A marquee-materialized straight line did not restore to one compatible segment.");
        }

        var scopedMarqueeScene = new VectorScene();
        scopedMarqueeScene.CreateEmpty();
        scopedMarqueeScene.AddLineSegment(0, new PointF(-200, 0), new PointF(200, 0), stroke, Color.Transparent, Color.Coral, 6);
        var scopedMarqueeMaterialization = scopedMarqueeScene.MaterializeMarqueeLineParts(new RectangleF(-20, -40, 40, 80), 0);
        var selectedMarqueeSegment = scopedMarqueeMaterialization.SelectedObjects.Single();
        var siblingScope = Enumerable.Range(0, scopedMarqueeScene.ObjectCount)
            .Where(index => scopedMarqueeScene.ShapeKind[index] == ShapeKind.Line
                && scopedMarqueeScene.ObjectOrder[index] == scopedMarqueeScene.ObjectOrder[selectedMarqueeSegment])
            .ToArray();
        if (!scopedMarqueeMaterialization.Changed
            || siblingScope.Length < 2
            || !scopedMarqueeScene.MergeCompatibleLineSegments(0, siblingScope).Changed
            || scopedMarqueeScene.ObjectCount != 1
            || !LineMatches(scopedMarqueeScene, 0, new PointF(-200, 0), new PointF(200, 0)))
        {
            throw new InvalidOperationException("A selected marquee segment did not simplify with all of its materialized line siblings.");
        }

        var scopedMergeScene = new VectorScene();
        scopedMergeScene.CreateEmpty();
        var scopedFirst = scopedMergeScene.AddLineSegment(0, new PointF(0, 0), new PointF(100, 0), stroke, Color.Transparent, Color.Coral, 6);
        var scopedSecond = scopedMergeScene.AddLineSegment(0, new PointF(104, 0), new PointF(204, 0), stroke, Color.Transparent, Color.Coral, 6);
        scopedMergeScene.AddLineSegment(0, new PointF(0, 100), new PointF(100, 100), stroke, Color.Transparent, Color.Coral, 6);
        scopedMergeScene.AddLineSegment(0, new PointF(104, 100), new PointF(204, 100), stroke, Color.Transparent, Color.Coral, 6);
        if (!scopedMergeScene.MergeCompatibleLineSegments(0, new[] { scopedFirst, scopedSecond }).Changed
            || scopedMergeScene.ObjectCount != 3
            || !Enumerable.Range(0, scopedMergeScene.ObjectCount).Any(index => LineMatches(scopedMergeScene, index, new PointF(0, 100), new PointF(100, 100)))
            || !Enumerable.Range(0, scopedMergeScene.ObjectCount).Any(index => LineMatches(scopedMergeScene, index, new PointF(104, 100), new PointF(204, 100))))
        {
            throw new InvalidOperationException("Scoped line merge changed segments outside its requested line chain.");
        }

        var exactThresholdScene = new VectorScene();
        exactThresholdScene.CreateEmpty();
        AddTopologyLine(exactThresholdScene, 0, new PointF(0, 0), new PointF(100, 0));
        AddTopologyLine(exactThresholdScene, 0, new PointF(105, 0), new PointF(205, 0));
        if (exactThresholdScene.MergeCompatibleLineSegments(0).Changed || exactThresholdScene.ObjectCount != 2)
        {
            throw new InvalidOperationException("Line endpoints exactly 5 vu apart were merged despite the strict distance boundary.");
        }

        var offsetParallelScene = new VectorScene();
        offsetParallelScene.CreateEmpty();
        AddTopologyLine(offsetParallelScene, 0, new PointF(0, 0), new PointF(100, 0));
        AddTopologyLine(offsetParallelScene, 0, new PointF(100, 4), new PointF(200, 4));
        if (offsetParallelScene.MergeCompatibleLineSegments(0).Changed || offsetParallelScene.ObjectCount != 2)
        {
            throw new InvalidOperationException("Parallel but non-collinear line segments were merged into distorted geometry.");
        }

        var accumulatedOffsetScene = new VectorScene();
        accumulatedOffsetScene.CreateEmpty();
        AddTopologyLine(accumulatedOffsetScene, 0, new PointF(0, 0), new PointF(100, 0));
        AddTopologyLine(accumulatedOffsetScene, 0, new PointF(104, 1), new PointF(204, 1));
        AddTopologyLine(accumulatedOffsetScene, 0, new PointF(208, 2), new PointF(308, 2));
        if (accumulatedOffsetScene.MergeCompatibleLineSegments(0).Changed || accumulatedOffsetScene.ObjectCount != 3)
        {
            throw new InvalidOperationException("Line merge accumulated a one-unit parallel offset through a transitive group.");
        }

        var nearToleranceChainScene = new VectorScene();
        nearToleranceChainScene.CreateEmpty();
        var nearToleranceOffset = DrawingTopologyRules.UnitIntersectionTolerance * 0.75f;
        AddTopologyLine(nearToleranceChainScene, 0, new PointF(0, 0), new PointF(100, 0));
        var nearMiddle = AddTopologyLine(nearToleranceChainScene, 0, new PointF(104, 0), new PointF(204, 0));
        var nearEnd = AddTopologyLine(nearToleranceChainScene, 0, new PointF(208, 0), new PointF(308, 0));
        // Keep sub-vu offsets that AddLineSegment normally quantizes so the transitive grouping path is exercised.
        nearToleranceChainScene.Y[nearMiddle] = nearToleranceOffset;
        nearToleranceChainScene.CurveControlY[nearMiddle] = nearToleranceOffset;
        nearToleranceChainScene.CurveControl2Y[nearMiddle] = nearToleranceOffset;
        nearToleranceChainScene.Y[nearEnd] = nearToleranceOffset * 2;
        nearToleranceChainScene.CurveControlY[nearEnd] = nearToleranceOffset * 2;
        nearToleranceChainScene.CurveControl2Y[nearEnd] = nearToleranceOffset * 2;
        if (nearToleranceChainScene.MergeCompatibleLineSegments(0).Changed || nearToleranceChainScene.ObjectCount != 3)
        {
            throw new InvalidOperationException("Line merge accumulated near-tolerance offsets through a transitive group.");
        }

        var directionScene = new VectorScene();
        directionScene.CreateEmpty();
        AddTopologyLine(directionScene, 0, new PointF(0, 0), new PointF(100, 0));
        AddTopologyLine(directionScene, 0, new PointF(100, 4), new PointF(100, 104));
        if (directionScene.MergeCompatibleLineSegments(0).Changed || directionScene.ObjectCount != 2)
        {
            throw new InvalidOperationException("Line segments with different directions were merged.");
        }

        var colorScene = new VectorScene();
        colorScene.CreateEmpty();
        colorScene.AddLineSegment(0, new PointF(0, 0), new PointF(100, 0), stroke, Color.Transparent, Color.Coral, 6);
        colorScene.AddLineSegment(0, new PointF(104, 0), new PointF(204, 0), stroke, Color.Transparent, Color.Aqua, 6);
        if (colorScene.MergeCompatibleLineSegments(0).Changed || colorScene.ObjectCount != 2)
        {
            throw new InvalidOperationException("Lines with different stroke ARGB values were merged.");
        }

        var fillColorScene = new VectorScene();
        fillColorScene.CreateEmpty();
        fillColorScene.AddLineSegment(0, new PointF(0, 0), new PointF(100, 0), stroke, Color.Coral, Color.White, 6);
        fillColorScene.AddLineSegment(0, new PointF(104, 0), new PointF(204, 0), stroke, Color.Aqua, Color.White, 6);
        if (fillColorScene.MergeCompatibleLineSegments(0).Changed || fillColorScene.ObjectCount != 2)
        {
            throw new InvalidOperationException("Lines with different fill ARGB values were merged.");
        }

        var widthScene = new VectorScene();
        widthScene.CreateEmpty();
        widthScene.AddLineSegment(0, new PointF(0, 0), new PointF(100, 0), stroke, Color.Transparent, Color.Coral, 6);
        widthScene.AddLineSegment(0, new PointF(104, 0), new PointF(204, 0), stroke + 1, Color.Transparent, Color.Coral, 6);
        if (widthScene.MergeCompatibleLineSegments(0).Changed || widthScene.ObjectCount != 2)
        {
            throw new InvalidOperationException("Lines with different stroke widths were merged.");
        }

        var curvedScene = new VectorScene();
        curvedScene.CreateEmpty();
        curvedScene.AddLineSegment(0, new PointF(0, 0), new PointF(100, 0), stroke, Color.Transparent, Color.Coral, 6);
        curvedScene.AddCurveSegment(0, new PointF(104, 0), new PointF(154, 40), new PointF(204, 0), stroke, Color.Transparent, Color.Coral, 6);
        if (curvedScene.MergeCompatibleLineSegments(0).Changed || curvedScene.ObjectCount != 2)
        {
            throw new InvalidOperationException("A curved line was treated as a mergeable straight segment.");
        }

        var layerScene = new VectorScene();
        layerScene.CreateEmpty(2);
        layerScene.AddLineSegment(0, new PointF(0, 0), new PointF(100, 0), stroke, Color.Transparent, Color.Coral, 6);
        layerScene.AddLineSegment(1, new PointF(104, 0), new PointF(204, 0), stroke, Color.Transparent, Color.Coral, 6);
        if (layerScene.MergeCompatibleLineSegments(0).Changed || layerScene.ObjectCount != 2)
        {
            throw new InvalidOperationException("Compatible lines on different layers were merged.");
        }

        var frameScene = new VectorScene();
        frameScene.CreateEmpty();
        frameScene.AddLineSegment(0, new PointF(0, 0), new PointF(100, 0), stroke, Color.Transparent, Color.Coral, 6);
        if (!frameScene.InsertTimelineBlankKeyframe(0, 10))
        {
            throw new InvalidOperationException("Line merge frame regression could not create an independent blank cel.");
        }

        frameScene.EditFrame = 10;
        frameScene.AddLineSegment(0, new PointF(104, 0), new PointF(204, 0), stroke, Color.Transparent, Color.Coral, 6);
        if (frameScene.MergeCompatibleLineSegments(0).Changed
            || frameScene.MergeCompatibleLineSegments(10).Changed
            || frameScene.ObjectCount != 2)
        {
            throw new InvalidOperationException("Line merge crossed active keyframe ownership.");
        }

        Console.WriteLine("line_segment_merge_regression=ok");
    }

    private static void RunCrossingFillTopologyRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        var fill = AddTopologyFill(scene, 0, 0);
        var line = AddTopologyLine(scene, 0, new PointF(-320, 0), new PointF(320, 0));

        AssertTopologyHit(scene, new PointF(-260, 0), line, DrawingElementKind.Stroke, 0, 0, 0.1875f, "crossing line left");
        var middleHit = AssertTopologyHit(scene, PointF.Empty, line, DrawingElementKind.Stroke, 1, 0.1875f, 0.8125f, "crossing line middle");
        AssertTopologyHit(scene, new PointF(260, 0), line, DrawingElementKind.Stroke, 2, 0.8125f, 1, "crossing line right");
        AssertTopologyHit(scene, new PointF(0, -80), fill, DrawingElementKind.Fill, 0, 0, 1, "crossing fill upper region");
        AssertTopologyHit(scene, new PointF(0, 80), fill, DrawingElementKind.Fill, 1, 0, 1, "crossing fill lower region");

        var detachedMiddle = scene.DetachElementForMove(middleHit, 0);
        AssertShapeCounts(scene, 4, lines: 3, paths: 0, rectangles: 1, "crossing line detach");
        AssertDetachedLine(scene, detachedMiddle, new PointF(-200, 0), new PointF(200, 0), "crossing line middle detach");
        AssertLineSet(
            scene,
            new[]
            {
                (new PointF(-320, 0), new PointF(-200, 0)),
                (new PointF(-200, 0), new PointF(200, 0)),
                (new PointF(200, 0), new PointF(320, 0))
            },
            "crossing line detach");

        var fillScene = new VectorScene();
        fillScene.CreateEmpty();
        var splitFill = AddTopologyFill(fillScene, 0, 0);
        AddTopologyLine(fillScene, 0, new PointF(-320, 0), new PointF(320, 0));
        var lowerHit = AssertTopologyHit(fillScene, new PointF(0, 80), splitFill, DrawingElementKind.Fill, 1, 0, 1, "crossing fill lower detach target");
        var detachedLower = fillScene.DetachElementForMove(lowerHit, 0);
        AssertShapeCounts(fillScene, 3, lines: 1, paths: 2, rectangles: 0, "crossing fill detach");
        AssertSplitPaths(fillScene, detachedLower, new PointF(0, 80), new PointF(0, -80), "crossing fill detach");
        AssertLineSet(fillScene, new[] { (new PointF(-320, 0), new PointF(320, 0)) }, "crossing fill detach");
    }

    private static void RunTerminatingLineTopologyRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        var fill = AddTopologyFill(scene, 0, 0);
        var line = AddTopologyLine(scene, 0, new PointF(-320, 0), PointF.Empty);

        AssertTopologyHit(scene, new PointF(-260, 0), line, DrawingElementKind.Stroke, 0, 0, 0.375f, "terminating line exterior");
        var interiorHit = AssertTopologyHit(scene, new PointF(-100, 0), line, DrawingElementKind.Stroke, 1, 0.375f, 1, "terminating line interior");
        AssertTopologyHit(scene, new PointF(0, -80), fill, DrawingElementKind.Fill, 0, 0, 1, "terminating line upper fill");
        var lowerFillHit = AssertTopologyHit(scene, new PointF(0, 80), fill, DrawingElementKind.Fill, 0, 0, 1, "terminating line lower fill");

        var unchangedFill = scene.DetachElementForMove(lowerFillHit, 0);
        if (scene.ObjectCount != 2 || unchangedFill.Key != lowerFillHit.Key)
        {
            throw new InvalidOperationException($"A line terminating inside a fill incorrectly split the fill: objects={scene.ObjectCount}, before={lowerFillHit.Key}, after={unchangedFill.Key}.");
        }

        var detachedInterior = scene.DetachElementForMove(interiorHit, 0);
        AssertShapeCounts(scene, 3, lines: 2, paths: 0, rectangles: 1, "terminating line detach");
        AssertDetachedLine(scene, detachedInterior, new PointF(-200, 0), PointF.Empty, "terminating line interior detach");
        AssertLineSet(
            scene,
            new[]
            {
                (new PointF(-320, 0), new PointF(-200, 0)),
                (new PointF(-200, 0), PointF.Empty)
            },
            "terminating line detach");
    }

    private static void RunTerminatingStrokeContactRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        var terminating = AddTopologyLine(scene, 0, new PointF(-200, 0), PointF.Empty);
        var through = AddTopologyLine(scene, 0, new PointF(0, -200), new PointF(0, 200));

        var upper = AssertTopologyHit(scene, new PointF(0, -100), through, DrawingElementKind.Stroke, 0, 0, 0.5f, "terminating contact upper");
        var lower = AssertTopologyHit(scene, new PointF(0, 100), through, DrawingElementKind.Stroke, 1, 0.5f, 1, "terminating contact lower");
        var connected = scene.GetConnectedStrokeElements(upper, 0);
        var materialized = scene.MaterializeSelectedParts(connected.Select(hit => hit.Key).ToArray(), 0);
        var contactEndpoints = Enumerable.Range(0, scene.ObjectCount)
            .Sum(index =>
            {
                scene.TryGetLineEndpoint(index, startEndpoint: true, out var start);
                scene.TryGetLineEndpoint(index, startEndpoint: false, out var end);
                return (PointsNear(start, PointF.Empty) ? 1 : 0) + (PointsNear(end, PointF.Empty) ? 1 : 0);
            });

        if (lower.Key.PartIndex != 1
            || connected.Length != 3
            || !connected.Select(hit => hit.Key.ObjectIndex).ToHashSet().SetEquals(new[] { terminating, through })
            || !materialized.Success
            || !materialized.Changed
            || scene.ObjectCount != 3
            || contactEndpoints != 3)
        {
            throw new InvalidOperationException("A terminating stroke contact did not preserve editable endpoints on the joined node.");
        }
    }

    private static void RunCommittedTerminatingCurveSplitRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        var start = new PointF(-200, 100);
        var control1 = new PointF(-50, -200);
        var control2 = new PointF(80, 200);
        var end = new PointF(220, -80);
        var through = scene.AddCubicCurveSegment(
            0,
            start,
            control1,
            control2,
            end,
            18,
            Color.Transparent,
            Color.CornflowerBlue,
            12,
            LineEndpointStyle.Sharp,
            LineEndpointStyle.Sharp);
        scene.SetLinearGradient(through, Color.CornflowerBlue, Color.Gold);
        var exactContact = CubicPoint(start, control1, control2, end, 0.5f);
        var quantizedContact = VectorUnits.Quantize(exactContact);
        var terminating = scene.AddLineSegment(
            0,
            new PointF(-180, -120),
            quantizedContact,
            24,
            Color.Transparent,
            Color.White,
            8);

        var result = scene.MaterializeLineIntersections([terminating, through], 0);
        var splitCurves = result.Parts
            .Where(part => part.Source.ObjectIndex == through)
            .Select(part => part.Result.ObjectIndex)
            .Distinct()
            .ToArray();
        var contactEndpoints = Enumerable.Range(0, scene.ObjectCount)
            .Sum(index =>
            {
                scene.TryGetLineEndpoint(index, startEndpoint: true, out var lineStart);
                scene.TryGetLineEndpoint(index, startEndpoint: false, out var lineEnd);
                return (PointsNear(lineStart, quantizedContact) ? 1 : 0)
                    + (PointsNear(lineEnd, quantizedContact) ? 1 : 0);
            });
        var outerSharpEndpoints = splitCurves.Sum(index =>
            (scene.GetLineEndpointStyle(index, startEndpoint: true) == LineEndpointStyle.Sharp ? 1 : 0)
            + (scene.GetLineEndpointStyle(index, startEndpoint: false) == LineEndpointStyle.Sharp ? 1 : 0));
        if (!result.Success
            || !result.Changed
            || scene.ObjectCount != 3
            || splitCurves.Length != 2
            || contactEndpoints != 3
            || outerSharpEndpoints != 2
            || splitCurves.Any(index => !scene.HasGradient(index)))
        {
            throw new InvalidOperationException(
                $"A quantized line endpoint did not materialize its terminating cubic contact: " +
                $"success={result.Success}, changed={result.Changed}, objects={scene.ObjectCount}, " +
                $"splits={splitCurves.Length}, endpoints={contactEndpoints}, sharp={outerSharpEndpoints}.");
        }

        static PointF CubicPoint(PointF p0, PointF p1, PointF p2, PointF p3, float t)
        {
            var inverse = 1f - t;
            var a = inverse * inverse * inverse;
            var b = 3f * inverse * inverse * t;
            var c = 3f * inverse * t * t;
            var d = t * t * t;
            return new PointF(
                p0.X * a + p1.X * b + p2.X * c + p3.X * d,
                p0.Y * a + p1.Y * b + p2.Y * c + p3.Y * d);
        }
    }

    private static void RunCrossingLinesTopologyRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        var horizontal = AddTopologyLine(scene, 0, new PointF(-320, 0), new PointF(320, 0));
        var vertical = AddTopologyLine(scene, 0, new PointF(0, -240), new PointF(0, 240));

        AssertTopologyHit(scene, new PointF(-160, 0), horizontal, DrawingElementKind.Stroke, 0, 0, 0.5f, "crossing lines horizontal left");
        var horizontalRight = AssertTopologyHit(scene, new PointF(160, 0), horizontal, DrawingElementKind.Stroke, 1, 0.5f, 1, "crossing lines horizontal right");
        AssertTopologyHit(scene, new PointF(0, -120), vertical, DrawingElementKind.Stroke, 0, 0, 0.5f, "crossing lines vertical upper");
        AssertTopologyHit(scene, new PointF(0, 120), vertical, DrawingElementKind.Stroke, 1, 0.5f, 1, "crossing lines vertical lower");

        var detachedRight = scene.DetachElementForMove(horizontalRight, 0);
        AssertShapeCounts(scene, 3, lines: 3, paths: 0, rectangles: 0, "crossing lines detach");
        AssertDetachedLine(scene, detachedRight, PointF.Empty, new PointF(320, 0), "crossing lines horizontal right detach");
        AssertLineSet(
            scene,
            new[]
            {
                (new PointF(-320, 0), PointF.Empty),
                (PointF.Empty, new PointF(320, 0)),
                (new PointF(0, -240), new PointF(0, 240))
            },
            "crossing lines detach");
    }

    private static void RunCollinearOverlapTopologyRegression()
    {
        var firstScene = new VectorScene();
        firstScene.CreateEmpty();
        var first = AddTopologyLine(firstScene, 0, new PointF(-100, 0), new PointF(100, 0));
        var firstOverlap = AddTopologyLine(firstScene, 0, PointF.Empty, new PointF(200, 0));

        var firstLeft = AssertTopologyHit(firstScene, new PointF(-50, 0), first, DrawingElementKind.Stroke, 0, 0, 0.5f, "collinear first line left");
        AssertTopologyHit(firstScene, new PointF(50, 0), firstOverlap, DrawingElementKind.Stroke, 0, 0, 0.5f, "collinear overlap priority");
        var detachedFirst = firstScene.DetachElementForMove(firstLeft, 0);
        AssertShapeCounts(firstScene, 3, lines: 3, paths: 0, rectangles: 0, "collinear first line detach");
        AssertDetachedLine(firstScene, detachedFirst, new PointF(-100, 0), PointF.Empty, "collinear first line detach");
        AssertLineSet(
            firstScene,
            new[]
            {
                (new PointF(-100, 0), PointF.Empty),
                (PointF.Empty, new PointF(100, 0)),
                (PointF.Empty, new PointF(200, 0))
            },
            "collinear first line detach");

        var secondScene = new VectorScene();
        secondScene.CreateEmpty();
        var second = AddTopologyLine(secondScene, 0, PointF.Empty, new PointF(200, 0));
        var secondOverlap = AddTopologyLine(secondScene, 0, new PointF(-100, 0), new PointF(100, 0));

        AssertTopologyHit(secondScene, new PointF(50, 0), secondOverlap, DrawingElementKind.Stroke, 1, 0.5f, 1, "collinear reverse overlap priority");
        var secondRight = AssertTopologyHit(secondScene, new PointF(150, 0), second, DrawingElementKind.Stroke, 1, 0.5f, 1, "collinear second line right");
        var detachedSecond = secondScene.DetachElementForMove(secondRight, 0);
        AssertShapeCounts(secondScene, 3, lines: 3, paths: 0, rectangles: 0, "collinear second line detach");
        AssertDetachedLine(secondScene, detachedSecond, new PointF(100, 0), new PointF(200, 0), "collinear second line detach");
        AssertLineSet(
            secondScene,
            new[]
            {
                (new PointF(-100, 0), new PointF(100, 0)),
                (PointF.Empty, new PointF(100, 0)),
                (new PointF(100, 0), new PointF(200, 0))
            },
            "collinear second line detach");

        var diagonalScene = new VectorScene();
        diagonalScene.CreateEmpty();
        var diagonalFirst = AddTopologyLine(diagonalScene, 0, new PointF(-100, -50), new PointF(100, 50));
        var diagonalSecond = AddTopologyLine(diagonalScene, 0, PointF.Empty, new PointF(200, 100));
        AssertTopologyHit(diagonalScene, new PointF(-50, -25), diagonalFirst, DrawingElementKind.Stroke, 0, 0, 0.5f, "diagonal collinear first line");
        AssertTopologyHit(diagonalScene, new PointF(150, 75), diagonalSecond, DrawingElementKind.Stroke, 1, 0.5f, 1, "diagonal collinear second line");
    }

    private static void RunOutlinedBoundaryTopologyRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        var outlinedFill = AddTopologyFill(scene, 0, VectorUnits.StrokePointsToUnits(2));
        var crossingLine = AddTopologyLine(scene, 0, new PointF(-320, 0), new PointF(320, 0));

        var topHit = AssertTopologyHit(scene, new PointF(0, -120), outlinedFill, DrawingElementKind.BoundaryStroke, 0, 0, 0.25f, "outlined boundary top");
        AssertTopologyHit(scene, new PointF(200, -60), outlinedFill, DrawingElementKind.BoundaryStroke, 1, 0.25f, 0.375f, "outlined boundary upper right");
        AssertTopologyHit(scene, new PointF(200, 60), outlinedFill, DrawingElementKind.BoundaryStroke, 2, 0.375f, 0.5f, "outlined boundary lower right");
        AssertTopologyHit(scene, new PointF(0, 120), outlinedFill, DrawingElementKind.BoundaryStroke, 3, 0.5f, 0.75f, "outlined boundary bottom");
        AssertTopologyHit(scene, new PointF(-200, 60), outlinedFill, DrawingElementKind.BoundaryStroke, 4, 0.75f, 0.875f, "outlined boundary lower left");
        AssertTopologyHit(scene, new PointF(-200, -60), outlinedFill, DrawingElementKind.BoundaryStroke, 5, 0.875f, 1, "outlined boundary upper left");
        AssertTopologyHit(scene, new PointF(-260, 0), crossingLine, DrawingElementKind.Stroke, 0, 0, 0.1875f, "outlined crossing line left");
        AssertTopologyHit(scene, PointF.Empty, crossingLine, DrawingElementKind.Stroke, 1, 0.1875f, 0.8125f, "outlined crossing line middle");
        AssertTopologyHit(scene, new PointF(260, 0), crossingLine, DrawingElementKind.Stroke, 2, 0.8125f, 1, "outlined crossing line right");

        var detachedTop = scene.DetachElementForMove(topHit, 0);
        AssertShapeCounts(scene, 8, lines: 7, paths: 0, rectangles: 1, "outlined boundary detach");
        if ((uint)outlinedFill >= scene.ObjectCount || scene.Stroke[outlinedFill] != 0)
        {
            throw new InvalidOperationException("Detaching an outlined boundary segment did not remove the original object's compound boundary stroke.");
        }

        AssertDetachedLine(scene, detachedTop, new PointF(-200, -120), new PointF(200, -120), "outlined boundary top detach");
        AssertLineSet(scene, OutlinedBoundaryLineSegments(), "outlined boundary detach");

        var fillScene = new VectorScene();
        fillScene.CreateEmpty();
        var fillWithOutline = AddTopologyFill(fillScene, 0, VectorUnits.StrokePointsToUnits(2));
        AddTopologyLine(fillScene, 0, new PointF(-320, 0), new PointF(320, 0));
        var lowerFillHit = AssertTopologyHit(fillScene, new PointF(0, 80), fillWithOutline, DrawingElementKind.Fill, 1, 0, 1, "outlined fill lower detach target");
        var detachedLower = fillScene.DetachElementForMove(lowerFillHit, 0);
        AssertShapeCounts(fillScene, 9, lines: 7, paths: 2, rectangles: 0, "outlined fill detach");
        AssertSplitPaths(fillScene, detachedLower, new PointF(0, 80), new PointF(0, -80), "outlined fill detach");
        AssertLineSet(fillScene, OutlinedBoundaryLineSegments(), "outlined fill detach");
    }

    private static void RunSmoothPathBoundaryGroupingRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        var contour = new List<PointF>
        {
            new(-240, -140),
            new(160, -140)
        };
        var curveStart = contour[^1];
        var control1 = new PointF(160, -40);
        var control2 = new PointF(260, 40);
        var curveEnd = new PointF(160, 140);
        for (var sample = 1; sample <= 16; sample++)
        {
            var t = sample / 16f;
            var inverse = 1 - t;
            contour.Add(new PointF(
                curveStart.X * inverse * inverse * inverse
                    + 3 * control1.X * inverse * inverse * t
                    + 3 * control2.X * inverse * t * t
                    + curveEnd.X * t * t * t,
                curveStart.Y * inverse * inverse * inverse
                    + 3 * control1.Y * inverse * inverse * t
                    + 3 * control2.Y * inverse * t * t
                    + curveEnd.Y * t * t * t));
        }
        contour.Add(new PointF(-200, 140));
        contour.Add(contour[0]);

        var outlinedPath = scene.AddPathObjectContours(
            0,
            [contour.ToArray()],
            VectorUnits.StrokePointsToUnits(2),
            Color.Teal,
            Color.White,
            32);
        var boundaryParts = scene.GetBoundaryParts(outlinedPath, 0);
        var smoothPart = boundaryParts.FirstOrDefault(part => part.Points.Length >= 12);
        if (boundaryParts.Length != 4
            || smoothPart.Points is null
            || smoothPart.Points.Length < 12)
        {
            throw new InvalidOperationException(
                $"A smooth outlined Path boundary was split at its sampling points: parts={boundaryParts.Length}, "
                + $"longest={boundaryParts.Select(part => part.Points.Length).DefaultIfEmpty(0).Max()}.");
        }
    }

    private static void RunCrossLayerTopologyRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty(2);
        var fill = AddTopologyFill(scene, 1, 0);
        var line = AddTopologyLine(scene, 0, new PointF(-320, 0), new PointF(320, 0));

        var lineHit = AssertTopologyHit(scene, PointF.Empty, line, DrawingElementKind.Stroke, 0, 0, 1, "cross-layer line");
        AssertTopologyHit(scene, new PointF(0, -80), fill, DrawingElementKind.Fill, 0, 0, 1, "cross-layer upper fill");
        AssertTopologyHit(scene, new PointF(0, 80), fill, DrawingElementKind.Fill, 0, 0, 1, "cross-layer lower fill");

        var unchangedLine = scene.DetachElementForMove(lineHit, 0);
        if (scene.ObjectCount != 2 || unchangedLine.Key != lineHit.Key)
        {
            throw new InvalidOperationException($"Objects on different layers incorrectly participated in topology splitting: objects={scene.ObjectCount}, before={lineHit.Key}, after={unchangedLine.Key}.");
        }
    }

    private static void RunMultipleCutterFillTopologyRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        var fill = AddTopologyFill(scene, 0, 0);
        AddTopologyLine(scene, 0, new PointF(-320, 0), new PointF(320, 0));
        AddTopologyLine(scene, 0, new PointF(0, -240), new PointF(0, 240));

        var points = new[]
        {
            new PointF(-100, -70),
            new PointF(100, -70),
            new PointF(-100, 70),
            new PointF(100, 70)
        };
        var hits = points.Select(point => scene.HitTestElement(point, 0, 0)).ToArray();
        if (hits.Any(hit => !hit.IsValid || hit.Key.ObjectIndex != fill || hit.Key.Kind != DrawingElementKind.Fill)
            || hits.Select(hit => hit.Key.PartIndex).Distinct().Count() != 4)
        {
            throw new InvalidOperationException("Two crossing cutters did not expose four independently selectable fill regions.");
        }

        var detached = scene.DetachElementForMove(hits[3], 0);
        AssertShapeCounts(scene, 6, lines: 2, paths: 4, rectangles: 0, "multiple cutter fill detach");
        if (!detached.IsValid || detached.Key.Kind != DrawingElementKind.Fill || !scene.FillContainsPoint(detached.Key.ObjectIndex, points[3]))
        {
            throw new InvalidOperationException("Detaching one quadrant did not return the selected fill region.");
        }

        foreach (var point in points)
        {
            if (CountPathFillsContaining(scene, point) != 1)
            {
                throw new InvalidOperationException($"Multiple-cutter materialization did not preserve exactly one fill at ({point.X},{point.Y}).");
            }
        }
    }

    private static void RunCurvedCutterFillTopologyRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        var fill = AddTopologyFill(scene, 0, 0);
        scene.AddCurveSegment(
            0,
            new PointF(-320, 0),
            new PointF(0, 200),
            new PointF(320, 0),
            VectorUnits.StrokePointsToUnits(2),
            Color.Transparent,
            Color.White,
            12);

        var aboveCurve = new PointF(0, 45);
        var belowCurve = new PointF(0, 118);
        var upperHit = scene.HitTestElement(aboveCurve, 0, 0);
        var lowerHit = scene.HitTestElement(belowCurve, 0, 0);
        if (!upperHit.IsValid
            || !lowerHit.IsValid
            || upperHit.Key.ObjectIndex != fill
            || lowerHit.Key.ObjectIndex != fill
            || upperHit.Key.Kind != DrawingElementKind.Fill
            || lowerHit.Key.Kind != DrawingElementKind.Fill
            || upperHit.Key.PartIndex == lowerHit.Key.PartIndex)
        {
            throw new InvalidOperationException("A curved stroke did not split the fill along its rendered curve.");
        }

        var detached = scene.DetachElementForMove(lowerHit, 0);
        AssertShapeCounts(scene, 3, lines: 1, paths: 2, rectangles: 0, "curved cutter fill detach");
        AssertSplitPaths(scene, detached, belowCurve, aboveCurve, "curved cutter fill detach");
    }

    private static void RunScaledClosedCutterFillTopologyRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        var fill = scene.AddPathObjectContours(
            0,
            [[
                new PointF(-200, -100),
                new PointF(0, -35),
                new PointF(220, -100),
                new PointF(210, 80),
                new PointF(220, 200),
                new PointF(-200, 200)
            ]],
            0,
            Color.Teal,
            Color.Transparent,
            24);
        if (!scene.TryConvertFillToBezierPath(fill)
            || !scene.SetPathBezierSegment(
                fill,
                0,
                new PointF(-200, -100),
                new PointF(-120, -20),
                new PointF(-60, -20),
                new PointF(0, -35))
            || !scene.SetPathBezierSegment(
                fill,
                1,
                new PointF(0, -35),
                new PointF(90, -55),
                new PointF(210, -170),
                new PointF(220, -100))
            || !scene.SetPathBezierSegment(
                fill,
                2,
                new PointF(220, -100),
                new PointF(300, -90),
                new PointF(230, 20),
                new PointF(210, 80))
            || !scene.SetPathBezierSegment(
                fill,
                3,
                new PointF(210, 80),
                new PointF(170, 130),
                new PointF(170, 180),
                new PointF(220, 200)))
        {
            throw new InvalidOperationException("The closed Pen-chain Fill split regression could not prepare its Bezier boundary.");
        }

        scene.AddLineSegment(
            0,
            new PointF(0, -220),
            new PointF(0, 80),
            VectorUnits.StrokePointsToUnits(2),
            Color.Transparent,
            Color.White,
            8);
        scene.AddLineSegment(
            0,
            new PointF(0, 80),
            new PointF(360, 80),
            VectorUnits.StrokePointsToUnits(2),
            Color.Transparent,
            Color.White,
            8);
        scene.AddCubicCurveSegment(
            0,
            new PointF(360, 80),
            new PointF(320, -180),
            new PointF(120, -260),
            new PointF(0, -220),
            VectorUnits.StrokePointsToUnits(2),
            Color.Transparent,
            Color.White,
            12);
        scene.TransformObjects(
            Enumerable.Range(0, scene.ObjectCount),
            point => new PointF(point.X * VectorUnits.UnitsPerPixel, point.Y * VectorUnits.UnitsPerPixel));

        var upperPoint = new PointF(100 * VectorUnits.UnitsPerPixel, 0);
        var lowerPoint = new PointF(-100 * VectorUnits.UnitsPerPixel, 120 * VectorUnits.UnitsPerPixel);
        var upper = scene.HitTestElement(upperPoint, 0, 0);
        var lower = scene.HitTestElement(lowerPoint, 0, 0);
        var parts = scene.GetFillParts(fill, 0);
        if (parts.Length != 2
            || !upper.IsValid
            || !lower.IsValid
            || upper.Key.ObjectIndex != fill
            || lower.Key.ObjectIndex != fill
            || upper.Key.Kind != DrawingElementKind.Fill
            || lower.Key.Kind != DrawingElementKind.Fill
            || upper.Key.PartIndex == lower.Key.PartIndex)
        {
            throw new InvalidOperationException(
                $"A full-scale closed Pen chain did not split a Bezier Fill into selectable regions: parts={parts.Length}, upper={upper.Key}, lower={lower.Key}.");
        }

        var detached = scene.DetachElementForMove(lower, 0);
        AssertSplitPaths(scene, detached, lowerPoint, upperPoint, "full-scale closed Pen chain fill detach");
    }

    private static void RunCompoundFillTopologyRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        var fill = scene.AddPathObjectContours(
            0,
            new[]
            {
                new[] { new PointF(-200, -120), new PointF(200, -120), new PointF(200, 120), new PointF(-200, 120) },
                new[] { new PointF(-60, -40), new PointF(60, -40), new PointF(60, 40), new PointF(-60, 40) },
                new[] { new PointF(260, -40), new PointF(340, -40), new PointF(340, 40), new PointF(260, 40) }
            },
            0,
            Color.Teal,
            Color.White,
            24);
        AddTopologyLine(scene, 0, new PointF(-320, 80), new PointF(240, 80));

        var upperPoint = new PointF(-120, 0);
        var lowerPoint = new PointF(0, 100);
        var islandPoint = new PointF(300, 0);
        var hits = new[]
        {
            scene.HitTestElement(upperPoint, 0, 0),
            scene.HitTestElement(lowerPoint, 0, 0),
            scene.HitTestElement(islandPoint, 0, 0)
        };
        if (hits.Any(hit => !hit.IsValid || hit.Key.ObjectIndex != fill || hit.Key.Kind != DrawingElementKind.Fill)
            || hits.Select(hit => hit.Key.PartIndex).Distinct().Count() != 3)
        {
            throw new InvalidOperationException("Compound Path fill regions were not independently selectable.");
        }

        var detached = scene.DetachElementForMove(hits[1], 0);
        AssertShapeCounts(scene, 4, lines: 1, paths: 3, rectangles: 0, "compound fill detach");
        if (!detached.IsValid || !scene.FillContainsPoint(detached.Key.ObjectIndex, lowerPoint))
        {
            throw new InvalidOperationException("Compound fill detach did not return the selected lower region.");
        }

        var upperPath = FindPathFillContaining(scene, upperPoint);
        var islandPath = FindPathFillContaining(scene, islandPoint);
        if (upperPath < 0 || islandPath < 0 || scene.FillContainsPoint(upperPath, PointF.Empty))
        {
            throw new InvalidOperationException("Compound fill detach lost its hole or secondary island.");
        }
    }

    private static void RunPencilStrokeTopologyRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        var pencil = scene.AddFreehandStroke(
            0,
            new[] { new PointF(-200, 0), PointF.Empty, new PointF(200, 0) },
            VectorUnits.StrokePointsToUnits(2),
            Color.White,
            brushStroke: false,
            9);
        AddTopologyLine(scene, 0, new PointF(0, -200), new PointF(0, 200));

        AssertTopologyHit(scene, new PointF(-100, 0), pencil, DrawingElementKind.Stroke, 0, 0, 0.5f, "pencil left segment");
        var rightHit = AssertTopologyHit(scene, new PointF(100, 0), pencil, DrawingElementKind.Stroke, 1, 0.5f, 1, "pencil right segment");
        var detached = scene.DetachElementForMove(rightHit, 0);
        var freeforms = Enumerable.Range(0, scene.ObjectCount).Where(index => scene.ShapeKind[index] == ShapeKind.Freeform).ToArray();
        if (scene.ObjectCount != 3
            || freeforms.Length != 2
            || !detached.IsValid
            || scene.ShapeKind[detached.Key.ObjectIndex] != ShapeKind.Freeform
            || !scene.TryGetFreehandWorldPoints(detached.Key.ObjectIndex, out var detachedPoints)
            || detachedPoints.Length < 2
            || !PointsNear(detachedPoints[0], PointF.Empty)
            || !PointsNear(detachedPoints[^1], new PointF(200, 0)))
        {
            throw new InvalidOperationException("Pencil stroke intersection did not materialize the selected local segment.");
        }

        var fillScene = new VectorScene();
        fillScene.CreateEmpty();
        var fill = AddTopologyFill(fillScene, 0, 0);
        fillScene.AddFreehandStroke(
            0,
            new[] { new PointF(-320, 0), PointF.Empty, new PointF(320, 0) },
            VectorUnits.StrokePointsToUnits(2),
            Color.White,
            brushStroke: false,
            9);
        var upper = fillScene.HitTestElement(new PointF(0, -80), 0, 0);
        var lower = fillScene.HitTestElement(new PointF(0, 80), 0, 0);
        if (!upper.IsValid
            || !lower.IsValid
            || upper.Key.ObjectIndex != fill
            || lower.Key.ObjectIndex != fill
            || upper.Key.PartIndex == lower.Key.PartIndex)
        {
            throw new InvalidOperationException("A Pencil stroke traversing a fill did not create selectable fill regions.");
        }
    }

    private static void RunConnectedCutterNetworkTopologyRegression()
    {
        var vScene = new VectorScene();
        vScene.CreateEmpty();
        var vFill = AddTopologyFill(vScene, 0, 0);
        AddTopologyLine(vScene, 0, new PointF(-200, -80), PointF.Empty);
        AddTopologyLine(vScene, 0, new PointF(200, -70), PointF.Empty);
        var vUpper = vScene.HitTestElement(new PointF(0, -60), 0, 0);
        var vLower = vScene.HitTestElement(new PointF(0, 80), 0, 0);
        if (!vUpper.IsValid
            || !vLower.IsValid
            || vUpper.Key.ObjectIndex != vFill
            || vLower.Key.ObjectIndex != vFill
            || vUpper.Key.Kind != DrawingElementKind.Fill
            || vLower.Key.Kind != DrawingElementKind.Fill
            || vUpper.Key.PartIndex == vLower.Key.PartIndex)
        {
            throw new InvalidOperationException("Two snapped Line objects did not form one continuous fill cutter.");
        }

        var tScene = new VectorScene();
        tScene.CreateEmpty();
        var tFill = AddTopologyFill(tScene, 0, 0);
        AddTopologyLine(tScene, 0, new PointF(-320, 0), new PointF(320, 0));
        AddTopologyLine(tScene, 0, PointF.Empty, new PointF(0, 80));
        var lowerHit = tScene.HitTestElement(new PointF(100, 80), 0, 0);
        if (!lowerHit.IsValid || lowerHit.Key.ObjectIndex != tFill || lowerHit.Key.Kind != DrawingElementKind.Fill)
        {
            throw new InvalidOperationException("T-junction fill setup did not expose the lower region.");
        }

        var detached = tScene.DetachElementForMove(lowerHit, 0);
        var selected = detached.Key.ObjectIndex;
        if (!detached.IsValid
            || detached.Key.Kind != DrawingElementKind.Fill
            || !tScene.FillContainsPoint(selected, new PointF(0, 10))
            || !tScene.FillContainsPoint(selected, new PointF(-1, 40))
            || !tScene.FillContainsPoint(selected, new PointF(1, 40))
            || CountPathFillsContaining(tScene, new PointF(0, 40)) != 1)
        {
            throw new InvalidOperationException("A dangling T-junction branch left a false notch in the materialized fill.");
        }

        var collinearScene = new VectorScene();
        collinearScene.CreateEmpty();
        var collinearFill = AddTopologyFill(collinearScene, 0, 0);
        AddTopologyLine(collinearScene, 0, new PointF(-320, -80), PointF.Empty);
        AddTopologyLine(collinearScene, 0, PointF.Empty, new PointF(320, 80));
        var collinearUpper = collinearScene.HitTestElement(new PointF(0, -60), 0, 0);
        var collinearLower = collinearScene.HitTestElement(new PointF(0, 60), 0, 0);
        if (!collinearUpper.IsValid
            || !collinearLower.IsValid
            || collinearUpper.Key.ObjectIndex != collinearFill
            || collinearLower.Key.ObjectIndex != collinearFill
            || collinearUpper.Key.Kind != DrawingElementKind.Fill
            || collinearLower.Key.Kind != DrawingElementKind.Fill
            || collinearUpper.Key.PartIndex == collinearLower.Key.PartIndex)
        {
            throw new InvalidOperationException("Two collinear Line objects sharing one endpoint did not form a continuous fill cutter.");
        }

        var collinearDetached = collinearScene.DetachElementForMove(collinearLower, 0);
        AssertShapeCounts(collinearScene, 4, lines: 2, paths: 2, rectangles: 0, "collinear connected fill detach");
        AssertSplitPaths(collinearScene, collinearDetached, new PointF(0, 60), new PointF(0, -60), "collinear connected fill detach");
    }

    private static void RunConnectedStrokeSelectionRegression()
    {
        var chainScene = new VectorScene();
        chainScene.CreateEmpty(2);
        var first = AddTopologyLine(chainScene, 0, new PointF(-200, 0), PointF.Empty);
        var second = AddTopologyLine(chainScene, 0, PointF.Empty, new PointF(200, 0));
        var third = AddTopologyLine(chainScene, 0, new PointF(200, 0), new PointF(200, 120));
        var disconnected = AddTopologyLine(chainScene, 0, new PointF(-200, 40), new PointF(0, 40));
        var otherLayer = AddTopologyLine(chainScene, 1, PointF.Empty, new PointF(0, -120));
        var chainSeed = chainScene.HitTestElement(new PointF(-100, 0), 0, 0);
        var chainSelection = chainScene.GetConnectedStrokeElements(chainSeed, 0);
        var chainObjects = chainSelection.Select(hit => hit.Key.ObjectIndex).ToHashSet();
        if (!chainSeed.IsValid
            || chainSelection.Length != 3
            || !chainObjects.SetEquals(new[] { first, second, third })
            || chainObjects.Contains(disconnected)
            || chainObjects.Contains(otherLayer))
        {
            throw new InvalidOperationException("Connected stroke selection did not follow endpoint links within the active layer.");
        }

        var crossingScene = new VectorScene();
        crossingScene.CreateEmpty();
        var horizontal = AddTopologyLine(crossingScene, 0, new PointF(-200, 0), new PointF(200, 0));
        var vertical = AddTopologyLine(crossingScene, 0, new PointF(0, -150), new PointF(0, 150));
        var crossingSeed = crossingScene.HitTestElement(new PointF(-100, 0), 0, 0);
        var crossingSelection = crossingScene.GetConnectedStrokeElements(crossingSeed, 0);
        if (crossingSelection.Length != 4
            || crossingSelection.Count(hit => hit.Key.ObjectIndex == horizontal) != 2
            || crossingSelection.Count(hit => hit.Key.ObjectIndex == vertical) != 2)
        {
            throw new InvalidOperationException("Connected stroke selection did not traverse a split crossing network.");
        }

        var crossingNodeParts = crossingSelection
            .Where(hit =>
            {
                var points = crossingScene.GetStrokePartPoints(hit, 0);
                return points.Length > 0
                    && (Math.Abs(points[0].X) <= 0.001f && Math.Abs(points[0].Y) <= 0.001f
                        || Math.Abs(points[^1].X) <= 0.001f && Math.Abs(points[^1].Y) <= 0.001f);
            })
            .ToArray();
        var crossingNodeMaterialized = crossingScene.MaterializeSelectedParts(
            crossingNodeParts.Select(hit => hit.Key).ToArray(),
            0);
        var crossingNodeEndpoints = Enumerable.Range(0, crossingScene.ObjectCount)
            .Sum(index =>
            {
                crossingScene.TryGetLineEndpoint(index, startEndpoint: true, out var start);
                crossingScene.TryGetLineEndpoint(index, startEndpoint: false, out var end);
                return (Math.Abs(start.X) <= 0.001f && Math.Abs(start.Y) <= 0.001f ? 1 : 0)
                    + (Math.Abs(end.X) <= 0.001f && Math.Abs(end.Y) <= 0.001f ? 1 : 0);
            });
        if (!crossingNodeMaterialized.Success
            || !crossingNodeMaterialized.Changed
            || crossingNodeParts.Length != 4
            || crossingScene.ObjectCount != 4
            || crossingNodeEndpoints != 4)
        {
            throw new InvalidOperationException(
                $"Crossing endpoint materialization did not preserve all node-linked line endpoints: success={crossingNodeMaterialized.Success}, changed={crossingNodeMaterialized.Changed}, parts={crossingNodeParts.Length}, objects={crossingScene.ObjectCount}, endpoints={crossingNodeEndpoints}.");
        }

        var boundaryScene = new VectorScene();
        boundaryScene.CreateEmpty();
        var outlined = AddTopologyFill(boundaryScene, 0, VectorUnits.StrokePointsToUnits(2));
        var attachedLine = AddTopologyLine(boundaryScene, 0, new PointF(-320, 0), new PointF(-200, 0));
        var boundarySeed = boundaryScene.HitTestElement(new PointF(-260, 0), 0, 0);
        var boundarySelection = boundaryScene.GetConnectedStrokeElements(boundarySeed, 0);
        var selectedKeys = boundarySelection.Select(hit => hit.Key).ToHashSet();
        var boundaryParts = boundaryScene.GetBoundaryParts(outlined, 0);
        if (!selectedKeys.Any(key => key.ObjectIndex == attachedLine && key.Kind == DrawingElementKind.Stroke)
            || boundaryParts.Length == 0
            || boundaryParts.Any(part => !selectedKeys.Contains(new DrawingElementKey(outlined, DrawingElementKind.BoundaryStroke, part.PartIndex))))
        {
            throw new InvalidOperationException("Connected stroke selection did not traverse an attached Fill boundary.");
        }
    }

    private static void RunMaterializedOrderTopologyRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        var lower = AddTopologyLine(scene, 0, new PointF(-200, 0), new PointF(200, 0));
        var upper = AddTopologyLine(scene, 0, new PointF(0, -200), new PointF(0, 200));
        var lowerOrder = scene.ObjectOrder[lower];
        var upperOrder = scene.ObjectOrder[upper];
        var rightHit = scene.HitTestElement(new PointF(100, 0), 0, 0);
        if (!rightHit.IsValid || rightHit.Key.ObjectIndex != lower || rightHit.Key.PartIndex != 1)
        {
            throw new InvalidOperationException("Materialized order regression could not select the lower line segment.");
        }

        scene.DetachElementForMove(rightHit, 0);
        var crossingHit = scene.HitTestElement(PointF.Empty, 0, 0);
        if (!crossingHit.IsValid || scene.ObjectOrder[crossingHit.Key.ObjectIndex] != upperOrder)
        {
            throw new InvalidOperationException("Materializing a lower stroke changed same-layer stroke priority.");
        }

        var lowerParts = Enumerable.Range(0, scene.ObjectCount).Count(index => scene.ObjectOrder[index] == lowerOrder);
        if (lowerParts != 2)
        {
            throw new InvalidOperationException($"Materialized stroke parts did not inherit the source draw order: parts={lowerParts}.");
        }

        var recursiveScene = new VectorScene();
        recursiveScene.CreateEmpty();
        var recursiveSource = AddTopologyLine(recursiveScene, 0, new PointF(-200, 0), new PointF(200, 0));
        var recursiveOrder = recursiveScene.ObjectOrder[recursiveSource];
        AddTopologyLine(recursiveScene, 0, new PointF(0, -160), new PointF(0, 160));
        var recursiveRight = recursiveScene.HitTestElement(new PointF(100, 0), 0, 0);
        recursiveScene.DetachElementForMove(recursiveRight, 0);
        var siblings = Enumerable.Range(0, recursiveScene.ObjectCount)
            .Where(index => recursiveScene.ObjectOrder[index] == recursiveOrder)
            .OrderBy(index => recursiveScene.ObjectSubOrder[index])
            .ToArray();
        if (siblings.Length != 2)
        {
            throw new InvalidOperationException("Recursive materialized order regression did not create two sibling segments.");
        }

        var lowerSibling = siblings[0];
        var upperSibling = siblings[1];
        var dx = recursiveScene.X[upperSibling] - recursiveScene.X[lowerSibling];
        recursiveScene.X[lowerSibling] += dx;
        recursiveScene.CurveControlX[lowerSibling] += dx;
        recursiveScene.CurveControl2X[lowerSibling] += dx;
        recursiveScene.RebuildGeometryIndex();
        AddTopologyLine(recursiveScene, 0, new PointF(100, -160), new PointF(100, 160));
        var beforeRecursiveSplit = recursiveScene.HitTestElement(new PointF(50, 0), 0, 0);
        if (!beforeRecursiveSplit.IsValid || beforeRecursiveSplit.Key.ObjectIndex != upperSibling)
        {
            throw new InvalidOperationException("Recursive materialized order setup did not place the higher sibling on top.");
        }

        var recursiveResult = recursiveScene.MaterializeSelectedParts(
            new[] { new DrawingElementKey(lowerSibling, DrawingElementKind.Stroke, 0) },
            0);
        var mappedUpperSibling = recursiveResult.Success
            ? recursiveResult.OldToNewObjectIndex[upperSibling]
            : -1;
        var afterRecursiveSplit = recursiveScene.HitTestElement(new PointF(50, 0), 0, 0);
        if (!recursiveResult.Success
            || !recursiveResult.Changed
            || !afterRecursiveSplit.IsValid
            || afterRecursiveSplit.Key.ObjectIndex != mappedUpperSibling)
        {
            throw new InvalidOperationException("Re-splitting a lower sibling changed same-order stroke priority.");
        }
    }

    private static void RunBatchElementMaterializationRegression()
    {
        var strokeScene = new VectorScene();
        strokeScene.CreateEmpty();
        var horizontal = AddTopologyLine(strokeScene, 0, new PointF(-200, 0), new PointF(200, 0));
        AddTopologyLine(strokeScene, 0, new PointF(0, -160), new PointF(0, 160));
        var left = AssertTopologyHit(strokeScene, new PointF(-100, 0), horizontal, DrawingElementKind.Stroke, 0, 0, 0.5f, "batch stroke left");
        var right = AssertTopologyHit(strokeScene, new PointF(100, 0), horizontal, DrawingElementKind.Stroke, 1, 0.5f, 1, "batch stroke right");
        var strokeOrder = strokeScene.ObjectOrder[horizontal];
        var strokeResult = strokeScene.MaterializeSelectedParts(new[] { left.Key, right.Key }, 0);
        if (!strokeResult.Success
            || !strokeResult.Changed
            || strokeResult.Parts.Length != 2
            || strokeResult.Parts.Select(part => part.Result.ObjectIndex).Distinct().Count() != 2
            || strokeResult.Parts.Any(part => part.Result.Kind != DrawingElementKind.Stroke))
        {
            throw new InvalidOperationException("Batch stroke materialization did not return two independent selected segments.");
        }

        AssertShapeCounts(strokeScene, 3, lines: 3, paths: 0, rectangles: 0, "batch stroke materialization");
        AssertLineSet(
            strokeScene,
            new[]
            {
                (new PointF(-200, 0), PointF.Empty),
                (PointF.Empty, new PointF(200, 0)),
                (new PointF(0, -160), new PointF(0, 160))
            },
            "batch stroke materialization");
        if (strokeResult.Parts.Any(part => strokeScene.ObjectOrder[part.Result.ObjectIndex] != strokeOrder))
        {
            throw new InvalidOperationException("Batch stroke materialization changed the source draw order.");
        }

        var multiHostScene = new VectorScene();
        multiHostScene.CreateEmpty();
        var upperStroke = AddTopologyLine(multiHostScene, 0, new PointF(-200, -40), new PointF(200, -40));
        var lowerStroke = AddTopologyLine(multiHostScene, 0, new PointF(-200, 40), new PointF(200, 40));
        var multiHostCutter = AddTopologyLine(multiHostScene, 0, new PointF(0, -160), new PointF(0, 160));
        var upperRight = AssertTopologyHit(multiHostScene, new PointF(100, -40), upperStroke, DrawingElementKind.Stroke, 1, 0.5f, 1, "batch multi-host upper");
        var lowerRight = AssertTopologyHit(multiHostScene, new PointF(100, 40), lowerStroke, DrawingElementKind.Stroke, 1, 0.5f, 1, "batch multi-host lower");
        var multiHostResult = multiHostScene.MaterializeSelectedParts(new[] { upperRight.Key, lowerRight.Key }, 0);
        if (!multiHostResult.Success
            || !multiHostResult.Changed
            || multiHostResult.Parts.Select(part => part.Result.ObjectIndex).Distinct().Count() != 2
            || multiHostResult.OldToNewObjectIndex[multiHostCutter] != 0)
        {
            throw new InvalidOperationException("Batch materialization across multiple hosts produced an invalid compacted index mapping.");
        }

        AssertShapeCounts(multiHostScene, 5, lines: 5, paths: 0, rectangles: 0, "batch multi-host materialization");
        AssertLineSet(
            multiHostScene,
            new[]
            {
                (new PointF(-200, -40), new PointF(0, -40)),
                (new PointF(0, -40), new PointF(200, -40)),
                (new PointF(-200, 40), new PointF(0, 40)),
                (new PointF(0, 40), new PointF(200, 40)),
                (new PointF(0, -160), new PointF(0, 160))
            },
            "batch multi-host materialization");

        var fillScene = new VectorScene();
        fillScene.CreateEmpty();
        var fill = AddTopologyFill(fillScene, 0, 0);
        AddTopologyLine(fillScene, 0, new PointF(-320, 0), new PointF(320, 0));
        var upper = AssertTopologyHit(fillScene, new PointF(0, -80), fill, DrawingElementKind.Fill, 0, 0, 1, "batch fill upper");
        var lower = AssertTopologyHit(fillScene, new PointF(0, 80), fill, DrawingElementKind.Fill, 1, 0, 1, "batch fill lower");
        var fillResult = fillScene.MaterializeSelectedParts(new[] { upper.Key, lower.Key }, 0);
        if (!fillResult.Success
            || !fillResult.Changed
            || fillResult.Parts.Length != 2
            || fillResult.Parts.Select(part => part.Result.ObjectIndex).Distinct().Count() != 2
            || fillResult.Parts.Any(part => part.Result.Kind != DrawingElementKind.Fill))
        {
            throw new InvalidOperationException("Batch fill materialization did not return two independent selected regions.");
        }

        AssertShapeCounts(fillScene, 3, lines: 1, paths: 2, rectangles: 0, "batch fill materialization");

        var curvedFillScene = new VectorScene();
        curvedFillScene.CreateEmpty();
        var curvedFill = curvedFillScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(400, 240),
            0,
            0,
            Color.Teal,
            Color.Transparent,
            16,
            ShapeKind.Ellipse);
        AddTopologyLine(curvedFillScene, 0, new PointF(-260, 0), new PointF(260, 0));
        var curvedUpper = curvedFillScene.HitTestElement(new PointF(0, -60), 0, toleranceWorld: 2);
        var curvedUpperPieces = curvedUpper.Key.Kind == DrawingElementKind.Fill
            ? curvedFillScene.GetExposedFillBezierSegmentPieces(
                curvedFill,
                0,
                curvedUpper.Key.PartIndex)
            : [];
        var curvedUpperPiece = curvedUpperPieces.FirstOrDefault(piece =>
            !VectorScene.IsStraightBezierSegment(
                piece.Start,
                piece.Control1,
                piece.Control2,
                piece.End));
        var curvedFillResult = curvedUpper.Key.Kind == DrawingElementKind.Fill
            ? curvedFillScene.MaterializeSelectedParts([curvedUpper.Key], 0)
            : new MaterializeSelectedPartsResult(false, false, [], []);
        var curvedFillTarget = curvedFillResult.Parts
            .Where(mapping => mapping.Source == curvedUpper.Key)
            .Select(mapping => mapping.Result.ObjectIndex)
            .FirstOrDefault(-1);
        if (curvedUpperPieces.Length == 0
            || curvedFillTarget < 0
            || !curvedFillResult.Success
            || !curvedFillResult.Changed
            || !curvedFillScene.TryResolveFillBezierSegmentPiece(
                curvedFillTarget,
                curvedUpperPiece,
                out var resolvedCurvedPiece,
                out _)
            || VectorScene.IsStraightBezierSegment(
                resolvedCurvedPiece.Start,
                resolvedCurvedPiece.Control1,
                resolvedCurvedPiece.Control2,
                resolvedCurvedPiece.End))
        {
            throw new InvalidOperationException(
                "Materializing a curved Fill part flattened its source cubic boundary.");
        }

        var curvedCutterScene = new VectorScene();
        curvedCutterScene.CreateEmpty();
        var curvedCutterFill = AddTopologyFill(curvedCutterScene, 0, 0);
        var curvedCutter = curvedCutterScene.AddCubicCurveSegment(
            0,
            new PointF(-320, 0),
            new PointF(-120, 100),
            new PointF(120, 100),
            new PointF(320, 0),
            VectorUnits.StrokePointsToUnits(2),
            Color.Transparent,
            Color.White,
            12);
        var curvedCutterHit = curvedCutterScene.HitTestElement(new PointF(0, 100), 0, toleranceWorld: 2);
        var curvedCutterResult = curvedCutterHit.Key.Kind == DrawingElementKind.Fill
            ? curvedCutterScene.MaterializeSelectedParts([curvedCutterHit.Key], 0)
            : new MaterializeSelectedPartsResult(false, false, [], []);
        var curvedCutterTarget = curvedCutterResult.Parts
            .Where(mapping => mapping.Source == curvedCutterHit.Key)
            .Select(mapping => mapping.Result.ObjectIndex)
            .FirstOrDefault(-1);
        var remappedCurvedCutter = curvedCutterResult.Success
            && (uint)curvedCutter < curvedCutterResult.OldToNewObjectIndex.Length
            ? curvedCutterResult.OldToNewObjectIndex[curvedCutter]
            : -1;
        var curvedCutterBoundary = curvedCutterTarget >= 0
            ? curvedCutterScene.GetEditableFillBezierSegmentParts(curvedCutterTarget)
            : [];
        var inheritedCutterParts = curvedCutterBoundary
            .Where(part => CurveEndpointsFollowCutter(part, remappedCurvedCutter))
            .ToArray();
        var inheritsExactCutterFormula = inheritedCutterParts.Length == 1
            && CutterControlsMatch(inheritedCutterParts[0], remappedCurvedCutter);
        if (!curvedCutterHit.IsValid
            || curvedCutterHit.Key.ObjectIndex != curvedCutterFill
            || curvedCutterHit.Key.Kind != DrawingElementKind.Fill
            || !curvedCutterResult.Success
            || !curvedCutterResult.Changed
            || curvedCutterTarget < 0
            || remappedCurvedCutter < 0
            || curvedCutterBoundary.Length > 6
            || !inheritsExactCutterFormula)
        {
            throw new InvalidOperationException(
                $"Materializing a Fill cut by a cubic Line sampled the cut boundary instead of preserving the Line formula: success={curvedCutterResult.Success}, changed={curvedCutterResult.Changed}, target={curvedCutterTarget}, cutter={remappedCurvedCutter}, parts={curvedCutterBoundary.Length}, inherited={inheritedCutterParts.Length}, exact={inheritsExactCutterFormula}.");
        }

        var sharedBoundaryScene = new VectorScene();
        sharedBoundaryScene.CreateEmpty();
        var sharedBoundaryFill = sharedBoundaryScene.AppendPathBezierObjectContours(
            0,
            [
                [
                    new PathBezierNode(new PointF(-697, -399), new PointF(-684, -399), new PointF(-980, -231)),
                    new PathBezierNode(new PointF(-998, 146), new PointF(-1057, -38), new PointF(-1392, 146)),
                    new PathBezierNode(new PointF(-2181, 146), new PointF(-1787, 146), new PointF(-2288, 68)),
                    new PathBezierNode(new PointF(-2429, -107), new PointF(-2373, -18), new PointF(-2055, -107)),
                    new PathBezierNode(new PointF(-1307, -107), new PointF(-1681, -107), new PointF(-1307, -327)),
                    new PathBezierNode(new PointF(-1307, -766), new PointF(-1307, -546), new PointF(-1091, -766)),
                    new PathBezierNode(new PointF(-659, -766), new PointF(-875, -766), new PointF(-659, -644)),
                    new PathBezierNode(new PointF(-659, -399), new PointF(-659, -521), new PointF(-672, -399))
                ]
            ],
            0,
            Color.Teal,
            Color.Transparent,
            6);
        sharedBoundaryScene.AddCubicCurveSegment(
            0,
            new PointF(-697, -399),
            new PointF(-190, -399),
            new PointF(318, -399),
            new PointF(825, -399),
            VectorUnits.StrokePointsToUnits(2),
            Color.Transparent,
            Color.White,
            6);
        sharedBoundaryScene.AddCubicCurveSegment(
            0,
            new PointF(825, -399),
            new PointF(825, -95),
            new PointF(825, 209),
            new PointF(825, 513),
            VectorUnits.StrokePointsToUnits(2),
            Color.Transparent,
            Color.White,
            6);
        sharedBoundaryScene.AddCubicCurveSegment(
            0,
            new PointF(825, 513),
            new PointF(318, 513),
            new PointF(-190, 513),
            new PointF(-697, 513),
            VectorUnits.StrokePointsToUnits(2),
            Color.Transparent,
            Color.White,
            6);
        sharedBoundaryScene.AddCubicCurveSegment(
            0,
            new PointF(-697, 513),
            new PointF(-1064, 244),
            new PointF(-1179, -112),
            new PointF(-697, -399),
            VectorUnits.StrokePointsToUnits(2),
            Color.Transparent,
            Color.White,
            6);
        var sharedBoundaryHit = sharedBoundaryScene.HitTestElement(new PointF(-1000, -600), 0, toleranceWorld: 2);
        var sharedBoundaryResult = sharedBoundaryHit.Key.Kind == DrawingElementKind.Fill
            ? sharedBoundaryScene.MaterializeSelectedParts([sharedBoundaryHit.Key], 0)
            : new MaterializeSelectedPartsResult(false, false, [], []);
        if (sharedBoundaryFill < 0
            || !sharedBoundaryHit.IsValid
            || sharedBoundaryHit.Key.ObjectIndex != sharedBoundaryFill
            || sharedBoundaryHit.Key.Kind != DrawingElementKind.Fill
            || !sharedBoundaryResult.Success
            || sharedBoundaryResult.Changed
            || sharedBoundaryScene.ObjectCount != 5)
        {
            throw new InvalidOperationException(
                $"A cubic cutter already shared by a Fill boundary generated false fragments when combined with connected curves: fill={sharedBoundaryFill}, hit={sharedBoundaryHit.Key}, success={sharedBoundaryResult.Success}, changed={sharedBoundaryResult.Changed}, objects={sharedBoundaryScene.ObjectCount}.");
        }

        var nearBoundaryScene = new VectorScene();
        nearBoundaryScene.CreateEmpty();
        var nearBoundaryFill = AddTopologyFill(nearBoundaryScene, 0, 0);
        AddTopologyLine(
            nearBoundaryScene,
            0,
            new PointF(-320, -119),
            new PointF(320, -119));
        var nearBoundaryParts = nearBoundaryScene.GetFillParts(nearBoundaryFill, 0);
        if (nearBoundaryParts.Length != 2)
        {
            throw new InvalidOperationException(
                $"A Line near but not coincident with a Fill boundary was discarded by overlap tolerance: parts={nearBoundaryParts.Length}.");
        }

        DrawingFillPartGeometry[] sharedBezierFillParts = [];
        FillBezierSegmentPiece[] sharedBezierBoundaryPieces = [];
        for (var iteration = 0; iteration < 8; iteration++)
        {
            sharedBezierFillParts = sharedBoundaryScene.GetFillParts(sharedBoundaryFill, 0);
            sharedBezierBoundaryPieces = sharedBoundaryScene.GetExposedFillBezierSegmentPieces(
                sharedBoundaryFill,
                0,
                includeCoincidentStrokes: true);
        }

        var expectedSharedFillPartCount = sharedBezierFillParts.Length;
        var expectedSharedBoundaryPieceCount = sharedBezierBoundaryPieces.Length;
        const int sharedBezierTopologyIterations = 128;
        var sharedBezierAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var sharedBezierWatch = Stopwatch.StartNew();
        for (var iteration = 0; iteration < sharedBezierTopologyIterations; iteration++)
        {
            sharedBezierFillParts = sharedBoundaryScene.GetFillParts(sharedBoundaryFill, 0);
            sharedBezierBoundaryPieces = sharedBoundaryScene.GetExposedFillBezierSegmentPieces(
                sharedBoundaryFill,
                0,
                includeCoincidentStrokes: true);
        }
        sharedBezierWatch.Stop();
        var sharedBezierAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - sharedBezierAllocatedBefore;
        var sharedBezierAllocatedBytesPerQuery = sharedBezierAllocatedBytes
            / (double)sharedBezierTopologyIterations;
        var sharedBezierAverageMilliseconds = sharedBezierWatch.Elapsed.TotalMilliseconds
            / sharedBezierTopologyIterations;
        if (sharedBezierFillParts.Length != expectedSharedFillPartCount
            || sharedBezierBoundaryPieces.Length != expectedSharedBoundaryPieceCount
            || sharedBezierAverageMilliseconds > 1)
        {
            throw new InvalidOperationException(
                $"Repeated shared Bezier topology queries changed geometry or exceeded 1 ms: fillParts={sharedBezierFillParts.Length}/{expectedSharedFillPartCount}, boundaryPieces={sharedBezierBoundaryPieces.Length}/{expectedSharedBoundaryPieceCount}, averageMs={sharedBezierAverageMilliseconds:0.000}.");
        }
        Console.WriteLine($"shared_bezier_topology_allocated_bytes_per_query={sharedBezierAllocatedBytesPerQuery:0.0}");
        Console.WriteLine($"shared_bezier_topology_avg_ms={sharedBezierAverageMilliseconds:0.000}");

        var compoundScene = new VectorScene();
        compoundScene.CreateEmpty();
        var outlined = AddTopologyFill(compoundScene, 0, VectorUnits.StrokePointsToUnits(2));
        AddTopologyLine(compoundScene, 0, new PointF(-320, 0), new PointF(320, 0));
        var boundary = AssertTopologyHit(compoundScene, new PointF(0, -120), outlined, DrawingElementKind.BoundaryStroke, 0, 0, 0.25f, "batch boundary top");
        var fillPart = AssertTopologyHit(compoundScene, new PointF(0, 80), outlined, DrawingElementKind.Fill, 1, 0, 1, "batch outlined fill lower");
        var outlinedOrder = compoundScene.ObjectOrder[outlined];
        var compoundResult = compoundScene.MaterializeSelectedParts(new[] { boundary.Key, fillPart.Key }, 0);
        if (!compoundResult.Success
            || !compoundResult.Changed
            || compoundResult.Parts.Length != 2
            || compoundResult.Parts.Count(part => part.Result.Kind == DrawingElementKind.Fill) != 1
            || compoundResult.Parts.Count(part => part.Result.Kind == DrawingElementKind.Stroke) != 1)
        {
            throw new InvalidOperationException("Batch Fill and Boundary materialization did not preserve both selected element types.");
        }

        AssertShapeCounts(compoundScene, 9, lines: 7, paths: 2, rectangles: 0, "batch Fill and Boundary materialization");
        if (Enumerable.Range(0, compoundScene.ObjectCount).Count(index => compoundScene.ObjectOrder[index] == outlinedOrder) != 8)
        {
            throw new InvalidOperationException("Batch Fill and Boundary materialization duplicated or lost boundary parts.");
        }

        var preservedHostScene = new VectorScene();
        preservedHostScene.CreateEmpty();
        var outlinedEllipse = preservedHostScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(200, 120),
            0,
            VectorUnits.StrokePointsToUnits(2),
            Color.Teal,
            Color.White,
            12,
            ShapeKind.Ellipse);
        var preservedOrder = preservedHostScene.ObjectOrder[outlinedEllipse];
        var preservedSubOrder = preservedHostScene.ObjectSubOrder[outlinedEllipse];
        var ellipseBoundary = preservedHostScene.HitTestElement(new PointF(0, -60), 0, 0);
        var preservedHostResult = preservedHostScene.MaterializeSelectedParts(new[] { ellipseBoundary.Key }, 0);
        var preservedHost = preservedHostResult.Success
            ? preservedHostResult.OldToNewObjectIndex[outlinedEllipse]
            : -1;
        if (!ellipseBoundary.IsValid
            || ellipseBoundary.Key.Kind != DrawingElementKind.BoundaryStroke
            || !preservedHostResult.Success
            || !preservedHostResult.Changed
            || preservedHost < 0
            || preservedHostScene.ObjectOrder[preservedHost] != preservedOrder
            || !preservedHostScene.ObjectSubOrder[preservedHost].Equals(preservedSubOrder)
            || Enumerable.Range(0, preservedHostScene.ObjectCount)
                .GroupBy(index => (preservedHostScene.ObjectOrder[index], preservedHostScene.ObjectSubOrder[index]))
                .Any(group => group.Count() > 1))
        {
            throw new InvalidOperationException("Boundary-only materialization reused the preserved Fill drawing stack key.");
        }

        var staleScene = new VectorScene();
        staleScene.CreateEmpty();
        var staleFill = AddTopologyFill(staleScene, 0, 0);
        AddTopologyLine(staleScene, 0, new PointF(-320, 0), new PointF(320, 0));
        var staleCount = staleScene.ObjectCount;
        var staleResult = staleScene.MaterializeSelectedParts(
            new[] { new DrawingElementKey(staleFill, DrawingElementKind.Fill, 99) },
            0);
        if (staleResult.Success || staleScene.ObjectCount != staleCount || staleScene.ShapeKind[staleFill] != ShapeKind.Rectangle)
        {
            throw new InvalidOperationException("A stale selected PartIndex partially modified the scene.");
        }

        bool CurveEndpointsFollowCutter(PathBezierSegmentPart part, int cutter)
        {
            return cutter >= 0
                && curvedCutterScene.TryGetClosestPointOnLine(
                    cutter,
                    part.Start,
                    out _,
                    out _,
                    out var startDistance)
                && curvedCutterScene.TryGetClosestPointOnLine(
                    cutter,
                    part.End,
                    out _,
                    out _,
                    out var endDistance)
                && Math.Max(startDistance, endDistance) <= 2f;
        }

        bool CutterControlsMatch(PathBezierSegmentPart part, int cutter)
        {
            if (!curvedCutterScene.TryGetClosestPointOnLine(
                    cutter,
                    part.Start,
                    out var startT,
                    out _,
                    out var startDistance)
                || !curvedCutterScene.TryGetClosestPointOnLine(
                    cutter,
                    part.End,
                    out var endT,
                    out _,
                    out var endDistance)
                || Math.Max(startDistance, endDistance) > 2f
                || !curvedCutterScene.TryGetLineBezierPart(
                    cutter,
                    Math.Min(startT, endT),
                    Math.Max(startT, endT),
                    out var expectedStart,
                    out var expectedControl1,
                    out var expectedControl2,
                    out var expectedEnd))
            {
                return false;
            }

            if (startT > endT)
            {
                (expectedStart, expectedEnd) = (expectedEnd, expectedStart);
                (expectedControl1, expectedControl2) = (expectedControl2, expectedControl1);
            }

            expectedControl1 = new PointF(
                expectedControl1.X + part.Start.X - expectedStart.X,
                expectedControl1.Y + part.Start.Y - expectedStart.Y);
            expectedControl2 = new PointF(
                expectedControl2.X + part.End.X - expectedEnd.X,
                expectedControl2.Y + part.End.Y - expectedEnd.Y);
            return PointsWithin(part.Control1, expectedControl1, 1.5f)
                && PointsWithin(part.Control2, expectedControl2, 1.5f)
                && !VectorScene.IsStraightBezierSegment(
                    part.Start,
                    part.Control1,
                    part.Control2,
                    part.End);
        }
    }

    private static void RunOutlinedFillMergeRegression()
    {
        var nearbyScene = new VectorScene();
        nearbyScene.CreateEmpty();
        nearbyScene.AddObject(
            0,
            new PointF(-54, 0),
            new SizeF(100, 80),
            0,
            0,
            Color.Teal,
            Color.Transparent,
            12,
            ShapeKind.Rectangle);
        var nearbySecond = nearbyScene.AddObject(
            0,
            new PointF(54, 0),
            new SizeF(100, 80),
            0,
            0,
            Color.Teal,
            Color.Transparent,
            12,
            ShapeKind.Rectangle);
        var nearbyMerged = nearbyScene.MergeSameColorFillsAround(nearbySecond, connectNearby: true, frame: 0);
        if (nearbyScene.ObjectCount != 1
            || (uint)nearbyMerged >= nearbyScene.ObjectCount
            || !nearbyScene.FillContainsPoint(nearbyMerged, new PointF(-54, 0))
            || !nearbyScene.FillContainsPoint(nearbyMerged, new PointF(54, 0))
            || nearbyScene.FillContainsPoint(nearbyMerged, PointF.Empty))
        {
            throw new InvalidOperationException("Nearby same-color fills did not merge as separate islands or invented paint in their transparent gap.");
        }

        var translatedScene = new VectorScene();
        translatedScene.CreateEmpty();
        translatedScene.AddObject(
            0,
            new PointF(-54, 0),
            new SizeF(100, 80),
            0,
            0,
            Color.Teal,
            Color.Transparent,
            12,
            ShapeKind.Rectangle);
        var translatedSecond = translatedScene.AddObject(
            0,
            new PointF(54, 0),
            new SizeF(100, 80),
            0,
            0,
            Color.Teal,
            Color.Transparent,
            12,
            ShapeKind.Rectangle);
        translatedScene.MergeSameColorFillsAround(translatedSecond, connectNearby: false, frame: 0);
        if (translatedScene.ObjectCount != 2)
        {
            throw new InvalidOperationException("The translated-fill exception merged shapes that were separated by 8 vu.");
        }

        var scene = new VectorScene();
        scene.CreateEmpty();
        var outlined = AddTopologyFill(scene, 0, VectorUnits.StrokePointsToUnits(2));
        var outlinedOrder = scene.ObjectOrder[outlined];
        var overlappingFill = scene.AddObject(
            0,
            new PointF(160, 0),
            new SizeF(160, 100),
            0,
            0,
            Color.Teal,
            Color.Transparent,
            12,
            ShapeKind.Rectangle);
        var merged = scene.MergeSameColorFillsAround(overlappingFill, frame: 0);
        AssertShapeCounts(scene, 5, lines: 4, paths: 1, rectangles: 0, "outlined fill merge");
        if ((uint)merged >= scene.ObjectCount
            || scene.ShapeKind[merged] != ShapeKind.Path
            || scene.Stroke[merged] != 0
            || !scene.FillContainsPoint(merged, new PointF(-150, 0))
            || !scene.FillContainsPoint(merged, new PointF(220, 0)))
        {
            throw new InvalidOperationException("Merging an outlined fill did not preserve the expected union geometry.");
        }

        AssertLineSet(
            scene,
            new[]
            {
                (new PointF(-200, -120), new PointF(200, -120)),
                (new PointF(200, -120), new PointF(200, 120)),
                (new PointF(200, 120), new PointF(-200, 120)),
                (new PointF(-200, 120), new PointF(-200, -120))
            },
            "outlined fill merge");
        if (Enumerable.Range(0, scene.ObjectCount)
            .Where(index => scene.ShapeKind[index] == ShapeKind.Line)
            .Any(index => scene.ObjectOrder[index] != outlinedOrder))
        {
            throw new InvalidOperationException("Preserved fill boundaries did not inherit their original draw order.");
        }

        var preservedHostScene = new VectorScene();
        preservedHostScene.CreateEmpty();
        preservedHostScene.AddObject(
            0,
            new PointF(80, 0),
            new SizeF(160, 100),
            0,
            0,
            Color.Teal,
            Color.Transparent,
            12,
            ShapeKind.Rectangle);
        var topOutlinedEllipse = preservedHostScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(200, 120),
            0,
            VectorUnits.StrokePointsToUnits(2),
            Color.Teal,
            Color.White,
            12,
            ShapeKind.Ellipse);
        var preservedOrder = preservedHostScene.ObjectOrder[topOutlinedEllipse];
        var preservedSubOrder = preservedHostScene.ObjectSubOrder[topOutlinedEllipse];
        var preservedMerge = preservedHostScene.MergeSameColorFillsAround(topOutlinedEllipse, frame: 0);
        if ((uint)preservedMerge >= preservedHostScene.ObjectCount
            || preservedHostScene.ObjectOrder[preservedMerge] != preservedOrder
            || !preservedHostScene.ObjectSubOrder[preservedMerge].Equals(preservedSubOrder)
            || Enumerable.Range(0, preservedHostScene.ObjectCount)
                .GroupBy(index => (preservedHostScene.ObjectOrder[index], preservedHostScene.ObjectSubOrder[index]))
                .Any(group => group.Count() > 1))
        {
            throw new InvalidOperationException("Outlined Fill merge reused the preserved merged Fill drawing stack key.");
        }
    }

    private static void RunFillBoundaryOverlapNormalizationRegression()
    {
        static PathBezierNode[] CircleContour(float centerX, float radius)
        {
            var handle = radius * 0.55228475f;
            return
            [
                new PathBezierNode(
                    new PointF(centerX + radius, 0),
                    new PointF(centerX + radius, -handle),
                    new PointF(centerX + radius, handle)),
                new PathBezierNode(
                    new PointF(centerX, radius),
                    new PointF(centerX + handle, radius),
                    new PointF(centerX - handle, radius)),
                new PathBezierNode(
                    new PointF(centerX - radius, 0),
                    new PointF(centerX - radius, handle),
                    new PointF(centerX - radius, -handle)),
                new PathBezierNode(
                    new PointF(centerX, -radius),
                    new PointF(centerX - handle, -radius),
                    new PointF(centerX + handle, -radius))
            ];
        }

        var scene = new VectorScene();
        scene.CreateEmpty();
        var fill = scene.AddPathObjectContours(
            0,
            [
                [
                    new PointF(-240, -120),
                    new PointF(0, -120),
                    new PointF(0, 120),
                    new PointF(-240, 120)
                ],
                [
                    new PointF(-180, -50),
                    new PointF(-100, -50),
                    new PointF(-100, 50),
                    new PointF(-180, 50)
                ],
                [
                    new PointF(40, -80),
                    new PointF(240, -80),
                    new PointF(240, 80),
                    new PointF(40, 80)
                ]
            ],
            0,
            Color.Teal,
            Color.Transparent,
            12);
        if (fill < 0 || !scene.TryConvertFillToBezierPath(fill))
        {
            throw new InvalidOperationException("The Fill overlap regression could not create editable compound geometry.");
        }

        var referenceContours = scene.GetObjectBoundaryContours(fill);
        var movedStart = new PointF(-40, 80);
        var movedEnd = new PointF(-40, -80);
        if (!scene.SetPathBezierSegment(
                fill,
                7,
                movedStart,
                new PointF(-40, 80 - 160f / 3f),
                new PointF(-40, -80 + 160f / 3f),
                movedEnd,
                preserveStraightAdjacentSegments: true)
            || scene.FillContainsPoint(fill, new PointF(-20, 0))
            || !scene.NormalizeFillBoundaryOverlaps(fill, referenceContours)
            || scene.ObjectCount != 1
            || !scene.FillContainsPoint(fill, new PointF(-20, 0))
            || scene.FillContainsPoint(fill, new PointF(-140, 0))
            || !scene.FillContainsPoint(fill, new PointF(120, 0))
            || !scene.TryGetPathBezierWorldContours(fill, out var normalizedLinearContours)
            || normalizedLinearContours.Sum(contour => contour.Length) > 16)
        {
            throw new InvalidOperationException("Overlapping solid Fill contours were not united while preserving the existing hole.");
        }

        var holeScene = new VectorScene();
        holeScene.CreateEmpty();
        var holedFill = holeScene.AddPathObjectContours(
            0,
            [
                [
                    new PointF(-160, -100),
                    new PointF(160, -100),
                    new PointF(160, 100),
                    new PointF(-160, 100)
                ],
                [
                    new PointF(-50, -40),
                    new PointF(50, -40),
                    new PointF(50, 40),
                    new PointF(-50, 40)
                ]
            ],
            0,
            Color.Teal,
            Color.Transparent,
            8);
        holeScene.TryConvertFillToBezierPath(holedFill);
        var holeReference = holeScene.GetObjectBoundaryContours(holedFill);
        if (holeScene.NormalizeFillBoundaryOverlaps(holedFill, holeReference)
            || !holeScene.TryGetPathBezierWorldContours(holedFill, out _)
            || holeScene.FillContainsPoint(holedFill, PointF.Empty))
        {
            throw new InvalidOperationException("A valid compound Fill hole was normalized or flattened without an overlap.");
        }

        var curvedScene = new VectorScene();
        curvedScene.CreateEmpty();
        var leftCircle = CircleContour(-60, 120);
        var rightCircle = CircleContour(60, 120);
        var curvedFill = curvedScene.AppendPathBezierObjectContours(
            0,
            [leftCircle, rightCircle],
            0,
            Color.Teal,
            Color.Transparent,
            16);
        curvedScene.CompleteDeferredBuild();
        var curvedReference = curvedScene.GetObjectBoundaryContours(curvedFill);
        if (curvedFill < 0
            || curvedScene.FillContainsPoint(curvedFill, PointF.Empty)
            || !curvedScene.NormalizeFillBoundaryOverlaps(curvedFill, curvedReference)
            || !curvedScene.FillContainsPoint(curvedFill, PointF.Empty)
            || !curvedScene.FillContainsPoint(curvedFill, new PointF(-150, 0))
            || !curvedScene.FillContainsPoint(curvedFill, new PointF(150, 0))
            || !curvedScene.TryGetPathBezierWorldContours(curvedFill, out var normalizedCurves)
            || normalizedCurves.Sum(contour => contour.Length) > 12)
        {
            throw new InvalidOperationException(
                $"Curved overlap normalization flattened exact cubics or retained sampled nodes: nodes={(curvedScene.TryGetPathBezierWorldContours(curvedFill, out var failedCurves) ? failedCurves.Sum(contour => contour.Length) : -1)}.");
        }

        var expectedHandle = 120 * 0.55228475f;
        var preservedOuterNode = normalizedCurves
            .SelectMany(contour => contour)
            .FirstOrDefault(node => PointsWithin(node.Anchor, new PointF(180, 0), 1f));
        var controlsPreserved = PointsWithin(
                preservedOuterNode.IncomingControl,
                new PointF(180, -expectedHandle),
                1f)
            && PointsWithin(
                preservedOuterNode.OutgoingControl,
                new PointF(180, expectedHandle),
                1f)
            || PointsWithin(
                preservedOuterNode.IncomingControl,
                new PointF(180, expectedHandle),
                1f)
            && PointsWithin(
                preservedOuterNode.OutgoingControl,
                new PointF(180, -expectedHandle),
                1f);
        var normalizedNodeCount = normalizedCurves.Sum(contour => contour.Length);
        if (!controlsPreserved
            || curvedScene.NormalizeFillBoundaryOverlaps(curvedFill, curvedReference)
            || !curvedScene.TryGetPathBezierWorldContours(curvedFill, out var idempotentCurves)
            || idempotentCurves.Sum(contour => contour.Length) != normalizedNodeCount)
        {
            throw new InvalidOperationException(
                "Curved overlap normalization did not preserve an untouched cubic or was not idempotent.");
        }

        var curvedMergeScene = new VectorScene();
        curvedMergeScene.CreateEmpty();
        curvedMergeScene.AppendPathBezierObjectContours(
            0,
            [leftCircle],
            0,
            Color.Teal,
            Color.Transparent,
            8);
        var curvedMergeSource = curvedMergeScene.AppendPathBezierObjectContours(
            0,
            [rightCircle],
            0,
            Color.Teal,
            Color.Transparent,
            8);
        curvedMergeScene.CompleteDeferredBuild();
        var curvedMerged = curvedMergeScene.MergeSameColorFillsAround(
            curvedMergeSource,
            connectNearby: false,
            frame: 0);
        if (curvedMergeScene.ObjectCount != 1
            || (uint)curvedMerged >= curvedMergeScene.ObjectCount
            || !curvedMergeScene.FillContainsPoint(curvedMerged, PointF.Empty)
            || !curvedMergeScene.TryGetPathBezierWorldContours(curvedMerged, out var mergedCurves)
            || mergedCurves.Sum(contour => contour.Length) > 12)
        {
            throw new InvalidOperationException(
                "Same-color curved Fill merging flattened exact cubics into sampled path nodes.");
        }
    }

    private static void RunMarqueeElementQueryRegression()
    {
        var partialScene = new VectorScene();
        partialScene.CreateEmpty();
        AddTopologyFill(partialScene, 0, 0);
        var partialHits = partialScene.QueryDrawingElementsInsideBounds(new RectangleF(-40, -40, 80, 80), 0);
        if (partialHits.Length != 0)
        {
            throw new InvalidOperationException("A marquee contained inside an unsplit fill selected the whole fill region.");
        }

        var splitFillScene = new VectorScene();
        splitFillScene.CreateEmpty();
        var splitFill = AddTopologyFill(splitFillScene, 0, 0);
        AddTopologyLine(splitFillScene, 0, new PointF(-320, 0), new PointF(320, 0));
        var upperHits = splitFillScene.QueryDrawingElementsInsideBounds(new RectangleF(-210, -130, 420, 140), 0);
        if (upperHits.Count(hit => hit.Key.ObjectIndex == splitFill && hit.Key.Kind == DrawingElementKind.Fill) != 1
            || upperHits.Any(hit => hit.Key.ObjectIndex == splitFill && hit.Key.Kind == DrawingElementKind.Fill && hit.Key.PartIndex != 0))
        {
            throw new InvalidOperationException("Geometry-driven marquee selection did not isolate the fully enclosed fill region.");
        }

        var strokeScene = new VectorScene();
        strokeScene.CreateEmpty();
        var horizontal = AddTopologyLine(strokeScene, 0, new PointF(-200, 0), new PointF(200, 0));
        AddTopologyLine(strokeScene, 0, new PointF(0, -160), new PointF(0, 160));
        var strokeHits = strokeScene.QueryDrawingElementsInsideBounds(new RectangleF(-5, -10, 210, 20), 0);
        if (strokeHits.Count(hit => hit.Key.ObjectIndex == horizontal && hit.Key.Kind == DrawingElementKind.Stroke) != 1
            || strokeHits.Any(hit => hit.Key.ObjectIndex == horizontal && hit.Key.PartIndex != 1))
        {
            throw new InvalidOperationException("Geometry-driven marquee selection missed a fully enclosed thin stroke segment.");
        }

        var curvedScene = new VectorScene();
        curvedScene.CreateEmpty();
        var curvedStroke = curvedScene.AddCurveSegment(
            0,
            new PointF(-80, 0),
            new PointF(0, 120),
            new PointF(80, 0),
            VectorUnits.StrokePointsToUnits(2),
            Color.Transparent,
            Color.White,
            6);
        var curvedBounds = new RectangleF(-100, -100, 200, 200);
        var curvedHits = curvedScene.QueryDrawingElementsInsideBounds(curvedBounds, 0);
        if (!curvedScene.IsObjectGeometryInsideBounds(curvedStroke, curvedBounds)
            || curvedHits.Count(hit => hit.Key.ObjectIndex == curvedStroke && hit.Key.Kind == DrawingElementKind.Stroke) != 1)
        {
            throw new InvalidOperationException("Geometry-driven marquee selection rejected a contained curve because its control point was outside the marquee.");
        }
    }

    private static void RunMarqueeLineMaterializationRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        var source = AddTopologyLine(scene, 0, new PointF(-200, 0), new PointF(200, 0));
        var sourceOrder = scene.ObjectOrder[source];
        var bounds = new RectangleF(-50, -20, 100, 40);
        var materialized = scene.MaterializeMarqueeLineParts(bounds, 0);

        if (!materialized.Changed
            || materialized.SelectedObjects.Length != 1
            || !LineMatches(scene, materialized.SelectedObjects[0], new PointF(-50, 0), new PointF(50, 0)))
        {
            var selectedDescription = string.Join(
                "; ",
                materialized.SelectedObjects.Select(index =>
                {
                    scene.TryGetLineEndpoint(index, true, out var start);
                    scene.TryGetLineEndpoint(index, false, out var end);
                    return $"{index}:{start}->{end}";
                }));
            throw new InvalidOperationException(
                $"Marquee line materialization did not isolate the selected interior segment: "
                + $"changed={materialized.Changed}, selected={selectedDescription}, objects={scene.ObjectCount}, "
                + $"candidates={scene.QueryObjects(bounds, 0).Length}, straight={scene.IsLineStraight(source)}, "
                + $"controls={scene.CurveControlX[source]},{scene.CurveControlY[source]}"
                + $"/{scene.CurveControl2X[source]},{scene.CurveControl2Y[source]}.");
        }

        AssertLineSet(
            scene,
            new[]
            {
                (new PointF(-200, 0), new PointF(-50, 0)),
                (new PointF(-50, 0), new PointF(50, 0)),
                (new PointF(50, 0), new PointF(200, 0))
            },
            "marquee line materialization");

        var replacementLines = Enumerable.Range(0, scene.ObjectCount)
            .Where(index => scene.ShapeKind[index] == ShapeKind.Line)
            .ToArray();
        if (replacementLines.Length != 3
            || replacementLines.Any(index => scene.ObjectOrder[index] != sourceOrder)
            || replacementLines.Select(index => scene.ObjectSubOrder[index]).Distinct().Count() != 3
            || replacementLines.Any(index => scene.ObjectKeyframeFrame[index] != 0))
        {
            throw new InvalidOperationException("Marquee line materialization did not preserve drawing order or keyframe ownership.");
        }
    }

    private static void RunMarqueeFillMaterializationRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        scene.AddObject(0, PointF.Empty, new SizeF(240, 160), 0, 0, Color.Teal, Color.Transparent, 12, ShapeKind.Rectangle);
        var materialized = scene.MaterializeMarqueeFillParts(new RectangleF(-40, -30, 80, 60), 0);
        var selected = materialized.SelectedObjects.SingleOrDefault(-1);
        var outside = Enumerable.Range(0, scene.ObjectCount).FirstOrDefault(index => index != selected, -1);
        if (!materialized.Changed
            || materialized.SelectedObjects.Length != 1
            || (uint)selected >= scene.ObjectCount
            || (uint)outside >= scene.ObjectCount
            || scene.ObjectCount != 2
            || scene.ShapeKind[selected] != ShapeKind.Path
            || scene.ShapeKind[outside] != ShapeKind.Path
            || !scene.FillContainsPoint(selected, PointF.Empty)
            || scene.FillContainsPoint(selected, new PointF(80, 0))
            || scene.FillContainsPoint(outside, PointF.Empty)
            || !scene.FillContainsPoint(outside, new PointF(80, 0)))
        {
            throw new InvalidOperationException("Marquee fill materialization did not preserve the outside region as one compound path with a marquee hole.");
        }

        var gradientScene = new VectorScene();
        gradientScene.CreateEmpty();
        var gradientSource = gradientScene.AddObject(0, PointF.Empty, new SizeF(240, 160), 0, 0, Color.Teal, Color.Transparent, 12, ShapeKind.Rectangle);
        var gradientStops = new[]
        {
            new GradientStop(0, Color.Teal),
            new GradientStop(0.4f, Color.Gold),
            new GradientStop(1, Color.MediumPurple)
        };
        var gradientStart = new PointF(0, 0);
        var gradientEnd = new PointF(120, 0);
        gradientScene.SetGradientPaint(gradientSource, GradientKind.Radial, gradientStops, gradientStart, gradientEnd);
        var gradientMaterialization = gradientScene.MaterializeMarqueeFillParts(new RectangleF(-40, -30, 80, 60), 0);
        var selectedGradient = gradientMaterialization.SelectedObjects.SingleOrDefault(-1);
        var outsideGradient = Enumerable.Range(0, gradientScene.ObjectCount).FirstOrDefault(index => index != selectedGradient, -1);
        if (!gradientMaterialization.Changed
            || gradientScene.ObjectCount != 2
            || (uint)selectedGradient >= gradientScene.ObjectCount
            || (uint)outsideGradient >= gradientScene.ObjectCount
            || !gradientScene.HasGradient(selectedGradient)
            || !gradientScene.HasGradient(outsideGradient)
            || gradientScene.GetGradientKind(selectedGradient) != GradientKind.Radial
            || gradientScene.GetGradientKind(outsideGradient) != GradientKind.Radial
            || !gradientScene.GetGradientStops(selectedGradient).SequenceEqual(gradientStops)
            || !gradientScene.GetGradientStops(outsideGradient).SequenceEqual(gradientStops)
            || gradientScene.GetGradientStart(selectedGradient) != gradientStart
            || gradientScene.GetGradientEnd(outsideGradient) != gradientEnd)
        {
            throw new InvalidOperationException("Marquee fill materialization discarded the gradient material of an extracted fill.");
        }

        var curvedScene = new VectorScene();
        curvedScene.CreateEmpty();
        var curvedFill = curvedScene.AddPathObject(
            0,
            [
                new PointF(-200, -80),
                new PointF(200, -80),
                new PointF(200, 160),
                new PointF(-200, 160)
            ],
            0,
            Color.MediumAquamarine,
            Color.Transparent,
            12);
        if (!curvedScene.TryConvertFillToBezierPath(curvedFill)
            || !curvedScene.TryGetPathBezierSegment(curvedFill, 0, out var curvedTop)
            || !curvedScene.SetPathBezierSegment(
                curvedFill,
                curvedTop.PartIndex,
                curvedTop.Start,
                new PointF(-80, -190),
                new PointF(80, -40),
                curvedTop.End))
        {
            throw new InvalidOperationException("The curved marquee Fill regression could not prepare an exact cubic edge.");
        }

        var curvedMarqueeBounds = new RectangleF(-70, -220, 140, 260);
        var curvedMaterialization = curvedScene.MaterializeMarqueeFillParts(curvedMarqueeBounds, 0);
        var selectedCurveFill = curvedMaterialization.SelectedObjects.SingleOrDefault(-1);
        var outsideCurveFill = Enumerable.Range(0, curvedScene.ObjectCount)
            .FirstOrDefault(index => index != selectedCurveFill, -1);
        var selectedCurveParts = (uint)selectedCurveFill < curvedScene.ObjectCount
            ? curvedScene.GetEditableFillBezierSegmentParts(selectedCurveFill)
            : [];
        var activeCurvePart = selectedCurveParts.FirstOrDefault();
        var activeLinearControl1 = new PointF(
            activeCurvePart.Start.X + (activeCurvePart.End.X - activeCurvePart.Start.X) / 3f,
            activeCurvePart.Start.Y + (activeCurvePart.End.Y - activeCurvePart.Start.Y) / 3f);
        var activeLinearControl2 = new PointF(
            activeCurvePart.Start.X + (activeCurvePart.End.X - activeCurvePart.Start.X) * 2f / 3f,
            activeCurvePart.Start.Y + (activeCurvePart.End.Y - activeCurvePart.Start.Y) * 2f / 3f);
        var activeCurveDeviation = selectedCurveParts.Length == 0
            ? 0
            : Math.Max(
                Math.Abs(activeCurvePart.Control1.X - activeLinearControl1.X)
                    + Math.Abs(activeCurvePart.Control1.Y - activeLinearControl1.Y),
                Math.Abs(activeCurvePart.Control2.X - activeLinearControl2.X)
                    + Math.Abs(activeCurvePart.Control2.Y - activeLinearControl2.Y));
        if (!curvedMaterialization.Changed
            || curvedMaterialization.SelectedObjects.Length != 1
            || curvedScene.ObjectCount != 2
            || (uint)selectedCurveFill >= curvedScene.ObjectCount
            || (uint)outsideCurveFill >= curvedScene.ObjectCount
            || !curvedScene.TryGetPathBezierWorldContours(selectedCurveFill, out _)
            || !curvedScene.TryGetPathBezierWorldContours(outsideCurveFill, out _)
            || selectedCurveParts.Length != 4
            || activeCurveDeviation <= 1
            || activeCurvePart.Start.X < curvedMarqueeBounds.Left - 2
            || activeCurvePart.End.X > curvedMarqueeBounds.Right + 2)
        {
            throw new InvalidOperationException(
                "Marquee Fill materialization flattened or failed to prioritize the selected cubic subcurve.");
        }

        var adjustedControl = new PointF(activeCurvePart.Control1.X, activeCurvePart.Control1.Y - 24);
        if (!curvedScene.SetPathBezierSegment(
                selectedCurveFill,
                activeCurvePart.PartIndex,
                activeCurvePart.Start,
                adjustedControl,
                activeCurvePart.Control2,
                activeCurvePart.End)
            || !curvedScene.TryGetPathBezierSegment(selectedCurveFill, activeCurvePart.PartIndex, out var adjustedCurvePart)
            || !PointsNear(adjustedCurvePart.Control1, VectorUnits.Quantize(adjustedControl)))
        {
            throw new InvalidOperationException("A marquee-selected cubic Fill subcurve was not directly editable.");
        }

        using (var curvedStage = new StageControl(curvedScene) { Size = new Size(640, 420) })
        {
            var overlayParts = curvedScene.GetEditableFillBezierSegmentParts(selectedCurveFill);
            curvedStage.RestoreViewState(curvedStage.CaptureViewState() with { Zoom = 32f });
            curvedStage.SetSelection([selectedCurveFill], selectedCurveFill);
            curvedStage.SetFillEdgeBezierOverlay(
                selectedCurveFill,
                overlayParts.Select(part => new FillEdgeBezierOverlaySegment(
                    part.PartIndex,
                    part.Start,
                    part.Control1,
                    part.Control2,
                    part.End)).ToArray(),
                overlayParts[0].PartIndex);
            var firstControlHit = curvedStage.HitTestFillEdgeBezierOverlay(
                Point.Round(curvedStage.WorldToScreen(overlayParts[0].Control1.X, overlayParts[0].Control1.Y)));
            var secondControlHit = curvedStage.HitTestFillEdgeBezierOverlay(
                Point.Round(curvedStage.WorldToScreen(overlayParts[0].Control2.X, overlayParts[0].Control2.Y)));
            if (firstControlHit.Handle != EditHandleKind.BezierControl
                || secondControlHit.Handle != EditHandleKind.BezierControl2)
            {
                throw new InvalidOperationException("A marquee-selected cubic Fill subcurve did not expose two distinct Stage controls.");
            }
        }

        var outlinedCurveScene = new VectorScene();
        outlinedCurveScene.CreateEmpty();
        var outlinedCurveFill = outlinedCurveScene.AddPathObject(
            0,
            [
                new PointF(-200, -80),
                new PointF(200, -80),
                new PointF(200, 160),
                new PointF(-200, 160)
            ],
            VectorUnits.StrokePointsToUnits(2),
            Color.MediumAquamarine,
            Color.White,
            12);
        if (!outlinedCurveScene.TryConvertFillToBezierPath(outlinedCurveFill)
            || !outlinedCurveScene.TryGetPathBezierSegment(outlinedCurveFill, 0, out var outlinedTop)
            || !outlinedCurveScene.SetPathBezierSegment(
                outlinedCurveFill,
                outlinedTop.PartIndex,
                outlinedTop.Start,
                new PointF(-80, -190),
                new PointF(80, -40),
                outlinedTop.End))
        {
            throw new InvalidOperationException("The outlined marquee regression could not prepare an exact cubic edge.");
        }

        var outlinedMaterialization = outlinedCurveScene.MaterializeMarqueeSelectionParts(curvedMarqueeBounds, 0);
        var selectedOutlineCurves = outlinedMaterialization.SelectedObjects
            .Where(index => (uint)index < outlinedCurveScene.ObjectCount
                && outlinedCurveScene.ShapeKind[index] == ShapeKind.Line)
            .ToArray();
        var retainedFreehandOutlines = Enumerable.Range(0, outlinedCurveScene.ObjectCount)
            .Any(index => outlinedCurveScene.ShapeKind[index] == ShapeKind.Freeform
                && outlinedCurveScene.Stroke[index] > 0);
        var selectedOutlineIsCurved = selectedOutlineCurves.Any(index =>
            outlinedCurveScene.TryGetLineBezierPart(
                index,
                0,
                1,
                out var start,
                out var control1,
                out var control2,
                out var end)
            && (Math.Abs(control1.Y - (start.Y + (end.Y - start.Y) / 3f)) > 1
                || Math.Abs(control2.Y - (start.Y + (end.Y - start.Y) * 2f / 3f)) > 1));
        if (!outlinedMaterialization.Changed
            || selectedOutlineCurves.Length == 0
            || retainedFreehandOutlines
            || !selectedOutlineIsCurved)
        {
            throw new InvalidOperationException("Marquee materialization flattened an outlined Path cubic subcurve.");
        }
    }

    private static void RunMovedFillIsolationRegression()
    {
        var mergeScene = new VectorScene();
        mergeScene.CreateEmpty();
        mergeScene.AddObject(
            0,
            new PointF(-54, 0),
            new SizeF(100, 80),
            0,
            0,
            Color.Teal,
            Color.Transparent,
            12,
            ShapeKind.Rectangle);
        var movingFill = mergeScene.AddObject(
            0,
            new PointF(154, 0),
            new SizeF(100, 80),
            0,
            0,
            Color.Teal,
            Color.Transparent,
            12,
            ShapeKind.Rectangle);
        mergeScene.TranslateObjectsForPreview([movingFill], -100, 0);
        mergeScene.CompleteDeferredBuild();
        movingFill = mergeScene.ApplyFillOverwriteToNewObjects([movingFill], 0).SingleOrDefault(-1);
        movingFill = mergeScene.MergeSameColorFillsAround(movingFill, connectNearby: true, frame: 0);
        if (mergeScene.ObjectCount != 1
            || (uint)movingFill >= mergeScene.ObjectCount
            || !mergeScene.FillContainsPoint(movingFill, new PointF(-54, 0))
            || !mergeScene.FillContainsPoint(movingFill, new PointF(54, 0)))
        {
            throw new InvalidOperationException("Moving a same-color fill to within 8 vu of another fill did not merge them.");
        }

        var scene = new VectorScene();
        scene.CreateEmpty();
        scene.AddObject(
            0,
            PointF.Empty,
            new SizeF(240, 160),
            0,
            VectorUnits.StrokePointsToUnits(2),
            Color.Teal,
            Color.White,
            12,
            ShapeKind.Rectangle);
        var materialized = scene.MaterializeMarqueeFillParts(new RectangleF(40, -30, 120, 60), 0);
        var movedObjects = materialized.SelectedObjects.ToHashSet();
        var moved = movedObjects.SingleOrDefault(
            index => (uint)index < scene.ObjectCount && scene.ShapeKind[index] == ShapeKind.Path,
            -1);
        var movedBoundaryLines = movedObjects
            .Where(index => (uint)index < scene.ObjectCount && scene.ShapeKind[index] == ShapeKind.Line)
            .ToArray();
        if (!materialized.Changed
            || (uint)moved >= scene.ObjectCount
            || movedBoundaryLines.Length != 1)
        {
            throw new InvalidOperationException(
                $"Moved fill isolation regression could not materialize an outlined rectangle selection: moved={moved}, boundaries={movedBoundaryLines.Length}.");
        }

        var boundaryLinks = movedBoundaryLines
            .SelectMany(index => scene.CaptureFillBoundaryLineLinks(index, 0))
            .ToArray();
        var retainedLinks = MainForm.ExcludeTranslatedFillBoundaryLinks(boundaryLinks, movedObjects);
        if (!boundaryLinks.Any(link => link.FillObjectIndex == moved)
            || retainedLinks.Any(link => link.FillObjectIndex == moved))
        {
            throw new InvalidOperationException("A marquee-selected fill retained a boundary link that would reapply its translation as a contour edit.");
        }

        var movedBoundsBefore = scene.GetObjectWorldBounds(moved);
        var unfilteredSnapshot = scene.CreateSnapshot();
        scene.TranslateObjectsForPreview(materialized.SelectedObjects, 100, 0);
        scene.UpdateFillBoundaryLineLinks(boundaryLinks, rebuildGeometryIndex: false);
        scene.CompleteDeferredBuild();
        var unfilteredBounds = scene.GetObjectWorldBounds(moved);
        scene.RestoreSnapshot(unfilteredSnapshot);
        scene.EditFrame = 0;
        if (unfilteredBounds.Width <= movedBoundsBefore.Width + DrawingTopologyRules.UnitIntersectionTolerance)
        {
            var movedLinkSummary = string.Join(
                ",",
                boundaryLinks
                    .Select(link => $"line={link.LineObjectIndex}/fill={link.FillObjectIndex}/segment={link.SegmentIndex}+{link.SegmentCount}/reverse={link.Reversed}"));
            throw new InvalidOperationException(
                $"Moved fill isolation regression did not reproduce the unfiltered boundary-link extension: before={movedBoundsBefore}, after={unfilteredBounds}, links={movedLinkSummary}.");
        }

        scene.TranslateObjectsForPreview(materialized.SelectedObjects, 100, 0);
        scene.UpdateFillBoundaryLineLinks(retainedLinks, rebuildGeometryIndex: false);
        scene.CompleteDeferredBuild();
        moved = scene.ApplyFillOverwriteToNewObjects([moved], 0).SingleOrDefault(-1);
        moved = scene.MergeSameColorFillsAround(moved, connectNearby: true, frame: 0);

        var outside = Enumerable.Range(0, scene.ObjectCount).SingleOrDefault(
            index => index != moved && scene.ShapeKind[index] == ShapeKind.Path,
            -1);
        var movedBounds = (uint)moved < scene.ObjectCount
            ? scene.GetObjectWorldBounds(moved)
            : RectangleF.Empty;
        if ((uint)moved >= scene.ObjectCount
            || (uint)outside >= scene.ObjectCount
            || Math.Abs(movedBounds.Left - (movedBoundsBefore.Left + 100)) > DrawingTopologyRules.UnitIntersectionTolerance
            || Math.Abs(movedBounds.Top - movedBoundsBefore.Top) > DrawingTopologyRules.UnitIntersectionTolerance
            || Math.Abs(movedBounds.Width - movedBoundsBefore.Width) > DrawingTopologyRules.UnitIntersectionTolerance
            || Math.Abs(movedBounds.Height - movedBoundsBefore.Height) > DrawingTopologyRules.UnitIntersectionTolerance
            || !scene.FillContainsPoint(moved, new PointF(160, 0))
            || scene.FillContainsPoint(moved, new PointF(60, 0))
            || scene.FillContainsPoint(outside, new PointF(60, 0))
            || !scene.FillContainsPoint(outside, new PointF(-60, 0)))
        {
            throw new InvalidOperationException(
                $"A translated outlined marquee fill extended back into its source region: objects={scene.ObjectCount}, moved={moved}, outside={outside}, bounds={movedBounds}.");
        }

        Console.WriteLine("moved_fill_isolation_regression=ok");
    }

    private static void RunMarqueeOutlinedBoundarySelectionRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        var stroke = VectorUnits.StrokePointsToUnits(2);
        scene.AddObject(0, PointF.Empty, new SizeF(240, 160), 0, stroke, Color.Teal, Color.White, 12, ShapeKind.Rectangle);

        var materialized = scene.MaterializeMarqueeFillParts(new RectangleF(-40, -100, 80, 200), 0);
        var selectedLines = materialized.SelectedObjects
            .Where(index => (uint)index < scene.ObjectCount && scene.ShapeKind[index] == ShapeKind.Line)
            .ToArray();
        var selectedFill = materialized.SelectedObjects
            .SingleOrDefault(index => (uint)index < scene.ObjectCount && scene.ShapeKind[index] == ShapeKind.Path, -1);

        if (!materialized.Changed
            || materialized.SelectedObjects.Length != 3
            || selectedLines.Length != 2
            || selectedFill < 0
            || !scene.FillContainsPoint(selectedFill, PointF.Empty)
            || !selectedLines.Any(index => LineMatches(scene, index, new PointF(-40, -80), new PointF(40, -80)))
            || !selectedLines.Any(index => LineMatches(scene, index, new PointF(-40, 80), new PointF(40, 80))))
        {
            throw new InvalidOperationException("Marquee fill materialization did not split and select the enclosed outlined boundary segments.");
        }

        var outlinedSelection = selectedLines.Append(selectedFill).ToArray();
        if (!MainForm.ShouldMoveMarqueeStrokeIndependently(outlinedSelection, selectedFill, selectedLines)
            || !MainForm.ShouldMoveMarqueeStrokeIndependently(selectedLines, selectedLines[0], selectedLines)
            || MainForm.ShouldMoveMarqueeStrokeIndependently([selectedFill], selectedFill, selectedLines))
        {
            throw new InvalidOperationException("Marquee-selected boundary strokes were not isolated from linked fill deformation during drag.");
        }

        var ellipseScene = new VectorScene();
        ellipseScene.CreateEmpty();
        var untouched = ellipseScene.AddLineSegment(
            0,
            new PointF(-120, 1_000),
            new PointF(120, 1_000),
            stroke,
            Color.Transparent,
            Color.White,
            6);
        var ellipse = ellipseScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(2_000, 1_200),
            0,
            stroke,
            Color.Teal,
            Color.White,
            48,
            ShapeKind.Ellipse);
        var crossingLine = ellipseScene.AddLineSegment(
            0,
            new PointF(-1_200, 0),
            new PointF(1_200, 0),
            stroke,
            Color.Transparent,
            Color.Gold,
            12);
        var ellipseBoundary = ellipseScene.GetShapeBoundary(ellipse);
        var cardinalParts = ellipseScene.GetBoundaryParts(ellipse, 0)
            .OrderBy(part => part.PartIndex)
            .ToArray();
        var expectedCardinalEndpoints = new[]
        {
            (new PointF(0, -600), new PointF(1_000, 0)),
            (new PointF(1_000, 0), new PointF(0, 600)),
            (new PointF(0, 600), new PointF(-1_000, 0)),
            (new PointF(-1_000, 0), new PointF(0, -600))
        };
        var hasCardinalCubicParts = cardinalParts.Length == expectedCardinalEndpoints.Length
            && cardinalParts.Zip(expectedCardinalEndpoints).All(pair =>
                pair.First.Points.Length > 2
                && PointsNear(pair.First.Points[0], pair.Second.Item1)
                && PointsNear(pair.First.Points[^1], pair.Second.Item2));
        if (ellipseBoundary.Length <= 65
            || ellipseBoundary[0] != ellipseBoundary[^1]
            || !PointsNear(ellipseBoundary[0], new PointF(0, -600))
            || !hasCardinalCubicParts)
        {
            throw new InvalidOperationException("Ellipse topology did not use four closed top/right/bottom/left cubic boundary segments.");
        }

        var combined = ellipseScene.MaterializeMarqueeSelectionParts(new RectangleF(-200, -700, 400, 1_400), 0);
        var remappedUntouched = (uint)untouched < combined.OldToNewObjectIndex.Length
            ? combined.OldToNewObjectIndex[untouched]
            : -1;
        var ellipseStrokes = Enumerable.Range(0, ellipseScene.ObjectCount)
            .Where(index => index != remappedUntouched
                && ellipseScene.ShapeKind[index] == ShapeKind.Line
                && ellipseScene.Stroke[index] > 0
                && ellipseScene.StrokeArgb[index] == Color.White.ToArgb())
            .ToArray();
        var flattenedEllipseStrokes = Enumerable.Range(0, ellipseScene.ObjectCount)
            .Where(index => ellipseScene.ShapeKind[index] == ShapeKind.Freeform
                && ellipseScene.Stroke[index] > 0
                && ellipseScene.StrokeArgb[index] == Color.White.ToArgb())
            .ToArray();
        var selectedEllipseStrokes = combined.SelectedObjects
            .Where(index => ellipseStrokes.Contains(index))
            .ToArray();
        var ellipseCurves = ellipseStrokes
            .Select(index => ellipseScene.TryGetLineBezierPart(
                index,
                0,
                1,
                out var start,
                out var control1,
                out var control2,
                out var end)
                ? (Valid: true, Start: start, Control1: control1, Control2: control2, End: end)
                : default)
            .ToArray();
        var ellipsePoints = ellipseCurves
            .Where(curve => curve.Valid)
            .SelectMany(curve => Enumerable.Range(0, 17).Select(sample => EvaluateCubic(
                curve.Start,
                curve.Control1,
                curve.Control2,
                curve.End,
                sample / 16f)))
            .ToArray();
        var maximumEllipseEquationError = ellipsePoints.Length == 0
            ? float.PositiveInfinity
            : ellipsePoints.Max(point => Math.Abs(
                point.X * point.X / (1_000f * 1_000f)
                + point.Y * point.Y / (600f * 600f)
                - 1));
        var selectedCurvesRemainEditable = selectedEllipseStrokes.All(index =>
            ellipseScene.TryGetLineBezierPart(index, 0, 1, out var start, out var control1, out var control2, out var end)
            && (DistanceFromChord(control1, start, end) > 0.1f
                || DistanceFromChord(control2, start, end) > 0.1f));
        var selectedEllipseFill = combined.SelectedObjects.Any(index =>
            (uint)index < ellipseScene.ObjectCount
            && ellipseScene.ShapeKind[index] == ShapeKind.Path
            && ellipseScene.FillContainsPoint(index, PointF.Empty));
        var orderedMarqueeSelection = MainForm.OrderMarqueeSelectionForCurveEditing(
            ellipseScene,
            combined.SelectedObjects,
            combined.SelectedObjects);
        var exposesCurveAdjustmentTools = orderedMarqueeSelection.Length > 0
            && selectedEllipseStrokes.Contains(orderedMarqueeSelection[^1]);
        if (!combined.Success
            || !combined.Changed
            || (uint)untouched >= combined.OldToNewObjectIndex.Length
            || combined.OldToNewObjectIndex[untouched] < 0
            || combined.OldToNewObjectIndex[ellipse] != -1
            || combined.OldToNewObjectIndex[crossingLine] != -1
            || ellipseStrokes.Length < 8
            || flattenedEllipseStrokes.Length != 0
            || selectedEllipseStrokes.Length != 4
            || ellipseCurves.Any(curve => !curve.Valid)
            || !selectedCurvesRemainEditable
            || !selectedEllipseFill
            || !exposesCurveAdjustmentTools
            || maximumEllipseEquationError > 0.005f)
        {
            throw new InvalidOperationException(
                $"Combined marquee materialization lost cubic ellipse geometry or fill boundaries: "
                + $"success={combined.Success}, changed={combined.Changed}, strokes={ellipseStrokes.Length}, "
                + $"flattened={flattenedEllipseStrokes.Length}, selected={selectedEllipseStrokes.Length}, "
                + $"editable={selectedCurvesRemainEditable}, fill={selectedEllipseFill}, "
                + $"handles={exposesCurveAdjustmentTools}, error={maximumEllipseEquationError:0.######}.");
        }

        static PointF EvaluateCubic(PointF start, PointF control1, PointF control2, PointF end, float t)
        {
            var inverse = 1 - t;
            return new PointF(
                inverse * inverse * inverse * start.X
                    + 3 * inverse * inverse * t * control1.X
                    + 3 * inverse * t * t * control2.X
                    + t * t * t * end.X,
                inverse * inverse * inverse * start.Y
                    + 3 * inverse * inverse * t * control1.Y
                    + 3 * inverse * t * t * control2.Y
                    + t * t * t * end.Y);
        }

        static float DistanceFromChord(PointF point, PointF start, PointF end)
        {
            var dx = end.X - start.X;
            var dy = end.Y - start.Y;
            var length = MathF.Sqrt(dx * dx + dy * dy);
            return length <= 0.0001f
                ? 0
                : Math.Abs((point.X - start.X) * dy - (point.Y - start.Y) * dx) / length;
        }
    }

    private static void RunLassoMaterializationRegression()
    {
        var lasso = new[] { new PointF(-140, -80), new PointF(140, -80), new PointF(140, -20), new PointF(20, -20), new PointF(20, 80), new PointF(-140, 80) };
        var fillScene = new VectorScene();
        fillScene.CreateEmpty();
        var source = fillScene.AddObject(0, PointF.Empty, new SizeF(240, 160), 0, VectorUnits.StrokePointsToUnits(2), Color.Teal, Color.White, 12, ShapeKind.Rectangle);
        var sourceMetadata = (
            Layer: fillScene.ObjectLayer[source],
            Frame: fillScene.ObjectKeyframeFrame[source],
            Order: fillScene.ObjectOrder[source],
            Stroke: fillScene.Stroke[source],
            FillArgb: fillScene.Argb[source],
            StrokeArgb: fillScene.StrokeArgb[source]);
        var sourceArea = PathArea(fillScene, new[] { source });
        var materialized = fillScene.MaterializeLassoSelectionParts(lasso, 0);
        var selectedPaths = materialized.SelectedObjects
            .Where(index => (uint)index < fillScene.ObjectCount && fillScene.ShapeKind[index] == ShapeKind.Path)
            .ToArray();
        var selectedFill = selectedPaths.FirstOrDefault(
            index => fillScene.FillContainsPoint(index, PointF.Empty),
            -1);
        var replacementLines = Enumerable.Range(0, fillScene.ObjectCount)
            .Where(index => fillScene.ShapeKind[index] == ShapeKind.Line)
            .ToArray();
        var afterArea = PathArea(
            fillScene,
            Enumerable.Range(0, fillScene.ObjectCount)
                .Where(index => fillScene.ShapeKind[index] == ShapeKind.Path));
        var notch = new PointF(80, 40);
        if (!materialized.Success
            || !materialized.Changed
            || selectedFill < 0
            || selectedPaths.Length == 0
            || selectedPaths.Any(index => fillScene.FillContainsPoint(index, notch))
            || Math.Abs(afterArea - sourceArea) > Math.Max(1d, sourceArea * 0.001d)
            || replacementLines.Length == 0
            || !materialized.SelectedObjects.Any(index => replacementLines.Contains(index))
            || replacementLines.Any(index => fillScene.ObjectLayer[index] != sourceMetadata.Layer
                || fillScene.ObjectKeyframeFrame[index] != sourceMetadata.Frame
                || fillScene.ObjectOrder[index] != sourceMetadata.Order
                || fillScene.Stroke[index] != sourceMetadata.Stroke
                || fillScene.Argb[index] != sourceMetadata.FillArgb
                || fillScene.StrokeArgb[index] != sourceMetadata.StrokeArgb))
        {
            throw new InvalidOperationException(
                $"Concave lasso materialization lost fill area or outlined boundary geometry: "
                + $"success={materialized.Success}, changed={materialized.Changed}, selectedFill={selectedFill}, "
                + $"paths={selectedPaths.Length}, lines={replacementLines.Length}, area={sourceArea:0.###}->{afterArea:0.###}.");
        }
        var curveScene = new VectorScene();
        curveScene.CreateEmpty();
        var curve = curveScene.AddCubicCurveSegment(
            0,
            new PointF(-220, 0),
            new PointF(-100, 70),
            new PointF(40, 70),
            new PointF(220, 0),
            VectorUnits.StrokePointsToUnits(3),
            Color.Transparent,
            Color.Coral,
            15,
            LineEndpointStyle.Sharp,
            LineEndpointStyle.Round);
        curveScene.ObjectSubOrder[curve] = 0.375;
        var gradientStops = new[]
        {
            new GradientStop(0, Color.Coral),
            new GradientStop(0.5f, Color.Gold),
            new GradientStop(1, Color.MediumPurple)
        };
        var gradientStart = new PointF(-220, 0);
        var gradientEnd = new PointF(220, 0);
        curveScene.SetGradientPaint(curve, GradientKind.Linear, gradientStops, gradientStart, gradientEnd);
        var curveMetadata = (
            Layer: curveScene.ObjectLayer[curve],
            Frame: curveScene.ObjectKeyframeFrame[curve],
            Order: curveScene.ObjectOrder[curve],
            SubOrder: curveScene.ObjectSubOrder[curve],
            Stroke: curveScene.Stroke[curve],
            Argb: curveScene.Argb[curve],
            StrokeArgb: curveScene.StrokeArgb[curve]);
        var curveLasso = new[] { new PointF(-150, -100), new PointF(30, -100), new PointF(30, 100), new PointF(-150, 100) };
        var curveMaterialized = curveScene.MaterializeLassoSelectionParts(curveLasso, 0);
        var curveParts = Enumerable.Range(0, curveScene.ObjectCount)
            .Where(index => curveScene.ShapeKind[index] == ShapeKind.Line)
            .OrderBy(index => curveScene.ObjectSubOrder[index])
            .ToArray();
        var selectedCurveParts = curveMaterialized.SelectedObjects
            .Where(index => (uint)index < curveScene.ObjectCount && curveScene.ShapeKind[index] == ShapeKind.Line)
            .ToArray();
        var hasCurvedSelectedPart = selectedCurveParts.Any(index =>
            curveScene.TryGetLineCubic(index, out var start, out var control1, out var control2, out var end)
            && (Math.Abs((control1.X - start.X) * (end.Y - start.Y)
                - (control1.Y - start.Y) * (end.X - start.X)) > 0.1f
                || Math.Abs((control2.X - start.X) * (end.Y - start.Y)
                    - (control2.Y - start.Y) * (end.X - start.X)) > 0.1f));
        if (!curveMaterialized.Success
            || !curveMaterialized.Changed
            || curveParts.Length < 3
            || selectedCurveParts.Length != 1
            || !hasCurvedSelectedPart
            || curveParts.Any(index => curveScene.ObjectLayer[index] != curveMetadata.Layer
                || curveScene.ObjectKeyframeFrame[index] != curveMetadata.Frame
                || curveScene.ObjectOrder[index] != curveMetadata.Order
                || curveScene.Stroke[index] != curveMetadata.Stroke
                || curveScene.Argb[index] != curveMetadata.Argb
                || curveScene.StrokeArgb[index] != curveMetadata.StrokeArgb
                || curveScene.GetGradientKind(index) != GradientKind.Linear
                || !curveScene.GetGradientStops(index).SequenceEqual(gradientStops)
                || !PointsNear(curveScene.GetGradientStart(index), gradientStart)
                || !PointsNear(curveScene.GetGradientEnd(index), gradientEnd))
            || curveParts.Select(index => curveScene.ObjectSubOrder[index]).Distinct().Count() != curveParts.Length
            || !curveParts.Any(index => Math.Abs(curveScene.ObjectSubOrder[index] - curveMetadata.SubOrder) < 0.0001)
            || curveScene.GetLineEndpointStyle(curveParts[0], true) != LineEndpointStyle.Sharp
            || curveScene.GetLineEndpointStyle(curveParts[^1], false) != LineEndpointStyle.Round)
        {
            throw new InvalidOperationException(
                $"Lasso line materialization did not preserve an editable cubic stroke: "
                + $"success={curveMaterialized.Success}, changed={curveMaterialized.Changed}, "
                + $"parts={curveParts.Length}, selected={selectedCurveParts.Length}, curved={hasCurvedSelectedPart}.");
        }
        var enclosedScene = new VectorScene();
        enclosedScene.CreateEmpty();
        enclosedScene.AddObject(0, PointF.Empty, new SizeF(240, 160), 0, 0, Color.Teal, Color.Transparent, 8, ShapeKind.Rectangle);
        var enclosedBefore = enclosedScene.CreateSnapshot();
        var enclosedGeometryRevision = enclosedScene.GeometryRevision;
        var enclosedSummaryRevision = enclosedScene.SummaryRevision;
        var enclosedResult = enclosedScene.MaterializeLassoSelectionParts(
            new[]
            {
                new PointF(-400, -300),
                new PointF(400, -300),
                new PointF(400, 300),
                new PointF(-400, 300)
            },
            0);
        if (!enclosedResult.Success
            || enclosedResult.Changed
            || !SnapshotUnchanged(enclosedScene, enclosedBefore, enclosedGeometryRevision, enclosedSummaryRevision))
        {
            throw new InvalidOperationException("A fully enclosed lasso unexpectedly materialized the whole object.");
        }
        var lockedScene = new VectorScene();
        lockedScene.CreateEmpty();
        lockedScene.AddObject(0, PointF.Empty, new SizeF(240, 160), 0, 0, Color.Teal, Color.Transparent, 8, ShapeKind.Rectangle);
        if (!lockedScene.SetLayerLocked(0, true)) throw new InvalidOperationException("Lasso lock setup failed.");
        var lockedBefore = lockedScene.CreateSnapshot();
        var lockedGeometryRevision = lockedScene.GeometryRevision;
        var lockedSummaryRevision = lockedScene.SummaryRevision;
        var lockedResult = lockedScene.MaterializeLassoSelectionParts(lasso, 0);
        if (!lockedResult.Success
            || lockedResult.Changed
            || !SnapshotUnchanged(lockedScene, lockedBefore, lockedGeometryRevision, lockedSummaryRevision))
        {
            throw new InvalidOperationException("Lasso materialization mutated a locked layer.");
        }
        var blankScene = new VectorScene();
        blankScene.CreateEmpty(frameCount: 4);
        blankScene.AddObject(0, PointF.Empty, new SizeF(240, 160), 0, 0, Color.Teal, Color.Transparent, 8, ShapeKind.Rectangle);
        if (!blankScene.InsertTimelineBlankKeyframe(0, 2)) throw new InvalidOperationException("Lasso blank-frame setup failed.");
        blankScene.EditFrame = 2;
        var blankBefore = blankScene.CreateSnapshot();
        var blankGeometryRevision = blankScene.GeometryRevision;
        var blankSummaryRevision = blankScene.SummaryRevision;
        var blankResult = blankScene.MaterializeLassoSelectionParts(lasso, 2);
        if (!blankResult.Success
            || blankResult.Changed
            || !SnapshotUnchanged(blankScene, blankBefore, blankGeometryRevision, blankSummaryRevision))
        {
            throw new InvalidOperationException("Lasso materialization mutated content on a blank frame.");
        }
        Console.WriteLine("lasso_materialization_regression=ok");
        static double PolygonArea(IReadOnlyList<PointF> points)
        {
            if (points.Count < 3) return 0;
            var area = 0d;
            for (var index = 0; index < points.Count; index++)
            {
                var next = (index + 1) % points.Count;
                area += (double)points[index].X * points[next].Y - (double)points[next].X * points[index].Y;
            }
            return Math.Abs(area) * 0.5d;
        }

        static double PathArea(VectorScene scene, IEnumerable<int> paths) =>
            paths.Sum(index => scene.GetObjectBoundaryContours(index).Sum(PolygonArea));
        static bool SnapshotUnchanged(VectorScene scene, VectorSceneSnapshot snapshot, long geometryRevision, long summaryRevision) =>
            scene.ObjectCount == snapshot.ObjectCount && scene.VirtualAtomCount == snapshot.VirtualAtomCount
            && scene.GeometryRevision == geometryRevision && scene.SummaryRevision == summaryRevision
            && scene.ObjectLayer.AsSpan(0, scene.ObjectCount).SequenceEqual(snapshot.ObjectLayer) && scene.ObjectKeyframeFrame.AsSpan(0, scene.ObjectCount).SequenceEqual(snapshot.ObjectKeyframeFrame)
            && scene.ObjectOrder.AsSpan(0, scene.ObjectCount).SequenceEqual(snapshot.ObjectOrder) && scene.ObjectSubOrder.AsSpan(0, scene.ObjectCount).SequenceEqual(snapshot.ObjectSubOrder)
            && scene.ShapeKind.AsSpan(0, scene.ObjectCount).SequenceEqual(snapshot.ShapeKind) && scene.Argb.AsSpan(0, scene.ObjectCount).SequenceEqual(snapshot.Argb)
            && scene.StrokeArgb.AsSpan(0, scene.ObjectCount).SequenceEqual(snapshot.StrokeArgb) && scene.Stroke.AsSpan(0, scene.ObjectCount).SequenceEqual(snapshot.Stroke)
            && scene.X.AsSpan(0, scene.ObjectCount).SequenceEqual(snapshot.X) && scene.Y.AsSpan(0, scene.ObjectCount).SequenceEqual(snapshot.Y)
            && scene.Width.AsSpan(0, scene.ObjectCount).SequenceEqual(snapshot.Width) && scene.Height.AsSpan(0, scene.ObjectCount).SequenceEqual(snapshot.Height)
            && scene.LayerLocked.SequenceEqual(snapshot.LayerLocked);
    }

    private static int AddTopologyFill(VectorScene scene, int layer, float stroke)
    {
        return scene.AddObject(layer, PointF.Empty, new SizeF(400, 240), 0, stroke, Color.Teal, Color.White, 12, ShapeKind.Rectangle);
    }

    private static int AddTopologyLine(VectorScene scene, int layer, PointF start, PointF end)
    {
        return scene.AddLineSegment(layer, start, end, VectorUnits.StrokePointsToUnits(2), Color.Transparent, Color.White, 6);
    }

    private static DrawingElementHit AssertTopologyHit(
        VectorScene scene,
        PointF point,
        int objectIndex,
        DrawingElementKind kind,
        int partIndex,
        float startT,
        float endT,
        string context)
    {
        var hit = scene.HitTestElement(point, 0, 0);
        if (!hit.IsValid
            || hit.Key.ObjectIndex != objectIndex
            || hit.Key.Kind != kind
            || hit.Key.PartIndex != partIndex
            || !NearlyEqual(hit.StartT, startT)
            || !NearlyEqual(hit.EndT, endT))
        {
            throw new InvalidOperationException(
                $"{context} hit mismatch at ({point.X:0.###},{point.Y:0.###}): expected object={objectIndex}, kind={kind}, part={partIndex}, t={startT:0.####}-{endT:0.####}; actual key={hit.Key}, t={hit.StartT:0.####}-{hit.EndT:0.####}.");
        }

        return hit;
    }

    private static void AssertShapeCounts(VectorScene scene, int objects, int lines, int paths, int rectangles, string context)
    {
        var actualLines = 0;
        var actualPaths = 0;
        var actualRectangles = 0;
        for (var i = 0; i < scene.ObjectCount; i++)
        {
            switch (scene.ShapeKind[i])
            {
                case ShapeKind.Line:
                    actualLines++;
                    break;
                case ShapeKind.Path:
                    actualPaths++;
                    break;
                case ShapeKind.Rectangle:
                    actualRectangles++;
                    break;
            }
        }

        if (scene.ObjectCount != objects || actualLines != lines || actualPaths != paths || actualRectangles != rectangles)
        {
            throw new InvalidOperationException(
                $"{context} shape counts mismatch: expected objects={objects}, lines={lines}, paths={paths}, rectangles={rectangles}; actual objects={scene.ObjectCount}, lines={actualLines}, paths={actualPaths}, rectangles={actualRectangles}.");
        }
    }

    private static void AssertDetachedLine(VectorScene scene, DrawingElementHit detached, PointF start, PointF end, string context)
    {
        if (!detached.IsValid
            || detached.Key.Kind != DrawingElementKind.Stroke
            || (uint)detached.Key.ObjectIndex >= scene.ObjectCount
            || scene.ShapeKind[detached.Key.ObjectIndex] != ShapeKind.Line
            || !LineMatches(scene, detached.Key.ObjectIndex, start, end))
        {
            throw new InvalidOperationException($"{context} did not return the expected materialized line segment: key={detached.Key}.");
        }
    }

    private static void AssertLineSet(VectorScene scene, IReadOnlyList<(PointF Start, PointF End)> expected, string context)
    {
        var remaining = expected.ToList();
        for (var i = 0; i < scene.ObjectCount; i++)
        {
            if (scene.ShapeKind[i] != ShapeKind.Line) continue;
            var match = remaining.FindIndex(segment => LineMatches(scene, i, segment.Start, segment.End));
            if (match < 0)
            {
                scene.TryGetLineEndpoint(i, true, out var actualStart);
                scene.TryGetLineEndpoint(i, false, out var actualEnd);
                throw new InvalidOperationException($"{context} contains an unexpected line ({actualStart.X:0.###},{actualStart.Y:0.###})->({actualEnd.X:0.###},{actualEnd.Y:0.###}).");
            }

            remaining.RemoveAt(match);
        }

        if (remaining.Count > 0)
        {
            throw new InvalidOperationException($"{context} is missing {remaining.Count} expected line segment(s).");
        }
    }

    private static bool LineMatches(VectorScene scene, int objectIndex, PointF expectedStart, PointF expectedEnd)
    {
        if (!scene.TryGetLineEndpoint(objectIndex, true, out var actualStart)
            || !scene.TryGetLineEndpoint(objectIndex, false, out var actualEnd))
        {
            return false;
        }

        return PointsNear(actualStart, expectedStart) && PointsNear(actualEnd, expectedEnd)
            || PointsNear(actualStart, expectedEnd) && PointsNear(actualEnd, expectedStart);
    }

    private static int CountPathFillsContaining(VectorScene scene, PointF point)
    {
        return Enumerable.Range(0, scene.ObjectCount)
            .Count(index => scene.ShapeKind[index] == ShapeKind.Path && scene.FillContainsPoint(index, point));
    }

    private static int FindPathFillContaining(VectorScene scene, PointF point)
    {
        return Enumerable.Range(0, scene.ObjectCount)
            .FirstOrDefault(index => scene.ShapeKind[index] == ShapeKind.Path && scene.FillContainsPoint(index, point), -1);
    }

    private static void AssertSplitPaths(VectorScene scene, DrawingElementHit detached, PointF selectedPoint, PointF otherPoint, string context)
    {
        var paths = Enumerable.Range(0, scene.ObjectCount).Where(index => scene.ShapeKind[index] == ShapeKind.Path).ToArray();
        if (!detached.IsValid
            || detached.Key.Kind != DrawingElementKind.Fill
            || !paths.Contains(detached.Key.ObjectIndex)
            || paths.Length != 2
            || paths.Any(index => scene.Stroke[index] != 0))
        {
            throw new InvalidOperationException($"{context} did not produce two zero-stroke Path fills: key={detached.Key}, paths={paths.Length}.");
        }

        var selected = detached.Key.ObjectIndex;
        var other = paths[0] == selected ? paths[1] : paths[0];
        if (!scene.FillContainsPoint(selected, selectedPoint)
            || scene.FillContainsPoint(selected, otherPoint)
            || !scene.FillContainsPoint(other, otherPoint)
            || scene.FillContainsPoint(other, selectedPoint))
        {
            throw new InvalidOperationException($"{context} produced incorrect fill-region geometry.");
        }
    }

    private static (PointF Start, PointF End)[] OutlinedBoundaryLineSegments()
    {
        return new[]
        {
            (new PointF(-320, 0), new PointF(320, 0)),
            (new PointF(-200, -120), new PointF(200, -120)),
            (new PointF(200, -120), new PointF(200, 0)),
            (new PointF(200, 0), new PointF(200, 120)),
            (new PointF(200, 120), new PointF(-200, 120)),
            (new PointF(-200, 120), new PointF(-200, 0)),
            (new PointF(-200, 0), new PointF(-200, -120))
        };
    }

}
