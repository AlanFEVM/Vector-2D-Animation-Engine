namespace VectorAnimationEngine;

internal sealed partial class SymbolFiltersPanel
{
    private readonly ListBox _effectOrder = new() { IntegralHeight = false, BorderStyle = BorderStyle.None, AccessibleName = "Filter order" };
    private readonly Button _moveEffectUp = new() { Text = "Move up", AccessibleName = "Move filter up" };
    private readonly Button _moveEffectDown = new() { Text = "Move down", AccessibleName = "Move filter down" };
    private readonly GradientStopStrip _gradientStrip = new() { AccessibleName = "Filter gradient stops" };
    private readonly ComboBox _stopSelector = new() { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Gradient stop" };
    private readonly ModernNumericUpDown _stopOffset = new() { Minimum = 0, Maximum = 100, DecimalPlaces = 2, Increment = 0.1m, Suffix = "%", ShowSuffix = true, AccessibleName = "Stop position" };
    private readonly ModernNumericUpDown _stopAlpha = new() { Minimum = 0, Maximum = 100, DecimalPlaces = 2, Increment = 0.1m, Suffix = "%", ShowSuffix = true, AccessibleName = "Stop alpha" };
    private readonly Label _stopPositionLabel = TextLabel("Stop position");
    private readonly Label _stopAlphaLabel = TextLabel("Stop alpha");
    private readonly ColorTargetButton _stopColor = new("Stop color");
    private readonly Button _addStop = new() { Text = "Add stop" };
    private readonly Button _removeStop = new() { Text = "Remove stop" };
    private int _selectedStop;
    private sealed record OrderItem(SymbolFilterKind Kind, string Name, bool Enabled)
    {
        public override string ToString() => UiLocalization.T(Name) + (Enabled ? "" : " · " + UiLocalization.T("Off"));
    }

    private void BuildOrderAndGradientEditors()
    {
        Controls.AddRange([_effectOrder, _moveEffectUp, _moveEffectDown, _gradientStrip, _stopSelector,
            _stopOffset, _stopAlpha, _stopPositionLabel, _stopAlphaLabel, _stopColor, _addStop, _removeStop]);
        _effectOrder.BackColor = Theme.PanelStrong;
        _effectOrder.ForeColor = Theme.Text;
        _effectOrder.Font = Theme.UiFont();
        foreach (var button in new[] { _moveEffectUp, _moveEffectDown, _addStop, _removeStop }) Theme.StyleButton(button);
        Theme.StyleComboBox(_stopSelector);
        Theme.StyleNumeric(_stopOffset);
        Theme.StyleNumeric(_stopAlpha);
        _effectOrder.SelectedIndexChanged += (_, _) =>
        {
            if (_updating || _effectOrder.SelectedItem is not OrderItem item) return;
            _selector.SelectedIndex = (int)item.Kind;
            RefreshOrderButtons();
        };
        _moveEffectUp.Click += (_, _) => MoveSelectedEffect(-1);
        _moveEffectDown.Click += (_, _) => MoveSelectedEffect(1);
        _stopSelector.SelectedIndexChanged += (_, _) =>
        {
            if (_updating || _stopSelector.SelectedIndex < 0) return;
            CompleteInteraction();
            _selectedStop = _stopSelector.SelectedIndex;
            RefreshFields();
        };
        _gradientStrip.StopSelected += (_, e) => { if (!_updating) { _selectedStop = e.Index; RefreshFields(); } };
        _gradientStrip.InteractionStarted += (_, _) => BeginInteraction();
        _gradientStrip.InteractionCompleted += (_, _) => CompleteInteraction();
        _gradientStrip.InteractionCanceled += (_, _) => CancelInteraction();
        _gradientStrip.StopsChanged += (_, e) =>
        {
            if (_updating) return;
            if (e.Stops.Length > SymbolGradientStops.MaximumCount)
            {
                CancelInteraction();
                _toolTip.Show(UiLocalization.T("A filter gradient supports up to 16 stops."), _gradientStrip, 0, _gradientStrip.Height, 2500);
                return;
            }
            _selectedStop = e.SelectedIndex;
            var stops = new SymbolGradientStops(e.Stops.Select(stop => new SymbolGradientStop(stop.Position, stop.Argb)).ToArray());
            var kind = (FilterKind)_selector.SelectedIndex;
            ApplyPatch(f => PatchEdge(f, kind, edge => edge with { GradientStops = stops }));
        };
        foreach (var input in new[] { _stopOffset, _stopAlpha })
        {
            input.InteractionStarted += (_, _) => BeginInteraction();
            input.InteractionCompleted += (_, _) => CompleteInteraction();
            input.InteractionCanceled += (_, _) => CancelInteraction();
            input.ValueChanged += (_, _) =>
            {
                if (_updating) return;
                var index = _selectedStop;
                var value = (float)input.Value / 100;
                var offset = input == _stopOffset;
                EditStop(index, stop => offset ? stop with { Offset = value }
                    : stop with { ColorArgb = (stop.ColorArgb & 0x00ffffff) | ((int)Math.Round(value * 255) << 24) });
            };
        }
        _stopColor.Click += (_, _) => ChooseStopColor();
        _addStop.Click += (_, _) => AddGradientStop();
        _removeStop.Click += (_, _) => RemoveGradientStop();
        _toolTip.SetToolTip(_gradientStrip, UiLocalization.T("Click the gradient to add a stop; drag a stop to move it. Up to 16 stops. Delete removes an interior stop."));
    }

    private static SymbolGradientStop[] EdgeStops(SymbolEdgeFilter edge) => edge.GradientStops?.Items
        ?? [new(0, edge.StartColorArgb), new(1, edge.EndColorArgb)];

    private void RefreshOrderAndGradientEditors()
    {
        if (_selector.SelectedIndex < 0) return;
        var kind = (FilterKind)_selector.SelectedIndex;
        _effectOrder.BeginUpdate();
        try
        {
            _effectOrder.Items.Clear();
            foreach (var item in (_filters.Order ?? SymbolFilterOrder.Default).Items)
            {
                var name = item switch { SymbolFilterKind.Shadow => "Drop Shadow", SymbolFilterKind.GradientBevel => "Gradient Bevel",
                    SymbolFilterKind.GradientGlow => "Gradient Glow", _ => item.ToString() };
                _effectOrder.Items.Add(new OrderItem(item, name, IsFilterEnabled((FilterKind)item)));
            }
            _effectOrder.SelectedIndex = Array.IndexOf((_filters.Order ?? SymbolFilterOrder.Default).Items, (SymbolFilterKind)kind);
        }
        finally { _effectOrder.EndUpdate(); }
        RefreshOrderButtons();
        var edge = ReadEdge(_filters, kind);
        var stops = EdgeStops(edge);
        _selectedStop = Math.Clamp(_selectedStop, 0, stops.Length - 1);
        _gradientStrip.SetStops(stops.Select(stop => new GradientStop(stop.Offset, stop.ColorArgb)).ToArray(), _selectedStop);
        _stopSelector.Items.Clear();
        for (int i = 0; i < stops.Length; i++) _stopSelector.Items.Add($"{UiLocalization.T("Gradient stop")} {i + 1} · {stops[i].Offset * 100:0.##}%");
        _stopSelector.SelectedIndex = _selectedStop;
        var current = stops[_selectedStop];
        _stopOffset.Value = (decimal)current.Offset * 100;
        _stopAlpha.Value = ((uint)current.ColorArgb >> 24) * 100m / 255;
        _stopOffset.MixedValue = _stopAlpha.MixedValue = _mixed;
        var editable = edge.Enabled && kind is FilterKind.GradientBevel or FilterKind.GradientGlow;
        _gradientStrip.Enabled = _stopSelector.Enabled = _stopColor.Enabled = _stopAlpha.Enabled = editable;
        _stopOffset.Enabled = editable && _selectedStop > 0 && _selectedStop < stops.Length - 1;
        _addStop.Enabled = editable && stops.Length < SymbolGradientStops.MaximumCount;
        _removeStop.Enabled = editable && stops.Length > 2 && _selectedStop > 0 && _selectedStop < stops.Length - 1;
        UpdateColor(_stopColor, current.ColorArgb, editable);
        _stopColor.DetailText = $"#{(uint)current.ColorArgb:X8}";
    }

    private void RefreshOrderButtons()
    {
        _moveEffectUp.Enabled = _effectOrder.SelectedIndex > 0;
        _moveEffectDown.Enabled = _effectOrder.SelectedIndex >= 0 && _effectOrder.SelectedIndex < 5;
    }

    private void MoveSelectedEffect(int direction)
    {
        if (_updating || _effectOrder.SelectedItem is not OrderItem item) return;
        CompleteInteraction();
        ApplyPatch(f =>
        {
            var items = (f.Order ?? SymbolFilterOrder.Default).Items;
            var index = Array.IndexOf(items, item.Kind);
            var next = index + direction;
            if (next < 0 || next >= items.Length) return f;
            (items[index], items[next]) = (items[next], items[index]);
            return f with { Order = new SymbolFilterOrder(items) };
        });
    }

    private void EditStop(int index, Func<SymbolGradientStop, SymbolGradientStop> edit)
    {
        var kind = (FilterKind)_selector.SelectedIndex;
        ApplyPatch(f => PatchEdge(f, kind, edge =>
        {
            var items = EdgeStops(edge);
            if (index >= items.Length) return edge;
            var stop = edit(items[index]);
            // Endpoints stay fixed; keep the selected stop between its neighbours.
            stop = stop with { Offset = index == 0 ? 0 : index == items.Length - 1 ? 1
                : Math.Clamp(stop.Offset, MathF.BitIncrement(items[index - 1].Offset), MathF.BitDecrement(items[index + 1].Offset)) };
            items[index] = stop;
            return edge with { GradientStops = new SymbolGradientStops(items) };
        }));
    }

    private void AddGradientStop()
    {
        var kind = (FilterKind)_selector.SelectedIndex;
        var items = EdgeStops(ReadEdge(_filters, kind)).ToList();
        if (items.Count >= SymbolGradientStops.MaximumCount) return;
        var interval = Enumerable.Range(0, items.Count - 1).OrderByDescending(i => items[i + 1].Offset - items[i].Offset).First();
        var offset = (items[interval].Offset + items[interval + 1].Offset) / 2;
        var sample = new SymbolGradientStops(items.ToArray()).Sample(offset);
        var argb = System.Drawing.Color.FromArgb((int)Math.Round(sample.W * 255), (int)Math.Round(sample.Z * 255),
            (int)Math.Round(sample.Y * 255), (int)Math.Round(sample.X * 255)).ToArgb();
        items.Insert(interval + 1, new(offset, argb));
        _selectedStop = interval + 1;
        var stops = new SymbolGradientStops(items.ToArray());
        ApplyPatch(f => PatchEdge(f, kind, edge => edge with { GradientStops = stops }));
    }

    private void RemoveGradientStop()
    {
        var kind = (FilterKind)_selector.SelectedIndex;
        var index = _selectedStop;
        var items = EdgeStops(ReadEdge(_filters, kind));
        if (items.Length <= 2 || index <= 0 || index >= items.Length - 1) return;
        _selectedStop--;
        ApplyPatch(f => PatchEdge(f, kind, edge =>
        {
            var stops = EdgeStops(edge);
            return index <= 0 || index >= stops.Length - 1 ? edge
                : edge with { GradientStops = new SymbolGradientStops(stops.Where((_, i) => i != index).ToArray()) };
        }));
    }

    private void ChooseStopColor()
    {
        var kind = (FilterKind)_selector.SelectedIndex;
        var index = _selectedStop;
        var original = EdgeStops(ReadEdge(_filters, kind))[index].ColorArgb;
        CompleteInteraction();
        using var picker = new ProfessionalColorPickerDialog(Color.FromArgb(original), "Stop color");
        _activePicker = picker;
        BeginInteraction();
        picker.ColorChanged += (_, _) =>
        {
            if (!_interactionActive || _disposing) return;
            var rgb = picker.Color.ToArgb() & 0x00ffffff;
            EditStop(index, stop => stop with { ColorArgb = (stop.ColorArgb & unchecked((int)0xff000000)) | rgb });
        };
        var accepted = false;
        try { accepted = picker.ShowDialog(FindForm()) == DialogResult.OK; }
        finally { _activePicker = null; if (accepted) CompleteInteraction(); else CancelInteraction(); }
    }

    private void LayoutOrderEditors(int left, int width)
    {
        _effectOrder.ItemHeight = ScaleLogical(28);
        _effectOrder.SetBounds(left, ScaleLogical(92), width, ScaleLogical(176));
        var half = Math.Max(1, (width - ScaleLogical(8)) / 2);
        _moveEffectUp.SetBounds(left, ScaleLogical(276), half, ScaleLogical(32));
        _moveEffectDown.SetBounds(left + half + ScaleLogical(8), ScaleLogical(276), half, ScaleLogical(32));
    }

    private void LayoutGradientEditors(int left, int width, int top, bool visible)
    {
        foreach (var control in new Control[] { _gradientStrip, _stopSelector, _stopOffset, _stopAlpha,
            _stopPositionLabel, _stopAlphaLabel, _stopColor, _addStop, _removeStop }) control.Visible = visible;
        if (!visible) return;
        _gradientStrip.SetBounds(left, ScaleLogical(top), width, ScaleLogical(56));
        _stopSelector.SetBounds(left, ScaleLogical(top + 64), width, ScaleLogical(32));
        var split = left + (int)Math.Floor(width * 0.48f);
        _stopPositionLabel.SetBounds(left, ScaleLogical(top + 104), Math.Max(1, split - left - ScaleLogical(8)), ScaleLogical(32));
        _stopOffset.SetBounds(split, ScaleLogical(top + 104), Math.Max(1, left + width - split), ScaleLogical(32));
        _stopAlphaLabel.SetBounds(left, ScaleLogical(top + 144), Math.Max(1, split - left - ScaleLogical(8)), ScaleLogical(32));
        _stopAlpha.SetBounds(split, ScaleLogical(top + 144), Math.Max(1, left + width - split), ScaleLogical(32));
        _stopColor.SetBounds(left, ScaleLogical(top + 184), width, ScaleLogical(32));
        var half = Math.Max(1, (width - ScaleLogical(8)) / 2);
        _addStop.SetBounds(left, ScaleLogical(top + 224), half, ScaleLogical(32));
        _removeStop.SetBounds(left + half + ScaleLogical(8), ScaleLogical(top + 224), half, ScaleLogical(32));
    }
}

