namespace VectorAnimationEngine;

internal static class Program
{
    private static readonly TimeSpan EditorRestartInstanceWait = TimeSpan.FromSeconds(10);

    private sealed record BenchmarkCommand(string LogMessage, Action Execute);

    private static readonly IReadOnlyDictionary<string, BenchmarkCommand> BenchmarkCommands =
        new Dictionary<string, BenchmarkCommand>(StringComparer.OrdinalIgnoreCase)
        {
            ["--bench"] = new("Running default benchmark", Benchmark.RunDefaultStress),
            ["--bench-freehand"] = new("Running freehand benchmark", Benchmark.RunFreehandStress),
            ["--bench-pressure"] = new("Running pressure brush benchmark", Benchmark.RunPressureBrushRegression),
            ["--bench-timeline"] = new("Running timeline regression benchmark", Benchmark.RunTimelineRegression),
            ["--bench-render"] = new("Running stage renderer regression benchmark", Benchmark.RunStageRendererRegression)
        };

    [STAThread]
    private static void Main(string[] args)
    {
        AppLog.Initialize(args);
        LauncherShutdownSignal.Configure(args);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => AppLog.Error("Unhandled UI thread exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var exception = e.ExceptionObject as Exception;
            AppLog.Error($"Unhandled domain exception. Terminating: {e.IsTerminating}", exception);
            AppLog.Flush();
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };

        if (TryRunBenchmark(args) || TryValidateReleaseNotes(args)) return;

        var restartToken = EditorRestartStore.GetRequestedToken(args);
        var instanceWait = string.IsNullOrWhiteSpace(restartToken)
            ? TimeSpan.Zero
            : EditorRestartInstanceWait;
        if (instanceWait > TimeSpan.Zero)
        {
            AppLog.Info($"Editor restart is waiting up to {instanceWait.TotalSeconds:0} seconds for the previous native instance to exit.");
        }
        if (!SingleInstanceLease.TryAcquire(instanceWait, out var instanceLease))
        {
            AppLog.Warn(instanceWait > TimeSpan.Zero
                ? "The previous native instance did not exit before the editor-restart startup timed out."
                : "A native application instance is already active; skipping duplicate startup.");
            return;
        }

        try
        {
            ApplicationConfiguration.Initialize();
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
            var inputFocusDismissalFilter = new InputFocusDismissalFilter();
            Application.AddMessageFilter(inputFocusDismissalFilter);
            using (instanceLease)
            {
                try
                {
                    var restartPrepared = EditorRestartStore.TryPrepareConsume(restartToken, out var restartState);
                    Action? completeRestartHandoff = restartPrepared
                        ? () => EditorRestartStore.CompletePreparedConsume(restartToken)
                        : null;
                    Application.Run(new AppHost(restartState, completeRestartHandoff));
                }
                finally
                {
                    Application.RemoveMessageFilter(inputFocusDismissalFilter);
                }
            }
            AppLog.Info("Application exited normally");
            AppLog.Flush();
        }
        catch (Exception ex)
        {
            AppLog.Error("Fatal application exception", ex);
            AppLog.Flush();
            ModernMessageDialog.Show(
                null,
                UiLocalization.CurrentLanguage == UiLanguage.SimplifiedChinese
                    ? $"应用程序已崩溃。详细信息请查看最新日志文件：\n\n{AppLog.LogPath}"
                    : $"The application crashed. See the latest log file for details:\n\n{AppLog.LogPath}",
                "Vector 2D Animation Engine",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static bool TryRunBenchmark(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || !BenchmarkCommands.TryGetValue(args[0], out var command)) return false;
        AppLog.Info(command.LogMessage);
        command.Execute();
        return true;
    }

    private static bool TryValidateReleaseNotes(IReadOnlyList<string> args)
    {
        if (args.Count == 0
            || !string.Equals(args[0], "--validate-release-notes", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        _ = ReleaseNotesCatalog.AllEntries;
        _ = ReleaseNotesCatalog.Current;
        AppLog.Info($"Embedded release notes validated for version {ReleaseNotesCatalog.CurrentVersion}.");
        AppLog.Flush();
        return true;
    }
}
