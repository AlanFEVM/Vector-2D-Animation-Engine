using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using GdiColor = System.Drawing.Color;
using GdiPointF = System.Drawing.PointF;

namespace VectorAnimationEngine;

internal sealed partial class Direct2DStageRenderer
{
    private void DrawSceneLightGizmo(StageControl stage)
    {
        var hasSelectedGizmo = stage.TryGetSceneLightGizmoScreenGeometry(out var geometry);
        if (stage.SceneLightMarkerCount == 0 && !hasSelectedGizmo) return;

        var dpiScale = stage.SpatialGizmoDpiScale;
        DrawSceneLightMarkers(stage, dpiScale);
        if (!hasSelectedGizmo) return;

        var highlight = BrushFor(StageControl.SceneLightGizmoHighlightColor.ToArgb());
        if (stage.SupportsSceneLightGizmoHandle(SceneLightGizmoHandleKind.PositionX))
        {
            DrawSceneLightGizmoPositionAxis(
                geometry.Origin,
                geometry.PositionXHandle,
                StageControl.SceneLightGizmoPositionXColor.ToArgb(),
                stage.IsSceneLightGizmoHandleHighlighted(SceneLightGizmoHandleKind.PositionX),
                dpiScale);
            DrawSceneLightGizmoPositionAxis(
                geometry.Origin,
                geometry.PositionYHandle,
                StageControl.SceneLightGizmoPositionYColor.ToArgb(),
                stage.IsSceneLightGizmoHandleHighlighted(SceneLightGizmoHandleKind.PositionY),
                dpiScale);
            DrawSceneLightGizmoPositionAxis(
                geometry.Origin,
                geometry.PositionZHandle,
                StageControl.SceneLightGizmoPositionZColor.ToArgb(),
                stage.IsSceneLightGizmoHandleHighlighted(SceneLightGizmoHandleKind.PositionZ),
                dpiScale);
        }
        if (stage.SupportsSceneLightGizmoHandle(SceneLightGizmoHandleKind.Range))
        {
            var highlighted = stage.IsSceneLightGizmoHandleHighlighted(SceneLightGizmoHandleKind.Range);
            _target!.DrawEllipse(
                new Ellipse(ToVector(geometry.Origin), geometry.RangeRadius, geometry.RangeRadius),
                BrushFor(StageControl.SceneLightGizmoRangeColor.ToArgb()),
                (highlighted ? 2.6f : 1.6f) * dpiScale);
            DrawSceneLightGizmoSquare(
                geometry.RangeHandle,
                StageControl.SceneLightGizmoRangeColor.ToArgb(),
                highlighted,
                dpiScale);
        }

        if (stage.SupportsSceneLightGizmoHandle(SceneLightGizmoHandleKind.AreaWidth))
        {
            DrawSceneLightGizmoAxis(
                geometry.Origin,
                geometry.AreaWidthHandle,
                StageControl.SceneLightGizmoAreaWidthColor.ToArgb(),
                stage.IsSceneLightGizmoHandleHighlighted(SceneLightGizmoHandleKind.AreaWidth),
                dpiScale);
            DrawSceneLightGizmoAxis(
                geometry.Origin,
                geometry.AreaHeightHandle,
                StageControl.SceneLightGizmoAreaHeightColor.ToArgb(),
                stage.IsSceneLightGizmoHandleHighlighted(SceneLightGizmoHandleKind.AreaHeight),
                dpiScale);
        }

        if (stage.SupportsSceneLightGizmoHandle(SceneLightGizmoHandleKind.Direction))
        {
            var highlighted = stage.IsSceneLightGizmoHandleHighlighted(SceneLightGizmoHandleKind.Direction);
            if (highlighted)
            {
                _target!.DrawLine(
                    ToVector(geometry.Origin),
                    ToVector(geometry.DirectionHandle),
                    highlight,
                    6f * dpiScale,
                    RoundStrokeStyle());
            }
            var direction = BrushFor(StageControl.SceneLightGizmoIntensityColor.ToArgb());
            _target!.DrawLine(
                ToVector(geometry.Origin),
                ToVector(geometry.DirectionHandle),
                direction,
                (highlighted ? 3.2f : 2.2f) * dpiScale,
                RoundStrokeStyle());
            using var arrow = CreateSpatialArrowGeometry(
                geometry.Origin,
                geometry.DirectionHandle,
                dpiScale);
            if (arrow is not null)
            {
                if (highlighted)
                    _target.DrawGeometry(arrow, highlight, 4f * dpiScale, RoundStrokeStyle());
                _target.FillGeometry(arrow, direction);
            }
        }

        DrawSceneLightGizmoTrack(
            geometry.IntensityTrackStart,
            geometry.IntensityTrackEnd,
            geometry.IntensityHandle,
            StageControl.SceneLightGizmoIntensityColor.ToArgb(),
            stage.IsSceneLightGizmoHandleHighlighted(SceneLightGizmoHandleKind.Intensity),
            dpiScale);
        if (stage.SupportsSceneLightGizmoHandle(SceneLightGizmoHandleKind.ShadowStrength))
        {
            DrawSceneLightGizmoTrack(
                geometry.ShadowStrengthTrackStart,
                geometry.ShadowStrengthTrackEnd,
                geometry.ShadowStrengthHandle,
                StageControl.SceneLightGizmoShadowStrengthColor.ToArgb(),
                stage.IsSceneLightGizmoHandleHighlighted(SceneLightGizmoHandleKind.ShadowStrength),
                dpiScale);
            DrawSceneLightGizmoTrack(
                geometry.ShadowSoftnessTrackStart,
                geometry.ShadowSoftnessTrackEnd,
                geometry.ShadowSoftnessHandle,
                StageControl.SceneLightGizmoShadowSoftnessColor.ToArgb(),
                stage.IsSceneLightGizmoHandleHighlighted(SceneLightGizmoHandleKind.ShadowSoftness),
                dpiScale);
        }

        var settings = stage.SceneLightGizmoSettings;
        var lightColor = GdiColor.FromArgb(settings.ColorArgb);
        if (!settings.Enabled) lightColor = GdiColor.FromArgb(145, lightColor);
        var positionHighlighted = stage.IsSceneLightGizmoHandleHighlighted(SceneLightGizmoHandleKind.Position);
        if (positionHighlighted)
        {
            var haloRadius = 9f * dpiScale;
            _target!.FillEllipse(
                new Ellipse(ToVector(geometry.Origin), haloRadius, haloRadius),
                highlight);
        }
        var originRadius = 6f * dpiScale;
        var originEllipse = new Ellipse(ToVector(geometry.Origin), originRadius, originRadius);
        _target!.FillEllipse(originEllipse, BrushFor(lightColor.ToArgb()));
        _target.DrawEllipse(
            originEllipse,
            BrushFor(GdiColor.FromArgb(235, 28, 32, 36).ToArgb()),
            1.5f * dpiScale);
    }

