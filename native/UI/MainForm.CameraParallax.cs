namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private SceneLayerSnapshot? _sceneShotEditLayerSnapshot;

    private void InitializeCameraParallax()
    {
        var editor = _shotDirectorPanel.ParallaxEditor;
        editor.EditStarted += (_, _) =>
        {
            var scene = ActiveScene();
            if (scene is null) return;
            BeginSceneShotEdit(scene);
            _sceneShotEditLayerSnapshot ??= scene.CreateLayerSnapshot();
        };
        editor.EditCompleted += (_, _) => CompleteSceneShotEdit();
        editor.EditCanceled += (_, _) => CancelSceneShotEdit();
        editor.SettingsChanged += (_, eventArgs) =>
        {
            ApplySceneShotFraming(eventArgs);
            _shotDirectorFramingPushed = null;
            PushShotDirectorFraming();
            RefreshShotPreview();
            _stage.Invalidate();
        };
        editor.LayerChanged += (_, eventArgs) =>
        {
            var scene = ActiveScene();
            if (scene is null) return;
            if (scene.SetLayerParallax(eventArgs.LayerId, eventArgs.Depth, eventArgs.Enabled)) MarkProjectDirty();
            var layers = scene.Layers.Where(layer => layer.Kind == SceneLayerKind.Content)
                .Select(layer => new CameraParallaxLayer(layer.Id, layer.Name, layer.ColorArgb,
                    layer.ParallaxDepth, layer.ParallaxEnabled)).ToArray();
            _shotDirectorPanel.SetParallaxLayers(layers);
            RefreshShotPreview();
            _stage.Invalidate();
        };
    }

    private static bool ParallaxLayersEqual(SceneLayerSnapshot? before, SceneDefinition scene) =>
        before is null || before.Layers.Select(layer => (layer.Id, layer.ParallaxDepth, layer.ParallaxEnabled))
            .SequenceEqual(scene.Layers.Select(layer => (layer.Id, layer.ParallaxDepth, layer.ParallaxEnabled)));
}
