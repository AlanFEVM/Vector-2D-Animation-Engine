using System.Globalization;

namespace VectorAnimationEngine;

internal sealed class WorkspaceColorFlyout : ToolStripDropDown
{
    private enum CompletionKind
    {
        None,
        Apply,
        Cancel
    }

    private readonly WorkspaceColorPickerPanel _picker = new();
    private readonly ToolStripControlHost _host;
    private CompletionKind _completion;
    private bool _completionRaised;

    public WorkspaceColorFlyout()
    {
        AutoClose = true;
        AutoSize = false;
        BackColor = Theme.PanelStrong;
        Padding = new Padding(1);
        Renderer = new ToolStripProfessionalRenderer(new FlyoutColorTable());
        AccessibleName = "Workspace color picker";

        _host = new ToolStripControlHost(_picker)
        {
            AutoSize = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            Size = _picker.Size
        };
        Items.Add(_host);
        Size = new Size(_picker.Width + Padding.Horizontal, _picker.Height + Padding.Vertical);

        _picker.ColorChanged += HandleColorChanged;
        _picker.ApplyRequested += HandleApplyRequested;
        _picker.CancelRequested += HandleCancelRequested;
        UiLocalization.Watch(_picker);
    }

    public Color Color => _picker.Color;

    public Color InitialColor => _picker.InitialColor;

    public event EventHandler? ColorPreviewChanged;

    public event EventHandler? ColorApplied;

    public event EventHandler? ColorCanceled;

    public void ShowFor(Control anchor, Color initialColor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        if (anchor.IsDisposed) throw new ObjectDisposedException(anchor.Name);

        if (Visible) Close(ToolStripDropDownCloseReason.CloseCalled);

        _completion = CompletionKind.None;
        _completionRaised = false;
        _picker.SetInitialColor(initialColor);

        var workingArea = Screen.FromControl(anchor).WorkingArea;
        var anchorTopLeft = anchor.PointToScreen(Point.Empty);
        var location = new Point(anchorTopLeft.X, anchorTopLeft.Y + anchor.Height);
        if (location.X + Width > workingArea.Right) location.X = workingArea.Right - Width;
        if (location.X < workingArea.Left) location.X = workingArea.Left;
        if (location.Y + Height > workingArea.Bottom) location.Y = anchorTopLeft.Y - Height;
        if (location.Y < workingArea.Top) location.Y = workingArea.Top;

        Show(location);
        _picker.FocusFirstControl();
    }

    protected override bool ProcessCmdKey(ref Message m, Keys keyData)
    {
        if ((keyData & Keys.KeyCode) == Keys.Escape)
        {
            RequestCompletion(CompletionKind.Cancel);
            return true;
        }

        return base.ProcessCmdKey(ref m, keyData);
    }

    protected override void OnClosed(ToolStripDropDownClosedEventArgs e)
    {
        base.OnClosed(e);
        if (_completion == CompletionKind.None) _completion = CompletionKind.Cancel;
        RaiseCompletionOnce();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _picker.ColorChanged -= HandleColorChanged;
            _picker.ApplyRequested -= HandleApplyRequested;
            _picker.CancelRequested -= HandleCancelRequested;
        }

        base.Dispose(disposing);
    }

    private void HandleColorChanged(object? sender, EventArgs e)
    {
        if (_completion == CompletionKind.None) ColorPreviewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void HandleApplyRequested(object? sender, EventArgs e) => RequestCompletion(CompletionKind.Apply);

    private void HandleCancelRequested(object? sender, EventArgs e) => RequestCompletion(CompletionKind.Cancel);

    private void RequestCompletion(CompletionKind completion)
    {
        if (_completion != CompletionKind.None || _completionRaised) return;
        _completion = completion;
        if (Visible) Close(ToolStripDropDownCloseReason.CloseCalled);
        else RaiseCompletionOnce();
    }

    private void RaiseCompletionOnce()
    {
        if (_completionRaised) return;
        _completionRaised = true;
        if (_completion == CompletionKind.Apply)
        {
            ColorApplied?.Invoke(this, EventArgs.Empty);
            return;
        }

        _picker.RestoreInitialColor();
        ColorCanceled?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FlyoutColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.PanelStrong;
        public override Color ToolStripBorder => Theme.BorderHover;
    }
}

internal sealed class WorkspaceColorPickerPanel : UserControl
{
    public const int PreferredPanelWidth = 360;
    public const int PreferredPanelHeight = 400;

