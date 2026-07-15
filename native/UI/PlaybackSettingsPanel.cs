namespace VectorAnimationEngine;

internal sealed class PlaybackSettingsPanel : Panel
{
    private readonly ModernNumericUpDown _fps = new()
    {
        Minimum = 1,
        Maximum = 120,
        Value = 30,
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

    public event EventHandler? FpsChanged;
    public event EventHandler? LoopPlaybackChanged;
    public event EventHandler? FrameRangeChanged;
    public event EventHandler? SettingsChanged;

    public PlaybackSettingsPanel()
    {
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Font = Theme.UiFont();
        Padding = new Padding(12);
        MinimumSize = new Size(236, 178);

        BuildUi();
        HookEvents();
    }

    public int Fps
    {
        get => (int)_fps.Value;
        set
        {
            var next = Math.Clamp(value, (int)_fps.Minimum, (int)_fps.Maximum);
            if ((int)_fps.Value == next) return;
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

    public void SetFrameRange(int startFrame, int endFrame)
    {
        if (endFrame < startFrame) (startFrame, endFrame) = (endFrame, startFrame);

        _updating = true;
        try
        {
            _startFrame.Value = Math.Clamp(startFrame, (int)_startFrame.Minimum, (int)_startFrame.Maximum);
            _endFrame.Value = Math.Clamp(endFrame, (int)_endFrame.Minimum, (int)_endFrame.Maximum);
        }
        finally
        {
            _updating = false;
        }

        RaiseFrameRangeChanged();
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        LayoutFields();
    }

    private void BuildUi()
    {
        Controls.Add(new Label
        {
            Text = "Playback",
            Left = 12,
            Top = 10,
            Width = 180,
            Height = 24,
            ForeColor = Theme.Text,
            BackColor = Color.Transparent,
            Font = Theme.UiFont(10, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        });

        AddFieldLabel("FPS", 42);
        AddFieldLabel("Loop", 76);
        AddFieldLabel("Start", 110);
        AddFieldLabel("End", 144);

        Theme.StyleNumeric(_fps);
        Theme.StyleNumeric(_startFrame);
        Theme.StyleNumeric(_endFrame);
        Controls.Add(_fps);
        Controls.Add(_loop);
        Controls.Add(_startFrame);
        Controls.Add(_endFrame);
        LayoutFields();
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

    private void LayoutFields()
    {
        var valueLeft = Math.Max(88, ClientSize.Width - Padding.Right - 96);
        var valueWidth = Math.Max(80, ClientSize.Width - valueLeft - Padding.Right);

        _fps.SetBounds(valueLeft, 40, valueWidth, 28);
        _loop.SetBounds(valueLeft, 72, valueWidth, 28);
        _startFrame.SetBounds(valueLeft, 104, valueWidth, 28);
        _endFrame.SetBounds(valueLeft, 136, valueWidth, 28);
    }

    private void AddFieldLabel(string text, int top)
    {
        Controls.Add(new Label
        {
            Text = text,
            Left = 12,
            Top = top,
            Width = 72,
            Height = 24,
            ForeColor = Theme.Muted,
            BackColor = Color.Transparent,
            Font = Theme.UiFont(),
            TextAlign = ContentAlignment.MiddleLeft
        });
    }

    private void RaiseFrameRangeChanged()
    {
        FrameRangeChanged?.Invoke(this, EventArgs.Empty);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

}
