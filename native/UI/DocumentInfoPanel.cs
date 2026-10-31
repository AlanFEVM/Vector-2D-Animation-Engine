namespace VectorAnimationEngine;

internal readonly record struct DocumentInfo(
    string Name, string? Location, int SceneCount, int SymbolCount,
    int SvgAssetCount, int ImageAssetCount, int TagCount, string SceneSummary,
    int FrameCount, decimal FrameRate, int ObjectCount,
    string? CanvasSummary = null, string? BackgroundSummary = null,
    bool IsDirty = false, bool HasCanvas = false);

internal sealed partial class DocumentInfoPanel : UserControl
{
    private readonly Label _title = Caption("Document & layers", false);
    private readonly Label _projectCaption = Caption("Current project");
    private readonly Label _saveState = Caption("");
    private readonly Label _name = Caption("", false);
    private readonly Label _location = Caption("");
    private readonly Label _scene = Caption("", false);
    private readonly Label _layerTitle = Caption("Current layer", false);
    private readonly Label _canvasTitle = Caption("Canvas & background", false);
    private readonly Label _canvasStatus = Caption("");
    private readonly Label _canvasPrompt = Caption("", false);
    private readonly Label _canvas = Caption("Canvas");
    private readonly Label _canvasValue = Caption("", false);
    private readonly Label _background = Caption("Background");
    private readonly Label _backgroundValue = Caption("", false);
    private readonly Button _canvasSettings = new SvgIconButton(SvgIconKind.Settings);
    private readonly SvgIconButton _overview = new(SvgIconKind.ChevronDown) { IsDisclosureHeader = true };
    private readonly Label[] _statCaptions =
        [Caption("Scenes"), Caption("Symbols"), Caption("Objects"), Caption("Frames"), Caption("Frame rate"), Caption("Tags")];
    private readonly Label[] _stats = Enumerable.Range(0, 6).Select(_ => Caption("", false)).ToArray();
    private readonly Label _assets = Caption("");
    private readonly Label _assetsTitle = Caption("Assets", false);
    private readonly Label _hint = Caption("");
    private readonly TextBox _nameEditor = new() { Visible = false };
    private readonly ToolTip _toolTip = new();
    private bool _overviewExpanded = true;
    private bool _hintVisible;
    private bool _updating;
    private int _projectBottom;
    private int _layerBottom;
    private int _canvasBottom;

    internal LayerBlendModePanel LayerPanel { get; } = new();
    public event EventHandler<string>? RenameCommitted;
    public event EventHandler? CanvasSettingsRequested;
    public int PreferredHeight => ScaleLogical((_overviewExpanded ? 820 : 646) + PlaybackViewHeight
        + (int)Math.Ceiling(LayerPanel.PreferredPanelHeight * 96f / DeviceDpi) - 98
        + (_hintVisible ? 52 : 0));

