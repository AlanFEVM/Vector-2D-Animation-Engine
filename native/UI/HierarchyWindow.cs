namespace VectorAnimationEngine;

/// <summary>A reusable, modeless host for the workbench's existing hierarchy control.</summary>
internal sealed class HierarchyWindow : Form
{
    internal delegate bool WorkbenchCommandHandler(ref Message message, Keys keyData);

    private readonly WorkbenchCommandHandler _handleWorkbenchCommand;
    private bool _positionInitialized;

    public HierarchyWindow(HierarchyPanel panel, WorkbenchCommandHandler handleWorkbenchCommand)
    {
        _handleWorkbenchCommand = handleWorkbenchCommand;
        Text = "Hierarchy";
        AccessibleName = "Hierarchy";
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Font = Theme.UiFont();
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        ShowInTaskbar = false;
        ShowIcon = false;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(320, 480);
        MinimumSize = new Size(260, 300);
        panel.Dock = DockStyle.Fill;
        Controls.Add(panel);
        UiLocalization.Watch(this);
    }

    public void ToggleFor(Form owner)
    {
        if (Visible)
        {
            Hide();
            owner.Activate();
            return;
        }

        if (!_positionInitialized)
        {
            Location = new Point(owner.Left + (owner.Width - Width) / 2,
                owner.Top + (owner.Height - Height) / 2);
            _positionInitialized = true;
        }
        // Reopening after monitor changes must leave the title bar reachable.
        Bounds = MainForm.NormalizeRestartWindowBounds(Bounds,
            Screen.AllScreens.Select(screen => screen.WorkingArea).ToArray(), MinimumSize);
        Show(owner);
        Activate();
        SelectNextControl(null, true, true, true, false);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            Owner?.Activate();
        }
        base.OnFormClosing(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Hide();
            Owner?.Activate();
            return true;
        }

        // Keep arrows, expansion, search and Tab local to the tree. Editing commands
        // use MainForm's existing interaction guards, shortcut profiles and history.
        if (((keyData & (Keys.Control | Keys.Alt)) != Keys.None
                || keyData is Keys.Delete or Keys.F1 or Keys.F2 or Keys.F8)
            && _handleWorkbenchCommand(ref msg, keyData)) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
