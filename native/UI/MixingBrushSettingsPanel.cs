namespace VectorAnimationEngine;

internal sealed class MixingBrushSettingsPanel : UserControl
{
    private const int PanelPaddingTop = 6;
    private const int PanelPaddingBottom = 6;
    private const int PanelRowCount = 5;
    private const int PanelHeight = PanelPaddingTop
        + PanelPaddingBottom
        + Theme.InspectorTitleHeight
        + 40 + 80 * (PanelRowCount - 1);

    private readonly SegmentedButton _opticalMode = new() { Text = "Optical" };
    private readonly SegmentedButton _pigmentMode = new() { Text = "Pigment" };
    private readonly ModernSlider _strength = new();
    private readonly ModernSlider _viscosity = new();
    private readonly ModernSlider _paintLoad = new();
    private readonly ModernSlider _influence = new();
    private readonly ModernNumericUpDown _strengthValue = CreateValueInput("Mixing strength value");
    private readonly ModernNumericUpDown _viscosityValue = CreateValueInput("Mixing viscosity value");
    private readonly ModernNumericUpDown _paintLoadValue = CreateValueInput("Mixing paint load value");
    private readonly ModernNumericUpDown _influenceValue = CreateValueInput("Mixing influence value");
    private BrushMixingMode _mode = BrushMixingMode.Optical;
    private MixingBrushSettings _settings = new(BrushMixingMode.Optical, 0, 0, 0, 0);
    private bool _updating;

