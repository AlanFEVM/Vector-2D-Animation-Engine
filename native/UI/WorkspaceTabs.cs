using System.ComponentModel;

namespace VectorAnimationEngine;

internal enum WorkspaceView
{
    BasicDrawing,
    SceneEditor,
    Animation
}

internal enum WorkspaceTabPlacement
{
    Top,
    Left
}

internal sealed class WorkspaceViewChangedEventArgs : EventArgs
{
    public WorkspaceViewChangedEventArgs(WorkspaceView previousView, WorkspaceView selectedView)
    {
        PreviousView = previousView;
        SelectedView = selectedView;
    }

    public WorkspaceView PreviousView { get; }
    public WorkspaceView SelectedView { get; }
}

internal sealed class WorkspaceTabs : UserControl
{
    private readonly FlowLayoutPanel _tabStrip = new();
    private readonly Dictionary<WorkspaceView, Button> _buttons = new();
    private readonly ToolTip _toolTip = new();
    private readonly System.Windows.Forms.Timer _indicatorTimer = new() { Interval = 16 };
    private WorkspaceView _selectedView = WorkspaceView.BasicDrawing;
    private WorkspaceTabPlacement _placement = WorkspaceTabPlacement.Top;
    private float _indicatorPosition;
    private float _indicatorExtent;
    private float _indicatorTargetPosition;
    private float _indicatorTargetExtent;
    private bool _indicatorInitialized;

    public WorkspaceTabs()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Top;
        MinimumSize = new Size(48, 42);

        _tabStrip.Dock = DockStyle.Fill;
        _tabStrip.BackColor = Theme.Top;
        _tabStrip.Padding = new Padding(8, 6, 8, 6);
        _tabStrip.Margin = Padding.Empty;
        _tabStrip.WrapContents = false;
        _tabStrip.AutoScroll = false;
        Controls.Add(_tabStrip);

        AddWorkspaceButton(WorkspaceView.BasicDrawing, "Basic Drawing", "Shape drawing and direct object editing");
        AddWorkspaceButton(WorkspaceView.SceneEditor, "Scene Edit", "Scene assembly, hierarchy and library workflow");
        AddWorkspaceButton(WorkspaceView.Animation, "Animation", "Timeline, playback and keyframe workflow");

