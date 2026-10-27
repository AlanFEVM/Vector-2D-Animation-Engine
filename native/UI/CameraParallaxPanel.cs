namespace VectorAnimationEngine;

internal sealed record CameraParallaxLayer(string Id, string Name, int ColorArgb, float Depth, bool Enabled);
internal sealed class CameraParallaxLayerEventArgs(string layerId, float depth, bool enabled) : EventArgs
{
    public string LayerId { get; } = layerId;
    public float Depth { get; } = depth;
    public bool Enabled { get; } = enabled;
}

internal sealed class CameraParallaxPanel : UserControl
{
    private readonly SvgIconButton _disclosure = new(SvgIconKind.ChevronDown)
        { Text = "2D parallax", IsDisclosureHeader = true };
    private readonly CheckBox _enabled = new ParallaxCheckBox { Text = "Enabled", AccessibleName = "Enable parallax", AutoSize = false };
    private readonly ModernNumericUpDown _strength = Number("Parallax strength", 0, 200, true);
    private readonly ModernNumericUpDown _cameraZ = Number("Camera Z");
    private readonly ModernNumericUpDown _referenceZ = Number("Reference plane Z");
    private readonly CheckBox _keepSize = new ParallaxCheckBox { Text = "Keep size", AutoSize = false };
    private readonly Label _layerTitle = new() { Text = "Layer depth", AutoEllipsis = true };
    private readonly Label _layerDirection = new() { Text = "Near − / Far +", AutoEllipsis = true, TextAlign = ContentAlignment.MiddleRight };
    private readonly Label _emptyLayers = new() { Text = "No layers yet", AutoEllipsis = true, TextAlign = ContentAlignment.MiddleCenter };
    private readonly ThemedScrollPanel _layers = new() { ShowScrollBar = true };
    private readonly CameraDepthDiagram _diagram = new();
    private readonly Button _closer = new() { Text = "Move closer" };
    private readonly Button _farther = new() { Text = "Move farther" };
    private readonly SvgIconButton _advanced = new(SvgIconKind.ChevronRight)
        { Text = "Advanced response", IsDisclosureHeader = true };
    private readonly ModernNumericUpDown _horizontal = Number("Horizontal response", 0, 200, true);
    private readonly ModernNumericUpDown _vertical = Number("Vertical response", 0, 200, true);
    private readonly CheckBox _dolly = new ParallaxCheckBox { Text = "Dolly depth scaling", AutoSize = false };
    private readonly Label _hint = new() { Text = "Drag depth lines · Shift: precise · Esc: cancel", AutoEllipsis = true };
    private readonly List<LayerRow> _rows = [];
    private SceneShotSettings _settings = SceneShotSettings.Default;
    private CameraParallaxLayer[] _layerState = [];
    private bool _updating;
    private bool _expanded = true;
    private bool _advancedExpanded;
    private bool _arranging;
    private string _cameraId = "";
    private int _frame = -1;

    internal event EventHandler? EditStarted;
    internal event EventHandler? EditCompleted;
    internal event EventHandler? EditCanceled;
    internal event EventHandler<ShotDirectorFramingEventArgs>? SettingsChanged;
    internal event EventHandler<CameraParallaxLayerEventArgs>? LayerChanged;

