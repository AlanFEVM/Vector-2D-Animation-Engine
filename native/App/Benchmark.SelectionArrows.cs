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
        RequireMethod(typeof(MainForm), "ClearSelection", [typeof(bool)]).Invoke(form, [false]);
        RequireMethod(typeof(MainForm), "StageMouseDown").Invoke(form, [stage, edgeMouse]);
        RequireMethod(typeof(MainForm), "StageMouseUp").Invoke(form, [stage, edgeMouse]);
        AssertTimeline(stage.SelectedElement.Key.Kind == DrawingElementKind.BoundaryStroke
            && !stage.ObjectArrowSelectedFills().Any() && stage.ObjectArrowSelectedStrokes().Any()
            && scene.GeometryRevision == geometryRevision,
            "Black arrow selected the fill together with its boundary stroke.");
        RequireMethod(typeof(MainForm), "SetSelection", [typeof(int), typeof(bool)]).Invoke(form, [objectIndex, true]);
        RequireMethod(typeof(MainForm), "StageMouseDown").Invoke(form, [stage, mouse]);
        RequireMethod(typeof(MainForm), "StageMouseUp").Invoke(form, [stage, mouse]);
        AssertTimeline(stage.SelectedElements.Count == 0 && stage.ObjectArrowSelectedFills().Any()
            && stage.ObjectArrowSelectedStrokes().Any(),
            "Clicking a stippled whole-shape selection narrowed its move scope.");
        var beforeCount = scene.ObjectCount;
        var start = Point.Round(stage.WorldToScreen(1800, 900));
        var end = Point.Round(stage.WorldToScreen(2300, 1500));
        var worldStart = stage.ScreenToWorld(start);
        var worldEnd = stage.ScreenToWorld(end);
        var selectionBounds = RectangleF.FromLTRB(
            MathF.Floor(Math.Min(worldStart.X, worldEnd.X)), MathF.Floor(Math.Min(worldStart.Y, worldEnd.Y)),
            MathF.Ceiling(Math.Max(worldStart.X, worldEnd.X)), MathF.Ceiling(Math.Max(worldStart.Y, worldEnd.Y)));
        RequireField(typeof(MainForm), "_marqueeStart").SetValue(form, start);
        RequireMethod(typeof(MainForm), "CompleteMarqueeSelection").Invoke(form, [end]);
        AssertTimeline(scene.ObjectCount > beforeCount && stage.SelectedObjects.Count > 0
            && stage.ObjectSelectionMode && !stage.FillEdgeBezierOverlayVisible
            && stage.SelectedObjects.All(index => scene.IsObjectGeometryInsideBounds(index, selectionBounds)),
            $"Black-arrow marquee failed to select only the cut portion of a raw shape: objects={beforeCount}/{scene.ObjectCount}, selected={string.Join(',', stage.SelectedObjects)}, bounds={selectionBounds}, objectMode={stage.ObjectSelectionMode}, overlay={stage.FillEdgeBezierOverlayVisible}.");
        RequireMethod(typeof(MainForm), "CancelStageSelection").Invoke(form, []);
        AssertTimeline(scene.ObjectCount == beforeCount && stage.SelectedObjects.Count == 0,
            "Cancelling an unedited black-arrow marquee failed to restore temporary cuts.");
        RunSelectionArrowArcDragRegression(form, scene, stage);
        RunSelectionArrowArcResponseRegression(form, scene, stage);
        RunSelectionNotchedFillBendRegression(form, scene, stage);
        RunSelectionArrowHoverLifetimeRegression();
        activate.Invoke(form, [ToolMode.Select]);
        AssertTimeline(!stage.ObjectSelectionMode, "White arrow did not restore geometry-editing presentation.");
        settingsField.SetValue(form, settings with { SelectionArrows = swapped });
        activate.Invoke(form, [ToolMode.ObjectSelect]);
        AssertTimeline(!stage.ObjectSelectionMode, "Custom black-arrow path role was ignored.");
        activate.Invoke(form, [ToolMode.Select]);
        AssertTimeline(stage.ObjectSelectionMode, "Custom white-arrow object role was ignored.");
        settingsField.SetValue(form, settings with { SelectionArrows = defaults });
        activate.Invoke(form, [ToolMode.ObjectSelect]);
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
        RunSelectionFillDragPresentationRegression(form, scene, stage);
        RunSelectionArrowMoveOnlyRegression(form, scene, stage);
        RunSelectionLineCoverageRegression();
        RunSelectionStippleOverlapRegression();
        RunSelectionMoveFeedbackRegression();
        RunSelectionFillStrokeOverwriteRegression();
        Console.WriteLine("selection_arrows_regression=ok");
    }

    private static void RunSelectionArrowHoverLifetimeRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        var source = scene.AddCubicCurveSegment(0, new PointF(0, 0),
            new PointF(300, -300), new PointF(700, -300), new PointF(1000, 0),
            25, Color.Transparent, Color.White, 8);
        var retained = scene.AddCubicCurveSegment(0, new PointF(3000, 0),
            new PointF(3300, -300), new PointF(3700, -300), new PointF(4000, 0),
            25, Color.Transparent, Color.White, 8);
        using var stage = new StageControl(scene) { Size = new Size(800, 600) };
        var hit = new DrawingElementHit(new DrawingElementKey(source, DrawingElementKind.Stroke, 0), 0, 0, 1);
        stage.SetHoveredLineElement(hit);
        AssertTimeline(stage.HoveredLineElement == hit && stage.ShouldDrawHoveredLineControls(),
            "A current hover target did not expose its curve controls.");

        stage.SetMarquee(new Point(20, 20), new Point(200, 200));
        stage.SetHoveredLineElement(hit);
        AssertTimeline(!stage.HoveredLineElement.IsValid && !stage.ShouldDrawHoveredLineControls()
            && stage.HitTestHoveredLineHandle(Point.Round(stage.WorldToScreen(0, 0))) == EditHandleKind.None,
            "A marquee retained or rearmed a hovered curve's edit handles.");
        stage.ClearMarquee();
        AssertTimeline(!stage.HoveredLineElement.IsValid,
            "Completing a marquee resurrected its previous hover target.");

        stage.SetHoveredLineElement(hit);
        var result = scene.MaterializeMarqueeSelectionParts(new RectangleF(250, -1000, 500, 2000), 0);
        AssertTimeline(result.Changed && result.OldToNewObjectIndex[retained] == source,
            "Hover lifetime regression failed to reuse the old hover target's object index.");
        AssertTimeline(!stage.HoveredLineElement.IsValid && !stage.ShouldDrawHoveredLineControls()
            && stage.HitTestHoveredLineHandle(Point.Round(stage.WorldToScreen(3000, 0))) == EditHandleKind.None,
            "Marquee compaction redirected stale hover handles to a different curve.");
        stage.SetHoveredLineElement(hit);
        AssertTimeline(stage.HoveredLineElement == hit && stage.ShouldDrawHoveredLineControls(),
            "A fresh hover at a reused object index did not restore curve controls.");

        var replacement = new VectorScene();
        replacement.CreateEmpty();
        replacement.AddCubicCurveSegment(0, new PointF(0, 1000),
            new PointF(300, 700), new PointF(700, 700), new PointF(1000, 1000),
            25, Color.Transparent, Color.White, 8);
        stage.BindScene(replacement);
        AssertTimeline(!stage.HoveredLineElement.IsValid && !stage.ShouldDrawHoveredLineControls(),
            "Rebinding the Stage preserved a hover target from the previous scene.");
        stage.BindScene(scene);
        AssertTimeline(!stage.HoveredLineElement.IsValid,
            "Returning to the previous scene resurrected a cleared hover target.");
        Console.WriteLine("selection_arrow_hover_lifetime=ok,marquee=cleared,compaction=invalidated,rebind=cleared");
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
        scene.QuadraticLineEditing[line] = true;
        var screen = Point.Round(stage.WorldToScreen(1800, 2500));
        var down = new MouseEventArgs(MouseButtons.Left, 1, screen.X, screen.Y, 0);
        var up = new MouseEventArgs(MouseButtons.Left, 1, screen.X, screen.Y + 20, 0);
        var originalControl = new PointF(scene.CurveControlX[line], scene.CurveControlY[line]);
        scene.TryGetLineEndpoint(line, true, out var start);
        scene.TryGetLineEndpoint(line, false, out var end);
        RequireMethod(typeof(MainForm), "UpdateInteractionCursor").Invoke(form, [screen]);
        AssertTimeline(stage.Cursor == stage.SelectionArrowCursorFor(false, StageControl.SelectionArrowCursorFeedback.Bend),
            "Unselected black-arrow line body lost its dart-and-arc cursor.");
        RequireMethod(typeof(MainForm), "StageMouseDown").Invoke(form, [stage, down]);
        AssertTimeline(RequireField(typeof(MainForm), "_arcDragSession").GetValue(form) is not null
            && stage.SelectionFeedbackSuppressed,
            "Black-arrow line press failed to start the existing arc-drag session.");
        var revision = scene.GeometryRevision;
        RequireMethod(typeof(MainForm), "StageMouseMove").Invoke(form,
            [stage, new MouseEventArgs(MouseButtons.Left, 0, screen.X, screen.Y + 20, 0)]);
        AssertTimeline(scene.GeometryRevision > revision && !scene.IsLineStraight(line),
            "The first black-arrow straight-to-curve move did not invalidate cached stroke geometry before release.");
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
        RequireMethod(typeof(MainForm), "ClearSelection", [typeof(bool)]).Invoke(form, [false]);
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

    private static void RunSelectionArrowArcResponseRegression(MainForm form, VectorScene scene, StageControl stage)
    {
        var begin = RequireMethod(typeof(MainForm), "BeginArcDrag");
        var update = RequireMethod(typeof(MainForm), "UpdateArcDrag");
        var cancel = RequireMethod(typeof(MainForm), "CancelArcDrag");
        foreach (var quadratic in new[] { true, false })
        foreach (var curved in new[] { false, true })
        {
            var line = scene.AddCubicCurveSegment(0, new PointF(-900, -600),
                new PointF(-300, curved ? -1000 : -600),
                new PointF(300, curved ? -1000 : -600), new PointF(900, -600),
                25, Color.Transparent, Color.Black, 6);
            scene.QuadraticLineEditing[line] = quadratic;
            scene.TryGetLineCubic(line, out var start, out var control1, out var control2, out var end);
            var originalMidpoint = Evaluate(start, control1, control2, end, 0.5f);
            foreach (var parameter in new[] { 0.12f, 0.5f, 0.88f })
            {
                var picked = Evaluate(start, control1, control2, end, parameter);
                var press = Point.Round(stage.WorldToScreen(picked.X, picked.Y));
                var pressWorld = stage.ScreenToWorld(press);
                RequireField(typeof(MainForm), "_startScreen").SetValue(form, press);
                AssertTimeline(begin.Invoke(form, [pressWorld, line]) is true,
                    "Arc response regression failed to begin a body drag.");
                var revision = scene.GeometryRevision;
                update.Invoke(form, [press]);
                AssertTimeline(scene.GeometryRevision == revision,
                    "Pressing without moving invalidated or changed the curve.");
                var moved = new Point(press.X + 12, press.Y + 9);
                var movedWorld = stage.ScreenToWorld(moved);
                update.Invoke(form, [moved]);
                AssertTimeline(scene.GeometryRevision > revision,
                    "The first arc-drag sample left the renderer's geometry revision stale.");
                scene.TryGetLineCubic(line, out var actualStart, out var actual1, out var actual2, out var actualEnd);
                var midpoint = Evaluate(actualStart, actual1, actual2, actualEnd, 0.5f);
                AssertTimeline(PointsWithin(actualStart, start, 0.001f) && PointsWithin(actualEnd, end, 0.001f)
                    && PointsWithin(midpoint, new PointF(originalMidpoint.X + movedWorld.X - pressWorld.X,
                        originalMidpoint.Y + movedWorld.Y - pressWorld.Y), 0.01f),
                    "Body reshaping moved an endpoint or amplified the bend near an endpoint.");
                var movedRevision = scene.GeometryRevision;
                update.Invoke(form, [press]);
                AssertTimeline(scene.GeometryRevision > movedRevision
                    && new PointF(scene.CurveControlX[line], scene.CurveControlY[line]) == control1
                    && new PointF(scene.CurveControl2X[line], scene.CurveControl2Y[line]) == control2,
                    "Returning an arc drag to the press point failed to restore and invalidate the exact original curve.");
                cancel.Invoke(form, [true]);
            }
            scene.RemoveObjectAt(line);
        }
        RequireField(typeof(MainForm), "_startScreen").SetValue(form, null);
        Console.WriteLine("selection_arrow_first_bend_and_uniform_gain=ok");

        static PointF Evaluate(PointF start, PointF control1, PointF control2, PointF end, float parameter)
        {
            var inverse = 1f - parameter;
            return new PointF(
                inverse * inverse * inverse * start.X + 3f * inverse * inverse * parameter * control1.X
                    + 3f * inverse * parameter * parameter * control2.X + parameter * parameter * parameter * end.X,
                inverse * inverse * inverse * start.Y + 3f * inverse * inverse * parameter * control1.Y
                    + 3f * inverse * parameter * parameter * control2.Y + parameter * parameter * parameter * end.Y);
        }
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

    private static void RunSelectionFillDragPresentationRegression(MainForm form, VectorScene scene, StageControl stage)
    {
        AssertTimeline(!MainForm.ShouldPresentSelectionDragPreview(
                true, EditHandleKind.None, 1, objectSelection: true),
            "Black-arrow movement presented a translated stipple over the unmoved source geometry.");
        var activate = RequireMethod(typeof(MainForm), "ActivateTool", [typeof(ToolMode)]);
        var setSelection = RequireMethod(typeof(MainForm), "SetSelection", [typeof(int), typeof(bool)]);
        var clearSelection = RequireMethod(typeof(MainForm), "ClearSelection", [typeof(bool)]);
        var mouseDown = RequireMethod(typeof(MainForm), "StageMouseDown");
        var mouseMove = RequireMethod(typeof(MainForm), "StageMouseMove");
        var mouseUp = RequireMethod(typeof(MainForm), "StageMouseUp");
        var flush = RequireMethod(typeof(MainForm), "FlushLineDragPreview");
        foreach (var wholeSelection in new[] { false, true })
        {
            clearSelection.Invoke(form, [false]);
            scene.CreateEmpty();
            var fill = scene.AddObject(0, PointF.Empty, new SizeF(2000, 1600), 0, 0,
                Color.Teal, 16, ShapeKind.Rectangle);
            scene.AddLineSegment(0, new PointF(-900, -2000), new PointF(-900, 2000),
                25, Color.Transparent, Color.White, 8);
            scene.CompleteDeferredBuild();
            activate.Invoke(form, [ToolMode.ObjectSelect]);
            if (wholeSelection) setSelection.Invoke(form, [fill, false]);
            for (var gesture = 0; gesture < 2; gesture++)
            {
                var selected = gesture == 0 ? fill : stage.SelectedObject;
                var start = Point.Round(stage.WorldToScreen(scene.X[selected], scene.Y[selected]));
                mouseDown.Invoke(form, [stage, new MouseEventArgs(MouseButtons.Left, 1, start.X, start.Y, 0)]);
                var target = start;
                for (var frame = 1; frame <= 2; frame++)
                {
                    target = new Point(start.X - 30 * frame, start.Y - 15 * frame);
                    mouseMove.Invoke(form, [stage, new MouseEventArgs(MouseButtons.Left, 0, target.X, target.Y, 0)]);
                    flush.Invoke(form, null);
                    var front = stage.SelectionFillDragFrontObjectsFor(scene);
                    AssertTimeline(stage.SelectedElements.Count == 0
                        && !stage.SelectionDragPreviewActive && stage.SelectionMoveFeedbackActive
                        && front is null && stage.SelectionMoveOrigins.ContainsKey(stage.SelectedObject),
                        $"Fill movement lost its origin reference or changed stacking: whole={wholeSelection}, gesture={gesture}, frame={frame}.");
                    var commands = new List<(int ObjectIndex, SceneRenderPass Pass)>();
                    SceneRenderOrder.DrawObjectPasses(scene, Enumerable.Range(0, scene.ObjectCount).ToArray(), 0, front,
                        index => commands.Add((index, SceneRenderPass.Fill)),
                        index => commands.Add((index, SceneRenderPass.Stroke)));
                    AssertTimeline(commands.Any(command => command.Pass == SceneRenderPass.Stroke)
                        && commands[^1].Pass == SceneRenderPass.Stroke,
                        "A dragged fill was promoted over the layer's normal stroke pass.");
                }
                mouseUp.Invoke(form, [stage, new MouseEventArgs(MouseButtons.Left, 1, target.X, target.Y, 0)]);
                AssertTimeline(!stage.SelectionFillDragFrontActive && !stage.SelectionDragPreviewActive
                    && !stage.SelectionMoveFeedbackActive && stage.SelectionMoveOrigins.Count == 0,
                    "Finishing a fill drag retained transient presentation state.");
            }
        }
    }

    private static void RunSelectionArrowMoveOnlyRegression(MainForm form, VectorScene scene, StageControl stage)
    {
        var activate = RequireMethod(typeof(MainForm), "ActivateTool", [typeof(ToolMode)]);
        var setSelection = RequireMethod(typeof(MainForm), "SetSelection", [typeof(DrawingElementHit), typeof(bool)]);
        var addSelection = RequireMethod(typeof(MainForm), "AddToSelection", [typeof(DrawingElementHit), typeof(bool)]);
        var clearSelection = RequireMethod(typeof(MainForm), "ClearSelection", [typeof(bool)]);
        var moveTarget = RequireMethod(typeof(MainForm), "TryGetObjectArrowMoveTarget");
        var mouseDown = RequireMethod(typeof(MainForm), "StageMouseDown");
        var mouseMove = RequireMethod(typeof(MainForm), "StageMouseMove");
        var mouseUp = RequireMethod(typeof(MainForm), "StageMouseUp");
        foreach (var scenario in new[] { "fill-edge", "fill-corner", "stroke-body", "stroke-endpoint" })
        {
            clearSelection.Invoke(form, [false]);
            scene.CreateEmpty();
            var stroke = scenario.StartsWith("stroke", StringComparison.Ordinal);
            var primary = stroke
                ? scene.AddLineSegment(0, new PointF(-1000, 0), new PointF(1000, 0),
                    25, Color.Transparent, Color.White, 8)
                : scene.AddObject(0, PointF.Empty, new SizeF(2000, 1600), 0, 25,
                    Color.Teal, Color.White, 16, ShapeKind.Rectangle);
            var secondary = scene.AddObject(0, new PointF(5000, 0), new SizeF(500, 500),
                0, 0, Color.Coral, 8, ShapeKind.Ellipse);
            scene.CompleteDeferredBuild();
            activate.Invoke(form, [ToolMode.ObjectSelect]);
            var primaryHit = new DrawingElementHit(new DrawingElementKey(primary,
                stroke ? DrawingElementKind.Stroke : DrawingElementKind.Fill, 0), 0, 0, 1);
            setSelection.Invoke(form, [primaryHit, false]);
            addSelection.Invoke(form, [new DrawingElementHit(
                new DrawingElementKey(secondary, DrawingElementKind.Fill, 0), 0, 0, 1), false]);
            var world = scenario switch
            {
                "fill-edge" => new PointF(1000, 0),
                "fill-corner" => new PointF(1000, 800),
                "stroke-endpoint" => new PointF(-1000, 0),
                _ => PointF.Empty
            };
            var screen = Point.Round(stage.WorldToScreen(world.X, world.Y));
            object?[] arguments = [screen, -1];
            AssertTimeline((bool)moveTarget.Invoke(form, arguments)! && (int)arguments[1]! == primary,
                $"The stippled {scenario} did not resolve to its existing move selection.");
            mouseDown.Invoke(form, [stage, new MouseEventArgs(MouseButtons.Left, 1, screen.X, screen.Y, 0)]);
            AssertTimeline(stage.SelectedElements.Count == 2
                && (EditHandleKind)RequireField(typeof(MainForm), "_activeHandle").GetValue(form)! == EditHandleKind.None
                && RequireField(typeof(MainForm), "_arcDragSession").GetValue(form) is null
                && RequireField(typeof(MainForm), "_fillEdgePreSelect").GetValue(form) is null
                && RequireField(typeof(MainForm), "_fillEdgeBezierArmedSession").GetValue(form) is null,
                $"Pressing the selected {scenario} narrowed the selection or armed a shape edit.");
            RequireMethod(typeof(MainForm), "UpdateInteractionCursor").Invoke(form, [screen]);
            AssertTimeline(stage.Cursor == stage.SelectionArrowCursorFor(false, StageControl.SelectionArrowCursorFeedback.Move),
                $"The selected {scenario} advertised editing instead of movement.");
            var target = new Point(screen.X + 40, screen.Y + 30);
            mouseMove.Invoke(form, [stage, new MouseEventArgs(MouseButtons.Left, 0, target.X, target.Y, 0)]);
            RequireMethod(typeof(MainForm), "FlushLineDragPreview").Invoke(form, null);
            AssertTimeline(stage.SelectedObjects.Count == 2,
                $"Dragging the selected {scenario} dropped another selected region.");
            var movedPrimary = stage.SelectedObject;
            var movedSecondary = stage.SelectedObjects.Single(index => index != movedPrimary);
            AssertTimeline(Math.Abs(scene.X[movedPrimary] - (scene.X[movedSecondary] - 5000)) < 0.01f
                && Math.Abs(scene.Y[movedPrimary] - scene.Y[movedSecondary]) < 0.01f,
                $"Dragging the selected {scenario} did not translate both selected regions together.");
            if (stroke)
                AssertTimeline(scene.IsLineStraight(movedPrimary) && Math.Abs(scene.Width[movedPrimary] - 2000) < 0.01f,
                    "Dragging a selected endpoint changed the stroke shape or length.");
            mouseUp.Invoke(form, [stage, new MouseEventArgs(MouseButtons.Left, 1, target.X, target.Y, 0)]);
            activate.Invoke(form, [ToolMode.Select]);
            arguments = [target, -1];
            AssertTimeline(!(bool)moveTarget.Invoke(form, arguments)!,
                "The black-arrow move-only rule leaked into white-arrow geometry editing.");
        }
    }

    private static void RunSelectionMoveFeedbackRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty(2, 1);
        var behind = scene.AddObject(1, PointF.Empty, new SizeF(3000, 2000), 0, 0,
            Color.Blue, 16, ShapeKind.Rectangle);
        var moving = scene.AddObject(1, new PointF(-2500, 0), new SizeF(1000, 1000), 0, 0,
            Color.Red, 16, ShapeKind.Rectangle);
        var above = scene.AddObject(0, PointF.Empty, new SizeF(3000, 2000), 0, 0,
            Color.Blue, 16, ShapeKind.Rectangle);
        var sameLayerAbove = scene.AddObject(1, PointF.Empty, new SizeF(3000, 2000), 0, 0,
            Color.Blue, 16, ShapeKind.Rectangle);
        scene.CompleteDeferredBuild();
        using var stage = new StageControl(scene) { Size = new Size(600, 400) };
        stage.ConfigureSelectionArrows(new SelectionArrowSettings(), true);
        stage.SetSelection([moving]);
        var order = scene.ObjectOrder[moving];
        var color = scene.Argb[moving];
        stage.BeginSelectionMoveFeedback([moving]);
        scene.X[moving] = 0;
        stage.Invalidate();
        AssertTimeline(stage.SelectionMoveOrigins[moving].X[0] == -2500
            && stage.SelectionMoveOrigins[moving].Argb[0] == color
            && scene.ObjectOrder[moving] == order && scene.Argb[moving] == color,
            "Move feedback changed artwork or failed to freeze the original appearance.");
        AssertTimeline(Covers(above, new PointF(0, 0)) && !Covers(above, new PointF(1400, 0))
            && Covers(sameLayerAbove, PointF.Empty)
            && stage.GetSelectionMoveOcclusionContours(behind, SceneRenderPass.Fill).Length == 0
            && stage.GetSelectionMoveOcclusionContours(moving, SceneRenderPass.Fill).Length == 0,
            "Move feedback faded a lower/selected object or extended beyond the moved paint.");
        using (var occluder = Capture("DrawObject", above, SceneRenderPass.Fill))
        {
            var covered = Sample(occluder, PointF.Empty);
            var untouched = Sample(occluder, new PointF(1200, 0));
            AssertTimeline(Math.Abs(covered.A - 255 * StageControl.SelectionMoveOccluderOpacity) <= 1
                && untouched.ToArgb() == Color.Blue.ToArgb()
                && covered.ToArgb() == RasterizedColor(Color.Blue, covered.A).ToArgb(),
                $"GDI did not fade only the covered portion of the upper object: covered={covered}, untouched={untouched}, opacity={StageControl.SelectionMoveOccluderOpacity}.");
        }
        using (var origin = Capture("DrawSelectionMoveOrigins"))
        {
            var reference = Sample(origin, new PointF(-2500, 0));
            AssertTimeline(Math.Abs(reference.A - 255 * StageControl.SelectionMoveOriginOpacity) <= 1
                && reference.ToArgb() == RasterizedColor(Color.Red, reference.A).ToArgb()
                && Sample(origin, PointF.Empty).A == 0,
                "The origin reference moved, accumulated selection highlights or lost its plain translucent paint.");
        }
        scene.X[moving] = 4000;
        stage.Invalidate();
        AssertTimeline(stage.GetSelectionMoveOcclusionContours(above, SceneRenderPass.Fill).Length == 0,
            "Moving away retained the previous occlusion mask.");
        PointF[][] ringContours =
        [
            [new(-1000, -1000), new(1000, -1000), new(1000, 1000), new(-1000, 1000)],
            [new(-250, -250), new(250, -250), new(250, 250), new(-250, 250)]
        ];
        var ring = scene.AddPathObjectContours(1, ringContours, 0, Color.Red, Color.Transparent, 16);
        scene.CompleteDeferredBuild();
        stage.SetSelection([ring]);
        stage.BeginSelectionMoveFeedback([ring]);
        AssertTimeline(Covers(above, new PointF(500, 0)) && !Covers(above, PointF.Empty),
            "Move feedback faded upper artwork through a hole in the moving fill.");
        var maskScene = new VectorScene();
        maskScene.CreateEmpty();
        maskScene.AddObject(0, new PointF(500, 0), new SizeF(1000, 2000), 0, 0,
            Color.White, 16, ShapeKind.Rectangle);
        maskScene.CompleteDeferredBuild();
        stage.SetSceneCompositionMaskClips([new SceneCompositionMaskClip(scene, maskScene, 0, new HashSet<int> { ring })]);
        stage.BeginSelectionMoveFeedback([ring]);
        AssertTimeline(Covers(above, new PointF(500, 0)) && !Covers(above, new PointF(-500, 0)),
            "Move feedback exposed artwork outside the moving object's composition mask.");
        using (var maskedOrigin = Capture("DrawSelectionMoveOrigins"))
            AssertTimeline(Sample(maskedOrigin, new PointF(500, 0)).A > 0
                && Sample(maskedOrigin, new PointF(-500, 0)).A == 0,
                "The origin reference lost its captured composition mask.");
        stage.Frame = 1;
        AssertTimeline(!stage.SelectionMoveFeedbackActive && stage.SelectionMoveOrigins.Count == 0,
            "Frame changes retained a move origin or faded artwork.");
        stage.BeginSelectionMoveFeedback([ring]);
        stage.ConfigureSelectionArrows(new SelectionArrowSettings(), false);
        AssertTimeline(!stage.SelectionMoveFeedbackActive && stage.SelectionMoveOrigins.Count == 0,
            "Switching arrow roles retained move feedback.");
        stage.ConfigureSelectionArrows(new SelectionArrowSettings(), true);
        stage.BeginSelectionMoveFeedback([ring]);
        stage.BindScene(scene);
        AssertTimeline(!stage.SelectionMoveFeedbackActive && stage.SelectionMoveOrigins.Count == 0,
            "Rebinding a scene retained transient move feedback.");
        Console.WriteLine("selection_move_feedback=ok,origin=plain-translucent,occlusion=overlap-only,stacking=preserved");

        bool Covers(int objectIndex, PointF point)
        {
            using var path = new System.Drawing.Drawing2D.GraphicsPath(System.Drawing.Drawing2D.FillMode.Alternate);
            foreach (var contour in stage.GetSelectionMoveOcclusionContours(objectIndex, SceneRenderPass.Fill))
                if (contour.Length >= 3) path.AddPolygon(contour);
            return path.IsVisible(point);
        }

        Bitmap Capture(string method, params object[] arguments)
        {
            var bitmap = new Bitmap(stage.Width, stage.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.Transparent);
            RequireMethod(typeof(StageControl), method).Invoke(stage, new object[] { graphics }.Concat(arguments).ToArray());
            return bitmap;
        }

        Color Sample(Bitmap bitmap, PointF world)
        {
            var pixel = Point.Round(stage.WorldToScreen(world.X, world.Y));
            return bitmap.GetPixel(pixel.X, pixel.Y);
        }

        static Color RasterizedColor(Color color, int alpha)
        {
            using var bitmap = new Bitmap(1, 1, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.FromArgb(alpha, color));
            return bitmap.GetPixel(0, 0);
        }
    }

    private static void RunSelectionStippleOverlapRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        var first = scene.AddLineSegment(0, new PointF(-1600, 0), new PointF(1600, 0),
            200, Color.Transparent, Color.White, 8);
        var duplicate = scene.AddLineSegment(0, new PointF(-1600, 0), new PointF(1600, 0),
            200, Color.Transparent, Color.White, 8);
        var contained = scene.AddLineSegment(0, new PointF(-603, 0), new PointF(707, 0),
            200, Color.Transparent, Color.White, 8);
        using var stage = new StageControl(scene) { Size = new Size(400, 300) };
        stage.ConfigureSelectionArrows(new SelectionArrowSettings(), true);
        stage.SetSelection([first]);
        using var single = Capture();
        stage.SetSelection([first, duplicate, contained]);
        using var overlapped = Capture();
        for (var row = 0; row < stage.Height; row++)
            for (var column = 0; column < stage.Width; column++)
                AssertTimeline(single.GetPixel(column, row).ToArgb() == overlapped.GetPixel(column, row).ToArgb(),
                    "Coincident strokes or contained endpoints changed stipple phase, opacity or coverage.");
        var cached = stage.GetObjectArrowStippleMask();
        AssertTimeline(ReferenceEquals(cached.Contours, stage.GetObjectArrowStippleMask().Contours),
            "Unchanged stipple coverage did not reuse its prepared mask.");
        var crossing = scene.AddLineSegment(0, new PointF(0, -1000), new PointF(0, 1000),
            200, Color.Transparent, Color.White, 8);
        stage.SetSelection([first, duplicate, contained, crossing]);
        using var crossed = Capture();
        AssertSingleOpacity(crossed);
        stage.SetSelection([crossing, contained, duplicate, first]);
        using var reordered = Capture();
        for (var row = 0; row < stage.Height; row++)
            for (var column = 0; column < stage.Width; column++)
                AssertTimeline(crossed.GetPixel(column, row).ToArgb() == reordered.GetPixel(column, row).ToArgb(),
                    "Stipple overlap depended on the order of the selected strokes.");

        scene.CreateEmpty();
        PointF[][] contours =
        [
            [new(-1000, -1000), new(1000, -1000), new(1000, 1000), new(-1000, 1000)],
            [new(-250, -250), new(250, -250), new(250, 250), new(-250, 250)]
        ];
        var ring = scene.AddPathObjectContours(0, contours, 0, Color.FromArgb(128, Color.Teal), Color.Transparent, 16);
        var ringCopy = scene.AddPathObjectContours(0, contours, 0, Color.FromArgb(128, Color.Teal), Color.Transparent, 16);
        stage.SetSelection([ring, ringCopy]);
        var mask = stage.GetObjectArrowStippleMask();
        using var coverage = new System.Drawing.Drawing2D.GraphicsPath(System.Drawing.Drawing2D.FillMode.Alternate);
        foreach (var contour in mask.Contours)
            if (contour.Length >= 3) coverage.AddPolygon(contour);
        AssertTimeline(coverage.IsVisible(500, 0) && !coverage.IsVisible(0, 0),
            "Merging overlapping stipple fills cancelled their paint or filled a hole.");
        using var rings = Capture();
        AssertSingleOpacity(rings);
        Console.WriteLine("selection_stipple_overlap=ok,duplicates=idempotent,crossings=single-pass,holes=preserved");

        Bitmap Capture()
        {
            var bitmap = new Bitmap(stage.Width, stage.Height);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.Transparent);
            RequireMethod(typeof(StageControl), "DrawObjectArrowSelection").Invoke(stage, [graphics]);
            return bitmap;
        }

        static void AssertSingleOpacity(Bitmap bitmap)
        {
            var visible = 0;
            for (var row = 0; row < bitmap.Height; row++)
                for (var column = 0; column < bitmap.Width; column++)
                {
                    var alpha = bitmap.GetPixel(column, row).A;
                    AssertTimeline(alpha <= 190, "Overlapping selection coverage accumulated stipple alpha.");
                    if (alpha > 0) visible++;
                }
            AssertTimeline(visible > 0, "Stipple overlap validation rendered an empty selection.");
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
