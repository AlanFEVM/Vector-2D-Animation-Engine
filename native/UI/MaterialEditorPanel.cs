namespace VectorAnimationEngine;

internal sealed class MaterialChangedEventArgs : EventArgs
{
    public MaterialChangedEventArgs(Color fill, Color stroke, float strokeWidth, float opacity)
    {
        Fill = fill;
        Stroke = stroke;
        StrokeWidth = strokeWidth;
        Opacity = opacity;
    }

    public Color Fill { get; }
    public Color Stroke { get; }
    public float StrokeWidth { get; }
    public float Opacity { get; }
}

internal sealed class MaterialEditorPanel : UserControl
{
    private readonly Panel _fillPreview = new();
    private readonly Panel _strokePreview = new();
    private readonly ComboBox _fillPreset = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _strokePreset = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown _strokeWidth = new();
    private readonly TrackBar _opacity = new();
    private readonly Label _opacityValue = new();
    private readonly ColorDialog _colorDialog = new();
    private bool _updating;
    private Color _fill = Color.FromArgb(79, 179, 162);
    private Color _stroke = Color.FromArgb(238, 242, 241);

    public MaterialEditorPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Panel;
        MinimumSize = new Size(240, 260);
        Padding = new Padding(12);

        BuildUi();
        ApplyMaterial(_fill, _stroke, 2f, 1f, raiseEvent: false);
    }

    public event EventHandler<MaterialChangedEventArgs>? MaterialChanged;
    public event EventHandler? FillChanged;
    public event EventHandler? StrokeChanged;
    public event EventHandler? OpacityChanged;

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

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
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
            ColumnCount = 3,
            RowCount = 5,
            Padding = new Padding(0, 10, 0, 0)
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38));
        for (var i = 0; i < 5; i++) content.RowStyles.Add(new RowStyle(SizeType.Absolute, i == 4 ? 56 : 42));
        Controls.Add(content);
        content.BringToFront();

        AddLabel(content, "Fill", 0);
        ConfigurePreset(_fillPreset);
        content.Controls.Add(_fillPreset, 1, 0);
        ConfigurePreview(_fillPreview);
        content.Controls.Add(_fillPreview, 2, 0);

        AddLabel(content, "Stroke", 1);
        ConfigurePreset(_strokePreset);
        content.Controls.Add(_strokePreset, 1, 1);
        ConfigurePreview(_strokePreview);
        content.Controls.Add(_strokePreview, 2, 1);

        AddLabel(content, "Width", 2);
        _strokeWidth.Minimum = 0;
        _strokeWidth.Maximum = 32;
        _strokeWidth.DecimalPlaces = 1;
        _strokeWidth.Increment = 0.5m;
        _strokeWidth.Dock = DockStyle.Fill;
        Theme.StyleNumeric(_strokeWidth);
        _strokeWidth.ValueChanged += (_, _) => RaiseMaterialChanged(strokeChanged: true);
        content.Controls.Add(_strokeWidth, 1, 2);

        AddLabel(content, "Opacity", 3);
        _opacity.Minimum = 0;
        _opacity.Maximum = 100;
        _opacity.TickFrequency = 10;
        _opacity.Dock = DockStyle.Fill;
        _opacity.ValueChanged += (_, _) =>
        {
            _opacityValue.Text = $"{_opacity.Value}%";
            RaiseMaterialChanged(opacityChanged: true);
        };
        content.Controls.Add(_opacity, 1, 3);
        _opacityValue.Dock = DockStyle.Fill;
        _opacityValue.ForeColor = Theme.Muted;
        _opacityValue.BackColor = Theme.Panel;
        _opacityValue.TextAlign = ContentAlignment.MiddleRight;
        _opacityValue.Font = Theme.UiFont();
        content.Controls.Add(_opacityValue, 2, 3);

        var apply = new Button { Text = "Apply", Dock = DockStyle.Left, Width = 88, Height = 32 };
        Theme.StyleButton(apply);
        apply.Click += (_, _) => RaiseMaterialChanged();
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
        var strokeChanged = _stroke.ToArgb() != stroke.ToArgb() || Math.Abs(StrokeWidth - strokeWidth) > 0.001f;
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

        if (raiseEvent) RaiseMaterialChanged(fillChanged, strokeChanged, opacityChanged);
    }

    private void RaiseMaterialChanged(bool fillChanged = false, bool strokeChanged = false, bool opacityChanged = false)
    {
        if (_updating) return;

        _fillPreview.BackColor = _fill;
        _strokePreview.BackColor = _stroke;
        MaterialChanged?.Invoke(this, new MaterialChangedEventArgs(_fill, _stroke, StrokeWidth, Opacity));
        if (fillChanged) FillChanged?.Invoke(this, EventArgs.Empty);
        if (strokeChanged) StrokeChanged?.Invoke(this, EventArgs.Empty);
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
        preview.Margin = new Padding(6, 4, 0, 8);
        preview.Cursor = Cursors.Hand;
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
