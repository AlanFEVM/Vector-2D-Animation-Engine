namespace VectorAnimationEngine;

internal sealed class DrawingObjectPlaybackSettingsChangedEventArgs : EventArgs
{
    public DrawingObjectPlaybackSettingsChangedEventArgs(
        int playbackFps,
        DrawingObjectPlaybackMode playbackMode,
        int holdFrame)
    {
        PlaybackFps = playbackFps;
        PlaybackMode = playbackMode;
        HoldFrame = holdFrame;
    }

    public int PlaybackFps { get; }
    public DrawingObjectPlaybackMode PlaybackMode { get; }
    public int HoldFrame { get; }
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
        Height = 180;
        MinimumSize = new Size(248, 180);

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
        AddFieldLabel("Original Size", 144);

        _playbackMode.Items.AddRange([
            new PlaybackModeItem(DrawingObjectPlaybackMode.PlayOnce, "Play Once"),
            new PlaybackModeItem(DrawingObjectPlaybackMode.Loop, "Loop"),
            new PlaybackModeItem(DrawingObjectPlaybackMode.HoldFrame, "Hold Frame")
        ]);
        _playbackMode.SelectedIndex = 0;
        Theme.StyleNumeric(_fps);
        Theme.StyleNumeric(_holdFrame);
        Theme.StyleComboBox(_playbackMode);
        Theme.StyleButton(_restoreSize);
        Theme.StyleToolTip(_toolTip);
        _toolTip.SetToolTip(_restoreSize, "Restore original size");

        Controls.Add(_fps);
        Controls.Add(_playbackMode);
        Controls.Add(_holdFrame);
        Controls.Add(_restoreSize);
        HookEvents();
        LayoutFields();
        UpdateModeState();
    }

    public event EventHandler<DrawingObjectPlaybackSettingsChangedEventArgs>? PlaybackSettingsChanged;
    public event EventHandler? RestoreOriginalSizeRequested;

    public void SetInstance(DrawingObjectInstanceDefinition instance, int sourceFrameCount)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var state = instance.EvaluateState(0);
        SetInstance(state, sourceFrameCount, Math.Abs(state.ScaleX - 1f) > 0.0001f || Math.Abs(state.ScaleY - 1f) > 0.0001f);
    }

    public void SetInstance(InstanceFrameState state, int sourceFrameCount, bool canRestoreSize)
    {
        _updating = true;
        try
        {
            _fps.Value = state.PlaybackFps;
            _playbackMode.SelectedIndex = ModeIndex(state.PlaybackMode);
            _holdFrame.Maximum = Math.Max(0, sourceFrameCount - 1);
            _holdFrame.Value = Math.Min(state.HoldFrame, (int)_holdFrame.Maximum);
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
        _restoreSize.Click += (_, _) => RestoreOriginalSizeRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RaisePlaybackSettingsChanged()
    {
        if (_updating) return;
        PlaybackSettingsChanged?.Invoke(
            this,
            new DrawingObjectPlaybackSettingsChangedEventArgs(
                (int)_fps.Value,
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
        _restoreSize.SetBounds(
            ClientSize.Width - Padding.Right - Theme.ControlHeightCompact,
            142,
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
