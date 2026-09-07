using System.Drawing.Drawing2D;

namespace VectorAnimationEngine;

internal sealed partial class StageControl
{
    private void DrawSceneLightGizmoGdi(Graphics graphics)
    {
        var hasSelectedGizmo = TryGetSceneLightGizmoScreenGeometry(out var geometry);
        if (SceneLightMarkerCount == 0 && !hasSelectedGizmo) return;

        var dpiScale = SpatialGizmoDpiScale;
        var previousSmoothing = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        try
        {
            DrawSceneLightMarkersGdi(graphics, dpiScale);
            if (!hasSelectedGizmo) return;

            if (SupportsSceneLightGizmoHandle(SceneLightGizmoHandleKind.PositionX))
            {
                DrawSceneLightGizmoPositionAxis(
                    graphics,
                    geometry.Origin,
                    geometry.PositionXHandle,
                    SceneLightGizmoPositionXColor,
                    IsSceneLightGizmoHandleHighlighted(SceneLightGizmoHandleKind.PositionX),
                    dpiScale);
                DrawSceneLightGizmoPositionAxis(
                    graphics,
                    geometry.Origin,
                    geometry.PositionYHandle,
                    SceneLightGizmoPositionYColor,
                    IsSceneLightGizmoHandleHighlighted(SceneLightGizmoHandleKind.PositionY),
                    dpiScale);
                DrawSceneLightGizmoPositionAxis(
                    graphics,
                    geometry.Origin,
                    geometry.PositionZHandle,
                    SceneLightGizmoPositionZColor,
                    IsSceneLightGizmoHandleHighlighted(SceneLightGizmoHandleKind.PositionZ),
                    dpiScale);
            }

            if (SupportsSceneLightGizmoHandle(SceneLightGizmoHandleKind.Range))
            {
                var highlighted = IsSceneLightGizmoHandleHighlighted(SceneLightGizmoHandleKind.Range);
                using var rangePen = new Pen(
                    SceneLightGizmoRangeColor,
                    (highlighted ? 2.6f : 1.6f) * dpiScale);
                graphics.DrawEllipse(
                    rangePen,
                    geometry.Origin.X - geometry.RangeRadius,
                    geometry.Origin.Y - geometry.RangeRadius,
                    geometry.RangeRadius * 2,
                    geometry.RangeRadius * 2);
                DrawSceneLightGizmoSquare(
                    graphics,
                    geometry.RangeHandle,
                    SceneLightGizmoRangeColor,
                    highlighted,
                    dpiScale);
            }

            if (SupportsSceneLightGizmoHandle(SceneLightGizmoHandleKind.AreaWidth))
            {
                DrawSceneLightGizmoAxis(
                    graphics,
                    geometry.Origin,
                    geometry.AreaWidthHandle,
                    SceneLightGizmoAreaWidthColor,
                    IsSceneLightGizmoHandleHighlighted(SceneLightGizmoHandleKind.AreaWidth),
                    dpiScale);
                DrawSceneLightGizmoAxis(
                    graphics,
                    geometry.Origin,
                    geometry.AreaHeightHandle,
                    SceneLightGizmoAreaHeightColor,
                    IsSceneLightGizmoHandleHighlighted(SceneLightGizmoHandleKind.AreaHeight),
                    dpiScale);
            }

            if (SupportsSceneLightGizmoHandle(SceneLightGizmoHandleKind.Direction))
            {
                var highlighted = IsSceneLightGizmoHandleHighlighted(SceneLightGizmoHandleKind.Direction);
                if (highlighted)
                {
                    using var halo = new Pen(SceneLightGizmoHighlightColor, 6f * dpiScale)
                    {
                        StartCap = LineCap.Round,
                        EndCap = LineCap.Round
                    };
                    graphics.DrawLine(halo, geometry.Origin, geometry.DirectionHandle);
                }
                using var directionPen = new Pen(
                    SceneLightGizmoIntensityColor,
                    (highlighted ? 3.2f : 2.2f) * dpiScale)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                };
                graphics.DrawLine(directionPen, geometry.Origin, geometry.DirectionHandle);
                DrawSpatialArrowHead(
                    graphics,
                    geometry.Origin,
                    geometry.DirectionHandle,
                    SceneLightGizmoIntensityColor,
                    dpiScale,
                    highlighted ? SceneLightGizmoHighlightColor : null);
            }

            DrawSceneLightGizmoTrack(
                graphics,
                geometry.IntensityTrackStart,
                geometry.IntensityTrackEnd,
                geometry.IntensityHandle,
                SceneLightGizmoIntensityColor,
                IsSceneLightGizmoHandleHighlighted(SceneLightGizmoHandleKind.Intensity),
                dpiScale);
            if (SupportsSceneLightGizmoHandle(SceneLightGizmoHandleKind.ShadowStrength))
            {
                DrawSceneLightGizmoTrack(
                    graphics,
                    geometry.ShadowStrengthTrackStart,
                    geometry.ShadowStrengthTrackEnd,
                    geometry.ShadowStrengthHandle,
                    SceneLightGizmoShadowStrengthColor,
                    IsSceneLightGizmoHandleHighlighted(SceneLightGizmoHandleKind.ShadowStrength),
                    dpiScale);
                DrawSceneLightGizmoTrack(
                    graphics,
                    geometry.ShadowSoftnessTrackStart,
                    geometry.ShadowSoftnessTrackEnd,
                    geometry.ShadowSoftnessHandle,
                    SceneLightGizmoShadowSoftnessColor,
                    IsSceneLightGizmoHandleHighlighted(SceneLightGizmoHandleKind.ShadowSoftness),
                    dpiScale);
            }

            DrawSceneLightGizmoOrigin(graphics, geometry.Origin, dpiScale);
        }
        finally
        {
            graphics.SmoothingMode = previousSmoothing;
        }
    }

    private void DrawSceneLightMarkersGdi(Graphics graphics, float dpiScale)
    {
        var hasSelectedMarker = false;
        var selectedMarker = default(SceneLightMarkerScreenGeometry);
        for (var index = 0; index < SceneLightMarkerCount; index++)
        {
            if (!TryGetSceneLightMarkerScreenGeometry(index, out var marker)) continue;
            if (marker.Selected)
            {
                selectedMarker = marker;
                hasSelectedMarker = true;
            }
            else
            {
                DrawSceneLightMarkerGdi(graphics, marker, dpiScale);
            }
        }
        if (hasSelectedMarker) DrawSceneLightMarkerGdi(graphics, selectedMarker, dpiScale);
    }

    private static void DrawSceneLightMarkerGdi(
        Graphics graphics,
        SceneLightMarkerScreenGeometry marker,
        float dpiScale)
    {
        var sourceColor = Color.FromArgb(marker.ColorArgb);
        var glyphColor = Color.FromArgb(marker.Enabled ? 245 : 115, sourceColor);
        var outlineColor = Color.FromArgb(marker.Enabled ? 235 : 145, 24, 29, 34);
        var origin = marker.Origin;
        if (marker.Selected)
        {
            var haloRadius = 13f * dpiScale;
            using var halo = new SolidBrush(Color.FromArgb(205, SceneLightGizmoHighlightColor));
            graphics.FillEllipse(
                halo,
                origin.X - haloRadius,
                origin.Y - haloRadius,
                haloRadius * 2,
                haloRadius * 2);
        }

        var backgroundRadius = 10f * dpiScale;
        using var background = new SolidBrush(Color.FromArgb(marker.Enabled ? 220 : 165, 25, 30, 35));
        using var outline = new Pen(outlineColor, 1.5f * dpiScale);
        graphics.FillEllipse(
            background,
            origin.X - backgroundRadius,
            origin.Y - backgroundRadius,
            backgroundRadius * 2,
            backgroundRadius * 2);
        graphics.DrawEllipse(
            outline,
            origin.X - backgroundRadius,
            origin.Y - backgroundRadius,
            backgroundRadius * 2,
            backgroundRadius * 2);

        using var glyph = new Pen(glyphColor, 1.65f * dpiScale)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        using var glyphFill = new SolidBrush(glyphColor);
        DrawSceneLightMarkerGlyphGdi(graphics, marker.Kind, origin, glyph, glyphFill, dpiScale);
    }

    private static void DrawSceneLightMarkerGlyphGdi(
        Graphics graphics,
        SceneLightKind kind,
        PointF origin,
        Pen pen,
        Brush fill,
        float dpiScale)
    {
        float X(float offset) => origin.X + offset * dpiScale;
        float Y(float offset) => origin.Y + offset * dpiScale;

        switch (kind)
        {
            case SceneLightKind.Directional:
                graphics.DrawEllipse(pen, X(-7), Y(2), 5f * dpiScale, 5f * dpiScale);
                graphics.DrawLine(pen, X(-5), Y(5), X(6), Y(-6));
                graphics.DrawLine(pen, X(6), Y(-6), X(1.5f), Y(-5.2f));
                graphics.DrawLine(pen, X(6), Y(-6), X(5.2f), Y(-1.5f));
                break;
            case SceneLightKind.Point:
            {
                var coreRadius = 2.5f * dpiScale;
                graphics.FillEllipse(
                    fill,
                    origin.X - coreRadius,
                    origin.Y - coreRadius,
                    coreRadius * 2,
                    coreRadius * 2);
                for (var ray = 0; ray < 8; ray++)
                {
                    var angle = ray * MathF.PI / 4f;
                    var dx = MathF.Cos(angle);
                    var dy = MathF.Sin(angle);
                    graphics.DrawLine(
                        pen,
                        X(dx * 4.5f),
                        Y(dy * 4.5f),
                        X(dx * 8f),
                        Y(dy * 8f));
                }
                break;
            }
            case SceneLightKind.Area:
                graphics.DrawRectangle(pen, X(-6), Y(-6), 12f * dpiScale, 7f * dpiScale);
                graphics.DrawLine(pen, X(-4), Y(3), X(-6), Y(8));
                graphics.DrawLine(pen, X(0), Y(3), X(0), Y(8));
                graphics.DrawLine(pen, X(4), Y(3), X(6), Y(8));
                break;
            case SceneLightKind.Ambient:
                graphics.DrawEllipse(pen, X(-7), Y(-7), 14f * dpiScale, 14f * dpiScale);
                graphics.DrawEllipse(pen, X(-3), Y(-7), 6f * dpiScale, 14f * dpiScale);
                graphics.DrawLine(pen, X(-6), Y(0), X(6), Y(0));
                break;
        }
    }

    private static void DrawSceneLightGizmoTrack(
        Graphics graphics,
        PointF start,
        PointF end,
        PointF handle,
        Color color,
        bool highlighted,
        float dpiScale)
    {
        using var background = new Pen(Color.FromArgb(185, 22, 28, 32), 5f * dpiScale)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        using var foreground = new Pen(color, 2f * dpiScale)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        graphics.DrawLine(background, start, end);
        graphics.DrawLine(foreground, start, handle);
        var radius = (highlighted ? 5.5f : 4f) * dpiScale;
        if (highlighted)
        {
            var haloRadius = 7f * dpiScale;
            using var halo = new SolidBrush(SceneLightGizmoHighlightColor);
            graphics.FillEllipse(
                halo,
                handle.X - haloRadius,
                handle.Y - haloRadius,
                haloRadius * 2,
                haloRadius * 2);
        }
        using var fill = new SolidBrush(color);
        graphics.FillEllipse(fill, handle.X - radius, handle.Y - radius, radius * 2, radius * 2);
    }

    private static void DrawSceneLightGizmoAxis(
        Graphics graphics,
        PointF origin,
        PointF endpoint,
        Color color,
        bool highlighted,
        float dpiScale)
    {
        if (highlighted)
        {
            using var halo = new Pen(SceneLightGizmoHighlightColor, 5.5f * dpiScale)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            graphics.DrawLine(halo, origin, endpoint);
        }
        using var pen = new Pen(color, (highlighted ? 3f : 2f) * dpiScale)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        graphics.DrawLine(pen, origin, endpoint);
        DrawSceneLightGizmoSquare(graphics, endpoint, color, highlighted, dpiScale);
    }

    private static void DrawSceneLightGizmoPositionAxis(
        Graphics graphics,
        PointF origin,
        PointF endpoint,
        Color color,
        bool highlighted,
        float dpiScale)
    {
        if (highlighted)
        {
            using var halo = new Pen(SceneLightGizmoHighlightColor, 5.5f * dpiScale)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            graphics.DrawLine(halo, origin, endpoint);
        }
        using var pen = new Pen(color, (highlighted ? 3f : 2f) * dpiScale)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        graphics.DrawLine(pen, origin, endpoint);
        DrawSpatialArrowHead(
            graphics,
            origin,
            endpoint,
            color,
            dpiScale,
            highlighted ? SceneLightGizmoHighlightColor : null);
    }

    private static void DrawSceneLightGizmoSquare(
        Graphics graphics,
        PointF center,
        Color color,
        bool highlighted,
        float dpiScale)
    {
        if (highlighted)
        {
            var haloHalf = 6.5f * dpiScale;
            using var halo = new SolidBrush(SceneLightGizmoHighlightColor);
            graphics.FillRectangle(
                halo,
                center.X - haloHalf,
                center.Y - haloHalf,
                haloHalf * 2,
                haloHalf * 2);
        }
        var half = 4f * dpiScale;
        using var fill = new SolidBrush(color);
        graphics.FillRectangle(fill, center.X - half, center.Y - half, half * 2, half * 2);
    }

    private void DrawSceneLightGizmoOrigin(Graphics graphics, PointF origin, float dpiScale)
    {
        var positionHighlighted = IsSceneLightGizmoHandleHighlighted(SceneLightGizmoHandleKind.Position);
        if (positionHighlighted)
        {
            var haloRadius = 9f * dpiScale;
            using var halo = new SolidBrush(SceneLightGizmoHighlightColor);
            graphics.FillEllipse(
                halo,
                origin.X - haloRadius,
                origin.Y - haloRadius,
                haloRadius * 2,
                haloRadius * 2);
        }
        var color = Color.FromArgb(_sceneLightGizmoSettings.ColorArgb);
        if (!_sceneLightGizmoSettings.Enabled) color = Color.FromArgb(145, color);
        var radius = 6f * dpiScale;
        using var fill = new SolidBrush(color);
        using var outline = new Pen(Color.FromArgb(235, 28, 32, 36), 1.5f * dpiScale);
        graphics.FillEllipse(fill, origin.X - radius, origin.Y - radius, radius * 2, radius * 2);
        graphics.DrawEllipse(outline, origin.X - radius, origin.Y - radius, radius * 2, radius * 2);
    }
}
