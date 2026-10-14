namespace VectorAnimationEngine;

internal sealed class TimelineTabGroupOrderDialog : ModernDialogForm
{
    private readonly List<TimelineTabGroup> _groups;
    private readonly Dictionary<string, string[]> _layerNames;
    private readonly GroupScrollHost _tags = new() { Dock = DockStyle.Fill, Margin = Padding.Empty };
    private readonly Button _rename = new() { Text = UiLocalization.T("Rename tab group") };
    private readonly Button _delete = new() { Text = UiLocalization.T("Delete tab group") };
    private readonly Button _color = new() { Text = UiLocalization.T("Tab group color") };
    private readonly Button _resetColor = new() { Text = UiLocalization.T("Reset tab group color") };
    private readonly ListBox _layers = new()
    {
        Dock = DockStyle.Fill, BorderStyle = BorderStyle.None,
        BackColor = Theme.Panel, ForeColor = Theme.Text,
        DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 36,
        IntegralHeight = false, SelectionMode = SelectionMode.None
    };
    private readonly Label _layerTitle = InspectorModuleStyle.CreateTitle("Layers");
    private readonly Label _count = new() { Dock = DockStyle.Fill, ForeColor = Theme.SecondaryText, AutoEllipsis = true };
    private readonly ToolTip _toolTip = new();
    private int _dropMarkerX = -1;
    private string? _selectedId;
    private string? _dragId;
    private Point _dragStart;

