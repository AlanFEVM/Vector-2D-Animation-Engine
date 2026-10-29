namespace VectorAnimationEngine;

internal enum ToolOptionKind
{
    KeepRatio, EraseStroke, EraseFill, Snap, Grid, Objects, Angle, AngleStep
}

internal sealed class ToolContextPanel : UserControl
{
    private readonly Label _title = new();
    private readonly Label _description = new() { AutoEllipsis = true, UseMnemonic = false };
    private readonly Label _unavailable = new() { AutoEllipsis = true, UseMnemonic = false };
    private readonly Dictionary<ToolOptionKind, (Label Caption, Control Editor)> _options = [];
    private readonly Button _related = new();
    private readonly Button _zoomIn = new() { Text = "+", AccessibleName = "Zoom In" };
    private readonly Button _zoomOut = new() { Text = "−", AccessibleName = "Zoom Out" };
    private readonly ToolTip _toolTip = new();
    private ToolMode _tool;
    private SvgIconKind _icon;
    private bool _available;
    private bool _updating;
    private bool _numericInteracting;
    private bool _ready;

    public ToolContextPanel()
    {
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Font = Theme.UiFont();
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AccessibleName = "Tool Settings";
        AccessibleRole = AccessibleRole.Pane;
        InspectorModuleStyle.StylePageTitle(_title);
        _description.Font = _unavailable.Font = Theme.UiFont();
        _description.ForeColor = _unavailable.ForeColor = Theme.PanelSecondaryText;
        _unavailable.Text = "This tool is not available in the current editing context.";
        Controls.AddRange([_title, _description, _unavailable, _related, _zoomIn, _zoomOut]);
        foreach (var (kind, caption) in new[]
        {
            (ToolOptionKind.KeepRatio, "Keep aspect ratio"),
            (ToolOptionKind.EraseStroke, "Erase stroke geometry"),
            (ToolOptionKind.EraseFill, "Erase fill geometry"),
            (ToolOptionKind.Snap, "Snapping"), (ToolOptionKind.Grid, "Snap to grid"),
            (ToolOptionKind.Objects, "Snap to objects"), (ToolOptionKind.Angle, "Angle snap"),
            (ToolOptionKind.AngleStep, "Angle step")
        })
        {
            var label = InspectorModuleStyle.CreateFieldLabel(caption);
            Control editor;
            if (kind == ToolOptionKind.AngleStep)
            {
                var numeric = new ModernNumericUpDown
                {
                    Minimum = 0.1m, Maximum = 90m, DecimalPlaces = 1, Increment = 1m,
                    Suffix = "°", ShowSuffix = true, AccessibleName = caption
                };
                InspectorModuleStyle.StyleField(numeric);
                numeric.InteractionStarted += (_, _) => _numericInteracting = true;
                numeric.InteractionCanceled += (_, _) => _numericInteracting = false;
                numeric.InteractionCompleted += (_, interaction) =>
                {
                    _numericInteracting = false;
                    if (interaction.Changed && !_updating) OptionChanged?.Invoke(kind, numeric.Value);
                };
                numeric.ValueChanged += (_, _) =>
                {
                    if (!_updating && !_numericInteracting) OptionChanged?.Invoke(kind, numeric.Value);
                };
                editor = numeric;
            }
            else
            {
                var toggle = new ModernToggleSwitch { AutoSize = false, AccessibleName = caption };
                toggle.CheckedChanged += (_, _) =>
                {
                    if (!_updating) OptionChanged?.Invoke(kind, toggle.Checked ? 1m : 0m);
                };
                editor = toggle;
            }
            _options.Add(kind, (label, editor));
            Controls.AddRange([label, editor]);
        }
        foreach (var button in new[] { _related, _zoomIn, _zoomOut }) Theme.StyleStandardButton(button);
        _related.Click += (_, _) => RelatedPageRequested?.Invoke(this, EventArgs.Empty);
        _zoomIn.Click += (_, _) => ZoomRequested?.Invoke(1.25f);
        _zoomOut.Click += (_, _) => ZoomRequested?.Invoke(0.8f);
        Theme.StyleToolTip(_toolTip);
        _ready = true;
    }

