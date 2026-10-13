namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunMotionParentPointerRegression()
    {
        using var form = new MainForm();
        var scene = (VectorScene)RequireField(typeof(MainForm), "_scene").GetValue(form)!;
        scene.CreateEmpty(3, 12);
        for (var layer = 0; layer < 3; layer++)
            scene.AddObject(layer, new PointF(layer * 200, 0), new SizeF(100, 100), 0, 2, Color.Coral, 0, ShapeKind.Rectangle);
        scene.Timeline.SetMotionParent(scene.LayerIds[1], scene.LayerIds[0]);
        scene.Timeline.SetMotionParent(scene.LayerIds[2], scene.LayerIds[1]);
        var timeline = (TimelineStrip)RequireField(typeof(MainForm), "_timeline").GetValue(form)!;
        timeline.BindScene(scene);
        timeline.AutoKeyframeEnabled = false;
        RequireMethod(typeof(MainForm), "SetSelection", [typeof(int), typeof(bool)]).Invoke(form, [0, false]);
        RequireMethod(typeof(MainForm), "CaptureEditStart", [typeof(int)]).Invoke(form, [0]);
        RequireField(typeof(MainForm), "_startWorld").SetValue(form, PointF.Empty);
        RequireMethod(typeof(MainForm), "MoveSelectedFromPointer", [typeof(PointF), typeof(bool), typeof(bool)])
            .Invoke(form, [new PointF(50, 25), true, false]);
        AssertTimeline(scene.X[0] == 50 && scene.X[1] == 250 && scene.X[2] == 450,
            "The actual selection drag did not move child layers.");
        RequireMethod(typeof(MainForm), "UndoLastEdit").Invoke(form, null);
        AssertTimeline(scene.X[0] == 0 && scene.X[1] == 200 && scene.X[2] == 400,
            "Pointer undo did not restore the full parent chain.");
        timeline.AutoKeyframeEnabled = true;
        RequireField(typeof(MainForm), "_frame").SetValue(form, 6);
        scene.EditFrame = 6;
        RequireField(typeof(MainForm), "_undoCapturedForPointerEdit").SetValue(form, false);
        RequireMethod(typeof(MainForm), "SetSelection", [typeof(int), typeof(bool)]).Invoke(form, [0, false]);
        RequireMethod(typeof(MainForm), "CaptureEditStart", [typeof(int)]).Invoke(form, [0]);
        RequireField(typeof(MainForm), "_startWorld").SetValue(form, PointF.Empty);
        RequireMethod(typeof(MainForm), "MoveSelectedFromPointer", [typeof(PointF), typeof(bool), typeof(bool)])
            .Invoke(form, [new PointF(50, 25), true, false]);
        AssertTimeline(Enumerable.Range(0, 3).All(layer =>
            scene.Timeline.EvaluateTargetExposure(scene.LayerIds[layer], 6).SourceKeyframeFrame == 6),
            "Auto Key did not prepare descendant cels.");
        AssertTimeline(Enumerable.Range(0, 3).All(layer =>
            Enumerable.Range(0, scene.ObjectCount).Any(index => scene.ObjectLayer[index] == layer
                && scene.ObjectKeyframeFrame[index] == 0 && scene.X[index] == layer * 200)),
            "Auto Key changed the original held cels.");
        AssertTimeline(scene.X[0] == 50 && scene.X[1] == 250 && scene.X[2] == 450,
            "Auto Key did not move the current descendant cels.");
    }

    private static void RunMotionParentPartPointerRegression()
    {
        using var form = new MainForm();
        var scene = (VectorScene)RequireField(typeof(MainForm), "_scene").GetValue(form)!;
        scene.CreateEmpty(3, 12);
        for (var layer = 0; layer < 3; layer++)
            scene.AddObject(layer, new PointF(layer * 200, 0), new SizeF(100, 100), 0, 2, Color.Coral, 0, ShapeKind.Rectangle);
        scene.Timeline.SetMotionParent(scene.LayerIds[1], scene.LayerIds[0]);
        scene.Timeline.SetMotionParent(scene.LayerIds[2], scene.LayerIds[1]);
        var timeline = (TimelineStrip)RequireField(typeof(MainForm), "_timeline").GetValue(form)!;
        timeline.BindScene(scene);
        timeline.AutoKeyframeEnabled = false;
        var hit = scene.HitTestElement(new PointF(200, 0), 0, 0);
        AssertTimeline(hit.IsValid, "The parent fill could not be selected.");
        RequireMethod(typeof(MainForm), "SetSelection", [typeof(DrawingElementHit), typeof(bool)]).Invoke(form, [hit, false]);
        RequireMethod(typeof(MainForm), "CaptureEditStart", [typeof(int)]).Invoke(form, [hit.Key.ObjectIndex]);
        RequireField(typeof(MainForm), "_startWorld").SetValue(form, new PointF(200, 0));
        RequireMethod(typeof(MainForm), "MoveSelectedFromPointer", [typeof(PointF), typeof(bool), typeof(bool)])
            .Invoke(form, [new PointF(250, 25), true, false]);
        AssertTimeline(Enumerable.Range(0, scene.ObjectCount).Any(index => scene.ObjectLayer[index] == 2
            && scene.X[index] == 450 && scene.Y[index] == 25),
            "Dragging parent 001's fill did not move child 002.");
    }

    private static void RunMotionParentNestedPointerRegression()
    {
        using var form = new MainForm();
        var project = (VectorProject)RequireField(typeof(MainForm), "_project").GetValue(form)!;
        var container = project.DrawingObjects[0];
        container.Scene.CreateEmpty(3, 12);
        var definition = project.AddDrawingObject("Parenting instance content");
        definition.Scene.AddObject(0, PointF.Empty, new SizeF(100, 100), 0, 2, Color.Coral, 0, ShapeKind.Rectangle);
        var instances = new List<DrawingObjectInstanceDefinition>();
        for (var layer = 0; layer < 3; layer++)
        {
            AssertTimeline(project.TryAddDrawingObjectInstance(container.Id, definition.Id,
                new PointF(layer * 200, 0), container.Scene.LayerIds[layer], out var instance) && instance is not null,
                "The nested parenting fixture could not create an instance.");
            instances.Add(instance!);
        }
        container.Timeline.SetMotionParent(container.Scene.LayerIds[1], container.Scene.LayerIds[0]);
        container.Timeline.SetMotionParent(container.Scene.LayerIds[2], container.Scene.LayerIds[1]);
        var timeline = (TimelineStrip)RequireField(typeof(MainForm), "_timeline").GetValue(form)!;
        timeline.BindContext(container);
        timeline.AutoKeyframeEnabled = false;
        RequireMethod(typeof(MainForm), "SetSceneInstanceSelection",
            [typeof(DrawingObjectInstanceDefinition), typeof(bool)]).Invoke(form, [instances[1], false]);
        RequireField(typeof(MainForm), "_transformLastPointer").SetValue(form, new PointF(200, 0));
        RequireMethod(typeof(MainForm), "MoveSelectedSceneInstance", [typeof(PointF)])
            .Invoke(form, [new PointF(250, 25)]);
        AssertTimeline(instances[0].EvaluateState(0).X == 0 && instances[1].EvaluateState(0).X == 250
            && instances[2].EvaluateState(0).X == 450 && instances[2].EvaluateState(0).Y == 25,
            "Nested child 002 did not follow parent 001 or incorrectly moved ancestor 000.");
    }

    private static void RunMotionParentScenePointerRegression()
    {
        using var form = new MainForm();
        var project = (VectorProject)RequireField(typeof(MainForm), "_project").GetValue(form)!;
        var scene = project.Scenes[0];
        var parentLayer = scene.Layers[0].Id;
        AssertTimeline(project.TryAddSceneLayer(scene.Id, out var childLayer) && childLayer is not null,
            "Scene pointer fixture could not create a child layer.");
        AssertTimeline(project.TryAddSceneInstance(scene.Id, project.DrawingObjects[0].Id,
            PointF.Empty, 0, parentLayer, out var parent) && parent is not null,
            "Scene pointer fixture could not create its parent instance.");
        AssertTimeline(project.TryAddSceneInstance(scene.Id, project.DrawingObjects[0].Id,
            new PointF(200, 0), 0, childLayer!.Id, out var child) && child is not null,
            "Scene pointer fixture could not create its child instance.");
        scene.Timeline.SetMotionParent(childLayer.Id, parentLayer);
        var tabs = (WorkspaceTabs)RequireField(typeof(MainForm), "_workspaceTabs").GetValue(form)!;
        tabs.SelectedView = WorkspaceView.SceneEditor;
        var timeline = (TimelineStrip)RequireField(typeof(MainForm), "_timeline").GetValue(form)!;
        timeline.BindSceneDefinition(scene);
        timeline.AutoKeyframeEnabled = false;
        AssertTimeline(timeline.MotionParentingAvailable && timeline.MotionParentingVisible,
            "Parent links were not visible in the real workbench by default.");
        RequireMethod(typeof(MainForm), "SetSceneInstanceSelection",
            [typeof(DrawingObjectInstanceDefinition), typeof(bool)]).Invoke(form, [parent, false]);
        RequireMethod(typeof(MainForm), "BeginSpatialTransformEdit", [typeof(bool)]).Invoke(form, [true]);
        Func<InstanceFrameState, string, InstanceFrameState> translate = (state, instanceId) =>
            state with { X = state.X + 50, Y = state.Y + 25 };
        RequireMethod(typeof(MainForm), "ApplySpatialPointerStates",
            [typeof(Func<InstanceFrameState, string, InstanceFrameState>)]).Invoke(form, [translate]);
        AssertTimeline(parent!.EvaluateState(0).X == 50 && child!.EvaluateState(0).X == 250
            && child.EvaluateState(0).Y == 25,
            "The actual scene pointer session did not move the child instance.");
        RequireMethod(typeof(MainForm), "CancelSpatialTransformEdit").Invoke(form, null);
        AssertTimeline(scene.Instances.Single(instance => instance.Id == parent.Id).EvaluateState(0).X == 0
            && scene.Instances.Single(instance => instance.Id == child!.Id).EvaluateState(0).X == 200,
            "Cancel did not restore the parent and child scene instances.");
    }

    private static void RunMotionParentTransformRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty(4, 12);
        for (var layer = 0; layer < 4; layer++)
            scene.AddObject(layer, new PointF(layer * 100, 0), new SizeF(40, 40), 0, 2, Color.Coral, 0, ShapeKind.Rectangle);
        scene.Timeline.SetMotionParent(scene.LayerIds[1], scene.LayerIds[0]);
        scene.Timeline.SetMotionParent(scene.LayerIds[2], scene.LayerIds[1]);
        var before = scene.CreateSnapshot();
        var targets = scene.ExpandMotionTransformObjects([0], 0);
        AssertTimeline(targets.SequenceEqual([0, 1, 2]), "Motion chain did not include every descendant exactly once.");
        var session = scene.BeginTransformSession(targets);
        scene.ApplyTranslationSessionForPreview(session, 50, 25);
        AssertTimeline(scene.X[0] == 50 && scene.X[1] == 150 && scene.X[2] == 250 && scene.X[3] == 300,
            "Parent translation did not move descendants or changed an unrelated layer.");
        scene.ApplyTranslationSessionForPreview(session, 75, 25);
        AssertTimeline(scene.X[2] == 275, "Repeated drag preview accumulated descendant offsets.");
        scene.RestoreSnapshot(before);
        AssertTimeline(scene.X[2] == 200, "Undo baseline did not restore descendants.");
        AssertTimeline(scene.ExpandMotionTransformObjects([0, 1], 0).SequenceEqual([0, 1, 2])
            && scene.ExpandMotionTransformObjects([1], 0).SequenceEqual([1, 2]),
            "Parent-child multiselection doubled a transform or child editing moved its parent.");
        scene.SetLayerVisible(2, false);
        AssertTimeline(scene.ExpandMotionTransformObjects([0], 0).Contains(2), "Hidden descendants stopped following.");
        scene.ApplyTransformSession(scene.BeginTransformSession(targets), point => new PointF(-point.Y, point.X), false);
        AssertTimeline(scene.Y[2] == 200 && scene.X[3] == 300, "Parent rotation did not preserve the descendant chain.");
        scene.RestoreSnapshot(before);
        scene.ApplyTransformSession(scene.BeginTransformSession(targets), point => new PointF(point.X * 2, point.Y * 2), false);
        AssertTimeline(scene.X[2] == 400 && scene.X[3] == 300, "Parent scale did not follow.");
        scene.RestoreSnapshot(before);
        scene.Timeline.SetMotionParent(scene.LayerIds[1], "");
        AssertTimeline(scene.ExpandMotionTransformObjects([0], 0).SequenceEqual([0]) && scene.X[1] == 100,
            "Detach moved content or retained following.");
    }

    private static void RunMotionParentDetachMenuRegression()
    {
        using var form = new MainForm();
        var scene = (VectorScene)RequireField(typeof(MainForm), "_scene").GetValue(form)!;
        scene.CreateEmpty(3, 12);
        for (var layer = 0; layer < 3; layer++)
            scene.AddObject(layer, new PointF(layer * 200, 0), new SizeF(100, 100), 0, 2, Color.Coral, 0, ShapeKind.Rectangle);
        var timeline = (TimelineStrip)RequireField(typeof(MainForm), "_timeline").GetValue(form)!;
        timeline.BindScene(scene);
        scene.Timeline.SetMotionParent(scene.LayerIds[1], scene.LayerIds[0]);
        scene.Timeline.SetMotionParent(scene.LayerIds[2], scene.LayerIds[1]);
        using var host = new Form
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-30_000, -30_000),
            ClientSize = new Size(900, 300)
        };
        using var anchor = new Control { Dock = DockStyle.Fill };
        host.Controls.Add(anchor);
        host.Show();
        Application.DoEvents();
        using var menu = (AnimatedContextMenuStrip)RequireMethod(typeof(TimelineStrip), "CreateMotionParentMenu", [typeof(int)])
            .Invoke(timeline, [1])!;
        menu.Show(anchor, new Point(20, 20));
        Application.DoEvents();
        var requests = 0;
        timeline.MotionParentRequested += (_, _) => requests++;
        ((ToolStripMenuItem)menu.Items[0]).PerformClick();
        AssertTimeline(requests == 1 && scene.Timeline.GetMotionParentTargetId(scene.LayerIds[1]) == "",
            "The real detach menu closed without dispatching the remove-parent command.");
        AssertTimeline(scene.Timeline.GetMotionParentTargetId(scene.LayerIds[2]) == scene.LayerIds[1]
            && scene.X[0] == 0 && scene.X[1] == 200 && scene.X[2] == 400,
            "Detach changed positions or removed the child's own descendant link.");
        Application.DoEvents();
        AssertTimeline(menu.IsDisposed, "The closed parenting menu leaked after its click callback.");
        RequireMethod(typeof(MainForm), "UndoLastEdit").Invoke(form, null);
        AssertTimeline(scene.Timeline.GetMotionParentTargetId(scene.LayerIds[1]) == scene.LayerIds[0],
            "Undo did not restore the detached parent link.");
    }

    private static void RunMotionParentGroupRegression()
    {
        using var form = new MainForm();
        var scene = (VectorScene)RequireField(typeof(MainForm), "_scene").GetValue(form)!;
        scene.CreateEmpty(3, 12);
        var timeline = scene.Timeline;
        var targets = scene.LayerIds;
        var parent = timeline.FindTrackByTargetId(targets[0])!;
        var child = timeline.FindTrackByTargetId(targets[1])!;
        var alternative = timeline.FindTrackByTargetId(targets[2])!;
        var group = timeline.CreateTabGroup("Parenting group");
        timeline.SetTrackTabGroup(child.Id, group);
        var strip = (TimelineStrip)RequireField(typeof(MainForm), "_timeline").GetValue(form)!;
        strip.BindScene(scene);
        timeline.SetActiveTabGroup(AnimationTimeline.AllTabGroupId);
        var canSet = RequireMethod(typeof(MainForm), "CanSetTimelineMotionParent", [typeof(string), typeof(string)]);
        var request = RequireMethod(typeof(MainForm), "SetTimelineMotionParent", [typeof(TimelineParentRequestedEventArgs)]);
        AssertTimeline(!timeline.CanSetMotionParent(targets[1], targets[0])
            && !timeline.SetMotionParent(targets[1], targets[0])
            && !(bool)canSet.Invoke(form, [targets[1], targets[0]])!,
            "Cross-group parenting was allowed in the model or All-view UI.");
        request.Invoke(form, [new TimelineParentRequestedEventArgs(child.Id, parent.Id)]);
        AssertTimeline(timeline.GetMotionParentTargetId(targets[1]) == "",
            "The editor created a cross-group parent link.");
        timeline.SetTrackTabGroup(parent.Id, group);
        AssertTimeline(timeline.CanSetMotionParent(targets[1], targets[0])
            && (bool)canSet.Invoke(form, [targets[1], targets[0]])!,
            "Same-group parenting was rejected.");
        request.Invoke(form, [new TimelineParentRequestedEventArgs(child.Id, parent.Id)]);
        AssertTimeline(timeline.GetMotionParentTargetId(targets[1]) == targets[0],
            "The editor did not create a same-group parent link.");
        AssertTimeline(!timeline.SetMotionParent(targets[1], alternative.TargetId)
            && timeline.GetMotionParentTargetId(targets[1]) == targets[0],
            "Rejected cross-group reparenting changed the existing parent.");
        RequireMethod(typeof(MainForm), "UndoLastEdit").Invoke(form, null);
        AssertTimeline(timeline.GetMotionParentTargetId(targets[1]) == "",
            "Undo did not restore the same-group parenting command.");
        timeline.SetMotionParent(targets[1], targets[0]);
        timeline.SetTrackTabGroup(parent.Id, AnimationTimeline.DefaultTabGroupId);
        var snapshot = timeline.CreateSnapshot();
        timeline.RestoreSnapshot(snapshot);
        AssertTimeline(timeline.GetMotionParentTargetId(targets[1]) == targets[0]
            && timeline.CanSetMotionParent(targets[1], "")
            && timeline.SetMotionParent(targets[1], ""),
            "Existing cross-group links were lost on restore or could not be detached.");
        var project = VectorProject.CreateEmpty();
        var composition = project.Scenes[0];
        AssertTimeline(project.TryAddSceneLayer(composition.Id, out var sceneChild) && sceneChild is not null,
            "The scene group-parenting fixture could not add its child layer.");
        var sceneTimeline = composition.Timeline;
        var sceneParentId = composition.Layers[0].Id;
        var sceneChildId = sceneChild!.Id;
        var sceneGroup = sceneTimeline.CreateTabGroup("Scene parenting group");
        sceneTimeline.SetTrackTabGroup(sceneTimeline.FindTrackByTargetId(sceneChildId)!.Id, sceneGroup);
        strip.BindContext(composition);
        AssertTimeline(!sceneTimeline.SetMotionParent(sceneChildId, sceneParentId)
            && !(bool)canSet.Invoke(form, [sceneChildId, sceneParentId])!,
            "Scene layers accepted cross-group parenting.");
        sceneTimeline.SetTrackTabGroup(sceneTimeline.FindTrackByTargetId(sceneParentId)!.Id, sceneGroup);
        AssertTimeline(sceneTimeline.SetMotionParent(sceneChildId, sceneParentId),
            "Scene layers rejected same-group parenting.");
        Console.WriteLine("motion_parent_group=ok,cross_group_rejected=ok,same_group=ok,all_view=ok,undo=ok,legacy_detach=ok,scene=ok");
    }

    private static void RunTimelineParentingRegression()
    {
        RunMotionParentGroupRegression();
        RunMotionParentTransformRegression();
        RunMotionParentPointerRegression();
        RunMotionParentPartPointerRegression();
        RunMotionParentNestedPointerRegression();
        RunMotionParentScenePointerRegression();
        RunMotionParentDetachMenuRegression();
        var project = VectorProject.CreateEmpty();
        var drawing = project.DrawingObjects[0];
        drawing.Scene.CreateEmpty(3, 12);
        var timeline = drawing.Timeline;
        var targets = drawing.Scene.LayerIds.Take(3).ToArray();
        AssertTimeline(timeline.SetMotionParent(targets[1], targets[0])
            && timeline.SetMotionParent(targets[2], targets[1]), "Parent chains could not be created.");
        AssertTimeline(!timeline.CanSetMotionParent(targets[0], targets[2])
            && !timeline.CanSetMotionParent(targets[0], targets[0])
            && !timeline.CanSetMotionParent(targets[0], "missing")
            && !timeline.SetMotionParent(targets[1], targets[0]), "Invalid or unchanged parenting was accepted.");
        AssertTimeline(drawing.Scene.LayerParentIds.All(string.IsNullOrEmpty),
            "Motion parents changed folder membership.");
        var before = timeline.CreateSnapshot();
        AssertTimeline(timeline.SetMotionParent(targets[2], ""), "Parent detach failed.");
        timeline.RestoreSnapshot(before);
        AssertTimeline(timeline.GetMotionParentTargetId(targets[2]) == targets[1], "Parent snapshot was not restored.");
        var invalid = new AnimationTimelineSnapshot
        {
            Tracks = before.Tracks,
            MotionParents = [new(targets[0], targets[1]), new(targets[1], targets[0])]
        };
        var rejected = false;
        try { timeline.RestoreSnapshot(invalid); }
        catch (InvalidDataException) { rejected = true; }
        AssertTimeline(rejected && timeline.GetMotionParentTargetId(targets[2]) == targets[1],
            "A cyclic snapshot was accepted or partially mutated the timeline.");
        var cloned = VectorProject.RestoreRestartSnapshot(project.CreateRestartSnapshot());
        AssertTimeline(cloned.DrawingObjects[0].Timeline.GetMotionParentTargetId(targets[2]) == targets[1],
            "Restart snapshot lost parent links.");
        AssertTimeline(project.TryDuplicateDrawingObject(drawing.Id, out var duplicate)
            && duplicate is not null && duplicate.Timeline.GetMotionParentTargetId(targets[2]) == targets[1],
            "Duplicating a drawing lost parent links.");
        var scene = project.Scenes[0];
        AssertTimeline(project.TryAddSceneLayer(scene.Id, out var sceneChild) && sceneChild is not null,
            "Scene parenting fixture could not add a layer.");
        var sceneParentId = scene.Layers[0].Id;
        var sceneChildId = scene.Layers[1].Id;
        AssertTimeline(scene.Timeline.SetMotionParent(sceneChildId, sceneParentId), "Scene parenting failed.");
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"VectorParenting-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(temporaryRoot);
            var manifest = Path.Combine(temporaryRoot, "Parenting.v2dProject");
            ProjectVaultStore.Save(project, manifest);
            var loaded = ProjectVaultStore.Load(manifest);
            AssertTimeline(loaded.DrawingObjects[0].Timeline.GetMotionParentTargetId(targets[2]) == targets[1],
                "Project save/open lost parent links.");
            AssertTimeline(loaded.Scenes[0].Timeline.GetMotionParentTargetId(sceneChildId) == sceneParentId,
                "Scene save/open lost parent links.");
        }
        finally
        {
            if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, true);
        }
        timeline.SynchronizeTracks(new TimelineTrackTarget[] { new(targets[0], 12), new(targets[2], 12) });
        AssertTimeline(timeline.GetMotionParentTargetId(targets[2]) == "", "Deleting a parent left a dangling link.");
        timeline.RestoreSnapshot(new AnimationTimelineSnapshot { Tracks = before.Tracks });
        AssertTimeline(timeline.GetMotionParentTargetId(targets[2]) == "", "Legacy snapshots inherited stale parents.");
    }
}
