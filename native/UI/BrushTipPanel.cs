namespace VectorAnimationEngine;

internal sealed class BrushTipPanel : UserControl
{
    private const int SettingsTop = 144;
    private const int RowPitch = 40;

    private readonly Label _title = new();
    private readonly Label _strokeTitle = InspectorModuleStyle.CreateFieldLabel("Brush stroke");
    private readonly Button _import = new();
    private readonly ToolTip _toolTip = new();
    private readonly List<(Label Caption, Control Input, Label? Value)> _rows = [];
    private readonly BrushTipPreview _preview = new();
    private readonly Label _name = InspectorModuleStyle.CreateTextLabel();
    private readonly ComboBox _preset = new();
    private readonly ModernNumericUpDown _size = new();
    private readonly ModernSlider _frequency = new();
    private readonly Label _frequencyValue = InspectorModuleStyle.CreateTextLabel();
    private readonly ModernToggleSwitch _continuous = new();
    private readonly ModernSlider _hardness = new();
    private readonly Label _hardnessValue = InspectorModuleStyle.CreateTextLabel();
    private readonly ComboBox _traditionalTip = new();
    private readonly ModernNumericUpDown _traditionalWidth = new();
    private readonly ModernSlider _traditionalDirection = new();
    private readonly Label _traditionalDirectionValue = InspectorModuleStyle.CreateTextLabel();
    private readonly ModernSlider _pressureSmoothing = new();
    private readonly Label _pressureSmoothingValue = InspectorModuleStyle.CreateTextLabel();
    private bool _updating;
    private bool _traditionalRowsVisible;
    private bool _layoutReady;
    private bool _arranging;
    private bool _compactLayout;
    private int _strokeTop;

