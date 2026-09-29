namespace VectorAnimationEngine;

internal sealed class SymbolFiltersChangedEventArgs(Func<SymbolFilters, SymbolFilters> update) : EventArgs
{
    // Apply separately to every selected instance to preserve its unrelated parameters.
    public Func<SymbolFilters, SymbolFilters> Update { get; } = update;
}

internal sealed partial class SymbolFiltersPanel : Panel
{
    private const int LogicalPanelHeight = 380;
    private enum FilterKind { Blur, Glow, Shadow, Bevel, GradientBevel, GradientGlow }
    private sealed record NumericField(Label Label, ModernNumericUpDown Input, FilterKind Kind,
        int Row, int Column, Func<SymbolFilters, float> Read,
        Func<SymbolFilters, float, SymbolFilters> Patch);

    private readonly List<NumericField> _fields = [];
    private readonly ModernToggleSwitch _blur = FilterToggle("Blur");
    private readonly ModernToggleSwitch _glow = FilterToggle("Glow");
    private readonly ModernToggleSwitch _shadow = FilterToggle("Drop Shadow");
    private readonly ColorTargetButton _glowColor = new("Glow color");
    private readonly ColorTargetButton _shadowColor = new("Shadow color");
    private readonly Label _title = TextLabel("Symbol Filters", bold: true);
    private readonly Label _order = TextLabel("Order: Blur → Glow → Drop Shadow");
    private readonly Label _selectionHint = TextLabel("Combine effects. Sizes are stage pixels.");
    private readonly ToolTip _toolTip = new();
    private SymbolFilters _filters;
    private SymbolFilters _interactionInitialFilters;
    private bool _mixed;
    private bool _interactionInitialMixed;
    private bool _updating;
    private bool _interactionActive;
    private bool _disposing;
    private ProfessionalColorPickerDialog? _activePicker;

    public SymbolFiltersPanel()
    {
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Font = Theme.UiFont();
        Height = PreferredPanelHeight;
        MinimumSize = new Size(248, PreferredPanelHeight);
        AccessibleName = "Symbol Filters";
        Theme.StyleToolTip(_toolTip);
        Controls.AddRange([_title, _order, _selectionHint, _blur, _glow, _shadow, _glowColor, _shadowColor]);

        AddField(FilterKind.Blur, 114, 0, "Blur X", "Blur horizontal radius", 128, "px",
            f => f.Blur.BlurX, (f, v) => f with { Blur = f.Blur with { BlurX = v } });
        AddField(FilterKind.Blur, 114, 1, "Blur Y", "Blur vertical radius", 128, "px",
            f => f.Blur.BlurY, (f, v) => f with { Blur = f.Blur with { BlurY = v } });
        AddField(FilterKind.Glow, 196, 0, "Blur X", "Glow horizontal radius", 128, "px",
            f => f.Glow.BlurX, (f, v) => f with { Glow = f.Glow with { BlurX = v } });
        AddField(FilterKind.Glow, 196, 1, "Blur Y", "Glow vertical radius", 128, "px",
            f => f.Glow.BlurY, (f, v) => f with { Glow = f.Glow with { BlurY = v } });
        AddField(FilterKind.Glow, 244, 0, "Intensity", "Glow intensity", 10, "×",
            f => f.Glow.Strength, (f, v) => f with { Glow = f.Glow with { Strength = v } }, increment: 0.1m);
        AddField(FilterKind.Glow, 244, 1, "Opacity", "Glow opacity", 100, "%",
            f => f.Glow.Opacity * 100, (f, v) => f with { Glow = f.Glow with { Opacity = v / 100 } });
        AddField(FilterKind.Shadow, 368, 0, "Blur X", "Shadow horizontal radius", 128, "px",
            f => f.Shadow.BlurX, (f, v) => f with { Shadow = f.Shadow with { BlurX = v } });
        AddField(FilterKind.Shadow, 368, 1, "Blur Y", "Shadow vertical radius", 128, "px",
            f => f.Shadow.BlurY, (f, v) => f with { Shadow = f.Shadow with { BlurY = v } });
        AddField(FilterKind.Shadow, 416, 0, "Intensity", "Shadow intensity", 10, "×",
            f => f.Shadow.Strength, (f, v) => f with { Shadow = f.Shadow with { Strength = v } }, increment: 0.1m);
        AddField(FilterKind.Shadow, 416, 1, "Opacity", "Shadow opacity", 100, "%",
            f => f.Shadow.Opacity * 100, (f, v) => f with { Shadow = f.Shadow with { Opacity = v / 100 } });
        AddField(FilterKind.Shadow, 464, 0, "Angle", "Shadow angle", 360, "°",
            f => f.Shadow.Angle, (f, v) => f with { Shadow = f.Shadow with { Angle = v } }, minimum: -360);
        AddField(FilterKind.Shadow, 464, 1, "Distance", "Shadow distance", 256, "px",
            f => f.Shadow.Distance, (f, v) => f with { Shadow = f.Shadow with { Distance = v } });

        BuildEffectSelector();
        _blur.CheckedChanged += (_, _) => ToggleFilter(FilterKind.Blur, _blur.Checked);
        _glow.CheckedChanged += (_, _) => ToggleFilter(FilterKind.Glow, _glow.Checked);
        _shadow.CheckedChanged += (_, _) => ToggleFilter(FilterKind.Shadow, _shadow.Checked);
        _glowColor.Click += (_, _) => ChooseColor(FilterKind.Glow);
        _shadowColor.Click += (_, _) => ChooseColor(FilterKind.Shadow);
        _toolTip.SetToolTip(_order, "Effects run in order: blur, glow, then drop shadow.");
        _toolTip.SetToolTip(_selectionHint, "With multiple symbols selected, only the edited parameter changes.");
        UiLocalization.Watch(this);
        SetFilters(default);
        LayoutFields();
    }

