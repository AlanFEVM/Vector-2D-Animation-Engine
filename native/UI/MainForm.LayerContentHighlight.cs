namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private const int MaxSceneLayerContentHighlightObjects = 512;
    private (object Context, string? TrackId, int DrawingLayer)? _dismissedLayerContentHighlight;

    private (object Context, string? TrackId, int DrawingLayer) CurrentLayerContentHighlightTarget() =>
        (_timeline.Context, _timeline.ActiveTrackId,
            _timeline.Context is SceneDefinition ? -1 : _scene.ActiveLayer);

    private void DismissStageLayerContentHighlight()
    {
        _dismissedLayerContentHighlight = CurrentLayerContentHighlightTarget();
        _stage.ClearLayerContentHighlight();
    }

    /// <summary>
    /// Keeps the Stage layer-content highlight in sync with the active layer. Drawing and open
    /// canvas contexts use the vector layer directly; scene content layers map their instances
    /// back to flattened composition objects so only the selected scene layer is outlined.
    /// </summary>
    private void RefreshStageLayerContentHighlight()
    {
        if (_stage is null || _stage.IsDisposed) return;
        if (_scene is null || !ReferenceEquals(_stage.Scene, _scene))
        {
            _stage.ClearLayerContentHighlight();
            return;
        }
        if (_dismissedLayerContentHighlight is { } dismissed)
        {
            var current = CurrentLayerContentHighlightTarget();
            if (ReferenceEquals(dismissed.Context, current.Context)
                && string.Equals(dismissed.TrackId, current.TrackId, StringComparison.Ordinal)
                && dismissed.DrawingLayer == current.DrawingLayer)
            {
                _stage.ClearLayerContentHighlight();
                return;
            }
            _dismissedLayerContentHighlight = null;
        }
        if (_playing || IsShotDirectorContext())
        {
            _stage.ClearLayerContentHighlight();
            return;
        }

        if (IsSceneBuildingContext())
        {
            RefreshSceneLayerContentHighlight();
            return;
        }

        if (_timeline.Context is DrawingObjectDefinition or VectorScene
            && (uint)_scene.ActiveLayer < _scene.LayerCount)
        {
            _stage.SetLayerContentHighlightLayer(
                _scene.ActiveLayer,
                _scene.GetLayerColor(_scene.ActiveLayer));
            return;
        }

        _stage.ClearLayerContentHighlight();
    }

    private void RefreshSceneLayerContentHighlight()
    {
        var sceneDefinition = ActiveScene();
        var selectedTrackId = _timeline.ActiveTrackId;
        var layer = sceneDefinition is null
            ? null
            : string.IsNullOrWhiteSpace(selectedTrackId)
                ? sceneDefinition.FindLayer(sceneDefinition.ActiveLayerId)
                : sceneDefinition.FindLayer(selectedTrackId);
        if (sceneDefinition is null || layer is null)
        {
            _stage.ClearLayerContentHighlight();
            return;
        }

        if (layer.Kind == SceneLayerKind.Content
            && ReferenceEquals(_stage.Scene, sceneDefinition.ActiveMaskScene()))
        {
            _stage.ClearLayerContentHighlight();
            return;
        }
        if (layer.Kind == SceneLayerKind.Mask
            && !ReferenceEquals(_stage.Scene, sceneDefinition.ActiveMaskScene()))
        {
            _stage.ClearLayerContentHighlight();
            return;
        }

        var color = Color.FromArgb(layer.ColorArgb);
        if (IsSceneMaskEditing())
        {
            var maskLayer = _scene.ActiveLayer;
            if ((uint)maskLayer < _scene.LayerCount)
            {
                _stage.SetLayerContentHighlightLayer(maskLayer, color);
            }
            else
            {
                _stage.ClearLayerContentHighlight();
            }
            return;
        }

        if (layer.Kind != SceneLayerKind.Content || !layer.Visible)
        {
            _stage.ClearLayerContentHighlight();
            return;
        }

        if (IsSceneDrawingContext())
        {
            var sourceLayer = Array.IndexOf(_scene.LayerIds, layer.Id);
            if (sourceLayer >= 0) _stage.SetLayerContentHighlightLayer(sourceLayer, color);
            else _stage.ClearLayerContentHighlight();
            return;
        }

        var instanceIds = sceneDefinition.InstancesInLayer(layer.Id)
            .Select(instance => instance.Id)
            .ToHashSet(StringComparer.Ordinal);
        var highlighted = new List<int>();
        var objectCount = Math.Min(
            _sceneEditStage.ObjectCount,
            _sceneCompositionResult.ObjectOwners.Count);
        for (var objectIndex = 0; objectIndex < objectCount; objectIndex++)
        {
            if (!_sceneCompositionResult.TryGetOwner(objectIndex, out var owner)) continue;
            var rootInstanceId = string.IsNullOrWhiteSpace(owner.RootInstanceId)
                ? owner.InstanceId
                : owner.RootInstanceId;
            if (!string.Equals(owner.SceneLayerId, layer.Id, StringComparison.Ordinal)
                && !instanceIds.Contains(owner.InstanceId)
                && !instanceIds.Contains(rootInstanceId))
            {
                continue;
            }

            highlighted.Add(objectIndex);
            if (highlighted.Count >= MaxSceneLayerContentHighlightObjects) break;
        }

        if (highlighted.Count == 0)
        {
            _stage.ClearLayerContentHighlight();
            return;
        }
        _stage.SetLayerContentHighlightObjects(highlighted, color);
    }
}
