namespace VectorAnimationEngine;

internal sealed record ImageInspectorState(string AssetId, string Name)
{
    public string SourcePath { get; init; } = "";
    public string SourceResolution { get; init; } = "";
    public string DisplayMode { get; init; } = "";
    public ImageFilterMode FilterMode { get; init; } = ImageFilterMode.Bilinear;
    public bool CanEdit { get; init; } = true;
    public string PixelsPerUnit { get; init; } = "";
    public string MaximumResolution { get; init; } = "";
    public string DecodedResolution { get; init; } = "";
    public string KeepTransparency { get; init; } = "";
    public string Crop { get; init; } = "";
    public string FlipHorizontal { get; init; } = "";
    public string FlipVertical { get; init; } = "";
}
internal sealed class ImageSettingsRequestedEventArgs(string assetId) : EventArgs
{
    public string AssetId { get; } = assetId;
}

/// <summary>Displays an image snapshot and emits intent; the host owns asset lookup and editing.</summary>
internal sealed class ImageInspectorPanel : UserControl
{
    private readonly Label _title = new();
    private readonly Label _name = InspectorModuleStyle.CreateTextLabel();
    private readonly ShotDirectorPanel.CameraSectionToggle _sourceDisclosure = new("Source file");
    private readonly ShotDirectorPanel.CameraSectionToggle _settingsDisclosure = new("Image Import Settings", expanded: false);
    private readonly Control _sourceTitle;
    private readonly Control _settingsTitle;
    private int _preferredLogicalHeight = 512;
    private readonly List<(Label Caption, Label Value)> _rows = [];
    private readonly ComboBox _displayMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Image display" };
    private readonly SvgIconButton _edit = new(SvgIconKind.Settings)
    {
        Margin = Padding.Empty, AutoEllipsis = true,
        AccessibleName = "Image Import Settings"
    };
    private readonly ToolTip _toolTip = new() { ShowAlways = true, AutoPopDelay = 15000 };
    private ImageInspectorState? _state;
    private bool _layoutReady;
    private bool _arranging;
    private bool _updating;

    public ImageInspectorPanel()
    {
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Font = Theme.UiFont();
        AutoScaleMode = AutoScaleMode.None;
        Padding = Padding.Empty;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AccessibleRole = AccessibleRole.Pane;
        AccessibleName = "Image";
        InspectorModuleStyle.StylePageTitle(_title);
        _title.Text = "Image";
        _name.Font = Theme.UiFont(Theme.InspectorSectionFontSize);
        _sourceTitle = InspectorModuleStyle.CreateSectionHeader("Source file", _sourceDisclosure, () => PerformLayout());
        _settingsTitle = InspectorModuleStyle.CreateSectionHeader("Image Import Settings", _settingsDisclosure, () => PerformLayout());
        Controls.AddRange([_title, _sourceTitle, _name, _settingsTitle, _edit]);
        foreach (var (caption, description) in new[]
        {
            ("Source file", "Source file"), ("Original size", "Source resolution"),
            ("Image display", "Image display"), ("Pixels per unit", "Pixels per unit"),
            ("Max size", "Maximum resolution"), ("Decoded size", "Decoded resolution"),
            ("Transparent", "Keep transparent background"),
            ("Image crop", "Image crop"),
            ("Flip Horizontal", "Flip Horizontal"), ("Flip Vertical", "Flip Vertical")
        })
        {
            var label = InspectorModuleStyle.CreateFieldLabel(caption);
            label.Dock = DockStyle.None;
            label.AccessibleDescription = description;
            var value = InspectorModuleStyle.CreateReadOnlyValueLabel();
            value.AccessibleName = description;
            _rows.Add((label, value));
            Controls.AddRange([label, value]);
        }
        Theme.StyleStandardButton(_edit);
        Theme.StyleToolTip(_toolTip);
        _displayMode.Items.AddRange(["Smooth", "Pixel art"]);
        InspectorModuleStyle.StyleField(_displayMode);
        _displayMode.SelectedIndexChanged += (_, _) =>
        {
            if (_updating || _state is not { CanEdit: true } state) return;
            var mode = _displayMode.SelectedIndex == 1 ? ImageFilterMode.Point
                : state.FilterMode == ImageFilterMode.Trilinear ? ImageFilterMode.Trilinear : ImageFilterMode.Bilinear;
            if (mode != state.FilterMode) DisplayModeChanged?.Invoke(state.AssetId, mode);
        };
        Controls.Add(_displayMode);
        _edit.Click += (_, _) =>
        {
            if (_state is { } state) SettingsRequested?.Invoke(this, new(state.AssetId));
        };
        _layoutReady = true;
        SetState(null);
        PerformLayout();
        UiLocalization.Watch(this);
    }

    public int PreferredHeight => ScaleMetric(_preferredLogicalHeight);
    public event EventHandler<ImageSettingsRequestedEventArgs>? SettingsRequested;
    public event Action<string, ImageFilterMode>? DisplayModeChanged;

