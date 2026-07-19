using System.Text.Json;

namespace VectorAnimationEngine;

internal enum ToolShortcutPreset
{
    TraditionalFlash,
    NumberKeys
}

internal enum TimelineFrameHeightPreset
{
    Low,
    Medium,
    High
}

internal sealed record ApplicationSettings
{
    public ToolShortcutPreset ToolShortcutPreset { get; init; } = ToolShortcutPreset.TraditionalFlash;
    public UiLanguage Language { get; init; } = UiLanguage.English;
    public int TimelineFrameWidth { get; init; } = 14;
    public TimelineFrameHeightPreset TimelineFrameHeight { get; init; } = TimelineFrameHeightPreset.Medium;
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
            if (!File.Exists(SettingsPath)) return new ApplicationSettings();
            var settings = JsonSerializer.Deserialize<ApplicationSettings>(File.ReadAllText(SettingsPath));
            if (settings is null
                || !Enum.IsDefined(settings.ToolShortcutPreset)
                || !Enum.IsDefined(settings.Language)
                || !Enum.IsDefined(settings.TimelineFrameHeight))
            {
                AppLog.Warn("Application settings contained an unsupported tool shortcut preset; using defaults.");
                return new ApplicationSettings();
            }

            return settings with { TimelineFrameWidth = Math.Clamp(settings.TimelineFrameWidth, 8, 32) };
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not load application settings; using defaults.", ex);
            return new ApplicationSettings();
        }
    }

    public static bool TrySave(ApplicationSettings settings)
    {
        var temporaryPath = SettingsPath + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(directory);
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
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
}
