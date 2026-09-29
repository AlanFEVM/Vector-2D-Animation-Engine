using System.Diagnostics;
using System.Globalization;
using System.Numerics;

namespace VectorAnimationEngine;

// The director camera is shown on the scene Stage as a framing gizmo. The 3D inspector remains the
// source of the complete camera state, while the gizmo provides the fast framing operations users
// expect from a camera object in a director workspace.
internal sealed partial class MainForm
{
    private const int ShotPreviewRefreshIntervalMilliseconds = 150;

    private readonly Stopwatch _shotPreviewStopwatch = Stopwatch.StartNew();
    private ShotFramingPointerSession? _shotFramingPointerSession;
    private bool _shotFramingHintVisible;
    /// <summary>
    /// Snaps camera handle drags to whole units. On by default, matching the snapping the other Stage
    /// gizmos apply, and suspended while Ctrl is held for free placement.
    /// </summary>
    private bool _shotFramingSnapEnabled = true;

    private sealed class ShotFramingPointerSession
    {
        public required SceneDefinition Scene { get; init; }
        public required string ShotId { get; init; }
        public required int EditFrame { get; init; }
        public required SceneShotSettings StartSettings { get; init; }
        public required ShotFramingHandleKind Handle { get; init; }
        public required Vector2 StartPointerWorld { get; init; }
        /// <summary>
        /// Press position in the flat Stage space used to draw the camera wireframe. It is captured
        /// alongside <see cref="StartPointerWorld"/> because the transform overlay may map the pointer
        /// through a reference projection that the wireframe itself does not use.
        /// </summary>
        public required Vector2 StartPointerFlatWorld { get; init; }
        public required Point StartScreen { get; init; }
        public Point LastScreen { get; set; }
        public Vector3 PositionAxis { get; init; }
        public Vector3? PositionPlaneNormal { get; init; }
        /// <summary>Camera world point at press time, used to solve the body-drag target position.</summary>
        public Vector3 BodyStartWorld { get; init; }
        public Vector3? PositionStartPoint { get; init; }
        public Vector3? PositionAxisDragPlaneNormal { get; init; }
        public float PositionAxisStartParameter { get; init; }
        public Vector2 PositionScreenAxis { get; init; }
        public Vector3 RotationAxis { get; init; }
        public Vector3? RotationStartVector { get; init; }
        public Vector3? RotationPreviousVector { get; set; }
        public float RotationAccumulatedDegrees { get; set; }
        public bool RotationUsedScreenFallback { get; set; }
        public Vector2 RotationScreenAxis { get; init; }
        public float RotationStartScreenAngle { get; init; }
        public float RotationPreviousScreenAngle { get; set; }
        public Vector2 OpticalScreenAxis { get; init; }
        public Vector3 CameraWorldPoint { get; init; }
        public PointF CameraScreenPoint { get; init; }
        public PointF LensStartScreen { get; init; }
        public SceneShotSettings PreviousRawSettings { get; set; }
        public SceneShotSettings PrecisionSettings { get; set; }
        /// <summary>
        /// Fraction of the drag currently applied, reduced while Shift is held for precise adjustment.
        /// Only new pointer movement is scaled, preserving motion already applied before Shift.
        /// </summary>
        public float PrecisionScale { get; set; } = 1f;
        /// <summary>
        /// Screen-space state of the yellow shot framing frame at press. The frame is drawn from the
        /// camera's projected world plane, so its drag is solved in that same screen space rather than
        /// in a second projection.
        /// </summary>
        public StageControl.ShotFramingScreenDragStart FrameStart { get; init; }
        public SceneShotSettings LastSettings { get; set; }
    }

    /// <summary>
    /// Shows the framing gizmo for the active shot and keeps it aligned with the playhead. Unlike the
    /// other stage gizmos it stays visible during playback: the shot camera handle is how the operator
    /// watches and trims the framing, so hiding it the moment playback starts defeats the purpose.
    /// It is still hidden when the workspace or edit context cannot supply a shot.
    /// </summary>
    private void UpdateShotFramingGizmo()
    {
        ClearShotFramingFeedback();
        if (_workspaceTabs.SelectedView != WorkspaceView.ShotDirector
            || !IsSceneCompositionContext() || ActiveScene() is not { } scene)
        {
            _stage.ClearShotFramingGizmo();
            return;
        }
        // Blank exposures and out-of-range frames still retain a camera outline.
        var frames = scene.Shots.Select(shot => new StageControl.ShotFrameDisplay(
            shot.Id,
            scene.TryEvaluateShotSettings(shot.Id, Math.Max(0, _frame), out var settings)
                ? settings : shot.EvaluateSettings(Math.Max(0, _frame)),
            shot.Id == _activeShotId)).OrderBy(frame => frame.Selected).ToArray();
        _stage.SetShotFrameDisplays(frames);
    }

