using System.Drawing.Drawing2D;
using System.Numerics;

namespace VectorAnimationEngine;

/// <summary>Interaction targets of the shot framing gizmo shown on the Stage.</summary>
internal enum ShotFramingHandleKind : byte
{
    None,
    /// <summary>Frame border band: drag to move the framed viewport.</summary>
    Body,
    TopLeft,
    Top,
    TopRight,
    Right,
    BottomRight,
    Bottom,
    BottomLeft,
    Left,
    /// <summary>Handle above the top edge: drag to rotate the framed viewport.</summary>
    Rotate,
    /// <summary>Camera body and axis handles edit the camera transform itself.</summary>
    CameraBody,
    CameraPositionX,
    CameraPositionY,
    CameraPositionZ,
    CameraRotateX,
    CameraRotateY,
    CameraRotateZ,
    /// <summary>Optical-axis handle edits focal length or orthographic size.</summary>
    CameraLens
}

internal readonly record struct ShotFramingHandleHit(ShotFramingHandleKind Kind)
{
    public static ShotFramingHandleHit None => new(ShotFramingHandleKind.None);

    public bool IsValid => Kind != ShotFramingHandleKind.None;
}

/// <summary>Screen geometry of the shot framing gizmo. Corners follow the framing rotation.</summary>
internal readonly record struct ShotFramingGizmoGeometry(
    PointF TopLeft,
    PointF TopRight,
    PointF BottomRight,
    PointF BottomLeft,
    PointF RotationHandle,
    PointF Center,
    PointF RotationGripStart)
{
    public PointF[] Corners => [TopLeft, TopRight, BottomRight, BottomLeft];

    /// <summary>Trimmed midpoints of the four edges in screen space.</summary>
    public PointF TopMidpoint => Midpoint(TopLeft, TopRight);

    public PointF RightMidpoint => Midpoint(TopRight, BottomRight);

    public PointF BottomMidpoint => Midpoint(BottomRight, BottomLeft);

    public PointF LeftMidpoint => Midpoint(BottomLeft, TopLeft);

    public RectangleF Bounds
    {
        get
        {
            var left = Math.Min(Math.Min(TopLeft.X, TopRight.X), Math.Min(BottomRight.X, BottomLeft.X));
            var top = Math.Min(Math.Min(TopLeft.Y, TopRight.Y), Math.Min(BottomRight.Y, BottomLeft.Y));
            var right = Math.Max(Math.Max(TopLeft.X, TopRight.X), Math.Max(BottomRight.X, BottomLeft.X));
            var bottom = Math.Max(Math.Max(TopLeft.Y, TopRight.Y), Math.Max(BottomRight.Y, BottomLeft.Y));
            return RectangleF.FromLTRB(left, top, right, bottom);
        }
    }

    private static PointF Midpoint(PointF a, PointF b) => new((a.X + b.X) / 2f, (a.Y + b.Y) / 2f);
}

internal enum ShotCameraWireframeSegmentKind : byte
{
    Body,
    Frame,
    Frustum,
    OrthographicGuide,
    RotationRingX,
    RotationRingY,
    RotationRingZ
}

internal readonly record struct ShotCameraWireframeSegment(
    PointF Start,
    PointF End,
    ShotCameraWireframeSegmentKind Kind);

internal readonly record struct ShotCameraWireframeGeometry(
    PointF CameraPoint,
    PointF LensPoint,
    bool HasCameraPoint,
    bool HasLensPoint,
    ShotCameraWireframeSegment[] Segments,
    bool IsPerspective,
    ShotCameraWireframeHandle[] Handles,
    PointF[] BodyFrontCorners,
    PointF[] BodyBackCorners);

internal readonly record struct ShotCameraWireframeHandle(
    ShotFramingHandleKind Kind,
    PointF Point,
    PointF Anchor);

internal readonly record struct ShotCameraWireframeWorldGeometry(
    Vector3 CameraPoint,
    Vector3 LensPoint,
    Vector3[] BodyFrontCorners,
    Vector3[] BodyBackCorners,
    Vector3[] FrameCorners,
    Vector3[] NearFrameCorners,
    bool IsPerspective,
    float RotationDegrees = 0f);

// Shot framing gizmo: the framed viewport of the active shot drawn as a rotatable rectangle with
// move, resize and rotate handles. Geometry is computed in scene units and projected per corner so
// the same code serves the 2D composition view and the projected reference view.
internal sealed partial class StageControl
{
    internal const float ShotFramingHandleHitRadiusPixels = 11f;
    internal const float ShotFramingBorderBandPixels = 7f;
    internal const float ShotFramingRotationGripOffsetPixels = 30f;
    internal const float ShotFramingHandleSizePixels = 8f;
    internal const float ShotCameraHandleTargetPixels = 72f;
    internal const float ShotCameraHandleMinimumSpacingPixels = 20f;
    internal const float ShotCameraHandleMinimumOffsetPixels = 34f;
    internal const float ShotCameraHandleShaftHitRadiusPixels = 8f;
    internal const float ShotCameraRotationRingHitRadiusPixels = 9f;
    internal const float ShotCameraHandleLeaderThresholdPixels = 1.5f;
    private const int ShotCameraRotationRingSegments = 40;
    private const float ShotCameraRotationRingRadiusFactor = 0.92f;
    /// <summary>
    /// Travel of the screen-space optical slider, in device-independent pixels. It is deliberately
    /// generous: the lens range spans more than a decade, so a short slider would quantize every drag
    /// into coarse jumps and make precise framing impossible.
    /// </summary>
    internal const float ShotCameraLensSliderLengthPixels = 190f;

    private SceneShotSettings _shotFramingSettings = SceneShotSettings.Default;
    private string _shotFramingLabel = "";
    private bool _shotFramingVisible;
    private ShotFramingHandleHit _shotFramingHoveredHandle = ShotFramingHandleHit.None;
    private ShotFramingHandleHit _shotFramingActiveHandle = ShotFramingHandleHit.None;

    private readonly record struct ShotProjectionView(
        VectorScene Scene, float StageWidth, float StageHeight, Size Size, int Dpi, bool Reference,
        float CameraX, float CameraY, float Zoom, float Yaw, float Pitch, float Distance,
        float ReferenceZoom, float TargetX, float TargetY, float TargetZ, float Blend);
    private ShotProjectionView _shotProjectionView;
    private readonly Dictionary<(SceneShotSettings Settings, bool Handles), (bool Valid, ShotCameraWireframeGeometry Geometry)> _shotWireframeCache = [];
    private readonly Dictionary<SceneShotSettings, (bool Valid, ShotFramingGizmoGeometry Geometry)> _shotOutlineCache = [];
    internal long ShotWireframeBuildCount { get; private set; }
    private void EnsureShotProjectionView()
    {
        var view = new ShotProjectionView(Scene, Scene.StageWidth, Scene.StageHeight, ClientSize, DeviceDpi,
            RendersReferenceProjection, CameraX, CameraY, Zoom, EffectiveReferenceYaw, EffectiveReferencePitch,
            _referenceDistance, _referenceZoomScale, _referenceTargetX, _referenceTargetY, _referenceTargetZ,
            ReferenceProjectionBlend);
        if (_shotProjectionView == view) return;
        _shotProjectionView = view;
        _shotWireframeCache.Clear();
        _shotOutlineCache.Clear();
    }

    internal readonly record struct ShotFrameDisplay(string Id, SceneShotSettings Settings, bool Selected);
    private ShotFrameDisplay[] _shotFrameDisplays = [];
    internal bool ShotFrameCollectionVisible { get; private set; }
    internal bool HasSelectedShotFrame => _shotFrameDisplays.Any(frame => frame.Selected);
    internal IReadOnlyList<ShotFrameDisplay> ShotFrameDisplays => _shotFrameDisplays;
    internal void SetShotFrameDisplays(ShotFrameDisplay[] frames)
    {
        if (ShotFrameCollectionVisible && _shotFrameDisplays.AsSpan().SequenceEqual(frames)) return;
        var selectionChanged = _shotFrameDisplays.FirstOrDefault(f => f.Selected).Id != frames.FirstOrDefault(f => f.Selected).Id;
        ShotFrameCollectionVisible = true;
        _shotFrameDisplays = frames;
        _shotFramingVisible = frames.Length > 0;
        _shotFramingSettings = frames.FirstOrDefault(f => f.Selected).Settings;
        if (selectionChanged) _shotFramingHoveredHandle = _shotFramingActiveHandle = ShotFramingHandleHit.None;
        InvalidateOverlay();
    }
    internal bool TryGetShotDisplayFrame(SceneShotSettings settings, out ShotFramingGizmoGeometry geometry)
        => TryResolveShotFramingGeometry(settings, out geometry);

    internal bool ShotFramingGizmoVisible => _shotFramingVisible;

    internal SceneShotSettings ShotFramingGizmoSettings => _shotFramingSettings;

    internal ShotFramingHandleHit ShotFramingHighlightedHandle =>
        _shotFramingActiveHandle.IsValid ? _shotFramingActiveHandle : _shotFramingHoveredHandle;

    /// <summary>Shows the framing gizmo of one shot. <paramref name="label"/> is drawn next to the frame.</summary>
    internal void SetShotFramingGizmo(SceneShotSettings settings, string label, bool visible = true)
    {
        if (ShotFrameCollectionVisible)
            _shotFrameDisplays = _shotFrameDisplays.Select(frame => frame.Selected ? frame with { Settings = settings.Normalized() } : frame).ToArray();
        var normalized = settings.Normalized();
        var labelChanged = !string.Equals(_shotFramingLabel, label, StringComparison.Ordinal);
        if (_shotFramingVisible == visible && _shotFramingSettings == normalized && !labelChanged) return;
        _shotFramingVisible = visible;
        _shotFramingSettings = normalized;
        _shotFramingLabel = label;
        if (!visible)
        {
            _shotFramingHoveredHandle = ShotFramingHandleHit.None;
            _shotFramingActiveHandle = ShotFramingHandleHit.None;
        }

        InvalidateOverlay();
    }

    /// <summary>Test hook: reports whether the framing gizmo is currently hidden.</summary>
    internal bool IsShotFramingGizmoClearedForTesting => !_shotFramingVisible;

    internal void ClearShotFramingGizmo()
    {
        ShotFrameCollectionVisible = false;
        _shotFrameDisplays = [];
        if (!_shotFramingVisible
            && !_shotFramingHoveredHandle.IsValid
            && !_shotFramingActiveHandle.IsValid)
        {
            return;
        }

        _shotFramingVisible = false;
        _shotFramingHoveredHandle = ShotFramingHandleHit.None;
        _shotFramingActiveHandle = ShotFramingHandleHit.None;
        InvalidateOverlay();
    }

    internal void SetShotFramingHover(ShotFramingHandleHit hit)
    {
        if (_shotFramingHoveredHandle == hit) return;
        _shotFramingHoveredHandle = hit;
        InvalidateOverlay();
    }

    internal void SetShotFramingActive(ShotFramingHandleHit hit)
    {
        if (_shotFramingActiveHandle == hit) return;
        _shotFramingActiveHandle = hit;
        InvalidateOverlay();
    }

    /// <summary>Screen geometry of the framed viewport; false when the frame cannot be projected.</summary>
    internal bool TryGetShotFramingGizmoGeometry(out ShotFramingGizmoGeometry geometry)
    {
        return TryResolveShotFramingGeometry(_shotFramingSettings, out geometry);
    }

