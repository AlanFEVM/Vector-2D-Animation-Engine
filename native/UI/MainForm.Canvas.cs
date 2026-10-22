namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private sealed record CanvasSettingsUndo(
        VectorProject Project, SceneDefinition Scene, SceneCanvasSettings? Before, SceneCanvasSettings? After);

    private readonly CanvasWelcomePanel _canvasWelcome = new() { Visible = false };
    private readonly Stack<CanvasSettingsUndo> _canvasSettingsUndo = new();
    private readonly Stack<CanvasSettingsUndo> _canvasSettingsRedo = new();
    private bool _changingCanvasSettings;
    private SceneCanvasSettings? _presentedCanvasSettings;
    private ImageAssetDefinition? _presentedCanvasImage;
    private string _presentedCanvasImageKey = "";
    private string _canvasBackgroundError = "";

    private void InitializeCanvasWorkspace()
    {
        _stage.Controls.Add(_canvasWelcome);
        _canvasWelcome.ConfigureRequested += (_, _) => ShowCanvasSettings();
        _canvasWelcome.StartRequested += (_, _) =>
        {
            if (ActiveScene() is not { } scene) return;
            scene.DismissCanvasWelcome();
            SetProjectDirty(true);
            UpdateCanvasWorkspace();
            _stage.Focus();
        };
        _stage.SizeChanged += (_, _) => LayoutCanvasWelcome();
        _documentInfoPanel.CanvasSettingsRequested += (_, _) => ShowCanvasSettings();
    }

    private void ShowCanvasSettings()
    {
        if (ActiveScene() is not { } scene
            || HasActiveCanvasPointerInteraction() || !CommitTextEdit()) return;
        StopPlayback();
        FinishPointerInteractionForContextChange();
        CompleteSceneShotEdit();
        using var dialog = new SceneCanvasDialog(scene.Canvas, _project.ImageAssets, ResolveImageAssetPath);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var settings = dialog.Settings;
        ImageAssetDefinition? addedImage = null;
        if (dialog.SelectedImagePath is { Length: > 0 } path
            && settings.BackgroundKind == SceneBackgroundKind.Image)
        {
            if (!TryProbeImageFile(path, out var probe)) return;
            addedImage = RegisterImageAsset(probe, BitmapImageImportSettings.Default, deferRefresh: true);
            if (addedImage is null) return;
            settings = settings with { ImageAssetId = addedImage.Id };
        }
        var previous = scene.Canvas;
        if (previous == settings) return;
        _changingCanvasSettings = true;
        try
        {
            if (!_project.TrySetSceneCanvas(scene.Id, settings))
            {
                if (addedImage is not null) _project.TryRemoveImageAsset(addedImage.Id, out _);
                ShowImageAssetError("The canvas settings could not be applied.", "");
                return;
            }
            scene.DismissCanvasWelcome();
            _canvasSettingsRedo.Clear();
            var entry = new CanvasSettingsUndo(_project, scene, previous, settings);
            _canvasSettingsUndo.Push(entry);
            RecordUndoSequence(entry);
            if (_canvasSettingsUndo.Count > MaxUndoSnapshots)
            {
                var retained = _canvasSettingsUndo.Take(MaxUndoSnapshots).Reverse().ToArray();
                _canvasSettingsUndo.Clear();
                foreach (var item in retained) _canvasSettingsUndo.Push(item);
            }
            PruneUndoSequenceEntries();
        }
        finally
        {
            _changingCanvasSettings = false;
        }
        _libraryVaultPanel.RefreshProjectObjects();
        UpdateCanvasWorkspace();
        if (previous is null && IsSceneWorkspaceSelected) _stage.FitCanvas();
        UpdateDocumentInfoPanel();
        RefreshShotDirector(force: true);
        UpdateShotFramingGizmo();
        RefreshShotPreview(force: true);
        _stage.Invalidate();
    }

    private void UpdateCanvasWorkspace()
    {
        if (_stage.IsDisposed) return;
        var scene = ActiveScene();
        var settings = IsSceneWorkspaceSelected ? scene?.Canvas : null;
        var asset = settings is { BackgroundKind: SceneBackgroundKind.Image }
            ? _project.FindImageAsset(settings.ImageAssetId) : null;
        var key = asset is null ? "" : $"{asset.SourcePath}|{asset.Sha256}|{asset.ImportSettings}";
        if (settings != _presentedCanvasSettings || !ReferenceEquals(asset, _presentedCanvasImage)
            || key != _presentedCanvasImageKey)
        {
            _presentedCanvasSettings = settings;
            _presentedCanvasImage = asset;
            _presentedCanvasImageKey = key;
            _canvasBackgroundError = "";
            BitmapImageRaster? raster = null;
            if (asset is not null)
            {
                try
                {
                    if (ResolveImageAssetPath(asset) is not { } source)
                        _canvasBackgroundError = UiLocalization.T("The selected background image is no longer available.");
                    else
                        raster = BitmapImageRasterizer.Decode(source, asset.ImportSettings);
                }
                catch (Exception exception) when (exception is IOException or ArgumentException
                    or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException or OutOfMemoryException)
                {
                    _canvasBackgroundError = exception.Message;
                    AppLog.Error("Unable to prepare scene background image", exception);
                }
            }
            _stage.SetCanvasPresentation(settings is null ? null : new SceneCanvasPresentation(
                settings, raster, raster is null ? SizeF.Empty : new SizeF(raster.PixelWidth, raster.PixelHeight),
                asset?.ImportSettings.FilterMode == ImageFilterMode.Point));
        }
        var started = scene is not null && (scene.Instances.Count > 0 || scene.ShotCount > 0 || scene.Layers.Count > 1
            || scene.Timeline.Tracks.Any(track => track.Keyframes.Any(keyframe => keyframe.HasContent)));
        if (started && scene is { CanvasWelcomeDismissed: false })
        {
            scene.DismissCanvasWelcome();
        }
        var welcome = IsSceneWorkspaceSelected && !_playing && !_stage.ShotWorkspacePreviewActive
            && scene is { Canvas: null, CanvasWelcomeDismissed: false } && !started;
        if (_canvasWelcome.Visible != welcome) _canvasWelcome.Visible = welcome;
        if (welcome)
        {
            LayoutCanvasWelcome();
            _canvasWelcome.BringToFront();
        }
    }

    private void LayoutCanvasWelcome()
    {
        var width = Math.Min((int)MathF.Round(364 * _stage.DeviceDpi / 96f), Math.Max(0, _stage.Width - 32));
        var height = (int)MathF.Round(152 * _stage.DeviceDpi / 96f);
        _canvasWelcome.SetBounds(Math.Max(0, (_stage.Width - width) / 2),
            Math.Max(0, (_stage.Height - height) / 2), width, height);
    }

    private bool TryUndoCanvasSettings(long latestOtherSequence)
    {
        if (!_canvasSettingsUndo.TryPeek(out var entry)
            || !ReferenceEquals(entry.Project, _project) || !ReferenceEquals(entry.Scene, ActiveScene())
            || UndoSequence(entry) < latestOtherSequence) return false;
        _changingCanvasSettings = true;
        try
        {
            if (!_project.TrySetSceneCanvas(entry.Scene.Id, entry.Before)) return false;
            _canvasSettingsUndo.Pop();
            _undoSequenceByEntry.Remove(entry);
            _canvasSettingsRedo.Push(entry);
            RefreshCanvasSettingsPresentation();
            return true;
        }
        finally
        {
            _changingCanvasSettings = false;
        }
    }

    private bool RedoCanvasSettings()
    {
        if (!_canvasSettingsRedo.TryPeek(out var entry)
            || !ReferenceEquals(entry.Project, _project) || !ReferenceEquals(entry.Scene, ActiveScene())
            || HasActiveCanvasPointerInteraction()) return false;
        _changingCanvasSettings = true;
        try
        {
            if (!_project.TrySetSceneCanvas(entry.Scene.Id, entry.After)) return false;
            _canvasSettingsRedo.Pop();
            _canvasSettingsUndo.Push(entry);
            RecordUndoSequence(entry);
            RefreshCanvasSettingsPresentation();
            return true;
        }
        finally
        {
            _changingCanvasSettings = false;
        }
    }

    private void RefreshCanvasSettingsPresentation()
    {
        UpdateCanvasWorkspace();
        UpdateDocumentInfoPanel();
        RefreshShotDirector(force: true);
        UpdateShotFramingGizmo();
        RefreshShotPreview(force: true);
        _stage.Invalidate();
    }
}
