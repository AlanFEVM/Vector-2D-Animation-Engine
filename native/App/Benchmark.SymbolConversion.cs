using System.ComponentModel;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunSymbolConversionContextMenuRegression()
    {
        using var form = new MainForm
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-30000, -30000),
            Size = new Size(1280, 800)
        };
        var project = (VectorProject)RequireField(typeof(MainForm), "_project").GetValue(form)!;
        var tabs = (WorkspaceTabs)RequireField(typeof(MainForm), "_workspaceTabs").GetValue(form)!;
        var menu = (ContextMenuStrip)RequireField(typeof(MainForm), "_stageContextMenu").GetValue(form)!;
        var item = (ToolStripMenuItem)RequireField(typeof(MainForm), "_convertToSymbolMenuItem").GetValue(form)!;
        var opening = RequireMethod(typeof(ContextMenuStrip), "OnOpening", [typeof(CancelEventArgs)]);
        RequireField(typeof(MainForm), "_stageContextMenuLocation").SetValue(form, new Point(-10000, -10000));
        var symbol = project.DrawingObjects[0];
        symbol.SetAnchor(PointF.Empty);
        var shape = symbol.Scene.AddObject(0, PointF.Empty, new SizeF(120, 80),
            0, 0, Color.Teal, Color.Transparent, 6, ShapeKind.Rectangle);
        form.Show();
        tabs.SelectedView = WorkspaceView.BasicDrawing;
        RequireMethod(typeof(MainForm), "OpenDrawingObjectEditor", [typeof(string)]).Invoke(form, [symbol.Id]);
        Application.DoEvents();
        RequireMethod(typeof(MainForm), "SetSelection", [typeof(int), typeof(bool)]).Invoke(form, [shape, false]);
        opening.Invoke(menu, [new CancelEventArgs()]);
        AssertTimeline(menu.Items.Contains(item) && item.Enabled
            && Equals(item.Tag, ShortcutCommandIds.ConvertToSymbol),
            "Drawing canvas did not expose the shared Convert to Symbol context command.");

        var scene = project.Scenes[0];
        scene.Dimension = SceneDimension.TwoD;
        AssertTimeline(project.TryAddSceneInstance(scene.Id, symbol.Id, new PointF(200, 100), 0, out var first)
            && first is not null, "Could not create the first 2D conversion instance.");
        AssertTimeline(project.TryAddSceneInstance(scene.Id, symbol.Id, new PointF(500, 250), 0, out var second)
            && second is not null, "Could not create the second 2D conversion instance.");
        first!.SetStateAtFrame(6, first.EvaluateState(6) with { X = 350, RotationZ = 20 });
        var originalIds = scene.Instances.Select(instance => instance.Id).ToArray();
        tabs.SelectedView = WorkspaceView.SceneEditor;
        Application.DoEvents();
        var select = RequireMethod(typeof(MainForm), "SetSceneInstanceSelection",
            [typeof(DrawingObjectInstanceDefinition), typeof(bool)]);
        select.Invoke(form, [first, false]);
        select.Invoke(form, [second, true]);
        opening.Invoke(menu, [new CancelEventArgs()]);
        AssertTimeline(item.Enabled, "2D Scene canvas did not enable Convert to Symbol for selected instances.");
        var before = new VectorScene();
        SceneCompositionBuilder.Build(before, scene, project.DrawingObjects, 0);
        item.PerformClick();
        AssertTimeline(scene.Instances.Count == 1 && project.DrawingObjects.Count == 2,
            "2D Scene context conversion did not replace the selected instances with one symbol.");
        var converted = project.DrawingObjects.Single(symbol => symbol.Id == scene.Instances[0].DrawingObjectId);
        AssertTimeline(converted.Kind == DrawingObjectAssetKinds.Basic
            && converted.Instances.Select(instance => instance.Id).SequenceEqual(originalIds),
            "2D Scene conversion changed member identities/order or created a 3D symbol.");
        foreach (var frame in new[] { 0, 6 })
        {
            var composed = new VectorScene();
            SceneCompositionBuilder.Build(composed, scene, project.DrawingObjects, frame);
            AssertTimeline(composed.ObjectCount == 2, "Converted scene lost a symbol member.");
            if (frame == 0)
            {
                for (var index = 0; index < before.ObjectCount; index++)
                {
                    AssertTimeline(NearlyEqual(composed.X[index], before.X[index])
                        && NearlyEqual(composed.Y[index], before.Y[index]),
                        "2D Scene conversion changed a member's rendered position.");
                }
            }
        }
        var member = converted.Instances.Single(instance => instance.Id == first.Id);
        AssertTimeline(NearlyEqual(member.EvaluateState(6).X + scene.Instances[0].X, 350)
            && NearlyEqual(member.EvaluateState(6).RotationZ, 20),
            "Scene conversion changed member animation keyframes.");
        AssertTimeline(RequireMethod(typeof(MainForm), "UndoLastEdit").Invoke(form, null) is true
            && project.DrawingObjects.Count == 1
            && scene.Instances.Select(instance => instance.Id).SequenceEqual(originalIds),
            "One undo did not restore the original 2D scene instances.");
        select.Invoke(form, [first, false]);
        opening.Invoke(menu, [new CancelEventArgs()]);
        item.PerformClick();
        AssertTimeline(project.DrawingObjects.Count == 2 && scene.Instances.Count == 2,
            "A single 2D scene instance could not be converted to a reusable wrapper symbol.");
        Console.WriteLine("symbol_conversion_context_menu=drawing,2d_scene,single,multiple,animation,undo");
    }
}