    private static string BuildShotFramingGizmoLabel(
        SceneShotDefinition shot,
        SceneShotRange range,
        SceneShotSettings settings,
        ShotFramingHandleKind handle = ShotFramingHandleKind.None)
    {
        var normalized = settings.Normalized();
        var cameraMode = normalized.Projection == CameraProjection.Perspective
            ? string.Format(CultureInfo.CurrentCulture, "Perspective {0:0.##}mm", normalized.FocalLength)
            : string.Format(CultureInfo.CurrentCulture, "Orthographic {0:0.##}", normalized.OrthographicSize);
        var label = string.Format(
            CultureInfo.CurrentCulture,
            "{0}  \u00b7  {1}-{2}  \u00b7  {3}",
            shot.Name,
            range.StartFrame,
            range.EndFrame,
            cameraMode);
        if (handle == ShotFramingHandleKind.None) return label;

        // While a camera handle is being dragged, the live value is appended so the operator can aim
        // for an exact number instead of reading the inspector after the fact.
        return string.Format(
            CultureInfo.CurrentCulture,
            "{0}  \u00b7  {1}",
            label,
            StageControl.DescribeShotCameraSettings(settings, handle));
    }

    /// <summary>
    /// Fraction of a camera drag applied while Shift is held. It matches the 10% precision convention
    /// used by the 3D Transform gizmo so the modifier means the same thing across the Stage.
    /// </summary>
    private const float ShotFramingPrecisionScale = 0.1f;

