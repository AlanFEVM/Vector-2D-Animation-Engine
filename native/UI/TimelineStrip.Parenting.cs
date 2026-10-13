using System.Drawing.Drawing2D;

namespace VectorAnimationEngine;

internal sealed class TimelineParentRequestedEventArgs(string trackId, string parentTrackId) : EventArgs
{
    public string TrackId { get; } = trackId;
    public string ParentTrackId { get; } = parentTrackId;
}

internal sealed partial class TimelineStrip
{
    private const int MotionParentToolbarWidth = AddLayerButtonWidth;
    private bool _motionParentingAvailable;
    private bool _motionParentingVisible;
    private string? _motionParentDragTrackId;
    private Point _motionParentPointer;
    private string? _motionParentHoverTrackId;
    public Func<string, string>? MotionParentTargetResolver { get; set; }
    public Func<string, string, bool>? CanMotionParent { get; set; }
    public event EventHandler<TimelineParentRequestedEventArgs>? MotionParentRequested;

    public bool MotionParentingAvailable
    {
        get => _motionParentingAvailable;
        set { if (_motionParentingAvailable == value) return; _motionParentingAvailable = value; CancelMotionParentingDrag(); Invalidate(); }
    }

    public bool MotionParentingVisible
    {
        get => _motionParentingVisible;
        set { if (_motionParentingVisible == value) return; _motionParentingVisible = value; CancelMotionParentingDrag(); Invalidate(); }
    }

    private int MotionParentingColumnWidth => MotionParentingAvailable && MotionParentingVisible ? ScaleTimelineMetric(112) : 0;
    private static string ParentingText(string english, string chinese) => UiLocalization.CurrentLanguage == UiLanguage.SimplifiedChinese ? chinese : UiLocalization.T(english);
    private static string ParentingTitle => ParentingText("Layer parenting (2D edit following)", "图层父子关系（二维编辑跟随）");
    private Rectangle MotionParentToggleBounds(TimelineLayout layout)
    {
        if (!MotionParentingAvailable) return Rectangle.Empty;
        var addLayer = AddLayerButtonBounds(layout);
        return new Rectangle(addLayer.Right + ScaleTimelineMetric(2), addLayer.Top,
            addLayer.Width, addLayer.Height);
    }
    private Rectangle MotionParentColumnBounds(TimelineLayout layout) => new(layout.TrackLeft - MotionParentingColumnWidth, HeaderHeight,
        Math.Max(0, MotionParentingColumnWidth - VerticalScrollWidth), Math.Max(0, layout.RowBottom - HeaderHeight));

    private void DrawParentingTimelineContent(Graphics graphics, TimelineLayout layout, Rectangle clip)
    {
        if (MotionParentingColumnWidth == 0)
        {
            DrawRuler(graphics, layout, clip);
            DrawTrackRows(graphics, layout, clip);
            return;
        }
        var gutterRight = layout.TrackLeft - MotionParentingColumnWidth;
        var gutterLayout = layout with { TrackLeft = gutterRight };
        var state = graphics.Save();
        var gutterClip = Rectangle.Intersect(clip, new Rectangle(0, HeaderHeight, Math.Max(0, gutterRight), Math.Max(0, Height - HeaderHeight)));
        graphics.SetClip(gutterClip, CombineMode.Intersect);
        DrawRuler(graphics, gutterLayout, gutterClip);
        DrawTrackRows(graphics, gutterLayout, gutterClip);
        graphics.Restore(state);
        state = graphics.Save();
        var frameClip = Rectangle.Intersect(clip, new Rectangle(layout.TrackLeft, HeaderHeight, Math.Max(0, Width - layout.TrackLeft), Math.Max(0, Height - HeaderHeight)));
        graphics.SetClip(frameClip, CombineMode.Intersect);
        DrawRuler(graphics, layout, frameClip);
        DrawTrackRows(graphics, layout, frameClip);
        graphics.Restore(state);
    }