        _indicatorTimer.Tick += (_, _) => TickIndicator();
        ApplyPlacement();
        RefreshButtons();
    }

    public event EventHandler<WorkspaceViewChangedEventArgs>? SelectedViewChanged;

    [DefaultValue(WorkspaceView.BasicDrawing)]
    public WorkspaceView SelectedView
    {
        get => _selectedView;
        set => SelectView(value, raiseEvent: true);
    }

    [DefaultValue(WorkspaceTabPlacement.Top)]
    public WorkspaceTabPlacement Placement
    {
        get => _placement;
        set
        {
            if (_placement == value) return;
            _placement = value;
            ApplyPlacement();
            RefreshButtons();
            Invalidate();
        }
    }

    public IReadOnlyCollection<WorkspaceView> Views => _buttons.Keys;

    public void SelectView(WorkspaceView view) => SelectView(view, raiseEvent: true);

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutButtons();
        UpdateIndicator(animate: false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _indicatorTimer.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.Border);
        if (_placement == WorkspaceTabPlacement.Top)
        {
            e.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
            if (_indicatorInitialized)
            {
                using var glow = new SolidBrush(Color.FromArgb(45, Theme.Accent));
                using var accent = new SolidBrush(Theme.Accent);
                var x = (int)Math.Round(_indicatorPosition);
                var width = Math.Max(0, (int)Math.Round(_indicatorExtent));
                e.Graphics.FillRectangle(glow, x - 3, Height - 6, width + 6, 4);
                e.Graphics.FillRectangle(accent, x, Height - 4, width, 3);
            }
        }
        else
        {
            e.Graphics.DrawLine(pen, Width - 1, 0, Width - 1, Height);
            if (_indicatorInitialized)
            {
                using var glow = new SolidBrush(Color.FromArgb(45, Theme.Accent));
                using var accent = new SolidBrush(Theme.Accent);
                var y = (int)Math.Round(_indicatorPosition);
                var height = Math.Max(0, (int)Math.Round(_indicatorExtent));
                e.Graphics.FillRectangle(glow, Width - 6, y - 3, 4, height + 6);
                e.Graphics.FillRectangle(accent, Width - 4, y, 3, height);
            }
        }
    }

    private void AddWorkspaceButton(WorkspaceView view, string text, string tip)
    {
        var button = new Button
        {
            Text = text,
            Height = 30,
            Width = 128,
            Margin = new Padding(0, 0, 6, 0),
            Tag = view,
            TextAlign = ContentAlignment.MiddleCenter,
            AutoEllipsis = true
        };
        Theme.StyleButton(button);
        button.Click += (_, _) => SelectView(view, raiseEvent: true);
        _toolTip.SetToolTip(button, tip);
        _buttons[view] = button;
        _tabStrip.Controls.Add(button);
    }

    private void SelectView(WorkspaceView view, bool raiseEvent)
    {
        if (!_buttons.ContainsKey(view)) return;
        if (_selectedView == view)
        {
            RefreshButtons();
            return;
        }

        var previous = _selectedView;
        _selectedView = view;
        RefreshButtons();
        UpdateIndicator(animate: true);
        if (raiseEvent) SelectedViewChanged?.Invoke(this, new WorkspaceViewChangedEventArgs(previous, _selectedView));
    }

    private void ApplyPlacement()
    {
        _tabStrip.FlowDirection = _placement == WorkspaceTabPlacement.Top
            ? FlowDirection.LeftToRight
            : FlowDirection.TopDown;
        _tabStrip.Padding = _placement == WorkspaceTabPlacement.Top
            ? new Padding(8, 6, 8, 6)
            : new Padding(6, 8, 6, 8);
        LayoutButtons();
    }

    private void LayoutButtons()
    {
        if (_buttons.Count == 0) return;

        if (_placement == WorkspaceTabPlacement.Top)
        {
            Height = Math.Max(Height, 42);
            foreach (var button in _buttons.Values)
            {
                button.Width = 128;
                button.Height = 30;
                button.Margin = new Padding(0, 0, 6, 0);
            }

            return;
        }

        foreach (var button in _buttons.Values)
        {
            button.Width = Math.Max(36, ClientSize.Width - _tabStrip.Padding.Horizontal);
            button.Height = 34;
            button.Margin = new Padding(0, 0, 0, 6);
        }
    }

    private void RefreshButtons()
    {
        foreach (var (view, button) in _buttons)
        {
            if (view == _selectedView) Theme.StyleActiveButton(button);
            else Theme.StyleButton(button);
        }

        UpdateIndicator(animate: _indicatorInitialized);
    }

    private void UpdateIndicator(bool animate)
    {
        if (!_buttons.TryGetValue(_selectedView, out var button)) return;
        var bounds = button.Bounds;
        var position = _placement == WorkspaceTabPlacement.Top ? bounds.Left : bounds.Top;
        var extent = _placement == WorkspaceTabPlacement.Top ? bounds.Width : bounds.Height;
        _indicatorTargetPosition = position;
        _indicatorTargetExtent = extent;
        if (!_indicatorInitialized || !animate)
        {
            _indicatorInitialized = true;
            _indicatorPosition = _indicatorTargetPosition;
            _indicatorExtent = _indicatorTargetExtent;
            Invalidate();
            return;
        }

        if (!_indicatorTimer.Enabled) _indicatorTimer.Start();
    }

    private void TickIndicator()
    {
        _indicatorPosition += (_indicatorTargetPosition - _indicatorPosition) * 0.28f;
        _indicatorExtent += (_indicatorTargetExtent - _indicatorExtent) * 0.28f;
        if (Math.Abs(_indicatorTargetPosition - _indicatorPosition) < 0.25f
            && Math.Abs(_indicatorTargetExtent - _indicatorExtent) < 0.25f)
        {
            _indicatorPosition = _indicatorTargetPosition;
            _indicatorExtent = _indicatorTargetExtent;
            _indicatorTimer.Stop();
        }

        Invalidate();
    }
}
