namespace VectorAnimationEngine;

internal sealed class AssetDeleteDialog : ModernDialogForm
{
    private readonly ComboBox _replacement = new();

    public AssetDeleteDialog(
        string assetName,
        IReadOnlyList<string> usages,
        IReadOnlyList<(string Id, string Name)> replacements)
        : base(UiLocalization.T("Delete Asset"), new Size(760, 510))
    {
        AccessibleName = UiLocalization.T("Delete Asset");
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 1,
            RowCount = 6,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        DialogContent.Controls.Add(layout);

        var nameLabel = CreateLabel(assetName);
        nameLabel.Font = Theme.UiFont(11, FontStyle.Bold);
        nameLabel.AutoEllipsis = true;
        nameLabel.UseMnemonic = false;
        layout.Controls.Add(nameLabel, 0, 0);
        layout.Controls.Add(CreateLabel(UiLocalization.T(
            "This asset is in use. Replace its references, or delete the asset and all its uses. You can undo either operation.")), 0, 1);
        layout.Controls.Add(CreateLabel(string.Format(
            UiLocalization.T("Usage locations ({0})"), usages.Count)), 0, 2);

        var usageList = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            WordWrap = false,
            ScrollBars = ScrollBars.Both,
            Lines = usages.ToArray(),
            Margin = new Padding(0, 0, 0, 8),
            AccessibleName = UiLocalization.T("Usage locations"),
            TabIndex = 0
        };
        Theme.StyleTextBox(usageList);
        layout.Controls.Add(usageList, 0, 3);
        layout.Controls.Add(CreateLabel(UiLocalization.T("Replacement asset")), 0, 4);

        _replacement.Dock = DockStyle.Fill;
        _replacement.DropDownStyle = ComboBoxStyle.DropDownList;
        _replacement.Margin = Padding.Empty;
        _replacement.AccessibleName = UiLocalization.T("Replacement asset");
        _replacement.TabIndex = 1;
        _replacement.DisplayMember = nameof(ReplacementOption.Name);
        foreach (var replacement in replacements)
        {
            if (!string.IsNullOrWhiteSpace(replacement.Id))
                _replacement.Items.Add(new ReplacementOption(replacement.Id, replacement.Name));
        }
        Theme.StyleComboBox(_replacement);
        _replacement.SelectedIndex = -1;
        _replacement.Enabled = _replacement.Items.Count > 0;
        layout.Controls.Add(_replacement, 0, 5);

        var cancel = AddDialogAction(UiLocalization.T("Cancel"), DialogResult.Cancel);
        var replace = AddDialogAction(UiLocalization.T("Replace and Delete"), DialogResult.Yes,
            DialogActionStyle.Primary, () => !string.IsNullOrEmpty(ReplacementAssetId));
        var delete = AddDialogAction(UiLocalization.T("Delete All Uses"), DialogResult.OK,
            DialogActionStyle.Danger);
        replace.Width = 180;
        delete.Width = 160;
        replace.Enabled = false;
        _replacement.SelectedIndexChanged += (_, _) =>
            replace.Enabled = !string.IsNullOrEmpty(ReplacementAssetId);
        AcceptButton = cancel;
        CancelButton = cancel;
        Shown += (_, _) => cancel.Focus();
        UiLocalization.Watch(this);
    }

    public string? ReplacementAssetId => (_replacement.SelectedItem as ReplacementOption)?.Id;

    private static Label CreateLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        ForeColor = Theme.Text,
        BackColor = Theme.Panel,
        Margin = Padding.Empty,
        TextAlign = ContentAlignment.MiddleLeft
    };

    private sealed record ReplacementOption(string Id, string Name);
}
