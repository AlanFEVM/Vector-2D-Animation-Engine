using System.Numerics;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunReferenceCameraTransitionRegression()
    {
        RunReferenceCameraFrameInterpolationRegression();
        RunReferenceCameraFocusFrameRegression();
        RunReferenceCameraFocusTransitionRegression();
        RunReferenceCameraProjectionBlendRegression();
        RunReferenceCamera2DRendererHandoffRegression();
        RunReferenceCameraCompositionTransitionRegression();
        RunReferenceCameraStageTransitionRegression();
        Console.WriteLine("scene_reference_camera_transition=ok");
    }

    private static void RunReferenceCameraFrameInterpolationRegression()
    {
        var from = new ReferenceCameraFrame(
            MathF.PI - 0.2f,
            -0.4f,
            4_000,
            0.5f,
            -300,
            450,
            -600,
            0);
        var to = new ReferenceCameraFrame(
            -MathF.PI + 0.2f,
            0.8f,
            16_000,
            2,
            700,
            -550,
            1_400,
            1);

        var start = StageControl.ResolveReferenceCameraTransitionFrame(from, to, -1);
        var midpoint = StageControl.ResolveReferenceCameraTransitionFrame(from, to, 0.5f);
        var end = StageControl.ResolveReferenceCameraTransitionFrame(from, to, 2);

        AssertReferenceCameraFrameNear(start, from, "start endpoint");
        AssertReferenceCameraFrameNear(end, to, "end endpoint");
        if (CameraTransitionAngularDistance(midpoint.Yaw, MathF.PI) > 0.0001f
            || Math.Abs(midpoint.Yaw) < 3
            || !CameraTransitionNear(midpoint.Pitch, 0.2f)
            || !CameraTransitionNear(midpoint.Distance, 8_000, 0.05f)
            || !CameraTransitionNear(midpoint.ZoomScale, 1)
            || !CameraTransitionNear(midpoint.TargetX, 200)
            || !CameraTransitionNear(midpoint.TargetY, -50)
            || !CameraTransitionNear(midpoint.TargetZ, 400)
            || !CameraTransitionNear(midpoint.ProjectionBlend, 0.5f))
        {
            throw new InvalidOperationException(
                "Reference camera interpolation did not preserve endpoints, positive logarithmic scaling, "
                + "projection blending, and the shortest yaw path across +/-PI.");
        }
    }

    private static void RunReferenceCameraFocusFrameRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        using var stage = new StageControl(scene) { Size = new Size(800, 600) };
        var definition = new SceneDefinition { Dimension = SceneDimension.ThreeD };
        definition.Camera.Projection = CameraProjection.Orthographic;
        stage.ConfigureReferenceView(definition, SceneDimension.ThreeD);
        stage.SetReferenceCameraOrientation(0, 0);
        var originalDistance = stage.ReferenceDistance;
        Vector3[] compactPoints =
        [
            new(100, -300, 250),
            new(500, -100, 350)
        ];
        if (!stage.FocusReferenceCamera(compactPoints, ReferenceCameraMotion.Immediate)
            || !CameraTransitionNear(stage.ReferenceZoomScale, 1)
            || !CameraTransitionNear(stage.ReferenceDistance, originalDistance)
            || !CameraTransitionNear(stage.ReferenceTargetX, 300)
            || !CameraTransitionNear(stage.ReferenceTargetY, 200)
            || !CameraTransitionNear(stage.ReferenceTargetZ, 300))
        {
            throw new InvalidOperationException(
                "Orthographic camera focus did not preserve 100% zoom, distance, and the scene-coordinate target.");
        }
        AssertReferenceCameraFocusVisible(stage, compactPoints, "compact orthographic focus");

        Vector3[] widePoints =
        [
            new(-20_000, -500, -250),
            new(20_000, 500, 250)
        ];
        if (!stage.FocusReferenceCamera(widePoints, ReferenceCameraMotion.Immediate)
            || stage.ReferenceZoomScale >= 1
            || stage.ReferenceZoomScale <= 0)
        {
            throw new InvalidOperationException(
                "Orthographic camera focus did not fit down an object that exceeded the 100% viewport.");
        }
        AssertReferenceCameraFocusVisible(stage, widePoints, "wide orthographic focus");

        definition.Camera.Projection = CameraProjection.Perspective;
        stage.ConfigureReferenceView(definition, SceneDimension.ThreeD);
        stage.SetReferenceCameraOrientation(0.42f, -0.27f);
        var yaw = stage.ReferenceYaw;
        var pitch = stage.ReferencePitch;
        Vector3[] perspectivePoints =
        [
            new(-2_000, -1_000, -500),
            new(-2_000, 1_000, 500),
            new(2_000, -1_000, 500),
            new(2_000, 1_000, -500)
        ];
        if (!stage.FocusReferenceCamera(perspectivePoints, ReferenceCameraMotion.Immediate)
            || !CameraTransitionNear(stage.ReferenceZoomScale, 1)
            || CameraTransitionAngularDistance(stage.ReferenceYaw, yaw) > 0.0001f
            || !CameraTransitionNear(stage.ReferencePitch, pitch))
        {
            throw new InvalidOperationException(
                "Perspective camera focus did not preserve orientation while solving a 100% camera distance.");
        }
        AssertReferenceCameraFocusVisible(stage, perspectivePoints, "perspective focus");

        Vector3[] enormousPoints =
        [
            new(-100_000, 0, 0),
            new(100_000, 0, 0)
        ];
        stage.SetReferenceCameraOrientation(0, 0);
        if (!stage.FocusReferenceCamera(enormousPoints, ReferenceCameraMotion.Immediate)
            || !CameraTransitionNear(stage.ReferenceDistance, 80_000, 0.1f)
            || stage.ReferenceZoomScale >= 1
            || stage.ReferenceZoomScale <= 0)
        {
            throw new InvalidOperationException(
                "Perspective camera focus did not fit down after reaching the supported camera-distance limit.");
        }
        AssertReferenceCameraFocusVisible(stage, enormousPoints, "distance-limited perspective focus");

        var stable = stage.CaptureReferenceCameraFrameForPersistence();
        if (stage.FocusReferenceCamera([], ReferenceCameraMotion.Immediate)
            || stage.FocusReferenceCamera([new Vector3(float.NaN, 0, 0)], ReferenceCameraMotion.Immediate)
            || StageControl.TryResolveReferenceCameraFocusFrame(
                compactPoints,
                Size.Empty,
                CameraProjection.Orthographic,
                stable,
                out _))
        {
            throw new InvalidOperationException("Invalid camera-focus inputs produced a camera target.");
        }
        AssertReferenceCameraFrameNear(
            stage.CaptureReferenceCameraFrameForPersistence(),
            stable,
            "invalid focus input");
    }

    private static void RunReferenceCameraFocusTransitionRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        using var stage = new StageControl(scene) { Size = new Size(960, 640) };
        var definition = new SceneDefinition { Dimension = SceneDimension.ThreeD };
        definition.Camera.Projection = CameraProjection.Perspective;
        stage.ConfigureReferenceView(definition, SceneDimension.ThreeD);
        stage.ResetReferenceCameraView();
        stage.SetReferenceCameraOrientation(0.58f, -0.31f);
        var from = stage.CaptureReferenceCameraFrameForPersistence();
        Vector3[] firstPoints =
        [
            new(2_000, -1_500, -300),
            new(8_000, 2_500, 900)
        ];
        if (!stage.FocusReferenceCamera(firstPoints, ReferenceCameraMotion.Animated))
        {
            throw new InvalidOperationException("Camera focus did not accept a valid animated target.");
        }
        var firstTarget = stage.CaptureReferenceCameraFrameForPersistence();
        if (!UiMotion.AnimationsEnabled)
        {
            if (stage.ReferenceCameraTransitionActive)
            {
                throw new InvalidOperationException("Reduced-motion camera focus retained an active transition.");
            }
            AssertReferenceCameraFrameNear(firstTarget, stage.CaptureReferenceCameraFrameForPersistence(), "focus target");
            return;
        }

        if (!stage.ReferenceCameraTransitionActive
            || CameraTransitionAngularDistance(firstTarget.Yaw, from.Yaw) > 0.0001f
            || !CameraTransitionNear(firstTarget.Pitch, from.Pitch)
            || !stage.AdvanceReferenceCameraTransition(80))
        {
            throw new InvalidOperationException(
                "Animated camera focus did not preserve orientation or expose an intermediate frame.");
        }
        var presented = new ReferenceCameraFrame(
            stage.ReferenceYaw,
            stage.ReferencePitch,
            stage.ReferenceDistance,
            stage.ReferenceZoomScale,
            stage.ReferenceTargetX,
            stage.ReferenceTargetY,
            stage.ReferenceTargetZ,
            stage.ReferenceProjectionBlend);
        Vector3[] redirectedPoints =
        [
            new(-9_000, -2_000, -500),
            new(-3_000, 3_000, 700)
        ];
        if (!stage.FocusReferenceCamera(redirectedPoints, ReferenceCameraMotion.Animated)
            || !stage.ReferenceCameraTransitionActive)
        {
            throw new InvalidOperationException("Camera focus could not redirect an active transition.");
        }
        var redirectedStart = new ReferenceCameraFrame(
            stage.ReferenceYaw,
            stage.ReferencePitch,
            stage.ReferenceDistance,
            stage.ReferenceZoomScale,
            stage.ReferenceTargetX,
            stage.ReferenceTargetY,
            stage.ReferenceTargetZ,
            stage.ReferenceProjectionBlend);
        if (CameraTransitionAngularDistance(redirectedStart.Yaw, presented.Yaw) > 0.01f
            || Math.Abs(redirectedStart.Pitch - presented.Pitch) > 0.01f
            || !CameraTransitionBetween(redirectedStart.Distance, presented.Distance, firstTarget.Distance)
            || !CameraTransitionBetween(redirectedStart.ZoomScale, presented.ZoomScale, firstTarget.ZoomScale)
            || !CameraTransitionBetween(redirectedStart.TargetX, presented.TargetX, firstTarget.TargetX)
            || !CameraTransitionBetween(redirectedStart.TargetY, presented.TargetY, firstTarget.TargetY)
            || !CameraTransitionBetween(redirectedStart.TargetZ, presented.TargetZ, firstTarget.TargetZ)
            || !CameraTransitionBetween(
                redirectedStart.ProjectionBlend,
                presented.ProjectionBlend,
                firstTarget.ProjectionBlend))
        {
            throw new InvalidOperationException(
                "Redirecting an active camera-focus transition caused a presentation discontinuity.");
        }
        var redirectedTarget = stage.CaptureReferenceCameraFrameForPersistence();
        stage.CompleteReferenceCameraTransition();
        AssertReferenceCameraFrameNear(
            stage.CaptureReferenceCameraFrameForPersistence(),
            redirectedTarget,
            "focus completion");
        AssertReferenceCameraFocusVisible(stage, redirectedPoints, "redirected perspective focus");
    }

    private static void AssertReferenceCameraFocusVisible(
        StageControl stage,
        IReadOnlyList<Vector3> scenePoints,
        string checkpoint)
    {
        var padding = Math.Max(16f, Math.Min(stage.Width, stage.Height) * 0.05f);
        foreach (var point in scenePoints)
        {
            if (!stage.TryProjectScenePosition(point, out var screen, out var depth)
                || depth < StageControl.ReferenceNearPlane
                || screen.X < padding - 0.5f
                || screen.X > stage.Width - padding + 0.5f
                || screen.Y < padding - 0.5f
                || screen.Y > stage.Height - padding + 0.5f)
            {
                throw new InvalidOperationException(
                    $"Reference camera {checkpoint} left a focus point outside the padded viewport: "
                    + $"point={point}, screen={screen}, depth={depth:0.###}.");
            }
        }
    }

    private static void RunReferenceCameraProjectionBlendRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        using var stage = new StageControl(scene) { Size = new Size(800, 600) };
        var definition = new SceneDefinition { Dimension = SceneDimension.ThreeD };
        definition.Camera.Projection = CameraProjection.Orthographic;
        stage.ConfigureReferenceView(definition, SceneDimension.ThreeD);
        stage.ResetReferenceCameraView();
        stage.SetReferenceCameraOrientation(0, 0);

        if (!CameraTransitionNear(stage.ReferenceProjectionBlend, 0)
            || !CameraTransitionNear(stage.ReferencePerspectiveScale(6_000), 1)
            || !CameraTransitionNear(stage.ReferencePerspectiveScale(24_000), 1))
        {
            throw new InvalidOperationException("The reference camera did not begin from a stable orthographic projection.");
        }

        definition.Camera.Projection = CameraProjection.Perspective;
        stage.ConfigureReferenceView(
            definition,
            SceneDimension.ThreeD,
            ReferenceCameraMotion.Animated);

        if (!UiMotion.AnimationsEnabled)
        {
            if (stage.ReferenceCameraTransitionActive
                || !CameraTransitionNear(stage.ReferenceProjectionBlend, 1))
            {
                throw new InvalidOperationException(
                    "Reduced-motion camera configuration did not complete the projection change immediately.");
            }
            return;
        }

        if (!stage.ReferenceCameraTransitionActive
            || !stage.AdvanceReferenceCameraTransition(110)
            || !CameraTransitionNear(stage.ReferenceProjectionBlend, 0.5f, 0.01f))
        {
            throw new InvalidOperationException(
                "The projection transition did not expose a deterministic orthographic/perspective midpoint.");
        }

        var nearScale = stage.ReferencePerspectiveScale(6_000);
        var focalScale = stage.ReferencePerspectiveScale(StageControl.ReferencePerspectiveFocalLength);
        var farScale = stage.ReferencePerspectiveScale(24_000);
        if (!CameraTransitionNear(nearScale, 4f / 3f, 0.01f)
            || !CameraTransitionNear(focalScale, 1, 0.001f)
            || !CameraTransitionNear(farScale, 2f / 3f, 0.01f))
        {
            throw new InvalidOperationException(
                "The mixed projection did not preserve the focal plane while interpolating near/far perspective scale.");
        }

        var scenePoint = new Vector3(750, -300, 1_000);
        if (!stage.TryProjectScenePosition(scenePoint, out var projected, out _)
            || !stage.TryGetReferenceRay(Point.Round(projected), out var ray)
            || ray.Direction.Z <= 0)
        {
            throw new InvalidOperationException("The mixed projection did not produce a usable matching pick ray.");
        }
        var rayAmount = (scenePoint.Z - ray.Origin.Z) / ray.Direction.Z;
        var rayHit = ray.Origin + ray.Direction * rayAmount;
        if (Vector2.Distance(
                new Vector2(rayHit.X, rayHit.Y),
                new Vector2(scenePoint.X, scenePoint.Y)) > 25)
        {
            throw new InvalidOperationException("The mixed projection pick ray diverged from the projected scene point.");
        }

        stage.CompleteReferenceCameraTransition();
        if (stage.ReferenceCameraTransitionActive
            || !CameraTransitionNear(stage.ReferenceProjectionBlend, 1)
            || stage.EffectiveReferenceProjection != CameraProjection.Perspective)
        {
            throw new InvalidOperationException("Explicit completion did not commit the perspective projection target.");
        }
    }

    private static void RunReferenceCamera2DRendererHandoffRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        using var stage = new StageControl(scene) { Size = new Size(900, 620) };
        var view = stage.CaptureViewState() with
        {
            CameraX = 240,
            CameraY = -180,
            Zoom = 1.7f
        };
        stage.RestoreViewState(view);

        var definition = new SceneDefinition { Dimension = SceneDimension.ThreeD };
        definition.Camera.Projection = CameraProjection.Orthographic;
        stage.ConfigureReferenceView(definition, SceneDimension.TwoD);
        var scenePoint = new Vector3(475, -325, 0);
        var ordinaryStart = stage.WorldToScreen(scenePoint.X, scenePoint.Y);

        stage.ConfigureReferenceView(
            definition,
            SceneDimension.ThreeD,
            ReferenceCameraMotion.Animated);
        if (!UiMotion.AnimationsEnabled)
        {
            stage.ConfigureReferenceView(definition, SceneDimension.TwoD, ReferenceCameraMotion.Animated);
            if (stage.ReferenceCameraTransitionActive || stage.RendersReferenceProjection)
            {
                throw new InvalidOperationException(
                    "Reduced-motion 2D camera handoff retained a temporary reference renderer.");
            }
            return;
        }

        if (!stage.ReferenceCameraTransitionActive
            || !stage.RendersReferenceProjection
            || !stage.TryProjectScenePosition(scenePoint, out var temporaryStart, out _)
            || CameraTransitionScreenDistance(ordinaryStart, temporaryStart) >= 0.5f
            || !CameraTransitionNear(stage.PlanarWorldGridOpacity, stage.WorldGridOpacity)
            || !CameraTransitionNear(stage.ReferenceWorldGridOpacity, 0))
        {
            throw new InvalidOperationException(
                "Entering the temporary reference renderer changed the ordinary 2D camera's screen-space endpoint.");
        }
        stage.AdvanceReferenceCameraTransition(110);
        if (!CameraTransitionNear(stage.PlanarWorldGridOpacity, stage.WorldGridOpacity * 0.5f, 0.002f)
            || !CameraTransitionNear(stage.ReferenceWorldGridOpacity, stage.WorldGridOpacity * 0.5f, 0.002f))
        {
            throw new InvalidOperationException("The 2D/3D transition did not cross-fade its planar and reference grids.");
        }
        stage.CompleteReferenceCameraTransition();

        stage.ConfigureReferenceView(
            definition,
            SceneDimension.TwoD,
            ReferenceCameraMotion.Animated);
        if (!stage.ReferenceCameraTransitionActive
            || stage.UsesReferenceProjection
            || !stage.RendersReferenceProjection
            || !stage.AdvanceReferenceCameraTransition(219.9)
            || !stage.TryProjectScenePosition(scenePoint, out var temporaryEnd, out _)
            || stage.ReferenceWorldGridOpacity > 0.001f
            || Math.Abs(stage.PlanarWorldGridOpacity - stage.WorldGridOpacity) > 0.001f)
        {
            throw new InvalidOperationException(
                "Leaving 3D did not retain a measurable temporary reference-renderer endpoint.");
        }

        var ordinaryEnd = stage.WorldToScreen(scenePoint.X, scenePoint.Y);
        if (CameraTransitionScreenDistance(temporaryEnd, ordinaryEnd) >= 0.5f)
        {
            throw new InvalidOperationException(
                "The temporary reference renderer did not converge to the ordinary 2D screen-space endpoint.");
        }

        stage.CompleteReferenceCameraTransition();
        var releasedEnd = stage.WorldToScreen(scenePoint.X, scenePoint.Y);
        if (stage.ReferenceCameraTransitionActive
            || stage.UsesReferenceProjection
            || stage.RendersReferenceProjection
            || CameraTransitionScreenDistance(temporaryEnd, releasedEnd) >= 0.5f)
        {
            throw new InvalidOperationException(
                "Completing the 2D handoff changed its endpoint or failed to release the reference renderer.");
        }
    }

    private static void RunReferenceCameraCompositionTransitionRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        scene.AddObject(
            0,
            PointF.Empty,
            new SizeF(600, 400),
            0,
            0,
            Color.CornflowerBlue,
            Color.Transparent,
            12,
            ShapeKind.Rectangle);
        using var stage = new StageControl(scene) { Size = new Size(800, 600) };
        var definition = new SceneDefinition { Dimension = SceneDimension.ThreeD };
        definition.Camera.Projection = CameraProjection.Orthographic;
        var identity = CameraTransitionCompositionResult(Matrix4x4.Identity);
        var spatial = CameraTransitionCompositionResult(Matrix4x4.CreateTranslation(0, 0, 500));

        stage.SetSceneCompositionResult(identity, scene);
        stage.ConfigureReferenceView(definition, SceneDimension.ThreeD);
        stage.ConfigureReferenceView(definition, SceneDimension.TwoD, ReferenceCameraMotion.Animated);
        if (UiMotion.AnimationsEnabled && !stage.ReferenceCameraTransitionActive)
        {
            throw new InvalidOperationException("The composition-transition regression could not begin a 3D-to-2D handoff.");
        }
        var spatialSwapFrame = PresentedFrame();
        stage.SetSceneCompositionResult(spatial, scene);
        if (stage.ReferenceCameraTransitionActive != UiMotion.AnimationsEnabled
            || !stage.UsesReferenceProjection
            || !stage.RendersReferenceProjection
            || stage.EffectiveReferenceProjection != CameraProjection.Orthographic)
        {
            throw new InvalidOperationException(
                "Enabling spatial poses disturbed the stable front-reference handoff.");
        }
        AssertReferenceCameraFrameNear(PresentedFrame(), spatialSwapFrame, "spatial-pose composition swap");

        stage.ConfigureReferenceView(definition, SceneDimension.ThreeD);
        stage.SetSceneCompositionResult(spatial, scene);
        stage.ConfigureReferenceView(definition, SceneDimension.TwoD, ReferenceCameraMotion.Animated);
        if (UiMotion.AnimationsEnabled && !stage.ReferenceCameraTransitionActive)
        {
            throw new InvalidOperationException("The spatial composition did not begin its reference-front handoff.");
        }
        var identitySwapFrame = PresentedFrame();
        stage.SetSceneCompositionResult(identity, scene);
        if (stage.ReferenceCameraTransitionActive != UiMotion.AnimationsEnabled
            || !stage.UsesReferenceProjection
            || !stage.RendersReferenceProjection
            || stage.EffectiveReferenceProjection != CameraProjection.Orthographic)
        {
            throw new InvalidOperationException(
                "Removing spatial poses disturbed the stable front-reference handoff.");
        }
        AssertReferenceCameraFrameNear(PresentedFrame(), identitySwapFrame, "identity-pose composition swap");

        ReferenceCameraFrame PresentedFrame() => new(
            stage.ReferenceYaw,
            stage.ReferencePitch,
            stage.ReferenceDistance,
            stage.ReferenceZoomScale,
            stage.ReferenceTargetX,
            stage.ReferenceTargetY,
            stage.ReferenceTargetZ,
            stage.ReferenceProjectionBlend);
    }

    private static SceneCompositionResult CameraTransitionCompositionResult(Matrix4x4 transform)
    {
        return new SceneCompositionResult(
            [new SceneCompositionObjectOwner("instance", "drawing")],
            [new SceneCompositionObjectPose(transform)]);
    }

    private static void RunReferenceCameraStageTransitionRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        using var stage = new StageControl(scene) { Size = new Size(960, 640) };
        var definition = new SceneDefinition { Dimension = SceneDimension.ThreeD };
        definition.Camera.Projection = CameraProjection.Orthographic;
        stage.ConfigureReferenceView(definition, SceneDimension.ThreeD);
        stage.ResetReferenceCameraView();
        stage.SetReferenceCameraOrientation(0, 0);

        stage.SetReferenceCameraOrientation(1.2f, 0.6f, ReferenceCameraMotion.Animated);
        if (UiMotion.AnimationsEnabled)
        {
            if (!stage.ReferenceCameraTransitionActive
                || !stage.AdvanceReferenceCameraTransition(90)
                || stage.ReferenceYaw <= 0
                || stage.ReferenceYaw >= 1.2f
                || stage.ReferencePitch <= 0
                || stage.ReferencePitch >= 0.6f)
            {
                throw new InvalidOperationException("Explicit camera advancement did not present an intermediate orientation.");
            }
        }
        stage.CompleteReferenceCameraTransition();
        if (stage.ReferenceCameraTransitionActive
            || CameraTransitionAngularDistance(stage.ReferenceYaw, 1.2f) > 0.001f
            || !CameraTransitionNear(stage.ReferencePitch, 0.6f))
        {
            throw new InvalidOperationException("Explicit camera completion did not commit the requested orientation.");
        }

        stage.ConfigureReferenceView(definition, SceneDimension.ThreeD);
        stage.ConfigureReferenceView(
            definition,
            SceneDimension.TwoD,
            ReferenceCameraMotion.Animated);
        if (UiMotion.AnimationsEnabled)
        {
            if (!stage.ReferenceCameraTransitionActive
                || stage.UsesReferenceProjection
                || !stage.RendersReferenceProjection)
            {
                throw new InvalidOperationException(
                    "The 3D-to-2D transition did not retain the temporary reference renderer after changing logical view state.");
            }
            stage.CompleteReferenceCameraTransition();
        }
        if (stage.ReferenceCameraTransitionActive
            || stage.ReferenceDimension != SceneDimension.TwoD
            || stage.UsesReferenceProjection
            || stage.RendersReferenceProjection)
        {
            throw new InvalidOperationException("The completed 2D transition did not return to the ordinary 2D renderer.");
        }

        stage.ConfigureReferenceView(definition, SceneDimension.ThreeD);
        stage.SetReferenceCameraOrientation(0, 0);
        stage.SetReferenceCameraOrientation(2.2f, 0.4f, ReferenceCameraMotion.Animated);
        if (UiMotion.AnimationsEnabled)
        {
            stage.AdvanceReferenceCameraTransition(70);
            var presentedYaw = stage.ReferenceYaw;
            var presentedPitch = stage.ReferencePitch;
            stage.SetReferenceCameraOrientation(-1.4f, -0.3f, ReferenceCameraMotion.Animated);
            if (!stage.ReferenceCameraTransitionActive
                || CameraTransitionAngularDistance(stage.ReferenceYaw, presentedYaw) > 0.03f
                || Math.Abs(stage.ReferencePitch - presentedPitch) > 0.03f)
            {
                throw new InvalidOperationException(
                    "Retargeting an active camera transition caused a presentation discontinuity: "
                    + $"before=({presentedYaw:0.####},{presentedPitch:0.####}), "
                    + $"after=({stage.ReferenceYaw:0.####},{stage.ReferencePitch:0.####}).");
            }
            stage.AdvanceReferenceCameraTransition(40);
        }
        stage.CompleteReferenceCameraTransition();
        if (CameraTransitionAngularDistance(stage.ReferenceYaw, -1.4f) > 0.001f
            || !CameraTransitionNear(stage.ReferencePitch, -0.3f))
        {
            throw new InvalidOperationException("The redirected camera transition did not complete at its latest target.");
        }

        stage.ResetReferenceCameraView();
        stage.SetReferenceCameraOrientation(1.1f, 0.2f, ReferenceCameraMotion.Animated);
        var dollyInterruptedAnimation = stage.ReferenceCameraTransitionActive;
        var dollyFactor = MathF.Pow(StageControl.ReferenceWheelDollyBase, 120f / 120f);
        stage.DollyReferenceCamera(120);
        if ((UiMotion.AnimationsEnabled && !dollyInterruptedAnimation)
            || stage.ReferenceCameraTransitionActive
            || CameraTransitionAngularDistance(stage.ReferenceYaw, 1.1f) > 0.001f
            || !CameraTransitionNear(stage.ReferencePitch, 0.2f)
            || !CameraTransitionNear(stage.ReferenceDistance, 12_000, 0.1f)
            || !CameraTransitionNear(stage.ReferenceZoomScale, 1f / dollyFactor, 0.0001f))
        {
            throw new InvalidOperationException(
                "Orthographic reference camera Dolly did not finish the active transition before applying its immediate zoom change.");
        }

        stage.ResetReferenceCameraView();
        stage.SetReferenceCameraOrientation(-1.25f, -0.35f, ReferenceCameraMotion.Animated);
        var bindInterruptedAnimation = stage.ReferenceCameraTransitionActive;
        var replacement = new VectorScene();
        replacement.CreateEmpty();
        stage.BindScene(replacement);
        if ((UiMotion.AnimationsEnabled && !bindInterruptedAnimation)
            || stage.ReferenceCameraTransitionActive
            || !ReferenceEquals(stage.Scene, replacement)
            || CameraTransitionAngularDistance(stage.ReferenceYaw, -1.25f) > 0.001f
            || !CameraTransitionNear(stage.ReferencePitch, -0.35f))
        {
            throw new InvalidOperationException(
                "Binding a replacement scene did not finish and clear the active camera transition deterministically.");
        }
    }

    private static void AssertReferenceCameraFrameNear(
        ReferenceCameraFrame actual,
        ReferenceCameraFrame expected,
        string checkpoint)
    {
        if (CameraTransitionAngularDistance(actual.Yaw, expected.Yaw) <= 0.0001f
            && CameraTransitionNear(actual.Pitch, expected.Pitch)
            && CameraTransitionNear(actual.Distance, expected.Distance, 0.05f)
            && CameraTransitionNear(actual.ZoomScale, expected.ZoomScale)
            && CameraTransitionNear(actual.TargetX, expected.TargetX)
            && CameraTransitionNear(actual.TargetY, expected.TargetY)
            && CameraTransitionNear(actual.TargetZ, expected.TargetZ)
            && CameraTransitionNear(actual.ProjectionBlend, expected.ProjectionBlend))
        {
            return;
        }

        throw new InvalidOperationException($"Reference camera interpolation changed its {checkpoint}.");
    }

    private static float CameraTransitionAngularDistance(float left, float right)
    {
        var difference = left - right;
        while (difference > MathF.PI) difference -= MathF.Tau;
        while (difference < -MathF.PI) difference += MathF.Tau;
        return Math.Abs(difference);
    }

    private static bool CameraTransitionNear(float actual, float expected, float tolerance = 0.001f)
    {
        return Math.Abs(actual - expected) <= tolerance;
    }

    private static bool CameraTransitionBetween(
        float actual,
        float first,
        float second,
        float tolerance = 0.01f)
    {
        return actual >= Math.Min(first, second) - tolerance
            && actual <= Math.Max(first, second) + tolerance;
    }

    private static float CameraTransitionScreenDistance(PointF left, PointF right)
    {
        var dx = left.X - right.X;
        var dy = left.Y - right.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
