namespace VectorAnimationEngine;

/// <summary>Shared presentation for inspector modules, independent of editor state.</summary>
internal static class InspectorModuleStyle
{
    public static Label CreateTitle(string text) => new ModuleTitleLabel
    {
        Text = text,
        Dock = DockStyle.Fill,
        Height = Theme.InspectorTitleHeight,
        BackColor = Color.Transparent,
        Font = Theme.UiFont(8.5f),
        ForeColor = TitleColor,
        TextAlign = ContentAlignment.TopLeft,
        Margin = Padding.Empty,
        Padding = Padding.Empty,
        AutoEllipsis = false
    };

    private static Color TitleColor => SystemInformation.HighContrast ? SystemColors.ControlText
        : Theme.ReadableUiColor(Theme.Panel, Theme.Mix(Theme.Panel, Theme.Text, 0.5f));

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

    private sealed class ModuleTitleLabel : Label
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            if (ForeColor != TitleColor) ForeColor = TitleColor;
            base.OnPaint(e);
        }
    }
}