    /// <summary>Hit test for the framing gizmo; a hit takes priority over canvas selection.</summary>
    private bool TryBeginShotFramingGizmoPointer(Point screen)
    {
        if (_workspaceTabs.SelectedView != WorkspaceView.ShotDirector
            || _playing
            || !IsSceneCompositionContext()
            || ActiveScene() is not { } scene
            || string.IsNullOrWhiteSpace(_activeShotId)
            || scene.FindShot(_activeShotId) is not { } shot
            || !_stage.ShotFramingGizmoVisible)
        {
            return false;
        }

        var hit = _stage.HitTestShotFramingGizmo(screen);
        if (!hit.IsValid
            || !TryResolveSceneShotEditFrame(scene, shot.Id, out var editFrame)
            || !scene.TryEvaluateShotSettings(shot.Id, editFrame, out var editSettings))
        {
            return false;
        }

        // The Stage shows the evaluated playhead state. Keep the pointer session anchored to that
        // visible state even when Auto Key is off and the edit is redirected to a tween source frame.
        var startSettings = scene.TryEvaluateShotSettings(
            shot.Id,
            Math.Max(0, _frame),
            out var displayedSettings)
            ? displayedSettings
            : editSettings;

        var positionAxis = Vector3.Zero;
        Vector3? positionPlaneNormal = null;
        Vector3? positionStartPoint = null;
        Vector3? positionAxisDragPlaneNormal = null;
        var positionAxisStartParameter = 0f;
        var positionScreenAxis = Vector2.Zero;
        var bodyStartWorld = Vector3.Zero;
        var rotationAxis = Vector3.Zero;
        Vector3? rotationStartVector = null;
        var rotationScreenAxis = Vector2.Zero;
        var rotationStartScreenAngle = 0f;
        var opticalScreenAxis = Vector2.UnitX;
        var cameraWorldPoint = startSettings.Position;
        var cameraScreenPoint = PointF.Empty;
        var lensStartScreen = PointF.Empty;
        if (StageControl.IsShotCameraHandle(hit.Kind))
        {
            if (!_stage.TryGetShotCameraWireframeInteractionOrigin(out cameraWorldPoint))
            {
                return false;
            }

            if (!_stage.TryGetShotCameraWireframeHandlePoint(
                    ShotFramingHandleKind.CameraBody,
                    out cameraScreenPoint))
            {
                return false;
            }

            if (hit.Kind == ShotFramingHandleKind.CameraBody)
            {
                bodyStartWorld = cameraWorldPoint;
                if (_stage.RendersReferenceProjection)
                {
                    if (!_stage.TryGetReferenceRay(
                            new Point(_stage.Width / 2, _stage.Height / 2), out var viewRay)) return false;
                    positionPlaneNormal = -viewRay.Direction;
                    if (!TryGetShotCameraPlanePoint(screen, cameraWorldPoint,
                            positionPlaneNormal.Value, out var grabbedPoint)) return false;
                    positionStartPoint = grabbedPoint;
                }
            }
            else if (StageControl.IsShotCameraPositionHandle(hit.Kind))
            {
                positionAxis = ShotCameraPositionAxis(hit.Kind);
                if (!_stage.TryGetShotCameraWireframeHandlePoint(hit.Kind, out var axisPoint)) return false;
                positionScreenAxis = NormalizeScreenAxis(axisPoint, cameraScreenPoint, Vector2.UnitX);
                if (_stage.RendersReferenceProjection
                    && TryCreateSpatialAxisDragPlane(screen, positionAxis, out var axisPlaneNormal)
                    && TryGetAxisPlaneParameter(
                        screen,
                        cameraWorldPoint,
                        positionAxis,
                        axisPlaneNormal,
                        out positionAxisStartParameter))
                {
                    positionAxisDragPlaneNormal = axisPlaneNormal;
                }
            }
            else if (StageControl.IsShotCameraRotationHandle(hit.Kind))
            {
                rotationAxis = ShotCameraRotationAxis(hit.Kind);
                if (!_stage.TryGetShotCameraWireframeHandlePoint(hit.Kind, out var rotationPoint)) return false;
                rotationScreenAxis = NormalizeScreenAxis(rotationPoint, cameraScreenPoint, new Vector2(0f, -1f));
                rotationStartScreenAngle = MathF.Atan2(
                    screen.Y - cameraScreenPoint.Y,
                    screen.X - cameraScreenPoint.X);
                if (_stage.RendersReferenceProjection)
                {
                    if (TryGetRotationPlaneVector(
                            screen,
                            cameraWorldPoint,
                            rotationAxis,
                            out var startVector))
                    {
                        rotationStartVector = startVector;
                    }
                }
            }
            else if (hit.Kind == ShotFramingHandleKind.CameraLens)
            {
                if (_stage.TryGetShotCameraWireframeHandlePoint(hit.Kind, out var lensPoint))
                {
                    lensStartScreen = lensPoint;
                    opticalScreenAxis = NormalizeScreenAxis(lensPoint, cameraScreenPoint, Vector2.UnitX);
                }
            }
        }

        BeginSceneShotEdit(scene);
        if (_sceneShotEditSnapshot is null) return false;
        _shotFramingPointerSession = new ShotFramingPointerSession
        {
            Scene = scene,
            ShotId = shot.Id,
            EditFrame = editFrame,
            StartSettings = startSettings,
            LastSettings = startSettings,
            PreviousRawSettings = startSettings,
            PrecisionSettings = startSettings,
            Handle = hit.Kind,
            StartPointerWorld = ToVector(_stage.TransformOverlayScreenToWorld(screen)),
            StartPointerFlatWorld = ToVector(_stage.ScreenToWorld(screen)),
            StartScreen = screen,
            LastScreen = screen,
            PositionAxis = positionAxis,
            PositionPlaneNormal = positionPlaneNormal,
            BodyStartWorld = bodyStartWorld,
            PositionStartPoint = positionStartPoint,
            PositionAxisDragPlaneNormal = positionAxisDragPlaneNormal,
            PositionAxisStartParameter = positionAxisStartParameter,
            PositionScreenAxis = positionScreenAxis,
            RotationAxis = rotationAxis,
            RotationStartVector = rotationStartVector,
            RotationPreviousVector = rotationStartVector,
            RotationAccumulatedDegrees = 0f,
            RotationUsedScreenFallback = rotationStartVector is null,
            RotationScreenAxis = rotationScreenAxis,
            RotationStartScreenAngle = rotationStartScreenAngle,
            RotationPreviousScreenAngle = rotationStartScreenAngle,
            OpticalScreenAxis = opticalScreenAxis,
            CameraWorldPoint = cameraWorldPoint,
            CameraScreenPoint = cameraScreenPoint,
            LensStartScreen = lensStartScreen,
            FrameStart = ResolveShotFramingFrameStart(startSettings, screen, hit.Kind)
        };

        _stage.SetShotFramingHover(ShotFramingHandleHit.None);
        _stage.SetShotFramingActive(hit);
        _stage.SetSceneLightGizmoHover(SceneLightGizmoHandleHit.None);
        _stage.SetSpatialTransformHover(SpatialTransformHandleHit.None);
        _stage.ClearBrushTipCursor();
        _stage.ClearFillToolCursor();
        ClearFillHoverPreview();
        _stage.Capture = true;
        _lastMouse = screen;
        _startScreen = screen;
        _startWorld = _stage.TransformOverlayScreenToWorld(screen);
        _stage.Cursor = CursorForShotFramingHandle(hit.Kind);
        ShowShotFramingFeedback(screen, hit.Kind, startSettings);
        return true;
    }

