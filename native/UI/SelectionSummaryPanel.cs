namespace VectorAnimationEngine;

/// <summary>Displays host-provided text without inspecting the project or selection.</summary>
internal sealed class SelectionSummaryPanel : UserControl
{
    private readonly Label[] _rows = Enumerable.Range(0, 4).Select(_ => new Label
    {
        Dock = DockStyle.Fill, ForeColor = Theme.Text, BackColor = Theme.Panel,
        Font = Theme.UiFont(), TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true, Margin = Padding.Empty
    }).ToArray();

    public SelectionSummaryPanel()
    {
        BackColor = Theme.Panel;
        Padding = new Padding(0, Theme.GapXs, 0, Theme.InspectorSectionPaddingVertical);
        Height = PreferredHeight;
        AccessibleRole = AccessibleRole.Pane;
        AccessibleName = UiLocalization.T("Inspector");
        var title = new Label
        {
            Text = UiLocalization.T("Inspector"), Dock = DockStyle.Top,
            Height = Theme.InspectorTitleHeight, ForeColor = Theme.Text, BackColor = Theme.Panel,
            Font = Theme.UiFont(10, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft
        };
        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = Theme.Panel, ColumnCount = 1, RowCount = 4,
            Padding = new Padding(0, Theme.InspectorContentPaddingTop, 0, 0)
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < _rows.Length; i++)
        {
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.InspectorMetaRowHeight));
            content.Controls.Add(_rows[i], 0, i);
        }
        Controls.Add(title);
        Controls.Add(content);
        content.BringToFront();
        SelectedText = UiLocalization.T("Selected: None");
        LayerText = UiLocalization.T("Layer: -");
        AtomsText = UiLocalization.T("Atoms: -");
        ObjectsText = UiLocalization.T("Objects: 0");
    }

    public int PreferredHeight => Theme.GapXs + Theme.InspectorTitleHeight
        + Theme.InspectorContentPaddingTop + Theme.InspectorMetaRowHeight * 4
        + Theme.InspectorSectionPaddingVertical;
    public string SelectedText { get => _rows[0].Text; set => SetRow(0, value); }
    public string LayerText { get => _rows[1].Text; set => SetRow(1, value); }
    public string AtomsText { get => _rows[2].Text; set => SetRow(2, value); }
    public string ObjectsText { get => _rows[3].Text; set => SetRow(3, value); }

    private void SetRow(int index, string value)
    {
        if (_rows[index].Text != value) _rows[index].Text = value;
    }
}
