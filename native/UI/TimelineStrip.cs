using System.Drawing.Drawing2D;

namespace VectorAnimationEngine;

internal sealed class TimelineStrip : Control
{
    private const int PreferredGutterWidth = 232;
    private const int MinimumGutterWidth = 148;
    private const int HeaderHeight = 32;
    private const int RulerHeight = 28;
    private const int RowHeight = 23;
    private const int FrameCellWidth = 20;
    private const int HorizontalScrollHeight = 16;
    private const int HorizontalScrollArrowWidth = 15;
    private const int VerticalScrollWidth = 7;
    private const int VisibilityColumnWidth = 30;
    private const int SoloButtonWidth = 48;
    private const int AllButtonWidth = 42;

    private ITimelineContext _context = null!;
    private AnimationTimeline _timeline = null!;
    private VectorScene? _vectorScene;
    private DrawingObjectDefinition? _drawingObjectDefinition;
    private SceneDefinition? _sceneDefinition;
    private string? _activeTrackId;
    private bool _draggingPlayhead;
    private bool _draggingHorizontalScroll;
    private bool _draggingVerticalScroll;
    private int _horizontalScrollDragOffset;
    private int _verticalScrollDragOffset;
    private int _currentFrame;
    private int _startFrame;
    private int _endFrame;
    private int _knownFrameCount;
    private int _firstVisibleFrame;
    private int _firstVisibleTrack;
    private readonly System.Windows.Forms.Timer _motionTimer = new() { Interval = 33 };
    private int _hoverFrame = -1;
    private int _hoverTrack = -1;
    private bool _isPlaying;
    private float _playheadPulse;

    public event EventHandler? CurrentFrameChanged;
    public event EventHandler? ActiveLayerChanged;
    public event EventHandler? LayerVisibilityChanged;

    public TimelineStrip(VectorScene scene)
        : this((ITimelineContext)scene)
    {
    }

    public TimelineStrip(ITimelineContext context)
    {
        DoubleBuffered = true;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint,
            true);
        BackColor = Theme.Top;
        ForeColor = Theme.Text;
        Font = Theme.UiFont(9);
        Cursor = Cursors.Default;
        MinimumSize = new Size(360, 118);
        TabStop = true;
        _motionTimer.Tick += (_, _) =>
        {
            _playheadPulse = (_playheadPulse + 0.18f) % (MathF.PI * 2f);
            Invalidate();
        };

