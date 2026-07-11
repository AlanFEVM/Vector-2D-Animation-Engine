namespace VectorAnimationEngine;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        AppLog.Initialize(args);
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

        if (args.Length > 0 && args[0].Equals("--bench", StringComparison.OrdinalIgnoreCase))
        {
            AppLog.Info("Running default benchmark");
            Benchmark.RunDefaultStress();
            return;
        }

        if (args.Length > 0 && args[0].Equals("--bench-freehand", StringComparison.OrdinalIgnoreCase))
        {
            AppLog.Info("Running freehand benchmark");
            Benchmark.RunFreehandStress();
            return;
        }

        if (args.Length > 0 && args[0].Equals("--bench-timeline", StringComparison.OrdinalIgnoreCase))
        {
            AppLog.Info("Running timeline regression benchmark");
            Benchmark.RunTimelineRegression();
            return;
        }

        try
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new AppHost());
            AppLog.Info("Application exited normally");
        }
        catch (Exception ex)
        {
            AppLog.Error("Fatal application exception", ex);
            AppLog.Flush();
            MessageBox.Show(
                $"The application crashed. See the latest log file for details:\n\n{AppLog.LogPath}",
                "Vector 2D Animation Engine",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}