    private void DrawSceneLightMarkers(StageControl stage, float dpiScale)
    {
        var hasSelectedMarker = false;
        var selectedMarker = default(SceneLightMarkerScreenGeometry);
        for (var index = 0; index < stage.SceneLightMarkerCount; index++)
        {
            if (!stage.TryGetSceneLightMarkerScreenGeometry(index, out var marker)) continue;
            if (marker.Selected)
            {
                selectedMarker = marker;
                hasSelectedMarker = true;
            }
            else
            {
                DrawSceneLightMarker(marker, dpiScale);
            }
        }
        if (hasSelectedMarker) DrawSceneLightMarker(selectedMarker, dpiScale);
    }

    private void DrawSceneLightMarker(SceneLightMarkerScreenGeometry marker, float dpiScale)
    {
        var sourceColor = GdiColor.FromArgb(marker.ColorArgb);
        var glyphColor = GdiColor.FromArgb(marker.Enabled ? 245 : 115, sourceColor);
        var outlineColor = GdiColor.FromArgb(marker.Enabled ? 235 : 145, 24, 29, 34);
        var origin = ToVector(marker.Origin);
        if (marker.Selected)
        {
            var haloRadius = 13f * dpiScale;
            _target!.FillEllipse(
                new Ellipse(origin, haloRadius, haloRadius),
                BrushFor(GdiColor.FromArgb(205, StageControl.SceneLightGizmoHighlightColor).ToArgb()));
        }

        var backgroundRadius = 10f * dpiScale;
        var backgroundEllipse = new Ellipse(origin, backgroundRadius, backgroundRadius);
        _target!.FillEllipse(
            backgroundEllipse,
            BrushFor(GdiColor.FromArgb(marker.Enabled ? 220 : 165, 25, 30, 35).ToArgb()));
        _target.DrawEllipse(
            backgroundEllipse,
            BrushFor(outlineColor.ToArgb()),
            1.5f * dpiScale);

        DrawSceneLightMarkerGlyph(
            marker.Kind,
            origin,
            BrushFor(glyphColor.ToArgb()),
            1.65f * dpiScale,
            dpiScale);
    }

