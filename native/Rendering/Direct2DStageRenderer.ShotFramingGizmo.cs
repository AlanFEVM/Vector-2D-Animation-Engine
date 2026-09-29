using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using GdiColor = System.Drawing.Color;
using GdiPointF = System.Drawing.PointF;

namespace VectorAnimationEngine;

// Direct2D overlay of the shot framing gizmo. Mirrors the GDI overlay and shares the stage geometry,
// so both render paths present identical handles.
internal sealed partial class Direct2DStageRenderer
{
    private void DrawShotFramingGizmo(StageControl stage)
    {
        if (stage.ShotFrameCollectionVisible)
        {
            foreach (var frame in stage.ShotFrameDisplays.Where(frame => !frame.Selected))
            {
                var brush = BrushFor((frame.Selected ? GdiColor.FromArgb(248, 255, 196, 96)
                    : GdiColor.FromArgb(232, 112, 204, 255)).ToArgb());
                var width = (frame.Selected ? 2.4f : 1.2f) * stage.SpatialGizmoDpiScale;
                if (stage.TryGetShotCameraWireframeGeometry(frame.Settings, out var wire, handlesVisible: false))
                    foreach (var segment in wire.Segments) _target!.DrawLine(ToVector(segment.Start), ToVector(segment.End), brush, width);
                if (stage.TryGetShotDisplayFrame(frame.Settings, out var outline))
                {
                    var points = outline.Corners;
                    for (int i = 0; i < points.Length; i++)
                        _target!.DrawLine(ToVector(points[i]), ToVector(points[(i + 1) % points.Length]), brush, width);
                }
            }
            if (!stage.HasSelectedShotFrame) return;
        }
        if (!stage.ShotFramingGizmoVisible) return;
        if (!stage.TryGetShotFramingGizmoGeometry(out var geometry)) return;
        var dpiScale = stage.SpatialGizmoDpiScale;
        var highlighted = stage.ShotFramingHighlightedHandle.Kind;
        var accent = BrushFor(GdiColor.FromArgb(248, 255, 196, 96).ToArgb());
        var softAccent = BrushFor(GdiColor.FromArgb(120, 255, 196, 96).ToArgb());
        var handleBrush = BrushFor(GdiColor.FromArgb(255, 255, 240, 168).ToArgb());
        var activeBrush = BrushFor(GdiColor.FromArgb(255, 112, 204, 255).ToArgb());
        var borderBrush = BrushFor(GdiColor.FromArgb(255, 16, 18, 22).ToArgb());
        var hasWireframe = stage.TryGetShotCameraWireframeGeometry(out var wireframe);
        if (hasWireframe) DrawShotCameraWireframe(wireframe, dpiScale, highlighted);

        var corners = geometry.Corners;
        for (var index = 0; index < corners.Length; index++)
        {
            _target!.DrawLine(
                ToVector(corners[index]),
                ToVector(corners[(index + 1) % corners.Length]),
                accent,
                1.8f);
        }

        var inner = StageControl.InsetPolygon(
            corners,
            geometry.Center,
            StageControl.ShotFramingBorderBandPixels * dpiScale);
        for (var index = 0; index < inner.Length; index++)
        {
            _target!.DrawLine(
                ToVector(inner[index]),
                ToVector(inner[(index + 1) % inner.Length]),
                softAccent,
                1f);
        }

        _target!.DrawLine(ToVector(geometry.RotationGripStart), ToVector(geometry.RotationHandle), accent, 1.4f);
        if (!hasWireframe)
        {
            DrawShotFramingCameraMarker(geometry.Center, dpiScale, highlighted == ShotFramingHandleKind.Body);
        }
        var handleSize = StageControl.ShotFramingHandleSizePixels * dpiScale;
        foreach (var (kind, point) in StageControl.EnumerateShotFramingHandles(geometry))
        {
            var center = ToVector(point);
            var brush = kind == highlighted ? activeBrush : handleBrush;
            if (kind == ShotFramingHandleKind.Rotate)
            {
                var radius = Math.Max(4f, handleSize * 0.75f);
                _target.FillEllipse(new Ellipse(center, radius, radius), brush);
                _target.DrawEllipse(new Ellipse(center, radius, radius), borderBrush, 1);
                continue;
            }

            DrawHandle(center, brush, handleSize);
        }
    }

