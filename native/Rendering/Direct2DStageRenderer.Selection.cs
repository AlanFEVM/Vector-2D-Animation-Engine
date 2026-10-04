using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using GdiColor = System.Drawing.Color;
using GdiPointF = System.Drawing.PointF;

namespace VectorAnimationEngine;

internal sealed partial class Direct2DStageRenderer
{
    private const int MaxSelectionOutlines = 512;
    private float _selectionHighlightPulse = 0.5f;

    private void DrawSelection(StageControl stage)
    {
        if ((stage.SelectedObject < 0 || stage.SelectedObject >= stage.Scene.ObjectCount)
            && stage.SelectedObjects.Count == 0
            && !HasHoveredLine(stage)
            && !stage.DrawingObjectSelectionVisible)
        {
            return;
        }

        _selectionHighlightPulse = stage.SelectionHighlightPulse;

        if (stage.SelectedElements.Count > 0)
        {
            var selectionTransform = _target!.Transform;
            try
            {
                if (stage.SelectionDragPreviewActive)
                {
                    var offset = stage.SelectionDragPreviewOffset;
                    var screenScale = VectorUnits.PixelsPerUnit * stage.Zoom;
                    _target.Transform = selectionTransform * Matrix3x2.CreateTranslation(
                        offset.X * screenScale,
                        offset.Y * screenScale);
                }
                foreach (var objectIndex in stage.SelectedElements
                             .Where(hit => (uint)hit.Key.ObjectIndex < stage.Scene.ObjectCount
                                 && hit.Key.Kind == DrawingElementKind.Stroke
                                 && stage.Scene.ShapeKind[hit.Key.ObjectIndex] == ShapeKind.Line
                                 && stage.Scene.IsObjectActive(hit.Key.ObjectIndex, stage.Frame))
                             .Select(hit => hit.Key.ObjectIndex)
                             .Distinct()
                             .Take(MaxSelectionOutlines))
                {
                    DrawBezierSelectionContext(stage, objectIndex);
                }

                var drawnElements = 0;
                foreach (var hit in stage.SelectedElements)
                {
                    if ((uint)hit.Key.ObjectIndex >= stage.Scene.ObjectCount
                        || hit.Key == stage.SelectedElement.Key
                        || stage.SuppressFillEdgeBezierSelectionOutline(hit.Key.ObjectIndex)
                        || !stage.Scene.IsObjectActive(hit.Key.ObjectIndex, stage.Frame))
                    {
                        continue;
                    }

                    DrawElementSelectionOutline(stage, hit, primary: false);
                    drawnElements++;
                    if (drawnElements >= MaxSelectionOutlines) break;
                }

                var primaryElement = stage.SelectedElement;
                if (primaryElement.IsValid
                    && (uint)primaryElement.Key.ObjectIndex < stage.Scene.ObjectCount
                    && !stage.SuppressFillEdgeBezierSelectionOutline(primaryElement.Key.ObjectIndex)
                    && stage.Scene.IsObjectActive(primaryElement.Key.ObjectIndex, stage.Frame))
                {
                    DrawElementSelectionOutline(stage, primaryElement, primary: true);
                    if (stage.PenPathHandlesVisible && !(stage.TransformMode || stage.DistortMode))
                    {
                        foreach (var objectIndex in stage.SelectedElements
                                     .Where(hit => hit.Key.Kind == DrawingElementKind.Stroke
                                         && (uint)hit.Key.ObjectIndex < stage.Scene.ObjectCount
                                         && stage.Scene.ShapeKind[hit.Key.ObjectIndex] == ShapeKind.Line
                                         && stage.Scene.IsObjectActive(hit.Key.ObjectIndex, stage.Frame))
                                     .Select(hit => hit.Key.ObjectIndex)
                                     .Distinct()
                                     .Take(MaxSelectionOutlines))
                        {
                            DrawBezierHandles(stage, objectIndex);
                        }
                    }
                    else if (stage.IsValidEditableBezierHit(primaryElement))
                    {
                        if (!(stage.TransformMode || stage.DistortMode)) DrawBezierHandles(stage, primaryElement);
                    }
                }
            }
            finally
            {
                _target.Transform = selectionTransform;
            }

            DrawDrawingObjectSelectionOverlay(stage);
            DrawTransformOverlay(stage);
            DrawDistortOverlay(stage);
            DrawHoveredLineControls(stage);
            return;
        }

        var drawn = 0;
        foreach (var index in stage.SelectedObjects)
        {
            if (index == stage.SelectedObject || index < 0 || index >= stage.Scene.ObjectCount) continue;
            if (stage.SuppressFillEdgeBezierSelectionOutline(index)) continue;
            if (!stage.Scene.IsObjectActive(index, stage.Frame)) continue;
            DrawSelectionOutline(stage, index, primary: false);
            drawn++;
            if (drawn >= MaxSelectionOutlines) break;
        }

        var primary = stage.SelectedObject;
        if (primary >= 0
            && primary < stage.Scene.ObjectCount
            && !stage.SuppressFillEdgeBezierSelectionOutline(primary)
            && stage.Scene.IsObjectActive(primary, stage.Frame))
        {
            DrawSelectionOutline(stage, primary, primary: true);
        }

        DrawDrawingObjectSelectionOverlay(stage);
        DrawTransformOverlay(stage);
        DrawDistortOverlay(stage);
        DrawHoveredLineControls(stage);
    }

