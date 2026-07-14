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
    private readonly Button _solidFillMode = new() { Text = "Solid" };
    private readonly Button _linearGradientMode = new() { Text = "Linear" };
    private readonly Button _radialGradientMode = new() { Text = "Radial" };
    private readonly Button _addGradientStop = new() { Text = "+" };
    private readonly Button _removeGradientStop = new() { Text = "-" };
    private readonly ModernNumericUpDown _gradientStopPosition = new() { Suffix = "%" };
    private readonly GradientStopStrip _gradientStopStrip = new();
    private readonly GradientPresetGrid _gradientPresetGrid = new();
    private readonly Panel _gradientPanel = new();
    private readonly Dictionary<ColorMode, Button> _modeButtons = [];
    private readonly Label[] _channelLabels = [new(), new(), new(), new()];
    private readonly ColorComponentSlider[] _channelSliders = [new(), new(), new(), new()];
    private readonly ModernNumericUpDown[] _channelValues = [new(), new(), new(), new()];
    private readonly Panel _channelsPanel = new();
    private readonly Panel _hexPanel = new();
    private readonly TextBox _hexText = new();
    private readonly HarmonyColorWheel _colorWheel = new();
    private readonly ComboBox _harmonyRule = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ColorPaletteGrid _harmonyGrid = new();
    private readonly ColorPaletteGrid _builtInGrid = new();
    private readonly ColorPaletteGrid _recentGrid = new();
    private readonly ColorPaletteGrid _customGrid = new();
    private readonly Label _recentPaletteTitle = CreateSectionLabel("Recent");
    private readonly Label _customPaletteTitle = CreateSectionLabel("Custom");
    private readonly Button _addCustom = new() { Text = "+" };
    private readonly Button _removeCustom = new() { Text = "-" };
    private readonly Button _colorEditorToggle = new() { Text = "Color" };
    private readonly ModernNumericUpDown _strokeWidth = new() { Suffix = "pt" };
    private readonly Label _strokeWidthLabel = CreateFieldLabel("Width pt");
    private readonly ModernSlider _opacity = new();
    private readonly Label _opacityValue = new();
    private readonly Panel _lineEndpointStylePanel = new();
    private readonly Button _startSharpEndpointStyle = new() { Text = "Sharp" };
    private readonly Button _startRoundEndpointStyle = new() { Text = "Round" };
    private readonly Button _endSharpEndpointStyle = new() { Text = "Sharp" };
    private readonly Button _endRoundEndpointStyle = new() { Text = "Round" };
    private readonly ToolTip _toolTip = new();
    private TableLayoutPanel? _content;
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
    private int _continuousColorStartArgb;
    private ColorMode _colorMode = ColorMode.Rgb;
    private Color _fill = Color.FromArgb(79, 179, 162);
    private Color _stroke = Color.FromArgb(238, 242, 241);
    private GradientStop[] _gradientStops =
    [
        new GradientStop(0, Color.FromArgb(79, 179, 162)),
        new GradientStop(1, Color.FromArgb(238, 242, 241))
    ];
    private GradientKind _gradientKind = GradientKind.Solid;
    private LineEndpointStyle _startEndpointStyle = LineEndpointStyle.Round;
    private LineEndpointStyle _endEndpointStyle = LineEndpointStyle.Round;
    private bool _lineEndpointStyleVisible;

    public MaterialEditorPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Panel;
        MinimumSize = new Size(240, 0);
        Padding = new Padding(0, 8, 0, 8);
        Theme.StyleToolTip(_toolTip);

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
        get => _opacity.Value / 100f;
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

    public void SetGradientPreviewTarget(bool strokeTarget)
    {
        if (_gradientPreviewOnStroke == strokeTarget) return;
        _gradientPreviewOnStroke = strokeTarget;
        UpdateTargetPresentation();
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

    public void SetGradient(bool enabled, Color start, Color end)
    {
        SetGradient(enabled ? GradientKind.Linear : GradientKind.Solid,
            [new GradientStop(0, start), new GradientStop(1, end)]);
    }

    public void SetGradient(GradientKind kind, IReadOnlyList<GradientStop> stops)
    {
        var normalized = NormalizeGradientStops(stops);
        if (_gradientKind == kind && _gradientStops.SequenceEqual(normalized)) return;

        _updating = true;
        _gradientKind = kind;
        _gradientStops = normalized;
        _selectedGradientStop = Math.Clamp(_selectedGradientStop, 0, _gradientStops.Length - 1);
        RefreshGradientPresentation();
        UpdateTargetPresentation();
        if (!_handlingColorChange && _colorInteractionDepth == 0) UpdateEditorFromColor();
        _updating = false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _toolTip.Dispose();
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
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 202));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        Controls.Add(content);
        _content = content;

        content.Controls.Add(new Label
        {
            Text = "Materials",
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
        BuildPalette(content);
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
        _toolTip.SetToolTip(_colorEditorToggle, "Show or hide color editor");
        _colorEditorToggle.Click += (_, _) => SetColorEditorExpanded(!_colorEditorExpanded);
        Theme.StyleButton(_colorEditorToggle);
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
        AddModeButton(row, ColorMode.Hex, "Hex", 4);
        content.Controls.Add(row, 0, 5);
    }

    private void AddModeButton(TableLayoutPanel row, ColorMode mode, string text, int column)
    {
        var button = new Button
        {
            Text = text,
            Dock = DockStyle.Fill,
            Height = Theme.ControlHeightCompact,
            Margin = new Padding(column == 1 ? 0 : 1, 3, column == 4 ? 0 : 1, 3)
        };
        Theme.StyleButton(button);
        button.Click += (_, _) => SetColorMode(mode);
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
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty
        };
        picker.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        picker.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void UpdatePickerColumns()
        {
            picker.ColumnStyles[0].Width = picker.ClientSize.Width < 256 ? 88 : 104;
        }
        picker.SizeChanged += (_, _) => UpdatePickerColumns();
        UpdatePickerColumns();
        _colorWheel.Dock = DockStyle.Fill;
        _colorWheel.Margin = new Padding(0, 1, 8, 1);
        _colorWheel.ColorChanged += (_, _) => ColorWheelChanged();
        _colorWheel.InteractionStarted += (_, _) => BeginColorContinuousEdit();
        _colorWheel.InteractionCompleted += (_, _) => CompleteColorContinuousEdit();
        _colorWheel.InteractionCanceled += (_, _) => CancelColorContinuousEdit();
        _toolTip.SetToolTip(_colorWheel, "Drag the ring to adjust the primary hue");
        picker.Controls.Add(_colorWheel, 0, 0);

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
        _harmonyRule.SelectedIndexChanged += (_, _) => HarmonyRuleChanged();
        _toolTip.SetToolTip(_harmonyRule, "Choose a color harmony rule");
        ruleRow.Controls.Add(_harmonyRule, 1, 0);
        harmony.Controls.Add(ruleRow, 0, 0);

        _harmonyGrid.Dock = DockStyle.Fill;
        _harmonyGrid.Margin = new Padding(0, 1, 0, 0);
        _harmonyGrid.SwatchSize = 20;
        _harmonyGrid.Gap = 4;
        _harmonyGrid.ColorSelected += (_, e) => SelectPaletteColor(e.Color);
        _harmonyGrid.HoveredColorChanged += (_, e) => _toolTip.SetToolTip(_harmonyGrid, ToHex(e.Color));
        _harmonyGrid.HoverCleared += (_, _) => _toolTip.SetToolTip(_harmonyGrid, string.Empty);
        harmony.Controls.Add(_harmonyGrid, 0, 1);
        picker.Controls.Add(harmony, 1, 0);
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
        channels.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 66));
        channels.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        channels.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62));
        for (var i = 0; i < 4; i++) channels.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        _channelsPanel.Controls.Add(channels);

        for (var i = 0; i < 4; i++) ConfigureChannelRow(channels, i);

        _hexPanel.Dock = DockStyle.Fill;
        _hexPanel.BackColor = Theme.Panel;
        _hexPanel.Visible = false;
        componentHost.Controls.Add(_hexPanel);
        var hexRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 38,
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

    private void BuildPalette(TableLayoutPanel content)
    {
        var palette = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 1,
            RowCount = 6,
            Margin = Padding.Empty
        };
        palette.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        palette.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        palette.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        palette.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
        palette.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        palette.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        palette.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        content.Controls.Add(palette, 0, 7);

        palette.Controls.Add(CreateSectionLabel("Palette"), 0, 0);
        ConfigurePaletteGrid(_builtInGrid);
        _builtInGrid.SetColors(BuiltInPalette);
        palette.Controls.Add(_builtInGrid, 0, 1);

        palette.Controls.Add(_recentPaletteTitle, 0, 2);
        ConfigurePaletteGrid(_recentGrid);
        _recentGrid.EmptyText = "No recent colors";
        palette.Controls.Add(_recentGrid, 0, 3);

        var customHeader = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty
        };
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
        RefreshRuntimePalettes();
    }

    private void ConfigurePaletteGrid(ColorPaletteGrid grid)
    {
        grid.Dock = DockStyle.Fill;
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
        button.Font = Theme.UiFont(10, FontStyle.Bold);
        Theme.StyleButton(button);
        _toolTip.SetToolTip(button, toolTip);
    }

    private void BuildMaterialSettings(TableLayoutPanel content)
    {
        var settings = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 2,
            RowCount = 2,
            Margin = Padding.Empty
        };
        _materialSettings = settings;
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        settings.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        settings.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        content.Controls.Add(settings, 0, 3);

        settings.Controls.Add(_strokeWidthLabel, 0, 0);
        _strokeWidth.Minimum = 0;
        _strokeWidth.Maximum = 32;
        _strokeWidth.DecimalPlaces = 1;
        _strokeWidth.Increment = 0.5m;
        _strokeWidth.Dock = DockStyle.Fill;
        _strokeWidth.Margin = new Padding(0, 4, 0, 4);
        Theme.StyleNumeric(_strokeWidth);
        _strokeWidth.ValueChanged += (_, _) => RaiseMaterialChanged(strokeWidthChanged: true);
        _strokeWidth.InteractionStarted += (_, _) => ContinuousEditStarted?.Invoke(this, EventArgs.Empty);
        _strokeWidth.InteractionCompleted += (_, _) => ContinuousEditCompleted?.Invoke(this, EventArgs.Empty);
        _strokeWidth.InteractionCanceled += (_, _) => ContinuousEditCanceled?.Invoke(this, EventArgs.Empty);
        settings.Controls.Add(_strokeWidth, 1, 0);

        settings.Controls.Add(CreateFieldLabel("All alpha"), 0, 1);
        _opacity.Minimum = 0;
        _opacity.Maximum = 100;
        _opacity.TickFrequency = 10;
        _opacity.Dock = DockStyle.Fill;
        _opacity.Margin = new Padding(0, 3, 0, 0);
        _opacity.ValueChanged += (_, _) =>
        {
            _opacityValue.Text = $"{_opacity.Value}%";
            if (!_updating) SetUniformAlpha(_opacity.Value / 100f);
        };
        _opacity.InteractionStarted += (_, _) => ContinuousEditStarted?.Invoke(this, EventArgs.Empty);
        _opacity.InteractionCompleted += (_, _) => ContinuousEditCompleted?.Invoke(this, EventArgs.Empty);
        _opacity.InteractionCanceled += (_, _) => ContinuousEditCanceled?.Invoke(this, EventArgs.Empty);

        var opacityRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty
        };
        opacityRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        opacityRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48));
        opacityRow.Controls.Add(_opacity, 0, 0);
        _opacityValue.Dock = DockStyle.Fill;
        _opacityValue.ForeColor = Theme.Muted;
        _opacityValue.BackColor = Theme.Panel;
        _opacityValue.TextAlign = ContentAlignment.MiddleCenter;
        _opacityValue.Font = Theme.UiFont();
        opacityRow.Controls.Add(_opacityValue, 1, 0);
        settings.Controls.Add(opacityRow, 1, 1);
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
            ColumnCount = 4,
            RowCount = 4,
            Margin = Padding.Empty
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 68));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34f));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        _gradientPanel.Controls.Add(grid);

        grid.Controls.Add(CreateFieldLabel("Paint"), 0, 0);
        ConfigureGradientModeButton(_solidFillMode, GradientKind.Solid, "Use a solid fill color");
        ConfigureGradientModeButton(_linearGradientMode, GradientKind.Linear, "Use a linear fill gradient");
        ConfigureGradientModeButton(_radialGradientMode, GradientKind.Radial, "Use a radial fill gradient");
        _solidFillMode.Margin = new Padding(0, 3, 2, 3);
        _linearGradientMode.Margin = new Padding(2, 3, 2, 3);
        _radialGradientMode.Margin = new Padding(2, 3, 0, 3);
        grid.Controls.Add(_solidFillMode, 1, 0);
        grid.Controls.Add(_linearGradientMode, 2, 0);
        grid.Controls.Add(_radialGradientMode, 3, 0);

        grid.Controls.Add(CreateFieldLabel("Presets"), 0, 1);
        _gradientPresetGrid.Dock = DockStyle.Fill;
        _gradientPresetGrid.Margin = new Padding(0, 2, 0, 2);
        _gradientPresetGrid.SetPresets(GradientPresets);
        _gradientPresetGrid.PresetSelected += (_, e) => ApplyGradientPreset(e.Preset);
        _gradientPresetGrid.HoveredPresetChanged += (_, e) => _toolTip.SetToolTip(_gradientPresetGrid, $"{e.Preset.Name} ({e.Preset.Kind})");
        _gradientPresetGrid.HoverCleared += (_, _) => _toolTip.SetToolTip(_gradientPresetGrid, string.Empty);
        grid.Controls.Add(_gradientPresetGrid, 1, 1);
        grid.SetColumnSpan(_gradientPresetGrid, 3);

        grid.Controls.Add(CreateFieldLabel("Gradient"), 0, 2);
        _gradientStopStrip.Dock = DockStyle.Fill;
        _gradientStopStrip.Margin = new Padding(0, 2, 0, 2);
        _gradientStopStrip.AccessibleName = "Gradient color stops";
        _gradientStopStrip.StopSelected += (_, e) => SelectGradientStop(e.Index);
        _gradientStopStrip.StopsChanged += (_, e) => SetGradientStopsFromStrip(e.Stops, e.SelectedIndex);
        _gradientStopStrip.InteractionStarted += (_, _) => ContinuousEditStarted?.Invoke(this, EventArgs.Empty);
        _gradientStopStrip.InteractionCompleted += (_, _) => ContinuousEditCompleted?.Invoke(this, EventArgs.Empty);
        _gradientStopStrip.InteractionCanceled += (_, _) => ContinuousEditCanceled?.Invoke(this, EventArgs.Empty);
        grid.Controls.Add(_gradientStopStrip, 1, 2);
        grid.SetColumnSpan(_gradientStopStrip, 3);

        grid.Controls.Add(CreateFieldLabel("Stop"), 0, 3);
        _gradientStopTarget.Dock = DockStyle.Fill;
        _gradientStopTarget.Margin = new Padding(0, 3, 0, 3);
        _gradientStopTarget.Click += (_, _) =>
        {
            SetColorEditorExpanded(expanded: true);
            SelectGradientStop(_selectedGradientStop);
        };
        _gradientStopTarget.AccessibleName = "Selected gradient stop color";
        _toolTip.SetToolTip(_gradientStopTarget, "Edit the selected gradient stop color");
        grid.Controls.Add(_gradientStopTarget, 1, 3);
        grid.SetColumnSpan(_gradientStopTarget, 3);

        grid.Controls.Add(CreateFieldLabel("Position"), 0, 4);
        _gradientStopPosition.Minimum = 0;
        _gradientStopPosition.Maximum = 100;
        _gradientStopPosition.DecimalPlaces = 0;
        _gradientStopPosition.Increment = 1;
        _gradientStopPosition.Dock = DockStyle.Fill;
        _gradientStopPosition.Margin = new Padding(0, 3, 2, 3);
        Theme.StyleNumeric(_gradientStopPosition);
        _gradientStopPosition.ValueChanged += (_, _) => UpdateGradientStopPosition();
        _gradientStopPosition.InteractionStarted += (_, _) => ContinuousEditStarted?.Invoke(this, EventArgs.Empty);
        _gradientStopPosition.InteractionCompleted += (_, _) => ContinuousEditCompleted?.Invoke(this, EventArgs.Empty);
        _gradientStopPosition.InteractionCanceled += (_, _) => ContinuousEditCanceled?.Invoke(this, EventArgs.Empty);
        grid.Controls.Add(_gradientStopPosition, 1, 4);
        ConfigureGradientStopButton(_addGradientStop, "Add a color stop after the selected stop", AddGradientStop);
        ConfigureGradientStopButton(_removeGradientStop, "Remove the selected color stop", RemoveGradientStop);
        _addGradientStop.Margin = new Padding(2, 3, 2, 3);
        _removeGradientStop.Margin = new Padding(2, 3, 0, 3);
        grid.Controls.Add(_addGradientStop, 2, 4);
        grid.Controls.Add(_removeGradientStop, 3, 4);
        RefreshGradientPresentation();
    }

    private void ConfigureGradientModeButton(Button button, GradientKind kind, string toolTip)
    {
        button.Dock = DockStyle.Fill;
        button.AccessibleName = kind switch
        {
            GradientKind.Linear => "Linear gradient fill",
            GradientKind.Radial => "Radial gradient fill",
            _ => "Solid fill"
        };
        Theme.StyleButton(button);
        _toolTip.SetToolTip(button, toolTip);
        button.Click += (_, _) => SetGradientKind(kind);
    }

    private void ConfigureGradientStopButton(Button button, string toolTip, Action action)
    {
        button.Dock = DockStyle.Fill;
        button.AccessibleName = toolTip;
        Theme.StyleButton(button);
        _toolTip.SetToolTip(button, toolTip);
        button.Click += (_, _) => action();
    }

    private void SetGradientKind(GradientKind kind)
    {
        if (_gradientKind == kind) return;
        _gradientKind = kind;
        if (kind != GradientKind.Solid && GradientStart.ToArgb() == GradientEnd.ToArgb())
        {
            _gradientStops =
            [
                new GradientStop(0, _fill),
                new GradientStop(1, _stroke)
            ];
            _selectedGradientStop = 0;
        }

        RefreshGradientPresentation();
        RaiseGradientChanged();
    }

    private void ApplyGradientPreset(GradientPreset preset)
    {
        if (_updating) return;
        _gradientKind = preset.Kind;
        _gradientStops = NormalizeGradientStops(preset.Stops);
        _selectedGradientStop = 0;
        _editingGradientTarget = GradientColorTarget.Stop;
        RefreshGradientPresentation();
        UpdateTargetPresentation();
        UpdateEditorFromColor();
        RaiseGradientChanged();
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
            _content.RowStyles[7].Height = expanded ? 162 : 0;
            _colorEditorToggle.Text = expanded ? "Color -" : "Color +";
            if (expanded) Theme.StyleActiveButton(_colorEditorToggle);
            else Theme.StyleButton(_colorEditorToggle);
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
        if (_gradientKind == GradientKind.Linear)
        {
            Theme.StyleButton(_solidFillMode);
            Theme.StyleActiveButton(_linearGradientMode);
            Theme.StyleButton(_radialGradientMode);
        }
        else if (_gradientKind == GradientKind.Radial)
        {
            Theme.StyleButton(_solidFillMode);
            Theme.StyleButton(_linearGradientMode);
            Theme.StyleActiveButton(_radialGradientMode);
        }
        else
        {
            Theme.StyleActiveButton(_solidFillMode);
            Theme.StyleButton(_linearGradientMode);
            Theme.StyleButton(_radialGradientMode);
        }
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

    private void RaiseGradientChanged()
    {
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
        Theme.StyleButton(button);
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
        if (endpointStyle == LineEndpointStyle.Sharp)
        {
            Theme.StyleActiveButton(sharp);
            Theme.StyleButton(round);
            return;
        }

        Theme.StyleButton(sharp);
        Theme.StyleActiveButton(round);
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
        _opacity.Value = (int)Math.Clamp(MathF.Round(opacity * 100f), _opacity.Minimum, _opacity.Maximum);
        _opacityValue.Text = $"{_opacity.Value}%";
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
        if (_editingGradientTarget == GradientColorTarget.None && _editingFill == fill) return;
        _editingGradientTarget = GradientColorTarget.None;
        _editingFill = fill;
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
        _channelsPanel.Visible = mode != ColorMode.Hex;
        _hexPanel.Visible = mode == ColorMode.Hex;
        if (_hexPanel.Visible) _hexPanel.BringToFront();
        else _channelsPanel.BringToFront();
        ConfigureChannelMode();
        UpdateEditorFromColor();
    }

    private void RefreshModeButtons()
    {
        foreach (var (mode, button) in _modeButtons)
        {
            if (mode == _colorMode) Theme.StyleActiveButton(button);
            else Theme.StyleButton(button);
        }
    }

    private void ConfigureChannelMode()
    {
        var labels = _colorMode switch
        {
            ColorMode.Hsv => new[] { "Hue", "Saturation", "Value" },
            ColorMode.Hsl => new[] { "Hue", "Saturation", "Lightness" },
            _ => new[] { "Red", "Green", "Blue" }
        };
        var maxima = _colorMode is ColorMode.Hsv or ColorMode.Hsl
            ? new[] { 360, 100, 100 }
            : new[] { 255, 255, 255 };

        _updatingComponents = true;
        for (var i = 0; i < 3; i++)
        {
            _channelLabels[i].Text = labels[i];
            _channelLabels[i].AccessibleName = labels[i];
            _channelSliders[i].AccessibleName = $"{_colorMode} {labels[i]}";
            _toolTip.SetToolTip(_channelLabels[i], labels[i]);
            _toolTip.SetToolTip(_channelSliders[i], $"Adjust {labels[i].ToLowerInvariant()}");
            _channelSliders[i].Maximum = maxima[i];
            _channelValues[i].Maximum = maxima[i];
            _channelValues[i].Suffix = _colorMode is ColorMode.Hsv or ColorMode.Hsl
                ? i == 0 ? "degrees" : "percent"
                : string.Empty;
        }
        _channelLabels[3].Text = "Alpha";
        _channelLabels[3].AccessibleName = "Alpha";
        _channelSliders[3].AccessibleName = "Alpha";
        _toolTip.SetToolTip(_channelLabels[3], "Alpha");
        _toolTip.SetToolTip(_channelSliders[3], "Adjust alpha");
        _channelSliders[3].Maximum = 255;
        _channelValues[3].Maximum = 255;
        _channelValues[3].Suffix = string.Empty;
        _updatingComponents = false;
    }

    private void UpdateEditorFromColor()
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
            default:
                SetChannelValues(color.R, color.G, color.B, color.A);
                break;
        }
        _hexText.Text = ToHex(color);
        SetHexInvalid(false);
        _updatingComponents = false;
        UpdateColorWheel();
        RefreshHarmonyPalette();
        RefreshChannelGradients();
        UpdateColorSelectionIndicators();
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

    private void ChannelSliderChanged(int index)
    {
        if (_updatingComponents) return;
        _updatingComponents = true;
        _channelValues[index].Value = _channelSliders[index].Value;
        _updatingComponents = false;
        ApplyColorComponents();
    }

    private void ChannelNumericChanged(int index)
    {
        if (_updatingComponents) return;
        _updatingComponents = true;
        _channelSliders[index].Value = (int)_channelValues[index].Value;
        _updatingComponents = false;
        ApplyColorComponents();
    }

    private void ApplyColorComponents()
    {
        var alpha = _channelSliders[3].Value;
        var first = _channelSliders[0].Value;
        var second = _channelSliders[1].Value;
        var third = _channelSliders[2].Value;
        var color = _colorMode switch
        {
            ColorMode.Hsv => ColorFromHsv(first, second / 100d, third / 100d, alpha),
            ColorMode.Hsl => ColorFromHsl(first, second / 100d, third / 100d, alpha),
            _ => Color.FromArgb(alpha, first, second, third)
        };
        SetEditedColor(color, addRecentForDiscreteEdit: true);
        UpdateColorWheel();
        RefreshHarmonyPalette();
        RefreshChannelGradients();
    }

    private void ColorWheelChanged()
    {
        if (_updatingComponents) return;
        var color = _colorWheel.Color;
        if (SetEditedColor(color, addRecentForDiscreteEdit: true)) UpdateEditorFromColor();
    }

    private void UpdateColorWheel()
    {
        _colorWheel.SetColor(EditedColor);
    }

    private void HarmonyRuleChanged()
    {
        if (_updatingComponents) return;
        RefreshHarmonyPalette();
    }

    private void RefreshHarmonyPalette()
    {
        var index = Math.Clamp(_harmonyRule.SelectedIndex, 0, HarmonyRules.Length - 1);
        _colorWheel.HarmonyMode = HarmonyRules[index].Mode;
        _harmonyGrid.SetColors(_colorWheel.HarmonyColors.Select(Opaque));
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
        if (_hexInvalid == invalid) return;
        _hexInvalid = invalid;
        _hexText.ForeColor = invalid ? Color.FromArgb(255, 150, 150) : Theme.Text;
        _toolTip.SetToolTip(_hexText, invalid ? "Use #RRGGBB or #RRGGBBAA" : "Hex color (#RRGGBB or #RRGGBBAA)");
    }

    private void SelectPaletteColor(Color color)
    {
        var selected = Color.FromArgb(EditedColor.A, color.R, color.G, color.B);
        SetEditedColor(selected, addRecentForDiscreteEdit: true);
        UpdateEditorFromColor();
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
        _customGrid.SetColors(CustomPalette);
        _recentPaletteTitle.Text = RecentPalette.Count == 0 ? "Recent" : $"Recent ({RecentPalette.Count})";
        _customPaletteTitle.Text = CustomPalette.Count == 0 ? "Custom" : $"Custom ({CustomPalette.Count})";
        _removeCustom.Enabled = CustomPalette.Count > 0;
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
        _fillTarget.AccessibleDescription = _gradientPreviewOnStroke
            ? $"Fill color {ToHex(_fill)}"
            : GradientTargetDescription("Fill");
        _strokeTarget.AccessibleDescription = _gradientPreviewOnStroke
            ? GradientTargetDescription("Stroke")
            : $"Stroke color {ToHex(_stroke)}";
        UpdateStrokeSettingsVisibility();
        RefreshGradientPresentation();
    }

    private void RefreshGradientTargetPreview()
    {
        var gradientTarget = _gradientPreviewOnStroke ? _strokeTarget : _fillTarget;
        var solidTarget = _gradientPreviewOnStroke ? _fillTarget : _strokeTarget;
        var solidColor = _gradientPreviewOnStroke ? _fill : _stroke;
        gradientTarget.SetGradientPreview(_gradientKind, _gradientStops);
        gradientTarget.DetailText = GradientTargetDetail();
        solidTarget.SetGradientPreview(GradientKind.Solid, [new GradientStop(0, solidColor), new GradientStop(1, solidColor)]);
        solidTarget.DetailText = ToHex(solidColor);
    }

    private void UpdateStrokeSettingsVisibility()
    {
        if (_materialSettings is null || _content is null) return;

        var showStrokeSettings = !_editingFill;
        var materialRowHeight = showStrokeSettings ? 78f : 38f;
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

    private string GradientTargetDetail() => _gradientKind == GradientKind.Solid
        ? ToHex(_gradientPreviewOnStroke ? _stroke : _fill)
        : $"{_gradientKind}, {_gradientStops.Length} stops";

    private string GradientTargetDescription(string targetName) => _gradientKind == GradientKind.Solid
        ? $"{targetName} color {GradientTargetDetail()}"
        : $"{_gradientKind} {targetName.ToLowerInvariant()} gradient with {_gradientStops.Length} color stops";

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
        if (EditedColor.ToArgb() != _continuousColorStartArgb) AddRecentColor(EditedColor);
        ContinuousEditCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void CancelColorContinuousEdit()
    {
        if (_colorInteractionDepth <= 0) return;
        _colorInteractionDepth = 0;
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
        Hex
    }

    private enum GradientColorTarget
    {
        None,
        Stop
    }
}