    /// <summary>Builds the Blender-like camera object shown on the Stage.</summary>
    internal bool TryGetShotCameraWireframeGeometry(out ShotCameraWireframeGeometry geometry)
        => TryGetShotCameraWireframeGeometry(_shotFramingSettings, out geometry);

    internal bool TryGetShotCameraWireframeGeometry(SceneShotSettings settings, out ShotCameraWireframeGeometry geometry, bool handlesVisible = true)
    {
        if (Scene is null) { geometry = default; return false; }
        EnsureShotProjectionView();
        var key = (settings, handlesVisible);
        if (_shotWireframeCache.TryGetValue(key, out var cached))
        { geometry = cached.Geometry; return cached.Valid; }
        var valid = BuildShotCameraWireframeGeometry(settings, out geometry, handlesVisible);
        if (_shotWireframeCache.Count >= 512) _shotWireframeCache.Clear();
        _shotWireframeCache[key] = (valid, geometry);
        ShotWireframeBuildCount++;
        return valid;
    }

    private bool BuildShotCameraWireframeGeometry(SceneShotSettings settings, out ShotCameraWireframeGeometry geometry, bool handlesVisible)
    {
        geometry = default;
        var scene = Scene;
        if (scene is null || Width <= 0 || Height <= 0) return false;

        var world = ResolveShotCameraWireframeWorld(
            settings,
            scene.StageWidth,
            scene.StageHeight);
        var segments = new List<ShotCameraWireframeSegment>(24);
        AppendClosedShotCameraWireframeSegments(
            segments,
            world.BodyFrontCorners,
            ShotCameraWireframeSegmentKind.Body);
        AppendClosedShotCameraWireframeSegments(
            segments,
            world.BodyBackCorners,
            ShotCameraWireframeSegmentKind.Body);
        AppendShotCameraWireframeConnections(
            segments,
            world.BodyFrontCorners,
            world.BodyBackCorners,
            ShotCameraWireframeSegmentKind.Body);
        foreach (var corner in world.BodyFrontCorners)
        {
            AppendShotCameraWireframeSegment(
                segments,
                world.LensPoint,
                corner,
                ShotCameraWireframeSegmentKind.Body);
        }

        if (world.IsPerspective)
        {
            AppendClosedShotCameraWireframeSegments(
                segments,
                world.FrameCorners,
                ShotCameraWireframeSegmentKind.Frame);
            foreach (var corner in world.FrameCorners)
            {
                AppendShotCameraWireframeSegment(
                    segments,
                    world.LensPoint,
                    corner,
                    ShotCameraWireframeSegmentKind.Frustum);
            }
        }
        else
        {
            AppendClosedShotCameraWireframeSegments(
                segments,
                world.NearFrameCorners,
                ShotCameraWireframeSegmentKind.Frame);
            AppendClosedShotCameraWireframeSegments(
                segments,
                world.FrameCorners,
                ShotCameraWireframeSegmentKind.Frame);
            AppendShotCameraWireframeConnections(
                segments,
                world.NearFrameCorners,
                world.FrameCorners,
                ShotCameraWireframeSegmentKind.OrthographicGuide);
        }

        if (handlesVisible)
        {
            var ringRadius = ResolveShotCameraHandleLength(world,
                ShotCameraBodyWidth(world), ShotCameraBodyHeight(world)) * ShotCameraRotationRingRadiusFactor;
            AppendShotCameraRotationRingSegments(segments, world, ringRadius);
        }

        var hasCameraPoint = TryProjectShotCameraWireframePoint(world.CameraPoint, out var cameraPoint);
        var hasLensPoint = TryProjectShotCameraWireframePoint(world.LensPoint, out var lensPoint);
        var bodyFrontCorners = ProjectShotCameraWireframePoints(world.BodyFrontCorners);
        var bodyBackCorners = ProjectShotCameraWireframePoints(world.BodyBackCorners);
        var handles = handlesVisible ? ResolveShotCameraWireframeHandles(settings, world) : [];
        if (segments.Count == 0 && !hasCameraPoint && !hasLensPoint && handles.Length == 0) return false;
        geometry = new ShotCameraWireframeGeometry(
            cameraPoint,
            lensPoint,
            hasCameraPoint,
            hasLensPoint,
            segments.ToArray(),
            world.IsPerspective,
            handles,
            bodyFrontCorners,
            bodyBackCorners);
        return true;
    }

    internal bool TryGetShotCameraWireframeHandlePoint(
        ShotFramingHandleKind kind,
        out PointF point)
    {
        point = PointF.Empty;
        if (!TryGetShotCameraWireframeGeometry(out var geometry)) return false;
        foreach (var handle in geometry.Handles)
        {
            if (handle.Kind != kind) continue;
            point = handle.Point;
            return true;
        }

        return false;
    }

    /// <summary>
    /// World units the camera must move for the pointer to travel one screen pixel at the camera's own
    /// depth. Body and lens drags use it to map pointer travel onto camera motion 1:1, so the handle
    /// stays under the cursor instead of drifting by a projection-dependent factor.
    /// </summary>
    internal bool TryGetShotCameraScreenScale(out float worldPerPixel)
    {
        worldPerPixel = 0f;
        var scene = Scene;
        if (scene is null || Width <= 0 || Height <= 0) return false;
        var world = ResolveShotCameraWireframeWorld(
            _shotFramingSettings,
            scene.StageWidth,
            scene.StageHeight);
        worldPerPixel = RendersReferenceProjection
            ? ResolveShotCameraReferenceWorldPerPixel(world.CameraPoint)
            : ScreenLengthToWorld(1f);
        return float.IsFinite(worldPerPixel) && worldPerPixel > 0f;
    }

    private float ResolveShotCameraReferenceWorldPerPixel(Vector3 cameraPoint)
    {
        if (!TryProjectScenePosition(cameraPoint, out _, out var depth)) return 0f;
        var scale = 0.035f * ReferenceZoomScale * ReferencePerspectiveScale(depth);
        return float.IsFinite(scale) && scale > 0.0001f ? 1f / scale : 0f;
    }

    /// <summary>
    /// Returns the world-space point used as the origin of camera handles. It is the rear body
    /// centre of the displayed camera, rather than the optical centre, so pointer intersections
    /// start exactly where the visible axis lines begin.
    /// </summary>
    internal bool TryGetShotCameraWireframeInteractionOrigin(out Vector3 origin)
    {
        origin = default;
        var scene = Scene;
        if (scene is null || Width <= 0 || Height <= 0) return false;
        origin = ResolveShotCameraWireframeWorld(
            _shotFramingSettings,
            scene.StageWidth,
            scene.StageHeight).CameraPoint;
        return Finite(origin);
    }

    internal static bool IsShotCameraHandle(ShotFramingHandleKind kind) => kind is
        ShotFramingHandleKind.CameraBody
            or ShotFramingHandleKind.CameraPositionX
            or ShotFramingHandleKind.CameraPositionY
            or ShotFramingHandleKind.CameraPositionZ
            or ShotFramingHandleKind.CameraRotateX
            or ShotFramingHandleKind.CameraRotateY
            or ShotFramingHandleKind.CameraRotateZ
            or ShotFramingHandleKind.CameraLens;

    internal static bool IsShotCameraPositionHandle(ShotFramingHandleKind kind) => kind is
        ShotFramingHandleKind.CameraPositionX
            or ShotFramingHandleKind.CameraPositionY
            or ShotFramingHandleKind.CameraPositionZ;

    internal static bool IsShotCameraRotationHandle(ShotFramingHandleKind kind) => kind is
        ShotFramingHandleKind.CameraRotateX
            or ShotFramingHandleKind.CameraRotateY
            or ShotFramingHandleKind.CameraRotateZ;

    internal static Color ShotCameraHandleColor(ShotFramingHandleKind kind) => kind switch
    {
        ShotFramingHandleKind.CameraPositionX
            or ShotFramingHandleKind.CameraRotateX => Color.FromArgb(255, 238, 112, 112),
        ShotFramingHandleKind.CameraPositionY
            or ShotFramingHandleKind.CameraRotateY => Color.FromArgb(255, 124, 220, 144),
        ShotFramingHandleKind.CameraPositionZ
            or ShotFramingHandleKind.CameraRotateZ => Color.FromArgb(255, 112, 168, 255),
        ShotFramingHandleKind.CameraLens => Color.FromArgb(255, 255, 196, 96),
        _ => Color.FromArgb(255, 112, 204, 255)
    };

    private ShotCameraWireframeHandle[] ResolveShotCameraWireframeHandles(
        SceneShotSettings settings,
        ShotCameraWireframeWorldGeometry world)
    {
        var normalized = settings.Normalized();
        var rotation = SpatialTransformMath.CreateRotation(
            normalized.RotationX,
            normalized.RotationY,
            normalized.RotationDegrees);
        var localRight = NormalizeShotVector(
            Vector3.TransformNormal(Vector3.UnitX, rotation),
            Vector3.UnitX);
        var localForward = NormalizeShotVector(
            Vector3.TransformNormal(Vector3.UnitZ, rotation),
            Vector3.UnitZ);
        // Position and rotation handles are deliberately laid out on the world axes. The values
        // they edit are the camera's world XYZ/Euler fields, so a rotated wireframe must not make
        // a handle look local while changing a different direction in the model.
        var axisX = Vector3.UnitX;
        var axisY = Vector3.UnitY;
        var axisZ = Vector3.UnitZ;
        var bodyWidth = ShotCameraBodyWidth(world);
        var bodyHeight = ShotCameraBodyHeight(world);
        var handleLength = ResolveShotCameraHandleLength(world, bodyWidth, bodyHeight);
        var origin = world.CameraPoint;
        var candidates = ResolveShotCameraAxisHandleCandidates(settings, world);
        var handles = new List<ShotCameraWireframeHandle>(candidates.Count + 1);
        var placed = new List<PointF>(candidates.Count + 1);
        var hasOriginScreen = TryProjectShotCameraWireframePoint(origin, out var originScreen);
        var minimumSpacing = ShotCameraHandleMinimumSpacingPixels * SpatialGizmoDpiScale;
        var minimumOffset = ShotCameraHandleMinimumOffsetPixels * SpatialGizmoDpiScale;
        foreach (var (kind, point) in candidates)
        {
            if (TryProjectShotCameraWireframePoint(point, out var anchor))
            {
                var screen = anchor;
                if (kind != ShotFramingHandleKind.CameraBody && hasOriginScreen)
                {
                    // The rotation handle carries its own geometry: the ring angle. Snapping it onto a
                    // fixed fallback direction, or rotating it to dodge another handle, would discard
                    // exactly the value it expresses, so it is placed where it projects.
                    if (kind != ShotFramingHandleKind.CameraRotateZ)
                    {
                        screen = StabilizeShotCameraHandlePoint(
                            kind,
                            screen,
                            originScreen,
                            placed,
                            minimumSpacing,
                            minimumOffset);
                    }
                }

                handles.Add(new ShotCameraWireframeHandle(kind, screen, anchor));
                placed.Add(screen);
            }
        }

        // The optical handle is a screen-space slider rather than a world-space offset. Its length is
        // measured in pixels along a direction chosen to have free room, so it is always reachable and
        // its travel maps predictably onto the lens value. A world-space offset collapsed to a point on
        // many views and sat on top of the body, which made the old diamond handle nearly impossible to
        // grab.
        if (TryResolveShotCameraLensSlider(
                normalized,
                world,
                out originScreen,
                out var sliderDirection,
                out var sliderTravel))
        {
            var sliderPoint = ShotCameraLensSliderPoint(
                originScreen,
                ShotCameraLensSliderFraction(normalized),
                sliderDirection,
                sliderTravel);
            handles.Add(new ShotCameraWireframeHandle(
                ShotFramingHandleKind.CameraLens,
                sliderPoint,
                originScreen));
            placed.Add(sliderPoint);
        }

        return handles.ToArray();
    }

