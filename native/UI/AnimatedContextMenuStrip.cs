using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace VectorAnimationEngine;

internal sealed class AnimatedContextMenuStrip : ContextMenuStrip
{
    private const int ItemHeight = 30;
    private const int MinimumWidth = 224;
    private const int MaximumWidth = 480;
    private const int OpeningDurationMilliseconds = 180;
    private const int OpeningOffsetPixels = 6;
    private const int ItemHorizontalPadding = 11;
    private const int ShortcutGap = 18;

    private readonly System.Windows.Forms.Timer _animationTimer = new() { Interval = 16 };
    private Point _targetLocation;
    private Point _openingStartLocation;
    private long _animationStartedAt;
    private ToolStripItem? _hoveredItem;
    private int ScaleMenu(int value) => Math.Max(1, (int)Math.Round(value * DeviceDpi / 96f));

    public AnimatedContextMenuStrip()
    {
        AutoSize = true;
        BackColor = Theme.PanelStrong;
        ForeColor = Theme.Text;
        Font = Theme.UiFont(9.5f);
        Padding = new Padding(7, 7, 7, 7);
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
        SizeChanged += (_, _) => RefreshMenuRegion();
        UiLocalization.Watch(this);
    }

    private void RefreshMenuRegion()
    {
        var oldRegion = Region;
        if (!Theme.UsesRoundedCorners || ClientSize.Width <= 0 || ClientSize.Height <= 0)
            Region = null;
        else
        {
            using var outline = ContextMenuRenderer.RoundedPath(ClientRectangle, 4f * DeviceDpi / 96f);
            Region = new Region(outline);
        }
        oldRegion?.Dispose();
    }

