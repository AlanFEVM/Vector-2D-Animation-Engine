using System.Numerics;
using System.Runtime.CompilerServices;
using Clipper2Lib;

namespace VectorAnimationEngine;

internal sealed partial class StageControl
{
    private static readonly float[] Reference3DSrgbByteToLinear =
        CreateReference3DSrgbByteToLinearTable();
    private const int MaximumReference3DShadowCastersPerReceiver = 12;
    private const int MaximumReference3DShadowProjectionsPerPlan = 4096;
    private const int Reference3DAreaLightSamples = 9;
    private const float Reference3DAreaLightPointSize = 0.1f;
    private const int Reference3DLocalLightGradientStops = 13;
    private const int Reference3DLocalLightCircleSegments = 48;
    private const int MaximumReference3DLocalLightLayersPerReceiver = 32;
    private const int Reference3DVectorLightingMinimumItems = 128;
    private const int MaximumReference3DVectorLightingPathPoints = 256;
    private const float Reference3DShadowRayBias = 0.01f;
    private const float Reference3DMaximumSoftShadowRetention = 0.25f;

    private readonly record struct Reference3DShadowCasterKey(
        int ObjectIndex,
        int SurfaceKind,
        int SurfaceSlot,
        int FragmentSlot,
        ulong StableFragmentIdentity);

    private sealed record Reference3DShadowCaster(
        Reference3DRenderItem Item,
        SpatialOpticalMaterial Material,
        Reference3DProjectedContour[] Contours,
        Vector3[][] WorldContours,
        Vector3 WorldMinimum,
        Vector3 WorldMaximum);

    // Shadow geometry is shared by every receiver and light sample in one plan.
    // The render plan is immutable after this method returns, so this cache is
    // deliberately plan-scoped rather than shared across frames.
    private sealed class Reference3DOpticsEvaluationContext(
        StageControl stage,
        IReadOnlyList<Reference3DRenderItem> items)
    {
        private readonly StageControl _stage = stage;
        private readonly IReadOnlyList<Reference3DRenderItem> _items = items;
        private readonly Dictionary<int, SpatialOpticalMaterial> _materials = [];
        private Reference3DShadowCaster[]? _shadowCasters;

        public SpatialOpticalMaterial Material(int objectIndex)
        {
            if (_materials.TryGetValue(objectIndex, out var material)) return material;
            material = _stage.GetReference3DOpticalMaterial(objectIndex);
            _materials.Add(objectIndex, material);
            return material;
        }

        public IReadOnlyList<Reference3DShadowCaster> ShadowCasters =>
            _shadowCasters ??= BuildShadowCasters();

        private Reference3DShadowCaster[] BuildShadowCasters()
        {
            var result = new List<Reference3DShadowCaster>();
            var seen = new HashSet<Reference3DShadowCasterKey>();
            foreach (var item in _items)
            {
                if (item.Kind is not (Reference3DRenderKind.Back
                        or Reference3DRenderKind.Side
                        or Reference3DRenderKind.FrontFill)
                    || !item.Plane.IsValid)
                {
                    continue;
                }

                var key = new Reference3DShadowCasterKey(
                    item.ObjectIndex,
                    Reference3DNormalizedSurfaceKind(item.Kind),
                    item.SurfaceSlot,
                    item.FragmentSlot,
                    item.StableFragmentIdentity);
                // Match the old per-receiver rule: the first item with a
                // fragment identity owns that identity for this plan.
                if (!seen.Add(key)) continue;

                var material = Material(item.ObjectIndex);
                if (!StageControl.TryGetReference3DShadowCasterContours(item, out var contours)
                    || !TryBuildShadowCasterWorldContours(
                        item,
                        contours,
                        out var worldContours,
                        out var worldMinimum,
                        out var worldMaximum))
                {
                    continue;
                }
                result.Add(new Reference3DShadowCaster(
                    item,
                    material,
                    contours.ToArray(),
                    worldContours,
                    worldMinimum,
                    worldMaximum));
            }
            return result.ToArray();
        }

        private bool TryBuildShadowCasterWorldContours(
            Reference3DRenderItem item,
            IReadOnlyList<Reference3DProjectedContour> contours,
            out Vector3[][] worldContours,
            out Vector3 worldMinimum,
            out Vector3 worldMaximum)
        {
            var result = new List<Vector3[]>(contours.Count);
            worldMinimum = new Vector3(
                float.PositiveInfinity,
                float.PositiveInfinity,
                float.PositiveInfinity);
            worldMaximum = new Vector3(
                float.NegativeInfinity,
                float.NegativeInfinity,
                float.NegativeInfinity);
            foreach (var contour in contours)
            {
                if (!contour.Closed || contour.Points.Length < 3) continue;
                var world = new Vector3[contour.Points.Length];
                var valid = true;
                for (var pointIndex = 0; pointIndex < contour.Points.Length; pointIndex++)
                {
                    var screen = contour.Points[pointIndex];
                    if (!_stage.TryGetReference3DRay(screen, out var cameraRay)
                        || !StageControl.TryIntersectReference3DPlane(
                            item.Plane,
                            cameraRay.Origin,
                            cameraRay.Direction,
                            out var worldPoint))
                    {
                        valid = false;
                        break;
                    }
                    world[pointIndex] = worldPoint;
                    worldMinimum = Vector3.Min(worldMinimum, worldPoint);
                    worldMaximum = Vector3.Max(worldMaximum, worldPoint);
                }
                if (valid) result.Add(world);
            }

            worldContours = result.ToArray();
            return worldContours.Length > 0
                && Finite(worldMinimum)
                && Finite(worldMaximum);
        }
    }

    private void SetReference3DSceneDefinition(SceneDefinition? scene)
    {
        if (ReferenceEquals(_referenceSceneDefinition, scene)) return;
        _referenceSceneDefinition = scene;
        InvalidateReference3DRenderPlanCache();
        Invalidate();
    }

    private ulong GetReference3DOpticsContentHash()
    {
        var definition = _referenceSceneDefinition;
        if (definition is null || !UsesSceneOpticsProjection) return 0;

        var composition = ReferenceEquals(_sceneCompositionResultScene, Scene)
            ? _sceneCompositionResult
            : null;
        var cacheKey = new Reference3DOpticsContentHashCacheKey(
            definition,
            definition.LightingRevision,
            Frame,
            composition,
            _sceneCompositionResultScene,
            _reference3DRenderPlanEpoch);
        if (_reference3DOpticsContentHashCacheValid
            && _reference3DOpticsContentHashCacheKey == cacheKey)
        {
            return _reference3DOpticsContentHashCacheValue;
        }

        const ulong offset = 14695981039346656037UL;
        var hash = offset;
        AddReference3DHashInt(ref hash, RuntimeHelpers.GetHashCode(definition));
        AddReference3DHashInt(ref hash, Frame);
        var frame = Math.Max(0, Frame);
        var evaluatedLightCount = 0;
        var lights = definition.Lights;
        for (var lightIndex = 0; lightIndex < lights.Count; lightIndex++)
        {
            var light = lights[lightIndex];
            if (!definition.TryEvaluateLightSettings(light.Id, frame, out var settings)) continue;
            evaluatedLightCount++;
            AddReference3DHashString(ref hash, light.Id);
            AddReference3DHashInt(ref hash, (int)light.Kind);
            AddReference3DHashInt(ref hash, settings.Enabled ? 1 : 0);
            AddReference3DHashInt(ref hash, settings.ColorArgb);
            AddReference3DHashInt(ref hash, BitConverter.SingleToInt32Bits(settings.Intensity));
            AddReference3DHashInt(ref hash, BitConverter.SingleToInt32Bits(settings.Range));
            AddReference3DHashVector(ref hash, settings.Position);
            AddReference3DHashVector(ref hash, settings.RotationDegrees);
            AddReference3DHashInt(ref hash, BitConverter.SingleToInt32Bits(settings.AreaSize.X));
            AddReference3DHashInt(ref hash, BitConverter.SingleToInt32Bits(settings.AreaSize.Y));
            AddReference3DHashInt(ref hash, settings.CastsShadows ? 1 : 0);
            AddReference3DHashInt(ref hash, BitConverter.SingleToInt32Bits(settings.ShadowStrength));
            AddReference3DHashInt(ref hash, BitConverter.SingleToInt32Bits(settings.ShadowSoftness));
        }
        AddReference3DHashInt(ref hash, evaluatedLightCount);

        if (composition is not null)
        {
            AddReference3DHashInt(ref hash, RuntimeHelpers.GetHashCode(composition));
            var materialHash = GetReference3DOpticsMaterialHash(composition);
            AddReference3DHashInt(ref hash, (int)materialHash);
            AddReference3DHashInt(ref hash, (int)(materialHash >> 32));
        }

        _reference3DOpticsContentHashCacheKey = cacheKey;
        _reference3DOpticsContentHashCacheValue = hash;
        _reference3DOpticsContentHashCacheValid = true;
        return hash;
    }

    private ulong GetReference3DOpticsMaterialHash(SceneCompositionResult composition)
    {
        if (_reference3DOpticsMaterialHashValid
            && ReferenceEquals(_reference3DOpticsMaterialHashComposition, composition))
        {
            return _reference3DOpticsMaterialHash;
        }

        // Composition results are immutable snapshots. Their identity is
        // already part of the render-plan key, so hashing every material in
        // a large snapshot only duplicates work on every playback frame.
        var hash = unchecked((ulong)(uint)RuntimeHelpers.GetHashCode(composition));

        _reference3DOpticsMaterialHashComposition = composition;
        _reference3DOpticsMaterialHash = hash;
        _reference3DOpticsMaterialHashValid = true;
        return hash;
    }

    private static void AddReference3DHashVector(ref ulong hash, Vector3 value)
    {
        AddReference3DHashInt(ref hash, BitConverter.SingleToInt32Bits(value.X));
        AddReference3DHashInt(ref hash, BitConverter.SingleToInt32Bits(value.Y));
        AddReference3DHashInt(ref hash, BitConverter.SingleToInt32Bits(value.Z));
    }