    private void DrawSelectionOutline(StageControl stage, int i, bool primary)
    {
        var shape = stage.Scene.ShapeKind.Length > i ? stage.Scene.ShapeKind[i] : ShapeKind.Rectangle;
        if (stage.Scene.TryGetObjectDistortions(i, out _))
        {
            foreach (var contour in stage.Scene.GetDistortedObjectBoundaryContours(i))
            {
                var points = contour.Select(point => WorldToVector(stage, point)).ToArray();
                if (points.Length == 1) DrawSelectionDot(points[0], primary, SelectionHighlightKind.Fill);
                else if (points.Length > 1)
                {
                    DrawSelectionPolyline(
                        points.Length >= 3 ? CloseSelectionPolyline(points) : points,
                        primary,
                        SelectionHighlightKind.Fill);
                }
            }
            return;
        }

        if (shape == ShapeKind.Text)
        {
            if (stage.TextAreaOverlayVisible(i))
            {
                DrawTextAreaOverlay(stage, i, primary && stage.TextAreaResizeHandlesVisible(i));
            }
            return;
        }
        if (shape == ShapeKind.Line)
        {
            if (primary && !(stage.TransformMode || stage.DistortMode)) DrawBezierGuides(stage, i);
            else DrawBezierOutline(stage, i, primary);
            return;
        }

        if (shape == ShapeKind.MixingStroke)
        {
            foreach (var vectors in GetMixingStrokeSelectionContours(stage, i))
            {
                if (vectors.Length == 1)
                {
                    DrawSelectionDot(vectors[0], primary, SelectionHighlightKind.Fill);
                }
                else if (vectors.Length > 1)
                {
                    DrawSelectionPolyline(vectors, primary, SelectionHighlightKind.Fill);
                }
            }
            return;
        }

        if (IsFreehandShape(shape))
        {
            var freehandVectors = GetBoundaryVectors(stage, i);
            var highlightKind = StageControl.SelectionHighlightForShape(shape);
            if (freehandVectors.Length == 1)
            {
                DrawSelectionDot(freehandVectors[0], primary, highlightKind);
            }
            else if (freehandVectors.Length > 1)
            {
                DrawSelectionPolyline(freehandVectors, primary, highlightKind);
            }

            return;
        }

        if (shape == ShapeKind.Path)
        {
            var path = ObjectPathGeometry(stage.Scene, i);
            if (path is not null)
            {
                var old = _target!.Transform;
                try
                {
                    _target.Transform = ObjectLocalToScreenTransform(stage, stage.Scene, i);
                    DrawSelectionGeometry(
                        path,
                        primary,
                        highlightKind: SelectionHighlightKind.Fill,
                        strokeWidthScale: stage.ScreenLengthToWorld(1));
                }
                finally
                {
                    _target.Transform = old;
                }
            }
        }
        else if (TryGetSelectionWorldContours(stage.Scene, i, shape, out var contours))
        {
            foreach (var contour in contours)
            {
                var vectors = contour.Select(point => WorldToVector(stage, point)).ToArray();
                if (vectors.Length >= 3) DrawSelectionPolyline(CloseSelectionPolyline(vectors), primary, SelectionHighlightKind.Fill);
            }
        }
        else
        {
            var points = GetBoundaryVectors(stage, i);
            if (points.Length < 2) return;
            DrawSelectionPolyline(points, primary, StageControl.SelectionHighlightForShape(shape));
        }

        if (!primary || shape is ShapeKind.Path or ShapeKind.Text || stage.TransformMode || stage.DistortMode) return;
        foreach (var handle in BoundaryHandles()) DrawHandle(WorldToVector(stage, stage.GetBoundaryHandleWorldPoint(i, handle)), BrushFor(GdiColor.FromArgb(255, 255, 240, 168).ToArgb()), 9);
    }

    private static Vector2[][] GetMixingStrokeSelectionContours(StageControl stage, int objectIndex)
    {
        return stage.GetMixingStrokeScreenContours(stage.Scene, objectIndex)
            .Select(contour => contour.Select(ToVector).ToArray())
            .ToArray();
    }

    private static bool TryGetSelectionWorldContours(VectorScene scene, int objectIndex, ShapeKind shape, out GdiPointF[][] contours)
    {
        if (shape == ShapeKind.Text) return scene.TryGetTextWorldContours(objectIndex, out contours);
        contours = Array.Empty<GdiPointF[]>();
        return false;
    }

