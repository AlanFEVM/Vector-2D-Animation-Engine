using System.Drawing.Drawing2D;

namespace VectorAnimationEngine;

internal sealed class TimelineStrip : Control
{
    private const int GutterWidth = 214;
    private const int HeaderHeight = 34;
    private const int RulerHeight = 30;
    private const int RowHeight = 22;
    private const int RowGap = 2;
    private const int TrackPadding = 10;
    private const int HandleWidth = 13;
    private const int VisibilityColumnWidth = 28;
    private const int SoloButtonWidth = 58;
    private const int AllButtonWidth = 52;

    private readonly VectorScene _scene;
    private bool _draggingPlayhead;
    private int _currentFrame;
    private int _startFrame;
    private int _endFrame;

    public event EventHandler? CurrentFrameChanged;
    public event EventHandler? ActiveLayerChanged;
    public event EventHandler? LayerVisibilityChanged;

    public TimelineStrip(VectorScene scene)
    {
        _scene = scene;
        _endFrame = Math.Max(0, scene.FrameCount - 1);

        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = Theme.Top;
        ForeColor = Theme.Text;
        Font = Theme.UiFont(9);
        Cursor = Cursors.Hand;
        MinimumSize = new Size(360, 118);
    }

    public int CurrentFrame
    {
        get => _currentFrame;
        set
        {
            var next = Math.Clamp(value, StartFrame, EndFrame);
            if (_currentFrame == next) return;
            _currentFrame = next;
            Invalidate();
            CurrentFrameChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public int StartFrame
    {
        get => _startFrame;
        set
        {
            var next = Math.Clamp(value, 0, Math.Max(0, FrameCount - 1));
            if (next > _endFrame) _endFrame = next;
            if (_startFrame == next) return;
            _startFrame = next;
            CurrentFrame = _currentFrame;
            Invalidate();
        }
    }

    public int EndFrame
    {
        get => _endFrame;
        set
        {
            var next = Math.Clamp(value, 0, Math.Max(0, FrameCount - 1));
            if (next < _startFrame) _startFrame = next;
            if (_endFrame == next) return;
            _endFrame = next;
            CurrentFrame = _currentFrame;
            Invalidate();
        }
    }

    private int FrameCount => Math.Max(1, _scene.FrameCount);
    private int VisibleFrameCount => Math.Max(1, EndFrame - StartFrame + 1);
    private int ActiveLayer
    {
        get => Math.Clamp(_scene.ActiveLayer, 0, Math.Max(0, _scene.LayerCount - 1));
        set
        {
            if (_scene.LayerCount <= 0) return;
            var next = Math.Clamp(value, 0, _scene.LayerCount - 1);
            if (_scene.ActiveLayer == next) return;
            _scene.ActiveLayer = next;
            Invalidate();
            ActiveLayerChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(BackColor);

        var content = ClientRectangle;
        if (content.Width <= 0 || content.Height <= 0) return;

        var trackLeft = Math.Min(GutterWidth, Math.Max(70, content.Width / 3));
        var trackRight = content.Width - TrackPadding;
        if (trackRight <= trackLeft + 24) return;

        DrawShell(g, content, trackLeft, trackRight);
        DrawRuler(g, trackLeft, trackRight);
        DrawLayerRows(g, trackLeft, trackRight);
        DrawPlayhead(g, trackLeft, trackRight);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        if (TryHandleLayerClick(e.Location)) return;
        _draggingPlayhead = true;
        Capture = true;
        CurrentFrame = FrameFromX(e.X);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button != MouseButtons.Left) return;
        if (LayerIndexFromPoint(e.Location) is { } layer)
        {
            ToggleLayerVisibility(layer);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_draggingPlayhead) return;
        CurrentFrame = FrameFromX(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        _draggingPlayhead = false;
        Capture = false;
        CurrentFrame = FrameFromX(e.X);
    }

    private void DrawShell(Graphics g, Rectangle bounds, int trackLeft, int trackRight)
    {
        using var headerBrush = new SolidBrush(Theme.Panel);
        using var gutterBrush = new SolidBrush(Color.FromArgb(28, 31, 34));
        using var borderPen = new Pen(Color.FromArgb(82, 91, 96));
        using var softPen = new Pen(Color.FromArgb(48, 54, 58));
        using var titleBrush = new SolidBrush(Theme.Text);
        using var mutedBrush = new SolidBrush(Theme.Muted);
        using var titleFont = Theme.UiFont(10, FontStyle.Bold);

        g.FillRectangle(headerBrush, 0, 0, bounds.Width, HeaderHeight);
        g.FillRectangle(gutterBrush, 0, HeaderHeight, trackLeft, bounds.Height - HeaderHeight);
        g.DrawLine(borderPen, 0, HeaderHeight - 1, bounds.Width, HeaderHeight - 1);
        g.DrawLine(borderPen, trackLeft - 1, HeaderHeight, trackLeft - 1, bounds.Height);
        g.DrawLine(borderPen, 0, bounds.Height - 1, bounds.Width, bounds.Height - 1);

        g.DrawString("Timeline", titleFont, titleBrush, 12, 8);
        var rangeText = $"{StartFrame} - {EndFrame}  ({VisibleFrameCount} frames)";
        g.DrawString(rangeText, Font, mutedBrush, trackLeft, 9);
        DrawHeaderButton(g, SoloButtonBounds(), "Solo");
        DrawHeaderButton(g, AllButtonBounds(), "All");

        var rulerTop = HeaderHeight;
        g.DrawLine(softPen, trackLeft, rulerTop + RulerHeight - 1, trackRight, rulerTop + RulerHeight - 1);
    }

    private void DrawRuler(Graphics g, int trackLeft, int trackRight)
    {
        using var majorPen = new Pen(Color.FromArgb(104, 116, 122));
        using var minorPen = new Pen(Color.FromArgb(55, 62, 67));
        using var textBrush = new SolidBrush(Color.FromArgb(218, 226, 226));
        using var bandBrush = new SolidBrush(Color.FromArgb(23, 26, 29));

        g.FillRectangle(bandBrush, trackLeft, HeaderHeight, trackRight - trackLeft, RulerHeight);

        var majorStep = ChooseFrameStep(trackRight - trackLeft, 76);
        var minorStep = Math.Max(1, majorStep / 4);
        var firstMinor = StartFrame - StartFrame % minorStep;

        for (var frame = firstMinor; frame <= EndFrame; frame += minorStep)
        {
            if (frame < StartFrame) continue;
            var x = XFromFrame(frame, trackLeft, trackRight);
            var major = frame % majorStep == 0 || frame == StartFrame || frame == EndFrame;
            g.DrawLine(major ? majorPen : minorPen, x, HeaderHeight + (major ? 4 : 14), x, HeaderHeight + RulerHeight - 1);
            if (!major) continue;

            var label = frame.ToString();
            g.DrawString(label, Font, textBrush, x + 4, HeaderHeight + 5);
        }
    }

    private void DrawLayerRows(Graphics g, int trackLeft, int trackRight)
    {
        using var rowBrush = new SolidBrush(Color.FromArgb(30, 34, 37));
        using var altRowBrush = new SolidBrush(Color.FromArgb(35, 39, 42));
        using var inactiveBrush = new SolidBrush(Color.FromArgb(47, 53, 57));
        using var exposureBrush = new SolidBrush(Color.FromArgb(69, 151, 140));
        using var exposureEdgePen = new Pen(Color.FromArgb(112, 218, 201));
        using var keyBrush = new SolidBrush(Color.FromArgb(227, 169, 86));
        using var textBrush = new SolidBrush(Theme.Muted);
        using var activeTextBrush = new SolidBrush(Theme.Text);
        using var faintTextBrush = new SolidBrush(Color.FromArgb(136, 148, 148));
        using var gridPen = new Pen(Color.FromArgb(42, 48, 52));
        using var activePen = new Pen(Theme.Accent, 2);
        using var eyePen = new Pen(Color.FromArgb(210, 224, 224, 224), 1.4f);
        using var hiddenPen = new Pen(Color.FromArgb(120, 136, 136, 136), 1.2f);

        var rowTop = HeaderHeight + RulerHeight;
        var availableRows = Math.Max(1, (Height - rowTop - 8) / (RowHeight + RowGap));
        var rowCount = Math.Min(Math.Min(availableRows, 16), Math.Max(1, _scene.LayerCount));
        var majorStep = ChooseFrameStep(trackRight - trackLeft, 76);

        for (var frame = StartFrame - StartFrame % majorStep; frame <= EndFrame; frame += majorStep)
        {
            if (frame < StartFrame) continue;
            var x = XFromFrame(frame, trackLeft, trackRight);
            g.DrawLine(gridPen, x, rowTop, x, Height - 8);
        }

        for (var i = 0; i < rowCount; i++)
        {
            var y = rowTop + i * (RowHeight + RowGap);
            var rowBounds = new Rectangle(0, y, Width, RowHeight);
            g.FillRectangle(i % 2 == 0 ? rowBrush : altRowBrush, rowBounds);
            if (i == ActiveLayer)
            {
                using var activeBack = new SolidBrush(Color.FromArgb(36, Theme.Accent));
                g.FillRectangle(activeBack, rowBounds);
                g.DrawRectangle(activePen, 1, y + 1, trackLeft - 4, RowHeight - 3);
            }

            var name = i < _scene.LayerNames.Length ? _scene.LayerNames[i] : $"Layer {i:0000}";
            var visible = i < _scene.LayerVisible.Length && _scene.LayerVisible[i];
            DrawVisibilityIcon(g, visible, 13, y + RowHeight / 2f, visible ? eyePen : hiddenPen);
            g.DrawString(name, Font, i == ActiveLayer ? activeTextBrush : textBrush, VisibilityColumnWidth + 8, y + 3);
            if (!visible) g.FillRectangle(inactiveBrush, trackLeft, y, trackRight - trackLeft, RowHeight);

            if (i >= _scene.LayerStart.Length || i >= _scene.LayerEnd.Length) continue;
            var layerStart = Math.Clamp(_scene.LayerStart[i], StartFrame, EndFrame);
            var layerEnd = Math.Clamp(_scene.LayerEnd[i], StartFrame, EndFrame);
            if (layerEnd < StartFrame || layerStart > EndFrame) continue;

            var x1 = XFromFrame(layerStart, trackLeft, trackRight);
            var x2 = XFromFrame(layerEnd + 1, trackLeft, trackRight);
            var exposure = new RectangleF(x1, y + 5, Math.Max(5, x2 - x1), RowHeight - 10);
            g.FillRoundedRectangle(exposureBrush, exposure, 4);
            g.DrawRoundedRectangle(exposureEdgePen, exposure, 4);

            DrawKeyDiamond(g, keyBrush, x1, y + RowHeight / 2f, 4.5f);
            DrawKeyDiamond(g, keyBrush, Math.Max(x1 + 8, x2 - 1), y + RowHeight / 2f, 4.5f);
        }

        if (_scene.LayerCount > rowCount)
        {
            var text = $"+ {_scene.LayerCount - rowCount} layers";
            g.DrawString(text, Font, faintTextBrush, 12, Height - 24);
        }
    }

    private bool TryHandleLayerClick(Point point)
    {
        if (SoloButtonBounds().Contains(point))
        {
            if (_scene.LayerCount > 0)
            {
                _scene.SoloLayer(ActiveLayer);
                Invalidate();
                LayerVisibilityChanged?.Invoke(this, EventArgs.Empty);
            }

            return true;
        }

        if (AllButtonBounds().Contains(point))
        {
            _scene.ShowAllLayers();
            Invalidate();
            LayerVisibilityChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        if (LayerIndexFromPoint(point) is not { } layer) return false;
        if (point.X < VisibilityColumnWidth)
        {
            ToggleLayerVisibility(layer);
            return true;
        }

        ActiveLayer = layer;
        return true;
    }

    private int? LayerIndexFromPoint(Point point)
    {
        var trackLeft = Math.Min(GutterWidth, Math.Max(120, Width / 3));
        if (point.X < 0 || point.X >= trackLeft) return null;
        var rowTop = HeaderHeight + RulerHeight;
        if (point.Y < rowTop) return null;
        var localY = point.Y - rowTop;
        var stride = RowHeight + RowGap;
        var row = localY / stride;
        if (localY % stride >= RowHeight) return null;
        var availableRows = Math.Max(1, (Height - rowTop - 8) / stride);
        var rowCount = Math.Min(Math.Min(availableRows, 16), Math.Max(1, _scene.LayerCount));
        return row >= 0 && row < rowCount ? row : null;
    }

    private void ToggleLayerVisibility(int layer)
    {
        if ((uint)layer >= _scene.LayerCount) return;
        _scene.ToggleLayer(layer);
        Invalidate();
        LayerVisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    private static Rectangle SoloButtonBounds() => new(78, 6, SoloButtonWidth, 22);

    private static Rectangle AllButtonBounds() => new(142, 6, AllButtonWidth, 22);

    private void DrawHeaderButton(Graphics g, Rectangle bounds, string text)
    {
        using var fill = new SolidBrush(Theme.PanelStrong);
        using var border = new Pen(Theme.Border);
        using var textBrush = new SolidBrush(Theme.Text);
        g.FillRectangle(fill, bounds);
        g.DrawRectangle(border, bounds);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(text, Font, textBrush, bounds, format);
    }

    private static void DrawVisibilityIcon(Graphics g, bool visible, float x, float y, Pen pen)
    {
        var bounds = new RectangleF(x - 8, y - 5, 16, 10);
        g.DrawEllipse(pen, bounds);
        if (visible)
        {
            using var fill = new SolidBrush(pen.Color);
            g.FillEllipse(fill, x - 2.5f, y - 2.5f, 5, 5);
        }
        else
        {
            g.DrawLine(pen, x - 8, y + 6, x + 8, y - 6);
        }
    }

    private void DrawPlayhead(Graphics g, int trackLeft, int trackRight)
    {
        var x = XFromFrame(CurrentFrame, trackLeft, trackRight);
        using var linePen = new Pen(Color.FromArgb(240, 94, 91), 2);
        using var fillBrush = new SolidBrush(Color.FromArgb(240, 94, 91));
        using var textBrush = new SolidBrush(Color.White);
        using var shadowBrush = new SolidBrush(Color.FromArgb(90, 0, 0, 0));

        g.DrawLine(linePen, x, HeaderHeight, x, Height - 8);

        var points = new[]
        {
            new PointF(x - HandleWidth / 2f, HeaderHeight - 1),
            new PointF(x + HandleWidth / 2f, HeaderHeight - 1),
            new PointF(x + HandleWidth / 2f, HeaderHeight + 12),
            new PointF(x, HeaderHeight + 20),
            new PointF(x - HandleWidth / 2f, HeaderHeight + 12)
        };
        g.FillPolygon(fillBrush, points);

        var label = CurrentFrame.ToString();
        var size = g.MeasureString(label, Font);
        var labelBounds = new RectangleF(
            Math.Clamp(x - size.Width * 0.5f - 6, trackLeft, Math.Max(trackLeft, trackRight - size.Width - 12)),
            6,
            size.Width + 12,
            19);
        g.FillRoundedRectangle(shadowBrush, labelBounds, 4);
        g.DrawString(label, Font, textBrush, labelBounds.Left + 6, labelBounds.Top + 2);
    }

    private int FrameFromX(int x)
    {
        var trackLeft = Math.Min(GutterWidth, Math.Max(70, Width / 3));
        var trackRight = Width - TrackPadding;
        if (trackRight <= trackLeft) return StartFrame;
        var t = Math.Clamp((x - trackLeft) / (float)(trackRight - trackLeft), 0f, 1f);
        return Math.Clamp(StartFrame + (int)MathF.Round(t * (VisibleFrameCount - 1)), StartFrame, EndFrame);
    }

    private float XFromFrame(int frame, int trackLeft, int trackRight)
    {
        if (VisibleFrameCount <= 1) return trackLeft;
        var t = (frame - StartFrame) / (float)(VisibleFrameCount - 1);
        return trackLeft + Math.Clamp(t, 0f, 1f) * (trackRight - trackLeft);
    }

    private int ChooseFrameStep(int pixelWidth, int targetPixels)
    {
        var raw = Math.Max(1, VisibleFrameCount * targetPixels / Math.Max(1, pixelWidth));
        var magnitude = 1;
        while (magnitude * 10 < raw) magnitude *= 10;
        foreach (var multiplier in new[] { 1, 2, 5, 10 })
        {
            var step = magnitude * multiplier;
            if (step >= raw) return step;
        }

        return magnitude * 10;
    }

    private static void DrawKeyDiamond(Graphics g, Brush brush, float x, float y, float radius)
    {
        var points = new[]
        {
            new PointF(x, y - radius),
            new PointF(x + radius, y),
            new PointF(x, y + radius),
            new PointF(x - radius, y)
        };
        g.FillPolygon(brush, points);
    }
}

internal static class TimelineGraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics graphics, Brush brush, RectangleF bounds, float radius)
    {
        using var path = RoundedPath(bounds, radius);
        graphics.FillPath(brush, path);
    }

    public static void DrawRoundedRectangle(this Graphics graphics, Pen pen, RectangleF bounds, float radius)
    {
        using var path = RoundedPath(bounds, radius);
        graphics.DrawPath(pen, path);
    }

    private static GraphicsPath RoundedPath(RectangleF bounds, float radius)
    {
        var diameter = Math.Max(1, radius * 2);
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
