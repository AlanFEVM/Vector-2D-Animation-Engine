using System.Drawing.Imaging;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void CheckFlashCompositionMaskProjectedFilterPixels()
    {
        for (var mode = 0; mode < 4; mode++)
        {
            var scene = new VectorScene();
            scene.CreateEmpty(4);
            scene.LayerKinds[0] = scene.LayerKinds[3] = DrawingLayerKind.Mask;
            scene.LayerLocked[0] = scene.LayerLocked[3] = true;
            scene.LayerKinds[1] = DrawingLayerKind.Folder;
            scene.LayerParentIds[2] = scene.LayerIds[1];
            scene.AddObject(0, PointF.Empty, new SizeF(200, 600), 0, 0, Color.White, 4, ShapeKind.Rectangle);
            var sourceX = mode == 3 ? 600 : 300;
            var source = scene.AddObject(2, new PointF(sourceX, 0), new SizeF(40, 40), 0, 0, Color.Red, 4, ShapeKind.Rectangle);
            scene.AddObject(3, new PointF(mode == 1 ? 400 : sourceX, 0), new SizeF(100, 100),
                0, 0, Color.White, 4, ShapeKind.Rectangle);
            scene.SetLayerSymbolFilters(mode == 2 ? 2 : 1, new SymbolFilters
            {
                Shadow = SymbolShadowFilter.Default with
                    { BlurX = 0, BlurY = 0, Angle = 180, Distance = mode == 3 ? 20 : 10, Opacity = 1 }
            });
            var plan = new SceneCompositionMaskPlan(
                new() { [1] = new[] { 0 }, [2] = mode == 2 ? new[] { 0 } : new[] { 0, 3 } },
                new() { [0] = new[] { 0 }, [3] = new[] { 3 } }, new());
            using var stage = new StageControl(scene) { Size = new Size(256, 256) };
            stage.SetSceneCompositionResult(new SceneCompositionResult { LayerMasks = plan }, scene, requestPaint: false);
            stage.RestoreViewState(stage.CaptureViewState() with
                { ReferenceZoomScale = 8, ReferenceTargetX = 0, ReferenceTargetY = 0 });
            using var bitmap = RenderGdiMaskObjects(stage, referenceProjection: true);
            AssertTimeline(stage.TryProjectScenePoint(source, new PointF(sourceX, 0), out var projectedSource, out _),
                "Could not project the filter source fixture.");
            var sourcePoint = Point.Round(projectedSource);
            var shadowPoint = new Point(sourcePoint.X - (mode == 3 ? 160 : 80), sourcePoint.Y);
            var shadow = bitmap.GetPixel(shadowPoint.X, shadowPoint.Y);
            AssertTimeline((shadow.A > 250) == (mode != 1)
                && (mode == 3 ? sourcePoint.X >= bitmap.Width : bitmap.GetPixel(sourcePoint.X, sourcePoint.Y).A == 0),
                $"Projected filter masks lost parent input, inner clipping, or the final group boundary: mode={mode}, shadow={shadow}.");
            AssertTimeline(bitmap.GetPixel(95, sourcePoint.Y).A == 0,
                "Projected filter output leaked outside the completed group's mask.");
            var requiresSoftware = typeof(Direct2DStageRenderer).GetMethod("RequiresSoftwareLayerCompositing",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Missing software layer compositing route.");
            AssertTimeline(requiresSoftware.Invoke(null, [stage]) is true,
                "Projected filter groups bypassed their existing GDI compositing route.");
        }
        Console.WriteLine("flash_stage_projected_mask_parent_filter_inner_clip_leaf_filter_boundary=ok");
    }

    private static void CheckFlashCompositionMaskProjectedPixels()
    {
        foreach (var hole in new[] { false, true })
        {
            var scene = new VectorScene();
            scene.CreateEmpty(4);
            scene.LayerKinds[0] = scene.LayerKinds[2] = DrawingLayerKind.Mask;
            scene.LayerLocked[0] = scene.LayerLocked[2] = true;
            // The outer mask root is empty: its visible coverage comes entirely
            // from the nested artwork layer, which must itself stay hidden.
            var outerObject = hole
                ? scene.AddPathObjectContours(1,
                    [new[] { new PointF(-200, -200), new PointF(200, -200), new PointF(200, 200), new PointF(-200, 200) },
                     new[] { new PointF(-50, -50), new PointF(50, -50), new PointF(50, 50), new PointF(-50, 50) }],
                    0, Color.White, Color.Transparent, 4)
                : scene.AddObject(1, PointF.Empty, new SizeF(400, 400), 0, 0, Color.White, 4, ShapeKind.Ellipse);
            scene.AddObject(2, new PointF(100, 0), new SizeF(400, 400), 0, 0, Color.White, 4, ShapeKind.Rectangle);
            var content = scene.AddObject(3, PointF.Empty, new SizeF(1000, 1000), 0, 0, Color.Teal, 4, ShapeKind.Rectangle);
            var plan = new SceneCompositionMaskPlan(new() { [3] = new[] { 0, 2 } },
                new() { [0] = new[] { 1 }, [2] = new[] { 2 } }, new() { [1] = new[] { 0 } });
            using var stage = new StageControl(scene) { Size = new Size(256, 256) };
            stage.SetSceneCompositionResult(new SceneCompositionResult { LayerMasks = plan }, scene, requestPaint: false);
            stage.RestoreViewState(stage.CaptureViewState() with
            {
                CameraX = 17, CameraY = -31, Zoom = 2,
                ReferenceZoomScale = 8, ReferenceTargetX = 0, ReferenceTargetY = 0,
                CanvasRotation = 0.17f
            });
            AssertTimeline(stage.UsesSpatialFrontProjection, "Projected mask fixture did not use the Scene Building 2D front pipeline.");
            if (!hole)
            {
                using var curve = stage.CreateProjectedCompositionMaskObjectPath(outerObject);
                AssertTimeline(curve.PathTypes.Any(type => (type & 7) == 3),
                    "Orthographic mask projection sampled an ellipse instead of retaining its Bezier curves.");
            }
            using var gdi = RenderGdiMaskObjects(stage, referenceProjection: true);
            using var direct = RenderDirect2DObjects(stage, referenceProjection: true);
            Color At(Bitmap bitmap, float x, float y)
            {
                AssertTimeline(stage.TryProjectScenePoint(content, new PointF(x, y), out var screen, out _),
                    "Could not project a mask fixture pixel.");
                var point = Point.Round(screen);
                AssertTimeline((uint)point.X < bitmap.Width && (uint)point.Y < bitmap.Height,
                    "Projected mask fixture pixel is outside its viewport.");
                return bitmap.GetPixel(point.X, point.Y);
            }
            static bool Teal(Color color) => color.R <= 3 && Math.Abs(color.G - 128) <= 3 && Math.Abs(color.B - 128) <= 3;
            bool HitContent(float x, float y)
            {
                AssertTimeline(stage.TryProjectScenePoint(content, new PointF(x, y), out var screen, out _),
                    "Could not project a mask hit-test fixture point.");
                return stage.TryHitTestProjectedObjectFiltered(Point.Round(screen), 1, out var hit,
                    index => index == content) && hit == content;
            }
            AssertTimeline(HitContent(100, 0) && HitContent(0, 0) == !hole
                && !HitContent(250, 0) && !HitContent(-150, 0)
                && (hole || !HitContent(180, 180)),
                "Projected point selection ignored nested mask intersection, curved coverage, or a hole.");
            AssertTimeline(stage.TryProjectScenePoint(outerObject, new PointF(-150, 0), out var artworkPoint, out _)
                && !stage.TryHitTestProjectedObjectFiltered(Point.Round(artworkPoint), 1, out _,
                    index => index == outerObject),
                "Projected point selection exposed locked nested mask artwork.");
            foreach (var bitmap in new[] { gdi, direct })
            {
                AssertTimeline(Teal(At(bitmap, 100, 0)) && Teal(At(bitmap, 0, 0)) == !hole,
                    "Projected nested masks lost their intersection or compound hole.");
                foreach (var outside in new[] { new PointF(250, 0), new PointF(-150, 0), new PointF(180, 180) })
                {
                    if (hole && outside.X == 180) continue;
                    AssertTimeline(!Teal(At(bitmap, outside.X, outside.Y)),
                        "Projected nested masks leaked content or replaced curved coverage by bounds.");
                }
                AssertTimeline(At(bitmap, -150, 0).ToArgb() != Color.White.ToArgb(),
                    "Projected nested mask artwork was displayed outside the masked content.");
            }
            if (!hole)
            {
                var definition = VectorProject.CreateEmpty().Scenes[0];
                definition.Camera.Projection = CameraProjection.Orthographic;
                stage.ConfigureReferenceView(definition, SceneDimension.ThreeD, ReferenceCameraMotion.Immediate);
                stage.RestoreViewState(stage.CaptureViewState() with
                {
                    ReferenceYaw = 0.8f, ReferencePitch = 0, ReferenceDistance = 2000,
                    ReferenceTargetX = 1800f / MathF.Sin(0.8f), ReferenceTargetY = 0, ReferenceTargetZ = 0
                });
                using var clipped = stage.CreateProjectedCompositionMaskObjectPath(outerObject);
                AssertTimeline(clipped.PointCount > 0 && !clipped.PathTypes.Any(type => (type & 7) == 3),
                    "An orthographic mask crossing the near plane incorrectly used an unclipped affine curve.");
            }
        }
        Console.WriteLine("flash_stage_projected_nested_mask_curve_hole_gdi_direct2d_dc=ok");
        Console.WriteLine("flash_stage_projected_nested_mask_point_selection_curve_hole_hidden_artwork=ok");
    }

    private static void CheckFlashCompositionMaskUnderlayPixels()
    {
        var project = VectorProject.CreateEmpty();
        var host = project.DrawingObjects[0];
        var child = project.AddDrawingObject("Masked underlay child");
        var mask = child.Scene.AddMaskLayer("Child mask");
        child.Scene.LayerLocked[mask] = true;
        child.Scene.AddObject(mask, PointF.Empty, new SizeF(20, 20), 0, 0, Color.White, 4, ShapeKind.Ellipse);
        child.Scene.AddObject(1, PointF.Empty, new SizeF(40, 40), 0, 0, Color.Teal, 4, ShapeKind.Rectangle);
        AssertTimeline(project.TryAddDrawingObjectInstance(host.Id, child.Id, PointF.Empty,
            host.Scene.LayerIds[0], out _), "Could not prepare a masked nested underlay.");
        var underlay = new VectorScene();
        var result = SceneCompositionBuilder.BuildDrawingObjectChildren(underlay, host,
            project.DrawingObjects, 0, 24m, compositeWithHostLayers: true);
        using var stage = new StageControl(host.Scene) { Size = new Size(200, 200) };
        stage.RestoreViewState(stage.CaptureViewState() with { CameraX = 0, CameraY = 0, Zoom = 64 });
        stage.BindUnderlayComposition(underlay, result);
        AssertTimeline(ReferenceEquals(stage.CompositionMasksFor(underlay), result.LayerMasks)
            && stage.CompositionMasksFor(host.Scene) is null
            && stage.CompositionMasksFor(new VectorScene()) is null,
            "Underlay mask binding leaked composition indices into another scene.");
        foreach (var source in new VectorScene?[] { underlay, null })
        {
            using var gdi = RenderGdiMaskObjects(stage, source);
            using var direct = RenderDirect2DObjects(stage, source);
            var center = Point.Round(stage.WorldToScreen(0, 0));
            var outside = Point.Round(stage.WorldToScreen(15, 0));
            var corner = Point.Round(stage.WorldToScreen(9, 9));
            AssertTimeline(gdi.GetPixel(center.X, center.Y).G == 128
                && gdi.GetPixel(outside.X, outside.Y).A == 0
                && gdi.GetPixel(corner.X, corner.Y).A == 0,
                "GDI nested underlay lost its curved mask or displayed mask artwork.");
            var visible = direct.GetPixel(center.X, center.Y);
            AssertTimeline(visible.R <= 3 && Math.Abs(visible.G - 128) <= 3
                && direct.GetPixel(outside.X, outside.Y).R >= 252
                && direct.GetPixel(corner.X, corner.Y).R >= 252,
                "Direct2D nested underlay lost its curved mask or displayed mask artwork.");
        }
        AssertTimeline(ReferenceEquals(stage.Scene, host.Scene), "Underlay rendering failed to restore the editable scene.");
        stage.BindUnderlayScene(underlay, result.HostLayerRanges);
        AssertTimeline(stage.CompositionMasksFor(underlay) is null,
            "Rebinding an ordinary underlay retained a stale composition mask plan.");
        stage.BindUnderlayComposition(underlay, result);
        stage.BindUnderlayScene(null);
        AssertTimeline(stage.CompositionMasksFor(underlay) is null,
            "Clearing an underlay retained its composition mask plan.");
        stage.BindUnderlayComposition(underlay, result);
        stage.BindScene(new VectorScene());
        AssertTimeline(stage.CompositionMasksFor(underlay) is null,
            "Binding another editable scene retained the previous underlay's mask plan.");
        Console.WriteLine("flash_stage_nested_underlay_mask_gdi_direct2d_binding_cleanup=ok");
    }

    private static void CheckFlashCompositionMaskStagePixels(StageControl stage, bool blank, bool hole, bool curved)
    {
        using var bitmap = RenderGdiMaskObjects(stage);
        Color Sample(float x, float y)
        {
            var screen = Point.Round(stage.WorldToScreen(x, y));
            AssertTimeline((uint)screen.X < bitmap.Width && (uint)screen.Y < bitmap.Height,
                "The Stage mask fixture sample is outside the rendered viewport.");
            return bitmap.GetPixel(screen.X, screen.Y);
        }
        AssertTimeline(Sample(118, 50).A == 0 && Sample(98, 50).A == 0,
            "GDI Stage leaked masked content or displayed a nested mask's artwork.");
        var center = Sample(107, 50);
        AssertTimeline(blank || hole ? center.A == 0 : center.A > 250 && center.G == 128 && center.B == 128,
            "GDI Stage lost the nested mask center, hole, or blank exposure.");
        var visible = Sample(102, 50);
        AssertTimeline(blank ? visible.A == 0 : visible.A > 250 && visible.G == 128 && visible.B == 128,
            "GDI Stage failed to draw the intersection of nested mask occurrences.");
        if (curved) AssertTimeline(Sample(101, 41).A == 0, "GDI Stage replaced the curved mask by its bounds.");
        Console.WriteLine($"flash_stage_nested_mask_gdi_pixels=ok,blank={blank},hole={hole}");

        using var direct = RenderDirect2DObjects(stage);
        Color Direct(float x, float y)
        {
            var screen = Point.Round(stage.WorldToScreen(x, y));
            return direct.GetPixel(screen.X, screen.Y);
        }
        static bool Teal(Color color) => color.R <= 3 && Math.Abs(color.G - 128) <= 3 && Math.Abs(color.B - 128) <= 3;
        static bool Sentinel(Color color) => color.R >= 252 && color.G <= 3 && color.B >= 252;
        AssertTimeline(Sentinel(Direct(118, 50)) && Sentinel(Direct(98, 50)),
            $"Direct2D Stage leaked masked content or nested mask artwork: outside={Direct(118, 50)}, artwork={Direct(98, 50)}.");
        AssertTimeline(Teal(Direct(107, 50)) == (!blank && !hole)
            && Teal(Direct(102, 50)) == !blank,
            $"Direct2D Stage lost nested mask coverage: center={Direct(107, 50)}, inside={Direct(102, 50)}.");
        if (curved) AssertTimeline(Sentinel(Direct(101, 41)), "Direct2D Stage replaced the curved mask by its bounds.");
        Console.WriteLine($"flash_stage_nested_mask_direct2d_dc_pixels=ok,blank={blank},hole={hole}");
    }

    private static void CheckFlashCompositionMaskFilterStagePixels()
    {
        var scene = new VectorScene();
        scene.CreateEmpty(layers: 3);
        scene.LayerKinds[0] = DrawingLayerKind.Mask;
        scene.LayerLocked[0] = true;
        scene.LayerKinds[1] = DrawingLayerKind.Folder;
        scene.LayerParentIds[2] = scene.LayerIds[1];
        scene.AddObject(0, new PointF(15, 32), new SizeF(30, 64), 0, 0, Color.White, 4, ShapeKind.Rectangle);
        // The source is outside the mask, but its leftward shadow is inside it.
        // Pre-clipping the child before the parent's filter would erase both.
        scene.AddObject(2, new PointF(44, 24), new SizeF(8, 8), 0, 0, Color.Red, 4, ShapeKind.Rectangle);
        scene.SetLayerSymbolFilters(1, new SymbolFilters
        {
            Shadow = SymbolShadowFilter.Default with
                { BlurX = 0, BlurY = 0, Angle = 180, Distance = 30, Opacity = 1 }
        });
        var result = new SceneCompositionResult
        {
            LayerMasks = new(new() { [1] = new[] { 0 }, [2] = new[] { 0 } },
                new() { [0] = new[] { 0 } }, new())
        };
        using var stage = new StageControl(scene) { Size = new Size(64, 64) };
        stage.SetSceneCompositionResult(result, scene, requestPaint: false);
        stage.RestoreViewState(stage.CaptureViewState() with { CameraX = 32, CameraY = 32, Zoom = 25 });
        using var output = RenderGdiMaskObjects(stage);
        AssertTimeline(output.GetPixel(14, 24).A > 250 && output.GetPixel(44, 24).A == 0,
            $"Stage masked its parent's filter input or failed to clip the completed group: shadow={output.GetPixel(14, 24)}, source={output.GetPixel(44, 24)}.");
        Console.WriteLine("flash_stage_mask_parent_filter_input_and_final_boundary=ok");
    }

    private static void CheckFlashCompositionMaskUnionStagePixels(VectorScene scene, SceneCompositionMaskPlan plan)
    {
        scene.AddObject(1, PointF.Empty, new SizeF(100, 100), 0, 0, Color.Teal, 4, ShapeKind.Rectangle);
        using var stage = new StageControl(scene) { Size = new Size(200, 200) };
        stage.RestoreViewState(stage.CaptureViewState() with { CameraX = 0, CameraY = 0, Zoom = 64 });
        stage.SetSceneCompositionResult(new SceneCompositionResult { LayerMasks = plan }, scene, requestPaint: false);
        using var gdi = RenderGdiMaskObjects(stage);
        using var direct = RenderDirect2DObjects(stage);
        foreach (var bitmap in new[] { gdi, direct })
        {
            foreach (var x in new[] { 0f, 14f })
            {
                var sample = Point.Round(stage.WorldToScreen(x, 0));
                var color = bitmap.GetPixel(sample.X, sample.Y);
                AssertTimeline(color.R <= 3 && Math.Abs(color.G - 128) <= 3 && Math.Abs(color.B - 128) <= 3,
                    "A Stage mask combined overlapping sibling objects by XOR instead of union.");
            }
        }
        Console.WriteLine("flash_stage_mask_overlap_union_gdi_direct2d=ok");
    }

    private static Bitmap RenderGdiMaskObjects(StageControl stage, VectorScene? scene = null, bool referenceProjection = false)
    {
        var bitmap = new Bitmap(stage.Width, stage.Height, PixelFormat.Format32bppPArgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.Transparent);
            if (scene is null && stage.HasLayeredUnderlay && stage.UnderlayScene is { } underlay)
            {
                ((SceneRenderOrderBuffer)RequireField(typeof(StageControl), "_underlayRenderOrder").GetValue(stage)!)
                    .Collect(underlay, stage.VisibleWorldBounds(), stage.Frame);
                RequireField(typeof(StageControl), "_underlayDrawLimit").SetValue(stage, int.MaxValue);
            }
            var draw = typeof(StageControl).GetMethod(referenceProjection ? "DrawReference3DCurrentScene"
                : scene is null ? "DrawObjects" : "DrawSceneGdi", PrivateInstanceFlags,
                null, referenceProjection ? [typeof(Graphics)] : scene is null ? [typeof(Graphics), typeof(int)]
                    : [typeof(Graphics), typeof(VectorScene), typeof(int), typeof(bool)], null)
                ?? throw new InvalidOperationException("Missing GDI Stage object rendering entry point.");
            draw.Invoke(stage, referenceProjection ? [graphics]
                : scene is null ? [graphics, int.MaxValue] : [graphics, scene, int.MaxValue, false]);
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    private static Bitmap RenderDirect2DObjects(StageControl stage, VectorScene? scene = null, bool referenceProjection = false)
    {
        var bitmap = new Bitmap(stage.Width, stage.Height, PixelFormat.Format32bppPArgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            var dc = graphics.GetHdc();
            try
            {
                using var renderer = new Direct2DStageRenderer();
                var factory = Vortice.Direct2D1.D2D1.D2D1CreateFactory<Vortice.Direct2D1.ID2D1Factory1>(
                    Vortice.Direct2D1.FactoryType.SingleThreaded, Vortice.Direct2D1.DebugLevel.None);
                RequireField(typeof(Direct2DStageRenderer), "_factory").SetValue(renderer, factory);
                var target = factory.CreateDCRenderTarget(new Vortice.Direct2D1.RenderTargetProperties(
                    Vortice.Direct2D1.RenderTargetType.Default,
                    new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                    96, 96, Vortice.Direct2D1.RenderTargetUsage.None, Vortice.Direct2D1.FeatureLevel.Default));
                RequireField(typeof(Direct2DStageRenderer), "_target").SetValue(renderer, target);
                RequireField(typeof(Direct2DStageRenderer), "_targetSize").SetValue(renderer,
                    new Vortice.Mathematics.SizeI(stage.Width, stage.Height));
                target.BindDC(dc, new Vortice.RawRect(0, 0, stage.Width, stage.Height));
                target.BeginDraw();
                var sentinel = new Vortice.Mathematics.Color(1f, 0f, 1f, 1f);
                target.Clear(in sentinel);
                if (scene is null && stage.HasLayeredUnderlay && stage.UnderlayScene is { } underlay)
                {
                    ((SceneRenderOrderBuffer)RequireField(typeof(Direct2DStageRenderer), "_underlayRenderOrder").GetValue(renderer)!)
                        .Collect(underlay, stage.VisibleWorldBounds(), stage.Frame);
                    RequireField(typeof(Direct2DStageRenderer), "_underlayDrawLimit").SetValue(renderer, int.MaxValue);
                }
                var draw = typeof(Direct2DStageRenderer).GetMethod(referenceProjection ? "DrawReference3DCurrentScene"
                    : scene is null ? "DrawObjects" : "DrawScene", PrivateInstanceFlags,
                    null, referenceProjection ? [typeof(StageControl)] : scene is null ? [typeof(StageControl), typeof(int)]
                        : [typeof(StageControl), typeof(int), typeof(bool)], null)
                    ?? throw new InvalidOperationException("Missing Direct2D Stage object rendering entry point.");
                var editableScene = stage.Scene;
                try
                {
                    if (scene is not null) stage.Scene = scene;
                    draw.Invoke(renderer, referenceProjection ? [stage]
                        : scene is null ? [stage, int.MaxValue] : [stage, int.MaxValue, false]);
                }
                finally { stage.Scene = editableScene; }
                target.EndDraw().CheckError();
            }
            finally { graphics.ReleaseHdc(dc); }
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }
}
