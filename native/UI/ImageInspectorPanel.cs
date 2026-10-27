namespace VectorAnimationEngine;

internal sealed record ImageInspectorState(string AssetId, string Summary);
internal sealed class ImageSettingsRequestedEventArgs(string assetId) : EventArgs
{
    public string AssetId { get; } = assetId;
}

/// <summary>Displays an image snapshot and emits intent; the host owns asset lookup and editing.</summary>
internal sealed class ImageInspectorPanel : UserControl
{
    private readonly Label _summary = new ImageSummaryLabel
    {
        Dock = DockStyle.Fill, AutoEllipsis = true, Margin = Padding.Empty,
        ForeColor = Theme.Text, BackColor = Theme.Panel, Font = Theme.UiFont()
    };
    private readonly SvgIconButton _edit = new(SvgIconKind.Settings)
    {
        Dock = DockStyle.Fill, Margin = Padding.Empty, AutoEllipsis = true,
        AccessibleName = "Image Import Settings"
    };
    private readonly ToolTip _toolTip = new() { ShowAlways = true, AutoPopDelay = 15000 };
    private ImageInspectorState? _state;

    public ImageInspectorPanel()
    {
        BackColor = Theme.Panel;
        Padding = Theme.InspectorSectionPadding;
        Height = PreferredHeight;
        AccessibleRole = AccessibleRole.Pane;
        AccessibleName = UiLocalization.T("Image Import Settings");
        Theme.StyleStandardButton(_edit);
        Theme.StyleToolTip(_toolTip);
        _edit.Click += (_, _) =>
        {
            if (_state is { } state) SettingsRequested?.Invoke(this, new(state.AssetId));
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = Theme.Panel, ColumnCount = 1, RowCount = 4,
            Margin = Padding.Empty, Padding = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.InspectorTitleHeight));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.GapSm));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.ControlHeight));
        layout.Controls.Add(InspectorModuleStyle.CreateTitle("Image Import Settings"), 0, 0);
        layout.Controls.Add(_summary, 0, 1);
        layout.Controls.Add(_edit, 0, 3);
        Controls.Add(layout);
        SetState(null);
        UiLocalization.Watch(this);
    }

    public int PreferredHeight => Theme.InspectorSectionPaddingVertical * 2
        + Theme.InspectorTitleHeight + 8 * ImageSummaryLabel.RowHeight
        + Theme.GapSm + Theme.ControlHeight;
    public event EventHandler<ImageSettingsRequestedEventArgs>? SettingsRequested;

    public void SetState(ImageInspectorState? state)
    {
        _state = state;
        if (_summary.Text != (state?.Summary ?? "")) _summary.Text = state?.Summary ?? "";
        _summary.AccessibleDescription = _summary.Text;
        _toolTip.SetToolTip(_summary, _summary.Text);
        _edit.Text = UiLocalization.T("Image Import Settings");
        _edit.Enabled = state is not null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _toolTip.Dispose();
        base.Dispose(disposing);
    }

    private sealed class ImageSummaryLabel : Label
    {
        public const int RowHeight = 18;

        protected override void OnPaint(PaintEventArgs e)
        {
            var rowHeight = Math.Max(Font.Height, (int)Math.Round(RowHeight * DeviceDpi / 96f));
            var foreground = SystemInformation.HighContrast
                ? Enabled ? SystemColors.ControlText : SystemColors.GrayText
                : Theme.ReadableText(BackColor, Enabled ? Theme.Text : Theme.DisabledText);
            var lines = Text.Replace("\r", string.Empty).Split('\n');
            for (var index = 0; index < lines.Length; index++)
            {
                var top = index * rowHeight;
                if (top + rowHeight > ClientSize.Height) break;
                TextRenderer.DrawText(e.Graphics, lines[index], Font,
                    new Rectangle(0, top, ClientSize.Width, rowHeight), foreground,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
                    | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding
                    | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform);
            }
        }
    }
}