    public event Action<ToolOptionKind, decimal>? OptionChanged;
    public event EventHandler? RelatedPageRequested;
    public event Action<float>? ZoomRequested;

    internal static bool UsesDrawingOptions(ToolMode tool) => tool is ToolMode.Rectangle or ToolMode.Ellipse
        or ToolMode.Triangle or ToolMode.Polygon or ToolMode.Star or ToolMode.Line or ToolMode.Pen or ToolMode.SimplePen;

    internal static bool UsesObjectPage(ToolMode tool) => tool is ToolMode.Select or ToolMode.PolygonLasso
        or ToolMode.FreehandLasso or ToolMode.Transform or ToolMode.Transform3D or ToolMode.Distort;

    public void SetContext(ToolMode tool, string title, SvgIconKind icon, bool available, DrawSettings settings)
    {
        var layoutChanged = _tool != tool || _available != available || Height == 0;
        _tool = tool;
        _icon = icon;
        _available = available;
        var description = UiLocalization.T(Description(tool));
        if (_title.Text != title) _title.Text = title;
        if (_description.Text != description) _description.Text = description;
        _toolTip.SetToolTip(_description, description);
        _description.AccessibleDescription = description;
        _related.Text = UiLocalization.T(UsesObjectPage(tool) ? "Object Properties" : "Appearance");
        _related.AccessibleName = _related.Text;
        _updating = true;
        try
        {
            foreach (var (kind, row) in _options)
            {
                if (row.Editor is ModernToggleSwitch toggle)
                {
                    var value = kind switch
                    {
                        ToolOptionKind.KeepRatio => settings.KeepAspectRatio,
                        ToolOptionKind.EraseStroke => settings.EraseLines,
                        ToolOptionKind.EraseFill => settings.EraseFills,
                        ToolOptionKind.Snap => settings.SnapEnabled,
                        ToolOptionKind.Grid => settings.SnapToGrid,
                        ToolOptionKind.Objects => settings.SnapToObjects,
                        ToolOptionKind.Angle => settings.AngleSnapEnabled,
                        _ => false
                    };
                    if (toggle.Checked != value) toggle.Checked = value;
                }
                else if (!_numericInteracting && row.Editor is ModernNumericUpDown numeric)
                {
                    var value = (decimal)settings.AngleSnapDegrees;
                    if (numeric.Value != value) numeric.Value = value;
                }
                row.Editor.Enabled = available && (kind switch
                {
                    ToolOptionKind.Grid or ToolOptionKind.Objects or ToolOptionKind.Angle => settings.SnapEnabled,
                    ToolOptionKind.AngleStep => settings.SnapEnabled && settings.AngleSnapEnabled,
                    _ => true
                });
            }
        }
        finally { _updating = false; }
        if (layoutChanged) PerformLayout();
        Invalidate();
    }

    private bool Shows(ToolOptionKind kind) => kind switch
    {
        ToolOptionKind.KeepRatio => _tool is ToolMode.Rectangle or ToolMode.Ellipse or ToolMode.Triangle or ToolMode.Polygon or ToolMode.Star,
        ToolOptionKind.EraseStroke or ToolOptionKind.EraseFill => _tool == ToolMode.Eraser,
        ToolOptionKind.Angle or ToolOptionKind.AngleStep => _tool is ToolMode.Line or ToolMode.Pen or ToolMode.SimplePen,
        _ => UsesDrawingOptions(_tool)
    };

