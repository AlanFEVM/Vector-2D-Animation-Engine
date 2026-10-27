namespace VectorAnimationEngine;

internal sealed partial class SceneDefinition
{
    internal SceneShotSettings ResolveShotParallaxAnchor(string shotId)
    {
        var shot = FindShot(shotId);
        if (shot is null) return SceneShotSettings.Default;
        var track = FindShotTrack(shotId);
        if (track is not null)
        {
            foreach (var keyframe in track.Keyframes)
                if (keyframe.HasContent && TryEvaluateShotSettings(shotId, keyframe.Frame, out var settings))
                    return settings;
        }
        return shot.Settings;
    }

    internal bool SetLayerParallax(string layerId, float depth, bool enabled)
    {
        if (!float.IsFinite(depth) || Math.Abs(depth) > SceneShotSettings.MaximumPosition
            || FindLayer(layerId) is not { Kind: SceneLayerKind.Content } layer) return false;
        if (layer.ParallaxDepth == depth && layer.ParallaxEnabled == enabled) return false;
        layer.ParallaxDepth = depth;
        layer.ParallaxEnabled = enabled;
        if (FindLayer(layer.MaskLayerId) is { } mask)
        {
            mask.ParallaxDepth = depth;
            mask.ParallaxEnabled = enabled;
        }
        return true;
    }
}
