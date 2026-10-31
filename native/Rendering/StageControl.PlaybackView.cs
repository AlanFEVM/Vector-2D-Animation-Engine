namespace VectorAnimationEngine;

internal sealed partial class StageControl
{
    private EnterPlaybackViewSettings? _enterPlaybackViewSettings;

    internal void SetEnterPlaybackViewSettings(EnterPlaybackViewSettings? settings)
    {
        _enterPlaybackViewSettings = settings?.Normalized();
        ClearGdiBaseFrameCache();
        Invalidate();
    }

    internal void ApplyEnterPlaybackView(StageViewState original, EnterPlaybackViewSettings settings)
    {
        if (UsesReferenceProjection || CanPreviewPlanarShot) return;
        if (settings.Mode == EnterPlaybackFitMode.KeepCurrent)
        {
            RestoreViewState(original);
            return;
        }
        var source = new SizeF(VectorUnits.ToPixels(CanvasWorldWidth), VectorUnits.ToPixels(CanvasWorldHeight));
        var bounds = settings.ResolveBounds(ClientSize, source, DeviceDpi / 96f);
        if (bounds.IsEmpty) return;
        RestoreViewState(original with { CameraX = 0, CameraY = 0, Zoom = bounds.Width / source.Width });
    }

    private RectangleF ResolveEnterPlaybackShotBounds()
    {
        var corners = _shotFramingSettings.ResolveWorldFrameCorners(CanvasWorldWidth, CanvasWorldHeight);
        var source = new SizeF(VectorUnits.ToPixels(System.Numerics.Vector3.Distance(corners[0], corners[1])),
            VectorUnits.ToPixels(System.Numerics.Vector3.Distance(corners[0], corners[3])));
        return _enterPlaybackViewSettings!.ResolveBounds(ClientSize, source, DeviceDpi / 96f);
    }
}
