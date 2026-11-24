using System.Numerics;
using Vortice.Direct2D1;
using Vortice.DXGI;

namespace VectorAnimationEngine;

internal sealed partial class Direct2DStageRenderer
{
    private ID2D1BitmapRenderTarget? _bitmapOriginTarget;
    private ID2D1Bitmap? _bitmapOriginImage;
    private long _bitmapOriginBytes;
    private BitmapOriginKey _bitmapOriginKey;
    private readonly List<(string Asset, BitmapImageRasterKey Key, BitmapSampling Sampling)> _bitmapOriginSources = [];
    private readonly record struct BitmapOriginKey(StageControl Stage, VectorScene Scene, long Session,
        StageViewState View, Size Size, long Generation, int Frame);
    internal long BitmapOriginBuilds { get; private set; }
    internal long BitmapOriginReuses { get; private set; }

    // The frozen origin already forms one opacity group. Retain its exact
    // screen pixels rather than decoding and uploading it again on every move.
    private bool TryDrawCachedBitmapOrigins(StageControl stage, VectorScene scene, IReadOnlyList<int> objects)
    {
        if (Environment.GetEnvironmentVariable("VECTOR_DISABLE_BITMAP_ORIGIN_CACHE") == "1"
            || _target is null || _target.Transform != Matrix3x2.Identity
            || (long)_targetSize.Width * _targetSize.Height * 4 > 32L * 1024 * 1024)
            return false;
        var key = new BitmapOriginKey(stage, scene, stage.BitmapMoveSelectionRevision,
            stage.CaptureViewState(), new Size(_targetSize.Width, _targetSize.Height), _targetGeneration, stage.Frame);
        var valid = _bitmapOriginImage is not null && key == _bitmapOriginKey;
        if (valid)
            foreach (var (asset, sourceKey, sampling) in _bitmapOriginSources)
                if (!stage.TryGetBitmapImageKey(asset, out var current) || current != sourceKey
                    || stage.BitmapImageSampling(asset) != sampling) { valid = false; break; }
        if (!valid)
        {
            ClearBitmapOrigins();
            _bitmapOriginBytes = (long)_targetSize.Width * _targetSize.Height * 4;
            EvictBitmapTextures(0);
            var parent = _target;
            var target = parent.CreateCompatibleRenderTarget(_targetSize, _targetSize,
                new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                CompatibleRenderTargetOptions.None);
            var drawing = false;
            try
            {
                _target = target;
                target.BeginDraw(); drawing = true;
                target.Transform = Matrix3x2.Identity;
                target.AntialiasMode = AntialiasMode.PerPrimitive;
                var transparent = new Vortice.Mathematics.Color(0f, 0f, 0f, 0f);
                target.Clear(in transparent);
                var rendered = TryDrawAffineBitmapLayer(stage, scene, objects, 0);
                var result = target.EndDraw(); drawing = false;
                if (!rendered || result.Failure) return false;
                foreach (var (index, frame, _) in _affineBitmapLayer)
                {
                    // A covered/outside object cannot affect this frozen view.
                    // Its occluders are validated below, so revealing it forces
                    // a rebuild before the cached image can be reused.
                    if (_coveredBitmaps.Contains(index)
                        || !frame.Bounds.IntersectsWith(new RectangleF(0, 0, stage.Width, stage.Height))) continue;
                    if (!scene.TryGetBitmapObjectData(index, out var data)
                        || !stage.TryGetBitmapImageKey(data.ImageAssetId, out var sourceKey)) return false;
                    _bitmapOriginSources.Add((data.ImageAssetId, sourceKey, stage.BitmapImageSampling(data.ImageAssetId)));
                }
                _bitmapOriginImage = target.Bitmap;
                _bitmapOriginTarget = target;
                _bitmapOriginKey = key;
                BitmapOriginBuilds++;
            }
            finally
            {
                if (drawing) target.EndDraw();
                _target = parent;
                if (!ReferenceEquals(_bitmapOriginTarget, target)) { target.Dispose(); ClearBitmapOrigins(); }
            }
        }
        else BitmapOriginReuses++;
        _target.DrawBitmap(_bitmapOriginImage!, Rect(0, 0, _targetSize.Width, _targetSize.Height), 1f,
            BitmapInterpolationMode.NearestNeighbor, Rect(0, 0, _targetSize.Width, _targetSize.Height));
        return true;
    }

    private void ClearBitmapOrigins()
    {
        _bitmapOriginImage?.Dispose(); _bitmapOriginImage = null;
        _bitmapOriginTarget?.Dispose(); _bitmapOriginTarget = null;
        _bitmapOriginSources.Clear(); _bitmapOriginKey = default;
        _bitmapOriginBytes = 0;
    }
}
