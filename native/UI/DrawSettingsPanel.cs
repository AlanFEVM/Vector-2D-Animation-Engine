namespace VectorAnimationEngine;

internal sealed class DrawSettingsPanel : UserControl
{
    private readonly DrawSettings _settings;
    private readonly Label _title = new();
    private readonly ComboBox _shape = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ToolTip _toolTip = new();
    private readonly SvgToggleButton _keepRatio = Toggle("Ratio", SvgIconKind.Ratio);
    private readonly ModernNumericUpDown _shapeVertexCount = new();
    private readonly ModernSlider _pencilSmoothing = new();
    private readonly Label _pencilSmoothingValue = new();
    private readonly Panel _eraserOptions = new();
    private readonly CheckBox _eraseLines = EraserOption("Stroke");
    private readonly CheckBox _eraseFills = EraserOption("Fill");
    private TableLayoutPanel? _content;
    private Label? _shapeLabel;
    private Label? _aspectLabel;
    private Label? _shapeVertexCountLabel;
    private bool _updating;
    private bool _shapeDetailToolsVisible = true;
    private bool _shapeDetailVisible;
    private bool _pencilPresentation;

    public DrawSettingsPanel(DrawSettings settings)
    {
        _settings = settings;
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Font = Theme.UiFont();
        Padding = Theme.InspectorSectionPadding;
        MinimumSize = new Size(248, 152);
        Theme.StyleToolTip(_toolTip);

        BuildUi();
        ReadSettings();
        _settings.Changed += SettingsChanged;
    }

    public void SetEraserOptionsVisible(bool visible)
    {
        if (_eraserOptions.Visible == visible) return;
        _eraserOptions.Visible = visible;
        NotifyPreferredHeightChanged();
    }

    public void SetShapeDetailToolsVisible(bool visible)
    {
        if (_shapeDetailToolsVisible == visible) return;
        _shapeDetailToolsVisible = visible;
        ConfigureShapeDetail(_settings.ShapeKind, syncValue: true);
    }

    // Rows are Shape + Aspect + optional Sides/Points + the comfortable smoothing row; the
    // optional eraser row is a separate docked strip below the content table.
    public int PreferredHeight => SectionBaseHeight
        + Theme.InspectorRowHeightComfortable
        + (_pencilPresentation
            ? 0
            : Theme.InspectorRowHeight * 2
              + (_shapeDetailVisible ? Theme.InspectorRowHeight : 0)
              + (_eraserOptions.Visible ? Theme.InspectorRowHeight : 0));

    private static int SectionBaseHeight =>
        Theme.InspectorTitleHeight
        + Theme.InspectorContentPaddingTop
        + Theme.InspectorSectionPaddingVertical * 2;

    public void SetPencilPresentation()
    {
        if (_pencilPresentation) return;
        _pencilPresentation = true;
        _title.Text = "Pencil";
        _content!.RowStyles[0].Height = 0;
        _content.RowStyles[1].Height = 0;
        _content.RowStyles[2].Height = 0;
        _content.RowStyles[3].Height = Theme.InspectorRowHeightComfortable;
        _shapeLabel!.Visible = false;
        _shape.Visible = false;
        _aspectLabel!.Visible = false;
        _keepRatio.Visible = false;
        _shapeVertexCountLabel!.Visible = false;
        _shapeVertexCount.Visible = false;
        _eraserOptions.Visible = false;
        NotifyPreferredHeightChanged();
    }

