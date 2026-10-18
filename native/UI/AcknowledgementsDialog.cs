namespace VectorAnimationEngine;

internal sealed class AcknowledgementsDialog : ModernDialogForm
{
    public AcknowledgementsDialog()
        : this(ReleaseNotesCatalog.Acknowledgements, UiLocalization.CurrentLanguage)
    {
    }

    internal AcknowledgementsDialog(ReleaseAcknowledgementEntry acknowledgedBy, UiLanguage language)
        : base("Acknowledgements", new Size(560, 420))
    {
        ArgumentNullException.ThrowIfNull(acknowledgedBy);

        AccessibleName = UiLocalization.T("Acknowledgements", language);
        DialogContent.Padding = Padding.Empty;

        var panel = new ReleaseNotesPanel(acknowledgedBy, language)
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty
        };
        DialogContent.Controls.Add(panel);

        var close = AddDialogAction("Close", DialogResult.Cancel, DialogActionStyle.Primary);
        AcceptButton = close;
        CancelButton = close;
        Shown += (_, _) => panel.FocusContent();

        UiLocalization.Watch(this);
    }
}
