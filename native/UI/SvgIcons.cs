using System.Drawing.Drawing2D;

namespace VectorAnimationEngine;

internal enum SvgIconKind
{
    Menu,
    Select,
    Transform,
    Pan,
    Rectangle,
    Ellipse,
    Triangle,
    Polygon,
    Star,
    Line,
    Pen,
    SimplePen,
    Pencil,
    Brush,
    PressureBrush,
    MixingBrush,
    Text,
    Fill,
    InkBottle,
    Eyedropper,
    Gradient,
    Swatches,
    Eraser,
    Vault,
    Snap,
    Grid,
    GoldenSpiral,
    Objects,
    TightFit,
    Align,
    TextAlignLeft,
    TextAlignCenter,
    TextAlignRight,
    Angle,
    Ratio,
    RestoreSize,
    PolarGrid,
    PropertiesPanel,
    TimelinePanel,
    Folder,
    FolderPlus,
    Open,
    Add,
    Remove,
    ZoomIn,
    ZoomOut,
    ChevronUp,
    ChevronDown,
    Close,
    Info,
    Warning,
    Error,
    Question
}

internal static class SvgIcons
{
    public static void Draw(Graphics g, SvgIconKind kind, Rectangle bounds, Color color)
    {
        var oldMode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var primaryTool = IsPrimaryToolIcon(kind);
        var scale = Math.Clamp(Math.Min(bounds.Width, bounds.Height) / (float)Theme.IconButtonSize, 0.72f, 3f);
        var inset = (primaryTool ? 4f : 5f) * scale;
        var r = new RectangleF(bounds.X + inset, bounds.Y + inset, bounds.Width - inset * 2, bounds.Height - inset * 2);
        using var pen = new Pen(color, (primaryTool ? 2.15f : 1.8f) * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var thinPen = new Pen(Color.FromArgb(primaryTool ? 205 : 170, color), (primaryTool ? 1.35f : 1.1f) * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var fill = new SolidBrush(Color.FromArgb(primaryTool ? 72 : 42, color));
        using var solidFill = new SolidBrush(color);

        switch (kind)
        {
            case SvgIconKind.Menu:
                g.DrawLine(pen, P(r, 4, 6), P(r, 20, 6));
                g.DrawLine(pen, P(r, 4, 12), P(r, 20, 12));
                g.DrawLine(pen, P(r, 4, 18), P(r, 20, 18));
                break;
            case SvgIconKind.Select:
            {
                var pointer = new[] { P(r, 4, 2), P(r, 13, 21), P(r, 15, 13), P(r, 22, 12) };
                g.FillPolygon(fill, pointer);
                DrawPolygon(g, pen, pointer);
                break;
            }
            case SvgIconKind.Transform:
                DrawRectangle(g, thinPen, Rect(r, 5, 5, 14, 14));
                FillHandle(g, solidFill, r, 5, 5);
                FillHandle(g, solidFill, r, 19, 5);
                FillHandle(g, solidFill, r, 5, 19);
                FillHandle(g, solidFill, r, 19, 19);
                g.DrawLine(pen, P(r, 9, 15), P(r, 15, 9));
                DrawChevron(g, pen, P(r, 15, 9), 0);
                DrawChevron(g, pen, P(r, 9, 15), 2);
                break;
            case SvgIconKind.Pan:
            {
                using var hand = new GraphicsPath();
                hand.StartFigure();
                hand.AddBezier(P(r, 8, 13), P(r, 6, 10), P(r, 4, 11), P(r, 5, 14));
                hand.AddLine(P(r, 7, 19), P(r, 10, 22));
                hand.AddBezier(P(r, 10, 22), P(r, 15, 23), P(r, 20, 20), P(r, 20, 15));
                hand.AddLine(P(r, 20, 9), P(r, 20, 8));
                hand.AddBezier(P(r, 20, 8), P(r, 20, 6), P(r, 17, 6), P(r, 17, 8));
                hand.AddLine(P(r, 17, 11), P(r, 17, 5));
                hand.AddBezier(P(r, 17, 5), P(r, 17, 3), P(r, 14, 3), P(r, 14, 5));
                hand.AddLine(P(r, 14, 11), P(r, 14, 4));
                hand.AddBezier(P(r, 14, 4), P(r, 14, 2), P(r, 11, 2), P(r, 11, 4));
                hand.AddLine(P(r, 11, 12), P(r, 11, 6));
                hand.AddBezier(P(r, 11, 6), P(r, 11, 4), P(r, 8, 4), P(r, 8, 6));
                hand.CloseFigure();
                g.FillPath(fill, hand);
                g.DrawPath(pen, hand);
                g.DrawLine(thinPen, P(r, 11, 12), P(r, 11, 16));
                break;
            }
            case SvgIconKind.Rectangle:
                g.FillRectangle(fill, Rect(r, 5, 5, 14, 14));
                DrawRectangle(g, pen, Rect(r, 5, 5, 14, 14));
                break;
            case SvgIconKind.Ellipse:
                g.FillEllipse(fill, Rect(r, 4, 5, 16, 14));
                g.DrawEllipse(pen, Rect(r, 4, 5, 16, 14));
                break;
            case SvgIconKind.Triangle:
            {
                var triangle = new[] { P(r, 12, 4), P(r, 21, 20), P(r, 3, 20) };
                g.FillPolygon(fill, triangle);
                DrawPolygon(g, pen, triangle);
                break;
            }
            case SvgIconKind.Polygon:
            {
                var polygon = new[] { P(r, 12, 3), P(r, 21, 8), P(r, 21, 17), P(r, 12, 22), P(r, 3, 17), P(r, 3, 8) };
                g.FillPolygon(fill, polygon);
                DrawPolygon(g, pen, polygon);
                break;
            }
            case SvgIconKind.Star:
            {
                var star = Star(r);
                g.FillPolygon(fill, star);
                DrawPolygon(g, pen, star);
                break;
            }
            case SvgIconKind.Line:
                g.DrawLine(pen, P(r, 4, 20), P(r, 20, 4));
                g.FillEllipse(solidFill, Rect(r, 2.5f, 18.5f, 4, 4));
                g.DrawEllipse(pen, Rect(r, 18.5f, 2.5f, 4, 4));
                break;
            case SvgIconKind.Pen:
            {
                var nib = new[] { P(r, 6, 4), P(r, 18, 4), P(r, 21, 10), P(r, 12, 22), P(r, 3, 10) };
                g.FillPolygon(fill, nib);
                DrawPolygon(g, pen, nib);
                g.DrawLine(pen, P(r, 12, 12), P(r, 12, 21));
                g.FillEllipse(solidFill, Rect(r, 10, 8, 4, 4));
                break;
            }
            case SvgIconKind.SimplePen:
                g.DrawLine(thinPen, P(r, 4, 19), P(r, 8, 5));
                g.DrawLine(thinPen, P(r, 16, 19), P(r, 21, 6));
                g.DrawBezier(pen, P(r, 4, 19), P(r, 8, 5), P(r, 16, 19), P(r, 21, 6));
                FillHandle(g, solidFill, r, 4, 19);
                FillHandle(g, solidFill, r, 21, 6);
                break;
            case SvgIconKind.Pencil:
            {
                var body = new[] { P(r, 5, 17), P(r, 16, 6), P(r, 21, 11), P(r, 10, 22) };
                g.FillPolygon(fill, body);
                DrawPolygon(g, pen, body);
                var tip = new[] { P(r, 5, 17), P(r, 2, 23), P(r, 10, 22) };
                g.FillPolygon(solidFill, tip);
                DrawPolygon(g, thinPen, tip);
                g.DrawLine(thinPen, P(r, 16, 6), P(r, 21, 11));
                break;
            }
            case SvgIconKind.Brush:
                DrawBrushGlyph(g, pen, thinPen, fill, r, pressure: false);
                break;
            case SvgIconKind.PressureBrush:
                DrawBrushGlyph(g, pen, thinPen, fill, r, pressure: true);
                break;
            case SvgIconKind.MixingBrush:
                DrawBrushGlyph(g, pen, thinPen, fill, r, pressure: false);
                g.FillEllipse(solidFill, Rect(r, 2, 3, 4, 4));
                g.FillEllipse(fill, Rect(r, 7, 6, 4, 4));
                g.DrawEllipse(thinPen, Rect(r, 7, 6, 4, 4));
                g.DrawArc(pen, Rect(r, 2, 2, 10, 9), 205, 115);
                break;
            case SvgIconKind.Text:
                g.DrawLine(pen, P(r, 4, 4), P(r, 20, 4));
                g.DrawLine(pen, P(r, 12, 4), P(r, 12, 21));
                g.DrawLine(thinPen, P(r, 8, 21), P(r, 16, 21));
                break;
            case SvgIconKind.Fill:
            {
                var bucket = new[] { P(r, 7, 5), P(r, 20, 15), P(r, 13, 22), P(r, 2, 12) };
                g.FillPolygon(fill, bucket);
                DrawPolygon(g, pen, bucket);
                g.DrawArc(thinPen, Rect(r, 6, 2, 12, 10), 190, 160);
                g.DrawLine(pen, P(r, 4, 14), P(r, 18, 14));
                g.FillEllipse(solidFill, Rect(r, 19, 18, 4, 4));
                break;
            }
            case SvgIconKind.InkBottle:
            {
                var bottle = new[] { P(r, 8, 3), P(r, 16, 3), P(r, 17, 8), P(r, 20, 11), P(r, 18, 22), P(r, 6, 22), P(r, 4, 11), P(r, 7, 8) };
                g.FillPolygon(fill, bottle);
                DrawPolygon(g, pen, bottle);
                g.DrawLine(pen, P(r, 7, 8), P(r, 17, 8));
                g.DrawLine(thinPen, P(r, 7, 15), P(r, 18, 15));
                g.FillRectangle(solidFill, Rect(r, 9, 17, 6, 2));
                break;
            }
            case SvgIconKind.Eyedropper:
                g.DrawLine(pen, P(r, 5, 19), P(r, 16, 8));
                g.DrawLine(pen, P(r, 8, 22), P(r, 19, 11));
                DrawPolygon(g, pen, [P(r, 15, 4), P(r, 20, 9), P(r, 17, 12), P(r, 12, 7)]);
                g.DrawLine(thinPen, P(r, 4, 20), P(r, 9, 20));
                break;
            case SvgIconKind.Gradient:
                using (var gradient = new LinearGradientBrush(Rect(r, 3, 6, 18, 12), Color.FromArgb(238, color), Color.FromArgb(55, color), LinearGradientMode.Horizontal))
                {
                    g.FillRectangle(gradient, Rect(r, 3, 6, 18, 12));
                }
                DrawRectangle(g, pen, Rect(r, 3, 6, 18, 12));
                g.DrawLine(thinPen, P(r, 4, 20), P(r, 20, 4));
                g.FillEllipse(solidFill, Rect(r, 2.5f, 18.5f, 3, 3));
                g.DrawEllipse(pen, Rect(r, 18.5f, 2.5f, 3, 3));
                break;
            case SvgIconKind.Swatches:
            {
                var swatchSize = Math.Max(3f, r.Width * 0.28f);
                var gap = Math.Max(1.5f, r.Width * 0.10f);
                var swatchLeft = r.X + (r.Width - swatchSize * 2 - gap) * 0.5f;
                var swatchTop = r.Y + (r.Height - swatchSize * 2 - gap) * 0.5f;
                using (var first = new SolidBrush(Color.FromArgb(220, color))) g.FillRectangle(first, swatchLeft, swatchTop, swatchSize, swatchSize);
                using (var second = new SolidBrush(Color.FromArgb(150, color))) g.FillRectangle(second, swatchLeft + swatchSize + gap, swatchTop, swatchSize, swatchSize);
                using (var third = new SolidBrush(Color.FromArgb(85, color))) g.FillRectangle(third, swatchLeft, swatchTop + swatchSize + gap, swatchSize, swatchSize);
                using (var gradient = new LinearGradientBrush(
                    new RectangleF(swatchLeft + swatchSize + gap, swatchTop + swatchSize + gap, swatchSize, swatchSize),
                    Color.FromArgb(235, color),
                    Color.FromArgb(45, color),
                    LinearGradientMode.Horizontal))
                {
                    g.FillRectangle(gradient, swatchLeft + swatchSize + gap, swatchTop + swatchSize + gap, swatchSize, swatchSize);
                }
                g.DrawRectangle(thinPen, swatchLeft, swatchTop, swatchSize, swatchSize);
                g.DrawRectangle(thinPen, swatchLeft + swatchSize + gap, swatchTop, swatchSize, swatchSize);
                g.DrawRectangle(thinPen, swatchLeft, swatchTop + swatchSize + gap, swatchSize, swatchSize);
                g.DrawRectangle(thinPen, swatchLeft + swatchSize + gap, swatchTop + swatchSize + gap, swatchSize, swatchSize);
                break;
            }
            case SvgIconKind.Eraser:
            {
                var eraser = new[] { P(r, 8, 4), P(r, 21, 17), P(r, 14, 23), P(r, 2, 11) };
                g.FillPolygon(fill, eraser);
                DrawPolygon(g, pen, eraser);
                g.DrawLine(pen, P(r, 7, 16), P(r, 17, 6));
                g.DrawLine(thinPen, P(r, 13, 22), P(r, 22, 22));
                break;
            }
            case SvgIconKind.Vault:
                DrawRectangle(g, pen, Rect(r, 4, 5, 16, 15));
                g.DrawLine(thinPen, P(r, 4, 10), P(r, 20, 10));
                g.DrawEllipse(pen, Rect(r, 10, 12, 4, 4));
                break;
            case SvgIconKind.Folder:
                DrawPolygon(g, pen, [P(r, 3, 7), P(r, 9, 7), P(r, 11, 10), P(r, 21, 10), P(r, 21, 20), P(r, 3, 20)]);
                break;
            case SvgIconKind.FolderPlus:
                DrawPolygon(g, pen, [P(r, 3, 7), P(r, 9, 7), P(r, 11, 10), P(r, 21, 10), P(r, 21, 20), P(r, 3, 20)]);
                g.DrawLine(thinPen, P(r, 15, 12), P(r, 15, 18));
                g.DrawLine(thinPen, P(r, 12, 15), P(r, 18, 15));
                break;
            case SvgIconKind.Open:
                DrawRectangle(g, thinPen, Rect(r, 3, 5, 13, 14));
                g.DrawLine(pen, P(r, 9, 12), P(r, 21, 12));
                g.DrawLine(pen, P(r, 16, 7), P(r, 21, 12));
                g.DrawLine(pen, P(r, 21, 12), P(r, 16, 17));
                break;
            case SvgIconKind.Add:
                g.DrawLine(pen, P(r, 12, 4), P(r, 12, 20));
                g.DrawLine(pen, P(r, 4, 12), P(r, 20, 12));
                break;
            case SvgIconKind.Remove:
                g.DrawLine(pen, P(r, 4, 12), P(r, 20, 12));
                break;
            case SvgIconKind.ZoomIn:
            case SvgIconKind.ZoomOut:
                g.DrawEllipse(pen, Rect(r, 3, 3, 13, 13));
                g.DrawLine(pen, P(r, 15, 15), P(r, 21, 21));
                g.DrawLine(thinPen, P(r, 6, 9.5f), P(r, 13, 9.5f));
                if (kind == SvgIconKind.ZoomIn) g.DrawLine(thinPen, P(r, 9.5f, 6), P(r, 9.5f, 13));
                break;
            case SvgIconKind.ChevronUp:
                g.DrawLines(pen, [P(r, 5, 15), P(r, 12, 8), P(r, 19, 15)]);
                break;
            case SvgIconKind.ChevronDown:
                g.DrawLines(pen, [P(r, 5, 9), P(r, 12, 16), P(r, 19, 9)]);
                break;
            case SvgIconKind.Close:
                g.DrawLine(pen, P(r, 5, 5), P(r, 19, 19));
                g.DrawLine(pen, P(r, 19, 5), P(r, 5, 19));
                break;
            case SvgIconKind.Info:
            {
                g.DrawEllipse(pen, Rect(r, 3, 3, 18, 18));
                using var dot = new SolidBrush(color);
                g.FillEllipse(dot, Rect(r, 11, 6, 2, 2));
                g.DrawLine(pen, P(r, 12, 11), P(r, 12, 18));
                break;
            }
            case SvgIconKind.Warning:
            {
                DrawPolygon(g, pen, [P(r, 12, 2), P(r, 22, 21), P(r, 2, 21)]);
                g.DrawLine(pen, P(r, 12, 8), P(r, 12, 14));
                using var dot = new SolidBrush(color);
                g.FillEllipse(dot, Rect(r, 11, 17, 2, 2));
                break;
            }
            case SvgIconKind.Error:
                g.DrawEllipse(pen, Rect(r, 3, 3, 18, 18));
                g.DrawLine(pen, P(r, 8, 8), P(r, 16, 16));
                g.DrawLine(pen, P(r, 16, 8), P(r, 8, 16));
                break;
            case SvgIconKind.Question:
            {
                g.DrawEllipse(pen, Rect(r, 3, 3, 18, 18));
                g.DrawArc(pen, Rect(r, 8, 6, 8, 8), 195, 255);
                g.DrawLine(pen, P(r, 12, 13), P(r, 12, 16));
                using var dot = new SolidBrush(color);
                g.FillEllipse(dot, Rect(r, 11, 18, 2, 2));
                break;
            }
            case SvgIconKind.Snap:
                g.DrawLine(pen, P(r, 6, 6), P(r, 18, 18));
                g.DrawLine(thinPen, P(r, 6, 18), P(r, 18, 6));
                g.DrawEllipse(pen, Rect(r, 9, 9, 6, 6));
                break;
            case SvgIconKind.Grid:
                for (var i = 6; i <= 18; i += 6)
                {
                    g.DrawLine(thinPen, P(r, i, 3), P(r, i, 21));
                    g.DrawLine(thinPen, P(r, 3, i), P(r, 21, i));
                }
                DrawRectangle(g, pen, Rect(r, 3, 3, 18, 18));
                break;
            case SvgIconKind.GoldenSpiral:
            {
                DrawRectangle(g, thinPen, Rect(r, 3, 5, 18, 14));
                var points = new PointF[36];
                var center = P(r, 10, 13);
                const float beta = 0.3063489f;
                for (var index = 0; index < points.Length; index++)
                {
                    var amount = index / (float)(points.Length - 1);
                    var theta = -MathF.Tau * 1.75f + MathF.Tau * 1.75f * amount;
                    var radius = 8.5f * MathF.Exp(beta * theta);
                    points[index] = new PointF(
                        center.X + MathF.Cos(theta) * radius * r.Width / 24f,
                        center.Y + MathF.Sin(theta) * radius * r.Height / 24f);
                }
                g.DrawLines(pen, points);
                break;
            }
            case SvgIconKind.PolarGrid:
                g.DrawEllipse(thinPen, Rect(r, 3, 3, 18, 18));
                g.DrawEllipse(thinPen, Rect(r, 6, 6, 12, 12));
                g.DrawEllipse(pen, Rect(r, 9, 9, 6, 6));
                g.DrawLine(thinPen, P(r, 12, 2), P(r, 12, 22));
                g.DrawLine(thinPen, P(r, 2, 12), P(r, 22, 12));
                g.DrawLine(thinPen, P(r, 5, 5), P(r, 19, 19));
                g.DrawLine(thinPen, P(r, 19, 5), P(r, 5, 19));
                break;
            case SvgIconKind.PropertiesPanel:
                DrawRectangle(g, pen, Rect(r, 3, 4, 18, 16));
                g.FillRectangle(fill, Rect(r, 14, 5, 6, 14));
                g.DrawLine(thinPen, P(r, 14, 4), P(r, 14, 20));
                g.DrawLine(thinPen, P(r, 6, 8), P(r, 11, 8));
                g.DrawLine(thinPen, P(r, 6, 12), P(r, 11, 12));
                g.DrawLine(thinPen, P(r, 6, 16), P(r, 10, 16));
                break;
            case SvgIconKind.TimelinePanel:
                DrawRectangle(g, pen, Rect(r, 3, 4, 18, 16));
                g.FillRectangle(fill, Rect(r, 4, 13, 16, 6));
                g.DrawLine(thinPen, P(r, 3, 13), P(r, 21, 13));
                g.DrawLine(thinPen, P(r, 8, 14), P(r, 8, 19));
                g.DrawLine(thinPen, P(r, 13, 14), P(r, 13, 19));
                g.DrawLine(thinPen, P(r, 17, 14), P(r, 17, 19));
                g.DrawLine(pen, P(r, 10, 6), P(r, 10, 12));
                g.FillEllipse(fill, Rect(r, 8, 5, 4, 4));
                break;
            case SvgIconKind.Objects:
                DrawRectangle(g, thinPen, Rect(r, 5, 5, 8, 8));
                g.DrawEllipse(pen, Rect(r, 11, 11, 8, 8));
                break;
            case SvgIconKind.TightFit:
                DrawRectangle(g, thinPen, Rect(r, 5, 5, 14, 14));
                g.DrawLine(pen, P(r, 2, 12), P(r, 8, 12));
                g.DrawLine(pen, P(r, 16, 12), P(r, 22, 12));
                g.DrawLine(pen, P(r, 12, 2), P(r, 12, 8));
                g.DrawLine(pen, P(r, 12, 16), P(r, 12, 22));
                break;
            case SvgIconKind.Align:
                g.DrawLine(pen, P(r, 4, 4), P(r, 4, 20));
                g.DrawLine(thinPen, P(r, 8, 7), P(r, 20, 7));
                g.DrawLine(thinPen, P(r, 8, 12), P(r, 16, 12));
                g.DrawLine(thinPen, P(r, 8, 17), P(r, 21, 17));
                break;
            case SvgIconKind.TextAlignLeft:
                DrawTextAlignmentGlyph(g, pen, r, TextHorizontalAlignment.Left);
                break;
            case SvgIconKind.TextAlignCenter:
                DrawTextAlignmentGlyph(g, pen, r, TextHorizontalAlignment.Center);
                break;
            case SvgIconKind.TextAlignRight:
                DrawTextAlignmentGlyph(g, pen, r, TextHorizontalAlignment.Right);
                break;
            case SvgIconKind.Angle:
                g.DrawLine(pen, P(r, 5, 19), P(r, 19, 19));
                g.DrawLine(pen, P(r, 5, 19), P(r, 17, 7));
                g.DrawArc(thinPen, Rect(r, 7, 11, 10, 10), 225, 45);
                break;
            case SvgIconKind.Ratio:
                DrawRectangle(g, pen, Rect(r, 4, 7, 16, 10));
                g.DrawLine(thinPen, P(r, 8, 7), P(r, 8, 17));
                g.DrawLine(thinPen, P(r, 16, 7), P(r, 16, 17));
                break;
            case SvgIconKind.RestoreSize:
                DrawRectangle(g, thinPen, Rect(r, 5, 5, 14, 14));
                g.DrawLine(pen, P(r, 3, 12), P(r, 9, 12));
                g.DrawLine(pen, P(r, 15, 12), P(r, 21, 12));
                DrawChevron(g, pen, P(r, 9, 12), 1);
                DrawChevron(g, pen, P(r, 15, 12), 3);
                break;
        }

        g.SmoothingMode = oldMode;
    }

    private static bool IsPrimaryToolIcon(SvgIconKind kind)
    {
        return kind is SvgIconKind.Select
            or SvgIconKind.Transform
            or SvgIconKind.Pan
            or SvgIconKind.Rectangle
            or SvgIconKind.Ellipse
            or SvgIconKind.Triangle
            or SvgIconKind.Polygon
            or SvgIconKind.Star
            or SvgIconKind.Line
            or SvgIconKind.Pen
            or SvgIconKind.SimplePen
            or SvgIconKind.Pencil
            or SvgIconKind.Brush
            or SvgIconKind.PressureBrush
            or SvgIconKind.MixingBrush
            or SvgIconKind.Text
            or SvgIconKind.Fill
            or SvgIconKind.InkBottle
            or SvgIconKind.Eyedropper
            or SvgIconKind.Gradient
            or SvgIconKind.Eraser;
    }

    private static void FillHandle(Graphics g, Brush brush, RectangleF r, float x, float y)
    {
        g.FillRectangle(brush, Rect(r, x - 1.5f, y - 1.5f, 3, 3));
    }

    private static void DrawBrushGlyph(
        Graphics g,
        Pen pen,
        Pen thinPen,
        Brush fill,
        RectangleF r,
        bool pressure)
    {
        var handle = new[] { P(r, 13, 3), P(r, 21, 7), P(r, 12, 16), P(r, 8, 13) };
        g.FillPolygon(fill, handle);
        g.DrawPolygon(pen, handle);
        g.DrawLine(thinPen, P(r, 12, 13), P(r, 17, 8));

        using var bristles = new GraphicsPath();
        if (pressure)
        {
            bristles.AddBezier(P(r, 11, 14), P(r, 9, 17), P(r, 11, 20), P(r, 3, 22));
            bristles.AddBezier(P(r, 3, 22), P(r, 7, 18), P(r, 3, 15), P(r, 11, 14));
            g.DrawLine(pen, P(r, 4, 3), P(r, 4, 10));
            DrawChevron(g, pen, P(r, 4, 11), 2);
        }
        else
        {
            bristles.AddBezier(P(r, 10, 14), P(r, 8, 16), P(r, 9, 20), P(r, 4, 22));
            bristles.AddBezier(P(r, 4, 22), P(r, 6, 18), P(r, 3, 16), P(r, 10, 14));
        }
        bristles.CloseFigure();
        g.FillPath(fill, bristles);
        g.DrawPath(pen, bristles);
    }

    private static void DrawTextAlignmentGlyph(
        Graphics g,
        Pen pen,
        RectangleF r,
        TextHorizontalAlignment alignment)
    {
        ReadOnlySpan<float> widths = [16, 11, 16, 8];
        for (var index = 0; index < widths.Length; index++)
        {
            var width = widths[index];
            var x = alignment switch
            {
                TextHorizontalAlignment.Center => 12 - width * 0.5f,
                TextHorizontalAlignment.Right => 20 - width,
                _ => 4
            };
            var y = 5 + index * 5;
            g.DrawLine(pen, P(r, x, y), P(r, x + width, y));
        }
    }

    private static PointF P(RectangleF r, float x, float y) => new(r.Left + r.Width * x / 24f, r.Top + r.Height * y / 24f);

    private static RectangleF Rect(RectangleF r, float x, float y, float w, float h) => new(r.Left + r.Width * x / 24f, r.Top + r.Height * y / 24f, r.Width * w / 24f, r.Height * h / 24f);

    private static void DrawPolygon(Graphics g, Pen pen, PointF[] points) => g.DrawPolygon(pen, points);

    private static void DrawRectangle(Graphics g, Pen pen, RectangleF rect) => g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);

    private static PointF[] Star(RectangleF r)
    {
        var points = new PointF[10];
        var center = P(r, 12, 12);
        for (var i = 0; i < points.Length; i++)
        {
            var radius = i % 2 == 0 ? r.Width * 0.42f : r.Width * 0.18f;
            var angle = -MathF.PI / 2 + i * MathF.Tau / points.Length;
            points[i] = new PointF(center.X + MathF.Cos(angle) * radius, center.Y + MathF.Sin(angle) * radius);
        }

        return points;
    }

    private static void DrawChevron(Graphics g, Pen pen, PointF point, int direction)
    {
        var s = 3.5f;
        var points = direction switch
        {
            0 => new[] { new PointF(point.X - s, point.Y + s), point, new PointF(point.X + s, point.Y + s) },
            1 => new[] { new PointF(point.X - s, point.Y - s), point, new PointF(point.X - s, point.Y + s) },
            2 => new[] { new PointF(point.X - s, point.Y - s), point, new PointF(point.X + s, point.Y - s) },
            _ => new[] { new PointF(point.X + s, point.Y - s), point, new PointF(point.X + s, point.Y + s) }
        };
        g.DrawLines(pen, points);
    }
}
