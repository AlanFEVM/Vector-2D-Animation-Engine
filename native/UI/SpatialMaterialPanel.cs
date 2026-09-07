namespace VectorAnimationEngine;

[Flags]
internal enum SpatialMaterialChangedFields
{
    None = 0,
    Transmission = 1 << 0,
    Reflectivity = 1 << 1,
    Metallic = 1 << 2,
    Roughness = 1 << 3,
    IndexOfRefraction = 1 << 4,
    CastsShadows = 1 << 5,
    ReceivesShadows = 1 << 6,
    All = Transmission | Reflectivity | Metallic | Roughness | IndexOfRefraction | CastsShadows | ReceivesShadows
}

internal enum SpatialMaterialPreset
{
    Default,
    Glass,
    Metal,
    Matte
}

internal sealed class SpatialMaterialChangedEventArgs(
    SpatialOpticalMaterial material,
    SpatialMaterialChangedFields fields) : EventArgs
{
    public SpatialOpticalMaterial Material { get; } = material;
    public SpatialMaterialChangedFields Fields { get; } = fields;
}

internal sealed class SpatialMaterialOverrideChangedEventArgs(
    bool useOverride,
    SpatialOpticalMaterial material) : EventArgs
{
    public bool UseOverride { get; } = useOverride;
    public SpatialOpticalMaterial Material { get; } = material;
}

internal sealed class SpatialMaterialPanel : UserControl
{
    private const int PanelHeight = 286;

    private readonly ModernToggleSwitch _useOverride = new();
    private readonly ComboBox _preset = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _mixedStatus = new();
    private readonly ModernSlider _transmission = PercentSlider("Material transmission");
    private readonly ModernSlider _reflectivity = PercentSlider("Material reflectivity");
    private readonly ModernSlider _metallic = PercentSlider("Material metallic amount");
    private readonly ModernSlider _roughness = PercentSlider("Material roughness");
    private readonly Label _transmissionValue = ValueLabel("Transmission value");
    private readonly Label _reflectivityValue = ValueLabel("Reflectivity value");
    private readonly Label _metallicValue = ValueLabel("Metallic value");
    private readonly Label _roughnessValue = ValueLabel("Roughness value");
    private readonly ModernNumericUpDown _indexOfRefraction = new()
    {
        Minimum = 1m,
        Maximum = 4m,
        Increment = 0.01m,
        DecimalPlaces = 2,
        WheelAdjustsHoveredDigit = true,
        AccessibleName = "Index of refraction",
        AccessibleDescription = "Optical index of refraction from 1 to 4"
    };
    private readonly ModernToggleSwitch _castsShadows = new();
    private readonly ModernToggleSwitch _receivesShadows = new();
    private readonly ToolTip _toolTip = new();
    private readonly HashSet<SpatialMaterialChangedFields> _mixedFields = [];
    private bool _updating;
    private int _interactionDepth;
    private bool _hasSelection;
    private bool _mixedOverride;

    public SpatialMaterialPanel()
    {
        AutoSize = false;
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Font = Theme.UiFont();
        Padding = new Padding(8, 6, 8, 8);
        Height = PanelHeight;
        MinimumSize = new Size(224, PanelHeight);
        AccessibleName = "3D surface material";
        AccessibleDescription = "Edit optical material overrides for selected 3D instances";
        AccessibleRole = AccessibleRole.Pane;

        BuildUi();
        HookEvents();
        Theme.StyleToolTip(_toolTip);
        RefreshText();
        SetMaterials([], enabled: false);
    }

    public int PreferredPanelHeight => PanelHeight;

    public event EventHandler<SpatialMaterialChangedEventArgs>? MaterialChanged;
    public event EventHandler<SpatialMaterialOverrideChangedEventArgs>? OverrideChanged;
    public event EventHandler? InteractionStarted;
    public event EventHandler? InteractionCompleted;
    public event EventHandler? InteractionCanceled;

