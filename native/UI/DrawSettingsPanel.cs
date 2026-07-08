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
        Padding = new Padding(10);
        Width = 250;
        Height = 336;

        BuildUi();
        ReadSettings();
    }

    private void BuildUi()
    {
        Controls.Add(new Label { Text = "Draw Settings", Left = 10, Top = 8, Width = 210, Height = 24, ForeColor = Theme.Text, BackColor = Theme.Panel, Font = Theme.UiFont(10, FontStyle.Bold) });

        var y = 42;
        Controls.Add(Theme.Label("Shape", 10, y, 210, Theme.Muted));
        y += 24;
        _shape.Left = 10;
        _shape.Top = y;
        _shape.Width = 180;
        _shape.Items.AddRange(Enum.GetNames<ShapeKind>());
        Theme.StyleComboBox(_shape);
        _shape.SelectedIndexChanged += (_, _) => UpdateSettings();
        Controls.Add(_shape);

        y += 42;
        Controls.Add(Theme.Label("Snapping", 10, y, 210, Theme.Muted));
        y += 26;
        AddCheck(_snap, 10, y);
        AddCheck(_snapGrid, 82, y);
        AddCheck(_snapObjects, 154, y);

        y += 32;
        AddCheck(_adhesion, 10, y);
        AddCheck(_alignment, 112, y);

        y += 38;
        Controls.Add(Theme.Label("Grid size", 10, y, 110, Theme.Muted));
        _gridSize.Left = 118;
        _gridSize.Top = y;
        Theme.StyleNumeric(_gridSize);
        _gridSize.ValueChanged += (_, _) => UpdateSettings();
        Controls.Add(_gridSize);

        y += 40;
        AddCheck(_angleSnap, 10, y);
        Controls.Add(Theme.Label("Step", 82, y, 42, Theme.Muted));
        _angleStep.Left = 124;
        _angleStep.Top = y;
        Theme.StyleNumeric(_angleStep);
        _angleStep.ValueChanged += (_, _) => UpdateSettings();
        Controls.Add(_angleStep);

        y += 40;
        AddCheck(_keepRatio, 10, y);
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

    private void AddCheck(CheckBox checkBox, int left, int top)
    {
        checkBox.Left = left;
        checkBox.Top = top;
        checkBox.CheckedChanged += (_, _) => UpdateSettings();
        Controls.Add(checkBox);
    }

    private static CheckBox CheckBox(string text)
    {
        return new CheckBox
        {
            Text = text,
            Width = 92,
            Height = 24,
            ForeColor = Theme.Text,
            BackColor = Theme.Panel,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.UiFont()
        };
    }
}
