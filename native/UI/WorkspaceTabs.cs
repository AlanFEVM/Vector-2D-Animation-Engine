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
    private const int TopNavigationHeight = 38;
    private const int PreferredWorkspaceButtonWidth = 116;
    private const int ExpandedGridControlsWidth = 260;
    private const int CompactGridControlsWidth = 40;
    private const int ExpandedGridBreakpoint = 660;

    private readonly FlowLayoutPanel _tabStrip = new();
    private readonly Dictionary<WorkspaceView, Button> _buttons = new();
    private readonly Dictionary<WorkspaceView, string> _buttonLabels = new();
    private readonly Dictionary<WorkspaceView, string> _buttonDescriptions = new();
    private readonly ToolTip _toolTip = new();
    private readonly System.Windows.Forms.Timer _indicatorTimer = new() { Interval = 16 };
    private readonly TableLayoutPanel _gridControls = new();
    private readonly Label _gridLabel = new();
    private readonly SvgIconButton _gridTypeButton = new(SvgIconKind.Grid)
    {
        AccessibleName = "Grid type",
        ShowsToolGroupIndicator = true
    };
    private readonly AnimatedContextMenuStrip _gridTypeMenu = new();
    private readonly ToolStripMenuItem _cartesianGridItem = new("Cartesian Grid");
    private readonly ToolStripMenuItem _goldenSpiralGridItem = new("Golden Spiral");
    private readonly ToolStripMenuItem _polarGridItem = new("Polar Grid");
    private readonly ModernSlider _gridOpacity = new()
    {
        Minimum = 0,
        Maximum = 100,
        SmallChange = 1,
        LargeChange = 10,
        TickFrequency = 10,
        Value = 10,
        AccessibleName = "World grid opacity"
    };
    private readonly Label _gridOpacityValue = new();
    private WorkspaceView _selectedView = WorkspaceView.BasicDrawing;
    private WorkspaceTabPlacement _placement = WorkspaceTabPlacement.Top;
    private WorldGridType _worldGridType;
    private float _indicatorPosition;
    private float _indicatorExtent;
    private float _indicatorTargetPosition;
    private float _indicatorTargetExtent;
    private bool _indicatorInitialized;
    private bool _compactGridControls;
    private UiLanguage? _accessibilityLanguage;

    public WorkspaceTabs()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Top;
        Height = TopNavigationHeight;
        MinimumSize = new Size(48, TopNavigationHeight);
        TabStop = false;
        Theme.StyleToolTip(_toolTip);

        _tabStrip.Dock = DockStyle.Fill;
        _tabStrip.BackColor = Theme.Top;
        _tabStrip.Padding = new Padding(8, 5, 8, 5);
        _tabStrip.Margin = Padding.Empty;
        _tabStrip.WrapContents = false;
        _tabStrip.AutoScroll = false;
        _tabStrip.TabIndex = 0;
        _tabStrip.TabStop = false;
        _tabStrip.AccessibleName = "Workspace views";
        _tabStrip.AccessibleRole = AccessibleRole.PageTabList;
        Controls.Add(_tabStrip);

        _gridControls.Dock = DockStyle.Right;
        _gridControls.Width = ExpandedGridControlsWidth;
        _gridControls.BackColor = Theme.Top;
        _gridControls.ColumnCount = 4;
        _gridControls.RowCount = 1;
        _gridControls.Padding = new Padding(0, 5, 8, 5);
        _gridControls.Margin = Padding.Empty;
        _gridControls.TabIndex = 1;
        _gridControls.TabStop = false;
        _gridControls.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
        _gridControls.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 30));
        _gridControls.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _gridControls.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38));
        _gridControls.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _gridLabel.Text = "Grid";
        _gridLabel.Dock = DockStyle.Fill;
        _gridLabel.ForeColor = Theme.Muted;
        _gridLabel.BackColor = Theme.Top;
        _gridLabel.Font = Theme.UiFont(8.5f, FontStyle.Bold);
        _gridLabel.TextAlign = ContentAlignment.MiddleLeft;
        _gridControls.Controls.Add(_gridLabel, 0, 0);

        Theme.StyleToolbarButton(_gridTypeButton);
        _gridTypeButton.Dock = DockStyle.Fill;
        _gridTypeButton.Margin = new Padding(1, 0, 1, 0);
        _gridTypeButton.TabIndex = 0;
        _gridTypeButton.Click += (_, _) =>
        {
            RefreshGridTypePresentation();
            _gridTypeMenu.Show(_gridTypeButton, new Point(0, _gridTypeButton.Height + 2));
        };
        _toolTip.SetToolTip(_gridTypeButton, "Grid type");
        _cartesianGridItem.Click += (_, _) => WorldGridType = VectorAnimationEngine.WorldGridType.Cartesian;
        _goldenSpiralGridItem.Click += (_, _) => WorldGridType = VectorAnimationEngine.WorldGridType.GoldenSpiral;
        _polarGridItem.Click += (_, _) => WorldGridType = VectorAnimationEngine.WorldGridType.Polar;
        _gridTypeMenu.Items.AddRange(new ToolStripItem[] { _cartesianGridItem, _polarGridItem, _goldenSpiralGridItem });
        _gridTypeMenu.Opening += (_, _) => RefreshGridTypePresentation();
        UiLocalization.Watch(_gridTypeMenu);
        _gridControls.Controls.Add(_gridTypeButton, 1, 0);

        _gridOpacity.Dock = DockStyle.Fill;
        _gridOpacity.Margin = new Padding(2, 0, 4, 0);
        _gridOpacity.TabIndex = 1;
        _gridOpacity.ValueChanged += (_, _) =>
        {
            _gridOpacityValue.Text = $"{_gridOpacity.Value}%";
            WorldGridOpacityChanged?.Invoke(this, EventArgs.Empty);
        };
        _toolTip.SetToolTip(_gridOpacity, "World grid opacity");
        _gridControls.Controls.Add(_gridOpacity, 2, 0);

        _gridOpacityValue.Text = "10%";
        _gridOpacityValue.Dock = DockStyle.Fill;
        _gridOpacityValue.ForeColor = Theme.Muted;
        _gridOpacityValue.BackColor = Theme.Top;
        _gridOpacityValue.Font = Theme.UiFont(8.5f);
        _gridOpacityValue.TextAlign = ContentAlignment.MiddleRight;
        _gridControls.Controls.Add(_gridOpacityValue, 3, 0);
        Controls.Add(_gridControls);

        AddWorkspaceButton(WorkspaceView.BasicDrawing, "Basic Drawing", "Shape drawing and direct object editing");
        AddWorkspaceButton(WorkspaceView.SceneEditor, "Scene Building", "Scene assembly, animation and keyframe workflow");

        _indicatorTimer.Tick += (_, _) => TickIndicator();
        RefreshGridTypePresentation();
        ApplyPlacement();
        RefreshButtons();
    }

    public event EventHandler<WorkspaceViewChangedEventArgs>? SelectedViewChanged;
    public event EventHandler? WorldGridOpacityChanged;
    public event EventHandler? WorldGridTypeChanged;

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

    [DefaultValue(10)]
    public int WorldGridOpacity
    {
        get => _gridOpacity.Value;
        set => _gridOpacity.Value = Math.Clamp(value, _gridOpacity.Minimum, _gridOpacity.Maximum);
    }

    [DefaultValue(VectorAnimationEngine.WorldGridType.Cartesian)]
    public WorldGridType WorldGridType
    {
        get => _worldGridType;
        set
        {
            var next = Enum.IsDefined(value) ? value : VectorAnimationEngine.WorldGridType.Cartesian;
            if (_worldGridType == next)
            {
                RefreshGridTypePresentation();
                return;
            }

            _worldGridType = next;
            RefreshGridTypePresentation();
            WorldGridTypeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SelectView(WorkspaceView view) => SelectView(view, raiseEvent: true);

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateResponsiveLayout();
        LayoutButtons();
        UpdateIndicator(animate: false);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        EnsureLocalizedAccessibility();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if ((keyData & Keys.Modifiers) == Keys.None
            && TryGetFocusedWorkspaceView(out var focusedView)
            && TrySelectWorkspaceFromKey(focusedView, keyData & Keys.KeyCode))
        {
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _indicatorTimer.Dispose();
            _gridTypeMenu.Dispose();
            _toolTip.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        EnsureLocalizedAccessibility();
        using var pen = new Pen(Theme.ReadableUiColor(BackColor, Theme.Border));
        if (_placement == WorkspaceTabPlacement.Top)
        {
            e.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
            if (_indicatorInitialized)
            {
                using var accent = new SolidBrush(
                    SystemInformation.HighContrast
                        ? SystemColors.Highlight
                        : Theme.ReadableUiColor(BackColor, Theme.Accent));
                var x = (int)Math.Round(_indicatorPosition);
                var width = Math.Max(0, (int)Math.Round(_indicatorExtent));
                e.Graphics.FillRectangle(accent, x, Height - 4, width, 3);
            }
        }
        else
        {
            e.Graphics.DrawLine(pen, Width - 1, 0, Width - 1, Height);
            if (_indicatorInitialized)
            {
                using var accent = new SolidBrush(
                    SystemInformation.HighContrast
                        ? SystemColors.Highlight
                        : Theme.ReadableUiColor(BackColor, Theme.Accent));
                var y = (int)Math.Round(_indicatorPosition);
                var height = Math.Max(0, (int)Math.Round(_indicatorExtent));
                e.Graphics.FillRectangle(accent, Width - 4, y, 3, height);
            }
        }
    }

    private void AddWorkspaceButton(WorkspaceView view, string text, string tip)
    {
        var button = new SegmentedButton
        {
            Text = text,
            Height = Theme.ControlHeightCompact,
            Width = PreferredWorkspaceButtonWidth,
            Margin = new Padding(0, 0, Theme.GapXs, 0),
            Tag = view,
            TextAlign = ContentAlignment.MiddleCenter,
            AutoEllipsis = true,
            AccessibleName = text,
            AccessibleDescription = tip,
            AccessibleRole = AccessibleRole.PageTab,
            TabIndex = _buttons.Count
        };
        Theme.StyleSegmentedButton(button);
        button.Click += (_, _) => SelectView(view, raiseEvent: true);
        button.KeyDown += HandleWorkspaceButtonKeyDown;
        _toolTip.SetToolTip(button, tip);
        _buttons[view] = button;
        _buttonLabels[view] = text;
        _buttonDescriptions[view] = tip;
        _tabStrip.Controls.Add(button);
    }

    private void SelectView(WorkspaceView view, bool raiseEvent)
    {
        view = NormalizeWorkspaceView(view);
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

    private static WorkspaceView NormalizeWorkspaceView(WorkspaceView view)
    {
        return view == WorkspaceView.Animation ? WorkspaceView.SceneEditor : view;
    }

    private void ApplyPlacement()
    {
        _tabStrip.FlowDirection = _placement == WorkspaceTabPlacement.Top
            ? FlowDirection.LeftToRight
            : FlowDirection.TopDown;
        _tabStrip.Padding = _placement == WorkspaceTabPlacement.Top
            ? new Padding(8, 5, 8, 5)
            : new Padding(6, 8, 6, 8);
        _gridControls.Dock = _placement == WorkspaceTabPlacement.Top
            ? DockStyle.Right
            : DockStyle.Bottom;
        _gridControls.Height = TopNavigationHeight;
        UpdateResponsiveLayout(force: true);
        LayoutButtons();
    }

    private void LayoutButtons()
    {
        if (_buttons.Count == 0) return;

        if (_placement == WorkspaceTabPlacement.Top)
        {
            var buttons = _buttons.Values.ToArray();
            var totalGap = Theme.GapXs * Math.Max(0, buttons.Length - 1);
            var availableWidth = Math.Max(1, _tabStrip.ClientSize.Width - _tabStrip.Padding.Horizontal - totalGap);
            var buttonWidth = Math.Max(1, Math.Min(PreferredWorkspaceButtonWidth, availableWidth / buttons.Length));
            for (var index = 0; index < buttons.Length; index++)
            {
                var button = buttons[index];
                button.Width = buttonWidth;
                button.Height = Theme.ControlHeightCompact;
                button.Margin = new Padding(0, 0, index == buttons.Length - 1 ? 0 : Theme.GapXs, 0);
            }

            _tabStrip.PerformLayout();
            return;
        }

        var verticalButtons = _buttons.Values.ToArray();
        for (var index = 0; index < verticalButtons.Length; index++)
        {
            var button = verticalButtons[index];
            button.Width = Math.Max(36, ClientSize.Width - _tabStrip.Padding.Horizontal);
            button.Height = Theme.ControlHeight;
            button.Margin = new Padding(0, 0, 0, index == verticalButtons.Length - 1 ? 0 : Theme.GapXs);
        }

        _tabStrip.PerformLayout();
    }

    private void RefreshButtons()
    {
        foreach (var (view, button) in _buttons)
        {
            var selected = view == _selectedView;
            Theme.StyleSegmentedButton(button, selected);
            button.TabStop = selected;
        }

        RefreshAccessibility();
        UpdateIndicator(animate: _indicatorInitialized && UiMotion.AnimationsEnabled);
    }

    private void RefreshGridTypePresentation()
    {
        _cartesianGridItem.Checked = _worldGridType == VectorAnimationEngine.WorldGridType.Cartesian;
        _goldenSpiralGridItem.Checked = _worldGridType == VectorAnimationEngine.WorldGridType.GoldenSpiral;
        _polarGridItem.Checked = _worldGridType == VectorAnimationEngine.WorldGridType.Polar;
        _gridTypeButton.Icon = _worldGridType switch
        {
            VectorAnimationEngine.WorldGridType.GoldenSpiral => SvgIconKind.GoldenSpiral,
            VectorAnimationEngine.WorldGridType.Polar => SvgIconKind.PolarGrid,
            _ => SvgIconKind.Grid
        };
        RefreshAccessibility();
        _gridTypeButton.Invalidate();
    }

    private void UpdateIndicator(bool animate)
    {
        if (!_buttons.TryGetValue(_selectedView, out var button)) return;
        var bounds = new Rectangle(
            _tabStrip.Left + button.Left,
            _tabStrip.Top + button.Top,
            button.Width,
            button.Height);
        var position = _placement == WorkspaceTabPlacement.Top ? bounds.Left : bounds.Top;
        var extent = _placement == WorkspaceTabPlacement.Top ? bounds.Width : bounds.Height;
        _indicatorTargetPosition = position;
        _indicatorTargetExtent = extent;
        if (!_indicatorInitialized || !animate || !UiMotion.AnimationsEnabled)
        {
            _indicatorTimer.Stop();
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
        if (!UiMotion.AnimationsEnabled)
        {
            _indicatorPosition = _indicatorTargetPosition;
            _indicatorExtent = _indicatorTargetExtent;
            _indicatorTimer.Stop();
            Invalidate();
            return;
        }

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

    private void UpdateResponsiveLayout(bool force = false)
    {
        if (_gridControls.ColumnStyles.Count < 4) return;

        var compact = _placement == WorkspaceTabPlacement.Left || ClientSize.Width < ExpandedGridBreakpoint;
        if (!force && _compactGridControls == compact) return;

        _compactGridControls = compact;
        _gridControls.SuspendLayout();
        _gridLabel.Visible = !compact;
        _gridOpacity.Visible = !compact;
        _gridOpacityValue.Visible = !compact;
        if (_placement == WorkspaceTabPlacement.Left)
        {
            _gridControls.Padding = new Padding(6, 5, 6, 5);
            SetGridColumn(0, SizeType.Percent, 50);
            SetGridColumn(1, SizeType.Absolute, Theme.ControlHeightCompact);
            SetGridColumn(2, SizeType.Percent, 50);
            SetGridColumn(3, SizeType.Absolute, 0);
            _gridTypeButton.Margin = Padding.Empty;
        }
        else if (compact)
        {
            _gridControls.Width = CompactGridControlsWidth;
            _gridControls.Padding = new Padding(4, 5, 8, 5);
            SetGridColumn(0, SizeType.Absolute, 0);
            SetGridColumn(1, SizeType.Percent, 100);
            SetGridColumn(2, SizeType.Absolute, 0);
            SetGridColumn(3, SizeType.Absolute, 0);
            _gridTypeButton.Margin = Padding.Empty;
        }
        else
        {
            _gridControls.Width = ExpandedGridControlsWidth;
            _gridControls.Padding = new Padding(0, 5, 8, 5);
            SetGridColumn(0, SizeType.Absolute, 44);
            SetGridColumn(1, SizeType.Absolute, 30);
            SetGridColumn(2, SizeType.Percent, 100);
            SetGridColumn(3, SizeType.Absolute, 38);
            _gridTypeButton.Margin = new Padding(1, 0, 1, 0);
        }

        _gridControls.ResumeLayout(performLayout: true);
    }

    private void SetGridColumn(int index, SizeType sizeType, float width)
    {
        _gridControls.ColumnStyles[index].SizeType = sizeType;
        _gridControls.ColumnStyles[index].Width = width;
    }

    private void HandleWorkspaceButtonKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not Button { Tag: WorkspaceView current }) return;
        if (!TrySelectWorkspaceFromKey(current, e.KeyCode)) return;

        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    private bool TrySelectWorkspaceFromKey(WorkspaceView current, Keys keyCode)
    {
        var views = _buttons.Keys.ToArray();
        var currentIndex = Array.IndexOf(views, current);
        if (currentIndex < 0) return false;

        var targetIndex = keyCode switch
        {
            Keys.Home => 0,
            Keys.End => views.Length - 1,
            Keys.Left when _placement == WorkspaceTabPlacement.Top => (currentIndex - 1 + views.Length) % views.Length,
            Keys.Right when _placement == WorkspaceTabPlacement.Top => (currentIndex + 1) % views.Length,
            Keys.Up when _placement == WorkspaceTabPlacement.Left => (currentIndex - 1 + views.Length) % views.Length,
            Keys.Down when _placement == WorkspaceTabPlacement.Left => (currentIndex + 1) % views.Length,
            _ => -1
        };
        if (targetIndex < 0) return false;

        var target = views[targetIndex];
        SelectView(target, raiseEvent: true);
        _buttons[target].Focus();
        return true;
    }

    private bool TryGetFocusedWorkspaceView(out WorkspaceView view)
    {
        foreach (var (candidate, button) in _buttons)
        {
            if (!button.Focused) continue;
            view = candidate;
            return true;
        }

        view = default;
        return false;
    }

    private void EnsureLocalizedAccessibility()
    {
        if (_accessibilityLanguage != UiLocalization.CurrentLanguage) RefreshAccessibility();
    }

    private void RefreshAccessibility()
    {
        _accessibilityLanguage = UiLocalization.CurrentLanguage;
        foreach (var (view, button) in _buttons)
        {
            button.AccessibleName = UiLocalization.T(_buttonLabels[view]);
            var description = UiLocalization.T(_buttonDescriptions[view]);
            button.AccessibleDescription = view == _selectedView
                ? $"{UiLocalization.T("Selected: ")}{description}"
                : description;
        }

        var gridType = _worldGridType switch
        {
            VectorAnimationEngine.WorldGridType.GoldenSpiral => "Golden Spiral",
            VectorAnimationEngine.WorldGridType.Polar => "Polar Grid",
            _ => "Cartesian Grid"
        };
        _gridTypeButton.AccessibleName = UiLocalization.T("Grid type");
        _gridTypeButton.AccessibleDescription = $"{UiLocalization.T("Grid type")}: {UiLocalization.T(gridType)}";
    }
}
