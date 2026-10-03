namespace VectorAnimationEngine;

internal readonly record struct ResponsiveWindowMetrics(
    Size InitialSize,
    Size MinimumSize,
    int InspectorWidth,
    int VaultWidth,
    int TimelineHeight);

internal sealed partial class MainForm
{
    private const int ResponsiveReferenceDpi = 96;
    private const int ResponsiveMinimumLogicalWidth = 760;
    private const int ResponsiveMinimumLogicalHeight = 520;
    private const int ResponsiveDefaultMinimumLogicalWidth = 1120;
    private const int ResponsiveDefaultMinimumLogicalHeight = 720;
    private const int ResponsiveInitialLogicalWidth = 1480;
    private const int ResponsiveInitialLogicalHeight = 920;

    private bool _applyingResponsiveWorkbenchLayout;
    private bool _resolutionRefreshQueued;
    private int _responsiveLayoutDpi = ResponsiveReferenceDpi;
    private string _responsiveScreenDeviceName = "";
    private Rectangle _responsiveScreenWorkingArea;

    internal static ResponsiveWindowMetrics CalculateResponsiveWindowMetrics(
        Rectangle workingArea,
        int dpi,
        Size clientSize,
        int preferredTimelineHeight)
    {
        dpi = Math.Max(ResponsiveReferenceDpi, dpi);
        var scale = dpi / (double)ResponsiveReferenceDpi;
        var logicalWorkingWidth = Math.Max(1, (int)Math.Floor(workingArea.Width / scale));
        var logicalWorkingHeight = Math.Max(1, (int)Math.Floor(workingArea.Height / scale));
        var logicalClientWidth = Math.Max(1, (int)Math.Floor(clientSize.Width / scale));

        var minimumLogicalWidth = Math.Min(
            ResponsiveDefaultMinimumLogicalWidth,
            Math.Max(
                Math.Min(ResponsiveMinimumLogicalWidth, logicalWorkingWidth),
                (int)Math.Round(logicalWorkingWidth * 0.75)));
        var minimumLogicalHeight = Math.Min(
            ResponsiveDefaultMinimumLogicalHeight,
            Math.Max(
                Math.Min(ResponsiveMinimumLogicalHeight, logicalWorkingHeight),
                (int)Math.Round(logicalWorkingHeight * 0.75)));
        var minimumSize = new Size(
            Math.Min(workingArea.Width, ScaleResponsive(minimumLogicalWidth, scale)),
            Math.Min(workingArea.Height, ScaleResponsive(minimumLogicalHeight, scale)));

        var outerMargin = ScaleResponsive(24, scale);
        var availableWidth = Math.Max(1, workingArea.Width - outerMargin * 2);
        var availableHeight = Math.Max(1, workingArea.Height - outerMargin * 2);
        var initialSize = new Size(
            Math.Clamp(
                ScaleResponsive(ResponsiveInitialLogicalWidth, scale),
                minimumSize.Width,
                Math.Max(minimumSize.Width, availableWidth)),
            Math.Clamp(
                ScaleResponsive(ResponsiveInitialLogicalHeight, scale),
                minimumSize.Height,
                Math.Max(minimumSize.Height, availableHeight)));

        var inspectorLogicalWidth = Math.Clamp(
            (int)Math.Round(logicalClientWidth * 0.25),
            272,
            InspectorPanelExpandedWidth);
        var vaultLogicalWidth = Math.Clamp(
            (int)Math.Round(logicalClientWidth * 0.21),
            232,
            VaultDrawerExpandedWidth);
        var minimumTimelineHeight = ScaleResponsive(118, scale);
        // Six rows need a little more than a fifth of the window, so the share was raised or the
        // default height would be clipped straight back to about four rows on a tall window.
        var responsiveTimelineCap = Math.Max(
            minimumTimelineHeight,
            (int)Math.Round(clientSize.Height * 0.28));
        var stagePreservingTimelineCap = Math.Max(
            minimumTimelineHeight,
            clientSize.Height - ScaleResponsive(320, scale));
        var timelineHeight = Math.Clamp(
            Math.Min(preferredTimelineHeight, Math.Min(responsiveTimelineCap, stagePreservingTimelineCap)),
            minimumTimelineHeight,
            Math.Max(minimumTimelineHeight, clientSize.Height));

        return new ResponsiveWindowMetrics(
            initialSize,
            minimumSize,
            ScaleResponsive(inspectorLogicalWidth, scale),
            ScaleResponsive(vaultLogicalWidth, scale),
            timelineHeight);
    }

    private static int ScaleResponsive(int logicalPixels, double scale)
        => Math.Max(1, (int)Math.Round(logicalPixels * scale));

