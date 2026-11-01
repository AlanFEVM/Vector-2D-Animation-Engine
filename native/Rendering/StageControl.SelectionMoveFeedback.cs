using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Clipper2Lib;

namespace VectorAnimationEngine;

internal sealed partial class StageControl
{
    internal const float SelectionMoveOriginOpacity = 0.25f;
    internal const float SelectionMoveOccluderOpacity = 0.35f;

    private VectorScene? _selectionMoveScene;
    private int _selectionMoveFrame;
    private readonly Dictionary<int, VectorScene> _selectionMoveOrigins = [];
    private readonly Dictionary<int, PointF[][][]> _selectionMoveOriginClips = [];
    private readonly Dictionary<(int ObjectIndex, SceneRenderPass Pass), PathsD> _selectionMoveCoverage = [];
    private readonly Dictionary<(int ObjectIndex, SceneRenderPass Pass), PointF[][]> _selectionMoveOcclusion = [];
    private long _selectionMoveCoverageRevision = -1;
    private float _selectionMoveCoverageZoom;

    internal bool SelectionMoveFeedbackActive => ReferenceEquals(_selectionMoveScene, Scene)
        && _selectionMoveFrame == Frame && _selectionMoveOrigins.Count > 0
        && UsesObjectSelectionOverlay;

    internal IReadOnlyDictionary<int, VectorScene> SelectionMoveOrigins => _selectionMoveOrigins;
    internal PointF[][][] SelectionMoveOriginClips(int objectIndex) => _selectionMoveOriginClips[objectIndex];

    internal void BeginSelectionMoveFeedback(IEnumerable<int> objectIndices)
    {
        ClearSelectionFillDragFront(invalidate: false);
        if (!UsesObjectSelectionOverlay) return;
        foreach (var objectIndex in objectIndices.Distinct()
            .Where(index => (uint)index < Scene.ObjectCount && Scene.IsObjectActive(index, Frame))
            .OrderByDescending(index => Scene.ObjectLayer[index])
            .ThenBy(index => Scene.ObjectOrder[index]).ThenBy(index => Scene.ObjectSubOrder[index]))
        {
            if (VectorScene.CreateObjectPreviewScene(Scene, objectIndex) is not { } origin) continue;
            origin.LayerOpacity[0] = Scene.LayerOpacity[Scene.ObjectLayer[objectIndex]];
            _selectionMoveOrigins.Add(objectIndex, origin);
            _selectionMoveOriginClips.Add(objectIndex, SelectionMoveClipContours(objectIndex)
                .Select(contours => contours.Select(contour => contour.ToArray()).ToArray()).ToArray());
        }
        _selectionMoveScene = Scene;
        _selectionMoveFrame = Frame;
    }

    private bool ClearSelectionMoveFeedbackCore()
    {
        var active = _selectionMoveScene is not null;
        _selectionMoveScene = null;
        _selectionMoveOrigins.Clear();
        _selectionMoveOriginClips.Clear();
        _selectionMoveCoverage.Clear();
        _selectionMoveOcclusion.Clear();
        _selectionMoveCoverageRevision = -1;
        return active;
    }

