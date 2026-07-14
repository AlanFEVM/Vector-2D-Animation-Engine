namespace VectorAnimationEngine;

internal sealed class AppHost : ApplicationContext
{
    private MainForm? _mainForm;
    private StartupBannerForm? _startupBanner;
    private EditorRestartState? _restartState;
    private bool _exiting;
    private bool _processRestartRequested;

    public static AppHost? Current { get; private set; }

    public AppHost(EditorRestartState? restartState = null)
    {
        Current = this;
        AppLog.Info("Creating application host");
        _startupBanner = new StartupBannerForm();
        _startupBanner.Show();
        Application.DoEvents();
        ShowMainForm(restartState);
    }

    public static void ReloadModulesForHotReload(Type[]? updatedTypes)
    {
        var host = Current;
        if (host?._mainForm is null || host._mainForm.IsDisposed) return;

        var plan = HotReloadModuleResolver.Resolve(updatedTypes);

        var form = host._mainForm;
        if (form.InvokeRequired)
        {
            form.BeginInvoke(() => host.ReloadModules(plan));
            return;
        }

        host.ReloadModules(plan);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CloseStartupBanner();
        }
        if (Current == this) Current = null;
        base.Dispose(disposing);
    }

    private void ShowMainForm(EditorRestartState? restartState = null)
    {
        var form = restartState is null
            ? new VectorAnimationEngine.MainForm()
            : VectorAnimationEngine.MainForm.CreateForRestart(restartState);
        AppLog.Info("Showing main form");
        MainForm = form;
        _mainForm = form;
        form.Shown += (_, _) =>
        {
            _startupBanner?.DismissWhenReady();
        };
        form.RestartRequested += MainFormRestartRequested;
        form.ProcessRestartRequested += MainFormProcessRestartRequested;
        form.FormClosed += MainFormClosed;
        form.Show();
        _startupBanner?.ShowAbove(form);
    }

    private void MainFormRestartRequested(object? sender, EditorRestartRequestedEventArgs e)
    {
        var form = _mainForm;
        if (_exiting || form is null || !ReferenceEquals(sender, form)) return;
        _restartState = e.State;
        // Detach ApplicationContext's default close-to-exit handler before
        // closing the old window; MainFormClosed creates the replacement.
        MainForm = null;
        form.Close();
    }

    private void MainFormProcessRestartRequested(object? sender, EventArgs e)
    {
        var form = _mainForm;
        if (_exiting || form is null || !ReferenceEquals(sender, form)) return;
        _processRestartRequested = true;
        MainForm = null;
        form.Close();
    }

    private void ReloadModules(HotReloadPlan plan)
    {
        if (_mainForm is null || _mainForm.IsDisposed) return;
        AppLog.Info($"Applying module hot reload: {plan.Modules}; types: {plan.UpdatedTypes}");
        if (plan.RequiresWorkbenchRebuild)
        {
            _mainForm.RebuildWorkbenchForHotReload(plan);
            return;
        }

        _mainForm.ReloadModulesForHotReload(plan);
    }

    private void MainFormClosed(object? sender, FormClosedEventArgs e)
    {
        AppLog.Info($"Main form closed. Reason: {e.CloseReason}");
        if (_exiting) return;

        if (_processRestartRequested)
        {
            _processRestartRequested = false;
            _exiting = true;
            CloseStartupBanner();
            _mainForm = null;
            ExitThread();
            return;
        }

        if (_restartState is { } restartState)
        {
            _restartState = null;
            _mainForm = null;
            ShowMainForm(restartState);
            return;
        }

        _exiting = true;
        if (e.CloseReason != CloseReason.ApplicationExitCall) LauncherShutdownSignal.NotifyLauncher();
        CloseStartupBanner();
        _mainForm = null;
        ExitThread();
    }

    private void CloseStartupBanner()
    {
        if (_startupBanner is null) return;
        if (!_startupBanner.IsDisposed) _startupBanner.Close();
        _startupBanner.Dispose();
        _startupBanner = null;
    }
}
