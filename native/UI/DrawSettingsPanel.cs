namespace VectorAnimationEngine;

internal sealed class DrawSettingsPanel : UserControl
{
    private readonly DrawSettings _settings;
    private readonly ComboBox _shape = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _snap = CheckBox("Snap");
    private readonly CheckBox _snapGrid = CheckBox("Grid");
    private readonly CheckBox _snapObjects = CheckBox("Objects");
    private readonly CheckBox _adhesion = CheckBox("Tight fit");
    private readonly CheckBox _alignment = CheckBox("Align");
    private readonly CheckBox _angleSnap = CheckBox("Angle");
    private readonly CheckBox _keepRatio = CheckBox("Ratio");
    private readonly NumericUpDown _gridSize = new() { Minimum = 1, Maximum = 10000, DecimalPlaces = 0, Increment = 8, Width = 112 };
    private readonly NumericUpDown _angleStep = new() { Minimum = 1, Maximum = 90, DecimalPlaces = 0, Increment = 1, Width = 112 };
    private bool _updating;

    public DrawSettingsPanel(DrawSettings settings)
    {
        _settings = settings;
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Font = Theme.UiFont();
        Padding = new Padding(0, 8, 0, 8);
        MinimumSize = new Size(248, 288);

        BuildUi();
        ReadSettings();
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
            RowCount = 6,
            Padding = new Padding(0, 4, 0, 0)
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
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

        var snapping = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Margin = new Padding(0, 2, 0, 2)
        };
        AddCheck(snapping, _snap);
        AddCheck(snapping, _snapGrid);
        AddCheck(snapping, _snapObjects);
        AddCheck(snapping, _adhesion);
        AddCheck(snapping, _alignment);
        AddField(content, "Snapping", snapping, 1);

        _gridSize.Dock = DockStyle.Fill;
        _gridSize.Margin = new Padding(0, 3, 0, 3);
        Theme.StyleNumeric(_gridSize);
        _gridSize.ValueChanged += (_, _) => UpdateSettings();
        AddField(content, "Grid vu", _gridSize, 2);

        var angle = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0)
        };
        angle.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
        angle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddCheck(_angleSnap);
        angle.Controls.Add(_angleSnap, 0, 0);
        _angleStep.Dock = DockStyle.Fill;
        _angleStep.Margin = new Padding(0, 3, 0, 3);
        Theme.StyleNumeric(_angleStep);
        _angleStep.ValueChanged += (_, _) => UpdateSettings();
        angle.Controls.Add(_angleStep, 1, 0);
        AddField(content, "Angle snap", angle, 3);

        AddCheck(_keepRatio);
        AddField(content, "Aspect", _keepRatio, 4);
    }

    private void ReadSettings()
    {
        _updating = true;
        _shape.SelectedItem = _settings.ShapeKind.ToString();
        _snap.Checked = _settings.SnapEnabled;
        _snapGrid.Checked = _settings.SnapToGrid;
        _snapObjects.Checked = _settings.SnapToObjects;
        _adhesion.Checked = _settings.AdhesionEnabled;
        _alignment.Checked = _settings.AlignmentEnabled;
        _angleSnap.Checked = _settings.AngleSnapEnabled;
        _keepRatio.Checked = _settings.KeepAspectRatio;
        _gridSize.Value = (decimal)_settings.GridSize;
        _angleStep.Value = (decimal)_settings.AngleSnapDegrees;
        _updating = false;
    }

    private void UpdateSettings()
    {
        if (_updating) return;
        if (_shape.SelectedItem is string selected && Enum.TryParse<ShapeKind>(selected, out var kind)) _settings.ShapeKind = kind;
        _settings.SnapEnabled = _snap.Checked;
        _settings.SnapToGrid = _snapGrid.Checked;
        _settings.SnapToObjects = _snapObjects.Checked;
        _settings.AdhesionEnabled = _adhesion.Checked;
        _settings.AlignmentEnabled = _alignment.Checked;
        _settings.AngleSnapEnabled = _angleSnap.Checked;
        _settings.KeepAspectRatio = _keepRatio.Checked;
        _settings.GridSize = (float)_gridSize.Value;
        _settings.AngleSnapDegrees = (float)_angleStep.Value;
        _settings.NotifyChanged();
    }

    private void AddCheck(FlowLayoutPanel parent, CheckBox checkBox)
    {
        AddCheck(checkBox);
        parent.Controls.Add(checkBox);
    }

    private void AddCheck(CheckBox checkBox)
    {
        checkBox.CheckedChanged += (_, _) => UpdateSettings();
    }

    private static void AddField(TableLayoutPanel parent, string label, Control input, int row)
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

        input.Dock = DockStyle.Fill;
        parent.Controls.Add(input, 1, row);
    }

    private static CheckBox CheckBox(string text)
    {
        return new CheckBox
        {
            Text = text,
            AutoSize = true,
            MinimumSize = new Size(72, 24),
            ForeColor = Theme.Text,
            BackColor = Theme.Panel,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.UiFont(),
            Margin = new Padding(0, 0, 8, 2),
            TextAlign = ContentAlignment.MiddleLeft
        };
    }
}
