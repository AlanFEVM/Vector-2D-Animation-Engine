using System.Numerics;

namespace VectorAnimationEngine;

[Flags]
internal enum SceneLightChangedFields
{
    None = 0,
    Name = 1 << 0,
    Kind = 1 << 1,
    Enabled = 1 << 2,
    Color = 1 << 3,
    Intensity = 1 << 4,
    Range = 1 << 5,
    Position = 1 << 6,
    Rotation = 1 << 7,
    AreaSize = 1 << 8,
    CastShadows = 1 << 9,
    ShadowStrength = 1 << 10,
    ShadowSoftness = 1 << 11
}

internal readonly record struct SceneLightEditorState(
    string Id,
    string Name,
    SceneLightKind Kind,
    bool Enabled,
    Color Color,
    float Intensity,
    float Range,
    Vector3 Position,
    Vector3 Rotation,
    float AreaWidth,
    float AreaHeight,
    bool CastShadows,
    float ShadowStrength,
    float ShadowSoftness,
    bool ParametersEditable = true);

internal sealed class SceneLightAddRequestedEventArgs(SceneLightKind kind) : EventArgs
{
    public SceneLightKind Kind { get; } = kind;
}

internal sealed class SceneLightSelectionChangedEventArgs(string lightId) : EventArgs
{
    public string LightId { get; } = lightId;
}

internal sealed class SceneLightChangedEventArgs(
    SceneLightEditorState state,
    SceneLightChangedFields fields) : EventArgs
{
    public SceneLightEditorState State { get; } = state;
    public SceneLightChangedFields Fields { get; } = fields;
}

internal sealed class SceneLightCommandEventArgs(string lightId) : EventArgs
{
    public string LightId { get; } = lightId;
}

internal sealed class SceneLightingPanel : UserControl
{
    private const int PanelHeight = 424;
    private const decimal SpatialLimit = 5_000_000m;

    private readonly ListBox _lights = new();
    private readonly SvgIconButton _add = new(SvgIconKind.Add);
    private readonly SvgIconButton _duplicate = new(SvgIconKind.Objects);
    private readonly SvgIconButton _remove = new(SvgIconKind.Remove);
    private readonly SvgIconButton _reset = new(SvgIconKind.RestoreSize);
    private readonly AnimatedContextMenuStrip _addMenu = new();
    private readonly Dictionary<SceneLightKind, Bitmap> _addMenuLightIcons = [];
    private readonly TextBox _name = new();
    private readonly ComboBox _kind = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ModernToggleSwitch _enabled = new();
    private readonly ColorTargetButton _color = new("Color");
    private readonly ModernNumericUpDown _intensity = PercentInput("Light intensity", 100_000m);
    private readonly ModernNumericUpDown _range = SpatialInput("Light range", minimum: 0.01m);
    private readonly ModernNumericUpDown[] _position =
    [
        SpatialInput("Light position X"),
        SpatialInput("Light position Y"),
        SpatialInput("Light position Z")
    ];
    private readonly ModernNumericUpDown[] _rotation =
    [
        RotationInput("Light direction X"),
        RotationInput("Light direction Y"),
        RotationInput("Light direction Z")
    ];
    private readonly ModernNumericUpDown _areaWidth = SpatialInput("Area light width", minimum: 0.01m);
    private readonly ModernNumericUpDown _areaHeight = SpatialInput("Area light height", minimum: 0.01m);
    private readonly ModernToggleSwitch _castShadows = new();
    private readonly ModernNumericUpDown _shadowStrength = PercentInput("Shadow strength");
    private readonly ModernNumericUpDown _shadowSoftness = PercentInput("Shadow softness");
    private readonly ToolTip _toolTip = new();
    private readonly List<SceneLightEditorState> _states = [];
    private bool _updating;
    private int _interactionDepth;
    private string _selectedLightId = string.Empty;
    private string _nameEditStart = string.Empty;
    private int _hoveredLightIndex = -1;

