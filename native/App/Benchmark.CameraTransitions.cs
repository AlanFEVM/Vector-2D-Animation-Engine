using System.Numerics;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunReferenceCameraTransitionRegression()
    {
        RunReferenceCameraFrameInterpolationRegression();
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
        stage.SetSceneCompositionResult(spatial, scene);
        if (stage.ReferenceCameraTransitionActive
            || !stage.UsesReferenceProjection
            || !stage.RendersReferenceProjection)
        {
            throw new InvalidOperationException(
                "Enabling spatial poses did not terminate the stale ordinary-2D camera endpoint.");
        }

        stage.ConfigureReferenceView(definition, SceneDimension.ThreeD);
        stage.SetSceneCompositionResult(spatial, scene);
        stage.ConfigureReferenceView(definition, SceneDimension.TwoD, ReferenceCameraMotion.Animated);
        if (UiMotion.AnimationsEnabled && !stage.ReferenceCameraTransitionActive)
        {
            throw new InvalidOperationException("The spatial composition did not begin its reference-front handoff.");
        }
        stage.SetSceneCompositionResult(identity, scene);
        if (stage.ReferenceCameraTransitionActive
            || stage.UsesReferenceProjection
            || stage.RendersReferenceProjection)
        {
            throw new InvalidOperationException(
                "Removing spatial poses did not terminate the stale reference-front camera endpoint.");
        }
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
        stage.DollyReferenceCamera(120);
        if ((UiMotion.AnimationsEnabled && !dollyInterruptedAnimation)
            || stage.ReferenceCameraTransitionActive
            || CameraTransitionAngularDistance(stage.ReferenceYaw, 1.1f) > 0.001f
            || !CameraTransitionNear(stage.ReferencePitch, 0.2f)
            || !CameraTransitionNear(stage.ReferenceDistance, 10_800, 0.1f))
        {
            throw new InvalidOperationException(
                "Reference camera Dolly did not finish the active transition before applying its immediate distance change.");
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

    private static float CameraTransitionScreenDistance(PointF left, PointF right)
    {
        var dx = left.X - right.X;
        var dy = left.Y - right.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
