namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void CheckFlashExactSvgMaskCoverage()
    {
        var scene = new VectorScene();
        scene.CreateEmpty(2);
        scene.LayerKinds[0] = DrawingLayerKind.Mask;
        scene.LayerLocked[0] = true;
        var svg = scene.AddImportedSvgObject(0, PointF.Empty, new SizeF(100, 100),
            "<svg xmlns='http://www.w3.org/2000/svg' width='100' height='100' viewBox='0 0 100 100'><path fill='red' fill-rule='evenodd' d='M10 10H90V90H10Z M30 30H70V70H30Z'/><rect fill='blue' x='45' y='10' width='10' height='80'/></svg>");
        var content = scene.AddObject(1, PointF.Empty, new SizeF(200, 200), 0, 0, Color.Teal, 4, ShapeKind.Rectangle);
        var plan = new SceneCompositionMaskPlan(new() { [1] = new[] { 0 } }, new() { [0] = new[] { 0 } }, new());
        var instance = new DrawingObjectInstanceDefinition { Id = "svg-mask-content", DrawingObjectId = "svg-content" };
        var composition = new SceneCompositionResult([
            new("svg-mask-artwork", "svg-mask"), new(instance.Id, instance.DrawingObjectId)]) { LayerMasks = plan };
        using var stage = new StageControl(scene) { Size = new Size(256, 256) };
        stage.RestoreViewState(stage.CaptureViewState() with { CameraX = 0, CameraY = 0, Zoom = 25 });
        stage.SetSceneCompositionResult(new SceneCompositionResult { LayerMasks = plan }, scene, requestPaint: false);
        foreach (var angle in new[] { 0f, .4f })
        {
            scene.Angle[svg] = angle;
            var paths = stage.CreateCompositionMaskObjectPaths(scene, svg);
            try { AssertTimeline(paths.Count == 2, "Exact SVG mask discarded independent filled paths."); }
            finally { foreach (var path in paths) path.Dispose(); }
            using var gdi = RenderGdiMaskObjects(stage);
            using var direct = RenderDirect2DObjects(stage);
            foreach (var (x, y, visible) in new[] { (0f, 0f, true), (-30f, 0f, true), (-15f, 0f, false),
                (45f, 0f, false), (0f, -30f, true) })
            {
                var world = new PointF(x * MathF.Cos(angle) - y * MathF.Sin(angle),
                    x * MathF.Sin(angle) + y * MathF.Cos(angle));
                var point = Point.Round(stage.WorldToScreen(world.X, world.Y));
                AssertTimeline((gdi.GetPixel(point.X, point.Y).A > 0) == visible
                    && (direct.GetPixel(point.X, point.Y).ToArgb() != Color.Fuchsia.ToArgb()) == visible,
                    "Exact SVG mask lost its hole, overlapping fills, transform, or exterior coverage.");
                var resolved = MainForm.TryResolveCompositionInstance(scene, composition, [instance],
                    world, 0, 1, out var selected, maskStage: stage);
                AssertTimeline(resolved == visible && (!resolved || ReferenceEquals(selected, instance)),
                    "Ordinary 2D instance picking ignored the composed SVG mask coverage or root owner.");
            }
            stage.RestoreViewState(stage.CaptureViewState() with
                { ReferenceTargetX = 0, ReferenceTargetY = 0, ReferenceZoomScale = 64 });
            using var projectedGdi = RenderGdiMaskObjects(stage, referenceProjection: true);
            using var projectedDirect = RenderDirect2DObjects(stage, referenceProjection: true);
            foreach (var (x, y, visible) in new[] { (0f, 0f, true), (-30f, 0f, true), (-15f, 0f, false),
                (45f, 0f, false), (0f, -30f, true) })
            {
                var world = new PointF(x * MathF.Cos(angle) - y * MathF.Sin(angle),
                    x * MathF.Sin(angle) + y * MathF.Cos(angle));
                AssertTimeline(stage.TryProjectScenePoint(content, world, out var screen, out _),
                    "Could not project SVG mask fixture.");
                var point = Point.Round(screen);
                AssertTimeline((projectedGdi.GetPixel(point.X, point.Y).A > 0) == visible
                    && (projectedDirect.GetPixel(point.X, point.Y).ToArgb() != Color.Fuchsia.ToArgb()) == visible
                    && stage.TryHitTestProjectedObjectFiltered(point, 1, out _, index => index == content) == visible,
                    "Orthographic SVG mask drawing and picking disagree on independent fills or holes.");
            }
        }
        Console.WriteLine("flash_stage_exact_svg_mask_independent_fill_union_hole_rotation_gdi_direct2d=ok");
        Console.WriteLine("flash_stage_exact_svg_mask_orthographic_gdi_direct2d_picking=ok");
        Console.WriteLine("flash_stage_exact_svg_mask_flat_instance_picking_owner=ok");
    }
}