    /// <summary>
    /// Forces the precision modifier on or off for a regression run. The form normally reads Shift from
    /// the live keyboard, which a headless test cannot hold down, so it drives this hook instead.
    /// </summary>
    public static void SetShotFramingPrecisionOverrideForTesting(bool held) =>
        _shotFramingPrecisionOverride = held;

    private static bool? _shotFramingPrecisionOverride;

    private static bool IsShotFramingPrecisionRequested() =>
        _shotFramingPrecisionOverride ?? IsShiftPressed();

    private void UpdateShotFramingPointer(Point screen)
    {
        var session = _shotFramingPointerSession;
        if (session is null) return;
        // Modifiers are sampled per move event so Shift (precision) and Ctrl (snap) take effect
        // immediately, including part way through a drag.
        var precision = IsShotFramingPrecisionRequested() ? ShotFramingPrecisionScale : 1f;
        // Ctrl suspends snapping so the operator can place a value freely; releasing it re-enables it.
        var snapping = (ModifierKeys & Keys.Control) == Keys.None;
        if (precision != session.PrecisionScale || snapping != _shotFramingSnapEnabled)
        {
            session.PrecisionSettings = session.LastSettings;
        }
        session.PrecisionScale = precision;
        _shotFramingSnapEnabled = snapping;
        ShowShotFramingFeedback(screen, session.Handle, session.LastSettings);
        if (screen == session.LastScreen) return;
        session.LastScreen = screen;
        var settings = StageControl.IsShotCameraHandle(session.Handle)
            ? ResolveShotCameraPointerSettings(session, screen)
            : ResolveShotFramingPointerSettings(session, screen);
        if (settings == session.LastSettings) return;
        session.LastSettings = settings;
        ShowShotFramingFeedback(screen, session.Handle, settings);
        if (!session.Scene.PreviewShotAtFrame(session.ShotId, session.EditFrame, settings)) return;
        if (session.Scene.FindShot(session.ShotId) is { } shot)
        {
            _stage.SetShotFramingGizmo(
                settings,
                BuildShotFramingGizmoLabel(
                    shot,
                    session.Scene.GetShotRange(shot.Id),
                    settings,
                    session.Handle));
        }
        _shotDirectorFramingPushed = null;
        PushShotDirectorFraming();
        _sceneShotEditNeedsTimelineRefresh = true;
        _timeline.Invalidate();
        _stage.InvalidateOverlay();
    }

    private SceneShotSettings ResolveShotFramingPointerSettings(
        ShotFramingPointerSession session,
        Point screen)
    {
        // The yellow frame is drawn from the camera's projected world plane, so the drag is solved in
        // that same screen space. Solving it in the flat canvas model would track a different
        // projection than the one the user grabbed, which is what made the edge handles run away.
        return _stage.ResolveShotFramingDragFromScreen(
            session.StartSettings,
            session.Handle,
            screen,
            session.FrameStart.Screen,
            session.FrameStart.HandleScreen,
            session.FrameStart.CenterScreen);
    }