    public BrushTipPanel()
    {
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Font = Theme.UiFont();
        Padding = Padding.Empty;
        AccessibleName = "Brush Tip";
        AccessibleRole = AccessibleRole.Pane;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        InspectorModuleStyle.StylePageTitle(_title);
        _title.Text = "Brush Tip";
        _strokeTitle.Dock = DockStyle.None;
        _name.ForeColor = Theme.PanelSecondaryText;
        _name.Font = Theme.UiFont(Theme.InspectorLabelFontSize);
        _preview.AccessibleName = "Brush tip preview";
        _preview.AccessibleRole = AccessibleRole.Graphic;
        _import.Text = "Import";
        _import.AccessibleName = "Import brush tip";
        Theme.StyleStandardButton(_import);
        _import.Click += (_, _) => ImportRequested?.Invoke(this, EventArgs.Empty);
        Theme.StyleToolTip(_toolTip);
        _toolTip.SetToolTip(_preset, UiLocalization.T("Brush style"));
        _preset.DropDownStyle = ComboBoxStyle.DropDownList;
        _preset.AccessibleName = "Brush style";
        _preset.Items.AddRange(["Soft Round", "Traditional Brush"]);
        InspectorModuleStyle.StyleField(_preset);
        _preset.SelectedIndex = 0;
        _preset.SelectedIndexChanged += (_, _) =>
        {
            if (_updating) return;
            if (_preset.SelectedIndex == 0) SoftRoundRequested?.Invoke(this, EventArgs.Empty);
            else if (_preset.SelectedIndex == 1) TraditionalBrushRequested?.Invoke(this, EventArgs.Empty);
        };
        Controls.AddRange([_title, _strokeTitle, _preview, _name, _import, _preset]);

        _size.Minimum = 0.1m;
        _size.Maximum = 128m;
        _size.DecimalPlaces = 1;
        _size.Increment = 0.1m;
        _size.Value = 8m;
        _size.Suffix = "pt";
        _size.ShowSuffix = true;
        _size.ValueChanged += (_, _) =>
        {
            if (!_updating) BrushSizeChanged?.Invoke(this, EventArgs.Empty);
        };
        AddSettingRow("Size", _size);

        _traditionalTip.DropDownStyle = ComboBoxStyle.DropDownList;
        _traditionalTip.AccessibleName = "Traditional brush tip shape";
        _traditionalTip.Items.AddRange(["Round", "Square"]);
        _traditionalTip.SelectedIndex = 0;
        _traditionalTip.SelectedIndexChanged += (_, _) =>
        {
            UpdateTraditionalInputState();
            if (!_updating) TraditionalSettingsChanged?.Invoke(this, EventArgs.Empty);
        };
        AddSettingRow("Shape", _traditionalTip);
        _traditionalWidth.Minimum = 25m;
        _traditionalWidth.Maximum = 400m;
        _traditionalWidth.DecimalPlaces = 0;
        _traditionalWidth.Increment = 5m;
        _traditionalWidth.Suffix = "%";
        _traditionalWidth.ShowSuffix = true;
        _traditionalWidth.Value = 100m;
        _traditionalWidth.AccessibleName = "Traditional brush shape width";
        _traditionalWidth.AccessibleDescription = "Width percentage relative to the brush size";
        _traditionalWidth.ValueChanged += (_, _) =>
        {
            UpdateTraditionalInputState();
            if (!_updating) TraditionalSettingsChanged?.Invoke(this, EventArgs.Empty);
        };
        AddSettingRow("Tip width", _traditionalWidth);
        ConfigureSlider(_traditionalDirection, 0, 359, 0, 45);
        _traditionalDirection.AccessibleName = "Traditional brush direction in degrees";
        _traditionalDirection.ValueChanged += (_, _) =>
        {
            _traditionalDirectionValue.Text = $"{_traditionalDirection.Value}°";
            if (!_updating) TraditionalSettingsChanged?.Invoke(this, EventArgs.Empty);
        };
        AddSettingRow("Angle", _traditionalDirection, _traditionalDirectionValue);
        ConfigureSlider(_frequency, 1, 24, 8, 4);
        _frequency.ValueChanged += (_, _) =>
        {
            _frequencyValue.Text = $"{_frequency.Value}x";
            if (!_updating) FrequencyChanged?.Invoke(this, EventArgs.Empty);
        };
        AddSettingRow("Freq", _frequency, _frequencyValue);
        _continuous.AutoSize = false;
        _continuous.AccessibleName = "Continuous stroke";
        _continuous.Checked = true;
        _continuous.CheckedChanged += (_, _) =>
        {
            if (!_updating) ContinuousChanged?.Invoke(this, EventArgs.Empty);
        };
        AddSettingRow("Continuous", _continuous);
        ConfigureSlider(_hardness, 0, 100, 0, 20);
        _hardness.ValueChanged += (_, _) =>
        {
            _hardnessValue.Text = $"{_hardness.Value}%";
            if (!_updating) HardnessChanged?.Invoke(this, EventArgs.Empty);
        };
        AddSettingRow("Hard", _hardness, _hardnessValue);
        ConfigureSlider(_pressureSmoothing, 0, 100, 70, 20);
        _pressureSmoothing.ValueChanged += (_, _) =>
        {
            _pressureSmoothingValue.Text = $"{_pressureSmoothing.Value}%";
            if (!_updating) PressureSmoothingChanged?.Invoke(this, EventArgs.Empty);
        };
        AddSettingRow("Smooth", _pressureSmoothing, _pressureSmoothingValue);
        _layoutReady = true;
        SetBrushShape(BrushShape.CreateTraditionalBrush());
        PerformLayout();
        UiLocalization.Watch(this);
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
    public int PreferredHeight => ScaleMetric(SettingsTop + (_compactLayout ? 44 : 0)
        + RowPitch * (_traditionalRowsVisible ? 8 : 5) + 48);

    public void SetBrushShape(BrushShape brushShape)
    {
        _updating = true;
        SetTraditionalRowsVisible(brushShape.IsTraditionalBrush);
        _preview.BrushShape = brushShape;
        _name.Text = brushShape.Name;
        _toolTip.SetToolTip(_name, brushShape.Name);
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
        slider.Dock = DockStyle.None;
        slider.Margin = Padding.Empty;
    }

    private void AddSettingRow(string label, Control input, Label? value = null)
    {
        var labelControl = InspectorModuleStyle.CreateFieldLabel(label);
        labelControl.Dock = DockStyle.None;
        labelControl.AutoSize = false;
        input.Dock = DockStyle.None;
        input.AccessibleName ??= label;
        if (input is ComboBox or ModernNumericUpDown) InspectorModuleStyle.StyleField(input);
        input.Dock = DockStyle.None;
        _rows.Add((labelControl, input, value));
        Controls.Add(labelControl);
        Controls.Add(input);
        if (value is null) return;
        value.Dock = DockStyle.None;
        value.AutoSize = false;
        value.BackColor = Theme.Panel;
        value.ForeColor = Theme.PanelSecondaryText;
        value.Font = Theme.UiFont(Theme.InspectorLabelFontSize);
        value.TextAlign = ContentAlignment.MiddleRight;
        Controls.Add(value);
    }

    private void SetTraditionalRowsVisible(bool visible)
    {
        if (_traditionalRowsVisible == visible) return;
        _traditionalRowsVisible = visible;
        PerformLayout();
        PreferredHeightChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnLayout(LayoutEventArgs eventArgs)
    {
        base.OnLayout(eventArgs);
        if (!_layoutReady || _arranging) return;
        _arranging = true;
        try
        {
            var compact = ClientSize.Width < ScaleMetric(300);
            var preferredHeightChanged = _compactLayout != compact;
            _compactLayout = compact;
            var inset = ScaleMetric(16);
            var width = Math.Max(1, ClientSize.Width - inset * 2);
            var right = inset + width;
            var rowHeight = ScaleMetric(Theme.ControlHeight);
            var split = inset + (int)Math.Round(width * 0.44);
            _title.SetBounds(inset + ScaleMetric(24), ScaleMetric(16), Math.Max(1, width - ScaleMetric(24)), ScaleMetric(28));
            _preview.SetBounds(inset, ScaleMetric(70), ScaleMetric(56), ScaleMetric(56));
            var detailsLeft = inset + ScaleMetric(68);
            var detailsWidth = Math.Max(1, right - detailsLeft);
            _name.AutoEllipsis = true;
            _name.AutoSize = false;
            _name.SetBounds(detailsLeft, ScaleMetric(compact ? 86 : 66), detailsWidth, ScaleMetric(24));
            var actionLeft = compact ? inset : detailsLeft;
            var actionWidth = compact ? width : detailsWidth;
            var actionTop = ScaleMetric(compact ? 138 : 94);
            var importWidth = Math.Min(ScaleMetric(64), actionWidth / 2);
            _import.SetBounds(actionLeft, actionTop, importWidth, rowHeight);
            var presetLeft = actionLeft + importWidth + ScaleMetric(6);
            _preset.SetBounds(presetLeft, actionTop, Math.Max(1, right - presetLeft), rowHeight);
            var cursorY = ScaleMetric(SettingsTop + (compact ? 44 : 0));
            var tabIndex = 2;
            foreach (var (caption, input, value) in _rows)
            {
                var traditional = input == _traditionalTip || input == _traditionalWidth || input == _traditionalDirection;
                var rowVisible = !traditional || _traditionalRowsVisible;
                caption.Visible = input.Visible = rowVisible;
                if (value is not null) value.Visible = rowVisible;
                if (traditional && !_traditionalRowsVisible) continue;
                if (input == _frequency)
                {
                    _strokeTop = cursorY;
                    _strokeTitle.SetBounds(inset, cursorY + ScaleMetric(8), width, ScaleMetric(24));
                    cursorY += ScaleMetric(40);
                }
                var captionHeight = Math.Min(rowHeight, caption.Font.Height + ScaleMetric(4));
                caption.SetBounds(inset, cursorY + (rowHeight - captionHeight) / 2,
                    Math.Max(1, split - inset - ScaleMetric(8)), captionHeight);
                var valueWidth = value is null ? 0 : ScaleMetric(38);
                input.SetBounds(split, cursorY, Math.Max(1, right - split - valueWidth), rowHeight);
                input.TabIndex = tabIndex++;
                value?.SetBounds(right - valueWidth, cursorY, valueWidth, rowHeight);
                cursorY += ScaleMetric(RowPitch);
            }
            if (Height != PreferredHeight) Height = PreferredHeight;
            if (preferredHeightChanged) PreferredHeightChanged?.Invoke(this, EventArgs.Empty);
            Invalidate();
        }
        finally { _arranging = false; }
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        SvgIcons.Draw(eventArgs.Graphics, SvgIconKind.Brush,
            new Rectangle(ScaleMetric(16), ScaleMetric(21), ScaleMetric(18), ScaleMetric(18)), Theme.PanelSecondaryText);
        InspectorModuleStyle.DrawDivider(this, eventArgs.Graphics, ScaleMetric(54));
        InspectorModuleStyle.DrawDivider(this, eventArgs.Graphics, _strokeTop);
        InspectorModuleStyle.DrawBottomDivider(this, eventArgs.Graphics);
    }

    protected override void OnDpiChangedAfterParent(EventArgs eventArgs)
    {
        base.OnDpiChangedAfterParent(eventArgs);
        PerformLayout();
        PreferredHeightChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _toolTip.Dispose();
        base.Dispose(disposing);
    }

    private int ScaleMetric(int value) => Math.Max(1, (int)Math.Round(value * DeviceDpi / 96f));

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
