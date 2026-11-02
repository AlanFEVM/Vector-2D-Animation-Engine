namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void CheckFlashCompositionMaskLocalOnionSkin()
    {
        var project = VectorProject.CreateEmpty();
        var drawing = project.DrawingObjects[0];
        var scene = drawing.Scene;
        var mask = scene.AddMaskLayer("Local onion mask");
        scene.LayerLocked[mask] = true;
        scene.AddObject(mask, PointF.Empty, new SizeF(20, 20), 0, 0, Color.White, 4, ShapeKind.Ellipse);
        scene.AddObject(1, PointF.Empty, new SizeF(60, 60), 0, 0, Color.Teal, 4, ShapeKind.Rectangle);
        AssertTimeline(scene.InsertTimelineKeyframe(1, 2), "Could not create the local onion exposure fixture.");
        var maskTrack = scene.Timeline.FindTrackByTargetId(scene.LayerIds[mask])!;
        scene.Timeline.SetTrackDuration(maskTrack.Id, 3);
        scene.SetOnionSkinEnabled(true);
        scene.SetOnionSkinRange(1, 0);
        var preview = new VectorScene();
        using var stage = new StageControl(scene) { Size = new Size(256, 256) };
        stage.RestoreViewState(stage.CaptureViewState() with { CameraX = 0, CameraY = 0, Zoom = 64 });
        foreach (var blank in new[] { false, true })
        {
            if (blank)
            {
                AssertTimeline(scene.Timeline.InsertBlankKeyframe(maskTrack.Id, 1), "Could not blank the onion mask dependency.");
            }
            var plan = SceneCompositionBuilder.BuildDrawingObjectOnionSkin(preview, drawing,
                project.DrawingObjects, 2, includeLocalArtwork: true);
            AssertTimeline(plan.ContentsByMaskLayer.Count == 1 && plan.ClipsByLayer.Count > 0,
                "Local onion skin did not compose its mask without nested instances.");
            stage.BindOnionSkinComposition(preview, plan);
            using var gdi = RenderGdiMaskObjects(stage, preview);
            using var direct = RenderDirect2DObjects(stage, preview);
            foreach (var (world, inside) in new[] { (PointF.Empty, true), (new PointF(18, 0), false),
                (new PointF(9, 9), false) })
            {
                var screen = Point.Round(stage.WorldToScreen(world.X, world.Y));
                AssertTimeline((gdi.GetPixel(screen.X, screen.Y).A > 0) == (inside && !blank)
                    && (direct.GetPixel(screen.X, screen.Y).ToArgb() != Color.Fuchsia.ToArgb()) == (inside && !blank),
                    $"Local onion mask lost curved or blank-exposure coverage: blank={blank}, point={world}, "
                        + $"gdi={gdi.GetPixel(screen.X, screen.Y)}, direct={direct.GetPixel(screen.X, screen.Y)}, "
                        + $"objects={preview.ObjectCount}, layers={preview.LayerCount}, "
                        + $"geometry={string.Join(';', Enumerable.Range(0, preview.ObjectCount).Select(i => $"{preview.ShapeKind[i]}@{preview.X[i]},{preview.Y[i]}:layer{preview.ObjectLayer[i]}"))}.");
            }
        }
        Console.WriteLine("flash_stage_local_onion_mask_dependency_curve_blank_gdi_direct2d=ok");
    }

    private static void CheckFlashCompositionMaskDrawingOnionSkin()
    {
        var project = VectorProject.CreateEmpty();
        var root = project.DrawingObjects[0];
        var child = project.AddDrawingObject("Drawing onion masked child");
        var artwork = project.AddDrawingObject("Drawing onion mask artwork");
        artwork.Scene.AddObject(0, PointF.Empty, new SizeF(20, 20), 0, 0, Color.White, 4, ShapeKind.Ellipse);
        var mask = child.Scene.AddMaskLayer("Locked nested onion mask");
        child.Scene.LayerLocked[mask] = true;
        child.Scene.AddObject(1, PointF.Empty, new SizeF(60, 60), 0, 0, Color.Teal, 4, ShapeKind.Rectangle);
        AssertTimeline(project.TryAddDrawingObjectInstance(child.Id, artwork.Id, PointF.Empty,
                child.Scene.LayerIds[mask], out _)
            && project.TryAddDrawingObjectInstance(root.Id, child.Id, PointF.Empty,
                root.Scene.LayerIds[0], out _)
            && root.Scene.InsertTimelineKeyframe(0, 2), "Could not create nested drawing onion fixtures.");
        root.Scene.SetOnionSkinEnabled(true);
        root.Scene.SetOnionSkinRange(1, 0);
        var nested = new VectorScene();
        var plan = SceneCompositionBuilder.BuildDrawingObjectOnionSkin(nested, root, project.DrawingObjects, 2);
        AssertTimeline(plan.ContentsByMaskLayer.Count == 1
            && plan.ContentsByMaskLayer.Keys.All(nested.IsLayerEffectivelyLocked)
            && plan.ContainersByLayer.Count > 0, "Nested drawing onion skin omitted locked mask dependencies.");
        var local = new VectorScene();
        local.CreateEmpty();
        local.AddObject(0, new PointF(30, 0), new SizeF(6, 6), 0, 0, Color.Blue, 4, ShapeKind.Rectangle);
        var combined = new VectorScene();
        combined.CombineOnionSkinPreviews([(local, 1, null), (nested, 1, null)]);
        var combinedPlan = SceneCompositionBuilder.CombineOnionSkinMaskPlans([
            (local, SceneCompositionMaskPlan.Empty), (nested, plan)]);
        AssertTimeline(combinedPlan.ClipsByLayer.Keys.All(index => index >= local.LayerCount),
            "Combined drawing onion masks clipped local preview layers.");
        root.Scene.AddObject(0, new PointF(30, 0), new SizeF(6, 6), 0, 0, Color.Blue, 4, ShapeKind.Rectangle);
        var full = new VectorScene();
        var fullPlan = SceneCompositionBuilder.BuildDrawingObjectOnionSkin(full, root, project.DrawingObjects,
            2, includeLocalArtwork: true);
        AssertTimeline(fullPlan.ContentsByMaskLayer.Count == 1,
            "Full drawing onion composition lost its nested instance's mask.");
        using var stage = new StageControl(root.Scene) { Size = new Size(256, 256) };
        stage.RestoreViewState(stage.CaptureViewState() with { CameraX = 10, CameraY = 0, Zoom = 64 });
        foreach (var (preview, masks) in new[] { (nested, plan), (combined, combinedPlan), (full, fullPlan) })
        {
            stage.BindOnionSkinComposition(preview, masks);
            using var gdi = RenderGdiMaskObjects(stage, preview);
            using var direct = RenderDirect2DObjects(stage, preview);
            foreach (var (world, visible) in new[] { (new PointF(0, 0), true), (new PointF(18, 0), false),
                (new PointF(9, 9), false), (new PointF(30, 0), !ReferenceEquals(preview, nested)) })
            {
                var point = Point.Round(stage.WorldToScreen(world.X, world.Y));
                AssertTimeline((gdi.GetPixel(point.X, point.Y).A > 0) == visible
                    && (direct.GetPixel(point.X, point.Y).ToArgb() != Color.Fuchsia.ToArgb()) == visible,
                    "Nested or mixed drawing onion skin lost curved mask coverage or clipped local artwork.");
            }
        }
        Console.WriteLine("flash_stage_drawing_onion_nested_locked_mask_mixed_local_gdi_direct2d=ok");
    }

    private static void CheckFlashCompositionMaskSceneOnionSkin()
    {
        var project = VectorProject.CreateEmpty();
        var host = project.DrawingObjects[0];
        var child = project.AddDrawingObject("Onion nested mask artwork");
        child.Scene.AddObject(0, PointF.Empty, new SizeF(20, 20), 0, 0, Color.White, 4, ShapeKind.Ellipse);
        var mask = host.Scene.AddMaskLayer("Onion empty mask root");
        host.Scene.LayerLocked[mask] = true;
        host.Scene.AddObject(1, PointF.Empty, new SizeF(60, 60), 0, 0, Color.Teal, 4, ShapeKind.Rectangle);
        AssertTimeline(project.TryAddDrawingObjectInstance(host.Id, child.Id, PointF.Empty,
                host.Scene.LayerIds[mask], out _), "Could not add nested onion mask artwork.");
        var definition = project.Scenes[0];
        AssertTimeline(project.TryAddSceneInstance(definition.Id, host.Id, PointF.Empty, 0, out _),
            "Could not add the masked onion scene instance.");
        var track = definition.Timeline.FindTrackByTargetId(definition.Layers[0].Id)!;
        AssertTimeline(definition.Timeline.InsertKeyframe(track.Id, 2)
            && definition.Timeline.InsertKeyframe(track.Id, 4)
            && definition.Timeline.InsertKeyframe(track.Id, 6),
            "Could not create the onion exposure fixture.");
        definition.SetOnionSkinRange(2, 2);
        definition.SetOnionSkinEnabled(true);
        var preview = new VectorScene();
        var plan = SceneCompositionBuilder.BuildSceneOnionSkin(preview, definition, project.DrawingObjects, 4, 24m);
        AssertTimeline(plan.ContentsByMaskLayer.Count == 2 && plan.ClipsByLayer.Count > 0,
            $"Onion composition discarded a neighbouring frame's mask plan: masks={plan.ContentsByMaskLayer.Count}, "
                + $"clips={plan.ClipsByLayer.Count}, layers={preview.LayerCount}, objects={preview.ObjectCount}, "
                + $"duration={definition.FrameCount}, enabled={definition.HasOnionSkinPreviewEnabled}, "
                + $"keys={string.Join(',', track.Keyframes.Select(key => key.Frame))}.");
        var frameLayerCount = preview.LayerCount / 2;
        AssertTimeline(preview.LayerCount % 2 == 0
            && plan.ContentsByMaskLayer.Keys.Count(index => index < frameLayerCount) == 1
            && plan.ContentsByMaskLayer.Keys.Count(index => index >= frameLayerCount) == 1
            && plan.ClipsByLayer.All(pair => pair.Value.All(maskIndex =>
                pair.Key < frameLayerCount == (maskIndex < frameLayerCount)))
            && plan.ContentsByMaskLayer.Keys.All(preview.IsLayerEffectivelyLocked),
            "Onion masks crossed frame boundaries or lost their locked state.");
        using var stage = new StageControl(host.Scene) { Size = new Size(256, 256) };
        stage.RestoreViewState(stage.CaptureViewState() with { CameraX = 0, CameraY = 0, Zoom = 64 });
        stage.BindOnionSkinComposition(preview, plan);
        AssertTimeline(ReferenceEquals(stage.CompositionMasksFor(preview), plan)
            && stage.CompositionMasksFor(host.Scene) is null, "Onion mask binding leaked to the editable scene.");
        using var gdi = RenderGdiMaskObjects(stage, preview);
        using var direct = RenderDirect2DObjects(stage, preview);
        var center = Point.Round(stage.WorldToScreen(0, 0));
        AssertTimeline(gdi.GetPixel(center.X, center.Y).A > 0
            && direct.GetPixel(center.X, center.Y).ToArgb() != Color.Fuchsia.ToArgb(),
            "Onion masks discarded valid tinted preview content.");
        foreach (var world in new[] { new PointF(18, 0), new PointF(9, 9) })
        {
            var screen = Point.Round(stage.WorldToScreen(world.X, world.Y));
            AssertTimeline(gdi.GetPixel(screen.X, screen.Y).A == 0
                && direct.GetPixel(screen.X, screen.Y).ToArgb() == Color.Fuchsia.ToArgb(),
                "Onion masks leaked content outside nested or curved coverage.");
        }
        stage.BindOnionSkinScene(preview);
        AssertTimeline(stage.CompositionMasksFor(preview) is null, "Plain onion rebinding retained a stale mask plan.");
        stage.BindOnionSkinComposition(preview, plan);
        stage.BindOnionSkinScene(null);
        AssertTimeline(stage.CompositionMasksFor(preview) is null, "Clearing onion skin retained its mask plan.");
        stage.BindOnionSkinComposition(preview, plan);
        stage.BindScene(host.Scene);
        AssertTimeline(stage.CompositionMasksFor(preview) is null, "Scene rebinding retained onion mask indices.");
        definition.SetOnionSkinEnabled(false);
        AssertTimeline(SceneCompositionBuilder.BuildSceneOnionSkin(preview, definition, project.DrawingObjects, 4)
            .ContentsByMaskLayer.Count == 0 && preview.ObjectCount == 0, "Disabled onion skin retained mask content.");
        Console.WriteLine("flash_stage_scene_onion_nested_mask_frame_offsets_curve_gdi_direct2d_binding_cleanup=ok");
    }
}
