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
    float AverageDepth);

internal readonly record struct Reference3DSourceContour(PointF[] Points, bool Closed);

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

    public Reference3DProjectedContour[]? FragmentClip { get; init; }

    public Reference3DProjectedContour[]? OcclusionContours { get; init; }

    public int FragmentSlot { get; init; }

    public ulong StableFragmentIdentity { get; init; }

    public int SecondaryObjectIndex { get; init; } = -1;

    public int EdgeArgb { get; init; }

    public float EdgeWidth { get; init; }

    public bool EdgeStartCap { get; init; } = true;

    public bool EdgeEndCap { get; init; } = true;
}

internal readonly record struct Reference3DProjectedSolid(
    Reference3DProjectedContour[] FrontContours,
    Reference3DProjectedContour[] BackContours,
    Reference3DProjectedContour[] SideSurfaces,
    Reference3DSurfacePlaneKey[] SidePlaneKeys,
    Reference3DProjectedContour[] SelectionEdges,
    bool HasExtrusion);

internal readonly record struct Reference3DRenderGroup(
    Reference3DRenderItem[] Items,
    float AverageDepth,
    Reference3DSurfacePlaneKey PlaneKey,
    int StableSlot);

internal sealed partial class StageControl
{
    internal const float ReferenceNearPlane = 120f;
    internal const float ReferencePerspectiveFocalLength = 12_000f;
    private const float ReferenceMaximumPitch = 1.5f;
    private const float SpatialGizmoTargetPixels = 72f;
    private const float SpatialGizmoHitRadiusPixels = 8f;
    private const int SpatialRotationRingSegments = 48;
    private const float ReferenceExtrusionEpsilon = 0.0001f;
    private const float ReferenceExtrusionCornerCosine = 0.94f;
    private const float ReferenceSurfaceOverlapTolerancePixels = 0.75f;
    private const float ReferenceSurfaceNormalEpsilon = 0.00000001f;
    private const double ReferenceSurfaceNormalQuantization = 100_000d;
    private const double ReferenceSurfacePlaneQuantization = 1_000d;
    private const float SpatialGizmoMinimumPlaneAltitudePixels = 3f;
    private const int MaximumReference3DRenderPlanCacheEntries = 64;
    internal const float SpatialGizmoMinimumPlaneRayDot = 0.06f;

    internal static Color Reference3DSelectionLineColor { get; } = Color.FromArgb(255, 255, 145, 44);
    internal static Color Reference3DSelectionHaloColor { get; } = Color.FromArgb(112, 112, 48, 8);

