namespace VectorAnimationEngine;

internal sealed class ShortcutProfilesDialog : ModernDialogForm
{
    private readonly ShortcutProfileEditorPanel _shortcutProfiles;

    public ShortcutProfilesDialog() : this(ShortcutProfiles.TraditionalFlashProfileId, [])
    {
    }

    public ShortcutProfilesDialog(string activeShortcutProfileId, IEnumerable<ShortcutProfileRecord>? customShortcutProfiles)
        : base("Shortcut profiles", new Size(860, 680))
    {
        AccessibleName = "Shortcut profiles";
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        DialogContent.Controls.Add(layout);
        _shortcutProfiles = new ShortcutProfileEditorPanel(activeShortcutProfileId, customShortcutProfiles)
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty
        };
        layout.Controls.Add(_shortcutProfiles, 0, 0);
        layout.Controls.Add(new Label
        {
            Text = "Confirm changes here, then save in Settings.",
            Dock = DockStyle.Fill,
            ForeColor = Theme.Muted,
            BackColor = Theme.Panel,
            Font = Theme.UiFont(8.8f),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(2, 0, 0, 0),
            Margin = Padding.Empty
        }, 0, 1);
        AcceptButton = AddDialogAction("OK", DialogResult.OK, DialogActionStyle.Primary);
        CancelButton = AddDialogAction("Cancel", DialogResult.Cancel);
        UiLocalization.Watch(this);
    }

    public string SelectedShortcutProfileId => _shortcutProfiles.ActiveProfileId;
    public ShortcutProfileRecord[] CustomShortcutProfiles => _shortcutProfiles.CustomProfiles;
}
