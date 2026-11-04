using System.Drawing;

namespace VectorAnimationEngine;

internal static partial class FlashDocumentImporter
{
    private sealed partial class ImportBuilder
    {
        private readonly Dictionary<string, List<DrawingObjectInstanceDefinition>> _editableStagingInstances = [];
        private readonly HashSet<(string Target, string Layer)> _editableSplitHosts = [];
        private readonly Dictionary<(string Target, string Layer, int Frame), int> _editableRanks = [];
        private readonly Dictionary<(string Target, string Layer, int Rank), string> _editableLayers = [];

        private int ResolveEditableLayer(DrawingObjectDefinition target, int host, int start, int end, bool tween)
        {
            var scene = target.Scene;
            // A mask is one alpha surface; all of its source shapes stay on it.
            if (scene.GetLayerKind(host) == DrawingLayerKind.Mask
                || !_editableSplitHosts.Contains((target.Id, scene.LayerIds[host]))) return host;
            var hostId = scene.LayerIds[host];
            var exposure = (target.Id, hostId, start);
            _editableRanks.TryGetValue(exposure, out var rank);
            _editableRanks[exposure] = rank + 1;
            var key = (target.Id, hostId, checked(start * MaximumElements + rank));
            if (!_editableLayers.TryGetValue(key, out var layerId))
            {
                var created = scene.AddLayer(scene.LayerNames[host] + " / " + (rank + 1));
                layerId = scene.LayerIds[created];
                scene.LayerColorArgb[created] = scene.LayerColorArgb[host];
                // Source masks apply to the source layer's complete contents.
                scene.LayerMaskIds[created] = scene.LayerMaskIds[host];
                _editableLayers.Add(key, layerId);
            }
            var layer = Array.IndexOf(scene.LayerIds, layerId);
            var track = scene.Timeline.FindTrackByTargetId(layerId)!;
            scene.Timeline.InsertBlankKeyframe(track.Id, start);
            if (end < target.FrameCount) scene.Timeline.InsertBlankKeyframe(track.Id, end);
            if (tween)
                for (var frame = start + 1; frame < end; frame++) scene.Timeline.InsertKeyframe(track.Id, frame);
            scene.EditFrame = start;
            return layer;
        }

        private void FinishEditableLayers(DrawingObjectDefinition target)
        {
            var scene = target.Scene;
            var slots = _editableLayers.Where(pair => pair.Key.Target == target.Id).ToArray();
            var original = scene.LayerIds.Where(id => !slots.Any(pair => pair.Value == id)).ToArray();
            var desired = original.SelectMany(id => new[] { id }.Concat(slots
                .Where(pair => pair.Key.Layer == id).OrderBy(pair => pair.Key.Rank).Select(pair => pair.Value))).ToArray();
            // Move before assigning parents, so moving a source row cannot drag
            // an incomplete block and change the painter order.
            for (var index = 0; index < desired.Length; index++)
            {
                var from = Array.IndexOf(scene.LayerIds, desired[index]);
                if (from != index) scene.MoveLayer(from, index);
            }
            foreach (var pair in slots)
            {
                var host = Array.IndexOf(scene.LayerIds, pair.Key.Layer);
                var child = Array.IndexOf(scene.LayerIds, pair.Value);
                scene.LayerKinds[host] = DrawingLayerKind.Folder;
                scene.LayerParentIds[child] = pair.Key.Layer;
            }
        }

