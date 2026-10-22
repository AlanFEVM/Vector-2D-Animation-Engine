namespace VectorAnimationEngine;

internal readonly record struct DocumentInfo(
    string Name,
    string? Location,
    int SceneCount,
    int SymbolCount,
    int SvgAssetCount,
    int ImageAssetCount,
    int TagCount,
    string SceneSummary,
    int FrameCount,
    decimal FrameRate,
    int ObjectCount,
    string? CanvasSummary = null,
    string? BackgroundSummary = null);

/// <summary>
/// Read-only document summary shown in the inspector while nothing is selected.
/// The host supplies every value; this control never inspects the project itself.
/// </summary>
internal sealed class DocumentInfoPanel : UserControl
{
    private const int LabelColumnWidth = 80;
    private const int HintHeight = 34;
    private const int RowHeight = Theme.InspectorMetaRowHeight;
    private const int VisibleRowCount = 13;

    private readonly Label _name = CreateValueLabel();
    private readonly Label _location = CreateValueLabel();
    private readonly Label _scenes = CreateValueLabel();
    private readonly Label _symbols = CreateValueLabel();
    private readonly Label _assets = CreateValueLabel();
    private readonly Label _tags = CreateValueLabel();
    private readonly Label _scene = CreateValueLabel();
    private readonly Label _frames = CreateValueLabel();
    private readonly Label _frameRate = CreateValueLabel();
    private readonly Label _objects = CreateValueLabel();
    private readonly Label _canvas = CreateValueLabel();
    private readonly Label _background = CreateValueLabel();
    private readonly Label _hint = new();
    private readonly TextBox _nameEditor = new() { Visible = false, BorderStyle = BorderStyle.FixedSingle };
    private bool _hintVisible;
    private bool _updating;

