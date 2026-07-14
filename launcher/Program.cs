using System.Diagnostics;
using System.Windows.Forms;

namespace VectorAnimationEngine.Launcher;

internal static class Program
{
    private const string LauncherMutexName = "Local\\Vector2DAnimationEngine.Launcher.Watch";

    private enum WatchExitReason
    {
        Exited,
        Shutdown,
        RestartEditor
    }

    private readonly record struct SourceLaunchOptions(bool ForceSourceLaunch, bool ModuleHotReload)
    {
        public static SourceLaunchOptions Parse(IReadOnlyList<string> args)
        {
            var noHotReload = args.Any(arg => arg.Equals("--no-hot-reload", StringComparison.OrdinalIgnoreCase));
            var developmentMode = args.Any(arg => arg.Equals("--dev", StringComparison.OrdinalIgnoreCase));
            return new SourceLaunchOptions(developmentMode || noHotReload, ModuleHotReload: !noHotReload);
        }
    }

    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        using var launcherMutex = new Mutex(initiallyOwned: true, LauncherMutexName, out var createdNew);
        if (!createdNew) return;

        var root = AppContext.BaseDirectory;
        var logDir = Path.Combine(root, "logs");
        Directory.CreateDirectory(logDir);
        Log(logDir, "Launcher starting");
        var projectPath = Path.Combine(root, "native", "VectorAnimationEngine.Native.csproj");
        var precompiledRuntimePath = Path.Combine(root, "runtime", "win-x64", "VectorAnimationEngine.exe");
        var sourceLaunch = SourceLaunchOptions.Parse(args);
        if (!sourceLaunch.ForceSourceLaunch
            && File.Exists(precompiledRuntimePath)
            && (!File.Exists(projectPath) || IsNativeOutputCurrent(projectPath, Path.GetDirectoryName(precompiledRuntimePath)!)))
        {
            StartPrecompiledRuntime(precompiledRuntimePath, logDir);
            return;
        }

        if (!File.Exists(projectPath))
        {
            Log(logDir, $"Cannot find project: {projectPath}");
            ShowError("Cannot find the precompiled runtime or development project.", projectPath);
            return;
        }

        if (!sourceLaunch.ForceSourceLaunch && File.Exists(precompiledRuntimePath))
        {
            Log(logDir, "Precompiled runtime is older than native source inputs; falling back to the development project.");
        }

        if (!TryFindDotnet(out var dotnet))
        {
            Log(logDir, ".NET SDK was not found in PATH");
            ShowError(".NET SDK was not found in PATH.", "Install .NET SDK 8+ or run the release build instead.");
            return;
        }