    private Reference3DRenderItem[] ApplyReference3DOptics(Reference3DRenderItem[] items)
    {
        LastReference3DOpticalLightEvaluations = 0;
        LastReference3DShadowProjectionCount = 0;
        LastReference3DShadowLayerCount = 0;
        LastReference3DLocalLightLayerCount = 0;
        LastReference3DOpticalRasterLod = -1;
        var definition = _referenceSceneDefinition;
        if (items.Length == 0
            || definition is null
            || !UsesSceneOpticsProjection)
        {
            return items;
        }

        Reference3DOpticsPlanBuildCount++;
        var lights = EvaluateReference3DSceneLights(definition);
        var ambientIrradiance = EvaluateReference3DAmbientIrradiance(lights);
        var evaluationContext = new Reference3DOpticsEvaluationContext(this, items);
        var hasEffectiveLighting = lights.Any(light =>
        {
            var settings = light.Settings;
            return settings.Enabled
                && settings.Intensity > 0f
                && settings.IsValid(light.Kind);
        });
        // Ambient-only lighting is spatially uniform, so vector evaluation is exact for small plans.
        var hasOnlyUniformLighting = !lights.Any(light =>
        {
            var settings = light.Settings;
            return light.Kind != SceneLightKind.Ambient
                && settings.Enabled
                && settings.Intensity > 0f
                && settings.IsValid(light.Kind);
        });
        if (TryApplyReference3DVectorOpticsParallel(
                items,
                lights,
                ambientIrradiance,
                hasEffectiveLighting,
                out var parallelItems))
        {
            return parallelItems;
        }
        var result = new Reference3DRenderItem[items.Length];
        var materials = new SpatialOpticalMaterial[items.Length];
        var surfaceBounds = new Rectangle[items.Length];
        var receivesLinearOptics = new bool[items.Length];
        var hasSurfaceBounds = new bool[items.Length];
        var sourceMaterialOpacities = new float[items.Length];
        Dictionary<(int ObjectIndex, int SurfaceSlot), Reference3DProjectedContour[]>?
            strokeSurfaceCache = null;
        Dictionary<int, GradientStop[]>? gradientStopsCache = null;
        Dictionary<int, SpatialOpticalMaterial>? materialCache = null;
        Dictionary<(int ObjectIndex, Reference3DSurfacePlaneKey PlaneKey), Reference3DVectorLighting>?
            vectorLightingCache = null;
        Reference3DProjectedContour[]?[]? parallelStrokeSurfaceContours = null;
        if (items.Length >= 64 && ParallelBatch.WorkerCount(items.Length, 16) > 1)
        {
            parallelStrokeSurfaceContours = new Reference3DProjectedContour[]?[items.Length];
            ParallelBatch.For(
                items.Length,
                16,
                (_, start, end) =>
                {
                    for (var index = start; index < end; index++)
                    {
                        var item = items[index];
                        if (item.Kind != Reference3DRenderKind.FrontStroke
                            || item.OcclusionContours is not null)
                        {
                            continue;
                        }

                        parallelStrokeSurfaceContours[index] =
                            GetReference3DProjectedStrokeOcclusionContours(
                                item.ObjectIndex,
                                item.Contours);
                    }
                });
        }
        var hasCenterRay = TryGetReference3DRay(
            new PointF(ClientSize.Width * 0.5f, ClientSize.Height * 0.5f),
            out var centerRay);
        for (var index = 0; index < items.Length; index++)
        {
            var item = items[index];
            sourceMaterialOpacities[index] = item.MaterialOpacity;
            if (!Reference3DOpticsAffects(item)
                || !TryGetReference3DSurfaceSample(
                    item,
                    hasCenterRay,
                    centerRay,
                    out var scenePoint,
                    out var viewDirection))
            {
                result[index] = item;
                continue;
            }

            materialCache ??= new Dictionary<int, SpatialOpticalMaterial>(items.Length);
            if (!materialCache.TryGetValue(item.ObjectIndex, out var material))
            {
                material = GetReference3DOpticalMaterial(item.ObjectIndex);
                materialCache.Add(item.ObjectIndex, material);
            }
            materials[index] = material;
            if (item.Kind == Reference3DRenderKind.FrontStroke)
            {
                var opticalSurfaceContours = item.OcclusionContours;
                if (opticalSurfaceContours is null
                    && parallelStrokeSurfaceContours is { } preparedContours
                    && preparedContours[index] is { } prepared)
                {
                    opticalSurfaceContours = prepared;
                }
                if (opticalSurfaceContours is null)
                {
                    strokeSurfaceCache ??= [];
                    var surfaceKey = (item.ObjectIndex, item.SurfaceSlot);
                    if (!strokeSurfaceCache.TryGetValue(surfaceKey, out opticalSurfaceContours))
                    {
                        opticalSurfaceContours = GetReference3DProjectedStrokeOcclusionContours(
                            item.ObjectIndex,
                            item.Contours);
                        strokeSurfaceCache.Add(surfaceKey, opticalSurfaceContours);
                    }
                }
                if (opticalSurfaceContours.Length == 0)
                {
                    result[index] = item;
                    continue;
                }
                item = item with { OpticalSurfaceContours = opticalSurfaceContours };
            }
            else if (item.Kind == Reference3DRenderKind.IntersectionEdge
                && (item.OpticalSurfaceContours is not { Length: > 0 }
                    || !hasEffectiveLighting))
            {
                result[index] = item;
                continue;
            }
            var finiteLightReceiverPlane = OrientReference3DFiniteLightReceiverPlane(
                item,
                viewDirection);
            var response = EvaluateReference3DOpticalResponse(material, ambientIrradiance);
            var normalizedMaterial = material.Normalize();
            var dielectricF0 = Reference3DDielectricF0(normalizedMaterial);
            Reference3DVectorLighting? vectorLighting = null;
            if (ShouldUseReference3DVectorLighting(
                    item,
                    items.Length,
                    allowBelowMinimum: hasOnlyUniformLighting
                        && (uint)item.ObjectIndex < Scene.ObjectCount
                        && Scene.ShapeKind[item.ObjectIndex] == ShapeKind.Path
                        && !Scene.HasGradient(item.ObjectIndex)
                        && material.Transmission <= 0.000001f))
            {
                vectorLightingCache ??= new Dictionary<
                    (int ObjectIndex, Reference3DSurfacePlaneKey PlaneKey),
                    Reference3DVectorLighting>(items.Length);
                var lightingKey = (item.ObjectIndex, item.PlaneKey);
                if (vectorLightingCache.TryGetValue(lightingKey, out var cachedLighting))
                {
                    vectorLighting = cachedLighting;
                }
                else if (TryEvaluateReference3DVectorLighting(
                             item,
                             finiteLightReceiverPlane,
                             scenePoint,
                             viewDirection,
                             material,
                             response.AmbientIrradiance,
                             lights,
                             out var lightEvaluations) is { } evaluatedLighting)
                {
                    vectorLightingCache.Add(lightingKey, evaluatedLighting);
                    vectorLighting = evaluatedLighting;
                    LastReference3DOpticalLightEvaluations += lightEvaluations;
                }
            }
            var vectorLightingApplied = false;
            int? vectorLightingArgb = null;
            GradientStop[]? opticalGradientStops = null;
            GradientStop[]? opticalStrokeGradientStops = null;
            if (vectorLighting is { } vector)
            {
                if (!hasEffectiveLighting)
                {
                    vector = new Reference3DVectorLighting(
                        Vector3.One,
                        Vector3.Zero);
                }

                if (item.Kind == Reference3DRenderKind.FrontFill
                    && Scene.HasGradient(item.ObjectIndex))
                {
                    var litStops = BuildReference3DOpticalGradientStops(
                        GetSourceGradientStops(item.ObjectIndex),
                        vector,
                        normalizedMaterial.Metallic,
                        dielectricF0);
                    if (litStops.Length > 0)
                    {
                        opticalGradientStops = litStops;
                        vectorLightingApplied = true;
                    }
                }
                else if (item.Kind == Reference3DRenderKind.FrontStroke
                         && Scene.HasGradient(item.ObjectIndex))
                {
                    var litStops = BuildReference3DOpticalGradientStops(
                        GetSourceGradientStops(item.ObjectIndex),
                        vector,
                        normalizedMaterial.Metallic,
                        dielectricF0);
                    if (litStops.Length > 0)
                    {
                        opticalStrokeGradientStops = litStops;
                        vectorLightingApplied = true;
                    }
                }
                else
                {
                    var baseArgb = item.Kind == Reference3DRenderKind.FrontStroke
                        && TryGetReference3DSolidStrokeAlbedo(
                            item.ObjectIndex,
                            out var solidStrokeAlbedo)
                            ? solidStrokeAlbedo
                        : item.Kind == Reference3DRenderKind.FrontFill
                            && (uint)item.ObjectIndex < Scene.Argb.Length
                            ? Scene.Argb[item.ObjectIndex]
                            : GetReference3DExtrusionSurfaceColor(item).ToArgb();
                    vectorLightingArgb = EvaluateReference3DLinearLightingArgb(
                        baseArgb,
                        vector.DiffuseIrradiance,
                        vector.SpecularRadiance,
                        normalizedMaterial.Metallic,
                        vector.FresnelRadiance,
                        dielectricF0);
                    vectorLightingApplied = true;
                }
            }
            if (!vectorLightingApplied
                && item.Kind == Reference3DRenderKind.FrontStroke
                && Scene.HasGradient(item.ObjectIndex))
            {
                opticalStrokeGradientStops = BuildReference3DOpticalStrokeGradientStops(
                    GetSourceGradientStops(item.ObjectIndex),
                    response.AmbientIrradiance,
                    normalizedMaterial.Metallic,
                    dielectricF0);
            }
            Reference3DLocalLightLayer[] localLightLayers = vectorLightingApplied
                ? Array.Empty<Reference3DLocalLightLayer>()
                : BuildReference3DLocalLightLayers(
                    item,
                    finiteLightReceiverPlane,
                    evaluationContext,
                    viewDirection,
                    material,
                    lights);
            var opticalMaterialOpacity = Math.Clamp(
                item.MaterialOpacity * response.OpacityScale,
                0f,
                1f);
            int? solidStrokeOpticalBaseArgb = null;
            var hasSolidStrokeAlbedo = false;
            var strokeAlbedo = 0;
            Color strokeColor = Color.Transparent;
            if (item.Kind == Reference3DRenderKind.IntersectionEdge)
            {
                strokeColor = Color.FromArgb(item.EdgeArgb);
                if (strokeColor.A > 0)
                {
                    strokeAlbedo = Color.FromArgb(
                        255,
                        strokeColor.R,
                        strokeColor.G,
                        strokeColor.B).ToArgb();
                    hasSolidStrokeAlbedo = true;
                }
            }
            else if (item.Kind == Reference3DRenderKind.FrontStroke)
            {
                hasSolidStrokeAlbedo = TryGetReference3DSolidStrokeAlbedo(
                    item.ObjectIndex,
                    out strokeAlbedo);
                if (hasSolidStrokeAlbedo) strokeColor = Color.FromArgb(strokeAlbedo);
            }
            if (hasSolidStrokeAlbedo)
            {
                var metallic = normalizedMaterial.Metallic;
                opticalMaterialOpacity *= strokeColor.A / 255f;
                if (item.Kind == Reference3DRenderKind.FrontStroke)
                {
                    strokeAlbedo = Color.FromArgb(
                        255,
                        strokeColor.R,
                        strokeColor.G,
                        strokeColor.B).ToArgb();
                }
                solidStrokeOpticalBaseArgb = vectorLightingApplied
                    && vectorLighting is { } lighting
                    ? EvaluateReference3DLinearLightingArgb(
                        strokeAlbedo,
                        !hasEffectiveLighting
                            ? Vector3.One
                            : lighting.DiffuseIrradiance,
                        !hasEffectiveLighting
                            ? Vector3.Zero
                            : lighting.SpecularRadiance,
                        metallic,
                        !hasEffectiveLighting
                            ? Vector3.Zero
                            : lighting.FresnelRadiance,
                        dielectricF0)
                    : Reference3DLinearVectorHasVisibleEnergy(
                        response.AmbientIrradiance)
                        ? EvaluateReference3DLinearLightingArgb(
                            strokeAlbedo,
                            response.AmbientIrradiance,
                            Vector3.Zero,
                            metallic,
                            dielectricF0: dielectricF0)
                        : strokeAlbedo;
                for (var layerIndex = 0;
                     !vectorLightingApplied && layerIndex < localLightLayers.Length;
                     layerIndex++)
                {
                    var layer = localLightLayers[layerIndex];
                    localLightLayers[layerIndex] = layer with
                    {
                        SolidStrokeStops = layer.LinearStops
                            .Select(stop => new GradientStop(
                                stop.Position,
                                Color.FromArgb(
                                    Reference3DLinearVectorHasVisibleEnergy(
                                        stop.DiffuseIrradiance + stop.SpecularRadiance + stop.FresnelRadiance)
                                        ? EvaluateReference3DLinearLightingArgb(
                                            strokeAlbedo,
                                            response.AmbientIrradiance + stop.DiffuseIrradiance,
                                            stop.SpecularRadiance,
                                            metallic,
                                            stop.FresnelRadiance,
                                            dielectricF0)
                                        : solidStrokeOpticalBaseArgb.Value)))
                            .ToArray()
                    };
                }
            }
            var opticalItem = item with
            {
                MaterialOpacity = opticalMaterialOpacity,
                OpticalResponse = vectorLightingArgb is int
                    || vectorLightingApplied
                        ? Reference3DOpticalResponse.Identity
                        : response,
                LocalLightLayers = localLightLayers,
                ShadowLayers = vectorLightingApplied
                    ? Array.Empty<Reference3DShadowLayer>()
                    : item.ShadowLayers,
                VectorLightingArgb = vectorLightingArgb,
                OpticalGradientStops = opticalGradientStops,
                SolidStrokeOpticalBaseArgb = solidStrokeOpticalBaseArgb,
                OpticalStrokeGradientStops = opticalStrokeGradientStops
            };
            result[index] = opticalItem;
            // Thin strokes lose coverage when they are rasterized into the quarter-resolution
            // interaction surface. Keep their base stroke and optical finish vector-based;
            // filled physical surfaces still use the bounded raster path.
            receivesLinearOptics[index] = item.Kind is not (
                    Reference3DRenderKind.FrontStroke
                    or Reference3DRenderKind.IntersectionEdge)
                && !vectorLightingApplied;
            if (!receivesLinearOptics[index]) continue;
            hasSurfaceBounds[index] = TryGetReference3DOpticalSurfaceBounds(
                opticalItem,
                out surfaceBounds[index]);
        }

        // Vector-lit plans have no raster surface to build. Returning here is
        // important for dense fracture scenes: the scratch raster layouts and
        // per-item optical surfaces would otherwise be allocated even though
        // every receiver was already resolved to a solid vector color.
        var requiresRasterSurfaces = false;
        for (var index = 0; index < receivesLinearOptics.Length; index++)
        {
            if (!receivesLinearOptics[index]) continue;
            requiresRasterSurfaces = true;
            break;
        }
        if (!requiresRasterSurfaces) return result;

        for (var lod = Reference3DOpticalRasterStartLod;
             lod <= MaximumReference3DOpticalRasterLod;
             lod++)
        {
            if (!TryCreateReference3DOpticalRasterLayouts(
                    surfaceBounds,
                    hasSurfaceBounds,
                    lod,
                    out var layouts))
            {
                continue;
            }

            var surfaces = new Reference3DOpticalSurface?[items.Length];
            var buildSucceeded = true;
            for (var index = 0; index < items.Length; index++)
            {
                if (!hasSurfaceBounds[index]) continue;
                surfaces[index] = BuildReference3DOpticalSurface(
                    result[index],
                    materials[index],
                    layouts[index]);
                if (surfaces[index] is not null) continue;
                buildSucceeded = false;
                break;
            }

            if (buildSucceeded)
            {
                for (var index = 0; index < result.Length; index++)
                {
                    if (surfaces[index] is { } surface)
                    {
                        result[index] = result[index] with { OpticalSurface = surface };
                    }
                    else if (result[index].Kind != Reference3DRenderKind.FrontStroke)
                    {
                        continue;
                    }
                    LastReference3DLocalLightLayerCount +=
                        result[index].LocalLightLayers?.Length ?? 0;
                }
                DisableReference3DOpticsWithoutSurface(
                    result,
                    receivesLinearOptics,
                    hasSurfaceBounds,
                    sourceMaterialOpacities);
                LastReference3DOpticalRasterLod = lod;
                return result;
            }

            foreach (var surface in surfaces) surface?.Dispose();
        }

        DisableReference3DOpticsWithoutSurface(
            result,
            receivesLinearOptics,
            hasSurface: null,
            sourceMaterialOpacities);
        return result;

        GradientStop[] GetSourceGradientStops(int objectIndex)
        {
            gradientStopsCache ??= new Dictionary<int, GradientStop[]>(items.Length);
            if (!gradientStopsCache.TryGetValue(objectIndex, out var stops))
            {
                Scene.TryGetGradientStopsForRendering(objectIndex, out stops!);
                gradientStopsCache.Add(objectIndex, stops);
            }
            return stops;
        }
    }

