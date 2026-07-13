using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace VectorAnimationEngine;

internal sealed class AnimatedContextMenuStrip : ContextMenuStrip
{
    private const int ItemHeight = 30;
    private const int MinimumWidth = 176;
    private const int MaximumWidth = 360;
    private const float OpeningScale = 0.86f;
    private const int OpeningDurationMilliseconds = 180;

    private readonly System.Windows.Forms.Timer _animationTimer = new() { Interval = 16 };
    private Size _targetSize;
    private Point _targetLocation;
    private long _animationStartedAt;
    private ToolStripItem? _hoveredItem;

    public AnimatedContextMenuStrip()
    {
        AutoSize = true;
        BackColor = Theme.PanelStrong;
        ForeColor = Theme.Text;
        Font = Theme.UiFont(9.5f);
        Padding = new Padding(6, 5, 6, 5);
        RestoreMinimumSize();
        ShowImageMargin = false;
        ShowCheckMargin = false;
        AccessibleRole = AccessibleRole.MenuPopup;
        Renderer = new ContextMenuRenderer(this);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);

        ItemAdded += (_, e) =>
        {
            if (e.Item is not null) StyleItem(e.Item);
        };
        _animationTimer.Tick += (_, _) => TickOpeningAnimation();
    }

    protected override void OnOpening(CancelEventArgs e)
    {
        StopOpeningAnimation();
        SetHoveredItem(null);
        RestoreMinimumSize();
        AutoSize = true;
        base.OnOpening(e);
        if (!e.Cancel) LayoutItems();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (!Visible || !SystemInformation.IsMenuAnimationEnabled) return;

        _targetSize = Size;
        _targetLocation = Location;
        if (_targetSize.Width <= 0 || _targetSize.Height <= 0) return;

        AutoSize = false;
        MinimumSize = Size.Empty;
        _animationStartedAt = Environment.TickCount64;
        ApplyOpeningScale(OpeningScale);
        _animationTimer.Start();
    }

    protected override void OnClosed(ToolStripDropDownClosedEventArgs e)
    {
        StopOpeningAnimation();
        SetHoveredItem(null);
        RestoreMinimumSize();
        AutoSize = true;
        base.OnClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopOpeningAnimation();
            _animationTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        SetHoveredItem(GetItemAt(e.Location));
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        SetHoveredItem(null);
    }

    private void LayoutItems()
    {
        ShowImageMargin = Items.OfType<ToolStripItem>().Any(item => item.Image is not null);
        ShowCheckMargin = Items.OfType<ToolStripMenuItem>().Any(item => item.Checked || item.CheckOnClick);

        var width = MinimumWidth;
        foreach (ToolStripItem item in Items)
        {
            if (item is ToolStripSeparator) continue;

            StyleItem(item);
            var textWidth = TextRenderer.MeasureText(
                item.Text,
                Font,
                new Size(MaximumWidth, ItemHeight),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width;
            var shortcutWidth = item is ToolStripMenuItem menuItem && menuItem.ShortcutKeys != Keys.None
                ? TextRenderer.MeasureText(
                    ShortcutText(menuItem),
                    Font,
                    new Size(MaximumWidth, ItemHeight),
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width + 18
                : 0;
            width = Math.Max(width, Math.Min(MaximumWidth, textWidth + shortcutWidth + 28));
        }

        foreach (ToolStripItem item in Items)
        {
            if (item is ToolStripSeparator)
            {
                item.AutoSize = false;
                item.Margin = new Padding(7, 5, 7, 5);
                item.Size = new Size(width - 14, 1);
                continue;
            }

            item.AutoSize = false;
            item.Margin = Padding.Empty;
            item.Padding = new Padding(11, 0, 11, 0);
            item.Size = new Size(width, ItemHeight);
        }

        PerformLayout();
    }

    private void StyleItem(ToolStripItem item)
    {
        item.BackColor = Theme.PanelStrong;
        item.ForeColor = item.Enabled ? Theme.Text : Theme.DisabledText;
        item.Font = Font;
        item.AccessibleRole = item is ToolStripSeparator ? AccessibleRole.Separator : AccessibleRole.MenuItem;
        if (string.IsNullOrWhiteSpace(item.AccessibleName)) item.AccessibleName = item.Text;
    }

    private bool IsHovered(ToolStripItem item) => ReferenceEquals(item, _hoveredItem);

    private void SetHoveredItem(ToolStripItem? item)
    {
        if (item is ToolStripSeparator || item?.Enabled != true) item = null;
        if (ReferenceEquals(item, _hoveredItem)) return;

        var previous = _hoveredItem;
        _hoveredItem = item;
        _hoveredItem?.Select();
        if (previous is not null) Invalidate(previous.Bounds);
        if (_hoveredItem is not null) Invalidate(_hoveredItem.Bounds);
    }

    private void TickOpeningAnimation()
    {
        if (IsDisposed || !Visible)
        {
            StopOpeningAnimation();
            return;
        }

        var elapsed = Environment.TickCount64 - _animationStartedAt;
        var progress = Math.Clamp(elapsed / (float)OpeningDurationMilliseconds, 0f, 1f);
        var scale = OpeningScale + (1f - OpeningScale) * EaseOutBack(progress);
        ApplyOpeningScale(scale);
        if (progress < 1f) return;

        StopOpeningAnimation();
        Size = _targetSize;
        Location = _targetLocation;
        RestoreMinimumSize();
        AutoSize = true;
    }

    private void StopOpeningAnimation()
    {
        if (_animationTimer.Enabled) _animationTimer.Stop();
    }

    private void RestoreMinimumSize() => MinimumSize = new Size(MinimumWidth, 0);

    private void ApplyOpeningScale(float scale)
    {
        var width = Math.Max(1, (int)Math.Round(_targetSize.Width * scale));
        var height = Math.Max(1, (int)Math.Round(_targetSize.Height * scale));
        var x = _targetLocation.X + (_targetSize.Width - width) / 2;
        var y = _targetLocation.Y + (_targetSize.Height - height) / 2;
        SetBounds(x, y, width, height, BoundsSpecified.All);
    }

    private static float EaseOutBack(float value)
    {
        value = Math.Clamp(value, 0f, 1f) - 1f;
        return 1f + 2.45f * value * value * value + 1.45f * value * value;
    }

    private static string ShortcutText(ToolStripMenuItem item)
    {
        return string.IsNullOrWhiteSpace(item.ShortcutKeyDisplayString)
            ? item.ShortcutKeys.ToString()
            : item.ShortcutKeyDisplayString;
    }

    private sealed class ContextMenuRenderer : ToolStripProfessionalRenderer
    {
        private readonly AnimatedContextMenuStrip _menu;

        public ContextMenuRenderer(AnimatedContextMenuStrip menu)
            : base(new ContextMenuColorTable())
        {
            _menu = menu;
            RoundedEdges = false;
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var background = new SolidBrush(Theme.PanelStrong);
            e.Graphics.FillRectangle(background, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var border = new Pen(Theme.BorderHover);
            e.Graphics.DrawRectangle(border, 0, 0, Math.Max(0, e.ToolStrip.Width - 1), Math.Max(0, e.ToolStrip.Height - 1));
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var hovered = _menu.IsHovered(e.Item);
            if ((!e.Item.Selected && !hovered) || !e.Item.Enabled) return;

            var bounds = Rectangle.Inflate(e.Item.Bounds, -2, -1);
            using var background = new SolidBrush(Theme.AccentSurface);
            using var accent = new SolidBrush(Theme.Accent);
            e.Graphics.FillRectangle(background, bounds);
            e.Graphics.FillRectangle(accent, bounds.Left, bounds.Top, 3, bounds.Height);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            var textBounds = e.TextRectangle;
            var color = e.Item.Enabled ? Theme.Text : Theme.DisabledText;
            var flags = TextFormatFlags.Left
                | TextFormatFlags.VerticalCenter
                | TextFormatFlags.EndEllipsis
                | TextFormatFlags.NoPadding
                | TextFormatFlags.NoPrefix
                | TextFormatFlags.SingleLine;

            if (e.Item is ToolStripMenuItem menuItem && menuItem.ShortcutKeys != Keys.None)
            {
                var shortcut = ShortcutText(menuItem);
                var shortcutWidth = TextRenderer.MeasureText(shortcut, e.TextFont, Size.Empty, flags).Width;
                var shortcutBounds = new Rectangle(
                    Math.Max(textBounds.Left, textBounds.Right - shortcutWidth),
                    textBounds.Top,
                    shortcutWidth,
                    textBounds.Height);
                TextRenderer.DrawText(e.Graphics, shortcut, e.TextFont, shortcutBounds, Theme.Muted, flags | TextFormatFlags.Right);
                textBounds.Width = Math.Max(0, shortcutBounds.Left - textBounds.Left - 12);
            }

            TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, textBounds, color, flags);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            var bounds = e.Item.Bounds;
            using var line = new Pen(Theme.Border);
            var y = bounds.Top + bounds.Height / 2;
            e.Graphics.DrawLine(line, bounds.Left, y, bounds.Right, y);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            var bounds = e.ArrowRectangle;
            var centerX = bounds.Left + bounds.Width / 2f;
            var centerY = bounds.Top + bounds.Height / 2f;
            using var arrow = new SolidBrush(e.Item?.Enabled == true ? Theme.Muted : Theme.DisabledText);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.FillPolygon(arrow,
            [
                new PointF(centerX - 2, centerY - 4),
                new PointF(centerX - 2, centerY + 4),
                new PointF(centerX + 3, centerY)
            ]);
        }
    }

    private sealed class ContextMenuColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.PanelStrong;
        public override Color ToolStripBorder => Theme.BorderHover;
        public override Color MenuItemSelected => Theme.AccentSurface;
        public override Color MenuItemBorder => Theme.Accent;
        public override Color SeparatorDark => Theme.Border;
        public override Color SeparatorLight => Theme.PanelStrong;
    }
}
