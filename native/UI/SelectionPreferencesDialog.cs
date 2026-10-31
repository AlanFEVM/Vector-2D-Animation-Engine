namespace VectorAnimationEngine;

internal sealed class SelectionPreferencesDialog : ModernDialogForm
{
    private readonly ComboBox _blackArrow = CreateBehaviorSelector();
    private readonly ComboBox _whiteArrow = CreateBehaviorSelector();
    private readonly ModernToggleSwitch _enterSymbols = new() { Text = "Double-click to enter symbol editing" };
    private readonly ModernToggleSwitch _corners = new() { Text = "Show group corner marks" };
    private readonly ModernToggleSwitch _label = new() { Text = "Show group selection label" };
    private readonly ModernNumericUpDown _width = new() { Minimum = 0.75m, Maximum = 3, Increment = 0.25m, DecimalPlaces = 2 };
    private readonly ModernNumericUpDown _tolerance = new() { Minimum = 4, Maximum = 20, Increment = 1, DecimalPlaces = 0 };

    public SelectionPreferencesDialog() : this(new SelectionArrowSettings()) { }

    public SelectionPreferencesDialog(SelectionArrowSettings settings)
        : base("Selection and editing", new Size(640, 600))
    {
        AccessibleName = "Selection and editing";
        var scroll = new ThemedScrollPanel { Dock = DockStyle.Fill, ContentPadding = Padding.Empty };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2,
            BackColor = Theme.Panel, Padding = new Padding(8), Margin = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
        AddField(layout, "Black arrow", _blackArrow);
        AddField(layout, "White arrow", _whiteArrow);
        AddField(layout, "Group outline width (px)", _width);
        AddField(layout, "Selection hit tolerance (px)", _tolerance);
        foreach (var toggle in new[] { _enterSymbols, _corners, _label })
        {
            var row = layout.RowCount++;
            toggle.Dock = DockStyle.Fill;
            toggle.ForeColor = Theme.Text;
            toggle.AccessibleName = toggle.Text;
            toggle.Margin = new Padding(0, 6, 0, 6);
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.Controls.Add(toggle, 0, row);
            layout.SetColumnSpan(toggle, 2);
        }
        var description = new Label
        {
            Text = "Selection highlights only the clicked fill or stroke with a stipple pattern. Groups stay whole. Path editing exposes geometry handles. Shared symbols require entering their editor.",
            ForeColor = Theme.Muted, Dock = DockStyle.Fill, AutoSize = false,
            Margin = new Padding(0, 10, 0, 6), UseMnemonic = false
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
        layout.Controls.Add(description, 0, layout.RowCount++);
        layout.SetColumnSpan(description, 2);
        var reset = new Button { Text = "Restore recommended selection settings", Dock = DockStyle.Fill };
        Theme.StyleStandardButton(reset);
        reset.Click += (_, _) => Bind(new SelectionArrowSettings());
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.Controls.Add(reset, 0, layout.RowCount++);
        layout.SetColumnSpan(reset, 2);
        scroll.Content.Controls.Add(layout);
        DialogContent.Controls.Add(scroll);
        AcceptButton = AddDialogAction("OK", DialogResult.OK, DialogActionStyle.Primary);
        CancelButton = AddDialogAction("Cancel", DialogResult.Cancel);
        Bind(settings.Normalized());
        UiLocalization.Watch(this);
    }

    public SelectionArrowSettings SelectedSettings => new SelectionArrowSettings
    {
        BlackArrow = (SelectionArrowBehavior)_blackArrow.SelectedIndex,
        WhiteArrow = (SelectionArrowBehavior)_whiteArrow.SelectedIndex,
        DoubleClickEntersSymbols = _enterSymbols.Checked,
        ShowCornerMarks = _corners.Checked,
        ShowSelectionLabel = _label.Checked,
        OutlineWidth = (float)_width.Value,
        HitTolerancePixels = (int)_tolerance.Value
    }.Normalized();

    private void Bind(SelectionArrowSettings settings)
    {
        _blackArrow.SelectedIndex = (int)settings.BlackArrow;
        _whiteArrow.SelectedIndex = (int)settings.WhiteArrow;
        _enterSymbols.Checked = settings.DoubleClickEntersSymbols;
        _corners.Checked = settings.ShowCornerMarks;
        _label.Checked = settings.ShowSelectionLabel;
        _width.Value = (decimal)settings.OutlineWidth;
        _tolerance.Value = settings.HitTolerancePixels;
    }

    private static ComboBox CreateBehaviorSelector()
    {
        var selector = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        selector.Items.AddRange([UiLocalization.T("Fill / stroke selection"), UiLocalization.T("Path editing")]);
        Theme.StyleComboBox(selector);
        return selector;
    }

    private static void AddField(TableLayoutPanel layout, string caption, Control editor)
    {
        var row = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        layout.Controls.Add(new Label
        {
            Text = caption, ForeColor = Theme.Text, Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, UseMnemonic = false
        }, 0, row);
        editor.Dock = DockStyle.Fill;
        editor.Margin = new Padding(6, 6, 0, 6);
        editor.AccessibleName = UiLocalization.T(caption);
        layout.Controls.Add(editor, 1, row);
    }
}
