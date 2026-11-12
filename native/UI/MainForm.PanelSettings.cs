namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private bool SavePanelSettings(SettingsDialog dialog)
    {
        var profiles = dialog.CustomShortcutProfiles;
        var settings = _applicationSettings with
        {
            ToolShortcutPreset = ShortcutProfiles.LegacyPresetForProfile(dialog.SelectedShortcutProfileId, profiles),
            ActiveShortcutProfileId = dialog.SelectedShortcutProfileId,
            CustomShortcutProfiles = profiles,
            Language = dialog.SelectedLanguage,
            ColorTheme = dialog.SelectedColorTheme,
            ThemeHueDegrees = dialog.SelectedThemeHueDegrees,
            ThemeSaturationPercent = dialog.SelectedThemeSaturationPercent,
            ThemeBrightnessPercent = dialog.SelectedThemeBrightnessPercent,
            AccentHueDegrees = dialog.SelectedAccentHueDegrees,
            AccentSaturationPercent = dialog.SelectedAccentSaturationPercent,
            AccentBrightnessPercent = dialog.SelectedAccentBrightnessPercent,
            SimpleSelectionHighlightEnabled = dialog.SimpleSelectionHighlightEnabled,
            SelectionArrows = dialog.SelectionSettings,
            ExperimentalSceneLightingEnabled = dialog.ExperimentalSceneLightingEnabled,
            NavigationPreviewEnabled = dialog.NavigationPreviewEnabled,
            Experimental3DEnabled = dialog.Experimental3DEnabled,
            FreeTransformShiftProportionalEnabled = dialog.FreeTransformShiftProportionalEnabled,
            ToolbarPosition = dialog.SelectedToolbarPosition,
            WindowCornerStyle = dialog.SelectedWindowCornerStyle
        };
        return CommitPanelSettings(dialog.ApplyCodexSettingsTo(settings), dialog, applyAppearance: true);
    }

    private bool CommitPanelSettings(ApplicationSettings settings, IWin32Window owner, bool applyAppearance = false)
    {
        settings = ApplicationSettingsStore.Normalize(settings);
        if (!ApplicationSettingsStore.TrySave(settings))
        {
            ModernMessageDialog.Show(owner,
                UiLocalization.T("The settings could not be saved. See the latest log file for details."),
                UiLocalization.T("Settings"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        var previous = _applicationSettings;
        _applicationSettings = settings;
        _stage.ExperimentalSceneLightingEnabled = settings.ExperimentalSceneLightingEnabled;
        _stage.NavigationPreviewEnabled = settings.NavigationPreviewEnabled;
        _stage.SimpleSelectionHighlightEnabled = settings.SimpleSelectionHighlightEnabled;
        RefreshSceneOpticsInspector();
        HideToolFlyouts();
        LayoutToolPalette();
        RefreshShortcutMenuDisplay();
        RefreshSelectionArrowPresentation();
        if (previous.Experimental3DEnabled != settings.Experimental3DEnabled) ApplyExperimental3DSettings();
        if (previous.CodexIntegrationEnabled != settings.CodexIntegrationEnabled
            || previous.CodexIntegrationAllowChanges != settings.CodexIntegrationAllowChanges
            || previous.CodexIntegrationPort != settings.CodexIntegrationPort
            || previous.CodexIntegrationAuthToken != settings.CodexIntegrationAuthToken)
            AppHost.Current?.ApplyCodexBridgeSettings(settings);
        if (applyAppearance)
        {
            ApplyWindowCornerStyle(settings.WindowCornerStyle);
            ApplyThemePreview(settings.ColorTheme,
                settings.ThemeHueDegrees, settings.ThemeSaturationPercent, settings.ThemeBrightnessPercent,
                settings.AccentHueDegrees, settings.AccentSaturationPercent, settings.AccentBrightnessPercent,
                owner as Control);
            UiLocalization.SetLanguage(settings.Language);
            RefreshSceneOpticsText();
            UpdateSceneDimensionButton();
        }
        AppLog.Info($"Application preferences saved: shortcuts={settings.ActiveShortcutProfileId}.");
        return true;
    }
}
