using System.Diagnostics;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    // 10,000 selected objects must keep navigation interactive: 10 ms is 100 FPS
    // and leaves the remaining budget for input, overlays, and present.
    internal const double LargeSelectionFrameBudgetMilliseconds = 10.0;

    // A single scheduler or GC spike among a hundred frames is not a stutter the user
    // can perceive, so the worst-frame guard is looser than the average budget while
    // still catching a real regression: 12.5 ms is 80 FPS.
    internal const double LargeSelectionFrameSpikeBudgetMilliseconds = 12.5;

    private const int LargeSelectionObjectCount = 10_000;
    private const int OverlappingSelectionObjectCount = 10_000;
    private const double OverlappingSelectionBuildBudgetMilliseconds = 2_000;
    // In an extreme stack of overlapping objects the remaining frame cost is the crisp
    // detail pass that redraws the selected objects over the tile mosaic; the highlight
    // overlay itself replays in microseconds. Ten milliseconds of added cost keeps this
    // deliberately pathological scene above 80 FPS.
    private const double OverlappingSelectionHighlightBudgetMilliseconds = 10.0;
    private const double OverlappingSelectionHighlightSpikeBudgetMilliseconds = 13.0;
    private const int LargeSelectionVertices = 16;
    private const float LargeSelectionSpacing = 420f;

    // An imported artwork broken apart keeps its folder layers, so it carries display layer
    // effects that used to force the per-object pass at every frame. These objects are the
    // shape of that class: a thousands large, curve-heavy scene whose pan frames must stay
    // above 60 FPS while the camera moves.
    private const int ComplexArtworkObjectCount = 1_114;
    private const int ComplexArtworkVertices = 52;
    private const double ComplexArtworkPanBudgetMilliseconds = 10.0;
    private const double ComplexArtworkMovingCommandBudgetMilliseconds = 2.0;
    private const double ComplexArtworkOverlayBudgetMilliseconds = 1.0;
    private const double LargeMarqueeQueryBudgetMilliseconds = 250.0;

    /// <summary>
    /// Selection highlight cost is derived from the number of selected objects, not
    /// from the scene size. A ten-thousand object selection must still build once
    /// and then replay while the camera moves, so this fixture measures the cold
    /// build plus pan, interactive zoom, and drag frames on every backend change.
    /// </summary>
    internal static void RunLargeSelectionFrameRateRegression()
    {
        var previousGpuPolicy = Environment.GetEnvironmentVariable("VECTOR_DISABLE_EXPLICIT_GPU");
        try
        {
            Environment.SetEnvironmentVariable("VECTOR_DISABLE_EXPLICIT_GPU", null);
            RunLargeSelectionFrameRateRegressionCore();
            Console.Out.Flush();
            RunOverlappingSelectionBuildRegressionCore();
            Console.Out.Flush();
            RunComplexArtworkInteractionLodRegression();
            RunLargeMarqueeSelectionRegression();
            Console.Out.Flush();
        }
        finally { Environment.SetEnvironmentVariable("VECTOR_DISABLE_EXPLICIT_GPU", previousGpuPolicy); }
    }

    /// <summary>
    /// Overlapping selections are the pathological shape for highlight occlusion: every
    /// selected object sits under hundreds of opaque fills. Resolving each selected
    /// outline against every covering fill used to cost seconds, so this fixture pins the
    /// cold selection build and the pan frames right after it.
    /// </summary>
    /// <summary>
    /// Repaints the stage once and fails when the repaint was skipped, so a coalesced
    /// invalidation can never be reported as an impossible frame time.
    /// </summary>
    private static void PaintStage(StageControl stage)
    {
        var frames = stage.RenderedFrameCount;
        stage.Refresh();
        if (stage.RenderedFrameCount != frames) return;
        stage.Invalidate();
        stage.Update();
        AssertTimeline(stage.RenderedFrameCount != frames, "The benchmark frame did not paint the stage.");
    }

    private static void RunOverlappingSelectionBuildRegressionCore()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        var owners = new int[OverlappingSelectionObjectCount];
        var hits = new List<DrawingElementHit>(OverlappingSelectionObjectCount * 2);
        scene.BeginDeferredAppend(OverlappingSelectionObjectCount, [0]);
        try
        {
            for (var index = 0; index < OverlappingSelectionObjectCount; index++)
            {
                var center = new PointF(index % 100 * 24f - 1_200, index / 100 * 24f - 1_200);
                var contour = new PointF[LargeSelectionVertices];
                for (var vertex = 0; vertex < LargeSelectionVertices; vertex++)
                {
                    contour[vertex] = new PointF(
                        center.X + 180 * MathF.Cos(vertex * MathF.Tau / LargeSelectionVertices),
                        center.Y + 180 * MathF.Sin(vertex * MathF.Tau / LargeSelectionVertices));
                }

                var owner = scene.AppendPathObjectContours(0, [contour], 2, Color.SlateGray, Color.Black, LargeSelectionVertices);
                owners[index] = owner;
                hits.Add(new DrawingElementHit(new DrawingElementKey(owner, DrawingElementKind.Fill, 0), 0, 0, 1));
                hits.Add(new DrawingElementHit(new DrawingElementKey(owner, DrawingElementKind.BoundaryStroke, 0), 0, 0, 1));
            }
        }
        finally
        {
            scene.EndDeferredAppend();
        }

        scene.CompleteDeferredBuild();
        using var host = new Form { ClientSize = new Size(1280, 800), ShowInTaskbar = false };
        using var stage = new StageControl(scene) { Dock = DockStyle.Fill };
        host.Controls.Add(stage);
        host.Show();
        // Wide enough that the scene itself renders through the tile LOD, so the frame
        // cost measured here belongs to the selection overlay and not to 2,000 detail
        // object draws.
        stage.SetVisibleWorldWidth(12_000);
        Application.DoEvents();

        var renderer = (Direct2DStageRenderer)RequireField(typeof(StageControl), "_direct2DRenderer").GetValue(stage)!;
        stage.Capture = true;
        try
        {
            // Same scene and view without a selection: the clustered tile LOD, not the
            // highlight, is what an extreme stack of objects costs to draw, so the
            // highlight regression is measured as the delta over this baseline.
            var basePanTimings = new List<double>(24);
            for (var frame = 0; frame < 24; frame++)
            {
                var started = Stopwatch.GetTimestamp();
                stage.Pan(frame % 2 == 0 ? 2 : -2, 0);
                PaintStage(stage);
                basePanTimings.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }

            basePanTimings.Sort();
            var basePanAverage = basePanTimings.Average();
            var basePanP95 = basePanTimings[(int)Math.Ceiling(basePanTimings.Count * 0.95) - 1];
            var buildStarted = Stopwatch.GetTimestamp();
            stage.SetSelectionState(owners, owners[^1], hits, hits[^1]);
            stage.Refresh();
            var buildMilliseconds = Stopwatch.GetElapsedTime(buildStarted).TotalMilliseconds;
            var panTimings = new List<double>(32);
            var panCommandMilliseconds = 0d;
            var panPresentMilliseconds = 0d;
            for (var frame = 0; frame < 32; frame++)
            {
                var started = Stopwatch.GetTimestamp();
                stage.Pan(frame % 2 == 0 ? 2 : -2, 0);
                PaintStage(stage);
                panTimings.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                panCommandMilliseconds += renderer.LastCommandMilliseconds;
                panPresentMilliseconds += renderer.LastPresentMilliseconds;
            }

            panTimings.Sort();
            var panAverage = panTimings.Average();
            var panP95 = panTimings[(int)Math.Ceiling(panTimings.Count * 0.95) - 1];
            var panFps = 1000d / panAverage;
            Console.WriteLine(
                $"overlapping_selection_build_ms={buildMilliseconds:F3},"
                + $"base_avg_ms={basePanAverage:F3},base_p95_ms={basePanP95:F3},"
                + $"pan_avg_ms={panAverage:F3},pan_p95_ms={panP95:F3},pan_fps={panFps:F1},"
                + $"objects={scene.ObjectCount},d2d={stage.LastFrameUsedDirect2D},"
                + $"path={stage.LastDirect2DSceneDrawPath},tiles={stage.LastStats.TileDraws},"
                + $"command_ms={panCommandMilliseconds / 32:F3},present_ms={panPresentMilliseconds / 32:F3},detail={stage.LastDirect2DLodDetailObjectDraws},"
                + $"detail_ms={stage.LastDirect2DLodDetailMilliseconds:F3},selection_ms={stage.LastDirect2DSelectionMilliseconds:F3},"
                + $"commands={renderer.SelectionCommandBuilds}/{renderer.SelectionCommandReuses}");
            AssertTimeline(scene.ObjectCount == OverlappingSelectionObjectCount,
                $"The overlapping selection fixture built {scene.ObjectCount} of {OverlappingSelectionObjectCount} objects.");
            AssertTimeline(stage.LastFrameUsedDirect2D, "The overlapping selection fixture did not use the Direct2D backend.");
            AssertTimeline(buildMilliseconds <= OverlappingSelectionBuildBudgetMilliseconds,
                $"Building the overlapping {OverlappingSelectionObjectCount} object selection took {buildMilliseconds:F1} ms.");
            AssertTimeline(panAverage - basePanAverage <= OverlappingSelectionHighlightBudgetMilliseconds,
                $"The overlapping selection highlight added {panAverage - basePanAverage:F2} ms per frame "
                + $"({basePanAverage:F2} ms without a selection, {panAverage:F2} ms with one).");
            AssertTimeline(panP95 - basePanP95 <= OverlappingSelectionHighlightSpikeBudgetMilliseconds,
                $"The overlapping selection highlight added {panP95 - basePanP95:F2} ms at p95.");
        }
        finally
        {
            stage.ClearSelectionDragPreview();
            stage.Capture = false;
            host.Close();
        }

        Console.WriteLine("overlapping_selection_build=ok");
    }

    /// <summary>
    /// An imported artwork broken apart into a thousand curve-heavy paths carries folder
    /// layers, so it kept the exact object pass at every frame and panned at roughly
    /// 25 FPS with nothing selected. This fixture pins the interaction LOD for that class:
    /// moving frames use the stage mosaic, the settled frame restores the exact objects,
    /// and a full selection replays instead of rebuilding.
    /// </summary>
    private static void RunComplexArtworkInteractionLodRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        var owners = new int[ComplexArtworkObjectCount];
        var hits = new List<DrawingElementHit>(ComplexArtworkObjectCount * 2);
        scene.BeginDeferredAppend(ComplexArtworkObjectCount, [0]);
        try
        {
            for (var index = 0; index < ComplexArtworkObjectCount; index++)
            {
                var center = new PointF(index % 40 * 320f - 6_400, index / 40 * 260f - 3_600);
                var contour = new PointF[ComplexArtworkVertices];
                for (var vertex = 0; vertex < ComplexArtworkVertices; vertex++)
                {
                    var radius = vertex % 2 == 0 ? 130f : 96f;
                    contour[vertex] = new PointF(
                        center.X + radius * MathF.Cos(vertex * MathF.Tau / ComplexArtworkVertices),
                        center.Y + radius * MathF.Sin(vertex * MathF.Tau / ComplexArtworkVertices));
                }

                var owner = scene.AppendPathObjectContours(0, [contour], 2, Color.SlateGray, Color.Black, ComplexArtworkVertices);
                owners[index] = owner;
                hits.Add(new DrawingElementHit(new DrawingElementKey(owner, DrawingElementKind.Fill, 0), 0, 0, 1));
            }
        }
        finally
        {
            scene.EndDeferredAppend();
        }

        // A folder layer is structural: it changes layer structure but not a single pixel,
        // exactly like the folder layers Break Apart leaves on an imported artwork.
        scene.AddFolderLayer("Artwork");
        scene.CompleteDeferredBuild();
        AssertTimeline(scene.HasDisplayLayerEffects && !scene.HasCompositeLayerEffects,
            "The complex artwork fixture did not keep a structural-only layer effect.");
        using var host = new Form { ClientSize = new Size(1920, 1080), ShowInTaskbar = false };
        using var stage = new StageControl(scene) { Dock = DockStyle.Fill };
        host.Controls.Add(stage);
        host.Show();
        stage.SetVisibleWorldWidth(40_000);
        Application.DoEvents();
        var renderer = (Direct2DStageRenderer)RequireField(typeof(StageControl), "_direct2DRenderer").GetValue(stage)!;
        stage.Capture = true;
        try
        {
            // Baseline: the same camera move without a selection, so the numbers below
            // attribute the frame cost to the overlay instead of to the mosaic blit.
            for (var baseWarmup = 0; baseWarmup < 4; baseWarmup++)
            {
                stage.Pan(2, 0);
                PaintStage(stage);
            }

            var baseTimings = new List<double>(16);
            var basePresent = 0d;
            for (var baseFrame = 0; baseFrame < 16; baseFrame++)
            {
                var baseStarted = Stopwatch.GetTimestamp();
                stage.Pan(baseFrame % 2 == 0 ? 3 : -3, 0);
                PaintStage(stage);
                baseTimings.Add(Stopwatch.GetElapsedTime(baseStarted).TotalMilliseconds);
                basePresent += renderer.LastPresentMilliseconds;
            }

            baseTimings.Sort();
            var baseAverage = baseTimings.Average();
            Console.WriteLine(
                $"complex_artwork_base_pan:avg_ms={baseAverage:F3},"
                + $"present_ms={basePresent / 16:F3},path={stage.LastDirect2DSceneDrawPath}");
            Console.Out.Flush();
            var buildStarted = Stopwatch.GetTimestamp();
            stage.SetSelectionState(owners, owners[^1], hits, hits[^1]);
            PaintStage(stage);
            var buildMilliseconds = Stopwatch.GetElapsedTime(buildStarted).TotalMilliseconds;
            for (var warmup = 0; warmup < 4; warmup++)
            {
                stage.Pan(2, 0);
                PaintStage(stage);
            }

            var panTimings = new List<double>(32);
            var lodPaths = new HashSet<string>();
            var panCommand = 0d;
            var panPresent = 0d;
            var panSelection = 0d;
            for (var frame = 0; frame < 32; frame++)
            {
                var started = Stopwatch.GetTimestamp();
                stage.Pan(frame % 2 == 0 ? 3 : -3, 0);
                PaintStage(stage);
                panTimings.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                lodPaths.Add(stage.LastDirect2DSceneDrawPath);
                panCommand += renderer.LastCommandMilliseconds;
                panPresent += renderer.LastPresentMilliseconds;
                panSelection += stage.LastDirect2DSelectionMilliseconds;
            }

            panTimings.Sort();
            var panAverage = panTimings.Average();
            var panP95 = panTimings[(int)Math.Ceiling(panTimings.Count * 0.95) - 1];
            var panTiles = stage.LastStats.TileDraws;
            var commandBuilds = renderer.SelectionCommandBuilds;
            var commandReuses = renderer.SelectionCommandReuses;
            // The first frame after the last pan still counts as moving; the second sees an
            // unchanged camera and must draw the exact objects again.
            PaintStage(stage);
            var firstSettledPath = stage.LastDirect2DSceneDrawPath;
            PaintStage(stage);
            var settledPath = stage.LastDirect2DSceneDrawPath;
            var settledDrawn = stage.LastStats.DrawnObjects;
            Console.WriteLine(
                $"complex_artwork_settle:first={firstSettledPath},second={settledPath},"
                + $"drawn={settledDrawn},motion={stage.CameraInMotion}");
            Console.WriteLine(
                $"complex_artwork_build_ms={buildMilliseconds:F1},pan_avg_ms={panAverage:F3},pan_p95_ms={panP95:F3},"
                + $"pan_fps={1000 / panAverage:F1},moving_paths={string.Join("|", lodPaths)},tiles={panTiles},"
                + $"settled_path={settledPath},settled_drawn={settledDrawn},objects={scene.ObjectCount},"
                + $"d2d={stage.LastFrameUsedDirect2D},commands={commandBuilds}/{commandReuses},"
                + $"command_ms={panCommand / 32:F3},present_ms={panPresent / 32:F3},"
                + $"selection_ms={panSelection / 32:F3}");
            AssertTimeline(scene.ObjectCount == ComplexArtworkObjectCount,
                $"The complex artwork fixture built {scene.ObjectCount} of {ComplexArtworkObjectCount} objects.");
            AssertTimeline(stage.LastFrameUsedDirect2D, "The complex artwork fixture did not use the Direct2D backend.");
            AssertTimeline(buildMilliseconds <= OverlappingSelectionBuildBudgetMilliseconds,
                $"Building the complex artwork selection took {buildMilliseconds:F1} ms.");
            AssertTimeline(lodPaths.SetEquals(["interaction-tiles"]),
                $"Moving frames drew the complex artwork through {string.Join("|", lodPaths)} instead of the interaction LOD.");
            AssertTimeline(panTiles > 0, "The interaction LOD drew no tiles for the complex artwork.");
            // The interaction LOD must remove the per-object scene work from moving frames.
            // The overlay's own GPU cost is pinned by the overlapping fixture, so this one
            // asserts the scene pass and the recording side, which do not depend on the
            // compositor the benchmark happens to run behind.
            AssertTimeline(baseAverage <= ComplexArtworkPanBudgetMilliseconds,
                $"The complex artwork moved {baseAverage:F2} ms per frame at baseline.");
            AssertTimeline(panCommand / 32 <= ComplexArtworkMovingCommandBudgetMilliseconds,
                $"Moving frames recorded {panCommand / 32:F3} ms of scene commands with the selection.");
            AssertTimeline(panSelection / 32 <= ComplexArtworkOverlayBudgetMilliseconds,
                $"The complex artwork overlay recorded {panSelection / 32:F3} ms per frame.");
            AssertTimeline(commandReuses > 16 && commandBuilds <= 2,
                $"The complex artwork selection rebuilt {commandBuilds} times across 36 pan frames ({commandReuses} replays).");
            AssertTimeline(settledPath == "detail-objects" && settledDrawn >= ComplexArtworkObjectCount / 2,
                $"The settled frame kept the interaction LOD ({settledPath}, {settledDrawn} objects).");
            Console.WriteLine(
                $"complex_artwork_pan_avg_ms={panAverage:F3},p95_ms={panP95:F3},"
                + $"fps={1000 / panAverage:F1},present_ms={panPresent / 32:F3}");
        }
        finally
        {
            stage.Capture = false;
            host.Close();
        }

        Console.WriteLine("complex_artwork_interaction_lod=ok");
    }

    /// <summary>
    /// A rectangle drawn across a traced artwork used to answer with one element hit per
    /// boundary segment, which took ~2 s to build and carried tens of thousands of hits
    /// through the selection UI before anything was highlighted.
    /// </summary>
    private static void RunLargeMarqueeSelectionRegression()
    {
        var scene = BuildComplexArtworkScene(ComplexArtworkObjectCount);
        var marquee = new RectangleF(-20_000, -20_000, 40_000, 40_000);
        var started = Stopwatch.GetTimestamp();
        var hits = scene.QueryDrawingElementsInsideBounds(marquee, 0);
        var milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var hitOwners = hits.Select(hit => hit.Key.ObjectIndex).Distinct().Count();
        Console.WriteLine(
            $"large_marquee:query_ms={milliseconds:F1},hits={hits.Length},owners={hitOwners},"
            + $"objects={scene.ObjectCount}");
        AssertTimeline(hitOwners == scene.ObjectCount,
            $"The marquee selected {hitOwners} of {scene.ObjectCount} objects.");
        AssertTimeline(hits.Length <= scene.ObjectCount * 4,
            $"The marquee produced {hits.Length} element hits for {scene.ObjectCount} objects.");
        AssertTimeline(milliseconds <= LargeMarqueeQueryBudgetMilliseconds,
            $"The large marquee query took {milliseconds:F1} ms.");

        // Below the whole-element limit the marquee must keep per-segment precision.
        var small = BuildComplexArtworkScene(40);
        var smallHits = small.QueryDrawingElementsInsideBounds(marquee, 0);
        AssertTimeline(smallHits.Length > 40 * 8,
            $"A 40 object marquee collapsed to {smallHits.Length} element hits.");
        Console.WriteLine($"large_marquee_small_scene_hits={smallHits.Length}");
        Console.WriteLine("large_marquee_selection=ok");
    }

    private static VectorScene BuildComplexArtworkScene(int objectCount)
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        scene.BeginDeferredAppend(objectCount, [0]);
        try
        {
            for (var index = 0; index < objectCount; index++)
            {
                var center = new PointF(index % 40 * 320f - 6_400, index / 40 * 260f - 3_600);
                var contour = new PointF[ComplexArtworkVertices];
                for (var vertex = 0; vertex < ComplexArtworkVertices; vertex++)
                {
                    var radius = vertex % 2 == 0 ? 130f : 96f;
                    contour[vertex] = new PointF(
                        center.X + radius * MathF.Cos(vertex * MathF.Tau / ComplexArtworkVertices),
                        center.Y + radius * MathF.Sin(vertex * MathF.Tau / ComplexArtworkVertices));
                }

                scene.AppendPathObjectContours(0, [contour], 2, Color.SlateGray, Color.Black, ComplexArtworkVertices);
            }
        }
        finally
        {
            scene.EndDeferredAppend();
        }

        scene.AddFolderLayer("Artwork");
        scene.CompleteDeferredBuild();
        return scene;
    }

    private static void RunLargeSelectionFrameRateRegressionCore()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        var owners = new int[LargeSelectionObjectCount];
        var hits = new List<DrawingElementHit>(LargeSelectionObjectCount * 2);
        var span = LargeSelectionSpacing * 100;
        scene.BeginDeferredAppend(LargeSelectionObjectCount, [0]);
        try
        {
            for (var index = 0; index < LargeSelectionObjectCount; index++)
            {
                var center = new PointF(
                    index % 100 * LargeSelectionSpacing - span * 0.5f,
                    index / 100 * LargeSelectionSpacing - span * 0.5f);
                var contour = new PointF[LargeSelectionVertices];
                for (var vertex = 0; vertex < LargeSelectionVertices; vertex++)
                {
                    contour[vertex] = new PointF(
                        center.X + 180 * MathF.Cos(vertex * MathF.Tau / LargeSelectionVertices),
                        center.Y + 180 * MathF.Sin(vertex * MathF.Tau / LargeSelectionVertices));
                }

                var owner = scene.AppendPathObjectContours(0, [contour], 2, Color.SlateGray, Color.Black, LargeSelectionVertices);
                owners[index] = owner;
                hits.Add(new DrawingElementHit(new DrawingElementKey(owner, DrawingElementKind.Fill, 0), 0, 0, 1));
                hits.Add(new DrawingElementHit(new DrawingElementKey(owner, DrawingElementKind.BoundaryStroke, 0), 0, 0, 1));
            }
        }
        finally
        {
            scene.EndDeferredAppend();
        }

        scene.CompleteDeferredBuild();
        if (scene.ObjectCount != LargeSelectionObjectCount)
        {
            throw new InvalidOperationException($"The large selection fixture built {scene.ObjectCount} of {LargeSelectionObjectCount} objects.");
        }

        using var host = new Form { ClientSize = new Size(1920, 1080), ShowInTaskbar = false };
        using var stage = new StageControl(scene) { Dock = DockStyle.Fill };
        host.Controls.Add(stage);
        host.Show();
        stage.SetVisibleWorldWidth(span * 1.1f);
        Application.DoEvents();

        var renderer = (Direct2DStageRenderer)RequireField(typeof(StageControl), "_direct2DRenderer").GetValue(stage)!;
        var basePan = MeasurePan("base");
        var buildStarted = Stopwatch.GetTimestamp();
        stage.SetSelectionState(owners, owners[^1], hits, hits[^1]);
        stage.Refresh();
        Application.DoEvents();
        var buildMilliseconds = Stopwatch.GetElapsedTime(buildStarted).TotalMilliseconds;
        Console.WriteLine(
            $"large_selection_build_ms={buildMilliseconds:F3},objects={scene.ObjectCount},"
            + $"d2d={stage.LastFrameUsedDirect2D},gpu={renderer.GpuAdapterName}");

        var panFit = MeasurePan("fit");
        stage.SetVisibleWorldWidth(12_000);
        stage.Refresh();
        Application.DoEvents();
        var panDense = MeasurePan("dense");
        var drag = MeasureDrag();
        var zoom = MeasureZoom();

        Console.WriteLine(
            $"large_selection_fps:build={1000d / Math.Max(buildMilliseconds, 0.001):F1},"
            + $"pan_base={basePan.Fps:F1},pan_fit={panFit.Fps:F1},pan_dense={panDense.Fps:F1},"
            + $"zoom={zoom.Fps:F1},drag={drag.Fps:F1}");
        var budgetMet = buildMilliseconds <= 250
            && MeetsBudget(panFit)
            && MeetsBudget(panDense)
            && MeetsBudget(zoom)
            && MeetsBudget(drag);
        Console.WriteLine(
            $"large_selection_budget_ms={LargeSelectionFrameBudgetMilliseconds:F1},"
            + $"spike_ms={LargeSelectionFrameSpikeBudgetMilliseconds:F1},"
            + $"met={budgetMet.ToString().ToLowerInvariant()}");
        AssertTimeline(stage.LastFrameUsedDirect2D, "The large selection fixture did not use the Direct2D backend.");
        AssertTimeline(buildMilliseconds <= 250,
            $"Building the 10,000 object selection highlight took {buildMilliseconds:F1} ms.");
        AssertFrameBudget("Panning a 10,000 object selection in the fit view", panFit);
        AssertFrameBudget("Panning a 10,000 object selection in a dense view", panDense);
        AssertFrameBudget("Interactive zoom on a 10,000 object selection", zoom);
        AssertFrameBudget("Dragging a 10,000 object selection", drag);
        Console.WriteLine("large_selection_frame_rate=ok");
        host.Close();

        static bool MeetsBudget((double Average, double P95, double Max, double Fps) measured)
        {
            return measured.Average <= LargeSelectionFrameBudgetMilliseconds
                && measured.P95 <= LargeSelectionFrameSpikeBudgetMilliseconds;
        }

        static void AssertFrameBudget(string action, (double Average, double P95, double Max, double Fps) measured)
        {
            AssertTimeline(measured.Average <= LargeSelectionFrameBudgetMilliseconds,
                $"{action} took {measured.Average:F2} ms per frame ({measured.Fps:F1} FPS).");
            AssertTimeline(measured.P95 <= LargeSelectionFrameSpikeBudgetMilliseconds,
                $"{action} took {measured.P95:F2} ms at p95.");
        }

        void Paint()
        {
            var frames = stage.RenderedFrameCount;
            stage.Refresh();
            if (stage.RenderedFrameCount != frames) return;
            // A skipped paint would be measured as an impossible frame time.
            stage.Invalidate();
            stage.Update();
            AssertTimeline(stage.RenderedFrameCount != frames, "The benchmark frame did not paint the stage.");
        }

        (double Average, double P95, double Max, double Fps) MeasurePan(string label)
        {
            stage.Capture = true;
            try
            {
                for (var warmup = 0; warmup < 8; warmup++)
                {
                    stage.Pan(1, 0);
                    Paint();
                }

                Application.DoEvents();
                var timings = new List<double>(96);
                var commandMilliseconds = 0d;
                var presentMilliseconds = 0d;
                var paintMilliseconds = 0d;
                for (var frame = 0; frame < 96; frame++)
                {
                    var started = Stopwatch.GetTimestamp();
                    stage.Pan(frame % 2 == 0 ? 2 : -2, 0);
                    Paint();
                    timings.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    commandMilliseconds += renderer.LastCommandMilliseconds;
                    presentMilliseconds += renderer.LastPresentMilliseconds;
                    paintMilliseconds += stage.LastFrameRenderMilliseconds;
                }

                var measured = Summarize(timings);
                Console.WriteLine(
                    $"large_selection_pan_{label}:avg_ms={measured.Average:F3},p95_ms={measured.P95:F3},"
                    + $"max_ms={measured.Max:F3},fps={measured.Fps:F1},drawn={stage.LastStats.DrawnObjects},"
                    + $"tiles={stage.LastStats.TileDraws},command_ms={commandMilliseconds / 96:F3},"
                    + $"paint_ms={paintMilliseconds / 96:F3},present_ms={presentMilliseconds / 96:F3},"
                    + $"base={stage.LastDirect2DBaseFrameCacheBuilds}/{stage.LastDirect2DBaseFrameCacheReuses},"
                    + $"copy_ms={stage.LastDirect2DBaseFrameCopyMilliseconds:F3},"
                    + $"detail={stage.LastDirect2DLodDetailObjectDraws},"
                    + $"commands={renderer.SelectionCommandBuilds}/{renderer.SelectionCommandReuses}");
                return measured;
            }
            finally { stage.Capture = false; }
        }

        (double Average, double P95, double Max, double Fps) MeasureZoom()
        {
            var anchor = new Point(stage.Width / 2 + 40, stage.Height / 2 - 20);
            for (var warmup = 0; warmup < 8; warmup++)
            {
                // Alternate so the warmup does not drift the zoom, which would silently
                // move later phases onto the per-object scene path.
                stage.ZoomAt(anchor, warmup % 2 == 0 ? 1.02f : 1f / 1.02f, interactivePreview: true);
                stage.Refresh();
                RequireMethod(typeof(StageControl), "EndZoomLodPreview").Invoke(stage, [false]);
            }

            Application.DoEvents();
            var timings = new List<double>(96);
            for (var frame = 0; frame < 96; frame++)
            {
                var started = Stopwatch.GetTimestamp();
                stage.ZoomAt(anchor, frame % 2 == 0 ? 1.02f : 1f / 1.02f, interactivePreview: true);
                Paint();
                timings.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }

            RequireMethod(typeof(StageControl), "EndZoomLodPreview").Invoke(stage, [false]);
            stage.Refresh();
            var measured = Summarize(timings);
            Console.WriteLine(
                $"large_selection_zoom:avg_ms={measured.Average:F3},p95_ms={measured.P95:F3},"
                + $"max_ms={measured.Max:F3},fps={measured.Fps:F1},preview={stage.ZoomLodPreviewActive},"
                + $"commands={renderer.SelectionCommandBuilds}/{renderer.SelectionCommandReuses}");
            return measured;
        }

        (double Average, double P95, double Max, double Fps) MeasureDrag()
        {
            stage.Capture = true;
            try
            {
                for (var warmup = 0; warmup < 8; warmup++)
                {
                    stage.SetSelectionDragPreviewOffset(new PointF(1, 0));
                    Paint();
                }

                Application.DoEvents();
                var timings = new List<double>(96);
                var commandMilliseconds = 0d;
                var presentMilliseconds = 0d;
                var paintMilliseconds = 0d;
                var samples = new List<string>(3);
                for (var frame = 0; frame < 96; frame++)
                {
                    var started = Stopwatch.GetTimestamp();
                    stage.SetSelectionDragPreviewOffset(new PointF(frame % 2 == 0 ? 2 : -2, 0));
                    Paint();
                    timings.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    commandMilliseconds += renderer.LastCommandMilliseconds;
                    presentMilliseconds += renderer.LastPresentMilliseconds;
                    paintMilliseconds += stage.LastFrameRenderMilliseconds;
                    if (frame is 10 or 20 or 40)
                    {
                        samples.Add(
                            $"#{frame}:cm={renderer.LastCommandMilliseconds:F2},pm={renderer.LastPresentMilliseconds:F2},"
                            + $"frame={stage.LastFrameRenderMilliseconds:F2},drawn={stage.LastStats.DrawnObjects},"
                            + $"tiles={stage.LastStats.TileDraws},base={stage.LastDirect2DBaseFrameCacheBuilds}/{stage.LastDirect2DBaseFrameCacheReuses},"
                            + $"copy={stage.LastDirect2DBaseFrameCopyMilliseconds:F2},detail={stage.LastDirect2DLodDetailObjectDraws},"
                            + $"path={stage.LastDirect2DSceneDrawPath}");
                    }
                }

                var measured = Summarize(timings);
                Console.WriteLine(
                    $"large_selection_drag:avg_ms={measured.Average:F3},p95_ms={measured.P95:F3},"
                    + $"max_ms={measured.Max:F3},fps={measured.Fps:F1},drawn={stage.LastStats.DrawnObjects},"
                    + $"tiles={stage.LastStats.TileDraws},"
                    + $"command_ms={commandMilliseconds / 96:F3},paint_ms={paintMilliseconds / 96:F3},"
                    + $"present_ms={presentMilliseconds / 96:F3},"
                    + $"commands={renderer.SelectionCommandBuilds}/{renderer.SelectionCommandReuses}");
                foreach (var sample in samples) Console.WriteLine($"large_selection_drag_sample {sample}");
                return measured;
            }
            finally
            {
                stage.ClearSelectionDragPreview();
                stage.Capture = false;
            }
        }

        (double Average, double P95, double Max, double Fps) Summarize(List<double> timings)
        {
            timings.Sort();
            var average = timings.Average();
            var p95 = timings[(int)Math.Ceiling(timings.Count * 0.95) - 1];
            return (average, p95, timings[^1], 1000d / Math.Max(average, 0.001));
        }
    }
}
