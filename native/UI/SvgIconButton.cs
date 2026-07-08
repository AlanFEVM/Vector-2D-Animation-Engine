namespace VectorAnimationEngine;

internal class SvgIconButton : Button
{
    public SvgIconButton(SvgIconKind icon)
    {
        Icon = icon;
        Text = string.Empty;
        Width = 34;
        Height = 34;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    public SvgIconKind Icon { get; set; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var active = BackColor.ToArgb() == Theme.Accent.ToArgb();
        using var background = new SolidBrush(BackColor);
        using var border = new Pen(active ? Theme.Accent : Theme.Border);
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Top);
        e.Graphics.FillRectangle(background, ClientRectangle);
        e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        SvgIcons.Draw(e.Graphics, Icon, ClientRectangle, ForeColor);
    }
}

internal sealed class SvgToggleButton : CheckBox
{
    public SvgToggleButton(SvgIconKind icon, string name)
    {
        Icon = icon;
        Text = string.Empty;
        AccessibleName = name;
        Width = 32;
        Height = 32;
        Appearance = Appearance.Button;
        FlatStyle = FlatStyle.Flat;
        Margin = new Padding(0, 0, 6, 0);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    public SvgIconKind Icon { get; set; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var back = Checked ? Theme.Accent : Theme.PanelStrong;
        var fore = Checked ? Theme.AccentText : Theme.Text;
        using var background = new SolidBrush(back);
        using var border = new Pen(Checked ? Theme.Accent : Theme.Border);
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Panel);
        e.Graphics.FillRectangle(background, ClientRectangle);
        e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        SvgIcons.Draw(e.Graphics, Icon, ClientRectangle, fore);
    }
}
