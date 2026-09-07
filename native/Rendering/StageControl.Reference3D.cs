using System.Buffers;
using System.Diagnostics;
using System.Numerics;

namespace VectorAnimationEngine;

internal enum ReferenceViewDirection
{
    Front,
    Back,
    Left,
    Right,
    Top,
    Bottom,
    Isometric
}

internal enum SpatialTransformMode
{
    Move,
    Rotate,
    Scale
}

internal enum SpatialGizmoMotion
{
    Immediate,
    Animated
}

internal enum SpatialTransformAxis
{
    None,
    X,
    Y,
    Z,
    Uniform,
    XY,
    XZ,
    YZ
}

internal readonly record struct SpatialTransformHandleHit(
    SpatialTransformMode Mode,
    SpatialTransformAxis Axis)
{
    public static SpatialTransformHandleHit None { get; } = new(
        SpatialTransformMode.Move,
        SpatialTransformAxis.None);

    public bool IsValid => Axis != SpatialTransformAxis.None;
}

internal readonly record struct SpatialRay(Vector3 Origin, Vector3 Direction);

internal sealed record SceneCompositionMaskClip(
    VectorScene TargetScene,
    VectorScene MaskScene,
    int Frame,
    IReadOnlySet<int> TargetObjectIndices);

internal readonly record struct Reference3DProjectedContour(
    PointF[] Points,
    bool Closed,
    float AverageDepth,
    bool HasSourceStart = true,
    bool HasSourceEnd = true);

internal readonly record struct Reference3DSourceContour(PointF[] Points, bool Closed);

internal readonly record struct Reference3DLineEndpointConnection(
    int ObjectIndex,
    bool StartEndpoint,
    PointF Endpoint,
    PointF Interior);

internal enum Reference3DRenderKind : byte
{
    Back,
    Side,
    FrontFill,
    FrontStroke,
    Outline,
    IntersectionEdge
}

internal readonly record struct Reference3DSurfacePlane(
    Vector3 Normal,
    float Distance,
    bool IsValid);

internal readonly record struct Reference3DSurfacePlaneKey(
    long NormalX,
    long NormalY,
    long NormalZ,
    long PlaneDistance,
    bool IsValid);

internal readonly record struct Reference3DBaseFrameSceneState(
    VectorScene? Scene,
    long GeometryRevision,
    long SummaryRevision,
    int EditFrame,
    ulong LayerRenderState);

internal readonly record struct Reference3DBaseFrameRenderState(
    long PresentationRevision,
    int Frame,
    Reference3DBaseFrameSceneState Editable,
    Reference3DBaseFrameSceneState Underlay,
    Reference3DBaseFrameSceneState OnionSkin,
    long RenderPlanEpoch,
    ulong OpticsState);

internal readonly record struct Reference3DOpticalResponse(
    int ShadeArgb,
    int HighlightArgb,
    float OpacityScale,
    Vector3 AmbientIrradiance)
{
    public static Reference3DOpticalResponse Identity { get; } = new(
        Color.Transparent.ToArgb(),
        Color.Transparent.ToArgb(),
        1f,
        Vector3.One);
}

internal readonly record struct Reference3DLinearLightStop(
    float Position,
    Vector3 DiffuseIrradiance,
    Vector3 SpecularRadiance,
    Vector3 FresnelRadiance = default);

internal readonly record struct Reference3DVectorLighting(
    Vector3 DiffuseIrradiance,
    Vector3 SpecularRadiance,
    Vector3 FresnelRadiance = default);

internal readonly record struct Reference3DShadowLayer(
    Reference3DProjectedContour[] Contours,
    int Argb);

internal readonly record struct Reference3DLocalLightLayer(
    Reference3DProjectedContour[] Contours,
    Matrix3x2 GradientTransform,
    GradientStop[] DiffuseStops,
    GradientStop[] SpecularStops,
    Reference3DLinearLightStop[] LinearStops)
{
    public GradientStop[]? SolidStrokeStops { get; init; }
}

internal sealed class Reference3DOpticalSurface : IDisposable
{
    private readonly int _pixelWidth;
    private readonly int _pixelHeight;

    public Reference3DOpticalSurface(
        Rectangle bounds,
        Bitmap bitmap,
        int[] premultipliedPixels)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ArgumentNullException.ThrowIfNull(premultipliedPixels);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bounds),
                bounds,
                "Optical surface bounds must be non-empty.");
        }

        _pixelWidth = bitmap.Width;
        _pixelHeight = bitmap.Height;
        if (premultipliedPixels.LongLength != (long)_pixelWidth * _pixelHeight)
        {
            throw new ArgumentException(
                "The optical pixel buffer must match the bitmap dimensions.",
                nameof(premultipliedPixels));
        }

        Bounds = bounds;
        Bitmap = bitmap;
        PremultipliedPixels = premultipliedPixels;
    }

    public Rectangle Bounds { get; }

    public Bitmap Bitmap { get; }

    public int[] PremultipliedPixels { get; }

    public int PixelWidth => _pixelWidth;

    public int PixelHeight => _pixelHeight;

    public long ByteSize => (long)PixelWidth * PixelHeight * sizeof(int) * 2;

    public long GpuByteSize => (long)PixelWidth * PixelHeight * sizeof(int);

    public bool TryGetPremultipliedPixelAtScreenPoint(
        float screenX,
        float screenY,
        out int pixel)
    {
        pixel = 0;
        if (!float.IsFinite(screenX)
            || !float.IsFinite(screenY)
            || screenX < Bounds.Left
            || screenY < Bounds.Top
            || screenX >= Bounds.Right
            || screenY >= Bounds.Bottom
            || PixelWidth <= 0
            || PixelHeight <= 0
            || PremultipliedPixels.LongLength != (long)PixelWidth * PixelHeight)
        {
            return false;
        }

        var x = Math.Clamp(
            (int)MathF.Floor((screenX - Bounds.Left) * PixelWidth / Bounds.Width),
            0,
            PixelWidth - 1);
        var y = Math.Clamp(
            (int)MathF.Floor((screenY - Bounds.Top) * PixelHeight / Bounds.Height),
            0,
            PixelHeight - 1);
        pixel = PremultipliedPixels[y * PixelWidth + x];
        return true;
    }

    public void Dispose() => Bitmap.Dispose();
}

internal readonly record struct Reference3DRenderItem(
    int ObjectIndex,
    int LayerIndex,
    Reference3DRenderKind Kind,
    Reference3DProjectedContour[] Contours,
    float AverageDepth,
    Reference3DSurfacePlaneKey PlaneKey,
    int ObjectSlot,
    int SurfaceSlot)
{
    public Reference3DSurfacePlane Plane { get; init; }

    public Vector3 SurfacePoint { get; init; }

    public bool HasSurfacePoint { get; init; }

    public Reference3DProjectedContour[]? FragmentClip { get; init; }

    public Reference3DProjectedContour[]? OcclusionContours { get; init; }

    public Reference3DProjectedContour[]? OpticalSurfaceContours { get; init; }

    public int FragmentSlot { get; init; }

    public ulong StableFragmentIdentity { get; init; }

    public int SecondaryObjectIndex { get; init; } = -1;

    public int EdgeArgb { get; init; }

    public float EdgeWidth { get; init; }

    public bool EdgeStartCap { get; init; } = true;

    public bool EdgeEndCap { get; init; } = true;

    public float MaterialOpacity { get; init; } = 1f;

    public Reference3DOpticalResponse OpticalResponse { get; init; } =
        Reference3DOpticalResponse.Identity;

    public Reference3DShadowLayer[]? ShadowLayers { get; init; }

    public Reference3DLocalLightLayer[]? LocalLightLayers { get; init; }

    public Reference3DOpticalSurface? OpticalSurface { get; init; }

    public int? VectorLightingArgb { get; init; }

    public GradientStop[]? OpticalGradientStops { get; init; }

    public int? SolidStrokeOpticalBaseArgb { get; init; }

    public GradientStop[]? OpticalStrokeGradientStops { get; init; }

    public int SharedShellOwnerObjectIndex { get; init; } = -1;

    public int[]? SharedShellObjectIndices { get; init; }
}

internal readonly record struct Reference3DProjectedSolid(
    Reference3DProjectedContour[] FrontContours,
    Reference3DProjectedContour[] BackContours,
    Reference3DProjectedContour[] SideSurfaces,
    Reference3DSurfacePlane[] SidePlanes,
    Reference3DSurfacePlaneKey[] SidePlaneKeys,
    Reference3DProjectedContour[] SelectionEdges,
    bool HasExtrusion)
{
    public Vector3[] SideSurfacePoints { get; init; } = [];
}

internal readonly record struct Reference3DRenderGroup(
    Reference3DRenderItem[] Items,
    float AverageDepth,
    Reference3DSurfacePlaneKey PlaneKey,
    int StableSlot);

internal readonly record struct Reference3DPlaybackPreparationState(
    SceneDimension Dimension,
    CameraProjection Projection,
    ReferenceViewDirection ViewDirection,
    ReferenceCameraFrame Camera,
    StageViewState View,
    SceneDefinition? SceneDefinition,
    int Width,
    int Height,
    int BackColorArgb,
    float WorldGridOpacity,
    WorldGridType WorldGridType,
    string FontName,
    float FontSize,
    FontStyle FontStyle,
    GraphicsUnit FontUnit);

internal sealed partial class StageControl
{
    internal const float ReferenceNearPlane = 120f;
    internal const float ReferencePerspectiveFocalLength = 12_000f;
    private const float ReferenceMaximumPitch = 1.5f;
    private const float SpatialGizmoTargetPixels = 72f;
    private const float SpatialGizmoHitRadiusPixels = 8f;
    private const int SpatialRotationRingSegments = 48;
    private const float ReferenceExtrusionEpsilon = 0.0001f;
    private const float Reference3DLineEndpointToleranceUnits = 1.5f;
    private const float Reference3DLinePoseTolerance = 0.0001f;
    private const float ReferenceExtrusionCornerCosine = 0.94f;
    private const float ReferenceSurfaceOverlapTolerancePixels = 0.75f;
    private const float ReferenceSurfaceNormalEpsilon = 0.00000001f;
    private const double ReferenceSurfaceNormalQuantization = 100_000d;
    private const double ReferenceSurfacePlaneQuantization = 1_000d;
    private const float SpatialGizmoMinimumPlaneAltitudePixels = 3f;
    private const int MaximumReference3DRenderPlanCacheEntries = 64;
    private const long MaximumReference3DOpticalSurfaceCacheBytes = 128L * 1024 * 1024;
    private const int Reference3DOpticalPreviewIdleMilliseconds = 120;
    internal const float SpatialGizmoMinimumPlaneRayDot = 0.06f;

    internal static Color Reference3DSelectionLineColor { get; } = Color.FromArgb(255, 255, 145, 44);
    internal static Color Reference3DSelectionHaloColor { get; } = Color.FromArgb(112, 112, 48, 8);

    private SceneCompositionResult? _sceneCompositionResult;
    private VectorScene? _sceneCompositionResultScene;
    private bool _sceneCompositionHasSpatialPoses;
    private SceneDefinition? _referenceSceneDefinition;
    private SceneCompositionMaskClip[] _sceneCompositionMaskClips = [];
    private ReferenceViewDirection _reference2DViewDirection = ReferenceViewDirection.Front;
    private int[] _reference3DSelectedObjects = [];
    private bool _spatialTransformGizmoVisible;
    private Vector3 _spatialTransformGizmoOrigin;
    private SpatialTransformMode _spatialTransformGizmoMode;
    private SpatialGizmoBasis _spatialTransformGizmoBasis = SpatialGizmoBasis.Identity;
    private SpatialTransformHandleHit _spatialTransformHoveredHandle = SpatialTransformHandleHit.None;
    private SpatialTransformHandleHit _spatialTransformActiveHandle = SpatialTransformHandleHit.None;
    private readonly List<Reference3DRenderPlanCacheEntry> _reference3DRenderPlanCache = [];
    private long _reference3DRenderPlanSurfaceBytes;
    private long _reference3DRenderPlanEpoch;
    private Reference3DOpticsContentHashCacheKey _reference3DOpticsContentHashCacheKey;
    private ulong _reference3DOpticsContentHashCacheValue;
    private bool _reference3DOpticsContentHashCacheValid;
    private SceneCompositionResult? _reference3DOpticsMaterialHashComposition;
    private ulong _reference3DOpticsMaterialHash;
    private bool _reference3DOpticsMaterialHashValid;
    private Reference3DActiveLayerObjectCacheKey _reference3DActiveLayerObjectCacheKey;
    private int[][] _reference3DActiveLayerObjects = [];
    private int[][] _reference3DSortedLayerObjects = [];
    private List<int>?[] _reference3DActiveLayerBuckets = [];
    private int[] _reference3DActiveKeyframes = [];
    private float[] _reference3DObjectDepths = [];
    private long _reference3DSortedLayerObjectsEpoch = -1;
    private Comparison<int>? _reference3DObjectDepthComparison;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(VectorScene Scene, int ObjectIndex),
        (long GeometryRevision, long SummaryRevision, int EditFrame, Reference3DSourceContour[] Contours)>
        _reference3DSourceContourCache = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(VectorScene Scene, int ObjectIndex), Reference3DProjectedContour[]>
        _reference3DProjectedContourCache = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(VectorScene Scene, int ObjectIndex), Reference3DProjectedSolid>
        _reference3DProjectedSolidCache = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(
        VectorScene Scene,
        int ObjectIndex,
        bool UsePrimaryContourQuad,
        Reference3DSourceContour[] SourceContours),
        Reference3DProjectiveTriangle[]> _reference3DProjectiveMeshCache = new();
    private bool _reference3DOpticalInteractionPreviewActive;
    private readonly System.Windows.Forms.Timer _reference3DOpticalPreviewTimer = new()
    {
        Interval = Reference3DOpticalPreviewIdleMilliseconds
    };

    private readonly record struct Reference3DRenderPlanCacheKey(
        VectorScene Scene,
        long GeometryRevision,
        long SummaryRevision,
        int ObjectCount,
        int LayerCount,
        int Frame,
        int EditFrame,
        int ViewportWidth,
        int ViewportHeight,
        SceneDimension Dimension,
        ReferenceViewDirection ViewDirection,
        CameraProjection Projection,
        float ProjectionBlend,
        float Yaw,
        float Pitch,
        float Distance,
        float ZoomScale,
        float TargetX,
        float TargetY,
        float TargetZ,
        SceneCompositionResult? Composition,
        VectorScene? CompositionTarget,
        bool CompositionHasSpatialPoses,
        ulong LayerRenderState,
        ulong OpticsState,
        int OpticalRasterLod,
        long Epoch,
        bool SubstituteOutlineItems);

    private readonly record struct Reference3DActiveLayerObjectCacheKey(
        VectorScene Scene,
        long GeometryRevision,
        long SummaryRevision,
        long ActiveContentRevision,
        int ObjectCount,
        int LayerCount,
        int Frame);

    private readonly record struct Reference3DOpticsContentHashCacheKey(
        SceneDefinition Definition,
        long LightingRevision,
        int Frame,
        SceneCompositionResult? Composition,
        VectorScene? CompositionScene,
        long RenderPlanEpoch);

    private sealed class Reference3DRenderPlanCacheEntry : IDisposable
    {
        public Reference3DRenderPlanCacheEntry(
            Reference3DRenderPlanCacheKey key,
            int[]? layers,
            Reference3DRenderItem[] items)
        {
            Key = key;
            Layers = layers;
            Items = items;
        }

        public Reference3DRenderPlanCacheKey Key { get; }

        public int[]? Layers { get; }

        public Reference3DRenderItem[] Items { get; }

        public long OpticalSurfaceBytes => Items
            .Select(item => item.OpticalSurface)
            .OfType<Reference3DOpticalSurface>()
            .Distinct()
            .Sum(surface => surface.ByteSize);

        public bool Matches(
            Reference3DRenderPlanCacheKey key,
            IReadOnlyList<int>? layers)
        {
            if (Key != key) return false;
            if (Layers is null || layers is null) return Layers is null && layers is null;
            if (Layers.Length != layers.Count) return false;
            for (var index = 0; index < Layers.Length; index++)
            {
                if (Layers[index] != layers[index]) return false;
            }
            return true;
        }

        public void Dispose()
        {
            foreach (var surface in Items
                         .Select(item => item.OpticalSurface)
                         .OfType<Reference3DOpticalSurface>()
                         .Distinct())
            {
                surface.Dispose();
            }
        }
    }

    internal SceneCompositionResult? SceneCompositionResult => _sceneCompositionResult;
    internal VectorScene? SceneCompositionResultScene => _sceneCompositionResultScene;
    internal IReadOnlyList<SceneCompositionMaskClip> SceneCompositionMaskClips => _sceneCompositionMaskClips;
    internal ReferenceViewDirection Reference2DViewDirection => _reference2DViewDirection;

    internal Reference3DPlaybackPreparationState CaptureReference3DPlaybackPreparationState() =>
        new(
            ReferenceDimension,
            ReferenceProjection,
            _reference2DViewDirection,
            CaptureReferenceCameraFrameForPersistence(),
            CaptureViewState(),
            _referenceSceneDefinition,
            Width,
            Height,
            BackColor.ToArgb(),
            WorldGridOpacity,
            WorldGridType,
            Font.Name,
            Font.Size,
            Font.Style,
            Font.Unit);

    internal void ConfigureReference3DPlaybackPreparation(
        VectorScene scene,
        SceneCompositionResult composition,
        int frame,
        Reference3DPlaybackPreparationState state)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(composition);