    private void DrawElementSelectionOutline(StageControl stage, DrawingElementHit hit, bool primary)
    {
        var objectIndex = hit.Key.ObjectIndex;
        var shape = stage.Scene.ShapeKind[objectIndex];
        if (hit.Key.Kind == DrawingElementKind.BoundaryStroke
            && DrawPresentedEditableBezierSelection(stage, hit, primary))
        {
            return;
        }

        if (stage.Scene.TryGetObjectDistortions(objectIndex, out _))
        {
            foreach (var contour in stage.Scene.GetDistortedObjectBoundaryContours(objectIndex))
            {
                var points = contour.Select(point => WorldToVector(stage, point)).ToArray();
                if (points.Length == 1) DrawSelectionDot(points[0], primary, SelectionHighlightKind.Fill);
                else if (points.Length > 1)
                {
                    DrawSelectionPolyline(
                        points.Length >= 3 ? CloseSelectionPolyline(points) : points,
                        primary,
                        SelectionHighlightKind.Fill);
                }
            }
            return;
        }

        if (hit.Key.Kind == DrawingElementKind.Stroke)
        {
            if (shape == ShapeKind.Line)
            {
                DrawBezierGuides(stage, objectIndex, hit.StartT, hit.EndT, primary);
                return;
            }

            var points = stage.GetSelectedStrokePartPoints(hit)
                .Select(point => WorldToVector(stage, point))
                .ToArray();
            if (points.Length == 1) DrawSelectionDot(points[0], primary, SelectionHighlightKind.Stroke);
            else if (points.Length > 1) DrawSelectionPolyline(points, primary, SelectionHighlightKind.Stroke);
            return;
        }

        if (hit.Key.Kind == DrawingElementKind.Fill)
        {
            foreach (var contour in stage.GetSelectedFillPartContours(hit))
            {
                var points = contour.Select(point => WorldToVector(stage, point)).ToArray();
                if (points.Length >= 3) DrawSelectionPolyline(CloseSelectionPolyline(points), primary, SelectionHighlightKind.Fill);
            }

            return;
        }

        if (hit.Key.Kind == DrawingElementKind.BoundaryStroke)
        {
            var points = stage.GetSelectedBoundaryPartPoints(hit)
                .Select(point => WorldToVector(stage, point))
                .ToArray();
            if (points.Length > 1) DrawSelectionPolyline(points, primary, SelectionHighlightKind.Stroke);
        }
    }

    private bool DrawPresentedEditableBezierSelection(
        StageControl stage,
        DrawingElementHit hit,
        bool primary)
    {
        var segments = stage.GetPresentedEditableBezierWorldSegments(hit);
        if (segments.Length == 0) return false;

        using var path = _factory!.CreatePathGeometry();
        using (var sink = path.Open())
        {
            foreach (var segment in segments)
            {
                var start = WorldToVector(stage, segment.Start);
                var control1 = WorldToVector(stage, segment.Control1);
                var control2 = WorldToVector(stage, segment.Control2);
                var end = WorldToVector(stage, segment.End);
                sink.BeginFigure(start, FigureBegin.Hollow);
                sink.AddBezier(new BezierSegment(in control1, in control2, in end));
                sink.EndFigure(FigureEnd.Open);
            }
            sink.Close();
        }

        stage.Scene.TryGetLinePartEndpointStyles(
            hit.Key,
            stage.Frame,
            out var startStyle,
            out var endStyle);
        DrawSelectionGeometry(path, primary, startStyle, endStyle);
        return true;
    }

    private void DrawBezierOutline(StageControl stage, int i, bool primary)
    {
        var startStyle = stage.Scene.GetLineEndpointStyle(i, startEndpoint: true);
        var endStyle = stage.Scene.GetLineEndpointStyle(i, startEndpoint: false);
        if (stage.Scene.IsLineStraight(i))
        {
            var points = GetBezierScreenPoints(stage, i);
            DrawSelectionLine(points.Start, points.End, primary, startStyle, endStyle);
        }
        else
        {
            DrawCachedLineSelectionGeometry(stage, i, primary, startStyle, endStyle);
        }
    }

    private void DrawBezierGuides(StageControl stage, int i)
    {
        var startStyle = stage.Scene.GetLineEndpointStyle(i, startEndpoint: true);
        var endStyle = stage.Scene.GetLineEndpointStyle(i, startEndpoint: false);
        var hit = new DrawingElementHit(
            new DrawingElementKey(i, DrawingElementKind.Stroke, 0),
            0,
            0,
            1);
        var segments = stage.GetPresentedEditableBezierWorldSegments(hit);
        if (segments.Length == 0) return;
        using (var path = _factory!.CreatePathGeometry())
        using (var sink = path.Open())
        {
            foreach (var segment in segments)
            {
                var start = WorldToVector(stage, segment.Start);
                var control1 = WorldToVector(stage, segment.Control1);
                var control2 = WorldToVector(stage, segment.Control2);
                var end = WorldToVector(stage, segment.End);
                sink.BeginFigure(start, FigureBegin.Hollow);
                sink.AddBezier(new BezierSegment(in control1, in control2, in end));
                sink.EndFigure(FigureEnd.Open);
            }
            sink.Close();
            DrawSelectionGeometry(path, primary: true, startStyle, endStyle);
        }

        foreach (var segment in segments)
        {
            DrawBezierHandles(
                WorldToVector(stage, segment.Start),
                WorldToVector(stage, segment.Control1),
                WorldToVector(stage, segment.Control2),
                WorldToVector(stage, segment.End));
        }
    }

