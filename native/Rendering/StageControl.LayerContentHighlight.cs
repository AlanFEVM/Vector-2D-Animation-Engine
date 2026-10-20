using System.Drawing.Drawing2D;

namespace VectorAnimationEngine;

internal sealed partial class StageControl
{
    private const int MaxLayerContentHighlightObjects = 512;

    private readonly record struct LayerContentHighlightCacheKey(
        VectorScene Scene,
        int Frame,
        long GeometryRevision,
        long SummaryRevision,
        long ActiveContentRevision,
        int Layer,
        int ObjectCount,
        int LayerCount);

    private readonly List<int> _layerContentHighlightObjects = [];
    private LayerContentHighlightCacheKey _layerContentHighlightCacheKey;
    private bool _layerContentHighlightCacheValid;
    private bool _layerContentHighlightUsesObjectSnapshot;
    private int _layerContentHighlightLayer = -1;
    private int[] _layerContentHighlightObjectSnapshot = [];
    private VectorScene? _layerContentHighlightScene;
    private Color _layerContentHighlightColor = Color.Empty;
    private long _layerContentHighlightRevision;

    internal long LayerContentHighlightRevision => _layerContentHighlightRevision;
    internal Color LayerContentHighlightColor => _layerContentHighlightColor;

    internal bool LayerContentHighlightVisible =>
        _layerContentHighlightScene is not null
        && ReferenceEquals(_layerContentHighlightScene, Scene)
        && _layerContentHighlightColor.A > 0
        && !PlaybackActive
        && !RendersReferenceProjection
        && DragPreviewScene is null
        && !SelectionDragPreviewActive
        && !HasDistortPreview
        && !TransformMode
        && !DistortMode
        && LayerContentHighlightObjects.Count > 0;

    internal IReadOnlyList<int> LayerContentHighlightObjects
    {
        get
        {
            if (_layerContentHighlightUsesObjectSnapshot) return _layerContentHighlightObjectSnapshot;
            EnsureLayerContentHighlightObjects();
            return _layerContentHighlightObjects;
        }
    }

    /// <summary>
    /// Highlights every visible object on a drawing layer (including the descendants of a folder)
    /// without changing the stage selection. The list is rebuilt lazily from the scene revisions so
    /// drawing, layer visibility and frame changes stay in sync.
    /// </summary>
    internal void SetLayerContentHighlightLayer(int layer, Color color)
    {
        if ((uint)layer >= Scene.LayerCount)
        {
            ClearLayerContentHighlight();
            return;
        }

        var nextColor = Color.FromArgb(255, color);
        if (!_layerContentHighlightUsesObjectSnapshot
            && _layerContentHighlightLayer == layer
            && _layerContentHighlightColor.ToArgb() == nextColor.ToArgb()
            && ReferenceEquals(_layerContentHighlightScene, Scene))
        {
            return;
        }

        _layerContentHighlightUsesObjectSnapshot = false;
        _layerContentHighlightLayer = layer;
        _layerContentHighlightObjectSnapshot = [];
        _layerContentHighlightScene = Scene;
        _layerContentHighlightColor = nextColor;
        BumpLayerContentHighlightRevision();
    }

    /// <summary>
    /// Highlights an explicit set of composition objects. Scene layers own flattened composition
    /// objects rather than a drawing layer index, so the host supplies their indices.
    /// </summary>
    internal void SetLayerContentHighlightObjects(IReadOnlyList<int> objectIndices, Color color)
    {
        ArgumentNullException.ThrowIfNull(objectIndices);

        var next = new int[Math.Min(objectIndices.Count, MaxLayerContentHighlightObjects)];
        var count = 0;
        foreach (var index in objectIndices)
        {
            if ((uint)index >= Scene.ObjectCount || Array.IndexOf(next, index, 0, count) >= 0) continue;
            next[count++] = index;
            if (count >= next.Length) break;
        }

        if (count != next.Length) Array.Resize(ref next, count);

        var nextColor = Color.FromArgb(255, color);
        if (_layerContentHighlightUsesObjectSnapshot
            && _layerContentHighlightColor.ToArgb() == nextColor.ToArgb()
            && ReferenceEquals(_layerContentHighlightScene, Scene)
            && _layerContentHighlightObjectSnapshot.AsSpan().SequenceEqual(next))
        {
            return;
        }

        _layerContentHighlightUsesObjectSnapshot = true;
        _layerContentHighlightLayer = -1;
        _layerContentHighlightObjectSnapshot = next;
        _layerContentHighlightScene = Scene;
        _layerContentHighlightColor = nextColor;
        BumpLayerContentHighlightRevision();
    }

    internal void ClearLayerContentHighlight()
    {
        if (_layerContentHighlightScene is null && _layerContentHighlightColor.IsEmpty) return;

        _layerContentHighlightUsesObjectSnapshot = false;
        _layerContentHighlightLayer = -1;
        _layerContentHighlightObjectSnapshot = [];
        _layerContentHighlightScene = null;
        _layerContentHighlightColor = Color.Empty;
        _layerContentHighlightObjects.Clear();
        _layerContentHighlightCacheValid = false;
        BumpLayerContentHighlightRevision();
    }

    internal PointF[][] GetLayerContentHighlightWorldContours(int objectIndex)
    {
        return (uint)objectIndex < Scene.ObjectCount
            ? GetObjectBoundaryContoursForRendering(Scene, objectIndex)
            : [];
    }

    internal bool IsLayerContentHighlightObject(int objectIndex)
    {
        if ((uint)objectIndex >= Scene.ObjectCount) return false;
        if (_layerContentHighlightUsesObjectSnapshot) return true;
        var layer = (int)Scene.ObjectLayer[objectIndex];
        return layer == _layerContentHighlightLayer
            || Scene.IsLayerDescendantOf(layer, _layerContentHighlightLayer);
    }