    public event EventHandler? PreferredHeightChanged;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _settings.Changed -= SettingsChanged;
            _toolTip.Dispose();
        }
        base.Dispose(disposing);
    }

    private void BuildUi()
    {
        _title.Text = "Draw Settings";
        _title.Dock = DockStyle.Top;
        _title.Height = Theme.InspectorTitleHeight;
        _title.ForeColor = Theme.Text;
        _title.BackColor = Theme.Panel;
        _title.Font = Theme.UiFont(10, FontStyle.Bold);
        _title.TextAlign = ContentAlignment.MiddleLeft;
        _title.AutoEllipsis = true;
        Controls.Add(_title);

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 2,
            RowCount = 4,
            Padding = new Padding(0, Theme.InspectorContentPaddingTop, 0, 0)
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.InspectorFieldLabelColumnWidth));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.InspectorRowHeight));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.InspectorRowHeight));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.InspectorRowHeight));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.InspectorRowHeightComfortable));
        _content = content;
        Controls.Add(content);
        content.BringToFront();

        _eraserOptions.Dock = DockStyle.Bottom;
        _eraserOptions.Height = Theme.InspectorRowHeight;
        _eraserOptions.BackColor = Theme.Panel;
        _eraserOptions.Padding = new Padding(0, 2, 0, 2);
        _eraserOptions.Visible = false;
        var eraseLabel = new Label
        {
            Text = "Erase",
            Dock = DockStyle.Left,
            Width = Theme.InspectorFieldLabelColumnWidth,
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
        // Dock last: WinForms lays out back-to-front, so the eraser row must stay behind the
        // filling content row. Bringing it to front made content claim the eraser's strip and
        // left dead space under the last field row.
        _eraserOptions.SendToBack();

        _shape.Items.AddRange([
            ShapeKind.Rectangle.ToString(),
            ShapeKind.Ellipse.ToString(),
            ShapeKind.Triangle.ToString(),
            ShapeKind.Polygon.ToString(),
            ShapeKind.Star.ToString(),
            ShapeKind.Line.ToString()
        ]);
        _shape.Dock = DockStyle.Fill;
        _shape.Margin = Theme.InspectorFieldMargin(rightGap: false);
        Theme.StyleComboBox(_shape);
        _shape.SelectedIndexChanged += (_, _) => UpdateSettings();
        _shapeLabel = AddField(content, "Shape", _shape, 0);

        _eraseLines.CheckedChanged += (_, _) => UpdateSettings();
        _eraseFills.CheckedChanged += (_, _) => UpdateSettings();

        AddCheck(_keepRatio);
        _keepRatio.Width = Theme.ControlHeightCompact;
        _keepRatio.Height = Theme.ControlHeightCompact;
        _keepRatio.Dock = DockStyle.Left;
        _keepRatio.Margin = new Padding(0, Theme.InspectorRowMarginVertical, 0, Theme.InspectorRowMarginVertical);
        _toolTip.SetToolTip(_keepRatio, _keepRatio.AccessibleName);
        _aspectLabel = AddField(content, "Aspect", _keepRatio, 1, fillInput: false);

        _shapeVertexCount.Minimum = 3m;
        _shapeVertexCount.Maximum = 64m;
        _shapeVertexCount.DecimalPlaces = 0;
        _shapeVertexCount.Increment = 1m;
        _shapeVertexCount.AccessibleName = "Polygon sides or star points";
        _shapeVertexCount.AccessibleDescription = "Controls the number of polygon sides or star points";
        _shapeVertexCount.ValueChanged += (_, _) => UpdateSettings();
        _shapeVertexCountLabel = AddField(content, "Sides", _shapeVertexCount, 2);
        content.RowStyles[2].Height = 0;
        _shapeVertexCountLabel.Visible = false;
        _shapeVertexCount.Visible = false;

        _pencilSmoothing.Minimum = 0;
        _pencilSmoothing.Maximum = 100;
        _pencilSmoothing.SmallChange = 1;
        _pencilSmoothing.LargeChange = 10;
        _pencilSmoothing.TickFrequency = 20;
        _pencilSmoothing.Dock = DockStyle.Fill;
        _pencilSmoothing.Margin = Padding.Empty;
        _pencilSmoothing.AccessibleName = "Pencil smoothing";
        _pencilSmoothing.AccessibleDescription = "Controls Pencil path smoothing from 0 to 100 percent";
        _pencilSmoothing.ValueChanged += (_, _) =>
        {
            _pencilSmoothingValue.Text = $"{_pencilSmoothing.Value}%";
            UpdatePencilSmoothing();
        };
        _pencilSmoothingValue.Dock = DockStyle.Fill;
        _pencilSmoothingValue.ForeColor = Theme.Muted;
        _pencilSmoothingValue.BackColor = Theme.Panel;
        _pencilSmoothingValue.TextAlign = ContentAlignment.MiddleRight;
        _pencilSmoothingValue.Font = Theme.UiFont();
        _pencilSmoothingValue.AccessibleName = "Pencil smoothing value";
        _pencilSmoothingValue.AccessibleRole = AccessibleRole.StaticText;
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
        smoothingRow.Controls.Add(_pencilSmoothing, 0, 0);
        smoothingRow.Controls.Add(_pencilSmoothingValue, 1, 0);
        AddField(content, "Smoothing", smoothingRow, 3);
        ConfigureShapeDetail(_settings.ShapeKind, syncValue: true);
    }

    private void ReadSettings()
    {
        _updating = true;
        _shape.SelectedItem = _settings.ShapeKind.ToString();
        _keepRatio.Checked = _settings.KeepAspectRatio;
        ConfigureShapeDetail(_settings.ShapeKind, syncValue: true);
        _pencilSmoothing.Value = _settings.PencilSmoothing;
        _pencilSmoothingValue.Text = $"{_settings.PencilSmoothing}%";
        _eraseLines.Checked = _settings.EraseLines;
        _eraseFills.Checked = _settings.EraseFills;
        _updating = false;
    }

    private void SettingsChanged(object? sender, EventArgs e) => ReadSettings();

    private void UpdateSettings()
    {
        if (_updating) return;
        if (_shape.SelectedItem is string selected && Enum.TryParse<ShapeKind>(selected, out var kind))
        {
            _settings.ShapeKind = kind;
            ConfigureShapeDetail(kind, syncValue: true);
        }
        _settings.KeepAspectRatio = _keepRatio.Checked;
        if (_settings.ShapeKind == ShapeKind.Polygon) _settings.PolygonSides = (int)_shapeVertexCount.Value;
        else if (_settings.ShapeKind == ShapeKind.Star) _settings.StarPoints = (int)_shapeVertexCount.Value;
        _settings.EraseLines = _eraseLines.Checked;
        _settings.EraseFills = _eraseFills.Checked;
        _settings.NotifyChanged();
    }

    private void UpdatePencilSmoothing()
    {
        if (_updating) return;
        _settings.PencilSmoothing = _pencilSmoothing.Value;
        _settings.NotifyChanged();
    }

    private void AddCheck(CheckBox checkBox)
    {
        checkBox.CheckedChanged += (_, _) => UpdateSettings();
    }

    private void ConfigureShapeDetail(ShapeKind shape, bool syncValue)
    {
        var supported = shape is ShapeKind.Polygon or ShapeKind.Star;
        var visible = !_pencilPresentation && _shapeDetailToolsVisible && supported;
        if (supported)
        {
            var wasUpdating = _updating;
            _updating = true;
            var polygon = shape == ShapeKind.Polygon;
            _shapeVertexCountLabel!.Text = polygon ? "Sides" : "Points";
            _shapeVertexCount.Minimum = 3m;
            _shapeVertexCount.Maximum = polygon ? 64m : 32m;
            if (syncValue)
            {
                _shapeVertexCount.Value = polygon
                    ? Math.Clamp(_settings.PolygonSides, (int)_shapeVertexCount.Minimum, (int)_shapeVertexCount.Maximum)
                    : Math.Clamp(_settings.StarPoints, (int)_shapeVertexCount.Minimum, (int)_shapeVertexCount.Maximum);
            }

            _updating = wasUpdating;
        }

        if (_shapeDetailVisible == visible) return;
        _shapeDetailVisible = visible;
        _content!.RowStyles[2].Height = visible ? Theme.InspectorRowHeight : 0;
        _shapeVertexCountLabel!.Visible = visible;
        _shapeVertexCount.Visible = visible;
        NotifyPreferredHeightChanged();
    }

    private void NotifyPreferredHeightChanged()
    {
        MinimumSize = new Size(248, _pencilPresentation ? PreferredHeight : PreferredHeight - 12);
        PreferredHeightChanged?.Invoke(this, EventArgs.Empty);
    }

    private static Label AddField(TableLayoutPanel parent, string label, Control input, int row, bool fillInput = true)
    {
        var labelControl = new Label
        {
            Text = label,
            Dock = DockStyle.Fill,
            ForeColor = Theme.Muted,
            BackColor = Theme.Panel,
            Font = Theme.UiFont(),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = false,
            Margin = Theme.InspectorFieldMargin(rightGap: true)
        };
        parent.Controls.Add(labelControl, 0, row);

        if (fillInput)
        {
            input.Dock = DockStyle.Fill;
            input.Margin = Theme.InspectorFieldMargin(rightGap: false);
        }

        parent.Controls.Add(input, 1, row);
        return labelControl;
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
        Margin = new Padding(0, 3, 14, 0)
    };
}
