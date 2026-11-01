using System.Drawing.Drawing2D;
using Clipper2Lib;

namespace VectorAnimationEngine;

internal sealed partial class StageControl
{
    internal SelectionArrowSettings SelectionArrows { get; private set; } = new();
    internal bool ObjectSelectionMode { get; private set; }
    internal bool SelectionFeedbackSuppressed { get; private set; }
    internal bool UsesObjectSelectionOverlay => ObjectSelectionMode && !UsesReferenceProjection;
    internal static BitmapImageRaster ObjectSelectionPattern { get; } = CreateObjectSelectionPattern();
    private readonly Dictionary<DrawingElementKey, ObjectArrowSelectionRegion> _objectArrowFillContours = new();
    private readonly Dictionary<int, DrawingElementHit[]> _objectArrowParts = new();
    private (VectorScene Scene, long Geometry, long Summary, long Content, long Selection, int Frame, ulong Layers) _objectArrowFillKey;
    private ObjectArrowStippleMask? _objectArrowStippleMask;
    private float _objectArrowStippleMaskZoom;

    internal readonly record struct ObjectArrowSelectionRegion(int ObjectIndex, PointF[][] Contours, PointF PatternOrigin);
    internal readonly record struct ObjectArrowStippleMask(PointF[][] Contours, PointF PatternOrigin);

    private static PointF ObjectArrowPatternOrigin(PointF[][] contours)
    {
        var left = float.PositiveInfinity;
        var top = float.PositiveInfinity;
        foreach (var contour in contours)
            foreach (var point in contour)
            {
                left = Math.Min(left, point.X);
                top = Math.Min(top, point.Y);
            }
        return float.IsFinite(left) && float.IsFinite(top) ? new PointF(left, top) : PointF.Empty;
    }

    private static BitmapImageRaster CreateObjectSelectionPattern()
    {
        var raster = new BitmapImageRaster(new BitmapImageRasterKey("selection-stipple", 4, 4));
        for (var row = 0; row < 4; row++)
            for (var column = 0; column < 4; column++)
            {
                if (((row + column) & 1) != 0) continue;
                var dark = ((row - column) & 3) == 0 || ((row + column) & 3) == 0;
                var offset = row * raster.Stride + column * 4;
                var channel = (byte)(dark ? 0 : 110);
                raster.Pixels[offset] = channel;
                raster.Pixels[offset + 1] = channel;
                raster.Pixels[offset + 2] = channel;
                raster.Pixels[offset + 3] = (byte)(dark ? 190 : 110);
            }
        return raster;
    }

    internal bool ObjectArrowUsesBounds(int objectIndex) => Scene.ShapeKind[objectIndex]
        is ShapeKind.Bitmap or ShapeKind.ImportedSvg or ShapeKind.Text
        || Scene.ShapeKind[objectIndex] == ShapeKind.MixingStroke && !Scene.TryGetMixingBrushLocalRegion(objectIndex, out _);

    private void PrepareObjectArrowSelection()
    {
        var key = (Scene, Scene.GeometryRevision, Scene.SummaryRevision, Scene.ActiveContentRevision,
            SelectionRenderRevision, Frame, GetReference3DWorkspaceFrameLayerRenderState(Scene, Frame));
        if (_objectArrowFillKey != key || HasDistortPreview)
        {
            _objectArrowFillContours.Clear();
            _objectArrowParts.Clear();
            _objectArrowStippleMask = null;
            foreach (var group in SelectedElements.GroupBy(hit => hit.Key.ObjectIndex))
                _objectArrowParts[group.Key] = group.ToArray();
            _objectArrowFillKey = key;
        }
    }

    internal IEnumerable<ObjectArrowSelectionRegion> ObjectArrowSelectedFills()
    {
        PrepareObjectArrowSelection();
        foreach (var objectIndex in SelectedObjects)
        {
            if ((uint)objectIndex >= Scene.ObjectCount || !Scene.IsObjectActive(objectIndex, Frame)
                || ObjectArrowUsesBounds(objectIndex)) continue;
            var parts = _objectArrowParts.GetValueOrDefault(objectIndex, []);
            if (parts.Length == 0)
            {
                if (Scene.HasFill(objectIndex))
                    yield return ObjectArrowFillContours(objectIndex, null);
            }
            else foreach (var hit in parts)
                if (hit.Key.Kind == DrawingElementKind.Fill)
                    yield return ObjectArrowFillContours(objectIndex, hit);
        }
    }

