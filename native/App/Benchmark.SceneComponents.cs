namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunSpatialComponentShortcutRegression()
    {
        const System.Reflection.BindingFlags instanceFlags =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var projectField = typeof(MainForm).GetField("_project", instanceFlags)
            ?? throw new InvalidOperationException("3D component shortcut regression could not find the project.");
        var workspaceTabsField = typeof(MainForm).GetField("_workspaceTabs", instanceFlags)
            ?? throw new InvalidOperationException("3D component shortcut regression could not find workspace tabs.");
        var stageField = typeof(MainForm).GetField("_stage", instanceFlags)
            ?? throw new InvalidOperationException("3D component shortcut regression could not find the Stage.");
        var setInstanceSelection = typeof(MainForm).GetMethod(
                "SetSceneInstanceSelection",
                instanceFlags,
                binder: null,
                types: [typeof(DrawingObjectInstanceDefinition), typeof(bool)],
                modifiers: null)
            ?? throw new InvalidOperationException("3D component shortcut regression could not select scene instances.");
        var processCmdKey = typeof(MainForm).GetMethod(
                "ProcessCmdKey",
                instanceFlags,
                binder: null,
                types: [typeof(Message).MakeByRefType(), typeof(Keys)],
                modifiers: null)
            ?? throw new InvalidOperationException("3D component shortcut regression could not find ProcessCmdKey.");
        var undoLastEdit = typeof(MainForm).GetMethod(
                "UndoLastEdit",
                instanceFlags,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null)
            ?? throw new InvalidOperationException("3D component shortcut regression could not find UndoLastEdit.");

        using var form = new MainForm
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-30_000, -30_000),
            Size = new Size(1280, 800)
        };
        var project = projectField.GetValue(form) as VectorProject
            ?? throw new InvalidOperationException("3D component shortcut regression did not obtain the project.");
        var workspaceTabs = workspaceTabsField.GetValue(form) as WorkspaceTabs
            ?? throw new InvalidOperationException("3D component shortcut regression did not obtain workspace tabs.");
        var stage = stageField.GetValue(form) as StageControl
            ?? throw new InvalidOperationException("3D component shortcut regression did not obtain the Stage.");
        var scene = project.Scenes[0];
        scene.Dimension = SceneDimension.ThreeD;
        var source = project.DrawingObjects[0];
        DrawingObjectInstanceDefinition? first = null;
        DrawingObjectInstanceDefinition? second = null;
        AssertTimeline(
            project.TryAddSceneInstance(scene.Id, source.Id, new PointF(-120, 40), 20, out first)
            && project.TryAddSceneInstance(scene.Id, source.Id, new PointF(180, 120), 140, out second)
            && first is not null
            && second is not null,
            "3D component shortcut regression could not create scene instances.");
        first!.ScaleZ = 2;
        second!.RotationY = 35;
        second.ScaleZ = 3;
        var originalIds = scene.Instances.Select(instance => instance.Id).ToArray();

        form.Show();
        form.Activate();
        workspaceTabs.SelectedView = WorkspaceView.SceneEditor;
        Application.DoEvents();
        stage.Select();
        stage.Focus();
        setInstanceSelection.Invoke(form, [first, false]);
        setInstanceSelection.Invoke(form, [second, true]);
        Application.DoEvents();

        object[] arguments =
        [
            Message.Create(form.Handle, 0, IntPtr.Zero, IntPtr.Zero),
            Keys.F8
        ];
        var handled = processCmdKey.Invoke(form, arguments) is true;
        var component = project.DrawingObjects.SingleOrDefault(drawingObject =>
            DrawingObjectAssetKinds.IsThreeDimensional(drawingObject.Kind));
        AssertTimeline(
            handled
            && component is not null
            && component.Instances.Select(instance => instance.Id)
                .ToHashSet(StringComparer.Ordinal)
                .SetEquals(originalIds)
            && scene.Instances.Count == 1
            && string.Equals(scene.Instances[0].DrawingObjectId, component.Id, StringComparison.Ordinal)
            && NearlyEqual(scene.Instances[0].ScaleZ, 1),
            "F8 did not replace the selected 3D scene instances with one reusable 3D symbol: "
            + $"handled={handled}, components={project.DrawingObjects.Count}, "
            + $"sceneInstances={scene.Instances.Count}, exposure="
            + scene.Timeline.EvaluateTargetExposure(first.SceneLayerId, 0).HasContent);

        AssertTimeline(
            undoLastEdit.Invoke(form, null) is true
            && project.DrawingObjects.Count == 1
            && !project.DrawingObjects.Any(drawingObject =>
                DrawingObjectAssetKinds.IsThreeDimensional(drawingObject.Kind))
            && scene.Instances.Select(instance => instance.Id)
                .SequenceEqual(originalIds, StringComparer.Ordinal),
            "One undo did not restore the source scene instances and remove the created 3D symbol.");
        Console.WriteLine("spatial_component_shortcut=ok");
    }
}
