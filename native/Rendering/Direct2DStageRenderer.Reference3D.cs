using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;
using DCommonAlphaMode = Vortice.DCommon.AlphaMode;
using D2DColor = Vortice.Mathematics.Color;
using D2DRect = Vortice.Mathematics.Rect;
using GdiColor = System.Drawing.Color;
using GdiPointF = System.Drawing.PointF;
using GdiRectangleF = System.Drawing.RectangleF;
using GdiSizeF = System.Drawing.SizeF;

namespace VectorAnimationEngine;

internal sealed partial class Direct2DStageRenderer
{
    private const int DenseReference3DRenderItemThreshold = 96;
    private const float DenseReference3DAffineErrorPixels = 1.25f;
    private const int MaximumReference3DLocalLightBrushProfiles = 256;
    private const int MaximumReference3DLocalPathGeometryEntries = 2048;
    private const int MaximumReference3DOpticalSurfaceBitmaps = 256;
    private const long MaximumReference3DOpticalSurfaceBitmapBytes = 128L * 1024 * 1024;
    private readonly Dictionary<Reference3DLocalPathGeometryKey, List<CachedReference3DLocalPathGeometry>>
        _reference3DLocalPathGeometryCache = new();
    private readonly Dictionary<GradientStop[], CachedReference3DLocalLightBrush>
        _reference3DLocalLightBrushes = new(GradientStopProfileComparer.Instance);
    private readonly Dictionary<Reference3DOpticalSurface, ID2D1Bitmap>
        _reference3DOpticalSurfaceBitmaps = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Reference3DMaterialBitmapCacheKey, List<CachedReference3DMaterialBitmap>>
        _reference3DMaterialBitmapCache = new();
    private readonly List<CachedReference3DMaterialBitmap>
        _transientReference3DMaterialBitmaps = [];
    private bool _reference3DDenseRenderMode;
    private Dictionary<int, Reference3DRenderItem>? _reference3DStrokePairs;
    private Dictionary<int, Reference3DRenderItem>? _reference3DFillItems;
    private long _reference3DMaterialBitmapCacheBytes;
    private int _reference3DMaterialBitmapCacheEntryCount;
    private long _reference3DOpticalSurfaceBitmapBytes;
    private bool _reference3DOpticalSurfaceBitmapCacheInvalidated;

    private readonly record struct Reference3DLocalPathGeometryKey(
        ulong Hash,
        int ContourCount,
        int PointCount,
        bool UsesBezier,
        bool Open);

    private readonly record struct Reference3DMaterialBitmapCacheKey(
        VectorScene Scene,
        ulong GeometryHash,
        bool UsesLocalPathGeometry);


    private sealed class CachedReference3DLocalPathGeometry(
        Reference3DLocalPathGeometryKey key,
        GdiPointF[][]? polygonContours,
        PathBezierNode[][]? bezierContours,
        ID2D1PathGeometry geometry) : IDisposable
    {
        public Reference3DLocalPathGeometryKey Key { get; } = key;
        public GdiPointF[][]? PolygonContours { get; } = polygonContours;
        public PathBezierNode[][]? BezierContours { get; } = bezierContours;
        public ID2D1PathGeometry Geometry { get; } = geometry;

        public bool Matches(
            Reference3DLocalPathGeometryKey key,
            GdiPointF[][]? polygonContours,
            PathBezierNode[][]? bezierContours)
        {
            return Key == key
                && PolygonContoursEqual(PolygonContours, polygonContours)
                && BezierContoursEqual(BezierContours, bezierContours);
        }

        public void Dispose() => Geometry.Dispose();

        private static bool PolygonContoursEqual(
            GdiPointF[][]? left,
            GdiPointF[][]? right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left is null || right is null || left.Length != right.Length) return false;
            for (var contourIndex = 0; contourIndex < left.Length; contourIndex++)
            {
                var leftContour = left[contourIndex];
                var rightContour = right[contourIndex];
                if (leftContour.Length != rightContour.Length) return false;
                for (var pointIndex = 0; pointIndex < leftContour.Length; pointIndex++)
                {
                    if (!leftContour[pointIndex].Equals(rightContour[pointIndex])) return false;
                }
            }

            return true;
        }

        private static bool BezierContoursEqual(
            PathBezierNode[][]? left,
            PathBezierNode[][]? right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left is null || right is null || left.Length != right.Length) return false;
            for (var contourIndex = 0; contourIndex < left.Length; contourIndex++)
            {
                var leftContour = left[contourIndex];
                var rightContour = right[contourIndex];
                if (leftContour.Length != rightContour.Length) return false;
                for (var nodeIndex = 0; nodeIndex < leftContour.Length; nodeIndex++)
                {
                    if (!leftContour[nodeIndex].Equals(rightContour[nodeIndex])) return false;
                }
            }