    public DocumentInfoPanel()
    {
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Font = Theme.UiFont();
        Padding = new Padding(0, Theme.GapXs, 0, Theme.InspectorSectionPaddingVertical);
        AccessibleRole = AccessibleRole.Pane;
        AccessibleName = "Document information";

        var title = InspectorModuleStyle.CreateTitle("Document");
        title.Dock = DockStyle.Top;

        var rows = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 2,
            RowCount = VisibleRowCount + 1,
            Margin = Padding.Empty,
            Padding = new Padding(0, Theme.InspectorContentPaddingTop, 0, 0)
        };
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LabelColumnWidth));
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < VisibleRowCount; row++)
        {
            rows.RowStyles.Add(new RowStyle(SizeType.Absolute, RowHeight));
        }

        rows.RowStyles.Add(new RowStyle(SizeType.Absolute, HintHeight));
        rows.RowStyles[12].Height = Theme.ControlHeight;

        AddRow(rows, 0, "Name", _name);
        AddRow(rows, 1, "Location", _location);
        AddRow(rows, 2, "Scenes", _scenes);
        AddRow(rows, 3, "Symbols", _symbols);
        AddRow(rows, 4, "Assets", _assets);
        AddRow(rows, 5, "Tags", _tags);
        AddRow(rows, 6, "Scene", _scene);
        AddRow(rows, 7, "Frames", _frames);
        AddRow(rows, 8, "Frame rate", _frameRate);
        AddRow(rows, 9, "Objects", _objects);
        AddRow(rows, 10, "Canvas", _canvas);
        AddRow(rows, 11, "Background", _background);
        var canvasSettings = new Button
        {
            Text = "Canvas & background…", AccessibleName = "Canvas & background…",
            Dock = DockStyle.Fill, AutoEllipsis = true, UseMnemonic = false, Margin = Padding.Empty
        };
        Theme.StyleStandardButton(canvasSettings);
        canvasSettings.Click += (_, _) => CanvasSettingsRequested?.Invoke(this, EventArgs.Empty);
        rows.Controls.Add(canvasSettings, 0, 12);
        rows.SetColumnSpan(canvasSettings, 2);

        _hint.Dock = DockStyle.Fill;
        _hint.BackColor = Theme.Panel;
        _hint.ForeColor = Theme.Muted;
        _hint.Font = Theme.UiFont(8.8f);
        _hint.TextAlign = ContentAlignment.TopLeft;
        _hint.Margin = Padding.Empty;
        _hint.Visible = false;
        rows.Controls.Add(_hint, 0, VisibleRowCount);
        rows.SetColumnSpan(_hint, 2);

        StyleValue(_name);
        _name.Cursor = Cursors.Hand;
        _name.Click += (_, _) => BeginNameEdit();
        Theme.StyleTextBox(_nameEditor);
        _nameEditor.Visible = false;
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
                CancelNameEdit();
            }
        };
        _nameEditor.Leave += (_, _) => CommitNameEdit();
        rows.Controls.Add(_nameEditor, 1, 0);

        Controls.Add(title);
        Controls.Add(rows);
        rows.BringToFront();
        Height = PreferredHeight;
        SetDocument(default);
    }

    /// <summary>Raised with the trimmed name when the user commits an inline rename.</summary>
    public event EventHandler<string>? RenameCommitted;
    public event EventHandler? CanvasSettingsRequested;

    public int PreferredHeight => Theme.GapXs
        + Theme.InspectorTitleHeight
        + Theme.InspectorContentPaddingTop
        + RowHeight * (VisibleRowCount - 1) + Theme.ControlHeight
        + (_hintVisible ? HintHeight : 0)
        + Theme.InspectorSectionPaddingVertical;

    public void SetDocument(DocumentInfo info)
    {
        _updating = true;
        try
        {
            _name.Text = string.IsNullOrWhiteSpace(info.Name) ? UiLocalization.T("Untitled Project") : info.Name;
            _location.Text = string.IsNullOrWhiteSpace(info.Location)
                ? UiLocalization.T("Unsaved")
                : info.Location;
            _location.ForeColor = string.IsNullOrWhiteSpace(info.Location) ? Theme.Muted : Theme.Text;
            _scenes.Text = CompactFormat.Number(info.SceneCount);
            _symbols.Text = CompactFormat.Number(info.SymbolCount);
            _assets.Text = $"{CompactFormat.Number(info.SvgAssetCount)} SVG · "
                + $"{CompactFormat.Number(info.ImageAssetCount)} {UiLocalization.T("Images")}";
            _tags.Text = CompactFormat.Number(info.TagCount);
            _scene.Text = string.IsNullOrWhiteSpace(info.SceneSummary) ? "-" : info.SceneSummary;
            _frames.Text = CompactFormat.Number(info.FrameCount);
            _frameRate.Text = $"{info.FrameRate:0.##} fps";
            _objects.Text = CompactFormat.Number(info.ObjectCount);
            _canvas.Text = info.CanvasSummary ?? UiLocalization.T("Not added");
            _background.Text = info.BackgroundSummary ?? UiLocalization.T("Not added");
            _hint.Text = info.ObjectCount == 0
                ? UiLocalization.T("Nothing here yet. Drag a symbol from the library onto the stage.")
                : string.Empty;
            _hintVisible = info.ObjectCount == 0;
            _hint.Visible = _hintVisible;
            var height = PreferredHeight;
            if (Height != height) Height = height;
        }
        finally
        {
            _updating = false;
        }
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
        if (string.IsNullOrWhiteSpace(name) || string.Equals(name, _name.Text, StringComparison.Ordinal)) return;
        RenameCommitted?.Invoke(this, name);
    }

    private void CancelNameEdit() => _nameEditor.Visible = false;

    private static void AddRow(TableLayoutPanel rows, int row, string label, Control value)
    {
        var caption = new Label
        {
            Dock = DockStyle.Fill,
            Text = label,
            ForeColor = Theme.Muted,
            BackColor = Theme.Panel,
            Font = Theme.UiFont(8.8f),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Margin = Padding.Empty
        };
        rows.Controls.Add(caption, 0, row);
        rows.Controls.Add(value, 1, row);
    }

    private static Label CreateValueLabel() => new()
    {
        Dock = DockStyle.Fill,
        ForeColor = Theme.Text,
        BackColor = Theme.Panel,
        Font = Theme.UiFont(),
        TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true,
        Margin = Padding.Empty
    };

    private static void StyleValue(Label label) => label.ForeColor = Theme.Text;
}
