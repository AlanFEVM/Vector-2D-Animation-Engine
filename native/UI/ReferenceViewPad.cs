namespace VectorAnimationEngine;

internal sealed class ReferenceViewRequestedEventArgs(ReferenceViewDirection direction) : EventArgs
{
    public ReferenceViewDirection Direction { get; } = direction;
}

internal sealed class ReferenceViewPad : UserControl
{
    public const int PreferredPadSize = 104;

    private readonly ToolTip _toolTip = new();
    private readonly TableLayoutPanel _layout = new()
    {
        Dock = DockStyle.Fill,
        BackColor = Theme.Stage,
        ColumnCount = 3,
        RowCount = 3,
        Margin = Padding.Empty,
        Padding = new Padding(1)
    };

    public ReferenceViewPad()
    {
        AutoSize = false;
        BackColor = Theme.Stage;
        Size = new Size(PreferredPadSize, PreferredPadSize);
        MinimumSize = Size;
        MaximumSize = Size;
        AccessibleName = "Reference view controls";
        AccessibleRole = AccessibleRole.Grouping;

        for (var index = 0; index < 3; index++)
        {
            _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3f));
            _layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / 3f));
        }

        AddViewButton(SvgIconKind.ChevronUp, "Top view", ReferenceViewDirection.Top, 1, 0, 0);
        AddViewButton(SvgIconKind.ChevronLeft, "Left view", ReferenceViewDirection.Left, 0, 1, 1);
        AddViewButton(SvgIconKind.FrontView, "Front view", ReferenceViewDirection.Front, 1, 1, 2);
        AddViewButton(SvgIconKind.ChevronRight, "Right view", ReferenceViewDirection.Right, 2, 1, 3);
        AddViewButton(SvgIconKind.ChevronDown, "Bottom view", ReferenceViewDirection.Bottom, 1, 2, 4);

        Theme.StyleToolTip(_toolTip);
        Controls.Add(_layout);
    }

    public event EventHandler<ReferenceViewRequestedEventArgs>? ViewRequested;

    protected override void OnBackColorChanged(EventArgs e)
    {
        base.OnBackColorChanged(e);
        if (_layout is not null) _layout.BackColor = BackColor;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _toolTip.Dispose();
        base.Dispose(disposing);
    }

    private void AddViewButton(
        SvgIconKind icon,
        string accessibleName,
        ReferenceViewDirection direction,
        int column,
        int row,
        int tabIndex)
    {
        var button = new SvgIconButton(icon)
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(1),
            AccessibleName = accessibleName,
            AccessibleDescription = $"Switch canvas to {accessibleName.ToLowerInvariant()}",
            TabIndex = tabIndex
        };
        Theme.StyleStandardButton(button);
        _toolTip.SetToolTip(button, accessibleName);
        button.Click += (_, _) => ViewRequested?.Invoke(this, new ReferenceViewRequestedEventArgs(direction));
        _layout.Controls.Add(button, column, row);
    }
}
