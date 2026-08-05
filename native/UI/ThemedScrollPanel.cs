namespace VectorAnimationEngine;

/// <summary>
/// A compact vertical scroll host that follows the workbench palette without
/// relying on the operating system's non-client scrollbar rendering.
/// </summary>
internal sealed class ThemedScrollPanel : UserControl
{
    private const int ScrollRailWidth = 12;
    private const int ScrollRailInset = 3;
    private const int MinimumThumbHeight = 28;
    private const int ScrollLineHeight = 28;

    private readonly Panel _viewport = new();
    private readonly Panel _content = new();
    private bool _updatingLayout;
    private bool _scrollBarHovered;
    private bool _draggingThumb;
    private int _scrollOffset;
    private int _dragOffset;

    public ThemedScrollPanel()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint,
            true);
        AccessibleRole = AccessibleRole.Pane;
        AccessibleName = "Scrollable inspector";
        AccessibleDescription = "Scrollable inspector content";
        BackColor = Theme.Panel;
        TabStop = false;

        _viewport.BackColor = Theme.Panel;
        _viewport.TabStop = false;
        _viewport.MouseWheel += ContentMouseWheel;
        _viewport.Controls.Add(_content);
        Controls.Add(_viewport);

        _content.BackColor = Theme.Panel;
        _content.TabStop = false;
        _content.ControlAdded += ContentControlAdded;
        _content.ControlRemoved += (_, _) => UpdateScrollMetrics();
        _content.Layout += (_, _) => UpdateScrollMetrics();
        _content.MouseWheel += ContentMouseWheel;
    }

    public Panel Content => _content;

    public Padding ContentPadding
    {
        get => _content.Padding;
        set
        {
            if (_content.Padding == value) return;
            _content.Padding = value;
            UpdateScrollMetrics();
        }
    }

    public int ScrollPosition => _scrollOffset;

    public void SuspendContentLayout() => _content.SuspendLayout();

    public void ResumeContentLayout(bool performLayout)
    {
        _content.ResumeLayout(performLayout: false);
        if (performLayout) UpdateScrollMetrics();
    }

    public void RestoreScrollPosition(int position)
    {
        UpdateScrollMetrics();
        SetScrollPosition(position);
    }

    protected override void OnBackColorChanged(EventArgs e)
    {
        base.OnBackColorChanged(e);
        _viewport.BackColor = BackColor;
        _content.BackColor = BackColor;
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        UpdateScrollMetrics();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (MaximumScroll <= 0) return;

        var track = ScrollTrackBounds();
        var thumb = ScrollThumbBounds(track);
        using var trackBrush = new SolidBrush(Theme.Field);
        using var thumbBrush = new SolidBrush(
            _draggingThumb ? Theme.Accent : _scrollBarHovered ? Theme.BorderHover : Theme.PanelHover);
        e.Graphics.FillRectangle(trackBrush, track);
        e.Graphics.FillRectangle(thumbBrush, thumb);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || MaximumScroll <= 0) return;

        var track = ScrollTrackBounds();
        if (!track.Contains(e.Location)) return;

        var thumb = ScrollThumbBounds(track);
        if (thumb.Contains(e.Location))
        {
            _draggingThumb = true;
            _dragOffset = e.Y - thumb.Top;
            Capture = true;
        }
        else
        {
            var page = Math.Max(ScrollLineHeight, _viewport.ClientSize.Height - ScrollLineHeight);
            SetScrollPosition(_scrollOffset + (e.Y < thumb.Top ? -page : page));
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var track = ScrollTrackBounds();
        var hovered = MaximumScroll > 0 && track.Contains(e.Location);
        if (_scrollBarHovered != hovered)
        {
            _scrollBarHovered = hovered;
            Invalidate(track);
        }

        if (!_draggingThumb) return;
        var thumb = ScrollThumbBounds(track);
        var travel = track.Height - thumb.Height;
        if (travel <= 0) return;
        var thumbTop = Math.Clamp(e.Y - _dragOffset, track.Top, track.Bottom - thumb.Height);
        var ratio = (thumbTop - track.Top) / (double)travel;
        SetScrollPosition((int)Math.Round(ratio * MaximumScroll));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        EndThumbDrag();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture) EndThumbDrag();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_draggingThumb || !_scrollBarHovered) return;
        _scrollBarHovered = false;
        Invalidate(ScrollTrackBounds());
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var page = Math.Max(ScrollLineHeight, _viewport.ClientSize.Height - ScrollLineHeight);
        switch (keyData & Keys.KeyCode)
        {
            case Keys.Up:
                SetScrollPosition(_scrollOffset - ScrollLineHeight);
                return true;
            case Keys.Down:
                SetScrollPosition(_scrollOffset + ScrollLineHeight);
                return true;
            case Keys.PageUp:
                SetScrollPosition(_scrollOffset - page);
                return true;
            case Keys.PageDown:
                SetScrollPosition(_scrollOffset + page);
                return true;
            case Keys.Home:
                SetScrollPosition(0);
                return true;
            case Keys.End:
                SetScrollPosition(MaximumScroll);
                return true;
            default:
                return base.ProcessCmdKey(ref msg, keyData);
        }
    }

    private int MaximumScroll => Math.Max(0, _content.Height - _viewport.ClientSize.Height);

    private void ContentControlAdded(object? sender, ControlEventArgs e)
    {
        if (e.Control is { } control) AttachMouseWheelHandler(control);
        UpdateScrollMetrics();
    }

    private void AttachMouseWheelHandler(Control control)
    {
        control.MouseWheel += ContentMouseWheel;
        control.ControlAdded += (_, e) =>
        {
            if (e.Control is { } child) AttachMouseWheelHandler(child);
        };
        foreach (Control child in control.Controls) AttachMouseWheelHandler(child);
    }

    private void ContentMouseWheel(object? sender, MouseEventArgs e)
    {
        if (e is HandledMouseEventArgs { Handled: true }) return;
        if (sender is TextBoxBase or ComboBox or NumericUpDown or ListBox or ListView or TreeView) return;
        if (MaximumScroll <= 0 || e.Delta == 0) return;

        var lines = SystemInformation.MouseWheelScrollLines;
        var amount = lines == -1
            ? Math.Max(ScrollLineHeight, _viewport.ClientSize.Height - ScrollLineHeight)
            : Math.Max(ScrollLineHeight, lines * ScrollLineHeight);
        SetScrollPosition(_scrollOffset - Math.Sign(e.Delta) * amount);
    }

    private void UpdateScrollMetrics()
    {
        if (_updatingLayout || IsDisposed) return;
        _updatingLayout = true;
        try
        {
            _viewport.Bounds = new Rectangle(0, 0, Math.Max(0, ClientSize.Width - ScrollRailWidth), ClientSize.Height);
            var viewportSize = _viewport.ClientSize;
            if (viewportSize.Width <= 0 || viewportSize.Height <= 0) return;

            _content.Width = viewportSize.Width;
            var requiredHeight = RequiredContentHeight();
            _content.Height = Math.Max(viewportSize.Height, requiredHeight);
            _content.PerformLayout();

            // A layout pass can update an auto-sized child such as the material editor.
            var updatedRequiredHeight = RequiredContentHeight();
            if (updatedRequiredHeight > _content.Height)
            {
                _content.Height = updatedRequiredHeight;
                _content.PerformLayout();
            }

            SetScrollPosition(_scrollOffset, invalidate: false);
            Invalidate(ScrollTrackBounds());
        }
        finally
        {
            _updatingLayout = false;
        }
    }

    private int RequiredContentHeight()
    {
        var height = _content.Padding.Top + _content.Padding.Bottom;
        var absoluteBottom = _content.Padding.Top;
        var fillMinimumHeight = 0;
        foreach (Control child in _content.Controls)
        {
            if (!child.Visible) continue;
            if (child.Dock is DockStyle.Top or DockStyle.Bottom)
            {
                height += child.Height + child.Margin.Top + child.Margin.Bottom;
            }
            else if (child.Dock == DockStyle.None)
            {
                absoluteBottom = Math.Max(absoluteBottom, child.Bottom + child.Margin.Bottom);
            }
            else if (child.Dock == DockStyle.Fill)
            {
                fillMinimumHeight = Math.Max(
                    fillMinimumHeight,
                    child.MinimumSize.Height + child.Margin.Top + child.Margin.Bottom);
            }
        }

        return Math.Max(height + fillMinimumHeight, absoluteBottom + _content.Padding.Bottom);
    }

    private Rectangle ScrollTrackBounds()
    {
        var height = Math.Max(0, ClientSize.Height - ScrollRailInset * 2);
        return new Rectangle(ClientSize.Width - ScrollRailWidth + ScrollRailInset, ScrollRailInset, 6, height);
    }

    private Rectangle ScrollThumbBounds(Rectangle track)
    {
        if (track.Height <= 0) return Rectangle.Empty;
        var contentHeight = Math.Max(1, _content.Height);
        var viewportHeight = Math.Max(1, _viewport.ClientSize.Height);
        var height = Math.Clamp(
            (int)Math.Round(track.Height * (viewportHeight / (double)contentHeight)),
            Math.Min(MinimumThumbHeight, track.Height),
            track.Height);
        var travel = track.Height - height;
        var top = MaximumScroll <= 0 || travel <= 0
            ? track.Top
            : track.Top + (int)Math.Round(travel * (_scrollOffset / (double)MaximumScroll));
        return new Rectangle(track.Left, top, track.Width, height);
    }

    private void SetScrollPosition(int position, bool invalidate = true)
    {
        var next = Math.Clamp(position, 0, MaximumScroll);
        if (_scrollOffset == next)
        {
            _content.Location = new Point(0, -next);
            return;
        }

        _scrollOffset = next;
        _content.Location = new Point(0, -next);
        if (invalidate) Invalidate(ScrollTrackBounds());
    }

    private void EndThumbDrag()
    {
        if (!_draggingThumb) return;
        _draggingThumb = false;
        _dragOffset = 0;
        if (Capture) Capture = false;
        Invalidate(ScrollTrackBounds());
    }
}