    public SceneLightingPanel()
    {
        AutoSize = false;
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Font = Theme.UiFont();
        Padding = new Padding(8, 6, 8, 8);
        Height = PanelHeight;
        MinimumSize = new Size(224, PanelHeight);
        AccessibleName = "Scene lighting";
        AccessibleDescription = "Edit lights in the active 3D scene";
        AccessibleRole = AccessibleRole.Pane;

        BuildUi();
        BuildAddMenu();
        HookEvents();
        Theme.StyleToolTip(_toolTip);
        RefreshText();
        SetLights([], null);
    }

    public int PreferredPanelHeight => PanelHeight;
    public string SelectedLightId => _selectedLightId;

    public event EventHandler<SceneLightAddRequestedEventArgs>? AddRequested;
    public event EventHandler<SceneLightSelectionChangedEventArgs>? SelectionChanged;
    public event EventHandler<SceneLightChangedEventArgs>? LightChanged;
    public event EventHandler<SceneLightCommandEventArgs>? DuplicateRequested;
    public event EventHandler<SceneLightCommandEventArgs>? RemoveRequested;
    public event EventHandler<SceneLightCommandEventArgs>? ResetRequested;
    public event EventHandler? InteractionStarted;
    public event EventHandler? InteractionCompleted;
    public event EventHandler? InteractionCanceled;

    public void SetLights(IReadOnlyList<SceneLightEditorState> lights, string? selectedLightId)
    {
        ArgumentNullException.ThrowIfNull(lights);
        var nextId = selectedLightId is null
            ? lights.Any(item => string.Equals(item.Id, _selectedLightId, StringComparison.Ordinal))
                ? _selectedLightId
                : lights.FirstOrDefault().Id ?? string.Empty
            : lights.Any(item => string.Equals(item.Id, selectedLightId, StringComparison.Ordinal))
                ? selectedLightId
                : string.Empty;

        _updating = true;
        try
        {
            _states.Clear();
            _states.AddRange(lights);
            _selectedLightId = nextId;
            RefreshListItems();
            LoadSelectedState();
        }
        finally
        {
            _updating = false;
        }
    }

    public void RefreshText()
    {
        _enabled.Text = UiLocalization.T("Enabled");
        _castShadows.Text = UiLocalization.T("Cast shadows");
        _color.Text = UiLocalization.T("Color");
        _toolTip.SetToolTip(_add, UiLocalization.T("Add light"));
        _toolTip.SetToolTip(_duplicate, UiLocalization.T("Duplicate light"));
        _toolTip.SetToolTip(_remove, UiLocalization.T("Remove light"));
        _toolTip.SetToolTip(_reset, UiLocalization.T("Reset light"));
        RefreshKindItems();
        RefreshAddMenuText();
        RefreshListItems(refreshText: true);
        Invalidate(true);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (ToolStripItem item in _addMenu.Items) item.Image = null;
            _addMenu.Dispose();
            foreach (var icon in _addMenuLightIcons.Values) icon.Dispose();
            _addMenuLightIcons.Clear();
            _toolTip.Dispose();
        }
        base.Dispose(disposing);
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 1,
            RowCount = 10,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        Controls.Add(root);

        root.Controls.Add(BuildHeader(), 0, 0);
        _lights.Dock = DockStyle.Fill;
        _lights.IntegralHeight = false;
        _lights.Margin = new Padding(0, 2, 0, 4);
        _lights.AccessibleName = "Scene lights";
        Theme.StyleListBox(_lights);
        _lights.DrawItem += DrawLightListItem;
        _lights.MouseMove += (_, e) => SetHoveredLightIndex(_lights.IndexFromPoint(e.Location));
        _lights.MouseLeave += (_, _) => SetHoveredLightIndex(-1);
        root.Controls.Add(_lights, 0, 1);
        root.Controls.Add(BuildNameRow(), 0, 2);
        root.Controls.Add(BuildTypeRow(), 0, 3);

