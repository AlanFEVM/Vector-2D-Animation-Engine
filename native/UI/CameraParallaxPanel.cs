using System.Globalization;

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
    private readonly Label _heading = InspectorModuleStyle.CreateTextLabel();
    private readonly ShotDirectorPanel.CameraSectionToggle _disclosure = new("2D parallax", expanded: false);
    private readonly Label _enabledCaption = InspectorModuleStyle.CreateFieldLabel("Enabled");
    private readonly ModernToggleSwitch _enabled = new() { AccessibleName = "Enable parallax", AutoSize = false };
    private readonly ModernNumericUpDown _strength = Number("Parallax strength", 0, 200, true);
    private readonly ModernNumericUpDown _cameraZ = Number("Camera Z");
    private readonly ModernNumericUpDown _referenceZ = Number("Reference plane Z");
    private readonly Label _keepSizeCaption = InspectorModuleStyle.CreateFieldLabel("Keep size");
    private readonly ModernToggleSwitch _keepSize = new() { AccessibleName = "Keep size", AutoSize = false };
    private readonly Label _layerTitle = new() { Text = "Layer depth", AutoEllipsis = true };
    private readonly Label _layerDirection = new() { Text = "Near − / Far +", AutoEllipsis = true, TextAlign = ContentAlignment.MiddleRight };
    private readonly Label _layerScope = new() { Text = "Layer depths are shared by every camera in this scene." };
    private readonly Label _constraintHint = new();
    private readonly ToolTip _help = new();
    private readonly Label _emptyLayers = new() { Text = "No layers yet", AutoEllipsis = true, TextAlign = ContentAlignment.MiddleCenter };
    private readonly Panel _layers = new() { TabStop = false };
    private readonly CameraDepthDiagram _diagram = new();
    private readonly Button _closer = new() { Text = "Move closer" };
    private readonly Button _farther = new() { Text = "Move farther" };
    private readonly Label _responseHeading = InspectorModuleStyle.CreateTextLabel();
    private readonly ShotDirectorPanel.CameraSectionToggle _advanced = new("Parallax response", expanded: false);
    private readonly ModernNumericUpDown _horizontal = Number("Horizontal response", 0, 200, true);
    private readonly ModernNumericUpDown _vertical = Number("Vertical response", 0, 200, true);
    private readonly Label _dollyCaption = InspectorModuleStyle.CreateFieldLabel("Dolly scaling");
    private readonly ModernToggleSwitch _dolly = new() { AccessibleName = "Dolly depth scaling", AutoSize = false };
    private readonly Label _hint = new() { Text = "Drag: change depth · Shift: precise · Esc: cancel. Click overlapping lines again to cycle; Left/Right selects a layer." };
    private readonly List<LayerRow> _rows = [];
    private SceneShotSettings _settings = SceneShotSettings.Default;
    private CameraParallaxLayer[] _layerState = [];
    private bool _updating;
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
        Controls.AddRange([_heading, _disclosure, _enabledCaption, _enabled, _keepSizeCaption, _keepSize, _layerTitle, _layerDirection, _layerScope, _constraintHint, _emptyLayers, _layers, _diagram,
            _closer, _farther, _responseHeading, _advanced, _dollyCaption, _dolly, _hint]);
        Theme.StyleToolTip(_help);
        _constraintHint.Text = NormalDepthHint();
        var keepSizeHelp = UiLocalization.T("Preserve layer size at the current camera depth. Dolly scaling can still change size as the camera moves.");
        _help.SetToolTip(_keepSize, keepSizeHelp);
        _help.SetToolTip(_keepSizeCaption, keepSizeHelp);
        _help.SetToolTip(_layerTitle, UiLocalization.T("Layer depths are shared by every camera in this scene."));
        foreach (var control in new Control[] { _disclosure, _advanced, _closer, _farther })
            Theme.StyleButton((Button)control);
        _heading.Text = "2D parallax";
        _heading.Font = Theme.UiFont(Theme.InspectorSectionFontSize);
        _responseHeading.Text = "Parallax response";
        _responseHeading.Font = _heading.Font;
        _responseHeading.Cursor = Cursors.Hand;
        _responseHeading.Click += (_, _) =>
        {
            _advanced.Focus();
            _advanced.PerformClick();
        };
        _heading.Cursor = Cursors.Hand;
        _heading.Click += (_, _) =>
        {
            _disclosure.Focus();
            _disclosure.PerformClick();
        };
        _disclosure.Click += (_, _) =>
        {
            _diagram.CancelGesture();
            PerformLayout();
        };
        _enabledCaption.Dock = DockStyle.None;
        _keepSizeCaption.Dock = DockStyle.None;
        _dollyCaption.Dock = DockStyle.None;
        foreach (var control in new Control[] { _enabled, _keepSize, _dolly, _layerTitle, _layerDirection, _layerScope, _constraintHint, _emptyLayers, _hint })
        {
            control.Font = Theme.UiFont();
            control.ForeColor = control == _constraintHint ? Theme.PanelSecondaryText
                : control == _hint || control == _layerTitle || control == _layerDirection || control == _layerScope || control == _emptyLayers ? Theme.PanelSecondaryText : Theme.Text;
            control.BackColor = Theme.Panel;
        }
        AddField(_strength, "Parallax strength");
        AddField(_cameraZ, "Camera Z");
        AddField(_referenceZ, "Reference plane Z");
        AddField(_horizontal, "Horizontal response");
        AddField(_vertical, "Vertical response");
        _advanced.Click += (_, _) => PerformLayout();
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
        if (_cameraId != cameraId || _frame != frame)
        {
            _diagram.CancelGesture();
            _constraintHint.Text = NormalDepthHint();
            _constraintHint.ForeColor = Theme.PanelSecondaryText;
        }
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
                _help.SetToolTip(row.Name, layer.Name);
                _help.SetToolTip(row.Toggle, layer.Name);
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
        depth.MouseWheelChangesValue = false;
        container.Controls.AddRange([swatch, toggle, name, depth]);
        _layers.Controls.Add(container);
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
        var requestedCameraZ = (float)_cameraZ.Value;
        var cameraZ = requestedCameraZ;
        if (parallax.Enabled) cameraZ = Math.Max(-SceneShotSettings.MaximumPosition,
            Math.Min(cameraZ, MinimumDepth(parallax.ReferenceZ) - SceneShotParallaxSettings.NearDistance));
        if (captureAnchor) parallax = parallax with { SizeAnchorZ = cameraZ };
        SettingsChanged?.Invoke(this, new ShotDirectorFramingEventArgs(_cameraId,
            _settings with { Z = cameraZ, Parallax = parallax }));
        ShowDepthConstraint(requestedCameraZ, cameraZ);
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
            ShowDepthConstraint(depth, (float)_cameraZ.Value);
            return;
        }
        if (_layerState.FirstOrDefault(layer => layer.Id == layerId) is not { } selected) return;
        var participates = enabled ?? selected.Enabled;
        var minimum = participates && _settings.Parallax.Enabled
            ? Math.Min(SceneShotSettings.MaximumPosition, _settings.Z + SceneShotParallaxSettings.NearDistance)
            : -SceneShotSettings.MaximumPosition;
        var requested = depth;
        depth = Math.Clamp(depth, Math.Max(-SceneShotSettings.MaximumPosition, minimum), SceneShotSettings.MaximumPosition);
        LayerChanged?.Invoke(this, new CameraParallaxLayerEventArgs(layerId, depth, participates));
        ShowDepthConstraint(requested, depth);
    }

    private void ShowDepthConstraint(float requested, float actual)
    {
        var limited = Math.Abs(requested - actual) > 0.001f;
        var text = limited
            ? string.Format(CultureInfo.CurrentCulture,
                UiLocalization.T("Depth limited to {0:0.#}. Keep at least {1:0.#} units between the camera and enabled layers or the reference plane."),
                actual, SceneShotParallaxSettings.NearDistance) : NormalDepthHint();
        if (_constraintHint.Text == text) return;
        _constraintHint.Text = text;
        _constraintHint.ForeColor = limited ? Theme.Danger : Theme.PanelSecondaryText;
        _help.SetToolTip(_constraintHint, text);
    }

    private static string NormalDepthHint() => string.Format(CultureInfo.CurrentCulture,
        UiLocalization.T("When parallax is enabled, keep at least {0:0.#} units between the camera and layers or the reference plane."),
        SceneShotParallaxSettings.NearDistance);

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
        _heading.SetBounds(0, 0, Math.Max(1, width - Scale(36)), rowHeight);
        _disclosure.SetBounds(Math.Max(0, width - Scale(28)), 0, Scale(28), Scale(28));
        _disclosure.TabIndex = 0;
        void Toggle(ModernToggleSwitch editor, Label label, int top)
        {
            var split = (int)Math.Floor(width * 0.48f);
            var labelHeight = Math.Min(rowHeight, label.Font.Height + Scale(4));
            label.SetBounds(0, top + (rowHeight - labelHeight) / 2, Math.Max(1, split - Scale(8)), labelHeight);
            editor.SetBounds(split, top, Math.Max(1, width - split), rowHeight);
        }
        Toggle(_enabled, _enabledCaption, Scale(40));
        _enabled.TabIndex = 1;
        foreach (Control control in Controls)
        {
            if (control == _heading || control == _disclosure) continue;
            var advanced = control == _horizontal || ReferenceEquals(control, _horizontal.Tag)
                || control == _vertical || ReferenceEquals(control, _vertical.Tag) || control == _dolly || control == _dollyCaption;
            control.Visible = _disclosure.Expanded && (!advanced || _advanced.Expanded)
                && (control != _layers || _rows.Count > 0)
                && (control != _emptyLayers || _rows.Count == 0);
        }
        if (!_disclosure.Expanded)
        {
            if (Height != rowHeight) Height = rowHeight;
            return;
        }
        var cursorY = Scale(80);
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
        Field(_strength);
        Field(_cameraZ);
        Field(_referenceZ);
        Toggle(_keepSize, _keepSizeCaption, cursorY);
        _keepSize.TabIndex = tabIndex++;
        cursorY += Scale(48);
        _constraintHint.SetBounds(0, cursorY, width, Scale(64));
        cursorY += Scale(72);
        _responseHeading.SetBounds(0, cursorY, Math.Max(1, width - Scale(36)), Scale(28));
        _advanced.SetBounds(Math.Max(0, width - Scale(28)), cursorY, Scale(28), Scale(28));
        _advanced.TabIndex = tabIndex++;
        cursorY += Scale(36);
        if (_advanced.Expanded)
        {
            Field(_horizontal);
            Field(_vertical);
            Toggle(_dolly, _dollyCaption, cursorY);
            _dolly.TabIndex = tabIndex++;
            cursorY += Scale(40);
        }
        cursorY += Scale(8);
        var directionWidth = Math.Min(width / 2, Scale(114));
        _layerTitle.SetBounds(0, cursorY, Math.Max(1, width - directionWidth - Scale(8)), Scale(28));
        _layerDirection.SetBounds(width - directionWidth, cursorY, directionWidth, Scale(28));
        cursorY += Scale(34);
        _layerScope.SetBounds(0, cursorY, width, Scale(40));
        cursorY += Scale(48);
        _layers.SetBounds(0, cursorY, width, Scale(Math.Max(1, _rows.Count) * 40));
        _layers.TabIndex = tabIndex++;
        _emptyLayers.SetBounds(0, cursorY, width, Scale(40));
        for (var index = 0; index < _rows.Count; index++)
        {
            var row = _rows[index];
            var available = Math.Max(1, _layers.ClientSize.Width);
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
        _layers.PerformLayout();
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
        _hint.SetBounds(0, cursorY, width, Scale(64));
        cursorY += Scale(72);
        if (Height != cursorY) Height = cursorY;
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        if (_disclosure.Expanded)
            InspectorModuleStyle.DrawDivider(this, eventArgs.Graphics, _responseHeading.Top - (int)Math.Round(8 * DeviceDpi / 96f));
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

    protected override void Dispose(bool disposing)
    {
        if (disposing) _help.Dispose();
        base.Dispose(disposing);
    }
}
