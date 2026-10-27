using System.Drawing.Drawing2D;
using System.Globalization;

namespace VectorAnimationEngine;

internal sealed class MaterialChangedEventArgs : EventArgs
{
    public MaterialChangedEventArgs(
        Color fill,
        Color stroke,
        float strokeWidth,
        float opacity,
        bool fillChanged,
        bool strokeChanged,
        bool strokeWidthChanged,
        bool opacityChanged,
        bool applyAll)
    {
        Fill = fill;
        Stroke = stroke;
        StrokeWidth = strokeWidth;
        Opacity = opacity;
        FillChanged = fillChanged;
        StrokeChanged = strokeChanged;
        StrokeWidthChanged = strokeWidthChanged;
        OpacityChanged = opacityChanged;
        ApplyAll = applyAll;
    }

    public Color Fill { get; }
    public Color Stroke { get; }
    public float StrokeWidth { get; }
    public float Opacity { get; }
    public bool FillChanged { get; }
    public bool StrokeChanged { get; }
    public bool StrokeWidthChanged { get; }
    public bool OpacityChanged { get; }
    public bool ApplyAll { get; }
}

internal sealed class LineEndpointStyleChangedEventArgs(LineEndpointStyle endpointStyle, bool startEndpoint) : EventArgs
{
    public LineEndpointStyle EndpointStyle { get; } = endpointStyle;
    public bool StartEndpoint { get; } = startEndpoint;
}

internal sealed class GradientChangedEventArgs(GradientKind kind, GradientStop[] stops) : EventArgs
{
    public GradientKind Kind { get; } = kind;
    public GradientStop[] Stops { get; } = stops.ToArray();
    public bool Enabled => Kind != GradientKind.Solid;
    public Color Start => Stops.Length > 0 ? Color.FromArgb(Stops[0].Argb) : Color.White;
    public Color End => Stops.Length > 0 ? Color.FromArgb(Stops[^1].Argb) : Color.White;
}

internal sealed class MaterialEditorPanel : UserControl
{
    private const int MaxRecentColors = 10;
    private const int MaxCustomColors = 10;
    private const int MaxRecentGradients = 4;
    private const int MaxSavedGradients = 6;

    private static readonly Color[] BuiltInPalette =
    [
        Color.FromArgb(242, 246, 245),
        Color.FromArgb(190, 202, 202),
        Color.FromArgb(104, 115, 119),
        Color.FromArgb(30, 32, 34),
        Color.FromArgb(192, 57, 65),
        Color.FromArgb(229, 95, 66),
        Color.FromArgb(232, 157, 65),
        Color.FromArgb(228, 202, 78),
        Color.FromArgb(94, 177, 92),
        Color.FromArgb(79, 179, 162),
        Color.FromArgb(68, 153, 204),
        Color.FromArgb(78, 106, 191),
        Color.FromArgb(136, 122, 214),
        Color.FromArgb(190, 91, 172),
        Color.FromArgb(200, 107, 99),
        Color.FromArgb(109, 73, 54),
        Color.FromArgb(226, 176, 139),
        Color.FromArgb(87, 129, 111)
    ];

    private static readonly List<Color> RecentPalette = [];
    private static readonly List<Color> CustomPalette = [];
    private static readonly List<GradientPreset> RecentGradientPresets = [];
    private static readonly List<GradientPreset> SavedGradientPresets = [];
    private static bool GradientPaletteStateLoaded;
    private static readonly (ColorHarmonyMode Mode, string Label)[] HarmonyRules =
    [
        (ColorHarmonyMode.Complementary, "Complementary"),
        (ColorHarmonyMode.Analogous, "Analogous"),
        (ColorHarmonyMode.Triadic, "Triadic"),
        (ColorHarmonyMode.SplitComplementary, "Split complementary"),
        (ColorHarmonyMode.Tetradic, "Tetradic")
    ];
    private static readonly GradientPreset[] GradientPresets =
    [
        new("Sunset", GradientKind.Linear,
        [new GradientStop(0, Color.FromArgb(255, 255, 95, 109)), new GradientStop(0.52f, Color.FromArgb(255, 255, 195, 113)), new GradientStop(1, Color.FromArgb(255, 255, 241, 118))]),
        new("Ocean", GradientKind.Linear,
        [new GradientStop(0, Color.FromArgb(255, 0, 198, 255)), new GradientStop(0.54f, Color.FromArgb(255, 0, 114, 255)), new GradientStop(1, Color.FromArgb(255, 0, 27, 109))]),
        new("Aurora", GradientKind.Linear,
        [new GradientStop(0, Color.FromArgb(255, 0, 245, 160)), new GradientStop(0.5f, Color.FromArgb(255, 0, 217, 245)), new GradientStop(1, Color.FromArgb(255, 123, 47, 247))]),
        new("Ink", GradientKind.Linear,
        [new GradientStop(0, Color.FromArgb(255, 17, 24, 39)), new GradientStop(0.52f, Color.FromArgb(255, 51, 65, 85)), new GradientStop(1, Color.FromArgb(255, 167, 139, 250))]),
        new("Spotlight", GradientKind.Radial,
        [new GradientStop(0, Color.FromArgb(255, 255, 247, 194)), new GradientStop(0.45f, Color.FromArgb(255, 245, 158, 11)), new GradientStop(1, Color.FromArgb(255, 124, 45, 18))]),
        new("Bloom", GradientKind.Radial,
        [new GradientStop(0, Color.FromArgb(255, 255, 228, 240)), new GradientStop(0.48f, Color.FromArgb(255, 244, 114, 182)), new GradientStop(1, Color.FromArgb(255, 88, 28, 135))])
    ];

    private readonly ColorTargetButton _fillTarget = new("Fill");
    private readonly ColorTargetButton _strokeTarget = new("Stroke");
    private readonly ColorTargetButton _gradientStopTarget = new("Color");
    private readonly Button _solidFillMode = new SegmentedButton { Text = "Solid", CompactText = true };
    private readonly Button _linearGradientMode = new SegmentedButton { Text = "Linear", CompactText = true };
    private readonly Button _radialGradientMode = new SegmentedButton { Text = "Radial", CompactText = true };
    private readonly Button _shapeRadialGradientMode = new SegmentedButton { Text = "Shape", CompactText = true };
    private readonly Button _addGradientStop = new SvgIconButton(SvgIconKind.Add);
    private readonly Button _removeGradientStop = new SvgIconButton(SvgIconKind.Remove);
    private readonly SvgIconButton _paletteButton = new(SvgIconKind.Swatches);
    private readonly ModernNumericUpDown _gradientStopPosition = new() { Suffix = "%" };
    private readonly GradientStopStrip _gradientStopStrip = new();
    private readonly GradientPresetGrid _gradientPresetGrid = new();
    private readonly Panel _gradientPanel = new();
    private TableLayoutPanel? _gradientSettingsGrid;
    private readonly Dictionary<ColorMode, Button> _modeButtons = [];
    private readonly Label[] _channelLabels = [new(), new(), new(), new()];
    private readonly ColorComponentSlider[] _channelSliders = [new(), new(), new(), new()];
    private readonly ModernNumericUpDown[] _channelValues = [new(), new(), new(), new()];
    private readonly Panel _channelsPanel = new();
    private TableLayoutPanel? _channelRows;
    private TableLayoutPanel? _modeRow;
    private TableLayoutPanel? _pickerLayout;
    private readonly ColorComponentSlider _hueComponent = new() { Maximum = 360, CircularThumb = true };
    private readonly ColorTargetButton _currentColor = new(string.Empty);
    private readonly Button _copyHex = new SvgIconButton(SvgIconKind.Copy);
    private readonly ColorPaletteGrid _inlineRecentGrid = new();
    private readonly SvgIconButton _channelToggle = new(SvgIconKind.ChevronRight)
    {
        Text = "Color channels", IsDisclosureHeader = true
    };
    private readonly Panel _hexPanel = new();
    private readonly ModernTextInput _hexText = new();
    private readonly TraditionalColorPlane _traditionalPlane = new();
    private readonly VerticalColorComponentSlider _primaryComponent = new();
    private readonly ComboBox _harmonyRule = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ColorPaletteGrid _harmonyGrid = new();
    private readonly TableLayoutPanel _harmonyPanel = new();
    private readonly SvgIconButton _harmonyToggle = new(SvgIconKind.ChevronRight)
    {
        Text = "Harmony", IsDisclosureHeader = true
    };
    private readonly ColorPaletteGrid _builtInGrid = new();
    private readonly ColorPaletteGrid _recentGrid = new();
    private readonly GradientPresetGrid _recentGradientGrid = new();
    private readonly ColorPaletteGrid _customGrid = new();
    private readonly Label _recentPaletteTitle = CreateSectionLabel("Recent");
    private readonly Label _customPaletteTitle = CreateSectionLabel("Custom");
    private readonly Button _addCustom = new SvgIconButton(SvgIconKind.Add);
    private readonly Button _removeCustom = new SvgIconButton(SvgIconKind.Remove);
    private readonly Button _saveGradientPreset = new SvgIconButton(SvgIconKind.Add);
    private readonly Button _removeGradientPreset = new SvgIconButton(SvgIconKind.Remove);
    private readonly Label _colorPageTitle = new() { Text = "Appearance" };
    private readonly ModernNumericUpDown _strokeWidth = new() { Suffix = UiLocalization.T("pt"), ShowSuffix = true };
    private readonly Label _strokeWidthLabel = CreateFieldLabel("Width");
    private readonly Panel _lineEndpointStylePanel = new();
    private readonly Button _startSharpEndpointStyle = new SegmentedButton { Text = "Sharp", CompactText = true };
    private readonly Button _startRoundEndpointStyle = new SegmentedButton { Text = "Round", CompactText = true };
    private readonly Button _endSharpEndpointStyle = new SegmentedButton { Text = "Sharp", CompactText = true };
    private readonly Button _endRoundEndpointStyle = new SegmentedButton { Text = "Round", CompactText = true };
    private readonly ToolTip _toolTip = new();
    private readonly PalettePopupDropDown _paletteDropDown = new();
    private TableLayoutPanel? _content;
    private TableLayoutPanel? _targetRow;
    private Panel? _materialSettings;
    private bool _updating;
    private bool _updatingComponents;
    private bool _handlingColorChange;
    private const bool _colorEditorExpanded = true;
    private bool _harmonyExpanded;
    private bool _channelsExpanded;
    private bool _editingFill = true;
    private bool _gradientPreviewOnStroke;
    private GradientColorTarget _editingGradientTarget;
    private int _selectedGradientStop;
    private bool _hexInvalid;
    private int _colorInteractionDepth;
    private int _continuousControlInteractionDepth;
    private int _continuousColorStartArgb;
    private ColorMode _colorMode = ColorMode.Hsv;
    private Color _fill = Color.FromArgb(79, 179, 162);
    private Color _stroke = Color.FromArgb(238, 242, 241);
    private float _opacity = 1f;
    private GradientStop[] _gradientStops =
    [
        new GradientStop(0, Color.FromArgb(79, 179, 162)),
        new GradientStop(1, Color.FromArgb(238, 242, 241))
    ];
    private GradientKind _gradientKind = GradientKind.Solid;
    private GradientPaintState _fillGradient = new(
        GradientKind.Solid,
        [new GradientStop(0, Color.FromArgb(79, 179, 162)), new GradientStop(1, Color.FromArgb(238, 242, 241))]);
    private GradientPaintState _strokeGradient = new(
        GradientKind.Solid,
        [new GradientStop(0, Color.FromArgb(238, 242, 241)), new GradientStop(1, Color.FromArgb(238, 242, 241))]);
    private LineEndpointStyle _startEndpointStyle = LineEndpointStyle.Round;
    private LineEndpointStyle _endEndpointStyle = LineEndpointStyle.Round;
    private bool _lineEndpointStyleVisible;
    private bool _gradientSettingsExpanded = true;
    private bool _textObjectMode;
    private GradientPreset? _selectedSavedGradientPreset;

    public MaterialEditorPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Panel;
        MinimumSize = new Size(240, 0);
        Padding = new Padding(4, 0, 8, Theme.InspectorSectionPaddingVertical + 4);
        Theme.StyleToolTip(_toolTip);