    private void DrawSceneLightMarkerGlyph(
        SceneLightKind kind,
        Vector2 origin,
        ID2D1Brush brush,
        float strokeWidth,
        float dpiScale)
    {
        Vector2 P(float x, float y) => origin + new Vector2(x * dpiScale, y * dpiScale);

        switch (kind)
        {
            case SceneLightKind.Directional:
                _target!.DrawEllipse(
                    new Ellipse(P(-4.5f, 4.5f), 2.5f * dpiScale, 2.5f * dpiScale),
                    brush,
                    strokeWidth);
                _target.DrawLine(P(-5, 5), P(6, -6), brush, strokeWidth, RoundStrokeStyle());
                _target.DrawLine(P(6, -6), P(1.5f, -5.2f), brush, strokeWidth, RoundStrokeStyle());
                _target.DrawLine(P(6, -6), P(5.2f, -1.5f), brush, strokeWidth, RoundStrokeStyle());
                break;
            case SceneLightKind.Point:
            {
                var coreRadius = 2.5f * dpiScale;
                _target!.FillEllipse(new Ellipse(origin, coreRadius, coreRadius), brush);
                for (var ray = 0; ray < 8; ray++)
                {
                    var angle = ray * MathF.PI / 4f;
                    var dx = MathF.Cos(angle);
                    var dy = MathF.Sin(angle);
                    _target.DrawLine(
                        P(dx * 4.5f, dy * 4.5f),
                        P(dx * 8f, dy * 8f),
                        brush,
                        strokeWidth,
                        RoundStrokeStyle());
                }
                break;
            }
            case SceneLightKind.Area:
                _target!.DrawRectangle(
                    Rect(P(-6, -6).X, P(-6, -6).Y, 12f * dpiScale, 7f * dpiScale),
                    brush,
                    strokeWidth);
                _target.DrawLine(P(-4, 3), P(-6, 8), brush, strokeWidth, RoundStrokeStyle());
                _target.DrawLine(P(0, 3), P(0, 8), brush, strokeWidth, RoundStrokeStyle());
                _target.DrawLine(P(4, 3), P(6, 8), brush, strokeWidth, RoundStrokeStyle());
                break;
            case SceneLightKind.Ambient:
                _target!.DrawEllipse(
                    new Ellipse(origin, 7f * dpiScale, 7f * dpiScale),
                    brush,
                    strokeWidth);
                _target.DrawEllipse(
                    new Ellipse(origin, 3f * dpiScale, 7f * dpiScale),
                    brush,
                    strokeWidth);
                _target.DrawLine(P(-6, 0), P(6, 0), brush, strokeWidth, RoundStrokeStyle());
                break;
        }
    }

    private void DrawSceneLightGizmoTrack(
        GdiPointF start,
        GdiPointF end,
        GdiPointF handle,
        int colorArgb,
        bool highlighted,
        float dpiScale)
    {
        _target!.DrawLine(
            ToVector(start),
            ToVector(end),
            BrushFor(GdiColor.FromArgb(185, 22, 28, 32).ToArgb()),
            5f * dpiScale,
            RoundStrokeStyle());
        _target.DrawLine(
            ToVector(start),
            ToVector(handle),
            BrushFor(colorArgb),
            2f * dpiScale,
            RoundStrokeStyle());
        if (highlighted)
        {
            var haloRadius = 7f * dpiScale;
            _target.FillEllipse(
                new Ellipse(ToVector(handle), haloRadius, haloRadius),
                BrushFor(StageControl.SceneLightGizmoHighlightColor.ToArgb()));
        }
        var radius = (highlighted ? 5.5f : 4f) * dpiScale;
        _target.FillEllipse(
            new Ellipse(ToVector(handle), radius, radius),
            BrushFor(colorArgb));
    }

    private void DrawSceneLightGizmoAxis(
        GdiPointF origin,
        GdiPointF endpoint,
        int colorArgb,
        bool highlighted,
        float dpiScale)
    {
        if (highlighted)
        {
            _target!.DrawLine(
                ToVector(origin),
                ToVector(endpoint),
                BrushFor(StageControl.SceneLightGizmoHighlightColor.ToArgb()),
                5.5f * dpiScale,
                RoundStrokeStyle());
        }
        _target!.DrawLine(
            ToVector(origin),
            ToVector(endpoint),
            BrushFor(colorArgb),
            (highlighted ? 3f : 2f) * dpiScale,
            RoundStrokeStyle());
        DrawSceneLightGizmoSquare(endpoint, colorArgb, highlighted, dpiScale);
    }

    private void DrawSceneLightGizmoPositionAxis(
        GdiPointF origin,
        GdiPointF endpoint,
        int colorArgb,
        bool highlighted,
        float dpiScale)
    {
        var color = BrushFor(colorArgb);
        if (highlighted)
        {
            _target!.DrawLine(
                ToVector(origin),
                ToVector(endpoint),
                BrushFor(StageControl.SceneLightGizmoHighlightColor.ToArgb()),
                5.5f * dpiScale,
                RoundStrokeStyle());
        }
        _target!.DrawLine(
            ToVector(origin),
            ToVector(endpoint),
            color,
            (highlighted ? 3f : 2f) * dpiScale,
            RoundStrokeStyle());
        using var arrow = CreateSpatialArrowGeometry(origin, endpoint, dpiScale);
        if (arrow is null) return;
        if (highlighted)
        {
            _target.DrawGeometry(
                arrow,
                BrushFor(StageControl.SceneLightGizmoHighlightColor.ToArgb()),
                4f * dpiScale,
                RoundStrokeStyle());
        }
        _target.FillGeometry(arrow, color);
    }

    private void DrawSceneLightGizmoSquare(
        GdiPointF center,
        int colorArgb,
        bool highlighted,
        float dpiScale)
    {
        if (highlighted)
        {
            var haloHalf = 6.5f * dpiScale;
            _target!.FillRectangle(
                Rect(center.X - haloHalf, center.Y - haloHalf, haloHalf * 2, haloHalf * 2),
                BrushFor(StageControl.SceneLightGizmoHighlightColor.ToArgb()));
        }
        var half = 4f * dpiScale;
        _target!.FillRectangle(
            Rect(center.X - half, center.Y - half, half * 2, half * 2),
            BrushFor(colorArgb));
    }
}
