namespace VectorAnimationEngine;

internal sealed partial class StageControl
{
    private bool _navigationPreviewEnabled = true;
    private bool _referenceNavigationPreviewActive;
    private long _referenceNavigationSettleDeadline;
    private long _planarZoomLodSettleDeadline;
    private long _planarPanHighlightSettleDeadline;
    private bool _planarPanHighlightPreviewActive;
    private long _lastPlanarNavigationInput;
    private long _lastSlowBlockNavigationLog;
    internal bool ReferenceNavigationPreviewActive => _referenceNavigationPreviewActive;
    private bool PlanarNavigationHighlightPreviewActive => ZoomLodPreviewActive || _planarPanHighlightPreviewActive;

    public bool NavigationPreviewEnabled
    {
        get => _navigationPreviewEnabled;
        set
        {
            if (_navigationPreviewEnabled == value) return;
            _navigationPreviewEnabled = value;
            _referenceNavigationPreviewActive = false;
            _direct2DRenderer.ClearReferenceNavigationPreview();
            RequestStageFrame(basePresentationChanged: true);
        }
    }

    private void PulseReferenceNavigationPreview()
    {
        if (!NavigationPreviewEnabled || !CanRetainReferenceNavigation) return;
        _referenceNavigationPreviewActive = true;
        _referenceNavigationSettleDeadline = PulseZoomLodIdleTimer();
    }

    private void PulsePlanarZoomLodPreview() => _planarZoomLodSettleDeadline = PulseZoomLodIdleTimer();

    private void PulsePlanarPanHighlightPreview()
    {
        if (!NavigationPreviewEnabled || RendersReferenceProjection
            || !_planarPanHighlightPreviewActive && !LayerContentHighlightVisible) return;
        if (!_planarPanHighlightPreviewActive && !HasDenseBitmapLayerHighlight()) return;
        _planarPanHighlightPreviewActive = true;
        _planarPanHighlightSettleDeadline = PulseZoomLodIdleTimer();
    }

    private void ReportSlowBlockNavigation(long presentedAt)
    {
        if (_lastPlanarNavigationInput == 0
            || ElapsedMilliseconds(_lastPlanarNavigationInput, presentedAt) > 250
            || LastFrameRenderMilliseconds < 1000d / 130
            || _lastSlowBlockNavigationLog != 0
                && ElapsedMilliseconds(_lastSlowBlockNavigationLog, presentedAt) < 2000
            || LastStats.VisibleObjects < 1000 || LastStats.DrawnObjects == 0
            || !_direct2DRenderer.DrewBlockTilesThisFrame && LastStats.TileDraws != 0) return;

        _lastSlowBlockNavigationLog = presentedAt;
        AppLog.Info(FormattableString.Invariant(
            $"Slow Stage navigation: frame={LastFrameRenderMilliseconds:0.00} ms, commands={LastDirect2DCommandMilliseconds:0.00} ms, present={LastDirect2DPresentMilliseconds:0.00} ms, bitmap_source_probe={_direct2DRenderer.BlockTileSourceProbeMilliseconds:0.00} ms, chunks={(_direct2DRenderer.DrewBlockTilesThisFrame ? LastStats.TileDraws : 0)}, objects={LastStats.DrawnObjects}, size={Width}x{Height}, backend={(LastFrameUsedDirect2D ? "Direct2D" : "GDI")}."));
    }

    private long PulseZoomLodIdleTimer()
    {
        var deadline = System.Diagnostics.Stopwatch.GetTimestamp()
            + (long)(ZoomLodPreviewIdleMilliseconds * System.Diagnostics.Stopwatch.Frequency / 1000d);
        // Restarting a WinForms Timer destroys/recreates its native message
        // window. Update a deadline on input and re-arm only when it expires.
        if (!_zoomLodPreviewTimer.Enabled)
        {
            _zoomLodPreviewTimer.Interval = ZoomLodPreviewIdleMilliseconds;
            _zoomLodPreviewTimer.Start();
        }
        return deadline;
    }

    private void TickZoomLodPreviewIdle()
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_planarPanHighlightPreviewActive && now >= _planarPanHighlightSettleDeadline)
        {
            _planarPanHighlightPreviewActive = false;
            InvalidateOverlay();
        }
        if (_gdiRasterZoomPreviewActive)
        {
            var rasterRemaining = _gdiRasterZoomSettleDeadline - now;
            if (rasterRemaining > 0)
            {
                _zoomLodPreviewTimer.Interval = Math.Max(1,
                    (int)Math.Ceiling(rasterRemaining * 1000d / System.Diagnostics.Stopwatch.Frequency));
                return;
            }
            EndZoomLodPreview(invalidate: true);
            return;
        }
        var deadline = _referenceNavigationPreviewActive ? _referenceNavigationSettleDeadline : 0;
        if (_zoomLodPreviewActive || _distortZoomPreviewActive || _selectionZoomPreviewActive)
            deadline = Math.Max(deadline, _planarZoomLodSettleDeadline);
        if (_planarPanHighlightPreviewActive)
            deadline = Math.Max(deadline, _planarPanHighlightSettleDeadline);
        var remaining = deadline - now;
        if (remaining > 0)
        {
            _zoomLodPreviewTimer.Interval = Math.Max(1,
                (int)Math.Ceiling(remaining * 1000d / System.Diagnostics.Stopwatch.Frequency));
            return;
        }
        EndZoomLodPreview(invalidate: true);
    }
}