    public void SetMaterials(IReadOnlyList<SpatialOpticalMaterial?> materials, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(materials);
        _updating = true;
        try
        {
            _hasSelection = enabled && materials.Count > 0;
            var overrideCount = materials.Count(material => material is not null);
            _mixedOverride = overrideCount > 0 && overrideCount < materials.Count;
            _useOverride.ThreeState = true;
            _useOverride.CheckState = !_hasSelection || overrideCount == 0
                ? CheckState.Unchecked
                : _mixedOverride ? CheckState.Indeterminate : CheckState.Checked;

            var effective = materials.Select(material => (material ?? SpatialOpticalMaterial.Default).Normalize()).ToArray();
            var first = effective.FirstOrDefault(SpatialOpticalMaterial.Default);
            _mixedFields.Clear();
            if (effective.Length > 1)
            {
                AddMixed(effective, SpatialMaterialChangedFields.Transmission, value => value.Transmission);
                AddMixed(effective, SpatialMaterialChangedFields.Reflectivity, value => value.Reflectivity);
                AddMixed(effective, SpatialMaterialChangedFields.Metallic, value => value.Metallic);
                AddMixed(effective, SpatialMaterialChangedFields.Roughness, value => value.Roughness);
                AddMixed(effective, SpatialMaterialChangedFields.IndexOfRefraction, value => value.IndexOfRefraction);
                if (effective.Any(value => value.CastsShadows != first.CastsShadows))
                    _mixedFields.Add(SpatialMaterialChangedFields.CastsShadows);
                if (effective.Any(value => value.ReceivesShadows != first.ReceivesShadows))
                    _mixedFields.Add(SpatialMaterialChangedFields.ReceivesShadows);
            }

            _transmission.Value = Percent(first.Transmission);
            _reflectivity.Value = Percent(first.Reflectivity);
            _metallic.Value = Percent(first.Metallic);
            _roughness.Value = Percent(first.Roughness);
            _indexOfRefraction.Value = Math.Clamp((decimal)first.IndexOfRefraction, _indexOfRefraction.Minimum, _indexOfRefraction.Maximum);
            _castsShadows.ThreeState = _mixedFields.Contains(SpatialMaterialChangedFields.CastsShadows);
            _castsShadows.CheckState = _mixedFields.Contains(SpatialMaterialChangedFields.CastsShadows)
                ? CheckState.Indeterminate
                : first.CastsShadows ? CheckState.Checked : CheckState.Unchecked;
            _receivesShadows.ThreeState = _mixedFields.Contains(SpatialMaterialChangedFields.ReceivesShadows);
            _receivesShadows.CheckState = _mixedFields.Contains(SpatialMaterialChangedFields.ReceivesShadows)
                ? CheckState.Indeterminate
                : first.ReceivesShadows ? CheckState.Checked : CheckState.Unchecked;
            _preset.SelectedIndex = -1;
            UpdatePresentation();
        }
        finally
        {
            _updating = false;
        }
    }

    public void RefreshText()
    {
        _useOverride.Text = UiLocalization.T("Override");
        _castsShadows.Text = UiLocalization.T("Cast shadows");
        _receivesShadows.Text = UiLocalization.T("Receive shadows");
        _toolTip.SetToolTip(_useOverride, UiLocalization.T("Use a material override on the selected instances"));
        _toolTip.SetToolTip(_preset, UiLocalization.T("Apply an optical material preset"));
        RefreshPresetItems();
        UpdatePresentation();
        Invalidate(true);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _toolTip.Dispose();
        base.Dispose(disposing);
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 1,
            RowCount = 8,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        for (var index = 0; index < 4; index++) root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        Controls.Add(root);

        root.Controls.Add(BuildHeader(), 0, 0);
        _mixedStatus.Dock = DockStyle.Fill;
        _mixedStatus.ForeColor = Theme.Muted;
        _mixedStatus.BackColor = Color.Transparent;
        _mixedStatus.Font = Theme.UiFont(8.5f);
        _mixedStatus.TextAlign = ContentAlignment.MiddleLeft;
        _mixedStatus.AutoEllipsis = true;
        _mixedStatus.AccessibleRole = AccessibleRole.StaticText;
        root.Controls.Add(_mixedStatus, 0, 1);
        root.Controls.Add(SliderRow("Transmission", _transmission, _transmissionValue), 0, 2);
        root.Controls.Add(SliderRow("Reflectivity", _reflectivity, _reflectivityValue), 0, 3);
        root.Controls.Add(SliderRow("Metallic", _metallic, _metallicValue), 0, 4);
        root.Controls.Add(SliderRow("Roughness", _roughness, _roughnessValue), 0, 5);
        root.Controls.Add(IorRow(), 0, 6);
        root.Controls.Add(ShadowRow(), 0, 7);
    }