        EnsureGradientPaletteStateLoaded();
        BuildUi();
        ApplyMaterial(_fill, _stroke, 1f, 1f, raiseEvent: false);
    }

    public event EventHandler<MaterialChangedEventArgs>? MaterialChanged;
    public event EventHandler? FillChanged;
    public event EventHandler? StrokeChanged;
    public event EventHandler? OpacityChanged;
    public event EventHandler? ContinuousEditStarted;
    public event EventHandler? ContinuousEditCompleted;
    public event EventHandler? ContinuousEditCanceled;
    public event EventHandler<LineEndpointStyleChangedEventArgs>? LineEndpointStyleChanged;
    public event EventHandler<GradientChangedEventArgs>? GradientChanged;
    /// <summary>
    /// Raised when the user picks the fill/stroke target by hand. The host uses it to stop
    /// re-applying its preferred target on every inspector refresh.
    /// </summary>
    internal event EventHandler? EditingTargetChanged;

    public Color Fill
    {
        get => _fill;
        set => ApplyMaterial(value, _stroke, StrokeWidth, Opacity, raiseEvent: true);
    }

    public IReadOnlyList<Color> RecentColors => RecentPalette.ToArray();

    public void SetFillColorForBrush(Color color)
    {
        var next = Color.FromArgb(_fill.A, color.R, color.G, color.B);
        ApplyMaterial(next, _stroke, StrokeWidth, Opacity, raiseEvent: false);
        AddRecentColor(next);
    }

    public void RecordRecentFillColor(Color color) => AddRecentColor(color);

    public Color Stroke
    {
        get => _stroke;
        set => ApplyMaterial(_fill, value, StrokeWidth, Opacity, raiseEvent: true);
    }

    public float StrokeWidth
    {
        get => (float)_strokeWidth.Value;
        set => ApplyMaterial(_fill, _stroke, value, Opacity, raiseEvent: true);
    }

    public float Opacity
    {
        get => _opacity;
        set => SetUniformAlpha(value);
    }

    public void SetLineEndpointStyles(LineEndpointStyle startEndpointStyle, LineEndpointStyle endEndpointStyle, bool visible)
    {
        var nextStartStyle = startEndpointStyle == LineEndpointStyle.Sharp
            ? LineEndpointStyle.Sharp
            : LineEndpointStyle.Round;
        var nextEndStyle = endEndpointStyle == LineEndpointStyle.Sharp
            ? LineEndpointStyle.Sharp
            : LineEndpointStyle.Round;
        var stylesChanged = _startEndpointStyle != nextStartStyle || _endEndpointStyle != nextEndStyle;
        var visibilityChanged = _lineEndpointStyleVisible != visible;
        var panelVisibilityChanged = _lineEndpointStylePanel.Visible != visible;
        var desiredRowHeight = visible ? InspectorHeight(Theme.InspectorRowHeight * 2) : 0f;
        var rowHeightChanged = _content is not null
            && _content.RowStyles.Count > 8
            && Math.Abs(_content.RowStyles[8].Height - desiredRowHeight) > 0.001f;
        if (!stylesChanged && !visibilityChanged && !panelVisibilityChanged && !rowHeightChanged) return;

        _startEndpointStyle = nextStartStyle;
        _endEndpointStyle = nextEndStyle;
        _lineEndpointStyleVisible = visible;
        if (visibilityChanged || panelVisibilityChanged || rowHeightChanged)
        {
            _lineEndpointStylePanel.Visible = visible;
            if (_content is not null && _content.RowStyles.Count > 8)
            {
                _content.RowStyles[8].Height = desiredRowHeight;
            }
        }

        if (visible) RefreshLineEndpointStyleButtons();
        UpdatePanelHeight();
    }

    public bool GradientEnabled => _gradientKind != GradientKind.Solid;
    public Color GradientStart => Color.FromArgb(_gradientStops[0].Argb);
    public Color GradientEnd => Color.FromArgb(_gradientStops[^1].Argb);
    public GradientKind GradientKind => _gradientKind;
    public GradientStop[] GradientStops => _gradientStops.ToArray();
    internal static IReadOnlyList<GradientPreset> BuiltInGradientPresets => GradientPresets;
    internal bool GradientPreviewOnStroke => _gradientPreviewOnStroke;
    internal bool EditingStroke => !_editingFill;
    internal bool EditingGradientStop => _editingGradientTarget == GradientColorTarget.Stop;
    internal bool GradientSettingsExpanded => _gradientSettingsExpanded;

    internal (GradientKind Kind, GradientStop[] Stops) GetGradientPaintForTarget(bool strokeTarget)
    {
        var paint = GradientForPreview(strokeTarget);
        return (paint.Kind, paint.Stops.ToArray());
    }

    public void SetGradientPreviewTarget(bool strokeTarget)
    {
        var editingFill = !strokeTarget;
        if (_gradientPreviewOnStroke == strokeTarget && _editingFill == editingFill) return;
        StoreActiveGradient();
        _gradientPreviewOnStroke = strokeTarget;
        _editingFill = editingFill;
        LoadGradientForTarget(strokeTarget);
        RefreshGradientPresentation();
        UpdateTargetPresentation();
        if (!_handlingColorChange && _colorInteractionDepth == 0) UpdateEditorFromColor();
    }

    private Color EditedColor => _editingGradientTarget switch
    {
        GradientColorTarget.Stop => Color.FromArgb(_gradientStops[Math.Clamp(_selectedGradientStop, 0, _gradientStops.Length - 1)].Argb),
        _ => _editingFill ? _fill : _stroke
    };

    public void SetMaterial(Color fill, Color stroke, float strokeWidth, float opacity)
    {
        ApplyMaterial(fill, stroke, strokeWidth, opacity, raiseEvent: false, isHostEcho: true);
    }

    public void SetTextObjectMode(bool enabled)
    {
        if (_textObjectMode == enabled || _content is null || _targetRow is null || _materialSettings is null) return;
        _textObjectMode = enabled;
        if (enabled) SetGradientPreviewTarget(strokeTarget: false);

        _content.SuspendLayout();
        _targetRow.SuspendLayout();
        try
        {
            _strokeTarget.Visible = !enabled;
            _targetRow.SetColumnSpan(_fillTarget, enabled ? 2 : 1);
            _gradientPanel.Visible = !enabled;
            _materialSettings.Visible = !enabled;
            _content.RowStyles[2].Height = enabled ? 0 : InspectorHeight(_gradientSettingsExpanded ? 132 : 40);
            _content.RowStyles[3].Height = enabled ? 0 : InspectorHeight(Theme.ControlHeight + Theme.InspectorRowMarginVertical * 2 + 16);
        }
        finally
        {
            _targetRow.ResumeLayout(performLayout: true);
            _content.ResumeLayout(performLayout: true);
        }

        UpdatePanelHeight();
    }

    public void SetGradient(bool enabled, Color start, Color end)
    {
        SetGradient(enabled ? GradientKind.Linear : GradientKind.Solid,
            [new GradientStop(0, start), new GradientStop(1, end)]);
    }

    public void SetGradient(GradientKind kind, IReadOnlyList<GradientStop> stops)
    {
        var normalized = NormalizeGradientStops(stops);
        var nextGradientTarget = kind == GradientKind.Solid
            ? GradientColorTarget.None
            : GradientColorTarget.Stop;
        if (_gradientKind == kind
            && _gradientStops.SequenceEqual(normalized)
            && _editingGradientTarget == nextGradientTarget)
        {
            return;
        }

        _updating = true;
        _gradientKind = kind;
        _gradientStops = normalized;
        _selectedGradientStop = Math.Clamp(_selectedGradientStop, 0, _gradientStops.Length - 1);
        _editingGradientTarget = nextGradientTarget;
        StoreActiveGradient();
        RefreshGradientPresentation();
        UpdateTargetPresentation();
        if (!_handlingColorChange && _colorInteractionDepth == 0) UpdateEditorFromColor();
        _updating = false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _paletteDropDown.Dispose();
            _toolTip.Dispose();
        }
        base.Dispose(disposing);
    }

    private void BuildUi()
    {
        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 1,
            RowCount = 15,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var height in new[] { InspectorModuleStyle.PageHeaderHeight + 12, 50, 132,
            Theme.ControlHeight + Theme.InspectorRowMarginVertical * 2 + 16,
            0, 0, 232, 44, Theme.InspectorRowHeight * 2, 80, 44, 0, 64, 40, 0 })
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        Controls.Add(content);
        _content = content;

        // TODO: Replace this basic shading editor with the full material system.

        BuildTargetRow(content);
        BuildGradientSettings(content);
        BuildMaterialSettings(content);
        BuildColorPageHeader(content);
        BuildModeRow(content);
        BuildColorEditor(content);
        BuildPersistentHex(content);
        BuildPalettePopup();
        BuildLineEndpointStyle(content);
        // Keep fields at their existing inset while the module title sits closer to the edge.
        foreach (Control section in content.Controls)
        {
            if (content.GetRow(section) == 0) continue;
            var margin = section.Margin;
            section.Margin = new Padding(margin.Left + 4, margin.Top, margin.Right, margin.Bottom);
        }
        ConfigureChannelMode();
        RefreshModeButtons();
        _channelsPanel.Visible = true;
        _channelsPanel.BringToFront();
        RefreshColorEditorLayout();
        SetLineEndpointStyles(LineEndpointStyle.Round, LineEndpointStyle.Round, visible: false);
    }

    private void BuildTargetRow(TableLayoutPanel content)
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _targetRow = row;

        _fillTarget.Dock = DockStyle.Fill;
        _fillTarget.Margin = new Padding(0, 3, 3, 3);
        _strokeTarget.Dock = DockStyle.Fill;
        _strokeTarget.OutlineSwatch = true;
        _strokeTarget.Margin = new Padding(3, 3, 0, 3);
        _fillTarget.Click += (_, _) => SelectTarget(fill: true);
        _strokeTarget.Click += (_, _) => SelectTarget(fill: false);
        _toolTip.SetToolTip(_fillTarget, "Edit fill color");
        _toolTip.SetToolTip(_strokeTarget, "Edit stroke color");
        row.Controls.Add(_fillTarget, 0, 0);
        row.Controls.Add(_strokeTarget, 1, 0);
        content.Controls.Add(row, 0, 1);
    }

    private void BuildColorPageHeader(TableLayoutPanel content)
    {
        InspectorModuleStyle.StylePageTitle(_colorPageTitle);
        _colorPageTitle.AccessibleRole = AccessibleRole.StaticText;
        var section = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        section.Controls.Add(_colorPageTitle);
        var divider = new Panel { BackColor = Theme.ControlOutline, TabStop = false };
        section.Controls.Add(divider);
        UiLocalization.Watch(_colorPageTitle);
        section.Paint += (_, eventArgs) =>
        {
            var scale = section.ClientSize.Height / (float)(InspectorModuleStyle.PageHeaderHeight + 12);
            SvgIcons.Draw(eventArgs.Graphics, SvgIconKind.InspectorColor,
                new Rectangle((int)Math.Round(8 * scale), (int)Math.Round(17 * scale),
                    (int)Math.Round(26 * scale), (int)Math.Round(26 * scale)), Theme.PanelSecondaryText);
        };
        section.Layout += (_, _) =>
        {
            var scale = section.ClientSize.Height / (float)(InspectorModuleStyle.PageHeaderHeight + 12);
            var top = (int)Math.Round(16 * scale);
            var height = (int)Math.Round(28 * scale);
            var left = (int)Math.Round(36 * scale);
            _colorPageTitle.SetBounds(left, top, Math.Max(1, section.ClientSize.Width - left), height);
            divider.SetBounds(0, (int)Math.Round(InspectorModuleStyle.PageHeaderHeight * scale),
                section.ClientSize.Width, Math.Max(1, (int)Math.Round(scale)));
        };
        content.Controls.Add(section, 0, 0);
    }

    private void BuildModeRow(TableLayoutPanel content)
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 4,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = new Padding(0, 4, 0, 4)
        };
        for (var i = 0; i < 4; i++) row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));

        AddModeButton(row, ColorMode.Rgb, "RGB", 0);
        AddModeButton(row, ColorMode.Hsv, "HSV", 1);
        AddModeButton(row, ColorMode.Hsl, "HSL", 2);
        AddModeButton(row, ColorMode.Lab, "Lab", 3);
        _modeRow = row;
        content.Controls.Add(row, 0, 5);
    }

    private void AddModeButton(TableLayoutPanel row, ColorMode mode, string text, int column)
    {
        var button = new SegmentedButton
        {
            Text = text,
            CompactText = true,
            ShowSelectionIndicator = true,
            Dock = DockStyle.Fill,
            Height = Theme.ControlHeightCompact,
            Margin = new Padding(column == 0 ? 0 : 1, 2, column == row.ColumnCount - 1 ? 0 : 1, 2),
            AccessibleName = text
        };
        button.AccessibleRole = AccessibleRole.RadioButton;
        Theme.StyleSegmentedButton(button);
        button.Click += (_, _) => SetColorMode(mode);
        if (mode == ColorMode.Lab) _toolTip.SetToolTip(button, UiLocalization.T("Edit color in CIELAB (D65)"));
        _modeButtons.Add(mode, button);
        row.Controls.Add(button, column, 0);
    }

    private void BuildColorEditor(TableLayoutPanel content)
    {
        _pickerLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = Theme.Panel, Margin = Padding.Empty,
            Padding = new Padding(0, 8, 0, 12), ColumnCount = 2, RowCount = 2
        };
        _pickerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _pickerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 24));
        _pickerLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _pickerLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        content.Controls.Add(_pickerLayout, 0, 6);
        _traditionalPlane.Dock = DockStyle.Fill;
        _traditionalPlane.Margin = Padding.Empty;
        _traditionalPlane.ColorAt = TraditionalPlaneColor;
        _traditionalPlane.ValueChanged += (_, _) => TraditionalPlaneChanged();
        _traditionalPlane.InteractionStarted += (_, _) => BeginColorContinuousEdit();
        _traditionalPlane.InteractionCompleted += (_, _) => CompleteColorContinuousEdit();
        _traditionalPlane.InteractionCanceled += (_, _) => CancelColorContinuousEdit();
        _pickerLayout.Controls.Add(_traditionalPlane, 0, 0);
        _primaryComponent.Dock = DockStyle.Fill;
        _primaryComponent.Margin = new Padding(8, 0, 0, 0);
        _primaryComponent.GradientColor = TraditionalPrimaryGradient;
        _primaryComponent.ValueChanged += (_, _) => TraditionalPrimaryChanged();
        _primaryComponent.InteractionStarted += (_, _) => BeginColorContinuousEdit();
        _primaryComponent.InteractionCompleted += (_, _) => CompleteColorContinuousEdit();
        _primaryComponent.InteractionCanceled += (_, _) => CancelColorContinuousEdit();
        _pickerLayout.Controls.Add(_primaryComponent, 1, 0);
        _hueComponent.Dock = DockStyle.Fill;
        _hueComponent.Margin = new Padding(0, 8, 0, 0);
        _hueComponent.AccessibleName = "Hue";
        _hueComponent.GradientColor = TraditionalPrimaryGradient;
        _hueComponent.ValueChanged += (_, _) =>
        {
            if (_updatingComponents) return;
            _updatingComponents = true;
            _primaryComponent.SetValue(_hueComponent.Value / 360f);
            _updatingComponents = false;
            TraditionalPrimaryChanged();
        };
        _hueComponent.InteractionStarted += (_, _) => BeginColorContinuousEdit();
        _hueComponent.InteractionCompleted += (_, _) => CompleteColorContinuousEdit();
        _hueComponent.InteractionCanceled += (_, _) => CancelColorContinuousEdit();
        _pickerLayout.Controls.Add(_hueComponent, 0, 1);
        _pickerLayout.SetColumnSpan(_hueComponent, 2);

        var opacity = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = Theme.Panel, Margin = Padding.Empty,
            Padding = new Padding(0, 10, 0, 8), ColumnCount = 2, RowCount = 2
        };
        opacity.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        opacity.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        opacity.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.ControlHeight));
        opacity.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.Controls.Add(opacity, 0, 9);
        ConfigureChannelEditor(3);
        _channelLabels[3].Dock = DockStyle.Fill;
        _channelLabels[3].Margin = Padding.Empty;
        _channelValues[3].Margin = Padding.Empty;
        _channelSliders[3].Margin = new Padding(0, 8, 0, 0);
        _channelSliders[3].CircularThumb = true;
        opacity.Controls.Add(_channelLabels[3], 0, 0);
        opacity.Controls.Add(_channelValues[3], 1, 0);
        opacity.Controls.Add(_channelSliders[3], 0, 1);
        opacity.SetColumnSpan(_channelSliders[3], 2);

        _channelToggle.Dock = DockStyle.Fill;
        _channelToggle.Margin = new Padding(0, 10, 0, 0);
        _channelToggle.AccessibleName = "Color channels";
        _channelToggle.AccessibleRole = AccessibleRole.CheckButton;
        Theme.StyleToolbarButton(_channelToggle);
        _channelToggle.Click += (_, _) => SetColorChannelsExpanded(!_channelsExpanded);
        content.Controls.Add(_channelToggle, 0, 10);
        _channelsPanel.Dock = DockStyle.Fill;
        _channelsPanel.BackColor = Theme.Panel;
        _channelsPanel.Margin = Padding.Empty;
        _channelsPanel.Padding = new Padding(0, 0, 0, 8);
        var channelContent = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = Theme.Panel, Margin = Padding.Empty,
            ColumnCount = 1, RowCount = 2
        };
        channelContent.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        channelContent.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        channelContent.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _channelsPanel.Controls.Add(channelContent);
        if (_modeRow is not null) channelContent.Controls.Add(_modeRow, 0, 0);
        _channelRows = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = Theme.Panel, Margin = Padding.Empty,
            ColumnCount = 3, RowCount = 1
        };
        for (var index = 0; index < 3; index++)
        {
            _channelRows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
            var editor = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, BackColor = Theme.Panel,
                Margin = new Padding(index == 0 ? 0 : 4, 0, index == 2 ? 0 : 4, 0),
                ColumnCount = 1, RowCount = 2
            };
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
            editor.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            ConfigureChannelEditor(index);
            _channelLabels[index].Margin = Padding.Empty;
            _channelValues[index].Margin = Padding.Empty;
            editor.Controls.Add(_channelLabels[index], 0, 0);
            editor.Controls.Add(_channelValues[index], 0, 1);
            _channelSliders[index].Visible = false;
            _channelSliders[index].TabStop = false;
            _channelsPanel.Controls.Add(_channelSliders[index]);
            _channelRows.Controls.Add(editor, index, 0);
        }
        _channelRows.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        channelContent.Controls.Add(_channelRows, 0, 1);
        content.Controls.Add(_channelsPanel, 0, 11);

        var recent = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = Theme.Panel, Margin = Padding.Empty,
            Padding = new Padding(0, 10, 0, 6), ColumnCount = 1, RowCount = 2
        };
        recent.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        recent.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        recent.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        recent.Controls.Add(CreateSectionLabel("Recent colors"), 0, 0);
        ConfigurePaletteGrid(_inlineRecentGrid);
        _inlineRecentGrid.BackColor = Theme.Panel;
        _inlineRecentGrid.InheritSurface = true;
        _inlineRecentGrid.SwatchSize = 24;
        _inlineRecentGrid.Gap = 6;
        _inlineRecentGrid.EmptyText = "No recent colors";
        recent.Controls.Add(_inlineRecentGrid, 0, 1);
        _inlineRecentGrid.SizeChanged += (_, _) => UpdateInlineRecentLayout();
        _inlineRecentGrid.DpiChangedAfterParent += (_, _) => UpdateInlineRecentLayout();
        content.Controls.Add(recent, 0, 12);

        _harmonyToggle.Dock = DockStyle.Fill;
        _harmonyToggle.Margin = new Padding(0, 8, 0, 0);
        _harmonyToggle.AccessibleName = "Color harmony rule";
        _harmonyToggle.AccessibleRole = AccessibleRole.CheckButton;
        Theme.StyleToolbarButton(_harmonyToggle);
        _harmonyToggle.Click += (_, _) => SetHarmonyExpanded(!_harmonyExpanded);
        content.Controls.Add(_harmonyToggle, 0, 13);
        _harmonyPanel.Dock = DockStyle.Fill;
        _harmonyPanel.BackColor = Theme.Panel;
        _harmonyPanel.Margin = Padding.Empty;
        _harmonyPanel.ColumnCount = 2;
        _harmonyPanel.RowCount = 2;
        _harmonyPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
        _harmonyPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
        _harmonyPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.ControlHeight + 6));
        _harmonyPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _harmonyPanel.Controls.Add(CreateFieldLabel("Color harmony rule"), 0, 0);
        _harmonyRule.Dock = DockStyle.Fill;
        _harmonyRule.Margin = new Padding(0, 3, 0, 3);
        _harmonyRule.AccessibleName = "Color harmony rule";
        _harmonyRule.Items.AddRange(HarmonyRules.Select(rule => (object)rule.Label).ToArray());
        _harmonyRule.SelectedIndex = 0;
        Theme.StyleComboBox(_harmonyRule);
        _harmonyRule.Font = Theme.UiFont(8.8f);
        Theme.StyleComboBoxSelection(_harmonyRule, Font.Size, Theme.ControlHeight);
        _harmonyRule.DropDownWidth = 176;
        _harmonyRule.SelectedIndexChanged += (_, _) => HarmonyRuleChanged();
        _toolTip.SetToolTip(_harmonyRule, "Choose a color harmony rule");
        _harmonyPanel.Controls.Add(_harmonyRule, 1, 0);
        void LayoutHarmonyRule()
        {
            var gap = Math.Max(1, (int)Math.Round(3f * _harmonyRule.DeviceDpi / 96f));
            _harmonyPanel.RowStyles[0].Height = _harmonyRule.Height + gap * 2;
            SetHarmonyExpanded(_harmonyExpanded);
        }
        _harmonyRule.SizeChanged += (_, _) => LayoutHarmonyRule();
        _harmonyRule.DpiChangedAfterParent += (_, _) => LayoutHarmonyRule();
        _harmonyGrid.Dock = DockStyle.Fill;
        _harmonyGrid.Margin = new Padding(0, 7, 0, 0);
        _harmonyGrid.SwatchSize = 32;
        _harmonyGrid.InheritSurface = true;
        _harmonyGrid.Gap = 6;
        _harmonyGrid.ColorSelected += (_, e) => SelectPaletteColor(e.Color);
        _harmonyGrid.HoveredColorChanged += (_, e) => _toolTip.SetToolTip(_harmonyGrid, ToHex(e.Color));
        _harmonyGrid.HoverCleared += (_, _) => _toolTip.SetToolTip(_harmonyGrid, string.Empty);
        _harmonyPanel.Controls.Add(_harmonyGrid, 0, 1);
        _harmonyPanel.SetColumnSpan(_harmonyGrid, 2);
        content.Controls.Add(_harmonyPanel, 0, 14);
    }

    private void SetColorChannelsExpanded(bool expanded)
    {
        _channelsExpanded = expanded;
        if (_content is null) return;
        _channelsPanel.Visible = _colorEditorExpanded && expanded;
        _content.RowStyles[11].Height = _colorEditorExpanded && expanded ? InspectorHeight(100) : 0;
        _channelToggle.Icon = expanded ? SvgIconKind.ChevronDown : SvgIconKind.ChevronRight;
        Theme.StyleToolbarButton(_channelToggle, expanded);
        _channelToggle.AccessibleDescription = UiLocalization.T(
            expanded ? "Color channels expanded" : "Color channels collapsed");
        UpdatePanelHeight();
    }

    private void UpdateInlineRecentLayout()
    {
        if (_content is null) return;
        _inlineRecentGrid.Gap = (int)Math.Round(6f * _inlineRecentGrid.DeviceDpi / 96f);
        var minimumSwatch = (int)Math.Round(24f * _inlineRecentGrid.DeviceDpi / 96f);
        _inlineRecentGrid.SwatchSize = Math.Max(minimumSwatch,
            (_inlineRecentGrid.ClientSize.Width - _inlineRecentGrid.Gap * 7) / 8);
        var columns = Math.Max(1, (_inlineRecentGrid.ClientSize.Width + _inlineRecentGrid.Gap)
            / (_inlineRecentGrid.SwatchSize + _inlineRecentGrid.Gap));
        var rows = Math.Max(1, (Math.Min(8, RecentPalette.Count) + columns - 1) / columns);
        var visible = _colorEditorExpanded && RecentPalette.Count > 0;
        if (_inlineRecentGrid.Parent is { } recent) recent.Visible = visible;
        var height = visible
            ? InspectorHeight(40) + rows * (_inlineRecentGrid.SwatchSize + _inlineRecentGrid.Gap) - _inlineRecentGrid.Gap : 0;
        if (Math.Abs(_content.RowStyles[12].Height - height) < 0.1f) return;
        _content.RowStyles[12].Height = height;
        UpdatePanelHeight();
    }

    private void SetHarmonyExpanded(bool expanded)
    {
        _harmonyExpanded = expanded;
        if (_content is null) return;
        _content.SuspendLayout();
        try
        {
            _harmonyPanel.Visible = _colorEditorExpanded && expanded;
            _content.RowStyles[14].Height = _colorEditorExpanded && expanded
                ? _harmonyPanel.RowStyles[0].Height + InspectorHeight(48) : 0;
            UpdatePanelHeight();
        }
        finally
        {
            _content.ResumeLayout(performLayout: true);
        }
        _harmonyToggle.Icon = expanded ? SvgIconKind.ChevronDown : SvgIconKind.ChevronRight;
        Theme.StyleToolbarButton(_harmonyToggle, expanded);
        _harmonyToggle.AccessibleDescription = UiLocalization.T(
            expanded ? "Color harmony expanded" : "Color harmony collapsed");
    }

    private void BuildPersistentHex(TableLayoutPanel content)
    {
        _hexPanel.Dock = DockStyle.Fill;
        _hexPanel.Margin = Padding.Empty;
        _hexPanel.BackColor = Theme.Panel;
        content.Controls.Add(_hexPanel, 0, 7);
        var hexRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = Theme.Panel, Margin = Padding.Empty,
            Padding = new Padding(0, 6, 0, 6), ColumnCount = 3, RowCount = 1
        };
        hexRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        hexRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));
        hexRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        hexRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));
        _currentColor.Dock = DockStyle.Fill;
        _currentColor.Margin = new Padding(0, 0, 8, 0);
        _currentColor.AccessibleName = "Current color";
        _currentColor.Click += (_, _) => TogglePalettePopup(_currentColor);
        _toolTip.SetToolTip(_currentColor, "Colors");
        hexRow.Controls.Add(_currentColor, 0, 0);
        _hexText.Dock = DockStyle.Fill;
        _hexText.Margin = Padding.Empty;
        _hexText.AccessibleName = "Hex color";
        _hexText.CharacterCasing = CharacterCasing.Upper;
        _hexText.PrefixText = "HEX";
        _hexText.MaxLength = 9;
        Theme.StyleTextBox(_hexText);
        _hexText.KeyDown += HexTextKeyDown;
        _hexText.Leave += (_, _) => CommitHexText();
        hexRow.Controls.Add(_hexText, 1, 0);
        _copyHex.Dock = DockStyle.Fill;
        _copyHex.Margin = new Padding(8, 0, 0, 0);
        _copyHex.AccessibleName = "Copy HEX color";
        Theme.StyleStandardButton(_copyHex);
        _toolTip.SetToolTip(_copyHex, "Copy HEX color");
        _copyHex.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(DisplayHex(EditedColor));
                _toolTip.SetToolTip(_copyHex, UiLocalization.T("Color copied"));
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                _toolTip.SetToolTip(_copyHex, UiLocalization.T("Clipboard is busy. Try again."));
            }
        };
        hexRow.Controls.Add(_copyHex, 2, 0);
        _hexPanel.Controls.Add(hexRow);
        void LayoutHexRow()
        {
            var gap = Math.Max(1, (int)Math.Round(6f * _hexText.DeviceDpi / 96f));
            hexRow.Padding = new Padding(0, gap, 0, gap);
            var height = Math.Max(InspectorHeight(Theme.ControlHeight) + gap * 2,
                _hexText.PreferredFieldHeight + gap * 2);
            if (Math.Abs(content.RowStyles[7].Height - height) < 0.1f) return;
            content.RowStyles[7].Height = height;
            UpdatePanelHeight();
        }
        _hexText.FontChanged += (_, _) => LayoutHexRow();
        _hexText.DpiChangedAfterParent += (_, _) => LayoutHexRow();
        hexRow.Layout += (_, _) => LayoutHexRow();
        LayoutHexRow();
    }

    private void ConfigureChannelEditor(int index)
    {
        var label = _channelLabels[index];
        label.Dock = DockStyle.Fill;
        label.ForeColor = Theme.Muted;
        label.BackColor = Theme.Panel;
        label.TextAlign = ContentAlignment.MiddleLeft;
        label.Font = Theme.UiFont(8.5f);
        label.Margin = Padding.Empty;
        label.AutoEllipsis = true;
        var slider = _channelSliders[index];
        slider.Dock = DockStyle.Fill;
        slider.GradientColor = amount => ChannelGradient(index, amount);
        slider.ValueChanged += (_, _) => ChannelSliderChanged(index);
        slider.InteractionStarted += (_, _) => BeginColorContinuousEdit();
        slider.InteractionCompleted += (_, _) => CompleteColorContinuousEdit();
        slider.InteractionCanceled += (_, _) => CancelColorContinuousEdit();
        var value = _channelValues[index];
        value.Minimum = 0;
        value.Maximum = 255;
        value.DecimalPlaces = 0;
        value.Increment = 1;
        value.Dock = DockStyle.Fill;
        Theme.StyleNumeric(value);
        value.ValueChanged += (_, _) => ChannelNumericChanged(index);
        value.InteractionStarted += (_, _) => BeginColorContinuousEdit();
        value.InteractionCompleted += (_, _) => CompleteColorContinuousEdit();
        value.InteractionCanceled += (_, _) => CancelColorContinuousEdit();
    }

    private void BuildPalettePopup()
    {
        var palette = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.PanelStrong,
            ColumnCount = 1,
            RowCount = 8,
            Margin = Padding.Empty,
            Padding = new Padding(12),
            Size = new Size(320, 430)
        };
        palette.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var height in new[] { 26, 56, 30, 52, 30, 32, 30, 150 })
            palette.RowStyles.Add(new RowStyle(SizeType.Absolute, height));

        palette.Controls.Add(CreatePalettePopupLabel("Colors"), 0, 0);
        ConfigurePaletteGrid(_builtInGrid);
        _builtInGrid.SetColors(BuiltInPalette);
        palette.Controls.Add(_builtInGrid, 0, 1);

        _recentPaletteTitle.BackColor = Theme.PanelStrong;
        _recentPaletteTitle.ForeColor = Theme.SecondaryText;
        _recentPaletteTitle.Font = Theme.UiFont(8.5f, FontStyle.Bold);
        _recentPaletteTitle.Margin = Padding.Empty;
        palette.Controls.Add(_recentPaletteTitle, 0, 2);
        var recent = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.PanelStrong,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty
        };
        recent.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        recent.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        ConfigurePaletteGrid(_recentGrid);
        _recentGrid.EmptyText = "No recent colors";
        recent.Controls.Add(_recentGrid, 0, 0);
        _recentGradientGrid.Dock = DockStyle.Fill;
        _recentGradientGrid.BackColor = Theme.PanelStrong;
        _recentGradientGrid.Margin = new Padding(4, 0, 0, 0);
        _recentGradientGrid.AccessibleName = "Recent gradients";
        _recentGradientGrid.PresetSelected += (_, e) =>
        {
            _selectedSavedGradientPreset = null;
            ApplyGradientPreset(e.Preset);
        };
        _recentGradientGrid.HoveredPresetChanged += (_, e) => _toolTip.SetToolTip(_recentGradientGrid, $"Recent {e.Preset.Kind} gradient");
        _recentGradientGrid.HoverCleared += (_, _) => _toolTip.SetToolTip(_recentGradientGrid, string.Empty);
        recent.Controls.Add(_recentGradientGrid, 1, 0);
        palette.Controls.Add(recent, 0, 3);

        var customHeader = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.PanelStrong,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty
        };
        _customPaletteTitle.BackColor = Theme.PanelStrong;
        _customPaletteTitle.ForeColor = Theme.SecondaryText;
        _customPaletteTitle.Font = Theme.UiFont(8.5f, FontStyle.Bold);
        _customPaletteTitle.Margin = Padding.Empty;
        customHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        customHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28));
        customHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28));
        customHeader.Controls.Add(_customPaletteTitle, 0, 0);
        ConfigurePaletteAction(_addCustom, "Add current color to custom palette");
        ConfigurePaletteAction(_removeCustom, "Remove selected custom color");
        _addCustom.Click += (_, _) => AddCurrentToCustomPalette();
        _removeCustom.Click += (_, _) => RemoveFocusedCustomColor();
        customHeader.Controls.Add(_addCustom, 1, 0);
        customHeader.Controls.Add(_removeCustom, 2, 0);
        palette.Controls.Add(customHeader, 0, 4);

        ConfigurePaletteGrid(_customGrid);
        _customGrid.EmptyText = "No saved colors";
        _customGrid.ColorRemoveRequested += (_, e) => RemoveCustomColor(e.Color);
        palette.Controls.Add(_customGrid, 0, 5);

        var gradientHeader = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.PanelStrong,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty
        };
        gradientHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        gradientHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28));
        gradientHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28));
        gradientHeader.Controls.Add(CreatePalettePopupLabel("Gradients"), 0, 0);
        ConfigurePaletteAction(_saveGradientPreset, "Save current gradient");
        ConfigurePaletteAction(_removeGradientPreset, "Remove selected saved gradient");
        _saveGradientPreset.Click += (_, _) => SaveCurrentGradientPreset();
        _removeGradientPreset.Click += (_, _) => RemoveSelectedGradientPreset();
        gradientHeader.Controls.Add(_saveGradientPreset, 1, 0);
        gradientHeader.Controls.Add(_removeGradientPreset, 2, 0);
        palette.Controls.Add(gradientHeader, 0, 6);
        _gradientPresetGrid.Dock = DockStyle.Fill;
        _gradientPresetGrid.BackColor = Theme.PanelStrong;
        _gradientPresetGrid.Margin = Padding.Empty;
        _gradientPresetGrid.SetPresets(AllGradientPresets());
        _gradientPresetGrid.PresetSelected += (_, e) =>
        {
            _selectedSavedGradientPreset = e.Preset.IsUserSaved ? e.Preset : null;
            RefreshGradientPresetActions();
            ApplyGradientPreset(e.Preset);
        };
        _gradientPresetGrid.HoveredPresetChanged += (_, e) => _toolTip.SetToolTip(_gradientPresetGrid, $"{e.Preset.Name} ({e.Preset.Kind})");
        _gradientPresetGrid.HoverCleared += (_, _) => _toolTip.SetToolTip(_gradientPresetGrid, string.Empty);
        palette.Controls.Add(_gradientPresetGrid, 0, 7);

        _paletteDropDown.Renderer = new PalettePopupRenderer();
        UiLocalization.Watch(palette);
        _paletteDropDown.Size = new Size(palette.Width + 8, palette.Height + 8);
        _paletteDropDown.Items.Add(new ToolStripControlHost(palette)
        {
            AutoSize = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            Size = palette.Size
        });

        _paletteButton.Dock = DockStyle.Fill;
        _paletteButton.Margin = new Padding(4, 3, 0, 3);
        _paletteButton.AccessibleName = "Color and gradient palette";
        Theme.StyleButton(_paletteButton);
        _toolTip.SetToolTip(_paletteButton, "Open color and gradient palettes");
        _paletteButton.Click += (_, _) => TogglePalettePopup();
        RefreshRuntimePalettes();
    }

    private void ConfigurePaletteGrid(ColorPaletteGrid grid)
    {
        grid.Dock = DockStyle.Fill;
        grid.BackColor = Theme.PanelStrong;
        grid.Margin = Padding.Empty;
        grid.SwatchSize = 22;
        grid.Gap = 6;
        grid.ColorSelected += (_, e) => SelectPaletteColor(e.Color);
        grid.HoveredColorChanged += (_, e) => _toolTip.SetToolTip(grid, ToHex(e.Color));
        grid.HoverCleared += (_, _) => _toolTip.SetToolTip(grid, string.Empty);
    }

    private void ConfigurePaletteAction(Button button, string toolTip)
    {
        button.Dock = DockStyle.Fill;
        button.Margin = new Padding(3, 1, 0, 1);
        button.AccessibleName = toolTip;
        button.AccessibleRole = AccessibleRole.PushButton;
        Theme.StyleToolbarButton(button);
        _toolTip.SetToolTip(button, toolTip);
    }

    private void TogglePalettePopup()
    {
        TogglePalettePopup(_paletteButton);
    }

    private void TogglePalettePopup(Control anchor)
    {
        DropDownToggle.Toggle(_paletteDropDown, () => ShowPalettePopup(anchor));
    }

    private void ShowPalettePopup(Control source)
    {
        RefreshRuntimePalettes();
        _gradientPresetGrid.SetSelection(_gradientKind, _gradientStops);
        var anchor = source.RectangleToScreen(source.ClientRectangle);
        var workingArea = Screen.FromControl(source).WorkingArea;
        var x = Math.Clamp(anchor.Right - _paletteDropDown.Width, workingArea.Left,
            Math.Max(workingArea.Left, workingArea.Right - _paletteDropDown.Width));
        var y = anchor.Bottom;
        if (y + _paletteDropDown.Height > workingArea.Bottom) y = anchor.Top - _paletteDropDown.Height;
        y = Math.Clamp(y, workingArea.Top, Math.Max(workingArea.Top, workingArea.Bottom - _paletteDropDown.Height));
        _paletteDropDown.Show(new Point(x, y));
    }

    private void ClosePalettePopup()
    {
        if (_paletteDropDown.Visible) _paletteDropDown.Close();
    }

    private static void EnsureGradientPaletteStateLoaded()
    {
        if (GradientPaletteStateLoaded) return;
        GradientPaletteStateLoaded = true;
        SavedGradientPresets.Clear();
        SavedGradientPresets.AddRange(MaterialPaletteStore.LoadSavedGradients().Take(MaxSavedGradients));
    }

    private static GradientPreset[] AllGradientPresets() => SavedGradientPresets.Concat(GradientPresets).ToArray();

    private void SaveCurrentGradientPreset()
    {
        if (_gradientKind == GradientKind.Solid) return;
        var existing = SavedGradientPresets.FirstOrDefault(preset => GradientMatches(preset, _gradientKind, _gradientStops));
        if (existing is not null)
        {
            SavedGradientPresets.Remove(existing);
        }
        else
        {
            existing = new GradientPreset(NextSavedGradientName(), _gradientKind, _gradientStops, isUserSaved: true);
        }

        SavedGradientPresets.Insert(0, existing);
        if (SavedGradientPresets.Count > MaxSavedGradients) SavedGradientPresets.RemoveRange(MaxSavedGradients, SavedGradientPresets.Count - MaxSavedGradients);
        _selectedSavedGradientPreset = existing;
        MaterialPaletteStore.SaveSavedGradients(SavedGradientPresets);
        RefreshRuntimePalettes();
    }

    private void RemoveSelectedGradientPreset()
    {
        var selected = _selectedSavedGradientPreset;
        if (selected is null || !SavedGradientPresets.Remove(selected)) return;

        _selectedSavedGradientPreset = null;
        MaterialPaletteStore.SaveSavedGradients(SavedGradientPresets);
        RefreshRuntimePalettes();
    }

    private string NextSavedGradientName()
    {
        for (var index = 1; ; index++)
        {
            var name = $"Saved {index}";
            if (SavedGradientPresets.All(preset => !string.Equals(preset.Name, name, StringComparison.OrdinalIgnoreCase))) return name;
        }
    }

    private static bool GradientMatches(GradientPreset preset, GradientKind kind, IReadOnlyList<GradientStop> stops) => preset.Kind == kind && preset.Stops.SequenceEqual(stops);

    private void AddRecentGradient(GradientKind kind, IReadOnlyList<GradientStop> stops)
    {
        if (kind == GradientKind.Solid) return;
        var existing = RecentGradientPresets.FirstOrDefault(preset => GradientMatches(preset, kind, stops));
        if (existing is not null) RecentGradientPresets.Remove(existing);
        else existing = new GradientPreset("Recent", kind, stops);
        RecentGradientPresets.Insert(0, existing);
        if (RecentGradientPresets.Count > MaxRecentGradients) RecentGradientPresets.RemoveRange(MaxRecentGradients, RecentGradientPresets.Count - MaxRecentGradients);
        RefreshRuntimePalettes();
    }

    private void RefreshGradientPresetActions()
    {
        _saveGradientPreset.Enabled = _gradientKind != GradientKind.Solid;
        _removeGradientPreset.Enabled = _selectedSavedGradientPreset is not null && SavedGradientPresets.Contains(_selectedSavedGradientPreset);
    }

    private void BuildMaterialSettings(TableLayoutPanel content)
    {
        var settings = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            Margin = Padding.Empty,
            Padding = new Padding(0, 8, 0, 8)
        };
        _materialSettings = settings;
        content.Controls.Add(settings, 0, 3);

        settings.Controls.Add(_strokeWidthLabel);
        _strokeWidth.Minimum = 0.1m;
        _strokeWidth.Maximum = 32;
        _strokeWidth.DecimalPlaces = 1;
        _strokeWidth.Increment = 0.1m;
        _strokeWidthLabel.Dock = DockStyle.None;
        _strokeWidth.Dock = DockStyle.None;
        _strokeWidth.Margin = Theme.InspectorFieldMargin(rightGap: false);
        Theme.StyleNumeric(_strokeWidth);
        _strokeWidth.Font = Theme.UiFont(11f);
        _strokeWidthLabel.Font = Theme.UiFont();
        _strokeWidth.ValueChanged += (_, _) => RaiseMaterialChanged(strokeWidthChanged: true);
        _strokeWidth.InteractionStarted += (_, _) => BeginGradientContinuousEdit();
        _strokeWidth.InteractionCompleted += (_, _) => CompleteGradientContinuousEdit(recordGradient: false);
        _strokeWidth.InteractionCanceled += (_, _) => CancelGradientContinuousEdit();
        settings.Controls.Add(_strokeWidth);
        settings.Layout += (_, _) =>
        {
            var area = settings.DisplayRectangle;
            var start = area.Left + (int)Math.Floor(area.Width * 0.48f);
            var gap = (int)Math.Round(Theme.InspectorRowMarginVertical * settings.DeviceDpi / 96f);
            var height = (int)Math.Round(Theme.ControlHeight * settings.DeviceDpi / 96f);
            _strokeWidthLabel.SetBounds(area.Left, area.Top + gap, Math.Max(1, start - area.Left), height);
            _strokeWidth.SetBounds(start, area.Top + gap, Math.Max(1, area.Right - start), height);
        };
    }

    private void BuildGradientSettings(TableLayoutPanel content)
    {
        _gradientPanel.Dock = DockStyle.Fill;
        _gradientPanel.BackColor = Theme.Panel;
        _gradientPanel.Margin = Padding.Empty;
        content.Controls.Add(_gradientPanel, 0, 2);

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 6,
            RowCount = 3,
            Margin = Padding.Empty
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48));
        for (var index = 0; index < 3; index++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        _gradientPanel.Controls.Add(grid);
        _gradientSettingsGrid = grid;

        var modes = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 5,
            RowCount = 1,
            Margin = Padding.Empty
        };
        for (var index = 0; index < 4; index++) modes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25f));
        modes.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28));
        grid.Controls.Add(modes, 0, 0);
        grid.SetColumnSpan(modes, 6);
        void UpdateGradientModeLabels()
        {
            // Measure each localized caption in its actual slot, including DPI.
            // A fixed panel-width threshold previously caused premature ellipsis.
            SetCaption(_solidFillMode, "Solid", "Sol");
            SetCaption(_linearGradientMode, "Linear", "Lin");
            SetCaption(_radialGradientMode, "Radial", "Rad");
            SetCaption(_shapeRadialGradientMode, "Shape", "Shp");

            static void SetCaption(Button button, string full, string shortText)
            {
                var caption = UiLocalization.T(full);
                var required = TextRenderer.MeasureText(caption, button.Font).Width
                    + button.Padding.Horizontal + 8;
                var text = required <= button.ClientSize.Width ? caption
                    : UiLocalization.T(shortText);
                if (button.Text != text) button.Text = text;
                button.AccessibleName = caption;
            }
        }
        modes.Layout += (_, _) => UpdateGradientModeLabels();
        UpdateGradientModeLabels();

        ConfigureGradientModeButton(_solidFillMode, GradientKind.Solid, "Use a solid fill color");
        ConfigureGradientModeButton(_linearGradientMode, GradientKind.Linear, "Use a linear fill gradient");
        ConfigureGradientModeButton(_radialGradientMode, GradientKind.Radial, "Use a radial fill gradient");
        ConfigureGradientModeButton(_shapeRadialGradientMode, GradientKind.ShapeRadial, "Use a shape radial fill gradient");
        _solidFillMode.Margin = new Padding(0, 3, 1, 3);
        _linearGradientMode.Margin = new Padding(1, 3, 1, 3);
        _radialGradientMode.Margin = new Padding(1, 3, 1, 3);
        _shapeRadialGradientMode.Margin = new Padding(1, 3, 0, 3);
        modes.Controls.Add(_solidFillMode, 0, 0);
        modes.Controls.Add(_linearGradientMode, 1, 0);
        modes.Controls.Add(_radialGradientMode, 2, 0);
        modes.Controls.Add(_shapeRadialGradientMode, 3, 0);
        _paletteButton.Margin = new Padding(2, 0, 0, 0);
        modes.Controls.Add(_paletteButton, 4, 0);

        grid.Controls.Add(CreateFieldLabel("Stops"), 0, 1);
        _gradientStopStrip.Dock = DockStyle.Fill;
        _gradientStopStrip.Margin = new Padding(0, 2, 0, 2);
        _gradientStopStrip.AccessibleName = "Gradient color stops";
        _gradientStopStrip.StopSelected += (_, e) => SelectGradientStop(e.Index);
        _gradientStopStrip.StopsChanged += (_, e) => SetGradientStopsFromStrip(e.Stops, e.SelectedIndex);
        _gradientStopStrip.InteractionStarted += (_, _) => BeginGradientContinuousEdit();
        _gradientStopStrip.InteractionCompleted += (_, _) => CompleteGradientContinuousEdit();
        _gradientStopStrip.InteractionCanceled += (_, _) => CancelGradientContinuousEdit();
        grid.Controls.Add(_gradientStopStrip, 1, 1);
        grid.SetColumnSpan(_gradientStopStrip, 3);

        var selectedStop = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty
        };
        selectedStop.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        selectedStop.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62));
        selectedStop.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
        grid.Controls.Add(selectedStop, 0, 2);
        grid.SetColumnSpan(selectedStop, 6);
        _gradientStopTarget.Dock = DockStyle.Fill;
        _gradientStopTarget.Margin = new Padding(0, 3, 0, 3);
        _gradientStopTarget.Click += (_, _) =>
        {
            RefreshColorEditorLayout();
            SelectGradientStop(_selectedGradientStop);
        };
        _gradientStopTarget.AccessibleName = "Selected gradient stop color";
        _toolTip.SetToolTip(_gradientStopTarget, "Edit the selected gradient stop color");
        selectedStop.Controls.Add(_gradientStopTarget, 0, 0);

        var positionLabel = CreateFieldLabel("Position");
        positionLabel.TextAlign = ContentAlignment.MiddleCenter;
        selectedStop.Controls.Add(positionLabel, 1, 0);
        _gradientStopPosition.Suffix = "%";
        _gradientStopPosition.ShowSuffix = true;
        _gradientStopPosition.AccessibleName = "Gradient stop position";
        _gradientStopPosition.Minimum = 0;
        _gradientStopPosition.Maximum = 100;
        _gradientStopPosition.DecimalPlaces = 0;
        _gradientStopPosition.Increment = 1;
        _gradientStopPosition.Dock = DockStyle.Fill;
        _gradientStopPosition.Margin = new Padding(0, 3, 2, 3);
        Theme.StyleNumeric(_gradientStopPosition);
        _gradientStopPosition.ValueChanged += (_, _) => UpdateGradientStopPosition();
        _gradientStopPosition.InteractionStarted += (_, _) => BeginGradientContinuousEdit();
        _gradientStopPosition.InteractionCompleted += (_, _) => CompleteGradientContinuousEdit();
        _gradientStopPosition.InteractionCanceled += (_, _) => CancelGradientContinuousEdit();
        selectedStop.Controls.Add(_gradientStopPosition, 2, 0);
        ConfigureGradientStopButton(_addGradientStop, "Add a color stop after the selected stop", AddGradientStop);
        ConfigureGradientStopButton(_removeGradientStop, "Remove the selected color stop", RemoveGradientStop);
        _addGradientStop.Margin = new Padding(2, 3, 2, 3);
        _removeGradientStop.Margin = new Padding(2, 3, 0, 3);
        grid.Controls.Add(_addGradientStop, 4, 1);
        grid.Controls.Add(_removeGradientStop, 5, 1);
        RefreshGradientPresentation();
    }

    private void ConfigureGradientModeButton(Button button, GradientKind kind, string toolTip)
    {
        if (button is SegmentedButton segmented) segmented.ShowSelectionIndicator = true;
        button.Dock = DockStyle.Fill;
        button.AccessibleName = kind switch
        {
            GradientKind.Linear => "Linear gradient fill",
            GradientKind.Radial => "Radial gradient fill",
            GradientKind.ShapeRadial => "Shape radial gradient fill",
            _ => "Solid fill"
        };
        button.AccessibleRole = AccessibleRole.RadioButton;
        Theme.StyleSegmentedButton(button);
        _toolTip.SetToolTip(button, toolTip);
        button.Click += (_, _) => SetGradientKind(kind);
    }

    private void ConfigureGradientStopButton(Button button, string toolTip, Action action)
    {
        button.Dock = DockStyle.Fill;
        button.AccessibleName = toolTip;
        Theme.StyleToolbarButton(button);
        _toolTip.SetToolTip(button, toolTip);
        button.Click += (_, _) => action();
    }

    internal void SetGradientKind(GradientKind kind)
    {
        if (kind == GradientKind.ShapeRadial && _gradientPreviewOnStroke) return;
        if (_gradientKind == kind) return;
        _gradientKind = kind;
        if (kind == GradientKind.Solid)
        {
            _editingGradientTarget = GradientColorTarget.None;
        }
        else
        {
            if (GradientStart.ToArgb() == GradientEnd.ToArgb())
            {
                var activePaint = _gradientPreviewOnStroke ? _stroke : _fill;
                var secondaryPaint = _stroke;
                _gradientStops =
                [
                    new GradientStop(0, activePaint),
                    new GradientStop(1, secondaryPaint)
                ];
            }
            _selectedGradientStop = 0;
            _editingGradientTarget = GradientColorTarget.Stop;
        }

        RefreshGradientPresentation();
        RaiseGradientChanged();
    }

    private void ApplyGradientPreset(GradientPreset preset)
    {
        if (_updating) return;
        if (preset.Kind == GradientKind.ShapeRadial && _gradientPreviewOnStroke) return;
        _gradientKind = preset.Kind;
        _gradientStops = NormalizeGradientStops(preset.Stops);
        _selectedGradientStop = 0;
        _editingGradientTarget = GradientColorTarget.Stop;
        RefreshGradientPresentation();
        UpdateTargetPresentation();
        UpdateEditorFromColor();
        RaiseGradientChanged();
        ClosePalettePopup();
    }

    private void AddGradientStop()
    {
        if (_gradientKind == GradientKind.Solid) _gradientKind = GradientKind.Linear;
        var before = Math.Clamp(_selectedGradientStop, 0, _gradientStops.Length - 2);
        var start = _gradientStops[before];
        var end = _gradientStops[before + 1];
        var position = (start.Position + end.Position) * 0.5f;
        var stops = _gradientStops.ToList();
        stops.Insert(before + 1, new GradientStop(position, Lerp(Color.FromArgb(start.Argb), Color.FromArgb(end.Argb), 0.5f)));
        _gradientStops = stops.ToArray();
        _selectedGradientStop = before + 1;
        RefreshGradientPresentation();
        RaiseGradientChanged();
    }

    private void RemoveGradientStop()
    {
        if (_selectedGradientStop <= 0 || _selectedGradientStop >= _gradientStops.Length - 1) return;
        _gradientStops = _gradientStops.Where((_, index) => index != _selectedGradientStop).ToArray();
        _selectedGradientStop = Math.Clamp(_selectedGradientStop - 1, 0, _gradientStops.Length - 1);
        RefreshGradientPresentation();
        UpdateEditorFromColor();
        RaiseGradientChanged();
    }

    private void UpdateGradientStopPosition()
    {
        if (_updating || _selectedGradientStop <= 0 || _selectedGradientStop >= _gradientStops.Length - 1) return;
        var previous = _gradientStops[_selectedGradientStop - 1].Position + 0.01f;
        var next = _gradientStops[_selectedGradientStop + 1].Position - 0.01f;
        var position = Math.Clamp((float)_gradientStopPosition.Value / 100f, previous, next);
        if (Math.Abs(_gradientStops[_selectedGradientStop].Position - position) < 0.0001f) return;
        _gradientStops[_selectedGradientStop] = new GradientStop(position, _gradientStops[_selectedGradientStop].Argb);
        RefreshGradientPresentation();
        RaiseGradientChanged();
    }

    private void SelectGradientStop(int index)
    {
        var next = Math.Clamp(index, 0, _gradientStops.Length - 1);
        var changed = _selectedGradientStop != next || _editingGradientTarget != GradientColorTarget.Stop;
        _selectedGradientStop = next;
        _editingGradientTarget = GradientColorTarget.Stop;
        UpdateTargetPresentation();
        RefreshGradientPresentation();
        if (changed) UpdateEditorFromColor();
    }

    private void RefreshColorEditorLayout()
    {
        if (_content is null) return;
        const bool expanded = true;
        _content.SuspendLayout();
        try
        {
            _content.RowStyles[5].Height = 0;
            _content.RowStyles[6].Height = expanded ? InspectorHeight(232) : 0;
            _content.RowStyles[9].Height = expanded ? InspectorHeight(80) : 0;
            _content.RowStyles[10].Height = expanded ? InspectorHeight(44) : 0;
            _content.RowStyles[11].Height = expanded && _channelsExpanded ? InspectorHeight(100) : 0;
            _content.RowStyles[13].Height = expanded ? InspectorHeight(40) : 0;
            _content.RowStyles[14].Height = expanded && _harmonyExpanded
                ? _harmonyPanel.RowStyles[0].Height + InspectorHeight(48) : 0;
            foreach (Control control in _content.Controls)
            {
                var row = _content.GetRow(control);
                if (row is 6 or 9 or 10 or 13) control.Visible = expanded;
                if (row == 12) control.Visible = expanded && RecentPalette.Count > 0;
                if (row == 11) control.Visible = expanded && _channelsExpanded;
                if (row == 14) control.Visible = expanded && _harmonyExpanded;
            }
            UpdateInlineRecentLayout();
        }
        finally
        {
            _content.ResumeLayout(performLayout: true);
        }
        UpdatePanelHeight();
    }

    private float InspectorHeight(int logicalHeight)
    {
        // The title row is scaled by WinForms and never collapsed. Use its ratio
        // so visibility changes preserve the current DPI instead of restoring 96-DPI rows.
        var scale = _content is null ? 1f
            : _content.RowStyles[0].Height / (InspectorModuleStyle.PageHeaderHeight + 12);
        return logicalHeight * scale;
    }

    private void UpdatePanelHeight()
    {
        if (_content is null) return;
        var contentHeight = 0f;
        foreach (RowStyle style in _content.RowStyles)
        {
            if (style.SizeType == SizeType.Absolute) contentHeight += style.Height;
        }
        Height = Math.Max(1, (int)Math.Ceiling(contentHeight) + Padding.Vertical);
    }

    private void SetGradientStopsFromStrip(GradientStop[] stops, int selectedIndex)
    {
        if (_updating) return;
        _gradientStops = NormalizeGradientStops(stops);
        _selectedGradientStop = Math.Clamp(selectedIndex, 0, _gradientStops.Length - 1);
        _editingGradientTarget = GradientColorTarget.Stop;
        RefreshGradientPresentation();
        UpdateEditorFromColor();
        RaiseGradientChanged();
    }

    private void RefreshGradientPresentation()
    {
        _selectedGradientStop = Math.Clamp(_selectedGradientStop, 0, _gradientStops.Length - 1);
        var stop = _gradientStops[_selectedGradientStop];
        var enabled = _gradientKind != GradientKind.Solid;
        UpdateGradientSettingsVisibility(enabled);
        RefreshGradientTargetPreview();
        _gradientPresetGrid.SetSelection(_gradientKind, _gradientStops);
        _gradientStopStrip.SetStops(_gradientStops, _selectedGradientStop);
        _gradientStopStrip.Enabled = enabled;
        _gradientStopTarget.SwatchColor = Color.FromArgb(stop.Argb);
        _gradientStopTarget.DetailText = string.Empty;
        _gradientStopTarget.Selected = _editingGradientTarget == GradientColorTarget.Stop;
        _gradientStopTarget.Enabled = enabled;
        var movable = enabled && _selectedGradientStop > 0 && _selectedGradientStop < _gradientStops.Length - 1;
        _gradientStopPosition.Enabled = movable;
        _gradientStopPosition.Value = (decimal)Math.Clamp(MathF.Round(stop.Position * 100f), 0f, 100f);
        _addGradientStop.Enabled = enabled;
        _removeGradientStop.Enabled = movable;
        _shapeRadialGradientMode.Enabled = !_gradientPreviewOnStroke;
        Theme.StyleSegmentedButton(_solidFillMode, _gradientKind == GradientKind.Solid);
        Theme.StyleSegmentedButton(_linearGradientMode, _gradientKind == GradientKind.Linear);
        Theme.StyleSegmentedButton(_radialGradientMode, _gradientKind == GradientKind.Radial);
        Theme.StyleSegmentedButton(_shapeRadialGradientMode, _gradientKind == GradientKind.ShapeRadial);
        RefreshGradientPresetActions();
    }

    private void UpdateGradientSettingsVisibility(bool expanded)
    {
        var grid = _gradientSettingsGrid;
        if (grid is null || _content is null || _gradientSettingsExpanded == expanded) return;

        _gradientSettingsExpanded = expanded;
        _content.SuspendLayout();
        grid.SuspendLayout();
        try
        {
            grid.RowStyles[1].Height = expanded ? InspectorHeight(46) : 0;
            grid.RowStyles[2].Height = expanded ? InspectorHeight(46) : 0;
            foreach (Control control in grid.Controls)
            {
                if (grid.GetRow(control) > 0) control.Visible = expanded;
            }

            _content.RowStyles[2].Height = _textObjectMode ? 0 : InspectorHeight(expanded ? 132 : 40);
        }
        finally
        {
            grid.ResumeLayout(performLayout: true);
            _content.ResumeLayout(performLayout: true);
        }

        UpdatePanelHeight();
    }

    private static GradientStop[] NormalizeGradientStops(IReadOnlyList<GradientStop> stops)
    {
        var normalized = stops
            .Select(stop => new GradientStop(Math.Clamp(stop.Position, 0f, 1f), stop.Argb))
            .OrderBy(stop => stop.Position)
            .GroupBy(stop => stop.Position)
            .Select(group => group.Last())
            .ToList();
        if (normalized.Count == 0) normalized.Add(new GradientStop(0, Color.White));
        if (normalized.Count == 1) normalized.Add(new GradientStop(1, normalized[0].Argb));
        if (normalized[0].Position > 0) normalized.Insert(0, new GradientStop(0, normalized[0].Argb));
        if (normalized[^1].Position < 1) normalized.Add(new GradientStop(1, normalized[^1].Argb));
        return normalized.ToArray();
    }

    private void StoreActiveGradient()
    {
        var state = new GradientPaintState(_gradientKind, _gradientStops.ToArray());
        if (_gradientPreviewOnStroke) _strokeGradient = state;
        else _fillGradient = state;
    }

    private void LoadGradientForTarget(bool strokeTarget)
    {
        var state = strokeTarget ? _strokeGradient : _fillGradient;
        _gradientKind = state.Kind;
        _gradientStops = state.Stops.ToArray();
        _selectedGradientStop = Math.Clamp(_selectedGradientStop, 0, _gradientStops.Length - 1);
        _editingGradientTarget = _gradientKind == GradientKind.Solid
            ? GradientColorTarget.None
            : GradientColorTarget.Stop;
    }

    private void RaiseGradientChanged()
    {
        StoreActiveGradient();
        if (_selectedSavedGradientPreset is not null && !GradientMatches(_selectedSavedGradientPreset, _gradientKind, _gradientStops)) _selectedSavedGradientPreset = null;
        if (_colorInteractionDepth == 0 && _continuousControlInteractionDepth == 0) AddRecentGradient(_gradientKind, _gradientStops);
        GradientChanged?.Invoke(this, new GradientChangedEventArgs(_gradientKind, _gradientStops));
    }

    private static Color Lerp(Color start, Color end, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        return Color.FromArgb(
            (int)MathF.Round(start.A + (end.A - start.A) * amount),
            (int)MathF.Round(start.R + (end.R - start.R) * amount),
            (int)MathF.Round(start.G + (end.G - start.G) * amount),
            (int)MathF.Round(start.B + (end.B - start.B) * amount));
    }

    private void BuildLineEndpointStyle(TableLayoutPanel content)
    {
        _lineEndpointStylePanel.Dock = DockStyle.Fill;
        _lineEndpointStylePanel.BackColor = Theme.Panel;
        _lineEndpointStylePanel.Margin = Padding.Empty;
        content.Controls.Add(_lineEndpointStylePanel, 0, 8);

        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 3,
            RowCount = 2,
            Margin = Padding.Empty
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        row.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.InspectorRowHeight));
        row.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.InspectorRowHeight));
        _lineEndpointStylePanel.Controls.Add(row);

        row.Controls.Add(CreateFieldLabel("Start join"), 0, 0);
        ConfigureLineEndpointStyleButton(_startSharpEndpointStyle, LineEndpointStyle.Sharp, startEndpoint: true, "Mitered start connection");
        ConfigureLineEndpointStyleButton(_startRoundEndpointStyle, LineEndpointStyle.Round, startEndpoint: true, "Rounded start connection");
        _startSharpEndpointStyle.Margin = new Padding(0, 4, 2, 4);
        _startRoundEndpointStyle.Margin = new Padding(2, 4, 0, 4);
        row.Controls.Add(_startSharpEndpointStyle, 1, 0);
        row.Controls.Add(_startRoundEndpointStyle, 2, 0);

        row.Controls.Add(CreateFieldLabel("End join"), 0, 1);
        ConfigureLineEndpointStyleButton(_endSharpEndpointStyle, LineEndpointStyle.Sharp, startEndpoint: false, "Mitered end connection");
        ConfigureLineEndpointStyleButton(_endRoundEndpointStyle, LineEndpointStyle.Round, startEndpoint: false, "Rounded end connection");
        _endSharpEndpointStyle.Margin = new Padding(0, 4, 2, 4);
        _endRoundEndpointStyle.Margin = new Padding(2, 4, 0, 4);
        row.Controls.Add(_endSharpEndpointStyle, 1, 1);
        row.Controls.Add(_endRoundEndpointStyle, 2, 1);
    }

    private void ConfigureLineEndpointStyleButton(
        Button button,
        LineEndpointStyle endpointStyle,
        bool startEndpoint,
        string toolTip)
    {
        button.Dock = DockStyle.Fill;
        button.AccessibleName = $"{button.Text} line endpoints";
        button.AccessibleRole = AccessibleRole.RadioButton;
        Theme.StyleSegmentedButton(button);
        _toolTip.SetToolTip(button, toolTip);
        button.Click += (_, _) => SelectLineEndpointStyle(endpointStyle, startEndpoint);
    }

    private void SelectLineEndpointStyle(LineEndpointStyle endpointStyle, bool startEndpoint)
    {
        if (!_lineEndpointStyleVisible) return;
        var normalized = endpointStyle == LineEndpointStyle.Sharp
            ? LineEndpointStyle.Sharp
            : LineEndpointStyle.Round;
        if (startEndpoint) _startEndpointStyle = normalized;
        else _endEndpointStyle = normalized;
        RefreshLineEndpointStyleButtons();
        LineEndpointStyleChanged?.Invoke(this, new LineEndpointStyleChangedEventArgs(normalized, startEndpoint));
    }

    private void RefreshLineEndpointStyleButtons()
    {
        RefreshLineEndpointStyleButtons(_startEndpointStyle, _startSharpEndpointStyle, _startRoundEndpointStyle);
        RefreshLineEndpointStyleButtons(_endEndpointStyle, _endSharpEndpointStyle, _endRoundEndpointStyle);
    }

    private static void RefreshLineEndpointStyleButtons(LineEndpointStyle endpointStyle, Button sharp, Button round)
    {
        Theme.StyleSegmentedButton(sharp, endpointStyle == LineEndpointStyle.Sharp);
        Theme.StyleSegmentedButton(round, endpointStyle != LineEndpointStyle.Sharp);
    }

    private void ApplyMaterial(
        Color fill,
        Color stroke,
        float strokeWidth,
        float opacity,
        bool raiseEvent,
        bool isHostEcho = false)
    {
        // A host echo mirrors the scene back into the panel while our own edit is still being
        // applied, and that echo still carries the pre-edit color. Adopting it would silently
        // revert the edit, so an echo never replaces the target the user is editing. Explicit
        // setter calls are real edits and always win. Gradient stops are raised through
        // GradientChanged and are never replaced from here.
        var colorEditActive = _handlingColorChange || _colorInteractionDepth > 0;
        if (isHostEcho
            && colorEditActive
            && _editingGradientTarget != GradientColorTarget.Stop)
        {
            if (_editingFill) fill = _fill;
            else stroke = _stroke;
        }

        var fillChanged = _fill.ToArgb() != fill.ToArgb();
        var strokeChanged = _stroke.ToArgb() != stroke.ToArgb();
        var strokeWidthChanged = Math.Abs(StrokeWidth - strokeWidth) > 0.001f;
        var opacityChanged = Math.Abs(Opacity - opacity) > 0.001f;
        if (!raiseEvent && !fillChanged && !strokeChanged && !strokeWidthChanged && !opacityChanged) return;
        var refreshComponents = !colorEditActive;

        _updating = true;
        _fill = fill;
        _stroke = stroke;
        _strokeWidth.Value = (decimal)Math.Clamp(strokeWidth, (float)_strokeWidth.Minimum, (float)_strokeWidth.Maximum);
        _opacity = Math.Clamp(opacity, 0f, 1f);
        UpdateTargetPresentation();
        if (refreshComponents) UpdateEditorFromColor();
        else UpdateColorSelectionIndicators();
        _updating = false;

        if (raiseEvent) RaiseMaterialChanged(fillChanged, strokeChanged, strokeWidthChanged, opacityChanged);
    }

    private void SetUniformAlpha(float opacity)
    {
        if (_updating) return;
        var alpha = (int)Math.Clamp(MathF.Round(opacity * 255f), 0f, 255f);
        if (_gradientKind != GradientKind.Solid)
        {
            _updating = true;
            _fill = Color.FromArgb(alpha, _fill);
            _stroke = Color.FromArgb(alpha, _stroke);
            _gradientStops = _gradientStops
                .Select(stop => new GradientStop(stop.Position, Color.FromArgb(alpha, Color.FromArgb(stop.Argb)).ToArgb()))
                .ToArray();
            _opacity = Math.Clamp(opacity, 0f, 1f);
            UpdateTargetPresentation();
            if (!_handlingColorChange && _colorInteractionDepth == 0) UpdateEditorFromColor();
            _updating = false;
            RaiseGradientChanged();
            return;
        }

        ApplyMaterial(
            Color.FromArgb(alpha, _fill),
            Color.FromArgb(alpha, _stroke),
            StrokeWidth,
            alpha / 255f,
            raiseEvent: true);
    }

    private void RaiseMaterialChanged(
        bool fillChanged = false,
        bool strokeChanged = false,
        bool strokeWidthChanged = false,
        bool opacityChanged = false,
        bool applyAll = false)
    {
        if (_updating) return;

        UpdateTargetPresentation();
        MaterialChanged?.Invoke(this, new MaterialChangedEventArgs(
            _fill,
            _stroke,
            StrokeWidth,
            Opacity,
            fillChanged,
            strokeChanged,
            strokeWidthChanged,
            opacityChanged,
            applyAll));
        if (fillChanged) FillChanged?.Invoke(this, EventArgs.Empty);
        if (strokeChanged || strokeWidthChanged) StrokeChanged?.Invoke(this, EventArgs.Empty);
        if (opacityChanged) OpacityChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SelectTarget(bool fill)
    {
        RefreshColorEditorLayout();
        var gradientPreviewOnStroke = !fill;
        if (_editingGradientTarget == GradientColorTarget.None
            && _editingFill == fill
            && _gradientPreviewOnStroke == gradientPreviewOnStroke)
        {
            return;
        }

        StoreActiveGradient();
        _editingGradientTarget = GradientColorTarget.None;
        _editingFill = fill;
        _gradientPreviewOnStroke = gradientPreviewOnStroke;
        LoadGradientForTarget(gradientPreviewOnStroke);
        _editingGradientTarget = GradientColorTarget.None;
        RefreshGradientPresentation();
        UpdateTargetPresentation();
        UpdateEditorFromColor();
        EditingTargetChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetColorMode(ColorMode mode)
    {
        if (_colorMode == mode && _modeButtons.Count > 0)
        {
            RefreshModeButtons();
            return;
        }

        _colorMode = mode;
        RefreshModeButtons();
        _channelsPanel.Visible = _channelsExpanded;
        _channelsPanel.BringToFront();
        ConfigureChannelMode();
        UpdateEditorFromColor();
    }

    private void RefreshModeButtons()
    {
        foreach (var (mode, button) in _modeButtons)
        {
            Theme.StyleSegmentedButton(button, mode == _colorMode);
        }
    }

    private void ConfigureChannelMode()
    {
        var labels = _colorMode switch
        {
            ColorMode.Hsv => new[] { "Hue", "Saturation", "Value" },
            ColorMode.Hsl => new[] { "Hue", "Saturation", "Lightness" },
            ColorMode.Lab => new[] { "L*", "a*", "b*" },
            _ => new[] { "Red", "Green", "Blue" }
        };
        var accessibleNames = _colorMode == ColorMode.Lab
            ? new[] { "CIELAB lightness", "CIELAB green-red axis", "CIELAB blue-yellow axis" }
            : labels.Select(label => $"{_colorMode} {label}").ToArray();
        var descriptions = _colorMode == ColorMode.Lab
            ? new[] { "Adjust CIELAB lightness", "Adjust CIELAB green-red axis", "Adjust CIELAB blue-yellow axis" }
            : labels.Select(label => $"Adjust {label.ToLowerInvariant()}").ToArray();
        var minima = _colorMode == ColorMode.Lab
            ? new[] { 0, -128, -128 }
            : new[] { 0, 0, 0 };
        var maxima = _colorMode switch
        {
            ColorMode.Hsv or ColorMode.Hsl => new[] { 360, 100, 100 },
            ColorMode.Lab => new[] { 100, 127, 127 },
            _ => new[] { 255, 255, 255 }
        };

        _updatingComponents = true;
        for (var i = 0; i < 3; i++)
        {
            _channelLabels[i].Text = _colorMode == ColorMode.Rgb ? new[] { "R", "G", "B" }[i]
                : UiLocalization.T(_colorMode == ColorMode.Hsv && i == 2 ? "HSV value" : labels[i]);
            _channelLabels[i].AccessibleName = UiLocalization.T(_colorMode == ColorMode.Lab ? accessibleNames[i] : labels[i]);
            _channelSliders[i].AccessibleName = UiLocalization.T(accessibleNames[i]);
            _channelValues[i].AccessibleName = UiLocalization.T(accessibleNames[i]);
            _toolTip.SetToolTip(_channelLabels[i], UiLocalization.T(_colorMode == ColorMode.Lab ? accessibleNames[i] : labels[i]));
            _toolTip.SetToolTip(_channelSliders[i], UiLocalization.T(descriptions[i]));
            var sliderScale = _colorMode == ColorMode.Lab ? 10 : 1;
            _channelSliders[i].Minimum = minima[i] * sliderScale;
            _channelSliders[i].Maximum = maxima[i] * sliderScale;
            _channelValues[i].Minimum = minima[i];
            _channelValues[i].Maximum = maxima[i];
            _channelValues[i].DecimalPlaces = _colorMode == ColorMode.Lab ? 1 : 0;
            _channelValues[i].Increment = _colorMode == ColorMode.Lab ? 0.1m : 1m;
            _channelValues[i].Suffix = _colorMode is ColorMode.Hsv or ColorMode.Hsl
                ? i == 0 ? "degrees" : "percent"
                : string.Empty;
        }
        _channelLabels[3].Text = UiLocalization.T("Opacity");
        _channelLabels[3].AccessibleName = UiLocalization.T("Alpha");
        _channelSliders[3].AccessibleName = UiLocalization.T("Alpha");
        _toolTip.SetToolTip(_channelLabels[3], UiLocalization.T("Alpha"));
        _toolTip.SetToolTip(_channelSliders[3], UiLocalization.T("Adjust alpha"));
        _channelSliders[3].Minimum = 0;
        _channelSliders[3].Maximum = 255;
        _channelValues[3].AccessibleName = UiLocalization.T("Alpha");
        _channelValues[3].Minimum = 0;
        _channelValues[3].Maximum = 100;
        _channelValues[3].DecimalPlaces = 1;
        _channelValues[3].Increment = 1;
        _channelValues[3].Suffix = "%";
        _channelValues[3].ShowSuffix = true;
        ConfigureTraditionalPickerMode(labels);
        _updatingComponents = false;
    }

    private void UpdateEditorFromColor(
        bool refreshTraditionalPicker = true,
        bool refreshSelectionIndicators = true)
    {
        var color = EditedColor;
        _updatingComponents = true;
        switch (_colorMode)
        {
            case ColorMode.Hsv:
                RgbToHsv(color, out var hsvHue, out var saturation, out var value);
                SetChannelValues(
                    (int)Math.Round(hsvHue),
                    (int)Math.Round(saturation * 100),
                    (int)Math.Round(value * 100),
                    color.A);
                break;
            case ColorMode.Hsl:
                SetChannelValues(
                    (int)Math.Round(color.GetHue()),
                    (int)Math.Round(color.GetSaturation() * 100),
                    (int)Math.Round(color.GetBrightness() * 100),
                    color.A);
                break;
            case ColorMode.Lab:
                RgbToLab(color, out var lightness, out var greenRed, out var blueYellow);
                SetLabChannelValues(lightness, greenRed, blueYellow, color.A);
                break;
            default:
                SetChannelValues(color.R, color.G, color.B, color.A);
                break;
        }
        _hexText.Text = DisplayHex(color);
        SetHexInvalid(false);
        _updatingComponents = false;
        if (refreshTraditionalPicker) UpdateTraditionalPicker();
        RefreshHarmonyPalette();
        RefreshChannelGradients();
        if (refreshSelectionIndicators) UpdateColorSelectionIndicators();
    }

    private void SetChannelValues(int first, int second, int third, int alpha)
    {
        var values = new[] { first, second, third, alpha };
        for (var i = 0; i < 4; i++)
        {
            var next = Math.Clamp(values[i], _channelSliders[i].Minimum, _channelSliders[i].Maximum);
            _channelSliders[i].Value = next;
            _channelValues[i].Value = i == 3 ? AlphaPercent(next) : next;
        }
    }

    private void SetLabChannelValues(double lightness, double greenRed, double blueYellow, int alpha)
    {
        var values = new[] { lightness, greenRed, blueYellow };
        for (var i = 0; i < values.Length; i++)
        {
            var scaled = (int)Math.Round(values[i] * 10, MidpointRounding.AwayFromZero);
            _channelSliders[i].Value = Math.Clamp(scaled, _channelSliders[i].Minimum, _channelSliders[i].Maximum);
            _channelValues[i].Value = Math.Clamp(
                decimal.Round((decimal)values[i], 1, MidpointRounding.AwayFromZero),
                _channelValues[i].Minimum,
                _channelValues[i].Maximum);
        }
        _channelSliders[3].Value = alpha;
        _channelValues[3].Value = AlphaPercent(alpha);
    }

    private void ChannelSliderChanged(int index)
    {
        if (_updatingComponents) return;
        _updatingComponents = true;
        _channelValues[index].Value = index == 3 ? AlphaPercent(_channelSliders[index].Value)
            : _colorMode == ColorMode.Lab ? _channelSliders[index].Value / 10m
            : _channelSliders[index].Value;
        _updatingComponents = false;
        ApplyColorComponents(index);
    }

    private void ChannelNumericChanged(int index)
    {
        if (_updatingComponents) return;
        _updatingComponents = true;
        _channelSliders[index].Value = index == 3
            ? (int)Math.Round(_channelValues[index].Value * 255m / 100m, MidpointRounding.AwayFromZero)
            : _colorMode == ColorMode.Lab
                ? (int)Math.Round((double)_channelValues[index].Value * 10, MidpointRounding.AwayFromZero)
                : (int)_channelValues[index].Value;
        _updatingComponents = false;
        ApplyColorComponents(index);
    }

    private void ApplyColorComponents(int changedIndex)
    {
        var alpha = _channelSliders[3].Value;
        var first = _channelSliders[0].Value;
        var second = _channelSliders[1].Value;
        var third = _channelSliders[2].Value;
        var color = changedIndex == 3
            ? Color.FromArgb(alpha, EditedColor.R, EditedColor.G, EditedColor.B)
            : ColorFromComponentValues(first, second, third, alpha);
        SetEditedColor(color, addRecentForDiscreteEdit: true);
        UpdateTraditionalPicker(changedIndex);
        RefreshHarmonyPalette();
        RefreshChannelGradients();
    }

    private void TraditionalPlaneChanged()
    {
        if (_updatingComponents) return;
        _updatingComponents = true;
        SetComponentFromAmount(1, _traditionalPlane.XValue);
        SetComponentFromAmount(2, _traditionalPlane.YValue);
        _updatingComponents = false;
        ApplyColorComponents(1);
    }

    private void TraditionalPrimaryChanged()
    {
        if (_updatingComponents) return;
        _updatingComponents = true;
        SetComponentFromAmount(0, _primaryComponent.Value);
        _updatingComponents = false;
        ApplyColorComponents(0);
    }

    private void SetComponentFromAmount(int index, float amount)
    {
        var slider = _channelSliders[index];
        var value = slider.Minimum + (int)Math.Round(
            (slider.Maximum - slider.Minimum) * Math.Clamp(amount, 0f, 1f),
            MidpointRounding.AwayFromZero);
        slider.Value = value;
        _channelValues[index].Value = _colorMode == ColorMode.Lab
            ? value / 10m
            : value;
    }

    private float ComponentAmount(int index)
    {
        var slider = _channelSliders[index];
        return slider.Maximum <= slider.Minimum
            ? 0f
            : (slider.Value - slider.Minimum) / (float)(slider.Maximum - slider.Minimum);
    }

    private int ComponentValue(int index, float amount)
    {
        var slider = _channelSliders[index];
        return slider.Minimum + (int)Math.Round(
            (slider.Maximum - slider.Minimum) * Math.Clamp(amount, 0f, 1f),
            MidpointRounding.AwayFromZero);
    }

    private Color TraditionalPlaneColor(float horizontal, float vertical)
    {
        return ColorFromComponentValues(
            _channelSliders[0].Value,
            ComponentValue(1, horizontal),
            ComponentValue(2, vertical),
            255);
    }

    private Color TraditionalPrimaryGradient(float amount)
    {
        return _colorMode switch
        {
            ColorMode.Hsv => ColorFromHsv(amount * 360, 1, 1, 255),
            ColorMode.Hsl => ColorFromHsl(amount * 360, 1, 0.5, 255),
            _ => ColorFromComponentValues(
                ComponentValue(0, amount),
                _channelSliders[1].Value,
                _channelSliders[2].Value,
                255)
        };
    }

    private Color ColorFromComponentValues(int first, int second, int third, int alpha)
    {
        return _colorMode switch
        {
            ColorMode.Hsv => ColorFromHsv(first, second / 100d, third / 100d, alpha),
            ColorMode.Hsl => ColorFromHsl(first, second / 100d, third / 100d, alpha),
            ColorMode.Lab => ColorFromLab(first / 10d, second / 10d, third / 10d, alpha),
            _ => Color.FromArgb(alpha, first, second, third)
        };
    }

    private void UpdateTraditionalPicker(int changedIndex = -1)
    {
        _traditionalPlane.SetValues(ComponentAmount(1), ComponentAmount(2));
        _primaryComponent.SetValue(ComponentAmount(0));
        var wasUpdating = _updatingComponents;
        _updatingComponents = true;
        try { _hueComponent.Value = (int)Math.Round(ComponentAmount(0) * 360); }
        finally { _updatingComponents = wasUpdating; }
        if (changedIndex is -1 or 0) _traditionalPlane.RefreshGradient();
        if (changedIndex is -1 or 1 or 2) _primaryComponent.RefreshGradient();
        if (changedIndex is -1 or 1 or 2) _hueComponent.RefreshGradient();
    }

    private void ConfigureTraditionalPickerMode(string[] labels)
    {
        var modeName = _colorMode switch
        {
            ColorMode.Hsv => "HSV",
            ColorMode.Hsl => "HSL",
            ColorMode.Lab => "Lab",
            _ => "RGB"
        };
        _traditionalPlane.HorizontalAxisName = UiLocalization.T(labels[1]);
        _traditionalPlane.VerticalAxisName = UiLocalization.T(labels[2]);
        _traditionalPlane.AccessibleName = UiLocalization.T($"{modeName} color field");
        _primaryComponent.AxisName = UiLocalization.T(labels[0]);
        _primaryComponent.AccessibleName = UiLocalization.T("Primary color component");
        _primaryComponent.HighAtTop = _colorMode is ColorMode.Rgb or ColorMode.Lab;
        if (_pickerLayout is not null)
        {
            var horizontalHue = _colorMode is ColorMode.Hsv or ColorMode.Hsl;
            _primaryComponent.Visible = !horizontalHue;
            _hueComponent.Visible = horizontalHue;
            _pickerLayout.ColumnStyles[1].Width = horizontalHue ? 0 : InspectorHeight(24);
            _pickerLayout.RowStyles[1].Height = horizontalHue ? InspectorHeight(36) : 0;
        }
        _toolTip.SetToolTip(
            _traditionalPlane,
            $"{UiLocalization.T(labels[1])} / {UiLocalization.T(labels[2])}");
        _toolTip.SetToolTip(_primaryComponent, UiLocalization.T(labels[0]));
    }

    private void HarmonyRuleChanged()
    {
        if (_updatingComponents) return;
        RefreshHarmonyPalette();
    }

    private void RefreshHarmonyPalette()
    {
        var index = Math.Clamp(_harmonyRule.SelectedIndex, 0, HarmonyRules.Length - 1);
        _harmonyGrid.SetColors(
            HarmonyColorWheel.CreateHarmonyColors(EditedColor, HarmonyRules[index].Mode).Select(Opaque));
        _harmonyGrid.SelectedColor = Opaque(EditedColor);
    }

    private bool SetEditedColor(Color color, bool addRecentForDiscreteEdit)
    {
        var current = EditedColor;
        if (current.ToArgb() == color.ToArgb())
        {
            _hexText.Text = DisplayHex(color);
            UpdateColorSelectionIndicators();
            return false;
        }

        var gradientChanged = _editingGradientTarget == GradientColorTarget.Stop;
        if (gradientChanged)
        {
            _selectedGradientStop = Math.Clamp(_selectedGradientStop, 0, _gradientStops.Length - 1);
            _gradientStops[_selectedGradientStop] = new GradientStop(_gradientStops[_selectedGradientStop].Position, color);
        }
        else if (_editingFill) _fill = color;
        else _stroke = color;
        _opacity = color.A / 255f;
        _hexText.Text = DisplayHex(color);
        SetHexInvalid(false);
        UpdateTargetPresentation();
        UpdateColorSelectionIndicators();

        _handlingColorChange = true;
        try
        {
            if (gradientChanged)
            {
                RefreshGradientPresentation();
                RaiseGradientChanged();
            }
            else
            {
                RaiseMaterialChanged(fillChanged: _editingFill, strokeChanged: !_editingFill);
            }
        }
        finally
        {
            _handlingColorChange = false;
        }

        if (addRecentForDiscreteEdit && _colorInteractionDepth == 0) AddRecentColor(color);
        return true;
    }

    private Color ChannelGradient(int index, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        var first = _channelSliders[0].Value;
        var second = _channelSliders[1].Value;
        var third = _channelSliders[2].Value;
        return _colorMode switch
        {
            ColorMode.Hsv => index switch
            {
                0 => ColorFromHsv(amount * 360, second / 100d, third / 100d, 255),
                1 => ColorFromHsv(first, amount, third / 100d, 255),
                2 => ColorFromHsv(first, second / 100d, amount, 255),
                _ => Color.FromArgb((int)Math.Round(amount * 255), EditedColor.R, EditedColor.G, EditedColor.B)
            },
            ColorMode.Hsl => index switch
            {
                0 => ColorFromHsl(amount * 360, second / 100d, third / 100d, 255),
                1 => ColorFromHsl(first, amount, third / 100d, 255),
                2 => ColorFromHsl(first, second / 100d, amount, 255),
                _ => Color.FromArgb((int)Math.Round(amount * 255), EditedColor.R, EditedColor.G, EditedColor.B)
            },
            ColorMode.Lab => index switch
            {
                0 => ColorFromLab(amount * 100, second / 10d, third / 10d, 255),
                1 => ColorFromLab(first / 10d, -128 + amount * 255, third / 10d, 255),
                2 => ColorFromLab(first / 10d, second / 10d, -128 + amount * 255, 255),
                _ => Color.FromArgb((int)Math.Round(amount * 255), EditedColor.R, EditedColor.G, EditedColor.B)
            },
            _ => index switch
            {
                0 => Color.FromArgb((int)Math.Round(amount * 255), second, third),
                1 => Color.FromArgb(first, (int)Math.Round(amount * 255), third),
                2 => Color.FromArgb(first, second, (int)Math.Round(amount * 255)),
                _ => Color.FromArgb((int)Math.Round(amount * 255), first, second, third)
            }
        };
    }

    private void RefreshChannelGradients()
    {
        foreach (var slider in _channelSliders) slider.RefreshGradient();
    }

    private void HexTextKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            CommitHexText();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Escape)
        {
            _hexText.Text = DisplayHex(EditedColor);
            SetHexInvalid(false);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private void CommitHexText()
    {
        if (_updatingComponents) return;
        if (!TryParseHex(_hexText.Text, out var parsed))
        {
            SetHexInvalid(true);
            return;
        }

        SetHexInvalid(false);
        if (SetEditedColor(parsed, addRecentForDiscreteEdit: true)) UpdateEditorFromColor();
        else _hexText.Text = DisplayHex(parsed);
    }

    private void SetHexInvalid(bool invalid)
    {
        _hexInvalid = invalid;
        var background = _hexText.BackColor.IsEmpty ? Theme.Field : _hexText.BackColor;
        _hexText.ForeColor = invalid
            ? Theme.ReadableText(background, Theme.DangerText)
            : Theme.ReadableText(background, Theme.Text);
        _toolTip.SetToolTip(_hexText, invalid ? "Use #RRGGBB or #RRGGBBAA" : "Hex color (#RRGGBB or #RRGGBBAA)");
    }

    private void SelectPaletteColor(Color color)
    {
        var selected = Color.FromArgb(EditedColor.A, color.R, color.G, color.B);
        SetEditedColor(selected, addRecentForDiscreteEdit: true);
        UpdateEditorFromColor();
        ClosePalettePopup();
    }

    private void AddCurrentToCustomPalette()
    {
        var color = Opaque(EditedColor);
        RemoveColor(CustomPalette, color);
        CustomPalette.Insert(0, color);
        if (CustomPalette.Count > MaxCustomColors) CustomPalette.RemoveRange(MaxCustomColors, CustomPalette.Count - MaxCustomColors);
        RefreshRuntimePalettes();
    }

    private void RemoveFocusedCustomColor()
    {
        var color = _customGrid.FocusedColor;
        if (color.HasValue) RemoveCustomColor(color.Value);
    }

    private void RemoveCustomColor(Color color)
    {
        if (!RemoveColor(CustomPalette, color)) return;
        RefreshRuntimePalettes();
    }

    private void AddRecentColor(Color color)
    {
        color = Opaque(color);
        RemoveColor(RecentPalette, color);
        RecentPalette.Insert(0, color);
        if (RecentPalette.Count > MaxRecentColors) RecentPalette.RemoveRange(MaxRecentColors, RecentPalette.Count - MaxRecentColors);
        RefreshRuntimePalettes();
    }

    private void RefreshRuntimePalettes()
    {
        _recentGrid.SetColors(RecentPalette);
        _inlineRecentGrid.SetColors(RecentPalette.Take(8));
        UpdateInlineRecentLayout();
        _recentGradientGrid.SetPresets(RecentGradientPresets);
        _customGrid.SetColors(CustomPalette);
        _gradientPresetGrid.SetPresets(AllGradientPresets());
        if (_selectedSavedGradientPreset is not null && !SavedGradientPresets.Contains(_selectedSavedGradientPreset)) _selectedSavedGradientPreset = null;
        _recentPaletteTitle.Text = RecentPalette.Count == 0 && RecentGradientPresets.Count == 0
            ? "Recent"
            : $"Recent ({RecentPalette.Count} colors, {RecentGradientPresets.Count} gradients)";
        _customPaletteTitle.Text = CustomPalette.Count == 0
            ? UiLocalization.T("Custom")
            : $"{UiLocalization.T("Custom")} ({CustomPalette.Count})";
        _removeCustom.Enabled = CustomPalette.Count > 0;
        RefreshGradientPresetActions();
        UpdateColorSelectionIndicators();
    }

    private void UpdateTargetPresentation()
    {
        _fillTarget.SwatchColor = _fill;
        _strokeTarget.SwatchColor = _stroke;
        // A gradient stop is still part of the active fill or stroke target.
        // Keep that target visible instead of leaving both target buttons idle.
        _fillTarget.Selected = _editingFill;
        _strokeTarget.Selected = !_editingFill;
        _fillTarget.AccessibleDescription = GradientTargetDescription("Fill", GradientForPreview(strokeTarget: false), _fill);
        _strokeTarget.AccessibleDescription = GradientTargetDescription("Stroke", GradientForPreview(strokeTarget: true), _stroke);
        UpdateStrokeSettingsVisibility();
        RefreshGradientPresentation();
    }

    private void RefreshGradientTargetPreview()
    {
        var fillGradient = GradientForPreview(strokeTarget: false);
        var strokeGradient = GradientForPreview(strokeTarget: true);
        _fillTarget.SetGradientPreview(fillGradient.Kind, fillGradient.Stops);
        _fillTarget.DetailText = GradientTargetDetail(fillGradient);
        _strokeTarget.SetGradientPreview(strokeGradient.Kind, strokeGradient.Stops);
        _strokeTarget.DetailText = GradientTargetDetail(strokeGradient);
        _toolTip.SetToolTip(_fillTarget, $"{UiLocalization.T("Edit fill color")}\n{ToHex(_fill)}");
        _toolTip.SetToolTip(_strokeTarget, $"{UiLocalization.T("Edit stroke color")}\n{ToHex(_stroke)}");
    }

    private void UpdateStrokeSettingsVisibility()
    {
        if (_materialSettings is null || _content is null) return;

        var showStrokeSettings = !_editingFill;
        var materialRowHeight = showStrokeSettings ? InspectorHeight(Theme.ControlHeight + Theme.InspectorRowMarginVertical * 2 + 16) : 0f;
        var changed = _strokeWidthLabel.Visible != showStrokeSettings
            || _strokeWidth.Visible != showStrokeSettings
            || Math.Abs(_content.RowStyles[3].Height - materialRowHeight) > 0.001f;
        if (!changed) return;

        _content.SuspendLayout();
        _materialSettings.SuspendLayout();
        try
        {
            _strokeWidthLabel.Visible = showStrokeSettings;
            _strokeWidth.Visible = showStrokeSettings;
            _content.RowStyles[3].Height = materialRowHeight;
        }
        finally
        {
            _materialSettings.ResumeLayout(performLayout: true);
            _content.ResumeLayout(performLayout: true);
        }

        UpdatePanelHeight();
    }

    private GradientPaintState GradientForPreview(bool strokeTarget)
    {
        return strokeTarget == _gradientPreviewOnStroke
            ? new GradientPaintState(_gradientKind, _gradientStops)
            : strokeTarget ? _strokeGradient : _fillGradient;
    }

    private static string GradientTargetDetail(GradientPaintState gradient) => gradient.Kind == GradientKind.Solid
        ? string.Empty
        : string.Format(UiLocalization.T("{0}, {1} stops"), GradientKindLabel(gradient.Kind, compact: true), gradient.Stops.Length);

    private static string GradientTargetDescription(string targetName, GradientPaintState gradient, Color solidColor) => gradient.Kind == GradientKind.Solid
        ? $"{targetName} color {ToHex(solidColor)}"
        : $"{GradientKindLabel(gradient.Kind, compact: false)} {targetName.ToLowerInvariant()} gradient with {gradient.Stops.Length} color stops";

    private static string GradientKindLabel(GradientKind kind, bool compact) => kind switch
    {
        GradientKind.ShapeRadial => UiLocalization.T(compact ? "Shape" : "Shape radial"),
        GradientKind.Radial => UiLocalization.T("Radial"),
        GradientKind.Linear => UiLocalization.T("Linear"),
        _ => UiLocalization.T("Solid")
    };

    private void UpdateColorSelectionIndicators()
    {
        var color = Opaque(EditedColor);
        _builtInGrid.SelectedColor = color;
        _recentGrid.SelectedColor = color;
        _inlineRecentGrid.SelectedColor = color;
        _currentColor.SwatchColor = EditedColor;
        _customGrid.SelectedColor = color;
    }

    private void BeginColorContinuousEdit()
    {
        if (_colorInteractionDepth++ > 0) return;
        _continuousColorStartArgb = EditedColor.ToArgb();
        ContinuousEditStarted?.Invoke(this, EventArgs.Empty);
    }

    private void CompleteColorContinuousEdit()
    {
        if (_colorInteractionDepth <= 0) return;
        _colorInteractionDepth--;
        if (_colorInteractionDepth > 0) return;
        if (EditedColor.ToArgb() != _continuousColorStartArgb)
        {
            AddRecentColor(EditedColor);
            if (_editingGradientTarget == GradientColorTarget.Stop) AddRecentGradient(_gradientKind, _gradientStops);
        }
        ContinuousEditCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void CancelColorContinuousEdit()
    {
        if (_colorInteractionDepth <= 0) return;
        _colorInteractionDepth = 0;
        ContinuousEditCanceled?.Invoke(this, EventArgs.Empty);
    }

    private void BeginGradientContinuousEdit()
    {
        if (_continuousControlInteractionDepth++ > 0) return;
        ContinuousEditStarted?.Invoke(this, EventArgs.Empty);
    }

    private void CompleteGradientContinuousEdit(bool recordGradient = true)
    {
        if (_continuousControlInteractionDepth <= 0) return;
        _continuousControlInteractionDepth--;
        if (_continuousControlInteractionDepth > 0) return;
        if (recordGradient) AddRecentGradient(_gradientKind, _gradientStops);
        ContinuousEditCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void CancelGradientContinuousEdit()
    {
        if (_continuousControlInteractionDepth <= 0) return;
        _continuousControlInteractionDepth = 0;
        ContinuousEditCanceled?.Invoke(this, EventArgs.Empty);
    }

    private static void AddSectionDivider(Panel section)
    {
        // Draw on the container, keeping the divider out of focus and hit testing.
        section.Paint += (_, e) =>
        {
            var y = Math.Max(1, section.Padding.Top / 2);
            using var line = new Pen(InspectorModuleStyle.DividerColor, section.DeviceDpi / 96f);
            e.Graphics.DrawLine(line, 0, y, Math.Max(0, section.ClientSize.Width - 1), y);
        };
        section.Resize += (_, _) => section.Invalidate();
    }

    private static Label CreateFieldLabel(string text)
    {
        return new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            ForeColor = Theme.Muted,
            BackColor = Theme.Panel,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = Theme.UiFont(Theme.InspectorLabelFontSize),
            Margin = Padding.Empty
        };
    }

    private static Label CreateSectionLabel(string text)
    {
        return new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            ForeColor = Theme.Muted,
            BackColor = Theme.Panel,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = Theme.UiFont(9, FontStyle.Bold)
        };
    }

    private static Label CreatePalettePopupLabel(string text)
    {
        var label = InspectorModuleStyle.CreateTitle(text);
        label.Dock = DockStyle.Fill;
        label.Margin = Padding.Empty;
        label.BackColor = Theme.PanelStrong;
        label.ForeColor = Theme.SecondaryText;
        label.Font = Theme.UiFont(8.5f, FontStyle.Bold);
        return label;
    }

    // Rounded-corner palette flyout. Follows the same approach as
    // AnimatedContextMenuStrip: clip the window with a rounded Region while
    // Theme.UsesRoundedCorners is active and paint a matching rounded border.
    private sealed class PalettePopupDropDown : ToolStripDropDown
    {
        public PalettePopupDropDown()
        {
            AutoClose = true;
            AutoSize = false;
            BackColor = Theme.PanelStrong;
            Padding = new Padding(4);
        }

        private void RefreshRegion()
        {
            var oldRegion = Region;
            if (!Theme.UsesRoundedCorners || ClientSize.Width <= 0 || ClientSize.Height <= 0)
                Region = null;
            else
            {
                using var outline = Theme.CreateUiOutline(ClientRectangle, DeviceDpi);
                Region = new Region(outline);
            }
            oldRegion?.Dispose();
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            RefreshRegion();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            RefreshRegion();
        }
    }

    private sealed class PalettePopupRenderer : ToolStripProfessionalRenderer
    {
        public PalettePopupRenderer() : base(new PalettePopupColorTable())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            if (!Theme.UsesRoundedCorners)
            {
                base.OnRenderToolStripBorder(e);
                return;
            }
            var bounds = new RectangleF(0.5f, 0.5f, Math.Max(1, e.ToolStrip.Width - 1), Math.Max(1, e.ToolStrip.Height - 1));
            using var path = Theme.CreateUiOutline(bounds, e.ToolStrip.DeviceDpi);
            using var border = new Pen(SystemInformation.HighContrast ? SystemColors.WindowText : Theme.ControlOutline);
            var state = e.Graphics.Save();
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.DrawPath(border, path);
            e.Graphics.Restore(state);
        }
    }

    private sealed class PalettePopupColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.PanelStrong;
        public override Color ToolStripBorder => Theme.ControlOutline;
    }

    private static bool RemoveColor(List<Color> colors, Color color)
    {
        var index = colors.FindIndex(item => item.ToArgb() == color.ToArgb());
        if (index < 0) return false;
        colors.RemoveAt(index);
        return true;
    }

    private static Color Opaque(Color color) => Color.FromArgb(color.R, color.G, color.B);

    private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}{color.A:X2}";
    private static string DisplayHex(Color color) => color.A == 255
        ? $"#{color.R:X2}{color.G:X2}{color.B:X2}" : ToHex(color);
    private static decimal AlphaPercent(int alpha) => Math.Round(alpha * 100m / 255m, 1);

    private static bool TryParseHex(string text, out Color color)
    {
        var value = text.Trim();
        if (value.StartsWith('#')) value = value[1..];
        if (value.Length is 3 or 4) value = string.Concat(value.Select(character => $"{character}{character}"));
        if (value.Length is 6 or 8 && uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgba))
        {
            var red = (int)(rgba >> (value.Length == 8 ? 24 : 16)) & 0xFF;
            var green = (int)(rgba >> (value.Length == 8 ? 16 : 8)) & 0xFF;
            var blue = (int)(rgba >> (value.Length == 8 ? 8 : 0)) & 0xFF;
            var alpha = value.Length == 8 ? (int)rgba & 0xFF : 255;
            color = Color.FromArgb(alpha, red, green, blue);
            return true;
        }

        color = Color.Empty;
        return false;
    }

    private static void RgbToHsv(Color color, out double hue, out double saturation, out double value)
    {
        var red = color.R / 255d;
        var green = color.G / 255d;
        var blue = color.B / 255d;
        var maximum = Math.Max(red, Math.Max(green, blue));
        var minimum = Math.Min(red, Math.Min(green, blue));
        var delta = maximum - minimum;

        hue = 0;
        if (delta > 0.000001)
        {
            if (Math.Abs(maximum - red) < 0.000001) hue = 60 * (((green - blue) / delta) % 6);
            else if (Math.Abs(maximum - green) < 0.000001) hue = 60 * ((blue - red) / delta + 2);
            else hue = 60 * ((red - green) / delta + 4);
            if (hue < 0) hue += 360;
        }

        saturation = maximum <= 0 ? 0 : delta / maximum;
        value = maximum;
    }

    private static Color ColorFromHsv(double hue, double saturation, double value, int alpha)
    {
        hue = ((hue % 360) + 360) % 360;
        saturation = Math.Clamp(saturation, 0, 1);
        value = Math.Clamp(value, 0, 1);
        var chroma = value * saturation;
        var secondary = chroma * (1 - Math.Abs(hue / 60 % 2 - 1));
        var offset = value - chroma;
        var (red, green, blue) = HueComponents(hue, chroma, secondary);
        return Color.FromArgb(alpha, Byte(red + offset), Byte(green + offset), Byte(blue + offset));
    }

    private static Color ColorFromHsl(double hue, double saturation, double lightness, int alpha)
    {
        hue = ((hue % 360) + 360) % 360;
        saturation = Math.Clamp(saturation, 0, 1);
        lightness = Math.Clamp(lightness, 0, 1);
        var chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        var secondary = chroma * (1 - Math.Abs(hue / 60 % 2 - 1));
        var offset = lightness - chroma / 2;
        var (red, green, blue) = HueComponents(hue, chroma, secondary);
        return Color.FromArgb(alpha, Byte(red + offset), Byte(green + offset), Byte(blue + offset));
    }

    // CIELAB uses the standard sRGB D65 reference white with XYZ normalized to Y = 1.
    internal static void RgbToLab(Color color, out double lightness, out double greenRed, out double blueYellow)
    {
        var red = SrgbToLinear(color.R / 255d);
        var green = SrgbToLinear(color.G / 255d);
        var blue = SrgbToLinear(color.B / 255d);
        var x = (0.4124564 * red + 0.3575761 * green + 0.1804375 * blue) / 0.95047;
        var y = 0.2126729 * red + 0.7151522 * green + 0.0721750 * blue;
        var z = (0.0193339 * red + 0.1191920 * green + 0.9503041 * blue) / 1.08883;
        var fx = LabForward(x);
        var fy = LabForward(y);
        var fz = LabForward(z);
        lightness = Math.Clamp(116 * fy - 16, 0, 100);
        greenRed = 500 * (fx - fy);
        blueYellow = 200 * (fy - fz);
        if (Math.Abs(greenRed) < 0.0001) greenRed = 0;
        if (Math.Abs(blueYellow) < 0.0001) blueYellow = 0;
    }

    internal static Color ColorFromLab(double lightness, double greenRed, double blueYellow, int alpha)
    {
        if (!double.IsFinite(lightness) || !double.IsFinite(greenRed) || !double.IsFinite(blueYellow))
        {
            throw new ArgumentOutOfRangeException(nameof(lightness), "CIELAB components must be finite.");
        }
        lightness = Math.Clamp(lightness, 0, 100);
        greenRed = Math.Clamp(greenRed, -128, 127);
        blueYellow = Math.Clamp(blueYellow, -128, 127);
        var fy = (lightness + 16) / 116;
        var x = 0.95047 * LabInverse(fy + greenRed / 500);
        var y = LabInverse(fy);
        var z = 1.08883 * LabInverse(fy - blueYellow / 200);
        var red = 3.2404542 * x - 1.5371385 * y - 0.4985314 * z;
        var green = -0.969266 * x + 1.8760108 * y + 0.041556 * z;
        var blue = 0.0556434 * x - 0.2040259 * y + 1.0572252 * z;
        return Color.FromArgb(
            alpha,
            Byte(LinearToSrgb(Math.Clamp(red, 0, 1))),
            Byte(LinearToSrgb(Math.Clamp(green, 0, 1))),
            Byte(LinearToSrgb(Math.Clamp(blue, 0, 1))));
    }

    private static double SrgbToLinear(double value)
    {
        return value <= 0.04045
            ? value / 12.92
            : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    private static double LinearToSrgb(double value)
    {
        return value <= 0.0031308
            ? value * 12.92
            : 1.055 * Math.Pow(value, 1 / 2.4) - 0.055;
    }

    private static double LabForward(double value)
    {
        const double epsilon = 216d / 24389d;
        const double kappa = 24389d / 27d;
        return value > epsilon ? Math.Cbrt(value) : (kappa * value + 16) / 116;
    }

    private static double LabInverse(double value)
    {
        const double epsilon = 216d / 24389d;
        const double kappa = 24389d / 27d;
        var cube = value * value * value;
        return cube > epsilon ? cube : (116 * value - 16) / kappa;
    }

    private static (double Red, double Green, double Blue) HueComponents(double hue, double chroma, double secondary)
    {
        return hue switch
        {
            < 60 => (chroma, secondary, 0),
            < 120 => (secondary, chroma, 0),
            < 180 => (0, chroma, secondary),
            < 240 => (0, secondary, chroma),
            < 300 => (secondary, 0, chroma),
            _ => (chroma, 0, secondary)
        };
    }

    private static int Byte(double value) => (int)Math.Clamp(Math.Round(value * 255), 0, 255);

    private enum ColorMode
    {
        Rgb,
        Hsv,
        Hsl,
        Lab
    }

    private enum GradientColorTarget
    {
        None,
        Stop
    }

    private sealed record GradientPaintState(GradientKind Kind, GradientStop[] Stops);
}