    private void DrawBezierHandles(StageControl stage, int i)
    {
        var (start, control1, control2, end) = GetBezierScreenPoints(stage, i);
        DrawBezierHandles(start, control1, control2, end);
    }

    private void DrawBezierHandles(StageControl stage, DrawingElementHit hit)
    {
        var segments = stage.GetPresentedEditableBezierWorldSegments(hit);
        if (segments.Length == 0) return;
        if ((uint)hit.PresentedBezierSegmentIndex < segments.Length)
        {
            var selected = segments[hit.PresentedBezierSegmentIndex];
            DrawBezierHandles(
                WorldToVector(stage, selected.Start),
                WorldToVector(stage, selected.Control1),
                WorldToVector(stage, selected.Control2),
                WorldToVector(stage, selected.End));
            return;
        }

        foreach (var segment in segments)
        {
            DrawBezierHandles(
                WorldToVector(stage, segment.Start),
                WorldToVector(stage, segment.Control1),
                WorldToVector(stage, segment.Control2),
                WorldToVector(stage, segment.End));
        }
    }

    private void DrawHoveredLineControls(StageControl stage)
    {
        var hit = stage.HoveredLineElement;
        if (!stage.ShouldDrawHoveredLineControls()) return;

        if (!TryGetEditableBezierScreenPoints(stage, hit, out var start, out var control1, out var control2, out var end)) return;
        var guide = BrushFor(GdiColor.FromArgb(120, 112, 204, 255).ToArgb());
        _target!.DrawLine(start, control1, guide, 1);
        _target.DrawLine(end, control2, guide, 1);
        DrawHandle(start, BrushFor(GdiColor.FromArgb(210, 255, 240, 168).ToArgb()), 7);
        DrawHandle(end, BrushFor(GdiColor.FromArgb(210, 255, 240, 168).ToArgb()), 7);
        DrawHandle(control1, BrushFor(GdiColor.FromArgb(210, 112, 204, 255).ToArgb()), 9);
        DrawHandle(control2, BrushFor(GdiColor.FromArgb(210, 112, 204, 255).ToArgb()), 9);
    }

    private static bool HasHoveredLine(StageControl stage)
    {
        return stage.IsValidEditableBezierHit(stage.HoveredLineElement);
    }

    private void DrawBezierHandles(Vector2 start, Vector2 control1, Vector2 control2, Vector2 end)
    {
        var guide = BrushFor(GdiColor.FromArgb(190, 112, 204, 255).ToArgb());
        _target!.DrawLine(start, control1, guide, 1);
        _target.DrawLine(end, control2, guide, 1);
        DrawHandle(start, BrushFor(GdiColor.FromArgb(255, 255, 240, 168).ToArgb()), 7);
        DrawHandle(end, BrushFor(GdiColor.FromArgb(255, 255, 240, 168).ToArgb()), 7);
        DrawHandle(control1, BrushFor(GdiColor.FromArgb(255, 112, 204, 255).ToArgb()), 9);
        DrawHandle(control2, BrushFor(GdiColor.FromArgb(255, 112, 204, 255).ToArgb()), 9);
    }