    public CameraParallaxPanel()
    {
        AutoScaleMode = AutoScaleMode.None;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Panel;
        Controls.AddRange([_disclosure, _enabled, _keepSize, _layerTitle, _layerDirection, _emptyLayers, _layers, _diagram,
            _closer, _farther, _advanced, _dolly, _hint]);
        foreach (var control in new Control[] { _disclosure, _advanced, _closer, _farther })
            Theme.StyleButton((Button)control);
        foreach (var control in new Control[] { _enabled, _keepSize, _dolly, _layerTitle, _layerDirection, _emptyLayers, _hint })
        {
            control.Font = Theme.UiFont();
            control.ForeColor = control == _hint || control == _layerTitle || control == _layerDirection || control == _emptyLayers ? Theme.PanelSecondaryText : Theme.Text;
            control.BackColor = Theme.Panel;
        }
        AddField(_strength, "Parallax strength");
        AddField(_cameraZ, "Camera Z");
        AddField(_referenceZ, "Reference plane Z");
        AddField(_horizontal, "Horizontal response");
        AddField(_vertical, "Vertical response");
        _disclosure.Click += (_, _) =>
        {
            _diagram.CancelGesture();
            _expanded = !_expanded;
            _disclosure.Icon = _expanded ? SvgIconKind.ChevronDown : SvgIconKind.ChevronRight;
            PerformLayout();
        };
        _advanced.Click += (_, _) =>
        {
            _advancedExpanded = !_advancedExpanded;
            _advanced.Icon = _advancedExpanded ? SvgIconKind.ChevronDown : SvgIconKind.ChevronRight;
            PerformLayout();
        };
        _enabled.CheckedChanged += (_, _) => Discrete(() => CommitSettings(captureAnchor: _enabled.Checked));
        _keepSize.CheckedChanged += (_, _) => Discrete(() => CommitSettings(captureAnchor: _keepSize.Checked));
        _dolly.CheckedChanged += (_, _) => Discrete(() => CommitSettings());
        _diagram.EditStarted += (_, _) => EditStarted?.Invoke(this, EventArgs.Empty);
        _diagram.EditCompleted += (_, _) => EditCompleted?.Invoke(this, EventArgs.Empty);
        _diagram.EditCanceled += (_, _) => EditCanceled?.Invoke(this, EventArgs.Empty);
        _diagram.DepthChanged += (_, eventArgs) => ChangeDepth(eventArgs.LayerId, eventArgs.Depth);
        _diagram.SelectionChanged += (_, _) => UpdateMoveLabels();
        _closer.Click += (_, _) => Discrete(() => StepDepth(-1));
        _farther.Click += (_, _) => Discrete(() => StepDepth(1));
        VisibleChanged += (_, _) => { if (!Visible) _diagram.CancelGesture(); };
        EnabledChanged += (_, _) => { if (!Enabled) _diagram.CancelGesture(); };
        UiLocalization.Watch(this);
    }

    internal void Bind(string cameraId, int frame, SceneShotSettings settings, CameraParallaxLayer[] layers)
    {
        if (_cameraId != cameraId || _frame != frame) _diagram.CancelGesture();
        _cameraId = cameraId;
        _frame = frame;
        _settings = settings;
        _layerState = layers;
        var layersChanged = !_rows.Select(row => row.Id).SequenceEqual(layers.Select(layer => layer.Id));
        _updating = true;
        SuspendLayout();
        try
        {
            _enabled.Checked = settings.Parallax.Enabled;
            _strength.Value = (decimal)settings.Parallax.Strength * 100;
            _cameraZ.Value = (decimal)settings.Z;
            _referenceZ.Value = (decimal)settings.Parallax.ReferenceZ;
            _keepSize.Checked = settings.Parallax.KeepSize;
            _horizontal.Value = (decimal)settings.Parallax.HorizontalResponse * 100;
            _vertical.Value = (decimal)settings.Parallax.VerticalResponse * 100;
            _dolly.Checked = settings.Parallax.DollyScaling;
            if (layersChanged)
            {
                foreach (var row in _rows) row.Container.Dispose();
                _rows.Clear();
                foreach (var layer in layers) AddLayerRow(layer);
            }
            for (var index = 0; index < layers.Length; index++)
            {
                var layer = layers[index];
                var row = _rows[index];
                row.Name.Text = layer.Name;
                row.Toggle.AccessibleName = layer.Name;
                row.Toggle.ForeColor = Theme.Text;
                row.Toggle.Checked = layer.Enabled;
                row.Depth.Value = (decimal)layer.Depth;
                row.Swatch.BackColor = Color.FromArgb(layer.ColorArgb);
            }
            _diagram.Bind(settings.Z, layers);
            UpdateMoveLabels();
        }
        finally
        {
            ResumeLayout(false);
            _updating = false;
        }
        if (layersChanged) PerformLayout();
    }

    private void AddLayerRow(CameraParallaxLayer layer)
    {
        var container = new Panel { BackColor = Theme.Panel };
        var swatch = new Panel();
        var toggle = new ParallaxCheckBox { Font = Theme.UiFont(), BackColor = Theme.Panel };
        var name = new Label { AutoEllipsis = true, Font = Theme.UiFont(), ForeColor = Theme.Text,
            TextAlign = ContentAlignment.MiddleLeft };
        var depth = Number("Layer depth Z");
        container.Controls.AddRange([swatch, toggle, name, depth]);
        _layers.Content.Controls.Add(container);
        var row = new LayerRow(layer.Id, container, swatch, toggle, name, depth);
        _rows.Add(row);
        container.Paint += (_, eventArgs) => InspectorModuleStyle.DrawDivider(container, eventArgs.Graphics, container.Height - 1);
        toggle.CheckedChanged += (_, _) => Discrete(() => ChangeDepth(row.Id, (float)depth.Value, toggle.Checked));
        WireNumeric(depth, () => ChangeDepth(row.Id, (float)depth.Value));
        toggle.GotFocus += (_, _) => _diagram.SelectLayer(row.Id);
        depth.Enter += (_, _) => _diagram.SelectLayer(row.Id);
        name.Click += (_, _) => _diagram.SelectLayer(row.Id);
    }

