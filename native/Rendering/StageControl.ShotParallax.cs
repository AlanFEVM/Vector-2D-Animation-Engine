using System.Numerics;

namespace VectorAnimationEngine;

internal sealed partial class StageControl
{
    private Dictionary<SceneCompositionMaskClip, Matrix4x4>? _shotParallaxMaskTransforms;

    private IDisposable? BeginShotParallax(SceneShotSettings settings, SceneShotSettings? parallaxAnchor = null)
    {
        if (!settings.Parallax.Enabled || settings.Parallax.Strength <= 0 || !settings.PinsPlanarRotation
            || _referenceSceneDefinition is not { } definition
            || _sceneCompositionResult is not { } composition
            || !ReferenceEquals(_sceneCompositionResultScene, Scene)) return null;

        var instanceLayers = definition.Instances.ToDictionary(instance => instance.Id,
            instance => instance.SceneLayerId, StringComparer.Ordinal);
        var selectedShotId = _shotFrameDisplays.FirstOrDefault(frame => frame.Selected).Id;
        var anchor = parallaxAnchor ?? (!string.IsNullOrEmpty(selectedShotId)
            ? definition.ResolveShotParallaxAnchor(selectedShotId)
            : settings with { X = 0, Y = 0, Z = settings.Parallax.SizeAnchorZ });
        var layerTransforms = definition.Layers.ToDictionary(layer => layer.Id,
            layer => layer.ParallaxEnabled ? settings.Parallax.ResolveTransform(settings, layer.ParallaxDepth, anchor)
                : Matrix4x4.Identity, StringComparer.Ordinal);
        var transforms = new Matrix4x4[composition.ObjectPoses.Count];
        var parents = Enumerable.Range(0, transforms.Length).ToArray();
        int Root(int index)
        {
            while (parents[index] != index)
            {
                parents[index] = parents[parents[index]];
                index = parents[index];
            }
            return index;
        }
        foreach (var clip in _sceneCompositionMaskClips)
        {
            if (!ReferenceEquals(clip.TargetScene, Scene)) continue;
            var first = -1;
            foreach (var index in clip.TargetObjectIndices.Order())
            {
                if ((uint)index >= parents.Length) continue;
                if (first < 0) first = index;
                else
                {
                    var firstRoot = Root(first);
                    var currentRoot = Root(index);
                    parents[Math.Max(firstRoot, currentRoot)] = Math.Min(firstRoot, currentRoot);
                }
            }
        }
        for (var index = 0; index < transforms.Length; index++)
        {
            composition.TryGetOwner(Root(index), out var owner);
            var rootId = string.IsNullOrEmpty(owner.RootInstanceId) ? owner.InstanceId : owner.RootInstanceId;
            var layerId = owner.SceneLayerId;
            if (!string.IsNullOrEmpty(rootId)) instanceLayers.TryGetValue(rootId, out layerId);
            transforms[index] = !string.IsNullOrEmpty(layerId)
                && layerTransforms.TryGetValue(layerId, out var transform) ? transform : Matrix4x4.Identity;
        }
        var maskTransforms = new Dictionary<SceneCompositionMaskClip, Matrix4x4>();
        foreach (var clip in _sceneCompositionMaskClips)
        {
            if (!ReferenceEquals(clip.TargetScene, Scene)) continue;
            foreach (var index in clip.TargetObjectIndices)
            {
                if ((uint)index >= transforms.Length) continue;
                maskTransforms[clip] = transforms[index];
                break;
            }
        }
        var scope = new ShotParallaxScope(this, composition, _sceneCompositionHasSpatialPoses, _shotParallaxMaskTransforms);
        _shotParallaxMaskTransforms = maskTransforms;
        _sceneCompositionResult = new SceneCompositionResult(
            Enumerable.Range(0, transforms.Length).Select(index => composition.ObjectOwners[index]).ToArray(),
            composition.ObjectPoses.Select((pose, index) => pose with
            {
                FlatToScene = pose.FlatToScene * transforms[index],
                ExtrusionVector = Vector3.TransformNormal(pose.ExtrusionVector, transforms[index])
            }).ToArray(), composition.ObjectMaterials.ToArray())
        {
            HostLayerRanges = composition.HostLayerRanges
        };
        _sceneCompositionHasSpatialPoses = true;
        ClearShotPreviewProjectionCaches();
        return scope;
    }

    private PointF[][] ApplyShotParallaxToMask(SceneCompositionMaskClip clip, PointF[][] contours)
    {
        if (_shotParallaxMaskTransforms?.TryGetValue(clip, out var transform) != true) return contours;
        return contours.Select(contour => contour.Select(point =>
        {
            var mapped = Vector3.Transform(new Vector3(point.X, point.Y, 0), transform);
            return new PointF(mapped.X, mapped.Y);
        }).ToArray()).ToArray();
    }

    private sealed class ShotParallaxScope : IDisposable
    {
        private readonly StageControl _stage;
        private readonly SceneCompositionResult _composition;
        private readonly bool _hasSpatialPoses;
        private readonly Dictionary<SceneCompositionMaskClip, Matrix4x4>? _masks;
        private readonly Reference3DRenderPlanCacheEntry[] _renderPlans;
        private readonly long _surfaceBytes;

        public ShotParallaxScope(StageControl stage, SceneCompositionResult composition,
            bool hasSpatialPoses, Dictionary<SceneCompositionMaskClip, Matrix4x4>? masks)
        {
            _stage = stage;
            _composition = composition;
            _hasSpatialPoses = hasSpatialPoses;
            _masks = masks;
            _renderPlans = stage._reference3DRenderPlanCache.ToArray();
            _surfaceBytes = stage._reference3DRenderPlanSurfaceBytes;
            stage._reference3DRenderPlanCache.Clear();
            stage._reference3DRenderPlanSurfaceBytes = 0;
        }

        public void Dispose()
        {
            _stage._sceneCompositionResult = _composition;
            _stage._sceneCompositionHasSpatialPoses = _hasSpatialPoses;
            _stage._shotParallaxMaskTransforms = _masks;
            foreach (var plan in _stage._reference3DRenderPlanCache) plan.Dispose();
            _stage._reference3DRenderPlanCache.Clear();
            _stage._reference3DRenderPlanCache.AddRange(_renderPlans);
            _stage._reference3DRenderPlanSurfaceBytes = _surfaceBytes;
            _stage.ClearShotPreviewProjectionCaches();
        }
    }
}