    private void DrawTransformOverlay(StageControl stage)
    {
        if (!stage.TransformBoundsVisible) return;
        var geometry = stage.GetTransformOverlayScreenGeometry();
        var topLeft = ToVector(geometry.TopLeft);
        var topRight = ToVector(geometry.TopRight);
        var bottomRight = ToVector(geometry.BottomRight);
        var bottomLeft = ToVector(geometry.BottomLeft);
        var accent = BrushFor(GdiColor.FromArgb(235, 112, 204, 255).ToArgb());
        _target!.DrawLine(topLeft, topRight, accent, 1);
        _target.DrawLine(topRight, bottomRight, accent, 1);
        _target.DrawLine(bottomRight, bottomLeft, accent, 1);
        _target.DrawLine(bottomLeft, topLeft, accent, 1);
        var handleBrush = BrushFor(GdiColor.FromArgb(255, 255, 240, 168).ToArgb());
        foreach (var (_, point, _) in geometry.ResizeHandles) DrawHandle(ToVector(point), handleBrush, 8);
        var rotationBrush = BrushFor(GdiColor.FromArgb(255, 112, 204, 255).ToArgb());
        var handleBorder = BrushFor(GdiColor.FromArgb(255, 16, 18, 22).ToArgb());
        foreach (var (_, point, anchor) in geometry.RotationHandles)
        {
            var handle = ToVector(point);
            var corner = ToVector(anchor);
            _target.DrawLine(corner, handle, accent, 1);
            _target.FillEllipse(new Ellipse(handle, 4, 4), rotationBrush);
            _target.DrawEllipse(new Ellipse(handle, 4, 4), handleBorder, 1);
        }

        foreach (var (_, point, anchor) in geometry.SkewHandles)
        {
            var handle = ToVector(point);
            var edge = ToVector(anchor);
            _target.DrawLine(edge, handle, accent, 1);
            using var diamond = _factory!.CreatePathGeometry();
            using (var sink = diamond.Open())
            {
                sink.BeginFigure(new Vector2(handle.X, handle.Y - 5), FigureBegin.Filled);
                sink.AddLine(new Vector2(handle.X + 5, handle.Y));
                sink.AddLine(new Vector2(handle.X, handle.Y + 5));
                sink.AddLine(new Vector2(handle.X - 5, handle.Y));
                sink.EndFigure(FigureEnd.Closed);
                sink.Close();
            }

            _target.FillGeometry(diamond, rotationBrush);
            _target.DrawGeometry(diamond, handleBorder, 1);
        }

        if (stage.IsTransformFocusVisible(geometry))
        {
            var focus = ToVector(stage.TransformOverlayPointToScreen(stage.TransformFocus));
            var focusLine = BrushFor(GdiColor.FromArgb(245, 104, 255, 188).ToArgb());
            _target.FillEllipse(new Ellipse(focus, 6, 6), BrushFor(GdiColor.FromArgb(220, 30, 82, 69).ToArgb()));
            _target.DrawEllipse(new Ellipse(focus, 6, 6), focusLine, 1.5f);
            _target.DrawLine(new Vector2(focus.X - 9, focus.Y), new Vector2(focus.X + 9, focus.Y), focusLine, 1.5f);
            _target.DrawLine(new Vector2(focus.X, focus.Y - 9), new Vector2(focus.X, focus.Y + 9), focusLine, 1.5f);
        }
    }

    private void DrawDistortOverlay(StageControl stage)
    {
        if (!stage.DistortBoundsVisible) return;
        var geometry = stage.GetDistortOverlayScreenGeometry();
        var boundary = BrushFor(GdiColor.FromArgb(245, 104, 255, 188).ToArgb());
        var grid = BrushFor(GdiColor.FromArgb(120, 104, 255, 188).ToArgb());
        var guide = BrushFor(GdiColor.FromArgb(165, 112, 204, 255).ToArgb());
        var dashedBoundary = PreviewBoundsStrokeStyle();
        using (var path = _factory!.CreatePathGeometry())
        {
            using (var sink = path.Open())
            {
                foreach (var segment in geometry.BezierSegments)
                {
                    var start = ToVector(segment.Start);
                    var control1 = ToVector(segment.Control1);
                    var control2 = ToVector(segment.Control2);
                    var end = ToVector(segment.End);
                    sink.BeginFigure(start, FigureBegin.Hollow);
                    sink.AddBezier(new BezierSegment(in control1, in control2, in end));
                    sink.EndFigure(FigureEnd.Open);
                }
                sink.Close();
            }

            _target!.DrawGeometry(path, boundary, 1.4f, dashedBoundary);
        }

        for (var index = 1; index < 3; index++)
        {
            var t = index / 3f;
            var left = ToVector(stage.TransformOverlayPointToScreen(
                stage.DistortFrame.BoundaryPoint(DistortSide.Left, t)));
            var right = ToVector(stage.TransformOverlayPointToScreen(
                stage.DistortFrame.BoundaryPoint(DistortSide.Right, t)));
            var top = ToVector(stage.TransformOverlayPointToScreen(
                stage.DistortFrame.BoundaryPoint(DistortSide.Top, t)));
            var bottom = ToVector(stage.TransformOverlayPointToScreen(
                stage.DistortFrame.BoundaryPoint(DistortSide.Bottom, t)));
            _target.DrawLine(left, right, grid, 1);
            _target.DrawLine(top, bottom, grid, 1);
        }

        foreach (var handle in geometry.VisualHandles)
        {
            if (handle.Reference.Kind == DistortHandleKind.Anchor) continue;
            _target.DrawLine(ToVector(handle.Anchor), ToVector(handle.Point), guide, 1, dashedBoundary);
        }

        var anchorFill = BrushFor(GdiColor.FromArgb(255, 255, 240, 168).ToArgb());
        var controlFill = BrushFor(GdiColor.FromArgb(255, 112, 204, 255).ToArgb());
        var border = BrushFor(GdiColor.FromArgb(255, 16, 18, 22).ToArgb());
        foreach (var visual in geometry.VisualHandles)
        {
            var handle = ToVector(visual.Point);
            if (visual.Reference.Kind == DistortHandleKind.Anchor)
            {
                var rect = Rect(handle.X - 4.5f, handle.Y - 4.5f, 9, 9);
                _target.FillRectangle(in rect, anchorFill);
                _target.DrawRectangle(in rect, border, 1);
            }
            else
            {
                _target.FillEllipse(new Ellipse(handle, 3.5f, 3.5f), controlFill);
                _target.DrawEllipse(new Ellipse(handle, 3.5f, 3.5f), border, 1);
            }
        }
    }