        private void AppendEditableLeaf(DrawingObjectDefinition target, int layer,
            DrawingObjectDefinition leaf, InstanceFrameState state, string location)
        {
            if (leaf.Scene.ShapeKind.Take(leaf.Scene.ObjectCount).Any(kind => kind == ShapeKind.ImportedSvg))
                throw Failure(location, "This paint cannot yet be converted to native editable drawing elements. No SVG helper symbol was created.");
            var holder = new DrawingObjectDefinition();
            holder.Scene.CreateEmpty(frameCount: 1);
            var instance = new DrawingObjectInstanceDefinition
            {
                DrawingObjectId = leaf.Id,
                SceneLayerId = holder.Scene.LayerIds[0]
            };
            target.Scene.SetLayerSymbolFilters(layer, state.Filters);
            target.Scene.LayerOpacity[layer] = state.Alpha;
            instance.SetStateAtFrame(0, state with { HoldFrame = 0, Filters = default, Alpha = 1, Visible = true });
            holder.RestoreInstanceSnapshot([instance]);
            holder.SynchronizeTimelineTracks();
            holder.Timeline.InsertKeyframe(holder.Timeline.FindTrackByTargetId(instance.SceneLayerId)!.Id, 0);
            var transformed = new VectorScene();
            try
            {
                SceneCompositionBuilder.BuildDrawingObjectInstanceForBreakApart(
                    transformed, holder, instance, [leaf], 0, fps);
            }
            catch (InvalidDataException exception)
            {
                throw Failure(location, "Native editable placement conversion failed: " + exception.Message);
            }
            foreach (var index in Enumerable.Range(0, transformed.ObjectCount)
                .OrderByDescending(index => transformed.ObjectLayer[index])
                .ThenBy(index => transformed.ObjectOrder[index]).ThenBy(index => transformed.ObjectSubOrder[index]))
            {
                var added = target.Scene.AppendClipboardObject(transformed, index, layer, PointF.Empty);
                // Source elements arrive front-to-back. Each native leaf's own
                // painter order stays intact inside this stack position.
                target.Scene.ObjectHidden[added] = !state.Visible;
                target.Scene.ObjectOrder[added] = -_elements;
                target.Scene.ObjectSubOrder[added] = index;
            }
        }
    }

