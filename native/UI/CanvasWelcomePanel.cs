namespace VectorAnimationEngine;

internal sealed class CanvasWelcomePanel : Panel
{
    public event EventHandler? ConfigureRequested;
    public event EventHandler? StartRequested;

    public CanvasWelcomePanel()
    {
        DoubleBuffered = true;
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Padding = new Padding(18, 14, 18, 14);
        Size = new Size(364, 152);
        AccessibleName = "Canvas and background";
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3,
            Margin = Padding.Empty, BackColor = Theme.Panel
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        var title = new Label
        {
            Text = "Canvas not added yet", Dock = DockStyle.Fill, AutoEllipsis = true,
            Font = Theme.UiFont(11f, FontStyle.Bold), ForeColor = Theme.Text,
            TextAlign = ContentAlignment.MiddleCenter, Margin = Padding.Empty
        };
        var hint = new Label
        {
            Text = "Set up your picture, or start creating right away.", Dock = DockStyle.Fill,
            Font = Theme.UiFont(9f), ForeColor = Theme.Muted,
            TextAlign = ContentAlignment.MiddleCenter, Margin = Padding.Empty
        };
        var actions = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty
        };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        var configure = new Button
        {
            Text = "Set up canvas", AccessibleName = "Set up canvas", Dock = DockStyle.Fill,
            AutoEllipsis = true, Margin = new Padding(0, 0, 6, 0)
        };
        var start = new Button
        {
            Text = "Start creating", AccessibleName = "Start creating", Dock = DockStyle.Fill,
            AutoEllipsis = true, Margin = Padding.Empty
        };
        Theme.StyleStandardButton(configure);
        Theme.StyleStandardButton(start);
        configure.Click += (_, _) => ConfigureRequested?.Invoke(this, EventArgs.Empty);
        start.Click += (_, _) => StartRequested?.Invoke(this, EventArgs.Empty);
        actions.Controls.Add(configure, 0, 0);
        actions.Controls.Add(start, 1, 0);
        layout.Controls.Add(title, 0, 0);
        layout.Controls.Add(hint, 0, 1);
        layout.Controls.Add(actions, 0, 2);
        Controls.Add(layout);
        UiLocalization.Watch(this);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var border = new Pen(Theme.Border);
        e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
    }
}
