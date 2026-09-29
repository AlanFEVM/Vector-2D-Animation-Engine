using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace VectorAnimationEngine;

// GDI overlay of the shot framing gizmo plus the offscreen capture used for shot previews.
internal sealed partial class StageControl
{
    private static readonly Color ShotFramingAccent = Color.FromArgb(248, 255, 196, 96);
    private static readonly Color ShotFramingAccentSoft = Color.FromArgb(120, 255, 196, 96);
    private static readonly Color ShotFramingLabelBackground = Color.FromArgb(214, 18, 20, 26);
    private static readonly Color ShotFramingLabelText = Color.FromArgb(255, 255, 232, 190);
    private static readonly Color ShotCameraWireframe = Color.FromArgb(244, 112, 204, 255);
    private static readonly Color ShotCameraWireframeFrame = Color.FromArgb(232, 112, 204, 255);
    private static readonly Color ShotCameraWireframeGuide = Color.FromArgb(156, 112, 204, 255);

    private void DrawShotFramingGizmoGdi(Graphics graphics)
    {
        if (ShotFrameCollectionVisible)
        {
            var displayState = graphics.Save();
            try
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                foreach (var frame in ShotFrameDisplays.Where(frame => !frame.Selected))
                {
                    using var pen = new Pen(frame.Selected ? ShotFramingAccent : ShotCameraWireframeFrame,
                        (frame.Selected ? 2.4f : 1.2f) * SpatialGizmoDpiScale);
                    if (TryGetShotCameraWireframeGeometry(frame.Settings, out var wire, handlesVisible: false))
                        foreach (var segment in wire.Segments) graphics.DrawLine(pen, segment.Start, segment.End);
                    if (TryGetShotDisplayFrame(frame.Settings, out var outline)) graphics.DrawPolygon(pen, outline.Corners);
                }
            }
            finally { graphics.Restore(displayState); }
            if (!HasSelectedShotFrame) return;
        }
        if (!_shotFramingVisible) return;
        if (!TryGetShotFramingGizmoGeometry(out var geometry)) return;
        var dpiScale = SpatialGizmoDpiScale;
        var highlighted = ShotFramingHighlightedHandle.Kind;
        var corners = geometry.Corners;
        var state = graphics.Save();
        try
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var hasWireframe = TryGetShotCameraWireframeGeometry(out var wireframe);
            if (hasWireframe) DrawShotCameraWireframeGdi(graphics, wireframe, dpiScale, highlighted);
            using var framePen = new Pen(ShotFramingAccent, 1.8f);
            using var innerPen = new Pen(ShotFramingAccentSoft, 1f) { DashStyle = DashStyle.Dot };
            using var rotationPen = new Pen(ShotFramingAccent, 1.4f);
            graphics.DrawPolygon(framePen, corners);
            var innerBand = ShotFramingBorderBandPixels * dpiScale;
            graphics.DrawPolygon(innerPen, InsetPolygon(corners, geometry.Center, innerBand));

            graphics.DrawLine(rotationPen, geometry.RotationGripStart, geometry.RotationHandle);
            if (!hasWireframe)
            {
                DrawShotFramingCameraMarker(graphics, geometry.Center, dpiScale, highlighted == ShotFramingHandleKind.Body);
            }
            var handleSize = ShotFramingHandleSizePixels * dpiScale;
            foreach (var (kind, point) in EnumerateShotFramingHandles(geometry))
            {
                if (kind == ShotFramingHandleKind.Rotate)
                {
                    DrawShotFramingRotationHandle(graphics, point, handleSize, kind == highlighted);
                    continue;
                }

                DrawHandle(
                    graphics,
                    point,
                    kind == highlighted ? _bezierHandleBrush : _handleBrush,
                    (int)Math.Round(handleSize));
            }

