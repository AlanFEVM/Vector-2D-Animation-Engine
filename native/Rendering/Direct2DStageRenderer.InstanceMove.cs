using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace VectorAnimationEngine;

internal sealed partial class Direct2DStageRenderer
{
    private ID2D1Bitmap1? _instanceMoveSelectionImage;
    private PointF _instanceMoveSelectionOrigin;
    private PointF _instanceMoveSelectionProjectionOrigin;
    private float _instanceMoveSelectionZoom;
    private object? _instanceMoveSelectionKey;

    private bool TryDrawInstanceMoveSelection(StageControl stage)
    {
        if (_target is not ID2D1DeviceContext context || stage.Reference3DSelectedObjects.Count == 0
            || !stage.NavigationPreviewEnabled || _referenceNavigationKey is null
            || !stage.CanRetainReferenceNavigation
            || !stage.TryProjectScenePosition(Vector3.Zero, out var projectionOrigin, out _)) return false;
        // Partitioning the same immutable scene for a drag does not change the
        // selection outline. Keep its prepared pixels through start/cancel.
        object key = (_referenceColorContentKey ?? _referenceNavigationKey, stage.Reference3DSelectedObjects);
        if (!Equals(key, _instanceMoveSelectionKey)
            || !stage.ReferenceNavigationPreviewActive && _instanceMoveSelectionZoom != stage.ReferenceZoomScale)
        {
            _instanceMoveSelectionImage?.Dispose();
            _instanceMoveSelectionImage = null;
            _instanceMoveSelectionKey = key;
        }
        if (_instanceMoveSelectionImage is null)
        {
            var bounds = RectangleF.Empty;
            foreach (var index in stage.Reference3DSelectedObjects)
                if (StageControl.TryGetReference3DProjectedBounds(stage.GetReference3DProjectedSolid(index).SelectionEdges,
                    8, out var objectBounds))
                    bounds = bounds.IsEmpty ? objectBounds : RectangleF.Union(bounds, objectBounds);
            var pixels = Rectangle.Ceiling(bounds);
            if (pixels.Width <= 0 || pixels.Height <= 0 || pixels.Width > context.MaximumBitmapSize
                || pixels.Height > context.MaximumBitmapSize || (long)pixels.Width * pixels.Height > 16_777_216) return false;
            using var previousTarget = context.Target;
            var previousTransform = context.Transform;
            context.EndDraw().CheckError();
            ID2D1Bitmap1? image = null;
            try
            {
                image = context.CreateBitmap(new SizeI(pixels.Width, pixels.Height), IntPtr.Zero, 0,
                    new BitmapProperties1(new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm,
                        Vortice.DCommon.AlphaMode.Premultiplied), 96, 96, BitmapOptions.Target));
                context.Target = image;
                context.Transform = Matrix3x2.CreateTranslation(-pixels.X, -pixels.Y);
                context.BeginDraw();
                try
                {
                    context.Clear(new Color4(0, 0, 0, 0));
                    DrawReference3DSelectionCore(stage);
                }
                finally { context.EndDraw().CheckError(); }
                _instanceMoveSelectionImage = image;
                image = null;
                _instanceMoveSelectionOrigin = pixels.Location;
                _instanceMoveSelectionProjectionOrigin = projectionOrigin;
                _instanceMoveSelectionZoom = stage.ReferenceZoomScale;
            }
            finally
            {
                image?.Dispose(); context.Target = previousTarget;
                context.Transform = previousTransform; context.BeginDraw();
            }
        }
        var transform = context.Transform;
        var offset = stage.InstanceMovePreviewOffset;
        var scale = stage.ReferenceZoomScale / _instanceMoveSelectionZoom;
        context.Transform = Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(
            projectionOrigin.X + (_instanceMoveSelectionOrigin.X - _instanceMoveSelectionProjectionOrigin.X) * scale + offset.X,
            projectionOrigin.Y + (_instanceMoveSelectionOrigin.Y - _instanceMoveSelectionProjectionOrigin.Y) * scale + offset.Y);
        try { context.DrawBitmap(_instanceMoveSelectionImage); }
        finally { context.Transform = transform; }
        return true;
    }
}