    private bool TryApplyReference3DVectorOpticsParallel(
        Reference3DRenderItem[] items,
        IReadOnlyList<SceneLightDefinition> lights,
        Vector3 ambientIrradiance,
        bool hasEffectiveLighting,
        out Reference3DRenderItem[] result)
    {
        result = [];
        if (ReferenceDimension != SceneDimension.ThreeD
            || items.Length < Reference3DVectorLightingMinimumItems)
        {
            return false;
        }

        var workers = ParallelBatch.WorkerCount(items.Length, 16);
        if (workers <= 1
            || !TryGetReference3DRay(
                new PointF(ClientSize.Width * 0.5f, ClientSize.Height * 0.5f),
                out var centerRay))
        {
            return false;
        }

        var materialsByObject = new SpatialOpticalMaterial[Scene.ObjectCount];
        var materialInitialized = new bool[Scene.ObjectCount];
        var gradientStopsByObject = new GradientStop[Scene.ObjectCount][];
        for (var index = 0; index < items.Length; index++)
        {
            var item = items[index];
            if (!Reference3DOpticsAffects(item)) continue;
            if ((uint)item.ObjectIndex >= Scene.ObjectCount
                || !ShouldUseReference3DVectorLighting(item, items.Length)
                || !TryGetReference3DSurfaceSample(
                    item,
                    hasCenterRay: true,
                    centerRay,
                    out _,
                    out _))
            {
                return false;
            }

            if (!materialInitialized[item.ObjectIndex])
            {
                materialsByObject[item.ObjectIndex] =
                    GetReference3DOpticalMaterial(item.ObjectIndex);
                materialInitialized[item.ObjectIndex] = true;
            }
            if (Scene.HasGradient(item.ObjectIndex)
                && gradientStopsByObject[item.ObjectIndex] is null)
            {
                gradientStopsByObject[item.ObjectIndex] =
                    Scene.TryGetGradientStopsForRendering(
                        item.ObjectIndex,
                        out var sourceStops)
                        ? sourceStops
                        : [];
            }
        }

        var parallelResult = new Reference3DRenderItem[items.Length];
        var lightEvaluationCounts = new int[items.Length];
        var failed = 0;
        ParallelBatch.For(
            items.Length,
            16,
            (_, start, end) =>
            {
                for (var index = start; index < end; index++)
                {
                    var item = items[index];
                    if (!Reference3DOpticsAffects(item))
                    {
                        parallelResult[index] = item;
                        continue;
                    }

                    var objectIndex = item.ObjectIndex;
                    var material = materialsByObject[objectIndex];
                    var opticalSurfaceContours = item.OcclusionContours;

                    if (!TryGetReference3DSurfaceSample(
                            item,
                            hasCenterRay: true,
                            centerRay,
                            out var scenePoint,
                            out var viewDirection)
                        || TryEvaluateReference3DVectorLighting(
                            item,
                            OrientReference3DFiniteLightReceiverPlane(item, viewDirection),
                            scenePoint,
                            viewDirection,
                            material,
                            ambientIrradiance,
                            lights,
                            out var lightEvaluations) is not { } vectorLighting)
                    {
                        Interlocked.Exchange(ref failed, 1);
                        continue;
                    }

                    lightEvaluationCounts[index] = lightEvaluations;
                    if (!hasEffectiveLighting)
                    {
                        vectorLighting = new Reference3DVectorLighting(
                            Vector3.One,
                            Vector3.Zero);
                    }

                    var normalizedMaterial = material.Normalize();
                    var dielectricF0 = Reference3DDielectricF0(normalizedMaterial);
                    var response = EvaluateReference3DOpticalResponse(
                        material,
                        ambientIrradiance);
                    var vectorLightingApplied = false;
                    int? vectorLightingArgb = null;
                    GradientStop[]? opticalGradientStops = null;
                    GradientStop[]? opticalStrokeGradientStops = null;
                    var sourceGradientStops = (uint)objectIndex < gradientStopsByObject.Length
                        ? gradientStopsByObject[objectIndex]
                        : null;
                    if (item.Kind == Reference3DRenderKind.FrontFill
                        && sourceGradientStops is { Length: > 0 })
                    {
                        var litStops = BuildReference3DOpticalGradientStops(
                            sourceGradientStops,
                            vectorLighting,
                            normalizedMaterial.Metallic,
                            dielectricF0);
                        if (litStops.Length > 0)
                        {
                            opticalGradientStops = litStops;
                            vectorLightingApplied = true;
                        }
                    }
                    else if (item.Kind == Reference3DRenderKind.FrontStroke
                             && sourceGradientStops is { Length: > 0 })
                    {
                        var litStops = BuildReference3DOpticalGradientStops(
                            sourceGradientStops,
                            vectorLighting,
                            normalizedMaterial.Metallic,
                            dielectricF0);
                        if (litStops.Length > 0)
                        {
                            opticalStrokeGradientStops = litStops;
                            vectorLightingApplied = true;
                        }
                    }
                    else
                    {
                        var baseArgb = item.Kind == Reference3DRenderKind.FrontStroke
                            && TryGetReference3DSolidStrokeAlbedo(
                                objectIndex,
                                out var solidStrokeAlbedo)
                            ? solidStrokeAlbedo
                            : item.Kind == Reference3DRenderKind.FrontFill
                                && (uint)objectIndex < Scene.Argb.Length
                                ? Scene.Argb[objectIndex]
                                : GetReference3DExtrusionSurfaceColor(item).ToArgb();
                        vectorLightingArgb = EvaluateReference3DLinearLightingArgb(
                            baseArgb,
                            vectorLighting.DiffuseIrradiance,
                            vectorLighting.SpecularRadiance,
                            normalizedMaterial.Metallic,
                            vectorLighting.FresnelRadiance,
                            dielectricF0);
                        vectorLightingApplied = true;
                    }
                    if (!vectorLightingApplied)
                    {
                        Interlocked.Exchange(ref failed, 1);
                        continue;
                    }

                    var opticalMaterialOpacity = Math.Clamp(
                        item.MaterialOpacity * response.OpacityScale,
                        0f,
                        1f);
                    int? solidStrokeOpticalBaseArgb = null;
                    if (item.Kind == Reference3DRenderKind.FrontStroke
                        && TryGetReference3DSolidStrokeAlbedo(
                            objectIndex,
                            out var strokeAlbedo))
                    {
                        var strokeColor = Color.FromArgb(strokeAlbedo);
                        opticalMaterialOpacity *= strokeColor.A / 255f;
                        strokeAlbedo = Color.FromArgb(
                            255,
                            strokeColor.R,
                            strokeColor.G,
                            strokeColor.B).ToArgb();
                        solidStrokeOpticalBaseArgb =
                            EvaluateReference3DLinearLightingArgb(
                                strokeAlbedo,
                                vectorLighting.DiffuseIrradiance,
                                vectorLighting.SpecularRadiance,
                                normalizedMaterial.Metallic,
                                vectorLighting.FresnelRadiance,
                                dielectricF0);
                    }

                    parallelResult[index] = item with
                    {
                        MaterialOpacity = opticalMaterialOpacity,
                        OpticalResponse = Reference3DOpticalResponse.Identity,
                        LocalLightLayers = Array.Empty<Reference3DLocalLightLayer>(),
                        ShadowLayers = Array.Empty<Reference3DShadowLayer>(),
                        VectorLightingArgb = vectorLightingArgb,
                        OpticalGradientStops = opticalGradientStops,
                        SolidStrokeOpticalBaseArgb = solidStrokeOpticalBaseArgb,
                        OpticalStrokeGradientStops = opticalStrokeGradientStops,
                        OpticalSurfaceContours = item.Kind == Reference3DRenderKind.FrontStroke
                            ? opticalSurfaceContours
                            : item.OpticalSurfaceContours
                    };
                }
            });

        if (Volatile.Read(ref failed) != 0)
        {
            result = [];
            return false;
        }

        result = parallelResult;
        for (var index = 0; index < lightEvaluationCounts.Length; index++)
        {
            LastReference3DOpticalLightEvaluations += lightEvaluationCounts[index];
        }
        return true;
    }

    internal static bool TryCreateReference3DOpticalRasterLayouts(
        IReadOnlyList<Rectangle> bounds,
        IReadOnlyList<bool> hasBounds,
        int lod,
        out Reference3DOpticalRasterLayout[] layouts)
    {
        layouts = new Reference3DOpticalRasterLayout[bounds.Count];
        var divisor = 1 << Math.Clamp(lod, 0, MaximumReference3DOpticalRasterLod);
        long totalBytes = 0;
        for (var index = 0; index < bounds.Count; index++)
        {
            if (!hasBounds[index]) continue;
            var current = bounds[index];
            var pixelWidth = Math.Max(1, (int)Math.Ceiling(current.Width / (double)divisor));
            var pixelHeight = Math.Max(1, (int)Math.Ceiling(current.Height / (double)divisor));
            var layout = new Reference3DOpticalRasterLayout(
                current,
                pixelWidth,
                pixelHeight);
            if (layout.PixelCount <= 0
                || layout.PixelCount > MaximumReference3DOpticalSurfacePixels)
            {
                return false;
            }
            totalBytes += layout.PixelCount * sizeof(int) * 2L;
            if (totalBytes > MaximumReference3DOpticalSurfaceCacheBytes) return false;
            layouts[index] = layout;
        }
        return true;
    }

    internal static void DisableReference3DOpticsWithoutSurface(
        Reference3DRenderItem[] items,
        IReadOnlyList<bool> receivesLinearOptics,
        IReadOnlyList<bool>? hasSurface,
        IReadOnlyList<float> sourceMaterialOpacities)
    {
        for (var index = 0; index < items.Length; index++)
        {
            if (!receivesLinearOptics[index]
                || hasSurface is not null && hasSurface[index])
            {
                continue;
            }
            items[index] = items[index] with
            {
                MaterialOpacity = sourceMaterialOpacities[index],
                OpticalResponse = Reference3DOpticalResponse.Identity,
                LocalLightLayers = [],
                ShadowLayers = [],
                OpticalSurface = null
            };
        }
    }

    private SceneLightDefinition[] EvaluateReference3DSceneLights(SceneDefinition definition)
    {
        var frame = Math.Max(0, Frame);
        var lights = new List<SceneLightDefinition>(definition.Lights.Count);
        foreach (var light in definition.Lights)
        {
            if (!definition.TryEvaluateLightSettings(light.Id, frame, out var settings)) continue;
            lights.Add(new SceneLightDefinition(light.Id, light.Name, light.Kind, settings));
        }
        return lights.ToArray();
    }

    private static bool Reference3DOpticsAffects(Reference3DRenderItem item)
    {
        return item.Plane.IsValid
            && item.Kind is Reference3DRenderKind.Back
                or Reference3DRenderKind.Side
                or Reference3DRenderKind.FrontFill
                or Reference3DRenderKind.FrontStroke
                or Reference3DRenderKind.IntersectionEdge;
    }

    private bool ShouldUseReference3DVectorLighting(
        Reference3DRenderItem item,
        int renderItemCount,
        bool allowBelowMinimum = false)
    {
        if (ReferenceDimension != SceneDimension.ThreeD
            || !allowBelowMinimum
            && renderItemCount < Reference3DVectorLightingMinimumItems
            || item.Kind is not (Reference3DRenderKind.Back
                or Reference3DRenderKind.Side
                or Reference3DRenderKind.FrontFill
                or Reference3DRenderKind.FrontStroke))
        {
            return false;
        }

        if ((uint)item.ObjectIndex >= Scene.ObjectCount) return false;
        var shape = Scene.ShapeKind[item.ObjectIndex];
        if (shape == ShapeKind.Path)
        {
            var pointCount = 0;
            foreach (var contour in item.Contours)
            {
                if (item.Kind == Reference3DRenderKind.FrontStroke)
                {
                    if (contour.Points.Length < 2) return false;
                }
                else if (!contour.Closed || contour.Points.Length < 3)
                {
                    return false;
                }
                pointCount += contour.Points.Length;
                if (pointCount > MaximumReference3DVectorLightingPathPoints) return false;
            }
            if (pointCount < (item.Kind == Reference3DRenderKind.FrontStroke ? 2 : 3))
            {
                return false;
            }
        }
        else if (shape is not (ShapeKind.Rectangle
                     or ShapeKind.Ellipse
                     or ShapeKind.Triangle
                     or ShapeKind.Polygon
                     or ShapeKind.Star
                     or ShapeKind.Line))
        {
            return false;
        }

        if (Scene.HasGradient(item.ObjectIndex))
        {
            return item.Kind is Reference3DRenderKind.FrontFill
                or Reference3DRenderKind.FrontStroke;
        }

        return item.Kind != Reference3DRenderKind.FrontStroke
            || TryGetReference3DSolidStrokeAlbedo(item.ObjectIndex, out _);
    }