            DrawShotFramingGizmoLabel(graphics, geometry);
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    private static void DrawShotCameraWireframeGdi(
        Graphics graphics,
        ShotCameraWireframeGeometry geometry,
        float dpiScale,
        ShotFramingHandleKind highlighted)
    {
        using var bodyPen = new Pen(ShotCameraWireframe, Math.Max(1f, 1.4f * dpiScale));
        using var activeBodyPen = new Pen(
            Color.FromArgb(255, 112, 232, 188),
            Math.Max(1.8f, 1.9f * dpiScale));
        using var framePen = new Pen(ShotCameraWireframeFrame, Math.Max(1f, 1.8f * dpiScale));
        using var frustumPen = new Pen(ShotCameraWireframe, Math.Max(1f, 1.1f * dpiScale));
        using var guidePen = new Pen(ShotCameraWireframeGuide, Math.Max(1f, 1f * dpiScale))
        {
            DashStyle = DashStyle.Dash
        };
        using var rotationXPen = new Pen(
            ShotCameraHandleColor(ShotFramingHandleKind.CameraRotateX),
            Math.Max(1.2f, 1.8f * dpiScale));
        using var rotationYPen = new Pen(
            ShotCameraHandleColor(ShotFramingHandleKind.CameraRotateY),
            Math.Max(1.2f, 1.8f * dpiScale));
        using var rotationZPen = new Pen(
            ShotCameraHandleColor(ShotFramingHandleKind.CameraRotateZ),
            Math.Max(1.2f, 1.8f * dpiScale));
        using var activeRingPen = new Pen(
            Color.FromArgb(255, 112, 232, 188),
            Math.Max(2f, 2.6f * dpiScale));
        foreach (var segment in geometry.Segments)
        {
            var rotationHandle = StageControl.ShotCameraRotationHandleForSegment(segment.Kind);
            var pen = highlighted == rotationHandle && rotationHandle != ShotFramingHandleKind.None
                ? activeRingPen
                : highlighted == ShotFramingHandleKind.CameraBody
                && segment.Kind == ShotCameraWireframeSegmentKind.Body
                ? activeBodyPen
                : segment.Kind switch
            {
                ShotCameraWireframeSegmentKind.Frame => framePen,
                ShotCameraWireframeSegmentKind.Frustum => frustumPen,
                ShotCameraWireframeSegmentKind.OrthographicGuide => guidePen,
                ShotCameraWireframeSegmentKind.RotationRingX => rotationXPen,
                ShotCameraWireframeSegmentKind.RotationRingY => rotationYPen,
                ShotCameraWireframeSegmentKind.RotationRingZ => rotationZPen,
                _ => bodyPen
            };
            graphics.DrawLine(pen, segment.Start, segment.End);
        }

        using var pointBrush = new SolidBrush(ShotCameraWireframe);
        var cameraRadius = Math.Max(2.5f, 3.2f * dpiScale);
        if (geometry.HasCameraPoint)
        {
            graphics.FillEllipse(
                pointBrush,
                geometry.CameraPoint.X - cameraRadius,
                geometry.CameraPoint.Y - cameraRadius,
                cameraRadius * 2f,
                cameraRadius * 2f);
        }

        if (geometry.HasLensPoint)
        {
            var lensRadius = Math.Max(2f, 2.4f * dpiScale);
            graphics.DrawEllipse(
                bodyPen,
                geometry.LensPoint.X - lensRadius,
                geometry.LensPoint.Y - lensRadius,
                lensRadius * 2f,
                lensRadius * 2f);
        }

        using var bodyHandleBrush = new SolidBrush(ShotCameraHandleColor(ShotFramingHandleKind.CameraBody));
        using var lensBrush = new SolidBrush(ShotCameraHandleColor(ShotFramingHandleKind.CameraLens));
        using var positionXBrush = new SolidBrush(ShotCameraHandleColor(ShotFramingHandleKind.CameraPositionX));
        using var positionYBrush = new SolidBrush(ShotCameraHandleColor(ShotFramingHandleKind.CameraPositionY));
        using var positionZBrush = new SolidBrush(ShotCameraHandleColor(ShotFramingHandleKind.CameraPositionZ));
        using var rotationXBrush = new SolidBrush(ShotCameraHandleColor(ShotFramingHandleKind.CameraRotateX));
        using var rotationYBrush = new SolidBrush(ShotCameraHandleColor(ShotFramingHandleKind.CameraRotateY));
        using var rotationZBrush = new SolidBrush(ShotCameraHandleColor(ShotFramingHandleKind.CameraRotateZ));
        using var activeBrush = new SolidBrush(Color.FromArgb(255, 112, 232, 188));
        foreach (var handle in geometry.Handles)
        {
            var baseBrush = handle.Kind switch
            {
                ShotFramingHandleKind.CameraBody => bodyHandleBrush,
                ShotFramingHandleKind.CameraLens => lensBrush,
                ShotFramingHandleKind.CameraPositionX => positionXBrush,
                ShotFramingHandleKind.CameraPositionY => positionYBrush,
                ShotFramingHandleKind.CameraPositionZ => positionZBrush,
                ShotFramingHandleKind.CameraRotateX => rotationXBrush,
                ShotFramingHandleKind.CameraRotateY => rotationYBrush,
                ShotFramingHandleKind.CameraRotateZ => rotationZBrush,
                _ => bodyHandleBrush
            };
            var handleBrush = handle.Kind == highlighted ? activeBrush : baseBrush;

            if (geometry.HasCameraPoint && handle.Kind != ShotFramingHandleKind.CameraBody)
            {
                using var shaftPen = new Pen(
                    handle.Kind == highlighted
                        ? Color.FromArgb(236, Color.FromArgb(255, 112, 232, 188))
                        : Color.FromArgb(204, ShotCameraHandleColor(handle.Kind)),
                    Math.Max(1f, dpiScale));
                if (StageControl.IsShotCameraRotationHandle(handle.Kind))
                {
                    shaftPen.DashStyle = DashStyle.Dot;
                    if (ShotFramingDistance(handle.Anchor, handle.Point) > ShotCameraHandleLeaderThresholdPixels)
                    {
                        graphics.DrawLine(shaftPen, handle.Anchor, handle.Point);
                    }
                }
                else
                {
                    graphics.DrawLine(shaftPen, geometry.CameraPoint, handle.Anchor);
                    if (ShotFramingDistance(handle.Anchor, handle.Point) > ShotCameraHandleLeaderThresholdPixels)
                    {
                        shaftPen.DashStyle = DashStyle.Dash;
                        graphics.DrawLine(shaftPen, handle.Anchor, handle.Point);
                    }
                }
            }

            var size = Math.Max(5f, 6f * dpiScale);
            if (handle.Kind == ShotFramingHandleKind.CameraLens)
            {
                DrawShotCameraDiamondGdi(graphics, handle.Point, size, handleBrush, bodyPen);
            }
            else if (StageControl.IsShotCameraRotationHandle(handle.Kind))
            {
                DrawShotCameraRotationHandleGdi(graphics, handle.Kind, handle.Point, size, handleBrush);
            }
            else if (StageControl.IsShotCameraPositionHandle(handle.Kind))
            {
                DrawShotCameraArrowHandleGdi(
                    graphics,
                    geometry.CameraPoint,
                    handle.Point,
                    size,
                    handleBrush,
                    bodyPen);
                DrawShotCameraAxisBadgeGdi(graphics, handle.Kind, handle.Point, size, handleBrush);
            }
            else
            {
                var radius = Math.Max(5f, size * 0.9f);
                graphics.FillEllipse(
                    handleBrush,
                    handle.Point.X - radius,
                    handle.Point.Y - radius,
                    radius * 2f,
                    radius * 2f);
                graphics.DrawEllipse(
                    bodyPen,
                    handle.Point.X - radius,
                    handle.Point.Y - radius,
                    radius * 2f,
                    radius * 2f);
            }
        }
    }

