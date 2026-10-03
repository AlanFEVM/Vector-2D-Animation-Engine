using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;

namespace VectorAnimationEngine;

internal readonly record struct SceneCompositionObjectOwner(
    string InstanceId,
    string DrawingObjectId,
    string RootInstanceId = "");

internal readonly record struct SceneCompositionObjectPose(
    Matrix4x4 FlatToScene,
    Vector3 ExtrusionVector = default,
    float FlatStrokeScale = 1f)
{
    public static SceneCompositionObjectPose Identity { get; } = new(Matrix4x4.Identity);
}

internal readonly record struct SceneCompositionBuildMetrics(
    double BucketMilliseconds,
    double SetupMilliseconds,
    double AppendMilliseconds,
    double FinalizeMilliseconds);

internal sealed class SceneCompositionResult
{
    private readonly SceneCompositionObjectOwner[] _owners;
    private readonly SceneCompositionObjectPose[] _poses;
    private readonly SpatialOpticalMaterial[] _materials;
    private IReadOnlyDictionary<int, SceneCompositionObjectOwner>? _ownerView;

    public SceneCompositionResult(
        SceneCompositionObjectOwner[]? owners = null,
        SceneCompositionObjectPose[]? poses = null,
        SpatialOpticalMaterial[]? materials = null)
    {
        _owners = owners ?? [];
        _poses = poses ?? Enumerable.Repeat(SceneCompositionObjectPose.Identity, _owners.Length).ToArray();
        _materials = materials ?? Enumerable.Repeat(SpatialOpticalMaterial.Default, _owners.Length).ToArray();
        if (_poses.Length != _owners.Length || _materials.Length != _owners.Length)
        {
            throw new ArgumentException("Composition metadata must align with composition owners.");
        }
    }

    public static SceneCompositionResult Empty { get; } = new();
    public IReadOnlyDictionary<int, SceneCompositionObjectOwner> ObjectOwners => _ownerView ??= new IndexedOwnerMap(_owners);
    public IReadOnlyList<SceneCompositionObjectPose> ObjectPoses => _poses;
    public IReadOnlyList<SpatialOpticalMaterial> ObjectMaterials => _materials;

    public bool TryGetOwner(int objectIndex, out SceneCompositionObjectOwner owner)
    {
        if ((uint)objectIndex < _owners.Length)
        {
            owner = _owners[objectIndex];
            return true;
        }

        owner = default;
        return false;
    }

    public bool TryGetPose(int objectIndex, out SceneCompositionObjectPose pose)
    {
        if ((uint)objectIndex < _poses.Length)
        {
            pose = _poses[objectIndex];
            return true;
        }

        pose = default;
        return false;
    }

    public bool TryGetMaterial(int objectIndex, out SpatialOpticalMaterial material)
    {
        if ((uint)objectIndex < _materials.Length)
        {
            material = _materials[objectIndex];
            return true;
        }

        material = SpatialOpticalMaterial.Default;
        return false;
    }

    private sealed class IndexedOwnerMap(SceneCompositionObjectOwner[] owners) : IReadOnlyDictionary<int, SceneCompositionObjectOwner>
    {
        public int Count => owners.Length;
        public IEnumerable<int> Keys => Enumerable.Range(0, owners.Length);
        public IEnumerable<SceneCompositionObjectOwner> Values => owners;
        public SceneCompositionObjectOwner this[int key] => (uint)key < owners.Length ? owners[key] : throw new KeyNotFoundException();

        public bool ContainsKey(int key) => (uint)key < owners.Length;

        public bool TryGetValue(int key, out SceneCompositionObjectOwner value)
        {
            if ((uint)key < owners.Length)
            {
                value = owners[key];
                return true;
            }

            value = default;
            return false;
        }

        public IEnumerator<KeyValuePair<int, SceneCompositionObjectOwner>> GetEnumerator()
        {
            for (var index = 0; index < owners.Length; index++) yield return new KeyValuePair<int, SceneCompositionObjectOwner>(index, owners[index]);
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

internal static class SceneCompositionBuilder
{
    private const int PreparedChunkSize = 2048;

    public static SceneCompositionBuildMetrics LastBuildMetrics { get; private set; }
    internal static int LastActiveObjectBucketCacheHits { get; private set; }
    internal static int LastActiveObjectBucketCacheMisses { get; private set; }

    private static readonly ConditionalWeakTable<VectorScene, ActiveObjectBucketCache> ActiveObjectBucketCaches = new();

    private readonly record struct CompositionLayer(
        VectorScene Source,
        int SourceLayer,
        int SourceFrame,
        string Name,
        Matrix3x2 Transform,
        Matrix4x4 SpatialTransform,
        bool SpatialIsPlanar,
        Vector3 ExtrusionVector,
        float InheritedOpacity,
        float Alpha,
        int TintArgb,
        SpatialOpticalMaterial OpticalMaterial,
        LayerBlendMode BlendMode,
        bool Outline,
        int OutlineColorArgb,
        SceneCompositionObjectOwner Owner,
        bool SyntheticGroup = false,
        string GroupId = "",
        string ParentGroupId = "",
        bool PreserveSourceParent = false,
        DistortWarp[]? Distortions = null)
    {
        public bool IsCollisionTerrain { get; init; }
        public SymbolFilters Filters { get; init; }
    }

    private readonly record struct SourceFrameKey(VectorScene Source, int Frame);

    private readonly record struct CompositionLayerGroupKey(VectorScene Source, string InstanceId);

    private readonly record struct CompositionWorkItem(
        VectorScene Source,
        int SourceObject,
        int DestinationLayer,
        Matrix3x2 Transform,
        SceneCompositionObjectPose Pose,
        float Alpha,
        int TintArgb,
        SpatialOpticalMaterial OpticalMaterial,
        SceneCompositionObjectOwner Owner,
        DistortWarp[]? Distortions = null);

    private enum PreparedCompositionKind : byte
    {
        Primitive,
        Path,
        Freehand,
        MixingStroke,
        Curve,
        ImportedSvg,
        Bitmap,
        Text
    }

    private readonly record struct PreparedCompositionObject(
        PreparedCompositionKind Kind,
        int DestinationLayer,
        ShapeKind Shape,
        PointF Center,
        SizeF Size,
        float Angle,
        float Stroke,
        int FillArgb,
        int StrokeArgb,
        uint Atoms,
        PointF Start,
        PointF Control,
        PointF End,
        PointF[] Points,
        PointF[][] Contours,
        LineEndpointStyle StartEndpointStyle = LineEndpointStyle.Round,
        LineEndpointStyle EndEndpointStyle = LineEndpointStyle.Round)
    {
        public bool UseSourceLocalPathGeometry { get; init; }
        public bool LinearGradientEnabled { get; init; }
        public GradientKind GradientKind { get; init; }
        public int GradientStartArgb { get; init; }
        public int GradientEndArgb { get; init; }
        public PointF GradientStart { get; init; }
        public PointF GradientEnd { get; init; }
        public GradientStop[] GradientStops { get; init; } = [];
        public PointF[] GradientPath { get; init; } = [];
        public MixingBrushTrajectorySample[] MixingSamples { get; init; } = [];
        public MixingBrushRegionData? MixingRegion { get; init; }
        public PointF[][] ShapeGradientMappingContours { get; init; } = [];
        public PathBezierNode[][] PathBezierContours { get; init; } = [];
        public PathBezierNode[] FreehandBezierNodes { get; init; } = [];
        public int ShapeVertexCount { get; init; }
        public PointF Control2 { get; init; }
        public string ImportedSvgSource { get; init; } = "";
        public string ImportedSvgName { get; init; } = "";
        public BitmapObjectData? BitmapObjectData { get; init; }
        public TextObjectData? TextObjectData { get; init; }
        public bool FillAutoMergeProtected { get; init; }
        public DistortWarp[] Distortions { get; init; } = [];
    }

    public static SceneCompositionResult Build(
        VectorScene destination,
        SceneDefinition? sceneDefinition,
        IReadOnlyList<DrawingObjectDefinition> drawingObjects,
        int frame,
        decimal parentFps = 30m)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(drawingObjects);

        if (sceneDefinition is null)
        {
            destination.CreateEmpty();
            return SceneCompositionResult.Empty;
        }

        var definitionsById = DefinitionsById(drawingObjects);
        var layers = new List<CompositionLayer>();
        var timeline = sceneDefinition.Timeline;
        var localFrame = Math.Clamp(frame, 0, Math.Max(0, sceneDefinition.FrameCount - 1));
        foreach (var sceneLayer in sceneDefinition.Layers)
        {
            if (!sceneLayer.Visible
                || !timeline.EvaluateTargetExposure(sceneLayer.Id, localFrame).HasContent)
            {
                continue;
            }

            var firstLayer = layers.Count;
            foreach (var instance in sceneDefinition.InstancesInLayer(sceneLayer.Id))
            {
                var state = instance.EvaluateState(localFrame);
                if (!state.Visible
                    || !definitionsById.TryGetValue(instance.DrawingObjectId, out var drawingObject))
                {
                    continue;
                }

                var spatialTransform = InstanceSpatialMatrix(drawingObject, state);
                CollectDrawingObjectLayers(
                    drawingObject,
                    instance,
                    InstanceMatrix(drawingObject, state),
                    spatialTransform,
                    InstanceSpatialIsPlanar(state),
                    CreateRootExtrusionVector(state, spatialTransform),
                    localFrame,
                    parentFps,
                    definitionsById,
                    layers,
                    new HashSet<string>(StringComparer.Ordinal),
                    $"{instance.Name} / {sceneLayer.Name}",
                    instance.Id,
                    inheritedOutline: sceneLayer.Outline,
                    inheritedOutlineColorArgb: sceneLayer.ColorArgb);
            }

            if (sceneLayer.BlendMode != LayerBlendMode.Normal && layers.Count > firstLayer)
            {
                WrapCompositionRangeInGroup(
                    layers,
                    firstLayer,
                    sceneLayer.Name,
                    sceneLayer.BlendMode,
                    sceneLayer.Outline,
                    sceneLayer.ColorArgb);
            }
        }

        return BuildLayers(destination, layers, Math.Max(AnimationTimeline.DefaultDuration, sceneDefinition.FrameCount));
    }

    public static SceneCompositionResult BuildDrawingObjectChildren(
        VectorScene destination,
        DrawingObjectDefinition? drawingObject,
        IReadOnlyList<DrawingObjectDefinition> drawingObjects,
        int frame,
        decimal parentFps = 30m)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(drawingObjects);
        if (drawingObject is null || drawingObject.Instances.Count == 0)
        {
            destination.CreateEmpty();
            return SceneCompositionResult.Empty;
        }

        return BuildDrawingObjectChildrenCore(destination, drawingObject, drawingObjects, frame, parentFps, hostLayerFilter: null);
    }

    public static SceneCompositionResult BuildDrawingObjectInstanceForBreakApart(
        VectorScene destination,
        DrawingObjectDefinition? container,
        DrawingObjectInstanceDefinition? instance,
        IReadOnlyList<DrawingObjectDefinition> drawingObjects,
        int frame,
        decimal parentFps = 30m)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(drawingObjects);
        if (container is null || instance is null)
        {
            destination.CreateEmpty();
            return SceneCompositionResult.Empty;
        }

        var localFrame = Math.Clamp(frame, 0, Math.Max(0, container.FrameCount - 1));
        var definitionsById = DefinitionsById(drawingObjects);
        if (!IsDrawingObjectInstanceActive(container, instance, localFrame)
            || !definitionsById.TryGetValue(instance.DrawingObjectId, out var child))
        {
            destination.CreateEmpty();
            return SceneCompositionResult.Empty;
        }

        var layers = new List<CompositionLayer>();
        var hostLayer = Array.IndexOf(container.Scene.LayerIds, instance.SceneLayerId);
        var hostOutline = hostLayer >= 0 && container.Scene.IsLayerEffectivelyOutlined(hostLayer);
        if (hostLayer >= 0
            && hostLayer < container.Scene.LayerBlendModes.Length
            && container.Scene.LayerBlendModes[hostLayer] != LayerBlendMode.Normal)
        {
            throw new InvalidDataException("Break Apart cannot flatten a symbol instance from a blended host layer.");
        }
        var instanceState = instance.EvaluateState(localFrame);
        var spatialTransform = InstanceSpatialMatrix(child, instanceState);
        CollectDrawingObjectLayers(
            child,
            instance,
            InstanceMatrix(child, instanceState),
            spatialTransform,
            InstanceSpatialIsPlanar(instanceState),
            CreateRootExtrusionVector(instanceState, spatialTransform),
            localFrame,
            parentFps,
            definitionsById,
            layers,
            new HashSet<string>(StringComparer.Ordinal) { container.Id },
            instance.Name,
            instance.Id,
            inheritedOpacity: 1f,
            synchronize: false,
            inheritedOutline: hostOutline,
            inheritedOutlineColorArgb: hostOutline ? container.Scene.LayerColorArgb[hostLayer] : 0);
        if (layers.Any(layer => layer.Source.HasLayerEffects || layer.Filters.HasEnabled))
        {
            throw new InvalidDataException("Break Apart cannot flatten symbols that use folders, masks, blend modes, or filters.");
        }

