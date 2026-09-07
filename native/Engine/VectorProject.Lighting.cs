namespace VectorAnimationEngine;

internal sealed partial class VectorProject
{
    public bool TrySetSceneDimension(string sceneId, SceneDimension dimension)
    {
        var scene = FindScene(sceneId);
        if (scene is null || !Enum.IsDefined(dimension) || scene.Dimension == dimension) return false;
        scene.Dimension = dimension;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool TryAddSceneLight(
        string sceneId,
        SceneLightKind kind,
        out SceneLightDefinition? light)
    {
        light = null;
        var scene = FindScene(sceneId);
        if (scene is null || !Enum.IsDefined(kind) || scene.Lights.Count >= SceneDefinition.MaximumLights) return false;
        light = SceneLightDefinition.CreateDefault(kind);
        if (!scene.AddLight(light))
        {
            light = null;
            return false;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool TryUpdateSceneLight(
        string sceneId,
        string lightId,
        string? name,
        SceneLightSettings settings)
    {
        return TryUpdateSceneLight(sceneId, lightId, name, settings, 0);
    }

    public bool TryUpdateSceneLight(
        string sceneId,
        string lightId,
        string? name,
        SceneLightSettings settings,
        int frame)
    {
        return TryUpdateSceneLightAtFrame(sceneId, lightId, name, settings, frame);
    }

    public bool TryUpdateSceneLightAtFrame(
        string sceneId,
        string lightId,
        string? name,
        SceneLightSettings settings,
        int frame)
    {
        return TryUpdateSceneLightAtFrameCore(
            sceneId,
            lightId,
            name,
            settings,
            frame,
            preview: false);
    }

    internal bool TryPreviewSceneLightAtFrame(
        string sceneId,
        string lightId,
        string? name,
        SceneLightSettings settings,
        int frame)
    {
        return TryUpdateSceneLightAtFrameCore(
            sceneId,
            lightId,
            name,
            settings,
            frame,
            preview: true);
    }

    private bool TryUpdateSceneLightAtFrameCore(
        string sceneId,
        string lightId,
        string? name,
        SceneLightSettings settings,
        int frame,
        bool preview)
    {
        var scene = FindScene(sceneId);
        if (scene is null) return false;
        var changed = preview
            ? scene.PreviewLightAtFrame(lightId, name, settings, frame)
            : scene.UpdateLightAtFrame(lightId, name, settings, frame);
        if (!changed) return false;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool TryRemoveSceneLight(string sceneId, string lightId)
    {
        var scene = FindScene(sceneId);
        if (scene is null || !scene.RemoveLight(lightId)) return false;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool TrySetSceneInstanceOpticalMaterial(
        string sceneId,
        string instanceId,
        SpatialOpticalMaterial? material)
    {
        return TrySetInstanceOpticalMaterial(FindScene(sceneId)?.Instances, instanceId, material);
    }

    public bool TrySetDrawingObjectInstanceOpticalMaterial(
        string drawingObjectId,
        string instanceId,
        SpatialOpticalMaterial? material)
    {
        return TrySetInstanceOpticalMaterial(FindDrawingObject(drawingObjectId)?.Instances, instanceId, material);
    }

    private bool TrySetInstanceOpticalMaterial(
        IReadOnlyList<DrawingObjectInstanceDefinition>? instances,
        string instanceId,
        SpatialOpticalMaterial? material)
    {
        if (instances is null
            || string.IsNullOrWhiteSpace(instanceId)
            || material is { IsValid: false })
        {
            return false;
        }

        var instance = instances.SingleOrDefault(candidate =>
            string.Equals(candidate.Id, instanceId, StringComparison.Ordinal));
        if (instance is null || instance.OpticalMaterialOverride == material) return false;
        instance.OpticalMaterialOverride = material;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }
}
