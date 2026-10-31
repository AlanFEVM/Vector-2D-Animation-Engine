namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private ShotWorkspacePreviewMode? _shotPlaybackPreviewRestoreMode;
    private bool _shotPlaybackPreviewOverridden;

    private void UpdateShotMicroMotionPlaybackPreview(SceneShotDefinition? shot = null)
    {
        if (_enterPlaybackViewActive)
        {
            UpdateEnterPlaybackPreview();
            return;
        }
        if (!_playing || _shotPlaybackPreviewOverridden || shot?.MicroMotion.Enabled != true
            || !_stage.CanPreviewPlanarShot)
        {
            if (_shotPlaybackPreviewRestoreMode is { } restoreMode)
            {
                _shotPlaybackPreviewRestoreMode = null;
                _shotDirectorPanel.SetPreviewMode(restoreMode, _stage.CanPreviewPlanarShot);
            }
            return;
        }

        if (_shotPlaybackPreviewRestoreMode.HasValue
            || _shotDirectorPanel.PreviewMode != ShotWorkspacePreviewMode.ObserveCamera) return;
        _shotPlaybackPreviewRestoreMode = _shotDirectorPanel.PreviewMode;
        _shotDirectorPanel.SetPreviewMode(ShotWorkspacePreviewMode.CameraView, enabled: true);
    }

    private void ApplySceneShotMicroMotion(ShotDirectorMicroMotionEventArgs eventArgs)
    {
        var scene = ActiveScene();
        if (scene?.FindShot(eventArgs.ShotId) is not { } shot || !IsSceneShotAllowed(shot)) return;
        var settings = eventArgs.Settings;
        if (eventArgs.UseHandheldDefaults || settings.Enabled && !shot.MicroMotion.Enabled)
            settings = settings.WithHandheldDefaults(shot.EvaluateSettings(0),
                _stage.CanvasWorldWidth, _stage.CanvasWorldHeight, eventArgs.UseHandheldDefaults);
        ApplySceneShotEdit(scene, () => scene.UpdateShotMicroMotion(shot.Id, settings));
        if (shot.Id == _activeShotId) _shotDirectorPanel.SetSelectedShotMicroMotion(shot.MicroMotion);
        UpdateShotFramingGizmo();
        RefreshShotPreview(force: true);
    }
}