    protected override void OnLayout(LayoutEventArgs eventArgs)
    {
        base.OnLayout(eventArgs);
        if (!_ready) return;
        var inset = ScaleMetric(16);
        var width = Math.Max(1, Width - inset * 2);
        var split = (int)Math.Floor(width * 0.48f);
        _title.SetBounds(inset + ScaleMetric(24), ScaleMetric(16), Math.Max(1, width - ScaleMetric(24)), ScaleMetric(28));
        _description.SetBounds(inset, ScaleMetric(66), width, ScaleMetric(64));
        var top = 142;
        _unavailable.Visible = !_available;
        _unavailable.SetBounds(inset, ScaleMetric(top), width, ScaleMetric(52));
        if (!_available) top += 60;
        var tabIndex = 0;
        foreach (var (kind, row) in _options)
        {
            row.Caption.Dock = DockStyle.None;
            row.Caption.Visible = row.Editor.Visible = Shows(kind);
            row.Editor.TabIndex = tabIndex++;
            if (!Shows(kind)) continue;
            row.Caption.SetBounds(inset, ScaleMetric(top), Math.Max(1, split - ScaleMetric(8)), ScaleMetric(32));
            row.Editor.SetBounds(inset + split, ScaleMetric(top), Math.Max(1, width - split), ScaleMetric(32));
            top += 40;
        }
        _related.Visible = _tool is not (ToolMode.Hand or ToolMode.SnapPoint or ToolMode.Eraser);
        _related.Enabled = _available;
        _related.TabIndex = tabIndex++;
        _related.SetBounds(inset, ScaleMetric(top), width, ScaleMetric(32));
        _zoomIn.Visible = _zoomOut.Visible = _tool == ToolMode.Hand;
        _zoomIn.Enabled = _zoomOut.Enabled = _available;
        _zoomOut.TabIndex = tabIndex++;
        _zoomIn.TabIndex = tabIndex;
        _zoomOut.SetBounds(inset, ScaleMetric(top), Math.Max(1, (width - ScaleMetric(8)) / 2), ScaleMetric(32));
        _zoomIn.SetBounds(inset + (width + ScaleMetric(8)) / 2, ScaleMetric(top), Math.Max(1, (width - ScaleMetric(8)) / 2), ScaleMetric(32));
        var height = ScaleMetric(top + (_tool is ToolMode.SnapPoint or ToolMode.Eraser ? 0 : 40) + 12);
        if (Height != height) Height = height;
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        SvgIcons.Draw(eventArgs.Graphics, _icon, new Rectangle(ScaleMetric(16), ScaleMetric(21), ScaleMetric(18), ScaleMetric(18)), Theme.PanelSecondaryText);
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

    private static string Description(ToolMode tool) => tool switch
    {
        ToolMode.Select => "Select objects or geometry on the stage. Inspect the selection on the Object page.",
        ToolMode.PolygonLasso or ToolMode.FreehandLasso => "Enclose geometry to select it. Selection properties belong to the Object page.",
        ToolMode.Transform => "Drag selection handles to move, scale or rotate. This tool has no separate numeric defaults.",
        ToolMode.Transform3D => "Use the stage gizmo and spatial transform controls to edit selected scene instances.",
        ToolMode.Distort => "Drag the envelope handles to distort supported objects. This tool has no separate defaults.",
        ToolMode.Hand => "Drag to pan the view. Zoom changes the view only, not the document.",
        ToolMode.Rectangle or ToolMode.Ellipse or ToolMode.Triangle or ToolMode.Polygon or ToolMode.Star => "These options affect new shapes only. Fill, stroke and line width are in Appearance.",
        ToolMode.Line => "Drag to draw a line. Snapping uses the shared drawing settings; stroke is in Appearance.",
        ToolMode.Pen or ToolMode.SimplePen => "Place and drag path points on the stage. Snapping uses the shared drawing settings.",
        ToolMode.Pencil => "Draw freehand paths. Smoothing is below; stroke and line width are in Appearance.",
        ToolMode.Brush or ToolMode.PressureBrush or ToolMode.MixingBrush => "Brush tip and stroke settings are below. Color is managed in Appearance.",
        ToolMode.Text => "New text defaults are separate from selected text properties.",
        ToolMode.Fill => "Click an enclosed region to apply the current fill. Choose its material in Appearance.",
        ToolMode.InkBottle => "Click a stroke to apply the current stroke material and width from Appearance.",
        ToolMode.Eyedropper => "Click the stage to sample a material into Appearance. There are no separate sampling parameters.",
        ToolMode.Gradient => "Edit gradient handles on the stage. Gradient type, colors and stops are in Appearance.",
        ToolMode.SnapPoint => "Click to add an attachment point, drag to move it, or right-click to remove it in editable drawing content.",
        ToolMode.Eraser => "Choose stroke or fill targets here. Eraser size and tip settings are below.",
        _ => throw new ArgumentOutOfRangeException(nameof(tool))
    };
}
