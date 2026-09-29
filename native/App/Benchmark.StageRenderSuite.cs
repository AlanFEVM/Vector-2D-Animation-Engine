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
        batchScene.LayerOpacity[0] = 0.5f;
        var batchesWithNormalOpacity = LayerBlendCompositor.GetSpatialLayerBatches(
            batchScene,
            layer => layer != batchMiddleLayer);
        if (batchesWithNormalOpacity.Length != 1
            || !batchesWithNormalOpacity[0].SequenceEqual([batchBottomLayer, 0]))
        {
            throw new InvalidOperationException(
                "Normal opacity split an otherwise continuous spatial batch.");
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

        AssertProjectedNormalOpacity(useParentFolder: false);
        AssertProjectedNormalOpacity(useParentFolder: true);

        void AssertProjectedNormalOpacity(bool useParentFolder)
        {
            var projectedScene = new VectorScene();
            projectedScene.CreateEmpty();
            var projectedBackdropLayer = projectedScene.AddLayer("Projected opacity backdrop");
            var projectedSource = Color.FromArgb(255, 208, 74, 54);
            var projectedBackdrop = Color.FromArgb(255, 48, 132, 206);
            var projectedSourceObject = projectedScene.AddObject(
                0,
                PointF.Empty,
                new SizeF(2_800, 2_000),
                0,
                0,
                projectedSource,
                Color.Transparent,
                12,
                ShapeKind.Rectangle);
            projectedScene.AddObject(
                projectedBackdropLayer,
                PointF.Empty,
                new SizeF(3_600, 2_800),
                0,
                0,
                projectedBackdrop,
                Color.Transparent,
                12,
                ShapeKind.Rectangle);
            if (useParentFolder) projectedScene.ActiveLayer = 0;
            var opacityLayer = useParentFolder
                ? projectedScene.AddFolderLayer("Projected opacity folder")
                : projectedScene.ObjectLayer[projectedSourceObject];
            projectedScene.LayerOpacity[opacityLayer] = 0.5f;

            using var projectedStage = new StageControl(projectedScene)
            {
                ClientSize = new Size(320, 240),
                BackColor = Color.FromArgb(255, 18, 20, 24),
                WorldGridOpacity = 0
            };
            projectedStage.ConfigureReferenceView(null, SceneDimension.ThreeD);
            projectedStage.SetReferenceCameraOrientation(0, 0);
            var projectedItems = projectedStage.GetReference3DSceneRenderItems();
            if (projectedScene.HasNonNormalLayerBlendModes
                || !projectedStage.TryProjectScenePosition(
                    System.Numerics.Vector3.Zero,
                    out var projectedCenter,
                    out _)
                || !projectedItems.Any(item => item.ObjectIndex == projectedSourceObject)
                || projectedItems.Any(item => item.ObjectIndex == projectedSourceObject
                    && Math.Abs(item.MaterialOpacity - 0.5f) > 0.000001f))
            {
                throw new InvalidOperationException(
                    "Projected Normal opacity did not remain in the global material render plan.");
            }

            var projectedDrawGdi = typeof(StageControl).GetMethod(
                "DrawGdi",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException(
                    "The projected Normal-opacity GDI entry point could not be located.");
            using (var bitmap = new Bitmap(projectedStage.ClientSize.Width, projectedStage.ClientSize.Height))
            using (var graphics = Graphics.FromImage(bitmap))
            {
                projectedDrawGdi.Invoke(projectedStage, [graphics]);
                AssertColorNear(
                    bitmap.GetPixel(
                        Math.Clamp((int)MathF.Round(projectedCenter.X), 0, bitmap.Width - 1),
                        Math.Clamp((int)MathF.Round(projectedCenter.Y), 0, bitmap.Height - 1)),
                    LayerBlendCompositor.CompositeColorForRegression(
                        projectedBackdrop,
                        projectedSource,
                        LayerBlendMode.Normal,
                        opacity: 0.5f),
                    useParentFolder ? "Projected parent-folder opacity" : "Projected Normal layer opacity");
            }

            projectedScene.LayerOpacity[opacityLayer] = 0f;
            if (projectedStage.GetReference3DSceneRenderItems().Any(item =>
                    item.ObjectIndex == projectedSourceObject))
            {
                throw new InvalidOperationException(
                    "A zero-opacity projected layer retained render, occlusion, or hit-test items.");
            }
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
                SampleBitmap(projectedBitmap, projectedCenter),
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
                SampleBitmap(flatBitmap, stage.WorldToScreen(insideMask.X, insideMask.Y)),
                targetColor,
                "The 2D scene mask removed content inside its clipping area");
            AssertPixelNear(
                SampleBitmap(flatBitmap, stage.WorldToScreen(outsideMask.X, outsideMask.Y)),
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
                SampleBitmap(spatialBitmap, projectedInsideMask),
                targetColor,
                "The 3D reference scene mask removed content inside its clipping area");
            AssertPixelNear(
                SampleBitmap(spatialBitmap, projectedOutsideMask),
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
                    SampleBitmap(flatBitmap, stage.WorldToScreen(0, 0)),
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
                SampleBitmap(spatialBitmap, center),
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
        if (!stage.UsesReferenceProjection
            || !stage.RendersReferenceProjection
            || stage.EffectiveReferenceProjection != CameraProjection.Orthographic
            || Math.Abs(stage.EffectiveReferenceYaw) > 0.0001f
            || Math.Abs(stage.EffectiveReferencePitch) > 0.0001f
            || Math.Abs(stage.ReferenceYaw - 0.83f) > 0.0001f
            || Math.Abs(stage.ReferencePitch + 0.41f) > 0.0001f)
        {
            throw new InvalidOperationException(
                "An identity Scene Building composition did not retain the stable front-facing 2D orthographic view.");
        }
        if (!stage.TryProjectScenePosition(
                System.Numerics.Vector3.Zero,
                out var planarCenter,
                out _))
        {
            throw new InvalidOperationException("The identity composition center could not be projected in the 2D front view.");
        }
        using (var planarBitmap = RenderGdi(stage))
        {
            AssertPixelNear(
                SampleBitmap(planarBitmap, planarCenter),
                planarColor,
                "The identity composition pose changed the stable 2D spatial presentation");
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
                SampleBitmap(spatialBitmap, projectedCenter),
                planarColor,
                "The 2D front view did not render the spatial pose");
        }

        var gdiCacheScene = new VectorScene();
        gdiCacheScene.CreateEmpty();
        var gdiCacheObject = gdiCacheScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(3_200, 2_000),
            0,
            80,
            Color.CornflowerBlue,
            Color.White,
            12,
            ShapeKind.Rectangle);
        using (var gdiCacheStage = CreateStage(gdiCacheScene))
        using (var gdiCacheBitmap = new Bitmap(stageWidth, stageHeight))
        using (var gdiCacheGraphics = Graphics.FromImage(gdiCacheBitmap))
        {
            gdiCacheStage.ConfigureReferenceView(null, SceneDimension.ThreeD);
            gdiCacheStage.SetReferenceCameraOrientation(0.54f, -0.27f);
            var drawBufferedGdi = typeof(StageControl).GetMethod(
                "DrawBufferedGdi",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException(
                    "The reference-3D buffered GDI regression entry point could not be located.");
            drawBufferedGdi.Invoke(gdiCacheStage, [gdiCacheGraphics]);
            var gdiCachedPlanBuilds = gdiCacheStage.Reference3DRenderPlanBuildCount;
            if (gdiCacheStage.LastGdiBaseFrameCacheBuilds != 1
                || gdiCacheStage.LastGdiBaseFrameCacheReuses != 0)
            {
                throw new InvalidOperationException(
                    "The initial reference-3D GDI frame did not populate its base-frame cache.");
            }

            gdiCacheStage.SetReference3DSelection([gdiCacheObject]);
            drawBufferedGdi.Invoke(gdiCacheStage, [gdiCacheGraphics]);
            if (gdiCacheStage.LastGdiBaseFrameCacheBuilds != 0
                || gdiCacheStage.LastGdiBaseFrameCacheReuses != 1
                || gdiCacheStage.Reference3DRenderPlanBuildCount != gdiCachedPlanBuilds)
            {
                throw new InvalidOperationException(
                    "A reference-3D GDI selection overlay rebuilt the stable base frame or render plan.");
            }

            gdiCacheStage.SetSpatialTransformGizmo(
                System.Numerics.Vector3.Zero,
                SpatialTransformMode.Move,
                [gdiCacheObject]);
            drawBufferedGdi.Invoke(gdiCacheStage, [gdiCacheGraphics]);
            if (gdiCacheStage.LastGdiBaseFrameCacheBuilds != 0
                || gdiCacheStage.LastGdiBaseFrameCacheReuses != 1
                || gdiCacheStage.Reference3DRenderPlanBuildCount != gdiCachedPlanBuilds)
            {
                throw new InvalidOperationException(
                    "A reference-3D GDI Gizmo overlay rebuilt the stable base frame or render plan.");
            }

            gdiCacheScene.SetLayerVisible(0, false);
            drawBufferedGdi.Invoke(gdiCacheStage, [gdiCacheGraphics]);
            var hiddenPlanBuilds = gdiCacheStage.Reference3DRenderPlanBuildCount;
            if (gdiCacheStage.LastGdiBaseFrameCacheBuilds != 1
                || gdiCacheStage.LastGdiBaseFrameCacheReuses != 0
                || hiddenPlanBuilds <= gdiCachedPlanBuilds)
            {
                throw new InvalidOperationException(
                    "An in-place GDI layer visibility change reused a stale reference-3D base frame.");
            }

            gdiCacheScene.SetLayerVisible(0, true);
            gdiCacheScene.LayerOpacity[0] = 0.5f;
            drawBufferedGdi.Invoke(gdiCacheStage, [gdiCacheGraphics]);
            if (gdiCacheStage.LastGdiBaseFrameCacheBuilds != 1
                || gdiCacheStage.LastGdiBaseFrameCacheReuses != 0
                || gdiCacheStage.Reference3DRenderPlanBuildCount <= hiddenPlanBuilds)
            {
                throw new InvalidOperationException(
                    "An in-place GDI layer opacity change reused a stale reference-3D base frame.");
            }
            drawBufferedGdi.Invoke(gdiCacheStage, [gdiCacheGraphics]);
            if (gdiCacheStage.LastGdiBaseFrameCacheBuilds != 0
                || gdiCacheStage.LastGdiBaseFrameCacheReuses != 1)
            {
                throw new InvalidOperationException(
                    "An unchanged GDI opacity frame did not reuse its rebuilt reference base.");
            }

            gdiCacheScene.LayerBlendModes[0] = LayerBlendMode.Dissolve;
            drawBufferedGdi.Invoke(gdiCacheStage, [gdiCacheGraphics]);
            var dissolvePlanBuilds = gdiCacheStage.Reference3DRenderPlanBuildCount;
            if (gdiCacheStage.LastGdiBaseFrameCacheBuilds != 1
                || gdiCacheStage.LastGdiBaseFrameCacheReuses != 0)
            {
                throw new InvalidOperationException(
                    "Enabling GDI Dissolve did not rebuild the reference base frame.");
            }
            drawBufferedGdi.Invoke(gdiCacheStage, [gdiCacheGraphics]);
            if (gdiCacheStage.LastGdiBaseFrameCacheBuilds != 0
                || gdiCacheStage.LastGdiBaseFrameCacheReuses != 1)
            {
                throw new InvalidOperationException(
                    "An unchanged GDI Dissolve frame did not reuse its reference base.");
            }
            if (!gdiCacheScene.RenameLayer(0, "Renamed dissolve layer"))
            {
                throw new InvalidOperationException(
                    "The GDI Dissolve cache fixture could not rename its layer.");
            }
            drawBufferedGdi.Invoke(gdiCacheStage, [gdiCacheGraphics]);
            if (gdiCacheStage.LastGdiBaseFrameCacheBuilds != 1
                || gdiCacheStage.LastGdiBaseFrameCacheReuses != 0
                || gdiCacheStage.Reference3DRenderPlanBuildCount <= dissolvePlanBuilds)
            {
                throw new InvalidOperationException(
                    "Renaming a Dissolve layer reused a stale reference-3D base frame.");
            }
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
            [PointF.Empty, new PointF(800, 0), new PointF(1_900, 900)],
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
        trajectoryScene.SetGradientPaint(
            trajectoryObject,
            GradientKind.Linear,
            [
                new GradientStop(0, Color.FromArgb(255, 232, 62, 70)),
                new GradientStop(0.5f, Color.FromArgb(255, 246, 194, 54)),
                new GradientStop(1, Color.FromArgb(255, 8, 10, 14))
            ],
            new PointF(-1_400, 0),
            new PointF(1_400, 0));
        trajectoryScene.SetGradientPath(trajectoryObject, trajectoryPath);

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
        if (!lineStage.TryProjectScenePoint(
                lineObject,
                lineSample,
                out var planarLineSample,
                out _))
        {
            throw new InvalidOperationException("The planar line-gradient sample could not be projected.");
        }
        Color planarLineColor;
        using (var planarLineBitmap = RenderGdi(lineStage))
        {
            planarLineColor = SampleBitmap(planarLineBitmap, planarLineSample);
        }
        lineStage.SetSceneCompositionResult(CompositionResult(spatialPose), lineScene);
        if (!lineStage.TryProjectScenePoint(lineObject, lineSample, out var projectedLineSample, out _))
        {
            throw new InvalidOperationException("The line-gradient sample could not be projected.");
        }
        using (var spatialLineBitmap = RenderGdi(lineStage))
        {
            AssertPixelNear(
                SampleBitmap(spatialLineBitmap, projectedLineSample),
                planarLineColor,
                "The 2D front view changed a linear-gradient line stroke",
                tolerance: 10);
        }
        var linePerspectiveView = new SceneDefinition { Dimension = SceneDimension.ThreeD };
        ConfigureNeutralReferenceLighting(linePerspectiveView);
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
                SampleBitmap(perspectiveLineBitmap, perspectiveLineSample),
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
                SampleBitmap(blendBitmap, blendCenter),
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
                SampleBitmap(depthBitmap, depthCenter),
                nearColor,
                "The farther spatial object painted over the nearer object");
        }
        if (!depthStage.TryHitTestProjectedObject(Point.Round(depthCenter), 1f, out var depthHit)
            || depthHit != nearObject)
        {
            throw new InvalidOperationException("Projected hit testing did not select the nearest painted object.");
        }

        var transparentStrokeScene = new VectorScene();
        transparentStrokeScene.CreateEmpty();
        var transparentBackdrop = transparentStrokeScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(5_000, 4_000),
            0,
            0,
            Color.FromArgb(255, 42, 162, 104),
            Color.Transparent,
            12,
            ShapeKind.Rectangle);
        var transparentOutline = transparentStrokeScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(2_400, 1_800),
            0,
            180,
            Color.Transparent,
            Color.FromArgb(255, 236, 78, 92),
            12,
            ShapeKind.Rectangle);
        using (var transparentStrokeStage = CreateStage(transparentStrokeScene))
        {
            transparentStrokeStage.ConfigureReferenceView(null, SceneDimension.ThreeD);
            transparentStrokeStage.SetReferenceCameraOrientation(0.38f, -0.19f);
            transparentStrokeStage.SetSceneCompositionResult(
                CompositionResult(
                    System.Numerics.Matrix4x4.Identity,
                    System.Numerics.Matrix4x4.CreateRotationY(0.68f)),
                transparentStrokeScene);
            var transparentItems = transparentStrokeStage.GetReference3DSceneRenderItems();
            if (transparentItems.Any(item => item.ObjectIndex == transparentOutline
                    && item.Kind == Reference3DRenderKind.FrontFill)
                || !transparentItems.Any(item => item.ObjectIndex == transparentOutline
                    && item.Kind == Reference3DRenderKind.FrontStroke
                    && item.OcclusionContours is { Length: > 0 })
                || transparentItems.Any(item => item.Kind == Reference3DRenderKind.IntersectionEdge
                    && (item.ObjectIndex == transparentOutline
                        || item.SecondaryObjectIndex == transparentOutline)))
            {
                throw new InvalidOperationException(
                    "A transparent fill retained a solid surface or synthetic intersection edge.");
            }
            if (!transparentStrokeStage.TryProjectScenePoint(
                    transparentBackdrop,
                    PointF.Empty,
                    out var transparentInterior,
                    out _)
                || !transparentStrokeStage.TryProjectScenePoint(
                    transparentOutline,
                    new PointF(1_200, 0),
                    out var transparentBoundary,
                    out _))
            {
                throw new InvalidOperationException(
                    "The transparent-fill hit-test samples could not be projected.");
            }
            using var transparentBitmap = RenderGdi(transparentStrokeStage);
            AssertPixelNear(
                SampleBitmap(transparentBitmap, transparentInterior),
                Color.FromArgb(255, 42, 162, 104),
                "A transparent fill occluded the surface behind its interior",
                tolerance: 2);
            if (!transparentStrokeStage.TryHitTestProjectedObject(
                    Point.Round(transparentInterior),
                    1f,
                    out var transparentInteriorHit)
                || transparentInteriorHit != transparentBackdrop
                || !transparentStrokeStage.TryHitTestProjectedObject(
                    Point.Round(transparentBoundary),
                    1f,
                    out var transparentBoundaryHit)
                || transparentBoundaryHit != transparentOutline)
            {
                throw new InvalidOperationException(
                    "Transparent-fill hit testing did not pass through the interior and retain the visible stroke.");
            }
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
                SampleBitmap(crossLayerDepthBitmap, crossLayerDepthCenter),
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
                SampleBitmap(reversedCrossLayerDepthBitmap, crossLayerDepthCenter),
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
                SampleBitmap(coplanarCrossLayerBitmap, crossLayerDepthCenter),
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

        RunSceneBuildingZTweenDepthRegression();

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
        var sharedStableFragments = stableOrderNearFills.Select(item => item.StableFragmentIdentity)
            .Intersect(stableOrderFarFills.Select(item => item.StableFragmentIdentity))
            .ToArray();
        if (sharedStableFragments.Length == 0
            || sharedStableFragments.Any(fragmentIdentity =>
                Array.FindIndex(stableOrderItems, item =>
                    item.ObjectIndex == stableOrderNearObject
                    && item.Kind == Reference3DRenderKind.FrontFill
                    && item.StableFragmentIdentity == fragmentIdentity)
                >= Array.FindIndex(stableOrderItems, item =>
                    item.ObjectIndex == stableOrderFarObject
                    && item.Kind == Reference3DRenderKind.FrontFill
                    && item.StableFragmentIdentity == fragmentIdentity)))
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
                .GroupBy(entry => (
                    entry.Item.StableFragmentIdentity,
                    entry.Item.FragmentSlot))
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
                item.StableFragmentIdentity,
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
                    item.StableFragmentIdentity,
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
                SampleBitmap(strokeOverlapBitmap, strokeOverlapSample),
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

        var curveProjectionScene = new VectorScene();
        curveProjectionScene.CreateEmpty();
        var curveStart = new PointF(-1_600, 0);
        var curveControl1 = new PointF(-800, -1_800);
        var curveControl2 = new PointF(800, -1_800);
        var curveEnd = new PointF(1_600, 0);
        var curveProjectionObject = curveProjectionScene.AddCubicCurveSegment(
            0,
            curveStart,
            curveControl1,
            curveControl2,
            curveEnd,
            120,
            Color.Transparent,
            Color.White,
            12);
        var thinProjectionObject = curveProjectionScene.AddLineSegment(
            0,
            new PointF(-1_000, 2_400),
            new PointF(1_000, 2_400),
            1,
            Color.Transparent,
            Color.White,
            12);
        using var curveProjectionStage = CreateStage(curveProjectionScene);
        var curveProjectionView = new SceneDefinition { Dimension = SceneDimension.ThreeD };
        ConfigureNeutralReferenceLighting(curveProjectionView);
        curveProjectionView.Camera.Projection = CameraProjection.Perspective;
        curveProjectionStage.ConfigureReferenceView(curveProjectionView, SceneDimension.ThreeD);
        curveProjectionStage.ResetReferenceCameraView();
        curveProjectionStage.SetReferenceCameraOrientation(0, 0);
        curveProjectionStage.SetSceneCompositionResult(
            CompositionResult(
                System.Numerics.Matrix4x4.Identity,
                System.Numerics.Matrix4x4.Identity),
            curveProjectionScene);

        var curveCenterlines = curveProjectionStage.GetReference3DProjectedContours(
            curveProjectionObject);
        if (curveCenterlines.Length != 1
            || curveCenterlines[0].Closed
            || curveCenterlines[0].Points.Length < 16
            || curveCenterlines[0].Points.Any(point =>
                !float.IsFinite(point.X) || !float.IsFinite(point.Y)))
        {
            throw new InvalidOperationException(
                "A fully visible cubic stroke was split into per-sample projected contours: "
                + string.Join(", ", curveCenterlines.Select(contour =>
                    $"closed={contour.Closed}/points={contour.Points.Length}")));
        }

        var curveProjectionItems = curveProjectionStage.GetReference3DSceneRenderItems();
        var curveProjectionItem = curveProjectionItems.Single(item =>
            item.ObjectIndex == curveProjectionObject
            && item.Kind == Reference3DRenderKind.FrontStroke);
        if (curveProjectionItem.Contours.Length != 1
            || curveProjectionItem.OcclusionContours is not { Length: > 0 }
            || curveProjectionItem.OcclusionContours.Any(contour =>
                !contour.Closed || contour.Points.Length < 3))
        {
            throw new InvalidOperationException(
                "A continuous cubic stroke did not retain one centerline and closed screen occupancy.");
        }

        var curveMidpoint = new PointF(0, -1_350);
        if (!curveProjectionStage.TryProjectScenePoint(
                curveProjectionObject,
                curveMidpoint,
                out var projectedCurveMidpoint,
                out _)
            || !curveProjectionStage.TryProjectScenePoint(
                curveProjectionObject,
                PointF.Empty,
                out var projectedCurveChordMidpoint,
                out _)
            || !curveProjectionStage.TryProjectScenePoint(
                curveProjectionObject,
                curveStart,
                out var projectedCurveStart,
                out _)
            || !ProjectedFillContainsMargin(
                curveProjectionItem.OcclusionContours,
                Point.Round(projectedCurveMidpoint),
                0)
            || !ProjectedFillContainsMargin(
                curveProjectionItem.OcclusionContours,
                Point.Round(projectedCurveStart),
                0)
            || ProjectedFillContainsMargin(
                curveProjectionItem.OcclusionContours,
                Point.Round(projectedCurveChordMidpoint),
                0))
        {
            throw new InvalidOperationException(
                "The cubic screen occupancy lost its round cap or incorrectly closed across the curve chord.");
        }

        var thinProjectionItem = curveProjectionItems.Single(item =>
            item.ObjectIndex == thinProjectionObject
            && item.Kind == Reference3DRenderKind.FrontStroke);
        var thinProjectedWidth = curveProjectionStage.GetReference3DStrokeWidth(
            thinProjectionObject,
            curveProjectionScene.Stroke[thinProjectionObject]);
        var thinOccupancyPoints = thinProjectionItem.OcclusionContours?
            .SelectMany(contour => contour.Points)
            .ToArray() ?? [];
        var thinOccupancyHeight = thinOccupancyPoints.Length == 0
            ? 0
            : thinOccupancyPoints.Max(point => point.Y)
                - thinOccupancyPoints.Min(point => point.Y);
        if (Math.Abs(thinProjectedWidth - 0.75f) > 0.0001f
            || Math.Abs(thinOccupancyHeight - thinProjectedWidth) > 0.02f)
        {
            throw new InvalidOperationException(
                "The thin projected stroke used different renderer and occupancy widths: "
                + $"render={thinProjectedWidth:0.###}, occupancy={thinOccupancyHeight:0.###}.");
        }

        var clippedCurveScene = new VectorScene();
        clippedCurveScene.CreateEmpty();
        var clippedCurveObject = clippedCurveScene.AddCubicCurveSegment(
            0,
            new PointF(0, -1_200),
            new PointF(3_000, -600),
            new PointF(3_000, 600),
            new PointF(0, 1_200),
            120,
            Color.Transparent,
            Color.White,
            12);
        using var clippedCurveStage = CreateStage(clippedCurveScene);
        clippedCurveStage.ConfigureReferenceView(curveProjectionView, SceneDimension.ThreeD);
        clippedCurveStage.ResetReferenceCameraView();
        clippedCurveStage.SetReferenceCameraOrientation(0, 0);
        clippedCurveStage.SetSceneCompositionResult(
            CompositionResult(
                System.Numerics.Matrix4x4.CreateRotationY(MathF.PI * 0.5f)
                * System.Numerics.Matrix4x4.CreateTranslation(0, 0, -11_000)),
            clippedCurveScene);
        var clippedCurveRuns = clippedCurveStage.GetReference3DProjectedContours(clippedCurveObject);
        if (clippedCurveRuns.Length != 2
            || clippedCurveRuns.Any(contour =>
                contour.Closed
                || contour.Points.Length < 2
                || contour.AverageDepth < StageControl.ReferenceNearPlane
                || contour.Points.Any(point =>
                    !float.IsFinite(point.X) || !float.IsFinite(point.Y)))
            || !clippedCurveRuns[0].HasSourceStart
            || clippedCurveRuns[0].HasSourceEnd
            || clippedCurveRuns[1].HasSourceStart
            || !clippedCurveRuns[1].HasSourceEnd)
        {
            throw new InvalidOperationException(
                "A cubic crossing behind the reference near plane did not split into two finite visible runs: "
                + string.Join(", ", clippedCurveRuns.Select(contour =>
                    $"closed={contour.Closed}/points={contour.Points.Length}/depth={contour.AverageDepth:0.###}")));
        }
        var clippedRunGap = CrossingScreenDistance(
            Point.Round(clippedCurveRuns[0].Points[^1]),
            Point.Round(clippedCurveRuns[1].Points[0]));
        var clippedCurveItem = clippedCurveStage.GetReference3DSceneRenderItems().Single(item =>
            item.ObjectIndex == clippedCurveObject
            && item.Kind == Reference3DRenderKind.FrontStroke);
        if (clippedRunGap <= 1
            || clippedCurveItem.Contours.Length != 2
            || clippedCurveItem.OcclusionContours is not { Length: 2 })
        {
            throw new InvalidOperationException(
                "Near-plane clipping reconnected the cubic's hidden interval: "
                + $"gap={clippedRunGap:0.###}, centerlines={clippedCurveItem.Contours.Length}, "
                + $"occupancy={clippedCurveItem.OcclusionContours?.Length ?? 0}.");
        }
        var clippedProjectedWidth = clippedCurveStage.GetReference3DStrokeWidth(
            clippedCurveObject,
            clippedCurveScene.Stroke[clippedCurveObject]);
        var firstArtificialCapSample = ProjectedEndpointExteriorSample(
            clippedCurveRuns[0].Points[^1],
            clippedCurveRuns[0].Points[^2],
            clippedProjectedWidth * 0.35f);
        var secondArtificialCapSample = ProjectedEndpointExteriorSample(
            clippedCurveRuns[1].Points[0],
            clippedCurveRuns[1].Points[1],
            clippedProjectedWidth * 0.35f);
        if (ProjectedFillContainsMargin(
                clippedCurveItem.OcclusionContours!,
                Point.Round(firstArtificialCapSample),
                0)
            || ProjectedFillContainsMargin(
                clippedCurveItem.OcclusionContours!,
                Point.Round(secondArtificialCapSample),
                0))
        {
            throw new InvalidOperationException(
                "Near-plane clipping added a round cap to an artificial projected Line endpoint.");
        }
        Console.WriteLine(
            "scene_reference_curve_projection="
            + $"points={curveCenterlines[0].Points.Length},runs={clippedCurveRuns.Length},"
            + $"thin_width={thinProjectedWidth:0.###}");

        var lineJoinScene = new VectorScene();
        lineJoinScene.CreateEmpty();
        var lineJoinColor = Color.FromArgb(255, 238, 242, 241);
        var lineJoinPoint = new PointF(0, -800);
        const float lineJoinStroke = 260f;
        var lineJoinFirst = lineJoinScene.AddLineSegment(
            0,
            new PointF(-900, 1_800),
            lineJoinPoint,
            lineJoinStroke,
            Color.Transparent,
            lineJoinColor,
            12,
            LineEndpointStyle.Round,
            LineEndpointStyle.Sharp);
        var lineJoinSecond = lineJoinScene.AddLineSegment(
            0,
            lineJoinPoint,
            new PointF(900, 1_800),
            lineJoinStroke,
            Color.Transparent,
            lineJoinColor,
            12,
            LineEndpointStyle.Sharp,
            LineEndpointStyle.Round);
        using var lineJoinStage = CreateStage(lineJoinScene);
        lineJoinStage.ConfigureReferenceView(curveProjectionView, SceneDimension.ThreeD);
        lineJoinStage.ResetReferenceCameraView();
        lineJoinStage.SetReferenceCameraOrientation(0.32f, -0.18f);
        lineJoinStage.SetSceneCompositionResult(
            CompositionResult(
                System.Numerics.Matrix4x4.Identity,
                System.Numerics.Matrix4x4.Identity),
            lineJoinScene);
        var sharpJoinItems = lineJoinStage.GetReference3DSceneRenderItems();
        var sharpJoinStrokes = sharpJoinItems
            .Where(item => item.Kind == Reference3DRenderKind.FrontStroke
                && item.ObjectIndex is var objectIndex
                && (objectIndex == lineJoinFirst || objectIndex == lineJoinSecond))
            .ToArray();
        if (sharpJoinStrokes.Length != 2
            || sharpJoinStrokes[0].SharedShellOwnerObjectIndex < 0
            || sharpJoinStrokes[0].SharedShellOwnerObjectIndex
                != sharpJoinStrokes[1].SharedShellOwnerObjectIndex
            || sharpJoinStrokes.Any(item => item.FragmentClip is { Length: > 0 }))
        {
            throw new InvalidOperationException(
                "A connected Sharp Line was partitioned against its own shared extrusion shell.");
        }
        var sharpJoinOwner = sharpJoinItems.Single(item =>
            item.ObjectIndex == lineJoinFirst
            && item.Kind == Reference3DRenderKind.FrontStroke);
        var projectedJoinWidth = lineJoinStage.GetReference3DStrokeWidth(
            lineJoinFirst,
            lineJoinStroke);
        if (!lineJoinStage.TryGetReference3DLineEndpointJoin(
                lineJoinFirst,
                startEndpoint: false,
                sharpJoinOwner.Contours,
                projectedJoinWidth,
                out var projectedJoinPoint,
                out var projectedJoinMiters,
                out _)
            || projectedJoinMiters.Length == 0)
        {
            throw new InvalidOperationException(
                "Two projected Sharp Line endpoints did not produce a shared miter join.");
        }
        var projectedJoinTip = projectedJoinMiters
            .SelectMany(miter => new[] { miter.OuterMiter, miter.InnerMiter })
            .OrderByDescending(point => CrossingScreenDistance(
                Point.Round(projectedJoinPoint),
                Point.Round(point)))
            .First();
        var sharpJoinSample = new PointF(
            projectedJoinPoint.X + (projectedJoinTip.X - projectedJoinPoint.X) * 0.72f,
            projectedJoinPoint.Y + (projectedJoinTip.Y - projectedJoinPoint.Y) * 0.72f);
        if (sharpJoinOwner.OcclusionContours is not { Length: > 0 }
            || !ProjectedFillContainsMargin(
                sharpJoinOwner.OcclusionContours,
                Point.Round(sharpJoinSample),
                0))
        {
            throw new InvalidOperationException(
                "The projected Sharp Line miter was missing from stroke occupancy.");
        }
        using (var sharpJoinBitmap = RenderGdi(lineJoinStage))
        {
            AssertPixelNear(
                SampleBitmap(sharpJoinBitmap, sharpJoinSample),
                lineJoinColor,
                "The GDI 3D renderer omitted a shared Sharp Line miter",
                tolerance: 10);
        }

        var lineJoinGradientStart = Color.FromArgb(255, 228, 58, 72);
        var lineJoinGradientEnd = Color.FromArgb(255, 42, 132, 232);
        var lineJoinGradientStops = new[]
        {
            new GradientStop(0, lineJoinGradientStart),
            new GradientStop(1, lineJoinGradientEnd)
        };
        lineJoinScene.SetGradientPaint(
            lineJoinFirst,
            GradientKind.Linear,
            lineJoinGradientStops,
            new PointF(-900, 1_800),
            lineJoinPoint);
        // Simulate legacy/stale path metadata on a Line; normal Line editing no longer creates it.
        lineJoinScene.ShapeKind[lineJoinFirst] = ShapeKind.Path;
        lineJoinScene.SetOrderedGradientPath(
            lineJoinFirst,
            [lineJoinPoint, new PointF(-900, 1_800)]);
        lineJoinScene.ShapeKind[lineJoinFirst] = ShapeKind.Line;
        lineJoinScene.SetGradientPaint(
            lineJoinSecond,
            GradientKind.Linear,
            lineJoinGradientStops,
            new PointF(900, 1_800),
            lineJoinPoint);
        lineJoinScene.ShapeKind[lineJoinSecond] = ShapeKind.Path;
        lineJoinScene.SetOrderedGradientPath(
            lineJoinSecond,
            [lineJoinPoint, new PointF(900, 1_800)]);
        lineJoinScene.ShapeKind[lineJoinSecond] = ShapeKind.Line;
        lineJoinStage.Invalidate();
        var axisEndpointColor = StageControl.SampleReference3DLineEndpointGradient(
            lineJoinScene,
            lineJoinFirst,
            startEndpoint: false,
            GradientKind.Linear);
        var conflictingPathColor = MixingBrushPaintSampler.SampleObjectFill(
            lineJoinScene,
            lineJoinFirst,
            lineJoinPoint);
        if (axisEndpointColor.ToArgb() != lineJoinGradientEnd.ToArgb()
            || conflictingPathColor.ToArgb() != lineJoinGradientStart.ToArgb())
        {
            throw new InvalidOperationException(
                "The projected Line miter gradient fixture did not separate its axis and path colors: "
                + $"axis={axisEndpointColor.ToArgb():X8}, path={conflictingPathColor.ToArgb():X8}.");
        }
        using (var gradientJoinBitmap = RenderGdi(lineJoinStage))
        {
            AssertPixelNear(
                SampleBitmap(gradientJoinBitmap, sharpJoinSample),
                lineJoinGradientEnd,
                "The GDI Sharp Line miter followed GradientPath instead of the rendered gradient axis",
                tolerance: 12);
        }

        var styleGeometryRevision = lineJoinScene.GeometryRevision;
        var sharpPlanBuilds = lineJoinStage.Reference3DRenderPlanBuildCount;
        if (!lineJoinScene.SetLineEndpointStyle(
                lineJoinFirst,
                startEndpoint: false,
                LineEndpointStyle.Round)
            || !lineJoinScene.SetLineEndpointStyle(
                lineJoinSecond,
                startEndpoint: true,
                LineEndpointStyle.Round)
            || lineJoinScene.GeometryRevision <= styleGeometryRevision)
        {
            throw new InvalidOperationException(
                "Changing a Line endpoint style did not invalidate projected stroke geometry.");
        }
        var roundJoinItems = lineJoinStage.GetReference3DSceneRenderItems();
        var roundJoinOwner = roundJoinItems.Single(item =>
            item.ObjectIndex == lineJoinFirst
            && item.Kind == Reference3DRenderKind.FrontStroke);
        if (lineJoinStage.Reference3DRenderPlanBuildCount <= sharpPlanBuilds
            || roundJoinOwner.OcclusionContours is not { Length: > 0 }
            || ProjectedFillContainsMargin(
                roundJoinOwner.OcclusionContours,
                Point.Round(sharpJoinSample),
                0))
        {
            throw new InvalidOperationException(
                "Switching a projected Line junction to Round retained stale Sharp occupancy.");
        }
        using (var roundJoinBitmap = RenderGdi(lineJoinStage))
        {
            AssertPixelNear(
                SampleBitmap(roundJoinBitmap, sharpJoinSample),
                background,
                "The GDI 3D renderer retained a Sharp miter after switching to Round",
                tolerance: 10);
        }
        Console.WriteLine("scene_reference_line_endpoint_joins=sharp_round_ok");

        var poseJoinScene = new VectorScene();
        poseJoinScene.CreateEmpty();
        var poseJoinPoint = PointF.Empty;
        const float poseJoinStroke = 220f;
        var poseJoinFirst = poseJoinScene.AddLineSegment(
            0,
            new PointF(-1_000, 1_200),
            poseJoinPoint,
            poseJoinStroke,
            Color.Transparent,
            lineJoinColor,
            12,
            LineEndpointStyle.Round,
            LineEndpointStyle.Sharp);
        var poseJoinSecond = poseJoinScene.AddLineSegment(
            0,
            poseJoinPoint,
            new PointF(1_000, 1_200),
            poseJoinStroke,
            Color.Transparent,
            lineJoinColor,
            12,
            LineEndpointStyle.Sharp,
            LineEndpointStyle.Round);
        using var poseJoinStage = CreateStage(poseJoinScene);
        var poseJoinView = new SceneDefinition { Dimension = SceneDimension.ThreeD };
        ConfigureNeutralReferenceLighting(poseJoinView);
        poseJoinView.Camera.Projection = CameraProjection.Orthographic;
        poseJoinStage.ConfigureReferenceView(poseJoinView, SceneDimension.ThreeD);
        poseJoinStage.ResetReferenceCameraView();
        poseJoinStage.SetReferenceCameraOrientation(0, 0);
        poseJoinStage.SetSceneCompositionResult(
            CompositionResult(
                System.Numerics.Matrix4x4.Identity,
                System.Numerics.Matrix4x4.CreateTranslation(0, 0, 900)),
            poseJoinScene);
        if (!poseJoinStage.TryProjectScenePoint(
                poseJoinFirst,
                poseJoinPoint,
                out var poseFirstJoint,
                out var poseFirstDepth)
            || !poseJoinStage.TryProjectScenePoint(
                poseJoinSecond,
                poseJoinPoint,
                out var poseSecondJoint,
                out var poseSecondDepth))
        {
            throw new InvalidOperationException(
                "The different-pose Line junction fixture could not project both endpoints.");
        }
        var poseJointDx = poseFirstJoint.X - poseSecondJoint.X;
        var poseJointDy = poseFirstJoint.Y - poseSecondJoint.Y;
        var poseJointScreenDistance = MathF.Sqrt(
            poseJointDx * poseJointDx + poseJointDy * poseJointDy);
        var poseJoinItems = poseJoinStage.GetReference3DLayerRenderItems(
            [poseJoinFirst, poseJoinSecond]);
        var poseFirstStrokeItem = poseJoinItems.Single(item =>
            item.ObjectIndex == poseJoinFirst
            && item.Kind == Reference3DRenderKind.FrontStroke);
        var poseProjectedWidth = poseJoinStage.GetReference3DStrokeWidth(
            poseJoinFirst,
            poseJoinStroke);
        if (poseJointScreenDistance > 0.01f
            || Math.Abs(poseFirstDepth - poseSecondDepth) <= 100f
            || poseJoinStage.TryGetReference3DLineEndpointJoin(
                poseJoinFirst,
                startEndpoint: false,
                poseFirstStrokeItem.Contours,
                poseProjectedWidth,
                out _,
                out _,
                out _)
            || poseJoinItems.Any(item => item.SharedShellObjectIndices is not null))
        {
            throw new InvalidOperationException(
                "Screen-overlapping Sharp Line endpoints with different 3D poses were joined: "
                + $"screenDistance={poseJointScreenDistance:0.###}, "
                + $"depths={poseFirstDepth:0.###}/{poseSecondDepth:0.###}.");
        }
        Console.WriteLine("scene_reference_line_endpoint_pose_isolation=ok");

        var lineComponentScene = new VectorScene();
        lineComponentScene.CreateEmpty();
        var componentFirstJoint = new PointF(-600, -500);
        var componentSecondJoint = new PointF(600, -500);
        var componentFirstOuter = new PointF(-1_800, 900);
        var componentThirdOuter = new PointF(1_800, 900);
        const float componentStroke = 240f;
        var componentFirst = lineComponentScene.AddLineSegment(
            0,
            componentFirstOuter,
            componentFirstJoint,
            componentStroke,
            Color.Transparent,
            lineJoinColor,
            12,
            LineEndpointStyle.Round,
            LineEndpointStyle.Sharp);
        var componentSecond = lineComponentScene.AddLineSegment(
            0,
            componentFirstJoint,
            componentSecondJoint,
            componentStroke,
            Color.Transparent,
            lineJoinColor,
            12,
            LineEndpointStyle.Sharp,
            LineEndpointStyle.Sharp);
        var componentThird = lineComponentScene.AddLineSegment(
            0,
            componentSecondJoint,
            componentThirdOuter,
            componentStroke,
            Color.Transparent,
            lineJoinColor,
            12,
            LineEndpointStyle.Sharp,
            LineEndpointStyle.Round);
        var componentMembers = new[]
        {
            componentFirst,
            componentSecond,
            componentThird
        };
        var componentExtrusion = new System.Numerics.Vector3(0, 0, 1_400);
        var componentPose = new SceneCompositionObjectPose(
            System.Numerics.Matrix4x4.Identity,
            componentExtrusion);
        using var lineComponentStage = CreateStage(lineComponentScene);
        var lineComponentView = new SceneDefinition { Dimension = SceneDimension.ThreeD };
        ConfigureNeutralReferenceLighting(lineComponentView);
        lineComponentView.Camera.Projection = CameraProjection.Perspective;
        lineComponentStage.ConfigureReferenceView(lineComponentView, SceneDimension.ThreeD);
        lineComponentStage.ResetReferenceCameraView();
        lineComponentStage.SetReferenceCameraOrientation(0.58f, -0.31f);
        lineComponentStage.SetSceneCompositionResult(
            new SceneCompositionResult(
                new SceneCompositionObjectOwner[componentMembers.Length],
                [componentPose, componentPose, componentPose]),
            lineComponentScene);
        var componentItems = lineComponentStage.GetReference3DLayerRenderItems(componentMembers);
        var componentBackItems = componentItems
            .Where(item => item.Kind == Reference3DRenderKind.Back)
            .ToArray();
        var componentSideItems = componentItems
            .Where(item => item.Kind == Reference3DRenderKind.Side)
            .ToArray();
        if (componentBackItems.Length != 1
            || componentBackItems[0].ObjectIndex != componentFirst
            || componentBackItems[0].Contours.Length != 1
            || componentSideItems.Length == 0
            || componentItems.Count(item => item.Kind == Reference3DRenderKind.FrontStroke) != 3
            || componentBackItems.Concat(componentSideItems).Any(item =>
                item.ObjectIndex != componentFirst
                || item.SharedShellObjectIndices is not { } members
                || !members.SequenceEqual(componentMembers)
                || !item.Plane.IsValid
                || !float.IsFinite(item.AverageDepth)
                || item.Contours.SelectMany(contour => contour.Points).Any(point =>
                    !float.IsFinite(point.X) || !float.IsFinite(point.Y)))
            || componentMembers.Any(objectIndex =>
            {
                var memberSolid = lineComponentStage.GetReference3DProjectedSolid(objectIndex);
                return !memberSolid.HasExtrusion
                    || memberSolid.BackContours.Length == 0
                    || memberSolid.SideSurfaces.Length == 0
                    || memberSolid.SelectionEdges.Length == 0;
            }))
        {
            throw new InvalidOperationException(
                "Connected Sharp Lines did not produce one finite shared extrusion shell with per-Line fronts: "
                + $"back={componentBackItems.Length}/contours="
                + string.Join(",", componentBackItems.Select(item => item.Contours.Length))
                + $", sides={componentSideItems.Length}, fronts="
                + componentItems.Count(item => item.Kind == Reference3DRenderKind.FrontStroke)
                + $", shellCount={componentBackItems.Length + componentSideItems.Length}, shells=["
                + string.Join(",", componentBackItems.Concat(componentSideItems).Take(8).Select(item =>
                    $"{item.Kind}:{item.ObjectIndex}:"
                    + $"{string.Join('/', item.SharedShellObjectIndices ?? [])}:"
                    + $"plane={item.Plane.IsValid}:depth={item.AverageDepth:0.###}"))
                + "], solids=["
                + string.Join(",", componentMembers.Select(objectIndex =>
                {
                    var memberSolid = lineComponentStage.GetReference3DProjectedSolid(objectIndex);
                    return $"{objectIndex}:{memberSolid.HasExtrusion}:"
                        + $"{memberSolid.BackContours.Length}/{memberSolid.SideSurfaces.Length}/"
                        + memberSolid.SelectionEdges.Length;
                }))
                + "].");
        }

        var componentFirstMidpoint = Midpoint(componentFirstOuter, componentFirstJoint);
        var componentSecondMidpoint = Midpoint(componentFirstJoint, componentSecondJoint);
        var componentThirdMidpoint = Midpoint(componentSecondJoint, componentThirdOuter);
        var componentBackZ = componentExtrusion.Z * 0.5f;
        if (!lineComponentStage.TryProjectScenePosition(
                new System.Numerics.Vector3(
                    componentFirstMidpoint.X,
                    componentFirstMidpoint.Y,
                    componentBackZ),
                out var componentFirstBackSample,
                out _)
            || !lineComponentStage.TryProjectScenePosition(
                new System.Numerics.Vector3(
                    componentSecondMidpoint.X,
                    componentSecondMidpoint.Y,
                    componentBackZ),
                out var componentSecondBackSample,
                out _)
            || !lineComponentStage.TryProjectScenePosition(
                new System.Numerics.Vector3(
                    componentThirdMidpoint.X,
                    componentThirdMidpoint.Y,
                    componentBackZ),
                out var componentThirdBackSample,
                out _)
            || !lineComponentStage.TryProjectScenePoint(
                componentFirst,
                componentFirstJoint,
                out var componentFirstJointScreen,
                out _))
        {
            throw new InvalidOperationException(
                "The shared Sharp Line shell fixture could not project its member samples.");
        }
        var componentBackItem = componentBackItems[0];
        var componentFirstBackContained = ProjectedFillContainsMargin(
            componentBackItem.Contours,
            Point.Round(componentFirstBackSample),
            0);
        var componentSecondBackContained = ProjectedFillContainsMargin(
            componentBackItem.Contours,
            Point.Round(componentSecondBackSample),
            0);
        var componentThirdBackContained = ProjectedFillContainsMargin(
            componentBackItem.Contours,
            Point.Round(componentThirdBackSample),
            0);
        var componentFirstResolved = lineComponentStage.TryResolveReference3DSharedShellHit(
            Point.Round(componentFirstBackSample),
            componentBackItem,
            out var componentFirstHit);
        var componentSecondResolved = lineComponentStage.TryResolveReference3DSharedShellHit(
            Point.Round(componentSecondBackSample),
            componentBackItem,
            out var componentSecondHit);
        var componentThirdResolved = lineComponentStage.TryResolveReference3DSharedShellHit(
            Point.Round(componentThirdBackSample),
            componentBackItem,
            out var componentThirdHit);
        var componentJointResolved = lineComponentStage.TryResolveReference3DSharedShellHit(
            Point.Round(componentFirstJointScreen),
            componentBackItem,
            out var componentJointHit);
        if (!componentFirstBackContained
            || !componentSecondBackContained
            || !componentThirdBackContained
            || !componentFirstResolved
            || componentFirstHit != componentFirst
            || !componentSecondResolved
            || componentSecondHit != componentSecond
            || !componentThirdResolved
            || componentThirdHit != componentThird
            || !componentJointResolved
            || (componentJointHit != componentFirst && componentJointHit != componentSecond))
        {
            throw new InvalidOperationException(
                "Shared Line extrusion hit resolution collapsed member identity onto its shell owner: "
                + $"hits={componentFirstHit}/{componentSecondHit}/{componentThirdHit}/{componentJointHit}, "
                + $"contained={componentFirstBackContained}/{componentSecondBackContained}/"
                + $"{componentThirdBackContained}, "
                + $"samples={componentFirstBackSample}/{componentSecondBackSample}/"
                + $"{componentThirdBackSample}/{componentFirstJointScreen}.");
        }

        var transparentPreview = new VectorScene();
        transparentPreview.CreateEmpty();
        transparentPreview.AddObject(
            0,
            PointF.Empty,
            new SizeF(10, 10),
            0,
            0,
            Color.Transparent,
            Color.Transparent,
            12,
            ShapeKind.Rectangle);
        lineComponentStage.BindDragPreviewScene(
            transparentPreview,
            lineComponentScene,
            hiddenSourceObjects: [componentFirst]);
        var hiddenOwnerItems = lineComponentStage.GetReference3DLayerRenderItems(componentMembers);
        var hiddenOwnerBack = hiddenOwnerItems.Single(item =>
            item.Kind == Reference3DRenderKind.Back);
        var remainingAfterOwnerHide = new[] { componentSecond, componentThird };
        if (hiddenOwnerItems.Any(item => item.ObjectIndex == componentFirst)
            || hiddenOwnerBack.ObjectIndex != componentSecond
            || hiddenOwnerItems
                .Where(item => item.Kind is Reference3DRenderKind.Back or Reference3DRenderKind.Side)
                .Any(item => item.SharedShellObjectIndices is not { } members
                    || !members.SequenceEqual(remainingAfterOwnerHide))
            || ProjectedFillContainsMargin(
                hiddenOwnerBack.Contours,
                Point.Round(componentFirstBackSample),
                0)
            || !ProjectedFillContainsMargin(
                hiddenOwnerBack.Contours,
                Point.Round(componentSecondBackSample),
                0))
        {
            throw new InvalidOperationException(
                "Hiding the prior Sharp Line shell owner removed visible extrusion or retained hidden geometry.");
        }
        using (var hiddenOwnerBitmap = RenderGdi(lineComponentStage))
        {
            AssertPixelNear(
                SampleBitmap(hiddenOwnerBitmap, componentFirstBackSample),
                background,
                "The GDI shared Line shell retained a hidden owner arm",
                tolerance: 10);
        }

        lineComponentStage.BindDragPreviewScene(
            transparentPreview,
            lineComponentScene,
            hiddenSourceObjects: [componentThird]);
        var hiddenMemberItems = lineComponentStage.GetReference3DLayerRenderItems(componentMembers);
        var hiddenMemberBack = hiddenMemberItems.Single(item =>
            item.Kind == Reference3DRenderKind.Back);
        var remainingAfterMemberHide = new[] { componentFirst, componentSecond };
        if (hiddenMemberItems.Any(item => item.ObjectIndex == componentThird)
            || hiddenMemberBack.ObjectIndex != componentFirst
            || hiddenMemberItems
                .Where(item => item.Kind is Reference3DRenderKind.Back or Reference3DRenderKind.Side)
                .Any(item => item.SharedShellObjectIndices is not { } members
                    || !members.SequenceEqual(remainingAfterMemberHide))
            || ProjectedFillContainsMargin(
                hiddenMemberBack.Contours,
                Point.Round(componentThirdBackSample),
                0)
            || !ProjectedFillContainsMargin(
                hiddenMemberBack.Contours,
                Point.Round(componentFirstBackSample),
                0))
        {
            throw new InvalidOperationException(
                "Hiding a non-owner Sharp Line retained its arm in the shared extrusion shell.");
        }
        lineComponentStage.BindDragPreviewScene(null);
        Console.WriteLine("scene_reference_line_component_shell=union_visibility_hit_ok");

        static PointF ProjectedEndpointExteriorSample(PointF endpoint, PointF interior, float distance)
        {
            var dx = endpoint.X - interior.X;
            var dy = endpoint.Y - interior.Y;
            var length = MathF.Sqrt(dx * dx + dy * dy);
            if (length <= 0.0001f) return endpoint;
            return new PointF(
                endpoint.X + dx / length * distance,
                endpoint.Y + dy / length * distance);
        }

        static PointF Midpoint(PointF first, PointF second) => new(
            (first.X + second.X) * 0.5f,
            (first.Y + second.Y) * 0.5f);

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
                SampleBitmap(blendDepthBitmap, blendDepthCenter),
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
        var containedStrokeScene = new VectorScene();
        containedStrokeScene.CreateEmpty();
        var containedStrokeCoverLayer = containedStrokeScene.AddLayer("Contained stroke cover");
        var containedStrokeColor = Color.White;
        var containedStrokeCoverColor = Color.FromArgb(255, 42, 204, 116);
        var containedStrokeObject = containedStrokeScene.AddLineSegment(
            0,
            new PointF(-600, 700),
            new PointF(600, 700),
            240,
            Color.Transparent,
            containedStrokeColor,
            12);
        var containedStrokeCoverObject = containedStrokeScene.AddObject(
            containedStrokeCoverLayer,
            PointF.Empty,
            new SizeF(8_000, 8_000),
            angle: 0,
            stroke: 0,
            color: containedStrokeCoverColor,
            strokeColor: Color.Transparent,
            atoms: 12,
            shapeKind: ShapeKind.Rectangle);
        var containedStrokeTransform = System.Numerics.Matrix4x4.CreateRotationX(0.18f);
        var containedStrokeCoverTransform = System.Numerics.Matrix4x4.CreateRotationX(0.72f);
        using var containedStrokeStage = CreateStage(containedStrokeScene);
        var containedStrokeView = new SceneDefinition { Dimension = SceneDimension.ThreeD };
        ConfigureNeutralReferenceLighting(containedStrokeView);
        containedStrokeView.Camera.Projection = CameraProjection.Perspective;
        containedStrokeStage.ConfigureReferenceView(containedStrokeView, SceneDimension.ThreeD);
        containedStrokeStage.ResetReferenceCameraView();
        containedStrokeStage.SetReferenceCameraOrientation(0.43f, -0.24f);
        containedStrokeStage.SetSceneCompositionResult(
            CompositionResult(containedStrokeTransform, containedStrokeCoverTransform),
            containedStrokeScene);
        var containedStrokeLocalSample = new PointF(0, 700);
        if (!containedStrokeStage.TryProjectScenePoint(
                containedStrokeObject,
                containedStrokeLocalSample,
                out var containedStrokeProjectedSample,
                out _))
        {
            throw new InvalidOperationException("The contained-stroke sample could not be projected.");
        }
        var containedStrokeSample = Point.Round(containedStrokeProjectedSample);
        var containedStrokeCoverContours =
            containedStrokeStage.GetReference3DProjectedContours(containedStrokeCoverObject);
        var containedStrokeInsideCover = ProjectedFillContainsMargin(
            containedStrokeCoverContours,
            containedStrokeSample,
            8);
        var containedStrokeHasRay = containedStrokeStage.TryGetReferenceRay(
            containedStrokeSample,
            out var containedStrokeRay);
        var containedStrokeDepth = 0f;
        var containedStrokeHasDepth = containedStrokeHasRay
            && TryIntersectCardPlane(
                containedStrokeRay,
                containedStrokeTransform,
                out containedStrokeDepth,
                out _);
        var containedStrokeCoverDepth = 0f;
        var containedStrokeCoverHasDepth = containedStrokeHasRay
            && TryIntersectCardPlane(
                containedStrokeRay,
                containedStrokeCoverTransform,
                out containedStrokeCoverDepth,
                out _);
        if (!containedStrokeInsideCover
            || !containedStrokeHasDepth
            || !containedStrokeCoverHasDepth
            || Math.Abs(containedStrokeDepth - containedStrokeCoverDepth) < 90f)
        {
            throw new InvalidOperationException(
                "The contained-stroke sample did not expose stable depth overlap inside its cover plane: "
                + $"inside={containedStrokeInsideCover}, ray={containedStrokeHasRay}, "
                + $"line={containedStrokeHasDepth}/{containedStrokeDepth:0.###}, "
                + $"cover={containedStrokeCoverHasDepth}/{containedStrokeCoverDepth:0.###}.");
        }
        var containedRawItems = containedStrokeStage.GetReference3DLayerRenderItems(
            [containedStrokeObject, containedStrokeCoverObject]);
        var containedRawStroke = containedRawItems.Single(item =>
            item.ObjectIndex == containedStrokeObject
            && item.Kind == Reference3DRenderKind.FrontStroke);
        var containedRawCover = containedRawItems.Single(item =>
            item.ObjectIndex == containedStrokeCoverObject
            && item.Kind == Reference3DRenderKind.FrontFill);
        var containedStrokeVisible = containedStrokeDepth < containedStrokeCoverDepth;
        if (containedStrokeVisible
            == (containedRawStroke.AverageDepth < containedRawCover.AverageDepth))
        {
            throw new InvalidOperationException(
                "The contained-stroke fixture did not distinguish local depth from whole-object depth: "
                + $"local={containedStrokeDepth:0.###}/{containedStrokeCoverDepth:0.###}, "
                + $"average={containedRawStroke.AverageDepth:0.###}/{containedRawCover.AverageDepth:0.###}.");
        }
        var containedStrokeItems = containedStrokeStage.GetReference3DSceneRenderItems();
        var containedStrokeFragments = containedStrokeItems.Where(item =>
                item.ObjectIndex == containedStrokeObject
                && item.Kind == Reference3DRenderKind.FrontStroke)
            .ToArray();
        if (containedStrokeFragments.Length != 1
            || containedStrokeFragments[0].OcclusionContours is not { Length: > 0 }
            || containedStrokeFragments[0].FragmentClip is not { Length: > 0 })
        {
            throw new InvalidOperationException(
                "A fully covered stroke did not retain its single local-depth fragment plan.");
        }
        if (containedStrokeFragments[0].OcclusionContours!.Any(contour =>
                contour.Points.Any(point => !ProjectedFillContainsMargin(
                    containedStrokeCoverContours,
                    Point.Round(point),
                    4))))
        {
            throw new InvalidOperationException(
                "The contained-stroke occupancy extended outside its cover plane.");
        }
        var containedStrokeCoverItems = containedStrokeItems.Where(item =>
                item.ObjectIndex == containedStrokeCoverObject
                && item.Kind == Reference3DRenderKind.FrontFill)
            .ToArray();
        if (containedStrokeCoverItems.Length != 1
            || containedStrokeCoverItems[0].FragmentClip is not null)
        {
            throw new InvalidOperationException(
                "A narrow contained stroke unnecessarily fragmented its large cover plane.");
        }
        if (containedStrokeItems.Any(item => item.Kind == Reference3DRenderKind.IntersectionEdge))
        {
            throw new InvalidOperationException(
                "A stroke-only intersection generated a synthetic surface connection edge.");
        }
        foreach (var endpoint in new[] { new PointF(-600, 700), new PointF(600, 700) })
        {
            if (!containedStrokeStage.TryProjectScenePoint(
                    containedStrokeObject,
                    endpoint,
                    out var projectedEndpoint,
                    out _)
                || !ProjectedFillContainsMargin(
                    containedStrokeCoverContours,
                    Point.Round(projectedEndpoint),
                    8)
                || !containedStrokeStage.TryGetReferenceRay(
                    Point.Round(projectedEndpoint),
                    out var endpointRay)
                || !TryIntersectCardPlane(
                    endpointRay,
                    containedStrokeTransform,
                    out var endpointStrokeDepth,
                    out _)
                || !TryIntersectCardPlane(
                    endpointRay,
                    containedStrokeCoverTransform,
                    out var endpointCoverDepth,
                    out _)
                || Math.Abs(endpointStrokeDepth - endpointCoverDepth) < 90f
                || (endpointStrokeDepth < endpointCoverDepth) != containedStrokeVisible)
            {
                throw new InvalidOperationException(
                    "The contained-stroke endpoints did not remain fully covered on one depth side.");
            }
        }
        var containedStrokeExpectedObject = containedStrokeVisible
            ? containedStrokeObject
            : containedStrokeCoverObject;
        var containedStrokeExpectedColor = containedStrokeVisible
            ? containedStrokeColor
            : containedStrokeCoverColor;
        using (var containedStrokeBitmap = RenderGdi(containedStrokeStage))
        {
            AssertPixelNear(
                SampleBitmap(containedStrokeBitmap, containedStrokeSample),
                containedStrokeExpectedColor,
                "A contained 3D stroke used whole-object depth instead of local cover depth",
                tolerance: 20);
        }
        if (!containedStrokeStage.TryHitTestProjectedObject(
                containedStrokeSample,
                1f,
                out var containedStrokeHit)
            || containedStrokeHit != containedStrokeExpectedObject)
        {
            throw new InvalidOperationException(
                "Contained-stroke hit testing disagreed with its locally visible object: "
                + $"expected={containedStrokeExpectedObject}, hit={containedStrokeHit}.");
        }
        var crossingLineScene = new VectorScene();
        crossingLineScene.CreateEmpty();
        var crossingLineBottomLayer = crossingLineScene.AddLayer("Crossing line cover");
        var crossingLineColor = Color.White;
        var crossingLineFillColor = Color.FromArgb(255, 42, 204, 116);
        var crossingLineObject = crossingLineScene.AddLineSegment(
            0,
            new PointF(0, -1_000),
            new PointF(0, 1_000),
            96,
            Color.Transparent,
            crossingLineColor,
            12);
        var crossingLineFillObject = crossingLineScene.AddObject(
            crossingLineBottomLayer,
            PointF.Empty,
            crossingCardSize,
            angle: 0,
            stroke: 0,
            color: crossingLineFillColor,
            strokeColor: Color.Transparent,
            atoms: 12,
            shapeKind: ShapeKind.Rectangle);
        using var crossingLineStage = CreateStage(crossingLineScene);
        var crossingLineView = new SceneDefinition { Dimension = SceneDimension.ThreeD };
        ConfigureNeutralReferenceLighting(crossingLineView);
        crossingLineView.Camera.Projection = CameraProjection.Perspective;
        crossingLineStage.ConfigureReferenceView(crossingLineView, SceneDimension.ThreeD);
        crossingLineStage.ResetReferenceCameraView();
        crossingLineStage.SetReferenceCameraOrientation(0.43f, -0.24f);
        crossingLineStage.SetSceneCompositionResult(
            CompositionResult(crossingCardATransform, crossingCardBTransform),
            crossingLineScene);
        var crossingLineSamples =
            new List<(Point Screen, int ExpectedObject, Color ExpectedColor)>();
        foreach (var localY in new[] { -700f, 700f })
        {
            if (!crossingLineStage.TryProjectScenePoint(
                    crossingLineObject,
                    new PointF(0, localY),
                    out var projected,
                    out _))
            {
                throw new InvalidOperationException("The crossing-line sample could not be projected.");
            }

            var screen = Point.Round(projected);
            if (!ProjectedFillContainsMargin(
                    crossingLineStage.GetReference3DProjectedContours(crossingLineFillObject),
                    screen,
                    4)
                || !crossingLineStage.TryGetReferenceRay(screen, out var ray)
                || !TryIntersectCardPlane(ray, crossingCardATransform, out var lineDepth, out _)
                || !TryIntersectCardPlane(ray, crossingCardBTransform, out var fillDepth, out _)
                || Math.Abs(lineDepth - fillDepth) < 90f)
            {
                throw new InvalidOperationException(
                    $"The crossing-line sample at local Y={localY:0.###} did not expose stable depth overlap.");
            }
            var lineVisible = lineDepth < fillDepth;
            crossingLineSamples.Add((
                screen,
                lineVisible ? crossingLineObject : crossingLineFillObject,
                lineVisible ? crossingLineColor : crossingLineFillColor));
        }
        if (crossingLineSamples.Count(sample =>
                sample.ExpectedObject == crossingLineObject) != 1
            || crossingLineSamples.Count(sample =>
                sample.ExpectedObject == crossingLineFillObject) != 1)
        {
            throw new InvalidOperationException(
                "The crossing-line fixture did not expose one visible-line half and one cover-occluded half.");
        }
        var crossingLineItems = crossingLineStage.GetReference3DSceneRenderItems();
        if (crossingLineItems.Count(item => item.ObjectIndex == crossingLineObject
                && item.Kind == Reference3DRenderKind.FrontStroke
                && item.OcclusionContours is { Length: > 0 }
                && item.FragmentClip is { Length: > 0 }) < 2)
        {
            throw new InvalidOperationException(
                "A stroke-only 3D line did not receive local occlusion fragments.");
        }
        using (var crossingLineBitmap = RenderGdi(crossingLineStage))
        {
            foreach (var sample in crossingLineSamples)
            {
                AssertPixelNear(
                    SampleBitmap(crossingLineBitmap, sample.Screen),
                    sample.ExpectedColor,
                    "A crossing 3D line segment used one whole-object depth across its cover plane",
                    tolerance: 20);
                if (!crossingLineStage.TryHitTestProjectedObject(
                        sample.Screen,
                        1f,
                        out var hit)
                    || hit != sample.ExpectedObject)
                {
                    throw new InvalidOperationException(
                        $"Crossing-line hit testing disagreed with the visible fragment at {sample.Screen}: "
                        + $"expected={sample.ExpectedObject}, hit={hit}.");
                }
            }
        }
        var crossingDirect2DSamples = new List<(Point Screen, int ObjectIndex, Color ExpectedColor)>();
        var crossingOccludedStrokeSamples =
            new List<(
                Point Screen,
                int ObjectIndex,
                Color ExpectedColor,
                int SourceObject,
                PointF SourcePoint,
                float DepthGap)>();
        var crossingRotationDirect2DSamples =
            new List<(float Yaw, float Pitch, Point Screen, Color ExpectedColor)>();
        var crossingIntersectionOrbitAnchors = new[] { -1_200f, -600f, 0f, 600f, 1_200f };
        var crossingIntersectionDirect2DFrames = new[] { 0, 12, 24, 36, 48 };
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
        ConfigureNeutralReferenceLighting(crossingView);
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
        AssertCrossingCardOpacity(useParentFolder: false);
        AssertCrossingCardOpacity(useParentFolder: true);
        AssertCrossingCardRotationTrajectory();
        AssertCrossingIntersectionEdgeOrbit();
        RunCrossingIntersectionEdgeOpticalLightingRegression();
        VectorScene? closedBezierIntersectionScene = null;
        var closedBezierDirect2DSamples = new List<(Point Screen, Color ExpectedColor)>();
        RunClosedBezierSurfaceIntersectionRegression();
        RunDisconnectedOverlapGapRegression();

        void RunCrossingIntersectionEdgeOpticalLightingRegression()
        {
            var edgePoint = new PointF();
            var frontLight = new SceneLightDefinition(
                "intersection-edge-front-light",
                "Intersection edge front light",
                SceneLightKind.Directional,
                new SceneLightSettings(
                    true,
                    Color.FromArgb(255, 255, 56, 36).ToArgb(),
                    1f,
                    0f,
                    System.Numerics.Vector3.Zero,
                    new System.Numerics.Vector3(0, 180, 0),
                    System.Numerics.Vector2.Zero,
                    false,
                    0f,
                    0f));
            var backLight = new SceneLightDefinition(
                "intersection-edge-back-light",
                "Intersection edge back light",
                SceneLightKind.Directional,
                frontLight.Settings with
                {
                    ColorArgb = Color.FromArgb(255, 36, 120, 255).ToArgb(),
                    RotationDegrees = System.Numerics.Vector3.Zero
                });

            if (!crossingStage.TryProjectScenePosition(
                    System.Numerics.Vector3.Zero,
                    out edgePoint,
                    out _))
            {
                throw new InvalidOperationException(
                    "The intersection-edge optical fixture could not project its sample point.");
            }

            var noLightView = new SceneDefinition { Dimension = SceneDimension.ThreeD };
            noLightView.Camera.Projection = CameraProjection.Perspective;
            noLightView.RestoreLights([], lightsWerePresent: true);
            var noLightEdge = ConfigureEdgeLighting(noLightView);
            if (noLightEdge.SolidStrokeOpticalBaseArgb is not null
                || noLightEdge.LocalLightLayers is { Length: > 0 }
                || noLightEdge.OpticalSurfaceContours is not { Length: > 0 })
            {
                throw new InvalidOperationException(
                    "An unlit intersection edge did not preserve its vector fallback state.");
            }
            using var noLightBitmap = RenderGdi(crossingStage);
            var noLightPixel = SampleBitmap(noLightBitmap, edgePoint);
            if (!PixelRgbNear(noLightPixel, crossingEdgeColor, 18))
            {
                throw new InvalidOperationException(
                    "An unlit intersection edge changed its original color: "
                    + $"actual={noLightPixel.ToArgb():X8}, expected={crossingEdgeColor.ToArgb():X8}.");
            }

            var frontView = new SceneDefinition { Dimension = SceneDimension.ThreeD };
            frontView.Camera.Projection = CameraProjection.Perspective;
            frontView.RestoreLights([frontLight], lightsWerePresent: true);
            var frontEdge = ConfigureEdgeLighting(frontView);
            if (frontEdge.OpticalSurfaceContours is not { Length: > 0 }
                || frontEdge.SolidStrokeOpticalBaseArgb is null
                || frontEdge.LocalLightLayers is not { Length: > 0 })
            {
                throw new InvalidOperationException(
                    "A front-lit intersection edge did not receive vector optical state.");
            }
            using var frontBitmap = RenderGdi(crossingStage);
            var frontPixel = SampleBitmap(frontBitmap, edgePoint);
            if (PixelRgbNear(frontPixel, noLightPixel, 18))
            {
                throw new InvalidOperationException(
                    "Front lighting did not change the automatic intersection-edge pixel: "
                    + $"unlit={noLightPixel.ToArgb():X8}, lit={frontPixel.ToArgb():X8}.");
            }

            var backView = new SceneDefinition { Dimension = SceneDimension.ThreeD };
            backView.Camera.Projection = CameraProjection.Perspective;
            backView.RestoreLights([backLight], lightsWerePresent: true);
            var backEdge = ConfigureEdgeLighting(backView);
            if (backEdge.LocalLightLayers is { Length: > 0 })
            {
                throw new InvalidOperationException(
                    "Back lighting incorrectly crossed the Primary intersection surface.");
            }
            using var backBitmap = RenderGdi(crossingStage);
            var backPixel = SampleBitmap(backBitmap, edgePoint);
            if (PixelRgbNear(backPixel, frontPixel, 18))
            {
                throw new InvalidOperationException(
                    "Back lighting produced the same automatic intersection-edge color as front lighting: "
                    + $"front={frontPixel.ToArgb():X8}, back={backPixel.ToArgb():X8}.");
            }

            var oppositeYaw = 0.43f + MathF.PI;
            const float oppositePitch = 0.24f;
            var oppositeNoLightEdge = ConfigureEdgeLighting(
                noLightView,
                oppositeYaw,
                oppositePitch);
            if (oppositeNoLightEdge.LocalLightLayers is { Length: > 0 }
                || !crossingStage.TryProjectScenePosition(
                    System.Numerics.Vector3.Zero,
                    out var oppositeEdgePoint,
                    out _))
            {
                throw new InvalidOperationException(
                    "The opposite-side intersection-edge fixture did not preserve its unlit state.");
            }
            using var oppositeNoLightBitmap = RenderGdi(crossingStage);
            var oppositeNoLightPixel = SampleBitmap(oppositeNoLightBitmap, oppositeEdgePoint);

            var oppositeBackEdge = ConfigureEdgeLighting(
                backView,
                oppositeYaw,
                oppositePitch);
            if (oppositeBackEdge.LocalLightLayers is not { Length: > 0 } oppositeBackLayers
                || oppositeBackLayers.Any(layer => layer.SolidStrokeStops is not { Length: > 0 }))
            {
                throw new InvalidOperationException(
                    "A directional light on the visible side did not illuminate the opposite-side intersection edge.");
            }
            using var oppositeBackBitmap = RenderGdi(crossingStage);
            var oppositeBackPixel = SampleBitmap(oppositeBackBitmap, oppositeEdgePoint);
            if (PixelRgbNear(oppositeBackPixel, oppositeNoLightPixel, 18))
            {
                throw new InvalidOperationException(
                    "Opposite-side directional lighting did not change the automatic intersection-edge pixel: "
                    + $"unlit={oppositeNoLightPixel.ToArgb():X8}, lit={oppositeBackPixel.ToArgb():X8}.");
            }

            var oppositeFrontEdge = ConfigureEdgeLighting(
                frontView,
                oppositeYaw,
                oppositePitch);
            if (oppositeFrontEdge.LocalLightLayers is { Length: > 0 })
            {
                throw new InvalidOperationException(
                    "A directional light behind the visible side leaked onto the opposite-side intersection edge.");
            }

            crossingStage.ConfigureReferenceView(crossingView, SceneDimension.ThreeD);
            crossingStage.ResetReferenceCameraView();
            crossingStage.SetReferenceCameraOrientation(0.43f, -0.24f);
            crossingStage.SetSceneCompositionResult(
                CompositionResult(crossingCardATransform, crossingCardBTransform),
                crossingScene);
            Console.WriteLine("scene_reference_intersection_edge_optics=ok");

            Reference3DRenderItem ConfigureEdgeLighting(
                SceneDefinition view,
                float yaw = 0.43f,
                float pitch = -0.24f)
            {
                crossingStage.ConfigureReferenceView(view, SceneDimension.ThreeD);
                crossingStage.ResetReferenceCameraView();
                crossingStage.SetReferenceCameraOrientation(yaw, pitch);
                crossingStage.SetSceneCompositionResult(
                    CompositionResult(crossingCardATransform, crossingCardBTransform),
                    crossingScene);
                return crossingStage.GetReference3DSceneRenderItems().Single(item =>
                    item.Kind == Reference3DRenderKind.IntersectionEdge
                    && (item.ObjectIndex == crossingCardA
                        && item.SecondaryObjectIndex == crossingCardB
                        || item.ObjectIndex == crossingCardB
                        && item.SecondaryObjectIndex == crossingCardA));
            }
        }

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
                    SampleBitmap(blendedBitmap, sample.Screen),
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
        ConfigureNeutralReferenceLighting(grazingView);
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
            var grazingActual = SampleBitmap(grazingBitmap, grazingEdgeSample);
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
        AssertGrazingIntersectionEdgeOrbit();

        const float thickEdgeIncidentAngle = 0.62f;
        const float thickEdgeStroke = 360f;
        const float thickEdgeCoverPivotY = 80f;
        const float thickEdgeCoverTilt = 0.88f;
        var thickEdgeIncidentATransform = System.Numerics.Matrix4x4.CreateRotationX(
            thickEdgeIncidentAngle);
        var thickEdgeIncidentBTransform = System.Numerics.Matrix4x4.CreateRotationX(
            -thickEdgeIncidentAngle);
        var thickEdgeCoverTransform = System.Numerics.Matrix4x4.CreateTranslation(
                0,
                -thickEdgeCoverPivotY,
                0)
            * System.Numerics.Matrix4x4.CreateRotationX(thickEdgeCoverTilt)
            * System.Numerics.Matrix4x4.CreateTranslation(0, thickEdgeCoverPivotY, -800f);
        var thickEdgeSurfaceColor = Color.FromArgb(255, 36, 70, 108);
        var thickEdgeCoverColor = Color.FromArgb(255, 242, 196, 54);
        var thickEdgeScene = new VectorScene();
        thickEdgeScene.CreateEmpty();
        var thickEdgeIncidentB = thickEdgeScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(3_600, 60),
            angle: 0,
            stroke: thickEdgeStroke,
            color: thickEdgeSurfaceColor,
            strokeColor: Color.Transparent,
            atoms: 12,
            shapeKind: ShapeKind.Rectangle);
        var thickEdgeIncidentALayer = thickEdgeScene.AddLayer("Thick intersection incident surface");
        var thickEdgeIncidentA = thickEdgeScene.AddObject(
            thickEdgeIncidentALayer,
            PointF.Empty,
            new SizeF(8_000, 8_000),
            angle: 0,
            stroke: 0,
            color: thickEdgeSurfaceColor,
            strokeColor: Color.Transparent,
            atoms: 12,
            shapeKind: ShapeKind.Rectangle);
        var thickEdgeCoverLayer = thickEdgeScene.AddLayer("Thick intersection grazing cover");
        var thickEdgeCover = thickEdgeScene.AddObject(
            thickEdgeCoverLayer,
            new PointF(0, 1_280),
            new SizeF(2_400, 2_400),
            angle: 0,
            stroke: 0,
            color: thickEdgeCoverColor,
            strokeColor: Color.Transparent,
            atoms: 12,
            shapeKind: ShapeKind.Rectangle);
        using var thickEdgeStage = CreateStage(thickEdgeScene);
        var thickEdgeView = new SceneDefinition { Dimension = SceneDimension.ThreeD };
        ConfigureNeutralReferenceLighting(thickEdgeView);
        thickEdgeView.Camera.Projection = CameraProjection.Perspective;
        thickEdgeStage.ConfigureReferenceView(thickEdgeView, SceneDimension.ThreeD);
        thickEdgeStage.ResetReferenceCameraView();
        thickEdgeStage.SetSceneCompositionResult(
            CompositionResult(
                thickEdgeIncidentBTransform,
                thickEdgeIncidentATransform,
                thickEdgeCoverTransform),
            thickEdgeScene);
        const int thickEdgeOrbitFrameCount = 17;
        var thickEdgeDirect2DFrames = new[] { 0, 4, 8, 12, 16 };
        for (var frame = 0; frame < thickEdgeOrbitFrameCount; frame++)
        {
            var progress = frame / (thickEdgeOrbitFrameCount - 1f);
            var yaw = -0.12f + 0.24f * progress;
            var pitch = 0.02f * MathF.Sin(progress * MathF.PI * 2f);
            var samples = AssertThickEdgeGrazingFrame(
                thickEdgeStage,
                yaw,
                pitch,
                $"GDI frame={frame}");
            using var bitmap = RenderGdi(thickEdgeStage);
            AssertThickEdgeGrazingPixels(bitmap, samples, $"GDI frame={frame}");
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
        var threeCardOccludedEdgeSamples = new List<(Point Screen, Color ExpectedColor)>();
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
        ConfigureNeutralReferenceLighting(threeCardView);
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
        var coldPlanBuildsBefore = threeCardStage.Reference3DRenderPlanBuildCount;
        AssertThreeCrossingCardView("perspective-oblique");
        AssertOccludedThreeCardEdges();
        var coldPlanBuilds = threeCardStage.Reference3DRenderPlanBuildCount - coldPlanBuildsBefore;
        if (coldPlanBuilds != 1)
        {
            throw new InvalidOperationException(
                $"The stable three-card render built {coldPlanBuilds} reference plans; expected one shared display and hit-test plan.");
        }

        var threeCardFragmentProbeCandidates = new[] { 450f, 700f }
            .SelectMany(radius => Enumerable.Range(0, 32).Select(index =>
            {
                var angle = index * MathF.PI * 2f / 32f + 0.043f;
                return new PointF(radius * MathF.Cos(angle), radius * MathF.Sin(angle));
            }))
            .ToArray();
        var threeCardFragmentObservations = new Dictionary<
            (int ObjectIndex, int CandidateIndex),
            List<(int Frame, ulong FragmentIdentity)>>();
        const int threeCardFragmentOrbitFrames = 17;
        for (var frame = 0; frame < threeCardFragmentOrbitFrames; frame++)
        {
            var progress = frame / (threeCardFragmentOrbitFrames - 1f);
            var yaw = -0.55f + 1.10f * progress;
            var pitch = -0.18f + 0.08f * MathF.Sin(progress * MathF.PI * 2f);
            threeCardStage.SetReferenceCameraOrientation(yaw, pitch);
            var renderItems = threeCardStage.GetReference3DSceneRenderItems();
            foreach (var objectIndex in threeCardObjects)
            {
                for (var candidateIndex = 0;
                     candidateIndex < threeCardFragmentProbeCandidates.Length;
                     candidateIndex++)
                {
                    var sourcePoint = threeCardFragmentProbeCandidates[candidateIndex];
                    if (!TryResolvePhysicalFragmentSlot(
                            threeCardStage,
                            renderItems,
                            objectIndex,
                            sourcePoint,
                            out var fragmentIdentity))
                    {
                        continue;
                    }
                    var key = (objectIndex, candidateIndex);
                    if (!threeCardFragmentObservations.TryGetValue(key, out var observations))
                    {
                        observations = [];
                        threeCardFragmentObservations.Add(key, observations);
                    }
                    observations.Add((frame, fragmentIdentity));
                }
            }
        }
        foreach (var pair in threeCardFragmentObservations)
        {
            var distinctSlots = pair.Value.Select(value => value.FragmentIdentity).Distinct().ToArray();
            if (distinctSlots.Length <= 1) continue;
            throw new InvalidOperationException(
                "A physical surface fragment changed identity during the camera orbit: "
                + $"object={pair.Key.ObjectIndex}, "
                + $"source={threeCardFragmentProbeCandidates[pair.Key.CandidateIndex]}, "
                + $"observations=[{string.Join(',', pair.Value)}].");
        }
        foreach (var objectIndex in threeCardObjects)
        {
            var retained = threeCardFragmentObservations.Count(pair =>
                pair.Key.ObjectIndex == objectIndex
                && pair.Value.Count >= 12);
            if (retained < 4)
            {
                throw new InvalidOperationException(
                    "The three-card fixture could not retain enough physical fragment probes: "
                    + $"object={objectIndex}, retained={retained}.");
            }
        }
        threeCardStage.SetReferenceCameraOrientation(0.47f, -0.29f);

        const int renderPlanCacheSamples = 64;
        const long renderPlanCacheAllocationBudget = 4_096;
        var cachedRenderItems = threeCardStage.GetReference3DSceneRenderItems();
        var cachedRenderItemCount = cachedRenderItems.Length;
        var cachedPlanBuildsBefore = threeCardStage.Reference3DRenderPlanBuildCount;
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var sameCachedRenderItems = true;
        long observedCachedRenderItems = 0;
        for (var sample = 0; sample < renderPlanCacheSamples; sample++)
        {
            var renderItems = threeCardStage.GetReference3DSceneRenderItems();
            sameCachedRenderItems &= ReferenceEquals(cachedRenderItems, renderItems);
            observedCachedRenderItems += renderItems.Length;
        }
        var referencePlanCacheAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        var cachedPlanBuilds = threeCardStage.Reference3DRenderPlanBuildCount - cachedPlanBuildsBefore;
        if (cachedPlanBuilds != 0
            || !sameCachedRenderItems
            || observedCachedRenderItems != (long)cachedRenderItemCount * renderPlanCacheSamples
            || referencePlanCacheAllocatedBytes > renderPlanCacheAllocationBudget)
        {
            throw new InvalidOperationException(
                $"Stable reference-3D render-plan reuse regressed: builds={cachedPlanBuilds}, "
                + $"same={sameCachedRenderItems}, items={observedCachedRenderItems}/"
                + $"{(long)cachedRenderItemCount * renderPlanCacheSamples}, "
                + $"allocated={referencePlanCacheAllocatedBytes}.");
        }

        var overlayPlanBuildsBefore = threeCardStage.Reference3DRenderPlanBuildCount;
        threeCardStage.SetReference3DSelection([threeCardObjects[0]]);
        _ = threeCardStage.GetReference3DSceneRenderItems();
        threeCardStage.ClearReference3DSelection();
        _ = threeCardStage.GetReference3DSceneRenderItems();
        if (threeCardStage.Reference3DRenderPlanBuildCount != overlayPlanBuildsBefore)
        {
            throw new InvalidOperationException("Reference-3D overlay invalidation rebuilt the stable scene plan.");
        }

        var cameraPlanBuildsBefore = threeCardStage.Reference3DRenderPlanBuildCount;
        threeCardStage.SetReferenceCameraOrientation(0.51f, -0.31f);
        _ = threeCardStage.GetReference3DSceneRenderItems();
        _ = threeCardStage.GetReference3DSceneRenderItems();
        if (threeCardStage.Reference3DRenderPlanBuildCount != cameraPlanBuildsBefore + 1)
        {
            throw new InvalidOperationException("A reference-camera change did not lazily rebuild exactly one scene plan.");
        }

        var changedThreeCardComposition = CompositionResult(
            threeCardTransforms[0] * System.Numerics.Matrix4x4.CreateTranslation(0, 0, 48),
            threeCardTransforms[1],
            threeCardTransforms[2]);
        var compositionPlanBuildsBefore = threeCardStage.Reference3DRenderPlanBuildCount;
        threeCardStage.SetSceneCompositionResult(changedThreeCardComposition, threeCardScene);
        _ = threeCardStage.GetReference3DSceneRenderItems();
        _ = threeCardStage.GetReference3DSceneRenderItems();
        if (threeCardStage.Reference3DRenderPlanBuildCount != compositionPlanBuildsBefore + 1)
        {
            throw new InvalidOperationException("A scene-composition change did not lazily rebuild exactly one reference plan.");
        }

        var geometryRevisionBefore = threeCardScene.GeometryRevision;
        var geometryPlanBuildsBefore = threeCardStage.Reference3DRenderPlanBuildCount;
        threeCardScene.TransformObjects(
            [threeCardObjects[0]],
            static point => new PointF(point.X + 24f, point.Y));
        if (threeCardScene.GeometryRevision == geometryRevisionBefore)
        {
            throw new InvalidOperationException("The reference-plan geometry invalidation fixture did not advance its revision.");
        }
        var geometryRenderItems = threeCardStage.GetReference3DSceneRenderItems();
        _ = threeCardStage.GetReference3DSceneRenderItems();
        if (threeCardStage.Reference3DRenderPlanBuildCount != geometryPlanBuildsBefore + 1)
        {
            throw new InvalidOperationException("A geometry revision did not lazily rebuild exactly one reference plan.");
        }

        var directStateLayer = threeCardScene.ObjectLayer[threeCardObjects[0]];
        var layerStatePlanBuildsBefore = threeCardStage.Reference3DRenderPlanBuildCount;
        if (!threeCardScene.SetLayerOutline(directStateLayer, true)
            || !threeCardStage.GetReference3DSceneRenderItems().Any(item =>
                item.ObjectIndex == threeCardObjects[0]
                && item.Kind == Reference3DRenderKind.Outline)
            || threeCardStage.Reference3DRenderPlanBuildCount != layerStatePlanBuildsBefore + 1)
        {
            throw new InvalidOperationException("A direct layer-outline change reused a stale reference render plan.");
        }
        if (!threeCardScene.SetLayerOutline(directStateLayer, false)
            || !ReferenceEquals(geometryRenderItems, threeCardStage.GetReference3DSceneRenderItems()))
        {
            throw new InvalidOperationException("Restoring layer outline did not recover the matching cached render plan.");
        }

        layerStatePlanBuildsBefore = threeCardStage.Reference3DRenderPlanBuildCount;
        if (!threeCardScene.SetLayerBlendMode(directStateLayer, LayerBlendMode.Multiply))
        {
            throw new InvalidOperationException("The reference-plan layer-blend fixture could not change its blend mode.");
        }
        var blendedRenderItems = threeCardStage.GetReference3DSceneRenderItems();
        if (threeCardStage.Reference3DRenderPlanBuildCount != layerStatePlanBuildsBefore + 1)
        {
            throw new InvalidOperationException("A direct layer-blend change reused a stale reference render plan.");
        }
        var hiddenStateLayer = threeCardScene.ObjectLayer[threeCardObjects[1]];
        layerStatePlanBuildsBefore = threeCardStage.Reference3DRenderPlanBuildCount;
        if (!threeCardScene.SetLayerVisible(hiddenStateLayer, false)
            || threeCardStage.GetReference3DSceneRenderItems().Any(item =>
                item.ObjectIndex == threeCardObjects[1])
            || threeCardStage.Reference3DRenderPlanBuildCount != layerStatePlanBuildsBefore + 1)
        {
            throw new InvalidOperationException("A direct layer-visibility change reused a stale reference render plan.");
        }
        if (!threeCardScene.SetLayerVisible(hiddenStateLayer, true)
            || !ReferenceEquals(blendedRenderItems, threeCardStage.GetReference3DSceneRenderItems())
            || !threeCardScene.SetLayerBlendMode(directStateLayer, LayerBlendMode.Normal)
            || !ReferenceEquals(geometryRenderItems, threeCardStage.GetReference3DSceneRenderItems()))
        {
            throw new InvalidOperationException("Restoring direct layer state did not recover matching reference plans.");
        }

        var directTimelineTrack = threeCardScene.Timeline.FindTrackByTargetId(
            threeCardScene.LayerIds[directStateLayer]);
        layerStatePlanBuildsBefore = threeCardStage.Reference3DRenderPlanBuildCount;
        if (directTimelineTrack is null
            || !threeCardScene.Timeline.InsertBlankKeyframe(directTimelineTrack.Id, 0)
            || threeCardStage.GetReference3DSceneRenderItems().Any(item =>
                item.ObjectIndex == threeCardObjects[0])
            || threeCardStage.Reference3DRenderPlanBuildCount != layerStatePlanBuildsBefore + 1)
        {
            throw new InvalidOperationException("A direct timeline-exposure change reused a stale reference render plan.");
        }
        if (!threeCardScene.Timeline.InsertKeyframe(directTimelineTrack.Id, 0)
            || !ReferenceEquals(geometryRenderItems, threeCardStage.GetReference3DSceneRenderItems()))
        {
            throw new InvalidOperationException("Restoring timeline exposure did not recover the matching reference plan.");
        }

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
        ConfigureNeutralReferenceLighting(cyclicCardView);
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
        ConfigureNeutralReferenceLighting(fixedStrokeView);
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
            ConfigureNeutralReferenceLighting(fixedLineView);
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
            || solid.SidePlanes.Length != solid.SideSurfaces.Length
            || solid.SidePlaneKeys.Length != solid.SideSurfaces.Length
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
        if (!extrusionFrontItem.Plane.IsValid
            || !extrusionFrontItem.PlaneKey.IsValid
            || extrusionFrontItems.Any(item =>
                !item.Plane.IsValid
                || !item.PlaneKey.IsValid
                || item.PlaneKey != extrusionFrontItem.PlaneKey)
            || !extrusionBackItem.Plane.IsValid
            || !extrusionBackItem.PlaneKey.IsValid
            || extrusionFrontItem.PlaneKey.NormalX != -extrusionBackItem.PlaneKey.NormalX
            || extrusionFrontItem.PlaneKey.NormalY != -extrusionBackItem.PlaneKey.NormalY
            || extrusionFrontItem.PlaneKey.NormalZ != -extrusionBackItem.PlaneKey.NormalZ
            || System.Numerics.Vector3.Dot(
                    extrusionFrontItem.Plane.Normal,
                    extrusionBackItem.SurfacePoint - extrusionFrontItem.SurfacePoint) >= 0
            || System.Numerics.Vector3.Dot(
                    extrusionBackItem.Plane.Normal,
                    extrusionBackItem.SurfacePoint - extrusionFrontItem.SurfacePoint) <= 0
            || extrusionSideItems.Length == 0
            || extrusionSideItems.Any(item => !item.Plane.IsValid || !item.PlaneKey.IsValid))
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
        if (extrusionStage.GetReference3DExtrusionSurfaceColor(extrusionBackItem).ToArgb()
                != extrusionFill.ToArgb()
            || extrusionSideItems.Any(item =>
                extrusionStage.GetReference3DExtrusionSurfaceColor(item).ToArgb()
                    != extrusionStroke.ToArgb()))
        {
            throw new InvalidOperationException(
                "Scale Z extrusion did not keep the fill color on its back face and the outline color on its side walls.");
        }

        var backFaceTransform = System.Numerics.Matrix4x4.CreateRotationY(MathF.PI);
        var backFaceExtrusion = System.Numerics.Vector3.Transform(
            new System.Numerics.Vector3(0, 0, 1_800),
            backFaceTransform);
        using (var backFaceStage = CreateStage(extrusionScene))
        {
            var backFaceView = new SceneDefinition { Dimension = SceneDimension.ThreeD };
            ConfigureNeutralReferenceLighting(backFaceView);
            backFaceView.Camera.Projection = CameraProjection.Perspective;
            backFaceStage.ConfigureReferenceView(backFaceView, SceneDimension.ThreeD);
            backFaceStage.ResetReferenceCameraView();
            backFaceStage.SetReferenceCameraOrientation(0.72f, -0.36f);
            backFaceStage.SetSceneCompositionResult(
                PoseResult(backFaceTransform, backFaceExtrusion),
                extrusionScene);
            var backFaceItems = backFaceStage.GetReference3DLayerRenderItems([extrusionObject]);
            var backFaceSample = FindVisibleExtrusionSurfaceSample(
                backFaceStage,
                backFaceItems,
                Reference3DRenderKind.Back);
            var backFaceSideSample = FindVisibleExtrusionSurfaceSample(
                backFaceStage,
                backFaceItems,
                Reference3DRenderKind.Side);
            using var backFaceBitmap = RenderGdi(backFaceStage);
            AssertPixelNear(
                SampleBitmap(backFaceBitmap, backFaceSample),
                extrusionFill,
                "The GDI extrusion back face used the outline color instead of the fill color",
                tolerance: 4);
            AssertPixelNear(
                SampleBitmap(backFaceBitmap, backFaceSideSample),
                extrusionStroke,
                "The GDI extrusion side wall stopped using the outline color",
                tolerance: 4);
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

        var extrusionOcclusionBoxColor = Color.FromArgb(255, 40, 142, 224);
        var extrusionOcclusionCardColor = Color.FromArgb(255, 52, 204, 116);
        var extrusionOcclusionScene = new VectorScene();
        extrusionOcclusionScene.CreateEmpty();
        var extrusionOcclusionBox = extrusionOcclusionScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(3_600, 2_200),
            angle: 0,
            stroke: 0,
            color: extrusionOcclusionBoxColor,
            strokeColor: Color.Transparent,
            atoms: 12,
            shapeKind: ShapeKind.Rectangle);
        var extrusionOcclusionCard = extrusionOcclusionScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(5_200, 3_600),
            angle: 0,
            stroke: 0,
            color: extrusionOcclusionCardColor,
            strokeColor: Color.Transparent,
            atoms: 12,
            shapeKind: ShapeKind.Rectangle);
        var extrusionOcclusionResult = new SceneCompositionResult(
            new SceneCompositionObjectOwner[2],
            [
                new SceneCompositionObjectPose(
                    System.Numerics.Matrix4x4.Identity,
                    new System.Numerics.Vector3(0, 0, 2_400)),
                new SceneCompositionObjectPose(System.Numerics.Matrix4x4.Identity)
            ]);
        var extrusionOcclusionView = new SceneDefinition { Dimension = SceneDimension.ThreeD };
        ConfigureNeutralReferenceLighting(extrusionOcclusionView);
        extrusionOcclusionView.Camera.Projection = CameraProjection.Perspective;
        using var extrusionOcclusionStage = CreateStage(extrusionOcclusionScene);
        extrusionOcclusionStage.ConfigureReferenceView(
            extrusionOcclusionView,
            SceneDimension.ThreeD);
        extrusionOcclusionStage.ResetReferenceCameraView();
        extrusionOcclusionStage.SetReferenceCameraOrientation(0.72f, -0.36f);
        extrusionOcclusionStage.SetSceneCompositionResult(
            extrusionOcclusionResult,
            extrusionOcclusionScene);

        var extrusionOcclusionSourceItems = extrusionOcclusionStage
            .GetReference3DLayerRenderItems([extrusionOcclusionBox, extrusionOcclusionCard]);
        var extrusionOcclusionBoxSurfaces = extrusionOcclusionSourceItems
            .Where(item => item.ObjectIndex == extrusionOcclusionBox
                && item.Kind is Reference3DRenderKind.Back
                    or Reference3DRenderKind.Side
                    or Reference3DRenderKind.FrontFill)
            .ToArray();
        var extrusionOcclusionCardSurface = extrusionOcclusionSourceItems.Single(item =>
            item.ObjectIndex == extrusionOcclusionCard
            && item.Kind == Reference3DRenderKind.FrontFill);
        if (extrusionOcclusionBoxSurfaces.Length < 6
            || extrusionOcclusionBoxSurfaces.Any(item =>
                !item.Plane.IsValid || !item.PlaneKey.IsValid)
            || !extrusionOcclusionCardSurface.Plane.IsValid)
        {
            throw new InvalidOperationException(
                "The perspective extrusion fixture did not expose exact back, side, and card planes.");
        }

        var extrusionOcclusionSamples =
            new List<(Point Screen, int ObjectIndex, Color ExpectedColor)>();
        var extrusionOcclusionSideSlot = -1;
        foreach (var sideItem in extrusionOcclusionBoxSurfaces.Where(item =>
                     item.Kind == Reference3DRenderKind.Side))
        {
            var boxSamples = new List<(Point Screen, int ObjectIndex, Color ExpectedColor)>();
            var cardSamples = new List<(Point Screen, int ObjectIndex, Color ExpectedColor)>();
            for (var y = 8; y < extrusionOcclusionStage.Height - 8; y += 2)
            {
                for (var x = 8; x < extrusionOcclusionStage.Width - 8; x += 2)
                {
                    var screen = new Point(x, y);
                    if (!ProjectedFillContainsMargin(sideItem.Contours, screen, 2)
                        || !ProjectedFillContainsMargin(
                            extrusionOcclusionCardSurface.Contours,
                            screen,
                            2)
                        || !extrusionOcclusionStage.TryGetReferenceRay(screen, out var ray)
                        || !TryIntersectReferenceSurface(
                            ray,
                            sideItem.Plane,
                            out var sideDistance)
                        || !TryIntersectReferenceSurface(
                            ray,
                            extrusionOcclusionCardSurface.Plane,
                            out var cardDistance)
                        || Math.Abs(sideDistance - cardDistance) < 80f
                        || extrusionOcclusionBoxSurfaces.Any(boxSurface =>
                            !ProjectedFillMembershipStable(
                                boxSurface.Contours,
                                screen,
                                2,
                                out _)))
                    {
                        continue;
                    }

                    var nearestBoxDistance = float.PositiveInfinity;
                    var nearestBoxSurface = default(Reference3DRenderItem);
                    foreach (var boxSurface in extrusionOcclusionBoxSurfaces)
                    {
                        if (!ProjectedFillContainsMargin(boxSurface.Contours, screen, 0)
                            || !TryIntersectReferenceSurface(
                                ray,
                                boxSurface.Plane,
                                out var boxDistance)
                            || boxDistance >= nearestBoxDistance)
                        {
                            continue;
                        }
                        nearestBoxDistance = boxDistance;
                        nearestBoxSurface = boxSurface;
                    }
                    if (nearestBoxSurface.Kind != Reference3DRenderKind.Side
                        || nearestBoxSurface.SurfaceSlot != sideItem.SurfaceSlot)
                    {
                        continue;
                    }

                    var destination = sideDistance < cardDistance ? boxSamples : cardSamples;
                    if (destination.Count >= 2
                        || destination.Any(sample =>
                            CrossingScreenDistance(sample.Screen, screen) < 14f))
                    {
                        continue;
                    }
                    var expectedObject = sideDistance < cardDistance
                        ? extrusionOcclusionBox
                        : extrusionOcclusionCard;
                    destination.Add((
                        screen,
                        expectedObject,
                        expectedObject == extrusionOcclusionBox
                            ? extrusionOcclusionBoxColor
                            : extrusionOcclusionCardColor));
                }
            }
            if (boxSamples.Count != 2 || cardSamples.Count != 2) continue;
            extrusionOcclusionSideSlot = sideItem.SurfaceSlot;
            extrusionOcclusionSamples.AddRange(boxSamples);
            extrusionOcclusionSamples.AddRange(cardSamples);
            break;
        }
        if (extrusionOcclusionSideSlot < 0 || extrusionOcclusionSamples.Count != 4)
        {
            throw new InvalidOperationException(
                "The perspective extrusion fixture did not expose both local depth halves on one side wall.");
        }

        var extrusionOcclusionItems = extrusionOcclusionStage.GetReference3DSceneRenderItems();
        var extrusionOcclusionSideFragments = extrusionOcclusionItems
            .Where(item => item.ObjectIndex == extrusionOcclusionBox
                && item.Kind == Reference3DRenderKind.Side
                && item.SurfaceSlot == extrusionOcclusionSideSlot)
            .ToArray();
        if (extrusionOcclusionSideFragments.Length < 2
            || extrusionOcclusionSideFragments.Any(item =>
                !item.Plane.IsValid
                || !item.PlaneKey.IsValid
                || item.FragmentClip is not { Length: > 0 }
                || item.StableFragmentIdentity == 0)
            || extrusionOcclusionSideFragments
                .Select(item => item.StableFragmentIdentity)
                .Distinct()
                .Count() < 2)
        {
            throw new InvalidOperationException(
                "A perspective extrusion side wall was not split into stable local-depth fragments.");
        }

        using (var extrusionOcclusionBitmap = RenderGdi(extrusionOcclusionStage))
        {
            foreach (var sample in extrusionOcclusionSamples)
            {
                var coveringItems = extrusionOcclusionItems.Where(item =>
                        item.Kind is Reference3DRenderKind.Back
                            or Reference3DRenderKind.Side
                            or Reference3DRenderKind.FrontFill
                        && ProjectedFillContainsMargin(item.Contours, sample.Screen, 0)
                        && (item.FragmentClip is not { Length: > 0 }
                            || ProjectedFillContainsMargin(
                                item.FragmentClip,
                                sample.Screen,
                                0)))
                    .ToArray();
                if (coveringItems.Length == 0
                    || coveringItems[^1].ObjectIndex != sample.ObjectIndex)
                {
                    throw new InvalidOperationException(
                        "The perspective extrusion render plan did not place the local foreground last: "
                        + $"screen={sample.Screen}, expected={sample.ObjectIndex}, "
                        + "order=["
                        + string.Join(",", coveringItems.Select(item =>
                            $"{item.ObjectIndex}/{item.Kind}/{item.SurfaceSlot}/{item.FragmentSlot}"))
                        + "].");
                }
                AssertPixelNear(
                    SampleBitmap(extrusionOcclusionBitmap, sample.Screen),
                    sample.ExpectedColor,
                    "A perspective extrusion side/card overlap painted the wrong foreground",
                    tolerance: 12);
                if (!extrusionOcclusionStage.TryHitTestProjectedObject(
                        sample.Screen,
                        1f,
                        out var extrusionOcclusionHit)
                    || extrusionOcclusionHit != sample.ObjectIndex)
                {
                    throw new InvalidOperationException(
                        "Perspective extrusion hit testing disagreed with the visible local-depth fragment: "
                        + $"screen={sample.Screen}, expected={sample.ObjectIndex}, hit={extrusionOcclusionHit}.");
                }
            }
        }
        Console.WriteLine("scene_reference_extrusion_occlusion=ok");
        RunDistantExtrusionOrderingRegression();
        AssertPerspectiveImportedSvgMaterial();

        const int denseIntersectionPlaneCount = 14;
        var denseIntersectionScene = new VectorScene();
        denseIntersectionScene.CreateEmpty();
        var denseIntersectionObjects = new int[denseIntersectionPlaneCount];
        var denseIntersectionTransforms = new System.Numerics.Matrix4x4[denseIntersectionPlaneCount];
        denseIntersectionTransforms[0] = System.Numerics.Matrix4x4.Identity;
        for (var planeIndex = 0; planeIndex < denseIntersectionPlaneCount; planeIndex++)
        {
            denseIntersectionObjects[planeIndex] = denseIntersectionScene.AddObject(
                0,
                PointF.Empty,
                new SizeF(4_000, 4_000),
                angle: 0,
                stroke: 24,
                color: Color.FromArgb(
                    255,
                    52 + planeIndex * 11 % 170,
                    72 + planeIndex * 17 % 160,
                    88 + planeIndex * 23 % 150),
                strokeColor: Color.White,
                atoms: 12,
                shapeKind: ShapeKind.Rectangle);
            if (planeIndex == 0) continue;
            var axisAngle = (planeIndex - 1) * MathF.PI / (denseIntersectionPlaneCount - 1);
            denseIntersectionTransforms[planeIndex] =
                System.Numerics.Matrix4x4.CreateRotationZ(-axisAngle)
                * System.Numerics.Matrix4x4.CreateRotationX(0.68f)
                * System.Numerics.Matrix4x4.CreateRotationZ(axisAngle);
        }
        using var denseIntersectionStage = CreateStage(denseIntersectionScene);
        var denseIntersectionView = new SceneDefinition { Dimension = SceneDimension.ThreeD };
        ConfigureNeutralReferenceLighting(denseIntersectionView);
        denseIntersectionView.Camera.Projection = CameraProjection.Perspective;
        denseIntersectionStage.ConfigureReferenceView(denseIntersectionView, SceneDimension.ThreeD);
        denseIntersectionStage.ResetReferenceCameraView();
        denseIntersectionStage.SetSceneCompositionResult(
            CompositionResult(denseIntersectionTransforms),
            denseIntersectionScene);
        var denseExpectedPairs = new HashSet<(int First, int Second)>();
        for (var first = 0; first < denseIntersectionObjects.Length; first++)
        {
            for (var second = first + 1; second < denseIntersectionObjects.Length; second++)
            {
                denseExpectedPairs.Add((
                    denseIntersectionObjects[first],
                    denseIntersectionObjects[second]));
            }
        }
        Dictionary<(int First, int Second), int>? denseStableSlots = null;
        var denseSharedEndpointCount = 0;
        const int denseIntersectionFrameCount = 5;
        for (var frame = 0; frame < denseIntersectionFrameCount; frame++)
        {
            var progress = frame / (denseIntersectionFrameCount - 1f);
            var yaw = -0.36f + 0.72f * progress;
            var pitch = -0.18f + 0.08f * MathF.Sin(progress * MathF.PI * 2f);
            denseIntersectionStage.SetReferenceCameraOrientation(yaw, pitch);
            var edges = denseIntersectionStage.GetReference3DSceneRenderItems()
                .Where(item => item.Kind == Reference3DRenderKind.IntersectionEdge)
                .ToArray();
            var actualPairs = edges
                .Select(item => (
                    First: Math.Min(item.ObjectIndex, item.SecondaryObjectIndex),
                    Second: Math.Max(item.ObjectIndex, item.SecondaryObjectIndex)))
                .ToHashSet();
            if (!actualPairs.SetEquals(denseExpectedPairs))
            {
                var missing = denseExpectedPairs.Except(actualPairs);
                throw new InvalidOperationException(
                    "A valid dense-plane intersection edge was dropped by the surface-fragment budget: "
                    + $"frame={frame}, yaw={yaw:0.###}, pitch={pitch:0.###}, "
                    + $"missing=[{string.Join(',', missing)}], pairs={actualPairs.Count}, "
                    + $"segments={edges.Length}.");
            }

            var currentSlots = edges
                .GroupBy(item => (
                    First: Math.Min(item.ObjectIndex, item.SecondaryObjectIndex),
                    Second: Math.Max(item.ObjectIndex, item.SecondaryObjectIndex)))
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(item => item.SurfaceSlot).Distinct().Single());
            denseStableSlots ??= currentSlots;
            if (denseStableSlots.Any(pair => currentSlots[pair.Key] != pair.Value))
            {
                throw new InvalidOperationException(
                    "Dense-plane intersection edge identities changed during the camera orbit: "
                    + $"frame={frame}, yaw={yaw:0.###}, pitch={pitch:0.###}.");
            }
            denseSharedEndpointCount += AssertIntersectionEdgeCapTopology(
                edges,
                $"dense frame={frame}, yaw={yaw:0.###}, pitch={pitch:0.###}");
        }
        if (denseSharedEndpointCount == 0)
        {
            throw new InvalidOperationException(
                "The dense-plane fixture produced no shared internal intersection-edge endpoint.");
        }

        Console.WriteLine("scene_reference_projection_2d_spatial_pose=ok");
        Console.WriteLine("scene_reference_projection_2d_gradients_and_blend=ok");
        Console.WriteLine("scene_reference_normal_and_folder_opacity=ok");
        Console.WriteLine("scene_reference_transparent_fill_occlusion=ok");
        Console.WriteLine("scene_reference_gdi_base_frame_cache=ok");
        Console.WriteLine("scene_reference_depth_order_and_hit_test=ok");
        Console.WriteLine("scene_reference_intersecting_planes=ok");
        Console.WriteLine("scene_reference_closed_bezier_intersection=ok");
        Console.WriteLine("scene_reference_stroke_occlusion_fragments=ok");
        Console.WriteLine("scene_reference_grazing_intersection_edges=ok");
        Console.WriteLine("scene_reference_thick_edge_grazing_orbit=ok");
        Console.WriteLine("scene_reference_blend_isolated_intersections=ok");
        Console.WriteLine("scene_reference_multi_plane_cycles=ok");
        Console.WriteLine("scene_reference_fragment_identity_orbit=ok");
        Console.WriteLine("scene_reference_dense_intersection_edges=ok");
        Console.WriteLine("scene_reference_intersection_edge_camera_orbits=ok");
        Console.WriteLine("scene_reference_projective_materials=ok");
        Console.WriteLine("scene_reference_non_scaling_strokes=ok");
        Console.WriteLine("scene_reference_extrusion_and_selection=ok");
        Console.WriteLine("scene_reference_render_plan_cache=ok");
        Console.WriteLine($"scene_reference_render_plan_cache_allocated_bytes={referencePlanCacheAllocatedBytes}");

        void RunClosedBezierSurfaceIntersectionRegression()
        {
            const float radiusX = 1_800f;
            const float radiusY = 1_200f;
            const float kappa = 0.5522848f;
            var handleX = radiusX * kappa;
            var handleY = radiusY * kappa;
            var ellipse = new[]
            {
                new PathBezierNode(
                    new PointF(0, -radiusY),
                    new PointF(-handleX, -radiusY),
                    new PointF(handleX, -radiusY)),
                new PathBezierNode(
                    new PointF(radiusX, 0),
                    new PointF(radiusX, -handleY),
                    new PointF(radiusX, handleY)),
                new PathBezierNode(
                    new PointF(0, radiusY),
                    new PointF(handleX, radiusY),
                    new PointF(-handleX, radiusY)),
                new PathBezierNode(
                    new PointF(-radiusX, 0),
                    new PointF(-radiusX, handleY),
                    new PointF(-radiusX, -handleY))
            };

            var scene = new VectorScene();
            scene.CreateEmpty();
            var bottomLayer = scene.AddLayer("Closed Bezier bottom");
            var firstObject = scene.AppendPathBezierObjectContours(
                0,
                [ellipse],
                0,
                crossingCardAColor,
                Color.Transparent,
                48);
            var secondObject = scene.AppendPathBezierObjectContours(
                bottomLayer,
                [ellipse],
                0,
                crossingCardBColor,
                Color.Transparent,
                48);
            scene.CompleteDeferredBuild();
            if (firstObject < 0
                || secondObject < 0
                || !scene.TryGetPathBezierWorldContours(firstObject, out var firstBezier)
                || !scene.TryGetPathBezierWorldContours(secondObject, out var secondBezier)
                || firstBezier is not [{ Length: 4 }]
                || secondBezier is not [{ Length: 4 }])
            {
                throw new InvalidOperationException(
                    "The closed-Bezier intersection fixture did not retain its exact cubic contours.");
            }

            using var stage = CreateStage(scene);
            var view = new SceneDefinition { Dimension = SceneDimension.ThreeD };
            ConfigureNeutralReferenceLighting(view);
            view.Camera.Projection = CameraProjection.Perspective;
            stage.ConfigureReferenceView(view, SceneDimension.ThreeD);
            stage.ResetReferenceCameraView();
            stage.SetSceneCompositionResult(
                CompositionResult(crossingCardATransform, crossingCardBTransform),
                scene);

            for (var warmup = 0; warmup < 3; warmup++)
            {
                stage.SetReferenceCameraOrientation(0.40f + warmup * 0.003f, -0.22f);
                _ = stage.GetReference3DSceneRenderItems();
            }

            const int samples = 9;
            const long allocatedByteBudget = 800_000;
            var elapsedTicks = new long[samples];
            var allocatedBytes = new long[samples];
            var measuredPlanBuildsBefore = stage.Reference3DRenderPlanBuildCount;
            for (var sample = 0; sample < samples; sample++)
            {
                stage.SetReferenceCameraOrientation(
                    0.43f + sample * 0.0017f,
                    -0.24f + sample * 0.0009f);
                var planBuildsBefore = stage.Reference3DRenderPlanBuildCount;
                var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                var started = Stopwatch.GetTimestamp();
                _ = stage.GetReference3DSceneRenderItems();
                elapsedTicks[sample] = Stopwatch.GetTimestamp() - started;
                allocatedBytes[sample] = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                if (stage.Reference3DRenderPlanBuildCount != planBuildsBefore + 1)
                {
                    throw new InvalidOperationException(
                        "A closed-Bezier performance sample reused a warm render plan: "
                        + $"sample={sample}, builds={stage.Reference3DRenderPlanBuildCount - planBuildsBefore}.");
                }
            }
            if (stage.Reference3DRenderPlanBuildCount - measuredPlanBuildsBefore != samples)
            {
                throw new InvalidOperationException(
                    "The closed-Bezier performance fixture did not measure nine cold render plans.");
            }
            Array.Sort(elapsedTicks);
            Array.Sort(allocatedBytes);
            var medianMilliseconds = elapsedTicks[samples / 2] * 1_000d / Stopwatch.Frequency;
            var medianAllocatedBytes = allocatedBytes[samples / 2];
            var maximumAllocatedBytes = allocatedBytes[^1];
            var medianTimeBudgetMet = medianMilliseconds <= RenderCollectBudgetMilliseconds;

            using var extrudedCurveStage = CreateStage(scene);
            extrudedCurveStage.ConfigureReferenceView(view, SceneDimension.ThreeD);
            extrudedCurveStage.ResetReferenceCameraView();
            var firstExtrusion = System.Numerics.Vector3.TransformNormal(
                System.Numerics.Vector3.UnitZ,
                crossingCardATransform) * 1_600f;
            var secondExtrusion = System.Numerics.Vector3.TransformNormal(
                System.Numerics.Vector3.UnitZ,
                crossingCardBTransform) * 1_600f;
            extrudedCurveStage.SetSceneCompositionResult(
                new SceneCompositionResult(
                    new SceneCompositionObjectOwner[2],
                    [
                        new SceneCompositionObjectPose(crossingCardATransform, firstExtrusion),
                        new SceneCompositionObjectPose(crossingCardBTransform, secondExtrusion)
                    ]),
                scene);
            extrudedCurveStage.SetReferenceCameraOrientation(0.41f, -0.23f);
            _ = extrudedCurveStage.GetReference3DSceneRenderItems();
            extrudedCurveStage.SetReferenceCameraOrientation(0.43f, -0.24f);
            var extrudedAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var extrudedStarted = Stopwatch.GetTimestamp();
            var extrudedItems = extrudedCurveStage.GetReference3DSceneRenderItems();
            var extrudedElapsedMilliseconds = (Stopwatch.GetTimestamp() - extrudedStarted)
                * 1_000d / Stopwatch.Frequency;
            var extrudedAllocatedBytes = GC.GetAllocatedBytesForCurrentThread()
                - extrudedAllocatedBefore;
            const double extrudedCpuBudgetMilliseconds = 250d;
            const long extrudedAllocatedByteBudget = 32_000_000;
            Console.WriteLine(
                "scene_reference_extruded_bezier_intersection_metrics="
                + $"items={extrudedItems.Length},elapsed_ms={extrudedElapsedMilliseconds:0.000},"
                + $"cpu_budget_ms={extrudedCpuBudgetMilliseconds:0.000},"
                + $"allocated_bytes={extrudedAllocatedBytes},"
                + $"allocated_budget_bytes={extrudedAllocatedByteBudget}");
            Console.WriteLine(
                "scene_reference_extruded_bezier_intersection_cpu_budget_met="
                + (extrudedElapsedMilliseconds <= extrudedCpuBudgetMilliseconds)
                    .ToString()
                    .ToLowerInvariant());
            Console.WriteLine(
                "scene_reference_extruded_bezier_intersection_allocation_budget_met="
                + (extrudedAllocatedBytes <= extrudedAllocatedByteBudget)
                    .ToString()
                    .ToLowerInvariant());

            stage.SetReferenceCameraOrientation(0.43f, -0.24f);
            var projectedFirst = stage.GetReference3DProjectedContours(firstObject);
            var projectedSecond = stage.GetReference3DProjectedContours(secondObject);
            var items = stage.GetReference3DSceneRenderItems();
            var firstFragments = items.Where(item => item.ObjectIndex == firstObject
                && item.Kind == Reference3DRenderKind.FrontFill
                && item.FragmentClip is { Length: > 0 }).ToArray();
            var secondFragments = items.Where(item => item.ObjectIndex == secondObject
                && item.Kind == Reference3DRenderKind.FrontFill
                && item.FragmentClip is { Length: > 0 }).ToArray();
            var intersectionEdges = items.Where(item =>
                item.Kind == Reference3DRenderKind.IntersectionEdge
                && Math.Min(item.ObjectIndex, item.SecondaryObjectIndex) == firstObject
                && Math.Max(item.ObjectIndex, item.SecondaryObjectIndex) == secondObject).ToArray();
            var fragments = firstFragments.Concat(secondFragments).ToArray();
            var fragmentGeometryValid = fragments.All(item =>
                item.StableFragmentIdentity != 0
                && item.FragmentClip is { Length: > 0 } clip
                && clip.All(contour =>
                    contour.Closed
                    && contour.Points.Length >= 3
                    && contour.Points.All(point =>
                        float.IsFinite(point.X) && float.IsFinite(point.Y))));
            var fragmentIdentitiesValid = firstFragments
                    .Select(item => item.StableFragmentIdentity)
                    .Distinct()
                    .Count() == 2
                && secondFragments
                    .Select(item => item.StableFragmentIdentity)
                    .Distinct()
                    .Count() == 2;
            var edgeGeometryValid = intersectionEdges is [{ } edge]
                && float.IsFinite(edge.AverageDepth)
                && float.IsFinite(edge.EdgeWidth)
                && edge.EdgeWidth > 0
                && edge.Contours is [{ Closed: false } edgeContour]
                && edgeContour.Points.Length >= 2
                && edgeContour.Points.All(point =>
                    float.IsFinite(point.X) && float.IsFinite(point.Y));
            if (projectedFirst is not [{ Closed: true }]
                || projectedSecond is not [{ Closed: true }]
                || projectedFirst[0].Points.Length < 16
                || projectedSecond[0].Points.Length != projectedFirst[0].Points.Length
                || projectedFirst[0].Points.Any(point =>
                    !float.IsFinite(point.X) || !float.IsFinite(point.Y))
                || projectedSecond[0].Points.Any(point =>
                    !float.IsFinite(point.X) || !float.IsFinite(point.Y))
                || firstFragments.Length != 2
                || secondFragments.Length != 2
                || !fragmentGeometryValid
                || !fragmentIdentitiesValid
                || !edgeGeometryValid)
            {
                throw new InvalidOperationException(
                    "Closed-Bezier surface intersection topology was incomplete: "
                    + $"points={projectedFirst.FirstOrDefault().Points?.Length ?? 0}, "
                    + $"fragments={firstFragments.Length}/{secondFragments.Length}, "
                    + $"edges={intersectionEdges.Length}, fragmentGeometry={fragmentGeometryValid}, "
                    + $"fragmentIdentities={fragmentIdentitiesValid}, edgeGeometry={edgeGeometryValid}.");
            }

            var visualSamples = new List<(Point Screen, int ObjectIndex, Color Color)>();
            for (var y = 16; y < stage.Height - 16; y += 6)
            {
                for (var x = 16; x < stage.Width - 16; x += 6)
                {
                    var screen = new Point(x, y);
                    if (!ProjectedFillContainsMargin(projectedFirst, screen, 6)
                        || !ProjectedFillContainsMargin(projectedSecond, screen, 6)
                        || !stage.TryGetReferenceRay(screen, out var ray)
                        || !TryIntersectCardPlane(ray, crossingCardATransform, out var firstDepth, out _)
                        || !TryIntersectCardPlane(ray, crossingCardBTransform, out var secondDepth, out _)
                        || Math.Abs(firstDepth - secondDepth) < 120f)
                    {
                        continue;
                    }

                    var expectedObject = firstDepth < secondDepth ? firstObject : secondObject;
                    if (visualSamples.Count(candidate => candidate.ObjectIndex == expectedObject) >= 2
                        || visualSamples.Any(candidate =>
                            candidate.ObjectIndex == expectedObject
                            && CrossingScreenDistance(candidate.Screen, screen) < 18f))
                    {
                        continue;
                    }
                    visualSamples.Add((
                        screen,
                        expectedObject,
                        expectedObject == firstObject ? crossingCardAColor : crossingCardBColor));
                }
            }
            if (visualSamples.Count(sample => sample.ObjectIndex == firstObject) != 2
                || visualSamples.Count(sample => sample.ObjectIndex == secondObject) != 2)
            {
                throw new InvalidOperationException(
                    "The closed-Bezier intersection fixture did not expose both local depth halves.");
            }

            using var bitmap = RenderGdi(stage);
            foreach (var sample in visualSamples)
            {
                AssertPixelNear(
                    SampleBitmap(bitmap, sample.Screen),
                    sample.Color,
                    "A closed-Bezier intersection painted the wrong foreground surface",
                    tolerance: 12);
                if (!stage.TryHitTestProjectedObject(sample.Screen, 1f, out var hit)
                    || hit != sample.ObjectIndex)
                {
                    throw new InvalidOperationException(
                        "Closed-Bezier hit testing disagreed with the visible foreground surface: "
                        + $"point={sample.Screen}, expected={sample.ObjectIndex}, hit={hit}.");
                }
            }

            closedBezierIntersectionScene = scene;
            closedBezierDirect2DSamples.Clear();
            closedBezierDirect2DSamples.AddRange(visualSamples.Select(sample => (
                sample.Screen,
                sample.Color)));

            Console.WriteLine(
                "scene_reference_closed_bezier_intersection_metrics="
                + $"points={projectedFirst[0].Points.Length},"
                + $"fragments={fragments.Length},edges={intersectionEdges.Length},"
                + $"median_ms={medianMilliseconds:0.000},"
                + $"budget_ms={RenderCollectBudgetMilliseconds:0.000},"
                + $"median_allocated_bytes={medianAllocatedBytes},"
                + $"max_allocated_bytes={maximumAllocatedBytes},"
                + $"budget_bytes={allocatedByteBudget},"
                + $"bytes_per_projected_point={maximumAllocatedBytes / (projectedFirst[0].Points.Length * 2d):0.0}");
            Console.WriteLine(
                "scene_reference_closed_bezier_intersection_cpu_budget_met="
                + medianTimeBudgetMet.ToString().ToLowerInvariant());
            Console.WriteLine(
                "scene_reference_closed_bezier_intersection_budget_met="
                + (maximumAllocatedBytes <= allocatedByteBudget).ToString().ToLowerInvariant());
        }

        void RunDisconnectedOverlapGapRegression()
        {
            const float islandCenterX = 1_300f;
            const float islandHalfWidth = 420f;
            const float islandHalfHeight = 460f;
            const float planeTilt = 0.62f;
            var firstColor = Color.FromArgb(255, 226, 68, 74);
            var secondColor = Color.FromArgb(255, 42, 204, 116);
            var islandContours = new[]
            {
                new[]
                {
                    new PointF(-islandCenterX - islandHalfWidth, -islandHalfHeight),
                    new PointF(-islandCenterX + islandHalfWidth, -islandHalfHeight),
                    new PointF(-islandCenterX + islandHalfWidth, islandHalfHeight),
                    new PointF(-islandCenterX - islandHalfWidth, islandHalfHeight)
                },
                new[]
                {
                    new PointF(islandCenterX - islandHalfWidth, -islandHalfHeight),
                    new PointF(islandCenterX + islandHalfWidth, -islandHalfHeight),
                    new PointF(islandCenterX + islandHalfWidth, islandHalfHeight),
                    new PointF(islandCenterX - islandHalfWidth, islandHalfHeight)
                }
            };

            var scene = new VectorScene();
            scene.CreateEmpty();
            var bottomLayer = scene.AddLayer("Disconnected overlap bottom");
            var firstObject = scene.AppendPathObjectContours(
                0,
                islandContours,
                0,
                firstColor,
                Color.Transparent,
                16);
            var secondObject = scene.AppendPathObjectContours(
                bottomLayer,
                islandContours,
                0,
                secondColor,
                Color.Transparent,
                16);
            scene.CompleteDeferredBuild();
            if (firstObject < 0
                || secondObject < 0
                || !scene.TryGetPathWorldContours(firstObject, out var firstWorldContours)
                || !scene.TryGetPathWorldContours(secondObject, out var secondWorldContours)
                || firstWorldContours.Length != 2
                || secondWorldContours.Length != 2)
            {
                throw new InvalidOperationException(
                    "The disconnected-overlap fixture did not retain two filled islands per object.");
            }

            var firstTransform = System.Numerics.Matrix4x4.CreateRotationY(planeTilt);
            var secondTransform = System.Numerics.Matrix4x4.CreateRotationY(-planeTilt);
            using var stage = CreateStage(scene);
            var view = new SceneDefinition { Dimension = SceneDimension.ThreeD };
            ConfigureNeutralReferenceLighting(view);
            view.Camera.Projection = CameraProjection.Perspective;
            stage.ConfigureReferenceView(view, SceneDimension.ThreeD);
            stage.ResetReferenceCameraView();
            stage.SetReferenceCameraOrientation(0, 0);
            stage.SetSceneCompositionResult(
                CompositionResult(firstTransform, secondTransform),
                scene);

            var firstProjected = stage.GetReference3DProjectedContours(firstObject);
            var secondProjected = stage.GetReference3DProjectedContours(secondObject);
            if (stage.EffectiveReferenceProjection != CameraProjection.Perspective
                || firstProjected.Length != 2
                || secondProjected.Length != 2
                || !stage.TryProjectScenePosition(
                    System.Numerics.Vector3.Zero,
                    out var projectedGap,
                    out _))
            {
                throw new InvalidOperationException(
                    "The disconnected-overlap fixture did not project both islands and their central gap.");
            }

            var gapScreen = Point.Round(projectedGap);
            var firstGapDepth = 0f;
            var secondGapDepth = 0f;
            if (ProjectedFillContainsMargin(firstProjected, gapScreen, 0)
                || ProjectedFillContainsMargin(secondProjected, gapScreen, 0)
                || !stage.TryGetReferenceRay(gapScreen, out var gapRay)
                || !TryIntersectCardPlane(gapRay, firstTransform, out firstGapDepth, out _)
                || !TryIntersectCardPlane(gapRay, secondTransform, out secondGapDepth, out _)
                || Math.Abs(firstGapDepth - secondGapDepth) > 1f)
            {
                throw new InvalidOperationException(
                    "The projected plane-equality line did not remain inside the empty gap: "
                    + $"screen={gapScreen}, depths={firstGapDepth:0.###}/{secondGapDepth:0.###}.");
            }

            var samples = new List<(Point Screen, int ExpectedObject, Color ExpectedColor)>();
            for (var y = 12; y < stage.Height - 12 && samples.Count < 2; y += 3)
            {
                for (var x = 12; x < stage.Width - 12 && samples.Count < 2; x += 3)
                {
                    var screen = new Point(x, y);
                    if (Math.Abs(screen.X - gapScreen.X) < 12
                        || !ProjectedFillContainsMargin(firstProjected, screen, 6)
                        || !ProjectedFillContainsMargin(secondProjected, screen, 6)
                        || !stage.TryGetReferenceRay(screen, out var ray)
                        || !TryIntersectCardPlane(ray, firstTransform, out var firstDepth, out _)
                        || !TryIntersectCardPlane(ray, secondTransform, out var secondDepth, out _)
                        || Math.Abs(firstDepth - secondDepth) < 80f)
                    {
                        continue;
                    }

                    var firstIsFront = firstDepth < secondDepth;
                    var expectedObject = firstIsFront ? firstObject : secondObject;
                    if (samples.Any(sample => sample.ExpectedObject == expectedObject)) continue;
                    samples.Add((
                        screen,
                        expectedObject,
                        firstIsFront ? firstColor : secondColor));
                }
            }
            if (samples.Select(sample => sample.ExpectedObject).Distinct().Count() != 2
                || !samples.Any(sample => sample.Screen.X < gapScreen.X - 8)
                || !samples.Any(sample => sample.Screen.X > gapScreen.X + 8))
            {
                throw new InvalidOperationException(
                    "The perspective disconnected-overlap islands did not expose opposite local depth orders.");
            }

            var items = stage.GetReference3DSceneRenderItems();
            if (items.Any(item => item.Kind == Reference3DRenderKind.IntersectionEdge
                    && Math.Min(item.ObjectIndex, item.SecondaryObjectIndex)
                        == Math.Min(firstObject, secondObject)
                    && Math.Max(item.ObjectIndex, item.SecondaryObjectIndex)
                        == Math.Max(firstObject, secondObject)))
            {
                throw new InvalidOperationException(
                    "An equality line crossing only the empty gap generated a visible IntersectionEdge.");
            }

            var separatedByCut = false;
            foreach (var objectIndex in new[] { firstObject, secondObject })
            {
                var identities = new ulong[samples.Count];
                for (var sampleIndex = 0; sampleIndex < samples.Count; sampleIndex++)
                {
                    var sample = samples[sampleIndex];
                    var covering = items.Where(item => item.ObjectIndex == objectIndex
                            && item.Kind == Reference3DRenderKind.FrontFill
                            && ProjectedFillContainsMargin(item.Contours, sample.Screen, 0)
                            && (item.FragmentClip is not { Length: > 0 }
                                || ProjectedFillContainsMargin(
                                    item.FragmentClip,
                                    sample.Screen,
                                    0)))
                        .ToArray();
                    if (covering.Length != 1)
                    {
                        throw new InvalidOperationException(
                            "A disconnected-overlap sample did not map to exactly one fill fragment: "
                            + $"object={objectIndex}, screen={sample.Screen}, count={covering.Length}.");
                    }
                    identities[sampleIndex] = covering[0].StableFragmentIdentity;
                }
                separatedByCut |= identities.All(identity => identity != 0)
                    && identities.Distinct().Count() == samples.Count;
            }
            if (!separatedByCut)
            {
                throw new InvalidOperationException(
                    "The disconnected overlap islands were not separated by stable equality-line fragments.");
            }

            using var bitmap = RenderGdi(stage);
            foreach (var sample in samples)
            {
                AssertPixelNear(
                    SampleBitmap(bitmap, sample.Screen),
                    sample.ExpectedColor,
                    "A disconnected overlap island painted the wrong local foreground",
                    tolerance: 12);
                if (!stage.TryHitTestProjectedObject(
                        sample.Screen,
                        1f,
                        out var hit)
                    || hit != sample.ExpectedObject)
                {
                    throw new InvalidOperationException(
                        "Disconnected-overlap hit testing disagreed with the visible island: "
                        + $"screen={sample.Screen}, expected={sample.ExpectedObject}, hit={hit}.");
                }
            }
            Console.WriteLine("scene_reference_disconnected_overlap_gap=ok");
        }

        void RunDistantExtrusionOrderingRegression()
        {
            var fillColor = Color.FromArgb(255, 52, 132, 218);
            var extrusionColor = Color.FromArgb(255, 214, 66, 152);
            var solidSize = new SizeF(2_800, 1_800);
            var extrusionVector = new System.Numerics.Vector3(0, 0, 1_600);

            var baselineScene = new VectorScene();
            baselineScene.CreateEmpty();
            var baselineObject = AddSolid(baselineScene, PointF.Empty);
            using var baselineStage = CreateStage(baselineScene);
            var view = new SceneDefinition { Dimension = SceneDimension.ThreeD };
            ConfigureNeutralReferenceLighting(view);
            view.Camera.Projection = CameraProjection.Perspective;
            ConfigureStage(baselineStage, view);
            baselineStage.SetSceneCompositionResult(
                new SceneCompositionResult(
                    new SceneCompositionObjectOwner[1],
                    [new SceneCompositionObjectPose(
                        System.Numerics.Matrix4x4.Identity,
                        extrusionVector)]),
                baselineScene);
            var baselineItems = baselineStage.GetReference3DSceneRenderItems();
            var baselineOrder = SurfaceOrder(baselineItems, baselineObject);
            if (!baselineOrder.Any(entry => entry.Kind == Reference3DRenderKind.Back)
                || !baselineOrder.Any(entry => entry.Kind == Reference3DRenderKind.Side)
                || !baselineOrder.Any(entry => entry.Kind == Reference3DRenderKind.FrontFill))
            {
                throw new InvalidOperationException(
                    "The distant-extrusion baseline did not expose Back, Side, and FrontFill surfaces.");
            }

            using var baselineBitmap = RenderGdi(baselineStage);
            var samples = new List<(Point Screen, Color Color)>();
            for (var y = 12; y < baselineStage.Height - 12 && samples.Count < 4; y += 6)
            {
                for (var x = 12; x < baselineStage.Width - 12 && samples.Count < 4; x += 6)
                {
                    var screen = new Point(x, y);
                    var color = SampleBitmap(baselineBitmap, screen);
                    if (PixelRgbNear(color, background, 8)
                        || !baselineStage.TryHitTestProjectedObject(screen, 1f, out var hit)
                        || hit != baselineObject
                        || samples.Any(sample =>
                            CrossingScreenDistance(sample.Screen, screen) < 28f))
                    {
                        continue;
                    }
                    samples.Add((screen, color));
                }
            }
            if (samples.Count < 3)
            {
                throw new InvalidOperationException(
                    "The distant-extrusion baseline exposed too few stable foreground samples.");
            }

            var expandedScene = new VectorScene();
            expandedScene.CreateEmpty();
            var expandedOriginal = AddSolid(expandedScene, PointF.Empty);
            var distantObject = AddSolid(expandedScene, new PointF(10_000, 0));
            using var expandedStage = CreateStage(expandedScene);
            ConfigureStage(expandedStage, view);
            expandedStage.SetSceneCompositionResult(
                new SceneCompositionResult(
                    new SceneCompositionObjectOwner[2],
                    [
                        new SceneCompositionObjectPose(
                            System.Numerics.Matrix4x4.Identity,
                            extrusionVector),
                        new SceneCompositionObjectPose(
                            System.Numerics.Matrix4x4.Identity,
                            extrusionVector)
                    ]),
                expandedScene);
            var expandedItems = expandedStage.GetReference3DSceneRenderItems();
            var expandedOrder = SurfaceOrder(expandedItems, expandedOriginal);
            var distantOrder = SurfaceOrder(expandedItems, distantObject);
            if (distantOrder.Length == 0
                || !TryGetBounds(
                    expandedItems.Where(item => item.ObjectIndex == expandedOriginal),
                    out var originalBounds)
                || !TryGetBounds(
                    expandedItems.Where(item => item.ObjectIndex == distantObject),
                    out var distantBounds)
                || RectangleF.Intersect(originalBounds, distantBounds) is { Width: > 0, Height: > 0 })
            {
                throw new InvalidOperationException(
                    "The distant-extrusion fixture did not retain two non-overlapping projected solids.");
            }
            if (!expandedOrder.SequenceEqual(baselineOrder))
            {
                throw new InvalidOperationException(
                    "Adding a distant extrusion changed the original solid's Back/Side/Front order: "
                    + $"baseline=[{FormatOrder(baselineOrder)}], "
                    + $"expanded=[{FormatOrder(expandedOrder)}].");
            }

            using var expandedBitmap = RenderGdi(expandedStage);
            foreach (var sample in samples)
            {
                AssertPixelNear(
                    SampleBitmap(expandedBitmap, sample.Screen),
                    sample.Color,
                    "A distant non-overlapping extrusion changed the original solid foreground",
                    tolerance: 8);
                if (!expandedStage.TryHitTestProjectedObject(
                        sample.Screen,
                        1f,
                        out var hit)
                    || hit != expandedOriginal)
                {
                    throw new InvalidOperationException(
                        "A distant extrusion changed the original solid's projected hit: "
                        + $"screen={sample.Screen}, expected={expandedOriginal}, hit={hit}.");
                }
            }
            Console.WriteLine("scene_reference_distant_extrusion_order_stability=ok");

            int AddSolid(VectorScene target, PointF center)
            {
                return target.AddObject(
                    0,
                    center,
                    solidSize,
                    angle: 0,
                    stroke: 84,
                    color: fillColor,
                    strokeColor: extrusionColor,
                    atoms: 24,
                    shapeKind: ShapeKind.Rectangle);
            }

            static void ConfigureStage(StageControl target, SceneDefinition targetView)
            {
                target.ConfigureReferenceView(targetView, SceneDimension.ThreeD);
                if (target.EffectiveReferenceProjection != CameraProjection.Perspective)
                {
                    throw new InvalidOperationException(
                        "The distant-extrusion occlusion regression did not enter perspective projection.");
                }
                target.ResetReferenceCameraView();
                target.SetReferenceCameraOrientation(0.68f, -0.34f);
            }

            static (
                Reference3DRenderKind Kind,
                int SurfaceSlot,
                ulong StableFragmentIdentity,
                int FragmentSlot)[] SurfaceOrder(
                    IEnumerable<Reference3DRenderItem> source,
                    int objectIndex)
            {
                return source.Where(item => item.ObjectIndex == objectIndex
                        && item.Kind is Reference3DRenderKind.Back
                            or Reference3DRenderKind.Side
                            or Reference3DRenderKind.FrontFill)
                    .Select(item => (
                        item.Kind,
                        item.SurfaceSlot,
                        item.StableFragmentIdentity,
                        item.FragmentSlot))
                    .ToArray();
            }

            static bool TryGetBounds(
                IEnumerable<Reference3DRenderItem> source,
                out RectangleF bounds)
            {
                var points = source.SelectMany(item => item.Contours)
                    .SelectMany(contour => contour.Points)
                    .Where(point => float.IsFinite(point.X) && float.IsFinite(point.Y))
                    .ToArray();
                if (points.Length == 0)
                {
                    bounds = RectangleF.Empty;
                    return false;
                }
                bounds = RectangleF.FromLTRB(
                    points.Min(point => point.X),
                    points.Min(point => point.Y),
                    points.Max(point => point.X),
                    points.Max(point => point.Y));
                return bounds.Width > 0 && bounds.Height > 0;
            }

            static string FormatOrder((
                Reference3DRenderKind Kind,
                int SurfaceSlot,
                ulong StableFragmentIdentity,
                int FragmentSlot)[] order)
            {
                return string.Join(',', order.Select(entry =>
                    $"{entry.Kind}/{entry.SurfaceSlot}/{entry.StableFragmentIdentity}/{entry.FragmentSlot}"));
            }
        }

        void AssertCrossingCardOpacity(bool useParentFolder)
        {
            var opacityScene = new VectorScene();
            opacityScene.RestoreSnapshot(crossingScene.CreateSnapshot());
            var sourceLayer = (int)opacityScene.ObjectLayer[crossingCardA];
            var opacityLayer = sourceLayer;
            if (useParentFolder)
            {
                opacityScene.ActiveLayer = sourceLayer;
                opacityLayer = opacityScene.AddFolderLayer("Crossing opacity folder");
            }
            opacityScene.LayerOpacity[opacityLayer] = 0.5f;

            using var opacityStage = CreateStage(opacityScene);
            var opacityView = new SceneDefinition { Dimension = SceneDimension.ThreeD };
            ConfigureNeutralReferenceLighting(opacityView);
            opacityView.Camera.Projection = CameraProjection.Perspective;
            opacityStage.ConfigureReferenceView(opacityView, SceneDimension.ThreeD);
            opacityStage.ResetReferenceCameraView();
            opacityStage.SetReferenceCameraOrientation(0.43f, -0.24f);
            opacityStage.SetSceneCompositionResult(
                CompositionResult(crossingCardATransform, crossingCardBTransform),
                opacityScene);

            var opacityItems = opacityStage.GetReference3DSceneRenderItems();
            var crossingEdges = opacityItems.Count(item =>
                item.Kind == Reference3DRenderKind.IntersectionEdge
                && item.ObjectIndex is 0 or 1
                && item.SecondaryObjectIndex is 0 or 1);
            if (opacityScene.HasNonNormalLayerBlendModes
                || crossingEdges == 0
                || new[] { crossingCardA, crossingCardB }.Any(objectIndex =>
                    opacityItems.Count(item => item.ObjectIndex == objectIndex
                        && item.Kind == Reference3DRenderKind.FrontFill
                        && item.FragmentClip is { Length: > 0 }) < 2)
                || opacityItems.Any(item => item.ObjectIndex == crossingCardA
                    && item.Kind != Reference3DRenderKind.IntersectionEdge
                    && Math.Abs(item.MaterialOpacity - 0.5f) > 0.000001f)
                || opacityItems.Any(item => item.ObjectIndex == crossingCardB
                    && item.Kind != Reference3DRenderKind.IntersectionEdge
                    && Math.Abs(item.MaterialOpacity - 1f) > 0.000001f))
            {
                throw new InvalidOperationException(
                    "Normal layer or folder opacity broke the global crossing-surface render plan.");
            }

            var transparentFrontColor = LayerBlendCompositor.CompositeColorForRegression(
                crossingCardBColor,
                crossingCardAColor,
                LayerBlendMode.Normal,
                opacity: 0.5f);
            using var opacityBitmap = RenderGdi(opacityStage);
            foreach (var sample in crossingDirect2DSamples)
            {
                var expected = sample.ObjectIndex == crossingCardA
                    ? transparentFrontColor
                    : crossingCardBColor;
                AssertPixelNear(
                    SampleBitmap(opacityBitmap, sample.Screen),
                    expected,
                    useParentFolder
                        ? "Parent-folder opacity broke crossing-surface depth"
                        : "Normal layer opacity broke crossing-surface depth",
                    tolerance: 18);
                if (!opacityStage.TryHitTestProjectedObject(
                        sample.Screen,
                        1f,
                        out var hit)
                    || hit != sample.ObjectIndex)
                {
                    throw new InvalidOperationException(
                        "Normal opacity changed the foreground object selected by spatial hit testing.");
                }
            }
        }

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
                    SampleBitmap(bitmap, sample.Screen),
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
                crossingOccludedStrokeSamples.Clear();
                crossingOccludedStrokeSamples.AddRange(FindOccludedCrossingCardStrokeSamples());
                foreach (var sample in crossingOccludedStrokeSamples)
                {
                    var actual = SampleBitmap(bitmap, sample.Screen);
                    if (!PixelRgbNear(actual, sample.ExpectedColor, 20))
                    {
                        var diagnosticItems = crossingStage.GetReference3DSceneRenderItems()
                            .Where(item => item.ObjectIndex == sample.SourceObject
                                || item.ObjectIndex == sample.ObjectIndex)
                            .Select((item, index) =>
                                $"{index}:{item.ObjectIndex}/{item.Kind}/fragment={item.FragmentSlot}"
                                + $"/depth={item.AverageDepth:0.###}"
                                + $"/clip={(item.FragmentClip is not { Length: > 0 } ? "none" : ProjectedFillContainsMargin(item.FragmentClip, sample.Screen, 0))}"
                                + $"/surface={ProjectedFillContainsMargin(item.Contours, sample.Screen, 0)}");
                        throw new InvalidOperationException(
                            "A foreground crossing card did not hide the rear card boundary: "
                            + $"screen={sample.Screen}, source={sample.SourceObject}/{sample.SourcePoint}, "
                            + $"foreground={sample.ObjectIndex}, gap={sample.DepthGap:0.###}, "
                            + $"actual={actual.ToArgb():X8}, expected={sample.ExpectedColor.ToArgb():X8}, "
                            + $"items=[{string.Join(',', diagnosticItems)}].");
                    }
                    AssertPixelNear(
                        actual,
                        sample.ExpectedColor,
                        "A foreground crossing card did not hide the rear card boundary: "
                        + $"screen={sample.Screen}, source={sample.SourceObject}/{sample.SourcePoint}, "
                        + $"foreground={sample.ObjectIndex}, gap={sample.DepthGap:0.###}",
                        tolerance: 20);
                }
                var expectedBoundary = new PointF(crossingCardSize.Width * 0.5f, -450f);
                if (!crossingOccludedStrokeSamples.Any(sample =>
                        sample.SourceObject == crossingCardB
                        && CrossingPointDistance(sample.SourcePoint, expectedBoundary) <= 0.001f))
                {
                    throw new InvalidOperationException(
                        "The oblique crossing-card regression did not retain its known occluded side boundary.");
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

        void AssertCrossingCardRotationTrajectory()
        {
            var sampledViews = 0;
            var sampledBoundaries = 0;
            foreach (var yaw in new[] { -1.2f, -0.8f, -0.4f, 0f, 0.4f, 0.8f, 1.2f })
            {
                foreach (var pitch in new[] { -0.9f, -0.6f, -0.3f, 0f, 0.3f, 0.6f, 0.9f })
                {
                    crossingStage.SetReferenceCameraOrientation(yaw, pitch);
                    var samples = FindOccludedCrossingCardStrokeSamples(requireSample: false);
                    if (samples.Count == 0) continue;
                    sampledViews++;
                    sampledBoundaries += samples.Count;
                    using var bitmap = RenderGdi(crossingStage);
                    foreach (var sample in samples)
                    {
                        var actual = SampleBitmap(bitmap, sample.Screen);
                        if (!PixelRgbNear(actual, sample.ExpectedColor, 20))
                        {
                            var diagnosticItems = crossingStage.GetReference3DSceneRenderItems()
                                .Where(item => item.ObjectIndex == sample.SourceObject
                                    || item.ObjectIndex == sample.ObjectIndex)
                                .Select((item, index) =>
                                    $"{index}:{item.ObjectIndex}/{item.Kind}/fragment={item.FragmentSlot}"
                                    + $"/depth={item.AverageDepth:0.###}"
                                    + $"/clip={(item.FragmentClip is not { Length: > 0 } ? "none" : ProjectedFillContainsMargin(item.FragmentClip, sample.Screen, 0))}"
                                    + $"/occlusion={(item.OcclusionContours is not { Length: > 0 } ? "none" : ProjectedFillContainsMargin(item.OcclusionContours, sample.Screen, 0))}"
                                    + $"/surface={ProjectedFillContainsMargin(item.Contours, sample.Screen, 0)}");
                            throw new InvalidOperationException(
                                "A rear card boundary leaked while rotating the camera: "
                                + $"yaw={yaw:0.###}, pitch={pitch:0.###}, screen={sample.Screen}, "
                                + $"source={sample.SourceObject}/{sample.SourcePoint}, foreground={sample.ObjectIndex}, "
                                + $"gap={sample.DepthGap:0.###}, actual={actual.ToArgb():X8}, "
                                + $"expected={sample.ExpectedColor.ToArgb():X8}, "
                                + $"items=[{string.Join(',', diagnosticItems)}].");
                        }
                        AssertPixelNear(
                            actual,
                            sample.ExpectedColor,
                            $"A rear card boundary leaked while rotating the camera to yaw={yaw:0.###}, pitch={pitch:0.###}",
                            tolerance: 20);
                        if (!crossingStage.TryHitTestProjectedObject(
                                sample.Screen,
                                1f,
                                out var hit)
                            || hit != sample.ObjectIndex)
                        {
                            throw new InvalidOperationException(
                                "Crossing-card camera-rotation hit testing exposed a rear boundary: "
                                + $"yaw={yaw:0.###}, pitch={pitch:0.###}, point={sample.Screen}, "
                                + $"expected={sample.ObjectIndex}, hit={hit}.");
                        }
                    }
                    if (yaw is -0.8f or 0f or 0.8f
                        && !crossingRotationDirect2DSamples.Any(sample => sample.Yaw == yaw))
                    {
                        var retained = samples[0];
                        crossingRotationDirect2DSamples.Add((
                            yaw,
                            pitch,
                            retained.Screen,
                            retained.ExpectedColor));
                    }
                }
            }
            crossingStage.SetReferenceCameraOrientation(0.43f, -0.24f);
            if (sampledViews < 12
                || sampledBoundaries < 18
                || crossingRotationDirect2DSamples.Count != 3)
            {
                throw new InvalidOperationException(
                    "The crossing-card camera trajectory did not retain enough occluded boundaries: "
                    + $"views={sampledViews}, samples={sampledBoundaries}, "
                    + $"direct2D={crossingRotationDirect2DSamples.Count}.");
            }
        }

        void AssertCrossingIntersectionEdgeOrbit()
        {
            const int frameCount = 49;
            for (var frame = 0; frame < frameCount; frame++)
            {
                var progress = frame / (frameCount - 1f);
                var yaw = -0.72f + 1.44f * progress;
                var pitch = -0.28f + 0.12f * MathF.Sin(progress * MathF.PI * 2f);
                crossingStage.SetReferenceCameraOrientation(yaw, pitch);

                var projected = new PointF[crossingIntersectionOrbitAnchors.Length];
                for (var index = 0; index < crossingIntersectionOrbitAnchors.Length; index++)
                {
                    if (!crossingStage.TryProjectScenePosition(
                            new System.Numerics.Vector3(crossingIntersectionOrbitAnchors[index], 0, 0),
                            out projected[index],
                            out _)
                        || !float.IsFinite(projected[index].X)
                        || !float.IsFinite(projected[index].Y)
                        || projected[index].X < 6
                        || projected[index].X >= crossingStage.ClientSize.Width - 6
                        || projected[index].Y < 6
                        || projected[index].Y >= crossingStage.ClientSize.Height - 6)
                    {
                        throw new InvalidOperationException(
                            "The analytic crossing-card intersection left the orbit fixture bounds: "
                            + $"frame={frame}, yaw={yaw:0.###}, pitch={pitch:0.###}, "
                            + $"anchor={crossingIntersectionOrbitAnchors[index]:0.###}, "
                            + $"projected={projected[index]}.");
                    }
                }
                if (CrossingPointDistance(projected[0], projected[^1]) < 24f)
                {
                    throw new InvalidOperationException(
                        "The analytic crossing-card intersection collapsed during the orbit fixture: "
                        + $"frame={frame}, yaw={yaw:0.###}, pitch={pitch:0.###}.");
                }

                var edges = crossingStage.GetReference3DSceneRenderItems()
                    .Where(item => item.Kind == Reference3DRenderKind.IntersectionEdge
                        && (item.ObjectIndex == crossingCardA
                            && item.SecondaryObjectIndex == crossingCardB
                            || item.ObjectIndex == crossingCardB
                            && item.SecondaryObjectIndex == crossingCardA))
                    .ToArray();
                if (edges.Length == 0)
                {
                    throw new InvalidOperationException(
                        "A crossing-card intersection edge disappeared during a continuous camera orbit: "
                        + $"frame={frame}, yaw={yaw:0.###}, pitch={pitch:0.###}.");
                }
                if (edges.Any(item => item.Contours.Length == 0
                        || item.Contours.Any(contour => contour.Points.Length < 2)))
                {
                    throw new InvalidOperationException(
                        "A crossing-card intersection edge retained an item but no drawable contour: "
                        + $"frame={frame}, yaw={yaw:0.###}, pitch={pitch:0.###}, "
                        + $"segments={edges.Length}.");
                }
                var contours = edges.SelectMany(item => item.Contours).ToArray();

                foreach (var anchor in projected)
                {
                    var distance = contours.Min(contour => CrossingDistanceToPolyline(
                            Point.Round(anchor),
                            contour.Points,
                            contour.Closed));
                    if (distance > 1.25f)
                    {
                        throw new InvalidOperationException(
                            "A crossing-card intersection edge was discontinuous during a continuous camera orbit: "
                            + $"frame={frame}, yaw={yaw:0.###}, pitch={pitch:0.###}, "
                            + $"anchor={anchor}, distance={distance:0.###}, segments={edges.Length}.");
                    }
                }

                using var bitmap = RenderGdi(crossingStage);
                var radius = Math.Clamp(
                    (int)MathF.Ceiling(edges.Max(item => item.EdgeWidth) * 0.5f) + 1,
                    2,
                    4);
                foreach (var anchor in projected)
                {
                    if (!HasPixelNearColor(
                            bitmap,
                            anchor,
                            crossingEdgeColor,
                            radius,
                            tolerance: 28))
                    {
                        throw new InvalidOperationException(
                            "GDI dropped a crossing-card intersection edge during a continuous camera orbit: "
                            + $"frame={frame}, yaw={yaw:0.###}, pitch={pitch:0.###}, anchor={anchor}.");
                    }
                }
            }

            crossingStage.SetReferenceCameraOrientation(0.43f, -0.24f);
        }

        void AssertGrazingIntersectionEdgeOrbit()
        {
            const int frameCount = 33;
            var anchors = new[] { -700f, 0f, 700f };
            var perspectiveView = new SceneDefinition();
            ConfigureNeutralReferenceLighting(perspectiveView);
            perspectiveView.Dimension = SceneDimension.ThreeD;
            perspectiveView.Camera.Projection = CameraProjection.Perspective;
            grazingStage.ConfigureReferenceView(perspectiveView, SceneDimension.ThreeD);
            grazingStage.ResetReferenceCameraView();
            for (var frame = 0; frame < frameCount; frame++)
            {
                var progress = frame / (frameCount - 1f);
                var yaw = -0.24f + 0.48f * progress;
                var pitch = -0.12f + 0.08f * MathF.Sin(progress * MathF.PI * 2f);
                grazingStage.SetReferenceCameraOrientation(yaw, pitch);

                var projected = new PointF[anchors.Length];
                for (var index = 0; index < anchors.Length; index++)
                {
                    if (!grazingStage.TryProjectScenePosition(
                            new System.Numerics.Vector3(grazingPivotX, anchors[index], 0),
                            out projected[index],
                            out _)
                        || !float.IsFinite(projected[index].X)
                        || !float.IsFinite(projected[index].Y)
                        || projected[index].X < 6
                        || projected[index].X >= grazingStage.ClientSize.Width - 6
                        || projected[index].Y < 6
                        || projected[index].Y >= grazingStage.ClientSize.Height - 6)
                    {
                        throw new InvalidOperationException(
                            "The analytic grazing intersection left the orbit fixture bounds: "
                            + $"frame={frame}, yaw={yaw:0.###}, pitch={pitch:0.###}, "
                            + $"anchor={anchors[index]:0.###}, projected={projected[index]}.");
                    }
                }
                if (CrossingPointDistance(projected[0], projected[^1]) < 24f)
                {
                    throw new InvalidOperationException(
                        "The analytic grazing intersection collapsed during the orbit fixture: "
                        + $"frame={frame}, yaw={yaw:0.###}, pitch={pitch:0.###}.");
                }
                var edges = grazingStage.GetReference3DSceneRenderItems()
                    .Where(item => item.Kind == Reference3DRenderKind.IntersectionEdge
                        && (item.ObjectIndex == grazingSurface
                            && item.SecondaryObjectIndex == grazingTilted
                            || item.ObjectIndex == grazingTilted
                            && item.SecondaryObjectIndex == grazingSurface))
                    .ToArray();
                if (edges.Length == 0)
                {
                    throw new InvalidOperationException(
                        "A grazing intersection edge disappeared during a continuous camera orbit: "
                        + $"frame={frame}, yaw={yaw:0.###}, pitch={pitch:0.###}.");
                }
                if (edges.Any(item => item.Contours.Length == 0
                        || item.Contours.Any(contour => contour.Points.Length < 2)))
                {
                    throw new InvalidOperationException(
                        "A grazing intersection edge retained an item but no drawable contour: "
                        + $"frame={frame}, yaw={yaw:0.###}, pitch={pitch:0.###}, "
                        + $"segments={edges.Length}.");
                }
                var contours = edges.SelectMany(item => item.Contours).ToArray();

                foreach (var anchor in projected)
                {
                    var distance = contours.Min(contour => CrossingDistanceToPolyline(
                            Point.Round(anchor),
                            contour.Points,
                            contour.Closed));
                    if (distance > 1.25f)
                    {
                        throw new InvalidOperationException(
                            "A grazing intersection edge was discontinuous during a continuous camera orbit: "
                            + $"frame={frame}, yaw={yaw:0.###}, pitch={pitch:0.###}, "
                            + $"anchor={anchor}, distance={distance:0.###}, segments={edges.Length}.");
                    }
                }

                using var bitmap = RenderGdi(grazingStage);
                foreach (var anchor in projected)
                {
                    if (!HasPixelNearColor(bitmap, anchor, grazingStrokeColor, radius: 3, tolerance: 28))
                    {
                        throw new InvalidOperationException(
                            "GDI dropped a grazing intersection edge during a continuous camera orbit: "
                            + $"frame={frame}, yaw={yaw:0.###}, pitch={pitch:0.###}, anchor={anchor}.");
                    }
                }
            }

            grazingView.Camera.Projection = CameraProjection.Orthographic;
            grazingStage.ConfigureReferenceView(grazingView, SceneDimension.ThreeD);
            grazingStage.ResetReferenceCameraView();
            grazingStage.SetReferenceCameraOrientation(0, 0);
        }

        static int AssertIntersectionEdgeCapTopology(
            IReadOnlyList<Reference3DRenderItem> edges,
            string context)
        {
            const float endpointTolerance = 0.01f;
            var sharedEndpointCount = 0;
            foreach (var pair in edges.GroupBy(item => (
                         First: Math.Min(item.ObjectIndex, item.SecondaryObjectIndex),
                         Second: Math.Max(item.ObjectIndex, item.SecondaryObjectIndex),
                         item.SurfaceSlot)))
            {
                var pieces = pair.OrderBy(item => item.FragmentSlot).ToArray();
                var contours = new Reference3DProjectedContour[pieces.Length];
                for (var index = 0; index < pieces.Length; index++)
                {
                    if (pieces[index].Contours is not { Length: 1 }
                        || pieces[index].Contours[0].Points is not { Length: >= 2 })
                    {
                        throw new InvalidOperationException(
                            "An intersection-edge piece had no single drawable contour while checking caps: "
                            + $"{context}, pair={pair.Key}, fragment={pieces[index].FragmentSlot}.");
                    }
                    contours[index] = pieces[index].Contours[0];
                }

                if (!pieces[0].EdgeStartCap || !pieces[^1].EdgeEndCap)
                {
                    throw new InvalidOperationException(
                        "An intersection-edge chain did not preserve round physical endpoints: "
                        + $"{context}, pair={pair.Key}, start={pieces[0].EdgeStartCap}, "
                        + $"end={pieces[^1].EdgeEndCap}.");
                }
                for (var index = 1; index < pieces.Length; index++)
                {
                    var previousEnd = contours[index - 1].Points[^1];
                    var currentStart = contours[index].Points[0];
                    var shared = CrossingPointDistance(previousEnd, currentStart) <= endpointTolerance;
                    if (shared && (pieces[index - 1].EdgeEndCap || pieces[index].EdgeStartCap))
                    {
                        throw new InvalidOperationException(
                            "A shared intersection-edge endpoint used a round cap: "
                            + $"{context}, pair={pair.Key}, previous={previousEnd}, current={currentStart}.");
                    }
                    if (!shared && (!pieces[index - 1].EdgeEndCap || !pieces[index].EdgeStartCap))
                    {
                        throw new InvalidOperationException(
                            "Separated intersection-edge contours did not preserve round physical endpoints: "
                            + $"{context}, pair={pair.Key}, previous={previousEnd}, current={currentStart}.");
                    }
                    if (shared) sharedEndpointCount++;
                }
            }
            return sharedEndpointCount;
        }

        List<(
            Point Screen,
            int ObjectIndex,
            Color ExpectedColor,
            int SourceObject,
            PointF SourcePoint,
            float DepthGap)>
            FindOccludedCrossingCardStrokeSamples(bool requireSample = true)
        {
            const float minimumDepthGap = 90f;
            var objects = new[] { crossingCardA, crossingCardB };
            var transforms = new[] { crossingCardATransform, crossingCardBTransform };
            var colors = new[] { crossingCardAColor, crossingCardBColor };
            var intersectionEdges = crossingStage.GetReference3DSceneRenderItems()
                .Where(item => item.Kind == Reference3DRenderKind.IntersectionEdge)
                .ToArray();
            var halfWidth = crossingCardSize.Width * 0.5f;
            var halfHeight = crossingCardSize.Height * 0.5f;
            var boundaryPoints = new List<PointF>();
            foreach (var localX in new[] { -1_500f, -900f, -300f, 300f, 900f, 1_500f })
            {
                boundaryPoints.Add(new PointF(localX, -halfHeight));
                boundaryPoints.Add(new PointF(localX, halfHeight));
            }
            foreach (var localY in new[] { -900f, -450f, 0f, 450f, 900f })
            {
                boundaryPoints.Add(new PointF(-halfWidth, localY));
                boundaryPoints.Add(new PointF(halfWidth, localY));
            }

            var result = new List<(
                Point Screen,
                int ObjectIndex,
                Color ExpectedColor,
                int SourceObject,
                PointF SourcePoint,
                float DepthGap)>();
            for (var sourceSlot = 0; sourceSlot < objects.Length; sourceSlot++)
            {
                var foregroundSlot = 1 - sourceSlot;
                var foregroundContours = crossingStage.GetReference3DProjectedContours(objects[foregroundSlot]);
                foreach (var localPoint in boundaryPoints)
                {
                    if (!crossingStage.TryProjectScenePoint(
                            objects[sourceSlot],
                            localPoint,
                            out var projected,
                            out _))
                    {
                        continue;
                    }

                    var screen = Point.Round(projected);
                    if (!ProjectedFillContainsMargin(foregroundContours, screen, 8)
                        || !crossingStage.TryGetReferenceRay(screen, out var ray)
                        || !TryIntersectCardPlane(
                            ray,
                            transforms[sourceSlot],
                            out var sourceDepth,
                            out _)
                        || !TryIntersectCardPlane(
                            ray,
                            transforms[foregroundSlot],
                            out var foregroundDepth,
                            out _)
                        || sourceDepth - foregroundDepth < minimumDepthGap
                        || intersectionEdges.Any(edge => edge.Contours.Any(contour =>
                            CrossingDistanceToPolyline(screen, contour.Points, contour.Closed)
                                <= Math.Max(6f, edge.EdgeWidth * 0.5f + 3f)))
                        || result.Any(sample => CrossingScreenDistance(sample.Screen, screen) < 8f))
                    {
                        continue;
                    }

                    result.Add((
                        screen,
                        objects[foregroundSlot],
                        colors[foregroundSlot],
                        objects[sourceSlot],
                        localPoint,
                        sourceDepth - foregroundDepth));
                    break;
                }
            }

            if (requireSample && result.Count == 0)
            {
                throw new InvalidOperationException(
                    "The oblique crossing-card fixture exposed no occluded boundary sample.");
            }
            return result;
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

        (PointF Point, Color Expected, Color Forbidden, string Role)[] AssertThickEdgeGrazingFrame(
            StageControl stage,
            float yaw,
            float pitch,
            string label)
        {
            stage.SetReferenceCameraOrientation(yaw, pitch);
            var indexedItems = stage.GetReference3DSceneRenderItems()
                .Select((item, index) => (Item: item, Index: index))
                .ToArray();
            var edgeEntries = indexedItems
                .Where(entry => entry.Item.Kind == Reference3DRenderKind.IntersectionEdge)
                .ToArray();
            var expectedFirst = Math.Min(thickEdgeIncidentA, thickEdgeIncidentB);
            var expectedSecond = Math.Max(thickEdgeIncidentA, thickEdgeIncidentB);
            var edgePairs = edgeEntries
                .Select(entry => (
                    First: Math.Min(entry.Item.ObjectIndex, entry.Item.SecondaryObjectIndex),
                    Second: Math.Max(entry.Item.ObjectIndex, entry.Item.SecondaryObjectIndex)))
                .ToHashSet();
            if (edgeEntries.Length == 0
                || edgePairs.Count != 1
                || !edgePairs.Contains((expectedFirst, expectedSecond)))
            {
                throw new InvalidOperationException(
                    "The thick-edge grazing fixture generated an unexpected intersection pair: "
                    + $"{label}, pairs=[{string.Join(',', edgePairs)}].");
            }

            var coverEntries = indexedItems
                .Where(entry => entry.Item.ObjectIndex == thickEdgeCover
                    && entry.Item.Kind == Reference3DRenderKind.FrontFill)
                .ToArray();
            if (coverEntries.Length != 1 || coverEntries[0].Item.FragmentClip is not null)
            {
                throw new InvalidOperationException(
                    "The thick-edge grazing cover did not remain one unfragmented fill: "
                    + $"{label}, fills={coverEntries.Length}.");
            }
            var coverEntry = coverEntries[0];
            var coverContours = stage.GetReference3DProjectedContours(thickEdgeCover);
            var samples = new List<(PointF Point, Color Expected, Color Forbidden, string Role)>();
            foreach (var anchorX in new[] { -600f, 0f, 600f })
            {
                if (!stage.TryProjectScenePosition(
                        new System.Numerics.Vector3(anchorX, 0, 0),
                        out var center,
                        out _)
                    || !stage.TryProjectScenePosition(
                        new System.Numerics.Vector3(anchorX - 100f, 0, 0),
                        out var tangentStart,
                        out _)
                    || !stage.TryProjectScenePosition(
                        new System.Numerics.Vector3(anchorX + 100f, 0, 0),
                        out var tangentEnd,
                        out _)
                    || !stage.TryProjectScenePoint(
                        thickEdgeCover,
                        new PointF(anchorX, 80f),
                        out var coverBoundary,
                        out _)
                    || !stage.TryProjectScenePoint(
                        thickEdgeCover,
                        new PointF(anchorX, 160f),
                        out var coverInterior,
                        out _))
                {
                    throw new InvalidOperationException(
                        $"The thick-edge grazing samples could not be projected: {label}, X={anchorX:0.###}.");
                }

                var tangentX = tangentEnd.X - tangentStart.X;
                var tangentY = tangentEnd.Y - tangentStart.Y;
                var tangentLength = MathF.Sqrt(tangentX * tangentX + tangentY * tangentY);
                if (!float.IsFinite(tangentLength) || tangentLength <= 0.001f)
                {
                    throw new InvalidOperationException(
                        $"The thick-edge grazing tangent collapsed: {label}, X={anchorX:0.###}.");
                }
                var normalX = -tangentY / tangentLength;
                var normalY = tangentX / tangentLength;
                if ((coverInterior.X - center.X) * normalX
                        + (coverInterior.Y - center.Y) * normalY
                    < 0)
                {
                    normalX = -normalX;
                    normalY = -normalY;
                }

                var nearestEdge = edgeEntries
                    .Select(entry => (
                        Entry: entry,
                        Distance: entry.Item.Contours.Min(contour => CrossingDistanceToPolyline(
                            Point.Round(center),
                            contour.Points,
                            contour.Closed))))
                    .OrderBy(candidate => candidate.Distance)
                    .First();
                var edge = nearestEdge.Entry.Item;
                var radius = edge.EdgeWidth * 0.5f;
                var boundaryDistance = (coverBoundary.X - center.X) * normalX
                    + (coverBoundary.Y - center.Y) * normalY;
                var open = new PointF(
                    center.X - normalX * radius * 0.65f,
                    center.Y - normalY * radius * 0.65f);
                var covered = new PointF(
                    center.X + normalX * radius * 0.82f,
                    center.Y + normalY * radius * 0.82f);
                var centerScreen = Point.Round(center);
                var openScreen = Point.Round(open);
                var coveredScreen = Point.Round(covered);
                var centerStable = ProjectedFillMembershipStable(
                    coverContours,
                    centerScreen,
                    1,
                    out var centerInside);
                var openStable = ProjectedFillMembershipStable(
                    coverContours,
                    openScreen,
                    1,
                    out var openInside);
                var coveredStable = ProjectedFillMembershipStable(
                    coverContours,
                    coveredScreen,
                    1,
                    out var coveredInside);
                if (!float.IsFinite(radius)
                    || radius <= 3f
                    || nearestEdge.Distance > 1.25f
                    || boundaryDistance < radius * 0.35f
                    || boundaryDistance > radius * 0.60f
                    || !centerStable
                    || centerInside
                    || !openStable
                    || openInside
                    || !coveredStable
                    || !coveredInside)
                {
                    throw new InvalidOperationException(
                        "The thick-edge grazing footprint was not stable around the cover boundary: "
                        + $"{label}, X={anchorX:0.###}, radius={radius:0.###}, "
                        + $"edgeDistance={nearestEdge.Distance:0.###}, boundary={boundaryDistance:0.###}, "
                        + $"stable={centerStable}/{openStable}/{coveredStable}, "
                        + $"inside={centerInside}/{openInside}/{coveredInside}.");
                }

                var openDistance = edge.Contours.Min(contour => CrossingDistanceToPolyline(
                    openScreen,
                    contour.Points,
                    contour.Closed));
                var coveredDistance = edge.Contours.Min(contour => CrossingDistanceToPolyline(
                    coveredScreen,
                    contour.Points,
                    contour.Closed));
                if (openDistance >= radius - 0.5f || coveredDistance >= radius - 0.5f)
                {
                    throw new InvalidOperationException(
                        "A thick-edge grazing sample left the synthetic stroke footprint: "
                        + $"{label}, X={anchorX:0.###}, radius={radius:0.###}, "
                        + $"distances={openDistance:0.###}/{coveredDistance:0.###}.");
                }

                var hasRay = stage.TryGetReferenceRay(coveredScreen, out var ray);
                var edgeDistance = 0f;
                var hasEdgeDepth = hasRay
                    && TryIntersectCardPlane(
                        ray,
                        thickEdgeIncidentATransform,
                        out edgeDistance,
                        out _);
                var coverDistance = 0f;
                var hasCoverDepth = hasRay
                    && TryIntersectCardPlane(
                        ray,
                        thickEdgeCoverTransform,
                        out coverDistance,
                        out _);
                if (!hasEdgeDepth
                    || !hasCoverDepth
                    || edgeDistance - coverDistance <= 300f)
                {
                    throw new InvalidOperationException(
                        "The thick-edge grazing cover was not locally in front of the edge footprint: "
                        + $"{label}, X={anchorX:0.###}, depth={edgeDistance:0.###}/{coverDistance:0.###}.");
                }

                if (coverEntry.Item.AverageDepth <= edge.AverageDepth + 50f
                    || nearestEdge.Entry.Index >= coverEntry.Index)
                {
                    throw new InvalidOperationException(
                        "The thick-edge grazing fixture did not override misleading average-depth order: "
                        + $"{label}, X={anchorX:0.###}, average={edge.AverageDepth:0.###}/"
                        + $"{coverEntry.Item.AverageDepth:0.###}, order={nearestEdge.Entry.Index}/"
                        + $"{coverEntry.Index}.");
                }

                var edgeColor = Color.FromArgb(edge.EdgeArgb);
                samples.Add((center, edgeColor, thickEdgeSurfaceColor, $"X={anchorX:0.###} center"));
                samples.Add((open, edgeColor, thickEdgeSurfaceColor, $"X={anchorX:0.###} open"));
                samples.Add((covered, thickEdgeCoverColor, edgeColor, $"X={anchorX:0.###} covered"));
            }
            return samples.ToArray();
        }

        static void AssertThickEdgeGrazingPixels(
            Bitmap bitmap,
            IReadOnlyList<(PointF Point, Color Expected, Color Forbidden, string Role)> samples,
            string label,
            Func<PointF, PointF>? mapPoint = null)
        {
            foreach (var sample in samples)
            {
                var point = mapPoint is null ? sample.Point : mapPoint(sample.Point);
                var actual = SampleBitmap(bitmap, point);
                if (!PixelRgbNear(actual, sample.Expected, 28)
                    || PixelRgbNear(actual, sample.Forbidden, 18))
                {
                    throw new InvalidOperationException(
                        "A thick intersection edge flickered across its grazing cover: "
                        + $"{label}, role={sample.Role}, point={point}, "
                        + $"actual={actual.ToArgb():X8}, expected={sample.Expected.ToArgb():X8}, "
                        + $"forbidden={sample.Forbidden.ToArgb():X8}.");
                }
            }
        }

        static bool TryResolvePhysicalFragmentSlot(
            StageControl stage,
            IReadOnlyList<Reference3DRenderItem> renderItems,
            int objectIndex,
            PointF sourcePoint,
            out ulong fragmentIdentity)
        {
            fragmentIdentity = 0;
            if (!stage.TryProjectScenePoint(
                    objectIndex,
                    sourcePoint,
                    out var projected,
                    out _)
                || !float.IsFinite(projected.X)
                || !float.IsFinite(projected.Y))
            {
                return false;
            }

            var screen = Point.Round(projected);
            var matches = new List<ulong>();
            foreach (var item in renderItems)
            {
                if (item.ObjectIndex != objectIndex
                    || item.Kind != Reference3DRenderKind.FrontFill
                    || item.FragmentClip is not { Length: > 0 })
                {
                    continue;
                }
                if (!ProjectedFillMembershipStable(
                        item.FragmentClip,
                        screen,
                        1,
                        out var contains))
                {
                    return false;
                }
                if (contains) matches.Add(item.StableFragmentIdentity);
            }
            if (matches.Count != 1) return false;
            fragmentIdentity = matches[0];
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

        void AssertOccludedThreeCardEdges()
        {
            const float minimumDepthGap = 90f;
            var renderItems = threeCardStage.GetReference3DSceneRenderItems();
            threeCardOccludedEdgeSamples.Clear();
            foreach (var edge in renderItems.Where(item => item.Kind == Reference3DRenderKind.IntersectionEdge))
            {
                var edgeSlot = Array.IndexOf(threeCardObjects, edge.ObjectIndex);
                if (edgeSlot < 0) continue;
                foreach (var contour in edge.Contours)
                {
                    if (contour.Points.Length < 2) continue;
                    var start = contour.Points[0];
                    var end = contour.Points[^1];
                    foreach (var amount in new[] { 0.25f, 0.5f, 0.75f })
                    {
                        var screen = Point.Round(new PointF(
                            start.X + (end.X - start.X) * amount,
                            start.Y + (end.Y - start.Y) * amount));
                        if (!threeCardStage.TryGetReferenceRay(screen, out var ray)
                            || !TryIntersectCardPlane(
                                ray,
                                threeCardTransforms[edgeSlot],
                                out var edgeDepth,
                                out _))
                        {
                            continue;
                        }

                        for (var foregroundSlot = 0; foregroundSlot < threeCardObjects.Length; foregroundSlot++)
                        {
                            var foregroundObject = threeCardObjects[foregroundSlot];
                            if (foregroundObject == edge.ObjectIndex
                                || foregroundObject == edge.SecondaryObjectIndex
                                || !ProjectedFillContainsMargin(
                                    threeCardStage.GetReference3DProjectedContours(foregroundObject),
                                    screen,
                                    3)
                                || !TryIntersectCardPlane(
                                    ray,
                                    threeCardTransforms[foregroundSlot],
                                    out var foregroundDepth,
                                    out _)
                                || edgeDepth - foregroundDepth < minimumDepthGap)
                            {
                                continue;
                            }

                            threeCardOccludedEdgeSamples.Add((screen, threeCardColors[foregroundSlot]));
                            break;
                        }
                        if (threeCardOccludedEdgeSamples.Count > 0) break;
                    }
                    if (threeCardOccludedEdgeSamples.Count > 0) break;
                }
                if (threeCardOccludedEdgeSamples.Count > 0) break;
            }

            if (threeCardOccludedEdgeSamples.Count == 0)
            {
                throw new InvalidOperationException(
                    "The oblique three-card fixture exposed no intersection edge behind a third surface.");
            }

            using var bitmap = RenderGdi(threeCardStage);
            foreach (var sample in threeCardOccludedEdgeSamples)
            {
                AssertPixelNear(
                    SampleBitmap(bitmap, sample.Screen),
                    sample.ExpectedColor,
                    "A three-card intersection edge showed through the foreground surface",
                    tolerance: 20);
            }
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
            var intersectionEdges = stage.GetReference3DSceneRenderItems()
                .Where(item => item.Kind == Reference3DRenderKind.IntersectionEdge)
                .ToArray();
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
                    if (intersectionEdges.Any(edge => edge.Contours.Any(contour =>
                            CrossingDistanceToPolyline(screen, contour.Points, contour.Closed)
                                <= edge.EdgeWidth * 0.5f + 2f)))
                    {
                        continue;
                    }

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
                var actual = SampleBitmap(bitmap, sample.Screen);
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

        static Point FindVisibleExtrusionSurfaceSample(
            StageControl stage,
            IReadOnlyList<Reference3DRenderItem> items,
            Reference3DRenderKind targetKind)
        {
            for (var y = 8; y < stage.Height - 8; y += 2)
            {
                for (var x = 8; x < stage.Width - 8; x += 2)
                {
                    var screen = new Point(x, y);
                    Reference3DRenderKind? visibleKind = null;
                    foreach (var item in items)
                    {
                        if (item.Kind is not (Reference3DRenderKind.Back
                            or Reference3DRenderKind.Side
                            or Reference3DRenderKind.FrontFill)
                            || !ProjectedFillContainsMargin(item.Contours, screen, 3)
                            || item.FragmentClip is { Length: > 0 }
                            && !ProjectedFillContainsMargin(item.FragmentClip, screen, 3))
                        {
                            continue;
                        }
                        visibleKind = item.Kind;
                    }

                    if (visibleKind == targetKind) return screen;
                }
            }

            throw new InvalidOperationException(
                $"The extrusion color fixture exposed no stable {targetKind} pixel.");
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

        static bool TryIntersectReferenceSurface(
            SpatialRay ray,
            Reference3DSurfacePlane plane,
            out float distance)
        {
            distance = 0;
            if (!plane.IsValid) return false;
            var denominator = System.Numerics.Vector3.Dot(ray.Direction, plane.Normal);
            if (!float.IsFinite(denominator) || Math.Abs(denominator) <= 0.000001f)
            {
                return false;
            }
            distance = (plane.Distance
                    - System.Numerics.Vector3.Dot(plane.Normal, ray.Origin))
                / denominator;
            return float.IsFinite(distance) && distance > 0;
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

        static float CrossingPointDistance(PointF first, PointF second)
        {
            var dx = first.X - second.X;
            var dy = first.Y - second.Y;
            return MathF.Sqrt(dx * dx + dy * dy);
        }

        static float CrossingDistanceToPolyline(
            Point point,
            IReadOnlyList<PointF> points,
            bool closed)
        {
            if (points.Count == 0) return float.PositiveInfinity;
            if (points.Count == 1) return CrossingScreenDistance(point, Point.Round(points[0]));
            var distance = float.PositiveInfinity;
            var segmentCount = closed ? points.Count : points.Count - 1;
            for (var segment = 0; segment < segmentCount; segment++)
            {
                var start = points[segment];
                var end = points[(segment + 1) % points.Count];
                var dx = end.X - start.X;
                var dy = end.Y - start.Y;
                var lengthSquared = dx * dx + dy * dy;
                var amount = lengthSquared <= 0.000001f
                    ? 0f
                    : Math.Clamp(
                        ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared,
                        0f,
                        1f);
                var nearestX = start.X + dx * amount;
                var nearestY = start.Y + dy * amount;
                var offsetX = point.X - nearestX;
                var offsetY = point.Y - nearestY;
                distance = Math.Min(distance, MathF.Sqrt(offsetX * offsetX + offsetY * offsetY));
            }
            return distance;
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
            ConfigureNeutralReferenceLighting(perspectiveView);
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
            ConfigureNeutralReferenceLighting(perspectiveView);
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
                        SampleBitmap(bitmap, projected),
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
                    SampleBitmap(bitmap, outsideProjected),
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
            direct2DStage.Invalidate();
            direct2DStage.Update();
            Application.DoEvents();
            var direct2DReferencePlanBuilds = direct2DStage.Reference3DRenderPlanBuildCount;
            if (direct2DStage.LastDirect2DBaseFrameCacheBuilds != 1
                || direct2DStage.LastDirect2DBaseFrameCacheReuses != 0)
            {
                throw new InvalidOperationException(
                    "The initial Direct2D reference-3D frame did not populate its base-frame cache.");
            }
            direct2DStage.SetReference3DSelection([materialObject]);
            direct2DStage.Update();
            Application.DoEvents();
            if (direct2DStage.LastDirect2DBaseFrameCacheBuilds != 0
                || direct2DStage.LastDirect2DBaseFrameCacheReuses != 1
                || direct2DStage.Reference3DRenderPlanBuildCount != direct2DReferencePlanBuilds)
            {
                throw new InvalidOperationException(
                    "A Direct2D reference-3D selection overlay rebuilt the stable base frame or render plan.");
            }
            direct2DStage.SetSpatialTransformGizmo(
                System.Numerics.Vector3.Zero,
                SpatialTransformMode.Move,
                [materialObject]);
            direct2DStage.Update();
            Application.DoEvents();
            if (direct2DStage.LastDirect2DBaseFrameCacheBuilds != 0
                || direct2DStage.LastDirect2DBaseFrameCacheReuses != 1
                || direct2DStage.Reference3DRenderPlanBuildCount != direct2DReferencePlanBuilds)
            {
                throw new InvalidOperationException(
                    "A Direct2D reference-3D Gizmo overlay rebuilt the stable base frame or render plan.");
            }
            var lightGizmoFixture = SceneLightDefinition.CreateDefaultDirectional();
            direct2DStage.SetSceneLightGizmo(lightGizmoFixture.Kind, lightGizmoFixture.Settings);
            direct2DStage.Update();
            Application.DoEvents();
            if (!direct2DStage.TryGetSceneLightGizmoScreenGeometry(out var direct2DLightGizmoGeometry)
                || direct2DStage.LastDirect2DBaseFrameCacheBuilds != 0
                || direct2DStage.LastDirect2DBaseFrameCacheReuses != 1
                || direct2DStage.Reference3DRenderPlanBuildCount != direct2DReferencePlanBuilds)
            {
                throw new InvalidOperationException(
                    "A Direct2D light-Gizmo overlay rebuilt the stable base frame or had no screen geometry.");
            }
            using var lightGizmoCapture = materialCapture is not null
                ? CapturePresentedStage(form, direct2DStage, Color.Fuchsia)
                : null;
            if (materialCapture is not null && lightGizmoCapture is null)
            {
                throw new InvalidOperationException(
                    "The visible Direct2D window could not capture the light-Gizmo overlay.");
            }
            if (lightGizmoCapture is not null
                && !HasPixelNearColor(
                    lightGizmoCapture,
                    CapturePoint(form, direct2DStage, direct2DLightGizmoGeometry.IntensityHandle),
                    StageControl.SceneLightGizmoIntensityColor,
                    radius: 5,
                    tolerance: 18))
            {
                throw new InvalidOperationException(
                    "The Direct2D reference-3D overlay did not paint the light-Gizmo intensity handle.");
            }
            direct2DStage.ClearSceneLightGizmo();
            Console.WriteLine(materialCapture is not null
                ? "scene_light_gizmo_direct2d_pixels=ok"
                : "scene_light_gizmo_direct2d_pixels=skipped_no_visible_desktop");
            direct2DStage.ClearSpatialTransformGizmo();
            direct2DStage.ClearReference3DSelection();
            materialScene.SetLayerVisible(0, false);
            direct2DStage.SetReference3DSelection([materialObject]);
            direct2DStage.Update();
            Application.DoEvents();
            var hiddenDirect2DPlanBuilds = direct2DStage.Reference3DRenderPlanBuildCount;
            if (!direct2DStage.LastFrameUsedDirect2D
                || direct2DStage.LastDirect2DBaseFrameCacheBuilds != 1
                || direct2DStage.LastDirect2DBaseFrameCacheReuses != 0
                || hiddenDirect2DPlanBuilds <= direct2DReferencePlanBuilds)
            {
                throw new InvalidOperationException(
                    "An in-place Direct2D layer visibility change reused a stale reference-3D base frame.");
            }

            materialScene.SetLayerVisible(0, true);
            materialScene.LayerOpacity[0] = 0.5f;
            direct2DStage.ClearReference3DSelection();
            direct2DStage.Update();
            Application.DoEvents();
            if (!direct2DStage.LastFrameUsedDirect2D
                || direct2DStage.LastDirect2DBaseFrameCacheBuilds != 1
                || direct2DStage.LastDirect2DBaseFrameCacheReuses != 0
                || direct2DStage.Reference3DRenderPlanBuildCount <= hiddenDirect2DPlanBuilds)
            {
                throw new InvalidOperationException(
                    "Normal opacity fell back from Direct2D or reused a stale reference-3D base frame.");
            }
            materialScene.LayerOpacity[0] = 1f;
            direct2DStage.Invalidate();
            direct2DStage.Update();
            Application.DoEvents();
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

            if (!lineJoinScene.SetLineEndpointStyle(
                    lineJoinFirst,
                    startEndpoint: false,
                    LineEndpointStyle.Sharp)
                || !lineJoinScene.SetLineEndpointStyle(
                    lineJoinSecond,
                    startEndpoint: true,
                    LineEndpointStyle.Sharp))
            {
                throw new InvalidOperationException(
                    "The Direct2D Line endpoint fixture could not restore its Sharp junction.");
            }
            direct2DStage.BindScene(lineJoinScene);
            direct2DStage.ConfigureReferenceView(curveProjectionView, SceneDimension.ThreeD);
            direct2DStage.ResetReferenceCameraView();
            direct2DStage.SetReferenceCameraOrientation(0.32f, -0.18f);
            direct2DStage.SetSceneCompositionResult(
                CompositionResult(
                    System.Numerics.Matrix4x4.Identity,
                    System.Numerics.Matrix4x4.Identity),
                lineJoinScene);
            var direct2DLineItems = direct2DStage.GetReference3DSceneRenderItems();
            var direct2DLineOwner = direct2DLineItems.Single(item =>
                item.ObjectIndex == lineJoinFirst
                && item.Kind == Reference3DRenderKind.FrontStroke);
            var direct2DLineWidth = direct2DStage.GetReference3DStrokeWidth(
                lineJoinFirst,
                lineJoinStroke);
            if (!direct2DStage.TryGetReference3DLineEndpointJoin(
                    lineJoinFirst,
                    startEndpoint: false,
                    direct2DLineOwner.Contours,
                    direct2DLineWidth,
                    out var direct2DLineJoint,
                    out var direct2DLineMiters,
                    out _)
                || direct2DLineMiters.Length == 0)
            {
                throw new InvalidOperationException(
                    "The real-HWND Direct2D Line fixture did not produce a Sharp miter.");
            }
            var direct2DLineTip = direct2DLineMiters
                .SelectMany(miter => new[] { miter.OuterMiter, miter.InnerMiter })
                .OrderByDescending(point => CrossingScreenDistance(
                    Point.Round(direct2DLineJoint),
                    Point.Round(point)))
                .First();
            var direct2DLineSample = new PointF(
                direct2DLineJoint.X + (direct2DLineTip.X - direct2DLineJoint.X) * 0.72f,
                direct2DLineJoint.Y + (direct2DLineTip.Y - direct2DLineJoint.Y) * 0.72f);
            direct2DStage.Invalidate();
            direct2DStage.Update();
            Application.DoEvents();
            using var sharpLineCapture = direct2DPixelsAvailable
                ? CapturePresentedStage(form, direct2DStage, Color.Fuchsia)
                : null;
            if (!direct2DStage.LastFrameUsedDirect2D || !direct2DStage.GpuAccelerationActive)
            {
                throw new InvalidOperationException(
                    "The real-HWND Direct2D Stage did not render the Sharp Line endpoint fixture.");
            }
            if (direct2DPixelsAvailable && sharpLineCapture is null)
            {
                throw new InvalidOperationException(
                    "The visible Direct2D window could not capture the Sharp Line endpoint fixture.");
            }
            if (sharpLineCapture is not null
                && !HasPixelNearColor(
                    sharpLineCapture,
                    CapturePoint(form, direct2DStage, direct2DLineSample),
                    lineJoinGradientEnd,
                    radius: 2,
                    tolerance: 28))
            {
                throw new InvalidOperationException(
                    "The Direct2D Sharp Line miter followed GradientPath instead of the rendered gradient axis.");
            }

            if (!lineJoinScene.SetLineEndpointStyle(
                    lineJoinFirst,
                    startEndpoint: false,
                    LineEndpointStyle.Round)
                || !lineJoinScene.SetLineEndpointStyle(
                    lineJoinSecond,
                    startEndpoint: true,
                    LineEndpointStyle.Round))
            {
                throw new InvalidOperationException(
                    "The Direct2D Line endpoint fixture could not switch its junction to Round.");
            }
            direct2DStage.Invalidate();
            direct2DStage.Update();
            Application.DoEvents();
            using var roundLineCapture = direct2DPixelsAvailable
                ? CapturePresentedStage(form, direct2DStage, Color.Fuchsia)
                : null;
            if (!direct2DStage.LastFrameUsedDirect2D || !direct2DStage.GpuAccelerationActive)
            {
                throw new InvalidOperationException(
                    "The real-HWND Direct2D Stage did not render the Round Line endpoint fixture.");
            }
            if (direct2DPixelsAvailable && roundLineCapture is null)
            {
                throw new InvalidOperationException(
                    "The visible Direct2D window could not capture the Round Line endpoint fixture.");
            }
            if (roundLineCapture is not null
                && !PixelRgbNear(
                    SampleBitmap(
                        roundLineCapture,
                        CapturePoint(form, direct2DStage, direct2DLineSample)),
                    background,
                    tolerance: 16))
            {
                throw new InvalidOperationException(
                    "The Direct2D Round Line junction retained a stale Sharp miter pixel.");
            }
            Console.WriteLine(direct2DPixelsAvailable
                ? "scene_reference_line_endpoint_direct2d_pixels=ok"
                : "scene_reference_line_endpoint_direct2d_pixels=skipped_no_visible_desktop");

            scene.SetGradientPaint(
                planarObject,
                GradientKind.Linear,
                gradientStops,
                new PointF(-1_600, 0),
                new PointF(1_600, 0));
            scene.ClearGradientPath(planarObject);
            direct2DStage.BindScene(scene);
            direct2DStage.ConfigureReferenceView(perspectiveView, SceneDimension.ThreeD);
            direct2DStage.ResetReferenceCameraView();
            direct2DStage.SetReferenceCameraOrientation(0, 0);
            direct2DStage.SetSceneCompositionResult(CompositionResult(spatialPose), scene);
            var perspectiveGradientItems = direct2DStage.GetReference3DSceneRenderItems()
                .Where(item => item.ObjectIndex == planarObject
                    && item.Kind == Reference3DRenderKind.FrontFill)
                .ToArray();
            var opticalViewport = new Rectangle(Point.Empty, direct2DStage.ClientSize);
            if (perspectiveGradientItems.Length == 0
                || perspectiveGradientItems.Any(item =>
                    item.OpticalSurface is not { } surface
                    || surface.Bounds.Width <= 0
                    || surface.Bounds.Height <= 0
                    || !opticalViewport.IntersectsWith(surface.Bounds)
                    || surface.PremultipliedPixels.Length
                        != surface.PixelWidth * surface.PixelHeight
                    || surface.Bitmap.Width != surface.PixelWidth
                    || surface.Bitmap.Height != surface.PixelHeight))
            {
                throw new InvalidOperationException(
                    "A perspective fill gradient did not build a bounded shared optical surface.");
            }
            direct2DStage.Invalidate();
            direct2DStage.Update();
            Application.DoEvents();
            if (!direct2DStage.LastFrameUsedDirect2D
                || direct2DStage.LastDirect2DReference3DProjectiveGradientDomainFills != 0)
            {
                throw new InvalidOperationException(
                    "A perspective fill gradient did not present through the shared Direct2D optical surface.");
            }

            direct2DStage.BindScene(extrusionScene);
            var backFaceView = new SceneDefinition { Dimension = SceneDimension.ThreeD };
            ConfigureNeutralReferenceLighting(backFaceView);
            backFaceView.Camera.Projection = CameraProjection.Perspective;
            direct2DStage.ConfigureReferenceView(backFaceView, SceneDimension.ThreeD);
            direct2DStage.ResetReferenceCameraView();
            direct2DStage.SetReferenceCameraOrientation(0.72f, -0.36f);
            direct2DStage.SetSceneCompositionResult(
                PoseResult(backFaceTransform, backFaceExtrusion),
                extrusionScene);
            var direct2DExtrusionItems = direct2DStage.GetReference3DLayerRenderItems([extrusionObject]);
            var direct2DBackSample = FindVisibleExtrusionSurfaceSample(
                direct2DStage,
                direct2DExtrusionItems,
                Reference3DRenderKind.Back);
            var direct2DSideSample = FindVisibleExtrusionSurfaceSample(
                direct2DStage,
                direct2DExtrusionItems,
                Reference3DRenderKind.Side);
            direct2DStage.Invalidate();
            direct2DStage.Update();
            Application.DoEvents();
            using var extrusionColorCapture = direct2DPixelsAvailable
                ? CapturePresentedStage(form, direct2DStage, Color.Fuchsia)
                : null;
            if (!direct2DStage.LastFrameUsedDirect2D || !direct2DStage.GpuAccelerationActive)
            {
                throw new InvalidOperationException(
                    "The real-HWND Direct2D Stage did not render the extrusion back-face fixture.");
            }
            if (direct2DPixelsAvailable && extrusionColorCapture is null)
            {
                throw new InvalidOperationException(
                    "The visible Direct2D window could not capture the extrusion back-face colors.");
            }
            if (extrusionColorCapture is not null
                && (!HasPixelNearColor(
                        extrusionColorCapture,
                        CapturePoint(form, direct2DStage, direct2DBackSample),
                        extrusionFill,
                        radius: 2,
                        tolerance: 28)
                    || !HasPixelNearColor(
                        extrusionColorCapture,
                        CapturePoint(form, direct2DStage, direct2DSideSample),
                        extrusionStroke,
                        radius: 2,
                        tolerance: 28)))
            {
                throw new InvalidOperationException(
                    "Direct2D did not keep the fill color on the extrusion back face and the outline color on its side wall.");
            }

            direct2DStage.BindScene(extrusionOcclusionScene);
            direct2DStage.ConfigureReferenceView(
                extrusionOcclusionView,
                SceneDimension.ThreeD);
            direct2DStage.ResetReferenceCameraView();
            direct2DStage.SetReferenceCameraOrientation(0.72f, -0.36f);
            direct2DStage.SetSceneCompositionResult(
                extrusionOcclusionResult,
                extrusionOcclusionScene);
            direct2DStage.Invalidate();
            direct2DStage.Update();
            Application.DoEvents();
            using var extrusionOcclusionCapture = direct2DPixelsAvailable
                ? CapturePresentedStage(form, direct2DStage, Color.Fuchsia)
                : null;
            if (!direct2DStage.LastFrameUsedDirect2D || !direct2DStage.GpuAccelerationActive)
            {
                throw new InvalidOperationException(
                    "The real-HWND Direct2D Stage did not render perspective extrusion fragments.");
            }
            if (direct2DPixelsAvailable && extrusionOcclusionCapture is null)
            {
                throw new InvalidOperationException(
                    "The visible Direct2D perspective extrusion window could not be captured.");
            }
            if (extrusionOcclusionCapture is not null)
            {
                foreach (var sample in extrusionOcclusionSamples)
                {
                    if (!HasPixelNearColor(
                            extrusionOcclusionCapture,
                            CapturePoint(form, direct2DStage, sample.Screen),
                            sample.ExpectedColor,
                            radius: 2,
                            tolerance: 28))
                    {
                        throw new InvalidOperationException(
                            "A Direct2D perspective extrusion fragment painted the wrong foreground: "
                            + $"screen={sample.Screen}, expected={sample.ExpectedColor.ToArgb():X8}.");
                    }
                }
            }
            Console.WriteLine(direct2DPixelsAvailable
                ? "scene_reference_extrusion_occlusion_direct2d=ok"
                : "scene_reference_extrusion_occlusion_direct2d=skipped_no_visible_desktop");

            trajectoryStage.ConfigureReferenceView(perspectiveView, SceneDimension.ThreeD);
            trajectoryStage.ResetReferenceCameraView();
            trajectoryStage.SetReferenceCameraOrientation(0, 0);
            trajectoryStage.SetSceneCompositionResult(CompositionResult(spatialPose), trajectoryScene);
            var trajectoryEdgeSamples = new[]
            {
                new PointF(-1_592, -700),
                new PointF(-1_580, -700),
                new PointF(-1_568, -700)
            };
            var trajectoryExpectedSamples = new List<(PointF Source, Color Expected, bool Edge)>();
            using (var trajectoryBitmap = RenderGdi(trajectoryStage))
            {
                foreach (var sourcePoint in trajectorySamples.Concat(trajectoryEdgeSamples))
                {
                    if (!trajectoryStage.TryProjectScenePoint(
                            trajectoryObject,
                            sourcePoint,
                            out var projected,
                            out _))
                    {
                        throw new InvalidOperationException("A perspective trajectory-gradient sample could not be projected.");
                    }
                    trajectoryExpectedSamples.Add((
                        sourcePoint,
                        SampleBitmap(trajectoryBitmap, projected),
                        trajectoryEdgeSamples.Contains(sourcePoint)));
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
                            radius: sample.Edge ? 0 : 2,
                            tolerance: sample.Edge ? 64 : 32))
                    {
                        throw new InvalidOperationException(
                            "The Direct2D perspective trajectory gradient disagreed with its GDI material sample: "
                            + $"source={sample.Source}, projected={projected}, capture={capturePoint}, "
                            + $"actual={SampleBitmap(trajectoryCapture, capturePoint).ToArgb():X8}, "
                            + $"expected={sample.Expected.ToArgb():X8}.");
                    }
                }
            }

            direct2DStage.BindScene(containedStrokeScene);
            direct2DStage.ConfigureReferenceView(containedStrokeView, SceneDimension.ThreeD);
            direct2DStage.ResetReferenceCameraView();
            direct2DStage.SetReferenceCameraOrientation(0.43f, -0.24f);
            direct2DStage.SetSceneCompositionResult(
                CompositionResult(containedStrokeTransform, containedStrokeCoverTransform),
                containedStrokeScene);
            direct2DStage.Invalidate();
            direct2DStage.Update();
            Application.DoEvents();
            using var containedStrokeCapture = direct2DPixelsAvailable
                ? CapturePresentedStage(form, direct2DStage, Color.Fuchsia)
                : null;
            if (!direct2DStage.LastFrameUsedDirect2D || !direct2DStage.GpuAccelerationActive)
            {
                throw new InvalidOperationException(
                    "The real-HWND Direct2D Stage did not render the contained stroke fragment.");
            }
            if (direct2DPixelsAvailable && containedStrokeCapture is null)
            {
                throw new InvalidOperationException(
                    "The visible Direct2D contained-stroke window could not be captured.");
            }
            if (containedStrokeCapture is not null
                && !HasPixelNearColor(
                    containedStrokeCapture,
                    CapturePoint(form, direct2DStage, containedStrokeSample),
                    containedStrokeExpectedColor,
                    radius: 1,
                    tolerance: 28))
            {
                throw new InvalidOperationException(
                    "Direct2D used whole-object depth for a fully covered stroke fragment.");
            }

            direct2DStage.BindScene(crossingLineScene);
            direct2DStage.ConfigureReferenceView(crossingLineView, SceneDimension.ThreeD);
            direct2DStage.ResetReferenceCameraView();
            direct2DStage.SetReferenceCameraOrientation(0.43f, -0.24f);
            direct2DStage.SetSceneCompositionResult(
                CompositionResult(crossingCardATransform, crossingCardBTransform),
                crossingLineScene);
            direct2DStage.Invalidate();
            direct2DStage.Update();
            Application.DoEvents();
            using var crossingLineCapture = direct2DPixelsAvailable
                ? CapturePresentedStage(form, direct2DStage, Color.Fuchsia)
                : null;
            if (!direct2DStage.LastFrameUsedDirect2D || !direct2DStage.GpuAccelerationActive)
            {
                throw new InvalidOperationException(
                    "The real-HWND Direct2D Stage did not render the fragmented crossing line.");
            }
            if (direct2DPixelsAvailable && crossingLineCapture is null)
            {
                throw new InvalidOperationException(
                    "The visible Direct2D crossing-line window could not be captured.");
            }
            if (crossingLineCapture is not null)
            {
                foreach (var sample in crossingLineSamples)
                {
                    if (!HasPixelNearColor(
                            crossingLineCapture,
                            CapturePoint(form, direct2DStage, sample.Screen),
                            sample.ExpectedColor,
                            radius: 1,
                            tolerance: 28))
                    {
                        throw new InvalidOperationException(
                            $"Direct2D used whole-object depth for a crossing line at {sample.Screen}.");
                    }
                }
            }

            if (crossingDirect2DSamples.Count < 4)
            {
                throw new InvalidOperationException("The crossing-card fixture did not retain its oblique Direct2D samples.");
            }
            var closedBezierScene = closedBezierIntersectionScene
                ?? throw new InvalidOperationException(
                    "The closed-Bezier fixture did not retain its Direct2D scene.");
            if (closedBezierDirect2DSamples.Count != 4)
            {
                throw new InvalidOperationException(
                    "The closed-Bezier fixture did not retain four Direct2D samples.");
            }
            direct2DStage.BindScene(closedBezierScene);
            direct2DStage.ConfigureReferenceView(crossingView, SceneDimension.ThreeD);
            direct2DStage.ResetReferenceCameraView();
            direct2DStage.SetReferenceCameraOrientation(0.43f, -0.24f);
            direct2DStage.SetSceneCompositionResult(
                CompositionResult(crossingCardATransform, crossingCardBTransform),
                closedBezierScene);
            direct2DStage.Invalidate();
            direct2DStage.Update();
            Application.DoEvents();
            using var closedBezierCapture = direct2DPixelsAvailable
                ? CapturePresentedStage(form, direct2DStage, Color.Fuchsia)
                : null;
            if (!direct2DStage.LastFrameUsedDirect2D || !direct2DStage.GpuAccelerationActive)
            {
                throw new InvalidOperationException(
                    "The real-HWND Direct2D Stage did not render closed-Bezier surface fragments.");
            }
            if (direct2DPixelsAvailable && closedBezierCapture is null)
            {
                throw new InvalidOperationException(
                    "The visible Direct2D closed-Bezier window could not be captured.");
            }
            if (closedBezierCapture is not null)
            {
                foreach (var sample in closedBezierDirect2DSamples)
                {
                    if (!HasPixelNearColor(
                            closedBezierCapture,
                            CapturePoint(form, direct2DStage, sample.Screen),
                            sample.ExpectedColor,
                            radius: 2,
                            tolerance: 28))
                    {
                        throw new InvalidOperationException(
                            $"The Direct2D closed-Bezier fragment painted the wrong foreground at {sample.Screen}.");
                    }
                }
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
                if (crossingOccludedStrokeSamples.Count == 0)
                {
                    throw new InvalidOperationException(
                        "The crossing-card fixture did not retain an occluded-stroke sample for Direct2D.");
                }
                foreach (var sample in crossingOccludedStrokeSamples)
                {
                    var capturePoint = CapturePoint(form, direct2DStage, sample.Screen);
                    if (!HasPixelNearColor(
                            crossingCapture,
                            capturePoint,
                            sample.ExpectedColor,
                            radius: 0,
                            tolerance: 28)
                        || HasPixelNearColor(
                            crossingCapture,
                            capturePoint,
                            crossingEdgeColor,
                            radius: 0,
                            tolerance: 28))
                    {
                        throw new InvalidOperationException(
                            $"Direct2D exposed a rear crossing-card boundary at {sample.Screen}.");
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

                foreach (var sample in crossingRotationDirect2DSamples)
                {
                    direct2DStage.SetReferenceCameraOrientation(sample.Yaw, sample.Pitch);
                    direct2DStage.Invalidate();
                    direct2DStage.Update();
                    Application.DoEvents();
                    using var rotationCapture = direct2DPixelsAvailable
                        ? CapturePresentedStage(form, direct2DStage, Color.Fuchsia)
                        : null;
                    if (!direct2DStage.LastFrameUsedDirect2D || !direct2DStage.GpuAccelerationActive)
                    {
                        throw new InvalidOperationException(
                            "The real-HWND Direct2D Stage did not render a crossing-card camera rotation frame.");
                    }
                    if (direct2DPixelsAvailable && rotationCapture is null)
                    {
                        throw new InvalidOperationException(
                            "A visible Direct2D crossing-card rotation frame could not be captured.");
                    }
                    if (rotationCapture is null) continue;
                    var capturePoint = CapturePoint(form, direct2DStage, sample.Screen);
                    if (!HasPixelNearColor(
                            rotationCapture,
                            capturePoint,
                            sample.ExpectedColor,
                            radius: 0,
                            tolerance: 28)
                        || HasPixelNearColor(
                            rotationCapture,
                            capturePoint,
                            crossingEdgeColor,
                            radius: 0,
                            tolerance: 28))
                    {
                        throw new InvalidOperationException(
                            "Direct2D exposed a rear boundary while rotating the crossing-card camera: "
                            + $"yaw={sample.Yaw:0.###}, pitch={sample.Pitch:0.###}, point={sample.Screen}.");
                    }
                }
            }

            const int crossingIntersectionOrbitFrameCount = 49;
            foreach (var frame in crossingIntersectionDirect2DFrames)
            {
                var progress = frame / (crossingIntersectionOrbitFrameCount - 1f);
                var yaw = -0.72f + 1.44f * progress;
                var pitch = -0.28f + 0.12f * MathF.Sin(progress * MathF.PI * 2f);
                direct2DStage.SetReferenceCameraOrientation(yaw, pitch);

                var projected = new PointF[crossingIntersectionOrbitAnchors.Length];
                for (var index = 0; index < crossingIntersectionOrbitAnchors.Length; index++)
                {
                    if (!direct2DStage.TryProjectScenePosition(
                            new System.Numerics.Vector3(crossingIntersectionOrbitAnchors[index], 0, 0),
                            out projected[index],
                            out _)
                        || !float.IsFinite(projected[index].X)
                        || !float.IsFinite(projected[index].Y)
                        || projected[index].X < 6
                        || projected[index].X >= direct2DStage.ClientSize.Width - 6
                        || projected[index].Y < 6
                        || projected[index].Y >= direct2DStage.ClientSize.Height - 6)
                    {
                        throw new InvalidOperationException(
                            "A Direct2D crossing-card orbit anchor left the fixture bounds: "
                            + $"frame={frame}, yaw={yaw:0.###}, pitch={pitch:0.###}, "
                            + $"anchor={crossingIntersectionOrbitAnchors[index]:0.###}, "
                            + $"projected={projected[index]}.");
                    }
                }

                var orbitEdges = direct2DStage.GetReference3DSceneRenderItems()
                    .Where(item => item.Kind == Reference3DRenderKind.IntersectionEdge
                        && (item.ObjectIndex == crossingCardA
                            && item.SecondaryObjectIndex == crossingCardB
                            || item.ObjectIndex == crossingCardB
                            && item.SecondaryObjectIndex == crossingCardA))
                    .ToArray();
                if (orbitEdges.Length == 0
                    || orbitEdges.Any(item => item.Contours.Length == 0
                        || item.Contours.Any(contour => contour.Points.Length < 2)))
                {
                    throw new InvalidOperationException(
                        "The Direct2D crossing-card orbit produced no drawable intersection edge: "
                        + $"frame={frame}, yaw={yaw:0.###}, pitch={pitch:0.###}, "
                        + $"segments={orbitEdges.Length}.");
                }
                var orbitContours = orbitEdges.SelectMany(item => item.Contours).ToArray();
                foreach (var anchor in projected)
                {
                    var distance = orbitContours.Min(contour => CrossingDistanceToPolyline(
                        Point.Round(anchor),
                        contour.Points,
                        contour.Closed));
                    if (distance > 1.25f)
                    {
                        throw new InvalidOperationException(
                            "The Direct2D crossing-card orbit plan had a discontinuous intersection edge: "
                            + $"frame={frame}, yaw={yaw:0.###}, pitch={pitch:0.###}, "
                            + $"anchor={anchor}, distance={distance:0.###}.");
                    }
                }

                direct2DStage.Invalidate();
                direct2DStage.Update();
                Application.DoEvents();
                using var orbitCapture = direct2DPixelsAvailable
                    ? CapturePresentedStage(form, direct2DStage, Color.Fuchsia)
                    : null;
                if (!direct2DStage.LastFrameUsedDirect2D || !direct2DStage.GpuAccelerationActive)
                {
                    throw new InvalidOperationException(
                        "The real-HWND Direct2D Stage did not render a crossing-card intersection orbit frame.");
                }
                if (direct2DPixelsAvailable && orbitCapture is null)
                {
                    throw new InvalidOperationException(
                        "A visible Direct2D crossing-card intersection orbit frame could not be captured: "
                        + $"frame={frame}, yaw={yaw:0.###}, pitch={pitch:0.###}.");
                }
                if (orbitCapture is null) continue;

                var radius = Math.Clamp(
                    (int)MathF.Ceiling(orbitEdges.Max(item => item.EdgeWidth) * 0.5f) + 1,
                    2,
                    4);
                foreach (var anchor in projected)
                {
                    if (!HasPixelNearColor(
                            orbitCapture,
                            CapturePoint(form, direct2DStage, anchor),
                            crossingEdgeColor,
                            radius,
                            tolerance: 28))
                    {
                        throw new InvalidOperationException(
                            "Direct2D dropped a crossing-card intersection edge during the camera orbit: "
                            + $"frame={frame}, yaw={yaw:0.###}, pitch={pitch:0.###}, anchor={anchor}.");
                    }
                }
            }
            direct2DStage.SetReferenceCameraOrientation(0.43f, -0.24f);

            direct2DStage.BindScene(thickEdgeScene);
            direct2DStage.ConfigureReferenceView(thickEdgeView, SceneDimension.ThreeD);
            direct2DStage.ResetReferenceCameraView();
            direct2DStage.SetSceneCompositionResult(
                CompositionResult(
                    thickEdgeIncidentBTransform,
                    thickEdgeIncidentATransform,
                    thickEdgeCoverTransform),
                thickEdgeScene);
            foreach (var frame in thickEdgeDirect2DFrames)
            {
                var progress = frame / (thickEdgeOrbitFrameCount - 1f);
                var yaw = -0.12f + 0.24f * progress;
                var pitch = 0.02f * MathF.Sin(progress * MathF.PI * 2f);
                var samples = AssertThickEdgeGrazingFrame(
                    direct2DStage,
                    yaw,
                    pitch,
                    $"Direct2D frame={frame}");
                direct2DStage.Invalidate();
                direct2DStage.Update();
                Application.DoEvents();
                using var thickEdgeCapture = direct2DPixelsAvailable
                    ? CapturePresentedStage(form, direct2DStage, Color.Fuchsia)
                    : null;
                if (!direct2DStage.LastFrameUsedDirect2D || !direct2DStage.GpuAccelerationActive)
                {
                    throw new InvalidOperationException(
                        "The real-HWND Direct2D Stage did not render a thick-edge grazing orbit frame.");
                }
                if (direct2DPixelsAvailable && thickEdgeCapture is null)
                {
                    throw new InvalidOperationException(
                        "A visible Direct2D thick-edge grazing orbit frame could not be captured: "
                        + $"frame={frame}, yaw={yaw:0.###}, pitch={pitch:0.###}.");
                }
                if (thickEdgeCapture is not null)
                {
                    AssertThickEdgeGrazingPixels(
                        thickEdgeCapture,
                        samples,
                        $"Direct2D frame={frame}",
                        point => CapturePoint(form, direct2DStage, point));
                }
            }
            Console.WriteLine(direct2DPixelsAvailable
                ? "scene_reference_thick_edge_grazing_direct2d=ok"
                : "scene_reference_thick_edge_grazing_direct2d=skipped_no_visible_desktop");

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
                && !PixelRgbNear(SampleBitmap(grazingCapture, grazingCapturePoint), grazingEdgeColor, 16))
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
            var direct2DOpticsScene = new VectorScene();
            direct2DOpticsScene.CreateEmpty();
            var direct2DOpticsReceiver = direct2DOpticsScene.AddObject(
                0,
                PointF.Empty,
                new SizeF(2400, 1800),
                angle: 0,
                stroke: 0,
                color: Color.FromArgb(255, 112, 156, 202),
                strokeColor: Color.Transparent,
                atoms: 12,
                shapeKind: ShapeKind.Rectangle);
            var direct2DOpticsView = new SceneDefinition { Dimension = SceneDimension.ThreeD };
            direct2DOpticsView.RestoreLights(
            [
                new SceneLightDefinition(
                    Guid.NewGuid().ToString("N"),
                    "Direct2D point fixture",
                    SceneLightKind.Point,
                    new SceneLightSettings(
                        true,
                        Color.White.ToArgb(),
                        3f,
                        1500f,
                        new System.Numerics.Vector3(0, 0, -1000),
                        System.Numerics.Vector3.Zero,
                        System.Numerics.Vector2.Zero,
                        false,
                        0,
                        0))
            ],
            lightsWerePresent: true);
            direct2DStage.BindScene(direct2DOpticsScene);
            direct2DStage.ConfigureReferenceView(direct2DOpticsView, SceneDimension.ThreeD);
            direct2DStage.ResetReferenceCameraView();
            direct2DStage.SetReferenceCameraOrientation(0, 0);
            direct2DStage.SetSceneCompositionResult(
                CompositionResult(System.Numerics.Matrix4x4.Identity),
                direct2DOpticsScene);
            var direct2DOpticsField = direct2DStage.GetReference3DSceneRenderItems()
                .Where(item => item.ObjectIndex == direct2DOpticsReceiver
                    && item.Kind == Reference3DRenderKind.FrontFill)
                .SelectMany(item => item.LocalLightLayers ?? [])
                .FirstOrDefault();
            if (direct2DOpticsField.LinearStops is not { Length: >= 9 })
            {
                throw new InvalidOperationException(
                    "The real-HWND Direct2D point-light fixture did not build a continuous field.");
            }
            direct2DStage.Invalidate();
            direct2DStage.Update();
            Application.DoEvents();
            using var direct2DOpticsCapture = direct2DPixelsAvailable
                ? CapturePresentedStage(form, direct2DStage, Color.Fuchsia)
                : null;
            if (!direct2DStage.LastFrameUsedDirect2D || !direct2DStage.GpuAccelerationActive)
            {
                throw new InvalidOperationException(
                    "The real-HWND Direct2D Stage did not render the continuous point-light fixture.");
            }
            if (direct2DOpticsCapture is not null)
            {
                var transform = direct2DOpticsField.GradientTransform;
                var radialColors = new HashSet<int>();
                for (var index = 0; index <= 48; index++)
                {
                    var radius = 0.4f + index / 48f * 0.3f;
                    var screen = new PointF(
                        transform.M21 * radius + transform.M31,
                        transform.M22 * radius + transform.M32);
                    var sample = Point.Round(CapturePoint(form, direct2DStage, screen));
                    if ((uint)sample.X < direct2DOpticsCapture.Width
                        && (uint)sample.Y < direct2DOpticsCapture.Height)
                    {
                        radialColors.Add(direct2DOpticsCapture.GetPixel(sample.X, sample.Y).ToArgb());
                    }
                }
                if (radialColors.Count < 12)
                {
                    throw new InvalidOperationException(
                        $"The Direct2D point-light radius retained visible color bands: unique={radialColors.Count}.");
                }
            }
            var direct2DStrokeScene = new VectorScene();
            direct2DStrokeScene.CreateEmpty();
            var direct2DStrokeObject = direct2DStrokeScene.AddObject(
                0,
                PointF.Empty,
                new SizeF(7_200, 5_400),
                angle: 0,
                stroke: VectorUnits.MinimumStrokeUnits,
                color: Color.FromArgb(255, 210, 216, 216),
                strokeColor: Color.FromArgb(255, 24, 28, 32),
                atoms: 24,
                shapeKind: ShapeKind.Rectangle);
            var direct2DStrokeView = new SceneDefinition { Dimension = SceneDimension.ThreeD };
            direct2DStrokeView.Camera.Projection = CameraProjection.Perspective;
            direct2DStrokeView.RestoreLights(
            [
                new SceneLightDefinition(
                    "direct2d-thin-stroke-ambient",
                    "Direct2D thin stroke ambient",
                    SceneLightKind.Ambient,
                    new SceneLightSettings(
                        true,
                        Color.White.ToArgb(),
                        1f,
                        0,
                        System.Numerics.Vector3.Zero,
                        System.Numerics.Vector3.Zero,
                        System.Numerics.Vector2.Zero,
                        false,
                        0,
                        0)),
                new SceneLightDefinition(
                    "direct2d-thin-stroke-point",
                    "Direct2D thin stroke point",
                    SceneLightKind.Point,
                    new SceneLightSettings(
                        true,
                        Color.FromArgb(255, 88, 220, 190).ToArgb(),
                        3f,
                        12_000,
                        new System.Numerics.Vector3(0, 0, 2_000),
                        System.Numerics.Vector3.Zero,
                        System.Numerics.Vector2.Zero,
                        false,
                        0,
                        0))
            ],
            lightsWerePresent: true);
            direct2DStage.BindScene(direct2DStrokeScene);
            direct2DStage.ConfigureReferenceView(direct2DStrokeView, SceneDimension.ThreeD);
            direct2DStage.ResetReferenceCameraView();
            direct2DStage.SetReferenceCameraOrientation(0.16f, -0.1f);
            direct2DStage.SetSceneCompositionResult(
                new SceneCompositionResult(
                    [new SceneCompositionObjectOwner("direct2d-thin-stroke", "fixture")],
                    [new SceneCompositionObjectPose(
                        System.Numerics.Matrix4x4.Identity,
                        new System.Numerics.Vector3(0, 0, 450))],
                    [SpatialOpticalMaterial.Default with { Reflectivity = 0.2f }]),
                direct2DStrokeScene);
            direct2DStage.BeginReference3DOpticalInteractionPreview();
            try
            {
                var direct2DStrokeItems = direct2DStage.GetReference3DSceneRenderItems();
                var direct2DStrokeItem = direct2DStrokeItems.Single(item =>
                    item.ObjectIndex == direct2DStrokeObject
                    && item.Kind == Reference3DRenderKind.FrontStroke);
                var direct2DStrokeFill = direct2DStrokeItems.Single(item =>
                    item.ObjectIndex == direct2DStrokeObject
                    && item.Kind == Reference3DRenderKind.FrontFill);
                if (direct2DStage.LastReference3DOpticalRasterLod != 2
                    || direct2DStrokeFill.OpticalSurface is null
                    || direct2DStrokeItem.OpticalSurface is not null
                    || direct2DStrokeItem.SolidStrokeOpticalBaseArgb is null)
                {
                    throw new InvalidOperationException(
                        "The real-HWND thin-stroke fixture did not keep its lit stroke vector-based "
                        + "while its fill used the reduced optical preview.");
                }

                direct2DStage.Invalidate();
                direct2DStage.Update();
                Application.DoEvents();
                using var direct2DStrokeCapture = direct2DPixelsAvailable
                    ? CapturePresentedStage(form, direct2DStage, Color.Fuchsia)
                    : null;
                if (!direct2DStage.LastFrameUsedDirect2D || !direct2DStage.GpuAccelerationActive)
                {
                    throw new InvalidOperationException(
                        "The real-HWND Direct2D Stage did not render the thin lit stroke fixture.");
                }
                if (direct2DStrokeCapture is not null)
                {
                    foreach (var contour in direct2DStrokeItem.Contours.Where(contour => contour.Closed))
                    {
                        var maximumGap = 0;
                        var currentGap = 0;
                        for (var pointIndex = 0; pointIndex < contour.Points.Length; pointIndex++)
                        {
                            var start = contour.Points[pointIndex];
                            var end = contour.Points[(pointIndex + 1) % contour.Points.Length];
                            var dx = end.X - start.X;
                            var dy = end.Y - start.Y;
                            var length = MathF.Sqrt(dx * dx + dy * dy);
                            var samples = Math.Max(2, (int)MathF.Ceiling(length));
                            for (var sampleIndex = 0; sampleIndex <= samples; sampleIndex++)
                            {
                                var amount = sampleIndex / (float)samples;
                                var capturePoint = CapturePoint(
                                    form,
                                    direct2DStage,
                                    new PointF(
                                        start.X + dx * amount,
                                        start.Y + dy * amount));
                                var centerX = (int)MathF.Round(capturePoint.X);
                                var centerY = (int)MathF.Round(capturePoint.Y);
                                var foundStroke = false;
                                for (var y = Math.Max(0, centerY - 1);
                                     y <= Math.Min(direct2DStrokeCapture.Height - 1, centerY + 1)
                                     && !foundStroke;
                                     y++)
                                {
                                    for (var x = Math.Max(0, centerX - 1);
                                         x <= Math.Min(direct2DStrokeCapture.Width - 1, centerX + 1);
                                         x++)
                                    {
                                        var pixel = direct2DStrokeCapture.GetPixel(x, y);
                                        if (pixel.R <= 8 && pixel.G <= 8 && pixel.B <= 8)
                                        {
                                            throw new InvalidOperationException(
                                                "The real-HWND Direct2D thin stroke rendered a hard black pixel: "
                                                + $"at=({x},{y}), actual={pixel.ToArgb():X8}.");
                                        }
                                        if (Math.Abs(pixel.R - background.R) > 5
                                            || Math.Abs(pixel.G - background.G) > 5
                                            || Math.Abs(pixel.B - background.B) > 5)
                                        {
                                            foundStroke = true;
                                            break;
                                        }
                                    }
                                }
                                if (foundStroke) currentGap = 0;
                                else
                                {
                                    currentGap++;
                                    maximumGap = Math.Max(maximumGap, currentGap);
                                }
                            }
                        }
                        if (maximumGap > 3)
                        {
                            throw new InvalidOperationException(
                                "The real-HWND Direct2D preview broke a thin lit 3D stroke into hard segments: "
                                + $"maximum_gap={maximumGap}px.");
                        }
                    }
                }
            }
            finally
            {
                direct2DStage.EndReference3DOpticalInteractionPreview();
            }
            Console.WriteLine(direct2DPixelsAvailable
                ? "scene_optics_continuous_direct2d_pixels=ok"
                : "scene_optics_continuous_direct2d_pixels=skipped_no_visible_desktop");
            Console.WriteLine(direct2DPixelsAvailable
                ? "scene_optics_thin_stroke_direct2d_pixels=ok"
                : "scene_optics_thin_stroke_direct2d_pixels=skipped_no_visible_desktop");
            Console.WriteLine(direct2DPixelsAvailable
                ? "scene_reference_projective_direct2d_pixels=ok"
                : "scene_reference_projective_direct2d_pixels=skipped_no_visible_desktop");
            Console.WriteLine(direct2DPixelsAvailable
                ? "scene_reference_closed_bezier_direct2d_pixels=ok"
                : "scene_reference_closed_bezier_direct2d_pixels=skipped_no_visible_desktop");
            Console.WriteLine(direct2DPixelsAvailable
                ? "scene_reference_extrusion_back_color_direct2d_pixels=ok"
                : "scene_reference_extrusion_back_color_direct2d_pixels=skipped_no_visible_desktop");
            Console.WriteLine("scene_reference_direct2d_base_frame_cache=ok");
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
                    if (!gradientStage.TryProjectScenePoint(
                            gradientObject,
                            samplePoint,
                            out var planarSample,
                            out _))
                    {
                        throw new InvalidOperationException(
                            $"The planar {label} gradient sample {index} could not be projected.");
                    }
                    expected[index] = SampleBitmap(planarGradientBitmap, planarSample);
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
                        SampleBitmap(spatialGradientBitmap, projectedSample),
                        expected[index],
                        $"The 2D front view changed the {label} fill gradient sample {index}",
                        tolerance: 10);
                }
            }

            var perspectiveView = new SceneDefinition { Dimension = SceneDimension.ThreeD };
            ConfigureNeutralReferenceLighting(perspectiveView);
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
                        SampleBitmap(perspectiveGradientBitmap, perspectiveSample),
                        expected[index],
                        $"Perspective changed the {label} fill gradient sample {index}",
                        tolerance: path is { Length: > 1 } || kind == GradientKind.ShapeRadial ? 22 : 16);
                }
            }
            gradientStage.ConfigureReferenceView(null, SceneDimension.TwoD);
            gradientStage.SetSceneCompositionResult(CompositionResult(spatialPose), gradientScene);
        }

        void RunSceneBuildingZTweenDepthRegression()
        {
            var project = VectorProject.CreateEmpty();
            var moverSource = project.DrawingObjects[0];
            var staticSource = project.AddDrawingObject("Static Z reference");
            var moverColor = Color.FromArgb(255, 224, 74, 82);
            var staticColor = Color.FromArgb(255, 48, 190, 126);
            ConfigureSource(moverSource, moverColor);
            ConfigureSource(staticSource, staticColor);

            var definition = project.Scenes[0];
            definition.Dimension = SceneDimension.TwoD;
            var moverLayerId = definition.Layers[0].Id;
            if (!project.TryAddSceneLayer(definition.Id, out var staticLayer)
                || staticLayer is null
                || !project.TryAddSceneInstance(
                    definition.Id,
                    moverSource.Id,
                    PointF.Empty,
                    1_000,
                    moverLayerId,
                    out var mover)
                || mover is null
                || !project.TryAddSceneInstance(
                    definition.Id,
                    staticSource.Id,
                    PointF.Empty,
                    0,
                    staticLayer.Id,
                    out var stationary)
                || stationary is null)
            {
                throw new InvalidOperationException(
                    "The Scene Building Z-tween depth fixture could not create its layered instances.");
            }

            var moverTrack = definition.Timeline.FindTrackByTargetId(moverLayerId)
                ?? throw new InvalidOperationException("The Z-tween mover layer lost its timeline track.");
            var staticTrack = definition.Timeline.FindTrackByTargetId(staticLayer.Id)
                ?? throw new InvalidOperationException("The Z-tween static layer lost its timeline track.");
            var tweenError = string.Empty;
            if (!definition.Timeline.SetTrackDuration(moverTrack.Id, 3)
                || !definition.Timeline.SetTrackDuration(staticTrack.Id, 3)
                || !definition.Timeline.InsertKeyframe(moverTrack.Id, 2)
                || !mover.SetStateAtFrame(2, mover.EvaluateState(0) with { Z = -1_000 })
                || !definition.TryCreateTimelineTween(
                    moverLayerId,
                    0,
                    2,
                    TimelineTweenKind.Classic,
                    out tweenError))
            {
                throw new InvalidOperationException(
                    $"The Scene Building Z-axis classic tween could not be created: {tweenError}");
            }
            if (Math.Abs(mover.EvaluateState(1).Z) > 0.0001f)
            {
                throw new InvalidOperationException("The Z-axis classic tween did not pass through the planar midpoint.");
            }

            for (var sourceFrame = 0; sourceFrame <= 2; sourceFrame++)
            {
                var composition = new VectorScene();
                var result = SceneCompositionBuilder.Build(
                    composition,
                    definition,
                    project.DrawingObjects,
                    sourceFrame);
                var moverObject = FindCompositionObject(result, composition, mover.Id);
                var staticObject = FindCompositionObject(result, composition, stationary.Id);
                var expectedMoverZ = sourceFrame switch
                {
                    0 => 1_000f,
                    1 => 0f,
                    _ => -1_000f
                };
                if (!result.TryGetPose(moverObject, out var moverPose)
                    || Math.Abs(moverPose.FlatToScene.M43 - expectedMoverZ) > 0.0001f)
                {
                    throw new InvalidOperationException(
                        $"The Z-tween frame {sourceFrame} composed the mover at the wrong depth.");
                }
                if (sourceFrame == 1
                    && result.ObjectPoses.Any(pose => pose.FlatToScene != System.Numerics.Matrix4x4.Identity))
                {
                    throw new InvalidOperationException(
                        "The planar Z-tween midpoint retained a non-identity composition pose.");
                }

                using var tweenStage = new StageControl(composition)
                {
                    ClientSize = new Size(stageWidth, stageHeight),
                    BackColor = background,
                    WorldGridOpacity = 0
                };
                tweenStage.ConfigureReferenceView(definition, SceneDimension.TwoD);
                tweenStage.SetSceneCompositionResult(result, composition);
                if (!tweenStage.UsesReferenceProjection
                    || !tweenStage.RendersReferenceProjection
                    || tweenStage.EffectiveReferenceProjection != CameraProjection.Orthographic
                    || !tweenStage.TryProjectScenePosition(
                        System.Numerics.Vector3.Zero,
                        out var center,
                        out _))
                {
                    throw new InvalidOperationException(
                        $"The Z-tween frame {sourceFrame} left the stable 2D orthographic spatial pipeline.");
                }

                var depthPlan = tweenStage.GetReference3DSceneRenderItems();
                var moverDepths = depthPlan
                    .Where(item => item.ObjectIndex == moverObject
                        && item.Kind == Reference3DRenderKind.FrontFill)
                    .Select(item => item.AverageDepth)
                    .ToArray();
                var staticDepths = depthPlan
                    .Where(item => item.ObjectIndex == staticObject
                        && item.Kind == Reference3DRenderKind.FrontFill)
                    .Select(item => item.AverageDepth)
                    .ToArray();
                if (moverDepths.Length == 0
                    || staticDepths.Length == 0
                    || moverDepths.Any(depth => !float.IsFinite(depth))
                    || staticDepths.Any(depth => !float.IsFinite(depth))
                    || moverDepths.Any(moverDepth => staticDepths.Any(staticDepth =>
                        Math.Abs((moverDepth - staticDepth) - expectedMoverZ) > 0.01f)))
                {
                    throw new InvalidOperationException(
                        $"The Z-tween frame {sourceFrame} render plan lost its signed depth: "
                        + $"expectedDelta={expectedMoverZ:0.###}, "
                        + $"mover=[{string.Join(',', moverDepths.Select(depth => depth.ToString("0.###")))}], "
                        + $"static=[{string.Join(',', staticDepths.Select(depth => depth.ToString("0.###")))}].");
                }

                var expectedObject = sourceFrame == 0 ? staticObject : moverObject;
                var expectedColor = sourceFrame == 0 ? staticColor : moverColor;
                using var bitmap = RenderGdi(tweenStage);
                AssertPixelNear(
                    SampleBitmap(bitmap, center),
                    expectedColor,
                    $"The Z-tween frame {sourceFrame} painted the wrong depth winner");
                if (!tweenStage.TryHitTestProjectedObject(
                        Point.Round(center),
                        1f,
                        out var hit)
                    || hit != expectedObject)
                {
                    throw new InvalidOperationException(
                        $"The Z-tween frame {sourceFrame} hit test did not select its nearest painted object.");
                }
            }

            static void ConfigureSource(DrawingObjectDefinition source, Color color)
            {
                source.Scene.CreateEmpty(frameCount: 1);
                source.Scene.AddObject(
                    0,
                    PointF.Empty,
                    new SizeF(2_400, 2_000),
                    0,
                    0,
                    color,
                    Color.Transparent,
                    12,
                    ShapeKind.Rectangle);
            }

            static int FindCompositionObject(
                SceneCompositionResult result,
                VectorScene composition,
                string rootInstanceId)
            {
                for (var objectIndex = 0; objectIndex < composition.ObjectCount; objectIndex++)
                {
                    if (!result.TryGetOwner(objectIndex, out var owner)) continue;
                    var root = string.IsNullOrWhiteSpace(owner.RootInstanceId)
                        ? owner.InstanceId
                        : owner.RootInstanceId;
                    if (string.Equals(root, rootInstanceId, StringComparison.Ordinal)) return objectIndex;
                }

                throw new InvalidOperationException(
                    $"The Z-tween composition lost instance '{rootInstanceId}'.");
            }
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

    private static void ConfigureNeutralReferenceLighting(SceneDefinition definition)
    {
        var ambient = SceneLightDefinition.CreateDefaultAmbient();
        _ = ambient.TryApply(
            ambient.Name,
            ambient.Settings with { Intensity = 1f });
        definition.RestoreLights([ambient], lightsWerePresent: true);
    }

    public static void RunStageRendererRegression()
    {
        RunUiRefreshStabilityRegression();
        RunSymbolFiltersRenderRegression();
        RunSymbolFiltersPanelRegression();
        RunGpuPlaybackTargetLifetimeRegression();
        RunLayerBlendRegression();
        RunSceneReferenceRenderRegression();
        RunWorkspacePreRenderTargetRegression();
        RunSceneOpticsRenderRegression();
        RunSnapPointSceneRegression();
        RunSnapPointOverlayRegression();
        RunImportedSvgRasterizerRegression();
        RunProjectiveSvgClippingRegression();
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
        RunCodexBridgeRegression();
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

        var direct2DExtrusionFill = Color.CornflowerBlue;
        var direct2DExtrusionStroke = Color.DeepPink;
        var direct2DExtrusionScene = new VectorScene();
        direct2DExtrusionScene.CreateEmpty();
        var direct2DExtrusionObject = direct2DExtrusionScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(3_600, 2_200),
            angle: 0,
            stroke: 120,
            color: direct2DExtrusionFill,
            strokeColor: direct2DExtrusionStroke,
            atoms: 120,
            shapeKind: ShapeKind.Rectangle);
        stage.BindScene(direct2DExtrusionScene);
        var direct2DExtrusionView = new SceneDefinition { Dimension = SceneDimension.ThreeD };
        ConfigureNeutralReferenceLighting(direct2DExtrusionView);
        direct2DExtrusionView.Camera.Projection = CameraProjection.Perspective;
        stage.ConfigureReferenceView(direct2DExtrusionView, SceneDimension.ThreeD);
        stage.ResetReferenceCameraView();
        stage.SetReferenceCameraOrientation(0.72f, -0.36f);
        var direct2DBackTransform = System.Numerics.Matrix4x4.CreateRotationY(MathF.PI);
        var direct2DBackExtrusion = System.Numerics.Vector3.Transform(
            new System.Numerics.Vector3(0, 0, 1_800),
            direct2DBackTransform);
        stage.SetSceneCompositionResult(
            new SceneCompositionResult(
                new SceneCompositionObjectOwner[1],
                [new SceneCompositionObjectPose(
                    direct2DBackTransform,
                    direct2DBackExtrusion)]),
            direct2DExtrusionScene);
        stage.Invalidate();
        stage.Update();
        Application.DoEvents();
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

        using var shotStrip = new ShotDirectorPanel();
        if (!MainForm.IsCanvasShortcutBlockingInteractiveControl(shotStrip))
        {
            throw new InvalidOperationException(
                "A focused shot strip did not take ownership of Delete and canvas shortcuts before the Stage.");
        }
        shotStrip.Bind(new ShotDirectorState
        {
            IsAvailable = true,
            ActiveShotId = "shot-a",
            CurrentFrame = 3,
            FrameCount = 40,
            PlaybackFps = 24m,
            ShotSequenceLength = 40,
            Shots =
            [
                new ShotDirectorItem
                {
                    Id = "shot-a",
                    Name = "Open",
                    Detail = "Wide",
                    DurationFrames = 16,
                    StartFrame = 0,
                    EndFrame = 15,
                    IsActive = true,
                    Layers =
                    [
                        new ShotDirectorLayer(
                            "layer-a",
                            "Background",
                            Color.Coral.ToArgb(),
                            false,
                            [new ShotDirectorExposure(0, 15, true)])
                    ]
                },
                new ShotDirectorItem
                {
                    Id = "shot-b",
                    Name = "Beat",
                    DurationFrames = 24,
                    StartFrame = 16,
                    EndFrame = 39
                }
            ]
        });
        if (shotStrip.SelectedShot?.Id != "shot-a"
            || shotStrip.FindShot("shot-b")?.EndFrame != 39
            || shotStrip.State.ShotSequenceLength != 40)
        {
            throw new InvalidOperationException(
                "The shot strip did not present the bound shot sequence and its active shot.");
        }
        shotStrip.SetPlayhead(20);
        if (shotStrip.SelectedShot?.Id != "shot-a")
        {
            throw new InvalidOperationException(
                "Moving the playhead into another shot span cleared the active shot filter.");
        }

        using var workspaceTabs = new WorkspaceTabs();
        if (!workspaceTabs.Views.Contains(WorkspaceView.ShotDirector)
            || MainForm.ResolveShotDirectorWidth(4000) != 760
            || MainForm.ResolveShotDirectorWidth(1200) != 480
            || MainForm.ResolveShotDirectorWidth(300) != 360)
        {
            throw new InvalidOperationException(
                "The Shots & Directing workspace was not registered as a bounded, responsive workspace view.");
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
