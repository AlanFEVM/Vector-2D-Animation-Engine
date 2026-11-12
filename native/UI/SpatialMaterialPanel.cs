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
    private const int PanelHeight = 648;
    private readonly TableLayoutPanel _root = new();
    private readonly ShotDirectorPanel.CameraSectionToggle _surfaceSection = new("3D Surface");
    private readonly ShotDirectorPanel.CameraSectionToggle _shadowSection = new("Shadows");
    private static readonly int[] LogicalRows = [96, 26, 36, 80, 80, 80, 80, 40, 36, 40, 40];
    private int _preferredLogicalHeight = PanelHeight;

    private readonly ModernToggleSwitch _useOverride = new();
    private readonly ComboBox _preset = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _mixedStatus = InspectorModuleStyle.CreateTextLabel();
    private readonly ModernSlider _transmission = PercentSlider("Material transmission");
    private readonly ModernSlider _reflectivity = PercentSlider("Material reflectivity");
    private readonly ModernSlider _metallic = PercentSlider("Material metallic amount");
    private readonly ModernSlider _roughness = PercentSlider("Material roughness");
    private readonly ModernNumericUpDown _transmissionValue = ValueInput("Transmission value");
    private readonly ModernNumericUpDown _reflectivityValue = ValueInput("Reflectivity value");
    private readonly ModernNumericUpDown _metallicValue = ValueInput("Metallic value");
    private readonly ModernNumericUpDown _roughnessValue = ValueInput("Roughness value");
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
    private SpatialOpticalMaterial _currentMaterial = SpatialOpticalMaterial.Default;

    public SpatialMaterialPanel()
    {
        AutoSize = false;
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Font = Theme.UiFont();
        Padding = new Padding(16, 6, 16, 8);
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

    public int PreferredPanelHeight => (int)Math.Round(_preferredLogicalHeight * DeviceDpi / 96f);

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
            _currentMaterial = first;
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
            SetExactValues(first);
            _indexOfRefraction.Value = Math.Clamp((decimal)first.IndexOfRefraction, _indexOfRefraction.Minimum, _indexOfRefraction.Maximum);
            _indexOfRefraction.MixedValue = _mixedFields.Contains(SpatialMaterialChangedFields.IndexOfRefraction);
            _castsShadows.ThreeState = _mixedFields.Contains(SpatialMaterialChangedFields.CastsShadows);
            _castsShadows.CheckState = _mixedFields.Contains(SpatialMaterialChangedFields.CastsShadows)
                ? CheckState.Indeterminate
                : first.CastsShadows ? CheckState.Checked : CheckState.Unchecked;
            _receivesShadows.ThreeState = _mixedFields.Contains(SpatialMaterialChangedFields.ReceivesShadows);
            _receivesShadows.CheckState = _mixedFields.Contains(SpatialMaterialChangedFields.ReceivesShadows)
                ? CheckState.Indeterminate
                : first.ReceivesShadows ? CheckState.Checked : CheckState.Unchecked;
            SelectCurrentPreset(first, _mixedOverride || _mixedFields.Count > 0);
            UpdatePresentation();
        }
        finally
        {
            _updating = false;
        }
    }

    public void RefreshText()
    {
        _useOverride.Text = string.Empty;
        _castsShadows.Text = string.Empty;
        _receivesShadows.Text = string.Empty;
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
        var root = _root;
        root.Dock = DockStyle.Fill;
        root.BackColor = Theme.Panel;
        root.ColumnCount = 1;
        root.RowCount = LogicalRows.Length;
        root.Margin = Padding.Empty;
        root.Padding = Padding.Empty;
        foreach (var height in LogicalRows) root.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
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
        root.Controls.Add(InspectorModuleStyle.CreateSectionHeader("3D Surface", _surfaceSection, LayoutSections), 0, 2);
        root.Controls.Add(SliderRow("Transmission", _transmission, _transmissionValue), 0, 3);
        root.Controls.Add(SliderRow("Reflectivity", _reflectivity, _reflectivityValue), 0, 4);
        root.Controls.Add(SliderRow("Metallic", _metallic, _metallicValue), 0, 5);
        root.Controls.Add(SliderRow("Roughness", _roughness, _roughnessValue), 0, 6);
        root.Controls.Add(IorRow(), 0, 7);
        root.Controls.Add(InspectorModuleStyle.CreateSectionHeader("Shadows", _shadowSection, LayoutSections), 0, 8);
        _castsShadows.Dock = DockStyle.Fill;
        _castsShadows.AccessibleName = "Material casts shadows";
        _receivesShadows.Dock = DockStyle.Fill;
        _receivesShadows.AccessibleName = "Material receives shadows";
        root.Controls.Add(ToggleRow("Cast shadows", _castsShadows), 0, 9);
        root.Controls.Add(ToggleRow("Receive shadows", _receivesShadows), 0, 10);
        LayoutSections();
    }

    private void LayoutSections()
    {
        _root.SuspendLayout();
        try
        {
            var logicalHeight = 14;
            for (var index = 0; index < LogicalRows.Length; index++)
            {
                var visible = index is >= 3 and <= 7 ? _surfaceSection.Expanded
                    : index >= 9 ? _shadowSection.Expanded : true;
                _root.RowStyles[index].Height = visible ? LogicalRows[index] * DeviceDpi / 96f : 0;
                foreach (Control child in _root.Controls)
                    if (_root.GetRow(child) == index) child.Visible = visible;
                if (visible) logicalHeight += LogicalRows[index];
            }
            _preferredLogicalHeight = logicalHeight;
            MinimumSize = new Size((int)Math.Round(224 * DeviceDpi / 96f), PreferredPanelHeight);
            Height = PreferredPanelHeight;
        }
        finally { _root.ResumeLayout(true); }
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        LayoutSections();
    }

    private static Control ToggleRow(string text, ModernToggleSwitch toggle)
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty, Padding = Padding.Empty
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
        row.Controls.Add(FieldLabel(text), 0, 0);
        InspectorModuleStyle.StyleField(toggle);
        row.Controls.Add(toggle, 1, 0);
        return row;
    }

    private Control BuildHeader()
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 2,
            RowCount = 3,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
        row.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        row.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        row.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        var title = InspectorModuleStyle.CreateTitle("3D Surface");
        row.Controls.Add(title, 0, 0);
        row.SetColumnSpan(title, 2);
        _preset.Dock = DockStyle.Fill;
        _preset.Margin = new Padding(2, 2, 4, 2);
        _preset.AccessibleName = "3D material preset";
        InspectorModuleStyle.StyleField(_preset);
        _useOverride.Dock = DockStyle.Fill;
        _useOverride.Margin = new Padding(2, 1, 0, 1);
        _useOverride.AccessibleName = "Use material override";
        row.Controls.Add(FieldLabel("Override"), 0, 1);
        row.Controls.Add(_useOverride, 1, 1);
        row.Controls.Add(FieldLabel("Preset"), 0, 2);
        row.Controls.Add(_preset, 1, 2);
        return row;
    }

    private static Control SliderRow(string label, ModernSlider slider, ModernNumericUpDown value)
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 2,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
        row.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        row.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        row.Controls.Add(FieldLabel(label), 0, 0);
        slider.Dock = DockStyle.Fill;
        slider.Margin = new Padding(0, 4, 0, 4);
        value.Dock = DockStyle.Fill;
        value.Margin = new Padding(0, 4, 0, 4);
        row.Controls.Add(slider, 0, 1);
        row.SetColumnSpan(slider, 2);
        row.Controls.Add(value, 1, 0);
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
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
        row.Controls.Add(FieldLabel("IOR"), 0, 0);
        _indexOfRefraction.Dock = DockStyle.Fill;
        _indexOfRefraction.Margin = new Padding(2, 3, 0, 3);
        InspectorModuleStyle.StyleField(_indexOfRefraction);
        row.Controls.Add(_indexOfRefraction, 1, 0);
        return row;
    }

    private void HookEvents()
    {
        _useOverride.CheckStateChanged += (_, _) => ChangeOverride();
        _preset.SelectedIndexChanged += (_, _) => ApplyPreset();
        HookSlider(_transmission, _transmissionValue, SpatialMaterialChangedFields.Transmission);
        HookSlider(_reflectivity, _reflectivityValue, SpatialMaterialChangedFields.Reflectivity);
        HookSlider(_metallic, _metallicValue, SpatialMaterialChangedFields.Metallic);
        HookSlider(_roughness, _roughnessValue, SpatialMaterialChangedFields.Roughness);
        HookExactValue(_transmissionValue, _transmission, SpatialMaterialChangedFields.Transmission);
        HookExactValue(_reflectivityValue, _reflectivity, SpatialMaterialChangedFields.Reflectivity);
        HookExactValue(_metallicValue, _metallic, SpatialMaterialChangedFields.Metallic);
        HookExactValue(_roughnessValue, _roughness, SpatialMaterialChangedFields.Roughness);
        _indexOfRefraction.ValueChanged += (_, _) => RaiseMaterialChanged(SpatialMaterialChangedFields.IndexOfRefraction);
        _indexOfRefraction.InteractionStarted += (_, _) => BeginInteraction();
        _indexOfRefraction.InteractionCompleted += (_, _) => CompleteInteraction();
        _indexOfRefraction.InteractionCanceled += (_, _) => CancelInteraction();
        _castsShadows.CheckStateChanged += (_, _) => ChangeShadowToggle(SpatialMaterialChangedFields.CastsShadows);
        _receivesShadows.CheckStateChanged += (_, _) => ChangeShadowToggle(SpatialMaterialChangedFields.ReceivesShadows);
    }

    private void HookSlider(ModernSlider slider, ModernNumericUpDown input, SpatialMaterialChangedFields field)
    {
        slider.ValueChanged += (_, _) =>
        {
            if (_updating || !CanEdit) return;
            _updating = true;
            try { input.Value = slider.Value / 10m; }
            finally { _updating = false; }
            RaiseMaterialChanged(field);
        };
        slider.InteractionStarted += (_, _) => BeginInteraction();
        slider.InteractionCompleted += (_, _) => CompleteInteraction();
        slider.InteractionCanceled += (_, _) => CancelInteraction();
    }

    private void HookExactValue(ModernNumericUpDown input, ModernSlider slider, SpatialMaterialChangedFields field)
    {
        input.InteractionStarted += (_, _) => BeginInteraction();
        input.InteractionCompleted += (_, _) => CompleteInteraction();
        input.InteractionCanceled += (_, _) => CancelInteraction();
        input.ValueChanged += (_, _) =>
        {
            if (_updating || !CanEdit) return;
            _updating = true;
            try { slider.Value = (int)Math.Round(input.Value * 10); }
            finally { _updating = false; }
            RaiseMaterialChanged(field);
        };
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
        _currentMaterial = material;
        _updating = true;
        try
        {
            _mixedFields.Clear();
            _transmission.Value = Percent(material.Transmission);
            _reflectivity.Value = Percent(material.Reflectivity);
            _metallic.Value = Percent(material.Metallic);
            _roughness.Value = Percent(material.Roughness);
            SetExactValues(material);
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
        _currentMaterial = field switch
        {
            SpatialMaterialChangedFields.Transmission => _currentMaterial with { Transmission = (float)(_transmissionValue.Value / 100m) },
            SpatialMaterialChangedFields.Reflectivity => _currentMaterial with { Reflectivity = (float)(_reflectivityValue.Value / 100m) },
            SpatialMaterialChangedFields.Metallic => _currentMaterial with { Metallic = (float)(_metallicValue.Value / 100m) },
            SpatialMaterialChangedFields.Roughness => _currentMaterial with { Roughness = (float)(_roughnessValue.Value / 100m) },
            SpatialMaterialChangedFields.IndexOfRefraction => _currentMaterial with { IndexOfRefraction = (float)_indexOfRefraction.Value },
            SpatialMaterialChangedFields.CastsShadows => _currentMaterial with { CastsShadows = _castsShadows.CheckState != CheckState.Unchecked },
            SpatialMaterialChangedFields.ReceivesShadows => _currentMaterial with { ReceivesShadows = _receivesShadows.CheckState != CheckState.Unchecked },
            _ => _currentMaterial
        };
        UpdateValueLabels();
        SelectCurrentPreset(CurrentMaterial(), _mixedOverride || _mixedFields.Count > 0);
        MaterialChanged?.Invoke(this, new SpatialMaterialChangedEventArgs(CurrentMaterial(), field));
    }

    private SpatialOpticalMaterial CurrentMaterial() => _currentMaterial;

    private void UpdatePresentation()
    {
        var useOverride = _hasSelection && _useOverride.CheckState != CheckState.Unchecked;
        _useOverride.Enabled = _hasSelection;
        _preset.Enabled = useOverride;
        _transmission.Enabled = useOverride;
        _reflectivity.Enabled = useOverride;
        _metallic.Enabled = useOverride;
        _roughness.Enabled = useOverride;
        foreach (var input in new[] { _transmissionValue, _reflectivityValue, _metallicValue, _roughnessValue })
            input.Enabled = useOverride;
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
        _transmissionValue.MixedValue = _mixedFields.Contains(SpatialMaterialChangedFields.Transmission);
        _reflectivityValue.MixedValue = _mixedFields.Contains(SpatialMaterialChangedFields.Reflectivity);
        _metallicValue.MixedValue = _mixedFields.Contains(SpatialMaterialChangedFields.Metallic);
        _roughnessValue.MixedValue = _mixedFields.Contains(SpatialMaterialChangedFields.Roughness);
    }

    private void SetExactValues(SpatialOpticalMaterial material)
    {
        _transmissionValue.Value = (decimal)material.Transmission * 100m;
        _reflectivityValue.Value = (decimal)material.Reflectivity * 100m;
        _metallicValue.Value = (decimal)material.Metallic * 100m;
        _roughnessValue.Value = (decimal)material.Roughness * 100m;
    }

    private void SelectCurrentPreset(SpatialOpticalMaterial material, bool mixed)
    {
        var previous = _updating;
        _updating = true;
        try
        {
            for (var index = _preset.Items.Count - 1; index >= 0; index--)
                if (_preset.Items[index] is not PresetItem) _preset.Items.RemoveAt(index);
            var match = mixed ? -1 : Array.FindIndex(Enum.GetValues<SpatialMaterialPreset>(),
                preset => PresetMaterial(preset).Normalize() == material.Normalize());
            if (match >= 0) _preset.SelectedIndex = match;
            else _preset.SelectedIndex = _preset.Items.Add(UiLocalization.T(mixed ? "Mixed" : "Custom"));
        }
        finally { _updating = previous; }
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
            else SelectCurrentPreset(CurrentMaterial(), _mixedOverride || _mixedFields.Count > 0);
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

    private static ModernNumericUpDown ValueInput(string accessibleName)
    {
        var input = new ModernNumericUpDown
        {
            Minimum = 0, Maximum = 100, DecimalPlaces = 4, Increment = 0.1m,
            Suffix = "%", ShowSuffix = true, AccessibleName = accessibleName, Dock = DockStyle.Fill
        };
        InspectorModuleStyle.StyleField(input);
        return input;
    }

    private static Label FieldLabel(string text) => InspectorModuleStyle.CreateFieldLabel(text);

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
