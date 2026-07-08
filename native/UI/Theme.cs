namespace VectorAnimationEngine;

internal static class Theme
{
    public static readonly Color App = Color.FromArgb(21, 23, 25);
    public static readonly Color Top = Color.FromArgb(25, 28, 31);
    public static readonly Color Panel = Color.FromArgb(32, 35, 38);
    public static readonly Color PanelStrong = Color.FromArgb(44, 49, 53);
    public static readonly Color Field = Color.FromArgb(18, 20, 22);
    public static readonly Color Stage = Color.FromArgb(17, 19, 21);
    public static readonly Color Border = Color.FromArgb(76, 84, 90);
    public static readonly Color Text = Color.FromArgb(242, 246, 245);
    public static readonly Color Muted = Color.FromArgb(190, 202, 202);
    public static readonly Color Accent = Color.FromArgb(79, 179, 162);
    public static readonly Color AccentText = Color.FromArgb(5, 22, 20);

    public static Font UiFont(float size = 9.5f, FontStyle style = FontStyle.Regular) => new("Segoe UI", size, style);

    public static void StyleButton(Button button)
    {
        button.UseVisualStyleBackColor = false;
        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = PanelStrong;
        button.ForeColor = Text;
        button.FlatAppearance.BorderColor = Border;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(56, 64, 68);
        button.FlatAppearance.MouseDownBackColor = Accent;
        button.Font = UiFont();
    }

    public static void StyleActiveButton(Button button)
    {
        StyleButton(button);
        button.BackColor = Accent;
        button.ForeColor = AccentText;
        button.FlatAppearance.BorderColor = Accent;
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
