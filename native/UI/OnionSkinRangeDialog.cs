namespace VectorAnimationEngine;

internal sealed class OnionSkinRangeDialog : Form
{
    private readonly ModernNumericUpDown _previous = new()
    {
        Minimum = 0,
        Maximum = VectorScene.MaximumOnionSkinFrames,
        DecimalPlaces = 0,
        Increment = 1,
        Suffix = " frames",
        AccessibleName = "Previous onion skin frames"
    };

    private readonly ModernNumericUpDown _next = new()
    {
        Minimum = 0,
        Maximum = VectorScene.MaximumOnionSkinFrames,
        DecimalPlaces = 0,
        Increment = 1,
        Suffix = " frames",
        AccessibleName = "Next onion skin frames"
    };

    public OnionSkinRangeDialog(int previousFrames, int nextFrames)
    {
        Text = "Onion Skin Range";
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        ClientSize = new Size(288, 164);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Font = Theme.UiFont();

        _previous.Value = Math.Clamp(previousFrames, (int)_previous.Minimum, (int)_previous.Maximum);
        _next.Value = Math.Clamp(nextFrames, (int)_next.Minimum, (int)_next.Maximum);
        BuildUi();
    }

    public int PreviousFrames => (int)_previous.Value;
    public int NextFrames => (int)_next.Value;

    private void BuildUi()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            Padding = new Padding(16, 14, 16, 14),
            ColumnCount = 2,
            RowCount = 3
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        AddField(layout, "Previous", _previous, 0);
        AddField(layout, "Next", _next, 1);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 10, 0, 0),
            Margin = Padding.Empty
        };
        var apply = new Button
        {
            Text = "Apply",
            DialogResult = DialogResult.OK,
            Width = 76,
            Height = Theme.ControlHeightCompact,
            AccessibleName = "Apply onion skin range",
            Margin = Padding.Empty
        };
        Theme.StyleActiveButton(apply);
        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Width = 76,
            Height = Theme.ControlHeightCompact,
            AccessibleName = "Cancel onion skin range",
            Margin = new Padding(0, 0, Theme.GapSm, 0)
        };
        Theme.StyleButton(cancel);
        actions.Controls.Add(apply);
        actions.Controls.Add(cancel);
        layout.Controls.Add(actions, 0, 2);
        layout.SetColumnSpan(actions, 2);

        Controls.Add(layout);
        AcceptButton = apply;
        CancelButton = cancel;
    }

    private static void AddField(TableLayoutPanel layout, string label, ModernNumericUpDown input, int row)
    {
        var caption = new Label
        {
            Text = label,
            Dock = DockStyle.Fill,
            ForeColor = Theme.Muted,
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleLeft,
            AccessibleName = $"{label} onion skin range"
        };
        Theme.StyleNumeric(input);
        input.Dock = DockStyle.Fill;
        input.Margin = Padding.Empty;
        layout.Controls.Add(caption, 0, row);
        layout.Controls.Add(input, 1, row);
    }
}
