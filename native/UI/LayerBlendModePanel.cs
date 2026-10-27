namespace VectorAnimationEngine;

internal sealed class LayerBlendModeChangedEventArgs(LayerBlendMode blendMode) : EventArgs
{
    public LayerBlendMode BlendMode { get; } = blendMode;
}

internal sealed class LayerBlendModePanel : Panel
{
    private const int PanelPaddingTop = 6;
    private const int PanelPaddingBottom = 16;
    private const int PanelPaddingHorizontal = 12;
    private const int PanelHeight = PanelPaddingTop
        + Theme.InspectorTitleHeight
        + Theme.InspectorContentPaddingTop + 4
        + Theme.ControlHeight + Theme.InspectorRowMarginVertical * 2
        + PanelPaddingBottom;

    private sealed record BlendModeItem(LayerBlendMode Mode, string Label)
    {
        public override string ToString() => UiLocalization.T(Label);
    }

    internal static readonly (LayerBlendMode Mode, string Label)[] BlendModes =
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
        (LayerBlendMode.Divide, "Divide"),
        (LayerBlendMode.Isolate, "Isolated Group"),
        (LayerBlendMode.Invert, "Invert")
    ];

    private readonly ToolTip _toolTip = new();
    private readonly Panel _layerColorMark = new() { BackColor = Color.Transparent, TabStop = false };
    private Color _layerColor = Color.Empty;
    private readonly Label _layerName = new()
    {
        ForeColor = Theme.Text,
        BackColor = Color.Transparent,
        Font = Theme.UiFont(9f),
        AutoEllipsis = true,
        TextAlign = ContentAlignment.TopLeft,
        AccessibleName = "Current layer"
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
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Font = Theme.UiFont();
        Height = PanelHeight;
        MinimumSize = new Size(248, PanelHeight);
        Padding = new Padding(4, 0, 10, PanelPaddingBottom);
        AccessibleRole = AccessibleRole.Pane;
        AccessibleName = "Layer";
        Theme.StyleToolTip(_toolTip);
        _layerColorMark.Paint += (_, e) =>
        {
            if (_layerColor.IsEmpty) return;
            var scale = _layerColorMark.DeviceDpi / 96f;
            var diameter = Math.Min(8f * scale, _layerColorMark.ClientSize.Width);
            using var brush = new SolidBrush(_layerColor);
            var state = e.Graphics.Save();
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            e.Graphics.FillEllipse(brush, (_layerColorMark.ClientSize.Width - diameter) / 2f,
                3f * scale, diameter, diameter);
            e.Graphics.Restore(state);
        };

        var title = InspectorModuleStyle.CreateTitle("Layer");
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = Theme.InspectorTitleHeight,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty,
            BackColor = Theme.Panel
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 12));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        title.AutoSize = true;
        title.Margin = new Padding(0, 0, 10, 0);
        header.Controls.Add(title, 0, 0);
        _layerColorMark.Dock = DockStyle.Fill;
        _layerColorMark.Margin = Padding.Empty;
        header.Controls.Add(_layerColorMark, 1, 0);
        _layerName.Dock = DockStyle.Fill;
        _layerName.Margin = new Padding(4, 0, 0, 0);
        header.Controls.Add(_layerName, 2, 0);
        Controls.Add(header);

        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = Theme.InspectorContentPaddingTop + 10 + Theme.ControlHeight + Theme.InspectorRowMarginVertical * 2,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.Panel,
            Margin = Padding.Empty,
            Padding = new Padding(4, Theme.InspectorContentPaddingTop + 10, 0, 0)
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
        row.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.ControlHeight + Theme.InspectorRowMarginVertical * 2));
        Controls.Add(row);
        row.BringToFront();

        var label = new Label
        {
            Text = "Blend Mode",
            Dock = DockStyle.Fill,
            ForeColor = Theme.PanelSecondaryText,
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = false
        };
        row.Controls.Add(label, 0, 0);

        foreach (var item in BlendModes) _blendMode.Items.Add(new BlendModeItem(item.Mode, item.Label));
        Theme.StyleComboBox(_blendMode);
        Theme.StyleComboBoxSelection(_blendMode, label.Font.Size, Theme.ControlHeight);
        _blendMode.Dock = DockStyle.Fill;
        _blendMode.Margin = Theme.InspectorFieldMargin(rightGap: false);
        _blendMode.DropDownWidth = 220;
        row.Controls.Add(_blendMode, 1, 0);

        _blendMode.SelectedIndexChanged += (_, _) =>
        {
            if (_updating || _blendMode.SelectedItem is not BlendModeItem item) return;
            BlendModeChanged?.Invoke(this, new LayerBlendModeChangedEventArgs(item.Mode));
        };
        ClearLayer();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _toolTip.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (ShowDivider) InspectorModuleStyle.DrawBottomDivider(this, e.Graphics);
    }

    public event EventHandler<LayerBlendModeChangedEventArgs>? BlendModeChanged;

    internal bool ShowDivider { get; set; } = true;

    public int PreferredPanelHeight => Math.Max(
        (int)Math.Round(PanelHeight * DeviceDpi / 96f),
        _blendMode.Parent is Control row
            ? row.Top + row.Padding.Vertical + _blendMode.Height + _blendMode.Margin.Vertical + Padding.Bottom
            : PanelHeight);

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (_blendMode.Parent is not TableLayoutPanel row) return;
        var rowHeight = _blendMode.Height + _blendMode.Margin.Vertical;
        if (row.RowStyles[0].Height != rowHeight) row.RowStyles[0].Height = rowHeight;
        if (row.Height != rowHeight + row.Padding.Vertical) row.Height = rowHeight + row.Padding.Vertical;
        var requiredHeight = PreferredPanelHeight;
        if (MinimumSize.Height != requiredHeight)
            MinimumSize = new Size(MinimumSize.Width, requiredHeight);
        if (Height < requiredHeight) Height = requiredHeight;
    }

    public void SetLayer(string layerName, LayerBlendMode blendMode, Color layerColor)
    {
        _updating = true;
        try
        {
            if (_layerName.Text != layerName) _layerName.Text = layerName;
            _layerName.AccessibleDescription = layerName;
            if (_toolTip.GetToolTip(_layerName) != layerName) _toolTip.SetToolTip(_layerName, layerName);
            if (_layerColor != layerColor)
            {
                _layerColor = layerColor;
                _layerColorMark.Invalidate();
            }
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
            _layerName.AccessibleDescription = "";
            _toolTip.SetToolTip(_layerName, "");
            if (!_layerColor.IsEmpty)
            {
                _layerColor = Color.Empty;
                _layerColorMark.Invalidate();
            }
            _blendMode.SelectedIndex = 0;
            _blendMode.Enabled = false;
        }
        finally
        {
            _updating = false;
        }
    }
}
