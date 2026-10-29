namespace VectorAnimationEngine;

internal enum ObjectTransformField { X, Y, Width, Height, Rotation }

internal sealed class ObjectGeometryPanel : UserControl
{
    private readonly Label _title = new();
    private readonly Label _scope = new() { AutoEllipsis = true, UseMnemonic = false };
    private readonly Label[] _captions = new[] { "X (vu)", "Y (vu)", "Width (vu)", "Height (vu)", "Rotation" }
        .Select(InspectorModuleStyle.CreateFieldLabel).ToArray();
    private readonly ModernNumericUpDown[] _values = Enumerable.Range(0, 5).Select(_ => new ModernNumericUpDown()).ToArray();
    private readonly Label _ratioCaption = InspectorModuleStyle.CreateFieldLabel("Keep aspect ratio");
    private readonly ModernToggleSwitch _keepRatio = new() { AutoSize = false, AccessibleName = "Keep aspect ratio" };
    private readonly Label _source = InspectorModuleStyle.CreateTextLabel();
    private readonly Button _appearance = new() { Text = "Appearance" };
    private readonly ToolTip _toolTip = new();
    private string _bindingToken = "";
    private bool _ready;
    private bool _updating;
    private bool _interacting;
    private bool _ratioPreference;

    public ObjectGeometryPanel()
    {
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Panel;
        Font = Theme.UiFont();
        ForeColor = Theme.Text;
        AccessibleName = "Object transform";
        AccessibleRole = AccessibleRole.Pane;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        InspectorModuleStyle.StylePageTitle(_title);
        _title.Text = "Object transform";
        _scope.Font = Theme.UiFont();
        _scope.ForeColor = Theme.PanelSecondaryText;
        Theme.StyleStandardButton(_appearance);
        _appearance.Click += (_, _) => AppearanceRequested?.Invoke(this, EventArgs.Empty);
        Theme.StyleToolTip(_toolTip);
        for (var index = 0; index < _values.Length; index++)
        {
            var field = (ObjectTransformField)index;
            var editor = _values[index];
            editor.Minimum = field is ObjectTransformField.Width or ObjectTransformField.Height ? 1 : -1_000_000;
            editor.Maximum = 1_000_000;
            editor.DecimalPlaces = 2;
            editor.Increment = 1;
            editor.AccessibleName = _captions[index].Text;
            editor.Suffix = field == ObjectTransformField.Rotation ? "°" : "vu";
            editor.ShowSuffix = true;
            InspectorModuleStyle.StyleField(editor);
            var gestureToken = "";
            editor.InteractionStarted += (_, _) => { _interacting = true; gestureToken = _bindingToken; };
            editor.InteractionCanceled += (_, _) => _interacting = false;
            editor.InteractionCompleted += (_, interaction) =>
            {
                _interacting = false;
                if (!_updating && interaction.Changed && gestureToken == _bindingToken)
                    ValueCommitted?.Invoke(field, (float)editor.Value, _keepRatio.Checked);
            };
            editor.ValueChanged += (_, _) =>
            {
                if (!_updating && !_interacting) ValueCommitted?.Invoke(field, (float)editor.Value, _keepRatio.Checked);
            };
        }
        _keepRatio.CheckedChanged += (_, _) => { if (!_updating) _ratioPreference = _keepRatio.Checked; };
        Controls.AddRange([_title, _scope, _source, _appearance, _ratioCaption, _keepRatio, .. _captions, .. _values]);
        Height = PreferredHeight;
        _ready = true;
        UiLocalization.Watch(this);
    }

    public int PreferredHeight => ScaleMetric(_appearance.Enabled ? 434 : 390);
    public event EventHandler? AppearanceRequested;
    public event Action<ObjectTransformField, float, bool>? ValueCommitted;

