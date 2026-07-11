namespace VectorAnimationEngine;

internal static class Theme
{
    public static readonly Color App = Color.FromArgb(18, 20, 22);
    public static readonly Color Top = Color.FromArgb(24, 27, 30);
    public static readonly Color Panel = Color.FromArgb(30, 34, 37);
    public static readonly Color PanelStrong = Color.FromArgb(42, 48, 53);
    public static readonly Color PanelHover = Color.FromArgb(55, 63, 68);
    public static readonly Color Field = Color.FromArgb(15, 17, 19);
    public static readonly Color Stage = Color.FromArgb(13, 15, 17);
    public static readonly Color Border = Color.FromArgb(61, 69, 76);
    public static readonly Color Text = Color.FromArgb(242, 246, 245);
    public static readonly Color Muted = Color.FromArgb(190, 202, 202);
    public static readonly Color Accent = Color.FromArgb(79, 179, 162);
    public static readonly Color AccentSurface = Color.FromArgb(39, 83, 77);
    public static readonly Color AccentHoverSurface = Color.FromArgb(51, 107, 99);
    public static readonly Color AccentPressedSurface = Color.FromArgb(31, 68, 63);
    public static readonly Color DisabledSurface = Color.FromArgb(35, 39, 43);
    public static readonly Color AccentText = Color.FromArgb(5, 22, 20);
    public static readonly Color AccentLabel = Color.FromArgb(209, 247, 238);

    public static Font UiFont(float size = 9.5f, FontStyle style = FontStyle.Regular) => new("Segoe UI", size, style);

    public static void StyleButton(Button button)
    {
        StyleButton(button, active: false);
    }

    public static void StyleActiveButton(Button button)
    {
        StyleButton(button, active: true);
    }

    public static Color Mix(Color from, Color to, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        return Color.FromArgb(
            (int)Math.Round(from.A + (to.A - from.A) * amount),
            (int)Math.Round(from.R + (to.R - from.R) * amount),
            (int)Math.Round(from.G + (to.G - from.G) * amount),
            (int)Math.Round(from.B + (to.B - from.B) * amount));
    }

    private static void StyleButton(Button button, bool active)
    {
        button.UseVisualStyleBackColor = false;
        button.FlatStyle = FlatStyle.Flat;
        button.ForeColor = active ? AccentLabel : Text;
        button.FlatAppearance.BorderColor = active ? Accent : Border;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.MouseOverBackColor = active ? AccentSurface : PanelStrong;
        button.FlatAppearance.MouseDownBackColor = active ? AccentSurface : PanelStrong;
        button.Font = UiFont();
        button.TextAlign = ContentAlignment.MiddleCenter;
        UiMotion.ConfigureButton(
            button,
            active ? AccentSurface : PanelStrong,
            active ? AccentHoverSurface : PanelHover,
            active ? AccentPressedSurface : Panel,
            active);
    }

    public static void StyleTextBox(TextBox box)
    {
        box.BackColor = Field;
        box.ForeColor = Text;
        box.BorderStyle = BorderStyle.FixedSingle;
        box.Font = UiFont();
    }

    public static void StyleComboBox(ComboBox box)
    {
        box.BackColor = Field;
        box.ForeColor = Text;
        box.FlatStyle = FlatStyle.Flat;
        box.Font = UiFont();
    }

    public static void StyleNumeric(NumericUpDown input)
    {
        input.BackColor = Field;
        input.ForeColor = Text;
        input.BorderStyle = BorderStyle.FixedSingle;
        input.Font = UiFont();
    }

    public static Label Label(string text, int left, int top, int width, Color color, Font? font = null)
    {
        return new Label
        {
            Text = text,
            Left = left,
            Top = top,
            Width = width,
            Height = 24,
            ForeColor = color,
            BackColor = Color.Transparent,
            Font = font ?? UiFont(),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };
    }
}