    public event EventHandler<SymbolFiltersChangedEventArgs>? FiltersChanged;
    public event EventHandler? InteractionStarted;
    public event EventHandler? InteractionCompleted;
    public event EventHandler? InteractionCanceled;
    public int PreferredPanelHeight => ScaleLogical(LogicalPanelHeight);

    public void SetFilters(SymbolFilters filters, bool mixed = false)
    {
        _filters = filters;
        _mixed = mixed;
        RefreshFields();
    }

    private void AddField(FilterKind kind, int row, int column, string label, string accessibleName,
        decimal maximum, string suffix, Func<SymbolFilters, float> read,
        Func<SymbolFilters, float, SymbolFilters> patch, decimal minimum = 0, decimal increment = 1)
    {
        var caption = TextLabel(label);
        var input = new ModernNumericUpDown
        {
            Minimum = minimum, Maximum = maximum, DecimalPlaces = 1, Increment = increment,
            Suffix = suffix, AccessibleName = accessibleName, WheelAdjustsHoveredDigit = true
        };
        Theme.StyleNumeric(input);
        var field = new NumericField(caption, input, kind, row, column, read, patch);
        _fields.Add(field);
        Controls.Add(caption);
        Controls.Add(input);
        input.InteractionStarted += (_, _) => BeginInteraction();
        input.InteractionCompleted += (_, _) => CompleteInteraction();
        input.InteractionCanceled += (_, _) => CancelInteraction();
        input.ValueChanged += (_, _) =>
        {
            if (_updating || _disposing) return;
            // Capture now: deferred application must not read a different selection.
            var value = (float)input.Value;
            ApplyPatch(f => field.Patch(f, value));
        };
        _toolTip.SetToolTip(input, accessibleName);
    }

    private void ToggleFilter(FilterKind kind, bool enabled)
    {
        if (_updating || _disposing) return;
        ApplyPatch(f => kind switch
        {
            FilterKind.Blur => f with { Blur = enabled && f.Blur == default
                ? SymbolBlurFilter.Default : f.Blur with { Enabled = enabled } },
            FilterKind.Glow => f with { Glow = enabled && f.Glow == default
                ? SymbolGlowFilter.Default : f.Glow with { Enabled = enabled } },
            FilterKind.Shadow => f with { Shadow = enabled && f.Shadow == default
                ? SymbolShadowFilter.Default : f.Shadow with { Enabled = enabled } },
            _ => PatchEdge(f, kind, e => enabled && e == default ? SymbolEdgeFilter.Default : e with { Enabled = enabled })
        });
    }

