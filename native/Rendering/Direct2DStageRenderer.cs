using System.Buffers;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;
using D2DColor = Vortice.Mathematics.Color;
using D2DRect = Vortice.Mathematics.Rect;
using GdiColor = System.Drawing.Color;
using GdiPointF = System.Drawing.PointF;
using GdiRectangleF = System.Drawing.RectangleF;
using DCommonAlphaMode = Vortice.DCommon.AlphaMode;

namespace VectorAnimationEngine;

internal sealed partial class Direct2DStageRenderer : IDisposable
{
    private const int MaxDirect2DBrushCacheEntries = 1024;
    private const int MaxMixingBrushBitmapCacheEntries = 64;
    private const int MaxFreehandGeometryCacheEntries = 1024;
    private const int MaxFreehandGeometryCachePoints = 262_144;
    private const int MaxLineGeometryCacheEntries = 8_192;
    private const int MaxObjectPathGeometryCacheEntries = 2_048;
    private const int MaxGradientBrushCacheEntries = 1_024;
    private const int MaxShapeGradientBitmapCacheEntries = 256;
    private const int MaxShapeGradientBitmapPixels = 262_144;
    private const int MaxShapeGradientMaskGeometryCacheEntries = 1_024;
    private const int MaxPathGradientBrushCacheEntries = 64;
    private const int MaxPathGradientBrushCount = 4_096;
    private const int MaxImportedSvgBitmapCacheEntries = 96;
    private const long MaxImportedSvgBitmapCacheBytes = 256L * 1024 * 1024;
    private const float FillEdgeCoverageWidthPixels = 0.8f;
    private readonly Dictionary<int, ID2D1SolidColorBrush> _brushCache = new(512);
    private ID2D1SolidColorBrush? _mixingBrush;
    private readonly Dictionary<(VectorScene Scene, int ObjectIndex), CachedMixingBrushBitmap> _mixingBrushBitmapCache = new();
    private readonly Dictionary<(VectorScene Scene, int ObjectIndex), CachedFreehandGeometry> _freehandGeometryCache = new();
    private readonly Dictionary<(VectorScene Scene, int ObjectIndex), CachedLineGeometry> _lineGeometryCache = new();
    private readonly Dictionary<(VectorScene Scene, int ObjectIndex), CachedObjectPathGeometry> _objectPathGeometryCache = new();
    private readonly Dictionary<(VectorScene Scene, int ObjectIndex), CachedGradientBrush> _gradientBrushCache = new();
    private readonly List<CachedGradientBrush> _transientGradientBrushes = [];
    private readonly Dictionary<(VectorScene Scene, int ObjectIndex), CachedShapeGradientBitmap> _shapeGradientBitmapCache = new();
    private readonly Dictionary<(VectorScene Scene, int ObjectIndex), CachedShapeGradientMaskGeometry> _shapeGradientMaskGeometryCache = new();
    private readonly List<CachedShapeGradientBitmap> _transientShapeGradientBitmaps = [];
    private readonly Dictionary<(VectorScene Scene, int ObjectIndex), CachedPathGradientBrushes> _pathGradientBrushCache = new();
    private readonly List<CachedPathGradientBrushes> _transientPathGradientBrushes = [];
    private readonly Dictionary<VectorScene, int> _freehandSceneObjectCounts = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<LodBitmapKey, CachedLodBitmap> _lodBitmapCache = new();
    private readonly Dictionary<ImportedSvgRasterKey, CachedImportedSvgBitmap> _importedSvgBitmapCache = new();
    private readonly SceneRenderOrderBuffer _renderOrder = new();
    private ID2D1Factory? _factory;
    private ID2D1HwndRenderTarget? _target;
    private ID2D1Bitmap? _baseFrameBitmap;
    private ID2D1Layer? _shapeGradientMaskLayer;
    private ID2D1PathGeometry? _fillEdgeBezierOverlayGeometry;
    private ID2D1StrokeStyle? _roundStrokeStyle;
    private ID2D1StrokeStyle? _previewBoundsStrokeStyle;
    private readonly Dictionary<(CapStyle Start, CapStyle End, bool MiterJoin), ID2D1StrokeStyle> _lineStrokeStyles = new();
    private SizeI _targetSize;
    private IntPtr _targetHwnd;
    private StageControl? _baseFrameStage;
    private SizeI _baseFrameSize;
    private long _targetGeneration;
    private long _baseFrameTargetGeneration = -1;
    private long _baseFramePresentationRevision = -1;
    private VectorScene? _baseFrameEditableScene;
    private long _baseFrameEditableGeometryRevision = -1;
    private VectorScene? _baseFrameUnderlayScene;
    private long _baseFrameUnderlayGeometryRevision = -1;
    private VectorScene? _baseFrameOnionSkinScene;
    private long _baseFrameOnionSkinGeometryRevision = -1;
    private RenderStats _baseFrameStats;
    private RenderStats _baseFrameEditableStats;
    private int _consecutiveFailures;
    private bool _disabled;
    private VectorScene? _cachedEditableScene;
    private VectorScene? _cachedUnderlayScene;
    private VectorScene? _cachedOnionSkinScene;
    private VectorScene? _cachedDragPreviewScene;
    private SceneMembershipStamp _cacheEditableMembership;
    private SceneMembershipStamp _cacheUnderlayMembership;
    private SceneMembershipStamp _cacheOnionSkinMembership;
    private SceneMembershipStamp _cacheDragPreviewMembership;
    private int _freehandGeometryCachePointCount;
    private int _pathGradientBrushCount;
    private long _importedSvgBitmapCacheBytes;
    private bool _hardwareTargetLogged;
    private long _fillEdgeBezierOverlayGeometryRevision = -1;
    private float _fillEdgeBezierOverlayGeometryCameraX;
    private float _fillEdgeBezierOverlayGeometryCameraY;
    private float _fillEdgeBezierOverlayGeometryZoom;
    private int _fillEdgeBezierOverlayGeometryWidth;
    private int _fillEdgeBezierOverlayGeometryHeight;

    private sealed record CachedFreehandGeometry(GdiPointF[] Points, ID2D1PathGeometry Geometry) : IDisposable
    {
        public void Dispose() => Geometry.Dispose();
    }

    private sealed record CachedLineGeometry(
        Vector2 Start,
        Vector2 Control1,
        Vector2 Control2,
        Vector2 End,
        ID2D1PathGeometry Geometry) : IDisposable
    {
        public bool Matches(Vector2 start, Vector2 control1, Vector2 control2, Vector2 end)
        {
            return Start == start && Control1 == control1 && Control2 == control2 && End == end;
        }

        public void Dispose() => Geometry.Dispose();
    }

    private sealed record CachedObjectPathGeometry(
        GdiPointF[][]? PathContoursIdentity,
        PathBezierNode[][]? PathBezierContoursIdentity,
        ID2D1PathGeometry Geometry) : IDisposable
    {
        public bool Matches(
            GdiPointF[][]? pathContoursIdentity,
            PathBezierNode[][]? pathBezierContoursIdentity)
        {
            return ReferenceEquals(PathContoursIdentity, pathContoursIdentity)
                && ReferenceEquals(PathBezierContoursIdentity, pathBezierContoursIdentity);
        }

        public void Dispose() => Geometry.Dispose();
    }

    private readonly record struct SceneMembershipStamp(VectorScene? Scene, int ObjectCount);

    private sealed class CachedGradientBrush : IDisposable
    {
        public CachedGradientBrush(
            GradientKind kind,
            GradientStop[] stops,
            ID2D1GradientStopCollection collection,
            ID2D1LinearGradientBrush? linearBrush,
            ID2D1RadialGradientBrush? radialBrush)
        {
            Kind = kind;
            Stops = stops;
            Collection = collection;
            LinearBrush = linearBrush;
            RadialBrush = radialBrush;
        }

        public GradientKind Kind { get; }
        public GradientStop[] Stops { get; }
        public ID2D1GradientStopCollection Collection { get; }
        public ID2D1LinearGradientBrush? LinearBrush { get; }
        public ID2D1RadialGradientBrush? RadialBrush { get; }
        public ID2D1Brush Brush => (ID2D1Brush?)LinearBrush ?? RadialBrush!;

        public bool Matches(GradientKind kind, IReadOnlyList<GradientStop> stops) =>
            Kind == kind && Stops.SequenceEqual(stops);

        public void SetAxis(Vector2 start, Vector2 end)
        {
            if (LinearBrush is not null)
            {
                LinearBrush.StartPoint = start;
                LinearBrush.EndPoint = end;
                return;
            }

            var radius = Math.Max(0.5f, Vector2.Distance(start, end));
            RadialBrush!.Center = start;
            RadialBrush.GradientOriginOffset = Vector2.Zero;
            RadialBrush.RadiusX = radius;
            RadialBrush.RadiusY = radius;
        }

        public void Dispose()
        {
            LinearBrush?.Dispose();
            RadialBrush?.Dispose();
            Collection.Dispose();
        }
    }

    private sealed class CachedShapeGradientBitmap : IDisposable
    {
        public CachedShapeGradientBitmap(
            GradientStop[] stops,
            GdiPointF center,
            GdiPointF[][]? localContoursIdentity,
            GdiPointF sourcePosition,
            float sourceAngle,
            GdiPointF[][] contours,
            GdiRectangleF worldBounds,
            int pixelWidth,
            int pixelHeight,
            byte[] positions,
            ID2D1Bitmap bitmap)
        {
            Stops = stops;
            Center = center;
            LocalContoursIdentity = localContoursIdentity;
            SourcePosition = sourcePosition;
            SourceAngle = sourceAngle;
            Contours = contours;
            WorldBounds = worldBounds;
            PixelWidth = pixelWidth;
            PixelHeight = pixelHeight;
            Positions = positions;
            Bitmap = bitmap;
        }

        public GradientStop[] Stops { get; }
        public GdiPointF Center { get; private set; }
        public GdiPointF[][]? LocalContoursIdentity { get; }
        public GdiPointF SourcePosition { get; private set; }
        public float SourceAngle { get; }
        public GdiPointF[][] Contours { get; }
        public GdiRectangleF WorldBounds { get; private set; }
        public int PixelWidth { get; }
        public int PixelHeight { get; }
        public byte[] Positions { get; }
        public ID2D1Bitmap Bitmap { get; }

        public bool MatchesSource(
            GdiPointF center,
            GdiPointF[][] localContoursIdentity,
            GdiPointF sourcePosition,
            float sourceAngle)
        {
            return Center == center
                && ReferenceEquals(LocalContoursIdentity, localContoursIdentity)
                && SourcePosition == sourcePosition
                && SourceAngle == sourceAngle;
        }

        public bool TryRelocateSource(
            GdiPointF center,
            GdiPointF[][] localContoursIdentity,
            GdiPointF sourcePosition,
            float sourceAngle)
        {
            if (!ReferenceEquals(LocalContoursIdentity, localContoursIdentity)
                || SourceAngle != sourceAngle)
            {
                return false;
            }

            var dx = sourcePosition.X - SourcePosition.X;
            var dy = sourcePosition.Y - SourcePosition.Y;
            if (new GdiPointF(Center.X + dx, Center.Y + dy) != center) return false;
            Center = center;
            SourcePosition = sourcePosition;
            WorldBounds = new GdiRectangleF(
                WorldBounds.X + dx,
                WorldBounds.Y + dy,
                WorldBounds.Width,
                WorldBounds.Height);
            return true;
        }

        public bool MatchesLayout(
            GdiPointF center,
            IReadOnlyList<GdiPointF[]> contours,
            GdiRectangleF worldBounds,
            int pixelWidth,
            int pixelHeight)
        {
            if (Center != center
                || WorldBounds != worldBounds
                || PixelWidth != pixelWidth
                || PixelHeight != pixelHeight
                || Contours.Length != contours.Count)
            {
                return false;
            }

            for (var contourIndex = 0; contourIndex < Contours.Length; contourIndex++)
            {
                if (!Contours[contourIndex].AsSpan().SequenceEqual(contours[contourIndex])) return false;
            }

            return true;
        }

        public void Dispose() => Bitmap.Dispose();
    }

    private sealed record CachedShapeGradientMaskGeometry(
        GdiPointF[][]? PathContoursIdentity,
        PathBezierNode[][]? PathBezierContoursIdentity,
        ShapeKind Shape,
        int ShapeVertexCount,
        GdiPointF Size,
        ID2D1PathGeometry Geometry) : IDisposable
    {
        public bool Matches(
            GdiPointF[][]? pathContoursIdentity,
            PathBezierNode[][]? pathBezierContoursIdentity,
            ShapeKind shape,
            int shapeVertexCount,
            GdiPointF size)
        {
            return ReferenceEquals(PathContoursIdentity, pathContoursIdentity)
                && ReferenceEquals(PathBezierContoursIdentity, pathBezierContoursIdentity)
                && Shape == shape
                && ShapeVertexCount == shapeVertexCount
                && Size == size;
        }

        public void Dispose() => Geometry.Dispose();
    }

    private sealed class CachedPathGradientBrushes : IDisposable
    {
        public CachedPathGradientBrushes(
            GradientStop[] stops,
            GradientPathGradientSegment[] segments,
            ID2D1GradientStopCollection collection,
            ID2D1LinearGradientBrush[] brushes)
        {
            Stops = stops;
            Segments = segments;
            Collection = collection;
            Brushes = brushes;
        }

        public GradientStop[] Stops { get; }
        public GradientPathGradientSegment[] Segments { get; }
        public ID2D1GradientStopCollection Collection { get; }
        public ID2D1LinearGradientBrush[] Brushes { get; }

        public bool Matches(IReadOnlyList<GradientStop> stops, IReadOnlyList<GradientPathGradientSegment> segments)
        {
            return Stops.SequenceEqual(stops) && Segments.SequenceEqual(segments);
        }

        public void Dispose()
        {
            foreach (var brush in Brushes) brush.Dispose();
            Collection.Dispose();
        }
    }

    private readonly record struct LodBitmapKey(VectorScene Scene, bool Overview);

    private sealed record CachedLodBitmap(long SummaryRevision, ID2D1Bitmap Bitmap) : IDisposable
    {
        public void Dispose() => Bitmap.Dispose();
    }

    private sealed record CachedImportedSvgBitmap(long PixelBytes, ID2D1Bitmap Bitmap) : IDisposable
    {
        public void Dispose() => Bitmap.Dispose();
    }

    private sealed record CachedMixingBrushBitmap(
        MixingBrushRegionRaster Raster,
        ID2D1Bitmap Bitmap) : IDisposable
    {
        public void Dispose() => Bitmap.Dispose();
    }

    public bool IsActive => !_disabled && _target is not null;
    public bool HardwareAccelerationActive => IsActive;
    public bool ImmediatePresentationEnabled => true;
    public double LastCacheMaintenanceMilliseconds { get; private set; }
    public double LastCommandMilliseconds { get; private set; }
    public double LastPresentMilliseconds { get; private set; }
    public int LastLodBitmapSubmissions { get; private set; }
    public int LastLodBitmapBuilds { get; private set; }
    public int LastLodDetailObjectDraws { get; private set; }
    public int LastGradientBrushCacheBuilds { get; private set; }
    public int LastGradientBrushCacheReuses { get; private set; }
    public int LastShapeGradientBitmapCacheBuilds { get; private set; }
    public int LastShapeGradientBitmapCacheReuses { get; private set; }
    public int LastShapeGradientMaskGeometryCacheBuilds { get; private set; }
    public int LastShapeGradientMaskGeometryCacheReuses { get; private set; }
    public int LastPathGradientBrushCacheBuilds { get; private set; }
    public int LastPathGradientBrushCacheReuses { get; private set; }
    public int LastLineGeometryCacheBuilds { get; private set; }
    public int LastLineGeometryCacheReuses { get; private set; }
    public int LastObjectPathGeometryCacheBuilds { get; private set; }
    public int LastObjectPathGeometryCacheReuses { get; private set; }
    public int LastFillEdgeBezierOverlayGeometryBuilds { get; private set; }
    public int LastBaseFrameCacheBuilds { get; private set; }
    public int LastBaseFrameCacheReuses { get; private set; }
    public double LastBaseFrameCopyMilliseconds { get; private set; }

    internal bool HasCachedFreehandGeometry(VectorScene scene)
    {
        return _freehandGeometryCache.Keys.Any(key => ReferenceEquals(key.Scene, scene));
    }

