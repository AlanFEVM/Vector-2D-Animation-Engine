namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunObjectDrawingRegression()
    {
        using var form = new MainForm
        {
            ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
            Location = new Point(-30000, -30000), Size = new Size(1280, 800)
        };
        var tabs = (WorkspaceTabs)RequireField(typeof(MainForm), "_workspaceTabs").GetValue(form)!;
        tabs.SelectedView = WorkspaceView.BasicDrawing;
        RequireMethod(typeof(MainForm), "SwitchDrawingScope").Invoke(form, [true]);
        var host = (DrawingObjectDefinition)RequireMethod(typeof(MainForm), "ActiveDrawingObject").Invoke(form, null)!;
        var project = (VectorProject)RequireField(typeof(MainForm), "_project").GetValue(form)!;
        var drawing = host.Scene;
        drawing.CreateEmpty(1, 12);
        RequireMethod(typeof(MainForm), "BindActiveDrawingObjectScene").Invoke(form, [false]);
        var stage = (StageControl)RequireField(typeof(MainForm), "_stage").GetValue(form)!;
        stage.Size = new Size(800, 600);
        var activate = RequireMethod(typeof(MainForm), "ActivateTool", [typeof(ToolMode)]);
        var addShape = RequireMethod(typeof(MainForm), "AddDrawnObject");
        var undo = RequireMethod(typeof(MainForm), "UndoLastEdit");
        var settings = (DrawSettings)RequireField(typeof(MainForm), "_drawSettings").GetValue(form)!;
        var mode = RequireField(typeof(MainForm), "_objectDrawingEnabled");
        var timeline = (TimelineStrip)RequireField(typeof(MainForm), "_timeline").GetValue(form)!;
        timeline.AutoKeyframeEnabled = false;
        var baselineSymbols = project.DrawingObjects.Count;
        var older = drawing.AddObject(0, PointF.Empty, new SizeF(2000, 2000), 0, 0,
            Color.Teal, Color.Transparent, 16, ShapeKind.Rectangle);
        var olderBounds = drawing.GetObjectWorldBounds(older);
        var olderColor = drawing.Argb[older];
        try
        {
            mode.SetValue(form, true);
            foreach (var tool in new[] { ToolMode.Rectangle, ToolMode.Ellipse, ToolMode.Triangle,
                ToolMode.Polygon, ToolMode.Star, ToolMode.Line })
            {
                activate.Invoke(form, [tool]);
                addShape.Invoke(form, [new PointF(-500, -400), new PointF(800, 700), tool]);
                AssertTimeline(host.Instances.Count == 1 && project.DrawingObjects.Count == baselineSymbols + 1
                    && drawing.ObjectCount == 1 && drawing.GetObjectWorldBounds(older) == olderBounds
                    && drawing.Argb[older] == olderColor && stage.SelectedObjects.Count == 0,
                    $"Object Drawing {tool} merged, cut, or changed the pre-existing raw drawing.");
                var symbol = project.DrawingObjects.Single(item => item.Id == host.Instances[0].DrawingObjectId);
                AssertTimeline(symbol.Scene.ObjectCount == 1
                    && symbol.Scene.ShapeKind[0] == (tool == ToolMode.Line ? ShapeKind.Line : settings.ShapeKind),
                    $"Object Drawing {tool} lost the editable shape inside its symbol.");
                AssertTimeline(undo.Invoke(form, null) is true && host.Instances.Count == 0
                    && project.DrawingObjects.Count == baselineSymbols && drawing.ObjectCount == 1,
                    $"One undo failed to remove the complete {tool} object and preserve older content.");
            }

            foreach (var tool in new[] { ToolMode.Pencil, ToolMode.Brush, ToolMode.PressureBrush, ToolMode.MixingBrush })
            {
                activate.Invoke(form, [tool]);
                RequireField(typeof(MainForm), "_brushShape").SetValue(form, BrushShape.CreateSoftRound());
                var start = new PointF(-600, -100);
                RequireMethod(typeof(MainForm), "BeginFreehandStroke").Invoke(form,
                    [Point.Round(stage.WorldToScreen(start.X, start.Y)), start, 0.8f]);
                var samples = (List<PointF>)RequireField(typeof(MainForm), "_freehandSamples").GetValue(form)!;
                var pressure = (List<PressureBrushSample>)RequireField(typeof(MainForm), "_pressureBrushSamples").GetValue(form)!;
                for (var index = 1; index <= 48; index++)
                {
                    var point = new PointF(-600 + index * 25, MathF.Sin(index / 48f * MathF.PI) * 400 - 100);
                    samples.Add(point);
                    if (tool == ToolMode.PressureBrush)
                        pressure.Add(new PressureBrushSample(point, 180, index / 120f, 0.8f));
                    if (tool == ToolMode.MixingBrush)
                        ((MixingBrushRegionAccumulator)RequireField(typeof(MainForm), "_mixingBrushRegionAccumulator")
                            .GetValue(form)!).Append(point);
                }
                RequireMethod(typeof(MainForm), "CommitFreehandStroke").Invoke(form, null);
                AssertTimeline(host.Instances.Count == 1 && project.DrawingObjects.Count == baselineSymbols + 1
                    && drawing.ObjectCount == 1 && drawing.GetObjectWorldBounds(older) == olderBounds,
                    $"{tool} did not commit exactly one independent object or damaged older geometry.");
                var symbol = project.DrawingObjects.Single(item => item.Id == host.Instances[0].DrawingObjectId);
                AssertTimeline(symbol.Scene.ObjectCount == (tool is ToolMode.Brush or ToolMode.PressureBrush ? 3 : 1),
                    $"{tool} lost its editable content or split soft layers into multiple instances.");
                AssertTimeline(undo.Invoke(form, null) is true && host.Instances.Count == 0
                    && project.DrawingObjects.Count == baselineSymbols && drawing.ObjectCount == 1,
                    $"One undo failed to remove all of the {tool} object.");
            }

            RunObjectDrawingPenRegression(form, host, project, baselineSymbols);
            activate.Invoke(form, [ToolMode.Rectangle]);
            var material = (MaterialEditorPanel)RequireField(typeof(MainForm), "_materialEditor").GetValue(form)!;
            GradientStop[] stops = [new(0, Color.Coral), new(0.4f, Color.Gold), new(1, Color.Navy)];
            foreach (var kind in new[] { GradientKind.Linear, GradientKind.Radial, GradientKind.ShapeRadial })
            {
                material.SetGradientPreviewTarget(false);
                material.SetGradient(kind, stops);
                addShape.Invoke(form, [new PointF(-500, -400), new PointF(800, 700), ToolMode.Rectangle]);
                var symbol = project.DrawingObjects.Single(item => item.Id == host.Instances.Single().DrawingObjectId);
                AssertTimeline(symbol.Scene.GetGradientKind(0) == kind
                    && symbol.Scene.GetGradientStops(0).SequenceEqual(stops),
                    $"Object Drawing lost its {kind} gradient material.");
                AssertTimeline(undo.Invoke(form, null) is true, "Gradient object drawing could not be undone.");
            }
            material.SetGradient(GradientKind.Solid, stops);
            addShape.Invoke(form, [new PointF(-300, -300), new PointF(300, 300), ToolMode.Rectangle]);
            var first = host.Instances.Single();
            addShape.Invoke(form, [new PointF(-100, -100), new PointF(500, 500), ToolMode.Rectangle]);
            var second = host.Instances.Single(instance => instance.Id != first.Id);
            AssertTimeline(host.Instances.Count == 2
                && host.Instances.Select(instance => instance.Id).SequenceEqual([second.Id, first.Id]),
                "Later object drawings did not stack above earlier instances.");
            var stackedScene = new VectorScene();
            var stacked = SceneCompositionBuilder.BuildDrawingObjectChildren(
                stackedScene, host, project.DrawingObjects, 0);
            var frontHit = stackedScene.HitTestElement(PointF.Empty, 0, 0);
            AssertTimeline(frontHit.IsValid
                && stacked.TryGetOwner(frontHit.Key.ObjectIndex, out var frontOwner)
                && frontOwner.RootInstanceId == second.Id,
                "Later object drawings were not frontmost in the flattened composition.");
            var directory = CreateTemporaryDirectory("object-drawing-roundtrip");
            try
            {
                var manifest = Path.Combine(directory, "Drawing.v2dProject");
                ProjectVaultStore.Save(project, manifest);
                var restored = ProjectVaultStore.Load(manifest);
                var restoredHost = restored.DrawingObjects.Single(item => item.Id == host.Id);
                AssertTimeline(restoredHost.Scene.ObjectCount == 1 && restoredHost.Instances.Count == 2
                    && restoredHost.Instances.Select(instance => instance.Id).SequenceEqual(host.Instances.Select(instance => instance.Id))
                    && restored.DrawingObjects.Single(item => item.Id == second.DrawingObjectId).Scene.ObjectCount == 1
                    && restoredHost.Instances[0].EvaluateState(0) == second.EvaluateState(0),
                    "Object Drawing Save/Open changed instance identity, order, placement, or editable contents.");
            }
            finally { DeleteTemporaryDirectory(directory); }
            AssertTimeline(undo.Invoke(form, null) is true && undo.Invoke(form, null) is true,
                "Object Drawing stacking fixture could not undo its two gestures.");

            RequireMethod(typeof(MainForm), "SwitchDrawingScope").Invoke(form, [false]);
            AssertTimeline(!(bool)RequireProperty(typeof(MainForm), "ObjectDrawingActive").GetValue(form)!,
                "Open scope unexpectedly enabled nested Object Drawing.");
            var button = (Button)RequireField(typeof(MainForm), "_objectDrawingButton").GetValue(form)!;
            RequireMethod(typeof(Button), "OnClick", [typeof(EventArgs)]).Invoke(button, [EventArgs.Empty]);
            AssertTimeline((bool)RequireProperty(typeof(MainForm), "ObjectDrawingActive").GetValue(form)!
                && ReferenceEquals(RequireMethod(typeof(MainForm), "ActiveDrawingObject").Invoke(form, null), host),
                "The Object Drawing toggle did not explicitly switch from Open back to Current.");

            timeline.AutoKeyframeEnabled = true;
            RequireMethod(typeof(MainForm), "SetFrame", [typeof(int), typeof(bool), typeof(bool), typeof(bool)])
                .Invoke(form, [5, true, true, false]);
            addShape.Invoke(form, [new PointF(-500, -500), new PointF(500, 500), ToolMode.Rectangle]);
            AssertTimeline(host.Instances.Count == 1 && !host.Instances[0].EvaluateState(4).Visible
                && host.Instances[0].EvaluateState(5).Visible
                && drawing.ObjectKeyframeFrame.Take(drawing.ObjectCount).Contains(5),
                "Auto Key object drawing did not preserve the earlier held Cel or start its instance on the current frame.");
            AssertTimeline(undo.Invoke(form, null) is true && host.Instances.Count == 0
                && drawing.ObjectCount == 1 && drawing.ObjectKeyframeFrame[0] == 0,
                "Undoing Auto Key object drawing left a new Cel or instance behind.");
            RunObjectDrawingSceneRegression();
            Console.WriteLine("object_drawing_regression=ok,shapes=all,brushes=all,pen=path,undo=single,storage=legacy,autokey=preserved");
        }
        finally
        {
            RequireField(typeof(MainForm), "_projectDirty").SetValue(form, false);
        }
    }

    private static void RunObjectDrawingPenRegression(
        MainForm form, DrawingObjectDefinition host, VectorProject project, int baselineSymbols)
    {
        foreach (var tool in new[] { ToolMode.Pen, ToolMode.SimplePen })
        {
            RequireMethod(typeof(MainForm), "ActivateTool", [typeof(ToolMode)]).Invoke(form, [tool]);
            for (var index = 0; index < 3; index++)
            {
                if (tool == ToolMode.Pen)
                {
                    RequireField(typeof(MainForm), "_traditionalPenCurrentAnchor").SetValue(form, new PointF(index * 200, 0));
                    RequireField(typeof(MainForm), "_traditionalPenPendingAnchor").SetValue(form, new PointF((index + 1) * 200, 100));
                    AssertTimeline(RequireMethod(typeof(MainForm), "CommitTraditionalPenSegment").Invoke(form, null) is true,
                        "Object Drawing Pen failed to accept a segment.");
                }
                else
                {
                    RequireField(typeof(MainForm), "_penStartWorld").SetValue(form, new PointF(index * 200, 0));
                    RequireField(typeof(MainForm), "_penEndWorld").SetValue(form, new PointF((index + 1) * 200, 100));
                    AssertTimeline(RequireMethod(typeof(MainForm), "CommitPenSegment").Invoke(form, null) is true,
                        "Object Drawing SimplePen failed to accept a segment.");
                }
                AssertTimeline(host.Instances.Count == 0 && project.DrawingObjects.Count == baselineSymbols,
                    "Object Drawing created a separate symbol for each Pen segment.");
            }
            RequireMethod(typeof(MainForm), tool == ToolMode.Pen ? "CancelTraditionalPenPath" : "CancelPenCurve")
                .Invoke(form, null);
            AssertTimeline(host.Instances.Count == 1
                && project.DrawingObjects.Single(item => item.Id == host.Instances[0].DrawingObjectId).Scene.ObjectCount == 3,
                "Finishing a Pen path did not create one object containing all three editable segments.");
            AssertTimeline(RequireMethod(typeof(MainForm), "UndoLastEdit").Invoke(form, null) is true
                && host.Instances.Count == 0 && project.DrawingObjects.Count == baselineSymbols,
                "One undo failed to remove the complete Pen path object.");
        }
    }

    private static void RunObjectDrawingSceneRegression()
    {
        using var form = new MainForm
        {
            ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
            Location = new Point(-30000, -30000), Size = new Size(1280, 800)
        };
        var project = (VectorProject)RequireField(typeof(MainForm), "_project").GetValue(form)!;
        var scene = project.Scenes[0];
        var layer = scene.AddLayer(project, "Independent objects");
        scene.SetActiveLayer(layer.Id);
        foreach (var track in scene.Timeline.Tracks) scene.Timeline.SetTrackDuration(track.Id, 12);
        project.TrySetPlaybackRange(0, 11);
        var tabs = (WorkspaceTabs)RequireField(typeof(MainForm), "_workspaceTabs").GetValue(form)!;
        var timeline = (TimelineStrip)RequireField(typeof(MainForm), "_timeline").GetValue(form)!;
        var symbolCount = project.DrawingObjects.Count;
        try
        {
            tabs.SelectedView = WorkspaceView.SceneEditor;
            RequireMethod(typeof(MainForm), "ActivateTool", [typeof(ToolMode)])
                .Invoke(form, [ToolMode.Rectangle]);
            timeline.AutoKeyframeEnabled = true;
            RequireMethod(typeof(MainForm), "SetFrame", [typeof(int), typeof(bool), typeof(bool), typeof(bool)])
                .Invoke(form, [5, true, true, false]);
            RequireField(typeof(MainForm), "_objectDrawingEnabled").SetValue(form, true);
            RequireMethod(typeof(MainForm), "AddDrawnObject").Invoke(form,
                [new PointF(-600, -400), new PointF(600, 400), ToolMode.Rectangle]);
            AssertTimeline(scene.Instances.Count == 1 && scene.DrawingScene.ObjectCount == 0
                && project.DrawingObjects.Count == symbolCount + 1
                && scene.Instances[0].SceneLayerId == layer.Id
                && !scene.Instances[0].EvaluateState(4).Visible
                && scene.Instances[0].EvaluateState(5).Visible,
                "Scene Object Drawing lost the current layer, Auto Key exposure, or independent instance.");
            AssertTimeline(RequireMethod(typeof(MainForm), "UndoLastEdit").Invoke(form, null) is true
                && scene.Instances.Count == 0 && scene.DrawingScene.ObjectCount == 0
                && project.DrawingObjects.Count == symbolCount,
                "One scene undo did not restore the entire object drawing transaction.");
        }
        finally { RequireField(typeof(MainForm), "_projectDirty").SetValue(form, false); }
    }
}
