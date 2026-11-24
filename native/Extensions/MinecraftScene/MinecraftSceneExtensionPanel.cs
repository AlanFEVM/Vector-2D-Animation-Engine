namespace VectorAnimationEngine;

internal sealed class MinecraftSceneExtensionPanel : UserControl
{
    private readonly Label _title = new() { Text = "Minecraft Scene", UseMnemonic = false };
    private readonly ThemedScrollPanel _scroll = new();
    private readonly ModernTextInput _source = new() { ReadOnly = true, AccessibleName = "Section JSON" };
    private readonly ModernTextInput _textures = new() { ReadOnly = true, AccessibleName = "Texture folder" };
    private readonly ModernTextInput _mapping = new() { ReadOnly = true, AccessibleName = "Texture map (optional)" };
    private readonly ModernNumericUpDown _size = new()
    {
        Minimum = 1, Maximum = 512, Value = MinecraftSectionImport.DefaultCellSize, Suffix = "px", ShowSuffix = true,
        MouseWheelChangesValue = false, AccessibleName = "Block size"
    };
    private readonly ModernToggleSwitch _lighting = new() { AutoSize = false, Checked = true, AccessibleName = "Block lighting" };
    private readonly ModernToggleSwitch _nightVision = new() { AutoSize = false, Checked = true, AccessibleName = "Night vision" };
    private readonly ComboBox _lightQuality = new() { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Lighting quality" };
    private readonly ModernToggleSwitch _editable = new() { AutoSize = false, Checked = true, AccessibleName = "Grid layers" };
    private readonly ComboBox _blockTool = new() { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Block tool" };
    private readonly ComboBox _material = new() { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Block material" };
    private readonly ModernNumericUpDown _lightLevel = new()
    {
        Minimum = 0, Maximum = SceneBlockLightVolume.MaximumLevel, Value = SceneBlockLightVolume.MaximumLevel,
        MouseWheelChangesValue = false, Enabled = false, AccessibleName = "Light strength (0 removes)"
    };
    private readonly Label _editTarget = new() { AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.PanelValueText, UseMnemonic = false };
    private readonly Label _editStatus = new() { ForeColor = Theme.PanelValueText, UseMnemonic = false };
    private SceneBlockGrid? _grid;
    private SceneBlockChunk? _activeChunk;
    private readonly Label _status = new()
    {
        Font = Theme.UiFont(), ForeColor = Theme.PanelValueText, UseMnemonic = false,
        Margin = Padding.Empty
    };
    private readonly Button _inspect;
    private readonly Button _generate;
    private readonly Button _backFromChunk;
    private readonly Section[] _sections;
    private readonly ToolTip _tooltips = new();
    private readonly CancellationTokenSource _lifetime = new();
    private bool _busy;
    private bool _layingOut;

    private sealed record Section(Label Heading, ShotDirectorPanel.CameraSectionToggle Toggle,
        (Label Caption, Control Editor)[] Fields, Control[] FullWidthRows);

    private MinecraftSectionImport.Plan? PreparedPlan { get; set; }
    internal event Action<MinecraftSectionImport.Plan>? GenerateRequested;
    internal event Action? ExitChunkRequested;
    internal enum BlockTool { Locate, Erase, Paint, LightPoint }
    private bool Editing => _grid is not null && _activeChunk.HasValue;
    internal BlockTool Tool => (BlockTool)Math.Max(0, _blockTool.SelectedIndex);
    internal int Material => _material.SelectedItem is MaterialChoice choice ? choice.Index : -1;
    internal int? CommitLightLevel() => _lightLevel.TryCommitInput() ? (int)_lightLevel.Value : null;
    private SceneBlockLightQuality LightQuality => _lightQuality.SelectedIndex switch
    {
        1 => SceneBlockLightQuality.Medium,
        2 => SceneBlockLightQuality.HighDetailed,
        _ => SceneBlockLightQuality.Low
    };
    private sealed record MaterialChoice(int Index, string Name)
    {
        public override string ToString() => Name;
    }

    public MinecraftSceneExtensionPanel()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Theme.Panel;
        AccessibleName = "Minecraft Scene";
        SetStyle(ControlStyles.ResizeRedraw, true);
        InspectorModuleStyle.StylePageTitle(_title);
        Theme.StyleToolTip(_tooltips);
        InspectorModuleStyle.StyleField(_size);
        InspectorModuleStyle.StyleField(_blockTool);
        InspectorModuleStyle.StyleField(_material);
        InspectorModuleStyle.StyleField(_lightLevel);
        InspectorModuleStyle.StyleField(_lightQuality);
        _lightQuality.Items.AddRange([UiLocalization.T("Low"), UiLocalization.T("Medium"), UiLocalization.T("High")]);
        _lightQuality.SelectedIndex = 2;
        _blockTool.Items.AddRange([UiLocalization.T("Locate"), UiLocalization.T("Erase"), UiLocalization.T("Stamp block")]);
        _blockTool.SelectedIndex = 0;
        _blockTool.Enabled = _material.Enabled = false;
        _blockTool.SelectedIndexChanged += (_, _) => UpdateBlockControls();
        _textures.Text = MinecraftSectionImport.DefaultTextureDirectory(AppContext.BaseDirectory);
        _status.Text = "Not checked";
        _inspect = CreateAction("Check Scene");
        _generate = CreateAction("Generate Scene", primary: true);
        _backFromChunk = CreateAction("Back to MC scene");
        _backFromChunk.Click += (_, _) => ExitChunkRequested?.Invoke();
        _sections =
        [
            CreateSection("Block editing", [Field("Active layer", _editTarget),
                Field("Block tool", _blockTool), Field("Block material", _material),
                Field("Light strength (0 removes)", _lightLevel)], [_editStatus, _backFromChunk]),
            CreateSection("Scene source",
            [
                Field("Section JSON", PathPicker(_source, () => PickFile(_source, "Section JSON"))),
                Field("Texture folder", PathPicker(_textures, PickDirectory)),
                Field("Texture map", PathPicker(_mapping, () => PickFile(_mapping, "Texture map (optional)"), optional: true))
            ]),
            CreateSection("Scene layout", [Field("Block size", _size), Field("Grid layers", _editable),
                Field("Block lighting", _lighting), Field("Night vision", _nightVision), Field("Lighting quality", _lightQuality)]),
            CreateSection("Scene check", [], [_status, _inspect, _generate])
        ];
        Controls.AddRange([_title, _scroll]);
        _scroll.Content.SizeChanged += (_, _) => LayoutInspector();
        _scroll.Content.Paint += (_, e) =>
        {
            for (var i = 1; i < _sections.Length; i++)
                InspectorModuleStyle.DrawDivider(_scroll.Content, e.Graphics, _sections[i].Heading.Top - ScaleMetric(10));
        };
        _status.TextChanged += (_, _) => LayoutInspector();
        _editStatus.TextChanged += (_, _) => LayoutInspector();

        _inspect.Click += async (_, _) => await InspectAsync();
        _generate.Click += (_, _) =>
        {
            if (_size.TryCommitInput() && !_busy && PreparedPlan is { MissingCount: 0 } plan)
                GenerateRequested?.Invoke(plan);
        };
        foreach (var path in new[] { _source, _textures, _mapping }) path.TextChanged += (_, _) => ResetPlan();
        _size.ValueChanged += (_, _) => ResetPlan();
        _lighting.CheckedChanged += (_, _) =>
        {
            _lightQuality.Enabled = _lighting.Checked;
            _nightVision.Enabled = _lighting.Checked;
            ResetPlan();
        };
        _lightQuality.SelectedIndexChanged += (_, _) => ResetPlan();
        _nightVision.CheckedChanged += (_, _) => ResetPlan();
        _editable.CheckedChanged += (_, _) => ResetPlan();
        UpdateButtons();
        UiLocalization.Watch(this);
    }

    private static Button CreateAction(string title, bool primary = false)
    {
        var button = new Button { Text = title, AutoEllipsis = true, Margin = Padding.Empty };
        Theme.StyleStandardButton(button, primary);
        return button;
    }

    internal void ShowGenerated(string name) => _status.Text = string.Format(UiLocalization.T("Scene created: {0}"), name);

    internal void ShowGenerationError(string detail) =>
        _status.Text = UiLocalization.T("Could not generate the Minecraft scene.") + "\n" + detail;

    internal void BindBlockEditor(SceneBlockGrid? grid, string? layerName)
    {
        if (!ReferenceEquals(_grid, grid))
        {
            _grid = grid;
            EndChunkEditing();
            _blockTool.SelectedIndex = 0;
            if (grid is { LightDepth: > 0 } && _blockTool.Items.Count == 3)
                _blockTool.Items.Add(UiLocalization.T("Light point"));
            else if (grid is not { LightDepth: > 0 } && _blockTool.Items.Count > 3)
                _blockTool.Items.RemoveAt(3);
            _material.BeginUpdate();
            try
            {
                _material.Items.Clear();
                if (grid is not null)
                    _material.Items.AddRange(grid.Palette.Select((material, index) => (material, index))
                        .Where(item => item.material.AssetId is not null).OrderBy(item => item.material.Name, StringComparer.Ordinal)
                        .Select(item => new MaterialChoice(item.index, item.material.Name)).ToArray());
                if (_material.Items.Count != 0) _material.SelectedIndex = 0;
            }
            finally { _material.EndUpdate(); }
            _editStatus.Text = "";
            UpdateBlockControls();
        }
        _editTarget.Text = layerName ?? "-";
    }

    internal void BeginChunkEditing(SceneBlockChunk chunk)
    {
        if (_activeChunk == chunk) return;
        _activeChunk = chunk;
        UpdateBlockControls();
        _sections[0].Heading.Text = _grid is { SectionDepth: > 0 }
            ? $"{UiLocalization.T("MC Chunk")} ({chunk.X}, {chunk.Y}, {chunk.Z})"
            : $"{UiLocalization.T("MC Chunk")} ({chunk.X}, {chunk.Y})";
        LayoutInspector();
    }

    internal void EndChunkEditing()
    {
        if (_activeChunk is null) return;
        _activeChunk = null;
        UpdateBlockControls();
        _sections[0].Heading.Text = UiLocalization.T("Block editing");
        LayoutInspector();
    }

    internal void ReportBlockEdit(string message) => _editStatus.Text = message;

    internal void SelectMaterial(int material)
    {
        for (var i = 0; i < _material.Items.Count; i++)
            if (_material.Items[i] is MaterialChoice choice && choice.Index == material)
            {
                _material.SelectedIndex = i;
                return;
            }
    }

    private void UpdateBlockControls()
    {
        _blockTool.Enabled = Editing;
        _material.Enabled = Editing && Tool == BlockTool.Paint && _material.Items.Count != 0;
        _lightLevel.Enabled = Editing && Tool == BlockTool.LightPoint;
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (_sections is not null) LayoutInspector();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        SvgIcons.Draw(e.Graphics, SvgIconKind.SceneSlate,
            new Rectangle(ScaleMetric(16), ScaleMetric(21), ScaleMetric(18), ScaleMetric(18)), Theme.PanelSecondaryText);
        InspectorModuleStyle.DrawDivider(this, e.Graphics, ScaleMetric(InspectorModuleStyle.PageHeaderHeight));
    }

    private void LayoutInspector()
    {
        if (_layingOut || IsDisposed) return;
        _layingOut = true;
        _scroll.SuspendContentLayout();
        try
        {
            var inset = ScaleMetric(16);
            var headerHeight = ScaleMetric(InspectorModuleStyle.PageHeaderHeight);
            var titleLeft = inset + ScaleMetric(24);
            _title.SetBounds(titleLeft, inset, Math.Max(1, ClientSize.Width - titleLeft - inset), ScaleMetric(28));
            _scroll.SetBounds(0, headerHeight, ClientSize.Width, Math.Max(0, ClientSize.Height - headerHeight));

            var width = Math.Max(1, _scroll.Content.ClientSize.Width - 2 * inset);
            var headingHeight = ScaleMetric(28);
            var fieldHeight = ScaleMetric(Theme.ControlHeight);
            var rowStep = ScaleMetric(Theme.InspectorRowHeightComfortable);
            var gap = ScaleMetric(8);
            var captionColumn = (int)Math.Floor(width * 0.48f);
            var top = ScaleMetric(6);
            _scroll.ContentPadding = new Padding(0, 0, 0, gap);

            foreach (var section in _sections)
            {
                section.Heading.SetBounds(inset, top, Math.Max(1, width - headingHeight - gap), headingHeight);
                section.Toggle.SetBounds(inset + Math.Max(0, width - headingHeight), top, headingHeight, headingHeight);
                top += ScaleMetric(36);
                var expanded = section.Toggle.Expanded;
                foreach (var (caption, editor) in section.Fields)
                {
                    caption.Visible = editor.Visible = expanded;
                    if (!expanded) continue;
                    var labelHeight = Math.Min(fieldHeight, caption.Font.Height + ScaleMetric(4));
                    caption.SetBounds(inset, top + (fieldHeight - labelHeight) / 2, Math.Max(1, captionColumn - gap), labelHeight);
                    editor.SetBounds(inset + captionColumn, top, width - captionColumn, fieldHeight);
                    top += rowStep;
                }
                foreach (var row in section.FullWidthRows)
                {
                    if (ReferenceEquals(row, _backFromChunk) && _activeChunk is null)
                    {
                        row.Visible = false;
                        continue;
                    }
                    row.Visible = expanded;
                    if (!expanded) continue;
                    var status = row as Label;
                    var rowHeight = status is not null
                        ? (status.Text.Length == 0 ? 0 : TextRenderer.MeasureText(status.Text, status.Font, new Size(width, int.MaxValue),
                            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height)
                        : fieldHeight;
                    row.SetBounds(inset, top, width, rowHeight);
                    top += status is not null ? rowHeight + gap : rowStep;
                }
                top += gap;
            }
        }
        finally
        {
            _scroll.ResumeContentLayout(performLayout: true);
            _layingOut = false;
        }
        _scroll.Content.Invalidate();
    }

    private Section CreateSection(string title, (Label Caption, Control Editor)[] fields, Control[]? fullWidthRows = null)
    {
        var heading = InspectorModuleStyle.CreateTextLabel();
        heading.Text = title;
        heading.Font = Theme.UiFont(Theme.InspectorSectionFontSize);
        heading.ForeColor = Theme.Text;
        heading.Margin = Padding.Empty;
        heading.Cursor = Cursors.Hand;
        var toggle = new ShotDirectorPanel.CameraSectionToggle(title) { Margin = Padding.Empty };
        Theme.StyleButton(toggle);
        heading.Click += (_, _) =>
        {
            toggle.Focus();
            toggle.PerformClick();
        };
        toggle.Click += (_, _) => LayoutInspector();
        var rows = fullWidthRows ?? [];
        _scroll.Content.Controls.AddRange([heading, toggle]);
        foreach (var (caption, editor) in fields) _scroll.Content.Controls.AddRange([caption, editor]);
        _scroll.Content.Controls.AddRange(rows);
        return new Section(heading, toggle, fields, rows);
    }

    private (Label Caption, Control Editor) Field(string title, Control editor)
    {
        var caption = InspectorModuleStyle.CreateFieldLabel(title);
        caption.Dock = DockStyle.None;
        caption.Margin = editor.Margin = Padding.Empty;
        _tooltips.SetToolTip(caption, UiLocalization.T(editor.AccessibleName ?? title));
        return (caption, editor);
    }

    private int ScaleMetric(int logical) => (int)Math.Round(logical * DeviceDpi / 96d);

    private Control PathPicker(ModernTextInput input, Action choose, bool optional = false)
    {
        InspectorModuleStyle.StyleField(input);
        var picker = new TableLayoutPanel
        {
            ColumnCount = optional ? 3 : 2, RowCount = 1, Padding = Padding.Empty, AccessibleName = input.AccessibleName
        };
        picker.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        picker.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.ControlHeight));
        if (optional) picker.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.ControlHeight));
        input.Dock = DockStyle.Fill;
        input.Margin = Padding.Empty;
        picker.Controls.Add(input, 0, 0);
        AddButton(SvgIconKind.Open, "Browse...", choose, 1);
        if (optional) AddButton(SvgIconKind.Close, "Clear", () => input.Text = string.Empty, 2);
        return picker;

