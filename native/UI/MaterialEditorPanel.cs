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
    private readonly ColorTargetButton _gradientStopTarget = new("Stop color");
    private readonly Button _solidFillMode = new SegmentedButton { Text = "Solid" };
    private readonly Button _linearGradientMode = new SegmentedButton { Text = "Linear" };
    private readonly Button _radialGradientMode = new SegmentedButton { Text = "Radial" };
    private readonly Button _shapeRadialGradientMode = new SegmentedButton { Text = "Shape" };
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
    private readonly Panel _hexPanel = new();
    private readonly TextBox _hexText = new();
    private readonly TraditionalColorPlane _traditionalPlane = new();
    private readonly VerticalColorComponentSlider _primaryComponent = new();
    private readonly ComboBox _harmonyRule = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ColorPaletteGrid _harmonyGrid = new();
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
    private readonly Button _colorEditorToggle = new SvgIconButton(SvgIconKind.ChevronDown) { Text = "Color" };
    private readonly ModernNumericUpDown _strokeWidth = new() { Suffix = "pt" };
    private readonly Label _strokeWidthLabel = CreateFieldLabel("Width pt");
    private readonly Panel _lineEndpointStylePanel = new();
    private readonly Button _startSharpEndpointStyle = new SegmentedButton { Text = "Sharp" };
    private readonly Button _startRoundEndpointStyle = new SegmentedButton { Text = "Round" };
    private readonly Button _endSharpEndpointStyle = new SegmentedButton { Text = "Sharp" };
    private readonly Button _endRoundEndpointStyle = new SegmentedButton { Text = "Round" };
    private readonly ToolTip _toolTip = new();
    private readonly ToolStripDropDown _paletteDropDown = new()
    {
        AutoClose = true,
        AutoSize = false,
        BackColor = Theme.PanelStrong,
        Padding = new Padding(4)
    };
    private TableLayoutPanel? _content;
    private TableLayoutPanel? _targetRow;
    private TableLayoutPanel? _materialSettings;
    private bool _updating;
    private bool _updatingComponents;
    private bool _handlingColorChange;
    private bool _colorEditorExpanded;
    private bool _editingFill = true;
    private bool _gradientPreviewOnStroke;
    private GradientColorTarget _editingGradientTarget;
    private int _selectedGradientStop;
    private bool _hexInvalid;
    private int _colorInteractionDepth;
    private int _continuousControlInteractionDepth;
    private int _continuousColorStartArgb;
    private ColorMode _colorMode = ColorMode.Rgb;
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
        Padding = new Padding(0, 8, 0, 8);
        Theme.StyleToolTip(_toolTip);

        EnsureGradientPaletteStateLoaded();
        BuildUi();
        ApplyMaterial(_fill, _stroke, 2f, 1f, raiseEvent: false);
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
        var desiredRowHeight = visible ? 76f : 0f;
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
        ApplyMaterial(fill, stroke, strokeWidth, opacity, raiseEvent: false);
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
            _content.RowStyles[2].Height = enabled ? 0 : _gradientSettingsExpanded ? 158 : 30;
            _content.RowStyles[3].Height = enabled ? 0 : 38;
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
            RowCount = 9,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 158));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        Controls.Add(content);
        _content = content;

        // TODO: Replace this basic shading editor with the full material system.
        content.Controls.Add(new Label
        {
            Text = "Basic Shading",
            Dock = DockStyle.Fill,
            ForeColor = Theme.Text,
            BackColor = Theme.Panel,
            Font = Theme.UiFont(10, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        BuildTargetRow(content);
        BuildGradientSettings(content);
        BuildMaterialSettings(content);
        BuildColorEditorToggle(content);
        BuildModeRow(content);
        BuildColorEditor(content);
        BuildPersistentHex(content);
        BuildPalettePopup();
        BuildLineEndpointStyle(content);
        ConfigureChannelMode();
        RefreshModeButtons();
        _channelsPanel.Visible = true;
        _channelsPanel.BringToFront();
        SetColorEditorExpanded(expanded: false);
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
        _fillTarget.Margin = new Padding(0, 3, 4, 3);
        _strokeTarget.Dock = DockStyle.Fill;
        _strokeTarget.Margin = new Padding(4, 3, 0, 3);
        _fillTarget.Click += (_, _) => SelectTarget(fill: true);
        _strokeTarget.Click += (_, _) => SelectTarget(fill: false);
        _toolTip.SetToolTip(_fillTarget, "Edit fill color");
        _toolTip.SetToolTip(_strokeTarget, "Edit stroke color");
        row.Controls.Add(_fillTarget, 0, 0);
        row.Controls.Add(_strokeTarget, 1, 0);
        content.Controls.Add(row, 0, 1);
    }

    private void BuildColorEditorToggle(TableLayoutPanel content)
    {
        _colorEditorToggle.Dock = DockStyle.Fill;
        _colorEditorToggle.Margin = new Padding(0, 2, 0, 2);
        _colorEditorToggle.AccessibleName = "Show or hide color editor";
        _colorEditorToggle.AccessibleRole = AccessibleRole.CheckButton;
        _toolTip.SetToolTip(_colorEditorToggle, "Show or hide color editor");
        _colorEditorToggle.Click += (_, _) => SetColorEditorExpanded(!_colorEditorExpanded);
        Theme.StyleToolbarButton(_colorEditorToggle);
        content.Controls.Add(_colorEditorToggle, 0, 4);
    }

    private void BuildModeRow(TableLayoutPanel content)
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 5,
            RowCount = 1,
            Margin = Padding.Empty
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48));
        for (var i = 0; i < 4; i++) row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));

        row.Controls.Add(CreateFieldLabel("Mode"), 0, 0);
        AddModeButton(row, ColorMode.Rgb, "RGB", 1);
        AddModeButton(row, ColorMode.Hsv, "HSV", 2);
        AddModeButton(row, ColorMode.Hsl, "HSL", 3);
        AddModeButton(row, ColorMode.Lab, "Lab", 4);
        content.Controls.Add(row, 0, 5);
    }

    private void AddModeButton(TableLayoutPanel row, ColorMode mode, string text, int column)
    {
        var button = new SegmentedButton
        {
            Text = text,
            Dock = DockStyle.Fill,
            Height = Theme.ControlHeightCompact,
            Margin = new Padding(column == 1 ? 0 : 1, 3, column == row.ColumnCount - 1 ? 0 : 1, 3),
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
        var host = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            Margin = Padding.Empty
        };
        content.Controls.Add(host, 0, 6);

        var picker = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 108,
            BackColor = Theme.Panel,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty
        };
        picker.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        picker.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 24));
        picker.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _traditionalPlane.Dock = DockStyle.Fill;
        _traditionalPlane.Margin = new Padding(0, 1, 4, 1);
        _traditionalPlane.ColorAt = TraditionalPlaneColor;
        _traditionalPlane.ValueChanged += (_, _) => TraditionalPlaneChanged();
        _traditionalPlane.InteractionStarted += (_, _) => BeginColorContinuousEdit();
        _traditionalPlane.InteractionCompleted += (_, _) => CompleteColorContinuousEdit();
        _traditionalPlane.InteractionCanceled += (_, _) => CancelColorContinuousEdit();
        picker.Controls.Add(_traditionalPlane, 0, 0);

        _primaryComponent.Dock = DockStyle.Fill;
        _primaryComponent.Margin = new Padding(0, 1, 6, 1);
        _primaryComponent.GradientColor = TraditionalPrimaryGradient;
        _primaryComponent.ValueChanged += (_, _) => TraditionalPrimaryChanged();
        _primaryComponent.InteractionStarted += (_, _) => BeginColorContinuousEdit();
        _primaryComponent.InteractionCompleted += (_, _) => CompleteColorContinuousEdit();
        _primaryComponent.InteractionCanceled += (_, _) => CancelColorContinuousEdit();
        picker.Controls.Add(_primaryComponent, 1, 0);

        var harmony = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty
        };
        harmony.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        harmony.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var ruleRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty
        };
        ruleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62));
        ruleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var harmonyLabel = CreateFieldLabel("Harmony");
        harmonyLabel.Font = Theme.UiFont(8f, FontStyle.Bold);
        harmonyLabel.AutoEllipsis = true;
        ruleRow.Controls.Add(harmonyLabel, 0, 0);
        _harmonyRule.Dock = DockStyle.Fill;
        _harmonyRule.Margin = new Padding(0, 3, 0, 3);
        _harmonyRule.AccessibleName = "Color harmony rule";
        _harmonyRule.Items.AddRange(HarmonyRules.Select(rule => (object)rule.Label).ToArray());
        _harmonyRule.SelectedIndex = 0;
        Theme.StyleComboBox(_harmonyRule);
        _harmonyRule.Font = Theme.UiFont(8.8f);
        _harmonyRule.DropDownWidth = 176;
        _harmonyRule.SelectedIndexChanged += (_, _) => HarmonyRuleChanged();
        _toolTip.SetToolTip(_harmonyRule, "Choose a color harmony rule");
        ruleRow.Controls.Add(_harmonyRule, 1, 0);
        harmony.Controls.Add(ruleRow, 0, 0);

        void UpdatePickerLayout()
        {
            var narrow = picker.ClientSize.Width < 256;
            var showHarmonyLabel = picker.ClientSize.Width >= 360;
            picker.ColumnStyles[0].Width = narrow ? 88 : 104;
            ruleRow.ColumnStyles[0].Width = showHarmonyLabel ? 62 : 0;
            harmonyLabel.Visible = showHarmonyLabel;
        }
        picker.SizeChanged += (_, _) => UpdatePickerLayout();
        UpdatePickerLayout();

        _harmonyGrid.Dock = DockStyle.Fill;
        _harmonyGrid.Margin = new Padding(0, 1, 0, 0);
        _harmonyGrid.SwatchSize = 20;
        _harmonyGrid.Gap = 4;
        _harmonyGrid.ColorSelected += (_, e) => SelectPaletteColor(e.Color);
        _harmonyGrid.HoveredColorChanged += (_, e) => _toolTip.SetToolTip(_harmonyGrid, ToHex(e.Color));
        _harmonyGrid.HoverCleared += (_, _) => _toolTip.SetToolTip(_harmonyGrid, string.Empty);
        harmony.Controls.Add(_harmonyGrid, 0, 1);
        picker.Controls.Add(harmony, 2, 0);
        host.Controls.Add(picker);

        var componentHost = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            Margin = Padding.Empty
        };
        host.Controls.Add(componentHost);
        // WinForms lays out docked controls from back to front. Keep the picker
        // behind the fill host so its top dock consumes its fixed-height area first.
        host.Controls.SetChildIndex(picker, host.Controls.Count - 1);

        _channelsPanel.Dock = DockStyle.Fill;
        _channelsPanel.BackColor = Theme.Panel;
        componentHost.Controls.Add(_channelsPanel);
        var channels = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 3,
            RowCount = 5,
            Margin = Padding.Empty
        };
        channels.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        channels.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        channels.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62));
        for (var i = 0; i < 4; i++) channels.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        _channelsPanel.Controls.Add(channels);

        for (var i = 0; i < 4; i++) ConfigureChannelRow(channels, i);

    }

    private void BuildPersistentHex(TableLayoutPanel content)
    {
        _hexPanel.Dock = DockStyle.Fill;
        _hexPanel.BackColor = Theme.Panel;
        _hexPanel.Visible = true;
        content.Controls.Add(_hexPanel, 0, 7);
        var hexRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = new Padding(0, 4, 0, 4)
        };
        hexRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 66));
        hexRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        hexRow.Controls.Add(CreateFieldLabel("Hex"), 0, 0);
        _hexText.Dock = DockStyle.Fill;
        _hexText.CharacterCasing = CharacterCasing.Upper;
        _hexText.MaxLength = 9;
        Theme.StyleTextBox(_hexText);
        _hexText.KeyDown += HexTextKeyDown;
        _hexText.Leave += (_, _) => CommitHexText();
        hexRow.Controls.Add(_hexText, 1, 0);
        _hexPanel.Controls.Add(hexRow);
    }

    private void ConfigureChannelRow(TableLayoutPanel channels, int index)
    {
        var label = _channelLabels[index];
        label.Dock = DockStyle.Fill;
        label.ForeColor = Theme.Muted;
        label.BackColor = Theme.Panel;
        label.TextAlign = ContentAlignment.MiddleLeft;
        label.Font = Theme.UiFont(8.5f, FontStyle.Bold);
        label.AutoEllipsis = true;
        channels.Controls.Add(label, 0, index);

        var slider = _channelSliders[index];
        slider.Dock = DockStyle.Fill;
        slider.Margin = new Padding(0, 3, 4, 3);
        slider.GradientColor = amount => ChannelGradient(index, amount);
        slider.ValueChanged += (_, _) => ChannelSliderChanged(index);
        slider.InteractionStarted += (_, _) => BeginColorContinuousEdit();
        slider.InteractionCompleted += (_, _) => CompleteColorContinuousEdit();
        slider.InteractionCanceled += (_, _) => CancelColorContinuousEdit();
        channels.Controls.Add(slider, 1, index);

        var value = _channelValues[index];
        value.Minimum = 0;
        value.Maximum = 255;
        value.DecimalPlaces = 0;
        value.Increment = 1;
        value.Dock = DockStyle.Fill;
        value.Margin = new Padding(0, 3, 0, 3);
        Theme.StyleNumeric(value);
        value.ValueChanged += (_, _) => ChannelNumericChanged(index);
        value.InteractionStarted += (_, _) => BeginColorContinuousEdit();
        value.InteractionCompleted += (_, _) => CompleteColorContinuousEdit();
        value.InteractionCanceled += (_, _) => CancelColorContinuousEdit();
        channels.Controls.Add(value, 2, index);
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
            Padding = new Padding(4),
            Size = new Size(278, 330)
        };
        palette.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        palette.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
        palette.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        palette.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
        palette.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        palette.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        palette.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        palette.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        palette.RowStyles.Add(new RowStyle(SizeType.Absolute, 120));

        palette.Controls.Add(CreatePalettePopupLabel("Colors"), 0, 0);
        ConfigurePaletteGrid(_builtInGrid);
        _builtInGrid.SetColors(BuiltInPalette);
        palette.Controls.Add(_builtInGrid, 0, 1);

        _recentPaletteTitle.BackColor = Theme.PanelStrong;
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

        _paletteDropDown.Renderer = new ToolStripProfessionalRenderer(new PalettePopupColorTable());
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
        grid.SwatchSize = 18;
        grid.Gap = 4;
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
        if (_paletteDropDown.Visible)
        {
            _paletteDropDown.Close();
            return;
        }

        RefreshRuntimePalettes();
        _gradientPresetGrid.SetSelection(_gradientKind, _gradientStops);
        _paletteDropDown.Show(
            _paletteButton,
            new Point(Math.Max(0, _paletteButton.Width - _paletteDropDown.Width), _paletteButton.Height));
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
        var settings = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty
        };
        _materialSettings = settings;
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        settings.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        content.Controls.Add(settings, 0, 3);

        settings.Controls.Add(_strokeWidthLabel, 0, 0);
        _strokeWidth.Minimum = 0.1m;
        _strokeWidth.Maximum = 32;
        _strokeWidth.DecimalPlaces = 1;
        _strokeWidth.Increment = 0.1m;
        _strokeWidth.Dock = DockStyle.Fill;
        _strokeWidth.Margin = new Padding(0, 4, 0, 4);
        Theme.StyleNumeric(_strokeWidth);
        _strokeWidth.ValueChanged += (_, _) => RaiseMaterialChanged(strokeWidthChanged: true);
        _strokeWidth.InteractionStarted += (_, _) => BeginGradientContinuousEdit();
        _strokeWidth.InteractionCompleted += (_, _) => CompleteGradientContinuousEdit(recordGradient: false);
        _strokeWidth.InteractionCanceled += (_, _) => CancelGradientContinuousEdit();
        settings.Controls.Add(_strokeWidth, 1, 0);
        _strokeWidthLabel.TextChanged += (_, _) => UpdateMaterialLabelColumnWidth();
        UpdateMaterialLabelColumnWidth();
    }

    private void UpdateMaterialLabelColumnWidth()
    {
        var settings = _materialSettings;
        if (settings is null || settings.ColumnStyles.Count == 0) return;
        var measuredWidth = TextRenderer.MeasureText(
                _strokeWidthLabel.Text,
                _strokeWidthLabel.Font,
                Size.Empty,
                TextFormatFlags.NoPadding).Width
            + _strokeWidthLabel.Margin.Horizontal;
        settings.ColumnStyles[0].Width = Math.Clamp(measuredWidth + 6, 78, 112);
        settings.PerformLayout();
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
            RowCount = 4,
            Margin = Padding.Empty
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
        for (var index = 0; index < 4; index++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25f));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        _gradientPanel.Controls.Add(grid);
        _gradientSettingsGrid = grid;

        void UpdateGradientModeLabels()
        {
            var compact = grid.ClientSize.Width < 270;
            _solidFillMode.Text = compact ? "Sol" : "Solid";
            _linearGradientMode.Text = compact ? "Lin" : "Linear";
            _radialGradientMode.Text = compact ? "Rad" : "Radial";
            _shapeRadialGradientMode.Text = compact ? "Shp" : "Shape";
        }
        grid.SizeChanged += (_, _) => UpdateGradientModeLabels();
        UpdateGradientModeLabels();

        grid.Controls.Add(CreateFieldLabel("Paint"), 0, 0);
        ConfigureGradientModeButton(_solidFillMode, GradientKind.Solid, "Use a solid fill color");
        ConfigureGradientModeButton(_linearGradientMode, GradientKind.Linear, "Use a linear fill gradient");
        ConfigureGradientModeButton(_radialGradientMode, GradientKind.Radial, "Use a radial fill gradient");
        ConfigureGradientModeButton(_shapeRadialGradientMode, GradientKind.ShapeRadial, "Use a shape radial fill gradient");
        _solidFillMode.Margin = new Padding(0, 3, 2, 3);
        _linearGradientMode.Margin = new Padding(2, 3, 2, 3);
        _radialGradientMode.Margin = new Padding(2, 3, 2, 3);
        _shapeRadialGradientMode.Margin = new Padding(2, 3, 0, 3);
        grid.Controls.Add(_solidFillMode, 1, 0);
        grid.Controls.Add(_linearGradientMode, 2, 0);
        grid.Controls.Add(_radialGradientMode, 3, 0);
        grid.Controls.Add(_shapeRadialGradientMode, 4, 0);
        grid.Controls.Add(_paletteButton, 5, 0);

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
        grid.SetColumnSpan(_gradientStopStrip, 5);

        grid.Controls.Add(CreateFieldLabel("Stop"), 0, 2);
        _gradientStopTarget.Dock = DockStyle.Fill;
        _gradientStopTarget.Margin = new Padding(0, 3, 0, 3);
        _gradientStopTarget.Click += (_, _) =>
        {
            SetColorEditorExpanded(expanded: true);
            SelectGradientStop(_selectedGradientStop);
        };
        _gradientStopTarget.AccessibleName = "Selected gradient stop color";
        _toolTip.SetToolTip(_gradientStopTarget, "Edit the selected gradient stop color");
        grid.Controls.Add(_gradientStopTarget, 1, 2);
        grid.SetColumnSpan(_gradientStopTarget, 5);

        grid.Controls.Add(CreateFieldLabel("Position"), 0, 3);
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
        grid.Controls.Add(_gradientStopPosition, 1, 3);
        grid.SetColumnSpan(_gradientStopPosition, 3);
        ConfigureGradientStopButton(_addGradientStop, "Add a color stop after the selected stop", AddGradientStop);
        ConfigureGradientStopButton(_removeGradientStop, "Remove the selected color stop", RemoveGradientStop);
        _addGradientStop.Margin = new Padding(2, 3, 2, 3);
        _removeGradientStop.Margin = new Padding(2, 3, 0, 3);
        grid.Controls.Add(_addGradientStop, 4, 3);
        grid.Controls.Add(_removeGradientStop, 5, 3);
        RefreshGradientPresentation();
    }

    private void ConfigureGradientModeButton(Button button, GradientKind kind, string toolTip)
    {
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

    private void SetColorEditorExpanded(bool expanded)
    {
        if (_content is null || _colorEditorExpanded == expanded) return;
        _colorEditorExpanded = expanded;
        _content.SuspendLayout();
        try
        {
            _content.RowStyles[5].Height = expanded ? 36 : 0;
            _content.RowStyles[6].Height = expanded ? 236 : 0;
            _content.RowStyles[7].Height = 38;
            _colorEditorToggle.Text = "Color";
            if (_colorEditorToggle is SvgIconButton iconButton)
            {
                iconButton.Icon = expanded ? SvgIconKind.ChevronUp : SvgIconKind.ChevronDown;
            }
            _colorEditorToggle.AccessibleDescription = UiLocalization.T(
                expanded ? "Color editor expanded" : "Color editor collapsed");
            Theme.StyleToolbarButton(_colorEditorToggle, expanded);
        }
        finally
        {
            _content.ResumeLayout(performLayout: true);
        }

        UpdatePanelHeight();
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
        _gradientStopTarget.DetailText = $"{MathF.Round(stop.Position * 100f):0}%";
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
            grid.RowStyles[1].Height = expanded ? 46 : 0;
            grid.RowStyles[2].Height = expanded ? 46 : 0;
            grid.RowStyles[3].Height = expanded ? 36 : 0;
            foreach (Control control in grid.Controls)
            {
                if (grid.GetRow(control) > 0) control.Visible = expanded;
            }

            _content.RowStyles[2].Height = _textObjectMode ? 0 : expanded ? 158 : 30;
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
        row.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        row.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
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

    private void ApplyMaterial(Color fill, Color stroke, float strokeWidth, float opacity, bool raiseEvent)
    {
        var fillChanged = _fill.ToArgb() != fill.ToArgb();
        var strokeChanged = _stroke.ToArgb() != stroke.ToArgb();
        var strokeWidthChanged = Math.Abs(StrokeWidth - strokeWidth) > 0.001f;
        var opacityChanged = Math.Abs(Opacity - opacity) > 0.001f;
        if (!raiseEvent && !fillChanged && !strokeChanged && !strokeWidthChanged && !opacityChanged) return;
        var refreshComponents = !_handlingColorChange && _colorInteractionDepth == 0;

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
        SetColorEditorExpanded(expanded: true);
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
        _channelsPanel.Visible = true;
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
            _channelLabels[i].Text = UiLocalization.T(labels[i]);
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
        _channelLabels[3].Text = UiLocalization.T("Alpha");
        _channelLabels[3].AccessibleName = UiLocalization.T("Alpha");
        _channelSliders[3].AccessibleName = UiLocalization.T("Alpha");
        _toolTip.SetToolTip(_channelLabels[3], UiLocalization.T("Alpha"));
        _toolTip.SetToolTip(_channelSliders[3], UiLocalization.T("Adjust alpha"));
        _channelSliders[3].Minimum = 0;
        _channelSliders[3].Maximum = 255;
        _channelValues[3].AccessibleName = UiLocalization.T("Alpha");
        _channelValues[3].Minimum = 0;
        _channelValues[3].Maximum = 255;
        _channelValues[3].DecimalPlaces = 0;
        _channelValues[3].Increment = 1;
        _channelValues[3].Suffix = string.Empty;
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
        _hexText.Text = ToHex(color);
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
            _channelValues[i].Value = next;
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
        _channelValues[3].Value = alpha;
    }

    private void ChannelSliderChanged(int index)
    {
        if (_updatingComponents) return;
        _updatingComponents = true;
        _channelValues[index].Value = _colorMode == ColorMode.Lab && index < 3
            ? _channelSliders[index].Value / 10m
            : _channelSliders[index].Value;
        _updatingComponents = false;
        ApplyColorComponents(index);
    }

    private void ChannelNumericChanged(int index)
    {
        if (_updatingComponents) return;
        _updatingComponents = true;
        _channelSliders[index].Value = _colorMode == ColorMode.Lab && index < 3
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
        if (changedIndex is -1 or 0) _traditionalPlane.RefreshGradient();
        if (changedIndex is -1 or 1 or 2) _primaryComponent.RefreshGradient();
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
            _hexText.Text = ToHex(color);
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
        _hexText.Text = ToHex(color);
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
            _hexText.Text = ToHex(EditedColor);
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
        else _hexText.Text = ToHex(parsed);
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
        _recentGradientGrid.SetPresets(RecentGradientPresets);
        _customGrid.SetColors(CustomPalette);
        _gradientPresetGrid.SetPresets(AllGradientPresets());
        if (_selectedSavedGradientPreset is not null && !SavedGradientPresets.Contains(_selectedSavedGradientPreset)) _selectedSavedGradientPreset = null;
        _recentPaletteTitle.Text = RecentPalette.Count == 0 && RecentGradientPresets.Count == 0
            ? "Recent"
            : $"Recent ({RecentPalette.Count} colors, {RecentGradientPresets.Count} gradients)";
        _customPaletteTitle.Text = CustomPalette.Count == 0 ? "Custom" : $"Custom ({CustomPalette.Count})";
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
        _fillTarget.DetailText = GradientTargetDetail(fillGradient, _fill);
        _strokeTarget.SetGradientPreview(strokeGradient.Kind, strokeGradient.Stops);
        _strokeTarget.DetailText = GradientTargetDetail(strokeGradient, _stroke);
    }

    private void UpdateStrokeSettingsVisibility()
    {
        if (_materialSettings is null || _content is null) return;

        var showStrokeSettings = !_editingFill;
        var materialRowHeight = showStrokeSettings ? 38f : 0f;
        var widthRowHeight = showStrokeSettings ? 38f : 0f;
        var changed = _strokeWidthLabel.Visible != showStrokeSettings
            || _strokeWidth.Visible != showStrokeSettings
            || Math.Abs(_materialSettings.RowStyles[0].Height - widthRowHeight) > 0.001f
            || Math.Abs(_content.RowStyles[3].Height - materialRowHeight) > 0.001f;
        if (!changed) return;

        _content.SuspendLayout();
        _materialSettings.SuspendLayout();
        try
        {
            _strokeWidthLabel.Visible = showStrokeSettings;
            _strokeWidth.Visible = showStrokeSettings;
            _materialSettings.RowStyles[0].Height = widthRowHeight;
            _content.RowStyles[3].Height = materialRowHeight;
        }
        finally
        {
            _materialSettings.ResumeLayout(performLayout: false);
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

    private static string GradientTargetDetail(GradientPaintState gradient, Color solidColor) => gradient.Kind == GradientKind.Solid
        ? ToHex(solidColor)
        : $"{GradientKindLabel(gradient.Kind, compact: true)}, {gradient.Stops.Length} stops";

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

    private static Label CreateFieldLabel(string text)
    {
        return new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            ForeColor = Theme.Muted,
            BackColor = Theme.Panel,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = Theme.UiFont()
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
        var label = CreateSectionLabel(text);
        label.BackColor = Theme.PanelStrong;
        return label;
    }

    private sealed class PalettePopupColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.PanelStrong;
        public override Color ToolStripBorder => Theme.BorderHover;
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