        var skipInitialBuild = IsNativeDebugOutputCurrent(projectPath);
        var shutdownEventName = $"Local\\Vector2DAnimationEngine.LauncherShutdown.{Environment.ProcessId}.{Guid.NewGuid():N}";
        var restartEventName = $"Local\\Vector2DAnimationEngine.LauncherRestart.{Environment.ProcessId}.{Guid.NewGuid():N}";
        var nativeLauncherArguments = $" -- --launcher-shutdown-event={shutdownEventName} --launcher-restart-event={restartEventName}";
        var runArguments = $"-c Debug --no-restore{(skipInitialBuild ? " --no-build" : string.Empty)}{nativeLauncherArguments}";
        using var shutdownEvent = new EventWaitHandle(false, EventResetMode.ManualReset, shutdownEventName);
        using var restartEvent = new EventWaitHandle(false, EventResetMode.AutoReset, restartEventName);
        var startInfo = new ProcessStartInfo
        {
            FileName = dotnet,
            Arguments = sourceLaunch.ModuleHotReload
                ? $"watch --non-interactive --project \"{projectPath}\" run {runArguments}"
                : $"run --project \"{projectPath}\" {runArguments}",
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.Environment["V2D_LOG_DIR"] = logDir;
        startInfo.Environment["V2D_DEV_HOT_RELOAD"] = sourceLaunch.ModuleHotReload ? "1" : "0";
        startInfo.Environment["V2D_LAUNCHER_SHUTDOWN_EVENT"] = shutdownEventName;
        startInfo.Environment["V2D_LAUNCHER_RESTART_EVENT"] = restartEventName;
        // Unsupported CLR edits remain opt-in through the preserved-state restart command.
        // Do not let dotnet watch discard an unsaved editor project behind the user's back.
        startInfo.Environment["DOTNET_WATCH_RESTART_ON_RUDE_EDIT"] = "0";
        startInfo.Environment["DOTNET_WATCH_SUPPRESS_LAUNCH_BROWSER"] = "true";

        try
        {
            Log(logDir, $"Launching native app. Module hot reload: {sourceLaunch.ModuleHotReload}. Initial native build skipped: {skipInitialBuild}. Command: {startInfo.FileName} {startInfo.Arguments}");
            while (true)
            {
                using var watchProcess = Process.Start(startInfo);
                if (watchProcess is null) throw new InvalidOperationException("The development watch process could not be started.");
                var reason = WaitForWatchProcessOrRequest(watchProcess, shutdownEvent, restartEvent);
                if (reason == WatchExitReason.RestartEditor)
                {
                    Log(logDir, "Native app requested a development editor-process restart; restarting the watch process tree.");
                    StopProcessTree(watchProcess, logDir);
                    if (!watchProcess.HasExited) watchProcess.WaitForExit(5000);
                    continue;
                }

                if (reason == WatchExitReason.Shutdown)
                {
                    Log(logDir, "Native app requested shutdown; stopping development watch process tree.");
                    StopProcessTree(watchProcess, logDir);
                }

                if (!watchProcess.HasExited) watchProcess.WaitForExit(5000);
                var exitCode = watchProcess.HasExited ? watchProcess.ExitCode : -1;
                Log(logDir, $"Native watch process exited with code {exitCode}.");
                break;
            }
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

    private static bool IsNativeDebugOutputCurrent(string projectPath)
    {
        var nativeDirectory = Path.GetDirectoryName(projectPath);
        return !string.IsNullOrWhiteSpace(nativeDirectory)
            && IsNativeOutputCurrent(projectPath, Path.Combine(nativeDirectory, "bin", "Debug", "net8.0-windows"));
    }

    private static bool IsNativeOutputCurrent(string projectPath, string outputDirectory)
    {
        try
        {
            var nativeDirectory = Path.GetDirectoryName(projectPath);
            if (string.IsNullOrWhiteSpace(nativeDirectory)) return false;

            var outputFiles = new[]
            {
                Path.Combine(outputDirectory, "VectorAnimationEngine.exe"),
                Path.Combine(outputDirectory, "VectorAnimationEngine.dll"),
                Path.Combine(outputDirectory, "VectorAnimationEngine.deps.json"),
                Path.Combine(outputDirectory, "VectorAnimationEngine.runtimeconfig.json")
            };
            if (outputFiles.Any(path => !File.Exists(path))) return false;

            var outputTime = outputFiles.Min(File.GetLastWriteTimeUtc);
            var latestInputTime = File.GetLastWriteTimeUtc(projectPath);
            var rootDirectory = Directory.GetParent(nativeDirectory)?.FullName;
            if (!string.IsNullOrWhiteSpace(rootDirectory))
            {
                var globalJson = Path.Combine(rootDirectory, "global.json");
                if (File.Exists(globalJson)) latestInputTime = Max(latestInputTime, File.GetLastWriteTimeUtc(globalJson));
            }

            foreach (var file in Directory.EnumerateFiles(nativeDirectory, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(nativeDirectory, file);
                if (relativePath.StartsWith($"bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                    || relativePath.StartsWith($"obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                    || !IsNativeBuildInput(file))
                {
                    continue;
                }

                latestInputTime = Max(latestInputTime, File.GetLastWriteTimeUtc(file));
                if (latestInputTime > outputTime) return false;
            }

            return latestInputTime <= outputTime;
        }
        catch
        {
            // Falling back to a normal build is safer than launching stale output.
            return false;
        }
    }

    private static void StartPrecompiledRuntime(string runtimePath, string logDir)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = runtimePath,
            WorkingDirectory = Path.GetDirectoryName(runtimePath)!,
            UseShellExecute = false
        };
        startInfo.Environment["V2D_LOG_DIR"] = logDir;
        startInfo.Environment["V2D_DEV_HOT_RELOAD"] = "0";
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The precompiled native runtime could not be started.");
        Log(logDir, $"Started precompiled ReadyToRun runtime. PID: {process.Id}. Path: {runtimePath}");
        process.Dispose();
    }

    private static bool IsNativeBuildInput(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".props", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".targets", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".resx", StringComparison.OrdinalIgnoreCase);
    }

    private static DateTime Max(DateTime first, DateTime second) => first >= second ? first : second;

    private static WatchExitReason WaitForWatchProcessOrRequest(
        Process watchProcess,
        EventWaitHandle shutdownEvent,
        EventWaitHandle restartEvent)
    {
        while (!watchProcess.HasExited)
        {
            var signal = WaitHandle.WaitAny([shutdownEvent, restartEvent], 250);
            if (signal == 0) return WatchExitReason.Shutdown;
            if (signal == 1) return WatchExitReason.RestartEditor;
        }

        return WatchExitReason.Exited;
    }

    private static void StopProcessTree(Process process, string logDir)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            if (!process.WaitForExit(5000)) Log(logDir, "Timed out while stopping the development watch process tree.");
        }
        catch (Exception ex)
        {
            Log(logDir, $"Failed to stop the development watch process tree: {ex}");
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
