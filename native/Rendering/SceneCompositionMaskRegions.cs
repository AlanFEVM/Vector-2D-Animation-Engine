using System.Drawing.Drawing2D;

namespace VectorAnimationEngine;

// One render-pass evaluator. Paths supplied by the renderer are already in its
// destination coordinate system (including distortion/camera transforms).
// Regions returned to callers are owned clones; cached regions live only until
// this evaluator is disposed, so frame/device changes cannot reuse stale clips.
internal sealed class SceneCompositionMaskRegions : IDisposable
{
    private readonly VectorScene _scene;
    private readonly SceneCompositionMaskPlan _plan;
    private readonly Func<int, IReadOnlyList<GraphicsPath>> _objectPaths;
    private readonly Dictionary<int, List<int>> _objectsByLayer = [];
    private readonly Dictionary<int, Region> _coverage = [];
    private readonly HashSet<int> _visiting = [];

    internal SceneCompositionMaskRegions(VectorScene scene, SceneCompositionMaskPlan plan,
        int frame, Func<int, GraphicsPath?> objectPath)
        : this(scene, plan, frame, index => objectPath(index) is { } path ? new[] { path } : [])
    {
    }

    internal SceneCompositionMaskRegions(VectorScene scene, SceneCompositionMaskPlan plan,
        int frame, Func<int, IReadOnlyList<GraphicsPath>> objectPaths)
    {
        _scene = scene;
        _plan = plan;
        _objectPaths = objectPaths;
        for (var index = 0; index < scene.ObjectCount; index++)
        {
            if (!scene.IsObjectActive(index, frame)) continue;
            var layer = scene.ObjectLayer[index];
            if (!_objectsByLayer.TryGetValue(layer, out var objects))
                _objectsByLayer.Add(layer, objects = []);
            objects.Add(index);
        }
    }

    internal Region? CreateLayerClip(int layer)
        => CreateLayerClip(layer, null);

    // An intersection edge can reference another layer. Keep that layer's
    // independent masks, but defer masks shared with the owner whose complete
    // filtered output will be clipped at its own compositing boundary.
    internal Region? CreateRelatedLayerClip(int layer, int ownerLayer)
        => CreateLayerClip(layer, _plan.ClipsByLayer.TryGetValue(ownerLayer, out var ownerMasks)
            ? ownerMasks.ToHashSet() : null);

    // A parent group applies its inherited masks after its filters. Its children
    // must not pre-clip those same masks, but still apply their own inner masks.
    internal Region? CreateLayerBoundaryClip(int layer)
    {
        var deferred = new HashSet<int>();
        var current = layer;
        var ancestors = new HashSet<int> { layer };
        while (!string.IsNullOrEmpty(_scene.LayerParentIds[current]))
        {
            current = Array.IndexOf(_scene.LayerIds, _scene.LayerParentIds[current]);
            if (current < 0 || !ancestors.Add(current))
                throw new InvalidDataException("Invalid composed mask parent hierarchy.");
            if (_plan.ClipsByLayer.TryGetValue(current, out var parentMasks)) deferred.UnionWith(parentMasks);
        }
        return CreateLayerClip(layer, deferred);
    }

    private Region? CreateLayerClip(int layer, HashSet<int>? deferred)
    {
        if (!_plan.ClipsByLayer.TryGetValue(layer, out var masks)) return null;
        Region? result = null;
        try
        {
            foreach (var mask in masks)
            {
                if (deferred?.Contains(mask) == true) continue;
                var coverage = GetMaskCoverage(mask);
                if (result is null) result = coverage.Clone();
                else result.Intersect(coverage);
            }
            return result;
        }
        catch { result?.Dispose(); throw; }
    }

    internal Region CreateMaskCoverage(int mask) => GetMaskCoverage(mask).Clone();

    private Region GetMaskCoverage(int mask)
    {
        if (_coverage.TryGetValue(mask, out var cached)) return cached;
        if (!_plan.ContentsByMaskLayer.TryGetValue(mask, out var layers))
            throw new InvalidDataException("Missing composed mask coverage.");
        if (!_visiting.Add(mask)) throw new InvalidDataException("Cyclic composed mask coverage.");
        var result = new Region();
        result.MakeEmpty();
        try
        {
            foreach (var layer in layers)
            {
                if ((uint)layer >= _scene.LayerCount)
                    throw new InvalidDataException("Invalid composed mask layer.");
                using var contents = new Region();
                contents.MakeEmpty();
                foreach (var index in _objectsByLayer.GetValueOrDefault(layer) ?? [])
                {
                    var paths = _objectPaths(index);
                    try
                    {
                        foreach (var path in paths)
                            if (path.PointCount > 0) contents.Union(path);
                    }
                    finally { foreach (var path in paths) path.Dispose(); }
                }
                // Union separate objects first. A compound object's own path
                // retains its fill rule/holes; overlapping siblings never XOR.
                using var clip = CreateLayerClip(layer);
                if (clip is not null) contents.Intersect(clip);
                result.Union(contents);
            }
            _coverage.Add(mask, result);
            return result;
        }
        catch { result.Dispose(); throw; }
        finally { _visiting.Remove(mask); }
    }

    public void Dispose()
    {
        foreach (var region in _coverage.Values) region.Dispose();
        _coverage.Clear();
    }
}
