using System.Drawing.Drawing2D;
using System.Runtime.CompilerServices;

namespace VectorAnimationEngine;

internal static partial class Theme
{
    private sealed class RoundedControlState
    {
        private readonly Control _control;
        private Region? _appliedRegion;

        internal RoundedControlState(Control control)
        {
            _control = control;
            control.Resize += RefreshRegion;
            control.Disposed += (_, _) =>
            {
                control.Resize -= RefreshRegion;
                RestoreRegion();
            };
            ApplyRegion();
        }

        internal void Refresh()
        {
            if (_control.IsDisposed) return;
            if (UsesRoundedCorners) ApplyRegion();
            else RestoreRegion();
        }

        private void RefreshRegion(object? sender, EventArgs args) => Refresh();

        private void ApplyRegion()
        {
            if (!UsesRoundedCorners || _control.Width <= 0 || _control.Height <= 0) return;
            if (_appliedRegion is not null && !ReferenceEquals(_control.Region, _appliedRegion)) return;
            if (_appliedRegion is null && _control.Region is not null) return;
            var path = CreateUiOutline(new RectangleF(0, 0, _control.Width, _control.Height), _control.DeviceDpi);
            var nextRegion = new Region(path);
            var previousRegion = _appliedRegion;
            _appliedRegion = nextRegion;
            _control.Region = nextRegion;
            if (previousRegion is not null) previousRegion.Dispose();
        }

        private void RestoreRegion()
        {
            if (_appliedRegion is null) return;
            var appliedRegion = _appliedRegion;
            _appliedRegion = null;
            if (!_control.IsDisposed && ReferenceEquals(_control.Region, appliedRegion)) _control.Region = null;
            appliedRegion.Dispose();
        }
    }

    private static readonly ConditionalWeakTable<Control, RoundedControlState> RoundedControls = new();

    internal static void RegisterCornerStyles(Control root)
    {
        if (root.IsDisposed) return;
        if (root is not Form)
            RoundedControls.GetValue(root, static control => new RoundedControlState(control));

        if (root is Control container)
        {
            container.ControlAdded -= RegisterCornerStyleOnAdded;
            container.ControlAdded += RegisterCornerStyleOnAdded;
        }
        foreach (Control child in root.Controls) RegisterCornerStyles(child);
        if (root is ToolStrip strip) RegisterToolStripCorners(strip);
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

    internal static void RegisterToolStripCorners(ToolStrip strip)
    {
        if (strip.IsDisposed || RoundedControls.TryGetValue(strip, out _)) return;
        RoundedControls.GetValue(strip, static control => new RoundedControlState(control));
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

    internal static void PaintControlCorners(Control control, Graphics graphics, Rectangle bounds, Color background, Color? border)
    {
        if (!UsesRoundedCorners || bounds.Width <= 2 || bounds.Height <= 2) return;
        using var outline = CreateUiOutline(new RectangleF(bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1), control.DeviceDpi);
        var saved = graphics.Save();
        try
        {
            graphics.SetClip(outline, CombineMode.Exclude);
            using var fill = new SolidBrush(background);
            graphics.FillRectangle(fill, bounds);
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