    private ObjectArrowSelectionRegion ObjectArrowFillContours(int objectIndex, DrawingElementHit? hit)
    {
        var key = hit?.Key ?? new DrawingElementKey(objectIndex, DrawingElementKind.Fill, -1);
        if (_objectArrowFillContours.TryGetValue(key, out var cached)) return cached;
        var contours = hit is { } part ? GetSelectedFillPartContours(part)
            .Select(contour => contour.Select(point => MapObjectPointForRendering(Scene, objectIndex, point)).ToArray()).ToArray()
            : GetObjectBoundaryContoursForRendering(Scene, objectIndex);
        var visible = Scene.GetVisibleSelectionFillContours(objectIndex, Frame, contours);
        var region = new ObjectArrowSelectionRegion(objectIndex, visible, ObjectArrowPatternOrigin(contours));
        _objectArrowFillContours[key] = region;
        return region;
    }

    internal IEnumerable<ObjectArrowSelectionRegion> ObjectArrowSelectedStrokes()
    {
        PrepareObjectArrowSelection();
        foreach (var objectIndex in SelectedObjects)
        {
            if ((uint)objectIndex >= Scene.ObjectCount || !Scene.IsObjectActive(objectIndex, Frame)
                || ObjectArrowUsesBounds(objectIndex)) continue;
            var parts = _objectArrowParts.GetValueOrDefault(objectIndex, []);
            if (parts.Length == 0)
            {
                if (Scene.HasStroke(objectIndex))
                {
                    var contours = Scene.ShapeKind[objectIndex] == ShapeKind.Line
                        ? ObjectArrowStrokeCurves(new DrawingElementHit(
                            new DrawingElementKey(objectIndex, DrawingElementKind.Stroke, 0), 0, 0, 1))
                        : GetObjectBoundaryContoursForRendering(Scene, objectIndex);
                    yield return ObjectArrowStrokeRegion(objectIndex, contours,
                        closed: Scene.ShapeKind[objectIndex] is not (ShapeKind.Line or ShapeKind.Freeform));
                }
                continue;
            }
            foreach (var hit in parts)
            {
                if (hit.Key.Kind == DrawingElementKind.Fill) continue;
                var curves = ObjectArrowStrokeCurves(hit);
                if (curves.Length > 0)
                {
                    yield return ObjectArrowStrokeRegion(objectIndex, curves, closed: false);
                    continue;
                }
                var points = hit.Key.Kind == DrawingElementKind.Stroke
                    ? GetSelectedStrokePartPoints(hit) : GetSelectedBoundaryPartPoints(hit);
                points = points.Select(point => MapObjectPointForRendering(Scene, objectIndex, point)).ToArray();
                yield return ObjectArrowStrokeRegion(objectIndex, [points], closed: false);
            }
        }
    }

    private PointF[][] ObjectArrowStrokeCurves(DrawingElementHit hit) =>
        GetPresentedEditableBezierWorldSegments(hit).Select(segment =>
        {
            var length = Distance(segment.Start, segment.Control1) + Distance(segment.Control1, segment.Control2)
                + Distance(segment.Control2, segment.End);
            var steps = Math.Clamp((int)Math.Ceiling(WorldLengthToScreen(length) / 2), 8, 1024);
            return Enumerable.Range(0, steps + 1).Select(step => CubicPoint(segment.Start,
                segment.Control1, segment.Control2, segment.End, step / (float)steps)).ToArray();
        }).ToArray();

    private ObjectArrowSelectionRegion ObjectArrowStrokeRegion(int objectIndex, PointF[][] contours, bool closed) =>
        new(objectIndex, Scene.GetVisiblePathSelectionPolylines(objectIndex, Frame, contours, closed),
            ObjectArrowPatternOrigin(contours));

