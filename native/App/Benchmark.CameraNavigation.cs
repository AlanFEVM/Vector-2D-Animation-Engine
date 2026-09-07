using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private const uint CameraNavigationWmKeyDown = 0x0100;
    private const uint CameraNavigationWmKeyUp = 0x0101;
    private const uint CameraNavigationWmLeftButtonDown = 0x0201;
    private const uint CameraNavigationWmLeftButtonUp = 0x0202;

    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostCameraNavigationMessage(
        IntPtr window,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    private static void RunReferenceCameraKeyboardNavigationFixtureRegression()
    {
        const BindingFlags privateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        var workspaceTabsField = typeof(MainForm).GetField("_workspaceTabs", privateInstance)
            ?? throw new InvalidOperationException("Camera keyboard fixture could not find workspace tabs.");
        var dimensionButtonField = typeof(MainForm).GetField("_sceneDimensionButton", privateInstance)
            ?? throw new InvalidOperationException("Camera keyboard fixture could not find the Scene dimension button.");
        var projectionButtonField = typeof(MainForm).GetField("_sceneProjectionButton", privateInstance)
            ?? throw new InvalidOperationException("Camera keyboard fixture could not find the Scene projection button.");
        var projectField = typeof(MainForm).GetField("_project", privateInstance)
            ?? throw new InvalidOperationException("Camera keyboard fixture could not find the project.");
        var stageField = typeof(MainForm).GetField("_stage", privateInstance)
            ?? throw new InvalidOperationException("Camera keyboard fixture could not find the Stage.");
        var undoStackField = typeof(MainForm).GetField("_sceneTimelineUndoStack", privateInstance)
            ?? throw new InvalidOperationException("Camera keyboard fixture could not inspect Scene undo state.");

        using var form = new MainForm
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-30_000, -30_000),
            Size = new Size(1280, 800)
        };
        var workspaceTabs = workspaceTabsField.GetValue(form) as WorkspaceTabs
            ?? throw new InvalidOperationException("Camera keyboard fixture lost workspace tabs.");
        var dimensionButton = dimensionButtonField.GetValue(form) as Button
            ?? throw new InvalidOperationException("Camera keyboard fixture lost the Scene dimension button.");
        var projectionButton = projectionButtonField.GetValue(form) as Button
            ?? throw new InvalidOperationException("Camera keyboard fixture lost the Scene projection button.");
        var project = projectField.GetValue(form) as VectorProject
            ?? throw new InvalidOperationException("Camera keyboard fixture lost the project.");
        var stage = stageField.GetValue(form) as StageControl
            ?? throw new InvalidOperationException("Camera keyboard fixture lost the Stage.");

        int UndoCount()
        {
            var stack = undoStackField.GetValue(form)
                ?? throw new InvalidOperationException("Camera keyboard fixture lost the Scene undo stack.");
            return stack.GetType().GetProperty("Count")?.GetValue(stack) is int count
                ? count
                : throw new InvalidOperationException("Camera keyboard fixture could not read Scene undo state.");
        }

        form.Show();
        form.Activate();
        Application.DoEvents();
        workspaceTabs.SelectedView = WorkspaceView.SceneEditor;
        Application.DoEvents();
        if (project.Scenes.Count == 0 || dimensionButton.Text != "2D")
        {
            throw new InvalidOperationException("Camera keyboard fixture could not enter Scene Building 2D.");
        }
        RunReferenceCameraMiddleDragRegression(form, stage, project.Scenes[0]);
        RunReferenceCameraProjectionNavigationRegression();
        RunReferenceCameraLookAroundRegression();
        if (!dimensionButton.Focus())
        {
            throw new InvalidOperationException("Camera keyboard fixture could not focus the Scene dimension button.");
        }
        dimensionButton.PerformClick();
        Application.DoEvents();
        if (dimensionButton.Text != "3D"
            || stage.ReferenceDimension != SceneDimension.ThreeD
            || !stage.ContainsFocus)
        {
            throw new InvalidOperationException("Entering Scene Building 3D did not transfer keyboard focus to the Stage.");
        }

        var projection = project.Scenes[0].Camera.Projection;
        if (!projectionButton.Focus())
        {
            throw new InvalidOperationException("Camera keyboard fixture could not focus the Scene projection button.");
        }
        projectionButton.PerformClick();
        Application.DoEvents();
        if (project.Scenes[0].Camera.Projection == projection || !stage.ContainsFocus)
        {
            throw new InvalidOperationException("Changing Scene projection did not transfer keyboard focus to the Stage.");
        }

        RunReferenceCameraKeyboardNavigationRegression(
            form,
            stage,
            project.Scenes[0],
            UndoCount);
        Console.WriteLine("reference_camera_middle_drag=ok");
        Console.WriteLine("reference_camera_keyboard_navigation=ok");
    }

    private static void RunReferenceCameraMiddleDragRegression(
        MainForm form,
        StageControl stage,
        SceneDefinition scene)
    {
        const BindingFlags privateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        var mouseDown = typeof(MainForm).GetMethod("StageMouseDown", privateInstance)
            ?? throw new InvalidOperationException("Camera middle-drag regression could not begin Stage input.");
        var mouseMove = typeof(MainForm).GetMethod("StageMouseMove", privateInstance)
            ?? throw new InvalidOperationException("Camera middle-drag regression could not update Stage input.");
        var mouseUp = typeof(MainForm).GetMethod("StageMouseUp", privateInstance)
            ?? throw new InvalidOperationException("Camera middle-drag regression could not complete Stage input.");
        var endViewDrag = typeof(MainForm).GetMethod("EndGlobalViewDrag", privateInstance)
            ?? throw new InvalidOperationException("Camera middle-drag regression could not clean up Stage input.");
        var lastMouseField = typeof(MainForm).GetField("_lastMouse", privateInstance)
            ?? throw new InvalidOperationException("Camera middle-drag regression could not inspect pointer state.");
        var viewPanningField = typeof(MainForm).GetField("_viewPanning", privateInstance)
            ?? throw new InvalidOperationException("Camera middle-drag regression could not inspect canvas pan state.");
        var viewZoomingField = typeof(MainForm).GetField("_viewZooming", privateInstance)
            ?? throw new InvalidOperationException("Camera middle-drag regression could not inspect canvas zoom state.");
        var viewOrbitingField = typeof(MainForm).GetField("_viewOrbiting", privateInstance)
            ?? throw new InvalidOperationException("Camera middle-drag regression could not inspect orbit state.");
        var referencePanningField = typeof(MainForm).GetField("_viewReferencePanning", privateInstance)
            ?? throw new InvalidOperationException("Camera middle-drag regression could not inspect reference pan state.");
        var referenceZoomingField = typeof(MainForm).GetField("_viewReferenceZooming", privateInstance)
            ?? throw new InvalidOperationException("Camera middle-drag regression could not inspect reference zoom state.");

        var originalDimension = stage.ReferenceDimension;
        var originalDirection = stage.Reference2DViewDirection;
        var originalView = stage.CaptureViewState();
        var start = new Point(
            Math.Max(24, stage.ClientSize.Width / 2 - 40),
            Math.Max(24, stage.ClientSize.Height / 2 - 30));

        bool Active(FieldInfo field) => field.GetValue(form) is true;

        void InvokePointer(MethodInfo method, MouseButtons button, Point location, int clicks)
        {
            method.Invoke(form, [stage, new MouseEventArgs(button, clicks, location.X, location.Y, 0)]);
        }

        void AssertDragState(bool referencePan, bool orbit, string operation)
        {
            if (Active(referencePanningField) != referencePan
                || Active(viewOrbitingField) != orbit
                || Active(referenceZoomingField)
                || Active(viewPanningField)
                || Active(viewZoomingField)
                || !stage.Capture
                || lastMouseField.GetValue(form) is not Point)
            {
                throw new InvalidOperationException($"{operation} entered the wrong middle-drag state.");
            }
        }

        void AssertDragEnded(string operation)
        {
            if (Active(referencePanningField)
                || Active(viewOrbitingField)
                || Active(referenceZoomingField)
                || Active(viewPanningField)
                || Active(viewZoomingField)
                || stage.Capture
                || lastMouseField.GetValue(form) is not null)
            {
                throw new InvalidOperationException($"{operation} did not release capture and clear its drag state.");
            }
        }

        try
        {
            stage.ConfigureReferenceView(scene, SceneDimension.TwoD, ReferenceCameraMotion.Immediate);
            foreach (var direction in new[]
                     {
                         ReferenceViewDirection.Front,
                         ReferenceViewDirection.Right,
                         ReferenceViewDirection.Top
                     })
            {
                stage.ResetReferenceCameraView(ReferenceCameraMotion.Immediate);
                stage.SetReferenceViewDirection(direction, ReferenceCameraMotion.Immediate);
                var before = stage.CaptureReferenceCameraFrameForPersistence();
                if (!stage.TryProjectScenePosition(Vector3.Zero, out var projectedBefore, out _))
                {
                    throw new InvalidOperationException($"2D {direction} middle-pan regression could not project its reference point.");
                }

                var delta = direction switch
                {
                    ReferenceViewDirection.Front => new Point(37, -19),
                    ReferenceViewDirection.Right => new Point(-29, 23),
                    _ => new Point(31, 17)
                };
                var target = new Point(start.X + delta.X, start.Y + delta.Y);
                InvokePointer(mouseDown, MouseButtons.Middle, start, 1);
                AssertDragState(referencePan: true, orbit: false, $"2D {direction} middle pan");
                InvokePointer(mouseMove, MouseButtons.Middle, target, 0);
                InvokePointer(mouseUp, MouseButtons.Middle, target, 1);

                var after = stage.CaptureReferenceCameraFrameForPersistence();
                if (!stage.TryProjectScenePosition(Vector3.Zero, out var projectedAfter, out _)
                    || ReferenceTargetDistance(before, after) <= 0.01f
                    || !CameraParametersNear(before, after)
                    || Math.Abs(projectedAfter.X - projectedBefore.X - delta.X) > 0.1f
                    || Math.Abs(projectedAfter.Y - projectedBefore.Y - delta.Y) > 0.1f)
                {
                    throw new InvalidOperationException(
                        $"2D {direction} middle drag did not pan the orthographic reference camera by the pointer delta.");
                }
                AssertDragEnded($"2D {direction} middle pan");
            }

            stage.ConfigureReferenceView(scene, SceneDimension.ThreeD, ReferenceCameraMotion.Immediate);
            stage.SetReferenceCameraOrientation(0.31f, -0.22f, ReferenceCameraMotion.Immediate);
            var orbitBefore = stage.CaptureReferenceCameraFrameForPersistence();
            var orbitDelta = new Point(33, -17);
            var orbitTarget = new Point(start.X + orbitDelta.X, start.Y + orbitDelta.Y);
            InvokePointer(mouseDown, MouseButtons.Middle, start, 1);
            AssertDragState(referencePan: false, orbit: true, "3D middle orbit");
            InvokePointer(mouseMove, MouseButtons.Middle, orbitTarget, 0);
            InvokePointer(mouseUp, MouseButtons.Middle, orbitTarget, 1);

            var orbitAfter = stage.CaptureReferenceCameraFrameForPersistence();
            if (CameraTransitionAngularDistance(orbitBefore.Yaw + orbitDelta.X * 0.01f, orbitAfter.Yaw) > 0.0001f
                || Math.Abs(orbitAfter.Pitch - (orbitBefore.Pitch + orbitDelta.Y * 0.01f)) > 0.0001f
                || ReferenceTargetDistance(orbitBefore, orbitAfter) > 0.001f
                || Math.Abs(orbitAfter.Distance - orbitBefore.Distance) > 0.001f
                || Math.Abs(orbitAfter.ZoomScale - orbitBefore.ZoomScale) > 0.0001f)
            {
                throw new InvalidOperationException("3D middle drag no longer rotated the reference camera.");
            }
            AssertDragEnded("3D middle orbit");
        }
        finally
        {
            if (stage.Capture || lastMouseField.GetValue(form) is not null)
            {
                endViewDrag.Invoke(form, null);
            }
            stage.ConfigureReferenceView(scene, originalDimension, ReferenceCameraMotion.Immediate);
            if (originalDimension == SceneDimension.TwoD)
            {
                stage.SetReferenceViewDirection(originalDirection, ReferenceCameraMotion.Immediate);
            }
            stage.RestoreViewState(originalView);
        }
    }

    private static void RunReferenceCameraProjectionNavigationRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        using var stage = new StageControl(scene) { Size = new Size(800, 600) };
        var definition = new SceneDefinition { Dimension = SceneDimension.ThreeD };
        var panDelta = new Point(41, -23);
        var pixelDolly = 24f;

        definition.Camera.Projection = CameraProjection.Orthographic;
        stage.ConfigureReferenceView(definition, SceneDimension.ThreeD, ReferenceCameraMotion.Immediate);
        stage.ResetReferenceCameraView(ReferenceCameraMotion.Immediate);
        stage.SetReferenceCameraOrientation(0.42f, -0.28f, ReferenceCameraMotion.Immediate);
        var orthographicView = stage.CaptureViewState();
        foreach (var distance in new[] { 2_000f, 80_000f })
        {
            foreach (var zoomScale in new[] { 0.25f, 8f })
            {
                stage.RestoreViewState(
                    orthographicView with
                    {
                        ReferenceDistance = distance,
                        ReferenceZoomScale = zoomScale
                    });
                AssertReferenceCameraScreenDelta(
                    stage,
                    panDelta,
                    $"orthographic 3D pan at distance={distance}, zoom={zoomScale}");
            }
        }

        stage.RestoreViewState(orthographicView with { ReferenceDistance = 24_000 });
        var orthographicBeforeDolly = stage.CaptureReferenceCameraFrameForPersistence();
        var wheelFactor = MathF.Pow(StageControl.ReferenceWheelDollyBase, 120f / 120f);
        stage.DollyReferenceCamera(120);
        var orthographicAfterDolly = stage.CaptureReferenceCameraFrameForPersistence();
        if (!CameraTransitionNear(orthographicAfterDolly.Distance, orthographicBeforeDolly.Distance, 0.05f)
            || !CameraTransitionNear(
                orthographicAfterDolly.ZoomScale,
                orthographicBeforeDolly.ZoomScale / wheelFactor,
                0.0001f))
        {
            throw new InvalidOperationException(
                "Orthographic wheel Dolly changed camera distance instead of applying projection zoom.");
        }
        stage.DollyReferenceCamera(-120);
        AssertReferenceCameraFrameNear(
            stage.CaptureReferenceCameraFrameForPersistence(),
            orthographicBeforeDolly,
            "orthographic wheel Dolly reversal");

        stage.RestoreViewState(orthographicView);
        stage.DollyReferenceCamera(60);
        stage.DollyReferenceCamera(60);
        var splitWheel = stage.CaptureReferenceCameraFrameForPersistence();
        stage.RestoreViewState(orthographicView);
        stage.DollyReferenceCamera(120);
        var fullWheel = stage.CaptureReferenceCameraFrameForPersistence();
        AssertReferenceCameraFrameNear(splitWheel, fullWheel, "split wheel Dolly");

        stage.RestoreViewState(orthographicView);
        stage.DollyReferenceCamera(120);
        stage.DollyReferenceCamera(120);
        var twoWheelSteps = stage.CaptureReferenceCameraFrameForPersistence();
        stage.RestoreViewState(orthographicView);
        stage.DollyReferenceCamera(240);
        var combinedWheelStep = stage.CaptureReferenceCameraFrameForPersistence();
        AssertReferenceCameraFrameNear(twoWheelSteps, combinedWheelStep, "combined wheel Dolly");

        stage.RestoreViewState(orthographicView);
        var orthographicBeforePixelDolly = stage.CaptureReferenceCameraFrameForPersistence();
        stage.DollyReferenceCameraByPixels(pixelDolly);
        var orthographicAfterPixelDolly = stage.CaptureReferenceCameraFrameForPersistence();
        var pixelFactor = MathF.Exp(pixelDolly * 0.012f);
        if (!CameraTransitionNear(orthographicAfterPixelDolly.Distance, orthographicBeforePixelDolly.Distance, 0.05f)
            || !CameraTransitionNear(
                orthographicAfterPixelDolly.ZoomScale,
                orthographicBeforePixelDolly.ZoomScale / pixelFactor,
                0.0001f))
        {
            throw new InvalidOperationException(
                "Orthographic Ctrl-middle Dolly changed camera distance instead of applying pixel zoom.");
        }
        stage.DollyReferenceCameraByPixels(-pixelDolly);
        AssertReferenceCameraFrameNear(
            stage.CaptureReferenceCameraFrameForPersistence(),
            orthographicBeforePixelDolly,
            "orthographic pixel Dolly reversal");

        definition.Camera.Projection = CameraProjection.Perspective;
        stage.ConfigureReferenceView(definition, SceneDimension.ThreeD, ReferenceCameraMotion.Immediate);
        stage.ResetReferenceCameraView(ReferenceCameraMotion.Immediate);
        stage.SetReferenceCameraOrientation(0.42f, -0.28f, ReferenceCameraMotion.Immediate);
        var perspectiveView = stage.CaptureViewState();
        foreach (var distance in new[] { 2_000f, 80_000f })
        {
            foreach (var zoomScale in new[] { 0.25f, 8f })
            {
                stage.RestoreViewState(
                    perspectiveView with
                    {
                        ReferenceDistance = distance,
                        ReferenceZoomScale = zoomScale
                    });
                AssertReferenceCameraScreenDelta(
                    stage,
                    panDelta,
                    $"perspective 3D pan at distance={distance}, zoom={zoomScale}");
            }
        }

        stage.RestoreViewState(perspectiveView);
        var perspectiveBeforeDolly = stage.CaptureReferenceCameraFrameForPersistence();
        stage.DollyReferenceCamera(120);
        var perspectiveAfterDolly = stage.CaptureReferenceCameraFrameForPersistence();
        if (!CameraTransitionNear(
                perspectiveAfterDolly.Distance,
                perspectiveBeforeDolly.Distance * wheelFactor,
                0.05f)
            || !CameraTransitionNear(
                perspectiveAfterDolly.ZoomScale,
                perspectiveBeforeDolly.ZoomScale,
                0.0001f))
        {
            throw new InvalidOperationException(
                "Perspective wheel Dolly changed projection zoom instead of camera distance.");
        }
        stage.DollyReferenceCamera(-120);
        AssertReferenceCameraFrameNear(
            stage.CaptureReferenceCameraFrameForPersistence(),
            perspectiveBeforeDolly,
            "perspective wheel Dolly reversal");

        stage.RestoreViewState(perspectiveView);
        var perspectiveBeforePixelDolly = stage.CaptureReferenceCameraFrameForPersistence();
        stage.DollyReferenceCameraByPixels(pixelDolly);
        var perspectiveAfterPixelDolly = stage.CaptureReferenceCameraFrameForPersistence();
        if (!CameraTransitionNear(
                perspectiveAfterPixelDolly.Distance,
                perspectiveBeforePixelDolly.Distance * pixelFactor,
                0.05f)
            || !CameraTransitionNear(
                perspectiveAfterPixelDolly.ZoomScale,
                perspectiveBeforePixelDolly.ZoomScale,
                0.0001f))
        {
            throw new InvalidOperationException(
                "Perspective Ctrl-middle Dolly changed projection zoom instead of camera distance.");
        }
        stage.DollyReferenceCameraByPixels(-pixelDolly);
        AssertReferenceCameraFrameNear(
            stage.CaptureReferenceCameraFrameForPersistence(),
            perspectiveBeforePixelDolly,
            "perspective pixel Dolly reversal");

        stage.ConfigureReferenceView(definition, SceneDimension.ThreeD, ReferenceCameraMotion.Immediate);
        stage.SetReferenceViewDirection(ReferenceViewDirection.Top, ReferenceCameraMotion.Immediate);
        var topFrame = stage.CaptureReferenceCameraFrameForPersistence();
        var topView = stage.CaptureViewState();
        stage.RotateReferenceCamera(17, 0);
        var afterHorizontalTopViewDrag = stage.CaptureReferenceCameraFrameForPersistence();
        if (CameraTransitionAngularDistance(afterHorizontalTopViewDrag.Yaw, topFrame.Yaw + 0.17f) > 0.0001f
            || !CameraTransitionNear(afterHorizontalTopViewDrag.Pitch, topFrame.Pitch)
            || ReferenceTargetDistance(topFrame, afterHorizontalTopViewDrag) > 0.001f
            || !CameraTransitionNear(afterHorizontalTopViewDrag.Distance, topFrame.Distance, 0.05f))
        {
            throw new InvalidOperationException(
                "A horizontal orbit from the top view changed the camera pitch or target.");
        }
        stage.RotateReferenceCamera(0, -4);
        var afterInwardTopViewDrag = stage.CaptureReferenceCameraFrameForPersistence();
        if (!CameraTransitionNear(afterInwardTopViewDrag.Pitch, topFrame.Pitch - 0.04f)
            || afterInwardTopViewDrag.Pitch >= topFrame.Pitch
            || CameraTransitionAngularDistance(afterInwardTopViewDrag.Yaw, afterHorizontalTopViewDrag.Yaw) > 0.0001f)
        {
            throw new InvalidOperationException(
                "An inward vertical orbit from the top view did not change pitch smoothly.");
        }
        stage.RestoreViewState(topView);
        AssertReferenceCameraFrameNear(
            stage.CaptureReferenceCameraFrameForPersistence(),
            topFrame,
            "top-view camera state restoration");
        if (topFrame.Pitch <= 1.4f || topFrame.Pitch > 1.5f)
        {
            throw new InvalidOperationException("The top view did not retain its supported maximum pitch.");
        }
        Console.WriteLine("reference_camera_projection_navigation=ok");

        void AssertReferenceCameraScreenDelta(StageControl cameraStage, Point delta, string operation)
        {
            if (!cameraStage.TryProjectScenePosition(Vector3.Zero, out var before, out _))
            {
                throw new InvalidOperationException($"{operation} could not project its center point before the drag.");
            }
            cameraStage.PanReferenceCamera(delta.X, delta.Y);
            if (!cameraStage.TryProjectScenePosition(Vector3.Zero, out var after, out _)
                || Math.Abs(after.X - before.X - delta.X) > 0.1f
                || Math.Abs(after.Y - before.Y - delta.Y) > 0.1f)
            {
                throw new InvalidOperationException(
                    $"{operation} did not preserve the pointer-space delta: "
                    + $"before={before}, after={after}, delta={delta}.");
            }
        }
    }

    private static void RunReferenceCameraLookAroundRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        using var stage = new StageControl(scene) { Size = new Size(800, 600) };
        var definition = new SceneDefinition { Dimension = SceneDimension.ThreeD };

        foreach (var projection in new[] { CameraProjection.Orthographic, CameraProjection.Perspective })
        {
            definition.Camera.Projection = projection;
            stage.ConfigureReferenceView(definition, SceneDimension.ThreeD, ReferenceCameraMotion.Immediate);
            stage.ResetReferenceCameraView(ReferenceCameraMotion.Immediate);
            stage.SetReferenceCameraOrientation(0.37f, -0.24f, ReferenceCameraMotion.Immediate);

            var baseline = stage.CaptureReferenceCameraFrameForPersistence();
            var baselineEye = ReferenceCameraEye(baseline);
            stage.LookAroundReferenceCamera(0, 0);
            AssertReferenceCameraFrameNear(
                stage.CaptureReferenceCameraFrameForPersistence(),
                baseline,
                $"{projection} zero look-around");
            stage.LookAroundReferenceCamera(float.NaN, 1);
            stage.LookAroundReferenceCamera(1, float.PositiveInfinity);
            AssertReferenceCameraFrameNear(
                stage.CaptureReferenceCameraFrameForPersistence(),
                baseline,
                $"{projection} invalid look-around");

            stage.LookAroundReferenceCamera(20, -12);
            var firstLook = stage.CaptureReferenceCameraFrameForPersistence();
            AssertLookAroundFrame(
                firstLook,
                baseline,
                baselineEye,
                baseline.Yaw + 0.2f,
                baseline.Pitch + 0.12f,
                $"{projection} right/up look-around");
            if (ReferenceTargetDistance(firstLook, baseline) <= 0.01f)
            {
                throw new InvalidOperationException($"{projection} look-around did not move its target around the fixed eye.");
            }

            stage.LookAroundReferenceCamera(-9, 7);
            var secondLook = stage.CaptureReferenceCameraFrameForPersistence();
            AssertLookAroundFrame(
                secondLook,
                firstLook,
                baselineEye,
                firstLook.Yaw - 0.09f,
                firstLook.Pitch - 0.07f,
                $"{projection} left/down look-around");

            stage.SetReferenceViewDirection(ReferenceViewDirection.Top, ReferenceCameraMotion.Immediate);
            var topFrame = stage.CaptureReferenceCameraFrameForPersistence();
            var topEye = ReferenceCameraEye(topFrame);
            stage.LookAroundReferenceCamera(15, -100);
            var clampedTop = stage.CaptureReferenceCameraFrameForPersistence();
            AssertLookAroundFrame(
                clampedTop,
                topFrame,
                topEye,
                topFrame.Yaw + 0.15f,
                topFrame.Pitch,
                $"{projection} top-view pitch limit");
            stage.LookAroundReferenceCamera(0, 10);
            var inwardTop = stage.CaptureReferenceCameraFrameForPersistence();
            AssertLookAroundFrame(
                inwardTop,
                clampedTop,
                topEye,
                clampedTop.Yaw,
                clampedTop.Pitch - 0.1f,
                $"{projection} top-view inward pitch");

            var boundedView = stage.CaptureViewState() with
            {
                ReferenceYaw = 0,
                ReferencePitch = 0,
                ReferenceTargetX = 5_000_000f,
                ReferenceTargetY = 0,
                ReferenceTargetZ = 0
            };
            stage.RestoreViewState(boundedView);
            var boundedFrame = stage.CaptureReferenceCameraFrameForPersistence();
            stage.LookAroundReferenceCamera(20, 0);
            AssertReferenceCameraFrameNear(
                stage.CaptureReferenceCameraFrameForPersistence(),
                boundedFrame,
                $"{projection} target-boundary look-around rejection");
        }

        definition.Camera.Projection = CameraProjection.Orthographic;
        stage.ConfigureReferenceView(definition, SceneDimension.TwoD, ReferenceCameraMotion.Immediate);
        stage.SetReferenceViewDirection(ReferenceViewDirection.Front, ReferenceCameraMotion.Immediate);
        var twoDimensional = stage.CaptureReferenceCameraFrameForPersistence();
        stage.LookAroundReferenceCamera(20, -12);
        AssertReferenceCameraFrameNear(
            stage.CaptureReferenceCameraFrameForPersistence(),
            twoDimensional,
            "2D look-around rejection");
        Console.WriteLine("reference_camera_look_around=ok");

        void AssertLookAroundFrame(
            ReferenceCameraFrame actual,
            ReferenceCameraFrame previous,
            Vector3 expectedEye,
            float expectedYaw,
            float expectedPitch,
            string operation)
        {
            var eye = ReferenceCameraEye(actual);
            if (CameraTransitionAngularDistance(actual.Yaw, expectedYaw) > 0.0001f
                || !CameraTransitionNear(actual.Pitch, expectedPitch, 0.0001f)
                || !CameraTransitionNear(actual.Distance, previous.Distance, 0.05f)
                || !CameraTransitionNear(actual.ZoomScale, previous.ZoomScale, 0.0001f)
                || !CameraTransitionNear(actual.ProjectionBlend, previous.ProjectionBlend, 0.0001f)
                || Vector3.Distance(eye, expectedEye) > 0.05f)
            {
                throw new InvalidOperationException(
                    $"{operation} changed the wrong camera state: "
                    + $"actual=({actual.Yaw:0.####},{actual.Pitch:0.####},{actual.Distance:0.###},{actual.ZoomScale:0.####}), "
                    + $"expected=({expectedYaw:0.####},{expectedPitch:0.####},{previous.Distance:0.###},{previous.ZoomScale:0.####}).");
            }
        }
    }

    private static Vector3 ReferenceCameraEye(ReferenceCameraFrame frame)
    {
        var pitchCos = MathF.Cos(frame.Pitch);
        var forward = new Vector3(
            pitchCos * MathF.Sin(frame.Yaw),
            MathF.Sin(frame.Pitch),
            pitchCos * MathF.Cos(frame.Yaw));
        return new Vector3(frame.TargetX, frame.TargetY, frame.TargetZ) - forward * frame.Distance;
    }

    private static void RunReferenceCameraKeyboardNavigationRegression(
        MainForm form,
        StageControl stage,
        SceneDefinition scene,
        Func<int> undoCount)
    {
        const BindingFlags privateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        var projectDirtyField = typeof(MainForm).GetField("_projectDirty", privateInstance)
            ?? throw new InvalidOperationException("Camera keyboard regression could not inspect project dirty state.");
        var numericEditorField = typeof(ModernNumericUpDown).GetField("_editor", privateInstance)
            ?? throw new InvalidOperationException("Camera keyboard regression could not find the numeric text editor.");
        TextBoxBase? numericEditor = null;
        var pendingControls = new Stack<Control>();
        pendingControls.Push(form);
        while (pendingControls.Count > 0 && numericEditor is null)
        {
            foreach (Control child in pendingControls.Pop().Controls)
            {
                pendingControls.Push(child);
                if (child is ModernNumericUpDown { Visible: true, Enabled: true } numeric
                    && numericEditorField.GetValue(numeric) is TextBoxBase
                    {
                        Visible: true,
                        Enabled: true,
                        CanFocus: true
                    } editor)
                {
                    numericEditor = editor;
                    break;
                }
            }
        }
        if (numericEditor is null)
        {
            throw new InvalidOperationException(
                "Camera keyboard regression could not find a visible numeric text editor.");
        }

        var originalDimension = stage.ReferenceDimension;
        var originalView = stage.CaptureViewState();
        var originalDirty = projectDirtyField.GetValue(form) is true;
        var originalUndoCount = undoCount();
        var originalGeometryRevision = stage.Scene.GeometryRevision;
        var originalSummaryRevision = stage.Scene.SummaryRevision;
        var camera = scene.Camera;
        var originalSceneCamera = (
            camera.Name,
            camera.Projection,
            camera.X,
            camera.Y,
            camera.Z,
            camera.Depth,
            camera.OrthographicSize,
            camera.FieldOfViewDegrees);

        try
        {
            AssertReferenceCameraKeyboardMapping();
            AssertReferenceCameraShortcutConflicts();

            stage.ConfigureReferenceView(scene, SceneDimension.ThreeD, ReferenceCameraMotion.Immediate);
            stage.ResetReferenceCameraView(ReferenceCameraMotion.Immediate);
            stage.SetReferenceCameraOrientation(0, 0, ReferenceCameraMotion.Immediate);
            form.Activate();
            stage.Select();
            stage.Focus();
            Application.DoEvents();
            if (!form.IsHandleCreated || !stage.IsHandleCreated || !stage.ContainsFocus)
            {
                throw new InvalidOperationException("Camera keyboard regression lost focused Stage input.");
            }

            var controlledView = stage.CaptureViewState();
            AssertReferenceCameraLocalMovement(form, stage, controlledView);
            AssertReferenceCameraSmoothMovement(form, stage, controlledView);
            AssertReferenceCameraTransitionHandoff(form, stage, controlledView);
            AssertReferenceCameraQueuedKeyboardInput(form, stage, controlledView);

            stage.ConfigureReferenceView(scene, SceneDimension.TwoD, ReferenceCameraMotion.Immediate);
            stage.Select();
            stage.Focus();
            Application.DoEvents();
            var twoDimensionalCamera = stage.CaptureReferenceCameraFrameForPersistence();
            if (form.BeginReferenceCameraKeyboardNavigation(Keys.Left))
            {
                throw new InvalidOperationException("The camera arrow shortcut was consumed outside the 3D Scene view.");
            }
            AssertReferenceCameraFrameNear(
                stage.CaptureReferenceCameraFrameForPersistence(),
                twoDimensionalCamera,
                "2D camera shortcut rejection");

            stage.ConfigureReferenceView(scene, SceneDimension.ThreeD, ReferenceCameraMotion.Immediate);
            stage.RestoreViewState(controlledView);
            if (!numericEditor.Focus())
            {
                throw new InvalidOperationException("Camera keyboard regression could not focus a numeric text editor.");
            }
            Application.DoEvents();
            var editorFocusedCamera = stage.CaptureReferenceCameraFrameForPersistence();
            PostCameraNavigationKey(numericEditor.Handle, Keys.Left, down: true);
            PostCameraNavigationKey(numericEditor.Handle, Keys.Left, down: false);
            PumpCameraNavigationMessages(40);
            if (!numericEditor.ContainsFocus
                || form.BeginReferenceCameraKeyboardNavigation(Keys.Left))
            {
                throw new InvalidOperationException("A focused numeric editor did not retain camera-arrow ownership.");
            }
            AssertReferenceCameraFrameNear(
                stage.CaptureReferenceCameraFrameForPersistence(),
                editorFocusedCamera,
                "focused editor camera shortcut rejection");

            var stageClick = new Point(
                Math.Max(1, stage.ClientSize.Width - 2),
                Math.Max(1, stage.ClientSize.Height - 2));
            form.Activate();
            Application.DoEvents();
            PostCameraNavigationMouseClick(stage, stageClick);
            PumpCameraNavigationMessages(120);
            if (!stage.ContainsFocus)
            {
                throw new InvalidOperationException("Clicking the Stage did not reclaim camera keyboard focus from a numeric editor.");
            }

            var currentCamera = scene.Camera;
            var currentSceneCamera = (
                currentCamera.Name,
                currentCamera.Projection,
                currentCamera.X,
                currentCamera.Y,
                currentCamera.Z,
                currentCamera.Depth,
                currentCamera.OrthographicSize,
                currentCamera.FieldOfViewDegrees);
            if (projectDirtyField.GetValue(form) is not bool currentDirty
                || currentDirty != originalDirty
                || undoCount() != originalUndoCount
                || stage.Scene.GeometryRevision != originalGeometryRevision
                || stage.Scene.SummaryRevision != originalSummaryRevision
                || currentSceneCamera != originalSceneCamera)
            {
                throw new InvalidOperationException("Camera keyboard movement changed project data, dirty state, scene revisions, or undo history.");
            }
        }
        finally
        {
            form.CancelReferenceCameraKeyboardNavigation();
            stage.ConfigureReferenceView(scene, originalDimension, ReferenceCameraMotion.Immediate);
            stage.RestoreViewState(originalView);
            stage.Select();
            stage.Focus();
            Application.DoEvents();
        }
    }

    private static void AssertReferenceCameraLocalMovement(
        MainForm form,
        StageControl stage,
        StageViewState controlledView)
    {
        var baseline = stage.CaptureReferenceCameraFrameForPersistence();
        var movedRight = MeasureReferenceCameraKeyboardMovement(
            form,
            stage,
            controlledView,
            [Keys.Right],
            64,
            accelerated: false);
        var movedLeft = MeasureReferenceCameraKeyboardMovement(
            form,
            stage,
            controlledView,
            [Keys.Left],
            64,
            accelerated: false);
        var movedForward = MeasureReferenceCameraKeyboardMovement(
            form,
            stage,
            controlledView,
            [Keys.Up],
            64,
            accelerated: false);
        var movedBackward = MeasureReferenceCameraKeyboardMovement(
            form,
            stage,
            controlledView,
            [Keys.Down],
            64,
            accelerated: false);
        var rightDelta = ReferenceTargetDelta(baseline, movedRight);
        var leftDelta = ReferenceTargetDelta(baseline, movedLeft);
        var forwardDelta = ReferenceTargetDelta(baseline, movedForward);
        var backwardDelta = ReferenceTargetDelta(baseline, movedBackward);
        if (rightDelta.X <= 0
            || Math.Abs(rightDelta.Y) > 0.01f
            || Math.Abs(rightDelta.Z) > 0.01f
            || forwardDelta.Z <= 0
            || Math.Abs(forwardDelta.X) > 0.01f
            || Math.Abs(forwardDelta.Y) > 0.01f
            || Vector3.Distance(rightDelta, -leftDelta) > 0.02f
            || Vector3.Distance(forwardDelta, -backwardDelta) > 0.02f
            || !CameraParametersNear(baseline, movedRight)
            || !CameraParametersNear(baseline, movedForward))
        {
            throw new InvalidOperationException("Arrow keys did not move right/left or forward/backward along the camera-local axes.");
        }

        var normalDistance = rightDelta.Length();
        var accelerated = MeasureReferenceCameraKeyboardMovement(
            form,
            stage,
            controlledView,
            [Keys.Right],
            64,
            accelerated: true);
        var acceleratedDistance = ReferenceTargetDistance(baseline, accelerated);
        if (normalDistance <= 0
            || Math.Abs(acceleratedDistance / normalDistance - 4f) > 0.001f
            || !CameraParametersNear(baseline, accelerated))
        {
            throw new InvalidOperationException(
                $"Shift camera movement was not exactly 4x: normal={normalDistance:0.###}, accelerated={acceleratedDistance:0.###}.");
        }

        var diagonal = MeasureReferenceCameraKeyboardMovement(
            form,
            stage,
            controlledView,
            [Keys.Right, Keys.Up],
            64,
            accelerated: false);
        if (Math.Abs(ReferenceTargetDistance(baseline, diagonal) / normalDistance - 1f) > 0.001f)
        {
            throw new InvalidOperationException("Diagonal camera movement was not normalized to the single-axis speed.");
        }

        stage.RestoreViewState(controlledView);
        form.CancelReferenceCameraKeyboardNavigation();
        if (!form.BeginReferenceCameraKeyboardNavigation(Keys.Left)
            || !form.BeginReferenceCameraKeyboardNavigation(Keys.Right))
        {
            throw new InvalidOperationException("Camera navigation could not hold opposite horizontal keys.");
        }
        form.AdvanceReferenceCameraKeyboardNavigation(64, accelerated: false);
        AssertReferenceCameraFrameNear(
            stage.CaptureReferenceCameraFrameForPersistence(),
            baseline,
            "opposite horizontal camera keys");
        form.EndReferenceCameraKeyboardNavigation(Keys.Left);
        form.AdvanceReferenceCameraKeyboardNavigation(64, accelerated: false);
        if (stage.CaptureReferenceCameraFrameForPersistence().TargetX <= baseline.TargetX)
        {
            throw new InvalidOperationException("Releasing one opposite camera key did not resume the remaining direction.");
        }
        form.CancelReferenceCameraKeyboardNavigation();

        const float yaw = 0.72f;
        const float pitch = -0.31f;
        stage.RestoreViewState(controlledView);
        stage.SetReferenceCameraOrientation(yaw, pitch, ReferenceCameraMotion.Immediate);
        var orientedBaseline = stage.CaptureReferenceCameraFrameForPersistence();
        var orientedView = stage.CaptureViewState();
        var center = new Point(
            stage.ClientSize.Width / 2,
            (int)Math.Round(stage.ClientSize.Height * 0.58f));
        var targetScenePoint = new Vector3(
            orientedBaseline.TargetX,
            -orientedBaseline.TargetY,
            orientedBaseline.TargetZ);
        if (!stage.TryGetReferenceRay(center, out var centerRay)
            || !stage.TryProjectScenePosition(targetScenePoint, out var projectedBeforeForward, out _))
        {
            throw new InvalidOperationException("Camera navigation could not resolve the current center view ray.");
        }
        var orientedForward = MeasureReferenceCameraKeyboardMovement(
            form,
            stage,
            orientedView,
            [Keys.Up],
            64,
            accelerated: false);
        if (!stage.TryProjectScenePosition(targetScenePoint, out var projectedAfterForward, out _)
            || CameraNavigationPointDistance(projectedBeforeForward, projectedAfterForward) > 0.05f)
        {
            throw new InvalidOperationException(
                "Forward camera movement drifted away from the current center view ray.");
        }
        var orientedRight = MeasureReferenceCameraKeyboardMovement(
            form,
            stage,
            orientedView,
            [Keys.Right],
            64,
            accelerated: false);
        AssertReferenceCameraDirection(
            ReferenceTargetDeltaInSceneSpace(orientedBaseline, orientedForward),
            centerRay.Direction,
            "current camera center-ray forward");
        AssertReferenceCameraDirection(
            ReferenceTargetDelta(orientedBaseline, orientedRight),
            new Vector3(MathF.Cos(yaw), 0, -MathF.Sin(yaw)),
            "oriented camera right");

        stage.RestoreViewState(controlledView);
        form.CancelReferenceCameraKeyboardNavigation();
    }

    private static void AssertReferenceCameraSmoothMovement(
        MainForm form,
        StageControl stage,
        StageViewState controlledView)
    {
        stage.RestoreViewState(controlledView);
        form.CancelReferenceCameraKeyboardNavigation();
        var baseline = stage.CaptureReferenceCameraFrameForPersistence();
        if (!form.BeginReferenceCameraKeyboardNavigation(Keys.Right))
        {
            throw new InvalidOperationException("Camera smoothing regression could not begin movement.");
        }
        form.AdvanceReferenceCameraKeyboardNavigation(16, accelerated: false);
        var first = stage.CaptureReferenceCameraFrameForPersistence();
        form.AdvanceReferenceCameraKeyboardNavigation(16, accelerated: false);
        var second = stage.CaptureReferenceCameraFrameForPersistence();
        var firstStep = ReferenceTargetDistance(baseline, first);
        var secondStep = ReferenceTargetDistance(first, second);
        if (firstStep <= 0
            || secondStep <= 0
            || UiMotion.AnimationsEnabled && secondStep <= firstStep)
        {
            throw new InvalidOperationException("Camera keyboard movement did not accelerate smoothly from rest.");
        }

        form.EndReferenceCameraKeyboardNavigation(Keys.Right);
        if (UiMotion.AnimationsEnabled)
        {
            form.AdvanceReferenceCameraKeyboardNavigation(16, accelerated: false);
            var coastOne = stage.CaptureReferenceCameraFrameForPersistence();
            form.AdvanceReferenceCameraKeyboardNavigation(16, accelerated: false);
            var coastTwo = stage.CaptureReferenceCameraFrameForPersistence();
            if (ReferenceTargetDistance(second, coastOne) <= 0
                || ReferenceTargetDistance(coastOne, coastTwo) <= 0
                || ReferenceTargetDistance(coastOne, coastTwo) >= ReferenceTargetDistance(second, coastOne))
            {
                throw new InvalidOperationException("Camera keyboard movement did not decelerate smoothly after key release.");
            }
            for (var frame = 0; frame < 60 && form.ReferenceCameraKeyboardNavigationActive; frame++)
            {
                form.AdvanceReferenceCameraKeyboardNavigation(16, accelerated: false);
            }
        }
        if (form.ReferenceCameraKeyboardNavigationActive)
        {
            throw new InvalidOperationException("Camera keyboard deceleration did not settle and stop.");
        }

        var fine = MeasureReferenceCameraKeyboardMovement(
            form,
            stage,
            controlledView,
            [Keys.Right],
            16,
            accelerated: false,
            frameCount: 10);
        var coarse = MeasureReferenceCameraKeyboardMovement(
            form,
            stage,
            controlledView,
            [Keys.Right],
            40,
            accelerated: false,
            frameCount: 4);
        var fineDistance = ReferenceTargetDistance(baseline, fine);
        var coarseDistance = ReferenceTargetDistance(baseline, coarse);
        if (fineDistance <= 0 || Math.Abs(coarseDistance / fineDistance - 1f) > 0.002f)
        {
            throw new InvalidOperationException(
                $"Camera movement depended on timer frame size: fine={fineDistance:0.###}, coarse={coarseDistance:0.###}.");
        }
    }

    private static void AssertReferenceCameraTransitionHandoff(
        MainForm form,
        StageControl stage,
        StageViewState controlledView)
    {
        stage.RestoreViewState(controlledView);
        form.CancelReferenceCameraKeyboardNavigation();
        stage.SetReferenceCameraOrientation(0.72f, -0.31f, ReferenceCameraMotion.Animated);
        var pendingTarget = stage.CaptureReferenceCameraFrameForPersistence();
        if (!form.BeginReferenceCameraKeyboardNavigation(Keys.Up))
        {
            throw new InvalidOperationException("Camera input did not take over an active camera transition.");
        }
        form.AdvanceReferenceCameraKeyboardNavigation(16, accelerated: false);
        var transitionHandoff = stage.CaptureReferenceCameraFrameForPersistence();
        form.CancelReferenceCameraKeyboardNavigation();
        if (stage.ReferenceCameraTransitionActive
            || !CameraParametersNear(pendingTarget, transitionHandoff)
            || ReferenceTargetDistance(pendingTarget, transitionHandoff) <= 0.01f)
        {
            throw new InvalidOperationException("Manual camera input did not complete the pending transition before moving its final target.");
        }
    }

    private static void AssertReferenceCameraQueuedKeyboardInput(
        MainForm form,
        StageControl stage,
        StageViewState controlledView)
    {
        stage.RestoreViewState(controlledView);
        form.CancelReferenceCameraKeyboardNavigation();
        form.Activate();
        stage.Select();
        stage.Focus();
        Application.DoEvents();
        var baseline = stage.CaptureReferenceCameraFrameForPersistence();

        PostCameraNavigationKey(stage.Handle, Keys.Right, down: true);
        PumpCameraNavigationMessages(90);
        var first = stage.CaptureReferenceCameraFrameForPersistence();
        PumpCameraNavigationMessages(90);
        var second = stage.CaptureReferenceCameraFrameForPersistence();
        if (first.TargetX <= baseline.TargetX || second.TargetX <= first.TargetX)
        {
            throw new InvalidOperationException("A held arrow did not continue moving without operating-system key repeats.");
        }

        PostCameraNavigationKey(stage.Handle, Keys.Right, down: false);
        PumpCameraNavigationMessages(UiMotion.AnimationsEnabled ? 500 : 40);
        var stopped = stage.CaptureReferenceCameraFrameForPersistence();
        PumpCameraNavigationMessages(80);
        AssertReferenceCameraFrameNear(
            stage.CaptureReferenceCameraFrameForPersistence(),
            stopped,
            "released camera key timer stop");
        if (form.ReferenceCameraKeyboardNavigationActive)
        {
            throw new InvalidOperationException("The queued camera key-up did not stop continuous navigation.");
        }
    }

    private static ReferenceCameraFrame MeasureReferenceCameraKeyboardMovement(
        MainForm form,
        StageControl stage,
        StageViewState view,
        IReadOnlyList<Keys> keys,
        double elapsedMilliseconds,
        bool accelerated,
        int frameCount = 1)
    {
        stage.RestoreViewState(view);
        form.CancelReferenceCameraKeyboardNavigation();
        foreach (var key in keys)
        {
            if (!form.BeginReferenceCameraKeyboardNavigation(key))
            {
                throw new InvalidOperationException($"Camera navigation could not begin {key}.");
            }
        }
        for (var frame = 0; frame < frameCount; frame++)
        {
            form.AdvanceReferenceCameraKeyboardNavigation(elapsedMilliseconds, accelerated);
        }
        var result = stage.CaptureReferenceCameraFrameForPersistence();
        form.CancelReferenceCameraKeyboardNavigation();
        return result;
    }

    private static void AssertReferenceCameraKeyboardMapping()
    {
        var left = MainForm.ResolveReferenceCameraKeyboardDirection(Keys.Left);
        var right = MainForm.ResolveReferenceCameraKeyboardDirection(Keys.Right);
        var up = MainForm.ResolveReferenceCameraKeyboardDirection(Keys.Up);
        var down = MainForm.ResolveReferenceCameraKeyboardDirection(Keys.Down);
        var fastRight = MainForm.ResolveReferenceCameraKeyboardDirection(Keys.Shift | Keys.Right);
        if (left is not { X: < 0, Y: 0 }
            || right is not { X: > 0, Y: 0 }
            || up is not { X: 0, Y: > 0 }
            || down is not { X: 0, Y: < 0 }
            || fastRight != right
            || MainForm.ResolveReferenceCameraKeyboardDirection(Keys.Control | Keys.Up) is not null
            || MainForm.ResolveReferenceCameraKeyboardDirection(Keys.Alt | Keys.Left) is not null
            || MainForm.ResolveReferenceCameraKeyboardDirection(Keys.Control | Keys.Shift | Keys.Right) is not null
            || MainForm.ResolveReferenceCameraKeyboardDirection(Keys.W) is not null
            || MainForm.ResolveReferenceCameraKeyboardDirection(Keys.A) is not null
            || MainForm.ResolveReferenceCameraKeyboardDirection(Keys.S) is not null
            || MainForm.ResolveReferenceCameraKeyboardDirection(Keys.D) is not null)
        {
            throw new InvalidOperationException("Camera keyboard mapping did not preserve local-axis directions or modifier conflicts.");
        }
    }

    private static void AssertReferenceCameraShortcutConflicts()
    {
        Keys[] cameraShortcuts =
        [
            Keys.Left,
            Keys.Right,
            Keys.Up,
            Keys.Down,
            Keys.Shift | Keys.Left,
            Keys.Shift | Keys.Right,
            Keys.Shift | Keys.Up,
            Keys.Shift | Keys.Down
        ];
        if (cameraShortcuts.Any(key => !ShortcutProfiles.HasContextualFixedShortcutConflict(key))
            || cameraShortcuts.Any(ShortcutProfiles.IsReservedGesture))
        {
            throw new InvalidOperationException("3D camera arrows were not declared as contextual, non-global shortcut conflicts.");
        }
    }

    private static void PostCameraNavigationKey(IntPtr handle, Keys key, bool down)
    {
        var posted = PostCameraNavigationMessage(
            handle,
            down ? CameraNavigationWmKeyDown : CameraNavigationWmKeyUp,
            new IntPtr((int)(key & Keys.KeyCode)),
            down ? new IntPtr(1) : new IntPtr(unchecked((int)0xC0000001)));
        if (!posted)
        {
            throw new InvalidOperationException(
                $"Camera navigation could not post {(down ? "key-down" : "key-up")} for {key}; Win32={Marshal.GetLastWin32Error()}.");
        }
    }

    private static void PostCameraNavigationMouseClick(StageControl stage, Point client)
    {
        var coordinates = new IntPtr((client.Y << 16) | (client.X & 0xffff));
        if (!PostCameraNavigationMessage(
                stage.Handle,
                CameraNavigationWmLeftButtonDown,
                new IntPtr(1),
                coordinates)
            || !PostCameraNavigationMessage(
                stage.Handle,
                CameraNavigationWmLeftButtonUp,
                IntPtr.Zero,
                coordinates))
        {
            throw new InvalidOperationException(
                $"Camera navigation could not post a Stage mouse click; Win32={Marshal.GetLastWin32Error()}.");
        }
    }

    private static void PumpCameraNavigationMessages(double milliseconds)
    {
        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started).TotalMilliseconds < milliseconds)
        {
            Application.DoEvents();
            Thread.Sleep(2);
        }
        Application.DoEvents();
    }

    private static void AssertReferenceCameraDirection(Vector3 actual, Vector3 expected, string operation)
    {
        if (actual.LengthSquared() <= 0
            || expected.LengthSquared() <= 0
            || Vector3.Dot(Vector3.Normalize(actual), Vector3.Normalize(expected)) < 0.9999f)
        {
            throw new InvalidOperationException(
                $"{operation} used the wrong local axis: actual={actual}, expected={expected}.");
        }
    }

    private static bool CameraParametersNear(ReferenceCameraFrame expected, ReferenceCameraFrame actual)
    {
        return CameraTransitionAngularDistance(expected.Yaw, actual.Yaw) <= 0.0001f
            && CameraTransitionNear(expected.Pitch, actual.Pitch)
            && CameraTransitionNear(expected.Distance, actual.Distance, 0.05f)
            && CameraTransitionNear(expected.ZoomScale, actual.ZoomScale)
            && CameraTransitionNear(expected.ProjectionBlend, actual.ProjectionBlend);
    }

    private static Vector3 ReferenceTargetDelta(ReferenceCameraFrame from, ReferenceCameraFrame to)
    {
        return new Vector3(
            to.TargetX - from.TargetX,
            to.TargetY - from.TargetY,
            to.TargetZ - from.TargetZ);
    }

    private static Vector3 ReferenceTargetDeltaInSceneSpace(
        ReferenceCameraFrame from,
        ReferenceCameraFrame to)
    {
        var reference = ReferenceTargetDelta(from, to);
        return new Vector3(reference.X, -reference.Y, reference.Z);
    }

    private static float ReferenceTargetDistance(ReferenceCameraFrame left, ReferenceCameraFrame right)
    {
        return ReferenceTargetDelta(left, right).Length();
    }

    private static float CameraNavigationPointDistance(PointF left, PointF right)
    {
        var dx = left.X - right.X;
        var dy = left.Y - right.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