        _color.Dock = DockStyle.Fill;
        _color.Margin = new Padding(66, 3, 0, 3);
        _color.AccessibleName = "Light color";
        root.Controls.Add(_color, 0, 4);
        root.Controls.Add(BuildPairRow("Intensity", _intensity, "Range", _range), 0, 5);
        root.Controls.Add(BuildAxisRow("Position", _position), 0, 6);
        root.Controls.Add(BuildAxisRow("Direction", _rotation), 0, 7);
        root.Controls.Add(BuildPairRow("Area width", _areaWidth, "Height", _areaHeight), 0, 8);
        root.Controls.Add(BuildShadowRow(), 0, 9);
    }

    private Control BuildHeader()
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 5,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var index = 0; index < 4; index++) row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 30));
        row.Controls.Add(new Label
        {
            Text = "Lighting",
            Dock = DockStyle.Fill,
            ForeColor = Theme.Text,
            BackColor = Color.Transparent,
            Font = Theme.UiFont(10, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        }, 0, 0);
        ConfigureHeaderButton(_add, "Add light");
        ConfigureHeaderButton(_duplicate, "Duplicate light");
        ConfigureHeaderButton(_remove, "Remove light");
        ConfigureHeaderButton(_reset, "Reset light");
        row.Controls.Add(_add, 1, 0);
        row.Controls.Add(_duplicate, 2, 0);
        row.Controls.Add(_remove, 3, 0);
        row.Controls.Add(_reset, 4, 0);
        return row;
    }

    private Control BuildNameRow()
    {
        Theme.StyleTextBox(_name);
        _name.Dock = DockStyle.Fill;
        _name.Margin = new Padding(2, 2, 0, 2);
        _name.AccessibleName = "Light name";
        return FieldRow("Name", _name);
    }

    private Control BuildTypeRow()
    {
        Theme.StyleComboBox(_kind);
        _kind.Dock = DockStyle.Fill;
        _kind.Margin = new Padding(2, 2, 4, 2);
        _kind.AccessibleName = "Light type";
        _enabled.Dock = DockStyle.Fill;
        _enabled.Margin = new Padding(4, 1, 0, 1);
        _enabled.AccessibleName = "Light enabled";
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 3,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44));
        row.Controls.Add(FieldLabel("Type"), 0, 0);
        row.Controls.Add(_kind, 1, 0);
        row.Controls.Add(_enabled, 2, 0);
        return row;
    }

    private Control BuildAxisRow(string label, IReadOnlyList<ModernNumericUpDown> inputs)
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 4,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
        for (var index = 0; index < 3; index++) row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3f));
        row.Controls.Add(FieldLabel(label), 0, 0);
        for (var index = 0; index < 3; index++)
        {
            var input = inputs[index];
            input.Dock = DockStyle.Fill;
            input.Margin = new Padding(index == 0 ? 2 : 3, 3, 0, 3);
            Theme.StyleNumeric(input);
            row.Controls.Add(input, index + 1, 0);
        }
        return row;
    }

    private Control BuildPairRow(string firstLabel, Control first, string secondLabel, Control second)
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 4,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        row.Controls.Add(FieldLabel(firstLabel), 0, 0);
        row.Controls.Add(FieldLabel(secondLabel), 2, 0);
        first.Dock = DockStyle.Fill;
        first.Margin = new Padding(2, 3, 3, 3);
        second.Dock = DockStyle.Fill;
        second.Margin = new Padding(2, 3, 0, 3);
        row.Controls.Add(first, 1, 0);
        row.Controls.Add(second, 3, 0);
        return row;
    }

    private Control BuildShadowRow()
    {
        _castShadows.Dock = DockStyle.Fill;
        _castShadows.Margin = new Padding(0, 2, 4, 2);
        _castShadows.AccessibleName = "Cast shadows";
        Theme.StyleNumeric(_shadowStrength);
        Theme.StyleNumeric(_shadowSoftness);
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 5,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 29));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 29));
        row.Controls.Add(_castShadows, 0, 0);
        row.Controls.Add(FieldLabel("Strength"), 1, 0);
        _shadowStrength.Dock = DockStyle.Fill;
        _shadowStrength.Margin = new Padding(2, 3, 3, 3);
        row.Controls.Add(_shadowStrength, 2, 0);
        row.Controls.Add(FieldLabel("Softness"), 3, 0);
        _shadowSoftness.Dock = DockStyle.Fill;
        _shadowSoftness.Margin = new Padding(2, 3, 0, 3);
        row.Controls.Add(_shadowSoftness, 4, 0);
        return row;
    }

    private void BuildAddMenu()
    {
        foreach (var kind in new[] { SceneLightKind.Directional, SceneLightKind.Point, SceneLightKind.Area, SceneLightKind.Ambient })
        {
            var icon = SvgIcons.CreateBitmap(LightIconKind(kind), new Size(18, 18), Theme.Text, DeviceDpi);
            _addMenuLightIcons.Add(kind, icon);
            var item = new ToolStripMenuItem { Tag = kind, Image = icon };
            item.Click += (_, _) => AddRequested?.Invoke(this, new SceneLightAddRequestedEventArgs(kind));
            _addMenu.Items.Add(item);
        }
    }

    private void HookEvents()
    {
        _add.Click += (_, _) => _addMenu.Show(_add, new Point(0, _add.Height));
        _duplicate.Click += (_, _) => RaiseCommand(DuplicateRequested);
        _remove.Click += (_, _) => RaiseCommand(RemoveRequested);
        _reset.Click += (_, _) => RaiseCommand(ResetRequested);
        _lights.SelectedIndexChanged += (_, _) => SelectListItem();
        _kind.SelectedIndexChanged += (_, _) => ChangeKind();
        _enabled.CheckedChanged += (_, _) => ChangeToggle(SceneLightChangedFields.Enabled);
        _castShadows.CheckedChanged += (_, _) => ChangeToggle(SceneLightChangedFields.CastShadows);
        _color.Click += (_, _) => ChooseColor();
        _name.Enter += (_, _) => _nameEditStart = _name.Text;
        _name.Leave += (_, _) => CommitName();
        _name.KeyDown += HandleNameKeyDown;

        HookNumeric(_intensity, SceneLightChangedFields.Intensity);
        HookNumeric(_range, SceneLightChangedFields.Range);
        foreach (var input in _position) HookNumeric(input, SceneLightChangedFields.Position);
        foreach (var input in _rotation) HookNumeric(input, SceneLightChangedFields.Rotation);
        HookNumeric(_areaWidth, SceneLightChangedFields.AreaSize);
        HookNumeric(_areaHeight, SceneLightChangedFields.AreaSize);
        HookNumeric(_shadowStrength, SceneLightChangedFields.ShadowStrength);
        HookNumeric(_shadowSoftness, SceneLightChangedFields.ShadowSoftness);
    }

    private void HookNumeric(ModernNumericUpDown input, SceneLightChangedFields field)
    {
        Theme.StyleNumeric(input);
        input.ValueChanged += (_, _) => RaiseChanged(field);
        input.InteractionStarted += (_, _) => BeginInteraction();
        input.InteractionCompleted += (_, _) => CompleteInteraction();
        input.InteractionCanceled += (_, _) => CancelInteraction();
    }

    private void SelectListItem()
    {
        if (_updating) return;
        var nextId = _lights.SelectedItem is LightListItem item ? item.State.Id : string.Empty;
        if (string.Equals(_selectedLightId, nextId, StringComparison.Ordinal)) return;
        _selectedLightId = nextId;
        _updating = true;
        try
        {
            LoadSelectedState();
        }
        finally
        {
            _updating = false;
        }
        SelectionChanged?.Invoke(this, new SceneLightSelectionChangedEventArgs(_selectedLightId));
    }

    private void ChangeKind()
    {
        if (_updating || _kind.SelectedItem is not LightKindItem) return;
        RaiseChanged(SceneLightChangedFields.Kind);
        UpdateApplicableControls(CurrentState().Kind);
    }

    private void ChangeToggle(SceneLightChangedFields field)
    {
        if (_updating) return;
        RaiseChanged(field);
        UpdateApplicableControls(CurrentState().Kind);
    }

    private void ChooseColor()
    {
        if (!CanEditParameters) return;
        using var picker = new ProfessionalColorPickerDialog(_color.SwatchColor, UiLocalization.T("Light color"));
        var startColor = _color.SwatchColor;
        var changed = false;
        BeginInteraction();
        picker.ColorChanged += (_, _) =>
        {
            _color.SwatchColor = Opaque(picker.Color);
            _color.DetailText = Hex(_color.SwatchColor);
            changed = true;
            RaiseChanged(SceneLightChangedFields.Color);
        };
        if (picker.ShowDialog(FindForm()) == DialogResult.OK)
        {
            if (!changed && picker.Color.ToArgb() != startColor.ToArgb())
            {
                _color.SwatchColor = Opaque(picker.Color);
                _color.DetailText = Hex(_color.SwatchColor);
                RaiseChanged(SceneLightChangedFields.Color);
            }
            CompleteInteraction();
            return;
        }
        _color.SwatchColor = startColor;
        _color.DetailText = Hex(startColor);
        CancelInteraction();
    }

    private void HandleNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            CommitName();
            Parent?.Focus();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Escape)
        {
            _name.Text = _nameEditStart;
            Parent?.Focus();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private void CommitName()
    {
        if (_updating || !CanEdit) return;
        var normalized = string.IsNullOrWhiteSpace(_name.Text) ? _nameEditStart : _name.Text.Trim();
        if (!string.Equals(_name.Text, normalized, StringComparison.Ordinal)) _name.Text = normalized;
        if (string.Equals(normalized, _nameEditStart, StringComparison.Ordinal)) return;
        _nameEditStart = normalized;
        RaiseChanged(SceneLightChangedFields.Name);
    }

    private void RaiseChanged(SceneLightChangedFields fields)
    {
        if (_updating
            || !CanEdit
            || fields != SceneLightChangedFields.Name && !CanEditParameters)
        {
            return;
        }
        var state = CurrentState();
        var index = _states.FindIndex(item => string.Equals(item.Id, state.Id, StringComparison.Ordinal));
        if (index >= 0) _states[index] = state;
        RefreshListItems();
        LightChanged?.Invoke(this, new SceneLightChangedEventArgs(state, fields));
    }

    private SceneLightEditorState CurrentState()
    {
        var existing = SelectedState();
        if (existing is null) return default;
        var kind = _kind.SelectedItem is LightKindItem item ? item.Kind : existing.Value.Kind;
        return existing.Value with
        {
            Name = _name.Text.Trim(),
            Kind = kind,
            Enabled = _enabled.Checked,
            Color = Opaque(_color.SwatchColor),
            Intensity = (float)_intensity.Value / 100f,
            Range = (float)_range.Value,
            Position = new Vector3((float)_position[0].Value, (float)_position[1].Value, (float)_position[2].Value),
            Rotation = new Vector3((float)_rotation[0].Value, (float)_rotation[1].Value, (float)_rotation[2].Value),
            AreaWidth = (float)_areaWidth.Value,
            AreaHeight = (float)_areaHeight.Value,
            CastShadows = _castShadows.Checked,
            ShadowStrength = (float)_shadowStrength.Value / 100f,
            ShadowSoftness = (float)_shadowSoftness.Value / 100f
        };
    }

    private void LoadSelectedState()
    {
        var state = SelectedState();
        var enabled = state is not null;
        var parametersEditable = state is { } selectedState && selectedState.ParametersEditable;
        if (state is { } value)
        {
            _name.Text = value.Name;
            _nameEditStart = value.Name;
            SelectKind(value.Kind);
            _enabled.Checked = value.Enabled;
            _color.SwatchColor = Opaque(value.Color);
            _color.DetailText = Hex(value.Color);
            _intensity.Value = Bounded(value.Intensity * 100f, _intensity);
            _range.Value = Bounded(value.Range, _range);
            SetVector(_position, value.Position);
            SetVector(_rotation, value.Rotation);
            _areaWidth.Value = Bounded(value.AreaWidth, _areaWidth);
            _areaHeight.Value = Bounded(value.AreaHeight, _areaHeight);
            _castShadows.Checked = value.CastShadows;
            _shadowStrength.Value = Bounded(value.ShadowStrength * 100f, _shadowStrength);
            _shadowSoftness.Value = Bounded(value.ShadowSoftness * 100f, _shadowSoftness);
            UpdateApplicableControls(value.Kind);
        }
        else
        {
            _name.Text = string.Empty;
            _nameEditStart = string.Empty;
            _kind.SelectedIndex = -1;
            _enabled.Checked = false;
            _color.SwatchColor = Color.White;
            _color.DetailText = "#FFFFFF";
            UpdateApplicableControls(SceneLightKind.Ambient);
        }

        _lights.Enabled = _states.Count > 0;
        _name.Enabled = enabled;
        // Light kinds are immutable so changing type never replaces a stable light ID.
        _kind.Enabled = false;
        _enabled.Enabled = parametersEditable;
        _color.Enabled = parametersEditable;
        _intensity.Enabled = parametersEditable;
        _duplicate.Enabled = enabled;
        _remove.Enabled = enabled;
        _reset.Enabled = parametersEditable;
    }

    private void UpdateApplicableControls(SceneLightKind kind)
    {
        var editable = CanEditParameters;
        var positioned = kind is SceneLightKind.Directional or SceneLightKind.Point or SceneLightKind.Area;
        var directed = kind is SceneLightKind.Directional or SceneLightKind.Area;
        var ranged = kind is SceneLightKind.Point or SceneLightKind.Area;
        var area = kind == SceneLightKind.Area;
        var shadows = kind != SceneLightKind.Ambient && _castShadows.Checked;
        foreach (var input in _position) input.Enabled = editable && positioned;
        foreach (var input in _rotation) input.Enabled = editable && directed;
        _range.Enabled = editable && ranged;
        _areaWidth.Enabled = editable && area;
        _areaHeight.Enabled = editable && area;
        _castShadows.Enabled = editable && kind != SceneLightKind.Ambient;
        _shadowStrength.Enabled = editable && shadows;
        _shadowSoftness.Enabled = editable && shadows;
    }

    private void RefreshListItems(bool refreshText = false)
    {
        var previousUpdating = _updating;
        _updating = true;
        var topIndex = _lights.TopIndex;
        var topId = topIndex >= 0 && topIndex < _lights.Items.Count
            ? (_lights.Items[topIndex] as LightListItem)?.State.Id : null;
        var preserveScroll = (_lights.SelectedItem as LightListItem)?.State.Id == _selectedLightId;
        try
        {
            _lights.BeginUpdate();
            for (var index = 0; index < _states.Count; index++)
            {
                var state = _states[index];
                if (index >= _lights.Items.Count)
                {
                    _lights.Items.Add(new LightListItem(state));
                }
                else if (_lights.Items[index] is LightListItem item && item.State.Id == state.Id)
                {
                    var presentationChanged = item.State.Name != state.Name
                        || item.State.Kind != state.Kind || item.State.Enabled != state.Enabled;
                    item.State = state;
                    // Update native list text only when its visible/accessibility text changes.
                    if (refreshText || presentationChanged) _lights.Items[index] = item;
                }
                else _lights.Items[index] = new LightListItem(state);
            }
            while (_lights.Items.Count > _states.Count) _lights.Items.RemoveAt(_lights.Items.Count - 1);
            var selectedIndex = _states.FindIndex(item => string.Equals(item.Id, _selectedLightId, StringComparison.Ordinal));
            if (_lights.SelectedIndex != selectedIndex) _lights.SelectedIndex = selectedIndex;
            var preservedTop = _states.FindIndex(item => string.Equals(item.Id, topId, StringComparison.Ordinal));
            if (preserveScroll && _lights.Items.Count > 0)
            {
                var nextTop = preservedTop >= 0 ? preservedTop : Math.Min(topIndex, _lights.Items.Count - 1);
                if (nextTop >= 0 && _lights.TopIndex != nextTop) _lights.TopIndex = nextTop;
            }
        }
        finally
        {
            _lights.EndUpdate();
            _updating = previousUpdating;
        }
    }

    private void RefreshKindItems()
    {
        var selected = _kind.SelectedItem is LightKindItem item ? item.Kind : SelectedState()?.Kind;
        _updating = true;
        try
        {
            _kind.Items.Clear();
            foreach (var kind in new[] { SceneLightKind.Directional, SceneLightKind.Point, SceneLightKind.Area, SceneLightKind.Ambient })
            {
                _kind.Items.Add(new LightKindItem(kind));
            }
            if (selected is { } value) SelectKind(value);
        }
        finally
        {
            _updating = false;
        }
    }

    private void RefreshAddMenuText()
    {
        foreach (ToolStripItem item in _addMenu.Items)
        {
            if (item.Tag is not SceneLightKind kind) continue;
            var label = LightKindLabel(kind);
            item.Text = label;
            item.AccessibleName = label;
        }
    }

    private void DrawLightListItem(object? sender, DrawItemEventArgs e)
    {
        if (sender is not ListBox list
            || e.Index < 0
            || e.Index >= list.Items.Count
            || list.Items[e.Index] is not LightListItem item)
        {
            return;
        }

        var selected = (e.State & DrawItemState.Selected) != 0;
        var background = selected
            ? Theme.AccentSurface
            : e.Index == _hoveredLightIndex ? Theme.PanelHover : Theme.Panel;
        using (var backgroundBrush = new SolidBrush(background))
            e.Graphics.FillRectangle(backgroundBrush, e.Bounds);
        if (selected)
        {
            using var accent = new SolidBrush(Theme.Accent);
            e.Graphics.FillRectangle(accent, e.Bounds.Left, e.Bounds.Top, 3, e.Bounds.Height);
        }

        var iconSize = Math.Min(18, Math.Max(1, e.Bounds.Height - 8));
        var iconBounds = new Rectangle(
            e.Bounds.Left + 8,
            e.Bounds.Top + (e.Bounds.Height - iconSize) / 2,
            iconSize,
            iconSize);
        SvgIcons.Draw(
            e.Graphics,
            LightIconKind(item.State.Kind),
            iconBounds,
            Theme.ReadableUiColor(
                background,
                list.Enabled ? Theme.Muted : Theme.DisabledText));

        var textLeft = iconBounds.Right + 7;
        var textBounds = new Rectangle(
            textLeft,
            e.Bounds.Top,
            Math.Max(0, e.Bounds.Right - textLeft - 5),
            e.Bounds.Height);
        TextRenderer.DrawText(
            e.Graphics,
            list.GetItemText(item),
            list.Font,
            textBounds,
            list.Enabled
                ? Theme.ReadableText(background, Theme.Text)
                : Theme.ReadableText(background, Theme.DisabledText),
            TextFormatFlags.Left
                | TextFormatFlags.VerticalCenter
                | TextFormatFlags.EndEllipsis
                | TextFormatFlags.NoPrefix);
        if ((e.State & DrawItemState.Focus) != 0 && list.Focused)
        {
            ControlPaint.DrawFocusRectangle(
                e.Graphics,
                Rectangle.Inflate(e.Bounds, -2, -2),
                SystemInformation.HighContrast
                    ? SystemColors.HighlightText
                    : Theme.ReadableUiColor(background, Theme.AccentLabel),
                background);
        }
    }

    private void SetHoveredLightIndex(int index)
    {
        if (index < 0 || index >= _lights.Items.Count) index = -1;
        if (_hoveredLightIndex == index) return;
        var previous = _hoveredLightIndex;
        _hoveredLightIndex = index;
        if (previous >= 0) _lights.Invalidate(_lights.GetItemRectangle(previous));
        if (_hoveredLightIndex >= 0) _lights.Invalidate(_lights.GetItemRectangle(_hoveredLightIndex));
    }

    private void SelectKind(SceneLightKind kind)
    {
        for (var index = 0; index < _kind.Items.Count; index++)
        {
            if (_kind.Items[index] is LightKindItem item && item.Kind == kind)
            {
                _kind.SelectedIndex = index;
                return;
            }
        }
        _kind.SelectedIndex = -1;
    }

    private SceneLightEditorState? SelectedState()
    {
        var index = _states.FindIndex(item => string.Equals(item.Id, _selectedLightId, StringComparison.Ordinal));
        return index >= 0 ? _states[index] : null;
    }

    private bool CanEdit => SelectedState() is not null;
    private bool CanEditParameters => SelectedState() is { } state && state.ParametersEditable;

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

    private void RaiseCommand(EventHandler<SceneLightCommandEventArgs>? handler)
    {
        if (!CanEdit) return;
        handler?.Invoke(this, new SceneLightCommandEventArgs(_selectedLightId));
    }

    private static void ConfigureHeaderButton(SvgIconButton button, string accessibleName)
    {
        button.Dock = DockStyle.Fill;
        button.Margin = new Padding(2, 1, 0, 1);
        button.AccessibleName = accessibleName;
        button.AccessibleDescription = accessibleName;
        Theme.StyleToolbarButton(button);
    }

    private static Control FieldRow(string label, Control value)
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.Controls.Add(FieldLabel(label), 0, 0);
        row.Controls.Add(value, 1, 0);
        return row;
    }

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

    private static ModernNumericUpDown PercentInput(string accessibleName, decimal maximum = 100m) => new()
    {
        Minimum = 0,
        Maximum = maximum,
        Increment = 1,
        DecimalPlaces = 1,
        WheelAdjustsHoveredDigit = true,
        Suffix = "%",
        AccessibleName = accessibleName,
        AccessibleDescription = accessibleName
    };

    private static ModernNumericUpDown SpatialInput(string accessibleName, decimal minimum = -SpatialLimit) => new()
    {
        Minimum = minimum,
        Maximum = SpatialLimit,
        Increment = 10,
        DecimalPlaces = 2,
        WheelAdjustsHoveredDigit = true,
        Suffix = "vu",
        AccessibleName = accessibleName,
        AccessibleDescription = accessibleName
    };

    private static ModernNumericUpDown RotationInput(string accessibleName) => new()
    {
        Minimum = -360,
        Maximum = 360,
        Increment = 1,
        DecimalPlaces = 2,
        WheelAdjustsHoveredDigit = true,
        Suffix = "deg",
        AccessibleName = accessibleName,
        AccessibleDescription = accessibleName
    };

    private static void SetVector(IReadOnlyList<ModernNumericUpDown> inputs, Vector3 value)
    {
        inputs[0].Value = Bounded(value.X, inputs[0]);
        inputs[1].Value = Bounded(value.Y, inputs[1]);
        inputs[2].Value = Bounded(value.Z, inputs[2]);
    }

    private static decimal Bounded(float value, ModernNumericUpDown input)
    {
        if (!float.IsFinite(value)) return Math.Clamp(0m, input.Minimum, input.Maximum);
        return Math.Clamp((decimal)value, input.Minimum, input.Maximum);
    }

    private static Color Opaque(Color color) => Color.FromArgb(255, color.R, color.G, color.B);
    private static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static string LightKindLabel(SceneLightKind kind) => UiLocalization.T(kind switch
    {
        SceneLightKind.Directional => "Directional Light",
        SceneLightKind.Point => "Point Light",
        SceneLightKind.Area => "Area Light",
        _ => "Ambient Light"
    });

    private static SvgIconKind LightIconKind(SceneLightKind kind) => kind switch
    {
        SceneLightKind.Directional => SvgIconKind.DirectionalLight,
        SceneLightKind.Point => SvgIconKind.PointLight,
        SceneLightKind.Area => SvgIconKind.AreaLight,
        _ => SvgIconKind.AmbientLight
    };

    private sealed record LightKindItem(SceneLightKind Kind)
    {
        public override string ToString() => LightKindLabel(Kind);
    }

    private sealed class LightListItem(SceneLightEditorState state)
    {
        public SceneLightEditorState State { get; set; } = state;

        public override string ToString() =>
            $"[{UiLocalization.T(State.Enabled ? "On" : "Off")}] {State.Name} - {LightKindLabel(State.Kind)}";
    }
}