    private static void DrawShotCameraArrowHandleGdi(
        Graphics graphics,
        PointF origin,
        PointF point,
        float size,
        Brush brush,
        Pen border)
    {
        var direction = new PointF(point.X - origin.X, point.Y - origin.Y);
        var length = MathF.Sqrt(direction.X * direction.X + direction.Y * direction.Y);
        if (length <= 0.001f) direction = new PointF(1f, 0f);
        else direction = new PointF(direction.X / length, direction.Y / length);
        var side = new PointF(-direction.Y, direction.X);
        // The axis badge sits at the tip, so pull the arrowhead back to keep both readable.
        var tip = new PointF(
            point.X - direction.X * size * 0.55f,
            point.Y - direction.Y * size * 0.55f);
        var basePoint = new PointF(
            tip.X - direction.X * size * 1.4f,
            tip.Y - direction.Y * size * 1.4f);
        var arrow = new[]
        {
            tip,
            new PointF(basePoint.X + side.X * size * 0.7f, basePoint.Y + side.Y * size * 0.7f),
            new PointF(basePoint.X - side.X * size * 0.7f, basePoint.Y - side.Y * size * 0.7f)
        };
        graphics.FillPolygon(brush, arrow);
        graphics.DrawPolygon(border, arrow);
    }

    private static void DrawShotCameraDiamondGdi(
        Graphics graphics,
        PointF point,
        float size,
        Brush brush,
        Pen border)
    {
        var diamond = new[]
        {
            new PointF(point.X, point.Y - size),
            new PointF(point.X + size, point.Y),
            new PointF(point.X, point.Y + size),
            new PointF(point.X - size, point.Y)
        };
        graphics.FillPolygon(brush, diamond);
        graphics.DrawPolygon(border, diamond);
    }

