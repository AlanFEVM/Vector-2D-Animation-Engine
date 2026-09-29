using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Clipper2Lib;

namespace VectorAnimationEngine;

internal sealed partial class VectorScene
{
    public const string CollisionTerrainLayerName = "Collision Terrain";
    private const float CollisionTerrainLayerOpacity = 0.35f;
    private static readonly Color CollisionTerrainObjectColor = Color.FromArgb(255, 236, 181, 72);

    internal static int NormalizeCollisionTerrainObjectArgb(int colorArgb)
    {
        // The layer opacity supplies the editor translucency. Keep the stored
        // fill opaque so collision extraction is independent of the paint UI.
        var color = Color.FromArgb(colorArgb);
        if (color.A == 0) return CollisionTerrainObjectColor.ToArgb();
        return Color.FromArgb(255, color.R, color.G, color.B).ToArgb();
    }

    public bool IsLayerActive(int layer, int frame)
    {
        return layer >= 0
            && layer < LayerCount
            && GetLayerKind(layer) != DrawingLayerKind.Folder
            && IsLayerEffectivelyVisible(layer)
            && Timeline.EvaluateTargetExposure(LayerIds[layer], frame).HasContent;
    }

    public DrawingLayerKind GetLayerKind(int layer)
    {
        return (uint)layer < LayerKinds.Length ? LayerKinds[layer] : DrawingLayerKind.Drawing;
    }

    public int GetLayerParentIndex(int layer)
    {
        if ((uint)layer >= LayerCount || layer >= LayerParentIds.Length) return -1;
        var parentId = LayerParentIds[layer];
        if (string.IsNullOrWhiteSpace(parentId)) return -1;
        var parent = Array.IndexOf(LayerIds, parentId);
        return parent >= 0 && GetLayerKind(parent) == DrawingLayerKind.Folder ? parent : -1;
    }

    public int GetLayerDepth(int layer)
    {
        if ((uint)layer >= LayerCount) return 0;
        var depth = 0;
        var current = layer;
        for (var hop = 0; hop < LayerCount; hop++)
        {
            var parent = GetLayerParentIndex(current);
            if (parent < 0) break;
            depth++;
            current = parent;
        }

        return depth;
    }

    public bool IsLayerEffectivelyVisible(int layer)
    {
        if ((uint)layer >= LayerCount) return false;
        var current = layer;
        for (var hop = 0; hop < LayerCount; hop++)
        {
            if (!LayerVisible[current]) return false;
            var parent = GetLayerParentIndex(current);
            if (parent < 0) return true;
            current = parent;
        }

        return false;
    }

    public bool IsLayerEffectivelyLocked(int layer)
    {
        if ((uint)layer >= LayerCount) return false;
        var current = layer;
        for (var hop = 0; hop < LayerCount; hop++)
        {
            if (current < LayerLocked.Length && LayerLocked[current]) return true;
            var parent = GetLayerParentIndex(current);
            if (parent < 0) return false;
            current = parent;
        }

        return true;
    }

    public bool IsObjectSelectable(int objectIndex, int frame)
    {
        return (uint)objectIndex < ObjectCount
            && IsObjectActive(objectIndex, frame)
            && !IsLayerEffectivelyLocked(ObjectLayer[objectIndex]);
    }

    public bool ShouldRenderLayerContent(int layer)
    {
        if ((uint)layer >= LayerCount) return false;
        if (IsCollisionTerrainLayer(layer)) return false;
        return GetLayerKind(layer) switch
        {
            DrawingLayerKind.Folder => false,
            DrawingLayerKind.Mask => !IsLayerEffectivelyLocked(layer),
            _ => true
        };
    }

    public bool TryGetMaskLayerIndex(int layer, out int maskLayer)
    {
        maskLayer = -1;
        if ((uint)layer >= LayerCount
            || GetLayerKind(layer) != DrawingLayerKind.Drawing
            || layer >= LayerMaskIds.Length
            || string.IsNullOrWhiteSpace(LayerMaskIds[layer]))
        {
            return false;
        }

        var candidate = Array.IndexOf(LayerIds, LayerMaskIds[layer]);
        if (candidate < 0 || GetLayerKind(candidate) != DrawingLayerKind.Mask) return false;
        maskLayer = candidate;
        return true;
    }

    public int GetMaskContentLayerIndex(int maskLayer)
    {
        if ((uint)maskLayer >= LayerCount || GetLayerKind(maskLayer) != DrawingLayerKind.Mask) return -1;
        var maskId = LayerIds[maskLayer];
        for (var layer = 0; layer < LayerCount; layer++)
        {
            if (string.Equals(LayerMaskIds[layer], maskId, StringComparison.Ordinal)) return layer;
        }

        return -1;
    }

    public bool SetLayerMask(int contentLayer, int maskLayer)
    {
        if ((uint)contentLayer >= LayerCount
            || (uint)maskLayer >= LayerCount
            || GetLayerKind(contentLayer) != DrawingLayerKind.Drawing
            || GetLayerKind(maskLayer) != DrawingLayerKind.Mask)
        {
            return false;
        }

        var maskId = LayerIds[maskLayer];
        var changed = false;
        for (var layer = 0; layer < LayerCount; layer++)
        {
            if (layer == contentLayer || !string.Equals(LayerMaskIds[layer], maskId, StringComparison.Ordinal)) continue;
            LayerMaskIds[layer] = string.Empty;
            changed = true;
        }

        if (!string.Equals(LayerMaskIds[contentLayer], maskId, StringComparison.Ordinal))
        {
            LayerMaskIds[contentLayer] = maskId;
            changed = true;
        }

        var parentId = LayerParentIds[maskLayer];
        if (!string.Equals(LayerParentIds[contentLayer], parentId, StringComparison.Ordinal))
        {
            LayerParentIds[contentLayer] = parentId;
            changed = true;
        }

        if (changed) InvalidateQueryActiveKeyframes();
        return changed;
    }

    public bool ClearLayerMask(int contentLayer)
    {
        if ((uint)contentLayer >= LayerCount || GetLayerKind(contentLayer) != DrawingLayerKind.Drawing) return false;
        if (string.IsNullOrWhiteSpace(LayerMaskIds[contentLayer])) return false;
        LayerMaskIds[contentLayer] = string.Empty;
        InvalidateQueryActiveKeyframes();
        return true;
    }

    public bool MoveLayerOutOfMask(int contentLayer)
    {
        if (!TryGetMaskLayerIndex(contentLayer, out var maskLayer)) return false;
        var contentLayerId = LayerIds[contentLayer];
        var maskLayerId = LayerIds[maskLayer];
        if (!ClearLayerMask(contentLayer)) return false;

        contentLayer = Array.IndexOf(LayerIds, contentLayerId);
        maskLayer = Array.IndexOf(LayerIds, maskLayerId);
        if (contentLayer >= 0 && maskLayer >= 0) MoveLayerAfter(contentLayer, maskLayer);
        return true;
    }

    public bool ClearMaskLayerLinks(int maskLayer)
    {
        if ((uint)maskLayer >= LayerCount || GetLayerKind(maskLayer) != DrawingLayerKind.Mask) return false;
        var maskId = LayerIds[maskLayer];
        var changed = false;
        for (var layer = 0; layer < LayerCount; layer++)
        {
            if (!string.Equals(LayerMaskIds[layer], maskId, StringComparison.Ordinal)) continue;
            LayerMaskIds[layer] = string.Empty;
            changed = true;
        }

        if (changed) InvalidateQueryActiveKeyframes();
        return changed;
    }

    public bool CanSetLayerParent(int layer, int parentLayer)
    {
        if ((uint)layer >= LayerCount) return false;
        if (parentLayer < 0) return true;
        if ((uint)parentLayer >= LayerCount || GetLayerKind(parentLayer) != DrawingLayerKind.Folder) return false;
        if (layer == parentLayer) return false;

        var current = parentLayer;
        var visited = new HashSet<int>();
        while (visited.Add(current))
        {
            if (current == layer) return false;
            current = GetLayerParentIndex(current);
            if (current < 0) return true;
        }

        return false;
    }

    public bool SetLayerParent(int layer, int parentLayer)
    {
        if (!CanSetLayerParent(layer, parentLayer)) return false;
        var parentId = parentLayer >= 0 ? LayerIds[parentLayer] : string.Empty;
        var relatedLayers = GetLinkedLayerIndices(layer);
        var changed = false;
        foreach (var relatedLayer in relatedLayers)
        {
            if (string.Equals(LayerParentIds[relatedLayer], parentId, StringComparison.Ordinal)) continue;
            LayerParentIds[relatedLayer] = parentId;
            changed = true;
        }

        if (changed) InvalidateQueryActiveKeyframes();
        return changed;
    }

    public bool RenameLayer(int layer, string? name)
    {
        if ((uint)layer >= LayerCount) return false;
        var normalized = name?.Trim();
        if (string.IsNullOrWhiteSpace(normalized)) return false;
        normalized = normalized.Length <= 80 ? normalized : normalized[..80];
        if (string.Equals(LayerNames[layer], normalized, StringComparison.Ordinal)) return false;
        LayerNames[layer] = normalized;
        return true;
    }

