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
        var tabRight = _drawingObjectTabs.Controls.Cast<Control>().Where(control => control.Visible)
            .Select(control => body.PointToClient(control.PointToScreen(new Point(control.Width, 0))).X + control.Margin.Right)
            .DefaultIfEmpty(0).Max();
        var left = Math.Max(tabRight + gap, Math.Min((int)Math.Round(280 * scale), stagePanel.Width / 5));
        var right = body.PointToClient(_drawSnappingStrip.PointToScreen(Point.Empty)).X - gap;
        var visibleCount = Math.Max(1, tools.Controls.Cast<Control>().Count(control => control.Visible));
        var compact = right - left < (int)Math.Round(240 * scale);
        var top = body.PointToClient(_workspaceTabs.PointToScreen(new Point(0, _workspaceTabs.Height))).Y + gap;
        var bottom = body.PointToClient(_stageMetrics.PointToScreen(new Point(0, _stageMetrics.Height))).Y - gap;
        if (compact)
        {
            left = stagePanel.Left + gap;
            right = _stageMetrics.Controls.Cast<Control>().Where(control => control.Visible)
                .Select(control => body.PointToClient(control.PointToScreen(Point.Empty)).X - gap)
                .DefaultIfEmpty(stagePanel.Right - gap).Min();
            top = body.PointToClient(_stageMetrics.PointToScreen(Point.Empty)).Y + gap;
        }
        var width = Math.Max(1, right - left);
        var height = Math.Max(1, bottom - top);
        tools.SuspendLayout();
        if (!ReferenceEquals(tools.Parent, body)) body.Controls.Add(tools);
        tools.AutoSize = false;
        tools.FlowDirection = FlowDirection.LeftToRight;
        tools.WrapContents = false;
        tools.Padding = new Padding(compact ? 1 : gap);
        tools.AutoScroll = false;
        tools.AutoScrollPosition = Point.Empty;
        var buttonHeight = Math.Max(1, height - tools.Padding.Vertical);
        var buttonGap = compact ? 1 : gap;
        var availableButtonWidth = Math.Max(1, (width - tools.Padding.Horizontal) / visibleCount - buttonGap);
        var buttonWidth = Math.Min(availableButtonWidth, compact ? buttonHeight : (int)Math.Round(64 * scale));
        foreach (Control control in tools.Controls)
        {
            control.Margin = new Padding(0, 0, buttonGap, 0);
            control.Size = new Size(buttonWidth, compact ? buttonWidth : buttonHeight);
            if (control is SvgIconButton button)
            {
                button.ShowsLabelBelowIcon = !compact;
                button.Text = compact ? string.Empty : UiLocalization.T(ToolbarButtonLabel(button));
                button.Invalidate();
            }
        }
        tools.SetBounds(left, top, width, height);
        tools.ResumeLayout(true);
        tools.BringToFront();
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
        if (ReferenceEquals(button, _shapeToolButton)) return "Shapes";
        if (ReferenceEquals(button, _lineToolButton)) return "Lines";
        if (ReferenceEquals(button, _brushToolButton)) return "Brush";
        if (ReferenceEquals(button, _paintToolGroup.ParentButton)) return "Fill";
        return button.Tag is ToolMode tool ? tool switch
        {
            ToolMode.Text => "Text",
            ToolMode.Eyedropper => "Eyedropper",
            ToolMode.Gradient => "Gradient",
            ToolMode.SnapPoint => "Snap points",
            ToolMode.Eraser => "Eraser",
            _ => "Tools"
        } : "Vault";
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