        var vectorizedSources = new Dictionary<SourceFrameKey, VectorScene>();
        var materializedLayers = new CompositionLayer[layers.Count];
        for (var index = 0; index < layers.Count; index++)
        {
            var layer = layers[index];
            var key = new SourceFrameKey(layer.Source, layer.SourceFrame);
            if (!vectorizedSources.TryGetValue(key, out var vectorized))
            {
                var activeObjects = BuildActiveObjectsByLayer(layer.Source, layer.SourceFrame);
                var importedObjects = Enumerable.Range(0, layer.Source.LayerCount)
                    .Where(layerIndex => layer.Source.IsLayerEffectivelyVisible(layerIndex))
                    .SelectMany(layerIndex => activeObjects[layerIndex])
                    .Where(objectIndex => layer.Source.ShapeKind[objectIndex] == ShapeKind.ImportedSvg)
                    .ToArray();
                if (importedObjects.Length == 0)
                {
                    vectorized = layer.Source;
                }
                else
                {
                    vectorized = new VectorScene();
                    vectorized.RestoreSnapshot(layer.Source.CreateSnapshot());
                    vectorized.EditFrame = layer.SourceFrame;
                    vectorized.BreakApartImportedSvgObjects(importedObjects, createDrawingLayers: false);
                }
                vectorizedSources.Add(key, vectorized);
            }

            materializedLayers[index] = new CompositionLayer(
                vectorized,
                layer.SourceLayer,
                layer.SourceFrame,
                layer.Name,
                layer.Transform,
                layer.SpatialTransform,
                layer.SpatialIsPlanar,
                layer.ExtrusionVector,
                layer.InheritedOpacity,
                layer.Alpha,
                layer.TintArgb,
                layer.OpticalMaterial,
                layer.BlendMode,
                layer.Outline,
                layer.OutlineColorArgb,
                layer.Owner,
                layer.SyntheticGroup,
                layer.GroupId,
                layer.ParentGroupId,
                layer.PreserveSourceParent,
                layer.Distortions)
            {
                IsCollisionTerrain = layer.IsCollisionTerrain
            };
        }

        return BuildLayers(destination, materializedLayers, Math.Max(AnimationTimeline.DefaultDuration, container.FrameCount));
    }

    public static void BuildDrawingObjectOnionSkin(
        VectorScene destination,
        DrawingObjectDefinition? drawingObject,
        IReadOnlyList<DrawingObjectDefinition> drawingObjects,
        int frame,
        decimal parentFps = 30m)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(drawingObjects);
        if (drawingObject is null || drawingObject.Instances.Count == 0)
        {
            destination.CreateEmpty();
            return;
        }

        drawingObject.SynchronizeTimelineTracks();
        var scene = drawingObject.Scene;
        if (!scene.HasOnionSkinPreviewEnabled)
        {
            destination.CreateEmpty();
            return;
        }

        var currentFrame = Math.Clamp(frame, 0, Math.Max(0, drawingObject.FrameCount - 1));
        var candidates = new List<(int Layer, int Frame, float Opacity, bool IsPrevious)>();
        var seen = new HashSet<(int Layer, int Keyframe)>();
        for (var layer = 0; layer < scene.LayerCount; layer++)
        {
            if (!scene.IsLayerEligibleForOnionSkin(layer)) continue;
            var track = scene.Timeline.FindTrackByTargetId(scene.LayerIds[layer]);
            if (track is null) continue;
            var currentExposure = track.EvaluateExposure(Math.Min(currentFrame, track.Duration - 1));
            var currentKeyframe = currentExposure.HasContent ? currentExposure.SourceKeyframeFrame : -1;
            for (var offset = scene.OnionSkinPreviousFrames; offset >= 1; offset--)
            {
                var previewFrame = currentFrame - offset;
                if (previewFrame < 0) continue;
                AddCandidate(previewFrame, offset, isPrevious: true);
            }
            for (var offset = 1; offset <= scene.OnionSkinNextFrames; offset++)
            {
                var previewFrame = currentFrame + offset;
                if (previewFrame >= track.Duration) continue;
                AddCandidate(previewFrame, offset, isPrevious: false);
            }

            void AddCandidate(int previewFrame, int offset, bool isPrevious)
            {
                var exposure = track.EvaluateExposure(previewFrame);
                if (!exposure.HasContent
                    || exposure.SourceKeyframeFrame == currentKeyframe
                    || !seen.Add((layer, exposure.SourceKeyframeFrame)))
                {
                    return;
                }
                candidates.Add((layer, previewFrame, 0.18f + 0.32f / offset, isPrevious));
            }
        }