    private static void DrawShotCameraRotationHandleGdi(
        Graphics graphics,
        ShotFramingHandleKind kind,
        PointF point,
        float size,
        Brush brush)
    {
        var radius = Math.Max(7f, size * 1.2f);
        var bounds = new RectangleF(
            point.X - radius,
            point.Y - radius,
            radius * 2f,
            radius * 2f);
        using var background = new SolidBrush(Color.FromArgb(224, 16, 18, 22));
        graphics.FillEllipse(background, bounds);
        var color = brush is SolidBrush solid ? solid.Color : ShotCameraWireframe;
        using var ring = new Pen(color, Math.Max(1.8f, size * 0.3f));
        // Hollow rings mean rotation; the centered axis letter identifies which rotation.
        graphics.DrawEllipse(ring, bounds);
        DrawShotCameraAxisGlyphGdi(graphics, kind, point, radius, size, color);
    }

    /// <summary>
    /// Draws a filled axis plate behind a position arrowhead. X/Y/Z keep their axis colour, so the
    /// solid plate separates them from the hollow ring used by rotation handles.
    /// </summary>
    private static void DrawShotCameraAxisBadgeGdi(
        Graphics graphics,
        ShotFramingHandleKind kind,
        PointF point,
        float size,
        Brush brush)
    {
        var color = brush is SolidBrush solid ? solid.Color : ShotCameraWireframe;
        // Keep the existing plate footprint; the letter fits inside its dark border.
        var radius = Math.Max(7.5f, size * 1.25f);
        using var plate = new SolidBrush(Color.FromArgb(248, color));
        graphics.FillEllipse(plate, point.X - radius, point.Y - radius, radius * 2f, radius * 2f);
        // Keep the outline dark and neutral so a highlighted handle stays coherent instead of
        // showing the fixed wireframe blue over the active plate.
        using var plateBorder = new Pen(Color.FromArgb(236, 16, 18, 22), Math.Max(1f, size * 0.18f));
        graphics.DrawEllipse(plateBorder, point.X - radius, point.Y - radius, radius * 2f, radius * 2f);
        DrawShotCameraAxisGlyphGdi(graphics, kind, point, radius, size, Color.FromArgb(240, 16, 18, 22));
    }

