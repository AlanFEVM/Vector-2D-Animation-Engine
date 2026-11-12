namespace VectorAnimationEngine;

[Flags]
internal enum DrawingObjectPlaybackChangedFields { Fps = 1, Mode = 2, HoldFrame = 4, All = Fps | Mode | HoldFrame }

internal sealed class DrawingObjectPlaybackSettingsChangedEventArgs : EventArgs
{
    public DrawingObjectPlaybackSettingsChangedEventArgs(
        decimal playbackFps,
        DrawingObjectPlaybackMode playbackMode,
        int holdFrame,
        DrawingObjectPlaybackChangedFields fields = DrawingObjectPlaybackChangedFields.All)
    {
        PlaybackFps = playbackFps;
        PlaybackMode = playbackMode;
        HoldFrame = holdFrame;
        Fields = fields;
    }

    public decimal PlaybackFps { get; }
    public DrawingObjectPlaybackMode PlaybackMode { get; }
    public int HoldFrame { get; }
    public DrawingObjectPlaybackChangedFields Fields { get; }
}

internal sealed class DrawingObjectAnchorChangedEventArgs(PointF anchor) : EventArgs
{
    public PointF Anchor { get; } = anchor;
}

internal sealed class DrawingObjectAppearanceChangedEventArgs(
    float alpha,
    int tintArgb,
    bool alphaChanged,
    bool tintChanged) : EventArgs
{
    public float Alpha { get; } = Math.Clamp(alpha, 0f, 1f);
    public int TintArgb { get; } = OpaqueArgb(tintArgb);
    public bool AlphaChanged { get; } = alphaChanged;
    public bool TintChanged { get; } = tintChanged;

    private static int OpaqueArgb(int argb)
    {
        var color = Color.FromArgb(argb);
        return Color.FromArgb(255, color.R, color.G, color.B).ToArgb();
    }
}

internal sealed class DrawingObjectInstancePanel : Panel
{
    // Logical field geometry. Convert every row and label together when the panel is laid out,
    // including after WinForms scales a hidden panel for a different monitor.
    private const int PanelTitleTop = 6;
    private const int FieldPitch = Theme.InspectorRowHeightComfortable;
    private const int FieldLabelOffset = 0;
    private const int FieldRowTop = PanelTitleTop + Theme.InspectorTitleHeight + Theme.GapXs;
    private const int FieldRowCount = 8;
    private const int TintRowTop = FieldRowTop + FieldPitch * FieldRowCount;
    private const int TintRowHeight = 42;
    private const int RestoreButtonTop = TintRowTop + TintRowHeight + Theme.GapXs;
    private const int FiltersButtonHeight = Theme.ControlHeight;
    private const int PanelHeight = RestoreButtonTop + Theme.ControlHeight + Theme.GapXs;
    private readonly Label _title;
    private readonly ShotDirectorPanel.CameraSectionToggle[] _sections =
        [new("Playback settings"), new("Source anchor"), new("Appearance")];
    private readonly Control[] _sectionHeaders = new Control[3];
    private int _preferredLogicalHeight = PanelHeight + FiltersButtonHeight + 10;
    private bool _layingOut;
    private readonly List<(Label Label, int Top)> _fieldLabels = [];
    private readonly SymbolFiltersPanel _filters = new();
    private readonly Button _showFilters = new() { Text = "Symbol Filters", AccessibleName = "Symbol Filters" };
    private FilterWindow? _filtersWindow;
    public SymbolFiltersPanel FiltersPanel => _filters;

    private sealed record PlaybackModeItem(DrawingObjectPlaybackMode Mode, string Label)
    {
        public override string ToString() => UiLocalization.T(Label);
    }

