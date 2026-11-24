namespace VectorAnimationEngine;

internal sealed class TimelineTabGroupOrderDialog : ModernDialogForm
{
    private readonly List<TimelineTabGroup> _groups;
    private readonly Dictionary<string, string[]> _layerNames;
    private readonly GroupScrollHost _tags = new() { Dock = DockStyle.Fill, Margin = Padding.Empty };
    private readonly SurfaceButton _rename = new() { Text = UiLocalization.T("Rename") };
    private readonly SurfaceButton _delete = new() { Text = UiLocalization.T("Delete") };
    private readonly SurfaceButton _color = new() { Text = UiLocalization.T("Color") };
    private readonly SurfaceButton _resetColor = new() { Text = UiLocalization.T("Reset") };
    private readonly SurfaceButton _lower = new() { Text = UiLocalization.T("Move left / lower") };
    private readonly SurfaceButton _higher = new() { Text = UiLocalization.T("Move right / higher") };
    private readonly Label _empty = new()
    {
        Dock = DockStyle.Fill, Text = UiLocalization.T("No layers in this group"),
        TextAlign = ContentAlignment.MiddleCenter, ForeColor = Theme.SecondaryText
    };
    private readonly ListBox _layers = new()
    {
        Dock = DockStyle.Fill, BorderStyle = BorderStyle.None,
        BackColor = Theme.Panel, ForeColor = Theme.Text,
        DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 36,
        IntegralHeight = false, SelectionMode = SelectionMode.None
    };
    private readonly Label _layerTitle = new SingleLineLabel { ForeColor = Theme.Text, AutoEllipsis = true };
    private readonly Label _count = new() { Dock = DockStyle.Fill, ForeColor = Theme.SecondaryText, AutoEllipsis = true };
    private readonly ToolTip _toolTip = new();
    private int _dropMarkerX = -1;
    private string? _selectedId;
    private string? _dragId;
    private Point _dragStart;

