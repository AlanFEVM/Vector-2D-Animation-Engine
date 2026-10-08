namespace VectorAnimationEngine;

internal sealed class OperationPreferencesDialog : ModernDialogForm
{
    private readonly ModernToggleSwitch _freeTransformShiftProportional = new()
    {
        Text = "Use Shift for proportional scaling",
        Dock = DockStyle.Fill,
        AutoSize = false,
        Margin = Padding.Empty
    };

    public OperationPreferencesDialog() : this(true)
    {
    }

    public OperationPreferencesDialog(bool freeTransformShiftProportionalEnabled)
        : base("Operation preferences", new Size(620, 270))
    {
        AccessibleName = "Operation preferences";
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        DialogContent.Controls.Add(layout);
        _freeTransformShiftProportional.Checked = freeTransformShiftProportionalEnabled;
        _freeTransformShiftProportional.AccessibleName = _freeTransformShiftProportional.Text;
        _freeTransformShiftProportional.AccessibleDescription =
            "On (default): scale freely; hold Shift to keep the aspect ratio. Off: scale proportionally; hold Shift to scale freely.";
        layout.Controls.Add(_freeTransformShiftProportional, 0, 0);
        layout.Controls.Add(new Label
        {
            Text = _freeTransformShiftProportional.AccessibleDescription,
            Dock = DockStyle.Fill,
            ForeColor = Theme.Muted,
            BackColor = Theme.Panel,
            TextAlign = ContentAlignment.TopLeft,
            Margin = new Padding(0, 6, 0, 0)
        }, 0, 1);
        layout.Controls.Add(new Label
        {
            Text = "Confirm changes here, then save in Settings.",
            Dock = DockStyle.Fill,
            ForeColor = Theme.Muted,
            BackColor = Theme.Panel,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty
        }, 0, 2);
        AcceptButton = AddDialogAction("OK", DialogResult.OK, DialogActionStyle.Primary);
        CancelButton = AddDialogAction("Cancel", DialogResult.Cancel);
        UiLocalization.Watch(this);
    }

    public bool FreeTransformShiftProportionalEnabled => _freeTransformShiftProportional.Checked;
}
