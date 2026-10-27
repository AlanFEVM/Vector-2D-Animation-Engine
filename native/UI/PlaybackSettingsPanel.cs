namespace VectorAnimationEngine;

internal sealed class PlaybackSettingsPanel : Panel
{
    private readonly ModernNumericUpDown _fps = new()
    {
        Minimum = 1,
        Maximum = 120,
        Value = 30,
        DecimalPlaces = 3,
        Increment = 0.001m,
        WheelAdjustsHoveredDigit = true,
        Suffix = "fps",
        Width = 88,
        Height = 28
    };

    private readonly ModernToggleSwitch _loop = new()
    {
        Text = "Loop",
        Checked = true,
        AutoSize = false,
        Width = 88,
        Height = 28,
        TextAlign = ContentAlignment.MiddleLeft
    };

    private readonly ModernNumericUpDown _startFrame = new()
    {
        Minimum = 0,
        Maximum = 999999,
        Value = 0,
        Width = 88,
        Height = 28
    };

    private readonly ModernNumericUpDown _endFrame = new()
    {
        Minimum = 0,
        Maximum = 999999,
        Value = 239,
        Width = 88,
        Height = 28
    };

    private bool _updating;
    private readonly Label _title = InspectorModuleStyle.CreateTitle("Playback", primary: true);
    private readonly Label _fpsCaption = Caption("Frame rate");
    private readonly Label _modeCaption = Caption("Playback mode");
    private readonly Label _rangeCaption = Caption("Playback range");
    private readonly Label _startCaption = Caption("Start");
    private readonly Label _endCaption = Caption("End");

    public event EventHandler? FpsChanged;
    public event EventHandler? LoopPlaybackChanged;
    public event EventHandler? FrameRangeChanged;
    public event EventHandler? SettingsChanged;

    public PlaybackSettingsPanel()
    {
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Font = Theme.UiFont();
        Padding = Padding.Empty;
        MinimumSize = new Size(0, 220);

        BuildUi();
        HookEvents();
    }

    public decimal Fps
    {
        get => _fps.Value;
        set
        {
            var next = Math.Clamp(value, _fps.Minimum, _fps.Maximum);
            if (_fps.Value == next) return;
            _fps.Value = next;
        }
    }

    public bool LoopPlayback
    {
        get => _loop.Checked;
        set
        {
            if (_loop.Checked == value) return;
            _loop.Checked = value;
        }
    }

    public int StartFrame
    {
        get => (int)_startFrame.Value;
        set
        {
            var next = Math.Clamp(value, (int)_startFrame.Minimum, (int)_startFrame.Maximum);
            if (next > EndFrame) EndFrame = next;
            if ((int)_startFrame.Value == next) return;
            _startFrame.Value = next;
        }
    }

    public int EndFrame
    {
        get => (int)_endFrame.Value;
        set
        {
            var next = Math.Clamp(value, (int)_endFrame.Minimum, (int)_endFrame.Maximum);
            if (next < StartFrame) StartFrame = next;
            if ((int)_endFrame.Value == next) return;
            _endFrame.Value = next;
        }
    }

    public void SetFrameRange(int startFrame, int endFrame, bool notifyChanged = true)
    {
        if (endFrame < startFrame) (startFrame, endFrame) = (endFrame, startFrame);
        var nextStartFrame = Math.Clamp(startFrame, (int)_startFrame.Minimum, (int)_startFrame.Maximum);
        var nextEndFrame = Math.Clamp(endFrame, (int)_endFrame.Minimum, (int)_endFrame.Maximum);
        if ((int)_startFrame.Value == nextStartFrame && (int)_endFrame.Value == nextEndFrame) return;

        _updating = true;
        try
        {
            _startFrame.Value = nextStartFrame;
            _endFrame.Value = nextEndFrame;
        }
        finally
        {
            _updating = false;
        }

        if (notifyChanged) RaiseFrameRangeChanged();
    }

    public int PreferredPanelHeight => (int)Math.Round(244 * DeviceDpi / 96f);

    private void BuildUi()
    {
        Theme.StyleNumeric(_fps);
        Theme.StyleNumeric(_startFrame);
        Theme.StyleNumeric(_endFrame);
        foreach (var input in new[] { _fps, _startFrame, _endFrame })
        {
            input.Font = Theme.UiFont(11f);
        }
        _loop.Text = "Loop";
        _loop.AccessibleName = "Loop";
        _fps.ShowSuffix = true;
        _fps.TabIndex = 0;
        _loop.TabIndex = 1;
        _startFrame.TabIndex = 2;
        _endFrame.TabIndex = 3;
        _title.Dock = DockStyle.None;
        Controls.AddRange([_title, _fpsCaption, _modeCaption, _rangeCaption, _startCaption,
            _endCaption, _fps, _loop, _startFrame, _endFrame]);
        UiLocalization.Watch(this);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int Scale(int value) => (int)Math.Round(value * DeviceDpi / 96f);
        var left = Scale(16);
        var width = Math.Max(1, Width - left * 2);
        var gap = Scale(16);
        var half = Math.Max(1, (width - gap) / 2);
        var second = left + half + gap;
        _title.SetBounds(left, Scale(18), width, Scale(26));
        _fpsCaption.SetBounds(left, Scale(58), half, Scale(24));
        _modeCaption.SetBounds(second, Scale(58), half, Scale(24));
        _fps.SetBounds(left, Scale(86), half, Scale(Theme.ControlHeight));
        _loop.SetBounds(second, Scale(86), half, Scale(Theme.ControlHeight));
        _rangeCaption.SetBounds(left, Scale(140), width, Scale(24));
        _startCaption.SetBounds(left, Scale(170), half, Scale(24));
        _endCaption.SetBounds(second, Scale(170), half, Scale(24));
        _startFrame.SetBounds(left, Scale(198), half, Scale(Theme.ControlHeight));
        _endFrame.SetBounds(second, Scale(198), half, Scale(Theme.ControlHeight));
    }

    private static Label Caption(string text)
    {
        var label = InspectorModuleStyle.CreateFieldLabel(text);
        label.Dock = DockStyle.None;
        label.BackColor = Color.Transparent;
        return label;
    }

    private void HookEvents()
    {
        _fps.ValueChanged += (_, _) =>
        {
            if (_updating) return;
            FpsChanged?.Invoke(this, EventArgs.Empty);
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        };

        _loop.CheckedChanged += (_, _) =>
        {
            if (_updating) return;
            LoopPlaybackChanged?.Invoke(this, EventArgs.Empty);
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        };

        _startFrame.ValueChanged += (_, _) =>
        {
            if (_updating) return;
            if (_startFrame.Value > _endFrame.Value)
            {
                _updating = true;
                _endFrame.Value = _startFrame.Value;
                _updating = false;
            }

            RaiseFrameRangeChanged();
        };

        _endFrame.ValueChanged += (_, _) =>
        {
            if (_updating) return;
            if (_endFrame.Value < _startFrame.Value)
            {
                _updating = true;
                _startFrame.Value = _endFrame.Value;
                _updating = false;
            }

            RaiseFrameRangeChanged();
        };
    }

    private void RaiseFrameRangeChanged()
    {
        FrameRangeChanged?.Invoke(this, EventArgs.Empty);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

}