    private void InitializeResolutionAwareWindow()
    {
        if (!IsHandleCreated) return;

        SynchronizeResponsiveDpiMetrics(DeviceDpi);
        var screen = Screen.FromHandle(Handle);
        RememberResponsiveScreen(screen);
        var metrics = CalculateResponsiveWindowMetrics(
            screen.WorkingArea,
            DeviceDpi,
            ClientSize,
            Math.Max(_timelinePanelMinimumSize.Height, _timelinePanelPreferredHeight));
        MinimumSize = metrics.MinimumSize;

        if (_restartState is null && WindowState == FormWindowState.Normal)
        {
            StartPosition = FormStartPosition.Manual;
            var x = screen.WorkingArea.Left + Math.Max(0, (screen.WorkingArea.Width - metrics.InitialSize.Width) / 2);
            var y = screen.WorkingArea.Top + Math.Max(0, (screen.WorkingArea.Height - metrics.InitialSize.Height) / 2);
            Bounds = new Rectangle(new Point(x, y), metrics.InitialSize);
            metrics = CalculateResponsiveWindowMetrics(
                screen.WorkingArea,
                DeviceDpi,
                ClientSize,
                Math.Max(_timelinePanelMinimumSize.Height, _timelinePanelPreferredHeight));
        }

        ApplyResponsiveWorkbenchLayout(metrics);
        AppLog.Info(
            $"Resolution-aware UI initialized for {screen.DeviceName}: work={screen.WorkingArea}, " +
            $"dpi={DeviceDpi}, window={Bounds}, minimum={MinimumSize}, inspector={metrics.InspectorWidth}, " +
            $"vault={metrics.VaultWidth}, timeline={metrics.TimelineHeight}.");
    }

    private void ApplyResponsiveWorkbenchLayout()
    {
        if (_applyingResponsiveWorkbenchLayout || !IsHandleCreated || _topBar is null) return;
        _applyingResponsiveWorkbenchLayout = true;
        var screen = Screen.FromHandle(Handle);
        try
        {
            var metrics = CalculateResponsiveWindowMetrics(
                screen.WorkingArea,
                DeviceDpi,
                ClientSize,
                Math.Max(_timelinePanelMinimumSize.Height, _timelinePanelPreferredHeight));
            MinimumSize = metrics.MinimumSize;
            ApplyResponsiveWorkbenchLayoutCore(metrics);
        }
        finally
        {
            _applyingResponsiveWorkbenchLayout = false;
        }
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        SynchronizeResponsiveDpiMetrics(e.DeviceDpiNew);
        QueueResolutionAwareLayoutRefresh();
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        if (IsDisposed || !IsHandleCreated || !Visible) return;
        var screen = Screen.FromHandle(Handle);
        if (!string.Equals(_responsiveScreenDeviceName, screen.DeviceName, StringComparison.OrdinalIgnoreCase)
            || _responsiveScreenWorkingArea != screen.WorkingArea)
        {
            QueueResolutionAwareLayoutRefresh();
        }
    }

    private void SynchronizeResponsiveDpiMetrics(int dpi)
    {
        dpi = Math.Max(ResponsiveReferenceDpi, dpi);
        if (_responsiveLayoutDpi == dpi && _timelinePanelMinimumSize.Height > 0) return;
        var previousDpi = Math.Max(ResponsiveReferenceDpi, _responsiveLayoutDpi);
        _timelinePanelPreferredHeight = Math.Max(
            ScaleResponsive(118, dpi / (double)ResponsiveReferenceDpi),
            (int)Math.Round(_timelinePanelPreferredHeight * dpi / (double)previousDpi));
        _responsiveLayoutDpi = dpi;
        var scale = dpi / (double)ResponsiveReferenceDpi;
        _timelinePanelMinimumSize = new Size(
            ScaleResponsive(360, scale),
            ScaleResponsive(118, scale));
        _timeline.MinimumSize = _timelinePanelMinimumSize;
    }

    private void ApplyResponsiveWorkbenchLayout(ResponsiveWindowMetrics metrics)
    {
        if (_applyingResponsiveWorkbenchLayout || _topBar is null) return;
        _applyingResponsiveWorkbenchLayout = true;
        try
        {
            ApplyResponsiveWorkbenchLayoutCore(metrics);
        }
        finally
        {
            _applyingResponsiveWorkbenchLayout = false;
        }
    }

