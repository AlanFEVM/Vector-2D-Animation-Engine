namespace VectorAnimationEngine;

internal sealed class DrawingObjectPlaybackSettingsChangedEventArgs : EventArgs
{
    public DrawingObjectPlaybackSettingsChangedEventArgs(
        decimal playbackFps,
        DrawingObjectPlaybackMode playbackMode,
        int holdFrame)
    {
        PlaybackFps = playbackFps;
        PlaybackMode = playbackMode;
        HoldFrame = holdFrame;
    }

    public decimal PlaybackFps { get; }
    public DrawingObjectPlaybackMode PlaybackMode { get; }
    public int HoldFrame { get; }
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
    private const int FieldPitch = Theme.InspectorRowHeight;
    private const int FieldLabelOffset = 2;
    private const int FieldRowTop = PanelTitleTop + Theme.InspectorTitleHeight + Theme.GapXs;
    private const int FieldRowCount = 7;
    private const int TintRowTop = FieldRowTop + FieldPitch * FieldRowCount;
    private const int TintRowHeight = 42;
    private const int RestoreButtonTop = TintRowTop + TintRowHeight + Theme.GapXs;
    private const int FiltersButtonHeight = 30;
    private const int PanelHeight = RestoreButtonTop + Theme.ControlHeightCompact + Theme.GapXs;
    private readonly Label _title;
    private readonly List<(Label Label, int Top)> _fieldLabels = [];
    private readonly SymbolFiltersPanel _filters = new();
    private readonly Button _showFilters = new() { Text = "Symbol Filters", AccessibleName = "Symbol Filters" };
    private Form? _filtersWindow;
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
        Minimum = 0,
        Maximum = 999999,
        Value = 0,
        AccessibleName = "Symbol hold frame"
    };
    private readonly ModernNumericUpDown _anchorX = new()
    {
        Minimum = -5_000_000,
        Maximum = 5_000_000,
        Increment = 10,
        Suffix = "vu",
        AccessibleName = "Symbol anchor X"
    };
    private readonly ModernNumericUpDown _anchorY = new()
    {
        Minimum = -5_000_000,
        Maximum = 5_000_000,
        Increment = 10,
        Suffix = "vu",
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
    private readonly Label _alphaValue = new()
    {
        Text = "100%",
        ForeColor = Theme.Text,
        BackColor = Color.Transparent,
        TextAlign = ContentAlignment.MiddleRight,
        AutoEllipsis = true,
        AccessibleName = "Symbol alpha value",
        AccessibleRole = AccessibleRole.StaticText
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
    private bool _alphaInteractionChanged;
    private Color _tintColor = Color.White;

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
        AddFieldLabel("Anchor X", RowLabelTop(3));
        AddFieldLabel("Anchor Y", RowLabelTop(4));
        AddFieldLabel("Alpha", RowLabelTop(5));
        AddFieldLabel("Blend Mode", RowLabelTop(6));
        AddFieldLabel("Original Size", RestoreButtonTop + FieldLabelOffset);

        _playbackMode.Items.AddRange([
            new PlaybackModeItem(DrawingObjectPlaybackMode.PlayOnce, "Play Once"),
            new PlaybackModeItem(DrawingObjectPlaybackMode.Loop, "Loop"),
            new PlaybackModeItem(DrawingObjectPlaybackMode.HoldFrame, "Hold Frame")
        ]);
        _playbackMode.SelectedIndex = 0;
        Theme.StyleNumeric(_fps);
        Theme.StyleNumeric(_holdFrame);
        Theme.StyleNumeric(_anchorX);
        Theme.StyleNumeric(_anchorY);
        Theme.StyleComboBox(_playbackMode);
        Theme.StyleComboBox(_blendMode);
        foreach (var item in LayerBlendModePanel.BlendModes) _blendMode.Items.Add(new BlendModeItem(item.Mode, item.Label));
        _blendMode.SelectedIndex = 0;
        Theme.StyleButton(_restoreSize);
        Theme.StyleToolTip(_toolTip);
        _toolTip.SetToolTip(_alpha, UiLocalization.T("Adjust symbol alpha"));
        _toolTip.SetToolTip(_tint, UiLocalization.T("Choose symbol tint"));
        _toolTip.SetToolTip(_restoreSize, UiLocalization.T("Restore original size"));

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

    public int PreferredPanelHeight => ScaleLayout(PanelHeight + FiltersButtonHeight + 10);

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
        bool blendModeMixed = false)
    {
        _updating = true;
        try
        {
            _fps.Value = state.PlaybackFps;
            _playbackMode.SelectedIndex = ModeIndex(state.PlaybackMode);
            _holdFrame.Maximum = Math.Max(0, sourceFrameCount - 1);
            _holdFrame.Value = Math.Min(state.HoldFrame, (int)_holdFrame.Maximum);
            _anchorX.Value = (decimal)sourceAnchor.X;
            _anchorY.Value = (decimal)sourceAnchor.Y;
            _anchorX.Enabled = canEditAnchor;
            _anchorY.Enabled = canEditAnchor;
            _restoreSize.Enabled = canRestoreOriginalTransform;
            _alpha.Value = (int)Math.Clamp(MathF.Round(state.Alpha * 100f), 0f, 100f);
            _tintColor = Opaque(Color.FromArgb(state.TintArgb));
            _alphaMixed = alphaMixed;
            _tintMixed = tintMixed;
            _filters.SetFilters(state.Filters, filtersMixed);
            _blendMode.SelectedIndex = blendModeMixed ? -1 : Array.FindIndex(LayerBlendModePanel.BlendModes, item => item.Mode == state.BlendMode);
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
            _filtersWindow = new Form
            {
                Text = UiLocalization.T("Symbol Filters"), FormBorderStyle = FormBorderStyle.SizableToolWindow,
                ShowInTaskbar = false, StartPosition = FormStartPosition.CenterParent,
                ClientSize = new Size(330, _filters.PreferredPanelHeight),
                MinimumSize = new Size(280, 430), BackColor = Theme.Panel
            };
            _filters.Dock = DockStyle.Fill;
            _filtersWindow.Controls.Add(_filters);
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
        _fps.ValueChanged += (_, _) => RaisePlaybackSettingsChanged();
        _holdFrame.ValueChanged += (_, _) => RaisePlaybackSettingsChanged();
        _playbackMode.SelectedIndexChanged += (_, _) =>
        {
            UpdateModeState();
            RaisePlaybackSettingsChanged();
        };
        _anchorX.ValueChanged += (_, _) => RaiseAnchorChanged();
        _anchorY.ValueChanged += (_, _) => RaiseAnchorChanged();
        _alpha.InteractionStarted += (_, _) => BeginAlphaInteraction();
        _alpha.ValueChanged += (_, _) => ApplyAlphaValue();
        _alpha.InteractionCompleted += (_, _) => CompleteAlphaInteraction();
        _alpha.InteractionCanceled += (_, _) => CancelAlphaInteraction();
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
        _alphaInteractionChanged = false;
        AppearanceInteractionStarted?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyAlphaValue()
    {
        if (_updating) return;
        _alphaMixed = false;
        _alphaInteractionChanged = true;
        UpdateAlphaPresentation();
        RaiseAppearanceChanged(alphaChanged: true, tintChanged: false);
    }

    private void CompleteAlphaInteraction()
    {
        if (!_alphaInteractionActive) return;
        if (_alphaInteractionStartedMixed && !_alphaInteractionChanged)
        {
            _alphaMixed = false;
            UpdateAlphaPresentation();
            RaiseAppearanceChanged(alphaChanged: true, tintChanged: false);
        }

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
        _alphaInteractionChanged = false;
    }

    private void ChooseTint()
    {
        using var picker = new ProfessionalColorPickerDialog(_tintColor, "Symbol tint");
        AppearanceInteractionStarted?.Invoke(this, EventArgs.Empty);
        picker.ColorChanged += (_, _) =>
        {
            _tintColor = Opaque(picker.Color);
            _tintMixed = false;
            UpdateTintPresentation();
            RaiseAppearanceChanged(alphaChanged: false, tintChanged: true);
        };

        try { picker.ShowDialog(FindForm()); }
        finally
        {
            AppearanceInteractionCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RaiseAppearanceChanged(bool alphaChanged, bool tintChanged)
    {
        if (_updating) return;
        AppearanceChanged?.Invoke(
            this,
            new DrawingObjectAppearanceChangedEventArgs(
                _alpha.Value / 100f,
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

    private void RaisePlaybackSettingsChanged()
    {
        if (_updating) return;
        PlaybackSettingsChanged?.Invoke(
            this,
            new DrawingObjectPlaybackSettingsChangedEventArgs(
                _fps.Value,
                SelectedMode(),
                (int)_holdFrame.Value));
    }

    private void UpdateModeState()
    {
        var enabled = SelectedMode() == DrawingObjectPlaybackMode.HoldFrame;
        _holdFrame.Enabled = enabled;
        _holdFrameLabel.ForeColor = enabled ? Theme.Muted : Theme.Mix(Theme.Muted, Theme.Panel, 0.55f);
    }

    private void UpdateAlphaPresentation()
    {
        _alphaValue.Text = _alphaMixed ? "Mixed" : $"{_alpha.Value}%";
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
        if (_title is null) return;
        var left = ScaleLayout(12);
        var right = Math.Max(left, ClientSize.Width - ScaleLayout(12));
        var contentWidth = Math.Max(1, right - left);
        var fieldHeight = ScaleLayout(Theme.ControlHeightCompact);
        var valueWidth = Math.Min(ScaleLayout(136),
            Math.Max(1, contentWidth - Math.Min(ScaleLayout(100), contentWidth / 2)));
        var valueLeft = right - valueWidth;
        _title.SetBounds(left, ScaleLayout(PanelTitleTop), contentWidth, ScaleLayout(Theme.InspectorTitleHeight));
        foreach (var (label, top) in _fieldLabels)
        {
            label.SetBounds(left, ScaleLayout(top),
                Math.Max(0, valueLeft - left - ScaleLayout(2)), ScaleLayout(24));
        }
        _showFilters.SetBounds(
            left,
            ScaleLayout(PanelHeight),
            contentWidth,
            ScaleLayout(FiltersButtonHeight));
        _fps.SetBounds(valueLeft, ScaleLayout(RowTop(0)), valueWidth, fieldHeight);
        _playbackMode.SetBounds(valueLeft, ScaleLayout(RowTop(1)), valueWidth, fieldHeight);
        _holdFrame.SetBounds(valueLeft, ScaleLayout(RowTop(2)), valueWidth, fieldHeight);
        _anchorX.SetBounds(valueLeft, ScaleLayout(RowTop(3)), valueWidth, fieldHeight);
        _anchorY.SetBounds(valueLeft, ScaleLayout(RowTop(4)), valueWidth, fieldHeight);
        _blendMode.SetBounds(valueLeft, ScaleLayout(RowTop(6)), valueWidth, fieldHeight);
        var alphaValueWidth = Math.Min(ScaleLayout(44), valueWidth / 2);
        var alphaGap = Math.Min(ScaleLayout(4), Math.Max(0, valueWidth - alphaValueWidth - 1));
        var alphaTop = ScaleLayout(RowTop(5));
        // This inspector shares its value column with the percentage label; the slider's
        // standalone 72px minimum would otherwise push that label out of a narrow column.
        _alpha.MinimumSize = new Size(0, ScaleLayout(24));
        _alpha.SetBounds(
            valueLeft,
            alphaTop,
            Math.Max(1, valueWidth - alphaValueWidth - alphaGap),
            fieldHeight);
        _alphaValue.SetBounds(
            _alpha.Right + alphaGap,
            alphaTop,
            Math.Max(0, valueLeft + valueWidth - _alpha.Right - alphaGap),
            fieldHeight);
        _tint.SetBounds(left, ScaleLayout(TintRowTop), contentWidth, ScaleLayout(TintRowHeight));
        _restoreSize.SetBounds(
            Math.Max(left, right - fieldHeight),
            ScaleLayout(RestoreButtonTop),
            Math.Min(contentWidth, fieldHeight),
            fieldHeight);
    }

    private int ScaleLayout(int value) => (int)Math.Round(value * DeviceDpi / 96f);

    private static int RowTop(int row) => FieldRowTop + FieldPitch * row;

    private static int RowLabelTop(int row) => RowTop(row) + FieldLabelOffset;

    private static Color Opaque(Color color) => Color.FromArgb(255, color.R, color.G, color.B);

    private Label AddFieldLabel(string text, int top)
    {
        var label = new Label
        {
            Text = text,
            Left = 12,
            Top = top,
            Width = 98,
            Height = 24,
            ForeColor = Theme.Muted,
            BackColor = Color.Transparent,
            Font = Theme.UiFont(),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };
        _fieldLabels.Add((label, top));
        Controls.Add(label);
        return label;
    }
}
