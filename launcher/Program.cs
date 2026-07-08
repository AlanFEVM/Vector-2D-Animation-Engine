using System.Diagnostics;
using System.Windows.Forms;

namespace VectorAnimationEngine.Launcher;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        var root = AppContext.BaseDirectory;
        var logDir = Path.Combine(root, "logs");
        Directory.CreateDirectory(logDir);
        Log(logDir, "Launcher starting");
        var projectPath = Path.Combine(root, "native", "VectorAnimationEngine.Native.csproj");
        if (!File.Exists(projectPath))
        {
            Log(logDir, $"Cannot find project: {projectPath}");
            ShowError("Cannot find the development project.", projectPath);
            return;
        }

        if (!TryFindDotnet(out var dotnet))
        {
            Log(logDir, ".NET SDK was not found in PATH");
            ShowError(".NET SDK was not found in PATH.", "Install .NET SDK 8+ or run the release build instead.");
            return;
        }

        var hotReload = !args.Any(arg => arg.Equals("--no-hot-reload", StringComparison.OrdinalIgnoreCase));
        var startInfo = new ProcessStartInfo
        {
            FileName = dotnet,
            Arguments = hotReload
                ? $"watch --project \"{projectPath}\" run -c Debug --no-restore"
                : $"run --project \"{projectPath}\" -c Debug --no-restore",
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.Environment["V2D_LOG_DIR"] = logDir;
        startInfo.Environment["V2D_DEV_HOT_RELOAD"] = hotReload ? "1" : "0";
        startInfo.Environment["DOTNET_WATCH_RESTART_ON_RUDE_EDIT"] = "true";
        startInfo.Environment["DOTNET_WATCH_SUPPRESS_LAUNCH_BROWSER"] = "true";

        try
        {
            Log(logDir, $"Launching native app. Hot reload: {hotReload}. Command: {startInfo.FileName} {startInfo.Arguments}");
            Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            Log(logDir, $"Launch failed: {ex}");
            ShowError("Failed to launch the development app.", ex.Message);
        }
    }

    private static bool TryFindDotnet(out string dotnet)
    {
        dotnet = "dotnet";
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = dotnet,
                Arguments = "--version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process is null) return false;
            process.WaitForExit(2500);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void ShowError(string title, string detail)
    {
        MessageBox.Show(
            $"{title}\n\n{detail}",
            "Vector 2D Animation Engine Launcher",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }

    private static void Log(string logDir, string message)
    {
        try
        {
            var path = Path.Combine(logDir, "launcher.log");
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // Launcher logging must not block startup or error reporting.
        }
    }
}
