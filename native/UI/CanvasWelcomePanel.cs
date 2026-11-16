namespace VectorAnimationEngine;

internal sealed class CanvasWelcomePanel : Panel
{
    private readonly Label _title = new();
    private readonly Label _hint = new();
    private readonly Button _configure = new();
    private readonly LinkLabel _start = new();
    private Control? _backdrop;

    public event EventHandler? ConfigureRequested;
    public event EventHandler? StartRequested;

    public CanvasWelcomePanel()
    {
        DoubleBuffered = true;
        Size = new Size(400, 204);
        Padding = new Padding(16, 0, 16, 8);
        AccessibleName = "Canvas and background";
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5,
            Margin = Padding.Empty, BackColor = Color.Transparent
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var height in new[] { 68, 32, 40, 20, 36 })
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, height));
        _title.Text = "Canvas and background";
        _title.Font = Theme.UiFont(12f);
        _hint.Text = "Set up your picture, or start creating right away.";
        _hint.Font = Theme.UiFont(9f);
        foreach (var label in new[] { _title, _hint })
        {
            label.Dock = DockStyle.Fill;
            label.TextAlign = ContentAlignment.MiddleCenter;
            label.Margin = Padding.Empty;
            label.BackColor = Color.Transparent;
        }
        _title.AutoEllipsis = true;
        var actions = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
            Margin = new Padding(28, 0, 28, 0), BackColor = Color.Transparent
        };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        _configure.Text = _configure.AccessibleName = "Set up canvas";
        _configure.Dock = DockStyle.Fill;
        _configure.Margin = new Padding(0, 0, 12, 0);
        _configure.AutoEllipsis = true;
        _configure.Font = Theme.UiFont(9f);
        _configure.FlatStyle = FlatStyle.Flat;
        _configure.FlatAppearance.BorderSize = 1;
        _configure.UseVisualStyleBackColor = false;
        _configure.TabIndex = 0;
        _start.Text = _start.AccessibleName = "Start creating";
        _start.Dock = DockStyle.Fill;
        _start.Margin = Padding.Empty;
        _start.AutoEllipsis = true;
        _start.Font = Theme.UiFont(9f);
        _start.TextAlign = ContentAlignment.MiddleCenter;
        _start.LinkBehavior = LinkBehavior.HoverUnderline;
        _start.TabIndex = 1;
        _configure.Click += (_, _) => ConfigureRequested?.Invoke(this, EventArgs.Empty);
        _start.LinkClicked += (_, _) => StartRequested?.Invoke(this, EventArgs.Empty);
        actions.Controls.Add(_configure, 0, 0);
        actions.Controls.Add(_start, 1, 0);
        layout.Controls.Add(_title, 0, 1);
        layout.Controls.Add(_hint, 0, 2);
        layout.Controls.Add(actions, 0, 4);
        Controls.Add(layout);
        UiLocalization.Watch(this);
        RefreshAppearance();
    }

    protected override void OnParentChanged(EventArgs e)
    {
        if (_backdrop is not null) _backdrop.BackColorChanged -= BackdropChanged;
        base.OnParentChanged(e);
        _backdrop = Parent;
        if (_backdrop is not null) _backdrop.BackColorChanged += BackdropChanged;
        RefreshAppearance();
    }

    private void BackdropChanged(object? sender, EventArgs e) => RefreshAppearance();

    private void RefreshAppearance()
    {
        // No canvas exists in this state. Match the workspace instead of simulating
        // transparency over the Direct2D HWND.
        var background = SystemInformation.HighContrast ? SystemColors.Window : _backdrop?.BackColor ?? Theme.Stage;
        var ink = Theme.ReadableText(background, Theme.Text);
        var secondary = Theme.ReadableText(background, Theme.Mix(background, ink, 0.76f));
        BackColor = background;
        _title.ForeColor = Theme.ReadableText(background, Theme.Mix(background, ink, 0.88f));
        _hint.ForeColor = secondary;
        _configure.BackColor = Theme.Mix(background, ink, 0.06f);
        _configure.ForeColor = Theme.ReadableText(_configure.BackColor, ink);
        _configure.FlatAppearance.BorderColor = SystemInformation.HighContrast
            ? ink : Theme.Mix(background, ink, 0.22f);
        _configure.FlatAppearance.MouseOverBackColor = Theme.Mix(background, ink, 0.12f);
        _configure.FlatAppearance.MouseDownBackColor = Theme.Mix(background, ink, 0.18f);
        _start.BackColor = background;
        _start.LinkColor = _start.VisitedLinkColor = secondary;
        _start.ActiveLinkColor = ink;
        _start.DisabledLinkColor = secondary;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        // Theme changes can alter text colours while the workspace stays the same.
        RefreshAppearance();
        base.OnPaint(e);
        var scale = DeviceDpi / 96f;
        var side = (int)MathF.Round(36 * scale);
        SvgIcons.Draw(e.Graphics, SvgIconKind.Image,
            new Rectangle((Width - side) / 2, (int)MathF.Round(16 * scale), side, side),
            Theme.ReadableUiColor(BackColor, Theme.Mix(BackColor, _title.ForeColor, 0.62f)));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _backdrop is not null) _backdrop.BackColorChanged -= BackdropChanged;
        base.Dispose(disposing);
    }
}
