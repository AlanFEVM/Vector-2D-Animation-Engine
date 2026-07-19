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
    Angle,
    Ratio,
    RestoreSize,
    PolarGrid,
    PropertiesPanel,
    TimelinePanel,
    FolderPlus,
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
        var r = new RectangleF(bounds.X + 5, bounds.Y + 5, bounds.Width - 10, bounds.Height - 10);
        using var pen = new Pen(color, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var thinPen = new Pen(Color.FromArgb(170, color), 1.1f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var fill = new SolidBrush(Color.FromArgb(42, color));

        switch (kind)
        {
            case SvgIconKind.Menu:
                g.DrawLine(pen, P(r, 4, 6), P(r, 20, 6));
                g.DrawLine(pen, P(r, 4, 12), P(r, 20, 12));
                g.DrawLine(pen, P(r, 4, 18), P(r, 20, 18));
                break;
            case SvgIconKind.Select:
                DrawPolygon(g, pen, [P(r, 4, 3), P(r, 13, 21), P(r, 15, 13), P(r, 22, 12)]);
                break;
            case SvgIconKind.Transform:
                DrawRectangle(g, pen, Rect(r, 6, 6, 12, 12));
                g.DrawLine(thinPen, P(r, 3, 3), P(r, 8, 3));
                g.DrawLine(thinPen, P(r, 3, 3), P(r, 3, 8));
                g.DrawLine(thinPen, P(r, 21, 21), P(r, 16, 21));
                g.DrawLine(thinPen, P(r, 21, 21), P(r, 21, 16));
                break;
            case SvgIconKind.Pan:
                g.DrawLine(pen, P(r, 12, 3), P(r, 12, 21));
                g.DrawLine(pen, P(r, 3, 12), P(r, 21, 12));
                DrawChevron(g, pen, P(r, 12, 3), 0);
                DrawChevron(g, pen, P(r, 21, 12), 1);
                DrawChevron(g, pen, P(r, 12, 21), 2);
                DrawChevron(g, pen, P(r, 3, 12), 3);
                break;
            case SvgIconKind.Rectangle:
                g.FillRectangle(fill, Rect(r, 5, 5, 14, 14));
                DrawRectangle(g, pen, Rect(r, 5, 5, 14, 14));
                break;
            case SvgIconKind.Ellipse:
                g.FillEllipse(fill, Rect(r, 4, 5, 16, 14));
                g.DrawEllipse(pen, Rect(r, 4, 5, 16, 14));
                break;
            case SvgIconKind.Triangle:
                DrawPolygon(g, pen, [P(r, 12, 4), P(r, 21, 20), P(r, 3, 20)]);
                break;
            case SvgIconKind.Polygon:
                DrawPolygon(g, pen, [P(r, 12, 3), P(r, 21, 8), P(r, 21, 17), P(r, 12, 22), P(r, 3, 17), P(r, 3, 8)]);
                break;
            case SvgIconKind.Star:
                DrawPolygon(g, pen, Star(r));
                break;
            case SvgIconKind.Line:
                g.DrawLine(pen, P(r, 4, 20), P(r, 20, 4));
                g.DrawEllipse(thinPen, Rect(r, 3, 19, 3, 3));
                g.DrawEllipse(thinPen, Rect(r, 19, 3, 3, 3));
                break;
            case SvgIconKind.Pen:
                DrawPolygon(g, pen, [P(r, 12, 3), P(r, 19, 10), P(r, 13, 21), P(r, 5, 13)]);
                g.DrawLine(thinPen, P(r, 9, 15), P(r, 15, 9));
                g.FillEllipse(fill, Rect(r, 10.5f, 10.5f, 3, 3));
                break;
            case SvgIconKind.SimplePen:
                g.DrawBezier(pen, P(r, 4, 18), P(r, 8, 5), P(r, 16, 19), P(r, 21, 7));
                g.FillEllipse(fill, Rect(r, 2.5f, 16.5f, 4, 4));
                g.DrawEllipse(thinPen, Rect(r, 2.5f, 16.5f, 4, 4));
                g.FillEllipse(fill, Rect(r, 19, 5, 4, 4));
                g.DrawEllipse(thinPen, Rect(r, 19, 5, 4, 4));
                break;
            case SvgIconKind.Pencil:
                g.DrawLine(pen, P(r, 5, 19), P(r, 17, 7));
                g.DrawLine(pen, P(r, 8, 22), P(r, 20, 10));
                g.DrawLine(thinPen, P(r, 5, 19), P(r, 8, 22));
                g.DrawLine(thinPen, P(r, 17, 7), P(r, 20, 10));
                DrawPolygon(g, thinPen, [P(r, 5, 19), P(r, 3, 22), P(r, 8, 22)]);
                break;
            case SvgIconKind.Brush:
                g.DrawLine(pen, P(r, 14, 4), P(r, 9, 15));
                g.DrawLine(pen, P(r, 20, 7), P(r, 11, 17));
                g.FillEllipse(fill, Rect(r, 4, 14, 9, 8));
                g.DrawArc(pen, Rect(r, 4, 14, 9, 8), 205, 285);
                break;
            case SvgIconKind.PressureBrush:
                g.DrawLine(pen, P(r, 15, 4), P(r, 10, 15));
                g.DrawLine(pen, P(r, 20, 7), P(r, 12, 17));
                g.FillEllipse(fill, Rect(r, 4, 14, 10, 8));
                g.DrawArc(pen, Rect(r, 4, 14, 10, 8), 205, 285);
                g.FillEllipse(fill, Rect(r, 17, 17, 4, 4));
                g.DrawEllipse(thinPen, Rect(r, 17, 17, 4, 4));
                break;
            case SvgIconKind.Fill:
                DrawPolygon(g, pen, [P(r, 8, 4), P(r, 19, 15), P(r, 13, 21), P(r, 2, 10)]);
                g.DrawLine(pen, P(r, 5, 13), P(r, 16, 13));
                g.FillEllipse(fill, Rect(r, 17, 18, 4, 4));
                break;
            case SvgIconKind.InkBottle:
                DrawPolygon(g, pen, [P(r, 8, 3), P(r, 17, 3), P(r, 20, 8), P(r, 18, 12), P(r, 7, 12), P(r, 5, 8)]);
                g.DrawLine(pen, P(r, 9, 12), P(r, 7, 20));
                g.DrawLine(pen, P(r, 16, 12), P(r, 18, 20));
                g.DrawLine(thinPen, P(r, 7, 20), P(r, 18, 20));
                g.FillEllipse(fill, Rect(r, 10, 15, 5, 5));
                break;
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
                g.DrawLine(thinPen, P(r, 5, 18), P(r, 19, 6));
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
                DrawPolygon(g, pen, [P(r, 7, 5), P(r, 19, 17), P(r, 13, 22), P(r, 2, 11)]);
                g.DrawLine(thinPen, P(r, 5, 14), P(r, 15, 4));
                g.DrawLine(thinPen, P(r, 10, 19), P(r, 20, 9));
                break;
            case SvgIconKind.Vault:
                DrawRectangle(g, pen, Rect(r, 4, 5, 16, 15));
                g.DrawLine(thinPen, P(r, 4, 10), P(r, 20, 10));
                g.DrawEllipse(pen, Rect(r, 10, 12, 4, 4));
                break;
            case SvgIconKind.FolderPlus:
                DrawPolygon(g, pen, [P(r, 3, 7), P(r, 9, 7), P(r, 11, 10), P(r, 21, 10), P(r, 21, 20), P(r, 3, 20)]);
                g.DrawLine(thinPen, P(r, 15, 12), P(r, 15, 18));
                g.DrawLine(thinPen, P(r, 12, 15), P(r, 18, 15));
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