    private void ApplyPatch(Func<SymbolFilters, SymbolFilters> update)
    {
        if (_updating || _disposing) return;
        var oneShot = !_interactionActive;
        if (oneShot) BeginInteraction();
        try
        {
            _filters = update(_filters);
            RefreshFields();
            FiltersChanged?.Invoke(this, new SymbolFiltersChangedEventArgs(update));
        }
        catch
        {
            CancelInteraction();
            throw;
        }
        finally
        {
            if (oneShot) CompleteInteraction();
        }
    }

    private void BeginInteraction()
    {
        if (_updating || _disposing || _interactionActive) return;
        _interactionInitialFilters = _filters;
        _interactionInitialMixed = _mixed;
        _interactionActive = true;
        InteractionStarted?.Invoke(this, EventArgs.Empty);
    }

    private void CompleteInteraction()
    {
        if (_updating || !_interactionActive) return;
        _interactionActive = false;
        InteractionCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void CancelInteraction()
    {
        if (!_interactionActive) return;
        _interactionActive = false;
        _filters = _interactionInitialFilters;
        _mixed = _interactionInitialMixed;
        if (!_disposing) RefreshFields();
        InteractionCanceled?.Invoke(this, EventArgs.Empty);
    }

    private void ChooseColor(FilterKind kind)
    {
        if (_updating || _disposing || !Enabled) return;
        CompleteInteraction();
        var argb = kind == FilterKind.Glow ? _filters.Glow.ColorArgb : _filters.Shadow.ColorArgb;
        using var picker = new ProfessionalColorPickerDialog(Color.FromArgb(argb),
            kind == FilterKind.Glow ? "Glow color" : "Shadow color");
        _activePicker = picker;
        BeginInteraction();
        picker.ColorChanged += (_, _) =>
        {
            if (_interactionActive && !_disposing) ApplyColor(kind, picker.Color);
        };
        try
        {
            if (picker.ShowDialog(FindForm()) == DialogResult.OK && _interactionActive && !_disposing)
            {
                // Confirming the displayed color also resolves this parameter for mixed selections.
                ApplyColor(kind, picker.Color);
                CompleteInteraction();
            }
            else CancelInteraction();
        }
        finally
        {
            _activePicker = null;
            CancelInteraction();
        }
    }

    private void ApplyColor(FilterKind kind, Color color)
    {
        var argb = Color.FromArgb(255, color.R, color.G, color.B).ToArgb();
        ApplyPatch(f => kind == FilterKind.Glow
            ? f with { Glow = f.Glow with { ColorArgb = argb } }
            : f with { Shadow = f.Shadow with { ColorArgb = argb } });
    }

    private void RefreshFields()
    {
        if (_disposing) return;
        var updating = _updating;
        _updating = true;
        try
        {
            _blur.Checked = _filters.Blur.Enabled;
            _glow.Checked = _filters.Glow.Enabled;
            _shadow.Checked = _filters.Shadow.Enabled;
            foreach (var field in _fields)
            {
                var value = field.Read(_filters);
                field.Input.Value = float.IsFinite(value)
                    ? (decimal)Math.Clamp(value, (float)field.Input.Minimum, (float)field.Input.Maximum)
                    : field.Input.Minimum;
                var enabled = IsFilterEnabled(field.Kind);
                field.Input.Enabled = enabled;
                field.Label.ForeColor = enabled ? Theme.Muted : Theme.Mix(Theme.Muted, Theme.Panel, 0.55f);
            }
            RefreshEffectSelector();
            UpdateColor(_glowColor, _filters.Glow.ColorArgb, _filters.Glow.Enabled);
            UpdateColor(_shadowColor, _filters.Shadow.ColorArgb, _filters.Shadow.Enabled);
            _selectionHint.Text = _mixed
                ? "Mixed values. Edit one parameter at a time."
                : "Combine effects. Sizes are stage pixels.";
        }
        finally { _updating = updating; }
    }

    private bool IsFilterEnabled(FilterKind kind) => kind switch
    {
        FilterKind.Blur => _filters.Blur.Enabled,
        FilterKind.Glow => _filters.Glow.Enabled,
        FilterKind.Shadow => _filters.Shadow.Enabled,
        _ => ReadEdge(_filters, kind).Enabled
    };

    private static void UpdateColor(ColorTargetButton button, int argb, bool enabled)
    {
        var color = Color.FromArgb(argb);
        button.SwatchColor = Color.FromArgb(255, color.R, color.G, color.B);
        button.DetailText = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        button.Enabled = enabled;
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        LayoutFields();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        MinimumSize = new Size(ScaleLogical(248), PreferredPanelHeight);
        Height = PreferredPanelHeight;
        LayoutFields();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        if (!Enabled) CancelPendingInteraction();
        base.OnEnabledChanged(e);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        if (!Visible) CancelPendingInteraction();
        base.OnVisibleChanged(e);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        CancelPendingInteraction();
        base.OnHandleDestroyed(e);
    }

    private void CancelPendingInteraction()
    {
        CancelInteraction();
        if (_activePicker is { IsDisposed: false } picker) picker.DialogResult = DialogResult.Cancel;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposing)
        {
            _disposing = true;
            CancelPendingInteraction();
            _toolTip.Dispose();
        }
        base.Dispose(disposing);
    }