    private Control BuildHeader()
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 3,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.Controls.Add(new Label
        {
            Text = "3D Surface",
            Dock = DockStyle.Fill,
            ForeColor = Theme.Text,
            BackColor = Color.Transparent,
            Font = Theme.UiFont(10, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        }, 0, 0);
        _preset.Dock = DockStyle.Fill;
        _preset.Margin = new Padding(2, 2, 4, 2);
        _preset.AccessibleName = "3D material preset";
        Theme.StyleComboBox(_preset);
        _useOverride.Dock = DockStyle.Fill;
        _useOverride.Margin = new Padding(2, 1, 0, 1);
        _useOverride.AccessibleName = "Use material override";
        row.Controls.Add(_preset, 1, 0);
        row.Controls.Add(_useOverride, 2, 0);
        return row;
    }

    private static Control SliderRow(string label, ModernSlider slider, Label value)
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 3,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46));
        row.Controls.Add(FieldLabel(label), 0, 0);
        slider.Dock = DockStyle.Fill;
        slider.Margin = new Padding(2, 3, 2, 3);
        value.Dock = DockStyle.Fill;
        value.Margin = new Padding(2, 2, 0, 2);
        row.Controls.Add(slider, 1, 0);
        row.Controls.Add(value, 2, 0);
        return row;
    }

    private Control IorRow()
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.Controls.Add(FieldLabel("IOR"), 0, 0);
        _indexOfRefraction.Dock = DockStyle.Fill;
        _indexOfRefraction.Margin = new Padding(2, 3, 0, 3);
        Theme.StyleNumeric(_indexOfRefraction);
        row.Controls.Add(_indexOfRefraction, 1, 0);
        return row;
    }

    private Control ShadowRow()
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _castsShadows.Dock = DockStyle.Fill;
        _castsShadows.Margin = new Padding(0, 2, 4, 2);
        _castsShadows.AccessibleName = "Material casts shadows";
        _receivesShadows.Dock = DockStyle.Fill;
        _receivesShadows.Margin = new Padding(4, 2, 0, 2);
        _receivesShadows.AccessibleName = "Material receives shadows";
        row.Controls.Add(_castsShadows, 0, 0);
        row.Controls.Add(_receivesShadows, 1, 0);
        return row;
    }

    private void HookEvents()
    {
        _useOverride.CheckStateChanged += (_, _) => ChangeOverride();
        _preset.SelectedIndexChanged += (_, _) => ApplyPreset();
        HookSlider(_transmission, SpatialMaterialChangedFields.Transmission);
        HookSlider(_reflectivity, SpatialMaterialChangedFields.Reflectivity);
        HookSlider(_metallic, SpatialMaterialChangedFields.Metallic);
        HookSlider(_roughness, SpatialMaterialChangedFields.Roughness);
        _indexOfRefraction.ValueChanged += (_, _) => RaiseMaterialChanged(SpatialMaterialChangedFields.IndexOfRefraction);
        _indexOfRefraction.InteractionStarted += (_, _) => BeginInteraction();
        _indexOfRefraction.InteractionCompleted += (_, _) => CompleteInteraction();
        _indexOfRefraction.InteractionCanceled += (_, _) => CancelInteraction();
        _castsShadows.CheckStateChanged += (_, _) => ChangeShadowToggle(SpatialMaterialChangedFields.CastsShadows);
        _receivesShadows.CheckStateChanged += (_, _) => ChangeShadowToggle(SpatialMaterialChangedFields.ReceivesShadows);
    }

    private void HookSlider(ModernSlider slider, SpatialMaterialChangedFields field)
    {
        slider.ValueChanged += (_, _) => RaiseMaterialChanged(field);
        slider.InteractionStarted += (_, _) => BeginInteraction();
        slider.InteractionCompleted += (_, _) => CompleteInteraction();
        slider.InteractionCanceled += (_, _) => CancelInteraction();
    }

    private void ChangeOverride()
    {
        if (_updating || !_hasSelection) return;
        if (_useOverride.CheckState == CheckState.Indeterminate)
        {
            _updating = true;
            _useOverride.CheckState = CheckState.Checked;
            _updating = false;
        }
        _mixedOverride = false;
        UpdatePresentation();
        OverrideChanged?.Invoke(
            this,
            new SpatialMaterialOverrideChangedEventArgs(_useOverride.Checked, CurrentMaterial()));
    }

    private void ChangeShadowToggle(SpatialMaterialChangedFields field)
    {
        if (_updating || !CanEdit) return;
        var control = field == SpatialMaterialChangedFields.CastsShadows ? _castsShadows : _receivesShadows;
        if (control.CheckState == CheckState.Indeterminate)
        {
            _updating = true;
            control.CheckState = CheckState.Checked;
            _updating = false;
        }
        _mixedFields.Remove(field);
        RaiseMaterialChanged(field);
    }

    private void ApplyPreset()
    {
        if (_updating || !CanEdit || _preset.SelectedItem is not PresetItem item) return;
        var material = PresetMaterial(item.Preset);
        _updating = true;
        try
        {
            _mixedFields.Clear();
            _transmission.Value = Percent(material.Transmission);
            _reflectivity.Value = Percent(material.Reflectivity);
            _metallic.Value = Percent(material.Metallic);
            _roughness.Value = Percent(material.Roughness);
            _indexOfRefraction.Value = (decimal)material.IndexOfRefraction;
            _castsShadows.ThreeState = false;
            _castsShadows.Checked = material.CastsShadows;
            _receivesShadows.ThreeState = false;
            _receivesShadows.Checked = material.ReceivesShadows;
            UpdateValueLabels();
        }
        finally
        {
            _updating = false;
        }
        MaterialChanged?.Invoke(this, new SpatialMaterialChangedEventArgs(material, SpatialMaterialChangedFields.All));
    }

    private void RaiseMaterialChanged(SpatialMaterialChangedFields field)
    {
        if (_updating || !CanEdit) return;
        _mixedFields.Remove(field);
        UpdateValueLabels();
        MaterialChanged?.Invoke(this, new SpatialMaterialChangedEventArgs(CurrentMaterial(), field));
    }

    private SpatialOpticalMaterial CurrentMaterial() => new SpatialOpticalMaterial(
        _transmission.Value / 1000f,
        _reflectivity.Value / 1000f,
        _metallic.Value / 1000f,
        _roughness.Value / 1000f,
        (float)_indexOfRefraction.Value,
        _castsShadows.CheckState != CheckState.Unchecked,
        _receivesShadows.CheckState != CheckState.Unchecked).Normalize();

    private void UpdatePresentation()
    {
        var useOverride = _hasSelection && _useOverride.CheckState != CheckState.Unchecked;
        _useOverride.Enabled = _hasSelection;
        _preset.Enabled = useOverride;
        _transmission.Enabled = useOverride;
        _reflectivity.Enabled = useOverride;
        _metallic.Enabled = useOverride;
        _roughness.Enabled = useOverride;
        _indexOfRefraction.Enabled = useOverride;
        _castsShadows.Enabled = useOverride;
        _receivesShadows.Enabled = useOverride;
        _mixedStatus.Text = !_hasSelection
            ? UiLocalization.T("No 3D instance selected")
            : _mixedOverride || _mixedFields.Count > 0
                ? UiLocalization.T("Mixed material values")
                : _useOverride.Checked
                    ? UiLocalization.T("Instance material override")
                    : UiLocalization.T("Inherited default material");
        UpdateValueLabels();
    }

    private void UpdateValueLabels()
    {
        SetValueLabel(_transmissionValue, _transmission.Value, SpatialMaterialChangedFields.Transmission);
        SetValueLabel(_reflectivityValue, _reflectivity.Value, SpatialMaterialChangedFields.Reflectivity);
        SetValueLabel(_metallicValue, _metallic.Value, SpatialMaterialChangedFields.Metallic);
        SetValueLabel(_roughnessValue, _roughness.Value, SpatialMaterialChangedFields.Roughness);
    }

    private void SetValueLabel(Label label, int value, SpatialMaterialChangedFields field)
    {
        label.Text = _mixedFields.Contains(field) ? UiLocalization.T("Mixed") : $"{value / 10f:0.#}%";
    }

    private void RefreshPresetItems()
    {
        var selected = _preset.SelectedItem is PresetItem item ? item.Preset : (SpatialMaterialPreset?)null;
        _updating = true;
        try
        {
            _preset.Items.Clear();
            foreach (var preset in Enum.GetValues<SpatialMaterialPreset>()) _preset.Items.Add(new PresetItem(preset));
            if (selected is { } value)
            {
                _preset.SelectedIndex = Array.IndexOf(Enum.GetValues<SpatialMaterialPreset>(), value);
            }
        }
        finally
        {
            _updating = false;
        }
    }

    private void BeginInteraction()
    {
        if (_interactionDepth++ > 0) return;
        InteractionStarted?.Invoke(this, EventArgs.Empty);
    }

    private void CompleteInteraction()
    {
        if (_interactionDepth <= 0) return;
        if (--_interactionDepth > 0) return;
        InteractionCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void CancelInteraction()
    {
        if (_interactionDepth <= 0) return;
        _interactionDepth = 0;
        InteractionCanceled?.Invoke(this, EventArgs.Empty);
    }

    private bool CanEdit => _hasSelection && _useOverride.CheckState != CheckState.Unchecked;

    private void AddMixed(
        IReadOnlyList<SpatialOpticalMaterial> values,
        SpatialMaterialChangedFields field,
        Func<SpatialOpticalMaterial, float> selector)
    {
        var first = selector(values[0]);
        if (values.Skip(1).Any(value => Math.Abs(selector(value) - first) > 0.0001f)) _mixedFields.Add(field);
    }

    private static ModernSlider PercentSlider(string accessibleName) => new()
    {
        Minimum = 0,
        Maximum = 1000,
        Value = 0,
        SmallChange = 5,
        LargeChange = 50,
        TickFrequency = 100,
        AccessibleName = accessibleName,
        AccessibleDescription = $"{accessibleName}, zero to one hundred percent"
    };

    private static Label ValueLabel(string accessibleName) => new()
    {
        Text = "0%",
        ForeColor = Theme.Text,
        BackColor = Color.Transparent,
        Font = Theme.UiFont(8.5f),
        TextAlign = ContentAlignment.MiddleRight,
        AutoEllipsis = true,
        AccessibleName = accessibleName,
        AccessibleRole = AccessibleRole.StaticText
    };

    private static Label FieldLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        ForeColor = Theme.Muted,
        BackColor = Color.Transparent,
        Font = Theme.UiFont(8.5f),
        TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true,
        Margin = new Padding(0, 2, 4, 2)
    };

    private static int Percent(float value) => (int)Math.Clamp(MathF.Round(value * 1000f), 0f, 1000f);

    private static SpatialOpticalMaterial PresetMaterial(SpatialMaterialPreset preset) => preset switch
    {
        SpatialMaterialPreset.Glass => new SpatialOpticalMaterial(0.92f, 0.08f, 0f, 0.08f, 1.5f, true, true),
        SpatialMaterialPreset.Metal => new SpatialOpticalMaterial(0f, 0.9f, 1f, 0.18f, 1.5f, true, true),
        SpatialMaterialPreset.Matte => new SpatialOpticalMaterial(0f, 0.02f, 0f, 0.85f, 1.5f, true, true),
        _ => SpatialOpticalMaterial.Default
    };

    private sealed record PresetItem(SpatialMaterialPreset Preset)
    {
        public override string ToString() => UiLocalization.T(Preset switch
        {
            SpatialMaterialPreset.Glass => "Glass",
            SpatialMaterialPreset.Metal => "Metal",
            SpatialMaterialPreset.Matte => "Matte",
            _ => "Default"
        });
    }
}
