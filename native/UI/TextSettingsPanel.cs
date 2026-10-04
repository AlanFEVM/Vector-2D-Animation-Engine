using System.Drawing.Text;

namespace VectorAnimationEngine;

internal sealed class TextSettingsPanel : UserControl
{
    private const string DefaultFontFamilyName = "Segoe UI";
    private const float DefaultFontSizePoints = 12f;

    private sealed class StyleOption(TextFontStyle value, string label)
    {
        public TextFontStyle Value { get; } = value;

        public override string ToString() => UiLocalization.T(label);
    }

    private readonly InstalledFontCollection _installedFonts = new();
    private readonly ComboBox _fontFamily = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ModernNumericUpDown _fontSize = new();
    private readonly ComboBox _fontStyle = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly SvgIconButton _alignLeft = new(SvgIconKind.TextAlignLeft);
    private readonly SvgIconButton _alignCenter = new(SvgIconKind.TextAlignCenter);
    private readonly SvgIconButton _alignRight = new(SvgIconKind.TextAlignRight);
    private readonly ToolTip _toolTip = new();
    private TextHorizontalAlignment _alignment = TextHorizontalAlignment.Left;
    private bool _settingsInitialized;
    private bool _updating;

    public TextSettingsPanel()
    {
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Font = Theme.UiFont();
        Padding = Theme.InspectorSectionPadding;
        MinimumSize = new Size(248, PreferredHeight - 12);
        AccessibleName = "Text Tool";
        AccessibleRole = AccessibleRole.Pane;
        Theme.StyleToolTip(_toolTip);

        BuildUi();
        SetSettings(
            DefaultFontFamilyName,
            DefaultFontSizePoints,
            TextFontStyle.Regular,
            TextHorizontalAlignment.Left);
    }

    public string FontFamilyName => _fontFamily.SelectedItem as string ?? DefaultFontFamilyName;

    public float FontSizePoints => (float)_fontSize.Value;

    public TextFontStyle FontStyle => _fontStyle.SelectedItem is StyleOption option
        ? option.Value
        : TextFontStyle.Regular;

    public TextHorizontalAlignment Alignment => _alignment;

    public int PreferredHeight => Theme.InspectorTitleHeight
        + Theme.InspectorContentPaddingTop
        + Theme.InspectorRowHeight * 4
        + Theme.InspectorSectionPaddingVertical * 2;

    public event EventHandler? SettingsChanged;

    public void SetSettings(
        string fontFamilyName,
        float fontSizePoints,
        TextFontStyle style,
        TextHorizontalAlignment alignment)
    {
        var requestedName = string.IsNullOrWhiteSpace(fontFamilyName)
            ? DefaultFontFamilyName
            : fontFamilyName.Trim();
        var normalizedSize = NormalizeFontSize(fontSizePoints);
        var normalizedStyle = Enum.IsDefined(style) ? style : TextFontStyle.Regular;
        var normalizedAlignment = Enum.IsDefined(alignment)
            ? alignment
            : TextHorizontalAlignment.Left;
        if (_settingsInitialized
            && string.Equals(FontFamilyName, requestedName, StringComparison.OrdinalIgnoreCase)
            && Math.Abs(FontSizePoints - normalizedSize) <= 0.001f
            && FontStyle == normalizedStyle
            && _alignment == normalizedAlignment)
        {
            return;
        }

        var fontIndex = FindFontFamilyIndex(requestedName);
        if (fontIndex < 0) fontIndex = FindFontFamilyIndex(DefaultFontFamilyName);
        if (fontIndex < 0 && _fontFamily.Items.Count > 0) fontIndex = 0;
        var styleIndex = FindFontStyleIndex(normalizedStyle);
        if (styleIndex < 0) styleIndex = FindFontStyleIndex(TextFontStyle.Regular);
        if (styleIndex < 0 && _fontStyle.Items.Count > 0) styleIndex = 0;
        var alignmentChanged = !_settingsInitialized || _alignment != normalizedAlignment;
        if (_settingsInitialized
            && _fontFamily.SelectedIndex == fontIndex
            && Math.Abs(FontSizePoints - normalizedSize) <= 0.001f
            && _fontStyle.SelectedIndex == styleIndex
            && !alignmentChanged)
        {
            return;
        }

        var wasUpdating = _updating;
        _updating = true;
        try
        {
            if (_fontFamily.SelectedIndex != fontIndex) _fontFamily.SelectedIndex = fontIndex;
            var sizeValue = (decimal)normalizedSize;
            if (_fontSize.Value != sizeValue) _fontSize.Value = sizeValue;
            if (_fontStyle.SelectedIndex != styleIndex) _fontStyle.SelectedIndex = styleIndex;
            _alignment = normalizedAlignment;
            if (alignmentChanged) UpdateAlignmentButtons();
            _settingsInitialized = true;
        }
        finally
        {
            _updating = wasUpdating;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _toolTip.Dispose();
            _installedFonts.Dispose();
        }

        base.Dispose(disposing);
    }

