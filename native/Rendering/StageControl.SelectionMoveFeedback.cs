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
    internal readonly record struct SelectionMoveOrigin(VectorScene Scene, int ObjectIndex, bool SharedBitmapScene);
    private readonly Dictionary<int, SelectionMoveOrigin> _selectionMoveOrigins = [];
    private readonly Dictionary<int, PointF[][][]> _selectionMoveOriginClips = [];
    private readonly Dictionary<(int ObjectIndex, SceneRenderPass Pass), PathsD> _selectionMoveCoverage = [];
    private readonly Dictionary<(int ObjectIndex, SceneRenderPass Pass), PointF[][]> _selectionMoveOcclusion = [];
    private long _selectionMoveCoverageRevision = -1;
    private float _selectionMoveCoverageZoom;
    private Dictionary<int, PointF[][]>? _bitmapMoveOutlineContours;
    private long _bitmapMoveOutlineGeometry;
    private long _bitmapMoveOutlineSummary;
    private long _bitmapMoveOutlineContent;
    private long _bitmapMoveOutlineCheckedRevision = -1;
    private bool _bitmapMoveOutlineTranslationValid;
    private PointF _bitmapMoveOutlineOffset;
    internal long BitmapMoveSelectionRevision { get; private set; }

    internal bool SelectionMoveFeedbackActive => ReferenceEquals(_selectionMoveScene, Scene)
        && _selectionMoveFrame == Frame && _selectionMoveOrigins.Count > 0
        && !UsesReferenceProjection;

    internal IReadOnlyDictionary<int, SelectionMoveOrigin> SelectionMoveOrigins => _selectionMoveOrigins;
    internal PointF[][][] SelectionMoveOriginClips(int objectIndex) => _selectionMoveOriginClips[objectIndex];

    internal void BeginSelectionMoveFeedback(IEnumerable<int> objectIndices)
    {
        ClearSelectionFillDragFront(invalidate: false);
        BitmapMoveSelectionRevision++;
        if (UsesReferenceProjection) return;
        var ordered = objectIndices.Distinct()
            .Where(index => (uint)index < Scene.ObjectCount && Scene.IsObjectActive(index, Frame))
            .OrderByDescending(index => Scene.ObjectLayer[index])
            .ThenBy(index => Scene.ObjectOrder[index]).ThenBy(index => Scene.ObjectSubOrder[index]).ToArray();
        // One immutable snapshot for ordinary bitmaps avoids allocating a full
        // spatial index and LOD grid for every selected image on pointer down.
        // Keep source-to-snapshot indices explicit; clips and ordering still
        // belong to each original object, including across interleaved shapes.
        var bitmaps = ordered.Where(index => Scene.ShapeKind[index] == ShapeKind.Bitmap
            && !Scene.TryGetObjectDistortionsView(index, out _)
            && Scene.LayerOpacity[Scene.ObjectLayer[index]] == 1f
            && Scene.IsLayerEffectivelyVisible(Scene.ObjectLayer[index])
            && !Scene.IsLayerEffectivelyOutlined(Scene.ObjectLayer[index])
            && !Scene.GetLayerSymbolFilters(Scene.ObjectLayer[index]).HasEnabled).ToArray();
        var bitmapScene = bitmaps.Length > 1 ? Scene.CreateClipboardScene(bitmaps) : null;
        var bitmapIndices = bitmaps.Select((source, index) => (source, index))
            .ToDictionary(item => item.source, item => item.index);
        foreach (var objectIndex in ordered)
        {
            SelectionMoveOrigin origin;
            if (bitmapScene is not null && bitmapIndices.TryGetValue(objectIndex, out var bitmapIndex))
                origin = new(bitmapScene, bitmapIndex, true);
            else
            {
                if (VectorScene.CreateObjectPreviewScene(Scene, objectIndex) is not { } preview) continue;
                preview.LayerOpacity[0] = Scene.LayerOpacity[Scene.ObjectLayer[objectIndex]];
                origin = new(preview, 0, false);
            }
            _selectionMoveOrigins.Add(objectIndex, origin);
            _selectionMoveOriginClips.Add(objectIndex, SelectionMoveClipContours(objectIndex)
                .Select(contours => contours.Select(contour => contour.ToArray()).ToArray()).ToArray());
        }
        _selectionMoveScene = Scene;
        _selectionMoveFrame = Frame;
        if (bitmapScene is not null && bitmaps.Length == Scene.ObjectCount
            && Scene.LayerCount == 1 && !Scene.HasDisplayLayerEffects
            && !HasSceneCompositionMaskClips(Scene)
            && bitmaps.All(index => !Scene.ObjectHidden[index] && ObjectArrowUsesBounds(index)))
        {
            Scene.PrepareSelectionOutlineBatch(ordered, Frame);
            _bitmapMoveOutlineContours = ordered.ToDictionary(index => index,
                index => GetObjectArrowSelectionContours(index));
            _bitmapMoveOutlineGeometry = Scene.GeometryRevision;
            _bitmapMoveOutlineSummary = Scene.SummaryRevision;
            _bitmapMoveOutlineContent = Scene.ActiveContentRevision;
        }
        RequestStageFrame(basePresentationChanged: true, preserveSelectionRenderCache: true);
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
        _bitmapMoveOutlineContours = null;
        _bitmapMoveOutlineCheckedRevision = -1;
        _bitmapMoveOutlineTranslationValid = false;
        return active;
    }

    internal bool TryGetBitmapMoveSelectionTranslation(out PointF translation)
    {
        translation = PointF.Empty;
        if (!SelectionMoveFeedbackActive || _bitmapMoveOutlineContours is null
            || _bitmapMoveOutlineGeometry != Scene.GeometryRevision
            || _bitmapMoveOutlineSummary != Scene.SummaryRevision
            || _bitmapMoveOutlineContent != Scene.ActiveContentRevision
            || _selectionMoveOrigins.Count != Scene.ObjectCount
            || HasDistortPreview || HasSceneCompositionMaskClips(Scene)) return false;
        if (_bitmapMoveOutlineCheckedRevision != SelectionGeometryRevision)
        {
            _bitmapMoveOutlineCheckedRevision = SelectionGeometryRevision;
            _bitmapMoveOutlineTranslationValid = true;
            PointF? offset = null;
            foreach (var (source, origin) in _selectionMoveOrigins)
            {
                var next = new PointF(Scene.X[source] - origin.Scene.X[origin.ObjectIndex],
                    Scene.Y[source] - origin.Scene.Y[origin.ObjectIndex]);
                if (offset is { } previous && previous != next)
                {
                    _bitmapMoveOutlineTranslationValid = false;
                    break;
                }
                offset = next;
            }
            _bitmapMoveOutlineOffset = offset ?? PointF.Empty;
        }
        if (!_bitmapMoveOutlineTranslationValid) return false;
        translation = _bitmapMoveOutlineOffset;
        return true;
    }

    private bool TryGetTranslatedBitmapMoveOutline(int objectIndex, out PointF[][] contours)
    {
        contours = [];
        if (!TryGetBitmapMoveSelectionTranslation(out var translation)
            || !_bitmapMoveOutlineContours!.TryGetValue(objectIndex, out var original)) return false;
        contours = original.Select(contour => contour.Select(point =>
            new PointF(point.X + translation.X, point.Y + translation.Y)).ToArray()).ToArray();
        return true;
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
            var world = origin.Scene.GetObjectWorldBounds(origin.ObjectIndex);
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
                        Scene = entry.Value.Scene;
                        if (entry.Value.SharedBitmapScene)
                            DrawObjectUnclipped(surface, entry.Value.ObjectIndex, SceneRenderPass.Fill);
                        else
                            DrawSceneGdi(surface, entry.Value.Scene, int.MaxValue, forceObjectRenderer: true);
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
