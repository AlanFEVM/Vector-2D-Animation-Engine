namespace VectorAnimationEngine;

internal sealed class DrawSettingsPanel : UserControl
{
    private readonly DrawSettings _settings;
    private readonly ComboBox _shape = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ToolTip _toolTip = new();
    private readonly SvgToggleButton _keepRatio = Toggle("Ratio", SvgIconKind.Ratio);
    private readonly ModernSlider _freehandSmoothing = new();
    private readonly Label _freehandSmoothingValue = new();
    private readonly Panel _eraserOptions = new();
    private readonly CheckBox _eraseLines = EraserOption("Erase Lines");
    private readonly CheckBox _eraseFills = EraserOption("Erase Fills");
    private bool _updating;

    public DrawSettingsPanel(DrawSettings settings)
    {
        _settings = settings;
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Font = Theme.UiFont();
        Padding = new Padding(0, 8, 0, 8);
        MinimumSize = new Size(248, 152);
        Theme.StyleToolTip(_toolTip);

        BuildUi();
        ReadSettings();
        _settings.Changed += (_, _) => ReadSettings();
    }

    public void SetEraserOptionsVisible(bool visible)
    {
        if (_eraserOptions.Visible == visible) return;
        _eraserOptions.Visible = visible;
        Height = visible ? 204 : 164;
        MinimumSize = new Size(248, visible ? 192 : 152);
        PerformLayout();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _toolTip.Dispose();
        base.Dispose(disposing);
    }

    private void BuildUi()
    {
        var title = new Label
        {
            Text = "Draw Settings",
            Dock = DockStyle.Top,
            Height = 28,
            ForeColor = Theme.Text,
            BackColor = Theme.Panel,
            Font = Theme.UiFont(10, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        };
        Controls.Add(title);

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 2,
            RowCount = 3,
            Padding = new Padding(0, 4, 0, 0)
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        Controls.Add(content);
        content.BringToFront();

        _eraserOptions.Dock = DockStyle.Bottom;
        _eraserOptions.Height = 40;
        _eraserOptions.BackColor = Theme.Panel;
        _eraserOptions.Padding = new Padding(0, 4, 0, 4);
        _eraserOptions.Visible = false;
        var eraseLabel = new Label
        {
            Text = "Erase",
            Dock = DockStyle.Left,
            Width = 104,
            ForeColor = Theme.Muted,
            BackColor = Theme.Panel,
            Font = Theme.UiFont(),
            TextAlign = ContentAlignment.MiddleLeft
        };
        var eraseControls = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = Padding.Empty,
            Margin = Padding.Empty
        };
        eraseControls.Controls.Add(_eraseLines);
        eraseControls.Controls.Add(_eraseFills);
        _eraserOptions.Controls.Add(eraseControls);
        _eraserOptions.Controls.Add(eraseLabel);
        Controls.Add(_eraserOptions);
        _eraserOptions.BringToFront();

        _shape.Items.AddRange([
            ShapeKind.Rectangle.ToString(),
            ShapeKind.Ellipse.ToString(),
            ShapeKind.Triangle.ToString(),
            ShapeKind.Polygon.ToString(),
            ShapeKind.Star.ToString(),
            ShapeKind.Line.ToString()
        ]);
        _shape.Dock = DockStyle.Fill;
        _shape.Margin = new Padding(0, 3, 0, 3);
        Theme.StyleComboBox(_shape);
        _shape.SelectedIndexChanged += (_, _) => UpdateSettings();
        AddField(content, "Shape", _shape, 0);

        _eraseLines.CheckedChanged += (_, _) => UpdateSettings();
        _eraseFills.CheckedChanged += (_, _) => UpdateSettings();

        AddCheck(_keepRatio);
        _keepRatio.Width = 32;
        _keepRatio.Height = 32;
        _keepRatio.Dock = DockStyle.Left;
        _keepRatio.Margin = new Padding(0, 2, 0, 2);
        _toolTip.SetToolTip(_keepRatio, _keepRatio.AccessibleName);
        AddField(content, "Aspect", _keepRatio, 1, fillInput: false);

        _freehandSmoothing.Minimum = 0;
        _freehandSmoothing.Maximum = 100;
        _freehandSmoothing.TickFrequency = 20;
        _freehandSmoothing.Dock = DockStyle.Fill;
        _freehandSmoothing.Margin = Padding.Empty;
        _freehandSmoothing.ValueChanged += (_, _) =>
        {
            _freehandSmoothingValue.Text = $"{_freehandSmoothing.Value}%";
            UpdateSettings();
        };
        _freehandSmoothingValue.Dock = DockStyle.Fill;
        _freehandSmoothingValue.ForeColor = Theme.Muted;
        _freehandSmoothingValue.BackColor = Theme.Panel;
        _freehandSmoothingValue.TextAlign = ContentAlignment.MiddleCenter;
        _freehandSmoothingValue.Font = Theme.UiFont();
        var smoothingRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty
        };
        smoothingRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        smoothingRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48));
        smoothingRow.Controls.Add(_freehandSmoothing, 0, 0);
        smoothingRow.Controls.Add(_freehandSmoothingValue, 1, 0);
        AddField(content, "Smoothing", smoothingRow, 2);
    }

    private void ReadSettings()
    {
        _updating = true;
        _shape.SelectedItem = _settings.ShapeKind.ToString();
        _keepRatio.Checked = _settings.KeepAspectRatio;
        _freehandSmoothing.Value = _settings.FreehandSmoothing;
        _freehandSmoothingValue.Text = $"{_settings.FreehandSmoothing}%";
        _eraseLines.Checked = _settings.EraseLines;
        _eraseFills.Checked = _settings.EraseFills;
        _updating = false;
    }

    private void UpdateSettings()
    {
        if (_updating) return;
        if (_shape.SelectedItem is string selected && Enum.TryParse<ShapeKind>(selected, out var kind)) _settings.ShapeKind = kind;
        _settings.KeepAspectRatio = _keepRatio.Checked;
        _settings.FreehandSmoothing = _freehandSmoothing.Value;
        _settings.EraseLines = _eraseLines.Checked;
        _settings.EraseFills = _eraseFills.Checked;
        _settings.NotifyChanged();
    }

    private void AddCheck(CheckBox checkBox)
    {
        checkBox.CheckedChanged += (_, _) => UpdateSettings();
    }

    private static void AddField(TableLayoutPanel parent, string label, Control input, int row, bool fillInput = true)
    {
        parent.Controls.Add(new Label
        {
            Text = label,
            Dock = DockStyle.Fill,
            ForeColor = Theme.Muted,
            BackColor = Theme.Panel,
            Font = Theme.UiFont(),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Margin = new Padding(0, 3, 8, 3)
        }, 0, row);

        if (fillInput) input.Dock = DockStyle.Fill;
        parent.Controls.Add(input, 1, row);
    }

    private static SvgToggleButton Toggle(string name, SvgIconKind icon) => new(icon, name);

    private static CheckBox EraserOption(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Checked = true,
        ForeColor = Theme.Text,
        BackColor = Theme.Panel,
        FlatStyle = FlatStyle.Flat,
        Font = Theme.UiFont(),
        Margin = new Padding(0, 4, 10, 0)
    };
}
