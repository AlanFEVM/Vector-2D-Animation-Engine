namespace VectorAnimationEngine;

internal static class MenuLayoutDiagnostics
{
    private const string ProbeFilePath = @"D:\Vector 2D Animation Engine\artifacts\screenshots\menu-probe.txt";

    internal static bool Enabled => Environment.GetEnvironmentVariable("V2D_MENU_PROBE") == "1";

    internal static void Write(string message)
    {
        if (!Enabled) return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ProbeFilePath)!);
            using var writer = new StreamWriter(ProbeFilePath, true);
            writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Could not write the menu layout probe: {exception.Message}");
        }
    }
}
