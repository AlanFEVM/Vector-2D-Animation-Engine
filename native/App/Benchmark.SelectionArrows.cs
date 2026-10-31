using System.Text.Json;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunSelectionArrowRegression()
    {
        var defaults = new SelectionArrowSettings();
        AssertTimeline(defaults.BehaviorFor(ToolMode.ObjectSelect) == SelectionArrowBehavior.Object
            && defaults.BehaviorFor(ToolMode.Select) == SelectionArrowBehavior.Path,
            "Selection arrow defaults lost the object/path distinction.");
        var invalid = new SelectionArrowSettings
        {
            BlackArrow = (SelectionArrowBehavior)99, WhiteArrow = (SelectionArrowBehavior)99,
            OutlineWidth = float.NaN, HitTolerancePixels = 999
        }.Normalized();
        AssertTimeline(invalid.BlackArrow == defaults.BlackArrow && invalid.WhiteArrow == defaults.WhiteArrow
            && invalid.OutlineWidth == 1.5f && invalid.HitTolerancePixels == 20,
            "Invalid selection preferences were not normalized.");
        var swapped = defaults with { BlackArrow = SelectionArrowBehavior.Path, WhiteArrow = SelectionArrowBehavior.Object };
        var roundTrip = ApplicationSettingsStore.Normalize(JsonSerializer.Deserialize<ApplicationSettings>(
            JsonSerializer.Serialize(new ApplicationSettings { SelectionArrows = swapped }))!);
        AssertTimeline(roundTrip.SelectionArrows == swapped,
            "Selection preferences did not survive settings JSON.");
        var legacy = ApplicationSettingsStore.Normalize(JsonSerializer.Deserialize<ApplicationSettings>("{\"SelectionArrows\":null}")!);
        AssertTimeline(legacy.SelectionArrows == defaults, "Legacy settings did not restore safe arrow defaults.");
        AssertTimeline(ToolShortcutMap.ResolveCommandId(ShortcutProfiles.GetBuiltInProfile(ToolShortcutPreset.TraditionalFlash), Keys.V)
                == ShortcutCommandIds.ToolObjectSelect
            && ToolShortcutMap.ResolveCommandId(ShortcutProfiles.GetBuiltInProfile(ToolShortcutPreset.TraditionalFlash), Keys.A)
                == ShortcutCommandIds.ToolSelect,
            "Built-in black/white arrow shortcuts did not resolve independently.");
        var marks = StageControl.ObjectArrowCornerMarks(new RectangleF(20, 30, 100, 80), 1);
        AssertTimeline(marks.Length == 4 && marks[0].Corner == new PointF(15, 25)
            && marks.All(mark => mark.Start.X != mark.Corner.X && mark.End.Y != mark.Corner.Y),
            "Object selection corner marks lost their screen-space geometry.");
        var routing = HotReloadModuleResolver.Resolve([typeof(SelectionPreferencesDialog)]);
        AssertTimeline(routing.Includes(HotReloadModule.Shell), "Selection preferences lost shell hot-reload routing.");
        using var form = new MainForm
        {
            ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
            Location = new Point(-30000, -30000), Size = new Size(1280, 800)
        };
        var tabs = (WorkspaceTabs)RequireField(typeof(MainForm), "_workspaceTabs").GetValue(form)!;
        tabs.SelectedView = WorkspaceView.BasicDrawing;
        var settingsField = RequireField(typeof(MainForm), "_applicationSettings");
        var settings = (ApplicationSettings)settingsField.GetValue(form)!;
        settingsField.SetValue(form, settings with { SelectionArrows = defaults });
        var activate = RequireMethod(typeof(MainForm), "ActivateTool", [typeof(ToolMode)]);
        var scene = (VectorScene)RequireField(typeof(MainForm), "_scene").GetValue(form)!;
        var stage = (StageControl)RequireField(typeof(MainForm), "_stage").GetValue(form)!;
        var objectIndex = scene.AddObject(0, new PointF(1800, 1200), new SizeF(600, 400), 0, 50,
            Color.Coral, 64, ShapeKind.Rectangle);
        activate.Invoke(form, [ToolMode.ObjectSelect]);
        var screen = Point.Round(stage.WorldToScreen(1800, 1200));
        var mouse = new MouseEventArgs(MouseButtons.Left, 1, screen.X, screen.Y, 0);
        var geometryRevision = scene.GeometryRevision;
        RequireMethod(typeof(MainForm), "StageMouseDown").Invoke(form, [stage, mouse]);
        RequireMethod(typeof(MainForm), "StageMouseUp").Invoke(form, [stage, mouse]);
        AssertTimeline(stage.ObjectSelectionMode && stage.SelectedObjects.Contains(objectIndex)
            && stage.SelectedElements.Count == 1 && stage.SelectedElement.Key.Kind == DrawingElementKind.Fill
            && stage.ObjectArrowSelectedFills().Any() && !stage.ObjectArrowSelectedStrokes().Any()
            && scene.GeometryRevision == geometryRevision
            && scene.ShapeKind[objectIndex] == ShapeKind.Rectangle
            && !stage.FillEdgeBezierOverlayVisible,
            $"Black arrow failed to select only the fill or exposed geometry editing handles: "
            + $"objects={string.Join(',', stage.SelectedObjects)}, element={stage.SelectedElement.Key.Kind}, "
            + $"revision={geometryRevision}->{scene.GeometryRevision}, zoom={stage.Zoom}.");
        var edgeScreen = Point.Round(stage.WorldToScreen(2100, 1200));
        var edgeMouse = new MouseEventArgs(MouseButtons.Left, 1, edgeScreen.X, edgeScreen.Y, 0);
        RequireMethod(typeof(MainForm), "StageMouseDown").Invoke(form, [stage, edgeMouse]);
        RequireMethod(typeof(MainForm), "StageMouseUp").Invoke(form, [stage, edgeMouse]);
        AssertTimeline(stage.SelectedElement.Key.Kind == DrawingElementKind.BoundaryStroke
            && !stage.ObjectArrowSelectedFills().Any() && stage.ObjectArrowSelectedStrokes().Any()
            && scene.GeometryRevision == geometryRevision,
            "Black arrow selected the fill together with its boundary stroke.");
        RequireMethod(typeof(MainForm), "SetSelection", [typeof(int), typeof(bool)]).Invoke(form, [objectIndex, true]);
        RequireMethod(typeof(MainForm), "StageMouseDown").Invoke(form, [stage, mouse]);
        RequireMethod(typeof(MainForm), "StageMouseUp").Invoke(form, [stage, mouse]);
        AssertTimeline(stage.SelectedElements.Count == 1 && stage.SelectedElement.Key.Kind == DrawingElementKind.Fill
            && !stage.ObjectArrowSelectedStrokes().Any(),
            "Clicking a previously whole-selected shape failed to narrow selection to its fill.");
        var beforeCount = scene.ObjectCount;
        var start = Point.Round(stage.WorldToScreen(1800, 900));
        var end = Point.Round(stage.WorldToScreen(2300, 1500));
        RequireField(typeof(MainForm), "_marqueeStart").SetValue(form, start);
        RequireMethod(typeof(MainForm), "CompleteMarqueeSelection").Invoke(form, [end]);
        AssertTimeline(scene.ObjectCount > beforeCount && stage.SelectedObjects.Count > 0
            && stage.ObjectSelectionMode && !stage.FillEdgeBezierOverlayVisible
            && stage.SelectedObjects.All(index => scene.IsObjectGeometryInsideBounds(index,
                RectangleF.FromLTRB(1800, 900, 2300, 1500))),
            "Black-arrow marquee failed to select only the cut portion of a raw shape.");
        RequireMethod(typeof(MainForm), "CancelStageSelection").Invoke(form, []);
        AssertTimeline(scene.ObjectCount == beforeCount && stage.SelectedObjects.Count == 0,
            "Cancelling an unedited black-arrow marquee failed to restore temporary cuts.");
        RunSelectionArrowArcDragRegression(form, scene, stage);
        RunSelectionNotchedFillBendRegression(form, scene, stage);
        activate.Invoke(form, [ToolMode.Select]);
        AssertTimeline(!stage.ObjectSelectionMode, "White arrow did not restore geometry-editing presentation.");
        settingsField.SetValue(form, settings with { SelectionArrows = swapped });
        activate.Invoke(form, [ToolMode.ObjectSelect]);
        AssertTimeline(!stage.ObjectSelectionMode, "Custom black-arrow path role was ignored.");
        activate.Invoke(form, [ToolMode.Select]);
        AssertTimeline(stage.ObjectSelectionMode, "Custom white-arrow object role was ignored.");
        var maskScene = new VectorScene();
        maskScene.CreateEmpty();
        PointF[][] ring =
        [
            [new(-500, -500), new(500, -500), new(500, 500), new(-500, 500)],
            [new(-100, -100), new(100, -100), new(100, 100), new(-100, 100)]
        ];
        var ringIndex = maskScene.AddPathObjectContours(0, ring, 40, Color.Red, Color.Gray, 32);
        maskScene.AddObject(0, new PointF(300, 0), new SizeF(200, 800), 0, 0, Color.Blue, 8, ShapeKind.Rectangle);
        var contours = maskScene.GetVisibleSelectionFillContours(ringIndex, 0, ring);
        using var mask = new System.Drawing.Drawing2D.GraphicsPath(System.Drawing.Drawing2D.FillMode.Alternate);
        foreach (var contour in contours)
            if (contour.Length >= 3) mask.AddPolygon(contour);
        AssertTimeline(mask.IsVisible(-300, 0) && !mask.IsVisible(0, 0)
            && !mask.IsVisible(300, 0) && !mask.IsVisible(-495, 0),
            "Fill stipple leaked into a hole, covering fill, or unselected boundary stroke.");
        RunSelectionStippleMotionRegression();
        RunSelectionLineCoverageRegression();
        RunSelectionFillStrokeOverwriteRegression();
        Console.WriteLine("selection_arrows_regression=ok");
    }

    private static void RunSelectionFillStrokeOverwriteRegression()
    {
        foreach (var scenario in new[] { "opaque", "hole", "translucent", "protected", "other-layer", "covered" })
        {
            var scene = new VectorScene();
            scene.CreateEmpty(2);
            var fill = scenario == "hole"
                ? scene.AddPathObjectContours(0,
                    [
                        [new(-500, -500), new(500, -500), new(500, 500), new(-500, 500)],
                        [new(-100, -100), new(100, -100), new(100, 100), new(-100, 100)]
                    ], 0, Color.Coral, Color.Transparent, 32)
                : scene.AddObject(0, PointF.Empty, new SizeF(1000, 1000), 0, 0,
                    scenario == "translucent" ? Color.FromArgb(128, Color.Coral) : Color.Coral, 32, ShapeKind.Rectangle);
            var source = scene.AddObject(scenario == "other-layer" ? 1 : 0, PointF.Empty,
                new SizeF(scenario == "covered" ? 500 : 2000, 100), 0, 50,
                Color.Transparent, Color.Navy, 16, ShapeKind.Line);
            var sourceOrder = scene.ObjectOrder[source];
            var retained = scene.ApplyFillOverwriteToNewObjects([fill], 0, overwriteStrokes: true,
                protectedObjects: scenario == "protected" ? [fill, source] : [fill]);
            var lines = Enumerable.Range(0, scene.ObjectCount)
                .Where(index => scene.ShapeKind[index] == ShapeKind.Line).ToArray();
            var expected = scenario switch { "opaque" => 2, "hole" => 3, "covered" => 0, _ => 1 };
            AssertTimeline(retained.Length == 1 && lines.Length == expected,
                $"Moved-fill stroke overwrite produced incorrect fragments for {scenario}.");
            AssertTimeline(lines.All(index => scene.ObjectOrder[index] == sourceOrder
                && scene.Stroke[index] == 50 && scene.StrokeArgb[index] == Color.Navy.ToArgb()),
                $"Moved-fill stroke overwrite changed stroke material or stacking for {scenario}.");
            if (scenario is not ("opaque" or "hole")) continue;
            foreach (var line in lines)
            {
                scene.TryGetLineEndpoint(line, true, out var start);
                scene.TryGetLineEndpoint(line, false, out var end);
                var midpoint = new PointF((start.X + end.X) * 0.5f, (start.Y + end.Y) * 0.5f);
                AssertTimeline(Math.Abs(midpoint.X) >= 499
                    || scenario == "hole" && Math.Abs(midpoint.X) <= 99,
                    "Covered stroke survived inside opaque paint, or clipping erased a hole's stroke.");
            }
        }
    }

    private static void RunSelectionNotchedFillBendRegression(MainForm form, VectorScene scene, StageControl stage)
    {
        foreach (var tool in new[] { ToolMode.ObjectSelect, ToolMode.Select })
        {
            RequireMethod(typeof(MainForm), "ClearSelection", [typeof(bool)]).Invoke(form, [false]);
            scene.CreateEmpty();
            PointF[] points = [new(1400, 400), new(6000, 900), new(6000, 4400),
                new(0, 4400), new(0, 2800), new(1400, 2800)];
            var fill = scene.AddPathObject(0, points, 0, Color.Teal, Color.Transparent, 32);
            scene.TryConvertFillToBezierPath(fill);
            scene.SetPathBezierSegment(fill, 0, points[0], new(3200, -900), new(4700, -700), points[1]);
            scene.TryGetPathBezierWorldContours(fill, out var contours);
            var originalCurves = new Dictionary<long, CubicBoundarySegment>();
            for (var partIndex = 0; partIndex < 4; partIndex++)
            {
                var start = contours[0][partIndex];
                var end = contours[0][partIndex + 1];
                var delta = new PointF(end.Anchor.X - start.Anchor.X, end.Anchor.Y - start.Anchor.Y);
                var line = scene.AddObject(0,
                    new PointF((start.Anchor.X + end.Anchor.X) * 0.5f, (start.Anchor.Y + end.Anchor.Y) * 0.5f),
                    new SizeF(MathF.Sqrt(delta.X * delta.X + delta.Y * delta.Y), 1),
                    MathF.Atan2(delta.Y, delta.X), 25, Color.Transparent, Color.White, 8, ShapeKind.Line);
                scene.CurveControlX[line] = start.OutgoingControl.X;
                scene.CurveControlY[line] = start.OutgoingControl.Y;
                scene.CurveControl2X[line] = end.IncomingControl.X;
                scene.CurveControl2Y[line] = end.IncomingControl.Y;
                originalCurves[scene.ObjectOrder[line]] = new(start.Anchor, start.OutgoingControl,
                    end.IncomingControl, end.Anchor);
            }
            scene.CompleteDeferredBuild();
            RequireMethod(typeof(MainForm), "ActivateTool", [typeof(ToolMode)]).Invoke(form, [tool]);
            var screen = Point.Round(stage.WorldToScreen(700, 2800));
            var down = new MouseEventArgs(MouseButtons.Left, 1, screen.X, screen.Y, 0);
            var up = new MouseEventArgs(MouseButtons.Left, 1, screen.X, screen.Y - 20, 0);
            RequireMethod(typeof(MainForm), "StageMouseDown").Invoke(form, [stage, down]);
            RequireMethod(typeof(MainForm), "StageMouseMove").Invoke(form, [stage, up]);
            AssertTimeline(RequireField(typeof(MainForm), "_fillEdgeNoUiArc").GetValue(form) is not null,
                $"{tool} failed to begin bending the unstroked notch boundary.");
            RequireMethod(typeof(MainForm), "StageMouseUp").Invoke(form, [stage, up]);
            var strokes = Enumerable.Range(0, scene.ObjectCount).Where(scene.HasStroke).ToArray();
            AssertTimeline(strokes.Length == originalCurves.Count && strokes.All(index =>
                originalCurves.ContainsKey(scene.ObjectOrder[index])
                && scene.Stroke[index] == 25 && scene.StrokeArgb[index] == Color.White.ToArgb()),
                $"{tool} fill bending erased the remaining outline or filled an existing gap.");
            foreach (var stroke in strokes)
            {
                var original = originalCurves[scene.ObjectOrder[stroke]];
                scene.TryGetLineEndpoint(stroke, true, out var start);
                scene.TryGetLineEndpoint(stroke, false, out var end);
                AssertTimeline(PointsWithin(start, original.Start, 1) && PointsWithin(end, original.End, 1)
                    && PointsWithin(new(scene.CurveControlX[stroke], scene.CurveControlY[stroke]), original.Control1, 1)
                    && PointsWithin(new(scene.CurveControl2X[stroke], scene.CurveControl2Y[stroke]), original.Control2, 1),
                    "Fill bending changed an untouched outline segment's geometry.");
            }
            RequireMethod(typeof(MainForm), "UndoLastEdit").Invoke(form, []);
            scene.TryGetPathBezierWorldContours(fill, out var restored);
            AssertTimeline(restored[0][4].OutgoingControl == contours[0][4].OutgoingControl
                && restored[0][5].IncomingControl == contours[0][5].IncomingControl
                && Enumerable.Range(0, scene.ObjectCount).Count(scene.HasStroke) == originalCurves.Count,
                "Undoing notch bending did not restore the original fill and its partial outline together.");
        }
    }

    private static void RunSelectionArrowArcDragRegression(MainForm form, VectorScene scene, StageControl stage)
    {
        var line = scene.AddObject(0, new PointF(1800, 2500), new SizeF(1800, 400), 0, 50,
            Color.Transparent, Color.Black, 16, ShapeKind.Line);
        var screen = Point.Round(stage.WorldToScreen(1800, 2500));
        var down = new MouseEventArgs(MouseButtons.Left, 1, screen.X, screen.Y, 0);
        var up = new MouseEventArgs(MouseButtons.Left, 1, screen.X, screen.Y + 20, 0);
        var originalControl = new PointF(scene.CurveControlX[line], scene.CurveControlY[line]);
        scene.TryGetLineEndpoint(line, true, out var start);
        scene.TryGetLineEndpoint(line, false, out var end);
        RequireMethod(typeof(MainForm), "UpdateInteractionCursor").Invoke(form, [screen]);
        AssertTimeline(stage.Cursor == Cursors.Cross, "Unselected black-arrow line body lost its bend cursor.");
        RequireMethod(typeof(MainForm), "StageMouseDown").Invoke(form, [stage, down]);
        AssertTimeline(RequireField(typeof(MainForm), "_arcDragSession").GetValue(form) is not null
            && stage.SelectionFeedbackSuppressed,
            "Black-arrow line press failed to start the existing arc-drag session.");
        RequireMethod(typeof(MainForm), "StageMouseUp").Invoke(form, [stage, up]);
        scene.TryGetLineEndpoint(line, true, out var bentStart);
        scene.TryGetLineEndpoint(line, false, out var bentEnd);
        AssertTimeline(start == bentStart && end == bentEnd
            && new PointF(scene.CurveControlX[line], scene.CurveControlY[line]) != originalControl
            && RequireField(typeof(MainForm), "_arcDragSession").GetValue(form) is null
            && stage.SelectedObjects.Count == 0,
            "Black-arrow body drag moved the line instead of bending it, or left an active session.");
        var selectedScreen = up.Location;
        var selectedDown = new MouseEventArgs(MouseButtons.Left, 1, selectedScreen.X, selectedScreen.Y, 0);
        RequireMethod(typeof(MainForm), "StageMouseDown").Invoke(form, [stage, selectedDown]);
        AssertTimeline(RequireField(typeof(MainForm), "_arcDragSession").GetValue(form) is not null,
            "Releasing a black-arrow bend incorrectly turned the next body drag into line movement.");
        RequireMethod(typeof(MainForm), "StageMouseUp").Invoke(form, [stage, selectedDown]);
        AssertTimeline(stage.SelectedObjects.Contains(line) && !stage.SelectionFeedbackSuppressed,
            "A plain line click did not restore stippled stroke selection.");
        RequireMethod(typeof(MainForm), "StageMouseDown").Invoke(form, [stage, selectedDown]);
        AssertTimeline(RequireField(typeof(MainForm), "_arcDragSession").GetValue(form) is null,
            "Already-selected black-arrow line unexpectedly restarted bending.");
        RequireMethod(typeof(MainForm), "StageMouseUp").Invoke(form, [stage, selectedDown]);
        var endpointScreen = Point.Round(stage.WorldToScreen(start.X, start.Y));
        var endpointDown = new MouseEventArgs(MouseButtons.Left, 1, endpointScreen.X, endpointScreen.Y, 0);
        var endpointUp = new MouseEventArgs(MouseButtons.Left, 1, endpointScreen.X - 15, endpointScreen.Y + 15, 0);
        RequireMethod(typeof(MainForm), "StageMouseDown").Invoke(form, [stage, endpointDown]);
        AssertTimeline((EditHandleKind)RequireField(typeof(MainForm), "_activeHandle").GetValue(form)! == EditHandleKind.LineStart
            && stage.SelectionFeedbackSuppressed,
            "Black arrow did not route endpoint editing through the shared geometry workflow without stipple.");
        RequireMethod(typeof(MainForm), "StageMouseUp").Invoke(form, [stage, endpointUp]);
        AssertTimeline(stage.SelectedObjects.Count == 0,
            "Black-arrow endpoint release left a movable stroke selection behind.");
    }

    private static void RunSelectionLineCoverageRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        var objectIndex = scene.AddObject(0, PointF.Empty, new SizeF(5500, 400), MathF.PI / 2,
            150, Color.Transparent, Color.White, 16, ShapeKind.Line);
        using var stage = new StageControl(scene) { Size = new Size(400, 340) };
        stage.ConfigureSelectionArrows(new SelectionArrowSettings(), true);
        var hit = new DrawingElementHit(new DrawingElementKey(objectIndex, DrawingElementKind.Stroke, 0), 0, 0, 1);
        foreach (var curved in new[] { false, true })
        {
            if (curved)
            {
                scene.CurveControlX[objectIndex] += 1800;
                scene.CurveControl2X[objectIndex] -= 1200;
                scene.InvalidateDeferredTopologyQueries();
                stage.Invalidate();
            }
            stage.SetSelectionState([objectIndex], objectIndex, [hit], hit);
            var part = stage.ObjectArrowSelectedStrokes().Single();
            stage.SetSelectionState([objectIndex], objectIndex, [], DrawingElementHit.None);
            var whole = stage.ObjectArrowSelectedStrokes().Single();
            AssertTimeline(part.PatternOrigin == whole.PatternOrigin
                && part.Contours.Length == whole.Contours.Length
                && part.Contours.Zip(whole.Contours).All(pair => pair.First.SequenceEqual(pair.Second)),
                "Whole-line selection used bounding-box edges instead of the actual straight/curved stroke.");
            if (curved) continue;
            using var bitmap = new Bitmap(stage.Width, stage.Height);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.Transparent);
            RequireMethod(typeof(StageControl), "DrawObjectArrowSelection").Invoke(stage, [graphics]);
            AssertTimeline(bitmap.GetPixel(192, 170).A == 0 && bitmap.GetPixel(208, 170).A == 0
                && bitmap.GetPixel(200, 170).A > 0,
                "Selected vertical line grew stippled side bands outside its actual stroke width.");
        }
    }

    private static void RunSelectionStippleMotionRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        var objectIndex = scene.AddObject(0, PointF.Empty, new SizeF(3000, 2000), 0, 0, Color.Red, 16, ShapeKind.Rectangle);
        var hit = scene.HitTestElement(PointF.Empty, 0, 1);
        using var stage = new StageControl(scene) { Size = new Size(400, 300) };
        stage.ConfigureSelectionArrows(new SelectionArrowSettings(), true);
        stage.SetSelectionState([objectIndex], objectIndex, [hit], hit);
        using var before = CaptureStipple();
        var origin = stage.ObjectArrowSelectedFills().Single().PatternOrigin;
        var offset = new PointF(VectorUnits.FromPixels(3), VectorUnits.FromPixels(5));
        stage.SetSelectionDragPreviewOffset(offset);
        using var preview = CaptureStipple();
        stage.ClearSelectionDragPreview(invalidate: false);
        var parts = scene.MaterializeSelectedParts([hit.Key], 0);
        AssertTimeline(parts.Success && parts.Parts.Length > 0, "Stipple motion fixture could not materialize its fill.");
        var movedObject = parts.Parts[0].Result.ObjectIndex;
        scene.X[movedObject] += offset.X;
        scene.Y[movedObject] += offset.Y;
        scene.InvalidateDeferredTopologyQueries();
        stage.SetSelectionState([movedObject], movedObject, [], DrawingElementHit.None);
        stage.Invalidate();
        using var committed = CaptureStipple();
        var movedOrigin = stage.ObjectArrowSelectedFills().Single().PatternOrigin;
        AssertTimeline(PointsWithin(movedOrigin, new PointF(origin.X + offset.X, origin.Y + offset.Y), 0.01f),
            "Selection stipple origin changed during fill materialization.");
        for (var row = 130; row < 170; row++)
            for (var column = 180; column < 220; column++)
            {
                var expected = before.GetPixel(column, row).ToArgb();
                AssertTimeline(preview.GetPixel(column + 3, row + 5).ToArgb() == expected
                    && committed.GetPixel(column + 3, row + 5).ToArgb() == expected,
                    "Selection stipple changed phase or sampling between preview and committed movement.");
            }

        Bitmap CaptureStipple()
        {
            var bitmap = new Bitmap(stage.Width, stage.Height);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.Red);
            RequireMethod(typeof(StageControl), "DrawObjectArrowSelection").Invoke(stage, [graphics]);
            return bitmap;
        }
    }
}