    private void LayoutFields()
    {
        if (_selector.Items.Count == 0) return;
        var kind = (FilterKind)Math.Max(0, _selector.SelectedIndex);
        int left = ScaleLogical(12), width = Math.Max(0, ClientSize.Width - left * 2);
        _title.SetBounds(left, ScaleLogical(8), width, ScaleLogical(24));
        _order.Visible = false;
        _selectionHint.SetBounds(left, ScaleLogical(34), width, ScaleLogical(28));
        _selector.SetBounds(left, ScaleLogical(68), width - ScaleLogical(76), ScaleLogical(28));
        _add.SetBounds(left + width - ScaleLogical(70), ScaleLogical(68), ScaleLogical(32), ScaleLogical(28));
        _remove.SetBounds(left + width - ScaleLogical(32), ScaleLogical(68), ScaleLogical(32), ScaleLogical(28));
        var toggles = new[] { _blur, _glow, _shadow };
        for (int i = 0; i < toggles.Length; i++)
        {
            toggles[i].Visible = (int)kind == i;
            toggles[i].SetBounds(left, ScaleLogical(102), width, ScaleLogical(28));
        }
        var selected = _fields.Where(f => f.Kind == kind).ToArray();
        int firstRow = selected.Length == 0 ? 0 : selected.Min(f => f.Row);
        int colWidth = (width - ScaleLogical(10)) / 2;
        foreach (var field in _fields)
        {
            bool visible = field.Kind == kind;
            field.Label.Visible = field.Input.Visible = visible;
            int x = left + field.Column * (colWidth + ScaleLogical(10));
            int y = 136 + field.Row - firstRow;
            field.Label.SetBounds(x, ScaleLogical(y), colWidth, ScaleLogical(16));
            field.Input.SetBounds(x, ScaleLogical(y + 17), colWidth, ScaleLogical(26));
        }
        _glowColor.Visible = kind == FilterKind.Glow;
        _shadowColor.Visible = kind == FilterKind.Shadow;
        _glowColor.SetBounds(left, ScaleLogical(240), width, ScaleLogical(38));
        _shadowColor.SetBounds(left, ScaleLogical(288), width, ScaleLogical(38));
        bool edge = (int)kind >= 3;
        _startColor.Visible = _endColor.Visible = edge;
        _effectStatus.Visible = edge;
        _effectStatus.SetBounds(left, ScaleLogical(102), width, ScaleLogical(28));
        _startColor.SetBounds(left, ScaleLogical(288), width, ScaleLogical(38));
        _endColor.SetBounds(left, ScaleLogical(332), width, ScaleLogical(38));
    }

    private int ScaleLogical(int value) => (int)Math.Round(value * DeviceDpi / 96f);
    private static ModernToggleSwitch FilterToggle(string text) => new()
    {
        Text = text, AccessibleName = text, AutoSize = false, TabStop = true
    };
    private static Label TextLabel(string text, bool bold = false) => new()
    {
        Text = text, ForeColor = bold ? Theme.Text : Theme.Muted, BackColor = Color.Transparent,
        Font = Theme.UiFont(bold ? 10 : 9, bold ? FontStyle.Bold : FontStyle.Regular),
        TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true
    };
}
