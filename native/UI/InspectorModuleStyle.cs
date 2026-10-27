namespace VectorAnimationEngine;

/// <summary>Shared presentation for inspector modules, independent of editor state.</summary>
internal static class InspectorModuleStyle
{
    public const int PageHeaderHeight = 54;
    public const float PageTitleFontSize = 10f;

    public static void StylePageTitle(Label title)
    {
        title.Font = Theme.UiFont(PageTitleFontSize);
        title.TextAlign = ContentAlignment.MiddleLeft;
        title.AutoEllipsis = true;
        title.ForeColor = Theme.PanelSecondaryText;
    }

    public static Label CreateTitle(string text, bool primary = false) => new ModuleTitleLabel
    {
        Primary = primary,
        Text = text,
        Dock = DockStyle.Fill,
        Height = Theme.InspectorTitleHeight,
        BackColor = Color.Transparent,
        Font = Theme.UiFont(8.5f),
        ForeColor = primary ? Theme.Text : TitleColor,
        TextAlign = ContentAlignment.TopLeft,
        Margin = Padding.Empty,
        Padding = Padding.Empty,
        AutoEllipsis = true,
        UseMnemonic = false
    };

    private static Color TitleColor => SystemInformation.HighContrast ? SystemColors.ControlText
        : Theme.ReadableUiColor(Theme.Panel, Theme.Mix(Theme.Panel, Theme.Text, 0.5f));

    public static Label CreateTextLabel() => new InspectorTextLabel
    {
        Font = Theme.UiFont(),
        ForeColor = Theme.Text,
        TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true,
        UseMnemonic = false
    };

    public static Label CreateFieldLabel(string text)
    {
        var label = CreateTextLabel();
        label.Text = text;
        label.Dock = DockStyle.Fill;
        label.Font = Theme.UiFont(Theme.InspectorLabelFontSize);
        label.ForeColor = Theme.PanelSecondaryText;
        label.Margin = new Padding(0, Theme.GapXs, Theme.GapSm, Theme.GapXs);
        return label;
    }

    public static void StyleField(Control input)
    {
        var scale = input.DeviceDpi / 96f;
        var gap = (int)Math.Round(Theme.GapXs * scale);
        var height = (int)Math.Round(Theme.ControlHeight * scale);
        input.Margin = new Padding(0, gap, 0, gap);
        if (input is ModernNumericUpDown numeric)
        {
            Theme.StyleNumeric(numeric);
            numeric.Font = Theme.UiFont(11f);
        }
        else if (input is ComboBox combo)
        {
            Theme.StyleComboBox(combo);
            Theme.StyleComboBoxSelection(combo, Theme.InspectorLabelFontSize, Theme.ControlHeight);
        }
        else if (input is ModernTextInput text)
        {
            Theme.StyleTextBox(text);
        }
        input.MinimumSize = new Size(input.MinimumSize.Width, Math.Max(input.MinimumSize.Height, height));
        input.Height = Math.Max(input.Height, height);
    }

    public static Color DividerColor => Theme.ControlOutline;

    public static void DrawBottomDivider(Control host, Graphics graphics) =>
        DrawDivider(host, graphics, host.ClientSize.Height);

    public static void DrawDivider(Control host, Graphics graphics, float bottom)
    {
        var thickness = 1.5f * host.DeviceDpi / 96f;
        var y = Math.Max(0, bottom - thickness / 2f);
        using var line = new Pen(DividerColor, thickness);
        var state = graphics.Save();
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.DrawLine(line, 0, y, host.ClientSize.Width, y);
        graphics.Restore(state);
    }

    private sealed class ModuleTitleLabel : InspectorTextLabel
    {
        public bool Primary { get; init; }

        protected override Color TextColor => Primary ? Theme.PanelSecondaryText : TitleColor;
    }

    private class InspectorTextLabel : Label
    {
        public InspectorTextLabel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected virtual Color TextColor => ForeColor;

        protected override void OnPaint(PaintEventArgs e)
        {
            var background = Parent?.BackColor ?? Theme.Panel;
            var color = SystemInformation.HighContrast
                ? Enabled ? SystemColors.ControlText : SystemColors.GrayText
                : !Enabled ? Theme.ReadableUiColor(background, Theme.DisabledText)
                : TextColor;
            var bounds = Rectangle.FromLTRB(Padding.Left, Padding.Top,
                Math.Max(Padding.Left, Width - Padding.Right), Math.Max(Padding.Top, Height - Padding.Bottom));
            var flags = TextFormatFlags.SingleLine
                | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping
                | TextFormatFlags.PreserveGraphicsTranslateTransform;
            flags |= TextAlign switch
            {
                ContentAlignment.TopCenter or ContentAlignment.MiddleCenter or ContentAlignment.BottomCenter
                    => TextFormatFlags.HorizontalCenter,
                ContentAlignment.TopRight or ContentAlignment.MiddleRight or ContentAlignment.BottomRight
                    => TextFormatFlags.Right,
                _ => TextFormatFlags.Left
            };
            flags |= TextAlign switch
            {
                ContentAlignment.MiddleLeft or ContentAlignment.MiddleCenter or ContentAlignment.MiddleRight
                    => TextFormatFlags.VerticalCenter,
                ContentAlignment.BottomLeft or ContentAlignment.BottomCenter or ContentAlignment.BottomRight
                    => TextFormatFlags.Bottom,
                _ => TextFormatFlags.Top
            };
            if (AutoEllipsis) flags |= TextFormatFlags.EndEllipsis;
            if (!UseMnemonic) flags |= TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(e.Graphics, Text, Font, bounds, color, flags);
        }
    }
}