    private void BuildUi()
    {
        SuspendLayout();

        var title = new Label
        {
            Text = "Text",
            Dock = DockStyle.Top,
            Height = Theme.InspectorTitleHeight,
            ForeColor = Theme.Text,
            BackColor = Theme.Panel,
            Font = Theme.UiFont(10, System.Drawing.FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        };
        Controls.Add(title);

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 2,
            RowCount = 4,
            Padding = new Padding(0, Theme.InspectorContentPaddingTop, 0, 0),
            Margin = Padding.Empty
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.InspectorFieldLabelColumnWidthNarrow));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 4; row++)
        {
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.InspectorRowHeight));
        }

        Controls.Add(content);
        content.BringToFront();

        PopulateFontFamilies();
        _fontFamily.Dock = DockStyle.Fill;
        _fontFamily.Margin = Theme.InspectorFieldMargin(rightGap: false);
        _fontFamily.AccessibleName = "Font";
        Theme.StyleComboBox(_fontFamily);
        _fontFamily.SelectedIndexChanged += (_, _) => NotifySettingsChanged();
        AddField(content, "Font", _fontFamily, 0);

        _fontSize.Minimum = 4m;
        _fontSize.Maximum = 512m;
        _fontSize.DecimalPlaces = 1;
        _fontSize.Increment = 1m;
        _fontSize.Suffix = UiLocalization.T("pt");
        _fontSize.Dock = DockStyle.Fill;
        _fontSize.Margin = Theme.InspectorFieldMargin(rightGap: false);
        _fontSize.AccessibleName = "Size";
        Theme.StyleNumeric(_fontSize);
        _fontSize.ValueChanged += (_, _) => NotifySettingsChanged();
        AddField(content, "Size", _fontSize, 1);

        _fontStyle.Items.AddRange([
            new StyleOption(TextFontStyle.Regular, "Regular"),
            new StyleOption(TextFontStyle.Bold, "Bold"),
            new StyleOption(TextFontStyle.Italic, "Italic"),
            new StyleOption(TextFontStyle.BoldItalic, "Bold Italic")
        ]);
        _fontStyle.Dock = DockStyle.Fill;
        _fontStyle.Margin = Theme.InspectorFieldMargin(rightGap: false);
        _fontStyle.AccessibleName = "Style";
        Theme.StyleComboBox(_fontStyle);
        _fontStyle.SelectedIndexChanged += (_, _) => NotifySettingsChanged();
        AddField(content, "Style", _fontStyle, 2);

        var alignmentControls = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            AccessibleName = "Align",
            AccessibleRole = AccessibleRole.Grouping
        };
        ConfigureAlignmentButton(
            _alignLeft,
            "Align Left",
            TextHorizontalAlignment.Left,
            new Padding(0, 2, 4, 2));
        ConfigureAlignmentButton(
            _alignCenter,
            "Align Center",
            TextHorizontalAlignment.Center,
            new Padding(0, 2, 4, 2));
        ConfigureAlignmentButton(
            _alignRight,
            "Align Right",
            TextHorizontalAlignment.Right,
            new Padding(0, 2, 0, 2));
        alignmentControls.Controls.Add(_alignLeft);
        alignmentControls.Controls.Add(_alignCenter);
        alignmentControls.Controls.Add(_alignRight);
        AddField(content, "Align", alignmentControls, 3);

        ResumeLayout(performLayout: true);
    }

    private void PopulateFontFamilies()
    {
        var fontNames = _installedFonts.Families
            .Select(static family => family.Name)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!fontNames.Contains(DefaultFontFamilyName, StringComparer.OrdinalIgnoreCase))
        {
            fontNames.Add(DefaultFontFamilyName);
        }

        fontNames.Sort(StringComparer.CurrentCultureIgnoreCase);
        _fontFamily.Items.AddRange(fontNames.Cast<object>().ToArray());
    }

    private void ConfigureAlignmentButton(
        SvgIconButton button,
        string accessibleName,
        TextHorizontalAlignment alignment,
        Padding margin)
    {
        button.Width = Theme.ControlHeightCompact;
        button.Height = Theme.ControlHeightCompact;
        button.Margin = margin;
        button.AccessibleName = accessibleName;
        button.AccessibleRole = AccessibleRole.RadioButton;
        button.TabStop = true;
        Theme.StyleSegmentedButton(button);
        _toolTip.SetToolTip(button, accessibleName);
        button.Click += (_, _) => SelectAlignment(alignment);
    }

    private void SelectAlignment(TextHorizontalAlignment alignment)
    {
        if (_alignment == alignment) return;
        _alignment = alignment;
        UpdateAlignmentButtons();
        NotifySettingsChanged();
    }

    private void UpdateAlignmentButtons()
    {
        StyleAlignmentButton(_alignLeft, _alignment == TextHorizontalAlignment.Left);
        StyleAlignmentButton(_alignCenter, _alignment == TextHorizontalAlignment.Center);
        StyleAlignmentButton(_alignRight, _alignment == TextHorizontalAlignment.Right);
    }

    private static void StyleAlignmentButton(SvgIconButton button, bool active)
    {
        Theme.StyleSegmentedButton(button, active);
        button.Invalidate();
    }

    private int FindFontFamilyIndex(string name)
    {
        for (var index = 0; index < _fontFamily.Items.Count; index++)
        {
            if (_fontFamily.Items[index] is string candidate
                && string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private int FindFontStyleIndex(TextFontStyle style)
    {
        for (var index = 0; index < _fontStyle.Items.Count; index++)
        {
            if (_fontStyle.Items[index] is StyleOption option && option.Value == style) return index;
        }
        return -1;
    }

    private void NotifySettingsChanged()
    {
        if (_updating) return;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static float NormalizeFontSize(float fontSizePoints)
    {
        return float.IsFinite(fontSizePoints)
            ? Math.Clamp(fontSizePoints, 4f, 512f)
            : DefaultFontSizePoints;
    }

    private static void AddField(TableLayoutPanel parent, string label, Control input, int row)
    {
        var labelControl = new Label
        {
            Text = label,
            Dock = DockStyle.Fill,
            ForeColor = Theme.Muted,
            BackColor = Theme.Panel,
            Font = Theme.UiFont(),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = false,
            Margin = Theme.InspectorFieldMargin(rightGap: true)
        };
        parent.Controls.Add(labelControl, 0, row);
        parent.Controls.Add(input, 1, row);
    }
}