    internal PointF[][] GetSelectionMoveOcclusionContours(int objectIndex, SceneRenderPass pass)
    {
        if (!SelectionMoveFeedbackActive || _selectionMoveOrigins.ContainsKey(objectIndex)
            || (uint)objectIndex >= Scene.ObjectCount) return [];
        if (_selectionMoveCoverageRevision != SelectionGeometryRevision || _selectionMoveCoverageZoom != Zoom)
        {
            _selectionMoveCoverage.Clear();
            _selectionMoveOcclusion.Clear();
            _selectionMoveCoverageRevision = SelectionGeometryRevision;
            _selectionMoveCoverageZoom = Zoom;
        }
        var key = (objectIndex, pass);
        if (_selectionMoveOcclusion.TryGetValue(key, out var cached)) return cached;
        var coverage = new PathsD();
        var bounds = Scene.GetObjectWorldBounds(objectIndex);
        foreach (var selected in _selectionMoveOrigins.Keys)
        {
            if ((uint)selected >= Scene.ObjectCount || !Scene.IsObjectActive(selected, Frame)) continue;
            var layerComparison = Scene.ObjectLayer[selected].CompareTo(Scene.ObjectLayer[objectIndex]);
            if (layerComparison < 0 || !bounds.IntersectsWith(Scene.GetObjectWorldBounds(selected))) continue;
            var laterInLayer = SceneRenderOrderBuffer.CompareObjects(Scene, objectIndex, selected) > 0;
            if (Scene.HasFill(selected) && (layerComparison > 0 || pass == SceneRenderPass.Stroke || laterInLayer))
                coverage.AddRange(SelectionMoveObjectCoverage(selected, SceneRenderPass.Fill));
            if (Scene.HasStroke(selected)
                && (layerComparison > 0 || pass == SceneRenderPass.Stroke && laterInLayer))
                coverage.AddRange(SelectionMoveObjectCoverage(selected, SceneRenderPass.Stroke));
        }
        var union = coverage.Count == 0 ? coverage : Clipper.Union(coverage, new PathsD(), FillRule.NonZero, 4);
        var contours = union.Select(contour => contour.Select(point =>
            new PointF((float)point.x, (float)point.y)).ToArray()).ToArray();
        _selectionMoveOcclusion.Add(key, contours);
        return contours;
    }

    private PathsD SelectionMoveObjectCoverage(int objectIndex, SceneRenderPass pass)
    {
        var key = (objectIndex, pass);
        if (_selectionMoveCoverage.TryGetValue(key, out var cached)) return cached;
        var shape = Scene.ShapeKind[objectIndex];
        var contours = shape == ShapeKind.Line
            ? ObjectArrowStrokeCurves(new DrawingElementHit(
                new DrawingElementKey(objectIndex, DrawingElementKind.Stroke, 0), 0, 0, 1))
            : GetObjectBoundaryContoursForRendering(Scene, objectIndex);
        var paths = new PathsD(contours.Where(contour => contour.Length > 0)
            .Select(contour => new PathD(contour.Select(point => new PointD(point.X, point.Y)))));
        var coverage = pass == SceneRenderPass.Fill
            ? Clipper.Union(paths, new PathsD(), FillRule.EvenOdd, 4)
            : Clipper.InflatePaths(paths, Math.Max(ScreenLengthToWorld(1), Scene.Stroke[objectIndex]) * 0.5d,
                JoinType.Round, shape is ShapeKind.Line or ShapeKind.Freeform ? EndType.Round : EndType.Polygon,
                precision: 4, arcTolerance: Math.Max(0.0001, ScreenLengthToWorld(0.1f)));
        foreach (var clip in SelectionMoveClipContours(objectIndex))
        {
            var mask = new PathsD(clip.Where(contour => contour.Length >= 3)
                .Select(contour => new PathD(contour.Select(point => new PointD(point.X, point.Y)))));
            coverage = Clipper.Intersect(coverage, mask, FillRule.EvenOdd, 4);
        }
        _selectionMoveCoverage.Add(key, coverage);
        return coverage;
    }

    private IEnumerable<PointF[][]> SelectionMoveClipContours(int objectIndex)
    {
        if (Scene.TryGetMaskLayerIndex(Scene.ObjectLayer[objectIndex], out var maskLayer))
        {
            yield return Scene.IsLayerEffectivelyVisible(maskLayer)
                ? Scene.GetActiveObjectIndices(Frame).ToArray().Where(index => Scene.ObjectLayer[index] == maskLayer
                    && SceneRenderOrder.HasFill(Scene.ShapeKind[index]))
                    .SelectMany(index => GetObjectBoundaryContoursForRendering(Scene, index)).ToArray()
                : [];
        }
        foreach (var clip in GetSceneCompositionMaskClips(objectIndex))
            yield return GetSceneCompositionMaskWorldContours(clip);
    }