    public bool ToggleLayerLocked(int layer)
    {
        if ((uint)layer >= LayerCount) return false;
        LayerLocked[layer] = !LayerLocked[layer];
        return true;
    }

    public bool SetLayerLocked(int layer, bool locked)
    {
        if ((uint)layer >= LayerCount || LayerLocked[layer] == locked) return false;
        LayerLocked[layer] = locked;
        return true;
    }

    public bool IsLayerDescendantOf(int layer, int ancestor)
    {
        if ((uint)layer >= LayerCount || (uint)ancestor >= LayerCount || layer == ancestor) return false;
        var current = GetLayerParentIndex(layer);
        var visited = new HashSet<int>();
        while (current >= 0 && visited.Add(current))
        {
            if (current == ancestor) return true;
            current = GetLayerParentIndex(current);
        }

        return false;
    }

    public int GetLayerSubtreeEnd(int layer)
    {
        if ((uint)layer >= LayerCount) return -1;
        var last = layer;
        for (var candidate = layer + 1; candidate < LayerCount; candidate++)
        {
            if (IsLayerDescendantOf(candidate, layer)) last = candidate;
        }

        return last;
    }

    public IReadOnlyList<int> GetLayerDisplayOrder()
    {
        if (LayerCount == 0) return [];

        var result = new List<int>(LayerCount);
        var appended = new bool[LayerCount];

        void AppendLayer(int layer)
        {
            if ((uint)layer >= LayerCount || appended[layer]) return;
            appended[layer] = true;
            result.Add(layer);

            if (GetLayerKind(layer) == DrawingLayerKind.Folder)
            {
                for (var candidate = 0; candidate < LayerCount; candidate++)
                {
                    if (GetLayerParentIndex(candidate) != layer) continue;
                    if (TryGetMaskLayerIndex(candidate, out _)) continue;
                    AppendLayer(candidate);
                }

                return;
            }

            if (GetLayerKind(layer) != DrawingLayerKind.Mask) return;
            var contentLayer = GetMaskContentLayerIndex(layer);
            if (contentLayer >= 0) AppendLayer(contentLayer);
        }

        for (var layer = 0; layer < LayerCount; layer++)
        {
            if (GetLayerParentIndex(layer) >= 0 || TryGetMaskLayerIndex(layer, out _)) continue;
            AppendLayer(layer);
        }

        for (var layer = 0; layer < LayerCount; layer++) AppendLayer(layer);
        return result;
    }

    public void SynchronizeTimelineTracks()
    {
        var additionalTargets = AdditionalTimelineTargetIds();
        var targetIds = additionalTargets.Length == 0
            ? LayerIds
            : LayerIds.Concat(additionalTargets).ToArray();
        Timeline.SynchronizeTracks(targetIds, FrameCount, populateNewTracks: false);
        var additionalTargetSet = additionalTargets.ToHashSet(StringComparer.Ordinal);
        foreach (var track in Timeline.Tracks)
        {
            if (track.Keyframes.Count == 0 || track.Keyframes[0].Frame > 0)
            {
                if (additionalTargetSet.Contains(track.TargetId)) Timeline.InsertKeyframe(track.Id, 0);
                else Timeline.InsertBlankKeyframe(track.Id, 0);
            }
        }

        foreach (var layer in Enumerable.Range(0, LayerCount))
        {
            if (!IsLegacyCollisionTerrainLayer(layer)) continue;
            var track = Timeline.FindTrackByTargetId(LayerIds[layer]);
            if (track is null || track.IsCollisionTerrain) continue;
            track.IsCollisionTerrain = true;
            Timeline.SetTrackTabGroup(track.Id, AnimationTimeline.TerrainTabGroupId);
        }
    }

    public int AddLayer(string? name = null) => InsertLayer(DrawingLayerKind.Drawing, LayerCount, name);

    public int AddFolderLayer(string? name = null)
    {
        var contentLayer = FindEditableLayer(ActiveLayer, allowMask: false);
        if (contentLayer < 0) return AddLayer(name);

        var parentId = LayerParentIds[contentLayer];
        var folderLayer = InsertLayer(DrawingLayerKind.Folder, contentLayer, name ?? $"Folder {LayerCount:0000}");
        LayerParentIds[folderLayer] = parentId;
        LayerParentIds[contentLayer + 1] = LayerIds[folderLayer];
        ActiveLayer = contentLayer + 1;
        return folderLayer;
    }

    public int AddMaskLayer(string? name = null)
    {
        var contentLayer = FindEditableLayer(ActiveLayer, allowMask: false);
        if (contentLayer < 0) return AddLayer(name);

        var parentId = LayerParentIds[contentLayer];
        var maskLayer = InsertLayer(DrawingLayerKind.Mask, contentLayer, name ?? $"Mask {LayerCount:0000}");
        LayerParentIds[maskLayer] = parentId;
        LayerMaskIds[contentLayer + 1] = LayerIds[maskLayer];
        ActiveLayer = maskLayer;
        return maskLayer;
    }

    public bool TryGetCollisionTerrainLayer(out int layer)
    {
        layer = FindCollisionTerrainLayer();
        return layer >= 0;
    }

    public int[] GetCollisionTerrainLayers()
    {
        if (LayerCount == 0) return [];

        var result = new List<int>();
        for (var candidate = 0; candidate < LayerCount; candidate++)
        {
            if (IsCollisionTerrainLayer(candidate)) result.Add(candidate);
        }

        return result.ToArray();
    }

    public int FindCollisionTerrainLayer()
    {
        var layers = GetCollisionTerrainLayers();
        return layers.Length > 0 ? layers[0] : -1;
    }

    internal bool IsCollisionTerrainLayer(int layer)
    {
        if ((uint)layer >= LayerCount
            || GetLayerKind(layer) != DrawingLayerKind.Mask
            || !string.IsNullOrWhiteSpace(LayerMaskIds[layer]))
        {
            return false;
        }

        var layerId = LayerIds[layer];
        for (var maskIndex = 0; maskIndex < LayerMaskIds.Length; maskIndex++)
        {
            if (string.Equals(LayerMaskIds[maskIndex], layerId, StringComparison.Ordinal))
            {
                return false;
            }
        }

        var track = Timeline.FindTrackByTargetId(layerId);
        return track?.IsCollisionTerrain == true
            || string.Equals(LayerNames[layer], CollisionTerrainLayerName, StringComparison.Ordinal);
    }

