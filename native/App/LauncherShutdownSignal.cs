namespace VectorAnimationEngine;

internal static class LauncherShutdownSignal
{
    private const string EventVariable = "V2D_LAUNCHER_SHUTDOWN_EVENT";
    private const string EventArgumentPrefix = "--launcher-shutdown-event=";
    private const string RestartEventVariable = "V2D_LAUNCHER_RESTART_EVENT";
    private const string RestartEventArgumentPrefix = "--launcher-restart-event=";
    private const string ReadyEventVariable = "V2D_LAUNCHER_READY_EVENT";
    private const string ReadyEventArgumentPrefix = "--launcher-ready-event=";
    private static string? _eventName;
    private static string? _restartEventName;
    private static string? _readyEventName;

    public static void Configure(IReadOnlyList<string> arguments)
    {
        _eventName = arguments
            .LastOrDefault(argument => argument.StartsWith(EventArgumentPrefix, StringComparison.OrdinalIgnoreCase))?
            .Substring(EventArgumentPrefix.Length);
        _restartEventName = arguments
            .LastOrDefault(argument => argument.StartsWith(RestartEventArgumentPrefix, StringComparison.OrdinalIgnoreCase))?
            .Substring(RestartEventArgumentPrefix.Length);
        _readyEventName = arguments
            .LastOrDefault(argument => argument.StartsWith(ReadyEventArgumentPrefix, StringComparison.OrdinalIgnoreCase))?
            .Substring(ReadyEventArgumentPrefix.Length);
    }

    public static void NotifyLauncher()
    {
        var eventName = _eventName;
        if (string.IsNullOrWhiteSpace(eventName)) eventName = Environment.GetEnvironmentVariable(EventVariable);
        if (string.IsNullOrWhiteSpace(eventName))
        {
            AppLog.Info("No development launcher shutdown signal was configured.");
            return;
        }

        try
        {
            using var shutdownEvent = EventWaitHandle.OpenExisting(eventName);
            shutdownEvent.Set();
            AppLog.Info("Notified the development launcher about native application shutdown.");
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The launcher already exited or this run was started without it.
        }
        catch (Exception ex)
        {
            AppLog.Error("Unable to notify the development launcher about application shutdown", ex);
        }
    }

    public static bool RequestEditorRestart()
    {
        var eventName = _restartEventName;
        if (string.IsNullOrWhiteSpace(eventName)) eventName = Environment.GetEnvironmentVariable(RestartEventVariable);
        if (string.IsNullOrWhiteSpace(eventName)) return false;

        try
        {
            using var restartEvent = EventWaitHandle.OpenExisting(eventName);
            restartEvent.Set();
            AppLog.Info("Requested a development editor-process restart from the launcher.");
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch (Exception ex)
        {
            AppLog.Error("Unable to request a development editor-process restart", ex);
            return false;
        }
    }

    public static void NotifyMainWindowReady()
    {
        var eventName = _readyEventName;
        if (string.IsNullOrWhiteSpace(eventName)) eventName = Environment.GetEnvironmentVariable(ReadyEventVariable);
        if (string.IsNullOrWhiteSpace(eventName)) return;

        try
        {
            using var readyEvent = EventWaitHandle.OpenExisting(eventName);
            readyEvent.Set();
            AppLog.Info($"Notified the development launcher that the main window is ready. PID: {Environment.ProcessId}.");
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The launcher already exited or this run was started without it.
        }
        catch (Exception ex)
        {
            AppLog.Error("Unable to notify the development launcher that the main window is ready", ex);
        }
    }
}