    private GraphicsPath SelectionMoveScreenPath(PointF[][] contours)
    {
        var path = new GraphicsPath(FillMode.Alternate);
        foreach (var contour in contours)
            if (contour.Length >= 3)
                path.AddPolygon(contour.Select(point => WorldToScreen(point.X, point.Y)).ToArray());
        return path;
    }

    private void DrawSelectionMoveObject(Graphics graphics, int objectIndex, SceneRenderPass pass)
    {
        var contours = GetSelectionMoveOcclusionContours(objectIndex, pass);
        if (contours.Length == 0)
        {
            DrawObjectUnclipped(graphics, objectIndex, pass);
            return;
        }
        using var path = SelectionMoveScreenPath(contours);
        var state = graphics.Save();
        try
        {
            graphics.SetClip(path, CombineMode.Exclude);
            DrawObjectUnclipped(graphics, objectIndex, pass);
        }
        finally { graphics.Restore(state); }
        state = graphics.Save();
        try
        {
            graphics.SetClip(path, CombineMode.Intersect);
            DrawSelectionMoveOpacity(graphics, path.GetBounds(), SelectionMoveOccluderOpacity,
                surface => DrawObjectUnclipped(surface, objectIndex, pass));
        }
        finally { graphics.Restore(state); }
    }

    private void DrawSelectionMoveOrigins(Graphics graphics)
    {
        if (!SelectionMoveFeedbackActive) return;
        var source = Scene;
        var bounds = RectangleF.Empty;
        foreach (var origin in _selectionMoveOrigins.Values)
        {
            var world = origin.GetObjectWorldBounds(0);
            var topLeft = WorldToScreen(world.Left, world.Top);
            var bottomRight = WorldToScreen(world.Right, world.Bottom);
            var screen = RectangleF.FromLTRB(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y);
            bounds = bounds.IsEmpty ? screen : RectangleF.Union(bounds, screen);
        }
        DrawSelectionMoveOpacity(graphics, bounds, SelectionMoveOriginOpacity, surface =>
        {
            try
            {
                foreach (var entry in _selectionMoveOrigins)
                {
                    var state = surface.Save();
                    try
                    {
                        foreach (var clip in SelectionMoveOriginClips(entry.Key))
                        {
                            using var path = SelectionMoveScreenPath(clip);
                            surface.SetClip(path, CombineMode.Intersect);
                        }
                        Scene = entry.Value;
                        DrawSceneGdi(surface, entry.Value, int.MaxValue, forceObjectRenderer: true);
                    }
                    finally
                    {
                        Scene = source;
                        surface.Restore(state);
                    }
                }
            }
            finally { Scene = source; }
        });
    }

    private void DrawSelectionMoveOpacity(Graphics graphics, RectangleF bounds, float opacity, Action<Graphics> draw)
    {
        bounds.Inflate(2, 2);
        var pixels = Rectangle.Ceiling(RectangleF.Intersect(bounds, graphics.VisibleClipBounds));
        if (pixels.Width <= 0 || pixels.Height <= 0) return;
        using var bitmap = new Bitmap(pixels.Width, pixels.Height, PixelFormat.Format32bppPArgb);
        using (var surface = Graphics.FromImage(bitmap))
        {
            surface.SmoothingMode = graphics.SmoothingMode;
            surface.PixelOffsetMode = graphics.PixelOffsetMode;
            surface.InterpolationMode = graphics.InterpolationMode;
            surface.TranslateTransform(-pixels.X, -pixels.Y);
            draw(surface);
        }
        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(new ColorMatrix { Matrix33 = opacity });
        graphics.DrawImage(bitmap, pixels, 0, 0, pixels.Width, pixels.Height, GraphicsUnit.Pixel, attributes);
    }
}
