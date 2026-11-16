using System.Numerics;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunBitmapLiveDistortionRegression()
    {
        var source = new BitmapImageRaster(new BitmapImageRasterKey("live-warp-texture", 128, 96));
        for (var y = 0; y < source.PixelHeight; y++)
        for (var x = 0; x < source.PixelWidth; x++)
        {
            var offset = y * source.Stride + x * 4;
            source.Pixels[offset] = (byte)(x < 64 ? 20 : 160);
            source.Pixels[offset + 1] = (byte)(y < 48 ? 25 : 130);
            source.Pixels[offset + 2] = (byte)(x < 64 ? 170 : 30);
            source.Pixels[offset + 3] = 200;
        }
        var scene = new VectorScene(); scene.CreateEmpty();
        scene.AddBitmapObject(0, PointF.Empty, new BitmapObjectData
            { ImageAssetId = "live-warp-texture", PlacedSize = new SizeF(6000, 4000), IsBrokenApart = true,
                VisibleContours = [[new(0, 0), new(1, 0), new(1, 1), new(0, 1)],
                    [new(.45f, .45f), new(.55f, .45f), new(.55f, .55f), new(.45f, .55f)]] });
        using var stage = new StageControl(scene) { Size = new Size(640, 480), BitmapImageResolver = _ => source };
        stage.ZoomAt(new Point(320, 240), 1 / stage.Zoom);
        var frame = TransformOverlayFrame.FromBounds(scene.GetRawObjectWorldBounds(0));
        var initial = DistortEnvelope.FromBounds(scene.GetRawObjectWorldBounds(0));
        var anchor = initial.GetAnchors(DistortSide.Top)[0];
        var handle = new DistortHandleRef(DistortSide.Top, anchor.Id, DistortHandleKind.OutgoingControl);
        var draw = RequireMethod(typeof(StageControl), "DrawObjectUnclipped",
            [typeof(Graphics), typeof(int), typeof(SceneRenderPass)]);
        var cache = (System.Collections.IDictionary)RequireField(typeof(StageControl), "_bitmapPreviewRasters").GetValue(stage)!;
        var revision = scene.GeometryRevision;
        var durations = new List<double>();
        var allocations = new List<long>();
        var buffers = new HashSet<BitmapImageRaster>();
        var maxTextureError = 0;
        var mapWithDerivatives = RequireMethod(typeof(DistortEnvelope), "MapCoordinates", [typeof(float), typeof(float)]);
        var mapPosition = RequireMethod(typeof(DistortEnvelope), "MapPosition", [typeof(float), typeof(float)]);
        for (var step = 0; step < 18; step++)
        {
            var envelope = initial.WithHandle(handle, anchor.OutgoingControl,
                new PointF(anchor.OutgoingControl.X + 800, anchor.OutgoingControl.Y - 2000 - step * 16));
            var warp = new DistortWarp(frame, envelope);
            if (step == 0)
            {
                // The existing derivative evaluator is an independent oracle for
                // the forward-only hot path, including extrapolated coordinates.
                for (var y = -2; y <= 22; y++)
                for (var x = -2; x <= 22; x++)
                {
                    var sample = mapWithDerivatives.Invoke(envelope, [x / 20f, y / 20f])!;
                    var expected = (PointF)sample.GetType().GetProperty("Point")!.GetValue(sample)!;
                    var u = x / 20f; var v = y / 20f;
                    var actual = (PointF)mapPosition.Invoke(envelope, [u, v])!;
                    AssertTimeline(actual == expected,
                        $"The forward-only warp evaluator changed the existing Coons mapping: uv={u},{v}, actual={actual}, expected={expected}.");
                }
            }
            stage.SetDistortPreview(scene, new Dictionary<int, DistortWarp[]> { [0] = [warp] });
            using var image = new Bitmap(stage.Width, stage.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(image))
            {
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                draw.Invoke(stage, [graphics, 0, SceneRenderPass.Fill]);
                var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (step >= 2)
                {
                    durations.Add(elapsed);
                    allocations.Add(GC.GetAllocatedBytesForCurrentThread() - allocated);
                }
            }
            // Independent analytic mapping of texture landmarks. Every pointer
            // update must display its own texture, including curved-edge samples.
            foreach (var u in new[] { .15f, .3f, .7f, .85f })
            foreach (var v in new[] { .03f, .2f, .7f, .85f })
            {
                var point = warp.Map(new PointF(-3000 + 6000 * u, -2000 + 4000 * v));
                var screen = stage.WorldToScreen(point.X, point.Y);
                var color = image.GetPixel((int)MathF.Floor(screen.X), (int)MathF.Floor(screen.Y));
                var offset = (int)(v * 96) * source.Stride + (int)(u * 128) * 4;
                var error = Math.Max(Math.Abs(color.A - 200), Math.Max(
                    Math.Abs(color.R * color.A / 255 - source.Pixels[offset + 2]),
                    Math.Max(Math.Abs(color.G * color.A / 255 - source.Pixels[offset + 1]),
                        Math.Abs(color.B * color.A / 255 - source.Pixels[offset]))));
                maxTextureError = Math.Max(maxTextureError, error);
                AssertTimeline(error <= 3, $"Live image texture lagged or lost coverage at update {step}, uv={u},{v}, error={error}.");
            }
            AssertTimeline(cache.Count == 1, "Live image preview retained obsolete pointer meshes.");
            var current = (BitmapDistortRaster)cache[(scene, 0)]!.GetType().GetProperty("Result")!.GetValue(cache[(scene, 0)])!;
            buffers.Add(current.Raster);
            var holeWorld = warp.Map(PointF.Empty);
            var hole = stage.WorldToScreen(holeWorld.X, holeWorld.Y);
            AssertTimeline(image.GetPixel((int)hole.X, (int)hole.Y).A == 0, "A live broken-apart image filled its texture clip hole.");
            var latticeX = current.WorldBounds.Left * stage.WorldLengthToScreen(1);
            var latticeY = current.WorldBounds.Top * stage.WorldLengthToScreen(1);
            AssertTimeline(Math.Abs(latticeX - MathF.Round(latticeX)) < .001f
                && Math.Abs(latticeY - MathF.Round(latticeY)) < .001f,
                "Moving the envelope bounds shifted the texture sampling phase.");
            var builds = stage.DistortMeshBuildCount;
            using var repeated = new Bitmap(stage.Width, stage.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(repeated)) draw.Invoke(stage, [graphics, 0, SceneRenderPass.Fill]);
            AssertTimeline(stage.DistortMeshBuildCount == builds, "An unchanged pointer rebuilt its bitmap preview mesh.");
            for (var y = 0; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++)
                AssertTimeline(image.GetPixel(x, y) == repeated.GetPixel(x, y), "An unchanged pointer produced jittering texture pixels.");
        }
        AssertTimeline(scene.GeometryRevision == revision && !scene.HasObjectDistortions
            && stage.DistortResultBakeAttemptCount == 0 && stage.DistortPreviewRasterBuildCount == 0,
            "Live image preview baked original-density pixels, rerasterized the source, or mutated committed geometry.");
        AssertTimeline(buffers.Count <= 4, $"Small pointer updates allocated {buffers.Count} large preview pixel buffers.");
        AssertTimeline(allocations.Max() < 512 * 1024,
            $"A small curved image preview allocated {allocations.Max()} bytes per pointer frame.");
        stage.ClearDistortPreview();
        AssertTimeline(cache.Count == 0, "Finishing a bitmap preview retained its mesh buffers.");
        durations.Sort();
        Console.WriteLine($"bitmap_live_distort=ok,pointer_updates=18,texture_error={maxTextureError},stable_pixels=ok,clip_hole=ok,preview_buffers={buffers.Count},max_allocated_bytes={allocations.Max()},source_rerasterizations=0," +
            $"avg_ms={durations.Average():0.000},p95_ms={durations[(int)Math.Ceiling(durations.Count * .95) - 1]:0.000},max_ms={durations.Max():0.000}");
    }

    private static void RunBitmapLayerPixelRegression()
    {
        var previous = Environment.GetEnvironmentVariable("VECTOR_DISABLE_BITMAP_LAYER");
        var scene = new VectorScene(); scene.CreateEmpty();
        var rasters = new Dictionary<string, BitmapImageRaster>();
        var sampling = BitmapSampling.Linear;
        var rasterResolutions = 0;
        for (var i = 0; i < 64; i++)
        {
            var id = $"layer-pixels-{i}";
            var raster = new BitmapImageRaster(new BitmapImageRasterKey(id, 32, 24));
            for (var y = 0; y < 24; y++)
            for (var x = 0; x < 32; x++)
            {
                var offset = y * raster.Stride + x * 4;
                raster.Pixels[offset] = (byte)(20 + i);
                raster.Pixels[offset + 1] = (byte)(30 + y * 4);
                raster.Pixels[offset + 2] = (byte)((x / 8 + y / 8) % 2 == 0 ? 140 : 40);
                raster.Pixels[offset + 3] = 160;
            }
            rasters.Add(id, raster);
            var index = scene.AddBitmapObject(0, new PointF((i % 8 - 3.5f) * 1400, (i / 8 - 3.5f) * 1100),
                new BitmapObjectData { ImageAssetId = id, PlacedSize = new SizeF(1000, 750),
                    VisibleContours = i % 2 == 0 ? null : [[new(0, 0), new(1, 0), new(1, 1), new(0, 1)],
                        [new(0.3f, 0.3f), new(0.7f, 0.3f), new(0.7f, 0.7f), new(0.3f, 0.7f)]] }, i % 3 * 0.2f);
            if (i % 2 == 0) scene.Argb[index] = Color.FromArgb(170, Color.White).ToArgb();
        }
        var affine = Matrix3x2.CreateScale(-1, 1) * Matrix3x2.CreateSkew(0.25f, 0.1f);
        scene.ShearObjects(Enumerable.Range(0, 64), point => TransformBitmapProbe(point, affine));
        using var host = new Form { ClientSize = new Size(640, 480), ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual, Location = new Point(-3000, -3000) };
        using var stage = new StageControl(scene) { Dock = DockStyle.Fill,
            BitmapImageResolver = id => { rasterResolutions++; return rasters.GetValueOrDefault(id); },
            BitmapImageSamplingProvider = _ => sampling };
        host.Controls.Add(stage); host.Show(); Application.DoEvents(); PaintStage(stage);
        var renderer = (Direct2DStageRenderer)RequireField(typeof(StageControl), "_direct2DRenderer").GetValue(stage)!;
        var settings = SceneShotSettings.Default with { Kind = SceneShotCameraKind.TwoD,
            Projection = CameraProjection.Orthographic, OrthographicSize = 14000, X = 0, Y = 0, RotationDegrees = 0 };
        try
        {
            var maximumDelta = 0;
            foreach (var filter in new[] { BitmapSampling.Point, BitmapSampling.Linear })
            {
                sampling = filter;
                stage.BitmapImageKeyResolver = null;
                Environment.SetEnvironmentVariable("VECTOR_DISABLE_BITMAP_LAYER", "1");
                AssertTimeline(renderer.TryCapturePlanarShotPreview(stage, settings, new Size(640, 480), null, out var reference)
                    && reference is not null, "Bitmap layer reference did not use a GPU readback.");
                using (reference)
                {
                    Environment.SetEnvironmentVariable("VECTOR_DISABLE_BITMAP_LAYER", null);
                    stage.BitmapImageKeyResolver = id => rasters.TryGetValue(id, out var value) ? value.Key : null;
                    AssertTimeline(renderer.TryCapturePlanarShotPreview(stage, settings, new Size(640, 480), null, out var warm)
                        && warm is not null, "Bitmap GPU cache warmup produced no readback.");
                    warm!.Dispose();
                    var resolutionsBefore = rasterResolutions;
                    AssertTimeline(renderer.TryCapturePlanarShotPreview(stage, settings, new Size(640, 480), null, out var actual)
                        && actual is not null, "Bitmap layer did not produce a GPU readback.");
                    AssertTimeline(rasterResolutions == resolutionsBefore,
                        "Warm GPU bitmaps unnecessarily resolved full CPU pixels.");
                    using (actual)
                    {
                        var delta = 0;
                        for (var y = 0; y < actual!.Height; y++)
                        for (var x = 0; x < actual.Width; x++)
                        {
                            var a = actual.GetPixel(x, y); var b = reference!.GetPixel(x, y);
                            // Compare premultiplied channels; GetPixel expands
                            // one-byte coverage rounding at translucent edges.
                            delta = Math.Max(delta, Math.Max(Math.Abs(a.A - b.A), Math.Max(Math.Abs(a.R * a.A / 255 - b.R * b.A / 255),
                                Math.Max(Math.Abs(a.G * a.A / 255 - b.G * b.A / 255), Math.Abs(a.B * a.A / 255 - b.B * b.A / 255)))));
                        }
                        maximumDelta = Math.Max(maximumDelta, delta);
                        Console.WriteLine($"bitmap_layer_gpu_{filter}_pixel_delta={delta}");
                    }
                }
            }
            AssertTimeline(maximumDelta <= 1, $"Bitmap layer changed texture/edge/alpha pixels (delta={maximumDelta}).");
            stage.BindScene(scene);
            Console.WriteLine("bitmap_layer=ok,gpu_readback=ok,mirror_skew_rotation=ok,alpha_holes=ok");
        }
        finally { Environment.SetEnvironmentVariable("VECTOR_DISABLE_BITMAP_LAYER", previous); }
    }

    private static void RunBitmapOpaqueOcclusionRegression()
    {
        var root = CreateTemporaryDirectory("bitmap-opaque-occlusion");
        var policy = Environment.GetEnvironmentVariable("VECTOR_DISABLE_BITMAP_OCCLUSION");
        try
        {
            var path = Path.Combine(root, "opaque.png");
            using (var image = new Bitmap(512, 384))
            {
                using var graphics = Graphics.FromImage(image);
                graphics.Clear(Color.Coral);
                graphics.FillRectangle(Brushes.Navy, 0, 0, 64, 48);
                image.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            BitmapImageRasterizer.ClearCache();
            var settings = BitmapImageImportSettings.Default;
            BitmapImageRasterizer.Decode(path, settings);
            var scene = new VectorScene(); scene.CreateEmpty();
            scene.AddBitmapObject(0, PointF.Empty, new BitmapObjectData
                { ImageAssetId = "opaque", PlacedSize = new SizeF(1200, 800) });
            scene.AddBitmapObject(0, new PointF(-350, 0), new BitmapObjectData
                { ImageAssetId = "opaque", PlacedSize = new SizeF(1000, 1000) });
            scene.AddBitmapObject(0, new PointF(350, 0), new BitmapObjectData
                { ImageAssetId = "opaque", PlacedSize = new SizeF(1000, 1000) });
            scene.AddBitmapObject(0, new PointF(0, 100), new BitmapObjectData
                { ImageAssetId = "opaque", PlacedSize = new SizeF(1500, 500),
                    VisibleContours = [[new(0, 0), new(1, 0), new(1, 1), new(0, 1)],
                        [new(.4f, .2f), new(.6f, .2f), new(.6f, .8f), new(.4f, .8f)]] });
            scene.Argb[3] = Color.FromArgb(180, Color.White).ToArgb();
            using var host = new Form { ClientSize = new Size(640, 480), ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual, Location = new Point(-3000, -3000) };
            using var stage = new StageControl(scene) { Dock = DockStyle.Fill,
                BitmapImageResolver = _ => BitmapImageRasterizer.Decode(path, settings),
                BitmapImageKeyResolver = _ => BitmapImageRasterizer.TryGetSourceKey(new FileInfo(path), settings, out var key) ? key : null };
            host.Controls.Add(stage); host.Show(); Application.DoEvents(); PaintStage(stage);
            var renderer = (Direct2DStageRenderer)RequireField(typeof(StageControl), "_direct2DRenderer").GetValue(stage)!;
            var shot = SceneShotSettings.Default with { Kind = SceneShotCameraKind.TwoD,
                Projection = CameraProjection.Orthographic, OrthographicSize = 2200, X = 0, Y = 0, RotationDegrees = 0 };
            var maximumDelta = 0;
            foreach (var sample in new[] { BitmapSampling.Point, BitmapSampling.Linear })
            foreach (var offset in new[] { PointF.Empty, new PointF(3.125f, -2.75f) })
            {
                stage.BitmapImageSamplingProvider = _ => sample;
                scene.X[2] = 350 + offset.X; scene.Y[2] = offset.Y;
                scene.RebuildGeometryIndex();
                Environment.SetEnvironmentVariable("VECTOR_DISABLE_BITMAP_OCCLUSION", "1");
                AssertTimeline(renderer.TryCapturePlanarShotPreview(stage, shot, new Size(640, 480), null, out var reference)
                    && reference is not null, "Occlusion reference GPU capture failed.");
                using (reference)
                {
                    Environment.SetEnvironmentVariable("VECTOR_DISABLE_BITMAP_OCCLUSION", null);
                    var reads = BitmapImageRasterizer.SourceReadCount;
                    typeof(BitmapImageRasterizer).GetMethod("EvictFor",
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                        .Invoke(null, [512L * 1024 * 1024]);
                    AssertTimeline(renderer.TryCapturePlanarShotPreview(stage, shot, new Size(640, 480), null, out var actual)
                        && actual is not null, "Occlusion GPU capture failed after CPU eviction.");
                    using (actual)
                    {
                        for (var y = 0; y < actual!.Height; y++)
                        for (var x = 0; x < actual.Width; x++)
                        {
                            var a = actual.GetPixel(x, y); var b = reference!.GetPixel(x, y);
                            maximumDelta = Math.Max(maximumDelta, Math.Max(Math.Abs(a.A - b.A), Math.Max(Math.Abs(a.R - b.R),
                                Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)))));
                        }
                    }
                    AssertTimeline(BitmapImageRasterizer.SourceReadCount == reads,
                        "A live GPU texture reread its evicted CPU source.");
                    var covered = (HashSet<int>)RequireField(typeof(Direct2DStageRenderer), "_coveredBitmaps").GetValue(renderer)!;
                    AssertTimeline(covered.Contains(0), "The occlusion pixel oracle did not exercise full coverage by multiple images.");
                }
            }
            AssertTimeline(maximumDelta == 0, $"Opaque coverage changed visible pixels (delta={maximumDelta}).");
            Console.WriteLine($"bitmap_opaque_occlusion_pixel_delta={maximumDelta} cpu_eviction_source_reads=0");
        }
        finally
        {
            Environment.SetEnvironmentVariable("VECTOR_DISABLE_BITMAP_OCCLUSION", policy);
            BitmapImageRasterizer.ClearCache();
            DeleteTemporaryDirectory(root);
        }
    }

    private static void RunBitmapBackgroundDistortionRegression()
    {
        var raster = new BitmapImageRaster(new BitmapImageRasterKey("background-warp", 768, 512));
        for (var i = 0; i < raster.Pixels.Length; i += 4)
        {
            raster.Pixels[i] = 20; raster.Pixels[i + 1] = 60;
            raster.Pixels[i + 2] = 140; raster.Pixels[i + 3] = 180;
        }
        var scene = new VectorScene();
        scene.CreateEmpty();
        scene.AddBitmapObject(0, PointF.Empty, new BitmapObjectData
            { ImageAssetId = "background-warp", PlacedSize = new SizeF(12000, 8000) });
        using var host = new Form { ClientSize = new Size(400, 300), ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual, Location = new Point(-3000, -3000) };
        using var stage = new StageControl(scene) { Dock = DockStyle.Fill, BitmapImageResolver = _ => raster };
        host.Controls.Add(stage);
        host.Show();
        Application.DoEvents();
        var raw = scene.GetRawObjectWorldBounds(0);
        var warp = new DistortWarp(TransformOverlayFrame.FromBounds(raw), new DistortEnvelope(
            new PointF(raw.Left - 200, raw.Top), new PointF(raw.Right + 500, raw.Top + 250),
            new PointF(raw.Right, raw.Bottom), new PointF(raw.Left, raw.Bottom)));
        scene.SetObjectDistortions(0, [warp]);
        var pending = RequireField(typeof(StageControl), "_pendingDistortBake");
        var cache = (System.Collections.IDictionary)RequireField(typeof(StageControl), "_distortResultCache").GetValue(stage)!;
        PaintStage(stage);
        AssertTimeline(stage.DistortResultBakeAttemptCount == 1 && stage.DistortResultBakeCompletedCount == 0
            && cache.Count == 0 && pending.GetValue(stage) is not null,
            "A large native bitmap distortion still baked synchronously on the UI thread.");
        var changed = warp.WithEnvelope(warp.Envelope.Transform(point => new PointF(point.X + 500, point.Y + 100)));
        stage.SetDistortPreview(scene, new Dictionary<int, DistortWarp[]> { [0] = [changed] });
        var retired = pending.GetValue(stage)!;
        AssertTimeline(((CancellationTokenSource)retired.GetType().GetProperty("Cancellation")!.GetValue(retired)!).IsCancellationRequested,
            "A previous native bake continued competing with a new live pointer preview.");
        stage.ClearDistortPreview();
        scene.SetObjectDistortions(0, [changed]);
        PaintStage(stage);
        WaitForBake();
        PaintStage(stage);
        WaitForBake();
        AssertTimeline(cache.Count == 1 && stage.DistortResultBakeCompletedCount == 1,
            "A stale background result was published after editing the image.");
        var cached = cache[(scene, 0)]!;
        var actual = (BitmapDistortRaster)cached.GetType().GetProperty("Result")!.GetValue(cached)!;
        var expected = BitmapDistortRasterizer.Rasterize(raster, scene, 0, [changed], BitmapSampling.Linear, 64L * 1024 * 1024);
        AssertTimeline(expected is not null && actual.WorldBounds == expected.WorldBounds
            && actual.Raster.Pixels.AsSpan().SequenceEqual(expected.Raster.Pixels),
            "Background distortion changed final pixel precision or published the old geometry.");
        scene.SetObjectDistortions(0, [warp]);
        PaintStage(stage);
        AssertTimeline(pending.GetValue(stage) is not null, "Scene lifecycle fixture did not queue a new bake.");
        var empty = new VectorScene(); empty.CreateEmpty();
        stage.BindScene(empty);
        WaitForBake();
        AssertTimeline(cache.Count == 0, "A retired scene's background result repopulated the cache.");
        stage.BindScene(scene);
        PaintStage(stage);
        AssertTimeline(pending.GetValue(stage) is not null, "Dispose fixture did not queue a native bake.");
        stage.Dispose();
        AssertTimeline(pending.GetValue(stage) is null
            && RequireField(typeof(StageControl), "_distortBakeTimer").GetValue(stage) is null,
            "Disposing Stage retained pending bitmap work or its UI timer.");
        Console.WriteLine("bitmap_distort_background=ok,ui_native_bake=deferred,stale_result=discarded,final_pixels=exact,scene_cancellation=ok,dispose=ok");

        void WaitForBake()
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            while (pending.GetValue(stage) is not null && started.Elapsed < TimeSpan.FromSeconds(15))
            {
                Application.DoEvents();
                Thread.Sleep(1);
            }
            AssertTimeline(pending.GetValue(stage) is null, "Background bitmap distortion did not settle within its deadline.");
        }
    }

    private static void RunBitmapBoundaryWorkingSetRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        for (var i = 0; i < 1000; i++)
            scene.AddBitmapObject(0, new PointF(i * 400, 0), new BitmapObjectData
                { ImageAssetId = "boundary-working-set", PlacedSize = new SizeF(300, 200) });
        scene.ShearObjects(Enumerable.Range(0, 1000), point => new PointF(point.X + point.Y * 0.2f, point.Y));
        using var stage = new StageControl(scene);
        var boundary = RequireMethod(typeof(StageControl), "GetObjectBoundaryContoursForRendering");
        for (var i = 0; i < 1000; i++) boundary.Invoke(stage, [scene, i]);
        var count = stage.DistortBoundaryBuildCount;
        for (var repetition = 0; repetition < 3; repetition++)
            for (var i = 0; i < 1000; i++) boundary.Invoke(stage, [scene, i]);
        AssertTimeline(count == 1000 && stage.DistortBoundaryBuildCount == count,
            "A thousand-image boundary working set was cleared and rebuilt every frame.");
        scene.ShearObjects([0], point => new PointF(point.X + 50, point.Y));
        for (var i = 0; i < 1000; i++) boundary.Invoke(stage, [scene, i]);
        AssertTimeline(stage.DistortBoundaryBuildCount == count + 1,
            "Editing one image invalidated unrelated image boundaries or retained stale geometry.");
        stage.BindScene(scene);
        AssertTimeline((long)RequireField(typeof(StageControl), "_distortBoundaryCacheBytes").GetValue(stage)! == 0,
            "Binding a scene retained boundary cache memory.");
        Console.WriteLine("bitmap_distort_boundary_working_set=1000,repeated_builds=0,single_edit_invalidation=ok,lifecycle=ok");
    }

    private static void RunBitmapBreakApartRegression()
    {
        RunBitmapTopologyCutRegression();
        var scene = new VectorScene();
        scene.CreateEmpty();
        var index = scene.AddBitmapObject(0, PointF.Empty, new BitmapObjectData
        {
            ImageAssetId = "bitmap-fill-regression", PlacedSize = new SizeF(400, 300),
            VisibleContours = [[new(0, 0), new(1, 0), new(1, 1), new(0, 1)],
                [new(0.4f, 0.4f), new(0.6f, 0.4f), new(0.6f, 0.6f), new(0.4f, 0.6f)]]
        });
        var before = scene.CreateSnapshot();
        AssertTimeline(scene.BreakApartBitmapObjects([index]).SequenceEqual([index])
            && scene.ObjectCount == 1 && scene.BreakApartBitmapObjects([index]).Length == 0,
            "Bitmap Break Apart did not create one editable texture fill or allowed repeated conversion.");
        var split = scene.MaterializeMarqueeSelectionParts(new RectangleF(-100, -150, 200, 300), 0);
        AssertTimeline(split.Success && split.Changed && split.SelectedObjects.Length == 1 && scene.ObjectCount == 2,
            "A marquee did not detach the selected bitmap fill.");
        var raster = new BitmapImageRaster(new BitmapImageRasterKey("bitmap-fill-pixels", 40, 30));
        for (var offset = 0; offset < raster.Pixels.Length; offset += 4)
        {
            raster.Pixels[offset] = 30; raster.Pixels[offset + 1] = 50;
            raster.Pixels[offset + 2] = 90; raster.Pixels[offset + 3] = 128;
        }
        var original = BitmapImageRasterizer.ApplyObjectClip(raster, before.BitmapObjects[index]);
        var pieces = Enumerable.Range(0, scene.ObjectCount).Select(i =>
        {
            AssertTimeline(scene.TryGetBitmapObjectData(i, out var data) && data.IsBrokenApart
                && data.ImageAssetId == "bitmap-fill-regression" && data.VisibleContours is { Length: > 0 },
                "A detached image fill lost its texture or clip metadata.");
            AssertBitmapTransformPoint(BitmapTransformProbe(scene, i, new PointF(0.25f, 0.25f)),
                new PointF(-100, -75), "Detaching a fill stretched its image mapping.");
            return BitmapImageRasterizer.ApplyObjectClip(raster, data);
        }).ToArray();
        for (var offset = 0; offset < original.Pixels.Length; offset++)
            AssertTimeline(pieces.Sum(piece => (int)piece.Pixels[offset]) == original.Pixels[offset],
                "Detached image fills lost pixels, filled a hole, or overlapped translucent pixels.");
        var savedSplit = scene.CreateSnapshot();
        var selected = split.SelectedObjects[0];
        var unselected = 1 - selected;
        var probe = new PointF(0.25f, 0.25f);
        var selectedPoint = BitmapTransformProbe(scene, selected, probe);
        var unselectedPoint = BitmapTransformProbe(scene, unselected, probe);
        scene.ApplyTranslationSessionForPreview(scene.BeginTransformSession([selected]), 100, 50);
        AssertBitmapTransformPoint(BitmapTransformProbe(scene, selected, probe),
            new PointF(selectedPoint.X + 100, selectedPoint.Y + 50), "Detached fill did not move with its texture.");
        AssertBitmapTransformPoint(BitmapTransformProbe(scene, unselected, probe), unselectedPoint,
            "Moving a detached fill also moved the remaining image.");
        var path = Path.Combine(Path.GetTempPath(), $"v2d-bitmap-fill-{Guid.NewGuid():N}.svg");
        try
        {
            DrawingObjectSvgCodec.Write(path, "bitmap-fill", savedSplit);
            scene.RestoreSnapshot(DrawingObjectSvgCodec.Read(path, "bitmap-fill"));
            AssertTimeline(scene.ObjectCount == 2 && Enumerable.Range(0, 2).All(i =>
                scene.TryGetBitmapObjectData(i, out var data) && data.IsBrokenApart && data.VisibleContours is { Length: > 0 }),
                "Saving and reopening detached texture fills lost their editability.");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
        scene.RestoreSnapshot(before);
        AssertTimeline(scene.ObjectCount == 1 && scene.TryGetBitmapObjectData(0, out var restored) && !restored.IsBrokenApart,
            "Undo did not restore the original placed bitmap.");
        scene.BreakApartBitmapObjects([0]);
        var affine = Matrix3x2.CreateScale(-1, 1) * Matrix3x2.CreateSkew(0.3f, 0);
        scene.ShearObjects([0], point => TransformBitmapProbe(point, affine));
        PointF[] polygon = [new(-80, -100), new(80, -100), new(0, 110)];
        var lasso = scene.MaterializeLassoSelectionParts(polygon.Select(point => TransformBitmapProbe(point, affine)).ToArray(), 0);
        AssertTimeline(lasso.Success && lasso.Changed && lasso.SelectedObjects.Length == 1 && scene.ObjectCount == 2,
            "Lasso did not detach a mirrored and skewed texture fill.");
        foreach (var i in Enumerable.Range(0, 2))
            AssertBitmapTransformPoint(BitmapTransformProbe(scene, i, probe), TransformBitmapProbe(new PointF(-100, -75), affine),
                "Lasso changed the original affine texture mapping.");
        Console.WriteLine("bitmap_break_apart=ok,marquee=ok,lasso_affine=ok,texture_alpha_holes=ok,move=ok,snapshot=ok,storage=ok");
    }

    private static void RunBitmapTopologyCutRegression()
    {
        foreach (var kind in new[] { "line", "curve", "fill", "bitmap" })
        foreach (var affine in new[] { Matrix3x2.Identity,
            Matrix3x2.CreateScale(-1, 1) * Matrix3x2.CreateSkew(0.3f, 0) })
        {
            var scene = new VectorScene();
            scene.CreateEmpty(2);
            var cutter = kind switch
            {
                "line" => scene.AddLineSegment(0, new(0, -250), new(0, 250), 4, Color.Transparent, Color.Black, 8),
                "curve" => scene.AddCubicCurveSegment(0, new(0, -250), new(100, -80), new(-100, 80), new(0, 250),
                    4, Color.Transparent, Color.Black, 8),
                "bitmap" => scene.AddBitmapObject(0, PointF.Empty, new BitmapObjectData
                    { ImageAssetId = "bitmap-cutter", PlacedSize = new SizeF(40, 500), IsBrokenApart = true }),
                _ => scene.AddObject(0, PointF.Empty, new SizeF(40, 500), 0, 0, Color.Blue, Color.Transparent, 8, ShapeKind.Rectangle)
            };
            var source = scene.AddBitmapObject(0, PointF.Empty, new BitmapObjectData
            {
                ImageAssetId = "topology-texture", PlacedSize = new SizeF(400, 300),
                VisibleContours = [[new(0, 0), new(1, 0), new(1, 1), new(0, 1)],
                    [new(0.45f, 0.45f), new(0.55f, 0.45f), new(0.55f, 0.55f), new(0.45f, 0.55f)]]
            });
            scene.ShearObjects([cutter, source], point => TransformBitmapProbe(point, affine));
            AssertTimeline(scene.GetFillParts(source, 0).Length == 1,
                "An intact image participated in topology cutting.");
            scene.BreakApartBitmapObjects([source]);
            var expected = kind is "line" or "curve" ? 2 : 3;
            AssertTimeline(scene.GetFillParts(source, 0).Length == expected,
                $"A {kind} failed to partition a broken-apart image (affine={affine}).");
            var left = scene.HitTestElement(TransformBitmapProbe(new PointF(-120, 80), affine), 0, 0);
            var right = scene.HitTestElement(TransformBitmapProbe(new PointF(120, 80), affine), 0, 0);
            AssertTimeline(left.IsValid && right.IsValid && left.Key.ObjectIndex == source
                && right.Key.ObjectIndex == source && left.Key.PartIndex != right.Key.PartIndex,
                "Clicking opposite sides of a cutter selected the same bitmap region.");
            using (var stage = new StageControl(scene))
            {
                stage.ConfigureSelectionArrows(new SelectionArrowSettings(), objectSelection: true);
                stage.SetSelectionState([source], source, [left], left);
                var mask = stage.GetObjectArrowStippleMask();
                using var path = new System.Drawing.Drawing2D.GraphicsPath(System.Drawing.Drawing2D.FillMode.Alternate);
                foreach (var contour in mask.Contours) if (contour.Length >= 3) path.AddPolygon(contour);
                AssertTimeline(path.IsVisible(TransformBitmapProbe(new PointF(-120, 80), affine))
                    && !path.IsVisible(TransformBitmapProbe(new PointF(120, 80), affine)),
                    "Bitmap stipple covered both sides of a topology cut.");
            }
            var before = scene.CreateSnapshot();
            var result = scene.MaterializeSelectedParts([left.Key], 0);
            AssertTimeline(result.Success && result.Changed && result.Parts.Length == 1
                && scene.ObjectCount == expected + 1 && result.OldToNewObjectIndex[cutter] >= 0,
                "Detaching a bitmap topology region failed or lost the cutter remap.");
            var selected = result.Parts[0].Result.ObjectIndex;
            var pieces = Enumerable.Range(0, scene.ObjectCount)
                .Where(i => scene.TryGetBitmapObjectData(i, out var data) && data.ImageAssetId == "topology-texture").ToArray();
            AssertTimeline(pieces.Length == expected && pieces.All(i => scene.ObjectKeyframeFrame[i] == 0
                && scene.ObjectOrder[i] == before.ObjectOrder[source]),
                "A bitmap topology fragment lost its frame or drawing order.");
            foreach (var piece in pieces)
                AssertBitmapTransformPoint(BitmapTransformProbe(scene, piece, new PointF(0.2f, 0.7f)),
                    TransformBitmapProbe(new PointF(-120, 60), affine), "A topology cut stretched or mirrored the texture twice.");
            if (kind is "fill" or "bitmap")
            {
                var raster = new BitmapImageRaster(new BitmapImageRasterKey("topology-pixels", 40, 30));
                for (var offset = 0; offset < raster.Pixels.Length; offset += 4)
                {
                    raster.Pixels[offset] = 30; raster.Pixels[offset + 1] = 50;
                    raster.Pixels[offset + 2] = 90; raster.Pixels[offset + 3] = 128;
                }
                var original = BitmapImageRasterizer.ApplyObjectClip(raster, before.BitmapObjects[source]);
                var clipped = pieces.Select(i => BitmapImageRasterizer.ApplyObjectClip(raster,
                    scene.CreateSnapshot().BitmapObjects[i])).ToArray();
                for (var offset = 0; offset < original.Pixels.Length; offset++)
                    AssertTimeline(clipped.Sum(piece => (int)piece.Pixels[offset]) == original.Pixels[offset],
                        "A fill boundary cut lost texture pixels, filled a hole or duplicated translucent pixels.");
            }
            var fixedPiece = pieces.First(i => i != selected);
            var fixedProbe = BitmapTransformProbe(scene, fixedPiece, new PointF(0.2f, 0.7f));
            scene.ApplyTranslationSessionForPreview(scene.BeginTransformSession([selected]), 600, 50);
            AssertBitmapTransformPoint(BitmapTransformProbe(scene, selected, new PointF(0.2f, 0.7f)),
                TransformBitmapProbe(new PointF(-120, 60), affine) + new SizeF(600, 50),
                "Moving a detached bitmap region did not carry its texture.");
            AssertBitmapTransformPoint(BitmapTransformProbe(scene, fixedPiece, new PointF(0.2f, 0.7f)), fixedProbe,
                "Moving a detached bitmap region moved another fragment.");
            scene.RestoreSnapshot(before);
            AssertTimeline(scene.ObjectCount == 2 && scene.GetFillParts(source, 0).Length == expected,
                "Undo failed to restore the bitmap topology partition.");
        }
        var isolated = new VectorScene();
        isolated.CreateEmpty(2);
        isolated.AddLineSegment(1, new(0, -250), new(0, 250), 4, Color.Transparent, Color.Black, 8);
        var locked = isolated.AddLineSegment(0, new(50, -250), new(50, 250), 4, Color.Transparent, Color.Black, 8);
        isolated.ObjectLocked[locked] = true;
        var image = isolated.AddBitmapObject(0, PointF.Empty, new BitmapObjectData
            { ImageAssetId = "isolated-texture", PlacedSize = new SizeF(400, 300), IsBrokenApart = true });
        AssertTimeline(isolated.GetFillParts(image, 0).Length == 1,
            "A locked cutter or a different layer partitioned the bitmap fill.");
        var invalid = isolated.MaterializeSelectedParts([new DrawingElementKey(image, DrawingElementKind.Fill, 999)], 0);
        AssertTimeline(!invalid.Success && isolated.ObjectCount == 3,
            "A stale bitmap part key changed the scene.");
        Console.WriteLine("bitmap_topology_cut=line,curve,fill,bitmap,affine,stipple,detach,move,undo,layer_lock,stale_key");
    }

    private static void RunBitmapAffineTransformRegression()
    {
        RunBitmapAffineMappingRegression();
        RunBitmapFreeTransformPointerRegression();
        var scene = new VectorScene();
        scene.CreateEmpty();
        var index = scene.AddBitmapObject(0, new PointF(400, 300), new BitmapObjectData
        {
            ImageAssetId = "affine-bitmap",
            PlacedSize = new SizeF(1200, 800),
            VisibleContours = [[new(0, 0), new(1, 0), new(1, 1), new(0, 1)]]
        }, 0.35f);
        var bounds = scene.GetRawObjectWorldBounds(index);
        scene.SetObjectDistortions(index, [new DistortWarp(
            TransformOverlayFrame.FromBounds(bounds),
            new DistortEnvelope(
                new PointF(bounds.Left - 100, bounds.Top),
                new PointF(bounds.Right + 200, bounds.Top + 80),
                new PointF(bounds.Right, bounds.Bottom),
                new PointF(bounds.Left, bounds.Bottom)))]);
        var transforms = new[]
        {
            Matrix3x2.CreateSkew(0.4f, 0),
            Matrix3x2.CreateSkew(0, -0.3f),
            Matrix3x2.CreateScale(1.7f, 0.6f),
            Matrix3x2.CreateScale(-1, 1),
            Matrix3x2.CreateScale(1, -1),
            Matrix3x2.CreateRotation(0.6f) * Matrix3x2.CreateTranslation(100, -40)
        };
        var probes = new[] { new PointF(0.2f, 0.3f), new PointF(0.7f, 0.8f) };
        var initial = probes.Select(probe => BitmapTransformProbe(scene, index, probe)).ToArray();
        var session = scene.BeginTransformSession([index]);
        foreach (var transform in transforms)
        {
            scene.ApplyTransformSession(session, point => TransformBitmapProbe(point, transform),
                convertPrimitivesToPaths: true);
            for (var probeIndex = 0; probeIndex < probes.Length; probeIndex++)
            {
                AssertBitmapTransformPoint(BitmapTransformProbe(scene, index, probes[probeIndex]),
                    TransformBitmapProbe(initial[probeIndex], transform), "Bitmap transform session lost shear or reflection.");
            }
            AssertTimeline(scene.TryGetObjectDistortions(index, out var warps) && warps.Length == 1
                && scene.TryGetBitmapObjectData(index, out var data) && data.ImageAssetId == "affine-bitmap"
                && data.VisibleContours is { Length: 1 },
                "Bitmap transform changed its asset/clip or accumulated preview warps.");
        }

        foreach (var transform in transforms)
        {
            var previous = probes.Select(probe => BitmapTransformProbe(scene, index, probe)).ToArray();
            scene.ShearObjects([index], point => TransformBitmapProbe(point, transform));
            for (var probeIndex = 0; probeIndex < probes.Length; probeIndex++)
            {
                var actual = BitmapTransformProbe(scene, index, probes[probeIndex]);
                AssertBitmapTransformPoint(actual, TransformBitmapProbe(previous[probeIndex], transform),
                    "Sequential bitmap transforms lost an existing distortion.");
                AssertTimeline(scene.HitTest(actual, 0, 1) == index && scene.HasSelectableObjectAt(actual, 0, 1),
                    "Transformed bitmap hit testing disagreed with its image mapping.");
            }
        }
        var restored = new VectorScene();
        restored.RestoreSnapshot(scene.CreateSnapshot());
        AssertBitmapTransformPoint(BitmapTransformProbe(restored, index, probes[0]),
            BitmapTransformProbe(scene, index, probes[0]), "Bitmap transform snapshot lost the image mapping.");

        var project = VectorProject.CreateEmpty();
        var symbol = project.DrawingObjects[0];
        symbol.SetAnchor(PointF.Empty);
        symbol.Scene.RestoreSnapshot(scene.CreateSnapshot());
        AssertTimeline(project.TryAddSceneInstance(project.Scenes[0].Id, symbol.Id,
            new PointF(200, 100), 0, out var instance) && instance is not null,
            "Could not create a bitmap composition fixture.");
        foreach (var mirror in new[] { false, true })
        {
            instance!.SkewX = 25;
            instance.ScaleX = mirror ? -1.4f : 1.4f;
            instance.ScaleY = 0.7f;
            instance.RotationZ = 20;
            var destination = new VectorScene();
            SceneCompositionBuilder.Build(destination, project.Scenes[0], project.DrawingObjects, 0);
            var transform = DrawingObjectInstanceDefinition.CreatePlanarTransform(instance.EvaluateState(0));
            AssertTimeline(destination.ObjectCount == 1 && destination.ShapeKind[0] == ShapeKind.Bitmap,
                "Sheared bitmap composition lost or materialized the image object.");
            AssertBitmapTransformPoint(BitmapTransformProbe(destination, 0, probes[0]),
                TransformBitmapProbe(BitmapTransformProbe(symbol.Scene, index, probes[0]), transform),
                "Bitmap composition did not preserve the source warp before its parent transform.");
        }
        Console.WriteLine("bitmap_affine_transform=session,sequential,shear,mirror,clip,snapshot,composition");
    }

    private static void RunBitmapAffineMappingRegression()
    {
        var source = TransformOverlayFrame.FromBounds(new RectangleF(-200, -100, 400, 200));
        var envelope = DistortEnvelope.FromBounds(source.Bounds);
        foreach (var matrix in new[] { Matrix3x2.CreateSkew(0.4f, -0.2f),
                     Matrix3x2.CreateScale(-2, 0.6f) * Matrix3x2.CreateRotation(0.7f)
                     * Matrix3x2.CreateTranslation(1000, -400) })
        {
            var warp = new DistortWarp(source, envelope.AffineTransform(matrix));
            AssertTimeline(warp.TryGetAffineTransform(out var actual), "Affine bitmap warp was classified as software distortion.");
            foreach (var point in new[] { source.TopLeft, source.BottomRight, source.Center, new PointF(31, -17) })
                AssertBitmapTransformPoint(TransformBitmapProbe(point, actual), warp.Map(point),
                    "Bitmap affine matrix disagrees with the persisted Coons mapping.");
        }
        var perspective = new DistortWarp(source, new DistortEnvelope(source.TopLeft,
            source.TopRight, new PointF(240, 100), source.BottomLeft));
        AssertTimeline(!perspective.TryGetAffineTransform(out _), "Perspective distortion entered the affine path.");
        // A displaced cubic control must never be mistaken for a straight mapping.
        var top = envelope.Top;
        top[0] = top[0] with { OutgoingControl = new PointF(top[0].OutgoingControl.X, -140) };
        var curve = new DistortWarp(source, new DistortEnvelope(top, envelope.Right, envelope.Bottom, envelope.Left));
        AssertTimeline(!curve.TryGetAffineTransform(out _), "Curved bitmap entered the affine path.");
    }

    private static void RunBitmapMoveFeedbackRegression()
    {
        const double budget = 1000d / 150;
        var scene = new VectorScene();
        scene.CreateEmpty();
        var rasters = new Dictionary<string, BitmapImageRaster>();
        var fixture = Environment.GetEnvironmentVariable("VECTOR_BENCH_BITMAP_DRAG_PROJECT");
        using var resolverHost = string.IsNullOrWhiteSpace(fixture) ? null : new MainForm();
        Func<string, BitmapImageRaster?> resolve = id => rasters.GetValueOrDefault(id);
        Func<string, StageControl, BitmapImageRaster?>? stageResolver = null;
        Func<string, BitmapSampling> sampling = _ => BitmapSampling.Linear;
        if (!string.IsNullOrWhiteSpace(fixture))
        {
            var (project, manifest) = ProjectPackageStore.OpenPackage(fixture);
            RequireField(typeof(MainForm), "_project").SetValue(resolverHost, project);
            RequireField(typeof(MainForm), "_projectManifestPath").SetValue(resolverHost, manifest);
            stageResolver = RequireMethod(typeof(MainForm), "ResolveBitmapImageRasterForStage")
                .CreateDelegate<Func<string, StageControl, BitmapImageRaster?>>(resolverHost);
            sampling = RequireMethod(typeof(MainForm), "ResolveBitmapImageSampling")
                .CreateDelegate<Func<string, BitmapSampling>>(resolverHost);
            scene = project.DrawingObjects.Single().Scene;
            foreach (var asset in project.ImageAssets)
                rasters[asset.Id] = BitmapImageRasterizer.Decode(asset.SourcePath, asset.ImportSettings);
        }
        else
        {
            for (var i = 0; i < 43; i++)
            {
                var id = $"move-feedback-{i}";
                var raster = new BitmapImageRaster(new BitmapImageRasterKey(id, 512, 256));
                for (var p = 0; p < raster.Pixels.Length; p += 4)
                {
                    raster.Pixels[p] = 40; raster.Pixels[p + 1] = (byte)(80 + i);
                    raster.Pixels[p + 2] = 180; raster.Pixels[p + 3] = 220;
                }
                rasters.Add(id, raster);
                scene.AddBitmapObject(0, new PointF((i % 7 - 3) * 2400, (i / 7 - 3) * 1400),
                    new BitmapObjectData { ImageAssetId = id, PlacedSize = new SizeF(3000, 1800) });
            }
        }
        scene.CompleteDeferredBuild();
        var indices = Enumerable.Range(0, scene.ObjectCount).ToArray();
        AssertTimeline(indices.Length > 0 && indices.All(i => scene.ShapeKind[i] == ShapeKind.Bitmap),
            "Bitmap movement fixture must contain visible bitmap objects.");
        using var host = new Form { ClientSize = new Size(1920, 1080), ShowInTaskbar = false };
        var lookups = 0;
        var resolveMilliseconds = 0d;
        using var stage = new StageControl(scene) { Dock = DockStyle.Fill,
            BitmapImageSamplingProvider = sampling };
        if (resolverHost is not null)
            RequireMethod(typeof(MainForm), "BindStageBitmapImageResolvers").Invoke(resolverHost, [stage]);
        stage.BitmapImageResolver = id =>
        {
            lookups++;
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var raster = stageResolver is null ? resolve(id) : stageResolver(id, stage);
            resolveMilliseconds += System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return raster;
        };
        host.Controls.Add(stage);
        host.Show();
        var artworkBounds = indices.Select(scene.GetObjectWorldBounds).Aggregate(RectangleF.Union);
        var visibleWidth = Math.Max(24000, Math.Max(artworkBounds.Width + 4000,
            (artworkBounds.Height + 2000) * stage.Width / stage.Height));
        stage.SetVisibleWorldWidth(visibleWidth);
        stage.RestoreViewState(stage.CaptureViewState() with
        {
            CameraX = artworkBounds.Left + artworkBounds.Width * 0.5f,
            CameraY = artworkBounds.Top + artworkBounds.Height * 0.5f,
            Zoom = stage.Width * VectorUnits.UnitsPerPixel / visibleWidth
        });
        Console.WriteLine($"bitmap_selection_move_bounds={artworkBounds} visible_width={visibleWidth} hidden={indices.Count(i => scene.ObjectHidden[i])} active={indices.Count(i => scene.IsObjectActive(i, 0))}");
        stage.SetSelectionState(indices, indices[^1], [], default);
        stage.ConfigureSelectionArrows(new SelectionArrowSettings(), objectSelection: true);
        Application.DoEvents();
        var samples = new List<double>();
        stage.Capture = true;
        var originStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        var originAllocated = GC.GetAllocatedBytesForCurrentThread();
        stage.BeginSelectionMoveFeedback(indices);
        var originMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(originStarted).TotalMilliseconds;
        var originBytes = GC.GetAllocatedBytesForCurrentThread() - originAllocated;
        var ordinaryBitmaps = indices.Where(index => !scene.TryGetObjectDistortionsView(index, out _)
            && scene.LayerOpacity[scene.ObjectLayer[index]] == 1f
            && scene.IsLayerEffectivelyVisible(scene.ObjectLayer[index])
            && !scene.IsLayerEffectivelyOutlined(scene.ObjectLayer[index])
            && !scene.GetLayerSymbolFilters(scene.ObjectLayer[index]).HasEnabled).ToArray();
        if (ordinaryBitmaps.Length > 1)
            AssertTimeline(ordinaryBitmaps.Select(index => stage.SelectionMoveOrigins[index].Scene).Distinct().Count() == 1,
                "Ordinary bitmap origins still allocate a complete scene per selected image.");
        var origins = indices.Select(index => new PointF(scene.X[index], scene.Y[index])).ToArray();
        Console.WriteLine(FormattableString.Invariant(
            $"bitmap_selection_move_origin_setup_ms={originMilliseconds:0.###} allocated_bytes={originBytes}"));
        var session = scene.BeginTransformSession(indices);
        try
        {
            for (var frame = -8; frame < 100; frame++)
            {
                var phase = frame + 9;
                lookups = 0;
                resolveMilliseconds = 0;
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                scene.ApplyTranslationSessionForPreview(session, phase * 16, phase * 8);
                PaintStage(stage);
                var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (frame == -1) Console.WriteLine(FormattableString.Invariant(
                    $"bitmap_selection_move_warm total_ms={elapsed:0.###} resolve_ms={resolveMilliseconds:0.###} render_ms={stage.LastFrameRenderMilliseconds:0.###} commands_ms={stage.LastDirect2DCommandMilliseconds:0.###} present_ms={stage.LastDirect2DPresentMilliseconds:0.###} cache_ms={stage.LastDirect2DCacheMaintenanceMilliseconds:0.###}"));
                AssertTimeline(stage.LastFrameUsedDirect2D && stage.LastStats.DrawnObjects >= indices.Length
                    && stage.SelectionMoveFeedbackActive && stage.SelectionMoveOrigins.Count == indices.Length,
                    $"Bitmap movement omitted artwork, origin feedback or hardware rendering: frame={frame}, hardware={stage.LastFrameUsedDirect2D}, drawn={stage.LastStats.DrawnObjects}/{indices.Length}, feedback={stage.SelectionMoveFeedbackActive}, origins={stage.SelectionMoveOrigins.Count}.");
                AssertTimeline(lookups <= rasters.Count,
                    "Origin feedback and moving artwork resolved the same asset repeatedly within a paint.");
                foreach (var index in indices)
                {
                    var origin = stage.SelectionMoveOrigins[index];
                    AssertTimeline(origin.Scene.X[origin.ObjectIndex] == origins[index].X
                        && origin.Scene.Y[origin.ObjectIndex] == origins[index].Y
                        && origin.Scene.TryGetBitmapObjectData(origin.ObjectIndex, out var originalData)
                        && scene.TryGetBitmapObjectData(index, out var movedData)
                        && originalData.ImageAssetId == movedData.ImageAssetId,
                        "A shared bitmap origin changed position, texture, or source-object mapping during dragging.");
                }
                if (frame >= 0) samples.Add(elapsed);
            }
            samples.Sort();
            Console.WriteLine(FormattableString.Invariant(
                $"bitmap_selection_move objects={indices.Length} viewport=1920x1080 origins=enabled warmup=8 samples=100 fixture={(string.IsNullOrWhiteSpace(fixture) ? "synthetic" : "package")} average_ms={samples.Average():0.###} p95_ms={samples[94]:0.###} max_ms={samples[^1]:0.###} fps={1000 / samples.Average():0.###}"));
            Console.WriteLine($"bitmap_selection_move_budget_met={(samples.Average() < budget && samples[94] < budget && samples[^1] < budget).ToString().ToLowerInvariant()}");
        }
        finally
        {
            stage.ClearSelectionFillDragFront();
            stage.Capture = false;
        }
        PaintStage(stage);
        AssertTimeline(!stage.SelectionMoveFeedbackActive, "Bitmap movement retained origin feedback after release.");
    }

    private static void RunBitmapPointerMoveRegression()
    {
        var fixture = Environment.GetEnvironmentVariable("VECTOR_BENCH_BITMAP_DRAG_PROJECT");
        if (string.IsNullOrWhiteSpace(fixture)) return;
        var (project, manifest) = ProjectPackageStore.OpenPackage(fixture);
        using var form = new MainForm { ClientSize = new Size(2560, 1400), ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual, Location = Point.Empty };
        form.Show();
        form.ClientSize = new Size(2560, 1400);
        RequireMethod(typeof(MainForm), "LoadProjectDocument").Invoke(form, [project, manifest]);
        var tabs = RequireField(typeof(MainForm), "_workspaceTabs").GetValue(form)!;
        tabs.GetType().GetProperty("SelectedView")!.SetValue(tabs, WorkspaceView.BasicDrawing);
        var scene = (VectorScene)RequireField(typeof(MainForm), "_scene").GetValue(form)!;
        var stage = (StageControl)RequireField(typeof(MainForm), "_stage").GetValue(form)!;
        form.ClientSize = new Size(form.ClientSize.Width + 1920 - stage.Width,
            form.ClientSize.Height + 1080 - stage.Height);
        form.PerformLayout();
        var indices = Enumerable.Range(0, scene.ObjectCount).ToArray();
        RequireMethod(typeof(MainForm), "ActivateTool", [typeof(ToolMode)]).Invoke(form, [ToolMode.ObjectSelect]);
        RequireMethod(typeof(MainForm), "SetSelection", [typeof(IEnumerable<int>), typeof(bool), typeof(bool), typeof(bool)])
            .Invoke(form, [indices, false, false, true]);
        var bounds = indices.Select(scene.GetObjectWorldBounds).Aggregate(RectangleF.Union);
        var viewWidth = Math.Max(bounds.Width, bounds.Height * stage.Width / stage.Height) * 1.25f;
        stage.RestoreViewState(stage.CaptureViewState() with
        {
            CameraX = bounds.Left + bounds.Width * 0.5f,
            CameraY = bounds.Top + bounds.Height * 0.5f,
            Zoom = stage.Width * VectorUnits.UnitsPerPixel / viewWidth
        });
        Application.DoEvents();
        PaintStage(stage);
        var before = indices.Select(i => new PointF(scene.X[i], scene.Y[i])).ToArray();
        var primary = indices[^1];
        var start = Point.Round(stage.WorldToScreen(scene.X[primary], scene.Y[primary]));
        var down = RequireMethod(typeof(MainForm), "StageMouseDown");
        var move = RequireMethod(typeof(MainForm), "StageMouseMove");
        var flush = RequireMethod(typeof(MainForm), "FlushLineDragPreview");
        var up = RequireMethod(typeof(MainForm), "StageMouseUp");
        down.Invoke(form, [stage, new MouseEventArgs(MouseButtons.Left, 1, start.X, start.Y, 0)]);
        var samples = new List<double>();
        var end = start;
        var gcCounts = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
        var allocatedStart = GC.GetAllocatedBytesForCurrentThread();
        var loopAllocated = 0L;
        var loopGc = "";
        var warmSourceReads = 0L;
        try
        {
            for (var sample = -8; sample < 100; sample++)
            {
                end = new Point(start.X + 6 + (sample + 8) % 20, start.Y + 6);
                var rendered = stage.RenderedFrameCount;
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                move.Invoke(form, [stage, new MouseEventArgs(MouseButtons.Left, 0, end.X, end.Y, 0)]);
                flush.Invoke(form, null);
                var inputMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (stage.RenderedFrameCount == rendered) PaintStage(stage);
                var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                AssertTimeline(stage.LastFrameUsedDirect2D && stage.LastStats.DrawnObjects >= indices.Length
                    && stage.SelectionMoveFeedbackActive && stage.SelectedObjects.Count == indices.Length,
                    "The editor pointer path lost the bitmap selection, origin feedback or hardware presentation.");
                if (sample == -8)
                {
                    Console.WriteLine(FormattableString.Invariant($"bitmap_pointer_first_move_ms={elapsed:0.###}"));
                    var firstOrigin = stage.SelectionMoveOrigins.Values.First();
                    Console.WriteLine($"bitmap_pointer_frame={stage.Frame} shared_origins={stage.SelectionMoveOrigins.Values.Count(origin => origin.SharedBitmapScene)} origin_layer_effects={firstOrigin.Scene.HasDisplayLayerEffects} origin_active_at_stage_frame={firstOrigin.Scene.IsObjectActive(firstOrigin.ObjectIndex, stage.Frame)}");
                }
                if (sample == -1) Console.WriteLine(FormattableString.Invariant($"bitmap_pointer_warm input_ms={inputMilliseconds:0.###} render_ms={stage.LastFrameRenderMilliseconds:0.###} present_ms={stage.LastDirect2DPresentMilliseconds:0.###}"));
                if (sample == -1) warmSourceReads = BitmapImageRasterizer.SourceReadCount;
                if (sample == -1)
                {
                    var gpu = RequireField(typeof(StageControl), "_direct2DRenderer").GetValue(stage)!;
                    var entries = (System.Collections.IDictionary)RequireField(typeof(Direct2DStageRenderer), "_bitmapObjectCache").GetValue(gpu)!;
                    Console.WriteLine($"bitmap_pointer_gpu_cache_entries={entries.Count} bytes={RequireField(typeof(Direct2DStageRenderer), "_bitmapObjectCacheBytes").GetValue(gpu)} source_reads={BitmapImageRasterizer.SourceReadCount} evictions={BitmapImageRasterizer.CacheEvictionCount}");
                    var timings = (Direct2DStageRenderer)gpu;
                    Console.WriteLine(FormattableString.Invariant($"bitmap_pointer_gpu_end_draw_ms={timings.DiagnosticEndDrawMilliseconds:0.###} swap_chain_present_ms={timings.DiagnosticSwapChainPresentMilliseconds:0.###}"));
                }
                if (sample >= 0) samples.Add(elapsed);
            }
            loopAllocated = GC.GetAllocatedBytesForCurrentThread() - allocatedStart;
            Console.WriteLine($"bitmap_pointer_warm_source_reads={BitmapImageRasterizer.SourceReadCount - warmSourceReads}");
            loopGc = $"{GC.CollectionCount(0) - gcCounts[0]}/{GC.CollectionCount(1) - gcCounts[1]}/{GC.CollectionCount(2) - gcCounts[2]}";
            var renderer = (Direct2DStageRenderer)RequireField(typeof(StageControl), "_direct2DRenderer").GetValue(stage)!;
            Console.WriteLine($"bitmap_pointer_selection_commands={renderer.SelectionCommandBuilds}/{renderer.SelectionCommandReuses} uniform={stage.TryGetBitmapMoveSelectionTranslation(out _)}");
            var outlineCacheField = RequireField(typeof(StageControl), "_bitmapMoveOutlineContours");
            var outlineCache = outlineCacheField.GetValue(stage);
            var translated = indices.ToDictionary(i => i, i => stage.GetObjectArrowSelectionContours(i)
                .SelectMany(contour => contour).OrderBy(point => point.X).ThenBy(point => point.Y).ToArray());
            var maximumOutlineError = 0f;
            try
            {
                outlineCacheField.SetValue(stage, null);
                foreach (var index in indices)
                {
                    var exhaustive = stage.GetObjectArrowSelectionContours(index).SelectMany(contour => contour)
                        .OrderBy(point => point.X).ThenBy(point => point.Y).ToArray();
                    AssertTimeline(exhaustive.Length == translated[index].Length,
                        "Bitmap movement outline reuse changed visible outline topology.");
                    for (var point = 0; point < exhaustive.Length; point++)
                        maximumOutlineError = Math.Max(maximumOutlineError, Math.Max(
                            Math.Abs(exhaustive[point].X - translated[index][point].X),
                            Math.Abs(exhaustive[point].Y - translated[index][point].Y)));
                }
            }
            finally { outlineCacheField.SetValue(stage, outlineCache); }
            AssertTimeline(maximumOutlineError <= 0.02f,
                "Bitmap movement outline reuse changed the exhaustive visible contours.");
            Console.WriteLine(FormattableString.Invariant($"bitmap_pointer_outline_oracle_max_world_error={maximumOutlineError:0.######}"));
        }
        finally { up.Invoke(form, [stage, new MouseEventArgs(MouseButtons.Left, 1, end.X, end.Y, 0)]); }
        samples.Sort();
        const double budget = 1000d / 150;
        Console.WriteLine(FormattableString.Invariant(
            $"bitmap_pointer_move objects={indices.Length} viewport={stage.Width}x{stage.Height} path=StageMouseMove+Flush+present warmup=8 samples=100 average_ms={samples.Average():0.###} p95_ms={samples[94]:0.###} max_ms={samples[^1]:0.###} fps={1000 / samples.Average():0.###}"));
        Console.WriteLine($"bitmap_pointer_move_budget_met={(samples.Average() < budget && samples[94] < budget && samples[^1] < budget).ToString().ToLowerInvariant()}");
        Console.WriteLine($"bitmap_pointer_move_gc={loopGc} allocated_bytes={loopAllocated}");
        AssertTimeline(indices.Any(i => scene.X[i] != before[i].X || scene.Y[i] != before[i].Y)
            && !stage.SelectionMoveFeedbackActive, "The editor pointer drag did not commit and clear its origin feedback.");
        AssertTimeline(RequireMethod(typeof(MainForm), "UndoLastEdit").Invoke(form, null) is true,
            "The editor bitmap drag created no undo step.");
        AssertTimeline(indices.All(i => scene.X[i] == before[i].X && scene.Y[i] == before[i].Y),
            "Undo failed to restore every bitmap after the measured editor pointer drag.");
        Console.WriteLine("bitmap_pointer_move_commit_undo=ok");
    }

    private static void RunThousandBitmapInteractionRegression()
    {
        const int count = 1000;
        const double budget = 1000d / 150;
        var rasters = new Dictionary<string, BitmapImageRaster>();
        for (var asset = 0; asset < count; asset++)
        {
            var raster = new BitmapImageRaster(new BitmapImageRasterKey($"bitmap-affine-performance-{asset}", 64, 64));
            for (var i = 0; i < raster.Pixels.Length; i += 4)
            {
                raster.Pixels[i] = (byte)(40 + asset % 100);
                raster.Pixels[i + 1] = 110;
                raster.Pixels[i + 2] = 220;
                raster.Pixels[i + 3] = 255;
            }
            rasters.Add($"bitmap-perf-{asset}", raster);
        }
        var scene = new VectorScene();
        scene.CreateEmpty();
        var indices = new int[count];
        for (var i = 0; i < count; i++)
            indices[i] = scene.AddBitmapObject(0, new PointF((i % 40 - 19.5f) * 800,
                (i / 40 - 12f) * 800), new BitmapObjectData
                { ImageAssetId = $"bitmap-perf-{i}", PlacedSize = new SizeF(650, 650),
                    VisibleContours = [[new(0, 0), new(1, 0), new(1, 1), new(0, 1)]] });
        scene.CompleteDeferredBuild();
        using var host = new Form { ClientSize = new Size(1920, 1080), ShowInTaskbar = false };
        using var stage = new StageControl(scene)
        { Dock = DockStyle.Fill, BitmapImageResolver = id => rasters.GetValueOrDefault(id) };
        host.Controls.Add(stage);
        host.Show();
        stage.SetVisibleWorldWidth(36000);
        stage.SetSelectionState(indices, indices[^1], [], default);
        Application.DoEvents();
        stage.Capture = true;
        try
        {
            foreach (var action in new[] { "skew", "move_after_skew", "scale_after_skew" })
            {
                var session = scene.BeginTransformSession(indices);
                var timings = new List<double>();
                for (var frame = -8; frame < 100; frame++)
                {
                    var phase = (frame + 8) % 20;
                    var matrix = action == "skew" ? Matrix3x2.CreateSkew(0.15f + phase * 0.001f, 0)
                        : action == "move_after_skew" ? Matrix3x2.CreateTranslation(phase * 12, phase * 6)
                        : Matrix3x2.CreateScale(1 + phase * 0.001f, 1 - phase * 0.001f);
                    var started = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (action == "move_after_skew") scene.ApplyTranslationSessionForPreview(session, phase * 12, phase * 6);
                    else scene.ApplyTransformSession(session, point => TransformBitmapProbe(point, matrix),
                        convertPrimitivesToPaths: true, rebuildGeometryIndex: false);
                    var transformMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    PaintStage(stage);
                    var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    if (frame == -1) Console.WriteLine(FormattableString.Invariant(
                        $"bitmap_1000_{action}_warm_transform_ms={transformMilliseconds:0.###} render_ms={stage.LastFrameRenderMilliseconds:0.###} command_ms={stage.LastDirect2DCommandMilliseconds:0.###} present_ms={stage.LastDirect2DPresentMilliseconds:0.###}"));
                    AssertTimeline(stage.LastFrameUsedDirect2D && stage.LastStats.DrawnObjects >= count,
                        "The bitmap performance fixture omitted images or fell back to software.");
                    AssertTimeline(stage.DistortResultBakeAttemptCount == 0,
                        "Affine interaction baked bitmap pixels on the CPU.");
                    if (frame >= 0) timings.Add(elapsed);
                }
                timings.Sort();
                var average = timings.Average();
                var p95 = timings[94];
                var maximum = timings[^1];
                Console.WriteLine(FormattableString.Invariant(
                    $"bitmap_1000_{action}_viewport=1920x1080 source=64x64 distinct_assets=1000 selected=1000 warmup=8 samples=100 average_ms={average:0.###} p95_ms={p95:0.###} max_ms={maximum:0.###} fps={1000 / average:0.###}"));
                Console.WriteLine($"bitmap_1000_{action}_budget_met={(average < budget && p95 < budget && maximum < budget).ToString().ToLowerInvariant()}");
                var bakeAttempts = stage.DistortResultBakeAttemptCount;
                stage.Capture = false;
                var releaseStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                PaintStage(stage);
                var releaseMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(releaseStarted).TotalMilliseconds;
                AssertTimeline(releaseMilliseconds < budget * 3 && stage.DistortResultBakeAttemptCount == bakeAttempts,
                    $"Releasing the {action} transform caused a long frame or CPU distortion bake ({releaseMilliseconds:0.###} ms).");
                Console.WriteLine(FormattableString.Invariant($"bitmap_1000_{action}_release_ms={releaseMilliseconds:0.###}"));
                stage.Capture = true;
            }
        }
        finally { stage.Capture = false; }
    }

    private static void RunBitmapFreeTransformPointerRegression()
    {
        using var form = new MainForm { Size = new Size(1280, 800) };
        form.CreateControl();
        form.PerformLayout();
        var scene = (VectorScene)RequireField(typeof(MainForm), "_scene").GetValue(form)!;
        var stage = (StageControl)RequireField(typeof(MainForm), "_stage").GetValue(form)!;
        stage.Size = new Size(760, 480);
        stage.CreateControl();
        stage.ZoomAt(new Point(stage.Width / 2, stage.Height / 2), 1f / stage.Zoom);
        var index = scene.AddBitmapObject(scene.ActiveLayer, new PointF(stage.CameraX, stage.CameraY),
            new BitmapObjectData { ImageAssetId = "pointer-bitmap", PlacedSize = new SizeF(3000, 2000) });
        RequireMethod(typeof(MainForm), "SetSelection", [typeof(int), typeof(bool)]).Invoke(form, [index, false]);
        RequireMethod(typeof(MainForm), "ActivateTool", [typeof(ToolMode)]).Invoke(form, [ToolMode.Transform]);
        RequireMethod(typeof(MainForm), "FlushDeferredPresentationOverlaysForPointerHitTest").Invoke(form, null);
        AssertTimeline(stage.TransformBoundsVisible,
            "Bitmap skew regression did not publish its transform overlay.");
        var handle = stage.GetTransformOverlayScreenGeometry().SkewHandles
            .Single(item => item.Kind == TransformHandleKind.SkewTop);
        var start = Point.Round(handle.Point);
        AssertTimeline(stage.HitTestTransformHandle(start) == TransformHandleKind.SkewTop,
            "Bitmap skew regression did not target the actual skew handle.");
        var end = new Point(start.X + 30, start.Y);
        var startWorld = stage.ScreenToWorld(start);
        var endWorld = stage.ScreenToWorld(end);
        var bounds = scene.GetObjectWorldBounds(index);
        var probe = new PointF(0.3f, 0.4f);
        var original = BitmapTransformProbe(scene, index, probe);
        RequireMethod(typeof(MainForm), "StageMouseDown").Invoke(form,
            [stage, new MouseEventArgs(MouseButtons.Left, 1, start.X, start.Y, 0)]);
        RequireMethod(typeof(MainForm), "ApplyTransformFromPointer").Invoke(form, [endWorld]);
        var factor = -(endWorld.X - startWorld.X) / bounds.Height;
        var expected = new PointF(original.X + (original.Y - bounds.Bottom) * factor, original.Y);
        AssertTimeline(scene.TryGetObjectDistortions(index, out _),
            "Free Transform still ignored the bitmap skew handle.");
        AssertBitmapTransformPoint(BitmapTransformProbe(scene, index, probe), expected,
            "Bitmap skew pointer preview changed the handles without the image mapping.");
        RequireMethod(typeof(MainForm), "StageMouseUp").Invoke(form,
            [stage, new MouseEventArgs(MouseButtons.Left, 1, end.X, end.Y, 0)]);
        AssertBitmapTransformPoint(BitmapTransformProbe(scene, index, probe), expected,
            "Committed bitmap skew disagreed with the pointer preview.");
        AssertTimeline(RequireMethod(typeof(MainForm), "UndoLastEdit").Invoke(form, null) is true,
            "Bitmap skew did not create an undoable pointer transaction.");
        AssertBitmapTransformPoint(BitmapTransformProbe(scene, index, probe), original,
            "Undo did not restore the bitmap before skewing.");
    }

    private static PointF TransformBitmapProbe(PointF point, Matrix3x2 transform)
    {
        var result = Vector2.Transform(new Vector2(point.X, point.Y), transform);
        return new PointF(result.X, result.Y);
    }

    private static PointF BitmapTransformProbe(VectorScene scene, int index, PointF normalized)
    {
        var localX = (normalized.X - 0.5f) * scene.Width[index];
        var localY = (normalized.Y - 0.5f) * scene.Height[index];
        var cosine = MathF.Cos(scene.Angle[index]);
        var sine = MathF.Sin(scene.Angle[index]);
        return scene.MapObjectPoint(index, new PointF(
            scene.X[index] + localX * cosine - localY * sine,
            scene.Y[index] + localX * sine + localY * cosine));
    }

    private static void AssertBitmapTransformPoint(PointF actual, PointF expected, string message)
    {
        AssertTimeline(Math.Abs(actual.X - expected.X) < 2 && Math.Abs(actual.Y - expected.Y) < 2,
            $"{message} actual={actual}, expected={expected}");
    }
}
