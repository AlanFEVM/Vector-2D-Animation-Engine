using System.Numerics;

namespace VectorAnimationEngine;

internal sealed partial class StageControl
{
    internal float CanvasRotation { get; private set; }
    internal float PlanarCanvasRotation => _renderingCanvasOutput || RendersReferenceProjection
        && ReferenceDimension == SceneDimension.ThreeD ? 0 : CanvasRotation;

    internal PointF RotateCanvasScreenPoint(PointF point) => TransformCanvasScreenPoint(point, PlanarCanvasRotation);
    internal PointF UnrotateCanvasScreenPoint(PointF point) => TransformCanvasScreenPoint(point, -PlanarCanvasRotation);
    internal PointF WorldOffsetToScreen(PointF offset)
    {
        var scale = VectorUnits.PixelsPerUnit * Zoom;
        var cos = MathF.Cos(PlanarCanvasRotation);
        var sin = MathF.Sin(PlanarCanvasRotation);
        return new PointF((offset.X * cos - offset.Y * sin) * scale, (offset.X * sin + offset.Y * cos) * scale);
    }

    private PointF TransformCanvasScreenPoint(PointF point, float angle)
    {
        if (angle == 0) return point;
        var dx = point.X - Width * 0.5f;
        var dy = point.Y - Height * 0.5f;
        var cos = MathF.Cos(angle);
        var sin = MathF.Sin(angle);
        return new PointF(Width * 0.5f + dx * cos - dy * sin, Height * 0.5f + dx * sin + dy * cos);
    }

    internal void SetCanvasRotation(float radians)
    {
        if (!float.IsFinite(radians)) return;
        radians = NormalizeRadians(radians);
        if (CanvasRotation == radians) return;
        CanvasRotation = radians;
        ClearGdiBaseFrameCache();
        _direct2DRenderer.InvalidateSelectionCommands();
        RequestStageFrame(basePresentationChanged: true);
        RaiseViewChanged();
    }

    internal Matrix3x2 CanvasScreenRotationTransform => Matrix3x2.CreateRotation(PlanarCanvasRotation,
        new Vector2(Width * 0.5f, Height * 0.5f));
}