    public void SetGeometry(RectangleF geometry, string? source, bool appearanceAvailable,
        float rotationDegrees = 0, bool editable = true, bool group = false, string bindingToken = "",
        bool relativeRotation = false, bool heightEditable = true, bool mixedSelection = false)
    {
        if (_interacting && _bindingToken == bindingToken) return;
        _bindingToken = bindingToken;
        _updating = true;
        try
        {
            var values = new[] { geometry.X, geometry.Y, geometry.Width, geometry.Height, rotationDegrees };
            for (var index = 0; index < values.Length; index++)
            {
                _values[index].Value = Math.Clamp((decimal)values[index], _values[index].Minimum, _values[index].Maximum);
                _values[index].Enabled = editable && (index != 3 || heightEditable);
            }
            _captions[4].Text = UiLocalization.T(group || relativeRotation ? "Rotate by" : "Rotation");
            _captions[2].Text = UiLocalization.T(heightEditable ? "Width (vu)" : "Length (vu)");
            _captions[3].Text = UiLocalization.T(heightEditable ? "Height (vu)" : "Selection height (vu)");
            _toolTip.SetToolTip(_values[3], heightEditable ? "" : UiLocalization.T("Edit line thickness on the Appearance page."));
            _scope.Text = UiLocalization.T(mixedSelection
                ? "Select either drawing objects or instances to edit transforms."
                : group
                ? "Multiple objects: center position, proportional group size and relative rotation."
                : relativeRotation ? "Whole-object bounds in vector units (vu); rotation is applied as an increment."
                : "Center position and local size in vector units (vu). Edits affect the whole object.");
            _toolTip.SetToolTip(_scope, _scope.Text);
            for (var index = 0; index < _values.Length; index++) _values[index].AccessibleName = _captions[index].Text;
            _keepRatio.Enabled = editable && !group;
            _keepRatio.Checked = group || _ratioPreference;
            _ratioCaption.Enabled = editable;
            var sourceText = source ?? UiLocalization.T("Geometry");
            if (_source.Text != sourceText) _source.Text = sourceText;
            _source.AccessibleDescription = sourceText;
            _toolTip.SetToolTip(_source, sourceText);
            _appearance.Enabled = appearanceAvailable;
            _appearance.Visible = appearanceAvailable;
        }
        finally { _updating = false; }
    }

    protected override void OnLayout(LayoutEventArgs eventArgs)
    {
        base.OnLayout(eventArgs);
        if (!_ready) return;
        var inset = ScaleMetric(16);
        var width = Math.Max(1, Width - inset * 2);
        var split = (int)Math.Floor(width * 0.48f);
        _title.SetBounds(inset + ScaleMetric(24), ScaleMetric(16), Math.Max(1, width - ScaleMetric(24)), ScaleMetric(28));
        _scope.SetBounds(inset, ScaleMetric(66), width, ScaleMetric(44));
        for (var index = 0; index < _values.Length; index++)
        {
            _captions[index].Dock = DockStyle.None;
            _captions[index].SetBounds(inset, ScaleMetric(118 + index * 40), Math.Max(1, split - ScaleMetric(8)), ScaleMetric(32));
            _values[index].SetBounds(inset + split, ScaleMetric(118 + index * 40), Math.Max(1, width - split), ScaleMetric(32));
            _values[index].TabIndex = index;
        }
        _ratioCaption.Dock = DockStyle.None;
        _ratioCaption.SetBounds(inset, ScaleMetric(318), Math.Max(1, split - ScaleMetric(8)), ScaleMetric(32));
        _keepRatio.SetBounds(inset + split, ScaleMetric(318), Math.Max(1, width - split), ScaleMetric(32));
        _keepRatio.TabIndex = 5;
        _source.SetBounds(inset, ScaleMetric(358), width, ScaleMetric(20));
        _appearance.SetBounds(inset, ScaleMetric(390), width, ScaleMetric(32));
        _appearance.TabIndex = 6;
        var height = PreferredHeight;
        if (Height != height) Height = height;
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        SvgIcons.Draw(eventArgs.Graphics, SvgIconKind.Transform, new Rectangle(ScaleMetric(16), ScaleMetric(21), ScaleMetric(18), ScaleMetric(18)), Theme.PanelSecondaryText);
        InspectorModuleStyle.DrawDivider(this, eventArgs.Graphics, ScaleMetric(54));
        InspectorModuleStyle.DrawBottomDivider(this, eventArgs.Graphics);
    }

    protected override void OnDpiChangedAfterParent(EventArgs eventArgs)
    {
        base.OnDpiChangedAfterParent(eventArgs);
        PerformLayout();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _toolTip.Dispose();
        base.Dispose(disposing);
    }

    private int ScaleMetric(int value) => Math.Max(1, (int)Math.Round(value * DeviceDpi / 96f));
}
