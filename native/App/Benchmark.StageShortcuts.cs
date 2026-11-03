namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunStageInstanceShortcutRegression()
    {
        using var form = CreateOffscreenSnapPointForm();
        var type = typeof(MainForm);
        var project = (VectorProject)RequireField(type, "_project").GetValue(form)!;
        var scene = project.Scenes[0];
        scene.Dimension = SceneDimension.TwoD;
        var source = project.DrawingObjects[0];
        source.Scene.CreateEmpty();
        source.Scene.AddObject(0, PointF.Empty, new SizeF(200, 150), 0, 0,
            Color.Teal, Color.Transparent, 4, ShapeKind.Rectangle);
        if (!project.TryAddSceneInstance(scene.Id, source.Id, PointF.Empty, 0, scene.Layers[0].Id, out var original)
            || original is null) throw new InvalidOperationException("Instance shortcut fixture could not create an instance.");
        ((WorkspaceTabs)RequireField(type, "_workspaceTabs").GetValue(form)!).SelectedView = WorkspaceView.SceneEditor;
        form.Show();
        Application.DoEvents();
        RequireMethod(type, "BindSceneEditStage", [typeof(bool)]).Invoke(form, [true]);
        var select = RequireMethod(type, "SetSceneInstanceSelection",
            [typeof(IEnumerable<DrawingObjectInstanceDefinition>), typeof(DrawingObjectInstanceDefinition)]);
        var undo = RequireMethod(type, "UndoLastEdit", Type.EmptyTypes);
        var redo = RequireMethod(type, "RedoLastEdit", Type.EmptyTypes);
        var begin = RequireMethod(type, "BeginSpatialTransformEdit");
        var duplicate = RequireMethod(type, "DuplicateProjectedSceneDrag");
        try
        {
            var definitionCount = project.DrawingObjects.Count;
            select.Invoke(form, [new[] { original }, original]);
            begin.Invoke(form, [true]);
            if (duplicate.Invoke(form, null) is not true || scene.Instances.Count != 2
                || project.DrawingObjects.Count != definitionCount + 1)
                throw new InvalidOperationException("Scene drag duplication did not create an independent copy.");
            RequireMethod(type, "CancelSpatialTransformEdit").Invoke(form, null);
            if (scene.Instances.Count != 1 || project.DrawingObjects.Count != definitionCount)
                throw new InvalidOperationException("Cancelled scene drag left a copied instance or source Symbol.");
            original = scene.Instances.Single();
            select.Invoke(form, [new[] { original }, original]);
            begin.Invoke(form, [true]);
            if (duplicate.Invoke(form, null) is not true) throw new InvalidOperationException("Second scene duplicate failed.");
            RequireMethod(type, "CompleteSpatialTransformEdit").Invoke(form, null);
            if (undo.Invoke(form, null) is not true || scene.Instances.Count != 1
                || project.DrawingObjects.Count != definitionCount
                || redo.Invoke(form, null) is not true || scene.Instances.Count != 2
                || project.DrawingObjects.Count != definitionCount + 1)
                throw new InvalidOperationException("Scene duplicate did not undo and redo in one transaction.");
            var linkedSource = scene.Instances.Last();
            select.Invoke(form, [new[] { linkedSource }, linkedSource]);
            if (RequireMethod(type, "DuplicateStageSelection").Invoke(form, [true]) is not true
                || scene.Instances.Count != 3 || project.DrawingObjects.Count != definitionCount + 1
                || scene.Instances.Last().DrawingObjectId != linkedSource.DrawingObjectId)
                throw new InvalidOperationException("Linked duplicate did not retain its source Symbol.");
            if (MainForm.ConstrainProjectedSceneMoveAxis(new System.Numerics.Vector3(0, 4, -9))
                != new System.Numerics.Vector3(0, 0, -9))
                throw new InvalidOperationException("Projected scene axis lock did not preserve the dominant view axis.");
            var stage = (StageControl)RequireField(type, "_stage").GetValue(form)!;
            var projectedBounds = stage.GetProjectedSelectionObjectBounds();
            if (projectedBounds.Count == 0) throw new InvalidOperationException("Scene selection fixture did not project visible geometry.");
            var firstBounds = projectedBounds.First().Value;
            var pointer = Point.Round(new PointF(firstBounds.X + firstBounds.Width / 2, firstBounds.Y + firstBounds.Height / 2));
            RequireField(type, "_occludedCycleStartInstanceId").SetValue(form, scene.Instances[0].Id);
            if (RequireMethod(type, "CycleOccludedStageSelection").Invoke(form, [pointer]) is not true
                || Equals(RequireField(type, "_selectedSceneInstanceId").GetValue(form), scene.Instances[0].Id))
                throw new InvalidOperationException("Projected occluded selection did not cycle between root instances.");
            var marquee = RequireMethod(type, "CompleteProjectedStageInstanceMarquee");
            RequireField(type, "_subtractMarquee").SetValue(form, false);
            marquee.Invoke(form, [Point.Empty, new Point(stage.Width, stage.Height)]);
            if (((IEnumerable<string>)RequireField(type, "_selectedSceneInstanceIds").GetValue(form)!).Count() != 3)
                throw new InvalidOperationException("Projected marquee did not select all contained instances.");
            RequireField(type, "_marqueeInstanceSelectionBase").SetValue(form, scene.Instances.Select(instance => instance.Id).ToArray());
            RequireField(type, "_subtractMarquee").SetValue(form, true);
            RequireField(type, "_intersectMarquee").SetValue(form, false);
            marquee.Invoke(form, [Point.Empty, new Point(stage.Width, stage.Height)]);
            if (((IEnumerable<string>)RequireField(type, "_selectedSceneInstanceIds").GetValue(form)!).Any())
                throw new InvalidOperationException("Projected subtract marquee retained contained instances.");
            RequireField(type, "_intersectMarquee").SetValue(form, true);
            marquee.Invoke(form, [Point.Empty, new Point(stage.Width, stage.Height)]);
            if (((IEnumerable<string>)RequireField(type, "_selectedSceneInstanceIds").GetValue(form)!).Count() != 3)
                throw new InvalidOperationException("Projected intersect marquee lost contained instances.");
            Console.WriteLine("stage_instance_shortcut_regression=ok");
        }
        finally { RequireField(type, "_projectDirty").SetValue(form, false); }
    }

    private static void RunStageShortcutEditingRegression()
    {
        using var form = CreateOffscreenSnapPointForm();
        form.Show();
        Application.DoEvents();
        var scene = (VectorScene)RequireField(typeof(MainForm), "_scene").GetValue(form)!;
        var stage = (StageControl)RequireField(typeof(MainForm), "_stage").GetValue(form)!;
        var select = RequireMethod(typeof(MainForm), "SetSelection", [typeof(int), typeof(bool)]);
        var command = RequireMethod(typeof(MainForm), "HandleAdditionalStageShortcut");
        var copy = RequireMethod(typeof(MainForm), "CopySelectedObjects");
        var selected = RequireField(typeof(MainForm), "_selectedObjects");
        var dirty = RequireField(typeof(MainForm), "_projectDirty");
        bool Run(Keys keys) => command.Invoke(form, [keys]) is true;
        try
        {
            var first = scene.AddObject(0, new PointF(-500, 0), new SizeF(100, 100), 0, 0,
                Color.CornflowerBlue, Color.Transparent, 4, ShapeKind.Rectangle);
            var second = scene.AddObject(0, new PointF(500, 0), new SizeF(100, 100), 0, 0,
                Color.Coral, Color.Transparent, 4, ShapeKind.Rectangle);
            stage.BindScene(scene);
            scene.SetLinearGradient(first, Color.Navy, Color.Cyan);
            select.Invoke(form, [first, false]);
            if (copy.Invoke(form, null) is not true) throw new InvalidOperationException("Shortcut fixture could not copy.");
            var count = scene.ObjectCount;
            if (!Run(Keys.Control | Keys.D) || scene.ObjectCount != count + 1)
                throw new InvalidOperationException("Duplicate in place merged away the copy.");
            var duplicate = ((IEnumerable<int>)selected.GetValue(form)!).Single();
            if (scene.X[duplicate] != -500 || scene.X[first] != -500)
                throw new InvalidOperationException("Duplicate in place moved its source or copy.");
            if (!scene.HasGradient(duplicate) || scene.GradientStartArgb[duplicate] != scene.GradientStartArgb[first]
                || scene.GradientEndArgb[duplicate] != scene.GradientEndArgb[first])
                throw new InvalidOperationException("Duplicate lost gradient appearance.");
            if (RequireMethod(typeof(MainForm), "UndoLastEdit").Invoke(form, null) is not true || scene.ObjectCount != count)
                throw new InvalidOperationException("Duplicate did not undo as a single edit.");
            select.Invoke(form, [first, false]);
            if (!Run(Keys.Control | Keys.D)) throw new InvalidOperationException("Duplicate failed after undo.");
            duplicate = ((IEnumerable<int>)selected.GetValue(form)!).Single();
            // Move the copy away, then repeat the resulting displacement twice.
            scene.TranslateObjectsForPreview([duplicate], 250, 0);
            scene.RebuildGeometryIndex();
            if (!Run(Keys.Control | Keys.D)) throw new InvalidOperationException("Repeat duplicate failed.");
            duplicate = ((IEnumerable<int>)selected.GetValue(form)!).Single();
            if (scene.X[duplicate] != 0 || scene.X[first] != -500)
                throw new InvalidOperationException("Duplicate did not repeat the last translation.");
            if (!Run(Keys.Control | Keys.D)) throw new InvalidOperationException("Second repeat duplicate failed.");
            duplicate = ((IEnumerable<int>)selected.GetValue(form)!).Single();
            if (scene.X[duplicate] != 250) throw new InvalidOperationException("Repeated duplicate lost its displacement.");
            if (!Run(Keys.Right) || scene.X[duplicate] != 275
                || !Run(Keys.Shift | Keys.Down) || scene.Y[duplicate] != 250)
                throw new InvalidOperationException("Nudge did not use 1 px / 10 px in vector units.");
            if (!Run(Keys.Alt | Keys.A) || ((IEnumerable<int>)selected.GetValue(form)!).Any())
                throw new InvalidOperationException("Deselect shortcut retained objects.");
            select.Invoke(form, [second, false]);
            if (!Run(Keys.Control | Keys.Shift | Keys.I)
                || ((IEnumerable<int>)selected.GetValue(form)!).Contains(second))
                throw new InvalidOperationException("Invert selection retained the selected source.");
            select.Invoke(form, [second, false]);
            var undo = RequireMethod(typeof(MainForm), "UndoLastEdit");
            var redo = RequireMethod(typeof(MainForm), "RedoLastEdit");
            var previousX = scene.X[second];
            if (!Run(Keys.Right) || undo.Invoke(form, null) is not true || scene.X[second] != previousX
                || redo.Invoke(form, null) is not true || scene.X[second] != previousX + 25)
                throw new InvalidOperationException("Document redo did not restore a nudge.");
            select.Invoke(form, [second, false]);
            if (!Run(Keys.H) || scene.IsObjectActive(second, 0)
                || scene.GetActiveObjectIndices(0).Contains(second))
                throw new InvalidOperationException("Hidden object remained in active rendering/composition queries.");
            if (undo.Invoke(form, null) is not true || !scene.IsObjectActive(second, 0)
                || redo.Invoke(form, null) is not true || scene.IsObjectActive(second, 0))
                throw new InvalidOperationException("Hide undo/redo failed.");
            if (!Run(Keys.Alt | Keys.H) || !scene.IsObjectActive(second, 0))
                throw new InvalidOperationException("Show all failed.");
            select.Invoke(form, [second, false]);
            var visibility = RequireMethod(typeof(MainForm), "ChangeStageObjectVisibility");
            if (visibility.Invoke(form, [false, false, true]) is not true || scene.IsObjectSelectable(second, 0)
                || !scene.IsObjectActive(second, 0)) throw new InvalidOperationException("Object lock hid or left a selectable object.");
            var persisted = scene.CreateSnapshot();
            var roundtrip = new VectorScene();
            roundtrip.RestoreSnapshot(persisted);
            if (!roundtrip.ObjectLocked[second]) throw new InvalidOperationException("Lock snapshot lost state.");
            if (visibility.Invoke(form, [false, false, false]) is not true || !scene.IsObjectSelectable(second, 0))
                throw new InvalidOperationException("Unlock all failed.");
            select.Invoke(form, [first, false]);
            if (!Run(Keys.Control | Keys.Alt | Keys.C)) throw new InvalidOperationException("Copy appearance failed.");
            select.Invoke(form, [second, false]);
            var position = new PointF(scene.X[second], scene.Y[second]);
            if (!Run(Keys.Control | Keys.Alt | Keys.V) || scene.Argb[second] != scene.Argb[first]
                || new PointF(scene.X[second], scene.Y[second]) != position)
                throw new InvalidOperationException("Appearance transfer moved geometry or lost the fill.");
            select.Invoke(form, [second, false]);
            if (RequireMethod(typeof(MainForm), "BeginStageNumericTransform").Invoke(form, ['G']) is not true)
                throw new InvalidOperationException("Numeric move did not start.");
            RequireField(typeof(MainForm), "_stageNumericInput").SetValue(form, "2,3");
            if (RequireMethod(typeof(MainForm), "CommitStageNumericTransform").Invoke(form, null) is not true
                || scene.X[second] != position.X + 50 || scene.Y[second] != position.Y + 75)
                throw new InvalidOperationException("Numeric move did not apply document pixels.");
            select.Invoke(form, [second, false]);
            var transformCount = scene.ObjectCount;
            var sourceX = scene.X[second];
            RequireField(typeof(MainForm), "_activeTransformHandle").SetValue(form, TransformHandleKind.Move);
            RequireField(typeof(MainForm), "_duplicateDragArmed").SetValue(form, true);
            RequireField(typeof(MainForm), "_drawingTransformStartPointer").SetValue(form, PointF.Empty);
            RequireField(typeof(MainForm), "_drawingTransformStartBounds").SetValue(form, scene.GetObjectWorldBounds(second));
            RequireMethod(typeof(MainForm), "ApplyTransformFromPointer").Invoke(form, [new PointF(2000, 0)]);
            RequireMethod(typeof(MainForm), "FinishPointerInteraction").Invoke(form, null);
            if (scene.ObjectCount != transformCount + 1 || scene.X[second] != sourceX
                || scene.X[((IEnumerable<int>)selected.GetValue(form)!).Single()] != sourceX + 2000
                || undo.Invoke(form, null) is not true || scene.ObjectCount != transformCount)
                throw new InvalidOperationException("Free Transform drag copy moved its source or lost its undo boundary.");
            var profile = ShortcutProfiles.GetBuiltInProfile(ToolShortcutPreset.TraditionalFlash);
            if (ShortcutProfiles.ResolveWorkbenchKey(profile, Keys.Control | Keys.D) != (Keys.Control | Keys.D)
                || !ShortcutProfiles.IsReservedGesture(Keys.Control | Keys.D2))
                throw new InvalidOperationException("Stage shortcuts broke the default profile or workspace reservations.");
            var lower = scene.AddObject(0, new PointF(3000, 3000), new SizeF(800, 800), 0, 0,
                Color.Coral, Color.Transparent, 4, ShapeKind.Rectangle);
            var upper = scene.AddObject(0, new PointF(3000, 3000), new SizeF(800, 800), 0, 0,
                Color.CadetBlue, Color.Transparent, 4, ShapeKind.Rectangle);
            var cycle = RequireMethod(typeof(MainForm), "CycleOccludedStageSelection");
            var cycleStart = RequireField(typeof(MainForm), "_occludedCycleStartObject");
            cycleStart.SetValue(form, upper);
            RequireMethod(typeof(MainForm), "ClearSelection").Invoke(form, [false]);
            if (cycle.Invoke(form, [Point.Round(stage.WorldToScreen(3000, 3000))]) is not true
                || !((IEnumerable<int>)selected.GetValue(form)!).Contains(lower))
                throw new InvalidOperationException("Occluded selection lost its pointer-down selection while forcing marquee.");
            scene.CreateEmpty();
            var editableLine = scene.AddLineSegment(0, new PointF(-1000, 0), PointF.Empty,
                12, Color.Transparent, Color.Black, 4);
            var lockedLine = scene.AddLineSegment(0, PointF.Empty, new PointF(1000, 0),
                12, Color.Transparent, Color.Black, 4);
            scene.ObjectLocked[lockedLine] = true;
            scene.CompleteDeferredBuild();
            select.Invoke(form, [editableLine, false]);
            RequireField(typeof(MainForm), "_activeHandle").SetValue(form, EditHandleKind.LineEnd);
            RequireMethod(typeof(MainForm), "CaptureEditStart").Invoke(form, [editableLine]);
            var connected = (System.Collections.ICollection)RequireField(typeof(MainForm), "_lineEndpointEditStarts").GetValue(form)!;
            if (connected.Count != 1)
                throw new InvalidOperationException("Endpoint dragging included a locked neighboring stroke.");
            var routeKey = RequireMethod(typeof(MainForm), "ProcessCmdKey", [typeof(Message).MakeByRefType(), typeof(Keys)]);
            var beforeGestureCount = scene.ObjectCount;
            var beforeGestureX = scene.X[editableLine];
            RequireField(typeof(MainForm), "_lastMouse").SetValue(form, Point.Empty);
            foreach (var key in new[] { Keys.Control | Keys.D, Keys.Back, Keys.Right })
                if (routeKey.Invoke(form, [default(Message), key]) is not true
                    || scene.ObjectCount != beforeGestureCount || scene.X[editableLine] != beforeGestureX)
                    throw new InvalidOperationException("A Stage shortcut mutated geometry during an endpoint gesture.");
            RequireField(typeof(MainForm), "_lastMouse").SetValue(form, null);
            RequireField(typeof(MainForm), "_activeHandle").SetValue(form, EditHandleKind.None);
            Console.WriteLine("stage_shortcut_editing_regression=ok");
        }
        finally { dirty.SetValue(form, false); }
    }
}
