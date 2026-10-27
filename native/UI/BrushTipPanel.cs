namespace VectorAnimationEngine;

internal sealed class BrushTipPanel : UserControl
{
    private const int PreviewRowHeight = 58;
    private const int SettingRowHeight = 30;
    private const int FieldRowHeight = Theme.InspectorRowHeightComfortable;
    private const int SettingRowCount = 7;

    private readonly BrushTipPreview _preview = new();
    private readonly Label _name = InspectorModuleStyle.CreateTextLabel();
    private readonly FlowLayoutPanel _tipActions = new();
    private readonly ComboBox _preset = new();
    private readonly ModernNumericUpDown _size = new();
    private readonly ModernSlider _frequency = new();
    private readonly Label _frequencyValue = InspectorModuleStyle.CreateTextLabel();
    private readonly CheckBox _continuous = new();
    private readonly ModernSlider _hardness = new();
    private readonly Label _hardnessValue = InspectorModuleStyle.CreateTextLabel();
    private readonly ComboBox _traditionalTip = new();
    private readonly ModernNumericUpDown _traditionalWidth = new();
    private readonly ModernSlider _traditionalDirection = new();
    private readonly Label _traditionalDirectionValue = InspectorModuleStyle.CreateTextLabel();
    private readonly ModernSlider _pressureSmoothing = new();
    private readonly Label _pressureSmoothingValue = InspectorModuleStyle.CreateTextLabel();
    private readonly TableLayoutPanel _content;
    private bool _updating;
    private bool _traditionalRowsVisible;
    private bool _compactLayout;

