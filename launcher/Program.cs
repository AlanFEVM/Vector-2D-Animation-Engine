using System.Diagnostics;
using System.Windows.Forms;

namespace VectorAnimationEngine.Launcher;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        var root = AppContext.BaseDirectory;
        var projectPath = Path.Combine(root, "native", "VectorAnimationEngine.Native.csproj");
        if (!File.Exists(projectPath))
        {
            ShowError("Cannot find the development project.", projectPath);
            return;
        }

        if (!TryFindDotnet(out var dotnet))
        {
            ShowError(".NET SDK was not found in PATH.", "Install .NET SDK 8+ or run the release build instead.");
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = dotnet,
            Arguments = $"run --project \"{projectPath}\" -c Debug --no-restore",
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            Process.Start(startInfo);
        }
        catch (Exception ex)
        {
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
}
