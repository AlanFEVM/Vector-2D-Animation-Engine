namespace VectorAnimationEngine;

internal sealed partial class StageControl
{
    private bool _navigationPreviewEnabled = true;
    private bool _referenceNavigationPreviewActive;
    private long _referenceNavigationSettleDeadline;
    internal bool ReferenceNavigationPreviewActive => _referenceNavigationPreviewActive;

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
        _referenceNavigationSettleDeadline = System.Diagnostics.Stopwatch.GetTimestamp()
            + (long)(ZoomLodPreviewIdleMilliseconds * System.Diagnostics.Stopwatch.Frequency / 1000d);
        // Restarting a WinForms Timer destroys/recreates its native message
        // window. Update a deadline on input and re-arm only when it expires.
        if (!_zoomLodPreviewTimer.Enabled)
        {
            _zoomLodPreviewTimer.Interval = ZoomLodPreviewIdleMilliseconds;
            _zoomLodPreviewTimer.Start();
        }
    }

    private void TickZoomLodPreviewIdle()
    {
        var remaining = _referenceNavigationSettleDeadline - System.Diagnostics.Stopwatch.GetTimestamp();
        if (_referenceNavigationPreviewActive && remaining > 0)
        {
            _zoomLodPreviewTimer.Interval = Math.Max(1,
                (int)Math.Ceiling(remaining * 1000d / System.Diagnostics.Stopwatch.Frequency));
            return;
        }
        EndZoomLodPreview(invalidate: true);
    }
}
