namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void CheckFlashCompositionMaskPlaybackBinding()
    {
        var scene = new VectorScene();
        scene.CreateEmpty(2);
        scene.LayerKinds[0] = DrawingLayerKind.Mask;
        scene.LayerLocked[0] = true;
        scene.AddObject(0, PointF.Empty, new SizeF(400, 400), 0, 0, Color.White, 4, ShapeKind.Ellipse);
        var content = scene.AddObject(1, PointF.Empty, new SizeF(1000, 1000), 0, 0, Color.Teal, 4, ShapeKind.Rectangle);
        var filled = new SceneCompositionResult
        {
            LayerMasks = new(new() { [1] = new[] { 0 } }, new() { [0] = new[] { 0 } }, new())
        };
        var blank = new SceneCompositionResult
        {
            LayerMasks = new(new() { [1] = new[] { 0 } }, new() { [0] = Array.Empty<int>() }, new())
        };
        using var stage = new StageControl(scene) { Size = new Size(256, 256) };
        stage.SetSceneCompositionResult(new SceneCompositionResult(), scene, requestPaint: false);
        stage.RestoreViewState(stage.CaptureViewState() with
            { ReferenceZoomScale = 8, ReferenceTargetX = 0, ReferenceTargetY = 0 });
        stage.SetReference3DPlaybackActive(true);
        using var renderer = new Direct2DStageRenderer();
        // This scene is eligible without its composition-only mask, ensuring
        // the negative checks below exercise mask handling rather than another
        // unrelated raster limitation or command-count threshold.
        var prepared = renderer.PrepareReference3DPlaybackRaster(stage);
        AssertTimeline(prepared is not null, "Playback mask fixture's unmasked raster control was ineligible.");
        var playbackKey = typeof(Direct2DStageRenderer).GetMethod("CreateReference3DPlaybackViewKey",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing playback view key.");
        var workspaceKey = RequireMethod(typeof(Direct2DStageRenderer), "CreateReference3DWorkspaceFrameCacheKey");
        var previousPlaybackKey = playbackKey.Invoke(null, [stage]);
        var previousWorkspaceKey = workspaceKey.Invoke(renderer, [stage]);
        var revision = stage.Reference3DWorkspaceFrameCacheRevision;
        var geometryRevision = scene.GeometryRevision;
        foreach (var result in new[] { filled, blank, filled })
        {
            stage.SetReference3DPlaybackComposition(scene, result, prepared, null);
            AssertTimeline(scene.GeometryRevision == geometryRevision && stage.Frame == 0
                && stage.Reference3DWorkspaceFrameCacheRevision == revision,
                "Playback mask fixture accidentally invalidated unrelated scene/frame state.");
            var currentPlaybackKey = playbackKey.Invoke(null, [stage]);
            var currentWorkspaceKey = workspaceKey.Invoke(renderer, [stage]);
            AssertTimeline(!Equals(previousPlaybackKey, currentPlaybackKey)
                && !Equals(previousWorkspaceKey, currentWorkspaceKey),
                "Playback or workspace cache reused a frame after only its mask plan changed.");
            AssertTimeline(!renderer.TryGetPreparedPlaybackRenderItems(stage, prepared, out _)
                && renderer.PrepareReference3DPlaybackRaster(stage) is null
                && renderer.LastReference3DCpuRasterFallbackReason == "composition_mask"
                && renderer.PrepareReference3DPlaybackRasterFrame(stage, prepared) is null,
                "Playback reused or prepared an unmasked raster for a composition mask.");
            using var gdi = RenderGdiMaskObjects(stage, referenceProjection: true);
            using var direct = RenderDirect2DObjects(stage, referenceProjection: true);
            var centerProjected = stage.TryProjectScenePoint(content, PointF.Empty, out var centerScreen, out _);
            var outsideProjected = stage.TryProjectScenePoint(content, new PointF(300, 0), out var outsideScreen, out _);
            AssertTimeline(centerProjected && outsideProjected, "Could not project the playback mask fixture.");
            var center = Point.Round(centerScreen);
            var outside = Point.Round(outsideScreen);
            var visible = ReferenceEquals(result, filled);
            AssertTimeline(stage.TryHitTestProjectedObjectFiltered(center, 1, out var hit,
                    index => index == content) == visible
                && (!visible || hit == content)
                && !stage.TryHitTestProjectedObjectFiltered(outside, 1, out _, index => index == content),
                "Projected picking retained an obsolete filled/blank composition mask.");
            static bool Teal(Color color) => color.R <= 3 && Math.Abs(color.G - 128) <= 3 && Math.Abs(color.B - 128) <= 3;
            AssertTimeline(Teal(gdi.GetPixel(center.X, center.Y)) == visible
                && Teal(direct.GetPixel(center.X, center.Y)) == visible
                && gdi.GetPixel(outside.X, outside.Y).A == 0
                && !Teal(direct.GetPixel(outside.X, outside.Y)),
                "Playback vector rendering retained an obsolete filled/blank mask frame.");
            previousPlaybackKey = currentPlaybackKey;
            previousWorkspaceKey = currentWorkspaceKey;
        }
        stage.SetReference3DPlaybackActive(false);
        Console.WriteLine("flash_stage_playback_mask_binding_raster_guard_cache_keys_filled_blank_gdi_direct2d=ok");
    }
}
