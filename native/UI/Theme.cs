using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace VectorAnimationEngine;

internal static class Theme
{
    public const int ControlHeightCompact = 28;
    public const int ControlHeight = 32;
    public const int IconButtonSize = 34;
    public const int GapXs = 4;
    public const int GapSm = 8;
    public const int GapMd = 12;

    public static readonly Color App = Color.FromArgb(18, 20, 22);
    public static readonly Color Top = Color.FromArgb(24, 27, 30);
    public static readonly Color Panel = Color.FromArgb(30, 34, 37);
    public static readonly Color PanelStrong = Color.FromArgb(42, 48, 53);
    public static readonly Color PanelHover = Color.FromArgb(55, 63, 68);
    public static readonly Color Field = Color.FromArgb(15, 17, 19);
    public static readonly Color FieldHover = Color.FromArgb(22, 25, 28);
    public static readonly Color FieldFocus = Color.FromArgb(24, 31, 32);
    public static readonly Color Stage = Color.FromArgb(13, 15, 17);
    public static readonly Color Border = Color.FromArgb(61, 69, 76);
    public static readonly Color BorderHover = Color.FromArgb(91, 103, 110);
    public static readonly Color Text = Color.FromArgb(242, 246, 245);
    public static readonly Color Muted = Color.FromArgb(190, 202, 202);
    public static readonly Color DisabledText = Color.FromArgb(116, 125, 128);
    public static readonly Color Accent = Color.FromArgb(79, 179, 162);
    public static readonly Color AccentSurface = Color.FromArgb(39, 83, 77);
    public static readonly Color AccentHoverSurface = Color.FromArgb(51, 107, 99);
    public static readonly Color AccentPressedSurface = Color.FromArgb(31, 68, 63);
    public static readonly Color DisabledSurface = Color.FromArgb(35, 39, 43);
    public static readonly Color AccentText = Color.FromArgb(5, 22, 20);
    public static readonly Color AccentLabel = Color.FromArgb(209, 247, 238);
    public static readonly Color Warning = Color.FromArgb(232, 184, 92);
    public static readonly Color Danger = Color.FromArgb(232, 104, 104);

    private static readonly ConditionalWeakTable<Control, FieldInteractionState> FieldStates = new();
    private static readonly ConditionalWeakTable<ComboBox, object> StyledComboBoxes = new();
    private static readonly ConditionalWeakTable<ListBox, ListBoxInteractionState> StyledListBoxes = new();
    private static readonly ConditionalWeakTable<TreeView, TreeViewInteractionState> StyledTreeViews = new();
    private static readonly ConditionalWeakTable<ListView, ListViewInteractionState> StyledListViews = new();
    private static readonly ConditionalWeakTable<ToolTip, object> StyledToolTips = new();

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
        var foreColor = active ? AccentLabel : Text;
        var borderColor = active ? Accent : Border;
        var hoverColor = active ? AccentSurface : PanelStrong;
        if (button.UseVisualStyleBackColor) button.UseVisualStyleBackColor = false;
        if (button.FlatStyle != FlatStyle.Flat) button.FlatStyle = FlatStyle.Flat;
        if (button.ForeColor != foreColor) button.ForeColor = foreColor;
        if (button.FlatAppearance.BorderColor != borderColor) button.FlatAppearance.BorderColor = borderColor;
        if (button.FlatAppearance.BorderSize != 1) button.FlatAppearance.BorderSize = 1;
        if (button.FlatAppearance.MouseOverBackColor != hoverColor) button.FlatAppearance.MouseOverBackColor = hoverColor;
        if (button.FlatAppearance.MouseDownBackColor != hoverColor) button.FlatAppearance.MouseDownBackColor = hoverColor;
        if (!string.Equals(button.Font.Name, "Segoe UI", StringComparison.Ordinal)
            || Math.Abs(button.Font.Size - 9.5f) > 0.01f
            || button.Font.Style != FontStyle.Regular)
        {
            button.Font = UiFont();
        }

