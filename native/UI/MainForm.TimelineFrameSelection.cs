namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    internal readonly record struct TimelineFrameContentSelection(
        int[] ObjectIndices,
        DrawingObjectInstanceDefinition[] Instances,
        bool IncludesLightTrack);

    private void SynchronizeSelectionFromTimelineFrames()
    {
        if (_playing) return;

        var cells = _timeline.SelectedFrameCells;
        if (cells.Count == 0)
        {
            ClearSelection();
            UpdateInspector();
            _stage.Invalidate();
            return;
        }

        var selection = ResolveTimelineFrameContentSelection(_timeline.Context, _scene, cells);
        if (selection.ObjectIndices.Length > 0)
        {
            SetSelection(
                selection.ObjectIndices,
                allowInactiveObjects: true,
                syncTimelineLayer: false);
            if (selection.Instances.Length > 0)
            {
                SetSceneInstanceSelectionCore(
                    selection.Instances,
                    selection.Instances[^1],
                    preserveDrawingSelection: true,
                    syncTimelineLayer: false);
            }
        }
        else if (selection.Instances.Length > 0)
        {
            SetSceneInstanceSelection(selection.Instances, selection.Instances[^1]);
        }
        else if (!selection.IncludesLightTrack)
        {
            ClearSelection();
        }

        UpdateInspector();
        _stage.Invalidate();
    }

    internal static TimelineFrameContentSelection ResolveTimelineFrameContentSelection(
        ITimelineContext context,
        VectorScene editScene,
        IReadOnlyList<TimelineFrameCell> cells)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(editScene);
        ArgumentNullException.ThrowIfNull(cells);

        var objects = new HashSet<int>();
        var instances = new List<DrawingObjectInstanceDefinition>();
        var instanceIds = new HashSet<string>(StringComparer.Ordinal);
        var includesLightTrack = false;

        switch (context)
        {
            case DrawingObjectDefinition drawingObject:
                foreach (var cell in cells)
                {
                    AddDrawingFrameContent(
                        drawingObject.Scene,
                        drawingObject,
                        cell,
                        objects,
                        instances,
                        instanceIds);
                }
                break;

            case VectorScene vectorScene:
                foreach (var cell in cells)
                {
                    AddDrawingFrameContent(
                        vectorScene,
                        drawingObject: null,
                        cell,
                        objects,
                        instances,
                        instanceIds);
                }
                break;

            case SceneDefinition sceneDefinition:
                includesLightTrack = CellsIncludeSceneLight(sceneDefinition, cells);
                if (ReferenceEquals(sceneDefinition.ActiveMaskScene(), editScene))
                {
                    AddSceneMaskFrameContent(sceneDefinition, editScene, cells, objects);
                }
                else
                {
                    AddSceneInstanceFrameContent(
                        sceneDefinition,
                        cells,
                        instances,
                        instanceIds);
                }
                break;
        }

        return new TimelineFrameContentSelection(
            objects.OrderBy(index => index).ToArray(),
            instances.ToArray(),
            includesLightTrack);
    }

    private static void AddDrawingFrameContent(
        VectorScene scene,
        DrawingObjectDefinition? drawingObject,
        TimelineFrameCell cell,
        HashSet<int> objects,
        List<DrawingObjectInstanceDefinition> instances,
        HashSet<string> instanceIds)
    {
        var track = scene.Timeline.FindTrack(cell.TrackId);
        if (track is null || cell.Frame < 0 || cell.Frame >= track.Duration) return;

        var layer = Array.IndexOf(scene.LayerIds, track.TargetId);
        if ((uint)layer >= scene.LayerCount
            || !scene.IsLayerEffectivelyVisible(layer)
            || scene.IsLayerEffectivelyLocked(layer))
        {
            return;
        }

        var exposure = track.EvaluateExposure(cell.Frame);
        if (!exposure.HasContent || exposure.SourceKeyframeFrame < 0) return;

        for (var index = 0; index < scene.ObjectCount; index++)
        {
            if (scene.ObjectLayer[index] == layer
                && scene.ObjectKeyframeFrame[index] == exposure.SourceKeyframeFrame
                && scene.IsObjectSelectable(index, cell.Frame))
            {
                objects.Add(index);
            }
        }

        if (drawingObject is null) return;
        foreach (var instance in drawingObject.InstancesInLayer(track.TargetId))
        {
            if (instance.EvaluateState(cell.Frame).Visible && instanceIds.Add(instance.Id))
            {
                instances.Add(instance);
            }
        }
    }

    private static void AddSceneInstanceFrameContent(
        SceneDefinition scene,
        IReadOnlyList<TimelineFrameCell> cells,
        List<DrawingObjectInstanceDefinition> instances,
        HashSet<string> instanceIds)
    {
        foreach (var cell in cells)
        {
            var track = scene.Timeline.FindTrack(cell.TrackId);
            if (track is null || cell.Frame < 0 || cell.Frame >= track.Duration) continue;

            var layer = scene.FindLayer(track.TargetId);
            if (layer?.Kind != SceneLayerKind.Content || !layer.Visible) continue;

            var exposure = track.EvaluateExposure(cell.Frame);
            if (!exposure.HasContent || exposure.SourceKeyframeFrame < 0) continue;
            if (!string.IsNullOrWhiteSpace(layer.MaskLayerId)
                && !scene.IsMaskLayerActive(layer.MaskLayerId, cell.Frame))
            {
                continue;
            }

            foreach (var instance in scene.InstancesInLayer(layer.Id))
            {
                if (instance.EvaluateState(cell.Frame).Visible && instanceIds.Add(instance.Id))
                {
                    instances.Add(instance);
                }
            }
        }
    }

    private static void AddSceneMaskFrameContent(
        SceneDefinition scene,
        VectorScene maskScene,
        IReadOnlyList<TimelineFrameCell> cells,
        HashSet<int> objects)
    {
        var maskLayer = scene.FindLayer(scene.ActiveLayerId);
        if (maskLayer?.Kind != SceneLayerKind.Mask) return;

        foreach (var cell in cells)
        {
            var outerTrack = scene.Timeline.FindTrack(cell.TrackId);
            if (outerTrack is null
                || !string.Equals(outerTrack.TargetId, maskLayer.Id, StringComparison.Ordinal)
                || cell.Frame < 0
                || cell.Frame >= outerTrack.Duration
                || !scene.IsMaskLayerActive(maskLayer.Id, cell.Frame))
            {
                continue;
            }

            foreach (var innerTrack in maskScene.Timeline.Tracks)
            {
                AddDrawingFrameContent(
                    maskScene,
                    drawingObject: null,
                    new TimelineFrameCell(innerTrack.Id, cell.Frame),
                    objects,
                    instances: [],
                    instanceIds: []);
            }
        }
    }

    private static bool CellsIncludeSceneLight(
        SceneDefinition scene,
        IReadOnlyList<TimelineFrameCell> cells)
    {
        foreach (var cell in cells)
        {
            var track = scene.Timeline.FindTrack(cell.TrackId);
            if (track is not null && scene.FindLight(track.TargetId) is not null) return true;
        }

        return false;
    }
}