    public bool TryRender(StageControl stage, out RenderStats stats)
    {
        stats = default;
        LastCacheMaintenanceMilliseconds = 0;
        LastLodBitmapSubmissions = 0;
        LastLodBitmapBuilds = 0;
        LastLodDetailObjectDraws = 0;
        LastGradientBrushCacheBuilds = 0;
        LastGradientBrushCacheReuses = 0;
        LastShapeGradientBitmapCacheBuilds = 0;
        LastShapeGradientBitmapCacheReuses = 0;
        LastShapeGradientMaskGeometryCacheBuilds = 0;
        LastShapeGradientMaskGeometryCacheReuses = 0;
        LastPathGradientBrushCacheBuilds = 0;
        LastPathGradientBrushCacheReuses = 0;
        LastLineGeometryCacheBuilds = 0;
        LastLineGeometryCacheReuses = 0;
        LastObjectPathGeometryCacheBuilds = 0;
        LastObjectPathGeometryCacheReuses = 0;
        LastFillEdgeBezierOverlayGeometryBuilds = 0;
        LastBaseFrameCacheBuilds = 0;
        LastBaseFrameCacheReuses = 0;
        LastBaseFrameCopyMilliseconds = 0;
        if (RequiresSoftwareLayerCompositing(stage) || RequiresSoftwareDistortion(stage))
        {
            ClearBaseFrameCache();
            return false;
        }
        ClearTransientGradientBrushes();
        ClearTransientShapeGradientBitmaps();
        ClearTransientPathGradientBrushes();
        if (_disabled || stage.Width <= 0 || stage.Height <= 0) return false;
        var drawingStarted = false;
        var editableScene = stage.Scene;

        try
        {
            EnsureTarget(stage);
            if (_target is null) return false;
            var cacheMaintenanceStarted = Stopwatch.GetTimestamp();
            PrepareFreehandGeometryCache(editableScene, stage.UnderlayScene, stage.OnionSkinScene, stage.DragPreviewScene);
            if (CacheMembershipChanged(
                    editableScene,
                    stage.UnderlayScene,
                    stage.OnionSkinScene,
                    stage.DragPreviewScene))
            {
                PruneLineGeometryCache(editableScene, stage.UnderlayScene, stage.OnionSkinScene, stage.DragPreviewScene);
                PruneObjectPathGeometryCache(editableScene, stage.UnderlayScene, stage.OnionSkinScene, stage.DragPreviewScene);
                PruneLodBitmapCache(editableScene, stage.UnderlayScene, stage.OnionSkinScene, stage.DragPreviewScene);
                PruneGradientBrushCache(editableScene, stage.UnderlayScene, stage.OnionSkinScene, stage.DragPreviewScene);
                PruneShapeGradientBitmapCache(editableScene, stage.UnderlayScene, stage.OnionSkinScene, stage.DragPreviewScene);
                PruneShapeGradientMaskGeometryCache(editableScene, stage.UnderlayScene, stage.OnionSkinScene, stage.DragPreviewScene);
                PrunePathGradientBrushCache(editableScene, stage.UnderlayScene, stage.OnionSkinScene, stage.DragPreviewScene);
                PruneMixingBrushBitmapCache(editableScene, stage.UnderlayScene, stage.OnionSkinScene, stage.DragPreviewScene);
            }
            LastCacheMaintenanceMilliseconds = Stopwatch.GetElapsedTime(cacheMaintenanceStarted).TotalMilliseconds;

            if (stage.RendersReferenceProjection) ClearBaseFrameCache();

            _target.BeginDraw();
            drawingStarted = true;
            var commandStarted = Stopwatch.GetTimestamp();
            _target.Transform = Matrix3x2.Identity;
            _target.AntialiasMode = AntialiasMode.PerPrimitive;
            stage.BeginScenePassOrder();
            if (stage.RendersReferenceProjection)
            {
                var background = ToD2D(stage.BackColor);
                _target.Clear(in background);
                DrawGrid(stage);
                Draw3DReferenceGrid(stage);
                stats = DrawReference3DScene(stage);
                if (stage.DragPreviewScene is { } projectedDragPreview)
                {
                    stage.Scene = projectedDragPreview;
                    try
                    {
                        DrawReference3DCurrentScene(stage);
                    }
                    finally
                    {
                        stage.Scene = editableScene;
                    }
                }
                DrawReference3DSelection(stage);
                DrawTransformOverlay(stage);
                DrawDistortOverlay(stage);
                DrawMarquee(stage);
                if (stage.ReferenceDimension == SceneDimension.ThreeD) DrawSpatialTransformGizmo(stage);
                LastCommandMilliseconds = Stopwatch.GetElapsedTime(commandStarted).TotalMilliseconds;
                var presentStarted = Stopwatch.GetTimestamp();
                var gridResult = _target.EndDraw();
                LastPresentMilliseconds = Stopwatch.GetElapsedTime(presentStarted).TotalMilliseconds;
                drawingStarted = false;
                if (gridResult.Failure)
                {
                    RecordFailure();
                    ResetTarget();
                    return false;
                }

                _consecutiveFailures = 0;
                return true;
            }

            var objectDrawLimit = ObjectDrawLimit(stage.Zoom);
            RenderStats editableStats;
            if (CanReuseBaseFrame(stage))
            {
                DrawBaseFrame();
                ReplayBaseScenePassOrder(stage);
                stats = _baseFrameStats;
                editableStats = _baseFrameEditableStats;
                LastBaseFrameCacheReuses = 1;
            }
            else
            {
                var background = ToD2D(stage.BackColor);
                _target.Clear(in background);
                DrawGrid(stage);

                var underlayStats = default(RenderStats);
                var onionSkinStats = default(RenderStats);
                var underlay = stage.UnderlayScene;
                var underlayLimit = objectDrawLimit;
                var forceEditableObjectRenderer = stage.SelectionFillDragFrontActive
                    || stage.FillEdgeBezierPointerEditing
                        && UsesObjectRenderer(stage, editableScene, stage.Zoom);
                if (underlay is not null
                    && UsesObjectRenderer(stage, underlay, stage.Zoom)
                    && (forceEditableObjectRenderer || UsesObjectRenderer(stage, editableScene, stage.Zoom)))
                {
                    underlayLimit = objectDrawLimit - Math.Min(editableScene.ObjectCount, objectDrawLimit * 3 / 4);
                }

                if (stage.OnionSkinScene is { } onionSkin)
                {
                    var reservedUnderlayObjects = underlay is not null && UsesObjectRenderer(stage, underlay, stage.Zoom)
                        ? Math.Min(underlay.ObjectCount, underlayLimit)
                        : 0;
                    var editableCapacity = Math.Max(0, objectDrawLimit - reservedUnderlayObjects);
                    var reservedEditableObjects = forceEditableObjectRenderer || UsesObjectRenderer(stage, editableScene, stage.Zoom)
                        ? Math.Min(editableScene.ObjectCount, editableCapacity)
                        : 0;
                    var onionSkinLimit = Math.Max(0, editableCapacity - reservedEditableObjects);
                    stage.Scene = onionSkin;
                    stage.RecordOnionSkinScenePass();
                    onionSkinStats = DrawScene(stage, onionSkinLimit);
                }

                if (underlay is not null)
                {
                    stage.Scene = underlay;
                    stage.RecordUnderlayScenePass();
                    underlayStats = DrawScene(stage, underlayLimit);
                }

                stage.Scene = editableScene;
                var editableLimit = Math.Max(0, objectDrawLimit - underlayStats.DrawnObjects - onionSkinStats.DrawnObjects);
                stage.RecordEditableScenePass();
                editableStats = DrawScene(
                    stage,
                    editableLimit,
                    forceObjectRenderer: forceEditableObjectRenderer);
                stats = RenderStats.Combine(RenderStats.Combine(onionSkinStats, underlayStats), editableStats);
                StoreBaseFrame(stage, stats, editableStats);
            }

            if (stage.DragPreviewScene is { } dragPreview)
            {
                stage.Scene = dragPreview;
                try
                {
                    DrawScene(stage, Math.Min(objectDrawLimit, 80_000));
                }
                finally
                {
                    stage.Scene = editableScene;
                }
            }

            if (editableStats.TileLod) LastLodDetailObjectDraws = DrawLodDetailObjects(stage);
            if (!stage.MarqueeLodPreviewActive) DrawActiveMaskOutline(stage);
            DrawSelection(stage);
            DrawFillEdgeBezierOverlay(stage);
            DrawPenAnchorGuides(stage);
            DrawDrawingPreview(stage);
            DrawPenDirectionHandles(stage);
            DrawFreehandPreview(stage);
            DrawFillPreview(stage);
            DrawFillAnimation(stage);
            DrawGradientOverlay(stage);
            DrawMarquee(stage);
            DrawBrushTipCursor(stage);
            DrawBrushColorPalette(stage);
            DrawFillToolCursor(stage);

            LastCommandMilliseconds = Stopwatch.GetElapsedTime(commandStarted).TotalMilliseconds;
            var framePresentStarted = Stopwatch.GetTimestamp();
            var result = _target.EndDraw();
            LastPresentMilliseconds = Stopwatch.GetElapsedTime(framePresentStarted).TotalMilliseconds;
            drawingStarted = false;
            if (result.Failure)
            {
                RecordFailure();
                ResetTarget();
                return false;
            }

            _consecutiveFailures = 0;
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Direct2D rendering failed; falling back to GDI renderer", ex);
            if (drawingStarted && _target is not null)
            {
                try
                {
                    _target.EndDraw();
                }
                catch
                {
                    // The render target is discarded below.
                }
            }

            RecordFailure();
            ResetTarget();
            return false;
        }
        finally
        {
            ClearTransientGradientBrushes();
            ClearTransientShapeGradientBitmaps();
            ClearTransientPathGradientBrushes();
            stage.Scene = editableScene;
        }
    }

    private static bool RequiresSoftwareLayerCompositing(StageControl stage)
    {
        return stage.Scene.HasNonNormalLayerBlendModes
            || stage.UnderlayScene?.HasNonNormalLayerBlendModes == true
            || stage.OnionSkinScene?.HasNonNormalLayerBlendModes == true
            || stage.DragPreviewScene?.HasNonNormalLayerBlendModes == true;
    }

    private static bool RequiresSoftwareDistortion(StageControl stage)
    {
        return stage.SceneHasDistortionsForRendering(stage.Scene)
            || stage.SceneHasDistortionsForRendering(stage.UnderlayScene)
            || stage.SceneHasDistortionsForRendering(stage.OnionSkinScene)
            || stage.SceneHasDistortionsForRendering(stage.DragPreviewScene);
    }

    private bool CanReuseBaseFrame(StageControl stage)
    {
        return _baseFrameBitmap is not null
            && ReferenceEquals(_baseFrameStage, stage)
            && _baseFramePresentationRevision == stage.BasePresentationRevision
            && BaseFrameSceneMatches(
                _baseFrameEditableScene,
                _baseFrameEditableGeometryRevision,
                stage.Scene)
            && BaseFrameSceneMatches(
                _baseFrameUnderlayScene,
                _baseFrameUnderlayGeometryRevision,
                stage.UnderlayScene)
            && BaseFrameSceneMatches(
                _baseFrameOnionSkinScene,
                _baseFrameOnionSkinGeometryRevision,
                stage.OnionSkinScene)
            && _baseFrameTargetGeneration == _targetGeneration
            && _baseFrameSize.Width == _targetSize.Width
            && _baseFrameSize.Height == _targetSize.Height;
    }

    private static bool BaseFrameSceneMatches(
        VectorScene? cachedScene,
        long cachedGeometryRevision,
        VectorScene? scene)
    {
        return ReferenceEquals(cachedScene, scene)
            && cachedGeometryRevision == (scene?.GeometryRevision ?? -1);
    }

    private void DrawBaseFrame()
    {
        var bounds = Rect(0, 0, _targetSize.Width, _targetSize.Height);
        _target!.DrawBitmap(
            _baseFrameBitmap!,
            bounds,
            1,
            BitmapInterpolationMode.NearestNeighbor,
            bounds);
    }

    private static void ReplayBaseScenePassOrder(StageControl stage)
    {
        if (stage.OnionSkinScene is not null) stage.RecordOnionSkinScenePass();
        if (stage.UnderlayScene is not null) stage.RecordUnderlayScenePass();
        stage.RecordEditableScenePass();
    }

    private void StoreBaseFrame(StageControl stage, RenderStats stats, RenderStats editableStats)
    {
        if (_target is null) return;

        try
        {
            if (_baseFrameBitmap is not null
                && (_baseFrameTargetGeneration != _targetGeneration
                    || _baseFrameSize.Width != _targetSize.Width
                    || _baseFrameSize.Height != _targetSize.Height))
            {
                ClearBaseFrameCache();
            }

            if (_baseFrameBitmap is null)
            {
                var properties = new BitmapProperties(
                    new PixelFormat(Format.B8G8R8A8_UNorm, DCommonAlphaMode.Ignore),
                    96,
                    96);
                _baseFrameBitmap = _target.CreateBitmap(
                    _targetSize,
                    IntPtr.Zero,
                    0,
                    properties);
            }

            var copyStarted = Stopwatch.GetTimestamp();
            var result = _baseFrameBitmap.CopyFromRenderTarget(_target);
            LastBaseFrameCopyMilliseconds = Stopwatch.GetElapsedTime(copyStarted).TotalMilliseconds;
            if (result.Failure)
            {
                ClearBaseFrameCache();
                return;
            }

            _baseFrameStage = stage;
            _baseFrameSize = _targetSize;
            _baseFrameTargetGeneration = _targetGeneration;
            _baseFramePresentationRevision = stage.BasePresentationRevision;
            _baseFrameEditableScene = stage.Scene;
            _baseFrameEditableGeometryRevision = stage.Scene.GeometryRevision;
            _baseFrameUnderlayScene = stage.UnderlayScene;
            _baseFrameUnderlayGeometryRevision = stage.UnderlayScene?.GeometryRevision ?? -1;
            _baseFrameOnionSkinScene = stage.OnionSkinScene;
            _baseFrameOnionSkinGeometryRevision = stage.OnionSkinScene?.GeometryRevision ?? -1;
            _baseFrameStats = stats;
            _baseFrameEditableStats = editableStats;
            LastBaseFrameCacheBuilds = 1;
        }
        catch
        {
            ClearBaseFrameCache();
        }
    }

    private void ClearBaseFrameCache()
    {
        _baseFrameBitmap?.Dispose();
        _baseFrameBitmap = null;
        _baseFrameStage = null;
        _baseFrameSize = default;
        _baseFrameTargetGeneration = -1;
        _baseFramePresentationRevision = -1;
        _baseFrameEditableScene = null;
        _baseFrameEditableGeometryRevision = -1;
        _baseFrameUnderlayScene = null;
        _baseFrameUnderlayGeometryRevision = -1;
        _baseFrameOnionSkinScene = null;
        _baseFrameOnionSkinGeometryRevision = -1;
        _baseFrameStats = default;
        _baseFrameEditableStats = default;
    }

    public void Resize(System.Drawing.Size clientSize)
    {
        if (_target is null || clientSize.Width <= 0 || clientSize.Height <= 0) return;
        var next = new SizeI(clientSize.Width, clientSize.Height);
        if (next.Width == _targetSize.Width && next.Height == _targetSize.Height) return;
        try
        {
            ClearBaseFrameCache();
            _shapeGradientMaskLayer?.Dispose();
            _shapeGradientMaskLayer = null;
            _target.Resize(next);
            _targetSize = next;
        }
        catch (Exception ex)
        {
            AppLog.Error("Direct2D resize failed; render target will be recreated", ex);
            RecordFailure();
            ResetTarget();
        }
    }

    public void ReleaseTarget() => ResetTarget();

    internal void InvalidateFillEdgeBezierOverlay() => ClearFillEdgeBezierOverlayGeometry();

    public void ReloadRuntimeResources()
    {
        ResetTarget();
        ClearFreehandGeometryCache();
        ClearShapeGradientMaskGeometryCache();
        _roundStrokeStyle?.Dispose();
        _roundStrokeStyle = null;
        _previewBoundsStrokeStyle?.Dispose();
        _previewBoundsStrokeStyle = null;
        DisposeLineStrokeStyles();
        _factory?.Dispose();
        _factory = null;
        _consecutiveFailures = 0;
        _disabled = false;
        _hardwareTargetLogged = false;
        LastCacheMaintenanceMilliseconds = 0;
        LastCommandMilliseconds = 0;
        LastPresentMilliseconds = 0;
        LastLodBitmapSubmissions = 0;
        LastLodBitmapBuilds = 0;
        LastLodDetailObjectDraws = 0;
        LastGradientBrushCacheBuilds = 0;
        LastGradientBrushCacheReuses = 0;
        LastShapeGradientBitmapCacheBuilds = 0;
        LastShapeGradientBitmapCacheReuses = 0;
        LastShapeGradientMaskGeometryCacheBuilds = 0;
        LastShapeGradientMaskGeometryCacheReuses = 0;
        LastPathGradientBrushCacheBuilds = 0;
        LastPathGradientBrushCacheReuses = 0;
        LastLineGeometryCacheBuilds = 0;
        LastLineGeometryCacheReuses = 0;
        LastObjectPathGeometryCacheBuilds = 0;
        LastObjectPathGeometryCacheReuses = 0;
        LastFillEdgeBezierOverlayGeometryBuilds = 0;
        LastBaseFrameCacheBuilds = 0;
        LastBaseFrameCacheReuses = 0;
        LastBaseFrameCopyMilliseconds = 0;
    }

    public void Dispose()
    {
        ResetTarget();
        ClearFreehandGeometryCache();
        ClearShapeGradientMaskGeometryCache();
        _roundStrokeStyle?.Dispose();
        _roundStrokeStyle = null;
        _previewBoundsStrokeStyle?.Dispose();
        _previewBoundsStrokeStyle = null;
        DisposeLineStrokeStyles();
        _factory?.Dispose();
        _factory = null;
    }

    private void EnsureTarget(StageControl stage)
    {
        _factory ??= D2D1.D2D1CreateFactory<ID2D1Factory>(FactoryType.SingleThreaded, DebugLevel.None);
        var nextSize = new SizeI(Math.Max(1, stage.Width), Math.Max(1, stage.Height));
        var nextHwnd = stage.Handle;
        if (_target is not null && _targetHwnd != nextHwnd) ResetTarget();
        if (_target is not null)
        {
            if (nextSize.Width != _targetSize.Width || nextSize.Height != _targetSize.Height)
            {
                Resize(stage.ClientSize);
            }

            return;
        }

        var pixelFormat = new PixelFormat(Format.B8G8R8A8_UNorm, DCommonAlphaMode.Ignore);
        var renderTargetProperties = new RenderTargetProperties(RenderTargetType.Hardware, pixelFormat, 96, 96, RenderTargetUsage.None, FeatureLevel.Default);
        var hwndProperties = new HwndRenderTargetProperties
        {
            Hwnd = stage.Handle,
            PixelSize = nextSize,
            PresentOptions = PresentOptions.Immediately
        };

        _target = _factory.CreateHwndRenderTarget(renderTargetProperties, hwndProperties);
        _targetGeneration++;
        _targetSize = nextSize;
        _targetHwnd = nextHwnd;
        if (!_hardwareTargetLogged)
        {
            AppLog.Info("Direct2D GPU hardware render target active with immediate presentation.");
            _hardwareTargetLogged = true;
        }
    }

    private RenderStats DrawScene(
        StageControl stage,
        int objectDrawLimit,
        bool forceObjectRenderer = false)
    {
        var pixelZoom = EffectivePixelZoom(stage.Zoom);
        if (forceObjectRenderer)
        {
            return DrawObjects(stage, int.MaxValue);
        }
        if (stage.HasSceneCompositionMaskClips(stage.Scene)) return DrawObjects(stage, objectDrawLimit);
        if (SceneRenderOrder.RequiresObjectRenderer(stage.Scene)) return DrawObjects(stage, objectDrawLimit);
        if (stage.MarqueeLodPreviewActive && stage.Scene.ObjectCount > 0)
        {
            return pixelZoom < 0.08f
                ? DrawOverviewTiles(stage)
                : DrawTiles(stage);
        }
        if (stage.ZoomLodPreviewActive
            && stage.Scene.ObjectCount >= SceneRenderOrder.DenseObjectLodMinimumVisibleObjects
            && !stage.Scene.HasDisplayLayerEffects)
        {
            return pixelZoom < 0.08f
                ? DrawOverviewTiles(stage)
                : DrawTiles(stage);
        }
        if (stage.Scene.HasDisplayLayerEffects || pixelZoom >= 0.18f) return DrawObjects(stage, objectDrawLimit);
        if (stage.Scene.ObjectCount >= 5000)
        {
            return pixelZoom < 0.08f
                ? DrawOverviewTiles(stage)
                : DrawTiles(stage);
        }

        if (stage.Scene.ObjectCount < SceneRenderOrder.DenseObjectLodMinimumVisibleObjects)
        {
            return DrawObjects(stage, objectDrawLimit);
        }

        var bounds = stage.VisibleWorldBounds();
        _renderOrder.Collect(stage.Scene, bounds, stage.Frame);
        if (SceneRenderOrder.ShouldUseDenseObjectLod(stage.Scene, pixelZoom, bounds, _renderOrder))
        {
            return pixelZoom < 0.08f
                ? DrawOverviewTiles(stage)
                : DrawTiles(stage);
        }

        return DrawCollectedObjects(stage, objectDrawLimit);
    }

    private static int ObjectDrawLimit(float zoom) => EffectivePixelZoom(zoom) < 0.35f ? 95_000 : 220_000;

