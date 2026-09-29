using System.Drawing.Imaging;
using System.Numerics;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static SymbolFilters RegressionSymbolFilters => new()
    {
        Blur = SymbolBlurFilter.Default,
        Glow = SymbolGlowFilter.Default with { ColorArgb = Color.Lime.ToArgb(), Strength = 1.5f },
        Shadow = SymbolShadowFilter.Default with { Angle = 90, Distance = 12 }
    };

    private static void RunSymbolFiltersModelRegression()
    {
        var filters = RegressionSymbolFilters with { Bevel = SymbolEdgeFilter.Default, GradientBevel = SymbolEdgeFilter.Default, GradientGlow = SymbolEdgeFilter.Default };
        var instance = new DrawingObjectInstanceDefinition { Filters = filters };
        var state = instance.EvaluateState(0);
        AssertTimeline(state.Filters == filters && instance.Clone().Filters == filters,
            "Symbol filters were lost by base-state evaluation or cloning.");
        AssertTimeline(!instance.SetStateAtFrame(0, state with
            { Filters = filters with { Blur = filters.Blur with { BlurX = float.NaN } } })
            && instance.Filters == filters, "Invalid filter parameters mutated an instance.");
        var disabled = filters with { Glow = filters.Glow with { Enabled = false } };
        instance.SetStateAtFrame(6, state with { Filters = disabled });
        var clone = instance.Clone();
        instance.SetStateAtFrame(6, state with { Filters = default });
        AssertTimeline(clone.EvaluateState(5).Filters == filters
            && clone.EvaluateState(6).Filters == disabled,
            "Filter keyframes or held exposures shared mutable state with their source.");
        AssertTimeline(default(SymbolFilters).IsValid && !default(SymbolFilters).HasEnabled,
            "Legacy documents did not default to disabled filters.");

        var project = VectorProject.CreateEmpty();
        var container = project.DrawingObjects[0];
        container.Scene.CreateEmpty(1, 12);
        container.Scene.AddFolderLayer("Filter parent folder");
        var child = project.AddDrawingObject("Filter child");
        child.Scene.AddObject(0, PointF.Empty, new SizeF(500, 500), 0, 0,
            Color.Coral, Color.Transparent, 4, ShapeKind.Rectangle);
        AssertTimeline(project.TryAddDrawingObjectInstance(container.Id, child.Id, PointF.Empty, out var nested)
            && nested is not null, "Could not set up nested filter regression.");
        nested!.Filters = new SymbolFilters { Glow = SymbolGlowFilter.Default };
        var scene = project.Scenes[0];
        AssertTimeline(project.TryAddSceneInstance(scene.Id, container.Id, PointF.Empty, 0, out var root)
            && root is not null, "Could not set up root filter regression.");
        root!.Filters = filters;
        var sceneTrack = scene.Timeline.FindTrackByTargetId(root.SceneLayerId)!;
        scene.Timeline.SetTrackDuration(sceneTrack.Id, 12);
        scene.Timeline.InsertKeyframe(sceneTrack.Id, 6);
        root.SetStateAtFrame(6, root.EvaluateState(0) with { Filters = disabled });
        var compositionScene = new VectorScene();
        var composition = SceneCompositionBuilder.Build(compositionScene, scene, project.DrawingObjects, 0);
        var groups = Enumerable.Range(0, compositionScene.LayerCount)
            .Where(layer => compositionScene.GetLayerSymbolFilters(layer).HasEnabled).ToArray();
        AssertTimeline(compositionScene.ObjectCount == 1 && composition.ObjectOwners.Count == 1 && groups.Length == 2,
            "Filtered composition changed geometry/provenance or lost nested effect groups.");
        var ancestry = new HashSet<int>();
        for (var layer = (int)compositionScene.ObjectLayer[0]; layer >= 0; layer = compositionScene.GetLayerParentIndex(layer))
            AssertTimeline(ancestry.Add(layer), "Filter groups introduced a layer cycle.");
        AssertTimeline(groups.All(ancestry.Contains),
            "A symbol inside a folder escaped its parent symbol's filter group.");
        var restoredComposition = new VectorScene();
        restoredComposition.RestoreSnapshot(compositionScene.CreateSnapshot());
        AssertTimeline(restoredComposition.HasSymbolFilters && groups.All(layer =>
            restoredComposition.GetLayerSymbolFilters(layer) == compositionScene.GetLayerSymbolFilters(layer)),
            "Background/pre-render scene snapshots discarded symbol filters.");
        var onions = new VectorScene();
        onions.CombineOnionSkinPreviews([(compositionScene, 0.4f, true)]);
        AssertTimeline(onions.HasSymbolFilters, "Onion-skin composition discarded symbol filters.");

        var restored = VectorProject.RestoreRestartSnapshot(
            EditorRestartStore.RoundTripProjectSnapshot(project.CreateRestartSnapshot()));
        AssertTimeline(restored.Scenes[0].Instances[0].Filters == filters
            && restored.Scenes[0].Instances[0].EvaluateState(6).Filters == disabled,
            "Restart serialization discarded base or animated filter parameters.");
        AssertTimeline(project.TryDuplicateDrawingObject(container.Id, out var duplicate)
            && duplicate!.Instances[0].Filters == nested.Filters,
            "Duplicating a symbol discarded nested instance filters.");

        var directory = CreateTemporaryDirectory("symbol-filters");
        try
        {
            var manifest = Path.Combine(directory, "Filters.v2dProject");
            ProjectVaultStore.Save(project, manifest);
            var loaded = ProjectVaultStore.Load(manifest);
            AssertTimeline(loaded.Scenes[0].Instances[0].Filters == filters
                && loaded.Scenes[0].Instances[0].EvaluateState(6).Filters == disabled
                && loaded.DrawingObjects.First(item => item.Id == container.Id).Instances[0].Filters == nested.Filters,
                "Save/Open did not retain stacked and disabled filter settings.");
            var package = DrawingObjectSymbolPackageService.CreatePackage(project, container.Id);
            var packagePath = Path.Combine(directory, "Filtered.V2DSymbol");
            package.Write(packagePath);
            var readPackage = DrawingObjectSymbolPackage.Read(packagePath);
            var targetProject = VectorProject.CreateEmpty();
            var imported = DrawingObjectSymbolPackageService.Import(targetProject, readPackage);
            AssertTimeline(imported.Instances[0].Filters == nested.Filters,
                "Symbol package import/export discarded filters.");
        }
        finally { DeleteTemporaryDirectory(directory); }
        Console.WriteLine("symbol_filters_model_snapshot_persistence=ok");
    }

    private static void RunSymbolFiltersRenderRegression()
    {
        AssertNormalCompositingFastPath();
        AssertEdgeFilters();
        AssertSymbolFilterGpuParity();
        AssertFilterThroughput();
        AssertSymbolFilterContentBounds();
        AssertSymbolFilterPresentation();
        using (var bitmap = new Bitmap(64, 64, PixelFormat.Format32bppPArgb))
        {
            using (var g = Graphics.FromImage(bitmap)) g.FillRectangle(Brushes.Red, 28, 28, 8, 8);
            SymbolFilterRasterizer.Apply(bitmap, new SymbolFilters
                { Blur = SymbolBlurFilter.Default with { BlurY = 0 } });
            var fringe = bitmap.GetPixel(26, 32);
            AssertTimeline(fringe.A > 0 && fringe.A < 255 && fringe.R >= 250 && fringe.G == 0
                && bitmap.GetPixel(32, 27).A == 0,
                "Premultiplied blur darkened a saturated fringe or ignored a zero radius axis.");
        }
        foreach (var angle in new[] { 0f, 90f, -90f, 180f })
        {
            using var bitmap = new Bitmap(64, 64, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bitmap)) g.FillRectangle(Brushes.Red, 30, 30, 4, 4);
            var filter = SymbolShadowFilter.Default with
                { BlurX = 0, BlurY = 0, Distance = 12, Angle = angle, Opacity = 1 };
            SymbolFilterRasterizer.Apply(bitmap, new SymbolFilters { Shadow = filter });
            var x = 31 + (int)Math.Round(Math.Cos(angle * Math.PI / 180) * 12);
            var y = 31 + (int)Math.Round(Math.Sin(angle * Math.PI / 180) * 12);
            AssertTimeline(bitmap.GetPixel(31, 31).ToArgb() == Color.Red.ToArgb()
                && bitmap.GetPixel(x, y).ToArgb() == Color.Black.ToArgb(),
                "Zero-blur drop shadow changed source colors or used the wrong angle/distance.");
        }

        var scene = new VectorScene();
        scene.CreateEmpty(2);
        scene.LayerKinds[0] = DrawingLayerKind.Folder;
        scene.LayerParentIds[1] = scene.LayerIds[0];
        scene.SetLayerSymbolFilters(0, new SymbolFilters
        {
            Shadow = SymbolShadowFilter.Default with { BlurX = 0, BlurY = 0, Angle = 0, Distance = 30, Opacity = 1 }
        });
        using (var output = new Bitmap(64, 64, PixelFormat.Format32bppPArgb))
        using (var g = Graphics.FromImage(output))
        using (var compositor = new LayerBlendCompositor(output.Size, LayerFilterBounds.GetPadding(scene, 1)))
        {
            compositor.CompositeTo(g, scene, (graphics, layer) => graphics.FillRectangle(Brushes.Red, -20, 20, 8, 8));
            AssertTimeline(output.GetPixel(14, 24).ToArgb() == Color.Black.ToArgb(),
                "An offscreen source's inward drop shadow was clipped at the viewport edge.");
        }

        var project = VectorProject.CreateEmpty();
        var symbol = project.DrawingObjects[0];
        symbol.Scene.AddObject(0, PointF.Empty, new SizeF(500, 500), 0, 0,
            Color.Red, Color.Transparent, 4, ShapeKind.Rectangle);
        project.TryAddSceneInstance(project.Scenes[0].Id, symbol.Id, PointF.Empty, 0, out var instance);
        instance!.Filters = RegressionSymbolFilters;
        var rendered = new VectorScene();
        var composition = SceneCompositionBuilder.Build(rendered, project.Scenes[0], project.DrawingObjects, 0);
        using var stage = new StageControl(rendered) { ClientSize = new Size(180, 140), WorldGridOpacity = 0 };
        using (var bitmap = new Bitmap(180, 140, PixelFormat.Format32bppPArgb))
        using (var g = Graphics.FromImage(bitmap))
        {
            RequireMethod(typeof(StageControl), "DrawSceneGdi").Invoke(stage, [g, rendered, int.MaxValue, false]);
            var center = Point.Round(stage.WorldToScreen(0, 0));
            AssertTimeline(bitmap.GetPixel(center.X, center.Y).R > 200
                && bitmap.GetPixel(center.X + 14, center.Y).A > 0
                && bitmap.GetPixel(center.X + 14, center.Y).G > 0,
                "The Stage did not render blur and glow around a selected symbol's source.");
        }

        // Reference projection must preserve the location of raster/SVG source pixels
        // when the filter compositor adds margins to its offscreen surface.
        symbol.Scene.CreateEmpty();
        symbol.Scene.AddImportedSvgObject(0, PointF.Empty, new SizeF(1000, 1000), 0,
            "<svg xmlns='http://www.w3.org/2000/svg' width='40' height='40'><rect width='40' height='40' fill='#ff0000'/></svg>", "Filter raster");
        instance.Filters = new SymbolFilters
        {
            Shadow = SymbolShadowFilter.Default with { BlurX = 0, BlurY = 0, Distance = 25, Angle = 0, Opacity = 1 }
        };
        composition = SceneCompositionBuilder.Build(rendered, project.Scenes[0], project.DrawingObjects, 0);
        stage.ConfigureReferenceView(project.Scenes[0], SceneDimension.ThreeD);
        stage.SetReferenceCameraOrientation(0, 0);
        stage.SetSceneCompositionResult(composition, rendered);
        AssertTimeline(stage.TryProjectScenePosition(Vector3.Zero, out var projectedCenter, out _),
            "Filter reference fixture could not project its origin.");
        using (var bitmap = new Bitmap(180, 140, PixelFormat.Format32bppPArgb))
        using (var g = Graphics.FromImage(bitmap))
        {
            RequireMethod(typeof(StageControl), "DrawReference3DCurrentScene").Invoke(stage, [g]);
            var color = SampleBitmap(bitmap, projectedCenter);
            AssertTimeline(color.R > 245 && color.G < 10 && color.A > 245,
                "Reference projection shifted filtered SVG content by the offscreen padding.");
        }
        Console.WriteLine("symbol_filters_pixels_groups_stage_projection=ok");
    }

    private static void AssertNormalCompositingFastPath()
    {
        const int size = 256;
        var source = new int[size * size]; var backdrop = new int[size * size];
        var random = new Random(93721);
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            source[y * size + x] = Pixel(x); backdrop[y * size + x] = Pixel(y);
        }
        using var compositor = new LayerBlendCompositor(new Size(size, size));
        var reference = (Func<int, int, LayerBlendMode, float, uint, int, int, int>)
            typeof(LayerBlendCompositor).GetMethod("CompositePixel", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .CreateDelegate(typeof(Func<int, int, LayerBlendMode, float, uint, int, int, int>));
        var composite = (Action<Bitmap, Bitmap, LayerBlendMode, float, uint>)
            typeof(LayerBlendCompositor).GetMethod("CompositeSurface", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .CreateDelegate(typeof(Action<Bitmap, Bitmap, LayerBlendMode, float, uint>), compositor);
        int maxError = 0;
        // Include the old fractional-opacity and non-Normal routes as controls.
        foreach (var mode in new[] { LayerBlendMode.Normal, LayerBlendMode.Multiply })
        foreach (var opacity in new[] { 1f, 0.43f })
        foreach (var crop in new[] { false, true })
        {
            using var src = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
            using var dst = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
            Copy(src, source, true); Copy(dst, backdrop, true);
            if (crop)
            {
                var regions = (Dictionary<Bitmap, Rectangle>)RequireField(typeof(LayerBlendCompositor), "_filterBounds").GetValue(compositor)!;
                // Column zero has exactly zero alpha, so cropping it is exact.
                regions[src] = new Rectangle(1, 0, size - 1, size);
            }
            composite(dst, src, mode, opacity, 0);
            var actual = new int[size * size]; Copy(dst, actual, false);
            for (int i = 0; i < actual.Length; i++)
            {
                int expected = reference(backdrop[i], source[i], mode, opacity, 0, i % size, i / size);
                for (int shift = 0; shift < 32; shift += 8)
                {
                    int delta = Math.Abs((actual[i] >> shift & 255) - (expected >> shift & 255));
                    maxError = Math.Max(maxError, delta);
                    AssertTimeline(delta <= (mode == LayerBlendMode.Normal && opacity == 1 ? 1 : 0),
                        $"Normal composite changed alpha/color: index={i}, channel={shift}, error={delta}");
                }
            }
        }
        Console.WriteLine($"normal_composite_alpha_pairs=65536,maximum_channel_error={maxError}");
        var scene = new VectorScene(); scene.CreateEmpty();
        using var reuse = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
        using var reuseGraphics = Graphics.FromImage(reuse);
        scene.SetLayerSymbolFilters(0, RegressionSymbolFilters);
        compositor.CompositeTo(reuseGraphics, scene, (g, _) => g.FillRectangle(Brushes.Red, 20, 20, 12, 12));
        scene.SetLayerSymbolFilters(0, default);
        reuseGraphics.Clear(Color.Transparent);
        compositor.CompositeTo(reuseGraphics, scene, (g, _) => g.FillRectangle(Brushes.Blue, 200, 200, 20, 20));
        AssertTimeline(reuse.GetPixel(210, 210).B == 255 && reuse.GetPixel(25, 25).A == 0,
            "Pooled surface retained the previous filter bounds or pixels.");
        Console.WriteLine("normal_composite_filtered_roi_pool_reuse=ok");
        int Pixel(int alpha) => (alpha << 24) | (random.Next(alpha + 1) << 16) | (random.Next(alpha + 1) << 8) | random.Next(alpha + 1);
        static void Copy(Bitmap bitmap, int[] pixels, bool write)
        {
            var data = bitmap.LockBits(new Rectangle(0, 0, size, size), write ? ImageLockMode.WriteOnly : ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                for (int y = 0; y < size; y++)
                    if (write) System.Runtime.InteropServices.Marshal.Copy(pixels, y * size, data.Scan0 + y * data.Stride, size);
                    else System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * size, size);
            }
            finally { bitmap.UnlockBits(data); }
        }
    }

    private static void AssertEdgeFilters()
    {
        var effect = SymbolEdgeFilter.Default with { StartColorArgb = Color.Red.ToArgb(), EndColorArgb = Color.Blue.ToArgb() };
        SymbolFilters[] cases = [new() { Bevel = effect }, new() { GradientBevel = effect }, new() { GradientGlow = effect }];
        for (int index = 0; index < cases.Length; index++)
        {
            using var source = new Bitmap(96, 96, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(source)) g.FillRectangle(Brushes.Gray, 30, 30, 36, 36);
            using var output = source.Clone(new Rectangle(0, 0, 96, 96), PixelFormat.Format32bppPArgb);
            SymbolFilterRasterizer.Apply(output, cases[index]);
            bool changed = false, expanded = false;
            for (int y = 0; y < 96; y++)
            for (int x = 0; x < 96; x++)
            {
                var before = source.GetPixel(x, y); var after = output.GetPixel(x, y);
                changed |= before.ToArgb() != after.ToArgb();
                expanded |= before.A == 0 && after.A > 0;
                if (index < 2) AssertTimeline(before.A == after.A, "Bevel changed silhouette alpha.");
            }
            AssertTimeline(changed && (index < 2 || expanded), "Edge filter did not shade the silhouette or create gradient glow.");
            var zero = effect with { Opacity = 0 };
            using var unchanged = source.Clone(new Rectangle(0, 0, 96, 96), PixelFormat.Format32bppPArgb);
            SymbolFilterRasterizer.Apply(unchanged, new SymbolFilters { Bevel = zero, GradientBevel = zero, GradientGlow = zero });
            AssertTimeline(unchanged.GetPixel(30, 40) == source.GetPixel(30, 40), "Zero-opacity edge filters changed pixels.");
            using var fallback = source.Clone(new Rectangle(0, 0, 96, 96), PixelFormat.Format32bppPArgb);
            using var gpu = new SymbolFilterGpuRasterizer();
            SymbolFilterRasterizer.Apply(fallback, cases[index], 1, gpu);
            for (int y = 0; y < 96; y++)
            for (int x = 0; x < 96; x++)
                {
                    var a = output.GetPixel(x,y); var b = fallback.GetPixel(x,y);
                    AssertTimeline(Math.Abs(a.A-b.A) <= 1 && Math.Abs(a.R-b.R) <= 1 && Math.Abs(a.G-b.G) <= 1 && Math.Abs(a.B-b.B) <= 1,
                        "Edge filter accelerator changed output.");
                }
        }
        AssertTimeline(!(new SymbolFilters { Bevel = effect with { Distance = float.NaN } }).IsValid,
            "Nonfinite bevel parameters were accepted.");
        Console.WriteLine("symbol_filters_bevel_gradient_pixels=ok");
    }

    private static void AssertSymbolFilterGpuParity()
    {
        var disabled = Environment.GetEnvironmentVariable("VECTOR_DISABLE_GPU_FILTERS");
        var explicitDisabled = Environment.GetEnvironmentVariable("VECTOR_DISABLE_EXPLICIT_GPU");
        try
        {
            Environment.SetEnvironmentVariable("VECTOR_DISABLE_GPU_FILTERS", null);
            Environment.SetEnvironmentVariable("VECTOR_DISABLE_EXPLICIT_GPU", null);
            using var gpu = new SymbolFilterGpuRasterizer();
            var cases = new[]
            {
                new SymbolFilters { Bevel = SymbolEdgeFilter.Default },
                new SymbolFilters { GradientBevel = SymbolEdgeFilter.Default },
                new SymbolFilters { GradientGlow = SymbolEdgeFilter.Default },
                new SymbolFilters { Bevel = SymbolEdgeFilter.Default with { BlurX = 0, BlurY = 0, Distance = 0 } },
                RegressionSymbolFilters with { Bevel = SymbolEdgeFilter.Default, GradientBevel = SymbolEdgeFilter.Default, GradientGlow = SymbolEdgeFilter.Default },
                new SymbolFilters { Blur = SymbolBlurFilter.Default },
                new SymbolFilters { Blur = SymbolBlurFilter.Default with { BlurX = 0, BlurY = 3.7f } },
                new SymbolFilters { Blur = SymbolBlurFilter.Default with { BlurX = 0.03f, BlurY = 0 } },
                new SymbolFilters { Blur = SymbolBlurFilter.Default with { BlurX = 128, BlurY = 107 } },
                new SymbolFilters { Glow = SymbolGlowFilter.Default with { BlurX = 0, BlurY = 0, Opacity = 0.3f } },
                RegressionSymbolFilters with
                {
                    Glow = SymbolGlowFilter.Default with { Strength = 10, Opacity = 0.83f, BlurX = 5.6f, BlurY = 2.9f },
                    Shadow = SymbolShadowFilter.Default with { Strength = 10, Distance = 11.3f, Angle = -137, Opacity = 0.91f }
                },
                RegressionSymbolFilters with { Shadow = SymbolShadowFilter.Default with { Distance = 256, Angle = 43 } }
            };
            var maxError = 0;
            foreach (var size in new[] { new Size(96, 80), new Size(257, 121), new Size(80, 129) })
            foreach (var alpha in new[] { 1, 137, 255 })
            foreach (var filter in cases)
            {
                using var expected = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
                using (var graphics = Graphics.FromImage(expected))
                {
                    using var brush = new SolidBrush(Color.FromArgb(alpha, 220, 71, 39));
                    graphics.FillRectangle(brush, 0, 0, 17, 31);
                    graphics.FillEllipse(brush, size.Width / 3, size.Height / 3, 39, 27);
                    graphics.FillRectangle(brush, size.Width - 9, size.Height - 16, 9, 16);
                }
                using var actual = expected.Clone(new Rectangle(Point.Empty, size), PixelFormat.Format32bppPArgb);
                const float scale = 1.35f;
                SymbolFilterRasterizer.Apply(expected, filter, scale);
                var before = gpu.CompletedApplications;
                SymbolFilterRasterizer.Apply(actual, filter, scale, gpu);
                if (gpu.AdapterName is null && gpu.LastFailure is not null)
                {
                    Console.WriteLine($"symbol_filters_gpu=unavailable:{gpu.LastFailure}");
                    return;
                }
                AssertTimeline(gpu.CompletedApplications == before + 1, $"GPU filter silently fell back: {gpu.LastFailure}");
                var a = Read(actual); var b = Read(expected);
                for (int i = 0; i < a.Length; i++)
                {
                    int delta = Math.Abs(a[i] - b[i]);
                    maxError = Math.Max(maxError, delta);
                    AssertTimeline(delta <= 1, $"GPU filter pixel differs: size={size}, alpha={alpha}, channel={i}, actual={a[i]}, expected={b[i]}, filter={filter}");
                }
            }
            using var fallback = new Bitmap(100, 100, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(fallback)) g.Clear(Color.Coral);
            using var cpu = fallback.Clone(new Rectangle(0, 0, 100, 100), PixelFormat.Format32bppPArgb);
            Environment.SetEnvironmentVariable("VECTOR_DISABLE_GPU_FILTERS", "1");
            var count = gpu.CompletedApplications;
            SymbolFilterRasterizer.Apply(fallback, RegressionSymbolFilters, 1, gpu);
            SymbolFilterRasterizer.Apply(cpu, RegressionSymbolFilters);
            AssertTimeline(count == gpu.CompletedApplications && Read(cpu).SequenceEqual(Read(fallback)),
                "Disabled GPU filters did not preserve the CPU fallback.");
            Environment.SetEnvironmentVariable("VECTOR_DISABLE_GPU_FILTERS", null);
            AssertSymbolFilterResultCache(gpu);
            AssertSymbolFilterPresentation(requireGpu: true);
            var reload = HotReloadModuleResolver.Resolve([typeof(SymbolFilterGpuRasterizer)]);
            AssertTimeline(reload.Modules == HotReloadModule.Rendering && reload.RequiresProcessRestart,
                "GPU filter shader changes must release/recompile device resources on restart.");
            Console.WriteLine($"symbol_filters_gpu_adapter={gpu.AdapterName}");
            Console.WriteLine($"symbol_filters_gpu_cases={count},maximum_channel_error={maxError},fallback=ok");
        }
        finally
        {
            Environment.SetEnvironmentVariable("VECTOR_DISABLE_GPU_FILTERS", disabled);
            Environment.SetEnvironmentVariable("VECTOR_DISABLE_EXPLICIT_GPU", explicitDisabled);
        }
        static byte[] Read(Bitmap bitmap)
        {
            var bytes = new byte[bitmap.Width * bitmap.Height * 4];
            var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                for (int y = 0; y < bitmap.Height; y++) System.Runtime.InteropServices.Marshal.Copy(
                    data.Scan0 + data.Stride * y, bytes, y * bitmap.Width * 4, bitmap.Width * 4);
            }
            finally { bitmap.UnlockBits(data); }
            return bytes;
        }
    }

    private static void AssertSymbolFilterResultCache(SymbolFilterGpuRasterizer gpu)
    {
        var input = new byte[128 * 96 * 4];
        for (int p = 0; p < input.Length; p += 4)
        { input[p] = 20; input[p + 1] = 50; input[p + 2] = 70; input[p + 3] = 137; }
        var first = (byte[])input.Clone();
        AssertTimeline(gpu.TryApply(first, 128, 96, RegressionSymbolFilters, 1), "Cache fixture did not dispatch.");
        var applications = gpu.CompletedApplications; var hits = gpu.CacheHits;
        var repeated = (byte[])input.Clone();
        AssertTimeline(gpu.TryApply(repeated, 128, 96, RegressionSymbolFilters, 1)
            && repeated.SequenceEqual(first) && gpu.CompletedApplications == applications && gpu.CacheHits == hits + 1,
            "Identical filter pixels were not reused exactly.");
        // Same byte length but different shape, scale, parameters and a single
        // changed alpha must all invalidate without relying on scene revisions.
        Miss(input, 96, 128, RegressionSymbolFilters, 1);
        Miss(input, 96, 128, RegressionSymbolFilters, 0.75f);
        var changed = RegressionSymbolFilters with { Shadow = SymbolShadowFilter.Default with { Angle = -137 } };
        Miss(input, 96, 128, changed, 0.75f);
        input[3]--; Miss(input, 96, 128, changed, 0.75f);
        Environment.SetEnvironmentVariable("VECTOR_DISABLE_GPU_FILTERS", "1");
        try { AssertTimeline(!gpu.TryApply((byte[])input.Clone(), 96, 128, changed, 0.75f), "Disable switch used cached GPU output."); }
        finally { Environment.SetEnvironmentVariable("VECTOR_DISABLE_GPU_FILTERS", null); }
        Console.WriteLine("symbol_filters_result_cache_exact_invalidation=ok");
        void Miss(byte[] source, int width, int height, SymbolFilters filter, float scale)
        {
            var before = gpu.CompletedApplications;
            var result = (byte[])source.Clone();
            AssertTimeline(gpu.TryApply(result, width, height, filter, scale) && gpu.CompletedApplications == before + 1, "Filter cache retained stale output.");
            using var fresh = new SymbolFilterGpuRasterizer();
            var expected = (byte[])source.Clone();
            AssertTimeline(fresh.TryApply(expected, width, height, filter, scale) && result.SequenceEqual(expected), "Cache invalidation changed filter pixels.");
        }
    }

    private static void AssertFilterThroughput()
    {
        const int width = 640, height = 480, samples = 12;
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using var gpu = new SymbolFilterGpuRasterizer();
        var filters = new SymbolFilters
        {
            Bevel = SymbolEdgeFilter.Default,
            GradientBevel = SymbolEdgeFilter.Default,
            GradientGlow = SymbolEdgeFilter.Default
        };
        void Reset(int i)
        {
            using var g = Graphics.FromImage(bitmap);
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(Color.FromArgb(180 + i, 100, 80, 180));
            g.FillEllipse(brush, 24, 24, width - 48, height - 48);
        }
        Reset(0); SymbolFilterRasterizer.Apply(bitmap, filters);
        Reset(0); SymbolFilterRasterizer.Apply(bitmap, filters, 1, gpu);
        var cpu = Measure(null, false);
        var accelerated = Measure(gpu, false);
        var dispatches = gpu.CompletedApplications;
        for (int i = 0; i < 3; i++) { Reset(i); SymbolFilterRasterizer.Apply(bitmap, filters, 1, gpu); }
        var hits = gpu.CacheHits;
        var cached = Measure(gpu, true);
        if (gpu.AdapterName is not null && gpu.LastFailure is null)
            AssertTimeline(gpu.CacheHits >= hits + samples, "Alternating filter symbols thrashed the bounded result cache.");
        Console.WriteLine($"symbol_filters_640x480_cpu_avg_ms={cpu.Average():F3},gpu_avg_ms={accelerated.Average():F3},cached_avg_ms={cached.Average():F3}");
        Console.WriteLine($"symbol_filters_640x480_cpu_p95_ms={cpu.Order().ElementAt(samples-1):F3},gpu_p95_ms={accelerated.Order().ElementAt(samples-1):F3},cached_p95_ms={cached.Order().ElementAt(samples-1):F3},adapter={gpu.AdapterName},failure={gpu.LastFailure}");
        double[] Measure(SymbolFilterGpuRasterizer? device, bool repeated)
        {
            var times = new double[samples];
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < samples; i++)
            {
                Reset(repeated ? i % 3 : i + 10);
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                SymbolFilterRasterizer.Apply(bitmap, filters, 1, device);
                times[i] = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }
            Console.WriteLine($"symbol_filters_allocated_{(device is null ? "cpu" : repeated ? "cached" : "gpu")}={GC.GetAllocatedBytesForCurrentThread()-allocated}");
            return times;
        }
    }

    private static void AssertSymbolFilterContentBounds()
    {
        // Distant corner sentinels force the reference to process the full canvas.
        // Their finite support cannot reach the source or the compared pixels.
        // This catches content-bound cropping of intermediate tails, including
        // low alpha that becomes visible only after a later high-strength effect.
        const int width = 320, height = 240, sentinelMargin = 64;
        Point[] positions = [new(160, 120), new(0, 120), new(318, 120), new(160, 0), new(160, 238)];
        foreach (var angle in new[] { -137f, 43f })
        foreach (var position in positions)
        {
            using var cropped = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(cropped))
            using (var brush = new SolidBrush(position == positions[0]
                       ? Color.FromArgb(1, 255, 80, 20) : Color.FromArgb(137, 255, 80, 20)))
                graphics.FillRectangle(brush, position.X, position.Y, 2, 2);
            using var full = cropped.Clone(new Rectangle(0, 0, width, height), PixelFormat.Format32bppPArgb);
            full.SetPixel(0, 0, Color.FromArgb(1, 255, 255, 255));
            full.SetPixel(width - 1, height - 1, Color.FromArgb(1, 255, 255, 255));
            var filters = new SymbolFilters
            {
                Blur = SymbolBlurFilter.Default with { BlurX = 3.8f, BlurY = 1.7f },
                Glow = SymbolGlowFilter.Default with
                    { BlurX = 5.6f, BlurY = 2.9f, Strength = 10, Opacity = 0.83f },
                Shadow = SymbolShadowFilter.Default with
                    { BlurX = 3.4f, BlurY = 4.1f, Strength = 10, Distance = 11.3f, Angle = angle, Opacity = 0.91f }
            };
            var scale = angle < 0 ? 0.75f : 1.35f;
            SymbolFilterRasterizer.Apply(cropped, filters, scale);
            SymbolFilterRasterizer.Apply(full, filters, scale);
            var actual = ReadPixels(cropped);
            var expected = ReadPixels(full);
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                if ((x < sentinelMargin && y < sentinelMargin)
                    || (x >= width - sentinelMargin && y >= height - sentinelMargin)) continue;
                var offset = (y * width + x) * 4;
                for (var channel = 0; channel < 4; channel++)
                    if (Math.Abs(actual[offset + channel] - expected[offset + channel]) > 1)
                        throw new InvalidOperationException(
                            $"Symbol filter ROI changed a full-canvas pixel at ({x},{y}), source={position}, angle={angle}.");
            }
            AssertTimeline(actual.Any(value => value != 0),
                "Symbol filter bounds discarded a low-alpha source.");
        }
        Console.WriteLine("symbol_filters_content_bounds=ok");

        static byte[] ReadPixels(Bitmap bitmap)
        {
            var bytes = new byte[width * height * 4];
            var data = bitmap.LockBits(new Rectangle(0, 0, width, height),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                for (var y = 0; y < height; y++)
                    System.Runtime.InteropServices.Marshal.Copy(
                        IntPtr.Add(data.Scan0, y * data.Stride), bytes, y * width * 4, width * 4);
            }
            finally { bitmap.UnlockBits(data); }
            return bytes;
        }
    }

    private static void AssertSymbolFilterPresentation(bool requireGpu = false)
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        scene.AddObject(0, PointF.Empty, new SizeF(1000, 1000), 0, 0, Color.Red, 4, ShapeKind.Rectangle);
        using var form = new Form
        {
            ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
            Location = new Point(-30_000, -30_000), ClientSize = new Size(240, 180)
        };
        using var stage = new StageControl(scene) { Dock = DockStyle.Fill, WorldGridOpacity = 0 };
        form.Controls.Add(stage);
        form.Show();
        Application.DoEvents();
        var renderer = (Direct2DStageRenderer)RequireField(typeof(StageControl), "_direct2DRenderer").GetValue(stage)!;
        var draw = RequireMethod(typeof(StageControl), "DrawGdiFrame");
        var hardwareAvailable = renderer.TryRender(stage, out _);
        var filters = RegressionSymbolFilters;
        if (hardwareAvailable)
        {
            foreach (var size in new[] { new Size(240, 180), new Size(300, 220) })
            {
                form.ClientSize = size;
                scene.SetLayerSymbolFilters(0, filters);
                AssertTimeline(!renderer.TryRender(stage, out _)
                    && renderer.TryPresentSoftwareFrame(stage, graphics => draw.Invoke(stage, [graphics])),
                    "Filtered software composition could not present after a native Direct2D frame.");
                var surface = (Bitmap)RequireField(typeof(Direct2DStageRenderer), "_softwareFrameSurface").GetValue(renderer)!;
                var center = Point.Round(stage.WorldToScreen(0, 0));
                AssertTimeline(surface.Size == size && surface.GetPixel(center.X + 24, center.Y).G > 40,
                    "Software presentation retained a stale size or omitted the symbol glow.");
                scene.SetLayerSymbolFilters(0, default);
                AssertTimeline(renderer.TryRender(stage, out _),
                    "Disabling symbol filters did not restore native Direct2D rendering.");
            }
            Console.WriteLine("symbol_filters_hardware_software_transitions=ok");
            if (requireGpu)
            {
                var compositor = (LayerBlendCompositor)RequireField(typeof(StageControl), "_layerBlendCompositor").GetValue(stage)!;
                var accelerator = (SymbolFilterGpuRasterizer)RequireField(typeof(LayerBlendCompositor), "_filterAccelerator").GetValue(compositor)!;
                AssertTimeline(accelerator.CompletedApplications > 0 && accelerator.LastFailure is null,
                    $"Stage did not use GPU filters: {accelerator.LastFailure}");
                Console.WriteLine("symbol_filters_gpu_stage_resize_present=ok");
            }
        }
        else Console.WriteLine("symbol_filters_hardware_software_transitions=unavailable");

        scene.SetLayerSymbolFilters(0, filters);
        stage.ConfigureReferenceView(VectorProject.CreateEmpty().Scenes[0], SceneDimension.ThreeD);
        stage.SetReference3DPlaybackActive(true);
        stage.Reference3DGpuOpticsEnabled = false;
        using var playback = new Bitmap(stage.Width, stage.Height);
        using var graphics = Graphics.FromImage(playback);
        AssertTimeline(!renderer.TryRenderReference3DPlaybackGdi(stage, graphics, out _)
            && renderer.LastReference3DPlaybackGdiStatus == "symbol_filters",
            "Fast playback bypassed symbol filter group composition.");
    }
}
