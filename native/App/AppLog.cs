using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace VectorAnimationEngine;

internal static class AppLog
{
    private const int MaxRecentEntryCount = 200;
    private static readonly object Sync = new();
    private static readonly Queue<string> BufferedEntries = new();
    private static string _logPath = string.Empty;
    private static string _logDirectory = string.Empty;
    private static bool _initialized;

    public static string LogPath => _logPath;
    public static string LogDirectory => _logDirectory;
    public static bool IsInitialized => _initialized;

    /// <summary>
    /// The most recent log lines, used to attach immediate pre-crash context to a crash report
    /// without re-reading a log file that may still be buffered by the operating system.
    /// </summary>
    public static IReadOnlyList<string> RecentEntries
    {
        get
        {
            lock (Sync)
            {
                return BufferedEntries.ToArray();
            }
        }
    }

    public static void Initialize(string[] args)
    {
        lock (Sync)
        {
            if (_initialized) return;

            var logDir = Environment.GetEnvironmentVariable("V2D_LOG_DIR");
            if (string.IsNullOrWhiteSpace(logDir)) logDir = DefaultLogDirectory();
            Directory.CreateDirectory(logDir);
            PruneOldLogs(logDir);

            _logDirectory = logDir;
            _logPath = Path.Combine(logDir, $"native-{DateTime.Now:yyyyMMdd-HHmmss}-pid{Environment.ProcessId}.log");
            _initialized = true;
            Info("Application starting");
            Info($"Log file: {_logPath}");
            Info($"Process: {Environment.ProcessPath}");
            Info($"Base directory: {AppContext.BaseDirectory}");
            Info($"Working directory: {Environment.CurrentDirectory}");
            Info($"Runtime: {Environment.Version}");
            Info($"OS: {Environment.OSVersion}");
            Info($"Args: {string.Join(' ', args)}");
        }
    }

    public static void Info(string message, [CallerMemberName] string member = "") => Write("INFO", message, member);

    public static void Warn(string message, [CallerMemberName] string member = "") => Write("WARN", message, member);

    public static void Error(string message, Exception? exception = null, [CallerMemberName] string member = "")
    {
        Write("ERROR", exception is null ? message : $"{message}{Environment.NewLine}{DescribeException(exception)}", member);
    }

    /// <summary>
    /// Records an exception with its full inner-exception chain and stack trace. Used by crash
    /// handlers, where losing the nested cause is the difference between a fixable and an opaque bug.
    /// </summary>
    public static void Crash(string message, Exception? exception, [CallerMemberName] string member = "")
    {
        var detail = new StringBuilder(message);
        if (exception is not null)
        {
            detail.Append(Environment.NewLine).Append(DescribeException(exception));
        }

        Write("FATAL", detail.ToString(), member);
    }

    public static void Flush()
    {
        lock (Sync)
        {
            if (!_initialized || string.IsNullOrEmpty(_logPath)) return;
            try
            {
                using var stream = new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                stream.Flush(flushToDisk: true);
            }
            catch
            {
                // Flushing is best-effort; the crash handler still reports the log path.
            }
        }
    }

    /// <summary>
    /// Builds a printable description of an exception including every inner exception and every
    /// stack trace, because <see cref="Exception.ToString"/> drops inner stack traces for aggregate
    /// cases and cannot be re-read after a process is gone.
    /// </summary>
    internal static string DescribeException(Exception exception)
    {
        var builder = new StringBuilder();
        var depth = 0;
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (depth > 0) builder.Append(Environment.NewLine);
            var indent = new string(' ', depth * 2);
            builder.Append(indent)
                .Append(depth == 0 ? "" : "Inner: ")
                .Append(current.GetType().FullName)
                .Append(": ")
                .Append(current.Message);

            var hResult = current.HResult;
            if (hResult != 0) builder.Append($" (HRESULT: 0x{hResult:X8})");

            if (current is AggregateException aggregate)
            {
                var index = 0;
                foreach (var inner in aggregate.InnerExceptions)
                {
                    builder.Append(Environment.NewLine)
                        .Append(indent)
                        .Append($"  Aggregate[{index++}]: ")
                        .Append(inner.GetType().FullName)
                        .Append(": ")
                        .Append(inner.Message);
                }
            }

            if (!string.IsNullOrWhiteSpace(current.StackTrace))
            {
                builder.Append(Environment.NewLine).Append(indent).Append(current.StackTrace!.TrimEnd());
            }

            depth++;
            if (depth > 12) break;
        }

        return builder.ToString();
    }

    private static void Write(string level, string message, string member)
    {
        lock (Sync)
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [t{Environment.CurrentManagedThreadId}] [{member}] {message}";
            Remember(line);
            if (!_initialized || string.IsNullOrEmpty(_logPath)) return;

            try
            {
                // FileShare.ReadWrite keeps the log readable by a crash-report tool or support
                // session while the editor is still running.
                using var stream = new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                var bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
                stream.Write(bytes, 0, bytes.Length);
            }
            catch
            {
                Debug.WriteLine(message);
            }
        }
    }

    private static void Remember(string line)
    {
        BufferedEntries.Enqueue(line);
        while (BufferedEntries.Count > MaxRecentEntryCount) BufferedEntries.Dequeue();
    }

    private static void PruneOldLogs(string logDir)
    {
        try
        {
            var files = Directory.GetFiles(logDir, "*.log")
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.CreationTimeUtc)
                .Skip(60);

            foreach (var file in files) file.Delete();
        }
        catch
        {
            // Logging must never prevent the application from starting.
        }
    }

    private static string DefaultLogDirectory()
    {
        var baseDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var baseName = Path.GetFileName(baseDirectory);
        var logRoot = string.Equals(baseName, ".V2DEngine", StringComparison.OrdinalIgnoreCase)
            ? Directory.GetParent(baseDirectory)?.FullName ?? baseDirectory
            : baseDirectory;
        return Path.Combine(logRoot, "logs");
    }
}
