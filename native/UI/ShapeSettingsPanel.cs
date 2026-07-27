namespace VectorAnimationEngine;

internal sealed class ShapeSettingsPanel : UserControl
{
    private readonly DrawSettings _settings;
    private readonly Label _vertexCountLabel = new();
    private readonly ModernNumericUpDown _vertexCount = new();
    private ShapeKind _shape = ShapeKind.Polygon;
    private bool _updating;

    public ShapeSettingsPanel(DrawSettings settings)
    {
        _settings = settings;
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Font = Theme.UiFont();
        Padding = new Padding(0, 8, 0, 8);
        MinimumSize = new Size(248, PreferredHeight);

        BuildUi();
        SetShape(settings.ShapeKind);
        _settings.Changed += SettingsChanged;
    }

    public int PreferredHeight => 80;

    internal static bool SupportsShape(ShapeKind shape) => shape is ShapeKind.Polygon or ShapeKind.Star;

    public void SetShape(ShapeKind shape)
    {
        if (!SupportsShape(shape)) return;
        _shape = shape;
        var polygon = shape == ShapeKind.Polygon;
        _updating = true;
        try
        {
            var label = polygon ? "Sides" : "Points";
            _vertexCountLabel.Text = label;
            _vertexCount.Minimum = 3m;
            _vertexCount.Maximum = polygon ? 64m : 32m;
            _vertexCount.Value = polygon
                ? Math.Clamp(_settings.PolygonSides, 3, 64)
                : Math.Clamp(_settings.StarPoints, 3, 32);
        }
        finally
        {
            _updating = false;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _settings.Changed -= SettingsChanged;
        base.Dispose(disposing);
    }

    private void BuildUi()
    {
        var title = new Label
        {
            Text = "Shape Settings",
            Dock = DockStyle.Top,
            Height = 28,
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
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        Controls.Add(content);
        content.BringToFront();

        _vertexCountLabel.Dock = DockStyle.Fill;
        _vertexCountLabel.ForeColor = Theme.Muted;
        _vertexCountLabel.BackColor = Theme.Panel;
        _vertexCountLabel.Font = Theme.UiFont();
        _vertexCountLabel.TextAlign = ContentAlignment.MiddleLeft;
        _vertexCountLabel.AutoEllipsis = true;
        _vertexCountLabel.Margin = new Padding(0, 3, 8, 3);
        content.Controls.Add(_vertexCountLabel, 0, 0);

        _vertexCount.Dock = DockStyle.Fill;
        _vertexCount.Margin = new Padding(0, 3, 0, 3);
        _vertexCount.DecimalPlaces = 0;
        _vertexCount.Increment = 1m;
        _vertexCount.AccessibleName = "Polygon sides or star points";
        _vertexCount.AccessibleDescription = "Polygon sides or star points";
        _vertexCount.ValueChanged += (_, _) => UpdateSettings();
        content.Controls.Add(_vertexCount, 1, 0);
    }

    private void SettingsChanged(object? sender, EventArgs e) => SetShape(_shape);

    private void UpdateSettings()
    {
        if (_updating) return;
        if (_shape == ShapeKind.Polygon) _settings.PolygonSides = (int)_vertexCount.Value;
        else _settings.StarPoints = (int)_vertexCount.Value;
        _settings.NotifyChanged();
    }
}