    private void DrawMotionParenting(Graphics graphics, TimelineLayout layout)
    {
        if (!MotionParentingAvailable) return;
        var toggle = MotionParentToggleBounds(layout);
        using var panel = new SolidBrush(Theme.Top);
        _headerIconCache.Draw(graphics, SvgIconKind.LayerParenting, toggle,
            Theme.ReadableUiColor(Theme.Panel, Enabled && MotionParentingVisible ? Theme.Accent : Theme.Muted));
        if (Focused) ControlPaint.DrawFocusRectangle(graphics, Rectangle.Inflate(toggle, -2, -2), Theme.Text, Theme.Panel);
        if (MotionParentingColumnWidth == 0) return;
        var bounds = MotionParentColumnBounds(layout);
        var state = graphics.Save();
        graphics.SetClip(bounds, CombineMode.Intersect);
        using var body = new SolidBrush(Theme.PanelStrong);
        graphics.FillRectangle(body, bounds);
        graphics.FillRectangle(panel, bounds.Left, bounds.Top, bounds.Width, RulerHeight);
        using var border = new Pen(Theme.Border);
        graphics.DrawLine(border, bounds.Left, bounds.Top, bounds.Left, bounds.Bottom);
        graphics.DrawLine(border, bounds.Right - 1, bounds.Top, bounds.Right - 1, bounds.Bottom);
        graphics.DrawLine(border, bounds.Left, layout.RowTop - 1, bounds.Right, layout.RowTop - 1);
        using var headerFont = Theme.UiFont(8);
        TextRenderer.DrawText(graphics, ParentingText("Parent links", "父子关系"), headerFont,
            new Rectangle(bounds.Left + ScaleTimelineMetric(8), HeaderHeight, Math.Max(0, bounds.Width - ScaleTimelineMetric(16)), RulerHeight),
            Theme.ReadableText(Theme.Top, Theme.Muted), TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter
                | TextFormatFlags.SingleLine | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.NoPadding);
        graphics.SetClip(new Rectangle(bounds.Left, layout.RowTop, bounds.Width,
            Math.Max(0, layout.RowBottom - layout.RowTop)), CombineMode.Intersect);
        var child = _motionParentDragTrackId is null ? -1 : TrackIndexForId(_motionParentDragTrackId);
        var active = _motionParentHoverTrackId is null ? GetActiveTrackIndex() : TrackIndexForId(_motionParentHoverTrackId);
        var related = new HashSet<int>();
        var ancestor = active;
        while (ancestor >= 0 && related.Add(ancestor)) ancestor = MotionParentTrackIndex(MotionParentTarget(ancestor));
        foreach (var index in VisibleTrackIndices())
        {
            var node = MotionParentNode(index, layout);
            if (node.Y < layout.RowTop || node.Y >= layout.RowBottom) continue;
            var rowTop = node.Y - _rowHeight / 2f;
            if (index == GetActiveTrackIndex() || index == active)
            {
                using var selected = new SolidBrush(Color.FromArgb(40, Theme.Accent));
                graphics.FillRectangle(selected, bounds.Left + 1, rowTop, Math.Max(0, bounds.Width - 2), _rowHeight);
            }
            graphics.DrawLine(border, bounds.Left, rowTop + _rowHeight - 1, bounds.Right, rowTop + _rowHeight - 1);
        }
        foreach (var index in VisibleTrackIndices())
        {
            var node = MotionParentNode(index, layout);
            var targetId = MotionParentTarget(index);
            var parent = MotionParentTrackIndex(targetId);
            var highlight = related.Contains(index);
            var legal = child >= 0 && MotionParentTargetAllowed(child, index);
            if (legal && node.Y >= layout.RowTop && node.Y < layout.RowBottom)
            {
                using var targetBrush = new SolidBrush(Color.FromArgb(35, Theme.Accent));
                graphics.FillRectangle(targetBrush, bounds.Left, node.Y - _rowHeight / 2f, bounds.Width, _rowHeight);
            }
            var color = Theme.ReadableUiColor(Theme.PanelStrong, Enabled && (highlight || legal) ? Theme.Accent : Theme.Muted);
            using var line = new Pen(color, highlight ? 2f : 1f) { LineJoin = LineJoin.Round };
            if (!string.IsNullOrEmpty(targetId))
            {
                if (parent >= 0 && VisibleTrackPosition(parent) >= 0)
                {
                    var parentNode = MotionParentNode(parent, layout);
                    var bend = bounds.Right - ScaleTimelineMetric(12 + index % 4 * 10);
                    graphics.DrawLines(line, new[] { new PointF(node.X + ScaleTimelineMetric(4), node.Y),
                        new PointF(bend, node.Y), new PointF(bend, parentNode.Y),
                        new PointF(parentNode.X + ScaleTimelineMetric(6), parentNode.Y) });
                    var arrow = ScaleTimelineMetric(3);
                    graphics.DrawLines(line, new[] { new PointF(parentNode.X + arrow * 3, parentNode.Y - arrow),
                        new PointF(parentNode.X + arrow * 2, parentNode.Y),
                        new PointF(parentNode.X + arrow * 3, parentNode.Y + arrow) });
                }
                else if (node.Y >= layout.RowTop && node.Y < layout.RowBottom)
                {
                    line.DashStyle = DashStyle.Dash;
                    graphics.DrawLine(line, node.X + 5, node.Y, node.X + ScaleTimelineMetric(16), node.Y);
                    TextRenderer.DrawText(graphics, ParentingText("Hidden parent", "隐藏父级"), headerFont,
                        new Rectangle((int)node.X + ScaleTimelineMetric(20), (int)(node.Y - _rowHeight / 2f),
                            Math.Max(0, bounds.Right - (int)node.X - ScaleTimelineMetric(24)), _rowHeight), color,
                        TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.PreserveGraphicsClipping);
                }
            }
        }
        // 最后绘制节点，避免其他图层的连线覆盖拖拽目标。
        foreach (var index in VisibleTrackIndices())
        {
            var node = MotionParentNode(index, layout);
            if (node.Y < layout.RowTop || node.Y >= layout.RowBottom) continue;
            var emphasized = related.Contains(index) || child >= 0 && MotionParentTargetAllowed(child, index);
            var color = Theme.ReadableUiColor(Theme.PanelStrong, Enabled && emphasized ? Theme.Accent : Theme.Muted);
            var radius = ScaleTimelineMetric(3);
            using var fill = new SolidBrush(Theme.PanelStrong);
            using var outline = new Pen(color, emphasized ? 2f : 1f);
            graphics.FillEllipse(fill, node.X - radius, node.Y - radius, radius * 2, radius * 2);
            graphics.DrawEllipse(outline, node.X - radius, node.Y - radius, radius * 2, radius * 2);
        }
        if (child >= 0 && VisibleTrackPosition(child) >= 0)
        {
            using var preview = new Pen(Theme.Accent, 2) { DashStyle = DashStyle.Dash };
            graphics.DrawLine(preview, MotionParentNode(child, layout), _motionParentPointer);
        }
        graphics.Restore(state);
    }

