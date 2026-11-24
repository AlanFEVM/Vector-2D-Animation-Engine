namespace VectorAnimationEngine;

internal sealed class OperationPreferencesDialog : ModernDialogForm
{
    private readonly Button _leftToolbar = new SegmentedButton { Text = "Left" };
    private readonly Button _topToolbar = new SegmentedButton { Text = "Top" };
    private readonly ModernNumericUpDown _imageAutoPlaceLimit = new()
    {
        Minimum = 0, Maximum = 1000, DecimalPlaces = 0, Increment = 1,
        Dock = DockStyle.Fill, Margin = new Padding(8, 0, 0, 0),
        AccessibleName = "Image import auto-place limit", MouseWheelChangesValue = false
    };
    public ToolbarPosition SelectedToolbarPosition { get; private set; }
    internal Func<bool>? ApplyRequested { get; set; }
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

    public OperationPreferencesDialog(bool freeTransformShiftProportionalEnabled, ToolbarPosition toolbarPosition = ToolbarPosition.Left,
        int imageImportAutoPlaceLimit = ApplicationSettings.DefaultImageImportAutoPlaceLimit)
        : base("Operation preferences", new Size(620, 480))
    {
        AccessibleName = "Operation preferences";
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 1,
            RowCount = 7,
            Margin = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        DialogContent.Controls.Add(layout);
        var toolbarRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty
        };
        toolbarRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        toolbarRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        toolbarRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        toolbarRow.Controls.Add(new Label
        {
            Text = "Toolbar position", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.Text, Margin = Padding.Empty
        }, 0, 0);
        foreach (var button in new[] { _leftToolbar, _topToolbar })
        {
            button.Dock = DockStyle.Fill;
            button.Margin = new Padding(2, 2, 2, 4);
            button.AccessibleName = button.Text;
        }
        _leftToolbar.Click += (_, _) => SelectToolbarPosition(ToolbarPosition.Left);
        _topToolbar.Click += (_, _) => SelectToolbarPosition(ToolbarPosition.Top);
        toolbarRow.Controls.Add(_leftToolbar, 1, 0);
        toolbarRow.Controls.Add(_topToolbar, 2, 0);
        SelectToolbarPosition(toolbarPosition);
        layout.Controls.Add(toolbarRow, 0, 0);
        var importRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty
        };
        importRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 68));
        importRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
        importRow.Controls.Add(new Label
        {
            Text = "Image import auto-place limit", Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Text, Margin = Padding.Empty
        }, 0, 0);
        _imageAutoPlaceLimit.Value = Math.Clamp(imageImportAutoPlaceLimit, 0, 1000);
        importRow.Controls.Add(_imageAutoPlaceLimit, 1, 0);
        layout.Controls.Add(importRow, 0, 4);
        layout.Controls.Add(new Label
        {
            Text = "Batches above this number stay in the library. Drag selected assets onto the stage to place them. Set 0 to always import into the library only.",
            Dock = DockStyle.Fill, ForeColor = Theme.Muted, Margin = new Padding(0, 6, 0, 0)
        }, 0, 5);
        layout.Controls.Add(new Label
        {
            Text = "Left: vertical toolbar. Top: horizontal toolbar with tool groups opening as a grid.",
            Dock = DockStyle.Fill, ForeColor = Theme.Muted, TextAlign = ContentAlignment.TopLeft,
            Margin = new Padding(0, 6, 0, 0)
        }, 0, 1);
        _freeTransformShiftProportional.Checked = freeTransformShiftProportionalEnabled;
        _freeTransformShiftProportional.AccessibleName = _freeTransformShiftProportional.Text;
        _freeTransformShiftProportional.AccessibleDescription =
            "On (default): scale freely; hold Shift to keep the aspect ratio. Off: scale proportionally; hold Shift to scale freely.";
        layout.Controls.Add(_freeTransformShiftProportional, 0, 2);
        layout.Controls.Add(new Label
        {
            Text = _freeTransformShiftProportional.AccessibleDescription,
            Dock = DockStyle.Fill,
            ForeColor = Theme.Muted,
            BackColor = Theme.Panel,
            TextAlign = ContentAlignment.TopLeft,
            Margin = new Padding(0, 6, 0, 0)
        }, 0, 3);
        layout.Controls.Add(new Label
        {
            Text = "Apply saves these preferences immediately. Cancel leaves them unchanged.",
            Dock = DockStyle.Fill,
            ForeColor = Theme.Muted,
            BackColor = Theme.Panel,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty
        }, 0, 6);
        AcceptButton = AddDialogAction("Apply", DialogResult.OK, DialogActionStyle.Primary,
            () => ApplyRequested?.Invoke() ?? true);
        CancelButton = AddDialogAction("Cancel", DialogResult.Cancel);
        UiLocalization.Watch(this);
    }

    public bool FreeTransformShiftProportionalEnabled => _freeTransformShiftProportional.Checked;
    public int ImageImportAutoPlaceLimit => (int)_imageAutoPlaceLimit.Value;

    private void SelectToolbarPosition(ToolbarPosition position)
    {
        SelectedToolbarPosition = position;
        Theme.StyleSegmentedButton(_leftToolbar, active: position == ToolbarPosition.Left);
        Theme.StyleSegmentedButton(_topToolbar, active: position == ToolbarPosition.Top);
    }
}