    /// <summary>
    /// Captures the yellow frame's screen geometry at press so the drag can be solved in the space the
    /// frame is actually drawn in.
    /// </summary>
    private StageControl.ShotFramingScreenDragStart ResolveShotFramingFrameStart(
        SceneShotSettings settings,
        Point screen,
        ShotFramingHandleKind handle)
    {
        _ = settings;
        if (!_stage.TryGetShotFramingGizmoGeometry(out var geometry))
        {
            return new StageControl.ShotFramingScreenDragStart(screen, PointF.Empty, PointF.Empty);
        }

        var handlePoint = StageControl.EnumerateShotFramingHandles(geometry)
            .Where(entry => entry.Kind == handle)
            .Select(entry => (PointF?)entry.Point)
            .FirstOrDefault();
        return new StageControl.ShotFramingScreenDragStart(
            screen,
            handlePoint ?? geometry.Center,
            geometry.Center);
    }

    private SceneShotSettings ResolveShotCameraPointerSettings(
        ShotFramingPointerSession session,
        Point screen)
    {
        var screenDelta = new Vector2(
            screen.X - session.StartScreen.X,
            screen.Y - session.StartScreen.Y);
        var positionDelta = Vector3.Zero;
        var rotationDelta = 0f;
        var opticalDelta = 0f;
        if (session.Handle == ShotFramingHandleKind.CameraBody)
        {
            // The body must stay under the pointer, so the camera is placed exactly where the pointer
            // ray meets the drag plane through the body. A world-per-pixel scale cannot do this: the
            // reference projection is perspective, so the screen position couples to the camera's own
            // depth and the body would drift by a projection-dependent factor.
            if (session.PositionPlaneNormal is { } bodyPlane
                && session.PositionStartPoint is { } grabbedPoint
                && TryGetShotCameraPlanePoint(screen, session.BodyStartWorld, bodyPlane, out var bodyPosition))
            {
                positionDelta = bodyPosition - grabbedPoint;
            }
            else if (!_stage.RendersReferenceProjection)
            {
                // The flat Stage draws the camera wireframe with WorldToScreen, so its drag delta has
                // to be measured in the same flat space. TransformOverlayScreenToWorld switches to a
                // ray/plane projection whenever a scene instance is selected, which would map the
                // pointer through a different transform than the one drawing the handle.
                var start = session.StartPointerFlatWorld;
                var current = ToVector(_stage.ScreenToWorld(screen));
                positionDelta = new Vector3(current.X - start.X, current.Y - start.Y, 0f);
            }
        }
        else if (StageControl.IsShotCameraPositionHandle(session.Handle))
        {
            var axisDelta = 0f;
            if (session.PositionAxisDragPlaneNormal is { } planeNormal
                && TryGetAxisPlaneParameter(
                    screen,
                    session.CameraWorldPoint,
                    session.PositionAxis,
                    planeNormal,
                    out var currentParameter))
            {
                axisDelta = currentParameter - session.PositionAxisStartParameter;
            }
            else
            {
                axisDelta = Vector2.Dot(screenDelta, session.PositionScreenAxis)
                    * _stage.ScreenLengthToWorld(1f);
            }

            positionDelta = session.PositionAxis * axisDelta;
        }
        else if (StageControl.IsShotCameraRotationHandle(session.Handle))
        {
            if (TryGetRotationPlaneVector(
                    screen,
                    session.CameraWorldPoint,
                    session.RotationAxis,
                    out var currentVector))
            {
                if (session.RotationPreviousVector is { } previousVector
                    && !session.RotationUsedScreenFallback)
                {
                    var cross = Vector3.Cross(previousVector, currentVector);
                    var sin = Vector3.Dot(cross, session.RotationAxis);
                    var cos = Math.Clamp(Vector3.Dot(previousVector, currentVector), -1f, 1f);
                    session.RotationAccumulatedDegrees += MathF.Atan2(sin, cos) * 180f / MathF.PI;
                }

                session.RotationPreviousVector = currentVector;
                // Keep the screen anchor current as well. If the ray/axis plane becomes
                // edge-on, the fallback must continue from the last visible pointer angle
                // instead of jumping back to the drag origin.
                session.RotationPreviousScreenAngle = MathF.Atan2(
                    screen.Y - session.CameraScreenPoint.Y,
                    screen.X - session.CameraScreenPoint.X);
                session.RotationUsedScreenFallback = false;
                rotationDelta = session.RotationAccumulatedDegrees;
            }
            else
            {
                var currentAngle = MathF.Atan2(
                    screen.Y - session.CameraScreenPoint.Y,
                    screen.X - session.CameraScreenPoint.X);
                var delta = StageControl.NormalizeShotAngleDeltaDegrees(
                    (currentAngle - session.RotationPreviousScreenAngle) * 180f / MathF.PI);
                session.RotationPreviousScreenAngle = currentAngle;
                session.RotationAccumulatedDegrees += delta;
                // Re-anchor the 3D plane on the next valid sample so the fallback delta is not
                // counted a second time after the pointer crosses a projection singularity.
                session.RotationPreviousVector = null;
                session.RotationUsedScreenFallback = true;
                rotationDelta = session.RotationAccumulatedDegrees;
            }
        }
        else if (session.Handle == ShotFramingHandleKind.CameraLens)
        {
            // The optical slider is solved directly in screen space: the pointer position determines
            // the lens, so the handle lands exactly under the cursor. It is applied here rather than
            // through the shared delta resolver because the mapping is a screen-space slider, not a
            // linear delta on the lens value.
            if (!_stage.TryResolveShotCameraLensSliderDrag(
                    new PointF(session.LensStartScreen.X + screenDelta.X,
                        session.LensStartScreen.Y + screenDelta.Y),
                    session.StartSettings,
                    out var lensSettings))
            {
                return session.LastSettings;
            }

            return ApplyShotFramingPrecision(
                session,
                StageControl.ResolveShotCameraHandleDrag(
                    session.StartSettings,
                    session.Handle,
                    Vector3.Zero,
                    rotationDelta,
                    ResolveShotCameraLensSliderDelta(session, lensSettings)));
        }

        return ApplyShotFramingPrecision(
            session,
            StageControl.ResolveShotCameraHandleDrag(
                session.StartSettings,
                session.Handle,
                positionDelta,
                rotationDelta,
                opticalDelta));
    }

