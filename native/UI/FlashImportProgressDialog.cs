namespace VectorAnimationEngine;

internal sealed class FlashImportProgressDialog : ModernDialogForm, IProgress<ProjectSaveProgress>, IProgress<FlashParseProgress>, IProgress<ProjectLoadProgress>
{
    private bool _completed;
    private readonly ProgressBar _progress = new()
    {
        Dock = DockStyle.Fill, Style = ProgressBarStyle.Marquee,
        AccessibleName = UiLocalization.T("Import progress")
    };
    private readonly System.Windows.Forms.Timer _progressTimer = new() { Interval = 100 };
    private ProjectLoadProgress? _latestProgress;
    private readonly System.Diagnostics.Stopwatch _elapsed = System.Diagnostics.Stopwatch.StartNew();
    private readonly Label _details = new() { Dock = DockStyle.Fill, ForeColor = Theme.Text,
        AutoEllipsis = true, UseMnemonic = false, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _elapsedLabel = new() { Dock = DockStyle.Fill, ForeColor = Theme.Text,
        UseMnemonic = false, TextAlign = ContentAlignment.MiddleLeft };

    // Worker threads only publish data. A UI timer coalesces thousands of
    // parallel file reports without flooding the WinForms message queue.
    public void Report(ProjectSaveProgress value) => Report(new ProjectLoadProgress(value.Stage, value.Completed, value.Total, value.Item));

    public void Report(ProjectLoadProgress value) => Interlocked.Exchange(ref _latestProgress, value);

    public void Report(FlashParseProgress value) => Report(new ProjectSaveProgress(value.Stage, value.Completed, value.Total, value.Item));

    private void RefreshSaveProgress()
    {
        var value = Interlocked.Exchange(ref _latestProgress, null);
        if (_completed) return;
        _elapsedLabel.Text = UiLocalization.T("Elapsed time") + $": {_elapsed.Elapsed:hh\\:mm\\:ss}";
        if (value is null) return;
        var count = value.Total > 0 ? $" ({value.Completed:N0}/{value.Total:N0})  {Math.Clamp((double)value.Completed / value.Total, 0, 1):P0}" : "";
        _message.Text = UiLocalization.T(value.Stage) + count;
        _details.Text = string.Join("\r\n", new[] { value.Item, UiLocalization.T(value.Operation), value.RelativePath }
            .Where(part => !string.IsNullOrEmpty(part)));
        _progress.Style = value.Total > 0 ? ProgressBarStyle.Continuous : ProgressBarStyle.Marquee;
        if (value.Total > 0)
        {
            _progress.Maximum = value.Total;
            _progress.Value = Math.Clamp(value.Completed, 0, value.Total);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _progressTimer.Dispose();
        base.Dispose(disposing);
    }
    private readonly Button _cancel;
    private readonly Label _message;
    private bool _saving;
    internal bool CancellationRequested { get; private set; }

    public FlashImportProgressDialog() : this(false) { }

    internal FlashImportProgressDialog(bool openingProject)
        : base(UiLocalization.T(openingProject ? "Open Project" : "Import Flash Project"), new Size(660, 340))
    {
        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4,
            BackColor = Theme.Panel, Margin = Padding.Empty
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        _message = new Label
        {
            Text = UiLocalization.T(openingProject
                ? "Reading and validating the project. Cancel keeps the current project after loading finishes."
                : "Reading and validating the Flash project. Cancel discards the result after parsing finishes."),
            Dock = DockStyle.Fill, ForeColor = Theme.Text, UseMnemonic = false,
            TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Margin = Padding.Empty
        };
        content.Controls.Add(_message, 0, 0);
        content.Controls.Add(_details, 0, 1);
        content.Controls.Add(_elapsedLabel, 0, 2);
        content.Controls.Add(_progress, 0, 3);
        _progressTimer.Tick += (_, _) => RefreshSaveProgress();
        DialogContent.Controls.Add(content);
        _progressTimer.Start();
        _cancel = AddDialogAction(UiLocalization.T("Cancel"), DialogResult.Cancel, canClose: () =>
        {
            RequestCancellation();
            return _completed;
        });
        CancelButton = _cancel;
    }

    internal void Complete()
    {
        _progressTimer.Stop();
        _completed = true;
        DialogResult = CancellationRequested ? DialogResult.Cancel : DialogResult.OK;
        Close();
    }

    internal void BeginSaving()
    {
        _saving = true;
        _progressTimer.Start();
        _message.Text = UiLocalization.T("Creating the V2D project in the selected folder. Please wait until saving finishes.");
        _cancel.Enabled = false;
    }

    private void RequestCancellation()
    {
        if (_completed || _saving) return;
        CancellationRequested = true;
        _cancel.Enabled = false;
        _cancel.Text = UiLocalization.T("Stopping...");
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_completed)
        {
            RequestCancellation();
            e.Cancel = true;
            DialogResult = DialogResult.None;
        }
        base.OnFormClosing(e);
    }
}
