namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunFlashNestedMaskPlanRegression(string root)
    {
        var project = VectorProject.CreateEmpty();
        var parent = project.DrawingObjects[0];
        parent.Scene.CreateEmpty(2, 4);
        var outer = parent.Scene.AddMaskLayer("Outer mask");
        parent.Scene.SetSharedLayerMask(2, outer);
        parent.Scene.LayerLocked[outer] = true;
        var maskLeaf = project.AddDrawingObject("Nested mask geometry");
        // Partial overlap makes the future rendered result distinguish an
        // intersection from replacing either mask with the other.
        maskLeaf.Scene.AddObject(0, PointF.Empty, new SizeF(20, 20), 0, 0, Color.White, 4, ShapeKind.Rectangle);
        var inner = project.AddDrawingObject("Nested masked content");
        inner.Scene.CreateEmpty(1, 4);
        var innerMask = inner.Scene.AddMaskLayer("Inner mask");
        inner.Scene.LayerLocked[innerMask] = true;
        inner.Scene.AddObject(innerMask, PointF.Empty, new SizeF(20, 20), 0, 0, Color.White, 4, ShapeKind.Rectangle);
        inner.Scene.AddObject(1, PointF.Empty, new SizeF(40, 40), 0, 0, Color.Teal, 4, ShapeKind.Rectangle);
        AssertTimeline(project.TryAddDrawingObjectInstance(parent.Id, maskLeaf.Id, new PointF(7, 0),
            parent.Scene.LayerIds[outer], out _), "Could not prepare a mask containing an instance.");
        for (var layer = 1; layer <= 2; layer++)
            AssertTimeline(project.TryAddDrawingObjectInstance(parent.Id, inner.Id, new PointF(layer * 10, 0),
                parent.Scene.LayerIds[layer], out _), "Could not prepare nested masked content.");
        AssertTimeline(project.TryAddSceneInstance(project.Scenes[0].Id, parent.Id, new PointF(100, 50), 0, out _),
            "Could not prepare the nested-mask root transform.");
        foreach (var sceneTrack in project.Scenes[0].Timeline.Tracks)
            project.Scenes[0].Timeline.SetTrackDuration(sceneTrack.Id, 4);
        Check(project, blank: false);
        var manifest = ProjectVaultStore.Save(project,
            Path.Combine(root, "NestedMaskPlan", "Nested.v2dProject"));
        Check(ProjectVaultStore.Load(manifest), blank: false);
        maskLeaf.Scene.CreateEmpty();
        maskLeaf.Scene.AddObject(0, PointF.Empty, new SizeF(20, 20), 0, 0, Color.White, 4, ShapeKind.Ellipse);
        Check(project, blank: false, curved: true);
        var curveManifest = ProjectVaultStore.Save(project, Path.Combine(root, "NestedMaskCurve", "Curve.v2dProject"));
        Check(ProjectVaultStore.Load(curveManifest), blank: false, curved: true);
        maskLeaf.Scene.CreateEmpty();
        maskLeaf.Scene.AddPathObjectContours(0,
            [new[] { new PointF(-10, -10), new PointF(10, -10), new PointF(10, 10), new PointF(-10, 10) },
             new[] { new PointF(-2, -2), new PointF(2, -2), new PointF(2, 2), new PointF(-2, 2) }],
            0, Color.White, Color.Transparent, 4);
        Check(project, blank: false, hole: true);
        var holeManifest = ProjectVaultStore.Save(project, Path.Combine(root, "NestedMaskHole", "Hole.v2dProject"));
        Check(ProjectVaultStore.Load(holeManifest), blank: false, hole: true);
        var track = parent.Timeline.Tracks.Single(item => item.TargetId == parent.Scene.LayerIds[outer]);
        parent.Timeline.InsertBlankKeyframe(track.Id, 1);
        Check(project, blank: true);
        Console.WriteLine("flash_nested_mask_plan_shared_inner_outer_geometry_held_blank_save_reopen=ok");
        Console.WriteLine("flash_nested_mask_svg_intersection_curve_hole_hidden_artwork_blank_save_reopen=ok");
        CheckReferencedExportFrame();
        CheckNestedCoverageAndOpacity();
        CheckMaskRegionUnion();
        CheckMaskAfterGroupFilter();
        CheckDeferredParentMask();
        CheckFlashCompositionMaskFilterStagePixels();
        CheckFlashCompositionMaskUnderlayPixels();
        CheckFlashCompositionMaskProjectedPixels();
        CheckFlashCompositionMaskProjectedFilterPixels();
        CheckFlashCompositionMaskPlaybackBinding();
        CheckFlashCompositionMaskSceneOnionSkin();
        CheckFlashCompositionMaskDrawingOnionSkin();
        CheckFlashCompositionMaskLocalOnionSkin();
        CheckFlashCompositionMaskDragPreview();
        CheckFlashCompositionTextMaskCoverage();
        CheckFlashExactSvgMaskCoverage();
        CheckIndependentMaskPaths();

        static void CheckIndependentMaskPaths()
        {
            var scene = new VectorScene();
            scene.CreateEmpty(2);
            scene.AddObject(0, PointF.Empty, new SizeF(80, 80), 0, 0, Color.White, 4, ShapeKind.Rectangle);
            var plan = new SceneCompositionMaskPlan(new() { [1] = new[] { 0 } },
                new() { [0] = new[] { 0 } }, new());
            var supplied = new List<System.Drawing.Drawing2D.GraphicsPath>();
            using var regions = new SceneCompositionMaskRegions(scene, plan, 0,
                (Func<int, IReadOnlyList<System.Drawing.Drawing2D.GraphicsPath>>)(_ =>
                {
                    var ring = new System.Drawing.Drawing2D.GraphicsPath(System.Drawing.Drawing2D.FillMode.Alternate);
                    ring.AddEllipse(0, 0, 80, 80);
                    ring.AddEllipse(20, 20, 40, 40);
                    var bar = new System.Drawing.Drawing2D.GraphicsPath(System.Drawing.Drawing2D.FillMode.Winding);
                    bar.AddRectangle(new Rectangle(35, 0, 10, 80));
                    supplied.Add(ring);
                    supplied.Add(bar);
                    return new[] { ring, bar };
                }));
            using var clip = regions.CreateLayerClip(1);
            AssertTimeline(clip is not null && clip.IsVisible(40, 40) && clip.IsVisible(40, 10)
                && clip.IsVisible(10, 40) && !clip.IsVisible(25, 40) && !clip.IsVisible(2, 2),
                "Independent paths within one mask object lost union, fill rules, holes, or curved coverage.");
            using var again = regions.CreateLayerClip(1);
            AssertTimeline(supplied.Count == 2 && again is not null && again.IsVisible(40, 40),
                "Multi-path mask coverage was rebuilt or its owned cache was corrupted.");
            Console.WriteLine("flash_mask_single_object_independent_paths_union_fill_rules_curve_hole=ok");
        }

        static void CheckDeferredParentMask()
        {
            var scene = new VectorScene();
            scene.CreateEmpty(4);
            scene.LayerKinds[0] = scene.LayerKinds[3] = DrawingLayerKind.Mask;
            scene.LayerKinds[1] = DrawingLayerKind.Folder;
            scene.LayerParentIds[2] = scene.LayerIds[1];
            scene.AddObject(0, new PointF(5, 5), new SizeF(10, 10), 0, 0, Color.White, 4, ShapeKind.Rectangle);
            scene.AddObject(3, new PointF(10, 5), new SizeF(10, 10), 0, 0, Color.White, 4, ShapeKind.Rectangle);
            var plan = new SceneCompositionMaskPlan(
                new() { [1] = new[] { 0 }, [2] = new[] { 0, 3 } },
                new() { [0] = new[] { 0 }, [3] = new[] { 3 } }, new());
            using var regions = new SceneCompositionMaskRegions(scene, plan, 0, index =>
            {
                var path = new System.Drawing.Drawing2D.GraphicsPath();
                path.AddRectangle(new RectangleF(scene.X[index] - 5, 0, 10, 10));
                return path;
            });
            using var full = regions.CreateLayerClip(2);
            using var childBoundary = regions.CreateLayerBoundaryClip(2);
            using var parentBoundary = regions.CreateLayerBoundaryClip(1);
            using var relatedClip = regions.CreateRelatedLayerClip(2, 1);
            AssertTimeline(full is not null && childBoundary is not null && parentBoundary is not null
                && full.IsVisible(7, 5) && !full.IsVisible(12, 5)
                && childBoundary.IsVisible(12, 5) && !childBoundary.IsVisible(2, 5)
                && parentBoundary.IsVisible(2, 5) && !parentBoundary.IsVisible(12, 5)
                && relatedClip is not null && relatedClip.IsVisible(12, 5) && !relatedClip.IsVisible(2, 5),
                "A child pre-clipped its parent's filter input or lost its own inner mask.");
            Console.WriteLine("flash_mask_parent_filter_boundary_deferred_inner_retained=ok");
        }

        static void CheckMaskAfterGroupFilter()
        {
            var scene = new VectorScene();
            scene.CreateEmpty(2);
            scene.LayerKinds[0] = DrawingLayerKind.Folder;
            scene.LayerParentIds[1] = scene.LayerIds[0];
            scene.SetLayerSymbolFilters(0, new SymbolFilters
            {
                Shadow = SymbolShadowFilter.Default with
                    { BlurX = 0, BlurY = 0, Angle = 0, Distance = 30, Opacity = 1 }
            });
            foreach (var clipped in new[] { false, true })
            {
                using var output = new Bitmap(64, 64, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                using var graphics = Graphics.FromImage(output);
                using var compositor = new LayerBlendCompositor(output.Size, LayerFilterBounds.GetPadding(scene, 1));
                compositor.CompositeTo(graphics, scene,
                    (g, layer) => g.FillRectangle(Brushes.Red, 10, 20, 8, 8),
                    layer => clipped && layer == 0 ? new Region(new Rectangle(0, 0, 30, 64)) : null);
                AssertTimeline(output.GetPixel(14, 24).R > 250
                    && (clipped ? output.GetPixel(44, 24).A == 0 : output.GetPixel(44, 24).A > 250),
                    "A folder mask was applied before its shadow filter or erased its retained content.");
            }
            Console.WriteLine("flash_mask_group_clip_after_shadow_filter=ok");
        }

        static void CheckMaskRegionUnion()
        {
            var scene = new VectorScene();
            scene.CreateEmpty();
            var mask = scene.AddMaskLayer("Overlapping mask objects");
            scene.LayerLocked[mask] = true;
            scene.AddObject(mask, PointF.Empty, new SizeF(20, 20), 0, 0, Color.White, 4, ShapeKind.Rectangle);
            scene.AddObject(mask, new PointF(5, 0), new SizeF(20, 20), 0, 0, Color.White, 4, ShapeKind.Rectangle);
            var plan = new SceneCompositionMaskPlan(new() { [1] = new[] { mask } },
                new() { [mask] = new[] { mask } }, new());
            using var regions = new SceneCompositionMaskRegions(scene, plan, 0, index =>
            {
                var path = new System.Drawing.Drawing2D.GraphicsPath(System.Drawing.Drawing2D.FillMode.Alternate);
                path.AddRectangle(new RectangleF(scene.X[index] - 10, scene.Y[index] - 10, 20, 20));
                return path;
            });
            using (var first = regions.CreateMaskCoverage(mask))
            {
                AssertTimeline(first.IsVisible(0, 0) && first.IsVisible(14, 0) && !first.IsVisible(16, 0),
                    "Separate overlapping mask objects cancelled each other's coverage.");
                first.MakeEmpty();
            }
            using var second = regions.CreateMaskCoverage(mask);
            AssertTimeline(second.IsVisible(0, 0), "Mutating a returned mask region corrupted cached coverage.");
            CheckFlashCompositionMaskUnionStagePixels(scene, plan);
            Console.WriteLine("flash_mask_region_overlap_union_owned_cache=ok");
        }

        static void CheckNestedCoverageAndOpacity()
        {
            // The mask's own children are clipped before their union. Merely
            // stacking masks on the painted content cannot satisfy this case.
            const string source = """
                <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100" width="100" height="100">
                  <defs>
                    <mask id="inner" data-v2d-geometric-mask="1" maskUnits="userSpaceOnUse">
                      <ellipse cx="50" cy="50" rx="40" ry="40" fill="white"/>
                    </mask>
                    <mask id="outer" data-v2d-geometric-mask="1" maskUnits="userSpaceOnUse">
                      <g mask="url(#inner)"><rect width="100" height="100" fill="white"/></g>
                      <rect x="0" y="0" width="10" height="10" fill="white"/>
                    </mask>
                  </defs>
                  <g mask="url(#outer)"><rect width="100" height="100" fill="teal"/></g>
                </svg>
                """;
            foreach (var size in new[] { 100, 200, 100 })
            {
                var raster = ImportedSvgRasterizer.Rasterize(source, size, size);
                using var lease = raster.AcquireBitmap();
                Color At(float x, float y) => lease.Bitmap.GetPixel((int)(x * raster.PixelWidth), (int)(y * raster.PixelHeight));
                AssertTimeline(At(.05f, .05f).A > 250 && At(.5f, .5f).A > 250
                    && At(.2f, .2f).A <= 5 && At(.95f, .5f).A <= 5,
                    "A mask's internally clipped child or its union sibling has incorrect coverage.");
            }
            var scene = new VectorScene();
            scene.CreateEmpty();
            var index = scene.AddObject(0, PointF.Empty, new SizeF(20, 20), 0, 0, Color.Teal, 4, ShapeKind.Rectangle);
            scene.LayerOpacity[0] = .5f;
            CheckAlpha(DrawingObjectSvgExport.CreateDocument("opacity", "Opacity", scene, 0), 128);
            var folder = scene.AddFolderLayer("Half opacity folder");
            AssertTimeline(scene.LayerParentIds[scene.ObjectLayer[index]] == scene.LayerIds[folder]
                || scene.SetLayerParent(scene.ObjectLayer[index], folder), "Could not prepare opacity folder.");
            scene.LayerOpacity[folder] = .5f;
            CheckAlpha(DrawingObjectSvgExport.CreateDocument("opacity", "Opacity", scene, 0, SceneCompositionMaskPlan.Empty), 64);
            var localProject = VectorProject.CreateEmpty();
            var local = localProject.DrawingObjects[0];
            local.Scene.AddObject(0, PointF.Empty, new SizeF(20, 20), 0, 0, Color.Teal, 4, ShapeKind.Rectangle);
            local.Scene.LayerOpacity[0] = .5f;
            var localFolder = local.Scene.AddFolderLayer("Local half opacity folder");
            local.Scene.LayerOpacity[localFolder] = .5f;
            CheckAlpha(DrawingObjectSvgExport.CreateSymbolDocument(local, localProject.DrawingObjects, 0, 24m), 64);
            Console.WriteLine("symbol_svg_nested_mask_child_union_curve_cache_layer_folder_opacity=ok");

            static void CheckAlpha(System.Xml.Linq.XDocument document, int expected)
            {
                var raster = ImportedSvgRasterizer.Rasterize(document.ToString(), 64, 64);
                using var lease = raster.AcquireBitmap();
                var actual = lease.Bitmap.GetPixel(raster.PixelWidth / 2, raster.PixelHeight / 2).A;
                AssertTimeline(Math.Abs(actual - expected) <= 2,
                    $"SVG layer/folder opacity was applied the wrong number of times: {actual}, expected {expected}.");
            }
        }

        void CheckReferencedExportFrame()
        {
            var referenced = VectorProject.CreateEmpty();
            var container = referenced.DrawingObjects[0];
            container.Scene.CreateEmpty(1, 4);
            var leaf = referenced.AddDrawingObject("Late referenced content");
            leaf.Scene.AddObject(0, PointF.Empty, new SizeF(30, 20), 0, 0, Color.Teal, 4, ShapeKind.Rectangle);
            AssertTimeline(referenced.TryAddDrawingObjectInstance(container.Id, leaf.Id, new PointF(15, 10), out _),
                "Could not prepare late referenced content.");
            var track = container.Timeline.Tracks.Single();
            container.Timeline.InsertBlankKeyframe(track.Id, 0);
            container.Timeline.InsertKeyframe(track.Id, 2);
            AssertTimeline(DrawingObjectSvgExport.FirstContentFrame(container) == 2,
                "Referenced-only symbol export selected its initial blank frame.");
            var path = Path.Combine(root, "ReferencedSymbol.svg");
            DrawingObjectSvgExport.Write(path, container, referenced.DrawingObjects, 2, 24m);
            var document = System.Xml.Linq.XDocument.Load(path);
            AssertSvgExportViewport(document, new RectangleF(0, 0, 30, 20),
                "The SVG file export omitted referenced-only content.");
            AssertTimeline(document.Root!.Attribute("data-v2d-drawing-object-id")!.Value == container.Id
                && document.Root.Attribute("data-v2d-frame")!.Value == "2",
                "Referenced SVG file export lost the source symbol or selected frame.");
            Console.WriteLine("symbol_svg_referenced_only_first_content_frame_file=ok");
        }

        static void Check(VectorProject candidate, bool blank, bool curved = false, bool hole = false)
        {
            var scene = new VectorScene();
            var result = SceneCompositionBuilder.Build(scene, candidate.Scenes[0], candidate.DrawingObjects, 1, 24m);
            AssertTimeline(scene.ObjectCount == (blank ? 4 : 5), "Nested mask exposure lost or retained the wrong geometry.");
            var content = Enumerable.Range(0, scene.ObjectCount)
                .Where(index => scene.Width[index] == 40).ToArray();
            AssertTimeline(content.Length == 2, "Nested mask fixture lost content occurrences.");
            var shared = content.Select(index => result.LayerMasks.ClipsByLayer[scene.ObjectLayer[index]])
                .Aggregate((first, second) => first.Intersect(second).ToArray());
            AssertTimeline(shared.Count == 1, "Nested content did not share exactly one outer mask.");
            var outer = shared[0];
            foreach (var index in content)
            {
                var layer = scene.ObjectLayer[index];
                AssertTimeline(result.LayerMasks.ClipsByLayer[layer].Count == 2
                    && scene.TryGetMaskLayerIndex(layer, out var own)
                    && own != outer && result.LayerMasks.ClipsByLayer[layer].Contains(own),
                    "Outer mask inheritance overwrote the inner mask.");
            }
            var maskObjects = Enumerable.Range(0, scene.ObjectCount)
                .Where(index => result.LayerMasks.ContentsByMaskLayer[outer].Contains(scene.ObjectLayer[index])).ToArray();
            AssertTimeline(maskObjects.Length == (blank ? 0 : 1),
                "A nested mask's geometry or blank exposure was lost from its coverage plan.");
            if (!blank)
            {
                var mask = maskObjects[0];
                AssertTimeline(Math.Abs(scene.X[mask] - 107) < 0.001f && Math.Abs(scene.Y[mask] - 50) < 0.001f
                    && result.LayerMasks.ContainersByLayer[scene.ObjectLayer[mask]].Contains(outer),
                    "Nested mask geometry lost its outer transform or container identity.");
            }
            var document = DrawingObjectSvgExport.CreateDocument("nested-mask-render", "Nested mask render", scene, 0, result.LayerMasks);
            using (var stage = new StageControl(scene) { Size = new Size(400, 400) })
            {
                stage.SetVisibleWorldWidth(40);
                stage.SetSceneCompositionResult(result, scene, requestPaint: false);
                stage.RestoreViewState(stage.CaptureViewState() with { CameraX = 107, CameraY = 50, Zoom = 64 });
                using var regions = new SceneCompositionMaskRegions(scene, result.LayerMasks, 0,
                    index => stage.CreateCompositionMaskObjectPath(scene, index));
                using var coverage = regions.CreateMaskCoverage(outer);
                var center = stage.WorldToScreen(107, 50);
                AssertTimeline(coverage.IsVisible(center) == (!blank && !hole),
                    "The composed mask region lost its center/hole/blank coverage.");
                if (curved)
                {
                    using var curvePath = stage.CreateCompositionMaskObjectPath(scene, maskObjects[0]);
                    AssertTimeline(curvePath.PathTypes.Any(type => (type & 7) == 3),
                        "The Stage mask ellipse was reduced to a sampled polygon.");
                    AssertTimeline(!coverage.IsVisible(stage.WorldToScreen(97.1f, 40.1f)),
                        "The composed mask region replaced its curve by bounding-box coverage.");
                }
                foreach (var index in content)
                {
                    using var clip = regions.CreateLayerClip(scene.ObjectLayer[index]);
                    AssertTimeline(clip is not null && !clip.IsVisible(stage.WorldToScreen(118, 50)),
                        "The composed layer region did not intersect the outer mask.");
                }
                CheckFlashCompositionMaskStagePixels(stage, blank, hole, curved);
            }
            AssertSvgExportViewport(document, blank ? new RectangleF(0, 0, 1, 1) : new RectangleF(100, 40, 17, 20),
                "Nested SVG mask viewport did not retain the inner/outer intersection.");
            var raster = ImportedSvgRasterizer.Rasterize(document.ToString(), 170, 200);
            using var lease = raster.AcquireBitmap();
            if (blank)
                AssertTimeline(!raster.Pixels.Where((_, index) => index % 4 == 3).Any(alpha => alpha > 0),
                    "A blank nested outer mask exposed its inner content.");
            else
            {
                foreach (var point in new[] { new Point(15, 100), new Point(155, 100) })
                {
                    var color = lease.Bitmap.GetPixel(point.X, point.Y);
                    AssertTimeline(color.A >= 250 && color.R <= 2 && Math.Abs(color.G - 128) <= 2 && Math.Abs(color.B - 128) <= 2,
                        "Nested SVG masking lost content or exposed the white mask artwork.");
                }
                if (curved)
                    AssertTimeline(lease.Bitmap.GetPixel(0, 0).A <= 5
                        && document.Descendants().Any(element => element.Name.LocalName == "ellipse"
                            || element.Name.LocalName == "path" && (element.Attribute("d")?.Value.Contains('C') ?? false)),
                        $"Nested SVG masking replaced a curved mask by its rectangle or polygon. Corner={lease.Bitmap.GetPixel(0, 0)}; elements={string.Join(',', document.Descendants().Select(element => element.Name.LocalName).Distinct())}.");
                if (hole)
                    AssertTimeline(lease.Bitmap.GetPixel((int)(7f / 17f * raster.PixelWidth), raster.PixelHeight / 2).A <= 5,
                        "Nested SVG masking filled a compound mask's hole.");
            }
            var parent = candidate.DrawingObjects[0];
            var symbol = DrawingObjectSvgExport.CreateSymbolDocument(parent, candidate.DrawingObjects, 1, 24m);
            AssertSvgExportViewport(symbol, blank ? new RectangleF(0, 0, 1, 1) : new RectangleF(0, -10, 17, 20),
                "Exporting a referenced symbol omitted content or changed its authored coordinates.");
            AssertTimeline(symbol.Root!.Attribute("data-v2d-frame")!.Value == "1",
                "Referenced symbol export lost its requested source frame.");
        }
    }

    private static void RunFlashRepeatedMaskedOccurrenceRegression(string root)
    {
        var project = VectorProject.CreateEmpty();
        var parent = project.DrawingObjects[0];
        var wrapper = project.AddDrawingObject("Repeated mask wrapper");
        var masked = project.AddDrawingObject("Repeated masked symbol");
        masked.Scene.CreateEmpty(2, 4);
        var maskLayer = masked.Scene.AddMaskLayer("Mask");
        masked.Scene.SetSharedLayerMask(2, maskLayer);
        masked.Scene.LayerLocked[maskLayer] = true;
        masked.Scene.AddObject(maskLayer, PointF.Empty, new SizeF(20, 20), 0, 0, Color.White, 4, ShapeKind.Rectangle);
        for (var layer = 1; layer <= 2; layer++)
            masked.Scene.AddObject(layer, PointF.Empty, new SizeF(40, 40), 0, 0, Color.Teal, 4, ShapeKind.Rectangle);
        AssertTimeline(project.TryAddDrawingObjectInstance(wrapper.Id, masked.Id, PointF.Empty, out _),
            "Could not prepare a masked nested symbol.");
        foreach (var x in new[] { -100f, 100f })
            AssertTimeline(project.TryAddDrawingObjectInstance(parent.Id, wrapper.Id, new PointF(x, 0), out _),
                "Could not prepare repeated wrappers.");
        foreach (var y in new[] { -200f, 200f })
            AssertTimeline(project.TryAddSceneInstance(project.Scenes[0].Id, parent.Id, new PointF(0, y), 0, out _),
                "Could not prepare repeated scene roots.");
        Check(project);
        var manifest = ProjectVaultStore.Save(project,
            Path.Combine(root, "RepeatedMaskedOccurrence", "Repeated.v2dProject"));
        Check(ProjectVaultStore.Load(manifest));
        Console.WriteLine("flash_repeated_masked_occurrence_branch_root_save_reopen=ok");

        static void Check(VectorProject candidate)
        {
            var composition = new VectorScene();
            var result = SceneCompositionBuilder.Build(composition, candidate.Scenes[0], candidate.DrawingObjects, 0, 24m);
            AssertTimeline(composition.ObjectCount == 12, "Repeated mask composition lost geometry.");
            var masks = new HashSet<int>();
            var contentCount = 0;
            for (var index = 0; index < composition.ObjectCount; index++)
            {
                var layer = composition.ObjectLayer[index];
                if (composition.GetLayerKind(layer) == DrawingLayerKind.Mask) continue;
                contentCount++;
                AssertTimeline(composition.TryGetMaskLayerIndex(layer, out var mask),
                    "A repeated masked symbol lost its mask binding.");
                masks.Add(mask);
                var geometry = Enumerable.Range(0, composition.ObjectCount)
                    .Single(item => composition.ObjectLayer[item] == mask);
                AssertTimeline(Math.Abs(composition.X[index] - composition.X[geometry]) < 0.001f
                    && Math.Abs(composition.Y[index] - composition.Y[geometry]) < 0.001f,
                    "A repeated symbol was clipped by another branch's transformed mask.");
                AssertTimeline(result.TryGetOwner(index, out var contentOwner)
                    && result.TryGetOwner(geometry, out var maskOwner)
                    && contentOwner.RootInstanceId == maskOwner.RootInstanceId,
                    "A repeated mask crossed scene-root provenance.");
            }
            AssertTimeline(contentCount == 8 && masks.Count == 4,
                "Repeated symbols did not retain one shared mask per occurrence.");
            var document = DrawingObjectSvgExport.CreateDocument("repeated-masks", "Repeated masks", composition, 0);
            var viewport = document.Root!.Attribute("viewBox")!.Value.Split(' ')
                .Select(value => float.Parse(value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var raster = ImportedSvgRasterizer.Rasterize(document.ToString(), 440, 840);
            using var lease = raster.AcquireBitmap();
            foreach (var mask in masks)
            {
                var geometry = Enumerable.Range(0, composition.ObjectCount)
                    .Single(item => composition.ObjectLayer[item] == mask);
                var x = (int)((composition.X[geometry] - viewport[0]) * raster.PixelWidth / viewport[2]);
                var y = (int)((composition.Y[geometry] - viewport[1]) * raster.PixelHeight / viewport[3]);
                AssertTimeline(x >= 0 && x < raster.PixelWidth && y >= 0 && y < raster.PixelHeight
                    && lease.Bitmap.GetPixel(x, y).A >= 250,
                    "A repeated masked occurrence disappeared from the exported pixels.");
            }
        }
    }
}