    private void DrawDrawingObjectSelectionOverlay(StageControl stage)
    {
        if (!stage.DrawingObjectSelectionVisible) return;
        var bounds = stage.DrawingObjectSelectionBounds;
        var topLeft = WorldToVector(stage, new GdiPointF(bounds.Left, bounds.Top));
        var bottomRight = WorldToVector(stage, new GdiPointF(bounds.Right, bounds.Bottom));
        var left = Math.Min(topLeft.X, bottomRight.X);
        var top = Math.Min(topLeft.Y, bottomRight.Y);
        var right = Math.Max(topLeft.X, bottomRight.X);
        var bottom = Math.Max(topLeft.Y, bottomRight.Y);
        if (right - left <= 0 || bottom - top <= 0) return;

        var rect = Rect(left, top, right - left, bottom - top);
        _target!.DrawRectangle(in rect, BrushFor(GdiColor.FromArgb(82, 24, 255, 104).ToArgb()), 14);
        _target.DrawRectangle(in rect, BrushFor(GdiColor.FromArgb(185, 38, 238, 122).ToArgb()), 7);
        _target.DrawRectangle(in rect, BrushFor(GdiColor.FromArgb(255, 118, 255, 170).ToArgb()), 2.2f);
        if (stage.DrawingObjectAnchorVisible)
        {
            var anchor = WorldToVector(stage, stage.DrawingObjectAnchor);
            var fill = BrushFor(GdiColor.FromArgb(255, 255, 240, 168).ToArgb());
            var border = BrushFor(GdiColor.FromArgb(255, 16, 18, 22).ToArgb());
            var cross = BrushFor(GdiColor.FromArgb(255, 118, 255, 170).ToArgb());
            _target.FillEllipse(new Ellipse(anchor, 4, 4), fill);
            _target.DrawEllipse(new Ellipse(anchor, 4, 4), border, 1);
            _target.DrawLine(new Vector2(anchor.X - 9, anchor.Y), new Vector2(anchor.X + 9, anchor.Y), cross, 2.2f);
            _target.DrawLine(new Vector2(anchor.X, anchor.Y - 9), new Vector2(anchor.X, anchor.Y + 9), cross, 2.2f);
        }
    }

    private void DrawBezierGuides(StageControl stage, int i, float startT, float endT, bool primary)
    {
        var (start, control1, control2, end) = GetBezierScreenPoints(stage, i);
        using var partialPath = BuildBezierSamplePath(start, control1, control2, end, startT, endT);
        DrawSelectionGeometry(
            partialPath,
            primary,
            startT <= DrawingTopologyRules.UnitIntersectionTolerance
                ? stage.Scene.GetLineEndpointStyle(i, startEndpoint: true)
                : LineEndpointStyle.Round,
            endT >= 1f - DrawingTopologyRules.UnitIntersectionTolerance
                ? stage.Scene.GetLineEndpointStyle(i, startEndpoint: false)
                : LineEndpointStyle.Round);
    }

    private void DrawBezierSelectionContext(StageControl stage, int i)
    {
        if (stage.Scene.IsLineStraight(i))
        {
            var points = GetBezierScreenPoints(stage, i);
            _target!.DrawLine(points.Start, points.End, BrushFor(GdiColor.FromArgb(80, 255, 235, 120).ToArgb()), 1.2f);
        }
        else
        {
            DrawCachedLineGeometry(
                stage,
                i,
                BrushFor(GdiColor.FromArgb(80, 255, 235, 120).ToArgb()),
                stage.ScreenLengthToWorld(1.2f));
        }
    }

    private void DrawCachedLineSelectionGeometry(
        StageControl stage,
        int objectIndex,
        bool primary,
        LineEndpointStyle startStyle,
        LineEndpointStyle endStyle)
    {
        var old = _target!.Transform;
        try
        {
            _target.Transform = ObjectLocalToScreenTransform(stage, stage.Scene, objectIndex) * old;
            DrawSelectionGeometry(
                LineGeometry(stage.Scene, objectIndex),
                primary,
                startStyle,
                endStyle,
                strokeWidthScale: stage.ScreenLengthToWorld(1));
        }
        finally
        {
            _target.Transform = old;
        }
    }