    private void ApplyResponsiveWorkbenchLayoutCore(ResponsiveWindowMetrics metrics)
    {
        if (_topBar is null) return;
        SuspendLayout();
        _inspectorHost.Parent?.SuspendLayout();
        try
        {
            _workspacePanelAnimationTimer.Stop();
            _vaultDrawerTimer.Stop();
            _inspectorPanelExpandedWidth = metrics.InspectorWidth;
            _vaultDrawerExpandedWidth = metrics.VaultWidth;
            _timelinePanelExpandedHeight = metrics.TimelineHeight;
            _inspectorPanelAnimationToWidth = _inspectorPanelOpen ? metrics.InspectorWidth : 0;
            _timelinePanelAnimationToHeight = _timelinePanelOpen ? metrics.TimelineHeight : 0;

            _inspectorHost.Width = _inspectorPanelOpen ? metrics.InspectorWidth : 0;
            _inspectorHost.Visible = _inspectorPanelOpen;
            var availableDrawingObjectRowWidth = Math.Max(0, ClientSize.Width - metrics.InspectorWidth);
            var showDrawingSnapping = availableDrawingObjectRowWidth >= ScaleResponsive(680, DeviceDpi / 96d);
            _drawSnappingStrip.Visible = showDrawingSnapping;
            _drawingObjectRow.ColumnStyles[1].Width = showDrawingSnapping
                ? _drawSnappingStrip.Width + ScaleResponsive(12, DeviceDpi / 96d)
                : 0;
            if (_timelinePanelOpen)
            {
                _timeline.MinimumSize = _timelinePanelMinimumSize;
                _timeline.Height = metrics.TimelineHeight;
                _timeline.Visible = true;
            }
            else
            {
                _timeline.MinimumSize = new Size(_timelinePanelMinimumSize.Width, 0);
                _timeline.Height = 0;
                _timeline.Visible = false;
            }

            if (_vaultDrawer is { } vaultDrawer)
            {
                vaultDrawer.Width = metrics.VaultWidth;
                _libraryVaultPanel.Width = metrics.VaultWidth;
                _vaultDrawerVisibleWidth = _vaultDrawerOpen ? metrics.VaultWidth : 0;
                ApplyVaultDrawerClip();
                PositionVaultToolStrip();
            }

            RefreshShotDirectorWidth();

            LayoutTopBar();
        }
        finally
        {
            _inspectorHost.Parent?.ResumeLayout(performLayout: true);
            ResumeLayout(performLayout: true);
        }
    }

    private void LayoutTopBar()
    {
        if (_topBar is null
            || _restartWindowButton is null
            || _minimizeWindowButton is null
            || _maximizeButton is null
            || _closeWindowButton is null
            || _topPlaybackFpsLabel is null)
        {
            return;
        }

        PositionWindowChromeButtons(
            _topBar,
            _restartWindowButton,
            _minimizeWindowButton,
            _maximizeButton,
            _closeWindowButton);

        var availableAfterDash = _restartWindowButton.Left - _dashDock.Left - 14;
        _dashDock.Visible = availableAfterDash >= ScaleResponsive(DashDock.CompactDockWidth, DeviceDpi / 96d);
        _dashDock.Compact = availableAfterDash < ScaleResponsive(
            DashDock.PreferredDockWidth + 158,
            DeviceDpi / 96d);
        var fpsFits = _dashDock.Visible && _dashDock.Right + ScaleResponsive(8, DeviceDpi / 96d)
            <= _restartWindowButton.Left - _topPlaybackFps.Width - _topPlaybackFpsLabel.Width - 18;
        _topPlaybackFps.Visible = fpsFits;
        _topPlaybackFpsLabel.Visible = fpsFits;
        if (fpsFits)
        {
            PositionTopPlaybackFpsControls(
                _topBar,
                _restartWindowButton,
                _topPlaybackFpsLabel,
                _topPlaybackFps);
        }
    }

    private void QueueResolutionAwareLayoutRefresh()
    {
        if (_resolutionRefreshQueued || IsDisposed || !IsHandleCreated) return;
        _resolutionRefreshQueued = true;
        try
        {
            BeginInvoke((Action)(() =>
            {
                _resolutionRefreshQueued = false;
                if (IsDisposed || !IsHandleCreated) return;
                var screen = Screen.FromHandle(Handle);
                RememberResponsiveScreen(screen);
                SynchronizeResponsiveDpiMetrics(DeviceDpi);
                var metrics = CalculateResponsiveWindowMetrics(
                    screen.WorkingArea,
                    DeviceDpi,
                    ClientSize,
                    Math.Max(_timelinePanelMinimumSize.Height, _timelinePanelPreferredHeight));
                MinimumSize = metrics.MinimumSize;
                if (WindowState == FormWindowState.Normal)
                {
                    Bounds = NormalizeRestartWindowBounds(Bounds, [screen.WorkingArea], MinimumSize);
                }
                ApplyMaximizedBounds();
                ApplyResponsiveWorkbenchLayout(metrics);
            }));
        }
        catch (InvalidOperationException)
        {
            _resolutionRefreshQueued = false;
        }
    }

    private void RememberResponsiveScreen(Screen screen)
    {
        _responsiveScreenDeviceName = screen.DeviceName;
        _responsiveScreenWorkingArea = screen.WorkingArea;
    }
}
