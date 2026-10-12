namespace VectorAnimationEngine;

internal sealed class TimelineTabGroupOrderDialog : ModernDialogForm
{
    private readonly List<TimelineTabGroup> _groups;
    private readonly Dictionary<string, string[]> _layerNames;
    private readonly FlowLayoutPanel _tags = new()
    {
        Dock = DockStyle.Fill, AutoScroll = true, WrapContents = false,
        FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(2, 6, 2, 6)
    };
    private readonly ToolTip _toolTip = new();
    private int _dropMarkerX = -1;
    private string? _selectedId;
    private string? _dragId;
    private Point _dragStart;

    public TimelineTabGroupOrderDialog(AnimationTimeline timeline, IReadOnlyDictionary<string, string> names)
        : base(UiLocalization.T("Layer group order"), new Size(720, 280))
    {
        _groups = timeline.TabGroups.ToList();
        _layerNames = timeline.Tracks.GroupBy(track => track.TabGroupId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(track => names.GetValueOrDefault(track.TargetId, track.TargetId)).ToArray(), StringComparer.Ordinal);
        _selectedId = _groups.FirstOrDefault()?.Id;
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.Controls.Add(new Label
        {
            Text = UiLocalization.T("Drag to reorder · Left: bottom  →  Right: top"),
            Dock = DockStyle.Fill, ForeColor = Theme.Text, AutoEllipsis = true
        }, 0, 0);
        layout.Controls.Add(_tags, 0, 1);
        _tags.Paint += (_, e) =>
        {
            if (_dropMarkerX < 0) return;
            using var pen = new Pen(Theme.Accent, 2);
            e.Graphics.DrawLine(pen, _dropMarkerX, 5, _dropMarkerX, 54);
        };
        layout.Controls.Add(new Label
        {
            Text = string.Format(UiLocalization.T("{0}/32 custom groups · Ctrl+Left/Right to reorder"), Math.Max(0, _groups.Count - 2)),
            Dock = DockStyle.Fill, ForeColor = Theme.Muted, AutoEllipsis = true
        }, 0, 2);
        DialogContent.Controls.Add(layout);
        var ok = AddDialogAction(UiLocalization.T("OK"), DialogResult.OK, DialogActionStyle.Primary);
        var cancel = AddDialogAction(UiLocalization.T("Cancel"), DialogResult.Cancel);
        AcceptButton = ok;
        CancelButton = cancel;
        RefreshTags();
        UiLocalization.Watch(this);
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
        _tags.SuspendLayout();
        foreach (var child in _tags.Controls.Cast<Control>().ToArray()) child.Dispose();
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
                finally { _dragId = null; _dropMarkerX = -1; _tags.Invalidate(); }
            };
            tag.DragOver += (_, e) =>
            {
                e.Effect = _dragId is not null && e.Data?.GetData(typeof(string)) as string == _dragId
                    ? DragDropEffects.Move : DragDropEffects.None;
                var after = tag.PointToClient(new Point(e.X, e.Y)).X >= tag.Width / 2;
                _dropMarkerX = after ? tag.Right + 1 : tag.Left - 2;
                _tags.Invalidate();
                var mouse = _tags.PointToClient(new Point(e.X, e.Y));
                if (mouse.X < 24 || mouse.X > _tags.ClientSize.Width - 24)
                    _tags.AutoScrollPosition = new Point(Math.Max(0, -_tags.AutoScrollPosition.X + (mouse.X < 24 ? -24 : 24)), 0);
            };
            tag.DragLeave += (_, _) => { _dropMarkerX = -1; _tags.Invalidate(); };
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
            _tags.Controls.Add(tag);
        }
        _tags.ResumeLayout();
        _tags.AutoScrollPosition = new Point(scrollX, 0);
        SelectGroup(_selectedId);
        if (restoreFocus)
        {
            var selected = _tags.Controls.OfType<Button>().FirstOrDefault(tag => tag.Tag as string == _selectedId);
            if (selected is not null)
            {
                _tags.ScrollControlIntoView(selected);
                selected.Focus();
            }
        }
    }

    private void SelectGroup(string? id)
    {
        _selectedId = id;
        foreach (var tag in _tags.Controls.OfType<Button>())
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
}