    private static void RestoreEditableNativeScene(VectorProject project, SceneDefinition scene,
        DrawingObjectDefinition staging, List<FlashImportDiagnostic> diagnostics)
    {
        var source = staging.Scene;
        var members = Enumerable.Range(0, source.LayerCount)
            .Where(layer => source.GetLayerKind(layer) != DrawingLayerKind.Folder).ToArray();
        var drawing = new VectorScene(); drawing.CreateEmpty(Math.Max(1, members.Length), staging.FrameCount);
        var mapping = members.Select((sourceLayer, destination) => (sourceLayer, destination))
            .ToDictionary(pair => pair.sourceLayer, pair => pair.destination);
        var rows = new List<SceneLayerSnapshotItem>();
        for (var index = 0; index < members.Length; index++)
        {
            var layer = members[index];
            drawing.LayerIds[index] = source.LayerIds[layer];
            drawing.LayerLocked[index] = source.IsLayerEffectivelyLocked(layer);
            drawing.LayerNames[index] = source.LayerNames[layer];
            drawing.LayerOpacity[index] = source.LayerOpacity[layer];
            drawing.SetLayerSymbolFilters(index, source.GetLayerSymbolFilters(layer));
            rows.Add(new(source.LayerIds[layer], source.LayerNames[layer], source.IsLayerEffectivelyVisible(layer),
                source.GetEffectiveLayerOutlineColor(layer).IsEmpty ? source.LayerColorArgb[layer]
                    : source.GetEffectiveLayerOutlineColor(layer).ToArgb(), source.IsLayerEffectivelyOutlined(layer))
            {
                Kind = source.GetLayerKind(layer) == DrawingLayerKind.Mask ? SceneLayerKind.Mask : SceneLayerKind.Content,
                MaskLayerId = source.TryGetMaskLayerIndex(layer, out var mask) ? source.LayerIds[mask] : "",
                MaskScene = source.GetLayerKind(layer) == DrawingLayerKind.Mask ? CopyMask(layer) : null
            });
        }
        if (rows.Count == 0)
            rows.Add(new(drawing.LayerIds[0], "Layer", true, drawing.LayerColorArgb[0]));
        foreach (var index in Enumerable.Range(0, source.ObjectCount).OrderBy(index => source.ObjectOrder[index]))
        {
            if (!mapping.TryGetValue(source.ObjectLayer[index], out var layer)) continue;
            drawing.EditFrame = source.ObjectKeyframeFrame[index];
            drawing.AppendClipboardObject(source, index, layer, PointF.Empty);
        }
        // Scene masks have exclusive bindings. Duplicate only the native mask
        // surface for additional consumers, never a library symbol.
        var claimedMasks = new HashSet<string>(StringComparer.Ordinal);
        var extraMasks = new List<SceneLayerSnapshotItem>();
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            if (row.MaskLayerId.Length == 0 || claimedMasks.Add(row.MaskLayerId)) continue;
            var originalMask = rows.Single(item => item.Id == row.MaskLayerId);
            var cloneId = Guid.NewGuid().ToString("N");
            extraMasks.Add(originalMask with { Id = cloneId });
            rows[index] = row with { MaskLayerId = cloneId };
        }
        rows.AddRange(extraMasks);
        var ids = rows.Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
        var importedTimeline = new AnimationTimelineSnapshot
        {
            Tracks = staging.Timeline.CreateSnapshot().Tracks.Where(track => ids.Contains(track.TargetId)).ToArray()
        };
        drawing.Timeline.RestoreSnapshot(importedTimeline);
        drawing.CompleteDeferredBuild();
        scene.RestoreLayerSnapshot(new() { Layers = rows.ToArray(), DrawingScene = drawing.CreateSnapshot(), ActiveLayerId = rows[0].Id });
        foreach (var original in staging.Instances)
        {
            if (!ids.Contains(original.SceneLayerId)
                || rows.Any(row => row.Id == original.SceneLayerId && row.Kind == SceneLayerKind.Mask)) continue;
            scene.AddInstance(project, original.Clone());
        }
        scene.Timeline.RestoreSnapshot(importedTimeline);
        scene.SynchronizeTimelineTracks();
        VectorSceneSnapshot CopyMask(int layer)
        {
            var mask = new VectorScene(); mask.CreateEmpty(frameCount: staging.FrameCount);
            var layerId = source.LayerIds[layer];
            var track = mask.Timeline.FindTrackByTargetId(mask.LayerIds[0])!;
            var originalTrack = source.Timeline.FindTrackByTargetId(layerId)!;
            for (var frame = 0; frame < staging.FrameCount; frame++)
            {
                mask.EditFrame = frame;
                mask.Timeline.InsertBlankKeyframe(track.Id, frame);
                if (!originalTrack.EvaluateExposure(frame).HasContent) continue;
                foreach (var index in Enumerable.Range(0, source.ObjectCount)
                    .Where(index => source.ObjectLayer[index] == layer && source.IsObjectActive(index, frame)))
                    mask.AppendClipboardObject(source, index, 0, PointF.Empty);
                foreach (var placement in staging.InstancesInLayer(layerId).Where(instance => instance.EvaluateState(frame).Visible))
                {
                    var geometry = new VectorScene();
                    SceneCompositionBuilder.BuildDrawingObjectInstanceForBreakApart(geometry, staging, placement,
                        project.DrawingObjects, frame, project.PlaybackFps);
                    foreach (var index in Enumerable.Range(0, geometry.ObjectCount))
                        mask.AppendClipboardObject(geometry, index, 0, PointF.Empty);
                }
                if (mask.ObjectCount > MaximumStateKeys)
                    throw Failure(scene.Name, "Native scene mask geometry exceeds the import budget.");
            }
            mask.CompleteDeferredBuild();
            return mask.CreateSnapshot();
        }
        if (Enumerable.Range(0, source.LayerCount).Any(layer => source.GetLayerKind(layer) == DrawingLayerKind.Folder))
            diagnostics.Add(new("scene-layers-materialized", scene.Name,
                "Scene folder contents are exposed as native scene layers, with effective visibility, lock, outline and masks retained. No additional library symbols were created."));
    }
}
