using System.Drawing.Drawing2D;

namespace VectorAnimationEngine;

internal enum SvgIconKind
{
    Menu,
    Select,
    PolygonLasso,
    FreehandLasso,
    Transform,
    Transform3D,
    Distort,
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
    Image,
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
    Light,
    Camera,
    DirectionalLight,
    PointLight,
    AreaLight,
    AmbientLight,
    Folder,
    FolderPlus,
    FilledFolder,
    Open,
    Add,
    Remove,
    ZoomIn,
    ZoomOut,
    ChevronUp,
    ChevronDown,
    ChevronLeft,
    ChevronRight,
    FrontView,
    Close,
    Info,
    Warning,
    Error,
    Question,
    EditImage,
    OnionSkin,
    MotionTrack,
    LayerParenting,
    TimelineTitle,
    Settings,
    AutoKeyframe,
    ImportImage,
    AssetFolder,
    AssetFolderPlus,
    AssetOpen,
    AssetAdd,
    AssetTags,
    AssetSymbol,
    AssetImage,
    AssetSvg,
    SoloLayerVisible,
    AllLayersVisible,
    LinearAutoKeyframe,
    LinearLayerParenting,
    TimelineFolder,
    Acknowledgements,
    FlipHorizontal,
    FlipVertical,
    InspectorProperties,
    InspectorPhysics,
    InspectorCamera,
    InspectorScene,
    InspectorColor,
    Copy,
    SceneSlate
}

