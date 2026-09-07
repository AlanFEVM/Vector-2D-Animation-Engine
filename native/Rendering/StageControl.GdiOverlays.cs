using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace VectorAnimationEngine;

internal sealed partial class StageControl : Control
{
    private void DrawSelection(Graphics g)
    {
        if ((SelectedObject < 0 || SelectedObject >= Scene.ObjectCount)
            && SelectedObjects.Count == 0
            && !IsValidEditableBezierHit(_hoveredLineElement)
            && !DrawingObjectSelectionVisible)
        {
            return;
        }
        var oldMode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        if (_selectedElements.Length > 0)
        {
            var previewState = g.Save();
            try
            {
                if (SelectionDragPreviewActive)
                {
                    var offset = SelectionDragPreviewOffset;
                    var screenScale = VectorUnits.PixelsPerUnit * Zoom;
                    g.TranslateTransform(offset.X * screenScale, offset.Y * screenScale);
                }
                foreach (var objectIndex in _selectedElements
                             .Where(hit => (uint)hit.Key.ObjectIndex < Scene.ObjectCount
                                 && hit.Key.Kind == DrawingElementKind.Stroke
                                 && Scene.ShapeKind[hit.Key.ObjectIndex] == ShapeKind.Line
                                 && Scene.IsObjectActive(hit.Key.ObjectIndex, Frame))
                             .Select(hit => hit.Key.ObjectIndex)
                             .Distinct()
                             .Take(MaxSelectionOutlines))
                {
                    DrawBezierSelectionContext(g, objectIndex);
                }

                var drawnElements = 0;
                foreach (var hit in _selectedElements)
                {
                    if ((uint)hit.Key.ObjectIndex >= Scene.ObjectCount
                        || hit.Key == SelectedElement.Key
                        || SuppressFillEdgeBezierSelectionOutline(hit.Key.ObjectIndex)
                        || !Scene.IsObjectActive(hit.Key.ObjectIndex, Frame))
                    {
                        continue;
                    }

                    DrawElementSelectionOutline(g, hit, primary: false);
                    drawnElements++;
                    if (drawnElements >= MaxSelectionOutlines) break;
                }

                if (SelectedElement.IsValid
                    && (uint)SelectedElement.Key.ObjectIndex < Scene.ObjectCount
                    && !SuppressFillEdgeBezierSelectionOutline(SelectedElement.Key.ObjectIndex)
                    && Scene.IsObjectActive(SelectedElement.Key.ObjectIndex, Frame))
                {
                    DrawElementSelectionOutline(g, SelectedElement, primary: true);
                    if (PenPathHandlesVisible && !(TransformMode || DistortMode))
                    {
                        foreach (var objectIndex in _selectedElements
                                     .Where(hit => hit.Key.Kind == DrawingElementKind.Stroke
                                         && (uint)hit.Key.ObjectIndex < Scene.ObjectCount
                                         && Scene.ShapeKind[hit.Key.ObjectIndex] == ShapeKind.Line
                                         && Scene.IsObjectActive(hit.Key.ObjectIndex, Frame))
                                     .Select(hit => hit.Key.ObjectIndex)
                                     .Distinct()
                                     .Take(MaxSelectionOutlines))
                        {
                            DrawBezierHandles(g, objectIndex);
                        }
                    }
                    else if (IsValidEditableBezierHit(SelectedElement))
                    {
                        if (!(TransformMode || DistortMode)) DrawBezierHandles(g, SelectedElement);
                    }
                }
            }
            finally
            {
                g.Restore(previewState);
            }

            DrawDrawingObjectSelectionOverlay(g);
            DrawTransformOverlay(g);
            DrawDistortOverlay(g);
            DrawHoveredLineControls(g);
            g.SmoothingMode = oldMode;
            return;
        }

        var drawn = 0;
        foreach (var index in SelectedObjects)
        {
            if (index == SelectedObject || index < 0 || index >= Scene.ObjectCount) continue;
            if (SuppressFillEdgeBezierSelectionOutline(index)) continue;
            if (!Scene.IsObjectActive(index, Frame)) continue;
            DrawSelectionOutline(g, index, primary: false);
            drawn++;
            if (drawn >= MaxSelectionOutlines) break;
        }

        var primary = SelectedObject;
        if (primary >= 0
            && primary < Scene.ObjectCount
            && !SuppressFillEdgeBezierSelectionOutline(primary)
            && Scene.IsObjectActive(primary, Frame))
        {
            DrawSelectionOutline(g, primary, primary: true);
        }

        DrawDrawingObjectSelectionOverlay(g);
        DrawTransformOverlay(g);
        DrawDistortOverlay(g);
        DrawHoveredLineControls(g);
        g.SmoothingMode = oldMode;
    }

    private void DrawSelectionOutline(Graphics g, int i, bool primary)
    {
        var shape = Scene.ShapeKind.Length > i ? Scene.ShapeKind[i] : ShapeKind.Rectangle;

        if (TryGetObjectDistortionsForRendering(Scene, i, out var distortions))
        {
            if (TryGetDistortedVectorGeometryForRendering(Scene, i, distortions, out var geometry))
            {
                using var path = new GraphicsPath(FillMode.Alternate);
                AppendDistortedVectorGeometry(path, geometry);
                if (path.PointCount > 0)
                {
                    DrawSelectionPath(
                        g,
                        path,
                        primary,
                        SelectionHighlightForShape(shape),
                        Scene.GetLineEndpointStyle(i, startEndpoint: true),
                        Scene.GetLineEndpointStyle(i, startEndpoint: false));
                }
                return;
            }
            foreach (var contour in GetObjectBoundaryContoursForRendering(Scene, i))
            {
                var points = contour.Select(WorldToScreen).ToArray();
                if (shape != ShapeKind.Line && points.Length >= 3)
                {
                    DrawSelectionPolygon(g, points, primary, SelectionHighlightKind.Fill);
                }
                else if (points.Length == 1) DrawSelectionDot(g, points[0], primary, SelectionHighlightKind.Fill);
                else if (points.Length > 1)
                {
                    DrawSelectionPolyline(g, points, primary, SelectionHighlightForShape(shape));
                }
            }
            return;
        }

        if (shape == ShapeKind.Text)
        {
            if (TextAreaOverlayVisible(i))
            {
                DrawTextAreaOverlay(g, i, primary && TextAreaResizeHandlesVisible(i));
            }
            return;
        }

        if (shape == ShapeKind.Line)
        {
            if (primary && !(TransformMode || DistortMode)) DrawBezierGuides(g, i);
            else DrawBezierOutline(g, i, primary);
        }
        else if (shape == ShapeKind.MixingStroke)
        {
            foreach (var points in GetMixingStrokeScreenContours(Scene, i))
            {
                if (points.Length == 1)
                {
                    DrawSelectionDot(g, points[0], primary, SelectionHighlightKind.Fill);
                }
                else if (points.Length > 1)
                {
                    DrawSelectionPolyline(g, points, primary, SelectionHighlightKind.Fill);
                }
            }
        }
        else if (IsFreehandShape(shape))
        {
            var points = GetBoundaryScreenPolyline(i);
            var highlightKind = SelectionHighlightForShape(shape);
            if (points.Length == 1)
            {
                DrawSelectionDot(g, points[0], primary, highlightKind);
            }
            else if (points.Length > 1)
            {
                DrawSelectionPolyline(g, points, primary, highlightKind);
            }
        }
        else
        {
            if (shape == ShapeKind.Path)
            {
                using var path = CreateObjectBoundaryPath(Scene, i);
                if (path.PointCount > 0)
                {
                    DrawSelectionPath(g, path, primary, SelectionHighlightKind.Fill);
                }
            }
            else if (TryGetSelectionWorldContours(i, shape, out var contours))
            {
                foreach (var contour in contours)
                {
                    var points = contour.Select(WorldToScreen).ToArray();
                    if (points.Length >= 3) DrawSelectionPolygon(g, points, primary, SelectionHighlightKind.Fill);
                }
            }
            else
            {
                DrawBoundaryOutline(g, i, primary, SelectionHighlightForShape(shape));
            }

            if (primary && shape is not ShapeKind.Path and not ShapeKind.Text && !(TransformMode || DistortMode)) DrawBoundaryHandles(g, i);
        }
    }

    private bool TryGetSelectionWorldContours(int objectIndex, ShapeKind shape, out PointF[][] contours)
    {
        if (shape == ShapeKind.Text) return Scene.TryGetTextWorldContours(objectIndex, out contours);
        contours = Array.Empty<PointF[]>();
        return false;
    }

