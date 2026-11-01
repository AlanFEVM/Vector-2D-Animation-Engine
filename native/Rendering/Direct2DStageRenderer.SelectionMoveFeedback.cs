using System.Numerics;
using Vortice.Direct2D1;
using DrawingPoint = System.Drawing.PointF;

namespace VectorAnimationEngine;

internal sealed partial class Direct2DStageRenderer
{
    private void DrawSelectionMoveObject(StageControl stage, int objectIndex, SceneRenderPass pass)
    {
        var contours = stage.GetSelectionMoveOcclusionContours(objectIndex, pass);
        if (contours.Length == 0)
        {
            DrawObjectUnclipped(stage, objectIndex, pass);
            return;
        }
        using var inside = SelectionMoveMask(stage, contours, outside: false);
        using var outside = SelectionMoveMask(stage, contours, outside: true);
        DrawSelectionMoveOpacity(1f, outside, () => DrawObjectUnclipped(stage, objectIndex, pass));
        DrawSelectionMoveOpacity(StageControl.SelectionMoveOccluderOpacity, inside,
            () => DrawObjectUnclipped(stage, objectIndex, pass));
    }

    private ID2D1PathGeometry SelectionMoveMask(StageControl stage, DrawingPoint[][] contours, bool outside)
    {
        var path = _factory!.CreatePathGeometry();
        using var sink = path.Open();
        sink.SetFillMode(FillMode.Alternate);
        if (outside)
        {
            sink.BeginFigure(Vector2.Zero, FigureBegin.Filled);
            sink.AddLine(new Vector2(_targetSize.Width, 0));
            sink.AddLine(new Vector2(_targetSize.Width, _targetSize.Height));
            sink.AddLine(new Vector2(0, _targetSize.Height));
            sink.EndFigure(FigureEnd.Closed);
        }
        foreach (var contour in contours)
        {
            if (contour.Length < 3) continue;
            sink.BeginFigure(WorldToVector(stage, contour[0]), FigureBegin.Filled);
            foreach (var point in contour.Skip(1)) sink.AddLine(WorldToVector(stage, point));
            sink.EndFigure(FigureEnd.Closed);
        }
        sink.Close();
        return path;
    }

    private void DrawSelectionMoveOrigins(StageControl stage)
    {
        if (!stage.SelectionMoveFeedbackActive) return;
        var source = stage.Scene;
        DrawSelectionMoveOpacity(StageControl.SelectionMoveOriginOpacity, null, () =>
        {
            try
            {
                foreach (var entry in stage.SelectionMoveOrigins)
                {
                    DrawOriginClipped(stage.SelectionMoveOriginClips(entry.Key), 0, () =>
                    {
                        stage.Scene = entry.Value;
                        try { DrawScene(stage, int.MaxValue, forceObjectRenderer: true); }
                        finally { stage.Scene = source; }
                    });
                }
            }
            finally { stage.Scene = source; }
        });

        void DrawOriginClipped(DrawingPoint[][][] clips, int clipIndex, Action draw)
        {
            if (clipIndex == clips.Length)
            {
                draw();
                return;
            }
            using var mask = SelectionMoveMask(stage, clips[clipIndex], outside: false);
            DrawSelectionMoveOpacity(1f, mask, () => DrawOriginClipped(clips, clipIndex + 1, draw));
        }
    }

    private void DrawSelectionMoveOpacity(float opacity, ID2D1Geometry? mask, Action draw)
    {
        var parameters = new LayerParameters
        {
            ContentBounds = Rect(0, 0, _targetSize.Width, _targetSize.Height),
            GeometricMask = mask,
            MaskAntialiasMode = AntialiasMode.PerPrimitive,
            MaskTransform = Matrix3x2.Identity,
            Opacity = opacity,
            LayerOptions = LayerOptions.None
        };
        _target!.PushLayer(parameters, null);
        try { draw(); }
        finally { _target.PopLayer(); }
    }
}
