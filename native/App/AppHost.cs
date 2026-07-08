namespace VectorAnimationEngine;

internal sealed class AppHost : ApplicationContext
{
    private MainForm? _mainForm;

    public static AppHost? Current { get; private set; }

    public AppHost()
    {
        Current = this;
        ShowMainForm();
    }

    public static void ReloadMainFormForHotReload()
    {
        var host = Current;
        if (host?._mainForm is null || host._mainForm.IsDisposed) return;

        var form = host._mainForm;
        if (form.InvokeRequired)
        {
            form.BeginInvoke(host.ReloadMainForm);
            return;
        }

        host.ReloadMainForm();
    }

    protected override void Dispose(bool disposing)
    {
        if (Current == this) Current = null;
        base.Dispose(disposing);
    }

    private void ShowMainForm(Rectangle? bounds = null, FormWindowState windowState = FormWindowState.Normal)
    {
        var form = new MainForm();
        if (bounds is { } nextBounds)
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Bounds = nextBounds;
            form.WindowState = windowState;
        }

        MainForm = form;
        _mainForm = form;
        form.FormClosed += MainFormClosed;
        form.Show();
    }

    private void ReloadMainForm()
    {
        if (_mainForm is null || _mainForm.IsDisposed) return;

        var oldForm = _mainForm;
        var bounds = oldForm.WindowState == FormWindowState.Normal ? oldForm.Bounds : oldForm.RestoreBounds;
        var windowState = oldForm.WindowState;
        oldForm.FormClosed -= MainFormClosed;
        ShowMainForm(bounds, windowState);
        oldForm.Close();
        oldForm.Dispose();
    }

    private void MainFormClosed(object? sender, FormClosedEventArgs e)
    {
        ExitThread();
    }
}