    private void DrawElementSelectionOutline(Graphics g, DrawingElementHit hit, bool primary)
    {
        var objectIndex = hit.Key.ObjectIndex;
        var shape = Scene.ShapeKind[objectIndex];
        if (hit.Key.Kind == DrawingElementKind.BoundaryStroke
            && DrawPresentedEditableBezierSelection(g, hit, primary))
        {
            return;
        }

        if (TryGetObjectDistortionsForRendering(Scene, objectIndex, out var distortions))
        {
            if (TryGetDistortedVectorGeometryForRendering(Scene, objectIndex, distortions, out var geometry))
            {
                using var path = new GraphicsPath(FillMode.Alternate);
                AppendDistortedVectorGeometry(path, geometry);
                if (path.PointCount > 0)
                {
                    DrawSelectionPath(
                        g,
                        path,
                        primary,
                        SelectionHighlightForShape(shape),
                        Scene.GetLineEndpointStyle(objectIndex, startEndpoint: true),
                        Scene.GetLineEndpointStyle(objectIndex, startEndpoint: false));
                }
                return;
            }
            foreach (var contour in GetObjectBoundaryContoursForRendering(Scene, objectIndex))
            {
                var points = contour.Select(WorldToScreen).ToArray();
                if (shape != ShapeKind.Line && points.Length >= 3)
                {
                    DrawSelectionPolygon(g, points, primary, SelectionHighlightKind.Fill);
                }
                else if (points.Length == 1) DrawSelectionDot(g, points[0], primary, SelectionHighlightKind.Fill);
                else if (points.Length > 1)
                {
                    DrawSelectionPolyline(g, points, primary, SelectionHighlightForShape(shape));
                }
            }
            return;
        }

        if (hit.Key.Kind == DrawingElementKind.Stroke)
        {
            if (shape == ShapeKind.Line)
            {
                DrawBezierGuides(g, objectIndex, hit.StartT, hit.EndT, primary);
                return;
            }

            var points = GetSelectedStrokePartPoints(hit).Select(WorldToScreen).ToArray();
            if (points.Length == 1) DrawSelectionDot(g, points[0], primary, SelectionHighlightKind.Stroke);
            else if (points.Length > 1) DrawSelectionPolyline(g, points, primary, SelectionHighlightKind.Stroke);
            return;
        }

        if (hit.Key.Kind == DrawingElementKind.Fill)
        {
            foreach (var contour in GetSelectedFillPartContours(hit))
            {
                var points = contour.Select(WorldToScreen).ToArray();
                if (points.Length >= 3) DrawSelectionPolygon(g, points, primary, SelectionHighlightKind.Fill);
            }

            return;
        }

        if (hit.Key.Kind == DrawingElementKind.BoundaryStroke)
        {
            var points = GetSelectedBoundaryPartPoints(hit).Select(WorldToScreen).ToArray();
            if (points.Length > 1) DrawSelectionPolyline(g, points, primary, SelectionHighlightKind.Stroke);
        }
    }

    private bool DrawPresentedEditableBezierSelection(Graphics graphics, DrawingElementHit hit, bool primary)
    {
        var segments = GetPresentedEditableBezierWorldSegments(hit);
        if (segments.Length == 0) return false;

        using var path = new GraphicsPath();
        foreach (var segment in segments)
        {
            path.StartFigure();
            path.AddBezier(
                WorldToScreen(segment.Start),
                WorldToScreen(segment.Control1),
                WorldToScreen(segment.Control2),
                WorldToScreen(segment.End));
        }

        if (path.PointCount == 0) return false;
        Scene.TryGetLinePartEndpointStyles(
            hit.Key,
            Frame,
            out var startStyle,
            out var endStyle);
        DrawSelectionPath(
            graphics,
            path,
            primary,
            SelectionHighlightKind.Stroke,
            startStyle,
            endStyle);
        return true;
    }