        var definitionsById = DefinitionsById(drawingObjects);
        var previews = new List<(VectorScene Scene, float Opacity, bool? IsPrevious)>(candidates.Count);
        foreach (var candidate in candidates)
        {
            if (!drawingObject.InstancesInLayer(scene.LayerIds[candidate.Layer]).Any(instance =>
                    IsDrawingObjectInstanceActive(drawingObject, instance, candidate.Frame)
                    && definitionsById.ContainsKey(instance.DrawingObjectId)))
            {
                continue;
            }

            var preview = new VectorScene();
            BuildDrawingObjectChildrenCore(
                preview,
                drawingObject,
                drawingObjects,
                candidate.Frame,
                parentFps,
                candidate.Layer,
                definitionsById,
                excludeEffectivelyLockedLayers: true);
            if (preview.ObjectCount > 0) previews.Add((preview, candidate.Opacity, candidate.IsPrevious));
        }
        destination.CombineOnionSkinPreviews(previews);
    }

    /// <summary>
    /// Builds the onion-skin preview for a scene composition. Every requested
    /// neighbouring frame is composed through <see cref="Build"/> and tinted by
    /// <see cref="VectorScene.CombineOnionSkinPreviews"/>. Neighbouring frames
    /// that resolve to the same layer exposures as the current frame are skipped
    /// so a held Cel is not drawn twice.
    /// </summary>
    public static void BuildSceneOnionSkin(
        VectorScene destination,
        SceneDefinition? sceneDefinition,
        IReadOnlyList<DrawingObjectDefinition> drawingObjects,
        int frame,
        decimal parentFps = 30m)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(drawingObjects);
        if (sceneDefinition is null || !sceneDefinition.HasOnionSkinPreviewEnabled)
        {
            destination.CreateEmpty();
            return;
        }

        sceneDefinition.SynchronizeTimelineTracks();
        var timeline = sceneDefinition.Timeline;
        var lastFrame = Math.Max(0, sceneDefinition.FrameCount - 1);
        var currentFrame = Math.Clamp(frame, 0, lastFrame);
        var previews = new List<(VectorScene Scene, float Opacity, bool? IsPrevious)>();
        for (var offset = sceneDefinition.OnionSkinPreviousFrames; offset >= 1; offset--)
        {
            AddPreview(currentFrame - offset, offset, isPrevious: true);
        }

        for (var offset = 1; offset <= sceneDefinition.OnionSkinNextFrames; offset++)
        {
            AddPreview(currentFrame + offset, offset, isPrevious: false);
        }

        destination.CombineOnionSkinPreviews(previews);
        return;

        void AddPreview(int previewFrame, int offset, bool isPrevious)
        {
            if (previewFrame < 0 || previewFrame > lastFrame) return;
            if (!HasDifferentLayerExposure(sceneDefinition, timeline, currentFrame, previewFrame)) return;
            var preview = new VectorScene();
            Build(preview, sceneDefinition, drawingObjects, previewFrame, parentFps);
            if (preview.ObjectCount == 0) return;
            previews.Add((preview, 0.18f + 0.32f / offset, isPrevious));
        }
    }

    private static bool HasDifferentLayerExposure(
        SceneDefinition sceneDefinition,
        AnimationTimeline timeline,
        int currentFrame,
        int previewFrame)
    {
        foreach (var layer in sceneDefinition.Layers)
        {
            if (layer.Kind != SceneLayerKind.Content) continue;
            var current = timeline.EvaluateTargetExposure(layer.Id, currentFrame);
            var preview = timeline.EvaluateTargetExposure(layer.Id, previewFrame);
            if (current.HasContent != preview.HasContent
                || current.SourceKeyframeFrame != preview.SourceKeyframeFrame)
            {
                return true;
            }
        }

        return false;
    }

    private static SceneCompositionResult BuildDrawingObjectChildrenCore(
        VectorScene destination,
        DrawingObjectDefinition? drawingObject,
        IReadOnlyList<DrawingObjectDefinition> drawingObjects,
        int frame,
        decimal parentFps,
        int? hostLayerFilter,
        IReadOnlyDictionary<string, DrawingObjectDefinition>? definitionsById = null,
        bool excludeEffectivelyLockedLayers = false)
    {
        if (drawingObject is null || drawingObject.Instances.Count == 0)
        {
            destination.CreateEmpty();
            return SceneCompositionResult.Empty;
        }

        definitionsById ??= DefinitionsById(drawingObjects);
        var layers = new List<CompositionLayer>();
        drawingObject.SynchronizeTimelineTracks();
        var localFrame = Math.Clamp(frame, 0, Math.Max(0, drawingObject.FrameCount - 1));
        var ancestry = new HashSet<string>(StringComparer.Ordinal) { drawingObject.Id };
        for (var hostLayer = 0; hostLayer < drawingObject.Scene.LayerCount; hostLayer++)
        {
            if (hostLayerFilter is not null && hostLayer != hostLayerFilter.Value) continue;
            if (excludeEffectivelyLockedLayers
                && drawingObject.Scene.IsLayerEffectivelyLocked(hostLayer))
            {
                continue;
            }
            var hostOutline = drawingObject.Scene.IsLayerEffectivelyOutlined(hostLayer);
            var firstLayer = layers.Count;
            foreach (var instance in drawingObject.InstancesInLayer(drawingObject.Scene.LayerIds[hostLayer]))
            {
                if (!IsDrawingObjectInstanceActive(drawingObject, instance, localFrame)
                    || !definitionsById.TryGetValue(instance.DrawingObjectId, out var child))
                {
                    continue;
                }

                var instanceState = instance.EvaluateState(localFrame);
                var spatialTransform = InstanceSpatialMatrix(child, instanceState);
                CollectDrawingObjectLayers(
                    child,
                    instance,
                    InstanceMatrix(child, instanceState),
                    spatialTransform,
                    InstanceSpatialIsPlanar(instanceState),
                    CreateRootExtrusionVector(instanceState, spatialTransform),
                    localFrame,
                    parentFps,
                    definitionsById,
                    layers,
                    ancestry,
                    instance.Name,
                    instance.Id,
                    inheritedOutline: hostOutline,
                    inheritedOutlineColorArgb: hostOutline
                        ? drawingObject.Scene.LayerColorArgb[hostLayer]
                        : 0,
                    excludeEffectivelyLockedLayers: excludeEffectivelyLockedLayers);
            }

            if (hostLayer < drawingObject.Scene.LayerBlendModes.Length
                && drawingObject.Scene.LayerBlendModes[hostLayer] != LayerBlendMode.Normal
                && layers.Count > firstLayer)
            {
                WrapCompositionRangeInGroup(
                    layers,
                    firstLayer,
                    drawingObject.Scene.LayerNames[hostLayer],
                    drawingObject.Scene.LayerBlendModes[hostLayer],
                    hostOutline,
                    drawingObject.Scene.LayerColorArgb[hostLayer]);
            }
        }

        return BuildLayers(destination, layers, Math.Max(AnimationTimeline.DefaultDuration, drawingObject.FrameCount));
    }

    public static SceneCompositionResult BuildDrawingObjectPreview(
        VectorScene destination,
        DrawingObjectDefinition? drawingObject,
        IReadOnlyList<DrawingObjectDefinition> drawingObjects,
        PointF position,
        int frame,
        float opacity = 0.48f,
        decimal parentFps = 30m)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(drawingObjects);
        if (drawingObject is null || !drawingObjects.Any(item => string.Equals(item.Id, drawingObject.Id, StringComparison.Ordinal)))
        {
            destination.CreateEmpty();
            return SceneCompositionResult.Empty;
        }

        var definitionsById = DefinitionsById(drawingObjects);
        var previewInstance = new DrawingObjectInstanceDefinition
        {
            Id = $"drag-preview-{drawingObject.Id}",
            DrawingObjectId = drawingObject.Id,
            Name = drawingObject.Name,
            X = position.X,
            Y = position.Y
        };
        var layers = new List<CompositionLayer>();
        var localFrame = Math.Clamp(frame, 0, Math.Max(0, drawingObject.FrameCount - 1));
        var previewState = previewInstance.EvaluateState(localFrame);
        var spatialTransform = InstanceSpatialMatrix(drawingObject, previewState);
        CollectDrawingObjectLayers(
            drawingObject,
            previewInstance,
            InstanceMatrix(drawingObject, previewState),
            spatialTransform,
            InstanceSpatialIsPlanar(previewState),
            CreateRootExtrusionVector(previewState, spatialTransform),
            localFrame,
            parentFps,
            definitionsById,
            layers,
            new HashSet<string>(StringComparer.Ordinal),
            drawingObject.Name,
            previewInstance.Id);
        var result = BuildLayers(destination, layers, Math.Max(AnimationTimeline.DefaultDuration, drawingObject.FrameCount));
        destination.ApplyOpacity(opacity);
        return result;
    }

    private static Dictionary<string, DrawingObjectDefinition> DefinitionsById(IReadOnlyList<DrawingObjectDefinition> drawingObjects)
    {
        return drawingObjects
            .Where(item => !string.IsNullOrWhiteSpace(item.Id))
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
    }

    private static void CollectDrawingObjectLayers(
        DrawingObjectDefinition drawingObject,
        DrawingObjectInstanceDefinition instance,
        Matrix3x2 transform,
        Matrix4x4 spatialTransform,
        bool spatialIsPlanar,
        Vector3 extrusionVector,
        int parentFrame,
        decimal parentFps,
        IReadOnlyDictionary<string, DrawingObjectDefinition> definitionsById,
        List<CompositionLayer> layers,
        ISet<string> ancestry,
        string path,
        string rootInstanceId,
        float inheritedOpacity = 1f,
        float inheritedAlpha = 1f,
        int inheritedTintArgb = unchecked((int)0xffffffff),
        SpatialOpticalMaterial? inheritedOpticalMaterial = null,
        bool synchronize = true,
        bool inheritedOutline = false,
        int inheritedOutlineColorArgb = 0,
        string inheritedParentGroupId = "",
        bool excludeEffectivelyLockedLayers = false,
        IReadOnlyList<DistortWarp>? inheritedDistortions = null)
    {
        if (!ancestry.Add(drawingObject.Id)) return;
        try
        {
            var source = drawingObject.Scene;
            if (synchronize)
            {
                source.SynchronizeTimelineTracks();
                drawingObject.SynchronizeTimelineTracks();
            }
            var state = instance.EvaluateState(parentFrame);
            var compositionDistortions = ComposeInstanceDistortions(
                state.Distortion,
                transform,
                inheritedDistortions);
            var alpha = Math.Clamp(inheritedAlpha * state.Alpha, 0f, 1f);
            var tintArgb = MultiplyTintArgb(inheritedTintArgb, state.TintArgb);
            var opticalMaterial = instance.OpticalMaterialOverride
                ?? inheritedOpticalMaterial
                ?? SpatialOpticalMaterial.Default;
            var localFrame = DrawingObjectInstanceDefinition.ResolvePlaybackFrame(
                parentFrame,
                parentFps,
                drawingObject.FrameCount,
                state);
            if (state.Filters.HasEnabled && source.LayerCount > 0)
            {
                var filterGroupId = Guid.NewGuid().ToString("N");
                layers.Add(new CompositionLayer(
                    source, 0, localFrame, $"{path} / Filters", transform, spatialTransform,
                    spatialIsPlanar, extrusionVector, 1f, 1f, unchecked((int)0xffffffff),
                    SpatialOpticalMaterial.Default, LayerBlendMode.Normal, inheritedOutline,
                    inheritedOutlineColorArgb,
                    new SceneCompositionObjectOwner(instance.Id, drawingObject.Id, rootInstanceId),
                    SyntheticGroup: true, GroupId: filterGroupId, ParentGroupId: inheritedParentGroupId)
                {
                    Filters = state.Filters
                });
                inheritedParentGroupId = filterGroupId;
            }
            for (var sourceLayer = 0; sourceLayer < source.LayerCount; sourceLayer++)
            {
                if (excludeEffectivelyLockedLayers && source.IsLayerEffectivelyLocked(sourceLayer)) continue;
                var outline = inheritedOutline || source.IsLayerEffectivelyOutlined(sourceLayer);
                var outlineColorArgb = inheritedOutline
                    ? inheritedOutlineColorArgb
                    : source.LayerColorArgb[sourceLayer];
                var blendMode = sourceLayer < source.LayerBlendModes.Length
                    ? source.LayerBlendModes[sourceLayer]
                    : LayerBlendMode.Normal;
                var activeChildren = drawingObject.InstancesInLayer(source.LayerIds[sourceLayer])
                    .Where(childInstance => IsDrawingObjectInstanceActive(drawingObject, childInstance, localFrame))
                    .Select(childInstance => definitionsById.TryGetValue(childInstance.DrawingObjectId, out var child)
                        ? (Instance: childInstance, DrawingObject: child)
                        : default)
                    .Where(item => item.Instance is not null && item.DrawingObject is not null)
                    .ToArray();
                var sourceParentGroupId = string.IsNullOrWhiteSpace(source.LayerParentIds[sourceLayer])
                    ? inheritedParentGroupId
                    : "";
                var hostGroupId = "";
                if (blendMode != LayerBlendMode.Normal && activeChildren.Length > 0)
                {
                    hostGroupId = Guid.NewGuid().ToString("N");
                    layers.Add(new CompositionLayer(
                        source,
                        sourceLayer,
                        localFrame,
                        $"{path} / {source.LayerNames[sourceLayer]} group",
                        transform,
                        spatialTransform,
                        spatialIsPlanar,
                        extrusionVector,
                        1f,
                        1f,
                        unchecked((int)0xffffffff),
                        SpatialOpticalMaterial.Default,
                        blendMode,
                        outline,
                        outlineColorArgb,
                        new SceneCompositionObjectOwner(instance.Id, drawingObject.Id, rootInstanceId),
                        SyntheticGroup: true,
                        GroupId: hostGroupId,
                        ParentGroupId: sourceParentGroupId,
                        PreserveSourceParent: true,
                        Distortions: compositionDistortions));
                }

                layers.Add(new CompositionLayer(
                    source,
                    sourceLayer,
                    localFrame,
                    $"{path} / {source.LayerNames[sourceLayer]}",
                    transform,
                    spatialTransform,
                    spatialIsPlanar,
                    extrusionVector,
                    inheritedOpacity,
                    alpha,
                    tintArgb,
                    opticalMaterial,
                    hostGroupId.Length > 0 ? LayerBlendMode.Normal : blendMode,
                    outline,
                    outlineColorArgb,
                    new SceneCompositionObjectOwner(instance.Id, drawingObject.Id, rootInstanceId),
                    ParentGroupId: hostGroupId.Length > 0 ? hostGroupId : sourceParentGroupId,
                    Distortions: compositionDistortions)
                {
                    IsCollisionTerrain = source.IsCollisionTerrainLayer(sourceLayer)
                });
                foreach (var childItem in activeChildren)
                {
                    var childInstance = childItem.Instance!;
                    var child = childItem.DrawingObject!;
                    var childTransform = InstanceMatrix(child, childInstance, localFrame) * transform;
                    var childSpatialIsPlanar = spatialIsPlanar && InstanceSpatialIsPlanar(childInstance, localFrame);
                    var childSpatialTransform = childSpatialIsPlanar
                        ? Lift(childTransform)
                        : InstanceSpatialMatrix(child, childInstance, localFrame) * spatialTransform;

                    CollectDrawingObjectLayers(
                        child,
                        childInstance,
                        childTransform,
                        childSpatialTransform,
                        childSpatialIsPlanar,
                        extrusionVector,
                        localFrame,
                        state.PlaybackFps,
                        definitionsById,
                        layers,
                        ancestry,
                        $"{path} / {childInstance.Name}",
                        rootInstanceId,
                        inheritedOpacity * EffectiveLayerOpacity(source, sourceLayer),
                        alpha,
                        tintArgb,
                        opticalMaterial,
                        synchronize,
                        outline,
                        outlineColorArgb,
                        hostGroupId.Length > 0 ? hostGroupId
                            : sourceParentGroupId.Length > 0 ? sourceParentGroupId : inheritedParentGroupId,
                        excludeEffectivelyLockedLayers,
                        compositionDistortions);
                }
            }
        }
        finally
        {
            ancestry.Remove(drawingObject.Id);
        }
    }

    private static DistortWarp[] ComposeInstanceDistortions(
        DistortWarp? localDistortion,
        Matrix3x2 transform,
        IReadOnlyList<DistortWarp>? inheritedDistortions)
    {
        var inheritedCount = inheritedDistortions?.Count ?? 0;
        var hasLocal = localDistortion is { IsValid: true };
        if (!hasLocal && inheritedCount == 0) return [];

        var result = new DistortWarp[inheritedCount + (hasLocal ? 1 : 0)];
        var offset = 0;
        if (hasLocal)
        {
            result[0] = localDistortion!.Value.AffineTransform(transform);
            offset = 1;
        }
        for (var index = 0; index < inheritedCount; index++)
        {
            result[offset + index] = inheritedDistortions![index].DeepClone();
        }
        return result;
    }

    private static DistortWarp[] PrepareCompositionDistortions(CompositionWorkItem workItem)
    {
        workItem.Source.TryGetObjectDistortions(workItem.SourceObject, out var objectDistortions);
        var inheritedCount = workItem.Distortions?.Length ?? 0;
        if (objectDistortions.Length == 0 && inheritedCount == 0) return [];

        var result = new DistortWarp[objectDistortions.Length + inheritedCount];
        for (var index = 0; index < objectDistortions.Length; index++)
        {
            result[index] = workItem.Transform.IsIdentity
                ? objectDistortions[index].DeepClone()
                : objectDistortions[index].AffineTransform(workItem.Transform);
        }
        for (var index = 0; index < inheritedCount; index++)
        {
            result[objectDistortions.Length + index] = workItem.Distortions![index].DeepClone();
        }
        return result;
    }

    private static void WrapCompositionRangeInGroup(
        List<CompositionLayer> layers,
        int firstLayer,
        string name,
        LayerBlendMode blendMode,
        bool outline,
        int outlineColorArgb)
    {
        if ((uint)firstLayer >= layers.Count) return;
        var groupId = Guid.NewGuid().ToString("N");
        var template = layers[firstLayer];
        layers.Insert(firstLayer, template with
        {
            Name = name,
            InheritedOpacity = 1f,
            Alpha = 1f,
            TintArgb = unchecked((int)0xffffffff),
            OpticalMaterial = SpatialOpticalMaterial.Default,
            BlendMode = blendMode,
            Outline = outline,
            OutlineColorArgb = outlineColorArgb,
            SyntheticGroup = true,
            GroupId = groupId,
            ParentGroupId = "",
            PreserveSourceParent = false,
            Filters = default,
            IsCollisionTerrain = false
        });

        for (var index = firstLayer + 1; index < layers.Count; index++)
        {
            var layer = layers[index];
            if (!string.IsNullOrWhiteSpace(layer.ParentGroupId)) continue;
            if (!layer.SyntheticGroup
                && layer.SourceLayer < layer.Source.LayerParentIds.Length
                && !string.IsNullOrWhiteSpace(layer.Source.LayerParentIds[layer.SourceLayer]))
            {
                continue;
            }

            layers[index] = layer with { ParentGroupId = groupId };
        }
    }

    private static float EffectiveLayerOpacity(VectorScene scene, int layer)
    {
        var opacity = 1f;
        var current = layer;
        var visited = new HashSet<int>();
        while ((uint)current < scene.LayerCount && visited.Add(current))
        {
            opacity *= Math.Clamp(scene.LayerOpacity[current], 0f, 1f);
            current = scene.GetLayerParentIndex(current);
            if (current < 0) break;
        }
        return opacity;
    }

    private static bool IsDrawingObjectInstanceActive(
        DrawingObjectDefinition drawingObject,
        DrawingObjectInstanceDefinition instance,
        int frame)
    {
        if (!instance.EvaluateState(frame).Visible) return false;
        var scene = drawingObject.Scene;
        var layer = Array.IndexOf(scene.LayerIds, instance.SceneLayerId);
        return layer >= 0
            && scene.IsLayerEffectivelyVisible(layer)
            && drawingObject.Timeline.EvaluateTargetExposure(instance.SceneLayerId, frame).HasContent;
    }

    private static SceneCompositionResult BuildLayers(
        VectorScene destination,
        IReadOnlyList<CompositionLayer> layers,
        int duration)
    {
        var phaseStarted = Stopwatch.GetTimestamp();
        LastActiveObjectBucketCacheHits = 0;
        LastActiveObjectBucketCacheMisses = 0;
        var sourceObjectsByLayer = BuildSourceObjectBuckets(layers);
        var expectedObjectCount = 0;
        var populatedDestinationLayers = new List<int>(layers.Count);
        for (var destinationLayer = 0; destinationLayer < layers.Count; destinationLayer++)
        {
            var layer = layers[destinationLayer];
            var sourceObjectCount = SourceObjects(layer, sourceObjectsByLayer).Length;
            expectedObjectCount += sourceObjectCount;
            if (sourceObjectCount > 0) populatedDestinationLayers.Add(destinationLayer);
        }
        var bucketMilliseconds = Stopwatch.GetElapsedTime(phaseStarted).TotalMilliseconds;

        phaseStarted = Stopwatch.GetTimestamp();
        destination.CreateEmpty(Math.Max(1, layers.Count));
        destination.EditFrame = 0;
        using (destination.Timeline.BeginBatchUpdate())
        {
            destination.SynchronizeTimelineTracks();
            foreach (var track in destination.Timeline.Tracks)
            {
                destination.Timeline.SetTrackDuration(track.Id, Math.Max(1, duration));
            }
        }

        var destinationLayerBySource = new Dictionary<CompositionLayerGroupKey, Dictionary<string, int>>();
        var destinationLayerByGroupId = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var destinationLayer = 0; destinationLayer < layers.Count; destinationLayer++)
        {
            var layer = layers[destinationLayer];
            destination.LayerNames[destinationLayer] = layer.IsCollisionTerrain
                ? VectorScene.CollisionTerrainLayerName
                : layer.Name;
            destination.LayerKinds[destinationLayer] = layer.SyntheticGroup
                ? DrawingLayerKind.Folder
                : layer.Source.GetLayerKind(layer.SourceLayer);
            destination.LayerLocked[destinationLayer] = !layer.SyntheticGroup
                && layer.Source.LayerLocked[layer.SourceLayer];
            destination.LayerVisible[destinationLayer] = layer.SyntheticGroup
                || layer.Source.LayerVisible[layer.SourceLayer];
            destination.LayerOpacity[destinationLayer] = layer.SyntheticGroup
                ? 1f
                : Math.Clamp(layer.Source.LayerOpacity[layer.SourceLayer] * layer.InheritedOpacity, 0f, 1f);
            destination.LayerBlendModes[destinationLayer] = layer.BlendMode;
            destination.SetLayerSymbolFilters(destinationLayer, layer.Filters);
            destination.LayerOutline[destinationLayer] = layer.Outline;
            destination.LayerColorArgb[destinationLayer] = layer.Outline
                ? layer.OutlineColorArgb
                : layer.Source.LayerColorArgb[layer.SourceLayer];
            if (!string.IsNullOrWhiteSpace(layer.GroupId))
            {
                destinationLayerByGroupId.Add(layer.GroupId, destinationLayer);
            }
            if (layer.SyntheticGroup) continue;

            var groupKey = new CompositionLayerGroupKey(layer.Source, layer.Owner.InstanceId);
            if (!destinationLayerBySource.TryGetValue(groupKey, out var map))
            {
                map = new Dictionary<string, int>(StringComparer.Ordinal);
                destinationLayerBySource.Add(groupKey, map);
            }

            map[layer.Source.LayerIds[layer.SourceLayer]] = destinationLayer;
        }

        for (var destinationLayer = 0; destinationLayer < layers.Count; destinationLayer++)
        {
            var layer = layers[destinationLayer];
            if (!string.IsNullOrWhiteSpace(layer.ParentGroupId)
                && destinationLayerByGroupId.TryGetValue(layer.ParentGroupId, out var explicitParentLayer))
            {
                destination.LayerParentIds[destinationLayer] = destination.LayerIds[explicitParentLayer];
            }

            if (layer.SyntheticGroup)
            {
                if (string.IsNullOrWhiteSpace(destination.LayerParentIds[destinationLayer])
                    && layer.PreserveSourceParent
                    && destinationLayerBySource.TryGetValue(
                        new CompositionLayerGroupKey(layer.Source, layer.Owner.InstanceId),
                        out var syntheticMap)
                    && layer.SourceLayer < layer.Source.LayerParentIds.Length
                    && syntheticMap.TryGetValue(layer.Source.LayerParentIds[layer.SourceLayer], out var syntheticParentLayer))
                {
                    destination.LayerParentIds[destinationLayer] = destination.LayerIds[syntheticParentLayer];
                }
                continue;
            }
            var map = destinationLayerBySource[new CompositionLayerGroupKey(layer.Source, layer.Owner.InstanceId)];
            var sourceLayer = layer.SourceLayer;
            if (string.IsNullOrWhiteSpace(destination.LayerParentIds[destinationLayer])
                && sourceLayer < layer.Source.LayerParentIds.Length
                && map.TryGetValue(layer.Source.LayerParentIds[sourceLayer], out var parentLayer))
            {
                destination.LayerParentIds[destinationLayer] = destination.LayerIds[parentLayer];
            }

            if (sourceLayer < layer.Source.LayerMaskIds.Length
                && map.TryGetValue(layer.Source.LayerMaskIds[sourceLayer], out var maskLayer))
            {
                destination.LayerMaskIds[destinationLayer] = destination.LayerIds[maskLayer];
            }
        }
        var setupMilliseconds = Stopwatch.GetElapsedTime(phaseStarted).TotalMilliseconds;

        phaseStarted = Stopwatch.GetTimestamp();
        var packedBatch = false;
        SceneCompositionObjectOwner[]? packedOwners = null;
        SceneCompositionObjectPose[]? packedPoses = null;
        SpatialOpticalMaterial[]? packedMaterials = null;
        List<SceneCompositionObjectOwner>? owners = null;
        List<SceneCompositionObjectPose>? poses = null;
        List<SpatialOpticalMaterial>? materials = null;
        destination.BeginDeferredAppend(expectedObjectCount, populatedDestinationLayers);
        try
        {
            packedBatch = expectedObjectCount >= 8192 && CanUsePackedBatch(layers, sourceObjectsByLayer);
            if (packedBatch)
            {
                var workItems = BuildWorkItems(layers, sourceObjectsByLayer, expectedObjectCount);
                var packedObjects = new PackedSceneObject[workItems.Length];
                var ownersForBuild = new SceneCompositionObjectOwner[workItems.Length];
                var posesForBuild = new SceneCompositionObjectPose[workItems.Length];
                var materialsForBuild = new SpatialOpticalMaterial[workItems.Length];
                packedOwners = ownersForBuild;
                packedPoses = posesForBuild;
                packedMaterials = materialsForBuild;
                ParallelBatch.For(workItems.Length, 4096, (_, start, end) =>
                {
                    for (var index = start; index < end; index++)
                    {
                        var workItem = workItems[index];
                        packedObjects[index] = PreparePackedObject(workItem);
                        ownersForBuild[index] = workItem.Owner;
                        posesForBuild[index] = workItem.Pose;
                        materialsForBuild[index] = workItem.OpticalMaterial;
                    }
                });

                var firstObject = destination.AppendPackedObjects(packedObjects);
                if (firstObject != 0
                    || firstObject + packedObjects.Length != destination.ObjectCount)
                {
                    throw new InvalidOperationException("Packed composition metadata lost object-index alignment.");
                }
            }
            else
            {
                var ownersForBuild = new List<SceneCompositionObjectOwner>(expectedObjectCount);
                var posesForBuild = new List<SceneCompositionObjectPose>(expectedObjectCount);
                var materialsForBuild = new List<SpatialOpticalMaterial>(expectedObjectCount);
                owners = ownersForBuild;
                poses = posesForBuild;
                materials = materialsForBuild;
                if (expectedObjectCount >= 8192)
                {
                    var workItems = BuildWorkItems(layers, sourceObjectsByLayer, expectedObjectCount);
                    AppendPreparedChunks(
                        destination,
                        workItems,
                        ownersForBuild,
                        posesForBuild,
                        materialsForBuild);
                }
                else
                {
                    for (var destinationLayer = 0; destinationLayer < layers.Count; destinationLayer++)
                    {
                        var layer = layers[destinationLayer];
                        var pose = CreateObjectPose(
                            layer.Transform,
                            layer.SpatialTransform,
                            layer.SpatialIsPlanar,
                            layer.ExtrusionVector);
                        var sourceObjects = SourceObjects(layer, sourceObjectsByLayer);
                        foreach (var sourceObject in sourceObjects)
                        {
                            var item = new CompositionWorkItem(
                                layer.Source,
                                sourceObject,
                                destinationLayer,
                                layer.Transform,
                                pose,
                                layer.Alpha,
                                layer.TintArgb,
                                layer.OpticalMaterial,
                                layer.Owner,
                                layer.Distortions);
                            var destinationObject = AppendPreparedObject(destination, PrepareObject(item));
                            if (destinationObject >= 0)
                            {
                                AppendCompositionMetadata(
                                    ownersForBuild,
                                    posesForBuild,
                                    materialsForBuild,
                                    destinationObject,
                                    layer.Owner,
                                    pose,
                                    layer.OpticalMaterial);
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            destination.EndDeferredAppend();
        }
        var appendMilliseconds = Stopwatch.GetElapsedTime(phaseStarted).TotalMilliseconds;

        phaseStarted = Stopwatch.GetTimestamp();
        destination.CompleteDeferredBuild();
        if (packedBatch)
        {
            if (packedOwners is null
                || packedPoses is null
                || packedMaterials is null
                || packedOwners.Length != destination.ObjectCount
                || packedPoses.Length != destination.ObjectCount
                || packedMaterials.Length != destination.ObjectCount)
            {
                throw new InvalidOperationException("Composition metadata does not align with destination objects.");
            }
        }
        else if (owners is null
            || poses is null
            || materials is null
            || owners.Count != destination.ObjectCount
            || poses.Count != destination.ObjectCount
            || materials.Count != destination.ObjectCount)
        {
            throw new InvalidOperationException("Composition metadata does not align with destination objects.");
        }
        var finalizeMilliseconds = Stopwatch.GetElapsedTime(phaseStarted).TotalMilliseconds;
        LastBuildMetrics = new SceneCompositionBuildMetrics(
            bucketMilliseconds,
            setupMilliseconds,
            appendMilliseconds,
            finalizeMilliseconds);
        return packedBatch
            ? new SceneCompositionResult(packedOwners!, packedPoses!, packedMaterials!)
            : new SceneCompositionResult(owners!.ToArray(), poses!.ToArray(), materials!.ToArray());
    }

    private static void AppendPreparedChunks(
        VectorScene destination,
        CompositionWorkItem[] workItems,
        List<SceneCompositionObjectOwner> owners,
        List<SceneCompositionObjectPose> poses,
        List<SpatialOpticalMaterial> materials)
    {
        var preparedObjects = new PreparedCompositionObject[Math.Min(PreparedChunkSize, workItems.Length)];
        for (var offset = 0; offset < workItems.Length; offset += preparedObjects.Length)
        {
            var count = Math.Min(preparedObjects.Length, workItems.Length - offset);
            ParallelBatch.For(count, 256, (_, start, end) =>
            {
                for (var index = start; index < end; index++) preparedObjects[index] = PrepareObject(workItems[offset + index]);
            });

            for (var index = 0; index < count; index++)
            {
                var destinationObject = AppendPreparedObject(destination, preparedObjects[index]);
                if (destinationObject >= 0)
                {
                    var workItem = workItems[offset + index];
                    AppendCompositionMetadata(
                        owners,
                        poses,
                        materials,
                        destinationObject,
                        workItem.Owner,
                        workItem.Pose,
                        workItem.OpticalMaterial);
                }
            }

            Array.Clear(preparedObjects, 0, count);
        }
    }

    private static void AppendCompositionMetadata(
        List<SceneCompositionObjectOwner> owners,
        List<SceneCompositionObjectPose> poses,
        List<SpatialOpticalMaterial> materials,
        int objectIndex,
        SceneCompositionObjectOwner owner,
        SceneCompositionObjectPose pose,
        SpatialOpticalMaterial material)
    {
        if (objectIndex != owners.Count || objectIndex != poses.Count || objectIndex != materials.Count)
        {
            throw new InvalidOperationException("Composition metadata lost object-index alignment.");
        }
        owners.Add(owner);
        poses.Add(pose);
        materials.Add(material);
    }

    private static bool CanUsePackedBatch(
        IReadOnlyList<CompositionLayer> layers,
        IReadOnlyDictionary<SourceFrameKey, int[][]> sourceObjectsByLayer)
    {
        foreach (var layer in layers)
        {
            if (HasShear(layer.Transform) || layer.Distortions is { Length: > 0 }) return false;
            var sourceObjects = SourceObjects(layer, sourceObjectsByLayer);
            foreach (var sourceObject in sourceObjects)
            {
                if (layer.Source.TryGetObjectDistortions(sourceObject, out _)) return false;
                if (layer.Source.ShapeKind[sourceObject] is ShapeKind.Path
                    or ShapeKind.Freeform
                    or ShapeKind.MixingStroke
                    or ShapeKind.ImportedSvg
                    or ShapeKind.Bitmap
                    or ShapeKind.Text)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static CompositionWorkItem[] BuildWorkItems(
        IReadOnlyList<CompositionLayer> layers,
        IReadOnlyDictionary<SourceFrameKey, int[][]> sourceObjectsByLayer,
        int expectedObjectCount)
    {
        var workItems = new CompositionWorkItem[expectedObjectCount];
        var index = 0;
        for (var destinationLayer = 0; destinationLayer < layers.Count; destinationLayer++)
        {
            var layer = layers[destinationLayer];
            var pose = CreateObjectPose(
                layer.Transform,
                layer.SpatialTransform,
                layer.SpatialIsPlanar,
                layer.ExtrusionVector);
            var sourceObjects = SourceObjects(layer, sourceObjectsByLayer);
            foreach (var sourceObject in sourceObjects)
            {
                workItems[index++] = new CompositionWorkItem(
                    layer.Source,
                    sourceObject,
                    destinationLayer,
                    layer.Transform,
                    pose,
                    layer.Alpha,
                    layer.TintArgb,
                    layer.OpticalMaterial,
                    layer.Owner,
                    layer.Distortions);
            }
        }

        return workItems;
    }

    private static int[] SourceObjects(
        CompositionLayer layer,
        IReadOnlyDictionary<SourceFrameKey, int[][]> sourceObjectsByLayer)
    {
        return layer.SyntheticGroup
            ? []
            : sourceObjectsByLayer[new SourceFrameKey(layer.Source, layer.SourceFrame)][layer.SourceLayer];
    }

    private static Dictionary<SourceFrameKey, int[][]> BuildSourceObjectBuckets(
        IReadOnlyList<CompositionLayer> layers)
    {
        var result = new Dictionary<SourceFrameKey, int[][]>();
        foreach (var layer in layers)
        {
            var key = new SourceFrameKey(layer.Source, layer.SourceFrame);
            if (!result.ContainsKey(key)) result[key] = BuildActiveObjectsByLayer(layer.Source, layer.SourceFrame);
        }

        return result;
    }

    private static int[][] BuildActiveObjectsByLayer(VectorScene source, int frame)
    {
        var activeKeyframes = new int[source.LayerCount];
        source.PopulateActiveKeyframeFrames(frame, activeKeyframes);
        var cache = ActiveObjectBucketCaches.GetValue(source, static _ => new ActiveObjectBucketCache());
        if (cache.TryGet(source, activeKeyframes, out var cached))
        {
            LastActiveObjectBucketCacheHits++;
            return cached;
        }

        LastActiveObjectBucketCacheMisses++;
        // VectorScene already maintains a revisioned active-object index. Use
        // it instead of scanning every source object for each exposure; the
        // per-layer sort keeps the composition order contract unchanged.
        var activeObjects = source.GetActiveObjectIndices(frame);
        var counts = new int[source.LayerCount];
        foreach (var objectIndex in activeObjects)
        {
            var layer = source.ObjectLayer[objectIndex];
            if ((uint)layer < (uint)counts.Length) counts[layer]++;
        }

        var objectsByLayer = new int[source.LayerCount][];
        for (var layer = 0; layer < objectsByLayer.Length; layer++)
        {
            objectsByLayer[layer] = counts[layer] == 0
                ? []
                : new int[counts[layer]];
        }

        Array.Clear(counts, 0, counts.Length);
        foreach (var objectIndex in activeObjects)
        {
            var layer = source.ObjectLayer[objectIndex];
            if ((uint)layer >= (uint)objectsByLayer.Length) continue;
            objectsByLayer[layer][counts[layer]++] = objectIndex;
        }

        for (var layer = 0; layer < objectsByLayer.Length; layer++)
        {
            var objects = objectsByLayer[layer];
            if (objects.Length > 1)
            {
                Array.Sort(objects, (a, b) => CompareSourceObjects(source, a, b));
            }
        }

        cache.Store(source, activeKeyframes, objectsByLayer);
        return objectsByLayer;
    }

    private sealed class ActiveObjectBucketCache
    {
        private long _geometryRevision = -1;
        private long _activeContentRevision = -1;
        private int _objectCount = -1;
        private int _layerCount = -1;
        private int[] _activeKeyframes = [];
        private int[][] _objectsByLayer = [];

        public bool TryGet(VectorScene source, int[] activeKeyframes, out int[][] objectsByLayer)
        {
            if (_geometryRevision == source.GeometryRevision
                && _activeContentRevision == source.ActiveContentRevision
                && _objectCount == source.ObjectCount
                && _layerCount == source.LayerCount
                && _activeKeyframes.AsSpan().SequenceEqual(activeKeyframes))
            {
                objectsByLayer = _objectsByLayer;
                return true;
            }

            objectsByLayer = [];
            return false;
        }

        public void Store(VectorScene source, int[] activeKeyframes, int[][] objectsByLayer)
        {
            _geometryRevision = source.GeometryRevision;
            _activeContentRevision = source.ActiveContentRevision;
            _objectCount = source.ObjectCount;
            _layerCount = source.LayerCount;
            _activeKeyframes = (int[])activeKeyframes.Clone();
            _objectsByLayer = objectsByLayer;
        }
    }

    private static int CompareSourceObjects(VectorScene source, int a, int b)
    {
        var comparison = source.ObjectOrder[a].CompareTo(source.ObjectOrder[b]);
        if (comparison != 0) return comparison;
        comparison = source.ObjectSubOrder[a].CompareTo(source.ObjectSubOrder[b]);
        return comparison != 0 ? comparison : a.CompareTo(b);
    }

    private static PreparedCompositionObject PrepareObject(CompositionWorkItem workItem)
    {
        var source = workItem.Source;
        var sourceObject = workItem.SourceObject;
        var destinationLayer = workItem.DestinationLayer;
        var transform = workItem.Transform;
        var identityTransform = transform.IsIdentity;
        var shape = source.ShapeKind[sourceObject];
        var determinant = identityTransform ? 1f : transform.M11 * transform.M22 - transform.M12 * transform.M21;
        var stroke = source.Stroke[sourceObject] * MathF.Sqrt(Math.Abs(determinant));
        var fillArgb = ApplyInstanceAppearance(source.Argb[sourceObject], workItem.Alpha, workItem.TintArgb);
        var strokeArgb = ApplyInstanceAppearance(source.StrokeArgb[sourceObject], workItem.Alpha, workItem.TintArgb);
        var atoms = source.AtomCount[sourceObject];
        var distortions = PrepareCompositionDistortions(workItem);

        if (shape == ShapeKind.Text)
        {
            if (!source.TryGetTextObjectData(sourceObject, out var textObjectData))
            {
                throw new InvalidOperationException("Text composition source is missing its editable payload.");
            }

            if (!TryPrepareEditableTextTransform(
                    source,
                    sourceObject,
                    transform,
                    identityTransform,
                    determinant,
                    out var textCenter,
                    out var textSize,
                    out var textAngle))
            {
                if (source.TryGetTextWorldContours(sourceObject, out var textContours)
                    && textContours.Length > 0)
                {
                    for (var contourIndex = 0; contourIndex < textContours.Length; contourIndex++)
                    {
                        var contour = textContours[contourIndex];
                        for (var pointIndex = 0; pointIndex < contour.Length; pointIndex++)
                        {
                            contour[pointIndex] = TransformPoint(contour[pointIndex], transform, identityTransform);
                        }
                    }

                    return new PreparedCompositionObject(
                        PreparedCompositionKind.Path,
                        destinationLayer,
                        ShapeKind.Path,
                        PointF.Empty,
                        SizeF.Empty,
                        0,
                        0,
                        fillArgb,
                        Color.Transparent.ToArgb(),
                        atoms,
                        PointF.Empty,
                        PointF.Empty,
                        PointF.Empty,
                        [],
                        textContours)
                    {
                        Distortions = distortions
                    };
                }
                if (!string.IsNullOrWhiteSpace(textObjectData.Content))
                {
                    throw new InvalidOperationException(
                        "Unsupported text composition transform could not be materialized to outline geometry.");
                }

                PrepareApproximateObjectTransform(
                    source,
                    sourceObject,
                    transform,
                    identityTransform,
                    out textCenter,
                    out textSize,
                    out textAngle);
            }

            return new PreparedCompositionObject(
                PreparedCompositionKind.Text,
                destinationLayer,
                ShapeKind.Text,
                textCenter,
                textSize,
                textAngle,
                0,
                fillArgb,
                Color.Transparent.ToArgb(),
                atoms,
                PointF.Empty,
                PointF.Empty,
                PointF.Empty,
                [],
                [])
            {
                TextObjectData = textObjectData,
                Distortions = distortions
            };
        }

        if (shape == ShapeKind.ImportedSvg)
        {
            if (!source.TryGetImportedSvgSource(sourceObject, out var importedSvgSource))
            {
                throw new InvalidOperationException("Imported SVG composition source is missing its payload.");
            }
            source.TryGetImportedSvgName(sourceObject, out var importedSvgName);

            var importedCenter = new PointF(source.X[sourceObject], source.Y[sourceObject]);
            var importedSize = new SizeF(source.Width[sourceObject], source.Height[sourceObject]);
            var importedAngle = source.Angle[sourceObject];
            if (!identityTransform)
            {
                if (TryPrepareImportedSvgTransform(
                        importedCenter,
                        importedSize,
                        importedAngle,
                        transform,
                        out var transformedCenter,
                        out var transformedSize,
                        out var transformedAngle))
                {
                    importedCenter = transformedCenter;
                    importedSize = transformedSize;
                    importedAngle = transformedAngle;
                }
                else
                {
                    var wrapped = WrapImportedSvgTransform(
                        importedSvgSource,
                        importedCenter,
                        importedSize,
                        importedAngle,
                        transform);
                    importedSvgSource = wrapped.Source;
                    importedCenter = wrapped.Center;
                    importedSize = wrapped.Size;
                    importedAngle = 0;
                }
            }
            if ((workItem.TintArgb & 0x00ffffff) != 0x00ffffff)
            {
                importedSvgSource = WrapImportedSvgTint(importedSvgSource, importedSize, workItem.TintArgb);
            }

            return new PreparedCompositionObject(
                PreparedCompositionKind.ImportedSvg,
                destinationLayer,
                shape,
                importedCenter,
                importedSize,
                importedAngle,
                0,
                fillArgb,
                strokeArgb,
                atoms,
                PointF.Empty,
                PointF.Empty,
                PointF.Empty,
                [],
                [])
            {
                ImportedSvgSource = importedSvgSource,
                ImportedSvgName = importedSvgName,
                Distortions = distortions
            };
        }

        if (shape == ShapeKind.Bitmap)
        {
            if (!source.TryGetBitmapObjectData(sourceObject, out var bitmapObjectData))
            {
                throw new InvalidOperationException("Bitmap composition source is missing its payload.");
            }

            var bitmapCenter = new PointF(source.X[sourceObject], source.Y[sourceObject]);
            var bitmapSize = new SizeF(source.Width[sourceObject], source.Height[sourceObject]);
            var bitmapAngle = source.Angle[sourceObject];
            if (!identityTransform)
            {
                if (!TryPrepareImportedSvgTransform(
                        bitmapCenter,
                        bitmapSize,
                        bitmapAngle,
                        transform,
                        out var transformedCenter,
                        out var transformedSize,
                        out var transformedAngle))
                {
                    // Unlike imported SVG, a bitmap cannot absorb a shear by re-encoding its
                    // payload, so a non-conformal parent transform cannot be represented as a
                    // placed quad. Failing loudly keeps the mismatch visible instead of
                    // silently emitting an un-sheared image.
                    throw new InvalidOperationException(
                        "Bitmap objects cannot be composed through a sheared or non-conformal transform.");
                }

                bitmapCenter = transformedCenter;
                bitmapSize = transformedSize;
                bitmapAngle = transformedAngle;
            }

            return new PreparedCompositionObject(
                PreparedCompositionKind.Bitmap,
                destinationLayer,
                shape,
                bitmapCenter,
                bitmapSize,
                bitmapAngle,
                0,
                fillArgb,
                strokeArgb,
                atoms,
                PointF.Empty,
                PointF.Empty,
                PointF.Empty,
                [],
                [])
            {
                // Instance alpha and tint ride on the composed object's Argb channel, exactly
                // as they do for imported SVG, so the renderers keep one tinting rule.
                BitmapObjectData = bitmapObjectData,
                Distortions = distortions
            };
        }

        if (shape == ShapeKind.MixingStroke)
        {
            if (source.TryGetMixingBrushWorldRegion(sourceObject, out var mixingRegion))
            {
                var vertices = new MixingBrushRegionVertex[mixingRegion.Vertices.Length];
                for (var vertexIndex = 0; vertexIndex < vertices.Length; vertexIndex++)
                {
                    var vertex = mixingRegion.Vertices[vertexIndex];
                    vertices[vertexIndex] = vertex with
                    {
                        Point = TransformPoint(vertex.Point, transform, identityTransform),
                        Argb = ApplyInstanceAppearance(vertex.Argb, workItem.Alpha, workItem.TintArgb)
                    };
                }

                var transformedRegion = new MixingBrushRegionData(
                    vertices,
                    mixingRegion.TriangleIndices.ToArray());
                return new PreparedCompositionObject(
                    PreparedCompositionKind.MixingStroke,
                    destinationLayer,
                    shape,
                    PointF.Empty,
                    SizeF.Empty,
                    0,
                    0,
                    vertices[^1].Argb,
                    Color.Transparent.ToArgb(),
                    atoms,
                    PointF.Empty,
                    PointF.Empty,
                    PointF.Empty,
                    [],
                    [])
                {
                    MixingRegion = transformedRegion,
                    Distortions = distortions
                };
            }

            if (!source.TryGetMixingStrokeWorldSamples(sourceObject, out var mixingSamples))
            {
                throw new InvalidOperationException("Mixing-stroke composition source is missing its trajectory payload.");
            }

            var diameterScale = MathF.Sqrt(Math.Abs(determinant));
            for (var sampleIndex = 0; sampleIndex < mixingSamples.Length; sampleIndex++)
            {
                var sample = mixingSamples[sampleIndex];
                mixingSamples[sampleIndex] = sample with
                {
                    Point = TransformPoint(sample.Point, transform, identityTransform),
                    Diameter = Math.Max(VectorUnits.MinimumStrokeUnits, sample.Diameter * diameterScale),
                    Argb = ApplyInstanceAppearance(sample.Argb, workItem.Alpha, workItem.TintArgb)
                };
            }

            return new PreparedCompositionObject(
                PreparedCompositionKind.MixingStroke,
                destinationLayer,
                shape,
                PointF.Empty,
                SizeF.Empty,
                0,
                0,
                mixingSamples[^1].Argb,
                Color.Transparent.ToArgb(),
                atoms,
                PointF.Empty,
                PointF.Empty,
                PointF.Empty,
                [],
                [])
            {
                MixingSamples = mixingSamples,
                Distortions = distortions
            };
        }

        if (shape == ShapeKind.Path
            && identityTransform
            && distortions.Length == 0
            && source.TryGetPathLocalContours(sourceObject, out var localContours))
        {
            var prepared = new PreparedCompositionObject(
                PreparedCompositionKind.Path,
                destinationLayer,
                shape,
                new PointF(source.X[sourceObject], source.Y[sourceObject]),
                new SizeF(source.Width[sourceObject], source.Height[sourceObject]),
                source.Angle[sourceObject],
                stroke,
                fillArgb,
                strokeArgb,
                atoms,
                PointF.Empty,
                PointF.Empty,
                PointF.Empty,
                [],
                localContours)
            {
                UseSourceLocalPathGeometry = true,
                Distortions = distortions
            };
            if (source.TryGetPathBezierLocalContours(sourceObject, out var localBezierContours))
            {
                prepared = prepared with { PathBezierContours = localBezierContours };
            }

            return WithGradient(
                prepared,
                source,
                sourceObject,
                transform,
                identityTransform,
                workItem.Alpha,
                workItem.TintArgb,
                preserveSourceGeometry: true);
        }

        if (shape == ShapeKind.Path
            && source.TryGetPathWorldContours(sourceObject, out var contours))
        {
            for (var contourIndex = 0; contourIndex < contours.Length; contourIndex++)
            {
                var contour = contours[contourIndex];
                for (var pointIndex = 0; pointIndex < contour.Length; pointIndex++)
                {
                    contour[pointIndex] = TransformPoint(contour[pointIndex], transform, identityTransform);
                }
            }

            var prepared = new PreparedCompositionObject(
                PreparedCompositionKind.Path,
                destinationLayer,
                shape,
                PointF.Empty,
                SizeF.Empty,
                0,
                stroke,
                fillArgb,
                strokeArgb,
                atoms,
                PointF.Empty,
                PointF.Empty,
                PointF.Empty,
                [],
                contours)
            {
                Distortions = distortions
            };
            if (source.TryGetPathBezierWorldContours(sourceObject, out var bezierContours))
            {
                for (var contourIndex = 0; contourIndex < bezierContours.Length; contourIndex++)
                {
                    var contour = bezierContours[contourIndex];
                    for (var nodeIndex = 0; nodeIndex < contour.Length; nodeIndex++)
                    {
                        var node = contour[nodeIndex];
                        contour[nodeIndex] = new PathBezierNode(
                            TransformPoint(node.Anchor, transform, identityTransform),
                            TransformPoint(node.IncomingControl, transform, identityTransform),
                            TransformPoint(node.OutgoingControl, transform, identityTransform));
                    }
                }

                prepared = prepared with { PathBezierContours = bezierContours };
            }

            return WithGradient(
                prepared,
                source,
                sourceObject,
                transform,
                identityTransform,
                workItem.Alpha,
                workItem.TintArgb);
        }

        if (shape == ShapeKind.Freeform && source.TryGetFreehandWorldPoints(sourceObject, out var freehand))
        {
            for (var pointIndex = 0; pointIndex < freehand.Length; pointIndex++)
            {
                freehand[pointIndex] = TransformPoint(freehand[pointIndex], transform, identityTransform);
            }
            var prepared = new PreparedCompositionObject(
                PreparedCompositionKind.Freehand,
                destinationLayer,
                shape,
                PointF.Empty,
                SizeF.Empty,
                0,
                stroke,
                fillArgb,
                strokeArgb,
                atoms,
                PointF.Empty,
                PointF.Empty,
                PointF.Empty,
                freehand,
                [])
            {
                Distortions = distortions
            };
            if (source.TryGetFreehandBezierWorldNodes(sourceObject, out var freehandBezierNodes))
            {
                for (var nodeIndex = 0; nodeIndex < freehandBezierNodes.Length; nodeIndex++)
                {
                    var node = freehandBezierNodes[nodeIndex];
                    freehandBezierNodes[nodeIndex] = new PathBezierNode(
                        TransformPoint(node.Anchor, transform, identityTransform),
                        TransformPoint(node.IncomingControl, transform, identityTransform),
                        TransformPoint(node.OutgoingControl, transform, identityTransform));
                }

                prepared = prepared with { FreehandBezierNodes = freehandBezierNodes };
            }

            return WithGradient(
                prepared,
                source,
                sourceObject,
                transform,
                identityTransform,
                workItem.Alpha,
                workItem.TintArgb);
        }

        if (shape == ShapeKind.Line
            && source.TryGetLineEndpoint(sourceObject, startEndpoint: true, out var start)
            && source.TryGetLineEndpoint(sourceObject, startEndpoint: false, out var end))
        {
            var control1 = new PointF(source.CurveControlX[sourceObject], source.CurveControlY[sourceObject]);
            var control2 = new PointF(source.CurveControl2X[sourceObject], source.CurveControl2Y[sourceObject]);
            var transformedStart = TransformPoint(start, transform, identityTransform);
            var transformedControl1 = TransformPoint(control1, transform, identityTransform);
            var transformedControl2 = TransformPoint(control2, transform, identityTransform);
            var transformedEnd = TransformPoint(end, transform, identityTransform);
            var dx = transformedEnd.X - transformedStart.X;
            var dy = transformedEnd.Y - transformedStart.Y;
            var curveCenter = new PointF((transformedStart.X + transformedEnd.X) * 0.5f, (transformedStart.Y + transformedEnd.Y) * 0.5f);
            var curveSize = new SizeF(
                Math.Max(DrawingTopologyRules.MinStrokeSegmentUnits, MathF.Sqrt(dx * dx + dy * dy)),
                Math.Max(VectorUnits.FromPixels(3), stroke + VectorUnits.FromPixels(2)));
            return WithGradient(new PreparedCompositionObject(
                PreparedCompositionKind.Curve,
                destinationLayer,
                shape,
                curveCenter,
                curveSize,
                MathF.Atan2(dy, dx),
                stroke,
                fillArgb,
                strokeArgb,
                atoms,
                transformedStart,
                transformedControl1,
                transformedEnd,
                [],
                [],
                source.GetLineEndpointStyle(sourceObject, startEndpoint: true),
                source.GetLineEndpointStyle(sourceObject, startEndpoint: false))
            {
                Control2 = transformedControl2,
                Distortions = distortions
            }, source, sourceObject, transform, identityTransform, workItem.Alpha, workItem.TintArgb);
        }

        if (!identityTransform && HasShear(transform))
        {
            var skewContours = source.GetObjectBoundaryContours(sourceObject);
            for (var contourIndex = 0; contourIndex < skewContours.Length; contourIndex++)
            {
                var contour = skewContours[contourIndex];
                for (var pointIndex = 0; pointIndex < contour.Length; pointIndex++)
                {
                    contour[pointIndex] = TransformPoint(contour[pointIndex], transform, identityTransform);
                }
            }

            if (skewContours.Length > 0)
            {
                return WithGradient(new PreparedCompositionObject(
                    PreparedCompositionKind.Path,
                    destinationLayer,
                    ShapeKind.Path,
                    PointF.Empty,
                    SizeF.Empty,
                    0,
                    stroke,
                    fillArgb,
                    strokeArgb,
                    atoms,
                    PointF.Empty,
                    PointF.Empty,
                    PointF.Empty,
                    [],
                    skewContours)
                {
                    Distortions = distortions
                }, source, sourceObject, transform, identityTransform, workItem.Alpha, workItem.TintArgb);
            }
        }

        if (identityTransform)
        {
            return WithGradient(new PreparedCompositionObject(
                PreparedCompositionKind.Primitive,
                destinationLayer,
                shape,
                new PointF(source.X[sourceObject], source.Y[sourceObject]),
                new SizeF(source.Width[sourceObject], source.Height[sourceObject]),
                source.Angle[sourceObject],
                stroke,
                fillArgb,
                strokeArgb,
                atoms,
                PointF.Empty,
                PointF.Empty,
                PointF.Empty,
                [],
                [])
            {
                Distortions = distortions
            }, source, sourceObject, transform, identityTransform, workItem.Alpha, workItem.TintArgb);
        }

        var center = Transform(new PointF(source.X[sourceObject], source.Y[sourceObject]), transform);
        var sourceAngle = source.Angle[sourceObject];
        var cos = MathF.Cos(sourceAngle);
        var sin = MathF.Sin(sourceAngle);
        var widthAxis = Vector2.TransformNormal(new Vector2(cos * source.Width[sourceObject], sin * source.Width[sourceObject]), transform);
        var heightAxis = Vector2.TransformNormal(new Vector2(-sin * source.Height[sourceObject], cos * source.Height[sourceObject]), transform);
        var size = new SizeF(Math.Max(1, widthAxis.Length()), Math.Max(1, heightAxis.Length()));
        var angle = MathF.Atan2(widthAxis.Y, widthAxis.X);
        return WithGradient(new PreparedCompositionObject(
            PreparedCompositionKind.Primitive,
            destinationLayer,
            shape,
            center,
            size,
            angle,
            stroke,
            fillArgb,
            strokeArgb,
            atoms,
            PointF.Empty,
            PointF.Empty,
            PointF.Empty,
            [],
            [])
        {
            Distortions = distortions
        }, source, sourceObject, transform, identityTransform, workItem.Alpha, workItem.TintArgb);
    }

    private static string WrapImportedSvgTint(string source, SizeF size, int tintArgb)
    {
        var width = Math.Max(1, size.Width);
        var height = Math.Max(1, size.Height);
        var encodedSource = Convert.ToBase64String(Encoding.UTF8.GetBytes(source));
        return FormattableString.Invariant($"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:v2d="urn:vector-animation-engine:instance-appearance" v2d:multiply-tint="{(tintArgb & 0x00ffffff):X6}" width="{width:R}" height="{height:R}" viewBox="0 0 {width:R} {height:R}">
              <image width="{width:R}" height="{height:R}" preserveAspectRatio="none" href="data:image/svg+xml;base64,{encodedSource}"/>
            </svg>
            """);
    }

    private static bool TryPrepareImportedSvgTransform(
        PointF center,
        SizeF size,
        float angle,
        Matrix3x2 parentTransform,
        out PointF transformedCenter,
        out SizeF transformedSize,
        out float transformedAngle)
    {
        transformedCenter = PointF.Empty;
        transformedSize = SizeF.Empty;
        transformedAngle = 0;
        if (!IsFinite(parentTransform)
            || !float.IsFinite(center.X)
            || !float.IsFinite(center.Y)
            || !float.IsFinite(size.Width)
            || !float.IsFinite(size.Height)
            || size.Width < 1
            || size.Height < 1
            || !float.IsFinite(angle))
        {
            return false;
        }

        var determinant = parentTransform.M11 * parentTransform.M22
            - parentTransform.M12 * parentTransform.M21;
        if (!float.IsFinite(determinant) || determinant <= 0)
        {
            return false;
        }

        var cosine = MathF.Cos(angle);
        var sine = MathF.Sin(angle);
        var widthAxis = Vector2.TransformNormal(
            new Vector2(cosine * size.Width, sine * size.Width),
            parentTransform);
        var heightAxis = Vector2.TransformNormal(
            new Vector2(-sine * size.Height, cosine * size.Height),
            parentTransform);
        var widthLength = widthAxis.Length();
        var heightLength = heightAxis.Length();
        var axisDot = Vector2.Dot(widthAxis, heightAxis);
        var axisProduct = widthLength * heightLength;
        if (!float.IsFinite(widthLength)
            || !float.IsFinite(heightLength)
            || widthLength < 1
            || heightLength < 1
            || !float.IsFinite(axisDot)
            || !float.IsFinite(axisProduct)
            || MathF.Abs(axisDot) > 0.000001f * axisProduct)
        {
            return false;
        }

        var transformedCenterVector = Vector2.Transform(new Vector2(center.X, center.Y), parentTransform);
        var candidateAngle = MathF.Atan2(widthAxis.Y, widthAxis.X);
        if (!float.IsFinite(transformedCenterVector.X)
            || !float.IsFinite(transformedCenterVector.Y)
            || !float.IsFinite(candidateAngle))
        {
            return false;
        }

        transformedCenter = VectorUnits.Quantize(new PointF(
            transformedCenterVector.X,
            transformedCenterVector.Y));
        transformedSize = new SizeF(
            Math.Max(1, VectorUnits.Quantize(widthLength)),
            Math.Max(1, VectorUnits.Quantize(heightLength)));
        transformedAngle = candidateAngle;
        return float.IsFinite(transformedCenter.X)
            && float.IsFinite(transformedCenter.Y)
            && float.IsFinite(transformedSize.Width)
            && float.IsFinite(transformedSize.Height);
    }

    private static (string Source, PointF Center, SizeF Size) WrapImportedSvgTransform(
        string source,
        PointF center,
        SizeF size,
        float angle,
        Matrix3x2 parentTransform)
    {
        var width = Math.Max(1, size.Width);
        var height = Math.Max(1, size.Height);
        var localToWorld = Matrix3x2.CreateTranslation(-width * 0.5f, -height * 0.5f)
            * Matrix3x2.CreateRotation(angle)
            * Matrix3x2.CreateTranslation(center.X, center.Y)
            * parentTransform;
        var corners = new[]
        {
            Vector2.Transform(Vector2.Zero, localToWorld),
            Vector2.Transform(new Vector2(width, 0), localToWorld),
            Vector2.Transform(new Vector2(width, height), localToWorld),
            Vector2.Transform(new Vector2(0, height), localToWorld)
        };
        var left = corners.Min(point => point.X);
        var top = corners.Min(point => point.Y);
        var right = corners.Max(point => point.X);
        var bottom = corners.Max(point => point.Y);
        var outputWidth = Math.Max(1, right - left);
        var outputHeight = Math.Max(1, bottom - top);
        localToWorld.M31 -= left;
        localToWorld.M32 -= top;

        var invariant = CultureInfo.InvariantCulture;
        var matrix = string.Join(" ", new[]
        {
            localToWorld.M11.ToString("R", invariant),
            localToWorld.M12.ToString("R", invariant),
            localToWorld.M21.ToString("R", invariant),
            localToWorld.M22.ToString("R", invariant),
            localToWorld.M31.ToString("R", invariant),
            localToWorld.M32.ToString("R", invariant)
        });
        var encodedSource = Convert.ToBase64String(Encoding.UTF8.GetBytes(source));
        var wrappedSource = FormattableString.Invariant($"""
            <svg xmlns="http://www.w3.org/2000/svg" width="{outputWidth:R}" height="{outputHeight:R}" viewBox="0 0 {outputWidth:R} {outputHeight:R}">
              <image width="{width:R}" height="{height:R}" preserveAspectRatio="none" transform="matrix({matrix})" href="data:image/svg+xml;base64,{encodedSource}"/>
            </svg>
            """);
        return (
            wrappedSource,
            VectorUnits.Quantize(new PointF(left + outputWidth * 0.5f, top + outputHeight * 0.5f)),
            VectorUnits.Quantize(new SizeF(outputWidth, outputHeight)));
    }

    private static PackedSceneObject PreparePackedObject(CompositionWorkItem workItem)
    {
        var item = PrepareObject(workItem);
        if (item.Kind is PreparedCompositionKind.Path
            or PreparedCompositionKind.Freehand
            or PreparedCompositionKind.MixingStroke
            or PreparedCompositionKind.ImportedSvg
            or PreparedCompositionKind.Bitmap
            or PreparedCompositionKind.Text)
        {
            throw new InvalidOperationException("Sparse geometry cannot be written through the packed batch path.");
        }

        return new PackedSceneObject(
            item.DestinationLayer,
            item.Center,
            item.Size,
            item.Angle,
            item.Stroke,
            item.FillArgb,
            item.StrokeArgb,
            item.Atoms,
            item.Shape,
            item.Kind == PreparedCompositionKind.Curve ? item.Control : item.Center,
            item.Kind == PreparedCompositionKind.Curve ? item.Control2 : item.Center,
            item.StartEndpointStyle,
            item.EndEndpointStyle,
            item.LinearGradientEnabled,
            item.GradientKind,
            item.GradientStartArgb,
            item.GradientEndArgb,
            item.GradientStart,
            item.GradientEnd,
            item.GradientStops,
            item.ShapeVertexCount);
    }

    private static int AppendPreparedObject(VectorScene destination, PreparedCompositionObject item)
    {
        var index = item.Kind switch
        {
            PreparedCompositionKind.ImportedSvg => destination.AppendImportedSvgObject(
                item.DestinationLayer,
                item.Center,
                item.Size,
                item.Angle,
                item.ImportedSvgSource,
                item.ImportedSvgName),
            PreparedCompositionKind.Bitmap => destination.AddBitmapObject(
                item.DestinationLayer,
                item.Center,
                item.BitmapObjectData
                    ?? throw new InvalidOperationException("Prepared bitmap composition is missing its image payload."),
                item.Angle),
            PreparedCompositionKind.Text => destination.AppendTextObject(
                item.DestinationLayer,
                item.Center,
                item.Size,
                item.Angle,
                item.TextObjectData
                    ?? throw new InvalidOperationException("Prepared text composition is missing its editable payload."),
                item.FillArgb),
            PreparedCompositionKind.Path => item.UseSourceLocalPathGeometry
                ? item.PathBezierContours.Length > 0
                    ? destination.AppendPathBezierObjectLocalContours(
                        item.DestinationLayer,
                        item.Center,
                        item.Size,
                        item.Angle,
                        item.Contours,
                        item.PathBezierContours,
                        item.Stroke,
                        Color.FromArgb(item.FillArgb),
                        Color.FromArgb(item.StrokeArgb),
                        item.Atoms,
                        item.ShapeVertexCount)
                    : destination.AppendPathObjectLocalContours(
                        item.DestinationLayer,
                        item.Center,
                        item.Size,
                        item.Angle,
                        item.Contours,
                        item.Stroke,
                        Color.FromArgb(item.FillArgb),
                        Color.FromArgb(item.StrokeArgb),
                        item.Atoms,
                        item.ShapeVertexCount)
                : item.PathBezierContours.Length > 0
                    ? destination.AppendPathBezierObjectContours(
                        item.DestinationLayer,
                        item.PathBezierContours,
                        item.Stroke,
                        Color.FromArgb(item.FillArgb),
                        Color.FromArgb(item.StrokeArgb),
                        item.Atoms)
                    : destination.AppendPathObjectContours(
                        item.DestinationLayer,
                        item.Contours,
                        item.Stroke,
                        Color.FromArgb(item.FillArgb),
                        Color.FromArgb(item.StrokeArgb),
                        item.Atoms),
            PreparedCompositionKind.Freehand => item.FreehandBezierNodes.Length >= 2
                ? destination.AppendFreehandBezierStroke(
                    item.DestinationLayer,
                    item.FreehandBezierNodes,
                    item.Stroke,
                    Color.FromArgb(item.StrokeArgb),
                    item.Atoms)
                : destination.AppendFreehandStroke(
                    item.DestinationLayer,
                    item.Points,
                    item.Stroke,
                    Color.FromArgb(item.StrokeArgb),
                    item.Atoms),
            PreparedCompositionKind.MixingStroke => item.MixingRegion is { } mixingRegion
                ? destination.AppendMixingBrushRegion(
                    item.DestinationLayer,
                    mixingRegion,
                    item.Atoms)
                : destination.AppendMixingBrushStroke(
                    item.DestinationLayer,
                    item.MixingSamples,
                    item.Atoms),
            PreparedCompositionKind.Curve => destination.AppendPackedObject(
                item.DestinationLayer,
                item.Center,
                item.Size,
                item.Angle,
                item.Stroke,
                item.FillArgb,
                item.StrokeArgb,
                item.Atoms,
                ShapeKind.Line,
                item.Control,
                item.StartEndpointStyle,
                item.EndEndpointStyle,
                item.ShapeVertexCount,
                curveControl2: item.Control2),
            _ => destination.AppendPackedObject(
                item.DestinationLayer,
                item.Center,
                item.Size,
                item.Angle,
                item.Stroke,
                item.FillArgb,
                item.StrokeArgb,
                item.Atoms,
                item.Shape,
                item.Center,
                item.StartEndpointStyle,
                item.EndEndpointStyle,
                item.ShapeVertexCount,
                curveControl2: item.Center)
        };
        if (index >= 0)
        {
            destination.FillAutoMergeProtected[index] = item.FillAutoMergeProtected;
            if (item.Kind is PreparedCompositionKind.ImportedSvg or PreparedCompositionKind.Bitmap)
            {
                destination.Argb[index] = item.FillArgb;
            }
            if (item.Distortions.Length > 0) destination.SetObjectDistortionsForComposition(index, item.Distortions);
        }
        if (item.LinearGradientEnabled)
        {
            destination.SetGradientPaint(
                index,
                item.GradientKind,
                item.GradientStops,
                item.GradientStart,
                item.GradientEnd);
            if (item.GradientPath.Length > 1) destination.SetGradientPath(index, item.GradientPath);
            if (item.ShapeGradientMappingContours.Length > 0)
            {
                destination.SetShapeGradientMapping(index, item.ShapeGradientMappingContours);
            }
        }

        return index;
    }

    private static PreparedCompositionObject WithGradient(
        PreparedCompositionObject item,
        VectorScene source,
        int sourceObject,
        Matrix3x2 transform,
        bool identityTransform,
        float alpha,
        int tintArgb,
        bool preserveSourceGeometry = false)
    {
        item = item with
        {
            ShapeVertexCount = source.GetShapeVertexCount(sourceObject),
            FillAutoMergeProtected = source.FillAutoMergeProtected[sourceObject]
        };
        if (!source.HasGradient(sourceObject)) return item;
        return item with
        {
            LinearGradientEnabled = true,
            GradientKind = source.GetGradientKind(sourceObject),
            GradientStartArgb = ApplyInstanceAppearance(source.GradientStartArgb[sourceObject], alpha, tintArgb),
            GradientEndArgb = ApplyInstanceAppearance(source.GradientEndArgb[sourceObject], alpha, tintArgb),
            GradientStart = preserveSourceGeometry
                ? source.GetGradientStart(sourceObject)
                : TransformPoint(source.GetGradientStart(sourceObject), transform, identityTransform),
            GradientEnd = preserveSourceGeometry
                ? source.GetGradientEnd(sourceObject)
                : TransformPoint(source.GetGradientEnd(sourceObject), transform, identityTransform),
            GradientStops = source.GetGradientStops(sourceObject)
                .Select(stop => new GradientStop(stop.Position, ApplyInstanceAppearance(stop.Argb, alpha, tintArgb)))
                .ToArray(),
            GradientPath = source.TryGetGradientPathWorldPoints(sourceObject, out var gradientPath)
                ? preserveSourceGeometry
                    ? gradientPath
                    : gradientPath.Select(point => TransformPoint(point, transform, identityTransform)).ToArray()
                : [],
            ShapeGradientMappingContours = source.TryGetShapeGradientMappingWorldContours(sourceObject, out var mappingContours)
                ? preserveSourceGeometry
                    ? mappingContours
                    : mappingContours
                        .Select(contour => contour.Select(point => TransformPoint(point, transform, identityTransform)).ToArray())
                        .ToArray()
                : []
        };
    }

    private static bool TryPrepareEditableTextTransform(
        VectorScene source,
        int sourceObject,
        Matrix3x2 transform,
        bool identityTransform,
        float determinant,
        out PointF center,
        out SizeF size,
        out float angle)
    {
        PrepareApproximateObjectTransform(
            source,
            sourceObject,
            transform,
            identityTransform,
            out center,
            out size,
            out angle,
            out var widthAxis,
            out var heightAxis);
        var widthLength = widthAxis.Length();
        var heightLength = heightAxis.Length();
        var axisDot = Vector2.Dot(widthAxis, heightAxis);
        return determinant > 0
            && widthLength > 0
            && heightLength > 0
            && Math.Abs(axisDot) <= 0.0001f * widthLength * heightLength;
    }

    private static void PrepareApproximateObjectTransform(
        VectorScene source,
        int sourceObject,
        Matrix3x2 transform,
        bool identityTransform,
        out PointF center,
        out SizeF size,
        out float angle)
    {
        PrepareApproximateObjectTransform(
            source,
            sourceObject,
            transform,
            identityTransform,
            out center,
            out size,
            out angle,
            out _,
            out _);
    }

    private static void PrepareApproximateObjectTransform(
        VectorScene source,
        int sourceObject,
        Matrix3x2 transform,
        bool identityTransform,
        out PointF center,
        out SizeF size,
        out float angle,
        out Vector2 widthAxis,
        out Vector2 heightAxis)
    {
        var sourceAngle = source.Angle[sourceObject];
        var cosine = MathF.Cos(sourceAngle);
        var sine = MathF.Sin(sourceAngle);
        if (identityTransform)
        {
            center = new PointF(source.X[sourceObject], source.Y[sourceObject]);
            size = new SizeF(source.Width[sourceObject], source.Height[sourceObject]);
            angle = sourceAngle;
            widthAxis = new Vector2(cosine * size.Width, sine * size.Width);
            heightAxis = new Vector2(-sine * size.Height, cosine * size.Height);
            return;
        }

        center = Transform(new PointF(source.X[sourceObject], source.Y[sourceObject]), transform);
        widthAxis = Vector2.TransformNormal(
            new Vector2(cosine * source.Width[sourceObject], sine * source.Width[sourceObject]),
            transform);
        heightAxis = Vector2.TransformNormal(
            new Vector2(-sine * source.Height[sourceObject], cosine * source.Height[sourceObject]),
            transform);
        size = new SizeF(Math.Max(1, widthAxis.Length()), Math.Max(1, heightAxis.Length()));
        angle = MathF.Atan2(widthAxis.Y, widthAxis.X);
    }

    private static int MultiplyTintArgb(int first, int second)
    {
        return unchecked((int)0xff000000)
            | (MultiplyColorChannel((first >>> 16) & 0xff, (second >>> 16) & 0xff) << 16)
            | (MultiplyColorChannel((first >>> 8) & 0xff, (second >>> 8) & 0xff) << 8)
            | MultiplyColorChannel(first & 0xff, second & 0xff);
    }

    private static int ApplyInstanceAppearance(int argb, float alpha, int tintArgb)
    {
        var resultAlpha = (int)Math.Clamp(((argb >>> 24) & 0xff) * alpha, 0f, 255f);
        return (resultAlpha << 24)
            | (MultiplyColorChannel((argb >>> 16) & 0xff, (tintArgb >>> 16) & 0xff) << 16)
            | (MultiplyColorChannel((argb >>> 8) & 0xff, (tintArgb >>> 8) & 0xff) << 8)
            | MultiplyColorChannel(argb & 0xff, tintArgb & 0xff);
    }

    private static int MultiplyColorChannel(int first, int second) => (first * second + 127) / 255;

    private static Matrix3x2 InstanceMatrix(
        DrawingObjectDefinition drawingObject,
        DrawingObjectInstanceDefinition instance,
        int frame)
    {
        return InstanceMatrix(drawingObject, instance.EvaluateState(frame));
    }

    private static Matrix3x2 InstanceMatrix(
        DrawingObjectDefinition drawingObject,
        InstanceFrameState state)
    {
        return Matrix3x2.CreateTranslation(-drawingObject.Anchor.X, -drawingObject.Anchor.Y)
            * DrawingObjectInstanceDefinition.CreatePlanarTransform(state);
    }

    private static Matrix4x4 InstanceSpatialMatrix(
        DrawingObjectDefinition drawingObject,
        DrawingObjectInstanceDefinition instance,
        int frame)
    {
        return InstanceSpatialMatrix(drawingObject, instance.EvaluateState(frame));
    }

    private static Matrix4x4 InstanceSpatialMatrix(
        DrawingObjectDefinition drawingObject,
        InstanceFrameState state)
    {
        if (InstanceSpatialIsPlanar(state)) return Lift(InstanceMatrix(drawingObject, state));
        return Matrix4x4.CreateTranslation(-drawingObject.Anchor.X, -drawingObject.Anchor.Y, 0)
            * DrawingObjectInstanceDefinition.CreateSpatialTransform(state);
    }

    private static Vector3 CreateRootExtrusionVector(
        InstanceFrameState state,
        Matrix4x4 spatialTransform)
    {
        return state.ScaleZ == 0
            ? Vector3.Zero
            : Vector3.TransformNormal(Vector3.UnitZ, spatialTransform);
    }

    private static SceneCompositionObjectPose CreateObjectPose(
        Matrix3x2 flattenedTransform,
        Matrix4x4 spatialTransform,
        bool spatialIsPlanar,
        Vector3 extrusionVector)
    {
        var flatStrokeScale = MathF.Sqrt(Math.Abs(
            flattenedTransform.M11 * flattenedTransform.M22
            - flattenedTransform.M12 * flattenedTransform.M21));
        if (!float.IsFinite(flatStrokeScale)) flatStrokeScale = 1f;
        if (spatialIsPlanar)
        {
            return new SceneCompositionObjectPose(
                Matrix4x4.Identity,
                extrusionVector,
                flatStrokeScale);
        }
        if (!IsFinite(flattenedTransform)
            || !IsFinite(spatialTransform)
            || !Matrix3x2.Invert(flattenedTransform, out var inverse)
            || !IsFinite(inverse))
        {
            // A collapsed flat plane has no unique inverse, so retain its finite flattened presentation.
            return new SceneCompositionObjectPose(
                Matrix4x4.Identity,
                extrusionVector,
                flatStrokeScale);
        }

        var flatToScene = Lift(inverse) * spatialTransform;
        if (!IsFinite(flatToScene))
        {
            return new SceneCompositionObjectPose(
                Matrix4x4.Identity,
                extrusionVector,
                flatStrokeScale);
        }
        return new SceneCompositionObjectPose(
            IsNearlyIdentity(flatToScene) ? Matrix4x4.Identity : flatToScene,
            extrusionVector,
            flatStrokeScale);
    }

    private static bool InstanceSpatialIsPlanar(DrawingObjectInstanceDefinition instance, int frame)
    {
        return InstanceSpatialIsPlanar(instance.EvaluateState(frame));
    }

    private static bool InstanceSpatialIsPlanar(InstanceFrameState state)
    {
        var effectiveZ = state.Z + state.ScalePivot.Z * (1f - state.ScaleZ);
        return effectiveZ == 0
            && state.RotationX == 0
            && state.RotationY == 0
            && (state.ScaleZ == 0 || state.ScaleZ == 1);
    }

    private static Matrix4x4 Lift(Matrix3x2 transform)
    {
        return new Matrix4x4(
            transform.M11, transform.M12, 0, 0,
            transform.M21, transform.M22, 0, 0,
            0, 0, 1, 0,
            transform.M31, transform.M32, 0, 1);
    }

    private static bool IsFinite(Matrix3x2 transform)
    {
        return float.IsFinite(transform.M11)
            && float.IsFinite(transform.M12)
            && float.IsFinite(transform.M21)
            && float.IsFinite(transform.M22)
            && float.IsFinite(transform.M31)
            && float.IsFinite(transform.M32);
    }

    private static bool IsFinite(Matrix4x4 transform)
    {
        return float.IsFinite(transform.M11)
            && float.IsFinite(transform.M12)
            && float.IsFinite(transform.M13)
            && float.IsFinite(transform.M14)
            && float.IsFinite(transform.M21)
            && float.IsFinite(transform.M22)
            && float.IsFinite(transform.M23)
            && float.IsFinite(transform.M24)
            && float.IsFinite(transform.M31)
            && float.IsFinite(transform.M32)
            && float.IsFinite(transform.M33)
            && float.IsFinite(transform.M34)
            && float.IsFinite(transform.M41)
            && float.IsFinite(transform.M42)
            && float.IsFinite(transform.M43)
            && float.IsFinite(transform.M44);
    }

    private static bool IsNearlyIdentity(Matrix4x4 transform)
    {
        return NearlyEqual(transform.M11, 1) && NearlyEqual(transform.M12, 0)
            && NearlyEqual(transform.M13, 0) && NearlyEqual(transform.M14, 0)
            && NearlyEqual(transform.M21, 0) && NearlyEqual(transform.M22, 1)
            && NearlyEqual(transform.M23, 0) && NearlyEqual(transform.M24, 0)
            && NearlyEqual(transform.M31, 0) && NearlyEqual(transform.M32, 0)
            && NearlyEqual(transform.M33, 1) && NearlyEqual(transform.M34, 0)
            && NearlyEqual(transform.M41, 0) && NearlyEqual(transform.M42, 0)
            && NearlyEqual(transform.M43, 0) && NearlyEqual(transform.M44, 1);
    }

    private static bool NearlyEqual(float value, float expected)
    {
        return Math.Abs(value - expected) <= 0.00001f * Math.Max(1f, Math.Max(Math.Abs(value), Math.Abs(expected)));
    }

    private static bool HasShear(Matrix3x2 transform)
    {
        var axisDot = transform.M11 * transform.M21 + transform.M12 * transform.M22;
        var scale = Math.Max(1, MathF.Sqrt(
            transform.M11 * transform.M11
            + transform.M12 * transform.M12
            + transform.M21 * transform.M21
            + transform.M22 * transform.M22));
        return Math.Abs(axisDot) > 0.0001f * scale * scale;
    }

    private static PointF Transform(PointF point, Matrix3x2 transform)
    {
        var transformed = Vector2.Transform(new Vector2(point.X, point.Y), transform);
        return VectorUnits.Quantize(new PointF(transformed.X, transformed.Y));
    }

    private static PointF TransformPoint(PointF point, Matrix3x2 transform, bool identityTransform)
    {
        return identityTransform ? VectorUnits.Quantize(point) : Transform(point, transform);
    }
}
