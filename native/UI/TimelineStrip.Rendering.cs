using System.Drawing.Drawing2D;
using System.Globalization;

namespace VectorAnimationEngine;

internal sealed partial class TimelineStrip : Control
{
    private void DrawShell(Graphics graphics, TimelineLayout layout, Rectangle clipBounds)
    {
        using var headerBrush = new SolidBrush(Theme.Panel);
        using var gutterBrush = new SolidBrush(ThemeNeutral(Color.FromArgb(28, 31, 34), Theme.PanelStrong));
        using var borderPen = new Pen(ThemeNeutral(Color.FromArgb(82, 91, 96), Theme.BorderHover));
        using var softPen = new Pen(ThemeNeutral(Color.FromArgb(48, 54, 58), Theme.Border));
        using var titleFont = Theme.UiFont(10, FontStyle.Bold);

        var headerPaintBounds = Rectangle.Intersect(layout.HeaderBounds, clipBounds);
        var gutterPaintBounds = Rectangle.Intersect(layout.GutterBounds, clipBounds);
        if (headerPaintBounds.Width > 0 && headerPaintBounds.Height > 0)
        {
            FillAlignedRectangle(graphics, headerBrush, headerPaintBounds);
        }
        if (gutterPaintBounds.Width > 0 && gutterPaintBounds.Height > 0)
        {
            FillAlignedRectangle(graphics, gutterBrush, gutterPaintBounds);
        }
        graphics.DrawLine(borderPen, 0, ToolbarHeaderHeight - 1, Width, ToolbarHeaderHeight - 1);
        graphics.DrawLine(borderPen, 0, HeaderHeight - 1, Width, HeaderHeight - 1);
        // The seam starts below the tab-group row: the toolbar header above it is pinned chrome, so a
        // divider that slides with the gutter would read as the header itself moving.
        graphics.DrawLine(borderPen, layout.TrackLeft - 1, HeaderHeight, layout.TrackLeft - 1, layout.ScrollTop);
        graphics.DrawLine(softPen, 0, layout.RowTop - 1, Width, layout.RowTop - 1);
        graphics.DrawLine(borderPen, 0, Height - 1, Width, Height - 1);
        DrawHeightResizeHandle(graphics);
        DrawGutterSplitterHandle(graphics, layout);

        var allBounds = AllButtonBounds(layout);
        var soloBounds = SoloButtonBounds(layout);
        var addLayerBounds = AddLayerButtonBounds(layout);
        var titleRight = Math.Max(42, addLayerBounds.Left - 6);
        TextRenderer.DrawText(
            graphics,
                UiLocalization.T(_shotFilterActive ? "Cameras" : "Timeline"),
                titleFont,
                Rectangle.FromLTRB(10, 1, titleRight, ToolbarHeaderHeight - 1),
                Theme.ReadableText(Theme.Panel, Theme.Text),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        if (!_shotFilterActive)
        {
            DrawHeaderButton(
                graphics,
                soloBounds,
                UiLocalization.T("Solo"),
                _hoveredHeaderCommand == HeaderCommand.Solo);
            DrawHeaderButton(
                graphics,
                allBounds,
                UiLocalization.T("All"),
                _hoveredHeaderCommand == HeaderCommand.All);
            DrawHeaderIconButton(
                graphics,
                addLayerBounds,
                SvgIconKind.Add,
                _hoveredHeaderCommand == HeaderCommand.AddLayer);
        }

        var frameStatusBounds = HeaderFrameStatusBounds(layout);
        var summaryRight = frameStatusBounds.Right;
        var contextName = _sceneDefinition?.Name ?? _drawingObjectDefinition?.Name ?? "Drawing Timeline";
        var name = ResolveTimelineHeaderLabel(contextName);
        var summaryLeft = frameStatusBounds.Left;
        var summaryWidth = Math.Max(0, summaryRight - summaryLeft);
        var timeText = FormatCursorTimeSeconds(CurrentFrame, PlaybackFps);
        var timeWidth = TextRenderer.MeasureText(
                timeText,
                Font,
                Size.Empty,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width
            + ScaleTimelineMetric(8);
        var showTime = summaryWidth >= ScaleTimelineMetric(116);
        var timeLeft = showTime ? Math.Max(summaryLeft, summaryRight - timeWidth) : summaryRight;
        var frameSummaryRight = showTime ? Math.Max(summaryLeft, timeLeft - ScaleTimelineMetric(6)) : summaryRight;
        var frameSummaryWidth = Math.Max(0, frameSummaryRight - summaryLeft);
        var frameLabel = UiLocalization.T("Frame");
        var frameSummary = frameSummaryWidth < ScaleTimelineMetric(210)
            ? $"{frameLabel} {CurrentFrame} / {Math.Max(0, FrameCount - 1)}"
            : $"{UiLocalization.T(name)}    {frameLabel} {CurrentFrame} / {Math.Max(0, FrameCount - 1)}";
        if (frameSummaryWidth >= ScaleTimelineMetric(64))
        {
            TextRenderer.DrawText(
                graphics,
                frameSummary,
                Font,
                Rectangle.FromLTRB(summaryLeft, 1, frameSummaryRight, ToolbarHeaderHeight - 1),
                Theme.ReadableText(Theme.Panel, Theme.Muted),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }
        if (showTime)
        {
            TextRenderer.DrawText(
                graphics,
                timeText,
                Font,
                Rectangle.FromLTRB(timeLeft, 1, summaryRight, ToolbarHeaderHeight - 1),
                Theme.ReadableText(Theme.Panel, Theme.AccentLabel),
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        DrawTabGroupBar(graphics, layout, clipBounds);
    }

    private Rectangle HeaderFrameStatusBounds(TimelineLayout layout)
    {
        var frameWidthControlsBounds = FrameWidthControlsBounds(layout, Rectangle.Empty);
        var frameHeightControlsBounds = FrameHeightControlsBounds(layout, frameWidthControlsBounds);
        var onionControlsBounds = OnionSkinControlsBounds(
            layout,
            frameHeightControlsBounds.IsEmpty
                ? frameWidthControlsBounds
                : frameHeightControlsBounds);
        var autoKeyframeBounds = AutoKeyframeControlsBounds(layout);
        var motionTrackBounds = MotionTrackControlsBounds(layout);
        var firstControlsLeft = Math.Min(
            frameWidthControlsBounds.IsEmpty ? layout.TrackRight : frameWidthControlsBounds.Left,
            Math.Min(
                frameHeightControlsBounds.IsEmpty ? layout.TrackRight : frameHeightControlsBounds.Left,
                onionControlsBounds.IsEmpty ? layout.TrackRight : onionControlsBounds.Left));
        // The motion-track toggle sits between Auto Key and the onion-skin group, so the frame-status
        // summary and its right-aligned time text must stop before it; otherwise the time text is
        // painted on top of the toggle's label and the two strings overlap.
        if (!motionTrackBounds.IsEmpty) firstControlsLeft = Math.Min(firstControlsLeft, motionTrackBounds.Left);
        var left = autoKeyframeBounds.IsEmpty
            ? layout.TrackLeft + ScaleTimelineMetric(8)
            : autoKeyframeBounds.Right + ScaleTimelineMetric(8);
        var right = Math.Max(left, firstControlsLeft - ScaleTimelineMetric(8));
        return Rectangle.FromLTRB(left, 0, right, ToolbarHeaderHeight);
    }

    internal static double CursorTimeSeconds(int frame, decimal playbackFps)
    {
        return Math.Max(0, frame) / (double)Math.Max(1m, playbackFps);
    }

    internal static string FormatCursorTimeSeconds(int frame, decimal playbackFps)
    {
        return CursorTimeSeconds(frame, playbackFps).ToString("0.000", CultureInfo.InvariantCulture) + " s";
    }

    private void DrawHeightResizeHandle(Graphics graphics)
    {
        var y = Math.Max(2, HeightResizeHandleHeight / 2);
        var width = Math.Min(66, Math.Max(28, Width / 8));
        var left = Math.Max(0, (Width - width) / 2);
        var color = _draggingHeightResize
            ? Theme.Accent
            : Theme.ReadableUiColor(Theme.Panel, Theme.Muted);
        using var line = new Pen(color, 1.2f);
        graphics.DrawLine(line, left, y, left + width, y);
        for (var x = left + 4; x < left + width - 2; x += 6)
        {
            graphics.DrawLine(line, x, y - 2, x, y + 2);
        }
    }

    /// <summary>
    /// Grip on the gutter's right edge. It is always visible so the column reads as resizable, and it
    /// brightens on hover and while dragging to confirm the grab.
    /// </summary>
    private void DrawGutterSplitterHandle(Graphics graphics, TimelineLayout layout)
    {
        var bounds = GutterSplitterBounds(layout);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        var active = _draggingGutterSplitter || _hoveringGutterSplitter;
        var color = active ? Theme.Accent : Theme.ReadableUiColor(Theme.Panel, Theme.Muted);
        var centerX = layout.TrackLeft - 1f;

        using var line = new Pen(color, active ? 1.8f : 1.2f);
        graphics.DrawLine(line, centerX, bounds.Top, centerX, bounds.Bottom);

        // Three ribs in the layer header row advertise the seam without sitting on a data row.
        var middle = bounds.Top + RulerHeight * 0.5f;
        graphics.DrawLine(line, centerX - 2.5f, middle - 4f, centerX + 2.5f, middle - 4f);
        graphics.DrawLine(line, centerX - 2.5f, middle, centerX + 2.5f, middle);
        graphics.DrawLine(line, centerX - 2.5f, middle + 4f, centerX + 2.5f, middle + 4f);
    }

    private void DrawRuler(Graphics graphics, TimelineLayout layout, Rectangle clipBounds)
    {
        using var rulerBrush = new SolidBrush(ThemeNeutral(Color.FromArgb(23, 26, 29), Theme.Top));
        using var majorBrush = new SolidBrush(ThemeNeutral(Color.FromArgb(32, 37, 40), Theme.PanelStrong));
        using var selectedBrush = new SolidBrush(ThemeNeutral(
            Color.FromArgb(150, 177, 68, 65),
            Theme.Mix(Theme.Top, Theme.Danger, 0.22f)));
        using var hoverBrush = new SolidBrush(Color.FromArgb(34, Theme.Accent));
        using var gridPen = new Pen(ThemeNeutral(Color.FromArgb(64, 72, 77), Theme.BorderHover));
        using var minorPen = new Pen(ThemeNeutral(Color.FromArgb(48, 55, 59), Theme.Border));
        using var rulerFont = Theme.UiFont(7.5f);

        var rulerPaintBounds = Rectangle.Intersect(layout.RulerBounds, clipBounds);
        if (rulerPaintBounds.Width > 0 && rulerPaintBounds.Height > 0)
        {
            FillAlignedRectangle(graphics, rulerBrush, rulerPaintBounds);
        }
        DrawShotRangeBands(graphics, layout, clipBounds);
        var layerHeaderBounds = Rectangle.FromLTRB(0, HeaderHeight, layout.TrackLeft, layout.RowTop);
        if (clipBounds.IntersectsWith(layerHeaderBounds))
        {
            if (_shotFilterActive)
            {
                SvgIcons.Draw(
                    graphics,
                    SvgIconKind.Camera,
                    new Rectangle(ScaleTimelineMetric(9), HeaderHeight + ScaleTimelineMetric(7), ScaleTimelineMetric(15), ScaleTimelineMetric(15)),
                    Theme.ReadableUiColor(Theme.Top, Theme.Accent));
                TextRenderer.DrawText(
                    graphics,
                    UiLocalization.T("Camera"),
                    Font,
                    new Rectangle(
                        ScaleTimelineMetric(30),
                        HeaderHeight,
                        Math.Max(24, layout.TrackLeft - ScaleTimelineMetric(38)),
                        RulerHeight),
                    Theme.ReadableText(Theme.Top, Theme.Text),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
            else
            {
            using var headerIconPen = new Pen(Theme.ReadableUiColor(Theme.Top, Theme.Muted), 1.1f);
            using var activeHeaderIconPen = new Pen(Theme.ReadableUiColor(Theme.Top, Theme.Text), 1.2f);
            DrawMasterControlCell(
                graphics,
                new Rectangle(0, HeaderHeight, VisibilityColumnWidth, RulerHeight),
                HeaderCommand.AllVisibility);
            DrawMasterControlCell(
                graphics,
                new Rectangle(VisibilityColumnWidth, HeaderHeight, LockColumnWidth, RulerHeight),
                HeaderCommand.AllLocks);
            DrawMasterControlCell(
                graphics,
                new Rectangle(LockColumnRight, HeaderHeight, OutlineColumnWidth, RulerHeight),
                HeaderCommand.AllOutlines);

            var allLayersVisible = AreAllLayersVisible();
            var allLayersLocked = AreAllLayersLocked();
            var allLayersOutlined = AreAllLayersOutlined();
            DrawVisibilityIcon(
                graphics,
                allLayersVisible,
                14,
                HeaderHeight + RulerHeight / 2f,
                allLayersVisible ? headerIconPen : activeHeaderIconPen);
            DrawLockIcon(
                graphics,
                allLayersLocked,
                VisibilityColumnWidth + LockColumnWidth / 2,
                HeaderHeight + RulerHeight / 2,
                headerIconPen,
                headerIconPen);
            DrawOutlineSwatch(
                graphics,
                new Rectangle(LockColumnRight, HeaderHeight, OutlineColumnWidth, RulerHeight),
                allLayersOutlined ? Theme.Accent : Theme.Muted,
                outlined: allLayersOutlined);
            TextRenderer.DrawText(
                graphics,
                UiLocalization.T("Layer"),
                Font,
                new Rectangle(LayerControlsWidth + 7, HeaderHeight, Math.Max(20, layout.TrackLeft - LayerControlsWidth - 19), RulerHeight),
                Theme.ReadableText(Theme.Top, Theme.Muted),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
        }

        var state = graphics.Save();
        graphics.SetClip(Rectangle.FromLTRB(layout.TrackLeft, HeaderHeight, layout.TrackRight, layout.RowTop), CombineMode.Intersect);
        var (firstColumn, lastColumnExclusive) = VisibleFramePaintRange(layout, clipBounds, includeLeadingColumn: true);
        for (var column = firstColumn; column < lastColumnExclusive; column++)
        {
            var frame = _firstVisibleFrame + column;
            var x = layout.TrackLeft + column * _frameCellWidth;
            var bounds = new Rectangle(x, HeaderHeight, _frameCellWidth, RulerHeight);
            if (!graphics.IsVisible(bounds)) continue;
            var inTimelineRange = frame <= EndFrame;
            var inSelectableRange = frame <= MaximumSelectableFrame(layout);
            var major = frame == StartFrame || frame % 5 == 0;
            var textBackground = Theme.Top;
            if (inTimelineRange && major) graphics.FillRectangle(majorBrush, bounds);
            if (inTimelineRange && major) textBackground = Theme.PanelStrong;
            if (inSelectableRange && frame == _hoverFrame && frame != CurrentFrame) graphics.FillRectangle(hoverBrush, bounds);
            if (inSelectableRange && frame == _hoverFrame && frame != CurrentFrame)
            {
                textBackground = Theme.Mix(textBackground, Theme.Accent, 34f / 255f);
            }
            if (inTimelineRange && frame == CurrentFrame) graphics.FillRectangle(selectedBrush, bounds);
            if (inTimelineRange && frame == CurrentFrame)
            {
                textBackground = Theme.Mix(Theme.Top, Theme.Danger, 0.22f);
            }

            graphics.DrawLine(inSelectableRange && major ? gridPen : minorPen, x, major ? HeaderHeight + 5 : HeaderHeight + 17, x, layout.RowTop - 1);
            if (!major) continue;
            TextRenderer.DrawText(
                graphics,
                frame.ToString(),
                rulerFont,
                new Rectangle(x + 2, HeaderHeight + 1, _frameCellWidth * 2 - 2, RulerHeight - 4),
                inTimelineRange && frame == CurrentFrame
                    ? Theme.ReadableText(textBackground, Theme.Danger)
                    : inTimelineRange
                        ? Theme.ReadableText(textBackground, Theme.Muted)
                        : Theme.ReadableText(textBackground, Theme.DisabledText),
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPadding);
        }

        graphics.DrawLine(gridPen, layout.TrackRight - 1, HeaderHeight, layout.TrackRight - 1, layout.RowTop);
        DrawShotRangeEdges(graphics, layout, clipBounds);
        DrawOnionSkinRangeHandles(graphics, layout);
        graphics.Restore(state);
    }

    private void DrawOnionSkinRangeHandles(Graphics graphics, TimelineLayout layout)
    {
        if (!TryGetOnionSkinRange(out var previousFrames, out var nextFrames, out var enabled) || !enabled) return;

        // With a track presented the handles mark its absolute window instead of an onion-skin offset
        // around the playhead, so they stay put however the playhead travels.
        var absolute = MotionTrackRangeVisible;
        var previousFrame = absolute
            ? MotionTrackRangeFirst
            : Math.Max(StartFrame, CurrentFrame - previousFrames);
        var nextFrame = absolute
            ? MotionTrackRangeLast
            : Math.Min(EndFrame, CurrentFrame + nextFrames);
        var previousX = FrameCenterX(previousFrame, layout);
        var currentX = FrameCenterX(CurrentFrame, layout);
        var nextX = FrameCenterX(nextFrame, layout);
        if (float.IsNaN(currentX)) return;

        var previousColor = Color.FromArgb(232, 224, 134, 126);
        var nextColor = Color.FromArgb(232, 111, 195, 218);
        var handleY = HeaderHeight + RulerHeight - ScaleTimelineMetric(7);
        using var previousPen = new Pen(previousColor, ScaleTimelineMetric(1));
        using var nextPen = new Pen(nextColor, ScaleTimelineMetric(1));
        if (absolute)
        {
            // One span from the first frame to the last: the playhead is not an endpoint any more.
            if (!float.IsNaN(previousX) && !float.IsNaN(nextX) && nextFrame != previousFrame)
            {
                graphics.DrawLine(previousPen, previousX, handleY, nextX, handleY);
            }
        }
        else
        {
            if (!float.IsNaN(previousX) && previousFrame != CurrentFrame)
            {
                graphics.DrawLine(previousPen, previousX, handleY, currentX, handleY);
            }
            if (!float.IsNaN(nextX) && nextFrame != CurrentFrame)
            {
                graphics.DrawLine(nextPen, currentX, handleY, nextX, handleY);
            }
        }

        DrawOnionSkinRangeHandle(
            graphics,
            OnionSkinRangeHandleBounds(TimelineOnionSkinRangeHandle.Previous, layout),
            previousColor,
            _hoveredOnionSkinRangeHandle == TimelineOnionSkinRangeHandle.Previous
                || _draggingOnionSkinRangeHandle == TimelineOnionSkinRangeHandle.Previous);
        DrawOnionSkinRangeHandle(
            graphics,
            OnionSkinRangeHandleBounds(TimelineOnionSkinRangeHandle.Next, layout),
            nextColor,
            _hoveredOnionSkinRangeHandle == TimelineOnionSkinRangeHandle.Next
                || _draggingOnionSkinRangeHandle == TimelineOnionSkinRangeHandle.Next);
    }

    private static void DrawOnionSkinRangeHandle(Graphics graphics, Rectangle bounds, Color color, bool emphasized)
    {
        if (bounds.IsEmpty) return;
        var grip = Rectangle.Inflate(bounds, -2, -1);
        var centerX = bounds.Left + bounds.Width / 2;
        using var stem = new Pen(Color.FromArgb(Math.Min(255, color.A + 18), color), emphasized ? 2f : 1.2f);
        using var fill = new SolidBrush(emphasized ? Color.FromArgb(255, color) : color);
        using var outline = new Pen(emphasized ? Color.White : Color.FromArgb(218, 222, 228, 228), 1f);
        graphics.DrawLine(stem, centerX, bounds.Top, centerX, grip.Top + 1);
        graphics.FillRectangle(fill, grip);
        graphics.DrawRectangle(outline, grip);
        using var detail = new Pen(Color.FromArgb(emphasized ? 224 : 148, Color.White), 1f);
        graphics.DrawLine(detail, centerX - 1, grip.Top + 2, centerX - 1, grip.Bottom - 2);
        graphics.DrawLine(detail, centerX + 1, grip.Top + 2, centerX + 1, grip.Bottom - 2);
    }

    private bool TryBeginOnionSkinRangeDrag(Point location, TimelineLayout layout)
    {
        var handle = HitTestOnionSkinRangeHandle(location, layout);
        if (handle == TimelineOnionSkinRangeHandle.None) return false;

        _draggingOnionSkinRangeHandle = handle;
        OnionSkinRangeInteractionStarted?.Invoke(this, EventArgs.Empty);
        Capture = true;
        Invalidate();
        return true;
    }

    private void UpdateOnionSkinRangeDrag(int x, TimelineLayout layout)
    {
        if (_draggingOnionSkinRangeHandle == TimelineOnionSkinRangeHandle.None
            || !TryGetOnionSkinRange(out var previousFrames, out var nextFrames, out var enabled)
            || !enabled)
        {
            return;
        }

        var targetFrame = FrameFromX(x, layout);
        if (MotionTrackRangeVisible)
        {
            // Dragging a handle moves that end of the absolute window; the other end stays put.
            if (_draggingOnionSkinRangeHandle == TimelineOnionSkinRangeHandle.Previous)
            {
                MotionTrackRangeChanged?.Invoke(
                    this,
                    new TimelineMotionTrackRangeChangedEventArgs(
                        Math.Clamp(targetFrame, StartFrame, MotionTrackRangeLast),
                        MotionTrackRangeLast));
            }
            else
            {
                MotionTrackRangeChanged?.Invoke(
                    this,
                    new TimelineMotionTrackRangeChangedEventArgs(
                        MotionTrackRangeFirst,
                        Math.Clamp(targetFrame, MotionTrackRangeFirst, EndFrame)));
            }

            Invalidate();
            return;
        }

        if (_draggingOnionSkinRangeHandle == TimelineOnionSkinRangeHandle.Previous)
        {
            previousFrames = Math.Clamp(
                CurrentFrame - targetFrame,
                0,
                Math.Min(VectorScene.MaximumOnionSkinFrames, CurrentFrame - StartFrame));
        }
        else
        {
            nextFrames = Math.Clamp(
                targetFrame - CurrentFrame,
                0,
                Math.Min(VectorScene.MaximumOnionSkinFrames, EndFrame - CurrentFrame));
        }

        OnionSkinRangeChanged?.Invoke(this, new TimelineOnionSkinRangeChangedEventArgs(previousFrames, nextFrames));
        Invalidate();
    }

    private TimelineOnionSkinRangeHandle HitTestOnionSkinRangeHandle(Point location, TimelineLayout layout)
    {
        if (!layout.RulerBounds.Contains(location)
            || !TryGetOnionSkinRange(out _, out _, out var enabled)
            || !enabled)
        {
            return TimelineOnionSkinRangeHandle.None;
        }
        var previous = OnionSkinRangeHandleBounds(TimelineOnionSkinRangeHandle.Previous, layout);
        var next = OnionSkinRangeHandleBounds(TimelineOnionSkinRangeHandle.Next, layout);
        if (previous.Contains(location) && next.Contains(location))
        {
            // In absolute mode the playhead is not between the handles any more, so the two are split
            // at the middle of their own overlap instead.
            var pivotX = MotionTrackRangeVisible
                ? (previous.Left + next.Right) * 0.5f
                : FrameCenterX(CurrentFrame, layout);
            return location.X <= pivotX
                ? TimelineOnionSkinRangeHandle.Previous
                : TimelineOnionSkinRangeHandle.Next;
        }

        if (previous.Contains(location)) return TimelineOnionSkinRangeHandle.Previous;
        return next.Contains(location) ? TimelineOnionSkinRangeHandle.Next : TimelineOnionSkinRangeHandle.None;
    }

    private Rectangle OnionSkinRangeHandleBounds(TimelineOnionSkinRangeHandle handle, TimelineLayout layout)
    {
        if (!TryGetOnionSkinRange(out var previousFrames, out var nextFrames, out var enabled) || !enabled)
        {
            return Rectangle.Empty;
        }
        var frame = handle switch
        {
            TimelineOnionSkinRangeHandle.Previous => MotionTrackRangeVisible
                ? MotionTrackRangeFirst
                : Math.Max(StartFrame, CurrentFrame - previousFrames),
            TimelineOnionSkinRangeHandle.Next => MotionTrackRangeVisible
                ? MotionTrackRangeLast
                : Math.Min(EndFrame, CurrentFrame + nextFrames),
            _ => -1
        };
        var centerX = FrameCenterX(frame, layout);
        if (float.IsNaN(centerX)) return Rectangle.Empty;

        var width = ScaleTimelineMetric(10);
        return new Rectangle(
            (int)Math.Round(centerX - width * 0.5f),
            HeaderHeight + RulerHeight - width,
            width,
            width);
    }

    private bool TryGetOnionSkinRange(out int previousFrames, out int nextFrames, out bool enabled)
    {
        previousFrames = 0;
        nextFrames = 0;
        enabled = false;
        if (_shotFilterActive) return false;
        if (DrawingScene() is { } drawingScene)
        {
            previousFrames = drawingScene.OnionSkinPreviousFrames;
            nextFrames = drawingScene.OnionSkinNextFrames;
            enabled = drawingScene.OnionSkinEnabled;
            return true;
        }

        // The scene composition timeline keeps the same range on its scene
        // definition, so its ruler handles drag exactly like the drawing ones.
        if (_sceneDefinition is not { } sceneDefinition) return false;
        previousFrames = sceneDefinition.OnionSkinPreviousFrames;
        nextFrames = sceneDefinition.OnionSkinNextFrames;
        enabled = sceneDefinition.OnionSkinEnabled;
        return true;
    }

    private float FrameCenterX(int frame, TimelineLayout layout)
    {
        if (frame < _firstVisibleFrame || frame >= _firstVisibleFrame + VisibleFrameDrawCount(layout)) return float.NaN;
        var center = layout.TrackLeft + (frame - _firstVisibleFrame) * _frameCellWidth + _frameCellWidth * 0.5f;
        return center < layout.TrackLeft || center >= layout.TrackRight ? float.NaN : center;
    }

    private void DrawTrackRows(Graphics graphics, TimelineLayout layout, Rectangle clipBounds)
    {
        using var rowBrush = new SolidBrush(ThemeNeutral(Color.FromArgb(29, 32, 35), Theme.Panel));
        using var alternateRowBrush = new SolidBrush(ThemeNeutral(
            Color.FromArgb(32, 35, 38),
            Theme.Mix(Theme.Panel, Theme.PanelStrong, 0.38f)));
        using var activeRowBrush = new SolidBrush(ThemeNeutral(Color.FromArgb(40, 46, 51), Theme.AccentSurface));
        using var selectedLayerRowBrush = new SolidBrush(ThemeNeutral(
            Color.FromArgb(38, 92, 139, 174),
            Color.FromArgb(34, Theme.Accent)));
        using var activeEdgeBrush = new SolidBrush(Theme.Accent);
        using var populatedExposureBrush = new SolidBrush(Color.FromArgb(62, 78, 126, 154));
        using var blankExposureBrush = new SolidBrush(Color.FromArgb(42, 105, 116, 122));
        using var populatedLinePen = new Pen(Color.FromArgb(184, 128, 174, 202), 1f);
        using var blankLinePen = new Pen(Color.FromArgb(150, 139, 151, 157), 1f);
        using var classicTweenBrush = new SolidBrush(Color.FromArgb(220, 92, 151, 222));
        using var shapeTweenBrush = new SolidBrush(Color.FromArgb(220, 78, 181, 132));
        using var gridPen = new Pen(ThemeNeutral(Color.FromArgb(44, 50, 54), Theme.Border));
        using var majorGridPen = new Pen(ThemeNeutral(Color.FromArgb(65, 73, 79), Theme.BorderHover));
        using var currentCellPen = new Pen(Color.FromArgb(176, 242, 94, 91), 1.2f);
        using var selectionPen = new Pen(Color.FromArgb(238, 105, 181, 230), 1.6f);
        using var hiddenBrush = new SolidBrush(ThemeNeutral(
            Color.FromArgb(126, 12, 14, 16),
            Color.FromArgb(36, Color.Black)));
        using var currentColumnBrush = new SolidBrush(Color.FromArgb(24, 240, 94, 91));
        using var hoverCellBrush = new SolidBrush(Color.FromArgb(30, 104, 181, 230));
        using var selectionBrush = new SolidBrush(Color.FromArgb(54, 68, 151, 207));
        using var layerDropFill = new SolidBrush(Color.FromArgb(52, Theme.Accent));
        using var layerDropPen = new Pen(Theme.Accent, 2f);
        using var layerDragShadow = new SolidBrush(Color.FromArgb(82, Color.Black));
        using var layerDragVeil = new SolidBrush(Color.FromArgb(34, Theme.Accent));
        using var layerDragEdge = new Pen(Color.FromArgb(224, Theme.Accent), 1.2f);
        using var eyePen = new Pen(ThemeNeutral(Color.FromArgb(214, 224, 224, 224), Theme.Text), 1.35f);
        using var hiddenEyePen = new Pen(ThemeNeutral(Color.FromArgb(130, 144, 148, 148), Theme.Muted), 1.2f);
        using var lockPen = new Pen(ThemeNeutral(Color.FromArgb(212, 224, 224, 224), Theme.Text), 1.25f);
        using var unlockedLockPen = new Pen(ThemeNeutral(Color.FromArgb(126, 144, 148, 148), Theme.Muted), 1.2f);

        var visibleTracks = VisibleTrackIndices();
        var visibleRows = VisibleTrackCapacity(layout);
        var activeTrack = GetActiveTrackIndex();
        var rowCount = Math.Min(visibleRows, Math.Max(0, visibleTracks.Count - _firstVisibleTrack));
        var paintLayerGutter = clipBounds.Left < layout.TrackLeft;

        if (rowCount == 0)
        {
            TextRenderer.DrawText(
                graphics,
                UiLocalization.T("No timeline tracks"),
                Font,
                layout.GridBounds,
                Theme.ReadableText(BackColor, Theme.Muted),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            return;
        }

        var graphicsState = graphics.Save();
        graphics.SetClip(Rectangle.FromLTRB(0, layout.RowTop, layout.TrackRight, layout.RowBottom), CombineMode.Intersect);
        var firstPaintRow = _draggingLayer
            ? 0
            : Math.Clamp((clipBounds.Top - layout.RowTop) / _rowHeight, 0, rowCount);
        var lastPaintRowExclusive = _draggingLayer
            ? rowCount
            : Math.Clamp(
                (int)Math.Ceiling((clipBounds.Bottom - layout.RowTop) / (double)_rowHeight),
                firstPaintRow,
                rowCount);

        void DrawRow(int visibleRow, bool dragGhost)
        {
            var trackIndex = visibleTracks[_firstVisibleTrack + visibleRow];
            var track = _timeline.Tracks[trackIndex];
            var y = layout.RowTop
                + visibleRow * _rowHeight
                + (dragGhost ? _layerDragPointerY - _layerDragStartPointerY : LayerDragOffset(trackIndex));
            var rowBounds = new Rectangle(0, y, layout.TrackRight, _rowHeight);
            if (!graphics.IsVisible(rowBounds)) return;
            if (dragGhost)
            {
                graphics.FillRectangle(
                    layerDragShadow,
                    new Rectangle(3, y + 3, Math.Max(1, layout.TrackRight - 6), _rowHeight));
            }
            var active = trackIndex == activeTrack;
            FillAlignedRectangle(
                graphics,
                active ? activeRowBrush : visibleRow % 2 == 0 ? rowBrush : alternateRowBrush,
                rowBounds);
            var visible = IsTrackVisible(trackIndex);
            if (paintLayerGutter)
            {
                var selectedLayer = IsTrackSelected(trackIndex);
                var layerItemBackground = LayerItemBackgroundColor(
                    GetTrackColor(trackIndex),
                    active,
                    selectedLayer,
                    visibleRow % 2 != 0);
                var layerItemBounds = new Rectangle(0, y, Math.Max(0, layout.TrackLeft), _rowHeight);
                using (var layerItemBrush = new SolidBrush(layerItemBackground))
                {
                    graphics.FillRectangle(layerItemBrush, layerItemBounds);
                }
                if (selectedLayer && !active)
                {
                    graphics.FillRectangle(selectedLayerRowBrush, 3, y + 1, Math.Max(0, layout.TrackLeft - 3), _rowHeight - 2);
                }
                if (active) graphics.FillRectangle(activeEdgeBrush, 0, y, 3, _rowHeight);

                var locked = IsTrackLocked(trackIndex);
                var textBackground = selectedLayer && !active
                    ? Theme.Mix(
                        layerItemBackground,
                        Theme.IsLight ? Theme.Accent : Color.FromArgb(92, 139, 174),
                        Theme.IsLight ? 34f / 255f : 38f / 255f)
                    : layerItemBackground;
                var preferredTextColor = !visible || locked ? Theme.Muted : active ? Theme.Text : Theme.Muted;
                var outlined = IsTrackOutlined(trackIndex);
                if (!IsSceneLightTrack(trackIndex) && !IsSceneShotTrack(trackIndex))
                {
                    DrawVisibilityIcon(graphics, visible, 14, y + _rowHeight / 2f, visible ? eyePen : hiddenEyePen);
                    DrawLockIcon(graphics, locked, VisibilityColumnWidth + LockColumnWidth / 2, y + _rowHeight / 2, lockPen, unlockedLockPen);
                    DrawOutlineSwatch(
                        graphics,
                        new Rectangle(LockColumnRight, y, OutlineColumnWidth, _rowHeight),
                        GetTrackColor(trackIndex),
                        outlined);
                }
                var layerDepth = GetTrackDisplayDepth(trackIndex);
                var labelLeft = GetTrackLabelLeft(trackIndex);
                DrawLayerHierarchyGuide(graphics, layerDepth, y, labelLeft, textBackground);
                if (IsTrackCollapsible(trackIndex)) DrawLayerGroupDisclosure(graphics, LayerGroupDisclosureBounds(trackIndex, y), IsTrackCollapsed(trackIndex));
                DrawTrackKindGlyph(graphics, trackIndex, labelLeft, y + _rowHeight / 2, textBackground);
                TextRenderer.DrawText(
                    graphics,
                    UiLocalization.T(GetTrackName(trackIndex)),
                    Font,
                    new Rectangle(labelLeft + 15, y, Math.Max(16, layout.TrackLeft - labelLeft - VerticalScrollWidth - 20), _rowHeight),
                    Theme.ReadableText(textBackground, preferredTextColor),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }

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
                classicTweenBrush,
                shapeTweenBrush,
                gridPen,
                majorGridPen,
                currentCellPen,
                hiddenBrush,
                currentColumnBrush,
                hoverCellBrush,
                clipBounds);

            if (!dragGhost) return;
            graphics.FillRectangle(layerDragVeil, rowBounds);
            graphics.DrawLine(layerDragEdge, 3, y + 1, layout.TrackRight - 2, y + 1);
            graphics.DrawLine(layerDragEdge, 3, y + _rowHeight - 2, layout.TrackRight - 2, y + _rowHeight - 2);
        }

        for (var visibleRow = firstPaintRow; visibleRow < lastPaintRowExclusive; visibleRow++)
        {
            var trackIndex = visibleTracks[_firstVisibleTrack + visibleRow];
            if (IsLayerDragGhost(trackIndex)) continue;
            DrawRow(visibleRow, dragGhost: false);
        }

        if (!_draggingLayer)
        {
            DrawSelectedTweenOverlay(graphics, layout, visibleTracks, rowCount);
            DrawFrameSelectionOverlay(
                graphics,
                layout,
                visibleTracks,
                rowCount,
                selectionBrush,
                selectionPen,
                clipBounds);
        }
        else
        {
            var nestedDrop = _layerDropPlacement is TimelineLayerDropPlacement.Inside or TimelineLayerDropPlacement.Mask;
            if (!nestedDrop)
            {
                for (var visibleRow = firstPaintRow; visibleRow < lastPaintRowExclusive; visibleRow++)
                {
                    var trackIndex = visibleTracks[_firstVisibleTrack + visibleRow];
                    if (IsLayerDragGhost(trackIndex)) DrawRow(visibleRow, dragGhost: true);
                }
            }
            DrawLayerDragDropIndicator(graphics, layout, layerDropFill, layerDropPen);
            if (nestedDrop) DrawNestedLayerDragGhost(graphics, layout);
        }

        graphics.Restore(graphicsState);
    }

    private void DrawLayerDragDropIndicator(
        Graphics graphics,
        TimelineLayout layout,
        Brush layerDropFill,
        Pen layerDropPen)
    {
        if (!_draggingLayer || !_layerDragPreviewValid || _layerDropTrack < 0) return;
        var visiblePosition = VisibleTrackPosition(_layerDropTrack);
        var visibleRow = visiblePosition - _firstVisibleTrack;
        if (visibleRow < 0 || visibleRow >= VisibleTrackCapacity(layout)) return;
        var y = layout.RowTop + visibleRow * _rowHeight + LayerDragOffset(_layerDropTrack);
        if (_layerDropPlacement is TimelineLayerDropPlacement.Inside or TimelineLayerDropPlacement.Mask)
        {
            var maximumInset = Math.Max(4, layout.TrackLeft - 10);
            var inset = Math.Clamp(
                LayerControlsWidth + 17 + GetTrackDisplayDepth(_layerDropTrack) * 12,
                4,
                maximumInset);
            graphics.FillRectangle(layerDropFill, inset, y + 2, Math.Max(8, layout.TrackRight - inset - 3), _rowHeight - 4);
            graphics.DrawRectangle(layerDropPen, inset, y + 2, Math.Max(7, layout.TrackRight - inset - 4), _rowHeight - 5);
            return;
        }

        var dropY = _layerDropPlacement == TimelineLayerDropPlacement.Before ? y : y + _rowHeight;
        graphics.DrawLine(layerDropPen, 4, dropY, layout.TrackRight - 1, dropY);
        using var marker = new SolidBrush(layerDropPen.Color);
        graphics.FillEllipse(marker, 2, dropY - 3, 7, 7);
    }

    private void DrawNestedLayerDragGhost(Graphics graphics, TimelineLayout layout)
    {
        if (string.IsNullOrWhiteSpace(_draggedLayerTrackId)) return;
        var sourceTrack = TrackIndexForId(_draggedLayerTrackId);
        if (sourceTrack < 0) return;
        var availableWidth = Math.Max(1, layout.TrackRight - layout.TrackLeft - 18);
        var width = Math.Min(190, availableWidth);
        if (width < 48) return;

        var x = layout.TrackLeft + 10;
        var maximumTop = Math.Max(layout.RowTop + 2, layout.RowBottom - _rowHeight - 2);
        var y = Math.Clamp(_layerDragPointerY - _rowHeight / 2, layout.RowTop + 2, maximumTop);
        var bounds = new Rectangle(x, y, width, Math.Max(16, _rowHeight - 2));
        using var shadow = new SolidBrush(Color.FromArgb(88, Color.Black));
        using var fill = new SolidBrush(Color.FromArgb(238, Theme.PanelStrong));
        using var border = new Pen(Color.FromArgb(232, Theme.Accent), 1.2f);
        using var colorBar = new SolidBrush(GetTrackColor(sourceTrack));
        graphics.FillRectangle(shadow, bounds.X + 2, bounds.Y + 2, bounds.Width, bounds.Height);
        graphics.FillRectangle(fill, bounds);
        graphics.FillRectangle(colorBar, bounds.X, bounds.Y, 4, bounds.Height);
        graphics.DrawRectangle(border, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
        DrawTrackKindGlyph(graphics, sourceTrack, bounds.X + 10, bounds.Top + bounds.Height / 2, Theme.PanelStrong);
        TextRenderer.DrawText(
            graphics,
            UiLocalization.T(GetTrackName(sourceTrack)),
            Font,
            new Rectangle(bounds.X + 26, bounds.Y, Math.Max(16, bounds.Width - 31), bounds.Height),
            Theme.ReadableText(Theme.PanelStrong, Theme.Text),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
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
        Brush classicTweenBrush,
        Brush shapeTweenBrush,
        Pen gridPen,
        Pen majorGridPen,
        Pen currentCellPen,
        Brush hiddenBrush,
        Brush currentColumnBrush,
        Brush hoverCellBrush,
        Rectangle clipBounds)
    {
        var state = graphics.Save();
        graphics.SetClip(new Rectangle(layout.TrackLeft, y, layout.TrackRight - layout.TrackLeft, _rowHeight), CombineMode.Intersect);
        var cameraTrack = IsSceneShotTrack(trackIndex);
        var cameraColor = GetTrackColor(trackIndex);
        using var cameraExposureBrush = cameraTrack
            ? new SolidBrush(Color.FromArgb(30, cameraColor.R, cameraColor.G, cameraColor.B))
            : null;
        using var cameraLinePen = cameraTrack
            ? new Pen(Color.FromArgb(130, cameraColor.R, cameraColor.G, cameraColor.B), 1f)
            : null;
        using var cameraTweenBrush = cameraTrack
            ? new SolidBrush(Color.FromArgb(88, cameraColor.R, cameraColor.G, cameraColor.B))
            : null;
        using var cameraTweenEdgePen = cameraTrack
            ? new Pen(Color.FromArgb(210, cameraColor.R, cameraColor.G, cameraColor.B), 1f)
            : null;
        using var cameraTweenCenterPen = cameraTrack
            ? new Pen(Color.FromArgb(150, cameraColor.R, cameraColor.G, cameraColor.B), 1f)
            : null;
        var (firstColumn, lastColumnExclusive) = VisibleFramePaintRange(layout, clipBounds);
        var maximumSelectableFrame = MaximumSelectableFrame(layout);
        var exposure = TimelineExposure.None(_firstVisibleFrame + firstColumn);
        var exposureEndFrame = _firstVisibleFrame + firstColumn - 1;
        for (var column = firstColumn; column < lastColumnExclusive; column++)
        {
            var frame = _firstVisibleFrame + column;
            var x = layout.TrackLeft + column * _frameCellWidth;
            var cellBounds = new Rectangle(x, y, _frameCellWidth, _rowHeight);
            if (!graphics.IsVisible(cellBounds)) continue;
            var inTimelineRange = frame <= EndFrame;
            var inSelectableRange = frame <= maximumSelectableFrame;
            var selected = false;
            if (inSelectableRange)
            {
                if (trackIndex == _hoverTrack && frame == _hoverFrame && frame != CurrentFrame)
                {
                    graphics.FillRectangle(hoverCellBrush, cellBounds);
                }
                if (frame == CurrentFrame) graphics.FillRectangle(currentColumnBrush, cellBounds);

                selected = _selectedFrameCells.Contains(new TimelineFrameCell(track.Id, frame));

                if (inTimelineRange)
                {
                    if (frame > exposureEndFrame)
                    {
                        exposure = track.EvaluateExposure(frame);
                        exposureEndFrame = exposure.SourceKind is null
                            ? track.Duration <= frame ? EndFrame : frame
                            : exposure.EndFrame;
                    }
                    if (exposure.SourceKind is { } sourceKind)
                    {
                        var effectiveKind = ResolveDisplayedKeyframeKind(
                            _sceneDefinition,
                            track.TargetId,
                            sourceKind,
                            frame);
                        var tween = effectiveKind == TimelineKeyframeKind.Populated
                            ? track.EvaluateTween(frame)
                            : null;
                        var tweenInterior = tween is { } span
                            && frame > span.StartFrame
                            && frame < span.EndFrame;
                        var isKeyframe = cameraTrack
                            ? ShouldDisplayKeyframeMarker(track, exposure, frame)
                            : exposure.SourceKeyframeFrame == frame && !tweenInterior;
                        var exposureBounds = new Rectangle(x + 1, y + 2, _frameCellWidth, _rowHeight - 4);
                        var exposureBrush = effectiveKind == TimelineKeyframeKind.Populated
                            ? cameraTrack ? cameraExposureBrush! : populatedExposureBrush
                            : blankExposureBrush;
                        var exposurePen = effectiveKind == TimelineKeyframeKind.Populated
                            ? cameraTrack ? cameraLinePen! : populatedLinePen
                            : blankLinePen;
                        graphics.FillRectangle(exposureBrush, exposureBounds);
                        var continuationStart = isKeyframe
                            ? x + _frameCellWidth / 2f + 3.5f
                            : x + 1;
                        graphics.DrawLine(
                            exposurePen,
                            continuationStart,
                            y + _rowHeight / 2f,
                            x + _frameCellWidth,
                            y + _rowHeight / 2f);
                        if (frame == exposure.EndFrame && !tweenInterior)
                        {
                            graphics.DrawLine(
                                exposurePen,
                                x + _frameCellWidth - 2,
                                y + 4,
                                x + _frameCellWidth - 2,
                                y + _rowHeight - 4);
                        }

                        if (isKeyframe)
                        {
                            DrawKeyframeMarker(
                                graphics,
                                effectiveKind,
                                x + _frameCellWidth / 2f,
                                y + _rowHeight / 2f,
                                cameraTrack,
                                cameraColor);
                        }

                        if (tween is { } tweenSpan)
                        {
                            if (cameraTrack && tweenSpan.Kind == TimelineTweenKind.Classic)
                            {
                                var bandTop = y + Math.Max(4, _rowHeight / 3);
                                var bandBottom = y + _rowHeight - Math.Max(4, _rowHeight / 4);
                                var bandHeight = Math.Max(4, bandBottom - bandTop);
                                graphics.FillRectangle(
                                    cameraTweenBrush!,
                                    x + 1,
                                    bandTop,
                                    _frameCellWidth,
                                    bandHeight);
                                graphics.DrawLine(
                                    cameraTweenEdgePen!,
                                    x + 1,
                                    bandTop,
                                    x + _frameCellWidth,
                                    bandTop);
                                graphics.DrawLine(
                                    cameraTweenEdgePen!,
                                    x + 1,
                                    bandBottom - 1,
                                    x + _frameCellWidth,
                                    bandBottom - 1);
                                graphics.DrawLine(
                                    cameraTweenCenterPen!,
                                    x + 1,
                                    bandTop + bandHeight / 2f,
                                    x + _frameCellWidth,
                                    bandTop + bandHeight / 2f);
                            }
                            else
                            {
                                graphics.FillRectangle(
                                    tweenSpan.Kind == TimelineTweenKind.Classic
                                        ? classicTweenBrush
                                        : shapeTweenBrush,
                                    x + 1,
                                    y + _rowHeight - 5,
                                    _frameCellWidth,
                                    3);
                            }
                        }
                    }
                }
            }

            graphics.DrawLine(inSelectableRange && frame % 5 == 0 ? majorGridPen : gridPen, x, y, x, y + _rowHeight);
            graphics.DrawLine(gridPen, x, y + _rowHeight - 1, x + _frameCellWidth, y + _rowHeight - 1);
            if (inSelectableRange && !visible) graphics.FillRectangle(hiddenBrush, cellBounds);

            if (!selected && active && frame == CurrentFrame)
            {
                graphics.DrawRectangle(currentCellPen, x + 1, y + 1, _frameCellWidth - 3, _rowHeight - 3);
            }
        }

        graphics.Restore(state);
    }

    private void DrawFrameSelectionOverlay(
        Graphics graphics,
        TimelineLayout layout,
        IReadOnlyList<int> visibleTracks,
        int rowCount,
        Brush selectionBrush,
        Pen selectionPen,
        Rectangle clipBounds)
    {
        if (_selectedFrameCells.Count == 0 || rowCount == 0) return;

        var hasTransformPreview = _draggingFrameTransform && _frameTransformPreviewCells.Length > 0;
        IEnumerable<TimelineFrameCell> previewCells = hasTransformPreview
            ? _frameTransformPreviewCells
            : _selectedFrameCells;
        if (!TryResolveVisibleFrameSelectionBounds(previewCells, out var selection)) return;

        var transformBounds = GetFrameSelectionTransformBounds(layout, selection);
        var visualBounds = Rectangle.Inflate(transformBounds, ScaleTimelineMetric(6), ScaleTimelineMetric(6));
        if (!graphics.IsVisible(visualBounds)) return;

        IReadOnlySet<TimelineFrameCell> previewCellSet = hasTransformPreview
            ? _frameTransformPreviewCells.ToHashSet()
            : _selectedFrameCells;

        void DrawCells(IReadOnlySet<TimelineFrameCell> selected, Brush fill, Pen pen)
        {
            var columns = VisibleFrameDrawCount(layout);
            var maximumSelectableFrame = MaximumSelectableFrame(layout);
            var visibleCellCapacity = Math.Min(selected.Count, rowCount * columns);
            var visibleCellPoints = new List<Point>(visibleCellCapacity);
            if (selected.Count <= rowCount * columns)
            {
                foreach (var cell in selected)
                {
                    var trackPosition = VisibleTrackPosition(TrackIndexForId(cell.TrackId));
                    var visibleRow = trackPosition - _firstVisibleTrack;
                    var column = cell.Frame - _firstVisibleFrame;
                    if (visibleRow < 0
                        || visibleRow >= rowCount
                        || column < 0
                        || column >= columns
                        || cell.Frame > maximumSelectableFrame)
                    {
                        continue;
                    }

                    var cellBounds = new Rectangle(
                        layout.TrackLeft + column * _frameCellWidth,
                        layout.RowTop + visibleRow * _rowHeight,
                        _frameCellWidth,
                        _rowHeight);
                    if (!graphics.IsVisible(cellBounds)) continue;
                    visibleCellPoints.Add(new Point(column, visibleRow));
                }
            }
            else
            {
                var (firstColumn, lastColumnExclusive) = VisibleFramePaintRange(layout, clipBounds);
                for (var visibleRow = 0; visibleRow < rowCount; visibleRow++)
                {
                    var trackIndex = visibleTracks[_firstVisibleTrack + visibleRow];
                    var trackId = _timeline.Tracks[trackIndex].Id;
                    for (var column = firstColumn; column < lastColumnExclusive; column++)
                    {
                        var frame = _firstVisibleFrame + column;
                        var cellBounds = new Rectangle(
                            layout.TrackLeft + column * _frameCellWidth,
                            layout.RowTop + visibleRow * _rowHeight,
                            _frameCellWidth,
                            _rowHeight);
                        if (frame > maximumSelectableFrame
                            || !graphics.IsVisible(cellBounds)
                            || !selected.Contains(new TimelineFrameCell(trackId, frame)))
                        {
                            continue;
                        }

                        visibleCellPoints.Add(new Point(column, visibleRow));
                    }
                }
            }

            foreach (var block in CoalesceFrameSelectionCells(visibleCellPoints))
            {
                var bounds = new Rectangle(
                    layout.TrackLeft + block.X * _frameCellWidth + 1,
                    layout.RowTop + block.Y * _rowHeight + 1,
                    Math.Max(1, block.Width * _frameCellWidth - 2),
                    Math.Max(1, block.Height * _rowHeight - 2));
                graphics.FillRectangle(fill, bounds);
                graphics.DrawRectangle(pen, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
            }
        }

        if (_draggingFrameTransform)
        {
            using var sourceGhostBrush = new SolidBrush(Color.FromArgb(28, Theme.Accent));
            using var sourceGhostPen = new Pen(Color.FromArgb(150, Theme.Muted), 1f) { DashStyle = DashStyle.Dash };
            DrawCells(_selectedFrameCells, sourceGhostBrush, sourceGhostPen);
        }
        DrawCells(previewCellSet, selectionBrush, selectionPen);

        using (var transformPen = new Pen(Color.FromArgb(235, Theme.AccentLabel), Math.Max(1.25f, DeviceDpi / 72f))
        {
            DashStyle = DashStyle.Dash
        })
        {
            graphics.DrawRectangle(
                transformPen,
                transformBounds.X,
                transformBounds.Y,
                Math.Max(0, transformBounds.Width - 1),
                Math.Max(0, transformBounds.Height - 1));
        }
        DrawFrameTransformHandles(graphics, transformBounds, selection);
    }

    private void DrawFrameTransformHandles(
        Graphics graphics,
        Rectangle bounds,
        TimelineFrameSelectionBounds selection)
    {
        var horizontalResizable = selection.FrameCount > 1;
        var verticalResizable = selection.TrackCount > 1;
        foreach (var mode in FrameTransformHandleModes)
        {
            var isHorizontal = mode is TimelineFrameTransformMode.ScaleLeft
                or TimelineFrameTransformMode.ScaleRight
                or TimelineFrameTransformMode.ScaleTopLeft
                or TimelineFrameTransformMode.ScaleTopRight
                or TimelineFrameTransformMode.ScaleBottomRight
                or TimelineFrameTransformMode.ScaleBottomLeft;
            var isVertical = mode is TimelineFrameTransformMode.ScaleTopLeft
                or TimelineFrameTransformMode.ScaleTopRight
                or TimelineFrameTransformMode.ScaleBottomRight
                or TimelineFrameTransformMode.ScaleBottomLeft;
            if (isHorizontal && !horizontalResizable || isVertical && !verticalResizable) continue;

            var handleBounds = FrameTransformHandleBounds(bounds, mode);
            var hovered = !_draggingFrameTransform && _hoveredFrameTransformHandle == mode;
            using var fill = new SolidBrush(hovered ? Theme.AccentLabel : Theme.PanelStrong);
            using var border = new Pen(hovered ? Theme.Text : Theme.Accent, Math.Max(1f, DeviceDpi / 96f));
            graphics.FillRectangle(fill, handleBounds);
            graphics.DrawRectangle(border, handleBounds.X, handleBounds.Y, handleBounds.Width - 1, handleBounds.Height - 1);
        }
    }

    private void DrawSelectedTweenOverlay(
        Graphics graphics,
        TimelineLayout layout,
        IReadOnlyList<int> visibleTracks,
        int rowCount)
    {
        if (SelectedTween is not { } selection || rowCount == 0) return;
        var trackIndex = TrackIndexForId(selection.TrackId);
        var visiblePosition = VisibleTrackPosition(trackIndex);
        var visibleRow = visiblePosition - _firstVisibleTrack;
        if (visiblePosition < 0 || visibleRow < 0 || visibleRow >= rowCount) return;

        var firstFrame = Math.Max(selection.StartFrame, _firstVisibleFrame);
        var lastVisibleFrame = _firstVisibleFrame + VisibleFrameDrawCount(layout) - 1;
        var lastFrame = Math.Min(selection.EndFrame, lastVisibleFrame);
        if (lastFrame < firstFrame) return;

        var bounds = new Rectangle(
            layout.TrackLeft + (firstFrame - _firstVisibleFrame) * _frameCellWidth + 1,
            layout.RowTop + visibleRow * _rowHeight + 1,
            Math.Max(1, (lastFrame - firstFrame + 1) * _frameCellWidth - 2),
            Math.Max(1, _rowHeight - 2));

        if (IsSceneShotTrack(trackIndex))
        {
            var cameraColor = GetTrackColor(trackIndex);
            var bandTop = bounds.Top + Math.Max(3, bounds.Height / 3);
            var bandBottom = bounds.Bottom - Math.Max(3, bounds.Height / 4);
            var bandHeight = Math.Max(4, bandBottom - bandTop);
            using var cameraFill = new SolidBrush(Color.FromArgb(
                66,
                cameraColor.R,
                cameraColor.G,
                cameraColor.B));
            using var cameraEdge = new Pen(Color.FromArgb(
                238,
                cameraColor.R,
                cameraColor.G,
                cameraColor.B), Math.Max(1.35f, DeviceDpi / 72f));
            using var cameraCenter = new Pen(Color.FromArgb(
                188,
                cameraColor.R,
                cameraColor.G,
                cameraColor.B), 1f);
            graphics.FillRectangle(cameraFill, bounds.Left, bandTop, bounds.Width, bandHeight);
            graphics.DrawRectangle(
                cameraEdge,
                bounds.Left,
                bandTop,
                Math.Max(0, bounds.Width - 1),
                Math.Max(0, bandHeight - 1));
            graphics.DrawLine(
                cameraCenter,
                bounds.Left,
                bandTop + bandHeight / 2f,
                bounds.Right - 1,
                bandTop + bandHeight / 2f);
            return;
        }

        using var fill = new SolidBrush(Color.FromArgb(30, Theme.Accent));
        using var pen = new Pen(Color.FromArgb(235, Theme.AccentLabel), Math.Max(1.25f, DeviceDpi / 72f));
        graphics.FillRectangle(fill, bounds);
        graphics.DrawRectangle(pen, bounds.X, bounds.Y, Math.Max(0, bounds.Width - 1), Math.Max(0, bounds.Height - 1));
    }

    internal static IReadOnlyList<Rectangle> CoalesceFrameSelectionCells(IEnumerable<Point> cells)
    {
        var remaining = new HashSet<Point>(cells);
        var blocks = new List<Rectangle>();
        while (remaining.Count > 0)
        {
            var topLeft = default(Point);
            var hasTopLeft = false;
            foreach (var cell in remaining)
            {
                if (hasTopLeft && (cell.Y > topLeft.Y || cell.Y == topLeft.Y && cell.X >= topLeft.X)) continue;
                topLeft = cell;
                hasTopLeft = true;
            }

            var width = 1;
            while (remaining.Contains(new Point(topLeft.X + width, topLeft.Y))) width++;

            var height = 1;
            while (true)
            {
                var nextY = topLeft.Y + height;
                var completeRow = true;
                for (var x = topLeft.X; x < topLeft.X + width; x++)
                {
                    if (remaining.Contains(new Point(x, nextY))) continue;
                    completeRow = false;
                    break;
                }

                if (!completeRow) break;
                height++;
            }

            for (var y = topLeft.Y; y < topLeft.Y + height; y++)
            {
                for (var x = topLeft.X; x < topLeft.X + width; x++) remaining.Remove(new Point(x, y));
            }

            blocks.Add(new Rectangle(topLeft.X, topLeft.Y, width, height));
        }

        return blocks;
    }

    private static void DrawKeyframeMarker(
        Graphics graphics,
        TimelineKeyframeKind kind,
        float centerX,
        float centerY,
        bool cameraTrack = false,
        Color? cameraColor = null)
    {
        const float radius = 3.25f;
        if (cameraTrack)
        {
            var markerColor = cameraColor is { } color
                ? Color.FromArgb(255, color.R, color.G, color.B)
                : ThemeNeutral(Color.FromArgb(240, 220, 228, 231), Theme.Text);
            var diamond = new[]
            {
                new PointF(centerX, centerY - radius - 0.5f),
                new PointF(centerX + radius + 0.5f, centerY),
                new PointF(centerX, centerY + radius + 0.5f),
                new PointF(centerX - radius - 0.5f, centerY)
            };
            using var outlinePen = new Pen(Theme.ReadableUiColor(Theme.Panel, Theme.Text), 1.15f);
            using var fillBrush = new SolidBrush(
                kind == TimelineKeyframeKind.Populated
                    ? markerColor
                    : ThemeNeutral(Color.FromArgb(31, 35, 38), Theme.Panel));
            graphics.FillPolygon(fillBrush, diamond);
            graphics.DrawPolygon(outlinePen, diamond);
            return;
        }

        var bounds = new RectangleF(centerX - radius, centerY - radius, radius * 2, radius * 2);
        using var roundOutlinePen = new Pen(ThemeNeutral(Color.FromArgb(232, 220, 228, 231), Theme.Text), 1.1f);
        if (kind == TimelineKeyframeKind.Populated)
        {
            using var fillBrush = new SolidBrush(ThemeNeutral(Color.FromArgb(240, 220, 228, 231), Theme.Text));
            graphics.FillEllipse(fillBrush, bounds);
            graphics.DrawEllipse(roundOutlinePen, bounds);
        }
        else
        {
            using var fillBrush = new SolidBrush(ThemeNeutral(Color.FromArgb(31, 35, 38), Theme.Panel));
            graphics.FillEllipse(fillBrush, bounds);
            graphics.DrawEllipse(roundOutlinePen, bounds);
        }
    }

    internal static bool ShouldDisplayKeyframeMarker(
        AnimationTimelineTrack track,
        TimelineExposure exposure,
        int frame)
    {
        if (!exposure.IsKeyframe || exposure.SourceKeyframeFrame != frame) return false;
        var tween = track.EvaluateTween(frame);
        return tween is not { } span || frame <= span.StartFrame || frame >= span.EndFrame;
    }

    internal static TimelineKeyframeKind ResolveDisplayedKeyframeKind(
        SceneDefinition? sceneDefinition,
        string targetId,
        TimelineKeyframeKind sourceKind,
        int frame = -1)
    {
        return sceneDefinition?.ResolveKeyframeKindForLayerContent(targetId, sourceKind, frame) ?? sourceKind;
    }

    internal void PlayCommandFeedback(TimelineCommand command, IEnumerable<TimelineFrameCell> cells)
    {
        var targets = cells
            .Where(cell => cell.Frame >= 0 && TrackIndexForId(cell.TrackId) >= 0)
            .Distinct()
            .Take(8192)
            .ToArray();
        if (targets.Length == 0) return;

        CancelCommandFeedback();
        _commandFeedback = command;
        _commandFeedbackCells = targets;
        _commandFeedbackStartedMilliseconds = Environment.TickCount64;
        _commandFeedbackTimer.Start();
        InvalidateCommandFeedbackBounds();
    }

    internal static TimelineFeedbackStyle ResolveCommandFeedbackStyle(TimelineCommand command)
    {
        return command switch
        {
            TimelineCommand.CopyFrames => new TimelineFeedbackStyle(
                TimelineFeedbackMotion.Outline,
                Color.FromArgb(112, 190, 236),
                240),
            TimelineCommand.PasteFrames => new TimelineFeedbackStyle(
                TimelineFeedbackMotion.Sweep,
                Color.FromArgb(79, 179, 162),
                320),
            TimelineCommand.ReverseFrames => new TimelineFeedbackStyle(
                TimelineFeedbackMotion.Reverse,
                Color.FromArgb(226, 168, 78),
                340),
            TimelineCommand.InsertFrames => new TimelineFeedbackStyle(
                TimelineFeedbackMotion.Expand,
                Color.FromArgb(94, 205, 190),
                300),
            TimelineCommand.DeleteFrames => new TimelineFeedbackStyle(
                TimelineFeedbackMotion.Contract,
                Color.FromArgb(232, 104, 104),
                280),
            TimelineCommand.InsertKeyframes => new TimelineFeedbackStyle(
                TimelineFeedbackMotion.Pulse,
                Color.FromArgb(112, 204, 255),
                360),
            TimelineCommand.InsertBlankKeyframes => new TimelineFeedbackStyle(
                TimelineFeedbackMotion.Ring,
                Color.FromArgb(232, 184, 92),
                420),
            TimelineCommand.ClearKeyframes => new TimelineFeedbackStyle(
                TimelineFeedbackMotion.Fade,
                Color.FromArgb(196, 207, 211),
                300),
            TimelineCommand.ClassicTween => new TimelineFeedbackStyle(
                TimelineFeedbackMotion.Flow,
                Color.FromArgb(126, 190, 236),
                420),
            TimelineCommand.ShapeTween => new TimelineFeedbackStyle(
                TimelineFeedbackMotion.Morph,
                Color.FromArgb(190, 150, 238),
                460),
            TimelineCommand.RemoveTween => new TimelineFeedbackStyle(
                TimelineFeedbackMotion.Dissolve,
                Color.FromArgb(232, 104, 104),
                300),
            _ => new TimelineFeedbackStyle(TimelineFeedbackMotion.Outline, Theme.Accent, 260)
        };
    }

    private void TickCommandFeedback()
    {
        if (_commandFeedback is not { } command)
        {
            _commandFeedbackTimer.Stop();
            return;
        }

        var style = ResolveCommandFeedbackStyle(command);
        if (Environment.TickCount64 - _commandFeedbackStartedMilliseconds >= style.DurationMilliseconds)
        {
            CancelCommandFeedback();
            return;
        }

        InvalidateCommandFeedbackBounds();
    }

    private void CancelCommandFeedback()
    {
        var dirtyBounds = CommandFeedbackBounds();
        _commandFeedbackTimer.Stop();
        _commandFeedback = null;
        _commandFeedbackCells = [];
        if (!dirtyBounds.IsEmpty && IsHandleCreated) Invalidate(dirtyBounds);
    }

    private void DrawCommandFeedback(Graphics graphics, TimelineLayout layout)
    {
        if (_commandFeedback is not { } command || _commandFeedbackCells.Length == 0) return;
        var style = ResolveCommandFeedbackStyle(command);
        var elapsed = Environment.TickCount64 - _commandFeedbackStartedMilliseconds;
        var progress = Math.Clamp(elapsed / (float)style.DurationMilliseconds, 0, 1);
        var eased = 1f - MathF.Pow(1f - progress, 3f);
        var fade = MathF.Pow(1f - progress, 1.35f);
        var pulse = MathF.Sin(progress * MathF.PI);
        var state = graphics.Save();
        graphics.SetClip(layout.GridBounds, CombineMode.Intersect);

        foreach (var cell in _commandFeedbackCells)
        {
            var bounds = CommandFeedbackCellBounds(layout, cell);
            if (bounds.IsEmpty || !graphics.IsVisible(bounds)) continue;
            DrawCommandFeedbackCell(graphics, bounds, style, eased, fade, pulse);
        }

        graphics.Restore(state);
    }

    private static void DrawCommandFeedbackCell(
        Graphics graphics,
        RectangleF bounds,
        TimelineFeedbackStyle style,
        float eased,
        float fade,
        float pulse)
    {
        var centerX = bounds.Left + bounds.Width * 0.5f;
        var centerY = bounds.Top + bounds.Height * 0.5f;
        switch (style.Motion)
        {
            case TimelineFeedbackMotion.Outline:
            {
                var inset = 1f + eased * 2f;
                using var pen = new Pen(WithOpacity(style.Color, fade), 1.4f) { DashStyle = DashStyle.Dash };
                graphics.DrawRectangle(pen, RectangleF.Inflate(bounds, -inset, -inset));
                break;
            }
            case TimelineFeedbackMotion.Sweep:
            {
                var width = Math.Max(1f, bounds.Width * eased);
                var sweep = new RectangleF(bounds.Left, bounds.Top + 1, width, Math.Max(1, bounds.Height - 2));
                using var fill = new SolidBrush(WithOpacity(style.Color, fade * 0.42f));
                using var edge = new Pen(WithOpacity(style.Color, fade), 1.4f);
                graphics.FillRectangle(fill, sweep);
                graphics.DrawLine(edge, sweep.Right, sweep.Top, sweep.Right, sweep.Bottom);
                break;
            }
            case TimelineFeedbackMotion.Reverse:
            {
                var halfWidth = Math.Max(2f, bounds.Width * (0.16f + eased * 0.34f));
                using var arrow = new Pen(WithOpacity(style.Color, fade), 1.45f)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.ArrowAnchor
                };
                graphics.DrawLine(arrow, centerX - halfWidth, centerY - 2f, centerX + halfWidth, centerY - 2f);
                graphics.DrawLine(arrow, centerX + halfWidth, centerY + 2f, centerX - halfWidth, centerY + 2f);
                break;
            }
            case TimelineFeedbackMotion.Expand:
            {
                var width = Math.Max(1f, bounds.Width * (0.12f + eased * 0.88f));
                var expanded = new RectangleF(centerX - width * 0.5f, bounds.Top + 2, width, Math.Max(1, bounds.Height - 4));
                using var fill = new SolidBrush(WithOpacity(style.Color, fade * 0.36f));
                using var edge = new Pen(WithOpacity(style.Color, fade), 1.2f);
                graphics.FillRectangle(fill, expanded);
                graphics.DrawRectangle(edge, expanded);
                break;
            }
            case TimelineFeedbackMotion.Contract:
            {
                var width = Math.Max(1f, bounds.Width * (1f - eased * 0.88f));
                var contracted = new RectangleF(centerX - width * 0.5f, bounds.Top + 2, width, Math.Max(1, bounds.Height - 4));
                using var fill = new SolidBrush(WithOpacity(style.Color, fade * 0.44f));
                using var edge = new Pen(WithOpacity(style.Color, fade), 1.2f);
                graphics.FillRectangle(fill, contracted);
                graphics.DrawRectangle(edge, contracted);
                break;
            }
            case TimelineFeedbackMotion.Pulse:
            {
                var radius = 3.25f + pulse * Math.Min(6f, bounds.Height * 0.32f);
                var circle = new RectangleF(centerX - radius, centerY - radius, radius * 2, radius * 2);
                using var fill = new SolidBrush(WithOpacity(style.Color, fade * 0.32f));
                using var ring = new Pen(WithOpacity(style.Color, fade), 1.6f);
                graphics.FillEllipse(fill, circle);
                graphics.DrawEllipse(ring, circle);
                break;
            }
            case TimelineFeedbackMotion.Ring:
            {
                var radius = 3.25f + eased * Math.Min(8f, bounds.Height * 0.42f);
                using var ring = new Pen(WithOpacity(style.Color, fade), 1.6f);
                graphics.DrawEllipse(ring, centerX - radius, centerY - radius, radius * 2, radius * 2);
                break;
            }
            case TimelineFeedbackMotion.Fade:
            {
                var radius = 3.6f * (1f - eased * 0.32f);
                using var ghost = new Pen(WithOpacity(style.Color, fade), 1.3f);
                graphics.DrawEllipse(ghost, centerX - radius, centerY - radius, radius * 2, radius * 2);
                graphics.DrawLine(ghost, centerX - radius, centerY - radius, centerX + radius, centerY + radius);
                break;
            }
            case TimelineFeedbackMotion.Flow:
            {
                var startX = bounds.Left + 2;
                var endX = bounds.Left + Math.Max(2, (bounds.Width - 4) * eased + 2);
                using var line = new Pen(WithOpacity(style.Color, fade), 1.7f)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.ArrowAnchor
                };
                graphics.DrawLine(line, startX, centerY, endX, centerY);
                break;
            }
            case TimelineFeedbackMotion.Morph:
            {
                var radius = 2.8f + pulse * Math.Min(5f, bounds.Height * 0.3f);
                var points = new[]
                {
                    new PointF(centerX, centerY - radius),
                    new PointF(centerX + radius, centerY),
                    new PointF(centerX, centerY + radius),
                    new PointF(centerX - radius, centerY)
                };
                using var fill = new SolidBrush(WithOpacity(style.Color, fade * 0.36f));
                using var edge = new Pen(WithOpacity(style.Color, fade), 1.4f);
                graphics.FillPolygon(fill, points);
                graphics.DrawPolygon(edge, points);
                break;
            }
            case TimelineFeedbackMotion.Dissolve:
            {
                var halfWidth = Math.Max(1f, bounds.Width * 0.5f * (1f - eased));
                using var line = new Pen(WithOpacity(style.Color, fade), 1.5f)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                };
                graphics.DrawLine(line, centerX - halfWidth, centerY, centerX + halfWidth, centerY);
                var offset = eased * Math.Min(5f, bounds.Height * 0.3f);
                graphics.DrawLine(line, centerX - 2, centerY - offset, centerX - 2, centerY - offset - 1);
                graphics.DrawLine(line, centerX + 2, centerY + offset, centerX + 2, centerY + offset + 1);
                break;
            }
        }
    }

    private RectangleF CommandFeedbackCellBounds(TimelineLayout layout, TimelineFrameCell cell)
    {
        var trackIndex = TrackIndexForId(cell.TrackId);
        var visiblePosition = VisibleTrackPosition(trackIndex);
        var visibleRow = visiblePosition - _firstVisibleTrack;
        var column = cell.Frame - _firstVisibleFrame;
        if (trackIndex < 0
            || visibleRow < 0
            || visibleRow >= VisibleTrackCapacity(layout)
            || column < 0
            || column >= VisibleFrameDrawCount(layout))
        {
            return RectangleF.Empty;
        }

        return new RectangleF(
            layout.TrackLeft + column * _frameCellWidth,
            layout.RowTop + visibleRow * _rowHeight,
            _frameCellWidth,
            _rowHeight);
    }

    private Rectangle CommandFeedbackBounds()
    {
        if (_commandFeedbackCells.Length == 0 || ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            return Rectangle.Empty;
        }

        var layout = CreateLayout();
        var result = Rectangle.Empty;
        foreach (var cell in _commandFeedbackCells)
        {
            var cellBounds = Rectangle.Ceiling(CommandFeedbackCellBounds(layout, cell));
            if (cellBounds.IsEmpty) continue;
            result = result.IsEmpty ? cellBounds : Rectangle.Union(result, cellBounds);
        }

        if (result.IsEmpty) return result;
        result.Inflate(10, 10);
        return Rectangle.Intersect(result, layout.GridBounds);
    }

    private void InvalidateCommandFeedbackBounds()
    {
        var bounds = CommandFeedbackBounds();
        if (!bounds.IsEmpty) Invalidate(bounds);
    }

    private static Color WithOpacity(Color color, float opacity)
    {
        return Color.FromArgb((int)MathF.Round(255 * Math.Clamp(opacity, 0, 1)), color.R, color.G, color.B);
    }

    private static Color ThemeNeutral(Color darkColor, Color lightColor)
    {
        return Theme.IsLight ? lightColor : darkColor;
    }

    internal static TimelineLayerFeedbackStyle ResolveLayerFeedbackStyle(TimelineLayerFeedbackKind kind)
    {
        return kind switch
        {
            TimelineLayerFeedbackKind.Add => new TimelineLayerFeedbackStyle(Color.FromArgb(79, 179, 162), 360),
            TimelineLayerFeedbackKind.Remove => new TimelineLayerFeedbackStyle(Color.FromArgb(232, 104, 104), 320),
            _ => new TimelineLayerFeedbackStyle(Theme.Accent, 320)
        };
    }

    private TimelineLayerVisualSnapshot[] CaptureLayerVisualSnapshots()
    {
        if (_timeline is null) return [];
        var result = new List<TimelineLayerVisualSnapshot>();
        for (var trackIndex = 0; trackIndex < TrackCount; trackIndex++)
        {
            if (!IsTrackLayer(trackIndex)) continue;
            var visiblePosition = VisibleTrackPosition(trackIndex);
            if (visiblePosition < 0) continue;
            result.Add(new TimelineLayerVisualSnapshot(
                _timeline.Tracks[trackIndex].Id,
                visiblePosition,
                GetTrackName(trackIndex),
                GetTrackColor(trackIndex)));
        }

        return result.ToArray();
    }

    private void DetectLayerCollectionFeedback()
    {
        var current = CaptureLayerVisualSnapshots();
        if (_knownLayerVisuals.Length == 0)
        {
            _knownLayerVisuals = current;
            return;
        }

        var previousIds = _knownLayerVisuals
            .Select(item => item.TrackId)
            .ToHashSet(StringComparer.Ordinal);
        var currentIds = current
            .Select(item => item.TrackId)
            .ToHashSet(StringComparer.Ordinal);
        var targets = current
            .Where(item => !previousIds.Contains(item.TrackId))
            .Select(item => new TimelineLayerFeedbackTarget(TimelineLayerFeedbackKind.Add, item))
            .Concat(_knownLayerVisuals
                .Where(item => !currentIds.Contains(item.TrackId))
                .Select(item => new TimelineLayerFeedbackTarget(TimelineLayerFeedbackKind.Remove, item)))
            .ToArray();
        _knownLayerVisuals = current;
        if (targets.Length > 0) StartLayerFeedback(targets);
    }

    private void StartLayerFeedback(TimelineLayerFeedbackTarget[] targets)
    {
        if (targets.Length == 0) return;
        CancelLayerFeedback();
        _layerFeedbackTargets = targets;
        _layerFeedbackStartedMilliseconds = Environment.TickCount64;
        _layerFeedbackTimer.Start();
        InvalidateLayerFeedbackBounds();
    }

    private void TickLayerFeedback()
    {
        if (_layerFeedbackTargets.Length == 0)
        {
            _layerFeedbackTimer.Stop();
            return;
        }

        var duration = _layerFeedbackTargets.Max(target => ResolveLayerFeedbackStyle(target.Kind).DurationMilliseconds);
        if (Environment.TickCount64 - _layerFeedbackStartedMilliseconds >= duration)
        {
            CancelLayerFeedback();
            return;
        }

        InvalidateLayerFeedbackBounds();
    }

    private void CancelLayerFeedback()
    {
        var dirtyBounds = LayerFeedbackBounds();
        _layerFeedbackTimer.Stop();
        _layerFeedbackTargets = [];
        if (!dirtyBounds.IsEmpty && IsHandleCreated) Invalidate(dirtyBounds);
    }

    private void DrawLayerFeedback(Graphics graphics, TimelineLayout layout)
    {
        if (_layerFeedbackTargets.Length == 0) return;
        var elapsed = Environment.TickCount64 - _layerFeedbackStartedMilliseconds;
        var state = graphics.Save();
        graphics.SetClip(Rectangle.FromLTRB(0, layout.RowTop, layout.TrackRight, layout.RowBottom), CombineMode.Intersect);
        foreach (var target in _layerFeedbackTargets)
        {
            var bounds = LayerFeedbackTargetBounds(layout, target);
            if (bounds.IsEmpty || !graphics.IsVisible(bounds)) continue;
            var style = ResolveLayerFeedbackStyle(target.Kind);
            var progress = Math.Clamp(elapsed / (float)style.DurationMilliseconds, 0, 1);
            var eased = progress * progress * (3f - 2f * progress);
            var fade = MathF.Pow(1f - progress, 1.25f);
            if (target.Kind == TimelineLayerFeedbackKind.Add)
            {
                DrawLayerAddedFeedback(graphics, bounds, style, eased, fade);
            }
            else
            {
                DrawLayerRemovedFeedback(graphics, bounds, layout.TrackLeft, target.Snapshot, style, eased, fade);
            }
        }
        graphics.Restore(state);
    }

    private void DrawLayerAddedFeedback(
        Graphics graphics,
        RectangleF bounds,
        TimelineLayerFeedbackStyle style,
        float eased,
        float fade)
    {
        var revealHeight = Math.Max(1f, bounds.Height * eased);
        var hiddenHeight = Math.Max(0, (bounds.Height - revealHeight) * 0.5f);
        using var cover = new SolidBrush(ThemeNeutral(Color.FromArgb(29, 32, 35), Theme.Panel));
        if (hiddenHeight > 0.1f)
        {
            graphics.FillRectangle(cover, bounds.Left, bounds.Top, bounds.Width, hiddenHeight);
            graphics.FillRectangle(cover, bounds.Left, bounds.Bottom - hiddenHeight, bounds.Width, hiddenHeight);
        }

        var reveal = new RectangleF(bounds.Left, bounds.Top + hiddenHeight, bounds.Width, revealHeight);
        using var highlight = new SolidBrush(WithOpacity(style.Color, fade * 0.18f));
        using var edge = new Pen(WithOpacity(style.Color, fade), 1.4f);
        using var accent = new SolidBrush(WithOpacity(style.Color, fade));
        graphics.FillRectangle(highlight, reveal);
        graphics.DrawLine(edge, reveal.Left, reveal.Top, reveal.Right, reveal.Top);
        graphics.DrawLine(edge, reveal.Left, reveal.Bottom, reveal.Right, reveal.Bottom);
        graphics.FillRectangle(accent, bounds.Left, reveal.Top, 3, reveal.Height);
    }

    private void DrawLayerRemovedFeedback(
        Graphics graphics,
        RectangleF bounds,
        int trackLeft,
        TimelineLayerVisualSnapshot snapshot,
        TimelineLayerFeedbackStyle style,
        float eased,
        float fade)
    {
        var ghostHeight = Math.Max(2f, bounds.Height * (1f - eased * 0.88f));
        var ghost = new RectangleF(bounds.Left, bounds.Top + (bounds.Height - ghostHeight) * 0.5f, bounds.Width, ghostHeight);
        var feedbackBackground = ThemeNeutral(Color.FromArgb(38, 42, 46), Theme.PanelStrong);
        using var background = new SolidBrush(WithOpacity(feedbackBackground, fade * 0.92f));
        using var edge = new Pen(WithOpacity(style.Color, fade), 1.4f);
        using var layerColor = new SolidBrush(WithOpacity(snapshot.Color, fade));
        graphics.FillRectangle(background, ghost);
        graphics.DrawLine(edge, ghost.Left, ghost.Top, ghost.Right, ghost.Top);
        graphics.DrawLine(edge, ghost.Left, ghost.Bottom, ghost.Right, ghost.Bottom);
        graphics.FillRectangle(layerColor, LayerControlsWidth + 3, ghost.Top + 2, 4, Math.Max(1, ghost.Height - 4));
        if (ghost.Height >= 9)
        {
            var readableText = Theme.ReadableText(feedbackBackground, Theme.Text);
            var textColor = Theme.Mix(feedbackBackground, readableText, fade);
            TextRenderer.DrawText(
                graphics,
                snapshot.Name,
                Font,
                Rectangle.Round(new RectangleF(
                    LayerControlsWidth + 22,
                    ghost.Top,
                    Math.Max(16, trackLeft - LayerControlsWidth - 28),
                    ghost.Height)),
                textColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }
    }

    private RectangleF LayerFeedbackTargetBounds(TimelineLayout layout, TimelineLayerFeedbackTarget target)
    {
        var visiblePosition = target.Kind == TimelineLayerFeedbackKind.Add
            ? VisibleTrackPosition(TrackIndexForId(target.Snapshot.TrackId))
            : target.Snapshot.VisiblePosition;
        var visibleRow = visiblePosition - _firstVisibleTrack;
        if (visibleRow < 0 || visibleRow >= VisibleTrackCapacity(layout)) return RectangleF.Empty;
        return new RectangleF(0, layout.RowTop + visibleRow * _rowHeight, layout.TrackRight, _rowHeight);
    }

    private Rectangle LayerFeedbackBounds()
    {
        if (_layerFeedbackTargets.Length == 0 || ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            return Rectangle.Empty;
        }

        var layout = CreateLayout();
        var result = Rectangle.Empty;
        foreach (var target in _layerFeedbackTargets)
        {
            var bounds = Rectangle.Ceiling(LayerFeedbackTargetBounds(layout, target));
            if (bounds.IsEmpty) continue;
            result = result.IsEmpty ? bounds : Rectangle.Union(result, bounds);
        }
        return result.IsEmpty
            ? result
            : Rectangle.Intersect(result, Rectangle.FromLTRB(0, layout.RowTop, layout.TrackRight, layout.RowBottom));
    }

    private void InvalidateLayerFeedbackBounds()
    {
        var bounds = LayerFeedbackBounds();
        if (!bounds.IsEmpty) Invalidate(bounds);
    }

    private void DrawPlayhead(Graphics graphics, TimelineLayout layout)
    {
        if (CurrentFrame < _firstVisibleFrame) return;
        var column = CurrentFrame - _firstVisibleFrame;
        if (column >= VisibleFrameDrawCount(layout)) return;

        var centerX = layout.TrackLeft + column * _frameCellWidth + _frameCellWidth / 2f;
        if (centerX < layout.TrackLeft || centerX >= layout.TrackRight) return;

        var pulse = _isPlaying ? 0.55f : 0f;
        var playhead = Color.FromArgb(242, 94, 91);
        using var glowPen = new Pen(Color.FromArgb((int)Math.Round(42 + pulse * 68), playhead), 4.5f + pulse * 2.5f);
        using var linePen = new Pen(playhead, 1.5f + pulse * 0.35f);
        using var fillBrush = new SolidBrush(playhead);
        if (_isPlaying) graphics.DrawLine(glowPen, centerX, HeaderHeight, centerX, layout.RowBottom);
        graphics.DrawLine(linePen, centerX, HeaderHeight, centerX, layout.RowBottom);
        _playheadHandlePoints[0] = new PointF(centerX - 5, HeaderHeight);
        _playheadHandlePoints[1] = new PointF(centerX + 5, HeaderHeight);
        _playheadHandlePoints[2] = new PointF(centerX + 5, HeaderHeight + 8);
        _playheadHandlePoints[3] = new PointF(centerX, HeaderHeight + 13);
        _playheadHandlePoints[4] = new PointF(centerX - 5, HeaderHeight + 8);
        graphics.FillPolygon(fillBrush, _playheadHandlePoints);
    }

    private void DrawHorizontalScroll(Graphics graphics, TimelineLayout layout)
    {
        var scroll = GetHorizontalScrollGeometry(layout);
        using var backgroundBrush = new SolidBrush(ThemeNeutral(Color.FromArgb(20, 23, 25), Theme.Top));
        using var buttonBrush = new SolidBrush(ThemeNeutral(Color.FromArgb(35, 40, 43), Theme.PanelStrong));
        using var thumbBrush = new SolidBrush(ThemeNeutral(Color.FromArgb(75, 85, 90), Theme.BorderHover));
        using var borderPen = new Pen(ThemeNeutral(Color.FromArgb(55, 63, 68), Theme.Border));
        using var arrowBrush = new SolidBrush(ThemeNeutral(Color.FromArgb(190, 202, 202), Theme.Muted));

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
        using var backgroundBrush = new SolidBrush(ThemeNeutral(Color.FromArgb(19, 22, 24), Theme.Top));
        using var thumbBrush = new SolidBrush(ThemeNeutral(Color.FromArgb(75, 85, 90), Theme.BorderHover));
        graphics.FillRectangle(backgroundBrush, scroll.Bounds);
        graphics.FillRectangle(thumbBrush, scroll.Thumb);
    }

    private static void FillAlignedRectangle(Graphics graphics, Brush brush, Rectangle bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        var smoothingMode = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.None;
        graphics.FillRectangle(brush, bounds);
        graphics.SmoothingMode = smoothingMode;
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
}