    private void DrawShotCameraWireframe(
        ShotCameraWireframeGeometry geometry,
        float dpiScale,
        ShotFramingHandleKind highlighted)
    {
        var body = BrushFor(GdiColor.FromArgb(244, 112, 204, 255).ToArgb());
        var frame = BrushFor(GdiColor.FromArgb(232, 112, 204, 255).ToArgb());
        var frustum = BrushFor(GdiColor.FromArgb(216, 112, 204, 255).ToArgb());
        var guide = BrushFor(GdiColor.FromArgb(156, 112, 204, 255).ToArgb());
        var rotationX = BrushFor(StageControl.ShotCameraHandleColor(
            ShotFramingHandleKind.CameraRotateX).ToArgb());
        var rotationY = BrushFor(StageControl.ShotCameraHandleColor(
            ShotFramingHandleKind.CameraRotateY).ToArgb());
        var rotationZ = BrushFor(StageControl.ShotCameraHandleColor(
            ShotFramingHandleKind.CameraRotateZ).ToArgb());
        var borderBrush = BrushFor(GdiColor.FromArgb(255, 16, 18, 22).ToArgb());
        var activeBrush = BrushFor(GdiColor.FromArgb(255, 112, 232, 188).ToArgb());
        foreach (var segment in geometry.Segments)
        {
            var rotationHandle = StageControl.ShotCameraRotationHandleForSegment(segment.Kind);
            var brush = highlighted == rotationHandle && rotationHandle != ShotFramingHandleKind.None
                ? activeBrush
                : highlighted == ShotFramingHandleKind.CameraBody
                && segment.Kind == ShotCameraWireframeSegmentKind.Body
                ? activeBrush
                : segment.Kind switch
            {
                ShotCameraWireframeSegmentKind.Frame => frame,
                ShotCameraWireframeSegmentKind.Frustum => frustum,
                ShotCameraWireframeSegmentKind.OrthographicGuide => guide,
                ShotCameraWireframeSegmentKind.RotationRingX => rotationX,
                ShotCameraWireframeSegmentKind.RotationRingY => rotationY,
                ShotCameraWireframeSegmentKind.RotationRingZ => rotationZ,
                _ => body
            };
            _target!.DrawLine(
                ToVector(segment.Start),
                ToVector(segment.End),
                brush,
                Math.Max(1f, segment.Kind == ShotCameraWireframeSegmentKind.Frame
                    ? 1.8f * dpiScale
                    : rotationHandle != ShotFramingHandleKind.None
                        ? 1.8f * dpiScale
                    : 1.2f * dpiScale));
        }

        var pointBrush = highlighted == ShotFramingHandleKind.CameraBody
            ? activeBrush
            : BrushFor(GdiColor.FromArgb(244, 112, 204, 255).ToArgb());
        var cameraRadius = Math.Max(2.5f, 3.2f * dpiScale);
        if (geometry.HasCameraPoint)
        {
            _target!.FillEllipse(
                new Ellipse(ToVector(geometry.CameraPoint), cameraRadius, cameraRadius),
                pointBrush);
        }

        if (geometry.HasLensPoint)
        {
            var lensRadius = Math.Max(2f, 2.4f * dpiScale);
            _target!.DrawEllipse(
                new Ellipse(ToVector(geometry.LensPoint), lensRadius, lensRadius),
                body,
                Math.Max(1f, dpiScale));
        }

        foreach (var handle in geometry.Handles)
        {
            var baseBrush = BrushFor(StageControl.ShotCameraHandleColor(handle.Kind).ToArgb());
            var handleBrush = handle.Kind == highlighted ? activeBrush : baseBrush;
            if (geometry.HasCameraPoint && handle.Kind != ShotFramingHandleKind.CameraBody)
            {
                var shaftBrush = handle.Kind == highlighted
                    ? activeBrush
                    : BrushFor(StageControl.ShotCameraHandleColor(handle.Kind).ToArgb());
                var anchor = ToVector(handle.Anchor);
                var point = ToVector(handle.Point);
                if (StageControl.IsShotCameraRotationHandle(handle.Kind))
                {
                    if (Vector2.Distance(anchor, point) > StageControl.ShotCameraHandleLeaderThresholdPixels)
                    {
                        _target!.DrawLine(anchor, point, shaftBrush, Math.Max(1f, dpiScale));
                    }
                }
                else
                {
                    _target!.DrawLine(
                        ToVector(geometry.CameraPoint),
                        anchor,
                        shaftBrush,
                        Math.Max(1f, dpiScale));
                    if (Vector2.Distance(anchor, point) > StageControl.ShotCameraHandleLeaderThresholdPixels)
                    {
                        _target.DrawLine(anchor, point, shaftBrush, Math.Max(1f, dpiScale));
                    }
                }
            }

            var size = Math.Max(5f, 6f * dpiScale);
            var center = ToVector(handle.Point);
            if (handle.Kind == ShotFramingHandleKind.CameraLens)
            {
                DrawShotCameraDiamond(center, size, handleBrush, borderBrush, dpiScale);
            }
            else if (StageControl.IsShotCameraRotationHandle(handle.Kind))
            {
                DrawShotCameraRotationHandle(handle.Kind, center, size, handleBrush);
            }
            else if (StageControl.IsShotCameraPositionHandle(handle.Kind))
            {
                DrawShotCameraArrowHandle(
                    ToVector(geometry.CameraPoint),
                    center,
                    size,
                    handleBrush,
                    borderBrush,
                    dpiScale);
                DrawShotCameraAxisBadge(
                    handle.Kind,
                    center,
                    size,
                    handleBrush,
                    dpiScale);
            }
            else
            {
                var radius = Math.Max(5f, size * 0.9f);
                _target!.FillEllipse(new Ellipse(center, radius, radius), handleBrush);
                _target.DrawEllipse(new Ellipse(center, radius, radius), borderBrush, Math.Max(1f, dpiScale));
            }
        }
    }