    private static readonly Color[] PresetColors =
    [
        Color.FromArgb(239, 83, 80),
        Color.FromArgb(255, 167, 38),
        Color.FromArgb(255, 238, 88),
        Color.FromArgb(102, 187, 106),
        Color.FromArgb(38, 198, 218),
        Color.FromArgb(66, 165, 245),
        Color.FromArgb(126, 87, 194),
        Color.FromArgb(236, 64, 122),
        Color.FromArgb(238, 238, 238),
        Color.FromArgb(144, 164, 174),
        Color.FromArgb(84, 110, 122),
        Color.FromArgb(38, 50, 56)
    ];

    private readonly HsvColorPlane _plane = new();
    private readonly ColorComponentSlider _hue = new();
    private readonly ModernNumericUpDown _red = CreateChannelInput("Red channel");
    private readonly ModernNumericUpDown _green = CreateChannelInput("Green channel");
    private readonly ModernNumericUpDown _blue = CreateChannelInput("Blue channel");
    private readonly TextBox _hex = new();
    private readonly ColorPaletteGrid _presets = new();
    private readonly Panel _originalPreview = new();
    private readonly Panel _newPreview = new();
    private readonly Button _applyButton = new() { Text = "Apply" };
    private bool _updating;
    private Color _initialColor = Theme.Stage;
    private Color _color = Theme.Stage;

    public WorkspaceColorPickerPanel()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = false;
        BackColor = Theme.PanelStrong;
        ForeColor = Theme.Text;
        Font = Theme.UiFont();
        Padding = new Padding(12);
        Size = new Size(PreferredPanelWidth, PreferredPanelHeight);
        MinimumSize = Size;
        MaximumSize = Size;
        AccessibleName = "Workspace color picker";
        AccessibleDescription = "Choose a workspace color using HSV, RGB, hex, or preset colors";
        AccessibleRole = AccessibleRole.Pane;

