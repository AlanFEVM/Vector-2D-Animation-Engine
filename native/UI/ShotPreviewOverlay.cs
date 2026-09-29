namespace VectorAnimationEngine;

/// <summary>
/// Floating shot monitor pinned to the lower-left of the Stage, directly above the timeline.
/// It renders the selected director camera's framed viewport while the Shots workspace is active.
/// The overlay is intentionally independent of <see cref="ShotDirectorPanel"/> so the right
/// inspector keeps only the camera list and numeric camera properties.
/// The user can drag its lower-right grip to resize it; the host re-captures at the new size.
/// </summary>
internal sealed class ShotPreviewOverlay : UserControl
{
    internal const int OverlayWidth = 268;
    internal const int OverlayHeight = 182;
    internal const int OverlayMargin = 12;
    internal const int MinimumOverlayWidth = 160;
    internal const int MinimumOverlayHeight = 120;
    private const int CaptionHeight = 20;
    private const int ChromePadding = 6;
    private const int GripSize = 14;

    private readonly Label _caption = new()
    {
        ForeColor = Theme.Muted,
        BackColor = Theme.Panel,
        AutoEllipsis = true,
        TextAlign = ContentAlignment.MiddleLeft
    };

    private readonly PictureBox _picture = new()
    {
        BackColor = Theme.App,
        SizeMode = PictureBoxSizeMode.Zoom,
        BorderStyle = BorderStyle.FixedSingle,
        Visible = false
    };

    private readonly Label _placeholder = new()
    {
        ForeColor = Theme.Muted,
        BackColor = Theme.App,
        TextAlign = ContentAlignment.MiddleCenter,
        AutoEllipsis = true
    };

    private Bitmap? _bitmap;
    private string _captionText = "";
    private bool _resizing;
    private Point _resizeOrigin;
    private Size _resizeStartSize;

    public ShotPreviewOverlay()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Panel;
        Visible = false;
        TabStop = false;
        // The overlay floats over the Stage and must never steal keyboard focus from drawing or
        // camera-gizmo interaction, so it does not participate in Tab order. It intentionally still
        // receives mouse input in its grip corner so the operator can resize the monitor.
        SetStyle(ControlStyles.Selectable, false);
        _caption.Font = Theme.UiFont(8.6f);
        _placeholder.Font = Theme.UiFont(8.8f);

        Controls.Add(_picture);
        Controls.Add(_placeholder);
        Controls.Add(_caption);
        _placeholder.BringToFront();
    }

    /// <summary>Raised after the user finishes resizing so the host can re-capture at the new size.</summary>
    internal event EventHandler? PreviewResized;

    /// <summary>Caption shown above the monitor. Defaults to the localized "Preview" string.</summary>
    internal string Caption
    {
        get => _captionText;
        set
        {
            var text = value ?? "";
            if (string.Equals(_captionText, text, StringComparison.Ordinal)) return;
            _captionText = text;
            ApplyCaption();
        }
    }

    internal bool HasPreview => _bitmap is not null;

    /// <summary>Requested capture size for the framed viewport, inside the overlay chrome.</summary>
    internal Size PreviewPixelSize
    {
        get
        {
            var width = Math.Max(64, Width - ChromePadding * 2 - 2);
            var height = Math.Max(36, Height - CaptionHeight - ChromePadding * 2 - 2);
            return new Size(width, height);
        }
    }

    /// <summary>
    /// Presents a captured framed viewport. Ownership of <paramref name="bitmap"/> transfers to the
    /// overlay, which disposes the previous frame.
    /// </summary>
    internal void SetPreview(Bitmap? bitmap, string caption = "")
    {
        if (!ReferenceEquals(_bitmap, bitmap))
        {
            _bitmap?.Dispose();
            _bitmap = bitmap;
            _picture.Image = bitmap;
        }

        _captionText = caption ?? "";
        ApplyCaption();
        _picture.Visible = _bitmap is not null;
        _placeholder.Visible = _bitmap is null;
        PerformLayout();
        Invalidate();
    }

    private void ApplyCaption()
    {
        var localized = _captionText.Length == 0 ? UiLocalization.T("Preview") : _captionText;
        if (string.Equals(_caption.Text, localized, StringComparison.Ordinal)) return;
        _caption.Text = localized;
        _caption.AccessibleName = localized;
        _placeholder.Text = localized;
    }

    private Rectangle GripBounds => new(
        Math.Max(0, Width - GripSize),
        Math.Max(0, Height - GripSize),
        GripSize,
        GripSize);

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !GripBounds.Contains(e.Location)) return;
        _resizing = true;
        _resizeOrigin = PointToScreen(e.Location);
        _resizeStartSize = Size;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_resizing)
        {
            // The grip is the only interactive part; show the resize cursor only over it.
            Cursor = GripBounds.Contains(e.Location) ? Cursors.SizeNWSE : Cursors.Default;
            return;
        }

        var current = PointToScreen(e.Location);
        var width = Math.Max(MinimumOverlayWidth, _resizeStartSize.Width + (current.X - _resizeOrigin.X));
        var height = Math.Max(MinimumOverlayHeight, _resizeStartSize.Height + (current.Y - _resizeOrigin.Y));
        Size = new Size(width, height);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_resizing) return;
        _resizing = false;
        Capture = false;
        // One re-capture per completed resize instead of one per mouse-move event.
        PreviewResized?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Current size expressed as a ratio of the default, so a host can persist proportions.</summary>
    internal float ResizeScale => Math.Max(0.1f, (float)Width / OverlayWidth);

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        // Child layout is positioned from the overlay's own size, so a pass raised for a child keeps
        // the same result; re-running it is idempotent and avoids a stale frame after visibility flips.
        var inner = Math.Max(0, Width - ChromePadding * 2);
        _caption.SetBounds(ChromePadding, ChromePadding, inner, CaptionHeight);
        var bodyTop = ChromePadding + CaptionHeight;
        var bodyHeight = Math.Max(1, Height - bodyTop - ChromePadding);
        _picture.SetBounds(ChromePadding, bodyTop, inner, bodyHeight);
        _placeholder.SetBounds(_picture.Bounds.X, _picture.Bounds.Y, _picture.Bounds.Width, _picture.Bounds.Height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var border = new Pen(Theme.Border);
        e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        // Lower-right resize grip: three short diagonal strokes, the conventional desktop affordance.
        var grip = GripBounds;
        using var gripPen = new Pen(Theme.Muted);
        for (var index = 0; index < 3; index++)
        {
            var offset = 3 + index * 4;
            e.Graphics.DrawLine(
                gripPen,
                grip.Right - offset,
                grip.Bottom - 2,
                grip.Right - 2,
                grip.Bottom - offset);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _picture.Image = null;
            _bitmap?.Dispose();
            _bitmap = null;
        }

        base.Dispose(disposing);
    }
}
