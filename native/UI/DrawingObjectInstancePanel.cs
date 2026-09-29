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
    private const int PanelHeight = 332;
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
        MinimumSize = new Size(248, PreferredPanelHeight);
        Controls.Add(_showFilters);
        Theme.StyleButton(_showFilters);
        _showFilters.Click += (_, _) => ShowFiltersWindow();

        Controls.Add(new Label
        {
            Text = "Symbol Instance",
            Left = 12,
            Top = 8,
            Width = 210,
            Height = 26,
            ForeColor = Theme.Text,
            BackColor = Color.Transparent,
            Font = Theme.UiFont(10, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        });

        AddFieldLabel("Playback FPS", 42);
        AddFieldLabel("Mode", 76);
        _holdFrameLabel = AddFieldLabel("Hold Frame", 110);
        AddFieldLabel("Anchor X", 144);
        AddFieldLabel("Anchor Y", 178);
        AddFieldLabel("Alpha", 212);
        AddFieldLabel("Original Size", 296);

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
        Theme.StyleButton(_restoreSize);
        Theme.StyleToolTip(_toolTip);
        _toolTip.SetToolTip(_alpha, "Adjust symbol alpha");
        _toolTip.SetToolTip(_tint, "Choose symbol tint");
        _toolTip.SetToolTip(_restoreSize, "Restore original size");

        Controls.Add(_fps);
        Controls.Add(_playbackMode);
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
    public event EventHandler? AppearanceInteractionStarted;
    public event EventHandler? AppearanceInteractionCompleted;
    public event EventHandler? AppearanceInteractionCanceled;
    public event EventHandler? RestoreOriginalSizeRequested;

    public int PreferredPanelHeight => PanelHeight + 40;

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
        bool filtersMixed = false)
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
        var startedMixed = _tintMixed;
        var changed = false;
        AppearanceInteractionStarted?.Invoke(this, EventArgs.Empty);
        picker.ColorChanged += (_, _) =>
        {
            _tintColor = Opaque(picker.Color);
            _tintMixed = false;
            changed = true;
            UpdateTintPresentation();
            RaiseAppearanceChanged(alphaChanged: false, tintChanged: true);
        };

        if (picker.ShowDialog(FindForm()) == DialogResult.OK)
        {
            if (startedMixed && !changed)
            {
                _tintMixed = false;
                UpdateTintPresentation();
                RaiseAppearanceChanged(alphaChanged: false, tintChanged: true);
            }
            AppearanceInteractionCompleted?.Invoke(this, EventArgs.Empty);
            return;
        }

        _tintColor = picker.InitialColor;
        _tintMixed = startedMixed;
        UpdateTintPresentation();
        AppearanceInteractionCanceled?.Invoke(this, EventArgs.Empty);
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
        _showFilters.SetBounds(12, PanelHeight, Math.Max(1, ClientSize.Width - 24), 30);
        var valueLeft = Math.Max(112, ClientSize.Width - Padding.Right - 136);
        var valueWidth = Math.Max(96, ClientSize.Width - valueLeft - Padding.Right);
        _fps.SetBounds(valueLeft, 40, valueWidth, Theme.ControlHeightCompact);
        _playbackMode.SetBounds(valueLeft, 74, valueWidth, Theme.ControlHeightCompact);
        _holdFrame.SetBounds(valueLeft, 108, valueWidth, Theme.ControlHeightCompact);
        _anchorX.SetBounds(valueLeft, 142, valueWidth, Theme.ControlHeightCompact);
        _anchorY.SetBounds(valueLeft, 176, valueWidth, Theme.ControlHeightCompact);
        var alphaValueWidth = 44;
        var alphaGap = 4;
        _alpha.SetBounds(
            valueLeft,
            210,
            Math.Max(72, valueWidth - alphaValueWidth - alphaGap),
            Theme.ControlHeightCompact);
        _alphaValue.SetBounds(
            _alpha.Right + alphaGap,
            210,
            Math.Max(0, valueLeft + valueWidth - _alpha.Right - alphaGap),
            Theme.ControlHeightCompact);
        _tint.SetBounds(Padding.Left, 244, Math.Max(96, ClientSize.Width - Padding.Horizontal), 42);
        _restoreSize.SetBounds(
            ClientSize.Width - Padding.Right - Theme.ControlHeightCompact,
            294,
            Theme.ControlHeightCompact,
            Theme.ControlHeightCompact);
    }

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
        Controls.Add(label);
        return label;
    }
}
