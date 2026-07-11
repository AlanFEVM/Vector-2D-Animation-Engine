namespace VectorAnimationEngine;

internal sealed class MaterialChangedEventArgs : EventArgs
{
    public MaterialChangedEventArgs(
        Color fill,
        Color stroke,
        float strokeWidth,
        float opacity,
        bool fillChanged,
        bool strokeChanged,
        bool strokeWidthChanged,
        bool opacityChanged,
        bool applyAll)
    {
        Fill = fill;
        Stroke = stroke;
        StrokeWidth = strokeWidth;
        Opacity = opacity;
        FillChanged = fillChanged;
        StrokeChanged = strokeChanged;
        StrokeWidthChanged = strokeWidthChanged;
        OpacityChanged = opacityChanged;
        ApplyAll = applyAll;
    }

    public Color Fill { get; }
    public Color Stroke { get; }
    public float StrokeWidth { get; }
    public float Opacity { get; }
    public bool FillChanged { get; }
    public bool StrokeChanged { get; }
    public bool StrokeWidthChanged { get; }
    public bool OpacityChanged { get; }
    public bool ApplyAll { get; }
}

internal sealed class MaterialEditorPanel : UserControl
{
    private readonly Panel _fillPreview = new();
    private readonly Panel _strokePreview = new();
    private readonly ComboBox _fillPreset = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _strokePreset = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ModernNumericUpDown _strokeWidth = new() { Suffix = "pt" };
    private readonly ModernSlider _opacity = new();
    private readonly Label _opacityValue = new();
    private readonly ColorDialog _colorDialog = new();
    private bool _updating;
    private Color _fill = Color.FromArgb(79, 179, 162);
    private Color _stroke = Color.FromArgb(238, 242, 241);

    public MaterialEditorPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Panel;
        MinimumSize = new Size(240, 232);
        Padding = new Padding(0, 8, 0, 8);

