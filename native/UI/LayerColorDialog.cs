using System.Globalization;

namespace VectorAnimationEngine;

internal sealed class LayerColorDialog : ModernDialogForm
{
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

    private readonly Color _initialColor;
    private readonly HsvColorPlane _plane = new();
    private readonly ColorComponentSlider _hue = new();
    private readonly ModernNumericUpDown _red = CreateChannelInput("Red channel");
    private readonly ModernNumericUpDown _green = CreateChannelInput("Green channel");
    private readonly ModernNumericUpDown _blue = CreateChannelInput("Blue channel");
    private readonly TextBox _hex = new();
    private readonly ColorPaletteGrid _presets = new();
    private readonly Panel _originalPreview = new();
    private readonly Panel _currentPreview = new();
    private bool _updating;
    private Color _color;

    public LayerColorDialog(Color color, string title = "Layer Color")
        : base(title, new Size(446, 462))
    {
        _initialColor = Opaque(color);
        _color = _initialColor;
        MinimumSize = new Size(390, 430);

        BuildUi();
        WireEvents();
        SetColor(_initialColor, raiseChanged: false);

        var apply = AddDialogAction("Apply", DialogResult.OK, DialogActionStyle.Primary, () => TryCommitHex());
        var cancel = AddDialogAction("Cancel", DialogResult.Cancel);
        AcceptButton = apply;
        CancelButton = cancel;
        UiLocalization.Watch(this);
    }

    public Color Color => _color;

    public event EventHandler? ColorChanged;

    private void BuildUi()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 1,
            RowCount = 6,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        DialogContent.Controls.Add(layout);

        var previews = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Margin = Padding.Empty,
            BackColor = Theme.Panel
        };
        previews.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        previews.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        previews.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        previews.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        previews.Controls.Add(CreateLabel("Original"), 0, 0);
        previews.Controls.Add(CreateLabel("New"), 1, 0);
        previews.Controls.Add(ConfigurePreview(_originalPreview, "Original color"), 0, 1);
        previews.Controls.Add(ConfigurePreview(_currentPreview, "New color"), 1, 1);
        layout.Controls.Add(previews, 0, 0);

        _plane.Dock = DockStyle.Fill;
        _plane.Margin = new Padding(0, 8, 0, 8);
        _plane.AccessibleDescription = "Choose color saturation and brightness";
        layout.Controls.Add(_plane, 0, 1);

        _hue.Dock = DockStyle.Fill;
        _hue.Margin = new Padding(0, 4, 0, 4);
        _hue.Minimum = 0;
        _hue.Maximum = 359;
        _hue.AccessibleName = "Hue";
        _hue.AccessibleDescription = "Color hue in degrees";
        _hue.GradientColor = ratio => HsvToColor(ratio * 359f, 1f, 1f);
        layout.Controls.Add(_hue, 0, 2);

        var channels = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 6,
            RowCount = 1,
            Margin = Padding.Empty,
            BackColor = Theme.Panel
        };
        for (var index = 0; index < 3; index++)
        {
            channels.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 24));
            channels.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333f));
        }
        AddChannel(channels, "R", _red, 0);
        AddChannel(channels, "G", _green, 2);
        AddChannel(channels, "B", _blue, 4);
        layout.Controls.Add(channels, 0, 3);

        var hexRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty,
            BackColor = Theme.Panel
        };
        hexRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));
        hexRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        hexRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
        hexRow.Controls.Add(CreateLabel("Hex"), 0, 0);
        _hex.Dock = DockStyle.Fill;
        _hex.Margin = new Padding(0, 4, 8, 4);
        _hex.MaxLength = 7;
        _hex.CharacterCasing = CharacterCasing.Upper;
        _hex.AccessibleName = "Hex color";
        Theme.StyleTextBox(_hex);
        hexRow.Controls.Add(_hex, 1, 0);
        var reset = new Button { Text = "Reset", Dock = DockStyle.Fill, Margin = new Padding(0, 3, 0, 3) };
        Theme.StyleButton(reset);
        reset.Click += (_, _) => SetColor(_initialColor);
        hexRow.Controls.Add(reset, 2, 0);
        layout.Controls.Add(hexRow, 0, 4);

        _presets.Dock = DockStyle.Fill;
        _presets.Margin = new Padding(0, 5, 0, 0);
        _presets.SwatchSize = 22;
        _presets.Gap = 6;
        _presets.AccessibleName = "Color presets";
        _presets.SetColors(PresetColors);
        layout.Controls.Add(_presets, 0, 5);
    }

    private void WireEvents()
    {
        _plane.ColorChanged += (_, _) =>
        {
            if (_updating) return;
            SetColor(HsvToColor(_plane.Hue, _plane.Saturation, _plane.Value));
        };
        _hue.ValueChanged += (_, _) =>
        {
            if (_updating) return;
            SetColor(HsvToColor(_hue.Value, _plane.Saturation, _plane.Value));
        };
        _red.ValueChanged += (_, _) => SetFromChannels();
        _green.ValueChanged += (_, _) => SetFromChannels();
        _blue.ValueChanged += (_, _) => SetFromChannels();
        _hex.Validated += (_, _) => TryCommitHex();
        _hex.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            TryCommitHex();
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

    private bool TryCommitHex()
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

        _hex.BackColor = Theme.Mix(Theme.Field, Theme.Danger, 0.22f);
        _hex.SelectAll();
        _hex.Focus();
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
            _currentPreview.BackColor = color;
            _presets.SelectedColor = color;
        }
        finally
        {
            _updating = false;
        }

        if (raiseChanged) ColorChanged?.Invoke(this, EventArgs.Empty);
    }

    private static ModernNumericUpDown CreateChannelInput(string accessibleName)
    {
        return new ModernNumericUpDown
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 4, 8, 4),
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
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.Muted,
            BackColor = Theme.Panel,
            Margin = new Padding(0, 0, 8, 0)
        };
    }

    private static Control ConfigurePreview(Panel preview, string accessibleName)
    {
        preview.Dock = DockStyle.Fill;
        preview.Margin = new Padding(0, 5, 12, 5);
        preview.BorderStyle = BorderStyle.FixedSingle;
        preview.AccessibleName = accessibleName;
        return preview;
    }

    private static void AddChannel(TableLayoutPanel layout, string label, Control input, int column)
    {
        layout.Controls.Add(CreateLabel(label), column, 0);
        layout.Controls.Add(input, column + 1, 0);
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
