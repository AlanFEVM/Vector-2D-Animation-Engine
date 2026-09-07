namespace VectorAnimationEngine;

internal sealed partial class StageControl
{
    private const float FocusViewPaddingRatio = 0.05f;
    private const float FocusViewMinimumPaddingPixels = 16f;
    private const float FocusViewMinimumZoom = 0.02f;
    private const float FocusViewMaximumZoom = 1f;

    internal bool FocusSceneObjects(
        IReadOnlyCollection<int>? objectIndices,
        ReferenceCameraMotion motion = ReferenceCameraMotion.Animated)
    {
        if (objectIndices is not { Count: > 0 }) return false;
        var targets = objectIndices
            .Where(index =>
            {
                if ((uint)index >= Scene.ObjectCount || !Scene.IsObjectActive(index, Frame)) return false;
                return Scene.ShouldRenderLayerContent(Scene.ObjectLayer[index]);
            })
            .Distinct()
            .Order()
            .ToArray();
        if (targets.Length == 0) return false;

        if (UsesReferenceProjection)
        {
            return TryGetReferenceFocusPoints(targets, out var scenePoints)
                && FocusReferenceCamera(scenePoints, motion);
        }

        return TryGetFlatFocusBounds(targets, out var bounds) && FocusWorldBounds(bounds);
    }

    internal bool FocusWorldBounds(RectangleF bounds)
    {
        if (ClientSize.Width <= 0
            || ClientSize.Height <= 0
            || !FiniteFocusBounds(bounds))
        {
            return false;
        }

        var centerX = bounds.Left + bounds.Width * 0.5f;
        var centerY = bounds.Top + bounds.Height * 0.5f;
        if (!float.IsFinite(centerX) || !float.IsFinite(centerY)) return false;

        var padding = Math.Max(
            FocusViewMinimumPaddingPixels,
            Math.Min(ClientSize.Width, ClientSize.Height) * FocusViewPaddingRatio);
        var availableWidth = Math.Max(1f, ClientSize.Width - padding * 2);
        var availableHeight = Math.Max(1f, ClientSize.Height - padding * 2);
        var worldWidth = Math.Max(Math.Abs(bounds.Width), VectorUnits.FromPixels(1));
        var worldHeight = Math.Max(Math.Abs(bounds.Height), VectorUnits.FromPixels(1));
        var targetZoom = Math.Clamp(
            Math.Min(
                availableWidth / Math.Max(0.000001f, Math.Abs(VectorUnits.ToPixels(worldWidth))),
                availableHeight / Math.Max(0.000001f, Math.Abs(VectorUnits.ToPixels(worldHeight)))),
            FocusViewMinimumZoom,
            FocusViewMaximumZoom);
        if (!float.IsFinite(targetZoom) || targetZoom <= 0) return false;

        CompleteReferenceCameraTransitionForDirectInput();
        _pendingVisibleWorldWidth = null;
        EndZoomLodPreview(invalidate: false);
        CameraX = centerX;
        CameraY = centerY;
        Zoom = targetZoom;
        RefreshBrushTipCursorScale();
        Invalidate();
        RaiseViewChanged();
        return true;
    }

    private bool TryGetFlatFocusBounds(IReadOnlyList<int> objectIndices, out RectangleF bounds)
    {
        bounds = RectangleF.Empty;
        var found = false;
        foreach (var objectIndex in objectIndices)
        {
            if ((uint)objectIndex >= Scene.ObjectCount) continue;
            var objectBounds = Scene.GetObjectWorldBounds(objectIndex);
            if (!FiniteFocusBounds(objectBounds)) continue;
            bounds = found ? RectangleF.Union(bounds, objectBounds) : objectBounds;
            found = true;
        }

        return found && FiniteFocusBounds(bounds);
    }

    private static bool FiniteFocusBounds(RectangleF bounds)
    {
        return float.IsFinite(bounds.X)
            && float.IsFinite(bounds.Y)
            && float.IsFinite(bounds.Width)
            && float.IsFinite(bounds.Height)
            && bounds.Width >= 0
            && bounds.Height >= 0;
    }
}
