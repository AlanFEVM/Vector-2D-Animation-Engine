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
    private WorkspaceView _selectedView = WorkspaceView.BasicDrawing;
    private WorkspaceTabPlacement _placement = WorkspaceTabPlacement.Top;

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
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.Border);
        if (_placement == WorkspaceTabPlacement.Top)
        {
            e.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
        }
        else
        {
            e.Graphics.DrawLine(pen, Width - 1, 0, Width - 1, Height);
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
    }
}