    private void DrawShotCameraArrowHandle(
        Vector2 origin,
        Vector2 point,
        float size,
        ID2D1SolidColorBrush brush,
        ID2D1SolidColorBrush border,
        float dpiScale)
    {
        var direction = point - origin;
        var length = direction.Length();
        if (length <= 0.001f) direction = Vector2.UnitX;
        else direction /= length;
        var side = new Vector2(-direction.Y, direction.X);
        // The axis badge sits at the tip, so pull the arrowhead back to keep both readable.
        var tip = point - direction * (size * 0.55f);
        var basePoint = tip - direction * (size * 1.4f);
        var left = basePoint + side * (size * 0.7f);
        var right = basePoint - side * (size * 0.7f);
        using var arrow = CreateReference3DTriangle(
            new GdiPointF(tip.X, tip.Y),
            new GdiPointF(left.X, left.Y),
            new GdiPointF(right.X, right.Y));
        if (arrow is null) return;
        _target!.FillGeometry(arrow, brush);
        _target.DrawGeometry(arrow, border, Math.Max(1f, dpiScale), RoundStrokeStyle());
    }

    private void DrawShotCameraDiamond(
        Vector2 point,
        float size,
        ID2D1SolidColorBrush brush,
        ID2D1SolidColorBrush border,
        float dpiScale)
    {
        var top = point + new Vector2(0f, -size);
        var right = point + new Vector2(size, 0f);
        var bottom = point + new Vector2(0f, size);
        var left = point + new Vector2(-size, 0f);
        _target!.DrawLine(top, right, brush, Math.Max(2f, dpiScale * 1.6f));
        _target.DrawLine(right, bottom, brush, Math.Max(2f, dpiScale * 1.6f));
        _target.DrawLine(bottom, left, brush, Math.Max(2f, dpiScale * 1.6f));
        _target.DrawLine(left, top, brush, Math.Max(2f, dpiScale * 1.6f));
        _target!.DrawLine(top, right, border, Math.Max(1f, dpiScale));
        _target.DrawLine(right, bottom, border, Math.Max(1f, dpiScale));
        _target.DrawLine(bottom, left, border, Math.Max(1f, dpiScale));
        _target.DrawLine(left, top, border, Math.Max(1f, dpiScale));
        _target.FillEllipse(new Ellipse(point, Math.Max(2f, size * 0.5f), Math.Max(2f, size * 0.5f)), brush);
    }