    private bool IsLegacyCollisionTerrainLayer(int layer)
    {
        return (uint)layer < LayerCount
            && GetLayerKind(layer) == DrawingLayerKind.Mask
            && string.Equals(LayerNames[layer], CollisionTerrainLayerName, StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(LayerMaskIds[layer]);
    }

    public int AddCollisionTerrainLayer(string? name = null)
    {
        if (LayerCount >= ushort.MaxValue) return -1;
        var layerName = string.IsNullOrWhiteSpace(name)
            ? GetUniqueCollisionTerrainLayerName()
            : name.Trim();
        var layer = InsertLayer(DrawingLayerKind.Mask, LayerCount, layerName);
        PrepareCollisionTerrainLayer(layer, layerName);
        ActiveLayer = layer;
        return layer;
    }

    private string GetUniqueCollisionTerrainLayerName()
    {
        var existingNames = LayerNames.ToHashSet(StringComparer.Ordinal);
        if (!existingNames.Contains(CollisionTerrainLayerName)) return CollisionTerrainLayerName;

        for (var suffix = 2; suffix <= LayerCount + 1; suffix++)
        {
            var candidate = $"{CollisionTerrainLayerName} {suffix}";
            if (!existingNames.Contains(candidate)) return candidate;
        }

        return $"{CollisionTerrainLayerName} {Guid.NewGuid():N}";
    }

    private void PrepareCollisionTerrainLayer(int layer, string? name = null)
    {
        if ((uint)layer >= LayerCount || GetLayerKind(layer) != DrawingLayerKind.Mask) return;

        if (!string.IsNullOrWhiteSpace(name)) LayerNames[layer] = name.Trim();
        LayerParentIds[layer] = string.Empty;
        LayerMaskIds[layer] = string.Empty;
        LayerOpacity[layer] = CollisionTerrainLayerOpacity;
        LayerVisible[layer] = true;
        LayerLocked[layer] = false;

        var frame = Math.Max(0, EditFrame);
        var track = Timeline.FindTrackByTargetId(LayerIds[layer]);
        if (track is not null)
        {
            track.IsCollisionTerrain = true;
            Timeline.SetTrackTabGroup(track.Id, AnimationTimeline.TerrainTabGroupId);
            if (frame >= track.Duration) Timeline.SetTrackDuration(track.Id, frame + 1);
            Timeline.InsertKeyframe(track.Id, frame);
            RefreshLegacyExposureBounds(layer);
        }

        InvalidateQueryActiveKeyframes();
    }

    private int InsertLayer(DrawingLayerKind kind, int index, string? name)
    {
        if (LayerCount >= ushort.MaxValue) return Math.Max(0, LayerCount - 1);

        var duration = Math.Max(1, FrameCount);
        var previousCount = LayerCount;
        index = Math.Clamp(index, 0, previousCount);
        var knownIds = LayerIds.ToHashSet(StringComparer.Ordinal);
        var layerId = Guid.NewGuid().ToString("N");
        while (!knownIds.Add(layerId)) layerId = Guid.NewGuid().ToString("N");

        LayerIds = InsertLayerValue(LayerIds, index, layerId);
        LayerNames = InsertLayerValue(
            LayerNames,
            index,
            string.IsNullOrWhiteSpace(name)
                ? kind switch
                {
                    DrawingLayerKind.Folder => $"Folder {previousCount:0000}",
                    DrawingLayerKind.Mask => $"Mask {previousCount:0000}",
                    _ => $"Layer {previousCount:0000}"
                }
                : name.Trim());
        LayerKinds = InsertLayerValue(LayerKinds, index, kind);
        LayerParentIds = InsertLayerValue(LayerParentIds, index, string.Empty);
        LayerMaskIds = InsertLayerValue(LayerMaskIds, index, string.Empty);
        LayerLocked = InsertLayerValue(LayerLocked, index, false);
        LayerVisible = InsertLayerValue(LayerVisible, index, true);
        LayerOpacity = InsertLayerValue(LayerOpacity, index, 1f);
        LayerBlendModes = InsertLayerValue(LayerBlendModes, index, LayerBlendMode.Normal);
        LayerColorArgb = InsertLayerValue(LayerColorArgb, index, DefaultLayerColor(index).ToArgb());
        LayerOutline = InsertLayerValue(LayerOutline, index, false);
        LayerStart = InsertLayerValue(LayerStart, index, 0);
        LayerEnd = InsertLayerValue(LayerEnd, index, -1);
        LayerCount++;

        for (var objectIndex = 0; objectIndex < ObjectCount; objectIndex++)
        {
            if (ObjectLayer[objectIndex] >= index) ObjectLayer[objectIndex]++;
        }

        ActiveLayer = index;
        using (Timeline.BeginBatchUpdate())
        {
            SynchronizeTimelineTracks();
            var track = Timeline.FindTrackByTargetId(layerId);
            if (track is not null)
            {
                Timeline.SetTrackDuration(track.Id, duration);
                Timeline.InsertBlankKeyframe(track.Id, 0);
            }
        }

        RebuildGeometryIndex();
        RebuildSummaries();
        return index;
    }

    private int ResolveObjectLayer(int requestedLayer)
    {
        var layer = FindEditableLayer(requestedLayer, allowMask: true);
        return layer >= 0 ? layer : Math.Clamp(requestedLayer, 0, LayerCount - 1);
    }

    internal string ResolveInstanceLayerId(string? preferredLayerId)
    {
        if (LayerCount == 0) return string.Empty;
        var preferredLayer = string.IsNullOrWhiteSpace(preferredLayerId)
            ? ActiveLayer
            : Array.IndexOf(LayerIds, preferredLayerId);
        if (preferredLayer < 0) preferredLayer = ActiveLayer;
        var layer = FindEditableLayer(preferredLayer, allowMask: false);
        return LayerIds[layer >= 0 ? layer : 0];
    }

    private int FindEditableLayer(int requestedLayer, bool allowMask)
    {
        if (LayerCount == 0) return -1;
        requestedLayer = Math.Clamp(requestedLayer, 0, LayerCount - 1);
        var requestedKind = GetLayerKind(requestedLayer);
        if (requestedKind == DrawingLayerKind.Drawing || (allowMask && requestedKind == DrawingLayerKind.Mask)) return requestedLayer;
        if (requestedKind != DrawingLayerKind.Folder) return -1;

        var folderId = LayerIds[requestedLayer];
        for (var layer = requestedLayer + 1; layer < LayerCount; layer++)
        {
            if (!string.Equals(LayerParentIds[layer], folderId, StringComparison.Ordinal)) continue;
            var kind = GetLayerKind(layer);
            if (kind == DrawingLayerKind.Drawing || (allowMask && kind == DrawingLayerKind.Mask)) return layer;
        }

        return -1;
    }

    private static T[] InsertLayerValue<T>(IReadOnlyList<T> source, int index, T value)
    {
        var result = new T[source.Count + 1];
        for (var sourceIndex = 0; sourceIndex < index; sourceIndex++) result[sourceIndex] = source[sourceIndex];
        result[index] = value;
        for (var sourceIndex = index; sourceIndex < source.Count; sourceIndex++) result[sourceIndex + 1] = source[sourceIndex];
        return result;
    }

    public Color GetLayerColor(int layer)
    {
        return (uint)layer < LayerColorArgb.Length
            ? Color.FromArgb(LayerColorArgb[layer])
            : DefaultLayerColor(layer);
    }

    public bool SetLayerColor(int layer, Color color)
    {
        if ((uint)layer >= LayerCount) return false;
        var next = color.ToArgb();
        if (LayerColorArgb[layer] == next) return false;
        LayerColorArgb[layer] = next;
        return true;
    }

    public bool SetLayerBlendMode(int layer, LayerBlendMode blendMode)
    {
        if ((uint)layer >= LayerCount || !Enum.IsDefined(blendMode) || LayerBlendModes[layer] == blendMode) return false;
        LayerBlendModes[layer] = blendMode;
        return true;
    }

    public bool IsLayerEffectivelyOutlined(int layer)
    {
        if ((uint)layer >= LayerCount) return false;
        var current = layer;
        for (var hop = 0; hop < LayerCount && current >= 0; hop++)
        {
            if (current < LayerOutline.Length && LayerOutline[current]) return true;
            current = GetLayerParentIndex(current);
        }

        return false;
    }

    public Color GetEffectiveLayerOutlineColor(int layer)
    {
        return IsLayerEffectivelyOutlined(layer) ? GetLayerColor(layer) : Color.Empty;
    }

    public bool ToggleLayerOutline(int layer)
    {
        if ((uint)layer >= LayerCount) return false;
        LayerOutline[layer] = !LayerOutline[layer];
        return true;
    }

    public bool SetLayerOutline(int layer, bool outline)
    {
        if ((uint)layer >= LayerCount || LayerOutline[layer] == outline) return false;
        LayerOutline[layer] = outline;
        return true;
    }

    public bool ToggleOnionSkin()
    {
        return SetOnionSkinEnabled(!OnionSkinEnabled);
    }

    public bool SetOnionSkinEnabled(bool enabled)
    {
        if (OnionSkinEnabled == enabled) return false;
        OnionSkinEnabled = enabled;
        return true;
    }

    public bool SetOnionSkinRange(int previousFrames, int nextFrames)
    {
        var previous = NormalizeOnionSkinFrames(previousFrames, DefaultOnionSkinPreviousFrames);
        var next = NormalizeOnionSkinFrames(nextFrames, DefaultOnionSkinNextFrames);
        if (OnionSkinPreviousFrames == previous && OnionSkinNextFrames == next) return false;

        OnionSkinPreviousFrames = previous;
        OnionSkinNextFrames = next;
        return true;
    }

    public bool MoveLayer(int from, int to)
    {
        if ((uint)from >= LayerCount || (uint)to >= LayerCount || from == to) return false;

        var moving = GetLayerMoveSet(from);
        if (moving.Contains(to)) return false;
        return MoveLayerToRemainingIndex(from, moving, Math.Clamp(to, 0, LayerCount - moving.Count));
    }

    private bool MoveLayerToRemainingIndex(int from, IReadOnlySet<int> moving, int destinationIndex)
    {
        var ordered = Enumerable.Range(0, LayerCount).ToList();
        var remaining = ordered.Where(layer => !moving.Contains(layer)).ToList();
        var block = ordered.Where(moving.Contains).ToList();
        var insertAt = Math.Clamp(destinationIndex, 0, remaining.Count);
        remaining.InsertRange(insertAt, block);
        if (remaining.SequenceEqual(ordered)) return false;
        var destinationBySource = new int[LayerCount];
        for (var destination = 0; destination < remaining.Count; destination++)
        {
            destinationBySource[remaining[destination]] = destination;
        }

        LayerIds = ReorderLayers(LayerIds, destinationBySource);
        LayerNames = ReorderLayers(LayerNames, destinationBySource);
        LayerKinds = ReorderLayers(LayerKinds, destinationBySource);
        LayerParentIds = ReorderLayers(LayerParentIds, destinationBySource);
        LayerMaskIds = ReorderLayers(LayerMaskIds, destinationBySource);
        LayerLocked = ReorderLayers(LayerLocked, destinationBySource);
        LayerVisible = ReorderLayers(LayerVisible, destinationBySource);
        LayerOpacity = ReorderLayers(LayerOpacity, destinationBySource);
        LayerBlendModes = ReorderLayers(LayerBlendModes, destinationBySource);
        LayerColorArgb = ReorderLayers(LayerColorArgb, destinationBySource);
        LayerOutline = ReorderLayers(LayerOutline, destinationBySource);
        LayerStart = ReorderLayers(LayerStart, destinationBySource);
        LayerEnd = ReorderLayers(LayerEnd, destinationBySource);
        for (var objectIndex = 0; objectIndex < ObjectCount; objectIndex++)
        {
            ObjectLayer[objectIndex] = (ushort)destinationBySource[ObjectLayer[objectIndex]];
        }

        ActiveLayer = destinationBySource[Math.Clamp(ActiveLayer, 0, LayerCount - 1)];
        SynchronizeTimelineTracks();
        InvalidateQueryActiveKeyframes();
        RebuildGeometryIndex();
        RebuildSummaries();
        return true;
    }

    public bool MoveLayerBefore(int from, int beforeLayer)
    {
        if ((uint)from >= LayerCount || (uint)beforeLayer >= LayerCount || from == beforeLayer) return false;
        if (TryGetMaskLayerIndex(beforeLayer, out var maskLayer)) beforeLayer = maskLayer;
        var moving = GetLayerMoveSet(from);
        if (moving.Contains(beforeLayer)) return false;
        var destination = Enumerable.Range(0, beforeLayer).Count(layer => !moving.Contains(layer));
        return MoveLayerToRemainingIndex(from, moving, destination);
    }

    public bool MoveLayerAfter(int from, int afterLayer)
    {
        if ((uint)from >= LayerCount || (uint)afterLayer >= LayerCount || from == afterLayer) return false;
        var moving = GetLayerMoveSet(from);
        if (moving.Contains(afterLayer)) return false;
        var boundary = GetLayerGroupEnd(afterLayer) + 1;
        var destination = Enumerable.Range(0, boundary).Count(layer => !moving.Contains(layer));
        return MoveLayerToRemainingIndex(from, moving, destination);
    }

    private int GetLayerGroupEnd(int layer)
    {
        var last = layer;
        foreach (var member in GetLayerMoveSet(layer)) last = Math.Max(last, member);
        return last;
    }

    private HashSet<int> GetLayerMoveSet(int rootLayer)
    {
        var result = new HashSet<int> { rootLayer };
        var pending = new Queue<int>();
        pending.Enqueue(rootLayer);
        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            var currentId = LayerIds[current];
            for (var candidate = 0; candidate < LayerCount; candidate++)
            {
                if (result.Contains(candidate)) continue;
                var isChild = string.Equals(LayerParentIds[candidate], currentId, StringComparison.Ordinal);
                var isLinkedMask = string.Equals(LayerMaskIds[candidate], currentId, StringComparison.Ordinal)
                    || string.Equals(LayerMaskIds[current], LayerIds[candidate], StringComparison.Ordinal);
                if (!isChild && !isLinkedMask) continue;
                result.Add(candidate);
                pending.Enqueue(candidate);
            }
        }

        return result;
    }