    private Reference3DVectorLighting? TryEvaluateReference3DVectorLighting(
        Reference3DRenderItem item,
        Reference3DSurfacePlane receiverPlane,
        Vector3 scenePoint,
        Vector3 viewDirection,
        SpatialOpticalMaterial material,
        Vector3 ambientIrradiance,
        IReadOnlyList<SceneLightDefinition> lights,
        out int lightEvaluations)
    {
        lightEvaluations = 0;
        if (!receiverPlane.IsValid
            || !Finite(receiverPlane.Normal)
            || !Finite(scenePoint)
            || !Finite(viewDirection)
            || receiverPlane.Normal.LengthSquared() <= 0.000001f)
        {
            return null;
        }

        var normalizedMaterial = material.Normalize();
        var evaluationCount = 0;
        var diffuse = Reference3DFiniteOrZero(ambientIrradiance);
        var specular = Vector3.Zero;
        var fresnel = Vector3.Zero;
        var normal = Vector3.Normalize(receiverPlane.Normal);
        Span<Vector3> areaSamples = stackalloc Vector3[Reference3DAreaLightSamples];
        foreach (var light in lights)
        {
            var settings = light.Settings;
            if (light.Kind == SceneLightKind.Ambient
                || !settings.Enabled
                || settings.Intensity <= 0f
                || !settings.IsValid(light.Kind))
            {
                continue;
            }

            if (light.Kind == SceneLightKind.Directional)
            {
                var towardLight = Reference3DDirectionalLightVector(
                    settings.RotationDegrees);
                AddSample(towardLight, 1f, 1f, settings);
                continue;
            }

            if (light.Kind is not (SceneLightKind.Point or SceneLightKind.Area)) continue;
            var pointLikeArea = light.Kind == SceneLightKind.Area
                && Math.Max(settings.AreaSize.X, settings.AreaSize.Y)
                    <= Reference3DAreaLightPointSize;
            var sampleCount = light.Kind == SceneLightKind.Area && !pointLikeArea
                ? Reference3DAreaLightSamples
                : 1;
            if (sampleCount > 1) GetReference3DAreaLightSamples(settings, areaSamples);
            else areaSamples[0] = settings.Position;
            var sampleWeight = 1f / sampleCount;
            for (var sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
            {
                var offset = areaSamples[sampleIndex] - scenePoint;
                var distanceSquared = offset.LengthSquared();
                if (!Finite(offset)
                    || !float.IsFinite(distanceSquared)
                    || distanceSquared <= 0.000001f)
                {
                    continue;
                }

                var distance = MathF.Sqrt(distanceSquared);
                if (!float.IsFinite(distance) || distance >= settings.Range) continue;
                var towardPoint = offset / distance;
                var unitDistance = Math.Clamp(distance / settings.Range, 0f, 1f);
                var unitDistanceSquared = unitDistance * unitDistance;
                var edge = 1f - unitDistanceSquared;
                var attenuation = edge * edge / (1f + 2f * unitDistanceSquared);
                AddSample(towardPoint, attenuation, sampleWeight, settings);
            }
        }

        lightEvaluations = evaluationCount;
        return new Reference3DVectorLighting(diffuse, specular, fresnel);

        void AddSample(
            Vector3 towardLight,
            float attenuation,
            float sampleWeight,
            SceneLightSettings settings)
        {
            if (!Finite(towardLight)
                || towardLight.LengthSquared() <= 0.000001f
                || !float.IsFinite(attenuation)
                || attenuation <= 0f)
            {
                return;
            }

            towardLight = Vector3.Normalize(towardLight);
            if (!TryGetReference3DHalfDots(
                    normal,
                    towardLight,
                    viewDirection,
                    out var nDotL,
                    out var sampleNDotV,
                    out var nDotH,
                    out var vDotH)
                || nDotL <= 0f)
            {
                return;
            }

            var sample = EvaluateReference3DLinearLightSample(
                settings,
                Reference3DLinearColor(settings.ColorArgb),
                normalizedMaterial,
                attenuation,
                nDotL,
                sampleNDotV,
                nDotH,
                vDotH,
                sampleWeight,
                visibility: 1f);
            diffuse += sample.DiffuseIrradiance;
            specular += sample.SpecularRadiance;
            fresnel += sample.FresnelRadiance;
            evaluationCount++;
        }
    }

    private SpatialOpticalMaterial GetReference3DOpticalMaterial(int objectIndex)
    {
        return ReferenceEquals(_sceneCompositionResultScene, Scene)
            && _sceneCompositionResult?.TryGetMaterial(objectIndex, out var material) == true
                ? material.Normalize()
                : SpatialOpticalMaterial.Default;
    }

    private bool TryGetReference3DSolidStrokeAlbedo(int objectIndex, out int argb)
    {
        argb = 0;
        if ((uint)objectIndex >= Scene.ObjectCount
            || Scene.ShapeKind[objectIndex] == ShapeKind.Line && Scene.HasGradient(objectIndex))
        {
            return false;
        }
        argb = (uint)objectIndex < Scene.StrokeArgb.Length
            ? Scene.StrokeArgb[objectIndex]
            : Color.FromArgb(238, 242, 241).ToArgb();
        return Color.FromArgb(argb).A > 0;
    }

    private static GradientStop[] BuildReference3DOpticalStrokeGradientStops(
        IReadOnlyList<GradientStop> sourceStops,
        Vector3 ambientIrradiance,
        float metallic,
        float dielectricF0)
    {
        if (sourceStops.Count == 0) return [];
        if (!Reference3DLinearVectorHasVisibleEnergy(ambientIrradiance))
        {
            return sourceStops.ToArray();
        }

        return sourceStops
            .Select(stop => new GradientStop(
                stop.Position,
                EvaluateReference3DLinearLightingArgb(
                    stop.Argb,
                    ambientIrradiance,
                    Vector3.Zero,
                    metallic,
                    dielectricF0: dielectricF0)))
            .ToArray();
    }

    private static GradientStop[] BuildReference3DOpticalGradientStops(
        IReadOnlyList<GradientStop> sourceStops,
        Reference3DVectorLighting lighting,
        float metallic,
        float dielectricF0)
    {
        if (sourceStops.Count == 0) return [];
        return sourceStops
            .Select(stop => new GradientStop(
                stop.Position,
                EvaluateReference3DLinearLightingArgb(
                    stop.Argb,
                    lighting.DiffuseIrradiance,
                    lighting.SpecularRadiance,
                    metallic,
                    lighting.FresnelRadiance,
                    dielectricF0)))
            .ToArray();
    }

    private bool TryGetReference3DSurfaceSample(
        Reference3DRenderItem item,
        bool hasCenterRay,
        SpatialRay centerRay,
        out Vector3 scenePoint,
        out Vector3 viewDirection)
    {
        scenePoint = default;
        viewDirection = Vector3.UnitZ;
        if (!item.Plane.IsValid || !Finite(item.Plane.Normal)) return false;
        scenePoint = item.HasSurfacePoint
            ? item.SurfacePoint
            : item.Plane.Normal * item.Plane.Distance;
        if (!Finite(scenePoint) || !hasCenterRay) return false;
        var perspectiveViewDirection = centerRay.Origin - scenePoint;
        var orthographicViewDirection = -centerRay.Direction;
        if (!Finite(perspectiveViewDirection)
            || perspectiveViewDirection.LengthSquared() <= 0.000001f
            || !Finite(orthographicViewDirection)
            || orthographicViewDirection.LengthSquared() <= 0.000001f)
        {
            return false;
        }
        perspectiveViewDirection = Vector3.Normalize(perspectiveViewDirection);
        orthographicViewDirection = Vector3.Normalize(orthographicViewDirection);
        viewDirection = Vector3.Normalize(Vector3.Lerp(
            orthographicViewDirection,
            perspectiveViewDirection,
            Math.Clamp(ReferenceProjectionBlend, 0f, 1f)));
        return Finite(viewDirection);
    }

    private Vector3 EvaluateReference3DAmbientIrradiance(
        IReadOnlyList<SceneLightDefinition> lights)
    {
        var ambient = Vector3.Zero;
        foreach (var light in lights)
        {
            var settings = light.Settings;
            if (light.Kind == SceneLightKind.Ambient
                && settings.Enabled
                && settings.Intensity > 0f
                && settings.IsValid(light.Kind))
            {
                ambient += Reference3DLinearColor(settings.ColorArgb) * settings.Intensity;
                LastReference3DOpticalLightEvaluations++;
            }
        }
        return Reference3DFiniteOrZero(ambient);
    }

    private static Reference3DOpticalResponse EvaluateReference3DOpticalResponse(
        SpatialOpticalMaterial material,
        Vector3 ambient)
    {
        var normalized = material.Normalize();

        var diffuseScale = (1f - normalized.Metallic) * (1f - normalized.Transmission);
        var illumination = ambient * diffuseScale;
        var shadeArgb = Reference3DShadeOverlay(illumination);
        return new Reference3DOpticalResponse(
            shadeArgb,
            Color.Transparent.ToArgb(),
            1f - normalized.Transmission * 0.85f,
            illumination);
    }

    private Reference3DLocalLightLayer[] BuildReference3DLocalLightLayers(
        Reference3DRenderItem receiver,
        Reference3DSurfacePlane receiverPlane,
        Reference3DOpticsEvaluationContext evaluationContext,
        Vector3 viewDirection,
        SpatialOpticalMaterial material,
        IReadOnlyList<SceneLightDefinition> lights)
    {
        if (!receiver.Plane.IsValid
            || !TryBuildReference3DVisibleSurfacePaths(receiver, out var receiverPaths))
        {
            return [];
        }

        var layers = new List<Reference3DLocalLightLayer>();
        Span<Vector3> areaSamples = stackalloc Vector3[Reference3DAreaLightSamples];
        var normalizedMaterial = material.Normalize();
        foreach (var light in lights)
        {
            if (layers.Count >= MaximumReference3DLocalLightLayersPerReceiver) break;
            var settings = light.Settings;
            if (light.Kind == SceneLightKind.Ambient
                || !settings.Enabled
                || settings.Intensity <= 0f
                || !settings.IsValid(light.Kind))
            {
                continue;
            }

            var lightLayers = new List<Reference3DLocalLightLayer>();
            if (light.Kind == SceneLightKind.Directional)
            {
                BuildReference3DDirectionalLightLayers(
                    receiver,
                    receiverPlane,
                    evaluationContext,
                    material,
                    receiverPaths,
                    viewDirection,
                    settings,
                    normalizedMaterial,
                    lightLayers);
                layers.AddRange(lightLayers);
                continue;
            }
            if (light.Kind is not (SceneLightKind.Point or SceneLightKind.Area)) continue;

            var pointLikeArea = light.Kind == SceneLightKind.Area
                && Math.Max(settings.AreaSize.X, settings.AreaSize.Y)
                    <= Reference3DAreaLightPointSize;
            var sampleCount = light.Kind == SceneLightKind.Area && !pointLikeArea
                ? Reference3DAreaLightSamples
                : 1;
            if (sampleCount > 1) GetReference3DAreaLightSamples(settings, areaSamples);
            else areaSamples[0] = settings.Position;
            var sampleWeight = 1f / sampleCount;
            var lightColor = Reference3DLinearColor(settings.ColorArgb);
            for (var sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
            {
                var sample = areaSamples[sampleIndex];
                var normalDistance = Vector3.Dot(receiverPlane.Normal, sample)
                    - receiverPlane.Distance;
                if (!float.IsFinite(normalDistance)
                    || normalDistance <= 0.0001f
                    || normalDistance >= settings.Range)
                {
                    continue;
                }
                var footprintRadiusSquared = settings.Range * settings.Range
                    - normalDistance * normalDistance;
                if (!float.IsFinite(footprintRadiusSquared) || footprintRadiusSquared <= 0f) continue;
                var footprintRadius = MathF.Sqrt(footprintRadiusSquared);
                var center = sample - receiverPlane.Normal * normalDistance;
                if (!TryGetReference3DPlaneBasis(receiverPlane.Normal, out var axisX, out var axisY))
                    continue;
                BuildReference3DLocalLightOcclusion(
                    receiver,
                    receiverPlane,
                    evaluationContext,
                    material,
                    settings,
                    sample,
                    normalDistance,
                    receiverPaths,
                    out var occlusionPaths,
                    out var occludedVisibility);
                if (!TryBuildReference3DLocalLightContours(
                        center,
                        axisX,
                        axisY,
                        footprintRadius,
                        receiverPaths,
                        occlusionPaths,
                        occludedVisibility,
                        out var contours,
                        out var occludedContours,
                        out var gradientTransform))
                {
                    continue;
                }

                BuildReference3DLocalLightGradientStops(
                    settings,
                    normalDistance,
                    footprintRadius,
                    sampleWeight,
                    lightColor,
                    normalizedMaterial,
                    receiverPlane.Normal,
                    viewDirection,
                    visibility: 1f,
                    out var diffuseStops,
                    out var specularStops,
                    out var linearStops);
                if (contours.Length > 0 && Reference3DLinearGradientHasVisibleEnergy(linearStops))
                {
                    lightLayers.Add(new Reference3DLocalLightLayer(
                        contours,
                        gradientTransform,
                        diffuseStops,
                        specularStops,
                        linearStops));
                }
                if (occludedContours.Length > 0
                    && occludedVisibility > 0.0001f)
                {
                    BuildReference3DLocalLightGradientStops(
                        settings,
                        normalDistance,
                        footprintRadius,
                        sampleWeight,
                        lightColor,
                        normalizedMaterial,
                        receiverPlane.Normal,
                        viewDirection,
                        occludedVisibility,
                        out var occludedDiffuseStops,
                        out var occludedSpecularStops,
                        out var occludedLinearStops);
                    if (Reference3DLinearGradientHasVisibleEnergy(occludedLinearStops))
                    {
                        lightLayers.Add(new Reference3DLocalLightLayer(
                            occludedContours,
                            gradientTransform,
                            occludedDiffuseStops,
                            occludedSpecularStops,
                            occludedLinearStops));
                    }
                }
                LastReference3DOpticalLightEvaluations += Reference3DLocalLightGradientStops;
            }
            layers.AddRange(lightLayers);
        }
        return layers.ToArray();
    }

    private void BuildReference3DDirectionalLightLayers(
        Reference3DRenderItem receiver,
        Reference3DSurfacePlane receiverPlane,
        Reference3DOpticsEvaluationContext evaluationContext,
        SpatialOpticalMaterial receiverMaterial,
        PathsD receiverPaths,
        Vector3 viewDirection,
        SceneLightSettings settings,
        SpatialOpticalMaterial material,
        List<Reference3DLocalLightLayer> layers)
    {
        var towardLight = Reference3DDirectionalLightVector(settings.RotationDegrees);
        LastReference3DOpticalLightEvaluations++;
        if (!TryGetReference3DHalfDots(
                receiverPlane.Normal,
                towardLight,
                viewDirection,
                out var nDotL,
                out var nDotV,
                out var nDotH,
                out var vDotH)
            || nDotL <= 0f)
        {
            return;
        }
        var lightColor = Reference3DLinearColor(settings.ColorArgb);
        BuildReference3DDirectionalLightOcclusion(
            receiver,
            receiverPlane,
            evaluationContext,
            receiverMaterial,
            settings,
            towardLight,
            receiverPaths,
            out var occlusionPaths,
            out var occludedVisibility);
        if (!Reference3DPathsHaveArea(occlusionPaths))
        {
            AddLayer(receiverPaths, visibility: 1f);
        }
        else
        {
            try
            {
                var visiblePaths = Clipper.Difference(
                    receiverPaths,
                    occlusionPaths,
                    FillRule.EvenOdd,
                    Reference3DClipperPrecision);
                AddLayer(visiblePaths, visibility: 1f);
                if (occludedVisibility > 0.0001f) AddLayer(occlusionPaths, occludedVisibility);
            }
            catch (Exception exception) when (Reference3DIsClipperFailure(exception))
            {
                AddLayer(receiverPaths, visibility: 1f);
            }
        }
        void AddLayer(PathsD paths, float visibility)
        {
            if (!Reference3DPathsHaveArea(paths))
            {
                return;
            }
            var diffuse = EvaluateReference3DDirectLightSample(
                settings,
                lightColor,
                material,
                attenuation: 1f,
                nDotL,
                nDotV,
                nDotH,
                vDotH,
                sampleWeight: 1f,
                visibility,
                specular: false);
            var specular = EvaluateReference3DDirectLightSample(
                settings,
                lightColor,
                material,
                attenuation: 1f,
                nDotL,
                nDotV,
                nDotH,
                vDotH,
                sampleWeight: 1f,
                visibility,
                specular: true);
            var linearStops = new[]
            {
                EvaluateReference3DLinearLightSample(
                    settings,
                    lightColor,
                    material,
                    attenuation: 1f,
                    nDotL,
                    nDotV,
                    nDotH,
                    vDotH,
                    sampleWeight: 1f,
                    visibility),
                EvaluateReference3DLinearLightSample(
                    settings,
                    lightColor,
                    material,
                    attenuation: 1f,
                    nDotL,
                    nDotV,
                    nDotH,
                    vDotH,
                    sampleWeight: 1f,
                    visibility) with { Position = 1f }
            };
            if (!Reference3DLinearGradientHasVisibleEnergy(linearStops)) return;
            layers.Add(new Reference3DLocalLightLayer(
                Reference3DPathsToContours(paths),
                Reference3DDirectionalLightGradientTransform(paths),
                [new GradientStop(0f, diffuse), new GradientStop(1f, diffuse)],
                [new GradientStop(0f, specular), new GradientStop(1f, specular)],
                linearStops));
        }
    }

    private static Matrix3x2 Reference3DDirectionalLightGradientTransform(PathsD paths)
    {
        var hasPoint = false;
        var left = double.PositiveInfinity;
        var top = double.PositiveInfinity;
        var right = double.NegativeInfinity;
        var bottom = double.NegativeInfinity;
        foreach (var path in paths)
        {
            foreach (var point in path)
            {
                hasPoint = true;
                left = Math.Min(left, point.x);
                top = Math.Min(top, point.y);
                right = Math.Max(right, point.x);
                bottom = Math.Max(bottom, point.y);
            }
        }
        if (!hasPoint) return Matrix3x2.Identity;
        var centerX = (float)((left + right) * 0.5d);
        var centerY = (float)((top + bottom) * 0.5d);
        var radius = (float)Math.Max(1d, Math.Max(right - left, bottom - top) * 0.75d);
        return new Matrix3x2(radius, 0f, 0f, radius, centerX, centerY);
    }

    private Reference3DSurfacePlane OrientReference3DFiniteLightReceiverPlane(
        Reference3DRenderItem receiver,
        Vector3 viewDirection)
    {
        var plane = receiver.Plane;
        var extrusion = GetReference3DExtrusionVector(receiver.ObjectIndex);
        var twoSided = receiver.Kind is Reference3DRenderKind.FrontFill
                or Reference3DRenderKind.FrontStroke
                or Reference3DRenderKind.IntersectionEdge
            && Finite(extrusion)
            && extrusion.LengthSquared()
                <= ReferenceExtrusionEpsilon * ReferenceExtrusionEpsilon;
        if (!twoSided
            || !Finite(viewDirection)
            || Vector3.Dot(plane.Normal, viewDirection) >= 0f)
        {
            return plane;
        }

        return new Reference3DSurfacePlane(-plane.Normal, -plane.Distance, plane.IsValid);
    }

    private bool TryBuildReference3DLocalLightContours(
        Vector3 center,
        Vector3 axisX,
        Vector3 axisY,
        float outerRadius,
        PathsD receiverPaths,
        PathsD occlusionPaths,
        float occludedVisibility,
        out Reference3DProjectedContour[] contours,
        out Reference3DProjectedContour[] occludedContours,
        out Matrix3x2 gradientTransform)
    {
        contours = [];
        occludedContours = [];
        gradientTransform = Matrix3x2.Identity;
        if (!float.IsFinite(outerRadius) || outerRadius <= 0.0001f) return false;
        if (!TryProjectCircle(out var outer, out var projectedCircle)
            || !TryResolveGradientTransform(projectedCircle, out gradientTransform)) return false;

        try
        {
            var footprint = new PathsD { outer };
            var clipped = Clipper.Intersect(
                footprint,
                receiverPaths,
                FillRule.EvenOdd,
                Reference3DClipperPrecision);
            if (!Reference3DPathsHaveArea(clipped)) return false;
            if (!Reference3DPathsHaveArea(occlusionPaths))
            {
                contours = Reference3DPathsToContours(clipped);
                return contours.Length > 0;
            }

            var visible = Clipper.Difference(
                clipped,
                occlusionPaths,
                FillRule.EvenOdd,
                Reference3DClipperPrecision);
            if (Reference3DPathsHaveArea(visible))
            {
                contours = Reference3DPathsToContours(visible);
            }
            if (occludedVisibility > 0.0001f)
            {
                var occluded = Clipper.Intersect(
                    clipped,
                    occlusionPaths,
                    FillRule.EvenOdd,
                    Reference3DClipperPrecision);
                if (Reference3DPathsHaveArea(occluded))
                {
                    occludedContours = Reference3DPathsToContours(occluded);
                }
            }
            return contours.Length > 0 || occludedContours.Length > 0;
        }
        catch (Exception exception) when (Reference3DIsClipperFailure(exception))
        {
            return false;
        }

        bool TryProjectCircle(
            out PathD path,
            out List<Reference3DLocalLightProjectionVertex> projectedVertices)
        {
            var cameraVertices = new List<Reference3DLocalLightProjectionVertex>(
                Reference3DLocalLightCircleSegments);
            for (var segment = 0; segment < Reference3DLocalLightCircleSegments; segment++)
            {
                var angle = MathF.Tau * segment / Reference3DLocalLightCircleSegments;
                var point = center
                    + axisX * (MathF.Cos(angle) * outerRadius)
                    + axisY * (MathF.Sin(angle) * outerRadius);
                var transformed = CameraSpacePoint(new Point3(point.X, -point.Y, point.Z));
                var camera = new Vector3(transformed.X, transformed.Y, transformed.Z);
                if (!Finite(camera))
                {
                    path = [];
                    projectedVertices = [];
                    return false;
                }
                cameraVertices.Add(new Reference3DLocalLightProjectionVertex(
                    camera,
                    new Vector2(MathF.Cos(angle), MathF.Sin(angle)),
                    PointF.Empty));
            }

            // Keep the world-space light intact and clip only its projected footprint.
            var clipped = ClipNearPlane(cameraVertices);
            path = new PathD(clipped.Count);
            projectedVertices = new List<Reference3DLocalLightProjectionVertex>(clipped.Count);
            foreach (var vertex in clipped)
            {
                var screen = ProjectCameraVector(vertex.Camera);
                if (!float.IsFinite(screen.X) || !float.IsFinite(screen.Y))
                {
                    path = [];
                    projectedVertices = [];
                    return false;
                }
                if (projectedVertices.Count > 0
                    && ReferencePointDistance(projectedVertices[^1].Screen, screen) <= 0.0001f)
                {
                    continue;
                }
                projectedVertices.Add(vertex with { Screen = screen });
                path.Add(new PointD(screen.X, screen.Y));
            }
            if (projectedVertices.Count > 2
                && ReferencePointDistance(projectedVertices[0].Screen, projectedVertices[^1].Screen)
                    <= 0.0001f)
            {
                projectedVertices.RemoveAt(projectedVertices.Count - 1);
                path.RemoveAt(path.Count - 1);
            }
            return projectedVertices.Count >= 3 && Reference3DPathsHaveArea(new PathsD { path });
        }

        List<Reference3DLocalLightProjectionVertex> ClipNearPlane(
            IReadOnlyList<Reference3DLocalLightProjectionVertex> source)
        {
            var result = new List<Reference3DLocalLightProjectionVertex>(source.Count + 2);
            if (source.Count == 0) return result;
            var previous = source[^1];
            var previousInside = previous.Camera.Z >= ReferenceNearPlane;
            foreach (var current in source)
            {
                var currentInside = current.Camera.Z >= ReferenceNearPlane;
                if (currentInside != previousInside)
                {
                    var denominator = (double)current.Camera.Z - previous.Camera.Z;
                    if (!double.IsFinite(denominator) || denominator == 0d) return [];
                    var amount = (float)Math.Clamp(
                        (ReferenceNearPlane - (double)previous.Camera.Z) / denominator,
                        0d,
                        1d);
                    var camera = Vector3.Lerp(previous.Camera, current.Camera, amount);
                    camera.Z = ReferenceNearPlane;
                    result.Add(new Reference3DLocalLightProjectionVertex(
                        camera,
                        Vector2.Lerp(previous.Local, current.Local, amount),
                        PointF.Empty));
                }
                if (currentInside) result.Add(current);
                previous = current;
                previousInside = currentInside;
            }
            return result;
        }

        bool TryResolveGradientTransform(
            IReadOnlyList<Reference3DLocalLightProjectionVertex> projectedVertices,
            out Matrix3x2 transform)
        {
            transform = Matrix3x2.Identity;
            if (TryProjectScenePosition(center, out var projectedCenter, out _)
                && TryProjectScenePosition(center + axisX * outerRadius, out var projectedX, out _)
                && TryProjectScenePosition(center + axisY * outerRadius, out var projectedY, out _))
            {
                transform = new Matrix3x2(
                    projectedX.X - projectedCenter.X,
                    projectedX.Y - projectedCenter.Y,
                    projectedY.X - projectedCenter.X,
                    projectedY.Y - projectedCenter.Y,
                    projectedCenter.X,
                    projectedCenter.Y);
                return Reference3DGradientTransformIsStable(transform);
            }

            return TryFitReference3DLocalLightGradientTransform(projectedVertices, out transform);
        }
    }

    private static bool TryFitReference3DLocalLightGradientTransform(
        IReadOnlyList<Reference3DLocalLightProjectionVertex> vertices,
        out Matrix3x2 transform)
    {
        transform = Matrix3x2.Identity;
        if (vertices.Count < 3) return false;

        double uu = 0;
        double uv = 0;
        double vv = 0;
        double u = 0;
        double v = 0;
        double sx = 0;
        double sy = 0;
        double usx = 0;
        double vsx = 0;
        double usy = 0;
        double vsy = 0;
        foreach (var vertex in vertices)
        {
            var localX = vertex.Local.X;
            var localY = vertex.Local.Y;
            var screenX = vertex.Screen.X;
            var screenY = vertex.Screen.Y;
            if (!float.IsFinite(localX)
                || !float.IsFinite(localY)
                || !float.IsFinite(screenX)
                || !float.IsFinite(screenY))
            {
                return false;
            }
            uu += localX * localX;
            uv += localX * localY;
            vv += localY * localY;
            u += localX;
            v += localY;
            sx += screenX;
            sy += screenY;
            usx += localX * screenX;
            vsx += localY * screenX;
            usy += localX * screenY;
            vsy += localY * screenY;
        }

        if (!TrySolve(usx, vsx, sx, out var x0, out var x1, out var x2)
            || !TrySolve(usy, vsy, sy, out var y0, out var y1, out var y2))
        {
            return false;
        }
        transform = new Matrix3x2(
            (float)x0,
            (float)y0,
            (float)x1,
            (float)y1,
            (float)x2,
            (float)y2);
        return Reference3DGradientTransformIsStable(transform);

        bool TrySolve(
            double right0,
            double right1,
            double right2,
            out double result0,
            out double result1,
            out double result2)
        {
            result0 = 0;
            result1 = 0;
            result2 = 0;
            var determinant = Determinant(
                uu, uv, u,
                uv, vv, v,
                u, v, vertices.Count);
            var scale = Math.Max(
                1d,
                Math.Max(
                    Math.Max(Math.Abs(uu), Math.Abs(uv)),
                    Math.Max(
                        Math.Max(Math.Abs(vv), Math.Abs(u)),
                        Math.Max(Math.Abs(v), vertices.Count))));
            if (!double.IsFinite(determinant)
                || Math.Abs(determinant) <= scale * scale * scale * 0.000000000001d)
            {
                return false;
            }

            result0 = Determinant(
                right0, uv, u,
                right1, vv, v,
                right2, v, vertices.Count) / determinant;
            result1 = Determinant(
                uu, right0, u,
                uv, right1, v,
                u, right2, vertices.Count) / determinant;
            result2 = Determinant(
                uu, uv, right0,
                uv, vv, right1,
                u, v, right2) / determinant;
            return double.IsFinite(result0)
                && double.IsFinite(result1)
                && double.IsFinite(result2);
        }

        static double Determinant(
            double a00,
            double a01,
            double a02,
            double a10,
            double a11,
            double a12,
            double a20,
            double a21,
            double a22)
        {
            return a00 * (a11 * a22 - a12 * a21)
                - a01 * (a10 * a22 - a12 * a20)
                + a02 * (a10 * a21 - a11 * a20);
        }
    }

    private static bool Reference3DGradientTransformIsStable(Matrix3x2 transform)
    {
        if (!float.IsFinite(transform.M11)
            || !float.IsFinite(transform.M12)
            || !float.IsFinite(transform.M21)
            || !float.IsFinite(transform.M22)
            || !float.IsFinite(transform.M31)
            || !float.IsFinite(transform.M32))
        {
            return false;
        }
        var determinant = (double)transform.M11 * transform.M22
            - (double)transform.M12 * transform.M21;
        var scale = Math.Max(
            1d,
            Math.Max(
                (double)transform.M11 * transform.M11 + (double)transform.M12 * transform.M12,
                (double)transform.M21 * transform.M21 + (double)transform.M22 * transform.M22));
        return double.IsFinite(determinant) && Math.Abs(determinant) > scale * 0.00000001d;
    }

    private static void BuildReference3DLocalLightGradientStops(
        SceneLightSettings settings,
        float normalDistance,
        float footprintRadius,
        float sampleWeight,
        Vector3 lightColor,
        SpatialOpticalMaterial material,
        Vector3 normal,
        Vector3 viewDirection,
        float visibility,
        out GradientStop[] diffuseStops,
        out GradientStop[] specularStops,
        out Reference3DLinearLightStop[] linearStops)
    {
        diffuseStops = new GradientStop[Reference3DLocalLightGradientStops];
        specularStops = new GradientStop[Reference3DLocalLightGradientStops];
        linearStops = new Reference3DLinearLightStop[Reference3DLocalLightGradientStops];
        material = material.Normalize();
        var radialExponent = 1f + (1f - material.Roughness) * 2f;
        visibility = Math.Clamp(visibility, 0f, 1f);

        for (var index = 0; index < Reference3DLocalLightGradientStops; index++)
        {
            var sampleFraction = index / (float)(Reference3DLocalLightGradientStops - 1);
            var radialPosition = index == 0
                ? 0f
                : index == Reference3DLocalLightGradientStops - 1
                    ? 1f
                    : MathF.Pow(sampleFraction, radialExponent);
            var radialDistance = footprintRadius * radialPosition;
            var distance = MathF.Sqrt(
                normalDistance * normalDistance + radialDistance * radialDistance);
            var unitDistance = Math.Clamp(distance / settings.Range, 0f, 1f);
            var unitDistanceSquared = unitDistance * unitDistance;
            var edge = 1f - unitDistanceSquared;
            var attenuation = edge * edge / (1f + 2f * unitDistanceSquared);
            if (!TryGetReference3DRadialHalfDots(
                    normal,
                    viewDirection,
                    normalDistance,
                    radialDistance,
                    out var nDotL,
                    out var nDotV,
                    out var nDotH,
                    out var vDotH))
            {
                nDotL = nDotV = nDotH = vDotH = 0f;
            }
            diffuseStops[index] = new GradientStop(
                radialPosition,
                EvaluateReference3DDirectLightSample(
                    settings,
                    lightColor,
                    material,
                    attenuation,
                    nDotL,
                    nDotV,
                    nDotH,
                    vDotH,
                    sampleWeight,
                    visibility,
                    specular: false));
            specularStops[index] = new GradientStop(
                radialPosition,
                EvaluateReference3DDirectLightSample(
                    settings,
                    lightColor,
                    material,
                    attenuation,
                    nDotL,
                    nDotV,
                    nDotH,
                    vDotH,
                    sampleWeight,
                    visibility,
                    specular: true));
            linearStops[index] = EvaluateReference3DLinearLightSample(
                settings,
                lightColor,
                material,
                attenuation,
                nDotL,
                nDotV,
                nDotH,
                vDotH,
                sampleWeight,
                visibility) with { Position = radialPosition };
        }

        PreserveTransparentEdgeColor(diffuseStops);
        PreserveTransparentEdgeColor(specularStops);

        static void PreserveTransparentEdgeColor(GradientStop[] stops)
        {
            var edge = Color.FromArgb(stops[^1].Argb);
            if (edge.A > 0) return;
            var prior = Color.FromArgb(stops[^2].Argb);
            stops[^1] = new GradientStop(
                1f,
                Color.FromArgb(0, prior.R, prior.G, prior.B));
        }
    }

    private static bool Reference3DGradientHasVisibleEnergy(
        IReadOnlyList<GradientStop> diffuseStops,
        IReadOnlyList<GradientStop> specularStops)
    {
        return diffuseStops.Any(stop => Color.FromArgb(stop.Argb).A > 0)
            || specularStops.Any(stop => Color.FromArgb(stop.Argb).A > 0);
    }

    private static bool Reference3DLinearGradientHasVisibleEnergy(
        IReadOnlyList<Reference3DLinearLightStop> stops)
    {
        return stops.Any(stop =>
            Reference3DLinearVectorHasVisibleEnergy(stop.DiffuseIrradiance)
            || Reference3DLinearVectorHasVisibleEnergy(stop.SpecularRadiance)
            || Reference3DLinearVectorHasVisibleEnergy(stop.FresnelRadiance));
    }

    private static bool Reference3DLinearVectorHasVisibleEnergy(Vector3 value)
    {
        return float.IsFinite(value.X)
            && float.IsFinite(value.Y)
            && float.IsFinite(value.Z)
            && (value.X > 0f || value.Y > 0f || value.Z > 0f);
    }

    private static float Reference3DDielectricF0(SpatialOpticalMaterial material) =>
        Math.Clamp(SceneLightingBrdf.DielectricF0(material.Normalize()), 0f, 1f);

    private static bool TryGetReference3DHalfDots(
        Vector3 normal,
        Vector3 towardLight,
        Vector3 viewDirection,
        out float nDotL,
        out float nDotV,
        out float nDotH,
        out float vDotH)
    {
        nDotL = 0f;
        nDotV = 0f;
        nDotH = 0f;
        vDotH = 0f;
        if (!Finite(normal)
            || !Finite(towardLight)
            || !Finite(viewDirection)
            || normal.LengthSquared() <= 0.000001f
            || towardLight.LengthSquared() <= 0.000001f
            || viewDirection.LengthSquared() <= 0.000001f)
        {
            return false;
        }

        normal = Vector3.Normalize(normal);
        towardLight = Vector3.Normalize(towardLight);
        viewDirection = Vector3.Normalize(viewDirection);
        nDotL = ClampReference3DDot(Vector3.Dot(normal, towardLight));
        nDotV = ClampReference3DDot(Vector3.Dot(normal, viewDirection));
        var halfVector = towardLight + viewDirection;
        var halfLengthSquared = halfVector.LengthSquared();
        if (!Finite(halfVector)
            || !float.IsFinite(halfLengthSquared)
            || halfLengthSquared <= 0.000001f)
        {
            return nDotL > 0f && nDotV == 0f;
        }

        halfVector /= MathF.Sqrt(halfLengthSquared);
        nDotH = ClampReference3DDot(Vector3.Dot(normal, halfVector));
        vDotH = ClampReference3DDot(Vector3.Dot(viewDirection, halfVector));
        return float.IsFinite(nDotL)
            && float.IsFinite(nDotV)
            && float.IsFinite(nDotH)
            && float.IsFinite(vDotH);
    }

    private static bool TryGetReference3DRadialHalfDots(
        Vector3 normal,
        Vector3 viewDirection,
        float normalDistance,
        float radialDistance,
        out float nDotL,
        out float nDotV,
        out float nDotH,
        out float vDotH)
    {
        nDotL = 0f;
        nDotV = 0f;
        nDotH = 0f;
        vDotH = 0f;
        if (!Finite(normal)
            || !Finite(viewDirection)
            || !float.IsFinite(normalDistance)
            || !float.IsFinite(radialDistance)
            || normalDistance <= 0f
            || radialDistance < 0f)
        {
            return false;
        }

        normal = Vector3.Normalize(normal);
        viewDirection = Vector3.Normalize(viewDirection);
        var signedViewDot = Math.Clamp(Vector3.Dot(normal, viewDirection), -1f, 1f);
        var nDotView = Math.Max(0f, signedViewDot);
        var tangent = viewDirection - normal * signedViewDot;
        if (!Finite(tangent) || tangent.LengthSquared() <= 0.000001f)
        {
            if (!TryGetReference3DPlaneBasis(normal, out tangent, out _)) return false;
        }
        else
        {
            tangent = Vector3.Normalize(tangent);
        }

        var towardLight = normal * normalDistance + tangent * radialDistance;
        var distanceSquared = normalDistance * normalDistance
            + radialDistance * radialDistance;
        if (!float.IsFinite(distanceSquared) || distanceSquared <= 0.000001f)
        {
            return false;
        }
        var distance = MathF.Sqrt(distanceSquared);
        if (!float.IsFinite(distance)) return false;
        if (!TryGetReference3DHalfDots(
                normal,
                towardLight,
                viewDirection,
                out _,
                out _,
                out nDotH,
                out vDotH))
        {
            return false;
        }
        nDotL = ClampReference3DDot(normalDistance / distance);
        nDotV = nDotView;
        return true;
    }

    private static float ClampReference3DDot(float value) =>
        float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0f;

    private static int EvaluateReference3DDirectLightSample(
        SceneLightSettings settings,
        Vector3 lightColor,
        SpatialOpticalMaterial material,
        float attenuation,
        float nDotL,
        float nDotV,
        float nDotH,
        float vDotH,
        float sampleWeight,
        float visibility,
        bool specular)
    {
        var energy = settings.Intensity
            * Math.Clamp(attenuation, 0f, 1f)
            * Math.Clamp(visibility, 0f, 1f);
        if (!float.IsFinite(energy) || energy <= 0f) return Color.Transparent.ToArgb();

        material = material.Normalize();
        var response = SceneLightingBrdf.Evaluate(
            material,
            ClampReference3DDot(nDotL),
            ClampReference3DDot(nDotV),
            ClampReference3DDot(nDotH),
            ClampReference3DDot(vDotH));
        // Back-facing extrusion surfaces still carry diffuse illumination into
        // transparency compositing; reflected highlights require a front-facing view.
        if (nDotV <= 0f)
            response = response with { Diffuse = SceneLightingBrdf.EvaluateDiffuse(material, nDotL) };
        var lightRadiance = Reference3DFiniteOrZero(lightColor) * (energy * MathF.PI);
        if (!specular)
        {
            return Reference3DWeightOverlay(
                Reference3DLocalLightOverlay(
                lightRadiance * response.Diffuse),
                sampleWeight);
        }

        var dielectricF0 = Reference3DDielectricF0(material);
        var specularF0 = Math.Clamp(
            dielectricF0 + (1f - dielectricF0) * material.Metallic,
            0f,
            1f);
        return Reference3DWeightOverlay(
            Reference3DHighlightOverlay(
                lightRadiance * (response.Specular * specularF0 + response.Fresnel)),
            sampleWeight);
    }

    private static Reference3DLinearLightStop EvaluateReference3DLinearLightSample(
        SceneLightSettings settings,
        Vector3 lightColor,
        SpatialOpticalMaterial material,
        float attenuation,
        float nDotL,
        float nDotV,
        float nDotH,
        float vDotH,
        float sampleWeight,
        float visibility)
    {
        var energy = settings.Intensity
            * Math.Clamp(attenuation, 0f, 1f)
            * Math.Clamp(visibility, 0f, 1f)
            * Math.Clamp(sampleWeight, 0f, 1f);
        if (!float.IsFinite(energy) || energy <= 0f) return default;

        material = material.Normalize();
        var response = SceneLightingBrdf.Evaluate(
            material,
            ClampReference3DDot(nDotL),
            ClampReference3DDot(nDotV),
            ClampReference3DDot(nDotH),
            ClampReference3DDot(vDotH));
        if (nDotV <= 0f)
            response = response with { Diffuse = SceneLightingBrdf.EvaluateDiffuse(material, nDotL) };
        var radiance = Reference3DFiniteOrZero(lightColor) * (energy * MathF.PI);
        return new Reference3DLinearLightStop(
            0f,
            radiance * response.Diffuse,
            radiance * response.Specular,
            radiance * response.Fresnel);
    }

    private static int Reference3DWeightOverlay(int argb, float weight)
    {
        var color = Color.FromArgb(argb);
        weight = Math.Clamp(weight, 0f, 1f);
        if (color.A == 0 || weight <= 0f) return Color.Transparent.ToArgb();
        if (weight >= 1f) return argb;
        var alpha = color.A / 255f;
        var weightedAlpha = 1f - MathF.Pow(1f - alpha, weight);
        return Color.FromArgb(
            Math.Clamp((int)MathF.Round(weightedAlpha * 255f), 0, 255),
            color.R,
            color.G,
            color.B).ToArgb();
    }

    private static bool TryBuildReference3DVisibleSurfacePaths(
        Reference3DRenderItem item,
        out PathsD paths)
    {
        var surfaceContours = item.Kind is Reference3DRenderKind.FrontStroke
                or Reference3DRenderKind.IntersectionEdge
            ? item.OpticalSurfaceContours ?? []
            : item.Contours;
        paths = Reference3DContoursToPaths(surfaceContours);
        if (!Reference3DPathsHaveArea(paths)) return false;
        if (item.FragmentClip is not { Length: > 0 } fragmentClip) return true;

        try
        {
            paths = Clipper.Intersect(
                paths,
                Reference3DContoursToPaths(fragmentClip),
                FillRule.EvenOdd,
                Reference3DClipperPrecision);
            return Reference3DPathsHaveArea(paths);
        }
        catch (Exception exception) when (Reference3DIsClipperFailure(exception))
        {
            paths = [];
            return false;
        }
    }

    private void BuildReference3DLocalLightOcclusion(
        Reference3DRenderItem receiver,
        Reference3DSurfacePlane receiverPlane,
        Reference3DOpticsEvaluationContext evaluationContext,
        SpatialOpticalMaterial receiverMaterial,
        SceneLightSettings settings,
        Vector3 lightSample,
        float lightPlaneDistance,
        PathsD receiverPaths,
        out PathsD occlusionPaths,
        out float occludedVisibility)
    {
        occlusionPaths = [];
        occludedVisibility = 1f;
        if (!receiverMaterial.ReceivesShadows
            || !settings.CastsShadows
            || settings.ShadowStrength <= 0f
            || lightPlaneDistance <= Reference3DShadowRayBias)
        {
            return;
        }

        var receiverSurfaceKey = (
            receiver.ObjectIndex,
            Reference3DNormalizedSurfaceKind(receiver.Kind),
            receiver.SurfaceSlot);
        var projected = new PathsD();
        var casterCount = 0;
        foreach (var casterCandidate in evaluationContext.ShadowCasters)
        {
            if (LastReference3DShadowProjectionCount
                    >= MaximumReference3DShadowProjectionsPerPlan
                || casterCount >= MaximumReference3DShadowCastersPerReceiver)
            {
                break;
            }
            var caster = casterCandidate.Item;
            var casterSurfaceKey = (
                caster.ObjectIndex,
                Reference3DNormalizedSurfaceKind(caster.Kind),
                caster.SurfaceSlot);
            if (casterSurfaceKey == receiverSurfaceKey)
            {
                continue;
            }
            var casterMaterial = casterCandidate.Material;
            var visibility = Reference3DOccludedLightVisibility(settings, casterMaterial);
            if (!casterMaterial.CastsShadows || visibility >= 0.9999f) continue;
            if (!Reference3DShadowCasterCanReachPlane(
                    casterCandidate,
                    receiverPlane,
                    Reference3DShadowRayBias,
                    lightPlaneDistance - Reference3DShadowRayBias))
            {
                continue;
            }
            if (!TryProjectReference3DFiniteLightOccluder(
                    casterCandidate,
                    receiverPlane,
                    lightSample,
                    lightPlaneDistance,
                    receiverPaths,
                    out var casterProjection))
            {
                continue;
            }

            projected.AddRange(casterProjection);
            casterCount++;
            occludedVisibility = Math.Min(occludedVisibility, Math.Clamp(visibility, 0f, 1f));
        }
        if (!Reference3DPathsHaveArea(projected)) return;

        try
        {
            var union = Clipper.Union(
                projected,
                new PathsD(),
                FillRule.NonZero,
                Reference3DClipperPrecision);
            occlusionPaths = Clipper.Intersect(
                union,
                receiverPaths,
                FillRule.EvenOdd,
                Reference3DClipperPrecision);
            if (!Reference3DPathsHaveArea(occlusionPaths))
            {
                occlusionPaths = [];
                occludedVisibility = 1f;
            }
        }
        catch (Exception exception) when (Reference3DIsClipperFailure(exception))
        {
            occlusionPaths = [];
            occludedVisibility = 1f;
        }
    }

    private void BuildReference3DDirectionalLightOcclusion(
        Reference3DRenderItem receiver,
        Reference3DSurfacePlane receiverPlane,
        Reference3DOpticsEvaluationContext evaluationContext,
        SpatialOpticalMaterial receiverMaterial,
        SceneLightSettings settings,
        Vector3 towardLight,
        PathsD receiverPaths,
        out PathsD occlusionPaths,
        out float occludedVisibility)
    {
        occlusionPaths = [];
        occludedVisibility = 1f;
        if (!receiverMaterial.ReceivesShadows
            || !settings.CastsShadows
            || settings.ShadowStrength <= 0f)
        {
            return;
        }

        var receiverSurfaceKey = (
            receiver.ObjectIndex,
            Reference3DNormalizedSurfaceKind(receiver.Kind),
            receiver.SurfaceSlot);
        var projected = new PathsD();
        var casterCount = 0;
        foreach (var casterCandidate in evaluationContext.ShadowCasters)
        {
            if (LastReference3DShadowProjectionCount
                    >= MaximumReference3DShadowProjectionsPerPlan
                || casterCount >= MaximumReference3DShadowCastersPerReceiver)
            {
                break;
            }
            var caster = casterCandidate.Item;
            var casterSurfaceKey = (
                caster.ObjectIndex,
                Reference3DNormalizedSurfaceKind(caster.Kind),
                caster.SurfaceSlot);
            if (casterSurfaceKey == receiverSurfaceKey)
            {
                continue;
            }
            var casterMaterial = casterCandidate.Material;
            var visibility = Reference3DOccludedLightVisibility(settings, casterMaterial);
            if (!casterMaterial.CastsShadows || visibility >= 0.9999f) continue;
            if (!Reference3DShadowCasterCanReachPlane(
                    casterCandidate,
                    receiverPlane,
                    Reference3DShadowRayBias * 2f,
                    float.PositiveInfinity))
            {
                continue;
            }
            if (!TryProjectReference3DShadow(
                    casterCandidate,
                    receiverPlane,
                    towardLight,
                    receiverPaths,
                    out var casterProjection))
            {
                continue;
            }
            projected.AddRange(casterProjection);
            casterCount++;
            occludedVisibility = Math.Min(occludedVisibility, Math.Clamp(visibility, 0f, 1f));
        }
        if (!Reference3DPathsHaveArea(projected)) return;

        try
        {
            var union = Clipper.Union(
                projected,
                new PathsD(),
                FillRule.NonZero,
                Reference3DClipperPrecision);
            occlusionPaths = Clipper.Intersect(
                union,
                receiverPaths,
                FillRule.EvenOdd,
                Reference3DClipperPrecision);
            if (!Reference3DPathsHaveArea(occlusionPaths))
            {
                occlusionPaths = [];
                occludedVisibility = 1f;
            }
        }
        catch (Exception exception) when (Reference3DIsClipperFailure(exception))
        {
            occlusionPaths = [];
            occludedVisibility = 1f;
        }
    }

    private static float Reference3DOccludedLightVisibility(
        SceneLightSettings settings,
        SpatialOpticalMaterial casterMaterial)
    {
        var shadowOpacity = Math.Clamp(settings.ShadowStrength, 0f, 1f)
            * (1f - Math.Clamp(casterMaterial.Transmission, 0f, 1f));
        var softnessRetention = Math.Clamp(settings.ShadowSoftness, 0f, 1f)
            * Reference3DMaximumSoftShadowRetention;
        return Math.Clamp(1f - shadowOpacity * (1f - softnessRetention), 0f, 1f);
    }

    private static bool Reference3DShadowCasterCanReachPlane(
        Reference3DShadowCaster caster,
        Reference3DSurfacePlane receiverPlane,
        float minimumDistance,
        float maximumDistance)
    {
        if (!receiverPlane.IsValid
            || !Finite(receiverPlane.Normal)
            || !float.IsFinite(receiverPlane.Distance)
            || !float.IsFinite(minimumDistance)
            || float.IsNaN(maximumDistance))
        {
            return true;
        }

        var normal = receiverPlane.Normal;
        var minimumDot = DotBounds(caster.WorldMinimum, caster.WorldMaximum, normal, useMaximum: false);
        var maximumDot = DotBounds(caster.WorldMinimum, caster.WorldMaximum, normal, useMaximum: true);
        if (!float.IsFinite(minimumDot) || !float.IsFinite(maximumDot)) return true;
        var casterMinimum = minimumDot - receiverPlane.Distance;
        var casterMaximum = maximumDot - receiverPlane.Distance;
        return casterMaximum > minimumDistance
            && casterMinimum < maximumDistance;

        static float DotBounds(
            Vector3 minCorner,
            Vector3 maxCorner,
            Vector3 normal,
            bool useMaximum)
        {
            var x = useMaximum ? (normal.X >= 0f ? maxCorner.X : minCorner.X)
                : (normal.X >= 0f ? minCorner.X : maxCorner.X);
            var y = useMaximum ? (normal.Y >= 0f ? maxCorner.Y : minCorner.Y)
                : (normal.Y >= 0f ? minCorner.Y : maxCorner.Y);
            var z = useMaximum ? (normal.Z >= 0f ? maxCorner.Z : minCorner.Z)
                : (normal.Z >= 0f ? minCorner.Z : maxCorner.Z);
            return x * normal.X + y * normal.Y + z * normal.Z;
        }
    }

    private static bool TryGetReference3DShadowCasterContours(
        Reference3DRenderItem caster,
        out IReadOnlyList<Reference3DProjectedContour> contours)
    {
        // The blocker is the visible surface domain, including its intersection
        // fragment. The projection helpers then recover world points on the
        // caster plane because FragmentClip remains a screen-space partition.
        if (!TryBuildReference3DVisibleSurfacePaths(caster, out var paths))
        {
            contours = [];
            return false;
        }

        contours = Reference3DPathsToContours(paths);
        return contours.Count > 0;
    }

    private bool TryProjectReference3DFiniteLightOccluder(
        Reference3DShadowCaster caster,
        Reference3DSurfacePlane receiverPlane,
        Vector3 lightSample,
        float lightPlaneDistance,
        PathsD receiverPaths,
        out PathsD clippedProjection)
    {
        clippedProjection = [];
        var projected = new PathsD();
        foreach (var world in caster.WorldContours)
        {
            var slab = ClipReference3DLightSlab(
                world,
                receiverPlane,
                Reference3DShadowRayBias,
                lightPlaneDistance - Reference3DShadowRayBias);
            if (slab.Count < 3) continue;
            var path = new PathD(slab.Count);
            var valid = true;
            foreach (var casterPoint in slab)
            {
                var direction = casterPoint - lightSample;
                var denominator = Vector3.Dot(receiverPlane.Normal, direction);
                if (!float.IsFinite(denominator)
                    || Math.Abs(denominator) <= 0.000001f)
                {
                    valid = false;
                    break;
                }
                var distance = -lightPlaneDistance / denominator;
                if (!float.IsFinite(distance) || distance <= 1f)
                {
                    valid = false;
                    break;
                }
                var receiverPoint = lightSample + direction * distance;
                if (!Finite(receiverPoint)
                    || !TryProjectScenePosition(receiverPoint, out var projectedPoint, out _))
                {
                    valid = false;
                    break;
                }
                path.Add(new PointD(projectedPoint.X, projectedPoint.Y));
            }
            if (valid && path.Count >= 3) projected.Add(path);
        }
        if (!Reference3DPathsHaveArea(projected)) return false;
        if (!Reference3DProjectedPathsMayOverlap(projected, receiverPaths)) return false;

        try
        {
            LastReference3DShadowProjectionCount++;
            clippedProjection = Clipper.Intersect(
                projected,
                receiverPaths,
                FillRule.EvenOdd,
                Reference3DClipperPrecision);
            return Reference3DPathsHaveArea(clippedProjection);
        }
        catch (Exception exception) when (Reference3DIsClipperFailure(exception))
        {
            clippedProjection = [];
            return false;
        }
    }

    private static List<Vector3> ClipReference3DLightSlab(
        IReadOnlyList<Vector3> polygon,
        Reference3DSurfacePlane receiverPlane,
        float minimumDistance,
        float maximumDistance)
    {
        var clipped = ClipHalfSpace(polygon, minimumDistance, keepAbove: true);
        return clipped.Count >= 3
            ? ClipHalfSpace(clipped, maximumDistance, keepAbove: false)
            : [];

        List<Vector3> ClipHalfSpace(
            IReadOnlyList<Vector3> source,
            float boundary,
            bool keepAbove)
        {
            var output = new List<Vector3>(source.Count + 2);
            if (source.Count == 0) return output;
            var previous = source[^1];
            var previousDistance = SignedDistance(previous);
            var previousInside = Inside(previousDistance);
            foreach (var current in source)
            {
                var currentDistance = SignedDistance(current);
                var currentInside = Inside(currentDistance);
                if (currentInside != previousInside)
                {
                    var denominator = currentDistance - previousDistance;
                    if (float.IsFinite(denominator) && Math.Abs(denominator) > 0.000001f)
                    {
                        var amount = Math.Clamp(
                            (boundary - previousDistance) / denominator,
                            0f,
                            1f);
                        var intersection = Vector3.Lerp(previous, current, amount);
                        if (Finite(intersection)) output.Add(intersection);
                    }
                }
                if (currentInside) output.Add(current);
                previous = current;
                previousDistance = currentDistance;
                previousInside = currentInside;
            }
            return output;

            bool Inside(float distance) => keepAbove
                ? distance >= boundary
                : distance <= boundary;
        }

        float SignedDistance(Vector3 point) =>
            Vector3.Dot(receiverPlane.Normal, point) - receiverPlane.Distance;
    }

    private static bool TryGetReference3DPlaneBasis(
        Vector3 normal,
        out Vector3 axisX,
        out Vector3 axisY)
    {
        axisX = default;
        axisY = default;
        if (!Finite(normal) || normal.LengthSquared() <= 0.000001f) return false;
        normal = Vector3.Normalize(normal);
        var reference = Math.Abs(normal.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitY;
        axisX = Vector3.Normalize(Vector3.Cross(reference, normal));
        axisY = Vector3.Normalize(Vector3.Cross(normal, axisX));
        return Finite(axisX) && Finite(axisY);
    }

    private bool TryProjectReference3DShadow(
        Reference3DShadowCaster caster,
        Reference3DSurfacePlane receiverPlane,
        Vector3 towardLight,
        PathsD receiverPaths,
        out PathsD clippedProjection)
    {
        clippedProjection = [];
        var projected = new PathsD();
        var shadowDirection = -towardLight;
        if (!Finite(shadowDirection)) return false;
        foreach (var world in caster.WorldContours)
        {
            var lightSide = ClipReference3DLightSlab(
                world,
                receiverPlane,
                Reference3DShadowRayBias * 2f,
                float.MaxValue);
            if (lightSide.Count < 3) continue;
            var path = new PathD(lightSide.Count);
            var valid = true;
            foreach (var casterPoint in lightSide)
            {
                if (!TryIntersectReference3DPlane(
                        receiverPlane,
                        casterPoint,
                        shadowDirection,
                        out var receiverPoint)
                    || !TryProjectScenePosition(receiverPoint, out var projectedPoint, out _))
                {
                    valid = false;
                    break;
                }
                path.Add(new PointD(projectedPoint.X, projectedPoint.Y));
            }
            if (valid && path.Count >= 3) projected.Add(path);
        }
        if (!Reference3DPathsHaveArea(projected)) return false;
        if (!Reference3DProjectedPathsMayOverlap(projected, receiverPaths)) return false;

        try
        {
            LastReference3DShadowProjectionCount++;
            clippedProjection = Clipper.Intersect(
                projected,
                receiverPaths,
                FillRule.EvenOdd,
                Reference3DClipperPrecision);
            return Reference3DPathsHaveArea(clippedProjection);
        }
        catch (Exception exception) when (Reference3DIsClipperFailure(exception))
        {
            clippedProjection = [];
            return false;
        }
    }

    private static bool TryIntersectReference3DPlane(
        Reference3DSurfacePlane plane,
        Vector3 origin,
        Vector3 direction,
        out Vector3 point)
    {
        point = default;
        if (!plane.IsValid) return false;
        var denominator = Vector3.Dot(plane.Normal, direction);
        if (!float.IsFinite(denominator) || Math.Abs(denominator) <= 0.000001f) return false;
        var distance = (plane.Distance - Vector3.Dot(plane.Normal, origin)) / denominator;
        if (!float.IsFinite(distance) || distance <= Reference3DShadowRayBias) return false;
        point = origin + direction * distance;
        return Finite(point);
    }

    private static bool Reference3DProjectedPathsMayOverlap(
        IReadOnlyList<PathD> left,
        IReadOnlyList<PathD> right)
    {
        // This test is exact for the rejection case and only avoids invoking
        // Clipper when the projected polygons cannot touch the receiver.
        if (!TryGetReference3DPathBounds(left, out var leftBounds)
            || !TryGetReference3DPathBounds(right, out var rightBounds))
        {
            return true;
        }
        return leftBounds.Left <= rightBounds.Right
            && rightBounds.Left <= leftBounds.Right
            && leftBounds.Top <= rightBounds.Bottom
            && rightBounds.Top <= leftBounds.Bottom;
    }

    private static bool TryGetReference3DPathBounds(
        IReadOnlyList<PathD> paths,
        out (double Left, double Top, double Right, double Bottom) bounds)
    {
        var left = double.PositiveInfinity;
        var top = double.PositiveInfinity;
        var right = double.NegativeInfinity;
        var bottom = double.NegativeInfinity;
        foreach (var path in paths)
        {
            foreach (var point in path)
            {
                if (!double.IsFinite(point.x) || !double.IsFinite(point.y)) continue;
                left = Math.Min(left, point.x);
                top = Math.Min(top, point.y);
                right = Math.Max(right, point.x);
                bottom = Math.Max(bottom, point.y);
            }
        }
        bounds = (left, top, right, bottom);
        return double.IsFinite(left)
            && double.IsFinite(top)
            && double.IsFinite(right)
            && double.IsFinite(bottom);
    }

    private static Vector3 Reference3DDirectionalLightVector(Vector3 rotationDegrees)
    {
        var rotation = Quaternion.CreateFromYawPitchRoll(
            DegreesToRadians(rotationDegrees.Y),
            DegreesToRadians(rotationDegrees.X),
            DegreesToRadians(rotationDegrees.Z));
        var direction = Vector3.Transform(Vector3.UnitZ, rotation);
        return Finite(direction) && direction.LengthSquared() > 0.000001f
            ? Vector3.Normalize(direction)
            : Vector3.UnitZ;
    }

    private static void GetReference3DAreaLightSamples(
        SceneLightSettings settings,
        Span<Vector3> destination)
    {
        var rotation = Quaternion.CreateFromYawPitchRoll(
            DegreesToRadians(settings.RotationDegrees.Y),
            DegreesToRadians(settings.RotationDegrees.X),
            DegreesToRadians(settings.RotationDegrees.Z));
        var axisX = Vector3.Transform(Vector3.UnitX, rotation);
        var axisY = Vector3.Transform(Vector3.UnitY, rotation);
        var index = 0;
        for (var row = 0; row < 3; row++)
        {
            var v = (row + 0.5f) / 3f - 0.5f;
            for (var column = 0; column < 3; column++)
            {
                var u = (column + 0.5f) / 3f - 0.5f;
                destination[index++] = settings.Position
                    + axisX * (u * settings.AreaSize.X)
                    + axisY * (v * settings.AreaSize.Y);
            }
        }
    }

    private static float DegreesToRadians(float degrees) => degrees * MathF.PI / 180f;

    private static Vector3 Reference3DLinearColor(int argb)
    {
        var color = Color.FromArgb(argb);
        return new Vector3(
            Reference3DSrgbByteToLinear[color.R],
            Reference3DSrgbByteToLinear[color.G],
            Reference3DSrgbByteToLinear[color.B]);
    }

    private static float[] CreateReference3DSrgbByteToLinearTable()
    {
        var table = new float[256];
        for (var value = 0; value < table.Length; value++)
        {
            table[value] = Reference3DSrgbToLinear(value / 255f);
        }
        return table;
    }

    private static float Reference3DSrgbToLinear(float value)
    {
        return value <= 0.04045f
            ? value / 12.92f
            : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);
    }

    private static float Reference3DLinearToSrgb(float value)
    {
        value = Math.Max(0f, value);
        return value <= 0.0031308f
            ? value * 12.92f
            : 1.055f * MathF.Pow(value, 1f / 2.4f) - 0.055f;
    }

    private static int Reference3DShadeOverlay(Vector3 illumination)
    {
        var luminance = illumination.X * 0.2126f
            + illumination.Y * 0.7152f
            + illumination.Z * 0.0722f;
        if (!float.IsFinite(luminance)) return Color.Transparent.ToArgb();
        if (luminance < 0.999f)
        {
            var alpha = (int)MathF.Round(Math.Clamp(1f - luminance, 0f, 0.88f) * 255f);
            return Color.FromArgb(alpha, 0, 0, 0).ToArgb();
        }

        var peak = Math.Max(illumination.X, Math.Max(illumination.Y, illumination.Z));
        if (peak <= 1.001f) return Color.Transparent.ToArgb();
        var normalized = illumination / peak;
        var opacity = Math.Clamp(1f - 1f / peak, 0f, 0.55f);
        return Reference3DLinearOverlayColor(normalized, opacity);
    }

    private static int Reference3DHighlightOverlay(Vector3 highlight)
    {
        var peak = Math.Max(highlight.X, Math.Max(highlight.Y, highlight.Z));
        if (!float.IsFinite(peak) || peak <= 0.0001f) return Color.Transparent.ToArgb();
        return Reference3DLinearOverlayColor(
            highlight / peak,
            Math.Clamp(peak, 0f, 0.45f));
    }

    private static int Reference3DLocalLightOverlay(Vector3 illumination)
    {
        var peak = Math.Max(illumination.X, Math.Max(illumination.Y, illumination.Z));
        if (!float.IsFinite(peak) || peak <= 0.0001f) return Color.Transparent.ToArgb();
        var opacity = Math.Clamp(peak / (0.65f + peak), 0f, 0.72f);
        return Reference3DLinearOverlayColor(illumination / peak, opacity);
    }

    private static int Reference3DLinearOverlayColor(Vector3 linear, float opacity)
    {
        static int Channel(float value) => (int)MathF.Round(
            Math.Clamp(Reference3DLinearToSrgb(value), 0f, 1f) * 255f);
        return Color.FromArgb(
            (int)MathF.Round(Math.Clamp(opacity, 0f, 1f) * 255f),
            Channel(linear.X),
            Channel(linear.Y),
            Channel(linear.Z)).ToArgb();
    }

    private readonly record struct Reference3DLocalLightProjectionVertex(
        Vector3 Camera,
        Vector2 Local,
        PointF Screen);
}