    private void DrawShotCameraRotationHandle(
        ShotFramingHandleKind kind,
        Vector2 point,
        float size,
        ID2D1SolidColorBrush brush)
    {
        var radius = Math.Max(7f, size * 1.2f);
        _target!.FillEllipse(
            new Ellipse(point, radius, radius),
            BrushFor(GdiColor.FromArgb(224, 16, 18, 22).ToArgb()));
        // Hollow rings mean rotation; the centered axis letter identifies which rotation.
        _target.DrawEllipse(
            new Ellipse(point, radius, radius),
            brush,
            Math.Max(1.8f, size * 0.3f));
        DrawShotCameraAxisGlyph(kind, point, radius, size, brush);
    }

    /// <summary>
    /// Draws a filled axis plate behind a position arrowhead. X/Y/Z keep their axis colour, so the
    /// solid plate separates them from the hollow ring used by rotation handles.
    /// </summary>
    private void DrawShotCameraAxisBadge(
        ShotFramingHandleKind kind,
        Vector2 point,
        float size,
        ID2D1SolidColorBrush brush,
        float dpiScale)
    {
        // Keep the existing plate footprint; the letter fits inside its dark border.
        var radius = Math.Max(7.5f, size * 1.25f);
        var plate = new Ellipse(point, radius, radius);
        _target!.FillEllipse(plate, brush);
        // Keep the outline dark and neutral so a highlighted handle stays coherent instead of
        // showing the fixed wireframe blue over the active plate.
        _target.DrawEllipse(
            plate,
            BrushFor(GdiColor.FromArgb(236, 16, 18, 22).ToArgb()),
            Math.Max(1f, size * 0.18f));
        DrawShotCameraAxisGlyph(
            kind,
            point,
            radius,
            size,
            BrushFor(GdiColor.FromArgb(240, 16, 18, 22).ToArgb()));
    }

    private void DrawShotCameraAxisGlyph(
        ShotFramingHandleKind kind,
        Vector2 point,
        float radius,
        float size,
        ID2D1SolidColorBrush brush)
    {
        Span<GdiPointF> points = stackalloc GdiPointF[StageControl.ShotCameraAxisGlyphPointCapacity];
        var count = StageControl.GetShotCameraAxisGlyphPoints(
            kind,
            new GdiPointF(point.X, point.Y),
            radius,
            points);
        var stroke = StageControl.GetShotCameraAxisGlyphStrokeWidth(size);
        for (var index = 0; index < count; index += 2)
        {
            _target!.DrawLine(
                ToVector(points[index]),
                ToVector(points[index + 1]),
                brush,
                stroke,
                RoundStrokeStyle());
        }
    }

    private void DrawShotFramingCameraMarker(GdiPointF center, float dpiScale, bool highlighted)
    {
        var width = 24f * dpiScale;
        var height = 16f * dpiScale;
        var body = Rect(center.X - width * 0.5f, center.Y - height * 0.5f, width, height);
        var fill = BrushFor(
            highlighted
                ? GdiColor.FromArgb(232, 112, 204, 255).ToArgb()
                : GdiColor.FromArgb(224, 18, 20, 26).ToArgb());
        var border = BrushFor(GdiColor.FromArgb(255, 255, 196, 96).ToArgb());
        var dark = BrushFor(GdiColor.FromArgb(255, 16, 18, 22).ToArgb());
        _target!.FillRectangle(in body, fill);
        _target.DrawRectangle(in body, border, Math.Max(1f, 1.2f * dpiScale));
        var radius = 4.2f * dpiScale;
        var lens = new Ellipse(new Vector2(center.X, center.Y), radius, radius);
        _target.FillEllipse(lens, border);
        _target.DrawEllipse(lens, dark, Math.Max(1f, dpiScale));
        _target.DrawLine(
            new Vector2(center.X + width * 0.5f, center.Y - height * 0.35f),
            new Vector2(center.X + width * 0.5f + 5f * dpiScale, center.Y - height * 0.1f),
            border,
            Math.Max(1f, dpiScale));
        _target.DrawLine(
            new Vector2(center.X + width * 0.5f, center.Y + height * 0.35f),
            new Vector2(center.X + width * 0.5f + 5f * dpiScale, center.Y + height * 0.1f),
            border,
            Math.Max(1f, dpiScale));
    }
}
