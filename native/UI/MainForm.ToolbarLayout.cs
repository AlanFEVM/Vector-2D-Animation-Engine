namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private void LayoutHeaderToolPalette(Control stagePanel)
    {
        if (_workspaceHeader.Parent is not Control body) return;
        var tools = _toolPalette;
        var scale = tools.DeviceDpi / 96f;
        var gap = Math.Max(2, (int)Math.Round(4 * scale));
        _stageMetrics.Height = (int)Math.Round(34 * scale);
        var drawingRowVisible = _workspaceTabs.SelectedView == WorkspaceView.BasicDrawing
            || IsSymbolEditing || ActiveBlockChunkBreadcrumb is not null;
        var tabRight = drawingRowVisible
            ? _drawingObjectTabs.Controls.Cast<Control>().Where(control => control.Visible)
                .Select(control => body.PointToClient(control.PointToScreen(new Point(control.Width, 0))).X + control.Margin.Right)
                .DefaultIfEmpty(0).Max()
            : 0;
        var contextGap = Math.Max(gap, (int)Math.Round(12 * scale));
        var left = drawingRowVisible
            ? tabRight + contextGap
            : Math.Min((int)Math.Round(280 * scale), stagePanel.Width / 5);
        // Hidden row children retain stale bounds and must not constrain the scene toolbar.
        var firstSnappingControlLeft = _drawSnappingStrip.Controls.Cast<Control>()
            .SelectMany(host => host.Controls.Count > 0 ? host.Controls.Cast<Control>() : new[] { host })
            .Where(control => control.Visible && control.Width > 0)
            .Select(control => body.PointToClient(control.PointToScreen(Point.Empty)).X)
            .DefaultIfEmpty(body.PointToClient(_drawSnappingStrip.PointToScreen(Point.Empty)).X)
            .Min();
        var right = drawingRowVisible
            ? firstSnappingControlLeft - gap
            : stagePanel.Right - gap;
        var visibleTools = tools.Controls.Cast<Control>().Where(control => control.Visible).ToArray();
        var visibleCount = Math.Max(1, visibleTools.Length);
        var compact = right - left < (int)Math.Round(240 * scale);
        var top = body.PointToClient(_workspaceTabs.PointToScreen(new Point(0, _workspaceTabs.Height))).Y + gap;
        if (compact)
        {
            left = stagePanel.Left + gap;
            right = _stageMetrics.Controls.Cast<Control>().Where(control => control.Visible)
                .Select(control => body.PointToClient(control.PointToScreen(Point.Empty)).X - gap)
                .DefaultIfEmpty(stagePanel.Right - gap).Min();
            top = body.PointToClient(_stageMetrics.PointToScreen(Point.Empty)).Y + gap;
        }
        var width = Math.Max(1, right - left);
        var toolbarButtonSize = Math.Max(20, (int)Math.Round(28 * scale));
        var toolbarVerticalInset = Math.Max(1, (int)Math.Round(2 * scale));
        var toolbarHeight = toolbarButtonSize + toolbarVerticalInset * 2;
        tools.SuspendLayout();
        try
        {
            if (!ReferenceEquals(tools.Parent, body)) body.Controls.Add(tools);
            tools.AutoSize = false;
            tools.FlowDirection = FlowDirection.LeftToRight;
            tools.WrapContents = false;
            tools.Padding = new Padding(compact ? 1 : gap, toolbarVerticalInset, compact ? 1 : gap, toolbarVerticalInset);
            tools.AutoScroll = false;
            tools.AutoScrollPosition = Point.Empty;
            var buttonHeight = toolbarButtonSize;
            var categoryBoundaryCount = visibleTools.Count(IsTopToolbarCategoryBoundary);
            var availableMarginWidth = Math.Max(0, width - tools.Padding.Horizontal - visibleCount * buttonHeight);
            var requestedCategoryGap = Math.Max(gap, (int)Math.Round(8 * scale));
            var categoryGap = compact || categoryBoundaryCount == 0
                ? 0
                : Math.Min(requestedCategoryGap,
                    Math.Max(0, (availableMarginWidth - visibleCount * gap) / categoryBoundaryCount));
            var preferredButtonGap = Math.Max(gap, (int)Math.Round(12 * scale));
            var maximumButtonGap = Math.Max(1,
                (availableMarginWidth - categoryBoundaryCount * categoryGap) / visibleCount);
            var buttonGap = compact ? 1 : Math.Min(preferredButtonGap, maximumButtonGap);
            var availableButtonWidth = Math.Max(1, (width - tools.Padding.Horizontal) / visibleCount - buttonGap);
            var buttonWidth = Math.Min(availableButtonWidth, buttonHeight);
            foreach (Control control in tools.Controls)
            {
                var trailingGap = buttonGap + (IsTopToolbarCategoryBoundary(control) ? categoryGap : 0);
                control.Margin = new Padding(0, 0, trailingGap, 0);
                control.Size = new Size(buttonWidth, buttonHeight);
                if (control is SvgIconButton button)
                {
                    button.ShowsLabelBelowIcon = false;
                    button.Text = string.Empty;
                    button.Invalidate();
                }
            }
            tools.SetBounds(left, top, width, toolbarHeight);
        }
        finally
        {
            tools.ResumeLayout(true);
        }
        tools.BringToFront();
    }

    private bool IsTopToolbarCategoryBoundary(Control control)
    {
        return ReferenceEquals(control, _lassoToolGroup.ParentButton)
            || ReferenceEquals(control, _transformToolGroup.ParentButton)
            || control.Tag is ToolMode.Text or ToolMode.Eraser;
    }

    private void BindToolGroupFlyoutTrigger(SvgIconButton button, Action showFlyout)
    {
        button.MouseEnter += (_, _) =>
        {
            if (_applicationSettings.ToolbarPosition == ToolbarPosition.Top)
            {
                HideToolFlyouts();
                _toolTip.ShowFor(button, button.AccessibleName ?? "Tools");
                return;
            }
            _toolTip.HideTip();
            showFlyout();
        };
        button.MouseLeave += (_, _) => _toolTip.HideTip();
        button.MouseUp += (_, mouse) =>
        {
            if (mouse.Button != MouseButtons.Right
                || _applicationSettings.ToolbarPosition != ToolbarPosition.Top) return;
            _toolTip.HideTip();
            showFlyout();
        };
    }

    private string ToolbarButtonLabel(SvgIconButton button)
    {
        if (ReferenceEquals(button, _selectionToolGroup.ParentButton)) return "Select";
        if (ReferenceEquals(button, _lassoToolGroup?.ParentButton)) return "Lasso";
        if (ReferenceEquals(button, _transformToolGroup?.ParentButton)) return "Transform";
        if (ReferenceEquals(button, _shapeToolButton)) return "Shapes";
        if (ReferenceEquals(button, _lineToolButton)) return "Lines";
        if (ReferenceEquals(button, _brushToolButton)) return "Brush";
        if (ReferenceEquals(button, _paintToolGroup.ParentButton)) return "Fill";
        if (ReferenceEquals(button, _vaultButton)) return "Vault";
        return button.Tag is ToolMode tool ? tool switch
        {
            ToolMode.Text => "Text",
            ToolMode.Eyedropper => "Eyedropper",
            ToolMode.Gradient => "Gradient",
            ToolMode.SnapPoint => "Snap points",
            ToolMode.Eraser => "Eraser",
            _ => "Tools"
        } : button.AccessibleName ?? "Tools";
    }

    private void LayoutToolFlyout(FlowLayoutPanel flyout, Control button)
    {
        if (flyout.Parent is not Control host) return;
        var top = _applicationSettings.ToolbarPosition == ToolbarPosition.Top;
        if (!top)
        {
            flyout.SuspendLayout();
            flyout.AutoSize = true;
            flyout.FlowDirection = FlowDirection.TopDown;
            flyout.WrapContents = false;
            foreach (Control control in flyout.Controls)
            {
                control.Width = Theme.ToolFlyoutButtonWidth;
                control.Margin = new Padding(0, 0, 0, control == flyout.Controls[^1] ? 0 : Theme.GapXs);
            }
            flyout.ResumeLayout(true);
            flyout.Location = host.PointToClient(button.PointToScreen(new Point(button.Width - 1, 0)));
            return;
        }
        var items = flyout.Controls.Cast<Control>()
            .Where(control => control.Tag is ToolMode tool && IsToolVisibleForCurrentContext(tool)).ToArray();
        flyout.SuspendLayout();
        flyout.AutoSize = false;
        flyout.FlowDirection = FlowDirection.LeftToRight;
        flyout.WrapContents = true;
        var gap = Theme.GapXs;
        var cellWidth = (int)Math.Round(168 * flyout.DeviceDpi / 96f);
        var columns = Math.Clamp((host.ClientSize.Width - flyout.Padding.Horizontal) / (cellWidth + gap), 1, Math.Min(3, Math.Max(1, items.Length)));
        var cellHeight = items.Length > 0 ? items.Max(control => control.Height) : Theme.IconButtonSize;
        foreach (Control control in flyout.Controls)
        {
            control.Width = cellWidth;
            control.Margin = new Padding(0, 0, gap, gap);
        }
        var rows = (int)Math.Ceiling(items.Length / (double)columns);
        flyout.Size = new Size(
            flyout.Padding.Horizontal + columns * (cellWidth + gap),
            flyout.Padding.Vertical + rows * (cellHeight + gap));
        flyout.ResumeLayout(true);
        var anchor = host.PointToClient(button.PointToScreen(new Point(0, button.Height)));
        anchor.Y = Math.Max(anchor.Y, host.PointToClient(_toolPalette.PointToScreen(new Point(0, _toolPalette.Height))).Y + 1);
        flyout.Location = new Point(
            Math.Clamp(anchor.X, 0, Math.Max(0, host.ClientSize.Width - flyout.Width)),
            Math.Clamp(anchor.Y, 0, Math.Max(0, host.ClientSize.Height - flyout.Height)));
    }
}
