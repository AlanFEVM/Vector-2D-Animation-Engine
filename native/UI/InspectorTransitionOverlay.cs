using System.Diagnostics;
using System.Drawing.Imaging;

namespace VectorAnimationEngine;

/// <summary>Short-lived presentation overlay; real inspector controls retain their state and events.</summary>
internal sealed class InspectorTransitionOverlay : Control
{
    private const double DurationMilliseconds = 180;
    private readonly Control _host;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
    private Bitmap? _previous;
    private Bitmap? _target;
    private long _started;
    private float _progress;

    public InspectorTransitionOverlay(Control host)
    {
        _host = host;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        TabStop = false;
        Visible = false;
        _timer.Tick += HandleTick;
        _host.SizeChanged += HandleHostChanged;
        _host.VisibleChanged += HandleHostChanged;
        _host.Controls.Add(this);
    }

    public bool Begin()
    {
        if (!UiMotion.AnimationsEnabled || !_host.Visible || !_host.IsHandleCreated
            || _host.ClientSize.Width <= 0 || _host.ClientSize.Height <= 0)
        {
            Cancel();
            return false;
        }
        // A second switch starts from the currently displayed transition, rather than queuing.
        Bitmap? snapshot;
        if (Visible) snapshot = Capture(this);
        else
        {
            // Hidden controls can still participate in DrawToBitmap; exclude this overlay explicitly.
            _host.Controls.Remove(this);
            try { snapshot = Capture(_host); }
            finally { if (!_host.IsDisposed) _host.Controls.Add(this); }
        }
        Cancel();
        if (snapshot is null) return false;
        _previous = snapshot;
        Bounds = _host.ClientRectangle;
        BackColor = _host.BackColor;
        Visible = true;
        BringToFront();
        Invalidate();
        return true;
    }

    public void Complete()
    {
        if (_previous is null) return;
        // Remove the overlay during capture so DrawToBitmap sees only the real destination controls.
        _host.Controls.Remove(this);
        Bitmap? snapshot;
        try { snapshot = Capture(_host); }
        finally
        {
            if (!_host.IsDisposed) _host.Controls.Add(this);
        }
        if (snapshot is null || _previous is null || snapshot.Size != _previous.Size || !UiMotion.AnimationsEnabled)
        {
            snapshot?.Dispose();
            Cancel();
            return;
        }
        _target = snapshot;
        _progress = 0;
        _started = Stopwatch.GetTimestamp();
        Bounds = _host.ClientRectangle;
        Visible = true;
        BringToFront();
        _timer.Start();
        Invalidate();
    }

    public void Cancel()
    {
        _timer.Stop();
        Visible = false;
        _previous?.Dispose();
        _target?.Dispose();
        _previous = null;
        _target = null;
        _progress = 0;
    }

    private static Bitmap? Capture(Control control)
    {
        var size = control.ClientSize;
        if (size.Width <= 0 || size.Height <= 0 || (long)size.Width * size.Height > 8_000_000)
            return null;
        Bitmap? bitmap = null;
        try
        {
            bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            bitmap.SetResolution(control.DeviceDpi, control.DeviceDpi);
            control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, size));
            return bitmap;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or System.Runtime.InteropServices.ExternalException)
        {
            bitmap?.Dispose();
            return null;
        }
    }

    private void HandleTick(object? sender, EventArgs e)
    {
        if (!UiMotion.AnimationsEnabled || !_host.Visible || _host.ClientSize != Size)
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
        if (_previous is null) return;
        var eased = 1f - MathF.Pow(1f - _progress, 3f);
        DrawSnapshot(e.Graphics, _previous, 1f - eased, 0);
        if (_target is not null)
            DrawSnapshot(e.Graphics, _target, eased,
                (int)Math.Round(6f * DeviceDpi / 96f * (1f - eased)));
    }

    private static void DrawSnapshot(Graphics graphics, Bitmap bitmap, float opacity, int offsetY)
    {
        if (opacity <= 0) return;
        using var attributes = new ImageAttributes();
        var matrix = new ColorMatrix { Matrix33 = opacity };
        attributes.SetColorMatrix(matrix);
        graphics.DrawImage(bitmap, new Rectangle(0, offsetY, bitmap.Width, bitmap.Height),
            0, 0, bitmap.Width, bitmap.Height, GraphicsUnit.Pixel, attributes);
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