    private readonly ModernNumericUpDown _fps = new()
    {
        Minimum = 1,
        Maximum = 120,
        Value = 30,
        DecimalPlaces = 3,
        Increment = 0.001m,
        WheelAdjustsHoveredDigit = true,
        Suffix = "fps",
        ShowSuffix = true,
        AccessibleName = "Symbol playback FPS"
    };
    private readonly ComboBox _playbackMode = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        AccessibleName = "Symbol playback mode"
    };
    private sealed record BlendModeItem(LayerBlendMode Mode, string Label)
    {
        public override string ToString() => UiLocalization.T(Label);
    }
    private readonly ComboBox _blendMode = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Symbol blend mode"
    };
    private readonly ModernNumericUpDown _holdFrame = new()
    {
        Minimum = 1,
        Maximum = 999999,
        Value = 1,
        AccessibleName = "Symbol hold frame"
    };
    private readonly ModernNumericUpDown _anchorX = new()
    {
        Minimum = -5_000_000,
        Maximum = 5_000_000,
        Increment = 10,
        Suffix = "vu",
        ShowSuffix = true,
        AccessibleName = "Symbol anchor X"
    };
    private readonly ModernNumericUpDown _anchorY = new()
    {
        Minimum = -5_000_000,
        Maximum = 5_000_000,
        Increment = 10,
        Suffix = "vu",
        ShowSuffix = true,
        AccessibleName = "Symbol anchor Y"
    };
    private readonly ModernSlider _alpha = new()
    {
        Minimum = 0,
        Maximum = 100,
        Value = 100,
        SmallChange = 1,
        LargeChange = 10,
        AccessibleName = "Symbol alpha",
        AccessibleDescription = "Symbol alpha percentage"
    };
    private readonly ModernNumericUpDown _alphaValue = new()
    {
        Minimum = 0, Maximum = 100, Value = 100, DecimalPlaces = 2, Increment = 0.1m,
        Suffix = "%", ShowSuffix = true, AccessibleName = "Symbol alpha percentage"
    };
    private readonly ColorTargetButton _tint = new("Tint")
    {
        SwatchColor = Color.White,
        DetailText = "#FFFFFF",
        AccessibleName = "Symbol tint",
        AccessibleDescription = "Multiply symbol colors by this tint"
    };
    private readonly Label _holdFrameLabel;
    private readonly SvgIconButton _restoreSize = new(SvgIconKind.RestoreSize)
    {
        AccessibleName = "Restore original size",
        Width = Theme.ControlHeightCompact,
        Height = Theme.ControlHeightCompact
    };
    private readonly ToolTip _toolTip = new();
    private bool _updating;
    private bool _alphaMixed;
    private bool _tintMixed;
    private bool _alphaInteractionActive;
    private bool _alphaInteractionStartedMixed;
    private Color _tintColor = Color.White;
    private bool _updatingAlpha;

    public DrawingObjectInstancePanel()
    {
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Font = Theme.UiFont();
        Padding = new Padding(12, 8, 12, 10);
        Height = PreferredPanelHeight;
        MinimumSize = new Size(0, PreferredPanelHeight);
        Controls.Add(_showFilters);
        Theme.StyleButton(_showFilters);
        _showFilters.Click += (_, _) => ShowFiltersWindow();

        _title = InspectorModuleStyle.CreateTitle("Symbol Instance");
        _title.Dock = DockStyle.None;
        _title.SetBounds(12, PanelTitleTop, 210, Theme.InspectorTitleHeight);
        _title.AutoEllipsis = true;
        Controls.Add(_title);

        AddFieldLabel("Playback FPS", RowLabelTop(0));
        AddFieldLabel("Mode", RowLabelTop(1));
        _holdFrameLabel = AddFieldLabel("Hold Frame", RowLabelTop(2));
        AddFieldLabel("Source anchor X", RowLabelTop(3));
        AddFieldLabel("Source anchor Y", RowLabelTop(4));
        AddFieldLabel("Alpha", RowLabelTop(5));
        AddFieldLabel("Blend Mode", RowLabelTop(7));
        AddFieldLabel("Original Size", RestoreButtonTop + FieldLabelOffset);

        _playbackMode.Items.AddRange([
            new PlaybackModeItem(DrawingObjectPlaybackMode.PlayOnce, "Play Once"),
            new PlaybackModeItem(DrawingObjectPlaybackMode.Loop, "Loop"),
            new PlaybackModeItem(DrawingObjectPlaybackMode.HoldFrame, "Hold Frame")
        ]);
        _playbackMode.SelectedIndex = 0;
        foreach (var field in new Control[] { _fps, _holdFrame, _anchorX, _anchorY, _playbackMode, _blendMode, _alphaValue })
            InspectorModuleStyle.StyleField(field);
        foreach (var item in LayerBlendModePanel.BlendModes) _blendMode.Items.Add(new BlendModeItem(item.Mode, item.Label));
        _blendMode.SelectedIndex = 0;
        Theme.StyleButton(_restoreSize);
        Theme.StyleToolTip(_toolTip);
        _toolTip.SetToolTip(_alpha, UiLocalization.T("Adjust symbol alpha"));
        _toolTip.SetToolTip(_tint, UiLocalization.T("Choose symbol tint"));
        _toolTip.SetToolTip(_restoreSize, UiLocalization.T("Restore original size"));
        foreach (var anchor in new[] { _anchorX, _anchorY })
        {
            var hint = UiLocalization.T("Edits the shared source symbol anchor and compensates every scene and nested placement.");
            anchor.AccessibleDescription = hint;
            _toolTip.SetToolTip(anchor, hint);
        }

        Controls.Add(_fps);
        Controls.Add(_playbackMode);
        Controls.Add(_blendMode);
        Controls.Add(_holdFrame);
        Controls.Add(_anchorX);
        Controls.Add(_anchorY);
        Controls.Add(_alpha);
        Controls.Add(_alphaValue);
        Controls.Add(_tint);
        Controls.Add(_restoreSize);
        string[] sectionNames = ["Playback settings", "Source anchor", "Appearance"];
        for (var index = 0; index < _sections.Length; index++)
        {
            _sectionHeaders[index] = InspectorModuleStyle.CreateSectionHeader(sectionNames[index], _sections[index], LayoutFields);
            _sectionHeaders[index].Dock = DockStyle.None;
            Controls.Add(_sectionHeaders[index]);
        }
        HookEvents();
        LayoutFields();
        UpdateModeState();
    }

    public event EventHandler<DrawingObjectPlaybackSettingsChangedEventArgs>? PlaybackSettingsChanged;
    public event EventHandler<DrawingObjectAnchorChangedEventArgs>? AnchorChanged;
    public event EventHandler<DrawingObjectAppearanceChangedEventArgs>? AppearanceChanged;
    public event EventHandler<LayerBlendModeChangedEventArgs>? BlendModeChanged;
    public event EventHandler? AppearanceInteractionStarted;
    public event EventHandler? AppearanceInteractionCompleted;
    public event EventHandler? AppearanceInteractionCanceled;
    public event EventHandler? RestoreOriginalSizeRequested;
    public event EventHandler? PlaybackInteractionStarted;
    public event EventHandler? PlaybackInteractionCompleted;
    public event EventHandler? PlaybackInteractionCanceled;
    public event EventHandler? AnchorInteractionStarted;
    public event EventHandler? AnchorInteractionCompleted;
    public event EventHandler? AnchorInteractionCanceled;

    public int PreferredPanelHeight => ScaleLayout(_preferredLogicalHeight);

    public void SetInstance(
        DrawingObjectInstanceDefinition instance,
        int sourceFrameCount,
        bool canRestoreOriginalTransform,
        PointF sourceAnchor,
        bool canEditAnchor = true,
        bool alphaMixed = false,
        bool tintMixed = false)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var state = instance.EvaluateState(0);
        SetInstance(
            state,
            sourceFrameCount,
            canRestoreOriginalTransform,
            sourceAnchor,
            canEditAnchor,
            alphaMixed,
            tintMixed);
    }

    public void SetInstance(
        InstanceFrameState state,
        int sourceFrameCount,
        bool canRestoreOriginalTransform,
        PointF sourceAnchor,
        bool canEditAnchor,
        bool alphaMixed = false,
        bool tintMixed = false,
        bool filtersMixed = false,
        bool blendModeMixed = false,
        bool fpsMixed = false, bool playbackModeMixed = false, bool holdFrameMixed = false)
    {
        _updating = true;
        try
        {
            _fps.Value = state.PlaybackFps;
            _fps.MixedValue = fpsMixed;
            for (var index = _playbackMode.Items.Count - 1; index >= 0; index--)
                if (_playbackMode.Items[index] is not PlaybackModeItem) _playbackMode.Items.RemoveAt(index);
            _playbackMode.SelectedIndex = playbackModeMixed
                ? _playbackMode.Items.Add(UiLocalization.T("Mixed")) : ModeIndex(state.PlaybackMode);
            _playbackMode.AccessibleDescription = UiLocalization.T(playbackModeMixed ? "Mixed" : "Symbol playback mode");
            _holdFrame.Maximum = Math.Max(1, sourceFrameCount);
            _holdFrame.Value = Math.Clamp(state.HoldFrame + 1, 1, (int)_holdFrame.Maximum);
            _holdFrame.MixedValue = holdFrameMixed;
            _anchorX.Value = (decimal)sourceAnchor.X;
            _anchorY.Value = (decimal)sourceAnchor.Y;
            _anchorX.Enabled = canEditAnchor;
            _anchorY.Enabled = canEditAnchor;
            _restoreSize.Enabled = canRestoreOriginalTransform;
            _alpha.Value = (int)Math.Clamp(MathF.Round(state.Alpha * 100f), 0f, 100f);
            _alphaValue.Value = (decimal)Math.Clamp(state.Alpha * 100f, 0f, 100f);
            _alphaValue.MixedValue = alphaMixed;
            _tintColor = Opaque(Color.FromArgb(state.TintArgb));
            _alphaMixed = alphaMixed;
            _tintMixed = tintMixed;
            _filters.SetFilters(state.Filters, filtersMixed);
            for (var index = _blendMode.Items.Count - 1; index >= 0; index--)
                if (_blendMode.Items[index] is not BlendModeItem) _blendMode.Items.RemoveAt(index);
            _blendMode.SelectedIndex = blendModeMixed ? _blendMode.Items.Add(UiLocalization.T("Mixed"))
                : Array.FindIndex(LayerBlendModePanel.BlendModes, item => item.Mode == state.BlendMode);
            _blendMode.AccessibleDescription = UiLocalization.T(blendModeMixed ? "Mixed" : "Symbol blend mode");
            UpdateAlphaPresentation();
            UpdateTintPresentation();
            UpdateModeState();
        }
        finally
        {
            _updating = false;
        }
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        LayoutFields();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        MinimumSize = new Size(0, PreferredPanelHeight);
        Height = PreferredPanelHeight;
        LayoutFields();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _toolTip.Dispose();
            _filtersWindow?.Dispose();
            _filters.Dispose();
        }
        base.Dispose(disposing);
    }

    private void ShowFiltersWindow()
    {
        if (_filtersWindow is null || _filtersWindow.IsDisposed)
        {
            _filtersWindow = new FilterWindow(_filters);
            _filtersWindow.FormClosing += (_, e) =>
            {
                if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; _filtersWindow.Hide(); }
            };
            UiLocalization.Watch(_filtersWindow);
        }
        _filters.Enabled = Enabled;
        if (!_filtersWindow.Visible) _filtersWindow.Show(FindForm());
        _filtersWindow.Activate();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) _filtersWindow?.Hide();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        _filters.Enabled = Enabled;
    }

    private void HookEvents()
    {
        foreach (var input in new[] { _fps, _holdFrame })
        {
            input.InteractionStarted += (_, _) => { if (!_updating) PlaybackInteractionStarted?.Invoke(this, EventArgs.Empty); };
            input.InteractionCompleted += (_, _) => { if (!_updating) PlaybackInteractionCompleted?.Invoke(this, EventArgs.Empty); };
            input.InteractionCanceled += (_, _) => { if (!_updating) PlaybackInteractionCanceled?.Invoke(this, EventArgs.Empty); };
        }
        foreach (var input in new[] { _anchorX, _anchorY })
        {
            input.InteractionStarted += (_, _) => { if (!_updating) AnchorInteractionStarted?.Invoke(this, EventArgs.Empty); };
            input.InteractionCompleted += (_, _) => { if (!_updating) AnchorInteractionCompleted?.Invoke(this, EventArgs.Empty); };
            input.InteractionCanceled += (_, _) => { if (!_updating) AnchorInteractionCanceled?.Invoke(this, EventArgs.Empty); };
        }
        _fps.ValueChanged += (_, _) => RaisePlaybackSettingsChanged(DrawingObjectPlaybackChangedFields.Fps);
        _holdFrame.ValueChanged += (_, _) => RaisePlaybackSettingsChanged(DrawingObjectPlaybackChangedFields.HoldFrame);
        _playbackMode.SelectedIndexChanged += (_, _) =>
        {
            UpdateModeState();
            RaisePlaybackSettingsChanged(DrawingObjectPlaybackChangedFields.Mode);
        };
        _anchorX.ValueChanged += (_, _) => RaiseAnchorChanged();
        _anchorY.ValueChanged += (_, _) => RaiseAnchorChanged();
        _alpha.InteractionStarted += (_, _) => BeginAlphaInteraction();
        _alpha.ValueChanged += (_, _) => ApplyAlphaValue();
        _alpha.InteractionCompleted += (_, _) => CompleteAlphaInteraction();
        _alpha.InteractionCanceled += (_, _) => CancelAlphaInteraction();
        _alphaValue.InteractionStarted += (_, _) => BeginAlphaInteraction();
        _alphaValue.InteractionCompleted += (_, _) => CompleteAlphaInteraction();
        _alphaValue.InteractionCanceled += (_, _) => CancelAlphaInteraction();
        _alphaValue.ValueChanged += (_, _) =>
        {
            if (_updating || _updatingAlpha) return;
            _alphaMixed = false;
            _updatingAlpha = true;
            try { _alpha.Value = (int)Math.Round(_alphaValue.Value); }
            finally { _updatingAlpha = false; }
            RaiseAppearanceChanged(alphaChanged: true, tintChanged: false);
        };
        _tint.Click += (_, _) => ChooseTint();
        _blendMode.SelectedIndexChanged += (_, _) =>
        {
            if (!_updating && _blendMode.SelectedItem is BlendModeItem item)
                BlendModeChanged?.Invoke(this, new LayerBlendModeChangedEventArgs(item.Mode));
        };
        _restoreSize.Click += (_, _) => RestoreOriginalSizeRequested?.Invoke(this, EventArgs.Empty);
    }

    private void BeginAlphaInteraction()
    {
        if (_updating || _alphaInteractionActive) return;
        _alphaInteractionActive = true;
        _alphaInteractionStartedMixed = _alphaMixed;
        AppearanceInteractionStarted?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyAlphaValue()
    {
        if (_updating || _updatingAlpha) return;
        _alphaMixed = false;
        _updatingAlpha = true;
        try { _alphaValue.Value = _alpha.Value; }
        finally { _updatingAlpha = false; }
        UpdateAlphaPresentation();
        RaiseAppearanceChanged(alphaChanged: true, tintChanged: false);
    }

    private void CompleteAlphaInteraction()
    {
        if (!_alphaInteractionActive) return;
        ResetAlphaInteraction();
        AppearanceInteractionCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void CancelAlphaInteraction()
    {
        if (!_alphaInteractionActive) return;
        _alphaMixed = _alphaInteractionStartedMixed;
        UpdateAlphaPresentation();
        ResetAlphaInteraction();
        AppearanceInteractionCanceled?.Invoke(this, EventArgs.Empty);
    }

    private void ResetAlphaInteraction()
    {
        _alphaInteractionActive = false;
        _alphaInteractionStartedMixed = false;
    }

    private void ChooseTint()
    {
        var originalColor = _tintColor;
        var originalMixed = _tintMixed;
        using var picker = new ProfessionalColorPickerDialog(_tintColor, "Symbol tint");
        AppearanceInteractionStarted?.Invoke(this, EventArgs.Empty);
        picker.ColorChanged += (_, _) =>
        {
            _tintColor = Opaque(picker.Color);
            _tintMixed = false;
            UpdateTintPresentation();
            RaiseAppearanceChanged(alphaChanged: false, tintChanged: true);
        };

        var accepted = false;
        try { accepted = picker.ShowDialog(FindForm()) == DialogResult.OK; }
        finally
        {
            if (accepted) AppearanceInteractionCompleted?.Invoke(this, EventArgs.Empty);
            else
            {
                _tintColor = originalColor;
                _tintMixed = originalMixed;
                UpdateTintPresentation();
                AppearanceInteractionCanceled?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private void RaiseAppearanceChanged(bool alphaChanged, bool tintChanged)
    {
        if (_updating) return;
        AppearanceChanged?.Invoke(
            this,
            new DrawingObjectAppearanceChangedEventArgs(
                (float)_alphaValue.Value / 100f,
                _tintColor.ToArgb(),
                alphaChanged,
                tintChanged));
    }

    private void RaiseAnchorChanged()
    {
        if (_updating) return;
        AnchorChanged?.Invoke(
            this,
            new DrawingObjectAnchorChangedEventArgs(new PointF((float)_anchorX.Value, (float)_anchorY.Value)));
    }

    private void RaisePlaybackSettingsChanged(DrawingObjectPlaybackChangedFields fields)
    {
        if (_updating) return;
        PlaybackSettingsChanged?.Invoke(
            this,
            new DrawingObjectPlaybackSettingsChangedEventArgs(
                _fps.Value,
                SelectedMode(),
                (int)_holdFrame.Value - 1, fields));
    }

    private void UpdateModeState()
    {
        var enabled = SelectedMode() == DrawingObjectPlaybackMode.HoldFrame;
        _holdFrame.Enabled = enabled && Enabled;
        _holdFrameLabel.ForeColor = enabled ? Theme.Muted : Theme.Mix(Theme.Muted, Theme.Panel, 0.55f);
        LayoutFields();
    }

    private void UpdateAlphaPresentation()
    {
        _alphaValue.MixedValue = _alphaMixed;
        _alpha.AccessibleDescription = UiLocalization.T(_alphaMixed
            ? "Symbol alpha percentage, mixed values"
            : $"Symbol alpha percentage, {_alpha.Value}%");
    }

    private void UpdateTintPresentation()
    {
        _tint.SwatchColor = _tintColor;
        _tint.DetailText = _tintMixed
            ? "Mixed"
            : $"#{_tintColor.R:X2}{_tintColor.G:X2}{_tintColor.B:X2}";
        _tint.AccessibleDescription = UiLocalization.T(_tintMixed
            ? "Symbol tint, mixed values"
            : $"Symbol tint {_tint.DetailText}");
    }

    private DrawingObjectPlaybackMode SelectedMode()
    {
        return _playbackMode.SelectedItem is PlaybackModeItem item
            ? item.Mode
            : DrawingObjectPlaybackMode.PlayOnce;
    }

    private static int ModeIndex(DrawingObjectPlaybackMode mode)
    {
        return mode switch
        {
            DrawingObjectPlaybackMode.Loop => 1,
            DrawingObjectPlaybackMode.HoldFrame => 2,
            _ => 0
        };
    }

    private void LayoutFields()
    {
        // Resize can run during base construction, before this panel's children exist.
        if (_title is null || _sectionHeaders[2] is null || _layingOut) return;
        _layingOut = true;
        SuspendLayout();
        try
        {
        var left = ScaleLayout(16);
        var right = Math.Max(left, ClientSize.Width - ScaleLayout(16));
        var contentWidth = Math.Max(1, right - left);
        var fieldHeight = ScaleLayout(Theme.ControlHeight);
        var valueWidth = Math.Max(1, (int)Math.Round(contentWidth * 0.52f));
        var valueLeft = right - valueWidth;
        _title.SetBounds(left, ScaleLayout(PanelTitleTop), contentWidth, ScaleLayout(Theme.InspectorTitleHeight));
        var y = FieldRowTop;
        var tabIndex = 0;
        void Heading(int index)
        {
            _sectionHeaders[index].TabIndex = tabIndex++;
            _sectionHeaders[index].SetBounds(left, ScaleLayout(y), contentWidth, ScaleLayout(36));
            y += 36;
        }
        void Field(int labelIndex, Control editor, bool visible)
        {
            var label = _fieldLabels[labelIndex].Label;
            label.Visible = editor.Visible = visible;
            if (!visible) return;
            editor.TabIndex = tabIndex++;
            label.SetBounds(left, ScaleLayout(y), Math.Max(1, valueLeft - left - ScaleLayout(8)), fieldHeight);
            editor.SetBounds(valueLeft, ScaleLayout(y), valueWidth, fieldHeight);
            y += FieldPitch;
        }
        Heading(0);
        Field(0, _fps, _sections[0].Expanded);
        Field(1, _playbackMode, _sections[0].Expanded);
        Field(2, _holdFrame, _sections[0].Expanded && SelectedMode() == DrawingObjectPlaybackMode.HoldFrame);
        Heading(1);
        Field(3, _anchorX, _sections[1].Expanded);
        Field(4, _anchorY, _sections[1].Expanded);
        Field(7, _restoreSize, _sections[1].Expanded);
        if (_sections[1].Expanded)
            _restoreSize.SetBounds(right - fieldHeight, _restoreSize.Top, fieldHeight, fieldHeight);
        Heading(2);
        Field(5, _alphaValue, _sections[2].Expanded);
        _alpha.Visible = _tint.Visible = _showFilters.Visible = _sections[2].Expanded;
        if (_sections[2].Expanded)
        {
            _alpha.TabIndex = tabIndex++;
            _alpha.MinimumSize = new Size(0, ScaleLayout(24));
            _alpha.SetBounds(left, ScaleLayout(y), contentWidth, fieldHeight);
            y += FieldPitch;
        }
        Field(6, _blendMode, _sections[2].Expanded);
        if (_sections[2].Expanded)
        {
            _tint.TabIndex = tabIndex++;
            _showFilters.TabIndex = tabIndex++;
            _tint.SetBounds(left, ScaleLayout(y), contentWidth, ScaleLayout(TintRowHeight));
            y += TintRowHeight + Theme.GapXs;
            _showFilters.SetBounds(left, ScaleLayout(y), contentWidth, ScaleLayout(FiltersButtonHeight));
            y += FiltersButtonHeight;
        }
        _preferredLogicalHeight = y + 10;
        MinimumSize = new Size(0, PreferredPanelHeight);
        Height = PreferredPanelHeight;
        }
        finally { ResumeLayout(true); _layingOut = false; }
    }

    private int ScaleLayout(int value) => (int)Math.Round(value * DeviceDpi / 96f);

    private static int RowTop(int row) => FieldRowTop + FieldPitch * row;

    private static int RowLabelTop(int row) => RowTop(row) + FieldLabelOffset;

    private static Color Opaque(Color color) => Color.FromArgb(255, color.R, color.G, color.B);

    private sealed class FilterWindow : ModernDialogForm
    {
        public FilterWindow(SymbolFiltersPanel filters) : base("Symbol Filters", new Size(360, 660))
        {
            MinimumSize = new Size(296, 480);
            DialogContent.Padding = Padding.Empty;
            var scroll = new ThemedScrollPanel { Dock = DockStyle.Fill };
            DialogContent.Controls.Add(scroll);
            filters.Dock = DockStyle.Top;
            filters.Height = filters.PreferredPanelHeight;
            scroll.Content.Controls.Add(filters);
            AddDialogAction("Close", DialogResult.OK);
            UiLocalization.Watch(this);
        }
    }

    private Label AddFieldLabel(string text, int top)
    {
        var label = InspectorModuleStyle.CreateFieldLabel(text);
        label.Dock = DockStyle.None;
        label.SetBounds(12, top, 98, Theme.ControlHeight);
        _fieldLabels.Add((label, top));
        Controls.Add(label);
        return label;
    }
}