    public int[] ResolveLayerRemovalIndices(IEnumerable<string> layerIds)
    {
        ArgumentNullException.ThrowIfNull(layerIds);
        var requestedIds = layerIds
            .Where(layerId => !string.IsNullOrWhiteSpace(layerId))
            .ToHashSet(StringComparer.Ordinal);
        if (requestedIds.Count == 0) return [];

        var removal = new HashSet<int>();
        for (var layer = 0; layer < LayerCount; layer++)
        {
            if (!requestedIds.Contains(LayerIds[layer])) continue;
            removal.UnionWith(GetLayerMoveSet(layer));
        }

        return removal.OrderBy(layer => layer).ToArray();
    }

    public bool RemoveLayers(IEnumerable<string> layerIds)
    {
        var removal = ResolveLayerRemovalIndices(layerIds);
        if (removal.Length == 0 || removal.Length >= LayerCount) return false;

        var removedLayers = removal.ToHashSet();
        var removedLayerIds = removal.Select(layer => LayerIds[layer]).ToHashSet(StringComparer.Ordinal);
        RemoveObjects(Enumerable.Range(0, ObjectCount).Where(index => removedLayers.Contains(ObjectLayer[index])));

        var destinationBySource = new int[LayerCount];
        Array.Fill(destinationBySource, -1);
        var destination = 0;
        for (var source = 0; source < LayerCount; source++)
        {
            if (removedLayers.Contains(source)) continue;
            destinationBySource[source] = destination++;
        }

        var oldActiveLayer = Math.Clamp(ActiveLayer, 0, LayerCount - 1);
        LayerIds = RemoveLayerValues(LayerIds, removedLayers);
        LayerNames = RemoveLayerValues(LayerNames, removedLayers);
        LayerKinds = RemoveLayerValues(LayerKinds, removedLayers);
        LayerParentIds = RemoveLayerValues(LayerParentIds, removedLayers)
            .Select(parentId => removedLayerIds.Contains(parentId) ? string.Empty : parentId)
            .ToArray();
        LayerMaskIds = RemoveLayerValues(LayerMaskIds, removedLayers)
            .Select(maskId => removedLayerIds.Contains(maskId) ? string.Empty : maskId)
            .ToArray();
        LayerLocked = RemoveLayerValues(LayerLocked, removedLayers);
        LayerVisible = RemoveLayerValues(LayerVisible, removedLayers);
        LayerOpacity = RemoveLayerValues(LayerOpacity, removedLayers);
        LayerBlendModes = RemoveLayerValues(LayerBlendModes, removedLayers);
        LayerColorArgb = RemoveLayerValues(LayerColorArgb, removedLayers);
        LayerOutline = RemoveLayerValues(LayerOutline, removedLayers);
        LayerStart = RemoveLayerValues(LayerStart, removedLayers);
        LayerEnd = RemoveLayerValues(LayerEnd, removedLayers);
        LayerCount = destination;

        for (var objectIndex = 0; objectIndex < ObjectCount; objectIndex++)
        {
            ObjectLayer[objectIndex] = (ushort)destinationBySource[ObjectLayer[objectIndex]];
        }

        if (destinationBySource[oldActiveLayer] >= 0)
        {
            ActiveLayer = destinationBySource[oldActiveLayer];
        }
        else
        {
            var nearestSource = Enumerable.Range(0, destinationBySource.Length)
                .Where(source => destinationBySource[source] >= 0)
                .MinBy(source => Math.Abs(source - oldActiveLayer));
            ActiveLayer = destinationBySource[nearestSource];
        }

        using (Timeline.BeginBatchUpdate()) SynchronizeTimelineTracks();
        InvalidateQueryActiveKeyframes();
        RebuildGeometryIndex();
        RebuildSummaries();
        return true;
    }

    private static T[] RemoveLayerValues<T>(IReadOnlyList<T> source, IReadOnlySet<int> removedLayers)
    {
        var result = new T[source.Count - removedLayers.Count];
        var destination = 0;
        for (var sourceIndex = 0; sourceIndex < source.Count; sourceIndex++)
        {
            if (removedLayers.Contains(sourceIndex)) continue;
            result[destination++] = source[sourceIndex];
        }
        return result;
    }

    private int[] GetLinkedLayerIndices(int layer)
    {
        var related = new List<int> { layer };
        var layerId = LayerIds[layer];
        for (var candidate = 0; candidate < LayerCount; candidate++)
        {
            if (candidate == layer) continue;
            if (string.Equals(LayerMaskIds[candidate], layerId, StringComparison.Ordinal)
                || string.Equals(LayerMaskIds[layer], LayerIds[candidate], StringComparison.Ordinal))
            {
                related.Add(candidate);
            }
        }

        return related.ToArray();
    }

    public bool CopyTimelineFrameFrom(
        VectorScene source,
        int sourceLayer,
        int sourceFrame,
        int destinationLayer,
        int destinationFrame)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (ReferenceEquals(source, this))
        {
            var snapshotSource = new VectorScene();
            snapshotSource.RestoreSnapshot(CreateSnapshot());
            return CopyTimelineFrameFrom(snapshotSource, sourceLayer, sourceFrame, destinationLayer, destinationFrame);
        }
        if ((uint)sourceLayer >= source.LayerCount
            || (uint)destinationLayer >= LayerCount
            || sourceFrame < 0
            || destinationFrame < 0)
        {
            return false;
        }

        var sourceTrack = source.TimelineTrackForLayer(sourceLayer);
        var destinationTrack = TimelineTrackForLayer(destinationLayer);
        if (sourceTrack is null || destinationTrack is null) return false;

        var sourceExposure = sourceTrack.EvaluateExposure(Math.Min(sourceFrame, sourceTrack.Duration - 1));
        if (!sourceExposure.HasContent)
        {
            return InsertTimelineBlankKeyframe(destinationLayer, destinationFrame);
        }

        var sourceObjects = Enumerable.Range(0, source.ObjectCount)
            .Where(index => source.ObjectLayer[index] == sourceLayer
                && source.ObjectKeyframeFrame[index] == sourceExposure.SourceKeyframeFrame)
            .ToArray();
        if (sourceObjects.Length == 0)
        {
            return InsertTimelineBlankKeyframe(destinationLayer, destinationFrame);
        }