    private ID2D1PathGeometry BuildBezierSamplePath(
        Vector2 start,
        Vector2 control1,
        Vector2 control2,
        Vector2 end,
        float startT,
        float endT)
    {
        startT = Math.Clamp(startT, 0, 1);
        endT = Math.Clamp(endT, startT, 1);
        var path = _factory!.CreatePathGeometry();
        using (var sink = path.Open())
        {
            sink.BeginFigure(CubicPoint(start, control1, control2, end, startT), FigureBegin.Hollow);
            const int samples = 20;
            for (var i = 1; i <= samples; i++)
            {
                var t = startT + (endT - startT) * i / samples;
                sink.AddLine(CubicPoint(start, control1, control2, end, t));
            }

            sink.EndFigure(FigureEnd.Open);
            sink.Close();
        }

        return path;
    }

    private void DrawBoundaryPartialOutline(Vector2[] points, float startT, float endT)
    {
        using var fullPath = BuildPolylineSamplePath(points, 0, 1);
        using var partialPath = BuildPolylineSamplePath(points, startT, endT);
        _target!.DrawGeometry(fullPath, BrushFor(GdiColor.FromArgb(80, 255, 217, 107).ToArgb()), 1.2f);
        DrawSelectionGeometry(partialPath, primary: true);
    }

    private void DrawSelectionGeometry(
        ID2D1PathGeometry path,
        bool primary,
        LineEndpointStyle startStyle = LineEndpointStyle.Round,
        LineEndpointStyle endStyle = LineEndpointStyle.Round,
        SelectionHighlightKind highlightKind = SelectionHighlightKind.Stroke,
        float strokeWidthScale = 1)
    {
        var strokeStyle = LineStrokeStyle(
            LineCapForEndpoint(startStyle),
            LineCapForEndpoint(endStyle),
            startStyle == LineEndpointStyle.Sharp || endStyle == LineEndpointStyle.Sharp);
        _target!.DrawGeometry(path, BrushFor(SelectionOuterGlowColor(highlightKind, primary).ToArgb()), SelectionOuterGlowWidth(highlightKind, primary) * strokeWidthScale, strokeStyle);
        _target.DrawGeometry(path, BrushFor(SelectionGlowColor(highlightKind, primary).ToArgb()), SelectionGlowWidth(highlightKind, primary) * strokeWidthScale, strokeStyle);
        _target.DrawGeometry(path, BrushFor(SelectionLineColor(highlightKind, primary).ToArgb()), SelectionLineWidth(highlightKind, primary) * strokeWidthScale, strokeStyle);
    }

    private void DrawSelectionLine(
        Vector2 start,
        Vector2 end,
        bool primary,
        LineEndpointStyle startStyle,
        LineEndpointStyle endStyle)
    {
        var strokeStyle = LineStrokeStyle(
            LineCapForEndpoint(startStyle),
            LineCapForEndpoint(endStyle),
            startStyle == LineEndpointStyle.Sharp || endStyle == LineEndpointStyle.Sharp);
        _target!.DrawLine(start, end, BrushFor(SelectionOuterGlowColor(SelectionHighlightKind.Stroke, primary).ToArgb()), SelectionOuterGlowWidth(SelectionHighlightKind.Stroke, primary), strokeStyle);
        _target.DrawLine(start, end, BrushFor(SelectionGlowColor(SelectionHighlightKind.Stroke, primary).ToArgb()), SelectionGlowWidth(SelectionHighlightKind.Stroke, primary), strokeStyle);
        _target.DrawLine(start, end, BrushFor(SelectionLineColor(SelectionHighlightKind.Stroke, primary).ToArgb()), SelectionLineWidth(SelectionHighlightKind.Stroke, primary), strokeStyle);
    }

    private void DrawSelectionPolyline(Vector2[] points, bool primary, SelectionHighlightKind highlightKind)
    {
        if (points.Length < 2) return;
        using var path = BuildSelectionPolylinePath(points);
        DrawSelectionGeometry(path, primary, highlightKind: highlightKind);
    }

    private ID2D1PathGeometry BuildSelectionPolylinePath(Vector2[] points)
    {
        var path = _factory!.CreatePathGeometry();
        using (var sink = path.Open())
        {
            sink.BeginFigure(points[0], FigureBegin.Hollow);
            for (var i = 1; i < points.Length; i++) sink.AddLine(points[i]);
            sink.EndFigure(FigureEnd.Open);
            sink.Close();
        }

        return path;
    }

    private static Vector2[] CloseSelectionPolyline(Vector2[] points)
    {
        if (points.Length == 0 || points[0] == points[^1]) return points;
        var result = new Vector2[points.Length + 1];
        Array.Copy(points, result, points.Length);
        result[^1] = points[0];
        return result;
    }

    private void DrawSelectionDot(Vector2 point, bool primary, SelectionHighlightKind highlightKind)
    {
        var pulseScale = 0.92f + 0.16f * _selectionHighlightPulse;
        var outer = (primary ? 12f : 8f) * pulseScale;
        var glow = (primary ? 7f : 5f) * pulseScale;
        var line = (primary ? 2.5f : 1.5f) * pulseScale;
        _target!.FillEllipse(new Ellipse(point, outer * 0.5f, outer * 0.5f), BrushFor(SelectionOuterGlowColor(highlightKind, primary).ToArgb()));
        _target.FillEllipse(new Ellipse(point, glow * 0.5f, glow * 0.5f), BrushFor(SelectionGlowColor(highlightKind, primary).ToArgb()));
        _target.FillEllipse(new Ellipse(point, line * 0.5f, line * 0.5f), BrushFor(SelectionLineColor(highlightKind, primary).ToArgb()));
    }