    private void BumpLayerContentHighlightRevision()
    {
        unchecked
        {
            _layerContentHighlightRevision++;
        }
        _layerContentHighlightCacheValid = false;
        _direct2DRenderer.InvalidateLayerContentHighlightCommands();
        InvalidateOverlay();
    }

    private void EnsureLayerContentHighlightObjects()
    {
        if (!ReferenceEquals(_layerContentHighlightScene, Scene)
            || (uint)_layerContentHighlightLayer >= Scene.LayerCount)
        {
            _layerContentHighlightObjects.Clear();
            _layerContentHighlightCacheValid = false;
            return;
        }

        var key = new LayerContentHighlightCacheKey(
            Scene,
            Frame,
            Scene.GeometryRevision,
            Scene.SummaryRevision,
            Scene.ActiveContentRevision,
            _layerContentHighlightLayer,
            Scene.ObjectCount,
            Scene.LayerCount);
        if (_layerContentHighlightCacheValid && key == _layerContentHighlightCacheKey) return;

        _layerContentHighlightObjects.Clear();
        var activeObjects = Scene.GetActiveObjectIndices(Frame);
        foreach (var objectIndex in activeObjects)
        {
            if ((uint)objectIndex >= Scene.ObjectCount) continue;
            var layer = (int)Scene.ObjectLayer[objectIndex];
            if (layer != _layerContentHighlightLayer
                && !Scene.IsLayerDescendantOf(layer, _layerContentHighlightLayer))
            {
                continue;
            }

            _layerContentHighlightObjects.Add(objectIndex);
            if (_layerContentHighlightObjects.Count >= MaxLayerContentHighlightObjects) break;
        }

        _layerContentHighlightCacheKey = key;
        _layerContentHighlightCacheValid = true;
    }

    private void DrawLayerContentHighlight(Graphics g)
    {
        if (!LayerContentHighlightVisible) return;

        var color = LayerContentHighlightColor;
        using var glowPen = new Pen(Color.FromArgb(48, color), 5f)
        {
            LineJoin = LineJoin.Round,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        using var linePen = new Pen(Color.FromArgb(220, color), 1.4f)
        {
            LineJoin = LineJoin.Round,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };

        var previousSmoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        try
        {
            foreach (var objectIndex in LayerContentHighlightObjects)
            {
                if ((uint)objectIndex >= Scene.ObjectCount
                    || !IsLayerContentHighlightObject(objectIndex)
                    || !Scene.IsObjectActive(objectIndex, Frame)
                    || !Scene.ShouldRenderLayerContent(Scene.ObjectLayer[objectIndex]))
                {
                    continue;
                }

                DrawLayerContentHighlightObject(g, objectIndex, glowPen, linePen);
            }
        }
        finally
        {
            g.SmoothingMode = previousSmoothing;
        }
    }

    private void DrawLayerContentHighlightObject(
        Graphics graphics,
        int objectIndex,
        Pen glowPen,
        Pen linePen)
    {
        var shape = Scene.ShapeKind[objectIndex];
        if (shape == ShapeKind.Line)
        {
            if (!Scene.TryGetLineCubic(
                    objectIndex,
                    out var start,
                    out var control1,
                    out var control2,
                    out var end))
            {
                return;
            }

            using var linePath = new GraphicsPath();
            linePath.AddBezier(
                WorldToScreen(start),
                WorldToScreen(control1),
                WorldToScreen(control2),
                WorldToScreen(end));
            graphics.DrawPath(glowPen, linePath);
            graphics.DrawPath(linePen, linePath);
            return;
        }

        if (shape == ShapeKind.MixingStroke)
        {
            foreach (var contour in GetMixingStrokeScreenContours(Scene, objectIndex))
            {
                DrawLayerContentHighlightPolyline(graphics, contour, closed: false, glowPen, linePen);
            }
            return;
        }

        if (shape == ShapeKind.Path
            && !Scene.HasFill(objectIndex)
            && Scene.TryGetPathWorldContours(objectIndex, out var pathContours))
        {
            foreach (var contour in pathContours)
            {
                if (contour.Length == 0) continue;
                var screenContour = new PointF[contour.Length];
                for (var index = 0; index < contour.Length; index++)
                {
                    screenContour[index] = WorldToScreen(contour[index]);
                }
                DrawLayerContentHighlightPolyline(graphics, screenContour, closed: false, glowPen, linePen);
            }
            return;
        }

        var closed = !IsFreehandShape(shape);
        foreach (var contour in GetObjectBoundaryContoursForRendering(Scene, objectIndex))
        {
            if (contour.Length == 0) continue;
            var screenContour = new PointF[contour.Length];
            for (var index = 0; index < contour.Length; index++)
            {
                screenContour[index] = WorldToScreen(contour[index]);
            }
            DrawLayerContentHighlightPolyline(graphics, screenContour, closed, glowPen, linePen);
        }
    }

    private static void DrawLayerContentHighlightPolyline(
        Graphics graphics,
        PointF[] points,
        bool closed,
        Pen glowPen,
        Pen linePen)
    {
        if (points.Length == 0) return;
        if (points.Length == 1)
        {
            const float radius = 2.5f;
            var bounds = new RectangleF(
                points[0].X - radius,
                points[0].Y - radius,
                radius * 2,
                radius * 2);
            graphics.DrawEllipse(glowPen, bounds);
            graphics.DrawEllipse(linePen, bounds);
            return;
        }

        using var path = new GraphicsPath();
        if (closed && points.Length >= 3) path.AddPolygon(points);
        else path.AddLines(points);
        graphics.DrawPath(glowPen, path);
        graphics.DrawPath(linePen, path);
    }
}