    /// <summary>
    /// Resolves the optical slider's screen origin, direction, and usable travel for the given
    /// settings. Drawing and dragging both go through this, so the pointer mapping always matches the
    /// control the user grabbed.
    /// </summary>
    private bool TryResolveShotCameraLensSlider(
        SceneShotSettings settings,
        ShotCameraWireframeWorldGeometry world,
        out PointF originScreen,
        out Vector2 direction,
        out float travel)
    {
        originScreen = PointF.Empty;
        direction = Vector2.UnitX;
        travel = 0f;
        if (!TryProjectShotCameraWireframePoint(world.CameraPoint, out originScreen)) return false;
        var neighbours = ResolveShotCameraSliderNeighbours(settings, world);
        var minimumSpacing = ShotCameraHandleMinimumSpacingPixels * SpatialGizmoDpiScale;
        direction = ShotCameraLensSliderScreenDirection(originScreen, neighbours, minimumSpacing);
        travel = ResolveShotCameraLensSliderTravel();
        return float.IsFinite(travel) && travel > 1f;
    }

    /// <summary>
    /// Screen positions of the other camera handles, used to keep the optical slider clear of them.
    /// The body is excluded because the slider's travel is measured from it; including it would make
    /// every direction look blocked at zero distance.
    /// </summary>
    private List<PointF> ResolveShotCameraSliderNeighbours(
        SceneShotSettings settings,
        ShotCameraWireframeWorldGeometry world)
    {
        var placed = new List<PointF>();
        foreach (var (kind, point) in ResolveShotCameraAxisHandleCandidates(settings, world))
        {
            if (kind == ShotFramingHandleKind.CameraBody) continue;
            if (TryProjectShotCameraWireframePoint(point, out var screen)) placed.Add(screen);
        }

        return placed;
    }

    /// <summary>
    /// Axis and ring handles of the camera object, in world space. It is shared by drawing, slider
    /// placement, and slider dragging so all three agree on the layout.
    /// </summary>
    private List<(ShotFramingHandleKind Kind, Vector3 Point)> ResolveShotCameraAxisHandleCandidates(
        SceneShotSettings settings,
        ShotCameraWireframeWorldGeometry world)
    {
        var normalized = settings.Normalized();
        var handleLength = ResolveShotCameraHandleLength(
            world,
            ShotCameraBodyWidth(world),
            ShotCameraBodyHeight(world));
        var origin = world.CameraPoint;
        var candidates = new List<(ShotFramingHandleKind Kind, Vector3 Point)>
        {
            (ShotFramingHandleKind.CameraBody, origin),
            (ShotFramingHandleKind.CameraPositionX, origin + Vector3.UnitX * handleLength),
            (ShotFramingHandleKind.CameraPositionY, origin + Vector3.UnitY * handleLength),
        };

        if (RendersReferenceProjection)
        {
            candidates.Add((ShotFramingHandleKind.CameraPositionZ, origin + Vector3.UnitZ * handleLength));
            // A 2D camera pins X/Y rotation, so its planar rotation rings are not offered. It still
            // keeps the Z dolly shaft (real depth) and the Z rotation ring.
            if (normalized.AllowsPlanarRotationEdits)
            {
                candidates.Add((
                    ShotFramingHandleKind.CameraRotateX,
                    origin + Vector3.UnitY * (handleLength * ShotCameraRotationRingRadiusFactor)));
                candidates.Add((
                    ShotFramingHandleKind.CameraRotateY,
                    origin + Vector3.UnitZ * (handleLength * ShotCameraRotationRingRadiusFactor)));
            }
        }

        // In the flat Stage, Z is the view-normal rotation axis, so its marker rides the visible XY
        // ring rather than a zero-length Z shaft.
        candidates.Add((
            ShotFramingHandleKind.CameraRotateZ,
            ResolveShotCameraRotationGripPoint(world, handleLength)));
        return candidates;
    }

    /// <summary>
    /// <summary>
    /// Screen-space direction the optical slider extends along. The four cardinal directions are taken
    /// by the axis handles and the rotation-ring grips, so the slider uses a diagonal, where it reads
    /// as a distinct control. Among the diagonals the one whose span keeps the most distance from the
    /// existing handles is chosen, so the knob stays easy to grab.
    /// </summary>
    private Vector2 ShotCameraLensSliderScreenDirection(
        PointF originScreen,
        IReadOnlyList<PointF> placed,
        float minimumSpacing)
    {
        var length = ShotCameraLensSliderLengthPixels * SpatialGizmoDpiScale;
        var diagonal = 0.70710678f;
        var candidates = new[]
        {
            new Vector2(diagonal, diagonal),
            new Vector2(diagonal, -diagonal),
            new Vector2(-diagonal, diagonal),
            new Vector2(-diagonal, -diagonal)
        };
        var bestDirection = candidates[0];
        var bestClearance = float.MinValue;
        foreach (var candidate in candidates)
        {
            // Score by how close any existing handle comes to the slider's span. The travel length
            // itself is fixed: shortening it would cripple the control, and the hit test already gives
            // the slider priority at its own knob.
            var clearance = float.MaxValue;
            for (var step = 0; step <= 8; step++)
            {
                var travel = length * (step / 8f);
                var probe = new PointF(
                    originScreen.X + candidate.X * travel,
                    originScreen.Y + candidate.Y * travel);
                foreach (var existing in placed)
                {
                    var distance = ShotFramingDistance(existing, probe);
                    if (distance < clearance) clearance = distance;
                }
            }

            // Keep the knob on the Stage, so it stays reachable near the edges.
            var tipX = originScreen.X + candidate.X * length;
            var tipY = originScreen.Y + candidate.Y * length;
            if (tipX < 0f || tipY < 0f || tipX > ClientSize.Width || tipY > ClientSize.Height)
            {
                clearance -= 10_000f;
            }

            if (clearance > bestClearance)
            {
                bestClearance = clearance;
                bestDirection = candidate;
            }
        }

        return bestDirection;
    }

    /// <summary>
    /// Travel length of the optical slider. It is a fixed screen length: the knob's precision comes
    /// from the travel, so shortening it to dodge a neighbouring marker would make the lens impossible
    /// to set accurately. Collisions at the knob are resolved by hit-test priority instead.
    /// </summary>
    private float ResolveShotCameraLensSliderTravel() =>
        ShotCameraLensSliderLengthPixels * SpatialGizmoDpiScale;

    /// <summary>
    /// Screen position of the optical slider's knob. The fraction maps onto the span between the
    /// minimum stand-off and the usable travel, and the drag solver uses the identical span, so the
    /// knob lands exactly where the pointer is.
    /// </summary>
    private PointF ShotCameraLensSliderPoint(
        PointF originScreen,
        float fraction,
        Vector2 direction,
        float travel)
    {
        var clamped = Math.Clamp(float.IsFinite(fraction) ? fraction : 0.5f, 0f, 1f);
        var distance = ShotCameraLensSliderDistance(clamped, travel);
        return new PointF(
            originScreen.X + direction.X * distance,
            originScreen.Y + direction.Y * distance);
    }

    /// <summary>
    /// Distance from the body to the optical knob for a normalized lens value. Both drawing and
    /// dragging derive the knob position from this single mapping.
    /// </summary>
    private float ShotCameraLensSliderDistance(float fraction, float travel)
    {
        var offset = Math.Min(
            ShotCameraHandleMinimumOffsetPixels * SpatialGizmoDpiScale,
            travel * 0.5f);
        return offset + (travel - offset) * Math.Clamp(fraction, 0f, 1f);
    }

    /// <summary>
    /// Normalized slider position for the current lens. Focal length and orthographic size are mapped
    /// through a logarithmic scale because lens perception is multiplicative: every doubling of the
    /// focal length is one equal step, so the slider feels the same at 18 mm and at 300 mm.
    /// </summary>
    internal static float ShotCameraLensSliderFraction(SceneShotSettings settings)
    {
        var normalized = settings.Normalized();
        float value;
        float minimum;
        float maximum;
        if (normalized.Projection == CameraProjection.Orthographic)
        {
            value = normalized.OrthographicSize;
            minimum = SceneShotSettings.MinimumOrthographicSize;
            maximum = SceneShotSettings.MaximumOrthographicSize;
        }
        else
        {
            value = normalized.FocalLength;
            minimum = SceneShotSettings.MinimumFocalLength;
            maximum = SceneShotSettings.MaximumFocalLength;
        }

        var logValue = MathF.Log(Math.Max(minimum, value));
        var logMinimum = MathF.Log(minimum);
        var logMaximum = MathF.Log(maximum);
        var span = logMaximum - logMinimum;
        if (!float.IsFinite(span) || span <= 0.0001f) return 0.5f;
        var fraction = (logValue - logMinimum) / span;
        return float.IsFinite(fraction) ? Math.Clamp(fraction, 0f, 1f) : 0.5f;
    }

    /// <summary>
    /// Lens value for a normalized slider position. This is the exact inverse of
    /// <see cref="ShotCameraLensSliderFraction"/>, so dragging the slider to the pointer's own
    /// position lands the handle under the cursor.
    /// </summary>
    internal static float ResolveShotCameraLensFromSliderFraction(
        SceneShotSettings settings,
        float fraction)
    {
        var normalized = settings.Normalized();
        float minimum;
        float maximum;
        if (normalized.Projection == CameraProjection.Orthographic)
        {
            minimum = SceneShotSettings.MinimumOrthographicSize;
            maximum = SceneShotSettings.MaximumOrthographicSize;
        }
        else
        {
            minimum = SceneShotSettings.MinimumFocalLength;
            maximum = SceneShotSettings.MaximumFocalLength;
        }

        var clamped = float.IsFinite(fraction) ? Math.Clamp(fraction, 0f, 1f) : 0.5f;
        var logMinimum = MathF.Log(minimum);
        var logMaximum = MathF.Log(maximum);
        var value = MathF.Exp(logMinimum + (logMaximum - logMinimum) * clamped);
        return Math.Clamp(
            value,
            normalized.Projection == CameraProjection.Orthographic
                ? SceneShotSettings.MinimumOrthographicSize
                : SceneShotSettings.MinimumFocalLength,
            normalized.Projection == CameraProjection.Orthographic
                ? SceneShotSettings.MaximumOrthographicSize
                : SceneShotSettings.MaximumFocalLength);
    }


    /// <summary>
    /// Grab point of the Z rotation ring. It rides the ring at the camera's current planar rotation, so
    /// turning the camera sweeps the marker around the ring and the handle stays under the pointer
    /// instead of being pinned to a fixed world direction.
    /// </summary>
    private Vector3 ResolveShotCameraRotationGripPoint(
        ShotCameraWireframeWorldGeometry world,
        float handleLength)
    {
        var radius = handleLength * ShotCameraRotationRingRadiusFactor;
        var radians = world.RotationDegrees * MathF.PI / 180f;
        if (!float.IsFinite(radians)) radians = 0f;
        var grip = world.CameraPoint
            + Vector3.UnitX * (MathF.Cos(radians) * radius)
            + Vector3.UnitY * (MathF.Sin(radians) * radius);

        return grip;
    }

