using System.Runtime.InteropServices;
using System.Text;

namespace VectorAnimationEngine;

/// <summary>
/// Writes a self-contained crash report next to the regular logs. A report captures process,
/// runtime, graphics, project and recent-log context so a crash that happened on another machine
/// can be diagnosed without access to that machine.
/// </summary>
internal static class CrashReporter
{
    private static readonly object Sync = new();
    private static string _reportPath = string.Empty;

    /// <summary>
    /// Records a crash and writes the forensic report. Returns the report path, or an empty string
    /// when a report could not be written.
    /// </summary>
    internal static string Capture(string source, Exception? exception, bool isTerminating)
    {
        // Re-entrancy is possible: a crash handler can itself throw, and the UI thread handler can
        // fire while a domain handler is already reporting. The first report is authoritative and
        // every later event is appended to the log only.
        lock (Sync)
        {
            if (!string.IsNullOrEmpty(_reportPath))
            {
                AppLog.Crash($"Additional crash event after the report was written, from '{source}'", exception);
                AppLog.Flush();
                return _reportPath;
            }

            try
            {
                AppLog.Crash($"{source}. Terminating: {isTerminating}", exception);
                AppLog.Flush();

                _reportPath = WriteReport(source, exception, isTerminating);
                return _reportPath;
            }
            catch
            {
                // Crash reporting must never mask the original failure.
                return "";
            }
        }
    }

    internal static string FindLatestReportPath()
    {
        if (!string.IsNullOrEmpty(_reportPath)) return _reportPath;
        try
        {
            var directory = ReportsDirectory;
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return "";
            return Directory.GetFiles(directory, "crash-*.txt")
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Select(file => file.FullName)
                .FirstOrDefault() ?? "";
        }
        catch
        {
            return "";
        }
    }

    internal static string ReportsDirectory => string.IsNullOrEmpty(AppLog.LogDirectory)
        ? ""
        : Path.Combine(AppLog.LogDirectory, "crashes");

    private static string WriteReport(string source, Exception? exception, bool isTerminating)
    {
        var directory = ReportsDirectory;
        if (string.IsNullOrEmpty(directory)) return "";
        Directory.CreateDirectory(directory);
        var path = Path.Combine(
            directory,
            $"crash-{DateTime.Now:yyyyMMdd-HHmmss}-pid{Environment.ProcessId}.txt");

        var builder = new StringBuilder();
        builder.AppendLine("Vector 2D Animation Engine crash report");
        builder.AppendLine("=======================================");
        builder.AppendLine($"Captured (local):   {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff zzz}");
        builder.AppendLine($"Captured (UTC):     {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}");
        builder.AppendLine($"Source:             {source}");
        builder.AppendLine($"Terminating:        {isTerminating}");
        builder.AppendLine();

        builder.AppendLine("-- Process --");
        builder.AppendLine($"Process id:         {Environment.ProcessId}");
        builder.AppendLine($"Process path:       {Environment.ProcessPath}");
        builder.AppendLine($"Base directory:     {AppContext.BaseDirectory}");
        builder.AppendLine($"Working directory:  {Environment.CurrentDirectory}");
        builder.AppendLine($"Command line:       {Environment.CommandLine}");
        builder.AppendLine($"Managed threads:    {Environment.ProcessorCount} logical processors");
        builder.AppendLine($"Is 64-bit process:  {Environment.Is64BitProcess}");
        builder.AppendLine($"OS:                 {RuntimeInformation.OSDescription}");
        builder.AppendLine($"OS architecture:    {RuntimeInformation.OSArchitecture}");
        builder.AppendLine($"Runtime:            {RuntimeInformation.FrameworkDescription}");
        builder.AppendLine($"CLR version:        {Environment.Version}");
        builder.AppendLine($"Uptime:             {FormatUptime()}");
        builder.AppendLine($"Memory (working):   {FormatBytes(Environment.WorkingSet)}");
        builder.AppendLine($"GC heap:            {FormatBytes(GC.GetTotalMemory(forceFullCollection: false))}");

        AppendLoadedAssemblies(builder);

        builder.AppendLine();
        builder.AppendLine("-- Exception --");
        builder.AppendLine(exception is null ? "(no exception object)" : AppLog.DescribeException(exception));

        builder.AppendLine();
        builder.AppendLine($"-- Recent user actions ({SessionBreadcrumbs.Count}) --");
        builder.AppendLine(SessionBreadcrumbs.Format());

        AppendRecentLog(builder);

        File.WriteAllText(path, builder.ToString());
        return path;
    }

    private static void AppendLoadedAssemblies(StringBuilder builder)
    {
        builder.AppendLine();
        builder.AppendLine("-- Modules --");
        try
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var name = assembly.GetName();
                    builder.AppendLine($"{name.Name} {name.Version}");
                }
                catch
                {
                    // A dynamic or partially loaded assembly can fail to report its name.
                }
            }
        }
        catch
        {
            builder.AppendLine("(module list unavailable)");
        }
    }

    private static void AppendRecentLog(StringBuilder builder)
    {
        builder.AppendLine();
        builder.AppendLine($"-- Recent log lines (last {AppLog.RecentEntries.Count}) --");
        foreach (var entry in AppLog.RecentEntries) builder.AppendLine(entry);
        builder.AppendLine();
        builder.AppendLine($"Full log: {AppLog.LogPath}");
    }

    private static string FormatUptime()
    {
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            var uptime = DateTime.Now - process.StartTime;
            return $"{uptime:hh\\:mm\\:ss}";
        }
        catch
        {
            return "unknown";
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.0} KiB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):0.0} MiB";
        return $"{bytes / (1024.0 * 1024 * 1024):0.00} GiB";
    }
}
