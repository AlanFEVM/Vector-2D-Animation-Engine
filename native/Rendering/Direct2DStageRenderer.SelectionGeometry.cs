using Vortice.Direct2D1;

namespace VectorAnimationEngine;

internal sealed partial class Direct2DStageRenderer
{
    private readonly Dictionary<DrawingElementHit, SelectionElementGeometry> _selectionElementGeometry = new();
    private SelectionGeometryKey _selectionGeometryKey;
    internal long SelectionGeometryBuilds { get; private set; }
    internal long SelectionGeometryReuses { get; private set; }

    private readonly record struct SelectionGeometryKey(
        StageControl Stage, VectorScene Scene, IReadOnlyList<DrawingElementHit> Elements,
        long Revision, long Geometry, long Summary, long ActiveContent, int Frame, ulong Layers);

    private sealed record SelectionElementGeometry(
        ID2D1PathGeometry[] Paths, PointF[] Dots, LineEndpointStyle StartStyle,
        LineEndpointStyle EndStyle, SelectionHighlightKind Kind);

    private bool TryDrawCachedElementSelection(StageControl stage, DrawingElementHit hit, bool primary)
    {
        if (hit.Key.Kind is not (DrawingElementKind.Fill or DrawingElementKind.Stroke or DrawingElementKind.BoundaryStroke)) return false;
        if (stage.UsesReferenceProjection || stage.SelectionDragPreviewActive || stage.HasDistortPreview
            || stage.Scene.TryGetObjectDistortions(hit.Key.ObjectIndex, out _)) return false;
        var shape = stage.Scene.ShapeKind[hit.Key.ObjectIndex];
        if (hit.Key.Kind == DrawingElementKind.Fill && shape != ShapeKind.Path
            || hit.Key.Kind == DrawingElementKind.Stroke && shape == ShapeKind.Line) return false;

        if (!_selectionElementGeometry.TryGetValue(hit, out var cached))
        {
            var paths = new List<ID2D1PathGeometry>();
            var dots = new List<PointF>();
            var startStyle = LineEndpointStyle.Round;
            var endStyle = LineEndpointStyle.Round;
            var kind = hit.Key.Kind == DrawingElementKind.Fill ? SelectionHighlightKind.Fill : SelectionHighlightKind.Stroke;
            try
            {
                var segments = hit.Key.Kind == DrawingElementKind.BoundaryStroke
                    ? stage.GetPresentedEditableBezierWorldSegments(hit) : [];
                if (segments.Length > 0)
                {
                    var path = _factory!.CreatePathGeometry();
                    paths.Add(path);
                    using var sink = path.Open();
                    foreach (var segment in segments)
                    {
                        var control1 = ToVector(segment.Control1);
                        var control2 = ToVector(segment.Control2);
                        var end = ToVector(segment.End);
                        sink.BeginFigure(ToVector(segment.Start), FigureBegin.Hollow);
                        sink.AddBezier(new BezierSegment(in control1, in control2, in end));
                        sink.EndFigure(FigureEnd.Open);
                    }
                    sink.Close();
                    stage.Scene.TryGetLinePartEndpointStyles(hit.Key, stage.Frame, out startStyle, out endStyle);
                    kind = stage.SelectionHighlightForObject(hit.Key.ObjectIndex);
                }
                else
                {
                    PointF[][] contours;
                    if (hit.Key.Kind == DrawingElementKind.Fill)
                        contours = stage.Scene.GetVisiblePathSelectionPolylines(hit.Key.ObjectIndex, stage.Frame, stage.GetSelectedFillPartContours(hit));
                    else if (hit.Key.Kind == DrawingElementKind.Stroke)
                    {
                        var points = stage.GetSelectedStrokePartPoints(hit);
                        if (points.Length == 0) return false;
                        if (points.Length == 1 && stage.Scene.IsSelectionPointExposed(hit.Key.ObjectIndex, points[0], stage.Frame, strokePass: true)) dots.Add(points[0]);
                        contours = stage.Scene.GetVisiblePathSelectionPolylines(hit.Key.ObjectIndex, stage.Frame, [points], closed: false);
                        kind = stage.SelectionHighlightForObject(hit.Key.ObjectIndex, SelectionHighlightKind.Stroke);
                    }
                    else
                    {
                        var points = stage.GetSelectedBoundaryPartPoints(hit);
                        if (points.Length < 2) return false;
                        contours = [points];
                        dots.Add(points[0]);
                        dots.Add(points[^1]);
                    }
                    foreach (var contour in contours)
                        if (contour.Length >= 2) paths.Add(BuildSelectionPolylinePath(contour.Select(ToVector).ToArray()));
                }
                cached = new SelectionElementGeometry(paths.ToArray(), dots.ToArray(), startStyle, endStyle, kind);
                _selectionElementGeometry.Add(hit, cached);
                SelectionGeometryBuilds++;
            }
            catch
            {
                foreach (var path in paths) path.Dispose();
                throw;
            }
        }
        else SelectionGeometryReuses++;

        var transform = _target!.Transform;
        try
        {
            _target.Transform = WorldToScreenTransform(stage) * transform;
            foreach (var path in cached.Paths)
                DrawSelectionGeometry(path, primary, cached.StartStyle, cached.EndStyle, cached.Kind,
                    strokeWidthScale: stage.ScreenLengthToWorld(1));
        }
        finally { _target.Transform = transform; }
        foreach (var point in cached.Dots) DrawSelectionDot(WorldToVector(stage, point), primary, cached.Kind);
        return true;
    }

    private void PrepareSelectionElementGeometry(StageControl stage)
    {
        var key = new SelectionGeometryKey(stage, stage.Scene, stage.SelectedElements,
            stage.SelectionGeometryRevision, stage.Scene.GeometryRevision, stage.Scene.SummaryRevision, stage.Scene.ActiveContentRevision,
            stage.Frame, StageControl.GetReference3DWorkspaceFrameLayerRenderState(stage.Scene, stage.Frame));
        if (key == _selectionGeometryKey) return;
        ClearSelectionElementGeometry();
        _selectionGeometryKey = key;
    }

    private void ClearSelectionElementGeometry()
    {
        foreach (var entry in _selectionElementGeometry.Values)
            foreach (var path in entry.Paths) path.Dispose();
        _selectionElementGeometry.Clear();
        _selectionGeometryKey = default;
    }
}
