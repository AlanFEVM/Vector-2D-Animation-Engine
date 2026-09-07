namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    partial void RecordSceneOpticsUndo(
        SceneOpticsEditSnapshot before,
        SceneOpticsEditSnapshot after)
    {
        var scene = _scenes.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, before.SceneId, StringComparison.Ordinal));
        if (scene is null || !string.Equals(before.SceneId, after.SceneId, StringComparison.Ordinal)) return;

        var instanceSnapshot = scene.CreateInstanceSnapshot();
        foreach (var instance in instanceSnapshot)
        {
            if (before.InstanceMaterials.TryGetValue(instance.Id, out var material))
                instance.OpticalMaterialOverride = material;
        }

        PushSceneTimelineUndo(
            scene,
            before.Timeline,
            instanceSnapshot: instanceSnapshot,
            playheadFrame: _frame,
            timelineSelection: _timeline.CaptureSelectionSnapshot(),
            lightSnapshot: before.Lights.Select(light => light.Clone()).ToArray(),
            selectedLightId: before.SelectedLightId);
    }
}