    public DocumentInfoPanel()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Font = Theme.UiFont();
        AccessibleName = "Document & layers";
        AccessibleRole = AccessibleRole.Pane;
        InspectorModuleStyle.StylePageTitle(_title);
        _name.Font = Theme.UiFont(15f);
        _name.Cursor = Cursors.Hand;
        _name.Click += (_, _) => BeginNameEdit();
        LayerPanel.MinimumSize = new Size(0, LayerPanel.PreferredPanelHeight);
        LayerPanel.ShowDivider = false;
        _hint.AutoEllipsis = false;
        _hint.TextAlign = ContentAlignment.TopLeft;
        Theme.StyleToolTip(_toolTip);
        Theme.StyleTextBox(_nameEditor);
        _nameEditor.Visible = false;
        _nameEditor.AccessibleName = "Project name";
        _nameEditor.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                CommitNameEdit();
            }
            else if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                _nameEditor.Visible = false;
            }
        };
        _nameEditor.Leave += (_, _) => CommitNameEdit();
        _canvasSettings.Text = "Set canvas & background";
        _canvasSettings.AccessibleName = "Set canvas & background";
        Theme.StyleStandardButton(_canvasSettings, active: true);
        _canvasSettings.Click += (_, _) => CanvasSettingsRequested?.Invoke(this, EventArgs.Empty);
        Theme.StyleStandardButton(_overview);
        _overview.Text = "Project overview";
        _overview.AccessibleName = "Project overview";
        _overview.TextAlign = ContentAlignment.MiddleLeft;
        _overview.Click += (_, _) =>
        {
            _overviewExpanded = !_overviewExpanded;
            UpdateOverview();
        };
        Controls.AddRange([_title, _projectCaption, _saveState, _name, _location, _scene,
            _layerTitle, LayerPanel, _canvasTitle, _canvasStatus, _canvasPrompt, _canvas,
            _background, _canvasValue, _backgroundValue, _canvasSettings, _overview, _assetsTitle, _assets, _hint, _nameEditor]);
        Controls.AddRange(_statCaptions);
        Controls.AddRange(_stats);
        foreach (var stat in _stats) stat.Font = Theme.UiFont(12f);
        BuildPlaybackViewControls();
        UiLocalization.Watch(this);
        UpdateOverview();
    }

    public void SetDocument(DocumentInfo info)
    {
        _updating = true;
        try
        {
            _name.Text = string.IsNullOrWhiteSpace(info.Name) ? UiLocalization.T("Untitled Project") : info.Name;
            _location.Text = string.IsNullOrWhiteSpace(info.Location)
                ? UiLocalization.T("No save location selected") : info.Location;
            _toolTip.SetToolTip(_name, _name.Text);
            _toolTip.SetToolTip(_location, _location.Text);
            _saveState.Text = UiLocalization.T(string.IsNullOrWhiteSpace(info.Location)
                ? "Unsaved" : info.IsDirty ? "Unsaved changes" : "Saved");
            _saveState.ForeColor = info.IsDirty || string.IsNullOrWhiteSpace(info.Location) ? Theme.Warning : Theme.PanelSecondaryText;
            _saveState.TextAlign = ContentAlignment.MiddleRight;
            _scene.Text = info.SceneSummary;
            _toolTip.SetToolTip(_scene, info.SceneSummary);
            _canvasStatus.Text = UiLocalization.T(info.HasCanvas ? "Configured" : "Not configured");
            _canvasStatus.TextAlign = ContentAlignment.MiddleRight;
            _canvasPrompt.Text = UiLocalization.T(info.HasCanvas ? "Your canvas is ready" : "Give your work a canvas");
            _canvasValue.Text = info.CanvasSummary ?? UiLocalization.T("Not added");
            _backgroundValue.Text = info.BackgroundSummary ?? UiLocalization.T("Not added");
            _toolTip.SetToolTip(_backgroundValue, _backgroundValue.Text);
            string[] values = [CompactFormat.Number(info.SceneCount), CompactFormat.Number(info.SymbolCount),
                CompactFormat.Number(info.ObjectCount), CompactFormat.Number(info.FrameCount),
                $"{info.FrameRate:0.##} fps", CompactFormat.Number(info.TagCount)];
            for (var index = 0; index < values.Length; index++) _stats[index].Text = values[index];
            _assets.Text = $"{CompactFormat.Number(info.SvgAssetCount)} SVG · {CompactFormat.Number(info.ImageAssetCount)} {UiLocalization.T("Images")}";
            _assets.TextAlign = ContentAlignment.MiddleRight;
            _hintVisible = info.ObjectCount == 0;
            _hint.Text = UiLocalization.T("Nothing here yet. Drag a symbol from the library onto the stage.");
            _hint.Visible = _hintVisible;
            if (Height != PreferredHeight) Height = PreferredHeight;
            PerformLayout();
        }
        finally { _updating = false; }
    }

    private void UpdateOverview()
    {
        foreach (var control in _statCaptions.Concat(_stats)) control.Visible = _overviewExpanded;
        _overview.Icon = _overviewExpanded ? SvgIconKind.ChevronDown : SvgIconKind.ChevronRight;
        _assets.Visible = _overviewExpanded;
        _assetsTitle.Visible = _overviewExpanded;
        _overview.AccessibleDescription = UiLocalization.T(_overviewExpanded ? "Expanded" : "Collapsed");
        Height = PreferredHeight;
        PerformLayout();
        Invalidate();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        var inset = ScaleLogical(18);
        var width = Math.Max(0, ClientSize.Width - inset * 2);
        void Place(Control control, int top, int height, int left = 0, int? controlWidth = null) =>
            control.SetBounds(inset + ScaleLogical(left), ScaleLogical(top), controlWidth ?? width, ScaleLogical(height));
        Place(_title, 16, 28, 24, Math.Max(1, width - ScaleLogical(24)));
        Place(_projectCaption, 68, 24, controlWidth: width / 2);
        _saveState.SetBounds(inset + width / 2, ScaleLogical(68), width - width / 2, ScaleLogical(24));
        Place(_name, 96, 36);
        _nameEditor.Bounds = _name.Bounds;
        Place(_location, 138, 24);
        Place(_scene, 176, 30);
        _projectBottom = ScaleLogical(220);
        Place(_layerTitle, 238, 26);
        LayerPanel.SetBounds(inset - ScaleLogical(4), ScaleLogical(274), width + ScaleLogical(14), LayerPanel.PreferredPanelHeight);
        _layerBottom = LayerPanel.Bottom + ScaleLogical(4);
        var offset = (int)Math.Ceiling(_layerBottom * 96f / DeviceDpi) - 376;
        Place(_canvasTitle, 394 + offset, 26, controlWidth: width * 3 / 5);
        _canvasStatus.SetBounds(inset + width * 3 / 5, ScaleLogical(394 + offset), width - width * 3 / 5, ScaleLogical(26));
        Place(_canvasPrompt, 430 + offset, 28);
        var captionWidth = Math.Min(ScaleLogical(76), width / 2);
        Place(_canvas, 466 + offset, 26, controlWidth: captionWidth);
        _canvasValue.SetBounds(inset + captionWidth, _canvas.Top, width - captionWidth, _canvas.Height);
        Place(_background, 496 + offset, 26, controlWidth: captionWidth);
        _backgroundValue.SetBounds(inset + captionWidth, _background.Top, width - captionWidth, _background.Height);
        Place(_canvasSettings, 536 + offset, Theme.ControlHeight);
        _canvasBottom = ScaleLogical(586 + offset);
        LayoutPlaybackViewControls(inset, width, 600 + offset);
        offset += PlaybackViewHeight;
        Place(_overview, 600 + offset, Theme.ControlHeight);
        for (var index = 0; index < _stats.Length; index++)
        {
            var column = index % 3;
            var top = 650 + offset + index / 3 * 62;
            var left = inset + width * column / 3;
            var cellWidth = width * (column + 1) / 3 - width * column / 3;
            _statCaptions[index].SetBounds(left, ScaleLogical(top), cellWidth, ScaleLogical(24));
            _stats[index].SetBounds(left, ScaleLogical(top + 24), cellWidth, ScaleLogical(30));
        }
        Place(_assetsTitle, 778 + offset, 26, controlWidth: captionWidth);
        _assets.SetBounds(inset + captionWidth, _assetsTitle.Top, width - captionWidth, _assetsTitle.Height);
        Place(_hint, (_overviewExpanded ? 824 : 650) + offset, 44);
        var desired = ScaleLogical((_overviewExpanded ? 820 : 646) + offset + (_hintVisible ? 52 : 0));
        if (Height != desired) Height = desired;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        SvgIcons.Draw(e.Graphics, SvgIconKind.InspectorProperties,
            new Rectangle(ScaleLogical(18), ScaleLogical(21), ScaleLogical(18), ScaleLogical(18)), Theme.PanelSecondaryText);
        using var pen = new Pen(Theme.ControlOutline);
        foreach (var top in new[] { ScaleLogical(54), _projectBottom, _layerBottom, _canvasBottom })
            e.Graphics.DrawLine(pen, 0, top, Width, top);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _toolTip.Dispose();
        base.Dispose(disposing);
    }

    private void BeginNameEdit()
    {
        if (_updating) return;
        _nameEditor.Text = _name.Text;
        _nameEditor.Visible = true;
        _nameEditor.BringToFront();
        _nameEditor.Focus();
        _nameEditor.SelectAll();
    }

    private void CommitNameEdit()
    {
        if (!_nameEditor.Visible) return;
        var name = _nameEditor.Text.Trim();
        _nameEditor.Visible = false;
        if (name.Length > 0 && name != _name.Text) RenameCommitted?.Invoke(this, name);
    }

    private int ScaleLogical(int value) => (int)Math.Round(value * DeviceDpi / 96f);

    private static Label Caption(string text, bool muted = true)
    {
        var label = InspectorModuleStyle.CreateTextLabel();
        label.Text = text;
        label.ForeColor = muted ? Theme.PanelSecondaryText : Theme.Text;
        label.BackColor = Color.Transparent;
        label.Margin = Padding.Empty;
        return label;
    }
}