    private static bool UsesObjectRenderer(StageControl stage, VectorScene scene, float zoom)
    {
        return scene.ObjectCount > 0
            && (stage.HasSceneCompositionMaskClips(scene)
                || SceneRenderOrder.RequiresObjectRenderer(scene)
                || scene.ObjectCount < 5000
                || scene.HasDisplayLayerEffects
                || EffectivePixelZoom(zoom) >= 0.18f);
    }

    private static float EffectivePixelZoom(float zoom) => zoom * VectorUnits.PixelsPerUnit;

    private RenderStats DrawOverviewTiles(StageControl stage)
    {
        var scene = stage.Scene;
        var tileW = scene.StageWidth / scene.OverviewColumnCount;
        var tileH = scene.StageHeight / scene.OverviewRowCount;
        var viewport = stage.VisibleWorldBounds();
        var left = viewport.Left;
        var right = viewport.Right;
        var top = viewport.Top;
        var bottom = viewport.Bottom;
        var minX = (int)Math.Clamp((left + scene.StageWidth * 0.5f) / tileW, 0, scene.OverviewColumnCount - 1);
        var maxX = (int)Math.Clamp((right + scene.StageWidth * 0.5f) / tileW, 0, scene.OverviewColumnCount - 1);
        var minY = (int)Math.Clamp((top + scene.StageHeight * 0.5f) / tileH, 0, scene.OverviewRowCount - 1);
        var maxY = (int)Math.Clamp((bottom + scene.StageHeight * 0.5f) / tileH, 0, scene.OverviewRowCount - 1);
        var draws = 0;
        long atoms = 0;

        for (var ty = minY; ty <= maxY; ty++)
        {
            for (var tx = minX; tx <= maxX; tx++)
            {
                var tile = ty * scene.OverviewColumnCount + tx;
                var count = scene.OverviewCount[tile];
                if (count <= 0) continue;
                atoms += scene.OverviewAtoms[tile];
                draws++;
            }
        }

        if (draws > 0) DrawLodBitmap(stage, overview: true);

        return new RenderStats(0, 0, atoms, draws, 0, true);
    }

    private RenderStats DrawTiles(StageControl stage)
    {
        var scene = stage.Scene;
        var tileW = scene.StageWidth / scene.TileColumnCount;
        var tileH = scene.StageHeight / scene.TileRowCount;
        var viewport = stage.VisibleWorldBounds();
        var left = viewport.Left;
        var right = viewport.Right;
        var top = viewport.Top;
        var bottom = viewport.Bottom;
        var minX = (int)Math.Clamp((left + scene.StageWidth * 0.5f) / tileW, 0, scene.TileColumnCount - 1);
        var maxX = (int)Math.Clamp((right + scene.StageWidth * 0.5f) / tileW, 0, scene.TileColumnCount - 1);
        var minY = (int)Math.Clamp((top + scene.StageHeight * 0.5f) / tileH, 0, scene.TileRowCount - 1);
        var maxY = (int)Math.Clamp((bottom + scene.StageHeight * 0.5f) / tileH, 0, scene.TileRowCount - 1);
        var draws = 0;
        long atoms = 0;

        for (var ty = minY; ty <= maxY; ty++)
        {
            for (var tx = minX; tx <= maxX; tx++)
            {
                var tile = ty * scene.TileColumnCount + tx;
                var count = scene.TileCount[tile];
                if (count <= 0) continue;
                atoms += scene.TileAtoms[tile];
                draws++;
            }
        }

        if (draws > 0) DrawLodBitmap(stage, overview: false);

        return new RenderStats(0, 0, atoms, draws, 0, true);
    }

    private void DrawLodBitmap(StageControl stage, bool overview)
    {
        var scene = stage.Scene;
        var bitmap = LodBitmap(scene, overview);
        var topLeft = stage.WorldToScreen(-scene.StageWidth * 0.5f, -scene.StageHeight * 0.5f);
        var bottomRight = stage.WorldToScreen(scene.StageWidth * 0.5f, scene.StageHeight * 0.5f);
        var destination = Rect(topLeft.X, topLeft.Y, bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y);
        var columns = overview ? scene.OverviewColumnCount : scene.TileColumnCount;
        var rows = overview ? scene.OverviewRowCount : scene.TileRowCount;
        var source = Rect(0, 0, columns, rows);
        _target!.DrawBitmap(bitmap, destination, 1, BitmapInterpolationMode.NearestNeighbor, source);
        LastLodBitmapSubmissions++;
    }

    private ID2D1Bitmap LodBitmap(VectorScene scene, bool overview)
    {
        if (_target is null) throw new InvalidOperationException("Direct2D render target is not ready.");
        var key = new LodBitmapKey(scene, overview);
        if (_lodBitmapCache.TryGetValue(key, out var cached)
            && cached.SummaryRevision == scene.SummaryRevision)
        {
            return cached.Bitmap;
        }

        var columns = overview ? scene.OverviewColumnCount : scene.TileColumnCount;
        var rows = overview ? scene.OverviewRowCount : scene.TileRowCount;
        var counts = overview ? scene.OverviewCount : scene.TileCount;
        var colors = overview ? scene.OverviewArgb : scene.TileArgb;
        var pixelCount = columns * rows;
        var pixels = ArrayPool<int>.Shared.Rent(pixelCount);
        try
        {
            ParallelBatch.For(pixelCount, 4096, (_, start, end) =>
            {
                for (var pixel = start; pixel < end; pixel++)
                {
                    pixels[pixel] = counts[pixel] > 0 ? PremultiplyArgb(colors[pixel]) : 0;
                }
            });

            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            ID2D1Bitmap bitmap;
            try
            {
                var properties = new BitmapProperties(
                    new PixelFormat(Format.B8G8R8A8_UNorm, DCommonAlphaMode.Premultiplied),
                    96,
                    96);
                bitmap = _target.CreateBitmap(
                    new SizeI(columns, rows),
                    handle.AddrOfPinnedObject(),
                    (uint)(columns * sizeof(int)),
                    properties);
            }
            finally
            {
                handle.Free();
            }

            _lodBitmapCache[key] = new CachedLodBitmap(scene.SummaryRevision, bitmap);
            cached?.Dispose();
            LastLodBitmapBuilds++;
            return bitmap;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(pixels);
        }
    }

    private static int PremultiplyArgb(int argb)
    {
        var alpha = (argb >>> 24) & 0xff;
        if (alpha == 0) return 0;
        if (alpha == 0xff) return argb;
        var red = ((argb >>> 16) & 0xff) * alpha + 127;
        var green = ((argb >>> 8) & 0xff) * alpha + 127;
        var blue = (argb & 0xff) * alpha + 127;
        return (alpha << 24) | ((red / 255) << 16) | ((green / 255) << 8) | (blue / 255);
    }

    private RenderStats DrawObjects(StageControl stage, int drawLimit)
    {
        var scene = stage.Scene;
        var bounds = stage.VisibleWorldBounds();
        _renderOrder.Collect(scene, bounds, stage.Frame);

        return DrawCollectedObjects(stage, drawLimit);
    }

    private RenderStats DrawCollectedObjects(StageControl stage, int drawLimit)
    {
        var scene = stage.Scene;
        var drawn = _renderOrder.DrawLayers(
            scene,
            drawLimit,
            (layer, objects, start) => DrawLayerObjects(stage, scene, layer, objects, start));
        return new RenderStats(_renderOrder.VisibleCount, drawn, _renderOrder.VisibleAtoms, 0, _renderOrder.ScannedCount, false);
    }

    private int DrawLodDetailObjects(StageControl stage)
    {
        var drawn = 0;
        foreach (var objectIndex in stage.GetLodDetailObjectIndices())
        {
            var scene = stage.Scene;
            if (!scene.IsObjectActive(objectIndex, stage.Frame)
                || !scene.ShouldRenderLayerContent(scene.ObjectLayer[objectIndex]))
            {
                continue;
            }

            if (scene.IsLayerEffectivelyOutlined(scene.ObjectLayer[objectIndex]))
            {
                DrawWithSceneCompositionMaskClips(
                    stage,
                    objectIndex,
                    () => DrawObjectOutline(stage, objectIndex),
                    reference3D: false);
            }
            else
            {
                if (SceneRenderOrder.HasFill(scene.ShapeKind[objectIndex])) DrawObject(stage, objectIndex, SceneRenderPass.Fill);
                if (SceneRenderOrder.HasStroke(scene.ShapeKind[objectIndex], scene.Stroke[objectIndex])) DrawObject(stage, objectIndex, SceneRenderPass.Stroke);
            }
            drawn++;
        }

        return drawn;
    }

    private void DrawActiveMaskOutline(StageControl stage)
    {
        if (!stage.TryGetActiveMaskLayer(out var maskLayer)) return;
        var scene = stage.Scene;
        var maskObjects = Enumerable.Range(0, scene.ObjectCount)
            .Where(index => scene.ObjectLayer[index] == maskLayer
                && scene.IsObjectActive(index, stage.Frame)
                && SceneRenderOrder.HasFill(scene.ShapeKind[index]))
            .ToArray();
        if (maskObjects.Length == 0) return;

        using var geometry = CreateMaskGeometry(stage, scene, maskObjects);
        if (geometry is null) return;
        _target!.FillGeometry(geometry, BrushFor(GdiColor.FromArgb(48, 112, 205, 209).ToArgb()));
        _target.DrawGeometry(geometry, BrushFor(GdiColor.FromArgb(110, 104, 231, 232).ToArgb()), 4f);
        _target.DrawGeometry(geometry, BrushFor(GdiColor.FromArgb(244, 159, 242, 242).ToArgb()), 1.4f);
    }

    private void DrawLayerObjects(StageControl stage, VectorScene scene, int layer, IReadOnlyList<int> objects, int start)
    {
        var layerKind = scene.GetLayerKind(layer);
        if (!scene.ShouldRenderLayerContent(layer)) return;
        if (layerKind == DrawingLayerKind.Mask)
        {
            DrawLayerObjectsUnmasked(stage, scene, layer, objects, start);
            return;
        }
        if (!scene.TryGetMaskLayerIndex(layer, out var maskLayer))
        {
            DrawLayerObjectsUnmasked(stage, scene, layer, objects, start);
            return;
        }

        if (!scene.IsLayerEffectivelyVisible(maskLayer)) return;
        using var maskGeometry = CreateMaskGeometry(stage, scene, _renderOrder.GetLayerObjects(maskLayer));
        if (maskGeometry is null) return;

        using var targetLayer = _target!.CreateLayer();
        var parameters = new LayerParameters
        {
            ContentBounds = Rect(0, 0, _targetSize.Width, _targetSize.Height),
            GeometricMask = maskGeometry,
            MaskAntialiasMode = AntialiasMode.PerPrimitive,
            MaskTransform = Matrix3x2.Identity,
            Opacity = 1f,
            OpacityBrush = null,
            LayerOptions = LayerOptions.None
        };
        _target.PushLayer(parameters, targetLayer);
        try
        {
            DrawLayerObjectsUnmasked(stage, scene, layer, objects, start);
        }
        finally
        {
            _target.PopLayer();
        }
    }

    private void DrawLayerObjectsUnmasked(
        StageControl stage,
        VectorScene scene,
        int layer,
        IReadOnlyList<int> objects,
        int start)
    {
        var outlineLayerColor = scene.GetEffectiveLayerOutlineColor(layer);
        if (!outlineLayerColor.IsEmpty)
        {
            for (var index = start; index < objects.Count; index++)
            {
                var objectIndex = objects[index];
                DrawWithSceneCompositionMaskClips(
                    stage,
                    objectIndex,
                    () => DrawObjectOutline(stage, objectIndex, outlineLayerColor),
                    reference3D: false);
            }
            return;
        }

        SceneRenderOrder.DrawObjectPasses(
            scene,
            objects,
            start,
            stage.SelectionFillDragFrontObjectsFor(scene),
            objectIndex => DrawObject(stage, objectIndex, SceneRenderPass.Fill),
            objectIndex => DrawObject(stage, objectIndex, SceneRenderPass.Stroke));
    }

    private void DrawObjectOutline(StageControl stage, int objectIndex)
    {
        var scene = stage.Scene;
        var layer = (uint)objectIndex < scene.ObjectLayer.Length ? scene.ObjectLayer[objectIndex] : -1;
        DrawObjectOutline(stage, objectIndex, scene.GetEffectiveLayerOutlineColor(layer));
    }

    private void DrawObjectOutline(StageControl stage, int objectIndex, GdiColor layerColor)
    {
        var scene = stage.Scene;
        if (stage.IsObjectHiddenForRendering(scene, objectIndex)) return;
        var color = OutlineColor(scene, objectIndex, layerColor);
        if (color.A == 0) return;

        var brush = BrushFor(color.ToArgb());
        var shape = scene.ShapeKind.Length > objectIndex ? scene.ShapeKind[objectIndex] : ShapeKind.Rectangle;
        if (scene.TryGetObjectDistortions(objectIndex, out _))
        {
            using var distortedOutline = CreatePolygonGeometry(
                stage,
                scene.GetDistortedObjectBoundaryContours(objectIndex));
            if (distortedOutline is not null)
            {
                _target!.DrawGeometry(distortedOutline, brush, 1f, RoundStrokeStyle());
            }
            return;
        }

        if (shape == ShapeKind.Line)
        {
            DrawBezierLine(stage, objectIndex, brush, 1f);
            return;
        }

        if (shape == ShapeKind.Freeform && scene.TryGetFreehandLocalPoints(objectIndex, out var centerline))
        {
            DrawFreehandOutline(stage, objectIndex, centerline, brush);
            return;
        }

        if (shape == ShapeKind.BrushStroke && scene.TryGetFreehandWorldPoints(objectIndex, out var brushCenterline))
        {
            using var brushOutline = CreatePolygonGeometry(
                stage,
                FreehandStrokeProcessor.CreateBrushOutlines(brushCenterline, scene.Stroke[objectIndex]));
            if (brushOutline is not null) _target!.DrawGeometry(brushOutline, brush, 1f, RoundStrokeStyle());
            return;
        }

        if (shape == ShapeKind.MixingStroke)
        {
            DrawMixingStrokeOutline(stage, objectIndex, brush);
            return;
        }

        if (shape == ShapeKind.Text && scene.TryGetTextWorldContours(objectIndex, out var textContours))
        {
            using var textOutline = CreatePolygonGeometry(stage, textContours);
            if (textOutline is not null) _target!.DrawGeometry(textOutline, brush, 1f, RoundStrokeStyle());
            return;
        }

        if (shape == ShapeKind.ImportedSvg)
        {
            var screen = stage.WorldToScreen(scene.X[objectIndex], scene.Y[objectIndex]);
            var width = Math.Max(0.75f, stage.WorldLengthToScreen(scene.Width[objectIndex]));
            var height = Math.Max(0.75f, stage.WorldLengthToScreen(scene.Height[objectIndex]));
            var bounds = Rect(screen.X - width * 0.5f, screen.Y - height * 0.5f, width, height);
            var old = _target!.Transform;
            try
            {
                _target.Transform = Matrix3x2.CreateRotation(scene.Angle[objectIndex], new Vector2(screen.X, screen.Y));
                _target.DrawRectangle(in bounds, brush, 1f);
            }
            finally
            {
                _target.Transform = old;
            }
            return;
        }

        if (shape == ShapeKind.Path && ObjectPathGeometry(scene, objectIndex) is { } path)
        {
            var old = _target!.Transform;
            try
            {
                _target.Transform = ObjectLocalToScreenTransform(stage, scene, objectIndex);
                _target.DrawGeometry(
                    path,
                    brush,
                    stage.ScreenLengthToWorld(1),
                    RoundStrokeStyle());
            }
            finally
            {
                _target.Transform = old;
            }
            return;
        }

        using var outline = CreateObjectBoundaryGeometry(stage, scene, objectIndex);
        if (outline is not null) _target!.DrawGeometry(outline, brush, 1f, RoundStrokeStyle());
    }

    private void DrawFreehandOutline(
        StageControl stage,
        int objectIndex,
        GdiPointF[] localPoints,
        ID2D1SolidColorBrush brush)
    {
        if (localPoints.Length == 0) return;
        if (localPoints.Length == 1)
        {
            var point = LocalFreehandPointToScreen(stage, objectIndex, localPoints[0]);
            _target!.DrawEllipse(new Ellipse(point, 0.5f, 0.5f), brush, 1f);
            return;
        }

        var scene = stage.Scene;
        var geometry = FreehandGeometry(scene, objectIndex, localPoints);
        var screen = stage.WorldToScreen(scene.X[objectIndex], scene.Y[objectIndex]);
        var scale = VectorUnits.PixelsPerUnit * stage.Zoom;
        var old = _target!.Transform;
        try
        {
            _target.Transform = Matrix3x2.CreateScale(scale)
                * Matrix3x2.CreateRotation(scene.Angle[objectIndex])
                * Matrix3x2.CreateTranslation(screen.X, screen.Y);
            _target.DrawGeometry(geometry, brush, 1f / Math.Max(0.0001f, scale), RoundStrokeStyle());
        }
        finally
        {
            _target.Transform = old;
        }
    }

    private void DrawMixingStrokeOutline(
        StageControl stage,
        int objectIndex,
        ID2D1SolidColorBrush brush)
    {
        foreach (var contour in stage.GetMixingStrokeScreenContours(stage.Scene, objectIndex))
        {
            if (contour.Length == 1)
            {
                _target!.DrawEllipse(new Ellipse(ToVector(contour[0]), 0.5f, 0.5f), brush, 1f);
            }
            else
            {
                for (var index = 1; index < contour.Length; index++)
                {
                    _target!.DrawLine(
                        ToVector(contour[index - 1]),
                        ToVector(contour[index]),
                        brush,
                        1f,
                        RoundStrokeStyle());
                }
            }
        }
    }

    private ID2D1PathGeometry? CreatePolygonGeometry(StageControl stage, IReadOnlyList<GdiPointF[]> contours)
    {
        var path = _factory!.CreatePathGeometry();
        var hasContours = false;
        using (var sink = path.Open())
        {
            sink.SetFillMode(FillMode.Alternate);
            hasContours = AppendPolygonFigures(sink, stage, contours);
            sink.Close();
        }
        if (hasContours) return path;
        path.Dispose();
        return null;
    }

    private static GdiColor OutlineColor(VectorScene scene, int objectIndex, GdiColor layerColor)
    {
        if (layerColor.IsEmpty) return GdiColor.Empty;
        var shape = scene.ShapeKind.Length > objectIndex ? scene.ShapeKind[objectIndex] : ShapeKind.Rectangle;
        var fillAlpha = SceneRenderOrder.HasFill(shape) && (uint)objectIndex < scene.Argb.Length
            ? GdiColor.FromArgb(scene.Argb[objectIndex]).A
            : 0;
        var strokeAlpha = SceneRenderOrder.HasStroke(shape, scene.Stroke[objectIndex])
            && (uint)objectIndex < scene.StrokeArgb.Length
                ? GdiColor.FromArgb(scene.StrokeArgb[objectIndex]).A
                : 0;
        var materialAlpha = Math.Max(fillAlpha, strokeAlpha);
        var alpha = (layerColor.A * materialAlpha + 127) / 255;
        return GdiColor.FromArgb(alpha, layerColor.R, layerColor.G, layerColor.B);
    }

