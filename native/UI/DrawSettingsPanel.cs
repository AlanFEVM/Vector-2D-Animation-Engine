namespace VectorAnimationEngine;

internal sealed class DrawSettingsPanel : UserControl
{
    private readonly DrawSettings _settings;
    private readonly ComboBox _shape = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ToolTip _toolTip = new();
    private readonly SvgToggleButton _keepRatio = Toggle("Ratio", SvgIconKind.Ratio);
    private bool _updating;

    public DrawSettingsPanel(DrawSettings settings)
    {
        _settings = settings;
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Font = Theme.UiFont();
        Padding = new Padding(0, 8, 0, 8);
        MinimumSize = new Size(248, 112);

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
            RowCount = 2,
            Padding = new Padding(0, 4, 0, 0)
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        Controls.Add(content);
        content.BringToFront();

        _shape.Items.AddRange(Enum.GetNames<ShapeKind>());
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
    }

    private void ReadSettings()
    {
        _updating = true;
        _shape.SelectedItem = _settings.ShapeKind.ToString();
        _keepRatio.Checked = _settings.KeepAspectRatio;
        _updating = false;
    }

    private void UpdateSettings()
    {
        if (_updating) return;
        if (_shape.SelectedItem is string selected && Enum.TryParse<ShapeKind>(selected, out var kind)) _settings.ShapeKind = kind;
        _settings.KeepAspectRatio = _keepRatio.Checked;
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
