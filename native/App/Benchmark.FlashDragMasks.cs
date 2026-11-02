namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void CheckFlashCompositionMaskDragPreview()
    {
        using var form = new MainForm { ShowInTaskbar = false, Size = new Size(1280, 800) };
        var project = (VectorProject)RequireField(typeof(MainForm), "_project").GetValue(form)!;
        var stage = (StageControl)RequireField(typeof(MainForm), "_stage").GetValue(form)!;
        stage.Size = new Size(256, 256);
        stage.RestoreViewState(stage.CaptureViewState() with
            { CameraX = 0, CameraY = 0, Zoom = 64, ReferenceTargetX = 0, ReferenceTargetY = 0, ReferenceZoomScale = 64 });
        var host = project.DrawingObjects[0];
        var artwork = project.AddDrawingObject("Drag nested mask artwork");
        artwork.Scene.AddObject(0, PointF.Empty, new SizeF(20, 20), 0, 0, Color.White, 4, ShapeKind.Ellipse);
        var mask = host.Scene.AddMaskLayer("Drag empty mask root");
        host.Scene.LayerLocked[mask] = true;
        host.Scene.AddObject(1, PointF.Empty, new SizeF(60, 60), 0, 0, Color.Teal, 4, ShapeKind.Rectangle);
        AssertTimeline(project.TryAddDrawingObjectInstance(host.Id, artwork.Id, PointF.Empty,
            host.Scene.LayerIds[mask], out _), "Could not create nested drag mask artwork.");
        var update = RequireMethod(typeof(MainForm), "UpdateDrawingObjectDragPreview");
        SceneCompositionMaskPlan? previousPlan = null;
        VectorScene? previousScene = null;
        foreach (var position in new[] { new PointF(-15, 0), new PointF(15, 0), new PointF(-15, 10) })
        {
            update.Invoke(form, [host, position]);
            var preview = stage.DragPreviewScene ?? throw new InvalidOperationException("Missing drag preview scene.");
            var plan = stage.CompositionMasksFor(preview);
            AssertTimeline(plan is { ContentsByMaskLayer.Count: 1 } && stage.CompositionMasksFor(stage.Scene) is null,
                "Drag preview lost its composition mask or leaked it into the editable scene.");
            if (previousScene is not null)
                AssertTimeline(ReferenceEquals(previousScene, preview) && ReferenceEquals(previousPlan, plan),
                    "A drag translation rebuilt the entire preview instead of translating its existing geometry.");
            using var gdi = RenderGdiMaskObjects(stage, preview);
            using var direct = RenderDirect2DObjects(stage, preview);
            foreach (var (offset, visible) in new[] { (PointF.Empty, true), (new PointF(18, 0), false),
                (new PointF(9, 9), false) })
            {
                var point = Point.Round(stage.WorldToScreen(position.X + offset.X, position.Y + offset.Y));
                AssertTimeline((gdi.GetPixel(point.X, point.Y).A > 0) == visible
                    && (direct.GetPixel(point.X, point.Y).ToArgb() != Color.Fuchsia.ToArgb()) == visible,
                    $"Drag preview retained an obsolete mask location or lost curved coverage: position={position}, offset={offset}.");
            }
            var editableScene = stage.Scene;
            try
            {
                // Match the temporary scene swap used by the projected drag
                // overlay, keeping the editable composition binding intact.
                stage.Scene = preview;
                var content = Enumerable.Range(0, preview.ObjectCount)
                    .First(index => preview.ShapeKind[index] == ShapeKind.Rectangle);
                using var projectedGdi = RenderGdiMaskObjects(stage, referenceProjection: true);
                using var projectedDirect = RenderDirect2DObjects(stage, referenceProjection: true);
                foreach (var (offset, visible) in new[] { (PointF.Empty, true), (new PointF(18, 0), false),
                    (new PointF(9, 9), false) })
                {
                    AssertTimeline(stage.TryProjectScenePoint(content,
                        new PointF(position.X + offset.X, position.Y + offset.Y), out var screen, out _),
                        "Could not project a drag mask sample.");
                    var point = Point.Round(screen);
                    AssertTimeline((projectedGdi.GetPixel(point.X, point.Y).A > 0) == visible
                        && (projectedDirect.GetPixel(point.X, point.Y).ToArgb() != Color.Fuchsia.ToArgb()) == visible,
                        "Projected drag overlay discarded its mask binding or reused obsolete coverage.");
                }
            }
            finally { stage.Scene = editableScene; }
            previousScene = preview;
            previousPlan = plan;
        }
        var last = stage.DragPreviewScene!;
        stage.BindDragPreviewScene(last, stage.Scene, [0]);
        AssertTimeline(stage.CompositionMasksFor(last) is null && stage.IsHiddenByDragPreview(stage.Scene, 0),
            "Plain drag binding retained a stale plan or lost source-object hiding.");
        stage.BindDragPreviewScene(null);
        AssertTimeline(!stage.IsHiddenByDragPreview(stage.Scene, 0) && stage.CompositionMasksFor(last) is null,
            "Clearing drag preview retained source hiding or mask indices.");
        update.Invoke(form, [host, PointF.Empty]);
        AssertTimeline(stage.CompositionMasksFor(stage.DragPreviewScene!) is not null,
            "Restarting a cleared drag failed to restore its composition plan.");
        stage.BindScene(stage.Scene);
        AssertTimeline(stage.DragPreviewScene is null && stage.CompositionMasksFor(last) is null,
            "Scene rebinding retained the previous drag mask.");
        Console.WriteLine("flash_stage_drag_preview_nested_mask_translation_curve_projected_gdi_direct2d_binding_cleanup=ok");
    }
}