        BindContext(context);
    }

    public ITimelineContext Context => _context;

    public string? ActiveTrackId
    {
        get
        {
            var index = GetActiveTrackIndex();
            return index >= 0 && index < TrackCount ? _timeline.Tracks[index].Id : null;
        }
    }

    public int ActiveTrackIndex
    {
        get => GetActiveTrackIndex();
        set => SetActiveTrack(value);
    }

    public int CurrentFrame
    {
        get => _currentFrame;
        set
        {
            var next = Math.Clamp(value, StartFrame, EndFrame);
            if (_currentFrame == next)
            {
                EnsureCurrentFrameVisible();
                return;
            }

            _currentFrame = next;
            EnsureCurrentFrameVisible();
            Invalidate();
            CurrentFrameChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            if (_isPlaying == value) return;
            _isPlaying = value;
            _playheadPulse = 0;
            if (_isPlaying) _motionTimer.Start();
            else _motionTimer.Stop();
            Invalidate();
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
            EnsureCurrentFrameVisible();
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
            EnsureCurrentFrameVisible();
            Invalidate();
        }
    }

    private int FrameCount => Math.Max(1, Math.Max(_context.FrameCount, _timeline.Duration));
    private int TrackCount => _timeline.Tracks.Count;

    public void BindScene(VectorScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        BindContext(scene);
    }

    public void BindSceneDefinition(SceneDefinition scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        BindContext(scene);
    }

    public void BindContext(ITimelineContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (_timeline is not null) _timeline.Changed -= HandleTimelineChanged;

        _context = context;
        _vectorScene = context as VectorScene;
        _drawingObjectDefinition = context as DrawingObjectDefinition;
        _sceneDefinition = context as SceneDefinition;
        _context.SynchronizeTimelineTracks();
        _timeline = _context.Timeline;
        _timeline.Changed += HandleTimelineChanged;

        _knownFrameCount = FrameCount;
        _startFrame = 0;
        _endFrame = Math.Max(0, _knownFrameCount - 1);
        _currentFrame = Math.Clamp(_currentFrame, _startFrame, _endFrame);
        _firstVisibleFrame = _startFrame;
        _firstVisibleTrack = 0;
        _activeTrackId = TrackCount > 0 ? _timeline.Tracks[Math.Max(0, GetModelActiveTrackIndex())].Id : null;
        EnsureActiveTrackVisible();
        EnsureCurrentFrameVisible();
        Invalidate();
    }

    public void RefreshTimeline()
    {
        _context.SynchronizeTimelineTracks();
        var boundTimeline = _context.Timeline;
        if (!ReferenceEquals(_timeline, boundTimeline))
        {
            _timeline.Changed -= HandleTimelineChanged;
            _timeline = boundTimeline;
            _timeline.Changed += HandleTimelineChanged;
        }

        UpdateFrameBoundsForTimelineChange();
        EnsureActiveTrackVisible();
        EnsureCurrentFrameVisible();
        Invalidate();
    }

    public void EnsureCurrentFrameVisible()
    {
        if (!IsHandleCreated && Width <= 0) return;
        var layout = CreateLayout();
        var capacity = VisibleFrameCapacity(layout);
        if (_currentFrame < _firstVisibleFrame)
        {
            SetFirstVisibleFrame(_currentFrame, invalidate: false);
        }
        else if (_currentFrame >= _firstVisibleFrame + capacity)
        {
            SetFirstVisibleFrame(_currentFrame - capacity + 1, invalidate: false);
        }
        else
        {
            SetFirstVisibleFrame(_firstVisibleFrame, invalidate: false);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_timeline is not null) _timeline.Changed -= HandleTimelineChanged;
            _motionTimer.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        graphics.Clear(BackColor);

        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;

        var layout = CreateLayout();
        DrawShell(graphics, layout);
        DrawRuler(graphics, layout);
        DrawTrackRows(graphics, layout);
        DrawPlayhead(graphics, layout);
        DrawHorizontalScroll(graphics, layout);
        DrawVerticalScroll(graphics, layout);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_timeline is null) return;
        EnsureCurrentFrameVisible();
        EnsureActiveTrackVisible();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;

        Focus();
        var layout = CreateLayout();
        if (TryHandleHeaderClick(e.Location, layout)) return;
        if (TryHandleHorizontalScrollMouseDown(e.Location, layout)) return;
        if (TryHandleVerticalScrollMouseDown(e.Location, layout)) return;

        if (TryGetTrackIndex(e.Location, layout, out var trackIndex))
        {
            if (e.X < layout.TrackLeft)
            {
                if (e.X < VisibilityColumnWidth) ToggleTrackVisibility(trackIndex);
                else SetActiveTrack(trackIndex);
                return;
            }

            SetActiveTrack(trackIndex);
            _draggingPlayhead = true;
            Capture = true;
            CurrentFrame = FrameFromX(e.X, layout);
            return;
        }

        if (layout.RulerBounds.Contains(e.Location) && e.X >= layout.TrackLeft)
        {
            _draggingPlayhead = true;
            Capture = true;
            CurrentFrame = FrameFromX(e.X, layout);
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button != MouseButtons.Left) return;
        var layout = CreateLayout();
        if (e.X >= VisibilityColumnWidth && e.X < layout.TrackLeft && TryGetTrackIndex(e.Location, layout, out var trackIndex))
        {
            ToggleTrackVisibility(trackIndex);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var layout = CreateLayout();
        UpdateHover(e.Location, layout);

        if (_draggingHorizontalScroll)
        {
            DragHorizontalScroll(e.X, layout);
            return;
        }

        if (_draggingVerticalScroll)
        {
            DragVerticalScroll(e.Y, layout);
            return;
        }

        if (!_draggingPlayhead) return;
        if (e.X < layout.TrackLeft)
        {
            SetFirstVisibleFrame(_firstVisibleFrame - 1);
        }
        else if (e.X >= layout.TrackRight)
        {
            SetFirstVisibleFrame(_firstVisibleFrame + 1);
        }

        CurrentFrame = FrameFromX(e.X, layout);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverFrame < 0 && _hoverTrack < 0) return;
        _hoverFrame = -1;
        _hoverTrack = -1;
        Invalidate();
    }

    private void UpdateHover(Point location, TimelineLayout layout)
    {
        var frame = -1;
        var track = -1;
        if (location.X >= layout.TrackLeft
            && location.X < layout.TrackRight
            && (layout.RulerBounds.Contains(location) || TryGetTrackIndex(location, layout, out track)))
        {
            frame = FrameFromX(location.X, layout);
        }

        if (frame == _hoverFrame && track == _hoverTrack) return;
        _hoverFrame = frame;
        _hoverTrack = track;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        EndMouseDrag();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture) EndMouseDrag();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        var layout = CreateLayout();
        var notches = Math.Max(1, Math.Abs(e.Delta) / 120) * Math.Sign(e.Delta);
        if (e.X < layout.TrackLeft && TrackCount > VisibleTrackCapacity(layout) && (ModifierKeys & Keys.Shift) == 0)
        {
            SetFirstVisibleTrack(_firstVisibleTrack - notches * 3);
        }
        else
        {
            SetFirstVisibleFrame(_firstVisibleFrame - notches * 3);
        }
    }

    private void DrawShell(Graphics graphics, TimelineLayout layout)
    {
        using var headerBrush = new SolidBrush(Theme.Panel);
        using var gutterBrush = new SolidBrush(Color.FromArgb(28, 31, 34));
        using var borderPen = new Pen(Color.FromArgb(82, 91, 96));
        using var softPen = new Pen(Color.FromArgb(48, 54, 58));
        using var titleFont = Theme.UiFont(10, FontStyle.Bold);

        graphics.FillRectangle(headerBrush, layout.HeaderBounds);
        graphics.FillRectangle(gutterBrush, layout.GutterBounds);
        graphics.DrawLine(borderPen, 0, HeaderHeight - 1, Width, HeaderHeight - 1);
        graphics.DrawLine(borderPen, layout.TrackLeft - 1, 0, layout.TrackLeft - 1, layout.ScrollTop);
        graphics.DrawLine(softPen, 0, layout.RowTop - 1, Width, layout.RowTop - 1);
        graphics.DrawLine(borderPen, 0, Height - 1, Width, Height - 1);

        var allBounds = AllButtonBounds(layout);
        var soloBounds = SoloButtonBounds(layout);
        var titleRight = Math.Max(42, soloBounds.Left - 6);
        TextRenderer.DrawText(
            graphics,
            "Timeline",
            titleFont,
            Rectangle.FromLTRB(10, 1, titleRight, HeaderHeight - 1),
            Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        DrawHeaderButton(graphics, soloBounds, "Solo");
        DrawHeaderButton(graphics, allBounds, "All");

        var name = _sceneDefinition?.Name ?? _drawingObjectDefinition?.Name ?? "Drawing Timeline";
        var summary = $"{name}    Frame {CurrentFrame} / {Math.Max(0, FrameCount - 1)}";
        TextRenderer.DrawText(
            graphics,
            summary,
            Font,
            Rectangle.FromLTRB(layout.TrackLeft + 8, 1, layout.TrackRight - 6, HeaderHeight - 1),
            Theme.Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }

    private void DrawRuler(Graphics graphics, TimelineLayout layout)
    {
        using var rulerBrush = new SolidBrush(Color.FromArgb(23, 26, 29));
        using var majorBrush = new SolidBrush(Color.FromArgb(32, 37, 40));
        using var selectedBrush = new SolidBrush(Color.FromArgb(150, 177, 68, 65));
        using var hoverBrush = new SolidBrush(Color.FromArgb(34, Theme.Accent));
        using var gridPen = new Pen(Color.FromArgb(64, 72, 77));
        using var minorPen = new Pen(Color.FromArgb(48, 55, 59));
        using var rulerFont = Theme.UiFont(7.5f);

        graphics.FillRectangle(rulerBrush, layout.RulerBounds);
        TextRenderer.DrawText(
            graphics,
            "Track",
            Font,
            new Rectangle(VisibilityColumnWidth + 5, HeaderHeight, Math.Max(20, layout.TrackLeft - VisibilityColumnWidth - 17), RulerHeight),
            Theme.Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        var state = graphics.Save();
        graphics.SetClip(Rectangle.FromLTRB(layout.TrackLeft, HeaderHeight, layout.TrackRight, layout.RowTop));
        var columns = VisibleFrameDrawCount(layout);
        for (var column = 0; column < columns; column++)
        {
            var frame = _firstVisibleFrame + column;
            if (frame > EndFrame) break;
            var x = layout.TrackLeft + column * FrameCellWidth;
            var bounds = new Rectangle(x, HeaderHeight, FrameCellWidth, RulerHeight);
            var major = frame == StartFrame || frame % 5 == 0;
            if (major) graphics.FillRectangle(majorBrush, bounds);
            if (frame == _hoverFrame && frame != CurrentFrame) graphics.FillRectangle(hoverBrush, bounds);
            if (frame == CurrentFrame) graphics.FillRectangle(selectedBrush, bounds);

            graphics.DrawLine(major ? gridPen : minorPen, x, major ? HeaderHeight + 5 : HeaderHeight + 17, x, layout.RowTop - 1);
            if (!major) continue;
            TextRenderer.DrawText(
                graphics,
                frame.ToString(),
                rulerFont,
                new Rectangle(x + 2, HeaderHeight + 1, FrameCellWidth * 2 - 2, RulerHeight - 4),
                frame == CurrentFrame ? Color.White : Theme.Muted,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPadding);
        }

        graphics.DrawLine(gridPen, layout.TrackRight - 1, HeaderHeight, layout.TrackRight - 1, layout.RowTop);
        graphics.Restore(state);
    }

    private void DrawTrackRows(Graphics graphics, TimelineLayout layout)
    {
        using var rowBrush = new SolidBrush(Color.FromArgb(30, 34, 37));
        using var alternateRowBrush = new SolidBrush(Color.FromArgb(34, 38, 41));
        using var activeRowBrush = new SolidBrush(Color.FromArgb(38, 65, 62));
        using var activeEdgeBrush = new SolidBrush(Theme.Accent);
        using var populatedExposureBrush = new SolidBrush(Color.FromArgb(86, 79, 179, 162));
        using var blankExposureBrush = new SolidBrush(Color.FromArgb(68, 120, 130, 133));
        using var populatedLinePen = new Pen(Color.FromArgb(196, 112, 218, 201));
        using var blankLinePen = new Pen(Color.FromArgb(170, 156, 166, 168));
        using var gridPen = new Pen(Color.FromArgb(47, 54, 58));
        using var majorGridPen = new Pen(Color.FromArgb(61, 69, 74));
        using var selectedCellPen = new Pen(Color.FromArgb(238, 206, 164, 81), 2);
        using var hiddenBrush = new SolidBrush(Color.FromArgb(126, 12, 14, 16));
        using var currentColumnBrush = new SolidBrush(Color.FromArgb(30, 240, 94, 91));
        using var hoverCellBrush = new SolidBrush(Color.FromArgb(27, Theme.Accent));
        using var eyePen = new Pen(Color.FromArgb(214, 224, 224, 224), 1.35f);
        using var hiddenEyePen = new Pen(Color.FromArgb(130, 144, 148, 148), 1.2f);

        var visibleRows = VisibleTrackCapacity(layout);
        var activeTrack = GetActiveTrackIndex();
        var rowCount = Math.Min(visibleRows, Math.Max(0, TrackCount - _firstVisibleTrack));

        if (rowCount == 0)
        {
            TextRenderer.DrawText(
                graphics,
                "No timeline tracks",
                Font,
                layout.GridBounds,
                Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            return;
        }

        var graphicsState = graphics.Save();
        graphics.SetClip(Rectangle.FromLTRB(0, layout.RowTop, layout.TrackRight, layout.RowBottom));
        for (var visibleRow = 0; visibleRow < rowCount; visibleRow++)
        {
            var trackIndex = _firstVisibleTrack + visibleRow;
            var track = _timeline.Tracks[trackIndex];
            var y = layout.RowTop + visibleRow * RowHeight;
            var rowBounds = new Rectangle(0, y, layout.TrackRight, RowHeight);
            var active = trackIndex == activeTrack;
            graphics.FillRectangle(active ? activeRowBrush : visibleRow % 2 == 0 ? rowBrush : alternateRowBrush, rowBounds);
            if (active) graphics.FillRectangle(activeEdgeBrush, 0, y, 3, RowHeight);

            var visible = IsTrackVisible(trackIndex);
            DrawVisibilityIcon(graphics, visible, 14, y + RowHeight / 2f, visible ? eyePen : hiddenEyePen);
            TextRenderer.DrawText(
                graphics,
                GetTrackName(trackIndex),
                Font,
                new Rectangle(VisibilityColumnWidth + 5, y, Math.Max(16, layout.TrackLeft - VisibilityColumnWidth - VerticalScrollWidth - 10), RowHeight),
                active ? Theme.Text : Theme.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            DrawTrackCells(
                graphics,
                layout,
                track,
                trackIndex,
                y,
                active,
                visible,
                populatedExposureBrush,
                blankExposureBrush,
                populatedLinePen,
                blankLinePen,
                gridPen,
                majorGridPen,
                selectedCellPen,
                hiddenBrush,
                currentColumnBrush,
                hoverCellBrush);
        }

        graphics.Restore(graphicsState);
    }

    private void DrawTrackCells(
        Graphics graphics,
        TimelineLayout layout,
        AnimationTimelineTrack track,
        int trackIndex,
        int y,
        bool active,
        bool visible,
        Brush populatedExposureBrush,
        Brush blankExposureBrush,
        Pen populatedLinePen,
        Pen blankLinePen,
        Pen gridPen,
        Pen majorGridPen,
        Pen selectedCellPen,
        Brush hiddenBrush,
        Brush currentColumnBrush,
        Brush hoverCellBrush)
    {
        var state = graphics.Save();
        graphics.SetClip(new Rectangle(layout.TrackLeft, y, layout.TrackRight - layout.TrackLeft, RowHeight));
        var columns = VisibleFrameDrawCount(layout);
        for (var column = 0; column < columns; column++)
        {
            var frame = _firstVisibleFrame + column;
            if (frame > EndFrame) break;
            var x = layout.TrackLeft + column * FrameCellWidth;
            var cellBounds = new Rectangle(x, y, FrameCellWidth, RowHeight);
            if (trackIndex == _hoverTrack && frame == _hoverFrame && frame != CurrentFrame)
            {
                graphics.FillRectangle(hoverCellBrush, cellBounds);
            }
            if (frame == CurrentFrame) graphics.FillRectangle(currentColumnBrush, cellBounds);

            var exposure = track.EvaluateExposure(frame);
            if (exposure.SourceKind is { } sourceKind)
            {
                var exposureBounds = new Rectangle(x + 1, y + 5, FrameCellWidth, RowHeight - 10);
                var exposureBrush = sourceKind == TimelineKeyframeKind.Populated
                    ? populatedExposureBrush
                    : blankExposureBrush;
                var exposurePen = sourceKind == TimelineKeyframeKind.Populated
                    ? populatedLinePen
                    : blankLinePen;
                graphics.FillRectangle(exposureBrush, exposureBounds);
                graphics.DrawLine(exposurePen, x + 1, y + RowHeight / 2, x + FrameCellWidth, y + RowHeight / 2);
                if (frame == exposure.EndFrame)
                {
                    graphics.DrawLine(exposurePen, x + FrameCellWidth - 2, y + 6, x + FrameCellWidth - 2, y + RowHeight - 6);
                }

                if (exposure.IsKeyframe)
                {
                    DrawKeyframeMarker(graphics, sourceKind, x + FrameCellWidth / 2f, y + RowHeight / 2f);
                }
            }

            graphics.DrawLine(frame % 5 == 0 ? majorGridPen : gridPen, x, y, x, y + RowHeight);
            graphics.DrawLine(gridPen, x, y + RowHeight - 1, x + FrameCellWidth, y + RowHeight - 1);
            if (!visible) graphics.FillRectangle(hiddenBrush, cellBounds);

            if (active && frame == CurrentFrame)
            {
                graphics.DrawRectangle(selectedCellPen, x + 1, y + 1, FrameCellWidth - 3, RowHeight - 3);
            }
        }

        graphics.Restore(state);
    }

    private static void DrawKeyframeMarker(Graphics graphics, TimelineKeyframeKind kind, float centerX, float centerY)
    {
        const float radius = 3.8f;
        var bounds = new RectangleF(centerX - radius, centerY - radius, radius * 2, radius * 2);
        using var outlinePen = new Pen(Color.FromArgb(235, 231, 237, 235), 1.2f);
        if (kind == TimelineKeyframeKind.Populated)
        {
            using var fillBrush = new SolidBrush(Color.FromArgb(238, 231, 237, 235));
            graphics.FillEllipse(fillBrush, bounds);
            graphics.DrawEllipse(outlinePen, bounds);
        }
        else
        {
            using var fillBrush = new SolidBrush(Color.FromArgb(30, 34, 37));
            graphics.FillEllipse(fillBrush, bounds);
            graphics.DrawEllipse(outlinePen, bounds);
        }
    }

    private void DrawPlayhead(Graphics graphics, TimelineLayout layout)
    {
        if (CurrentFrame < _firstVisibleFrame) return;
        var column = CurrentFrame - _firstVisibleFrame;
        if (column >= VisibleFrameDrawCount(layout)) return;

        var centerX = layout.TrackLeft + column * FrameCellWidth + FrameCellWidth / 2f;
        if (centerX < layout.TrackLeft || centerX >= layout.TrackRight) return;

        var pulse = _isPlaying ? (MathF.Sin(_playheadPulse) + 1f) * 0.5f : 0f;
        var playhead = Color.FromArgb(242, 94, 91);
        using var glowPen = new Pen(Color.FromArgb((int)Math.Round(42 + pulse * 68), playhead), 4.5f + pulse * 2.5f);
        using var linePen = new Pen(playhead, 1.5f + pulse * 0.35f);
        using var fillBrush = new SolidBrush(playhead);
        if (_isPlaying) graphics.DrawLine(glowPen, centerX, HeaderHeight, centerX, layout.RowBottom);
        graphics.DrawLine(linePen, centerX, HeaderHeight, centerX, layout.RowBottom);
        var handle = new[]
        {
            new PointF(centerX - 5, HeaderHeight),
            new PointF(centerX + 5, HeaderHeight),
            new PointF(centerX + 5, HeaderHeight + 8),
            new PointF(centerX, HeaderHeight + 13),
            new PointF(centerX - 5, HeaderHeight + 8)
        };
        graphics.FillPolygon(fillBrush, handle);
    }

    private void DrawHorizontalScroll(Graphics graphics, TimelineLayout layout)
    {
        var scroll = GetHorizontalScrollGeometry(layout);
        using var backgroundBrush = new SolidBrush(Color.FromArgb(20, 23, 25));
        using var buttonBrush = new SolidBrush(Color.FromArgb(35, 40, 43));
        using var thumbBrush = new SolidBrush(Color.FromArgb(75, 85, 90));
        using var borderPen = new Pen(Color.FromArgb(55, 63, 68));
        using var arrowBrush = new SolidBrush(Color.FromArgb(190, 202, 202));

        graphics.FillRectangle(backgroundBrush, scroll.Bounds);
        graphics.FillRectangle(buttonBrush, scroll.DecreaseButton);
        graphics.FillRectangle(buttonBrush, scroll.IncreaseButton);
        graphics.DrawRectangle(borderPen, scroll.Bounds.X, scroll.Bounds.Y, Math.Max(0, scroll.Bounds.Width - 1), Math.Max(0, scroll.Bounds.Height - 1));
        if (scroll.Thumb.Width > 0) graphics.FillRectangle(thumbBrush, scroll.Thumb);

        DrawHorizontalArrow(graphics, scroll.DecreaseButton, pointsLeft: true, arrowBrush);
        DrawHorizontalArrow(graphics, scroll.IncreaseButton, pointsLeft: false, arrowBrush);
    }

    private void DrawVerticalScroll(Graphics graphics, TimelineLayout layout)
    {
        var scroll = GetVerticalScrollGeometry(layout);
        if (scroll.MaxValue <= 0 || scroll.Bounds.Height <= 0) return;
        using var backgroundBrush = new SolidBrush(Color.FromArgb(19, 22, 24));
        using var thumbBrush = new SolidBrush(Color.FromArgb(75, 85, 90));
        graphics.FillRectangle(backgroundBrush, scroll.Bounds);
        graphics.FillRectangle(thumbBrush, scroll.Thumb);
    }

    private static void DrawHorizontalArrow(Graphics graphics, Rectangle bounds, bool pointsLeft, Brush brush)
    {
        var centerX = bounds.Left + bounds.Width / 2f;
        var centerY = bounds.Top + bounds.Height / 2f;
        var direction = pointsLeft ? 1 : -1;
        var points = new[]
        {
            new PointF(centerX + direction * 3, centerY - 4),
            new PointF(centerX + direction * 3, centerY + 4),
            new PointF(centerX - direction * 2, centerY)
        };
        graphics.FillPolygon(brush, points);
    }

    private bool TryHandleHeaderClick(Point point, TimelineLayout layout)
    {
        if (SoloButtonBounds(layout).Contains(point))
        {
            SoloActiveTrack();
            return true;
        }

        if (AllButtonBounds(layout).Contains(point))
        {
            ShowAllTracks();
            return true;
        }

        return false;
    }

    private bool TryHandleHorizontalScrollMouseDown(Point point, TimelineLayout layout)
    {
        var scroll = GetHorizontalScrollGeometry(layout);
        if (!scroll.Bounds.Contains(point)) return false;

        if (scroll.DecreaseButton.Contains(point))
        {
            SetFirstVisibleFrame(_firstVisibleFrame - 1);
        }
        else if (scroll.IncreaseButton.Contains(point))
        {
            SetFirstVisibleFrame(_firstVisibleFrame + 1);
        }
        else if (scroll.Thumb.Contains(point) && scroll.MaxValue > 0)
        {
            _draggingHorizontalScroll = true;
            _horizontalScrollDragOffset = point.X - scroll.Thumb.Left;
            Capture = true;
        }
        else if (point.X < scroll.Thumb.Left)
        {
            SetFirstVisibleFrame(_firstVisibleFrame - VisibleFrameCapacity(layout));
        }
        else if (point.X > scroll.Thumb.Right)
        {
            SetFirstVisibleFrame(_firstVisibleFrame + VisibleFrameCapacity(layout));
        }

        return true;
    }

    private bool TryHandleVerticalScrollMouseDown(Point point, TimelineLayout layout)
    {
        var scroll = GetVerticalScrollGeometry(layout);
        if (scroll.MaxValue <= 0 || !scroll.Bounds.Contains(point)) return false;

        if (scroll.Thumb.Contains(point))
        {
            _draggingVerticalScroll = true;
            _verticalScrollDragOffset = point.Y - scroll.Thumb.Top;
            Capture = true;
        }
        else if (point.Y < scroll.Thumb.Top)
        {
            SetFirstVisibleTrack(_firstVisibleTrack - VisibleTrackCapacity(layout));
        }
        else
        {
            SetFirstVisibleTrack(_firstVisibleTrack + VisibleTrackCapacity(layout));
        }

        return true;
    }

    private void DragHorizontalScroll(int mouseX, TimelineLayout layout)
    {
        var scroll = GetHorizontalScrollGeometry(layout);
        var travel = scroll.Track.Width - scroll.Thumb.Width;
        if (scroll.MaxValue <= 0 || travel <= 0) return;
        var thumbX = Math.Clamp(mouseX - _horizontalScrollDragOffset, scroll.Track.Left, scroll.Track.Right - scroll.Thumb.Width);
        var ratio = (thumbX - scroll.Track.Left) / (double)travel;
        SetFirstVisibleFrame(StartFrame + (int)Math.Round(ratio * scroll.MaxValue));
    }

    private void DragVerticalScroll(int mouseY, TimelineLayout layout)
    {
        var scroll = GetVerticalScrollGeometry(layout);
        var travel = scroll.Bounds.Height - scroll.Thumb.Height;
        if (scroll.MaxValue <= 0 || travel <= 0) return;
        var thumbY = Math.Clamp(mouseY - _verticalScrollDragOffset, scroll.Bounds.Top, scroll.Bounds.Bottom - scroll.Thumb.Height);
        var ratio = (thumbY - scroll.Bounds.Top) / (double)travel;
        SetFirstVisibleTrack((int)Math.Round(ratio * scroll.MaxValue));
    }

    private void EndMouseDrag()
    {
        _draggingPlayhead = false;
        _draggingHorizontalScroll = false;
        _draggingVerticalScroll = false;
        if (Capture) Capture = false;
    }

    private void SetActiveTrack(int trackIndex)
    {
        if (trackIndex < 0 || trackIndex >= TrackCount) return;
        var previousTrackId = ActiveTrackId;
        var track = _timeline.Tracks[trackIndex];
        _activeTrackId = track.Id;

        var drawingScene = DrawingScene();
        if (drawingScene is not null)
        {
            var layerIndex = FindLayerIndex(drawingScene, track.TargetId);
            if (layerIndex >= 0) drawingScene.ActiveLayer = layerIndex;
        }

        EnsureActiveTrackVisible();
        Invalidate();
        if (!string.Equals(previousTrackId, track.Id, StringComparison.Ordinal))
        {
            ActiveLayerChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private int GetActiveTrackIndex()
    {
        if (TrackCount == 0) return -1;

        var rememberedIndex = -1;
        if (!string.IsNullOrWhiteSpace(_activeTrackId))
        {
            for (var i = 0; i < TrackCount; i++)
            {
                if (!string.Equals(_timeline.Tracks[i].Id, _activeTrackId, StringComparison.Ordinal)) continue;
                rememberedIndex = i;
                break;
            }
        }

        if (rememberedIndex >= 0
            && _drawingObjectDefinition?.Instances.Any(instance => string.Equals(
                instance.Id,
                _timeline.Tracks[rememberedIndex].TargetId,
                StringComparison.Ordinal)) == true)
        {
            return rememberedIndex;
        }

        var modelIndex = GetModelActiveTrackIndex();
        if (modelIndex >= 0) return modelIndex;

        return rememberedIndex >= 0 ? rememberedIndex : 0;
    }

    private int GetModelActiveTrackIndex()
    {
        var drawingScene = DrawingScene();
        if (drawingScene is null || drawingScene.ActiveLayer < 0 || drawingScene.ActiveLayer >= drawingScene.LayerIds.Length)
        {
            return -1;
        }

        var targetId = drawingScene.LayerIds[drawingScene.ActiveLayer];
        for (var i = 0; i < TrackCount; i++)
        {
            if (string.Equals(_timeline.Tracks[i].TargetId, targetId, StringComparison.Ordinal)) return i;
        }

        return -1;
    }

    private string GetTrackName(int trackIndex)
    {
        if (trackIndex < 0 || trackIndex >= TrackCount) return "Track";
        var targetId = _timeline.Tracks[trackIndex].TargetId;

        var drawingScene = DrawingScene();
        if (drawingScene is not null)
        {
            var layerIndex = FindLayerIndex(drawingScene, targetId);
            if (layerIndex >= 0) return drawingScene.LayerNames[layerIndex];
        }

        if (_drawingObjectDefinition is not null)
        {
            var instance = _drawingObjectDefinition.Instances.FirstOrDefault(item => string.Equals(item.Id, targetId, StringComparison.Ordinal));
            if (instance is not null) return instance.Name;
        }

        if (_sceneDefinition is not null)
        {
            var instance = _sceneDefinition.Instances.FirstOrDefault(item => string.Equals(item.Id, targetId, StringComparison.Ordinal));
            if (instance is not null) return instance.Name;
        }

        return $"Track {trackIndex + 1}";
    }

    private bool IsTrackVisible(int trackIndex)
    {
        if (trackIndex < 0 || trackIndex >= TrackCount) return false;
        var targetId = _timeline.Tracks[trackIndex].TargetId;

        var drawingScene = DrawingScene();
        if (drawingScene is not null)
        {
            var layerIndex = FindLayerIndex(drawingScene, targetId);
            if (layerIndex >= 0) return drawingScene.LayerVisible[layerIndex];
        }

        if (_drawingObjectDefinition is not null)
        {
            return _drawingObjectDefinition.Instances
                .FirstOrDefault(item => string.Equals(item.Id, targetId, StringComparison.Ordinal))?.Visible == true;
        }

        if (_sceneDefinition is not null)
        {
            return _sceneDefinition.Instances.FirstOrDefault(item => string.Equals(item.Id, targetId, StringComparison.Ordinal))?.Visible == true;
        }

        return true;
    }

    private void ToggleTrackVisibility(int trackIndex)
    {
        if (trackIndex < 0 || trackIndex >= TrackCount) return;
        var targetId = _timeline.Tracks[trackIndex].TargetId;

        var drawingScene = DrawingScene();
        var layerIndex = drawingScene is null ? -1 : FindLayerIndex(drawingScene, targetId);
        if (drawingScene is not null && layerIndex >= 0)
        {
            drawingScene.ToggleLayer(layerIndex);
        }
        else if (_drawingObjectDefinition is not null)
        {
            var instance = _drawingObjectDefinition.Instances.FirstOrDefault(item => string.Equals(item.Id, targetId, StringComparison.Ordinal));
            if (instance is not null) instance.Visible = !instance.Visible;
        }
        else if (_sceneDefinition is not null)
        {
            var instance = _sceneDefinition.Instances.FirstOrDefault(item => string.Equals(item.Id, targetId, StringComparison.Ordinal));
            if (instance is not null) instance.Visible = !instance.Visible;
        }

        Invalidate();
        LayerVisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SoloActiveTrack()
    {
        var trackIndex = GetActiveTrackIndex();
        if (trackIndex < 0 || trackIndex >= TrackCount) return;
        var targetId = _timeline.Tracks[trackIndex].TargetId;

        var drawingScene = DrawingScene();
        if (drawingScene is not null)
        {
            drawingScene.SoloLayer(FindLayerIndex(drawingScene, targetId));
            if (_drawingObjectDefinition is not null)
            {
                foreach (var instance in _drawingObjectDefinition.Instances)
                {
                    instance.Visible = string.Equals(instance.Id, targetId, StringComparison.Ordinal);
                }
            }
        }
        else if (_sceneDefinition is not null)
        {
            foreach (var instance in _sceneDefinition.Instances)
            {
                instance.Visible = string.Equals(instance.Id, targetId, StringComparison.Ordinal);
            }
        }

        Invalidate();
        LayerVisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ShowAllTracks()
    {
        var drawingScene = DrawingScene();
        if (drawingScene is not null)
        {
            drawingScene.ShowAllLayers();
            if (_drawingObjectDefinition is not null)
            {
                foreach (var instance in _drawingObjectDefinition.Instances) instance.Visible = true;
            }
        }
        else if (_sceneDefinition is not null)
        {
            foreach (var instance in _sceneDefinition.Instances) instance.Visible = true;
        }

        Invalidate();
        LayerVisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    private VectorScene? DrawingScene() => _vectorScene ?? _drawingObjectDefinition?.Scene;

    private static int FindLayerIndex(VectorScene scene, string targetId)
    {
        for (var i = 0; i < scene.LayerIds.Length; i++)
        {
            if (string.Equals(scene.LayerIds[i], targetId, StringComparison.Ordinal)) return i;
        }

        return -1;
    }

    private void HandleTimelineChanged(object? sender, EventArgs e)
    {
        UpdateFrameBoundsForTimelineChange();
        EnsureActiveTrackVisible();
        EnsureCurrentFrameVisible();
        Invalidate();
    }

    private void UpdateFrameBoundsForTimelineChange()
    {
        var oldLastFrame = Math.Max(0, _knownFrameCount - 1);
        var followedTimelineEnd = _knownFrameCount <= 0 || _endFrame >= oldLastFrame;
        _knownFrameCount = FrameCount;
        var newLastFrame = Math.Max(0, _knownFrameCount - 1);
        _startFrame = Math.Clamp(_startFrame, 0, newLastFrame);
        _endFrame = followedTimelineEnd
            ? newLastFrame
            : Math.Clamp(_endFrame, _startFrame, newLastFrame);
        _currentFrame = Math.Clamp(_currentFrame, _startFrame, _endFrame);

        if (!string.IsNullOrWhiteSpace(_activeTrackId) && _timeline.FindTrack(_activeTrackId) is null)
        {
            _activeTrackId = TrackCount > 0 ? _timeline.Tracks[0].Id : null;
        }
    }

    private void EnsureActiveTrackVisible()
    {
        var layout = CreateLayout();
        var capacity = VisibleTrackCapacity(layout);
        var active = GetActiveTrackIndex();
        if (active < 0)
        {
            SetFirstVisibleTrack(0, invalidate: false);
        }
        else if (active < _firstVisibleTrack)
        {
            SetFirstVisibleTrack(active, invalidate: false);
        }
        else if (active >= _firstVisibleTrack + capacity)
        {
            SetFirstVisibleTrack(active - capacity + 1, invalidate: false);
        }
        else
        {
            SetFirstVisibleTrack(_firstVisibleTrack, invalidate: false);
        }
    }

    private void SetFirstVisibleFrame(int frame, bool invalidate = true)
    {
        var layout = CreateLayout();
        var maxFirst = Math.Max(StartFrame, EndFrame - VisibleFrameCapacity(layout) + 1);
        var next = Math.Clamp(frame, StartFrame, maxFirst);
        if (_firstVisibleFrame == next) return;
        _firstVisibleFrame = next;
        if (invalidate) Invalidate();
    }

    private void SetFirstVisibleTrack(int track, bool invalidate = true)
    {
        var layout = CreateLayout();
        var maxFirst = Math.Max(0, TrackCount - VisibleTrackCapacity(layout));
        var next = Math.Clamp(track, 0, maxFirst);
        if (_firstVisibleTrack == next) return;
        _firstVisibleTrack = next;
        if (invalidate) Invalidate();
    }

    private bool TryGetTrackIndex(Point point, TimelineLayout layout, out int trackIndex)
    {
        trackIndex = -1;
        if (point.Y < layout.RowTop || point.Y >= layout.RowBottom) return false;
        var visibleRow = (point.Y - layout.RowTop) / RowHeight;
        trackIndex = _firstVisibleTrack + visibleRow;
        return trackIndex >= 0 && trackIndex < TrackCount;
    }

    private int FrameFromX(int x, TimelineLayout layout)
    {
        var column = (Math.Clamp(x, layout.TrackLeft, Math.Max(layout.TrackLeft, layout.TrackRight - 1)) - layout.TrackLeft) / FrameCellWidth;
        return Math.Clamp(_firstVisibleFrame + column, StartFrame, EndFrame);
    }

    private int VisibleFrameCapacity(TimelineLayout layout)
    {
        return Math.Max(1, (layout.TrackRight - layout.TrackLeft) / FrameCellWidth);
    }

    private int VisibleFrameDrawCount(TimelineLayout layout)
    {
        return Math.Max(1, (int)Math.Ceiling((layout.TrackRight - layout.TrackLeft) / (double)FrameCellWidth));
    }

    private int VisibleTrackCapacity(TimelineLayout layout)
    {
        return Math.Max(1, (layout.RowBottom - layout.RowTop) / RowHeight);
    }

    private TimelineLayout CreateLayout()
    {
        var trackLeft = Math.Min(PreferredGutterWidth, Math.Max(MinimumGutterWidth, Width - FrameCellWidth * 5));
        var trackRight = Math.Max(trackLeft + 1, Width - 1);
        var rowTop = HeaderHeight + RulerHeight;
        var scrollTop = Math.Max(rowTop, Height - HorizontalScrollHeight);
        var rowBottom = Math.Max(rowTop, scrollTop);
        return new TimelineLayout(
            trackLeft,
            trackRight,
            rowTop,
            rowBottom,
            scrollTop,
            new Rectangle(0, 0, Width, HeaderHeight),
            new Rectangle(0, HeaderHeight, trackLeft, Math.Max(0, scrollTop - HeaderHeight)),
            new Rectangle(trackLeft, HeaderHeight, Math.Max(0, trackRight - trackLeft), RulerHeight),
            new Rectangle(trackLeft, rowTop, Math.Max(0, trackRight - trackLeft), Math.Max(0, rowBottom - rowTop)));
    }

    private ScrollGeometry GetHorizontalScrollGeometry(TimelineLayout layout)
    {
        var bounds = new Rectangle(
            layout.TrackLeft,
            layout.ScrollTop,
            Math.Max(1, layout.TrackRight - layout.TrackLeft),
            Math.Max(1, Height - layout.ScrollTop));
        var decrease = new Rectangle(bounds.Left, bounds.Top, Math.Min(HorizontalScrollArrowWidth, bounds.Width), bounds.Height);
        var increase = new Rectangle(Math.Max(bounds.Left, bounds.Right - HorizontalScrollArrowWidth), bounds.Top, Math.Min(HorizontalScrollArrowWidth, bounds.Width), bounds.Height);
        var trackLeft = Math.Min(bounds.Right, decrease.Right + 2);
        var trackRight = Math.Max(trackLeft, increase.Left - 2);
        var track = Rectangle.FromLTRB(trackLeft, bounds.Top + 3, trackRight, Math.Max(bounds.Top + 4, bounds.Bottom - 3));
        var capacity = VisibleFrameCapacity(layout);
        var total = Math.Max(1, EndFrame - StartFrame + 1);
        var maxValue = Math.Max(0, total - capacity);
        var thumbWidth = maxValue == 0
            ? track.Width
            : Math.Clamp((int)Math.Round(track.Width * Math.Min(1d, capacity / (double)total)), 24, Math.Max(24, track.Width));
        thumbWidth = Math.Min(track.Width, thumbWidth);
        var travel = Math.Max(0, track.Width - thumbWidth);
        var position = maxValue == 0 ? 0d : (_firstVisibleFrame - StartFrame) / (double)maxValue;
        var thumbLeft = track.Left + (int)Math.Round(Math.Clamp(position, 0d, 1d) * travel);
        var thumb = new Rectangle(thumbLeft, track.Top, thumbWidth, track.Height);
        return new ScrollGeometry(bounds, decrease, increase, track, thumb, maxValue);
    }

    private VerticalScrollGeometry GetVerticalScrollGeometry(TimelineLayout layout)
    {
        var bounds = new Rectangle(
            Math.Max(0, layout.TrackLeft - VerticalScrollWidth),
            layout.RowTop,
            VerticalScrollWidth,
            Math.Max(0, layout.RowBottom - layout.RowTop));
        var capacity = VisibleTrackCapacity(layout);
        var maxValue = Math.Max(0, TrackCount - capacity);
        if (bounds.Height <= 0 || maxValue == 0) return new VerticalScrollGeometry(bounds, Rectangle.Empty, maxValue);

        var thumbHeight = Math.Clamp(
            (int)Math.Round(bounds.Height * Math.Min(1d, capacity / (double)Math.Max(1, TrackCount))),
            18,
            Math.Max(18, bounds.Height));
        thumbHeight = Math.Min(bounds.Height, thumbHeight);
        var travel = Math.Max(0, bounds.Height - thumbHeight);
        var ratio = _firstVisibleTrack / (double)maxValue;
        var thumbTop = bounds.Top + (int)Math.Round(Math.Clamp(ratio, 0d, 1d) * travel);
        return new VerticalScrollGeometry(bounds, new Rectangle(bounds.Left, thumbTop, bounds.Width, thumbHeight), maxValue);
    }

    private static Rectangle SoloButtonBounds(TimelineLayout layout)
    {
        var all = AllButtonBounds(layout);
        return new Rectangle(Math.Max(4, all.Left - SoloButtonWidth - 5), 5, SoloButtonWidth, 22);
    }

    private static Rectangle AllButtonBounds(TimelineLayout layout)
    {
        return new Rectangle(Math.Max(4, layout.TrackLeft - AllButtonWidth - 8), 5, AllButtonWidth, 22);
    }

    private static void DrawHeaderButton(Graphics graphics, Rectangle bounds, string text)
    {
        using var fillBrush = new SolidBrush(Theme.PanelStrong);
        using var borderPen = new Pen(Theme.Border);
        using var font = Theme.UiFont(8.5f);
        graphics.FillRectangle(fillBrush, bounds);
        graphics.DrawRectangle(borderPen, bounds);
        TextRenderer.DrawText(
            graphics,
            text,
            font,
            bounds,
            Theme.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private static void DrawVisibilityIcon(Graphics graphics, bool visible, float x, float y, Pen pen)
    {
        var bounds = new RectangleF(x - 8, y - 5, 16, 10);
        graphics.DrawEllipse(pen, bounds);
        if (visible)
        {
            using var fillBrush = new SolidBrush(pen.Color);
            graphics.FillEllipse(fillBrush, x - 2.4f, y - 2.4f, 4.8f, 4.8f);
        }
        else
        {
            graphics.DrawLine(pen, x - 8, y + 6, x + 8, y - 6);
        }
    }

    private readonly record struct TimelineLayout(
        int TrackLeft,
        int TrackRight,
        int RowTop,
        int RowBottom,
        int ScrollTop,
        Rectangle HeaderBounds,
        Rectangle GutterBounds,
        Rectangle RulerBounds,
        Rectangle GridBounds);

    private readonly record struct ScrollGeometry(
        Rectangle Bounds,
        Rectangle DecreaseButton,
        Rectangle IncreaseButton,
        Rectangle Track,
        Rectangle Thumb,
        int MaxValue);

    private readonly record struct VerticalScrollGeometry(Rectangle Bounds, Rectangle Thumb, int MaxValue);
}
