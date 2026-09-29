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
    private readonly ModernNumericUpDown _gridSize = new()
    {
        Minimum = 1,
        Maximum = 10000,
        DecimalPlaces = 0,
        Increment = 8,
        Width = 64,
        Suffix = "vu",
        AccessibleName = "Grid vu"
    };
    private readonly ModernNumericUpDown _angleStep = new()
    {
        Minimum = 1,
        Maximum = 90,
        DecimalPlaces = 0,
        Increment = 1,
        Width = 54,
        Suffix = "degrees",
        AccessibleName = "Angle snap degrees"
    };
    private bool _updating;
    private UiLanguage? _helpLanguage;

    public DrawSnappingStrip(DrawSettings settings)
    {
        _settings = settings;
        Width = 320;
        Height = 36;
        MinimumSize = new Size(0, 36);
        BackColor = Theme.Top;
        ForeColor = Theme.Text;
        Font = Theme.UiFont();
        AccessibleName = "Snap";
        AccessibleRole = AccessibleRole.ToolBar;
        TabStop = false;
        Theme.StyleToolTip(_toolTip);

        BuildUi();
        foreach (var control in new Control[] { _snap, _snapGrid, _snapObjects, _alignment, _angleSnap, _gridSize, _angleStep })
        {
            control.MouseEnter += (_, _) => RefreshHelp();
            control.Enter += (_, _) => RefreshHelp();
        }
        ReadSettings();
        _settings.Changed += HandleSettingsChanged;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _settings.Changed -= HandleSettingsChanged;
            _toolTip.Dispose();
        }
        base.Dispose(disposing);
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
        _gridSize.Margin = new Padding(0, 0, 2, 0);
        _gridSize.TabIndex = strip.Controls.Count;
        _gridSize.ValueChanged += (_, _) => UpdateSettings();
        _toolTip.SetToolTip(_gridSize, "Grid vu");
        strip.Controls.Add(_gridSize);

        AddToggle(strip, _angleSnap);
        Theme.StyleNumeric(_angleStep);
        _angleStep.Margin = Padding.Empty;
        _angleStep.TabIndex = strip.Controls.Count;
        _angleStep.ValueChanged += (_, _) => UpdateSettings();
        _toolTip.SetToolTip(_angleStep, "Angle snap degrees");
        strip.Controls.Add(_angleStep);
    }

    private void ReadSettings()
    {
        _updating = true;
        try
        {
            _snap.Checked = _settings.SnapEnabled;
            _snapGrid.Checked = _settings.SnapToGrid;
            _snapObjects.Checked = _settings.SnapToObjects;
            _adhesion.Checked = _settings.AdhesionEnabled;
            _alignment.Checked = _settings.AlignmentEnabled;
            _angleSnap.Checked = _settings.AngleSnapEnabled;
            _gridSize.Value = (decimal)_settings.GridSize;
            _angleStep.Value = (decimal)_settings.AngleSnapDegrees;
            RefreshHelp();
        }
        finally
        {
            _updating = false;
        }
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
        toggle.Margin = new Padding(0, 0, 2, 0);
        toggle.FlatAppearance.BorderSize = 0;
        toggle.TabIndex = parent.Controls.Count;
        toggle.CheckedChanged += (_, _) => UpdateSettings();
        _toolTip.SetToolTip(toggle, toggle.AccessibleName);
        parent.Controls.Add(toggle);
    }

    private static void AddSeparator(FlowLayoutPanel parent)
    {
        parent.Controls.Add(new Panel
        {
            Width = 1,
            Height = 18,
            BackColor = Theme.Border,
            Margin = new Padding(3, 5, 4, 5)
        });
    }

    private void HandleSettingsChanged(object? sender, EventArgs e) => ReadSettings();

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_helpLanguage != UiLocalization.CurrentLanguage) RefreshHelp();
    }

    private void RefreshHelp()
    {
        _helpLanguage = UiLocalization.CurrentLanguage;
        SetToggleHelp(_snap, "Enable or pause snapping. Your individual snap options are kept.", false);
        SetToggleHelp(_snapGrid, "Place points on the grid. Set the spacing with the vu field.", true);
        SetToggleHelp(_snapObjects, "Snap to nearby object points while drawing or moving.", true);
        SetToggleHelp(_alignment, "Use alignment guides while drawing.", true);
        SetToggleHelp(_angleSnap, "Constrain angles to the step in the degrees field.", true);
        SetHelp(_gridSize, UiLocalization.T("Grid spacing in vector units (vu). Smaller values give a finer grid."));
        SetHelp(_angleStep, UiLocalization.T("Angle step in degrees. For example, 15 gives 15, 30, 45 degrees."));
    }

    private void SetToggleHelp(SvgToggleButton control, string purpose, bool requiresSnap)
    {
        var message = UiLocalization.T(purpose) + Environment.NewLine
            + UiLocalization.T(control.Checked ? "On" : "Off");
        if (requiresSnap && !_settings.SnapEnabled)
            message += Environment.NewLine + UiLocalization.T("Turn on Snap first to use this option.");
        SetHelp(control, message);
    }

    private void SetHelp(Control control, string description)
    {
        if (control.AccessibleDescription != description) control.AccessibleDescription = description;
        if (_toolTip.GetToolTip(control) != description) _toolTip.SetToolTip(control, description);
    }

    private static SvgToggleButton Toggle(string name, SvgIconKind icon) => new(icon, name);
}
