using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    public static void RunCollisionProjectPerformance()
    {
        var manifestPath = Path.GetFullPath(Path.Combine(
            Environment.CurrentDirectory,
            "破碎测试",
            "Untitled Project.v2dProject"));
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException(
                "The collision-project performance fixture was not found.",
                manifestPath);
        }

        var project = ProjectVaultStore.Load(manifestPath);
        var drawing = project.DrawingObjects.Single();
        var source = drawing.Scene;
        var definition = project.Scenes.Single();
        definition.Dimension = SceneDimension.ThreeD;
        var sceneTrack = definition.Timeline.Tracks.Single();
        definition.Timeline.SetTrackDuration(sceneTrack.Id, source.FrameCount);
        if (!project.TryAddSceneInstance(
                definition.Id,
                drawing.Id,
                PointF.Empty,
                0,
                out _))
        {
            throw new InvalidOperationException("The collision-project fixture could not create its scene instance.");
        }

        if (!project.TryAddSceneLight(definition.Id, SceneLightKind.Directional, out _))
        {
            throw new InvalidOperationException("The collision-project fixture could not create its directional light.");
        }

        Console.WriteLine(
            $"collision_fixture layers={source.LayerCount} objects={source.ObjectCount} "
            + $"frames={source.FrameCount} atoms={source.VirtualAtomCount} "
            + $"lights={definition.Lights.Count}");

        var compositionScene = new VectorScene();
        var buildSamples = new List<double>(32);
        var buildBucketSamples = new List<double>(32);
        var buildSetupSamples = new List<double>(32);
        var buildAppendSamples = new List<double>(32);
        var buildFinalizeSamples = new List<double>(32);
        SceneCompositionResult? composition = null;
        for (var frame = 0; frame < 32; frame++)
        {
            var watch = Stopwatch.StartNew();
            composition = SceneCompositionBuilder.Build(
                compositionScene,
                definition,
                project.DrawingObjects,
                frame,
                project.PlaybackFps);
            watch.Stop();
            buildSamples.Add(watch.Elapsed.TotalMilliseconds);
            buildBucketSamples.Add(SceneCompositionBuilder.LastBuildMetrics.BucketMilliseconds);
            buildSetupSamples.Add(SceneCompositionBuilder.LastBuildMetrics.SetupMilliseconds);
            buildAppendSamples.Add(SceneCompositionBuilder.LastBuildMetrics.AppendMilliseconds);
            buildFinalizeSamples.Add(SceneCompositionBuilder.LastBuildMetrics.FinalizeMilliseconds);
        }

        Console.WriteLine(
            $"collision_build avg_ms={Average(buildSamples):0.000} "
            + $"bucket_ms={Average(buildBucketSamples):0.000} "
            + $"setup_ms={Average(buildSetupSamples):0.000} "
            + $"append_ms={Average(buildAppendSamples):0.000} "
            + $"finalize_ms={Average(buildFinalizeSamples):0.000} "
            + $"objects={compositionScene.ObjectCount}");

        using var form = new Form
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-30_000, -30_000),
            ClientSize = new Size(1280, 720)
        };
        using var stage = new StageControl(compositionScene)
        {
            Dock = DockStyle.Fill,
            WorldGridOpacity = 0
        };
        form.Controls.Add(stage);
        stage.ConfigureReferenceView(definition, SceneDimension.ThreeD, ReferenceCameraMotion.Immediate);
        stage.ResetReferenceCameraView();
        stage.SetSceneCompositionResult(composition!, compositionScene);
        form.Show();
        Application.DoEvents();

        RenderCollisionFrame(stage, 0, composition!);
        stage.SetReference3DPlaybackActive(active: true);
        var renderSamples = new List<double>(32);
        var commandSamples = new List<double>(32);
        var presentSamples = new List<double>(32);
        var cacheSamples = new List<double>(32);
        var referenceGridSamples = new List<double>(32);
        var referenceSceneSamples = new List<double>(32);
        var referencePlanSamples = new List<double>(32);
        var referenceOverlaySamples = new List<double>(32);
        var referenceBaseStoreSamples = new List<double>(32);
        var referenceWorkspaceStoreSamples = new List<double>(32);
        var referenceLayerItemsSamples = new List<double>(32);
        var referenceIntersectionSamples = new List<double>(32);
        var referenceOpticsSamples = new List<double>(32);
        var referenceSortSamples = new List<double>(32);
        var cpuRasterSamples = new List<double>(32);
        var cpuRasterUploadSamples = new List<double>(32);
        var cpuRasterPreparationSamples = new List<double>(32);
        var cpuRasterBitmapBuilds = 0;
        var cpuRasterBitmapReuses = 0;
        var planBuilds = 0L;
        var opticsBuilds = 0L;
        var previousPlanBuildCount = stage.Reference3DRenderPlanBuildCount;
        var previousOpticsPlanBuildCount = stage.Reference3DOpticsPlanBuildCount;
        for (var frame = 1; frame <= 32; frame++)
        {
            var frameCompositionWatch = Stopwatch.StartNew();
            composition = SceneCompositionBuilder.Build(
                compositionScene,
                definition,
                project.DrawingObjects,
                frame,
                project.PlaybackFps);
            frameCompositionWatch.Stop();

            stage.SetSceneCompositionResult(
                composition,
                compositionScene,
                preserveWorkspaceFrameCache: true);
            stage.Frame = frame;
            var renderWatch = Stopwatch.StartNew();
            stage.Update();
            Application.DoEvents();
            renderWatch.Stop();
            renderSamples.Add(renderWatch.Elapsed.TotalMilliseconds);
            commandSamples.Add(stage.LastDirect2DCommandMilliseconds);
            presentSamples.Add(stage.LastDirect2DPresentMilliseconds);
            cacheSamples.Add(stage.LastDirect2DCacheMaintenanceMilliseconds);
            referenceGridSamples.Add(stage.LastDirect2DReference3DGridMilliseconds);
            referenceSceneSamples.Add(stage.LastDirect2DReference3DSceneMilliseconds);
            referencePlanSamples.Add(stage.LastDirect2DReference3DPlanLookupMilliseconds);
            referenceOverlaySamples.Add(stage.LastDirect2DReference3DOverlayMilliseconds);
            referenceBaseStoreSamples.Add(stage.LastDirect2DReference3DBaseStoreMilliseconds);
            referenceWorkspaceStoreSamples.Add(stage.LastDirect2DReference3DWorkspaceStoreMilliseconds);
            referenceLayerItemsSamples.Add(stage.LastReference3DLayerItemsMilliseconds);
            referenceIntersectionSamples.Add(stage.LastReference3DIntersectionMilliseconds);
            referenceOpticsSamples.Add(stage.LastReference3DOpticsMilliseconds);
            referenceSortSamples.Add(stage.LastReference3DSortMilliseconds);
            cpuRasterSamples.Add(stage.LastDirect2DReference3DCpuRasterMilliseconds);
            cpuRasterUploadSamples.Add(stage.LastDirect2DReference3DCpuRasterUploadMilliseconds);
            cpuRasterPreparationSamples.Add(
                stage.LastDirect2DReference3DCpuRasterPreparationMilliseconds);
            cpuRasterBitmapBuilds += stage.LastDirect2DReference3DCpuRasterBitmapBuilds;
            cpuRasterBitmapReuses += stage.LastDirect2DReference3DCpuRasterBitmapReuses;
            var planBuildCount = stage.Reference3DRenderPlanBuildCount;
            var opticsPlanBuildCount = stage.Reference3DOpticsPlanBuildCount;
            planBuilds += Math.Max(0, planBuildCount - previousPlanBuildCount);
            opticsBuilds += Math.Max(0, opticsPlanBuildCount - previousOpticsPlanBuildCount);
            previousPlanBuildCount = planBuildCount;
            previousOpticsPlanBuildCount = opticsPlanBuildCount;

            if (frame <= 4)
            {
                if (frame == 1)
                {
                    var items = stage.GetReference3DSceneRenderItems();
                    var fillItems = items.Count(item => item.Kind == Reference3DRenderKind.FrontFill);
                    var strokeItems = items
                        .Where(item => item.Kind == Reference3DRenderKind.FrontStroke)
                        .ToArray();
                    var fillGradientProfiles = items
                        .Where(item => item.Kind == Reference3DRenderKind.FrontFill)
                        .Select(item => GradientProfile(compositionScene, item.ObjectIndex))
                        .Distinct(StringComparer.Ordinal)
                        .Count();
                    var gradientKinds = items
                        .Where(item => item.Kind is Reference3DRenderKind.FrontFill
                            or Reference3DRenderKind.FrontStroke)
                        .GroupBy(item => compositionScene.GetGradientKind(item.ObjectIndex))
                        .OrderBy(group => group.Key)
                        .Select(group => $"{group.Key}:{group.Count()}");
                    var fillGradientAxes = items
                        .Where(item => item.Kind == Reference3DRenderKind.FrontFill)
                        .Select(item =>
                        {
                            var start = compositionScene.GetGradientStart(item.ObjectIndex);
                            var end = compositionScene.GetGradientEnd(item.ObjectIndex);
                            return $"{start.X:R},{start.Y:R}|{end.X:R},{end.Y:R}";
                        })
                        .Distinct(StringComparer.Ordinal)
                        .Count();
                    var planeGroups = items
                        .Where(item => item.PlaneKey.IsValid)
                        .GroupBy(item => item.PlaneKey)
                        .ToArray();
                    var projectedPointCount = items
                        .SelectMany(item => item.Contours)
                        .Sum(contour => contour.Points.Length);
                    var frontItems = items
                        .Where(item => item.Kind is Reference3DRenderKind.FrontFill
                            or Reference3DRenderKind.FrontStroke)
                        .ToArray();
                    var projectedBoundsPairs = 0;
                    for (var leftIndex = 0; leftIndex < frontItems.Length; leftIndex++)
                    {
                        if (!TryGetProjectedBounds(frontItems[leftIndex], out var leftBounds)) continue;
                        for (var rightIndex = leftIndex + 1;
                             rightIndex < frontItems.Length;
                             rightIndex++)
                        {
                            if (!TryGetProjectedBounds(frontItems[rightIndex], out var rightBounds)
                                || leftBounds.Left > rightBounds.Right + 0.75f
                                || rightBounds.Left > leftBounds.Right + 0.75f
                                || leftBounds.Top > rightBounds.Bottom + 0.75f
                                || rightBounds.Top > leftBounds.Bottom + 0.75f)
                            {
                                continue;
                            }
                            projectedBoundsPairs++;
                        }
                    }
                    var cpuBezierPathItems = items.Count(item =>
                        compositionScene.ShapeKind[item.ObjectIndex] == ShapeKind.Path
                        && compositionScene.TryGetPathBezierLocalContours(
                            item.ObjectIndex,
                            out var bezierContours)
                        && bezierContours.Length > 0);
                    var cpuGradientPathItems = items.Count(item =>
                        item.Kind == Reference3DRenderKind.FrontFill
                        && compositionScene.HasGradientPath(item.ObjectIndex));
                    var cpuNonIdentityResponses = items.Count(item =>
                        item.OpticalResponse != Reference3DOpticalResponse.Identity);
                    var cpuOpticalContourItems = items.Count(item => item.OpticalSurfaceContours is not null);
                    var cpuNonDrawingLayers = items.Count(item =>
                        compositionScene.GetLayerKind(item.LayerIndex) != DrawingLayerKind.Drawing);
                    Console.WriteLine(
                        $"collision_items fills={fillItems} strokes={strokeItems.Length} "
                        + $"fragment_clips={items.Count(item => item.FragmentClip is not null)} "
                        + $"occlusion={items.Count(item => item.OcclusionContours is not null)} "
                        + $"opacity={items.Count(item => item.MaterialOpacity < 0.999999f)} "
                        + $"optical_surface={items.Count(item => item.OpticalSurface is not null)} "
                        + $"stroke_solid_base={strokeItems.Count(item => item.SolidStrokeOpticalBaseArgb is not null)} "
                        + $"stroke_gradient={strokeItems.Count(item => item.OpticalStrokeGradientStops is { Length: > 0 })} "
                        + $"fill_gradient_profiles={fillGradientProfiles} "
                        + $"gradient_kinds={string.Join(',', gradientKinds)} "
                        + $"fill_gradient_axes={fillGradientAxes} "
                        + $"plane_groups={planeGroups.Length} "
                        + $"plane_singletons={planeGroups.Count(group => group.Count() == 1)} "
                        + $"projected_points={projectedPointCount} "
                        + $"bounds_pairs={projectedBoundsPairs} "
                        + $"cpu_bezier={cpuBezierPathItems} "
                        + $"cpu_gradient_path={cpuGradientPathItems} "
                        + $"cpu_response={cpuNonIdentityResponses} "
                        + $"cpu_optical_contours={cpuOpticalContourItems} "
                        + $"cpu_non_drawing_layers={cpuNonDrawingLayers}");
                    foreach (var item in strokeItems.Take(3))
                    {
                        Console.WriteLine(
                            $"collision_stroke_item obj={item.ObjectIndex} contours={item.Contours.Length} "
                            + $"fragment={item.FragmentClip is not null} secondary={item.SecondaryObjectIndex} "
                            + $"occlusion={item.OcclusionContours is not null} opacity={item.MaterialOpacity:0.000} "
                            + $"solid={item.SolidStrokeOpticalBaseArgb?.ToString() ?? "null"} "
                            + $"layers={item.LocalLightLayers?.Length ?? 0}/{item.ShadowLayers?.Length ?? 0} "
                            + $"stroke={compositionScene.Stroke[item.ObjectIndex]:0.000} "
                            + $"shape={compositionScene.ShapeKind[item.ObjectIndex]}");
                    }
                }
                Console.WriteLine(
                    $"collision_frame frame={frame} build_ms={frameCompositionWatch.Elapsed.TotalMilliseconds:0.000} "
                    + $"render_ms={renderWatch.Elapsed.TotalMilliseconds:0.000} "
                    + $"command_ms={stage.LastDirect2DCommandMilliseconds:0.000} "
                    + $"present_ms={stage.LastDirect2DPresentMilliseconds:0.000} "
                    + $"cache_ms={stage.LastDirect2DCacheMaintenanceMilliseconds:0.000} "
                    + $"plan_builds={stage.Reference3DRenderPlanBuildCount} "
                    + $"optics_builds={stage.Reference3DOpticsPlanBuildCount} "
                    + $"material={stage.LastDirect2DReference3DMaterialBitmapCacheBuilds}/"
                    + $"{stage.LastDirect2DReference3DMaterialBitmapCacheReuses}/"
                    + $"{stage.LastDirect2DReference3DMaterialBitmapSubmissions} "
                    + $"gradients={stage.LastDirect2DGradientBrushCacheBuilds}/"
                    + $"{stage.LastDirect2DGradientBrushCacheReuses} "
                    + $"local_geometry={stage.LastDirect2DReference3DLocalPathGeometryCacheBuilds}/"
                    + $"{stage.LastDirect2DReference3DLocalPathGeometryCacheReuses} "
                    + $"workspace={stage.LastDirect2DWorkspaceFrameCacheBuilds}/"
                    + $"{stage.LastDirect2DWorkspaceFrameCacheHits}/"
                    + $"{stage.LastDirect2DWorkspaceFrameCacheEvictions} "
                    + $"stroke_batch={stage.LastDirect2DReference3DStrokeBatchSubmissions}/"
                    + $"{stage.LastDirect2DReference3DStrokeBatchObjects} "
                    + $"affine_approx={stage.LastDirect2DReference3DProjectiveAffineApproximationUses} "
                    + $"screen_masks={stage.LastDirect2DReference3DProjectiveScreenMaskBuilds} "
                     + $"parallel_plan={stage.LastReference3DParallelRenderPlanWorkers}/"
                      + $"{stage.LastReference3DParallelRenderPlanLayers}/"
                      + $"{stage.LastReference3DParallelRenderPlanObjects}/"
                      + $"{stage.LastReference3DParallelRenderPlanMilliseconds:0.000}ms "
                     + $"plan_parts={stage.LastReference3DLayerItemsMilliseconds:0.000}/"
                     + $"{stage.LastReference3DIntersectionMilliseconds:0.000}/"
                     + $"{stage.LastReference3DOpticsMilliseconds:0.000}ms "
                     + $"cpu_raster={stage.LastDirect2DReference3DCpuRasterWorkers}/"
                      + $"{stage.LastDirect2DReference3DCpuRasterTiles}/"
                      + $"{stage.LastDirect2DReference3DCpuRasterCommands}/"
                      + $"{stage.LastDirect2DReference3DCpuRasterMilliseconds:0.000}/"
                      + $"{stage.LastDirect2DReference3DCpuRasterUploadMilliseconds:0.000}ms "
                      + $"cpu_bitmap={stage.LastDirect2DReference3DCpuRasterBitmapBuilds}/"
                      + $"{stage.LastDirect2DReference3DCpuRasterBitmapReuses} "
                      + $"scale={stage.LastDirect2DReference3DCpuRasterScale:0.00} "
                      + $"cpu_prepare={stage.LastDirect2DReference3DCpuRasterPreparationWorkers}/"
                      + $"{stage.LastDirect2DReference3DCpuRasterPreparationMilliseconds:0.000}ms "
                      + $"cpu_fallback={stage.LastDirect2DReference3DCpuRasterFallbackReason} "
                      + $"parts={stage.LastDirect2DReference3DGridMilliseconds:0.000}/"
                      + $"{stage.LastDirect2DReference3DSceneMilliseconds:0.000}/"
                      + $"{stage.LastDirect2DReference3DPlanLookupMilliseconds:0.000}/"
                      + $"{stage.LastDirect2DReference3DOverlayMilliseconds:0.000}/"
                      + $"{stage.LastDirect2DReference3DBaseStoreMilliseconds:0.000}/"
                      + $"{stage.LastDirect2DReference3DWorkspaceStoreMilliseconds:0.000}ms "
                       + $"projective_fills={stage.LastDirect2DReference3DProjectiveGradientDomainFills} "
                    + $"stats={stage.LastStats.VisibleObjects}/{stage.LastStats.DrawnObjects}");
            }
        }

        stage.SetReference3DPlaybackActive(active: false);

        Console.WriteLine(
            $"collision_render avg_ms={Average(renderSamples):0.000} "
            + $"fps={1000d / Math.Max(0.001, Average(renderSamples)):0.0} "
            + $"command_avg_ms={Average(commandSamples):0.000} "
            + $"present_avg_ms={Average(presentSamples):0.000} "
            + $"cache_avg_ms={Average(cacheSamples):0.000} "
            + $"plan_build_delta={planBuilds} optics_build_delta={opticsBuilds} "
            + $"material={stage.LastDirect2DReference3DMaterialBitmapCacheBuilds}/"
            + $"{stage.LastDirect2DReference3DMaterialBitmapCacheReuses}/"
            + $"{stage.LastDirect2DReference3DMaterialBitmapSubmissions} "
            + $"gradients={stage.LastDirect2DGradientBrushCacheBuilds}/"
            + $"{stage.LastDirect2DGradientBrushCacheReuses} "
            + $"local_geometry={stage.LastDirect2DReference3DLocalPathGeometryCacheBuilds}/"
            + $"{stage.LastDirect2DReference3DLocalPathGeometryCacheReuses} "
            + $"workspace={stage.LastDirect2DWorkspaceFrameCacheBuilds}/"
            + $"{stage.LastDirect2DWorkspaceFrameCacheHits}/"
            + $"{stage.LastDirect2DWorkspaceFrameCacheEvictions} "
            + $"stroke_batch={stage.LastDirect2DReference3DStrokeBatchSubmissions}/"
            + $"{stage.LastDirect2DReference3DStrokeBatchObjects} "
            + $"affine_approx={stage.LastDirect2DReference3DProjectiveAffineApproximationUses} "
            + $"screen_masks={stage.LastDirect2DReference3DProjectiveScreenMaskBuilds} "
             + $"parallel_plan={stage.LastReference3DParallelRenderPlanWorkers}/"
             + $"{stage.LastReference3DParallelRenderPlanLayers}/"
             + $"{stage.LastReference3DParallelRenderPlanObjects}/"
             + $"{stage.LastReference3DParallelRenderPlanMilliseconds:0.000}ms "
             + $"cpu_raster={stage.LastDirect2DReference3DCpuRasterWorkers}/"
             + $"{stage.LastDirect2DReference3DCpuRasterTiles}/"
             + $"{stage.LastDirect2DReference3DCpuRasterCommands}/"
             + $"{stage.LastDirect2DReference3DCpuRasterMilliseconds:0.000}/"
             + $"{stage.LastDirect2DReference3DCpuRasterUploadMilliseconds:0.000}ms "
             + $"scale={stage.LastDirect2DReference3DCpuRasterScale:0.00} "
             + $"cpu_prepare={stage.LastDirect2DReference3DCpuRasterPreparationWorkers}/"
             + $"{stage.LastDirect2DReference3DCpuRasterPreparationMilliseconds:0.000}ms "
             + $"cpu_fallback={stage.LastDirect2DReference3DCpuRasterFallbackReason} "
             + $"cpu_avg={Average(cpuRasterSamples):0.000}/"
             + $"{Average(cpuRasterUploadSamples):0.000}/"
             + $"{Average(cpuRasterPreparationSamples):0.000}ms "
             + $"cpu_bitmap={cpuRasterBitmapBuilds}/{cpuRasterBitmapReuses} "
             + $"parts_avg={Average(referenceGridSamples):0.000}/"
             + $"{Average(referenceSceneSamples):0.000}/"
             + $"{Average(referencePlanSamples):0.000}/"
             + $"{Average(referenceOverlaySamples):0.000}/"
             + $"{Average(referenceBaseStoreSamples):0.000}/"
             + $"{Average(referenceWorkspaceStoreSamples):0.000}ms "
             + $"plan_parts_avg={Average(referenceLayerItemsSamples):0.000}/"
             + $"{Average(referenceIntersectionSamples):0.000}/"
             + $"{Average(referenceOpticsSamples):0.000}/"
             + $"{Average(referenceSortSamples):0.000}ms "
               + $"projective_fills={stage.LastDirect2DReference3DProjectiveGradientDomainFills} "
            + $"material_cache={stage.Direct2DReference3DMaterialBitmapCacheEntryCount}/"
            + $"{stage.Direct2DReference3DMaterialBitmapCacheBytes}");

        var warmRenderSamples = new List<double>(32);
        var warmCacheHits = 0;
        for (var frame = 1; frame <= 32; frame++)
        {
            composition = SceneCompositionBuilder.Build(
                compositionScene,
                definition,
                project.DrawingObjects,
                frame,
                project.PlaybackFps);
            stage.SetSceneCompositionResult(
                composition,
                compositionScene,
                preserveWorkspaceFrameCache: true);
            stage.Frame = frame;
            var renderWatch = Stopwatch.StartNew();
            stage.Update();
            Application.DoEvents();
            renderWatch.Stop();
            warmRenderSamples.Add(renderWatch.Elapsed.TotalMilliseconds);
            warmCacheHits += stage.LastDirect2DWorkspaceFrameCacheHits;
        }

        Console.WriteLine(
            $"collision_workspace_replay avg_ms={Average(warmRenderSamples):0.000} "
            + $"fps={1000d / Math.Max(0.001, Average(warmRenderSamples)):0.0} "
            + $"cache_hits={warmCacheHits}/{warmRenderSamples.Count} "
            + $"entries={stage.Direct2DWorkspaceFrameCacheEntryCount} "
            + $"bytes={stage.Direct2DWorkspaceFrameCacheBytes}");

        if (source.FrameCount <= 33)
        {
            throw new InvalidOperationException(
                "The collision-project fixture must contain an uncached frame after the warm replay.");
        }

        var preRenderFrame = 33;
        var preRenderScene = new VectorScene();
        var preRenderComposition = SceneCompositionBuilder.Build(
            preRenderScene,
            definition,
            project.DrawingObjects,
            preRenderFrame,
            project.PlaybackFps);
        var preRenderWatch = Stopwatch.StartNew();
        var preRendered = stage.TryPreRenderReference3DFrame(
            preRenderFrame,
            preRenderScene,
            preRenderComposition);
        preRenderWatch.Stop();
        if (!preRendered || stage.LastDirect2DWorkspaceFrameCacheBuilds <= 0)
        {
            throw new InvalidOperationException(
                $"Workspace pre-render did not build frame {preRenderFrame}. "
                + $"result={preRendered}, builds={stage.LastDirect2DWorkspaceFrameCacheBuilds}.");
        }

        composition = SceneCompositionBuilder.Build(
            compositionScene,
            definition,
            project.DrawingObjects,
            preRenderFrame,
            project.PlaybackFps);
        stage.SetSceneCompositionResult(
            composition,
            compositionScene,
            preserveWorkspaceFrameCache: true);
        stage.Frame = preRenderFrame;
        var preRenderedReplayWatch = Stopwatch.StartNew();
        stage.Update();
        Application.DoEvents();
        preRenderedReplayWatch.Stop();
        var preRenderCacheHit = stage.LastDirect2DWorkspaceFrameCacheHits;
        Console.WriteLine(
            $"collision_workspace_prerender frame={preRenderFrame} "
            + $"build_ms={preRenderWatch.Elapsed.TotalMilliseconds:0.000} "
            + $"replay_ms={preRenderedReplayWatch.Elapsed.TotalMilliseconds:0.000} "
            + $"cache_builds={stage.LastDirect2DWorkspaceFrameCacheBuilds} "
            + $"cache_hits={preRenderCacheHit} "
            + $"entries={stage.Direct2DWorkspaceFrameCacheEntryCount}");
        if (preRenderCacheHit != 1)
        {
            throw new InvalidOperationException(
                $"Workspace pre-render frame {preRenderFrame} was not hit on first presentation.");
        }

        if (Environment.GetEnvironmentVariable("VECTOR_BENCH_EDITOR_PLAYBACK") == "1")
        {
            RunEditorPlaybackPerformanceProbe(manifestPath, project);
        }
    }

    private static void RunEditorPlaybackPerformanceProbe(
        string manifestPath,
        VectorProject sourceProject)
    {
        ArgumentNullException.ThrowIfNull(sourceProject);

        if (decimal.TryParse(
                Environment.GetEnvironmentVariable("VECTOR_BENCH_EDITOR_FPS"),
                out var requestedFps)
            && requestedFps >= 1m)
        {
            // Configure the fixture before MainForm subscribes to the project.
            // Changing the settings panel after loading marks the document dirty
            // and makes the automated close path wait for a save decision.
            sourceProject.TrySetPlaybackFps(requestedFps);
        }

        var totalWatch = Stopwatch.StartNew();
        var initializationTimeout = TimeSpan.FromSeconds(30);
        var playbackTimeout = TimeSpan.FromSeconds(10);
        var totalTimeout = TimeSpan.FromSeconds(45);
        var samplingDuration = TimeSpan.FromSeconds(2);
        var cleanupGracePeriod = TimeSpan.FromMilliseconds(500);
        var outputSync = new object();
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellation = new CancellationTokenSource();
        MainForm? formForAbort = null;
        Exception? probeFailure = null;
        string currentStage = "not_started";
        long currentStageStartedAt = Stopwatch.GetTimestamp();
        long samplingStartedAt = currentStageStartedAt;
        var samplingPhase = 0;

        void WriteOutput(string line)
        {
            lock (outputSync)
            {
                Console.WriteLine(line);
                Console.Out.Flush();
            }
        }

        void EnterStage(string stage)
        {
            var startedAt = Stopwatch.GetTimestamp();
            Volatile.Write(ref currentStage, stage);
            Volatile.Write(ref currentStageStartedAt, startedAt);
            if (string.Equals(stage, "sampling", StringComparison.Ordinal))
            {
                Volatile.Write(ref samplingPhase, 1);
                Volatile.Write(ref samplingStartedAt, startedAt);
            }

            WriteOutput(
                $"editor_playback stage={stage} "
                + $"elapsed_ms={totalWatch.Elapsed.TotalMilliseconds:0.0}");
        }

        void RunStage(string stage, Action action)
        {
            EnterStage(stage);
            action();
            EnterStage($"{stage}_done");
        }

        InvalidOperationException FailureForStage(string stage, Exception exception)
        {
            var stageStartedAt = Volatile.Read(ref currentStageStartedAt);
            var stageMilliseconds = Stopwatch.GetElapsedTime(stageStartedAt).TotalMilliseconds;
            return new InvalidOperationException(
                $"Editor playback probe failed during {stage} after "
                + $"{totalWatch.Elapsed.TotalMilliseconds:0.0} ms "
                + $"(stage_ms={stageMilliseconds:0.0}).",
                exception);
        }

        void RequestFormClose()
        {
            try
            {
                var form = Volatile.Read(ref formForAbort);
                if (form is null || form.IsDisposed || !form.IsHandleCreated) return;
                form.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (!form.IsDisposed) form.Close();
                    }
                    catch
                    {
                        // The worker's finally block still attempts disposal.
                    }
                }));
            }
            catch (ObjectDisposedException)
            {
                // The worker may already be completing cleanup.
            }
            catch (InvalidOperationException)
            {
                // The form may be between handle creation and disposal.
            }
        }

        EnterStage("thread_start");
        var probeThread = new Thread(() =>
        {
            MainForm? form = null;
            string? successOutput = null;
            try
            {
                EnterStage("ui_thread_start");
                string sceneId = string.Empty;
                RunStage("mainform_construct", () =>
                {
                    form = new MainForm();
                    Volatile.Write(ref formForAbort, form);
                });

                RunStage("project_load", () =>
                {
                    var definition = sourceProject.Scenes.Single();
                    sceneId = definition.Id;
                    var load = typeof(MainForm).GetMethod(
                        "LoadProjectDocument",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                        ?? throw new InvalidOperationException(
                            "The editor playback probe could not locate project loading.");
                    load.Invoke(form, [sourceProject, manifestPath]);
                });

                WorkspaceTabs workspaceTabs = null!;
                RunStage("workspace_select", () =>
                {
                    workspaceTabs = FindControl<WorkspaceTabs>(form!)
                        ?? throw new InvalidOperationException(
                            "The editor playback probe could not locate workspace tabs.");
                    workspaceTabs.SelectView(WorkspaceView.SceneEditor);
                });

                RunStage("form_show", () =>
                {
                    form!.StartPosition = FormStartPosition.Manual;
                    form.Location = new Point(-30_000, -30_000);
                    form.Show();
                    Application.DoEvents();
                });

                StageControl stage = null!;
                RunStage("stage_discover", () =>
                {
                stage = FindControl<StageControl>(form!)
                        ?? throw new InvalidOperationException(
                            "The editor playback probe could not locate the StageControl.");
                });

                var rendered = 0;
                var distinctPresentedFrames = new HashSet<int>();
                var frameRenderSamples = new List<double>(128);
                var commandSamples = new List<double>(128);
                var presentSamples = new List<double>(128);
                var cpuRasterSamples = new List<double>(128);
                var playbackBackgroundSamples = new List<double>(128);
                var playbackBackgroundReadbackSamples = new List<double>(128);
                var playbackBitmapWriteSamples = new List<double>(128);
                var playbackBlitSamples = new List<double>(128);
                stage.FrameRendered += (_, _) =>
                {
                    frameRenderSamples.Add(stage.LastFrameRenderMilliseconds);
                    commandSamples.Add(stage.LastDirect2DCommandMilliseconds);
                    presentSamples.Add(stage.LastDirect2DPresentMilliseconds);
                    cpuRasterSamples.Add(stage.LastDirect2DReference3DCpuRasterMilliseconds);
                    playbackBackgroundSamples.Add(
                        stage.LastDirect2DReference3DPlaybackBackgroundMilliseconds);
                    playbackBackgroundReadbackSamples.Add(
                        stage.LastDirect2DReference3DPlaybackBackgroundReadbackMilliseconds);
                    playbackBitmapWriteSamples.Add(
                        stage.LastDirect2DReference3DPlaybackBitmapWriteMilliseconds);
                    playbackBlitSamples.Add(
                        stage.LastDirect2DReference3DPlaybackBlitMilliseconds);
                    var renderCount = Interlocked.Increment(ref rendered);
                    var presentedFrame = stage.LastDirect2DReference3DPlaybackPresentedFrame;
                    if (presentedFrame >= 0) distinctPresentedFrames.Add(presentedFrame);
                    if (Environment.GetEnvironmentVariable("VECTOR_BENCH_PLAYBACK_TRACE") == "1"
                        && renderCount <= 8)
                    {
                        WriteOutput(
                            $"editor_playback frame_rendered count={renderCount} "
                            + $"frame={stage.Frame} render_ms={stage.LastFrameRenderMilliseconds:0.0} "
                            + $"command_ms={stage.LastDirect2DCommandMilliseconds:0.0} "
                            + $"cpu_raster_ms={stage.LastDirect2DReference3DCpuRasterMilliseconds:0.0}");
                    }
                };
                PlaybackCompositionPreloadMetrics metricsAtStop = default;
                var preloadStatusAtStop = "unknown";
                long prefetchedFrameMatchesAtStop = 0;
                long prefetchedFrameMismatchesAtStop = 0;
                var prefetchedFrameLastMismatchAtStop = "unknown";
                long prefetchedFrameSetCountAtStop = 0;
                var prefetchedFrameLastSetTypeAtStop = "unknown";
                var prefetchedFrameLastSetCastAtStop = false;
                var prefetchedFrameMatchFailureAtStop = "unknown";
                var playbackGdiStatusAtStop = "unknown";
                var activeSamplingElapsed = TimeSpan.Zero;
                using var samplingTimer = new System.Windows.Forms.Timer
                {
                    Interval = (int)samplingDuration.TotalMilliseconds
                };
                samplingTimer.Tick += (_, _) =>
                {
                    samplingTimer.Stop();
                    activeSamplingElapsed = Stopwatch.GetElapsedTime(
                        Volatile.Read(ref samplingStartedAt));
                    if (Environment.GetEnvironmentVariable("VECTOR_BENCH_PLAYBACK_TRACE") == "1")
                    {
                        WriteOutput(
                            $"editor_playback sampling_timer rendered={Volatile.Read(ref rendered)} "
                            + $"active_elapsed_ms={activeSamplingElapsed.TotalMilliseconds:0.0} "
                            + $"frame={stage.Frame} "
                            + $"frame_avg_ms={Average(frameRenderSamples):0.0} "
                            + $"command_avg_ms={Average(commandSamples):0.0} "
                            + $"present_avg_ms={Average(presentSamples):0.0} "
                            + $"cpu_raster_avg_ms={Average(cpuRasterSamples):0.0} "
                            + $"parts_avg_ms={Average(playbackBackgroundSamples):0.0}/"
                            + $"{Average(playbackBackgroundReadbackSamples):0.0}/"
                            + $"{Average(playbackBitmapWriteSamples):0.0}/"
                            + $"{Average(playbackBlitSamples):0.0}");
                    }
                    metricsAtStop = form!.PlaybackCompositionMetrics;
                    preloadStatusAtStop = form.PlaybackCompositionPreloadStatus;
                    prefetchedFrameMatchesAtStop =
                        stage.Direct2DReference3DPlaybackRasterFrameMatchCount;
                    prefetchedFrameMismatchesAtStop =
                        stage.Direct2DReference3DPlaybackRasterFrameMismatchCount;
                    prefetchedFrameLastMismatchAtStop =
                        stage.Direct2DReference3DPlaybackRasterFrameLastMismatch;
                    prefetchedFrameSetCountAtStop =
                        stage.Direct2DReference3DPlaybackRasterFrameSetCount;
                    prefetchedFrameLastSetTypeAtStop =
                        stage.Direct2DReference3DPlaybackRasterFrameLastSetType;
                    prefetchedFrameLastSetCastAtStop =
                        stage.Direct2DReference3DPlaybackRasterFrameLastSetCast;
                    prefetchedFrameMatchFailureAtStop =
                        stage.LastDirect2DReference3DPlaybackRasterFrameMatchFailure;
                    playbackGdiStatusAtStop =
                        stage.LastDirect2DReference3DPlaybackGdiStatus;
                    if (!form!.IsDisposed)
                    {
                        form.Close();
                        if (!form.IsDisposed) Application.ExitThread();
                    }
                };
                RunStage("toggle_playback", () =>
                {
                    var toggle = typeof(MainForm).GetMethod(
                        "TogglePlayback",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                        ?? throw new InvalidOperationException(
                            "The editor playback probe could not locate playback control.");
                    toggle.Invoke(form, null);
                });

                rendered = 0;
                distinctPresentedFrames.Clear();
                frameRenderSamples.Clear();
                commandSamples.Clear();
                presentSamples.Clear();
                cpuRasterSamples.Clear();
                playbackBackgroundSamples.Clear();
                playbackBackgroundReadbackSamples.Clear();
                playbackBitmapWriteSamples.Clear();
                playbackBlitSamples.Clear();
                var samplingWatch = Stopwatch.StartNew();
                RunStage("sampling", () =>
                {
                    samplingTimer.Start();
                    Application.Run(form!);
                    samplingWatch.Stop();

                    if (Volatile.Read(ref rendered) <= 0)
                    {
                        throw new InvalidOperationException(
                            "The editor playback probe received no FrameRendered events.");
                    }
                });

                var metrics = metricsAtStop;
                var activeSamplingSeconds = activeSamplingElapsed.TotalSeconds > 0
                    ? activeSamplingElapsed.TotalSeconds
                    : samplingWatch.Elapsed.TotalSeconds;
                var shutdownMilliseconds = Math.Max(
                    0,
                    samplingWatch.Elapsed.TotalMilliseconds
                        - activeSamplingElapsed.TotalMilliseconds);
                var playbackItems = stage.GetReference3DSceneRenderItems();
                successOutput =
                    $"editor_playback elapsed_ms={samplingWatch.Elapsed.TotalMilliseconds:0.0} "
                    + $"active_elapsed_ms={activeSamplingElapsed.TotalMilliseconds:0.0} "
                    + $"shutdown_ms={shutdownMilliseconds:0.0} "
                    + $"renders={rendered} fps={rendered / Math.Max(0.001, activeSamplingSeconds):0.0} "
                    + $"distinct_frames={distinctPresentedFrames.Count} "
                    + $"distinct_fps={distinctPresentedFrames.Count / Math.Max(0.001, activeSamplingSeconds):0.0} "
                    + $"prefetch={metrics.AppliedFrames}/{metrics.MissedFrames}/{metrics.ReadyFrames} "
                    + $"built={metrics.BuiltFrames} restore_ms={metrics.SnapshotRestoreMilliseconds:0.0} "
                    + $"prefetched_raster={metrics.PrefetchedRasterFrames}/"
                    + $"{metrics.PrefetchedRasterMilliseconds:0.0} "
                    + $"dropped={metrics.DroppedFrames} "
                    + $"preload_build_ms={metrics.BuildMilliseconds:0.0}/{metrics.LastBuildMilliseconds:0.0}/"
                    + $"{metrics.LastBuiltFrame} "
                    + $"preload_stage_ms={metrics.CompositionMilliseconds:0.0}/"
                    + $"{metrics.RasterPreparationMilliseconds:0.0} "
                    + $"preload_raster_parts_ms={metrics.RenderPlanMilliseconds:0.0}/"
                    + $"{metrics.CommandPreparationMilliseconds:0.0} "
                    + $"preload_active={form!.PlaybackCompositionPreloadActive} "
                    + $"preload_status={preloadStatusAtStop} "
                    + $"frame_avg_ms={Average(frameRenderSamples):0.0} "
                    + $"command_avg_ms={Average(commandSamples):0.0} "
                    + $"present_avg_ms={Average(presentSamples):0.0} "
                    + $"cpu_raster_avg_ms={Average(cpuRasterSamples):0.0} "
                    + $"playback_parts_avg_ms={Average(playbackBackgroundSamples):0.0}/"
                    + $"{Average(playbackBackgroundReadbackSamples):0.0}/"
                    + $"{Average(playbackBitmapWriteSamples):0.0}/"
                    + $"{Average(playbackBlitSamples):0.0} "
                    + $"cpu_raster_reason={stage.LastDirect2DReference3DCpuRasterFallbackReason} "
                    + $"gdi_raster_failure={stage.LastDirect2DReference3DPlaybackGdiRasterFailure} "
                    + $"playback_gdi={playbackGdiStatusAtStop} "
                    + $"frame_match_failure={prefetchedFrameMatchFailureAtStop} "
                    + $"prefetched_match={prefetchedFrameMatchesAtStop}/"
                    + $"{prefetchedFrameMismatchesAtStop}/{prefetchedFrameLastMismatchAtStop} "
                    + $"frame_set={prefetchedFrameSetCountAtStop}/"
                    + $"{prefetchedFrameLastSetTypeAtStop}/{prefetchedFrameLastSetCastAtStop} "
                    + $"stage_ms={stage.LastFrameRenderMilliseconds:0.0} "
                    + $"command_ms={stage.LastDirect2DCommandMilliseconds:0.0} "
                    + $"present_ms={stage.LastDirect2DPresentMilliseconds:0.0} "
                    + $"playback_parts_ms={stage.LastDirect2DReference3DPlaybackBackgroundMilliseconds:0.0}/"
                    + $"{stage.LastDirect2DReference3DPlaybackBackgroundReadbackMilliseconds:0.0}/"
                    + $"{stage.LastDirect2DReference3DPlaybackBitmapWriteMilliseconds:0.0}/"
                    + $"{stage.LastDirect2DReference3DPlaybackBlitMilliseconds:0.0} "
                    + $"plan_builds={stage.Reference3DRenderPlanBuildCount} "
                    + $"cpu_raster_ms={stage.LastDirect2DReference3DCpuRasterMilliseconds:0.0} "
                    + $"viewport={stage.Width}x{stage.Height} "
                    + $"dimension={stage.ReferenceDimension} "
                    + $"mask_clips={stage.SceneCompositionMaskClips.Count} "
                    + $"items={playbackItems.Length} "
                    + $"item_optical={playbackItems.Count(item => item.OpticalResponse != Reference3DOpticalResponse.Identity)} "
                    + $"item_local_light={playbackItems.Count(item => item.LocalLightLayers is { Length: > 0 })} "
                    + $"item_shadow={playbackItems.Count(item => item.ShadowLayers is { Length: > 0 })} "
                    + $"item_surface={playbackItems.Count(item => item.OpticalSurface is not null)} "
                    + $"item_fragment={playbackItems.Count(item => item.FragmentClip is not null)} "
                    + $"item_occlusion={playbackItems.Count(item => item.OcclusionContours is not null)} "
                    + $"item_secondary={playbackItems.Count(item => item.SecondaryObjectIndex >= 0)} "
                    + $"underlay_objects={stage.UnderlayScene?.ObjectCount ?? 0} "
                    + $"onion_objects={stage.OnionSkinScene?.ObjectCount ?? 0} "
                    + $"drag_preview_objects={stage.DragPreviewScene?.ObjectCount ?? 0} "
                    + $"scene_non_normal_blend={stage.Scene.HasNonNormalLayerBlendModes} "
                    + $"scene_distortion={stage.SceneHasDistortionsForRendering(stage.Scene)} "
                    + $"shape_unsupported={playbackItems.Count(item =>
                        item.Kind is Reference3DRenderKind.FrontFill or Reference3DRenderKind.FrontStroke
                        && stage.Scene.ShapeKind[item.ObjectIndex] is
                            ShapeKind.Line or ShapeKind.Freeform or ShapeKind.BrushStroke
                                or ShapeKind.ImportedSvg or ShapeKind.Text or ShapeKind.MixingStroke)} "
                    + $"path_bezier={playbackItems.Count(item =>
                        item.Kind is Reference3DRenderKind.FrontFill or Reference3DRenderKind.FrontStroke
                        && stage.Scene.ShapeKind[item.ObjectIndex] == ShapeKind.Path
                        && stage.Scene.TryGetPathBezierLocalContours(item.ObjectIndex, out var contours)
                        && contours.Length > 0)} "
                    + $"layer_non_drawing={playbackItems.Count(item =>
                        (uint)item.LayerIndex >= stage.Scene.LayerCount
                        || stage.Scene.GetLayerKind(item.LayerIndex) != DrawingLayerKind.Drawing)} "
                    + $"stage_frame={stage.Frame} scene={sceneId}";
            }
            catch (Exception exception)
            {
                Volatile.Write(
                    ref probeFailure,
                    FailureForStage(Volatile.Read(ref currentStage) ?? "unknown", exception));
            }
            finally
            {
                EnterStage("cleanup");
                if (form is not null)
                {
                    try
                    {
                        if (!form.IsDisposed) form.Close();
                    }
                    catch (Exception exception)
                    {
                        if (Volatile.Read(ref probeFailure) is null)
                        {
                            Volatile.Write(ref probeFailure, FailureForStage("cleanup", exception));
                        }
                    }

                    try
                    {
                        form.Dispose();
                    }
                    catch (Exception exception)
                    {
                        if (Volatile.Read(ref probeFailure) is null)
                        {
                            Volatile.Write(ref probeFailure, FailureForStage("cleanup", exception));
                        }
                    }
                }

                Volatile.Write(ref formForAbort, null);
                EnterStage("cleanup_done");
                if (Volatile.Read(ref probeFailure) is null && successOutput is not null)
                {
                    WriteOutput(successOutput);
                }

                completion.TrySetResult(Volatile.Read(ref probeFailure) is null);
            }
        })
        {
            IsBackground = true,
            Name = "Editor playback benchmark probe"
        };

        try
        {
            probeThread.SetApartmentState(ApartmentState.STA);
            probeThread.Start();
        }
        catch (Exception exception)
        {
            cancellation.Cancel();
            cancellation.Dispose();
            throw FailureForStage("thread_start", exception);
        }

        while (!completion.Task.Wait(TimeSpan.FromMilliseconds(50)))
        {
            var phase = Volatile.Read(ref samplingPhase);
            var phaseStartedAt = phase == 0
                ? totalWatch.Elapsed
                : Stopwatch.GetElapsedTime(Volatile.Read(ref samplingStartedAt));
            var phaseTimeout = phase == 0 ? initializationTimeout : playbackTimeout;
            var totalElapsed = totalWatch.Elapsed;
            if (phaseStartedAt < phaseTimeout && totalElapsed < totalTimeout) continue;
            if (completion.Task.IsCompleted) break;

            var stage = Volatile.Read(ref currentStage) ?? "unknown";
            var stageMilliseconds = Stopwatch.GetElapsedTime(
                Volatile.Read(ref currentStageStartedAt)).TotalMilliseconds;
            WriteOutput(
                $"editor_playback stage=timeout current={stage} "
                + $"elapsed_ms={totalElapsed.TotalMilliseconds:0.0} "
                + $"stage_ms={stageMilliseconds:0.0}");
            cancellation.Cancel();
            RequestFormClose();
            if (completion.Task.Wait(cleanupGracePeriod)) cancellation.Dispose();
            throw new InvalidOperationException(
                $"Editor playback probe timed out during {stage} after "
                + $"{totalElapsed.TotalMilliseconds:0.0} ms "
                + $"(stage_ms={stageMilliseconds:0.0}).");
        }

        cancellation.Dispose();
        if (Volatile.Read(ref probeFailure) is { } failure) throw failure;

        static T? FindControl<T>(Control root)
            where T : Control
        {
            if (root is T match) return match;
            foreach (Control child in root.Controls)
            {
                var result = FindControl<T>(child);
                if (result is not null) return result;
            }

            return null;
        }
    }

    private static void RenderCollisionFrame(
        StageControl stage,
        int frame,
        SceneCompositionResult composition)
    {
        stage.SetSceneCompositionResult(composition, stage.Scene);
        stage.Frame = frame;
        stage.Invalidate();
        stage.Update();
        Application.DoEvents();
    }

    private static double Average(IReadOnlyList<double> samples)
    {
        return samples.Count == 0 ? 0 : samples.Average();
    }

    private static string GradientProfile(VectorScene scene, int objectIndex)
    {
        return $"{scene.GetGradientKind(objectIndex)}|"
            + string.Join(
                ";",
                scene.GetGradientStops(objectIndex)
                    .Select(stop => $"{stop.Position:R}:{stop.Argb}"));
    }

    private static bool TryGetProjectedBounds(
        Reference3DRenderItem item,
        out RectangleF bounds)
    {
        var left = float.PositiveInfinity;
        var top = float.PositiveInfinity;
        var right = float.NegativeInfinity;
        var bottom = float.NegativeInfinity;
        foreach (var contour in item.Contours)
        {
            foreach (var point in contour.Points)
            {
                left = Math.Min(left, point.X);
                top = Math.Min(top, point.Y);
                right = Math.Max(right, point.X);
                bottom = Math.Max(bottom, point.Y);
            }
        }

        bounds = RectangleF.Empty;
        if (!float.IsFinite(left)
            || !float.IsFinite(top)
            || !float.IsFinite(right)
            || !float.IsFinite(bottom))
        {
            return false;
        }

        bounds = RectangleF.FromLTRB(left, top, right, bottom);
        return true;
    }
}
