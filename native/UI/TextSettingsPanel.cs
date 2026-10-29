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
    private readonly Label _title = new();
    private readonly Label _editingScope = InspectorModuleStyle.CreateTextLabel();
    private readonly Label _textType = InspectorModuleStyle.CreateTextLabel();
    private readonly Label _fontCaption = InspectorModuleStyle.CreateFieldLabel("Font");
    private readonly Label _styleCaption = InspectorModuleStyle.CreateFieldLabel("Style");
    private readonly Label _sizeCaption = InspectorModuleStyle.CreateFieldLabel("Size");
    private readonly Label _alignCaption = InspectorModuleStyle.CreateFieldLabel("Align");
    private readonly SvgIconButton _characterHeader = new(SvgIconKind.ChevronDown)
        { Text = "Character", IsDisclosureHeader = true };
    private readonly SvgIconButton _paragraphHeader = new(SvgIconKind.ChevronDown)
        { Text = "Paragraph", IsDisclosureHeader = true };
    private bool _characterExpanded = true;
    private bool _paragraphExpanded = true;
    private bool _layoutReady;
    private bool _arranging;
    private int _paragraphTop;
    private TextHorizontalAlignment _alignment = TextHorizontalAlignment.Left;
    private bool _settingsInitialized;
    private bool _fontSizeInteracting;
    private bool _updating;

    public TextSettingsPanel()
    {
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Font = Theme.UiFont();
        AutoScaleMode = AutoScaleMode.None;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Padding = Padding.Empty;
        AccessibleName = "Text Tool";
        AccessibleRole = AccessibleRole.Pane;
        Theme.StyleToolTip(_toolTip);

        BuildUi();
        SetEditingContext(toolDefaults: false);
        SetSettings(
            DefaultFontFamilyName,
            DefaultFontSizePoints,
            TextFontStyle.Regular,
            TextHorizontalAlignment.Left);
        UiLocalization.Watch(this);
    }

    public string FontFamilyName => _fontFamily.SelectedItem as string ?? DefaultFontFamilyName;

    public float FontSizePoints => (float)_fontSize.Value;

    public TextFontStyle FontStyle => _fontStyle.SelectedItem is StyleOption option
        ? option.Value
        : TextFontStyle.Regular;

    public TextHorizontalAlignment Alignment => _alignment;

    public int PreferredHeight => 106 + 36 + (_characterExpanded ? 120 : 0)
        + 12 + 36 + (_paragraphExpanded ? 40 : 0) + 12;

    public event EventHandler? SettingsChanged;

    public void SetEditingContext(bool toolDefaults)
    {
        AccessibleName = UiLocalization.T(toolDefaults ? "Text Tool Defaults" : "Text Object Properties");
        _title.Text = UiLocalization.T(toolDefaults ? "Text Tool" : "Text");
        _editingScope.Text = UiLocalization.T(toolDefaults ? "New text defaults" : "Selected text");
        _textType.Text = UiLocalization.T("Static Text");
        _toolTip.SetToolTip(_editingScope, UiLocalization.T(toolDefaults
            ? "New text defaults. Existing text is not changed."
            : "Editing this text only. Tool defaults are not changed."));
        PerformLayout();
    }

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
        if (fontIndex < 0) fontIndex = _fontFamily.Items.Add(requestedName);
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
        Controls.AddRange([_title, _editingScope, _textType, _characterHeader, _paragraphHeader,
            _fontCaption, _styleCaption, _sizeCaption, _alignCaption,
            _fontFamily, _fontStyle, _fontSize, _alignLeft, _alignCenter, _alignRight]);
        InspectorModuleStyle.StylePageTitle(_title);
        foreach (var label in new[] { _editingScope, _textType })
        {
            label.Font = Theme.UiFont(Theme.InspectorLabelFontSize);
            label.ForeColor = Theme.PanelSecondaryText;
            label.BackColor = Color.Transparent;
        }
        _textType.TextAlign = ContentAlignment.MiddleRight;
        foreach (var header in new[] { _characterHeader, _paragraphHeader })
        {
            header.AccessibleRole = AccessibleRole.PushButton;
            header.AccessibleName = header.Text;
            Theme.StyleToolbarButton(header);
        }
        _characterHeader.Click += (_, _) =>
        {
            _characterExpanded = !_characterExpanded;
            _characterHeader.Icon = _characterExpanded ? SvgIconKind.ChevronDown : SvgIconKind.ChevronRight;
            PerformLayout();
        };
        _paragraphHeader.Click += (_, _) =>
        {
            _paragraphExpanded = !_paragraphExpanded;
            _paragraphHeader.Icon = _paragraphExpanded ? SvgIconKind.ChevronDown : SvgIconKind.ChevronRight;
            PerformLayout();
        };

        PopulateFontFamilies();
        _fontFamily.AccessibleName = "Font";
        Theme.StyleComboBox(_fontFamily);
        InspectorModuleStyle.StyleField(_fontFamily);
        _fontFamily.SelectedIndexChanged += (_, _) => NotifySettingsChanged();
        _fontFamily.SelectedIndexChanged += (_, _) => _toolTip.SetToolTip(_fontFamily, FontFamilyName);

        _fontSize.Minimum = 4m;
        _fontSize.Maximum = 512m;
        _fontSize.DecimalPlaces = 1;
        _fontSize.Increment = 1m;
        _fontSize.Suffix = UiLocalization.T("pt");
        _fontSize.ShowSuffix = true;
        _fontSize.AccessibleName = "Size";
        InspectorModuleStyle.StyleField(_fontSize);
        _fontSize.ValueChanged += (_, _) => NotifySettingsChanged();
        _fontSize.InteractionStarted += (_, _) => _fontSizeInteracting = true;
        _fontSize.InteractionCompleted += (_, interaction) =>
        {
            _fontSizeInteracting = false;
            if (interaction.Changed) NotifySettingsChanged();
        };
        _fontSize.InteractionCanceled += (_, _) => _fontSizeInteracting = false;

        _fontStyle.Items.AddRange([
            new StyleOption(TextFontStyle.Regular, "Regular"),
            new StyleOption(TextFontStyle.Bold, "Bold"),
            new StyleOption(TextFontStyle.Italic, "Italic"),
            new StyleOption(TextFontStyle.BoldItalic, "Bold Italic")
        ]);
        _fontStyle.AccessibleName = "Style";
        Theme.StyleComboBox(_fontStyle);
        InspectorModuleStyle.StyleField(_fontStyle);
        _fontStyle.SelectedIndexChanged += (_, _) => NotifySettingsChanged();

        ConfigureAlignmentButton(_alignLeft, "Align Left", TextHorizontalAlignment.Left);
        ConfigureAlignmentButton(_alignCenter, "Align Center", TextHorizontalAlignment.Center);
        ConfigureAlignmentButton(_alignRight, "Align Right", TextHorizontalAlignment.Right);
        _layoutReady = true;
        ResumeLayout(performLayout: true);
    }

    protected override void OnLayout(LayoutEventArgs eventArgs)
    {
        base.OnLayout(eventArgs);
        if (!_layoutReady || _arranging) return;
        _arranging = true;
        try
        {
            var inset = ScaleMetric(16);
            var right = Math.Max(inset + 1, ClientSize.Width - inset);
            var width = Math.Max(1, right - inset);
            var rowHeight = ScaleMetric(Theme.ControlHeight);
            var split = inset + (int)Math.Floor(width * 0.48f);
            _title.SetBounds(inset + ScaleMetric(24), ScaleMetric(16), Math.Max(1, width - ScaleMetric(24)), ScaleMetric(28));
            var typeWidth = Math.Min(width / 2, TextRenderer.MeasureText(_textType.Text, _textType.Font).Width + ScaleMetric(4));
            _editingScope.SetBounds(inset, ScaleMetric(62), Math.Max(1, width - typeWidth - ScaleMetric(8)), ScaleMetric(28));
            _textType.SetBounds(right - typeWidth, ScaleMetric(62), typeWidth, ScaleMetric(28));
            _characterHeader.SetBounds(inset, ScaleMetric(106), width, ScaleMetric(28));
            _characterHeader.TabIndex = 0;
            _characterHeader.AccessibleDescription = UiLocalization.T(_characterExpanded ? "Expanded" : "Collapsed");
            _paragraphHeader.AccessibleDescription = UiLocalization.T(_paragraphExpanded ? "Expanded" : "Collapsed");
            var cursorY = ScaleMetric(142);
            var tabIndex = 1;
            void Field(Control editor, Label caption, bool visible)
            {
                caption.Dock = DockStyle.None;
                caption.Visible = visible;
                editor.Visible = visible;
                editor.TabStop = visible && editor.Enabled;
                if (!visible) return;
                var labelHeight = Math.Min(rowHeight, caption.Font.Height + ScaleMetric(4));
                caption.SetBounds(inset, cursorY + (rowHeight - labelHeight) / 2,
                    Math.Max(1, split - inset - ScaleMetric(8)), labelHeight);
                editor.SetBounds(split, cursorY, Math.Max(1, right - split), rowHeight);
                editor.TabIndex = tabIndex++;
                cursorY += ScaleMetric(40);
            }
            Field(_fontFamily, _fontCaption, _characterExpanded);
            Field(_fontStyle, _styleCaption, _characterExpanded);
            Field(_fontSize, _sizeCaption, _characterExpanded);
            cursorY += ScaleMetric(12);
            _paragraphTop = cursorY;
            _paragraphHeader.SetBounds(inset, cursorY, width, ScaleMetric(28));
            _paragraphHeader.TabIndex = tabIndex++;
            cursorY += ScaleMetric(36);
            _alignCaption.Dock = DockStyle.None;
            _alignCaption.Visible = _paragraphExpanded;
            var captionHeight = Math.Min(rowHeight, _alignCaption.Font.Height + ScaleMetric(4));
            _alignCaption.SetBounds(inset, cursorY + (rowHeight - captionHeight) / 2,
                Math.Max(1, split - inset - ScaleMetric(8)), captionHeight);
            var alignLeft = split;
            foreach (var button in new[] { _alignLeft, _alignCenter, _alignRight })
            {
                button.Visible = _paragraphExpanded;
                button.TabStop = _paragraphExpanded && button.Enabled;
                button.TabIndex = tabIndex++;
                button.SetBounds(alignLeft, cursorY, rowHeight, rowHeight);
                alignLeft += rowHeight + ScaleMetric(4);
            }
            var preferredHeight = ScaleMetric(PreferredHeight);
            if (Height != preferredHeight) Height = preferredHeight;
            Invalidate();
        }
        finally { _arranging = false; }
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        SvgIcons.Draw(eventArgs.Graphics, SvgIconKind.Text,
            new Rectangle(ScaleMetric(16), ScaleMetric(21), ScaleMetric(18), ScaleMetric(18)), Theme.PanelSecondaryText);
        InspectorModuleStyle.DrawDivider(this, eventArgs.Graphics, ScaleMetric(54));
        InspectorModuleStyle.DrawDivider(this, eventArgs.Graphics, ScaleMetric(98));
        InspectorModuleStyle.DrawDivider(this, eventArgs.Graphics, _paragraphTop - ScaleMetric(10));
        InspectorModuleStyle.DrawBottomDivider(this, eventArgs.Graphics);
    }

    protected override void OnDpiChangedAfterParent(EventArgs eventArgs)
    {
        base.OnDpiChangedAfterParent(eventArgs);
        PerformLayout();
    }

    private int ScaleMetric(int value) => Math.Max(1, (int)Math.Round(value * DeviceDpi / 96f));

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
        TextHorizontalAlignment alignment)
    {
        button.Width = Theme.ControlHeight;
        button.Height = Theme.ControlHeight;
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
        if (_updating || _fontSizeInteracting) return;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static float NormalizeFontSize(float fontSizePoints)
    {
        return float.IsFinite(fontSizePoints)
            ? Math.Clamp(fontSizePoints, 4f, 512f)
            : DefaultFontSizePoints;
    }

}
