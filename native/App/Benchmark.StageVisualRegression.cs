using System.Diagnostics;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunWorkspacePreRenderTargetRegression()
    {
        const int initialWidth = 640;
        const int initialHeight = 420;
        var initialSize = new Size(initialWidth, initialHeight);
        var resizedSize = new Size(480, 300);
        var background = Color.FromArgb(255, 17, 19, 21);
        var targetColor = Color.FromArgb(255, 48, 196, 112);
        var scene = new VectorScene();
        scene.CreateEmpty();
        var objectIndex = scene.AddObject(
            0,
            PointF.Empty,
            new SizeF(6_000, 4_000),
            0,
            0,
            targetColor,
            Color.Transparent,
            12,
            ShapeKind.Rectangle);
        var frameOneComposition = CreateComposition(System.Numerics.Matrix4x4.Identity);
        var frameTwoComposition = CreateComposition(
            System.Numerics.Matrix4x4.CreateTranslation(500, 0, 0));
        var screenBounds = SystemInformation.VirtualScreen;

        using var form = new Form
        {
            ShowInTaskbar = false,
            TopMost = true,
            FormBorderStyle = FormBorderStyle.None,
            AutoScaleMode = AutoScaleMode.None,
            StartPosition = FormStartPosition.Manual,
            ClientSize = initialSize,
            Location = new Point(
                Math.Max(screenBounds.Left, screenBounds.Right - initialWidth),
                Math.Max(screenBounds.Top, screenBounds.Bottom - initialHeight))
        };
        using var stage = new StageControl(scene)
        {
            Dock = DockStyle.Fill,
            BackColor = background,
            WorldGridOpacity = 0
        };
        stage.ConfigureReferenceView(null, SceneDimension.ThreeD);
        stage.SetReferenceCameraOrientation(0, 0);
        stage.SetSceneCompositionResult(
            frameOneComposition,
            scene,
            preserveWorkspaceFrameCache: true);
        form.Controls.Add(stage);
        form.Show();
        form.Activate();
        form.BringToFront();
        Application.DoEvents();

        var renderer = RequireField(typeof(StageControl), "_direct2DRenderer").GetValue(stage)
            ?? throw new InvalidOperationException("The workspace pre-render renderer field was null.");
        var mainTargetField = RequireField(renderer.GetType(), "_target");
        var workspaceTargetField = RequireField(renderer.GetType(), "_workspacePreRenderTarget");
        var workspaceBitmapField = RequireField(renderer.GetType(), "_workspacePreRenderBitmap");

        using (var warmUpCapture = PresentStage())
        {
            AssertDirect2DFrame("warm-up");
        }
        var mainTarget = mainTargetField.GetValue(renderer);
        if (mainTarget is null)
        {
            throw new InvalidOperationException("The real HWND warm-up did not create a Direct2D main target.");
        }

        stage.Frame = 1;
        using var frameOneBaseline = PresentStage();
        AssertDirect2DFrame("frame 1 baseline");
        var frameOneCenter = ProjectObjectCenter();
        AssertPresentedColor(frameOneBaseline, frameOneCenter, "frame 1 baseline");

        stage.SetSceneCompositionResult(
            frameTwoComposition,
            scene,
            preserveWorkspaceFrameCache: true);
        stage.Frame = 2;
        using var frameTwoBaseline = PresentStage();
        AssertDirect2DFrame("frame 2 baseline");
        var frameTwoCenter = ProjectObjectCenter();
        AssertPresentedColor(frameTwoBaseline, frameTwoCenter, "frame 2 baseline");

        // Start a new workspace-cache revision so the next two calls must
        // populate the pre-render path instead of finding the normal-frame copy.
        stage.Invalidate();
        stage.SetSceneCompositionResult(
            frameOneComposition,
            scene,
            preserveWorkspaceFrameCache: true);
        stage.Frame = 1;
        var frameOneTarget = PreRenderFrame(1, frameOneComposition, "frame 1");
        using var frameOneCached = PresentStage();
        AssertDirect2DFrame("frame 1 cached");
        AssertPresentedColor(frameOneCached, frameOneCenter, "frame 1 cached");
        AssertPresentedSamplesMatch(
            frameOneBaseline,
            frameOneCached,
            frameOneCenter,
            "frame 1 pre-render");

        stage.SetSceneCompositionResult(
            frameTwoComposition,
            scene,
            preserveWorkspaceFrameCache: true);
        stage.Frame = 2;
        var frameTwoTarget = PreRenderFrame(2, frameTwoComposition, "frame 2");
        if (!ReferenceEquals(frameOneTarget, frameTwoTarget))
        {
            throw new InvalidOperationException(
                "Different pre-rendered frames did not reuse the same compatible workspace target.");
        }
        if (stage.Direct2DWorkspaceFrameCacheEntryCount != 2)
        {
            throw new InvalidOperationException(
                $"Pre-rendered frame cache lost one of two frame entries: "
                + $"entries={stage.Direct2DWorkspaceFrameCacheEntryCount}.");
        }
        using var frameTwoCached = PresentStage();
        AssertDirect2DFrame("frame 2 cached");
        AssertPresentedColor(frameTwoCached, frameTwoCenter, "frame 2 cached");
        AssertPresentedSamplesMatch(
            frameTwoBaseline,
            frameTwoCached,
            frameTwoCenter,
            "frame 2 pre-render");

        form.ClientSize = resizedSize;
        Application.DoEvents();
        if (stage.ClientSize != resizedSize)
        {
            throw new InvalidOperationException(
                $"The Stage did not follow its resized window: actual={stage.ClientSize}, expected={resizedSize}.");
        }
        if (workspaceTargetField.GetValue(renderer) is not null)
        {
            throw new InvalidOperationException(
                "Resizing the Stage retained a target-bound workspace pre-render target.");
        }
        mainTarget = mainTargetField.GetValue(renderer)
            ?? throw new InvalidOperationException(
                "Resizing the Stage lost its Direct2D main target before the next pre-render.");

        using var resizedBaseline = PresentStage();
        AssertDirect2DFrame("resized baseline");
        var resizedCenter = ProjectObjectCenter();
        AssertPresentedColor(resizedBaseline, resizedCenter, "resized baseline");

        stage.Frame = 3;
        using var resizedFrameBaseline = PresentStage();
        AssertDirect2DFrame("resized frame baseline");
        var resizedFrameCenter = ProjectObjectCenter();
        AssertPresentedColor(resizedFrameBaseline, resizedFrameCenter, "resized frame baseline");

        stage.Invalidate();
        var resizedTarget = PreRenderFrame(3, frameTwoComposition, "resized frame");
        if (ReferenceEquals(frameOneTarget, resizedTarget)
            || stage.Direct2DWorkspaceFrameCacheEntryCount != 1
            || stage.Direct2DWorkspaceFrameCacheBytes
                != (long)resizedSize.Width * resizedSize.Height * sizeof(int))
        {
            throw new InvalidOperationException(
                "The resized workspace pre-render target or cache did not adopt the new viewport dimensions: "
                + $"targetReused={ReferenceEquals(frameOneTarget, resizedTarget)}, "
                + $"entries={stage.Direct2DWorkspaceFrameCacheEntryCount}, "
                + $"bytes={stage.Direct2DWorkspaceFrameCacheBytes}, "
                + $"expectedBytes={(long)resizedSize.Width * resizedSize.Height * sizeof(int)}.");
        }
        using var resizedCached = PresentStage();
        AssertDirect2DFrame("resized frame cached");
        AssertPresentedColor(resizedCached, resizedFrameCenter, "resized frame cached");
        AssertPresentedSamplesMatch(
            resizedFrameBaseline,
            resizedCached,
            resizedFrameCenter,
            "resized frame pre-render");

        var freshColor = Color.FromArgb(255, 224, 84, 52);
        scene.SetLinearGradient(
            objectIndex,
            freshColor,
            freshColor,
            new PointF(-3_000, 0),
            new PointF(3_000, 0));
        stage.Invalidate();
        stage.Frame = 4;
        var freshCenter = ProjectObjectCenter();
        _ = PreRenderFrame(4, frameTwoComposition, "fresh fill");
        using var freshCached = PresentStage();
        AssertDirect2DFrame("fresh fill cached");
        AssertPresentedColor(freshCached, freshCenter, "fresh fill cached", freshColor);
        stage.Invalidate();
        using var freshUncached = PresentStage();
        AssertDirect2DFrame("fresh fill invalidated");
        AssertPresentedColor(freshUncached, freshCenter, "fresh fill invalidated", freshColor);
        AssertPresentedSamplesMatch(
            freshCached,
            freshUncached,
            freshCenter,
            "fresh fill invalidation");

        stage.Dispose();
        if (workspaceTargetField.GetValue(renderer) is not null
            || workspaceBitmapField.GetValue(renderer) is not null
            || mainTargetField.GetValue(renderer) is not null)
        {
            throw new InvalidOperationException(
                "Stage.Dispose did not release the Direct2D main and workspace render targets.");
        }
        form.Close();

        Console.WriteLine("workspace_prerender_target=ok");
        Console.WriteLine($"workspace_prerender_initial_size={initialSize.Width}x{initialSize.Height}");
        Console.WriteLine($"workspace_prerender_resized_size={resizedSize.Width}x{resizedSize.Height}");

        SceneCompositionResult CreateComposition(System.Numerics.Matrix4x4 transform)
        {
            return new SceneCompositionResult(
                [new SceneCompositionObjectOwner("workspace-frame", "regression")],
                [new SceneCompositionObjectPose(transform)]);
        }

        object PreRenderFrame(int frame, SceneCompositionResult composition, string label)
        {
            var priorBuilds = stage.LastDirect2DWorkspaceFrameCacheBuilds;
            if (!stage.TryPreRenderReference3DFrame(frame, scene, composition)
                || stage.LastDirect2DWorkspaceFrameCacheBuilds <= priorBuilds)
            {
                throw new InvalidOperationException(
                    $"The {label} workspace pre-render did not build a cache entry: "
                    + $"builds={stage.LastDirect2DWorkspaceFrameCacheBuilds}, prior={priorBuilds}.");
            }

            var main = mainTargetField.GetValue(renderer);
            var workspace = workspaceTargetField.GetValue(renderer);
            var workspaceBitmap = workspaceBitmapField.GetValue(renderer);
            if (main is null
                || workspace is null
                || workspaceBitmap is null
                || ReferenceEquals(main, workspace)
                || !ReferenceEquals(main, mainTarget))
            {
                throw new InvalidOperationException(
                    $"The {label} pre-render did not restore the original Direct2D target domain: "
                    + $"mainNull={main is null}, workspaceNull={workspace is null}, "
                    + $"workspaceBitmapNull={workspaceBitmap is null}, "
                    + $"sameTarget={ReferenceEquals(main, workspace)}, "
                    + $"mainChanged={!ReferenceEquals(main, mainTarget)}.");
            }
            return workspace;
        }

        PointF ProjectObjectCenter()
        {
            if (!stage.TryProjectScenePoint(objectIndex, PointF.Empty, out var center, out _))
            {
                throw new InvalidOperationException("The workspace pre-render fixture object could not be projected.");
            }
            return center;
        }

        Bitmap PresentStage()
        {
            stage.InvalidateOverlay();
            stage.Update();
            Application.DoEvents();
            return CapturePresentedStage();
        }

        Bitmap CapturePresentedStage()
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                form.Activate();
                form.BringToFront();
                stage.Update();
                Application.DoEvents();
                System.Threading.Thread.Sleep(20);
                Application.DoEvents();

                var captureBounds = stage.RectangleToScreen(stage.ClientRectangle);
                Bitmap? capture = null;
                try
                {
                    capture = new Bitmap(captureBounds.Width, captureBounds.Height);
                    using var graphics = Graphics.FromImage(capture);
                    graphics.CopyFromScreen(
                        captureBounds.Location,
                        Point.Empty,
                        captureBounds.Size,
                        CopyPixelOperation.SourceCopy);
                    return capture;
                }
                catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
                    or System.Runtime.InteropServices.ExternalException
                    or ArgumentException)
                {
                    capture?.Dispose();
                }
            }

            throw new InvalidOperationException(
                "The real HWND workspace pre-render Stage could not be captured from the desktop.");
        }

        void AssertDirect2DFrame(string label)
        {
            if (!stage.LastFrameUsedDirect2D
                || !stage.GpuAccelerationActive
                || mainTargetField.GetValue(renderer) is null)
            {
                throw new InvalidOperationException(
                    $"The {label} workspace pre-render presentation did not use the Direct2D main target.");
            }
        }

        void AssertPresentedColor(
            Bitmap bitmap,
            PointF center,
            string label,
            Color? expectedColor = null)
        {
            var actual = SampleBitmap(bitmap, center);
            var expected = expectedColor ?? targetColor;
            if (PixelRgbNear(actual, background, 12)
                || !PixelRgbNear(actual, expected, 64))
            {
                throw new InvalidOperationException(
                    $"The {label} presentation lost representative scene content: "
                    + $"actual={actual.ToArgb():X8}, expected={expected.ToArgb():X8}.");
            }
        }

        static void AssertPresentedSamplesMatch(
            Bitmap baseline,
            Bitmap cached,
            PointF center,
            string label)
        {
            var expected = SampleBitmap(baseline, center);
            var actual = SampleBitmap(cached, center);
            if (!PixelRgbNear(actual, expected, 12))
            {
                throw new InvalidOperationException(
                    $"The {label} cached presentation changed its representative pixel: "
                    + $"actual={actual.ToArgb():X8}, baseline={expected.ToArgb():X8}.");
            }
        }

        static bool PixelRgbNear(Color actual, Color expected, int tolerance)
        {
            return Math.Abs(actual.R - expected.R) <= tolerance
                && Math.Abs(actual.G - expected.G) <= tolerance
                && Math.Abs(actual.B - expected.B) <= tolerance;
        }
    }

    private static void RunWorkspacePanelAnimationRegression()
    {
        var start = MainForm.ResolveWorkspacePanelAnimationValue(324, 0, 0);
        var quarter = MainForm.ResolveWorkspacePanelAnimationValue(324, 0, 0.25);
        var midpoint = MainForm.ResolveWorkspacePanelAnimationValue(324, 0, 0.5);
        var end = MainForm.ResolveWorkspacePanelAnimationValue(324, 0, 1);
        var reversedMidpoint = MainForm.ResolveWorkspacePanelAnimationValue(midpoint, 324, 0.5);
        AssertTimeline(
            start == 324
            && quarter < start
            && midpoint < quarter
            && midpoint > end
            && end == 0
            && reversedMidpoint > midpoint
            && reversedMidpoint < 324,
            "Workspace panel animation did not preserve bounded easing or mid-animation reversal.");

        RunResponsiveWorkbenchLayoutRegression();
    }

    private static void RunResponsiveWorkbenchLayoutRegression()
    {
        var desktop = MainForm.CalculateResponsiveWindowMetrics(
            new Rectangle(0, 0, 1920, 1040),
            96,
            new Size(1480, 920),
            192);
        var laptop = MainForm.CalculateResponsiveWindowMetrics(
            new Rectangle(0, 0, 1366, 728),
            96,
            new Size(1318, 680),
            192);
        var compact = MainForm.CalculateResponsiveWindowMetrics(
            new Rectangle(0, 0, 1024, 728),
            96,
            new Size(976, 680),
            192);
        var small = MainForm.CalculateResponsiveWindowMetrics(
            new Rectangle(0, 0, 800, 560),
            96,
            new Size(752, 520),
            192);
        var highDpi = MainForm.CalculateResponsiveWindowMetrics(
            new Rectangle(0, 0, 2560, 1440),
            144,
            new Size(1920, 1080),
            288);

        AssertTimeline(
            desktop.InitialSize == new Size(1480, 920)
            && desktop.MinimumSize == new Size(1120, 720)
            && desktop.InspectorWidth == 324
            && desktop.VaultWidth == 306
            && desktop.TimelineHeight == 192
            && laptop.InitialSize.Width <= 1366
            && laptop.InitialSize.Height <= 728
            && laptop.InspectorWidth < desktop.InspectorWidth
            && laptop.TimelineHeight < desktop.TimelineHeight
            && compact.MinimumSize.Width <= compact.InitialSize.Width
            && compact.MinimumSize.Height <= compact.InitialSize.Height
            && compact.InspectorWidth <= laptop.InspectorWidth
            && compact.InspectorWidth >= 272
            && small.InitialSize.Width <= 800
            && small.InitialSize.Height <= 560
            && small.MinimumSize.Width <= small.InitialSize.Width
            && small.MinimumSize.Height <= small.InitialSize.Height
            && small.TimelineHeight >= 118
            && highDpi.InspectorWidth >= 366
            && highDpi.TimelineHeight >= 177,
            $"Responsive workbench metrics were not bounded across resolutions: " +
            $"desktop={desktop}, laptop={laptop}, compact={compact}, small={small}, highDpi={highDpi}.");
    }

    private static void RunWorldGridRegression()
    {
        var emptyScene = new VectorScene();
        emptyScene.CreateEmpty();
        using var defaultStage = new StageControl(emptyScene);
        using var workspaceTabs = new WorkspaceTabs();
        var defaultStageGridType = defaultStage.WorldGridType;
        var defaultTabGridType = workspaceTabs.WorldGridType;
        var gridTypeChanges = 0;
        workspaceTabs.WorldGridTypeChanged += (_, _) => gridTypeChanges++;
        workspaceTabs.WorldGridType = WorldGridType.GoldenSpiral;
        defaultStage.WorldGridType = WorldGridType.GoldenSpiral;
        using var restoredGridStage = new StageControl(emptyScene);
        restoredGridStage.RestoreViewState(defaultStage.CaptureViewState());
        const float goldenGridSnapStep = 1000;
        var goldenViewport = new RectangleF(
            -emptyScene.StageWidth * 0.5f,
            -emptyScene.StageHeight * 0.5f,
            emptyScene.StageWidth,
            emptyScene.StageHeight);
        var goldenGrid = GoldenSpiralGridLayout.Resolve(goldenViewport, goldenGridSnapStep, 1);
        var distantGoldenViewport = new RectangleF(
            goldenViewport.Left * 50,
            goldenViewport.Top * 50,
            goldenViewport.Width * 50,
            goldenViewport.Height * 50);
        var distantGoldenGrid = GoldenSpiralGridLayout.Resolve(
            distantGoldenViewport,
            goldenGridSnapStep * 10,
            500);
        var polarGrid = PolarGridLayout.Resolve(goldenViewport, goldenGridSnapStep);
        var distantPolarViewport = new RectangleF(
            1_000_000,
            800_000,
            goldenViewport.Width,
            goldenViewport.Height);
        var distantPolarGrid = PolarGridLayout.Resolve(distantPolarViewport, goldenGridSnapStep);
        var zoomedDistantPolarViewport = new RectangleF(4_999_875, 2_999_918, 250, 164);
        var zoomedDistantPolarGrid = PolarGridLayout.Resolve(
            zoomedDistantPolarViewport,
            WorldGridLayout.MinimumStepWorld);
        Span<PolarGridArc> visiblePolarArcs = stackalloc PolarGridArc[PolarGridLayout.MaximumVisibleArcsPerCircle];
        var visiblePolarArcCount = 0;
        var maximumPolarArcSegments = 0;
        foreach (var circle in zoomedDistantPolarGrid.Circles)
        {
            var arcCount = PolarGridLayout.ResolveVisibleArcs(
                zoomedDistantPolarViewport,
                circle.Radius,
                visiblePolarArcs);
            visiblePolarArcCount += arcCount;
            for (var arcIndex = 0; arcIndex < arcCount; arcIndex++)
            {
                maximumPolarArcSegments = Math.Max(
                    maximumPolarArcSegments,
                    PolarGridLayout.ResolveArcSegmentCount(
                        circle.Radius * 2.56f,
                        visiblePolarArcs[arcIndex].SweepAngle));
            }
        }
        var clippedPolarDiameterCount = zoomedDistantPolarGrid.DiameterSegments.Count(segment =>
            PolarGridLayout.TryClipSegment(zoomedDistantPolarViewport, segment, out _));
        workspaceTabs.WorldGridType = WorldGridType.Polar;
        defaultStage.WorldGridType = WorldGridType.Polar;
        using var restoredPolarStage = new StageControl(emptyScene);
        restoredPolarStage.RestoreViewState(defaultStage.CaptureViewState());
        var goldenCenter = new PointF(
            goldenGrid.Bounds.Left + goldenGrid.Bounds.Width * (1f - 1f / GoldenSpiralGridLayout.GoldenRatio),
            goldenGrid.Bounds.Top + goldenGrid.Bounds.Height / GoldenSpiralGridLayout.GoldenRatio);
        var goldenStart = goldenGrid.SpiralPoints[0];
        var outerRadius = RadiusFromCenter(goldenGrid.SpiralPoints[^1]);
        var priorQuarterRadius = RadiusFromCenter(goldenGrid.SpiralPoints[^(16 + 1)]);
        var distantOuterRadius = RadiusFromCenter(distantGoldenGrid.SpiralPoints[^1]);
        var firstRenderedRadius = RadiusFromCenter(goldenGrid.SpiralPoints[1]);
        float RadiusFromCenter(PointF point)
        {
            var dx = point.X - goldenCenter.X;
            var dy = point.Y - goldenCenter.Y;
            return MathF.Sqrt(dx * dx + dy * dy);
        }
        using var zoomGridStage = new StageControl(emptyScene) { Size = new Size(760, 520) };
        var zoomPointA = goldenGrid.SpiralPoints[160];
        var zoomPointB = goldenGrid.SpiralPoints[176];
        zoomGridStage.SetVisibleWorldWidth(emptyScene.StageWidth);
        var normalDistance = ScreenDistance(zoomPointA, zoomPointB);
        zoomGridStage.SetVisibleWorldWidth(emptyScene.StageWidth * 0.5f);
        var zoomedDistance = ScreenDistance(zoomPointA, zoomPointB);
        float ScreenDistance(PointF first, PointF second)
        {
            var firstScreen = zoomGridStage.WorldToScreen(first.X, first.Y);
            var secondScreen = zoomGridStage.WorldToScreen(second.X, second.Y);
            var dx = firstScreen.X - secondScreen.X;
            var dy = firstScreen.Y - secondScreen.Y;
            return MathF.Sqrt(dx * dx + dy * dy);
        }
        var closeScale = WorldGridLayout.Resolve(2.56f);
        var defaultScale = WorldGridLayout.Resolve(VectorUnits.PixelsPerUnit);
        var distantScale = WorldGridLayout.Resolve(VectorUnits.PixelsPerUnit * 0.02f);
        var defaultSnapStep = defaultStage.AdaptiveGridSnapStep;
        defaultStage.RestoreViewState(defaultStage.CaptureViewState() with { Zoom = 64f });
        var closeSnapStep = defaultStage.AdaptiveGridSnapStep;
        defaultStage.RestoreViewState(defaultStage.CaptureViewState() with { Zoom = 0.02f });
        var distantSnapStep = defaultStage.AdaptiveGridSnapStep;
        var beforeTransition = new WorldGridScale(100, 5.9f);
        var afterTransition = new WorldGridScale(1000, 59f);
        var carriedMajor = WorldGridLayout.ResolveLineStyle(10, beforeTransition, 1);
        var promotedMinor = WorldGridLayout.ResolveLineStyle(1, afterTransition, 1);
        var fineLine = WorldGridLayout.ResolveLineStyle(1, defaultScale, 1);
        var majorLine = WorldGridLayout.ResolveLineStyle(10, defaultScale, 1);
        if (Math.Abs(defaultStage.WorldGridOpacity - 0.1f) > 0.0001f
            || workspaceTabs.WorldGridOpacity != 10
            || defaultStageGridType != WorldGridType.Cartesian
            || defaultTabGridType != WorldGridType.Cartesian
            || workspaceTabs.WorldGridType != WorldGridType.Polar
            || restoredGridStage.WorldGridType != WorldGridType.GoldenSpiral
            || restoredPolarStage.WorldGridType != WorldGridType.Polar
            || gridTypeChanges != 2
            || polarGrid.Origin != PointF.Empty
            || polarGrid.DiameterSegments.Length != PolarGridLayout.DiameterCount
            || polarGrid.Circles.Length < 2
            || Math.Abs(polarGrid.Circles[0].Radius - goldenGridSnapStep) > 0.001f
            || Math.Abs(polarGrid.Circles[1].Radius - polarGrid.Circles[0].Radius - goldenGridSnapStep) > 0.001f
            || distantPolarGrid.Circles.Length > 256
            || distantPolarGrid.Circles.Length < 2
            || distantPolarGrid.Circles[0].Radius < 1_000_000
            || zoomedDistantPolarGrid.Circles.Length > PolarGridLayout.MaximumVisibleCircles
            || visiblePolarArcCount <= 0
            || visiblePolarArcCount > zoomedDistantPolarGrid.Circles.Length * PolarGridLayout.MaximumVisibleArcsPerCircle
            || maximumPolarArcSegments <= 0
            || maximumPolarArcSegments > PolarGridLayout.MaximumArcSegments
            || clippedPolarDiameterCount > PolarGridLayout.DiameterCount
            || goldenGrid.GuideSegments.Length != 8
            || goldenGrid.SpiralPoints.Length < 300
            || distantGoldenGrid.SpiralPoints.Length < 250
            || Math.Abs(goldenGrid.Bounds.Width / goldenGrid.Bounds.Height - GoldenSpiralGridLayout.GoldenRatio) > 0.001f
            || Math.Abs(goldenCenter.X) > 0.01f
            || Math.Abs(goldenCenter.Y) > 0.01f
            || Math.Abs(goldenStart.X) > 0.001f
            || Math.Abs(goldenStart.Y) > 0.001f
            || firstRenderedRadius > 1.001f
            || goldenGrid.Bounds.Left > goldenViewport.Left
            || goldenGrid.Bounds.Right < goldenViewport.Right
            || goldenGrid.Bounds.Top > goldenViewport.Top
            || goldenGrid.Bounds.Bottom < goldenViewport.Bottom
            || distantGoldenGrid.Bounds.Left > distantGoldenViewport.Left
            || distantGoldenGrid.Bounds.Right < distantGoldenViewport.Right
            || distantGoldenGrid.Bounds.Top > distantGoldenViewport.Top
            || distantGoldenGrid.Bounds.Bottom < distantGoldenViewport.Bottom
            || Math.Abs(goldenGrid.Bounds.Right / goldenGridSnapStep
                - MathF.Round(goldenGrid.Bounds.Right / goldenGridSnapStep)) > 0.001f
            || distantOuterRadius <= outerRadius * 25
            || Math.Abs(outerRadius / priorQuarterRadius - GoldenSpiralGridLayout.GoldenRatio) > 0.001f
            || Math.Abs(zoomedDistance / normalDistance - 2f) > 0.001f
            || closeScale.StepWorld != 10
            || defaultScale.StepWorld != 1000
            || distantScale.StepWorld != 10000
            || closeSnapStep != closeScale.StepWorld
            || defaultSnapStep != defaultScale.StepWorld
            || distantSnapStep != distantScale.StepWorld
            || Math.Abs(carriedMajor.Width - promotedMinor.Width) > 0.001f
            || carriedMajor.Color != promotedMinor.Color
            || majorLine.Width <= fineLine.Width
            || majorLine.Color.A <= fineLine.Color.A
            || WorldGridLayout.MinimumStepWorld != 10)
        {
            throw new InvalidOperationException("The world-grid modes did not preserve their defaults, shared world origin, bounded and clipped polar layout, unbounded golden-ratio layout, adaptive snapping scale, persistence, decimal hierarchy, or smooth zoom transition.");
        }
    }

    private static void RunDenseOverlapLodRegression()
    {
        const int triangleCount = 832;
        var scene = new VectorScene();
        scene.CreateEmpty();
        for (var index = 0; index < triangleCount; index++)
        {
            var offsetX = (index % 13 - 6) * 18f;
            var offsetY = ((index / 13) % 11 - 5) * 18f;
            var fill = Color.FromArgb(210, 64 + index % 96, 124 + index % 72, 194 - index % 80);
            scene.AppendObject(
                0,
                new PointF(offsetX, offsetY),
                new SizeF(2900 + index % 5 * 110, 2500 + index % 7 * 95),
                (index % 9 - 4) * 0.035f,
                VectorUnits.StrokePointsToUnits(1.5f),
                fill,
                Color.FromArgb(244, 232, 242, 250),
                6,
                ShapeKind.Triangle);
        }
        scene.CompleteDeferredBuild();

        using var form = new Form
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-30_000, -30_000),
            ClientSize = new Size(640, 420)
        };
        using var stage = new StageControl(scene) { Dock = DockStyle.Fill };
        form.Controls.Add(stage);
        form.Show();
        Application.DoEvents();
        stage.SetVisibleWorldWidth(4500);
        stage.SelectedObject = triangleCount - 1;
        stage.Invalidate();
        stage.Update();
        Application.DoEvents();

        if (!stage.LastFrameUsedDirect2D
            || !stage.LastStats.TileLod
            || stage.LastStats.TileDraws <= 0
            || stage.LastStats.DrawnObjects != 0
            || stage.LastDirect2DLodBitmapSubmissions != 1
            || stage.LastDirect2DLodDetailObjectDraws != 1)
        {
            throw new InvalidOperationException("Dense overlapping triangles did not use cached LOD while preserving the selected object's exact draw.");
        }

        const int samples = 16;
        for (var warmup = 0; warmup < 3; warmup++)
        {
            stage.Invalidate();
            stage.Update();
            Application.DoEvents();
        }

        double commandMilliseconds = 0;
        var lodBitmapBuilds = 0;
        for (var sample = 0; sample < samples; sample++)
        {
            stage.Invalidate();
            stage.Update();
            Application.DoEvents();
            commandMilliseconds += stage.LastDirect2DCommandMilliseconds;
            lodBitmapBuilds += stage.LastDirect2DLodBitmapBuilds;
            if (!stage.LastStats.TileLod
                || stage.LastDirect2DLodBitmapSubmissions != 1
                || stage.LastDirect2DLodDetailObjectDraws != 1)
            {
                throw new InvalidOperationException("Dense overlapping triangle LOD regressed during repeated rendering.");
            }
        }

        var averageCommandMilliseconds = commandMilliseconds / samples;
        var commandBudgetMet = averageCommandMilliseconds <= RenderCollectBudgetMilliseconds;
        if (lodBitmapBuilds != 0)
        {
            throw new InvalidOperationException("Dense overlapping triangle LOD rebuilt its cached bitmap during repeated rendering.");
        }

        form.Close();
        Console.WriteLine("dense_overlap_lod=ok");
        Console.WriteLine($"dense_overlap_lod_visible_objects={triangleCount}");
        Console.WriteLine($"dense_overlap_lod_command_avg_ms={averageCommandMilliseconds:0.000}");
        Console.WriteLine($"dense_overlap_lod_command_budget_ms={RenderCollectBudgetMilliseconds:0.000}");
        Console.WriteLine($"dense_overlap_lod_command_budget_met={commandBudgetMet.ToString().ToLowerInvariant()}");
    }

    private static void RunFillOverwriteRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        var older = scene.AddObject(0, PointF.Empty, new SizeF(200, 160), 0, 0, Color.Teal, Color.Transparent, 12, ShapeKind.Rectangle);
        var newer = scene.AddObject(0, new PointF(50, 0), new SizeF(200, 160), 0, 0, Color.Coral, Color.Transparent, 12, ShapeKind.Rectangle);
        var overwritten = scene.ApplyFillOverwriteToNewObjects([newer]);
        if (overwritten.Length != 1
            || !scene.FillContainsPoint(overwritten[0], new PointF(50, 0))
            || !scene.FillContainsPoint(overwritten[0], new PointF(120, 0)))
        {
            throw new InvalidOperationException("The newest fill was not retained after overpaint resolution.");
        }

        var olderRemainder = Enumerable.Range(0, scene.ObjectCount)
            .FirstOrDefault(index => index != overwritten[0] && scene.Argb[index] == Color.Teal.ToArgb());
        if (olderRemainder < 0
            || scene.ShapeKind[olderRemainder] != ShapeKind.Path
            || !scene.FillContainsPoint(olderRemainder, new PointF(-80, 0))
            || scene.FillContainsPoint(olderRemainder, new PointF(50, 0)))
        {
            throw new InvalidOperationException("A later fill did not subtract its overlap from the older fill.");
        }

        var movedScene = new VectorScene();
        movedScene.CreateEmpty();
        var stationary = movedScene.AddObject(0, PointF.Empty, new SizeF(200, 160), 0, 0, Color.Teal, Color.Transparent, 12, ShapeKind.Rectangle);
        var moved = movedScene.AddObject(0, new PointF(-220, 0), new SizeF(120, 160), 0, 0, Color.Coral, Color.Transparent, 12, ShapeKind.Rectangle);
        movedScene.TransformObjects([moved], point => new PointF(point.X + 220, point.Y));
        var movedResult = movedScene.ApplyFillOverwriteToNewObjects([moved]);
        if (movedResult.Length != 1
            || !movedScene.FillContainsPoint(movedResult[0], PointF.Empty)
            || Enumerable.Range(0, movedScene.ObjectCount)
                .Any(index => index != movedResult[0] && movedScene.FillContainsPoint(index, PointF.Empty))
            || !Enumerable.Range(0, movedScene.ObjectCount)
                .Any(index => index != movedResult[0]
                    && movedScene.Argb[index] == Color.Teal.ToArgb()
                    && movedScene.FillContainsPoint(index, new PointF(-80, 0))))
        {
            throw new InvalidOperationException("Moving a fill did not overwrite the stationary fill in its new overlap.");
        }

        var alphaScene = new VectorScene();
        alphaScene.CreateEmpty();
        var translucentOld = Color.FromArgb(96, 68, 172, 196);
        var translucentNew = Color.FromArgb(210, 68, 172, 196);
        alphaScene.AddObject(0, PointF.Empty, new SizeF(160, 120), 0, 0, translucentOld, Color.Transparent, 12, ShapeKind.Rectangle);
        var alphaOverpaint = alphaScene.AddObject(0, new PointF(35, 0), new SizeF(160, 120), 0, 0, translucentNew, Color.Transparent, 12, ShapeKind.Rectangle);
        var alphaNew = alphaScene.ApplyFillOverwriteToNewObjects([alphaOverpaint]);
        if (alphaNew.Length != 1
            || Enumerable.Range(0, alphaScene.ObjectCount)
                .Any(index => index != alphaNew[0]
                    && alphaScene.Argb[index] == translucentOld.ToArgb()
                    && alphaScene.FillContainsPoint(index, new PointF(35, 0))))
        {
            throw new InvalidOperationException("Fill overwrite treated different alpha values as the same fill material.");
        }

        var gradientScene = new VectorScene();
        gradientScene.CreateEmpty();
        var gradient = gradientScene.AddObject(0, PointF.Empty, new SizeF(220, 160), 0, 0, Color.Teal, Color.Transparent, 12, ShapeKind.Rectangle);
        var gradientStops = new[] { new GradientStop(0, Color.Teal), new GradientStop(0.45f, Color.Gold), new GradientStop(1, Color.MediumPurple) };
        var gradientStart = new PointF(-110, 0);
        var gradientEnd = new PointF(110, 0);
        gradientScene.SetGradientPaint(gradient, GradientKind.Linear, gradientStops, gradientStart, gradientEnd);
        var gradientOverpaint = gradientScene.AddObject(0, new PointF(55, 0), new SizeF(180, 160), 0, 0, Color.Coral, Color.Transparent, 12, ShapeKind.Rectangle);
        var gradientNew = gradientScene.ApplyFillOverwriteToNewObjects([gradientOverpaint]);
        var gradientRemainder = Enumerable.Range(0, gradientScene.ObjectCount)
            .FirstOrDefault(index => index != gradientNew[0] && gradientScene.Argb[index] == Color.Teal.ToArgb());
        if (gradientNew.Length != 1
            || gradientRemainder < 0
            || !gradientScene.HasGradient(gradientRemainder)
            || gradientScene.GetGradientKind(gradientRemainder) != GradientKind.Linear
            || gradientScene.GetGradientStart(gradientRemainder) != gradientStart
            || gradientScene.GetGradientEnd(gradientRemainder) != gradientEnd
            || !gradientScene.GetGradientStops(gradientRemainder).SequenceEqual(gradientStops)
            || gradientScene.FillContainsPoint(gradientRemainder, new PointF(55, 0)))
        {
            throw new InvalidOperationException("Fill overwrite did not retain the remaining gradient's material or geometry.");
        }

        var gradientCoverageScene = new VectorScene();
        gradientCoverageScene.CreateEmpty();
        var gradientCoverageStops = new[]
        {
            new GradientStop(0, Color.Teal),
            new GradientStop(0.5f, Color.Gold),
            new GradientStop(1, Color.MediumPurple)
        };
        var gradientCoverageStart = new PointF(-90, 0);
        var gradientCoverageEnd = new PointF(90, 0);
        var olderGradient = gradientCoverageScene.AddObject(0, PointF.Empty, new SizeF(180, 120), 0, 0, Color.Teal, Color.Transparent, 12, ShapeKind.Rectangle);
        gradientCoverageScene.SetGradientPaint(olderGradient, GradientKind.Linear, gradientCoverageStops, gradientCoverageStart, gradientCoverageEnd);
        var newerGradient = gradientCoverageScene.AddObject(0, PointF.Empty, new SizeF(180, 120), 0, 0, Color.Teal, Color.Transparent, 12, ShapeKind.Rectangle);
        gradientCoverageScene.SetGradientPaint(newerGradient, GradientKind.Linear, gradientCoverageStops, gradientCoverageStart, gradientCoverageEnd);
        var retainedGradient = gradientCoverageScene.ApplyFillOverwriteToNewObjects([newerGradient]);
        if (retainedGradient.Length != 1
            || gradientCoverageScene.ObjectCount != 1
            || !gradientCoverageScene.HasGradient(retainedGradient[0])
            || !gradientCoverageScene.GetGradientStops(retainedGradient[0]).SequenceEqual(gradientCoverageStops)
            || !gradientCoverageScene.FillContainsPoint(retainedGradient[0], PointF.Empty))
        {
            throw new InvalidOperationException("A newer gradient fill did not cover an identical older gradient fill.");
        }

        var gradientMergeScene = new VectorScene();
        gradientMergeScene.CreateEmpty();
        var firstGradient = gradientMergeScene.AddObject(0, new PointF(-45, 0), new SizeF(120, 120), 0, 0, Color.Teal, Color.Transparent, 12, ShapeKind.Rectangle);
        var secondGradient = gradientMergeScene.AddObject(0, new PointF(45, 0), new SizeF(120, 120), 0, 0, Color.Teal, Color.Transparent, 12, ShapeKind.Rectangle);
        gradientMergeScene.SetGradientPaint(firstGradient, GradientKind.Linear, gradientCoverageStops, gradientCoverageStart, gradientCoverageEnd);
        gradientMergeScene.SetGradientPaint(secondGradient, GradientKind.Linear, gradientCoverageStops, gradientCoverageStart, gradientCoverageEnd);
        var preservedGradient = gradientMergeScene.MergeSameColorFillsAround(secondGradient, frame: 0);
        if (preservedGradient != secondGradient
            || gradientMergeScene.ObjectCount != 2
            || !gradientMergeScene.HasGradient(firstGradient)
            || !gradientMergeScene.HasGradient(secondGradient))
        {
            throw new InvalidOperationException("Gradient fills participated in same-color fill merging.");
        }

        var borderedScene = new VectorScene();
        borderedScene.CreateEmpty();
        borderedScene.AddObject(0, PointF.Empty, new SizeF(160, 120), 0, VectorUnits.StrokePointsToUnits(2), Color.Teal, Color.White, 12, ShapeKind.Rectangle);
        var covering = borderedScene.AddObject(0, PointF.Empty, new SizeF(240, 180), 0, 0, Color.Coral, Color.Transparent, 12, ShapeKind.Rectangle);
        borderedScene.ApplyFillOverwriteToNewObjects([covering]);
        if (Enumerable.Range(0, borderedScene.ObjectCount).Any(index => borderedScene.ShapeKind[index] != ShapeKind.Freeform && borderedScene.Argb[index] == Color.Teal.ToArgb())
            || !Enumerable.Range(0, borderedScene.ObjectCount).Any(index => borderedScene.ShapeKind[index] == ShapeKind.Freeform
                && borderedScene.Stroke[index] > 0
                && borderedScene.StrokeArgb[index] == Color.White.ToArgb()))
        {
            throw new InvalidOperationException("Fill overwrite removed an existing shape boundary instead of preserving it as a stroke.");
        }

        var isolatedScene = new VectorScene();
        isolatedScene.CreateEmpty();
        var baseFill = isolatedScene.AddObject(0, PointF.Empty, new SizeF(160, 120), 0, 0, Color.Teal, Color.Transparent, 12, ShapeKind.Rectangle);
        var secondLayer = isolatedScene.AddLayer();
        isolatedScene.ActiveLayer = secondLayer;
        var secondLayerFill = isolatedScene.AddObject(secondLayer, PointF.Empty, new SizeF(160, 120), 0, 0, Color.Coral, Color.Transparent, 12, ShapeKind.Rectangle);
        isolatedScene.ApplyFillOverwriteToNewObjects([secondLayerFill]);
        if (!isolatedScene.FillContainsPoint(baseFill, PointF.Empty))
        {
            throw new InvalidOperationException("Fill overwrite crossed drawing-layer boundaries.");
        }

        var celScene = new VectorScene();
        celScene.CreateEmpty();
        var frameZeroFill = celScene.AddObject(0, PointF.Empty, new SizeF(160, 120), 0, 0, Color.Teal, Color.Transparent, 12, ShapeKind.Rectangle);
        if (!celScene.InsertTimelineKeyframe(0, 10)) throw new InvalidOperationException("Fill overwrite cel setup failed.");
        celScene.EditFrame = 10;
        var frameTenFill = celScene.AddObject(0, PointF.Empty, new SizeF(160, 120), 0, 0, Color.Coral, Color.Transparent, 12, ShapeKind.Rectangle);
        celScene.ApplyFillOverwriteToNewObjects([frameTenFill], 10);
        if (!celScene.FillContainsPoint(frameZeroFill, PointF.Empty))
        {
            throw new InvalidOperationException("Fill overwrite crossed keyframe boundaries.");
        }

        Console.WriteLine("fill_overwrite_regression=ok");
    }

    private static void RunClosedFillRegionRegression()
    {
        var stroke = VectorUnits.StrokePointsToUnits(2);
        var color = Color.FromArgb(79, 179, 162);
        var scene = new VectorScene();
        scene.CreateEmpty();
        scene.AddLineSegment(0, new PointF(0, 0), new PointF(200, 0), stroke, Color.Transparent, Color.White, 6);
        scene.AddLineSegment(0, new PointF(200, 0), new PointF(200, 120), stroke, Color.Transparent, Color.White, 6);
        scene.AddLineSegment(0, new PointF(200, 120), new PointF(0, 120), stroke, Color.Transparent, Color.White, 6);
        scene.AddLineSegment(0, new PointF(0, 120), new PointF(0, 0), stroke, Color.Transparent, Color.White, 6);
        scene.AddLineSegment(0, new PointF(100, 0), new PointF(100, 120), stroke, Color.Transparent, Color.White, 6);

        if (!scene.TryCreateFillFromClosedStrokeRegion(new PointF(50, 60), 0, color, out var created, out var contours)
            || created < 0
            || scene.ShapeKind[created] != ShapeKind.Path
            || scene.Argb[created] != color.ToArgb()
            || scene.Stroke[created] != 0
            || contours.Length != 1
            || !scene.FillContainsPoint(created, new PointF(50, 60))
            || scene.FillContainsPoint(created, new PointF(150, 60)))
        {
            throw new InvalidOperationException("Closed stroke fill did not create only the clicked bounded region.");
        }

        if (scene.TryCreateFillFromClosedStrokeRegion(new PointF(260, 60), 0, color, out _, out _))
        {
            throw new InvalidOperationException("Closed stroke fill created geometry outside every bounded region.");
        }

        var fillBoundaryScene = new VectorScene();
        fillBoundaryScene.CreateEmpty();
        fillBoundaryScene.AddObject(
            0,
            new PointF(-50, 50),
            new SizeF(100, 100),
            0,
            0,
            Color.RoyalBlue,
            Color.Transparent,
            12,
            ShapeKind.Rectangle);
        fillBoundaryScene.AddLineSegment(0, new PointF(0, 0), new PointF(100, 0), stroke, Color.Transparent, Color.White, 6);
        fillBoundaryScene.AddLineSegment(0, new PointF(100, 0), new PointF(100, 100), stroke, Color.Transparent, Color.White, 6);
        fillBoundaryScene.AddLineSegment(0, new PointF(100, 100), new PointF(0, 100), stroke, Color.Transparent, Color.White, 6);
        if (!fillBoundaryScene.TryGetClosedStrokeFillRegion(new PointF(50, 50), 0, out var fillBoundedPreview)
            || fillBoundedPreview is not [{ Length: 4 }]
            || !fillBoundaryScene.TryCreateFillFromClosedStrokeRegion(
                new PointF(50, 50),
                0,
                Color.Coral,
                out var fillBoundedObject,
                out _)
            || fillBoundedObject < 0
            || !fillBoundaryScene.FillContainsPoint(fillBoundedObject, new PointF(50, 50))
            || fillBoundaryScene.FillContainsPoint(fillBoundedObject, new PointF(-50, 50))
            || !fillBoundaryScene.TryGetPathBezierWorldContours(fillBoundedObject, out var fillBoundedBezier)
            || fillBoundedBezier is not [{ Length: 4 }])
        {
            throw new InvalidOperationException(
                "The Fill tool did not use an existing Fill edge with three strokes to bound an adjacent empty region.");
        }

        var otherLayerBoundaryScene = new VectorScene();
        otherLayerBoundaryScene.CreateEmpty();
        var otherLayer = otherLayerBoundaryScene.AddLayer();
        otherLayerBoundaryScene.AddObject(
            otherLayer,
            new PointF(-50, 50),
            new SizeF(100, 100),
            0,
            0,
            Color.RoyalBlue,
            Color.Transparent,
            12,
            ShapeKind.Rectangle);
        otherLayerBoundaryScene.AddLineSegment(0, new PointF(0, 0), new PointF(100, 0), stroke, Color.Transparent, Color.White, 6);
        otherLayerBoundaryScene.AddLineSegment(0, new PointF(100, 0), new PointF(100, 100), stroke, Color.Transparent, Color.White, 6);
        otherLayerBoundaryScene.AddLineSegment(0, new PointF(100, 100), new PointF(0, 100), stroke, Color.Transparent, Color.White, 6);
        if (otherLayerBoundaryScene.TryCreateFillFromClosedStrokeRegion(
                new PointF(50, 50),
                0,
                Color.Coral,
                out _,
                out _))
        {
            throw new InvalidOperationException("The Fill tool used a Fill edge from another drawing layer as a boundary.");
        }

        var curvedScene = new VectorScene();
        curvedScene.CreateEmpty();
        var curvedBoundary = curvedScene.AddCubicCurveSegment(
            0,
            new PointF(0, 120),
            new PointF(-80, 90),
            new PointF(-80, 30),
            new PointF(0, 0),
            stroke,
            Color.Transparent,
            Color.White,
            12);
        curvedScene.AddLineSegment(0, new PointF(0, 0), new PointF(200, 0), stroke, Color.Transparent, Color.White, 6);
        curvedScene.AddLineSegment(0, new PointF(200, 0), new PointF(200, 120), stroke, Color.Transparent, Color.White, 6);
        curvedScene.AddLineSegment(0, new PointF(200, 120), new PointF(0, 120), stroke, Color.Transparent, Color.White, 6);
        var curvedCreated = curvedScene.TryCreateFillFromClosedStrokeRegion(
            new PointF(100, 60),
            0,
            Color.Teal,
            out var curvedFill,
            out var curvedAnimationContours);
        PathBezierNode[][] curvedFillContours = [];
        var hasCurvedBezier = curvedCreated
            && curvedFill >= 0
            && curvedScene.TryGetPathBezierWorldContours(curvedFill, out curvedFillContours);
        var curvedBezierParts = hasCurvedBezier
            ? curvedScene.GetPathBezierSegmentParts(curvedFill)
            : [];
        var curvedBoundaryLinks = curvedCreated
            ? curvedScene.CaptureFillBoundaryLineLinks(curvedBoundary, 0)
            : [];
        if (!curvedCreated
            || curvedFill < 0
            || !hasCurvedBezier
            || curvedFillContours.Length != 1
            || curvedFillContours[0].Length != 4
            || curvedBezierParts.Length != 4
            || curvedBoundaryLinks is not [{ BezierSegmentCount: 1 }]
            || curvedAnimationContours.Length != 1
            || curvedAnimationContours[0].Length <= curvedFillContours[0].Length
            || !curvedScene.FillContainsPoint(curvedFill, new PointF(100, 60)))
        {
            throw new InvalidOperationException(
                $"Closed-stroke filling flattened an adjacent cubic boundary into sampled linear Bezier anchors: created={curvedCreated}/{curvedFill}, exact={hasCurvedBezier}, contours={curvedFillContours.Length}, nodes={(curvedFillContours.Length > 0 ? curvedFillContours[0].Length : 0)}, parts={curvedBezierParts.Length}, links={string.Join(',', curvedBoundaryLinks.Select(link => $"{link.BezierSegmentIndex}+{link.BezierSegmentCount}"))}, animation={(curvedAnimationContours.Length > 0 ? curvedAnimationContours[0].Length : 0)}.");
        }

        var shallowCurveScene = new VectorScene();
        shallowCurveScene.CreateEmpty();
        shallowCurveScene.AddLineSegment(0, new PointF(0, 0), new PointF(240, 0), stroke, Color.Transparent, Color.White, 6);
        shallowCurveScene.AddLineSegment(0, new PointF(240, 0), new PointF(240, 140), stroke, Color.Transparent, Color.White, 6);
        var shallowBoundary = shallowCurveScene.AddCubicCurveSegment(
            0,
            new PointF(240, 140),
            new PointF(165, 145),
            new PointF(75, 75),
            new PointF(0, 140),
            stroke,
            Color.Transparent,
            Color.White,
            12);
        shallowCurveScene.AddLineSegment(0, new PointF(0, 140), new PointF(0, 0), stroke, Color.Transparent, Color.White, 6);
        if (!shallowCurveScene.TryCreateFillFromClosedStrokeRegion(
                new PointF(120, 60),
                0,
                Color.Teal,
                out var shallowFill,
                out _)
            || !shallowCurveScene.TryGetPathBezierWorldContours(shallowFill, out var shallowContours)
            || shallowContours is not [{ Length: 4 }])
        {
            throw new InvalidOperationException("A shallow cubic closed-stroke boundary was flattened into sampled Fill anchors.");
        }

        var shallowOverlayPieces = shallowCurveScene.GetExposedFillBezierSegmentPieces(
            shallowFill,
            0,
            includeCoincidentStrokes: true);
        if (shallowOverlayPieces.Length != 4)
        {
            throw new InvalidOperationException(
                $"A coincident cubic Line split the Fill edit overlay into sampled handles: pieces={shallowOverlayPieces.Length}.");
        }

        var shallowLineLinks = shallowCurveScene.CaptureFillBoundaryLineLinks(shallowBoundary, 0);
        if (shallowLineLinks is not [{ BezierSegmentCount: 1 }])
        {
            throw new InvalidOperationException("A shallow cubic Fill boundary did not retain its editable Line link.");
        }

        var crossingCurveScene = new VectorScene();
        crossingCurveScene.CreateEmpty();
        crossingCurveScene.AddObject(
            0,
            new PointF(200, 120),
            new SizeF(320, 200),
            0,
            stroke,
            Color.Transparent,
            Color.White,
            24,
            ShapeKind.Ellipse);
        crossingCurveScene.AddCubicCurveSegment(
            0,
            new PointF(0, 130),
            new PointF(110, 55),
            new PointF(145, 90),
            new PointF(205, 105),
            stroke,
            Color.Transparent,
            Color.White,
            12);
        crossingCurveScene.AddCubicCurveSegment(
            0,
            new PointF(205, 105),
            new PointF(270, 120),
            new PointF(305, 75),
            new PointF(400, 130),
            stroke,
            Color.Transparent,
            Color.White,
            12);
        if (!crossingCurveScene.TryCreateFillFromClosedStrokeRegion(
                new PointF(200, 170),
                0,
                Color.MediumSeaGreen,
                out var crossingCurveFill,
                out _)
            || !crossingCurveScene.TryGetPathBezierWorldContours(crossingCurveFill, out _))
        {
            throw new InvalidOperationException("The two-intersection cubic Fill regression could not create an exact boundary.");
        }

        var crossingCurveParts = crossingCurveScene.GetPathBezierSegmentParts(crossingCurveFill);
        var crossingCurvePieces = crossingCurveScene.GetExposedFillBezierSegmentPieces(
            crossingCurveFill,
            0,
            includeCoincidentStrokes: true);
        if (crossingCurveParts.Length > 8 || crossingCurvePieces.Length > 8)
        {
            throw new InvalidOperationException(
                $"A cubic crossing a closed boundary twice became sampled Fill anchors: parts={crossingCurveParts.Length}, pieces={crossingCurvePieces.Length}.");
        }

        if (!crossingCurveScene.TryGetClosedStrokeFillRegion(
                new PointF(200, 170),
                0,
                out var flattenedCrossingContours))
        {
            throw new InvalidOperationException("The flattened two-intersection cubic regression could not resolve its region.");
        }

        var flattenedCrossingFill = crossingCurveScene.AddPathObjectContours(
            0,
            flattenedCrossingContours,
            0,
            Color.Coral,
            Color.Transparent,
            (uint)flattenedCrossingContours.Sum(contour => contour.Length));
        var flattenedCrossingPartsBefore = crossingCurveScene.GetEditableFillBezierSegmentParts(flattenedCrossingFill).Length;
        var flattenedCrossingCanonicalized = crossingCurveScene.CanonicalizeFlattenedFillBoundaryCurves(
            flattenedCrossingFill,
            0);
        var flattenedCrossingPartsAfter = crossingCurveScene.GetEditableFillBezierSegmentParts(flattenedCrossingFill).Length;
        if (flattenedCrossingPartsBefore <= 8
            || !flattenedCrossingCanonicalized
            || flattenedCrossingPartsAfter > 8)
        {
            throw new InvalidOperationException(
                $"A sampled Fill between two cubic intersections was not restored from its adjacent curves: before={flattenedCrossingPartsBefore}, canonicalized={flattenedCrossingCanonicalized}, after={flattenedCrossingPartsAfter}.");
        }

        var liveChainScene = new VectorScene();
        liveChainScene.CreateEmpty();
        var liveChainFirstLine = liveChainScene.AddCubicCurveSegment(
            0,
            new PointF(-844, -212),
            new PointF(-473, -292),
            new PointF(-377, -210),
            new PointF(-78, -212),
            stroke,
            Color.Transparent,
            Color.White,
            12);
        var liveChainSecondLine = liveChainScene.AddCubicCurveSegment(
            0,
            new PointF(-78, -212),
            new PointF(375, -211),
            new PointF(245, 85),
            new PointF(-78, 258),
            stroke,
            Color.Transparent,
            Color.White,
            12);
        var liveDenseFill = liveChainScene.AddPathObjectContours(
            0,
            [
                [
                    new PointF(-6, 215),
                    new PointF(-83, 183),
                    new PointF(-190, -215),
                    new PointF(-182, -215),
                    new PointF(-132, -213),
                    new PointF(-78, -212),
                    new PointF(-37, -211),
                    new PointF(0, -208),
                    new PointF(34, -204),
                    new PointF(65, -199),
                    new PointF(80, -195),
                    new PointF(93, -192),
                    new PointF(106, -187),
                    new PointF(118, -183),
                    new PointF(129, -178),
                    new PointF(140, -173),
                    new PointF(149, -168),
                    new PointF(159, -162),
                    new PointF(167, -157),
                    new PointF(175, -150),
                    new PointF(182, -144),
                    new PointF(188, -137),
                    new PointF(193, -130),
                    new PointF(198, -123),
                    new PointF(203, -116),
                    new PointF(206, -108),
                    new PointF(209, -101),
                    new PointF(212, -93),
                    new PointF(213, -85),
                    new PointF(214, -76),
                    new PointF(215, -68),
                    new PointF(215, -59),
                    new PointF(214, -50),
                    new PointF(213, -41),
                    new PointF(209, -23),
                    new PointF(202, -5),
                    new PointF(194, 14),
                    new PointF(183, 34),
                    new PointF(171, 53),
                    new PointF(156, 73),
                    new PointF(140, 92),
                    new PointF(122, 112),
                    new PointF(102, 131),
                    new PointF(81, 150),
                    new PointF(58, 169),
                    new PointF(33, 188),
                    new PointF(7, 206)
                ]
            ],
            0,
            Color.MediumSeaGreen,
            Color.Transparent,
            47);
        var liveDensePartsBefore = liveChainScene.GetEditableFillBezierSegmentParts(liveDenseFill).Length;
        var liveDenseBoundary = liveChainScene.GetObjectBoundaryContours(liveDenseFill)[0];
        var liveFirstMaximumDistance = liveDenseBoundary
            .Skip(2)
            .Take(4)
            .Max(point =>
            {
                liveChainScene.TryGetClosestPointOnLine(
                    liveChainFirstLine,
                    point,
                    out _,
                    out _,
                    out var distance);
                return distance;
            });
        var liveSecondMaximumDistance = liveDenseBoundary
            .Skip(5)
            .Take(42)
            .Max(point =>
            {
                liveChainScene.TryGetClosestPointOnLine(
                    liveChainSecondLine,
                    point,
                    out _,
                    out _,
                    out var distance);
                return distance;
            });
        liveChainScene.TryGetClosestPointOnLine(
            liveChainSecondLine,
            liveDenseBoundary[46],
            out var liveLastParameter,
            out _,
            out var liveLastDistance);
        liveChainScene.TryGetClosestPointOnLine(
            liveChainSecondLine,
            liveDenseBoundary[0],
            out var liveWrappedParameter,
            out _,
            out var liveWrappedDistance);
        var liveDenseCanonicalized = liveChainScene.CanonicalizeFlattenedFillBoundaryCurves(
            liveDenseFill,
            0);
        var liveDensePartsAfter = liveChainScene.GetEditableFillBezierSegmentParts(liveDenseFill).Length;
        if (liveDensePartsBefore != 47
            || !liveDenseCanonicalized
            || liveDensePartsAfter > 6)
        {
            throw new InvalidOperationException(
                $"The live two-Line Fill boundary remained sampled: before={liveDensePartsBefore}, canonicalized={liveDenseCanonicalized}, after={liveDensePartsAfter}, distances={liveFirstMaximumDistance:F3}/{liveSecondMaximumDistance:F3}, wrap={liveLastParameter:F4}/{liveLastDistance:F3}->{liveWrappedParameter:F4}/{liveWrappedDistance:F3}.");
        }

        // Add the merge target after resolving the stroke-bounded region. Existing
        // Fill edges now intentionally participate in the Fill tool's boundary graph.
        crossingCurveScene.AddObject(
            0,
            new PointF(200, -70),
            new SizeF(480, 340),
            0,
            0,
            Color.MediumSeaGreen,
            Color.Transparent,
            24,
            ShapeKind.Ellipse);
        var normalizedCrossingFills = crossingCurveScene.NormalizePaintForInteractiveCommit(
            [crossingCurveFill],
            frame: 0);
        var normalizedCrossingFill = normalizedCrossingFills.FirstOrDefault(index =>
            (uint)index < crossingCurveScene.ObjectCount
            && crossingCurveScene.FillContainsPoint(index, new PointF(200, 170)), -1);
        if (normalizedCrossingFill >= 0)
        {
            crossingCurveScene.CanonicalizeFlattenedFillBoundaryCurves(normalizedCrossingFill, 0);
        }
        var normalizedCrossingParts = normalizedCrossingFill >= 0
            ? crossingCurveScene.GetPathBezierSegmentParts(normalizedCrossingFill)
            : [];
        var normalizedCrossingPieces = normalizedCrossingFill >= 0
            ? crossingCurveScene.GetExposedFillBezierSegmentPieces(
                normalizedCrossingFill,
                0,
                includeCoincidentStrokes: true)
            : [];
        if (normalizedCrossingFill < 0
            || normalizedCrossingParts.Length > 12
            || normalizedCrossingPieces.Length > 16)
        {
            throw new InvalidOperationException(
                $"Merging a two-intersection cubic Fill restored sampled anchors: fill={normalizedCrossingFill}, parts={normalizedCrossingParts.Length}, pieces={normalizedCrossingPieces.Length}.");
        }

        var fillSegmentIndex = shallowLineLinks[0].BezierSegmentIndex;
        var fillStrokeLinks = shallowCurveScene.CaptureFillBoundaryStrokeLinks(shallowFill, fillSegmentIndex, 0);
        if (!shallowCurveScene.TryGetPathBezierSegment(shallowFill, fillSegmentIndex, out var shallowFillSegment))
        {
            throw new InvalidOperationException("The shallow cubic Fill boundary segment could not be resolved.");
        }
        if (fillStrokeLinks.Length != 1)
        {
            throw new InvalidOperationException("A shallow cubic Fill boundary did not retain its editable Line link.");
        }

        var adjustedControl1 = new PointF(shallowFillSegment.Control1.X, shallowFillSegment.Control1.Y - 20);
        var adjustedControl2 = new PointF(shallowFillSegment.Control2.X, shallowFillSegment.Control2.Y - 20);
        if (!shallowCurveScene.SetPathBezierSegment(
                shallowFill,
                fillSegmentIndex,
                shallowFillSegment.Start,
                adjustedControl1,
                adjustedControl2,
                shallowFillSegment.End,
                rebuildGeometryIndex: false)
            || !shallowCurveScene.UpdateFillBoundaryStrokeLinks(
                fillStrokeLinks,
                shallowFillSegment.Start,
                adjustedControl1,
                adjustedControl2,
                shallowFillSegment.End)
            || shallowCurveScene.CaptureFillBoundaryLineLinks(shallowBoundary, 0) is not [{ BezierSegmentCount: 1 }])
        {
            throw new InvalidOperationException("Editing a shallow cubic Fill boundary separated it from the adjacent Line.");
        }

        var legacyCurveScene = new VectorScene();
        legacyCurveScene.CreateEmpty();
        legacyCurveScene.AddLineSegment(0, new PointF(0, 0), new PointF(240, 0), stroke, Color.Transparent, Color.White, 6);
        legacyCurveScene.AddLineSegment(0, new PointF(240, 0), new PointF(240, 140), stroke, Color.Transparent, Color.White, 6);
        var legacyBoundary = legacyCurveScene.AddCubicCurveSegment(
            0,
            new PointF(240, 140),
            new PointF(165, 145),
            new PointF(75, 75),
            new PointF(0, 140),
            stroke,
            Color.Transparent,
            Color.White,
            12);
        legacyCurveScene.AddLineSegment(0, new PointF(0, 140), new PointF(0, 0), stroke, Color.Transparent, Color.White, 6);
        if (!legacyCurveScene.TryGetClosedStrokeFillRegion(new PointF(120, 60), 0, out var legacyContours))
        {
            throw new InvalidOperationException("The legacy cubic Fill regression could not resolve its closed region.");
        }

        var legacyFill = legacyCurveScene.AddPathObjectContours(
            0,
            legacyContours,
            0,
            Color.Teal,
            Color.Transparent,
            (uint)legacyContours.Sum(contour => contour.Length));
        var legacyPartsBefore = legacyContours.Sum(contour => contour.Length);
        var legacyCanonicalized = legacyCurveScene.CanonicalizeFlattenedFillBoundaryCurves(legacyFill, 0);
        var legacyPartsAfter = legacyCurveScene.GetPathBezierSegmentParts(legacyFill);
        var legacyLinksAfter = legacyCurveScene.CaptureFillBoundaryLineLinks(legacyBoundary, 0);
        var legacyOverlayAfter = legacyCurveScene.GetExposedFillBezierSegmentPieces(
            legacyFill,
            0,
            includeCoincidentStrokes: true);
        if (legacyPartsBefore <= 4
            || !legacyCanonicalized
            || legacyPartsAfter.Length != 4
            || legacyLinksAfter is not [{ BezierSegmentCount: 1 }]
            || legacyOverlayAfter.Length != 4)
        {
            throw new InvalidOperationException(
                $"A sampled legacy Fill boundary was not restored to its neighboring cubic Line: before={legacyPartsBefore}, canonicalized={legacyCanonicalized}, after={legacyPartsAfter.Length}, links={string.Join(',', legacyLinksAfter.Select(link => $"{link.BezierSegmentIndex}+{link.BezierSegmentCount}"))}, overlay={legacyOverlayAfter.Length}.");
        }

        var mergedCurveScene = new VectorScene();
        mergedCurveScene.CreateEmpty();
        mergedCurveScene.AddLineSegment(0, new PointF(0, 0), new PointF(240, 0), stroke, Color.Transparent, Color.White, 6);
        mergedCurveScene.AddLineSegment(0, new PointF(240, 0), new PointF(240, 140), stroke, Color.Transparent, Color.White, 6);
        var mergedBoundary = mergedCurveScene.AddCubicCurveSegment(
            0,
            new PointF(240, 140),
            new PointF(165, 145),
            new PointF(75, 75),
            new PointF(0, 140),
            stroke,
            Color.Transparent,
            Color.White,
            12);
        mergedCurveScene.AddLineSegment(0, new PointF(0, 140), new PointF(0, 0), stroke, Color.Transparent, Color.White, 6);
        if (!mergedCurveScene.TryCreateFillFromClosedStrokeRegion(
                new PointF(120, 80),
                0,
                Color.Teal,
                out var mergedCurveFill,
                out _))
        {
            throw new InvalidOperationException("The shallow cubic merge regression could not create its Fill region.");
        }

        mergedCurveScene.AddObject(
            0,
            new PointF(160, -5),
            new SizeF(240, 70),
            0,
            0,
            Color.Teal,
            Color.Transparent,
            12,
            ShapeKind.Rectangle);
        var normalizedCurveFills = mergedCurveScene.NormalizePaintForInteractiveCommit([mergedCurveFill], frame: 0);
        var normalizedCurveFill = normalizedCurveFills.FirstOrDefault(index =>
            (uint)index < mergedCurveScene.ObjectCount
            && mergedCurveScene.FillContainsPoint(index, new PointF(120, 80)), -1);
        var normalizedCurveParts = normalizedCurveFill >= 0
            ? mergedCurveScene.GetPathBezierSegmentParts(normalizedCurveFill)
            : [];
        var normalizedCurveLinks = mergedCurveScene.CaptureFillBoundaryLineLinks(mergedBoundary, 0);
        if (normalizedCurveFill < 0
            || normalizedCurveParts.Length > 10
            || normalizedCurveLinks is not [{ BezierSegmentCount: 1 }])
        {
            throw new InvalidOperationException(
                $"Interactive Fill normalization flattened a neighboring shallow cubic: fill={normalizedCurveFill}, parts={normalizedCurveParts.Length}, links={string.Join(',', normalizedCurveLinks.Select(link => $"{link.BezierSegmentIndex}+{link.BezierSegmentCount}"))}.");
        }

        var pencilScene = new VectorScene();
        pencilScene.CreateEmpty();
        pencilScene.AddFreehandStroke(
            0,
            new[]
            {
                new PointF(0, 0),
                new PointF(100, 100),
                new PointF(0, 100),
                new PointF(100, 0)
            },
            stroke,
            Color.White,
            brushStroke: false,
            12);
        if (!pencilScene.TryCreateFillFromClosedStrokeRegion(new PointF(50, 78), 0, Color.Coral, out var pencilFill, out _)
            || pencilFill < 0
            || !pencilScene.FillContainsPoint(pencilFill, new PointF(50, 78))
            || pencilScene.FillContainsPoint(pencilFill, new PointF(50, 22)))
        {
            throw new InvalidOperationException("A self-intersecting pencil stroke did not expose an independently fillable loop.");
        }

        var smoothPencilScene = new VectorScene();
        smoothPencilScene.CreateEmpty();
        var smoothPencilPoints = Enumerable.Range(0, 129)
            .Select(index =>
            {
                var angle = MathF.Tau * index / 128f;
                return new PointF(
                    MathF.Cos(angle) * 320,
                    MathF.Sin(angle) * 180 + MathF.Sin(angle * 2) * 24);
            })
            .ToArray();
        smoothPencilScene.AddFreehandStroke(
            0,
            smoothPencilPoints,
            stroke,
            Color.White,
            brushStroke: false,
            129);
        var smoothCreated = smoothPencilScene.TryCreateFillFromClosedStrokeRegion(
            PointF.Empty,
            0,
            Color.Teal,
            out var smoothPencilFill,
            out _);
        var smoothPencilPieces = smoothCreated
            ? smoothPencilScene.GetExposedFillBezierSegmentPieces(
                smoothPencilFill,
                0,
                includeCoincidentStrokes: true)
            : [];
        if (!smoothCreated
            || smoothPencilFill < 0
            || !smoothPencilScene.TryGetPathBezierWorldContours(smoothPencilFill, out _)
            || smoothPencilPieces.Length is < 3 or > 12
            || !smoothPencilScene.FillContainsPoint(smoothPencilFill, PointF.Empty))
        {
            throw new InvalidOperationException(
                $"A smooth Pencil-bounded Fill retained dense sampled edit anchors: created={smoothCreated}/{smoothPencilFill}, source={smoothPencilPoints.Length}, pieces={smoothPencilPieces.Length}.");
        }

        var overlappingPencilScene = new VectorScene();
        overlappingPencilScene.CreateEmpty();
        overlappingPencilScene.AddFreehandStroke(
            0,
            smoothPencilPoints.Select(point => new PointF(point.X, point.Y + 100)).ToArray(),
            stroke,
            Color.White,
            brushStroke: false,
            129);
        if (!overlappingPencilScene.TryCreateFillFromClosedStrokeRegion(
                new PointF(0, 200),
                0,
                Color.Teal,
                out var overlappingFill,
                out _))
        {
            throw new InvalidOperationException("The overlapping Pencil Fill regression could not create its closed region.");
        }

        overlappingPencilScene.AddObject(
            0,
            new PointF(0, -140),
            new SizeF(520, 320),
            0,
            0,
            Color.Teal,
            Color.Transparent,
            12,
            ShapeKind.Ellipse);
        var normalizedOverlappingFills = overlappingPencilScene.NormalizePaintForInteractiveCommit(
            [overlappingFill],
            frame: 0);
        var normalizedOverlappingFill = normalizedOverlappingFills.SingleOrDefault(-1);
        var overlappingPieces = normalizedOverlappingFill >= 0
            ? overlappingPencilScene.GetExposedFillBezierSegmentPieces(
                normalizedOverlappingFill,
                0,
                includeCoincidentStrokes: true)
            : [];
        if (normalizedOverlappingFill < 0
            || overlappingPieces.Length is < 3 or > 20
            || !overlappingPencilScene.FillContainsPoint(normalizedOverlappingFill, new PointF(0, -140))
            || !overlappingPencilScene.FillContainsPoint(normalizedOverlappingFill, new PointF(0, 200)))
        {
            throw new InvalidOperationException(
                $"Merging a Pencil-bounded Fill with an existing curved Fill restored dense edit anchors: fill={normalizedOverlappingFill}, pieces={overlappingPieces.Length}, objects={overlappingPencilScene.ObjectCount}.");
        }

        Console.WriteLine("closed_fill_region_regression=ok");
    }

    private static void RunBezierOperationPerformanceRegression()
    {
        const int addIterations = 256;
        var addScene = new VectorScene();
        addScene.CreateEmpty();
        for (var iteration = 0; iteration < 16; iteration++) AddCurve(addScene, iteration);
        var addMeasurement = Measure(addIterations, iteration => AddCurve(addScene, iteration + 16));
        if (addMeasurement.P95Milliseconds > 1)
        {
            throw new InvalidOperationException(
                $"Incremental Bezier drawing exceeded its budget: p95Ms={addMeasurement.P95Milliseconds:0.000}.");
        }

        var (fillEditScene, fillEdit, _) = CreateSplitScene();
        var fillEditPieces = fillEditScene.GetExposedFillBezierSegmentPieces(
            fillEdit,
            0,
            includeCoincidentStrokes: true);
        if (fillEditPieces.Length == 0
            || !fillEditScene.TryGetPathBezierSegment(fillEdit, 3, out var fillEditSegment))
        {
            throw new InvalidOperationException("The Bezier interaction benchmark could not resolve its editable Fill edge.");
        }

        FillBezierSegmentPiece[] refreshedPieces = fillEditPieces;
        void ApplyFillEdgeUpdate(int iteration)
        {
            var controlX = iteration % 2 == 0 ? -205f : -275f;
            if (!fillEditScene.SetPathBezierSegmentForPreview(
                    fillEdit,
                    3,
                    fillEditSegment.Start,
                    new PointF(controlX, fillEditSegment.Control1.Y),
                    new PointF(controlX, fillEditSegment.Control2.Y),
                    fillEditSegment.End))
            {
                throw new InvalidOperationException("The Bezier interaction benchmark could not update its Fill edge.");
            }

            refreshedPieces = fillEditScene.RefreshFillBezierSegmentPiecesForPreview(
                fillEdit,
                refreshedPieces,
                3,
                EditHandleKind.BezierControl);
            if (refreshedPieces.Length == 0)
            {
                throw new InvalidOperationException("The Bezier interaction benchmark lost its Fill-edge overlay pieces.");
            }
        }

        for (var iteration = 0; iteration < 16; iteration++) ApplyFillEdgeUpdate(iteration);
        var fillEdgeMeasurement = Measure(256, ApplyFillEdgeUpdate);
        fillEditScene.CompletePathBezierPreview(fillEdit, rebuildGeometryIndex: false);
        fillEditScene.CompleteDeferredBuild();
        if (fillEdgeMeasurement.P95Milliseconds > 1)
        {
            throw new InvalidOperationException(
                $"Fill Bezier interaction exceeded its budget: p95Ms={fillEdgeMeasurement.P95Milliseconds:0.000}.");
        }

        const int denseFillNodeCount = 2_048;
        var denseFillScene = new VectorScene();
        denseFillScene.CreateEmpty();
        var denseFillAnchors = Enumerable.Range(0, denseFillNodeCount)
            .Select(index =>
            {
                var angle = MathF.Tau * index / denseFillNodeCount;
                var radius = 900f + MathF.Sin(angle * 17) * 80f;
                return new PointF(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius);
            })
            .ToArray();
        var denseFillNodes = Enumerable.Range(0, denseFillNodeCount)
            .Select(index =>
            {
                var previous = denseFillAnchors[(index - 1 + denseFillNodeCount) % denseFillNodeCount];
                var current = denseFillAnchors[index];
                var next = denseFillAnchors[(index + 1) % denseFillNodeCount];
                return new PathBezierNode(
                    current,
                    new PointF(
                        previous.X + (current.X - previous.X) * 2f / 3f,
                        previous.Y + (current.Y - previous.Y) * 2f / 3f),
                    new PointF(
                        current.X + (next.X - current.X) / 3f,
                        current.Y + (next.Y - current.Y) / 3f));
            })
            .ToArray();
        var denseFill = denseFillScene.AppendPathBezierObjectContours(
            0,
            [denseFillNodes],
            0,
            Color.Teal,
            Color.Transparent,
            denseFillNodeCount);
        denseFillScene.CompleteDeferredBuild();
        var denseFillPieces = denseFillScene.GetExposedFillBezierSegmentPieces(
            denseFill,
            0,
            includeCoincidentStrokes: true);
        var denseFillPieceCount = denseFillPieces.Length;
        if (denseFillPieceCount < denseFillNodeCount
            || !denseFillScene.TryGetPathBezierSegment(denseFill, 0, out var denseFillSegment))
        {
            throw new InvalidOperationException(
                $"The dense Fill drag benchmark could not resolve its editable boundary: pieces={denseFillPieces.Length}.");
        }

        void ApplyDenseFillEdgePreview(int iteration)
        {
            var offset = iteration % 2 == 0 ? 12f : -12f;
            if (!denseFillScene.SetPathBezierSegmentForPreview(
                    denseFill,
                    0,
                    denseFillSegment.Start,
                    new PointF(denseFillSegment.Control1.X + offset, denseFillSegment.Control1.Y),
                    denseFillSegment.Control2,
                    denseFillSegment.End))
            {
                throw new InvalidOperationException("The dense Fill drag benchmark could not update its active edge.");
            }

            denseFillPieces = denseFillScene.RefreshFillBezierSegmentPiecesForPreview(
                denseFill,
                denseFillPieces,
                0,
                EditHandleKind.BezierControl);
            if (denseFillPieces.Length != denseFillPieceCount)
            {
                throw new InvalidOperationException("The dense Fill drag benchmark lost overlay segments.");
            }
        }

        for (var iteration = 0; iteration < 16; iteration++) ApplyDenseFillEdgePreview(iteration);
        var denseFillEdgeMeasurement = Measure(256, ApplyDenseFillEdgePreview);
        denseFillScene.CompletePathBezierPreview(denseFill, rebuildGeometryIndex: false);
        denseFillScene.CompleteDeferredBuild();
        if (denseFillEdgeMeasurement.P95Milliseconds > 1)
        {
            throw new InvalidOperationException(
                $"Dense Fill drag preview exceeded its budget: p95Ms={denseFillEdgeMeasurement.P95Milliseconds:0.000}.");
        }

        var (partitionScene, partitionFill, _) = CreateSplitScene();
        if (!partitionScene.TryGetPathBezierSegment(partitionFill, 3, out var partitionSegment))
        {
            throw new InvalidOperationException("The Fill partition benchmark could not resolve its editable edge.");
        }

        DrawingFillPartGeometry[] partitionParts = [];
        void RebuildPartition(int iteration)
        {
            var controlX = iteration % 2 == 0 ? -210f : -270f;
            if (!partitionScene.SetPathBezierSegment(
                    partitionFill,
                    3,
                    partitionSegment.Start,
                    new PointF(controlX, partitionSegment.Control1.Y),
                    new PointF(controlX, partitionSegment.Control2.Y),
                    partitionSegment.End,
                    rebuildGeometryIndex: false))
            {
                throw new InvalidOperationException("The Fill partition benchmark could not update its source edge.");
            }

            partitionParts = partitionScene.GetFillParts(partitionFill, 0);
            if (partitionParts.Length != 2)
            {
                throw new InvalidOperationException(
                    $"The Fill partition benchmark produced {partitionParts.Length} parts instead of 2.");
            }
        }

        for (var iteration = 0; iteration < 8; iteration++) RebuildPartition(iteration);
        var partitionMeasurement = Measure(128, RebuildPartition);
        partitionScene.CompleteDeferredBuild();
        if (partitionMeasurement.P95Milliseconds > 1)
        {
            throw new InvalidOperationException(
                $"Cold Fill partition interaction exceeded its budget: p95Ms={partitionMeasurement.P95Milliseconds:0.000}.");
        }

        var (lineToFillScene, lineToFill, boundaryLine) = CreateLinkedScene();
        var lineToFillLinks = lineToFillScene.CaptureFillBoundaryLineLinks(boundaryLine, 0);
        if (lineToFillLinks.Length == 0
            || !lineToFillScene.TryGetLineCubic(
                boundaryLine,
                out var linkedStart,
                out _,
                out _,
                out var linkedEnd))
        {
            throw new InvalidOperationException("The Line-to-Fill benchmark could not capture its shared cubic.");
        }

        void ApplyLineToFill(int iteration)
        {
            var controlY = iteration % 2 == 0 ? -138f : -182f;
            lineToFillScene.SetLineEndpoint(
                boundaryLine,
                startEndpoint: true,
                linkedStart,
                linkedEnd,
                new PointF(-80, controlY),
                new PointF(80, controlY),
                keepStraight: false);
            if (!lineToFillScene.UpdateFillBoundaryLineLinks(lineToFillLinks, rebuildGeometryIndex: false))
            {
                throw new InvalidOperationException("The Line-to-Fill benchmark did not update its shared boundary.");
            }
        }

        for (var iteration = 0; iteration < 16; iteration++) ApplyLineToFill(iteration);
        var lineToFillMeasurement = Measure(256, ApplyLineToFill);
        lineToFillScene.CompleteDeferredBuild();
        AssertSharedCurve(lineToFillScene, lineToFill, boundaryLine, "Line-to-Fill");

        var (fillToLineScene, fillToLine, fillBoundaryLine) = CreateLinkedScene();
        var fillToLineLinks = fillToLineScene.CaptureFillBoundaryStrokeLinks(fillToLine, 0, 0);
        if (fillToLineLinks.Length == 0
            || !fillToLineScene.TryGetPathBezierSegment(fillToLine, 0, out var linkedFillSegment))
        {
            throw new InvalidOperationException("The Fill-to-Line benchmark could not capture its shared cubic.");
        }

        void ApplyFillToLine(int iteration)
        {
            var controlY = iteration % 2 == 0 ? -140f : -180f;
            var control1 = new PointF(linkedFillSegment.Control1.X, controlY);
            var control2 = new PointF(linkedFillSegment.Control2.X, controlY);
            if (!fillToLineScene.SetPathBezierSegment(
                    fillToLine,
                    0,
                    linkedFillSegment.Start,
                    control1,
                    control2,
                    linkedFillSegment.End,
                    rebuildGeometryIndex: false)
                || !fillToLineScene.UpdateFillBoundaryStrokeLinks(
                    fillToLineLinks,
                    linkedFillSegment.Start,
                    control1,
                    control2,
                    linkedFillSegment.End,
                    rebuildGeometryIndex: false))
            {
                throw new InvalidOperationException("The Fill-to-Line benchmark did not update its shared boundary.");
            }
        }

        for (var iteration = 0; iteration < 16; iteration++) ApplyFillToLine(iteration);
        var fillToLineMeasurement = Measure(256, ApplyFillToLine);
        fillToLineScene.CompleteDeferredBuild();
        AssertSharedCurve(fillToLineScene, fillToLine, fillBoundaryLine, "Fill-to-Line");

        if (lineToFillMeasurement.P95Milliseconds > 1
            || fillToLineMeasurement.P95Milliseconds > 1)
        {
            throw new InvalidOperationException(
                $"Shared Fill/Line interaction exceeded its budget: lineToFillP95={lineToFillMeasurement.P95Milliseconds:0.000} ms, fillToLineP95={fillToLineMeasurement.P95Milliseconds:0.000} ms.");
        }

        WriteMeasurement("bezier_add_drawing", addMeasurement);
        WriteMeasurement("bezier_fill_edge_update", fillEdgeMeasurement);
        WriteMeasurement("dense_fill_edge_preview", denseFillEdgeMeasurement);
        WriteMeasurement("bezier_fill_partition", partitionMeasurement);
        WriteMeasurement("bezier_line_to_fill", lineToFillMeasurement);
        WriteMeasurement("bezier_fill_to_line", fillToLineMeasurement);

        static void AddCurve(VectorScene scene, int iteration)
        {
            var y = -6000 + iteration * 24f;
            scene.AddCubicCurveSegment(
                0,
                new PointF(-320, y),
                new PointF(-120, y - 28),
                new PointF(120, y + 28),
                new PointF(320, y),
                VectorUnits.StrokePointsToUnits(2),
                Color.Transparent,
                Color.White,
                8);
        }

        static (VectorScene Scene, int Fill, int Cutter) CreateSplitScene()
        {
            var scene = new VectorScene();
            scene.CreateEmpty();
            var fill = AppendRectangleFill(scene);
            var cutter = scene.AddCubicCurveSegment(
                0,
                new PointF(-340, 0),
                new PointF(-120, 36),
                new PointF(120, -36),
                new PointF(340, 0),
                VectorUnits.StrokePointsToUnits(2),
                Color.Transparent,
                Color.White,
                8);
            scene.CompleteDeferredBuild();
            return (scene, fill, cutter);
        }

        static (VectorScene Scene, int Fill, int Line) CreateLinkedScene()
        {
            var scene = new VectorScene();
            scene.CreateEmpty();
            var fill = AppendRectangleFill(scene);
            var line = scene.AddCubicCurveSegment(
                0,
                new PointF(-240, -160),
                new PointF(-80, -160),
                new PointF(80, -160),
                new PointF(240, -160),
                VectorUnits.StrokePointsToUnits(2),
                Color.Transparent,
                Color.White,
                8);
            scene.CompleteDeferredBuild();
            return (scene, fill, line);
        }

        static int AppendRectangleFill(VectorScene scene)
        {
            return scene.AppendPathBezierObjectContours(
                0,
                [[
                    new PathBezierNode(new PointF(-240, -160), new PointF(-240, -53), new PointF(-80, -160)),
                    new PathBezierNode(new PointF(240, -160), new PointF(80, -160), new PointF(240, -53)),
                    new PathBezierNode(new PointF(240, 160), new PointF(240, 53), new PointF(80, 160)),
                    new PathBezierNode(new PointF(-240, 160), new PointF(-80, 160), new PointF(-240, 53))
                ]],
                0,
                Color.Teal,
                Color.Transparent,
                16);
        }

        static void AssertSharedCurve(VectorScene scene, int fill, int line, string operation)
        {
            if (!scene.TryGetPathBezierSegment(fill, 0, out var fillSegment)
                || !scene.TryGetLineCubic(line, out var start, out var control1, out var control2, out var end)
                || !PointsWithin(fillSegment.Start, start, 0.01f)
                || !PointsWithin(fillSegment.Control1, control1, 0.01f)
                || !PointsWithin(fillSegment.Control2, control2, 0.01f)
                || !PointsWithin(fillSegment.End, end, 0.01f))
            {
                throw new InvalidOperationException($"{operation} did not preserve one shared cubic formula.");
            }
        }

        static void WriteMeasurement(
            string prefix,
            (double AverageMilliseconds, double P95Milliseconds, double AllocatedBytesPerOperation) measurement)
        {
            Console.WriteLine($"{prefix}_avg_ms={measurement.AverageMilliseconds:0.000}");
            Console.WriteLine($"{prefix}_p95_ms={measurement.P95Milliseconds:0.000}");
            Console.WriteLine(
                $"{prefix}_allocated_bytes_per_op={measurement.AllocatedBytesPerOperation:0.0}");
        }

        static (double AverageMilliseconds, double P95Milliseconds, double AllocatedBytesPerOperation) Measure(
            int iterations,
            Action<int> operation)
        {
            const int batchSize = 4;
            var batchMilliseconds = new List<double>((iterations + batchSize - 1) / batchSize);
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            for (var batchStart = 0; batchStart < iterations; batchStart += batchSize)
            {
                var batchIterations = Math.Min(batchSize, iterations - batchStart);
                var batchWatch = Stopwatch.StartNew();
                for (var offset = 0; offset < batchIterations; offset++)
                {
                    operation(batchStart + offset);
                }
                batchWatch.Stop();
                batchMilliseconds.Add(batchWatch.Elapsed.TotalMilliseconds / batchIterations);
            }
            watch.Stop();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            batchMilliseconds.Sort();
            var p95Index = Math.Clamp(
                (int)Math.Ceiling(batchMilliseconds.Count * 0.95) - 1,
                0,
                batchMilliseconds.Count - 1);
            return (
                watch.Elapsed.TotalMilliseconds / iterations,
                batchMilliseconds[p95Index],
                allocated / (double)iterations);
        }
    }

}
