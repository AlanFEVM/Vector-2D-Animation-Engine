namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunSelectionPickingRegression()
    {
        RunSelectionPickingGeometryRegression();
        RunSelectionPickingVisibilityRegression();
        RunSelectionPickingPointerRegression();
        Console.WriteLine("selection_picking_regression=ok,ordinary=preserved,visible_fill=nearest,pointer=white_arrow");
    }

    private static int AddSelectionPickingRectangle(
        VectorScene scene, int layer, PointF center, SizeF size, float stroke = 0)
    {
        return scene.AddObject(layer, center, size, 0, stroke,
            Color.Coral, Color.Navy, 16, ShapeKind.Rectangle);
    }

    private static void RunSelectionPickingGeometryRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty(2);
        AddSelectionPickingRectangle(scene, 1, PointF.Empty, new SizeF(1000, 1000));
        var upper = AddSelectionPickingRectangle(scene, 0, PointF.Empty, new SizeF(800, 800));
        var ordinary = scene.HitTestElement(PointF.Empty, 0, 50);
        AssertTimeline(ordinary.Key.ObjectIndex == upper
            && MainForm.HitTestSelectionArrow(scene, PointF.Empty, 0, 50) == ordinary,
            "Selection picking replaced a normal upper-layer fill hit.");
        var latest = AddSelectionPickingRectangle(scene, 0, PointF.Empty, new SizeF(600, 600));
        ordinary = scene.HitTestElement(PointF.Empty, 0, 50);
        AssertTimeline(ordinary.Key.ObjectIndex == latest
            && MainForm.HitTestSelectionArrow(scene, PointF.Empty, 0, 50) == ordinary,
            "Selection picking changed ordinary same-layer paint order.");

        scene.CreateEmpty();
        var filled = AddSelectionPickingRectangle(scene, 0, PointF.Empty, new SizeF(1000, 1000));
        var outside = new PointF(525, 0);
        AssertTimeline(!scene.HitTestElement(outside, 0, 50).IsValid,
            "The fill-neighbor fixture unexpectedly received an ordinary hit.");
        var hit = MainForm.HitTestSelectionArrow(scene, outside, 0, 50);
        AssertTimeline(hit.Key.ObjectIndex == filled && hit.Key.Kind == DrawingElementKind.Fill
            && Math.Abs(hit.Distance - 25) < 0.01f,
            "Selection picking did not measure the actual fill edge of a normal-sized object.");
        hit = MainForm.HitTestSelectionArrow(scene, new PointF(550, 0), 0, 50);
        AssertTimeline(hit.Key.ObjectIndex == filled && hit.Distance == 50,
            "Selection picking rejected a fill exactly at its tolerance boundary.");
        AssertTimeline(!MainForm.HitTestSelectionArrow(scene, new PointF(540, 540), 0, 50).IsValid
            && !MainForm.HitTestSelectionArrow(scene, new PointF(551, 0), 0, 50).IsValid
            && !MainForm.HitTestSelectionArrow(scene, outside, 0, 0).IsValid,
            "Selection picking used an inflated box instead of contour distance, or ignored its tolerance.");
        var stroked = AddSelectionPickingRectangle(scene, 0, new PointF(3000, 0), new SizeF(1000, 1000), 50);
        var strokePoint = new PointF(3525, 0);
        ordinary = scene.HitTestElement(strokePoint, 0, 50);
        AssertTimeline(ordinary.Key.ObjectIndex == stroked && ordinary.Key.Kind == DrawingElementKind.BoundaryStroke
            && MainForm.HitTestSelectionArrow(scene, strokePoint, 0, 50) == ordinary,
            "Selection picking replaced an ordinary boundary stroke with a fill.");

        scene.CreateEmpty();
        var thin = AddSelectionPickingRectangle(scene, 0, PointF.Empty, new SizeF(2500, 50));
        var thinPoint = new PointF(0, -100);
        hit = MainForm.HitTestSelectionArrow(scene, thinPoint, 0, 2500);
        AssertTimeline(!scene.HitTestElement(thinPoint, 0, 2500).IsValid
            && hit.Key.ObjectIndex == thin && hit.Key.Kind == DrawingElementKind.Fill
            && Math.Abs(hit.Distance - 75) < 0.01f,
            "Selection picking missed the subpixel-fill fixture.");

        scene.CreateEmpty();
        var triangle = scene.AddPathObjectContours(0,
            [[new(0, 0), new(1000, 0), new(0, 1000)]], 0, Color.Coral, Color.Transparent, 16);
        AssertTimeline(!MainForm.HitTestSelectionArrow(scene, new PointF(900, 900), 0, 50).IsValid,
            "Selection picking accepted empty space inside a triangle's bounding box.");
        hit = MainForm.HitTestSelectionArrow(scene, new PointF(-20, 500), 0, 50);
        AssertTimeline(hit.Key.ObjectIndex == triangle && hit.Key.Kind == DrawingElementKind.Fill
            && Math.Abs(hit.Distance - 20) < 0.01f,
            "Selection picking ignored the closing edge of a fill contour.");

        scene.CreateEmpty();
        var ring = scene.AddPathObjectContours(0,
            [
                [new(-1000, -1000), new(1000, -1000), new(1000, 1000), new(-1000, 1000)],
                [new(-600, -600), new(600, -600), new(600, 600), new(-600, 600)]
            ], 0, Color.Coral, Color.Transparent, 16);
        AssertTimeline(!MainForm.HitTestSelectionArrow(scene, PointF.Empty, 0, 50).IsValid,
            "Selection picking selected a fill from deep inside its hole.");
        hit = MainForm.HitTestSelectionArrow(scene, new PointF(575, 0), 0, 50);
        AssertTimeline(hit.Key.ObjectIndex == ring && hit.Key.Kind == DrawingElementKind.Fill
            && Math.Abs(hit.Distance - 25) < 0.01f,
            "Selection picking rejected a hole edge inside the configured tolerance.");

        scene.CreateEmpty();
        var islands = scene.AddPathObjectContours(0,
            [
                [new(-1100, -100), new(-900, -100), new(-900, 100), new(-1100, 100)],
                [new(900, -100), new(1100, -100), new(1100, 100), new(900, 100)]
            ], 0, Color.Coral, Color.Transparent, 16);
        var parts = scene.GetFillParts(islands, 0);
        var rightPart = parts.Single(part => part.Contours.SelectMany(contour => contour).All(point => point.X > 0));
        hit = MainForm.HitTestSelectionArrow(scene, new PointF(1125, 0), 0, 50);
        AssertTimeline(parts.Length == 2 && hit.Key.ObjectIndex == islands
            && hit.Key.PartIndex == rightPart.PartIndex && hit.Key.Kind == DrawingElementKind.Fill,
            "Selection picking lost the identity of a disconnected fill part.");
        AssertTimeline(!MainForm.HitTestSelectionArrow(scene, new PointF(float.NaN, 0), 0, 50).IsValid
            && !MainForm.HitTestSelectionArrow(scene, PointF.Empty, 0, float.PositiveInfinity).IsValid,
            "Selection picking accepted non-finite input.");
    }

    private static void RunSelectionPickingVisibilityRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty(2);
        var target = AddSelectionPickingRectangle(scene, 1, PointF.Empty, new SizeF(1000, 1000));
        var point = new PointF(550, 0);
        scene.SetLayerLocked(1, true);
        AssertTimeline(!MainForm.HitTestSelectionArrow(scene, point, 0, 100).IsValid,
            "Selection picking selected a locked layer.");
        scene.SetLayerLocked(1, false);
        scene.SetLayerVisible(1, false);
        AssertTimeline(!MainForm.HitTestSelectionArrow(scene, point, 0, 100).IsValid,
            "Selection picking selected a hidden layer.");
        scene.SetLayerVisible(1, true);
        scene.SetLayerOutline(1, true);
        AssertTimeline(!MainForm.HitTestSelectionArrow(scene, point, 0, 100).IsValid,
            "Selection picking compensated for a fill that is only displayed as an outline.");
        scene.SetLayerOutline(1, false);
        scene.LayerOpacity[1] = 0;
        AssertTimeline(!MainForm.HitTestSelectionArrow(scene, point, 0, 100).IsValid,
            "Selection picking selected an invisible zero-opacity fill.");
        scene.LayerOpacity[1] = 1;
        var cover = AddSelectionPickingRectangle(scene, 0, PointF.Empty, new SizeF(1200, 1200));
        scene.SetLayerLocked(0, true);
        AssertTimeline(!MainForm.HitTestSelectionArrow(scene, new PointF(650, 0), 0, 200).IsValid,
            "Selection picking reached through an opaque locked cover to a fully hidden fill.");
        scene.SetLayerVisible(0, false);
        var hit = MainForm.HitTestSelectionArrow(scene, point, 0, 100);
        AssertTimeline(hit.Key.ObjectIndex == target && hit.Key.Kind == DrawingElementKind.Fill,
            "A hidden cover still occluded a visible fill during compensated selection.");
        scene.SetLayerVisible(0, true);
        scene.Argb[cover] = Color.FromArgb(128, Color.Coral).ToArgb();
        hit = MainForm.HitTestSelectionArrow(scene, new PointF(650, 0), 0, 200);
        AssertTimeline(hit.Key.ObjectIndex == target && hit.Key.Kind == DrawingElementKind.Fill,
            "A translucent locked cover incorrectly erased the fill below it.");

        scene.CreateEmpty(2);
        target = AddSelectionPickingRectangle(scene, 1, PointF.Empty, new SizeF(1000, 1000));
        AddSelectionPickingRectangle(scene, 0, new PointF(-250, 0), new SizeF(500, 1000));
        scene.SetLayerLocked(0, true);
        AssertTimeline(!MainForm.HitTestSelectionArrow(scene, new PointF(-550, 0), 0, 100).IsValid,
            "Selection picking measured a hidden edge instead of the remaining visible fill.");
        hit = MainForm.HitTestSelectionArrow(scene, point, 0, 100);
        AssertTimeline(hit.Key.ObjectIndex == target && hit.Key.Kind == DrawingElementKind.Fill,
            "Selection picking rejected the exposed side of a partially covered fill.");

        scene.CreateEmpty(2);
        var farther = AddSelectionPickingRectangle(scene, 0, new PointF(-150, 0), new SizeF(100, 100));
        var nearer = AddSelectionPickingRectangle(scene, 1, new PointF(100, 0), new SizeF(100, 100));
        hit = MainForm.HitTestSelectionArrow(scene, PointF.Empty, 0, 150);
        AssertTimeline(hit.Key.ObjectIndex == nearer && hit.Key.ObjectIndex != farther,
            "Compensated selection did not choose the nearest visible fill.");
        scene.TransformObjects([farther], position => new PointF(position.X + 50, position.Y));
        hit = MainForm.HitTestSelectionArrow(scene, PointF.Empty, 0, 150);
        AssertTimeline(hit.Key.ObjectIndex == farther,
            "Equal-distance compensated selection lost upper-layer priority.");
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetKeyboardState", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SelectionPickingGetKeyboardState([System.Runtime.InteropServices.Out] byte[] state);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetKeyboardState", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SelectionPickingSetKeyboardState(byte[] state);

    private static void RunSelectionPickingPointerRegression()
    {
        var keyboardState = new byte[256];
        if (!SelectionPickingGetKeyboardState(keyboardState))
            throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        try
        {
            var modifiers = keyboardState.ToArray();
            foreach (var virtualKey in new[] { 0x10, 0x11, 0x12, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5 })
                modifiers[virtualKey] = 0;
            SetModifiers(false);
            using var form = new MainForm
            {
                ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
                Location = new Point(-30000, -30000), Size = new Size(1280, 800)
            };
            form.CreateControl();
            form.PerformLayout();
            var tabs = (WorkspaceTabs)RequireField(typeof(MainForm), "_workspaceTabs").GetValue(form)!;
            tabs.SelectedView = WorkspaceView.BasicDrawing;
            var settingsField = RequireField(typeof(MainForm), "_applicationSettings");
            var settings = (ApplicationSettings)settingsField.GetValue(form)!;
            settingsField.SetValue(form, settings with { SelectionArrows = new SelectionArrowSettings() });
            var scene = (VectorScene)RequireField(typeof(MainForm), "_scene").GetValue(form)!;
            var stage = (StageControl)RequireField(typeof(MainForm), "_stage").GetValue(form)!;
            stage.Size = new Size(800, 600);
            stage.CreateControl();
            var activate = RequireMethod(typeof(MainForm), "ActivateTool", [typeof(ToolMode)]);
            var clear = RequireMethod(typeof(MainForm), "ClearSelection", [typeof(bool)]);
            var mouseDown = RequireMethod(typeof(MainForm), "StageMouseDown");
            var mouseUp = RequireMethod(typeof(MainForm), "StageMouseUp");
            activate.Invoke(form, [ToolMode.Select]);

            foreach (var zoom in new[] { 0.02f, 0.1f, 0.25f, 1f, 4f, 16f, 64f })
            {
                foreach (var camera in new[] { PointF.Empty, new PointF(125, -100) })
                {
                    Reset(zoom, camera);
                    var filled = AddSelectionPickingRectangle(scene, 0, PointF.Empty, new SizeF(30000, 30000));
                    Click(Point.Round(stage.WorldToScreen(0, 0)));
                    AssertSelection(filled, DrawingElementKind.Fill, $"center/zoom={zoom}/camera={camera}");

                    Reset(zoom, camera);
                    var tiny = AddSelectionPickingRectangle(scene, 0, PointF.Empty, new SizeF(2500, 50));
                    Click(Point.Round(stage.WorldToScreen(0, 0)));
                    AssertSelection(tiny, DrawingElementKind.Fill, $"thin-center/zoom={zoom}/camera={camera}");

                    Reset(zoom, camera);
                    var thin = AddSelectionPickingRectangle(scene, 0, PointF.Empty, new SizeF(2500, 50));
                    var center = stage.WorldToScreen(0, 0);
                    var nearby = new Point((int)MathF.Round(center.X),
                        (int)MathF.Ceiling(center.Y + stage.WorldLengthToScreen(25)) + 2);
                    AssertTimeline(!scene.HitTestElement(stage.ScreenToWorld(nearby), 0, stage.ScreenLengthToWorld(10)).IsValid,
                        "The thin-fill pointer fixture did not exercise compensation.");
                    Click(nearby);
                    AssertSelection(thin, DrawingElementKind.Fill, $"thin/zoom={zoom}/camera={camera}");
                }
            }

            Reset(0.1f, new PointF(125, -100));
            stage.RestoreViewState(stage.CaptureViewState() with
            {
                CameraY = (stage.Height * 0.5f - MathF.Floor(stage.Height * 0.5f) - 0.375f)
                    * stage.ScreenLengthToWorld(1)
            });
            var subpixel = AddSelectionPickingRectangle(scene, 0, PointF.Empty, new SizeF(2500, 50));
            var subpixelScreen = Point.Round(stage.WorldToScreen(0, 0));
            AssertTimeline(!scene.HitTestElement(stage.ScreenToWorld(subpixelScreen), 0,
                stage.ScreenLengthToWorld(10)).IsValid,
                "The rounded-center pointer fixture unexpectedly fell inside the subpixel fill.");
            Click(subpixelScreen, new Point(subpixelScreen.X + 2, subpixelScreen.Y + 1));
            AssertSelection(subpixel, DrawingElementKind.Fill, "subpixel/rounded-center/small-release-jitter");

            Reset(0.1f, new PointF(125, -100));
            var first = AddSelectionPickingRectangle(scene, 0, new PointF(-6000, 0), new SizeF(2500, 50));
            var second = AddSelectionPickingRectangle(scene, 0, new PointF(6000, 0), new SizeF(2500, 50));
            Click(Point.Round(stage.WorldToScreen(-6000, 0)));
            AssertSelection(first, DrawingElementKind.Fill, "shift/base");
            SetModifiers(true);
            Click(Point.Round(stage.WorldToScreen(6000, 0)));
            SetModifiers(false);
            AssertTimeline(stage.SelectedObjects.Order().SequenceEqual(new[] { first, second }.Order())
                && stage.SelectedElement.Key.ObjectIndex == second
                && stage.SelectedElement.Key.Kind == DrawingElementKind.Fill
                && stage.SelectedElements.Count == 2
                && stage.SelectedElements.All(hit => hit.Key.Kind == DrawingElementKind.Fill),
                "Shift compensated click did not append a fill without replacing the existing selection.");

            Reset(1, PointF.Empty);
            var stroked = AddSelectionPickingRectangle(scene, 0, PointF.Empty, new SizeF(10000, 10000), 50);
            Click(Point.Round(stage.WorldToScreen(5000, 0)));
            AssertSelection(stroked, DrawingElementKind.BoundaryStroke, "ordinary/stroke");

            Reset(1, PointF.Empty);
            var ring = scene.AddPathObjectContours(0,
                [
                    [new(-10000, -10000), new(10000, -10000), new(10000, 10000), new(-10000, 10000)],
                    [new(-3000, -3000), new(3000, -3000), new(3000, 3000), new(-3000, 3000)]
                ], 0, Color.Coral, Color.Transparent, 16);
            Click(Point.Round(stage.WorldToScreen(0, 0)));
            AssertTimeline(stage.SelectedObjects.Count == 0 && !stage.SelectedElement.IsValid,
                "Actual white-arrow pointer selection picked the deep interior of a hole.");
            Click(Point.Round(stage.WorldToScreen(2900, 0)));
            AssertSelection(ring, DrawingElementKind.Fill, "hole/near-inner-edge");

            Reset(1, PointF.Empty);
            AddSelectionPickingRectangle(scene, 0, PointF.Empty, new SizeF(1000, 1000));
            Click(Point.Round(stage.WorldToScreen(775, 0)));
            AssertTimeline(stage.SelectedObjects.Count == 0 && !stage.SelectedElement.IsValid,
                "Actual white-arrow pointer selection exceeded the ten-pixel tolerance.");

            Reset(0.1f, new PointF(125, -100));
            AddSelectionPickingRectangle(scene, 0, PointF.Empty, new SizeF(2500, 50));
            scene.SetLayerLocked(0, true);
            Click(Point.Round(stage.WorldToScreen(0, 0)));
            AssertTimeline(stage.SelectedObjects.Count == 0 && !stage.SelectedElement.IsValid,
                "Actual white-arrow pointer selection picked a locked fill.");
            scene.SetLayerLocked(0, false);
            scene.SetLayerVisible(0, false);
            Click(Point.Round(stage.WorldToScreen(0, 0)));
            AssertTimeline(stage.SelectedObjects.Count == 0 && !stage.SelectedElement.IsValid,
                "Actual white-arrow pointer selection picked a hidden fill.");

            void Reset(float zoom, PointF camera)
            {
                clear.Invoke(form, [false]);
                scene.CreateEmpty();
                stage.RestoreViewState(stage.CaptureViewState() with
                {
                    CameraX = camera.X, CameraY = camera.Y, Zoom = zoom
                });
            }

            void Click(Point screen, Point? release = null)
            {
                SetModifiers((modifiers[0x10] & 0x80) != 0);
                var revision = scene.GeometryRevision;
                var objectCount = scene.ObjectCount;
                var undo = RequireField(typeof(MainForm), "_undoStack").GetValue(form)!;
                var countProperty = undo.GetType().GetProperty("Count")!;
                var undoCount = countProperty.GetValue(undo);
                var mouse = new MouseEventArgs(MouseButtons.Left, 1, screen.X, screen.Y, 0);
                mouseDown.Invoke(form, [stage, mouse]);
                var end = release ?? screen;
                mouseUp.Invoke(form, [stage, new MouseEventArgs(MouseButtons.Left, 1, end.X, end.Y, 0)]);
                AssertTimeline(scene.GeometryRevision == revision && scene.ObjectCount == objectCount
                    && Equals(undoCount, countProperty.GetValue(undo)) && !stage.Capture
                    && RequireField(typeof(MainForm), "_arcDragSession").GetValue(form) is null
                    && RequireField(typeof(MainForm), "_fillEdgeNoUiArc").GetValue(form) is null
                    && RequireField(typeof(MainForm), "_fillEdgeDetachMoving").GetValue(form) is null,
                    "A selection click changed geometry/history or retained a deformation session.");
            }

            void AssertSelection(int objectIndex, DrawingElementKind kind, string scenario)
            {
                AssertTimeline(stage.SelectedObjects.SequenceEqual([objectIndex])
                    && stage.SelectedElement.Key.ObjectIndex == objectIndex && stage.SelectedElement.Key.Kind == kind
                    && stage.SelectedElements.Count == 1 && !stage.SelectionFeedbackSuppressed,
                    $"White-arrow picking failed: {scenario}, objects={string.Join(',', stage.SelectedObjects)}, "
                    + $"element={stage.SelectedElement.Key.Kind}, zoom={stage.Zoom}.");
            }

            void SetModifiers(bool shift)
            {
                modifiers[0x10] = modifiers[0xA0] = shift ? (byte)0x80 : (byte)0;
                if (!SelectionPickingSetKeyboardState(modifiers))
                    throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            if (!SelectionPickingSetKeyboardState(keyboardState))
                throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        }
    }
}