        BuildUi();
        WireEvents();
        SetInitialColor(_initialColor);
    }

    public Color Color => _color;

    public Color InitialColor => _initialColor;

    public event EventHandler? ColorChanged;

    internal event EventHandler? ApplyRequested;

    internal event EventHandler? CancelRequested;

    public override Size GetPreferredSize(Size proposedSize) => new(PreferredPanelWidth, PreferredPanelHeight);

    internal void SetInitialColor(Color color)
    {
        _initialColor = Opaque(color);
        SetColor(_initialColor, raiseChanged: false);
        _originalPreview.BackColor = _initialColor;
    }

    internal void RestoreInitialColor() => SetColor(_initialColor, raiseChanged: false);

    internal void FocusFirstControl()
    {
        _plane.Focus();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if ((keyData & Keys.KeyCode) == Keys.Escape)
        {
            CancelRequested?.Invoke(this, EventArgs.Empty);
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void BuildUi()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.PanelStrong,
            ColumnCount = 1,
            RowCount = 7,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 146));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        Controls.Add(layout);

        layout.Controls.Add(BuildPreviewRow(), 0, 0);

        _plane.Dock = DockStyle.Fill;
        _plane.Margin = new Padding(0, 6, 0, 6);
        _plane.BackColor = Theme.PanelStrong;
        _plane.AccessibleDescription = "Choose color saturation and value";
        layout.Controls.Add(_plane, 0, 1);

        var hueRow = CreateLabeledRow("Hue", 42);
        _hue.Dock = DockStyle.Fill;
        _hue.Margin = new Padding(0, 2, 0, 2);
        _hue.Minimum = 0;
        _hue.Maximum = 359;
        _hue.AccessibleName = "Hue";
        _hue.AccessibleDescription = "Color hue in degrees";
        _hue.GradientColor = ratio => HsvToColor(ratio * 359f, 1f, 1f);
        hueRow.Controls.Add(_hue, 1, 0);
        layout.Controls.Add(hueRow, 0, 2);

        layout.Controls.Add(BuildChannelRow(), 0, 3);

        var hexRow = CreateLabeledRow("Hex", 42);
        _hex.Dock = DockStyle.Fill;
        _hex.Margin = new Padding(0, 4, 0, 4);
        _hex.MaxLength = 7;
        _hex.CharacterCasing = CharacterCasing.Upper;
        _hex.AccessibleName = "Hex color";
        _hex.AccessibleDescription = "Six digit RGB color beginning with a number sign";
        Theme.StyleTextBox(_hex);
        hexRow.Controls.Add(_hex, 1, 0);
        layout.Controls.Add(hexRow, 0, 4);

        var presetRow = CreateLabeledRow("Presets", 52);
        _presets.Dock = DockStyle.Fill;
        _presets.Margin = new Padding(0, 5, 0, 5);
        _presets.BackColor = Theme.PanelStrong;
        _presets.SwatchSize = 20;
        _presets.Gap = 4;
        _presets.AccessibleName = "Color presets";
        _presets.SetColors(PresetColors);
        presetRow.Controls.Add(_presets, 1, 0);
        layout.Controls.Add(presetRow, 0, 5);

        layout.Controls.Add(BuildActionRow(), 0, 6);
    }

    private Control BuildPreviewRow()
    {
        var previews = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.PanelStrong,
            ColumnCount = 2,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        previews.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        previews.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        previews.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        previews.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        previews.Controls.Add(CreateLabel("Original color"), 0, 0);
        previews.Controls.Add(CreateLabel("New color"), 1, 0);
        previews.Controls.Add(ConfigurePreview(_originalPreview, "Original workspace color"), 0, 1);
        previews.Controls.Add(ConfigurePreview(_newPreview, "New workspace color"), 1, 1);
        return previews;
    }

    private Control BuildChannelRow()
    {
        var channels = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.PanelStrong,
            ColumnCount = 6,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        for (var index = 0; index < 3; index++)
        {
            channels.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 18));
            channels.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333f));
        }

        AddChannel(channels, "R", _red, 0);
        AddChannel(channels, "G", _green, 2);
        AddChannel(channels, "B", _blue, 4);
        return channels;
    }

    private Control BuildActionRow()
    {
        var actions = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.PanelStrong,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = new Padding(0, 6, 0, 0)
        };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333f));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333f));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333f));

        var reset = CreateActionButton("Reset", "Restore the original workspace color");
        var cancel = CreateActionButton("Cancel", "Cancel workspace color changes");
        _applyButton.Text = "Apply";
        ConfigureActionButton(_applyButton, "Apply workspace color");
        Theme.StylePrimaryButton(_applyButton);
        reset.Click += (_, _) => SetColor(_initialColor);
        cancel.Click += (_, _) => CancelRequested?.Invoke(this, EventArgs.Empty);
        _applyButton.Click += (_, _) =>
        {
            if (TryCommitHex(showError: true)) ApplyRequested?.Invoke(this, EventArgs.Empty);
        };

        actions.Controls.Add(reset, 0, 0);
        actions.Controls.Add(cancel, 1, 0);
        actions.Controls.Add(_applyButton, 2, 0);
        return actions;
    }

    private void WireEvents()
    {
        _plane.ColorChanged += (_, _) =>
        {
            if (!_updating) SetColor(HsvToColor(_plane.Hue, _plane.Saturation, _plane.Value));
        };
        _hue.ValueChanged += (_, _) =>
        {
            if (!_updating) SetColor(HsvToColor(_hue.Value, _plane.Saturation, _plane.Value));
        };
        _red.ValueChanged += (_, _) => SetFromChannels();
        _green.ValueChanged += (_, _) => SetFromChannels();
        _blue.ValueChanged += (_, _) => SetFromChannels();
        _hex.TextChanged += (_, _) =>
        {
            if (!_updating) TryCommitHex(showError: false);
        };
        _hex.Validated += (_, _) => TryCommitHex(showError: true);
        _hex.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            TryCommitHex(showError: true);
            e.Handled = true;
            e.SuppressKeyPress = true;
        };
        _presets.ColorSelected += (_, e) => SetColor(e.Color);
    }

    private void SetFromChannels()
    {
        if (_updating) return;
        SetColor(Color.FromArgb((int)_red.Value, (int)_green.Value, (int)_blue.Value));
    }

    private bool TryCommitHex(bool showError)
    {
        var text = _hex.Text.Trim();
        if (text.StartsWith('#')) text = text[1..];
        if (text.Length == 6
            && int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            _hex.BackColor = Theme.Field;
            SetColor(Color.FromArgb((rgb >> 16) & 0xff, (rgb >> 8) & 0xff, rgb & 0xff));
            return true;
        }

        if (showError)
        {
            _hex.BackColor = Theme.Mix(Theme.Field, Theme.Danger, 0.22f);
            _hex.SelectAll();
            _hex.Focus();
        }
        return false;
    }

    private void SetColor(Color color, bool raiseChanged = true)
    {
        color = Opaque(color);
        if (_color.ToArgb() == color.ToArgb() && raiseChanged) return;

        _color = color;
        ColorToHsv(color, out var hue, out var saturation, out var value);
        _updating = true;
        try
        {
            _plane.SetHsv(hue, saturation, value);
            _hue.Value = (int)MathF.Round(hue) % 360;
            _red.Value = color.R;
            _green.Value = color.G;
            _blue.Value = color.B;
            var hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            if (!string.Equals(_hex.Text, hex, StringComparison.Ordinal)) _hex.Text = hex;
            _hex.BackColor = Theme.Field;
            _originalPreview.BackColor = _initialColor;
            _newPreview.BackColor = color;
            _newPreview.AccessibleDescription = hex;
            _presets.SelectedColor = color;
        }
        finally
        {
            _updating = false;
        }

        if (raiseChanged) ColorChanged?.Invoke(this, EventArgs.Empty);
    }

    private static TableLayoutPanel CreateLabeledRow(string label, int labelWidth)
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.PanelStrong,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, labelWidth));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.Controls.Add(CreateLabel(label), 0, 0);
        return row;
    }

    private static ModernNumericUpDown CreateChannelInput(string accessibleName)
    {
        return new ModernNumericUpDown
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 5, 4, 5),
            Minimum = 0,
            Maximum = 255,
            Increment = 1,
            DecimalPlaces = 0,
            AccessibleName = accessibleName
        };
    }

    private static Label CreateLabel(string text)
    {
        return new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.Muted,
            BackColor = Theme.PanelStrong,
            Margin = Padding.Empty,
            UseMnemonic = false
        };
    }

    private static Control ConfigurePreview(Panel preview, string accessibleName)
    {
        preview.Dock = DockStyle.Fill;
        preview.Margin = new Padding(0, 3, 8, 4);
        preview.BorderStyle = BorderStyle.FixedSingle;
        preview.AccessibleName = accessibleName;
        preview.AccessibleRole = AccessibleRole.Graphic;
        return preview;
    }

    private static void AddChannel(TableLayoutPanel layout, string label, Control input, int column)
    {
        layout.Controls.Add(CreateLabel(label), column, 0);
        layout.Controls.Add(input, column + 1, 0);
    }

    private static Button CreateActionButton(string text, string accessibleDescription)
    {
        var button = new Button { Text = text };
        ConfigureActionButton(button, accessibleDescription);
        Theme.StyleButton(button);
        return button;
    }

    private static void ConfigureActionButton(Button button, string accessibleDescription)
    {
        button.Dock = DockStyle.Fill;
        button.Margin = new Padding(0, 0, 6, 0);
        button.AccessibleName = button.Text;
        button.AccessibleDescription = accessibleDescription;
        button.AccessibleRole = AccessibleRole.PushButton;
    }

    private static Color Opaque(Color color) => Color.FromArgb(255, color.R, color.G, color.B);

    private static void ColorToHsv(Color color, out float hue, out float saturation, out float value)
    {
        hue = color.GetHue();
        var max = Math.Max(color.R, Math.Max(color.G, color.B)) / 255f;
        var min = Math.Min(color.R, Math.Min(color.G, color.B)) / 255f;
        value = max;
        saturation = max <= 0.0001f ? 0f : (max - min) / max;
    }

    private static Color HsvToColor(float hue, float saturation, float value)
    {
        hue = (hue % 360f + 360f) % 360f;
        saturation = Math.Clamp(saturation, 0f, 1f);
        value = Math.Clamp(value, 0f, 1f);
        var chroma = value * saturation;
        var secondary = chroma * (1f - MathF.Abs(hue / 60f % 2f - 1f));
        var offset = value - chroma;
        var (red, green, blue) = hue switch
        {
            < 60f => (chroma, secondary, 0f),
            < 120f => (secondary, chroma, 0f),
            < 180f => (0f, chroma, secondary),
            < 240f => (0f, secondary, chroma),
            < 300f => (secondary, 0f, chroma),
            _ => (chroma, 0f, secondary)
        };
        return Color.FromArgb(
            (int)MathF.Round((red + offset) * 255f),
            (int)MathF.Round((green + offset) * 255f),
            (int)MathF.Round((blue + offset) * 255f));
    }
}
