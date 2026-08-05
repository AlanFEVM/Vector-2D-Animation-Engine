namespace VectorAnimationEngine;

internal sealed class MixingBrushSettingsPanel : UserControl
{
    private const int PanelHeight = 210;

    private readonly SegmentedButton _opticalMode = new() { Text = "Optical" };
    private readonly SegmentedButton _pigmentMode = new() { Text = "Pigment" };
    private readonly ModernSlider _strength = new();
    private readonly ModernSlider _viscosity = new();
    private readonly ModernSlider _paintLoad = new();
    private readonly ModernSlider _influence = new();
    private readonly Label _strengthValue = CreateValueLabel("Mixing strength value");
    private readonly Label _viscosityValue = CreateValueLabel("Mixing viscosity value");
    private readonly Label _paintLoadValue = CreateValueLabel("Mixing paint load value");
    private readonly Label _influenceValue = CreateValueLabel("Mixing influence value");
    private BrushMixingMode _mode = BrushMixingMode.Optical;
    private bool _updating;

    public MixingBrushSettingsPanel()
    {
        SuspendLayout();
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Font = Theme.UiFont();
        Height = PanelHeight;
        MinimumSize = new Size(240, PanelHeight);
        Padding = new Padding(12, 8, 12, 8);
        AccessibleRole = AccessibleRole.Pane;
        AccessibleName = "Mixing brush settings";

        var title = new Label
        {
            Text = "Mixing",
            Dock = DockStyle.Top,
            Height = 24,
            BackColor = Color.Transparent,
            ForeColor = Theme.Text,
            Font = Theme.UiFont(10, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 3,
            RowCount = 5,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));
        for (var row = 0; row < content.RowCount; row++)
        {
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
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
            new Padding(0, 3, 2, 3));
        ConfigureModeButton(
            _pigmentMode,
            "Pigment mixing mode",
            "Mixes the sampled and loaded colors as physical pigments",
            new Padding(2, 3, 0, 3));
        modeSelector.Controls.Add(_opticalMode, 0, 0);
        modeSelector.Controls.Add(_pigmentMode, 1, 0);

        content.Controls.Add(CreateFieldLabel("Mode"), 0, 0);
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

        Controls.Add(content);
        Controls.Add(title);
        RefreshModeButtons();
        RefreshValueLabels();
        ResumeLayout(performLayout: true);
    }

    public event EventHandler? SettingsChanged;

    public MixingBrushSettings Settings => new(
        _mode,
        _strength.Value / 100f,
        _viscosity.Value / 100f,
        _paintLoad.Value / 100f,
        _influence.Value / 100f);

    public int PreferredPanelHeight => PanelHeight;

    public void SetSettings(MixingBrushSettings settings)
    {
        _updating = true;
        try
        {
            _mode = settings.Mode == BrushMixingMode.Pigment
                ? BrushMixingMode.Pigment
                : BrushMixingMode.Optical;
            _strength.Value = ToPercent(settings.Strength);
            _viscosity.Value = ToPercent(settings.Viscosity);
            _paintLoad.Value = ToPercent(settings.PaintLoad);
            _influence.Value = ToPercent(settings.Influence);
            RefreshModeButtons();
            RefreshValueLabels();
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
        Label value,
        int row)
    {
        content.Controls.Add(CreateFieldLabel(label), 0, row);
        content.Controls.Add(slider, 1, row);
        content.Controls.Add(value, 2, row);
    }

    private static Label CreateFieldLabel(string text)
    {
        return new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            ForeColor = Theme.Muted,
            Font = Theme.UiFont(),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Margin = new Padding(0, 2, 8, 2)
        };
    }

    private static Label CreateValueLabel(string accessibleName)
    {
        return new Label
        {
            Text = "0%",
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            ForeColor = Theme.Muted,
            Font = Theme.UiFont(),
            TextAlign = ContentAlignment.MiddleRight,
            AutoEllipsis = true,
            Margin = Padding.Empty,
            AccessibleName = accessibleName,
            AccessibleRole = AccessibleRole.StaticText
        };
    }

    private void SelectMode(BrushMixingMode mode)
    {
        if (_mode == mode) return;
        _mode = mode;
        RefreshModeButtons();
        if (!_updating) SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SliderValueChanged(ModernSlider slider, Label value)
    {
        value.Text = $"{slider.Value}%";
        if (!_updating) SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshModeButtons()
    {
        Theme.StyleSegmentedButton(_opticalMode, _mode == BrushMixingMode.Optical);
        Theme.StyleSegmentedButton(_pigmentMode, _mode == BrushMixingMode.Pigment);
        _opticalMode.Invalidate();
        _pigmentMode.Invalidate();
    }

    private void RefreshValueLabels()
    {
        _strengthValue.Text = $"{_strength.Value}%";
        _viscosityValue.Text = $"{_viscosity.Value}%";
        _paintLoadValue.Text = $"{_paintLoad.Value}%";
        _influenceValue.Text = $"{_influence.Value}%";
    }

    private static int ToPercent(float value)
    {
        if (!float.IsFinite(value)) return 0;
        return Math.Clamp((int)MathF.Round(value * 100f), 0, 100);
    }
}