    private void AddField(ModernNumericUpDown editor, string name)
    {
        var label = InspectorModuleStyle.CreateFieldLabel(name);
        label.Dock = DockStyle.None;
        editor.Tag = label;
        Controls.AddRange([label, editor]);
        WireNumeric(editor, () => CommitSettings());
    }

    private void WireNumeric(ModernNumericUpDown editor, Action changed)
    {
        editor.Enter += (_, _) => { if (!_updating) EditStarted?.Invoke(this, EventArgs.Empty); };
        editor.InteractionStarted += (_, _) => { if (!_updating) EditStarted?.Invoke(this, EventArgs.Empty); };
        editor.ValueChanged += (_, _) => { if (!_updating) changed(); };
        editor.Leave += (_, _) => { if (!_updating) EditCompleted?.Invoke(this, EventArgs.Empty); };
        editor.InteractionCompleted += (_, _) => { if (!_updating) EditCompleted?.Invoke(this, EventArgs.Empty); };
        editor.InteractionCanceled += (_, _) => { if (!_updating) EditCanceled?.Invoke(this, EventArgs.Empty); };
    }

    private void Discrete(Action action)
    {
        if (_updating || !Enabled) return;
        EditStarted?.Invoke(this, EventArgs.Empty);
        action();
        EditCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void CommitSettings(bool captureAnchor = false)
    {
        if (_cameraId.Length == 0) return;
        var parallax = _settings.Parallax with
        {
            Enabled = _enabled.Checked, Strength = (float)_strength.Value / 100,
            ReferenceZ = (float)_referenceZ.Value, KeepSize = _keepSize.Checked,
            HorizontalResponse = (float)_horizontal.Value / 100,
            VerticalResponse = (float)_vertical.Value / 100, DollyScaling = _dolly.Checked,
            SizeAnchorZ = captureAnchor ? (float)_cameraZ.Value : _settings.Parallax.SizeAnchorZ
        };
        var cameraZ = (float)_cameraZ.Value;
        if (parallax.Enabled) cameraZ = Math.Max(-SceneShotSettings.MaximumPosition,
            Math.Min(cameraZ, MinimumDepth(parallax.ReferenceZ) - SceneShotParallaxSettings.NearDistance));
        if (captureAnchor) parallax = parallax with { SizeAnchorZ = cameraZ };
        SettingsChanged?.Invoke(this, new ShotDirectorFramingEventArgs(_cameraId,
            _settings with { Z = cameraZ, Parallax = parallax }));
    }

    private float MinimumDepth(float referenceZ) => _layerState.Where(layer => layer.Enabled)
        .Select(layer => layer.Depth).Append(referenceZ).Min();

    private void ChangeDepth(string layerId, float depth, bool? enabled = null)
    {
        if (_updating) return;
        if (layerId.Length == 0)
        {
            var maximum = _settings.Parallax.Enabled
                ? Math.Max(-SceneShotSettings.MaximumPosition,
                    MinimumDepth(_settings.Parallax.ReferenceZ) - SceneShotParallaxSettings.NearDistance)
                : SceneShotSettings.MaximumPosition;
            _cameraZ.Value = (decimal)Math.Clamp(depth, -SceneShotSettings.MaximumPosition,
                maximum);
            return;
        }
        if (_layerState.FirstOrDefault(layer => layer.Id == layerId) is not { } selected) return;
        var participates = enabled ?? selected.Enabled;
        var minimum = participates && _settings.Parallax.Enabled
            ? Math.Min(SceneShotSettings.MaximumPosition, _settings.Z + SceneShotParallaxSettings.NearDistance)
            : -SceneShotSettings.MaximumPosition;
        depth = Math.Clamp(depth, Math.Max(-SceneShotSettings.MaximumPosition, minimum), SceneShotSettings.MaximumPosition);
        LayerChanged?.Invoke(this, new CameraParallaxLayerEventArgs(layerId, depth, participates));
    }

    private void StepDepth(int direction)
    {
        var layerId = _diagram.SelectedLayerId;
        var depth = layerId.Length == 0 ? _settings.Z : _layerState.FirstOrDefault(layer => layer.Id == layerId)?.Depth ?? 0;
        var step = (ModifierKeys & Keys.Shift) != 0 ? 10 : 100;
        ChangeDepth(layerId, depth + direction * step * (layerId.Length == 0 ? -1 : 1));
    }

    private void UpdateMoveLabels()
    {
        _closer.Text = _diagram.SelectedLayerId.Length == 0 ? "Dolly in" : "Move closer";
        _farther.Text = _diagram.SelectedLayerId.Length == 0 ? "Dolly out" : "Move farther";
    }

    protected override void OnLayout(LayoutEventArgs eventArgs)
    {
        base.OnLayout(eventArgs);
        if (_arranging) return;
        _arranging = true;
        SuspendLayout();
        try
        {
            ArrangeControls();
        }
        finally
        {
            ResumeLayout(false);
            _arranging = false;
        }
        Invalidate(true);
    }

    private void ArrangeControls()
    {
        int Scale(int value) => Math.Max(1, (int)Math.Round(value * DeviceDpi / 96f));
        var width = ClientSize.Width;
        var rowHeight = Scale(Theme.ControlHeight);
        var enabledWidth = Math.Min(width / 2, Math.Max(Scale(72), TextRenderer.MeasureText(_enabled.Text, _enabled.Font).Width + Scale(28)));
        _disclosure.SetBounds(0, 0, Math.Max(1, width - enabledWidth), rowHeight);
        _enabled.SetBounds(Math.Max(0, width - enabledWidth), 0, enabledWidth, rowHeight);
        _disclosure.TabIndex = 0;
        _enabled.TabIndex = 1;
        foreach (Control control in Controls)
        {
            if (control == _disclosure || control == _enabled) continue;
            var advanced = control == _horizontal || ReferenceEquals(control, _horizontal.Tag)
                || control == _vertical || ReferenceEquals(control, _vertical.Tag) || control == _dolly;
            control.Visible = _expanded && (!advanced || _advancedExpanded)
                && (control != _layers || _rows.Count > 0)
                && (control != _emptyLayers || _rows.Count == 0);
        }
        var cursorY = Scale(42);
        var tabIndex = 2;
        void Field(ModernNumericUpDown editor)
        {
            if (editor.Tag is not Label label) return;
            var split = (int)Math.Floor(width * 0.48f);
            var labelHeight = Math.Min(rowHeight, label.Font.Height + Scale(4));
            label.SetBounds(0, cursorY + (rowHeight - labelHeight) / 2, Math.Max(1, split - Scale(8)), labelHeight);
            editor.SetBounds(split, cursorY, Math.Max(1, width - split), rowHeight);
            editor.TabIndex = tabIndex++;
            cursorY += Scale(40);
        }
        if (_expanded)
        {
            Field(_strength);
            Field(_cameraZ);
            Field(_referenceZ);
            _keepSize.SetBounds(0, cursorY, width, rowHeight);
            _keepSize.TabIndex = tabIndex++;
            cursorY += Scale(48);
            var directionWidth = Math.Min(width / 2, Scale(114));
            _layerTitle.SetBounds(0, cursorY, Math.Max(1, width - directionWidth - Scale(8)), Scale(28));
            _layerDirection.SetBounds(width - directionWidth, cursorY, directionWidth, Scale(28));
            cursorY += Scale(34);
            _layers.SetBounds(0, cursorY, width, Scale(Math.Clamp(_rows.Count, 1, 5) * 40));
            _layers.TabIndex = tabIndex++;
            _emptyLayers.SetBounds(0, cursorY, width, Scale(40));
            for (var index = 0; index < _rows.Count; index++)
            {
                var row = _rows[index];
                var available = Math.Max(1, _layers.Content.ClientSize.Width);
                var inputWidth = Math.Min(available / 2, Scale(104));
                var split = available - inputWidth;
                row.Container.SetBounds(0, index * Scale(40), available, Scale(40));
                row.Container.TabIndex = index;
                row.Swatch.SetBounds(Scale(24), Scale(14), Scale(8), Scale(8));
                row.Toggle.SetBounds(0, 0, Scale(20), rowHeight);
                row.Toggle.TabIndex = 0;
                row.Name.SetBounds(Scale(40), 0, Math.Max(1, split - Scale(48)), rowHeight);
                row.Depth.SetBounds(split, 0, inputWidth, rowHeight);
                row.Depth.TabIndex = 1;
            }
            _layers.Content.PerformLayout();
            cursorY += _layers.Height + Scale(12);
            _diagram.SetBounds(0, cursorY, width, Scale(width >= Scale(300) ? 240 : 200));
            _diagram.TabIndex = tabIndex++;
            cursorY += _diagram.Height + Scale(10);
            var half = Math.Max(1, (width - Scale(8)) / 2);
            _closer.SetBounds(0, cursorY, half, rowHeight);
            _farther.SetBounds(half + Scale(8), cursorY, Math.Max(1, width - half - Scale(8)), rowHeight);
            _closer.TabIndex = tabIndex++;
            _farther.TabIndex = tabIndex++;
            cursorY += Scale(38);
            _hint.SetBounds(0, cursorY, width, Scale(34));
            cursorY += Scale(48);
            _advanced.SetBounds(0, cursorY, width, rowHeight);
            _advanced.TabIndex = tabIndex++;
            cursorY += Scale(40);
            if (_advancedExpanded)
            {
                Field(_horizontal);
                Field(_vertical);
                _dolly.SetBounds(0, cursorY, width, rowHeight);
                _dolly.TabIndex = tabIndex++;
                cursorY += Scale(40);
            }
        }
        var height = _expanded ? cursorY : rowHeight;
        if (Height != height) Height = height;
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        if (_expanded) InspectorModuleStyle.DrawDivider(this, eventArgs.Graphics, _advanced.Top - (int)Math.Round(8 * DeviceDpi / 96f));
    }

    private static ModernNumericUpDown Number(string name, decimal? minimum = null, decimal? maximum = null, bool percent = false)
    {
        var editor = new ModernNumericUpDown
        {
            AccessibleName = name, Minimum = minimum ?? -5_000_000, Maximum = maximum ?? 5_000_000,
            DecimalPlaces = 1, Increment = 1, Suffix = percent ? "%" : "", ShowSuffix = percent
        };
        Theme.StyleNumeric(editor);
        editor.Font = Theme.UiFont(11f);
        return editor;
    }

    private sealed class ParallaxCheckBox : CheckBox
    {
        public ParallaxCheckBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AutoSize = false;
        }

        protected override void OnPaint(PaintEventArgs eventArgs)
        {
            eventArgs.Graphics.Clear(BackColor);
            var scale = DeviceDpi / 96f;
            var side = Math.Max(1, (int)Math.Round(16 * scale));
            var box = new Rectangle(0, Math.Max(0, (Height - side) / 2), side, side);
            var highContrast = SystemInformation.HighContrast;
            var surface = highContrast ? Checked ? SystemColors.Highlight : SystemColors.Window
                : Checked && Enabled ? Theme.Accent : Theme.PanelStrong;
            using var fill = new SolidBrush(surface);
            eventArgs.Graphics.FillRectangle(fill, box);
            using var border = new Pen(highContrast ? SystemColors.WindowText : Theme.ControlOutline);
            eventArgs.Graphics.DrawRectangle(border, box.X, box.Y, Math.Max(0, side - 1), Math.Max(0, side - 1));
            if (Checked)
            {
                using var tick = new Pen(highContrast ? SystemColors.HighlightText
                    : Enabled ? Theme.ReadableUiColor(surface, Theme.PanelStrong) : Theme.DisabledText, 2 * scale);
                eventArgs.Graphics.DrawLines(tick, [
                    new PointF(box.Left + 3 * scale, box.Top + 8 * scale),
                    new PointF(box.Left + 7 * scale, box.Top + 12 * scale),
                    new PointF(box.Left + 13 * scale, box.Top + 4 * scale)]);
            }
            var textLeft = side + (int)Math.Round(8 * scale);
            TextRenderer.DrawText(eventArgs.Graphics, Text, Font,
                new Rectangle(textLeft, 0, Math.Max(0, Width - textLeft), Height),
                highContrast ? Enabled ? SystemColors.ControlText : SystemColors.GrayText
                    : Enabled ? ForeColor : Theme.DisabledText,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (Focused && ShowFocusCues)
                ControlPaint.DrawFocusRectangle(eventArgs.Graphics, Rectangle.Inflate(ClientRectangle, -1, -1));
        }
    }

    private sealed record LayerRow(string Id, Panel Container, Panel Swatch, CheckBox Toggle, Label Name, ModernNumericUpDown Depth);
}