    private SceneCompositionResult? _sceneCompositionResult;
    private VectorScene? _sceneCompositionResultScene;
    private bool _sceneCompositionHasSpatialPoses;
    private SceneCompositionMaskClip[] _sceneCompositionMaskClips = [];
    private ReferenceViewDirection _reference2DViewDirection = ReferenceViewDirection.Front;
    private int[] _reference3DSelectedObjects = [];
    private bool _spatialTransformGizmoVisible;
    private Vector3 _spatialTransformGizmoOrigin;
    private SpatialTransformMode _spatialTransformGizmoMode;
    private readonly List<Reference3DRenderPlanCacheEntry> _reference3DRenderPlanCache = [];
    private long _reference3DRenderPlanEpoch;

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
        long Epoch,
        bool SubstituteOutlineItems);

    private sealed class Reference3DRenderPlanCacheEntry
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
    }

    internal SceneCompositionResult? SceneCompositionResult => _sceneCompositionResult;
    internal VectorScene? SceneCompositionResultScene => _sceneCompositionResultScene;
    internal IReadOnlyList<SceneCompositionMaskClip> SceneCompositionMaskClips => _sceneCompositionMaskClips;
    internal ReferenceViewDirection Reference2DViewDirection => _reference2DViewDirection;
    // Underlay and drag-preview passes temporarily replace Scene but must retain the editable front camera.
    private bool UsesSpatialFrontView => ReferenceDimension == SceneDimension.TwoD
        && _reference2DViewDirection == ReferenceViewDirection.Front
        && _sceneCompositionHasSpatialPoses;
    internal bool UsesSpatialFrontProjection => UsesSpatialFrontView
        && ReferenceEquals(_sceneCompositionResultScene, Scene);
    internal bool UsesReferenceProjection => ReferenceDimension == SceneDimension.ThreeD
        || _reference2DViewDirection != ReferenceViewDirection.Front
        || UsesSpatialFrontProjection;
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
    internal long Reference3DRenderPlanBuildCount { get; private set; }

    internal void SetSceneCompositionResult(
        SceneCompositionResult? result,
        VectorScene? targetScene = null)
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
            && (_sceneCompositionHasSpatialPoses != nextHasSpatialPoses
                || !ReferenceEquals(_sceneCompositionResultScene, nextTarget)))
        {
            CompleteReferenceCameraTransition(invalidate: false);
        }
        _sceneCompositionResult = result;
        _sceneCompositionResultScene = nextTarget;
        _sceneCompositionHasSpatialPoses = nextHasSpatialPoses;
        _reference3DSelectedObjects = _reference3DSelectedObjects
            .Where(index => nextTarget is not null && (uint)index < nextTarget.ObjectCount)
            .ToArray();
        Invalidate();
    }

    internal void SetSceneCompositionMaskClips(IEnumerable<SceneCompositionMaskClip>? clips)
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
        Invalidate();
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
        IReadOnlyCollection<int>? selectedObjectIndices = null)
    {
        if (!Finite(origin))
        {
            ClearSpatialTransformGizmo();
            return;
        }

        if (selectedObjectIndices is not null) SetReference3DSelection(selectedObjectIndices);
        var changed = !_spatialTransformGizmoVisible
            || _spatialTransformGizmoOrigin != origin
            || _spatialTransformGizmoMode != mode;
        _spatialTransformGizmoVisible = true;
        _spatialTransformGizmoOrigin = origin;
        _spatialTransformGizmoMode = mode;
        if (changed) InvalidateOverlay();
    }

    internal void ClearSpatialTransformGizmo()
    {
        if (!_spatialTransformGizmoVisible) return;
        _spatialTransformGizmoVisible = false;
        _spatialTransformGizmoOrigin = default;
        InvalidateOverlay();
    }

    internal SpatialTransformHandleHit HitTestSpatialTransformGizmo(Point screen)
    {
        if (!_spatialTransformGizmoVisible
            || ReferenceDimension != SceneDimension.ThreeD
            || !TryGetSpatialGizmoScreenGeometry(out var geometry))
        {
            return SpatialTransformHandleHit.None;
        }

        if (_spatialTransformGizmoMode == SpatialTransformMode.Rotate)
        {
            foreach (var axis in SpatialAxes)
            {
                var ring = geometry.Rings[(int)axis - 1];
                if (ring.Length > 1
                    && DistanceToPolyline(screen, ring, closed: true) <= SpatialGizmoHitRadiusPixels)
                {
                    return new SpatialTransformHandleHit(_spatialTransformGizmoMode, axis);
                }
            }

            return SpatialTransformHandleHit.None;
        }

        if (_spatialTransformGizmoMode == SpatialTransformMode.Scale
            && ReferencePointDistance(screen, geometry.Origin) <= SpatialGizmoHitRadiusPixels)
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

        if (bestDistance <= SpatialGizmoHitRadiusPixels)
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
            var candidate = item.ObjectIndex;
            if (IsObjectHiddenForRendering(Scene, candidate)) continue;
            if (!IsPointWithinReference3DFragment(screen, item)) continue;
            if (!IsProjectedPointVisibleThroughMasks(screen, candidate)) continue;

            if (item.Kind is Reference3DRenderKind.Back or Reference3DRenderKind.Side
                && PointInProjectedFill(screen, item.Contours))
            {
                objectIndex = candidate;
                return true;
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
        return Enumerable.Range(0, Scene.ObjectCount)
            .Where(index => Scene.ObjectLayer[index] == layer && Scene.IsObjectActive(index, Frame))
            .OrderByDescending(GetReference3DObjectCenterDepth)
            .ThenBy(index => Scene.ObjectOrder[index])
            .ThenBy(index => Scene.ObjectSubOrder[index])
            .ThenBy(index => index)
            .ToArray();
    }

    internal Reference3DProjectedContour[] GetReference3DProjectedContours(int objectIndex)
    {
        if ((uint)objectIndex >= Scene.ObjectCount) return [];
        return ProjectReference3DContours(
            objectIndex,
            GetReference3DSourceContours(Scene, objectIndex),
            GetReference3DExtrusionOffset(objectIndex, front: true));
    }

    internal Reference3DProjectedSolid GetReference3DProjectedSolid(int objectIndex)
    {
        if ((uint)objectIndex >= Scene.ObjectCount)
        {
            return new Reference3DProjectedSolid([], [], [], [], [], false);
        }

        var frontContours = GetReference3DProjectedContours(objectIndex);
        var extrusion = GetReference3DExtrusionVector(objectIndex);
        var hasExtrusion = Finite(extrusion)
            && extrusion.LengthSquared() > ReferenceExtrusionEpsilon * ReferenceExtrusionEpsilon;
        if (!hasExtrusion)
        {
            return new Reference3DProjectedSolid(frontContours, [], [], [], frontContours, false);
        }

        var sourceContours = GetReference3DExtrusionSourceContours(objectIndex);
        if (sourceContours.Length == 0)
        {
            return new Reference3DProjectedSolid(frontContours, [], [], [], frontContours, false);
        }

        var frontOffset = extrusion * -0.5f;
        var backOffset = extrusion * 0.5f;
        var bodyFrontContours = ProjectReference3DContours(objectIndex, sourceContours, frontOffset);
        var backContours = ProjectReference3DContours(objectIndex, sourceContours, backOffset);
        var (sideSurfaces, sidePlaneKeys) = BuildReference3DSideSurfaces(
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
            sidePlaneKeys,
            selectionEdges,
            true);
    }

    internal Reference3DRenderItem[] GetReference3DLayerRenderItems(IReadOnlyList<int> objectIndices)
    {
        var items = new List<Reference3DRenderItem>(objectIndices.Count * 4);
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
            var hasFill = SceneRenderOrder.HasFill(shape);
            var hasStroke = SceneRenderOrder.HasStroke(shape, Scene.Stroke[objectIndex]);
            var solid = GetReference3DProjectedSolid(objectIndex);
            var extrusion = GetReference3DExtrusionVector(objectIndex);
            var frontPlane = GetReference3DSurfacePlane(objectIndex, extrusion * -0.5f);
            var frontPlaneKey = CreateReference3DSurfacePlaneKey(frontPlane);
            var backPlaneKey = GetReference3DSurfacePlaneKey(
                objectIndex,
                extrusion * 0.5f,
                reverseNormal: true);
            if (solid.HasExtrusion && GetReference3DExtrusionColor(objectIndex).A > 0)
            {
                var backContours = solid.BackContours
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
                        0));
                }

                for (var surfaceIndex = 0; surfaceIndex < solid.SideSurfaces.Length; surfaceIndex++)
                {
                    var surface = solid.SideSurfaces[surfaceIndex];
                    items.Add(new Reference3DRenderItem(
                        objectIndex,
                        layerIndex,
                        Reference3DRenderKind.Side,
                        [surface],
                        surface.AverageDepth,
                        surfaceIndex < solid.SidePlaneKeys.Length
                            ? solid.SidePlaneKeys[surfaceIndex]
                            : default,
                        objectSlot,
                        surfaceIndex));
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
                    Plane = frontPlane
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
                    OcclusionContours = hasFill
                        ? null
                        : GetReference3DProjectedStrokeOcclusionContours(objectIndex)
                });
            }
        }

        items.Sort(CompareReference3DRenderItems);
        return items.ToArray();
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

    private Reference3DProjectedContour[] ProjectReference3DContours(
        int objectIndex,
        IReadOnlyList<Reference3DSourceContour> sourceContours,
        Vector3 sceneOffset)
    {
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
        if ((uint)objectIndex >= Scene.ObjectCount || Scene.Stroke[objectIndex] <= 0) return [];
        var width = GetReference3DSourceStrokeWidth(objectIndex, Scene.Stroke[objectIndex]);
        var result = new List<Reference3DSourceContour>();
        foreach (var centerline in GetReference3DSourceContours(objectIndex))
        {
            if (centerline.Closed || centerline.Points.Length < 2) continue;
            foreach (var contour in FreehandStrokeProcessor.CreateBrushOutlines(centerline.Points, width))
            {
                if (contour.Length >= 3) result.Add(new Reference3DSourceContour(contour, true));
            }
        }
        return result.ToArray();
    }

    private Reference3DProjectedContour[] GetReference3DProjectedStrokeOcclusionContours(
        int objectIndex)
    {
        if (Scene.ShapeKind[objectIndex] == ShapeKind.Line && Scene.HasGradient(objectIndex))
        {
            var sourceContours = GetReference3DStrokeOutlineSourceContours(objectIndex);
            return sourceContours.Length == 0
                ? []
                : ProjectReference3DContours(
                    objectIndex,
                    sourceContours,
                    GetReference3DExtrusionOffset(objectIndex, front: true));
        }

        var width = GetReference3DStrokeWidth(objectIndex, Scene.Stroke[objectIndex]);
        if (width <= 0) return [];
        var result = new List<Reference3DProjectedContour>();
        foreach (var centerline in GetReference3DProjectedContours(objectIndex))
        {
            if (centerline.Closed)
            {
                result.AddRange(CreateReference3DClosedStrokeOcclusionContours(centerline, width));
                continue;
            }
            if (centerline.Points.Length < 2) continue;
            foreach (var outline in FreehandStrokeProcessor.CreateBrushOutlines(centerline.Points, width))
            {
                if (outline.Length >= 3)
                {
                    result.Add(new Reference3DProjectedContour(
                        outline,
                        true,
                        centerline.AverageDepth));
                }
            }
        }
        return result.ToArray();
    }

    private (Reference3DProjectedContour[] Surfaces, Reference3DSurfacePlaneKey[] PlaneKeys)
        BuildReference3DSideSurfaces(
        int objectIndex,
        IReadOnlyList<Reference3DSourceContour> sourceContours,
        Vector3 frontOffset,
        Vector3 backOffset)
    {
        var surfaces = new List<Reference3DProjectedContour>();
        var planeKeys = new List<Reference3DSurfacePlaneKey>();
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
                    planeKeys.Add(GetReference3DSideSurfacePlaneKey(
                        objectIndex,
                        source.Points[segment],
                        source.Points[next],
                        frontOffset,
                        backOffset));
                }
            }
        }
        return (surfaces.ToArray(), planeKeys.ToArray());
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
        var key = CreateReference3DRenderPlanCacheKey(substituteOutlineItems);
        foreach (var entry in _reference3DRenderPlanCache)
        {
            if (entry.Matches(key, layers)) return entry.Items;
        }

        var items = layers is null
            ? Scene.HasNonNormalLayerBlendModes
                ? BuildReference3DCompositedRenderItems(substituteOutlineItems)
                : BuildReference3DSceneRenderItems(substituteOutlineItems)
            : BuildReference3DCompositeLayerRenderItems(layers, substituteOutlineItems);
        Reference3DRenderPlanBuildCount++;
        if (_reference3DRenderPlanCache.Count >= MaximumReference3DRenderPlanCacheEntries)
        {
            _reference3DRenderPlanCache.RemoveAt(0);
        }
        _reference3DRenderPlanCache.Add(new Reference3DRenderPlanCacheEntry(
            key,
            layers?.ToArray(),
            items));
        return items;
    }

    private Reference3DRenderPlanCacheKey CreateReference3DRenderPlanCacheKey(
        bool substituteOutlineItems)
    {
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
            GetReference3DLayerRenderState(Scene, Frame),
            _reference3DRenderPlanEpoch,
            substituteOutlineItems);
    }

    private static ulong GetReference3DLayerRenderState(VectorScene scene, int frame)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;

        AddInt(scene.LayerCount);
        for (var layer = 0; layer < scene.LayerCount; layer++)
        {
            AddInt(layer < scene.LayerVisible.Length && scene.LayerVisible[layer] ? 1 : 0);
            AddInt(layer < scene.LayerLocked.Length && scene.LayerLocked[layer] ? 1 : 0);
            AddInt(layer < scene.LayerOpacity.Length
                ? BitConverter.SingleToInt32Bits(scene.LayerOpacity[layer])
                : 0);
            AddInt(layer < scene.LayerBlendModes.Length ? (int)scene.LayerBlendModes[layer] : 0);
            AddInt(layer < scene.LayerColorArgb.Length ? scene.LayerColorArgb[layer] : 0);
            AddInt(layer < scene.LayerOutline.Length && scene.LayerOutline[layer] ? 1 : 0);
            AddInt(layer < scene.LayerKinds.Length ? (int)scene.LayerKinds[layer] : 0);
            AddString(layer < scene.LayerIds.Length ? scene.LayerIds[layer] : null);
            AddString(layer < scene.LayerParentIds.Length ? scene.LayerParentIds[layer] : null);
            AddString(layer < scene.LayerMaskIds.Length ? scene.LayerMaskIds[layer] : null);
            var exposure = layer < scene.LayerIds.Length
                ? scene.Timeline.EvaluateTargetExposure(scene.LayerIds[layer], frame)
                : TimelineExposure.None(frame);
            AddInt(exposure.HasContent ? 1 : 0);
            AddInt(exposure.SourceKeyframeFrame);
        }
        return hash;

        void AddInt(int value)
        {
            unchecked
            {
                hash ^= (uint)value;
                hash *= prime;
                hash ^= (uint)(value >> 16);
                hash *= prime;
            }
        }

        void AddString(string? value)
        {
            AddInt(value?.Length ?? -1);
            if (value is null) return;
            foreach (var character in value) AddInt(character);
        }
    }

    private Reference3DRenderItem[] BuildReference3DSceneRenderItems(bool substituteOutlineItems)
    {
        var result = new List<Reference3DRenderItem>(Scene.ObjectCount * 4);
        for (var layer = Scene.LayerCount - 1; layer >= 0; layer--)
        {
            if (!Scene.ShouldRenderLayerContent(layer)) continue;
            var objects = GetReference3DLayerObjects(layer);
            if (substituteOutlineItems && !Scene.GetEffectiveLayerOutlineColor(layer).IsEmpty)
            {
                AppendReference3DOutlineItems(result, layer, objects);
                continue;
            }

            result.AddRange(GetReference3DLayerRenderItems(objects));
        }

        return SortReference3DSceneRenderItems(BuildReference3DIntersectionRenderItems(result));
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
        foreach (var layer in layers)
        {
            if ((uint)layer >= Scene.LayerCount || !Scene.ShouldRenderLayerContent(layer)) continue;
            var objects = GetReference3DLayerObjects(layer);
            if (substituteOutlineItems && !Scene.GetEffectiveLayerOutlineColor(layer).IsEmpty)
            {
                AppendReference3DOutlineItems(result, layer, objects);
            }
            else
            {
                result.AddRange(GetReference3DLayerRenderItems(objects));
            }
        }
        return SortReference3DSceneRenderItems(BuildReference3DIntersectionRenderItems(result));
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
                0));
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
        IReadOnlyList<Reference3DRenderItem> items)
    {
        if (items.Count <= 1) return items.ToArray();

        var bounds = new RectangleF[items.Count];
        var hasBounds = new bool[items.Count];
        var overlapRadii = new float[items.Count];
        var planeBuckets = new Dictionary<Reference3DSurfacePlaneKey, List<int>>();
        var groups = new List<Reference3DRenderGroup>(items.Count);
        for (var itemIndex = 0; itemIndex < items.Count; itemIndex++)
        {
            var item = items[itemIndex];
            overlapRadii[itemIndex] = GetReference3DRenderItemOverlapRadius(item);
            hasBounds[itemIndex] = TryGetReference3DProjectedBounds(
                item.FragmentClip ?? item.OcclusionContours ?? item.Contours,
                overlapRadii[itemIndex],
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
            AppendReference3DPlaneGroups(
                groups,
                items,
                pair.Key,
                pair.Value,
                bounds,
                hasBounds,
                overlapRadii);
        }

        if (items.Any(item => item.FragmentClip is { Length: > 0 }
                || item.Kind == Reference3DRenderKind.IntersectionEdge))
        {
            return SortReference3DIntersectingRenderGroups(groups);
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

    private void AppendReference3DPlaneGroups(
        ICollection<Reference3DRenderGroup> destination,
        IReadOnlyList<Reference3DRenderItem> items,
        Reference3DSurfacePlaneKey planeKey,
        IReadOnlyList<int> bucket,
        IReadOnlyList<RectangleF> bounds,
        IReadOnlyList<bool> hasBounds,
        IReadOnlyList<float> overlapRadii)
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
        var boundedItems = Enumerable.Range(0, bucket.Count)
            .Where(localIndex => hasBounds[bucket[localIndex]])
            .OrderBy(localIndex => bounds[bucket[localIndex]].Left)
            .ThenBy(localIndex => bucket[localIndex])
            .ToArray();
        for (var orderIndex = 0; orderIndex < boundedItems.Length; orderIndex++)
        {
            var leftLocal = boundedItems[orderIndex];
            var leftIndex = bucket[leftLocal];
            var leftBounds = bounds[leftIndex];
            for (var candidateOrder = orderIndex + 1; candidateOrder < boundedItems.Length; candidateOrder++)
            {
                var rightLocal = boundedItems[candidateOrder];
                var rightIndex = bucket[rightLocal];
                var rightBounds = bounds[rightIndex];
                if (rightBounds.Left > leftBounds.Right + ReferenceSurfaceOverlapTolerancePixels) break;
                if (!Reference3DProjectedBoundsOverlap(leftBounds, rightBounds)
                    || !Reference3DFragmentClipsOverlap(items[leftIndex], items[rightIndex])
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
        if (!_spatialTransformGizmoVisible
            || !TryProjectScenePosition(_spatialTransformGizmoOrigin, out var origin, out var originDepth))
        {
            return false;
        }

        var scale = 0.035f * _referenceZoomScale;
        var perspective = ReferencePerspectiveScale(originDepth);
        var length = Math.Clamp(
            SpatialGizmoTargetPixels / Math.Max(0.0001f, scale * perspective),
            20f,
            100_000f);
        var endpoints = new PointF[3];
        for (var index = 0; index < SpatialAxes.Length; index++)
        {
            var sceneEndpoint = _spatialTransformGizmoOrigin + AxisVector(SpatialAxes[index]) * length;
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
                    var offset = RingOffset(SpatialAxes[axisIndex], angle) * length * 0.82f;
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
                var (firstAxis, secondAxis) = PlaneAxisVectors(SpatialPlanes[planeIndex]);
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

        geometry = new SpatialGizmoScreenGeometry(origin, endpoints, rings, planeHandles, length);
        return true;
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
        _spatialTransformGizmoVisible = false;
        _spatialTransformGizmoOrigin = default;
    }

    private void InvalidateReference3DRenderPlanCache()
    {
        unchecked
        {
            _reference3DRenderPlanEpoch++;
        }
        _reference3DRenderPlanCache.Clear();
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
        var camera = new List<Vector3>(source.Count);
        var count = source.Count;
        if (count > 2 && ReferencePointDistance(source[0], source[^1]) <= 0.0001f) count--;
        for (var index = 0; index < count; index++)
        {
            if (!TryTransformFlatPointToCamera(objectIndex, source[index], sceneOffset, out var point)) return false;
            camera.Add(point);
        }

        return TryProjectCameraPolygon(camera, out projected);
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
        for (var index = 1; index < source.Count; index++)
        {
            if (!TryTransformFlatPointToCamera(objectIndex, source[index - 1], sceneOffset, out var start)
                || !TryTransformFlatPointToCamera(objectIndex, source[index], sceneOffset, out var end)
                || !ClipNearPlane(ref start, ref end))
            {
                continue;
            }

            var a = ProjectCameraVector(start);
            var b = ProjectCameraVector(end);
            if (!float.IsFinite(a.X) || !float.IsFinite(a.Y)
                || !float.IsFinite(b.X) || !float.IsFinite(b.Y))
            {
                continue;
            }
            destination.Add(new Reference3DProjectedContour([a, b], false, (start.Z + end.Z) * 0.5f));
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

    private Reference3DSurfacePlaneKey GetReference3DSideSurfacePlaneKey(
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
        return CreateReference3DSurfacePlaneKey(normal, frontStart);
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

    private static Vector3 AxisVector(SpatialTransformAxis axis) => axis switch
    {
        SpatialTransformAxis.X => Vector3.UnitX,
        SpatialTransformAxis.Y => Vector3.UnitY,
        SpatialTransformAxis.Z => Vector3.UnitZ,
        _ => Vector3.Zero
    };

    private static Vector3 RingOffset(SpatialTransformAxis axis, float angle) => axis switch
    {
        SpatialTransformAxis.X => new Vector3(0, MathF.Cos(angle), MathF.Sin(angle)),
        SpatialTransformAxis.Y => new Vector3(MathF.Cos(angle), 0, MathF.Sin(angle)),
        SpatialTransformAxis.Z => new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0),
        _ => Vector3.Zero
    };

    private static (Vector3 First, Vector3 Second) PlaneAxisVectors(SpatialTransformAxis axis) => axis switch
    {
        SpatialTransformAxis.XY => (Vector3.UnitX, Vector3.UnitY),
        SpatialTransformAxis.XZ => (Vector3.UnitX, Vector3.UnitZ),
        SpatialTransformAxis.YZ => (Vector3.UnitY, Vector3.UnitZ),
        _ => (Vector3.Zero, Vector3.Zero)
    };

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
            || area < 12f
            || area / maximumEdge < SpatialGizmoMinimumPlaneAltitudePixels)
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
        float WorldLength);
}
