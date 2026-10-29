using System.Text.Json;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunSceneDrawingInteractionRegression()
    {
        using var form = new MainForm
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-30_000, -30_000),
            Size = new Size(1280, 800)
        };
        var project = (VectorProject)RequireField(typeof(MainForm), "_project").GetValue(form)!;
        var stage = (StageControl)RequireField(typeof(MainForm), "_stage").GetValue(form)!;
        var tabs = (WorkspaceTabs)RequireField(typeof(MainForm), "_workspaceTabs").GetValue(form)!;
        var timeline = (TimelineStrip)RequireField(typeof(MainForm), "_timeline").GetValue(form)!;
        var scene = project.Scenes[0];
        var layer = scene.AddLayer(project, "Hand-drawn animation");
        foreach (var track in scene.Timeline.Tracks.ToArray()) scene.Timeline.SetTrackDuration(track.Id, 16);
        project.TrySetPlaybackRange(0, 15);
        var symbolCount = project.DrawingObjects.Count;
        var setFrame = RequireMethod(typeof(MainForm), "SetFrame");
        var activate = RequireMethod(typeof(MainForm), "ActivateTool", [typeof(ToolMode)]);
        var undo = RequireMethod(typeof(MainForm), "UndoLastEdit");
        var edit = RequireMethod(typeof(MainForm), "ExecuteTimelineEdit");
        var editKind = edit.GetParameters()[0].ParameterType;
        try
        {
            form.Show();
            tabs.SelectedView = WorkspaceView.SceneEditor;
            Application.DoEvents();
            setFrame.Invoke(form, [5, true, true, false]);
            activate.Invoke(form, [ToolMode.Rectangle]);
            AssertTimeline(ReferenceEquals(stage.Scene, scene.DrawingScene)
                && ReferenceEquals(timeline.Context, scene),
                "Activating a scene drawing tool did not retain the scene document and timeline.");
            var start = Point.Round(stage.WorldToScreen(-1500, -1000));
            var end = Point.Round(stage.WorldToScreen(-600, -100));
            RequireMethod(typeof(MainForm), "StageMouseDown").Invoke(form,
                [stage, new MouseEventArgs(MouseButtons.Left, 1, start.X, start.Y, 0)]);
            RequireMethod(typeof(MainForm), "StageMouseMove").Invoke(form,
                [stage, new MouseEventArgs(MouseButtons.Left, 0, end.X, end.Y, 0)]);
            RequireMethod(typeof(MainForm), "StageMouseUp").Invoke(form,
                [stage, new MouseEventArgs(MouseButtons.Left, 1, end.X, end.Y, 0)]);
            var drawing = scene.DrawingScene;
            AssertTimeline(drawing.ObjectCount == 1 && drawing.ObjectKeyframeFrame[0] == 5
                && !drawing.IsObjectActive(0, 4) && drawing.IsObjectActive(0, 5)
                && project.DrawingObjects.Count == symbolCount && scene.Instances.Count == 0,
                "Direct scene drawing created hidden symbols or backfilled an earlier blank exposure.");
            var track = scene.Timeline.FindTrackByTargetId(layer.Id)!;
            AssertTimeline(edit.Invoke(form, [Enum.Parse(editKind, "InsertKeyframe"),
                new TimelineFrameCell[] { new(track.Id, 8) }, false]) is true
                && drawing.ObjectCount == 2,
                "Scene F6 through the workbench did not copy direct geometry.");
            AssertTimeline(edit.Invoke(form, [Enum.Parse(editKind, "InsertBlankKeyframe"),
                new TimelineFrameCell[] { new(track.Id, 10) }, false]) is true
                && !scene.Timeline.EvaluateTargetExposure(layer.Id, 10).HasContent,
                "Scene F7 through the workbench did not hold a blank exposure.");
            undo.Invoke(form, null);
            AssertTimeline(scene.Timeline.EvaluateTargetExposure(layer.Id, 10).HasContent
                && drawing.ObjectCount == 2, "Undo did not restore the scene exposure before F7.");
            undo.Invoke(form, null);
            AssertTimeline(drawing.ObjectCount == 1, "Undo did not remove the copied scene Cel.");
            undo.Invoke(form, null);
            AssertTimeline(drawing.ObjectCount == 0 && project.DrawingObjects.Count == symbolCount,
                "Drawing undo did not follow scene timeline undo in chronological order.");
            AssertTimeline(project.TryAddSceneInstance(scene.Id, project.DrawingObjects[0].Id,
                PointF.Empty, 0, scene.Layers.First(candidate => candidate.Id != layer.Id).Id,
                out var instance) && instance is not null,
                "Scene brush fixture could not add an existing symbol instance.");
            setFrame.Invoke(form, [11, true, true, false]);
            activate.Invoke(form, [ToolMode.Brush]);
            start = Point.Round(stage.WorldToScreen(-1500, -1000));
            end = Point.Round(stage.WorldToScreen(-600, -100));
            RequireMethod(typeof(MainForm), "StageMouseDown").Invoke(form,
                [stage, new MouseEventArgs(MouseButtons.Left, 1, start.X, start.Y, 0)]);
            for (var sample = 1; sample <= 8; sample++)
            {
                var samplePoint = new Point(start.X + (end.X - start.X) * sample / 8,
                    start.Y + (end.Y - start.Y) * sample / 8);
                RequireMethod(typeof(MainForm), "StageMouseMove").Invoke(form,
                    [stage, new MouseEventArgs(MouseButtons.Left, 0, samplePoint.X, samplePoint.Y, 0)]);
            }
            RequireMethod(typeof(MainForm), "StageMouseUp").Invoke(form,
                [stage, new MouseEventArgs(MouseButtons.Left, 1, end.X, end.Y, 0)]);
            AssertTimeline(drawing.ObjectCount > 0 && drawing.ObjectKeyframeFrame
                .Take(drawing.ObjectCount).All(frame => frame == 11)
                && project.DrawingObjects.Count == symbolCount
                && scene.Instances.Count == 1 && scene.Instances[0].Id == instance!.Id,
                "A scene brush stroke did not commit to its current Cel alongside an existing instance: "
                + $"objects={drawing.ObjectCount},frames={string.Join(',', drawing.ObjectKeyframeFrame.Take(drawing.ObjectCount))},"
                + $"symbols={project.DrawingObjects.Count},instances={scene.Instances.Count}.");
            undo.Invoke(form, null);
            AssertTimeline(drawing.ObjectCount == 0 && scene.Instances.Count == 1,
                "Undoing a scene brush stroke also removed the existing symbol instance.");
            Console.WriteLine("scene_brush_with_instance=ok");
            Console.WriteLine("scene_drawing_interaction=ok");
        }
        finally
        {
            RequireField(typeof(MainForm), "_projectDirty").SetValue(form, false);
            form.Hide();
        }
    }

    private static void RunSceneDrawingRegression()
    {
        var project = VectorProject.CreateEmpty();
        var scene = project.AddScene("Direct scene drawing");
        var sceneId = scene.Id;
        var frontLayer = scene.Layers[0];
        if (!project.TryAddSceneLayer(scene.Id, out var backLayer) || backLayer is null)
        {
            throw new InvalidOperationException("Scene drawing regression could not create a second ordinary scene layer.");
        }
        var timeline = scene.Timeline;
        var drawing = scene.DrawingScene;
        var frontLayerIndex = Array.IndexOf(drawing.LayerIds, frontLayer.Id);
        var backLayerIndex = Array.IndexOf(drawing.LayerIds, backLayer.Id);
        AssertTimeline(
            scene.CanDraw
            && ReferenceEquals(timeline, drawing.Timeline)
            && frontLayerIndex == 0 && backLayerIndex == 1
            && drawing.LayerIds.SequenceEqual(scene.Layers.Select(layer => layer.Id))
            && scene.Layers.All(layer => layer.Kind == SceneLayerKind.Content),
            "Scene drawing did not share its timeline or mirror ordinary scene-layer stable IDs.");
        foreach (var track in timeline.Tracks.ToArray()) timeline.SetTrackDuration(track.Id, 12);

        var symbol = project.DrawingObjects[0];
        symbol.Scene.CreateEmpty(frameCount: 12);
        symbol.Scene.AddObject(0, PointF.Empty, new SizeF(120, 80), 0, 0,
            Color.Gold, Color.Transparent, 8, ShapeKind.Rectangle);
        drawing.EditFrame = 0;
        var original = drawing.AddObject(frontLayerIndex, PointF.Empty, new SizeF(120, 80), 0, 0,
            Color.Coral, Color.Transparent, 8, ShapeKind.Rectangle);
        var background = drawing.AddObject(backLayerIndex, PointF.Empty, new SizeF(120, 80), 0, 0,
            Color.Teal, Color.Transparent, 8, ShapeKind.Rectangle);
        if (!project.TryAddSceneInstance(scene.Id, symbol.Id, PointF.Empty, 0, backLayer.Id, out var instance)
            || instance is null)
        {
            throw new InvalidOperationException("Scene drawing regression could not place an instance alongside direct geometry.");
        }
        var instanceId = instance.Id;
        var jsonOptions = new JsonSerializerOptions { IncludeFields = true, MaxDepth = 256 };
        var symbolBefore = Capture(symbol.Scene.CreateSnapshot());
        var instanceBefore = instance.EvaluateState(0);
        var frontTrack = scene.Timeline.FindTrackByTargetId(frontLayer.Id)
            ?? throw new InvalidOperationException("Scene drawing lost its front-layer track.");
        var frontTrackId = frontTrack.Id;
        AssertTimeline(
            project.DrawingObjects.Count == 1
            && scene.InstancesInLayer(backLayer.Id).Count == 1
            && drawing.ObjectCount == 2
            && frontTrack.EvaluateExposure(3).SourceKeyframeFrame == 0
            && frontTrack.EvaluateExposure(3).HasContent
            && drawing.IsObjectActive(original, 3),
            "Direct scene artwork did not coexist with an instance or hold from its frame-zero Cel.");

        var initial = AssertComposition(project, 0, 3, "Initial scene composition");
        var initialHit = initial.Scene.HitTestElement(PointF.Empty, 0, 0);
        AssertTimeline(
            initialHit.IsValid
            && initial.Scene.Argb[initialHit.Key.ObjectIndex] == Color.Coral.ToArgb()
            && initial.Result.TryGetOwner(initialHit.Key.ObjectIndex, out var directOwner)
            && directOwner.DrawingObjectId == scene.Id && string.IsNullOrEmpty(directOwner.InstanceId)
            && initial.Result.ObjectOwners.Values.Count(owner => owner.DrawingObjectId == scene.Id) == 2
            && initial.Result.ObjectOwners.Values.Count(owner => owner.InstanceId == instanceId) == 1,
            "Scene composition did not keep front-layer direct geometry ahead of lower-layer instances with aligned owners.");

        frontLayer.Visible = false;
        var backOnly = AssertComposition(project, 0, 2, "Hidden front scene layer");
        var backHit = backOnly.Scene.HitTestElement(PointF.Empty, 0, 0);
        AssertTimeline(
            backHit.IsValid
            && backOnly.Result.TryGetOwner(backHit.Key.ObjectIndex, out var instanceOwner)
            && instanceOwner.InstanceId == instanceId,
            "Same-layer direct geometry covered its instance or hidden front-layer artwork remained composited.");
        frontLayer.Visible = true;
        backLayer.BlendMode = LayerBlendMode.Multiply;
        var instancesOnly = AssertComposition(project, 0, 1, "Instances-only composition", includeDirectDrawing: false);
        var underlay = AssertComposition(project, 0, 1, "Host-composited instance underlay",
            includeDirectDrawing: false, compositeWithHostLayers: true);
        AssertTimeline(
            instancesOnly.Scene.LayerBlendModes.Contains(LayerBlendMode.Multiply)
            && underlay.Scene.LayerCount == 1
            && underlay.Scene.LayerBlendModes.All(mode => mode == LayerBlendMode.Normal)
            && underlay.Result.ObjectOwners.Values.All(owner => owner.InstanceId == instanceId)
            && underlay.Result.HostLayerRanges.Count == 1
            && underlay.Result.HostLayerRanges.TryGetValue(backLayerIndex, out var hostRange)
            && hostRange.Start == 0 && hostRange.Count == 1,
            "Instance underlay included direct artwork, lost its host-layer range, or applied the host blend twice.");
        backLayer.BlendMode = LayerBlendMode.Normal;
        _ = scene.Timeline;

        AssertTimeline(drawing.InsertTimelineKeyframe(frontLayerIndex, 4),
            "Scene F6 did not duplicate its held direct-drawing Cel.");
        var clone = Enumerable.Range(0, drawing.ObjectCount).Single(index =>
            drawing.ObjectLayer[index] == frontLayerIndex && drawing.ObjectKeyframeFrame[index] == 4);
        AssertTimeline(
            drawing.ObjectCount == 3
            && drawing.ObjectKeyframeFrame[original] == 0
            && drawing.IsObjectActive(original, 3) && !drawing.IsObjectActive(original, 4)
            && !drawing.IsObjectActive(clone, 3) && drawing.IsObjectActive(clone, 6),
            "Scene F6 did not retain independent original and copied Cel ownership across held frames.");
        drawing.X[clone] = 240;
        drawing.RebuildGeometryIndex();
        AssertTimeline(NearlyEqual(drawing.X[original], 0) && NearlyEqual(drawing.X[clone], 240),
            "Editing a copied scene Cel changed the held source geometry.");
        AssertTimeline(drawing.InsertTimelineBlankKeyframe(frontLayerIndex, 8),
            "Scene F7 did not insert a blank direct-drawing Cel.");
        AssertTimeline(
            !scene.Timeline.FindTrackByTargetId(frontLayer.Id)!.EvaluateExposure(9).HasContent
            && !drawing.IsObjectActive(original, 8) && !drawing.IsObjectActive(clone, 8)
            && drawing.IsObjectActive(background, 9)
            && !drawing.InsertTimelineKeyframe(frontLayerIndex, 8) && drawing.ObjectCount == 3,
            "Scene F7 did not hold blank, affected another layer, or F6 resurrected artwork on a blank key.");
        AssertComposition(project, 6, 3, "Held copied scene Cel");
        var blankComposition = AssertComposition(project, 9, 2, "Blank front scene Cel");
        AssertTimeline(!blankComposition.Scene.Argb.Take(blankComposition.Scene.ObjectCount).Contains(Color.Coral.ToArgb()),
            "A blank scene Cel still contributed direct geometry to composition.");

        AssertTimeline(drawing.InsertTimelineFrame(frontLayerIndex, 2, 2),
            "Scene frame insertion failed before an independent drawing Cel.");
        AssertTimeline(
            drawing.ObjectKeyframeFrame[clone] == 6
            && drawing.ObjectKeyframeFrame[original] == 0 && drawing.ObjectKeyframeFrame[background] == 0
            && scene.Timeline.FindTrackByTargetId(frontLayer.Id)!.EvaluateExposure(10).SourceKind == TimelineKeyframeKind.Blank,
            "Scene frame insertion did not move Cel ownership and its blank boundary together.");
        AssertTimeline(drawing.RemoveTimelineFrame(frontLayerIndex, 2, 2),
            "Scene frame removal failed to restore direct-drawing Cel positions.");
        AssertTimeline(
            drawing.ObjectKeyframeFrame[clone] == 4
            && scene.Timeline.FindTrackByTargetId(frontLayer.Id)!.Id == frontTrackId
            && scene.Timeline.FindTrackByTargetId(frontLayer.Id)!.EvaluateExposure(8).SourceKind == TimelineKeyframeKind.Blank
            && instance.EvaluateState(0) == instanceBefore && Capture(symbol.Scene.CreateSnapshot()) == symbolBefore,
            "Scene frame removal lost track identity or changed the reusable symbol or instance state.");

        var layerSnapshot = scene.CreateLayerSnapshot();
        var layerSnapshotBefore = Capture(layerSnapshot);
        AssertTimeline(
            layerSnapshot.DrawingScene is { ObjectCount: 3 } savedDrawing
            && savedDrawing.LayerIds.SequenceEqual(drawing.LayerIds)
            && savedDrawing.ObjectKeyframeFrame.SequenceEqual(drawing.ObjectKeyframeFrame.Take(drawing.ObjectCount))
            && Capture(savedDrawing.Timeline) == Capture(scene.Timeline.CreateSnapshot()),
            "A scene-layer snapshot omitted direct geometry, stable layer IDs, Cel ownership, or the shared timeline.");
        AssertTimeline(
            drawing.InsertTimelineKeyframe(backLayerIndex, 4) && drawing.ObjectCount == 4
            && scene.Instances.Count == 1 && project.DrawingObjects.Count == 1,
            "F6 on a mixed scene layer did not copy its local Cel or introduced hidden instance/symbol duplicates.");
        AssertComposition(project, 6, 3, "F6 on a mixed direct-and-instance scene layer");
        AssertTimeline(drawing.InsertTimelineBlankKeyframe(backLayerIndex, 8),
            "F7 failed on a scene layer containing both local artwork and an instance.");
        AssertComposition(project, 9, 0, "F7 hides mixed scene-layer content");
        AssertComposition(project, 9, 0, "F7 hides the instance underlay",
            includeDirectDrawing: false, compositeWithHostLayers: true);
        AssertTimeline(!drawing.InsertTimelineKeyframe(backLayerIndex, 8) && drawing.ObjectCount == 4,
            "F6 on a mixed layer's blank key resurrected local geometry or external instance content.");
        scene.RestoreLayerSnapshot(layerSnapshot);
        AssertTimeline(Capture(scene.CreateLayerSnapshot()) == layerSnapshotBefore,
            "A scene-layer snapshot did not restore independent mixed-layer keys and geometry.");
        drawing.X[clone] += 100;
        drawing.RebuildGeometryIndex();
        AssertTimeline(Capture(layerSnapshot) == layerSnapshotBefore,
            "A scene-layer snapshot shared mutable geometry with the live scene.");
        scene.RestoreLayerSnapshot(layerSnapshot);
        AssertTimeline(
            ReferenceEquals(scene.Timeline, drawing.Timeline) && Capture(scene.CreateLayerSnapshot()) == layerSnapshotBefore,
            "Restoring scene layers did not restore direct geometry and its shared timeline without loss.");

        var legacyScene = new SceneDefinition();
        legacyScene.RestoreLayerSnapshot(new SceneLayerSnapshot
        {
            Layers = layerSnapshot.Layers,
            ActiveLayerId = layerSnapshot.ActiveLayerId,
            InstanceLayerIds = new Dictionary<string, string>(layerSnapshot.InstanceLayerIds, StringComparer.Ordinal)
        });
        AssertTimeline(
            legacyScene.DrawingScene.ObjectCount == 0
            && ReferenceEquals(legacyScene.Timeline, legacyScene.DrawingScene.Timeline)
            && legacyScene.DrawingScene.LayerIds.SequenceEqual(layerSnapshot.Layers.Select(layer => layer.Id)),
            "A legacy null drawing snapshot did not restore an empty scene drawing with stable host layers.");

        var projectSnapshot = project.CreateRestartSnapshot();
        var roundTripped = EditorRestartStore.RoundTripProjectSnapshot(projectSnapshot);
        AssertTimeline(
            Capture(roundTripped.Scenes.Single(item => item.Id == sceneId).Layers) == layerSnapshotBefore,
            "Project restart JSON did not preserve the complete scene-layer drawing snapshot.");
        AssertRestoredProject(VectorProject.RestoreRestartSnapshot(roundTripped), "Project restart round trip");

        var temporaryRoot = CreateTemporaryDirectory("scene-drawing-regression");
        string? packageWorkspace = null;
        try
        {
            var manifestPath = Path.Combine(temporaryRoot, "Folder", "SceneDrawing.v2dProject");
            ProjectVaultStore.Save(project, manifestPath);
            AssertRestoredProject(ProjectVaultStore.Load(manifestPath), "Folder save and reopen");
            var packagePath = Path.Combine(temporaryRoot, "SceneDrawing.v2d");
            packageWorkspace = Path.GetDirectoryName(ProjectPackageStore.SavePackage(project, packagePath));
            var (packaged, _) = ProjectPackageStore.OpenPackage(packagePath);
            AssertRestoredProject(packaged, "Package save and reopen");
        }
        finally
        {
            if (packageWorkspace is not null) DeleteTemporaryDirectory(packageWorkspace);
            DeleteTemporaryDirectory(temporaryRoot);
        }

        Console.WriteLine("scene_drawing_model_persistence=ok");

        string Capture<T>(T value) => JsonSerializer.Serialize(value, jsonOptions);

        (VectorScene Scene, SceneCompositionResult Result) AssertComposition(
            VectorProject source,
            int frame,
            int expectedObjects,
            string context,
            bool includeDirectDrawing = true,
            bool compositeWithHostLayers = false)
        {
            var sourceScene = source.Scenes.FirstOrDefault(candidate => candidate.Id == sceneId)
                ?? throw new InvalidOperationException($"{context}: the scene is missing.");
            _ = sourceScene.Timeline;
            var before = Capture(source.CreateRestartSnapshot());
            var destination = new VectorScene();
            var result = SceneCompositionBuilder.Build(destination, sourceScene, source.DrawingObjects, frame,
                parentFps: source.PlaybackFps, includeDirectDrawing: includeDirectDrawing,
                compositeWithHostLayers: compositeWithHostLayers);
            AssertTimeline(
                destination.ObjectCount == expectedObjects
                && result.ObjectOwners.Count == expectedObjects
                && result.ObjectPoses.Count == expectedObjects && result.ObjectMaterials.Count == expectedObjects,
                $"{context}: geometry or composition metadata did not match the expected visible content.");
            AssertTimeline(Capture(source.CreateRestartSnapshot()) == before,
                $"{context}: building derived composition mutated source geometry, instances, or timeline state.");
            return (destination, result);
        }

        void AssertRestoredProject(VectorProject restored, string context)
        {
            var restoredScene = restored.Scenes.FirstOrDefault(candidate => candidate.Id == sceneId)
                ?? throw new InvalidOperationException($"{context}: the scene is missing.");
            AssertTimeline(
                restored.Id == project.Id && restored.Scenes.Count == project.Scenes.Count
                && restored.DrawingObjects.Count == project.DrawingObjects.Count
                && restoredScene.Instances.Count == 1 && restoredScene.Instances[0].Id == instanceId
                && restoredScene.Instances[0].SceneLayerId == backLayer.Id
                && restoredScene.Instances[0].EvaluateState(0) == instanceBefore
                && ReferenceEquals(restoredScene.Timeline, restoredScene.DrawingScene.Timeline)
                && Capture(restoredScene.CreateLayerSnapshot()) == layerSnapshotBefore
                && Capture(restored.DrawingObjects.First(candidate => candidate.Id == symbol.Id).Scene.CreateSnapshot()) == symbolBefore,
                $"{context}: scene drawing, stable IDs, Cel ownership, shared timeline, or original symbols changed.");
            AssertComposition(restored, 0, 3, $"{context}, initial Cel");
            AssertComposition(restored, 6, 3, $"{context}, held copied Cel");
            AssertComposition(restored, 9, 2, $"{context}, held blank Cel");
        }
    }
}
