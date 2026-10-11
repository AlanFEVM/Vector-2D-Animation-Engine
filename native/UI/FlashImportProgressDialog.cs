namespace VectorAnimationEngine;

internal sealed class FlashImportProgressDialog : ModernDialogForm
{
    private bool _completed;
    private readonly Button _cancel;
    private readonly Label _message;
    private bool _saving;
    internal bool CancellationRequested { get; private set; }

    public FlashImportProgressDialog() : base(UiLocalization.T("Import Flash Project"), new Size(560, 260))
    {
        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
            BackColor = Theme.Panel, Margin = Padding.Empty
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        _message = new Label
        {
            Text = UiLocalization.T("Reading and validating the Flash project. Cancel discards the result after parsing finishes."),
            Dock = DockStyle.Fill, ForeColor = Theme.Text, UseMnemonic = false,
            TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty
        };
        content.Controls.Add(_message, 0, 0);
        content.Controls.Add(new ProgressBar
        {
            Dock = DockStyle.Fill, Style = ProgressBarStyle.Marquee,
            AccessibleName = UiLocalization.T("Import progress")
        }, 0, 1);
        DialogContent.Controls.Add(content);
        _cancel = AddDialogAction(UiLocalization.T("Cancel"), DialogResult.Cancel, canClose: () =>
        {
            RequestCancellation();
            return _completed;
        });
        CancelButton = _cancel;
    }

    internal void Complete()
    {
        _completed = true;
        DialogResult = CancellationRequested ? DialogResult.Cancel : DialogResult.OK;
        Close();
    }

    internal void BeginSaving()
    {
        _saving = true;
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
