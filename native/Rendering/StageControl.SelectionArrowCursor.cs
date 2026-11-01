using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace VectorAnimationEngine;

internal sealed partial class StageControl
{
    internal enum SelectionArrowCursorFeedback
    {
        None,
        Move,
        Bend,
        Node,
        Insert
    }

    private readonly Dictionary<(bool WhiteArrow, SelectionArrowCursorFeedback Feedback),
        (Cursor Cursor, FillCursorHandle Handle)> _selectionArrowCursors = [];
    private int _selectionArrowCursorDpi;

    internal Cursor SelectionArrowCursorFor(bool whiteArrow, SelectionArrowCursorFeedback feedback)
    {
        if (_selectionArrowCursorDpi != DeviceDpi)
        {
            DisposeSelectionArrowCursors();
            _selectionArrowCursorDpi = DeviceDpi;
        }
        var key = (whiteArrow, feedback);
        if (!_selectionArrowCursors.TryGetValue(key, out var entry))
        {
            using var bitmap = CreateSelectionArrowCursorBitmap(whiteArrow, feedback, DeviceDpi);
            var scale = bitmap.Width / 32f;
            var hotspot = new Point((int)MathF.Round(4f * scale), (int)MathF.Round(2f * scale));
            var cursor = CreateSystemCursor(bitmap, hotspot, out var handle);
            entry = (cursor, handle);
            _selectionArrowCursors.Add(key, entry);
        }
        return entry.Cursor;
    }

    internal static Bitmap CreateSelectionArrowCursorBitmap(
        bool whiteArrow, SelectionArrowCursorFeedback feedback, int dpi)
    {
        var size = Math.Clamp((int)MathF.Round(32f * dpi / 96f), 32, 128);
        using var supersampled = RenderSelectionArrowCursorBitmap(whiteArrow, feedback, size * 4);
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using var attributes = new ImageAttributes();
            attributes.SetWrapMode(WrapMode.TileFlipXY);
            graphics.DrawImage(supersampled, new Rectangle(0, 0, size, size),
                0, 0, supersampled.Width, supersampled.Height, GraphicsUnit.Pixel, attributes);
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static Bitmap RenderSelectionArrowCursorBitmap(
        bool whiteArrow, SelectionArrowCursorFeedback feedback, int size)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.Transparent);
            graphics.ScaleTransform(size / 32f, size / 32f);
            graphics.TranslateTransform(4f, 2f);
            graphics.ScaleTransform(0.75f, 0.75f);
            graphics.TranslateTransform(-4f, -2f);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var pointer = SvgIcons.SelectionArrowPointer(new RectangleF(0, 0, 24, 24));
            var tip = pointer[0];
            var leftEdge = pointer[1];
            var tilt = MathF.Atan2(leftEdge.X - tip.X, leftEdge.Y - tip.Y) * 180f / MathF.PI;
            using var pointerRotation = new Matrix();
            pointerRotation.RotateAt(tilt - 5f, tip);
            pointerRotation.TransformPoints(pointer);
            using var pointerOutline = new Pen(whiteArrow ? Color.Black : Color.White, 1.4f) { LineJoin = LineJoin.Round };
            graphics.FillPolygon(whiteArrow ? Brushes.White : Brushes.Black, pointer);
            graphics.DrawPolygon(pointerOutline, pointer);
            var cornerMark = !whiteArrow && feedback == SelectionArrowCursorFeedback.Node;
            var markWidth = cornerMark || feedback == SelectionArrowCursorFeedback.Bend ? 2.1f : 1.4f;
            using var halo = new Pen(Color.White, markWidth + 2f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var outline = new Pen(Color.Black, markWidth) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var mark = new GraphicsPath();
            switch (feedback)
            {
                case SelectionArrowCursorFeedback.Move:
                    mark.AddLine(24.5f, 20.5f, 24.5f, 30.5f);
                    mark.StartFigure();
                    mark.AddLine(19.5f, 25.5f, 29.5f, 25.5f);
                    mark.StartFigure();
                    mark.AddLines([new PointF(22.5f, 22.5f), new PointF(24.5f, 20.5f), new PointF(26.5f, 22.5f)]);
                    mark.StartFigure();
                    mark.AddLines([new PointF(22.5f, 28.5f), new PointF(24.5f, 30.5f), new PointF(26.5f, 28.5f)]);
                    mark.StartFigure();
                    mark.AddLines([new PointF(21.5f, 23.5f), new PointF(19.5f, 25.5f), new PointF(21.5f, 27.5f)]);
                    mark.StartFigure();
                    mark.AddLines([new PointF(27.5f, 23.5f), new PointF(29.5f, 25.5f), new PointF(27.5f, 27.5f)]);
                    break;
                case SelectionArrowCursorFeedback.Bend:
                    mark.AddLine(8f, 32f, 12f, 32f);
                    mark.AddArc(2f, 12f, 20f, 20f, 90f, -90f);
                    mark.AddLine(22f, 22f, 22f, 20f);
                    break;
                case SelectionArrowCursorFeedback.Node:
                    if (whiteArrow)
                        mark.AddLine(21, 29, 28, 22);
                    else
                        mark.AddLines([new PointF(22, 20), new PointF(22, 32), new PointF(8, 32)]);
                    break;
                case SelectionArrowCursorFeedback.Insert:
                    mark.AddLine(24.5f, 20.5f, 24.5f, 29.5f);
                    mark.StartFigure();
                    mark.AddLine(20, 25, 29, 25);
                    break;
            }
            if (mark.PointCount > 0)
            {
                graphics.DrawPath(halo, mark);
                graphics.DrawPath(outline, mark);
            }
            if (whiteArrow && feedback == SelectionArrowCursorFeedback.Node)
            {
                graphics.DrawRectangle(halo, 25, 21, 4, 4);
                graphics.FillRectangle(Brushes.White, 25, 21, 4, 4);
                graphics.DrawRectangle(outline, 25, 21, 4, 4);
            }
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private void DisposeSelectionArrowCursors()
    {
        foreach (var entry in _selectionArrowCursors.Values)
        {
            if (ReferenceEquals(Cursor, entry.Cursor)) Cursor = Cursors.Default;
            entry.Cursor.Dispose();
            entry.Handle.Dispose();
        }
        _selectionArrowCursors.Clear();
        _selectionArrowCursorDpi = 0;
    }
}