            return true;
        }
    }

    private sealed class CachedReference3DMaterialBitmap(
        GradientKind kind,
        GradientStop[] stops,
        Vector2 gradientStart,
        Vector2 gradientEnd,
        GdiRectangleF sourceBounds,
        float rasterScale,
        int pixelWidth,
        int pixelHeight,
        Matrix3x2 bitmapToSourceTransform,
        ID2D1PathGeometry? geometryIdentity,
        Reference3DSourceContour[]? sourceContours,
        bool bakesStroke,
        int strokeArgb,
        float strokeWidth,
        ID2D1Bitmap bitmap) : IDisposable
    {
        public GradientKind Kind { get; } = kind;
        public GradientStop[] Stops { get; } = stops;
        public Vector2 GradientStart { get; } = gradientStart;
        public Vector2 GradientEnd { get; } = gradientEnd;
        public GdiRectangleF SourceBounds { get; } = sourceBounds;
        public float RasterScale { get; } = rasterScale;
        public int PixelWidth { get; } = pixelWidth;
        public int PixelHeight { get; } = pixelHeight;
        public Matrix3x2 BitmapToSourceTransform { get; } = bitmapToSourceTransform;
        public ID2D1PathGeometry? GeometryIdentity { get; } = geometryIdentity;
        public Reference3DSourceContour[]? SourceContours { get; } = sourceContours;
        public bool BakesStroke { get; } = bakesStroke;
        public int StrokeArgb { get; } = strokeArgb;
        public float StrokeWidth { get; } = strokeWidth;
        public ID2D1Bitmap Bitmap { get; } = bitmap;

        public long ByteSize => (long)PixelWidth * PixelHeight * sizeof(int);

        public bool Matches(
            GradientKind kind,
            IReadOnlyList<GradientStop> stops,
            Vector2 gradientStart,
            Vector2 gradientEnd,
            GdiRectangleF sourceBounds,
            ID2D1PathGeometry? geometryIdentity,
            IReadOnlyList<Reference3DSourceContour>? sourceContours,
            bool bakesStroke,
            int strokeArgb,
            float strokeWidth)
        {
            if (Kind != kind
                || !Stops.SequenceEqual(stops)
                || GradientStart != gradientStart
                || GradientEnd != gradientEnd
                || SourceBounds != sourceBounds
                || BakesStroke != bakesStroke
                || bakesStroke
                    && (StrokeArgb != strokeArgb
                        || Math.Abs(StrokeWidth - strokeWidth) > 0.0001f))
            {
                return false;
            }

            // The bitmap is stored in source-local coordinates. Perspective
            // motion changes the requested screen raster scale and dimensions,
            // but it does not change the material or the local silhouette. Reuse
            // the source bitmap and let the final DrawBitmap transform perform
            // the frame-specific sampling; geometry/material changes still
            // invalidate through the checks above.

            if (GeometryIdentity is not null || geometryIdentity is not null)
            {
                return ReferenceEquals(GeometryIdentity, geometryIdentity);
            }

            return SourceContoursEqual(SourceContours, sourceContours);
        }

        public bool MatchesGeometry(
            ID2D1PathGeometry? geometryIdentity,
            IReadOnlyList<Reference3DSourceContour>? sourceContours)
        {
            if (!BakesStroke) return false;
            if (GeometryIdentity is not null || geometryIdentity is not null)
            {
                return ReferenceEquals(GeometryIdentity, geometryIdentity);
            }

            return SourceContoursEqual(SourceContours, sourceContours);
        }

        public void Dispose()
        {
            Bitmap.Dispose();
        }

        private static bool SourceContoursEqual(
            IReadOnlyList<Reference3DSourceContour>? left,
            IReadOnlyList<Reference3DSourceContour>? right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left is null || right is null || left.Count != right.Count) return false;
            for (var contourIndex = 0; contourIndex < left.Count; contourIndex++)
            {
                var leftContour = left[contourIndex];
                var rightContour = right[contourIndex];
                if (leftContour.Closed != rightContour.Closed
                    || !leftContour.Points.AsSpan().SequenceEqual(rightContour.Points))
                {
                    return false;
                }
            }

            return true;
        }
    }

    private sealed class GradientStopProfileComparer : IEqualityComparer<GradientStop[]>
    {
        public static GradientStopProfileComparer Instance { get; } = new();

        public bool Equals(GradientStop[]? left, GradientStop[]? right) =>
            ReferenceEquals(left, right)
            || left is not null && right is not null && left.AsSpan().SequenceEqual(right);

        public int GetHashCode(GradientStop[] stops)
        {
            var hash = new HashCode();
            foreach (var stop in stops) hash.Add(stop);
            return hash.ToHashCode();
        }
    }

    private sealed class CachedReference3DLocalLightBrush(
        ID2D1GradientStopCollection collection,
        ID2D1RadialGradientBrush brush) : IDisposable
    {
        public ID2D1GradientStopCollection Collection { get; } = collection;
        public ID2D1RadialGradientBrush Brush { get; } = brush;

        public void Dispose()
        {
            Brush.Dispose();
            Collection.Dispose();
        }
    }

    private RenderStats DrawReference3DScene(StageControl stage)
    {
        var editableScene = stage.Scene;
        var onionSkinStats = default(RenderStats);
        var underlayStats = default(RenderStats);
        try
        {
            if (stage.OnionSkinScene is { } onionSkin)
            {
                stage.Scene = onionSkin;
                stage.RecordOnionSkinScenePass();
                onionSkinStats = DrawReference3DCurrentScene(stage);
            }

            if (stage.UnderlayScene is { } underlay)
            {
                stage.Scene = underlay;
                stage.RecordUnderlayScenePass();
                underlayStats = DrawReference3DCurrentScene(stage);
            }

            stage.Scene = editableScene;
            stage.RecordEditableScenePass();
            var editableStats = DrawReference3DCurrentScene(stage);
            return RenderStats.Combine(
                RenderStats.Combine(onionSkinStats, underlayStats),
                editableStats);
        }
        finally
        {
            stage.Scene = editableScene;
        }
    }

    private RenderStats DrawReference3DCurrentScene(StageControl stage)
    {
        var scene = stage.Scene;
        var visible = 0;
        long atoms = 0;
        for (var layer = scene.LayerCount - 1; layer >= 0; layer--)
        {
            if (!scene.ShouldRenderLayerContent(layer)) continue;
            var objects = stage.GetReference3DLayerObjects(layer);
            if (objects.Length == 0) continue;
            visible += objects.Length;
            foreach (var objectIndex in objects) atoms += scene.AtomCount[objectIndex];
        }

        var maskGeometries = new Dictionary<int, ID2D1PathGeometry?>();
        var drawnObjects = new HashSet<int>();
        var planStarted = Stopwatch.GetTimestamp();
        var renderItems = stage.GetReference3DSceneRenderItems();
        LastReference3DPlanLookupMilliseconds +=
            Stopwatch.GetElapsedTime(planStarted).TotalMilliseconds;
        var previousDenseRenderMode = _reference3DDenseRenderMode;
        _reference3DDenseRenderMode = renderItems.Length >= DenseReference3DRenderItemThreshold;
        if (TryDrawReference3DCpuRaster(stage, renderItems, out var cpuDrawnObjects))
        {
            _reference3DDenseRenderMode = previousDenseRenderMode;
            return new RenderStats(visible, cpuDrawnObjects, atoms, 0, scene.ObjectCount, false);
        }
        using var maskTargetLayer = _target!.CreateLayer();
        using var secondaryMaskTargetLayer = renderItems.Any(item =>
                item.SecondaryObjectIndex >= 0
                && item.SecondaryObjectIndex != item.ObjectIndex)
            ? _target.CreateLayer()
            : null;
        using var fragmentTargetLayer = renderItems.Any(item => item.FragmentClip is not null)
            ? _target.CreateLayer()
            : null;
        using var opacityTargetLayer = renderItems.Any(item => item.MaterialOpacity < 0.999999f)
            ? _target.CreateLayer()
            : null;
        var strokePairs = new Dictionary<int, Reference3DRenderItem>();
        var fillItems = new Dictionary<int, Reference3DRenderItem>();
        foreach (var item in renderItems)
        {
            if (item.Kind == Reference3DRenderKind.FrontFill)
            {
                fillItems[item.ObjectIndex] = item;
            }
            else if (item.Kind == Reference3DRenderKind.FrontStroke)
            {
                strokePairs[item.ObjectIndex] = item;
            }
        }
        try
        {
            _reference3DStrokePairs = strokePairs;
            _reference3DFillItems = fillItems;
            for (var itemIndex = 0; itemIndex < renderItems.Length; itemIndex++)
            {
                if (renderItems[itemIndex].Kind == Reference3DRenderKind.FrontStroke
                    && TryDrawReference3DFrontStrokeBatch(
                        stage,
                        renderItems,
                        itemIndex,
                        out var consumed,
                        out var batchedObjectIndices))
                {
                    foreach (var objectIndex in batchedObjectIndices)
                    {
                        drawnObjects.Add(objectIndex);
                    }
                    itemIndex += consumed - 1;
                    continue;
                }

                var item = renderItems[itemIndex];
                if (DrawReference3DSceneItem(
                        stage,
                        item,
                        maskGeometries,
                        maskTargetLayer,
                        secondaryMaskTargetLayer,
                        fragmentTargetLayer,
                        opacityTargetLayer))
                {
                    drawnObjects.Add(item.ObjectIndex);
                }
            }
        }
        finally
        {
            _reference3DDenseRenderMode = previousDenseRenderMode;
            _reference3DStrokePairs = null;
            _reference3DFillItems = null;
            foreach (var geometry in maskGeometries.Values) geometry?.Dispose();
        }
        var drawn = drawnObjects.Count;
        return new RenderStats(visible, drawn, atoms, 0, scene.ObjectCount, false);
    }

    private bool TryDrawReference3DFrontStrokeBatch(
        StageControl stage,
        IReadOnlyList<Reference3DRenderItem> renderItems,
        int start,
        out int consumed,
        out int[] batchedObjectIndices)
    {
        consumed = 0;
        batchedObjectIndices = [];
        if ((uint)start >= (uint)renderItems.Count
            || renderItems[start].Kind != Reference3DRenderKind.FrontStroke)
        {
            return false;
        }

        var scene = stage.Scene;
        var objectIndices = new List<int>();
        var contoursByWidth = new Dictionary<float, List<Reference3DProjectedContour>>();
        var strokeArgb = 0;
        for (var index = start; index < renderItems.Count; index++)
        {
            var item = renderItems[index];
            if (item.Kind != Reference3DRenderKind.FrontStroke
                || !CanBatchReference3DFrontStroke(stage, item, out var itemArgb, out var itemWidth))
            {
                break;
            }
            if (objectIndices.Count == 0)
            {
                strokeArgb = itemArgb;
            }
            else if (itemArgb != strokeArgb)
            {
                break;
            }

            objectIndices.Add(item.ObjectIndex);
            if (!contoursByWidth.TryGetValue(itemWidth, out var contours))
            {
                contours = [];
                contoursByWidth.Add(itemWidth, contours);
            }
            contours.AddRange(item.Contours);
        }

        if (objectIndices.Count < 2 || contoursByWidth.Count == 0) return false;

        foreach (var (width, contours) in contoursByWidth)
        {
            using var geometry = _factory!.CreatePathGeometry();
            using (var sink = geometry.Open())
            {
                sink.SetFillMode(FillMode.Winding);
                AppendReference3DGeometry(sink, contours, fillOnly: false);
                sink.Close();
            }
            _target!.DrawGeometry(geometry, BrushFor(strokeArgb), width, RoundStrokeStyle());
            LastReference3DStrokeBatchSubmissions++;
        }
        LastReference3DStrokeBatchObjects += objectIndices.Count;
        consumed = objectIndices.Count;
        batchedObjectIndices = objectIndices.ToArray();
        return true;
    }

    private static bool CanBatchReference3DFrontStroke(
        StageControl stage,
        Reference3DRenderItem item,
        out int strokeArgb,
        out float strokeWidth)
    {
        strokeArgb = 0;
        strokeWidth = 0;
        var scene = stage.Scene;
        var objectIndex = item.ObjectIndex;
        if ((uint)objectIndex >= (uint)scene.ObjectCount
            || stage.IsObjectHiddenForRendering(scene, objectIndex)
            || item.Contours.Length == 0
            || item.FragmentClip is not null
            || item.SecondaryObjectIndex >= 0
            || item.MaterialOpacity < 0.999999f
            || item.OpticalSurface is not null
            || item.OcclusionContours is not null
            || HasReference3DStrokeOpticalLayers(item))
        {
            return false;
        }

        var shape = scene.ShapeKind[objectIndex];
        if (shape is ShapeKind.Line
            or ShapeKind.ImportedSvg
            or ShapeKind.MixingStroke
            or ShapeKind.Bitmap
            or ShapeKind.Text
            || !SceneRenderOrder.HasStroke(shape, scene.Stroke[objectIndex])
            || scene.GetLayerKind(item.LayerIndex) == DrawingLayerKind.Mask
            || scene.TryGetMaskLayerIndex(item.LayerIndex, out _))
        {
            return false;
        }

        strokeArgb = item.SolidStrokeOpticalBaseArgb
            ?? (scene.StrokeArgb.Length > objectIndex
                ? scene.StrokeArgb[objectIndex]
                : GdiColor.FromArgb(238, 242, 241).ToArgb());
        if (GdiColor.FromArgb(strokeArgb).A != 255) return false;
        strokeWidth = stage.GetReference3DStrokeWidth(objectIndex, scene.Stroke[objectIndex]);
        return float.IsFinite(strokeWidth) && strokeWidth > 0;
    }

    private bool DrawReference3DSceneItem(
        StageControl stage,
        Reference3DRenderItem item,
        IDictionary<int, ID2D1PathGeometry?> maskGeometries,
        ID2D1Layer maskTargetLayer,
        ID2D1Layer? secondaryMaskTargetLayer,
        ID2D1Layer? fragmentTargetLayer,
        ID2D1Layer? opacityTargetLayer)
    {
        var scene = stage.Scene;
        if ((uint)item.ObjectIndex >= scene.ObjectCount
            || stage.IsObjectHiddenForRendering(scene, item.ObjectIndex))
        {
            return false;
        }

        if (!TryGetReference3DLayerMaskGeometry(
                stage,
                item.LayerIndex,
                maskGeometries,
                out var maskGeometry))
        {
            return false;
        }
        ID2D1PathGeometry? secondaryMaskGeometry = null;
        if (item.SecondaryObjectIndex >= 0
            && item.SecondaryObjectIndex != item.ObjectIndex)
        {
            if ((uint)item.SecondaryObjectIndex >= scene.ObjectCount
                || stage.IsObjectHiddenForRendering(scene, item.SecondaryObjectIndex)
                || !TryGetReference3DLayerMaskGeometry(
                    stage,
                    scene.ObjectLayer[item.SecondaryObjectIndex],
                    maskGeometries,
                    out secondaryMaskGeometry))
            {
                return false;
            }
            if (ReferenceEquals(secondaryMaskGeometry, maskGeometry)) secondaryMaskGeometry = null;
        }
        var fragmentClip = item.FragmentClip;
        if (maskGeometry is null && secondaryMaskGeometry is null && fragmentClip is null)
        {
            return DrawReference3DSceneItemWithOpacity(
                item.MaterialOpacity,
                opacityTargetLayer,
                () => DrawReference3DSceneItemUnmasked(stage, item));
        }

        using var fragmentGeometry = fragmentClip is null
            ? null
            : CreateReference3DGeometry(fragmentClip, fillOnly: true);
        if (fragmentClip is not null && fragmentGeometry is null) return false;
        return DrawReference3DSceneItemWithOpacity(
            item.MaterialOpacity,
            opacityTargetLayer,
            () => DrawReference3DSceneItemWithGeometricClip(
                maskGeometry,
                maskTargetLayer,
                () => DrawReference3DSceneItemWithGeometricClip(
                    secondaryMaskGeometry,
                    secondaryMaskTargetLayer,
                    () => DrawReference3DSceneItemWithGeometricClip(
                        fragmentGeometry,
                        fragmentTargetLayer,
                        () => DrawReference3DSceneItemUnmasked(stage, item),
                        AntialiasMode.Aliased))));
    }

    private bool DrawReference3DSceneItemWithOpacity(
        float opacity,
        ID2D1Layer? targetLayer,
        Func<bool> draw)
    {
        opacity = float.IsFinite(opacity) ? Math.Clamp(opacity, 0f, 1f) : 1f;
        if (opacity >= 0.999999f) return draw();
        if (opacity <= 0f || targetLayer is null) return false;

        var parameters = new LayerParameters
        {
            ContentBounds = Rect(0, 0, _targetSize.Width, _targetSize.Height),
            GeometricMask = null,
            MaskAntialiasMode = AntialiasMode.PerPrimitive,
            MaskTransform = Matrix3x2.Identity,
            Opacity = opacity,
            OpacityBrush = null,
            LayerOptions = LayerOptions.None
        };
        _target!.PushLayer(parameters, targetLayer);
        try
        {
            return draw();
        }
        finally
        {
            _target.PopLayer();
        }
    }

    private bool DrawReference3DSceneItemWithGeometricClip(
        ID2D1PathGeometry? geometry,
        ID2D1Layer? targetLayer,
        Func<bool> draw,
        AntialiasMode maskAntialiasMode = AntialiasMode.PerPrimitive)
    {
        if (geometry is null) return draw();
        if (targetLayer is null) return false;

        var parameters = new LayerParameters
        {
            ContentBounds = Rect(0, 0, _targetSize.Width, _targetSize.Height),
            GeometricMask = geometry,
            MaskAntialiasMode = maskAntialiasMode,
            MaskTransform = Matrix3x2.Identity,
            Opacity = 1f,
            OpacityBrush = null,
            LayerOptions = LayerOptions.None
        };
        _target!.PushLayer(parameters, targetLayer);
        try
        {
            return draw();
        }
        finally
        {
            _target.PopLayer();
        }
    }

    private bool TryGetReference3DLayerMaskGeometry(
        StageControl stage,
        int layer,
        IDictionary<int, ID2D1PathGeometry?> maskGeometries,
        out ID2D1PathGeometry? maskGeometry)
    {
        var scene = stage.Scene;
        maskGeometry = null;
        if (scene.GetLayerKind(layer) == DrawingLayerKind.Mask
            || !scene.TryGetMaskLayerIndex(layer, out var maskLayer))
        {
            return true;
        }
        if (!scene.IsLayerEffectivelyVisible(maskLayer)) return false;

        if (!maskGeometries.TryGetValue(layer, out maskGeometry))
        {
            maskGeometry = CreateReference3DMaskGeometry(
                stage,
                stage.GetReference3DLayerObjects(maskLayer));
            maskGeometries[layer] = maskGeometry;
        }
        return maskGeometry is not null;
    }

    private bool DrawReference3DSceneItemUnmasked(
        StageControl stage,
        Reference3DRenderItem item)
    {
        if (TryDrawGpuOpticalSurface(stage, item)) return true;
        if (item.OpticalSurface is { } opticalSurface
            && item.Kind is Reference3DRenderKind.Back
                or Reference3DRenderKind.Side
                or Reference3DRenderKind.FrontFill
                or Reference3DRenderKind.FrontStroke)
        {
            var surfaceDrawn = false;
            DrawWithSceneCompositionMaskClips(
                stage,
                item.ObjectIndex,
                () =>
                {
                    DrawReference3DOpticalSurface(opticalSurface);
                    surfaceDrawn = true;
                },
                reference3D: true);
            return surfaceDrawn;
        }

        switch (item.Kind)
        {
            case Reference3DRenderKind.FrontFill:
                DrawReference3DObject(stage, item, SceneRenderPass.Fill);
                return true;
            case Reference3DRenderKind.FrontStroke:
                DrawReference3DObject(stage, item, SceneRenderPass.Stroke);
                return true;
            case Reference3DRenderKind.Back:
            case Reference3DRenderKind.Side:
                DrawReference3DExtrusionSurface(stage, item);
                return true;
            case Reference3DRenderKind.IntersectionEdge:
                return DrawReference3DIntersectionEdgeWithCompositionMasks(stage, item);
            case Reference3DRenderKind.Outline:
                var objectDrawn = false;
                var outlineColor = stage.Scene.GetEffectiveLayerOutlineColor(item.LayerIndex);
                DrawWithSceneCompositionMaskClips(
                    stage,
                    item.ObjectIndex,
                    () => objectDrawn = DrawReference3DObjectOutline(
                        stage,
                        item.ObjectIndex,
                        outlineColor),
                    reference3D: true);
                return objectDrawn;
            default:
                return false;
        }
    }

    private void DrawReference3DOpticalSurface(Reference3DOpticalSurface surface)
    {
        var bitmap = Reference3DOpticalSurfaceBitmap(surface);
        var interpolation = surface.PixelWidth == surface.Bounds.Width
            && surface.PixelHeight == surface.Bounds.Height
                ? BitmapInterpolationMode.NearestNeighbor
                : BitmapInterpolationMode.Linear;
        var previousTransform = _target!.Transform;
        try
        {
            _target.Transform = Matrix3x2.Identity;
            _target.DrawBitmap(
                bitmap,
                Rect(
                    surface.Bounds.Left,
                    surface.Bounds.Top,
                    surface.Bounds.Width,
                    surface.Bounds.Height),
                1f,
                interpolation,
                Rect(0, 0, surface.PixelWidth, surface.PixelHeight));
        }
        finally
        {
            _target.Transform = previousTransform;
        }
    }

    private ID2D1Bitmap Reference3DOpticalSurfaceBitmap(Reference3DOpticalSurface surface)
    {
        if (_reference3DOpticalSurfaceBitmaps.TryGetValue(surface, out var bitmap)) return bitmap;
        while (_reference3DOpticalSurfaceBitmaps.Count >= MaximumReference3DOpticalSurfaceBitmaps
               || _reference3DOpticalSurfaceBitmaps.Count > 0
               && _reference3DOpticalSurfaceBitmapBytes + surface.GpuByteSize
                   > MaximumReference3DOpticalSurfaceBitmapBytes)
        {
            var stale = _reference3DOpticalSurfaceBitmaps.First();
            _reference3DOpticalSurfaceBitmaps.Remove(stale.Key);
            _reference3DOpticalSurfaceBitmapBytes -= stale.Key.GpuByteSize;
            stale.Value.Dispose();
        }

        var handle = GCHandle.Alloc(surface.PremultipliedPixels, GCHandleType.Pinned);
        try
        {
            var properties = new BitmapProperties(
                new PixelFormat(Format.B8G8R8A8_UNorm, DCommonAlphaMode.Premultiplied),
                96,
                96);
            bitmap = _target!.CreateBitmap(
                new SizeI(surface.PixelWidth, surface.PixelHeight),
                handle.AddrOfPinnedObject(),
                (uint)(surface.PixelWidth * sizeof(int)),
                properties);
        }
        finally
        {
            handle.Free();
        }
        _reference3DOpticalSurfaceBitmaps.Add(surface, bitmap);
        _reference3DOpticalSurfaceBitmapBytes += surface.GpuByteSize;
        return bitmap;
    }

    private void ClearReference3DOpticalSurfaceBitmapCache()
    {
        foreach (var bitmap in _reference3DOpticalSurfaceBitmaps.Values) bitmap.Dispose();
        _reference3DOpticalSurfaceBitmaps.Clear();
        _reference3DOpticalSurfaceBitmapBytes = 0;
        _reference3DOpticalSurfaceBitmapCacheInvalidated = false;
    }

    internal void InvalidateReference3DOpticalSurfaceBitmapCache() =>
        _reference3DOpticalSurfaceBitmapCacheInvalidated = true;

    private void ApplyReference3DOpticalSurfaceBitmapCacheInvalidation()
    {
        if (_reference3DOpticalSurfaceBitmapCacheInvalidated)
        {
            ClearReference3DOpticalSurfaceBitmapCache();
        }
    }

    private void DrawReference3DLayer(
        StageControl stage,
        int layer,
        IReadOnlyList<int> objects,
        ref int drawn)
    {
        var scene = stage.Scene;
        if (scene.GetLayerKind(layer) == DrawingLayerKind.Mask)
        {
            DrawReference3DLayerUnmasked(stage, layer, objects, ref drawn);
            return;
        }
        if (!scene.TryGetMaskLayerIndex(layer, out var maskLayer))
        {
            DrawReference3DLayerUnmasked(stage, layer, objects, ref drawn);
            return;
        }
        if (!scene.IsLayerEffectivelyVisible(maskLayer)) return;
        using var mask = CreateReference3DMaskGeometry(stage, stage.GetReference3DLayerObjects(maskLayer));
        if (mask is null) return;
        using var targetLayer = _target!.CreateLayer();
        var parameters = new LayerParameters
        {
            ContentBounds = Rect(0, 0, _targetSize.Width, _targetSize.Height),
            GeometricMask = mask,
            MaskAntialiasMode = AntialiasMode.PerPrimitive,
            MaskTransform = Matrix3x2.Identity,
            Opacity = 1f,
            OpacityBrush = null,
            LayerOptions = LayerOptions.None
        };
        _target.PushLayer(parameters, targetLayer);
        try
        {
            DrawReference3DLayerUnmasked(stage, layer, objects, ref drawn);
        }
        finally
        {
            _target.PopLayer();
        }
    }

    private void DrawReference3DLayerUnmasked(
        StageControl stage,
        int layer,
        IReadOnlyList<int> objects,
        ref int drawn)
    {
        var scene = stage.Scene;
        var outlineColor = scene.GetEffectiveLayerOutlineColor(layer);
        if (!outlineColor.IsEmpty)
        {
            foreach (var objectIndex in objects)
            {
                var objectDrawn = false;
                DrawWithSceneCompositionMaskClips(
                    stage,
                    objectIndex,
                    () => objectDrawn = DrawReference3DObjectOutline(stage, objectIndex, outlineColor),
                    reference3D: true);
                if (objectDrawn) drawn++;
            }
            return;
        }
        foreach (var item in stage.GetReference3DLayerRenderItems(objects))
        {
            switch (item.Kind)
            {
                case Reference3DRenderKind.FrontFill:
                    DrawReference3DObject(stage, item, SceneRenderPass.Fill);
                    break;
                case Reference3DRenderKind.FrontStroke:
                    DrawReference3DObject(stage, item, SceneRenderPass.Stroke);
                    break;
                default:
                    DrawReference3DExtrusionSurface(stage, item);
                    break;
            }
        }
        foreach (var objectIndex in objects)
        {
            if (!stage.IsObjectHiddenForRendering(scene, objectIndex)) drawn++;
        }
    }

    private void DrawReference3DExtrusionSurface(
        StageControl stage,
        Reference3DRenderItem item)
    {
        DrawWithSceneCompositionMaskClips(
            stage,
            item.ObjectIndex,
            () =>
            {
                using var geometry = CreateReference3DGeometry(item.Contours, fillOnly: true);
                if (geometry is null) return;
                FillAntialiasedGeometry(
                    geometry,
                    BrushFor(item.VectorLightingArgb
                        ?? stage.GetReference3DExtrusionSurfaceColor(item).ToArgb()));
                if (item.VectorLightingArgb is null)
                {
                    DrawReference3DOpticalFinish(item, geometry);
                }
            },
            reference3D: true);
    }

    private void DrawReference3DOpticalFinish(
        Reference3DRenderItem item,
        ID2D1PathGeometry? surfaceGeometry = null)
    {
        if (item.VectorLightingArgb is not null
            || !HasReference3DOpticalFinish(item))
        {
            return;
        }
        var ownsSurfaceGeometry = surfaceGeometry is null;
        var resolvedSurfaceGeometry = surfaceGeometry
            ?? CreateReference3DGeometry(item.Contours, fillOnly: true);
        if (resolvedSurfaceGeometry is null) return;
        try
        {
            var shade = GdiColor.FromArgb(item.OpticalResponse.ShadeArgb);
            if (shade.A > 0)
            {
                FillAntialiasedGeometry(resolvedSurfaceGeometry, BrushFor(shade.ToArgb()));
            }
            if (item.LocalLightLayers is { Length: > 0 }
                || item.ShadowLayers is { Length: > 0 })
            {
                DrawReference3DSceneItemWithGeometricClip(
                    resolvedSurfaceGeometry,
                    ShapeGradientMaskLayer(),
                    () =>
                    {
                        if (item.LocalLightLayers is { Length: > 0 } localLightLayers)
                        {
                            foreach (var layer in localLightLayers)
                            {
                                using var lightGeometry = CreateReference3DGeometry(
                                    layer.Contours,
                                    fillOnly: true);
                                if (lightGeometry is null) continue;
                                if (layer.DiffuseStops.Any(stop => GdiColor.FromArgb(stop.Argb).A > 0))
                                {
                                    DrawReference3DLocalLightGradient(
                                        lightGeometry,
                                        layer.GradientTransform,
                                        layer.DiffuseStops);
                                }
                                if (layer.SpecularStops.Any(stop => GdiColor.FromArgb(stop.Argb).A > 0))
                                {
                                    DrawReference3DLocalLightGradient(
                                        lightGeometry,
                                        layer.GradientTransform,
                                        layer.SpecularStops);
                                }
                            }
                        }
                        if (item.ShadowLayers is { Length: > 0 } shadowLayers)
                        {
                            foreach (var shadow in shadowLayers)
                            {
                                using var shadowGeometry = CreateReference3DGeometry(
                                    shadow.Contours,
                                    fillOnly: true);
                                if (shadowGeometry is null) continue;
                                FillAntialiasedGeometry(
                                    shadowGeometry,
                                    BrushFor(shadow.Argb));
                            }
                        }
                        return true;
                    },
                    AntialiasMode.Aliased);
            }
            var highlight = GdiColor.FromArgb(item.OpticalResponse.HighlightArgb);
            if (highlight.A > 0)
            {
                FillAntialiasedGeometry(resolvedSurfaceGeometry, BrushFor(highlight.ToArgb()));
            }
        }
        finally
        {
            if (ownsSurfaceGeometry) resolvedSurfaceGeometry.Dispose();
        }
    }

    private void DrawReference3DLocalLightGradient(
        ID2D1Geometry geometry,
        Matrix3x2 transform,
        IReadOnlyList<GradientStop> stops,
        bool reinforceEdge = true)
    {
        if (stops is not GradientStop[] stopArray)
        {
            stopArray = stops.ToArray();
        }
        var brush = Reference3DLocalLightBrush(stopArray);
        brush.Transform = transform;
        if (reinforceEdge)
        {
            FillAntialiasedGeometry(geometry, brush);
        }
        else
        {
            FillReference3DOpticalOverlayGeometry(geometry, brush);
        }
    }

    private void FillReference3DOpticalOverlayGeometry(
        ID2D1Geometry geometry,
        ID2D1Brush brush)
    {
        var antialiasMode = _target!.AntialiasMode;
        try
        {
            _target.AntialiasMode = AntialiasMode.PerPrimitive;
            _target.FillGeometry(geometry, brush);
        }
        finally
        {
            _target.AntialiasMode = antialiasMode;
        }
    }

    private ID2D1RadialGradientBrush Reference3DLocalLightBrush(GradientStop[] stops)
    {
        if (_reference3DLocalLightBrushes.TryGetValue(stops, out var cached))
        {
            return cached.Brush;
        }
        if (_reference3DLocalLightBrushes.Count >= MaximumReference3DLocalLightBrushProfiles)
        {
            ClearReference3DLocalLightBrushCache();
        }

        var gradientStops = stops
            .Select(stop => new Vortice.Direct2D1.GradientStop(
                stop.Position,
                ToColor4(GdiColor.FromArgb(stop.Argb))))
            .ToArray();
        var collection = _target!.CreateGradientStopCollection(
            gradientStops,
            Gamma.StandardRgb,
            ExtendMode.Clamp);
        try
        {
            var brush = _target.CreateRadialGradientBrush(
                new RadialGradientBrushProperties(Vector2.Zero, Vector2.Zero, 1f, 1f),
                new BrushProperties(1f),
                collection);
            cached = new CachedReference3DLocalLightBrush(collection, brush);
            _reference3DLocalLightBrushes.Add(stops, cached);
            return brush;
        }
        catch
        {
            collection.Dispose();
            throw;
        }
    }

    private void ClearReference3DLocalLightBrushCache()
    {
        foreach (var cached in _reference3DLocalLightBrushes.Values) cached.Dispose();
        _reference3DLocalLightBrushes.Clear();
    }

    private bool DrawReference3DIntersectionEdge(Reference3DRenderItem item)
    {
        if (!float.IsFinite(item.EdgeWidth)
            || item.EdgeWidth <= 0
            || GdiColor.FromArgb(item.EdgeArgb).A == 0)
        {
            return false;
        }
        using var geometry = CreateReference3DGeometry(item.Contours, fillOnly: false);
        if (geometry is null) return false;
        _target!.DrawGeometry(
            geometry,
            BrushFor(item.SolidStrokeOpticalBaseArgb ?? item.EdgeArgb),
            item.EdgeWidth,
            LineStrokeStyle(
                item.EdgeStartCap ? CapStyle.Round : CapStyle.Flat,
                item.EdgeEndCap ? CapStyle.Round : CapStyle.Flat,
                miterJoin: false));
        if (item.OpticalSurfaceContours is { Length: > 0 } opticalContours)
        {
            using var opticalGeometry = CreateReference3DGeometry(
                opticalContours,
                fillOnly: true);
            if (opticalGeometry is not null)
            {
                DrawReference3DStrokeOpticalLayers(
                    item,
                    opticalGeometry);
            }
        }
        return true;
    }

    private bool DrawReference3DIntersectionEdgeWithCompositionMasks(
        StageControl stage,
        Reference3DRenderItem item)
    {
        var edgeDrawn = false;
        void DrawEdge() => edgeDrawn = DrawReference3DIntersectionEdge(item);

        DrawWithSceneCompositionMaskClips(
            stage,
            item.ObjectIndex,
            () =>
            {
                if (item.SecondaryObjectIndex >= 0
                    && item.SecondaryObjectIndex != item.ObjectIndex)
                {
                    DrawWithSceneCompositionMaskClips(
                        stage,
                        item.SecondaryObjectIndex,
                        DrawEdge,
                        reference3D: true);
                    return;
                }
                DrawEdge();
            },
            reference3D: true);
        return edgeDrawn;
    }

    private void DrawReference3DObject(
        StageControl stage,
        Reference3DRenderItem item,
        SceneRenderPass pass)
    {
        DrawWithSceneCompositionMaskClips(
            stage,
            item.ObjectIndex,
            () =>
            {
                ID2D1PathGeometry? sharedFillGeometry = null;
                try
                {
                    if (pass == SceneRenderPass.Fill
                        && item.OpticalSurface is null
                        && CanReuseReference3DVectorFillGeometry(stage, item))
                    {
                        sharedFillGeometry = CreateReference3DGeometry(
                            item.Contours,
                            fillOnly: true);
                    }

                    DrawReference3DObjectUnclipped(
                        stage,
                        item,
                        pass,
                        sharedFillGeometry);
                    if (pass == SceneRenderPass.Fill
                        && HasReference3DOpticalFinish(item))
                    {
                        DrawReference3DOpticalFinish(item, sharedFillGeometry);
                    }
                    else if (pass == SceneRenderPass.Stroke
                        && HasReference3DStrokeOpticalLayers(item)
                        && item.OpticalSurfaceContours is { Length: > 0 } opticalContours)
                    {
                        using var opticalGeometry = CreateReference3DGeometry(
                            opticalContours,
                            fillOnly: true);
                        if (opticalGeometry is not null)
                        {
                            DrawReference3DStrokeOpticalLayers(
                                item,
                                opticalGeometry);
                        }
                    }
                }
                finally
                {
                    sharedFillGeometry?.Dispose();
                }
            },
            reference3D: true);
    }

    private static bool CanReuseReference3DVectorFillGeometry(
        StageControl stage,
        Reference3DRenderItem item)
    {
        if (item.Kind != Reference3DRenderKind.FrontFill
            || item.Contours.Length == 0
            || (uint)item.ObjectIndex >= stage.Scene.ObjectCount)
        {
            return false;
        }
        var shape = stage.Scene.ShapeKind[item.ObjectIndex];
        return shape is not (ShapeKind.ImportedSvg or ShapeKind.MixingStroke or ShapeKind.Bitmap)
            && !stage.Scene.HasGradient(item.ObjectIndex);
    }

    private static bool HasReference3DOpticalFinish(Reference3DRenderItem item)
    {
        return GdiColor.FromArgb(item.OpticalResponse.ShadeArgb).A > 0
            || GdiColor.FromArgb(item.OpticalResponse.HighlightArgb).A > 0
            || item.LocalLightLayers is { Length: > 0 }
            || item.ShadowLayers is { Length: > 0 };
    }

    private static bool HasReference3DStrokeOpticalLayers(Reference3DRenderItem item)
    {
        return item.LocalLightLayers is { Length: > 0 }
            || item.ShadowLayers is { Length: > 0 };
    }

    private bool TrySkipReference3DBakedMaterialStroke(
        StageControl stage,
        Reference3DRenderItem item,
        ShapeKind shape)
    {
        LastReference3DBakedStrokeChecks++;
        if (_reference3DStrokePairs is null
            || !_reference3DStrokePairs.ContainsKey(item.ObjectIndex)
            || shape is ShapeKind.Line
                or ShapeKind.ImportedSvg
                or ShapeKind.MixingStroke
                or ShapeKind.Bitmap
                or ShapeKind.Text
            || item.MaterialOpacity < 0.999999f
            || HasReference3DStrokeOpticalLayers(item)
            || !SceneRenderOrder.HasStroke(shape, stage.Scene.Stroke[item.ObjectIndex])
            || !TryGetReference3DBakedMaterialBitmap(stage, item.ObjectIndex, out var cached))
        {
            return false;
        }

        var strokeArgb = item.SolidStrokeOpticalBaseArgb
            ?? (stage.Scene.StrokeArgb.Length > item.ObjectIndex
                ? stage.Scene.StrokeArgb[item.ObjectIndex]
                : GdiColor.FromArgb(238, 242, 241).ToArgb());
        var strokeWidth = stage.GetReference3DSourceStrokeWidth(
            item.ObjectIndex,
            stage.Scene.Stroke[item.ObjectIndex]);
        var canSkip = GdiColor.FromArgb(strokeArgb).A == 255
            && cached.StrokeArgb == strokeArgb
            && float.IsFinite(strokeWidth)
            && Math.Abs(cached.StrokeWidth - strokeWidth) <= 0.0001f;
        if (canSkip) LastReference3DBakedStrokeSkips++;
        return canSkip;
    }

    private bool TryGetReference3DBakedMaterialBitmap(
        StageControl stage,
        int objectIndex,
        out CachedReference3DMaterialBitmap cached)
    {
        cached = null!;
        var scene = stage.Scene;
        var usesLocalPathGeometry = scene.ShapeKind[objectIndex] == ShapeKind.Path
            && !stage.SceneHasDistortionsForRendering(scene);
        var geometryIdentity = usesLocalPathGeometry
            ? Reference3DLocalPathGeometry(scene, objectIndex)
            : null;
        var sourceContours = usesLocalPathGeometry
            ? Array.Empty<Reference3DSourceContour>()
            : stage.GetReference3DSourceContours(objectIndex);
        var key = new Reference3DMaterialBitmapCacheKey(
            scene,
            Reference3DMaterialGeometryHash(
                scene,
                objectIndex,
                usesLocalPathGeometry,
                sourceContours),
            usesLocalPathGeometry);
        if (!_reference3DMaterialBitmapCache.TryGetValue(key, out var candidates)) return false;
        foreach (var candidate in candidates)
        {
            if (!candidate.MatchesGeometry(geometryIdentity, sourceContours)) continue;
            cached = candidate;
            return true;
        }

        return false;
    }

    private void DrawReference3DStrokeOpticalLayers(
        Reference3DRenderItem item,
        ID2D1PathGeometry surfaceGeometry)
    {
        if (!HasReference3DOpticalFinish(item)) return;
        DrawReference3DSceneItemWithGeometricClip(
            surfaceGeometry,
            ShapeGradientMaskLayer(),
            () =>
            {
                foreach (var layer in item.LocalLightLayers ?? [])
                {
                    using var lightGeometry = CreateReference3DGeometry(
                        layer.Contours,
                        fillOnly: true);
                    if (lightGeometry is null) continue;
                    if (layer.SolidStrokeStops is { Length: > 0 } solidStrokeStops)
                    {
                        DrawReference3DLocalLightGradient(
                            lightGeometry,
                            layer.GradientTransform,
                            solidStrokeStops,
                            reinforceEdge: false);
                    }
                    else
                    {
                        if (layer.DiffuseStops.Any(stop => GdiColor.FromArgb(stop.Argb).A > 0))
                        {
                            DrawReference3DLocalLightGradient(
                                lightGeometry,
                                layer.GradientTransform,
                                layer.DiffuseStops,
                                reinforceEdge: false);
                        }
                        if (layer.SpecularStops.Any(stop => GdiColor.FromArgb(stop.Argb).A > 0))
                        {
                            DrawReference3DLocalLightGradient(
                                lightGeometry,
                                layer.GradientTransform,
                                layer.SpecularStops,
                                reinforceEdge: false);
                        }
                    }
                }
                foreach (var shadow in item.ShadowLayers ?? [])
                {
                    using var shadowGeometry = CreateReference3DGeometry(
                        shadow.Contours,
                        fillOnly: true);
                    if (shadowGeometry is null) continue;
                    FillReference3DOpticalOverlayGeometry(
                        shadowGeometry,
                        BrushFor(shadow.Argb));
                }
                return true;
            },
            AntialiasMode.PerPrimitive);
    }

    private void DrawReference3DObjectUnclipped(
        StageControl stage,
        Reference3DRenderItem item,
        SceneRenderPass pass,
        ID2D1PathGeometry? fillGeometry = null)
    {
        var scene = stage.Scene;
        var objectIndex = item.ObjectIndex;
        if (stage.IsObjectHiddenForRendering(scene, objectIndex)) return;
        var shape = scene.ShapeKind[objectIndex];
        if (pass == SceneRenderPass.Stroke
            && TrySkipReference3DBakedMaterialStroke(stage, item, shape))
        {
            return;
        }
        if (shape == ShapeKind.ImportedSvg)
        {
            if (pass == SceneRenderPass.Fill) DrawReference3DImportedSvg(stage, objectIndex);
            return;
        }
        if (shape == ShapeKind.Bitmap)
        {
            if (pass == SceneRenderPass.Fill) DrawReference3DBitmap(stage, objectIndex);
            return;
        }
        if (shape == ShapeKind.MixingStroke)
        {
            if (pass == SceneRenderPass.Fill) DrawReference3DMixingStroke(stage, objectIndex);
            return;
        }
        if (pass == SceneRenderPass.Stroke
            && shape == ShapeKind.Path
            && TryDrawReference3DPathStroke(stage, item))
        {
            return;
        }
        if (scene.HasGradient(objectIndex)
            && TryDrawReference3DFrontGradient(stage, item, shape, pass))
        {
            if (pass == SceneRenderPass.Stroke && shape == ShapeKind.Line)
            {
                var gradientWidth = stage.GetReference3DStrokeWidth(
                    objectIndex,
                    scene.Stroke[objectIndex]);
                DrawReference3DLineEndpointJoins(
                    stage,
                    item,
                    gradientWidth,
                    Reference3DLineEndpointArgb(
                        scene,
                        objectIndex,
                        startEndpoint: true,
                        opticalStops: item.OpticalStrokeGradientStops),
                    Reference3DLineEndpointArgb(
                        scene,
                        objectIndex,
                        startEndpoint: false,
                        opticalStops: item.OpticalStrokeGradientStops));
            }
            return;
        }

        var ownsGeometry = fillGeometry is null;
        var geometry = fillGeometry
            ?? CreateReference3DGeometry(item.Contours, fillOnly: pass == SceneRenderPass.Fill);
        if (geometry is null) return;
        try
        {
            if (pass == SceneRenderPass.Fill)
            {
                FillAntialiasedGeometry(
                    geometry,
                    BrushFor(item.VectorLightingArgb ?? scene.Argb[objectIndex]));
                return;
            }
            var width = stage.GetReference3DStrokeWidth(objectIndex, scene.Stroke[objectIndex]);
            if (width <= 0) return;
            var color = scene.StrokeArgb.Length > objectIndex
                ? scene.StrokeArgb[objectIndex]
                : GdiColor.FromArgb(238, 242, 241).ToArgb();
            if (item.SolidStrokeOpticalBaseArgb is int solidStrokeArgb)
            {
                color = solidStrokeArgb;
            }
            if (shape == ShapeKind.Line)
            {
                DrawReference3DLineStroke(stage, item, BrushFor(color), width);
                DrawReference3DLineEndpointJoins(stage, item, width, color, color);
                return;
            }
            _target!.DrawGeometry(geometry, BrushFor(color), width, RoundStrokeStyle());
        }
        finally
        {
            if (ownsGeometry) geometry.Dispose();
        }
    }

    private bool TryDrawReference3DPathStroke(
        StageControl stage,
        Reference3DRenderItem item)
    {
        var scene = stage.Scene;
        var objectIndex = item.ObjectIndex;
        if ((uint)objectIndex >= scene.ObjectCount
            || stage.SceneHasDistortionsForRendering(scene))
        {
            return false;
        }

        var geometry = Reference3DLocalPathGeometry(scene, objectIndex);
        if (geometry is null) return false;

        var sourceContours = stage.GetReference3DSourceContours(objectIndex);
        if (sourceContours.Length == 0) return false;

        Matrix3x2 projection;
        if (!stage.TryGetReference3DFlatToScreenTransform(objectIndex, out projection))
        {
            if (!stage.TryGetReference3DProjectiveMesh(
                    objectIndex,
                    sourceContours,
                    usePrimaryContourQuad: false,
                    out var triangles)
                || !StageControl.TryGetReference3DProjectiveAffineTransform(
                    triangles,
                    out projection))
            {
                return false;
            }
        }

        var width = stage.GetReference3DSourceStrokeWidth(
            objectIndex,
            scene.Stroke[objectIndex]);
        if (!float.IsFinite(width) || width <= 0) return false;

        var color = scene.StrokeArgb.Length > objectIndex
            ? scene.StrokeArgb[objectIndex]
            : GdiColor.FromArgb(238, 242, 241).ToArgb();
        if (item.SolidStrokeOpticalBaseArgb is int solidStrokeArgb)
        {
            color = solidStrokeArgb;
        }

        var oldTransform = _target!.Transform;
        try
        {
            _target.Transform = ObjectLocalToWorldTransform(scene, objectIndex) * projection;
            _target.DrawGeometry(geometry, BrushFor(color), width, RoundStrokeStyle());
        }
        finally
        {
            _target.Transform = oldTransform;
        }
        return true;
    }

    private void DrawReference3DLineStroke(
        StageControl stage,
        Reference3DRenderItem item,
        ID2D1Brush brush,
        float width)
    {
        var scene = stage.Scene;
        var startStyle = scene.GetLineEndpointStyle(item.ObjectIndex, startEndpoint: true);
        var endStyle = scene.GetLineEndpointStyle(item.ObjectIndex, startEndpoint: false);
        var miterJoin = startStyle == LineEndpointStyle.Sharp
            || endStyle == LineEndpointStyle.Sharp;
        foreach (var contour in item.Contours)
        {
            if (contour.Closed || contour.Points.Length < 2) continue;
            using var geometry = CreateReference3DGeometry([contour], fillOnly: false);
            if (geometry is null) continue;
            _target!.DrawGeometry(
                geometry,
                brush,
                width,
                LineStrokeStyle(
                    contour.HasSourceStart
                        ? LineCapForEndpoint(startStyle)
                        : CapStyle.Flat,
                    contour.HasSourceEnd
                        ? LineCapForEndpoint(endStyle)
                        : CapStyle.Flat,
                    miterJoin));
        }
    }

    private void DrawReference3DLineEndpointJoins(
        StageControl stage,
        Reference3DRenderItem item,
        float width,
        int startArgb,
        int endArgb)
    {
        DrawEndpoint(startEndpoint: true, startArgb);
        DrawEndpoint(startEndpoint: false, endArgb);

        void DrawEndpoint(bool startEndpoint, int argb)
        {
            if (!stage.TryGetReference3DLineEndpointJoin(
                    item.ObjectIndex,
                    startEndpoint,
                    item.Contours,
                    width,
                    out var joint,
                    out var miters,
                    out _))
            {
                return;
            }

            using var path = _factory!.CreatePathGeometry();
            using (var sink = path.Open())
            {
                sink.SetFillMode(FillMode.Winding);
                var vectorJoint = ToVector(joint);
                foreach (var miter in miters)
                {
                    AppendJoin(
                        vectorJoint,
                        ToVector(miter.OuterFirstOffset),
                        ToVector(miter.OuterMiter),
                        ToVector(miter.OuterSecondOffset));
                    AppendJoin(
                        vectorJoint,
                        ToVector(miter.InnerFirstOffset),
                        ToVector(miter.InnerMiter),
                        ToVector(miter.InnerSecondOffset));
                }
                sink.Close();

                void AppendJoin(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
                {
                    sink.BeginFigure(a, FigureBegin.Filled);
                    sink.AddLine(b);
                    sink.AddLine(c);
                    sink.AddLine(d);
                    sink.EndFigure(FigureEnd.Closed);
                }
            }
            _target!.FillGeometry(path, BrushFor(argb));
        }
    }

    private static int Reference3DLineEndpointArgb(
        VectorScene scene,
        int objectIndex,
        bool startEndpoint,
        IReadOnlyList<GradientStop>? opticalStops = null)
    {
        var renderedKind = scene.GetGradientKind(objectIndex) == GradientKind.ShapeRadial
            ? GradientKind.Radial
            : scene.GetGradientKind(objectIndex);
        return StageControl.SampleReference3DLineEndpointGradient(
            scene,
            objectIndex,
            startEndpoint,
            renderedKind,
            opticalStops).ToArgb();
    }

    private bool TryDrawReference3DFrontGradient(
        StageControl stage,
        Reference3DRenderItem item,
        ShapeKind shape,
        SceneRenderPass pass)
    {
        var objectIndex = item.ObjectIndex;
        var gradientPass = pass == SceneRenderPass.Fill && shape != ShapeKind.Line
            || pass == SceneRenderPass.Stroke && shape == ShapeKind.Line;
        if (!gradientPass)
        {
            return false;
        }
        if (!stage.TryGetReference3DFlatToScreenTransform(objectIndex, out var transform))
        {
            return TryDrawReference3DProjectiveFrontGradient(stage, item, pass);
        }

        var scene = stage.Scene;
        var gradientKind = scene.GetGradientKind(objectIndex);
        var needsScreenMask = pass == SceneRenderPass.Fill
            && (gradientKind == GradientKind.ShapeRadial
                || gradientKind == GradientKind.Linear
                    && scene.TryGetGradientPathWorldPoints(objectIndex, out var gradientPath)
                    && gradientPath.Length >= 2
                    && scene.EstimateGradientPathStrokeWidth(objectIndex) > 0);
        using var screenMask = needsScreenMask
            ? CreateReference3DGeometry(item.Contours, fillOnly: true)
            : null;
        if (needsScreenMask && screenMask is null) return false;

        var stops = pass == SceneRenderPass.Stroke
            && item.OpticalStrokeGradientStops is { Length: > 0 } opticalStrokeStops
            ? opticalStrokeStops
            : pass == SceneRenderPass.Fill
                && item.OpticalGradientStops is { Length: > 0 } opticalGradientStops
                ? opticalGradientStops
            : scene.GetGradientStops(objectIndex);
        if (pass == SceneRenderPass.Fill
            && gradientKind == GradientKind.Linear
            && DrawReference3DSourcePathGradientFill(
                stage,
                screenMask!,
                objectIndex,
                stops,
                transform))
        {
            return true;
        }
        if (pass == SceneRenderPass.Fill
            && gradientKind == GradientKind.ShapeRadial
            && DrawReference3DSourceShapeGradientFill(
                stage,
                screenMask!,
                objectIndex,
                stops,
                transform))
        {
            return true;
        }

        var localPathGeometry = shape == ShapeKind.Path
            && !stage.SceneHasDistortionsForRendering(scene)
            ? Reference3DLocalPathGeometry(scene, objectIndex)
            : null;
        using var generatedGeometry = localPathGeometry is null
            ? CreateReference3DSourceGeometry(
                stage.GetReference3DSourceContours(objectIndex),
                fillOnly: pass == SceneRenderPass.Fill)
            : null;
        var geometry = localPathGeometry ?? generatedGeometry;
        if (geometry is null) return false;

        var renderedGradientKind = gradientKind == GradientKind.ShapeRadial
            ? GradientKind.Radial
            : gradientKind;
        var cached = GradientBrush(scene, objectIndex, renderedGradientKind, stops);
        var usesLocalPathGeometry = localPathGeometry is not null;
        cached.SetAxis(
            usesLocalPathGeometry
                ? ObjectWorldToLocal(scene, objectIndex, scene.GetGradientStart(objectIndex))
                : ToVector(scene.GetGradientStart(objectIndex)),
            usesLocalPathGeometry
                ? ObjectWorldToLocal(scene, objectIndex, scene.GetGradientEnd(objectIndex))
                : ToVector(scene.GetGradientEnd(objectIndex)));
        var drawTransform = usesLocalPathGeometry
            ? ObjectLocalToWorldTransform(scene, objectIndex) * transform
            : transform;
        if (pass == SceneRenderPass.Fill
            && TryDrawReference3DMaterialBitmap(
                stage,
                scene,
                objectIndex,
                renderedGradientKind,
                stops,
                cached,
                geometry,
                usesLocalPathGeometry,
                usesLocalPathGeometry
                    ? Array.Empty<Reference3DSourceContour>()
                    : stage.GetReference3DSourceContours(objectIndex),
                drawTransform))
        {
            return true;
        }
        var old = _target!.Transform;
        try
        {
            if (pass == SceneRenderPass.Fill)
            {
                var parameters = new LayerParameters
                {
                    ContentBounds = Rect(0, 0, _targetSize.Width, _targetSize.Height),
                    GeometricMask = screenMask,
                    MaskAntialiasMode = AntialiasMode.PerPrimitive,
                    MaskTransform = Matrix3x2.Identity,
                    Opacity = 1f,
                    OpacityBrush = null,
                    LayerOptions = LayerOptions.None
                };
                using var layer = _target.CreateLayer();
                _target.Transform = Matrix3x2.Identity;
                _target.PushLayer(parameters, layer);
                try
                {
                    _target.Transform = drawTransform;
                    // The projected silhouette is already represented by the source
                    // geometry in this affine path. Per-primitive fill antialiasing
                    // is sufficient and avoids a second gradient submission for
                    // every fragment.
                    _target.FillGeometry(geometry, cached.Brush);
                }
                finally
                {
                    _target.Transform = Matrix3x2.Identity;
                    _target.PopLayer();
                }
            }
            else
            {
                var startStyle = scene.GetLineEndpointStyle(objectIndex, startEndpoint: true);
                var endStyle = scene.GetLineEndpointStyle(objectIndex, startEndpoint: false);
                _target.Transform = drawTransform;
                _target.DrawGeometry(
                    geometry,
                    cached.Brush,
                    stage.GetReference3DSourceStrokeWidth(objectIndex, scene.Stroke[objectIndex]),
                    LineStrokeStyle(
                        LineCapForEndpoint(startStyle),
                        LineCapForEndpoint(endStyle),
                        startStyle == LineEndpointStyle.Sharp
                            || endStyle == LineEndpointStyle.Sharp));
            }
        }
        finally
        {
            _target.Transform = old;
        }
        return true;
    }

    private bool TryDrawReference3DProjectiveFrontGradient(
        StageControl stage,
        Reference3DRenderItem item,
        SceneRenderPass pass)
    {
        var objectIndex = item.ObjectIndex;
        var sourceContours = stage.GetReference3DSourceContours(objectIndex);
        if (!stage.TryGetReference3DProjectiveMesh(
                objectIndex,
                sourceContours,
                usePrimaryContourQuad: false,
                out var triangles)
            || triangles.Length == 0)
        {
            return false;
        }

        var scene = stage.Scene;
        var stops = pass == SceneRenderPass.Stroke
            && item.OpticalStrokeGradientStops is { Length: > 0 } opticalStrokeStops
            ? opticalStrokeStops
            : pass == SceneRenderPass.Fill
                && item.OpticalGradientStops is { Length: > 0 } opticalGradientStops
                ? opticalGradientStops
            : scene.GetGradientStops(objectIndex);
        GradientPathGradientSegment[] pathSegments = [];
        CachedPathGradientBrushes? pathBrushes = null;
        var pathWidth = 0f;
        if (pass == SceneRenderPass.Fill
            && scene.GetGradientKind(objectIndex) == GradientKind.Linear)
        {
            var estimatedWidth = scene.EstimateGradientPathStrokeWidth(objectIndex);
            if (scene.TryGetGradientPathWorldPoints(objectIndex, out var path)
                && path.Length >= 2
                && estimatedWidth is > 0)
            {
                var screenLength = 0f;
                for (var index = 1; index < path.Length; index++)
                {
                    if (stage.TryProjectReference3DFrontPoint(objectIndex, path[index - 1], out var start, out _)
                        && stage.TryProjectReference3DFrontPoint(objectIndex, path[index], out var end, out _))
                    {
                        screenLength += Reference3DDistance(start, end);
                    }
                }
                pathSegments = GradientPaintUtilities.CreatePathGradientSegments(
                    path,
                    stops,
                    Math.Clamp((int)MathF.Ceiling(screenLength / 4f), 8, 256));
                if (pathSegments.Length > 0)
                {
                    pathBrushes = PathGradientBrushes(scene, objectIndex, stops, pathSegments);
                    pathWidth = Math.Max(VectorUnits.MinimumStrokeUnits, estimatedWidth * 1.12f);
                }
            }
        }

        var affineTransform = default(Matrix3x2);
        var canUseAffineProjection = triangles.Length == 2
            && StageControl.Reference3DProjectiveMeshCoversFullDomain(triangles)
            && StageControl.TryGetReference3DFlatToScreenTransform(
                triangles[0],
                out affineTransform);
        var canUseApproximateAffineProjection = !canUseAffineProjection
            && (_reference3DDenseRenderMode
                ? StageControl.TryGetReference3DProjectiveAffineTransform(
                    triangles,
                    DenseReference3DAffineErrorPixels,
                    out affineTransform,
                    out _)
                : StageControl.TryGetReference3DProjectiveAffineTransform(
                    triangles,
                    out affineTransform));
        if (canUseApproximateAffineProjection)
        {
            LastReference3DProjectiveAffineApproximationUses++;
        }
        var useAffineProjection = canUseAffineProjection
            || canUseApproximateAffineProjection;
        var shapeGradient = pass == SceneRenderPass.Fill
            && scene.GetGradientKind(objectIndex) == GradientKind.ShapeRadial
            ? ShapeGradientBitmap(stage, scene, objectIndex, stops)
            : null;
        var localPathGeometry = pathBrushes is null
            && shapeGradient is null
            && scene.ShapeKind[objectIndex] == ShapeKind.Path
            && !stage.SceneHasDistortionsForRendering(scene)
            ? Reference3DLocalPathGeometry(scene, objectIndex)
            : null;
        var useSourceGeometryForAffineFill = useAffineProjection
            && pass == SceneRenderPass.Fill
            && pathBrushes is null
            && shapeGradient is null
            && (!canUseApproximateAffineProjection
                || _reference3DDenseRenderMode);
        using var screenMask = pass == SceneRenderPass.Fill && !useSourceGeometryForAffineFill
            ? CreateReference3DGeometry(item.Contours, fillOnly: true)
            : null;
        if (screenMask is not null) LastReference3DProjectiveScreenMaskBuilds++;
        if (pass == SceneRenderPass.Fill
            && !useSourceGeometryForAffineFill
            && screenMask is null)
        {
            return false;
        }
        var gradientKind = scene.GetGradientKind(objectIndex) == GradientKind.ShapeRadial
            ? GradientKind.Radial
            : scene.GetGradientKind(objectIndex);
        using var generatedMaterialGeometry = pathBrushes is null
                && shapeGradient is null
                && localPathGeometry is null
            ? pass == SceneRenderPass.Fill
                ? useSourceGeometryForAffineFill
                    ? CreateReference3DSourceGeometry(sourceContours, fillOnly: true)
                    : CreateReference3DSourceDomainGeometry(sourceContours)
                : CreateReference3DSourceGeometry(sourceContours, fillOnly: false)
            : null;
        var materialGeometry = localPathGeometry ?? generatedMaterialGeometry;
        if (pathBrushes is null && shapeGradient is null && materialGeometry is null) return false;
        var cached = GradientBrush(scene, objectIndex, gradientKind, stops);
        var usesLocalPathGeometry = localPathGeometry is not null;
        cached.SetAxis(
            usesLocalPathGeometry
                ? ObjectWorldToLocal(scene, objectIndex, scene.GetGradientStart(objectIndex))
                : ToVector(scene.GetGradientStart(objectIndex)),
            usesLocalPathGeometry
                ? ObjectWorldToLocal(scene, objectIndex, scene.GetGradientEnd(objectIndex))
                : ToVector(scene.GetGradientEnd(objectIndex)));
        var affineDrawTransform = usesLocalPathGeometry
            ? ObjectLocalToWorldTransform(scene, objectIndex) * affineTransform
            : affineTransform;

        // The common dense-fracture path is an exact affine projection of a
        // perspective mesh. The material bitmap cache only needs source
        // contours to validate a hit; defer source geometry creation until a
        // cache miss so playback does not rebuild one path per fragment.
        if (pass == SceneRenderPass.Fill
            && useAffineProjection
            && TryDrawReference3DMaterialBitmap(
                stage,
                scene,
                objectIndex,
                cached.Kind,
                cached.Stops,
                cached,
                usesLocalPathGeometry ? localPathGeometry : null,
                usesLocalPathGeometry,
                usesLocalPathGeometry
                    ? Array.Empty<Reference3DSourceContour>()
                    : stage.GetReference3DSourceContours(objectIndex),
                affineDrawTransform))
        {
            return true;
        }

        // The adaptive projective mesh returns two triangles when the whole
        // source domain is within the configured pixel-error tolerance. One
        // affine draw is materially cheaper than pushing a layer per triangle
        // and is indistinguishable at that error bound.
        if (useAffineProjection)
        {
            return DrawReference3DAffineProjectedGradient(
                screenMask,
                affineDrawTransform,
                pathBrushes,
                pathWidth,
                pathSegments,
                shapeGradient,
                materialGeometry,
                cached,
                scene,
                stage,
                objectIndex,
                pass,
                sourceContours,
                usesLocalPathGeometry);
        }

        var drewMaterial = DrawReference3DProjectiveTriangles(
            triangles,
            screenMask,
            triangle =>
            {
                if (!StageControl.TryGetReference3DFlatToScreenTransform(triangle, out var transform))
                {
                    return false;
                }

                var drawTransform = localPathGeometry is null
                    ? transform
                    : ObjectLocalToWorldTransform(scene, objectIndex) * transform;
                _target!.Transform = drawTransform;
                if (pathBrushes is not null)
                {
                    DrawReference3DSourcePathGradientSegments(
                        pathSegments,
                        pathBrushes,
                        pathWidth,
                        triangle);
                    return true;
                }
                if (shapeGradient is not null)
                {
                    var destination = Rect(
                        shapeGradient.WorldBounds.Left,
                        shapeGradient.WorldBounds.Top,
                        shapeGradient.WorldBounds.Width,
                        shapeGradient.WorldBounds.Height);
                    var source = Rect(0, 0, shapeGradient.PixelWidth, shapeGradient.PixelHeight);
                    _target.DrawBitmap(
                        shapeGradient.Bitmap,
                        destination,
                        1f,
                        BitmapInterpolationMode.Linear,
                        source);
                    return true;
                }
                if (pass == SceneRenderPass.Fill)
                {
                    // The screen mask owns silhouette antialiasing. Repeating the
                    // source-edge coverage stroke for every projective triangle
                    // doubles complex gradient geometry work and hardens edges.
                    _target.FillGeometry(materialGeometry!, cached.Brush);
                    LastReference3DProjectiveGradientDomainFills++;
                }
                else
                {
                    var startStyle = scene.GetLineEndpointStyle(objectIndex, startEndpoint: true);
                    var endStyle = scene.GetLineEndpointStyle(objectIndex, startEndpoint: false);
                    _target.DrawGeometry(
                        materialGeometry!,
                        cached.Brush,
                        stage.GetReference3DSourceStrokeWidth(objectIndex, scene.Stroke[objectIndex]),
                        LineStrokeStyle(
                            LineCapForEndpoint(startStyle),
                            LineCapForEndpoint(endStyle),
                            startStyle == LineEndpointStyle.Sharp
                                || endStyle == LineEndpointStyle.Sharp));
                }
                return true;
            });
        return drewMaterial;
    }

    private bool DrawReference3DAffineProjectedGradient(
        ID2D1Geometry? screenMask,
        Matrix3x2 transform,
        CachedPathGradientBrushes? pathBrushes,
        float pathWidth,
        IReadOnlyList<GradientPathGradientSegment> pathSegments,
        CachedShapeGradientBitmap? shapeGradient,
        ID2D1PathGeometry? materialGeometry,
        CachedGradientBrush cached,
        VectorScene scene,
        StageControl stage,
        int objectIndex,
        SceneRenderPass pass,
        IReadOnlyList<Reference3DSourceContour> sourceContours,
        bool usesLocalPathGeometry)
    {
        if (pass == SceneRenderPass.Fill
            && screenMask is null
            && materialGeometry is not null
            && TryDrawReference3DMaterialBitmap(
                stage,
                scene,
                objectIndex,
                cached.Kind,
                cached.Stops,
                cached,
                materialGeometry,
                usesLocalPathGeometry,
                sourceContours,
                transform))
        {
            return true;
        }
        using var layer = screenMask is null ? null : _target!.CreateLayer();
        var oldTransform = _target!.Transform;
        var pushed = false;
        try
        {
            _target.Transform = Matrix3x2.Identity;
            if (screenMask is not null)
            {
                var parameters = new LayerParameters
                {
                    ContentBounds = Rect(0, 0, _targetSize.Width, _targetSize.Height),
                    GeometricMask = screenMask,
                    MaskAntialiasMode = AntialiasMode.PerPrimitive,
                    MaskTransform = Matrix3x2.Identity,
                    Opacity = 1f,
                    OpacityBrush = null,
                    LayerOptions = LayerOptions.None
                };
                _target.PushLayer(parameters, layer!);
                pushed = true;
            }

            _target.Transform = transform;
            if (pathBrushes is not null)
            {
                DrawReference3DSourcePathGradientSegments(
                    pathSegments,
                    pathBrushes,
                    pathWidth);
                return true;
            }

            if (shapeGradient is not null)
            {
                _target.DrawBitmap(
                    shapeGradient.Bitmap,
                    Rect(
                        shapeGradient.WorldBounds.Left,
                        shapeGradient.WorldBounds.Top,
                        shapeGradient.WorldBounds.Width,
                        shapeGradient.WorldBounds.Height),
                    1f,
                    BitmapInterpolationMode.Linear,
                    Rect(0, 0, shapeGradient.PixelWidth, shapeGradient.PixelHeight));
                return true;
            }

            if (pass == SceneRenderPass.Fill)
            {
                // The affine source geometry owns the exact silhouette. Let
                // Direct2D antialias the fill once instead of drawing a second
                // gradient-covered edge pass for every fragment.
                _target.FillGeometry(materialGeometry!, cached.Brush);
                LastReference3DProjectiveGradientDomainFills++;
                return true;
            }

            var startStyle = scene.GetLineEndpointStyle(objectIndex, startEndpoint: true);
            var endStyle = scene.GetLineEndpointStyle(objectIndex, startEndpoint: false);
            _target.DrawGeometry(
                materialGeometry!,
                cached.Brush,
                stage.GetReference3DSourceStrokeWidth(objectIndex, scene.Stroke[objectIndex]),
                LineStrokeStyle(
                    LineCapForEndpoint(startStyle),
                    LineCapForEndpoint(endStyle),
                    startStyle == LineEndpointStyle.Sharp
                        || endStyle == LineEndpointStyle.Sharp));
            return true;
        }
        finally
        {
            _target.Transform = Matrix3x2.Identity;
            if (pushed) _target.PopLayer();
            _target.Transform = oldTransform;
        }
    }

    private bool TryDrawReference3DMaterialBitmap(
        StageControl stage,
        VectorScene scene,
        int objectIndex,
        GradientKind kind,
        IReadOnlyList<GradientStop> stops,
        CachedGradientBrush cachedBrush,
        ID2D1PathGeometry? materialGeometry,
        bool usesLocalPathGeometry,
        IReadOnlyList<Reference3DSourceContour> sourceContours,
        Matrix3x2 drawTransform)
    {
        // Keep the old vector path available for A/B diagnostics. The bitmap
        // cache is deliberately opt-in during this experiment because its
        // first-frame upload cost can otherwise hide the steady-state result.
        if (_reference3DDenseRenderMode
            && !string.Equals(
                Environment.GetEnvironmentVariable("V2D_ENABLE_REFERENCE3D_MATERIAL_BITMAP"),
                "1",
                StringComparison.Ordinal))
        {
            return false;
        }
        if (string.Equals(
                Environment.GetEnvironmentVariable("V2D_SKIP_REFERENCE3D_MATERIAL_BITMAP"),
                "1",
                StringComparison.Ordinal))
        {
            return false;
        }

        if (kind is not (GradientKind.Linear or GradientKind.Radial)
            || stops.Count == 0
            || !TryGetReference3DMaterialBitmapSource(
                scene,
                objectIndex,
                materialGeometry,
                usesLocalPathGeometry,
                sourceContours,
                out var sourceBounds,
                out var geometryIdentity,
                out var sourceContoursIdentity)
            || !TryGetReference3DMaterialBitmapLayout(
                sourceBounds,
                drawTransform,
                out var rasterScale,
                out var pixelWidth,
                out var pixelHeight,
                out var sourceToBitmap,
                out var bitmapToSource))
        {
            return false;
        }

        var gradientStart = usesLocalPathGeometry
            ? ObjectWorldToLocal(scene, objectIndex, scene.GetGradientStart(objectIndex))
            : ToVector(scene.GetGradientStart(objectIndex));
        var gradientEnd = usesLocalPathGeometry
            ? ObjectWorldToLocal(scene, objectIndex, scene.GetGradientEnd(objectIndex))
            : ToVector(scene.GetGradientEnd(objectIndex));
        var bakesStroke = TryGetReference3DMaterialStroke(
            stage,
            scene,
            objectIndex,
            out var strokeArgb,
            out var strokeWidth);
        var key = new Reference3DMaterialBitmapCacheKey(
            scene,
            Reference3DMaterialGeometryHash(
                scene,
                objectIndex,
                usesLocalPathGeometry,
                sourceContours),
            usesLocalPathGeometry);
        if (_reference3DMaterialBitmapCache.TryGetValue(key, out var candidates))
        {
            foreach (var cached in candidates)
            {
                if (!cached.Matches(
                        kind,
                        stops,
                        gradientStart,
                        gradientEnd,
                        sourceBounds,
                        geometryIdentity,
                        sourceContoursIdentity,
                        bakesStroke,
                        strokeArgb,
                        strokeWidth)) continue;

                DrawReference3DMaterialBitmap(cached, drawTransform);
                LastReference3DMaterialBitmapCacheReuses++;
                return true;
            }
        }

        if (materialGeometry is null) return false;

        var bitmapResource = CreateReference3DMaterialBitmap(
            materialGeometry,
            kind,
            stops,
            gradientStart,
            gradientEnd,
            sourceToBitmap,
            pixelWidth,
            pixelHeight,
            bakesStroke,
            strokeArgb,
            strokeWidth);
        if (bitmapResource is null) return false;

        var bitmap = bitmapResource;

        var created = new CachedReference3DMaterialBitmap(
            kind,
            stops.ToArray(),
            gradientStart,
            gradientEnd,
            sourceBounds,
            rasterScale,
            pixelWidth,
            pixelHeight,
            bitmapToSource,
            geometryIdentity,
            sourceContoursIdentity is null
                ? null
                : sourceContoursIdentity
                    .Select(contour => new Reference3DSourceContour(
                        contour.Points.ToArray(),
                        contour.Closed))
                    .ToArray(),
            bakesStroke,
            strokeArgb,
            strokeWidth,
            bitmap);
        LastReference3DMaterialBitmapCacheBuilds++;
        StoreReference3DMaterialBitmap(key, created);
        DrawReference3DMaterialBitmap(created, drawTransform);
        return true;
    }

    private bool TryGetReference3DMaterialStroke(
        StageControl stage,
        VectorScene scene,
        int objectIndex,
        out int strokeArgb,
        out float strokeWidth)
    {
        strokeArgb = 0;
        strokeWidth = 0;
        if (_reference3DFillItems?.TryGetValue(objectIndex, out var fillItem) != true
            || fillItem.MaterialOpacity < 0.999999f
            || _reference3DStrokePairs?.TryGetValue(objectIndex, out var strokeItem) != true
            || !CanBatchReference3DFrontStroke(
                stage,
                strokeItem,
                out strokeArgb,
                out _))
        {
            return false;
        }

        if (!ReferenceEquals(scene, stage.Scene)
            || !scene.HasGradient(objectIndex)
            || scene.ShapeKind[objectIndex] is ShapeKind.Line)
        {
            return false;
        }

        strokeWidth = stage.GetReference3DSourceStrokeWidth(
            objectIndex,
            scene.Stroke[objectIndex]);
        return float.IsFinite(strokeWidth) && strokeWidth > 0;
    }

    private void StoreReference3DMaterialBitmap(
        Reference3DMaterialBitmapCacheKey key,
        CachedReference3DMaterialBitmap bitmap)
    {
        if (bitmap.ByteSize > MaxReference3DMaterialBitmapCacheBytes)
        {
            _transientReference3DMaterialBitmaps.Add(bitmap);
            return;
        }

        while ((_reference3DMaterialBitmapCacheEntryCount
                    >= MaxReference3DMaterialBitmapCacheEntries
                || _reference3DMaterialBitmapCacheEntryCount > 0
                    && _reference3DMaterialBitmapCacheBytes + bitmap.ByteSize
                        > MaxReference3DMaterialBitmapCacheBytes)
            && _reference3DMaterialBitmapCacheEntryCount > 0)
        {
            var oldest = _reference3DMaterialBitmapCache.First(pair => pair.Value.Count > 0);
            var oldestBitmap = oldest.Value[0];
            oldest.Value.RemoveAt(0);
            if (oldest.Value.Count == 0) _reference3DMaterialBitmapCache.Remove(oldest.Key);
            _reference3DMaterialBitmapCacheEntryCount--;
            _reference3DMaterialBitmapCacheBytes -= oldestBitmap.ByteSize;
            oldestBitmap.Dispose();
        }

        if (!_reference3DMaterialBitmapCache.TryGetValue(key, out var candidates))
        {
            candidates = [];
            _reference3DMaterialBitmapCache.Add(key, candidates);
        }
        candidates.Add(bitmap);
        _reference3DMaterialBitmapCacheEntryCount++;
        _reference3DMaterialBitmapCacheBytes += bitmap.ByteSize;
    }

    private void DrawReference3DMaterialBitmap(
        CachedReference3DMaterialBitmap cached,
        Matrix3x2 drawTransform)
    {
        var oldTransform = _target!.Transform;
        try
        {
            _target.Transform = cached.BitmapToSourceTransform * drawTransform;
            _target.DrawBitmap(
                cached.Bitmap,
                Rect(0, 0, cached.PixelWidth, cached.PixelHeight),
                1f,
                BitmapInterpolationMode.Linear,
                Rect(0, 0, cached.PixelWidth, cached.PixelHeight));
            LastReference3DMaterialBitmapSubmissions++;
        }
        finally
        {
            _target.Transform = oldTransform;
        }
    }

    private static bool TryGetReference3DMaterialBitmapSource(
        VectorScene scene,
        int objectIndex,
        ID2D1PathGeometry? materialGeometry,
        bool usesLocalPathGeometry,
        IReadOnlyList<Reference3DSourceContour> sourceContours,
        out GdiRectangleF sourceBounds,
        out ID2D1PathGeometry? geometryIdentity,
        out IReadOnlyList<Reference3DSourceContour>? sourceContoursIdentity)
    {
        sourceBounds = GdiRectangleF.Empty;
        geometryIdentity = null;
        sourceContoursIdentity = null;
        if (usesLocalPathGeometry)
        {
            if (materialGeometry is null
                || !TryGetReference3DLocalPathBounds(scene, objectIndex, out sourceBounds))
            {
                return false;
            }
            geometryIdentity = materialGeometry;
            return true;
        }

        if (!StageControl.TryGetReference3DSourceDomainBounds(sourceContours, out sourceBounds))
        {
            return false;
        }

        sourceContoursIdentity = sourceContours;
        return true;
    }

    private static ulong Reference3DMaterialGeometryHash(
        VectorScene scene,
        int objectIndex,
        bool usesLocalPathGeometry,
        IReadOnlyList<Reference3DSourceContour> sourceContours)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        if (usesLocalPathGeometry)
        {
            scene.TryGetPathLocalContours(objectIndex, out var polygonContours);
            scene.TryGetPathBezierLocalContours(objectIndex, out var bezierContours);
            AddInt(bezierContours.Length > 0 ? 1 : 0);
            if (bezierContours.Length > 0)
            {
                AddInt(bezierContours.Length);
                foreach (var contour in bezierContours)
                {
                    AddInt(contour.Length);
                    foreach (var node in contour)
                    {
                        AddPoint(node.Anchor);
                        AddPoint(node.IncomingControl);
                        AddPoint(node.OutgoingControl);
                    }
                }
            }
            else
            {
                AddInt(polygonContours.Length);
                foreach (var contour in polygonContours)
                {
                    AddInt(contour.Length);
                    foreach (var point in contour) AddPoint(point);
                }
            }
        }
        else
        {
            AddInt(sourceContours.Count);
            foreach (var contour in sourceContours)
            {
                AddInt(contour.Closed ? 1 : 0);
                AddInt(contour.Points.Length);
                foreach (var point in contour.Points) AddPoint(point);
            }
        }

        return hash;

        void AddPoint(GdiPointF point)
        {
            AddInt(BitConverter.SingleToInt32Bits(point.X));
            AddInt(BitConverter.SingleToInt32Bits(point.Y));
        }

        void AddInt(int value)
        {
            unchecked
            {
                hash ^= (uint)value;
                hash *= prime;
                hash ^= (uint)(value >> 16);
                hash *= prime;
            }
        }
    }

    private static bool TryGetReference3DLocalPathBounds(
        VectorScene scene,
        int objectIndex,
        out GdiRectangleF bounds)
    {
        var left = float.PositiveInfinity;
        var top = float.PositiveInfinity;
        var right = float.NegativeInfinity;
        var bottom = float.NegativeInfinity;
        var hasPoint = false;

        if (scene.TryGetPathLocalContours(objectIndex, out var polygonContours))
        {
            foreach (var contour in polygonContours)
            {
                foreach (var point in contour) Include(point);
            }
        }
        if (scene.TryGetPathBezierLocalContours(objectIndex, out var bezierContours))
        {
            foreach (var contour in bezierContours)
            {
                foreach (var node in contour)
                {
                    Include(node.Anchor);
                    Include(node.IncomingControl);
                    Include(node.OutgoingControl);
                }
            }
        }

        if (!hasPoint
            || !float.IsFinite(left)
            || !float.IsFinite(top)
            || !float.IsFinite(right)
            || !float.IsFinite(bottom)
            || right - left <= 0.0001f
            || bottom - top <= 0.0001f)
        {
            bounds = GdiRectangleF.Empty;
            return false;
        }

        bounds = GdiRectangleF.FromLTRB(left, top, right, bottom);
        return true;

        void Include(GdiPointF point)
        {
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y)) return;
            hasPoint = true;
            left = Math.Min(left, point.X);
            top = Math.Min(top, point.Y);
            right = Math.Max(right, point.X);
            bottom = Math.Max(bottom, point.Y);
        }
    }

    private static bool TryGetReference3DMaterialBitmapLayout(
        GdiRectangleF sourceBounds,
        Matrix3x2 drawTransform,
        out float rasterScale,
        out int pixelWidth,
        out int pixelHeight,
        out Matrix3x2 sourceToBitmap,
        out Matrix3x2 bitmapToSource)
    {
        rasterScale = 0;
        pixelWidth = 0;
        pixelHeight = 0;
        sourceToBitmap = default;
        bitmapToSource = default;
        if (!float.IsFinite(sourceBounds.Left)
            || !float.IsFinite(sourceBounds.Top)
            || !float.IsFinite(sourceBounds.Width)
            || !float.IsFinite(sourceBounds.Height)
            || sourceBounds.Width <= 0.0001f
            || sourceBounds.Height <= 0.0001f)
        {
            return false;
        }

        var scaleX = MathF.Sqrt(
            drawTransform.M11 * drawTransform.M11
            + drawTransform.M12 * drawTransform.M12);
        var scaleY = MathF.Sqrt(
            drawTransform.M21 * drawTransform.M21
            + drawTransform.M22 * drawTransform.M22);
        var desiredScale = MathF.Max(scaleX, scaleY);
        if (!float.IsFinite(desiredScale)
            || desiredScale <= 0.000001f
            || !float.IsFinite(drawTransform.GetDeterminant())
            || Math.Abs(drawTransform.GetDeterminant()) <= 0.00000001f)
        {
            return false;
        }

        const int padding = 2;
        const int minimumDimension = 8;
        const int maximumDimension = 512;
        const int maximumPixels = 262_144;
        var maximumScale = Math.Min(
            (maximumDimension - padding * 2) / sourceBounds.Width,
            (maximumDimension - padding * 2) / sourceBounds.Height);
        var area = (double)sourceBounds.Width * sourceBounds.Height;
        maximumScale = Math.Min(
            maximumScale,
            (float)Math.Sqrt(maximumPixels / Math.Max(1d, area)));
        rasterScale = Math.Min(desiredScale, maximumScale);
        if (!float.IsFinite(rasterScale) || rasterScale <= 0.000001f) return false;

        pixelWidth = Math.Clamp(
            (int)MathF.Ceiling(sourceBounds.Width * rasterScale) + padding * 2,
            minimumDimension,
            maximumDimension);
        pixelHeight = Math.Clamp(
            (int)MathF.Ceiling(sourceBounds.Height * rasterScale) + padding * 2,
            minimumDimension,
            maximumDimension);
        sourceToBitmap = Matrix3x2.CreateScale(rasterScale)
            * Matrix3x2.CreateTranslation(
                padding - sourceBounds.Left * rasterScale,
                padding - sourceBounds.Top * rasterScale);
        return Matrix3x2.Invert(sourceToBitmap, out bitmapToSource);
    }

    private ID2D1Bitmap? CreateReference3DMaterialBitmap(
        ID2D1PathGeometry materialGeometry,
        GradientKind kind,
        IReadOnlyList<GradientStop> stops,
        Vector2 gradientStart,
        Vector2 gradientEnd,
        Matrix3x2 sourceToBitmap,
        int pixelWidth,
        int pixelHeight,
        bool bakesStroke,
        int strokeArgb,
        float strokeWidth)
    {
        ID2D1BitmapRenderTarget? bitmapTarget = null;
        ID2D1GradientStopCollection? collection = null;
        ID2D1Brush? brush = null;
        ID2D1Brush? strokeBrush = null;
        var drawingStarted = false;
        try
        {
            var pixelFormat = new PixelFormat(
                Format.B8G8R8A8_UNorm,
                DCommonAlphaMode.Premultiplied);
            bitmapTarget = _target!.CreateCompatibleRenderTarget(
                new SizeI(pixelWidth, pixelHeight),
                new SizeI(pixelWidth, pixelHeight),
                pixelFormat,
                CompatibleRenderTargetOptions.None);
            bitmapTarget.BeginDraw();
            drawingStarted = true;
            var transparent = new D2DColor(0f, 0f, 0f, 0f);
            bitmapTarget.Clear(in transparent);
            bitmapTarget.Transform = sourceToBitmap;
            bitmapTarget.AntialiasMode = AntialiasMode.PerPrimitive;
            collection = bitmapTarget.CreateGradientStopCollection(
                stops.Select(stop => new Vortice.Direct2D1.GradientStop(
                    stop.Position,
                    ToColor4(GdiColor.FromArgb(stop.Argb)))).ToArray(),
                Gamma.StandardRgb,
                ExtendMode.Clamp);
            if (kind == GradientKind.Radial)
            {
                var radius = Math.Max(0.5f, Vector2.Distance(gradientStart, gradientEnd));
                brush = bitmapTarget.CreateRadialGradientBrush(
                    new RadialGradientBrushProperties(
                        gradientStart,
                        Vector2.Zero,
                        radius,
                        radius),
                    new BrushProperties(1f),
                    collection);
            }
            else
            {
                brush = bitmapTarget.CreateLinearGradientBrush(
                    new LinearGradientBrushProperties(gradientStart, gradientEnd),
                    new BrushProperties(1f),
                    collection);
            }
            bitmapTarget.FillGeometry(materialGeometry, brush);
            if (bakesStroke)
            {
                strokeBrush = bitmapTarget.CreateSolidColorBrush(
                    ToColor4(GdiColor.FromArgb(strokeArgb)));
                bitmapTarget.DrawGeometry(
                    materialGeometry,
                    strokeBrush,
                    strokeWidth,
                    RoundStrokeStyle());
            }
            var result = bitmapTarget.EndDraw();
            drawingStarted = false;
            if (result.Failure) return null;
            var sourceBitmap = bitmapTarget.Bitmap;
            try
            {
                var properties = new BitmapProperties(
                    new PixelFormat(Format.B8G8R8A8_UNorm, DCommonAlphaMode.Premultiplied),
                    96,
                    96);
                var bitmap = _target.CreateBitmap(
                    new SizeI(pixelWidth, pixelHeight),
                    IntPtr.Zero,
                    0,
                    properties);
                try
                {
                    bitmap.CopyFromBitmap(sourceBitmap);
                    return bitmap;
                }
                catch
                {
                    bitmap.Dispose();
                    return null;
                }
                finally
                {
                    sourceBitmap.Dispose();
                }
            }
            catch
            {
                sourceBitmap.Dispose();
                return null;
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (drawingStarted)
            {
                try
                {
                    bitmapTarget?.EndDraw();
                }
                catch
                {
                }
            }
            brush?.Dispose();
            strokeBrush?.Dispose();
            collection?.Dispose();
            bitmapTarget?.Dispose();
        }
    }

    private void DrawReference3DSourcePathGradientSegments(
        IReadOnlyList<GradientPathGradientSegment> segments,
        CachedPathGradientBrushes cachedBrushes,
        float width,
        Reference3DProjectiveTriangle? clipTriangle = null)
    {
        var clipLeft = float.NegativeInfinity;
        var clipTop = float.NegativeInfinity;
        var clipRight = float.PositiveInfinity;
        var clipBottom = float.PositiveInfinity;
        if (clipTriangle is { } triangle)
        {
            var padding = width * 0.5f;
            clipLeft = Math.Min(triangle.A.Flat.X, Math.Min(triangle.B.Flat.X, triangle.C.Flat.X)) - padding;
            clipTop = Math.Min(triangle.A.Flat.Y, Math.Min(triangle.B.Flat.Y, triangle.C.Flat.Y)) - padding;
            clipRight = Math.Max(triangle.A.Flat.X, Math.Max(triangle.B.Flat.X, triangle.C.Flat.X)) + padding;
            clipBottom = Math.Max(triangle.A.Flat.Y, Math.Max(triangle.B.Flat.Y, triangle.C.Flat.Y)) + padding;
        }
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            if (Math.Max(segment.Start.X, segment.End.X) < clipLeft
                || Math.Min(segment.Start.X, segment.End.X) > clipRight
                || Math.Max(segment.Start.Y, segment.End.Y) < clipTop
                || Math.Min(segment.Start.Y, segment.End.Y) > clipBottom)
            {
                continue;
            }
            var start = ToVector(segment.Start);
            var end = ToVector(segment.End);
            var direction = end - start;
            var segmentLength = direction.Length();
            var positionSpan = segment.EndPosition - segment.StartPosition;
            if (segmentLength <= 0.001f || positionSpan <= 0.0001f) continue;

            direction /= segmentLength;
            var gradientAxisLength = segmentLength / positionSpan;
            var gradientAxisStart = start - direction * (gradientAxisLength * segment.StartPosition);
            var brush = cachedBrushes.Brushes[index];
            brush.StartPoint = gradientAxisStart;
            brush.EndPoint = gradientAxisStart + direction * gradientAxisLength;
            _target!.DrawLine(start, end, brush, width, RoundStrokeStyle());
        }
    }

    private bool DrawReference3DProjectiveTriangles(
        IReadOnlyList<Reference3DProjectiveTriangle> triangles,
        ID2D1Geometry? screenMask,
        Func<Reference3DProjectiveTriangle, bool> drawTriangle)
    {
        if (triangles.Count == 0) return false;

        var hasPotentiallyVisibleTriangle = false;
        foreach (var triangle in triangles)
        {
            if (!IsReference3DProjectiveTriangleOutsideViewport(triangle))
            {
                hasPotentiallyVisibleTriangle = true;
                break;
            }
        }
        if (!hasPotentiallyVisibleTriangle) return true;

        using var outerLayer = screenMask is null ? null : _target!.CreateLayer();
        using var triangleLayer = _target!.CreateLayer();
        var old = _target.Transform;
        var outerPushed = false;
        try
        {
            _target.Transform = Matrix3x2.Identity;
            if (screenMask is not null)
            {
                var outerParameters = new LayerParameters
                {
                    ContentBounds = Rect(0, 0, _targetSize.Width, _targetSize.Height),
                    GeometricMask = screenMask,
                    MaskAntialiasMode = AntialiasMode.PerPrimitive,
                    MaskTransform = Matrix3x2.Identity,
                    Opacity = 1f,
                    OpacityBrush = null,
                    LayerOptions = LayerOptions.None
                };
                _target.PushLayer(outerParameters, outerLayer!);
                outerPushed = true;
            }

            var drewTriangle = false;
            foreach (var triangle in triangles)
            {
                if (IsReference3DProjectiveTriangleOutsideViewport(triangle)) continue;

                using var triangleGeometry = CreateReference3DTriangle(
                    triangle.A.Screen,
                    triangle.B.Screen,
                    triangle.C.Screen);
                var triangleParameters = new LayerParameters
                {
                    ContentBounds = Rect(0, 0, _targetSize.Width, _targetSize.Height),
                    GeometricMask = triangleGeometry,
                    MaskAntialiasMode = AntialiasMode.Aliased,
                    MaskTransform = Matrix3x2.Identity,
                    Opacity = 1f,
                    OpacityBrush = null,
                    LayerOptions = LayerOptions.None
                };
                _target.Transform = Matrix3x2.Identity;
                _target.PushLayer(triangleParameters, triangleLayer);
                try
                {
                    drewTriangle |= drawTriangle(triangle);
                }
                finally
                {
                    _target.Transform = Matrix3x2.Identity;
                    _target.PopLayer();
                }
            }
            return drewTriangle;
        }
        finally
        {
            _target.Transform = Matrix3x2.Identity;
            if (outerPushed) _target.PopLayer();
            _target.Transform = old;
        }
    }

    private bool IsReference3DProjectiveTriangleOutsideViewport(
        Reference3DProjectiveTriangle triangle)
    {
        var ax = triangle.A.Screen.X;
        var ay = triangle.A.Screen.Y;
        var bx = triangle.B.Screen.X;
        var by = triangle.B.Screen.Y;
        var cx = triangle.C.Screen.X;
        var cy = triangle.C.Screen.Y;
        if (!float.IsFinite(ax)
            || !float.IsFinite(ay)
            || !float.IsFinite(bx)
            || !float.IsFinite(by)
            || !float.IsFinite(cx)
            || !float.IsFinite(cy))
        {
            return false;
        }

        const float padding = 1f;
        var minX = MathF.Min(ax, MathF.Min(bx, cx));
        var minY = MathF.Min(ay, MathF.Min(by, cy));
        var maxX = MathF.Max(ax, MathF.Max(bx, cx));
        var maxY = MathF.Max(ay, MathF.Max(by, cy));
        var viewportRight = _targetSize.Width + padding;
        var viewportBottom = _targetSize.Height + padding;
        return maxX < -padding
            || minX > viewportRight
            || maxY < -padding
            || minY > viewportBottom;
    }

    private bool DrawReference3DSourcePathGradientFill(
        StageControl stage,
        ID2D1Geometry mask,
        int objectIndex,
        IReadOnlyList<GradientStop> stops,
        Matrix3x2 transform)
    {
        var scene = stage.Scene;
        var estimatedWidth = scene.EstimateGradientPathStrokeWidth(objectIndex);
        if (!scene.TryGetGradientPathWorldPoints(objectIndex, out var path)
            || path.Length < 2
            || estimatedWidth is not > 0)
        {
            return false;
        }

        var screenLength = 0f;
        for (var index = 1; index < path.Length; index++)
        {
            screenLength += Vector2.Distance(
                Vector2.Transform(ToVector(path[index - 1]), transform),
                Vector2.Transform(ToVector(path[index]), transform));
        }
        var segments = GradientPaintUtilities.CreatePathGradientSegments(
            path,
            stops,
            Math.Clamp((int)MathF.Ceiling(screenLength / 4f), 8, 256));
        if (segments.Length == 0) return false;

        using var layer = _target!.CreateLayer();
        var parameters = new LayerParameters
        {
            ContentBounds = Rect(0, 0, _targetSize.Width, _targetSize.Height),
            GeometricMask = mask,
            MaskAntialiasMode = AntialiasMode.PerPrimitive,
            MaskTransform = Matrix3x2.Identity,
            Opacity = 1f,
            OpacityBrush = null,
            LayerOptions = LayerOptions.None
        };
        var old = _target.Transform;
        _target.Transform = Matrix3x2.Identity;
        _target.PushLayer(parameters, layer);
        try
        {
            _target.Transform = transform;
            var cachedBrushes = PathGradientBrushes(scene, objectIndex, stops, segments);
            var width = Math.Max(VectorUnits.MinimumStrokeUnits, estimatedWidth * 1.12f);
            for (var index = 0; index < segments.Length; index++)
            {
                var segment = segments[index];
                var start = ToVector(segment.Start);
                var end = ToVector(segment.End);
                var direction = end - start;
                var segmentLength = direction.Length();
                var positionSpan = segment.EndPosition - segment.StartPosition;
                if (segmentLength <= 0.001f || positionSpan <= 0.0001f) continue;

                direction /= segmentLength;
                var gradientAxisLength = segmentLength / positionSpan;
                var gradientAxisStart = start - direction * (gradientAxisLength * segment.StartPosition);
                var brush = cachedBrushes.Brushes[index];
                brush.StartPoint = gradientAxisStart;
                brush.EndPoint = gradientAxisStart + direction * gradientAxisLength;
                _target.DrawLine(start, end, brush, width, RoundStrokeStyle());
            }
        }
        finally
        {
            _target.Transform = Matrix3x2.Identity;
            _target.PopLayer();
            _target.Transform = old;
        }

        return true;
    }

    private bool DrawReference3DSourceShapeGradientFill(
        StageControl stage,
        ID2D1Geometry mask,
        int objectIndex,
        IReadOnlyList<GradientStop> stops,
        Matrix3x2 transform)
    {
        var cached = ShapeGradientBitmap(stage, stage.Scene, objectIndex, stops);
        if (cached is null) return false;

        var parameters = new LayerParameters
        {
            ContentBounds = Rect(0, 0, _targetSize.Width, _targetSize.Height),
            GeometricMask = mask,
            MaskAntialiasMode = AntialiasMode.PerPrimitive,
            MaskTransform = Matrix3x2.Identity,
            Opacity = 1f,
            OpacityBrush = null,
            LayerOptions = LayerOptions.None
        };
        var destination = Rect(
            cached.WorldBounds.Left,
            cached.WorldBounds.Top,
            cached.WorldBounds.Width,
            cached.WorldBounds.Height);
        var source = Rect(0, 0, cached.PixelWidth, cached.PixelHeight);
        var old = _target!.Transform;
        _target.Transform = Matrix3x2.Identity;
        _target.PushLayer(parameters, ShapeGradientMaskLayer());
        try
        {
            _target.Transform = transform;
            _target.DrawBitmap(cached.Bitmap, destination, 1f, BitmapInterpolationMode.Linear, source);
        }
        finally
        {
            _target.Transform = Matrix3x2.Identity;
            _target.PopLayer();
            _target.Transform = old;
        }

        return true;
    }

    private static float Reference3DSourcePixelWidth(Matrix3x2 transform)
    {
        var scaleX = MathF.Sqrt(transform.M11 * transform.M11 + transform.M12 * transform.M12);
        var scaleY = MathF.Sqrt(transform.M21 * transform.M21 + transform.M22 * transform.M22);
        return 1f / Math.Max(0.0001f, Math.Max(scaleX, scaleY));
    }

    private bool DrawReference3DObjectOutline(StageControl stage, int objectIndex, GdiColor layerColor)
    {
        var scene = stage.Scene;
        if (stage.IsObjectHiddenForRendering(scene, objectIndex)) return false;
        var color = Reference3DOutlineColor(scene, objectIndex, layerColor);
        if (color.A == 0) return false;
        using var geometry = CreateReference3DGeometry(
            stage.GetReference3DProjectedSolid(objectIndex).SelectionEdges,
            fillOnly: false);
        if (geometry is null) return false;
        _target!.DrawGeometry(geometry, BrushFor(color.ToArgb()), 1f, RoundStrokeStyle());
        return true;
    }

    private ID2D1PathGeometry? CreateReference3DMaskGeometry(
        StageControl stage,
        IReadOnlyList<int> maskObjects)
    {
        var path = _factory!.CreatePathGeometry();
        var hasGeometry = false;
        using (var sink = path.Open())
        {
            sink.SetFillMode(FillMode.Alternate);
            foreach (var objectIndex in maskObjects)
            {
                if (!SceneRenderOrder.HasFill(stage.Scene.ShapeKind[objectIndex])
                    || stage.IsObjectHiddenForRendering(stage.Scene, objectIndex))
                {
                    continue;
                }
                hasGeometry |= AppendReference3DGeometry(
                    sink,
                    stage.GetReference3DProjectedContours(objectIndex),
                    fillOnly: true);
            }
            sink.Close();
        }
        if (hasGeometry) return path;
        path.Dispose();
        return null;
    }

    private ID2D1PathGeometry? CreateReference3DGeometry(
        IReadOnlyList<Reference3DProjectedContour> contours,
        bool fillOnly)
    {
        var path = _factory!.CreatePathGeometry();
        var hasGeometry = false;
        using (var sink = path.Open())
        {
            sink.SetFillMode(FillMode.Alternate);
            hasGeometry = AppendReference3DGeometry(sink, contours, fillOnly);
            sink.Close();
        }
        if (hasGeometry) return path;
        path.Dispose();
        return null;
    }


    private ID2D1PathGeometry? CreateReference3DSourceGeometry(
        IReadOnlyList<Reference3DSourceContour> contours,
        bool fillOnly)
    {
        var path = _factory!.CreatePathGeometry();
        var hasGeometry = false;
        using (var sink = path.Open())
        {
            sink.SetFillMode(FillMode.Alternate);
            foreach (var contour in contours)
            {
                if (fillOnly && !contour.Closed) continue;
                if (contour.Points.Length < (contour.Closed ? 3 : 2)) continue;
                sink.BeginFigure(
                    ToVector(contour.Points[0]),
                    contour.Closed ? FigureBegin.Filled : FigureBegin.Hollow);
                for (var index = 1; index < contour.Points.Length; index++)
                {
                    sink.AddLine(ToVector(contour.Points[index]));
                }
                sink.EndFigure(contour.Closed ? FigureEnd.Closed : FigureEnd.Open);
                hasGeometry = true;
            }
            sink.Close();
        }
        if (hasGeometry) return path;
        path.Dispose();
        return null;
    }

    private ID2D1PathGeometry? Reference3DLocalPathGeometry(
        VectorScene scene,
        int objectIndex)
    {
        if ((uint)objectIndex >= scene.ObjectCount
            || scene.ShapeKind[objectIndex] != ShapeKind.Path)
        {
            return null;
        }

        scene.TryGetPathLocalContours(objectIndex, out var polygonContours);
        GdiPointF[][]? polygonIdentity = polygonContours.Length > 0 ? polygonContours : null;
        scene.TryGetPathBezierLocalContours(objectIndex, out var bezierContours);
        PathBezierNode[][]? bezierIdentity = bezierContours.Length > 0 ? bezierContours : null;
        if (polygonIdentity is null && bezierIdentity is null) return null;

        var open = scene.IsPathOpen(objectIndex);
        var sourceKey = CreatePathGeometryContentKey(scene, polygonIdentity, bezierIdentity, open);
        var key = new Reference3DLocalPathGeometryKey(
            sourceKey.Hash,
            sourceKey.ContourCount,
            sourceKey.PointCount,
            sourceKey.UsesBezier,
            open);
        if (_reference3DLocalPathGeometryCache.TryGetValue(key, out var entries))
        {
            foreach (var entry in entries)
            {
                if (!entry.Matches(key, polygonIdentity, bezierIdentity)) continue;
                LastReference3DLocalPathGeometryCacheReuses++;
                return entry.Geometry;
            }
        }

        if (_reference3DLocalPathGeometryCache.Count >= MaximumReference3DLocalPathGeometryEntries)
        {
            ClearReference3DLocalPathGeometryCache();
        }

        var geometry = _factory!.CreatePathGeometry();
        var hasContours = false;
        using (var sink = geometry.Open())
        {
            sink.SetFillMode(FillMode.Alternate);
            hasContours = bezierIdentity is not null
                ? AppendBezierFigures(sink, stage: null, bezierIdentity, open)
                : AppendPolygonFigures(sink, stage: null, polygonIdentity!);
            sink.Close();
        }
        if (!hasContours)
        {
            geometry.Dispose();
            return null;
        }

        var cached = new CachedReference3DLocalPathGeometry(
            key,
            polygonIdentity,
            bezierIdentity,
            geometry);
        if (entries is null)
        {
            entries = [];
            _reference3DLocalPathGeometryCache.Add(key, entries);
        }
        entries.Add(cached);
        LastReference3DLocalPathGeometryCacheBuilds++;
        return geometry;
    }

    private void ClearReference3DLocalPathGeometryCache()
    {
        foreach (var entries in _reference3DLocalPathGeometryCache.Values)
        {
            foreach (var entry in entries) entry.Dispose();
        }
        _reference3DLocalPathGeometryCache.Clear();
    }

    private static Matrix3x2 ObjectLocalToWorldTransform(VectorScene scene, int objectIndex)
    {
        return Matrix3x2.CreateRotation(scene.Angle[objectIndex])
            * Matrix3x2.CreateTranslation(scene.X[objectIndex], scene.Y[objectIndex]);
    }

    private ID2D1PathGeometry? CreateReference3DSourceDomainGeometry(
        IReadOnlyList<Reference3DSourceContour> contours)
    {
        if (!StageControl.TryGetReference3DSourceDomainBounds(contours, out var bounds)) return null;
        return CreateReference3DSourceGeometry(
        [
            new Reference3DSourceContour(
            [
                new GdiPointF(bounds.Left, bounds.Top),
                new GdiPointF(bounds.Right, bounds.Top),
                new GdiPointF(bounds.Right, bounds.Bottom),
                new GdiPointF(bounds.Left, bounds.Bottom)
            ],
            true)
        ],
        fillOnly: true);
    }

    private static bool AppendReference3DGeometry(
        ID2D1GeometrySink sink,
        IReadOnlyList<Reference3DProjectedContour> contours,
        bool fillOnly)
    {
        var hasGeometry = false;
        foreach (var contour in contours)
        {
            if (fillOnly && !contour.Closed) continue;
            if (contour.Points.Length < (contour.Closed ? 3 : 2)) continue;
            sink.BeginFigure(
                ToVector(contour.Points[0]),
                contour.Closed ? FigureBegin.Filled : FigureBegin.Hollow);
            for (var index = 1; index < contour.Points.Length; index++)
            {
                sink.AddLine(ToVector(contour.Points[index]));
            }
            sink.EndFigure(contour.Closed ? FigureEnd.Closed : FigureEnd.Open);
            hasGeometry = true;
        }
        return hasGeometry;
    }

    private void DrawReference3DImportedSvg(StageControl stage, int objectIndex)
    {
        var scene = stage.Scene;
        if (!scene.TryGetImportedSvgSource(objectIndex, out var source)
            || string.IsNullOrWhiteSpace(source))
        {
            return;
        }

        DrawReference3DRaster(
            stage,
            objectIndex,
            (pixelWidth, pixelHeight) =>
                ImportedSvgBitmap(ImportedSvgRasterizer.Rasterize(source, pixelWidth, pixelHeight)),
            BitmapInterpolationMode.Linear);
    }

    /// <summary>
    /// Direct2D reference-3D draw for a placed bitmap. Shares the projective/affine
    /// submission with imported SVG so both raster kinds stay perspective-correct on
    /// tilted surfaces, while sampling follows the asset's import filter mode.
    /// </summary>
    private void DrawReference3DBitmap(StageControl stage, int objectIndex)
    {
        var scene = stage.Scene;
        if (!scene.TryGetBitmapObjectData(objectIndex, out var data)) return;
        if (!stage.TryDecodeBitmapImage(data.ImageAssetId, out var decoded)) return;
        decoded = BitmapImageRasterizer.ApplyObjectClip(decoded, data);
        var interpolation = stage.BitmapImageSampling(data.ImageAssetId) == BitmapSampling.Point
            ? BitmapInterpolationMode.NearestNeighbor
            : BitmapInterpolationMode.Linear;

        DrawReference3DRaster(
            stage,
            objectIndex,
            (_, _) => BitmapObjectBitmap(decoded),
            interpolation,
            decoded.PixelWidth,
            decoded.PixelHeight);
    }

    /// <summary>
    /// Shared reference-3D raster submission. <paramref name="resolveBitmap"/> returns the
    /// GPU bitmap for the raster size the caller needs; SVG resolves by re-rasterizing at the
    /// projected size while a decoded image reuses its existing upload.
    /// </summary>
    private void DrawReference3DRaster(
        StageControl stage,
        int objectIndex,
        Func<float, float, ID2D1Bitmap> resolveBitmap,
        BitmapInterpolationMode interpolation,
        int? fixedPixelWidth = null,
        int? fixedPixelHeight = null)
    {
        var scene = stage.Scene;
        var contour = stage.GetReference3DProjectedContours(objectIndex)
            .FirstOrDefault(item => item.Closed && item.Points.Length >= 3);
        if (contour.Points is not { Length: >= 3 }) return;

        var hasAffineTransform = stage.TryGetReference3DFlatToScreenTransform(objectIndex, out _);
        Reference3DProjectiveTriangle[] triangles = [];
        var hasProjectiveMesh = stage.TryGetReference3DProjectiveMesh(
            objectIndex,
            usePrimaryContourQuad: true,
            out triangles)
            && triangles.Length > 0;
        var projective = hasProjectiveMesh
            && (!hasAffineTransform
                || !StageControl.Reference3DProjectiveMeshCoversFullDomain(triangles));

        GdiPointF topLeft = default;
        GdiPointF topRight = default;
        GdiPointF bottomLeft = default;
        GdiSizeF rasterSize;
        if (projective)
        {
            rasterSize = stage.EstimateReference3DProjectiveSvgTextureSize(triangles);
        }
        else
        {
            if (!hasAffineTransform || contour.Points.Length < 4) return;
            topLeft = contour.Points[0];
            topRight = contour.Points[1];
            bottomLeft = contour.Points[^1];
            rasterSize = new GdiSizeF(
                Math.Max(1f, Reference3DDistance(topLeft, topRight)),
                Math.Max(1f, Reference3DDistance(topLeft, bottomLeft)));
        }

        var pixelWidth = fixedPixelWidth ?? Math.Max(1, (int)rasterSize.Width);
        var pixelHeight = fixedPixelHeight ?? Math.Max(1, (int)rasterSize.Height);
        var bitmap = resolveBitmap(rasterSize.Width, rasterSize.Height);
        var opacity = GdiColor.FromArgb(scene.Argb[objectIndex]).A / 255f;
        if (projective)
        {
            using var screenMask = CreateReference3DGeometry(
                stage.GetReference3DProjectedContours(objectIndex),
                fillOnly: true);
            if (screenMask is not null)
            {
                if (stage.Reference3DGpuOpticsEnabled && stage.Reference3DPlaybackActive
                    && TryDrawGpuProjectiveBitmap(stage, bitmap, triangles, screenMask,
                        pixelWidth, pixelHeight, opacity)) return;
                DrawReference3DProjectiveTriangles(
                    triangles,
                    screenMask,
                    triangle =>
                    {
                        if (!StageControl.TryGetReference3DTextureToScreenTransform(
                                triangle,
                                pixelWidth,
                                pixelHeight,
                                out var projectiveTransform))
                        {
                            return false;
                        }
                        _target!.Transform = projectiveTransform;
                        var textureBounds = StageControl.GetReference3DProjectiveTextureBounds(
                            triangle,
                            pixelWidth,
                            pixelHeight);
                        if (textureBounds.Width <= 0 || textureBounds.Height <= 0) return false;
                        var rectangle = Rect(
                            textureBounds.X,
                            textureBounds.Y,
                            textureBounds.Width,
                            textureBounds.Height);
                        _target.DrawBitmap(
                            bitmap,
                            rectangle,
                            opacity,
                            interpolation,
                            rectangle);
                        return true;
                    });
            }
            return;
        }

        var transform = new Matrix3x2(
            (topRight.X - topLeft.X) / pixelWidth,
            (topRight.Y - topLeft.Y) / pixelWidth,
            (bottomLeft.X - topLeft.X) / pixelHeight,
            (bottomLeft.Y - topLeft.Y) / pixelHeight,
            topLeft.X,
            topLeft.Y);
        var old = _target!.Transform;
        try
        {
            _target.Transform = transform;
            var destination = Rect(0, 0, pixelWidth, pixelHeight);
            var sourceRect = Rect(0, 0, pixelWidth, pixelHeight);
            _target.DrawBitmap(
                bitmap,
                destination,
                opacity,
                interpolation,
                sourceRect);
        }
        finally
        {
            _target.Transform = old;
        }
    }

    private void DrawReference3DMixingStroke(StageControl stage, int objectIndex)
    {
        var scene = stage.Scene;
        if (!scene.TryGetMixingBrushWorldRegion(objectIndex, out var region))
        {
            using var fallback = CreateReference3DGeometry(
                stage.GetReference3DProjectedContours(objectIndex),
                fillOnly: true);
            if (fallback is not null) _target!.FillGeometry(fallback, BrushFor(scene.Argb[objectIndex]));
            return;
        }
        var vertices = new GdiPointF[region.Vertices.Length];
        var visible = new bool[vertices.Length];
        for (var index = 0; index < vertices.Length; index++)
        {
            visible[index] = stage.TryProjectReference3DFrontPoint(
                objectIndex,
                region.Vertices[index].Point,
                out vertices[index],
                out _);
        }
        for (var triangle = 0; triangle + 2 < region.TriangleIndices.Length; triangle += 3)
        {
            var a = region.TriangleIndices[triangle];
            var b = region.TriangleIndices[triangle + 1];
            var c = region.TriangleIndices[triangle + 2];
            if ((uint)a >= vertices.Length || (uint)b >= vertices.Length || (uint)c >= vertices.Length
                || !visible[a] || !visible[b] || !visible[c])
            {
                continue;
            }
            using var geometry = CreateReference3DTriangle(vertices[a], vertices[b], vertices[c]);
            _target!.FillGeometry(
                geometry,
                BrushFor(AverageArgb(
                    region.Vertices[a].Argb,
                    region.Vertices[b].Argb,
                    region.Vertices[c].Argb)));
        }
    }

    private ID2D1PathGeometry CreateReference3DTriangle(GdiPointF a, GdiPointF b, GdiPointF c)
    {
        var path = _factory!.CreatePathGeometry();
        using var sink = path.Open();
        sink.BeginFigure(ToVector(a), FigureBegin.Filled);
        sink.AddLine(ToVector(b));
        sink.AddLine(ToVector(c));
        sink.EndFigure(FigureEnd.Closed);
        sink.Close();
        return path;
    }

    private void DrawReference3DSelection(StageControl stage)
    {
        foreach (var objectIndex in stage.Reference3DSelectedObjects)
        {
            using var geometry = CreateReference3DGeometry(
                stage.GetReference3DProjectedSolid(objectIndex).SelectionEdges,
                fillOnly: false);
            if (geometry is null) continue;
            DrawReference3DSelectionWithMasks(stage, objectIndex, () =>
            {
                _target!.DrawGeometry(
                    geometry,
                    BrushFor(StageControl.Reference3DSelectionHaloColor.ToArgb()),
                    3.5f,
                    RoundStrokeStyle());
                _target.DrawGeometry(
                    geometry,
                    BrushFor(StageControl.Reference3DSelectionLineColor.ToArgb()),
                    1.5f,
                    RoundStrokeStyle());
            });
        }
    }

    private void DrawReference3DSelectionWithMasks(
        StageControl stage,
        int objectIndex,
        Action draw)
    {
        var scene = stage.Scene;
        var layer = scene.ObjectLayer[objectIndex];
        if (scene.GetLayerKind(layer) == DrawingLayerKind.Mask
            || !scene.TryGetMaskLayerIndex(layer, out var maskLayer))
        {
            DrawWithSceneCompositionMaskClips(stage, objectIndex, draw, reference3D: true);
            return;
        }
        if (!scene.IsLayerEffectivelyVisible(maskLayer)) return;

        using var mask = CreateReference3DMaskGeometry(stage, stage.GetReference3DLayerObjects(maskLayer));
        if (mask is null) return;
        using var targetLayer = _target!.CreateLayer();
        var parameters = new LayerParameters
        {
            ContentBounds = Rect(0, 0, _targetSize.Width, _targetSize.Height),
            GeometricMask = mask,
            MaskAntialiasMode = AntialiasMode.PerPrimitive,
            MaskTransform = Matrix3x2.Identity,
            Opacity = 1f,
            OpacityBrush = null,
            LayerOptions = LayerOptions.None
        };
        _target.PushLayer(parameters, targetLayer);
        try
        {
            DrawWithSceneCompositionMaskClips(stage, objectIndex, draw, reference3D: true);
        }
        finally
        {
            _target.PopLayer();
        }
    }

    private void DrawSpatialTransformGizmo(StageControl stage)
    {
        if (!stage.TryGetSpatialGizmoRenderGeometry(
                out var geometry,
                out var opacity,
                out var renderScale)
            || opacity <= 0.001f)
        {
            return;
        }

        using var opacityLayer = opacity < 0.999999f ? _target!.CreateLayer() : null;
        DrawReference3DSceneItemWithOpacity(opacity, opacityLayer, () =>
        {
            DrawSpatialTransformGizmoCore(stage, geometry, renderScale);
            return true;
        });
    }

    private void DrawSpatialTransformGizmoCore(
        StageControl stage,
        StageControl.SpatialGizmoScreenGeometry geometry,
        float renderScale)
    {
        var dpiScale = stage.SpatialGizmoDpiScale * renderScale;
        var highlight = BrushFor(GdiColor.FromArgb(246, 255, 196, 56).ToArgb());
        if (stage.SpatialTransformGizmoMode == SpatialTransformMode.Rotate)
        {
            for (var axis = 0; axis < 3; axis++)
            {
                var ring = geometry.Rings[axis];
                if (ring.Length < 3) continue;
                using var path = CreateReference3DGeometry(
                    [new Reference3DProjectedContour(ring, true, 0)],
                    fillOnly: false);
                if (path is not null)
                {
                    var highlighted = stage.IsSpatialTransformHandleHighlighted(
                        (SpatialTransformAxis)(axis + 1));
                    if (highlighted)
                    {
                        _target!.DrawGeometry(path, highlight, 6.2f * dpiScale, RoundStrokeStyle());
                    }
                    _target!.DrawGeometry(
                        path,
                        BrushFor(SpatialAxisArgb(axis)),
                        (highlighted ? 3.4f : 2.2f) * dpiScale,
                        RoundStrokeStyle());
                }
            }
            return;
        }

        if (stage.SpatialTransformGizmoMode == SpatialTransformMode.Move)
        {
            for (var plane = 0; plane < geometry.PlaneHandles.Length; plane++)
            {
                var polygon = geometry.PlaneHandles[plane];
                if (polygon.Length != 4) continue;
                using var path = CreateReference3DGeometry(
                    [new Reference3DProjectedContour(polygon, true, 0)],
                    fillOnly: true);
                if (path is null) continue;
                var planeAxis = plane switch
                {
                    0 => SpatialTransformAxis.XY,
                    1 => SpatialTransformAxis.XZ,
                    _ => SpatialTransformAxis.YZ
                };
                var highlighted = stage.IsSpatialTransformHandleHighlighted(planeAxis);
                _target!.FillGeometry(path, BrushFor(SpatialPlaneArgb(plane, highlighted ? 126 : 68)));
                if (highlighted)
                {
                    _target.DrawGeometry(path, highlight, 4.6f * dpiScale, RoundStrokeStyle());
                }
                _target.DrawGeometry(
                    path,
                    BrushFor(SpatialPlaneArgb(plane, highlighted ? 255 : 205)),
                    (highlighted ? 2.1f : 1.3f) * dpiScale,
                    RoundStrokeStyle());
            }
        }

        for (var axis = 0; axis < 3; axis++)
        {
            var axisKind = (SpatialTransformAxis)(axis + 1);
            var highlighted = stage.IsSpatialTransformHandleHighlighted(axisKind);
            var color = BrushFor(SpatialAxisArgb(axis));
            if (highlighted)
            {
                _target!.DrawLine(
                    ToVector(geometry.Origin),
                    ToVector(geometry.Endpoints[axis]),
                    highlight,
                    6.4f * dpiScale,
                    RoundStrokeStyle());
            }
            _target!.DrawLine(
                ToVector(geometry.Origin),
                ToVector(geometry.Endpoints[axis]),
                color,
                (highlighted ? 3.5f : 2.4f) * dpiScale,
                RoundStrokeStyle());
            if (stage.SpatialTransformGizmoMode == SpatialTransformMode.Scale)
            {
                var endpoint = geometry.Endpoints[axis];
                var halfSize = 4f * dpiScale;
                if (highlighted)
                {
                    var haloHalfSize = 6.5f * dpiScale;
                    _target.FillRectangle(
                        Rect(
                            endpoint.X - haloHalfSize,
                            endpoint.Y - haloHalfSize,
                            haloHalfSize * 2,
                            haloHalfSize * 2),
                        highlight);
                }
                var square = Rect(
                    endpoint.X - halfSize,
                    endpoint.Y - halfSize,
                    halfSize * 2,
                    halfSize * 2);
                _target.FillRectangle(square, color);
            }
            else
            {
                using var arrow = CreateSpatialArrowGeometry(
                    geometry.Origin,
                    geometry.Endpoints[axis],
                    dpiScale);
                if (arrow is not null)
                {
                    if (highlighted)
                    {
                        _target.DrawGeometry(arrow, highlight, 4f * dpiScale, RoundStrokeStyle());
                    }
                    _target.FillGeometry(arrow, color);
                }
            }
        }
        if (stage.SpatialTransformGizmoMode == SpatialTransformMode.Scale)
        {
            var origin = geometry.Origin;
            var highlighted = stage.IsSpatialTransformHandleHighlighted(SpatialTransformAxis.Uniform);
            var halfSize = 4f * dpiScale;
            if (highlighted)
            {
                var haloHalfSize = 6.5f * dpiScale;
                _target!.FillRectangle(
                    Rect(
                        origin.X - haloHalfSize,
                        origin.Y - haloHalfSize,
                        haloHalfSize * 2,
                        haloHalfSize * 2),
                    highlight);
            }
            var square = Rect(
                origin.X - halfSize,
                origin.Y - halfSize,
                halfSize * 2,
                halfSize * 2);
            _target!.FillRectangle(square, BrushFor(GdiColor.FromArgb(238, 235, 241, 242).ToArgb()));
        }
    }

    private ID2D1PathGeometry? CreateSpatialArrowGeometry(
        GdiPointF origin,
        GdiPointF endpoint,
        float dpiScale)
    {
        var dx = endpoint.X - origin.X;
        var dy = endpoint.Y - origin.Y;
        var length = MathF.Sqrt(dx * dx + dy * dy);
        if (length <= 0.001f) return null;
        dx /= length;
        dy /= length;
        var perpendicularX = -dy;
        var perpendicularY = dx;
        var basePoint = new GdiPointF(
            endpoint.X - dx * 10f * dpiScale,
            endpoint.Y - dy * 10f * dpiScale);
        return CreateReference3DTriangle(
            endpoint,
            new GdiPointF(
                basePoint.X + perpendicularX * 4.5f * dpiScale,
                basePoint.Y + perpendicularY * 4.5f * dpiScale),
            new GdiPointF(
                basePoint.X - perpendicularX * 4.5f * dpiScale,
                basePoint.Y - perpendicularY * 4.5f * dpiScale));
    }

    private static GdiColor Reference3DOutlineColor(
        VectorScene scene,
        int objectIndex,
        GdiColor layerColor)
    {
        if (layerColor.IsEmpty) return GdiColor.Empty;
        var shape = scene.ShapeKind[objectIndex];
        var fillAlpha = SceneRenderOrder.HasFill(shape) ? GdiColor.FromArgb(scene.Argb[objectIndex]).A : 0;
        var strokeAlpha = SceneRenderOrder.HasStroke(shape, scene.Stroke[objectIndex])
            ? GdiColor.FromArgb(scene.StrokeArgb[objectIndex]).A
            : 0;
        var alpha = (layerColor.A * Math.Max(fillAlpha, strokeAlpha) + 127) / 255;
        return GdiColor.FromArgb(alpha, layerColor.R, layerColor.G, layerColor.B);
    }

    private static int AverageArgb(int first, int second, int third)
    {
        var a = GdiColor.FromArgb(first);
        var b = GdiColor.FromArgb(second);
        var c = GdiColor.FromArgb(third);
        return GdiColor.FromArgb(
            (a.A + b.A + c.A) / 3,
            (a.R + b.R + c.R) / 3,
            (a.G + b.G + c.G) / 3,
            (a.B + b.B + c.B) / 3).ToArgb();
    }

    private static int SpatialAxisArgb(int axis) => axis switch
    {
        0 => GdiColor.FromArgb(245, 235, 82, 82).ToArgb(),
        1 => GdiColor.FromArgb(245, 82, 205, 122).ToArgb(),
        _ => GdiColor.FromArgb(245, 82, 155, 245).ToArgb()
    };

    private static int SpatialPlaneArgb(int plane, int alpha)
    {
        var color = GdiColor.FromArgb(SpatialAxisArgb(plane switch
        {
            0 => 2,
            1 => 1,
            _ => 0
        }));
        return GdiColor.FromArgb(alpha, color.R, color.G, color.B).ToArgb();
    }

    private static float Reference3DDistance(GdiPointF left, GdiPointF right)
    {
        var dx = left.X - right.X;
        var dy = left.Y - right.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private void DrawWithSceneCompositionMaskClips(
        StageControl stage,
        int objectIndex,
        Action draw,
        bool reference3D)
    {
        var clips = stage.GetSceneCompositionMaskClips(objectIndex);
        if (clips.Count == 0)
        {
            draw();
            return;
        }

        var layers = new List<ID2D1Layer>(clips.Count);
        var geometries = new List<ID2D1PathGeometry>(clips.Count);
        try
        {
            foreach (var clip in clips)
            {
                var geometry = reference3D
                    ? CreateReference3DGeometry(stage.GetReference3DProjectedMaskContours(clip), fillOnly: true)
                    : CreateSceneCompositionMaskGeometry2D(stage, clip);
                if (geometry is null) return;
                var layer = _target!.CreateLayer();
                var parameters = new LayerParameters
                {
                    ContentBounds = Rect(0, 0, _targetSize.Width, _targetSize.Height),
                    GeometricMask = geometry,
                    MaskAntialiasMode = AntialiasMode.PerPrimitive,
                    MaskTransform = Matrix3x2.Identity,
                    Opacity = 1f,
                    OpacityBrush = null,
                    LayerOptions = LayerOptions.None
                };
                _target.PushLayer(parameters, layer);
                geometries.Add(geometry);
                layers.Add(layer);
            }
            draw();
        }
        finally
        {
            for (var index = layers.Count - 1; index >= 0; index--)
            {
                _target!.PopLayer();
                layers[index].Dispose();
                geometries[index].Dispose();
            }
        }
    }

    private ID2D1PathGeometry? CreateSceneCompositionMaskGeometry2D(
        StageControl stage,
        SceneCompositionMaskClip clip)
    {
        var projected = stage.GetSceneCompositionMaskWorldContours(clip)
            .Where(contour => contour.Length >= 3)
            .Select(contour => new Reference3DProjectedContour(
                contour.Select(point => stage.WorldToScreen(point.X, point.Y)).ToArray(),
                true,
                0))
            .ToArray();
        return CreateReference3DGeometry(projected, fillOnly: true);
    }
}
