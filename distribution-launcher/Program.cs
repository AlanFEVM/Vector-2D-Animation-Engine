using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VectorAnimationEngine.DistributionLauncher;

internal static class Program
{
    private const int MissingRuntimeExitCode = 10;
    private const int MissingApplicationExitCode = 11;
    private const int LaunchFailedExitCode = 12;
    private const uint ErrorMessageBox = 0x00000010;
    private const string ValidateLayoutArgument = "--validate-package-layout";

    private enum LayoutError
    {
        None,
        MissingRuntime,
        MissingApplication
    }

    [STAThread]
    private static int Main(string[] args)
    {
        var root = AppContext.BaseDirectory;
        var layoutError = ValidateLayout(root);
        var validationOnly = args.Any(arg =>
            arg.Equals(ValidateLayoutArgument, StringComparison.OrdinalIgnoreCase));
        if (layoutError != LayoutError.None)
        {
            var message = ErrorMessage(layoutError);
            if (validationOnly) Console.Error.WriteLine(message);
            else ShowError(message);
            return layoutError == LayoutError.MissingRuntime
                ? MissingRuntimeExitCode
                : MissingApplicationExitCode;
        }
        if (validationOnly) return 0;

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
        catch
        {
            // The package layout was valid, but the private runtime could not start the application.
        }

        ShowError("软件启动失败");
        return LaunchFailedExitCode;
    }

    private static LayoutError ValidateLayout(string root)
    {
        var runtimeDirectory = Path.Combine(root, ".Runtime");
        if (!File.Exists(Path.Combine(runtimeDirectory, "dotnet.exe"))
            || !ContainsRuntimeFile(Path.Combine(runtimeDirectory, "host", "fxr"), "hostfxr.dll")
            || !ContainsRuntimeFile(Path.Combine(runtimeDirectory, "shared", "Microsoft.NETCore.App"), "coreclr.dll")
            || !ContainsRuntimeFile(Path.Combine(runtimeDirectory, "shared", "Microsoft.WindowsDesktop.App"), "System.Windows.Forms.dll"))
        {
            return LayoutError.MissingRuntime;
        }

        var applicationDirectory = Path.Combine(root, ".V2DEngine");
        return File.Exists(Path.Combine(applicationDirectory, "VectorAnimationEngine.dll"))
            && File.Exists(Path.Combine(applicationDirectory, "VectorAnimationEngine.deps.json"))
            && File.Exists(Path.Combine(applicationDirectory, "VectorAnimationEngine.runtimeconfig.json"))
                ? LayoutError.None
                : LayoutError.MissingApplication;
    }

    private static bool ContainsRuntimeFile(string versionRoot, string fileName)
    {
        try
        {
            return Directory.Exists(versionRoot)
                && Directory.EnumerateDirectories(versionRoot)
                    .Any(directory => IsCompatibleRuntimeVersion(directory)
                        && File.Exists(Path.Combine(directory, fileName)));
        }
        catch
        {
            return false;
        }
    }

    private static bool IsCompatibleRuntimeVersion(string directory)
    {
        return Version.TryParse(Path.GetFileName(directory), out var version) && version.Major == 8;
    }

    private static string ErrorMessage(LayoutError error) => error switch
    {
        LayoutError.MissingRuntime => "没有运行环境",
        LayoutError.MissingApplication => "软件主体代码缺失",
        _ => "软件启动失败"
    };

    private static string QuoteArgument(string value)
    {
        if (value.Length > 0 && !value.Any(character => char.IsWhiteSpace(character) || character == '"'))
        {
            return value;
        }

        var result = new System.Text.StringBuilder(value.Length + 2).Append('"');
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