    private static void DrawShotCameraAxisGlyphGdi(
        Graphics graphics,
        ShotFramingHandleKind kind,
        PointF point,
        float radius,
        float size,
        Color color)
    {
        using var glyph = new Pen(color, GetShotCameraAxisGlyphStrokeWidth(size))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        Span<PointF> points = stackalloc PointF[ShotCameraAxisGlyphPointCapacity];
        var count = GetShotCameraAxisGlyphPoints(kind, point, radius, points);
        for (var index = 0; index < count; index += 2)
        {
            graphics.DrawLine(glyph, points[index], points[index + 1]);
        }
    }

    private static void DrawShotFramingCameraMarker(
        Graphics graphics,
        PointF center,
        float dpiScale,
        bool highlighted)
    {
        var width = 24f * dpiScale;
        var height = 16f * dpiScale;
        var left = center.X - width * 0.5f;
        var top = center.Y - height * 0.5f;
        var fillColor = highlighted
            ? Color.FromArgb(232, 112, 204, 255)
            : Color.FromArgb(224, 18, 20, 26);
        using var fill = new SolidBrush(fillColor);
        using var border = new Pen(ShotFramingAccent, Math.Max(1f, 1.2f * dpiScale));
        graphics.FillRectangle(fill, left, top, width, height);
        graphics.DrawRectangle(border, left, top, width, height);
        using var lens = new SolidBrush(ShotFramingAccent);
        var radius = 4.2f * dpiScale;
        graphics.FillEllipse(lens, center.X - radius, center.Y - radius, radius * 2f, radius * 2f);
        using var lensBorder = new Pen(Color.FromArgb(255, 16, 18, 22), Math.Max(1f, dpiScale));
        graphics.DrawEllipse(
            lensBorder,
            center.X - radius,
            center.Y - radius,
            radius * 2f,
            radius * 2f);
        graphics.DrawLine(
            border,
            left + width,
            center.Y - height * 0.35f,
            left + width + 5f * dpiScale,
            center.Y - height * 0.1f);
        graphics.DrawLine(
            border,
            left + width,
            center.Y + height * 0.35f,
            left + width + 5f * dpiScale,
            center.Y + height * 0.1f);
    }

    private void DrawShotFramingRotationHandle(Graphics graphics, PointF point, float size, bool highlighted)
    {
        var radius = Math.Max(4f, size * 0.75f);
        graphics.FillEllipse(
            highlighted ? _bezierHandleBrush : _handleBrush,
            point.X - radius,
            point.Y - radius,
            radius * 2f,
            radius * 2f);
        graphics.DrawEllipse(_handleBorderPen, point.X - radius, point.Y - radius, radius * 2f, radius * 2f);
    }