    /// <summary>
    /// Converts a solved optical slider result into the pixel-scaled delta the shared handle resolver
    /// expects, so clamping and normalization stay in one place.
    /// </summary>
    private static float ResolveShotCameraLensSliderDelta(
        ShotFramingPointerSession session,
        SceneShotSettings solved)
    {
        var start = session.StartSettings.Normalized();
        if (start.Projection == CameraProjection.Orthographic)
        {
            return (start.OrthographicSize - solved.OrthographicSize) / 24f;
        }

        return (solved.FocalLength - start.FocalLength) / 0.75f;
    }

    /// <summary>
    /// Accumulates only new movement at the selected precision, keeping the unsnapped value separate
    /// so small movements are retained. Snapping touches only the handle's edited channels.
    /// </summary>
    private SceneShotSettings ApplyShotFramingPrecision(
        ShotFramingPointerSession session,
        SceneShotSettings settings)
    {
        var effective = StageControl.AccumulateShotFramingDrag(
            session.PreviousRawSettings, settings, session.PrecisionSettings, session.PrecisionScale);
        session.PreviousRawSettings = settings;
        session.PrecisionSettings = effective;
        return _shotFramingSnapEnabled && session.PrecisionScale >= 1f
            ? StageControl.SnapShotFramingSettings(session.StartSettings, effective, session.Handle)
            : effective;
    }

    private void CompleteShotFramingPointer()
    {
        ClearShotFramingFeedback();
        var session = _shotFramingPointerSession;
        _shotFramingPointerSession = null;
        if (session is null) return;
        _stage.SetShotFramingActive(ShotFramingHandleHit.None);
        _stage.Capture = false;
        _lastMouse = null;
        _startScreen = null;
        _startWorld = null;
        if (session.LastSettings != session.StartSettings)
        {
            if (_timeline.AutoKeyframeEnabled && session.EditFrame == Math.Max(0, _frame))
            {
                session.Scene.InsertShotTimelineKeyframe(session.ShotId, session.EditFrame);
            }

            session.Scene.UpdateShotAtFrame(session.ShotId, session.EditFrame, session.LastSettings);
        }

        CompleteSceneShotEdit();
        _timeline.RefreshTimeline();
        _stage.Invalidate();
        UpdateShotFramingGizmo();
        RefreshShotPreview(force: true);
        RefreshInteractionCursorAtPointer();
    }