    internal ObjectArrowStippleMask GetObjectArrowStippleMask()
    {
        PrepareObjectArrowSelection();
        if (_objectArrowStippleMask is { } cached && _objectArrowStippleMaskZoom == Zoom) return cached;
        const int precision = 4;
        var coverage = new PathsD();
        var left = float.PositiveInfinity;
        var top = float.PositiveInfinity;
        foreach (var fill in ObjectArrowSelectedFills())
        {
            var contours = new PathsD(fill.Contours.Where(contour => contour.Length >= 3)
                .Select(contour => new PathD(contour.Select(point => new PointD(point.X, point.Y)))));
            if (contours.Count == 0) continue;
            coverage.AddRange(Clipper.Union(contours, new PathsD(), FillRule.EvenOdd, precision));
            left = Math.Min(left, fill.PatternOrigin.X);
            top = Math.Min(top, fill.PatternOrigin.Y);
        }
        foreach (var stroke in ObjectArrowSelectedStrokes())
        {
            var contours = new PathsD(stroke.Contours.Where(contour => contour.Length > 0)
                .Select(contour => new PathD(contour.Select(point => new PointD(point.X, point.Y)))));
            if (contours.Count == 0) continue;
            var radius = Math.Max(ScreenLengthToWorld(1), Scene.Stroke[stroke.ObjectIndex]) * 0.5d;
            coverage.AddRange(Clipper.InflatePaths(contours, radius, JoinType.Round, EndType.Round,
                precision: precision, arcTolerance: Math.Max(0.0001, ScreenLengthToWorld(0.1f))));
            left = Math.Min(left, stroke.PatternOrigin.X);
            top = Math.Min(top, stroke.PatternOrigin.Y);
        }
        var union = coverage.Count == 0 ? coverage
            : Clipper.Union(coverage, new PathsD(), FillRule.NonZero, precision);
        var mask = new ObjectArrowStippleMask(
            union.Select(contour => contour.Select(point => new PointF((float)point.x, (float)point.y)).ToArray()).ToArray(),
            float.IsFinite(left) && float.IsFinite(top) ? new PointF(left, top) : PointF.Empty);
        _objectArrowStippleMask = mask;
        _objectArrowStippleMaskZoom = Zoom;
        return mask;
    }

    internal void ConfigureSelectionArrows(SelectionArrowSettings settings, bool objectSelection,
        bool suppressGeometryFeedback = false)
    {
        settings = settings.Normalized();
        var suppress = objectSelection && suppressGeometryFeedback;
        if (SelectionArrows == settings && ObjectSelectionMode == objectSelection
            && SelectionFeedbackSuppressed == suppress) return;
        var clearsMoveFeedback = ObjectSelectionMode != objectSelection && SelectionMoveFeedbackActive;
        if (ObjectSelectionMode != objectSelection) ClearSelectionFillDragFront(invalidate: false);
        SelectionArrows = settings;
        ObjectSelectionMode = objectSelection;
        SelectionFeedbackSuppressed = suppress;
        if (clearsMoveFeedback) Invalidate();
        else InvalidateOverlay();
    }

    internal PointF[][] GetObjectArrowSelectionContours(int objectIndex)
    {
        var contours = GetObjectBoundaryContoursForRendering(Scene, objectIndex);
        var shape = Scene.ShapeKind[objectIndex];
        return Scene.GetVisiblePathSelectionPolylines(objectIndex, Frame, contours,
            closed: shape is not (ShapeKind.Line or ShapeKind.Freeform));
    }

    internal bool TryGetObjectArrowSelectionBounds(out RectangleF bounds)
    {
        bounds = DrawingObjectSelectionVisible ? DrawingObjectSelectionBounds : RectangleF.Empty;
        var initialized = DrawingObjectSelectionVisible;
        foreach (var objectIndex in SelectedObjects)
        {
            if ((uint)objectIndex >= Scene.ObjectCount || !Scene.IsObjectActive(objectIndex, Frame)) continue;
            if (!ObjectArrowUsesBounds(objectIndex)) continue;
            var objectBounds = Scene.GetObjectWorldBounds(objectIndex);
            if (!float.IsFinite(objectBounds.X) || !float.IsFinite(objectBounds.Y)
                || !float.IsFinite(objectBounds.Width) || !float.IsFinite(objectBounds.Height)) continue;
            bounds = initialized ? RectangleF.Union(bounds, objectBounds) : objectBounds;
            initialized = true;
        }
        return initialized;
    }

    internal string ObjectArrowSelectionLabel => DrawingObjectSelectionVisible
        ? UiLocalization.T("Symbol selection")
        : SelectedObjects.Count == 1 ? UiLocalization.T("Object selection")
        : string.Format(UiLocalization.T("{0} selected objects"), SelectedObjects.Count);

