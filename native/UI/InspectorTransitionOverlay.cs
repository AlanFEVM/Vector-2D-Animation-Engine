using System.Diagnostics;

namespace VectorAnimationEngine;

/// <summary>A lightweight category-change cue that never snapshots the inspector controls.</summary>
internal sealed class InspectorTransitionOverlay : Control
{
    private const double DurationMilliseconds = 180;
    private readonly Control _host;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
    private long _started;
    private float _progress;

    public InspectorTransitionOverlay(Control host)
    {
        _host = host;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer, true);
        TabStop = false;
        Enabled = false;
        Visible = false;
        _timer.Tick += HandleTick;
        _host.SizeChanged += HandleHostChanged;
        _host.VisibleChanged += HandleHostChanged;
        _host.Controls.Add(this);
    }

    public bool IsPreparing { get; private set; }

    public bool Begin()
    {
        Cancel();
        if (!UiMotion.AnimationsEnabled || !_host.Visible || !_host.IsHandleCreated
            || _host.ClientSize.Width <= 0 || _host.ClientSize.Height <= 0) return false;
        IsPreparing = true;
        return true;
    }

    public void Complete()
    {
        if (!IsPreparing) return;
        IsPreparing = false;
        if (!UiMotion.AnimationsEnabled || !_host.Visible) return;
        _progress = 0;
        _started = Stopwatch.GetTimestamp();
        Bounds = new Rectangle(0, 0, _host.ClientSize.Width, Math.Max(1, (int)Math.Round(2 * DeviceDpi / 96f)));
        BackColor = _host.BackColor;
        Visible = true;
        BringToFront();
        _timer.Start();
        Invalidate();
    }

    public void Cancel()
    {
        _timer.Stop();
        Visible = false;
        IsPreparing = false;
        _progress = 0;
    }

    private void HandleTick(object? sender, EventArgs e)
    {
        if (!UiMotion.AnimationsEnabled || !_host.Visible || _host.ClientSize.Width != Width)
        {
            Cancel();
            return;
        }
        _progress = (float)Math.Clamp(Stopwatch.GetElapsedTime(_started).TotalMilliseconds
            / DurationMilliseconds, 0, 1);
        if (_progress >= 1) Cancel();
        else Invalidate();
    }

    private void HandleHostChanged(object? sender, EventArgs e) => Cancel();

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(_host.BackColor);
        using var cue = new SolidBrush(Color.FromArgb((int)(160 * (1f - _progress)), Theme.Accent));
        e.Graphics.FillRectangle(cue, 0, 0, Width, Height);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _host.SizeChanged -= HandleHostChanged;
            _host.VisibleChanged -= HandleHostChanged;
            Cancel();
            _timer.Dispose();
        }
        base.Dispose(disposing);
    }
}