        using var batchUpdate = Timeline.BeginBatchUpdate();
        if (destinationFrame >= destinationTrack.Duration)
        {
            Timeline.SetTrackDuration(destinationTrack.Id, destinationFrame + 1);
        }

        _synchronizingKeyframeContent = true;
        try
        {
            RemoveObjectsForKeyframe(destinationLayer, destinationFrame);
            Timeline.InsertKeyframe(destinationTrack.Id, destinationFrame);
            CloneObjectsFrom(source, sourceObjects, destinationLayer, destinationFrame);
        }
        finally
        {
            _synchronizingKeyframeContent = false;
        }

        RefreshLegacyExposureBounds(destinationLayer);
        return true;
    }

    public void BuildOnionSkinPreview(
        VectorScene destination,
        int frame,
        int? previousFrames = null,
        int? nextFrames = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var previousRange = NormalizeOnionSkinFrames(previousFrames, OnionSkinPreviousFrames);
        var nextRange = NormalizeOnionSkinFrames(nextFrames, OnionSkinNextFrames);
        if (!OnionSkinEnabled
            || (previousRange <= 0 && nextRange <= 0)
            || !HasEligibleOnionSkinLayer())
        {
            destination.CreateEmpty();
            return;
        }

        var candidates = new List<(int Layer, int Keyframe, float Opacity, bool IsPrevious)>();
        var seen = new HashSet<(int Layer, int Keyframe)>();
        var lastFrame = Math.Max(0, FrameCount - 1);

        for (var layer = 0; layer < LayerCount; layer++)
        {
            if (!IsLayerEligibleForOnionSkin(layer)) continue;
            var track = TimelineTrackForLayer(layer);
            if (track is null) continue;
            var currentExposure = track.EvaluateExposure(Math.Clamp(frame, 0, track.Duration - 1));
            var currentKeyframe = currentExposure.HasContent ? currentExposure.SourceKeyframeFrame : -1;

            for (var offset = previousRange; offset >= 1; offset--)
            {
                var previewFrame = frame - offset;
                if (previewFrame < 0) continue;
                var exposure = track.EvaluateExposure(previewFrame);
                if (!exposure.HasContent
                    || exposure.SourceKeyframeFrame == currentKeyframe
                    || !seen.Add((layer, exposure.SourceKeyframeFrame)))
                {
                    continue;
                }
                candidates.Add((layer, exposure.SourceKeyframeFrame, 0.18f + 0.32f / offset, true));
            }

            for (var offset = 1; offset <= nextRange; offset++)
            {
                var previewFrame = frame + offset;
                if (previewFrame > lastFrame || previewFrame >= track.Duration) continue;
                var exposure = track.EvaluateExposure(previewFrame);
                if (!exposure.HasContent
                    || exposure.SourceKeyframeFrame == currentKeyframe
                    || !seen.Add((layer, exposure.SourceKeyframeFrame)))
                {
                    continue;
                }
                candidates.Add((layer, exposure.SourceKeyframeFrame, 0.18f + 0.32f / offset, false));
            }
        }

        var objectsByCel = new Dictionary<(int Layer, int Keyframe), List<int>>(candidates.Count);
        foreach (var candidate in candidates)
        {
            objectsByCel.TryAdd((candidate.Layer, candidate.Keyframe), []);
        }

        for (var objectIndex = 0; objectIndex < ObjectCount; objectIndex++)
        {
            if (objectsByCel.TryGetValue((ObjectLayer[objectIndex], ObjectKeyframeFrame[objectIndex]), out var objects))
            {
                objects.Add(objectIndex);
            }
        }

        var populated = new List<((int Layer, int Keyframe, float Opacity, bool IsPrevious) Candidate, List<int> Objects)>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var objects = objectsByCel[(candidate.Layer, candidate.Keyframe)];
            if (objects.Count > 0) populated.Add((candidate, objects));
        }

        destination.CreateEmpty(Math.Max(1, populated.Count));
        if (populated.Count == 0) return;

        var expectedCount = populated.Sum(item => item.Objects.Count);
        destination.EnsureObjectCapacity(expectedCount);
        using var batchUpdate = destination.Timeline.BeginBatchUpdate();
        for (var destinationLayer = 0; destinationLayer < populated.Count; destinationLayer++)
        {
            var item = populated[destinationLayer];
            destination.LayerNames[destinationLayer] = $"{LayerNames[item.Candidate.Layer]} onion";
            destination.LayerVisible[destinationLayer] = true;
            destination.LayerOpacity[destinationLayer] = item.Candidate.Opacity;
            destination.LayerBlendModes[destinationLayer] = LayerBlendMode.Normal;
            destination.LayerOutline[destinationLayer] = IsLayerEffectivelyOutlined(item.Candidate.Layer);
            destination.LayerColorArgb[destinationLayer] = item.Candidate.IsPrevious
                ? OnionSkinPreviousTintArgb
                : OnionSkinNextTintArgb;
            var track = destination.Timeline.FindTrackByTargetId(destination.LayerIds[destinationLayer]);
            if (track is not null) destination.Timeline.InsertKeyframe(track.Id, 0);

            foreach (var sourceObject in item.Objects)
            {
                var destinationObject = destination.ObjectCount++;
                destination.CopyObjectDataFrom(this, sourceObject, destinationObject);
                destination.ObjectLayer[destinationObject] = (ushort)destinationLayer;
                destination.ObjectKeyframeFrame[destinationObject] = 0;
                destination.ObjectOrder[destinationObject] = ++destination._nextObjectOrder;
                destination.ApplyOnionSkinAppearance(
                    destinationObject,
                    item.Candidate.Opacity,
                    item.Candidate.IsPrevious ? OnionSkinPreviousTintArgb : OnionSkinNextTintArgb);
                destination.VirtualAtomCount += destination.AtomCount[destinationObject];
            }
        }

        destination.SynchronizeAllKeyframeContentKinds();
        destination.RebuildGeometryIndex();
        destination.RebuildSummaries();
    }

    internal bool IsLayerEligibleForOnionSkin(int layer)
    {
        return (uint)layer < LayerCount
            && GetLayerKind(layer) != DrawingLayerKind.Folder
            && IsLayerEffectivelyVisible(layer)
            && !IsLayerEffectivelyLocked(layer);
    }

    private bool HasEligibleOnionSkinLayer()
    {
        for (var layer = 0; layer < LayerCount; layer++)
        {
            if (IsLayerEligibleForOnionSkin(layer)) return true;
        }

        return false;
    }

    internal void CombineOnionSkinPreviews(
        IReadOnlyList<(VectorScene Scene, float Opacity, bool? IsPrevious)> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var populated = sources.Where(source => source.Scene.ObjectCount > 0).ToArray();
        var layerCount = Math.Max(1, populated.Sum(source => source.Scene.LayerCount));
        CreateEmpty(layerCount);
        if (populated.Length == 0) return;

        EnsureObjectCapacity(populated.Sum(source => source.Scene.ObjectCount));
        var destinationLayerOffset = 0;
        foreach (var sourceItem in populated)
        {
            var source = sourceItem.Scene;
            for (var sourceLayer = 0; sourceLayer < source.LayerCount; sourceLayer++)
            {
                var destinationLayer = destinationLayerOffset + sourceLayer;
                LayerNames[destinationLayer] = source.LayerNames[sourceLayer];
                LayerKinds[destinationLayer] = source.LayerKinds[sourceLayer];
                LayerVisible[destinationLayer] = source.LayerVisible[sourceLayer];
                LayerOpacity[destinationLayer] = source.LayerOpacity[sourceLayer] * sourceItem.Opacity;
                LayerBlendModes[destinationLayer] = source.LayerBlendModes[sourceLayer];
                SetLayerSymbolFilters(destinationLayer, source.GetLayerSymbolFilters(sourceLayer));
                LayerOutline[destinationLayer] = source.LayerOutline[sourceLayer];
                LayerColorArgb[destinationLayer] = sourceItem.IsPrevious switch
                {
                    true => OnionSkinPreviousTintArgb,
                    false => OnionSkinNextTintArgb,
                    null => source.LayerColorArgb[sourceLayer]
                };
                var sourceParent = Array.IndexOf(source.LayerIds, source.LayerParentIds[sourceLayer]);
                if (sourceParent >= 0)
                {
                    LayerParentIds[destinationLayer] = LayerIds[destinationLayerOffset + sourceParent];
                }
                var sourceMask = Array.IndexOf(source.LayerIds, source.LayerMaskIds[sourceLayer]);
                if (sourceMask >= 0)
                {
                    LayerMaskIds[destinationLayer] = LayerIds[destinationLayerOffset + sourceMask];
                }
            }

            for (var sourceObject = 0; sourceObject < source.ObjectCount; sourceObject++)
            {
                var destinationObject = ObjectCount++;
                CopyObjectDataFrom(source, sourceObject, destinationObject);
                ObjectLayer[destinationObject] = (ushort)(destinationLayerOffset + source.ObjectLayer[sourceObject]);
                ObjectKeyframeFrame[destinationObject] = 0;
                ObjectOrder[destinationObject] = ++_nextObjectOrder;
                if (sourceItem.IsPrevious is { } isPrevious)
                {
                    ApplyOnionSkinAppearance(
                        destinationObject,
                        sourceItem.Opacity,
                        isPrevious ? OnionSkinPreviousTintArgb : OnionSkinNextTintArgb);
                }
                VirtualAtomCount += AtomCount[destinationObject];
            }

            destinationLayerOffset += source.LayerCount;
        }

        SynchronizeAllKeyframeContentKinds();
        RebuildGeometryIndex();
        RebuildSummaries();
    }

    private string[] AdditionalTimelineTargetIds()
    {
        var targets = _additionalTimelineTargets?.Invoke();
        if (targets is null || targets.Count == 0) return [];

        var layerIds = LayerIds.ToHashSet(StringComparer.Ordinal);
        return targets
            .Where(targetId => !string.IsNullOrWhiteSpace(targetId) && !layerIds.Contains(targetId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public bool IsObjectActive(int objectIndex, int frame)
    {
        if ((uint)objectIndex >= ObjectCount) return false;
        var layer = ObjectLayer[objectIndex];
        if ((uint)layer >= LayerCount || !IsLayerEffectivelyVisible(layer)) return false;

        var exposure = Timeline.EvaluateTargetExposure(LayerIds[layer], frame);
        return exposure.HasContent && ObjectKeyframeFrame[objectIndex] == exposure.SourceKeyframeFrame;
    }

    internal void PopulateActiveKeyframeFrames(int frame, int[] destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (destination.Length < LayerCount) throw new ArgumentException("The active-frame buffer is too small.", nameof(destination));

        if (_queryActiveFrame == frame && _queryActiveKeyframes.Length == LayerCount)
        {
            if (!ReferenceEquals(destination, _queryActiveKeyframes))
            {
                Array.Copy(_queryActiveKeyframes, destination, LayerCount);
            }

            return;
        }

        if (_queryActiveKeyframes.Length != LayerCount) Array.Resize(ref _queryActiveKeyframes, LayerCount);
        for (var layer = 0; layer < LayerCount; layer++)
        {
            if (GetLayerKind(layer) == DrawingLayerKind.Folder || !IsLayerEffectivelyVisible(layer))
            {
                _queryActiveKeyframes[layer] = int.MinValue;
                continue;
            }

            var exposure = Timeline.EvaluateTargetExposure(LayerIds[layer], frame);
            _queryActiveKeyframes[layer] = exposure.HasContent ? exposure.SourceKeyframeFrame : int.MinValue;
        }

        _queryActiveFrame = frame;
        if (!ReferenceEquals(destination, _queryActiveKeyframes))
        {
            Array.Copy(_queryActiveKeyframes, destination, LayerCount);
        }
    }

    public bool InsertTimelineFrame(int layer, int frame, int count = 1)
    {
        var track = TimelineTrackForLayer(layer);
        if (track is null || count <= 0) return false;
        frame = Math.Max(0, frame);
        if (frame > track.Duration) Timeline.SetTrackDuration(track.Id, frame);
        if (!Timeline.InsertFrame(track.Id, frame, count)) return false;

        for (var i = 0; i < ObjectCount; i++)
        {
            if (ObjectLayer[i] == layer && ObjectKeyframeFrame[i] > frame)
            {
                ObjectKeyframeFrame[i] += count;
            }
        }

        RefreshLegacyExposureBounds(layer);
        return true;
    }

    public bool RemoveTimelineFrame(int layer, int frame, int count = 1)
    {
        var track = TimelineTrackForLayer(layer);
        if (track is null || count <= 0 || frame < 0 || frame >= track.Duration || track.Duration <= 1) return false;

        var removeCount = Math.Min(Math.Min(count, track.Duration - frame), track.Duration - 1);
        var removeEnd = frame + removeCount;
        var continuation = removeEnd < track.Duration
            ? track.EvaluateExposure(removeEnd)
            : TimelineExposure.None(removeEnd);
        var preservedSourceFrame = continuation.SourceKeyframeFrame >= frame
            && continuation.SourceKeyframeFrame < removeEnd
            ? continuation.SourceKeyframeFrame
            : -1;
        if (removeCount <= 0 || !Timeline.RemoveFrame(track.Id, frame, removeCount)) return false;

        var removed = new List<int>();
        for (var i = 0; i < ObjectCount; i++)
        {
            if (ObjectLayer[i] != layer) continue;
            var sourceFrame = ObjectKeyframeFrame[i];
            if (sourceFrame == preservedSourceFrame) ObjectKeyframeFrame[i] = frame;
            else if (sourceFrame >= frame && sourceFrame < removeEnd) removed.Add(i);
            else if (sourceFrame >= removeEnd) ObjectKeyframeFrame[i] -= removeCount;
        }

        if (removed.Count > 0) RemoveObjects(removed);
        RefreshLegacyExposureBounds(layer);
        return true;
    }

    public bool InsertTimelineKeyframe(int layer, int frame)
    {
        var track = TimelineTrackForLayer(layer);
        if (track is null || frame < 0) return false;

        var current = frame < track.Duration
            ? track.EvaluateExposure(frame)
            : track.EvaluateExposure(track.Duration - 1);
        if (frame < track.Duration
            && current.IsKeyframe
            && current.SourceKind == TimelineKeyframeKind.Populated)
        {
            return false;
        }
        var sourceObjects = current.HasContent
            ? Enumerable.Range(0, ObjectCount)
                .Where(index => ObjectLayer[index] == layer && ObjectKeyframeFrame[index] == current.SourceKeyframeFrame)
                .ToArray()
            : [];
        var carriesExternalContent = current.HasContent
            && HasExternalLayerKeyframeContent(layer, current.SourceKeyframeFrame);

        if (frame >= track.Duration) Timeline.SetTrackDuration(track.Id, frame + 1);

        var inserted = sourceObjects.Length > 0 || carriesExternalContent
            ? Timeline.InsertKeyframe(track.Id, frame)
            : Timeline.InsertBlankKeyframe(track.Id, frame);
        if (!inserted) return false;
        CloneObjectsIntoKeyframe(sourceObjects, frame);
        RefreshLegacyExposureBounds(layer);
        return true;
    }

    public bool MaterializeAutoKeyframeInPlace(int layer, int frame)
    {
        using var batchUpdate = Timeline.BeginBatchUpdate();
        var track = TimelineTrackForLayer(layer);
        if (track is null || frame < 0 || frame == int.MaxValue) return false;

        var current = frame < track.Duration
            ? track.EvaluateExposure(frame)
            : track.EvaluateExposure(track.Duration - 1);
        if (frame < track.Duration && current.IsKeyframe) return false;

        var sourceObjects = current.HasContent
            ? Enumerable.Range(0, ObjectCount)
                .Where(index => ObjectLayer[index] == layer && ObjectKeyframeFrame[index] == current.SourceKeyframeFrame)
                .ToArray()
            : [];
        var carriesExternalContent = current.HasContent
            && HasExternalLayerKeyframeContent(layer, current.SourceKeyframeFrame);

        if (frame >= track.Duration) Timeline.SetTrackDuration(track.Id, frame + 1);
        var inserted = sourceObjects.Length > 0 || carriesExternalContent
            ? Timeline.InsertKeyframe(track.Id, frame)
            : Timeline.InsertBlankKeyframe(track.Id, frame);
        if (!inserted) return false;

        if (sourceObjects.Length > 0)
        {
            CloneObjectsIntoKeyframe(sourceObjects, current.SourceKeyframeFrame);
            foreach (var source in sourceObjects) ObjectKeyframeFrame[source] = frame;
        }

        RefreshLegacyExposureBounds(layer);
        return true;
    }

    public bool InsertTimelineBlankKeyframe(int layer, int frame)
    {
        var track = TimelineTrackForLayer(layer);
        if (track is null || frame < 0) return false;
        if (frame >= track.Duration) Timeline.SetTrackDuration(track.Id, frame + 1);
        var existing = track.EvaluateExposure(frame);
        if (existing.IsKeyframe && existing.SourceKind == TimelineKeyframeKind.Blank) return false;
        if (!Timeline.InsertBlankKeyframe(track.Id, frame)) return false;

        RemoveObjectsForKeyframe(layer, frame);
        RefreshLegacyExposureBounds(layer);
        return true;
    }

    public bool ClearTimelineKeyframe(int layer, int frame)
    {
        var track = TimelineTrackForLayer(layer);
        if (track is null || frame < 0) return false;
        var existing = track.EvaluateExposure(frame);
        if (!existing.IsKeyframe || !Timeline.ClearKeyframe(track.Id, frame)) return false;

        RemoveObjectsForKeyframe(layer, frame);
        RefreshLegacyExposureBounds(layer);
        return true;
    }

    private AnimationTimelineTrack? TimelineTrackForLayer(int layer)
    {
        if ((uint)layer >= LayerCount) return null;
        SynchronizeTimelineTracks();
        return Timeline.FindTrackByTargetId(LayerIds[layer]);
    }

    private int EnsureWritableKeyframe(int layer, int frame)
    {
        var track = TimelineTrackForLayer(layer);
        if (track is null) return 0;

        return EnsureWritableKeyframe(track, layer, frame);
    }

    private int EnsureWritableKeyframe(AnimationTimelineTrack track, int layer, int frame)
    {

        frame = Math.Max(0, frame);
        if (frame >= track.Duration)
        {
            Timeline.SetTrackDuration(track.Id, frame + 1);
            Timeline.InsertKeyframe(track.Id, frame);
            RefreshLegacyExposureBounds(layer);
            return frame;
        }

        var exposure = track.EvaluateExposure(frame);
        if (exposure.HasContent) return exposure.SourceKeyframeFrame;

        var keyframeFrame = exposure.SourceKeyframeFrame >= 0 ? exposure.SourceKeyframeFrame : frame;
        Timeline.InsertKeyframe(track.Id, keyframeFrame);
        RefreshLegacyExposureBounds(layer);
        return keyframeFrame;
    }

    private void CloneObjectsIntoKeyframe(IReadOnlyList<int> sourceObjects, int keyframeFrame)
    {
        if (sourceObjects.Count == 0) return;

        var sourceCount = ObjectCount;
        var firstClone = ObjectCount;
        EnsureObjectCapacity(ObjectCount + sourceObjects.Count);
        foreach (var source in sourceObjects)
        {
            if ((uint)source >= sourceCount) continue;
            var destination = ObjectCount++;
            CopyObjectData(source, destination);
            ObjectKeyframeFrame[destination] = keyframeFrame;
            VirtualAtomCount += AtomCount[destination];
        }

        CompleteIncrementalObjectAppends(firstClone, ObjectCount - firstClone);
    }

    private void CloneObjectsFrom(
        VectorScene source,
        IReadOnlyList<int> sourceObjects,
        int destinationLayer,
        int keyframeFrame)
    {
        EnsureObjectCapacity(ObjectCount + sourceObjects.Count);
        foreach (var sourceObject in sourceObjects)
        {
            if ((uint)sourceObject >= source.ObjectCount) continue;
            var destination = ObjectCount++;
            CopyObjectDataFrom(source, sourceObject, destination);
            ObjectLayer[destination] = (ushort)destinationLayer;
            ObjectKeyframeFrame[destination] = keyframeFrame;
            ObjectOrder[destination] = ++_nextObjectOrder;
            VirtualAtomCount += AtomCount[destination];
        }

        RebuildGeometryIndex();
        RebuildSummaries();
    }

    private void RemoveObjectsForKeyframe(int layer, int keyframeFrame)
    {
        var removed = Enumerable.Range(0, ObjectCount)
            .Where(index => ObjectLayer[index] == layer && ObjectKeyframeFrame[index] == keyframeFrame)
            .ToArray();
        if (removed.Length > 0) RemoveObjects(removed);
    }

    private void SynchronizeAllKeyframeContentKinds()
    {
        var occupied = new HashSet<(int Layer, int Frame)>();
        for (var index = 0; index < ObjectCount; index++)
        {
            occupied.Add((ObjectLayer[index], ObjectKeyframeFrame[index]));
        }

        _synchronizingKeyframeContent = true;
        try
        {
            for (var layer = 0; layer < LayerCount; layer++)
            {
                var track = Timeline.FindTrackByTargetId(LayerIds[layer]);
                if (track is null) continue;
                foreach (var keyframe in track.Keyframes.ToArray())
                {
                    var hasContent = occupied.Contains((layer, keyframe.Frame))
                        || (keyframe.HasContent && HasExternalLayerKeyframeContent(layer, keyframe.Frame));
                    if (keyframe.HasContent == hasContent) continue;
                    if (hasContent) Timeline.InsertKeyframe(track.Id, keyframe.Frame);
                    else Timeline.InsertBlankKeyframe(track.Id, keyframe.Frame);
                }

                RefreshLegacyExposureBounds(layer);
            }
        }
        finally
        {
            _synchronizingKeyframeContent = false;
        }
    }

    private void SynchronizeKeyframeContentKind(int layer, int keyframeFrame)
    {
        if (_synchronizingKeyframeContent || (uint)layer >= LayerCount) return;
        var track = Timeline.FindTrackByTargetId(LayerIds[layer]);
        var keyframe = track?.Keyframes
            .Where(item => item.Frame == keyframeFrame)
            .Select(item => (TimelineKeyframe?)item)
            .FirstOrDefault();
        if (track is null || keyframe is null) return;

        var hasContent = Enumerable.Range(0, ObjectCount)
                .Any(index => ObjectLayer[index] == layer && ObjectKeyframeFrame[index] == keyframeFrame)
            || (keyframe.Value.HasContent && HasExternalLayerKeyframeContent(layer, keyframeFrame));
        if (keyframe.Value.HasContent == hasContent) return;

        _synchronizingKeyframeContent = true;
        try
        {
            if (hasContent) Timeline.InsertKeyframe(track.Id, keyframeFrame);
            else Timeline.InsertBlankKeyframe(track.Id, keyframeFrame);
            RefreshLegacyExposureBounds(layer);
        }
        finally
        {
            _synchronizingKeyframeContent = false;
        }
    }

    private bool HasExternalLayerKeyframeContent(int layer, int keyframeFrame)
    {
        return (uint)layer < LayerCount
            && _externalLayerKeyframeContent?.Invoke(LayerIds[layer], keyframeFrame) == true;
    }

    private void RefreshLegacyExposureBounds(int layer)
    {
        if ((uint)layer >= LayerCount || layer >= LayerStart.Length || layer >= LayerEnd.Length) return;
        var track = Timeline.FindTrackByTargetId(LayerIds[layer]);
        if (track is null) return;

        var populated = track.Keyframes.Where(keyframe => keyframe.HasContent).ToArray();
        if (populated.Length == 0)
        {
            LayerStart[layer] = 0;
            LayerEnd[layer] = -1;
            return;
        }

        LayerStart[layer] = populated[0].Frame;
        LayerEnd[layer] = populated
            .Select(keyframe => track.EvaluateExposure(keyframe.Frame).EndFrame)
            .Max();
    }

    public void SoloLayer(int layer)
    {
        Array.Fill(LayerVisible, false);
        if (layer >= 0 && layer < LayerCount) LayerVisible[layer] = true;
        InvalidateQueryActiveKeyframes();
    }

    public void ShowAllLayers()
    {
        Array.Fill(LayerVisible, true);
        InvalidateQueryActiveKeyframes();
    }

    public bool SetLayerVisible(int layer, bool visible)
    {
        if ((uint)layer >= LayerCount || LayerVisible[layer] == visible) return false;
        LayerVisible[layer] = visible;
        InvalidateQueryActiveKeyframes();
        return true;
    }

    public void ToggleLayer(int layer)
    {
        if (layer < 0 || layer >= LayerCount) return;
        LayerVisible[layer] = !LayerVisible[layer];
        InvalidateQueryActiveKeyframes();
    }

    private void InitializeTimelineFromLayerExposure(int defaultDuration = AnimationTimeline.DefaultDuration)
    {
        defaultDuration = Math.Max(1, defaultDuration);
        using var batchUpdate = Timeline.BeginBatchUpdate();
        var additionalTargetIds = AdditionalTimelineTargetIds();
        var additionalTargetSet = additionalTargetIds.ToHashSet(StringComparer.Ordinal);
        var previousTimelineSnapshot = Timeline.CreateSnapshot();
        var previousTracksByTargetId = previousTimelineSnapshot.Tracks
            .ToDictionary(track => track.TargetId, track => track, StringComparer.Ordinal);
        var preservedAdditionalTracks = previousTimelineSnapshot.Tracks
            .Where(track => additionalTargetSet.Contains(track.TargetId))
            .ToDictionary(track => track.TargetId, track => track, StringComparer.Ordinal);

        Timeline.Clear();
        Timeline.SynchronizeTracks(LayerIds, defaultDuration, populateNewTracks: false);
        var occupied = new HashSet<(int Layer, int Frame)>();
        for (var index = 0; index < ObjectCount; index++)
        {
            occupied.Add((ObjectLayer[index], ObjectKeyframeFrame[index]));
        }

        for (var layer = 0; layer < LayerCount; layer++)
        {
            var track = Timeline.FindTrackByTargetId(LayerIds[layer]);
            if (track is null) continue;

            var startFrame = layer < LayerStart.Length
                ? Math.Clamp(LayerStart[layer], 0, track.Duration - 1)
                : 0;
            var endFrame = layer < LayerEnd.Length
                ? Math.Clamp(LayerEnd[layer], startFrame, track.Duration - 1)
                : track.Duration - 1;
            var hasContent = occupied.Contains((layer, startFrame));
            if (startFrame > 0) Timeline.InsertBlankKeyframe(track.Id, 0);
            if (hasContent) Timeline.InsertKeyframe(track.Id, startFrame);
            else if (startFrame == 0) Timeline.InsertBlankKeyframe(track.Id, startFrame);
            if (endFrame + 1 < track.Duration) Timeline.InsertBlankKeyframe(track.Id, endFrame + 1);
            RefreshLegacyExposureBounds(layer);
        }

        var layerTracks = Timeline.CreateSnapshot().Tracks
            .Select(track =>
            {
                if (!previousTracksByTargetId.TryGetValue(track.TargetId, out var previous)) return track;
                return new AnimationTimelineTrackSnapshot
                {
                    Id = track.Id,
                    TargetId = track.TargetId,
                    TabGroupId = previous.TabGroupId,
                    IsCollisionTerrain = previous.IsCollisionTerrain,
                    Duration = track.Duration,
                    Keyframes = track.Keyframes,
                    Tweens = track.Tweens
                };
            })
            .ToArray();
        var additionalTracks = additionalTargetIds
            .Select(targetId => preservedAdditionalTracks.GetValueOrDefault(targetId) ?? new AnimationTimelineTrackSnapshot
            {
                Id = Guid.NewGuid().ToString("N"),
                TargetId = targetId,
                Duration = defaultDuration,
                Keyframes = [new TimelineKeyframe(0, TimelineKeyframeKind.Populated)],
                Tweens = []
            });
        Timeline.RestoreSnapshot(new AnimationTimelineSnapshot
        {
            Tracks = layerTracks.Concat(additionalTracks).ToArray(),
            TabGroups = previousTimelineSnapshot.TabGroups,
            ActiveTabGroupId = previousTimelineSnapshot.ActiveTabGroupId
        });
        SynchronizeTimelineTracks();
    }

    private static string[] CreateStableIds(int count, IEnumerable<string>? reservedIds = null)
    {
        var result = new string[Math.Max(0, count)];
        var used = reservedIds is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : reservedIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < result.Length; i++)
        {
            string id;
            do
            {
                id = Guid.NewGuid().ToString("N");
            }
            while (!used.Add(id));
            result[i] = id;
        }

        return result;
    }

    private static string[] NormalizeStableIds(IReadOnlyList<string>? source, int count)
    {
        var result = new string[Math.Max(0, count)];
        var used = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < result.Length; i++)
        {
            var candidate = source is not null && i < source.Count ? source[i] : "";
            if (!string.IsNullOrWhiteSpace(candidate) && used.Add(candidate))
            {
                result[i] = candidate;
                continue;
            }

            string id;
            do
            {
                id = Guid.NewGuid().ToString("N");
            }
            while (!used.Add(id));

            result[i] = id;
        }

        return result;
    }

    private static DrawingLayerKind[] NormalizeLayerKinds(IReadOnlyList<DrawingLayerKind>? source, int count)
    {
        var result = new DrawingLayerKind[Math.Max(0, count)];
        if (source is null) return result;
        for (var index = 0; index < result.Length && index < source.Count; index++)
        {
            result[index] = Enum.IsDefined(source[index]) ? source[index] : DrawingLayerKind.Drawing;
        }

        return result;
    }

    private static string[] NormalizeLayerParentIds(
        IReadOnlyList<string>? source,
        IReadOnlyList<string> layerIds,
        IReadOnlyList<DrawingLayerKind> layerKinds)
    {
        var result = Enumerable.Repeat(string.Empty, layerIds.Count).ToArray();
        if (source is null) return result;
        var indexes = layerIds
            .Select((id, index) => (id, index))
            .ToDictionary(item => item.id, item => item.index, StringComparer.Ordinal);
        for (var layer = 0; layer < result.Length && layer < source.Count; layer++)
        {
            var parentId = source[layer];
            if (string.IsNullOrWhiteSpace(parentId)
                || !indexes.TryGetValue(parentId, out var parent)
                || parent == layer
                || layerKinds[parent] != DrawingLayerKind.Folder)
            {
                continue;
            }

            var visited = new HashSet<int> { layer };
            var current = parent;
            var valid = true;
            while (true)
            {
                if (!visited.Add(current))
                {
                    valid = false;
                    break;
                }

                if (current >= source.Count || string.IsNullOrWhiteSpace(source[current])) break;
                if (!indexes.TryGetValue(source[current], out current) || layerKinds[current] != DrawingLayerKind.Folder)
                {
                    valid = false;
                    break;
                }
            }

            if (valid) result[layer] = parentId;
        }

        return result;
    }

    private static string[] NormalizeLayerMaskIds(
        IReadOnlyList<string>? source,
        IReadOnlyList<string> layerIds,
        IReadOnlyList<DrawingLayerKind> layerKinds)
    {
        var result = Enumerable.Repeat(string.Empty, layerIds.Count).ToArray();
        if (source is null) return result;
        var claimedMaskIds = new HashSet<string>(StringComparer.Ordinal);
        var indexes = layerIds
            .Select((id, index) => (id, index))
            .ToDictionary(item => item.id, item => item.index, StringComparer.Ordinal);
        for (var layer = 0; layer < result.Length && layer < source.Count; layer++)
        {
            var maskId = source[layer];
            if (layerKinds[layer] != DrawingLayerKind.Drawing
                || string.IsNullOrWhiteSpace(maskId)
                || !indexes.TryGetValue(maskId, out var maskLayer)
                || layerKinds[maskLayer] != DrawingLayerKind.Mask)
            {
                continue;
            }

            if (!claimedMaskIds.Add(maskId)) continue;
            result[layer] = maskId;
        }

        return result;
    }

    private static void NormalizeMaskedLayerParents(
        string[] layerParentIds,
        IReadOnlyList<string> layerIds,
        IReadOnlyList<string> layerMaskIds)
    {
        var indexes = layerIds
            .Select((id, index) => (id, index))
            .ToDictionary(item => item.id, item => item.index, StringComparer.Ordinal);
        for (var contentLayer = 0; contentLayer < layerMaskIds.Count; contentLayer++)
        {
            var maskId = layerMaskIds[contentLayer];
            if (string.IsNullOrWhiteSpace(maskId)) continue;
            if (!indexes.TryGetValue(maskId, out var maskLayer) || maskLayer >= layerParentIds.Length) continue;
            layerParentIds[contentLayer] = layerParentIds[maskLayer];
        }
    }

    private static Color DefaultLayerColor(int index)
    {
        var colors = new[]
        {
            Color.FromArgb(79, 195, 247),
            Color.FromArgb(255, 183, 77),
            Color.FromArgb(129, 199, 132),
            Color.FromArgb(244, 143, 177),
            Color.FromArgb(179, 157, 219),
            Color.FromArgb(128, 203, 196),
            Color.FromArgb(255, 138, 128),
            Color.FromArgb(255, 241, 118)
        };
        return colors[Math.Abs(index) % colors.Length];
    }

    private static int[] NormalizeLayerColors(IReadOnlyList<int>? source, int count)
    {
        var result = new int[Math.Max(0, count)];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = source is not null && index < source.Count
                ? source[index]
                : DefaultLayerColor(index).ToArgb();
        }

        return result;
    }

    private static LayerBlendMode[] NormalizeLayerBlendModes(IReadOnlyList<LayerBlendMode>? source, int count)
    {
        var result = new LayerBlendMode[Math.Max(0, count)];
        if (source is null) return result;
        for (var index = 0; index < result.Length && index < source.Count; index++)
        {
            result[index] = Enum.IsDefined(source[index]) ? source[index] : LayerBlendMode.Normal;
        }

        return result;
    }

    private static bool[] NormalizeLayerOutline(IReadOnlyList<bool>? source, int count)
    {
        var result = new bool[Math.Max(0, count)];
        if (source is null) return result;
        for (var index = 0; index < result.Length && index < source.Count; index++) result[index] = source[index];
        return result;
    }

    private static bool[] NormalizeLayerLocked(IReadOnlyList<bool>? source, int count)
    {
        var result = new bool[Math.Max(0, count)];
        if (source is null) return result;
        for (var index = 0; index < result.Length && index < source.Count; index++) result[index] = source[index];
        return result;
    }

    private static int NormalizeOnionSkinFrames(int? source, int fallback)
    {
        return Math.Clamp(source ?? fallback, 0, MaximumOnionSkinFrames);
    }

    private static T[] ReorderLayers<T>(IReadOnlyList<T> source, IReadOnlyList<int> destinationBySource)
    {
        var result = new T[destinationBySource.Count];
        for (var sourceIndex = 0; sourceIndex < result.Length; sourceIndex++)
        {
            result[destinationBySource[sourceIndex]] = source[sourceIndex];
        }

        return result;
    }

    private static Color ColorFromHsl(double h, double s, double light)
    {
        var a = s * Math.Min(light, 1 - light);
        int F(double n)
        {
            var k = (n + h * 12) % 12;
            var channel = light - a * Math.Max(-1, Math.Min(k - 3, Math.Min(9 - k, 1)));
            return (int)Math.Clamp(channel * 255, 0, 255);
        }

        return Color.FromArgb(F(0), F(8), F(4));
    }

}