        if (button.TextAlign != ContentAlignment.MiddleCenter) button.TextAlign = ContentAlignment.MiddleCenter;
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
        ConfigureFieldInteraction(box);
    }

    public static void StyleComboBox(ComboBox box)
    {
        box.BackColor = Field;
        box.ForeColor = Text;
        box.FlatStyle = FlatStyle.Flat;
        box.Font = UiFont();
        box.DrawMode = DrawMode.OwnerDrawFixed;
        box.ItemHeight = 26;
        if (StyledComboBoxes.TryGetValue(box, out _)) return;
        StyledComboBoxes.Add(box, new object());
        box.DrawItem += DrawComboBoxItem;
        ConfigureFieldInteraction(box);
    }

    public static void StyleNumeric(NumericUpDown input)
    {
        input.BackColor = Field;
        input.ForeColor = Text;
        input.BorderStyle = BorderStyle.FixedSingle;
        input.Font = UiFont();
        input.MinimumSize = new Size(0, ControlHeightCompact);
        ConfigureFieldInteraction(input);
    }

    public static void StyleNumeric(ModernNumericUpDown input)
    {
        input.BackColor = Field;
        input.ForeColor = Text;
        input.Font = UiFont();
        input.MinimumSize = new Size(0, ControlHeightCompact);
    }

    public static void StyleListBox(ListBox list)
    {
        list.BackColor = Panel;
        list.ForeColor = Text;
        list.BorderStyle = BorderStyle.FixedSingle;
        list.Font = UiFont(9.2f);
        list.DrawMode = DrawMode.OwnerDrawFixed;
        list.ItemHeight = 28;
        list.IntegralHeight = false;
        StyleNativeScrollBars(list);
        if (StyledListBoxes.TryGetValue(list, out _)) return;
        var state = new ListBoxInteractionState(list);
        StyledListBoxes.Add(list, state);
        list.DrawItem += DrawListBoxItem;
    }

    public static void StyleTreeView(TreeView tree, bool useCustomExpandButtons = false)
    {
        tree.BackColor = Panel;
        tree.ForeColor = Text;
        tree.BorderStyle = BorderStyle.None;
        tree.Font = UiFont(9.5f);
        tree.HideSelection = false;
        tree.FullRowSelect = true;
        tree.ShowLines = false;
        tree.ShowRootLines = false;
        tree.ShowPlusMinus = !useCustomExpandButtons;
        tree.ItemHeight = 28;
        tree.DrawMode = TreeViewDrawMode.OwnerDrawAll;
        tree.HotTracking = true;
        StyleNativeScrollBars(tree);
        if (StyledTreeViews.TryGetValue(tree, out _)) return;
        var state = new TreeViewInteractionState(tree, useCustomExpandButtons);
        StyledTreeViews.Add(tree, state);
        tree.DrawNode += DrawTreeNode;
    }

    public static void StyleListView(ListView list)
    {
        list.BackColor = Panel;
        list.ForeColor = Text;
        list.BorderStyle = BorderStyle.None;
        list.Font = UiFont(9.2f);
        list.View = View.Details;
        list.FullRowSelect = true;
        list.HideSelection = false;
        list.MultiSelect = false;
        list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        list.OwnerDraw = true;
        StyleNativeScrollBars(list);
        if (StyledListViews.TryGetValue(list, out _)) return;
        var state = new ListViewInteractionState(list);
        StyledListViews.Add(list, state);
        list.DrawColumnHeader += DrawListViewHeader;
        list.DrawItem += DrawListViewItem;
        list.DrawSubItem += DrawListViewSubItem;
    }

    public static void StyleStatusStrip(StatusStrip strip)
    {
        strip.BackColor = Top;
        strip.ForeColor = Muted;
        strip.Font = UiFont(9);
        strip.RenderMode = ToolStripRenderMode.Professional;
        strip.Renderer = new ModernStatusStripRenderer();
    }

    /// <summary>
    /// Applies the dark Explorer theme to stock control scrollbars. Complex
    /// scrolling surfaces use <see cref="ThemedScrollPanel"/> for the fully
    /// custom accent treatment.
    /// </summary>
    private static void StyleNativeScrollBars(Control control)
    {
        void ApplyTheme()
        {
            if (control.IsDisposed || !control.IsHandleCreated) return;
            try
            {
                SetWindowTheme(control.Handle, "DarkMode_Explorer", null);
            }
            catch (EntryPointNotFoundException)
            {
                // Older Windows versions keep their native scrollbar rendering.
            }
        }

        if (control.IsHandleCreated) ApplyTheme();
        else control.HandleCreated += (_, _) => ApplyTheme();
    }

    public static void StyleToolTip(ToolTip toolTip)
    {
        toolTip.BackColor = PanelStrong;
        toolTip.ForeColor = Text;
        toolTip.AutoPopDelay = 6000;
        toolTip.InitialDelay = 420;
        toolTip.ReshowDelay = 90;
        toolTip.ShowAlways = true;
        toolTip.OwnerDraw = true;
        if (StyledToolTips.TryGetValue(toolTip, out _)) return;
        StyledToolTips.Add(toolTip, new object());
        toolTip.Popup += (_, e) =>
        {
            var text = e.AssociatedControl is null
                ? string.Empty
                : UiLocalization.T(toolTip.GetToolTip(e.AssociatedControl));
            using var font = UiFont(9);
            var size = TextRenderer.MeasureText(text, font, new Size(360, 0), TextFormatFlags.NoPadding | TextFormatFlags.WordBreak);
            e.ToolTipSize = new Size(Math.Max(64, size.Width + 18), Math.Max(28, size.Height + 12));
        };
        toolTip.Draw += (_, e) =>
        {
            using var background = new SolidBrush(PanelStrong);
            using var border = new Pen(BorderHover);
            e.Graphics.FillRectangle(background, e.Bounds);
            e.Graphics.DrawRectangle(border, 0, 0, Math.Max(0, e.Bounds.Width - 1), Math.Max(0, e.Bounds.Height - 1));
            var bounds = Rectangle.Inflate(e.Bounds, -9, -6);
            using var font = UiFont(9);
            TextRenderer.DrawText(
                e.Graphics,
                UiLocalization.T(e.ToolTipText),
                font,
                bounds,
                Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        };
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

    private static void ConfigureFieldInteraction(Control control)
    {
        if (FieldStates.TryGetValue(control, out _)) return;
        FieldStates.Add(control, new FieldInteractionState(control));
    }

    private static void DrawComboBoxItem(object? sender, DrawItemEventArgs e)
    {
        if (sender is not ComboBox box) return;
        var selected = (e.State & DrawItemState.Selected) != 0;
        var focused = (e.State & DrawItemState.Focus) != 0;
        var background = selected ? AccentSurface : box.BackColor;
        using var backgroundBrush = new SolidBrush(background);
        e.Graphics.FillRectangle(backgroundBrush, e.Bounds);

        if (selected)
        {
            using var accent = new SolidBrush(Accent);
            e.Graphics.FillRectangle(accent, e.Bounds.Left, e.Bounds.Top, 3, e.Bounds.Height);
        }

        var text = e.Index >= 0 && e.Index < box.Items.Count
            ? box.GetItemText(box.Items[e.Index])
            : box.Text;
        text = UiLocalization.T(text);
        var textBounds = Rectangle.Inflate(e.Bounds, -10, 0);
        TextRenderer.DrawText(
            e.Graphics,
            text,
            box.Font,
            textBounds,
            box.Enabled ? Text : DisabledText,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (focused && e.Index >= 0)
        {
            using var focus = new Pen(Accent);
            var focusBounds = Rectangle.Inflate(e.Bounds, -1, -1);
            e.Graphics.DrawRectangle(focus, focusBounds);
        }
    }

    private static void DrawListBoxItem(object? sender, DrawItemEventArgs e)
    {
        if (sender is not ListBox list || e.Index < 0 || e.Index >= list.Items.Count) return;
        StyledListBoxes.TryGetValue(list, out var state);
        var selected = (e.State & DrawItemState.Selected) != 0;
        var hovered = state?.HoverIndex == e.Index;
        var background = selected ? AccentSurface : hovered ? PanelHover : Panel;
        using var backgroundBrush = new SolidBrush(background);
        e.Graphics.FillRectangle(backgroundBrush, e.Bounds);
        if (selected)
        {
            using var accent = new SolidBrush(Accent);
            e.Graphics.FillRectangle(accent, e.Bounds.Left, e.Bounds.Top, 3, e.Bounds.Height);
        }

        var textBounds = new Rectangle(e.Bounds.Left + 10, e.Bounds.Top, Math.Max(0, e.Bounds.Width - 14), e.Bounds.Height);
        TextRenderer.DrawText(
            e.Graphics,
            UiLocalization.T(list.GetItemText(list.Items[e.Index])),
            list.Font,
            textBounds,
            list.Enabled ? Text : DisabledText,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    private static void DrawTreeNode(object? sender, DrawTreeNodeEventArgs e)
    {
        if (sender is not TreeView tree || e.Node is null) return;
        StyledTreeViews.TryGetValue(tree, out var state);
        var selected = tree.SelectedNode == e.Node;
        var hovered = state?.HoverNode == e.Node;
        var row = new Rectangle(0, e.Bounds.Top, tree.ClientSize.Width, e.Bounds.Height);
        using var background = new SolidBrush(selected ? AccentSurface : hovered ? PanelHover : Panel);
        e.Graphics.FillRectangle(background, row);
        if (selected)
        {
            using var accent = new SolidBrush(Accent);
            e.Graphics.FillRectangle(accent, 0, row.Top, 3, row.Height);
        }

        var customExpandButtons = state?.UseCustomExpandButtons == true;
        var indent = Math.Max(16, tree.Indent);
        var contentLeft = customExpandButtons
            ? Math.Max(e.Bounds.Left, 6 + e.Node.Level * indent)
            : Math.Max(e.Bounds.Left, 8 + e.Node.Level * Math.Max(12, tree.Indent));
        if (e.Node.Nodes.Count > 0)
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            if (customExpandButtons)
            {
                var buttonBounds = TreeExpandGlyphBounds(tree, e.Node);
                var expandHovered = state?.HoverExpandNode == e.Node;
                var expandPressed = state?.PressedExpandNode == e.Node;
                var buttonColor = expandPressed
                    ? AccentPressedSurface
                    : expandHovered
                        ? AccentHoverSurface
                        : selected
                            ? AccentPressedSurface
                            : PanelStrong;
                var borderColor = expandHovered || selected ? Accent : Border;
                using (var button = new SolidBrush(buttonColor))
                using (var border = new Pen(borderColor))
                {
                    e.Graphics.FillRectangle(button, buttonBounds);
                    e.Graphics.DrawRectangle(border, buttonBounds.X, buttonBounds.Y, buttonBounds.Width - 1, buttonBounds.Height - 1);
                }

                var centerX = buttonBounds.Left + buttonBounds.Width / 2f;
                var centerY = buttonBounds.Top + buttonBounds.Height / 2f;
                PointF[] chevron = e.Node.IsExpanded
                    ? [new(centerX - 4, centerY - 2), new(centerX, centerY + 2), new(centerX + 4, centerY - 2)]
                    : [new(centerX - 2, centerY - 4), new(centerX + 2, centerY), new(centerX - 2, centerY + 4)];
                using var glyph = new Pen(selected || expandHovered ? AccentLabel : Muted, 1.6f)
                {
                    StartCap = System.Drawing.Drawing2D.LineCap.Round,
                    EndCap = System.Drawing.Drawing2D.LineCap.Round,
                    LineJoin = System.Drawing.Drawing2D.LineJoin.Round
                };
                e.Graphics.DrawLines(glyph, chevron);
                contentLeft = Math.Max(contentLeft, buttonBounds.Right + 6);
            }
            else
            {
                var centerX = Math.Max(8, contentLeft - 10);
                var centerY = e.Bounds.Top + e.Bounds.Height / 2f;
                PointF[] points = e.Node.IsExpanded
                    ? [new(centerX - 4, centerY - 2), new(centerX + 4, centerY - 2), new(centerX, centerY + 3)]
                    : [new(centerX - 2, centerY - 4), new(centerX - 2, centerY + 4), new(centerX + 3, centerY)];
                using var glyph = new SolidBrush(selected ? AccentLabel : Muted);
                e.Graphics.FillPolygon(glyph, points);
            }
        }

        var color = e.Node.ForeColor.IsEmpty ? tree.ForeColor : e.Node.ForeColor;
        if (!tree.Enabled) color = DisabledText;
        var bounds = new Rectangle(contentLeft, e.Bounds.Top, Math.Max(0, tree.ClientSize.Width - contentLeft - 6), e.Bounds.Height);
        TextRenderer.DrawText(
            e.Graphics,
            UiLocalization.T(e.Node.Text),
            tree.Font,
            bounds,
            color,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if ((e.State & TreeNodeStates.Focused) != 0)
        {
            var focusBounds = new Rectangle(4, row.Top + 1, Math.Max(0, row.Width - 8), Math.Max(0, row.Height - 2));
            ControlPaint.DrawFocusRectangle(e.Graphics, focusBounds, AccentLabel, background.Color);
        }
    }

    internal static bool IsTreeExpandGlyphHit(TreeView tree, TreeNode node, Point location)
    {
        return node.Nodes.Count > 0 && TreeExpandGlyphBounds(tree, node).Contains(location);
    }

    private static Rectangle TreeExpandGlyphBounds(TreeView tree, TreeNode node)
    {
        const int buttonSize = 16;
        var indent = Math.Max(16, tree.Indent);
        var x = 4 + node.Level * indent;
        var y = node.Bounds.Top + Math.Max(0, (node.Bounds.Height - buttonSize) / 2);
        return new Rectangle(x, y, buttonSize, buttonSize);
    }

    private static TreeNode? TreeNodeAtRow(TreeView tree, int y)
    {
        for (var node = tree.TopNode; node is not null; node = node.NextVisibleNode)
        {
            if (node.Bounds.Top > y) return null;
            if (y >= node.Bounds.Top && y < node.Bounds.Bottom) return node;
        }

        return null;
    }

    private static void DrawListViewHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        using var background = new SolidBrush(PanelStrong);
        using var border = new Pen(Border);
        e.Graphics.FillRectangle(background, e.Bounds);
        e.Graphics.DrawLine(border, e.Bounds.Right - 1, e.Bounds.Top, e.Bounds.Right - 1, e.Bounds.Bottom);
        e.Graphics.DrawLine(border, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        var bounds = Rectangle.Inflate(e.Bounds, -8, 0);
        TextRenderer.DrawText(
            e.Graphics,
            UiLocalization.T(e.Header?.Text ?? string.Empty),
            e.Font ?? SystemFonts.MessageBoxFont,
            bounds,
            Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    private static void DrawListViewItem(object? sender, DrawListViewItemEventArgs e)
    {
        if (sender is not ListView list) return;
        StyledListViews.TryGetValue(list, out var state);
        var selected = e.Item?.Selected == true;
        var hovered = state?.HoverItem == e.ItemIndex;
        using var background = new SolidBrush(selected ? AccentSurface : hovered ? PanelHover : Panel);
        e.Graphics.FillRectangle(background, e.Bounds);
        if (selected)
        {
            using var accent = new SolidBrush(Accent);
            e.Graphics.FillRectangle(accent, e.Bounds.Left, e.Bounds.Top, 3, e.Bounds.Height);
        }
    }

    private static void DrawListViewSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (sender is not ListView list) return;
        StyledListViews.TryGetValue(list, out var state);
        var selected = e.Item?.Selected == true;
        var hovered = state?.HoverItem == e.ItemIndex;
        using var background = new SolidBrush(selected ? AccentSurface : hovered ? PanelHover : Panel);
        e.Graphics.FillRectangle(background, e.Bounds);
        if (selected && e.ColumnIndex == 0)
        {
            using var accent = new SolidBrush(Accent);
            e.Graphics.FillRectangle(accent, e.Bounds.Left, e.Bounds.Top, 3, e.Bounds.Height);
        }

        var bounds = new Rectangle(e.Bounds.Left + 8, e.Bounds.Top, Math.Max(0, e.Bounds.Width - 12), e.Bounds.Height);
        TextRenderer.DrawText(
            e.Graphics,
            UiLocalization.T(e.SubItem?.Text ?? string.Empty),
            list.Font,
            bounds,
            list.Enabled ? Text : DisabledText,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    private sealed class FieldInteractionState
    {
        private readonly Control _control;
        private bool _hovered;

        public FieldInteractionState(Control control)
        {
            _control = control;
            control.MouseEnter += (_, _) =>
            {
                _hovered = true;
                Refresh();
            };
            control.MouseLeave += (_, _) =>
            {
                _hovered = false;
                Refresh();
            };
            control.Enter += (_, _) => Refresh();
            control.Leave += (_, _) => Refresh();
            control.EnabledChanged += (_, _) => Refresh();
        }

        private void Refresh()
        {
            var color = !_control.Enabled ? DisabledSurface : _control.ContainsFocus ? FieldFocus : _hovered ? FieldHover : Field;
            if (_control.BackColor != color) _control.BackColor = color;
            if (_control.ForeColor != (_control.Enabled ? Text : DisabledText)) _control.ForeColor = _control.Enabled ? Text : DisabledText;
            _control.Invalidate();
        }
    }

    private sealed class ListBoxInteractionState
    {
        private readonly ListBox _list;

        public ListBoxInteractionState(ListBox list)
        {
            _list = list;
            list.MouseMove += (_, e) => SetHover(list.IndexFromPoint(e.Location));
            list.MouseLeave += (_, _) => SetHover(-1);
        }

        public int HoverIndex { get; private set; } = -1;

        private void SetHover(int index)
        {
            if (HoverIndex == index) return;
            var previous = HoverIndex;
            HoverIndex = index;
            InvalidateItem(previous);
            InvalidateItem(HoverIndex);
        }

        private void InvalidateItem(int index)
        {
            if (index >= 0 && index < _list.Items.Count) _list.Invalidate(_list.GetItemRectangle(index));
        }
    }

    private sealed class TreeViewInteractionState
    {
        private readonly TreeView _tree;

        public TreeViewInteractionState(TreeView tree, bool useCustomExpandButtons)
        {
            _tree = tree;
            UseCustomExpandButtons = useCustomExpandButtons;
            tree.MouseMove += (_, e) => SetHover(TreeNodeAtRow(tree, e.Y), e.Location);
            tree.MouseLeave += (_, _) => SetHover(null, Point.Empty);
            tree.MouseDown += (_, e) =>
            {
                if (!UseCustomExpandButtons || e.Button != MouseButtons.Left || e.Clicks != 1) return;
                var node = TreeNodeAtRow(tree, e.Y);
                if (node is null || !IsTreeExpandGlyphHit(tree, node, e.Location)) return;
                PressedExpandNode = node;
                tree.Invalidate(new Rectangle(0, node.Bounds.Top, tree.ClientSize.Width, node.Bounds.Height));
                if (node.IsExpanded) node.Collapse(ignoreChildren: true);
                else node.Expand();
            };
            tree.MouseUp += (_, _) => ClearPressedNode();
            tree.MouseCaptureChanged += (_, _) => ClearPressedNode();
        }

        public TreeNode? HoverNode { get; private set; }
        public TreeNode? HoverExpandNode { get; private set; }
        public TreeNode? PressedExpandNode { get; private set; }
        public bool UseCustomExpandButtons { get; }

        private void SetHover(TreeNode? node, Point location)
        {
            var expandNode = UseCustomExpandButtons
                && node is not null
                && IsTreeExpandGlyphHit(_tree, node, location)
                    ? node
                    : null;
            if (HoverNode == node && HoverExpandNode == expandNode) return;
            var previous = HoverNode;
            HoverNode = node;
            HoverExpandNode = expandNode;
            if (previous is not null) _tree.Invalidate(new Rectangle(0, previous.Bounds.Top, _tree.ClientSize.Width, previous.Bounds.Height));
            if (HoverNode is not null) _tree.Invalidate(new Rectangle(0, HoverNode.Bounds.Top, _tree.ClientSize.Width, HoverNode.Bounds.Height));
        }

        private void ClearPressedNode()
        {
            var previous = PressedExpandNode;
            PressedExpandNode = null;
            if (previous is not null) _tree.Invalidate(new Rectangle(0, previous.Bounds.Top, _tree.ClientSize.Width, previous.Bounds.Height));
        }
    }

    private sealed class ListViewInteractionState
    {
        private readonly ListView _list;
        private readonly ImageList? _rowHeightImages;

        public ListViewInteractionState(ListView list)
        {
            _list = list;
            if (list.SmallImageList is null)
            {
                _rowHeightImages = new ImageList
                {
                    ColorDepth = ColorDepth.Depth32Bit,
                    ImageSize = new Size(1, 28)
                };
                list.SmallImageList = _rowHeightImages;
            }
            list.MouseMove += (_, e) => SetHover(list.GetItemAt(e.X, e.Y)?.Index ?? -1);
            list.MouseLeave += (_, _) => SetHover(-1);
            list.Disposed += (_, _) => _rowHeightImages?.Dispose();
        }

        public int HoverItem { get; private set; } = -1;

        private void SetHover(int index)
        {
            if (HoverItem == index) return;
            var previous = HoverItem;
            HoverItem = index;
            if (previous >= 0 && previous < _list.Items.Count) _list.Invalidate(_list.Items[previous].Bounds);
            if (HoverItem >= 0 && HoverItem < _list.Items.Count) _list.Invalidate(_list.Items[HoverItem].Bounds);
        }
    }

    private sealed class ModernStatusStripRenderer : ToolStripProfessionalRenderer
    {
        public ModernStatusStripRenderer()
            : base(new ModernStatusColorTable())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var border = new Pen(Border);
            e.Graphics.DrawLine(border, 0, 0, e.ToolStrip.Width, 0);
        }
    }

    private sealed class ModernStatusColorTable : ProfessionalColorTable
    {
        public override Color ToolStripGradientBegin => Top;
        public override Color ToolStripGradientMiddle => Top;
        public override Color ToolStripGradientEnd => Top;
        public override Color StatusStripGradientBegin => Top;
        public override Color StatusStripGradientEnd => Top;
        public override Color ToolStripBorder => Border;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => PanelStrong;
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hWnd, string? pszSubAppName, string? pszSubIdList);
}
