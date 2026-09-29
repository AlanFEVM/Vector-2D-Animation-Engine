namespace VectorAnimationEngine;

/// <summary>Shared drawing primitives used by owner-drawn color controls.</summary>
internal static class UiDrawingHelpers
{
    internal static void DrawCheckerboard(Graphics graphics, Rectangle bounds, int square, Color lightColor, Color darkColor)
    {
        using var light = new SolidBrush(lightColor);
        using var dark = new SolidBrush(darkColor);
        for (var y = bounds.Top; y < bounds.Bottom; y += square)
        {
            for (var x = bounds.Left; x < bounds.Right; x += square)
            {
                var alternate = ((x - bounds.Left) / square + (y - bounds.Top) / square) % 2 == 0;
                graphics.FillRectangle(alternate ? light : dark, x, y, Math.Min(square, bounds.Right - x), Math.Min(square, bounds.Bottom - y));
            }
        }
    }

    internal static Color Lerp(Color start, Color end, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        return Color.FromArgb(
            (int)MathF.Round(start.A + (end.A - start.A) * amount),
            (int)MathF.Round(start.R + (end.R - start.R) * amount),
            (int)MathF.Round(start.G + (end.G - start.G) * amount),
            (int)MathF.Round(start.B + (end.B - start.B) * amount));
    }
}