    private bool HandleMotionParentingMouseDown(MouseEventArgs args)
    {
        var layout = CreateLayout();
        if (MotionParentToggleBounds(layout).Contains(args.Location))
        {
            if (args.Button == MouseButtons.Left && Enabled)
            {
                Focus();
                MotionParentingVisible = !MotionParentingVisible;
            }
            return true;
        }
        if (MotionParentingColumnWidth == 0 || !MotionParentColumnBounds(layout).Contains(args.Location)) return false;
        if (!Enabled || !TryGetTrackIndex(args.Location, layout, out var track)) return true;
        Focus();
        if (args.Button == MouseButtons.Right)
        {
            ShowMotionParentMenu(track, args.Location);
            return true;
        }
        if (args.Button != MouseButtons.Left) return true;
        SelectLayerTrack(track, Keys.None);
        var node = MotionParentNode(track, layout);
        if (Math.Abs(args.X - node.X) <= ScaleTimelineMetric(10) && Math.Abs(args.Y - node.Y) <= ScaleTimelineMetric(10))
        {
            _motionParentDragTrackId = _timeline.Tracks[track].Id;
            _motionParentPointer = args.Location;
            Capture = true;
        }
        Invalidate();
        return true;
    }

    private bool HandleMotionParentingMouseMove(Point point)
    {
        var layout = CreateLayout();
        if (_motionParentDragTrackId is not null)
        {
            if (!Enabled || MotionParentingColumnWidth == 0 || TrackIndexForId(_motionParentDragTrackId) < 0)
            {
                CancelMotionParentingDrag();
                return true;
            }
            _motionParentPointer = point;
            var target = MotionParentColumnBounds(layout).Contains(point) && TryGetTrackIndex(point, layout, out var index) ? index : -1;
            Cursor = MotionParentTargetAllowed(TrackIndexForId(_motionParentDragTrackId), target) ? Cursors.Hand : Cursors.No;
            SetLayerControlToolTip(ParentingTitle + "\n" + ParentingText("Drag child to parent; Escape cancels", "从子层拖向父层；Escape 取消"));
            Invalidate();
            return true;
        }
        if (MotionParentToggleBounds(layout).Contains(point) || MotionParentingColumnWidth > 0 && MotionParentColumnBounds(layout).Contains(point))
        {
            Cursor = Enabled ? Cursors.Hand : Cursors.Default;
            var hint = ParentingTitle;
            if (TryGetTrackIndex(point, layout, out var track))
            {
                _motionParentHoverTrackId = _timeline.Tracks[track].Id;
                var target = MotionParentTarget(track);
                var parent = MotionParentTrackIndex(target);
                hint += "\n" + GetTrackName(track) + " → " + (parent >= 0 ? GetTrackName(parent)
                    : string.IsNullOrEmpty(target) ? ParentingText("No parent", "无父级") : target);
                if (parent >= 0 && VisibleTrackPosition(parent) < 0)
                    hint += "\n" + ParentingText("Parent is collapsed or filtered out", "父级位于折叠组或筛选范围外");
                else if (parent < 0 && !string.IsNullOrEmpty(target))
                    hint += "\n" + ParentingText("Parent is outside this timeline", "父级不在当前时间轴中");
            }
            else _motionParentHoverTrackId = null;
            SetLayerControlToolTip(hint);
            Invalidate();
            return true;
        }
        if (_motionParentHoverTrackId is not null)
        {
            _motionParentHoverTrackId = null;
            Invalidate();
        }
        return false;
    }

