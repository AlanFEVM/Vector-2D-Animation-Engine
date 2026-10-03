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
            ["--bench-render"] = new("Running stage renderer regression benchmark", Benchmark.RunStageRendererRegression),
            ["--bench-gpu-optics"] = new("Running GPU optical parity benchmark", Benchmark.RunGpuOpticsRegression),
            ["--bench-collision"] = new("Running collision-project performance benchmark", Benchmark.RunCollisionProjectPerformance)
        };

    [STAThread]
    private static void Main(string[] args)
    {
        AppLog.Initialize(args);
        LauncherShutdownSignal.Configure(args);
        InstallCrashHandlers();

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
            var report = CrashReporter.Capture("Fatal application exception", ex, isTerminating: true);
            AppLog.Flush();
            ModernMessageDialog.Show(
                null,
                UiLocalization.CurrentLanguage == UiLanguage.SimplifiedChinese
                    ? $"应用程序已崩溃。诊断报告已保存到：\n\n{(string.IsNullOrEmpty(report) ? AppLog.LogPath : report)}"
                    : $"The application crashed. A diagnostic report was saved to:\n\n{(string.IsNullOrEmpty(report) ? AppLog.LogPath : report)}",
                "Vector 2D Animation Engine",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// Installs every managed crash channel this process can observe. Native access violations and
    /// stack overflows cannot be caught in managed code, so the log and crash-report paths are also
    /// flushed on the way out to leave the best possible trail.
    /// </summary>
    private static void InstallCrashHandlers()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        // UI-thread exceptions: the message loop keeps running, so this is a recoverable crash.
        Application.ThreadException += (_, e) =>
        {
            var report = CrashReporter.Capture("Unhandled UI thread exception", e.Exception, isTerminating: false);
            ReportCrashToUser(e.Exception, report, terminating: false);
        };

        // A background thread exception that nothing handled. This terminates the process.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var exception = e.ExceptionObject as Exception;
            if (exception is null)
            {
                AppLog.Crash(
                    $"Unhandled domain exception with a non-Exception object of type " +
                    $"{e.ExceptionObject?.GetType().FullName ?? "null"}. Terminating: {e.IsTerminating}",
                    exception: null);
                AppLog.Flush();
                return;
            }

            var report = CrashReporter.Capture("Unhandled domain exception", exception, e.IsTerminating);
            AppLog.Flush();
            ReportCrashToUser(exception, report, terminating: e.IsTerminating);
        };

        // A faulted Task whose exception was never observed. Usually not fatal, but it is exactly
        // the kind of silent failure that later surfaces as an unexplained crash.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };

        // Capture the originating stack of exceptions that are later swallowed or rethrown. This is
        // opt-in because first-chance handling is expensive and produces a large volume.
        if (IsEnabled(Environment.GetEnvironmentVariable("V2D_LOG_FIRST_CHANCE_EXCEPTIONS")))
        {
            AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
                AppLog.Warn($"First-chance exception: {AppLog.DescribeException(e.Exception)}");
            AppLog.Info("First-chance exception logging is enabled.");
        }

        AppLog.Info("Crash handlers installed.");
    }

    private static bool IsEnabled(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !value.Equals("0", StringComparison.Ordinal)
        && !value.Equals("false", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Surfaces a crash report path after the log has been flushed. Kept defensive: a failure while
    /// reporting must not replace the original exception or block termination.
    /// </summary>
    private static void ReportCrashToUser(Exception? exception, string reportPath, bool terminating)
    {
        var detail = string.IsNullOrEmpty(reportPath)
            ? AppLog.LogPath
            : reportPath;
        AppLog.Crash($"Crash report available at: {detail}", exception: null);

        if (!terminating) return;
        try
        {
            ModernMessageDialog.Show(
                null,
                UiLocalization.CurrentLanguage == UiLanguage.SimplifiedChinese
                    ? $"应用程序遇到未处理的错误。诊断报告已保存到：\n\n{detail}"
                    : $"The application hit an unhandled error. A diagnostic report was saved to:\n\n{detail}",
                "Vector 2D Animation Engine",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch
        {
            // A terminating path may be unable to show UI; the log already has the report path.
        }
    }

    private static bool TryRunBenchmark(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || !BenchmarkCommands.TryGetValue(args[0], out var command)) return false;
        AppLog.Info(command.LogMessage);
        var priorGpu = Environment.GetEnvironmentVariable("VECTOR_DISABLE_EXPLICIT_GPU");
        if (!string.Equals(args[0], "--bench-gpu-optics", StringComparison.OrdinalIgnoreCase))
            Environment.SetEnvironmentVariable("VECTOR_DISABLE_EXPLICIT_GPU", "1");
        try { command.Execute(); }
        finally { Environment.SetEnvironmentVariable("VECTOR_DISABLE_EXPLICIT_GPU", priorGpu); }
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