    private ID2D1PathGeometry? CreateMaskGeometry(StageControl stage, VectorScene scene, IReadOnlyList<int> maskObjects)
    {
        var path = _factory!.CreatePathGeometry();
        var hasContours = false;
        using (var sink = path.Open())
        {
            sink.SetFillMode(FillMode.Alternate);
            foreach (var objectIndex in maskObjects)
            {
                if (!SceneRenderOrder.HasFill(scene.ShapeKind[objectIndex])) continue;
                hasContours |= scene.TryGetObjectDistortions(objectIndex, out _)
                    ? AppendPolygonFigures(sink, stage, scene.GetDistortedObjectBoundaryContours(objectIndex))
                    : AppendObjectBoundaryFigures(sink, stage, scene, objectIndex);
            }

            sink.Close();
        }

        if (hasContours) return path;
        path.Dispose();
        return null;
    }

    private void DrawObject(StageControl stage, int i, SceneRenderPass pass)
    {
        DrawWithSceneCompositionMaskClips(
            stage,
            i,
            () => DrawObjectUnclipped(stage, i, pass),
            reference3D: false);
    }

    private void DrawObjectUnclipped(StageControl stage, int i, SceneRenderPass pass)
    {
        var scene = stage.Scene;
        if (stage.IsObjectHiddenForRendering(scene, i)) return;
        var screen = stage.WorldToScreen(scene.X[i], scene.Y[i]);
        var w = Math.Max(0.75f, stage.WorldLengthToScreen(scene.Width[i]));
        var h = Math.Max(0.75f, stage.WorldLengthToScreen(scene.Height[i]));
        var shape = scene.ShapeKind.Length > i ? scene.ShapeKind[i] : ShapeKind.Rectangle;

        if (shape == ShapeKind.ImportedSvg)
        {
            if (pass == SceneRenderPass.Fill
                && scene.TryGetImportedSvgSource(i, out var source)
                && !string.IsNullOrWhiteSpace(source))
            {
                DrawImportedSvg(
                    source,
                    screen,
                    w,
                    h,
                    scene.Angle[i],
                    GdiColor.FromArgb(scene.Argb[i]).A / 255f);
            }
            return;
        }

        if (shape == ShapeKind.MixingStroke)
        {
            if (pass == SceneRenderPass.Fill) DrawMixingStroke(stage, i);
            return;
        }

        var shapeVertexCount = scene.GetShapeVertexCount(i);
        var brush = BrushFor(scene.Argb[i]);
        var strokeBrush = BrushFor(scene.StrokeArgb.Length > i ? scene.StrokeArgb[i] : GdiColor.FromArgb(238, 242, 241).ToArgb());
        var screenStroke = Math.Max(0.1f, stage.WorldLengthToScreen(scene.Stroke[i]));

        if (shape == ShapeKind.Text)
        {
            if (pass == SceneRenderPass.Fill && scene.TryGetTextWorldContours(i, out var textContours))
            {
                DrawPathObject(stage, textContours, brush, strokeBrush, 0, 0, SceneRenderPass.Fill);
            }
            return;
        }

        if (pass == SceneRenderPass.Fill && shape != ShapeKind.Line && scene.HasGradient(i))
        {
            DrawGradientFill(stage, scene, i);
            return;
        }

        if (shape == ShapeKind.Path)
        {
            var path = ObjectPathGeometry(scene, i);
            if (path is not null)
            {
                DrawCachedPathObject(stage, scene, i, path, brush, strokeBrush, pass);
            }
            return;
        }

        if (IsFreehandShape(shape) && scene.TryGetFreehandLocalPoints(i, out var freehandPoints))
        {
            var freehandBrush = shape == ShapeKind.BrushStroke ? brush : strokeBrush;
            DrawFreehandStroke(stage, i, freehandPoints, freehandBrush);
            return;
        }

        if (shape == ShapeKind.Line)
        {
            if (scene.Stroke[i] <= 0) return;
            if (pass == SceneRenderPass.Stroke && scene.HasGradient(i)) DrawGradientBezierLine(stage, scene, i, screenStroke);
            else if (pass == SceneRenderPass.Stroke) DrawBezierLine(stage, i, strokeBrush, screenStroke);
            return;
        }

        var old = _target!.Transform;
        try
        {
            _target.Transform = Matrix3x2.CreateRotation(scene.Angle[i], new Vector2(screen.X, screen.Y));
            DrawLocalShape(shape, shapeVertexCount, brush, strokeBrush, scene.Stroke[i], screenStroke, screen.X, screen.Y, w, h, pass);
        }
        finally
        {
            _target.Transform = old;
        }
    }

    private void DrawImportedSvg(
        string source,
        GdiPointF screenCenter,
        float screenWidth,
        float screenHeight,
        float angleRadians,
        float opacity)
    {
        var raster = ImportedSvgRasterizer.Rasterize(source, screenWidth, screenHeight);
        var bitmap = ImportedSvgBitmap(raster);
        var destination = Rect(
            screenCenter.X - screenWidth * 0.5f,
            screenCenter.Y - screenHeight * 0.5f,
            screenWidth,
            screenHeight);
        var sourceRectangle = Rect(0, 0, raster.PixelWidth, raster.PixelHeight);
        var old = _target!.Transform;
        try
        {
            _target.Transform = Matrix3x2.CreateRotation(
                angleRadians,
                new Vector2(screenCenter.X, screenCenter.Y));
            _target.DrawBitmap(
                bitmap,
                destination,
                Math.Clamp(opacity, 0f, 1f),
                BitmapInterpolationMode.Linear,
                sourceRectangle);
        }
        finally
        {
            _target.Transform = old;
        }
    }

    private ID2D1Bitmap ImportedSvgBitmap(ImportedSvgRaster raster)
    {
        if (_target is null) throw new InvalidOperationException("Direct2D render target is not ready.");
        if (_importedSvgBitmapCache.TryGetValue(raster.Key, out var cached)) return cached.Bitmap;
        var pixelBytes = raster.Pixels.LongLength;
        if (_importedSvgBitmapCache.Count >= MaxImportedSvgBitmapCacheEntries
            || _importedSvgBitmapCacheBytes > MaxImportedSvgBitmapCacheBytes - pixelBytes)
        {
            ClearImportedSvgBitmapCache();
        }

        var pixelsHandle = GCHandle.Alloc(raster.Pixels, GCHandleType.Pinned);
        ID2D1Bitmap bitmap;
        try
        {
            var properties = new BitmapProperties(
                new PixelFormat(Format.B8G8R8A8_UNorm, DCommonAlphaMode.Premultiplied),
                96,
                96);
            bitmap = _target.CreateBitmap(
                new SizeI(raster.PixelWidth, raster.PixelHeight),
                pixelsHandle.AddrOfPinnedObject(),
                (uint)raster.Stride,
                properties);
        }
        finally
        {
            pixelsHandle.Free();
        }

        _importedSvgBitmapCache[raster.Key] = new CachedImportedSvgBitmap(pixelBytes, bitmap);
        _importedSvgBitmapCacheBytes += pixelBytes;
        return bitmap;
    }

    private void DrawPathObject(StageControl stage, GdiPointF[][] worldContours, ID2D1SolidColorBrush brush, ID2D1SolidColorBrush strokeBrush, float stroke, float screenStroke, SceneRenderPass pass)
    {
        if (worldContours.Length == 0) return;
        using var path = _factory!.CreatePathGeometry();
        var hasContours = false;
        using (var sink = path.Open())
        {
            sink.SetFillMode(FillMode.Alternate);
            hasContours = AppendPolygonFigures(sink, stage, worldContours);
            sink.Close();
        }

        if (!hasContours) return;
        DrawPathObject(path, brush, strokeBrush, stroke, screenStroke, pass);
    }

    private void DrawPathObject(
        ID2D1PathGeometry path,
        ID2D1SolidColorBrush brush,
        ID2D1SolidColorBrush strokeBrush,
        float stroke,
        float screenStroke,
        SceneRenderPass pass)
    {
        if (pass == SceneRenderPass.Fill) FillAntialiasedGeometry(path, brush);
        else if (stroke > 0) _target!.DrawGeometry(path, strokeBrush, screenStroke);
    }

    private ID2D1PathGeometry? CreateObjectBoundaryGeometry(
        StageControl? stage,
        VectorScene scene,
        int objectIndex)
    {
        var path = _factory!.CreatePathGeometry();
        var hasContours = false;
        using (var sink = path.Open())
        {
            sink.SetFillMode(FillMode.Alternate);
            hasContours = AppendObjectBoundaryFigures(sink, stage, scene, objectIndex);
            sink.Close();
        }

        if (hasContours) return path;
        path.Dispose();
        return null;
    }

    private ID2D1PathGeometry? CreateObjectLocalBoundaryGeometry(
        VectorScene scene,
        int objectIndex)
    {
        var worldContours = scene.GetObjectBoundaryContours(objectIndex);
        if (worldContours.Length == 0) return null;

        var localContours = new GdiPointF[worldContours.Length][];
        for (var contourIndex = 0; contourIndex < worldContours.Length; contourIndex++)
        {
            var worldContour = worldContours[contourIndex];
            var localContour = new GdiPointF[worldContour.Length];
            for (var pointIndex = 0; pointIndex < worldContour.Length; pointIndex++)
            {
                var local = ObjectWorldToLocal(scene, objectIndex, worldContour[pointIndex]);
                localContour[pointIndex] = new GdiPointF(local.X, local.Y);
            }
            localContours[contourIndex] = localContour;
        }

        var path = _factory!.CreatePathGeometry();
        var hasContours = false;
        using (var sink = path.Open())
        {
            sink.SetFillMode(FillMode.Alternate);
            hasContours = AppendPolygonFigures(sink, stage: null, localContours);
            sink.Close();
        }

        if (hasContours) return path;
        path.Dispose();
        return null;
    }

    private static bool AppendObjectBoundaryFigures(
        ID2D1GeometrySink sink,
        StageControl? stage,
        VectorScene scene,
        int objectIndex)
    {
        if (scene.TryGetPathBezierWorldContours(objectIndex, out var bezierContours))
        {
            return AppendBezierFigures(sink, stage, bezierContours);
        }

        return AppendPolygonFigures(sink, stage, scene.GetObjectBoundaryContours(objectIndex));
    }

    private static bool AppendBezierFigures(
        ID2D1GeometrySink sink,
        StageControl? stage,
        IReadOnlyList<PathBezierNode[]> contours)
    {
        var hasContours = false;
        foreach (var contour in contours)
        {
            if (contour.Length < 3) continue;
            sink.BeginFigure(GeometryPoint(stage, contour[0].Anchor), FigureBegin.Filled);
            for (var nodeIndex = 0; nodeIndex < contour.Length; nodeIndex++)
            {
                var current = contour[nodeIndex];
                var next = contour[(nodeIndex + 1) % contour.Length];
                var control1 = GeometryPoint(stage, current.OutgoingControl);
                var control2 = GeometryPoint(stage, next.IncomingControl);
                var end = GeometryPoint(stage, next.Anchor);
                sink.AddBezier(new BezierSegment(in control1, in control2, in end));
            }
            sink.EndFigure(FigureEnd.Closed);
            hasContours = true;
        }

        return hasContours;
    }

    private static bool AppendPolygonFigures(
        ID2D1GeometrySink sink,
        StageControl? stage,
        IReadOnlyList<GdiPointF[]> contours)
    {
        var hasContours = false;
        foreach (var contour in contours)
        {
            if (contour.Length < 3) continue;
            sink.BeginFigure(GeometryPoint(stage, contour[0]), FigureBegin.Filled);
            for (var pointIndex = 1; pointIndex < contour.Length; pointIndex++)
            {
                sink.AddLine(GeometryPoint(stage, contour[pointIndex]));
            }
            sink.EndFigure(FigureEnd.Closed);
            hasContours = true;
        }

        return hasContours;
    }

    private static Vector2 GeometryPoint(StageControl? stage, GdiPointF point)
    {
        return stage is null ? new Vector2(point.X, point.Y) : WorldToVector(stage, point);
    }

    private void DrawGradientFill(StageControl stage, VectorScene scene, int objectIndex)
    {
        var gradientStops = scene.GetGradientStops(objectIndex);
        var localPath = scene.ShapeKind[objectIndex] == ShapeKind.Path
            ? ObjectPathGeometry(scene, objectIndex)
            : null;
        var localMask = localPath ?? ShapeGradientMaskGeometry(scene, objectIndex);
        if (scene.GetGradientKind(objectIndex) == GradientKind.ShapeRadial
            && localMask is { } shapeMask
            && DrawShapeRadialGradientFill(
                stage,
                scene,
                objectIndex,
                shapeMask,
                gradientStops,
                maskIsObjectLocal: true))
        {
            return;
        }

        if (localMask is not null
            && scene.GetGradientKind(objectIndex) == GradientKind.Linear
            && DrawPathGradientFill(
                stage,
                scene,
                objectIndex,
                localMask,
                gradientStops,
                maskIsObjectLocal: true))
        {
            return;
        }

        if (localMask is not null)
        {
            DrawCachedLocalGradientFill(stage, scene, objectIndex, localMask, gradientStops);
            return;
        }

        using var path = CreateObjectBoundaryGeometry(stage, scene, objectIndex);
        if (path is null) return;

        if (scene.GetGradientKind(objectIndex) == GradientKind.Linear
            && DrawPathGradientFill(stage, scene, objectIndex, path, gradientStops))
        {
            return;
        }

        var start = WorldToVector(stage, scene.GetGradientStart(objectIndex));
        var end = WorldToVector(stage, scene.GetGradientEnd(objectIndex));
        if (Vector2.DistanceSquared(start, end) < 0.25f)
        {
            FillAntialiasedGeometry(path, BrushFor(scene.GradientEndArgb[objectIndex]));
            return;
        }

        var cached = GradientBrush(scene, objectIndex, scene.GetGradientKind(objectIndex), gradientStops);
        cached.SetAxis(start, end);
        FillAntialiasedGeometry(path, cached.Brush);
    }

    private void DrawCachedLocalGradientFill(
        StageControl stage,
        VectorScene scene,
        int objectIndex,
        ID2D1PathGeometry path,
        IReadOnlyList<GradientStop> stops)
    {
        var start = ObjectWorldToLocal(scene, objectIndex, scene.GetGradientStart(objectIndex));
        var end = ObjectWorldToLocal(scene, objectIndex, scene.GetGradientEnd(objectIndex));
        var old = _target!.Transform;
        try
        {
            _target.Transform = ObjectLocalToScreenTransform(stage, scene, objectIndex);
            if (Vector2.DistanceSquared(start, end) < 0.0001f)
            {
                FillAntialiasedGeometry(
                    path,
                    BrushFor(scene.GradientEndArgb[objectIndex]),
                    stage.ScreenLengthToWorld(FillEdgeCoverageWidthPixels));
                return;
            }

            var cached = GradientBrush(scene, objectIndex, scene.GetGradientKind(objectIndex), stops);
            cached.SetAxis(start, end);
            FillAntialiasedGeometry(
                path,
                cached.Brush,
                stage.ScreenLengthToWorld(FillEdgeCoverageWidthPixels));
        }
        finally
        {
            _target.Transform = old;
        }
    }

    private bool DrawShapeRadialGradientFill(
        StageControl stage,
        VectorScene scene,
        int objectIndex,
        ID2D1Geometry mask,
        IReadOnlyList<GradientStop> stops,
        bool maskIsObjectLocal)
    {
        var cached = ShapeGradientBitmap(stage, scene, objectIndex, stops);
        if (cached is null) return false;

        var topLeft = WorldToVector(stage, new GdiPointF(cached.WorldBounds.Left, cached.WorldBounds.Top));
        var bottomRight = WorldToVector(stage, new GdiPointF(cached.WorldBounds.Right, cached.WorldBounds.Bottom));
        var destination = Rect(topLeft.X, topLeft.Y, bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y);
        var source = Rect(0, 0, cached.PixelWidth, cached.PixelHeight);
        var parameters = new LayerParameters
        {
            ContentBounds = Rect(0, 0, _targetSize.Width, _targetSize.Height),
            GeometricMask = mask,
            MaskAntialiasMode = AntialiasMode.PerPrimitive,
            MaskTransform = maskIsObjectLocal
                ? ObjectLocalToScreenTransform(stage, scene, objectIndex)
                : WorldToScreenTransform(stage),
            Opacity = 1f,
            OpacityBrush = null,
            LayerOptions = LayerOptions.None
        };
        var edgeBrush = BrushFor(stops.Count > 0 ? stops[^1].Argb : scene.GradientEndArgb[objectIndex]);
        if (maskIsObjectLocal) ReinforceObjectLocalGeometryEdge(stage, scene, objectIndex, mask, edgeBrush);
        else ReinforceWorldGeometryEdge(stage, mask, edgeBrush);
        _target!.PushLayer(parameters, ShapeGradientMaskLayer());
        try
        {
            _target.DrawBitmap(cached.Bitmap, destination, 1f, BitmapInterpolationMode.Linear, source);
        }
        finally
        {
            _target.PopLayer();
        }

        return true;
    }

    private void ReinforceObjectLocalGeometryEdge(
        StageControl stage,
        VectorScene scene,
        int objectIndex,
        ID2D1Geometry geometry,
        ID2D1Brush brush)
    {
        var transform = _target!.Transform;
        try
        {
            _target.Transform = ObjectLocalToScreenTransform(stage, scene, objectIndex);
            _target.DrawGeometry(
                geometry,
                brush,
                stage.ScreenLengthToWorld(FillEdgeCoverageWidthPixels));
        }
        finally
        {
            _target.Transform = transform;
        }
    }

    private void FillAntialiasedGeometry(
        ID2D1Geometry geometry,
        ID2D1Brush brush,
        float edgeCoverageWidth = FillEdgeCoverageWidthPixels)
    {
        // Reinforce less than one screen pixel with the fill material before the
        // regular coverage pass. This avoids a dark fringe on pale fills over a
        // dark Stage without hardening the silhouette or changing the interior.
        var antialiasMode = _target!.AntialiasMode;
        try
        {
            if (antialiasMode != AntialiasMode.PerPrimitive)
            {
                _target.AntialiasMode = AntialiasMode.PerPrimitive;
            }
            _target.DrawGeometry(geometry, brush, edgeCoverageWidth);
            _target.FillGeometry(geometry, brush);
        }
        finally
        {
            _target.AntialiasMode = antialiasMode;
        }
    }

    private void ReinforceWorldGeometryEdge(StageControl stage, ID2D1Geometry geometry, ID2D1Brush brush)
    {
        var transform = _target!.Transform;
        try
        {
            _target.Transform = WorldToScreenTransform(stage);
            _target.DrawGeometry(geometry, brush, stage.ScreenLengthToWorld(FillEdgeCoverageWidthPixels));
        }
        finally
        {
            _target.Transform = transform;
        }
    }

