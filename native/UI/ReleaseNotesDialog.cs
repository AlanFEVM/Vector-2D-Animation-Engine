namespace VectorAnimationEngine;

internal sealed class ReleaseNotesDialog : ModernDialogForm
{
    public ReleaseNotesDialog()
        : base("Release Notes", new Size(680, 560))
    {
        AccessibleName = "Release Notes";
        DialogContent.Padding = Padding.Empty;

        var releaseNotes = new ReleaseNotesPanel
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty
        };
        DialogContent.Controls.Add(releaseNotes);

        var close = AddDialogAction("Close", DialogResult.Cancel, DialogActionStyle.Primary);
        AcceptButton = close;
        CancelButton = close;
        Shown += (_, _) => releaseNotes.FocusContent();

        UiLocalization.Watch(this);
    }
}
