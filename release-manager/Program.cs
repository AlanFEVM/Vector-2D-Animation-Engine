namespace VectorAnimationEngine;

internal static class ReleaseManagerProgram
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var settings = ApplicationSettingsStore.Load();
        Theme.ConfigureColorAdjustments(
            settings.ColorTheme,
            settings.ThemeHueDegrees,
            settings.ThemeSaturationPercent,
            settings.ThemeBrightnessPercent,
            settings.AccentHueDegrees,
            settings.AccentSaturationPercent,
            settings.AccentBrightnessPercent);
        UiLocalization.SetLanguage(settings.Language);

        try
        {
            var repositoryRoot = args.Length > 0
                ? RepositoryLocator.Validate(args[0])
                : RepositoryLocator.Find();
            Application.Run(new ReleaseManagerForm(repositoryRoot));
        }
        catch (Exception exception)
        {
            ModernMessageDialog.Show(
                null,
                exception.Message,
                "Release Manager",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}
