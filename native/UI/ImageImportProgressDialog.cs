namespace VectorAnimationEngine;

internal sealed class ImageImportProgressDialog : ModernDialogForm
{
    private readonly ProgressBar _progress = new() { Style = ProgressBarStyle.Continuous };
    private readonly Label _status = new();
    private readonly Label _fileName = new();
    private readonly Button _cancelButton;
    private readonly int _total;
    private bool _completed;

    public ImageImportProgressDialog(int total)
        : base(UiLocalization.T("Importing images"), new Size(560, 270))
    {
        _total = Math.Max(1, total);
        AccessibleName = UiLocalization.T("Importing images");
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 1,
            RowCount = 4,
            Margin = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        ConfigureLabel(_status, Theme.Text);
        ConfigureLabel(_fileName, Theme.Muted);
        layout.Controls.Add(_status, 0, 0);
        layout.Controls.Add(_fileName, 0, 1);
        _progress.Dock = DockStyle.Fill;
        _progress.Margin = new Padding(0, 5, 0, 5);
        _progress.Maximum = _total;
        _progress.AccessibleName = UiLocalization.T("Import progress");
        layout.Controls.Add(_progress, 0, 2);
        var hint = new Label();
        ConfigureLabel(hint, Theme.Muted);
        hint.Text = UiLocalization.T("Cancel stops after the current file. Imported images are kept.");
        layout.Controls.Add(hint, 0, 3);
        DialogContent.Controls.Add(layout);
        _cancelButton = AddDialogAction(UiLocalization.T("Cancel"), DialogResult.Cancel,
            canClose: () =>
            {
                RequestCancellation();
                return _completed;
            });
        CancelButton = _cancelButton;
        Report(0, "");
    }

    public bool CancellationRequested { get; private set; }

    public void Report(int completed, string fileName)
    {
        _progress.Value = Math.Clamp(completed, 0, _total);
        _status.Text = string.Format(UiLocalization.T("{0} / {1} images ({2:P0})"),
            completed, _total, completed / (double)_total);
        _fileName.Text = fileName;
    }

    public void Complete()
    {
        _completed = true;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void RequestCancellation()
    {
        if (_completed || CancellationRequested) return;
        CancellationRequested = true;
        _cancelButton.Enabled = false;
        _cancelButton.Text = UiLocalization.T("Stopping...");
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

    private static void ConfigureLabel(Label label, Color color)
    {
        label.Dock = DockStyle.Fill;
        label.ForeColor = color;
        label.Margin = Padding.Empty;
        label.AutoEllipsis = true;
        label.UseMnemonic = false;
        label.TextAlign = ContentAlignment.MiddleLeft;
    }
}
