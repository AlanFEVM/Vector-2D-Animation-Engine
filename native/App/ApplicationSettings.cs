using System.Text.Json;

namespace VectorAnimationEngine;

internal enum ToolShortcutPreset
{
    TraditionalFlash,
    NumberKeys
}

internal enum ApplicationColorTheme
{
    Dark,
    White
}

internal enum TimelineFrameHeightPreset
{
    Low,
    Medium,
    High
}

internal sealed record ApplicationSettings
{
    public const int DefaultThemeHueDegrees = 210;
    public const int DefaultThemeSaturationPercent = 100;
    public const int DefaultThemeBrightnessPercent = 100;
    public const int DefaultAccentHueDegrees = 170;
    public const int DefaultAccentSaturationPercent = 100;
    public const int DefaultAccentBrightnessPercent = 100;
    public const int MinimumSaturationPercent = 0;
    public const int MaximumSaturationPercent = 200;
    public const int MinimumBrightnessPercent = 50;
    public const int MaximumBrightnessPercent = 150;
    public const int DefaultWorkspaceColorArgb = unchecked((int)0xFF111315);

    public ToolShortcutPreset ToolShortcutPreset { get; init; } = ToolShortcutPreset.TraditionalFlash;
    public string ActiveShortcutProfileId { get; init; } = "";
    public ShortcutProfileRecord[] CustomShortcutProfiles { get; init; } = [];
    public UiLanguage Language { get; init; } = UiLanguage.English;
    public ApplicationColorTheme ColorTheme { get; init; } = ApplicationColorTheme.Dark;
    public int ThemeHueDegrees { get; init; } = DefaultThemeHueDegrees;
    public int ThemeSaturationPercent { get; init; } = DefaultThemeSaturationPercent;
    public int ThemeBrightnessPercent { get; init; } = DefaultThemeBrightnessPercent;
    public int AccentHueDegrees { get; init; } = DefaultAccentHueDegrees;
    public int AccentSaturationPercent { get; init; } = DefaultAccentSaturationPercent;
    public int AccentBrightnessPercent { get; init; } = DefaultAccentBrightnessPercent;
    public int TimelineFrameWidth { get; init; } = 14;
    public TimelineFrameHeightPreset TimelineFrameHeight { get; init; } = TimelineFrameHeightPreset.Medium;
    public bool TimelineAutoKeyframeEnabled { get; init; } = false;
    /// <summary>
    /// Operation preference. When enabled, holding Shift while dragging a Free Transform scale handle
    /// keeps the selection's aspect ratio and releasing Shift allows free scaling. This mode is the
    /// default. When disabled, scaling is proportional and Shift temporarily allows free scaling.
    /// </summary>
    public bool FreeTransformShiftProportionalEnabled { get; init; } = true;
    public int WorkspaceColorArgb { get; init; } = DefaultWorkspaceColorArgb;
    /// <summary>Enables the loopback JSON-RPC bridge used by Codex and MCP clients.</summary>
    public bool CodexIntegrationEnabled { get; init; } = false;
    public bool CodexIntegrationAllowChanges { get; init; } = false;
    /// <summary>TCP port for the loopback Codex bridge. Port zero is not persisted.</summary>
    public int CodexIntegrationPort { get; init; } = 43521;
    /// <summary>Optional bearer token. An empty value keeps the bridge local-only without a token.</summary>
    public string CodexIntegrationAuthToken { get; init; } = "";
}

internal static class ApplicationSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Vector2DAnimationEngine",
        "settings.json");

    public static ApplicationSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return Normalize(new ApplicationSettings());
            var settings = JsonSerializer.Deserialize<ApplicationSettings>(File.ReadAllText(SettingsPath));
            if (settings is null)
            {
                AppLog.Warn("Application settings were empty; using defaults.");
                return Normalize(new ApplicationSettings());
            }

            return Normalize(settings);
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not load application settings; using defaults.", ex);
            return Normalize(new ApplicationSettings());
        }
    }

    public static bool TrySave(ApplicationSettings settings)
    {
        var temporaryPath = SettingsPath + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(directory);
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(Normalize(settings), JsonOptions));
            File.Move(temporaryPath, SettingsPath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not save application settings.", ex);
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch
            {
                // Preserve the original save failure as the actionable error.
            }

            return false;
        }
    }

    internal static ApplicationSettings Normalize(ApplicationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var preset = Enum.IsDefined(settings.ToolShortcutPreset)
            ? settings.ToolShortcutPreset
            : ToolShortcutPreset.TraditionalFlash;
        var language = Enum.IsDefined(settings.Language) ? settings.Language : UiLanguage.English;
        var colorTheme = Enum.IsDefined(settings.ColorTheme)
            ? settings.ColorTheme
            : ApplicationColorTheme.Dark;
        var frameHeight = Enum.IsDefined(settings.TimelineFrameHeight)
            ? settings.TimelineFrameHeight
            : TimelineFrameHeightPreset.Medium;
        var shortcutProfiles = ShortcutProfiles.Normalize(
            settings.ActiveShortcutProfileId,
            settings.CustomShortcutProfiles,
            ShortcutProfiles.BuiltInProfileId(preset));
        foreach (var warning in shortcutProfiles.Warnings) AppLog.Warn(warning);

        return settings with
        {
            ToolShortcutPreset = preset,
            ActiveShortcutProfileId = shortcutProfiles.ActiveProfileId,
            CustomShortcutProfiles = shortcutProfiles.CustomProfiles,
            Language = language,
            ColorTheme = colorTheme,
            ThemeHueDegrees = NormalizeHueDegrees(settings.ThemeHueDegrees),
            ThemeSaturationPercent = NormalizeSaturationPercent(settings.ThemeSaturationPercent),
            ThemeBrightnessPercent = NormalizeBrightnessPercent(settings.ThemeBrightnessPercent),
            AccentHueDegrees = NormalizeHueDegrees(settings.AccentHueDegrees),
            AccentSaturationPercent = NormalizeSaturationPercent(settings.AccentSaturationPercent),
            AccentBrightnessPercent = NormalizeBrightnessPercent(settings.AccentBrightnessPercent),
            TimelineFrameWidth = Math.Clamp(settings.TimelineFrameWidth, 8, 32),
            TimelineFrameHeight = frameHeight,
            WorkspaceColorArgb = OpaqueArgb(settings.WorkspaceColorArgb),
            CodexIntegrationPort = Math.Clamp(settings.CodexIntegrationPort, 1024, 65535),
            CodexIntegrationAuthToken = NormalizeAuthToken(settings.CodexIntegrationAuthToken)
        };
    }

    private static string NormalizeAuthToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return "";
        var normalized = token.Trim();
        return normalized.Length <= 256 ? normalized : normalized[..256];
    }

    internal static int NormalizeHueDegrees(int hueDegrees)
    {
        var normalized = hueDegrees % 360;
        return normalized < 0 ? normalized + 360 : normalized;
    }

    internal static int NormalizeSaturationPercent(int saturationPercent)
    {
        return Math.Clamp(
            saturationPercent,
            ApplicationSettings.MinimumSaturationPercent,
            ApplicationSettings.MaximumSaturationPercent);
    }

    internal static int NormalizeBrightnessPercent(int brightnessPercent)
    {
        return Math.Clamp(
            brightnessPercent,
            ApplicationSettings.MinimumBrightnessPercent,
            ApplicationSettings.MaximumBrightnessPercent);
    }

    private static int OpaqueArgb(int argb)
    {
        return unchecked((int)(0xFF000000u | ((uint)argb & 0x00FFFFFFu)));
    }
}
