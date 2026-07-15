namespace VectorAnimationEngine;

internal sealed class BrushTipPanel : UserControl
{
    private readonly BrushTipPreview _preview = new();
    private readonly Label _name = new();
    private readonly ComboBox _preset = new();
    private readonly ModernNumericUpDown _size = new();
    private readonly Label _sizeUnit = new();
    private readonly ModernSlider _frequency = new();
    private readonly Label _frequencyValue = new();
    private readonly CheckBox _continuous = new();
    private readonly ModernSlider _hardness = new();
    private readonly Label _hardnessValue = new();
    private readonly ComboBox _traditionalTip = new();
    private readonly ModernNumericUpDown _traditionalWidth = new();
    private readonly ModernSlider _traditionalDirection = new();
    private readonly Label _traditionalDirectionValue = new();
    private readonly ModernSlider _pressureSmoothing = new();
    private readonly Label _pressureSmoothingValue = new();
    private readonly TableLayoutPanel _content;
    private bool _updating;
    private bool _traditionalRowsVisible;

    public BrushTipPanel()
    {
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Font = Theme.UiFont();
        Padding = new Padding(0, 8, 0, 8);
        MinimumSize = new Size(280, 248);

        var title = new Label
        {
            Text = "Brush Tip",
            Dock = DockStyle.Top,
            Height = 24,
            BackColor = Theme.Panel,
            ForeColor = Theme.Text,
            Font = Theme.UiFont(10, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        };
        _content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 3,
            RowCount = 8,
            Padding = new Padding(0, 2, 0, 0)
        };
        _content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62));
        _content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46));
        _content.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        for (var row = 1; row < _content.RowCount; row++) _content.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));

        _preview.Dock = DockStyle.Fill;
        _preview.Margin = new Padding(0, 2, 8, 4);
        _content.Controls.Add(_preview, 0, 0);

        var tipDetails = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Panel, Margin = Padding.Empty };
        _name.Dock = DockStyle.Top;
        _name.Height = 24;
        _name.BackColor = Theme.Panel;
        _name.ForeColor = Theme.Muted;
        _name.Font = Theme.UiFont();
        _name.TextAlign = ContentAlignment.MiddleLeft;
        tipDetails.Controls.Add(_name);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 28,
            BackColor = Theme.Panel,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = Padding.Empty,
            Margin = Padding.Empty
        };
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
        _preset.DropDownStyle = ComboBoxStyle.DropDownList;
        _preset.FlatStyle = FlatStyle.Flat;
        _preset.BackColor = Theme.Field;
        _preset.ForeColor = Theme.Text;
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

        _size.Minimum = 0.5m;
        _size.Maximum = 128m;
        _size.DecimalPlaces = 1;
        _size.Increment = 0.5m;
        _size.Value = 8m;
        _size.Suffix = "pt";
        _size.ValueChanged += (_, _) =>
        {
            if (!_updating) BrushSizeChanged?.Invoke(this, EventArgs.Empty);
        };
        _sizeUnit.Text = "pt";
        AddSettingRow(_content, "Size", _size, _sizeUnit, 1);

        Theme.StyleComboBox(_traditionalTip);
        _traditionalTip.DropDownStyle = ComboBoxStyle.DropDownList;
        _traditionalTip.Width = 96;
        _traditionalTip.Dock = DockStyle.Left;
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
        var shapeInput = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Panel, Margin = Padding.Empty };
        shapeInput.Controls.Add(_traditionalWidth);
        shapeInput.Controls.Add(_traditionalTip);
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
        _continuous.ForeColor = Theme.Text;
        _continuous.BackColor = Theme.Panel;
        _continuous.FlatStyle = FlatStyle.Flat;
        _continuous.Font = Theme.UiFont();
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
    public int PreferredHeight => _traditionalRowsVisible ? 308 : 248;

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
        parent.Controls.Add(new Label
        {
            Text = label,
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ForeColor = Theme.Muted,
            Font = Theme.UiFont(),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Margin = new Padding(0, 2, 8, 2)
        }, 0, row);

        input.Dock = input is CheckBox ? DockStyle.Left : DockStyle.Fill;
        parent.Controls.Add(input, 1, row);
        if (value is null) return;
        value.Dock = DockStyle.Fill;
        value.BackColor = Theme.Panel;
        value.ForeColor = Theme.Muted;
        value.Font = Theme.UiFont();
        value.TextAlign = ContentAlignment.MiddleRight;
        parent.Controls.Add(value, 2, row);
    }

    private void SetTraditionalRowsVisible(bool visible)
    {
        if (_traditionalRowsVisible == visible) return;
        _traditionalRowsVisible = visible;
        SetSettingRowVisible(2, visible);
        SetSettingRowVisible(3, visible);
        MinimumSize = new Size(280, PreferredHeight);
        PreferredHeightChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetSettingRowVisible(int row, bool visible)
    {
        _content.RowStyles[row].Height = visible ? 30 : 0;
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
        _traditionalDirectionValue.ForeColor = _traditionalDirection.Enabled ? Theme.Muted : Theme.DisabledText;
        _traditionalDirectionValue.Text = _traditionalDirection.Enabled ? $"{_traditionalDirection.Value}°" : "-";
    }

    private sealed class BrushTipPreview : Control
    {
        public BrushShape? BrushShape { get; set; }

        protected override void OnPaint(PaintEventArgs e)
        {
            using var background = new SolidBrush(Theme.Field);
            using var border = new Pen(Theme.Border);
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
                using var fill = new SolidBrush(Color.FromArgb((int)Math.Clamp(layer.Opacity * 255, 0, 255), Theme.Text));
                e.Graphics.FillPolygon(fill, contour);
            }
        }
    }
}