    private bool DrawPathGradientFill(
        StageControl stage,
        VectorScene scene,
        int objectIndex,
        ID2D1Geometry mask,
        IReadOnlyList<GradientStop> stops,
        bool maskIsObjectLocal = false)
    {
        var estimatedWidth = scene.EstimateGradientPathStrokeWidth(objectIndex);
        var hasPath = maskIsObjectLocal
            ? scene.TryGetGradientPathLocalPoints(objectIndex, out var path)
            : scene.TryGetGradientPathWorldPoints(objectIndex, out path);
        if (!hasPath
            || path.Length < 2
            || estimatedWidth is not > 0)
        {
            return false;
        }

        var screenLength = 0f;
        for (var index = 1; index < path.Length; index++)
        {
            screenLength += stage.WorldLengthToScreen(Vector2.Distance(
                new Vector2(path[index - 1].X, path[index - 1].Y),
                new Vector2(path[index].X, path[index].Y)));
        }

        var segments = GradientPaintUtilities.CreatePathGradientSegments(
            path,
            stops,
            Math.Clamp((int)MathF.Ceiling(screenLength / 4f), 8, 256));
        if (segments.Length == 0) return false;

        var width = maskIsObjectLocal
            ? Math.Max(VectorUnits.MinimumStrokeUnits, estimatedWidth * 1.12f)
            : Math.Max(1f, stage.WorldLengthToScreen(estimatedWidth * 1.12f));
        using var layer = _target!.CreateLayer();
        var parameters = new LayerParameters
        {
            ContentBounds = Rect(0, 0, _targetSize.Width, _targetSize.Height),
            GeometricMask = mask,
            MaskAntialiasMode = AntialiasMode.PerPrimitive,
            MaskTransform = maskIsObjectLocal
                ? ObjectLocalToScreenTransform(stage, scene, objectIndex)
                : Matrix3x2.Identity,
            Opacity = 1f,
            OpacityBrush = null,
            LayerOptions = LayerOptions.None
        };
        var old = _target.Transform;
        if (maskIsObjectLocal)
        {
            _target.Transform = ObjectLocalToScreenTransform(stage, scene, objectIndex);
        }
        _target.PushLayer(parameters, layer);
        try
        {
            var cachedBrushes = PathGradientBrushes(scene, objectIndex, stops, segments);
            for (var index = 0; index < segments.Length; index++)
            {
                var segment = segments[index];
                var start = maskIsObjectLocal
                    ? new Vector2(segment.Start.X, segment.Start.Y)
                    : WorldToVector(stage, segment.Start);
                var end = maskIsObjectLocal
                    ? new Vector2(segment.End.X, segment.End.Y)
                    : WorldToVector(stage, segment.End);
                var direction = end - start;
                var segmentLength = direction.Length();
                var positionSpan = segment.EndPosition - segment.StartPosition;
                if (segmentLength <= 0.1f || positionSpan <= 0.0001f) continue;

                direction /= segmentLength;
                var gradientAxisLength = segmentLength / positionSpan;
                var gradientAxisStart = start - direction * (gradientAxisLength * segment.StartPosition);
                // Direct2D batches draw calls. Each segment owns its brush so a
                // later axis update cannot alter a previously submitted segment.
                var brush = cachedBrushes.Brushes[index];
                brush.StartPoint = gradientAxisStart;
                brush.EndPoint = gradientAxisStart + direction * gradientAxisLength;
                _target.DrawLine(start, end, brush, width, RoundStrokeStyle());
            }
        }
        finally
        {
            _target.PopLayer();
            _target.Transform = old;
        }

        var edgeStart = maskIsObjectLocal
            ? ObjectWorldToLocal(scene, objectIndex, scene.GetGradientStart(objectIndex))
            : WorldToVector(stage, scene.GetGradientStart(objectIndex));
        var edgeEnd = maskIsObjectLocal
            ? ObjectWorldToLocal(scene, objectIndex, scene.GetGradientEnd(objectIndex))
            : WorldToVector(stage, scene.GetGradientEnd(objectIndex));
        if (Vector2.DistanceSquared(edgeStart, edgeEnd) >= 0.25f)
        {
            var edgeBrush = GradientBrush(scene, objectIndex, GradientKind.Linear, stops);
            edgeBrush.SetAxis(edgeStart, edgeEnd);
            if (maskIsObjectLocal)
            {
                _target.Transform = ObjectLocalToScreenTransform(stage, scene, objectIndex);
                _target.DrawGeometry(
                    mask,
                    edgeBrush.Brush,
                    stage.ScreenLengthToWorld(FillEdgeCoverageWidthPixels));
                _target.Transform = old;
            }
            else
            {
                _target.DrawGeometry(mask, edgeBrush.Brush, FillEdgeCoverageWidthPixels);
            }
        }

        return true;
    }

    private void DrawFreehandStroke(StageControl stage, int objectIndex, GdiPointF[] localPoints, ID2D1SolidColorBrush brush)
    {
        if (localPoints.Length == 0) return;
        var scene = stage.Scene;
        var screenWidth = Math.Max(0.75f, stage.WorldLengthToScreen(scene.Stroke[objectIndex]));
        if (localPoints.Length == 1)
        {
            var point = LocalFreehandPointToScreen(stage, objectIndex, localPoints[0]);
            var dot = new Ellipse(point, screenWidth * 0.5f, screenWidth * 0.5f);
            _target!.FillEllipse(dot, brush);
            return;
        }

        var geometry = FreehandGeometry(scene, objectIndex, localPoints);
        var screen = stage.WorldToScreen(scene.X[objectIndex], scene.Y[objectIndex]);
        var scale = VectorUnits.PixelsPerUnit * stage.Zoom;
        var old = _target!.Transform;
        try
        {
            _target.Transform = Matrix3x2.CreateScale(scale)
                * Matrix3x2.CreateRotation(scene.Angle[objectIndex])
                * Matrix3x2.CreateTranslation(screen.X, screen.Y);
            _target.DrawGeometry(geometry, brush, scene.Stroke[objectIndex], RoundStrokeStyle());
        }
        finally
        {
            _target.Transform = old;
        }
    }