    private void CancelShotFramingPointer()
    {
        ClearShotFramingFeedback();
        var session = _shotFramingPointerSession;
        _shotFramingPointerSession = null;
        if (session is null) return;
        _stage.SetShotFramingActive(ShotFramingHandleHit.None);
        _stage.Capture = false;
        _lastMouse = null;
        _startScreen = null;
        _startWorld = null;
        CancelSceneShotEdit();
        UpdateShotFramingGizmo();
        RefreshInteractionCursorAtPointer();
    }

    /// <summary>Hover feedback for the framing gizmo. Returns true when the pointer is on the frame.</summary>
    private bool UpdateShotFramingGizmoCursor(Point screen)
    {
        if (_workspaceTabs.SelectedView != WorkspaceView.ShotDirector
            || _playing
            || !IsSceneCompositionContext()
            || !_stage.ShotFramingGizmoVisible)
        {
            ClearShotFramingFeedback();
            return false;
        }

        var hit = _stage.HitTestShotFramingGizmo(screen);
        if (_shotFramingPointerSession is null) _stage.SetShotFramingHover(hit);
        if (_shotFramingPointerSession is null && !hit.IsValid)
        {
            ClearShotFramingFeedback();
            return false;
        }

        _stage.SetSceneLightGizmoHover(SceneLightGizmoHandleHit.None);
        _stage.SetSpatialTransformHover(SpatialTransformHandleHit.None);
        _stage.ClearBrushTipCursor();
        _stage.ClearFillToolCursor();
        ClearFillHoverPreview();
        var cursorHandle = _shotFramingPointerSession is { } session
            ? session.Handle
            : hit.Kind;
        _stage.Cursor = CursorForShotFramingHandle(cursorHandle);
        ShowShotFramingFeedback(screen, cursorHandle, _stage.ShotFramingGizmoSettings);
        return true;
    }

    private void ClearShotFramingFeedback()
    {
        _stage.SetShotFramingHover(ShotFramingHandleHit.None);
        if (!_shotFramingHintVisible) return;
        _shotFramingHintVisible = false;
        _toolTip.HideTip();
    }

    private void ShowShotFramingFeedback(Point screen, ShotFramingHandleKind handle, SceneShotSettings settings)
    {
        if (_playing || !_stage.ClientRectangle.Contains(screen))
        {
            ClearShotFramingFeedback();
            return;
        }
        var action = handle switch
        {
            ShotFramingHandleKind.CameraBody => "Move camera in view plane",
            ShotFramingHandleKind.CameraPositionX => "Move camera along X",
            ShotFramingHandleKind.CameraPositionY => "Move camera along Y",
            ShotFramingHandleKind.CameraPositionZ => "Move camera along Z",
            ShotFramingHandleKind.CameraRotateX => "Rotate camera around X",
            ShotFramingHandleKind.CameraRotateY => "Rotate camera around Y",
            ShotFramingHandleKind.CameraRotateZ => "Roll camera around Z",
            ShotFramingHandleKind.CameraLens => settings.Projection == CameraProjection.Perspective
                ? "Adjust focal length" : "Adjust orthographic size",
            ShotFramingHandleKind.Body => "Move shot frame",
            ShotFramingHandleKind.Rotate => "Rotate shot frame",
            _ => "Resize shot frame"
        };
        var hint = StageControl.IsShotCameraHandle(handle)
            ? UiLocalization.T("Shift: fine adjustment · Ctrl: unsnap · Esc: cancel")
            : UiLocalization.T("Drag to adjust · Esc: cancel");
        var value = StageControl.DescribeShotCameraSettings(settings, handle);
        _toolTip.ShowAt(_stage, screen, $"{UiLocalization.T(action)}  ·  {value}\n{hint}");
        _shotFramingHintVisible = true;
    }