    internal static (PointF Start, PointF Corner, PointF End)[] ObjectArrowCornerMarks(RectangleF bounds, float dpiScale)
    {
        bounds.Inflate(5f * dpiScale, 5f * dpiScale);
        var length = Math.Min(12f * dpiScale, Math.Min(bounds.Width, bounds.Height) * 0.35f);
        return
        [
            (new(bounds.Left + length, bounds.Top), new(bounds.Left, bounds.Top), new(bounds.Left, bounds.Top + length)),
            (new(bounds.Right - length, bounds.Top), new(bounds.Right, bounds.Top), new(bounds.Right, bounds.Top + length)),
            (new(bounds.Left + length, bounds.Bottom), new(bounds.Left, bounds.Bottom), new(bounds.Left, bounds.Bottom - length)),
            (new(bounds.Right - length, bounds.Bottom), new(bounds.Right, bounds.Bottom), new(bounds.Right, bounds.Bottom - length))
        ];
    }

    private void DrawObjectArrowSelection(Graphics graphics)
    {
        if (SelectionFeedbackSuppressed) return;
        var previousMode = graphics.SmoothingMode;
        var previousInterpolation = graphics.InterpolationMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        try
        {
            Scene.PrepareSelectionOutlineBatch(SelectedObjects, Frame);
            using var outline = new Pen(Theme.Accent, SelectionArrows.OutlineWidth * DeviceDpi / 96f);
            using var tile = ObjectSelectionPattern.AcquireBitmap();
            using var stipple = new TextureBrush(tile.Bitmap, WrapMode.Tile);
            var preview = graphics.Save();
            try
            {
                var offset = SelectionDragPreviewOffset;
                graphics.TranslateTransform(WorldLengthToScreen(1) * offset.X, WorldLengthToScreen(1) * offset.Y);
                var mask = GetObjectArrowStippleMask();
                var origin = WorldToScreen(mask.PatternOrigin);
                using var patternTransform = new Matrix(1, 0, 0, 1, origin.X, origin.Y);
                stipple.Transform = patternTransform;
                using var path = new GraphicsPath(FillMode.Alternate);
                foreach (var contour in mask.Contours)
                    if (contour.Length >= 3) path.AddPolygon(contour.Select(WorldToScreen).ToArray());
                graphics.FillPath(stipple, path);
            }
            finally { graphics.Restore(preview); }
            foreach (var objectIndex in SelectedObjects)
            {
                if ((uint)objectIndex >= Scene.ObjectCount || !Scene.IsObjectActive(objectIndex, Frame)) continue;
                if (!ObjectArrowUsesBounds(objectIndex)) continue;
                foreach (var contour in GetObjectArrowSelectionContours(objectIndex))
                {
                    if (contour.Length > 1) graphics.DrawLines(outline, contour.Select(WorldToScreen).ToArray());
                    else if (contour.Length == 1)
                    {
                        var point = WorldToScreen(contour[0]);
                        using var dot = new SolidBrush(outline.Color);
                        graphics.FillEllipse(dot, point.X - 2, point.Y - 2, 4, 4);
                    }
                }
            }
            if (!TryGetObjectArrowSelectionBounds(out var bounds)) return;
            var screen = WorldToScreenBounds(bounds);
            if (DrawingObjectSelectionVisible)
                graphics.DrawRectangle(outline, screen.X, screen.Y, screen.Width, screen.Height);
            if (SelectionArrows.ShowCornerMarks)
            {
                foreach (var mark in ObjectArrowCornerMarks(screen, DeviceDpi / 96f))
                    graphics.DrawLines(outline, [mark.Start, mark.Corner, mark.End]);
            }
            if (SelectionArrows.ShowSelectionLabel)
            {
                var text = ObjectArrowSelectionLabel;
                var size = TextRenderer.MeasureText(text, Font, new Size(Math.Max(1, Width - 20), Font.Height + 8),
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                var label = new Rectangle((int)Math.Clamp(screen.Left, 2, Math.Max(2, Width - size.Width - 12)),
                    (int)Math.Max(2, screen.Top - size.Height - 13), size.Width + 10, size.Height + 4);
                using var background = new SolidBrush(Theme.Panel);
                graphics.FillRectangle(background, label);
                TextRenderer.DrawText(graphics, text, Font, Rectangle.Inflate(label, -5, -2), Theme.Accent,
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            }
        }
        finally
        {
            graphics.SmoothingMode = previousMode;
            graphics.InterpolationMode = previousInterpolation;
        }
    }
}
