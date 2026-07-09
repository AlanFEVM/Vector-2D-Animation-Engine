namespace VectorAnimationEngine;

internal sealed class DrawSnappingStrip : UserControl
{
    private readonly DrawSettings _settings;
    private readonly ToolTip _toolTip = new();
    private readonly SvgToggleButton _snap = Toggle("Snap", SvgIconKind.Snap);
    private readonly SvgToggleButton _snapGrid = Toggle("Grid snap", SvgIconKind.Grid);
    private readonly SvgToggleButton _snapObjects = Toggle("Object snap", SvgIconKind.Objects);
    private readonly SvgToggleButton _adhesion = Toggle("Tight fit", SvgIconKind.TightFit);
    private readonly SvgToggleButton _alignment = Toggle("Align", SvgIconKind.Align);
    private readonly SvgToggleButton _angleSnap = Toggle("Angle snap", SvgIconKind.Angle);
    private readonly NumericUpDown _gridSize = new() { Minimum = 1, Maximum = 10000, DecimalPlaces = 0, Increment = 8, Width = 64 };
    private readonly NumericUpDown _angleStep = new() { Minimum = 1, Maximum = 90, DecimalPlaces = 0, Increment = 1, Width = 54 };
    private bool _updating;

    public DrawSnappingStrip(DrawSettings settings)
    {
        _settings = settings;
        Width = 320;
        Height = 40;
        BackColor = Theme.Top;
        ForeColor = Theme.Text;
        Font = Theme.UiFont();

        BuildUi();
        ReadSettings();
        _settings.Changed += (_, _) => ReadSettings();
    }

    private void BuildUi()
    {
        var strip = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Top,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(4, 4, 4, 4),
            Margin = Padding.Empty
        };
        Controls.Add(strip);

        AddToggle(strip, _snap);
        AddToggle(strip, _snapGrid);
        AddToggle(strip, _snapObjects);
        AddToggle(strip, _adhesion);
        AddToggle(strip, _alignment);
        AddSeparator(strip);

        Theme.StyleNumeric(_gridSize);
        _gridSize.Margin = new Padding(0, 2, 6, 0);
        _gridSize.ValueChanged += (_, _) => UpdateSettings();
        _toolTip.SetToolTip(_gridSize, "Grid vu");
        strip.Controls.Add(_gridSize);

        AddToggle(strip, _angleSnap);
        Theme.StyleNumeric(_angleStep);
        _angleStep.Margin = new Padding(0, 2, 0, 0);
        _angleStep.ValueChanged += (_, _) => UpdateSettings();
        _toolTip.SetToolTip(_angleStep, "Angle snap degrees");
        strip.Controls.Add(_angleStep);
    }

    private void ReadSettings()
    {
        _updating = true;
        _snap.Checked = _settings.SnapEnabled;
        _snapGrid.Checked = _settings.SnapToGrid;
        _snapObjects.Checked = _settings.SnapToObjects;
        _adhesion.Checked = _settings.AdhesionEnabled;
        _alignment.Checked = _settings.AlignmentEnabled;
        _angleSnap.Checked = _settings.AngleSnapEnabled;
        _gridSize.Value = (decimal)_settings.GridSize;
        _angleStep.Value = (decimal)_settings.AngleSnapDegrees;
        _updating = false;
    }

    private void UpdateSettings()
    {
        if (_updating) return;
        _settings.SnapEnabled = _snap.Checked;
        _settings.SnapToGrid = _snapGrid.Checked;
        _settings.SnapToObjects = _snapObjects.Checked;
        _settings.AdhesionEnabled = _adhesion.Checked;
        _settings.AlignmentEnabled = _alignment.Checked;
        _settings.AngleSnapEnabled = _angleSnap.Checked;
        _settings.GridSize = (float)_gridSize.Value;
        _settings.AngleSnapDegrees = (float)_angleStep.Value;
        _settings.NotifyChanged();
    }

    private void AddToggle(FlowLayoutPanel parent, SvgToggleButton toggle)
    {
        toggle.Width = 28;
        toggle.Height = 28;
        toggle.Margin = new Padding(0, 2, 4, 0);
        toggle.CheckedChanged += (_, _) => UpdateSettings();
        _toolTip.SetToolTip(toggle, toggle.AccessibleName);
        parent.Controls.Add(toggle);
    }

    private static void AddSeparator(FlowLayoutPanel parent)
    {
        parent.Controls.Add(new Panel
        {
            Width = 1,
            Height = 28,
            BackColor = Theme.Border,
            Margin = new Padding(2, 2, 8, 0)
        });
    }

    private static SvgToggleButton Toggle(string name, SvgIconKind icon) => new(icon, name);
}
