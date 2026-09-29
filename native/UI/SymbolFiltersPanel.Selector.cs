namespace VectorAnimationEngine;

internal sealed partial class SymbolFiltersPanel
{
    private readonly ComboBox _selector = new() { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Filter effect" };
    private readonly Button _add = new() { Text = "+", AccessibleName = "Add filter effect" };
    private readonly Button _remove = new() { Text = "−", AccessibleName = "Remove filter effect" };
    private readonly Label _effectStatus = TextLabel("Effect not added");
    private readonly ColorTargetButton _startColor = new("Start color");
    private readonly ColorTargetButton _endColor = new("End color");
    private sealed record EffectItem(FilterKind Kind, string Label)
    {
        public override string ToString() => UiLocalization.T(Label);
    }
    private void BuildEffectSelector()
    {
        Theme.StyleComboBox(_selector);
        Theme.StyleButton(_add); Theme.StyleButton(_remove);
        Controls.AddRange([_selector, _add, _remove, _effectStatus, _startColor, _endColor]);
        string[] names = ["Blur", "Glow", "Drop Shadow", "Bevel", "Gradient Bevel", "Gradient Glow"];
        for (int i = 0; i < names.Length; i++) _selector.Items.Add(new EffectItem((FilterKind)i, names[i]));
        foreach (var kind in new[] { FilterKind.Bevel, FilterKind.GradientBevel, FilterKind.GradientGlow })
        {
            AddField(kind, 0, 0, "Blur X", names[(int)kind] + " Blur X", 128, "px", f => ReadEdge(f, kind).BlurX,
                (f,v) => PatchEdge(f, kind, e => e with { BlurX = v }));
            AddField(kind, 0, 1, "Blur Y", names[(int)kind] + " Blur Y", 128, "px", f => ReadEdge(f, kind).BlurY,
                (f,v) => PatchEdge(f, kind, e => e with { BlurY = v }));
            AddField(kind, 48, 0, "Intensity", names[(int)kind] + " intensity", 10, "×", f => ReadEdge(f, kind).Strength,
                (f,v) => PatchEdge(f, kind, e => e with { Strength = v }), increment: 0.1m);
            AddField(kind, 48, 1, "Opacity", names[(int)kind] + " opacity", 100, "%", f => ReadEdge(f, kind).Opacity * 100,
                (f,v) => PatchEdge(f, kind, e => e with { Opacity = v / 100 }));
            if (kind == FilterKind.GradientGlow) continue;
            AddField(kind, 96, 0, "Angle", names[(int)kind] + " angle", 360, "°", f => ReadEdge(f, kind).Angle,
                (f,v) => PatchEdge(f, kind, e => e with { Angle = v }), minimum: -360);
            AddField(kind, 96, 1, "Distance", names[(int)kind] + " distance", 256, "px", f => ReadEdge(f, kind).Distance,
                (f,v) => PatchEdge(f, kind, e => e with { Distance = v }));
        }
        _selector.SelectedIndexChanged += (_, _) => { CompleteInteraction(); RefreshFields(); LayoutFields(); };
        _selector.SelectedIndex = 0;
        _add.Click += (_, _) => ToggleFilter((FilterKind)_selector.SelectedIndex, true);
        _remove.Click += (_, _) => ToggleFilter((FilterKind)_selector.SelectedIndex, false);
        _startColor.Click += (_, _) => ChooseEdgeColor(false);
        _endColor.Click += (_, _) => ChooseEdgeColor(true);
        _toolTip.SetToolTip(_add, "Add filter effect");
        _toolTip.SetToolTip(_remove, "Remove filter effect");
    }
    private void RefreshEffectSelector()
    {
        var kind = (FilterKind)Math.Max(0, _selector.SelectedIndex);
        _add.Enabled = _mixed || !IsFilterEnabled(kind);
        _remove.Enabled = _mixed || IsFilterEnabled(kind);
        _effectStatus.Text = UiLocalization.T(_mixed ? "Mixed values. Edit one parameter at a time." : IsFilterEnabled(kind) ? "Effect enabled" : "Effect not added");
        var edge = ReadEdge(_filters, kind);
        UpdateColor(_startColor, edge.StartColorArgb, edge.Enabled);
        UpdateColor(_endColor, edge.EndColorArgb, edge.Enabled);
    }
    private static SymbolEdgeFilter ReadEdge(SymbolFilters f, FilterKind kind) => kind switch
    {
        FilterKind.Bevel => f.Bevel, FilterKind.GradientBevel => f.GradientBevel, _ => f.GradientGlow
    };
    private static SymbolFilters PatchEdge(SymbolFilters f, FilterKind kind, Func<SymbolEdgeFilter, SymbolEdgeFilter> update) => kind switch
    {
        FilterKind.Bevel => f with { Bevel = update(f.Bevel) },
        FilterKind.GradientBevel => f with { GradientBevel = update(f.GradientBevel) },
        _ => f with { GradientGlow = update(f.GradientGlow) }
    };
    private void ChooseEdgeColor(bool end)
    {
        if (_updating || _disposing || !Enabled) return;
        CompleteInteraction();
        var kind = (FilterKind)_selector.SelectedIndex;
        var effect = ReadEdge(_filters, kind);
        using var picker = new ProfessionalColorPickerDialog(Color.FromArgb(end ? effect.EndColorArgb : effect.StartColorArgb), end ? "End color" : "Start color");
        _activePicker = picker;
        BeginInteraction();
        void Preview()
        {
            int argb = Color.FromArgb(255, picker.Color.R, picker.Color.G, picker.Color.B).ToArgb();
            ApplyPatch(f => PatchEdge(f, kind, e => end ? e with { EndColorArgb = argb } : e with { StartColorArgb = argb }));
        }
        picker.ColorChanged += (_, _) => { if (_interactionActive && !_disposing) Preview(); };
        try
        {
            if (picker.ShowDialog(FindForm()) == DialogResult.OK && _interactionActive && !_disposing)
            { Preview(); CompleteInteraction(); }
            else CancelInteraction();
        }
        finally { _activePicker = null; CancelInteraction(); }
    }
}