    private void DrawShotFramingGizmoLabel(Graphics graphics, ShotFramingGizmoGeometry geometry)
    {
        if (_shotFramingLabel.Length == 0) return;
        var point = geometry.TopLeft;
        var textSize = TextRenderer.MeasureText(
            graphics,
            _shotFramingLabel,
            Font,
            Size.Empty,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        var width = Math.Min(Math.Max(textSize.Width + 10, 40), Math.Max(40, Width - 16));
        var top = (int)Math.Max(2f, point.Y - textSize.Height - 8f);
        var left = Math.Clamp((int)Math.Round(point.X), 2, Math.Max(2, Width - width - 2));
        var bounds = new Rectangle(left, top, width, textSize.Height + 4);
        using var background = new SolidBrush(ShotFramingLabelBackground);
        graphics.FillRectangle(background, bounds);
        TextRenderer.DrawText(
            graphics,
            _shotFramingLabel,
            Font,
            bounds,
            ShotFramingLabelText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    /// <summary>Shrinks a polygon towards its centre, used to show the draggable border band.</summary>
    internal static PointF[] InsetPolygon(PointF[] corners, PointF center, float inset)
    {
        var result = new PointF[corners.Length];
        for (var index = 0; index < corners.Length; index++)
        {
            var dx = corners[index].X - center.X;
            var dy = corners[index].Y - center.Y;
            var length = MathF.Sqrt(dx * dx + dy * dy);
            if (length <= float.Epsilon)
            {
                result[index] = corners[index];
                continue;
            }

            var factor = Math.Max(0f, length - inset) / length;
            result[index] = new PointF(center.X + dx * factor, center.Y + dy * factor);
        }

        return result;
    }

    /// <summary>
    /// Renders the composition of the current frame offscreen and returns the pixels covered by the
    /// framed viewport, so the director panel can present a real shot preview (rotation included).
    /// </summary>
    internal bool TryCaptureShotPreview(SceneShotSettings settings, Size targetSize, out Bitmap? bitmap)
    {
        bitmap = null;
        if (Scene is null) return false;
        var surfaceWidth = ClientSize.Width;
        var surfaceHeight = ClientSize.Height;
        if (surfaceWidth <= 8 || surfaceHeight <= 8) return false;
        // The camera is a real 3D object. Project the framed plane through the camera's own
        // orientation so planar rotation, dolly depth and focal length reach the preview; the legacy
        // 2D adapter would silently drop X/Y rotation and Z distance.
        if (!TryResolveShotFramingGeometry(settings, out var geometry)) return false;
        var width = Math.Clamp(targetSize.Width, 8, 4096);
        var height = Math.Clamp(targetSize.Height, 8, 4096);

        // Preset frames fit inside the monitor without stretching. Legacy follow-scene captures
        // retain their existing requested-size contract.
        if (settings.AspectRatio != SceneShotAspectRatio.FollowScene)
        {
            var aspect = settings.ResolveAspectRatio(Scene.StageWidth, Scene.StageHeight);
            if (width / (float)height > aspect)
                width = Math.Max(1, (int)MathF.Round(height * aspect));
            else
                height = Math.Max(1, (int)MathF.Round(width / aspect));
        }

        using var surface = new Bitmap(surfaceWidth, surfaceHeight, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(surface))
        {
            graphics.Clear(BackColor);
            if (RendersReferenceProjection) DrawGdiReferenceBase(graphics);
            else DrawGdiBase2D(graphics, backgroundAlreadyDrawn: true);
        }

        var corners = geometry.Corners;
        var destination = new PointF[]
        {
            new(0f, 0f),
            new(width, 0f),
            new(0f, height)
        };
        var source = new[] { corners[0], corners[1], corners[3] };
        if (!TryCreateQuadTransform(source, destination, out var transform)) return false;

        var result = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        try
        {
            using (var graphics = Graphics.FromImage(result))
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.Clear(BackColor);
                graphics.SetClip(new Rectangle(0, 0, width, height));
                graphics.Transform = transform;
                graphics.DrawImage(surface, 0, 0, surfaceWidth, surfaceHeight);
            }

            bitmap = result;
            return true;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    /// <summary>Affine transform mapping three source points onto three destination points.</summary>
    internal static bool TryCreateQuadTransform(PointF[] source, PointF[] destination, out Matrix transform)
    {
        transform = new Matrix();
        if (source.Length < 3 || destination.Length < 3) return false;
        var ax = source[1].X - source[0].X;
        var ay = source[1].Y - source[0].Y;
        var bx = source[2].X - source[0].X;
        var by = source[2].Y - source[0].Y;
        var determinant = ax * by - bx * ay;
        if (MathF.Abs(determinant) < 1e-4f) return false;
        var cax = destination[1].X - destination[0].X;
        var cay = destination[1].Y - destination[0].Y;
        var cbx = destination[2].X - destination[0].X;
        var cby = destination[2].Y - destination[0].Y;
        var m11 = (cax * by - cbx * ay) / determinant;
        var m21 = (ax * cbx - bx * cax) / determinant;
        var m12 = (cay * by - cby * ay) / determinant;
        var m22 = (ax * cby - bx * cay) / determinant;
        var dx = destination[0].X - (m11 * source[0].X + m21 * source[0].Y);
        var dy = destination[0].Y - (m12 * source[0].X + m22 * source[0].Y);
        transform = new Matrix(m11, m12, m21, m22, dx, dy);
        return true;
    }
}
