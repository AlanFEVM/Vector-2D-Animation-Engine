using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace VectorAnimationEngine.DistributionLauncher;

internal static class Program
{
    private const int ApplicationInstallFailedExitCode = 11;
    private const int LaunchFailedExitCode = 12;
    private const int RuntimeInstallFailedExitCode = 13;
    private const uint ErrorMessageBox = 0x00000010;

    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var root = AppContext.BaseDirectory;
        if (HasArgument(args, "--validate-single-exe"))
        {
            return EmbeddedApplicationInstaller.HasValidEmbeddedPayload()
                && EmbeddedApplicationInstaller.RunSelfTest()
                && RuntimeInstaller.RunSelfTest()
                    ? 0
                    : ApplicationInstallFailedExitCode;
        }

        if (HasArgument(args, "--validate-package-layout"))
        {
            return ValidateInstalledLayout(root);
        }

        try
        {
            EmbeddedApplicationInstaller.EnsureInstalled(root);
        }
        catch (Exception ex)
        {
            return Fail("软件主体代码缺失", ex, root, ApplicationInstallFailedExitCode, args);
        }

        if (HasArgument(args, "--deploy-embedded-application")) return 0;

        try
        {
            if (!RuntimeInstaller.IsRuntimeValid(Path.Combine(root, ".Runtime")))
            {
                if (HasArgument(args, "--provision-runtime"))
                {
                    RuntimeInstaller.EnsureInstalled(root, progress: null, CancellationToken.None);
                }
                else
                {
                    using var dialog = new RuntimeDownloadDialog(root);
                    if (dialog.ShowDialog() != DialogResult.OK)
                    {
                        var message = string.IsNullOrWhiteSpace(dialog.ErrorMessage)
                            ? "运行环境安装失败。"
                            : dialog.ErrorMessage;
                        throw new InvalidOperationException(message);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            return Fail("运行环境安装失败", ex, root, RuntimeInstallFailedExitCode, args);
        }

        if (HasArgument(args, "--provision-runtime")) return 0;
        return LaunchApplication(root, ForwardedArguments(args));
    }

    private static int ValidateInstalledLayout(string root)
    {
        var applicationDirectory = Path.Combine(root, ".V2DEngine");
        if (!File.Exists(Path.Combine(applicationDirectory, "VectorAnimationEngine.dll"))
            || !File.Exists(Path.Combine(applicationDirectory, "VectorAnimationEngine.deps.json"))
            || !File.Exists(Path.Combine(applicationDirectory, "VectorAnimationEngine.runtimeconfig.json"))
            || !File.Exists(Path.Combine(applicationDirectory, "release.json")))
        {
            Console.Error.WriteLine("软件主体代码缺失");
            return ApplicationInstallFailedExitCode;
        }

        if (!RuntimeInstaller.IsRuntimeValid(Path.Combine(root, ".Runtime")))
        {
            Console.Error.WriteLine("没有运行环境");
            return RuntimeInstallFailedExitCode;
        }
        return 0;
    }

    private static int LaunchApplication(string root, string[] args)
    {
        var runtimePath = Path.Combine(root, ".Runtime", "dotnet.exe");
        var applicationPath = Path.Combine(root, ".V2DEngine", "VectorAnimationEngine.dll");
        var startInfo = new ProcessStartInfo
        {
            FileName = runtimePath,
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            Arguments = string.Join(" ", new[] { applicationPath }.Concat(args).Select(QuoteArgument))
        };
        startInfo.EnvironmentVariables["V2D_LOG_DIR"] = Path.Combine(root, "logs");
        startInfo.EnvironmentVariables["V2D_DEV_HOT_RELOAD"] = "0";

        try
        {
            using var process = Process.Start(startInfo);
            if (process is not null) return 0;
        }
        catch (Exception ex)
        {
            WriteLog(root, "软件启动失败", ex);
        }

        ShowError("软件启动失败");
        return LaunchFailedExitCode;
    }

    private static int Fail(string message, Exception exception, string root, int exitCode, string[] args)
    {
        WriteLog(root, message, exception);
        if (args.Any(arg => arg.StartsWith("--", StringComparison.Ordinal)))
        {
            Console.Error.WriteLine($"{message}: {exception.Message}");
        }
        else
        {
            ShowError($"{message}\r\n\r\n{exception.Message}");
        }
        return exitCode;
    }

    private static void WriteLog(string root, string message, Exception exception)
    {
        try
        {
            var logDirectory = Path.Combine(root, "logs");
            Directory.CreateDirectory(logDirectory);
            File.AppendAllText(
                Path.Combine(logDirectory, "bootstrap.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}{exception}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch
        {
            // Logging must not mask the original startup error.
        }
    }

    private static bool HasArgument(IEnumerable<string> args, string expected) =>
        args.Any(arg => arg.Equals(expected, StringComparison.OrdinalIgnoreCase));

    private static string[] ForwardedArguments(IEnumerable<string> args) => args
        .Where(arg => !arg.Equals("--validate-single-exe", StringComparison.OrdinalIgnoreCase)
            && !arg.Equals("--validate-package-layout", StringComparison.OrdinalIgnoreCase)
            && !arg.Equals("--deploy-embedded-application", StringComparison.OrdinalIgnoreCase)
            && !arg.Equals("--provision-runtime", StringComparison.OrdinalIgnoreCase))
        .ToArray();

    private static string QuoteArgument(string value)
    {
        if (value.Length > 0 && !value.Any(character => char.IsWhiteSpace(character) || character == '"'))
        {
            return value;
        }

        var result = new StringBuilder(value.Length + 2).Append('"');
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }
            if (character == '"')
            {
                result.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }

            result.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }
        result.Append('\\', backslashes * 2).Append('"');
        return result.ToString();
    }

    private static void ShowError(string message)
    {
        MessageBox(IntPtr.Zero, message, "Vector 2D Animation Engine", ErrorMessageBox);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(IntPtr windowHandle, string text, string caption, uint type);
}
