using System.Numerics;

namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private SceneLightGizmoPointerSession? _sceneLightGizmoPointerSession;
    private Point? _pendingSceneLightGizmoScreen;

    private sealed class SceneLightGizmoPointerSession
    {
        public required SceneDefinition Scene { get; init; }
        public required string LightId { get; init; }
        public required SceneLightKind Kind { get; init; }
        public required int EditFrame { get; init; }
        public required SceneLightSettings StartSettings { get; init; }
        public required SceneLightSettings LastSettings { get; set; }
        public required SceneLightGizmoHandleHit Handle { get; init; }
        public required Point StartScreen { get; init; }
        public required SceneLightGizmoScreenGeometry Geometry { get; init; }
        public Vector3? PositionPlaneNormal { get; init; }
        public Vector3? PositionStartPoint { get; init; }
        public Vector3 PositionAxis { get; init; }
        public Vector3? PositionAxisDragPlaneNormal { get; init; }
        public float PositionAxisStartParameter { get; init; }
        public Vector2 PositionScreenAxis { get; init; }
        public bool PointerUpdateApplied { get; set; }
    }

    private bool TryBeginSceneLightGizmoPointer(Point screen)
    {
        if (!IsScene3DView()
            || _playing
            || ActiveScene() is not { } scene
            || scene.Lights.FirstOrDefault(light => string.Equals(
                light.Id,
                _selectedSceneLightId,
                StringComparison.Ordinal)) is not { } light)
        {
            return false;
        }

        var handle = _stage.HitTestSceneLightGizmo(screen);
        if (!handle.IsValid || !_stage.TryGetSceneLightGizmoScreenGeometry(out var geometry))
            return false;
        if (!TryResolveSceneLightEdit(scene, light, out var editFrame, out var startSettings)) return false;

        Vector3? positionPlaneNormal = null;
        Vector3? positionStartPoint = null;
        var positionAxis = SceneLightPositionAxis(handle.Kind);
        Vector3? positionAxisDragPlaneNormal = null;
        var positionAxisStartParameter = 0f;
        var positionScreenAxis = Vector2.Zero;
        if (handle.Kind == SceneLightGizmoHandleKind.Position)
        {
            if (!_stage.TryGetReferenceRay(screen, out var ray)
                || !TryGetSceneLightGizmoPlanePoint(
                    screen,
                    startSettings.Position,
                    ray.Direction,
                    out var startPoint))
            {
                return false;
            }
            positionPlaneNormal = ray.Direction;
            positionStartPoint = startPoint;
        }
        else if (positionAxis != Vector3.Zero)
        {
            var endpoint = handle.Kind switch
            {
                SceneLightGizmoHandleKind.PositionX => geometry.PositionXHandle,
                SceneLightGizmoHandleKind.PositionY => geometry.PositionYHandle,
                _ => geometry.PositionZHandle
            };
            positionScreenAxis = new Vector2(
                endpoint.X - geometry.Origin.X,
                endpoint.Y - geometry.Origin.Y);
            if (positionScreenAxis.LengthSquared() > 0.0001f)
                positionScreenAxis = Vector2.Normalize(positionScreenAxis);
            if (TryCreateSpatialAxisDragPlane(screen, positionAxis, out var axisPlaneNormal)
                && TryGetAxisPlaneParameter(
                    screen,
                    startSettings.Position,
                    positionAxis,
                    axisPlaneNormal,
                    out positionAxisStartParameter))
            {
                positionAxisDragPlaneNormal = axisPlaneNormal;
            }
        }

        BeginSceneOpticsEdit();
        if (_sceneOpticsEditSnapshot is null) return false;
        _sceneLightGizmoPointerSession = new SceneLightGizmoPointerSession
        {
            Scene = scene,
            LightId = light.Id,
            Kind = light.Kind,
            EditFrame = editFrame,
            StartSettings = startSettings,
            LastSettings = startSettings,
            Handle = handle,
            StartScreen = screen,
            Geometry = geometry,
            PositionPlaneNormal = positionPlaneNormal,
            PositionStartPoint = positionStartPoint,
            PositionAxis = positionAxis,
            PositionAxisDragPlaneNormal = positionAxisDragPlaneNormal,
            PositionAxisStartParameter = positionAxisStartParameter,
            PositionScreenAxis = positionScreenAxis
        };
        _pendingSceneLightGizmoScreen = null;
        _stage.SetSceneLightGizmoHover(SceneLightGizmoHandleHit.None);
        _stage.SetSceneLightGizmoActive(handle);
        _stage.BeginReference3DOpticalInteractionPreview();
        _stage.SetSpatialTransformHover(SpatialTransformHandleHit.None);
        _stage.Capture = true;
        _lastMouse = screen;
        _startScreen = screen;
        _startWorld = null;
        _stage.Cursor = Cursors.SizeAll;
        return true;
    }

    private void QueueSceneLightGizmoPointer(Point screen)
    {
        var pointer = _sceneLightGizmoPointerSession;
        if (pointer is null) return;
        _pendingSceneLightGizmoScreen = screen;
        if (!pointer.PointerUpdateApplied) ApplyPendingSceneLightGizmoPointer();
        _lineDragPreviewTimer.Start();
    }

    private bool ApplyPendingSceneLightGizmoPointer()
    {
        if (_pendingSceneLightGizmoScreen is not { } screen) return false;
        _pendingSceneLightGizmoScreen = null;
        var pointer = _sceneLightGizmoPointerSession;
        if (pointer is null) return false;
        pointer.PointerUpdateApplied = true;
        UpdateSceneLightGizmoPointer(screen);
        return true;
    }

    private void ClearPendingSceneLightGizmoPointer()
    {
        _pendingSceneLightGizmoScreen = null;
        if (!HasPendingFrameCoalescedWork()) _lineDragPreviewTimer.Stop();
    }

    private void UpdateSceneLightGizmoPointer(Point screen)
    {
        var pointer = _sceneLightGizmoPointerSession;
        if (pointer is null) return;
        var light = pointer.Scene.Lights.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, pointer.LightId, StringComparison.Ordinal));
        if (light is null || light.Kind != pointer.Kind) return;

        var screenDelta = new Vector2(
            screen.X - pointer.StartScreen.X,
            screen.Y - pointer.StartScreen.Y);
        var scalarDelta = ResolveSceneLightGizmoScalarDelta(pointer, screen, screenDelta);
        var settings = ApplySceneLightGizmoDelta(
            pointer.Kind,
            pointer.StartSettings,
            pointer.Handle.Kind,
            screenDelta,
            scalarDelta,
            pointer.Geometry.WorldUnitsPerPixel);

        if (pointer.Handle.Kind == SceneLightGizmoHandleKind.Position
            && pointer.PositionPlaneNormal is { } normal
            && pointer.PositionStartPoint is { } startPoint
            && TryGetSceneLightGizmoPlanePoint(
                screen,
                pointer.StartSettings.Position,
                normal,
                out var currentPoint))
        {
            var next = pointer.StartSettings.Position + currentPoint - startPoint;
            settings = settings with
            {
                Position = new Vector3(
                    VectorUnits.Quantize(next.X),
                    VectorUnits.Quantize(next.Y),
                    VectorUnits.Quantize(next.Z))
            };
        }
        else if (pointer.PositionAxis != Vector3.Zero)
        {
            float axisDelta;
            if (pointer.PositionAxisDragPlaneNormal is { } axisPlaneNormal)
            {
                if (!TryGetAxisPlaneParameter(
                        screen,
                        pointer.StartSettings.Position,
                        pointer.PositionAxis,
                        axisPlaneNormal,
                        out var axisParameter))
                {
                    return;
                }
                axisDelta = axisParameter - pointer.PositionAxisStartParameter;
            }
            else
            {
                axisDelta = Vector2.Dot(screenDelta, pointer.PositionScreenAxis)
                    * pointer.Geometry.WorldUnitsPerPixel;
            }
            var next = pointer.StartSettings.Position + pointer.PositionAxis * axisDelta;
            settings = settings with
            {
                Position = new Vector3(
                    VectorUnits.Quantize(next.X),
                    VectorUnits.Quantize(next.Y),
                    VectorUnits.Quantize(next.Z))
            };
        }

        if (settings == pointer.LastSettings
            || !ApplyResolvedSceneLightEdit(
                pointer.Scene,
                light,
                light.Name,
                settings,
                pointer.EditFrame,
                pointer.LastSettings))
        {
            return;
        }
        pointer.LastSettings = settings;
        RefreshSceneLightGizmo();
        _sceneLightingPanel.SetLights(
            pointer.Scene.Lights.Select(candidate => ToEditorState(pointer.Scene, candidate, _frame)).ToArray(),
            _selectedSceneLightId);
        _stage.Invalidate();
    }

    private static float ResolveSceneLightGizmoScalarDelta(
        SceneLightGizmoPointerSession pointer,
        Point screen,
        Vector2 screenDelta)
    {
        return pointer.Handle.Kind switch
        {
            SceneLightGizmoHandleKind.Range =>
                Distance(screen, pointer.Geometry.Origin)
                - Distance(pointer.StartScreen, pointer.Geometry.Origin),
            SceneLightGizmoHandleKind.AreaWidth => ProjectScreenDelta(
                screenDelta,
                pointer.Geometry.Origin,
                pointer.Geometry.AreaWidthHandle),
            SceneLightGizmoHandleKind.AreaHeight => ProjectScreenDelta(
                screenDelta,
                pointer.Geometry.Origin,
                pointer.Geometry.AreaHeightHandle),
            SceneLightGizmoHandleKind.PositionX => ProjectScreenDelta(
                screenDelta,
                pointer.Geometry.Origin,
                pointer.Geometry.PositionXHandle),
            SceneLightGizmoHandleKind.PositionY => ProjectScreenDelta(
                screenDelta,
                pointer.Geometry.Origin,
                pointer.Geometry.PositionYHandle),
            SceneLightGizmoHandleKind.PositionZ => ProjectScreenDelta(
                screenDelta,
                pointer.Geometry.Origin,
                pointer.Geometry.PositionZHandle),
            _ => screenDelta.X
        };
    }

    internal static SceneLightSettings ApplySceneLightGizmoDelta(
        SceneLightKind kind,
        SceneLightSettings start,
        SceneLightGizmoHandleKind handle,
        Vector2 screenDelta,
        float scalarDeltaPixels,
        float worldUnitsPerPixel)
    {
        if (!start.IsValid(kind)
            || !float.IsFinite(screenDelta.X)
            || !float.IsFinite(screenDelta.Y)
            || !float.IsFinite(scalarDeltaPixels)
            || !float.IsFinite(worldUnitsPerPixel)
            || worldUnitsPerPixel <= 0)
        {
            return start;
        }

        const float trackPixels = 62f;
        return handle switch
        {
            SceneLightGizmoHandleKind.PositionX
                when kind is SceneLightKind.Directional or SceneLightKind.Point or SceneLightKind.Area => start with
                {
                    Position = start.Position with
                    {
                        X = VectorUnits.Quantize(
                            start.Position.X + scalarDeltaPixels * worldUnitsPerPixel)
                    }
                },
            SceneLightGizmoHandleKind.PositionY
                when kind is SceneLightKind.Directional or SceneLightKind.Point or SceneLightKind.Area => start with
                {
                    Position = start.Position with
                    {
                        Y = VectorUnits.Quantize(
                            start.Position.Y + scalarDeltaPixels * worldUnitsPerPixel)
                    }
                },
            SceneLightGizmoHandleKind.PositionZ
                when kind is SceneLightKind.Directional or SceneLightKind.Point or SceneLightKind.Area => start with
                {
                    Position = start.Position with
                    {
                        Z = VectorUnits.Quantize(
                            start.Position.Z + scalarDeltaPixels * worldUnitsPerPixel)
                    }
                },
            SceneLightGizmoHandleKind.Direction
                when kind is SceneLightKind.Directional or SceneLightKind.Area => start with
                {
                    RotationDegrees = new Vector3(
                        QuantizeLightValue(Math.Clamp(start.RotationDegrees.X + screenDelta.Y * 0.5f, -360f, 360f)),
                        QuantizeLightValue(Math.Clamp(start.RotationDegrees.Y + screenDelta.X * 0.5f, -360f, 360f)),
                        start.RotationDegrees.Z)
                },
            SceneLightGizmoHandleKind.Intensity => start with
            {
                Intensity = QuantizeLightValue(Math.Clamp(
                    MathF.Exp(Math.Clamp(
                        MathF.Log(1f + start.Intensity)
                            + scalarDeltaPixels / trackPixels
                            * MathF.Log(1f + SceneLightSettings.MaximumIntensity),
                        0f,
                        MathF.Log(1f + SceneLightSettings.MaximumIntensity))) - 1f,
                    0f,
                    SceneLightSettings.MaximumIntensity),
                    3)
            },
            SceneLightGizmoHandleKind.Range
                when kind is SceneLightKind.Point or SceneLightKind.Area => start with
                {
                    Range = QuantizeLightValue(Math.Clamp(
                        start.Range + scalarDeltaPixels * worldUnitsPerPixel,
                        0.01f,
                        SceneLightSettings.MaximumRange))
                },
            SceneLightGizmoHandleKind.AreaWidth when kind == SceneLightKind.Area => start with
            {
                AreaSize = start.AreaSize with
                {
                    X = QuantizeLightValue(Math.Clamp(
                        start.AreaSize.X + scalarDeltaPixels * worldUnitsPerPixel * 2f,
                        0.01f,
                        SceneLightSettings.MaximumRange))
                }
            },
            SceneLightGizmoHandleKind.AreaHeight when kind == SceneLightKind.Area => start with
            {
                AreaSize = start.AreaSize with
                {
                    Y = QuantizeLightValue(Math.Clamp(
                        start.AreaSize.Y + scalarDeltaPixels * worldUnitsPerPixel * 2f,
                        0.01f,
                        SceneLightSettings.MaximumRange))
                }
            },
            SceneLightGizmoHandleKind.ShadowStrength
                when kind != SceneLightKind.Ambient && start.CastsShadows => start with
                {
                    ShadowStrength = QuantizeLightValue(Math.Clamp(
                        start.ShadowStrength + scalarDeltaPixels / trackPixels,
                        0f,
                        1f),
                        3)
                },
            SceneLightGizmoHandleKind.ShadowSoftness
                when kind != SceneLightKind.Ambient && start.CastsShadows => start with
                {
                    ShadowSoftness = QuantizeLightValue(Math.Clamp(
                        start.ShadowSoftness + scalarDeltaPixels / trackPixels,
                        0f,
                        1f),
                        3)
                },
            _ => start
        };
    }

    private void CompleteSceneLightGizmoPointer()
    {
        if (_sceneLightGizmoPointerSession is null) return;
        ClearPendingSceneLightGizmoPointer();
        _sceneLightGizmoPointerSession = null;
        _stage.SetSceneLightGizmoActive(SceneLightGizmoHandleHit.None);
        _stage.EndReference3DOpticalInteractionPreview();
        _lastMouse = null;
        _startScreen = null;
        CompleteSceneOpticsEdit();
        _timeline.RefreshTimeline();
        if (_stage.Capture) _stage.Capture = false;
        RefreshInteractionCursorAtPointer();
    }

    private void CancelSceneLightGizmoPointer()
    {
        if (_sceneLightGizmoPointerSession is null) return;
        ClearPendingSceneLightGizmoPointer();
        _sceneLightGizmoPointerSession = null;
        _stage.SetSceneLightGizmoActive(SceneLightGizmoHandleHit.None);
        _stage.EndReference3DOpticalInteractionPreview();
        _lastMouse = null;
        _startScreen = null;
        CancelSceneOpticsEdit();
        if (_stage.Capture) _stage.Capture = false;
        RefreshInteractionCursorAtPointer();
    }

    private bool UpdateSceneLightGizmoCursor(Point screen)
    {
        if (!IsScene3DView() || !_stage.SceneLightGizmoVisible)
        {
            _stage.SetSceneLightGizmoHover(SceneLightGizmoHandleHit.None);
            return false;
        }

        var hit = _stage.HitTestSceneLightGizmo(screen);
        if (_sceneLightGizmoPointerSession is null) _stage.SetSceneLightGizmoHover(hit);
        if (_sceneLightGizmoPointerSession is null && !hit.IsValid) return false;
        _stage.SetSpatialTransformHover(SpatialTransformHandleHit.None);
        _stage.ClearBrushTipCursor();
        _stage.ClearFillToolCursor();
        ClearFillHoverPreview();
        _stage.Cursor = Cursors.SizeAll;
        return true;
    }

    private bool TryGetSceneLightGizmoPlanePoint(
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
        if (!float.IsFinite(distance) || distance < 0) return false;
        point = ray.Origin + ray.Direction * distance;
        return float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);
    }

    private static float ProjectScreenDelta(Vector2 delta, PointF origin, PointF endpoint)
    {
        var axis = new Vector2(endpoint.X - origin.X, endpoint.Y - origin.Y);
        var length = axis.Length();
        return length > 0.001f ? Vector2.Dot(delta, axis / length) : delta.X;
    }

    private static float Distance(Point point, PointF target)
    {
        var dx = point.X - target.X;
        var dy = point.Y - target.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private static float QuantizeLightValue(float value, int decimals = 2) =>
        MathF.Round(value, decimals, MidpointRounding.AwayFromZero);

    private static Vector3 SceneLightPositionAxis(SceneLightGizmoHandleKind handle) => handle switch
    {
        SceneLightGizmoHandleKind.PositionX => Vector3.UnitX,
        SceneLightGizmoHandleKind.PositionY => Vector3.UnitY,
        SceneLightGizmoHandleKind.PositionZ => Vector3.UnitZ,
        _ => Vector3.Zero
    };
}