    public TimelineTabGroupOrderDialog(AnimationTimeline timeline, IReadOnlyDictionary<string, string> names, Func<string, string?>? rename = null)
        : base(UiLocalization.T("Layer group order"), new Size(800, 520))
    {
        MinimumSize = new Size(680, 480);
        _groups = timeline.TabGroups.ToList();
        _layerNames = timeline.Tracks.GroupBy(track => track.TabGroupId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(track => names.GetValueOrDefault(track.TargetId, track.TargetId)).ToArray(), StringComparer.Ordinal);
        _selectedId = _groups.FirstOrDefault()?.Id;
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Margin = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 94));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 8));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.Controls.Add(new Label
        {
            Text = UiLocalization.T("Drag to reorder · Left: bottom  →  Right: top"),
            Dock = DockStyle.Fill, ForeColor = Theme.AccentLabel, AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(12, 0, 0, 0),
            BackColor = Theme.AccentSurface, Margin = new Padding(0, 0, 0, 8)
        }, 0, 0);
        layout.Controls.Add(_tags, 0, 1);
        _tags.Items.Paint += (_, e) =>
        {
            if (_dropMarkerX < 0) return;
            var scale = _tags.DeviceDpi / 96f;
            using var pen = new Pen(Theme.Accent, 2 * scale);
            e.Graphics.DrawLine(pen, _dropMarkerX + _tags.ScrollOffset, 5 * scale, _dropMarkerX + _tags.ScrollOffset, 62 * scale);
        };
        var orderActions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        foreach (var button in new[] { _lower, _higher })
        {
            StyleActionButton(button);
            button.AutoSize = true;
            button.Height = Theme.ControlHeight;
            button.Margin = new Padding(0, 0, 8, 0);
            orderActions.Controls.Add(button);
        }
        _lower.Click += (_, _) => MoveSelected(-1);
        _higher.Click += (_, _) => MoveSelected(1);
        _toolTip.SetToolTip(_lower, UiLocalization.T("Ctrl+Left/Right changes the group order"));
        _toolTip.SetToolTip(_higher, UiLocalization.T("Ctrl+Left/Right changes the group order"));
        layout.Controls.Add(orderActions, 0, 2);
        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = Padding.Empty, Padding = new Padding(0, 8, 0, 8) };
        actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        foreach (var button in new[] { _rename, _delete, _color, _resetColor })
        {
            StyleActionButton(button);
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            button.Dock = DockStyle.Fill;
            button.Height = Theme.ControlHeight;
            button.Margin = new Padding(0, 0, 8, 0);
            actions.Controls.Add(button);
        }
        _toolTip.SetToolTip(_rename, UiLocalization.T("Rename tab group"));
        _toolTip.SetToolTip(_delete, UiLocalization.T("Delete tab group"));
        _toolTip.SetToolTip(_color, UiLocalization.T("Tab group color"));
        _toolTip.SetToolTip(_resetColor, UiLocalization.T("Reset tab group color"));
        _rename.Click += (_, _) =>
        {
            var group = _groups.FirstOrDefault(item => item.Id == _selectedId);
            if (group is null || IsBuiltIn(group.Id)) return;
            var name = rename?.Invoke(group.Name);
            if (!string.IsNullOrWhiteSpace(name)) ReplaceSelected(group with { Name = name });
        };
        _delete.Click += (_, _) =>
        {
            var group = _groups.FirstOrDefault(item => item.Id == _selectedId);
            if (group is null || IsBuiltIn(group.Id)) return;
            if (ModernMessageDialog.Show(this,
                string.Format(UiLocalization.T("Delete the timeline tab group '{0}'? Tracks in it will move to Layers."), group.Name),
                UiLocalization.T("Delete Timeline Tab Group"), MessageBoxButtons.YesNo,
                MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            _groups.Remove(group);
            _selectedId = _groups.FirstOrDefault()?.Id;
            RefreshTags();
        };
        _color.Click += (_, _) =>
        {
            var group = _groups.FirstOrDefault(item => item.Id == _selectedId);
            if (group is null) return;
            using var picker = new ProfessionalColorPickerDialog(
                group.ColorArgb is { } argb ? Color.FromArgb(argb) : Theme.Accent, UiLocalization.T("Tab Group Color"));
            if (picker.ShowDialog(this) == DialogResult.OK)
                ReplaceSelected(group with { ColorArgb = picker.Color.ToArgb() });
        };
        _resetColor.Click += (_, _) =>
        {
            var group = _groups.FirstOrDefault(item => item.Id == _selectedId);
            if (group is not null) ReplaceSelected(group with { ColorArgb = null });
        };
        _layerTitle.Dock = DockStyle.Fill;
        _layerTitle.Font = Theme.UiFont(11, FontStyle.Bold);
        _layerTitle.TextAlign = ContentAlignment.MiddleLeft;
        _layerTitle.Margin = Padding.Empty;
        var detailHeader = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        detailHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        detailHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 320));
        detailHeader.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        detailHeader.Controls.Add(_layerTitle, 0, 0);
        detailHeader.Controls.Add(actions, 1, 0);
        layout.Controls.Add(detailHeader, 0, 3);
        _layers.DrawItem += (_, e) =>
        {
            if (e.Index < 0 || e.Index >= _layers.Items.Count) return;
            var scale = _layers.DeviceDpi / 96f;
            using var fill = new SolidBrush(Theme.Field);
            e.Graphics.FillRectangle(fill, e.Bounds);
            using var separator = new Pen(Theme.Mix(Theme.Field, Theme.Border, 0.30f));
            e.Graphics.DrawLine(separator, e.Bounds.Left + 14, e.Bounds.Bottom - 1, e.Bounds.Right - 14, e.Bounds.Bottom - 1);
            TextRenderer.DrawText(e.Graphics, $"{e.Index + 1:00}", Font,
                new Rectangle(e.Bounds.Left + (int)(12 * scale), e.Bounds.Top, (int)(32 * scale), e.Bounds.Height), Theme.SecondaryText,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(e.Graphics, _layers.Items[e.Index]?.ToString(), Font,
                new Rectangle(e.Bounds.Left + (int)(48 * scale), e.Bounds.Top,
                    Math.Max(0, e.Bounds.Width - (int)(60 * scale)), e.Bounds.Height), Theme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        };
        _layers.BackColor = Theme.Field;
        _layers.Margin = new Padding(0, 0, 0, 8);
        _empty.BackColor = Theme.Field;
        var layerHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(1), Margin = new Padding(0, 0, 0, 8), BackColor = Theme.Mix(Theme.Field, Theme.Border, 0.45f) };
        layerHost.Controls.Add(_layers);
        layerHost.Controls.Add(_empty);
        layout.Controls.Add(layerHost, 0, 5);
        layout.Controls.Add(_count, 0, 6);
        DialogContent.Controls.Add(layout);
        var ok = AddDialogAction(UiLocalization.T("OK"), DialogResult.OK, DialogActionStyle.Primary);
        var cancel = AddDialogAction(UiLocalization.T("Cancel"), DialogResult.Cancel);
        AcceptButton = ok;
        CancelButton = cancel;
        RefreshTags();
        UiLocalization.Watch(this);
    }

    public TimelineTabGroup[] Groups => _groups.ToArray();

    private static void PaintGroupTag(Graphics graphics, Button tag, int index, string name, int layerCount,
        Color groupColor, bool selected, bool hovered)
    {
        var scale = tag.DeviceDpi / 96f;
        int S(int value) => (int)Math.Round(value * scale);
        var background = Theme.Mix(Theme.PanelStrong, groupColor, selected ? 0.18f : hovered ? 0.10f : 0.03f);
        using var fill = new SolidBrush(background);
        using var border = new Pen(selected ? Theme.Accent : Theme.Mix(background, Theme.Border, hovered ? 0.85f : 0.40f), selected ? 1.5f * scale : scale);
        Theme.PaintUiSurface(graphics, fill, border, tag.ClientRectangle);
        using var dot = new SolidBrush(groupColor);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.FillEllipse(dot, S(14), S(13), S(7), S(7));
        using var indexFont = Theme.UiFont(8f, FontStyle.Bold);
        using var nameFont = Theme.UiFont(10f, FontStyle.Bold);
        using var countFont = Theme.UiFont(8.3f);
        TextRenderer.DrawText(graphics, $"{index + 1:00}", indexFont,
            new Rectangle(S(27), S(6), tag.Width - S(42), S(20)), Theme.SecondaryText,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(graphics, name, nameFont,
            new Rectangle(S(12), S(25), tag.Width - S(24), S(22)), Theme.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(graphics, string.Format(UiLocalization.T("{0} layers"), layerCount), countFont,
            new Rectangle(S(12), S(48), tag.Width - S(24), S(18)), Theme.SecondaryText,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        // A grip makes the drag affordance visible without adding another command.
        using var grip = new SolidBrush(Theme.Mix(background, Theme.Text, selected ? 0.60f : 0.35f));
        for (var row = 0; row < 3; row++)
            for (var column = 0; column < 2; column++)
                graphics.FillEllipse(grip, tag.Width - S(22) + S(column * 5), S(10 + row * 5), S(2), S(2));
        if (tag.Focused) Theme.DrawKeyboardFocus(graphics, Rectangle.Inflate(tag.ClientRectangle, -S(6), -S(5)), Theme.AccentLabel, background);
    }

    private static void StyleActionButton(SurfaceButton button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.BackColor = Theme.PanelStrong;
        button.ForeColor = Theme.Text;
        button.Cursor = Cursors.Hand;
        button.Padding = new Padding(10, 0, 10, 0);
        var hover = false;
        var pressed = false;
        button.MouseEnter += (_, _) => { hover = true; button.Invalidate(); };
        button.MouseLeave += (_, _) => { hover = false; button.Invalidate(); };
        button.MouseDown += (_, _) => { pressed = true; button.Invalidate(); };
        button.MouseUp += (_, _) => { pressed = false; button.Invalidate(); };
        button.MouseCaptureChanged += (_, _) => { if (!button.Capture) pressed = false; button.Invalidate(); };
        button.PaintContent = graphics =>
        {
            var background = !button.Enabled ? Theme.Panel : pressed ? Theme.AccentPressedSurface
                : hover ? Theme.PanelHover : Theme.PanelStrong;
            using var fill = new SolidBrush(background);
            using var border = new Pen(Theme.Mix(background, Theme.Border, hover ? 0.8f : 0.35f));
            Theme.PaintUiSurface(graphics, fill, border, button.ClientRectangle);
            TextRenderer.DrawText(graphics, button.Text, button.Font,
                Rectangle.Inflate(button.ClientRectangle, -4, -2),
                button.Enabled ? Theme.Text : Theme.DisabledText,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
            if (button.Focused) Theme.DrawKeyboardFocus(graphics, Rectangle.Inflate(button.ClientRectangle, -5, -4));
        };
    }

    private static bool IsBuiltIn(string id) => id is AnimationTimeline.DefaultTabGroupId or AnimationTimeline.TerrainTabGroupId or AnimationTimeline.ShotTabGroupId;

    private void ReplaceSelected(TimelineTabGroup group)
    {
        var index = _groups.FindIndex(item => item.Id == group.Id);
        if (index < 0) return;
        _groups[index] = group;
        RefreshTags();
    }

    public string[] OrderedIds => _groups.Select(group => group.Id).ToArray();

    private static string GroupName(TimelineTabGroup group) => group.Id switch
    {
        AnimationTimeline.DefaultTabGroupId => UiLocalization.T("Layers"),
        AnimationTimeline.TerrainTabGroupId => UiLocalization.T("Terrain"),
        AnimationTimeline.ShotTabGroupId => UiLocalization.T("Shots"),
        _ => group.Name
    };

    private void MoveSelected(int delta)
    {
        var index = _groups.FindIndex(group => group.Id == _selectedId);
        if (index < 0) return;
        MoveGroup(index, Math.Clamp(index + delta, 0, _groups.Count - 1));
    }

    private void MoveGroup(int from, int to)
    {
        if (from < 0 || from == to) return;
        var group = _groups[from];
        _groups.RemoveAt(from);
        _groups.Insert(to, group);
        _selectedId = group.Id;
        // Defer recreation until OLE has finished its drop callback.
        if (IsHandleCreated) BeginInvoke((Action)(() => { if (!IsDisposed) RefreshTags(); }));
        else RefreshTags();
    }

    private void RefreshTags()
    {
        var restoreFocus = _tags.ContainsFocus;
        var scrollX = -_tags.AutoScrollPosition.X;
        var scale = DeviceDpi / 96f;
        _tags.Items.SuspendLayout();
        foreach (var child in _tags.Items.Controls.Cast<Control>().ToArray()) child.Dispose();
        for (var index = 0; index < _groups.Count; index++)
        {
            var group = _groups[index];
            var text = GroupName(group);
            var layers = _layerNames.GetValueOrDefault(group.Id, []);
            var width = Math.Clamp(TextRenderer.MeasureText(text, Font).Width + (int)(62 * scale), (int)(140 * scale), (int)(240 * scale));
            var tag = new SurfaceButton
            {
                Text = string.Empty,
                Tag = group.Id, Width = width, UseMnemonic = false,
                Height = (int)(72 * scale), Margin = new Padding(0, 0, (int)(10 * scale), 0), AllowDrop = true, AutoEllipsis = true,
                AccessibleName = $"{index + 1} · {text}", AccessibleDescription = UiLocalization.T("Ctrl+Left/Right changes the group order")
            };
            tag.FlatStyle = FlatStyle.Flat;
            tag.BackColor = Theme.PanelStrong;
            tag.ForeColor = Theme.Text;
            tag.Cursor = Cursors.Hand;
            tag.FlatAppearance.BorderSize = group.Id == _selectedId ? 2 : 1;
            tag.FlatAppearance.BorderColor = group.Id == _selectedId ? Theme.Accent : Theme.Border;
            var customColor = group.ColorArgb is { } argb ? Color.FromArgb(argb) : Theme.Accent;
            var hovered = false;
            var ordinal = index;
            tag.MouseEnter += (_, _) => { hovered = true; tag.Invalidate(); };
            tag.MouseLeave += (_, _) => { hovered = false; tag.Invalidate(); };
            tag.PaintContent = graphics => PaintGroupTag(graphics, tag, ordinal, text, layers.Length,
                customColor, group.Id == _selectedId, hovered);
            _toolTip.SetToolTip(tag, $"{index + 1} · {text}\n" + tag.AccessibleDescription
                + (layers.Length == 0 ? "" : "\n" + string.Join("\n", layers.Take(8))));
            tag.Click += (_, _) => SelectGroup(group.Id);
            tag.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { _dragStart = e.Location; _dragId = group.Id; } };
            tag.MouseMove += (_, e) =>
            {
                if (e.Button != MouseButtons.Left || _dragId != group.Id
                    || new Rectangle(_dragStart.X - SystemInformation.DragSize.Width / 2, _dragStart.Y - SystemInformation.DragSize.Height / 2,
                        SystemInformation.DragSize.Width, SystemInformation.DragSize.Height).Contains(e.Location)) return;
                tag.DragSource = true;
                tag.Invalidate();
                tag.Update();
                try { tag.DoDragDrop(group.Id, DragDropEffects.Move); }
                finally
                {
                    tag.DragSource = false;
                    if (!tag.IsDisposed) tag.Invalidate();
                    _dragId = null;
                    _dropMarkerX = -1;
                    _tags.Items.Invalidate();
                }
            };
            tag.DragOver += (_, e) =>
            {
                e.Effect = _dragId is not null && e.Data?.GetData(typeof(string)) as string == _dragId
                    ? DragDropEffects.Move : DragDropEffects.None;
                var after = tag.PointToClient(new Point(e.X, e.Y)).X >= tag.Width / 2;
                _dropMarkerX = (after ? tag.Right + 1 : tag.Left - 2) - _tags.ScrollOffset;
                _tags.Items.Invalidate();
                var mouse = _tags.PointToClient(new Point(e.X, e.Y));
                if (mouse.X < 24 || mouse.X > _tags.ClientSize.Width - 24)
                    _tags.AutoScrollPosition = new Point(Math.Max(0, -_tags.AutoScrollPosition.X + (mouse.X < 24 ? -24 : 24)), 0);
            };
            tag.DragLeave += (_, _) => { _dropMarkerX = -1; _tags.Items.Invalidate(); };
            tag.DragDrop += (_, e) =>
            {
                if (_dragId is null || e.Data?.GetData(typeof(string)) as string != _dragId) return;
                var from = _groups.FindIndex(item => item.Id == _dragId);
                var target = _groups.FindIndex(item => item.Id == group.Id);
                var after = tag.PointToClient(new Point(e.X, e.Y)).X >= tag.Width / 2;
                var insertion = target + (after ? 1 : 0);
                if (from < insertion) insertion--;
                MoveGroup(from, Math.Clamp(insertion, 0, _groups.Count - 1));
            };
            tag.KeyDown += (_, e) =>
            {
                if (e.Control && e.KeyCode is Keys.Left or Keys.Right)
                {
                    _selectedId = group.Id;
                    MoveSelected(e.KeyCode == Keys.Left ? -1 : 1);
                    e.Handled = e.SuppressKeyPress = true;
                }
            };
            _tags.Items.Controls.Add(tag);
        }
        _tags.Items.ResumeLayout();
        _tags.AutoScrollPosition = new Point(scrollX, 0);
        SelectGroup(_selectedId);
        var selected = _tags.Items.Controls.OfType<Button>().FirstOrDefault(tag => tag.Tag as string == _selectedId);
        if (selected is not null)
        {
            _tags.EnsureVisible(selected);
            if (restoreFocus) selected.Focus();
        }
    }

    private void SelectGroup(string? id)
    {
        _selectedId = id;
        var selectedGroup = _groups.FirstOrDefault(item => item.Id == id);
        _layerTitle.Text = selectedGroup is null ? UiLocalization.T("Layers")
            : $"{GroupName(selectedGroup)}  /  {string.Format(UiLocalization.T("{0} layers"), _layerNames.GetValueOrDefault(selectedGroup.Id, []).Length)}";
        _layerTitle.AccessibleName = _layerTitle.Text;
        _toolTip.SetToolTip(_layerTitle, _layerTitle.Text);
        _layers.ItemHeight = (int)Math.Round(36 * DeviceDpi / 96f);
        _layers.BeginUpdate();
        _layers.Items.Clear();
        if (id is not null) _layers.Items.AddRange(_layerNames.GetValueOrDefault(id, []).Cast<object>().ToArray());
        _layers.EndUpdate();
        _layers.Visible = _layers.Items.Count > 0;
        _empty.Visible = _layers.Items.Count == 0;
        if (_layers.Items.Count == 0) _empty.BringToFront();
        _count.Text = string.Format(UiLocalization.T("{0}/32 custom groups · Ctrl+Left/Right to reorder"), _groups.Count(group => !IsBuiltIn(group.Id)));
        var group = _groups.FirstOrDefault(item => item.Id == id);
        _rename.Enabled = _delete.Enabled = group is not null && !IsBuiltIn(group.Id);
        _color.Enabled = group is not null;
        _resetColor.Enabled = group?.ColorArgb is not null;
        var selectedIndex = _groups.FindIndex(item => item.Id == id);
        _lower.Enabled = selectedIndex > 0;
        _higher.Enabled = selectedIndex >= 0 && selectedIndex < _groups.Count - 1;
        foreach (var tag in _tags.Items.Controls.OfType<Button>())
        {
            tag.FlatAppearance.BorderSize = tag.Tag as string == id ? 2 : 1;
            tag.FlatAppearance.BorderColor = tag.Tag as string == id ? Theme.Accent : Theme.Border;
            tag.Invalidate();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _toolTip.Dispose();
        base.Dispose(disposing);
    }

    // Paint after the shared corner adapter so custom typography survives both
    // square and rounded application themes. Button retains keyboard/UIA behavior.
    private sealed class SurfaceButton : Button
    {
        internal Action<Graphics>? PaintContent { get; set; }
        internal bool DragSource { get; set; }

        internal SurfaceButton() => DoubleBuffered = true;

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            PaintContent?.Invoke(e.Graphics);
            if (!DragSource) return;
            using var veil = new SolidBrush(Color.FromArgb(191, Theme.Panel));
            e.Graphics.FillRectangle(veil, ClientRectangle);
        }
    }

    private sealed class SingleLineLabel : Label
    {
        protected override void OnPaint(PaintEventArgs e) => TextRenderer.DrawText(e.Graphics, Text, Font,
            ClientRectangle, ForeColor, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis
                | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }
    private sealed class GroupScrollHost : Panel
    {
        private readonly Panel _viewport = new() { Dock = DockStyle.None };
        internal FlowLayoutPanel Items { get; } = new()
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(2, 6, 2, 6), Margin = Padding.Empty
        };
        private int _offset;
        private bool _dragging;
        private bool _hover;
        private int _grab;
        internal int ScrollOffset => _offset;
        private int Maximum => Math.Max(0, Items.Width - _viewport.Width);
        internal Point AutoScrollPosition
        {
            get => new(-_offset, 0);
            set => ScrollTo(value.X);
        }

        internal GroupScrollHost()
        {
            DoubleBuffered = true;
            BackColor = Theme.Panel;
            _viewport.Controls.Add(Items);
            Controls.Add(_viewport);
            Items.SizeChanged += (_, _) => { ScrollTo(_offset); Parent?.PerformLayout(); };
            Items.MouseWheel += (_, e) => ScrollTo(_offset - Math.Sign(e.Delta) * 64);
            _viewport.MouseWheel += (_, e) => ScrollTo(_offset - Math.Sign(e.Delta) * 64);
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            var contentHeight = Items.GetPreferredSize(Size.Empty).Height;
            var minimumHeight = (int)Math.Ceiling(60 * DeviceDpi / 96f);
            return new Size(proposedSize.Width > 0 ? proposedSize.Width : Width,
                Math.Max(contentHeight, minimumHeight) + RailHeight);
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            _viewport.SetBounds(0, 0, ClientSize.Width, Math.Max(0, ClientSize.Height - RailHeight));
            ScrollTo(_offset);
        }

        private int RailHeight => Math.Max(10, (int)Math.Round(12 * DeviceDpi / 96f));
        private Rectangle Rail => new(6, Height - RailHeight + RailHeight / 3, Math.Max(0, Width - 12), Math.Max(3, RailHeight / 3));
        private Rectangle Thumb
        {
            get
            {
                var rail = Rail;
                var width = Math.Min(rail.Width, Math.Max(32, (int)(rail.Width * (_viewport.Width / (double)Math.Max(1, Items.Width)))));
                var travel = rail.Width - width;
                return new Rectangle(rail.Left + (Maximum == 0 ? 0 : (int)(travel * (_offset / (double)Maximum))), rail.Top, width, rail.Height);
            }
        }

        private void ScrollTo(int offset)
        {
            _offset = Math.Clamp(offset, 0, Maximum);
            Items.Location = new Point(-_offset, 0);
            Invalidate();
        }

        internal void EnsureVisible(Control item)
        {
            if (item.Left < _offset) ScrollTo(item.Left);
            else if (item.Right > _offset + _viewport.Width) ScrollTo(item.Right - _viewport.Width);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Maximum <= 0) return;
            using var track = new SolidBrush(Theme.Mix(Theme.Panel, Theme.ControlOutline, 0.3f));
            using var thumb = new SolidBrush(SystemInformation.HighContrast ? SystemColors.Highlight
                : _dragging ? Theme.Accent : _hover ? Theme.SecondaryText : Theme.ControlOutline);
            Theme.FillUiRectangle(e.Graphics, track, Rail);
            Theme.FillUiRectangle(e.Graphics, thumb, Thumb);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || Maximum <= 0 || e.Y < Height - RailHeight) return;
            var thumb = Thumb;
            if (e.X < thumb.Left || e.X > thumb.Right) ScrollTo(_offset + (e.X < thumb.Left ? -_viewport.Width : _viewport.Width));
            else { _grab = e.X - thumb.Left; _dragging = true; Capture = true; }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            _hover = e.Y >= Height - RailHeight;
            if (_dragging)
            {
                var travel = Rail.Width - Thumb.Width;
                if (travel > 0) ScrollTo((int)Math.Round((e.X - _grab - Rail.Left) * Maximum / (double)travel));
            }
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragging = false; Capture = false; Invalidate();
        }
        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture) _dragging = false;
            Invalidate();
        }
        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e); _hover = false; Invalidate();
        }
    }

}