    public void SetState(ImageInspectorState? state)
    {
        _state = state;
        _toolTip.SetToolTip(_displayMode, UiLocalization.T("Changes apply to all placements of this image asset."));
        _updating = true;
        try
        {
            _displayMode.SelectedIndex = state is null ? -1 : state.FilterMode == ImageFilterMode.Point ? 1 : 0;
            _displayMode.Enabled = state is { CanEdit: true };
        }
        finally { _updating = false; }
        SetText(_name, state?.Name ?? UiLocalization.T("None"));
        var values = new[] { state?.SourcePath, state?.SourceResolution, state?.DisplayMode,
            state?.PixelsPerUnit, state?.MaximumResolution, state?.DecodedResolution, state?.KeepTransparency,
            state?.Crop, state?.FlipHorizontal, state?.FlipVertical };
        for (var index = 0; index < _rows.Count; index++) SetText(_rows[index].Value, values[index] ?? "–");
        _edit.Text = UiLocalization.T("Image Import Settings");
        _edit.Enabled = state is { CanEdit: true };
    }

    private void SetText(Label label, string text)
    {
        if (label.Text != text) label.Text = text;
        label.AccessibleDescription = text;
        _toolTip.SetToolTip(label, text);
    }

    protected override void OnLayout(LayoutEventArgs eventArgs)
    {
        base.OnLayout(eventArgs);
        if (!_layoutReady || _arranging) return;
        _arranging = true;
        try
        {
            var inset = ScaleMetric(16);
            var width = Math.Max(1, ClientSize.Width - inset * 2);
            var split = (int)Math.Round(width * 0.48);
            _title.SetBounds(inset + ScaleMetric(24), ScaleMetric(16), Math.Max(1, width - ScaleMetric(24)), ScaleMetric(28));
            var top = 66;
            LayoutHeader(_sourceTitle, ref top);
            _name.Visible = _sourceDisclosure.Expanded;
            if (_sourceDisclosure.Expanded)
            {
                _name.SetBounds(inset, ScaleMetric(top), width, ScaleMetric(32));
                top += 40;
            }
            for (var index = 0; index < 2; index++) LayoutRow(index, _sourceDisclosure.Expanded, ref top);
            top += 8;
            LayoutHeader(_settingsTitle, ref top);
            for (var index = 2; index < _rows.Count; index++) LayoutRow(index, _settingsDisclosure.Expanded, ref top);
            _displayMode.Visible = _settingsDisclosure.Expanded;
            _displayMode.TabStop = _settingsDisclosure.Expanded;
            _sourceTitle.TabIndex = 0;
            _settingsTitle.TabIndex = 1;
            _displayMode.TabIndex = 2;
            _edit.TabIndex = 3;
            _edit.Visible = _settingsDisclosure.Expanded;
            _edit.TabStop = _settingsDisclosure.Expanded;
            if (_settingsDisclosure.Expanded)
            {
                _edit.SetBounds(inset, ScaleMetric(top), width, ScaleMetric(Theme.ControlHeight));
                top += 40;
            }
            _preferredLogicalHeight = top + 8;

            void LayoutHeader(Control header, ref int y)
            {
                header.Dock = DockStyle.None;
                header.SetBounds(inset, ScaleMetric(y), width, ScaleMetric(28));
                y += 36;
            }

            void LayoutRow(int index, bool expanded, ref int y)
            {
                var (caption, value) = _rows[index];
                caption.Visible = expanded;
                value.Visible = expanded && index != 2;
                if (!expanded) return;
                caption.SetBounds(inset, ScaleMetric(y), Math.Max(1, split - ScaleMetric(8)), ScaleMetric(32));
                value.SetBounds(inset + split, ScaleMetric(y), Math.Max(1, width - split), ScaleMetric(32));
                if (index == 2) _displayMode.SetBounds(value.Bounds.X, value.Bounds.Y, value.Width, ScaleMetric(32));
                _toolTip.SetToolTip(caption, caption.AccessibleDescription);
                y += 40;
            }
            if (Height != PreferredHeight) Height = PreferredHeight;
        }
        finally { _arranging = false; }
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        SvgIcons.Draw(eventArgs.Graphics, SvgIconKind.Image,
            new Rectangle(ScaleMetric(16), ScaleMetric(21), ScaleMetric(18), ScaleMetric(18)), Theme.PanelSecondaryText);
        InspectorModuleStyle.DrawDivider(this, eventArgs.Graphics, ScaleMetric(54));
        InspectorModuleStyle.DrawBottomDivider(this, eventArgs.Graphics);
    }

    protected override void OnDpiChangedAfterParent(EventArgs eventArgs)
    {
        base.OnDpiChangedAfterParent(eventArgs);
        PerformLayout();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _toolTip.Dispose();
        base.Dispose(disposing);
    }

    private int ScaleMetric(int value) => Math.Max(1, (int)Math.Round(value * DeviceDpi / 96f));
}
