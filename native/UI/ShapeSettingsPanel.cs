namespace VectorAnimationEngine;

internal readonly record struct ShapeSettingsState(ShapeKind Shape, int VertexCount);

internal sealed class ShapeSettingsChangedEventArgs(ShapeSettingsState state) : EventArgs
{
    public ShapeSettingsState State { get; } = state;
}

internal sealed class ShapeSettingsPanel : UserControl
{
    private readonly Label _vertexCountLabel = new();
    private readonly ModernNumericUpDown _vertexCount = new();
    private ShapeKind _shape = ShapeKind.Polygon;
    private bool _updating;

    public ShapeSettingsPanel()
    {
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Font = Theme.UiFont();
        Padding = Theme.InspectorSectionPadding;
        MinimumSize = new Size(248, PreferredHeight);

        BuildUi();
        SetState(new(ShapeKind.Polygon, 6));
    }

    public int PreferredHeight => Theme.InspectorTitleHeight
        + Theme.InspectorRowHeight
        + Theme.InspectorSectionPaddingVertical * 2;

    internal static bool SupportsShape(ShapeKind shape) => shape is ShapeKind.Polygon or ShapeKind.Star;

    public event EventHandler<ShapeSettingsChangedEventArgs>? SettingsChanged;

    public ShapeSettingsState State => new(_shape, (int)_vertexCount.Value);

    public void SetState(ShapeSettingsState state)
    {
        var shape = state.Shape;
        if (!SupportsShape(shape)) return;
        _shape = shape;
        var polygon = shape == ShapeKind.Polygon;
        _updating = true;
        try
        {
            var label = polygon ? "Sides" : "Points";
            _vertexCountLabel.Text = UiLocalization.T(label);
            _vertexCount.Minimum = 3m;
            _vertexCount.Maximum = polygon ? 64m : 32m;
            _vertexCount.Value = Math.Clamp(state.VertexCount, 3, polygon ? 64 : 32);
        }
        finally
        {
            _updating = false;
        }
    }

    private void BuildUi()
    {
        var title = new Label
        {
            Text = UiLocalization.T("Shape Settings"),
            Dock = DockStyle.Top,
            Height = Theme.InspectorTitleHeight,
            ForeColor = Theme.Text,
            BackColor = Theme.Panel,
            Font = Theme.UiFont(10, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        };
        Controls.Add(title);

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.InspectorFieldLabelColumnWidth));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.InspectorRowHeight));
        Controls.Add(content);
        content.BringToFront();

        _vertexCountLabel.Dock = DockStyle.Fill;
        _vertexCountLabel.ForeColor = Theme.Muted;
        _vertexCountLabel.BackColor = Theme.Panel;
        _vertexCountLabel.Font = Theme.UiFont();
        _vertexCountLabel.TextAlign = ContentAlignment.MiddleLeft;
        _vertexCountLabel.AutoEllipsis = true;
        _vertexCountLabel.Margin = Theme.InspectorFieldMargin(rightGap: true);
        content.Controls.Add(_vertexCountLabel, 0, 0);

        _vertexCount.Dock = DockStyle.Fill;
        _vertexCount.Margin = Theme.InspectorFieldMargin(rightGap: false);
        _vertexCount.DecimalPlaces = 0;
        _vertexCount.Increment = 1m;
        _vertexCount.AccessibleName = "Polygon sides or star points";
        _vertexCount.AccessibleDescription = "Polygon sides or star points";
        _vertexCount.ValueChanged += (_, _) => UpdateSettings();
        content.Controls.Add(_vertexCount, 1, 0);
    }

    private void UpdateSettings()
    {
        if (_updating) return;
        SettingsChanged?.Invoke(this, new(State));
    }
}