        BuildUi();
        ApplyMaterial(_fill, _stroke, 2f, 1f, raiseEvent: false);
    }

    public event EventHandler<MaterialChangedEventArgs>? MaterialChanged;
    public event EventHandler? FillChanged;
    public event EventHandler? StrokeChanged;
    public event EventHandler? OpacityChanged;
    public event EventHandler? ContinuousEditStarted;
    public event EventHandler? ContinuousEditCompleted;
    public event EventHandler? ContinuousEditCanceled;

    public Color Fill
    {
        get => _fill;
        set => ApplyMaterial(value, _stroke, StrokeWidth, Opacity, raiseEvent: true);
    }

    public Color Stroke
    {
        get => _stroke;
        set => ApplyMaterial(_fill, value, StrokeWidth, Opacity, raiseEvent: true);
    }

    public float StrokeWidth
    {
        get => (float)_strokeWidth.Value;
        set => ApplyMaterial(_fill, _stroke, value, Opacity, raiseEvent: true);
    }

    public float Opacity
    {
        get => _opacity.Value / 100f;
        set => ApplyMaterial(_fill, _stroke, StrokeWidth, value, raiseEvent: true);
    }

    public void SetMaterial(Color fill, Color stroke, float strokeWidth, float opacity)
    {
        ApplyMaterial(fill, stroke, strokeWidth, opacity, raiseEvent: false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _colorDialog.Dispose();
        base.Dispose(disposing);
    }

    private void BuildUi()
    {
        var title = new Label
        {
            Text = "Materials",
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
            RowCount = 5,
            Padding = new Padding(0, 4, 0, 0)
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 5; i++) content.RowStyles.Add(new RowStyle(SizeType.Absolute, i == 4 ? 42 : 38));
        Controls.Add(content);
        content.BringToFront();

        AddLabel(content, "Fill", 0);
        var fillRow = ColorRow(_fillPreset, _fillPreview);
        content.Controls.Add(fillRow, 1, 0);

        ConfigurePreset(_fillPreset);
        ConfigurePreview(_fillPreview);

        AddLabel(content, "Stroke", 1);
        var strokeRow = ColorRow(_strokePreset, _strokePreview);
        content.Controls.Add(strokeRow, 1, 1);

        ConfigurePreset(_strokePreset);
        ConfigurePreview(_strokePreview);

        AddLabel(content, "Width pt", 2);
        _strokeWidth.Minimum = 0;
        _strokeWidth.Maximum = 32;
        _strokeWidth.DecimalPlaces = 1;
        _strokeWidth.Increment = 0.5m;
        _strokeWidth.Dock = DockStyle.Fill;
        _strokeWidth.Margin = new Padding(0, 4, 0, 4);
        Theme.StyleNumeric(_strokeWidth);
        _strokeWidth.ValueChanged += (_, _) => RaiseMaterialChanged(strokeWidthChanged: true);
        _strokeWidth.InteractionStarted += (_, _) => ContinuousEditStarted?.Invoke(this, EventArgs.Empty);
        _strokeWidth.InteractionCompleted += (_, _) => ContinuousEditCompleted?.Invoke(this, EventArgs.Empty);
        _strokeWidth.InteractionCanceled += (_, _) => ContinuousEditCanceled?.Invoke(this, EventArgs.Empty);
        content.Controls.Add(_strokeWidth, 1, 2);

        AddLabel(content, "Opacity", 3);
        _opacity.Minimum = 0;
        _opacity.Maximum = 100;
        _opacity.TickFrequency = 10;
        _opacity.Dock = DockStyle.Fill;
        _opacity.Margin = new Padding(0, 3, 0, 0);
        _opacity.ValueChanged += (_, _) =>
        {
            _opacityValue.Text = $"{_opacity.Value}%";
            RaiseMaterialChanged(opacityChanged: true);
        };
        _opacity.InteractionStarted += (_, _) => ContinuousEditStarted?.Invoke(this, EventArgs.Empty);
        _opacity.InteractionCompleted += (_, _) => ContinuousEditCompleted?.Invoke(this, EventArgs.Empty);
        _opacity.InteractionCanceled += (_, _) => ContinuousEditCanceled?.Invoke(this, EventArgs.Empty);
        var opacityRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty
        };
        opacityRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        opacityRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48));
        opacityRow.Controls.Add(_opacity, 0, 0);
        _opacityValue.Dock = DockStyle.Fill;
        _opacityValue.ForeColor = Theme.Muted;
        _opacityValue.BackColor = Theme.Panel;
        _opacityValue.TextAlign = ContentAlignment.MiddleCenter;
        _opacityValue.Font = Theme.UiFont();
        opacityRow.Controls.Add(_opacityValue, 1, 0);
        content.Controls.Add(opacityRow, 1, 3);

        var apply = new Button { Text = "Apply", Dock = DockStyle.Left, Width = 86, Height = 30, Margin = new Padding(0, 5, 0, 0) };
        Theme.StyleButton(apply);
        apply.Click += (_, _) => RaiseMaterialChanged(applyAll: true);
        content.Controls.Add(apply, 1, 4);

        LoadPresets(_fillPreset);
        LoadPresets(_strokePreset);
        _fillPreset.SelectedIndexChanged += (_, _) => SelectPreset(_fillPreset, fill: true);
        _strokePreset.SelectedIndexChanged += (_, _) => SelectPreset(_strokePreset, fill: false);
        _fillPreview.Click += (_, _) => PickColor(fill: true);
        _strokePreview.Click += (_, _) => PickColor(fill: false);
    }

    private void ApplyMaterial(Color fill, Color stroke, float strokeWidth, float opacity, bool raiseEvent)
    {
        var fillChanged = _fill.ToArgb() != fill.ToArgb();
        var strokeChanged = _stroke.ToArgb() != stroke.ToArgb();
        var strokeWidthChanged = Math.Abs(StrokeWidth - strokeWidth) > 0.001f;
        var opacityChanged = Math.Abs(Opacity - opacity) > 0.001f;

        _updating = true;
        _fill = fill;
        _stroke = stroke;
        _strokeWidth.Value = (decimal)Math.Clamp(strokeWidth, (float)_strokeWidth.Minimum, (float)_strokeWidth.Maximum);
        _opacity.Value = (int)Math.Clamp(MathF.Round(opacity * 100f), _opacity.Minimum, _opacity.Maximum);
        _opacityValue.Text = $"{_opacity.Value}%";
        _fillPreview.BackColor = _fill;
        _strokePreview.BackColor = _stroke;
        MatchPreset(_fillPreset, _fill);
        MatchPreset(_strokePreset, _stroke);
        _updating = false;

        if (raiseEvent) RaiseMaterialChanged(fillChanged, strokeChanged, strokeWidthChanged, opacityChanged);
    }

    private void RaiseMaterialChanged(
        bool fillChanged = false,
        bool strokeChanged = false,
        bool strokeWidthChanged = false,
        bool opacityChanged = false,
        bool applyAll = false)
    {
        if (_updating) return;

        _fillPreview.BackColor = _fill;
        _strokePreview.BackColor = _stroke;
        MaterialChanged?.Invoke(this, new MaterialChangedEventArgs(
            _fill,
            _stroke,
            StrokeWidth,
            Opacity,
            fillChanged,
            strokeChanged,
            strokeWidthChanged,
            opacityChanged,
            applyAll));
        if (fillChanged) FillChanged?.Invoke(this, EventArgs.Empty);
        if (strokeChanged || strokeWidthChanged) StrokeChanged?.Invoke(this, EventArgs.Empty);
        if (opacityChanged) OpacityChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SelectPreset(ComboBox combo, bool fill)
    {
        if (_updating || combo.SelectedItem is not MaterialPreset preset) return;
        if (fill)
        {
            _fill = preset.Color;
            RaiseMaterialChanged(fillChanged: true);
        }
        else
        {
            _stroke = preset.Color;
            RaiseMaterialChanged(strokeChanged: true);
        }
    }

    private void PickColor(bool fill)
    {
        _colorDialog.Color = fill ? _fill : _stroke;
        if (_colorDialog.ShowDialog(this) != DialogResult.OK) return;
        if (fill)
        {
            _fill = _colorDialog.Color;
            MatchPreset(_fillPreset, _fill);
            RaiseMaterialChanged(fillChanged: true);
        }
        else
        {
            _stroke = _colorDialog.Color;
            MatchPreset(_strokePreset, _stroke);
            RaiseMaterialChanged(strokeChanged: true);
        }
    }

    private static void AddLabel(TableLayoutPanel content, string text, int row)
    {
        content.Controls.Add(new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            ForeColor = Theme.Muted,
            BackColor = Theme.Panel,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = Theme.UiFont()
        }, 0, row);
    }

    private static void ConfigurePreset(ComboBox combo)
    {
        combo.Dock = DockStyle.Fill;
        Theme.StyleComboBox(combo);
    }

    private static void ConfigurePreview(Panel preview)
    {
        preview.Dock = DockStyle.Fill;
        preview.Margin = new Padding(8, 5, 0, 5);
        preview.Cursor = Cursors.Hand;
    }

    private static TableLayoutPanel ColorRow(Control preset, Control preview)
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));
        row.Controls.Add(preset, 0, 0);
        row.Controls.Add(preview, 1, 0);
        return row;
    }

    private static void LoadPresets(ComboBox combo)
    {
        combo.Items.AddRange(
        [
            new MaterialPreset("Teal", Color.FromArgb(79, 179, 162)),
            new MaterialPreset("Amber", Color.FromArgb(213, 151, 74)),
            new MaterialPreset("Coral", Color.FromArgb(200, 107, 99)),
            new MaterialPreset("Violet", Color.FromArgb(136, 122, 214)),
            new MaterialPreset("White", Color.FromArgb(238, 242, 241)),
            new MaterialPreset("Black", Color.FromArgb(30, 32, 34))
        ]);
        combo.SelectedIndex = 0;
    }

    private static void MatchPreset(ComboBox combo, Color color)
    {
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is MaterialPreset preset && preset.Color.ToArgb() == color.ToArgb())
            {
                combo.SelectedIndex = i;
                return;
            }
        }

        combo.SelectedIndex = -1;
    }

    private readonly record struct MaterialPreset(string Name, Color Color)
    {
        public override string ToString() => Name;
    }
}