    private void DrawFillEdgeBezierOverlay(Graphics graphics)
    {
        if (!FillEdgeBezierOverlayVisible) return;

        var oldMode = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var edge = new Pen(Color.Lime, 1.5f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        using var guide = new Pen(Color.Lime, 1f);
        using var handleCore = new SolidBrush(BackColor);
        using var path = new GraphicsPath(FillMode.Alternate);
        var presented = PresentedFillEdgeBezierOverlaySegments();
        foreach (var source in presented)
        {
            var segment = TranslatedFillEdgeBezierOverlaySegment(source);
            if (!Finite(segment)) continue;
            path.StartFigure();
            path.AddBezier(
                WorldToScreen(segment.Start),
                WorldToScreen(segment.Control1),
                WorldToScreen(segment.Control2),
                WorldToScreen(segment.End));
        }

        if (path.PointCount > 0) graphics.DrawPath(edge, path);
        foreach (var source in presented)
        {
            var segment = TranslatedFillEdgeBezierOverlaySegment(source);
            if (segment.PartIndex == _fillEdgeBezierOverlayActivePartIndex || !Finite(segment)) continue;
            DrawFillEdgeBezierSegmentHandles(graphics, segment, guide, handleCore, edge);
        }

        if (_fillEdgeBezierOverlayActivePartIndex >= 0)
        {
            foreach (var source in presented)
            {
                if (source.PartIndex != _fillEdgeBezierOverlayActivePartIndex) continue;
                var active = TranslatedFillEdgeBezierOverlaySegment(source);
                if (Finite(active))
                {
                    DrawFillEdgeBezierSegmentHandles(graphics, active, guide, handleCore, edge);
                }
            }
        }

        graphics.SmoothingMode = oldMode;
    }

    private void DrawFillEdgeBezierSegmentHandles(
        Graphics graphics,
        FillEdgeBezierOverlaySegment segment,
        Pen guide,
        Brush handleCore,
        Pen edge)
    {
        var start = WorldToScreen(segment.Start);
        var control1 = WorldToScreen(segment.Control1);
        var control2 = WorldToScreen(segment.Control2);
        var end = WorldToScreen(segment.End);
        graphics.DrawLine(guide, start, control1);
        graphics.DrawLine(guide, end, control2);
        DrawFillEdgeBezierAnchor(graphics, start, handleCore, edge);
        DrawFillEdgeBezierAnchor(graphics, end, handleCore, edge);
        DrawFillEdgeBezierControl(graphics, control1, handleCore, edge);
        DrawFillEdgeBezierControl(graphics, control2, handleCore, edge);
    }

    private static void DrawFillEdgeBezierAnchor(Graphics graphics, PointF point, Brush core, Pen edge)
    {
        const float radius = 3.5f;
        graphics.FillRectangle(core, point.X - radius, point.Y - radius, radius * 2, radius * 2);
        graphics.DrawRectangle(edge, point.X - radius, point.Y - radius, radius * 2, radius * 2);
    }

    private static void DrawFillEdgeBezierControl(Graphics graphics, PointF point, Brush core, Pen edge)
    {
        const float radius = 4f;
        graphics.FillEllipse(core, point.X - radius, point.Y - radius, radius * 2, radius * 2);
        graphics.DrawEllipse(edge, point.X - radius, point.Y - radius, radius * 2, radius * 2);
    }

    private void DrawPenAnchorGuides(Graphics graphics)
    {
        if (!PenAnchorGuidesVisible) return;
        var point = WorldToScreen(PenAnchorGuidePoint);
        var oldMode = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var guidePen = new Pen(Color.FromArgb(205, 70, 210, 235), 1f) { DashStyle = DashStyle.Dash };
        using var markerPen = new Pen(Color.FromArgb(245, 128, 239, 255), 1.5f);
        using var markerFill = new SolidBrush(Color.FromArgb(210, 18, 48, 55));
        if (PenAnchorGuideVertical) graphics.DrawLine(guidePen, point.X, 0, point.X, Height);
        if (PenAnchorGuideHorizontal) graphics.DrawLine(guidePen, 0, point.Y, Width, point.Y);

        if (PenAnchorGuideInsertion)
        {
            var diamond = new[]
            {
                new PointF(point.X, point.Y - 7),
                new PointF(point.X + 7, point.Y),
                new PointF(point.X, point.Y + 7),
                new PointF(point.X - 7, point.Y)
            };
            graphics.FillPolygon(markerFill, diamond);
            graphics.DrawPolygon(markerPen, diamond);
            graphics.DrawLine(markerPen, point.X - 3, point.Y, point.X + 3, point.Y);
            graphics.DrawLine(markerPen, point.X, point.Y - 3, point.X, point.Y + 3);
        }
        else if (PenAnchorGuideSnapped)
        {
            graphics.FillEllipse(markerFill, point.X - 6, point.Y - 6, 12, 12);
            graphics.DrawEllipse(markerPen, point.X - 6, point.Y - 6, 12, 12);
            graphics.DrawLine(markerPen, point.X - 8, point.Y, point.X + 8, point.Y);
            graphics.DrawLine(markerPen, point.X, point.Y - 8, point.X, point.Y + 8);
        }
        else
        {
            graphics.FillEllipse(markerFill, point.X - 3, point.Y - 3, 6, 6);
            graphics.DrawEllipse(markerPen, point.X - 3, point.Y - 3, 6, 6);
        }

        graphics.SmoothingMode = oldMode;
    }

    private void DrawDrawingPreview(Graphics g)
    {
        if (!DrawingPreviewVisible) return;

        var start = DrawingPreviewStart;
        var end = DrawingPreviewEnd;
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        if (Math.Abs(dx) + Math.Abs(dy) < 0.001f) return;

        var oldMode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var fillColor = Color.FromArgb(72, DrawingPreviewColor);
        var strokeColor = Color.FromArgb(230, DrawingPreviewColor);
        using var fill = new SolidBrush(fillColor);
        using var stroke = new Pen(strokeColor, Math.Max(0.1f, WorldLengthToScreen(DrawingPreviewStroke)))
        {
            DashStyle = DashStyle.Solid,
            LineJoin = LineJoin.Round,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };

        if (DrawingPreviewShape == ShapeKind.Line)
        {
            var a = WorldToScreen(start);
            var b = WorldToScreen(end);
            if (DrawingPreviewHasCurve)
            {
                var segments = DrawingPreviewCurveSegments.Count > 0
                    ? DrawingPreviewCurveSegments
                    : [new CubicDrawingPreviewSegment(start, DrawingPreviewControl, DrawingPreviewControl2, end)];
                foreach (var segment in segments)
                {
                    var segmentStart = WorldToScreen(segment.Start);
                    var segmentControl1 = WorldToScreen(segment.Control1);
                    var segmentControl2 = WorldToScreen(segment.Control2);
                    var segmentEnd = WorldToScreen(segment.End);
                    using var path = BuildCubicPath(segmentStart, segmentControl1, segmentControl2, segmentEnd);
                    g.DrawPath(stroke, path);
                }
                var control1 = WorldToScreen(DrawingPreviewControl);
                var control2 = WorldToScreen(DrawingPreviewControl2);
                if (segments.Count == 1)
                {
                    g.DrawLine(_previewGuidePen, a, control1);
                    g.DrawLine(_previewGuidePen, b, control2);
                }
                DrawHandle(g, a, _handleBrush, 7);
                DrawHandle(g, b, _handleBrush, 7);
                if (segments.Count == 1)
                {
                    DrawHandle(g, control1, _bezierHandleBrush, 9);
                    DrawHandle(g, control2, _bezierHandleBrush, 9);
                }
                g.SmoothingMode = oldMode;
                return;
            }

            g.DrawLine(stroke, a, b);
            DrawHandle(g, a, _handleBrush, 7);
            DrawHandle(g, b, _handleBrush, 7);
            g.SmoothingMode = oldMode;
            return;
        }

        var center = WorldToScreen((start.X + end.X) * 0.5f, (start.Y + end.Y) * 0.5f);
        var w = Math.Max(2, WorldLengthToScreen(Math.Abs(dx)));
        var h = Math.Max(2, WorldLengthToScreen(Math.Abs(dy)));
        var state = g.Save();
        g.TranslateTransform(center.X, center.Y);
        DrawPreviewLocalShape(g, DrawingPreviewShape, DrawingPreviewShapeVertexCount, fill, stroke, w, h);
        g.Restore(state);

        using var boundsPen = new Pen(Color.FromArgb(180, 255, 255, 255), 1) { DashStyle = DashStyle.Dash };
        g.DrawRectangle(boundsPen, center.X - w * 0.5f, center.Y - h * 0.5f, w, h);
        g.SmoothingMode = oldMode;
    }

    private void DrawPenDirectionHandles(Graphics graphics)
    {
        if (!PenDirectionHandlesVisible) return;
        var anchor = WorldToScreen(PenDirectionAnchor);
        var oldMode = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var guide = new Pen(Color.FromArgb(220, 112, 204, 255), 1f);
        using var pointFill = new SolidBrush(Color.FromArgb(245, 18, 48, 55));
        using var pointBorder = new Pen(Color.FromArgb(250, 146, 224, 255), 1.4f);
        DrawDirectionPoint(PenDirectionIncoming);
        DrawDirectionPoint(PenDirectionOutgoing);
        graphics.FillRectangle(pointFill, anchor.X - 3.5f, anchor.Y - 3.5f, 7, 7);
        graphics.DrawRectangle(pointBorder, anchor.X - 3.5f, anchor.Y - 3.5f, 7, 7);
        graphics.SmoothingMode = oldMode;

        void DrawDirectionPoint(PointF? world)
        {
            if (world is not { } point) return;
            var screen = WorldToScreen(point);
            graphics.DrawLine(guide, anchor, screen);
            graphics.FillEllipse(pointFill, screen.X - 3.5f, screen.Y - 3.5f, 7, 7);
            graphics.DrawEllipse(pointBorder, screen.X - 3.5f, screen.Y - 3.5f, 7, 7);
        }
    }

    private void DrawFreehandPreview(Graphics g)
    {
        if (!FreehandPreviewVisible || FreehandPreviewPoints.Count == 0) return;
        var screenWidth = Math.Max(0.75f, WorldLengthToScreen(FreehandPreviewStroke));
        var variableWidth = FreehandPreviewDiameters.Count == FreehandPreviewPoints.Count;
        var oldMode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (FreehandPreviewBrushShape is { IsTraditionalBrush: true, IsRadiallySymmetric: false } tipShape)
        {
            DrawTraditionalBrushPreview(g, tipShape, screenWidth, variableWidth);
            g.SmoothingMode = oldMode;
            return;
        }

        if (FreehandPreviewPoints.Count == 1)
        {
            var point = WorldToScreen(FreehandPreviewPoints[0]);
            if (variableWidth) screenWidth = Math.Max(0.75f, WorldLengthToScreen(FreehandPreviewDiameters[0]));
            using var dot = new SolidBrush(FreehandPreviewColor);
            g.FillEllipse(dot, point.X - screenWidth * 0.5f, point.Y - screenWidth * 0.5f, screenWidth, screenWidth);
        }
        else if (variableWidth)
        {
            using var dot = new SolidBrush(FreehandPreviewColor);
            var previous = WorldToScreen(FreehandPreviewPoints[0]);
            var previousWidth = Math.Max(0.75f, WorldLengthToScreen(FreehandPreviewDiameters[0]));
            g.FillEllipse(dot, previous.X - previousWidth * 0.5f, previous.Y - previousWidth * 0.5f, previousWidth, previousWidth);
            for (var i = 1; i < FreehandPreviewPoints.Count; i++)
            {
                var current = WorldToScreen(FreehandPreviewPoints[i]);
                var currentWidth = Math.Max(0.75f, WorldLengthToScreen(FreehandPreviewDiameters[i]));
                using var pen = StrokePen(FreehandPreviewColor, (previousWidth + currentWidth) * 0.5f);
                g.DrawLine(pen, previous, current);
                g.FillEllipse(dot, current.X - currentWidth * 0.5f, current.Y - currentWidth * 0.5f, currentWidth, currentWidth);
                previous = current;
                previousWidth = currentWidth;
            }
        }
        else
        {
            using var pen = StrokePen(FreehandPreviewColor, screenWidth);
            var previous = WorldToScreen(FreehandPreviewPoints[0]);
            for (var i = 1; i < FreehandPreviewPoints.Count; i++)
            {
                var current = WorldToScreen(FreehandPreviewPoints[i]);
                g.DrawLine(pen, previous, current);
                previous = current;
            }
        }

        g.SmoothingMode = oldMode;
    }

    private void DrawTraditionalBrushPreview(Graphics g, BrushShape tipShape, float defaultWidth, bool variableWidth)
    {
        var contour = tipShape.NormalizedContour(0.5f);
        if (contour.Length < 3) return;

        using var fill = new SolidBrush(FreehandPreviewColor);
        for (var index = 0; index < FreehandPreviewPoints.Count; index++)
        {
            var diameter = variableWidth
                ? Math.Max(0.75f, WorldLengthToScreen(FreehandPreviewDiameters[index]))
                : defaultWidth;
            var center = WorldToScreen(FreehandPreviewPoints[index]);
            var radius = diameter * 0.5f;
            var points = new PointF[contour.Length];
            for (var pointIndex = 0; pointIndex < contour.Length; pointIndex++)
            {
                points[pointIndex] = new PointF(
                    center.X + contour[pointIndex].X * radius,
                    center.Y + contour[pointIndex].Y * radius);
            }

            g.FillPolygon(fill, points);
        }
    }

    private void DrawBrushTipCursor(Graphics g)
    {
        if (!BrushTipCursorVisible || BrushTipCursorShape is null) return;
        var oldMode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var outlineColor = BrushTipCursorIsEraser
            ? Color.FromArgb(235, 255, 120, 120)
            : Color.FromArgb(235, 112, 204, 255);
        foreach (var layer in BrushTipCursorShape.Layers)
        {
            var points = BrushTipCursorShape.NormalizedContour(layer.Threshold)
                .Select(point => new PointF(
                    BrushTipCursorScreen.X + point.X * BrushTipCursorRadiusPixels,
                    BrushTipCursorScreen.Y + point.Y * BrushTipCursorRadiusPixels))
                .ToArray();
            if (points.Length < 3) continue;
            using var fill = new SolidBrush(Color.FromArgb(
                (int)Math.Clamp(42 * layer.Opacity / 0.66f, 8, 42),
                outlineColor));
            using var pen = new Pen(Color.FromArgb(
                (int)Math.Clamp(190 * layer.Opacity / 0.66f, 48, 190),
                outlineColor),
                layer.Threshold >= 0.7f ? 1.4f : 1f);
            g.FillPolygon(fill, points);
            g.DrawPolygon(pen, points);
        }

        g.SmoothingMode = oldMode;
    }

    private void DrawBrushColorPalette(Graphics g)
    {
        if (!BrushColorPaletteVisible) return;
        var oldMode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var center = BrushColorPaletteCenter;
        var radius = BrushColorPaletteRadius + BrushColorPaletteSwatchSize * 0.72f;
        using (var backdrop = new SolidBrush(Color.FromArgb(214, Theme.Top)))
        {
            g.FillEllipse(backdrop, center.X - radius, center.Y - radius, radius * 2, radius * 2);
        }

        using (var backdropOutline = new Pen(Color.FromArgb(225, Theme.BorderHover), 1f))
        {
            g.DrawEllipse(backdropOutline, center.X - radius, center.Y - radius, radius * 2, radius * 2);
        }

        for (var index = 0; index < _brushColorPaletteColors.Length; index++)
        {
            var bounds = BrushColorPaletteSwatchBounds(index);
            using var fill = new SolidBrush(_brushColorPaletteColors[index]);
            using var outline = new Pen(
                index == _brushColorPaletteHoveredIndex
                    ? Color.FromArgb(255, 255, 240, 168)
                    : Color.FromArgb(235, Theme.BorderHover),
                index == _brushColorPaletteHoveredIndex ? 2.4f : 1f);
            g.FillRectangle(fill, bounds);
            g.DrawRectangle(outline, bounds.X, bounds.Y, Math.Max(0, bounds.Width - 1), Math.Max(0, bounds.Height - 1));
        }

        using var pointer = new SolidBrush(Color.FromArgb(230, Theme.Text));
        g.FillEllipse(pointer, center.X - 3, center.Y - 3, 6, 6);
        g.SmoothingMode = oldMode;
    }

    private void DrawFillAnimation(Graphics g)
    {
        if (!FillAnimationVisible) return;

        var oldMode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = new GraphicsPath(FillMode.Alternate);
        foreach (var contour in _fillAnimationContours)
        {
            var points = contour.Select(point => WorldToScreen(point.X, point.Y)).ToArray();
            if (points.Length >= 3) path.AddPolygon(points);
        }

        if (path.PointCount > 0)
        {
            var origin = WorldToScreen(_fillAnimationOrigin);
            var radius = Math.Max(10f, WorldLengthToScreen(FillAnimationBloomRadiusWorld));
            var fade = FillAnimationFade;
            var glowColor = FillAnimationGlowColor;
            var state = g.Save();
            try
            {
                g.SetClip(path, CombineMode.Intersect);
                DrawFillBloom(g, origin, radius, glowColor, fade);
            }
            finally
            {
                g.Restore(state);
            }
        }
        g.SmoothingMode = oldMode;
    }

    private static void DrawFillBloom(Graphics g, PointF origin, float radius, Color color, float fade)
    {
        DrawFillBloomLayer(g, origin, radius, color, 20f * fade);
        DrawFillBloomLayer(g, origin, radius * 0.72f, color, 28f * fade);
        DrawFillBloomLayer(g, origin, radius * 0.38f, color, 38f * fade);

        var waveWidth = Math.Clamp(radius * 0.025f, 2f, 12f);
        var waveAlpha = (int)Math.Clamp(210f * fade, 0f, 210f);
        using var wave = new Pen(Color.FromArgb(waveAlpha, color), waveWidth);
        g.DrawEllipse(wave, origin.X - radius, origin.Y - radius, radius * 2f, radius * 2f);
    }

    private static void DrawFillBloomLayer(Graphics g, PointF origin, float radius, Color color, float alpha)
    {
        if (radius <= 0.5f || alpha <= 0.5f) return;
        using var brush = new SolidBrush(Color.FromArgb((int)Math.Clamp(alpha, 0f, 255f), color));
        g.FillEllipse(brush, origin.X - radius, origin.Y - radius, radius * 2f, radius * 2f);
    }

    private static float SmoothStep(float value)
    {
        value = Math.Clamp(value, 0f, 1f);
        return value * value * (3f - 2f * value);
    }

    private static Color LightenForFillAnimation(Color color)
    {
        return Color.FromArgb(
            color.A,
            (int)Math.Round(color.R + (255 - color.R) * 0.38f),
            (int)Math.Round(color.G + (255 - color.G) * 0.38f),
            (int)Math.Round(color.B + (255 - color.B) * 0.38f));
    }

    private void DrawFillPreview(Graphics g)
    {
        if (!FillPreviewVisible) return;

        var oldMode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = new GraphicsPath(FillMode.Alternate);
        foreach (var contour in _fillPreviewContours)
        {
            var points = contour.Select(point => WorldToScreen(point.X, point.Y)).ToArray();
            if (points.Length >= 3) path.AddPolygon(points);
        }

        if (path.PointCount > 0)
        {
            using var fill = new SolidBrush(Color.FromArgb(72, _fillPreviewColor));
            using var outline = new Pen(Color.FromArgb(150, _fillPreviewColor), 1f) { DashStyle = DashStyle.Dash };
            g.FillPath(fill, path);
            g.DrawPath(outline, path);
        }

        g.SmoothingMode = oldMode;
    }

    private void DrawFillToolCursor(Graphics g)
    {
        if (!_fillToolCursorVisible) return;

        var oldMode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var x = _fillToolCursorScreen.X;
        var y = _fillToolCursorScreen.Y;
        var bucket = new[]
        {
            new PointF(x - 10, y - 12),
            new PointF(x + 3, y - 12),
            new PointF(x + 10, y - 5),
            new PointF(x - 3, y - 5)
        };
        using var fill = new SolidBrush(Color.FromArgb(150, _fillToolCursorColor));
        using var outline = new Pen(Color.FromArgb(235, Theme.Text), 1.4f);
        using var drop = new SolidBrush(Color.FromArgb(220, _fillToolCursorColor));
        using var dropOutline = new Pen(Color.FromArgb(235, Theme.Text), 1f);
        g.FillPolygon(fill, bucket);
        g.DrawPolygon(outline, bucket);
        g.FillEllipse(drop, x + 3, y - 1, 6, 8);
        g.DrawEllipse(dropOutline, x + 3, y - 1, 6, 8);
        g.SmoothingMode = oldMode;
    }

    private void DrawGradientOverlay(Graphics g)
    {
        if (!_gradientOverlayVisible) return;
        var start = WorldToScreen(_gradientOverlayStart);
        var end = WorldToScreen(_gradientOverlayEnd);
        var oldMode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var guide = new Pen(Color.FromArgb(230, 255, 244, 166), 1.5f) { DashStyle = DashStyle.Dash };
        using var startFill = new SolidBrush(_gradientOverlayStartColor);
        using var endFill = new SolidBrush(_gradientOverlayEndColor);
        using var outline = new Pen(Color.FromArgb(245, 16, 18, 22), 1.4f);
        if (_gradientOverlayKind == GradientKind.Radial)
        {
            var radius = Distance(start, end);
            using var radialGuide = new Pen(Color.FromArgb(190, 255, 244, 166), 1.25f) { DashStyle = DashStyle.Dash };
            g.DrawEllipse(radialGuide, start.X - radius, start.Y - radius, radius * 2, radius * 2);
        }
        g.DrawLine(guide, start, end);
        g.FillEllipse(startFill, start.X - 6, start.Y - 6, 12, 12);
        g.DrawEllipse(outline, start.X - 6, start.Y - 6, 12, 12);
        if (_gradientOverlayKind == GradientKind.ShapeRadial)
        {
            var edgeMarker = new[]
            {
                new PointF(end.X, end.Y - 4),
                new PointF(end.X + 4, end.Y),
                new PointF(end.X, end.Y + 4),
                new PointF(end.X - 4, end.Y)
            };
            g.FillPolygon(endFill, edgeMarker);
            g.DrawPolygon(outline, edgeMarker);
        }
        else
        {
            g.FillEllipse(endFill, end.X - 6, end.Y - 6, 12, 12);
            g.DrawEllipse(outline, end.X - 6, end.Y - 6, 12, 12);
        }
        for (var index = 1; index < _gradientOverlayStops.Length - 1; index++)
        {
            var point = Lerp(start, end, _gradientOverlayStops[index].Position);
            var diamond = new[]
            {
                new PointF(point.X, point.Y - 6),
                new PointF(point.X + 6, point.Y),
                new PointF(point.X, point.Y + 6),
                new PointF(point.X - 6, point.Y)
            };
            using var fill = new SolidBrush(Color.FromArgb(_gradientOverlayStops[index].Argb));
            g.FillPolygon(fill, diamond);
            g.DrawPolygon(outline, diamond);
        }
        g.SmoothingMode = oldMode;
    }

    private static float SquaredDistance(Point a, PointF b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    private bool CubicCurveHit(Point screen, FillEdgeBezierOverlaySegment segment, float radiusPixels)
    {
        if (!Finite(segment)) return false;
        var start = WorldToScreen(segment.Start);
        var control1 = WorldToScreen(segment.Control1);
        var control2 = WorldToScreen(segment.Control2);
        var end = WorldToScreen(segment.End);
        var controlLength = Distance(start, control1) + Distance(control1, control2) + Distance(control2, end);
        var steps = Math.Clamp((int)MathF.Ceiling(controlLength / 8f), 8, 64);
        var point = new PointF(screen.X, screen.Y);
        var previous = start;
        var radiusSquared = radiusPixels * radiusPixels;
        for (var step = 1; step <= steps; step++)
        {
            var current = CubicPoint(start, control1, control2, end, step / (float)steps);
            if (SquaredDistanceToSegment(point, previous, current) <= radiusSquared) return true;
            previous = current;
        }

        return false;
    }

    private static float SquaredDistanceToSegment(PointF point, PointF start, PointF end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var lengthSquared = dx * dx + dy * dy;
        if (lengthSquared <= float.Epsilon)
        {
            dx = point.X - start.X;
            dy = point.Y - start.Y;
            return dx * dx + dy * dy;
        }

        var amount = Math.Clamp(((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared, 0f, 1f);
        var closestX = start.X + dx * amount;
        var closestY = start.Y + dy * amount;
        dx = point.X - closestX;
        dy = point.Y - closestY;
        return dx * dx + dy * dy;
    }

    private static bool Finite(FillEdgeBezierOverlaySegment segment)
    {
        return Finite(segment.Start)
            && Finite(segment.Control1)
            && Finite(segment.Control2)
            && Finite(segment.End);
    }

    private static bool Finite(PointF point) => float.IsFinite(point.X) && float.IsFinite(point.Y);

    private void DrawMarquee(Graphics g)
    {
        DrawLassoPreview(g);
        if (!MarqueeVisible || MarqueeOverlayActive) return;
        var rect = Rectangle.FromLTRB(
            Math.Min(MarqueeStart.X, MarqueeEnd.X),
            Math.Min(MarqueeStart.Y, MarqueeEnd.Y),
            Math.Max(MarqueeStart.X, MarqueeEnd.X),
            Math.Max(MarqueeStart.Y, MarqueeEnd.Y));

        if (rect.Width < 2 || rect.Height < 2) return;
        g.FillRectangle(_marqueeBrush, rect);
        g.DrawRectangle(_marqueePen, rect);
    }

    private void DrawPreviewLocalShape(Graphics g, ShapeKind shape, int shapeVertexCount, Brush fill, Pen stroke, float w, float h)
    {
        var rect = new RectangleF(-w * 0.5f, -h * 0.5f, w, h);
        switch (shape)
        {
            case ShapeKind.Ellipse:
                g.FillEllipse(fill, rect);
                g.DrawEllipse(stroke, rect);
                break;
            case ShapeKind.Triangle:
                DrawPreviewPolygon(g, fill, stroke, RegularPolygonPoints(3, w, h, -MathF.PI / 2));
                break;
            case ShapeKind.Polygon:
                DrawPreviewPolygon(g, fill, stroke, RegularPolygonPoints(PolygonVertexCount(shapeVertexCount), w, h, -MathF.PI / 2));
                break;
            case ShapeKind.Star:
                DrawPreviewPolygon(g, fill, stroke, StarPoints(StarVertexCount(shapeVertexCount), w, h, -MathF.PI / 2));
                break;
            default:
                g.FillRectangle(fill, rect);
                g.DrawRectangle(stroke, rect.X, rect.Y, rect.Width, rect.Height);
                break;
        }
    }

    private static void DrawPreviewPolygon(Graphics g, Brush fill, Pen stroke, PointF[] points)
    {
        g.FillPolygon(fill, points);
        g.DrawPolygon(stroke, points);
    }

    private void DrawBezierLine(Graphics g, int i, Brush brush, float screenStroke)
    {
        var (start, control1, control2, end) = GetBezierScreenPoints(i);
        var startStyle = Scene.GetLineEndpointStyle(i, startEndpoint: true);
        var endStyle = Scene.GetLineEndpointStyle(i, startEndpoint: false);
        using var linePen = new Pen(brush, screenStroke)
        {
            StartCap = LineCapForEndpoint(startStyle),
            EndCap = LineCapForEndpoint(endStyle),
            LineJoin = startStyle == LineEndpointStyle.Sharp || endStyle == LineEndpointStyle.Sharp ? LineJoin.Miter : LineJoin.Round,
            MiterLimit = startStyle == LineEndpointStyle.Sharp || endStyle == LineEndpointStyle.Sharp ? 8 : 1
        };

        var oldMode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (Scene.IsLineStraight(i)) g.DrawLine(linePen, start, end);
        else
        {
            using var path = BuildCubicPath(start, control1, control2, end);
            g.DrawPath(linePen, path);
        }
        DrawEndpointJoin(g, i, startEndpoint: true, screenStroke, brush);
        DrawEndpointJoin(g, i, startEndpoint: false, screenStroke, brush);
        g.SmoothingMode = oldMode;
    }

    private void DrawGradientBezierLine(Graphics g, VectorScene scene, int objectIndex, float screenStroke)
    {
        if (scene.GetGradientKind(objectIndex) == GradientKind.Radial)
        {
            DrawRadialGradientBezierLine(g, scene, objectIndex, screenStroke);
            return;
        }

        using var gradient = CreateGradientFillBrush(scene, objectIndex);
        DrawBezierLine(g, objectIndex, gradient, screenStroke);
    }

    private void DrawRadialGradientBezierLine(Graphics g, VectorScene scene, int objectIndex, float screenStroke)
    {
        var (start, control1, control2, end) = GetBezierScreenPoints(objectIndex);
        var center = WorldToScreen(scene.GetGradientStart(objectIndex));
        var radiusPoint = WorldToScreen(scene.GetGradientEnd(objectIndex));
        var radius = Math.Max(0.5f, Distance(center, radiusPoint));
        var stops = scene.GetGradientStops(objectIndex);
        var startStyle = scene.GetLineEndpointStyle(objectIndex, startEndpoint: true);
        var endStyle = scene.GetLineEndpointStyle(objectIndex, startEndpoint: false);
        var oldMode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        const int segments = 64;
        for (var segment = 0; segment < segments; segment++)
        {
            var startT = segment / (float)segments;
            var endT = (segment + 1) / (float)segments;
            var a = CubicPoint(start, control1, control2, end, startT);
            var b = CubicPoint(start, control1, control2, end, endT);
            var midpoint = CubicPoint(start, control1, control2, end, (startT + endT) * 0.5f);
            var position = Math.Clamp(Distance(midpoint, center) / radius, 0f, 1f);
            using var pen = new Pen(GradientColorAt(stops, position), screenStroke)
            {
                StartCap = segment == 0 ? LineCapForEndpoint(startStyle) : LineCap.Round,
                EndCap = segment == segments - 1 ? LineCapForEndpoint(endStyle) : LineCap.Round,
                LineJoin = LineJoin.Round
            };
            g.DrawLine(pen, a, b);
        }

        DrawRadialGradientEndpointJoin(g, scene, objectIndex, startEndpoint: true, screenStroke);
        DrawRadialGradientEndpointJoin(g, scene, objectIndex, startEndpoint: false, screenStroke);

        g.SmoothingMode = oldMode;
    }

    private static LineCap LineCapForEndpoint(LineEndpointStyle endpointStyle)
    {
        return endpointStyle == LineEndpointStyle.Sharp ? LineCap.Flat : LineCap.Round;
    }

    private void DrawEndpointJoin(Graphics g, int objectIndex, bool startEndpoint, float screenStroke, Brush brush)
    {
        if (!TryGetEndpointJoins(objectIndex, startEndpoint, screenStroke, out var joint, out var miters)) return;
        foreach (var miter in miters) FillMiterJoin(g, brush, joint, miter);
    }

    private void DrawRadialGradientEndpointJoin(
        Graphics g,
        VectorScene scene,
        int objectIndex,
        bool startEndpoint,
        float screenStroke)
    {
        if (!TryGetEndpointJoins(objectIndex, startEndpoint, screenStroke, out var joint, out var miters)) return;
        using var path = new GraphicsPath(FillMode.Winding);
        foreach (var miter in miters) AddMiterJoinPolygons(path, joint, miter);
        DrawRadialGradientFill(g, path, scene, objectIndex, scene.GetGradientStops(objectIndex));
    }

    private bool TryGetEndpointJoins(
        int objectIndex,
        bool startEndpoint,
        float screenStroke,
        out PointF joint,
        out LineMiterJoin[] miters)
    {
        joint = PointF.Empty;
        miters = Array.Empty<LineMiterJoin>();
        if (Scene.GetLineEndpointStyle(objectIndex, startEndpoint) != LineEndpointStyle.Sharp
            || !Scene.TryGetLineEndpointJunctionForRender(objectIndex, startEndpoint, Frame, out var junction)
            || !junction.AllSharp
            || objectIndex != junction.OwnerObjectIndex)
        {
            return false;
        }

        var current = GetBezierScreenPoints(objectIndex);
        joint = startEndpoint ? current.Start : current.End;
        var interiorPoints = new PointF[junction.Connections.Length + 1];
        interiorPoints[0] = EndpointInteriorPoint(current, startEndpoint);
        for (var index = 0; index < junction.Connections.Length; index++)
        {
            var connection = junction.Connections[index];
            var adjacent = GetBezierScreenPoints(connection.ObjectIndex);
            interiorPoints[index + 1] = EndpointInteriorPoint(adjacent, connection.StartEndpoint);
        }

        miters = LineJoinGeometry.CreateJunctionMiters(joint, interiorPoints, screenStroke * 0.5f);
        return miters.Length > 0;
    }

    private static void FillMiterJoin(Graphics g, Brush brush, PointF joint, LineMiterJoin miter)
    {
        g.FillPolygon(brush, [joint, miter.OuterFirstOffset, miter.OuterMiter, miter.OuterSecondOffset]);
        g.FillPolygon(brush, [joint, miter.InnerFirstOffset, miter.InnerMiter, miter.InnerSecondOffset]);
    }

    private static void AddMiterJoinPolygons(GraphicsPath path, PointF joint, LineMiterJoin miter)
    {
        path.AddPolygon([joint, miter.OuterFirstOffset, miter.OuterMiter, miter.OuterSecondOffset]);
        path.AddPolygon([joint, miter.InnerFirstOffset, miter.InnerMiter, miter.InnerSecondOffset]);
    }

    private static PointF EndpointInteriorPoint(
        (PointF Start, PointF Control1, PointF Control2, PointF End) curve,
        bool startEndpoint)
    {
        var endpoint = startEndpoint ? curve.Start : curve.End;
        var control = startEndpoint ? curve.Control1 : curve.Control2;
        var controlDistanceSquared = (control.X - endpoint.X) * (control.X - endpoint.X)
            + (control.Y - endpoint.Y) * (control.Y - endpoint.Y);
        if (controlDistanceSquared > 0.01f) return control;
        return startEndpoint ? curve.End : curve.Start;
    }

    private void DrawBezierGuides(Graphics g, int i)
    {
        var hit = new DrawingElementHit(
            new DrawingElementKey(i, DrawingElementKind.Stroke, 0),
            0,
            0,
            1);
        var segments = GetPresentedEditableBezierWorldSegments(hit);
        if (segments.Length == 0) return;
        using var path = new GraphicsPath();
        foreach (var segment in segments)
        {
            path.StartFigure();
            path.AddBezier(
                WorldToScreen(segment.Start),
                WorldToScreen(segment.Control1),
                WorldToScreen(segment.Control2),
                WorldToScreen(segment.End));
        }
        DrawSelectionPath(
            g,
            path,
            true,
            SelectionHighlightKind.Stroke,
            Scene.GetLineEndpointStyle(i, startEndpoint: true),
            Scene.GetLineEndpointStyle(i, startEndpoint: false));
        foreach (var segment in segments)
        {
            DrawBezierHandles(
                g,
                WorldToScreen(segment.Start),
                WorldToScreen(segment.Control1),
                WorldToScreen(segment.Control2),
                WorldToScreen(segment.End));
        }
    }

    private void DrawBezierHandles(Graphics g, int i)
    {
        var (start, control1, control2, end) = GetBezierScreenPoints(i);
        DrawBezierHandles(g, start, control1, control2, end);
    }

    private void DrawBezierHandles(Graphics g, DrawingElementHit hit)
    {
        var segments = GetPresentedEditableBezierWorldSegments(hit);
        if (segments.Length == 0) return;
        if ((uint)hit.PresentedBezierSegmentIndex < segments.Length)
        {
            var selected = segments[hit.PresentedBezierSegmentIndex];
            DrawBezierHandles(
                g,
                WorldToScreen(selected.Start),
                WorldToScreen(selected.Control1),
                WorldToScreen(selected.Control2),
                WorldToScreen(selected.End));
            return;
        }

        foreach (var segment in segments)
        {
            DrawBezierHandles(
                g,
                WorldToScreen(segment.Start),
                WorldToScreen(segment.Control1),
                WorldToScreen(segment.Control2),
                WorldToScreen(segment.End));
        }
    }

    private void DrawHoveredLineControls(Graphics g)
    {
        var hit = _hoveredLineElement;
        if (!ShouldDrawHoveredLineControls()) return;

        if (!TryGetEditableBezierWorldPoints(hit, out var start, out var control1, out var control2, out var end)) return;
        var startScreen = WorldToScreen(start);
        var control1Screen = WorldToScreen(control1);
        var control2Screen = WorldToScreen(control2);
        var endScreen = WorldToScreen(end);
        using var guide = new Pen(Color.FromArgb(120, 112, 204, 255), 1);
        using var endpoint = new SolidBrush(Color.FromArgb(210, 255, 240, 168));
        using var controlBrush = new SolidBrush(Color.FromArgb(210, 112, 204, 255));
        g.DrawLine(guide, startScreen, control1Screen);
        g.DrawLine(guide, endScreen, control2Screen);
        DrawHandle(g, startScreen, endpoint, 7);
        DrawHandle(g, endScreen, endpoint, 7);
        DrawHandle(g, control1Screen, controlBrush, 9);
        DrawHandle(g, control2Screen, controlBrush, 9);
    }

    private void DrawBezierHandles(Graphics g, PointF start, PointF control1, PointF control2, PointF end)
    {
        g.DrawLine(_guidePen, start, control1);
        g.DrawLine(_guidePen, end, control2);
        DrawHandle(g, start, _handleBrush, 8);
        DrawHandle(g, end, _handleBrush, 8);
        DrawHandle(g, control1, _bezierHandleBrush, 10);
        DrawHandle(g, control2, _bezierHandleBrush, 10);
    }

    private void DrawTransformOverlay(Graphics g)
    {
        if (!TransformBoundsVisible) return;
        var geometry = GetTransformOverlayScreenGeometry();
        var corners = new[] { geometry.TopLeft, geometry.TopRight, geometry.BottomRight, geometry.BottomLeft };
        using var boundsPen = new Pen(Color.FromArgb(235, 112, 204, 255), 1) { DashStyle = DashStyle.Dash };
        using var rotationPen = new Pen(Color.FromArgb(235, 112, 204, 255), 1);
        using var focusPen = new Pen(Color.FromArgb(245, 104, 255, 188), 1.5f);
        using var focusBrush = new SolidBrush(Color.FromArgb(220, 30, 82, 69));
        g.DrawPolygon(boundsPen, corners);
        foreach (var (_, point, _) in geometry.ResizeHandles) DrawHandle(g, point, _handleBrush, 8);
        foreach (var (_, point, corner) in geometry.RotationHandles)
        {
            g.DrawLine(rotationPen, corner, point);
            g.FillEllipse(_bezierHandleBrush, point.X - 4, point.Y - 4, 8, 8);
            g.DrawEllipse(_handleBorderPen, point.X - 4, point.Y - 4, 8, 8);
        }
        foreach (var (_, point, edge) in geometry.SkewHandles)
        {
            g.DrawLine(rotationPen, edge, point);
            var diamond = new[]
            {
                new PointF(point.X, point.Y - 5),
                new PointF(point.X + 5, point.Y),
                new PointF(point.X, point.Y + 5),
                new PointF(point.X - 5, point.Y)
            };
            g.FillPolygon(_bezierHandleBrush, diamond);
            g.DrawPolygon(_handleBorderPen, diamond);
        }

        if (IsTransformFocusVisible(geometry))
        {
            var focus = TransformOverlayPointToScreen(_transformFocus);
            g.FillEllipse(focusBrush, focus.X - 6, focus.Y - 6, 12, 12);
            g.DrawEllipse(focusPen, focus.X - 6, focus.Y - 6, 12, 12);
            g.DrawLine(focusPen, focus.X - 9, focus.Y, focus.X + 9, focus.Y);
            g.DrawLine(focusPen, focus.X, focus.Y - 9, focus.X, focus.Y + 9);
        }
    }

    private void DrawDistortOverlay(Graphics g)
    {
        if (!DistortBoundsVisible) return;
        var geometry = GetDistortOverlayScreenGeometry();
        using var boundary = new Pen(Color.FromArgb(245, 104, 255, 188), 1.4f) { DashStyle = DashStyle.Dash };
        using var grid = new Pen(Color.FromArgb(120, 104, 255, 188), 1f);
        using var guide = new Pen(Color.FromArgb(165, 112, 204, 255), 1f) { DashStyle = DashStyle.Dot };
        foreach (var segment in geometry.BezierSegments)
        {
            g.DrawBezier(
                boundary,
                segment.Start,
                segment.Control1,
                segment.Control2,
                segment.End);
        }

        for (var index = 1; index < 3; index++)
        {
            var t = index / 3f;
            var left = TransformOverlayPointToScreen(_distortFrame.BoundaryPoint(DistortSide.Left, t));
            var right = TransformOverlayPointToScreen(_distortFrame.BoundaryPoint(DistortSide.Right, t));
            var top = TransformOverlayPointToScreen(_distortFrame.BoundaryPoint(DistortSide.Top, t));
            var bottom = TransformOverlayPointToScreen(_distortFrame.BoundaryPoint(DistortSide.Bottom, t));
            g.DrawLine(grid, left, right);
            g.DrawLine(grid, top, bottom);
        }

        foreach (var handle in geometry.VisualHandles)
        {
            if (handle.Reference.Kind != DistortHandleKind.Anchor)
            {
                g.DrawLine(guide, handle.Anchor, handle.Point);
            }
        }

        using var anchorFill = new SolidBrush(Color.FromArgb(255, 255, 240, 168));
        using var controlFill = new SolidBrush(Color.FromArgb(255, 112, 204, 255));
        foreach (var handle in geometry.VisualHandles)
        {
            if (handle.Reference.Kind == DistortHandleKind.Anchor)
            {
                DrawHandle(g, handle.Point, anchorFill, 9);
                continue;
            }

            g.FillEllipse(controlFill, handle.Point.X - 3.5f, handle.Point.Y - 3.5f, 7, 7);
            g.DrawEllipse(_handleBorderPen, handle.Point.X - 3.5f, handle.Point.Y - 3.5f, 7, 7);
        }
    }

    private void DrawDrawingObjectSelectionOverlay(Graphics g)
    {
        if (!DrawingObjectSelectionVisible) return;
        var rect = WorldToScreenBounds(_drawingObjectSelectionBounds);
        if (rect.Width <= 0 || rect.Height <= 0) return;
        g.DrawRectangle(_drawingObjectSelectionOuterGlowPen, rect.X, rect.Y, rect.Width, rect.Height);
        g.DrawRectangle(_drawingObjectSelectionGlowPen, rect.X, rect.Y, rect.Width, rect.Height);
        g.DrawRectangle(_drawingObjectSelectionPen, rect.X, rect.Y, rect.Width, rect.Height);
        if (DrawingObjectAnchorVisible)
        {
            var anchor = WorldToScreen(_drawingObjectAnchor);
            g.FillEllipse(_handleBrush, anchor.X - 4, anchor.Y - 4, 8, 8);
            g.DrawEllipse(_handleBorderPen, anchor.X - 4, anchor.Y - 4, 8, 8);
            g.DrawLine(_drawingObjectSelectionPen, anchor.X - 9, anchor.Y, anchor.X + 9, anchor.Y);
            g.DrawLine(_drawingObjectSelectionPen, anchor.X, anchor.Y - 9, anchor.X, anchor.Y + 9);
        }
    }

    private RectangleF WorldToScreenBounds(RectangleF bounds)
    {
        var topLeft = WorldToScreen(new PointF(bounds.Left, bounds.Top));
        var bottomRight = WorldToScreen(new PointF(bounds.Right, bounds.Bottom));
        return RectangleF.FromLTRB(
            Math.Min(topLeft.X, bottomRight.X),
            Math.Min(topLeft.Y, bottomRight.Y),
            Math.Max(topLeft.X, bottomRight.X),
            Math.Max(topLeft.Y, bottomRight.Y));
    }

    private static PointF Midpoint(PointF left, PointF right)
    {
        return new PointF((left.X + right.X) * 0.5f, (left.Y + right.Y) * 0.5f);
    }

    private static PointF UnitVector(PointF start, PointF end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var length = MathF.Sqrt(dx * dx + dy * dy);
        return length > 0.0001f ? new PointF(dx / length, dy / length) : PointF.Empty;
    }

    private static PointF Offset(
        PointF origin,
        PointF firstAxis,
        float firstDistance,
        PointF secondAxis,
        float secondDistance)
    {
        return new PointF(
            origin.X + firstAxis.X * firstDistance + secondAxis.X * secondDistance,
            origin.Y + firstAxis.Y * firstDistance + secondAxis.Y * secondDistance);
    }

    private static PointF OffsetFromEdge(
        PointF edgeStart,
        PointF edgeEnd,
        PointF edgeMidpoint,
        PointF center,
        float distance)
    {
        var dx = edgeEnd.X - edgeStart.X;
        var dy = edgeEnd.Y - edgeStart.Y;
        var length = MathF.Sqrt(dx * dx + dy * dy);
        if (length <= 0.0001f) return edgeMidpoint;
        var normalX = -dy / length;
        var normalY = dx / length;
        if (normalX * (edgeMidpoint.X - center.X) + normalY * (edgeMidpoint.Y - center.Y) < 0)
        {
            normalX = -normalX;
            normalY = -normalY;
        }

        return new PointF(edgeMidpoint.X + normalX * distance, edgeMidpoint.Y + normalY * distance);
    }

    private void DrawBezierGuides(Graphics g, int i, float startT, float endT, bool primary)
    {
        var (start, control1, control2, end) = GetBezierScreenPoints(i);
        using var partialPath = BuildCubicSamplePath(start, control1, control2, end, startT, endT);
        DrawSelectionPath(
            g,
            partialPath,
            primary,
            SelectionHighlightKind.Stroke,
            startT <= DrawingTopologyRules.UnitIntersectionTolerance
                ? Scene.GetLineEndpointStyle(i, startEndpoint: true)
                : LineEndpointStyle.Round,
            endT >= 1f - DrawingTopologyRules.UnitIntersectionTolerance
                ? Scene.GetLineEndpointStyle(i, startEndpoint: false)
                : LineEndpointStyle.Round);
    }

    private void DrawBezierSelectionContext(Graphics g, int i)
    {
        var (start, control1, control2, end) = GetBezierScreenPoints(i);
        using var fullPath = BuildCubicPath(start, control1, control2, end);
        using var mutedPen = new Pen(Color.FromArgb(80, SelectionLineColor(SelectionHighlightKind.Stroke, primary: false)), 1.2f);
        g.DrawPath(mutedPen, fullPath);
    }

    private void DrawBezierOutline(Graphics g, int i, bool primary)
    {
        var (start, control1, control2, end) = GetBezierScreenPoints(i);
        using var path = BuildCubicPath(start, control1, control2, end);
        DrawSelectionPath(
            g,
            path,
            primary,
            SelectionHighlightKind.Stroke,
            Scene.GetLineEndpointStyle(i, startEndpoint: true),
            Scene.GetLineEndpointStyle(i, startEndpoint: false));
    }

    private void DrawBoundaryOutline(Graphics g, int i, bool primary, SelectionHighlightKind highlightKind)
    {
        var points = GetBoundaryScreenPolyline(i);
        if (points.Length < 2) return;
        DrawSelectionPolygon(g, points, primary, highlightKind);
    }

    private void DrawBoundaryPartialOutline(Graphics g, int i, float startT, float endT)
    {
        var points = GetBoundaryScreenPolyline(i);
        if (points.Length < 2) return;

        using var fullPath = BuildPolylineSamplePath(points, 0, 1);
        using var partialPath = BuildPolylineSamplePath(points, startT, endT);
        using var mutedPen = new Pen(Color.FromArgb(80, SelectionLineColor(SelectionHighlightKind.Stroke, primary: false)), 1.2f);
        g.DrawPath(mutedPen, fullPath);
        DrawSelectionPath(g, partialPath, true, SelectionHighlightKind.Stroke);
    }

    private void DrawSelectionPath(
        Graphics g,
        GraphicsPath path,
        bool primary,
        SelectionHighlightKind highlightKind,
        LineEndpointStyle startStyle = LineEndpointStyle.Round,
        LineEndpointStyle endStyle = LineEndpointStyle.Round)
    {
        PrepareSelectionHighlightPens(highlightKind);
        var outer = primary ? _selectionOuterGlowPen : _multiSelectionOuterGlowPen;
        var glow = primary ? _selectionGlowPen : _multiSelectionGlowPen;
        var line = primary ? _selectionPen : _multiSelectionPen;
        var startCap = LineCapForEndpoint(startStyle);
        var endCap = LineCapForEndpoint(endStyle);
        outer.StartCap = glow.StartCap = line.StartCap = startCap;
        outer.EndCap = glow.EndCap = line.EndCap = endCap;
        try
        {
            g.DrawPath(outer, path);
            g.DrawPath(glow, path);
            g.DrawPath(line, path);
        }
        finally
        {
            outer.StartCap = glow.StartCap = line.StartCap = LineCap.Round;
            outer.EndCap = glow.EndCap = line.EndCap = LineCap.Round;
        }
    }

    internal static SelectionHighlightKind SelectionHighlightForShape(ShapeKind shape)
    {
        return shape is ShapeKind.Line or ShapeKind.Freeform
            ? SelectionHighlightKind.Stroke
            : SelectionHighlightKind.Fill;
    }

    internal static Color SelectionLineColor(SelectionHighlightKind highlightKind, bool primary)
    {
        return (highlightKind, primary) switch
        {
            (SelectionHighlightKind.Fill, true) => Color.FromArgb(255, 104, 244, 214),
            (SelectionHighlightKind.Fill, false) => Color.FromArgb(235, 112, 220, 255),
            (SelectionHighlightKind.Stroke, true) => Color.FromArgb(255, 255, 214, 92),
            _ => Color.FromArgb(235, 255, 171, 72)
        };
    }

    internal static Color SelectionGlowColor(SelectionHighlightKind highlightKind, bool primary, float pulse)
    {
        pulse = Math.Clamp(pulse, 0, 1);
        if (highlightKind == SelectionHighlightKind.Fill)
        {
            return primary
                ? Color.FromArgb(140 + (int)MathF.Round(55 * pulse), 32, 190, 224)
                : Color.FromArgb(100 + (int)MathF.Round(45 * pulse), 32, 172, 220);
        }

        return primary
            ? Color.FromArgb(145 + (int)MathF.Round(65 * pulse), 255, 145, 44)
            : Color.FromArgb(100 + (int)MathF.Round(50 * pulse), 255, 126, 40);
    }

    internal static Color SelectionOuterGlowColor(SelectionHighlightKind highlightKind, bool primary, float pulse)
    {
        pulse = Math.Clamp(pulse, 0, 1);
        if (highlightKind == SelectionHighlightKind.Fill)
        {
            return primary
                ? Color.FromArgb(55 + (int)MathF.Round(40 * pulse), 20, 150, 180)
                : Color.FromArgb(38 + (int)MathF.Round(30 * pulse), 20, 140, 175);
        }

        return primary
            ? Color.FromArgb(65 + (int)MathF.Round(50 * pulse), 255, 86, 30)
            : Color.FromArgb(45 + (int)MathF.Round(35 * pulse), 240, 78, 28);
    }

    internal static float SelectionOuterGlowWidth(SelectionHighlightKind highlightKind, bool primary, float pulse)
    {
        return highlightKind == SelectionHighlightKind.Fill
            ? primary ? 7.5f + 2f * pulse : 5.5f + 1.5f * pulse
            : primary ? 10.5f + 3f * pulse : 7f + 2f * pulse;
    }

    internal static float SelectionGlowWidth(SelectionHighlightKind highlightKind, bool primary, float pulse)
    {
        return highlightKind == SelectionHighlightKind.Fill
            ? primary ? 4.2f + 1.4f * pulse : 3.2f + 1f * pulse
            : primary ? 5.8f + 2.4f * pulse : 4.2f + 1.6f * pulse;
    }

    internal static float SelectionLineWidth(SelectionHighlightKind highlightKind, bool primary, float pulse)
    {
        return highlightKind == SelectionHighlightKind.Fill
            ? primary ? 1.7f + 0.45f * pulse : 1.15f + 0.35f * pulse
            : primary ? 2.2f + 0.7f * pulse : 1.35f + 0.45f * pulse;
    }

    private void PrepareSelectionHighlightPens(SelectionHighlightKind highlightKind)
    {
        var pulse = SelectionHighlightPulse;
        _selectionOuterGlowPen.Color = SelectionOuterGlowColor(highlightKind, primary: true, pulse);
        _selectionOuterGlowPen.Width = SelectionOuterGlowWidth(highlightKind, primary: true, pulse);
        _selectionGlowPen.Color = SelectionGlowColor(highlightKind, primary: true, pulse);
        _selectionGlowPen.Width = SelectionGlowWidth(highlightKind, primary: true, pulse);
        _selectionPen.Color = SelectionLineColor(highlightKind, primary: true);
        _selectionPen.Width = SelectionLineWidth(highlightKind, primary: true, pulse);

        _multiSelectionOuterGlowPen.Color = SelectionOuterGlowColor(highlightKind, primary: false, pulse);
        _multiSelectionOuterGlowPen.Width = SelectionOuterGlowWidth(highlightKind, primary: false, pulse);
        _multiSelectionGlowPen.Color = SelectionGlowColor(highlightKind, primary: false, pulse);
        _multiSelectionGlowPen.Width = SelectionGlowWidth(highlightKind, primary: false, pulse);
        _multiSelectionPen.Color = SelectionLineColor(highlightKind, primary: false);
        _multiSelectionPen.Width = SelectionLineWidth(highlightKind, primary: false, pulse);
    }

    private void DrawSelectionPolygon(Graphics g, PointF[] points, bool primary, SelectionHighlightKind highlightKind)
    {
        if (points.Length < 3) return;
        using var path = new GraphicsPath();
        path.AddLines(points);
        path.CloseFigure();
        DrawSelectionPath(g, path, primary, highlightKind);
    }

    private void DrawSelectionPolyline(Graphics g, PointF[] points, bool primary, SelectionHighlightKind highlightKind)
    {
        if (points.Length < 2) return;
        using var path = new GraphicsPath();
        path.AddLines(points);
        DrawSelectionPath(g, path, primary, highlightKind);
    }

    private void DrawSelectionDot(Graphics g, PointF point, bool primary, SelectionHighlightKind highlightKind)
    {
        PrepareSelectionHighlightPens(highlightKind);
        var pulseScale = 0.92f + 0.16f * SelectionHighlightPulse;
        var outer = (primary ? 12f : 8f) * pulseScale;
        var glow = (primary ? 7f : 5f) * pulseScale;
        var line = (primary ? 2.5f : 1.5f) * pulseScale;
        using var outerBrush = new SolidBrush(primary ? _selectionOuterGlowPen.Color : _multiSelectionOuterGlowPen.Color);
        using var glowBrush = new SolidBrush(primary ? _selectionGlowPen.Color : _multiSelectionGlowPen.Color);
        using var lineBrush = new SolidBrush(primary ? _selectionPen.Color : _multiSelectionPen.Color);
        g.FillEllipse(outerBrush, point.X - outer * 0.5f, point.Y - outer * 0.5f, outer, outer);
        g.FillEllipse(glowBrush, point.X - glow * 0.5f, point.Y - glow * 0.5f, glow, glow);
        g.FillEllipse(lineBrush, point.X - line * 0.5f, point.Y - line * 0.5f, line, line);
    }

    private void DrawBoundaryHandles(Graphics g, int i)
    {
        foreach (var handle in BoundaryHandles())
        {
            DrawHandle(g, WorldToScreen(GetBoundaryHandleWorldPoint(i, handle)), _handleBrush, 9);
        }
    }

    private void DrawTextAreaOverlay(Graphics graphics, int objectIndex, bool drawHandles)
    {
        var corners = GetTextAreaWorldCorners(objectIndex).Select(WorldToScreen).ToArray();
        if (corners.Length != 4) return;
        graphics.DrawPolygon(_textAreaGlowPen, corners);
        graphics.DrawPolygon(_textAreaPen, corners);
        if (!drawHandles) return;

        foreach (var handle in TextAreaResizeHandles())
        {
            DrawHandle(
                graphics,
                WorldToScreen(GetTextAreaHandleWorldPoint(objectIndex, handle)),
                _textAreaHandleBrush,
                9);
        }
    }

    private void DrawHandle(Graphics g, PointF point, Brush brush, float size)
    {
        var rect = new RectangleF(point.X - size * 0.5f, point.Y - size * 0.5f, size, size);
        g.FillRectangle(brush, rect);
        g.DrawRectangle(_handleBorderPen, rect.X, rect.Y, rect.Width, rect.Height);
    }

    private (PointF Start, PointF Control1, PointF Control2, PointF End) GetBezierScreenPoints(int i)
    {
        var hit = new DrawingElementHit(
            new DrawingElementKey(i, DrawingElementKind.Stroke, 0),
            0,
            0,
            1);
        return GetBezierScreenPoints(hit);
    }

    private (PointF Start, PointF Control1, PointF Control2, PointF End) GetBezierScreenPoints(DrawingElementHit hit)
    {
        if (TryGetEditableBezierWorldPoints(
                hit,
                out var start,
                out var control1,
                out var control2,
                out var end))
        {
            return (WorldToScreen(start), WorldToScreen(control1), WorldToScreen(control2), WorldToScreen(end));
        }

        return GetBezierScreenPoints(hit.Key.ObjectIndex);
    }

    internal bool TryGetLineBezierWorldPoints(
        int objectIndex,
        float startT,
        float endT,
        out PointF start,
        out PointF control1,
        out PointF control2,
        out PointF end)
    {
        start = PointF.Empty;
        control1 = PointF.Empty;
        control2 = PointF.Empty;
        end = PointF.Empty;
        return Scene.TryGetLineBezierPart(
            objectIndex,
            startT,
            endT,
            out start,
            out control1,
            out control2,
            out end);
    }

    private static GraphicsPath BuildCubicPath(PointF start, PointF control1, PointF control2, PointF end)
    {
        var path = new GraphicsPath();
        path.AddBezier(start, control1, control2, end);
        return path;
    }

    private static GraphicsPath BuildCubicSamplePath(
        PointF start,
        PointF control1,
        PointF control2,
        PointF end,
        float startT,
        float endT)
    {
        startT = Math.Clamp(startT, 0, 1);
        endT = Math.Clamp(endT, startT, 1);
        var path = new GraphicsPath();
        var previous = CubicPoint(start, control1, control2, end, startT);
        const int samples = 20;
        for (var i = 1; i <= samples; i++)
        {
            var t = startT + (endT - startT) * i / samples;
            var current = CubicPoint(start, control1, control2, end, t);
            path.AddLine(previous, current);
            previous = current;
        }

        return path;
    }

    private static GraphicsPath BuildPolylineSamplePath(PointF[] points, float startT, float endT)
    {
        startT = Math.Clamp(startT, 0, 1);
        endT = Math.Clamp(endT, startT, 1);
        var path = new GraphicsPath();
        var previous = PolylinePointAt(points, startT);
        var segments = Math.Max(1, points.Length - 1);
        var samples = Math.Max(2, (int)Math.Ceiling((endT - startT) * segments * 4));
        for (var i = 1; i <= samples; i++)
        {
            var t = startT + (endT - startT) * i / samples;
            var current = PolylinePointAt(points, t);
            path.AddLine(previous, current);
            previous = current;
        }

        return path;
    }

    private static PointF PolylinePointAt(PointF[] points, float t)
    {
        if (points.Length == 0) return PointF.Empty;
        if (points.Length == 1) return points[0];
        t = Math.Clamp(t, 0, 1);
        var segments = points.Length - 1;
        var scaled = t * segments;
        var index = Math.Min(segments - 1, (int)MathF.Floor(scaled));
        return Lerp(points[index], points[index + 1], scaled - index);
    }

    private static PointF CubicPoint(PointF start, PointF control1, PointF control2, PointF end, float t)
    {
        var inv = 1 - t;
        return new PointF(
            inv * inv * inv * start.X
                + 3 * inv * inv * t * control1.X
                + 3 * inv * t * t * control2.X
                + t * t * t * end.X,
            inv * inv * inv * start.Y
                + 3 * inv * inv * t * control1.Y
                + 3 * inv * t * t * control2.Y
                + t * t * t * end.Y);
    }

    private PointF LocalToWorld(int i, PointF local)
    {
        return ObjectLocalToWorld(Scene, i, local);
    }

    private static PointF ObjectLocalToWorld(VectorScene scene, int i, PointF local)
    {
        var angle = scene.Angle[i];
        var cos = MathF.Cos(angle);
        var sin = MathF.Sin(angle);
        return new PointF(
            scene.X[i] + local.X * cos - local.Y * sin,
            scene.Y[i] + local.X * sin + local.Y * cos);
    }

    internal PointF[][] GetMixingStrokeScreenContours(VectorScene scene, int objectIndex)
    {
        if (scene.TryGetMixingBrushLocalRegion(objectIndex, out var region))
        {
            var edges = MixingBrushRegionRasterizer.ExtractBoundaryEdges(region.TriangleIndices);
            var contours = new List<PointF[]>(edges.Length);
            foreach (var edge in edges)
            {
                if ((uint)edge.StartVertex >= region.Vertices.Length
                    || (uint)edge.EndVertex >= region.Vertices.Length)
                {
                    continue;
                }
                contours.Add(
                [
                    WorldToScreen(ObjectLocalToWorld(scene, objectIndex, region.Vertices[edge.StartVertex].Point)),
                    WorldToScreen(ObjectLocalToWorld(scene, objectIndex, region.Vertices[edge.EndVertex].Point))
                ]);
            }
            return contours.ToArray();
        }

        if (!scene.TryGetMixingStrokeLocalSamples(objectIndex, out var samples) || samples.Length == 0)
        {
            return [];
        }
        var points = new List<PointF>(samples.Length);
        foreach (var sample in samples)
        {
            if (!IsFiniteMixingPoint(sample.Point)) continue;
            points.Add(WorldToScreen(ObjectLocalToWorld(scene, objectIndex, sample.Point)));
        }
        return points.Count > 0 ? [points.ToArray()] : [];
    }

    private static bool IsRenderableMixingSample(MixingBrushTrajectorySample sample) =>
        IsFiniteMixingPoint(sample.Point)
        && float.IsFinite(sample.Diameter)
        && sample.Diameter > 0;

    private static bool IsFiniteMixingPoint(PointF point) =>
        float.IsFinite(point.X) && float.IsFinite(point.Y);

    private PointF[] GetBoundaryScreenPolyline(int i)
    {
        var boundary = Scene.GetShapeBoundary(i);
        var points = new PointF[boundary.Length];
        for (var p = 0; p < boundary.Length; p++) points[p] = WorldToScreen(boundary[p]);
        return points;
    }

    private static PointF Lerp(PointF a, PointF b, float t)
    {
        return new PointF(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
    }

    private static IEnumerable<EditHandleKind> BoundaryHandles()
    {
        yield return EditHandleKind.BoundsTopLeft;
        yield return EditHandleKind.BoundsTopRight;
        yield return EditHandleKind.BoundsBottomRight;
        yield return EditHandleKind.BoundsBottomLeft;
    }

    internal static IEnumerable<EditHandleKind> TextAreaResizeHandles()
    {
        yield return EditHandleKind.TextAreaLeft;
        yield return EditHandleKind.TextAreaRight;
    }

    private static bool IsFreehandShape(ShapeKind shape)
    {
        return shape is ShapeKind.Freeform or ShapeKind.BrushStroke;
    }

    private static float Distance(Point screen, PointF point)
    {
        var dx = screen.X - point.X;
        var dy = screen.Y - point.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private static float Distance(PointF a, PointF b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private PointF WorldToScreen(PointF point) => WorldToScreen(point.X, point.Y);

    private SolidBrush BrushFor(int argb)
    {
        if (_brushCache.TryGetValue(argb, out var brush)) return brush;
        if (_brushCache.Count >= MaxGdiBrushCacheEntries)
        {
            foreach (var item in _brushCache.Values) item.Dispose();
            _brushCache.Clear();
        }

        brush = new SolidBrush(Color.FromArgb(argb));
        _brushCache[argb] = brush;
        return brush;
    }
}
