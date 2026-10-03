namespace VectorAnimationEngine;

internal sealed class LayerBlendModeChangedEventArgs(LayerBlendMode blendMode) : EventArgs
{
    public LayerBlendMode BlendMode { get; } = blendMode;
}

internal sealed class LayerBlendModePanel : Panel
{
    private const int PanelPaddingTop = 6;
    private const int PanelPaddingBottom = 8;
    private const int PanelPaddingHorizontal = 12;
    private const int PanelHeight = PanelPaddingTop
        + Theme.InspectorTitleHeight
        + Theme.InspectorContentPaddingTop
        + Theme.InspectorRowHeight
        + PanelPaddingBottom;

    private sealed record BlendModeItem(LayerBlendMode Mode, string Label)
    {
        public override string ToString() => UiLocalization.T(Label);
    }

    private static readonly (LayerBlendMode Mode, string Label)[] BlendModes =
    [
        (LayerBlendMode.Normal, "Normal"),
        (LayerBlendMode.Multiply, "Multiply"),
        (LayerBlendMode.Screen, "Screen"),
        (LayerBlendMode.Darken, "Darken"),
        (LayerBlendMode.Lighten, "Lighten"),
        (LayerBlendMode.Dissolve, "Dissolve"),
        (LayerBlendMode.ColorBurn, "Color Burn"),
        (LayerBlendMode.LinearBurn, "Linear Burn"),
        (LayerBlendMode.DarkerColor, "Darker Color"),
        (LayerBlendMode.ColorDodge, "Color Dodge"),
        (LayerBlendMode.LinearDodge, "Linear Dodge"),
        (LayerBlendMode.LighterColor, "Lighter Color"),
        (LayerBlendMode.Overlay, "Overlay"),
        (LayerBlendMode.SoftLight, "Soft Light"),
        (LayerBlendMode.HardLight, "Hard Light"),
        (LayerBlendMode.VividLight, "Vivid Light"),
        (LayerBlendMode.LinearLight, "Linear Light"),
        (LayerBlendMode.PinLight, "Pin Light"),
        (LayerBlendMode.HardMix, "Hard Mix"),
        (LayerBlendMode.Difference, "Difference"),
        (LayerBlendMode.Exclusion, "Exclusion"),
        (LayerBlendMode.Hue, "Hue"),
        (LayerBlendMode.Saturation, "Saturation"),
        (LayerBlendMode.Color, "Color"),
        (LayerBlendMode.Luminosity, "Luminosity"),
        (LayerBlendMode.Subtract, "Subtract"),
        (LayerBlendMode.Divide, "Divide")
    ];

    private readonly Label _layerName = new()
    {
        ForeColor = Theme.Muted,
        BackColor = Color.Transparent,
        AutoEllipsis = true,
        TextAlign = ContentAlignment.MiddleRight
    };
    private readonly ComboBox _blendMode = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        AccessibleName = "Layer blend mode",
        AccessibleDescription = "Controls how the selected layer composites with layers below"
    };
    private bool _updating;

    public LayerBlendModePanel()
    {
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Font = Theme.UiFont();
        Height = PanelHeight;
        MinimumSize = new Size(248, PanelHeight);
        Padding = new Padding(PanelPaddingHorizontal, PanelPaddingTop, PanelPaddingHorizontal, PanelPaddingBottom);
        AccessibleRole = AccessibleRole.Pane;
        AccessibleName = "Layer";

        var title = new Label
        {
            Text = "Layer",
            Dock = DockStyle.Top,
            Height = Theme.InspectorTitleHeight,
            ForeColor = Theme.Text,
            BackColor = Color.Transparent,
            Font = Theme.UiFont(10, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = false
        };
        Controls.Add(title);

        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Theme.Panel,
            Margin = Padding.Empty,
            Padding = new Padding(0, Theme.InspectorContentPaddingTop, 0, 0)
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        row.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.InspectorRowHeight));
        Controls.Add(row);
        row.BringToFront();

        var label = new Label
        {
            Text = "Blend Mode",
            Dock = DockStyle.Fill,
            ForeColor = Theme.Muted,
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = false
        };
        row.Controls.Add(label, 0, 0);

        foreach (var item in BlendModes) _blendMode.Items.Add(new BlendModeItem(item.Mode, item.Label));
        Theme.StyleComboBox(_blendMode);
        _blendMode.Dock = DockStyle.Fill;
        _blendMode.Margin = new Padding(0, Theme.InspectorRowMarginVertical, 8, Theme.InspectorRowMarginVertical);
        row.Controls.Add(_blendMode, 1, 0);

        _layerName.Dock = DockStyle.Fill;
        _layerName.Margin = Padding.Empty;
        row.Controls.Add(_layerName, 2, 0);

        _blendMode.SelectedIndexChanged += (_, _) =>
        {
            if (_updating || _blendMode.SelectedItem is not BlendModeItem item) return;
            BlendModeChanged?.Invoke(this, new LayerBlendModeChangedEventArgs(item.Mode));
        };
        SizeChanged += (_, _) =>
        {
            var showLayerName = ClientSize.Width >= 300;
            row.ColumnStyles[2].Width = showLayerName ? 104 : 0;
            _layerName.Visible = showLayerName;
        };

        ClearLayer();
    }

    public event EventHandler<LayerBlendModeChangedEventArgs>? BlendModeChanged;

    public int PreferredPanelHeight => PanelHeight;

    public void SetLayer(string layerName, LayerBlendMode blendMode)
    {
        _updating = true;
        try
        {
            _layerName.Text = layerName;
            _blendMode.SelectedIndex = Array.FindIndex(BlendModes, item => item.Mode == blendMode);
            if (_blendMode.SelectedIndex < 0) _blendMode.SelectedIndex = 0;
            _blendMode.Enabled = true;
        }
        finally
        {
            _updating = false;
        }
    }

    public void ClearLayer()
    {
        _updating = true;
        try
        {
            _layerName.Text = "-";
            _blendMode.SelectedIndex = 0;
            _blendMode.Enabled = false;
        }
        finally
        {
            _updating = false;
        }
    }
}
