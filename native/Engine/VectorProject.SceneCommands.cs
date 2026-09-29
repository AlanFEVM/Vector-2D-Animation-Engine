namespace VectorAnimationEngine;

internal sealed partial class VectorProject
{
    public bool TryAddSceneLayer(
        string sceneId,
        string? name,
        out SceneLayerDefinition? layer)
    {
        layer = null;
        var scene = FindScene(sceneId);
        if (scene is null) return false;

        layer = scene.AddLayer(this, name);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool TryRemoveScene(string sceneId, out SceneDefinition? removed)
    {
        removed = null;
        if (string.IsNullOrWhiteSpace(sceneId) || _scenes.Count <= 1) return false;

        var index = -1;
        for (var candidate = 0; candidate < _scenes.Count; candidate++)
        {
            if (!string.Equals(_scenes[candidate].Id, sceneId, StringComparison.Ordinal)) continue;
            if (index >= 0) return false;
            index = candidate;
        }

        if (index < 0) return false;
        removed = _scenes[index];
        _scenes.RemoveAt(index);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }
}