    private static float ShotCameraBodyWidth(ShotCameraWireframeWorldGeometry world) =>
        world.BodyFrontCorners.Length >= 2
            ? Vector3.Distance(world.BodyFrontCorners[0], world.BodyFrontCorners[1])
            : 600f;

    private static float ShotCameraBodyHeight(ShotCameraWireframeWorldGeometry world) =>
        world.BodyFrontCorners.Length >= 4
            ? Vector3.Distance(world.BodyFrontCorners[0], world.BodyFrontCorners[3])
            : 400f;

    private void AppendShotCameraRotationRingSegments(
        List<ShotCameraWireframeSegment> segments,
        ShotCameraWireframeWorldGeometry world,
        float radius)
    {
        if (!float.IsFinite(radius) || radius <= 0f) return;
        var rings = RendersReferenceProjection
            ? new[]
            {
                (ShotCameraWireframeSegmentKind.RotationRingX, Vector3.UnitY, Vector3.UnitZ),
                (ShotCameraWireframeSegmentKind.RotationRingY, Vector3.UnitZ, Vector3.UnitX),
                (ShotCameraWireframeSegmentKind.RotationRingZ, Vector3.UnitX, Vector3.UnitY)
            }
            : new[]
            {
                (ShotCameraWireframeSegmentKind.RotationRingZ, Vector3.UnitX, Vector3.UnitY)
            };

        foreach (var (kind, firstAxis, secondAxis) in rings)
        {
            var previous = world.CameraPoint + firstAxis * radius;
            for (var index = 1; index <= ShotCameraRotationRingSegments; index++)
            {
                var angle = index * MathF.Tau / ShotCameraRotationRingSegments;
                var current = world.CameraPoint
                    + (firstAxis * MathF.Cos(angle) + secondAxis * MathF.Sin(angle)) * radius;
                AppendShotCameraWireframeSegment(segments, previous, current, kind);
                previous = current;
            }
        }
    }

    private PointF[] ProjectShotCameraWireframePoints(IReadOnlyList<Vector3> points)
    {
        if (points.Count == 0) return [];
        var projected = new PointF[points.Count];
        for (var index = 0; index < points.Count; index++)
        {
            if (!TryProjectShotCameraWireframePoint(points[index], out projected[index])) return [];
        }

        return projected;
    }

    private float ResolveShotCameraHandleLength(
        ShotCameraWireframeWorldGeometry world,
        float bodyWidth,
        float bodyHeight)
    {
        var targetPixels = ShotCameraHandleTargetPixels * SpatialGizmoDpiScale;
        var targetWorldLength = 420f;
        if (!RendersReferenceProjection)
        {
            targetWorldLength = ScreenLengthToWorld(targetPixels);
        }
        else if (TryProjectScenePosition(world.CameraPoint, out _, out var depth))
        {
            var scale = 0.035f * ReferenceZoomScale * ReferencePerspectiveScale(depth);
            if (float.IsFinite(scale) && scale > 0.0001f)
            {
                targetWorldLength = targetPixels / scale;
            }
        }

        if (!float.IsFinite(targetWorldLength) || targetWorldLength <= 0f)
        {
            targetWorldLength = 420f;
        }

        var bodyLength = Math.Max(1f, Math.Max(bodyWidth, bodyHeight) * 1.25f);
        var length = Math.Max(targetWorldLength, bodyLength);
        var maximum = Math.Max(100_000f, targetWorldLength * 4f);
        return Math.Clamp(length, 80f, maximum);
    }

    private static PointF StabilizeShotCameraHandlePoint(
        ShotFramingHandleKind kind,
        PointF point,
        PointF origin,
        IReadOnlyList<PointF> placed,
        float minimumSpacing,
        float minimumOffset)
    {
        var fallback = ShotCameraHandleFallbackDirection(kind);
        var offset = new Vector2(point.X - origin.X, point.Y - origin.Y);
        var distance = offset.Length();
        if (!float.IsFinite(distance) || distance < minimumOffset)
        {
            point = new PointF(
                origin.X + fallback.X * minimumOffset,
                origin.Y + fallback.Y * minimumOffset);
            distance = minimumOffset;
        }

        if (!placed.Any(existing => ShotFramingDistance(existing, point) < minimumSpacing))
        {
            return point;
        }

        var radius = Math.Max(minimumOffset, distance);
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var angle = attempt * MathF.Tau / 16f;
            var direction = RotateScreenDirection(fallback, angle);
            var candidate = new PointF(
                origin.X + direction.X * radius,
                origin.Y + direction.Y * radius);
            if (placed.All(existing => ShotFramingDistance(existing, candidate) >= minimumSpacing))
            {
                return candidate;
            }
        }