    public BrushTipPanel()
    {
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Font = Theme.UiFont();
        Padding = Theme.InspectorSectionPadding;
        MinimumSize = new Size(0, PreferredHeight);

        var title = InspectorModuleStyle.CreateTitle("Brush Tip");
        title.Dock = DockStyle.Top;
        _content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 3,
            RowCount = 8,
            Padding = new Padding(0, Theme.InspectorContentPaddingTop, 0, 0)
        };
        _content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62));
        _content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46));
        _content.RowStyles.Add(new RowStyle(SizeType.Absolute, PreviewRowHeight));
        for (var row = 1; row < _content.RowCount; row++)
        {
            _content.RowStyles.Add(new RowStyle(SizeType.Absolute,
                row is 1 or 2 ? FieldRowHeight : SettingRowHeight));
        }

        _preview.Dock = DockStyle.Fill;
        _preview.Margin = new Padding(0, 2, 8, 4);
        _content.Controls.Add(_preview, 0, 0);

        var tipDetails = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Panel, Margin = Padding.Empty };
        _name.Dock = DockStyle.Top;
        _name.Height = 24;
        _name.BackColor = Theme.Panel;
        _name.ForeColor = Theme.PanelSecondaryText;
        _name.Font = Theme.UiFont(Theme.InspectorLabelFontSize);
        _name.TextAlign = ContentAlignment.MiddleLeft;
        _name.AutoEllipsis = true;
        tipDetails.Controls.Add(_name);

        var actions = _tipActions;
        actions.Dock = DockStyle.Bottom;
        actions.Height = 28;
        actions.BackColor = Theme.Panel;
        actions.FlowDirection = FlowDirection.LeftToRight;
        actions.WrapContents = true;
        actions.Padding = Padding.Empty;
        actions.Margin = Padding.Empty;
        var import = new Button
        {
            Text = "Import",
            Width = 72,
            Height = 26,
            AccessibleName = "Import brush tip",
            Margin = new Padding(0, 0, 5, 0)
        };
        Theme.StyleButton(import);
        import.Click += (_, _) => ImportRequested?.Invoke(this, EventArgs.Empty);
        Theme.StyleComboBox(_preset);
        _preset.DropDownStyle = ComboBoxStyle.DropDownList;
        _preset.Font = Theme.UiFont(8.5f);
        _preset.Width = 106;
        _preset.Height = 26;
        _preset.AccessibleName = "Brush style";
        _preset.Margin = Padding.Empty;
        _preset.Items.AddRange(["Soft Round", "Traditional Brush"]);
        _preset.SelectedIndex = 0;
        _preset.SelectedIndexChanged += (_, _) =>
        {
            if (_updating) return;
            if (_preset.SelectedIndex == 0) SoftRoundRequested?.Invoke(this, EventArgs.Empty);
            else if (_preset.SelectedIndex == 1) TraditionalBrushRequested?.Invoke(this, EventArgs.Empty);
        };
        actions.Controls.Add(import);
        actions.Controls.Add(_preset);
        tipDetails.Controls.Add(actions);
        _content.Controls.Add(tipDetails, 1, 0);
        _content.SetColumnSpan(tipDetails, 2);

        _size.Minimum = 0.1m;
        _size.Maximum = 128m;
        _size.DecimalPlaces = 1;
        _size.Increment = 0.1m;
        _size.Value = 8m;
        _size.Suffix = UiLocalization.T("pt");
        _size.ValueChanged += (_, _) =>
        {
            if (!_updating) BrushSizeChanged?.Invoke(this, EventArgs.Empty);
        };
        AddSettingRow(_content, "Size", _size, null, 1);

        Theme.StyleComboBox(_traditionalTip);
        _traditionalTip.DropDownStyle = ComboBoxStyle.DropDownList;
        _traditionalTip.Width = 96;
        _traditionalTip.Dock = DockStyle.Fill;
        _traditionalTip.AccessibleName = "Traditional brush tip shape";
        _traditionalTip.Items.AddRange(["Round", "Square"]);
        _traditionalTip.SelectedIndex = 0;
        _traditionalTip.SelectedIndexChanged += (_, _) =>
        {
            UpdateTraditionalInputState();
            if (!_updating) TraditionalSettingsChanged?.Invoke(this, EventArgs.Empty);
        };
        _traditionalWidth.Minimum = 25m;
        _traditionalWidth.Maximum = 400m;
        _traditionalWidth.DecimalPlaces = 0;
        _traditionalWidth.Increment = 5m;
        _traditionalWidth.Suffix = "%";
        _traditionalWidth.Value = 100m;
        _traditionalWidth.Dock = DockStyle.Fill;
        _traditionalWidth.Margin = Padding.Empty;
        _traditionalWidth.AccessibleName = "Traditional brush shape width";
        _traditionalWidth.AccessibleDescription = "Width percentage relative to the brush size";
        _traditionalWidth.ValueChanged += (_, _) =>
        {
            UpdateTraditionalInputState();
            if (!_updating) TraditionalSettingsChanged?.Invoke(this, EventArgs.Empty);
        };
        var shapeInput = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = Theme.Panel, Margin = Padding.Empty,
            ColumnCount = 2, RowCount = 1
        };
        shapeInput.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
        shapeInput.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
        shapeInput.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        InspectorModuleStyle.StyleField(_traditionalTip);
        InspectorModuleStyle.StyleField(_traditionalWidth);
        _traditionalTip.Margin = new Padding(0, 0, Theme.GapXs, 0);
        _traditionalWidth.Margin = Padding.Empty;
        shapeInput.Controls.Add(_traditionalTip, 0, 0);
        shapeInput.Controls.Add(_traditionalWidth, 1, 0);
        AddSettingRow(_content, "Shape %", shapeInput, null, 2);

        ConfigureSlider(_traditionalDirection, 0, 359, 0, 45);
        _traditionalDirection.AccessibleName = "Traditional brush direction in degrees";
        _traditionalDirection.ValueChanged += (_, _) =>
        {
            _traditionalDirectionValue.Text = $"{_traditionalDirection.Value}°";
            if (!_updating) TraditionalSettingsChanged?.Invoke(this, EventArgs.Empty);
        };
        AddSettingRow(_content, "Angle", _traditionalDirection, _traditionalDirectionValue, 3);

        ConfigureSlider(_frequency, 1, 24, 8, 4);
        _frequency.ValueChanged += (_, _) =>
        {
            _frequencyValue.Text = $"{_frequency.Value}x";
            if (!_updating) FrequencyChanged?.Invoke(this, EventArgs.Empty);
        };
        AddSettingRow(_content, "Freq", _frequency, _frequencyValue, 4);

        _continuous.Text = "Continuous";
        _continuous.AutoSize = true;
        _continuous.Checked = true;
        _continuous.ForeColor = Theme.PanelSecondaryText;
        _continuous.BackColor = Theme.Panel;
        _continuous.FlatStyle = FlatStyle.Flat;
        _continuous.Font = Theme.UiFont(Theme.InspectorLabelFontSize);
        _continuous.Margin = new Padding(0, 4, 0, 0);
        _continuous.CheckedChanged += (_, _) =>
        {
            if (!_updating) ContinuousChanged?.Invoke(this, EventArgs.Empty);
        };
        AddSettingRow(_content, "Stroke", _continuous, null, 5);

        ConfigureSlider(_hardness, 0, 100, 0, 20);
        _hardness.ValueChanged += (_, _) =>
        {
            _hardnessValue.Text = $"{_hardness.Value}%";
            if (!_updating) HardnessChanged?.Invoke(this, EventArgs.Empty);
        };
        AddSettingRow(_content, "Hard", _hardness, _hardnessValue, 6);

        ConfigureSlider(_pressureSmoothing, 0, 100, 70, 20);
        _pressureSmoothing.ValueChanged += (_, _) =>
        {
            _pressureSmoothingValue.Text = $"{_pressureSmoothing.Value}%";
            if (!_updating) PressureSmoothingChanged?.Invoke(this, EventArgs.Empty);
        };
        AddSettingRow(_content, "Smooth", _pressureSmoothing, _pressureSmoothingValue, 7);

        Controls.Add(_content);
        Controls.Add(title);
        SizeChanged += (_, _) => UpdateResponsiveLayout();
        SetBrushShape(BrushShape.CreateTraditionalBrush());
    }

    public event EventHandler? ImportRequested;
    public event EventHandler? SoftRoundRequested;
    public event EventHandler? TraditionalBrushRequested;
    public event EventHandler? FrequencyChanged;
    public event EventHandler? ContinuousChanged;
    public event EventHandler? HardnessChanged;
    public event EventHandler? PressureSmoothingChanged;
    public event EventHandler? BrushSizeChanged;
    public event EventHandler? TraditionalSettingsChanged;
    public event EventHandler? PreferredHeightChanged;

    public float SizePoints => (float)_size.Value;
    public int Frequency => _frequency.Value;
    public bool Continuous => _continuous.Checked;
    public int Hardness => _hardness.Value;
    public int PressureSmoothing => _pressureSmoothing.Value;
    public TraditionalBrushTipKind TraditionalTipKind => _traditionalTip.SelectedIndex == 1
        ? TraditionalBrushTipKind.Square
        : TraditionalBrushTipKind.Round;
    public int TraditionalWidthPercent => (int)_traditionalWidth.Value;
    public int TraditionalDirectionDegrees => _traditionalDirection.Value;
    // Row 0 is the tip preview strip; the remaining dense rows are tip size, frequency,
    // hardness, pressure smoothing, the two traditional-brush rows and the stroke row.
    public int PreferredHeight => Theme.InspectorSectionPaddingVertical * 2
        + Theme.InspectorTitleHeight
        + Theme.InspectorContentPaddingTop
        + PreviewRowHeight
        + (_compactLayout ? 28 : 0)
        + SettingRowHeight * SettingRowCount
        + (FieldRowHeight - SettingRowHeight) * 2
        - (_traditionalRowsVisible ? 0 : FieldRowHeight + SettingRowHeight);

    public void SetBrushShape(BrushShape brushShape)
    {
        _updating = true;
        SetTraditionalRowsVisible(brushShape.IsTraditionalBrush);
        _preview.BrushShape = brushShape;
        _name.Text = brushShape.Name;
        _preset.SelectedIndex = brushShape.IsDefaultSoftRound
            ? 0
            : brushShape.IsTraditionalBrush
            ? 1
            : -1;
        _hardness.Enabled = brushShape.IsDefaultSoftRound;
        _hardness.Value = brushShape.Hardness;
        _hardnessValue.Text = brushShape.IsDefaultSoftRound ? $"{brushShape.Hardness}%" : "-";
        if (brushShape.IsTraditionalBrush)
        {
            _traditionalTip.SelectedIndex = brushShape.TraditionalTipKind == TraditionalBrushTipKind.Square ? 1 : 0;
            _traditionalWidth.Value = Math.Clamp(
                (decimal)brushShape.TraditionalWidthPercent,
                _traditionalWidth.Minimum,
                _traditionalWidth.Maximum);
            _traditionalDirection.Value = Math.Clamp(
                brushShape.TraditionalDirectionDegrees,
                _traditionalDirection.Minimum,
                _traditionalDirection.Maximum);
            _traditionalDirectionValue.Text = $"{_traditionalDirection.Value}°";
        }

        UpdateTraditionalInputState();
        _updating = false;
        _preview.Invalidate();
    }

    public void SetBrushStrokeSettings(int frequency, bool continuous, float sizePoints, int pressureSmoothing)
    {
        _updating = true;
        _size.Value = Math.Clamp((decimal)sizePoints, _size.Minimum, _size.Maximum);
        _frequency.Value = Math.Clamp(frequency, _frequency.Minimum, _frequency.Maximum);
        _frequencyValue.Text = $"{_frequency.Value}x";
        _continuous.Checked = continuous;
        _pressureSmoothing.Value = Math.Clamp(pressureSmoothing, _pressureSmoothing.Minimum, _pressureSmoothing.Maximum);
        _pressureSmoothingValue.Text = $"{_pressureSmoothing.Value}%";
        _updating = false;
    }

    private static void ConfigureSlider(ModernSlider slider, int minimum, int maximum, int value, int tickFrequency)
    {
        slider.Minimum = minimum;
        slider.Maximum = maximum;
        slider.Value = value;
        slider.TickFrequency = tickFrequency;
        slider.Dock = DockStyle.Fill;
        slider.Margin = Padding.Empty;
    }

    private static void AddSettingRow(TableLayoutPanel parent, string label, Control input, Label? value, int row)
    {
        var labelControl = InspectorModuleStyle.CreateFieldLabel(label);
        if (input is ModernNumericUpDown or TableLayoutPanel)
        {
            var fieldRow = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Panel,
                ColumnCount = 2,
                RowCount = 1,
                Margin = Padding.Empty
            };
            fieldRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, input is TableLayoutPanel ? 32 : 48));
            fieldRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, input is TableLayoutPanel ? 68 : 52));
            fieldRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            input.Dock = DockStyle.Fill;
            InspectorModuleStyle.StyleField(input);
            fieldRow.Controls.Add(labelControl, 0, 0);
            fieldRow.Controls.Add(input, 1, 0);
            parent.Controls.Add(fieldRow, 0, row);
            parent.SetColumnSpan(fieldRow, parent.ColumnCount);
            return;
        }
        parent.Controls.Add(labelControl, 0, row);

        input.Dock = input is CheckBox ? DockStyle.Left : DockStyle.Fill;
        parent.Controls.Add(input, 1, row);
        if (value is null) return;
        value.Dock = DockStyle.Fill;
        value.BackColor = Theme.Panel;
        value.ForeColor = Theme.PanelSecondaryText;
        value.Font = Theme.UiFont(Theme.InspectorLabelFontSize);
        value.TextAlign = ContentAlignment.MiddleRight;
        parent.Controls.Add(value, 2, row);
    }

    private void SetTraditionalRowsVisible(bool visible)
    {
        if (_traditionalRowsVisible == visible) return;
        _traditionalRowsVisible = visible;
        SetSettingRowVisible(2, visible);
        SetSettingRowVisible(3, visible);
        MinimumSize = new Size(0, PreferredHeight);
        PreferredHeightChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateResponsiveLayout()
    {
        var scale = DeviceDpi / 96f;
        var compact = ClientSize.Width < 300 * scale;
        if (_compactLayout == compact) return;
        _compactLayout = compact;
        _content.SuspendLayout();
        _content.ColumnStyles[0].Width = (compact ? 56 : 62) * scale;
        _content.ColumnStyles[2].Width = (compact ? 40 : 46) * scale;
        _content.RowStyles[0].Height = (PreviewRowHeight + (compact ? 28 : 0)) * scale;
        _tipActions.Height = (int)Math.Round((compact ? 56 : 28) * scale);
        MinimumSize = new Size(0, PreferredHeight);
        _content.ResumeLayout(true);
        PreferredHeightChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetSettingRowVisible(int row, bool visible)
    {
        _content.RowStyles[row].Height = visible ? row == 2 ? FieldRowHeight : SettingRowHeight : 0;
        for (var column = 0; column < _content.ColumnCount; column++)
        {
            var control = _content.GetControlFromPosition(column, row);
            if (control is not null) control.Visible = visible;
        }
    }

    private void UpdateTraditionalInputState()
    {
        var hasDirectionalShape = _traditionalTip.SelectedIndex == 1 || _traditionalWidth.Value != 100m;
        _traditionalDirection.Enabled = _traditionalRowsVisible && hasDirectionalShape;
        _traditionalDirectionValue.ForeColor = _traditionalDirection.Enabled ? Theme.PanelSecondaryText : Theme.DisabledText;
        _traditionalDirectionValue.Text = _traditionalDirection.Enabled ? $"{_traditionalDirection.Value}°" : "-";
    }

    private sealed class BrushTipPreview : Control
    {
        public BrushShape? BrushShape { get; set; }

        protected override void OnPaint(PaintEventArgs e)
        {
            using var background = new SolidBrush(Theme.Field);
            using var border = new Pen(Theme.ReadableUiColor(Theme.Field, Theme.Border));
            e.Graphics.FillRectangle(background, ClientRectangle);
            e.Graphics.DrawRectangle(border, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
            if (BrushShape is null) return;

            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var center = new PointF(Width * 0.5f, Height * 0.5f);
            var radius = Math.Max(1, Math.Min(Width, Height) * 0.43f);
            foreach (var layer in BrushShape.Layers)
            {
                var contour = BrushShape.NormalizedContour(layer.Threshold)
                    .Select(point => new PointF(center.X + point.X * radius, center.Y + point.Y * radius))
                    .ToArray();
                if (contour.Length < 3) continue;
                using var fill = new SolidBrush(
                    Color.FromArgb(
                        (int)Math.Clamp(layer.Opacity * 255, 0, 255),
                        Theme.ReadableText(Theme.Field, Theme.Text)));
                e.Graphics.FillPolygon(fill, contour);
            }
        }
    }
}
