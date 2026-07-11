namespace VectorAnimationEngine;

internal sealed class DrawSettingsPanel : UserControl
{
    private readonly DrawSettings _settings;
    private readonly ComboBox _shape = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ToolTip _toolTip = new();
    private readonly SvgToggleButton _keepRatio = Toggle("Ratio", SvgIconKind.Ratio);
    private readonly TrackBar _freehandSmoothing = new();
    private readonly Label _freehandSmoothingValue = new();
    private bool _updating;

    public DrawSettingsPanel(DrawSettings settings)
    {
        _settings = settings;
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Font = Theme.UiFont();
        Padding = new Padding(0, 8, 0, 8);
        MinimumSize = new Size(248, 152);

        BuildUi();
        ReadSettings();
        _settings.Changed += (_, _) => ReadSettings();
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
        _updating = false;
    }

    private void UpdateSettings()
    {
        if (_updating) return;
        if (_shape.SelectedItem is string selected && Enum.TryParse<ShapeKind>(selected, out var kind)) _settings.ShapeKind = kind;
        _settings.KeepAspectRatio = _keepRatio.Checked;
        _settings.FreehandSmoothing = _freehandSmoothing.Value;
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
}