    private bool CompleteMotionParentingDrag(MouseEventArgs args)
    {
        if (_motionParentDragTrackId is null || args.Button != MouseButtons.Left) return false;
        var trackId = _motionParentDragTrackId;
        var child = TrackIndexForId(trackId);
        var layout = CreateLayout();
        var target = MotionParentColumnBounds(layout).Contains(args.Location) && TryGetTrackIndex(args.Location, layout, out var index) ? index : -1;
        var parentTrackId = MotionParentTargetAllowed(child, target) ? _timeline.Tracks[target].Id : null;
        CancelMotionParentingDrag();
        if (parentTrackId is not null) MotionParentRequested?.Invoke(this, new TimelineParentRequestedEventArgs(trackId, parentTrackId));
        return true;
    }

    private void CancelMotionParentingDrag()
    {
        if (_motionParentDragTrackId is null) return;
        _motionParentDragTrackId = null;
        if (Capture) Capture = false;
        Cursor = Cursors.Default;
        Invalidate();
    }

    private bool HandleMotionParentingKeyDown(KeyEventArgs args)
    {
        if (args.KeyCode == Keys.Escape && _motionParentDragTrackId is not null) CancelMotionParentingDrag();
        else if (args.Control && args.Shift && args.KeyCode == Keys.P && MotionParentingAvailable) MotionParentingVisible = !MotionParentingVisible;
        else return false;
        args.Handled = true;
        args.SuppressKeyPress = true;
        return true;
    }

    private void ShowMotionParentMenu(int track, Point point)
    {
        CreateMotionParentMenu(track).Show(this, point);
    }

    private AnimatedContextMenuStrip CreateMotionParentMenu(int track)
    {
        var timeline = _timeline;
        var trackId = timeline.Tracks[track].Id;
        var parentTarget = MotionParentTarget(track);
        var parent = MotionParentTrackIndex(parentTarget);
        var parentTrackId = parent >= 0 ? timeline.Tracks[parent].Id : "";
        var menu = new AnimatedContextMenuStrip { AccessibleName = ParentingTitle };
        var detach = new ToolStripMenuItem(ParentingText("Remove layer parent", "解除父子关系")) { Enabled = !string.IsNullOrEmpty(parentTarget) };
        detach.Click += (_, _) =>
        {
            if (Enabled && MotionParentingAvailable && ReferenceEquals(timeline, _timeline) && TrackIndexForId(trackId) >= 0)
                MotionParentRequested?.Invoke(this, new TimelineParentRequestedEventArgs(trackId, ""));
        };
        var locate = new ToolStripMenuItem(ParentingText("Locate parent layer", "定位父层")) { Enabled = parent >= 0 };
        locate.Click += (_, _) =>
        {
            if (!ReferenceEquals(timeline, _timeline)) return;
            var parentIndex = TrackIndexForId(parentTrackId);
            if (parentIndex < 0) return;
            SetActiveTabGroup(AnimationTimeline.AllTabGroupId);
            ExpandAncestorsForTrack(parentIndex);
            SelectLayerTrack(parentIndex, Keys.None);
            EnsureActiveTrackVisible();
            Invalidate();
        };
        menu.Items.Add(detach);
        menu.Items.Add(locate);
        menu.Closed += (_, _) => menu.BeginInvoke((Action)menu.Dispose);
        return menu;
    }

    private int MotionParentTrackIndex(string targetId)
    {
        if (string.IsNullOrEmpty(targetId)) return -1;
        for (var index = 0; index < TrackCount; index++) if (_timeline.Tracks[index].TargetId == targetId) return index;
        return -1;
    }
    private string MotionParentTarget(int index) => index >= 0 && index < TrackCount ? MotionParentTargetResolver?.Invoke(_timeline.Tracks[index].TargetId) ?? "" : "";
    private PointF MotionParentNode(int index, TimelineLayout layout) => new(MotionParentColumnBounds(layout).Left + ScaleTimelineMetric(15),
        layout.RowTop + (VisibleTrackPosition(index) - _firstVisibleTrack) * _rowHeight + _rowHeight / 2f);
    private bool MotionParentTargetAllowed(int child, int parent) => Enabled && MotionParentingAvailable
        && child >= 0 && child < TrackCount && parent >= 0 && parent < TrackCount && child != parent
        && CanMotionParent?.Invoke(_timeline.Tracks[child].TargetId, _timeline.Tracks[parent].TargetId) == true;
}