    private void DrawMixingStroke(StageControl stage, int objectIndex)
    {
        var scene = stage.Scene;
        if (_target is null) return;
        var raster = stage.MixingBrushRasterFor(scene, objectIndex);
        if (raster is null) return;
        var bitmap = MixingBrushBitmap(scene, objectIndex, raster);
        var bounds = raster.LocalBounds;
        var destination = Rect(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        var source = Rect(0, 0, raster.PixelWidth, raster.PixelHeight);
        var old = _target.Transform;
        try
        {
            _target.Transform = ObjectLocalToScreenTransform(stage, scene, objectIndex);
            _target.DrawBitmap(bitmap, destination, 1f, BitmapInterpolationMode.Linear, source);
        }
        finally
        {
            _target.Transform = old;
        }
    }

    private ID2D1Bitmap MixingBrushBitmap(
        VectorScene scene,
        int objectIndex,
        MixingBrushRegionRaster raster)
    {
        var key = (scene, objectIndex);
        if (_mixingBrushBitmapCache.TryGetValue(key, out var cached))
        {
            if (ReferenceEquals(cached.Raster, raster)) return cached.Bitmap;
            _mixingBrushBitmapCache.Remove(key);
            cached.Dispose();
        }

        if (_mixingBrushBitmapCache.Count >= MaxMixingBrushBitmapCacheEntries)
        {
            ClearMixingBrushBitmapCache();
        }

        var properties = new BitmapProperties(
            new PixelFormat(Format.B8G8R8A8_UNorm, DCommonAlphaMode.Premultiplied),
            96,
            96);
        var bitmap = _target!.CreateBitmap(
            new SizeI(raster.PixelWidth, raster.PixelHeight),
            raster.PixelData,
            (uint)raster.Stride,
            properties);
        _mixingBrushBitmapCache[key] = new CachedMixingBrushBitmap(raster, bitmap);
        return bitmap;
    }

    private void PruneMixingBrushBitmapCache(
        VectorScene editableScene,
        VectorScene? underlayScene,
        VectorScene? onionSkinScene,
        VectorScene? dragPreviewScene)
    {
        List<(VectorScene Scene, int ObjectIndex)>? staleKeys = null;
        foreach (var key in _mixingBrushBitmapCache.Keys)
        {
            var activeScene = ReferenceEquals(key.Scene, editableScene)
                || ReferenceEquals(key.Scene, underlayScene)
                || ReferenceEquals(key.Scene, onionSkinScene)
                || ReferenceEquals(key.Scene, dragPreviewScene);
            if (activeScene
                && (uint)key.ObjectIndex < key.Scene.ObjectCount
                && key.Scene.ShapeKind[key.ObjectIndex] == ShapeKind.MixingStroke)
            {
                continue;
            }

            (staleKeys ??= []).Add(key);
        }

        if (staleKeys is null) return;
        foreach (var key in staleKeys)
        {
            if (_mixingBrushBitmapCache.Remove(key, out var cached)) cached.Dispose();
        }
    }

    private void ClearMixingBrushBitmapCache()
    {
        foreach (var cached in _mixingBrushBitmapCache.Values) cached.Dispose();
        _mixingBrushBitmapCache.Clear();
    }

    private void DrawLocalShape(
        ShapeKind shape,
        int shapeVertexCount,
        ID2D1SolidColorBrush brush,
        ID2D1SolidColorBrush strokeBrush,
        float stroke,
        float screenStroke,
        float cx,
        float cy,
        float w,
        float h,
        SceneRenderPass pass)
    {
        var rect = Rect(cx - w * 0.5f, cy - h * 0.5f, w, h);
        switch (shape)
        {
            case ShapeKind.Ellipse:
                var center = new Vector2(cx, cy);
                var ellipse = new Ellipse(in center, w * 0.5f, h * 0.5f);
                if (pass == SceneRenderPass.Fill)
                {
                    _target!.DrawEllipse(ellipse, brush, FillEdgeCoverageWidthPixels);
                    _target.FillEllipse(ellipse, brush);
                }
                else if (stroke > 0 && w > 4 && h > 4) _target!.DrawEllipse(ellipse, strokeBrush, screenStroke);
                break;
            case ShapeKind.Triangle:
                DrawPolygon(RegularPolygonPoints(3, cx, cy, w, h, -MathF.PI / 2), brush, strokeBrush, stroke, screenStroke, pass);
                break;
            case ShapeKind.Polygon:
                DrawPolygon(RegularPolygonPoints(PolygonVertexCount(shapeVertexCount), cx, cy, w, h, -MathF.PI / 2), brush, strokeBrush, stroke, screenStroke, pass);
                break;
            case ShapeKind.Star:
                DrawPolygon(StarPoints(StarVertexCount(shapeVertexCount), cx, cy, w, h, -MathF.PI / 2), brush, strokeBrush, stroke, screenStroke, pass);
                break;
            default:
                if (pass == SceneRenderPass.Fill)
                {
                    _target!.DrawRectangle(in rect, brush, FillEdgeCoverageWidthPixels);
                    _target.FillRectangle(in rect, brush);
                }
                else if (stroke > 0 && w > 4 && h > 4) _target!.DrawRectangle(in rect, strokeBrush, screenStroke);
                break;
        }
    }

    private void DrawPolygon(Vector2[] points, ID2D1SolidColorBrush fill, ID2D1SolidColorBrush strokeBrush, float stroke, float screenStroke, SceneRenderPass pass)
    {
        using var path = _factory!.CreatePathGeometry();
        using (var sink = path.Open())
        {
            sink.BeginFigure(points[0], FigureBegin.Filled);
            for (var i = 1; i < points.Length; i++) sink.AddLine(points[i]);
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }

        if (pass == SceneRenderPass.Fill) FillAntialiasedGeometry(path, fill);
        else if (stroke > 0) _target!.DrawGeometry(path, strokeBrush, screenStroke);
    }

    private void DrawGrid(StageControl stage)
    {
        if (stage.PlanarWorldGridOpacity <= 0.001f) return;
        if (stage.WorldGridType == WorldGridType.GoldenSpiral)
        {
            DrawGoldenSpiralGrid(stage);
            return;
        }
        if (stage.WorldGridType == WorldGridType.Polar)
        {
            DrawPolarGrid(stage);
            return;
        }
        var scale = stage.CurrentWorldGridScale;
        var bounds = stage.VisibleWorldBounds();
        var origin = stage.WorldToScreen(0, 0);
        DrawWorldGridLines(stage, scale, bounds.Left, bounds.Right, vertical: true);
        DrawWorldGridLines(stage, scale, bounds.Top, bounds.Bottom, vertical: false);

        if (origin.Y >= 0 && origin.Y <= stage.Height)
        {
            var xAxis = BrushFor(GdiColor.FromArgb(GridAlpha(stage.PlanarWorldGridOpacity, 205), 214, 82, 82).ToArgb());
            _target!.DrawLine(new Vector2(0, origin.Y), new Vector2(stage.Width, origin.Y), xAxis, 1.6f);
        }
        if (origin.X >= 0 && origin.X <= stage.Width)
        {
            var yAxis = BrushFor(GdiColor.FromArgb(GridAlpha(stage.PlanarWorldGridOpacity, 205), 82, 190, 122).ToArgb());
            _target!.DrawLine(new Vector2(origin.X, 0), new Vector2(origin.X, stage.Height), yAxis, 1.6f);
        }
        if (origin.X >= 0 && origin.X <= stage.Width && origin.Y >= 0 && origin.Y <= stage.Height)
        {
            var center = new Vector2(origin.X, origin.Y);
            _target!.FillEllipse(
                new Ellipse(center, 3, 3),
                BrushFor(GdiColor.FromArgb(GridAlpha(stage.PlanarWorldGridOpacity, 230), 224, 232, 234).ToArgb()));
            _target.DrawEllipse(
                new Ellipse(center, 4.5f, 4.5f),
                BrushFor(GdiColor.FromArgb(GridAlpha(stage.PlanarWorldGridOpacity, 235), 22, 26, 29).ToArgb()),
                1.2f);
        }
    }

    private void DrawGoldenSpiralGrid(StageControl stage)
    {
        var geometry = stage.ResolveGoldenSpiralGrid();
        if (geometry.SpiralPoints.Length < 2) return;

        var guide = BrushFor(GdiColor.FromArgb(GridAlpha(stage.PlanarWorldGridOpacity, 82), 118, 128, 134).ToArgb());
        foreach (var segment in geometry.GuideSegments)
        {
            var start = stage.WorldToScreen(segment.Start.X, segment.Start.Y);
            var end = stage.WorldToScreen(segment.End.X, segment.End.Y);
            _target!.DrawLine(
                new Vector2(start.X, start.Y),
                new Vector2(end.X, end.Y),
                guide,
                1f);
        }

        using var path = _factory!.CreatePathGeometry();
        using (var sink = path.Open())
        {
            var firstWorld = geometry.SpiralPoints[0];
            var first = stage.WorldToScreen(firstWorld.X, firstWorld.Y);
            sink.BeginFigure(new Vector2(first.X, first.Y), FigureBegin.Hollow);
            for (var index = 1; index < geometry.SpiralPoints.Length; index++)
            {
                var world = geometry.SpiralPoints[index];
                var point = stage.WorldToScreen(world.X, world.Y);
                sink.AddLine(new Vector2(point.X, point.Y));
            }
            sink.EndFigure(FigureEnd.Open);
            sink.Close();
        }

        var spiral = BrushFor(GdiColor.FromArgb(GridAlpha(stage.PlanarWorldGridOpacity, 205), 232, 194, 86).ToArgb());
        _target!.DrawGeometry(path, spiral, 1.6f, RoundStrokeStyle());
    }

    private void DrawPolarGrid(StageControl stage)
    {
        var geometry = stage.ResolvePolarGrid();
        if (geometry.Circles.Length == 0) return;

        var originPoint = stage.WorldToScreen(geometry.Origin.X, geometry.Origin.Y);
        var origin = new Vector2(originPoint.X, originPoint.Y);
        var scale = stage.CurrentWorldGridScale;
        var visibleBounds = stage.VisibleWorldBounds();
        var arcOverscan = stage.ScreenLengthToWorld(2f);
        visibleBounds.Inflate(arcOverscan, arcOverscan);
        var maximumFastEllipseRadius = Math.Max(stage.Width, stage.Height) * 2f;
        Span<PolarGridArc> visibleArcs = stackalloc PolarGridArc[PolarGridLayout.MaximumVisibleArcsPerCircle];
        foreach (var circle in geometry.Circles)
        {
            var style = WorldGridLayout.ResolveLineStyle(circle.Index, scale, stage.PlanarWorldGridOpacity);
            if (style.Color.A == 0) continue;
            var radius = stage.WorldLengthToScreen(circle.Radius);
            var brush = BrushFor(style.Color.ToArgb());
            if (radius <= maximumFastEllipseRadius)
            {
                _target!.DrawEllipse(new Ellipse(origin, radius, radius), brush, style.Width);
                continue;
            }

            var arcCount = PolarGridLayout.ResolveVisibleArcs(visibleBounds, circle.Radius, visibleArcs);
            if (arcCount == 0) continue;
            using var path = _factory!.CreatePathGeometry();
            using (var sink = path.Open())
            {
                for (var arcIndex = 0; arcIndex < arcCount; arcIndex++)
                {
                    var arc = visibleArcs[arcIndex];
                    var segmentCount = PolarGridLayout.ResolveArcSegmentCount(radius, arc.SweepAngle);
                    sink.BeginFigure(PolarArcScreenPoint(origin, radius, arc.StartAngle), FigureBegin.Hollow);
                    for (var segmentIndex = 1; segmentIndex <= segmentCount; segmentIndex++)
                    {
                        var angle = arc.StartAngle + arc.SweepAngle * segmentIndex / segmentCount;
                        sink.AddLine(PolarArcScreenPoint(origin, radius, angle));
                    }
                    sink.EndFigure(FigureEnd.Open);
                }
                sink.Close();
            }
            _target!.DrawGeometry(path, brush, style.Width);
        }

        for (var index = 0; index < geometry.DiameterSegments.Length; index++)
        {
            if (!PolarGridLayout.TryClipSegment(visibleBounds, geometry.DiameterSegments[index], out var segment)) continue;
            var color = index switch
            {
                0 => GdiColor.FromArgb(GridAlpha(stage.PlanarWorldGridOpacity, 205), 214, 82, 82),
                PolarGridLayout.DiameterCount / 2 => GdiColor.FromArgb(GridAlpha(stage.PlanarWorldGridOpacity, 205), 82, 190, 122),
                _ => GdiColor.FromArgb(GridAlpha(stage.PlanarWorldGridOpacity, index % 3 == 0 ? 112 : 72), 92, 104, 112)
            };
            var width = index is 0 or PolarGridLayout.DiameterCount / 2
                ? 1.6f
                : index % 3 == 0 ? 1.15f : 0.8f;
            var start = stage.WorldToScreen(segment.Start.X, segment.Start.Y);
            var end = stage.WorldToScreen(segment.End.X, segment.End.Y);
            _target!.DrawLine(
                new Vector2(start.X, start.Y),
                new Vector2(end.X, end.Y),
                BrushFor(color.ToArgb()),
                width);
        }

        if (origin.X >= 0 && origin.X <= stage.Width && origin.Y >= 0 && origin.Y <= stage.Height)
        {
            _target!.FillEllipse(
                new Ellipse(origin, 3, 3),
                BrushFor(GdiColor.FromArgb(GridAlpha(stage.PlanarWorldGridOpacity, 230), 224, 232, 234).ToArgb()));
            _target.DrawEllipse(
                new Ellipse(origin, 4.5f, 4.5f),
                BrushFor(GdiColor.FromArgb(GridAlpha(stage.PlanarWorldGridOpacity, 235), 22, 26, 29).ToArgb()),
                1.2f);
        }
    }

    private static Vector2 PolarArcScreenPoint(Vector2 origin, float radius, float angle)
    {
        return new Vector2(
            (float)(origin.X + Math.Cos(angle) * radius),
            (float)(origin.Y + Math.Sin(angle) * radius));
    }

    private void DrawWorldGridLines(
        StageControl stage,
        WorldGridScale scale,
        float minimumWorld,
        float maximumWorld,
        bool vertical)
    {
        var (first, last) = WorldGridLayout.VisibleIndexRange(minimumWorld, maximumWorld, scale.StepWorld);
        for (var index = first; index <= last; index++)
        {
            if (index == 0) continue;
            var style = WorldGridLayout.ResolveLineStyle(index, scale, stage.PlanarWorldGridOpacity);
            if (style.Color.A == 0) continue;
            var world = index * scale.StepWorld;
            var position = vertical ? stage.WorldToScreen((float)world, 0).X : stage.WorldToScreen(0, (float)world).Y;
            if (vertical && (position < -style.Width || position > stage.Width + style.Width)
                || !vertical && (position < -style.Width || position > stage.Height + style.Width))
            {
                continue;
            }

            var brush = BrushFor(style.Color.ToArgb());
            if (vertical)
            {
                _target!.DrawLine(new Vector2(position, 0), new Vector2(position, stage.Height), brush, style.Width);
            }
            else
            {
                _target!.DrawLine(new Vector2(0, position), new Vector2(stage.Width, position), brush, style.Width);
            }
        }
    }

    private void Draw3DReferenceGrid(StageControl stage)
    {
        if (stage.ReferenceWorldGridOpacity <= 0.001f) return;
        var horizon = stage.Height * 0.42f;
        _target!.DrawLine(new Vector2(0, horizon), new Vector2(stage.Width, horizon), BrushFor(GdiColor.FromArgb(GridAlpha(stage.ReferenceWorldGridOpacity, 40), 112, 204, 255).ToArgb()), 1);

        var step = InfiniteGridStep(stage);
        var lineRadius = InfiniteGridLineRadius(stage);
        var centerX = SnapToGrid(stage.ReferenceTargetX, step);
        var centerY = SnapToGrid(stage.ReferenceTargetY, step);
        var minX = centerX - lineRadius * step;
        var maxX = centerX + lineRadius * step;
        var minY = centerY - lineRadius * step;
        var maxY = centerY + lineRadius * step;
        var grid = BrushFor(GdiColor.FromArgb(GridAlpha(stage.ReferenceWorldGridOpacity, 76), 72, 84, 92).ToArgb());
        var center = BrushFor(GdiColor.FromArgb(GridAlpha(stage.ReferenceWorldGridOpacity, 130), 150, 164, 174).ToArgb());
        var xAxis = BrushFor(GdiColor.FromArgb(GridAlpha(stage.ReferenceWorldGridOpacity, 220), 255, 92, 92).ToArgb());
        var yAxis = BrushFor(GdiColor.FromArgb(GridAlpha(stage.ReferenceWorldGridOpacity, 220), 122, 224, 92).ToArgb());
        var zAxis = BrushFor(GdiColor.FromArgb(GridAlpha(stage.ReferenceWorldGridOpacity, 220), 92, 172, 255).ToArgb());

        for (var offset = -lineRadius; offset <= lineRadius; offset++)
        {
            var y = centerY + offset * step;
            var x = centerX + offset * step;
            DrawProjectedLine(stage, new Vector3(minX, y, 0), new Vector3(maxX, y, 0), Math.Abs(y) < 0.001f ? center : grid, Math.Abs(y) < 0.001f ? 1.4f : 1f);
            DrawProjectedLine(stage, new Vector3(x, minY, 0), new Vector3(x, maxY, 0), Math.Abs(x) < 0.001f ? center : grid, Math.Abs(x) < 0.001f ? 1.4f : 1f);
        }

        DrawProjectedLine(stage, new Vector3(minX, 0, 0), new Vector3(maxX, 0, 0), xAxis, 2);
        DrawProjectedLine(stage, new Vector3(0, minY, 0), new Vector3(0, maxY, 0), yAxis, 2);
        DrawProjectedLine(stage, new Vector3(0, 0, -5000), new Vector3(0, 0, 5000), zAxis, 2);
    }

    private static int InfiniteGridLineRadius(StageControl stage)
    {
        return Math.Clamp((int)MathF.Ceiling(Math.Max(stage.Width, stage.Height) / 14f), 64, 180);
    }

    private static float InfiniteGridStep(StageControl stage)
    {
        var raw = stage.ReferenceDistance / Math.Max(0.35f, stage.ReferenceZoomScale) / 10f;
        if (raw <= 750) return 500;
        if (raw <= 1500) return 1000;
        if (raw <= 3500) return 2000;
        if (raw <= 7000) return 5000;
        return 10000;
    }

    private static float SnapToGrid(float value, float step) => MathF.Round(value / step) * step;

    private static int GridAlpha(float opacity, int alpha)
    {
        return (int)MathF.Round(Math.Clamp(alpha * opacity, 0f, 255f));
    }

    private void DrawProjectedLine(StageControl stage, Vector3 a, Vector3 b, ID2D1SolidColorBrush brush, float width)
    {
        var ca = CameraSpacePoint(stage, a);
        var cb = CameraSpacePoint(stage, b);
        if (!ClipNear(ref ca, ref cb)) return;
        var pa = ProjectCameraPoint(stage, ca);
        var pb = ProjectCameraPoint(stage, cb);
        if (!LineMayTouchViewport(stage, pa, pb)) return;
        _target!.DrawLine(pa, pb, brush, width);
    }

    private static Vector3 CameraSpacePoint(StageControl stage, Vector3 point)
    {
        var yawCos = MathF.Cos(stage.EffectiveReferenceYaw);
        var yawSin = MathF.Sin(stage.EffectiveReferenceYaw);
        var pitchCos = MathF.Cos(stage.EffectiveReferencePitch);
        var pitchSin = MathF.Sin(stage.EffectiveReferencePitch);

        var worldX = point.X - stage.ReferenceTargetX;
        var worldY = point.Y - stage.ReferenceTargetY;
        var worldZ = point.Z - stage.ReferenceTargetZ;
        var x = worldX * yawCos - worldZ * yawSin;
        var z = worldX * yawSin + worldZ * yawCos;
        var y = worldY;
        var py = y * pitchCos - z * pitchSin;
        var pz = y * pitchSin + z * pitchCos + stage.ReferenceDistance;
        return new Vector3(x, py, pz);
    }

    private static bool ClipNear(ref Vector3 a, ref Vector3 b)
    {
        const float near = 120f;
        var aInside = a.Z >= near;
        var bInside = b.Z >= near;
        if (!aInside && !bInside) return false;
        if (aInside && bInside) return true;
        var t = (near - a.Z) / (b.Z - a.Z);
        var clipped = Vector3.Lerp(a, b, Math.Clamp(t, 0, 1));
        if (aInside) b = clipped;
        else a = clipped;
        return true;
    }

    private static Vector2 ProjectCameraPoint(StageControl stage, Vector3 point)
    {
        var scale = 0.035f * stage.ReferenceZoomScale;
        var perspective = stage.ReferencePerspectiveScale(point.Z);
        return new Vector2(
            stage.Width * 0.5f + point.X * scale * perspective,
            stage.Height * 0.58f - point.Y * scale * perspective);
    }

    private static bool LineMayTouchViewport(StageControl stage, Vector2 a, Vector2 b)
    {
        const float margin = 320;
        var minX = MathF.Min(a.X, b.X);
        var maxX = MathF.Max(a.X, b.X);
        var minY = MathF.Min(a.Y, b.Y);
        var maxY = MathF.Max(a.Y, b.Y);
        return maxX >= -margin && minX <= stage.Width + margin && maxY >= -margin && minY <= stage.Height + margin;
    }

    private void DrawFillEdgeBezierOverlay(StageControl stage)
    {
        if (!stage.FillEdgeBezierOverlayVisible) return;
        var geometry = FillEdgeBezierOverlayGeometry(stage);
        if (geometry is null) return;

        var lime = BrushFor(GdiColor.Lime.ToArgb());
        var core = BrushFor(stage.BackColor.ToArgb());
        var translation = stage.FillEdgeBezierOverlayTranslation;
        var old = _target!.Transform;
        try
        {
            var screenScale = VectorUnits.PixelsPerUnit * stage.Zoom;
            _target.Transform = Matrix3x2.CreateTranslation(
                translation.X * screenScale,
                translation.Y * screenScale);
            _target.DrawGeometry(geometry, lime, 1.5f);
        }
        finally
        {
            _target.Transform = old;
        }

        foreach (var source in stage.FillEdgeBezierOverlaySegments)
        {
            var segment = stage.TranslatedFillEdgeBezierOverlaySegment(source);
            if (segment.PartIndex == stage.FillEdgeBezierOverlayActivePartIndex || !Finite(segment)) continue;
            DrawFillEdgeBezierSegmentHandles(stage, segment, core, lime);
        }

        if (stage.FillEdgeBezierOverlayActivePartIndex < 0)
        {
            return;
        }

        foreach (var source in stage.FillEdgeBezierOverlaySegments)
        {
            if (source.PartIndex != stage.FillEdgeBezierOverlayActivePartIndex) continue;
            var active = stage.TranslatedFillEdgeBezierOverlaySegment(source);
            if (Finite(active)) DrawFillEdgeBezierSegmentHandles(stage, active, core, lime);
        }
    }

    private void DrawFillEdgeBezierSegmentHandles(
        StageControl stage,
        FillEdgeBezierOverlaySegment segment,
        ID2D1SolidColorBrush core,
        ID2D1SolidColorBrush edge)
    {
        var start = WorldToVector(stage, segment.Start);
        var control1 = WorldToVector(stage, segment.Control1);
        var control2 = WorldToVector(stage, segment.Control2);
        var end = WorldToVector(stage, segment.End);
        _target!.DrawLine(start, control1, edge, 1f);
        _target.DrawLine(end, control2, edge, 1f);
        DrawFillEdgeBezierAnchor(start, core, edge);
        DrawFillEdgeBezierAnchor(end, core, edge);
        DrawFillEdgeBezierControl(control1, core, edge);
        DrawFillEdgeBezierControl(control2, core, edge);
    }

    private ID2D1PathGeometry? FillEdgeBezierOverlayGeometry(StageControl stage)
    {
        if (_fillEdgeBezierOverlayGeometry is not null
            && _fillEdgeBezierOverlayGeometryRevision == stage.FillEdgeBezierOverlayRevision
            && _fillEdgeBezierOverlayGeometryCameraX == stage.CameraX
            && _fillEdgeBezierOverlayGeometryCameraY == stage.CameraY
            && _fillEdgeBezierOverlayGeometryZoom == stage.Zoom
            && _fillEdgeBezierOverlayGeometryWidth == stage.Width
            && _fillEdgeBezierOverlayGeometryHeight == stage.Height)
        {
            return _fillEdgeBezierOverlayGeometry;
        }

        ClearFillEdgeBezierOverlayGeometry();
        var geometry = _factory!.CreatePathGeometry();
        var hasSegments = false;
        using (var sink = geometry.Open())
        {
            foreach (var segment in stage.FillEdgeBezierOverlaySegments)
            {
                if (!Finite(segment)) continue;
                var start = WorldToVector(stage, segment.Start);
                var control1 = WorldToVector(stage, segment.Control1);
                var control2 = WorldToVector(stage, segment.Control2);
                var end = WorldToVector(stage, segment.End);
                sink.BeginFigure(start, FigureBegin.Hollow);
                sink.AddBezier(new BezierSegment(in control1, in control2, in end));
                sink.EndFigure(FigureEnd.Open);
                hasSegments = true;
            }
            sink.Close();
        }

        if (!hasSegments)
        {
            geometry.Dispose();
            return null;
        }

        _fillEdgeBezierOverlayGeometry = geometry;
        _fillEdgeBezierOverlayGeometryRevision = stage.FillEdgeBezierOverlayRevision;
        _fillEdgeBezierOverlayGeometryCameraX = stage.CameraX;
        _fillEdgeBezierOverlayGeometryCameraY = stage.CameraY;
        _fillEdgeBezierOverlayGeometryZoom = stage.Zoom;
        _fillEdgeBezierOverlayGeometryWidth = stage.Width;
        _fillEdgeBezierOverlayGeometryHeight = stage.Height;
        LastFillEdgeBezierOverlayGeometryBuilds++;
        return geometry;
    }

    private void DrawFillEdgeBezierAnchor(Vector2 point, ID2D1SolidColorBrush core, ID2D1SolidColorBrush edge)
    {
        var bounds = Rect(point.X - 3.5f, point.Y - 3.5f, 7f, 7f);
        _target!.FillRectangle(in bounds, core);
        _target.DrawRectangle(in bounds, edge, 1.5f);
    }

    private void DrawFillEdgeBezierControl(Vector2 point, ID2D1SolidColorBrush core, ID2D1SolidColorBrush edge)
    {
        var ellipse = new Ellipse(point, 4f, 4f);
        _target!.FillEllipse(ellipse, core);
        _target.DrawEllipse(ellipse, edge, 1.5f);
    }

    private static bool Finite(FillEdgeBezierOverlaySegment segment)
    {
        return Finite(segment.Start)
            && Finite(segment.Control1)
            && Finite(segment.Control2)
            && Finite(segment.End);
    }

    private static bool Finite(GdiPointF point) => float.IsFinite(point.X) && float.IsFinite(point.Y);

    private void DrawPenAnchorGuides(StageControl stage)
    {
        if (!stage.PenAnchorGuidesVisible) return;
        var point = WorldToVector(stage, stage.PenAnchorGuidePoint);
        var guide = BrushFor(GdiColor.FromArgb(205, 70, 210, 235).ToArgb());
        var marker = BrushFor(GdiColor.FromArgb(245, 128, 239, 255).ToArgb());
        var markerFill = BrushFor(GdiColor.FromArgb(210, 18, 48, 55).ToArgb());
        if (stage.PenAnchorGuideVertical)
        {
            _target!.DrawLine(new Vector2(point.X, 0), new Vector2(point.X, stage.Height), guide, 1);
        }
        if (stage.PenAnchorGuideHorizontal)
        {
            _target!.DrawLine(new Vector2(0, point.Y), new Vector2(stage.Width, point.Y), guide, 1);
        }

        if (stage.PenAnchorGuideInsertion)
        {
            var top = new Vector2(point.X, point.Y - 7);
            var right = new Vector2(point.X + 7, point.Y);
            var bottom = new Vector2(point.X, point.Y + 7);
            var left = new Vector2(point.X - 7, point.Y);
            _target!.DrawLine(top, right, marker, 1.5f);
            _target.DrawLine(right, bottom, marker, 1.5f);
            _target.DrawLine(bottom, left, marker, 1.5f);
            _target.DrawLine(left, top, marker, 1.5f);
            _target.DrawLine(new Vector2(point.X - 3, point.Y), new Vector2(point.X + 3, point.Y), marker, 1.5f);
            _target.DrawLine(new Vector2(point.X, point.Y - 3), new Vector2(point.X, point.Y + 3), marker, 1.5f);
        }
        else if (stage.PenAnchorGuideSnapped)
        {
            var ellipse = new Ellipse(point, 6, 6);
            _target!.FillEllipse(ellipse, markerFill);
            _target.DrawEllipse(ellipse, marker, 1.5f);
            _target.DrawLine(new Vector2(point.X - 8, point.Y), new Vector2(point.X + 8, point.Y), marker, 1.5f);
            _target.DrawLine(new Vector2(point.X, point.Y - 8), new Vector2(point.X, point.Y + 8), marker, 1.5f);
        }
        else
        {
            var ellipse = new Ellipse(point, 3, 3);
            _target!.FillEllipse(ellipse, markerFill);
            _target.DrawEllipse(ellipse, marker, 1.5f);
        }
    }

    private void DrawDrawingPreview(StageControl stage)
    {
        if (!stage.DrawingPreviewVisible) return;
        var start = stage.DrawingPreviewStart;
        var end = stage.DrawingPreviewEnd;
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        if (Math.Abs(dx) + Math.Abs(dy) < 0.001f) return;

        var fill = BrushFor(GdiColor.FromArgb(72, stage.DrawingPreviewColor).ToArgb());
        var stroke = BrushFor(GdiColor.FromArgb(230, stage.DrawingPreviewColor).ToArgb());
        if (stage.DrawingPreviewShape == ShapeKind.Line)
        {
            var a = WorldToVector(stage, start);
            var b = WorldToVector(stage, end);
            if (stage.DrawingPreviewHasCurve)
            {
                var segments = stage.DrawingPreviewCurveSegments.Count > 0
                    ? stage.DrawingPreviewCurveSegments
                    : [new CubicDrawingPreviewSegment(start, stage.DrawingPreviewControl, stage.DrawingPreviewControl2, end)];
                foreach (var segment in segments)
                {
                    var segmentStart = WorldToVector(stage, segment.Start);
                    var segmentControl1 = WorldToVector(stage, segment.Control1);
                    var segmentControl2 = WorldToVector(stage, segment.Control2);
                    var segmentEnd = WorldToVector(stage, segment.End);
                    using var path = BuildBezierPath(segmentStart, segmentControl1, segmentControl2, segmentEnd);
                    _target!.DrawGeometry(path, stroke, Math.Max(0.1f, stage.WorldLengthToScreen(stage.DrawingPreviewStroke)));
                }
                var control1 = WorldToVector(stage, stage.DrawingPreviewControl);
                var control2 = WorldToVector(stage, stage.DrawingPreviewControl2);
                if (segments.Count == 1)
                {
                    var guide = BrushFor(GdiColor.FromArgb(170, 255, 255, 255).ToArgb());
                    _target!.DrawLine(a, control1, guide, 1);
                    _target.DrawLine(b, control2, guide, 1);
                }
                DrawHandle(a, BrushFor(GdiColor.FromArgb(255, 255, 240, 168).ToArgb()), 7);
                DrawHandle(b, BrushFor(GdiColor.FromArgb(255, 255, 240, 168).ToArgb()), 7);
                if (segments.Count == 1)
                {
                    DrawHandle(control1, BrushFor(GdiColor.FromArgb(255, 112, 204, 255).ToArgb()), 9);
                    DrawHandle(control2, BrushFor(GdiColor.FromArgb(255, 112, 204, 255).ToArgb()), 9);
                }
                return;
            }

            _target!.DrawLine(a, b, stroke, Math.Max(0.1f, stage.WorldLengthToScreen(stage.DrawingPreviewStroke)));
            DrawHandle(a, BrushFor(GdiColor.FromArgb(255, 255, 240, 168).ToArgb()), 7);
            DrawHandle(b, BrushFor(GdiColor.FromArgb(255, 255, 240, 168).ToArgb()), 7);
            return;
        }

        var center = stage.WorldToScreen((start.X + end.X) * 0.5f, (start.Y + end.Y) * 0.5f);
        var w = Math.Max(2, stage.WorldLengthToScreen(Math.Abs(dx)));
        var h = Math.Max(2, stage.WorldLengthToScreen(Math.Abs(dy)));
        var screenStroke = Math.Max(0.1f, stage.WorldLengthToScreen(stage.DrawingPreviewStroke));
        DrawLocalShape(
            stage.DrawingPreviewShape,
            stage.DrawingPreviewShapeVertexCount,
            fill,
            stroke,
            0,
            0,
            center.X,
            center.Y,
            w,
            h,
            SceneRenderPass.Fill);
        DrawLocalShape(
            stage.DrawingPreviewShape,
            stage.DrawingPreviewShapeVertexCount,
            fill,
            stroke,
            Math.Max(float.Epsilon, stage.DrawingPreviewStroke),
            screenStroke,
            center.X,
            center.Y,
            w,
            h,
            SceneRenderPass.Stroke);
        var rect = new Vortice.RawRectF(
            center.X - w * 0.5f,
            center.Y - h * 0.5f,
            center.X + w * 0.5f,
            center.Y + h * 0.5f);
        var bounds = BrushFor(GdiColor.FromArgb(180, 255, 255, 255).ToArgb());
        _target!.DrawRectangle(rect, bounds, 1, PreviewBoundsStrokeStyle());
    }

    private void DrawPenDirectionHandles(StageControl stage)
    {
        if (!stage.PenDirectionHandlesVisible) return;
        var anchor = WorldToVector(stage, stage.PenDirectionAnchor);
        var guide = BrushFor(GdiColor.FromArgb(220, 112, 204, 255).ToArgb());
        var fill = BrushFor(GdiColor.FromArgb(245, 18, 48, 55).ToArgb());
        var border = BrushFor(GdiColor.FromArgb(250, 146, 224, 255).ToArgb());
        DrawDirectionPoint(stage.PenDirectionIncoming);
        DrawDirectionPoint(stage.PenDirectionOutgoing);
        var anchorBounds = Rect(anchor.X - 3.5f, anchor.Y - 3.5f, 7, 7);
        _target!.FillRectangle(in anchorBounds, fill);
        _target.DrawRectangle(in anchorBounds, border, 1.4f);

        void DrawDirectionPoint(PointF? world)
        {
            if (world is not { } point) return;
            var screen = WorldToVector(stage, point);
            _target!.DrawLine(anchor, screen, guide, 1);
            var ellipse = new Ellipse(screen, 3.5f, 3.5f);
            _target.FillEllipse(ellipse, fill);
            _target.DrawEllipse(ellipse, border, 1.4f);
        }
    }

    private void DrawFreehandPreview(StageControl stage)
    {
        if (!stage.FreehandPreviewVisible || stage.FreehandPreviewPoints.Count == 0) return;
        var brush = BrushFor(stage.FreehandPreviewColor.ToArgb());
        var width = Math.Max(0.75f, stage.WorldLengthToScreen(stage.FreehandPreviewStroke));
        var variableWidth = stage.FreehandPreviewDiameters.Count == stage.FreehandPreviewPoints.Count;
        if (stage.FreehandPreviewBrushShape is { IsTraditionalBrush: true, IsRadiallySymmetric: false } tipShape)
        {
            DrawTraditionalBrushPreview(stage, tipShape, brush, width, variableWidth);
            return;
        }

        if (stage.FreehandPreviewPoints.Count == 1)
        {
            var point = WorldToVector(stage, stage.FreehandPreviewPoints[0]);
            if (variableWidth) width = Math.Max(0.75f, stage.WorldLengthToScreen(stage.FreehandPreviewDiameters[0]));
            var dot = new Ellipse(point, width * 0.5f, width * 0.5f);
            _target!.FillEllipse(dot, brush);
            return;
        }

        if (!variableWidth)
        {
            using var path = _factory!.CreatePathGeometry();
            using (var sink = path.Open())
            {
                sink.BeginFigure(WorldToVector(stage, stage.FreehandPreviewPoints[0]), FigureBegin.Hollow);
                for (var index = 1; index < stage.FreehandPreviewPoints.Count; index++)
                {
                    sink.AddLine(WorldToVector(stage, stage.FreehandPreviewPoints[index]));
                }
                sink.EndFigure(FigureEnd.Open);
                sink.Close();
            }
            _target!.DrawGeometry(path, brush, width, RoundStrokeStyle());
            return;
        }

        var previous = WorldToVector(stage, stage.FreehandPreviewPoints[0]);
        var previousWidth = variableWidth
            ? Math.Max(0.75f, stage.WorldLengthToScreen(stage.FreehandPreviewDiameters[0]))
            : width;
        if (variableWidth) _target!.FillEllipse(new Ellipse(previous, previousWidth * 0.5f, previousWidth * 0.5f), brush);
        for (var i = 1; i < stage.FreehandPreviewPoints.Count; i++)
        {
            var current = WorldToVector(stage, stage.FreehandPreviewPoints[i]);
            var currentWidth = variableWidth
                ? Math.Max(0.75f, stage.WorldLengthToScreen(stage.FreehandPreviewDiameters[i]))
                : width;
            _target!.DrawLine(previous, current, brush, (previousWidth + currentWidth) * 0.5f, RoundStrokeStyle());
            if (variableWidth) _target!.FillEllipse(new Ellipse(current, currentWidth * 0.5f, currentWidth * 0.5f), brush);
            previous = current;
            previousWidth = currentWidth;
        }
    }

    private void DrawTraditionalBrushPreview(
        StageControl stage,
        BrushShape tipShape,
        ID2D1SolidColorBrush brush,
        float defaultWidth,
        bool variableWidth)
    {
        var contour = tipShape.NormalizedContour(0.5f);
        if (contour.Length < 3) return;

        for (var index = 0; index < stage.FreehandPreviewPoints.Count; index++)
        {
            var diameter = variableWidth
                ? Math.Max(0.75f, stage.WorldLengthToScreen(stage.FreehandPreviewDiameters[index]))
                : defaultWidth;
            var radius = diameter * 0.5f;
            var center = WorldToVector(stage, stage.FreehandPreviewPoints[index]);
            using var path = _factory!.CreatePathGeometry();
            using (var sink = path.Open())
            {
                sink.BeginFigure(new Vector2(
                    center.X + contour[0].X * radius,
                    center.Y + contour[0].Y * radius), FigureBegin.Filled);
                for (var pointIndex = 1; pointIndex < contour.Length; pointIndex++)
                {
                    sink.AddLine(new Vector2(
                        center.X + contour[pointIndex].X * radius,
                        center.Y + contour[pointIndex].Y * radius));
                }

                sink.EndFigure(FigureEnd.Closed);
                sink.Close();
            }

            _target!.FillGeometry(path, brush);
        }
    }

    private void DrawBrushTipCursor(StageControl stage)
    {
        if (!stage.BrushTipCursorVisible || stage.BrushTipCursorShape is null) return;
        var outlineColor = stage.BrushTipCursorIsEraser
            ? GdiColor.FromArgb(235, 255, 120, 120)
            : GdiColor.FromArgb(235, 112, 204, 255);
        foreach (var layer in stage.BrushTipCursorShape.Layers)
        {
            var points = stage.BrushTipCursorShape.NormalizedContour(layer.Threshold)
                .Select(point => new Vector2(
                    stage.BrushTipCursorScreen.X + point.X * stage.BrushTipCursorRadiusPixels,
                    stage.BrushTipCursorScreen.Y + point.Y * stage.BrushTipCursorRadiusPixels))
                .ToArray();
            if (points.Length < 3) continue;
            using var path = _factory!.CreatePathGeometry();
            using (var sink = path.Open())
            {
                sink.BeginFigure(points[0], FigureBegin.Filled);
                for (var index = 1; index < points.Length; index++) sink.AddLine(points[index]);
                sink.EndFigure(FigureEnd.Closed);
                sink.Close();
            }

            var fillAlpha = (int)Math.Clamp(42 * layer.Opacity / 0.66f, 8, 42);
            var strokeAlpha = (int)Math.Clamp(190 * layer.Opacity / 0.66f, 48, 190);
            _target!.FillGeometry(path, BrushFor(GdiColor.FromArgb(fillAlpha, outlineColor).ToArgb()));
            _target.DrawGeometry(path, BrushFor(GdiColor.FromArgb(strokeAlpha, outlineColor).ToArgb()), layer.Threshold >= 0.7f ? 1.4f : 1f);
        }
    }

    private void DrawBrushColorPalette(StageControl stage)
    {
        if (!stage.BrushColorPaletteVisible) return;
        var center = stage.BrushColorPaletteCenter;
        var radius = stage.BrushColorPaletteRadius + 22f;
        _target!.FillEllipse(
            new Ellipse(new Vector2(center.X, center.Y), radius, radius),
            BrushFor(GdiColor.FromArgb(214, Theme.Top).ToArgb()));
        _target.DrawEllipse(
            new Ellipse(new Vector2(center.X, center.Y), radius, radius),
            BrushFor(GdiColor.FromArgb(225, Theme.BorderHover).ToArgb()),
            1f);

        for (var index = 0; index < stage.BrushColorPaletteColors.Count; index++)
        {
            var bounds = stage.BrushColorPaletteSwatchBounds(index);
            var rect = Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
            _target.FillRectangle(in rect, BrushFor(stage.BrushColorPaletteColors[index].ToArgb()));
            var outline = index == stage.BrushColorPaletteHoveredIndex
                ? GdiColor.FromArgb(255, 255, 240, 168)
                : GdiColor.FromArgb(235, Theme.BorderHover);
            _target.DrawRectangle(in rect, BrushFor(outline.ToArgb()), index == stage.BrushColorPaletteHoveredIndex ? 2.4f : 1f);
        }

        _target.FillEllipse(new Ellipse(new Vector2(center.X, center.Y), 3, 3), BrushFor(GdiColor.FromArgb(230, Theme.Text).ToArgb()));
    }

    private void DrawFillAnimation(StageControl stage)
    {
        if (!stage.FillAnimationVisible) return;

        using var path = _factory!.CreatePathGeometry();
        using (var sink = path.Open())
        {
            sink.SetFillMode(FillMode.Alternate);
            foreach (var contour in stage.FillAnimationContours)
            {
                if (contour.Length < 3) continue;
                sink.BeginFigure(WorldToVector(stage, contour[0]), FigureBegin.Filled);
                for (var index = 1; index < contour.Length; index++) sink.AddLine(WorldToVector(stage, contour[index]));
                sink.EndFigure(FigureEnd.Closed);
            }

            sink.Close();
        }

        var origin = WorldToVector(stage, stage.FillAnimationOrigin);
        var radius = Math.Max(10f, stage.WorldLengthToScreen(stage.FillAnimationBloomRadiusWorld));
        DrawFillBloom(path, origin, radius, stage.FillAnimationGlowColor, stage.FillAnimationFade);
    }

    private void DrawFillBloom(ID2D1Geometry fillGeometry, Vector2 origin, float radius, System.Drawing.Color color, float fade)
    {
        DrawFillBloomLayer(fillGeometry, origin, radius, color, 20f * fade);
        DrawFillBloomLayer(fillGeometry, origin, radius * 0.72f, color, 28f * fade);
        DrawFillBloomLayer(fillGeometry, origin, radius * 0.38f, color, 38f * fade);

        using var bloomGeometry = IntersectWithBloom(fillGeometry, origin, radius);
        var waveWidth = Math.Clamp(radius * 0.025f, 2f, 12f);
        var waveAlpha = (int)Math.Clamp(210f * fade, 0f, 210f);
        _target!.DrawGeometry(bloomGeometry, BrushFor(GdiColor.FromArgb(waveAlpha, color).ToArgb()), waveWidth);
    }

    private void DrawFillBloomLayer(ID2D1Geometry fillGeometry, Vector2 origin, float radius, System.Drawing.Color color, float alpha)
    {
        if (radius <= 0.5f || alpha <= 0.5f) return;
        using var bloomGeometry = IntersectWithBloom(fillGeometry, origin, radius);
        var opacity = (int)Math.Clamp(alpha, 0f, 255f);
        _target!.FillGeometry(bloomGeometry, BrushFor(GdiColor.FromArgb(opacity, color).ToArgb()));
    }

    private ID2D1PathGeometry IntersectWithBloom(ID2D1Geometry fillGeometry, Vector2 origin, float radius)
    {
        using var bloom = _factory!.CreateEllipseGeometry(new Ellipse(origin, radius, radius));
        var intersection = _factory.CreatePathGeometry();
        using var sink = intersection.Open();
        fillGeometry.CombineWithGeometry(bloom, CombineMode.Intersect, sink);
        sink.Close();
        return intersection;
    }

    private void DrawFillPreview(StageControl stage)
    {
        if (!stage.FillPreviewVisible) return;

        using var path = _factory!.CreatePathGeometry();
        using (var sink = path.Open())
        {
            sink.SetFillMode(FillMode.Alternate);
            foreach (var contour in stage.FillPreviewContours)
            {
                if (contour.Length < 3) continue;
                sink.BeginFigure(WorldToVector(stage, contour[0]), FigureBegin.Filled);
                for (var index = 1; index < contour.Length; index++) sink.AddLine(WorldToVector(stage, contour[index]));
                sink.EndFigure(FigureEnd.Closed);
            }

            sink.Close();
        }

        _target!.FillGeometry(path, BrushFor(GdiColor.FromArgb(72, stage.FillPreviewColor).ToArgb()));
        _target.DrawGeometry(path, BrushFor(GdiColor.FromArgb(150, stage.FillPreviewColor).ToArgb()), 1f);
    }

    private void DrawGradientOverlay(StageControl stage)
    {
        if (!stage.GradientOverlayVisible) return;
        var start = WorldToVector(stage, stage.GradientOverlayStart);
        var end = WorldToVector(stage, stage.GradientOverlayEnd);
        var guide = BrushFor(GdiColor.FromArgb(230, 255, 244, 166).ToArgb());
        var outline = BrushFor(GdiColor.FromArgb(245, 16, 18, 22).ToArgb());
        if (stage.GradientOverlayKind == GradientKind.Radial)
        {
            var radius = Vector2.Distance(start, end);
            _target!.DrawEllipse(new Ellipse(start, radius, radius), BrushFor(GdiColor.FromArgb(190, 255, 244, 166).ToArgb()), 1.25f);
        }
        _target!.DrawLine(start, end, guide, 1.5f);
        _target.FillEllipse(new Ellipse(start, 6, 6), BrushFor(stage.GradientOverlayStartColor.ToArgb()));
        _target.DrawEllipse(new Ellipse(start, 6, 6), outline, 1.4f);
        if (stage.GradientOverlayKind == GradientKind.ShapeRadial)
        {
            using var edgeMarker = _factory!.CreatePathGeometry();
            using (var sink = edgeMarker.Open())
            {
                sink.BeginFigure(new Vector2(end.X, end.Y - 4), FigureBegin.Filled);
                sink.AddLine(new Vector2(end.X + 4, end.Y));
                sink.AddLine(new Vector2(end.X, end.Y + 4));
                sink.AddLine(new Vector2(end.X - 4, end.Y));
                sink.EndFigure(FigureEnd.Closed);
                sink.Close();
            }
            _target.FillGeometry(edgeMarker, BrushFor(stage.GradientOverlayEndColor.ToArgb()));
            _target.DrawGeometry(edgeMarker, outline, 1.4f);
        }
        else
        {
            _target.FillEllipse(new Ellipse(end, 6, 6), BrushFor(stage.GradientOverlayEndColor.ToArgb()));
            _target.DrawEllipse(new Ellipse(end, 6, 6), outline, 1.4f);
        }
        for (var index = 1; index < stage.GradientOverlayStops.Count - 1; index++)
        {
            var stop = stage.GradientOverlayStops[index];
            var point = Vector2.Lerp(start, end, stop.Position);
            var points = new[]
            {
                new Vector2(point.X, point.Y - 6),
                new Vector2(point.X + 6, point.Y),
                new Vector2(point.X, point.Y + 6),
                new Vector2(point.X - 6, point.Y)
            };
            using var path = _factory!.CreatePathGeometry();
            using (var sink = path.Open())
            {
                sink.BeginFigure(points[0], FigureBegin.Filled);
                for (var pointIndex = 1; pointIndex < points.Length; pointIndex++) sink.AddLine(points[pointIndex]);
                sink.EndFigure(FigureEnd.Closed);
                sink.Close();
            }
            _target.FillGeometry(path, BrushFor(stop.Argb));
            _target.DrawGeometry(path, outline, 1.4f);
        }
    }

    private void DrawFillToolCursor(StageControl stage)
    {
        if (!stage.FillToolCursorVisible) return;

        var x = stage.FillToolCursorScreen.X;
        var y = stage.FillToolCursorScreen.Y;
        var points = new[]
        {
            new Vector2(x - 10, y - 12),
            new Vector2(x + 3, y - 12),
            new Vector2(x + 10, y - 5),
            new Vector2(x - 3, y - 5)
        };
        using var path = _factory!.CreatePathGeometry();
        using (var sink = path.Open())
        {
            sink.BeginFigure(points[0], FigureBegin.Filled);
            for (var index = 1; index < points.Length; index++) sink.AddLine(points[index]);
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }

        var color = stage.FillToolCursorColor;
        _target!.FillGeometry(path, BrushFor(GdiColor.FromArgb(150, color).ToArgb()));
        _target.DrawGeometry(path, BrushFor(GdiColor.FromArgb(235, Theme.Text).ToArgb()), 1.4f);
        var drop = new Ellipse(new Vector2(x + 6, y + 3), 3, 4);
        _target.FillEllipse(drop, BrushFor(GdiColor.FromArgb(220, color).ToArgb()));
        _target.DrawEllipse(drop, BrushFor(GdiColor.FromArgb(235, Theme.Text).ToArgb()), 1f);
    }

    private void DrawMarquee(StageControl stage)
    {
        if (!stage.MarqueeVisible || stage.MarqueeOverlayActive) return;
        var left = Math.Min(stage.MarqueeStart.X, stage.MarqueeEnd.X);
        var top = Math.Min(stage.MarqueeStart.Y, stage.MarqueeEnd.Y);
        var right = Math.Max(stage.MarqueeStart.X, stage.MarqueeEnd.X);
        var bottom = Math.Max(stage.MarqueeStart.Y, stage.MarqueeEnd.Y);
        if (right - left < 2 || bottom - top < 2) return;

        var rect = Rect(left, top, right - left, bottom - top);
        _target!.FillRectangle(in rect, BrushFor(GdiColor.FromArgb(34, 112, 204, 255).ToArgb()));
        _target.DrawRectangle(in rect, BrushFor(GdiColor.FromArgb(230, 112, 204, 255).ToArgb()), 1);
    }

    private void DrawBezierLine(StageControl stage, int i, ID2D1SolidColorBrush brush, float screenStroke)
    {
        var startStyle = stage.Scene.GetLineEndpointStyle(i, startEndpoint: true);
        var endStyle = stage.Scene.GetLineEndpointStyle(i, startEndpoint: false);
        var startCap = LineCapForEndpoint(startStyle);
        var endCap = LineCapForEndpoint(endStyle);
        var strokeStyle = LineStrokeStyle(
            startCap,
            endCap,
            startStyle == LineEndpointStyle.Sharp || endStyle == LineEndpointStyle.Sharp);
        if (stage.Scene.IsLineStraight(i))
        {
            var points = GetBezierScreenPoints(stage, i);
            _target!.DrawLine(points.Start, points.End, brush, screenStroke, strokeStyle);
        }
        else
        {
            DrawCachedLineGeometry(
                stage,
                i,
                brush,
                stage.Scene.Stroke[i],
                strokeStyle);
        }
        DrawEndpointJoin(stage, i, startEndpoint: true, screenStroke, brush);
        DrawEndpointJoin(stage, i, startEndpoint: false, screenStroke, brush);
    }

    private void DrawGradientBezierLine(StageControl stage, VectorScene scene, int objectIndex, float screenStroke)
    {
        var stops = scene.GetGradientStops(objectIndex);
        var startStyle = scene.GetLineEndpointStyle(objectIndex, startEndpoint: true);
        var endStyle = scene.GetLineEndpointStyle(objectIndex, startEndpoint: false);
        var strokeStyle = LineStrokeStyle(
            LineCapForEndpoint(startStyle),
            LineCapForEndpoint(endStyle),
            startStyle == LineEndpointStyle.Sharp || endStyle == LineEndpointStyle.Sharp);
        var cached = GradientBrush(scene, objectIndex, scene.GetGradientKind(objectIndex), stops);
        if (scene.IsLineStraight(objectIndex))
        {
            var start = WorldToVector(stage, scene.GetGradientStart(objectIndex));
            var end = WorldToVector(stage, scene.GetGradientEnd(objectIndex));
            if (Vector2.DistanceSquared(start, end) < 0.25f)
            {
                DrawBezierLine(stage, objectIndex, BrushFor(scene.GradientEndArgb[objectIndex]), screenStroke);
                return;
            }
            cached.SetAxis(start, end);
            var points = GetBezierScreenPoints(stage, objectIndex);
            _target!.DrawLine(points.Start, points.End, cached.Brush, screenStroke, strokeStyle);
        }
        else
        {
            var start = ObjectWorldToLocal(scene, objectIndex, scene.GetGradientStart(objectIndex));
            var end = ObjectWorldToLocal(scene, objectIndex, scene.GetGradientEnd(objectIndex));
            if (Vector2.DistanceSquared(start, end) < 0.0001f)
            {
                DrawBezierLine(stage, objectIndex, BrushFor(scene.GradientEndArgb[objectIndex]), screenStroke);
                return;
            }
            cached.SetAxis(start, end);
            DrawCachedLineGeometry(
                stage,
                objectIndex,
                cached.Brush,
                scene.Stroke[objectIndex],
                strokeStyle);
        }
        DrawEndpointJoin(stage, objectIndex, startEndpoint: true, screenStroke, cached.Brush);
        DrawEndpointJoin(stage, objectIndex, startEndpoint: false, screenStroke, cached.Brush);
    }

    private static CapStyle LineCapForEndpoint(LineEndpointStyle endpointStyle)
    {
        return endpointStyle == LineEndpointStyle.Sharp ? CapStyle.Flat : CapStyle.Round;
    }

    private void DrawEndpointJoin(
        StageControl stage,
        int objectIndex,
        bool startEndpoint,
        float screenStroke,
        ID2D1Brush brush)
    {
        var scene = stage.Scene;
        if (scene.GetLineEndpointStyle(objectIndex, startEndpoint) != LineEndpointStyle.Sharp
            || !scene.TryGetLineEndpointJunctionForRender(objectIndex, startEndpoint, stage.Frame, out var junction)
            || !junction.AllSharp
            || objectIndex != junction.OwnerObjectIndex)
        {
            return;
        }

        var current = GetBezierScreenPoints(stage, objectIndex);
        var joint = startEndpoint ? current.Start : current.End;
        var interiorPoints = new GdiPointF[junction.Connections.Length + 1];
        var currentInterior = EndpointInteriorPoint(current, startEndpoint);
        interiorPoints[0] = new GdiPointF(currentInterior.X, currentInterior.Y);
        for (var index = 0; index < junction.Connections.Length; index++)
        {
            var connection = junction.Connections[index];
            var adjacent = GetBezierScreenPoints(stage, connection.ObjectIndex);
            var adjacentInterior = EndpointInteriorPoint(adjacent, connection.StartEndpoint);
            interiorPoints[index + 1] = new GdiPointF(adjacentInterior.X, adjacentInterior.Y);
        }

        var jointPoint = new GdiPointF(joint.X, joint.Y);
        var miters = LineJoinGeometry.CreateJunctionMiters(jointPoint, interiorPoints, screenStroke * 0.5f);
        if (miters.Length == 0) return;

        using var path = _factory!.CreatePathGeometry();
        using (var sink = path.Open())
        {
            sink.SetFillMode(FillMode.Winding);
            var vectorJoint = new Vector2(joint.X, joint.Y);
            foreach (var miter in miters)
            {
                sink.BeginFigure(vectorJoint, FigureBegin.Filled);
                sink.AddLine(new Vector2(miter.OuterFirstOffset.X, miter.OuterFirstOffset.Y));
                sink.AddLine(new Vector2(miter.OuterMiter.X, miter.OuterMiter.Y));
                sink.AddLine(new Vector2(miter.OuterSecondOffset.X, miter.OuterSecondOffset.Y));
                sink.EndFigure(FigureEnd.Closed);
                sink.BeginFigure(vectorJoint, FigureBegin.Filled);
                sink.AddLine(new Vector2(miter.InnerFirstOffset.X, miter.InnerFirstOffset.Y));
                sink.AddLine(new Vector2(miter.InnerMiter.X, miter.InnerMiter.Y));
                sink.AddLine(new Vector2(miter.InnerSecondOffset.X, miter.InnerSecondOffset.Y));
                sink.EndFigure(FigureEnd.Closed);
            }
            sink.Close();
        }

        _target!.FillGeometry(path, brush);
    }

    private static Vector2 EndpointInteriorPoint(
        (Vector2 Start, Vector2 Control1, Vector2 Control2, Vector2 End) curve,
        bool startEndpoint)
    {
        var endpoint = startEndpoint ? curve.Start : curve.End;
        var control = startEndpoint ? curve.Control1 : curve.Control2;
        if (Vector2.DistanceSquared(endpoint, control) > 0.01f) return control;
        return startEndpoint ? curve.End : curve.Start;
    }

    private ID2D1PathGeometry LineGeometry(VectorScene scene, int objectIndex)
    {
        var points = GetBezierLocalPoints(scene, objectIndex);
        var key = (scene, objectIndex);
        if (_lineGeometryCache.TryGetValue(key, out var cached))
        {
            if (cached.Matches(points.Start, points.Control1, points.Control2, points.End))
            {
                LastLineGeometryCacheReuses++;
                return cached.Geometry;
            }

            _lineGeometryCache.Remove(key);
            cached.Dispose();
        }

        if (_lineGeometryCache.Count >= MaxLineGeometryCacheEntries) ClearLineGeometryCache();
        var geometry = BuildBezierPath(points.Start, points.Control1, points.Control2, points.End);
        _lineGeometryCache[key] = new CachedLineGeometry(
            points.Start,
            points.Control1,
            points.Control2,
            points.End,
            geometry);
        LastLineGeometryCacheBuilds++;
        return geometry;
    }

    private void DrawCachedLineGeometry(
        StageControl stage,
        int objectIndex,
        ID2D1Brush brush,
        float worldStroke,
        ID2D1StrokeStyle? strokeStyle = null)
    {
        var old = _target!.Transform;
        try
        {
            _target.Transform = ObjectLocalToScreenTransform(stage, stage.Scene, objectIndex) * old;
            _target.DrawGeometry(
                LineGeometry(stage.Scene, objectIndex),
                brush,
                worldStroke,
                strokeStyle);
        }
        finally
        {
            _target.Transform = old;
        }
    }

    private ID2D1PathGeometry? ObjectPathGeometry(VectorScene scene, int objectIndex)
    {
        scene.TryGetPathLocalContours(objectIndex, out var pathContoursIdentity);
        GdiPointF[][]? pathIdentity = pathContoursIdentity.Length > 0 ? pathContoursIdentity : null;
        scene.TryGetPathBezierLocalContours(objectIndex, out var pathBezierContoursIdentity);
        PathBezierNode[][]? pathBezierIdentity = pathBezierContoursIdentity.Length > 0
            ? pathBezierContoursIdentity
            : null;
        var key = (scene, objectIndex);
        if (_objectPathGeometryCache.TryGetValue(key, out var cached))
        {
            if (cached.Matches(pathIdentity, pathBezierIdentity))
            {
                LastObjectPathGeometryCacheReuses++;
                return cached.Geometry;
            }

            _objectPathGeometryCache.Remove(key);
            cached.Dispose();
        }

        if (pathIdentity is null && pathBezierIdentity is null) return null;
        if (_objectPathGeometryCache.Count >= MaxObjectPathGeometryCacheEntries)
        {
            ClearObjectPathGeometryCache();
        }

        var geometry = _factory!.CreatePathGeometry();
        var hasContours = false;
        using (var sink = geometry.Open())
        {
            sink.SetFillMode(FillMode.Alternate);
            hasContours = pathBezierIdentity is not null
                ? AppendBezierFigures(sink, null, pathBezierIdentity)
                : AppendPolygonFigures(sink, null, pathIdentity!);
            sink.Close();
        }

        if (!hasContours)
        {
            geometry.Dispose();
            return null;
        }

        _objectPathGeometryCache[key] = new CachedObjectPathGeometry(
            pathIdentity,
            pathBezierIdentity,
            geometry);
        LastObjectPathGeometryCacheBuilds++;
        return geometry;
    }

    private static Matrix3x2 ObjectLocalToScreenTransform(
        StageControl stage,
        VectorScene scene,
        int objectIndex)
    {
        var screen = stage.WorldToScreen(scene.X[objectIndex], scene.Y[objectIndex]);
        var scale = VectorUnits.PixelsPerUnit * stage.Zoom;
        return Matrix3x2.CreateScale(scale)
            * Matrix3x2.CreateRotation(scene.Angle[objectIndex])
            * Matrix3x2.CreateTranslation(screen.X, screen.Y);
    }

    private static Vector2 ObjectWorldToLocal(
        VectorScene scene,
        int objectIndex,
        GdiPointF point)
    {
        var dx = point.X - scene.X[objectIndex];
        var dy = point.Y - scene.Y[objectIndex];
        var angle = scene.Angle[objectIndex];
        var cos = MathF.Cos(angle);
        var sin = MathF.Sin(angle);
        return new Vector2(
            dx * cos + dy * sin,
            -dx * sin + dy * cos);
    }

    private void DrawCachedPathObject(
        StageControl stage,
        VectorScene scene,
        int objectIndex,
        ID2D1PathGeometry path,
        ID2D1SolidColorBrush brush,
        ID2D1SolidColorBrush strokeBrush,
        SceneRenderPass pass)
    {
        var old = _target!.Transform;
        try
        {
            _target.Transform = ObjectLocalToScreenTransform(stage, scene, objectIndex);
            if (pass == SceneRenderPass.Fill)
            {
                FillAntialiasedGeometry(
                    path,
                    brush,
                    stage.ScreenLengthToWorld(FillEdgeCoverageWidthPixels));
            }
            else if (scene.Stroke[objectIndex] > 0)
            {
                _target.DrawGeometry(path, strokeBrush, scene.Stroke[objectIndex]);
            }
        }
        finally
        {
            _target.Transform = old;
        }
    }

    private ID2D1PathGeometry BuildBezierPath(Vector2 start, Vector2 control1, Vector2 control2, Vector2 end)
    {
        var path = _factory!.CreatePathGeometry();
        using (var sink = path.Open())
        {
            sink.BeginFigure(start, FigureBegin.Hollow);
            var segment = new BezierSegment(in control1, in control2, in end);
            sink.AddBezier(segment);
            sink.EndFigure(FigureEnd.Open);
            sink.Close();
        }

        return path;
    }

    private (Vector2 Start, Vector2 Control1, Vector2 Control2, Vector2 End) GetBezierScreenPoints(StageControl stage, int i)
    {
        var hit = new DrawingElementHit(
            new DrawingElementKey(i, DrawingElementKind.Stroke, 0),
            0,
            0,
            1);
        if (stage.TryGetEditableBezierWorldPoints(
                hit,
                out var start,
                out var control1,
                out var control2,
                out var end))
        {
            return (
                WorldToVector(stage, start),
                WorldToVector(stage, control1),
                WorldToVector(stage, control2),
                WorldToVector(stage, end));
        }

        return default;
    }

    private static (Vector2 Start, Vector2 Control1, Vector2 Control2, Vector2 End) GetBezierLocalPoints(
        VectorScene scene,
        int objectIndex)
    {
        if (!scene.TryGetLineCubic(
                objectIndex,
                out var start,
                out var control1,
                out var control2,
                out var end))
        {
            return default;
        }

        return (
            ObjectWorldToLocal(scene, objectIndex, start),
            ObjectWorldToLocal(scene, objectIndex, control1),
            ObjectWorldToLocal(scene, objectIndex, control2),
            ObjectWorldToLocal(scene, objectIndex, end));
    }

    private void DrawOpenPolygon(Vector2[] points, ID2D1SolidColorBrush brush, float width)
    {
        for (var i = 0; i < points.Length; i++) _target!.DrawLine(points[i], points[(i + 1) % points.Length], brush, width);
    }

    private void DrawPolyline(Vector2[] points, ID2D1SolidColorBrush brush, float width)
    {
        for (var i = 0; i < points.Length - 1; i++) _target!.DrawLine(points[i], points[i + 1], brush, width);
    }

    private void DrawHandle(Vector2 point, ID2D1SolidColorBrush brush, float size)
    {
        var rect = Rect(point.X - size * 0.5f, point.Y - size * 0.5f, size, size);
        _target!.FillRectangle(in rect, brush);
        _target.DrawRectangle(in rect, BrushFor(GdiColor.FromArgb(255, 16, 18, 22).ToArgb()), 1);
    }

    private void FillRectangle(float x, float y, float width, float height, int argb)
    {
        var rect = Rect(x, y, width, height);
        _target!.FillRectangle(in rect, BrushFor(argb));
    }

    private ID2D1SolidColorBrush BrushFor(int argb)
    {
        if (_target is null) throw new InvalidOperationException("Direct2D render target is not ready.");
        if (_brushCache.TryGetValue(argb, out var brush)) return brush;
        if (_brushCache.Count >= MaxDirect2DBrushCacheEntries) ClearBrushCache();

        var color = ToD2D(GdiColor.FromArgb(argb));
        brush = _target.CreateSolidColorBrush(in color, null);
        _brushCache[argb] = brush;
        return brush;
    }

    private ID2D1SolidColorBrush MixingBrushFor(int argb)
    {
        if (_target is null) throw new InvalidOperationException("Direct2D render target is not ready.");
        var color = ToD2D(GdiColor.FromArgb(argb));
        if (_mixingBrush is null)
        {
            _mixingBrush = _target.CreateSolidColorBrush(in color, null);
        }
        else
        {
            _mixingBrush.Color = color;
        }

        return _mixingBrush;
    }


    private static D2DRect Rect(float x, float y, float width, float height) => new(x, y, width, height);

    private static D2DColor ToD2D(GdiColor color) => new(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);

    private static Color4 ToColor4(GdiColor color) => new(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);

    private static Vector2 ToVector(GdiPointF point) => new(point.X, point.Y);

    private static Vector2 WorldToVector(StageControl stage, GdiPointF point) => ToVector(stage.WorldToScreen(point.X, point.Y));

    private static Vector2 LocalFreehandPointToScreen(StageControl stage, int objectIndex, GdiPointF local)
    {
        var scene = stage.Scene;
        var angle = scene.Angle[objectIndex];
        var cos = MathF.Cos(angle);
        var sin = MathF.Sin(angle);
        var world = new GdiPointF(
            scene.X[objectIndex] + local.X * cos - local.Y * sin,
            scene.Y[objectIndex] + local.X * sin + local.Y * cos);
        return WorldToVector(stage, world);
    }

    private static bool IsFiniteMixingPoint(GdiPointF point) =>
        float.IsFinite(point.X) && float.IsFinite(point.Y);

    private static bool IsFreehandShape(ShapeKind shape)
    {
        return shape is ShapeKind.Freeform or ShapeKind.BrushStroke;
    }

    private static Vector2[] GetBoundaryVectors(StageControl stage, int i)
    {
        var boundary = stage.Scene.GetShapeBoundary(i);
        var points = new Vector2[boundary.Length];
        for (var p = 0; p < boundary.Length; p++) points[p] = WorldToVector(stage, boundary[p]);
        return points;
    }

    private static Vector2[] RegularPolygonPoints(int sides, float cx, float cy, float w, float h, float startAngle)
    {
        var points = new Vector2[sides];
        for (var i = 0; i < sides; i++)
        {
            var angle = startAngle + i * MathF.Tau / sides;
            points[i] = new Vector2(cx + MathF.Cos(angle) * w * 0.5f, cy + MathF.Sin(angle) * h * 0.5f);
        }

        return points;
    }

    private static Vector2[] StarPoints(int points, float cx, float cy, float w, float h, float startAngle)
    {
        var result = new Vector2[points * 2];
        for (var i = 0; i < result.Length; i++)
        {
            var radius = i % 2 == 0 ? 0.5f : 0.23f;
            var angle = startAngle + i * MathF.Tau / result.Length;
            result[i] = new Vector2(cx + MathF.Cos(angle) * w * radius, cy + MathF.Sin(angle) * h * radius);
        }

        return result;
    }

    private static int PolygonVertexCount(int value) => Math.Clamp(value == 0 ? 6 : value, 3, 64);

    private static int StarVertexCount(int value) => Math.Clamp(value == 0 ? 5 : value, 3, 32);

    private static GdiPointF LocalToWorld(StageControl stage, int i, GdiPointF local)
    {
        var angle = stage.Scene.Angle[i];
        var cos = MathF.Cos(angle);
        var sin = MathF.Sin(angle);
        return new GdiPointF(
            stage.Scene.X[i] + local.X * cos - local.Y * sin,
            stage.Scene.Y[i] + local.X * sin + local.Y * cos);
    }

    private static IEnumerable<EditHandleKind> BoundaryHandles()
    {
        yield return EditHandleKind.BoundsTopLeft;
        yield return EditHandleKind.BoundsTopRight;
        yield return EditHandleKind.BoundsBottomRight;
        yield return EditHandleKind.BoundsBottomLeft;
    }
}
