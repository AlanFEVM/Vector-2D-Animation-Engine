namespace VectorAnimationEngine;

internal static class Program
{
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

        if (TryRunBenchmark(args)) return;

        if (!SingleInstanceLease.TryAcquire(out var instanceLease))
        {
            AppLog.Info("A native application instance is already active; skipping duplicate startup.");
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
                    var restartToken = EditorRestartStore.GetRequestedToken(args);
                    EditorRestartStore.TryConsume(restartToken, out var restartState);
                    Application.Run(new AppHost(restartState));
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
}
