namespace VectorAnimationEngine;

internal sealed class ReleaseManagerTabControl : TabControl
{
    private const int WindowMessagePaint = 0x000F;
    private const int WindowMessagePrint = 0x0317;
    private const int WindowMessagePrintClient = 0x0318;

    public ReleaseManagerTabControl()
    {
        Appearance = TabAppearance.Normal;
        DrawMode = TabDrawMode.OwnerDrawFixed;
        ItemSize = new Size(118, 34);
        SizeMode = TabSizeMode.Fixed;
        Padding = new Point(0, 0);
        Font = Theme.UiFont(9.5f);
        AccessibleRole = AccessibleRole.PageTabList;
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= TabPages.Count) return;
        var selected = e.Index == SelectedIndex;
        var bounds = GetTabRect(e.Index);
        using var background = new SolidBrush(selected ? Theme.AccentSurface : Theme.Top);
        using var border = new Pen(selected ? Theme.Accent : Theme.Border);
        e.Graphics.FillRectangle(background, bounds);
        e.Graphics.DrawLine(border, bounds.Left, bounds.Bottom - 1, bounds.Right, bounds.Bottom - 1);
        if (selected)
        {
            using var accent = new SolidBrush(Theme.Accent);
            e.Graphics.FillRectangle(accent, bounds.Left + 8, bounds.Bottom - 3, Math.Max(0, bounds.Width - 16), 2);
        }
        TextRenderer.DrawText(
            e.Graphics,
            TabPages[e.Index].Text,
            Font,
            bounds,
            selected ? Theme.AccentLabel : Theme.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        if (Focused && selected && ShowFocusCues)
        {
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(bounds, -5, -5), Theme.AccentLabel, Theme.Top);
        }
    }

    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if (TabCount == 0 || !IsHandleCreated) return;

        if (message.Msg == WindowMessagePaint)
        {
            using var graphics = CreateGraphics();
            PaintHeaderRemainder(graphics);
            return;
        }
        if (message.Msg is not (WindowMessagePrint or WindowMessagePrintClient)
            || message.WParam == IntPtr.Zero)
        {
            return;
        }

        using var printGraphics = Graphics.FromHdc(message.WParam);
        PaintHeaderRemainder(printGraphics);
    }

    private void PaintHeaderRemainder(Graphics graphics)
    {
        var lastTab = GetTabRect(TabCount - 1);
        var remainder = Rectangle.FromLTRB(
            Math.Min(ClientSize.Width, lastTab.Right),
            0,
            ClientSize.Width,
            Math.Min(ClientSize.Height, lastTab.Bottom));
        if (remainder.Width <= 0 || remainder.Height <= 0) return;

        using var background = new SolidBrush(Theme.Top);
        using var border = new Pen(Theme.Border);
        graphics.FillRectangle(background, remainder);
        graphics.DrawLine(border, remainder.Left, remainder.Bottom - 1, remainder.Right, remainder.Bottom - 1);
    }
}