    private GdiColor SelectionOuterGlowColor(SelectionHighlightKind highlightKind, bool primary) =>
        StageControl.SelectionOuterGlowColor(highlightKind, primary, _selectionHighlightPulse);

    private GdiColor SelectionGlowColor(SelectionHighlightKind highlightKind, bool primary) =>
        StageControl.SelectionGlowColor(highlightKind, primary, _selectionHighlightPulse);

    private static GdiColor SelectionLineColor(SelectionHighlightKind highlightKind, bool primary) =>
        StageControl.SelectionLineColor(highlightKind, primary);

    private float SelectionOuterGlowWidth(SelectionHighlightKind highlightKind, bool primary) =>
        StageControl.SelectionOuterGlowWidth(highlightKind, primary, _selectionHighlightPulse);

    private float SelectionGlowWidth(SelectionHighlightKind highlightKind, bool primary) =>
        StageControl.SelectionGlowWidth(highlightKind, primary, _selectionHighlightPulse);

    private float SelectionLineWidth(SelectionHighlightKind highlightKind, bool primary) =>
        StageControl.SelectionLineWidth(highlightKind, primary, _selectionHighlightPulse);

    private ID2D1PathGeometry BuildPolylineSamplePath(Vector2[] points, float startT, float endT)
    {
        startT = Math.Clamp(startT, 0, 1);
        endT = Math.Clamp(endT, startT, 1);
        var path = _factory!.CreatePathGeometry();
        using (var sink = path.Open())
        {
            sink.BeginFigure(PolylinePointAt(points, startT), FigureBegin.Hollow);
            var segments = Math.Max(1, points.Length - 1);
            var samples = Math.Max(2, (int)Math.Ceiling((endT - startT) * segments * 4));
            for (var i = 1; i <= samples; i++)
            {
                var t = startT + (endT - startT) * i / samples;
                sink.AddLine(PolylinePointAt(points, t));
            }

            sink.EndFigure(FigureEnd.Open);
            sink.Close();
        }

        return path;
    }

    private static Vector2 PolylinePointAt(Vector2[] points, float t)
    {
        if (points.Length == 0) return default;
        if (points.Length == 1) return points[0];
        t = Math.Clamp(t, 0, 1);
        var segments = points.Length - 1;
        var scaled = t * segments;
        var index = Math.Min(segments - 1, (int)MathF.Floor(scaled));
        return Vector2.Lerp(points[index], points[index + 1], scaled - index);
    }

    private static Vector2 CubicPoint(Vector2 start, Vector2 control1, Vector2 control2, Vector2 end, float t)
    {
        var inv = 1 - t;
        return start * (inv * inv * inv)
            + control1 * (3 * inv * inv * t)
            + control2 * (3 * inv * t * t)
            + end * (t * t * t);
    }

    private bool TryGetEditableBezierScreenPoints(
        StageControl stage,
        DrawingElementHit hit,
        out Vector2 start,
        out Vector2 control1,
        out Vector2 control2,
        out Vector2 end)
    {
        start = default;
        control1 = default;
        control2 = default;
        end = default;
        if (stage.TryGetEditableBezierWorldPoints(
                hit,
                out var worldStart,
                out var worldControl1,
                out var worldControl2,
                out var worldEnd))
        {
            start = WorldToVector(stage, worldStart);
            control1 = WorldToVector(stage, worldControl1);
            control2 = WorldToVector(stage, worldControl2);
            end = WorldToVector(stage, worldEnd);
            return true;
        }

        return false;
    }

    private void DrawTextAreaOverlay(StageControl stage, int objectIndex, bool drawHandles)
    {
        var corners = stage.GetTextAreaWorldCorners(objectIndex)
            .Select(point => WorldToVector(stage, point))
            .ToArray();
        if (corners.Length != 4) return;

        var glow = BrushFor(StageControl.TextAreaGlowColor.ToArgb());
        var border = BrushFor(StageControl.TextAreaBorderColor.ToArgb());
        for (var index = 0; index < corners.Length; index++)
        {
            var start = corners[index];
            var end = corners[(index + 1) % corners.Length];
            _target!.DrawLine(start, end, glow, 4f);
            _target.DrawLine(start, end, border, 1.6f);
        }
        if (!drawHandles) return;

        foreach (var handle in StageControl.TextAreaResizeHandles())
        {
            DrawHandle(
                WorldToVector(stage, stage.GetTextAreaHandleWorldPoint(objectIndex, handle)),
                border,
                9);
        }
    }
}