    protected override void OnOpening(CancelEventArgs e)
    {
        StopOpeningAnimation();
        SetHoveredItem(null);
        RestoreMinimumSize();
        AutoSize = true;
        base.OnOpening(e);
        if (!e.Cancel)
        {
            foreach (ToolStripItem item in Items) StyleItem(item);
            LayoutItems();
            RefreshMenuRegion();
        }
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

        if (!Visible || OwnerItem is not null || !UiMotion.AnimationsEnabled) return;

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
        Padding = new Padding(ScaleMenu(7));
        ShowImageMargin = Items.OfType<ToolStripItem>().Any(item => item.Image is not null);
        ShowCheckMargin = Items.OfType<ToolStripMenuItem>().Any(item => item.Checked || item.CheckOnClick);
        var visibleItems = Items.Cast<ToolStripItem>().Where(item => item.Available).ToArray();
        var width = ScaleMenu(MinimumWidth);
        foreach (var item in visibleItems)
        {
            if (item is ToolStripSeparator) continue;
            StyleItem(item);
            var textWidth = TextRenderer.MeasureText(item.Text, Font, Size.Empty,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
            var shortcut = item is ToolStripMenuItem menu ? menu.ShortcutKeyDisplayString : "";
            var shortcutWidth = string.IsNullOrEmpty(shortcut) ? 0
                : TextRenderer.MeasureText(shortcut, Font, Size.Empty,
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + ScaleMenu(ShortcutGap);
            var adornments = ShowImageMargin || ShowCheckMargin ? ScaleMenu(32) : 0;
            var arrowWidth = item is ToolStripMenuItem { HasDropDownItems: true } ? ScaleMenu(24) : ScaleMenu(8);
            width = Math.Max(width, Math.Min(ScaleMenu(MaximumWidth),
                textWidth + shortcutWidth + adornments + arrowWidth + ScaleMenu(ItemHorizontalPadding * 2)));
        }
        foreach (var item in visibleItems)
        {
            item.AutoSize = false;
            if (item is ToolStripSeparator)
            {
                item.Margin = new Padding(ScaleMenu(7), ScaleMenu(5), ScaleMenu(7), ScaleMenu(5));
                item.Size = new Size(width - ScaleMenu(14), ScaleMenu(1));
            }
            else
            {
                item.Margin = Padding.Empty;
                item.Padding = new Padding(ScaleMenu(ItemHorizontalPadding), 0, ScaleMenu(ItemHorizontalPadding), 0);
                item.Size = new Size(width, ScaleMenu(ItemHeight));
            }
        }
        PerformLayout();
    }

    private void EnsureSubmenu(ToolStripMenuItem owner)
    {
        var previous = owner.DropDown;
        if (previous is AnimatedContextMenuStrip) return;
        // Keep the command instances, checked state, tags and their event handlers.
        var children = previous.Items.Cast<ToolStripItem>().ToArray();
        previous.Items.Clear();
        var styled = new AnimatedContextMenuStrip();
        owner.DropDown = styled;
        styled.Items.AddRange(children);
        previous.Dispose();
    }

    private void StyleItem(ToolStripItem item)
    {
        if (item is ToolStripMenuItem submenu) EnsureSubmenu(submenu);
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

    private void RestoreMinimumSize() => MinimumSize = new Size(ScaleMenu(MinimumWidth), 0);

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

        public static GraphicsPath RoundedPath(RectangleF bounds, float radius)
        {
            var path = new GraphicsPath();
            var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
            if (diameter <= 0) { path.AddRectangle(bounds); return path; }
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            var scale = e.ToolStrip.DeviceDpi / 96f;
            var bounds = new RectangleF(0.5f, 0.5f, Math.Max(1, e.ToolStrip.Width - 1), Math.Max(1, e.ToolStrip.Height - 1));
            using var path = RoundedPath(bounds, Theme.UsesRoundedCorners ? 4f * scale : 0);
            using var border = new Pen(SystemInformation.HighContrast ? SystemColors.WindowText
                : Theme.ControlOutline);
            var state = e.Graphics.Save();
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.DrawPath(border, path);
            e.Graphics.Restore(state);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            // Renderer callbacks use item-local coordinates, unlike invalidation rectangles.
            if (!e.Item.Enabled || !e.Item.Selected) return;
            var scale = e.ToolStrip.DeviceDpi / 96f;
            var bounds = new RectangleF(2f * scale, scale,
                Math.Max(1, e.Item.Width - 4f * scale), Math.Max(1, e.Item.Height - 2f * scale));
            using var path = RoundedPath(bounds, Theme.UsesRoundedCorners ? 4f * scale : 0);
            using var background = new SolidBrush(SystemInformation.HighContrast
                ? SystemColors.Highlight : Theme.MenuSelectionSurface);
            var state = e.Graphics.Save();
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.FillPath(background, path);
            e.Graphics.Restore(state);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            var shortcut = e.Item is ToolStripMenuItem menu
                && !string.IsNullOrEmpty(menu.ShortcutKeyDisplayString)
                && e.Text == menu.ShortcutKeyDisplayString;
            e.TextColor = SystemInformation.HighContrast
                ? !e.Item.Enabled ? SystemColors.GrayText : e.Item.Selected ? SystemColors.HighlightText : SystemColors.MenuText
                : Theme.ReadableText(e.Item.Selected && e.Item.Enabled ? Theme.MenuSelectionSurface : Theme.PanelStrong,
                    !e.Item.Enabled ? Theme.DisabledText : shortcut ? Theme.Muted : Theme.Text);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            var scale = e.ToolStrip.DeviceDpi / 96f;
            using var line = new Pen(SystemInformation.HighContrast ? SystemColors.WindowText
                : Theme.Mix(Theme.PanelStrong, Theme.Border, 0.55f));
            var y = e.Item.Height / 2f;
            e.Graphics.DrawLine(line, 4f * scale, y, Math.Max(4f * scale, e.Item.Width - 4f * scale), y);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            var bounds = e.ArrowRectangle;
            var centerX = bounds.Left + bounds.Width / 2f;
            var centerY = bounds.Top + bounds.Height / 2f;
            using var arrow = new SolidBrush(Theme.ReadableUiColor(e.Item?.Selected == true ? Theme.MenuSelectionSurface : Theme.PanelStrong,
                e.Item?.Enabled == true ? Theme.Muted : Theme.DisabledText));
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