    public MixingBrushSettingsPanel()
    {
        SuspendLayout();
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Font = Theme.UiFont();
        Height = PanelHeight;
        MinimumSize = new Size(0, PanelHeight);
        Padding = new Padding(16, Theme.InspectorSectionPaddingVertical, 16, Theme.InspectorSectionPaddingVertical);
        AccessibleRole = AccessibleRole.Pane;
        AccessibleName = "Mixing brush settings";

        var title = InspectorModuleStyle.CreateTitle("Mixing");
        title.Dock = DockStyle.Top;

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 3,
            RowCount = 5,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 0));
        for (var row = 0; row < content.RowCount; row++)
        {
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, row == 0 ? 40 : 80));
        }

        var modeSelector = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        modeSelector.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        modeSelector.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        modeSelector.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        modeSelector.TabIndex = 0;
        ConfigureModeButton(
            _opticalMode,
            "Optical mixing mode",
            "Mixes the sampled and loaded colors as emitted light",
            new Padding(0, Theme.InspectorRowMarginVertical, 2, Theme.InspectorRowMarginVertical));
        ConfigureModeButton(
            _pigmentMode,
            "Pigment mixing mode",
            "Mixes the sampled and loaded colors as physical pigments",
            new Padding(2, Theme.InspectorRowMarginVertical, 0, Theme.InspectorRowMarginVertical));
        modeSelector.Controls.Add(_opticalMode, 0, 0);
        modeSelector.Controls.Add(_pigmentMode, 1, 0);

        content.Controls.Add(InspectorModuleStyle.CreateFieldLabel("Mode"), 0, 0);
        content.Controls.Add(modeSelector, 1, 0);
        content.SetColumnSpan(modeSelector, 2);

        ConfigureSlider(
            _strength,
            "Mixing strength",
            "Controls how strongly the brush blends sampled color with loaded paint");
        ConfigureSlider(
            _viscosity,
            "Mixing viscosity",
            "Controls how quickly sampled color transfers into the brush");
        ConfigureSlider(
            _paintLoad,
            "Mixing paint load",
            "Controls how much loaded paint the brush carries into the stroke");
        ConfigureSlider(
            _influence,
            "Mixing influence",
            "Controls how quickly the brush returns to the selected fill color after leaving sampled paint");
        _strength.TabIndex = 1;
        _viscosity.TabIndex = 2;
        _paintLoad.TabIndex = 3;
        _influence.TabIndex = 4;
        AddSliderRow(content, "Strength", _strength, _strengthValue, 1);
        AddSliderRow(content, "Viscosity", _viscosity, _viscosityValue, 2);
        AddSliderRow(content, "Paint Load", _paintLoad, _paintLoadValue, 3);
        AddSliderRow(content, "Influence", _influence, _influenceValue, 4);

        _opticalMode.Click += (_, _) => SelectMode(BrushMixingMode.Optical);
        _pigmentMode.Click += (_, _) => SelectMode(BrushMixingMode.Pigment);
        _strength.ValueChanged += (_, _) => SliderValueChanged(_strength, _strengthValue);
        _viscosity.ValueChanged += (_, _) => SliderValueChanged(_viscosity, _viscosityValue);
        _paintLoad.ValueChanged += (_, _) => SliderValueChanged(_paintLoad, _paintLoadValue);
        _influence.ValueChanged += (_, _) => SliderValueChanged(_influence, _influenceValue);
        HookExactValue(_strengthValue, _strength);
        HookExactValue(_viscosityValue, _viscosity);
        HookExactValue(_paintLoadValue, _paintLoad);
        HookExactValue(_influenceValue, _influence);

        Controls.Add(content);
        Controls.Add(title);
        RefreshModeButtons();
        ResumeLayout(performLayout: true);
    }

    public event EventHandler? SettingsChanged;

    public MixingBrushSettings Settings => _settings with { Mode = _mode };

    public int PreferredPanelHeight => PanelHeight;

    public void SetSettings(MixingBrushSettings settings)
    {
        _updating = true;
        try
        {
            _mode = settings.Mode == BrushMixingMode.Pigment
                ? BrushMixingMode.Pigment
                : BrushMixingMode.Optical;
            _settings = new(_mode, settings.Strength, settings.Viscosity, settings.PaintLoad, settings.Influence);
            _strengthValue.Value = ExactPercent(_settings.Strength);
            _viscosityValue.Value = ExactPercent(_settings.Viscosity);
            _paintLoadValue.Value = ExactPercent(_settings.PaintLoad);
            _influenceValue.Value = ExactPercent(_settings.Influence);
            _strength.Value = ToPercent(_settings.Strength);
            _viscosity.Value = ToPercent(_settings.Viscosity);
            _paintLoad.Value = ToPercent(_settings.PaintLoad);
            _influence.Value = ToPercent(_settings.Influence);
            RefreshModeButtons();
        }
        finally
        {
            _updating = false;
        }
    }

    private static void ConfigureModeButton(
        SegmentedButton button,
        string accessibleName,
        string accessibleDescription,
        Padding margin)
    {
        button.Dock = DockStyle.Fill;
        button.Margin = margin;
        button.AccessibleName = accessibleName;
        button.AccessibleDescription = accessibleDescription;
        button.AccessibleRole = AccessibleRole.RadioButton;
        Theme.StyleSegmentedButton(button);
    }

    private static void ConfigureSlider(
        ModernSlider slider,
        string accessibleName,
        string accessibleDescription)
    {
        slider.Minimum = 0;
        slider.Maximum = 100;
        slider.SmallChange = 1;
        slider.LargeChange = 10;
        slider.TickFrequency = 20;
        slider.Dock = DockStyle.Fill;
        slider.Margin = Padding.Empty;
        slider.AccessibleName = accessibleName;
        slider.AccessibleDescription = $"{accessibleDescription}. Percentage from 0 to 100";
    }

    private static void AddSliderRow(
        TableLayoutPanel content,
        string label,
        ModernSlider slider,
        ModernNumericUpDown value,
        int row)
    {
        var fields = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = 2, RowCount = 2
        };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        fields.Controls.Add(InspectorModuleStyle.CreateFieldLabel(label), 0, 0);
        fields.Controls.Add(value, 1, 0);
        fields.Controls.Add(slider, 0, 1);
        fields.SetColumnSpan(slider, 2);
        content.Controls.Add(fields, 0, row);
        content.SetColumnSpan(fields, 3);
    }

    private static ModernNumericUpDown CreateValueInput(string accessibleName)
    {
        var label = new ModernNumericUpDown
        {
            Minimum = 0, Maximum = 100, DecimalPlaces = 2, Increment = 0.1m,
            Suffix = "%", ShowSuffix = true, Dock = DockStyle.Fill, AccessibleName = accessibleName
        };
        InspectorModuleStyle.StyleField(label);
        return label;
    }

    private void HookExactValue(ModernNumericUpDown input, ModernSlider slider)
    {
        input.ValueChanged += (_, _) =>
        {
            if (_updating) return;
            _updating = true;
            try { slider.Value = (int)Math.Round(input.Value); }
            finally { _updating = false; }
            UpdateSetting(input);
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        };
    }

    private void SelectMode(BrushMixingMode mode)
    {
        if (_mode == mode) return;
        _mode = mode;
        RefreshModeButtons();
        if (!_updating) SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SliderValueChanged(ModernSlider slider, ModernNumericUpDown value)
    {
        if (_updating) return;
        _updating = true;
        try { value.Value = slider.Value; }
        finally { _updating = false; }
        UpdateSetting(value);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshModeButtons()
    {
        Theme.StyleSegmentedButton(_opticalMode, _mode == BrushMixingMode.Optical);
        Theme.StyleSegmentedButton(_pigmentMode, _mode == BrushMixingMode.Pigment);
        _opticalMode.Invalidate();
        _pigmentMode.Invalidate();
    }

    private void UpdateSetting(ModernNumericUpDown input)
    {
        var value = (float)(input.Value / 100m);
        if (input == _strengthValue) _settings = _settings with { Strength = value };
        else if (input == _viscosityValue) _settings = _settings with { Viscosity = value };
        else if (input == _paintLoadValue) _settings = _settings with { PaintLoad = value };
        else if (input == _influenceValue) _settings = _settings with { Influence = value };
    }

    private static decimal ExactPercent(float value) => (decimal)Math.Clamp(value, 0, 1) * 100m;

    private static int ToPercent(float value)
    {
        if (!float.IsFinite(value)) return 0;
        return Math.Clamp((int)MathF.Round(value * 100f), 0, 100);
    }
}
