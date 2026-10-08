namespace VectorAnimationEngine;

internal sealed record ImageInspectorState(string AssetId, string Summary);
internal sealed class ImageSettingsRequestedEventArgs(string assetId) : EventArgs
{
    public string AssetId { get; } = assetId;
}

/// <summary>Displays an image snapshot and emits intent; the host owns asset lookup and editing.</summary>
internal sealed class ImageInspectorPanel : UserControl
{
    private readonly Label _summary = new()
    {
        Dock = DockStyle.Fill, AutoEllipsis = true, Padding = new Padding(8),
        ForeColor = Theme.Text, BackColor = Theme.Panel, Font = Theme.UiFont()
    };
    private readonly Button _edit = new() { Dock = DockStyle.Bottom, Height = Theme.ControlHeightCompact };
    private ImageInspectorState? _state;

    public ImageInspectorPanel()
    {
        BackColor = Theme.Panel;
        Height = PreferredHeight;
        AccessibleRole = AccessibleRole.Pane;
        AccessibleName = UiLocalization.T("Image Import Settings");
        Theme.StyleButton(_edit);
        _edit.Click += (_, _) =>
        {
            if (_state is { } state) SettingsRequested?.Invoke(this, new(state.AssetId));
        };
        Controls.Add(_summary);
        Controls.Add(_edit);
        _edit.SendToBack();
        SetState(null);
    }

    public int PreferredHeight => 300;
    public event EventHandler<ImageSettingsRequestedEventArgs>? SettingsRequested;

    public void SetState(ImageInspectorState? state)
    {
        _state = state;
        if (_summary.Text != (state?.Summary ?? "")) _summary.Text = state?.Summary ?? "";
        _edit.Text = UiLocalization.T("Image Import Settings");
        _edit.Enabled = state is not null;
    }
}
