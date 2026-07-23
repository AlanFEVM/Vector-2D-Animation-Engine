using System.Diagnostics;
using System.Text.Json;
using System.Windows.Forms;

namespace VectorAnimationEngine.Launcher;

internal static class Program
{
    private const string LauncherMutexName = "Local\\Vector2DAnimationEngine.Launcher.Watch";
    private const int MaxUnexpectedWatchRestarts = 3;
    private const long MaxLauncherLogBytes = 8L * 1024 * 1024;
    private const int RestartTokenSidecarVersion = 1;
    private const long MaxRestartTokenSidecarBytes = 512;
    private static readonly TimeSpan RestartTokenLifetime = TimeSpan.FromMinutes(5);
    private const string RestartTokenArgumentPrefix = "--editor-restart-token=";
    private static readonly object LogSync = new();
    private static long _launcherLogBytes = -1;

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
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
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
            Log(logDir, $"Launching native app. Module hot reload: {sourceLaunch.ModuleHotReload}. Initial native build skipped: {skipInitialBuild}.");
            var unexpectedRestarts = 0;
            string? pendingRestartToken = null;
            while (true)
            {
                var startedUtc = DateTime.UtcNow;
                var launchToken = pendingRestartToken;
                pendingRestartToken = null;
                var tokenArgument = launchToken is null ? "" : $" {RestartTokenArgumentPrefix}{launchToken}";
                startInfo.Arguments = sourceLaunch.ModuleHotReload
                    ? $"watch --non-interactive --project \"{projectPath}\" run {runArguments}{tokenArgument}"
                    : $"run --project \"{projectPath}\" {runArguments}{tokenArgument}";
                using var watchProcess = Process.Start(startInfo);
                if (watchProcess is null) throw new InvalidOperationException("The development watch process could not be started.");
                AttachWatchLogging(watchProcess, logDir);
                var reason = WaitForWatchProcessOrRequest(watchProcess, shutdownEvent, restartEvent);
                if (reason == WatchExitReason.RestartEditor)
                {
                    pendingRestartToken = TryConsumeEditorRestartToken(logDir);
                    Log(logDir, "Native app requested a development editor-process restart; restarting the watch process tree.");
                    StopProcessTree(watchProcess, logDir);
                    WaitForExitAndDrain(watchProcess, logDir);
                    continue;
                }

                if (reason == WatchExitReason.Shutdown)
                {
                    Log(logDir, "Native app requested shutdown; stopping development watch process tree.");
                    StopProcessTree(watchProcess, logDir);
                }

                WaitForExitAndDrain(watchProcess, logDir);
                var exitCode = watchProcess.HasExited ? watchProcess.ExitCode : -1;
                Log(logDir, $"Native watch process exited with code {exitCode}.");
                var ranFor = DateTime.UtcNow - startedUtc;
                if (reason == WatchExitReason.Exited
                    && exitCode != 0
                    && unexpectedRestarts < MaxUnexpectedWatchRestarts)
                {
                    if (ranFor >= TimeSpan.FromSeconds(30)) unexpectedRestarts = 0;
                    unexpectedRestarts++;
                    var delayMilliseconds = Math.Min(2000, 250 * (1 << (unexpectedRestarts - 1)));
                    Log(logDir, $"Restarting the failed watch process in {delayMilliseconds} ms ({unexpectedRestarts}/{MaxUnexpectedWatchRestarts}).");
                    Thread.Sleep(delayMilliseconds);
                    continue;
                }
                break;
            }
        }
        catch (Exception ex)
        {
            Log(logDir, $"Launch failed: {ex}");
            ShowError("Failed to launch the development app.", ex.Message);
        }
    }

    private static string? TryConsumeEditorRestartToken(string logDir)
    {
        var tokenPath = Path.Combine(
            Path.GetTempPath(),
            "Vector2DAnimationEngine",
            "editor-restart-token.json");
        try
        {
            if (!File.Exists(tokenPath))
            {
                Log(logDir, "The editor restart request did not include a token sidecar; restarting without state recovery.");
                return null;
            }
            RejectRestartTokenReparsePoint(Path.GetDirectoryName(tokenPath)!);
            RejectRestartTokenReparsePoint(tokenPath);
            if (new FileInfo(tokenPath).Length > MaxRestartTokenSidecarBytes)
            {
                throw new InvalidDataException("The editor restart token sidecar exceeds its supported size.");
            }

            var sidecar = JsonSerializer.Deserialize<EditorRestartTokenFile>(File.ReadAllText(tokenPath));
            var nowUtc = DateTimeOffset.UtcNow;
            if (sidecar is null
                || sidecar.Version != RestartTokenSidecarVersion
                || !IsCanonicalRestartToken(sidecar.Token)
                || sidecar.ExpiresUtc.Offset != TimeSpan.Zero
                || sidecar.ExpiresUtc <= nowUtc
                || sidecar.ExpiresUtc > nowUtc.Add(RestartTokenLifetime))
            {
                throw new InvalidDataException("The editor restart token sidecar is invalid or expired.");
            }

            Log(logDir, "Accepted a one-shot editor restart token for the next native process.");
            return sidecar.Token;
        }
        catch (Exception ex)
        {
            Log(logDir, $"Unable to accept the editor restart token; restarting without state recovery: {ex.Message}");
            return null;
        }
        finally
        {
            DeleteRestartTokenSidecar(tokenPath, logDir);
        }
    }

    private static bool IsCanonicalRestartToken(string? value)
    {
        return Guid.TryParseExact(value, "N", out var token)
            && string.Equals(token.ToString("N"), value, StringComparison.Ordinal);
    }

    private static void RejectRestartTokenReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The editor restart token sidecar cannot be a filesystem reparse point.");
        }
    }

    private static void DeleteRestartTokenSidecar(string tokenPath, string logDir)
    {
        foreach (var path in new[] { tokenPath, tokenPath + ".tmp" })
        {
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                {
                    RejectRestartTokenReparsePoint(directory);
                }
                if (File.Exists(path)) RejectRestartTokenReparsePoint(path);
                File.Delete(path);
            }
            catch (Exception ex)
            {
                Log(logDir, $"Unable to delete the one-shot editor restart token sidecar: {ex.Message}");
            }
        }
    }

    private sealed class EditorRestartTokenFile
    {
        public int Version { get; init; }
        public string Token { get; init; } = "";
        public DateTimeOffset ExpiresUtc { get; init; }
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
            if (!process.WaitForExit(2500))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return false;
            }
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
                foreach (var rootBuildInput in new[]
                {
                    "global.json",
                    "Directory.Build.props",
                    "Directory.Build.targets",
                    "NuGet.Config"
                })
                {
                    var inputPath = Path.Combine(rootDirectory, rootBuildInput);
                    if (File.Exists(inputPath)) latestInputTime = Max(latestInputTime, File.GetLastWriteTimeUtc(inputPath));
                }
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

    private static void WaitForExitAndDrain(Process process, string logDir)
    {
        if (!process.HasExited && !process.WaitForExit(5000))
        {
            Log(logDir, "Timed out while waiting for the development watch process to exit.");
            return;
        }

        // The parameterless wait completes asynchronous stdout/stderr handlers.
        process.WaitForExit();
    }

    private static void AttachWatchLogging(Process process, string logDir)
    {
        process.OutputDataReceived += (_, e) => LogWatchLine(logDir, "OUT", e.Data);
        process.ErrorDataReceived += (_, e) => LogWatchLine(logDir, "ERR", e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    private static void LogWatchLine(string logDir, string stream, string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        Log(logDir, $"[watch:{stream}] {message}");
    }

    private static void ShowError(string title, string detail)
    {
        var simplifiedChinese = UseSimplifiedChinese();
        MessageBox.Show(
            $"{(simplifiedChinese ? TranslateLauncherError(title) : title)}\n\n{(simplifiedChinese ? TranslateLauncherError(detail) : detail)}",
            simplifiedChinese ? "Vector 2D Animation Engine 启动器" : "Vector 2D Animation Engine Launcher",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }

    private static bool UseSimplifiedChinese()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Vector2DAnimationEngine",
                "settings.json");
            if (!File.Exists(path)) return false;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("Language", out var language)) return false;
            return language.ValueKind switch
            {
                JsonValueKind.Number => language.TryGetInt32(out var value) && value == 1,
                JsonValueKind.String => string.Equals(language.GetString(), "SimplifiedChinese", StringComparison.OrdinalIgnoreCase),
                _ => false
            };
        }
        catch
        {
            return false;
        }
    }

    private static string TranslateLauncherError(string text)
    {
        return text switch
        {
            "Cannot find the precompiled runtime or development project." => "找不到预编译运行时或开发项目。",
            ".NET SDK was not found in PATH." => "在 PATH 中找不到 .NET SDK。",
            "Install .NET SDK 8+ or run the release build instead." => "请安装 .NET SDK 8 或更高版本，或改用发布版本。",
            "Failed to launch the development app." => "无法启动开发版本。",
            _ => text
        };
    }

    private static void Log(string logDir, string message)
    {
        lock (LogSync)
        {
            try
            {
                var path = Path.Combine(logDir, "launcher.log");
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}";
                if (_launcherLogBytes < 0) _launcherLogBytes = File.Exists(path) ? new FileInfo(path).Length : 0;
                var lineBytes = System.Text.Encoding.UTF8.GetByteCount(line);
                if (_launcherLogBytes + lineBytes > MaxLauncherLogBytes)
                {
                    var previousPath = Path.Combine(logDir, "launcher.previous.log");
                    if (File.Exists(path)) File.Move(path, previousPath, overwrite: true);
                    _launcherLogBytes = 0;
                }
                File.AppendAllText(path, line);
                _launcherLogBytes += lineBytes;
            }
            catch
            {
                // Launcher logging must not block startup or error reporting.
            }
        }
    }
}
