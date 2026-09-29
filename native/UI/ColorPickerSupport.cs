using System.Globalization;

namespace VectorAnimationEngine;

// Shared arithmetic and inputs only; each host keeps its own layout and event lifecycle.
internal static class ColorPickerSupport
{
    public static IReadOnlyList<Color> PresetColors { get; } = Array.AsReadOnly<Color>(
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
    ]);

    public static Color Opaque(Color color) => Color.FromArgb(255, color.R, color.G, color.B);

    public static void ColorToHsv(Color color, out float hue, out float saturation, out float value)
    {
        hue = color.GetHue();
        var max = Math.Max(color.R, Math.Max(color.G, color.B)) / 255f;
        var min = Math.Min(color.R, Math.Min(color.G, color.B)) / 255f;
        value = max;
        saturation = max <= 0.0001f ? 0f : (max - min) / max;
    }

    public static Color HsvToColor(float hue, float saturation, float value)
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

    public static bool TryParseRgb(string text, out Color color)
    {
        text = text.Trim();
        if (text.StartsWith('#')) text = text[1..];
        if (text.Length == 6
            && int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            color = Color.FromArgb((rgb >> 16) & 0xff, (rgb >> 8) & 0xff, rgb & 0xff);
            return true;
        }

        color = default;
        return false;
    }

    public static ModernNumericUpDown CreateChannelInput(string accessibleName, Padding margin)
    {
        return new ModernNumericUpDown
        {
            Dock = DockStyle.Fill,
            Margin = margin,
            Minimum = 0,
            Maximum = 255,
            Increment = 1,
            DecimalPlaces = 0,
            AccessibleName = accessibleName
        };
    }
}