    public TimelineTabGroupOrderDialog(AnimationTimeline timeline, IReadOnlyDictionary<string, string> names, Func<string, string?>? rename = null)
        : base(UiLocalization.T("Layer group order"), ManagementDialogSize)
    {
        _groups = timeline.TabGroups.ToList();
        _layerNames = timeline.Tracks.GroupBy(track => track.TabGroupId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(track => names.GetValueOrDefault(track.TargetId, track.TargetId)).ToArray(), StringComparer.Ordinal);
        _selectedId = _groups.FirstOrDefault()?.Id;
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Margin = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.Controls.Add(new Label
        {
            Text = UiLocalization.T("Drag to reorder · Left: bottom  →  Right: top"),
            Dock = DockStyle.Fill, ForeColor = Theme.SecondaryText, AutoEllipsis = true, Margin = Padding.Empty
        }, 0, 0);
        layout.Controls.Add(_tags, 0, 1);
        _tags.Items.Paint += (_, e) =>
        {
            if (_dropMarkerX < 0) return;
            using var pen = new Pen(Theme.Accent, 2);
            e.Graphics.DrawLine(pen, _dropMarkerX + _tags.ScrollOffset, 5, _dropMarkerX + _tags.ScrollOffset, 54);
        };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        foreach (var button in new[] { _rename, _delete, _color, _resetColor })
        {
            Theme.StyleButton(button);
            button.AutoSize = true;
            button.Height = 28;
            button.Margin = new Padding(0, 4, 8, 0);
            actions.Controls.Add(button);
        }
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
            picker.ShowDialog(this);
            ReplaceSelected(group with { ColorArgb = picker.Color.ToArgb() });
        };
        _resetColor.Click += (_, _) =>
        {
            var group = _groups.FirstOrDefault(item => item.Id == _selectedId);
            if (group is not null) ReplaceSelected(group with { ColorArgb = null });
        };
        var sectionHeader = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty
        };
        sectionHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        sectionHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _layerTitle.Dock = DockStyle.Fill;
        _layerTitle.TextAlign = ContentAlignment.MiddleLeft;
        _layerTitle.Margin = Padding.Empty;
        sectionHeader.Controls.Add(_layerTitle, 0, 0);
        sectionHeader.Controls.Add(actions, 1, 0);
        layout.Controls.Add(sectionHeader, 0, 2);
        _layers.DrawItem += (_, e) =>
        {
            if (e.Index < 0 || e.Index >= _layers.Items.Count) return;
            var scale = _layers.DeviceDpi / 96f;
            using var fill = new SolidBrush(e.Index % 2 == 0 ? Theme.Mix(Theme.Panel, Theme.PanelStrong, 0.45f) : Theme.Panel);
            e.Graphics.FillRectangle(fill, e.Bounds);
            TextRenderer.DrawText(e.Graphics, _layers.Items[e.Index]?.ToString(), Font,
                Rectangle.Inflate(e.Bounds, -(int)(12 * scale), 0), Theme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        };
        _layers.Margin = new Padding(0, 0, 0, 8);
        layout.Controls.Add(_layers, 0, 3);
        layout.Controls.Add(_count, 0, 4);
        DialogContent.Controls.Add(layout);
        var ok = AddDialogAction(UiLocalization.T("OK"), DialogResult.OK, DialogActionStyle.Primary);
        var cancel = AddDialogAction(UiLocalization.T("Cancel"), DialogResult.Cancel);
        AcceptButton = ok;
        CancelButton = cancel;
        RefreshTags();
        UiLocalization.Watch(this);
    }

    public TimelineTabGroup[] Groups => _groups.ToArray();

    private static bool IsBuiltIn(string id) => id is AnimationTimeline.DefaultTabGroupId or AnimationTimeline.TerrainTabGroupId;

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
        if (IsHandleCreated) BeginInvoke((Action)RefreshTags);
        else RefreshTags();
    }

    private void RefreshTags()
    {
        var restoreFocus = _tags.ContainsFocus;
        var scrollX = -_tags.AutoScrollPosition.X;
        _tags.Items.SuspendLayout();
        foreach (var child in _tags.Items.Controls.Cast<Control>().ToArray()) child.Dispose();
        for (var index = 0; index < _groups.Count; index++)
        {
            var group = _groups[index];
            var text = GroupName(group);
            var tag = new Button
            {
                Text = text, Tag = group.Id, Width = Math.Clamp(TextRenderer.MeasureText(text, Font).Width + 30, 100, 200),
                Height = 42, Margin = new Padding(3), AllowDrop = true, AutoEllipsis = true,
                AccessibleName = $"{index + 1} · {text}", AccessibleDescription = UiLocalization.T("Ctrl+Left/Right changes the group order")
            };
            Theme.StyleButton(tag);
            tag.BackColor = group.ColorArgb is { } argb ? Color.FromArgb(argb) : Theme.PanelStrong;
            tag.ForeColor = Theme.ReadableText(tag.BackColor, Theme.Text);
            tag.FlatAppearance.BorderSize = group.Id == _selectedId ? 2 : 1;
            tag.FlatAppearance.BorderColor = group.Id == _selectedId ? Theme.Accent : Theme.Border;
            var layers = _layerNames.GetValueOrDefault(group.Id, []);
            _toolTip.SetToolTip(tag, $"{index + 1} · {text}\n" + tag.AccessibleDescription
                + (layers.Length == 0 ? "" : "\n" + string.Join("\n", layers.Take(8))));
            tag.Click += (_, _) => SelectGroup(group.Id);
            tag.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { _dragStart = e.Location; _dragId = group.Id; } };
            tag.MouseMove += (_, e) =>
            {
                if (e.Button != MouseButtons.Left || _dragId != group.Id
                    || new Rectangle(_dragStart.X - SystemInformation.DragSize.Width / 2, _dragStart.Y - SystemInformation.DragSize.Height / 2,
                        SystemInformation.DragSize.Width, SystemInformation.DragSize.Height).Contains(e.Location)) return;
                try { tag.DoDragDrop(group.Id, DragDropEffects.Move); }
                finally { _dragId = null; _dropMarkerX = -1; _tags.Items.Invalidate(); }
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
        if (restoreFocus)
        {
            var selected = _tags.Items.Controls.OfType<Button>().FirstOrDefault(tag => tag.Tag as string == _selectedId);
            if (selected is not null)
            {
                _tags.EnsureVisible(selected);
                selected.Focus();
            }
        }
    }

    private void SelectGroup(string? id)
    {
        _selectedId = id;
        var selectedGroup = _groups.FirstOrDefault(item => item.Id == id);
        _layerTitle.Text = selectedGroup is null ? UiLocalization.T("Layers")
            : $"{GroupName(selectedGroup)} · {UiLocalization.T("Layers")}";
        _layers.ItemHeight = (int)Math.Round(36 * DeviceDpi / 96f);
        _layers.BeginUpdate();
        _layers.Items.Clear();
        if (id is not null) _layers.Items.AddRange(_layerNames.GetValueOrDefault(id, []).Cast<object>().ToArray());
        _layers.EndUpdate();
        _count.Text = string.Format(UiLocalization.T("{0}/32 custom groups · Ctrl+Left/Right to reorder"), Math.Max(0, _groups.Count - 2));
        var group = _groups.FirstOrDefault(item => item.Id == id);
        _rename.Enabled = _delete.Enabled = group is not null && !IsBuiltIn(group.Id);
        _color.Enabled = group is not null;
        _resetColor.Enabled = group?.ColorArgb is not null;
        foreach (var tag in _tags.Items.Controls.OfType<Button>())
        {
            tag.FlatAppearance.BorderSize = tag.Tag as string == id ? 2 : 1;
            tag.FlatAppearance.BorderColor = tag.Tag as string == id ? Theme.Accent : Theme.Border;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _toolTip.Dispose();
        base.Dispose(disposing);
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