        return point;
    }

    private static Vector2 ShotCameraHandleFallbackDirection(ShotFramingHandleKind kind) => kind switch
    {
        ShotFramingHandleKind.CameraPositionX => new Vector2(1f, 0f),
        ShotFramingHandleKind.CameraPositionY => new Vector2(0f, 1f),
        ShotFramingHandleKind.CameraPositionZ => new Vector2(0f, -1f),
        ShotFramingHandleKind.CameraRotateX => new Vector2(-0.7071f, -0.7071f),
        ShotFramingHandleKind.CameraRotateY => new Vector2(0.7071f, -0.7071f),
        ShotFramingHandleKind.CameraRotateZ => new Vector2(-1f, 0f),
        ShotFramingHandleKind.CameraLens => new Vector2(1f, 0f),
        _ => new Vector2(0f, -1f)
    };

    private static Vector2 RotateScreenDirection(Vector2 direction, float radians)
    {
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);
        return new Vector2(
            direction.X * cos - direction.Y * sin,
            direction.X * sin + direction.Y * cos);
    }

    private static Vector3 CenterOf(IReadOnlyList<Vector3> points)
    {
        if (points.Count == 0) return Vector3.Zero;
        var center = Vector3.Zero;
        foreach (var point in points) center += point;
        return center / points.Count;
    }

    /// <summary>Returns camera wireframe points in scene space for geometry and regression checks.</summary>
    internal static ShotCameraWireframeWorldGeometry ResolveShotCameraWireframeWorld(
        SceneShotSettings settings,
        float canvasWidth,
        float canvasHeight)
    {
        var normalized = settings.Normalized();
        var width = Math.Max(1f, canvasWidth);
        var height = Math.Max(1f, canvasHeight);
        var aspect = normalized.ResolveAspectRatio(width, height);
        var rotation = SpatialTransformMath.CreateRotation(
            normalized.RotationX,
            normalized.RotationY,
            normalized.RotationDegrees);
        var right = NormalizeShotVector(
            Vector3.TransformNormal(Vector3.UnitX, rotation),
            Vector3.UnitX);
        // Scene Y grows down in the Stage coordinate system, so local +Y becomes screen-up here.
        var screenUp = NormalizeShotVector(
            -Vector3.TransformNormal(Vector3.UnitY, rotation),
            -Vector3.UnitY);
        var forward = NormalizeShotVector(
            Vector3.TransformNormal(Vector3.UnitZ, rotation),
            Vector3.UnitZ);
        var halfHeight = normalized.Projection == CameraProjection.Orthographic
            ? Math.Max(1f, normalized.OrthographicSize * 0.5f)
            : Math.Max(1f, height * 25f / Math.Max(SceneShotSettings.MinimumFocalLength, normalized.FocalLength));
        halfHeight = Math.Max(halfHeight, SceneShotSettings.MinimumFrameHalfHeight(aspect));
        var halfWidth = Math.Max(SceneShotSettings.MinimumFrameWidth * 0.5f, halfHeight * aspect);
        var position = normalized.Position;
        var targetDistance = Vector3.Dot(-position, forward);
        var fallbackDistance = Math.Max(1_000f, halfHeight * 0.35f);
        var displayDistance = float.IsFinite(targetDistance) && targetDistance > ReferenceNearPlane
            ? targetDistance
            : fallbackDistance;
        displayDistance = Math.Clamp(displayDistance, 600f, 2_000_000f);

        var bodyDepth = Math.Clamp(displayDistance * 0.08f, 240f, 1_800f);
        displayDistance = Math.Max(displayDistance, bodyDepth * 4f);
        var bodyHalfHeight = Math.Clamp(halfHeight * 0.09f, 220f, 1_600f);
        var bodyHalfWidth = bodyHalfHeight * 1.3f;
        var frameCenter = position + forward * displayDistance;
        var bodyFrontCenter = position - forward * (bodyDepth * 0.35f);
        var bodyBackCenter = position - forward * (bodyDepth * 1.35f);
        var bodyFront = BuildShotCameraRectangle(bodyFrontCenter, right, screenUp, bodyHalfWidth, bodyHalfHeight);
        var bodyBack = BuildShotCameraRectangle(bodyBackCenter, right, screenUp, bodyHalfWidth * 0.82f, bodyHalfHeight * 0.82f);
        var frame = BuildShotCameraRectangle(frameCenter, right, screenUp, halfWidth, halfHeight);

        if (normalized.Projection == CameraProjection.Perspective)
        {
            return new ShotCameraWireframeWorldGeometry(
                bodyBackCenter,
                position,
                bodyFront,
                bodyBack,
                frame,
                [],
                IsPerspective: true,
                normalized.RotationDegrees);
        }

        var nearDistance = Math.Clamp(
            displayDistance * 0.55f,
            bodyDepth * 2.5f,
            displayDistance * 0.82f);
        var nearFrame = BuildShotCameraRectangle(
            position + forward * nearDistance,
            right,
            screenUp,
            halfWidth,
            halfHeight);
        return new ShotCameraWireframeWorldGeometry(
            bodyBackCenter,
            position,
            bodyFront,
            bodyBack,
            frame,
            nearFrame,
            IsPerspective: false,
            normalized.RotationDegrees);
    }

    internal bool TryResolveShotFramingGeometry(SceneShotSettings settings, out ShotFramingGizmoGeometry geometry)
    {
        if (Scene is null) { geometry = default; return false; }
        EnsureShotProjectionView();
        if (_shotOutlineCache.TryGetValue(settings, out var cached))
        { geometry = cached.Geometry; return cached.Valid; }
        var valid = BuildShotFramingGeometry(settings, out geometry);
        if (_shotOutlineCache.Count >= 512) _shotOutlineCache.Clear();
        _shotOutlineCache[settings] = (valid, geometry);
        return valid;
    }

    private bool BuildShotFramingGeometry(SceneShotSettings settings, out ShotFramingGizmoGeometry geometry)
    {
        geometry = default;
        var scene = Scene;
        if (scene is null || Width <= 0 || Height <= 0) return false;

        // Project the camera's real world-space framed plane. Falling back to the legacy flat 2D
        // framing would discard X/Y rotation and Z depth, which is exactly what made the shot preview
        // disagree with the Stage.
        var worldCorners = settings.Normalized().ResolveWorldFrameCorners(scene.StageWidth, scene.StageHeight);
        var projected = new PointF[worldCorners.Length];
        for (var index = 0; index < worldCorners.Length; index++)
        {
            if (!TryProjectShotPointToScreen(worldCorners[index], out projected[index])) return false;
        }

        // ResolveWorldFrameCorners is mathematical TL/TR/BR/BL; the screen projection keeps that
        // order, and Stage screen Y already grows downward.
        var topLeft = projected[0];
        var topRight = projected[1];
        var bottomRight = projected[2];
        var bottomLeft = projected[3];
        if (!IsSanePoint(topLeft)
            || !IsSanePoint(topRight)
            || !IsSanePoint(bottomRight)
            || !IsSanePoint(bottomLeft))
        {
            return false;
        }

        var center = new PointF(
            (topLeft.X + topRight.X + bottomRight.X + bottomLeft.X) / 4f,
            (topLeft.Y + topRight.Y + bottomRight.Y + bottomLeft.Y) / 4f);
        var topMidpoint = new PointF((topLeft.X + topRight.X) / 2f, (topLeft.Y + topRight.Y) / 2f);
        var up = Normalize(new PointF(topMidpoint.X - center.X, topMidpoint.Y - center.Y));
        var gripDistance = ShotFramingRotationGripOffsetPixels * SpatialGizmoDpiScale;
        var rotationHandle = new PointF(
            topMidpoint.X + up.X * gripDistance,
            topMidpoint.Y + up.Y * gripDistance);
        geometry = new ShotFramingGizmoGeometry(
            topLeft,
            topRight,
            bottomRight,
            bottomLeft,
            rotationHandle,
            center,
            topMidpoint);
        return true;
    }

    private void AppendClosedShotCameraWireframeSegments(
        List<ShotCameraWireframeSegment> segments,
        IReadOnlyList<Vector3> points,
        ShotCameraWireframeSegmentKind kind)
    {
        if (points.Count < 2) return;
        for (var index = 0; index < points.Count; index++)
        {
            AppendShotCameraWireframeSegment(
                segments,
                points[index],
                points[(index + 1) % points.Count],
                kind);
        }
    }

    private void AppendShotCameraWireframeConnections(
        List<ShotCameraWireframeSegment> segments,
        IReadOnlyList<Vector3> first,
        IReadOnlyList<Vector3> second,
        ShotCameraWireframeSegmentKind kind)
    {
        if (first.Count == 0 || second.Count == 0) return;
        var count = Math.Min(first.Count, second.Count);
        for (var index = 0; index < count; index++)
        {
            AppendShotCameraWireframeSegment(segments, first[index], second[index], kind);
        }
    }

    private void AppendShotCameraWireframeSegment(
        List<ShotCameraWireframeSegment> segments,
        Vector3 start,
        Vector3 end,
        ShotCameraWireframeSegmentKind kind)
    {
        if (!TryProjectShotCameraWireframeSegment(start, end, out var projectedStart, out var projectedEnd)
            || ShotFramingDistance(projectedStart, projectedEnd) < 0.35f)
        {
            return;
        }

        segments.Add(new ShotCameraWireframeSegment(projectedStart, projectedEnd, kind));
    }

    private bool TryProjectShotCameraWireframePoint(Vector3 point, out PointF screen)
    {
        screen = PointF.Empty;
        if (!Finite(point)) return false;
        if (!RendersReferenceProjection)
        {
            screen = WorldToScreen(point.X, point.Y);
            return IsSanePoint(screen);
        }

        return TryProjectScenePosition(point, out screen, out _)
            && IsSanePoint(screen);
    }

    /// <summary>
    /// Projects a scene-space point of the framed camera plane to Stage screen coordinates. A
    /// non-reference (2D) Stage maps X/Y directly, which is the correct flattening for a camera whose
    /// planar rotation is pinned.
    /// </summary>
    private bool TryProjectShotPointToScreen(Vector3 point, out PointF screen)
    {
        screen = PointF.Empty;
        if (!Finite(point)) return false;
        if (!RendersReferenceProjection)
        {
            screen = WorldToScreen(point.X, point.Y);
            return IsSanePoint(screen);
        }

        return TryProjectScenePosition(point, out screen, out _) && IsSanePoint(screen);
    }

    private bool TryProjectShotCameraWireframeSegment(
        Vector3 start,
        Vector3 end,
        out PointF projectedStart,
        out PointF projectedEnd)
    {
        projectedStart = PointF.Empty;
        projectedEnd = PointF.Empty;
        if (!Finite(start) || !Finite(end)) return false;
        if (!RendersReferenceProjection)
        {
            projectedStart = WorldToScreen(start.X, start.Y);
            projectedEnd = WorldToScreen(end.X, end.Y);
            return IsSanePoint(projectedStart)
                && IsSanePoint(projectedEnd)
                && LineMayTouchViewport(projectedStart, projectedEnd);
        }

        var cameraStart = CameraSpacePoint(new Point3(start.X, -start.Y, start.Z));
        var cameraEnd = CameraSpacePoint(new Point3(end.X, -end.Y, end.Z));
        if (!ClipNear(ref cameraStart, ref cameraEnd)) return false;
        projectedStart = ProjectCameraPoint(cameraStart);
        projectedEnd = ProjectCameraPoint(cameraEnd);
        return IsSanePoint(projectedStart)
            && IsSanePoint(projectedEnd)
            && LineMayTouchViewport(projectedStart, projectedEnd);
    }

    private static Vector3[] BuildShotCameraRectangle(
        Vector3 center,
        Vector3 right,
        Vector3 screenUp,
        float halfWidth,
        float halfHeight) =>
    [
        center - right * halfWidth + screenUp * halfHeight,
        center + right * halfWidth + screenUp * halfHeight,
        center + right * halfWidth - screenUp * halfHeight,
        center - right * halfWidth - screenUp * halfHeight
    ];

    private static Vector3 NormalizeShotVector(Vector3 value, Vector3 fallback)
    {
        var lengthSquared = value.LengthSquared();
        return float.IsFinite(lengthSquared) && lengthSquared > 0.00000001f
            ? value / MathF.Sqrt(lengthSquared)
            : fallback;
    }

    /// <summary>Screen hit test. Only handles and the border band react, so the frame interior stays editable.</summary>
    internal ShotFramingHandleHit HitTestShotFramingGizmo(Point screen)
    {
        if (ShotFrameCollectionVisible && !HasSelectedShotFrame) return ShotFramingHandleHit.None;
        if (!_shotFramingVisible) return ShotFramingHandleHit.None;
        if (!TryGetShotFramingGizmoGeometry(out var geometry)) return ShotFramingHandleHit.None;
        var hitRadius = ShotFramingHandleHitRadiusPixels * SpatialGizmoDpiScale;

        // The yellow frame's own corner and edge handles win outright: they are small, unambiguous
        // targets that the user aims at directly, and the camera wireframe is drawn on top of the same
        // area. Without this the frame's edge handles became unreachable whenever the camera overlapped
        // them.
        var handles = EnumerateShotFramingHandles(geometry);
        var frameBest = ShotFramingHandleKind.None;
        var frameBestDistance = float.MaxValue;
        foreach (var (kind, point) in handles)
        {
            if (kind == ShotFramingHandleKind.Body) continue;
            var distance = ShotFramingDistance(point, screen);
            if (distance > hitRadius || distance >= frameBestDistance) continue;
            frameBest = kind;
            frameBestDistance = distance;
        }

        if (frameBest != ShotFramingHandleKind.None)
        {
            return new ShotFramingHandleHit(frameBest);
        }

        // The camera object owns the next priority. This prevents the frame rectangle from swallowing
        // the body, axis, and lens controls when the 2D projection collapses depth.
        if (TryGetShotCameraWireframeGeometry(out var wireframe))
        {
            var cameraHandle = FindNearestShotCameraHandle(wireframe, screen, hitRadius);
            if (cameraHandle != ShotFramingHandleKind.None)
            {
                return new ShotFramingHandleHit(cameraHandle);
            }

            // The camera body is a volume, not only the four outline strokes. Accepting the
            // projected front/back faces makes the body drag target usable at low zoom and when
            // the body edges overlap the shot frame.
            if (IsPointInsideProjectedCameraBody(wireframe, screen))
            {
                return new ShotFramingHandleHit(ShotFramingHandleKind.CameraBody);
            }

            var bodyBand = Math.Max(ShotFramingBorderBandPixels, hitRadius * 0.75f);
            foreach (var segment in wireframe.Segments)
            {
                if (segment.Kind != ShotCameraWireframeSegmentKind.Body) continue;
                if (ShotFramingDistanceToSegment(segment.Start, segment.End, screen) <= bodyBand)
                {
                    return new ShotFramingHandleHit(ShotFramingHandleKind.CameraBody);
                }
            }
        }

        // Nothing else claimed the point but it is inside the yellow rectangle, so the operator is
        // grabbing the frame itself. This is what makes the frame body draggable away from the camera.
        if (IsPointInsidePolygon(geometry.Corners, new PointF(screen.X, screen.Y)))
        {
            return new ShotFramingHandleHit(ShotFramingHandleKind.Body);
        }

        var best = ShotFramingHandleKind.None;
        var bestDistance = float.MaxValue;
        foreach (var (kind, point) in handles)
        {
            var distance = ShotFramingDistance(point, screen);
            if (distance > hitRadius || distance >= bestDistance) continue;
            best = kind;
            bestDistance = distance;
        }

        if (best != ShotFramingHandleKind.None) return new ShotFramingHandleHit(best);

        var band = ShotFramingBorderBandPixels * SpatialGizmoDpiScale;
        return IsNearShotFramingBorder(geometry, screen, band)
            ? new ShotFramingHandleHit(ShotFramingHandleKind.Body)
            : ShotFramingHandleHit.None;
    }

    internal static ShotFramingHandleKind FindNearestShotCameraHandle(
        ShotCameraWireframeGeometry geometry,
        Point screen,
        float hitRadius)
    {
        var best = ShotFramingHandleKind.None;
        var bestDistance = float.MaxValue;
        var bestIsBody = false;
        var bestIsLensSlider = false;
        foreach (var handle in geometry.Handles)
        {
            var distance = ShotFramingDistance(handle.Point, screen);
            if (distance > hitRadius) continue;

            var isBody = handle.Kind == ShotFramingHandleKind.CameraBody;
            // The body and the optical slider are the two direct-manipulation controls, and in a
            // flattened projection the rotation rings can land on either of them. Give them priority
            // over the rings and over each other's ties, so neither drag is stolen.
            var isLensSlider = handle.Kind == ShotFramingHandleKind.CameraLens;
            if (bestIsBody && !isBody) continue;
            if (bestIsLensSlider && !isLensSlider) continue;
            if (!bestIsBody && isBody)
            {
                best = handle.Kind;
                bestDistance = distance;
                bestIsBody = true;
                continue;
            }

            if (!bestIsLensSlider && isLensSlider)
            {
                best = handle.Kind;
                bestDistance = distance;
                bestIsLensSlider = true;
                continue;
            }

            if (distance >= bestDistance) continue;
            best = handle.Kind;
            bestDistance = distance;
        }

        // A visible grip is an explicit target. A ring crossing its hit area must not steal it.
        if (best != ShotFramingHandleKind.None) return best;

        var ringHitRadius = Math.Max(
            6f,
            hitRadius * ShotCameraRotationRingHitRadiusPixels
                / Math.Max(1f, ShotFramingHandleHitRadiusPixels));
        foreach (var segment in geometry.Segments)
        {
            var rotationHandle = ShotCameraRotationHandleForSegment(segment.Kind);
            if (rotationHandle == ShotFramingHandleKind.None) continue;
            var distance = ShotFramingDistanceToSegment(segment.Start, segment.End, screen);
            // The rotation rings are drawn around the body centre, so they pass straight through the
            // body handle and can cross the optical slider. Without this guard a ring grabs the drag
            // whenever the operator clicks one of those controls, making them unmovable.
            if (bestIsBody || bestIsLensSlider) continue;
            if (distance > ringHitRadius || distance >= bestDistance) continue;
            best = rotationHandle;
            bestDistance = distance;
        }

        if (best != ShotFramingHandleKind.None) return best;
        if (!geometry.HasCameraPoint) return ShotFramingHandleKind.None;

        // The visible axis shafts are part of the control, not decoration. Keep endpoint hits
        // higher priority, then accept the shaft itself so a long 3D axis does not require a
        // pixel-perfect click on its arrow tip.
        var shaftHitRadius = Math.Max(
            6f,
            hitRadius * ShotCameraHandleShaftHitRadiusPixels
                / Math.Max(1f, ShotFramingHandleHitRadiusPixels));
        var screenPoint = new PointF(screen.X, screen.Y);
        foreach (var handle in geometry.Handles)
        {
            if (!IsShotCameraPositionHandle(handle.Kind)
                && !IsShotCameraRotationHandle(handle.Kind)) continue;

            // Match the rendered segments: position shafts run to the true axis endpoint, then
            // follow a leader to the grip. Rotation grips have only that leader, never a spoke.
            var distance = IsShotCameraPositionHandle(handle.Kind)
                ? ShotFramingDistanceToSegment(geometry.CameraPoint, handle.Anchor, screenPoint)
                : float.MaxValue;
            if (ShotFramingDistance(handle.Anchor, handle.Point) > ShotCameraHandleLeaderThresholdPixels)
            {
                distance = Math.Min(distance,
                    ShotFramingDistanceToSegment(handle.Anchor, handle.Point, screenPoint));
            }
            if (distance > shaftHitRadius || distance >= bestDistance) continue;
            best = handle.Kind;
            bestDistance = distance;
        }

        return best;
    }

    private static bool IsPointInsideProjectedCameraBody(
        ShotCameraWireframeGeometry geometry,
        Point screen)
    {
        var point = new PointF(screen.X, screen.Y);
        return geometry.BodyFrontCorners.Length >= 3
            && IsPointInsidePolygon(geometry.BodyFrontCorners, point)
            || geometry.BodyBackCorners.Length >= 3
            && IsPointInsidePolygon(geometry.BodyBackCorners, point);
    }

    internal static ShotFramingHandleKind ShotCameraRotationHandleForSegment(
        ShotCameraWireframeSegmentKind kind) => kind switch
        {
            ShotCameraWireframeSegmentKind.RotationRingX => ShotFramingHandleKind.CameraRotateX,
            ShotCameraWireframeSegmentKind.RotationRingY => ShotFramingHandleKind.CameraRotateY,
            ShotCameraWireframeSegmentKind.RotationRingZ => ShotFramingHandleKind.CameraRotateZ,
            _ => ShotFramingHandleKind.None
        };

    internal static IEnumerable<(ShotFramingHandleKind Kind, PointF Point)> EnumerateShotFramingHandles(
        ShotFramingGizmoGeometry geometry)
    {
        yield return (ShotFramingHandleKind.TopLeft, geometry.TopLeft);
        yield return (ShotFramingHandleKind.Top, geometry.TopMidpoint);
        yield return (ShotFramingHandleKind.TopRight, geometry.TopRight);
        yield return (ShotFramingHandleKind.Right, geometry.RightMidpoint);
        yield return (ShotFramingHandleKind.BottomRight, geometry.BottomRight);
        yield return (ShotFramingHandleKind.Bottom, geometry.BottomMidpoint);
        yield return (ShotFramingHandleKind.BottomLeft, geometry.BottomLeft);
        yield return (ShotFramingHandleKind.Left, geometry.LeftMidpoint);
        yield return (ShotFramingHandleKind.Rotate, geometry.RotationHandle);
    }

    /// <summary>Corner points of the framed viewport in scene units, in TL/TR/BR/BL order.</summary>
    internal static (Vector2[] Corners, Vector2 Center) ResolveShotFramingLocalCorners(
        SceneShotSettings settings,
        float canvasWidth,
        float canvasHeight)
    {
        var normalized = settings.Normalized();
        var (lowerLeft, upperRight, _, _) = normalized.ResolveFrame(canvasWidth, canvasHeight);
        var center = new Vector2(normalized.X, normalized.Y);
        var radians = normalized.RotationDegrees * MathF.PI / 180f;
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);
        Vector2 Rotate(Vector2 point)
        {
            var offset = point - center;
            return new Vector2(
                center.X + offset.X * cos - offset.Y * sin,
                center.Y + offset.X * sin + offset.Y * cos);
        }

        return (
            [
                Rotate(new Vector2(lowerLeft.X, upperRight.Y)),
                Rotate(new Vector2(upperRight.X, upperRight.Y)),
                Rotate(new Vector2(upperRight.X, lowerLeft.Y)),
                Rotate(new Vector2(lowerLeft.X, lowerLeft.Y))
            ],
            center);
    }

    /// <summary>
    /// Applies one pointer delta to the framing. Move follows the pointer, resize keeps the opposite
    /// edge anchored and derives the zoom from the dragged extent, rotate turns around the frame centre.
    /// </summary>
    internal static SceneShotSettings ResolveShotFramingDrag(
        SceneShotSettings start,
        ShotFramingHandleKind handle,
        Vector2 pointerWorld,
        Vector2 pointerStartWorld,
        float canvasWidth,
        float canvasHeight)
    {
        start = start.Normalized();
        if (handle == ShotFramingHandleKind.None) return start;
        var framing = start.ToPreviewFraming();
        var center = new Vector2(framing.X, framing.Y);

        switch (handle)
        {
            case ShotFramingHandleKind.Body:
                return WithPreviewFraming(
                    start,
                    framing.X + (pointerWorld.X - pointerStartWorld.X),
                    framing.Y + (pointerWorld.Y - pointerStartWorld.Y),
                    framing.Zoom,
                    framing.RotationDegrees);
            case ShotFramingHandleKind.Rotate:
            {
                var startAngle = MathF.Atan2(pointerStartWorld.Y - center.Y, pointerStartWorld.X - center.X);
                var currentAngle = MathF.Atan2(pointerWorld.Y - center.Y, pointerWorld.X - center.X);
                var deltaDegrees = NormalizeShotAngleDeltaDegrees(
                    (currentAngle - startAngle) * 180f / MathF.PI);
                return WithPreviewFraming(
                    start,
                    framing.X,
                    framing.Y,
                    framing.Zoom,
                    framing.RotationDegrees + deltaDegrees);
            }
        }

        var (directionX, directionY) = handle switch
        {
            ShotFramingHandleKind.TopLeft => (-1, -1),
            ShotFramingHandleKind.Top => (0, -1),
            ShotFramingHandleKind.TopRight => (1, -1),
            ShotFramingHandleKind.Right => (1, 0),
            ShotFramingHandleKind.BottomRight => (1, 1),
            ShotFramingHandleKind.Bottom => (0, 1),
            ShotFramingHandleKind.BottomLeft => (-1, 1),
            ShotFramingHandleKind.Left => (-1, 0),
            _ => (0, 0)
        };
        if (directionX == 0 && directionY == 0) return start;

        var radians = framing.RotationDegrees * MathF.PI / 180f;
        var axisX = new Vector2(MathF.Cos(radians), MathF.Sin(radians));
        var axisY = new Vector2(-MathF.Sin(radians), MathF.Cos(radians));
        var (_, _, halfWidth, halfHeight) = framing.ResolveFrame(canvasWidth, canvasHeight);
        var anchor = center + axisX * (-directionX * halfWidth) + axisY * (-directionY * halfHeight);

        var delta = pointerWorld - anchor;
        var extentX = MathF.Abs(Vector2.Dot(delta, axisX));
        var extentY = MathF.Abs(Vector2.Dot(delta, axisY));

        var zoom = framing.Zoom;
        if (directionX != 0 && extentX > 1f)
        {
            zoom = Math.Max(1f, canvasHeight) * framing.ResolveAspectRatio(canvasWidth, canvasHeight) / extentX;
        }
        else if (directionY != 0 && extentY > 1f)
        {
            zoom = canvasHeight / extentY;
        }

        zoom = Math.Clamp(
            float.IsFinite(zoom) ? zoom : framing.Zoom,
            SceneShotSettings.MinimumZoom,
            Math.Max(SceneShotSettings.MinimumZoom,
                Math.Min(SceneShotSettings.MaximumZoom,
                    Math.Max(1f, canvasHeight) / (2f * SceneShotSettings.MinimumFrameHalfHeight(
                        framing.ResolveAspectRatio(canvasWidth, canvasHeight))))));
        var (_, _, newHalfWidth, newHalfHeight) = (framing with { X = 0f, Y = 0f, Zoom = zoom, RotationDegrees = 0f })
            .ResolveFrame(canvasWidth, canvasHeight);
        var newCenter = anchor + axisX * (directionX * newHalfWidth) + axisY * (directionY * newHalfHeight);
        return WithPreviewFraming(start, newCenter.X, newCenter.Y, zoom, framing.RotationDegrees);
    }

    /// <summary>
    /// Resolves a drag of the yellow shot framing frame in the same screen space the frame is drawn in.
    /// The frame is drawn by projecting the camera's world-space framed plane, so solving the drag in
    /// the flat canvas model (or through the transform overlay ray) would use a different projection
    /// than the one the user grabbed, and the handles would slide away from the pointer.
    /// The framed centre is kept as the transform focus: a drag moves or grows the frame about its own
    /// centre instead of letting the anchor run away from the pointer.
    /// </summary>
    internal SceneShotSettings ResolveShotFramingDragFromScreen(
        SceneShotSettings start,
        ShotFramingHandleKind handle,
        Point screen,
        Point startScreen,
        PointF startHandleScreen,
        PointF startCenterScreen)
    {
        start = start.Normalized();
        if (handle == ShotFramingHandleKind.None) return start;
        _ = startScreen;

        var pointer = new Vector2(screen.X, screen.Y);
        switch (handle)
        {
            case ShotFramingHandleKind.Body:
            {
                // The frame centre must land exactly under the pointer, so the centre is offset by the
                // pointer's own screen travel.
                var travel = new Vector2(screen.X - startScreen.X, screen.Y - startScreen.Y);
                var movedCenter = new Vector2(
                    startCenterScreen.X + travel.X,
                    startCenterScreen.Y + travel.Y);
                return TryResolveShotFramingFromScreenCenter(start, movedCenter, start.Zoom)
                    ?? start;
            }
            case ShotFramingHandleKind.Rotate:
            {
                var startAngle = MathF.Atan2(
                    startScreen.Y - startCenterScreen.Y,
                    startScreen.X - startCenterScreen.X);
                var currentAngle = MathF.Atan2(
                    pointer.Y - startCenterScreen.Y,
                    pointer.X - startCenterScreen.X);
                var delta = NormalizeShotAngleDeltaDegrees(
                    (currentAngle - startAngle) * 180f / MathF.PI);
                return WithPreviewFraming(
                    start,
                    start.X,
                    start.Y,
                    start.Zoom,
                    start.RotationDegrees + delta);
            }
        }

        var (directionX, directionY) = handle switch
        {
            ShotFramingHandleKind.TopLeft => (-1, -1),
            ShotFramingHandleKind.Top => (0, -1),
            ShotFramingHandleKind.TopRight => (1, -1),
            ShotFramingHandleKind.Right => (1, 0),
            ShotFramingHandleKind.BottomRight => (1, 1),
            ShotFramingHandleKind.Bottom => (0, 1),
            ShotFramingHandleKind.BottomLeft => (-1, 1),
            ShotFramingHandleKind.Left => (-1, 0),
            _ => (0, 0)
        };
        if (directionX == 0 && directionY == 0) return start;

        var center = new Vector2(startCenterScreen.X, startCenterScreen.Y);

        // The press-time handle radius is the scale reference. Re-reading it from the live frame would
        // let the solve consume its own result: after the frame resized once, the live radius already
        // matched the pointer and every later event became a no-op.
        // The press-time handle radius is the scale reference. Re-reading it from the live frame would
        // let the solve consume its own result: after the frame resized once, the live radius already
        // matched the pointer and every later event became a no-op.
        var radiusNow = new Vector2(
            startHandleScreen.X - center.X,
            startHandleScreen.Y - center.Y);
        var radiusAt = pointer - center;

        // Resize about the framed centre - the camera's own focus - and make the dragged edge satisfy
        // the pointer. Solving this from the full anchor span made the edge crawl, because the frame is
        // laid out in canvas units but drawn at the projected screen scale.
        var scale = 1f;
        if (directionX != 0 && MathF.Abs(radiusNow.X) > 1f)
        {
            scale = radiusAt.X / radiusNow.X;
        }
        else if (directionY != 0 && MathF.Abs(radiusNow.Y) > 1f)
        {
            scale = radiusAt.Y / radiusNow.Y;
        }

        if (!float.IsFinite(scale) || scale <= 0.0001f) return start;
        var zoom = Math.Clamp(
            start.Zoom / scale,
            SceneShotSettings.MinimumZoom,
            SceneShotSettings.MaximumZoom);
        // The framed centre is the transform focus, so it stays exactly where it is. Only the edge the
        // operator grabbed moves, which is what makes the handle track the pointer.
        return TryResolveShotFramingFromScreenCenter(start, center, zoom) ?? start;
    }

    /// <summary>
    /// Session state for a yellow-frame drag, captured at press so the solve can work in the frame's
    /// own screen space.
    /// </summary>
    internal readonly record struct ShotFramingScreenDragStart(
        Point Screen,
        PointF HandleScreen,
        PointF CenterScreen);

    /// <summary>
    /// Finds the shot settings whose framed centre projects to <paramref name="centerScreen"/> at the
    /// given zoom. The frame is drawn by projecting the camera's world-space plane, so the settings are
    /// corrected by the screen residual at the frame centre until the projection agrees. This keeps the
    /// solve in the same space as the drawing instead of mixing in a second projection.
    /// </summary>
    private SceneShotSettings? TryResolveShotFramingFromScreenCenter(
        SceneShotSettings start,
        Vector2 centerScreen,
        float zoom)
    {
        var scene = Scene;
        if (scene is null) return null;

        var candidate = WithPreviewFraming(start, start.X, start.Y, zoom, start.RotationDegrees);
        for (var iteration = 0; iteration < 8; iteration++)
        {
            if (!TryResolveShotFramingGeometry(candidate, out var geometry)) return null;
            var dx = geometry.Center.X - centerScreen.X;
            var dy = geometry.Center.Y - centerScreen.Y;
            if (MathF.Abs(dx) < 0.05f && MathF.Abs(dy) < 0.05f) return candidate;

            // Convert the screen residual into a settings delta using the frame's own screen footprint,
            // so the correction does not depend on which projection happens to be active.
            var (localCorners, _) = ResolveShotFramingLocalCorners(
                candidate,
                scene.StageWidth,
                scene.StageHeight);
            if (localCorners.Length < 4) return null;
            var frameCenterSettings = new Vector2(candidate.X, candidate.Y);
            if (!TryProjectShotPointToScreen(
                    new Vector3(frameCenterSettings.X, frameCenterSettings.Y, 0f),
                    out var projectedCenter))
            {
                return null;
            }

            // Perturb the settings centre by one unit to learn the local scale, then apply the residual.
            var probe = WithPreviewFraming(
                candidate,
                candidate.X + 1f,
                candidate.Y,
                candidate.Zoom,
                candidate.RotationDegrees);
            if (!TryResolveShotFramingGeometry(probe, out var probeGeometry)) return null;
            var perUnitX = probeGeometry.Center.X - projectedCenter.X;
            var perUnitY = probeGeometry.Center.Y - projectedCenter.Y;
            var probeY = WithPreviewFraming(
                candidate,
                candidate.X,
                candidate.Y + 1f,
                candidate.Zoom,
                candidate.RotationDegrees);
            if (!TryResolveShotFramingGeometry(probeY, out var probeYGeometry)) return null;
            var perUnitXy = probeYGeometry.Center.X - projectedCenter.X;
            var perUnitYy = probeYGeometry.Center.Y - projectedCenter.Y;
            var determinant = perUnitX * perUnitYy - perUnitXy * perUnitY;
            if (!float.IsFinite(determinant) || MathF.Abs(determinant) < 1e-6f)
            {
                // The projection is degenerate; fall back to an axis-aligned correction.
                candidate = WithPreviewFraming(
                    candidate,
                    candidate.X - dx,
                    candidate.Y + dy,
                    candidate.Zoom,
                    candidate.RotationDegrees);
                continue;
            }

            var worldDx = (-dx * perUnitYy + perUnitXy * dy) / determinant;
            var worldDy = (-perUnitX * dy + perUnitY * dx) / determinant;
            if (!float.IsFinite(worldDx) || !float.IsFinite(worldDy)) return null;
            candidate = WithPreviewFraming(
                candidate,
                candidate.X + worldDx,
                candidate.Y + worldDy,
                candidate.Zoom,
                candidate.RotationDegrees);
        }

        return candidate;
    }


    /// slider's own screen axis and converted to a normalized travel, which is then mapped back through
    /// the same logarithmic scale the slider is drawn with. The handle therefore lands exactly where
    /// the pointer is, on every view and at every zoom, without the non-linear radius matching the old
    /// world-space diamond needed.
    /// </summary>
    internal bool TryResolveShotCameraLensSliderDrag(
        PointF screen,
        SceneShotSettings start,
        out SceneShotSettings settings)
    {
        settings = start;
        var scene = Scene;
        if (scene is null || Width <= 0 || Height <= 0) return false;
        var normalized = start.Normalized();
        var world = ResolveShotCameraWireframeWorld(
            normalized,
            scene.StageWidth,
            scene.StageHeight);
        if (!TryResolveShotCameraLensSlider(
                normalized,
                world,
                out var originScreen,
                out var direction,
                out var travel))
        {
            return false;
        }

        if (!float.IsFinite(travel) || travel <= 1f) return false;

        // Invert the exact mapping the knob is drawn with: the pointer's distance from the body along
        // the slider axis is converted back into a normalized lens value. Sharing the mapping is what
        // guarantees the knob lands under the cursor instead of lagging by a fixed stand-off.
        var offset = Math.Min(
            ShotCameraHandleMinimumOffsetPixels * SpatialGizmoDpiScale,
            travel * 0.5f);
        var span = travel - offset;
        if (!float.IsFinite(span) || span <= 1f) return false;

        var pointer = new Vector2(screen.X - originScreen.X, screen.Y - originScreen.Y);
        var pointerTravel = Vector2.Dot(pointer, direction);
        var fraction = (pointerTravel - offset) / span;
        var value = ResolveShotCameraLensFromSliderFraction(normalized, fraction);
        if (!float.IsFinite(value)) return false;

        if (normalized.Projection == CameraProjection.Orthographic)
        {
            settings = normalized with
            {
                OrthographicSize = value,
                Zoom = 28_000f / Math.Max(SceneShotSettings.MinimumOrthographicSize, value)
            };
            return true;
        }

        settings = normalized with
        {
            FocalLength = value,
            Zoom = value / 50f
        };
        return true;
    }

    /// <summary>
    /// Blends a camera edit back towards its start state for precise dragging. Position moves and the
    /// lens are scaled about the start value, and rotations are scaled about zero, so holding Shift
    /// mid-drag immediately reduces the applied amount without recomputing the drag history.
    /// </summary>
    internal static SceneShotSettings ScaleShotFramingDragFromStart(
        SceneShotSettings start,
        SceneShotSettings current,
        float scale)
    {
        start = start.Normalized();
        current = current.Normalized();
        if (!float.IsFinite(scale)) return current;
        scale = Math.Clamp(scale, 0.01f, 1f);
        if (scale >= 1f) return current;

        var position = start.Position + (current.Position - start.Position) * scale;
        var rotationX = start.RotationX + NormalizeShotAngleDelta(current.RotationX - start.RotationX) * scale;
        var rotationY = start.RotationY + NormalizeShotAngleDelta(current.RotationY - start.RotationY) * scale;
        var rotationDegrees = start.RotationDegrees
            + NormalizeShotAngleDelta(current.RotationDegrees - start.RotationDegrees) * scale;
        // Focal length and orthographic size are perceived multiplicatively, so they are scaled in log
        // space; a linear blend would make fine adjustment feel inconsistent across the range.
        var focal = BlendShotLogValue(start.FocalLength, current.FocalLength, scale);
        var orthographic = BlendShotLogValue(start.OrthographicSize, current.OrthographicSize, scale);
        return (current with
        {
            X = position.X,
            Y = position.Y,
            Z = position.Z,
            RotationX = rotationX,
            RotationY = rotationY,
            RotationDegrees = rotationDegrees,
            FocalLength = focal,
            OrthographicSize = orthographic,
            Zoom = current.Projection == CameraProjection.Orthographic
                ? 28_000f / Math.Max(SceneShotSettings.MinimumOrthographicSize, orthographic)
                : focal / 50f
        }).Normalized();
    }

    private static float BlendShotLogValue(float start, float current, float scale)
    {
        if (start <= 0f || current <= 0f || !float.IsFinite(start) || !float.IsFinite(current))
        {
            return current;
        }

        var blended = MathF.Exp(MathF.Log(start) + (MathF.Log(current) - MathF.Log(start)) * scale);
        return float.IsFinite(blended) ? blended : current;
    }

    private static float NormalizeShotAngleDelta(float delta) =>
        float.IsFinite(delta) ? delta : 0f;

    /// <summary>
    /// Integrates precision per sample, without rescaling the distance already travelled.
    /// </summary>
    internal static SceneShotSettings AccumulateShotFramingDrag(
        SceneShotSettings previousRaw, SceneShotSettings raw, SceneShotSettings effective, float scale)
    {
        if (raw == previousRaw) return effective;
        scale = float.IsFinite(scale) ? Math.Clamp(scale, 0.01f, 1f) : 1f;
        float Step(float before, float now, float applied) =>
            before == now ? applied : applied + (now - before) * scale;
        float OpticalStep(float before, float now, float applied) => before == now
            ? applied
            : applied * MathF.Pow(now / before, scale);
        var focal = OpticalStep(previousRaw.FocalLength, raw.FocalLength, effective.FocalLength);
        var size = OpticalStep(previousRaw.OrthographicSize, raw.OrthographicSize, effective.OrthographicSize);
        return (raw with
        {
            X = Step(previousRaw.X, raw.X, effective.X),
            Y = Step(previousRaw.Y, raw.Y, effective.Y),
            Z = Step(previousRaw.Z, raw.Z, effective.Z),
            RotationX = Step(previousRaw.RotationX, raw.RotationX, effective.RotationX),
            RotationY = Step(previousRaw.RotationY, raw.RotationY, effective.RotationY),
            RotationDegrees = Step(previousRaw.RotationDegrees, raw.RotationDegrees, effective.RotationDegrees),
            FocalLength = focal,
            OrthographicSize = size,
            Zoom = raw.Projection == CameraProjection.Orthographic
                ? (previousRaw.OrthographicSize == raw.OrthographicSize ? effective.Zoom
                    : 28_000f / Math.Max(SceneShotSettings.MinimumOrthographicSize, size))
                : (previousRaw.FocalLength == raw.FocalLength ? effective.Zoom : focal / 50f)
        }).Normalized();
    }

    /// <summary>Snap only channels owned by the active handle, retaining unrelated fractional values.</summary>
    internal static SceneShotSettings SnapShotFramingSettings(
        SceneShotSettings start, SceneShotSettings settings, ShotFramingHandleKind handle)
    {
        float Snap(float before, float value) => before == value ? value : MathF.Round(value);
        return (handle switch
        {
            ShotFramingHandleKind.CameraBody => settings with
            {
                X = Snap(start.X, settings.X), Y = Snap(start.Y, settings.Y), Z = Snap(start.Z, settings.Z)
            },
            ShotFramingHandleKind.CameraPositionX => settings with { X = Snap(start.X, settings.X) },
            ShotFramingHandleKind.CameraPositionY => settings with { Y = Snap(start.Y, settings.Y) },
            ShotFramingHandleKind.CameraPositionZ => settings with { Z = Snap(start.Z, settings.Z) },
            ShotFramingHandleKind.CameraRotateX => settings with { RotationX = Snap(start.RotationX, settings.RotationX) },
            ShotFramingHandleKind.CameraRotateY => settings with { RotationY = Snap(start.RotationY, settings.RotationY) },
            ShotFramingHandleKind.CameraRotateZ => settings with { RotationDegrees = Snap(start.RotationDegrees, settings.RotationDegrees) },
            ShotFramingHandleKind.CameraLens when settings.Projection == CameraProjection.Orthographic
                => settings with
                {
                    OrthographicSize = start.OrthographicSize == settings.OrthographicSize
                        ? settings.OrthographicSize : MathF.Round(settings.OrthographicSize / 100f) * 100f,
                    Zoom = 28_000f / Math.Max(SceneShotSettings.MinimumOrthographicSize,
                        start.OrthographicSize == settings.OrthographicSize
                            ? settings.OrthographicSize : MathF.Round(settings.OrthographicSize / 100f) * 100f)
                },
            ShotFramingHandleKind.CameraLens => settings with
            {
                FocalLength = Snap(start.FocalLength, settings.FocalLength),
                Zoom = Snap(start.FocalLength, settings.FocalLength) / 50f
            },
            _ => settings
        }).Normalized();
    }

    /// <summary>
    /// Human-readable summary of the camera values being edited, shown while a handle is dragged so
    /// the operator can stop on an exact number.
    /// </summary>
    internal static string DescribeShotCameraSettings(
        SceneShotSettings settings,
        ShotFramingHandleKind handle)
    {
        settings = settings.Normalized();
        return handle switch
        {
            ShotFramingHandleKind.CameraPositionX => $"X {settings.X:0.##}",
            ShotFramingHandleKind.CameraPositionY => $"Y {settings.Y:0.##}",
            ShotFramingHandleKind.CameraPositionZ => $"Z {settings.Z:0.##}",
            ShotFramingHandleKind.CameraRotateX => $"Rot X {settings.RotationX:0.##}°",
            ShotFramingHandleKind.CameraRotateY => $"Rot Y {settings.RotationY:0.##}°",
            ShotFramingHandleKind.CameraRotateZ => $"Rot Z {settings.RotationDegrees:0.##}°",
            ShotFramingHandleKind.CameraLens => settings.Projection == CameraProjection.Orthographic
                ? $"Size {settings.OrthographicSize:0.#}"
                : $"Lens {settings.FocalLength:0.#} mm",
            _ => string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                "X {0:0.##}  Y {1:0.##}  Z {2:0.##}  Rot {3:0.##}°",
                settings.X,
                settings.Y,
                settings.Z,
                settings.RotationDegrees)
        };
    }

    /// <summary>
    /// Applies a body drag so the grabbable camera body lands under the pointer. The reference
    /// projection is perspective and its depth term couples the screen position to the camera's own
    /// motion, so a linear world-per-pixel scale cannot track exactly. Instead the pointer is cast
    /// against the plane through the camera facing the viewer and the camera is moved to that point;
    /// the caller then re-projects and the body follows the cursor.
    /// </summary>
    internal bool TryResolveShotCameraBodyDragPosition(
        Point screen,
        Vector3 startCameraPoint,
        out Vector3 position)
    {
        position = startCameraPoint;
        if (!RendersReferenceProjection) return false;
        if (!TryGetReferenceRay(screen, out var ray)) return false;

        // A plane through the start point facing the viewer keeps the drag stable and avoids the
        // near-singular intersections a plane through the eye would produce.
        var normal = -ray.Direction;
        var denominator = Vector3.Dot(ray.Direction, normal);
        if (Math.Abs(denominator) < SpatialGizmoMinimumPlaneRayDot) return false;
        var distance = Vector3.Dot(startCameraPoint - ray.Origin, normal) / denominator;
        if (!float.IsFinite(distance) || distance < 0f) return false;

        var candidate = ray.Origin + ray.Direction * distance;
        if (!Finite(candidate)) return false;
        position = candidate;
        return true;
    }

    /// <summary>
    /// Converts a pointer delta in pixels into camera motion on the view plane, so the grabbable body
    /// stays exactly under the cursor. The caller supplies the world axes that map to screen right and
    /// screen up for the current projection; scene Y grows downward on the Stage, so a downward
    /// pointer move lowers the camera along screen up.
    /// </summary>
    internal static Vector3 ResolveShotCameraBodyDragDelta(
        Vector2 pointerDeltaPixels,
        float worldPerPixel,
        Vector3 screenRight,
        Vector3 screenUp)
    {
        if (!float.IsFinite(worldPerPixel) || worldPerPixel <= 0f)
        {
            return Vector3.Zero;
        }

        return screenRight * (pointerDeltaPixels.X * worldPerPixel)
            + screenUp * (pointerDeltaPixels.Y * worldPerPixel);
    }

    /// <summary>
    /// Applies a camera-object handle delta. Position and rotation deltas are already resolved in
    /// scene space by the pointer session; the optical delta is a screen-space scalar so the same
    /// handle remains usable in both the 2D composition and the projected 3D view.
    /// </summary>
    internal static SceneShotSettings ResolveShotCameraHandleDrag(
        SceneShotSettings start,
        ShotFramingHandleKind handle,
        Vector3 positionDelta,
        float rotationDeltaDegrees,
        float opticalDeltaPixels)
    {
        start = start.Normalized();
        if (!IsShotCameraHandle(handle)) return start;
        if (!Finite(positionDelta)
            || !float.IsFinite(rotationDeltaDegrees)
            || !float.IsFinite(opticalDeltaPixels))
        {
            return start;
        }

        var next = start;
        if (handle == ShotFramingHandleKind.CameraBody
            || IsShotCameraPositionHandle(handle))
        {
            next = next with
            {
                X = start.X + positionDelta.X,
                Y = start.Y + positionDelta.Y,
                Z = start.Z + positionDelta.Z
            };
        }
        else if (handle == ShotFramingHandleKind.CameraRotateX)
        {
            next = next with { RotationX = start.RotationX + rotationDeltaDegrees };
        }
        else if (handle == ShotFramingHandleKind.CameraRotateY)
        {
            next = next with { RotationY = start.RotationY + rotationDeltaDegrees };
        }
        else if (handle == ShotFramingHandleKind.CameraRotateZ)
        {
            next = next with { RotationDegrees = start.RotationDegrees + rotationDeltaDegrees };
        }
        else if (handle == ShotFramingHandleKind.CameraLens)
        {
            if (start.Projection == CameraProjection.Orthographic)
            {
                var size = Math.Clamp(
                    start.OrthographicSize - opticalDeltaPixels * 24f,
                    SceneShotSettings.MinimumOrthographicSize,
                    SceneShotSettings.MaximumOrthographicSize);
                next = next with
                {
                    OrthographicSize = size,
                    Zoom = 28_000f / Math.Max(SceneShotSettings.MinimumOrthographicSize, size)
                };
            }
            else
            {
                var focal = Math.Clamp(
                    start.FocalLength + opticalDeltaPixels * 0.75f,
                    SceneShotSettings.MinimumFocalLength,
                    SceneShotSettings.MaximumFocalLength);
                next = next with
                {
                    FocalLength = focal,
                    Zoom = focal / 50f
                };
            }
        }

        return next.Normalized();
    }

    internal static float NormalizeShotAngleDeltaDegrees(float degrees)
    {
        if (!float.IsFinite(degrees)) return 0f;
        var normalized = degrees % 360f;
        if (normalized > 180f) normalized -= 360f;
        else if (normalized < -180f) normalized += 360f;
        return normalized;
    }

    private static SceneShotSettings WithPreviewFraming(
        SceneShotSettings start,
        float x,
        float y,
        float zoom,
        float rotationDegrees)
    {
        var normalized = start.Normalized();
        var next = normalized with
        {
            X = x,
            Y = y,
            RotationDegrees = rotationDegrees,
            Zoom = zoom
        };
        if (normalized.Projection == CameraProjection.Orthographic)
        {
            next = next with
            {
                OrthographicSize = 28_000f / Math.Max(SceneShotSettings.MinimumZoom, zoom)
            };
        }
        else
        {
            next = next with
            {
                FocalLength = 50f * zoom
            };
        }

        return next.Normalized();
    }

    private static bool IsNearShotFramingBorder(
        ShotFramingGizmoGeometry geometry,
        Point screen,
        float band)
    {
        var corners = geometry.Corners;
        if (!IsPointInsidePolygon(corners, screen)) return false;
        for (var index = 0; index < corners.Length; index++)
        {
            var start = corners[index];
            var end = corners[(index + 1) % corners.Length];
            if (ShotFramingDistanceToSegment(start, end, screen) <= band) return true;
        }

        return false;
    }

    private static bool IsPointInsidePolygon(PointF[] polygon, PointF point)
    {
        var inside = false;
        for (int index = 0, previous = polygon.Length - 1; index < polygon.Length; previous = index++)
        {
            var current = polygon[index];
            var last = polygon[previous];
            if (current.Y > point.Y == last.Y > point.Y) continue;
            var x = (last.X - current.X) * (point.Y - current.Y) / (last.Y - current.Y) + current.X;
            if (point.X < x) inside = !inside;
        }

        return inside;
    }

    private static float ShotFramingDistanceToSegment(PointF start, PointF end, PointF point)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var lengthSquared = dx * dx + dy * dy;
        if (lengthSquared <= float.Epsilon) return ShotFramingDistance(start, point);
        var t = ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared;
        t = Math.Clamp(t, 0f, 1f);
        return ShotFramingDistance(new PointF(start.X + t * dx, start.Y + t * dy), point);
    }

    private static float ShotFramingDistance(PointF a, PointF b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>Canvas size the framed viewport is resolved against (the composition stage size).</summary>
    internal SizeF ShotFramingCanvasSize =>
        Scene is { } scene ? new SizeF(scene.StageWidth, scene.StageHeight) : SizeF.Empty;

    private static PointF ToPointF(Vector2 value) => new(value.X, value.Y);

    private static PointF Normalize(PointF value)
    {
        var length = MathF.Sqrt(value.X * value.X + value.Y * value.Y);
        return length <= float.Epsilon ? new PointF(0f, -1f) : new PointF(value.X / length, value.Y / length);
    }

    private static bool IsSanePoint(PointF point) =>
        float.IsFinite(point.X) && float.IsFinite(point.Y)
        && MathF.Abs(point.X) < 1_000_000f && MathF.Abs(point.Y) < 1_000_000f;
}
