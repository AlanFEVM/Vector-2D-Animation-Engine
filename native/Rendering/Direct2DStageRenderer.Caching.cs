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
    private void ResetTarget()
    {
        ClearSoftwareFramePresentation();
        ClearGpuOpticalSurfaces();
        _gpuOpticalShader?.Dispose();
        _gpuOpticalShader = null;
        DisposeWorkspacePreRenderTarget();
        ClearBaseFrameCache();
        ClearReference3DWorkspaceFrameCache();
        ClearReference3DCpuRasterBuffer();
        ClearReference3DGridCache();
        ClearFillEdgeBezierOverlayGeometry();
        ClearCollisionTerrainOverlayGeometry();
        ClearLassoPreviewGeometry();
        ClearBrushCache();
        ClearMixingBrush();
        ClearMixingBrushBitmapCache();
        ClearLineGeometryCache();
        ClearObjectPathGeometryCache();
        ClearObjectStrokeGeometryCache();
        ClearReference3DLocalPathGeometryCache();
        ClearGradientBrushCache();
        ClearTransientGradientBrushes();
        ClearReference3DLocalLightBrushCache();
        ClearReference3DOpticalSurfaceBitmapCache();
        ClearReference3DMaterialBitmapCache();
        ClearTransientReference3DMaterialBitmaps();
        ClearShapeGradientBitmapCache();
        ClearTransientShapeGradientBitmaps();
        ClearPathGradientBrushCache();
        ClearTransientPathGradientBrushes();
        ClearLodBitmapCache();
        ClearImportedSvgBitmapCache();
        ClearBitmapObjectCache();
        _shapeGradientMaskLayer?.Dispose();
        _shapeGradientMaskLayer = null;
        ClearBatch2DLayerCache();
        _deviceContext2?.Dispose();
        _deviceContext2 = null;
        if (_gpuDevice is null) _target?.Dispose();
        _gpuDevice?.Dispose();
        _gpuDevice = null;
        _target = null;
        _targetSize = default;
        _targetHwnd = IntPtr.Zero;
    }

    private void ClearFillEdgeBezierOverlayGeometry()
    {
        _fillEdgeBezierOverlayGeometry?.Dispose();
        _fillEdgeBezierOverlayGeometry = null;
        _fillEdgeBezierOverlayGeometryRevision = -1;
        _fillEdgeBezierOverlayGeometryCameraX = 0;
        _fillEdgeBezierOverlayGeometryCameraY = 0;
        _fillEdgeBezierOverlayGeometryZoom = 0;
        _fillEdgeBezierOverlayGeometryWidth = 0;
        _fillEdgeBezierOverlayGeometryHeight = 0;
    }

    private void ClearCollisionTerrainOverlayGeometry()
    {
        _collisionTerrainOverlayGeometry?.Dispose();
        _collisionTerrainOverlayGeometry = null;
        _collisionTerrainOverlayScene = null;
        _collisionTerrainOverlayRevision = -1;
        _collisionTerrainOverlayLayer = -1;
        _collisionTerrainOverlayObjects = [];
    }

    private void RecordFailure()
    {
        _consecutiveFailures++;
        if (_consecutiveFailures >= 3) _disabled = true;
    }

    private void ClearBrushCache()
    {
        foreach (var brush in _brushCache.Values) brush.Dispose();
        _brushCache.Clear();
    }

    private void ClearTransientReference3DMaterialBitmaps()
    {
        foreach (var cached in _transientReference3DMaterialBitmaps) cached.Dispose();
        _transientReference3DMaterialBitmaps.Clear();
    }

    private void ClearReference3DMaterialBitmapCache()
    {
        foreach (var candidates in _reference3DMaterialBitmapCache.Values)
        {
            foreach (var cached in candidates) cached.Dispose();
        }
        _reference3DMaterialBitmapCache.Clear();
        _reference3DMaterialBitmapCacheEntryCount = 0;
        _reference3DMaterialBitmapCacheBytes = 0;
    }

    private void PruneReference3DMaterialBitmapCache(
        VectorScene editableScene,
        VectorScene? underlayScene,
        VectorScene? onionSkinScene,
        VectorScene? dragPreviewScene)
    {
        List<Reference3DMaterialBitmapCacheKey>? staleKeys = null;
        foreach (var key in _reference3DMaterialBitmapCache.Keys)
        {
            var scene = key.Scene;
            var activeScene = ReferenceEquals(scene, editableScene)
                || ReferenceEquals(scene, underlayScene)
                || ReferenceEquals(scene, onionSkinScene)
                || ReferenceEquals(scene, dragPreviewScene);
            if (activeScene)
            {
                continue;
            }

            (staleKeys ??= []).Add(key);
        }

        if (staleKeys is null) return;
        foreach (var key in staleKeys)
        {
            if (!_reference3DMaterialBitmapCache.Remove(key, out var candidates)) continue;
            foreach (var cached in candidates)
            {
                _reference3DMaterialBitmapCacheEntryCount--;
                _reference3DMaterialBitmapCacheBytes -= cached.ByteSize;
                cached.Dispose();
            }
        }
    }

    private void ClearMixingBrush()
    {
        _mixingBrush?.Dispose();
        _mixingBrush = null;
    }

    private bool CacheMembershipChanged(
        VectorScene editableScene,
        VectorScene? underlayScene,
        VectorScene? onionSkinScene,
        VectorScene? dragPreviewScene)
    {
        var editable = new SceneMembershipStamp(editableScene, editableScene.ObjectCount);
        var underlay = new SceneMembershipStamp(underlayScene, underlayScene?.ObjectCount ?? 0);
        var onionSkin = new SceneMembershipStamp(onionSkinScene, onionSkinScene?.ObjectCount ?? 0);
        var dragPreview = new SceneMembershipStamp(dragPreviewScene, dragPreviewScene?.ObjectCount ?? 0);
        if (_cacheEditableMembership == editable
            && _cacheUnderlayMembership == underlay
            && _cacheOnionSkinMembership == onionSkin
            && _cacheDragPreviewMembership == dragPreview)
        {
            return false;
        }

        _cacheEditableMembership = editable;
        _cacheUnderlayMembership = underlay;
        _cacheOnionSkinMembership = onionSkin;
        _cacheDragPreviewMembership = dragPreview;
        return true;
    }

    private void PruneLineGeometryCache(
        VectorScene editableScene,
        VectorScene? underlayScene,
        VectorScene? onionSkinScene,
        VectorScene? dragPreviewScene)
    {
        List<(VectorScene Scene, int ObjectIndex)>? staleKeys = null;
        foreach (var key in _lineGeometryCache.Keys)
        {
            var activeScene = ReferenceEquals(key.Scene, editableScene)
                || ReferenceEquals(key.Scene, underlayScene)
                || ReferenceEquals(key.Scene, onionSkinScene)
                || ReferenceEquals(key.Scene, dragPreviewScene);
            if (activeScene
                && (uint)key.ObjectIndex < key.Scene.ObjectCount
                && key.Scene.ShapeKind[key.ObjectIndex] == ShapeKind.Line)
            {
                continue;
            }

            (staleKeys ??= []).Add(key);
        }

        if (staleKeys is null) return;
        foreach (var key in staleKeys)
        {
            if (_lineGeometryCache.Remove(key, out var cached)) cached.Dispose();
        }
    }

    private void ClearLineGeometryCache()
    {
        foreach (var cached in _lineGeometryCache.Values) cached.Dispose();
        _lineGeometryCache.Clear();
    }

    private void PruneObjectPathGeometryCache(
        VectorScene editableScene,
        VectorScene? underlayScene,
        VectorScene? onionSkinScene,
        VectorScene? dragPreviewScene)
    {
        List<(VectorScene Scene, int ObjectIndex)>? staleKeys = null;
        foreach (var key in _objectPathGeometryCache.Keys)
        {
            var activeScene = ReferenceEquals(key.Scene, editableScene)
                || ReferenceEquals(key.Scene, underlayScene)
                || ReferenceEquals(key.Scene, onionSkinScene)
                || ReferenceEquals(key.Scene, dragPreviewScene);
            if (activeScene
                && (uint)key.ObjectIndex < key.Scene.ObjectCount
                && key.Scene.ShapeKind[key.ObjectIndex] == ShapeKind.Path)
            {
                continue;
            }

            (staleKeys ??= []).Add(key);
        }

        if (staleKeys is null) return;
        foreach (var key in staleKeys)
        {
            if (_objectPathGeometryCache.Remove(key, out var cached)) ReleaseObjectPathGeometry(cached);
        }
    }

    private void ClearObjectPathGeometryCache()
    {
        foreach (var cached in _objectPathGeometryCache.Values) cached.ReleaseReference();
        _objectPathGeometryCache.Clear();
        _objectPathGeometryContentCache.Clear();
    }

    private void ReleaseObjectPathGeometry(CachedObjectPathGeometry cached)
    {
        cached.ReleaseReference();
        if (cached.ReferenceCount > 0) return;
        if (!_objectPathGeometryContentCache.TryGetValue(cached.ContentKey, out var entries)) return;
        entries.Remove(cached);
        if (entries.Count == 0) _objectPathGeometryContentCache.Remove(cached.ContentKey);
    }

    private CachedGradientBrush GradientBrush(
        VectorScene scene,
        int objectIndex,
        GradientKind kind,
        IReadOnlyList<GradientStop> stops)
    {
        var key = (scene, objectIndex);
        if (_gradientBrushCache.TryGetValue(key, out var cached))
        {
            if (cached.Matches(kind, stops))
            {
                LastGradientBrushCacheReuses++;
                return cached;
            }

            _gradientBrushCache.Remove(key);
            cached.Dispose();
        }

        cached = CreateGradientBrush(kind, stops);
        if (_gradientBrushCache.Count >= MaxGradientBrushCacheEntries)
        {
            _transientGradientBrushes.Add(cached);
            LastGradientBrushCacheBuilds++;
            return cached;
        }

        _gradientBrushCache[key] = cached;
        LastGradientBrushCacheBuilds++;
        return cached;
    }

    private CachedGradientBrush CreateGradientBrush(GradientKind kind, IReadOnlyList<GradientStop> stops)
    {
        var gradientStops = stops
            .Select(stop => new Vortice.Direct2D1.GradientStop(stop.Position, ToColor4(GdiColor.FromArgb(stop.Argb))))
            .ToArray();
        var collection = _target!.CreateGradientStopCollection(gradientStops, Gamma.StandardRgb, ExtendMode.Clamp);
        try
        {
            if (kind == GradientKind.Radial)
            {
                var brush = _target.CreateRadialGradientBrush(
                    new RadialGradientBrushProperties(Vector2.Zero, Vector2.Zero, 1f, 1f),
                    new BrushProperties(1f),
                    collection);
                return new CachedGradientBrush(kind, stops.ToArray(), collection, null, brush);
            }

            var linearBrush = _target.CreateLinearGradientBrush(
                new LinearGradientBrushProperties(Vector2.Zero, Vector2.UnitX),
                new BrushProperties(1f),
                collection);
            return new CachedGradientBrush(kind, stops.ToArray(), collection, linearBrush, null);
        }
        catch
        {
            collection.Dispose();
            throw;
        }
    }

    private void PruneGradientBrushCache(
        VectorScene editableScene,
        VectorScene? underlayScene,
        VectorScene? onionSkinScene,
        VectorScene? dragPreviewScene)
    {
        List<(VectorScene Scene, int ObjectIndex)>? staleKeys = null;
        foreach (var key in _gradientBrushCache.Keys)
        {
            var scene = key.Scene;
            if ((ReferenceEquals(scene, editableScene)
                    || ReferenceEquals(scene, underlayScene)
                    || ReferenceEquals(scene, onionSkinScene)
                    || ReferenceEquals(scene, dragPreviewScene))
                && (uint)key.ObjectIndex < scene.ObjectCount
                && scene.HasGradient(key.ObjectIndex))
            {
                continue;
            }

            (staleKeys ??= []).Add(key);
        }

        if (staleKeys is null) return;
        foreach (var key in staleKeys)
        {
            if (_gradientBrushCache.Remove(key, out var cached)) cached.Dispose();
        }
    }

    private void ClearGradientBrushCache()
    {
        foreach (var cached in _gradientBrushCache.Values) cached.Dispose();
        _gradientBrushCache.Clear();
    }

    private void ClearTransientGradientBrushes()
    {
        foreach (var cached in _transientGradientBrushes) cached.Dispose();
        _transientGradientBrushes.Clear();
    }

    private CachedShapeGradientBitmap? ShapeGradientBitmap(
        StageControl stage,
        VectorScene scene,
        int objectIndex,
        IReadOnlyList<GradientStop> stops)
    {
        var center = scene.GetGradientStart(objectIndex);
        var sourcePosition = new GdiPointF(scene.X[objectIndex], scene.Y[objectIndex]);
        var sourceAngle = scene.Angle[objectIndex];
        var hasLocalMapping = scene.TryGetShapeGradientMappingLocalContours(objectIndex, out var localContoursIdentity);
        var key = (scene, objectIndex);
        if (hasLocalMapping
            && _shapeGradientBitmapCache.TryGetValue(key, out var sourceCached)
            && (sourceCached.MatchesSource(center, localContoursIdentity, sourcePosition, sourceAngle)
                || sourceCached.TryRelocateSource(center, localContoursIdentity, sourcePosition, sourceAngle)))
        {
            ShapeGradientBitmapSize(stage, sourceCached.WorldBounds, out var sourcePixelWidth, out var sourcePixelHeight);
            if (sourceCached.PixelWidth == sourcePixelWidth && sourceCached.PixelHeight == sourcePixelHeight)
            {
                if (sourceCached.Stops.SequenceEqual(stops))
                {
                    LastShapeGradientBitmapCacheReuses++;
                    return sourceCached;
                }

                var recolored = new CachedShapeGradientBitmap(
                    stops.ToArray(),
                    center,
                    localContoursIdentity,
                    sourcePosition,
                    sourceAngle,
                    sourceCached.Contours,
                    sourceCached.WorldBounds,
                    sourceCached.PixelWidth,
                    sourceCached.PixelHeight,
                    sourceCached.Positions,
                    CreateShapeGradientBitmap(stops, sourceCached.Positions, sourceCached.PixelWidth, sourceCached.PixelHeight));
                _shapeGradientBitmapCache[key] = recolored;
                sourceCached.Dispose();
                LastShapeGradientBitmapCacheBuilds++;
                return recolored;
            }
        }

        var contours = scene.GetShapeGradientMappingContours(objectIndex);
        if (!TryGetContourBounds(contours, out var worldBounds)) return null;
        ShapeGradientBitmapSize(stage, worldBounds, out var pixelWidth, out var pixelHeight);
        if (_shapeGradientBitmapCache.TryGetValue(key, out var cached))
        {
            if (cached.MatchesLayout(center, contours, worldBounds, pixelWidth, pixelHeight))
            {
                if (cached.Stops.SequenceEqual(stops))
                {
                    LastShapeGradientBitmapCacheReuses++;
                    return cached;
                }

                var recolored = new CachedShapeGradientBitmap(
                    stops.ToArray(),
                    center,
                    hasLocalMapping ? localContoursIdentity : null,
                    sourcePosition,
                    sourceAngle,
                    cached.Contours,
                    worldBounds,
                    pixelWidth,
                    pixelHeight,
                    cached.Positions,
                    CreateShapeGradientBitmap(stops, cached.Positions, pixelWidth, pixelHeight));
                _shapeGradientBitmapCache[key] = recolored;
                cached.Dispose();
                LastShapeGradientBitmapCacheBuilds++;
                return recolored;
            }

            _shapeGradientBitmapCache.Remove(key);
            cached.Dispose();
        }

        var copiedContours = contours.Select(contour => contour.ToArray()).ToArray();
        var sharedLayout = _shapeGradientBitmapCache.Values.FirstOrDefault(item =>
            item.MatchesLayout(center, contours, worldBounds, pixelWidth, pixelHeight));
        var positions = sharedLayout?.Positions
            ?? CreateShapeGradientPositionMap(copiedContours, center, worldBounds, pixelWidth, pixelHeight);
        var created = new CachedShapeGradientBitmap(
            stops.ToArray(),
            center,
            hasLocalMapping ? localContoursIdentity : null,
            sourcePosition,
            sourceAngle,
            copiedContours,
            worldBounds,
            pixelWidth,
            pixelHeight,
            positions,
            CreateShapeGradientBitmap(stops, positions, pixelWidth, pixelHeight));
        LastShapeGradientBitmapCacheBuilds++;
        if (_shapeGradientBitmapCache.Count >= MaxShapeGradientBitmapCacheEntries)
        {
            _transientShapeGradientBitmaps.Add(created);
            return created;
        }

        _shapeGradientBitmapCache[key] = created;
        return created;
    }

    private static void ShapeGradientBitmapSize(
        StageControl stage,
        GdiRectangleF worldBounds,
        out int pixelWidth,
        out int pixelHeight)
    {
        pixelWidth = Math.Clamp((int)MathF.Ceiling(stage.WorldLengthToScreen(worldBounds.Width)), 16, 1024);
        pixelHeight = Math.Clamp((int)MathF.Ceiling(stage.WorldLengthToScreen(worldBounds.Height)), 16, 1024);
        var pixelCount = pixelWidth * pixelHeight;
        if (pixelCount <= MaxShapeGradientBitmapPixels) return;

        var scale = MathF.Sqrt(MaxShapeGradientBitmapPixels / (float)pixelCount);
        pixelWidth = Math.Max(16, (int)MathF.Floor(pixelWidth * scale));
        pixelHeight = Math.Max(16, (int)MathF.Floor(pixelHeight * scale));
    }

    private ID2D1Layer ShapeGradientMaskLayer()
    {
        return _shapeGradientMaskLayer ??= _target!.CreateLayer();
    }

    private ID2D1PathGeometry? ShapeGradientMaskGeometry(VectorScene scene, int objectIndex)
    {
        scene.TryGetPathLocalContours(objectIndex, out var pathContoursIdentity);
        GdiPointF[][]? pathIdentity = pathContoursIdentity.Length > 0 ? pathContoursIdentity : null;
        scene.TryGetPathBezierLocalContours(objectIndex, out var pathBezierContoursIdentity);
        PathBezierNode[][]? pathBezierIdentity = pathBezierContoursIdentity.Length > 0
            ? pathBezierContoursIdentity
            : null;
        var shape = scene.ShapeKind[objectIndex];
        var shapeVertexCount = scene.GetShapeVertexCount(objectIndex);
        var size = new GdiPointF(scene.Width[objectIndex], scene.Height[objectIndex]);
        var key = (scene, objectIndex);
        if (_shapeGradientMaskGeometryCache.TryGetValue(key, out var cached))
        {
            if (cached.Matches(pathIdentity, pathBezierIdentity, shape, shapeVertexCount, size))
            {
                LastShapeGradientMaskGeometryCacheReuses++;
                return cached.Geometry;
            }
            _shapeGradientMaskGeometryCache.Remove(key);
            cached.Dispose();
        }

        if (_shapeGradientMaskGeometryCache.Count >= MaxShapeGradientMaskGeometryCacheEntries)
        {
            ClearShapeGradientMaskGeometryCache();
        }

        var geometry = CreateObjectLocalBoundaryGeometry(scene, objectIndex);
        if (geometry is null) return null;

        _shapeGradientMaskGeometryCache[key] = new CachedShapeGradientMaskGeometry(
            pathIdentity,
            pathBezierIdentity,
            shape,
            shapeVertexCount,
            size,
            geometry);
        LastShapeGradientMaskGeometryCacheBuilds++;
        return geometry;
    }

    private static Matrix3x2 WorldToScreenTransform(StageControl stage)
    {
        var origin = stage.WorldToScreen(0, 0);
        var scale = VectorUnits.PixelsPerUnit * stage.Zoom;
        return Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(origin.X, origin.Y);
    }

    private static bool TryGetContourBounds(IReadOnlyList<GdiPointF[]> contours, out GdiRectangleF bounds)
    {
        var minimumX = float.PositiveInfinity;
        var minimumY = float.PositiveInfinity;
        var maximumX = float.NegativeInfinity;
        var maximumY = float.NegativeInfinity;
        foreach (var point in contours.SelectMany(contour => contour))
        {
            minimumX = Math.Min(minimumX, point.X);
            minimumY = Math.Min(minimumY, point.Y);
            maximumX = Math.Max(maximumX, point.X);
            maximumY = Math.Max(maximumY, point.Y);
        }

        if (!float.IsFinite(minimumX)
            || maximumX - minimumX <= 0.001f
            || maximumY - minimumY <= 0.001f)
        {
            bounds = GdiRectangleF.Empty;
            return false;
        }

        bounds = GdiRectangleF.FromLTRB(minimumX, minimumY, maximumX, maximumY);
        return true;
    }

    private static byte[] CreateShapeGradientPositionMap(
        IReadOnlyList<GdiPointF[]> contours,
        GdiPointF center,
        GdiRectangleF bounds,
        int pixelWidth,
        int pixelHeight)
    {
        var inside = RasterizeShapeGradientMask(contours, bounds, pixelWidth, pixelHeight);
        var boundaryDistances = ShapeGradientBoundaryDistances(inside, pixelWidth, pixelHeight);
        var maximumBoundaryDistance = boundaryDistances
            .Where((_, index) => inside[index])
            .DefaultIfEmpty(1f)
            .Max();
        var desiredCenterX = (center.X - bounds.Left) / bounds.Width * pixelWidth - 0.5f;
        var desiredCenterY = (center.Y - bounds.Top) / bounds.Height * pixelHeight - 0.5f;
        var centerX = Math.Clamp((int)MathF.Round(desiredCenterX), 0, pixelWidth - 1);
        var centerY = Math.Clamp((int)MathF.Round(desiredCenterY), 0, pixelHeight - 1);
        if (!inside[centerY * pixelWidth + centerX])
        {
            var preferredDepth = Math.Max(1f, maximumBoundaryDistance * 0.65f);
            var bestDistanceSquared = float.PositiveInfinity;
            var foundPreferred = false;
            for (var pixel = 0; pixel < inside.Length; pixel++)
            {
                if (!inside[pixel]) continue;
                var preferred = boundaryDistances[pixel] >= preferredDepth;
                if (foundPreferred && !preferred) continue;
                var x = pixel % pixelWidth;
                var y = pixel / pixelWidth;
                var dx = x - desiredCenterX;
                var dy = y - desiredCenterY;
                var distanceSquared = dx * dx + dy * dy;
                if ((!foundPreferred && preferred) || distanceSquared < bestDistanceSquared)
                {
                    foundPreferred = preferred;
                    bestDistanceSquared = distanceSquared;
                    centerX = x;
                    centerY = y;
                }
            }
        }

        var maximumCenterDistance = 1f;
        for (var pixel = 0; pixel < inside.Length; pixel++)
        {
            if (!inside[pixel]) continue;
            var dx = pixel % pixelWidth - centerX;
            var dy = pixel / pixelWidth - centerY;
            maximumCenterDistance = Math.Max(maximumCenterDistance, MathF.Sqrt(dx * dx + dy * dy));
        }
        var boundaryScale = maximumCenterDistance / Math.Max(1f, maximumBoundaryDistance - 1f);
        var positions = new byte[pixelWidth * pixelHeight];
        ParallelBatch.For(positions.Length, 4096, (_, start, end) =>
        {
            for (var pixel = start; pixel < end; pixel++)
            {
                if (!inside[pixel])
                {
                    positions[pixel] = byte.MaxValue;
                    continue;
                }

                var x = pixel % pixelWidth;
                var y = pixel / pixelWidth;
                var dx = x - centerX;
                var dy = y - centerY;
                var centerDistance = MathF.Sqrt(dx * dx + dy * dy);
                var boundaryDistance = Math.Max(0f, boundaryDistances[pixel] - 1f) * boundaryScale;
                var denominator = centerDistance + boundaryDistance;
                var position = denominator <= 0.0001f ? 0f : centerDistance / denominator;
                positions[pixel] = (byte)Math.Clamp(MathF.Round(position * 255f), 0f, 255f);
            }
        });
        return positions;
    }

    private static bool[] RasterizeShapeGradientMask(
        IReadOnlyList<GdiPointF[]> contours,
        GdiRectangleF bounds,
        int pixelWidth,
        int pixelHeight)
    {
        var inside = new bool[pixelWidth * pixelHeight];
        var intersections = new List<float>(256);
        for (var y = 0; y < pixelHeight; y++)
        {
            intersections.Clear();
            var worldY = bounds.Top + (y + 0.5f) * bounds.Height / pixelHeight;
            foreach (var contour in contours)
            {
                for (var index = 0; index < contour.Length; index++)
                {
                    var first = contour[index];
                    var second = contour[(index + 1) % contour.Length];
                    if ((first.Y > worldY) == (second.Y > worldY)) continue;
                    var amount = (worldY - first.Y) / (second.Y - first.Y);
                    intersections.Add(first.X + (second.X - first.X) * amount);
                }
            }

            intersections.Sort();
            for (var index = 1; index < intersections.Count; index += 2)
            {
                var firstPixel = Math.Clamp(
                    (int)MathF.Ceiling((intersections[index - 1] - bounds.Left) / bounds.Width * pixelWidth - 0.5f),
                    0,
                    pixelWidth - 1);
                var lastPixel = Math.Clamp(
                    (int)MathF.Floor((intersections[index] - bounds.Left) / bounds.Width * pixelWidth - 0.5f),
                    0,
                    pixelWidth - 1);
                for (var x = firstPixel; x <= lastPixel; x++) inside[y * pixelWidth + x] = true;
            }
        }

        return inside;
    }

    private static float[] ShapeGradientBoundaryDistances(bool[] inside, int width, int height)
    {
        const float diagonal = 1.41421356f;
        var distances = new float[inside.Length];
        for (var pixel = 0; pixel < inside.Length; pixel++)
        {
            var x = pixel % width;
            var y = pixel / width;
            distances[pixel] = inside[pixel]
                ? x == 0 || y == 0 || x == width - 1 || y == height - 1 ? 1f : float.PositiveInfinity
                : 0f;
        }

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var pixel = y * width + x;
                if (!inside[pixel]) continue;
                var distance = distances[pixel];
                if (x > 0) distance = Math.Min(distance, distances[pixel - 1] + 1f);
                if (y > 0)
                {
                    distance = Math.Min(distance, distances[pixel - width] + 1f);
                    if (x > 0) distance = Math.Min(distance, distances[pixel - width - 1] + diagonal);
                    if (x + 1 < width) distance = Math.Min(distance, distances[pixel - width + 1] + diagonal);
                }
                distances[pixel] = distance;
            }
        }

        for (var y = height - 1; y >= 0; y--)
        {
            for (var x = width - 1; x >= 0; x--)
            {
                var pixel = y * width + x;
                if (!inside[pixel]) continue;
                var distance = distances[pixel];
                if (x + 1 < width) distance = Math.Min(distance, distances[pixel + 1] + 1f);
                if (y + 1 < height)
                {
                    distance = Math.Min(distance, distances[pixel + width] + 1f);
                    if (x > 0) distance = Math.Min(distance, distances[pixel + width - 1] + diagonal);
                    if (x + 1 < width) distance = Math.Min(distance, distances[pixel + width + 1] + diagonal);
                }
                distances[pixel] = distance;
            }
        }

        return distances;
    }

    private ID2D1Bitmap CreateShapeGradientBitmap(
        IReadOnlyList<GradientStop> stops,
        IReadOnlyList<byte> positions,
        int pixelWidth,
        int pixelHeight)
    {
        var pixels = ArrayPool<int>.Shared.Rent(pixelWidth * pixelHeight);
        try
        {
            ParallelBatch.For(positions.Count, 4096, (_, start, end) =>
            {
                for (var pixel = start; pixel < end; pixel++)
                {
                    pixels[pixel] = PremultiplyArgb(SampleGradientArgb(stops, positions[pixel] / 255f));
                }
            });

            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                var properties = new BitmapProperties(
                    new PixelFormat(Format.B8G8R8A8_UNorm, DCommonAlphaMode.Premultiplied),
                    96,
                    96);
                return _target!.CreateBitmap(
                    new SizeI(pixelWidth, pixelHeight),
                    handle.AddrOfPinnedObject(),
                    (uint)(pixelWidth * sizeof(int)),
                    properties);
            }
            finally
            {
                handle.Free();
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(pixels);
        }
    }

    private static int SampleGradientArgb(IReadOnlyList<GradientStop> stops, float position)
    {
        if (stops.Count == 0) return GdiColor.White.ToArgb();
        position = Math.Clamp(position, 0f, 1f);
        var previous = stops[0];
        for (var index = 1; index < stops.Count; index++)
        {
            var current = stops[index];
            if (position > current.Position)
            {
                previous = current;
                continue;
            }

            var amount = Math.Clamp((position - previous.Position) / Math.Max(0.0001f, current.Position - previous.Position), 0f, 1f);
            var from = GdiColor.FromArgb(previous.Argb);
            var to = GdiColor.FromArgb(current.Argb);
            return GdiColor.FromArgb(
                (int)MathF.Round(from.A + (to.A - from.A) * amount),
                (int)MathF.Round(from.R + (to.R - from.R) * amount),
                (int)MathF.Round(from.G + (to.G - from.G) * amount),
                (int)MathF.Round(from.B + (to.B - from.B) * amount)).ToArgb();
        }

        return stops[^1].Argb;
    }

    private void PruneShapeGradientBitmapCache(
        VectorScene editableScene,
        VectorScene? underlayScene,
        VectorScene? onionSkinScene,
        VectorScene? dragPreviewScene)
    {
        List<(VectorScene Scene, int ObjectIndex)>? staleKeys = null;
        foreach (var key in _shapeGradientBitmapCache.Keys)
        {
            var scene = key.Scene;
            if ((ReferenceEquals(scene, editableScene)
                    || ReferenceEquals(scene, underlayScene)
                    || ReferenceEquals(scene, onionSkinScene)
                    || ReferenceEquals(scene, dragPreviewScene))
                && (uint)key.ObjectIndex < scene.ObjectCount
                && scene.GetGradientKind(key.ObjectIndex) == GradientKind.ShapeRadial)
            {
                continue;
            }

            (staleKeys ??= []).Add(key);
        }

        if (staleKeys is null) return;
        foreach (var key in staleKeys)
        {
            if (_shapeGradientBitmapCache.Remove(key, out var cached)) cached.Dispose();
        }
    }

    private void ClearShapeGradientBitmapCache()
    {
        foreach (var cached in _shapeGradientBitmapCache.Values) cached.Dispose();
        _shapeGradientBitmapCache.Clear();
    }

    private void PruneShapeGradientMaskGeometryCache(
        VectorScene editableScene,
        VectorScene? underlayScene,
        VectorScene? onionSkinScene,
        VectorScene? dragPreviewScene)
    {
        List<(VectorScene Scene, int ObjectIndex)>? staleKeys = null;
        foreach (var key in _shapeGradientMaskGeometryCache.Keys)
        {
            var scene = key.Scene;
            if ((ReferenceEquals(scene, editableScene)
                    || ReferenceEquals(scene, underlayScene)
                    || ReferenceEquals(scene, onionSkinScene)
                    || ReferenceEquals(scene, dragPreviewScene))
                && (uint)key.ObjectIndex < scene.ObjectCount
                && scene.GetGradientKind(key.ObjectIndex) == GradientKind.ShapeRadial)
            {
                continue;
            }

            (staleKeys ??= []).Add(key);
        }

        if (staleKeys is null) return;
        foreach (var key in staleKeys)
        {
            if (_shapeGradientMaskGeometryCache.Remove(key, out var cached)) cached.Dispose();
        }
    }

    private void ClearShapeGradientMaskGeometryCache()
    {
        foreach (var cached in _shapeGradientMaskGeometryCache.Values) cached.Dispose();
        _shapeGradientMaskGeometryCache.Clear();
    }

    private void ClearTransientShapeGradientBitmaps()
    {
        foreach (var cached in _transientShapeGradientBitmaps) cached.Dispose();
        _transientShapeGradientBitmaps.Clear();
    }

    private CachedPathGradientBrushes PathGradientBrushes(
        VectorScene scene,
        int objectIndex,
        IReadOnlyList<GradientStop> stops,
        GradientPathGradientSegment[] segments)
    {
        var key = (scene, objectIndex);
        if (_pathGradientBrushCache.TryGetValue(key, out var cached))
        {
            if (cached.Matches(stops, segments))
            {
                LastPathGradientBrushCacheReuses++;
                return cached;
            }

            _pathGradientBrushCache.Remove(key);
            _pathGradientBrushCount -= cached.Brushes.Length;
            cached.Dispose();
        }

        if (_pathGradientBrushCache.Count >= MaxPathGradientBrushCacheEntries
            || _pathGradientBrushCount > MaxPathGradientBrushCount - segments.Length)
        {
            cached = CreatePathGradientBrushes(stops, segments);
            _transientPathGradientBrushes.Add(cached);
            LastPathGradientBrushCacheBuilds++;
            return cached;
        }

        cached = CreatePathGradientBrushes(stops, segments);
        _pathGradientBrushCache[key] = cached;
        _pathGradientBrushCount += cached.Brushes.Length;
        LastPathGradientBrushCacheBuilds++;
        return cached;
    }

    private CachedPathGradientBrushes CreatePathGradientBrushes(
        IReadOnlyList<GradientStop> stops,
        GradientPathGradientSegment[] segments)
    {
        var gradientStops = stops
            .Select(stop => new Vortice.Direct2D1.GradientStop(stop.Position, ToColor4(GdiColor.FromArgb(stop.Argb))))
            .ToArray();
        var collection = _target!.CreateGradientStopCollection(gradientStops, Gamma.StandardRgb, ExtendMode.Clamp);
        var brushes = new ID2D1LinearGradientBrush[segments.Length];
        try
        {
            for (var index = 0; index < brushes.Length; index++)
            {
                brushes[index] = _target.CreateLinearGradientBrush(
                    new LinearGradientBrushProperties(Vector2.Zero, Vector2.UnitX),
                    new BrushProperties(1f),
                    collection);
            }
        }
        catch
        {
            foreach (var brush in brushes) brush?.Dispose();
            collection.Dispose();
            throw;
        }

        return new CachedPathGradientBrushes(stops.ToArray(), segments.ToArray(), collection, brushes);
    }

    private void PrunePathGradientBrushCache(
        VectorScene editableScene,
        VectorScene? underlayScene,
        VectorScene? onionSkinScene,
        VectorScene? dragPreviewScene)
    {
        List<(VectorScene Scene, int ObjectIndex)>? staleKeys = null;
        foreach (var item in _pathGradientBrushCache)
        {
            var scene = item.Key.Scene;
            var isActiveScene = ReferenceEquals(scene, editableScene)
                || ReferenceEquals(scene, underlayScene)
                || ReferenceEquals(scene, onionSkinScene)
                || ReferenceEquals(scene, dragPreviewScene);
            if (!isActiveScene || (uint)item.Key.ObjectIndex >= scene.ObjectCount)
            {
                (staleKeys ??= []).Add(item.Key);
            }
        }

        if (staleKeys is null) return;
        foreach (var key in staleKeys)
        {
            if (!_pathGradientBrushCache.Remove(key, out var cached)) continue;
            _pathGradientBrushCount -= cached.Brushes.Length;
            cached.Dispose();
        }
    }

    private void ClearPathGradientBrushCache()
    {
        foreach (var cached in _pathGradientBrushCache.Values) cached.Dispose();
        _pathGradientBrushCache.Clear();
        _pathGradientBrushCount = 0;
    }

    private void ClearTransientPathGradientBrushes()
    {
        foreach (var cached in _transientPathGradientBrushes) cached.Dispose();
        _transientPathGradientBrushes.Clear();
    }

    private void PruneLodBitmapCache(
        VectorScene editableScene,
        VectorScene? underlayScene,
        VectorScene? onionSkinScene,
        VectorScene? dragPreviewScene)
    {
        List<LodBitmapKey>? staleKeys = null;
        foreach (var key in _lodBitmapCache.Keys)
        {
            if (ReferenceEquals(key.Scene, editableScene)
                || ReferenceEquals(key.Scene, underlayScene)
                || ReferenceEquals(key.Scene, onionSkinScene)
                || ReferenceEquals(key.Scene, dragPreviewScene))
            {
                continue;
            }
            (staleKeys ??= []).Add(key);
        }

        if (staleKeys is null) return;
        foreach (var key in staleKeys)
        {
            if (_lodBitmapCache.Remove(key, out var cached)) cached.Dispose();
        }
    }

    private void ClearLodBitmapCache()
    {
        foreach (var cached in _lodBitmapCache.Values) cached.Dispose();
        _lodBitmapCache.Clear();
    }

    private void ClearImportedSvgBitmapCache()
    {
        foreach (var cached in _importedSvgBitmapCache.Values) cached.Dispose();
        _importedSvgBitmapCache.Clear();
        _importedSvgBitmapCacheBytes = 0;
    }

    private void ClearBitmapObjectCache()
    {
        foreach (var cached in _bitmapObjectCache.Values) cached.Dispose();
        _bitmapObjectCache.Clear();
        _bitmapObjectCacheBytes = 0;
    }

    private ID2D1PathGeometry FreehandGeometry(VectorScene scene, int objectIndex, GdiPointF[] localPoints)
    {
        var key = (scene, objectIndex);
        _freehandGeometryCache.TryGetValue(key, out var cached);
        if (cached is not null)
        {
            if (ReferenceEquals(cached.Points, localPoints)) return cached.Geometry;
            _freehandGeometryCache.Remove(key);
            _freehandGeometryCachePointCount -= cached.Points.Length;
            cached.Dispose();
        }

        if (_freehandGeometryCache.Count >= MaxFreehandGeometryCacheEntries
            || _freehandGeometryCachePointCount > MaxFreehandGeometryCachePoints - localPoints.Length)
        {
            EvictFreehandGeometryCache();
        }

        var geometry = _factory!.CreatePathGeometry();
        using (var sink = geometry.Open())
        {
            sink.BeginFigure(new Vector2(localPoints[0].X, localPoints[0].Y), FigureBegin.Hollow);
            for (var i = 1; i < localPoints.Length; i++) sink.AddLine(new Vector2(localPoints[i].X, localPoints[i].Y));
            sink.EndFigure(FigureEnd.Open);
            sink.Close();
        }

        _freehandGeometryCache[key] = new CachedFreehandGeometry(localPoints, geometry);
        _freehandGeometryCachePointCount += localPoints.Length;
        return geometry;
    }

    private ID2D1StrokeStyle RoundStrokeStyle()
    {
        if (_roundStrokeStyle is not null) return _roundStrokeStyle;
        var properties = new StrokeStyleProperties
        {
            StartCap = CapStyle.Round,
            EndCap = CapStyle.Round,
            DashCap = CapStyle.Round,
            LineJoin = LineJoin.Round,
            MiterLimit = 1,
            DashStyle = DashStyle.Solid,
            DashOffset = 0
        };
        _roundStrokeStyle = _factory!.CreateStrokeStyle(properties, Array.Empty<float>());
        return _roundStrokeStyle;
    }

    private ID2D1StrokeStyle PreviewBoundsStrokeStyle()
    {
        if (_previewBoundsStrokeStyle is not null) return _previewBoundsStrokeStyle;
        var properties = new StrokeStyleProperties
        {
            StartCap = CapStyle.Flat,
            EndCap = CapStyle.Flat,
            DashCap = CapStyle.Flat,
            LineJoin = LineJoin.Miter,
            MiterLimit = 1,
            DashStyle = DashStyle.Dash,
            DashOffset = 0
        };
        _previewBoundsStrokeStyle = _factory!.CreateStrokeStyle(properties, Array.Empty<float>());
        return _previewBoundsStrokeStyle;
    }

    private ID2D1StrokeStyle LineStrokeStyle(CapStyle startCap, CapStyle endCap, bool miterJoin)
    {
        var key = (startCap, endCap, miterJoin);
        if (_lineStrokeStyles.TryGetValue(key, out var style)) return style;
        var properties = new StrokeStyleProperties
        {
            StartCap = startCap,
            EndCap = endCap,
            DashCap = CapStyle.Round,
            LineJoin = miterJoin ? LineJoin.Miter : LineJoin.Round,
            MiterLimit = miterJoin ? 8 : 1,
            DashStyle = DashStyle.Solid,
            DashOffset = 0
        };
        style = _factory!.CreateStrokeStyle(properties, Array.Empty<float>());
        _lineStrokeStyles.Add(key, style);
        return style;
    }

    private void DisposeLineStrokeStyles()
    {
        foreach (var style in _lineStrokeStyles.Values) style.Dispose();
        _lineStrokeStyles.Clear();
    }

    private void PrepareFreehandGeometryCache(
        VectorScene editableScene,
        VectorScene? underlayScene,
        VectorScene? onionSkinScene,
        VectorScene? dragPreviewScene)
    {
        var sceneSetChanged = !ReferenceEquals(_cachedEditableScene, editableScene)
            || !ReferenceEquals(_cachedUnderlayScene, underlayScene)
            || !ReferenceEquals(_cachedOnionSkinScene, onionSkinScene)
            || !ReferenceEquals(_cachedDragPreviewScene, dragPreviewScene);
        var editableShrank = _freehandSceneObjectCounts.TryGetValue(editableScene, out var previousEditableCount)
            && editableScene.ObjectCount < previousEditableCount;
        var underlayShrank = underlayScene is not null
            && _freehandSceneObjectCounts.TryGetValue(underlayScene, out var previousUnderlayCount)
            && underlayScene.ObjectCount < previousUnderlayCount;
        var onionSkinShrank = onionSkinScene is not null
            && _freehandSceneObjectCounts.TryGetValue(onionSkinScene, out var previousOnionSkinCount)
            && onionSkinScene.ObjectCount < previousOnionSkinCount;
        var dragPreviewShrank = dragPreviewScene is not null
            && _freehandSceneObjectCounts.TryGetValue(dragPreviewScene, out var previousPreviewCount)
            && dragPreviewScene.ObjectCount < previousPreviewCount;

        if (sceneSetChanged || editableShrank || underlayShrank || onionSkinShrank)
        {
            List<(VectorScene Scene, int ObjectIndex)>? staleKeys = null;
            foreach (var item in _freehandGeometryCache)
            {
                var scene = item.Key.Scene;
                var isEditable = ReferenceEquals(scene, editableScene);
                var isUnderlay = underlayScene is not null && ReferenceEquals(scene, underlayScene);
                var isOnionSkin = onionSkinScene is not null && ReferenceEquals(scene, onionSkinScene);
                var isDragPreview = dragPreviewScene is not null && ReferenceEquals(scene, dragPreviewScene);
                if (!isEditable && !isUnderlay && !isOnionSkin && !isDragPreview)
                {
                    (staleKeys ??= new List<(VectorScene Scene, int ObjectIndex)>()).Add(item.Key);
                    continue;
                }

                var sceneShrank = (isEditable && editableShrank)
                    || (isUnderlay && underlayShrank)
                    || (isOnionSkin && onionSkinShrank)
                    || (isDragPreview && dragPreviewShrank);
                if (!sceneShrank) continue;
                if ((uint)item.Key.ObjectIndex < scene.ObjectCount
                    && scene.TryGetFreehandLocalPoints(item.Key.ObjectIndex, out var points)
                    && ReferenceEquals(points, item.Value.Points))
                {
                    continue;
                }

                (staleKeys ??= new List<(VectorScene Scene, int ObjectIndex)>()).Add(item.Key);
            }

            if (staleKeys is not null)
            {
                foreach (var key in staleKeys)
                {
                    if (_freehandGeometryCache.Remove(key, out var cached))
                    {
                        _freehandGeometryCachePointCount -= cached.Points.Length;
                        cached.Dispose();
                    }
                }
            }
        }

        _freehandSceneObjectCounts.Clear();
        _freehandSceneObjectCounts[editableScene] = editableScene.ObjectCount;
        if (underlayScene is not null) _freehandSceneObjectCounts[underlayScene] = underlayScene.ObjectCount;
        if (onionSkinScene is not null) _freehandSceneObjectCounts[onionSkinScene] = onionSkinScene.ObjectCount;
        if (dragPreviewScene is not null) _freehandSceneObjectCounts[dragPreviewScene] = dragPreviewScene.ObjectCount;
        _cachedEditableScene = editableScene;
        _cachedUnderlayScene = underlayScene;
        _cachedOnionSkinScene = onionSkinScene;
        _cachedDragPreviewScene = dragPreviewScene;
    }

    private void ClearFreehandGeometryCache()
    {
        EvictFreehandGeometryCache();
        _freehandSceneObjectCounts.Clear();
        _cachedEditableScene = null;
        _cachedUnderlayScene = null;
        _cachedOnionSkinScene = null;
        _cachedDragPreviewScene = null;
    }

    private void EvictFreehandGeometryCache()
    {
        foreach (var item in _freehandGeometryCache.Values) item.Dispose();
        _freehandGeometryCache.Clear();
        _freehandGeometryCachePointCount = 0;
    }
}
