using System.Diagnostics;
using System.Runtime.InteropServices;
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
    private const string ValidateDevelopmentLauncherArgument = "--validate-development-launcher";
    private const int SwShow = 5;
    private const int SwRestore = 9;
    private static readonly object LogSync = new();
    private static long _launcherLogBytes = -1;

    private enum WatchExitReason
    {
        Exited,
        Shutdown,
        RestartEditor
    }

    private readonly record struct LaunchOptions(bool ModuleHotReload)
    {
        public static LaunchOptions Parse(IReadOnlyList<string> args)
        {
            var noHotReload = args.Any(arg => arg.Equals("--no-hot-reload", StringComparison.OrdinalIgnoreCase));
            return new LaunchOptions(ModuleHotReload: !noHotReload);
        }
    }

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Any(arg => arg.Equals(ValidateDevelopmentLauncherArgument, StringComparison.OrdinalIgnoreCase)))
        {
            Environment.ExitCode = ValidateDevelopmentLauncher();
            return;
        }

        ApplicationConfiguration.Initialize();
        var root = ResolveRepositoryRoot();

        using var launcherMutex = new Mutex(initiallyOwned: true, LauncherMutexName, out var createdNew);
        if (!createdNew)
        {
            if (!TryActivateNativeMainWindow(root)) ShowStartupAlreadyInProgress();
            return;
        }

        var logDir = Path.Combine(root, "logs");
        Directory.CreateDirectory(logDir);
        Log(logDir, $"Launcher starting. Repository root: {root}");
        var projectPath = Path.Combine(root, "native", "VectorAnimationEngine.Native.csproj");
        var launchOptions = LaunchOptions.Parse(args);
        using var startupStatus = new StartupStatusForm(UseSimplifiedChinese());
        startupStatus.Show();
        startupStatus.Activate();
        Application.DoEvents();

        if (!File.Exists(projectPath))
        {
            Log(logDir, $"Cannot find project: {projectPath}");
            DismissStartupStatus(startupStatus);
            ShowError("Cannot find the development project.", projectPath);
            return;
        }

        if (!TryFindDotnet(out var dotnet))
        {
            Log(logDir, ".NET SDK was not found in PATH");
            DismissStartupStatus(startupStatus);
            ShowError(".NET SDK was not found in PATH.", "Install .NET SDK 8+ or run the release build instead.");
            return;
        }

        var skipInitialBuild = IsNativeDebugOutputCurrent(projectPath);
        startupStatus.SetBuildRequired(!skipInitialBuild);
        Application.DoEvents();
        var shutdownEventName = $"Local\\Vector2DAnimationEngine.LauncherShutdown.{Environment.ProcessId}.{Guid.NewGuid():N}";
        var restartEventName = $"Local\\Vector2DAnimationEngine.LauncherRestart.{Environment.ProcessId}.{Guid.NewGuid():N}";
        var nativeLauncherArguments = $" -- --launcher-shutdown-event={shutdownEventName} --launcher-restart-event={restartEventName}";
        var runArguments = $"-c Debug{(skipInitialBuild ? " --no-build" : string.Empty)}{nativeLauncherArguments}";
        using var shutdownEvent = new EventWaitHandle(false, EventResetMode.ManualReset, shutdownEventName);
        using var restartEvent = new EventWaitHandle(false, EventResetMode.AutoReset, restartEventName);
        var startInfo = new ProcessStartInfo
        {
            FileName = dotnet,
            Arguments = launchOptions.ModuleHotReload
                ? $"watch --non-interactive --project \"{projectPath}\" run {runArguments}"
                : $"run --project \"{projectPath}\" {runArguments}",
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.Environment["V2D_LOG_DIR"] = logDir;
        startInfo.Environment["V2D_DEV_HOT_RELOAD"] = launchOptions.ModuleHotReload ? "1" : "0";
        startInfo.Environment["V2D_LAUNCHER_SHUTDOWN_EVENT"] = shutdownEventName;
        startInfo.Environment["V2D_LAUNCHER_RESTART_EVENT"] = restartEventName;
        // Unsupported CLR edits remain opt-in through the preserved-state restart command.
        // Do not let dotnet watch discard an unsaved editor project behind the user's back.
        startInfo.Environment["DOTNET_WATCH_RESTART_ON_RUDE_EDIT"] = "0";
        startInfo.Environment["DOTNET_WATCH_SUPPRESS_LAUNCH_BROWSER"] = "true";

        try
        {
            Log(logDir, $"Launching native app. Module hot reload: {launchOptions.ModuleHotReload}. Initial native build skipped: {skipInitialBuild}.");
            var unexpectedRestarts = 0;
            string? pendingRestartToken = null;
            while (true)
            {
                var startedUtc = DateTime.UtcNow;
                var launchToken = pendingRestartToken;
                pendingRestartToken = null;
                var tokenArgument = launchToken is null ? "" : $" {RestartTokenArgumentPrefix}{launchToken}";
                startInfo.Arguments = launchOptions.ModuleHotReload
                    ? $"watch --non-interactive --project \"{projectPath}\" run {runArguments}{tokenArgument}"
                    : $"run --project \"{projectPath}\" {runArguments}{tokenArgument}";
                using var watchProcess = Process.Start(startInfo);
                if (watchProcess is null) throw new InvalidOperationException("The development watch process could not be started.");
                AttachWatchLogging(watchProcess, logDir);
                var reason = WaitForWatchProcessOrRequest(watchProcess, shutdownEvent, restartEvent, root, startupStatus);
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
                    WaitWithStartupStatus(delayMilliseconds, root, startupStatus);
                    continue;
                }
                break;
            }
        }
        catch (Exception ex)
        {
            DismissStartupStatus(startupStatus);
            Log(logDir, $"Launch failed: {ex}");
            ShowError("Failed to launch the development app.", ex.Message);
        }
        finally
        {
            DismissStartupStatus(startupStatus);
        }
    }

    private static int ValidateDevelopmentLauncher()
    {
        var root = ResolveRepositoryRoot();
        var projectPath = Path.Combine(root, "native", "VectorAnimationEngine.Native.csproj");
        return LaunchOptions.Parse(Array.Empty<string>()).ModuleHotReload
            && File.Exists(projectPath)
            && TryFindDotnet(out _)
                ? 0
                : 1;
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

    private static string ResolveRepositoryRoot()
    {
        foreach (var startPath in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(startPath);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "native", "VectorAnimationEngine.Native.csproj")))
                {
                    return directory.FullName;
                }
                directory = directory.Parent;
            }
        }

        return Environment.CurrentDirectory;
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

            var managedAssemblyPath = Path.Combine(outputDirectory, "VectorAnimationEngine.dll");
            var outputFiles = new[]
            {
                Path.Combine(outputDirectory, "VectorAnimationEngine.exe"),
                managedAssemblyPath,
                Path.Combine(outputDirectory, "VectorAnimationEngine.deps.json"),
                Path.Combine(outputDirectory, "VectorAnimationEngine.runtimeconfig.json")
            };
            if (outputFiles.Any(path => !File.Exists(path))) return false;

            // Incremental builds can retain older deps/runtimeconfig files while refreshing the managed assembly.
            var outputTime = File.GetLastWriteTimeUtc(managedAssemblyPath);
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
        EventWaitHandle restartEvent,
        string root,
        StartupStatusForm startupStatus)
    {
        while (!watchProcess.HasExited)
        {
            PumpStartupStatus(root, startupStatus);
            var signal = WaitHandle.WaitAny([shutdownEvent, restartEvent], 100);
            if (signal == 0) return WatchExitReason.Shutdown;
            if (signal == 1) return WatchExitReason.RestartEditor;
        }

        PumpStartupStatus(root, startupStatus);
        return WatchExitReason.Exited;
    }

    private static void WaitWithStartupStatus(int delayMilliseconds, string root, StartupStatusForm startupStatus)
    {
        var end = Environment.TickCount64 + delayMilliseconds;
        while (true)
        {
            var remaining = end - Environment.TickCount64;
            if (remaining <= 0) break;
            PumpStartupStatus(root, startupStatus);
            Thread.Sleep((int)Math.Min(50, remaining));
        }
    }

    private static void PumpStartupStatus(string root, StartupStatusForm startupStatus)
    {
        if (startupStatus.IsDisposed) return;
        Application.DoEvents();
        if (!TryGetNativeMainWindow(root, out _)) return;
        DismissStartupStatus(startupStatus);
    }

    private static void DismissStartupStatus(StartupStatusForm startupStatus)
    {
        if (startupStatus.IsDisposed) return;
        startupStatus.Dismiss();
        Application.DoEvents();
    }

    private static bool TryActivateNativeMainWindow(string root)
    {
        if (!TryGetNativeMainWindow(root, out var windowHandle)) return false;
        ShowWindow(windowHandle, IsIconic(windowHandle) ? SwRestore : SwShow);
        SetForegroundWindow(windowHandle);
        return true;
    }

    private static bool TryGetNativeMainWindow(string root, out IntPtr windowHandle)
    {
        windowHandle = IntPtr.Zero;
        var expectedPath = Path.GetFullPath(Path.Combine(
            root,
            "native",
            "bin",
            "Debug",
            "net8.0-windows",
            "VectorAnimationEngine.exe"));
        foreach (var process in Process.GetProcessesByName("VectorAnimationEngine"))
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId) continue;
                    process.Refresh();
                    var handle = process.MainWindowHandle;
                    if (handle == IntPtr.Zero
                        || !IsWindowVisible(handle)
                        || string.IsNullOrWhiteSpace(process.MainWindowTitle)
                        || !string.Equals(process.MainModule?.FileName, expectedPath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    windowHandle = handle;
                    return true;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Processes can exit while their window and executable path are being inspected.
                }
            }
        }

        return false;
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

    private static void ShowStartupAlreadyInProgress()
    {
        var simplifiedChinese = UseSimplifiedChinese();
        MessageBox.Show(
            simplifiedChinese
                ? "开发版本正在编译或启动，请稍候。"
                : "The development app is compiling or starting. Please wait.",
            simplifiedChinese ? "Vector 2D Animation Engine 启动器" : "Vector 2D Animation Engine Launcher",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
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
            "Cannot find the development project." => "找不到开发项目。",
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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr windowHandle, int command);

    private sealed class StartupStatusForm : Form
    {
        private readonly bool _simplifiedChinese;
        private readonly Label _detail;
        private bool _dismissRequested;

        public StartupStatusForm(bool simplifiedChinese)
        {
            _simplifiedChinese = simplifiedChinese;
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Color.FromArgb(30, 33, 37);
            ClientSize = new Size(420, 132);
            ControlBox = false;
            Font = SystemFonts.MessageBoxFont;
            ForeColor = Color.FromArgb(235, 238, 242);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowIcon = false;
            StartPosition = FormStartPosition.CenterScreen;
            Text = simplifiedChinese
                ? "Vector 2D Animation Engine 启动器"
                : "Vector 2D Animation Engine Launcher";

            var title = new Label
            {
                AccessibleName = simplifiedChinese ? "启动状态" : "Startup status",
                AutoEllipsis = true,
                Font = SystemFonts.CaptionFont,
                ForeColor = ForeColor,
                Location = new Point(24, 20),
                Size = new Size(372, 24),
                Text = "Vector 2D Animation Engine",
                TextAlign = ContentAlignment.MiddleLeft
            };
            _detail = new Label
            {
                AutoEllipsis = true,
                ForeColor = Color.FromArgb(178, 186, 196),
                Location = new Point(24, 49),
                Size = new Size(372, 35),
                Text = simplifiedChinese ? "正在准备开发环境..." : "Preparing the development environment...",
                TextAlign = ContentAlignment.MiddleLeft
            };
            var progress = new ProgressBar
            {
                AccessibleName = simplifiedChinese ? "启动进度" : "Startup progress",
                Location = new Point(24, 94),
                MarqueeAnimationSpeed = 24,
                Size = new Size(372, 12),
                Style = ProgressBarStyle.Marquee
            };
            Controls.Add(title);
            Controls.Add(_detail);
            Controls.Add(progress);
        }

        public void SetBuildRequired(bool buildRequired)
        {
            _detail.Text = _simplifiedChinese
                ? buildRequired
                    ? "正在编译并启动编辑器，首次启动可能需要一些时间..."
                    : "正在启动编辑器..."
                : buildRequired
                    ? "Building and starting the editor. The first launch may take a moment..."
                    : "Starting the editor...";
        }

        public void Dismiss()
        {
            if (IsDisposed) return;
            _dismissRequested = true;
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_dismissRequested && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                return;
            }

            base.OnFormClosing(e);
        }
    }
}
