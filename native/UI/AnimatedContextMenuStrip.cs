using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace VectorAnimationEngine;

internal sealed class AnimatedContextMenuStrip : ContextMenuStrip
{
    private const int ItemHeight = 30;
    private const int MinimumWidth = 300;
    private const int MaximumWidth = 360;
    private const int OpeningDurationMilliseconds = 180;
    private const int OpeningOffsetPixels = 6;
    private const int ItemHorizontalPadding = 11;
    private const int ShortcutGap = 18;
    private const int ShortcutColumnWidth = 72;

    private readonly System.Windows.Forms.Timer _animationTimer = new() { Interval = 16 };
    private Point _targetLocation;
    private Point _openingStartLocation;
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
        Renderer = new ContextMenuRenderer();
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);

        ItemAdded += (_, e) =>
        {
            if (e.Item is null) return;

            StyleItem(e.Item);
            if (e.Item.Tag is null) e.Item.Tag = "VectorAnimationEngine.AnimatedContextMenuStrip.HoverRouting";
            e.Item.MouseEnter += (_, _) => SetHoveredItem(e.Item);
            e.Item.MouseMove += (_, _) => SetHoveredItem(e.Item);
            e.Item.MouseLeave += (_, _) =>
            {
                if (ReferenceEquals(_hoveredItem, e.Item)) SetHoveredItem(null);
            };
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
        const string hoverRoutingTag = "VectorAnimationEngine.AnimatedContextMenuStrip.HoverRouting";
        foreach (ToolStripItem item in Items)
        {
            if (item.Tag is not null) continue;

            item.Tag = hoverRoutingTag;
            item.MouseEnter += (_, _) => SetHoveredItem(item);
            item.MouseMove += (_, _) => SetHoveredItem(item);
            item.MouseLeave += (_, _) =>
            {
                if (ReferenceEquals(_hoveredItem, item)) SetHoveredItem(null);
            };
        }

        if (!Visible || !SystemInformation.IsMenuAnimationEnabled) return;

        _targetLocation = Location;
        _openingStartLocation = new Point(_targetLocation.X, _targetLocation.Y + OpeningOffsetPixels);
        _animationStartedAt = Environment.TickCount64;
        Location = _openingStartLocation;
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
        if (ClientRectangle.Contains(PointToClient(Control.MousePosition))) return;
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
            var hasShortcut = item is ToolStripMenuItem { ShortcutKeys: not Keys.None };
            var shortcutWidth = hasShortcut ? ShortcutColumnWidth : 0;
            var contentWidth = textWidth + shortcutWidth + (hasShortcut ? ShortcutGap : 0);
            width = Math.Max(width, Math.Min(MaximumWidth, contentWidth + ItemHorizontalPadding * 2));
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
            item.Padding = new Padding(ItemHorizontalPadding, 0, ItemHorizontalPadding, 0);
            item.Size = new Size(width, ItemHeight);
        }

        PerformLayout();
    }

    private void StyleItem(ToolStripItem item)
    {
        item.BackColor = Theme.PanelStrong;
        item.ForeColor = item.Enabled ? Theme.Text : Theme.DisabledText;
        item.Font = Font;
        if (item is ToolStripMenuItem { ShortcutKeys: not Keys.None } menuItem)
        {
            menuItem.ShortcutKeyDisplayString = ShortcutText(menuItem);
        }
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
        var easedProgress = EaseOutBack(progress);
        Location = new Point(
            (int)Math.Round(_openingStartLocation.X + (_targetLocation.X - _openingStartLocation.X) * easedProgress),
            (int)Math.Round(_openingStartLocation.Y + (_targetLocation.Y - _openingStartLocation.Y) * easedProgress));
        if (progress < 1f) return;

        StopOpeningAnimation();
        Location = _targetLocation;
        RestoreMinimumSize();
        AutoSize = true;
    }

    private void StopOpeningAnimation()
    {
        if (_animationTimer.Enabled) _animationTimer.Stop();
    }

    private void RestoreMinimumSize() => MinimumSize = new Size(MinimumWidth, 0);

    private static float EaseOutBack(float value)
    {
        value = Math.Clamp(value, 0f, 1f) - 1f;
        return 1f + 2.45f * value * value * value + 1.45f * value * value;
    }

    private static string ShortcutText(ToolStripMenuItem item)
    {
        var shortcut = item.ShortcutKeys;
        var parts = new List<string>(3);
        if ((shortcut & Keys.Control) == Keys.Control) parts.Add("Ctrl");
        if ((shortcut & Keys.Shift) == Keys.Shift) parts.Add("Shift");
        if ((shortcut & Keys.Alt) == Keys.Alt) parts.Add("Alt");

        var key = shortcut & Keys.KeyCode;
        if (key != Keys.None)
        {
            parts.Add(key switch
            {
                Keys.Oemcomma => ",",
                Keys.OemPeriod => ".",
                Keys.Delete => "Del",
                Keys.Return => "Enter",
                _ => key.ToString()
            });
        }

        return string.Join("+", parts);
    }

    private sealed class ContextMenuRenderer : ToolStripProfessionalRenderer
    {
        public ContextMenuRenderer()
            : base(new ContextMenuColorTable())
        {
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
            base.OnRenderMenuItemBackground(e);
            if (!e.Item.Enabled || !e.Item.Selected) return;

            var bounds = Rectangle.Inflate(e.Item.Bounds, -2, -1);
            using var background = new SolidBrush(Theme.AccentSurface);
            using var accent = new SolidBrush(Theme.Accent);
            e.Graphics.FillRectangle(background, bounds);
            e.Graphics.FillRectangle(accent, bounds.Left, bounds.Top, 3, bounds.Height);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            base.OnRenderItemText(e);
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
        public override Color MenuItemSelectedGradientBegin => Theme.AccentSurface;
        public override Color MenuItemSelectedGradientEnd => Theme.AccentSurface;
        public override Color MenuItemPressedGradientBegin => Theme.AccentPressedSurface;
        public override Color MenuItemPressedGradientMiddle => Theme.AccentPressedSurface;
        public override Color MenuItemPressedGradientEnd => Theme.AccentPressedSurface;
        public override Color SeparatorDark => Theme.Border;
        public override Color SeparatorLight => Theme.PanelStrong;
    }
}
