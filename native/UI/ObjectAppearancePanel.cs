namespace VectorAnimationEngine;

internal sealed class ObjectAppearancePanel : UserControl
{
    private readonly Label _title = new();
    private readonly ColorTargetButton _fill = new("Fill");
    private readonly ColorTargetButton _stroke = new("Stroke") { OutlineSwatch = true };
    private readonly Label _widthLabel = InspectorModuleStyle.CreateFieldLabel("Width");
    private readonly Label _opacityLabel = InspectorModuleStyle.CreateFieldLabel("Opacity");
    private readonly ModernNumericUpDown _width = new();
    private readonly ModernNumericUpDown _opacity = new();
    private readonly Button _more = new() { Text = "Appearance" };
    private bool _updating;
    private bool _ready;
    private bool _interacting;
    private bool _fillAvailable = true;
    private bool _strokeAvailable = true;

    public ObjectAppearancePanel()
    {
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Font = Theme.UiFont();
        AccessibleName = "Appearance";
        AccessibleRole = AccessibleRole.Pane;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        InspectorModuleStyle.StylePageTitle(_title);
        _title.Text = "Appearance";
        Theme.StyleStandardButton(_more);
        _fill.Click += (_, _) => ColorRequested?.Invoke(false, _fill);
        _stroke.Click += (_, _) => ColorRequested?.Invoke(true, _stroke);
        _more.Click += (_, _) => MoreRequested?.Invoke(this, EventArgs.Empty);
        _width.Minimum = 0;
        _width.Maximum = 512;
        _width.DecimalPlaces = 2;
        _width.Increment = 0.5m;
        _width.Suffix = "pt";
        _width.ShowSuffix = true;
        _opacity.Minimum = 0;
        _opacity.Maximum = 100;
        _opacity.DecimalPlaces = 1;
        _opacity.Increment = 1;
        _opacity.Suffix = "%";
        _opacity.ShowSuffix = true;
        _width.AccessibleName = "Width";
        _opacity.AccessibleName = "Opacity";
        foreach (var editor in new[] { _width, _opacity })
        {
            InspectorModuleStyle.StyleField(editor);
            editor.InteractionStarted += (_, _) =>
            {
                if (_updating) return;
                _interacting = true;
                InteractionStarted?.Invoke(this, EventArgs.Empty);
            };
            editor.InteractionCompleted += (_, _) =>
            {
                _interacting = false;
                InteractionCompleted?.Invoke(this, EventArgs.Empty);
            };
            editor.InteractionCanceled += (_, _) =>
            {
                _interacting = false;
                InteractionCanceled?.Invoke(this, EventArgs.Empty);
            };
        }
        _width.ValueChanged += (_, _) => { if (!_updating) StrokeWidthChanged?.Invoke((float)_width.Value); };
        _opacity.ValueChanged += (_, _) => { if (!_updating) OpacityChanged?.Invoke((float)_opacity.Value / 100f); };
        Controls.AddRange([_title, _fill, _stroke, _widthLabel, _width, _opacityLabel, _opacity, _more]);
        Height = PreferredHeight;
        _ready = true;
        UiLocalization.Watch(this);
    }

    public event Action<bool, Control>? ColorRequested;
    public event Action<float>? StrokeWidthChanged;
    public event Action<float>? OpacityChanged;
    public event EventHandler? MoreRequested;
    public event EventHandler? InteractionStarted;
    public event EventHandler? InteractionCompleted;
    public event EventHandler? InteractionCanceled;
    public int PreferredHeight => ScaleMetric(_strokeAvailable ? 232 : 192);

    public void SetMaterial(MaterialEditorPanel editor, bool fillAvailable, bool strokeAvailable, bool mixed)
    {
        _updating = true;
        try
        {
            _fillAvailable = fillAvailable;
            _strokeAvailable = strokeAvailable;
            _fill.Visible = fillAvailable;
            _stroke.Visible = strokeAvailable;
            _width.Visible = _widthLabel.Visible = strokeAvailable;
            _fill.Selected = !editor.EditingStroke;
            _stroke.Selected = editor.EditingStroke;
            _fill.SwatchColor = editor.Fill;
            _stroke.SwatchColor = editor.Stroke;
            var fillPaint = editor.GetGradientPaintForTarget(false);
            var strokePaint = editor.GetGradientPaintForTarget(true);
            _fill.SetGradientPreview(fillPaint.Kind, fillPaint.Stops);
            _stroke.SetGradientPreview(strokePaint.Kind, strokePaint.Stops);
            _fill.DetailText = _stroke.DetailText = mixed ? UiLocalization.T("Mixed") : string.Empty;
            if (!_interacting)
            {
                _width.Value = Math.Clamp((decimal)editor.StrokeWidth, _width.Minimum, _width.Maximum);
                _opacity.Value = Math.Clamp((decimal)(editor.Opacity * 100f), _opacity.Minimum, _opacity.Maximum);
            }
        }
        finally { _updating = false; }
        PerformLayout();
    }

    protected override void OnLayout(LayoutEventArgs eventArgs)
    {
        base.OnLayout(eventArgs);
        if (!_ready) return;
        var inset = ScaleMetric(16);
        var available = Math.Max(1, Width - inset * 2);
        var split = (int)Math.Floor(available * 0.48f);
        _title.SetBounds(inset + ScaleMetric(24), ScaleMetric(16), Math.Max(1, available - ScaleMetric(24)), ScaleMetric(28));
        var gap = ScaleMetric(8);
        var both = _fillAvailable && _strokeAvailable;
        var swatchWidth = both ? Math.Max(1, (available - gap) / 2) : available;
        _fill.SetBounds(inset, ScaleMetric(66), swatchWidth, ScaleMetric(32));
        _stroke.SetBounds(both ? inset + swatchWidth + gap : inset, ScaleMetric(66), both ? available - swatchWidth - gap : available, ScaleMetric(32));
        var row = 106;
        if (_strokeAvailable)
        {
            LayoutField(_widthLabel, _width, row);
            row += 40;
        }
        LayoutField(_opacityLabel, _opacity, row);
        _more.SetBounds(inset, ScaleMetric(row + 44), available, ScaleMetric(32));
        if (Height != PreferredHeight) Height = PreferredHeight;

        void LayoutField(Label label, Control field, int top)
        {
            label.Dock = DockStyle.None;
            label.SetBounds(inset, ScaleMetric(top), Math.Max(1, split - gap), ScaleMetric(32));
            field.SetBounds(inset + split, ScaleMetric(top), Math.Max(1, available - split), ScaleMetric(32));
        }
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        SvgIcons.Draw(eventArgs.Graphics, SvgIconKind.Swatches, new Rectangle(ScaleMetric(16), ScaleMetric(22), ScaleMetric(14), ScaleMetric(14)), Theme.PanelSecondaryText);
        InspectorModuleStyle.DrawDivider(this, eventArgs.Graphics, ScaleMetric(54));
        InspectorModuleStyle.DrawBottomDivider(this, eventArgs.Graphics);
    }

    protected override void OnDpiChangedAfterParent(EventArgs eventArgs)
    {
        base.OnDpiChangedAfterParent(eventArgs);
        PerformLayout();
    }

    private int ScaleMetric(int value) => (int)Math.Round(value * DeviceDpi / 96f);
}