    private static Cursor CursorForShotFramingHandle(ShotFramingHandleKind handle) => handle switch
    {
        ShotFramingHandleKind.TopLeft or ShotFramingHandleKind.BottomRight => Cursors.SizeNWSE,
        ShotFramingHandleKind.TopRight or ShotFramingHandleKind.BottomLeft => Cursors.SizeNESW,
        ShotFramingHandleKind.Top or ShotFramingHandleKind.Bottom => Cursors.SizeNS,
        ShotFramingHandleKind.Left or ShotFramingHandleKind.Right => Cursors.SizeWE,
        ShotFramingHandleKind.Rotate => Cursors.Hand,
        ShotFramingHandleKind.CameraRotateX
            or ShotFramingHandleKind.CameraRotateY
            or ShotFramingHandleKind.CameraRotateZ => Cursors.Hand,
        ShotFramingHandleKind.CameraLens => Cursors.SizeWE,
        _ => Cursors.SizeAll
    };

    private static Vector3 ShotCameraPositionAxis(ShotFramingHandleKind handle) => handle switch
    {
        ShotFramingHandleKind.CameraPositionX => Vector3.UnitX,
        ShotFramingHandleKind.CameraPositionY => Vector3.UnitY,
        ShotFramingHandleKind.CameraPositionZ => Vector3.UnitZ,
        _ => Vector3.Zero
    };

    private static Vector3 ShotCameraRotationAxis(ShotFramingHandleKind handle) => handle switch
    {
        ShotFramingHandleKind.CameraRotateX => Vector3.UnitX,
        ShotFramingHandleKind.CameraRotateY => Vector3.UnitY,
        ShotFramingHandleKind.CameraRotateZ => Vector3.UnitZ,
        _ => Vector3.Zero
    };

    private static Vector2 NormalizeScreenAxis(PointF endpoint, PointF origin, Vector2 fallback)
    {
        var axis = new Vector2(endpoint.X - origin.X, endpoint.Y - origin.Y);
        return axis.LengthSquared() > 0.0001f ? Vector2.Normalize(axis) : fallback;
    }

    private bool TryGetShotCameraPlanePoint(
        Point screen,
        Vector3 origin,
        Vector3 normal,
        out Vector3 point)
    {
        point = default;
        if (!_stage.TryGetReferenceRay(screen, out var ray)) return false;
        var denominator = Vector3.Dot(ray.Direction, normal);
        if (Math.Abs(denominator) < StageControl.SpatialGizmoMinimumPlaneRayDot) return false;
        var distance = Vector3.Dot(origin - ray.Origin, normal) / denominator;
        // This is a screen-space drag plane through the camera, not a scene surface. Its
        // mathematical intersection may be behind the reference eye when the camera object is
        // behind the current view, and that signed point still gives a continuous drag mapping.
        if (!float.IsFinite(distance)) return false;
        point = ray.Origin + ray.Direction * distance;
        return float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);
    }

    /// <summary>
    /// Renders the selected shot's framed viewport into the director panel preview. Throttled because
    /// the capture renders the composition offscreen, and skipped while playing or dragging.
    /// </summary>
    private void RefreshShotPreview(bool force = false)
    {
        if (!_shotDirectorInitialized || !_shotPreviewOverlay.Visible) return;
        if (!force && _shotPreviewStopwatch.ElapsedMilliseconds < ShotPreviewRefreshIntervalMilliseconds) return;
        var scene = IsSceneBuildingContext() ? ActiveScene() : null;
        var shot = scene is not null && _activeShotId.Length > 0 ? scene.FindShot(_activeShotId) : null;
        if (scene is null || shot is null || _playing || _shotFramingPointerSession is not null)
        {
            _shotPreviewOverlay.SetPreview(null);
            return;
        }

        _shotPreviewStopwatch.Restart();
        var frame = Math.Max(0, _frame);
        var settings = scene.TryEvaluateShotSettings(shot.Id, frame, out var evaluated)
            ? evaluated
            : shot.Settings;
        var caption = string.Format(
            CultureInfo.CurrentCulture,
            UiLocalization.T("Shot preview of {0} at frame {1}"),
            shot.Name,
            frame);
        if (_stage.TryCaptureShotPreview(settings, _shotPreviewOverlay.PreviewPixelSize, out var bitmap))
        {
            _shotPreviewOverlay.SetPreview(bitmap, caption);
            return;
        }

        _shotPreviewOverlay.SetPreview(null);
    }

    private static Vector2 ToVector(PointF point) => new(point.X, point.Y);
}
