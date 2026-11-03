using System.Numerics;

namespace VectorAnimationEngine;

internal sealed partial class StageControl
{
    internal HashSet<int>? InstanceMovePreviewObjects { get; private set; }
    internal PointF InstanceMovePreviewOffset { get; private set; }
    internal long InstanceMovePreviewEpoch { get; private set; }
    internal bool InstanceMovePreviewRasterizing { get; set; }
    internal bool InstanceMovePreviewAwaitingPresent { get; private set; }
    private VectorScene? _instanceMoveSource;
    private long _instanceMoveGeometryRevision;
    private long _instanceMoveSummaryRevision;
    private int _instanceMoveFrame;
    internal bool InstanceMovePreviewContentValid => ReferenceEquals(Scene, _instanceMoveSource)
        && Scene.GeometryRevision == _instanceMoveGeometryRevision && Scene.SummaryRevision == _instanceMoveSummaryRevision
        && Frame == _instanceMoveFrame;

    internal bool BeginInstanceMovePreview(IReadOnlyCollection<int> indices)
    {
        if (indices.Count == 0 || !Direct2DStageRenderer.CanDrawReferenceColorGroups(this)
            || !LastFrameUsedDirect2D && !(_gdiBaseFrameBorrowed && _direct2DRenderer.ExplicitGpuDeviceActive)
            || !NavigationPreviewEnabled || !CanMoveFilteredGroups(indices)) return false;
        ReleasePreparedWorkspaceBaseBitmap();
        InstanceMovePreviewAwaitingPresent = true;
        InstanceMovePreviewObjects = indices.ToHashSet();
        _instanceMoveSource = Scene;
        _instanceMoveGeometryRevision = Scene.GeometryRevision;
        _instanceMoveSummaryRevision = Scene.SummaryRevision;
        _instanceMoveFrame = Frame;
        InstanceMovePreviewOffset = default;
        InstanceMovePreviewEpoch++;
        // Starting a display-only drag must not discard the already prepared
        // projection/contour plan. The preview epoch invalidates its own stream.
        RequestStageFrame(basePresentationChanged: false);
        return true;
    }

    private bool CanMoveFilteredGroups(IReadOnlyCollection<int> indices)
    {
        if (!Scene.HasSymbolFilters) return true;
        var selected = indices.ToHashSet();
        var ids = Enumerable.Range(0, Scene.LayerCount).ToDictionary(i => Scene.LayerIds[i]);
        var moving = new bool[Scene.LayerCount];
        var stationary = new bool[Scene.LayerCount];
        var parents = new int[Scene.LayerCount];
        var childCounts = new int[Scene.LayerCount];
        for (var layer = 0; layer < parents.Length; layer++)
        {
            parents[layer] = ids.TryGetValue(Scene.LayerParentIds[layer], out var parent) ? parent : -1;
            if (parents[layer] >= 0) childCounts[parents[layer]]++;
        }
        foreach (var index in Scene.GetActiveObjectIndices(Frame))
        {
            var layer = Scene.ObjectLayer[index];
            if (selected.Contains(index)) moving[layer] = true;
            else stationary[layer] = true;
        }
        // Fold each layer into its parent once. Walking every object's entire
        // ancestry repeatedly makes selecting a large imported symbol costly.
        var ready = new Queue<int>();
        for (var layer = 0; layer < childCounts.Length; layer++)
            if (childCounts[layer] == 0) ready.Enqueue(layer);
        var visited = 0;
        while (ready.TryDequeue(out var layer))
        {
            visited++;
            if (moving[layer] && stationary[layer])
            {
                var filters = Scene.GetLayerSymbolFilters(layer);
                var color = filters.ColorTransform;
                if ((filters with { ColorTransform = default }).HasEnabled
                    || color.Enabled && (color.RedOffset != 0 || color.GreenOffset != 0 || color.BlueOffset != 0
                        || color.RedMultiplier < 0 || color.GreenMultiplier < 0 || color.BlueMultiplier < 0))
                    return false;
            }
            if (parents[layer] >= 0)
            {
                var parent = parents[layer];
                moving[parent] |= moving[layer];
                stationary[parent] |= stationary[layer];
                if (--childCounts[parent] == 0) ready.Enqueue(parent);
            }
        }
        return visited == Scene.LayerCount;
    }

    internal void UpdateInstanceMovePreview(Vector3 delta)
    {
        if (InstanceMovePreviewObjects is null) return;
        if (!TryProjectScenePosition(Vector3.Zero, out var origin, out _)
            || !TryProjectScenePosition(delta, out var translated, out _)) return;
        InstanceMovePreviewOffset = new(translated.X - origin.X, translated.Y - origin.Y);
        RequestStageFrame(basePresentationChanged: false);
    }

    internal void EndInstanceMovePreview()
    {
        if (InstanceMovePreviewObjects is null) return;
        var preserveContent = InstanceMovePreviewContentValid;
        InstanceMovePreviewObjects = null;
        InstanceMovePreviewAwaitingPresent = false;
        _instanceMoveSource = null;
        InstanceMovePreviewOffset = default;
        InstanceMovePreviewEpoch++;
        RequestStageFrame(basePresentationChanged: true,
            preserveReferenceNavigation: preserveContent,
            preserveReference3DRenderPlanCache: preserveContent);
    }
}
