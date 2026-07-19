namespace VectorAnimationEngine;

internal sealed class AppHost : ApplicationContext
{
    private MainForm? _mainForm;
    private StartupBannerForm? _startupBanner;
    private EditorRestartState? _restartState;
    private bool _exiting;
    private bool _processRestartRequested;
    private readonly HotReloadCoordinator _hotReloadCoordinator;

    public static AppHost? Current { get; private set; }

    public AppHost(EditorRestartState? restartState = null)
    {
        Current = this;
        _hotReloadCoordinator = new HotReloadCoordinator(() => _mainForm, ApplyHotReloadBatch);
        AppLog.Info("Creating application host");
        _startupBanner = new StartupBannerForm();
        _startupBanner.Show();
        Application.DoEvents();
        ShowMainForm(restartState);
    }

    public static void EnqueueHotReload(HotReloadPlan plan)
    {
        var host = Current;
        if (host is null || host._exiting) return;
        if (plan.RequiresProcessRestart
            && (host._mainForm is null
                || host._mainForm.IsDisposed
                || !host._mainForm.IsHandleCreated)
            && host.TryRequestDetachedEditorProcessRestart())
        {
            return;
        }
        host._hotReloadCoordinator.Enqueue(plan);
    }

    private bool TryRequestDetachedEditorProcessRestart()
    {
        var form = Application.OpenForms
            .OfType<MainForm>()
            .FirstOrDefault(candidate => !candidate.IsDisposed && candidate.IsHandleCreated);
        if (form is null)
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            form = Control.FromHandle(process.MainWindowHandle) as MainForm;
        }
        if (form is null) return false;

        void SaveAndRestart()
        {
            try
            {
                var state = form.CaptureEditorRestartStateForRecovery();
                if (!EditorRestartStore.TrySave(state)
                    || !LauncherShutdownSignal.RequestEditorRestart())
                {
                    AppLog.Error("Unable to restart the detached editor after a failed in-process hot reload.");
                    return;
                }

                _exiting = true;
                AppLog.Info("Requested a state-preserving process restart for the detached editor window.");
            }
            catch (Exception ex)
            {
                AppLog.Error("Unable to capture the detached editor before a process restart", ex);
            }
        }

        try
        {
            if (form.InvokeRequired) form.BeginInvoke(SaveAndRestart);
            else SaveAndRestart();
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            AppLog.Error("Unable to dispatch detached-editor hot-reload recovery", ex);
            return false;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hotReloadCoordinator.Dispose();
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

    private void ApplyHotReloadBatch(HotReloadBatch batch)
    {
        var form = _mainForm;
        if (_exiting || form is null || form.IsDisposed) return;
        var plan = batch.Plan;
        var queuedMilliseconds = Math.Max(0, (DateTime.UtcNow - batch.QueuedUtc).TotalMilliseconds);
        AppLog.Info($"Applying hot reload generation {batch.Generation} after {queuedMilliseconds:0} ms: {plan.Modules}; types: {plan.UpdatedTypes}");
        form.SetHotReloadStatus(HotReloadUiState.Applying, batch.Generation);

        var accepted = false;
        try
        {
            accepted = plan.RequiresProcessRestart
                ? form.RequestProcessRestartForHotReload()
                : plan.RequiresWorkbenchRebuild
                ? form.RebuildWorkbenchForHotReload(plan)
                : form.ReloadModulesForHotReload(plan);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Hot reload generation {batch.Generation} failed before recovery could be scheduled", ex);
        }
        if (accepted)
        {
            if (!plan.RequiresProcessRestart && !plan.RequiresWorkbenchRebuild)
            {
                form.SetHotReloadStatus(HotReloadUiState.Applied, batch.Generation);
            }
            AppLog.Info(plan.RequiresProcessRestart
                ? $"Hot reload generation {batch.Generation} requested a state-preserving process restart."
                : $"Hot reload generation {batch.Generation} applied successfully.");
            return;
        }

        AppLog.Warn($"Hot reload generation {batch.Generation} requested recovery after a module refresh failure.");
        form = _mainForm;
        if (form is null || form.IsDisposed) return;
        form.SetHotReloadStatus(HotReloadUiState.Recovering, batch.Generation);
        var recoveryPlan = new HotReloadPlan(HotReloadModule.All, plan.UpdatedTypes);
        try
        {
            if (form.RebuildWorkbenchForHotReload(recoveryPlan)) return;
            if (form.RequestProcessRestartForHotReload()) return;
        }
        catch (Exception ex)
        {
            AppLog.Error($"Hot reload generation {batch.Generation} recovery failed", ex);
        }

        form.SetHotReloadStatus(HotReloadUiState.Failed, batch.Generation);
        AppLog.Error($"Hot reload generation {batch.Generation} could not recover through a workbench or process restart.");
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
            _mainForm = null;
            try
            {
                ShowMainForm(restartState);
                _restartState = null;
            }
            catch (Exception ex)
            {
                AppLog.Error("The in-process editor restart failed; escalating to a state-preserving process restart", ex);
                if (EditorRestartStore.TrySave(restartState)
                    && LauncherShutdownSignal.RequestEditorRestart())
                {
                    _restartState = null;
                    _exiting = true;
                    CloseStartupBanner();
                    ExitThread();
                    return;
                }

                _restartState = null;
                _exiting = true;
                CloseStartupBanner();
                ExitThread();
            }
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
