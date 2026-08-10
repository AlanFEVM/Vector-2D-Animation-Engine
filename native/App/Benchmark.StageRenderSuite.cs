using System.Diagnostics;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunLayerBlendRegression()
    {
        var backdrop = Color.FromArgb(255, 64, 128, 192);
        var source = Color.FromArgb(255, 128, 64, 32);
        AssertColorNear(
            LayerBlendCompositor.CompositeColorForRegression(backdrop, source, LayerBlendMode.Normal),
            source,
            "Normal");
        AssertColorNear(
            LayerBlendCompositor.CompositeColorForRegression(backdrop, source, LayerBlendMode.Multiply),
            Color.FromArgb(255, 32, 32, 24),
            "Multiply");
        AssertColorNear(
            LayerBlendCompositor.CompositeColorForRegression(backdrop, source, LayerBlendMode.Screen),
            Color.FromArgb(255, 160, 160, 200),
            "Screen");
        AssertColorNear(
            LayerBlendCompositor.CompositeColorForRegression(backdrop, source, LayerBlendMode.Difference),
            Color.FromArgb(255, 64, 64, 160),
            "Difference");

        var translucentBackdrop = Color.FromArgb(120, 40, 100, 180);
        var translucentSource = Color.FromArgb(180, 220, 80, 30);
        var sourceAlpha = translucentSource.A / 255f * 0.75f;
        var backdropAlpha = translucentBackdrop.A / 255f;
        var expectedAlpha = (int)MathF.Round((sourceAlpha + backdropAlpha * (1f - sourceAlpha)) * 255f);
        foreach (var mode in Enum.GetValues<LayerBlendMode>())
        {
            var result = LayerBlendCompositor.CompositeColorForRegression(
                translucentBackdrop,
                translucentSource,
                mode,
                opacity: 0.75f,
                dissolveSeed: 0x12345678u,
                x: 11,
                y: 7);
            if (mode != LayerBlendMode.Dissolve && Math.Abs(result.A - expectedAlpha) > 1)
            {
                throw new InvalidOperationException(
                    $"Layer blend mode {mode} produced alpha {result.A}; expected {expectedAlpha}.");
            }
        }

        var dissolveA = LayerBlendCompositor.CompositeColorForRegression(
            translucentBackdrop,
            translucentSource,
            LayerBlendMode.Dissolve,
            opacity: 0.55f,
            dissolveSeed: 0x5a17c9e3u,
            x: 29,
            y: 31);
        var dissolveB = LayerBlendCompositor.CompositeColorForRegression(
            translucentBackdrop,
            translucentSource,
            LayerBlendMode.Dissolve,
            opacity: 0.55f,
            dissolveSeed: 0x5a17c9e3u,
            x: 29,
            y: 31);
        if (dissolveA.ToArgb() != dissolveB.ToArgb())
        {
            throw new InvalidOperationException("Dissolve blend mode changed between identical samples.");
        }

        var layeredScene = new VectorScene();
        layeredScene.CreateEmpty();
        var lowerLayer = layeredScene.AddLayer("Backdrop");
        layeredScene.SetLayerBlendMode(0, LayerBlendMode.Multiply);
        using (var layeredBitmap = new Bitmap(12, 8, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
        using (var layeredGraphics = Graphics.FromImage(layeredBitmap))
        using (var compositor = new LayerBlendCompositor(layeredBitmap.Size))
        {
            compositor.CompositeTo(layeredGraphics, layeredScene, (graphics, layer) =>
            {
                using var brush = new SolidBrush(layer == lowerLayer ? Color.CornflowerBlue : Color.Coral);
                graphics.FillRectangle(brush, layer == lowerLayer ? new Rectangle(0, 0, 12, 8) : new Rectangle(0, 0, 6, 8));
            });

            AssertColorNear(layeredBitmap.GetPixel(9, 4), Color.CornflowerBlue, "Multiply lower-layer preservation");
        }

        var batchScene = new VectorScene();
        batchScene.CreateEmpty();
        var batchMiddleLayer = batchScene.AddLayer("Empty blend boundary");
        var batchBottomLayer = batchScene.AddLayer("Batch bottom");
        batchScene.SetLayerBlendMode(batchMiddleLayer, LayerBlendMode.Multiply);
        var batchesWithoutBlendContent = LayerBlendCompositor.GetSpatialLayerBatches(
            batchScene,
            layer => layer != batchMiddleLayer);
        if (batchesWithoutBlendContent.Length != 1
            || !batchesWithoutBlendContent[0].SequenceEqual([batchBottomLayer, 0]))
        {
            throw new InvalidOperationException(
                "An empty non-Normal layer split an otherwise continuous spatial batch.");
        }
        var batchesWithBlendContent = LayerBlendCompositor.GetSpatialLayerBatches(
            batchScene,
            _ => true);
        if (batchesWithBlendContent.Length != 3
            || batchesWithBlendContent[1].Length != 1
            || batchesWithBlendContent[1][0] != batchMiddleLayer)
        {
            throw new InvalidOperationException(
                "A drawable non-Normal layer did not remain an isolated compositing boundary.");
        }

        var stageScene = new VectorScene();
        stageScene.CreateEmpty();
        var stageLowerLayer = stageScene.AddLayer("Stage Backdrop");
        stageScene.AddObject(0, PointF.Empty, new SizeF(2_000, 2_000), 0, 0, Color.Coral, Color.Transparent, 12, ShapeKind.Rectangle);
        stageScene.AddObject(stageLowerLayer, PointF.Empty, new SizeF(4_000, 3_000), 0, 0, Color.CornflowerBlue, Color.Transparent, 12, ShapeKind.Rectangle);
        stageScene.SetLayerBlendMode(0, LayerBlendMode.Multiply);
        using (var stage = new StageControl(stageScene) { ClientSize = new Size(320, 240), WorldGridOpacity = 0 })
        using (var stageBitmap = new Bitmap(stage.ClientSize.Width, stage.ClientSize.Height))
        using (var stageGraphics = Graphics.FromImage(stageBitmap))
        {
            var drawGdi = typeof(StageControl).GetMethod(
                "DrawGdi",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("The GDI layer-blend regression entry point could not be located.");
            drawGdi.Invoke(stage, [stageGraphics]);
            var lowerOnlyPoint = Point.Round(stage.WorldToScreen(1_500, 0));
            AssertColorNear(
                stageBitmap.GetPixel(lowerOnlyPoint.X, lowerOnlyPoint.Y),
                Color.CornflowerBlue,
                "Stage lower-layer preservation");
        }

        static void AssertColorNear(Color actual, Color expected, string mode)
        {
            if (Math.Abs(actual.A - expected.A) <= 1
                && Math.Abs(actual.R - expected.R) <= 1
                && Math.Abs(actual.G - expected.G) <= 1
                && Math.Abs(actual.B - expected.B) <= 1)
            {
                return;
            }

            throw new InvalidOperationException(
                $"{mode} blend equation returned {actual.ToArgb():X8}; expected {expected.ToArgb():X8}.");
        }
    }

    private static void RunSceneReferenceRenderRegression()
    {
        RunSpatialFrontProjectionRegression();

        const int stageWidth = 360;
        const int stageHeight = 260;
        const float narrowStroke = 80f;
        const float wideStroke = 320f;
        var background = Color.FromArgb(255, 17, 19, 21);
        var targetColor = Color.FromArgb(255, 36, 184, 96);
        var target = new VectorScene();
        target.CreateEmpty();
        var targetObject = target.AddObject(
            0,
            PointF.Empty,
            new SizeF(4_000, 2_400),
            0,
            0,
            targetColor,
            Color.Transparent,
            12,
            ShapeKind.Rectangle);
        var targetObjects = new HashSet<int> { targetObject };
        using var stage = new StageControl(target)
        {
            ClientSize = new Size(stageWidth, stageHeight),
            BackColor = background,
            WorldGridOpacity = 0
        };
        var drawGdi = typeof(StageControl).GetMethod(
            "DrawGdi",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The scene-reference GDI regression entry point could not be located.");

        stage.ConfigureReferenceView(null, SceneDimension.ThreeD);
        var projectedContours = stage.GetReference3DProjectedContours(targetObject);
        if (projectedContours.Length == 0
            || !projectedContours.Any(contour => contour.Closed && contour.Points.Length >= 3)
            || projectedContours.SelectMany(contour => contour.Points)
                .Any(point => !float.IsFinite(point.X) || !float.IsFinite(point.Y))
            || !stage.TryProjectScenePosition(System.Numerics.Vector3.Zero, out var projectedCenter, out var projectedDepth)
            || projectedDepth < StageControl.ReferenceNearPlane)
        {
            throw new InvalidOperationException("A flat 2D scene object produced no finite 3D reference projection.");
        }
        using (var projectedBitmap = RenderGdi())
        {
            AssertPixelNear(
                Sample(projectedBitmap, projectedCenter),
                targetColor,
                "The GDI reference projection did not draw flat 2D scene content");
        }

        var filledMask = new VectorScene();
        filledMask.CreateEmpty();
        filledMask.AddObject(
            0,
            new PointF(-800, 0),
            new SizeF(1_200, 1_200),
            0,
            0,
            Color.White,
            Color.Transparent,
            6,
            ShapeKind.Rectangle);
        var filledClip = new SceneCompositionMaskClip(target, filledMask, 0, targetObjects);
        var insideMask = new PointF(-800, 0);
        var outsideMask = new PointF(800, 0);
        if (stage.GetSceneCompositionMaskWorldContours(filledClip).Length == 0)
        {
            throw new InvalidOperationException("A populated scene mask produced no world-space clipping contours.");
        }
        stage.SetSceneCompositionMaskClips([filledClip]);

        stage.ConfigureReferenceView(null, SceneDimension.TwoD);
        using (var flatBitmap = RenderGdi())
        {
            AssertPixelNear(
                Sample(flatBitmap, stage.WorldToScreen(insideMask.X, insideMask.Y)),
                targetColor,
                "The 2D scene mask removed content inside its clipping area");
            AssertPixelNear(
                Sample(flatBitmap, stage.WorldToScreen(outsideMask.X, outsideMask.Y)),
                background,
                "The 2D scene mask leaked content outside its clipping area");
        }

        stage.ConfigureReferenceView(null, SceneDimension.ThreeD);
        if (!stage.TryProjectScenePosition(
                new System.Numerics.Vector3(insideMask.X, insideMask.Y, 0),
                out var projectedInsideMask,
                out _)
            || !stage.TryProjectScenePosition(
                new System.Numerics.Vector3(outsideMask.X, outsideMask.Y, 0),
                out var projectedOutsideMask,
                out _))
        {
            throw new InvalidOperationException("Scene-mask sample points could not be projected into the 3D reference view.");
        }
        using (var spatialBitmap = RenderGdi())
        {
            AssertPixelNear(
                Sample(spatialBitmap, projectedInsideMask),
                targetColor,
                "The 3D reference scene mask removed content inside its clipping area");
            AssertPixelNear(
                Sample(spatialBitmap, projectedOutsideMask),
                background,
                "The 3D reference scene mask leaked content outside its clipping area");
        }
        if (!stage.TryHitTestProjectedObject(Point.Round(projectedInsideMask), 1f, out var projectedHit)
            || projectedHit != targetObject
            || stage.TryHitTestProjectedObject(Point.Round(projectedOutsideMask), 1f, out _))
        {
            throw new InvalidOperationException("Projected scene-mask hit testing disagreed with rendered clipping.");
        }

        var emptyMask = new VectorScene();
        emptyMask.CreateEmpty();
        AssertEmptyMaskClip(emptyMask, 0, "empty");

        var hiddenMask = new VectorScene();
        hiddenMask.CreateEmpty();
        hiddenMask.AddObject(
            0,
            PointF.Empty,
            new SizeF(1_200, 1_200),
            0,
            0,
            Color.White,
            Color.Transparent,
            6,
            ShapeKind.Rectangle);
        if (!hiddenMask.SetLayerVisible(0, false))
        {
            throw new InvalidOperationException("The hidden scene-mask regression could not hide its drawing layer.");
        }
        AssertEmptyMaskClip(hiddenMask, 0, "hidden");

        var blankMask = new VectorScene();
        blankMask.CreateEmpty();
        blankMask.AddObject(
            0,
            PointF.Empty,
            new SizeF(1_200, 1_200),
            0,
            0,
            Color.White,
            Color.Transparent,
            6,
            ShapeKind.Rectangle);
        if (!blankMask.InsertTimelineBlankKeyframe(0, 5)
            || stage.GetSceneCompositionMaskWorldContours(
                new SceneCompositionMaskClip(target, blankMask, 4, targetObjects)).Length == 0)
        {
            throw new InvalidOperationException("The blank scene-mask regression did not establish populated and blank exposures.");
        }
        AssertEmptyMaskClip(blankMask, 5, "blank-keyframe");

        AssertStrokeMaskWidth(
            "Line",
            CreateLineMask(narrowStroke),
            CreateLineMask(wideStroke));
        AssertStrokeMaskWidth(
            "Freeform",
            CreateFreeformMask(narrowStroke),
            CreateFreeformMask(wideStroke));

        Console.WriteLine("scene_reference_projection_2d_content=ok");
        Console.WriteLine("scene_mask_2d_3d_clip=ok");
        Console.WriteLine("scene_mask_empty_hidden_blank=ok");
        Console.WriteLine("scene_mask_line_freeform_stroke_area=ok");

        Bitmap RenderGdi()
        {
            var bitmap = new Bitmap(stage.ClientSize.Width, stage.ClientSize.Height);
            using var graphics = Graphics.FromImage(bitmap);
            drawGdi.Invoke(stage, [graphics]);
            return bitmap;
        }

        void AssertEmptyMaskClip(VectorScene maskScene, int frame, string label)
        {
            var clip = new SceneCompositionMaskClip(target, maskScene, frame, targetObjects);
            if (stage.GetSceneCompositionMaskWorldContours(clip).Length != 0)
            {
                throw new InvalidOperationException($"The {label} scene mask produced non-empty clipping contours.");
            }
            stage.SetSceneCompositionMaskClips([clip]);

            stage.ConfigureReferenceView(null, SceneDimension.TwoD);
            using (var flatBitmap = RenderGdi())
            {
                AssertPixelNear(
                    Sample(flatBitmap, stage.WorldToScreen(0, 0)),
                    background,
                    $"The {label} scene mask did not produce an empty 2D clip");
            }

            stage.ConfigureReferenceView(null, SceneDimension.ThreeD);
            if (!stage.TryProjectScenePosition(System.Numerics.Vector3.Zero, out var center, out _))
            {
                throw new InvalidOperationException($"The {label} scene-mask center could not be projected.");
            }
            using var spatialBitmap = RenderGdi();
            AssertPixelNear(
                Sample(spatialBitmap, center),
                background,
                $"The {label} scene mask did not produce an empty 3D clip");
        }

        void AssertStrokeMaskWidth(string label, VectorScene narrowMask, VectorScene wideMask)
        {
            var narrow = MaskMetrics(narrowMask);
            var wide = MaskMetrics(wideMask);
            if (narrow.ContourCount == 0
                || narrow.Area <= 1
                || narrow.Thickness <= 1
                || wide.ContourCount == 0
                || !float.IsFinite(wide.Area)
                || !float.IsFinite(wide.Thickness)
                || wide.Area <= narrow.Area * 2.5f
                || wide.Thickness <= narrow.Thickness * 2.5f)
            {
                throw new InvalidOperationException(
                    $"The {label} mask stroke did not generate width-scaled area: "
                    + $"area={narrow.Area:0.###}/{wide.Area:0.###}, thickness={narrow.Thickness:0.###}/{wide.Thickness:0.###}.");
            }
        }

        (int ContourCount, float Area, float Thickness) MaskMetrics(VectorScene maskScene)
        {
            var contours = stage.GetSceneCompositionMaskWorldContours(
                new SceneCompositionMaskClip(target, maskScene, 0, targetObjects));
            var points = contours.SelectMany(contour => contour).ToArray();
            if (points.Length == 0) return (0, 0, 0);
            var minimumY = points.Min(point => point.Y);
            var maximumY = points.Max(point => point.Y);
            var area = contours.Sum(contour => Math.Abs(SignedArea(contour)));
            return (contours.Length, area, maximumY - minimumY);
        }

        static float SignedArea(IReadOnlyList<PointF> contour)
        {
            if (contour.Count < 3) return 0;
            double area = 0;
            var previous = contour[^1];
            foreach (var current in contour)
            {
                area += previous.X * current.Y - current.X * previous.Y;
                previous = current;
            }
            return (float)(area * 0.5d);
        }

        static VectorScene CreateLineMask(float stroke)
        {
            var scene = new VectorScene();
            scene.CreateEmpty();
            scene.AddLineSegment(
                0,
                new PointF(-600, 0),
                new PointF(600, 0),
                stroke,
                Color.Transparent,
                Color.White,
                8);
            return scene;
        }

        static VectorScene CreateFreeformMask(float stroke)
        {
            var scene = new VectorScene();
            scene.CreateEmpty();
            scene.AddFreehandStroke(
                0,
                [new PointF(-600, 0), new PointF(0, 0), new PointF(600, 0)],
                stroke,
                Color.White,
                brushStroke: false,
                8);
            return scene;
        }

        static Color Sample(Bitmap bitmap, PointF point)
        {
            return bitmap.GetPixel(
                Math.Clamp((int)MathF.Round(point.X), 0, bitmap.Width - 1),
                Math.Clamp((int)MathF.Round(point.Y), 0, bitmap.Height - 1));
        }

        static void AssertPixelNear(Color actual, Color expected, string message)
        {
            if (Math.Abs(actual.A - expected.A) <= 1
                && Math.Abs(actual.R - expected.R) <= 1
                && Math.Abs(actual.G - expected.G) <= 1
                && Math.Abs(actual.B - expected.B) <= 1)
            {
                return;
            }
            throw new InvalidOperationException(
                $"{message}: actual={actual.ToArgb():X8}, expected={expected.ToArgb():X8}.");
        }
    }

    private static void RunSpatialFrontProjectionRegression()
    {
        const int stageWidth = 360;
        const int stageHeight = 260;
        var background = Color.FromArgb(255, 17, 19, 21);
        var planarColor = Color.FromArgb(255, 38, 164, 226);
        var scene = new VectorScene();
        scene.CreateEmpty();
        var planarObject = scene.AddObject(
            0,
            PointF.Empty,
            new SizeF(4_000, 2_400),
            0,
            0,
            planarColor,
            Color.Transparent,
            12,
            ShapeKind.Rectangle);
        using var stage = CreateStage(scene);
        var drawGdi = typeof(StageControl).GetMethod(
            "DrawGdi",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The spatial-front GDI regression entry point could not be located.");

        stage.ConfigureReferenceView(null, SceneDimension.ThreeD);
        stage.SetReferenceCameraOrientation(0.83f, -0.41f);
        stage.ConfigureReferenceView(null, SceneDimension.TwoD);
        stage.SetSceneCompositionResult(
            CompositionResult(System.Numerics.Matrix4x4.Identity),
            scene);
        if (stage.UsesReferenceProjection)
        {
            throw new InvalidOperationException("An identity composition pose unexpectedly replaced the ordinary 2D renderer.");
        }
        using (var planarBitmap = RenderGdi(stage))
        {
            AssertPixelNear(
                Sample(planarBitmap, stage.WorldToScreen(0, 0)),
                planarColor,
                "The identity composition pose changed the ordinary 2D presentation");
        }

        var spatialPose = System.Numerics.Matrix4x4.CreateRotationX(MathF.PI / 3f);
        stage.SetSceneCompositionResult(CompositionResult(spatialPose), scene);
        if (!stage.UsesReferenceProjection
            || stage.EffectiveReferenceProjection != CameraProjection.Orthographic
            || Math.Abs(stage.EffectiveReferenceYaw) > 0.0001f
            || Math.Abs(stage.EffectiveReferencePitch) > 0.0001f
            || Math.Abs(stage.ReferenceYaw - 0.83f) > 0.0001f
            || Math.Abs(stage.ReferencePitch + 0.41f) > 0.0001f)
        {
            throw new InvalidOperationException("A spatial composition pose did not activate the front-facing 2D orthographic view.");
        }

        var projectedPoints = stage.GetReference3DProjectedContours(planarObject)
            .Where(contour => contour.Closed)
            .SelectMany(contour => contour.Points)
            .ToArray();
        if (projectedPoints.Length < 3)
        {
            throw new InvalidOperationException("The 2D front view produced no projected spatial contour.");
        }
        var projectedWidth = projectedPoints.Max(point => point.X) - projectedPoints.Min(point => point.X);
        var projectedHeight = projectedPoints.Max(point => point.Y) - projectedPoints.Min(point => point.Y);
        var aspect = projectedHeight / Math.Max(0.0001f, projectedWidth);
        if (!float.IsFinite(aspect) || aspect < 0.27f || aspect > 0.33f)
        {
            throw new InvalidOperationException(
                $"The 2D front view did not foreshorten the spatial pose: size={projectedWidth:0.###}x{projectedHeight:0.###}.");
        }
        if (!stage.TryProjectScenePosition(System.Numerics.Vector3.Zero, out var projectedCenter, out _))
        {
            throw new InvalidOperationException("The spatial pose center could not be projected in the 2D front view.");
        }
        using (var spatialBitmap = RenderGdi(stage))
        {
            AssertPixelNear(
                Sample(spatialBitmap, projectedCenter),
                planarColor,
                "The 2D front view did not render the spatial pose");
        }

        var gradientStops = new[]
        {
            new GradientStop(0, Color.FromArgb(255, 232, 62, 70)),
            new GradientStop(0.5f, Color.FromArgb(255, 246, 194, 54)),
            new GradientStop(1, Color.FromArgb(255, 46, 104, 224))
        };
        AssertProjectedFillGradient(
            scene,
            stage,
            planarObject,
            GradientKind.Linear,
            new PointF(-1_600, 0),
            new PointF(1_600, 0),
            [new PointF(-800, 0), new PointF(600, 650)],
            path: null);
        AssertProjectedFillGradient(
            scene,
            stage,
            planarObject,
            GradientKind.Radial,
            PointF.Empty,
            new PointF(1_600, 0),
            [new PointF(800, 0), new PointF(0, 800)],
            path: null);
        AssertProjectedFillGradient(
            scene,
            stage,
            planarObject,
            GradientKind.ShapeRadial,
            PointF.Empty,
            new PointF(1_600, 0),
            [new PointF(800, 0), new PointF(0, 800)],
            path: null);

        var trajectoryScene = new VectorScene();
        trajectoryScene.CreateEmpty();
        var trajectoryObject = trajectoryScene.AddPathObject(
            0,
            [
                new PointF(-1_600, -900),
                new PointF(1_200, -900),
                new PointF(1_200, 900),
                new PointF(-600, 900),
                new PointF(-600, 500),
                new PointF(800, 500),
                new PointF(800, -500),
                new PointF(-1_600, -500),
                new PointF(-1_600, -900)
            ],
            0,
            planarColor,
            Color.Transparent,
            12);
        if (trajectoryObject < 0)
        {
            throw new InvalidOperationException("The trajectory-gradient fixture could not create its Path object.");
        }
        var trajectoryPath = new[]
        {
            new PointF(-1_400, -700),
            new PointF(1_000, -700),
            new PointF(1_000, 700),
            new PointF(-400, 700)
        };
        var trajectorySamples = new[]
        {
            new PointF(-200, -700),
            new PointF(1_000, 0),
            new PointF(200, 700)
        };
        using var trajectoryStage = CreateStage(trajectoryScene);
        AssertProjectedFillGradient(
            trajectoryScene,
            trajectoryStage,
            trajectoryObject,
            GradientKind.Linear,
            new PointF(-1_400, 0),
            new PointF(1_400, 0),
            trajectorySamples,
            trajectoryPath);

        var lineScene = new VectorScene();
        lineScene.CreateEmpty();
        var lineObject = lineScene.AddLineSegment(
            0,
            new PointF(-1_400, -400),
            new PointF(1_400, 400),
            500,
            Color.Transparent,
            Color.White,
            12);
        lineScene.SetGradientPaint(
            lineObject,
            GradientKind.Linear,
            gradientStops,
            new PointF(-1_400, -400),
            new PointF(1_400, 400));
        using var lineStage = CreateStage(lineScene);
        var lineSample = new PointF(-700, -200);
        lineStage.SetSceneCompositionResult(
            CompositionResult(System.Numerics.Matrix4x4.Identity),
            lineScene);
        Color planarLineColor;
        using (var planarLineBitmap = RenderGdi(lineStage))
        {
            planarLineColor = Sample(planarLineBitmap, lineStage.WorldToScreen(lineSample.X, lineSample.Y));
        }
        lineStage.SetSceneCompositionResult(CompositionResult(spatialPose), lineScene);
        if (!lineStage.TryProjectScenePoint(lineObject, lineSample, out var projectedLineSample, out _))
        {
            throw new InvalidOperationException("The line-gradient sample could not be projected.");
        }
        using (var spatialLineBitmap = RenderGdi(lineStage))
        {
            AssertPixelNear(
                Sample(spatialLineBitmap, projectedLineSample),
                planarLineColor,
                "The 2D front view changed a linear-gradient line stroke",
                tolerance: 10);
        }
        var linePerspectiveView = new SceneDefinition { Dimension = SceneDimension.ThreeD };
        linePerspectiveView.Camera.Projection = CameraProjection.Perspective;
        lineStage.ConfigureReferenceView(linePerspectiveView, SceneDimension.ThreeD);
        lineStage.ResetReferenceCameraView();
        lineStage.SetReferenceCameraOrientation(0, 0);
        lineStage.SetSceneCompositionResult(CompositionResult(spatialPose), lineScene);
        if (!lineStage.TryProjectScenePoint(
                lineObject,
                lineSample,
                out var perspectiveLineSample,
                out _))
        {
            throw new InvalidOperationException("The perspective line-gradient sample could not be projected.");
        }
        using (var perspectiveLineBitmap = RenderGdi(lineStage))
        {
            AssertPixelNear(
                Sample(perspectiveLineBitmap, perspectiveLineSample),
                planarLineColor,
                "Perspective changed a linear-gradient line stroke",
                tolerance: 14);
        }

        var blendScene = new VectorScene();
        blendScene.CreateEmpty();
        var backdropLayer = blendScene.AddLayer("Projected backdrop");
        var sourceColor = Color.FromArgb(255, 196, 92, 42);
        var backdropColor = Color.FromArgb(255, 72, 154, 218);
        blendScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(2_400, 2_400),
            0,
            0,
            sourceColor,
            Color.Transparent,
            12,
            ShapeKind.Rectangle);
        blendScene.AddObject(
            backdropLayer,
            PointF.Empty,
            new SizeF(2_400, 2_400),
            0,
            0,
            backdropColor,
            Color.Transparent,
            12,
            ShapeKind.Rectangle);
        blendScene.SetLayerBlendMode(0, LayerBlendMode.Multiply);
        using var blendStage = CreateStage(blendScene);
        blendStage.SetSceneCompositionResult(
            CompositionResult(
                System.Numerics.Matrix4x4.CreateTranslation(0, 0, 200),
                System.Numerics.Matrix4x4.CreateTranslation(0, 0, 200)),
            blendScene);
        if (!blendStage.TryProjectScenePosition(System.Numerics.Vector3.Zero, out var blendCenter, out _))
        {
            throw new InvalidOperationException("The projected layer-blend sample could not be projected.");
        }
        var expectedBlend = LayerBlendCompositor.CompositeColorForRegression(
            backdropColor,
            sourceColor,
            LayerBlendMode.Multiply);
        using (var blendBitmap = RenderGdi(blendStage))
        {
            AssertPixelNear(
                Sample(blendBitmap, blendCenter),
                expectedBlend,
                "The 2D front view changed the projected layer blend",
                tolerance: 2);
        }

        var farColor = Color.FromArgb(255, 226, 68, 74);
        var nearColor = Color.FromArgb(255, 42, 204, 116);
        var depthScene = new VectorScene();
        depthScene.CreateEmpty();
        var farObject = depthScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(2_400, 2_400),
            0,
            0,
            farColor,
            Color.Transparent,
            12,
            ShapeKind.Rectangle);
        var nearObject = depthScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(2_400, 2_400),
            0,
            0,
            nearColor,
            Color.Transparent,
            12,
            ShapeKind.Rectangle);
        depthScene.ObjectOrder[farObject] = 20;
        depthScene.ObjectOrder[nearObject] = 10;
        using var depthStage = CreateStage(depthScene);
        depthStage.SetSceneCompositionResult(
            CompositionResult(
                System.Numerics.Matrix4x4.CreateTranslation(0, 0, 1_000),
                System.Numerics.Matrix4x4.CreateTranslation(0, 0, -1_000)),
            depthScene);

        var depthOrder = depthStage.GetReference3DLayerObjects(0);
        if (!depthOrder.SequenceEqual([farObject, nearObject]))
        {
            throw new InvalidOperationException(
                $"Spatial objects were not ordered far-to-near within their layer: [{string.Join(",", depthOrder)}].");
        }
        if (!depthStage.TryProjectScenePosition(System.Numerics.Vector3.Zero, out var depthCenter, out _))
        {
            throw new InvalidOperationException("The depth-order sample point could not be projected.");
        }
        using (var depthBitmap = RenderGdi(depthStage))
        {
            AssertPixelNear(
                Sample(depthBitmap, depthCenter),
                nearColor,
                "The farther spatial object painted over the nearer object");
        }
        if (!depthStage.TryHitTestProjectedObject(Point.Round(depthCenter), 1f, out var depthHit)
            || depthHit != nearObject)
        {
            throw new InvalidOperationException("Projected hit testing did not select the nearest painted object.");
        }

        depthStage.SetSceneCompositionResult(
            CompositionResult(
                System.Numerics.Matrix4x4.CreateTranslation(0, 0, 500),
                System.Numerics.Matrix4x4.CreateTranslation(0, 0, 500)),
            depthScene);
        var equalDepthOrder = depthStage.GetReference3DLayerObjects(0);
        if (!equalDepthOrder.SequenceEqual([nearObject, farObject]))
        {
            throw new InvalidOperationException(
                "Equal-depth spatial objects did not retain their object-order tie-break.");
        }

        var crossLayerScene = new VectorScene();
        crossLayerScene.CreateEmpty();
        var crossLayerBottomLayer = crossLayerScene.AddLayer("Spatial depth bottom");
        var crossLayerFarObject = crossLayerScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(2_400, 2_400),
            0,
            0,
            farColor,
            Color.Transparent,
            12,
            ShapeKind.Rectangle);
        var crossLayerNearObject = crossLayerScene.AddObject(
            crossLayerBottomLayer,
            PointF.Empty,
            new SizeF(2_400, 2_400),
            0,
            0,
            nearColor,
            Color.Transparent,
            12,
            ShapeKind.Rectangle);
        using var crossLayerStage = CreateStage(crossLayerScene);
        crossLayerStage.SetSceneCompositionResult(
            CompositionResult(
                System.Numerics.Matrix4x4.CreateTranslation(0, 0, 1_000),
                System.Numerics.Matrix4x4.CreateTranslation(0, 0, -1_000)),
            crossLayerScene);
        var crossLayerDepthItems = crossLayerStage.GetReference3DSceneRenderItems()
            .Where(item => item.Kind == Reference3DRenderKind.FrontFill)
            .ToArray();
        if (crossLayerDepthItems.Length != 2
            || crossLayerDepthItems[0].ObjectIndex != crossLayerFarObject
            || crossLayerDepthItems[0].LayerIndex != 0
            || crossLayerDepthItems[1].ObjectIndex != crossLayerNearObject
            || crossLayerDepthItems[1].LayerIndex != crossLayerBottomLayer)
        {
            throw new InvalidOperationException(
                "Spatial objects on different layers were not globally ordered far-to-near: "
                + string.Join(", ", crossLayerDepthItems.Select(
                    item => $"object={item.ObjectIndex}/layer={item.LayerIndex}/depth={item.AverageDepth}")));
        }
        if (!crossLayerStage.TryProjectScenePosition(
                System.Numerics.Vector3.Zero,
                out var crossLayerDepthCenter,
                out _))
        {
            throw new InvalidOperationException("The cross-layer depth sample point could not be projected.");
        }
        using (var crossLayerDepthBitmap = RenderGdi(crossLayerStage))
        {
            AssertPixelNear(
                Sample(crossLayerDepthBitmap, crossLayerDepthCenter),
                nearColor,
                "A farther top-layer object painted over a nearer bottom-layer object");
        }
        if (!crossLayerStage.TryHitTestProjectedObject(
                Point.Round(crossLayerDepthCenter),
                1f,
                out var crossLayerDepthHit)
            || crossLayerDepthHit != crossLayerNearObject)
        {
            throw new InvalidOperationException(
                "Projected hit testing preferred layer order over the nearest spatial object.");
        }

        crossLayerStage.SetSceneCompositionResult(
            CompositionResult(
                System.Numerics.Matrix4x4.CreateTranslation(0, 0, -1_000),
                System.Numerics.Matrix4x4.CreateTranslation(0, 0, 1_000)),
            crossLayerScene);
        var reversedCrossLayerDepthItems = crossLayerStage.GetReference3DSceneRenderItems()
            .Where(item => item.Kind == Reference3DRenderKind.FrontFill)
            .ToArray();
        if (reversedCrossLayerDepthItems.Length != 2
            || reversedCrossLayerDepthItems[0].ObjectIndex != crossLayerNearObject
            || reversedCrossLayerDepthItems[0].LayerIndex != crossLayerBottomLayer
            || reversedCrossLayerDepthItems[1].ObjectIndex != crossLayerFarObject
            || reversedCrossLayerDepthItems[1].LayerIndex != 0)
        {
            throw new InvalidOperationException(
                "Reversing spatial depth did not reverse the global cross-layer render order: "
                + string.Join(", ", reversedCrossLayerDepthItems.Select(
                    item => $"object={item.ObjectIndex}/layer={item.LayerIndex}/depth={item.AverageDepth}")));
        }
        using (var reversedCrossLayerDepthBitmap = RenderGdi(crossLayerStage))
        {
            AssertPixelNear(
                Sample(reversedCrossLayerDepthBitmap, crossLayerDepthCenter),
                farColor,
                "Reversing spatial depth did not change the nearest painted object");
        }
        if (!crossLayerStage.TryHitTestProjectedObject(
                Point.Round(crossLayerDepthCenter),
                1f,
                out var reversedCrossLayerDepthHit)
            || reversedCrossLayerDepthHit != crossLayerFarObject)
        {
            throw new InvalidOperationException(
                "Projected hit testing did not follow reversed cross-layer spatial depth.");
        }

        crossLayerStage.SetSceneCompositionResult(
            CompositionResult(
                System.Numerics.Matrix4x4.CreateTranslation(0, 0, 500),
                System.Numerics.Matrix4x4.CreateTranslation(0, 0, 500)),
            crossLayerScene);
        var coplanarCrossLayerItems = crossLayerStage.GetReference3DSceneRenderItems()
            .Where(item => item.Kind == Reference3DRenderKind.FrontFill)
            .ToArray();
        if (coplanarCrossLayerItems.Length != 2
            || coplanarCrossLayerItems[0].ObjectIndex != crossLayerNearObject
            || coplanarCrossLayerItems[0].LayerIndex != crossLayerBottomLayer
            || coplanarCrossLayerItems[1].ObjectIndex != crossLayerFarObject
            || coplanarCrossLayerItems[1].LayerIndex != 0)
        {
            throw new InvalidOperationException(
                "Coplanar overlapping objects did not use layer order as their depth tie-break: "
                + string.Join(", ", coplanarCrossLayerItems.Select(
                    item => $"object={item.ObjectIndex}/layer={item.LayerIndex}/depth={item.AverageDepth}")));
        }
        using (var coplanarCrossLayerBitmap = RenderGdi(crossLayerStage))
        {
            AssertPixelNear(
                Sample(coplanarCrossLayerBitmap, crossLayerDepthCenter),
                farColor,
                "A bottom-layer coplanar object painted over the overlapping top-layer object");
        }
        if (!crossLayerStage.TryHitTestProjectedObject(
                Point.Round(crossLayerDepthCenter),
                1f,
                out var coplanarCrossLayerHit)
            || coplanarCrossLayerHit != crossLayerFarObject)
        {
            throw new InvalidOperationException(
                "Projected hit testing did not use layer order for coplanar overlapping objects.");
        }

        var stableOrderScene = new VectorScene();
        stableOrderScene.CreateEmpty();
        var stableOrderMiddleLayer = stableOrderScene.AddLayer("Coplanar near");
        var stableOrderBackLayer = stableOrderScene.AddLayer("Non-coplanar middle");
        var stableOrderFarObject = stableOrderScene.AddObject(
            0,
            new PointF(-600, 0),
            new SizeF(2_000, 1_600),
            angle: 0,
            stroke: 80,
            color: Color.Firebrick,
            strokeColor: Color.White,
            atoms: 12,
            shapeKind: ShapeKind.Rectangle);
        var stableOrderNearObject = stableOrderScene.AddObject(
            stableOrderMiddleLayer,
            new PointF(600, 0),
            new SizeF(2_000, 1_600),
            angle: 0,
            stroke: 80,
            color: Color.SeaGreen,
            strokeColor: Color.White,
            atoms: 12,
            shapeKind: ShapeKind.Rectangle);
        var stableOrderCenterObject = stableOrderScene.AddObject(
            stableOrderBackLayer,
            PointF.Empty,
            new SizeF(900, 900),
            angle: 0,
            stroke: 80,
            color: Color.RoyalBlue,
            strokeColor: Color.White,
            atoms: 12,
            shapeKind: ShapeKind.Rectangle);
        using var stableOrderStage = CreateStage(stableOrderScene);
        var stableOrderTilt = System.Numerics.Matrix4x4.CreateRotationY(0.55f);
        stableOrderStage.SetSceneCompositionResult(
            CompositionResult(
                stableOrderTilt,
                stableOrderTilt,
                System.Numerics.Matrix4x4.Identity),
            stableOrderScene);
        var stableOrderItems = stableOrderStage.GetReference3DSceneRenderItems();
        var stableOrderFarFills = stableOrderItems.Where(item =>
            item.ObjectIndex == stableOrderFarObject
            && item.Kind == Reference3DRenderKind.FrontFill).ToArray();
        var stableOrderNearFills = stableOrderItems.Where(item =>
            item.ObjectIndex == stableOrderNearObject
            && item.Kind == Reference3DRenderKind.FrontFill).ToArray();
        var stableOrderCenterFills = stableOrderItems.Where(item =>
            item.ObjectIndex == stableOrderCenterObject
            && item.Kind == Reference3DRenderKind.FrontFill).ToArray();
        if (stableOrderFarFills.Length == 0
            || stableOrderNearFills.Length == 0
            || stableOrderCenterFills.Length == 0
            || !stableOrderFarFills[0].PlaneKey.IsValid
            || stableOrderFarFills.Any(item => item.PlaneKey != stableOrderFarFills[0].PlaneKey)
            || stableOrderNearFills.Any(item => item.PlaneKey != stableOrderFarFills[0].PlaneKey)
            || !stableOrderCenterFills[0].PlaneKey.IsValid
            || stableOrderCenterFills.Any(item => item.PlaneKey != stableOrderCenterFills[0].PlaneKey)
            || stableOrderCenterFills[0].PlaneKey == stableOrderFarFills[0].PlaneKey
            || !stableOrderStage.TryProjectScenePoint(
                stableOrderFarObject,
                new PointF(
                    stableOrderScene.X[stableOrderFarObject],
                    stableOrderScene.Y[stableOrderFarObject]),
                out _,
                out var stableOrderFarDepth)
            || !stableOrderStage.TryProjectScenePoint(
                stableOrderCenterObject,
                new PointF(
                    stableOrderScene.X[stableOrderCenterObject],
                    stableOrderScene.Y[stableOrderCenterObject]),
                out _,
                out var stableOrderCenterDepth)
            || !stableOrderStage.TryProjectScenePoint(
                stableOrderNearObject,
                new PointF(
                    stableOrderScene.X[stableOrderNearObject],
                    stableOrderScene.Y[stableOrderNearObject]),
                out _,
                out var stableOrderNearDepth)
            || stableOrderFarDepth <= stableOrderCenterDepth
            || stableOrderCenterDepth <= stableOrderNearDepth)
        {
            throw new InvalidOperationException(
                "The three-object render-order fixture did not contain a coplanar far/near pair around a non-coplanar middle depth: "
                + $"far={string.Join(',', stableOrderFarFills.Select(item => item.AverageDepth))}/{stableOrderFarFills.FirstOrDefault().PlaneKey}, "
                + $"middle={string.Join(',', stableOrderCenterFills.Select(item => item.AverageDepth))}/{stableOrderCenterFills.FirstOrDefault().PlaneKey}, "
                + $"near={string.Join(',', stableOrderNearFills.Select(item => item.AverageDepth))}/{stableOrderNearFills.FirstOrDefault().PlaneKey}.");
        }
        var sharedStableFragments = stableOrderNearFills.Select(item => item.FragmentSlot)
            .Intersect(stableOrderFarFills.Select(item => item.FragmentSlot))
            .ToArray();
        if (sharedStableFragments.Length == 0
            || sharedStableFragments.Any(fragmentSlot =>
                Array.FindIndex(stableOrderItems, item =>
                    item.ObjectIndex == stableOrderNearObject
                    && item.Kind == Reference3DRenderKind.FrontFill
                    && item.FragmentSlot == fragmentSlot)
                >= Array.FindIndex(stableOrderItems, item =>
                    item.ObjectIndex == stableOrderFarObject
                    && item.Kind == Reference3DRenderKind.FrontFill
                    && item.FragmentSlot == fragmentSlot)))
        {
            throw new InvalidOperationException(
                "The coplanar pair in the three-object fixture did not retain its layer tie-break.");
        }
        foreach (var objectIndex in new[]
                 {
                     stableOrderFarObject,
                     stableOrderNearObject,
                     stableOrderCenterObject
                 })
        {
            var frontItems = stableOrderItems
                .Select((item, index) => (Item: item, Index: index))
                .Where(entry => entry.Item.ObjectIndex == objectIndex
                    && entry.Item.Kind is Reference3DRenderKind.FrontFill
                        or Reference3DRenderKind.FrontStroke)
                .ToArray();
            var invalidFragment = frontItems
                .GroupBy(entry => entry.Item.FragmentSlot)
                .Any(fragment =>
                {
                    var passes = fragment.OrderBy(entry => entry.Index).ToArray();
                    return passes.Length != 2
                        || passes[0].Item.Kind != Reference3DRenderKind.FrontFill
                        || passes[1].Item.Kind != Reference3DRenderKind.FrontStroke
                        || passes[1].Index != passes[0].Index + 1;
                });
            if (frontItems.Length == 0 || invalidFragment)
            {
                throw new InvalidOperationException(
                    $"Global scene ordering separated or reversed object {objectIndex} front passes: "
                    + string.Join(",", frontItems.Select(entry =>
                        $"{entry.Item.Kind}/fragment={entry.Item.FragmentSlot}@{entry.Index}")));
            }
        }
        var stableOrderSignature = stableOrderItems
            .Select(item => (
                item.ObjectIndex,
                item.LayerIndex,
                item.Kind,
                item.AverageDepth,
                item.PlaneKey,
                item.SurfaceSlot,
                item.FragmentSlot))
            .ToArray();
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var repeatedSignature = stableOrderStage.GetReference3DSceneRenderItems()
                .Select(item => (
                    item.ObjectIndex,
                    item.LayerIndex,
                    item.Kind,
                    item.AverageDepth,
                    item.PlaneKey,
                    item.SurfaceSlot,
                    item.FragmentSlot))
                .ToArray();
            if (!repeatedSignature.SequenceEqual(stableOrderSignature))
            {
                throw new InvalidOperationException(
                    $"Global 3D scene render order changed on identical evaluation {attempt + 1}.");
            }
        }

        var strokeOverlapScene = new VectorScene();
        strokeOverlapScene.CreateEmpty();
        var strokeOverlapBottomLayer = strokeOverlapScene.AddLayer("Stroke overlap bottom");
        const float strokeOverlapWidth = 180f;
        var strokeOverlapTopColor = Color.FromArgb(255, 232, 76, 142);
        var strokeOverlapBottomColor = Color.FromArgb(255, 46, 190, 214);
        var strokeOverlapTopObject = strokeOverlapScene.AddLineSegment(
            0,
            new PointF(-1_600, -50),
            new PointF(800, -50),
            strokeOverlapWidth,
            Color.Transparent,
            strokeOverlapTopColor,
            12);
        var strokeOverlapBottomObject = strokeOverlapScene.AddLineSegment(
            strokeOverlapBottomLayer,
            new PointF(-800, 50),
            new PointF(1_600, 50),
            strokeOverlapWidth,
            Color.Transparent,
            strokeOverlapBottomColor,
            12);
        using var strokeOverlapStage = CreateStage(strokeOverlapScene);
        strokeOverlapStage.SetSceneCompositionResult(
            CompositionResult(stableOrderTilt, stableOrderTilt),
            strokeOverlapScene);
        var strokeOverlapTopContours = strokeOverlapStage
            .GetReference3DProjectedContours(strokeOverlapTopObject);
        var strokeOverlapBottomContours = strokeOverlapStage
            .GetReference3DProjectedContours(strokeOverlapBottomObject);
        if (strokeOverlapTopContours.Length == 0
            || strokeOverlapBottomContours.Length == 0
            || strokeOverlapTopContours.Any(contour => contour.Closed || contour.Points.Length < 2)
            || strokeOverlapBottomContours.Any(contour => contour.Closed || contour.Points.Length < 2))
        {
            throw new InvalidOperationException(
                "The projected stroke-overlap fixture did not retain two open centerlines.");
        }
        var strokeOverlapTopPoints = strokeOverlapTopContours.SelectMany(contour => contour.Points).ToArray();
        var strokeOverlapBottomPoints = strokeOverlapBottomContours.SelectMany(contour => contour.Points).ToArray();
        var strokeOverlapTopMinY = strokeOverlapTopPoints.Min(point => point.Y);
        var strokeOverlapTopMaxY = strokeOverlapTopPoints.Max(point => point.Y);
        var strokeOverlapBottomMinY = strokeOverlapBottomPoints.Min(point => point.Y);
        var strokeOverlapBottomMaxY = strokeOverlapBottomPoints.Max(point => point.Y);
        if (!strokeOverlapStage.TryProjectScenePoint(
                strokeOverlapTopObject,
                new PointF(0, -50),
                out var strokeOverlapTopCenter,
                out _)
            || !strokeOverlapStage.TryProjectScenePoint(
                strokeOverlapBottomObject,
                new PointF(0, 50),
                out var strokeOverlapBottomCenter,
                out _))
        {
            throw new InvalidOperationException("The stroke-overlap sample centers could not be projected.");
        }
        var strokeOverlapCenterDx = strokeOverlapTopCenter.X - strokeOverlapBottomCenter.X;
        var strokeOverlapCenterDy = strokeOverlapTopCenter.Y - strokeOverlapBottomCenter.Y;
        var strokeOverlapCenterDistance = MathF.Sqrt(
            strokeOverlapCenterDx * strokeOverlapCenterDx
            + strokeOverlapCenterDy * strokeOverlapCenterDy);
        var strokeOverlapTopProjectedWidth = strokeOverlapStage.GetReference3DStrokeWidth(
            strokeOverlapTopObject,
            strokeOverlapScene.Stroke[strokeOverlapTopObject]);
        var strokeOverlapBottomProjectedWidth = strokeOverlapStage.GetReference3DStrokeWidth(
            strokeOverlapBottomObject,
            strokeOverlapScene.Stroke[strokeOverlapBottomObject]);
        var strokeOverlapHalfWidthSum = (
            strokeOverlapTopProjectedWidth + strokeOverlapBottomProjectedWidth) * 0.5f;
        var strokeOverlapCenterlinesSeparated = strokeOverlapTopMaxY < strokeOverlapBottomMinY
            || strokeOverlapBottomMaxY < strokeOverlapTopMinY;
        if (!strokeOverlapCenterlinesSeparated
            || strokeOverlapCenterDistance <= 0.75f
            || strokeOverlapCenterDistance >= strokeOverlapHalfWidthSum)
        {
            throw new InvalidOperationException(
                "The projected stroke-overlap fixture did not separate its centerlines while overlapping their visible widths: "
                + $"distance={strokeOverlapCenterDistance:0.###}, "
                + $"halfWidthSum={strokeOverlapHalfWidthSum:0.###}, "
                + $"topY={strokeOverlapTopMinY:0.###}-{strokeOverlapTopMaxY:0.###}, "
                + $"bottomY={strokeOverlapBottomMinY:0.###}-{strokeOverlapBottomMaxY:0.###}.");
        }
        var strokeOverlapItems = strokeOverlapStage.GetReference3DSceneRenderItems();
        if (strokeOverlapItems.Length != 2
            || strokeOverlapItems.Any(item => item.Kind != Reference3DRenderKind.FrontStroke)
            || strokeOverlapItems.Any(item => !item.PlaneKey.IsValid)
            || strokeOverlapItems[0].PlaneKey != strokeOverlapItems[1].PlaneKey
            || strokeOverlapItems[0].ObjectIndex != strokeOverlapBottomObject
            || strokeOverlapItems[0].LayerIndex != strokeOverlapBottomLayer
            || strokeOverlapItems[1].ObjectIndex != strokeOverlapTopObject
            || strokeOverlapItems[1].LayerIndex != 0
            || strokeOverlapItems[1].AverageDepth <= strokeOverlapItems[0].AverageDepth)
        {
            throw new InvalidOperationException(
                "Overlapping coplanar strokes did not use bottom-to-top layer order over their opposing average depths: "
                + string.Join(", ", strokeOverlapItems.Select(item =>
                    $"object={item.ObjectIndex}/layer={item.LayerIndex}/kind={item.Kind}/depth={item.AverageDepth}/plane={item.PlaneKey}")));
        }
        var strokeOverlapSample = new PointF(
            (strokeOverlapTopCenter.X + strokeOverlapBottomCenter.X) * 0.5f,
            (strokeOverlapTopCenter.Y + strokeOverlapBottomCenter.Y) * 0.5f);
        using (var strokeOverlapBitmap = RenderGdi(strokeOverlapStage))
        {
            AssertPixelNear(
                Sample(strokeOverlapBitmap, strokeOverlapSample),
                strokeOverlapTopColor,
                "A bottom-layer coplanar stroke painted over the overlapping top-layer stroke",
                tolerance: 3);
        }
        if (!strokeOverlapStage.TryHitTestProjectedObject(
                Point.Round(strokeOverlapSample),
                1f,
                out var strokeOverlapHit)
            || strokeOverlapHit != strokeOverlapTopObject)
        {
            throw new InvalidOperationException(
                "Projected hit testing did not select the top layer in the visible stroke overlap.");
        }

        var outlineScene = new VectorScene();
        outlineScene.CreateEmpty();
        var outlineBottomLayer = outlineScene.AddLayer("Outline bottom");
        var outlineTopObject = outlineScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(2_000, 1_600),
            0,
            0,
            Color.Coral,
            Color.Transparent,
            12,
            ShapeKind.Rectangle);
        var outlineBottomObject = outlineScene.AddObject(
            outlineBottomLayer,
            PointF.Empty,
            new SizeF(2_000, 1_600),
            0,
            0,
            Color.CornflowerBlue,
            Color.Transparent,
            12,
            ShapeKind.Rectangle);
        outlineScene.SetLayerOutline(0, true);
        outlineScene.SetLayerOutline(outlineBottomLayer, true);
        using var outlineStage = CreateStage(outlineScene);
        outlineStage.SetSceneCompositionResult(
            CompositionResult(
                System.Numerics.Matrix4x4.CreateTranslation(0, 0, 500),
                System.Numerics.Matrix4x4.CreateTranslation(0, 0, 500)),
            outlineScene);
        var outlineItems = outlineStage.GetReference3DSceneRenderItems()
            .Where(item => item.Kind == Reference3DRenderKind.Outline)
            .ToArray();
        if (outlineItems.Length != 2
            || outlineItems.Any(item => !item.PlaneKey.IsValid)
            || outlineItems[0].ObjectIndex != outlineBottomObject
            || outlineItems[0].LayerIndex != outlineBottomLayer
            || outlineItems[1].ObjectIndex != outlineTopObject
            || outlineItems[1].LayerIndex != 0)
        {
            throw new InvalidOperationException(
                "Coplanar Outline layers did not expose valid plane keys in bottom-to-top order: "
                + string.Join(", ", outlineItems.Select(
                    item => $"object={item.ObjectIndex}/layer={item.LayerIndex}/plane={item.PlaneKey}")));
        }

        var blendDepthScene = new VectorScene();
        blendDepthScene.CreateEmpty();
        var blendDepthBottomLayer = blendDepthScene.AddLayer("Blend depth bottom");
        var blendDepthTopObject = blendDepthScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(2_400, 2_400),
            0,
            0,
            farColor,
            Color.Transparent,
            12,
            ShapeKind.Rectangle);
        var blendDepthBottomObject = blendDepthScene.AddObject(
            blendDepthBottomLayer,
            PointF.Empty,
            new SizeF(2_400, 2_400),
            0,
            0,
            nearColor,
            Color.Transparent,
            12,
            ShapeKind.Rectangle);
        blendDepthScene.SetLayerBlendMode(blendDepthBottomLayer, LayerBlendMode.Multiply);
        using var blendDepthStage = CreateStage(blendDepthScene);
        blendDepthStage.SetSceneCompositionResult(
            CompositionResult(
                System.Numerics.Matrix4x4.CreateTranslation(0, 0, 1_000),
                System.Numerics.Matrix4x4.CreateTranslation(0, 0, -1_000)),
            blendDepthScene);
        if (!blendDepthScene.HasNonNormalLayerBlendModes
            || !blendDepthStage.TryProjectScenePosition(
                System.Numerics.Vector3.Zero,
                out var blendDepthCenter,
                out _))
        {
            throw new InvalidOperationException("The blended layer-depth fixture was not configured correctly.");
        }
        using (var blendDepthBitmap = RenderGdi(blendDepthStage))
        {
            AssertPixelNear(
                Sample(blendDepthBitmap, blendDepthCenter),
                farColor,
                "A nearer bottom blend layer painted over the top display layer");
        }
        if (!blendDepthStage.TryHitTestProjectedObject(
                Point.Round(blendDepthCenter),
                1f,
                out var blendDepthHit)
            || blendDepthHit != blendDepthTopObject)
        {
            throw new InvalidOperationException(
                $"Blended-layer hit testing did not match the top displayed object: hit={blendDepthHit}, "
                + $"top={blendDepthTopObject}, bottom={blendDepthBottomObject}.");
        }

        const float crossingCardAngle = 0.62f;
        var crossingCardSize = new SizeF(3_600, 2_400);
        var crossingCardAColor = Color.FromArgb(255, 226, 68, 74);
        var crossingCardBColor = Color.FromArgb(255, 42, 204, 116);
        var crossingEdgeColor = Color.White;
        var crossingCardAFallbackColor = Color.FromArgb(255, 83, 48, 146);
        var crossingCardBFallbackColor = Color.FromArgb(255, 34, 104, 156);
        var crossingCardATransform = System.Numerics.Matrix4x4.CreateRotationX(crossingCardAngle);
        var crossingCardBTransform = System.Numerics.Matrix4x4.CreateRotationX(-crossingCardAngle);
        var crossingDirect2DSamples = new List<(Point Screen, int ObjectIndex, Color ExpectedColor)>();
        var crossingScene = new VectorScene();
        crossingScene.CreateEmpty();
        var crossingBottomLayer = crossingScene.AddLayer("Crossing card bottom");
        var crossingCardA = crossingScene.AddObject(
            0,
            PointF.Empty,
            crossingCardSize,
            angle: 0,
            stroke: 96,
            color: crossingCardAFallbackColor,
            strokeColor: crossingEdgeColor,
            atoms: 12,
            shapeKind: ShapeKind.Rectangle);
        var crossingCardB = crossingScene.AddObject(
            crossingBottomLayer,
            PointF.Empty,
            crossingCardSize,
            angle: 0,
            stroke: 96,
            color: crossingCardBFallbackColor,
            strokeColor: crossingEdgeColor,
            atoms: 12,
            shapeKind: ShapeKind.Rectangle);
        crossingScene.SetGradientPaint(
            crossingCardA,
            GradientKind.Linear,
            [new GradientStop(0, crossingCardAColor), new GradientStop(1, crossingCardAColor)],
            new PointF(-1_800, 0),
            new PointF(1_800, 0));
        crossingScene.SetGradientPaint(
            crossingCardB,
            GradientKind.Linear,
            [new GradientStop(0, crossingCardBColor), new GradientStop(1, crossingCardBColor)],
            new PointF(-1_800, 0),
            new PointF(1_800, 0));
        if (!crossingScene.HasGradient(crossingCardA) || !crossingScene.HasGradient(crossingCardB))
        {
            throw new InvalidOperationException("The crossing-card fixture did not enable projective materials.");
        }
        using var crossingStage = CreateStage(crossingScene);
        crossingStage.SetSceneCompositionResult(
            CompositionResult(crossingCardATransform, crossingCardBTransform),
            crossingScene);
        var crossingView = new SceneDefinition { Dimension = SceneDimension.ThreeD };
        crossingView.Camera.Projection = CameraProjection.Orthographic;
        crossingStage.ConfigureReferenceView(crossingView, SceneDimension.ThreeD);
        crossingStage.ResetReferenceCameraView();
        crossingStage.SetReferenceCameraOrientation(0, 0);
        if (crossingStage.EffectiveReferenceProjection != CameraProjection.Orthographic)
        {
            throw new InvalidOperationException("The crossing-card fixture did not activate orthographic projection.");
        }
        AssertCrossingCardView("orthographic-front");

        crossingView.Camera.Projection = CameraProjection.Perspective;
        crossingStage.ConfigureReferenceView(crossingView, SceneDimension.ThreeD);
        crossingStage.SetReferenceCameraOrientation(0, 0);
        if (crossingStage.EffectiveReferenceProjection != CameraProjection.Perspective)
        {
            throw new InvalidOperationException("The crossing-card fixture did not activate perspective projection.");
        }
        AssertCrossingCardPerspective();
        AssertCrossingCardView("perspective-front");
        if (!crossingStage.TryProjectScenePoint(
                crossingCardA,
                new PointF(900, 800),
                out var crossingFrontAnchor,
                out _))
        {
            throw new InvalidOperationException("The front crossing-card camera pose could not project its anchor.");
        }

        crossingStage.SetReferenceCameraOrientation(0.43f, -0.24f);
        if (!crossingStage.TryProjectScenePoint(
                crossingCardA,
                new PointF(900, 800),
                out var crossingObliqueAnchor,
                out _)
            || CrossingScreenDistance(Point.Round(crossingFrontAnchor), Point.Round(crossingObliqueAnchor)) < 4f)
        {
            throw new InvalidOperationException("Changing crossing-card yaw and pitch did not change its perspective projection.");
        }
        AssertCrossingCardView("perspective-oblique");

        var blendedCrossingScene = new VectorScene();
        blendedCrossingScene.RestoreSnapshot(crossingScene.CreateSnapshot());
        var unrelatedBlendLayer = blendedCrossingScene.AddLayer("Unrelated blend content");
        blendedCrossingScene.AddObject(
            unrelatedBlendLayer,
            new PointF(5_400, 3_200),
            new SizeF(600, 600),
            angle: 0,
            stroke: 0,
            color: Color.FromArgb(255, 110, 118, 132),
            strokeColor: Color.Transparent,
            atoms: 12,
            shapeKind: ShapeKind.Rectangle);
        blendedCrossingScene.SetLayerBlendMode(unrelatedBlendLayer, LayerBlendMode.Multiply);
        using (var blendedCrossingStage = CreateStage(blendedCrossingScene))
        {
            blendedCrossingStage.ConfigureReferenceView(crossingView, SceneDimension.ThreeD);
            blendedCrossingStage.ResetReferenceCameraView();
            blendedCrossingStage.SetReferenceCameraOrientation(0.43f, -0.24f);
            blendedCrossingStage.SetSceneCompositionResult(
                CompositionResult(
                    crossingCardATransform,
                    crossingCardBTransform,
                    System.Numerics.Matrix4x4.Identity),
                blendedCrossingScene);
            var normalItems = crossingStage.GetReference3DSceneRenderItems();
            var blendedItems = blendedCrossingStage.GetReference3DSceneRenderItems();
            var normalEdgeCount = normalItems.Count(item =>
                item.Kind == Reference3DRenderKind.IntersectionEdge
                && item.ObjectIndex is 0 or 1
                && item.SecondaryObjectIndex is 0 or 1);
            var blendedEdgeCount = blendedItems.Count(item =>
                item.Kind == Reference3DRenderKind.IntersectionEdge
                && item.ObjectIndex is 0 or 1
                && item.SecondaryObjectIndex is 0 or 1);
            if (!blendedCrossingScene.HasNonNormalLayerBlendModes
                || normalEdgeCount == 0
                || blendedEdgeCount != normalEdgeCount
                || new[] { crossingCardA, crossingCardB }.Any(objectIndex =>
                    normalItems.Count(item => item.ObjectIndex == objectIndex
                        && item.Kind == Reference3DRenderKind.FrontFill
                        && item.FragmentClip is { Length: > 0 })
                    != blendedItems.Count(item => item.ObjectIndex == objectIndex
                        && item.Kind == Reference3DRenderKind.FrontFill
                        && item.FragmentClip is { Length: > 0 })))
            {
                throw new InvalidOperationException(
                    "An unrelated blend layer disabled crossing-card fragments or connection edges.");
            }

            using var blendedBitmap = RenderGdi(blendedCrossingStage);
            foreach (var sample in crossingDirect2DSamples)
            {
                AssertPixelNear(
                    Sample(blendedBitmap, sample.Screen),
                    sample.ExpectedColor,
                    "An unrelated blend layer changed the crossing-card foreground",
                    tolerance: 12);
                if (!blendedCrossingStage.TryHitTestProjectedObject(
                        sample.Screen,
                        1f,
                        out var blendedHit)
                    || blendedHit != sample.ObjectIndex)
                {
                    throw new InvalidOperationException(
                        "Mixed-layer crossing-card hit testing disagreed with the composited spatial batch.");
                }
            }
            if (!blendedCrossingStage.TryProjectScenePosition(
                    System.Numerics.Vector3.Zero,
                    out var blendedEdgePoint,
                    out _)
                || !HasPixelNearColor(
                    blendedBitmap,
                    blendedEdgePoint,
                    crossingEdgeColor,
                    radius: 2,
                    tolerance: 20))
            {
                throw new InvalidOperationException(
                    "GDI software blending did not paint the crossing-card connection edge.");
            }
        }

        var grazingScene = new VectorScene();
        grazingScene.CreateEmpty();
        var grazingBottomLayer = grazingScene.AddLayer("Grazing intersection bottom");
        const float grazingStroke = 120f;
        var grazingStrokeColor = Color.White;
        var grazingSurface = grazingScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(4_800, 3_600),
            angle: 0,
            stroke: grazingStroke,
            color: Color.FromArgb(255, 44, 112, 210),
            strokeColor: grazingStrokeColor,
            atoms: 12,
            shapeKind: ShapeKind.Rectangle);
        var grazingTilted = grazingScene.AddObject(
            grazingBottomLayer,
            PointF.Empty,
            new SizeF(2_400, 2_000),
            angle: 0,
            stroke: 0,
            color: Color.FromArgb(255, 224, 78, 72),
            strokeColor: Color.Transparent,
            atoms: 12,
            shapeKind: ShapeKind.Rectangle);
        const float grazingPivotX = -1_199.95f;
        var grazingTiltedTransform = System.Numerics.Matrix4x4.CreateTranslation(-grazingPivotX, 0, 0)
            * System.Numerics.Matrix4x4.CreateRotationY(0.65f)
            * System.Numerics.Matrix4x4.CreateTranslation(grazingPivotX, 0, 0);
        using var grazingStage = CreateStage(grazingScene);
        var grazingView = new SceneDefinition { Dimension = SceneDimension.ThreeD };
        grazingView.Camera.Projection = CameraProjection.Orthographic;
        grazingStage.ConfigureReferenceView(grazingView, SceneDimension.ThreeD);
        grazingStage.ResetReferenceCameraView();
        grazingStage.SetReferenceCameraOrientation(0, 0);
        grazingStage.SetSceneCompositionResult(
            CompositionResult(System.Numerics.Matrix4x4.Identity, grazingTiltedTransform),
            grazingScene);
        var grazingItems = grazingStage.GetReference3DSceneRenderItems();
        var grazingEdges = grazingItems
            .Where(item => item.Kind == Reference3DRenderKind.IntersectionEdge
                && (item.ObjectIndex == grazingSurface || item.SecondaryObjectIndex == grazingSurface)
                && (item.ObjectIndex == grazingTilted || item.SecondaryObjectIndex == grazingTilted))
            .ToArray();
        var grazingEdge = grazingEdges
            .OrderByDescending(item => item.Contours.Sum(contour => contour.Points.Length < 2
                ? 0f
                : CrossingScreenDistance(Point.Round(contour.Points[0]), Point.Round(contour.Points[^1]))))
            .FirstOrDefault();
        if (grazingEdges.Length == 0
            || grazingItems.Count(item => item.ObjectIndex == grazingSurface
                && item.Kind == Reference3DRenderKind.FrontFill
                && item.FragmentClip is { Length: > 0 }) < 2
            || grazingItems.Any(item => item.ObjectIndex == grazingTilted
                && item.Kind == Reference3DRenderKind.FrontFill
                && item.FragmentClip is { Length: > 0 })
            || grazingEdge.Contours is not { Length: > 0 }
            || grazingEdge.Contours[0].Points is not { Length: >= 2 })
        {
            throw new InvalidOperationException(
                "A grazing intersection lost its connection edge when only one incident surface required multiple fragments.");
        }
        if (grazingEdge.ObjectIndex != grazingSurface
            || grazingEdge.EdgeArgb != grazingStrokeColor.ToArgb()
            || grazingEdge.EdgeWidth is < 3.9f or > 4.5f)
        {
            throw new InvalidOperationException(
                "The grazing-intersection fixture no longer guarantees an opaque automatic edge sample.");
        }
        var grazingEdgePoints = grazingEdge.Contours[0].Points;
        var grazingEdgeSample = new PointF(
            (grazingEdgePoints[0].X + grazingEdgePoints[^1].X) * 0.5f,
            (grazingEdgePoints[0].Y + grazingEdgePoints[^1].Y) * 0.5f);
        var grazingEdgeColor = Color.FromArgb(grazingEdge.EdgeArgb);
        using (var grazingBitmap = RenderGdi(grazingStage))
        {
            var grazingActual = Sample(grazingBitmap, grazingEdgeSample);
            if (!PixelRgbNear(grazingActual, grazingEdgeColor, 12))
            {
                throw new InvalidOperationException(
                    "GDI did not paint the successful singleton-fragment intersection edge: "
                    + $"sample={grazingEdgeSample}, actual={grazingActual.ToArgb():X8}, "
                    + $"expected={grazingEdgeColor.ToArgb():X8}, width={grazingEdge.EdgeWidth:0.###}, "
                    + "order=["
                    + string.Join(',', grazingItems.Select(item =>
                        $"{item.Kind}:{item.ObjectIndex}/{item.SecondaryObjectIndex}:f{item.FragmentSlot}"))
                    + "].");
            }
        }

        var threeCardSize = new SizeF(4_200, 3_200);
        var threeCardColors = new[]
        {
            Color.FromArgb(255, 232, 72, 78),
            Color.FromArgb(255, 48, 190, 112),
            Color.FromArgb(255, 58, 122, 224)
        };
        var threeCardTransforms = new[]
        {
            System.Numerics.Matrix4x4.CreateRotationX(0.72f)
                * System.Numerics.Matrix4x4.CreateRotationZ(0.14f),
            System.Numerics.Matrix4x4.CreateRotationY(-0.66f)
                * System.Numerics.Matrix4x4.CreateRotationZ(-0.31f),
            System.Numerics.Matrix4x4.CreateRotationX(-0.58f)
                * System.Numerics.Matrix4x4.CreateRotationY(0.38f)
                * System.Numerics.Matrix4x4.CreateRotationZ(0.57f)
        };
        var threeCardScene = new VectorScene();
        threeCardScene.CreateEmpty();
        var threeCardObjects = new int[threeCardTransforms.Length];
        for (var cardIndex = 0; cardIndex < threeCardObjects.Length; cardIndex++)
        {
            var layer = cardIndex == 0
                ? 0
                : threeCardScene.AddLayer($"Crossing card {cardIndex + 1}");
            threeCardObjects[cardIndex] = threeCardScene.AddObject(
                layer,
                PointF.Empty,
                threeCardSize,
                angle: 0,
                stroke: 0,
                color: threeCardColors[cardIndex],
                strokeColor: Color.Transparent,
                atoms: 12,
                shapeKind: ShapeKind.Rectangle);
        }
        using var threeCardStage = CreateStage(threeCardScene);
        threeCardStage.SetSceneCompositionResult(
            CompositionResult(threeCardTransforms),
            threeCardScene);
        var threeCardView = new SceneDefinition { Dimension = SceneDimension.ThreeD };
        threeCardView.Camera.Projection = CameraProjection.Orthographic;
        threeCardStage.ConfigureReferenceView(threeCardView, SceneDimension.ThreeD);
        threeCardStage.ResetReferenceCameraView();
        threeCardStage.SetReferenceCameraOrientation(0, 0);
        AssertThreeCrossingCardView("orthographic-front");
        threeCardStage.SetReferenceCameraOrientation(0.47f, -0.29f);
        AssertThreeCrossingCardView("orthographic-oblique");

        threeCardView.Camera.Projection = CameraProjection.Perspective;
        threeCardStage.ConfigureReferenceView(threeCardView, SceneDimension.ThreeD);
        threeCardStage.SetReferenceCameraOrientation(0, 0);
        AssertThreeCrossingCardView("perspective-front");
        threeCardStage.SetReferenceCameraOrientation(0.47f, -0.29f);
        AssertThreeCrossingCardView("perspective-oblique");

        var cyclicCardPoints = new[]
        {
            new System.Numerics.Vector3(-1_000, 0, 0),
            new System.Numerics.Vector3(500, 866, 0),
            new System.Numerics.Vector3(500, -866, 0)
        };
        var cyclicCardStarts = new[]
        {
            cyclicCardPoints[0] with { Z = 0 },
            cyclicCardPoints[0] with { Z = 600 },
            cyclicCardPoints[1] with { Z = 600 }
        };
        var cyclicCardEnds = new[]
        {
            cyclicCardPoints[2] with { Z = 600 },
            cyclicCardPoints[1] with { Z = 0 },
            cyclicCardPoints[2] with { Z = 0 }
        };
        var cyclicCardTransforms = Enumerable.Range(0, cyclicCardStarts.Length)
            .Select(index => CreateFiniteCardTransform(
                cyclicCardStarts[index],
                cyclicCardEnds[index]))
            .ToArray();
        var cyclicCardLength = System.Numerics.Vector3.Distance(
            cyclicCardStarts[0],
            cyclicCardEnds[0]);
        var cyclicCardSize = new SizeF(cyclicCardLength + 900, 520);
        var cyclicCardColors = new[]
        {
            Color.FromArgb(255, 220, 58, 66),
            Color.FromArgb(255, 38, 182, 102),
            Color.FromArgb(255, 52, 108, 218)
        };
        var cyclicDirect2DSamples = new List<(Point Screen, Color ExpectedColor)>();
        var cyclicCardScene = new VectorScene();
        cyclicCardScene.CreateEmpty();
        var cyclicCardObjects = new int[cyclicCardTransforms.Length];
        for (var cardIndex = 0; cardIndex < cyclicCardObjects.Length; cardIndex++)
        {
            var layer = cardIndex == 0
                ? 0
                : cyclicCardScene.AddLayer($"Cyclic card {cardIndex + 1}");
            cyclicCardObjects[cardIndex] = cyclicCardScene.AddObject(
                layer,
                PointF.Empty,
                cyclicCardSize,
                angle: 0,
                stroke: 0,
                color: cyclicCardColors[cardIndex],
                strokeColor: Color.Transparent,
                atoms: 12,
                shapeKind: ShapeKind.Rectangle);
        }
        using var cyclicCardStage = CreateStage(cyclicCardScene);
        cyclicCardStage.SetSceneCompositionResult(
            CompositionResult(cyclicCardTransforms),
            cyclicCardScene);
        var cyclicCardView = new SceneDefinition { Dimension = SceneDimension.ThreeD };
        cyclicCardView.Camera.Projection = CameraProjection.Orthographic;
        cyclicCardStage.ConfigureReferenceView(cyclicCardView, SceneDimension.ThreeD);
        cyclicCardStage.ResetReferenceCameraView();
        cyclicCardStage.SetReferenceCameraOrientation(0, 0);
        var cyclicItems = cyclicCardStage.GetReference3DSceneRenderItems();
        if (cyclicItems.Any(item => item.Kind == Reference3DRenderKind.IntersectionEdge)
            || cyclicCardObjects.Any(objectIndex => cyclicItems.Count(item =>
                    item.ObjectIndex == objectIndex
                    && item.Kind == Reference3DRenderKind.FrontFill
                    && item.FragmentClip is { Length: > 0 }) < 2))
        {
            throw new InvalidOperationException(
                "The finite cyclic-card fixture did not require contour partitioning without visible plane intersections.");
        }
        AssertMultiCardView(
            cyclicCardStage,
            cyclicCardObjects,
            cyclicCardTransforms,
            cyclicCardColors,
            "orthographic-finite-cycle",
            minimumSamplesPerObject: 2,
            retainedSamples: cyclicDirect2DSamples);
        AssertPerspectiveImportedSvgMaterial();
        AssertNearClippedProjectiveMaterial();

        var fixedStrokeProject = new VectorProject();
        var fixedStrokeDrawing = fixedStrokeProject.DrawingObjects[0];
        fixedStrokeDrawing.Scene.CreateEmpty();
        const float fixedStrokeSourceWidth = 84f;
        fixedStrokeDrawing.Scene.AddObject(
            0,
            PointF.Empty,
            new SizeF(2_000, 1_200),
            angle: 0,
            stroke: fixedStrokeSourceWidth,
            color: Color.Transparent,
            strokeColor: Color.White,
            atoms: 12,
            shapeKind: ShapeKind.Rectangle);
        var fixedStrokeSceneDefinition = fixedStrokeProject.Scenes[0];
        if (!fixedStrokeProject.TryAddSceneInstance(
                fixedStrokeSceneDefinition.Id,
                fixedStrokeDrawing.Id,
                PointF.Empty,
                0,
                out var fixedStrokeInstance)
            || fixedStrokeInstance is null)
        {
            throw new InvalidOperationException("The non-scaling reference-stroke fixture could not create its instance.");
        }
        fixedStrokeInstance.ScaleX = 3;
        fixedStrokeInstance.ScaleY = 3;
        fixedStrokeInstance.ScaleZ = 0;
        fixedStrokeInstance.RotationY = 28;
        var fixedStrokeScene = new VectorScene();
        var fixedStrokeResult = SceneCompositionBuilder.Build(
            fixedStrokeScene,
            fixedStrokeSceneDefinition,
            fixedStrokeProject.DrawingObjects,
            0);
        if (fixedStrokeScene.ObjectCount != 1
            || !fixedStrokeResult.TryGetPose(0, out var fixedStrokePose)
            || Math.Abs(fixedStrokePose.FlatStrokeScale - 3f) > 0.001f
            || Math.Abs(fixedStrokeScene.Stroke[0] - fixedStrokeSourceWidth * 3f) > 0.001f)
        {
            throw new InvalidOperationException(
                "Scene composition did not retain the flat stroke scale needed by non-scaling 3D rendering.");
        }
        using var fixedStrokeStage = CreateStage(fixedStrokeScene);
        var fixedStrokeView = new SceneDefinition { Dimension = SceneDimension.ThreeD };
        fixedStrokeView.Camera.Projection = CameraProjection.Orthographic;
        fixedStrokeStage.ConfigureReferenceView(fixedStrokeView, SceneDimension.ThreeD);
        fixedStrokeStage.ResetReferenceCameraView();
        fixedStrokeStage.SetReferenceCameraOrientation(0, 0);
        fixedStrokeStage.SetSceneCompositionResult(fixedStrokeResult, fixedStrokeScene);

        var fixedStrokeBaselineScene = new VectorScene();
        fixedStrokeBaselineScene.CreateEmpty();
        fixedStrokeBaselineScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(2_000, 1_200),
            angle: 0,
            stroke: fixedStrokeSourceWidth,
            color: Color.Transparent,
            strokeColor: Color.White,
            atoms: 12,
            shapeKind: ShapeKind.Rectangle);
        using var fixedStrokeBaselineStage = CreateStage(fixedStrokeBaselineScene);
        fixedStrokeBaselineStage.ConfigureReferenceView(fixedStrokeView, SceneDimension.ThreeD);
        fixedStrokeBaselineStage.ResetReferenceCameraView();
        fixedStrokeBaselineStage.SetReferenceCameraOrientation(0, 0);
        fixedStrokeBaselineStage.SetSceneCompositionResult(
            CompositionResult(System.Numerics.Matrix4x4.CreateRotationY(28 * MathF.PI / 180f)),
            fixedStrokeBaselineScene);
        var fixedStrokeWidth = fixedStrokeStage.GetReference3DStrokeWidth(
            0,
            fixedStrokeScene.Stroke[0]);
        var fixedStrokeBaselineWidth = fixedStrokeBaselineStage.GetReference3DStrokeWidth(
            0,
            fixedStrokeBaselineScene.Stroke[0]);
        if (Math.Abs(fixedStrokeStage.GetReference3DSourceStrokeWidth(0, fixedStrokeScene.Stroke[0])
                - fixedStrokeSourceWidth) > 0.001f
            || Math.Abs(fixedStrokeWidth - fixedStrokeBaselineWidth) > 0.05f)
        {
            throw new InvalidOperationException(
                "Scaling a Scene instance changed its reference-view stroke thickness: "
                + $"scaled={fixedStrokeWidth:0.###}, baseline={fixedStrokeBaselineWidth:0.###}.");
        }

        var fixedLineProject = new VectorProject();
        var fixedLineDrawing = fixedLineProject.DrawingObjects[0];
        fixedLineDrawing.Scene.CreateEmpty();
        var fixedLineObject = fixedLineDrawing.Scene.AddLineSegment(
            0,
            new PointF(-1_600, 0),
            new PointF(1_600, 0),
            fixedStrokeSourceWidth,
            Color.Transparent,
            Color.White,
            12);
        fixedLineDrawing.Scene.SetGradientPaint(
            fixedLineObject,
            GradientKind.Linear,
            [new GradientStop(0, Color.Cyan), new GradientStop(1, Color.Magenta)],
            new PointF(-1_600, 0),
            new PointF(1_600, 0));
        var fixedLineDefinition = fixedLineProject.Scenes[0];
        if (!fixedLineProject.TryAddSceneInstance(
                fixedLineDefinition.Id,
                fixedLineDrawing.Id,
                PointF.Empty,
                0,
                out var fixedLineInstance)
            || fixedLineInstance is null)
        {
            throw new InvalidOperationException("The non-scaling projective Line fixture could not create its instance.");
        }
        fixedLineInstance.ScaleX = 0.25f;
        fixedLineInstance.ScaleY = 0.25f;
        fixedLineInstance.ScaleZ = 240f;
        fixedLineInstance.RotationY = 34f;
        var fixedLineScene = new VectorScene();
        var fixedLineResult = SceneCompositionBuilder.Build(
            fixedLineScene,
            fixedLineDefinition,
            fixedLineProject.DrawingObjects,
            0);
        using (var fixedLineStage = CreateStage(fixedLineScene))
        {
            var fixedLineView = new SceneDefinition { Dimension = SceneDimension.ThreeD };
            fixedLineView.Camera.Projection = CameraProjection.Perspective;
            fixedLineStage.ConfigureReferenceView(fixedLineView, SceneDimension.ThreeD);
            fixedLineStage.ResetReferenceCameraView();
            fixedLineStage.SetReferenceCameraOrientation(0.28f, -0.16f);
            fixedLineStage.SetSceneCompositionResult(fixedLineResult, fixedLineScene);
            var compensatedOutline = fixedLineStage.GetReference3DStrokeOutlineSourceContours(0);
            var outlinePoints = compensatedOutline.SelectMany(contour => contour.Points).ToArray();
            if (outlinePoints.Length < 3
                || outlinePoints.Max(point => point.Y) - outlinePoints.Min(point => point.Y)
                    < fixedStrokeSourceWidth * 0.9f
                || !fixedLineStage.TryGetReference3DProjectiveMesh(
                    0,
                    usePrimaryContourQuad: false,
                    out var fixedLineMesh)
                || fixedLineMesh.Length == 0)
            {
                throw new InvalidOperationException(
                    "The scaled projective Line did not retain its compensated source-width domain.");
            }
            var meshPoints = fixedLineMesh
                .SelectMany(triangle => new[] { triangle.A.Flat, triangle.B.Flat, triangle.C.Flat })
                .ToArray();
            if (meshPoints.Min(point => point.X) > outlinePoints.Min(point => point.X)
                || meshPoints.Max(point => point.X) < outlinePoints.Max(point => point.X)
                || meshPoints.Min(point => point.Y) > outlinePoints.Min(point => point.Y)
                || meshPoints.Max(point => point.Y) < outlinePoints.Max(point => point.Y)
                || !fixedLineStage.GetReference3DProjectedSolid(0).HasExtrusion)
            {
                throw new InvalidOperationException(
                    "The projective Line mesh or extrusion clipped its non-scaling stroke outline.");
            }
        }

        var extrusionFill = Color.FromArgb(255, 52, 132, 218);
        var extrusionStroke = Color.FromArgb(255, 214, 66, 152);
        var extrusionScene = new VectorScene();
        extrusionScene.CreateEmpty();
        var extrusionObject = extrusionScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(3_600, 2_200),
            angle: 0,
            stroke: 120,
            color: extrusionFill,
            strokeColor: extrusionStroke,
            atoms: 120,
            shapeKind: ShapeKind.Rectangle);
        using var extrusionStage = CreateStage(extrusionScene);
        extrusionStage.ConfigureReferenceView(null, SceneDimension.ThreeD);
        extrusionStage.SetReferenceCameraOrientation(0.72f, -0.36f);
        extrusionStage.SetSceneCompositionResult(
            PoseResult(System.Numerics.Matrix4x4.Identity, System.Numerics.Vector3.Zero),
            extrusionScene);
        var flatSolid = extrusionStage.GetReference3DProjectedSolid(extrusionObject);
        if (flatSolid.HasExtrusion
            || flatSolid.BackContours.Length != 0
            || flatSolid.SideSurfaces.Length != 0)
        {
            throw new InvalidOperationException("A zero Scale Z pose produced extruded reference geometry.");
        }

        extrusionStage.SetSceneCompositionResult(
            PoseResult(
                System.Numerics.Matrix4x4.Identity,
                new System.Numerics.Vector3(0, 0, 1_800)),
            extrusionScene);
        var solid = extrusionStage.GetReference3DProjectedSolid(extrusionObject);
        if (!solid.HasExtrusion
            || solid.BackContours.Length == 0
            || solid.SideSurfaces.Length == 0
            || solid.BackContours.Concat(solid.SideSurfaces)
                .SelectMany(contour => contour.Points)
                .Any(point => !float.IsFinite(point.X) || !float.IsFinite(point.Y)))
        {
            throw new InvalidOperationException("A nonzero Scale Z pose produced incomplete or non-finite extrusion geometry.");
        }

        var extrusionItems = extrusionStage.GetReference3DLayerRenderItems([extrusionObject]);
        if (!extrusionItems.Any(item => item.Kind == Reference3DRenderKind.Back)
            || !extrusionItems.Any(item => item.Kind == Reference3DRenderKind.Side)
            || !extrusionItems.Any(item => item.Kind == Reference3DRenderKind.FrontFill)
            || !extrusionItems.Any(item => item.Kind == Reference3DRenderKind.FrontStroke)
            || extrusionItems.Any(item => !float.IsFinite(item.AverageDepth)))
        {
            throw new InvalidOperationException(
                "Extruded reference render items did not include finite back, side, and front passes: "
                + string.Join(", ", extrusionItems.Select(item => $"{item.Kind}={item.AverageDepth}"))
                + $"; stroke={extrusionScene.Stroke[extrusionObject]}, strokeArgb={extrusionScene.StrokeArgb[extrusionObject]:X8}.");
        }
        for (var index = 1; index < extrusionItems.Length; index++)
        {
            if (extrusionItems[index - 1].AverageDepth + 0.0001f < extrusionItems[index].AverageDepth)
            {
                throw new InvalidOperationException("Extruded reference surfaces were not sorted far-to-near.");
            }
        }
        var extrusionFrontItems = extrusionItems
            .Where(item => item.Kind is Reference3DRenderKind.FrontFill
                or Reference3DRenderKind.FrontStroke)
            .ToArray();
        var extrusionFrontItem = extrusionFrontItems.First(item =>
            item.Kind == Reference3DRenderKind.FrontFill);
        var extrusionBackItem = extrusionItems.First(item =>
            item.Kind == Reference3DRenderKind.Back);
        var extrusionSideItems = extrusionItems
            .Where(item => item.Kind == Reference3DRenderKind.Side)
            .ToArray();
        if (!extrusionFrontItem.PlaneKey.IsValid
            || extrusionFrontItems.Any(item =>
                !item.PlaneKey.IsValid || item.PlaneKey != extrusionFrontItem.PlaneKey)
            || !extrusionBackItem.PlaneKey.IsValid
            || extrusionFrontItem.PlaneKey.NormalX != -extrusionBackItem.PlaneKey.NormalX
            || extrusionFrontItem.PlaneKey.NormalY != -extrusionBackItem.PlaneKey.NormalY
            || extrusionFrontItem.PlaneKey.NormalZ != -extrusionBackItem.PlaneKey.NormalZ
            || extrusionSideItems.Length == 0
            || extrusionSideItems.Any(item => !item.PlaneKey.IsValid))
        {
            throw new InvalidOperationException(
                "Extruded reference surfaces did not expose valid outward-facing plane keys: "
                + $"front={extrusionFrontItem.PlaneKey}, back={extrusionBackItem.PlaneKey}, "
                + "sides=["
                + string.Join(",", extrusionSideItems.Select(item => item.PlaneKey))
                + "].");
        }
        if (!extrusionScene.SetLayerOutline(0, true))
        {
            throw new InvalidOperationException("The extrusion Outline fixture could not enable its layer effect.");
        }
        var extrusionOutlineItems = extrusionStage.GetReference3DSceneRenderItems()
            .Where(item => item.Kind == Reference3DRenderKind.Outline)
            .ToArray();
        if (extrusionOutlineItems.Length != 1
            || extrusionOutlineItems[0].ObjectIndex != extrusionObject
            || extrusionOutlineItems[0].PlaneKey.IsValid)
        {
            throw new InvalidOperationException(
                "An extruded Outline item incorrectly claimed a single surface plane: "
                + string.Join(", ", extrusionOutlineItems.Select(item =>
                    $"object={item.ObjectIndex}/plane={item.PlaneKey}")));
        }
        if (!extrusionScene.SetLayerOutline(0, false))
        {
            throw new InvalidOperationException("The extrusion Outline fixture could not restore its layer effect.");
        }
        if (extrusionStage.GetReference3DExtrusionColor(extrusionObject).ToArgb() != extrusionStroke.ToArgb())
        {
            throw new InvalidOperationException("Scale Z extrusion did not use the object's visible outline color.");
        }

        var side = solid.SideSurfaces
            .FirstOrDefault(contour => contour.Points.Length >= 3);
        var projectedSidePoints = side.Points;
        if (projectedSidePoints is not { Length: >= 3 })
        {
            throw new InvalidOperationException("No projected side surface was available for hit testing.");
        }
        var sideSample = new PointF(
            projectedSidePoints.Average(point => point.X),
            projectedSidePoints.Average(point => point.Y));
        if (!extrusionStage.TryHitTestProjectedObject(Point.Round(sideSample), 1f, out var extrusionHit)
            || extrusionHit != extrusionObject)
        {
            throw new InvalidOperationException("Projected hit testing did not select an extruded side surface.");
        }

        extrusionStage.SetReference3DSelection([extrusionObject]);
        var longestSelectionStart = PointF.Empty;
        var longestSelectionEnd = PointF.Empty;
        var longestSelectionLengthSquared = 0f;
        foreach (var edge in solid.SelectionEdges)
        {
            var segmentCount = edge.Closed ? edge.Points.Length : edge.Points.Length - 1;
            for (var segment = 0; segment < segmentCount; segment++)
            {
                var start = edge.Points[segment];
                var end = edge.Points[(segment + 1) % edge.Points.Length];
                var dx = end.X - start.X;
                var dy = end.Y - start.Y;
                var lengthSquared = dx * dx + dy * dy;
                if (lengthSquared <= longestSelectionLengthSquared) continue;
                longestSelectionStart = start;
                longestSelectionEnd = end;
                longestSelectionLengthSquared = lengthSquared;
            }
        }
        if (longestSelectionLengthSquared < 100f)
        {
            throw new InvalidOperationException("The extrusion selection regression produced no usable outline segment.");
        }
        using (var selectionBitmap = RenderGdi(extrusionStage))
        {
            for (var sampleIndex = 1; sampleIndex <= 9; sampleIndex++)
            {
                var amount = sampleIndex / 10f;
                var samplePoint = new PointF(
                    longestSelectionStart.X + (longestSelectionEnd.X - longestSelectionStart.X) * amount,
                    longestSelectionStart.Y + (longestSelectionEnd.Y - longestSelectionStart.Y) * amount);
                if (!HasPixelNearColor(
                        selectionBitmap,
                        samplePoint,
                        StageControl.Reference3DSelectionLineColor,
                        radius: 2,
                        tolerance: 28))
                {
                    throw new InvalidOperationException("The 3D selection outline was not a continuous orange stroke.");
                }
            }
        }

        Console.WriteLine("scene_reference_projection_2d_spatial_pose=ok");
        Console.WriteLine("scene_reference_projection_2d_gradients_and_blend=ok");
        Console.WriteLine("scene_reference_depth_order_and_hit_test=ok");
        Console.WriteLine("scene_reference_intersecting_planes=ok");
        Console.WriteLine("scene_reference_grazing_intersection_edges=ok");
        Console.WriteLine("scene_reference_blend_isolated_intersections=ok");
        Console.WriteLine("scene_reference_multi_plane_cycles=ok");
        Console.WriteLine("scene_reference_projective_materials=ok");
        Console.WriteLine("scene_reference_non_scaling_strokes=ok");
        Console.WriteLine("scene_reference_extrusion_and_selection=ok");

        void AssertCrossingCardView(string label)
        {
            var candidates = new List<(Point Screen, int ObjectIndex, Color ExpectedColor, float DepthGap)>();
            foreach (var sourceObject in new[] { crossingCardA, crossingCardB })
            {
                foreach (var localY in new[] { -700f, 700f })
                {
                    foreach (var localX in new[] { -650f, 0f, 650f })
                    {
                        if (!crossingStage.TryProjectScenePoint(
                                sourceObject,
                                new PointF(localX, localY),
                                out var projected,
                                out _))
                        {
                            continue;
                        }

                        var screen = Point.Round(projected);
                        if (!TryResolveCrossingCardFront(
                                screen,
                                out var expectedObject,
                                out var depthGap)
                            || candidates.Any(candidate => candidate.Screen == screen))
                        {
                            continue;
                        }
                        candidates.Add((
                            screen,
                            expectedObject,
                            expectedObject == crossingCardA ? crossingCardAColor : crossingCardBColor,
                            depthGap));
                    }
                }
            }

            var samples = new List<(Point Screen, int ObjectIndex, Color ExpectedColor, float DepthGap)>();
            foreach (var expectedObject in new[] { crossingCardA, crossingCardB })
            {
                foreach (var candidate in candidates.Where(candidate => candidate.ObjectIndex == expectedObject))
                {
                    if (samples.Any(sample => sample.ObjectIndex == expectedObject
                            && CrossingScreenDistance(sample.Screen, candidate.Screen) < 8f))
                    {
                        continue;
                    }
                    samples.Add(candidate);
                    if (samples.Count(sample => sample.ObjectIndex == expectedObject) == 2) break;
                }
            }

            var cardASamples = samples.Count(sample => sample.ObjectIndex == crossingCardA);
            var cardBSamples = samples.Count(sample => sample.ObjectIndex == crossingCardB);
            if (cardASamples < 2 || cardBSamples < 2)
            {
                throw new InvalidOperationException(
                    $"The {label} crossing-card fixture did not expose both depth halves: "
                    + $"A={cardASamples}, B={cardBSamples}, candidates={candidates.Count}, "
                    + $"gaps=[{string.Join(",", candidates.Select(candidate => candidate.DepthGap.ToString("0.###")))}].");
            }

            using var bitmap = RenderGdi(crossingStage);
            foreach (var sample in samples)
            {
                AssertPixelNear(
                    Sample(bitmap, sample.Screen),
                    sample.ExpectedColor,
                    $"The {label} crossing-card projection painted the wrong foreground object",
                    tolerance: 12);
                if (!crossingStage.TryHitTestProjectedObject(
                        sample.Screen,
                        1f,
                        out var hit)
                    || hit != sample.ObjectIndex)
                {
                    throw new InvalidOperationException(
                        $"The {label} crossing-card hit disagreed with its visible foreground: "
                        + $"point={sample.Screen}, expected={sample.ObjectIndex}, hit={hit}, "
                        + $"depthGap={sample.DepthGap:0.###}.");
                }
            }

            if (label == "perspective-oblique")
            {
                crossingDirect2DSamples.Clear();
                crossingDirect2DSamples.AddRange(samples.Select(sample => (
                    sample.Screen,
                    sample.ObjectIndex,
                    sample.ExpectedColor)));
            }

            for (var edgeSample = -4; edgeSample <= 4; edgeSample++)
            {
                var crossingX = edgeSample * 200f;
                if (!crossingStage.TryProjectScenePosition(
                        new System.Numerics.Vector3(crossingX, 0, 0),
                        out var edgePoint,
                        out _)
                    || !HasPixelNearColor(
                        bitmap,
                        edgePoint,
                        crossingEdgeColor,
                        radius: 3,
                        tolerance: 28))
                {
                    throw new InvalidOperationException(
                        $"The {label} crossing-card projection did not draw a continuous automatic intersection edge at X={crossingX:0.###}.");
                }
            }
            foreach (var outsideX in new[] { -2_100f, 2_100f })
            {
                if (!crossingStage.TryProjectScenePosition(
                        new System.Numerics.Vector3(outsideX, 0, 0),
                        out var outsidePoint,
                        out _))
                {
                    throw new InvalidOperationException(
                        $"The {label} crossing-card outside-edge sample could not be projected.");
                }
                if (HasPixelNearColor(
                        bitmap,
                        outsidePoint,
                        crossingEdgeColor,
                        radius: 2,
                        tolerance: 28))
                {
                    throw new InvalidOperationException(
                        $"The {label} automatic intersection edge extended beyond both cards at X={outsideX:0.###}.");
                }
            }
        }

        bool TryResolveCrossingCardFront(Point screen, out int objectIndex, out float depthGap)
        {
            objectIndex = -1;
            depthGap = 0;
            if (!crossingStage.TryGetReferenceRay(screen, out var ray)
                || !TryIntersectCrossingCard(ray, crossingCardATransform, out var cardADepth)
                || !TryIntersectCrossingCard(ray, crossingCardBTransform, out var cardBDepth))
            {
                return false;
            }

            depthGap = Math.Abs(cardADepth - cardBDepth);
            if (!float.IsFinite(depthGap) || depthGap < 20f) return false;
            objectIndex = cardADepth < cardBDepth ? crossingCardA : crossingCardB;
            return true;
        }

        void AssertThreeCrossingCardView(string label)
        {
            AssertMultiCardView(
                threeCardStage,
                threeCardObjects,
                threeCardTransforms,
                threeCardColors,
                label,
                minimumSamplesPerObject: 8,
                retainedSamples: null);
        }

        void AssertMultiCardView(
            StageControl stage,
            IReadOnlyList<int> objects,
            IReadOnlyList<System.Numerics.Matrix4x4> transforms,
            IReadOnlyList<Color> colors,
            string label,
            int minimumSamplesPerObject,
            ICollection<(Point Screen, Color ExpectedColor)>? retainedSamples)
        {
            const float minimumDepthGap = 90f;
            var samples = new List<(Point Screen, int ObjectIndex, Color ExpectedColor, string Depths)>();
            var counts = new int[objects.Count];
            for (var y = 28; y < stage.Height - 28; y += 7)
            {
                for (var x = 28; x < stage.Width - 28; x += 7)
                {
                    var screen = new Point(x, y);
                    if (!stage.TryGetReferenceRay(screen, out var ray)) continue;
                    var hits = new List<(int Slot, float Distance, System.Numerics.Vector3 Point)>();
                    var nearProjectedBoundary = false;
                    for (var cardIndex = 0; cardIndex < transforms.Count; cardIndex++)
                    {
                        var contours = stage.GetReference3DProjectedContours(objects[cardIndex]);
                        if (!ProjectedFillMembershipStable(
                                contours,
                                screen,
                                3,
                                out var containsCard))
                        {
                            nearProjectedBoundary = true;
                            break;
                        }
                        if (!containsCard) continue;
                        if (TryIntersectCardPlane(
                                ray,
                                transforms[cardIndex],
                                out var distance,
                                out var intersection))
                        {
                            hits.Add((cardIndex, distance, intersection));
                        }
                    }
                    if (nearProjectedBoundary) continue;
                    if (hits.Count < 2) continue;
                    hits.Sort((left, right) => left.Distance.CompareTo(right.Distance));
                    if (hits[1].Distance - hits[0].Distance < minimumDepthGap) continue;

                    if (!stage.TryProjectScenePosition(
                            hits[0].Point,
                            out var projectedIntersection,
                            out _)
                        || CrossingScreenDistance(screen, Point.Round(projectedIntersection)) > 1.5f)
                    {
                        throw new InvalidOperationException(
                            $"The {label} camera ray did not invert its projected three-card point: "
                            + $"screen={screen}, projected={projectedIntersection}.");
                    }

                    samples.Add((
                        screen,
                        objects[hits[0].Slot],
                        colors[hits[0].Slot],
                        string.Join(",", hits.Select(hit => $"{hit.Slot}:{hit.Distance:0.###}"))));
                    counts[hits[0].Slot]++;
                }
            }
            if (counts.Any(count => count < minimumSamplesPerObject))
            {
                throw new InvalidOperationException(
                    $"The {label} three-card fixture did not expose every local foreground region: "
                    + string.Join(",", counts));
            }

            using var bitmap = RenderGdi(stage);
            var renderItems = stage.GetReference3DSceneRenderItems();
            foreach (var sample in samples)
            {
                var actual = Sample(bitmap, sample.Screen);
                if (!PixelRgbNear(actual, sample.ExpectedColor, 12))
                {
                    var coveringOrder = renderItems
                        .Where(item => item.Kind == Reference3DRenderKind.FrontFill
                            && ProjectedFillContainsMargin(item.Contours, sample.Screen, 0)
                            && (item.FragmentClip is not { Length: > 0 }
                                || ProjectedFillContainsMargin(item.FragmentClip, sample.Screen, 0)))
                        .Select(item =>
                            $"{item.ObjectIndex}/fragment={item.FragmentSlot}/depth={item.AverageDepth:0.###}");
                    throw new InvalidOperationException(
                        $"The {label} three-card projection painted the wrong local foreground at "
                        + $"{sample.Screen} for object {sample.ObjectIndex}: "
                        + $"actual={actual.ToArgb():X8}, expected={sample.ExpectedColor.ToArgb():X8}, "
                        + $"ray=[{sample.Depths}], order=[{string.Join(',', coveringOrder)}].");
                }
                if (!stage.TryHitTestProjectedObject(
                        sample.Screen,
                        1f,
                        out var hit)
                    || hit != sample.ObjectIndex)
                {
                    throw new InvalidOperationException(
                        $"The {label} three-card hit disagreed with the ray-traced foreground: "
                        + $"point={sample.Screen}, expected={sample.ObjectIndex}, hit={hit}.");
                }
            }

            if (retainedSamples is not null)
            {
                retainedSamples.Clear();
                foreach (var objectSamples in samples.GroupBy(sample => sample.ObjectIndex))
                {
                    foreach (var sample in objectSamples.Take(3))
                    {
                        retainedSamples.Add((sample.Screen, sample.ExpectedColor));
                    }
                }
            }
        }

        static bool TryIntersectCardPlane(
            SpatialRay ray,
            System.Numerics.Matrix4x4 transform,
            out float distance,
            out System.Numerics.Vector3 intersection)
        {
            distance = 0;
            intersection = default;
            var axisX = System.Numerics.Vector3.TransformNormal(System.Numerics.Vector3.UnitX, transform);
            var axisY = System.Numerics.Vector3.TransformNormal(System.Numerics.Vector3.UnitY, transform);
            var normal = System.Numerics.Vector3.Cross(axisX, axisY);
            var normalLength = normal.Length();
            if (!float.IsFinite(normalLength) || normalLength <= 0.000001f)
            {
                return false;
            }
            normal /= normalLength;

            var planePoint = System.Numerics.Vector3.Transform(System.Numerics.Vector3.Zero, transform);
            var denominator = System.Numerics.Vector3.Dot(ray.Direction, normal);
            if (!float.IsFinite(denominator) || Math.Abs(denominator) <= 0.000001f) return false;
            distance = System.Numerics.Vector3.Dot(planePoint - ray.Origin, normal) / denominator;
            if (!float.IsFinite(distance) || distance <= 0) return false;

            intersection = ray.Origin + ray.Direction * distance;
            return float.IsFinite(intersection.X)
                && float.IsFinite(intersection.Y)
                && float.IsFinite(intersection.Z);
        }

        static System.Numerics.Matrix4x4 CreateFiniteCardTransform(
            System.Numerics.Vector3 start,
            System.Numerics.Vector3 end)
        {
            var axisX = System.Numerics.Vector3.Normalize(end - start);
            var planarDirection = new System.Numerics.Vector2(
                end.X - start.X,
                end.Y - start.Y);
            var planarLength = planarDirection.Length();
            if (!float.IsFinite(planarLength) || planarLength <= 0.000001f)
            {
                throw new InvalidOperationException("A finite card requires a nonzero projected length.");
            }
            var axisY = new System.Numerics.Vector3(
                -planarDirection.Y / planarLength,
                planarDirection.X / planarLength,
                0);
            var axisZ = System.Numerics.Vector3.Normalize(
                System.Numerics.Vector3.Cross(axisX, axisY));
            var center = (start + end) * 0.5f;
            return new System.Numerics.Matrix4x4(
                axisX.X, axisX.Y, axisX.Z, 0,
                axisY.X, axisY.Y, axisY.Z, 0,
                axisZ.X, axisZ.Y, axisZ.Z, 0,
                center.X, center.Y, center.Z, 1);
        }

        static bool ProjectedFillContainsMargin(
            IReadOnlyList<Reference3DProjectedContour> contours,
            Point point,
            int margin)
        {
            foreach (var offset in new[]
                     {
                         new Point(0, 0),
                         new Point(-margin, 0),
                         new Point(margin, 0),
                         new Point(0, -margin),
                         new Point(0, margin)
                     })
            {
                var sample = new PointF(point.X + offset.X, point.Y + offset.Y);
                var inside = false;
                foreach (var contour in contours)
                {
                    if (!contour.Closed || contour.Points.Length < 3) continue;
                    var previous = contour.Points[^1];
                    foreach (var current in contour.Points)
                    {
                        if ((current.Y > sample.Y) != (previous.Y > sample.Y)
                            && sample.X < (previous.X - current.X) * (sample.Y - current.Y)
                                / (previous.Y - current.Y)
                                + current.X)
                        {
                            inside = !inside;
                        }
                        previous = current;
                    }
                }
                if (!inside) return false;
            }
            return true;
        }

        static bool ProjectedFillMembershipStable(
            IReadOnlyList<Reference3DProjectedContour> contours,
            Point point,
            int margin,
            out bool contains)
        {
            contains = ProjectedFillContainsMargin(contours, point, 0);
            foreach (var offset in new[]
                     {
                         new Point(-margin, 0),
                         new Point(margin, 0),
                         new Point(0, -margin),
                         new Point(0, margin)
                     })
            {
                if (ProjectedFillContainsMargin(
                        contours,
                        new Point(point.X + offset.X, point.Y + offset.Y),
                        0)
                    != contains)
                {
                    return false;
                }
            }
            return true;
        }

        bool TryIntersectCrossingCard(
            SpatialRay ray,
            System.Numerics.Matrix4x4 transform,
            out float depth)
        {
            depth = 0;
            var axisX = System.Numerics.Vector3.TransformNormal(System.Numerics.Vector3.UnitX, transform);
            var axisY = System.Numerics.Vector3.TransformNormal(System.Numerics.Vector3.UnitY, transform);
            var normal = System.Numerics.Vector3.Cross(axisX, axisY);
            var normalLength = normal.Length();
            if (!float.IsFinite(normalLength)
                || normalLength <= 0.000001f
                || !System.Numerics.Matrix4x4.Invert(transform, out var inverse))
            {
                return false;
            }
            normal /= normalLength;

            var planePoint = System.Numerics.Vector3.Transform(System.Numerics.Vector3.Zero, transform);
            var denominator = System.Numerics.Vector3.Dot(ray.Direction, normal);
            if (!float.IsFinite(denominator) || Math.Abs(denominator) <= 0.000001f) return false;
            var amount = System.Numerics.Vector3.Dot(planePoint - ray.Origin, normal) / denominator;
            if (!float.IsFinite(amount) || amount <= 0) return false;

            var intersection = ray.Origin + ray.Direction * amount;
            var local = System.Numerics.Vector3.Transform(intersection, inverse);
            const float boundaryMargin = 180f;
            if (!float.IsFinite(local.X)
                || !float.IsFinite(local.Y)
                || Math.Abs(local.X) > crossingCardSize.Width * 0.5f - boundaryMargin
                || Math.Abs(local.Y) > crossingCardSize.Height * 0.5f - boundaryMargin
                || !crossingStage.TryProjectScenePosition(intersection, out _, out depth))
            {
                return false;
            }
            return float.IsFinite(depth);
        }

        void AssertCrossingCardPerspective()
        {
            var cardANegative = CrossingCardStrip(crossingCardA, -800);
            var cardAPositive = CrossingCardStrip(crossingCardA, 800);
            var cardBNegative = CrossingCardStrip(crossingCardB, -800);
            var cardBPositive = CrossingCardStrip(crossingCardB, 800);
            AssertNearStripLarger("A", cardANegative, cardAPositive);
            AssertNearStripLarger("B", cardBNegative, cardBPositive);
            if (Math.Sign(cardANegative.Depth - cardAPositive.Depth)
                == Math.Sign(cardBNegative.Depth - cardBPositive.Depth))
            {
                throw new InvalidOperationException(
                    "The crossing-card perspective fixture did not place opposite ends of the two cards nearer to the camera.");
            }
        }

        (float Span, float Depth) CrossingCardStrip(int objectIndex, float localY)
        {
            if (!crossingStage.TryProjectScenePoint(
                    objectIndex,
                    new PointF(-1_000, localY),
                    out var left,
                    out _)
                || !crossingStage.TryProjectScenePoint(
                    objectIndex,
                    new PointF(1_000, localY),
                    out var right,
                    out _)
                || !crossingStage.TryProjectScenePoint(
                    objectIndex,
                    new PointF(0, localY),
                    out _,
                    out var depth))
            {
                throw new InvalidOperationException(
                    $"The crossing-card perspective strip at Y={localY:0.###} could not be projected.");
            }
            var dx = right.X - left.X;
            var dy = right.Y - left.Y;
            return (MathF.Sqrt(dx * dx + dy * dy), depth);
        }

        static void AssertNearStripLarger(
            string label,
            (float Span, float Depth) first,
            (float Span, float Depth) second)
        {
            var near = first.Depth < second.Depth ? first : second;
            var far = first.Depth < second.Depth ? second : first;
            if (!float.IsFinite(near.Span)
                || !float.IsFinite(far.Span)
                || near.Span <= far.Span * 1.05f)
            {
                throw new InvalidOperationException(
                    $"Crossing card {label} did not become wider toward the perspective camera: "
                    + $"near={near.Span:0.###}@{near.Depth:0.###}, far={far.Span:0.###}@{far.Depth:0.###}.");
            }
        }

        static float CrossingScreenDistance(Point first, Point second)
        {
            var dx = first.X - second.X;
            var dy = first.Y - second.Y;
            return MathF.Sqrt(dx * dx + dy * dy);
        }

        void AssertNearClippedProjectiveMaterial()
        {
            const string source = """
                <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 40 40">
                  <rect x="0" y="0" width="40" height="40" fill="#ff5a4e"/>
                </svg>
                """;
            var expected = Color.FromArgb(255, 255, 90, 78);
            var nearScene = new VectorScene();
            nearScene.CreateEmpty();
            var nearObject = nearScene.AddImportedSvgObject(
                0,
                PointF.Empty,
                new SizeF(40, 40),
                0,
                source,
                "Near-clipped SVG");
            nearScene.Argb[nearObject] = Color.White.ToArgb();
            using var nearStage = CreateStage(nearScene);
            var perspectiveView = new SceneDefinition { Dimension = SceneDimension.ThreeD };
            perspectiveView.Camera.Projection = CameraProjection.Perspective;
            nearStage.ConfigureReferenceView(perspectiveView, SceneDimension.ThreeD);
            nearStage.ResetReferenceCameraView();
            nearStage.SetReferenceCameraOrientation(0, 0);

            nearStage.SetSceneCompositionResult(
                CompositionResult(System.Numerics.Matrix4x4.Identity),
                nearScene);
            if (!nearStage.TryGetReference3DProjectiveMesh(
                    nearObject,
                    usePrimaryContourQuad: true,
                    out var fullMesh)
                || !StageControl.Reference3DProjectiveMeshCoversFullDomain(fullMesh))
            {
                throw new InvalidOperationException("A fully visible SVG did not retain its complete projective UV domain.");
            }
            AssertFiniteMesh(fullMesh, "fully visible");

            nearStage.SetSceneCompositionResult(
                CompositionResult(System.Numerics.Matrix4x4.CreateTranslation(0, 0, -13_000)),
                nearScene);
            if (nearStage.TryGetReference3DProjectiveMesh(
                    nearObject,
                    usePrimaryContourQuad: true,
                    out _)
                || nearStage.GetReference3DProjectedContours(nearObject).Length != 0)
            {
                throw new InvalidOperationException("An SVG entirely behind the reference near plane remained visible.");
            }

            var clippedPose = System.Numerics.Matrix4x4.CreateRotationX(0.7f)
                * System.Numerics.Matrix4x4.CreateRotationY(0.7f)
                * System.Numerics.Matrix4x4.CreateTranslation(0, 0, -11_900);
            nearStage.SetSceneCompositionResult(CompositionResult(clippedPose), nearScene);
            var clippedContour = nearStage.GetReference3DProjectedContours(nearObject)
                .FirstOrDefault(contour => contour.Closed);
            if (clippedContour.Points is not { Length: 3 }
                || !nearStage.TryGetReference3DProjectiveMesh(
                    nearObject,
                    usePrimaryContourQuad: true,
                    out var clippedMesh)
                || clippedMesh.Length == 0
                || StageControl.Reference3DProjectiveMeshCoversFullDomain(clippedMesh))
            {
                throw new InvalidOperationException(
                    "A one-corner near-plane crossing did not produce a clipped triangular SVG mesh.");
            }
            AssertFiniteMesh(clippedMesh, "near clipped");
            var textureSize = StageControl.EstimateReference3DProjectiveTextureSize(clippedMesh);
            if (!float.IsFinite(textureSize.Width)
                || !float.IsFinite(textureSize.Height)
                || textureSize.Width < 1
                || textureSize.Height < 1)
            {
                throw new InvalidOperationException(
                    $"The near-clipped SVG produced an invalid texture size: {textureSize}.");
            }

            using var bitmap = RenderGdi(nearStage);
            var materialVisible = false;
            for (var y = 0; y < bitmap.Height && !materialVisible; y++)
            {
                for (var x = 0; x < bitmap.Width; x++)
                {
                    if (!PixelRgbNear(bitmap.GetPixel(x, y), expected, 28)) continue;
                    materialVisible = true;
                    break;
                }
            }
            if (!materialVisible)
            {
                throw new InvalidOperationException(
                    "The triangular near-clipped SVG mesh did not paint its visible GDI material wedge.");
            }
            Console.WriteLine("scene_reference_projective_near_clip=ok");

            static void AssertFiniteMesh(
                IReadOnlyList<Reference3DProjectiveTriangle> mesh,
                string label)
            {
                if (mesh.Count == 0 || mesh.SelectMany(triangle => new[]
                    {
                        triangle.A,
                        triangle.B,
                        triangle.C
                    }).Any(vertex =>
                        !float.IsFinite(vertex.Flat.X)
                        || !float.IsFinite(vertex.Flat.Y)
                        || !float.IsFinite(vertex.Screen.X)
                        || !float.IsFinite(vertex.Screen.Y)
                        || !float.IsFinite(vertex.U)
                        || !float.IsFinite(vertex.V)
                        || !float.IsFinite(vertex.Camera.X)
                        || !float.IsFinite(vertex.Camera.Y)
                        || !float.IsFinite(vertex.Camera.Z)
                        || vertex.Camera.Z < StageControl.ReferenceNearPlane))
                {
                    throw new InvalidOperationException(
                        $"The {label} projective mesh contained empty, non-finite, or near-plane-invalid triangles.");
                }
            }
        }

        void AssertPerspectiveImportedSvgMaterial()
        {
            const string source = """
                <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 360 240">
                  <rect x="0" y="0" width="180" height="120" fill="#ef4050"/>
                  <rect x="180" y="0" width="180" height="120" fill="#2dcc78"/>
                  <rect x="0" y="120" width="180" height="120" fill="#269ad9"/>
                  <rect x="180" y="120" width="180" height="120" fill="#f3c846"/>
                </svg>
                """;
            const float objectAngle = 0.31f;
            const int materialAlpha = 160;
            var sourceSamples = new (PointF Local, Color Color, string Label)[]
            {
                (new PointF(-900, -600), Color.FromArgb(255, 239, 64, 80), "top-left mesh boundary"),
                (new PointF(900, -600), Color.FromArgb(255, 45, 204, 120), "top-right"),
                (new PointF(-900, 600), Color.FromArgb(255, 38, 154, 217), "bottom-left"),
                (new PointF(900, 600), Color.FromArgb(255, 243, 200, 70), "bottom-right")
            };
            var materialScene = new VectorScene();
            materialScene.CreateEmpty();
            var materialObject = materialScene.AddImportedSvgObject(
                0,
                PointF.Empty,
                new SizeF(3_600, 2_400),
                objectAngle,
                source,
                "Perspective stripes");
            materialScene.Argb[materialObject] = Color.FromArgb(
                materialAlpha,
                128,
                128,
                128).ToArgb();
            using var materialStage = CreateStage(materialScene);
            var perspectiveView = new SceneDefinition { Dimension = SceneDimension.ThreeD };
            perspectiveView.Camera.Projection = CameraProjection.Perspective;
            materialStage.ConfigureReferenceView(perspectiveView, SceneDimension.ThreeD);
            materialStage.ResetReferenceCameraView();
            materialStage.SetReferenceCameraOrientation(0, 0);
            var materialPose = System.Numerics.Matrix4x4.CreateRotationX(0.92f);
            materialStage.SetSceneCompositionResult(CompositionResult(materialPose), materialScene);
            if (!materialStage.TryGetReference3DProjectiveMesh(
                    materialObject,
                    usePrimaryContourQuad: true,
                    out var materialMesh)
                || materialMesh.Length < 2)
            {
                throw new InvalidOperationException(
                    "The perspective SVG fixture did not produce an exact projective quad mesh.");
            }

            var materialExpectedSamples = new List<(PointF Source, Color Expected, string Label)>();
            using (var bitmap = RenderGdi(materialStage))
            {
                var cos = MathF.Cos(objectAngle);
                var sin = MathF.Sin(objectAngle);
                foreach (var sample in sourceSamples)
                {
                    var sourcePoint = new PointF(
                        cos * sample.Local.X - sin * sample.Local.Y,
                        sin * sample.Local.X + cos * sample.Local.Y);
                    if (!materialStage.TryProjectScenePoint(
                            materialObject,
                            sourcePoint,
                            out var projected,
                            out _))
                    {
                        throw new InvalidOperationException(
                            $"Perspective SVG {sample.Label} sample could not be projected.");
                    }
                    var expected = LayerBlendCompositor.CompositeColorForRegression(
                        background,
                        Color.FromArgb(materialAlpha, sample.Color),
                        LayerBlendMode.Normal);
                    materialExpectedSamples.Add((sourcePoint, expected, sample.Label));
                    AssertPixelNear(
                        Sample(bitmap, projected),
                        expected,
                        $"Perspective SVG {sample.Label} lost its source-space material position",
                        tolerance: 24);
                }

                var outsideLocal = new PointF(0, 1_700);
                var outsideSource = new PointF(
                    cos * outsideLocal.X - sin * outsideLocal.Y,
                    sin * outsideLocal.X + cos * outsideLocal.Y);
                if (!materialStage.TryProjectScenePoint(
                        materialObject,
                        outsideSource,
                        out var outsideProjected,
                        out _))
                {
                    throw new InvalidOperationException("The perspective SVG outside sample could not be projected.");
                }
                materialExpectedSamples.Add((outsideSource, background, "outside contour"));
                AssertPixelNear(
                    Sample(bitmap, outsideProjected),
                    background,
                    "Perspective SVG material escaped its projected outer contour",
                    tolerance: 4);
            }

            const int captureBorder = 6;
            var screenBounds = SystemInformation.VirtualScreen;
            using var form = new Form
            {
                ShowInTaskbar = false,
                TopMost = true,
                FormBorderStyle = FormBorderStyle.None,
                AutoScaleMode = AutoScaleMode.None,
                StartPosition = FormStartPosition.Manual,
                BackColor = Color.Fuchsia,
                Padding = new Padding(captureBorder),
                ClientSize = new Size(stageWidth + captureBorder * 2, stageHeight + captureBorder * 2),
                Location = new Point(
                    Math.Max(screenBounds.Left, screenBounds.Right - stageWidth - captureBorder * 2),
                    Math.Max(screenBounds.Top, screenBounds.Bottom - stageHeight - captureBorder * 2))
            };
            using var direct2DStage = new StageControl(materialScene)
            {
                Dock = DockStyle.Fill,
                BackColor = background,
                WorldGridOpacity = 0
            };
            direct2DStage.ConfigureReferenceView(perspectiveView, SceneDimension.ThreeD);
            direct2DStage.ResetReferenceCameraView();
            direct2DStage.SetReferenceCameraOrientation(0, 0);
            direct2DStage.SetSceneCompositionResult(CompositionResult(materialPose), materialScene);
            form.Controls.Add(direct2DStage);
            form.Show();
            form.Activate();
            form.BringToFront();
            using var materialCapture = CapturePresentedStage(form, direct2DStage, Color.Fuchsia);
            if (!direct2DStage.LastFrameUsedDirect2D || !direct2DStage.GpuAccelerationActive)
            {
                throw new InvalidOperationException(
                    "The real-HWND Direct2D Stage did not render the perspective SVG material mesh.");
            }
            var direct2DPixelsAvailable = materialCapture is not null;
            if (materialCapture is not null)
            {
                foreach (var sample in materialExpectedSamples)
                {
                    if (!direct2DStage.TryProjectScenePoint(
                            materialObject,
                            sample.Source,
                            out var projected,
                            out _)
                        || !HasPixelNearColor(
                            materialCapture,
                            CapturePoint(form, direct2DStage, projected),
                            sample.Expected,
                            radius: 2,
                            tolerance: 28))
                    {
                        throw new InvalidOperationException(
                            $"The Direct2D perspective SVG {sample.Label} pixel disagreed with the GDI/source-space material.");
                    }
                }
            }

            trajectoryStage.ConfigureReferenceView(perspectiveView, SceneDimension.ThreeD);
            trajectoryStage.ResetReferenceCameraView();
            trajectoryStage.SetReferenceCameraOrientation(0, 0);
            trajectoryStage.SetSceneCompositionResult(CompositionResult(spatialPose), trajectoryScene);
            var trajectoryExpectedSamples = new List<(PointF Source, Color Expected)>();
            using (var trajectoryBitmap = RenderGdi(trajectoryStage))
            {
                foreach (var sourcePoint in trajectorySamples)
                {
                    if (!trajectoryStage.TryProjectScenePoint(
                            trajectoryObject,
                            sourcePoint,
                            out var projected,
                            out _))
                    {
                        throw new InvalidOperationException("A perspective trajectory-gradient sample could not be projected.");
                    }
                    trajectoryExpectedSamples.Add((sourcePoint, Sample(trajectoryBitmap, projected)));
                }
            }

            direct2DStage.BindScene(trajectoryScene);
            direct2DStage.ConfigureReferenceView(perspectiveView, SceneDimension.ThreeD);
            direct2DStage.ResetReferenceCameraView();
            direct2DStage.SetReferenceCameraOrientation(0, 0);
            direct2DStage.SetSceneCompositionResult(CompositionResult(spatialPose), trajectoryScene);
            direct2DStage.Invalidate();
            direct2DStage.Update();
            Application.DoEvents();
            using var trajectoryCapture = direct2DPixelsAvailable
                ? CapturePresentedStage(form, direct2DStage, Color.Fuchsia)
                : null;
            if (!direct2DStage.LastFrameUsedDirect2D || !direct2DStage.GpuAccelerationActive)
            {
                throw new InvalidOperationException(
                    "The real-HWND Direct2D Stage did not render the perspective path-gradient mesh.");
            }
            if (direct2DPixelsAvailable && trajectoryCapture is null)
            {
                throw new InvalidOperationException("The visible Direct2D trajectory-gradient window could not be captured.");
            }
            if (trajectoryCapture is not null)
            {
                foreach (var sample in trajectoryExpectedSamples)
                {
                    if (!direct2DStage.TryProjectScenePoint(
                            trajectoryObject,
                            sample.Source,
                            out var projected,
                            out _))
                    {
                        throw new InvalidOperationException(
                            $"The Direct2D perspective trajectory source point {sample.Source} could not be projected.");
                    }
                    var capturePoint = CapturePoint(form, direct2DStage, projected);
                    if (!HasPixelNearColor(
                            trajectoryCapture,
                            capturePoint,
                            sample.Expected,
                            radius: 2,
                            tolerance: 32))
                    {
                        throw new InvalidOperationException(
                            "The Direct2D perspective trajectory gradient disagreed with its GDI material sample: "
                            + $"source={sample.Source}, projected={projected}, capture={capturePoint}, "
                            + $"actual={Sample(trajectoryCapture, capturePoint).ToArgb():X8}, "
                            + $"expected={sample.Expected.ToArgb():X8}.");
                    }
                }
            }

            if (crossingDirect2DSamples.Count < 4)
            {
                throw new InvalidOperationException("The crossing-card fixture did not retain its oblique Direct2D samples.");
            }
            direct2DStage.BindScene(crossingScene);
            direct2DStage.ConfigureReferenceView(crossingView, SceneDimension.ThreeD);
            direct2DStage.ResetReferenceCameraView();
            direct2DStage.SetReferenceCameraOrientation(0.43f, -0.24f);
            direct2DStage.SetSceneCompositionResult(
                CompositionResult(crossingCardATransform, crossingCardBTransform),
                crossingScene);
            direct2DStage.Invalidate();
            direct2DStage.Update();
            Application.DoEvents();
            using var crossingCapture = direct2DPixelsAvailable
                ? CapturePresentedStage(form, direct2DStage, Color.Fuchsia)
                : null;
            if (!direct2DStage.LastFrameUsedDirect2D || !direct2DStage.GpuAccelerationActive)
            {
                throw new InvalidOperationException(
                    "The real-HWND Direct2D Stage did not render projective crossing-card fragments.");
            }
            if (direct2DPixelsAvailable && crossingCapture is null)
            {
                throw new InvalidOperationException("The visible Direct2D crossing-card window could not be captured.");
            }
            if (crossingCapture is not null)
            {
                foreach (var sample in crossingDirect2DSamples)
                {
                    if (!HasPixelNearColor(
                            crossingCapture,
                            CapturePoint(form, direct2DStage, sample.Screen),
                            sample.ExpectedColor,
                            radius: 2,
                            tolerance: 28))
                    {
                        throw new InvalidOperationException(
                            $"The Direct2D crossing-card fragment painted the wrong foreground at {sample.Screen}.");
                    }
                }
                for (var edgeSample = -4; edgeSample <= 4; edgeSample++)
                {
                    var crossingX = edgeSample * 200f;
                    if (!direct2DStage.TryProjectScenePosition(
                            new System.Numerics.Vector3(crossingX, 0, 0),
                            out var edgePoint,
                            out _)
                        || !HasPixelNearColor(
                            crossingCapture,
                            CapturePoint(form, direct2DStage, edgePoint),
                            crossingEdgeColor,
                            radius: 3,
                            tolerance: 28))
                    {
                        throw new InvalidOperationException(
                            $"The Direct2D crossing-card intersection edge was discontinuous at X={crossingX:0.###}.");
                    }
                }
            }

            direct2DStage.BindScene(grazingScene);
            direct2DStage.ConfigureReferenceView(grazingView, SceneDimension.ThreeD);
            direct2DStage.ResetReferenceCameraView();
            direct2DStage.SetReferenceCameraOrientation(0, 0);
            direct2DStage.SetSceneCompositionResult(
                CompositionResult(System.Numerics.Matrix4x4.Identity, grazingTiltedTransform),
                grazingScene);
            direct2DStage.Invalidate();
            direct2DStage.Update();
            Application.DoEvents();
            using var grazingCapture = direct2DPixelsAvailable
                ? CapturePresentedStage(form, direct2DStage, Color.Fuchsia)
                : null;
            if (!direct2DStage.LastFrameUsedDirect2D || !direct2DStage.GpuAccelerationActive)
            {
                throw new InvalidOperationException(
                    "The real-HWND Direct2D Stage did not render the grazing intersection fixture.");
            }
            if (direct2DPixelsAvailable && grazingCapture is null)
            {
                throw new InvalidOperationException(
                    "The visible Direct2D grazing-intersection window could not be captured.");
            }
            var grazingCapturePoint = CapturePoint(form, direct2DStage, grazingEdgeSample);
            if (grazingCapture is not null
                && !PixelRgbNear(Sample(grazingCapture, grazingCapturePoint), grazingEdgeColor, 16))
            {
                throw new InvalidOperationException(
                    "Direct2D did not paint the successful singleton-fragment intersection edge.");
            }
            if (cyclicDirect2DSamples.Count < 6)
            {
                throw new InvalidOperationException(
                    "The finite cyclic-card fixture did not retain its Direct2D depth samples.");
            }
            direct2DStage.BindScene(cyclicCardScene);
            direct2DStage.ConfigureReferenceView(cyclicCardView, SceneDimension.ThreeD);
            direct2DStage.ResetReferenceCameraView();
            direct2DStage.SetReferenceCameraOrientation(0, 0);
            direct2DStage.SetSceneCompositionResult(
                CompositionResult(cyclicCardTransforms),
                cyclicCardScene);
            direct2DStage.Invalidate();
            direct2DStage.Update();
            Application.DoEvents();
            using var cyclicCapture = direct2DPixelsAvailable
                ? CapturePresentedStage(form, direct2DStage, Color.Fuchsia)
                : null;
            if (direct2DPixelsAvailable && cyclicCapture is null)
            {
                throw new InvalidOperationException(
                    "The visible Direct2D finite cyclic-card window could not be captured.");
            }
            if (cyclicCapture is not null)
            {
                foreach (var sample in cyclicDirect2DSamples)
                {
                    if (!HasPixelNearColor(
                            cyclicCapture,
                            CapturePoint(form, direct2DStage, sample.Screen),
                            sample.ExpectedColor,
                            radius: 2,
                            tolerance: 28))
                    {
                        throw new InvalidOperationException(
                            $"The Direct2D finite cyclic-card fragment painted the wrong foreground at {sample.Screen}.");
                    }
                }
            }
            Console.WriteLine(direct2DPixelsAvailable
                ? "scene_reference_projective_direct2d_pixels=ok"
                : "scene_reference_projective_direct2d_pixels=skipped_no_visible_desktop");
            form.Close();
        }

        void AssertProjectedFillGradient(
            VectorScene gradientScene,
            StageControl gradientStage,
            int gradientObject,
            GradientKind kind,
            PointF start,
            PointF end,
            IReadOnlyList<PointF> samplePoints,
            PointF[]? path)
        {
            gradientScene.SetGradientPaint(gradientObject, kind, gradientStops, start, end);
            if (kind == GradientKind.ShapeRadial)
            {
                gradientScene.SetShapeGradientMapping(
                    gradientObject,
                    gradientScene.GetObjectBoundaryContours(gradientObject));
            }
            if (path is { Length: > 1 })
            {
                gradientScene.SetGradientPath(gradientObject, path);
                if (!gradientScene.HasGradientPath(gradientObject))
                {
                    throw new InvalidOperationException(
                        "The trajectory-gradient fixture did not retain its source path.");
                }
            }
            else
            {
                gradientScene.ClearGradientPath(gradientObject);
            }
            var label = path is { Length: > 1 } ? "trajectory" : kind.ToString();

            gradientStage.SetSceneCompositionResult(
                CompositionResult(System.Numerics.Matrix4x4.Identity),
                gradientScene);
            var expected = new Color[samplePoints.Count];
            using (var planarGradientBitmap = RenderGdi(gradientStage))
            {
                for (var index = 0; index < samplePoints.Count; index++)
                {
                    var samplePoint = samplePoints[index];
                    expected[index] = Sample(
                        planarGradientBitmap,
                        gradientStage.WorldToScreen(samplePoint.X, samplePoint.Y));
                }
            }

            gradientStage.SetSceneCompositionResult(CompositionResult(spatialPose), gradientScene);
            using (var spatialGradientBitmap = RenderGdi(gradientStage))
            {
                for (var index = 0; index < samplePoints.Count; index++)
                {
                    if (!gradientStage.TryProjectScenePoint(
                            gradientObject,
                            samplePoints[index],
                            out var projectedSample,
                            out _))
                    {
                        throw new InvalidOperationException(
                            $"The {label} gradient sample {index} could not be projected.");
                    }
                    AssertPixelNear(
                        Sample(spatialGradientBitmap, projectedSample),
                        expected[index],
                        $"The 2D front view changed the {label} fill gradient sample {index}",
                        tolerance: 10);
                }
            }

            var perspectiveView = new SceneDefinition { Dimension = SceneDimension.ThreeD };
            perspectiveView.Camera.Projection = CameraProjection.Perspective;
            gradientStage.ConfigureReferenceView(perspectiveView, SceneDimension.ThreeD);
            gradientStage.ResetReferenceCameraView();
            gradientStage.SetReferenceCameraOrientation(0, 0);
            gradientStage.SetSceneCompositionResult(CompositionResult(spatialPose), gradientScene);
            using (var perspectiveGradientBitmap = RenderGdi(gradientStage))
            {
                for (var index = 0; index < samplePoints.Count; index++)
                {
                    if (!gradientStage.TryProjectScenePoint(
                            gradientObject,
                            samplePoints[index],
                            out var perspectiveSample,
                            out _))
                    {
                        throw new InvalidOperationException(
                            $"The perspective {label} gradient sample {index} could not be projected.");
                    }
                    AssertPixelNear(
                        Sample(perspectiveGradientBitmap, perspectiveSample),
                        expected[index],
                        $"Perspective changed the {label} fill gradient sample {index}",
                        tolerance: path is { Length: > 1 } || kind == GradientKind.ShapeRadial ? 22 : 16);
                }
            }
            gradientStage.ConfigureReferenceView(null, SceneDimension.TwoD);
            gradientStage.SetSceneCompositionResult(CompositionResult(spatialPose), gradientScene);
        }

        StageControl CreateStage(VectorScene source)
        {
            var result = new StageControl(source)
            {
                ClientSize = new Size(stageWidth, stageHeight),
                BackColor = background,
                WorldGridOpacity = 0
            };
            result.ConfigureReferenceView(null, SceneDimension.ThreeD);
            result.SetReferenceCameraOrientation(0.83f, -0.41f);
            result.ConfigureReferenceView(null, SceneDimension.TwoD);
            return result;
        }

        Bitmap RenderGdi(StageControl source)
        {
            var bitmap = new Bitmap(source.ClientSize.Width, source.ClientSize.Height);
            using var graphics = Graphics.FromImage(bitmap);
            drawGdi.Invoke(source, [graphics]);
            return bitmap;
        }

        Bitmap? CapturePresentedStage(Form form, StageControl source, Color sentinel)
        {
            if (source.ClientSize != new Size(stageWidth, stageHeight))
            {
                throw new InvalidOperationException(
                    $"The Direct2D capture Stage changed size: {source.ClientSize.Width}x{source.ClientSize.Height}.");
            }

            for (var attempt = 0; attempt < 8; attempt++)
            {
                form.BringToFront();
                source.Invalidate();
                source.Update();
                Application.DoEvents();
                System.Threading.Thread.Sleep(20);
                Application.DoEvents();

                var captureBounds = form.RectangleToScreen(form.ClientRectangle);
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
                }
                catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
                    or System.Runtime.InteropServices.ExternalException
                    or ArgumentException)
                {
                    capture?.Dispose();
                    continue;
                }

                var sentinelPoints = new[]
                {
                    new Point(2, 2),
                    new Point(capture.Width - 3, 2),
                    new Point(2, capture.Height - 3),
                    new Point(capture.Width - 3, capture.Height - 3)
                };
                if (sentinelPoints.All(point => PixelRgbNear(capture.GetPixel(point.X, point.Y), sentinel, 8)))
                {
                    return capture;
                }
                capture.Dispose();
            }
            return null;
        }

        static PointF CapturePoint(Form form, StageControl source, PointF clientPoint)
        {
            var screenPoint = source.PointToScreen(Point.Round(clientPoint));
            var captureOrigin = form.PointToScreen(Point.Empty);
            return new PointF(screenPoint.X - captureOrigin.X, screenPoint.Y - captureOrigin.Y);
        }

        static bool PixelRgbNear(Color actual, Color expected, int tolerance)
        {
            return Math.Abs(actual.R - expected.R) <= tolerance
                && Math.Abs(actual.G - expected.G) <= tolerance
                && Math.Abs(actual.B - expected.B) <= tolerance;
        }

        static SceneCompositionResult CompositionResult(params System.Numerics.Matrix4x4[] transforms)
        {
            return new SceneCompositionResult(
                new SceneCompositionObjectOwner[transforms.Length],
                transforms.Select(transform => new SceneCompositionObjectPose(transform)).ToArray());
        }

        static SceneCompositionResult PoseResult(
            System.Numerics.Matrix4x4 transform,
            System.Numerics.Vector3 extrusionVector)
        {
            return new SceneCompositionResult(
                new SceneCompositionObjectOwner[1],
                [new SceneCompositionObjectPose(transform, extrusionVector)]);
        }

        static Color Sample(Bitmap bitmap, PointF point)
        {
            return bitmap.GetPixel(
                Math.Clamp((int)MathF.Round(point.X), 0, bitmap.Width - 1),
                Math.Clamp((int)MathF.Round(point.Y), 0, bitmap.Height - 1));
        }

        static bool HasPixelNearColor(
            Bitmap bitmap,
            PointF point,
            Color expected,
            int radius,
            int tolerance)
        {
            var centerX = Math.Clamp((int)MathF.Round(point.X), 0, bitmap.Width - 1);
            var centerY = Math.Clamp((int)MathF.Round(point.Y), 0, bitmap.Height - 1);
            for (var y = Math.Max(0, centerY - radius); y <= Math.Min(bitmap.Height - 1, centerY + radius); y++)
            {
                for (var x = Math.Max(0, centerX - radius); x <= Math.Min(bitmap.Width - 1, centerX + radius); x++)
                {
                    var actual = bitmap.GetPixel(x, y);
                    if (Math.Abs(actual.R - expected.R) <= tolerance
                        && Math.Abs(actual.G - expected.G) <= tolerance
                        && Math.Abs(actual.B - expected.B) <= tolerance)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        static void AssertPixelNear(Color actual, Color expected, string message, int tolerance = 1)
        {
            if (Math.Abs(actual.A - expected.A) <= tolerance
                && Math.Abs(actual.R - expected.R) <= tolerance
                && Math.Abs(actual.G - expected.G) <= tolerance
                && Math.Abs(actual.B - expected.B) <= tolerance)
            {
                return;
            }
            throw new InvalidOperationException(
                $"{message}: actual={actual.ToArgb():X8}, expected={expected.ToArgb():X8}.");
        }
    }

    public static void RunStageRendererRegression()
    {
        RunLayerBlendRegression();
        RunSceneReferenceRenderRegression();
        RunImportedSvgRasterizerRegression();
        RunImportedSvgBreakApartRegression();
        RunSelectionHighlightStyleRegression();
        RunSelectionUsabilityRegression();
        RunTemporaryCanvasPanRegression();
        RunImmediateMarqueeOverlayRegression();
        RunSceneToolPaletteVisibilityRegression();
        RunMarqueeToolPolicyRegression();
        RunDistortPointerRegression();
        RunZoomLodPreviewRegression();
        RunWorkspacePanelAnimationRegression();
        RunWorldGridRegression();
        RunDenseOverlapLodRegression();
        RunLinkedFillBoundaryRegression();
        RunShapeToolRegression();
        RunReleaseNotesRegression();
        RunHotReloadModuleRoutingRegression();
        RunColorHarmonyRegression();
        RunGradientPresetRegression();
        RunGradientPaintRegression();
        RunLineSegmentMergeRegression();
        RunClosedFillRegionRegression();
        RunTransformGeometryRegression();
        RunSoftBrushRegression();
        RunBrushFrequencyRegression();
        RunPressureBrushRegression();
        RunMixingBrushDirect2DRegression();
        RunBrushWorldScaleRegression();
        RunBrushEraserRegression();

        var editable = new VectorScene();
        editable.CreateEmpty();
        var editableObject = editable.AddObject(0, new PointF(120, 80), new SizeF(180, 120), 0, 0, Color.Teal, 12, ShapeKind.Rectangle);
        editable.SetLinearGradient(editableObject, Color.Teal, Color.Coral);
        var editableText = editable.AddTextObject(
            0,
            new PointF(1_000, -800),
            new TextObjectData(
                "T",
                TextGeometry.FallbackFontFamilyName,
                24,
                TextFontStyle.Bold,
                TextHorizontalAlignment.Center,
                new SizeF(800, 1_000)),
            Color.White);
        var underlay = new VectorScene();
        underlay.CreateEmpty();
        underlay.AddFreehandStroke(
            0,
            [new PointF(-200, -120), new PointF(-120, -40), new PointF(-40, -100)],
            12,
            Color.Coral,
            brushStroke: false,
            12);
        var underlayOverlap = new PointF(-120, 100);
        underlay.AddObject(0, underlayOverlap, new SizeF(120, 90), 0, 0, Color.LimeGreen, 13, ShapeKind.Rectangle);
        var onionSkin = new VectorScene();
        onionSkin.CreateEmpty();
        onionSkin.AddObject(0, underlayOverlap, new SizeF(120, 90), 0, 0, Color.FromArgb(96, Color.CornflowerBlue), 12, ShapeKind.Rectangle);

        using var form = new Form
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-30_000, -30_000),
            ClientSize = new Size(640, 420)
        };
        using var stage = new StageControl(editable) { Dock = DockStyle.Fill };
        stage.BindUnderlayScene(underlay);
        stage.BindOnionSkinScene(onionSkin);
        form.Controls.Add(stage);
        form.Show();
        Application.DoEvents();
        stage.Invalidate();
        stage.Update();
        Application.DoEvents();

        if (!stage.LastFrameUsedDirect2D
            || !stage.GpuAccelerationActive
            || !stage.ImmediateGpuPresentationEnabled)
        {
            throw new InvalidOperationException("The Stage did not activate the immediate Direct2D GPU hardware target.");
        }

        if (stage.LastDirect2DBaseFrameCacheBuilds != 1
            || stage.LastDirect2DBaseFrameCacheReuses != 0)
        {
            throw new InvalidOperationException(
                $"The initial Direct2D frame did not populate exactly one GPU base frame: "
                + $"builds={stage.LastDirect2DBaseFrameCacheBuilds}, reuses={stage.LastDirect2DBaseFrameCacheReuses}.");
        }

        var direct2DExtrusionScene = new VectorScene();
        direct2DExtrusionScene.CreateEmpty();
        var direct2DExtrusionObject = direct2DExtrusionScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(3_600, 2_200),
            angle: 0,
            stroke: 120,
            color: Color.CornflowerBlue,
            strokeColor: Color.DeepPink,
            atoms: 120,
            shapeKind: ShapeKind.Rectangle);
        stage.BindScene(direct2DExtrusionScene);
        stage.ConfigureReferenceView(null, SceneDimension.ThreeD);
        stage.SetReferenceCameraOrientation(0.72f, -0.36f);
        stage.SetSceneCompositionResult(
            new SceneCompositionResult(
                new SceneCompositionObjectOwner[1],
                [new SceneCompositionObjectPose(
                    System.Numerics.Matrix4x4.Identity,
                    new System.Numerics.Vector3(0, 0, 1_800))]),
            direct2DExtrusionScene);
        stage.SetReference3DSelection([direct2DExtrusionObject]);
        stage.Invalidate();
        stage.Update();
        Application.DoEvents();
        if (!stage.LastFrameUsedDirect2D || !stage.GpuAccelerationActive)
        {
            throw new InvalidOperationException(
                "The real-HWND Direct2D Stage did not render the extruded 3D selection path.");
        }

        stage.BindScene(editable);
        stage.ConfigureReferenceView(null, SceneDimension.TwoD);
        stage.BindUnderlayScene(underlay);
        stage.BindOnionSkinScene(onionSkin);
        stage.Invalidate();
        stage.Update();
        Application.DoEvents();
        if (!stage.LastFrameUsedDirect2D || !stage.GpuAccelerationActive)
        {
            throw new InvalidOperationException("Direct2D did not resume after the extruded 3D selection regression.");
        }

        var overlayBaseRevision = stage.BasePresentationRevision;
        var overlayBrushShape = BrushShape.CreateSoftRound();
        stage.SetBrushTipCursor(
            new Point(stage.ClientSize.Width / 2, stage.ClientSize.Height / 2),
            overlayBrushShape,
            VectorUnits.StrokePointsToUnits(18),
            eraser: false);
        stage.Update();
        if (stage.BasePresentationRevision != overlayBaseRevision
            || stage.LastDirect2DBaseFrameCacheBuilds != 0
            || stage.LastDirect2DBaseFrameCacheReuses != 1)
        {
            throw new InvalidOperationException(
                $"A brush-cursor overlay invalidated the GPU base frame: revision={stage.BasePresentationRevision}/{overlayBaseRevision}, "
                + $"builds={stage.LastDirect2DBaseFrameCacheBuilds}, reuses={stage.LastDirect2DBaseFrameCacheReuses}.");
        }

        stage.SetFreehandPreview(
            [new PointF(-80, -40), new PointF(0, 20), new PointF(90, -10)],
            Color.Coral,
            VectorUnits.StrokePointsToUnits(3));
        stage.Update();
        if (stage.BasePresentationRevision != overlayBaseRevision
            || stage.LastDirect2DBaseFrameCacheBuilds != 0
            || stage.LastDirect2DBaseFrameCacheReuses != 1)
        {
            throw new InvalidOperationException("A freehand preview did not reuse the stable Direct2D GPU base frame.");
        }

        stage.ClearBrushTipCursor();
        stage.ClearFreehandPreview();
        stage.Update();
        var revisionBeforeFullInvalidation = stage.BasePresentationRevision;
        stage.Invalidate();
        stage.Update();
        if (stage.BasePresentationRevision != revisionBeforeFullInvalidation + 1
            || stage.LastDirect2DBaseFrameCacheBuilds != 1
            || stage.LastDirect2DBaseFrameCacheReuses != 0)
        {
            throw new InvalidOperationException(
                $"A full Stage invalidation reused a stale GPU base frame: revision={stage.BasePresentationRevision}/{revisionBeforeFullInvalidation}, "
                + $"builds={stage.LastDirect2DBaseFrameCacheBuilds}, reuses={stage.LastDirect2DBaseFrameCacheReuses}.");
        }

        var fillEdgePreviewSnapshot = editable.CreateSnapshot();
        if (!editable.TryConvertFillToBezierPath(editableObject)
            || !editable.TryGetPathBezierSegment(editableObject, 0, out var fillEdgePreviewSegment))
        {
            throw new InvalidOperationException("The Direct2D Fill preview cache regression could not prepare a Bezier edge.");
        }
        stage.InvalidateOverlay();
        stage.Update();
        var fillEdgePreviewBaseRevision = stage.BasePresentationRevision;
        var fillEdgePreviewGeometryRevision = editable.GeometryRevision;
        var fillEdgePreviewControl = VectorUnits.Quantize(new PointF(
            fillEdgePreviewSegment.Control1.X + 24,
            fillEdgePreviewSegment.Control1.Y - 36));
        if (!editable.SetPathBezierSegmentForPreview(
                editableObject,
                fillEdgePreviewSegment.PartIndex,
                fillEdgePreviewSegment.Start,
                fillEdgePreviewControl,
                fillEdgePreviewSegment.Control2,
                fillEdgePreviewSegment.End))
        {
            throw new InvalidOperationException("The Direct2D Fill preview cache regression could not move a Bezier control.");
        }
        stage.InvalidateOverlay();
        stage.Update();
        var fillEdgePreviewObservedGeometryRevision = editable.GeometryRevision;
        var fillEdgePreviewObservedBaseRevision = stage.BasePresentationRevision;
        var fillEdgePreviewBaseBuilds = stage.LastDirect2DBaseFrameCacheBuilds;
        var fillEdgePreviewBaseReuses = stage.LastDirect2DBaseFrameCacheReuses;
        var fillEdgePreviewPathBuilds = stage.LastDirect2DObjectPathGeometryCacheBuilds;
        var fillEdgePreviewRebuilt = fillEdgePreviewObservedGeometryRevision > fillEdgePreviewGeometryRevision
            && fillEdgePreviewObservedBaseRevision == fillEdgePreviewBaseRevision
            && fillEdgePreviewBaseBuilds == 1
            && fillEdgePreviewBaseReuses == 0
            && fillEdgePreviewPathBuilds >= 1;
        editable.RestoreSnapshot(fillEdgePreviewSnapshot);
        stage.Invalidate();
        stage.Update();
        if (!fillEdgePreviewRebuilt)
        {
            throw new InvalidOperationException(
                $"A Bezier Fill preview reused a stale Direct2D base frame: "
                + $"geometry={fillEdgePreviewObservedGeometryRevision}/{fillEdgePreviewGeometryRevision}, "
                + $"baseRevision={fillEdgePreviewObservedBaseRevision}/{fillEdgePreviewBaseRevision}, "
                + $"base={fillEdgePreviewBaseBuilds}/{fillEdgePreviewBaseReuses}, "
                + $"pathBuilds={fillEdgePreviewPathBuilds}.");
        }

        var pointerFeedbackFrames = 0;
        EventHandler pointerFrameRendered = (_, _) => pointerFeedbackFrames++;
        MouseEventHandler pointerFeedback = (_, args) =>
        {
            stage.SetBrushTipCursor(
                new Point(args.X + 4, args.Y + 3),
                overlayBrushShape,
                VectorUnits.StrokePointsToUnits(20),
                eraser: false);
            stage.SetFreehandPreview(
                [new PointF(-30, 0), new PointF(30, 0)],
                Color.White,
                VectorUnits.StrokePointsToUnits(2));
        };
        var invokeMouseDown = typeof(StageControl).GetMethod(
            "OnMouseDown",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The Stage pointer-feedback regression entry point could not be located.");
        var pointerBaseRevision = stage.BasePresentationRevision;
        stage.FrameRendered += pointerFrameRendered;
        stage.MouseDown += pointerFeedback;
        try
        {
            invokeMouseDown.Invoke(
                stage,
                [new MouseEventArgs(MouseButtons.Left, 1, stage.ClientSize.Width / 2, stage.ClientSize.Height / 2, 0)]);
        }
        finally
        {
            stage.MouseDown -= pointerFeedback;
            stage.FrameRendered -= pointerFrameRendered;
        }
        if (pointerFeedbackFrames != 1
            || stage.BasePresentationRevision != pointerBaseRevision
            || stage.LastDirect2DBaseFrameCacheBuilds != 0
            || stage.LastDirect2DBaseFrameCacheReuses != 1
            || stage.LastInteractiveRequestCount < 2
            || stage.LastInteractiveCoalescedRequestCount < 1
            || stage.LastPointerDownToPresentMilliseconds < stage.LastPointerDownHandlerMilliseconds)
        {
            throw new InvalidOperationException(
                $"Pointer-down feedback was not coalesced and presented synchronously: frames={pointerFeedbackFrames}, "
                + $"handler={stage.LastPointerDownHandlerMilliseconds:0.000}ms, total={stage.LastPointerDownToPresentMilliseconds:0.000}ms, "
                + $"requests={stage.LastInteractiveRequestCount}, coalesced={stage.LastInteractiveCoalescedRequestCount}, "
                + $"base={stage.LastDirect2DBaseFrameCacheBuilds}/{stage.LastDirect2DBaseFrameCacheReuses}.");
        }
        var gpuPointerFeedbackHandlerMilliseconds = stage.LastPointerDownHandlerMilliseconds;
        var gpuPointerFeedbackTotalMilliseconds = stage.LastPointerDownToPresentMilliseconds;
        var gpuPointerFeedbackCoalescedRequests = stage.LastInteractiveCoalescedRequestCount;

        var dragElement = editable.HitTestElement(new PointF(120, 80), 0, 0);
        if (!dragElement.IsValid
            || dragElement.Key.ObjectIndex != editableObject
            || dragElement.Key.Kind != DrawingElementKind.Fill)
        {
            throw new InvalidOperationException("The GPU drag-preview regression did not hit its Fill element.");
        }
        stage.SetSelectionState(
            [editableObject],
            editableObject,
            [dragElement],
            dragElement);
        var dragPreviewBaseRevision = stage.BasePresentationRevision;
        var dragFirstMoveStartedAt = Stopwatch.GetTimestamp();
        stage.BeginDragFirstMoveTelemetry(dragFirstMoveStartedAt);
        var dragPreviewOffset = new PointF(96, -48);
        var dragPreviewPresented = stage.PresentSelectionDragPreview(dragPreviewOffset);
        stage.RecordDragFirstMovePreparationTimings(
            snapshotMilliseconds: 0,
            materializeMilliseconds: 0);
        var gpuDragFirstPresentMilliseconds = stage.LastDragFirstPresentMilliseconds;
        var gpuDragFirstMoveTotalMilliseconds = stage.LastDragFirstMoveTotalMilliseconds;
        var gpuDragBudgetMet = gpuDragFirstMoveTotalMilliseconds <= StageControl.DragFirstMoveBudgetMilliseconds;
        if (!dragPreviewPresented
            || !stage.SelectionDragPreviewActive
            || stage.SelectionDragPreviewOffset != dragPreviewOffset
            || !gpuDragBudgetMet
            || gpuDragFirstPresentMilliseconds <= 0
            || stage.BasePresentationRevision != dragPreviewBaseRevision
            || stage.LastDirect2DBaseFrameCacheBuilds != 0
            || stage.LastDirect2DBaseFrameCacheReuses != 1)
        {
            throw new InvalidOperationException(
                $"The first drag preview was not presented from the GPU overlay budget: total={gpuDragFirstMoveTotalMilliseconds:0.000} ms, "
                + $"present={gpuDragFirstPresentMilliseconds:0.000} ms, base={stage.LastDirect2DBaseFrameCacheBuilds}/{stage.LastDirect2DBaseFrameCacheReuses}.");
        }
        stage.ClearSelectionDragPreview();
        stage.SetSelectionState(
            Array.Empty<int>(),
            -1,
            Array.Empty<DrawingElementHit>(),
            DrawingElementHit.None);
        stage.ClearBrushTipCursor();
        stage.ClearFreehandPreview();
        stage.Update();

        AssertTimeline(
            editable.SetLayerBlendMode(0, LayerBlendMode.Overlay),
            "Renderer regression could not enable a non-Normal layer blend mode.");
        stage.Invalidate();
        stage.Update();
        Application.DoEvents();
        if (stage.LastFrameUsedDirect2D)
        {
            throw new InvalidOperationException("A blended frame bypassed the authoritative software layer compositor.");
        }

        var gdiPointerFeedbackFrames = 0;
        EventHandler gdiPointerFrameRendered = (_, _) => gdiPointerFeedbackFrames++;
        MouseEventHandler gdiPointerFeedback = (_, args) => stage.SetBrushTipCursor(
            new Point(args.X + 2, args.Y + 2),
            overlayBrushShape,
            VectorUnits.StrokePointsToUnits(18),
            eraser: false);
        stage.FrameRendered += gdiPointerFrameRendered;
        stage.MouseDown += gdiPointerFeedback;
        try
        {
            invokeMouseDown.Invoke(
                stage,
                [new MouseEventArgs(MouseButtons.Left, 1, stage.ClientSize.Width / 2, stage.ClientSize.Height / 2, 0)]);
            if (gdiPointerFeedbackFrames != 0)
            {
                throw new InvalidOperationException(
                    "Pointer-down synchronously rendered the software fallback before returning to the message loop.");
            }

            stage.Update();
            if (gdiPointerFeedbackFrames != 1 || stage.LastFrameUsedDirect2D)
            {
                throw new InvalidOperationException(
                    $"Queued software pointer feedback did not render exactly once: frames={gdiPointerFeedbackFrames}, "
                    + $"direct2d={stage.LastFrameUsedDirect2D}.");
            }
        }
        finally
        {
            stage.MouseDown -= gdiPointerFeedback;
            stage.FrameRendered -= gdiPointerFrameRendered;
        }
        stage.ClearBrushTipCursor();

        AssertTimeline(
            editable.SetLayerBlendMode(0, LayerBlendMode.Normal),
            "Renderer regression could not restore the Normal layer blend mode.");
        stage.Invalidate();
        stage.Update();
        Application.DoEvents();
        if (!stage.LastFrameUsedDirect2D)
        {
            throw new InvalidOperationException("Direct2D did not resume after restoring all layers to Normal mode.");
        }

        var originalLayerColor = editable.GetLayerColor(0);
        AssertTimeline(
            editable.SetLayerColor(0, Color.DeepPink)
            && editable.SetLayerOutline(0, true),
            "Renderer regression could not enable a colored Outline layer.");
        stage.WorldGridOpacity = 0;
        stage.Invalidate();
        stage.Update();
        Application.DoEvents();
        if (!stage.LastFrameUsedDirect2D || stage.LastStats.TileLod)
        {
            throw new InvalidOperationException("An outlined editable layer did not use the Direct2D object path.");
        }

        using (var outlineBitmap = new Bitmap(stage.ClientSize.Width, stage.ClientSize.Height))
        using (var outlineGraphics = Graphics.FromImage(outlineBitmap))
        {
            var drawGdi = typeof(StageControl).GetMethod(
                "DrawGdi",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("The GDI Outline fallback entry point could not be located.");
            drawGdi.Invoke(stage, [outlineGraphics]);
            var center = Point.Round(stage.WorldToScreen(editable.X[editableObject], editable.Y[editableObject]));
            var leftEdge = Point.Round(stage.WorldToScreen(
                editable.X[editableObject] - editable.Width[editableObject] * 0.5f,
                editable.Y[editableObject]));
            var centerPixel = outlineBitmap.GetPixel(
                Math.Clamp(center.X, 0, outlineBitmap.Width - 1),
                Math.Clamp(center.Y, 0, outlineBitmap.Height - 1));
            var foundOutlinePixel = false;
            for (var y = Math.Max(0, leftEdge.Y - 3); y <= Math.Min(outlineBitmap.Height - 1, leftEdge.Y + 3); y++)
            {
                for (var x = Math.Max(0, leftEdge.X - 3); x <= Math.Min(outlineBitmap.Width - 1, leftEdge.X + 3); x++)
                {
                    var pixel = outlineBitmap.GetPixel(x, y);
                    if (pixel.R >= 180 && pixel.B >= 80 && pixel.G <= 100)
                    {
                        foundOutlinePixel = true;
                        break;
                    }
                }
                if (foundOutlinePixel) break;
            }

            if (!foundOutlinePixel
                || centerPixel.R >= 180 && centerPixel.B >= 80 && centerPixel.G <= 100)
            {
                throw new InvalidOperationException(
                    $"The GDI Outline path did not keep the fill hollow while drawing the layer-color edge: center={centerPixel.ToArgb():X8}, edge={foundOutlinePixel}.");
            }
        }
        editable.SetLayerOutline(0, false);
        editable.SetLayerColor(0, originalLayerColor);
        stage.WorldGridOpacity = 1;
        stage.Invalidate();
        stage.Update();
        Application.DoEvents();

        var polarViewState = stage.CaptureViewState();
        double polarDirect2DCommandMilliseconds;
        double polarDirect2DFrameMilliseconds;
        double polarGdiFrameMilliseconds;
        try
        {
            stage.WorldGridType = WorldGridType.Polar;
            stage.SetVisibleWorldWidth(250);
            stage.Pan(
                -VectorUnits.ToPixels(5_000_000) * stage.Zoom,
                -VectorUnits.ToPixels(3_000_000) * stage.Zoom);
            for (var warmup = 0; warmup < 3; warmup++)
            {
                stage.Invalidate();
                stage.Update();
            }

            const int polarRenderSamples = 12;
            var polarCommandTotal = 0d;
            var polarFrameWatch = Stopwatch.StartNew();
            for (var sample = 0; sample < polarRenderSamples; sample++)
            {
                stage.Invalidate();
                stage.Update();
                polarCommandTotal += stage.LastDirect2DCommandMilliseconds;
            }
            polarFrameWatch.Stop();
            polarDirect2DCommandMilliseconds = polarCommandTotal / polarRenderSamples;
            polarDirect2DFrameMilliseconds = polarFrameWatch.Elapsed.TotalMilliseconds / polarRenderSamples;

            using var polarGdiBitmap = new Bitmap(stage.ClientSize.Width, stage.ClientSize.Height);
            using var polarGdiGraphics = Graphics.FromImage(polarGdiBitmap);
            var drawPolarGdi = typeof(StageControl).GetMethod(
                "DrawGdi",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("The GDI polar-grid fallback entry point could not be located.");
            for (var warmup = 0; warmup < 2; warmup++) drawPolarGdi.Invoke(stage, [polarGdiGraphics]);
            const int polarGdiSamples = 6;
            var polarGdiWatch = Stopwatch.StartNew();
            for (var sample = 0; sample < polarGdiSamples; sample++) drawPolarGdi.Invoke(stage, [polarGdiGraphics]);
            polarGdiWatch.Stop();
            polarGdiFrameMilliseconds = polarGdiWatch.Elapsed.TotalMilliseconds / polarGdiSamples;

            if (!stage.LastFrameUsedDirect2D
                || polarDirect2DCommandMilliseconds > RenderCollectBudgetMilliseconds
                || polarDirect2DFrameMilliseconds > 1000d / 60d
                || polarGdiFrameMilliseconds > 50d)
            {
                throw new InvalidOperationException(
                    $"The zoomed distant polar grid exceeded its bounded render budget: " +
                    $"direct2d={stage.LastFrameUsedDirect2D}, commands={polarDirect2DCommandMilliseconds:0.000}ms, " +
                    $"frame={polarDirect2DFrameMilliseconds:0.000}ms, gdi={polarGdiFrameMilliseconds:0.000}ms.");
            }
        }
        finally
        {
            stage.RestoreViewState(polarViewState);
            stage.Invalidate();
            stage.Update();
        }

        if (stage.LastStats.VisibleObjects != 5)
        {
            throw new InvalidOperationException("The Direct2D scene passes did not include the onion skin, underlay, and editable scene.");
        }

        if (stage.LastOnionSkinScenePassOrder <= 0
            || stage.LastOnionSkinScenePassOrder >= stage.LastUnderlayScenePassOrder
            || stage.LastUnderlayScenePassOrder >= stage.LastEditableScenePassOrder)
        {
            throw new InvalidOperationException("The onion-skin pass did not render below the drawing-object underlay and editable scene.");
        }

        using (var gdiBitmap = new Bitmap(stage.ClientSize.Width, stage.ClientSize.Height))
        using (var gdiGraphics = Graphics.FromImage(gdiBitmap))
        {
            var drawGdi = typeof(StageControl).GetMethod(
                "DrawGdi",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("The GDI fallback entry point could not be located.");
            drawGdi.Invoke(stage, [gdiGraphics]);
            if (stage.LastOnionSkinScenePassOrder <= 0
                || stage.LastOnionSkinScenePassOrder >= stage.LastUnderlayScenePassOrder
                || stage.LastUnderlayScenePassOrder >= stage.LastEditableScenePassOrder)
            {
                throw new InvalidOperationException("The GDI onion-skin pass did not render below the drawing-object underlay and editable scene.");
            }

            var overlapSample = stage.WorldToScreen(underlayOverlap.X, underlayOverlap.Y);
            var overlapPixel = gdiBitmap.GetPixel(
                Math.Clamp((int)MathF.Round(overlapSample.X), 0, gdiBitmap.Width - 1),
                Math.Clamp((int)MathF.Round(overlapSample.Y), 0, gdiBitmap.Height - 1));
            if (overlapPixel.ToArgb() != Color.LimeGreen.ToArgb())
            {
                throw new InvalidOperationException("The GDI onion skin covered the drawing-object underlay at their overlap.");
            }
        }

        if (!ReferenceEquals(stage.Scene, editable))
        {
            throw new InvalidOperationException("The renderer did not restore the editable scene after drawing its underlay.");
        }

        if (!stage.HasCachedDirect2DFreehandGeometry(underlay))
        {
            throw new InvalidOperationException("The Direct2D underlay pass did not populate the freehand geometry cache.");
        }

        var layeredVisibleObjects = stage.LastStats.VisibleObjects;
        stage.BindOnionSkinScene(null);
        const int cachedPathNodeCount = 64;
        var cachedPathNodes = Enumerable.Range(0, cachedPathNodeCount)
            .Select(index =>
            {
                var angle = MathF.Tau * index / cachedPathNodeCount;
                var radius = 140f + MathF.Sin(angle * 5) * 28f;
                var radialDerivative = MathF.Cos(angle * 5) * 140f;
                var anchor = new PointF(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius);
                var derivative = new PointF(
                    MathF.Cos(angle) * radialDerivative - MathF.Sin(angle) * radius,
                    MathF.Sin(angle) * radialDerivative + MathF.Cos(angle) * radius);
                var handleScale = MathF.Tau / cachedPathNodeCount / 3f;
                return new PathBezierNode(
                    anchor,
                    new PointF(
                        anchor.X - derivative.X * handleScale,
                        anchor.Y - derivative.Y * handleScale),
                    new PointF(
                        anchor.X + derivative.X * handleScale,
                        anchor.Y + derivative.Y * handleScale));
            })
            .ToArray();
        var cachedPath = editable.AppendPathBezierObjectContours(
            0,
            [cachedPathNodes],
            VectorUnits.StrokePointsToUnits(2),
            Color.Teal,
            Color.White,
            cachedPathNodeCount);
        if (cachedPath < 0)
        {
            throw new InvalidOperationException("The Direct2D Path cache regression could not create its source geometry.");
        }
        editable.CompleteDeferredBuild();
        stage.SetSelection([cachedPath], cachedPath);
        stage.Invalidate();
        stage.Update();
        if (!stage.LastFrameUsedDirect2D
            || stage.LastDirect2DObjectPathGeometryCacheBuilds != 1
            || stage.LastDirect2DObjectPathGeometryCacheReuses < 2)
        {
            throw new InvalidOperationException(
                $"A selected Fill+Stroke Path did not share one Direct2D geometry: "
                + $"builds={stage.LastDirect2DObjectPathGeometryCacheBuilds}, "
                + $"reuses={stage.LastDirect2DObjectPathGeometryCacheReuses}.");
        }

        stage.Invalidate();
        stage.Update();
        var stablePathCacheMaintenanceMilliseconds = stage.LastDirect2DCacheMaintenanceMilliseconds;
        var stablePathFrameMilliseconds = stage.LastFrameRenderMilliseconds;
        if (stage.LastDirect2DObjectPathGeometryCacheBuilds != 0
            || stage.LastDirect2DObjectPathGeometryCacheReuses < 3
            || stablePathCacheMaintenanceMilliseconds > 1)
        {
            throw new InvalidOperationException(
                $"A stable selected Path did not reuse its Direct2D geometry within budget: "
                + $"builds={stage.LastDirect2DObjectPathGeometryCacheBuilds}, "
                + $"reuses={stage.LastDirect2DObjectPathGeometryCacheReuses}, "
                + $"maintenanceMs={stablePathCacheMaintenanceMilliseconds:0.000}.");
        }

        if (!editable.TryGetPathBezierSegment(cachedPath, 0, out var cachedPathSegment)
            || !editable.SetPathBezierSegment(
                cachedPath,
                0,
                cachedPathSegment.Start,
                new PointF(cachedPathSegment.Control1.X + 4, cachedPathSegment.Control1.Y),
                cachedPathSegment.Control2,
                cachedPathSegment.End))
        {
            throw new InvalidOperationException("The Direct2D Path cache regression could not edit its source anchor geometry.");
        }
        stage.Invalidate();
        stage.Update();
        if (stage.LastDirect2DObjectPathGeometryCacheBuilds != 1
            || stage.LastDirect2DObjectPathGeometryCacheReuses < 2)
        {
            throw new InvalidOperationException(
                $"Editing one Path segment did not rebuild exactly one shared Direct2D geometry: "
                + $"builds={stage.LastDirect2DObjectPathGeometryCacheBuilds}, "
                + $"reuses={stage.LastDirect2DObjectPathGeometryCacheReuses}.");
        }

        var dragGradientStops = new[]
        {
            new GradientStop(0, Color.Teal),
            new GradientStop(1, Color.Coral)
        };
        var dragGradientStart = new PointF(-120, 0);
        var dragGradientEnd = new PointF(120, 0);
        editable.SetGradientPaint(
            cachedPath,
            GradientKind.Linear,
            dragGradientStops,
            dragGradientStart,
            dragGradientEnd);
        stage.Invalidate();
        stage.Update();
        editable.X[cachedPath] += 24;
        editable.Y[cachedPath] += 16;
        dragGradientStart = new PointF(dragGradientStart.X + 24, dragGradientStart.Y + 16);
        dragGradientEnd = new PointF(dragGradientEnd.X + 24, dragGradientEnd.Y + 16);
        editable.SetLinearGradientEndpoints(cachedPath, dragGradientStart, dragGradientEnd);
        stage.Invalidate();
        stage.Update();
        if (stage.LastDirect2DObjectPathGeometryCacheBuilds != 0
            || stage.LastDirect2DObjectPathGeometryCacheReuses < 3
            || stage.LastDirect2DGradientBrushCacheBuilds != 0
            || stage.LastDirect2DGradientBrushCacheReuses < 1)
        {
            throw new InvalidOperationException(
                $"Dragging a linear-gradient Path rebuilt cached Direct2D resources: "
                + $"path={stage.LastDirect2DObjectPathGeometryCacheBuilds}/{stage.LastDirect2DObjectPathGeometryCacheReuses}, "
                + $"brush={stage.LastDirect2DGradientBrushCacheBuilds}/{stage.LastDirect2DGradientBrushCacheReuses}.");
        }

        editable.SetGradientPath(
            cachedPath,
            [
                new PointF(-96 + 24, -20 + 16),
                new PointF(24, 76),
                new PointF(96 + 24, 20 + 16)
            ]);
        stage.Invalidate();
        stage.Update();
        editable.X[cachedPath] += 12;
        editable.Y[cachedPath] += 8;
        dragGradientStart = new PointF(dragGradientStart.X + 12, dragGradientStart.Y + 8);
        dragGradientEnd = new PointF(dragGradientEnd.X + 12, dragGradientEnd.Y + 8);
        editable.SetLinearGradientEndpoints(cachedPath, dragGradientStart, dragGradientEnd);
        stage.Invalidate();
        stage.Update();
        if (stage.LastDirect2DObjectPathGeometryCacheBuilds != 0
            || stage.LastDirect2DPathGradientBrushCacheBuilds != 0
            || stage.LastDirect2DPathGradientBrushCacheReuses != 1)
        {
            throw new InvalidOperationException(
                $"Dragging a trajectory-gradient Path rebuilt cached Direct2D resources: "
                + $"pathBuilds={stage.LastDirect2DObjectPathGeometryCacheBuilds}, "
                + $"brushes={stage.LastDirect2DPathGradientBrushCacheBuilds}/{stage.LastDirect2DPathGradientBrushCacheReuses}.");
        }

        editable.SetGradientPaint(
            cachedPath,
            GradientKind.ShapeRadial,
            dragGradientStops,
            new PointF(24, 16),
            new PointF(144, 16));
        editable.SetShapeGradientMapping(cachedPath, editable.GetObjectBoundaryContours(cachedPath));
        stage.Invalidate();
        stage.Update();
        editable.X[cachedPath] += 20;
        editable.Y[cachedPath] += 12;
        editable.SetLinearGradientEndpoints(
            cachedPath,
            new PointF(44, 28),
            new PointF(164, 28));
        stage.Invalidate();
        stage.Update();
        if (stage.LastDirect2DObjectPathGeometryCacheBuilds != 0
            || stage.LastDirect2DShapeGradientBitmapCacheBuilds != 0
            || stage.LastDirect2DShapeGradientBitmapCacheReuses != 1)
        {
            throw new InvalidOperationException(
                $"Dragging a shape-radial Path rebuilt its cached bitmap or geometry: "
                + $"pathBuilds={stage.LastDirect2DObjectPathGeometryCacheBuilds}, "
                + $"bitmap={stage.LastDirect2DShapeGradientBitmapCacheBuilds}/{stage.LastDirect2DShapeGradientBitmapCacheReuses}.");
        }
        stage.SetSelection([], -1);
        if (editable.RemoveObjects([cachedPath]) != 1)
        {
            throw new InvalidOperationException("The Direct2D Path cache regression did not restore the editable scene.");
        }

        stage.SetSelection([editableObject], editableObject);
        if (!stage.SelectionHighlightAnimating
            || stage.SelectionHighlightPulse < 0f
            || stage.SelectionHighlightPulse > 1f)
        {
            throw new InvalidOperationException("Selecting a fill did not start its bounded highlight animation.");
        }

        var fillEdgeViewState = stage.CaptureViewState();
        stage.SetVisibleWorldWidth(800);
        var fillEdgeOverlaySegments = editable.GetEditableFillBezierSegmentParts(editableObject)
            .Select(part => new FillEdgeBezierOverlaySegment(
                part.PartIndex,
                part.Start,
                part.Control1,
                part.Control2,
                part.End))
            .ToArray();
        stage.SetFillEdgeBezierOverlay(editableObject, fillEdgeOverlaySegments, fillEdgeOverlaySegments[0].PartIndex);
        stage.Invalidate();
        stage.Update();
        Application.DoEvents();
        if (!stage.LastFrameUsedDirect2D
            || !stage.FillEdgeBezierOverlayVisible
            || stage.SelectionHighlightAnimating
            || stage.LastDirect2DFillEdgeBezierOverlayGeometryBuilds != 1)
        {
            throw new InvalidOperationException("The real-HWND Direct2D Stage did not render the non-glowing fill-edge Bezier overlay.");
        }

        var overlayTranslation = new PointF(48, 32);
        stage.SetFillEdgeBezierOverlayTranslation(overlayTranslation);
        stage.Invalidate();
        stage.Update();
        Application.DoEvents();
        var translatedActiveAnchor = fillEdgeOverlaySegments[0].Start;
        translatedActiveAnchor = new PointF(
            translatedActiveAnchor.X + overlayTranslation.X,
            translatedActiveAnchor.Y + overlayTranslation.Y);
        var translatedOverlayHit = stage.HitTestFillEdgeBezierOverlay(
            Point.Round(stage.WorldToScreen(translatedActiveAnchor.X, translatedActiveAnchor.Y)));
        if (!stage.LastFrameUsedDirect2D
            || stage.LastDirect2DFillEdgeBezierOverlayGeometryBuilds != 0
            || !translatedOverlayHit.IsValid
            || translatedOverlayHit.PartIndex != fillEdgeOverlaySegments[0].PartIndex
            || translatedOverlayHit.Handle != EditHandleKind.LineStart)
        {
            throw new InvalidOperationException(
                $"Translating a Fill overlay rebuilt its Direct2D geometry or lost hit testing: "
                + $"builds={stage.LastDirect2DFillEdgeBezierOverlayGeometryBuilds}, hit={translatedOverlayHit}.");
        }
        stage.SetFillEdgeBezierOverlay(
            editableObject,
            fillEdgeOverlaySegments,
            fillEdgeOverlaySegments[0].PartIndex);

        using (var fillEdgeBitmap = new Bitmap(stage.ClientSize.Width, stage.ClientSize.Height))
        using (var fillEdgeGraphics = Graphics.FromImage(fillEdgeBitmap))
        {
            var drawGdi = typeof(StageControl).GetMethod(
                "DrawGdi",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("The GDI fill-edge Bezier overlay entry point could not be located.");
            drawGdi.Invoke(stage, [fillEdgeGraphics]);
            var limePixels = 0;
            for (var y = 0; y < fillEdgeBitmap.Height; y++)
            {
                for (var x = 0; x < fillEdgeBitmap.Width; x++)
                {
                    var pixel = fillEdgeBitmap.GetPixel(x, y);
                    if (pixel.R <= 20 && pixel.G >= 235 && pixel.B <= 20) limePixels++;
                }
            }

            if (limePixels < 20)
            {
                throw new InvalidOperationException("The GDI fallback did not draw a crisp lime fill-edge Bezier overlay.");
            }

            var inactiveAnchor = fillEdgeOverlaySegments[2].Start;
            var inactiveAnchorScreen = Point.Round(stage.WorldToScreen(inactiveAnchor.X, inactiveAnchor.Y));
            var inactiveAnchorHasDarkCore = false;
            var inactiveAnchorHasLimeBorder = false;
            for (var y = Math.Max(0, inactiveAnchorScreen.Y - 1);
                 y <= Math.Min(fillEdgeBitmap.Height - 1, inactiveAnchorScreen.Y + 1);
                 y++)
            {
                for (var x = Math.Max(0, inactiveAnchorScreen.X - 1);
                     x <= Math.Min(fillEdgeBitmap.Width - 1, inactiveAnchorScreen.X + 1);
                     x++)
                {
                    var pixel = fillEdgeBitmap.GetPixel(x, y);
                    if (Math.Abs(pixel.R - stage.BackColor.R) <= 20
                        && Math.Abs(pixel.G - stage.BackColor.G) <= 20
                        && Math.Abs(pixel.B - stage.BackColor.B) <= 20)
                    {
                        inactiveAnchorHasDarkCore = true;
                        break;
                    }
                }
                if (inactiveAnchorHasDarkCore) break;
            }

            for (var y = Math.Max(0, inactiveAnchorScreen.Y - 5);
                 y <= Math.Min(fillEdgeBitmap.Height - 1, inactiveAnchorScreen.Y + 5);
                 y++)
            {
                for (var x = Math.Max(0, inactiveAnchorScreen.X - 5);
                     x <= Math.Min(fillEdgeBitmap.Width - 1, inactiveAnchorScreen.X + 5);
                     x++)
                {
                    var pixel = fillEdgeBitmap.GetPixel(x, y);
                    if (pixel.G >= 160
                        && pixel.G >= pixel.R + 80
                        && pixel.G >= pixel.B + 80)
                    {
                        inactiveAnchorHasLimeBorder = true;
                        break;
                    }
                }
                if (inactiveAnchorHasLimeBorder) break;
            }

            if (!inactiveAnchorHasDarkCore || !inactiveAnchorHasLimeBorder)
            {
                throw new InvalidOperationException("The GDI fill-edge overlay did not draw an inactive segment anchor handle.");
            }
        }
        stage.ClearFillEdgeBezierOverlay();
        stage.RestoreViewState(fillEdgeViewState);

        stage.SetSelection([editableText], editableText);
        var textAreaRightWorld = stage.GetTextAreaHandleWorldPoint(editableText, EditHandleKind.TextAreaRight);
        var textAreaRightScreen = Point.Round(stage.WorldToScreen(textAreaRightWorld.X, textAreaRightWorld.Y));
        if (!stage.TextAreaOverlayVisible(editableText)
            || !stage.TextAreaResizeHandlesVisible(editableText)
            || stage.HitTestHandle(textAreaRightScreen, editableText) != EditHandleKind.TextAreaRight)
        {
            throw new InvalidOperationException("The renderer Stage did not expose the selected text area's resize overlay.");
        }
        stage.Invalidate();
        stage.Update();
        Application.DoEvents();
        if (!stage.LastFrameUsedDirect2D)
        {
            throw new InvalidOperationException("Direct2D did not render the selected text-area overlay.");
        }

        using (var textAreaBitmap = new Bitmap(stage.ClientSize.Width, stage.ClientSize.Height))
        using (var textAreaGraphics = Graphics.FromImage(textAreaBitmap))
        {
            var drawGdi = typeof(StageControl).GetMethod(
                "DrawGdi",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("The GDI text-area overlay entry point could not be located.");
            drawGdi.Invoke(stage, [textAreaGraphics]);
            var foundGreenHandlePixel = false;
            for (var y = Math.Max(0, textAreaRightScreen.Y - 4); y <= Math.Min(textAreaBitmap.Height - 1, textAreaRightScreen.Y + 4); y++)
            {
                for (var x = Math.Max(0, textAreaRightScreen.X - 4); x <= Math.Min(textAreaBitmap.Width - 1, textAreaRightScreen.X + 4); x++)
                {
                    var pixel = textAreaBitmap.GetPixel(x, y);
                    if (pixel.G >= 140 && pixel.B >= 120 && pixel.R <= 130)
                    {
                        foundGreenHandlePixel = true;
                        break;
                    }
                }
                if (foundGreenHandlePixel) break;
            }
            if (!foundGreenHandlePixel)
            {
                throw new InvalidOperationException("The GDI fallback did not draw the green text-area resize handle.");
            }
        }

        var selectedLine = editable.AddLineSegment(
            0,
            new PointF(-120, -60),
            new PointF(80, 40),
            VectorUnits.StrokePointsToUnits(3),
            Color.Transparent,
            Color.Gold,
            8);
        stage.SetSelection([selectedLine], selectedLine);
        if (!stage.SelectionHighlightAnimating)
        {
            throw new InvalidOperationException("Selecting a line did not retain the highlight animation.");
        }

        stage.SetSelection([], -1);
        if (stage.SelectionHighlightAnimating)
        {
            throw new InvalidOperationException("Clearing the Stage selection did not stop the highlight animation.");
        }
        if (editable.RemoveObjects([selectedLine]) != 1)
        {
            throw new InvalidOperationException("Selection animation setup did not restore the renderer regression scene.");
        }

        var tiledUnderlay = new VectorScene();
        tiledUnderlay.CreateEmpty();
        const int tiledUnderlayObjectCount = 56_000;
        for (var index = 0; index < tiledUnderlayObjectCount; index++)
        {
            var x = (index % 1000) * 2 - 1000;
            var y = (index / 1000) * 2 - 56;
            tiledUnderlay.AppendObject(
                0,
                new PointF(x, y),
                new SizeF(18, 18),
                0,
                0,
                Color.Coral,
                Color.Transparent,
                3,
                ShapeKind.Rectangle);
        }
        tiledUnderlay.CompleteDeferredBuild();

        stage.BindUnderlayScene(tiledUnderlay);
        stage.ZoomAt(new Point(stage.Width / 2, stage.Height / 2), 0.1f);
        stage.Update();
        Application.DoEvents();

        if (!stage.LastFrameUsedDirect2D
            || !stage.LastStats.TileLod
            || stage.LastStats.TileDraws <= 0
            || stage.LastStats.VisibleObjects != 2
            || stage.LastStats.DrawnObjects != 2)
        {
            throw new InvalidOperationException("Mixed object and tile LOD rendering produced inconsistent statistics.");
        }
        var mixedLodTileDraws = stage.LastStats.TileDraws;

        if (stage.HasCachedDirect2DFreehandGeometry(underlay))
        {
            throw new InvalidOperationException("Switching the underlay retained stale Direct2D freehand geometry.");
        }

        stage.ZoomAt(new Point(stage.Width / 2, stage.Height / 2), 50f);
        stage.Update();
        Application.DoEvents();
        var expectedObjectDraws = tiledUnderlayObjectCount + editable.ObjectCount;
        if (stage.LastStats.TileLod
            || stage.LastStats.VisibleObjects != expectedObjectDraws
            || stage.LastStats.DrawnObjects != expectedObjectDraws)
        {
            throw new InvalidOperationException("The shared object budget did not return unused editable capacity to the underlay.");
        }

        var stressScene = new VectorScene();
        stressScene.Generate(1000, 100000, 100000000);
        stage.BindScene(stressScene);
        stage.Fit();
        stage.Update();
        Application.DoEvents();
        if (!stage.LastFrameUsedDirect2D
            || !stage.LastStats.TileLod
            || stage.LastStats.TileDraws <= 0
            || stage.LastStats.DrawnObjects != 0
            || stage.LastDirect2DLodBitmapSubmissions != 1)
        {
            throw new InvalidOperationException("Fit Stage did not submit the stress scene as one cached LOD bitmap.");
        }

        var fillEdgeDragSummaryRevision = stressScene.SummaryRevision;
        var denseDetailObject = Enumerable.Range(0, stressScene.ObjectCount)
            .FirstOrDefault(index => stressScene.IsObjectActive(index, stage.Frame), -1);
        if (denseDetailObject < 0)
        {
            throw new InvalidOperationException("The dense fill-edge benchmark has no active detail object.");
        }
        stage.SetSelection([denseDetailObject], denseDetailObject);
        stage.SetFillEdgeBezierPointerEditing(true);
        stage.Update();
        Application.DoEvents();
        if (!stage.LastFrameUsedDirect2D
            || !stage.LastStats.TileLod
            || stage.LastDirect2DLodBitmapSubmissions != 1
            || stage.LastDirect2DLodDetailObjectDraws != 1
            || stressScene.SummaryRevision != fillEdgeDragSummaryRevision)
        {
            throw new InvalidOperationException(
                $"Dense fill-edge dragging did not retain cached LOD with one live detail object: " +
                $"tileLod={stage.LastStats.TileLod}, bitmaps={stage.LastDirect2DLodBitmapSubmissions}, " +
                $"details={stage.LastDirect2DLodDetailObjectDraws}, summary={stressScene.SummaryRevision}/{fillEdgeDragSummaryRevision}.");
        }
        const int denseFillEdgeSamples = 12;
        var denseFillEdgeCommandMilliseconds = 0d;
        for (var sample = 0; sample < denseFillEdgeSamples; sample++)
        {
            stage.Invalidate();
            stage.Update();
            Application.DoEvents();
            denseFillEdgeCommandMilliseconds += stage.LastDirect2DCommandMilliseconds;
        }
        var denseFillEdgeAverageCommandMilliseconds = denseFillEdgeCommandMilliseconds / denseFillEdgeSamples;
        if (denseFillEdgeAverageCommandMilliseconds > RenderCollectBudgetMilliseconds)
        {
            throw new InvalidOperationException(
                $"Dense fill-edge dragging exceeded its render budget: averageMs={denseFillEdgeAverageCommandMilliseconds:0.000}.");
        }
        stage.SetFillEdgeBezierPointerEditing(false);
        stage.SetSelection([], -1);
        stage.Update();
        Application.DoEvents();
        if (!stage.LastFrameUsedDirect2D || !stage.LastStats.TileLod)
        {
            throw new InvalidOperationException("The Stage did not restore normal LOD rendering after fill-edge dragging ended.");
        }

        const int renderSamples = 24;
        for (var warmup = 0; warmup < 4; warmup++)
        {
            stage.Invalidate();
            stage.Update();
        }
        var completedFrames = 0;
        EventHandler rendered = (_, _) => completedFrames++;
        stage.FrameRendered += rendered;
        var renderWatch = Stopwatch.StartNew();
        double commandMilliseconds = 0;
        double presentMilliseconds = 0;
        double frameMilliseconds = 0;
        double cacheMaintenanceMilliseconds = 0;
        var lodBitmapBuilds = 0;
        for (var sample = 0; sample < renderSamples; sample++)
        {
            stage.Invalidate();
            stage.Update();
            commandMilliseconds += stage.LastDirect2DCommandMilliseconds;
            presentMilliseconds += stage.LastDirect2DPresentMilliseconds;
            frameMilliseconds += stage.LastFrameRenderMilliseconds;
            cacheMaintenanceMilliseconds += stage.LastDirect2DCacheMaintenanceMilliseconds;
            lodBitmapBuilds += stage.LastDirect2DLodBitmapBuilds;
        }
        renderWatch.Stop();
        stage.FrameRendered -= rendered;
        if (completedFrames != renderSamples
            || !stage.LastFrameUsedDirect2D
            || !stage.LastStats.TileLod
            || stage.LastDirect2DLodBitmapSubmissions != 1
            || lodBitmapBuilds != 0)
        {
            throw new InvalidOperationException("The repeated stress-scene render sample did not complete through the Direct2D LOD path.");
        }
        var fitStageRenderFps = renderSamples / renderWatch.Elapsed.TotalSeconds;
        var averageCommandMilliseconds = commandMilliseconds / renderSamples;
        var averageFrameMilliseconds = frameMilliseconds / renderSamples;
        var averageCacheMaintenanceMilliseconds = cacheMaintenanceMilliseconds / renderSamples;
        var commandBudgetMet = averageCommandMilliseconds <= RenderCollectBudgetMilliseconds;
        if (averageFrameMilliseconds > 1000d / 60d)
        {
            throw new InvalidOperationException(
                $"The stable stress-scene frame exceeded its 60 Hz budget: averageMs={averageFrameMilliseconds:0.000}.");
        }

        form.Close();
        Console.WriteLine("underlay_direct2d=ok");
        Console.WriteLine("onion_skin_below_drawing_objects=ok");
        Console.WriteLine("onion_skin_below_drawing_objects_gdi=ok");
        Console.WriteLine("layer_outline_gdi_direct2d=ok");
        Console.WriteLine("gpu_hardware_target=ok");
        Console.WriteLine("gpu_immediate_present=ok");
        Console.WriteLine("gpu_base_frame_cache=ok");
        Console.WriteLine("pointer_feedback_sync_present=ok");
        Console.WriteLine($"pointer_feedback_handler_ms={gpuPointerFeedbackHandlerMilliseconds:0.000}");
        Console.WriteLine($"pointer_feedback_total_ms={gpuPointerFeedbackTotalMilliseconds:0.000}");
        Console.WriteLine($"pointer_feedback_coalesced_requests={gpuPointerFeedbackCoalescedRequests}");
        Console.WriteLine($"drag_first_present_ms={gpuDragFirstPresentMilliseconds:0.000}");
        Console.WriteLine($"drag_gpu_first_move_total_ms={gpuDragFirstMoveTotalMilliseconds:0.000}");
        Console.WriteLine($"drag_gpu_first_move_budget_met={gpuDragBudgetMet.ToString().ToLowerInvariant()}");
        Console.WriteLine("pointer_feedback_gdi_deferred=ok");
        Console.WriteLine($"polar_grid_zoomed_direct2d_command_ms={polarDirect2DCommandMilliseconds:0.000}");
        Console.WriteLine($"polar_grid_zoomed_direct2d_frame_ms={polarDirect2DFrameMilliseconds:0.000}");
        Console.WriteLine($"polar_grid_zoomed_gdi_frame_ms={polarGdiFrameMilliseconds:0.000}");
        Console.WriteLine("polar_grid_zoomed_budget_met=true");
        Console.WriteLine($"visible_objects={layeredVisibleObjects}");
        Console.WriteLine($"mixed_lod_tiles={mixedLodTileDraws}");
        Console.WriteLine("freehand_cache_switch=ok");
        Console.WriteLine($"shared_object_draws={expectedObjectDraws}");
        Console.WriteLine($"fit_stage_lod_tiles={stage.LastStats.TileDraws}");
        Console.WriteLine($"fit_stage_lod_bitmap_submissions={stage.LastDirect2DLodBitmapSubmissions}");
        Console.WriteLine($"fit_stage_lod_bitmap_rebuilds={lodBitmapBuilds}");
        Console.WriteLine($"fit_stage_offscreen_present_fps={fitStageRenderFps:0.0}");
        Console.WriteLine($"fit_stage_command_avg_ms={averageCommandMilliseconds:0.000}");
        Console.WriteLine($"fit_stage_command_budget_ms={RenderCollectBudgetMilliseconds:0.000}");
        Console.WriteLine($"fit_stage_command_budget_met={commandBudgetMet.ToString().ToLowerInvariant()}");
        Console.WriteLine($"fit_stage_command_capacity_fps={1000.0 / averageCommandMilliseconds:0.0}");
        Console.WriteLine($"fit_stage_present_avg_ms={presentMilliseconds / renderSamples:0.000}");
        Console.WriteLine($"fit_stage_frame_avg_ms={averageFrameMilliseconds:0.000}");
        Console.WriteLine($"fit_stage_cache_maintenance_avg_ms={averageCacheMaintenanceMilliseconds:0.000}");
        Console.WriteLine($"path_geometry_cache_stable_maintenance_ms={stablePathCacheMaintenanceMilliseconds:0.000}");
        Console.WriteLine($"path_geometry_cache_stable_frame_ms={stablePathFrameMilliseconds:0.000}");
        Console.WriteLine($"dense_fill_edge_drag_command_avg_ms={denseFillEdgeAverageCommandMilliseconds:0.000}");
    }

    private static void RunZoomLodPreviewRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        for (var index = 0; index < 320; index++)
        {
            scene.AppendObject(
                0,
                new PointF((index % 32) * 40 - 620, (index / 32) * 40 - 180),
                new SizeF(24, 24),
                0,
                0,
                Color.CornflowerBlue,
                Color.Transparent,
                0,
                ShapeKind.Rectangle);
        }

        scene.CompleteDeferredBuild();
        using var stage = new StageControl(scene);
        stage.ZoomAt(Point.Empty, 2, interactivePreview: true);
        if (!stage.ZoomLodPreviewActive
            || !StageControl.ShouldUseZoomLodPreview(scene.ObjectCount, scene.HasDisplayLayerEffects)
            || StageControl.ShouldUseZoomLodPreview(319, hasDisplayLayerEffects: false)
            || StageControl.ShouldUseZoomLodPreview(320, hasDisplayLayerEffects: true))
        {
            throw new InvalidOperationException("Dense-scene zoom did not enter the bounded LOD preview policy.");
        }

        stage.ZoomAt(Point.Empty, 1.1f);
        if (stage.ZoomLodPreviewActive)
        {
            throw new InvalidOperationException("Non-interactive zoom did not leave the temporary LOD preview state.");
        }

        Console.WriteLine("zoom_lod_preview_regression=ok");
    }

    private static void RunSelectionHighlightStyleRegression()
    {
        const float pulse = 0.5f;
        var fillLine = StageControl.SelectionLineColor(SelectionHighlightKind.Fill, primary: true);
        var strokeLine = StageControl.SelectionLineColor(SelectionHighlightKind.Stroke, primary: true);
        var fillGlow = StageControl.SelectionGlowColor(SelectionHighlightKind.Fill, primary: true, pulse);
        var strokeGlow = StageControl.SelectionGlowColor(SelectionHighlightKind.Stroke, primary: true, pulse);
        var fillWidth = StageControl.SelectionLineWidth(SelectionHighlightKind.Fill, primary: true, pulse);
        var strokeWidth = StageControl.SelectionLineWidth(SelectionHighlightKind.Stroke, primary: true, pulse);
        AssertTimeline(
            StageControl.SelectionHighlightForShape(ShapeKind.Rectangle) == SelectionHighlightKind.Fill
            && StageControl.SelectionHighlightForShape(ShapeKind.Path) == SelectionHighlightKind.Fill
            && StageControl.SelectionHighlightForShape(ShapeKind.BrushStroke) == SelectionHighlightKind.Fill
            && StageControl.SelectionHighlightForShape(ShapeKind.Line) == SelectionHighlightKind.Stroke
            && StageControl.SelectionHighlightForShape(ShapeKind.Freeform) == SelectionHighlightKind.Stroke
            && fillLine.ToArgb() != strokeLine.ToArgb()
            && fillGlow.B > fillGlow.R
            && strokeGlow.R > strokeGlow.B
            && strokeWidth > fillWidth
            && StageControl.SelectionOuterGlowWidth(SelectionHighlightKind.Fill, primary: true, pulse: 1)
                > StageControl.SelectionOuterGlowWidth(SelectionHighlightKind.Fill, primary: true, pulse: 0)
            && StageControl.SelectionOuterGlowWidth(SelectionHighlightKind.Stroke, primary: true, pulse: 1)
                > StageControl.SelectionOuterGlowWidth(SelectionHighlightKind.Stroke, primary: true, pulse: 0)
            && StageControl.ShouldAnimateSelectionHighlight(512, 8_192)
            && !StageControl.ShouldAnimateSelectionHighlight(513, 8_192)
            && !StageControl.ShouldAnimateSelectionHighlight(512, 8_193),
            "Fill and stroke selection highlights did not retain distinct cool/thin and warm/strong styles.");
        Console.WriteLine("selection_highlight_style_regression=ok");
    }

    private static void RunSelectionUsabilityRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty(layers: 2);
        var targetObject = scene.AddObject(
            1,
            PointF.Empty,
            new SizeF(200, 120),
            0,
            0,
            Color.CornflowerBlue,
            Color.Black,
            12,
            ShapeKind.Rectangle);
        scene.ActiveLayer = 0;

        using var timeline = new TimelineStrip(scene);
        if (!MainForm.SyncTimelineLayerToPrimarySelection(scene, timeline, targetObject)
            || scene.ActiveLayer != 1
            || timeline.SelectedLayerTargetIds.Count != 1
            || !string.Equals(timeline.SelectedLayerTargetIds[0], scene.LayerIds[1], StringComparison.Ordinal)
            || MainForm.SyncTimelineLayerToPrimarySelection(scene, timeline, -1))
        {
            throw new InvalidOperationException("Stage selection did not focus the selected object's timeline layer.");
        }
        if (MainForm.IsCanvasShortcutBlockingInteractiveControl(timeline))
        {
            throw new InvalidOperationException(
                "Timeline focus incorrectly disabled global canvas and tool shortcuts after handling timeline-specific keys.");
        }

        if (MainForm.ShouldPreferStrokeMaterial(DrawingElementKind.Fill, ShapeKind.Rectangle)
            || !MainForm.ShouldPreferStrokeMaterial(DrawingElementKind.Stroke, ShapeKind.Freeform)
            || !MainForm.ShouldPreferStrokeMaterial(DrawingElementKind.BoundaryStroke, ShapeKind.Rectangle)
            || !MainForm.ShouldPreferStrokeMaterial(DrawingElementKind.None, ShapeKind.Line)
            || MainForm.ShouldPreferStrokeMaterial(DrawingElementKind.None, ShapeKind.Rectangle)
            || MainForm.CanEditGradientForMaterialTarget(ShapeKind.Rectangle, strokeTarget: true)
            || !MainForm.CanEditGradientForMaterialTarget(ShapeKind.Rectangle, strokeTarget: false))
        {
            throw new InvalidOperationException("Selection material priority did not preserve Fill, Stroke, boundary, and whole-object routing.");
        }

        using var material = new MaterialEditorPanel();
        var materialChanged = false;
        var gradientChanged = false;
        material.MaterialChanged += (_, _) => materialChanged = true;
        material.GradientChanged += (_, _) => gradientChanged = true;
        material.SetGradientPreviewTarget(strokeTarget: true);
        if (!material.EditingStroke || materialChanged || gradientChanged)
        {
            throw new InvalidOperationException("Prioritizing Stroke changed material data or failed to update the inspector target.");
        }
        material.SetGradientPreviewTarget(strokeTarget: false);
        if (material.EditingStroke || materialChanged || gradientChanged)
        {
            throw new InvalidOperationException("Prioritizing Fill changed material data or failed to update the inspector target.");
        }

        Console.WriteLine("selection_usability_regression=ok");
    }

}
