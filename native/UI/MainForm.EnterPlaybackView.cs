namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private bool _enterPlaybackViewActive;
    private StageViewState? _enterPlaybackOriginalView;
    private ShotWorkspacePreviewMode _enterPlaybackOriginalMode;

    private void InitializeEnterPlaybackView()
    {
        _documentInfoPanel.SetPlaybackView(_applicationSettings.EnterPlaybackView);
        _documentInfoPanel.PlaybackViewChanged += (_, _) =>
        {
            _applicationSettings = _applicationSettings with { EnterPlaybackView = _documentInfoPanel.PlaybackView.Normalized() };
            if (!ApplicationSettingsStore.TrySave(_applicationSettings))
                AppLog.Warn("Playback view preferences could not be saved; current-session changes remain active.");
            if (!_enterPlaybackViewActive) return;
            _shotPlaybackPreviewOverridden = false;
            ApplyEnterPlaybackView();
            UpdateShotFramingGizmo();
        };
        _stage.SizeChanged += (_, _) =>
        {
            if (_enterPlaybackViewActive && _playing) ApplyEnterPlaybackView();
        };
    }

    private void BeginEnterPlaybackView()
    {
        _enterPlaybackViewActive = true;
        _enterPlaybackOriginalView = _stage.CaptureViewState();
        _enterPlaybackOriginalMode = _stage.ShotPreviewMode;
        ApplyEnterPlaybackView();
    }

    private void ApplyEnterPlaybackView()
    {
        var settings = _applicationSettings.EnterPlaybackView;
        _stage.SetEnterPlaybackViewSettings(settings);
        if (_enterPlaybackOriginalView is { } original) _stage.ApplyEnterPlaybackView(original, settings);
    }

    private void UpdateEnterPlaybackPreview()
    {
        if (_shotPlaybackPreviewOverridden || !_stage.CanPreviewPlanarShot) return;
        var mode = _applicationSettings.EnterPlaybackView.Mode == EnterPlaybackFitMode.KeepCurrent
            ? _enterPlaybackOriginalMode : ShotWorkspacePreviewMode.CameraView;
        _stage.SetShotWorkspacePreviewMode(mode);
        _shotDirectorPanel.SetPreviewMode(_stage.ShotPreviewMode, _stage.CanPreviewPlanarShot);
    }

    private void EndEnterPlaybackView()
    {
        if (!_enterPlaybackViewActive) return;
        _enterPlaybackViewActive = false;
        if (_applicationSettings.EnterPlaybackView.RestoreView)
        {
            _stage.SetEnterPlaybackViewSettings(null);
            if (_enterPlaybackOriginalView is { } original) _stage.RestoreViewState(original);
            if (!_shotPlaybackPreviewOverridden)
            {
                _stage.SetShotWorkspacePreviewMode(_enterPlaybackOriginalMode);
                _shotDirectorPanel.SetPreviewMode(_stage.ShotPreviewMode, _stage.CanPreviewPlanarShot);
            }
        }
        _enterPlaybackOriginalView = null;
        _shotPlaybackPreviewOverridden = true;
    }
}
