using System.Drawing.Drawing2D;

namespace VectorAnimationEngine;

internal enum SvgIconKind
{
    Select,
    Pan,
    Rectangle,
    Ellipse,
    Triangle,
    Polygon,
    Star,
    Line,
    Fill,
    Vault,
    Snap,
    Grid,
    Objects,
    TightFit,
    Align,
    Angle,
    Ratio
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
            case SvgIconKind.Select:
                DrawPolygon(g, pen, [P(r, 4, 3), P(r, 13, 21), P(r, 15, 13), P(r, 22, 12)]);
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
            case SvgIconKind.Fill:
                DrawPolygon(g, pen, [P(r, 8, 4), P(r, 19, 15), P(r, 13, 21), P(r, 2, 10)]);
                g.DrawLine(pen, P(r, 5, 13), P(r, 16, 13));
                g.FillEllipse(fill, Rect(r, 17, 18, 4, 4));
                break;
            case SvgIconKind.Vault:
                DrawRectangle(g, pen, Rect(r, 4, 5, 16, 15));
                g.DrawLine(thinPen, P(r, 4, 10), P(r, 20, 10));
                g.DrawEllipse(pen, Rect(r, 10, 12, 4, 4));
                break;
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
