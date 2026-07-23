using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace VectorAnimationEngine;

internal static class AppLog
{
    private static readonly object Sync = new();
    private static string _logPath = string.Empty;
    private static bool _initialized;

    public static string LogPath => _logPath;

    public static void Initialize(string[] args)
    {
        lock (Sync)
        {
            if (_initialized) return;

            var logDir = Environment.GetEnvironmentVariable("V2D_LOG_DIR");
            if (string.IsNullOrWhiteSpace(logDir)) logDir = DefaultLogDirectory();
            Directory.CreateDirectory(logDir);
            PruneOldLogs(logDir);

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
        Write("ERROR", exception is null ? message : $"{message}{Environment.NewLine}{exception}", member);
    }

    public static void Flush()
    {
        lock (Sync)
        {
            if (!_initialized || string.IsNullOrEmpty(_logPath)) return;
            File.AppendAllText(_logPath, string.Empty);
        }
    }

    private static void Write(string level, string message, string member)
    {
        lock (Sync)
        {
            if (!_initialized || string.IsNullOrEmpty(_logPath)) return;

            try
            {
                File.AppendAllText(
                    _logPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [{member}] {message}{Environment.NewLine}");
            }
            catch
            {
                Debug.WriteLine(message);
            }
        }
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
