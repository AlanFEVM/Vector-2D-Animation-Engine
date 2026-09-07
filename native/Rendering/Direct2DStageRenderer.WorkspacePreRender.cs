using System.Diagnostics;
using SharpGen.Runtime;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;
using DCommonAlphaMode = Vortice.DCommon.AlphaMode;

namespace VectorAnimationEngine;

internal sealed partial class Direct2DStageRenderer
{
    private ID2D1BitmapRenderTarget? _workspacePreRenderTarget;
    private ID2D1Bitmap? _workspacePreRenderBitmap;
    private ID2D1HwndRenderTarget? _workspacePreRenderParentTarget;
    private SizeI _workspacePreRenderTargetSize;
    private long _workspacePreRenderTargetGeneration = -1;

    internal bool TryPreRenderReference3DFrame(StageControl stage)
    {
        if (!stage.RendersReferenceProjection
            || stage.UnderlayScene is not null
            || stage.OnionSkinScene is not null
            || stage.DragPreviewScene is not null
            || stage.SceneCompositionMaskClips.Count > 0
            || stage.ReferenceCameraTransitionActive
            || stage.Reference3DOpticalInteractionPreviewActive
            || RequiresSoftwareLayerCompositing(stage)
            || RequiresSoftwareDistortion(stage)
            || _disabled
            || stage.Width <= 0
            || stage.Height <= 0)
        {
            return false;
        }

        try
        {
            EnsureTarget(stage);
            if (_target is not ID2D1HwndRenderTarget mainTarget) return false;
            PrepareReference3DWorkspaceFrameCache(stage);
            if (HasReference3DWorkspaceFrame(stage)) return true;

            var previousTarget = _target;
            var stopwatch = Stopwatch.StartNew();
            ID2D1BitmapRenderTarget? bitmapTarget = null;
            var drawingStarted = false;
            var releaseWorkspaceTarget = false;
            try
            {
                bitmapTarget = EnsureWorkspacePreRenderTarget(mainTarget);
                _target = bitmapTarget;

                bitmapTarget.BeginDraw();
                drawingStarted = true;
                bitmapTarget.Transform = System.Numerics.Matrix3x2.Identity;
                bitmapTarget.AntialiasMode = AntialiasMode.PerPrimitive;
                stage.BeginScenePassOrder();
                var background = ToD2D(stage.BackColor);
                bitmapTarget.Clear(in background);
                DrawGrid(stage);
                Draw3DReferenceGrid(stage);
                var stats = DrawReference3DScene(stage);
                var result = bitmapTarget.EndDraw();
                drawingStarted = false;
                if (result.Failure)
                {
                    releaseWorkspaceTarget = true;
                    return false;
                }

                // Vortice caches this wrapper; keep it alive with the compatible target.
                var sourceBitmap = _workspacePreRenderBitmap ??= bitmapTarget.Bitmap;
                _target = mainTarget;
                if (!TryStoreReference3DWorkspaceFrameBitmap(
                        stage,
                        stats,
                        mainTarget,
                        sourceBitmap))
                {
                    releaseWorkspaceTarget = true;
                    return false;
                }

                return true;
            }
            catch
            {
                releaseWorkspaceTarget = true;
                throw;
            }
            finally
            {
                if (drawingStarted && bitmapTarget is not null)
                {
                    try
                    {
                        var result = bitmapTarget.EndDraw();
                        if (result.Failure) releaseWorkspaceTarget = true;
                    }
                    catch
                    {
                        releaseWorkspaceTarget = true;
                    }
                }

                _target = previousTarget;
                if (releaseWorkspaceTarget)
                {
                    DisposeWorkspacePreRenderTarget();
                }
                LastWorkspaceFramePreRenderMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Reference 3D workspace pre-render skipped: {ex.Message}");
            return false;
        }
    }

    internal double LastWorkspaceFramePreRenderMilliseconds { get; private set; }

    private ID2D1BitmapRenderTarget EnsureWorkspacePreRenderTarget(
        ID2D1HwndRenderTarget parentTarget)
    {
        if (_workspacePreRenderTarget is not null
            && ReferenceEquals(_workspacePreRenderParentTarget, parentTarget)
            && _workspacePreRenderTargetGeneration == _targetGeneration
            && _workspacePreRenderTargetSize.Width == _targetSize.Width
            && _workspacePreRenderTargetSize.Height == _targetSize.Height)
        {
            return _workspacePreRenderTarget;
        }

        DisposeWorkspacePreRenderTarget();
        var pixelFormat = new PixelFormat(
            Format.B8G8R8A8_UNorm,
            DCommonAlphaMode.Ignore);
        var target = parentTarget.CreateCompatibleRenderTarget(
            _targetSize,
            _targetSize,
            pixelFormat,
            CompatibleRenderTargetOptions.None);
        _workspacePreRenderTarget = target;
        _workspacePreRenderParentTarget = parentTarget;
        _workspacePreRenderTargetSize = _targetSize;
        _workspacePreRenderTargetGeneration = _targetGeneration;
        return target;
    }

    private void DisposeWorkspacePreRenderTarget()
    {
        var target = _workspacePreRenderTarget;
        var bitmap = _workspacePreRenderBitmap;
        try
        {
            if (target is not null) ClearTargetBoundResourcesForSwitch();
        }
        finally
        {
            try
            {
                bitmap?.Dispose();
            }
            finally
            {
                try
                {
                    target?.Dispose();
                }
                finally
                {
                    _workspacePreRenderBitmap = null;
                    _workspacePreRenderTarget = null;
                    _workspacePreRenderParentTarget = null;
                    _workspacePreRenderTargetSize = default;
                    _workspacePreRenderTargetGeneration = -1;
                }
            }
        }
    }

    private bool HasReference3DWorkspaceFrame(StageControl stage)
    {
        if (_target is null) return false;
        var key = CreateReference3DWorkspaceFrameCacheKey(stage);
        for (var index = 0; index < _reference3DWorkspaceFrameCache.Count; index++)
        {
            var cached = _reference3DWorkspaceFrameCache[index];
            if (cached.Key != key || cached.TargetGeneration != _targetGeneration) continue;
            if (index != _reference3DWorkspaceFrameCache.Count - 1)
            {
                _reference3DWorkspaceFrameCache.RemoveAt(index);
                _reference3DWorkspaceFrameCache.Add(cached);
            }
            return true;
        }

        return false;
    }

    private bool TryStoreReference3DWorkspaceFrameBitmap(
        StageControl stage,
        RenderStats stats,
        ID2D1RenderTarget destinationTarget,
        ID2D1Bitmap sourceBitmap)
    {
        var key = CreateReference3DWorkspaceFrameCacheKey(stage);
        for (var index = _reference3DWorkspaceFrameCache.Count - 1; index >= 0; index--)
        {
            var existing = _reference3DWorkspaceFrameCache[index];
            if (existing.Key != key) continue;
            _reference3DWorkspaceFrameCache.RemoveAt(index);
            _reference3DWorkspaceFrameCacheBytes -= existing.ByteSize;
            existing.Dispose();
        }

        var byteSize = (long)_targetSize.Width * _targetSize.Height * sizeof(int);
        if (byteSize <= 0 || byteSize > MaxReference3DWorkspaceFrameCacheBytes) return false;

        ID2D1Bitmap? bitmap = null;
        try
        {
            var properties = new BitmapProperties(
                new PixelFormat(Format.B8G8R8A8_UNorm, DCommonAlphaMode.Ignore),
                96,
                96);
            bitmap = destinationTarget.CreateBitmap(_targetSize, IntPtr.Zero, 0, properties);
            bitmap.CopyFromBitmap(sourceBitmap);
            var cached = new CachedReference3DWorkspaceFrame(
                key,
                _targetGeneration,
                stats,
                bitmap);
            bitmap = null;

            while (_reference3DWorkspaceFrameCache.Count >= MaxReference3DWorkspaceFrameCacheEntries
                || _reference3DWorkspaceFrameCache.Count > 0
                    && _reference3DWorkspaceFrameCacheBytes + byteSize
                        > MaxReference3DWorkspaceFrameCacheBytes)
            {
                var oldest = _reference3DWorkspaceFrameCache[0];
                _reference3DWorkspaceFrameCache.RemoveAt(0);
                _reference3DWorkspaceFrameCacheBytes -= oldest.ByteSize;
                oldest.Dispose();
                LastReference3DWorkspaceFrameCacheEvictions++;
            }

            _reference3DWorkspaceFrameCache.Add(cached);
            _reference3DWorkspaceFrameCacheBytes += byteSize;
            LastReference3DWorkspaceFrameCacheBuilds++;
            return true;
        }
        catch
        {
            bitmap?.Dispose();
            return false;
        }
    }

    private void ClearTargetBoundResourcesForSwitch()
    {
        ClearReference3DCpuRasterBitmap();
        ClearBrushCache();
        ClearMixingBrush();
        ClearMixingBrushBitmapCache();
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
        _shapeGradientMaskLayer?.Dispose();
        _shapeGradientMaskLayer = null;
    }
}
