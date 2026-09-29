namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunInstanceSkewPreviewRegression()
    {
        using var form = new MainForm
        {
            ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
            Location = new Point(-30000, -30000), Size = new Size(1280, 800)
        };
        var project = (VectorProject)RequireField(typeof(MainForm), "_project").GetValue(form)!;
        var tabs = (WorkspaceTabs)RequireField(typeof(MainForm), "_workspaceTabs").GetValue(form)!;
        var source = project.AddDrawingObject("Skew preview regression");
        source.Scene.AddObject(0, PointF.Empty, new SizeF(100, 60), 0, 0, Color.Teal, 0, ShapeKind.Rectangle);
        var container = project.DrawingObjects[0];
        AssertTimeline(project.TryAddDrawingObjectInstance(container.Id, source.Id, PointF.Empty, out var created)
            && created is not null, "Could not create skew preview instance.");
        var instance = created!;
        form.Show();
        tabs.SelectedView = WorkspaceView.BasicDrawing;
        RequireMethod(typeof(MainForm), "OpenDrawingObjectEditor", [typeof(string)]).Invoke(form, [container.Id]);
        Application.DoEvents();
        RequireMethod(typeof(MainForm), "SetSceneInstanceSelection",
            [typeof(DrawingObjectInstanceDefinition), typeof(bool)]).Invoke(form, [instance, false]);
        var begin = RequireMethod(typeof(MainForm), "BeginSceneInstanceTransformPreview");
        var preview = RequireMethod(typeof(MainForm), "PreviewSelectedSceneInstanceStates");
        var rebuild = RequireMethod(typeof(MainForm), "RebuildEditableInstanceComposition");
        var original = instance.EvaluateState(0);
        foreach (var vertical in new[] { false, true })
        {
            instance.SetStateAtFrame(0, original);
            rebuild.Invoke(form, null);
            begin.Invoke(form, null);
            foreach (var degrees in new[] { 12f, 30f, -18f })
            {
                instance.SetStateAtFrame(0, vertical
                    ? original with { SkewY = degrees }
                    : original with { SkewX = degrees });
                AssertTimeline(preview.Invoke(form, null) is true, "Instance skew preview was not applied.");
                var displayed = (VectorScene)RequireField(typeof(MainForm), "_drawingObjectUnderlayStage").GetValue(form)!;
                var committed = new VectorScene();
                SceneCompositionBuilder.BuildDrawingObjectChildren(committed, container, project.DrawingObjects, 0);
                AssertTimeline(displayed.TryGetPathWorldContours(0, out var actual),
                    $"First skew preview approximated shear as a rotated primitive (vertical={vertical}).");
                AssertTimeline(committed.TryGetPathWorldContours(0, out var expected)
                    && actual.Length == expected.Length, "Skew preview lost committed contours.");
                for (var contour = 0; contour < actual.Length; contour++)
                {
                    // Preview paths use Bezier sampling; composition uses primitive
                    // boundary samples. Compare both boundaries, not sample counts.
                    AssertTimeline(actual[contour].All(point => OnBoundary(point, expected[contour]))
                        && expected[contour].All(point => OnBoundary(point, actual[contour])),
                        $"Skew preview differs from committed geometry: vertical={vertical}, degrees={degrees}.");
                }
                AssertTimeline(source.Scene.ShapeKind[0] == ShapeKind.Rectangle, "Skew preview modified the source symbol.");
            }
        }
        Console.WriteLine("instance_skew_preview_commit_parity=ok");

        static bool OnBoundary(PointF point, PointF[] boundary)
        {
            for (var index = 0; index < boundary.Length; index++)
            {
                var start = boundary[index];
                var end = boundary[(index + 1) % boundary.Length];
                var dx = end.X - start.X;
                var dy = end.Y - start.Y;
                var lengthSquared = dx * dx + dy * dy;
                var t = lengthSquared == 0 ? 0 : Math.Clamp(
                    ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared, 0, 1);
                if (PointsNear(point, new PointF(start.X + t * dx, start.Y + t * dy))) return true;
            }
            return false;
        }
    }
}