        Scene = scene;
        _frame = Math.Max(0, frame);
        CameraX = Math.Clamp(state.View.CameraX, -5_000_000f, 5_000_000f);
        CameraY = Math.Clamp(state.View.CameraY, -5_000_000f, 5_000_000f);
        Zoom = Math.Clamp(state.View.Zoom, 0.02f, 64f);
        ReferenceDimension = state.Dimension;
        ReferenceProjection = state.Projection;
        _reference2DViewDirection = state.ViewDirection;
        _referenceYaw = NormalizeRadians(state.Camera.Yaw);
        _referencePitch = Math.Clamp(state.Camera.Pitch, -ReferenceMaximumPitch, ReferenceMaximumPitch);
        _referenceDistance = Math.Max(0.0001f, state.Camera.Distance);
        _referenceZoomScale = Math.Max(0.0001f, state.Camera.ZoomScale);
        _referenceTargetX = state.Camera.TargetX;
        _referenceTargetY = state.Camera.TargetY;
        _referenceTargetZ = state.Camera.TargetZ;
        _referenceProjectionBlend = Math.Clamp(state.Camera.ProjectionBlend, 0f, 1f);
        _referenceCameraTransitionActive = false;
        _referenceSceneDefinition = state.SceneDefinition;
        BackColor = Color.FromArgb(state.BackColorArgb);
        WorldGridOpacity = state.WorldGridOpacity;
        WorldGridType = state.WorldGridType;
        var fontSize = Math.Max(0.1f, state.FontSize);
        if (!string.Equals(Font.Name, state.FontName, StringComparison.Ordinal)
            || Font.Size != fontSize
            || Font.Style != state.FontStyle
            || Font.Unit != state.FontUnit)
        {
            Font = new Font(
                state.FontName,
                fontSize,
                state.FontStyle,
                state.FontUnit);
        }
        _sceneCompositionResult = composition;
        _sceneCompositionResultScene = scene;
        _sceneCompositionHasSpatialPoses = composition.ObjectPoses.Any(
            pose => pose.FlatToScene != Matrix4x4.Identity);
        _sceneCompositionMaskClips = [];
        _reference3DSelectedObjects = [];
        InvalidateReference3DRenderPlanCache();
    }

    internal object? PrepareReference3DPlaybackRaster() =>
        _direct2DRenderer.PrepareReference3DPlaybackRaster(this);

    internal object? PrepareReference3DPlaybackRasterFrame(object? preparedRaster) =>
        _direct2DRenderer.PrepareReference3DPlaybackRasterFrame(this, preparedRaster);

    internal bool QueueReference3DPlaybackRaster() =>
        _direct2DRenderer.QueueReference3DPlaybackRaster(this);
    // Scene Building keeps one orthographic spatial pipeline in 2D, including frames whose pose is planar.
    // Underlay and drag-preview passes temporarily replace Scene but must retain that editable front camera.
    private bool UsesSpatialFrontView => ReferenceDimension == SceneDimension.TwoD
        && _reference2DViewDirection == ReferenceViewDirection.Front
        && _sceneCompositionResult is not null
        && _sceneCompositionResultScene is not null;
    internal bool UsesSpatialFrontProjection => UsesSpatialFrontView
        && ReferenceEquals(_sceneCompositionResultScene, Scene);
    internal bool UsesReferenceProjection => ReferenceDimension == SceneDimension.ThreeD
        || _reference2DViewDirection != ReferenceViewDirection.Front
        || UsesSpatialFrontProjection;
    internal bool UsesSceneOpticsProjection => UsesReferenceProjection
        && ReferenceEquals(_sceneCompositionResultScene, Scene)
        && _referenceSceneDefinition?.LightingInitialized == true;
    internal CameraProjection EffectiveReferenceProjection =>
        ReferenceDimension == SceneDimension.TwoD && UsesReferenceProjection
            ? CameraProjection.Orthographic
            : ReferenceProjection;
    internal float EffectiveReferenceYaw => _referenceCameraTransitionActive
        ? _referenceYaw
        : UsesSpatialFrontView ? 0f : _referenceYaw;
    internal float EffectiveReferencePitch => _referenceCameraTransitionActive
        ? _referencePitch
        : UsesSpatialFrontView ? 0f : _referencePitch;
    internal IReadOnlyList<int> Reference3DSelectedObjects => _reference3DSelectedObjects;
    internal bool SpatialTransformGizmoVisible => _spatialTransformGizmoVisible;
    internal Vector3 SpatialTransformGizmoOrigin => _spatialTransformGizmoOrigin;
    internal SpatialTransformMode SpatialTransformGizmoMode => _spatialTransformGizmoMode;
    internal SpatialGizmoBasis SpatialTransformGizmoBasis => _spatialTransformGizmoBasis;
    internal SpatialTransformHandleHit SpatialTransformHoveredHandle => _spatialTransformHoveredHandle;
    internal SpatialTransformHandleHit SpatialTransformActiveHandle => _spatialTransformActiveHandle;
    internal SpatialTransformHandleHit SpatialTransformHighlightedHandle =>
        _spatialTransformActiveHandle.IsValid
            ? _spatialTransformActiveHandle
            : _spatialTransformHoveredHandle;
    internal float SpatialGizmoDpiScale => Math.Clamp(DeviceDpi / 96f, 1f, 3f);
    internal long Reference3DRenderPlanBuildCount { get; private set; }

    internal long Reference3DOpticsPlanBuildCount { get; private set; }

    internal double LastReference3DLayerItemsMilliseconds { get; private set; }

    internal double LastReference3DIntersectionMilliseconds { get; private set; }

    internal double LastReference3DSortMilliseconds { get; private set; }

    internal double LastReference3DOpticsMilliseconds { get; private set; }

    internal int LastReference3DOpticalLightEvaluations { get; private set; }

    internal int LastReference3DShadowProjectionCount { get; private set; }

    internal int LastReference3DShadowLayerCount { get; private set; }

    internal int LastReference3DLocalLightLayerCount { get; private set; }

    internal int LastReference3DOpticalRasterLod { get; private set; } = -1;

    internal bool Reference3DOpticalInteractionPreviewActive =>
        _reference3DOpticalInteractionPreviewActive;

    private int Reference3DOpticalRasterStartLod =>
        _reference3DOpticalInteractionPreviewActive || ReferenceCameraTransitionActive
            ? MaximumReference3DOpticalRasterLod
            : 0;

    internal void BeginReference3DOpticalInteractionPreview()
    {
        _reference3DOpticalPreviewTimer.Stop();
        SetReference3DOpticalInteractionPreview(active: true);
    }

    internal void PulseReference3DOpticalInteractionPreview()
    {
        if (ReferenceDimension != SceneDimension.ThreeD) return;
        SetReference3DOpticalInteractionPreview(active: true);
        _reference3DOpticalPreviewTimer.Stop();
        _reference3DOpticalPreviewTimer.Start();
    }

    internal void EndReference3DOpticalInteractionPreview()
    {
        _reference3DOpticalPreviewTimer.Stop();
        SetReference3DOpticalInteractionPreview(active: false);
    }

    private void SetReference3DOpticalInteractionPreview(bool active)
    {
        if (_reference3DOpticalInteractionPreviewActive == active) return;
        _reference3DOpticalInteractionPreviewActive = active;
        Invalidate();
    }

    internal void SetSceneCompositionResult(
        SceneCompositionResult? result,
        VectorScene? targetScene = null,
        bool preserveWorkspaceFrameCache = false,
        bool requestPaint = true)
    {
        var nextTarget = result is null ? null : targetScene ?? Scene;
        var nextHasSpatialPoses = result?.ObjectPoses.Any(
            pose => pose.FlatToScene != Matrix4x4.Identity) == true;
        if (ReferenceEquals(_sceneCompositionResult, result)
            && ReferenceEquals(_sceneCompositionResultScene, nextTarget)
            && _sceneCompositionHasSpatialPoses == nextHasSpatialPoses)
        {
            return;
        }
        if (_referenceCameraTransitionActive
            && !ReferenceEquals(_sceneCompositionResultScene, nextTarget))
        {
            CompleteReferenceCameraTransition(invalidate: false);
        }
        _sceneCompositionResult = result;
        _sceneCompositionResultScene = nextTarget;
        _sceneCompositionHasSpatialPoses = nextHasSpatialPoses;
        _reference3DSelectedObjects = _reference3DSelectedObjects
            .Where(index => nextTarget is not null && (uint)index < nextTarget.ObjectCount)
            .ToArray();
        RequestStageFrame(
            basePresentationChanged: true,
            preserveReference3DWorkspaceFrameCache: preserveWorkspaceFrameCache,
            requestPaint: requestPaint);
    }

    internal void SetReference3DPlaybackComposition(
        VectorScene scene,
        SceneCompositionResult composition,
        object? rasterPreparation,
        object? rasterFrame)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(composition);

        // Playback frames already carry their immutable render plan and
        // raster result. Rebinding the live stage without invalidating the
        // editor caches keeps this UI-thread operation limited to state
        // publication; the normal invalidation is performed when playback
        // stops and editing resumes.
        Scene = scene;
        var nextHasSpatialPoses = composition.ObjectPoses.Any(
            pose => pose.FlatToScene != Matrix4x4.Identity);
        _sceneCompositionResult = composition;
        _sceneCompositionResultScene = scene;
        _sceneCompositionHasSpatialPoses = nextHasSpatialPoses;
        _reference3DSelectedObjects = _reference3DSelectedObjects
            .Where(index => (uint)index < scene.ObjectCount)
            .ToArray();
        Reference3DPlaybackRasterPreparation = rasterPreparation;
        SetReference3DPlaybackRasterFrame(rasterFrame);
    }

    internal void SetSceneCompositionMaskClips(
        IEnumerable<SceneCompositionMaskClip>? clips,
        bool preserveWorkspaceFrameCache = false)
    {
        _sceneCompositionMaskClips = clips?
            .Where(clip => clip.TargetScene is not null
                && clip.MaskScene is not null
                && clip.TargetObjectIndices is { Count: > 0 })
            .Select(clip => new SceneCompositionMaskClip(
                clip.TargetScene,
                clip.MaskScene,
                clip.Frame,
                clip.TargetObjectIndices
                    .Where(index => index >= 0)
                    .ToHashSet()))
            .Where(clip => clip.TargetObjectIndices.Count > 0)
            .ToArray()
            ?? [];
        RequestStageFrame(
            basePresentationChanged: true,
            preserveReference3DWorkspaceFrameCache: preserveWorkspaceFrameCache);
    }

    internal IReadOnlyList<SceneCompositionMaskClip> GetSceneCompositionMaskClips(int objectIndex)
    {
        if ((uint)objectIndex >= Scene.ObjectCount || _sceneCompositionMaskClips.Length == 0) return [];
        return _sceneCompositionMaskClips
            .Where(clip => ReferenceEquals(clip.TargetScene, Scene)
                && clip.TargetObjectIndices.Contains(objectIndex))
            .ToArray();
    }

    internal bool HasSceneCompositionMaskClips(VectorScene scene)
    {
        return _sceneCompositionMaskClips.Any(clip => ReferenceEquals(clip.TargetScene, scene));
    }

    internal void SetReference3DSelection(IReadOnlyCollection<int>? objectIndices)
    {
        var next = objectIndices is { Count: > 0 }
            ? objectIndices
                .Where(index => (uint)index < Scene.ObjectCount)
                .Distinct()
                .Order()
                .ToArray()
            : [];
        if (_reference3DSelectedObjects.AsSpan().SequenceEqual(next)) return;
        _reference3DSelectedObjects = next;
        InvalidateOverlay();
    }

    internal void ClearReference3DSelection()
    {
        if (_reference3DSelectedObjects.Length == 0) return;
        _reference3DSelectedObjects = [];
        InvalidateOverlay();
    }

    internal void SetSpatialTransformGizmo(
        Vector3 origin,
        SpatialTransformMode mode,
        IReadOnlyCollection<int>? selectedObjectIndices = null,
        SpatialGizmoBasis? basis = null,
        SpatialGizmoMotion motion = SpatialGizmoMotion.Immediate)
    {
        if (!Finite(origin))
        {
            ClearSpatialTransformGizmo();
            return;
        }

        if (selectedObjectIndices is not null) SetReference3DSelection(selectedObjectIndices);
        var nextBasis = basis is { IsValid: true } value ? value : SpatialGizmoBasis.Identity;
        var visibilityChanged = !_spatialTransformGizmoVisible;
        var changed = visibilityChanged
            || _spatialTransformGizmoOrigin != origin
            || _spatialTransformGizmoMode != mode
            || _spatialTransformGizmoBasis != nextBasis;
        if (_spatialTransformGizmoMode != mode)
        {
            _spatialTransformHoveredHandle = SpatialTransformHandleHit.None;
            _spatialTransformActiveHandle = SpatialTransformHandleHit.None;
        }
        _spatialTransformGizmoVisible = true;
        _spatialTransformGizmoOrigin = origin;
        _spatialTransformGizmoMode = mode;
        _spatialTransformGizmoBasis = nextBasis;
        if (visibilityChanged) RecordSpatialTransformGizmoMotionTargetChange();
        RetargetSpatialTransformGizmoMotion(motion);
        if (changed && !SpatialTransformGizmoMotionActive) InvalidateOverlay();
    }

    internal void ClearSpatialTransformGizmo(
        SpatialGizmoMotion motion = SpatialGizmoMotion.Immediate)
    {
        var changed = _spatialTransformGizmoVisible
            || _spatialTransformHoveredHandle.IsValid
            || _spatialTransformActiveHandle.IsValid;
        var visibilityChanged = _spatialTransformGizmoVisible;
        _spatialTransformGizmoVisible = false;
        _spatialTransformHoveredHandle = SpatialTransformHandleHit.None;
        _spatialTransformActiveHandle = SpatialTransformHandleHit.None;
        if (visibilityChanged) RecordSpatialTransformGizmoMotionTargetChange();
        RetargetSpatialTransformGizmoMotion(motion);
        if (changed && !SpatialTransformGizmoMotionActive) InvalidateOverlay();
    }

    internal void SetSpatialTransformHover(SpatialTransformHandleHit handle)
    {
        var next = NormalizeSpatialTransformHandle(handle);
        if (_spatialTransformHoveredHandle == next) return;
        _spatialTransformHoveredHandle = next;
        InvalidateOverlay();
    }

    internal void SetSpatialTransformActive(SpatialTransformHandleHit handle)
    {
        var next = NormalizeSpatialTransformHandle(handle);
        if (_spatialTransformActiveHandle == next) return;
        _spatialTransformActiveHandle = next;
        if (next.IsValid && SpatialTransformGizmoMotionActive)
        {
            CompleteSpatialTransformGizmoMotion(invalidate: false);
        }
        InvalidateOverlay();
    }

    internal bool IsSpatialTransformHandleHighlighted(SpatialTransformAxis axis)
    {
        var highlighted = SpatialTransformHighlightedHandle;
        return highlighted.IsValid
            && highlighted.Mode == _spatialTransformGizmoMode
            && highlighted.Axis == axis;
    }

    private SpatialTransformHandleHit NormalizeSpatialTransformHandle(SpatialTransformHandleHit handle)
    {
        return _spatialTransformGizmoVisible
            && handle.IsValid
            && handle.Mode == _spatialTransformGizmoMode
                ? handle
                : SpatialTransformHandleHit.None;
    }

    internal SpatialTransformHandleHit HitTestSpatialTransformGizmo(Point screen)
    {
        if (!_spatialTransformGizmoVisible
            || ReferenceDimension != SceneDimension.ThreeD
            || !TryGetSpatialGizmoRenderGeometry(
                out var geometry,
                out _,
                out _))
        {
            return SpatialTransformHandleHit.None;
        }

        if (_spatialTransformGizmoMode == SpatialTransformMode.Rotate)
        {
            var bestRotationAxis = SpatialTransformAxis.None;
            var bestRotationDistance = float.MaxValue;
            foreach (var axis in SpatialAxes)
            {
                var ring = geometry.Rings[(int)axis - 1];
                if (ring.Length <= 1) continue;
                var distance = DistanceToPolyline(screen, ring, closed: true);
                if (distance >= bestRotationDistance) continue;
                bestRotationDistance = distance;
                bestRotationAxis = axis;
            }

            return bestRotationDistance <= SpatialGizmoHitRadiusPixels * SpatialGizmoDpiScale
                ? new SpatialTransformHandleHit(_spatialTransformGizmoMode, bestRotationAxis)
                : SpatialTransformHandleHit.None;
        }

        if (_spatialTransformGizmoMode == SpatialTransformMode.Scale
            && ReferencePointDistance(screen, geometry.Origin)
                <= SpatialGizmoHitRadiusPixels * SpatialGizmoDpiScale)
        {
            return new SpatialTransformHandleHit(_spatialTransformGizmoMode, SpatialTransformAxis.Uniform);
        }

        var bestAxis = SpatialTransformAxis.None;
        var bestDistance = float.MaxValue;
        foreach (var axis in SpatialAxes)
        {
            var endpoint = geometry.Endpoints[(int)axis - 1];
            var distance = DistanceToSegment(screen, geometry.Origin, endpoint);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            bestAxis = axis;
        }

        if (bestDistance <= SpatialGizmoHitRadiusPixels * SpatialGizmoDpiScale)
        {
            return new SpatialTransformHandleHit(_spatialTransformGizmoMode, bestAxis);
        }

        if (_spatialTransformGizmoMode == SpatialTransformMode.Move)
        {
            for (var planeIndex = SpatialPlanes.Length - 1; planeIndex >= 0; planeIndex--)
            {
                var polygon = geometry.PlaneHandles[planeIndex];
                if (polygon.Length == 4 && PointInSpatialGizmoPolygon(screen, polygon))
                {
                    return new SpatialTransformHandleHit(
                        _spatialTransformGizmoMode,
                        SpatialPlanes[planeIndex]);
                }
            }
        }

        return SpatialTransformHandleHit.None;
    }

    internal bool TryGetReference3DFocusPoints(
        IReadOnlyCollection<int>? objectIndices,
        out Vector3[] scenePoints)
    {
        scenePoints = [];
        return ReferenceDimension == SceneDimension.ThreeD
            && TryGetReferenceFocusPoints(objectIndices, out scenePoints);
    }

    internal bool TryGetReferenceFocusPoints(
        IReadOnlyCollection<int>? objectIndices,
        out Vector3[] scenePoints)
    {
        scenePoints = [];
        if (!UsesReferenceProjection || objectIndices is not { Count: > 0 })
        {
            return false;
        }

        var includeExtrusion = ReferenceDimension == SceneDimension.ThreeD;
        var points = new List<Vector3>();
        foreach (var objectIndex in objectIndices.Distinct().Order())
        {
            if ((uint)objectIndex >= Scene.ObjectCount) continue;
            var frontOffset = includeExtrusion
                ? GetReference3DExtrusionOffset(objectIndex, front: true)
                : Vector3.Zero;
            var backOffset = includeExtrusion
                ? GetReference3DExtrusionOffset(objectIndex, front: false)
                : Vector3.Zero;
            var added = false;
            foreach (var contour in GetReference3DSourceContours(objectIndex))
            {
                foreach (var flatPoint in contour.Points)
                {
                    if (TryTransformFlatPointToScene(objectIndex, flatPoint, frontOffset, out var front))
                    {
                        points.Add(front);
                        added = true;
                    }
                    if (backOffset != frontOffset
                        && TryTransformFlatPointToScene(objectIndex, flatPoint, backOffset, out var back))
                    {
                        points.Add(back);
                    }
                }
            }

            if (!added)
            {
                if (TryTransformFlatPointToScene(
                        objectIndex,
                        new PointF(Scene.X[objectIndex], Scene.Y[objectIndex]),
                        frontOffset,
                        out var center)
                    && Finite(center))
                {
                    points.Add(center);
                }
            }
        }

        scenePoints = points.ToArray();
        return scenePoints.Length > 0;
    }

    internal void SetReferenceViewDirection(
        ReferenceViewDirection direction,
        ReferenceCameraMotion motion = ReferenceCameraMotion.Immediate)
    {
        if (!Enum.IsDefined(direction)) return;
        SetReferenceViewDirectionCore(direction, motion);
    }

    internal bool TryProjectScenePoint(
        int objectIndex,
        PointF flatPoint,
        out PointF screen,
        out float depth)
    {
        screen = PointF.Empty;
        depth = 0;
        if ((uint)objectIndex >= Scene.ObjectCount
            || !TryTransformFlatPointToScene(objectIndex, flatPoint, out var scenePoint))
        {
            return false;
        }

        return TryProjectScenePosition(scenePoint, out screen, out depth);
    }

    internal bool TryProjectReference3DFrontPoint(
        int objectIndex,
        PointF flatPoint,
        out PointF screen,
        out float depth)
    {
        screen = PointF.Empty;
        depth = 0;
        if ((uint)objectIndex >= Scene.ObjectCount
            || !TryTransformFlatPointToScene(
                objectIndex,
                flatPoint,
                GetReference3DExtrusionOffset(objectIndex, front: true),
                out var scenePoint))
        {
            return false;
        }

        return TryProjectScenePosition(scenePoint, out screen, out depth);
    }

    internal bool TryProjectScenePosition(Vector3 scenePoint, out PointF screen, out float depth)
    {
        screen = PointF.Empty;
        depth = 0;
        if (!Finite(scenePoint)) return false;
        var camera = CameraSpacePoint(new Point3(scenePoint.X, -scenePoint.Y, scenePoint.Z));
        depth = camera.Z;
        if (camera.Z < ReferenceNearPlane) return false;
        screen = ProjectCameraPoint(camera);
        return float.IsFinite(screen.X) && float.IsFinite(screen.Y);
    }

    internal bool TryGetReferenceRay(Point screen, out SpatialRay ray)
    {
        ray = default;
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return false;

        var scale = 0.035f * _referenceZoomScale;
        if (scale <= 0.000001f) return false;
        var cameraX = (screen.X - Width * 0.5f) / scale;
        var cameraY = -(screen.Y - Height * 0.58f) / scale;
        Vector3 cameraOrigin;
        Vector3 cameraDirection;
        var perspectiveBlend = ReferenceProjectionBlend;
        cameraOrigin = new Vector3(
            cameraX * (1 - perspectiveBlend),
            cameraY * (1 - perspectiveBlend),
            0);
        cameraDirection = Vector3.Normalize(new Vector3(
            cameraX * perspectiveBlend / ReferencePerspectiveFocalLength,
            cameraY * perspectiveBlend / ReferencePerspectiveFocalLength,
            1));

        var referenceOrigin = CameraToReference(cameraOrigin, direction: false);
        var referenceDirection = Vector3.Normalize(CameraToReference(cameraDirection, direction: true));
        var sceneOrigin = new Vector3(referenceOrigin.X, -referenceOrigin.Y, referenceOrigin.Z);
        var sceneDirection = Vector3.Normalize(new Vector3(
            referenceDirection.X,
            -referenceDirection.Y,
            referenceDirection.Z));
        if (!Finite(sceneOrigin) || !Finite(sceneDirection)) return false;
        ray = new SpatialRay(sceneOrigin, sceneDirection);
        return true;
    }

    internal bool TryHitTestProjectedObject(
        Point screen,
        float tolerancePixels,
        out int objectIndex)
    {
        objectIndex = -1;
        tolerancePixels = Math.Max(1f, tolerancePixels);
        var drawingItems = GetReference3DDrawingItems();
        for (var itemIndex = drawingItems.Length - 1; itemIndex >= 0; itemIndex--)
        {
            var item = drawingItems[itemIndex];
            if (!IsPointWithinReference3DFragment(screen, item)) continue;

            if (item.Kind is Reference3DRenderKind.Back or Reference3DRenderKind.Side)
            {
                if (!PointInProjectedFill(screen, item.Contours)
                    || !TryResolveReference3DSharedShellHit(screen, item, out objectIndex))
                {
                    continue;
                }
                return true;
            }

            var candidate = item.ObjectIndex;
            if (IsObjectHiddenForRendering(Scene, candidate)
                || !IsProjectedPointVisibleThroughMasks(screen, candidate))
            {
                continue;
            }

            if (item.Kind == Reference3DRenderKind.FrontFill
                && PointInProjectedFill(screen, item.Contours))
            {
                objectIndex = candidate;
                return true;
            }

            if (item.Kind != Reference3DRenderKind.FrontStroke) continue;
            var strokeTolerance = tolerancePixels
                + GetReference3DStrokeWidth(candidate, Scene.Stroke[candidate]) * 0.5f;
            if (item.Contours.Any(contour =>
                    DistanceToPolyline(screen, contour.Points, contour.Closed) <= strokeTolerance))
            {
                objectIndex = candidate;
                return true;
            }
        }

        return false;
    }

    internal bool TryResolveReference3DSharedShellHit(
        Point screen,
        Reference3DRenderItem item,
        out int objectIndex)
    {
        objectIndex = -1;
        var candidates = item.SharedShellObjectIndices;
        if (candidates is not { Length: > 0 })
        {
            var candidate = item.ObjectIndex;
            if ((uint)candidate >= Scene.ObjectCount
                || IsObjectHiddenForRendering(Scene, candidate)
                || !IsProjectedPointVisibleThroughMasks(screen, candidate))
            {
                return false;
            }
            objectIndex = candidate;
            return true;
        }

        var bestDistance = float.MaxValue;
        foreach (var candidate in candidates)
        {
            if ((uint)candidate >= Scene.ObjectCount
                || IsObjectHiddenForRendering(Scene, candidate)
                || !IsProjectedPointVisibleThroughMasks(screen, candidate))
            {
                continue;
            }

            var distance = float.MaxValue;
            var hitOffset = item.Kind switch
            {
                Reference3DRenderKind.Back => GetReference3DExtrusionOffset(
                    candidate,
                    front: false),
                Reference3DRenderKind.Side => Vector3.Zero,
                _ => GetReference3DExtrusionOffset(candidate, front: true)
            };
            var hitContours = ProjectReference3DContours(
                candidate,
                GetReference3DSourceContours(Scene, candidate),
                hitOffset);
            foreach (var contour in hitContours)
            {
                distance = Math.Min(
                    distance,
                    DistanceToPolyline(screen, contour.Points, contour.Closed));
            }

            const float distanceTieTolerance = 0.001f;
            var replacesBest = objectIndex < 0
                || distance < bestDistance - distanceTieTolerance;
            if (!replacesBest
                && Math.Abs(distance - bestDistance) <= distanceTieTolerance)
            {
                var orderComparison = Scene.ObjectOrder[candidate]
                    .CompareTo(Scene.ObjectOrder[objectIndex]);
                replacesBest = orderComparison > 0
                    || orderComparison == 0 && candidate > objectIndex;
            }
            if (!replacesBest) continue;
            bestDistance = distance;
            objectIndex = candidate;
        }
        return objectIndex >= 0;
    }

    private bool IsProjectedPointVisibleThroughMasks(Point screen, int objectIndex)
    {
        var layer = Scene.ObjectLayer[objectIndex];
        if (Scene.GetLayerKind(layer) != DrawingLayerKind.Mask
            && Scene.TryGetMaskLayerIndex(layer, out var maskLayer))
        {
            if (!Scene.IsLayerEffectivelyVisible(maskLayer)) return false;
            var maskContours = GetReference3DLayerObjects(maskLayer)
                .Where(index => SceneRenderOrder.HasFill(Scene.ShapeKind[index])
                    && !IsObjectHiddenForRendering(Scene, index))
                .SelectMany(GetReference3DProjectedContours)
                .ToArray();
            if (maskContours.Length == 0 || !PointInProjectedFill(screen, maskContours)) return false;
        }

        var clips = GetSceneCompositionMaskClips(objectIndex);
        if (clips.Count == 0) return true;
        foreach (var clip in clips)
        {
            var contours = GetReference3DProjectedMaskContours(clip);
            if (contours.Length == 0 || !PointInProjectedFill(screen, contours)) return false;
        }
        return true;
    }

    internal int[] GetReference3DLayerObjects(int layer)
    {
        if ((uint)layer >= Scene.LayerCount) return [];
        EnsureReference3DLayerObjectCache();
        EnsureReference3DSortedLayerObjectCache();
        return _reference3DSortedLayerObjects[layer];
    }

    private void EnsureReference3DLayerObjectCache()
    {
        var scene = Scene;
        var key = new Reference3DActiveLayerObjectCacheKey(
            scene,
            scene.GeometryRevision,
            scene.SummaryRevision,
            scene.ActiveContentRevision,
            scene.ObjectCount,
            scene.LayerCount,
            Frame);
        if (key == _reference3DActiveLayerObjectCacheKey
            && _reference3DActiveLayerObjects.Length == scene.LayerCount)
        {
            return;
        }

        if (_reference3DActiveKeyframes.Length < scene.LayerCount)
        {
            Array.Resize(ref _reference3DActiveKeyframes, scene.LayerCount);
        }
        scene.PopulateActiveKeyframeFrames(Frame, _reference3DActiveKeyframes);

        if (_reference3DActiveLayerBuckets.Length != scene.LayerCount)
        {
            Array.Resize(ref _reference3DActiveLayerBuckets, scene.LayerCount);
        }
        for (var layer = 0; layer < scene.LayerCount; layer++)
        {
            _reference3DActiveLayerBuckets[layer]?.Clear();
        }

        foreach (var objectIndex in scene.GetActiveObjectIndices(Frame))
        {
            var objectLayer = scene.ObjectLayer[objectIndex];
            (_reference3DActiveLayerBuckets[objectLayer] ??= new List<int>(64))
                .Add(objectIndex);
        }

        if (_reference3DActiveLayerObjects.Length != scene.LayerCount)
        {
            Array.Resize(ref _reference3DActiveLayerObjects, scene.LayerCount);
            Array.Resize(ref _reference3DSortedLayerObjects, scene.LayerCount);
        }
        for (var layer = 0; layer < scene.LayerCount; layer++)
        {
            var source = _reference3DActiveLayerBuckets[layer];
            var count = source?.Count ?? 0;
            var objects = _reference3DActiveLayerObjects[layer] ?? [];
            if (objects.Length != count) objects = new int[count];
            if (count > 0) source!.CopyTo(objects, 0);
            _reference3DActiveLayerObjects[layer] = objects;
        }

        _reference3DActiveLayerObjectCacheKey = key;
        _reference3DSortedLayerObjectsEpoch = -1;
    }

    private void EnsureReference3DSortedLayerObjectCache()
    {
        EnsureReference3DLayerObjectCache();
        if (_reference3DSortedLayerObjectsEpoch == _reference3DRenderPlanEpoch
            && _reference3DSortedLayerObjects.Length == Scene.LayerCount)
        {
            return;
        }

        if (_reference3DSortedLayerObjects.Length != Scene.LayerCount)
        {
            Array.Resize(ref _reference3DSortedLayerObjects, Scene.LayerCount);
        }

        if (_reference3DObjectDepths.Length != Scene.ObjectCount)
        {
            Array.Resize(ref _reference3DObjectDepths, Scene.ObjectCount);
        }
        for (var layer = 0; layer < Scene.LayerCount; layer++)
        {
            var objects = _reference3DActiveLayerObjects[layer] ?? [];
            for (var objectSlot = 0; objectSlot < objects.Length; objectSlot++)
            {
                var objectIndex = objects[objectSlot];
                _reference3DObjectDepths[objectIndex] = GetReference3DObjectCenterDepth(objectIndex);
            }

            if (objects.Length <= 1)
            {
                _reference3DSortedLayerObjects[layer] = objects;
                continue;
            }

            var sorted = _reference3DSortedLayerObjects[layer] ?? [];
            if (sorted.Length != objects.Length) sorted = new int[objects.Length];
            objects.AsSpan().CopyTo(sorted);
            _reference3DObjectDepthComparison ??= CompareReference3DObjectDepth;
            Array.Sort(sorted, _reference3DObjectDepthComparison);
            _reference3DSortedLayerObjects[layer] = sorted;
        }

        _reference3DSortedLayerObjectsEpoch = _reference3DRenderPlanEpoch;
    }

    private int CompareReference3DObjectDepth(int left, int right)
    {
        var comparison = _reference3DObjectDepths[right]
            .CompareTo(_reference3DObjectDepths[left]);
        if (comparison != 0) return comparison;
        comparison = Scene.ObjectOrder[left].CompareTo(Scene.ObjectOrder[right]);
        if (comparison != 0) return comparison;
        comparison = Scene.ObjectSubOrder[left].CompareTo(Scene.ObjectSubOrder[right]);
        return comparison != 0 ? comparison : left.CompareTo(right);
    }

    internal Reference3DProjectedContour[] GetReference3DProjectedContours(int objectIndex)
    {
        if ((uint)objectIndex >= Scene.ObjectCount) return [];
        var key = (Scene, objectIndex);
        if (_reference3DProjectedContourCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var projected = ProjectReference3DContours(
            objectIndex,
            GetReference3DSourceContours(Scene, objectIndex),
            GetReference3DExtrusionOffset(objectIndex, front: true));
        _reference3DProjectedContourCache[key] = projected;
        return projected;
    }

    internal Reference3DProjectedSolid GetReference3DProjectedSolid(int objectIndex)
    {
        if ((uint)objectIndex >= Scene.ObjectCount)
        {
            return new Reference3DProjectedSolid([], [], [], [], [], [], false);
        }

        var key = (Scene, objectIndex);
        if (_reference3DProjectedSolidCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var frontContours = GetReference3DProjectedContours(objectIndex);
        var extrusion = GetReference3DExtrusionVector(objectIndex);
        var hasExtrusion = Finite(extrusion)
            && extrusion.LengthSquared() > ReferenceExtrusionEpsilon * ReferenceExtrusionEpsilon;
        var solid = !hasExtrusion
            ? new Reference3DProjectedSolid(
                frontContours,
                [],
                [],
                [],
                [],
                frontContours,
                false)
            : BuildReference3DProjectedSolid(
                objectIndex,
                frontContours,
                GetReference3DExtrusionSourceContours(objectIndex),
                extrusion);
        _reference3DProjectedSolidCache[key] = solid;
        return solid;
    }

    private Reference3DProjectedSolid BuildReference3DProjectedSolid(
        int objectIndex,
        Reference3DProjectedContour[] frontContours,
        Reference3DSourceContour[] sourceContours,
        Vector3? extrusionOverride = null)
    {
        var extrusion = extrusionOverride ?? GetReference3DExtrusionVector(objectIndex);
        var hasExtrusion = Finite(extrusion)
            && extrusion.LengthSquared() > ReferenceExtrusionEpsilon * ReferenceExtrusionEpsilon;
        if (!hasExtrusion)
        {
            return new Reference3DProjectedSolid(frontContours, [], [], [], [], frontContours, false);
        }

        if (sourceContours.Length == 0)
        {
            return new Reference3DProjectedSolid(frontContours, [], [], [], [], frontContours, false);
        }

        var frontOffset = extrusion * -0.5f;
        var backOffset = extrusion * 0.5f;
        var bodyFrontContours = ProjectReference3DContours(objectIndex, sourceContours, frontOffset);
        var backContours = ProjectReference3DContours(objectIndex, sourceContours, backOffset);
        var (sideSurfaces, sidePlanes, sidePlaneKeys, sideSurfacePoints) = BuildReference3DSideSurfaces(
            objectIndex,
            sourceContours,
            frontOffset,
            backOffset);
        var selectionEdges = BuildReference3DSelectionEdges(
            objectIndex,
            sourceContours,
            bodyFrontContours,
            backContours,
            frontOffset,
            backOffset);
        return new Reference3DProjectedSolid(
            frontContours,
            backContours,
            sideSurfaces,
            sidePlanes,
            sidePlaneKeys,
            selectionEdges,
            true)
        {
            SideSurfacePoints = sideSurfacePoints
        };
    }

    internal Reference3DRenderItem[] GetReference3DLayerRenderItems(IReadOnlyList<int> objectIndices)
    {
        var items = new List<Reference3DRenderItem>(objectIndices.Count * 4);
        var visibleLineObjects = objectIndices
            .Where(objectIndex => (uint)objectIndex < Scene.ObjectCount
                && Scene.ShapeKind[objectIndex] == ShapeKind.Line
                && Scene.IsObjectActive(objectIndex, Frame)
                && !IsObjectHiddenForRendering(Scene, objectIndex))
            .ToHashSet();
        var sharpLineComponents = BuildReference3DSharpLineRenderComponents(visibleLineObjects);
        for (var objectSlot = 0; objectSlot < objectIndices.Count; objectSlot++)
        {
            var objectIndex = objectIndices[objectSlot];
            if ((uint)objectIndex >= Scene.ObjectCount
                || IsObjectHiddenForRendering(Scene, objectIndex))
            {
                continue;
            }

            var shape = Scene.ShapeKind[objectIndex];
            var layerIndex = Scene.ObjectLayer[objectIndex];
            if (!TryGetReference3DLayerMaterialOpacity(Scene, layerIndex, out var materialOpacity)) continue;

            var hasFill = HasVisibleReference3DFill(objectIndex, shape);
            var hasStroke = HasVisibleReference3DStroke(objectIndex, shape);
            var solid = GetReference3DProjectedSolid(objectIndex);
            var extrusionSolid = solid;
            var emitExtrusion = true;
            var sharedShellOwnerObjectIndex = -1;
            int[]? sharedShellObjectIndices = null;
            if (sharpLineComponents.TryGetValue(objectIndex, out var sharpLineComponent))
            {
                sharedShellOwnerObjectIndex = sharpLineComponent[0];
                emitExtrusion = objectIndex == sharpLineComponent[0];
                if (emitExtrusion)
                {
                    extrusionSolid = BuildReference3DSharpLineComponentSolid(
                        objectIndex,
                        sharpLineComponent,
                        visibleLineObjects);
                    sharedShellObjectIndices = sharpLineComponent;
                }
            }
            var extrusion = GetReference3DExtrusionVector(objectIndex);
            var frontPlane = GetReference3DSurfacePlane(
                objectIndex,
                extrusion * -0.5f,
                reverseNormal: true);
            var frontPlaneKey = CreateReference3DSurfacePlaneKey(frontPlane);
            var backPlane = GetReference3DSurfacePlane(
                objectIndex,
                extrusion * 0.5f);
            var backPlaneKey = CreateReference3DSurfacePlaneKey(backPlane);
            var flatCenter = new PointF(Scene.X[objectIndex], Scene.Y[objectIndex]);
            if (!TryTransformFlatPointToScene(
                    objectIndex,
                    flatCenter,
                    extrusion * -0.5f,
                    out var frontSurfacePoint))
            {
                frontSurfacePoint = frontPlane.Normal * frontPlane.Distance;
            }
            if (!TryTransformFlatPointToScene(
                    objectIndex,
                    flatCenter,
                    extrusion * 0.5f,
                    out var backSurfacePoint))
            {
                backSurfacePoint = backPlane.Normal * backPlane.Distance;
            }
            if (emitExtrusion
                && extrusionSolid.HasExtrusion
                && GetReference3DExtrusionColor(objectIndex).A > 0)
            {
                var backContours = extrusionSolid.BackContours
                    .Where(contour => contour.Closed && contour.Points.Length >= 3)
                    .ToArray();
                if (backContours.Length > 0)
                {
                    items.Add(new Reference3DRenderItem(
                        objectIndex,
                        layerIndex,
                        Reference3DRenderKind.Back,
                        backContours,
                        AverageReference3DDepth(backContours, GetReference3DObjectCenterDepth(objectIndex)),
                        backPlaneKey,
                        objectSlot,
                        0)
                    {
                        Plane = backPlane,
                        SurfacePoint = backSurfacePoint,
                        HasSurfacePoint = Finite(backSurfacePoint),
                        MaterialOpacity = materialOpacity,
                        SharedShellOwnerObjectIndex = sharedShellOwnerObjectIndex,
                        SharedShellObjectIndices = sharedShellObjectIndices
                    });
                }

                for (var surfaceIndex = 0; surfaceIndex < extrusionSolid.SideSurfaces.Length; surfaceIndex++)
                {
                    var surface = extrusionSolid.SideSurfaces[surfaceIndex];
                    items.Add(new Reference3DRenderItem(
                        objectIndex,
                        layerIndex,
                        Reference3DRenderKind.Side,
                        [surface],
                        surface.AverageDepth,
                        surfaceIndex < extrusionSolid.SidePlaneKeys.Length
                            ? extrusionSolid.SidePlaneKeys[surfaceIndex]
                            : default,
                        objectSlot,
                        surfaceIndex)
                    {
                        Plane = surfaceIndex < extrusionSolid.SidePlanes.Length
                            ? extrusionSolid.SidePlanes[surfaceIndex]
                            : default,
                        SurfacePoint = surfaceIndex < extrusionSolid.SideSurfacePoints.Length
                            ? extrusionSolid.SideSurfacePoints[surfaceIndex]
                            : surfaceIndex < extrusionSolid.SidePlanes.Length
                                ? extrusionSolid.SidePlanes[surfaceIndex].Normal
                                    * extrusionSolid.SidePlanes[surfaceIndex].Distance
                                : default,
                        HasSurfacePoint = surfaceIndex < extrusionSolid.SideSurfacePoints.Length
                            || surfaceIndex < extrusionSolid.SidePlanes.Length,
                        MaterialOpacity = materialOpacity,
                        SharedShellOwnerObjectIndex = sharedShellOwnerObjectIndex,
                        SharedShellObjectIndices = sharedShellObjectIndices
                    });
                }
            }

            var frontDepth = AverageReference3DDepth(
                solid.FrontContours,
                GetReference3DObjectCenterDepth(objectIndex));
            if (hasFill)
            {
                items.Add(new Reference3DRenderItem(
                    objectIndex,
                    layerIndex,
                    Reference3DRenderKind.FrontFill,
                    solid.FrontContours,
                    frontDepth,
                    frontPlaneKey,
                    objectSlot,
                    0)
                {
                    Plane = frontPlane,
                    SurfacePoint = frontSurfacePoint,
                    HasSurfacePoint = Finite(frontSurfacePoint),
                    MaterialOpacity = materialOpacity,
                    SharedShellOwnerObjectIndex = sharedShellOwnerObjectIndex
                });
            }
            if (hasStroke)
            {
                items.Add(new Reference3DRenderItem(
                    objectIndex,
                    layerIndex,
                    Reference3DRenderKind.FrontStroke,
                    solid.FrontContours,
                    frontDepth,
                    frontPlaneKey,
                    objectSlot,
                    0)
                {
                    Plane = frontPlane,
                    SurfacePoint = frontSurfacePoint,
                    HasSurfacePoint = Finite(frontSurfacePoint),
                    OcclusionContours = hasFill
                        ? null
                        : GetReference3DProjectedStrokeOcclusionContours(
                            objectIndex,
                            solid.FrontContours),
                    MaterialOpacity = materialOpacity,
                    SharedShellOwnerObjectIndex = sharedShellOwnerObjectIndex
                });
            }
        }

        items.Sort(CompareReference3DRenderItems);
        return items.ToArray();
    }

    private bool HasVisibleReference3DFill(int objectIndex, ShapeKind shape)
    {
        if (!SceneRenderOrder.HasFill(shape)) return false;
        if (shape == ShapeKind.MixingStroke
            && Scene.TryGetMixingBrushWorldRegion(objectIndex, out var region))
        {
            return region.Vertices.Any(vertex => Color.FromArgb(vertex.Argb).A > 0);
        }
        if (Scene.HasGradient(objectIndex))
        {
            return Scene.GetGradientStops(objectIndex)
                .Any(stop => Color.FromArgb(stop.Argb).A > 0);
        }
        return Color.FromArgb(Scene.Argb[objectIndex]).A > 0;
    }

    private bool HasVisibleReference3DStroke(int objectIndex, ShapeKind shape)
    {
        if (!SceneRenderOrder.HasStroke(shape, Scene.Stroke[objectIndex])) return false;
        if (shape == ShapeKind.Line && Scene.HasGradient(objectIndex))
        {
            return Scene.GetGradientStops(objectIndex)
                .Any(stop => Color.FromArgb(stop.Argb).A > 0);
        }
        return Color.FromArgb(Scene.StrokeArgb[objectIndex]).A > 0;
    }

    private static bool TryGetReference3DLayerMaterialOpacity(
        VectorScene scene,
        int layer,
        out float materialOpacity)
    {
        materialOpacity = 1f;
        var current = layer;
        for (var visited = 0; visited < scene.LayerCount && current >= 0; visited++)
        {
            if ((uint)current >= scene.LayerCount) return false;
            var opacity = current < scene.LayerOpacity.Length
                && float.IsFinite(scene.LayerOpacity[current])
                    ? Math.Clamp(scene.LayerOpacity[current], 0f, 1f)
                    : 1f;
            if (opacity <= 0f) return false;
            var blendMode = current < scene.LayerBlendModes.Length
                && Enum.IsDefined(scene.LayerBlendModes[current])
                    ? scene.LayerBlendModes[current]
                    : LayerBlendMode.Normal;
            if (blendMode == LayerBlendMode.Normal) materialOpacity *= opacity;
            current = scene.GetLayerParentIndex(current);
        }
        if (current >= 0) return false;
        materialOpacity = Math.Clamp(materialOpacity, 0f, 1f);
        return materialOpacity > 0f;
    }

    internal Color GetReference3DExtrusionColor(int objectIndex)
    {
        if ((uint)objectIndex >= Scene.ObjectCount) return Color.Transparent;
        var shape = Scene.ShapeKind[objectIndex];
        var stroke = Color.FromArgb(Scene.StrokeArgb[objectIndex]);
        if (stroke.A > 0
            && (SceneRenderOrder.HasStroke(shape, Scene.Stroke[objectIndex])
                || shape == ShapeKind.BrushStroke))
        {
            return stroke;
        }

        var layerColor = Scene.GetEffectiveLayerOutlineColor(Scene.ObjectLayer[objectIndex]);
        if (!layerColor.IsEmpty && layerColor.A > 0) return layerColor;
        var fill = Color.FromArgb(Scene.Argb[objectIndex]);
        return fill.A > 0 ? fill : Color.Transparent;
    }

    internal Color GetReference3DExtrusionSurfaceColor(Reference3DRenderItem item)
    {
        if ((uint)item.ObjectIndex >= Scene.ObjectCount) return Color.Transparent;
        if (item.Kind == Reference3DRenderKind.Back
            && HasVisibleReference3DFill(item.ObjectIndex, Scene.ShapeKind[item.ObjectIndex]))
        {
            var fill = Color.FromArgb(Scene.Argb[item.ObjectIndex]);
            if (fill.A > 0) return fill;
        }

        return GetReference3DExtrusionColor(item.ObjectIndex);
    }

    private Reference3DProjectedContour[] ProjectReference3DContours(
        int objectIndex,
        IReadOnlyList<Reference3DSourceContour> sourceContours,
        Vector3 sceneOffset)
    {
        if (sourceContours.Count == 1
            && sourceContours[0].Closed)
        {
            var source = sourceContours[0];
            if (source.Points.Length < 2
                || !TryProjectClosedContour(
                    objectIndex,
                    source.Points,
                    sceneOffset,
                    out var singleContour))
            {
                return [];
            }
            return [singleContour];
        }

        var projected = new List<Reference3DProjectedContour>();
        foreach (var source in sourceContours)
        {
            if (source.Points.Length < 2) continue;
            if (source.Closed)
            {
                if (TryProjectClosedContour(objectIndex, source.Points, sceneOffset, out var contour)) projected.Add(contour);
                continue;
            }

            AppendProjectedOpenContour(objectIndex, source.Points, sceneOffset, projected);
        }

        return projected.ToArray();
    }

    private Reference3DSourceContour[] GetReference3DExtrusionSourceContours(int objectIndex)
    {
        var shape = Scene.ShapeKind[objectIndex];
        if (shape is ShapeKind.Line or ShapeKind.Freeform)
        {
            var strokeContours = GetReference3DStrokeOutlineSourceContours(objectIndex);
            if (strokeContours.Length > 0) return strokeContours;
        }

        return GetReference3DSourceContours(Scene, objectIndex);
    }

    internal Reference3DSourceContour[] GetReference3DStrokeOutlineSourceContours(int objectIndex)
    {
        return GetReference3DStrokeOutlineSourceContours(objectIndex, eligibleLineObjects: null);
    }

    private Reference3DSourceContour[] GetReference3DStrokeOutlineSourceContours(
        int objectIndex,
        IReadOnlySet<int>? eligibleLineObjects)
    {
        if ((uint)objectIndex >= Scene.ObjectCount || Scene.Stroke[objectIndex] <= 0) return [];
        var width = GetReference3DSourceStrokeWidth(objectIndex, Scene.Stroke[objectIndex]);
        var isLine = Scene.ShapeKind[objectIndex] == ShapeKind.Line;
        var roundStart = !isLine
            || Scene.GetLineEndpointStyle(objectIndex, startEndpoint: true) == LineEndpointStyle.Round;
        var roundEnd = !isLine
            || Scene.GetLineEndpointStyle(objectIndex, startEndpoint: false) == LineEndpointStyle.Round;
        var result = new List<Reference3DSourceContour>();
        foreach (var centerline in GetReference3DSourceContours(objectIndex))
        {
            if (centerline.Closed || centerline.Points.Length < 2) continue;
            foreach (var contour in FreehandStrokeProcessor.CreateBrushSectionOutlines(
                         centerline.Points,
                         width,
                         roundStart,
                         roundEnd))
            {
                if (contour.Length >= 3) result.Add(new Reference3DSourceContour(contour, true));
            }
        }
        if (isLine)
        {
            AppendReference3DLineSourceEndpointJoinContours(
                objectIndex,
                startEndpoint: true,
                result,
                eligibleLineObjects);
            AppendReference3DLineSourceEndpointJoinContours(
                objectIndex,
                startEndpoint: false,
                result,
                eligibleLineObjects);
        }
        return UnionReference3DSourceContours(result);
    }

    private Dictionary<int, int[]> BuildReference3DSharpLineRenderComponents(
        IReadOnlySet<int> visibleLineObjects)
    {
        if (visibleLineObjects.Count < 2) return [];
        var lineObjects = visibleLineObjects.Order().ToArray();
        var slotByObject = new Dictionary<int, int>(lineObjects.Length);
        var parents = Enumerable.Range(0, lineObjects.Length).ToArray();
        for (var slot = 0; slot < lineObjects.Length; slot++)
        {
            slotByObject.Add(lineObjects[slot], slot);
        }

        for (var slot = 0; slot < lineObjects.Length; slot++)
        {
            var objectIndex = lineObjects[slot];
            AppendConnections(startEndpoint: true);
            AppendConnections(startEndpoint: false);

            void AppendConnections(bool startEndpoint)
            {
                if (!TryGetReference3DSharpLineJunction(
                        objectIndex,
                        startEndpoint,
                        out _,
                        out _,
                        out var connections,
                        visibleLineObjects))
                {
                    return;
                }
                foreach (var connection in connections)
                {
                    if (slotByObject.TryGetValue(connection.ObjectIndex, out var connectedSlot))
                    {
                        UnionReference3DRenderComponents(parents, slot, connectedSlot);
                    }
                }
            }
        }

        var membersByRoot = new Dictionary<int, List<int>>();
        for (var slot = 0; slot < lineObjects.Length; slot++)
        {
            var root = FindReference3DRenderComponent(parents, slot);
            if (!membersByRoot.TryGetValue(root, out var members))
            {
                members = [];
                membersByRoot.Add(root, members);
            }
            members.Add(lineObjects[slot]);
        }

        var result = new Dictionary<int, int[]>();
        foreach (var members in membersByRoot.Values)
        {
            if (members.Count < 2) continue;
            var component = members.Order().ToArray();
            foreach (var objectIndex in component) result.Add(objectIndex, component);
        }
        return result;
    }

    private Reference3DProjectedSolid BuildReference3DSharpLineComponentSolid(
        int ownerObjectIndex,
        IReadOnlyList<int> component,
        IReadOnlySet<int> visibleLineObjects)
    {
        var combined = new List<Reference3DSourceContour>();
        foreach (var objectIndex in component)
        {
            combined.AddRange(GetReference3DStrokeOutlineSourceContours(
                objectIndex,
                visibleLineObjects));
        }
        return BuildReference3DProjectedSolid(
            ownerObjectIndex,
            GetReference3DProjectedContours(ownerObjectIndex),
            UnionReference3DSourceContours(combined));
    }

    private static Reference3DSourceContour[] UnionReference3DSourceContours(
        IReadOnlyList<Reference3DSourceContour> contours)
    {
        if (contours.Count == 0) return [];
        var union = UnionReference3DStrokeOcclusionContours(contours
            .Where(contour => contour.Closed && contour.Points.Length >= 3)
            .Select(contour => new Reference3DProjectedContour(contour.Points, true, 0))
            .ToArray());
        return union
            .Where(contour => contour.Closed && contour.Points.Length >= 3)
            .Select(contour => new Reference3DSourceContour(contour.Points, true))
            .ToArray();
    }

    private void AppendReference3DLineSourceEndpointJoinContours(
        int objectIndex,
        bool startEndpoint,
        ICollection<Reference3DSourceContour> destination,
        IReadOnlySet<int>? eligibleLineObjects)
    {
        if (!TryGetReference3DLineSourceEndpointJoin(
                objectIndex,
                startEndpoint,
                eligibleLineObjects,
                out var joint,
                out var miters))
        {
            return;
        }
        foreach (var miter in miters)
        {
            Add([joint, miter.OuterFirstOffset, miter.OuterMiter, miter.OuterSecondOffset]);
            Add([joint, miter.InnerFirstOffset, miter.InnerMiter, miter.InnerSecondOffset]);
        }

        void Add(PointF[] points)
        {
            var twiceArea = 0d;
            for (var index = 0; index < points.Length; index++)
            {
                var next = (index + 1) % points.Length;
                twiceArea += points[index].X * points[next].Y - points[next].X * points[index].Y;
            }
            if (Math.Abs(twiceArea) > 0.000001d)
            {
                destination.Add(new Reference3DSourceContour(points, true));
            }
        }
    }

    private Reference3DProjectedContour[] GetReference3DProjectedStrokeOcclusionContours(
        int objectIndex,
        IReadOnlyList<Reference3DProjectedContour>? projectedCenterlines = null)
    {
        var centerlines = projectedCenterlines ?? GetReference3DProjectedContours(objectIndex);
        var width = GetReference3DStrokeWidth(objectIndex, Scene.Stroke[objectIndex]);
        if (width <= 0) return [];
        var isLine = Scene.ShapeKind[objectIndex] == ShapeKind.Line;
        if (isLine && Scene.HasGradient(objectIndex))
        {
            var sourceContours = GetReference3DStrokeOutlineSourceContours(objectIndex);
            var projectedOutlines = sourceContours.Length == 0
                ? []
                : ProjectReference3DContours(
                    objectIndex,
                    sourceContours,
                    GetReference3DExtrusionOffset(objectIndex, front: true));
            var gradientResult = projectedOutlines.ToList();
            AppendReference3DLineEndpointJoinContours(
                objectIndex,
                centerlines,
                width,
                gradientResult);
            return UnionReference3DStrokeOcclusionContours(gradientResult);
        }

        var result = new List<Reference3DProjectedContour>();
        var startStyle = isLine
            ? Scene.GetLineEndpointStyle(objectIndex, startEndpoint: true)
            : LineEndpointStyle.Round;
        var endStyle = isLine
            ? Scene.GetLineEndpointStyle(objectIndex, startEndpoint: false)
            : LineEndpointStyle.Round;
        foreach (var centerline in centerlines)
        {
            if (centerline.Closed)
            {
                result.AddRange(CreateReference3DClosedStrokeOcclusionContours(centerline, width));
                continue;
            }
            if (centerline.Points.Length < 2) continue;
            result.AddRange(CreateReference3DOpenStrokeOcclusionContours(
                centerline,
                width,
                centerline.HasSourceStart && startStyle == LineEndpointStyle.Round,
                centerline.HasSourceEnd && endStyle == LineEndpointStyle.Round,
                isLine && (startStyle == LineEndpointStyle.Sharp
                    || endStyle == LineEndpointStyle.Sharp)));
        }
        if (isLine)
        {
            AppendReference3DLineEndpointJoinContours(
                objectIndex,
                centerlines,
                width,
                result);
            return UnionReference3DStrokeOcclusionContours(result);
        }
        return result.ToArray();
    }

    private (
        Reference3DProjectedContour[] Surfaces,
        Reference3DSurfacePlane[] Planes,
        Reference3DSurfacePlaneKey[] PlaneKeys,
        Vector3[] SurfacePoints)
        BuildReference3DSideSurfaces(
        int objectIndex,
        IReadOnlyList<Reference3DSourceContour> sourceContours,
        Vector3 frontOffset,
        Vector3 backOffset)
    {
        var surfaces = new List<Reference3DProjectedContour>();
        var planes = new List<Reference3DSurfacePlane>();
        var planeKeys = new List<Reference3DSurfacePlaneKey>();
        var surfacePoints = new List<Vector3>();
        foreach (var source in sourceContours)
        {
            var count = EffectiveReference3DPointCount(source.Points, source.Closed);
            if (count < 2) continue;
            var segmentCount = source.Closed ? count : count - 1;
            for (var segment = 0; segment < segmentCount; segment++)
            {
                var next = (segment + 1) % count;
                if (!TryTransformFlatPointToCamera(objectIndex, source.Points[segment], frontOffset, out var frontStart)
                    || !TryTransformFlatPointToCamera(objectIndex, source.Points[next], frontOffset, out var frontEnd)
                    || !TryTransformFlatPointToCamera(objectIndex, source.Points[next], backOffset, out var backEnd)
                    || !TryTransformFlatPointToCamera(objectIndex, source.Points[segment], backOffset, out var backStart))
                {
                    continue;
                }

                if (TryProjectCameraPolygon(
                        [frontStart, frontEnd, backEnd, backStart],
                        out var surface))
                {
                    surfaces.Add(surface);
                    var plane = GetReference3DSideSurfacePlane(
                        objectIndex,
                        source.Points[segment],
                        source.Points[next],
                        frontOffset,
                        backOffset);
                    planes.Add(plane);
                    planeKeys.Add(CreateReference3DSurfacePlaneKey(plane));
                    if (TryTransformFlatPointToScene(
                            objectIndex,
                            source.Points[segment],
                            frontOffset,
                            out var sceneFrontStart)
                        && TryTransformFlatPointToScene(
                            objectIndex,
                            source.Points[next],
                            frontOffset,
                            out var sceneFrontEnd)
                        && TryTransformFlatPointToScene(
                            objectIndex,
                            source.Points[next],
                            backOffset,
                            out var sceneBackEnd)
                        && TryTransformFlatPointToScene(
                            objectIndex,
                            source.Points[segment],
                            backOffset,
                            out var sceneBackStart))
                    {
                        surfacePoints.Add(
                            (sceneFrontStart + sceneFrontEnd + sceneBackEnd + sceneBackStart) * 0.25f);
                    }
                    else
                    {
                        surfacePoints.Add(plane.Normal * plane.Distance);
                    }
                }
            }
        }
        return (
            surfaces.ToArray(),
            planes.ToArray(),
            planeKeys.ToArray(),
            surfacePoints.ToArray());
    }

    private Reference3DProjectedContour[] BuildReference3DSelectionEdges(
        int objectIndex,
        IReadOnlyList<Reference3DSourceContour> sourceContours,
        IReadOnlyList<Reference3DProjectedContour> frontContours,
        IReadOnlyList<Reference3DProjectedContour> backContours,
        Vector3 frontOffset,
        Vector3 backOffset)
    {
        var edges = new List<Reference3DProjectedContour>(frontContours.Count + backContours.Count + 8);
        edges.AddRange(frontContours);
        edges.AddRange(backContours);
        foreach (var source in sourceContours)
        {
            var count = EffectiveReference3DPointCount(source.Points, source.Closed);
            if (!source.Closed || count < 3) continue;
            for (var pointIndex = 0; pointIndex < count; pointIndex++)
            {
                if (!IsReference3DExtrusionCorner(source.Points, count, pointIndex)
                    || !TryProjectFlatPointWithOffset(
                        objectIndex,
                        source.Points[pointIndex],
                        frontOffset,
                        out var front,
                        out var frontDepth)
                    || !TryProjectFlatPointWithOffset(
                        objectIndex,
                        source.Points[pointIndex],
                        backOffset,
                        out var back,
                        out var backDepth))
                {
                    continue;
                }
                edges.Add(new Reference3DProjectedContour(
                    [front, back],
                    false,
                    (frontDepth + backDepth) * 0.5f));
            }
        }
        return edges.ToArray();
    }

    internal Reference3DRenderItem[] GetReference3DSceneRenderItems()
    {
        return GetReference3DRenderPlan(substituteOutlineItems: true, layers: null);
    }

    private Reference3DRenderItem[] GetReference3DDrawingItems()
    {
        return GetReference3DRenderPlan(substituteOutlineItems: false, layers: null);
    }

    private Reference3DRenderItem[] GetReference3DRenderPlan(
        bool substituteOutlineItems,
        IReadOnlyList<int>? layers)
    {
        if (substituteOutlineItems && !HasReference3DOutlineSubstitution())
        {
            substituteOutlineItems = false;
        }
        var key = CreateReference3DRenderPlanCacheKey(substituteOutlineItems);
        foreach (var entry in _reference3DRenderPlanCache)
        {
            if (entry.Matches(key, layers)) return entry.Items;
        }

        var itemBuildStarted = Stopwatch.GetTimestamp();
        var items = layers is null
            ? Scene.HasNonNormalLayerBlendModes
                ? BuildReference3DCompositedRenderItems(substituteOutlineItems)
                : BuildReference3DSceneRenderItems(substituteOutlineItems)
            : BuildReference3DCompositeLayerRenderItems(layers, substituteOutlineItems);
        LastReference3DLayerItemsMilliseconds =
            Stopwatch.GetElapsedTime(itemBuildStarted).TotalMilliseconds;
        var opticsStarted = Stopwatch.GetTimestamp();
        items = ApplyReference3DOptics(items);
        LastReference3DOpticsMilliseconds =
            Stopwatch.GetElapsedTime(opticsStarted).TotalMilliseconds;
        Reference3DRenderPlanBuildCount++;
        var cacheEntry = new Reference3DRenderPlanCacheEntry(
            key,
            layers?.ToArray(),
            items);
        while (_reference3DRenderPlanCache.Count >= MaximumReference3DRenderPlanCacheEntries
               || _reference3DRenderPlanCache.Count > 0
               && _reference3DRenderPlanSurfaceBytes + cacheEntry.OpticalSurfaceBytes
                   > MaximumReference3DOpticalSurfaceCacheBytes)
        {
            _direct2DRenderer.InvalidateReference3DOpticalSurfaceBitmapCache();
            _reference3DRenderPlanSurfaceBytes -=
                _reference3DRenderPlanCache[0].OpticalSurfaceBytes;
            _reference3DRenderPlanCache[0].Dispose();
            _reference3DRenderPlanCache.RemoveAt(0);
        }
        _reference3DRenderPlanCache.Add(cacheEntry);
        _reference3DRenderPlanSurfaceBytes += cacheEntry.OpticalSurfaceBytes;
        return items;
    }

    private bool HasReference3DOutlineSubstitution()
    {
        for (var layer = 0; layer < Scene.LayerCount; layer++)
        {
            if (Scene.ShouldRenderLayerContent(layer)
                && IsReference3DLayerEffectivelyOutlined(layer)
                && !Scene.GetLayerColor(layer).IsEmpty)
            {
                return true;
            }
        }
        return false;
    }

    private bool IsReference3DLayerEffectivelyOutlined(int layer)
    {
        var current = layer;
        for (var visited = 0; visited < Scene.LayerCount && current >= 0; visited++)
        {
            if ((uint)current >= Scene.LayerCount) return false;
            if (current < Scene.LayerOutline.Length && Scene.LayerOutline[current]) return true;
            current = Scene.GetLayerParentIndex(current);
        }
        return false;
    }

    private Reference3DRenderPlanCacheKey CreateReference3DRenderPlanCacheKey(
        bool substituteOutlineItems)
    {
        var layerRenderState = GetReference3DLayerRenderState(Scene, Frame);
        var opticsContentHash = GetReference3DOpticsContentHash();
        return new Reference3DRenderPlanCacheKey(
            Scene,
            Scene.GeometryRevision,
            Scene.SummaryRevision,
            Scene.ObjectCount,
            Scene.LayerCount,
            Frame,
            Scene.EditFrame,
            Width,
            Height,
            ReferenceDimension,
            _reference2DViewDirection,
            ReferenceProjection,
            ReferenceProjectionBlend,
            EffectiveReferenceYaw,
            EffectiveReferencePitch,
            _referenceDistance,
            _referenceZoomScale,
            _referenceTargetX,
            _referenceTargetY,
            _referenceTargetZ,
            _sceneCompositionResult,
            _sceneCompositionResultScene,
            _sceneCompositionHasSpatialPoses,
            layerRenderState,
            opticsContentHash,
            Reference3DOpticalRasterStartLod,
            _reference3DRenderPlanEpoch,
            substituteOutlineItems);
    }

    private static ulong GetReference3DLayerRenderState(VectorScene scene, int frame)
    {
        const ulong offset = 14695981039346656037UL;
        var hash = offset;

        AddReference3DHashInt(ref hash, scene.LayerCount);
        for (var layer = 0; layer < scene.LayerCount; layer++)
        {
            AddReference3DHashInt(ref hash,
                layer < scene.LayerVisible.Length && scene.LayerVisible[layer] ? 1 : 0);
            AddReference3DHashInt(ref hash,
                layer < scene.LayerLocked.Length && scene.LayerLocked[layer] ? 1 : 0);
            AddReference3DHashInt(ref hash, layer < scene.LayerOpacity.Length
                ? BitConverter.SingleToInt32Bits(scene.LayerOpacity[layer])
                : 0);
            AddReference3DHashInt(ref hash,
                layer < scene.LayerBlendModes.Length ? (int)scene.LayerBlendModes[layer] : 0);
            AddReference3DHashInt(ref hash,
                layer < scene.LayerColorArgb.Length ? scene.LayerColorArgb[layer] : 0);
            AddReference3DHashInt(ref hash,
                layer < scene.LayerOutline.Length && scene.LayerOutline[layer] ? 1 : 0);
            AddReference3DHashInt(ref hash,
                layer < scene.LayerKinds.Length ? (int)scene.LayerKinds[layer] : 0);
            AddReference3DHashString(ref hash, layer < scene.LayerIds.Length ? scene.LayerIds[layer] : null);
            AddReference3DHashString(ref hash, layer < scene.LayerNames.Length ? scene.LayerNames[layer] : null);
            AddReference3DHashString(ref hash, layer < scene.LayerParentIds.Length ? scene.LayerParentIds[layer] : null);
            AddReference3DHashString(ref hash, layer < scene.LayerMaskIds.Length ? scene.LayerMaskIds[layer] : null);
            var exposure = layer < scene.LayerIds.Length
                ? scene.Timeline.EvaluateTargetExposure(scene.LayerIds[layer], frame)
                : TimelineExposure.None(frame);
            AddReference3DHashInt(ref hash, exposure.HasContent ? 1 : 0);
            AddReference3DHashInt(ref hash, exposure.SourceKeyframeFrame);
        }
        return hash;
    }

    internal static ulong GetReference3DWorkspaceFrameLayerRenderState(
        VectorScene scene,
        int frame)
    {
        const ulong offset = 14695981039346656037UL;
        var hash = offset;

        AddReference3DHashInt(ref hash, scene.LayerCount);
        for (var layer = 0; layer < scene.LayerCount; layer++)
        {
            AddReference3DHashInt(ref hash,
                layer < scene.LayerVisible.Length && scene.LayerVisible[layer] ? 1 : 0);
            AddReference3DHashInt(ref hash,
                layer < scene.LayerLocked.Length && scene.LayerLocked[layer] ? 1 : 0);
            AddReference3DHashInt(ref hash, layer < scene.LayerOpacity.Length
                ? BitConverter.SingleToInt32Bits(scene.LayerOpacity[layer])
                : 0);
            AddReference3DHashInt(ref hash,
                layer < scene.LayerBlendModes.Length ? (int)scene.LayerBlendModes[layer] : 0);
            AddReference3DHashInt(ref hash,
                layer < scene.LayerColorArgb.Length ? scene.LayerColorArgb[layer] : 0);
            AddReference3DHashInt(ref hash,
                layer < scene.LayerOutline.Length && scene.LayerOutline[layer] ? 1 : 0);
            AddReference3DHashInt(ref hash,
                layer < scene.LayerKinds.Length ? (int)scene.LayerKinds[layer] : 0);
            AddReference3DHashInt(ref hash, scene.GetLayerParentIndex(layer));
            AddReference3DHashInt(ref hash,
                scene.TryGetMaskLayerIndex(layer, out var maskLayer) ? maskLayer : -1);
            var exposure = layer < scene.LayerIds.Length
                ? scene.Timeline.EvaluateTargetExposure(scene.LayerIds[layer], frame)
                : TimelineExposure.None(frame);
            AddReference3DHashInt(ref hash, exposure.HasContent ? 1 : 0);
            AddReference3DHashInt(ref hash, exposure.SourceKeyframeFrame);
        }

        return hash;
    }

    private static void AddReference3DHashInt(ref ulong hash, int value)
    {
        unchecked
        {
            hash ^= (uint)value;
            hash *= 1099511628211UL;
            hash ^= (uint)(value >> 16);
            hash *= 1099511628211UL;
        }
    }

    private static void AddReference3DHashString(ref ulong hash, string? value)
    {
        AddReference3DHashInt(ref hash, value?.Length ?? -1);
        if (value is null) return;
        foreach (var character in value) AddReference3DHashInt(ref hash, character);
    }

    internal Reference3DBaseFrameRenderState CreateReference3DBaseFrameRenderState()
    {
        return new Reference3DBaseFrameRenderState(
            BasePresentationRevision,
            Frame,
            CreateSceneState(Scene),
            CreateSceneState(UnderlayScene),
            CreateSceneState(OnionSkinScene),
            _reference3DRenderPlanEpoch,
            GetReference3DOpticsContentHash());

        Reference3DBaseFrameSceneState CreateSceneState(VectorScene? scene)
        {
            return scene is null
                ? new Reference3DBaseFrameSceneState(null, -1, -1, -1, 0)
                : new Reference3DBaseFrameSceneState(
                    scene,
                    scene.GeometryRevision,
                    scene.SummaryRevision,
                    scene.EditFrame,
                    GetReference3DLayerRenderState(scene, Frame));
        }
    }

    private Reference3DRenderItem[] BuildReference3DSceneRenderItems(bool substituteOutlineItems)
    {
        var layers = Enumerable
            .Range(0, Scene.LayerCount)
            .Reverse()
            .ToArray();
        var result = new List<Reference3DRenderItem>(Scene.ObjectCount * 4);
        result.AddRange(BuildReference3DLayerRenderItemsParallel(layers, substituteOutlineItems));

        var pathCache = new Reference3DIntersectionPathCache();
        var intersectionStarted = Stopwatch.GetTimestamp();
        var intersectionItems = CanSkipReference3DIntersectionBuild(result)
            ? result.ToArray()
            : BuildReference3DIntersectionRenderItems(result, pathCache);
        LastReference3DIntersectionMilliseconds =
            Stopwatch.GetElapsedTime(intersectionStarted).TotalMilliseconds;
        var sortStarted = Stopwatch.GetTimestamp();
        var sorted = SortReference3DSceneRenderItems(intersectionItems, pathCache);
        LastReference3DSortMilliseconds =
            Stopwatch.GetElapsedTime(sortStarted).TotalMilliseconds;
        return sorted;
    }

    internal Reference3DRenderItem[] GetReference3DCompositeLayerRenderItems(
        IReadOnlyList<int> layers,
        bool substituteOutlineItems = true)
    {
        return GetReference3DRenderPlan(substituteOutlineItems, layers);
    }

    private Reference3DRenderItem[] BuildReference3DCompositeLayerRenderItems(
        IReadOnlyList<int> layers,
        bool substituteOutlineItems)
    {
        var result = new List<Reference3DRenderItem>();
        result.AddRange(BuildReference3DLayerRenderItemsParallel(layers, substituteOutlineItems));
        var pathCache = new Reference3DIntersectionPathCache();
        var intersectionStarted = Stopwatch.GetTimestamp();
        var intersectionItems = CanSkipReference3DIntersectionBuild(result)
            ? result.ToArray()
            : BuildReference3DIntersectionRenderItems(result, pathCache);
        LastReference3DIntersectionMilliseconds =
            Stopwatch.GetElapsedTime(intersectionStarted).TotalMilliseconds;
        var sortStarted = Stopwatch.GetTimestamp();
        var sorted = SortReference3DSceneRenderItems(intersectionItems, pathCache);
        LastReference3DSortMilliseconds =
            Stopwatch.GetElapsedTime(sortStarted).TotalMilliseconds;
        return sorted;
    }

    private static bool CanSkipReference3DIntersectionBuild(
        IReadOnlyList<Reference3DRenderItem> items)
    {
        if (items.Count < 2) return true;

        var planeKey = default(Reference3DSurfacePlaneKey);
        var hasPlaneKey = false;
        foreach (var item in items)
        {
            if (item.Kind is not (Reference3DRenderKind.FrontFill
                or Reference3DRenderKind.FrontStroke)
                || !item.PlaneKey.IsValid
                || item.FragmentClip is { Length: > 0 }
                || item.SecondaryObjectIndex >= 0)
            {
                return false;
            }

            if (!hasPlaneKey)
            {
                planeKey = item.PlaneKey;
                hasPlaneKey = true;
            }
            else if (item.PlaneKey != planeKey)
            {
                return false;
            }
        }

        return hasPlaneKey;
    }

    private Reference3DRenderItem[] BuildReference3DCompositedRenderItems(bool substituteOutlineItems)
    {
        var objectsByLayer = new Dictionary<int, int[]>();
        bool ShouldDrawLayer(int layer)
        {
            if ((uint)layer >= Scene.LayerCount || !Scene.ShouldRenderLayerContent(layer)) return false;
            if (!objectsByLayer.TryGetValue(layer, out var objects))
            {
                objects = GetReference3DLayerObjects(layer);
                objectsByLayer.Add(layer, objects);
            }
            return objects.Length > 0;
        }

        var result = new List<Reference3DRenderItem>();
        foreach (var batch in LayerBlendCompositor.GetSpatialLayerBatches(Scene, ShouldDrawLayer))
        {
            result.AddRange(BuildReference3DCompositeLayerRenderItems(batch, substituteOutlineItems));
        }
        return result.ToArray();
    }

    private void AppendReference3DOutlineItems(
        ICollection<Reference3DRenderItem> destination,
        int layer,
        IReadOnlyList<int> objectIndices)
    {
        if (!TryGetReference3DLayerMaterialOpacity(Scene, layer, out var materialOpacity)) return;
        for (var objectSlot = 0; objectSlot < objectIndices.Count; objectSlot++)
        {
            var objectIndex = objectIndices[objectSlot];
            if ((uint)objectIndex >= Scene.ObjectCount
                || IsObjectHiddenForRendering(Scene, objectIndex))
            {
                continue;
            }

            var solid = GetReference3DProjectedSolid(objectIndex);
            var planeKey = solid.HasExtrusion
                ? default
                : GetReference3DSurfacePlaneKey(objectIndex, Vector3.Zero);
            destination.Add(new Reference3DRenderItem(
                objectIndex,
                layer,
                Reference3DRenderKind.Outline,
                solid.SelectionEdges,
                AverageReference3DDepth(
                    solid.SelectionEdges,
                    GetReference3DObjectCenterDepth(objectIndex)),
                planeKey,
                objectSlot,
                0)
            {
                MaterialOpacity = materialOpacity
            });
        }
    }

    private static int CompareReference3DRenderItems(
        Reference3DRenderItem left,
        Reference3DRenderItem right)
    {
        var comparison = right.AverageDepth.CompareTo(left.AverageDepth);
        if (comparison != 0) return comparison;
        comparison = Reference3DRenderPriority(left.Kind).CompareTo(Reference3DRenderPriority(right.Kind));
        if (comparison != 0) return comparison;
        comparison = left.ObjectSlot.CompareTo(right.ObjectSlot);
        return comparison != 0 ? comparison : left.SurfaceSlot.CompareTo(right.SurfaceSlot);
    }

    private Reference3DRenderItem[] SortReference3DSceneRenderItems(
        IReadOnlyList<Reference3DRenderItem> items,
        Reference3DIntersectionPathCache pathCache)
    {
        if (items.Count <= 1) return items.ToArray();

        // A set of front-only surfaces on one quantized plane has no depth
        // ordering problem: painter order is the established layer/object
        // order, while contour-overlap grouping only adds work. Keep the
        // precise path for perspective, extrusion, clips, and edge items.
        if (CanUseReference3DCoplanarFrontOrder(items))
        {
            var ordered = items.ToArray();
            Array.Sort(ordered, CompareReference3DCoplanarItems);
            return ordered;
        }

        var bounds = new RectangleF[items.Count];
        var hasBounds = new bool[items.Count];
        var overlapRadii = new float[items.Count];
        var projectedBoundsCache = new Dictionary<Reference3DProjectedContour[], RectangleF>();
        var planeBuckets = new Dictionary<Reference3DSurfacePlaneKey, List<int>>();
        var groups = new List<Reference3DRenderGroup>(items.Count);
        for (var itemIndex = 0; itemIndex < items.Count; itemIndex++)
        {
            var item = items[itemIndex];
            overlapRadii[itemIndex] = GetReference3DRenderItemOverlapRadius(item);
            hasBounds[itemIndex] = TryGetReference3DProjectedBoundsCached(
                    item.FragmentClip ?? item.OcclusionContours ?? item.Contours,
                    overlapRadii[itemIndex],
                    projectedBoundsCache,
                    out bounds[itemIndex]);
            if (!item.PlaneKey.IsValid)
            {
                groups.Add(CreateReference3DRenderGroup(
                    [item],
                    default,
                    item.Kind == Reference3DRenderKind.IntersectionEdge
                        ? item.SurfaceSlot
                        : itemIndex));
                continue;
            }

            if (!planeBuckets.TryGetValue(item.PlaneKey, out var bucket))
            {
                bucket = [];
                planeBuckets.Add(item.PlaneKey, bucket);
            }
            bucket.Add(itemIndex);
        }

        // Coplanar overlap components are indivisible sorting units, which keeps the group comparator transitive.
        foreach (var pair in planeBuckets)
        {
            if (pair.Value.Count == 1)
            {
                var itemIndex = pair.Value[0];
                groups.Add(CreateReference3DRenderGroup(
                    [items[itemIndex]],
                    pair.Key,
                    itemIndex));
                continue;
            }

            AppendReference3DPlaneGroups(
                groups,
                items,
                pair.Key,
                pair.Value,
                bounds,
                hasBounds,
                overlapRadii,
                pathCache);
        }

        var requiresExtrusionDepthConstraints = items.Any(item =>
                item.Kind is Reference3DRenderKind.Back or Reference3DRenderKind.Side)
            && items.Any(item => item.ObjectIndex != items[0].ObjectIndex);
        if (requiresExtrusionDepthConstraints
            || items.Any(item => item.FragmentClip is { Length: > 0 }
                || item.Kind == Reference3DRenderKind.IntersectionEdge))
        {
            return SortReference3DIntersectingRenderGroups(groups, pathCache);
        }

        groups.Sort(CompareReference3DRenderGroups);
        var result = new List<Reference3DRenderItem>(items.Count);
        foreach (var group in groups)
        {
            var groupItems = group.Items;
            if (group.PlaneKey.IsValid)
            {
                Array.Sort(groupItems, CompareReference3DCoplanarItems);
            }
            result.AddRange(groupItems);
        }
        return result.ToArray();
    }

    private static bool CanUseReference3DCoplanarFrontOrder(
        IReadOnlyList<Reference3DRenderItem> items)
    {
        if (items.Count < 2) return false;

        var planeKey = default(Reference3DSurfacePlaneKey);
        var hasPlane = false;
        foreach (var item in items)
        {
            if (item.Kind is not (Reference3DRenderKind.FrontFill
                or Reference3DRenderKind.FrontStroke)
                || !item.PlaneKey.IsValid
                || item.FragmentClip is { Length: > 0 }
                || item.OcclusionContours is { Length: > 0 }
                || item.SecondaryObjectIndex >= 0)
            {
                return false;
            }

            if (!hasPlane)
            {
                planeKey = item.PlaneKey;
                hasPlane = true;
            }
            else if (item.PlaneKey != planeKey)
            {
                return false;
            }
        }

        return hasPlane;
    }

    private void AppendReference3DPlaneGroups(
        ICollection<Reference3DRenderGroup> destination,
        IReadOnlyList<Reference3DRenderItem> items,
        Reference3DSurfacePlaneKey planeKey,
        IReadOnlyList<int> bucket,
        IReadOnlyList<RectangleF> bounds,
        IReadOnlyList<bool> hasBounds,
        float[] overlapRadii,
        Reference3DIntersectionPathCache pathCache)
    {
        var parents = Enumerable.Range(0, bucket.Count).ToArray();
        var firstItemBySurface = new Dictionary<
            (
                int ObjectIndex,
                int SurfaceKind,
                int SurfaceSlot,
                ulong StableFragmentIdentity,
                int FragmentSlot),
            int>();
        for (var localIndex = 0; localIndex < bucket.Count; localIndex++)
        {
            var item = items[bucket[localIndex]];
            if (item.Kind is not (Reference3DRenderKind.FrontFill or Reference3DRenderKind.FrontStroke))
            {
                continue;
            }
            var surface = (
                item.ObjectIndex,
                Reference3DNormalizedSurfaceKind(item.Kind),
                item.SurfaceSlot,
                item.StableFragmentIdentity,
                item.FragmentSlot);
            if (firstItemBySurface.TryGetValue(surface, out var firstSurfaceItem))
            {
                UnionReference3DRenderComponents(parents, localIndex, firstSurfaceItem);
            }
            else
            {
                firstItemBySurface.Add(surface, localIndex);
            }
        }

        // Sweep projected bounds before the exact contour test so a large flat scene avoids all-pairs geometry work.
        var filledSurfaceKeys = new HashSet<(
            int ObjectIndex,
            int SurfaceKind,
            int SurfaceSlot,
            ulong StableFragmentIdentity,
            int FragmentSlot)>();
        var strokeSurfaceRadii = new Dictionary<(
            int ObjectIndex,
            int SurfaceKind,
            int SurfaceSlot,
            ulong StableFragmentIdentity,
            int FragmentSlot), float>();
        for (var localIndex = 0; localIndex < bucket.Count; localIndex++)
        {
            var item = items[bucket[localIndex]];
            var surfaceKey = (
                item.ObjectIndex,
                Reference3DNormalizedSurfaceKind(item.Kind),
                item.SurfaceSlot,
                item.StableFragmentIdentity,
                item.FragmentSlot);
            if (item.Kind == Reference3DRenderKind.FrontFill)
            {
                filledSurfaceKeys.Add(surfaceKey);
            }
            else if (item.Kind == Reference3DRenderKind.FrontStroke)
            {
                var radius = overlapRadii[bucket[localIndex]];
                if (!strokeSurfaceRadii.TryGetValue(surfaceKey, out var current)
                    || radius > current)
                {
                    strokeSurfaceRadii[surfaceKey] = radius;
                }
            }
        }
        for (var localIndex = 0; localIndex < bucket.Count; localIndex++)
        {
            var globalIndex = bucket[localIndex];
            var item = items[globalIndex];
            if (item.Kind != Reference3DRenderKind.FrontFill) continue;
            var surfaceKey = (
                item.ObjectIndex,
                Reference3DNormalizedSurfaceKind(item.Kind),
                item.SurfaceSlot,
                item.StableFragmentIdentity,
                item.FragmentSlot);
            if (strokeSurfaceRadii.TryGetValue(surfaceKey, out var strokeRadius))
            {
                overlapRadii[globalIndex] = Math.Max(
                    overlapRadii[globalIndex],
                    strokeRadius);
            }
        }

        var boundedItems = Enumerable.Range(0, bucket.Count)
            .Where(localIndex =>
            {
                var item = items[bucket[localIndex]];
                if (item.Kind != Reference3DRenderKind.FrontStroke)
                {
                    return hasBounds[bucket[localIndex]];
                }

                var surfaceKey = (
                    item.ObjectIndex,
                    Reference3DNormalizedSurfaceKind(item.Kind),
                    item.SurfaceSlot,
                    item.StableFragmentIdentity,
                    item.FragmentSlot);
                return !filledSurfaceKeys.Contains(surfaceKey)
                    && hasBounds[bucket[localIndex]];
            })
            .OrderBy(localIndex => bounds[bucket[localIndex]].Left)
            .ThenBy(localIndex => bucket[localIndex])
            .ToArray();
        var hasFragmentClips = boundedItems.Any(localIndex =>
            items[bucket[localIndex]].FragmentClip is { Length: > 0 });
        var canParallelizeOverlapTests = !hasFragmentClips && boundedItems.Length >= 64;
        if (canParallelizeOverlapTests)
        {
            var workers = ParallelBatch.WorkerCount(boundedItems.Length, 24);
            var overlapPairs = new List<(int Left, int Right)>[workers];
            ParallelBatch.For(
                boundedItems.Length,
                24,
                (worker, start, end) =>
                {
                    var pairs = new List<(int Left, int Right)>();
                    for (var orderIndex = start; orderIndex < end; orderIndex++)
                    {
                        var leftLocal = boundedItems[orderIndex];
                        var leftIndex = bucket[leftLocal];
                        var leftBounds = bounds[leftIndex];
                        for (var candidateOrder = orderIndex + 1;
                             candidateOrder < boundedItems.Length;
                             candidateOrder++)
                        {
                            var rightLocal = boundedItems[candidateOrder];
                            var rightIndex = bucket[rightLocal];
                            var rightBounds = bounds[rightIndex];
                            if (rightBounds.Left
                                > leftBounds.Right + ReferenceSurfaceOverlapTolerancePixels)
                            {
                                break;
                            }
                            if (!Reference3DProjectedBoundsOverlap(leftBounds, rightBounds)
                                || !Reference3DRenderItemsOverlap(
                                    items[leftIndex],
                                    overlapRadii[leftIndex],
                                    items[rightIndex],
                                    overlapRadii[rightIndex]))
                            {
                                continue;
                            }
                            pairs.Add((leftLocal, rightLocal));
                        }
                    }
                    overlapPairs[worker] = pairs;
                });

            // Apply unions in worker and sweep order so the resulting
            // component representatives remain deterministic.
            for (var worker = 0; worker < overlapPairs.Length; worker++)
            {
                foreach (var (leftLocal, rightLocal) in overlapPairs[worker])
                {
                    UnionReference3DRenderComponents(parents, leftLocal, rightLocal);
                }
            }
        }
        else
        {
            for (var orderIndex = 0; orderIndex < boundedItems.Length; orderIndex++)
            {
                var leftLocal = boundedItems[orderIndex];
                var leftIndex = bucket[leftLocal];
                var leftBounds = bounds[leftIndex];
                for (var candidateOrder = orderIndex + 1;
                     candidateOrder < boundedItems.Length;
                     candidateOrder++)
                {
                    var rightLocal = boundedItems[candidateOrder];
                    var rightIndex = bucket[rightLocal];
                    var rightBounds = bounds[rightIndex];
                    if (rightBounds.Left
                        > leftBounds.Right + ReferenceSurfaceOverlapTolerancePixels)
                    {
                        break;
                    }
                    if (FindReference3DRenderComponent(parents, leftLocal)
                            == FindReference3DRenderComponent(parents, rightLocal)
                        || !Reference3DProjectedBoundsOverlap(leftBounds, rightBounds)
                        || !Reference3DFragmentClipsOverlap(
                            items[leftIndex],
                            items[rightIndex],
                            pathCache)
                        || !Reference3DRenderItemsOverlap(
                            items[leftIndex],
                            overlapRadii[leftIndex],
                            items[rightIndex],
                            overlapRadii[rightIndex]))
                    {
                        continue;
                    }
                    UnionReference3DRenderComponents(parents, leftLocal, rightLocal);
                }
            }
        }

        var components = new Dictionary<int, List<int>>();
        for (var localIndex = 0; localIndex < bucket.Count; localIndex++)
        {
            var root = FindReference3DRenderComponent(parents, localIndex);
            if (!components.TryGetValue(root, out var component))
            {
                component = [];
                components.Add(root, component);
            }
            component.Add(bucket[localIndex]);
        }

        foreach (var component in components.Values)
        {
            component.Sort();
            destination.Add(CreateReference3DRenderGroup(
                component.Select(index => items[index]).ToArray(),
                planeKey,
                component[0]));
        }
    }

    private static Reference3DRenderGroup CreateReference3DRenderGroup(
        Reference3DRenderItem[] items,
        Reference3DSurfacePlaneKey planeKey,
        int stableSlot)
    {
        if (items.Length == 1)
        {
            var depth = float.IsFinite(items[0].AverageDepth)
                ? items[0].AverageDepth
                : float.NegativeInfinity;
            return new Reference3DRenderGroup(items, depth, planeKey, stableSlot);
        }

        var surfaceDepths = new Dictionary<(int ObjectIndex, int SurfaceKind, int SurfaceSlot), float>();
        foreach (var item in items)
        {
            if (!float.IsFinite(item.AverageDepth)) continue;
            var surfaceKind = Reference3DNormalizedSurfaceKind(item.Kind);
            surfaceDepths.TryAdd((item.ObjectIndex, surfaceKind, item.SurfaceSlot), item.AverageDepth);
        }

        var samples = surfaceDepths.Values.Order().ToArray();
        double total = 0;
        foreach (var sample in samples) total += sample;
        var averageDepth = samples.Length > 0
            ? (float)(total / samples.Length)
            : float.NegativeInfinity;
        return new Reference3DRenderGroup(items, averageDepth, planeKey, stableSlot);
    }

    private static int CompareReference3DRenderGroups(
        Reference3DRenderGroup left,
        Reference3DRenderGroup right)
    {
        var comparison = right.AverageDepth.CompareTo(left.AverageDepth);
        if (comparison != 0) return comparison;
        comparison = CompareReference3DSurfacePlaneKeys(left.PlaneKey, right.PlaneKey);
        if (comparison != 0) return comparison;
        var leftHasEdge = TryGetReference3DGroupEdgeItem(left, out var leftEdge);
        var rightHasEdge = TryGetReference3DGroupEdgeItem(right, out var rightEdge);
        if (leftHasEdge || rightHasEdge)
        {
            comparison = leftHasEdge.CompareTo(rightHasEdge);
            if (comparison != 0) return comparison;
            comparison = Math.Min(leftEdge.ObjectIndex, leftEdge.SecondaryObjectIndex)
                .CompareTo(Math.Min(rightEdge.ObjectIndex, rightEdge.SecondaryObjectIndex));
            if (comparison != 0) return comparison;
            comparison = Math.Max(leftEdge.ObjectIndex, leftEdge.SecondaryObjectIndex)
                .CompareTo(Math.Max(rightEdge.ObjectIndex, rightEdge.SecondaryObjectIndex));
            if (comparison != 0) return comparison;
            comparison = leftEdge.FragmentSlot.CompareTo(rightEdge.FragmentSlot);
            if (comparison != 0) return comparison;
        }
        return left.StableSlot.CompareTo(right.StableSlot);
    }

    private static int CompareReference3DRenderGroupStableIdentity(
        Reference3DRenderGroup left,
        Reference3DRenderGroup right)
    {
        var leftHasEdge = TryGetReference3DGroupEdgeItem(left, out var leftEdge);
        var rightHasEdge = TryGetReference3DGroupEdgeItem(right, out var rightEdge);
        var comparison = leftHasEdge.CompareTo(rightHasEdge);
        if (comparison != 0) return comparison;
        if (leftHasEdge)
        {
            comparison = Math.Min(leftEdge.ObjectIndex, leftEdge.SecondaryObjectIndex)
                .CompareTo(Math.Min(rightEdge.ObjectIndex, rightEdge.SecondaryObjectIndex));
            if (comparison != 0) return comparison;
            comparison = Math.Max(leftEdge.ObjectIndex, leftEdge.SecondaryObjectIndex)
                .CompareTo(Math.Max(rightEdge.ObjectIndex, rightEdge.SecondaryObjectIndex));
            if (comparison != 0) return comparison;
            comparison = leftEdge.SurfaceSlot.CompareTo(rightEdge.SurfaceSlot);
            if (comparison != 0) return comparison;
            comparison = leftEdge.FragmentSlot.CompareTo(rightEdge.FragmentSlot);
            if (comparison != 0) return comparison;
        }

        var leftItem = Reference3DStableGroupItem(left);
        var rightItem = Reference3DStableGroupItem(right);
        comparison = CompareReference3DRenderItemStableIdentity(leftItem, rightItem);
        if (comparison != 0) return comparison;
        comparison = CompareReference3DSurfacePlaneKeys(left.PlaneKey, right.PlaneKey);
        if (comparison != 0) return comparison;
        comparison = left.Items.Length.CompareTo(right.Items.Length);
        return comparison != 0 ? comparison : left.StableSlot.CompareTo(right.StableSlot);
    }

    private static Reference3DRenderItem Reference3DStableGroupItem(Reference3DRenderGroup group)
    {
        var result = group.Items[0];
        for (var index = 1; index < group.Items.Length; index++)
        {
            if (CompareReference3DRenderItemStableIdentity(group.Items[index], result) < 0)
            {
                result = group.Items[index];
            }
        }
        return result;
    }

    private static int CompareReference3DRenderItemStableIdentity(
        Reference3DRenderItem left,
        Reference3DRenderItem right)
    {
        var comparison = left.ObjectIndex.CompareTo(right.ObjectIndex);
        if (comparison != 0) return comparison;
        comparison = ((int)left.Kind).CompareTo((int)right.Kind);
        if (comparison != 0) return comparison;
        comparison = left.SurfaceSlot.CompareTo(right.SurfaceSlot);
        if (comparison != 0) return comparison;
        comparison = left.StableFragmentIdentity.CompareTo(right.StableFragmentIdentity);
        if (comparison != 0) return comparison;
        comparison = left.FragmentSlot.CompareTo(right.FragmentSlot);
        if (comparison != 0) return comparison;
        comparison = left.SecondaryObjectIndex.CompareTo(right.SecondaryObjectIndex);
        if (comparison != 0) return comparison;
        comparison = left.ObjectSlot.CompareTo(right.ObjectSlot);
        return comparison != 0 ? comparison : left.LayerIndex.CompareTo(right.LayerIndex);
    }

    private int CompareReference3DCoplanarItems(
        Reference3DRenderItem left,
        Reference3DRenderItem right)
    {
        var comparison = right.LayerIndex.CompareTo(left.LayerIndex);
        return comparison != 0 ? comparison : CompareReference3DSameLayerItems(left, right);
    }

    private static int FindReference3DRenderComponent(int[] parents, int index)
    {
        var root = index;
        while (parents[root] != root) root = parents[root];
        while (parents[index] != index)
        {
            var next = parents[index];
            parents[index] = root;
            index = next;
        }
        return root;
    }

    private static void UnionReference3DRenderComponents(int[] parents, int left, int right)
    {
        var leftRoot = FindReference3DRenderComponent(parents, left);
        var rightRoot = FindReference3DRenderComponent(parents, right);
        if (leftRoot == rightRoot) return;
        if (leftRoot < rightRoot) parents[rightRoot] = leftRoot;
        else parents[leftRoot] = rightRoot;
    }

    private float GetReference3DRenderItemOverlapRadius(Reference3DRenderItem item)
    {
        return item.Kind switch
        {
            Reference3DRenderKind.FrontStroke
                when item.OcclusionContours is { Length: > 0 } => 0,
            Reference3DRenderKind.FrontStroke => Math.Max(
                0,
                GetReference3DStrokeWidth(item.ObjectIndex, Scene.Stroke[item.ObjectIndex]) * 0.5f),
            Reference3DRenderKind.Outline => 0.5f,
            _ => 0
        };
    }

    private static bool TryGetReference3DProjectedBounds(
        IReadOnlyList<Reference3DProjectedContour> contours,
        float overlapRadius,
        out RectangleF bounds)
    {
        var left = float.PositiveInfinity;
        var top = float.PositiveInfinity;
        var right = float.NegativeInfinity;
        var bottom = float.NegativeInfinity;
        foreach (var contour in contours)
        {
            foreach (var point in contour.Points)
            {
                if (!float.IsFinite(point.X) || !float.IsFinite(point.Y)) continue;
                left = Math.Min(left, point.X);
                top = Math.Min(top, point.Y);
                right = Math.Max(right, point.X);
                bottom = Math.Max(bottom, point.Y);
            }
        }

        if (!float.IsFinite(left)
            || !float.IsFinite(top)
            || !float.IsFinite(right)
            || !float.IsFinite(bottom))
        {
            bounds = RectangleF.Empty;
            return false;
        }

        overlapRadius = float.IsFinite(overlapRadius) ? Math.Max(0, overlapRadius) : 0;
        bounds = RectangleF.FromLTRB(
            left - overlapRadius,
            top - overlapRadius,
            right + overlapRadius,
            bottom + overlapRadius);
        return true;
    }

    private static bool TryGetReference3DProjectedBoundsCached(
        IReadOnlyList<Reference3DProjectedContour> contours,
        float overlapRadius,
        IDictionary<Reference3DProjectedContour[], RectangleF> cache,
        out RectangleF bounds)
    {
        if (contours is not Reference3DProjectedContour[] contourArray)
        {
            return TryGetReference3DProjectedBounds(contours, overlapRadius, out bounds);
        }

        if (!cache.TryGetValue(contourArray, out var baseBounds)
            && !TryGetReference3DProjectedBounds(contourArray, 0, out baseBounds))
        {
            bounds = RectangleF.Empty;
            return false;
        }

        overlapRadius = float.IsFinite(overlapRadius) ? Math.Max(0, overlapRadius) : 0;
        bounds = RectangleF.FromLTRB(
            baseBounds.Left - overlapRadius,
            baseBounds.Top - overlapRadius,
            baseBounds.Right + overlapRadius,
            baseBounds.Bottom + overlapRadius);
        cache.TryAdd(contourArray, baseBounds);
        return true;
    }

    private static bool Reference3DProjectedBoundsOverlap(RectangleF left, RectangleF right)
    {
        return left.Left <= right.Right + ReferenceSurfaceOverlapTolerancePixels
            && right.Left <= left.Right + ReferenceSurfaceOverlapTolerancePixels
            && left.Top <= right.Bottom + ReferenceSurfaceOverlapTolerancePixels
            && right.Top <= left.Bottom + ReferenceSurfaceOverlapTolerancePixels;
    }

    private static bool Reference3DRenderItemsOverlap(
        Reference3DRenderItem left,
        float leftRadius,
        Reference3DRenderItem right,
        float rightRadius)
    {
        var tolerance = Math.Max(0, leftRadius)
            + Math.Max(0, rightRadius)
            + ReferenceSurfaceOverlapTolerancePixels;
        var leftContours = left.OcclusionContours ?? left.Contours;
        var rightContours = right.OcclusionContours ?? right.Contours;
        foreach (var leftContour in leftContours)
        {
            foreach (var rightContour in rightContours)
            {
                if (Reference3DProjectedContourEdgesOverlap(leftContour, rightContour, tolerance)) return true;
            }
        }

        if (Reference3DRenderItemHasFill(right)
            && Reference3DAnyProjectedPointInside(leftContours, rightContours))
        {
            return true;
        }
        return Reference3DRenderItemHasFill(left)
            && Reference3DAnyProjectedPointInside(rightContours, leftContours);
    }

    private static bool Reference3DRenderItemHasFill(Reference3DRenderItem item)
    {
        return item.Kind is Reference3DRenderKind.Back
            or Reference3DRenderKind.Side
            or Reference3DRenderKind.FrontFill
            || item.OcclusionContours is { Length: > 0 };
    }

    private static bool Reference3DAnyProjectedPointInside(
        IReadOnlyList<Reference3DProjectedContour> source,
        IReadOnlyList<Reference3DProjectedContour> target)
    {
        foreach (var contour in source)
        {
            foreach (var point in contour.Points)
            {
                if (PointInProjectedFill(point, target)) return true;
            }
        }
        return false;
    }

    private static bool Reference3DProjectedContourEdgesOverlap(
        Reference3DProjectedContour left,
        Reference3DProjectedContour right,
        float tolerance)
    {
        var leftCount = EffectiveReference3DPointCount(left.Points, left.Closed);
        var rightCount = EffectiveReference3DPointCount(right.Points, right.Closed);
        if (leftCount == 0 || rightCount == 0) return false;
        if (leftCount == 1 || rightCount == 1)
        {
            return ReferencePointDistance(left.Points[0], right.Points[0])
                <= tolerance;
        }

        var leftSegments = left.Closed ? leftCount : leftCount - 1;
        var rightSegments = right.Closed ? rightCount : rightCount - 1;
        for (var leftSegment = 0; leftSegment < leftSegments; leftSegment++)
        {
            var leftStart = left.Points[leftSegment];
            var leftEnd = left.Points[(leftSegment + 1) % leftCount];
            for (var rightSegment = 0; rightSegment < rightSegments; rightSegment++)
            {
                var rightStart = right.Points[rightSegment];
                var rightEnd = right.Points[(rightSegment + 1) % rightCount];
                if (Reference3DProjectedSegmentsOverlap(
                        leftStart,
                        leftEnd,
                        rightStart,
                        rightEnd,
                        tolerance))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool Reference3DProjectedSegmentsOverlap(
        PointF leftStart,
        PointF leftEnd,
        PointF rightStart,
        PointF rightEnd,
        float tolerance)
    {
        var leftRightStart = Reference2DCross(leftStart, leftEnd, rightStart);
        var leftRightEnd = Reference2DCross(leftStart, leftEnd, rightEnd);
        var rightLeftStart = Reference2DCross(rightStart, rightEnd, leftStart);
        var rightLeftEnd = Reference2DCross(rightStart, rightEnd, leftEnd);
        if (((leftRightStart > 0 && leftRightEnd < 0) || (leftRightStart < 0 && leftRightEnd > 0))
            && ((rightLeftStart > 0 && rightLeftEnd < 0) || (rightLeftStart < 0 && rightLeftEnd > 0)))
        {
            return true;
        }

        return DistanceToSegment(leftStart, rightStart, rightEnd) <= tolerance
            || DistanceToSegment(leftEnd, rightStart, rightEnd) <= tolerance
            || DistanceToSegment(rightStart, leftStart, leftEnd) <= tolerance
            || DistanceToSegment(rightEnd, leftStart, leftEnd) <= tolerance;
    }

    private static double Reference2DCross(PointF start, PointF end, PointF point)
    {
        return (double)(end.X - start.X) * (point.Y - start.Y)
            - (double)(end.Y - start.Y) * (point.X - start.X);
    }

    private int CompareReference3DSameLayerItems(
        Reference3DRenderItem left,
        Reference3DRenderItem right)
    {
        var comparison = Reference3DRenderPriority(left.Kind).CompareTo(Reference3DRenderPriority(right.Kind));
        if (comparison != 0) return comparison;
        comparison = Scene.ObjectOrder[left.ObjectIndex].CompareTo(Scene.ObjectOrder[right.ObjectIndex]);
        if (comparison != 0) return comparison;
        comparison = Scene.ObjectSubOrder[left.ObjectIndex].CompareTo(Scene.ObjectSubOrder[right.ObjectIndex]);
        if (comparison != 0) return comparison;
        comparison = left.ObjectIndex.CompareTo(right.ObjectIndex);
        if (comparison != 0) return comparison;
        comparison = left.SurfaceSlot.CompareTo(right.SurfaceSlot);
        if (comparison != 0) return comparison;
        comparison = left.StableFragmentIdentity.CompareTo(right.StableFragmentIdentity);
        return comparison != 0 ? comparison : left.FragmentSlot.CompareTo(right.FragmentSlot);
    }

    private static int CompareReference3DSurfacePlaneKeys(
        Reference3DSurfacePlaneKey left,
        Reference3DSurfacePlaneKey right)
    {
        var comparison = right.IsValid.CompareTo(left.IsValid);
        if (comparison != 0) return comparison;
        comparison = left.NormalX.CompareTo(right.NormalX);
        if (comparison != 0) return comparison;
        comparison = left.NormalY.CompareTo(right.NormalY);
        if (comparison != 0) return comparison;
        comparison = left.NormalZ.CompareTo(right.NormalZ);
        if (comparison != 0) return comparison;
        return left.PlaneDistance.CompareTo(right.PlaneDistance);
    }

    private static int Reference3DRenderPriority(Reference3DRenderKind kind) => kind switch
    {
        Reference3DRenderKind.Back => 0,
        Reference3DRenderKind.Side => 1,
        Reference3DRenderKind.FrontFill => 2,
        Reference3DRenderKind.FrontStroke => 3,
        Reference3DRenderKind.Outline => 4,
        _ => 5
    };

    private static int Reference3DNormalizedSurfaceKind(Reference3DRenderKind kind)
    {
        return kind is Reference3DRenderKind.FrontFill or Reference3DRenderKind.FrontStroke
            ? (int)Reference3DRenderKind.FrontFill
            : (int)kind;
    }

    private static float AverageReference3DDepth(
        IReadOnlyList<Reference3DProjectedContour> contours,
        float fallback)
    {
        double total = 0;
        var points = 0;
        foreach (var contour in contours)
        {
            var count = contour.Points.Length;
            if (count == 0 || !float.IsFinite(contour.AverageDepth)) continue;
            total += contour.AverageDepth * count;
            points += count;
        }
        return points > 0 ? (float)(total / points) : fallback;
    }

    private static int EffectiveReference3DPointCount(IReadOnlyList<PointF> points, bool closed)
    {
        var count = points.Count;
        return closed && count > 2 && ReferencePointDistance(points[0], points[^1]) <= 0.0001f
            ? count - 1
            : count;
    }

    private static bool IsReference3DExtrusionCorner(
        IReadOnlyList<PointF> points,
        int count,
        int index)
    {
        if (count <= 4) return true;
        var previous = points[(index + count - 1) % count];
        var current = points[index];
        var next = points[(index + 1) % count];
        var incoming = new Vector2(current.X - previous.X, current.Y - previous.Y);
        var outgoing = new Vector2(next.X - current.X, next.Y - current.Y);
        if (incoming.LengthSquared() <= 0.000001f || outgoing.LengthSquared() <= 0.000001f) return false;
        return Vector2.Dot(Vector2.Normalize(incoming), Vector2.Normalize(outgoing)) < ReferenceExtrusionCornerCosine;
    }

    internal Reference3DProjectedContour[] GetReference3DProjectedMaskContours(
        SceneCompositionMaskClip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        var projected = new List<Reference3DProjectedContour>();
        foreach (var contour in GetSceneCompositionMaskWorldContours(clip))
        {
            if (TryProjectIdentityClosedContour(contour, out var mapped)) projected.Add(mapped);
        }
        return projected.ToArray();
    }

    internal PointF[][] GetSceneCompositionMaskWorldContours(SceneCompositionMaskClip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (clip.Frame < 0) return [];
        var maskScene = clip.MaskScene;
        var result = new List<PointF[]>();
        for (var objectIndex = 0; objectIndex < maskScene.ObjectCount; objectIndex++)
        {
            var layer = maskScene.ObjectLayer[objectIndex];
            if (!maskScene.IsObjectActive(objectIndex, clip.Frame)
                || !maskScene.ShouldRenderLayerContent(layer))
            {
                continue;
            }

            var shape = maskScene.ShapeKind[objectIndex];
            if (SceneRenderOrder.HasFill(shape))
            {
                foreach (var contour in GetReference3DSourceContours(maskScene, objectIndex))
                {
                    if (contour.Closed && contour.Points.Length >= 3) result.Add(contour.Points);
                }
                continue;
            }

            if (!SceneRenderOrder.HasStroke(shape, maskScene.Stroke[objectIndex])) continue;
            foreach (var contour in maskScene.GetStrokeOutlineContours(objectIndex))
            {
                if (contour.Length >= 3) result.Add(contour);
            }
        }
        return result.ToArray();
    }

    internal float GetReference3DStrokeWidth(int objectIndex, float worldWidth)
    {
        if ((uint)objectIndex >= Scene.ObjectCount || worldWidth <= 0) return 0;
        worldWidth = GetReference3DSourceStrokeWidth(objectIndex, worldWidth);
        var center = new PointF(Scene.X[objectIndex], Scene.Y[objectIndex]);
        var offsetX = new PointF(center.X + worldWidth, center.Y);
        var offsetY = new PointF(center.X, center.Y + worldWidth);
        if (!TryProjectReference3DFrontPoint(objectIndex, center, out var projectedCenter, out _)) return 1f;
        var lengthX = TryProjectReference3DFrontPoint(objectIndex, offsetX, out var projectedX, out _)
            ? Distance(projectedCenter, projectedX)
            : 0;
        var lengthY = TryProjectReference3DFrontPoint(objectIndex, offsetY, out var projectedY, out _)
            ? Distance(projectedCenter, projectedY)
            : 0;
        return Math.Max(0.75f, Math.Max(lengthX, lengthY));
    }

    internal bool TryGetReference3DLineEndpointJoin(
        int objectIndex,
        bool startEndpoint,
        IReadOnlyList<Reference3DProjectedContour> projectedContours,
        float screenStroke,
        out PointF joint,
        out LineMiterJoin[] miters,
        out float depth)
    {
        joint = PointF.Empty;
        miters = [];
        depth = 0;
        if (!float.IsFinite(screenStroke)
            || screenStroke <= 0
            || !TryGetReference3DSharpLineJunction(
                objectIndex,
                startEndpoint,
                out var sourceJoint,
                out var sourceInterior,
                out var connections)
            || objectIndex != connections
                .Select(connection => connection.ObjectIndex)
                .Append(objectIndex)
                .Min()
            || !TryProjectReference3DFrontPoint(
                objectIndex,
                sourceJoint,
                out joint,
                out depth)
            || !TryProjectReference3DFrontPoint(
                objectIndex,
                sourceInterior,
                out var currentInterior,
                out _)
            || !Reference3DProjectedContoursContainLineEndpoint(
                projectedContours,
                joint,
                startEndpoint))
        {
            return false;
        }

        var interiorPoints = new PointF[connections.Length + 1];
        interiorPoints[0] = currentInterior;
        var endpointTolerance = Math.Max(1.5f, Math.Min(4f, screenStroke * 0.25f));
        var depthTolerance = GetReference3DLineWorldEndpointTolerance(objectIndex);
        for (var index = 0; index < connections.Length; index++)
        {
            var connection = connections[index];
            if (!TryProjectReference3DFrontPoint(
                    connection.ObjectIndex,
                    connection.Endpoint,
                    out var adjacentJoint,
                    out var adjacentDepth)
                || !TryProjectReference3DFrontPoint(
                    connection.ObjectIndex,
                    connection.Interior,
                    out var adjacentInterior,
                    out _)
                || Distance(joint, adjacentJoint) > endpointTolerance
                || Math.Abs(depth - adjacentDepth) > depthTolerance)
            {
                return false;
            }
            interiorPoints[index + 1] = adjacentInterior;
        }

        miters = LineJoinGeometry.CreateJunctionMiters(joint, interiorPoints, screenStroke * 0.5f);
        return miters.Length > 0;
    }

    internal bool TryGetReference3DLineSourceEndpointJoin(
        int objectIndex,
        bool startEndpoint,
        out PointF joint,
        out LineMiterJoin[] miters)
    {
        return TryGetReference3DLineSourceEndpointJoin(
            objectIndex,
            startEndpoint,
            eligibleLineObjects: null,
            out joint,
            out miters);
    }

    private bool TryGetReference3DLineSourceEndpointJoin(
        int objectIndex,
        bool startEndpoint,
        IReadOnlySet<int>? eligibleLineObjects,
        out PointF joint,
        out LineMiterJoin[] miters)
    {
        joint = PointF.Empty;
        miters = [];
        if (!TryGetReference3DSharpLineJunction(
                objectIndex,
                startEndpoint,
                out joint,
                out var currentInterior,
                out var connections,
                eligibleLineObjects)
            || objectIndex != connections
                .Select(connection => connection.ObjectIndex)
                .Append(objectIndex)
                .Min())
        {
            return false;
        }

        var interiorPoints = new PointF[connections.Length + 1];
        interiorPoints[0] = currentInterior;
        for (var index = 0; index < connections.Length; index++)
        {
            interiorPoints[index + 1] = connections[index].Interior;
        }
        var width = GetReference3DSourceStrokeWidth(objectIndex, Scene.Stroke[objectIndex]);
        miters = LineJoinGeometry.CreateJunctionMiters(joint, interiorPoints, width * 0.5f);
        return miters.Length > 0;
    }

    private bool TryGetReference3DSharpLineJunction(
        int objectIndex,
        bool startEndpoint,
        out PointF endpoint,
        out PointF interior,
        out Reference3DLineEndpointConnection[] connections,
        IReadOnlySet<int>? eligibleLineObjects = null)
    {
        endpoint = PointF.Empty;
        interior = PointF.Empty;
        connections = [];
        if ((uint)objectIndex >= Scene.ObjectCount
            || Scene.ShapeKind[objectIndex] != ShapeKind.Line
            || !Scene.IsObjectActive(objectIndex, Frame)
            || IsObjectHiddenForRendering(Scene, objectIndex)
            || eligibleLineObjects is not null && !eligibleLineObjects.Contains(objectIndex)
            || Scene.GetLineEndpointStyle(objectIndex, startEndpoint) != LineEndpointStyle.Sharp
            || !Scene.TryGetLineEndpoint(objectIndex, startEndpoint, out var rawEndpoint)
            || !TryGetReference3DLineSourceEndpointGeometry(
                objectIndex,
                startEndpoint,
                out endpoint,
                out interior))
        {
            return false;
        }

        var tolerance = Reference3DLineEndpointToleranceUnits;
        var bounds = RectangleF.FromLTRB(
            rawEndpoint.X - tolerance,
            rawEndpoint.Y - tolerance,
            rawEndpoint.X + tolerance,
            rawEndpoint.Y + tolerance);
        var allSharp = true;
        var result = new List<Reference3DLineEndpointConnection>();
        foreach (var candidate in Scene.QueryObjects(bounds, Frame))
        {
            if (candidate == objectIndex
                || Scene.ShapeKind[candidate] != ShapeKind.Line
                || IsObjectHiddenForRendering(Scene, candidate)
                || eligibleLineObjects is not null && !eligibleLineObjects.Contains(candidate)
                || Scene.ObjectLayer[candidate] != Scene.ObjectLayer[objectIndex]
                || Scene.ObjectKeyframeFrame[candidate] != Scene.ObjectKeyframeFrame[objectIndex]
                || Scene.StrokeArgb[candidate] != Scene.StrokeArgb[objectIndex]
                || Math.Abs(Scene.Stroke[candidate] - Scene.Stroke[objectIndex]) > 0.001f
                || !Reference3DLineObjectsShareExtrusionSpace(objectIndex, candidate))
            {
                continue;
            }

            var connectedAtStart = Scene.TryGetLineEndpoint(
                    candidate,
                    startEndpoint: true,
                    out var candidateStart)
                && Distance(candidateStart, rawEndpoint) <= tolerance;
            var connectedAtEnd = !connectedAtStart
                && Scene.TryGetLineEndpoint(
                    candidate,
                    startEndpoint: false,
                    out var candidateEnd)
                && Distance(candidateEnd, rawEndpoint) <= tolerance;
            if (!connectedAtStart && !connectedAtEnd
                || !TryGetReference3DLineSourceEndpointGeometry(
                    candidate,
                    connectedAtStart,
                    out var candidateEndpoint,
                    out var candidateInterior)
                || Distance(endpoint, candidateEndpoint) > tolerance)
            {
                continue;
            }

            allSharp &= Scene.GetLineEndpointStyle(candidate, connectedAtStart)
                == LineEndpointStyle.Sharp;
            result.Add(new Reference3DLineEndpointConnection(
                candidate,
                connectedAtStart,
                candidateEndpoint,
                candidateInterior));
        }
        if (!allSharp || result.Count == 0) return false;
        connections = result
            .OrderBy(connection => connection.ObjectIndex)
            .ThenBy(connection => connection.StartEndpoint)
            .ToArray();
        return true;
    }

    private bool TryGetReference3DLineSourceEndpointGeometry(
        int objectIndex,
        bool startEndpoint,
        out PointF endpoint,
        out PointF interior)
    {
        endpoint = PointF.Empty;
        interior = PointF.Empty;
        var contours = GetReference3DSourceContours(objectIndex);
        for (var contourIndex = startEndpoint ? 0 : contours.Length - 1;
             startEndpoint ? contourIndex < contours.Length : contourIndex >= 0;
             contourIndex += startEndpoint ? 1 : -1)
        {
            var contour = contours[contourIndex];
            if (contour.Closed || contour.Points.Length < 2) continue;
            var endpointIndex = startEndpoint ? 0 : contour.Points.Length - 1;
            endpoint = contour.Points[endpointIndex];

            var step = startEndpoint ? 1 : -1;
            for (var index = endpointIndex + step;
                 (uint)index < contour.Points.Length;
                 index += step)
            {
                var candidate = contour.Points[index];
                if (Distance(endpoint, candidate) <= 0.01f) continue;
                interior = candidate;
                return true;
            }
            return false;
        }
        return false;
    }

    private bool Reference3DLineObjectsShareExtrusionSpace(int firstObject, int secondObject)
    {
        var firstTransform = GetReference3DFlatToScene(firstObject);
        var secondTransform = GetReference3DFlatToScene(secondObject);
        if (!Reference3DMatrixNear(firstTransform, secondTransform)) return false;
        var firstExtrusion = GetReference3DExtrusionVector(firstObject);
        var secondExtrusion = GetReference3DExtrusionVector(secondObject);
        if (Vector3.DistanceSquared(firstExtrusion, secondExtrusion)
                > Reference3DLinePoseTolerance * Reference3DLinePoseTolerance)
        {
            return false;
        }
        var firstWidth = GetReference3DSourceStrokeWidth(firstObject, Scene.Stroke[firstObject]);
        var secondWidth = GetReference3DSourceStrokeWidth(secondObject, Scene.Stroke[secondObject]);
        return Math.Abs(firstWidth - secondWidth) <= 0.001f;
    }

    private float GetReference3DLineWorldEndpointTolerance(int objectIndex)
    {
        var transform = GetReference3DFlatToScene(objectIndex);
        var x = Vector3.TransformNormal(
            new Vector3(Reference3DLineEndpointToleranceUnits, 0, 0),
            transform).Length();
        var y = Vector3.TransformNormal(
            new Vector3(0, Reference3DLineEndpointToleranceUnits, 0),
            transform).Length();
        var tolerance = Math.Max(x, y);
        return float.IsFinite(tolerance)
            ? Math.Max(Reference3DLinePoseTolerance, tolerance)
            : Reference3DLineEndpointToleranceUnits;
    }

    private static bool Reference3DMatrixNear(Matrix4x4 left, Matrix4x4 right)
    {
        return Near(left.M11, right.M11) && Near(left.M12, right.M12)
            && Near(left.M13, right.M13) && Near(left.M14, right.M14)
            && Near(left.M21, right.M21) && Near(left.M22, right.M22)
            && Near(left.M23, right.M23) && Near(left.M24, right.M24)
            && Near(left.M31, right.M31) && Near(left.M32, right.M32)
            && Near(left.M33, right.M33) && Near(left.M34, right.M34)
            && Near(left.M41, right.M41) && Near(left.M42, right.M42)
            && Near(left.M43, right.M43) && Near(left.M44, right.M44);

        static bool Near(float first, float second) =>
            float.IsFinite(first)
            && float.IsFinite(second)
            && Math.Abs(first - second) <= Reference3DLinePoseTolerance;
    }

    private static bool Reference3DProjectedContoursContainLineEndpoint(
        IReadOnlyList<Reference3DProjectedContour> contours,
        PointF endpoint,
        bool startEndpoint)
    {
        const float endpointTolerance = 1.5f;
        foreach (var contour in contours)
        {
            if (contour.Closed || contour.Points.Length < 2) continue;
            if (startEndpoint && !contour.HasSourceStart
                || !startEndpoint && !contour.HasSourceEnd)
            {
                continue;
            }
            var projectedEndpoint = startEndpoint ? contour.Points[0] : contour.Points[^1];
            if (Distance(endpoint, projectedEndpoint) <= endpointTolerance) return true;
        }
        return false;
    }

    internal float GetReference3DSourceStrokeWidth(int objectIndex, float composedWidth)
    {
        if ((uint)objectIndex >= Scene.ObjectCount || composedWidth <= 0) return 0;
        if (!ReferenceEquals(_sceneCompositionResultScene, Scene)
            || _sceneCompositionResult?.TryGetPose(objectIndex, out var pose) != true
            || !float.IsFinite(pose.FlatStrokeScale)
            || pose.FlatStrokeScale <= 0.000001f)
        {
            return composedWidth;
        }
        return composedWidth / pose.FlatStrokeScale;
    }

    internal bool TryGetReference3DFlatToScreenTransform(int objectIndex, out Matrix3x2 transform)
    {
        transform = default;
        if ((uint)objectIndex >= Scene.ObjectCount || ReferenceProjectionBlend > 0.000001f) return false;

        const float basis = 1_024f;
        var center = new PointF(Scene.X[objectIndex], Scene.Y[objectIndex]);
        if (!TryProjectReference3DFrontPoint(objectIndex, center, out var origin, out _)
            || !TryProjectReference3DFrontPoint(
                objectIndex,
                new PointF(center.X + basis, center.Y),
                out var axisX,
                out _)
            || !TryProjectReference3DFrontPoint(
                objectIndex,
                new PointF(center.X, center.Y + basis),
                out var axisY,
                out _))
        {
            return false;
        }

        var m11 = (axisX.X - origin.X) / basis;
        var m12 = (axisX.Y - origin.Y) / basis;
        var m21 = (axisY.X - origin.X) / basis;
        var m22 = (axisY.Y - origin.Y) / basis;
        var m31 = origin.X - center.X * m11 - center.Y * m21;
        var m32 = origin.Y - center.X * m12 - center.Y * m22;
        transform = new Matrix3x2(m11, m12, m21, m22, m31, m32);
        return Finite(new Vector3(m11, m12, m21))
            && Finite(new Vector3(m22, m31, m32))
            && Math.Abs(transform.GetDeterminant()) > 0.00000001f;
    }

    internal bool TryGetSpatialGizmoScreenGeometry(out SpatialGizmoScreenGeometry geometry)
    {
        geometry = default;
        return _spatialTransformGizmoVisible
            && TryBuildSpatialGizmoScreenGeometry(out geometry);
    }

    internal bool TryGetSpatialGizmoRenderGeometry(
        out SpatialGizmoScreenGeometry geometry,
        out float opacity,
        out float renderScale)
    {
        geometry = default;
        opacity = SpatialTransformGizmoRenderOpacity;
        renderScale = SpatialTransformGizmoRenderScale;
        if (!_spatialTransformGizmoPresented
            || !TryBuildSpatialGizmoScreenGeometry(out var canonical))
        {
            return false;
        }

        geometry = ScaleSpatialGizmoScreenGeometry(canonical, renderScale);
        return true;
    }

    private bool TryBuildSpatialGizmoScreenGeometry(out SpatialGizmoScreenGeometry geometry)
    {
        geometry = default;
        if (!TryProjectScenePosition(_spatialTransformGizmoOrigin, out var origin, out var originDepth))
        {
            return false;
        }

        var scale = 0.035f * _referenceZoomScale;
        var perspective = ReferencePerspectiveScale(originDepth);
        var length = Math.Clamp(
            SpatialGizmoTargetPixels * SpatialGizmoDpiScale
                / Math.Max(0.0001f, scale * perspective),
            20f,
            100_000f);
        var endpoints = new PointF[3];
        for (var index = 0; index < SpatialAxes.Length; index++)
        {
            var sceneEndpoint = _spatialTransformGizmoOrigin
                + _spatialTransformGizmoBasis.Axis(SpatialAxes[index]) * length;
            if (!TryProjectScenePosition(sceneEndpoint, out endpoints[index], out _)) return false;
        }

        var rings = new PointF[3][];
        if (_spatialTransformGizmoMode == SpatialTransformMode.Rotate)
        {
            for (var axisIndex = 0; axisIndex < SpatialAxes.Length; axisIndex++)
            {
                var points = new PointF[SpatialRotationRingSegments];
                for (var segment = 0; segment < points.Length; segment++)
                {
                    var angle = segment * MathF.Tau / points.Length;
                    var offset = _spatialTransformGizmoBasis.RingOffset(SpatialAxes[axisIndex], angle)
                        * length * 0.82f;
                    if (!TryProjectScenePosition(_spatialTransformGizmoOrigin + offset, out points[segment], out _))
                    {
                        points = [];
                        break;
                    }
                }
                rings[axisIndex] = points;
            }
        }
        else
        {
            rings = [[], [], []];
        }

        var planeHandles = new PointF[3][] { [], [], [] };
        if (_spatialTransformGizmoMode == SpatialTransformMode.Move)
        {
            const float planeStart = 0.18f;
            const float planeEnd = 0.40f;
            for (var planeIndex = 0; planeIndex < SpatialPlanes.Length; planeIndex++)
            {
                var (firstAxis, secondAxis) = _spatialTransformGizmoBasis.PlaneAxes(
                    SpatialPlanes[planeIndex]);
                var firstStart = firstAxis * (length * planeStart);
                var firstEnd = firstAxis * (length * planeEnd);
                var secondStart = secondAxis * (length * planeStart);
                var secondEnd = secondAxis * (length * planeEnd);
                var scenePoints = new[]
                {
                    _spatialTransformGizmoOrigin + firstStart + secondStart,
                    _spatialTransformGizmoOrigin + firstEnd + secondStart,
                    _spatialTransformGizmoOrigin + firstEnd + secondEnd,
                    _spatialTransformGizmoOrigin + firstStart + secondEnd
                };
                var projected = new PointF[scenePoints.Length];
                var valid = true;
                for (var pointIndex = 0; pointIndex < scenePoints.Length; pointIndex++)
                {
                    if (TryProjectScenePosition(scenePoints[pointIndex], out projected[pointIndex], out _)) continue;
                    valid = false;
                    break;
                }
                if (valid && IsSpatialGizmoPlaneHandleStable(projected, firstAxis, secondAxis))
                {
                    planeHandles[planeIndex] = projected;
                }
            }
        }

        geometry = new SpatialGizmoScreenGeometry(
            origin,
            endpoints,
            rings,
            planeHandles,
            length,
            _spatialTransformGizmoBasis);
        return true;
    }

    private static SpatialGizmoScreenGeometry ScaleSpatialGizmoScreenGeometry(
        SpatialGizmoScreenGeometry geometry,
        float scale)
    {
        if (Math.Abs(scale - 1f) <= 0.000001f) return geometry;

        var endpoints = ScaleSpatialGizmoPoints(geometry.Endpoints, geometry.Origin, scale);
        var rings = new PointF[geometry.Rings.Length][];
        for (var index = 0; index < rings.Length; index++)
        {
            rings[index] = ScaleSpatialGizmoPoints(geometry.Rings[index], geometry.Origin, scale);
        }
        var planeHandles = new PointF[geometry.PlaneHandles.Length][];
        for (var index = 0; index < planeHandles.Length; index++)
        {
            planeHandles[index] = ScaleSpatialGizmoPoints(
                geometry.PlaneHandles[index],
                geometry.Origin,
                scale);
        }
        return new SpatialGizmoScreenGeometry(
            geometry.Origin,
            endpoints,
            rings,
            planeHandles,
            geometry.WorldLength * scale,
            geometry.Basis);
    }

    private static PointF[] ScaleSpatialGizmoPoints(
        IReadOnlyList<PointF> points,
        PointF origin,
        float scale)
    {
        if (points.Count == 0) return [];
        var scaled = new PointF[points.Count];
        for (var index = 0; index < points.Count; index++)
        {
            scaled[index] = new PointF(
                origin.X + (points[index].X - origin.X) * scale,
                origin.Y + (points[index].Y - origin.Y) * scale);
        }
        return scaled;
    }

    internal float ReferencePerspectiveScale(float cameraDepth)
    {
        var denominator = ReferencePerspectiveFocalLength
            + (Math.Max(ReferenceNearPlane, cameraDepth) - ReferencePerspectiveFocalLength)
            * ReferenceProjectionBlend;
        return ReferencePerspectiveFocalLength / Math.Max(ReferenceNearPlane, denominator);
    }

    private void ClearReference3DStateForSceneBinding()
    {
        InvalidateReference3DRenderPlanCache();
        _sceneCompositionResult = null;
        _sceneCompositionResultScene = null;
        _sceneCompositionHasSpatialPoses = false;
        _sceneCompositionMaskClips = [];
        _reference2DViewDirection = ReferenceViewDirection.Front;
        _reference3DSelectedObjects = [];
        _reference3DSourceContourCache.Clear();
        ResetSpatialTransformGizmoMotionState();
        _sceneLightGizmoEntries = [];
        _sceneLightGizmoSelectedIndex = -1;
        _sceneLightGizmoVisible = false;
        _sceneLightGizmoSettings = default;
        _sceneLightGizmoHoveredHandle = SceneLightGizmoHandleHit.None;
        _sceneLightGizmoActiveHandle = SceneLightGizmoHandleHit.None;
        _reference3DOpticalPreviewTimer.Stop();
        _reference3DOpticalInteractionPreviewActive = false;
    }

    private void InvalidateReference3DRenderPlanCache()
    {
        _direct2DRenderer.InvalidateReference3DOpticalSurfaceBitmapCache();
        ClearReference3DGdiLocalLightBrushCache();
        unchecked
        {
            _reference3DRenderPlanEpoch++;
        }
        foreach (var entry in _reference3DRenderPlanCache) entry.Dispose();
        _reference3DRenderPlanCache.Clear();
        _reference3DRenderPlanSurfaceBytes = 0;
        _reference3DProjectedContourCache.Clear();
        _reference3DProjectedSolidCache.Clear();
        _reference3DProjectiveMeshCache.Clear();
        _reference3DSortedLayerObjectsEpoch = -1;
    }

    private int[] GetReference3DDrawingOrder()
    {
        var result = new List<int>(Scene.ObjectCount);
        for (var layer = Scene.LayerCount - 1; layer >= 0; layer--)
        {
            if (!Scene.ShouldRenderLayerContent(layer)) continue;
            result.AddRange(GetReference3DLayerObjects(layer));
        }
        return result.ToArray();
    }

    internal Reference3DSourceContour[] GetReference3DSourceContours(int objectIndex)
    {
        return (uint)objectIndex < Scene.ObjectCount
            ? GetReference3DSourceContours(Scene, objectIndex)
            : [];
    }

    private Reference3DSourceContour[] GetReference3DSourceContours(VectorScene scene, int objectIndex)
    {
        if ((uint)objectIndex >= scene.ObjectCount) return [];
        var cacheable = !ReferenceEquals(scene, _distortPreviewScene)
            || _distortPreviewOverrides.Count == 0;
        if (cacheable)
        {
            var cacheKey = (scene, objectIndex);
            if (_reference3DSourceContourCache.TryGetValue(cacheKey, out var cached)
                && cached.GeometryRevision == scene.GeometryRevision
                && cached.SummaryRevision == scene.SummaryRevision
                && cached.EditFrame == scene.EditFrame)
            {
                return cached.Contours;
            }

            var contours = BuildReference3DSourceContours(scene, objectIndex);
            _reference3DSourceContourCache[cacheKey] = (
                scene.GeometryRevision,
                scene.SummaryRevision,
                scene.EditFrame,
                contours);
            return contours;
        }

        return BuildReference3DSourceContours(scene, objectIndex);
    }

    private Reference3DSourceContour[] BuildReference3DSourceContours(
        VectorScene scene,
        int objectIndex)
    {
        var shape = scene.ShapeKind[objectIndex];
        if ((shape is ShapeKind.Freeform or ShapeKind.BrushStroke)
            && scene.TryGetFreehandWorldPoints(objectIndex, out var freehandCenterline))
        {
            var centerline = freehandCenterline
                .Select(point => MapObjectPointForRendering(scene, objectIndex, point))
                .ToArray();
            if (shape == ShapeKind.Freeform)
            {
                return centerline.Length > 1
                    ? [new Reference3DSourceContour(centerline, false)]
                    : [];
            }

            var width = ReferenceEquals(scene, Scene)
                ? GetReference3DSourceStrokeWidth(objectIndex, scene.Stroke[objectIndex])
                : scene.Stroke[objectIndex];
            return FreehandStrokeProcessor.CreateBrushOutlines(centerline, width)
                .Where(contour => contour.Length > 2)
                .Select(contour => new Reference3DSourceContour(contour, true))
                .ToArray();
        }

        if (TryGetObjectDistortionsForRendering(scene, objectIndex, out var distortions)
            && TryGetDistortedVectorGeometryForRendering(scene, objectIndex, distortions, out var distorted))
        {
            var distortedContours = new List<Reference3DSourceContour>();
            foreach (var contour in distorted.ClosedContours)
            {
                var sampled = SampleBezierContour(contour);
                if (sampled.Length > 2) distortedContours.Add(new Reference3DSourceContour(sampled, true));
            }
            if (distorted.OpenStrokeSegments.Length > 0)
            {
                distortedContours.Add(new Reference3DSourceContour(
                    SampleCubicSegments(distorted.OpenStrokeSegments),
                    false));
            }
            if (distortedContours.Count > 0) return distortedContours.ToArray();
        }

        if (shape == ShapeKind.Line
            && scene.TryGetLineCubic(objectIndex, out var start, out var control1, out var control2, out var end))
        {
            return [new Reference3DSourceContour(SampleCubic(start, control1, control2, end), false)];
        }

        PointF[][] contours;
        if (shape == ShapeKind.Text && scene.TryGetTextWorldContours(objectIndex, out var textContours))
        {
            contours = textContours;
        }
        else
        {
            contours = GetObjectBoundaryContoursForRendering(scene, objectIndex);
        }

        return contours
            .Where(contour => contour.Length > 1)
            .Select(contour => new Reference3DSourceContour(contour, true))
            .ToArray();
    }

    private bool TryProjectClosedContour(
        int objectIndex,
        IReadOnlyList<PointF> source,
        Vector3 sceneOffset,
        out Reference3DProjectedContour projected)
    {
        projected = default;
        var count = source.Count;
        if (count > 2 && ReferencePointDistance(source[0], source[^1]) <= 0.0001f) count--;
        if (count < 3 || count > (int.MaxValue - 2) / 2) return false;

        var camera = ArrayPool<Vector3>.Shared.Rent(count * 2 + 2);
        var clippedCount = 0;
        try
        {
            // Keep the original closed-polygon emission order while transforming each source point once.
            if (!TryTransformFlatPointToCamera(
                    objectIndex,
                    source[count - 1],
                    sceneOffset,
                    out var closingPoint))
            {
                return false;
            }

            var previous = closingPoint;
            var previousInside = previous.Z >= ReferenceNearPlane;
            for (var index = 0; index < count - 1; index++)
            {
                if (!TryTransformFlatPointToCamera(
                        objectIndex,
                        source[index],
                        sceneOffset,
                        out var current))
                {
                    return false;
                }

                var currentInside = current.Z >= ReferenceNearPlane;
                if (currentInside != previousInside)
                {
                    camera[clippedCount++] = IntersectNearPlane(previous, current);
                }
                if (currentInside) camera[clippedCount++] = current;
                previous = current;
                previousInside = currentInside;
            }

            var finalInside = closingPoint.Z >= ReferenceNearPlane;
            if (finalInside != previousInside)
            {
                camera[clippedCount++] = IntersectNearPlane(previous, closingPoint);
            }
            if (finalInside) camera[clippedCount++] = closingPoint;

            if (clippedCount < 3) return false;
            var points = new PointF[clippedCount];
            var depth = 0f;
            for (var index = 0; index < clippedCount; index++)
            {
                var point = camera[index];
                points[index] = ProjectCameraVector(point);
                depth += point.Z;
                if (!float.IsFinite(points[index].X) || !float.IsFinite(points[index].Y))
                {
                    return false;
                }
            }

            projected = new Reference3DProjectedContour(points, true, depth / points.Length);
            return true;
        }
        finally
        {
            Array.Clear(camera, 0, clippedCount);
            ArrayPool<Vector3>.Shared.Return(camera);
        }
    }

    private bool TryProjectCameraPolygon(
        IReadOnlyList<Vector3> camera,
        out Reference3DProjectedContour projected)
    {
        projected = default;
        var clipped = ClipClosedNearPlane(camera);
        if (clipped.Count < 3) return false;
        var points = new PointF[clipped.Count];
        var depth = 0f;
        for (var index = 0; index < clipped.Count; index++)
        {
            points[index] = ProjectCameraVector(clipped[index]);
            depth += clipped[index].Z;
        }
        if (points.Any(point => !float.IsFinite(point.X) || !float.IsFinite(point.Y))) return false;
        projected = new Reference3DProjectedContour(points, true, depth / points.Length);
        return true;
    }

    private bool TryProjectIdentityClosedContour(
        IReadOnlyList<PointF> source,
        out Reference3DProjectedContour projected)
    {
        projected = default;
        var camera = new List<Vector3>(source.Count);
        var count = source.Count;
        if (count > 2 && ReferencePointDistance(source[0], source[^1]) <= 0.0001f) count--;
        for (var index = 0; index < count; index++)
        {
            var point = source[index];
            var transformed = CameraSpacePoint(new Point3(point.X, -point.Y, 0));
            camera.Add(new Vector3(transformed.X, transformed.Y, transformed.Z));
        }
        var clipped = ClipClosedNearPlane(camera);
        if (clipped.Count < 3) return false;
        var points = new PointF[clipped.Count];
        var depth = 0f;
        for (var index = 0; index < clipped.Count; index++)
        {
            points[index] = ProjectCameraVector(clipped[index]);
            depth += clipped[index].Z;
        }
        if (points.Any(point => !float.IsFinite(point.X) || !float.IsFinite(point.Y))) return false;
        projected = new Reference3DProjectedContour(points, true, depth / points.Length);
        return true;
    }

    private void AppendProjectedOpenContour(
        int objectIndex,
        IReadOnlyList<PointF> source,
        Vector3 sceneOffset,
        ICollection<Reference3DProjectedContour> destination)
    {
        if (source.Count < 2) return;

        var cameraPoints = new Vector3[source.Count];
        var validCameraPoints = new bool[source.Count];
        for (var index = 0; index < source.Count; index++)
        {
            validCameraPoints[index] = TryTransformFlatPointToCamera(
                objectIndex,
                source[index],
                sceneOffset,
                out cameraPoints[index]);
        }

        List<PointF>? run = null;
        double runDepth = 0;
        var runDepthCount = 0;
        var runHasSourceStart = false;
        var runHasSourceEnd = false;
        for (var index = 1; index < source.Count; index++)
        {
            if (!validCameraPoints[index - 1] || !validCameraPoints[index])
            {
                FlushRun();
                continue;
            }

            var sourceStart = cameraPoints[index - 1];
            var sourceEnd = cameraPoints[index];
            var startInside = sourceStart.Z >= ReferenceNearPlane;
            var endInside = sourceEnd.Z >= ReferenceNearPlane;
            var start = sourceStart;
            var end = sourceEnd;
            if (!ClipNearPlane(ref start, ref end))
            {
                FlushRun();
                continue;
            }

            var a = ProjectCameraVector(start);
            var b = ProjectCameraVector(end);
            if (!float.IsFinite(a.X) || !float.IsFinite(a.Y)
                || !float.IsFinite(b.X) || !float.IsFinite(b.Y))
            {
                FlushRun();
                continue;
            }

            if (!startInside) FlushRun();
            if (run is null) runHasSourceStart = index == 1 && startInside;
            AppendPoint(a, start.Z);
            AppendPoint(b, end.Z);
            runHasSourceEnd = index == source.Count - 1 && endInside;
            if (!endInside) FlushRun();
        }
        FlushRun();

        void AppendPoint(PointF point, float depth)
        {
            run ??= new List<PointF>();
            if (run.Count > 0
                && ReferencePointDistance(run[^1], point) <= 0.0001f)
            {
                return;
            }
            run.Add(point);
            runDepth += depth;
            runDepthCount++;
        }

        void FlushRun()
        {
            if (run is { Count: >= 2 } && runDepthCount > 0)
            {
                destination.Add(new Reference3DProjectedContour(
                    run.ToArray(),
                    false,
                    (float)(runDepth / runDepthCount),
                    runHasSourceStart,
                    runHasSourceEnd));
            }
            run = null;
            runDepth = 0;
            runDepthCount = 0;
            runHasSourceStart = false;
            runHasSourceEnd = false;
        }
    }

    private bool TryTransformFlatPointToScene(int objectIndex, PointF flatPoint, out Vector3 scenePoint)
    {
        return TryTransformFlatPointToScene(objectIndex, flatPoint, Vector3.Zero, out scenePoint);
    }

    private bool TryTransformFlatPointToScene(
        int objectIndex,
        PointF flatPoint,
        Vector3 sceneOffset,
        out Vector3 scenePoint)
    {
        var transform = GetReference3DFlatToScene(objectIndex);
        scenePoint = Vector3.Transform(new Vector3(flatPoint.X, flatPoint.Y, 0), transform) + sceneOffset;
        return Finite(scenePoint);
    }

    private Matrix4x4 GetReference3DFlatToScene(int objectIndex)
    {
        return ReferenceEquals(_sceneCompositionResultScene, Scene)
            && _sceneCompositionResult?.TryGetPose(objectIndex, out var pose) == true
            ? pose.FlatToScene
            : Matrix4x4.Identity;
    }

    internal bool TryGetReference3DFlatToSceneTransform(int objectIndex, out Matrix4x4 transform)
    {
        transform = Matrix4x4.Identity;
        if ((uint)objectIndex >= Scene.ObjectCount
            || !ReferenceEquals(_sceneCompositionResultScene, Scene)
            || _sceneCompositionResult?.TryGetPose(objectIndex, out var pose) != true)
        {
            return false;
        }
        transform = pose.FlatToScene;
        return true;
    }

    private Reference3DSurfacePlaneKey GetReference3DSurfacePlaneKey(
        int objectIndex,
        Vector3 sceneOffset,
        bool reverseNormal = false)
    {
        return CreateReference3DSurfacePlaneKey(
            GetReference3DSurfacePlane(objectIndex, sceneOffset, reverseNormal));
    }

    private Reference3DSurfacePlane GetReference3DSurfacePlane(
        int objectIndex,
        Vector3 sceneOffset,
        bool reverseNormal = false)
    {
        if ((uint)objectIndex >= Scene.ObjectCount || !Finite(sceneOffset)) return default;
        var transform = GetReference3DFlatToScene(objectIndex);
        var axisX = Vector3.TransformNormal(Vector3.UnitX, transform);
        var axisY = Vector3.TransformNormal(Vector3.UnitY, transform);
        var normal = Vector3.Cross(axisX, axisY);
        if (reverseNormal) normal = -normal;
        var flatCenter = new Vector3(Scene.X[objectIndex], Scene.Y[objectIndex], 0);
        var surfacePoint = Vector3.Transform(flatCenter, transform) + sceneOffset;
        var normalLength = normal.Length();
        if (!Finite(normal)
            || !Finite(surfacePoint)
            || !float.IsFinite(normalLength)
            || normalLength <= ReferenceSurfaceNormalEpsilon)
        {
            return default;
        }

        normal /= normalLength;
        var distance = Vector3.Dot(normal, surfacePoint);
        return float.IsFinite(distance)
            ? new Reference3DSurfacePlane(normal, distance, true)
            : default;
    }

    private Reference3DSurfacePlane GetReference3DSideSurfacePlane(
        int objectIndex,
        PointF flatStart,
        PointF flatEnd,
        Vector3 frontOffset,
        Vector3 backOffset)
    {
        if (!TryTransformFlatPointToScene(objectIndex, flatStart, frontOffset, out var frontStart)
            || !TryTransformFlatPointToScene(objectIndex, flatEnd, frontOffset, out var frontEnd)
            || !TryTransformFlatPointToScene(objectIndex, flatStart, backOffset, out var backStart))
        {
            return default;
        }

        var normal = Vector3.Cross(frontEnd - frontStart, backStart - frontStart);
        var normalLength = normal.Length();
        if (!Finite(normal)
            || !Finite(frontStart)
            || !float.IsFinite(normalLength)
            || normalLength <= ReferenceSurfaceNormalEpsilon)
        {
            return default;
        }

        normal /= normalLength;
        var distance = Vector3.Dot(normal, frontStart);
        return float.IsFinite(distance)
            ? new Reference3DSurfacePlane(normal, distance, true)
            : default;
    }

    private static Reference3DSurfacePlaneKey CreateReference3DSurfacePlaneKey(
        Vector3 normal,
        Vector3 surfacePoint)
    {
        var normalLength = normal.Length();
        if (!Finite(normal)
            || !Finite(surfacePoint)
            || !float.IsFinite(normalLength)
            || normalLength <= ReferenceSurfaceNormalEpsilon)
        {
            return default;
        }

        normal /= normalLength;
        var planeDistance = Vector3.Dot(normal, surfacePoint);
        if (!float.IsFinite(planeDistance)
            || !TryQuantizeReference3DSurfaceValue(
                normal.X,
                ReferenceSurfaceNormalQuantization,
                out var normalX)
            || !TryQuantizeReference3DSurfaceValue(
                normal.Y,
                ReferenceSurfaceNormalQuantization,
                out var normalY)
            || !TryQuantizeReference3DSurfaceValue(
                normal.Z,
                ReferenceSurfaceNormalQuantization,
                out var normalZ)
            || !TryQuantizeReference3DSurfaceValue(
                planeDistance,
                ReferenceSurfacePlaneQuantization,
                out var quantizedDistance))
        {
            return default;
        }

        return new Reference3DSurfacePlaneKey(
            normalX,
            normalY,
            normalZ,
            quantizedDistance,
            true);
    }

    private static Reference3DSurfacePlaneKey CreateReference3DSurfacePlaneKey(
        Reference3DSurfacePlane plane)
    {
        if (!plane.IsValid || !Finite(plane.Normal) || !float.IsFinite(plane.Distance)) return default;
        return CreateReference3DSurfacePlaneKey(plane.Normal, plane.Normal * plane.Distance);
    }

    private static bool TryQuantizeReference3DSurfaceValue(
        float value,
        double scale,
        out long quantized)
    {
        quantized = 0;
        var scaled = value * scale;
        if (!double.IsFinite(scaled)
            || scaled <= long.MinValue + 1d
            || scaled >= long.MaxValue - 1d)
        {
            return false;
        }

        quantized = (long)Math.Round(scaled, MidpointRounding.AwayFromZero);
        return true;
    }

    private bool TryTransformFlatPointToCamera(int objectIndex, PointF flatPoint, out Vector3 camera)
    {
        return TryTransformFlatPointToCamera(objectIndex, flatPoint, Vector3.Zero, out camera);
    }

    private bool TryTransformFlatPointToCamera(
        int objectIndex,
        PointF flatPoint,
        Vector3 sceneOffset,
        out Vector3 camera)
    {
        camera = default;
        if (!TryTransformFlatPointToScene(objectIndex, flatPoint, sceneOffset, out var scenePoint)) return false;
        var transformed = CameraSpacePoint(new Point3(scenePoint.X, -scenePoint.Y, scenePoint.Z));
        camera = new Vector3(transformed.X, transformed.Y, transformed.Z);
        return Finite(camera);
    }

    private bool TryProjectFlatPointWithOffset(
        int objectIndex,
        PointF flatPoint,
        Vector3 sceneOffset,
        out PointF screen,
        out float depth)
    {
        screen = PointF.Empty;
        depth = 0;
        return TryTransformFlatPointToScene(objectIndex, flatPoint, sceneOffset, out var scenePoint)
            && TryProjectScenePosition(scenePoint, out screen, out depth);
    }

    private Vector3 GetReference3DExtrusionVector(int objectIndex)
    {
        if (!ReferenceEquals(_sceneCompositionResultScene, Scene)
            || _sceneCompositionResult?.TryGetPose(objectIndex, out var pose) != true
            || !Finite(pose.ExtrusionVector))
        {
            return Vector3.Zero;
        }
        return pose.ExtrusionVector;
    }

    private Vector3 GetReference3DExtrusionOffset(int objectIndex, bool front)
    {
        var extrusion = GetReference3DExtrusionVector(objectIndex) * 0.5f;
        return front ? -extrusion : extrusion;
    }

    private float GetReference3DObjectCenterDepth(int objectIndex)
    {
        return TryTransformFlatPointToCamera(
                objectIndex,
                new PointF(Scene.X[objectIndex], Scene.Y[objectIndex]),
                out var camera)
            ? camera.Z
            : float.NegativeInfinity;
    }

    private List<Vector3> ClipClosedNearPlane(IReadOnlyList<Vector3> source)
    {
        var result = new List<Vector3>(source.Count + 2);
        if (source.Count == 0) return result;
        var previous = source[^1];
        var previousInside = previous.Z >= ReferenceNearPlane;
        foreach (var current in source)
        {
            var currentInside = current.Z >= ReferenceNearPlane;
            if (currentInside != previousInside)
            {
                result.Add(IntersectNearPlane(previous, current));
            }
            if (currentInside) result.Add(current);
            previous = current;
            previousInside = currentInside;
        }
        return result;
    }

    private static bool ClipNearPlane(ref Vector3 start, ref Vector3 end)
    {
        var startInside = start.Z >= ReferenceNearPlane;
        var endInside = end.Z >= ReferenceNearPlane;
        if (!startInside && !endInside) return false;
        if (startInside && endInside) return true;
        var intersection = IntersectNearPlane(start, end);
        if (startInside) end = intersection;
        else start = intersection;
        return true;
    }

    private static Vector3 IntersectNearPlane(Vector3 start, Vector3 end)
    {
        var denominator = end.Z - start.Z;
        var amount = Math.Abs(denominator) <= 0.000001f
            ? 0f
            : Math.Clamp((ReferenceNearPlane - start.Z) / denominator, 0f, 1f);
        var point = Vector3.Lerp(start, end, amount);
        point.Z = ReferenceNearPlane;
        return point;
    }

    private PointF ProjectCameraVector(Vector3 point) => ProjectCameraPoint(new Point3(point.X, point.Y, point.Z));

    private Vector3 CameraToReference(Vector3 camera, bool direction)
    {
        var x = camera.X;
        var y = camera.Y;
        var z = direction ? camera.Z : camera.Z - _referenceDistance;
        var pitchCos = MathF.Cos(EffectiveReferencePitch);
        var pitchSin = MathF.Sin(EffectiveReferencePitch);
        var worldY = y * pitchCos + z * pitchSin;
        var yawZ = -y * pitchSin + z * pitchCos;
        var yawCos = MathF.Cos(EffectiveReferenceYaw);
        var yawSin = MathF.Sin(EffectiveReferenceYaw);
        var worldX = x * yawCos + yawZ * yawSin;
        var worldZ = -x * yawSin + yawZ * yawCos;
        if (direction) return new Vector3(worldX, worldY, worldZ);
        return new Vector3(
            worldX + _referenceTargetX,
            worldY + _referenceTargetY,
            worldZ + _referenceTargetZ);
    }

    private static PointF[] SampleCubic(PointF start, PointF control1, PointF control2, PointF end)
    {
        const int segments = 32;
        var points = new PointF[segments + 1];
        for (var index = 0; index <= segments; index++)
        {
            var t = index / (float)segments;
            var u = 1f - t;
            points[index] = new PointF(
                u * u * u * start.X
                    + 3f * u * u * t * control1.X
                    + 3f * u * t * t * control2.X
                    + t * t * t * end.X,
                u * u * u * start.Y
                    + 3f * u * u * t * control1.Y
                    + 3f * u * t * t * control2.Y
                    + t * t * t * end.Y);
        }
        return points;
    }

    private static PointF[] SampleCubicSegments(IReadOnlyList<CubicBoundarySegment> segments)
    {
        if (segments.Count == 0) return [];
        var result = new List<PointF>(segments.Count * 32 + 1);
        foreach (var segment in segments)
        {
            var points = SampleCubic(segment.Start, segment.Control1, segment.Control2, segment.End);
            if (result.Count > 0) result.AddRange(points.Skip(1));
            else result.AddRange(points);
        }
        return result.ToArray();
    }

    private static PointF[] SampleBezierContour(IReadOnlyList<PathBezierNode> nodes)
    {
        if (nodes.Count < 2) return [];
        var result = new List<PointF>(nodes.Count * 32 + 1);
        for (var index = 0; index < nodes.Count; index++)
        {
            var current = nodes[index];
            var next = nodes[(index + 1) % nodes.Count];
            var points = SampleCubic(
                current.Anchor,
                current.OutgoingControl,
                next.IncomingControl,
                next.Anchor);
            if (result.Count > 0) result.AddRange(points.Skip(1));
            else result.AddRange(points);
        }
        return result.ToArray();
    }

    private static bool PointInProjectedFill(Point point, IReadOnlyList<Reference3DProjectedContour> contours)
    {
        return PointInProjectedFill(new PointF(point.X, point.Y), contours);
    }

    private static bool PointInProjectedFill(PointF point, IReadOnlyList<Reference3DProjectedContour> contours)
    {
        var inside = false;
        foreach (var contour in contours)
        {
            if (!contour.Closed || contour.Points.Length < 3) continue;
            if (PointInPolygon(point, contour.Points)) inside = !inside;
        }
        return inside;
    }

    private static bool PointInPolygon(PointF point, IReadOnlyList<PointF> polygon)
    {
        var inside = false;
        var previous = polygon[^1];
        foreach (var current in polygon)
        {
            var crosses = (current.Y > point.Y) != (previous.Y > point.Y)
                && point.X < (previous.X - current.X) * (point.Y - current.Y)
                    / (previous.Y - current.Y) + current.X;
            if (crosses) inside = !inside;
            previous = current;
        }
        return inside;
    }

    private static float DistanceToPolyline(Point point, IReadOnlyList<PointF> points, bool closed)
    {
        if (points.Count == 0) return float.MaxValue;
        if (points.Count == 1) return ReferencePointDistance(point, points[0]);
        var minimum = float.MaxValue;
        for (var index = 1; index < points.Count; index++)
        {
            minimum = Math.Min(minimum, DistanceToSegment(point, points[index - 1], points[index]));
        }
        if (closed) minimum = Math.Min(minimum, DistanceToSegment(point, points[^1], points[0]));
        return minimum;
    }

    private static float DistanceToSegment(Point point, PointF start, PointF end)
    {
        return DistanceToSegment(new PointF(point.X, point.Y), start, end);
    }

    private static float DistanceToSegment(PointF point, PointF start, PointF end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var lengthSquared = dx * dx + dy * dy;
        if (lengthSquared <= 0.000001f) return ReferencePointDistance(point, start);
        var amount = Math.Clamp(
            ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared,
            0f,
            1f);
        var projected = new PointF(start.X + dx * amount, start.Y + dy * amount);
        return ReferencePointDistance(point, projected);
    }

    private static float ReferencePointDistance(Point point, PointF target)
    {
        var dx = point.X - target.X;
        var dy = point.Y - target.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private static float ReferencePointDistance(PointF left, PointF right)
    {
        var dx = left.X - right.X;
        var dy = left.Y - right.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private static float SpatialGizmoPolygonArea(IReadOnlyList<PointF> polygon)
    {
        if (polygon.Count < 3) return 0;
        double area = 0;
        var previous = polygon[^1];
        foreach (var current in polygon)
        {
            area += (double)previous.X * current.Y - (double)current.X * previous.Y;
            previous = current;
        }
        return (float)(Math.Abs(area) * 0.5d);
    }

    private bool IsSpatialGizmoPlaneHandleStable(
        IReadOnlyList<PointF> polygon,
        Vector3 firstAxis,
        Vector3 secondAxis)
    {
        if (polygon.Count != 4) return false;
        var area = SpatialGizmoPolygonArea(polygon);
        var maximumEdge = 0f;
        var center = PointF.Empty;
        for (var index = 0; index < polygon.Count; index++)
        {
            maximumEdge = Math.Max(
                maximumEdge,
                ReferencePointDistance(polygon[index], polygon[(index + 1) % polygon.Count]));
            center.X += polygon[index].X;
            center.Y += polygon[index].Y;
        }
        if (maximumEdge <= 0.001f
            || area < 12f * SpatialGizmoDpiScale * SpatialGizmoDpiScale
            || area / maximumEdge < SpatialGizmoMinimumPlaneAltitudePixels * SpatialGizmoDpiScale)
        {
            return false;
        }

        var normal = Vector3.Cross(firstAxis, secondAxis);
        if (normal.LengthSquared() <= 0.000001f) return false;
        normal = Vector3.Normalize(normal);
        center.X /= polygon.Count;
        center.Y /= polygon.Count;
        return TryGetReferenceRay(Point.Round(center), out var ray)
            && Math.Abs(Vector3.Dot(ray.Direction, normal)) >= SpatialGizmoMinimumPlaneRayDot;
    }

    private static bool PointInSpatialGizmoPolygon(Point point, IReadOnlyList<PointF> polygon)
    {
        if (polygon.Count < 3) return false;
        var hasPositive = false;
        var hasNegative = false;
        var previous = polygon[^1];
        foreach (var current in polygon)
        {
            var cross = (current.X - previous.X) * (point.Y - previous.Y)
                - (current.Y - previous.Y) * (point.X - previous.X);
            hasPositive |= cross > 0.001f;
            hasNegative |= cross < -0.001f;
            if (hasPositive && hasNegative) return false;
            previous = current;
        }
        return true;
    }

    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static readonly SpatialTransformAxis[] SpatialAxes =
        [SpatialTransformAxis.X, SpatialTransformAxis.Y, SpatialTransformAxis.Z];

    private static readonly SpatialTransformAxis[] SpatialPlanes =
        [SpatialTransformAxis.XY, SpatialTransformAxis.XZ, SpatialTransformAxis.YZ];

    internal readonly record struct SpatialGizmoScreenGeometry(
        PointF Origin,
        PointF[] Endpoints,
        PointF[][] Rings,
        PointF[][] PlaneHandles,
        float WorldLength,
        SpatialGizmoBasis Basis);
}