internal static partial class SvgIcons
{
    public static Bitmap CreateBitmap(
        SvgIconKind kind,
        Size size,
        Color color,
        float dpi = 96f)
    {
        if (size.Width <= 0 || size.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(size));

        var normalizedDpi = float.IsFinite(dpi) && dpi > 0f
            ? Math.Clamp(dpi, 48f, 960f)
            : 96f;
        var bitmap = new Bitmap(
            size.Width,
            size.Height,
            System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            bitmap.SetResolution(normalizedDpi, normalizedDpi);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.Transparent);
            Draw(graphics, kind, new Rectangle(Point.Empty, size), color);
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

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
        var referenceSize = Math.Min(bounds.Width, bounds.Height) * 24f / Theme.ControlHeightCompact;
        var referenceBounds = new RectangleF(
            bounds.X + (bounds.Width - referenceSize) / 2f,
            bounds.Y + (bounds.Height - referenceSize) / 2f,
            referenceSize, referenceSize);
        if (kind is SvgIconKind.MotionTrack or SvgIconKind.OnionSkin)
            referenceBounds.Offset(-0.5f, -0.5f);

        switch (kind)
        {
            case SvgIconKind.SceneSlate:
            {
                var body = Rect(referenceBounds, 4, 9, 16, 12);
                g.DrawRectangle(pen, body.X, body.Y, body.Width, body.Height);
                g.DrawPolygon(pen, [P(referenceBounds, 3, 5), P(referenceBounds, 19, 2),
                    P(referenceBounds, 20, 6), P(referenceBounds, 4, 9)]);
                g.DrawLine(thinPen, P(referenceBounds, 7, 4.25f), P(referenceBounds, 10, 7.75f));
                g.DrawLine(thinPen, P(referenceBounds, 13, 3.15f), P(referenceBounds, 16, 6.65f));
                break;
            }
            case SvgIconKind.Copy:
                g.DrawRectangle(pen, r.X + r.Width * 0.26f, r.Y + r.Height * 0.26f,
                    r.Width * 0.64f, r.Height * 0.64f);
                g.DrawLines(pen,
                [
                    new PointF(r.X + r.Width * 0.10f, r.Y + r.Height * 0.70f),
                    new PointF(r.X + r.Width * 0.10f, r.Y + r.Height * 0.10f),
                    new PointF(r.X + r.Width * 0.70f, r.Y + r.Height * 0.10f)
                ]);
                break;
            case SvgIconKind.InspectorProperties:
            case SvgIconKind.InspectorPhysics:
            case SvgIconKind.InspectorCamera:
            case SvgIconKind.InspectorScene:
            case SvgIconKind.InspectorColor:
                DrawInspectorRailIcon(g, kind, r, color);
                break;
            case SvgIconKind.AutoKeyframe:
            {
                using var autoPen = new Pen(color, referenceSize / 14f)
                { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                g.DrawLines(autoPen, new[] { P(referenceBounds, 14, 10), P(referenceBounds, 17, 2), P(referenceBounds, 20, 10) });
                g.DrawLine(autoPen, P(referenceBounds, 15, 7), P(referenceBounds, 19, 7));
                var keyBounds = Rect(referenceBounds, 3, 12, 18, 9);
                g.FillRectangle(solidFill, keyBounds);
                using var keyDot = new SolidBrush(Theme.Top);
                g.FillEllipse(keyDot, Rect(referenceBounds, 10, 14.5f, 4, 4));
                break;
            }
            case SvgIconKind.LayerParenting:
            {
                using var outline = new GraphicsPath(FillMode.Alternate);
                void RoundedBox(float left, float top, float width, float height, float radius)
                {
                    var box = Rect(referenceBounds, left * 24f / 1024f, top * 24f / 1024f,
                        width * 24f / 1024f, height * 24f / 1024f);
                    var diameter = radius * 2f * referenceSize / 1024f;
                    outline.StartFigure();
                    outline.AddArc(box.Left, box.Top, diameter, diameter, 180, 90);
                    outline.AddArc(box.Right - diameter, box.Top, diameter, diameter, 270, 90);
                    outline.AddArc(box.Right - diameter, box.Bottom - diameter, diameter, diameter, 0, 90);
                    outline.AddArc(box.Left, box.Bottom - diameter, diameter, diameter, 90, 90);
                    outline.CloseFigure();
                }
                RoundedBox(96, 128, 832, 320, 96);
                RoundedBox(160, 192, 704, 192, 32);
                RoundedBox(352, 576, 576, 320, 96);
                RoundedBox(416, 640, 448, 192, 32);
                g.FillPath(solidFill, outline);
                using var connectorPen = new Pen(color, referenceSize * 64f / 1024f)
                { LineJoin = LineJoin.Round };
                using var connector = new GraphicsPath();
                connector.AddLine(P(referenceBounds, 4.5f, 10), P(referenceBounds, 4.5f, 15.75f));
                connector.AddBezier(P(referenceBounds, 4.5f, 15.75f), P(referenceBounds, 4.5f, 17),
                    P(referenceBounds, 4.75f, 17.25f), P(referenceBounds, 6, 17.25f));
                connector.AddLine(P(referenceBounds, 6, 17.25f), P(referenceBounds, 8.25f, 17.25f));
                g.DrawPath(connectorPen, connector);
                break;
            }
            case SvgIconKind.TimelineTitle:
            {
                // Preserve the supplied SVG's 1024-unit geometry in the shared icon bounds.
                const float svgToIcon = 24f / 1024f;
                void FillRoundedBar(float x, float y, float width, float height, float radius)
                {
                    var bar = Rect(referenceBounds, x * svgToIcon, y * svgToIcon,
                        width * svgToIcon, height * svgToIcon);
                    var diameter = radius * 2f * referenceSize / 1024f;
                    using var path = new GraphicsPath();
                    path.AddArc(bar.Left, bar.Top, diameter, diameter, 180, 90);
                    path.AddArc(bar.Right - diameter, bar.Top, diameter, diameter, 270, 90);
                    path.AddArc(bar.Right - diameter, bar.Bottom - diameter, diameter, diameter, 0, 90);
                    path.AddArc(bar.Left, bar.Bottom - diameter, diameter, diameter, 90, 90);
                    path.CloseFigure();
                    g.FillPath(solidFill, path);
                }

                FillRoundedBar(172.76f, 63.83f, 85.33f, 179.29f, 35.86f);
                FillRoundedBar(172.76f, 458.24f, 85.33f, 179.29f, 35.86f);
                FillRoundedBar(172.76f, 780.94f, 85.33f, 179.29f, 35.86f);
                foreach (var centerY in new[] { 350.68f, 709.23f })
                {
                    using var ring = new GraphicsPath(FillMode.Alternate);
                    ring.AddEllipse(Rect(referenceBounds, (208.62f - 143.42f) * svgToIcon,
                        (centerY - 143.42f) * svgToIcon, 286.84f * svgToIcon, 286.84f * svgToIcon));
                    ring.AddEllipse(Rect(referenceBounds, (208.62f - 64f) * svgToIcon,
                        (centerY - 64f) * svgToIcon, 128f * svgToIcon, 128f * svgToIcon));
                    g.FillPath(solidFill, ring);
                }
                FillRoundedBar(387.89f, 278.96f, 573.70f, 85.33f, 35.86f);
                FillRoundedBar(387.90f, 386.54f, 286.83f, 85.33f, 33.80f);
                FillRoundedBar(387.89f, 637.51f, 573.70f, 85.33f, 35.86f);
                FillRoundedBar(387.90f, 745.10f, 286.83f, 85.33f, 33.80f);
                break;
            }
            case SvgIconKind.MotionTrack:
            {
                using var trackPen = new Pen(color, referenceSize / 12f)
                { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                using var trackPath = new GraphicsPath();
                trackPath.AddLine(P(referenceBounds, 8.1f, 5.8f), P(referenceBounds, 16.4f, 5.8f));
                trackPath.AddBezier(P(referenceBounds, 16.4f, 5.8f), P(referenceBounds, 20.53f, 5.8f),
                    P(referenceBounds, 20.53f, 12), P(referenceBounds, 16.4f, 12));
                trackPath.AddLine(P(referenceBounds, 16.4f, 12), P(referenceBounds, 7.6f, 12));
                trackPath.AddBezier(P(referenceBounds, 7.6f, 12), P(referenceBounds, 3.47f, 12),
                    P(referenceBounds, 3.47f, 18.2f), P(referenceBounds, 7.6f, 18.2f));
                trackPath.AddLine(P(referenceBounds, 7.6f, 18.2f), P(referenceBounds, 15.9f, 18.2f));
                g.DrawPath(trackPen, trackPath);
                g.DrawEllipse(trackPen, Rect(referenceBounds, 4.5f, 4, 3.6f, 3.6f));
                g.DrawEllipse(trackPen, Rect(referenceBounds, 15.9f, 16.4f, 3.6f, 3.6f));
                break;
            }
            case SvgIconKind.OnionSkin:
            {
                var iconSize = referenceSize;
                var iconBounds = referenceBounds;
                using var rearPen = new Pen(Color.FromArgb((int)(color.A * 0.72f), color), iconSize / 12f)
                { LineJoin = LineJoin.Miter };
                using var frontPen = new Pen(color, iconSize / 12f) { LineJoin = LineJoin.Miter };
                using var rearPath = new GraphicsPath();
                rearPath.AddLines(new[]
                {
                    P(iconBounds, 12, 6), P(iconBounds, 14, 4),
                    P(iconBounds, 22, 12), P(iconBounds, 14, 20),
                    P(iconBounds, 12, 18)
                });
                using var frontPath = new GraphicsPath();
                frontPath.AddPolygon(new[]
                {
                    P(iconBounds, 10, 4), P(iconBounds, 18, 12),
                    P(iconBounds, 10, 20), P(iconBounds, 2, 12)
                });
                g.DrawPath(rearPen, rearPath);
                g.DrawPath(frontPen, frontPath);
                break;
            }
            case SvgIconKind.EditImage:
            {
                var xml = $"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 1024 1024'><path fill='#{color.R:X2}{color.G:X2}{color.B:X2}' d='M526.41 117.029v58.514a7.314 7.314 0 0 1-7.315 7.314H219.429a36.571 36.571 0 0 0-35.987 29.989l-0.585 6.583V804.57a36.571 36.571 0 0 0 29.989 35.987l6.583 0.585H804.57a36.571 36.571 0 0 0 35.987-29.989l0.585-6.583v-317.44a7.314 7.314 0 0 1 7.314-7.314h58.514a7.314 7.314 0 0 1 7.315 7.314v317.44a109.714 109.714 0 0 1-99.182 109.203l-10.533 0.512H219.43a109.714 109.714 0 0 1-109.203-99.182l-0.512-10.533V219.43a109.714 109.714 0 0 1 99.182-109.203l10.533-0.512h299.666a7.314 7.314 0 0 1 7.314 7.315z m307.345 31.817l41.4 41.399a7.314 7.314 0 0 1 0 10.313L419.985 655.726a7.314 7.314 0 0 1-10.313 0l-41.399-41.4a7.314 7.314 0 0 1 0-10.312l455.168-455.168a7.314 7.314 0 0 1 10.313 0z'/></svg>";
                var document = Svg.SvgDocument.FromSvg<Svg.SvgDocument>(xml);
                using var bitmap = document.Draw(Math.Max(1, (int)r.Width), Math.Max(1, (int)r.Height));
                g.DrawImage(bitmap, r);
                break;
            }
            case SvgIconKind.FlipHorizontal:
            case SvgIconKind.FlipVertical:
            {
                var data = kind == SvgIconKind.FlipHorizontal
                    ? "M928 896H546.304V128zM97.92 896h381.44V128L97.92 896z m334.08-49.92H170.24l261.76-518.4v518.4z"
                    : "M896.896 927.04V545.344h-768zM128.896 478.4l768 0.064v-381.44l-768 381.376z m199.68-47.424l518.4-261.696v261.76l-518.4-0.064z";
                var xml = $"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 1024 1024'><path fill='#{color.R:X2}{color.G:X2}{color.B:X2}' d='{data}'/></svg>";
                var document = Svg.SvgDocument.FromSvg<Svg.SvgDocument>(xml);
                using var bitmap = document.Draw(Math.Max(1, (int)r.Width), Math.Max(1, (int)r.Height));
                g.DrawImage(bitmap, r);
                break;
            }
            case SvgIconKind.Settings:
            {
                // Eight teeth and a clear center ring remain readable in compact toolbar slots.
                var teeth = new PointF[32];
                for (var tooth = 0; tooth < 8; tooth++)
                {
                    for (var corner = 0; corner < 4; corner++)
                    {
                        var angle = (-90f + tooth * 45f + (corner switch
                        {
                            0 => -22.5f,
                            1 => -12f,
                            2 => 12f,
                            _ => 22.5f
                        })) * MathF.PI / 180f;
                        var radius = corner is 1 or 2 ? 10f : 7.5f;
                        teeth[tooth * 4 + corner] = P(r,
                            12f + MathF.Cos(angle) * radius,
                            12f + MathF.Sin(angle) * radius);
                    }
                }
                DrawPolygon(g, pen, teeth);
                g.DrawEllipse(pen, Rect(r, 8, 8, 8, 8));
                break;
            }
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
            case SvgIconKind.PolygonLasso:
            {
                var polygon = new[]
                {
                    P(r, 5, 7), P(r, 12, 3), P(r, 20, 7), P(r, 19, 16),
                    P(r, 12, 21), P(r, 5, 17), P(r, 5, 7)
                };
                g.DrawLines(pen, polygon);
                g.DrawLine(pen, P(r, 5, 7), P(r, 2, 4));
                g.DrawLine(thinPen, P(r, 2, 4), P(r, 3, 9));
                FillHandle(g, solidFill, r, 5, 7);
                FillHandle(g, solidFill, r, 12, 3);
                FillHandle(g, solidFill, r, 20, 7);
                break;
            }
            case SvgIconKind.FreehandLasso:
            {
                using var lasso = new GraphicsPath();
                lasso.StartFigure();
                lasso.AddBezier(P(r, 5, 8), P(r, 7, 3), P(r, 17, 3), P(r, 20, 8));
                lasso.AddBezier(P(r, 20, 8), P(r, 23, 13), P(r, 18, 21), P(r, 11, 19));
                lasso.AddBezier(P(r, 11, 19), P(r, 5, 18), P(r, 2, 13), P(r, 5, 8));
                g.DrawPath(pen, lasso);
                g.DrawLine(pen, P(r, 5, 8), P(r, 2, 5));
                g.DrawLine(thinPen, P(r, 2, 5), P(r, 3, 10));
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
            case SvgIconKind.Transform3D:
            {
                var origin = P(r, 11, 13);
                g.FillRectangle(fill, Rect(r, 7, 9, 8, 8));
                DrawRectangle(g, thinPen, Rect(r, 7, 9, 8, 8));
                g.DrawLine(pen, origin, P(r, 21, 13));
                DrawChevron(g, pen, P(r, 21, 13), 1);
                g.DrawLine(pen, origin, P(r, 11, 3));
                DrawChevron(g, pen, P(r, 11, 3), 0);
                g.DrawLine(pen, origin, P(r, 4, 21));
                g.DrawLine(pen, P(r, 4, 21), P(r, 5, 16));
                g.DrawLine(pen, P(r, 4, 21), P(r, 9, 20));
                FillHandle(g, solidFill, r, 11, 13);
                break;
            }
            case SvgIconKind.Distort:
            {
                var quad = new[]
                {
                    P(r, 5, 7), P(r, 19, 4), P(r, 21, 18), P(r, 4, 21)
                };
                g.FillPolygon(fill, quad);
                DrawPolygon(g, pen, quad);
                g.DrawLine(thinPen, P(r, 9, 6), P(r, 10, 20));
                g.DrawLine(thinPen, P(r, 16, 5), P(r, 17, 19));
                FillHandle(g, solidFill, r, 5, 7);
                FillHandle(g, solidFill, r, 19, 4);
                FillHandle(g, solidFill, r, 21, 18);
                FillHandle(g, solidFill, r, 4, 21);
                break;
            }
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
            {
                var textTool = new[]
                {
                    P(r, 4, 3), P(r, 20, 3), P(r, 20, 7), P(r, 17.5f, 7),
                    P(r, 17.5f, 5.5f), P(r, 13.5f, 5.5f), P(r, 13.5f, 19.5f),
                    P(r, 17, 19.5f), P(r, 17, 22), P(r, 7, 22), P(r, 7, 19.5f),
                    P(r, 10.5f, 19.5f), P(r, 10.5f, 5.5f), P(r, 6.5f, 5.5f),
                    P(r, 6.5f, 7), P(r, 4, 7)
                };
                g.FillPolygon(fill, textTool);
                DrawPolygon(g, pen, textTool);
                break;
            }
            case SvgIconKind.Fill:
            {
                var bucket = new[] { P(r, 10, 2), P(r, 20, 12), P(r, 11, 21), P(r, 1, 11) };
                g.FillPolygon(fill, bucket);
                DrawPolygon(g, pen, bucket);
                g.DrawLine(thinPen, P(r, 3.5f, 2.5f), P(r, 12.5f, 11.5f));
                g.DrawLine(pen, P(r, 1.5f, 11), P(r, 19.5f, 11));
                using var drop = new GraphicsPath();
                drop.AddBezier(P(r, 21, 13), P(r, 18.8f, 16.2f), P(r, 18.5f, 19), P(r, 21, 20.5f));
                drop.AddBezier(P(r, 21, 20.5f), P(r, 23.5f, 19), P(r, 23.2f, 16.2f), P(r, 21, 13));
                drop.CloseFigure();
                g.FillPath(fill, drop);
                g.DrawPath(thinPen, drop);
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
            {
                var eyedropper = new[]
                {
                    P(r, 2, 22), P(r, 6.5f, 22), P(r, 16.6f, 11.9f),
                    P(r, 18.2f, 13.5f), P(r, 21.5f, 10.2f), P(r, 19.7f, 8.4f),
                    P(r, 22, 6.1f), P(r, 18, 2.1f), P(r, 15.7f, 4.4f), P(r, 13.8f, 2.5f),
                    P(r, 10.5f, 5.8f), P(r, 12.1f, 7.4f), P(r, 2, 17.5f)
                };
                g.FillPolygon(fill, eyedropper);
                DrawPolygon(g, pen, eyedropper);
                g.DrawLine(thinPen, P(r, 12.1f, 7.4f), P(r, 16.6f, 11.9f));
                g.DrawLine(thinPen, P(r, 2, 19.5f), P(r, 6.5f, 19.5f));
                break;
            }
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
                var eraser = new[]
                {
                    P(r, 2, 14), P(r, 13, 3), P(r, 22, 12),
                    P(r, 14, 20), P(r, 8, 20)
                };
                g.FillPolygon(fill, eraser);
                DrawPolygon(g, pen, eraser);
                g.DrawLine(pen, P(r, 9.7f, 6.3f), P(r, 18.7f, 15.3f));
                g.DrawLine(thinPen, P(r, 14, 20), P(r, 22, 20));
                break;
            }
            case SvgIconKind.Vault:
            {
                var outer = new[]
                {
                    P(r, 3, 21), P(r, 21, 21), P(r, 21, 9.5f),
                    P(r, 16.5f, 3), P(r, 7.5f, 3), P(r, 3, 9.5f)
                };
                var opening = new[]
                {
                    P(r, 8.8f, 5.2f), P(r, 15.2f, 5.2f),
                    P(r, 17.6f, 9.5f), P(r, 6.4f, 9.5f)
                };
                using var vault = new GraphicsPath(FillMode.Alternate);
                vault.AddPolygon(outer);
                vault.AddPolygon(opening);
                g.FillPath(fill, vault);
                DrawPolygon(g, pen, outer);
                DrawPolygon(g, thinPen, opening);
                g.DrawLine(pen, P(r, 3, 9.5f), P(r, 21, 9.5f));
                break;
            }
            case SvgIconKind.Folder:
                DrawPolygon(g, pen, [P(r, 3, 7), P(r, 9, 7), P(r, 11, 10), P(r, 21, 10), P(r, 21, 20), P(r, 3, 20)]);
                break;
            case SvgIconKind.FolderPlus:
                DrawPolygon(g, pen, [P(r, 3, 7), P(r, 9, 7), P(r, 11, 10), P(r, 21, 10), P(r, 21, 20), P(r, 3, 20)]);
                g.DrawLine(thinPen, P(r, 15, 12), P(r, 15, 18));
                g.DrawLine(thinPen, P(r, 12, 15), P(r, 18, 15));
                break;
            case SvgIconKind.FilledFolder:
            {
                // Two filled paths reproduce the user's folder mark in the shared 24-unit icon space.
                var folderCenterX = referenceBounds.X + referenceBounds.Width * 0.5f;
                var folderCenterY = referenceBounds.Y + referenceBounds.Height * 0.5f;
                var folderTransform = g.Save();
                g.TranslateTransform(folderCenterX, folderCenterY);
                g.ScaleTransform(0.72f, 0.72f);
                g.TranslateTransform(-folderCenterX, -folderCenterY);
                using var back = new GraphicsPath();
                back.StartFigure();
                back.AddBezier(P(referenceBounds, 13.448f, 5.916f), P(referenceBounds, 13.057f, 4.981f), P(referenceBounds, 12.510f, 3.978f), P(referenceBounds, 11.877f, 3.690f));
                back.AddBezier(P(referenceBounds, 11.877f, 3.690f), P(referenceBounds, 10.604f, 3.111f), P(referenceBounds, 2.729f, 3.864f), P(referenceBounds, 1.746f, 4.788f));
                back.AddBezier(P(referenceBounds, 1.746f, 4.788f), P(referenceBounds, 0.926f, 5.562f), P(referenceBounds, 2.360f, 14.041f), P(referenceBounds, 2.857f, 17.350f));
                back.AddBezier(P(referenceBounds, 2.857f, 17.350f), P(referenceBounds, 3.028f, 14.041f), P(referenceBounds, 3.859f, 8.096f), P(referenceBounds, 4.869f, 7.162f));
                back.AddBezier(P(referenceBounds, 4.869f, 7.162f), P(referenceBounds, 5.563f, 6.532f), P(referenceBounds, 9.538f, 6.148f), P(referenceBounds, 13.448f, 5.916f));
                back.CloseFigure();
                g.FillPath(solidFill, back);

                using var front = new GraphicsPath();
                front.StartFigure();
                front.AddBezier(P(referenceBounds, 22.357f, 7.047f), P(referenceBounds, 21.720f, 6.063f), P(referenceBounds, 6.959f, 6.583f), P(referenceBounds, 5.568f, 7.858f));
                front.AddBezier(P(referenceBounds, 5.568f, 7.858f), P(referenceBounds, 4.178f, 9.133f), P(referenceBounds, 3.137f, 19.842f), P(referenceBounds, 3.658f, 20.247f));
                front.AddBezier(P(referenceBounds, 3.658f, 20.247f), P(referenceBounds, 4.178f, 20.652f), P(referenceBounds, 20.279f, 20.652f), P(referenceBounds, 20.793f, 19.842f));
                front.AddBezier(P(referenceBounds, 20.793f, 19.842f), P(referenceBounds, 21.316f, 19.031f), P(referenceBounds, 22.995f, 8.032f), P(referenceBounds, 22.357f, 7.047f));
                front.CloseFigure();
                g.FillPath(solidFill, front);
                g.Restore(folderTransform);
                break;
            }
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
            case SvgIconKind.ChevronLeft:
                g.DrawLines(pen, [P(r, 15, 5), P(r, 8, 12), P(r, 15, 19)]);
                break;
            case SvgIconKind.ChevronRight:
                g.DrawLines(pen, [P(r, 9, 5), P(r, 16, 12), P(r, 9, 19)]);
                break;
            case SvgIconKind.FrontView:
                g.FillRectangle(fill, Rect(r, 4, 4, 16, 16));
                DrawRectangle(g, pen, Rect(r, 4, 4, 16, 16));
                g.DrawLine(thinPen, P(r, 12, 6), P(r, 12, 18));
                g.DrawLine(thinPen, P(r, 6, 12), P(r, 18, 12));
                g.FillEllipse(solidFill, Rect(r, 10, 10, 4, 4));
                break;
            case SvgIconKind.Camera:
                // Shot camera: body, viewfinder bump, lens and flash.
                DrawRectangle(g, pen, Rect(r, 3, 8, 18, 11));
                g.DrawLine(pen, P(r, 8, 8), P(r, 10, 4));
                g.DrawLine(pen, P(r, 10, 4), P(r, 15, 4));
                g.DrawLine(pen, P(r, 15, 4), P(r, 17, 8));
                g.DrawEllipse(pen, Rect(r, 8, 11, 8, 8));
                g.FillEllipse(solidFill, Rect(r, 16, 10, 2, 2));
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
            case SvgIconKind.Light:
                g.FillEllipse(fill, Rect(r, 6, 3, 12, 12));
                g.DrawEllipse(pen, Rect(r, 6, 3, 12, 12));
                g.DrawLine(thinPen, P(r, 9, 14), P(r, 9, 17));
                g.DrawLine(thinPen, P(r, 15, 14), P(r, 15, 17));
                g.DrawLine(pen, P(r, 9, 17), P(r, 15, 17));
                g.DrawLine(pen, P(r, 10, 20), P(r, 14, 20));
                DrawLightRays(g, thinPen, r, 12, 9, 9, 0, 8);
                break;
            case SvgIconKind.DirectionalLight:
                g.FillEllipse(fill, Rect(r, 3, 3, 8, 8));
                g.DrawEllipse(pen, Rect(r, 3, 3, 8, 8));
                g.DrawLine(pen, P(r, 10, 10), P(r, 20, 20));
                DrawChevron(g, pen, P(r, 20, 20), 1);
                g.DrawLine(thinPen, P(r, 13, 7), P(r, 21, 15));
                g.DrawLine(thinPen, P(r, 7, 13), P(r, 15, 21));
                break;
            case SvgIconKind.PointLight:
                g.FillEllipse(fill, Rect(r, 7, 7, 10, 10));
                g.DrawEllipse(pen, Rect(r, 7, 7, 10, 10));
                g.FillEllipse(solidFill, Rect(r, 10, 10, 4, 4));
                DrawLightRays(g, pen, r, 12, 12, 9, 0, 8);
                break;
            case SvgIconKind.AreaLight:
            {
                var panel = new[]
                {
                    P(r, 5, 6), P(r, 19, 4), P(r, 19, 15), P(r, 5, 17)
                };
                g.FillPolygon(fill, panel);
                DrawPolygon(g, pen, panel);
                g.DrawLine(thinPen, P(r, 8, 9), P(r, 16, 8));
                g.DrawLine(thinPen, P(r, 8, 13), P(r, 16, 12));
                g.DrawLine(pen, P(r, 8, 18), P(r, 8, 22));
                g.DrawLine(pen, P(r, 12, 17), P(r, 12, 21));
                g.DrawLine(pen, P(r, 16, 16), P(r, 16, 20));
                break;
            }
            case SvgIconKind.AmbientLight:
                g.FillEllipse(fill, Rect(r, 8, 8, 8, 8));
                g.DrawEllipse(pen, Rect(r, 8, 8, 8, 8));
                g.DrawArc(thinPen, Rect(r, 4, 4, 16, 16), 205, 250);
                g.DrawArc(pen, Rect(r, 2, 2, 20, 20), 25, 250);
                DrawChevron(g, thinPen, P(r, 19, 7), 1);
                DrawChevron(g, pen, P(r, 5, 18), 3);
                break;
            case SvgIconKind.TimelineFolder:
            {
                var xml = $"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 1024 1024' fill='#{color.R:X2}{color.G:X2}{color.B:X2}' fill-opacity='{(color.A / 255f).ToString(System.Globalization.CultureInfo.InvariantCulture)}'><path stroke='#{color.R:X2}{color.G:X2}{color.B:X2}' stroke-width='18' stroke-linejoin='round' d='M111.104 388.096h25.088V165.888c0-18.432 15.36-33.792 33.792-33.792h250.368c9.728 0 19.456 4.608 25.6 12.288l116.224 126.464h292.352c18.432 0 33.28 14.848 33.792 33.28v83.456h25.6c18.432 0 33.28 14.848 33.28 33.28 0 2.048 0 3.584-0.512 5.632l-53.248 431.616c-1.536 10.24-7.168 18.944-15.36 24.576a32.8704 32.8704 0 0 1-23.552 9.728H169.984c-8.704 0-16.896-3.072-23.04-9.216-8.704-6.144-14.336-15.36-15.872-26.112L77.824 425.472c-2.56-17.92 10.24-34.816 28.672-36.864h1.024l3.584-0.512z m92.16 0h617.984v-49.664h-273.92V337.92c-9.216 0-17.92-4.096-24.064-10.752L405.504 199.68H203.264v188.416zM875.52 455.168H148.992l45.056 365.568h635.904l45.568-365.568z'/></svg>";
                var document = Svg.SvgDocument.FromSvg<Svg.SvgDocument>(xml);
                using var bitmap = document.Draw(Math.Max(1, (int)referenceSize), Math.Max(1, (int)referenceSize));
                g.DrawImage(bitmap, referenceBounds);
                break;
            }
            case SvgIconKind.SoloLayerVisible:
            case SvgIconKind.AllLayersVisible:
            case SvgIconKind.LinearAutoKeyframe:
            case SvgIconKind.LinearLayerParenting:
            {
                using var outline = new Pen(color, referenceSize / 12f)
                { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                PointF V(float x, float y) => P(referenceBounds, x, y);
                if (kind is SvgIconKind.SoloLayerVisible or SvgIconKind.AllLayersVisible)
                {
                    PointF[] layer = kind == SvgIconKind.SoloLayerVisible
                        ? [V(4, 12), V(12, 8), V(20, 12), V(12, 16)]
                        : [V(2, 7), V(10, 3), V(18, 7), V(10, 11)];
                    g.DrawPolygon(outline, layer);
                    if (kind == SvgIconKind.AllLayersVisible)
                    {
                        g.DrawLines(outline, [V(2, 10.5f), V(10, 14.5f), V(18, 10.5f)]);
                        g.DrawLines(outline, [V(2, 14), V(10, 18), V(12, 17)]);
                    }
                    using var eye = new GraphicsPath();
                    eye.AddBezier(V(13, 20), V(15.5f, 16.5f), V(20.5f, 16.5f), V(23, 20));
                    eye.AddBezier(V(23, 20), V(20.5f, 23.5f), V(15.5f, 23.5f), V(13, 20));
                    g.DrawPath(outline, eye);
                    g.DrawEllipse(outline, Rect(referenceBounds, 16.75f, 18.75f, 2.5f, 2.5f));
                }
                else if (kind == SvgIconKind.LinearAutoKeyframe)
                {
                    g.DrawLines(outline, [V(12, 10), V(15.5f, 2), V(19, 10)]);
                    g.DrawLine(outline, V(13.5f, 7), V(17.5f, 7));
                    g.DrawRectangle(outline, Rect(referenceBounds, 3, 13, 18, 8));
                    g.DrawEllipse(outline, Rect(referenceBounds, 10.5f, 15.5f, 3, 3));
                }
                else
                {
                    g.DrawRectangle(outline, Rect(referenceBounds, 3, 3, 18, 6));
                    g.DrawRectangle(outline, Rect(referenceBounds, 9, 15, 12, 6));
                    g.DrawLines(outline, [V(5, 9), V(5, 18), V(9, 18)]);
                }
                break;
            }
            case SvgIconKind.AssetFolder:
            case SvgIconKind.AssetFolderPlus:
            case SvgIconKind.AssetOpen:
            case SvgIconKind.AssetAdd:
            case SvgIconKind.AssetTags:
            case SvgIconKind.AssetSymbol:
            case SvgIconKind.AssetImage:
            case SvgIconKind.AssetSvg:
            {
                // Match the supplied import mark: 24-unit canvas, rounded 1.65-unit strokes.
                using var assetPen = new Pen(color, kind == SvgIconKind.AssetAdd ? referenceSize / 12f : referenceSize * 1.65f / 24f)
                { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                PointF A(float x, float y) => P(referenceBounds, x, y);
                void Line(float x1, float y1, float x2, float y2) =>
                    g.DrawLine(assetPen, A(x1, y1), A(x2, y2));
                void RoundedFrame(float x, float y, float w, float h)
                {
                    using var path = new GraphicsPath();
                    var box = Rect(referenceBounds, x, y, w, h);
                    var diameter = referenceSize * 4f / 24f;
                    path.AddArc(box.Left, box.Top, diameter, diameter, 180, 90);
                    path.AddArc(box.Right - diameter, box.Top, diameter, diameter, 270, 90);
                    path.AddArc(box.Right - diameter, box.Bottom - diameter, diameter, diameter, 0, 90);
                    path.AddArc(box.Left, box.Bottom - diameter, diameter, diameter, 90, 90);
                    path.CloseFigure();
                    g.DrawPath(assetPen, path);
                }
                switch (kind)
                {
                    case SvgIconKind.AssetFolder:
                    case SvgIconKind.AssetFolderPlus:
                    {
                        using var path = new GraphicsPath();
                        path.AddLine(A(3, 19), A(3, 6));
                        path.AddBezier(A(3, 6), A(3, 4), A(4, 3), A(6, 3));
                        path.AddLine(A(6, 3), A(9, 3));
                        path.AddLine(A(9, 3), A(12, 6));
                        path.AddLine(A(12, 6), A(19, 6));
                        path.AddBezier(A(19, 6), A(21, 6), A(21, 7), A(21, 9));
                        path.AddLine(A(21, 9), A(21, 19));
                        path.AddBezier(A(21, 19), A(21, 21), A(20, 21), A(18, 21));
                        path.AddLine(A(18, 21), A(6, 21));
                        path.AddBezier(A(6, 21), A(4, 21), A(3, 21), A(3, 19));
                        path.CloseFigure();
                        g.DrawPath(assetPen, path);
                        if (kind == SvgIconKind.AssetFolderPlus)
                        { Line(12, 10, 12, 17); Line(8.5f, 13.5f, 15.5f, 13.5f); }
                        break;
                    }
                    case SvgIconKind.AssetOpen:
                    {
                        using var frame = new GraphicsPath();
                        frame.AddLine(A(12, 3), A(6, 3));
                        frame.AddBezier(A(6, 3), A(4, 3), A(3, 4), A(3, 6));
                        frame.AddLine(A(3, 6), A(3, 18));
                        frame.AddBezier(A(3, 18), A(3, 20), A(4, 21), A(6, 21));
                        frame.AddLine(A(6, 21), A(12, 21));
                        g.DrawPath(assetPen, frame);
                        Line(10, 12, 21, 12);
                        g.DrawLines(assetPen, [A(17, 8), A(21, 12), A(17, 16)]);
                        break;
                    }
                    case SvgIconKind.AssetAdd:
                        Line(12, 3, 12, 21); Line(3, 12, 21, 12);
                        break;
                    case SvgIconKind.AssetTags:
                        g.DrawPolygon(assetPen, [A(3, 3), A(12, 3), A(21, 12), A(12, 21), A(3, 12)]);
                        g.FillEllipse(solidFill, Rect(referenceBounds, 6, 6, 2.5f, 2.5f));
                        break;
                    case SvgIconKind.AssetSymbol:
                        RoundedFrame(3, 3, 12, 12); RoundedFrame(9, 9, 12, 12);
                        break;
                    case SvgIconKind.AssetImage:
                        RoundedFrame(3, 3, 18, 18);
                        g.FillEllipse(solidFill, Rect(referenceBounds, 6.5f, 6.5f, 3.5f, 3.5f));
                        g.DrawLines(assetPen, [A(3, 17), A(9, 11), A(14, 16), A(17, 13), A(21, 17)]);
                        break;
                    case SvgIconKind.AssetSvg:
                        RoundedFrame(3, 3, 18, 18);
                        g.DrawLines(assetPen, [A(9, 8), A(5.5f, 12), A(9, 16)]);
                        g.DrawLines(assetPen, [A(15, 8), A(18.5f, 12), A(15, 16)]);
                        break;
                }
                break;
            }
            case SvgIconKind.Acknowledgements:
            {
                var xml = $"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 1024 1024' fill='#{color.R:X2}{color.G:X2}{color.B:X2}'><path d='M512 180.195556Q443.178667 128 355.555556 128q-111.800889 0-190.919112 78.336Q85.333333 284.842667 85.333333 395.975111q0 301.368889 406.215111 523.235556 2.346667 1.28 4.835556 2.275555 2.474667 0.967111 5.063111 1.635556 2.588444 0.654222 5.233778 0.995555 2.645333 0.327111 5.319111 0.327111t5.319111-0.327111q2.645333-0.341333 5.233778-0.995555 2.588444-0.668444 5.077333-1.635556 2.474667-0.995556 4.835556-2.275555Q938.666667 697.358222 938.666667 395.989333q0-111.132444-79.303111-189.639111Q780.245333 128 668.444444 128q-87.623111 0-156.444444 52.195556z m-287.331556 86.784Q278.855111 213.333333 355.555556 213.333333q72.248889 0 126.435555 53.646223 1.464889 1.464889 3.086222 2.773333 1.621333 1.322667 3.356445 2.460444 1.735111 1.152 3.569778 2.133334 1.848889 0.981333 3.768888 1.763555 1.92 0.796444 3.911112 1.393778 1.991111 0.611556 4.039111 1.009778 2.048 0.398222 4.124444 0.611555 2.076444 0.199111 4.152889 0.199111t4.152889-0.199111q2.076444-0.213333 4.124444-0.611555 2.033778-0.398222 4.039111-0.995556 1.991111-0.611556 3.911112-1.408 1.934222-0.782222 3.768888-1.763555 1.848889-0.981333 3.569778-2.133334 1.735111-1.137778 3.356445-2.460444 1.621333-1.308444 3.100444-2.773333Q596.195556 213.333333 668.444444 213.333333q76.714667 0 130.887112 53.646223Q853.333333 320.455111 853.333333 395.975111q0 243.555556-341.333333 436.977778-341.333333-193.422222-341.333333-436.977778 0-75.52 54.001777-128.995555z m493.824 147.797333q-4.593778 40.803556-40.362666 84.380444l65.962666 54.129778q51.768889-63.089778 59.192889-128.952889 7.623111-67.598222-31.488-133.262222l-73.315555 43.648q24.405333 40.988444 20.010666 80.071111z'/></svg>";
                var document = Svg.SvgDocument.FromSvg<Svg.SvgDocument>(xml);
                using var bitmap = document.Draw(Math.Max(1, (int)referenceSize), Math.Max(1, (int)referenceSize));
                g.DrawImage(bitmap, referenceBounds);
                break;
            }
            case SvgIconKind.ImportImage:
            {
                // Keep the supplied image/upload geometry; theme color comes from the button.
                var xml = $"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 1024 1024' fill='#{color.R:X2}{color.G:X2}{color.B:X2}' fill-opacity='{(color.A / 255f).ToString(System.Globalization.CultureInfo.InvariantCulture)}'><path d='M721.984 599.68a73.664 73.664 0 0 1 92.032 0l136 108.8a35.2 35.2 0 0 1-44.032 55.04l-102.784-82.368V928a35.2 35.2 0 1 1-70.4 0v-246.848L630.016 763.52a35.2 35.2 0 0 1-44.032-55.04l136-108.8zM386.56 295.808a81.664 81.664 0 1 1 0 163.392 81.664 81.664 0 0 1 0-163.392z'/><path d='M730.88 112a181.12 181.12 0 0 1 181.184 181.184L912 512l-0.192 1.536a33.92 33.92 0 0 1-0.448 4.032c-0.128 0.896-0.256 1.856-0.512 2.752a35.136 35.136 0 0 1-2.56 7.04c-0.256 0.512-0.32 1.024-0.64 1.536-0.192 0.384-0.512 0.704-0.768 1.088a33.664 33.664 0 0 1-6.4 7.808 34.112 34.112 0 0 1-8.96 6.016l-3.008 1.216c-1.024 0.32-2.048 0.704-3.136 0.96a34.816 34.816 0 0 1-7.296 1.088l-1.28 0.128-1.344-0.128a34.752 34.752 0 0 1-13.952-3.52c-0.512-0.256-1.088-0.384-1.536-0.64-150.08-81.792-309.888-29.376-439.488 52.096-64 40.192-118.272 86.144-156.608 122.176a1036.672 1036.672 0 0 0-55.936 56.768l-2.816 3.2-0.64 0.768-0.192 0.192-5.12 4.928c-0.896 0.64-1.856 1.152-2.752 1.728 18.944 33.92 55.104 56.768 96.704 56.768h218.944a35.2 35.2 0 0 1 0 70.4h-218.88a181.12 181.12 0 0 1-181.184-181.12V293.12a181.12 181.12 0 0 1 181.12-181.12h437.76z m-437.76 70.4c-61.12 0-110.72 49.6-110.72 110.72v405.44a1147.264 1147.264 0 0 1 200.576-163.2c123.648-77.696 291.136-141.696 458.56-78.208l0.128-164.032a110.72 110.72 0 0 0-110.72-110.72H293.12z'/></svg>";
                var document = Svg.SvgDocument.FromSvg<Svg.SvgDocument>(xml);
                using var bitmap = document.Draw(Math.Max(1, (int)referenceSize), Math.Max(1, (int)referenceSize));
                g.DrawImage(bitmap, referenceBounds);
                break;
            }
            case SvgIconKind.Image:
                DrawRectangle(g, pen, Rect(r, 3, 5, 18, 14));
                g.DrawEllipse(thinPen, Rect(r, 6, 8, 3, 3));
                g.DrawLines(pen, new[]
                {
                    P(r, 4, 18),
                    P(r, 9, 12),
                    P(r, 13, 16),
                    P(r, 16, 13),
                    P(r, 20, 18)
                });
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
            or SvgIconKind.PolygonLasso
            or SvgIconKind.FreehandLasso
            or SvgIconKind.Transform
            or SvgIconKind.Transform3D
            or SvgIconKind.Distort
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

    private static void DrawLightRays(
        Graphics graphics,
        Pen pen,
        RectangleF bounds,
        float centerX,
        float centerY,
        float radius,
        float startAngleDegrees,
        int count)
    {
        for (var index = 0; index < count; index++)
        {
            var angle = (startAngleDegrees + index * 360f / count) * MathF.PI / 180f;
            var innerRadius = radius - 3f;
            graphics.DrawLine(
                pen,
                P(bounds, centerX + MathF.Cos(angle) * innerRadius, centerY + MathF.Sin(angle) * innerRadius),
                P(bounds, centerX + MathF.Cos(angle) * radius, centerY + MathF.Sin(angle) * radius));
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