        void AddButton(SvgIconKind icon, string name, Action click, int column)
        {
            var button = new SvgIconButton(icon) { AccessibleName = name, Dock = DockStyle.Fill, Margin = new Padding(4, 0, 0, 0) };
            Theme.StyleToolbarButton(button);
            button.Click += (_, _) => click();
            _tooltips.SetToolTip(button, UiLocalization.T(name));
            picker.Controls.Add(button, column, 0);
        }
    }

    private void PickFile(ModernTextInput target, string title)
    {
        using var dialog = new OpenFileDialog { Title = UiLocalization.T(title), Filter = "JSON (*.json)|*.json", CheckFileExists = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) target.Text = dialog.FileName;
    }

    private void PickDirectory()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = UiLocalization.T("Texture folder"), UseDescriptionForTitle = true, ShowNewFolderButton = false,
            SelectedPath = _textures.Text
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) _textures.Text = dialog.SelectedPath;
    }

    private void ResetPlan()
    {
        PreparedPlan = null;
        _status.Text = UiLocalization.T("Not checked");
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        _inspect.Enabled = !_busy && _source.Text.Length > 0 && _textures.Text.Length > 0;
        _generate.Enabled = !_busy && PreparedPlan is { MissingCount: 0 };
    }

    private async Task InspectAsync()
    {
        if (_busy || !_size.TryCommitInput()) return;
        var (source, textures, map, size, lighting, editable, quality, nightVision) = (_source.Text, _textures.Text, _mapping.Text,
            (int)_size.Value, _lighting.Checked, _editable.Checked, LightQuality, _nightVision.Checked);
        var token = _lifetime.Token;
        _busy = true;
        PreparedPlan = null;
        _status.Text = UiLocalization.T("Checking scene...");
        UpdateButtons();
        try
        {
            var plan = await Task.Run(() => MinecraftSectionImport.Prepare(source, textures, map, size, lighting, token, editable, quality, nightVision), token);
            if (IsDisposed || token.IsCancellationRequested) return;
            if (source != _source.Text || textures != _textures.Text || map != _mapping.Text
                || size != (int)_size.Value || lighting != _lighting.Checked || editable != _editable.Checked
                || quality != LightQuality || nightVision != _nightVision.Checked) return;
            PreparedPlan = plan;
            var summary = string.Format(UiLocalization.T("{0} visible / {1} source blocks   |   {2} depth layers\n{3} × {4} px"),
                plan.Tiles.Length, plan.SourceCount, plan.SectionDepth > 0 ? SceneBlockChunk.CountFor(plan.SectionDepth)
                    : plan.Tiles.Select(tile => tile.Depth).Distinct().Count(), plan.Width, plan.Height);
            if (plan.ProceduralLighting) summary += "\n" + string.Format(UiLocalization.T("Shaded blocks: {0}"),
                plan.Tiles.Count(tile => tile.ShadowCorners != 0));
            if (plan.MissingHiddenCount > 0) summary += "\n" + string.Format(UiLocalization.T("Missing hidden block textures: {0}"), plan.MissingHiddenCount);
            _status.Text = plan.MissingCount == 0 ? summary : summary + "\n\n" +
                string.Format(UiLocalization.T("Missing textures ({0}):"), plan.MissingCount) + "\n" + string.Join("\n", plan.Missing)
                + (plan.MissingCount > plan.Missing.Length ? "\n..." : "");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
            or InvalidDataException or InvalidOperationException or ArgumentException or KeyNotFoundException or FormatException
            or System.Runtime.InteropServices.ExternalException)
        {
            if (!IsDisposed && !token.IsCancellationRequested)
                _status.Text = UiLocalization.T("Could not check the scene.") + "\n" + e.Message;
        }
        finally
        {
            _busy = false;
            if (!IsDisposed) UpdateButtons();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            _lifetime.Cancel();
            _lifetime.Dispose();
            _tooltips.Dispose();
        }
        base.Dispose(disposing);
    }
}
