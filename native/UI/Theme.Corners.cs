using System.Drawing.Drawing2D;
using System.Runtime.CompilerServices;

namespace VectorAnimationEngine;

internal static partial class Theme
{
    // Keyboard focus uses a short solid underline instead of a dotted rectangle.
    internal static void DrawKeyboardFocus(Graphics graphics, Rectangle bounds) =>
        DrawKeyboardFocus(graphics, bounds, SecondaryText, Panel);

    internal static void DrawKeyboardFocus(Graphics graphics, Rectangle bounds, Color foreground, Color background)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        var thickness = Math.Max(1f, graphics.DpiY / 96f);
        var width = Math.Min(bounds.Width, Math.Max(8, bounds.Width / 3));
        var left = bounds.Left + (bounds.Width - width) / 2f;
        using var brush = new SolidBrush(SystemInformation.HighContrast
            ? SystemColors.Highlight : Mix(background, foreground, 0.65f));
        graphics.FillRectangle(brush, left, bounds.Bottom - thickness, width, thickness);
    }

    private sealed class RoundedControlState
    {
        private readonly Control _control;

        internal RoundedControlState(Control control)
        {
            _control = control;
            // Only ordinary buttons need a paint adapter. Custom controls draw their
            // own surfaces; containers, content and splitters must never be clipped.
            if (control is Button and not SvgIconButton) control.Paint += PaintButton;
        }

        internal void Refresh()
        {
            if (_control.IsDisposed) return;
            _control.Invalidate();
        }

        private void PaintButton(object? sender, PaintEventArgs e)
        {
            if (!UsesRoundedCorners || _control is not Button button) return;
            var parentBackground = GetCornerBackground(button);
            using var outside = new SolidBrush(parentBackground);
            e.Graphics.FillRectangle(outside, button.ClientRectangle);
            using var fill = new SolidBrush(button.BackColor);
            using var border = new Pen(button.FlatAppearance.BorderColor, Math.Max(1f, button.DeviceDpi / 96f));
            PaintUiSurface(e.Graphics, fill, button.FlatAppearance.BorderSize > 0 ? border : null, button.ClientRectangle);
            var textBounds = Rectangle.Inflate(button.ClientRectangle, -4, -2);
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis
                | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform;
            flags |= button.TextAlign is ContentAlignment.MiddleLeft or ContentAlignment.TopLeft or ContentAlignment.BottomLeft
                ? TextFormatFlags.Left
                : button.TextAlign is ContentAlignment.MiddleRight or ContentAlignment.TopRight or ContentAlignment.BottomRight
                    ? TextFormatFlags.Right : TextFormatFlags.HorizontalCenter;
            if (!button.UseMnemonic) flags |= TextFormatFlags.NoPrefix;
            if (!SystemInformation.MenuAccessKeysUnderlined && !button.Focused) flags |= TextFormatFlags.HidePrefix;
            if (button.RightToLeft == RightToLeft.Yes) flags |= TextFormatFlags.RightToLeft;
            TextRenderer.DrawText(e.Graphics, button.Text, button.Font, textBounds,
                ReadableText(button.BackColor, button.Enabled ? button.ForeColor : DisabledText), flags);
            if (button.Focused)
                Theme.DrawKeyboardFocus(e.Graphics, Rectangle.Inflate(button.ClientRectangle, -4, -4),
                    ReadableUiColor(button.BackColor, AccentLabel), button.BackColor);
        }
    }

    private static readonly ConditionalWeakTable<Control, RoundedControlState> RoundedControls = new();

    internal static void RegisterCornerStyles(Control root)
    {
        if (root.IsDisposed) return;
        if (root is Button and not SvgIconButton and not DrawingScopeButton)
            RoundedControls.GetValue(root, static control => new RoundedControlState(control));

        root.ControlAdded -= RegisterCornerStyleOnAdded;
        root.ControlAdded += RegisterCornerStyleOnAdded;
        foreach (Control child in root.Controls) RegisterCornerStyles(child);
    }

    private static void RegisterCornerStyleOnAdded(object? sender, ControlEventArgs args)
    {
        if (args.Control is { } control) RegisterCornerStyles(control);
    }

    private static Color GetCornerBackground(Control control)
    {
        for (var parent = control.Parent; parent is not null; parent = parent.Parent)
            if (parent.BackColor != Color.Transparent && parent.BackColor.A > 0) return parent.BackColor;
        return SystemColors.Control;
    }

    internal static bool UsesRoundedCorners => CornerStyle == WindowCornerStyle.SubtleRounded && !SystemInformation.HighContrast;

    internal static GraphicsPath CreateUiOutline(RectangleF bounds, float dpi)
    {
        var path = new GraphicsPath();
        if (bounds.Width <= 0 || bounds.Height <= 0) return path;
        var radius = UsesRoundedCorners
            ? Math.Min(4f * dpi / 96f, Math.Min(bounds.Width, bounds.Height) / 2f)
            : 0f;
        if (radius <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }
        var diameter = radius * 2f;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    internal static void FillUiRectangle(Graphics graphics, Brush brush, RectangleF bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        if (!UsesRoundedCorners) { graphics.FillRectangle(brush, bounds); return; }
        using var path = CreateUiOutline(bounds, graphics.DpiX);
        var saved = graphics.Save();
        try
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.FillPath(brush, path);
        }
        finally { graphics.Restore(saved); }
    }

    internal static void DrawUiRectangle(Graphics graphics, Pen pen, RectangleF bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        if (!UsesRoundedCorners) { graphics.DrawRectangle(pen, bounds.X, bounds.Y, bounds.Width, bounds.Height); return; }
        using var path = CreateUiOutline(bounds, graphics.DpiX);
        var saved = graphics.Save();
        try
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.DrawPath(pen, path);
        }
        finally { graphics.Restore(saved); }
    }

    // Keep the complete hit rectangle. The stroke is inset so neither its straight
    // edges nor its antialiased corners are cut off by the control's client bounds.
    internal static void PaintUiSurface(Graphics graphics, Brush fill, Pen? border, Rectangle bounds)
    {
        if (!UsesRoundedCorners)
        {
            graphics.FillRectangle(fill, bounds);
            if (border is not null) graphics.DrawRectangle(border, bounds.X, bounds.Y,
                Math.Max(0, bounds.Width - 1), Math.Max(0, bounds.Height - 1));
            return;
        }
        var inset = border?.Width / 2f ?? 0f;
        var surface = new RectangleF(bounds.X + inset, bounds.Y + inset,
            bounds.Width - inset * 2f, bounds.Height - inset * 2f);
        if (surface.Width <= 0 || surface.Height <= 0) return;
        using var outline = CreateUiOutline(surface, graphics.DpiX);
        var saved = graphics.Save();
        try
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.FillPath(fill, outline);
            if (border is not null) graphics.DrawPath(border, outline);
        }
        finally { graphics.Restore(saved); }
    }

    internal static void PaintControlCorners(Control control, Graphics graphics, Rectangle bounds, Color background, Color? border)
    {
        if (!UsesRoundedCorners || bounds.Width <= 2 || bounds.Height <= 2) return;
        using var outline = CreateUiOutline(new RectangleF(bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1), control.DeviceDpi);
        var saved = graphics.Save();
        try
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var outside = new GraphicsPath(FillMode.Alternate);
            outside.AddRectangle(bounds);
            outside.AddPath(outline, false);
            using var fill = new SolidBrush(background);
            graphics.FillPath(fill, outside);
        }
        finally { graphics.Restore(saved); }
        if (border is not { } borderColor) return;
        saved = graphics.Save();
        try
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(borderColor, Math.Max(1f, control.DeviceDpi / 96f));
            graphics.DrawPath(pen, outline);
        }
        finally { graphics.Restore(saved); }
    }

}
