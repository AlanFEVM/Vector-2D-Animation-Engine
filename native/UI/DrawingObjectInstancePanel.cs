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

internal sealed class DrawingObjectInstancePanel : Panel
{
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
        AccessibleName = "Drawing object playback FPS"
    };
    private readonly ComboBox _playbackMode = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        AccessibleName = "Drawing object playback mode"
    };
    private readonly ModernNumericUpDown _holdFrame = new()
    {
        Minimum = 0,
        Maximum = 999999,
        Value = 0,
        AccessibleName = "Drawing object hold frame"
    };
    private readonly ModernNumericUpDown _anchorX = new()
    {
        Minimum = -5_000_000,
        Maximum = 5_000_000,
        Increment = 10,
        Suffix = "vu",
        AccessibleName = "Drawing object anchor X"
    };
    private readonly ModernNumericUpDown _anchorY = new()
    {
        Minimum = -5_000_000,
        Maximum = 5_000_000,
        Increment = 10,
        Suffix = "vu",
        AccessibleName = "Drawing object anchor Y"
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

    public DrawingObjectInstancePanel()
    {
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Font = Theme.UiFont();
        Padding = new Padding(12, 8, 12, 10);
        Height = 248;
        MinimumSize = new Size(248, 248);

        Controls.Add(new Label
        {
            Text = "Drawing Object",
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
        AddFieldLabel("Original Size", 212);

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
        _toolTip.SetToolTip(_restoreSize, "Restore original size");

        Controls.Add(_fps);
        Controls.Add(_playbackMode);
        Controls.Add(_holdFrame);
        Controls.Add(_anchorX);
        Controls.Add(_anchorY);
        Controls.Add(_restoreSize);
        HookEvents();
        LayoutFields();
        UpdateModeState();
    }

    public event EventHandler<DrawingObjectPlaybackSettingsChangedEventArgs>? PlaybackSettingsChanged;
    public event EventHandler<DrawingObjectAnchorChangedEventArgs>? AnchorChanged;
    public event EventHandler? RestoreOriginalSizeRequested;

    public void SetInstance(
        DrawingObjectInstanceDefinition instance,
        int sourceFrameCount,
        PointF sourceAnchor,
        bool canEditAnchor = true)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var state = instance.EvaluateState(0);
        SetInstance(
            state,
            sourceFrameCount,
            Math.Abs(state.ScaleX - 1f) > 0.0001f || Math.Abs(state.ScaleY - 1f) > 0.0001f,
            sourceAnchor,
            canEditAnchor);
    }

    public void SetInstance(
        InstanceFrameState state,
        int sourceFrameCount,
        bool canRestoreSize,
        PointF sourceAnchor,
        bool canEditAnchor)
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
            _restoreSize.Enabled = canRestoreSize;
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
        if (disposing) _toolTip.Dispose();
        base.Dispose(disposing);
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
        _restoreSize.Click += (_, _) => RestoreOriginalSizeRequested?.Invoke(this, EventArgs.Empty);
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
        var valueLeft = Math.Max(112, ClientSize.Width - Padding.Right - 136);
        var valueWidth = Math.Max(96, ClientSize.Width - valueLeft - Padding.Right);
        _fps.SetBounds(valueLeft, 40, valueWidth, Theme.ControlHeightCompact);
        _playbackMode.SetBounds(valueLeft, 74, valueWidth, Theme.ControlHeightCompact);
        _holdFrame.SetBounds(valueLeft, 108, valueWidth, Theme.ControlHeightCompact);
        _anchorX.SetBounds(valueLeft, 142, valueWidth, Theme.ControlHeightCompact);
        _anchorY.SetBounds(valueLeft, 176, valueWidth, Theme.ControlHeightCompact);
        _restoreSize.SetBounds(
            ClientSize.Width - Padding.Right - Theme.ControlHeightCompact,
            210,
            Theme.ControlHeightCompact,
            Theme.ControlHeightCompact);
    }

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
