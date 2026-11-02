using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace VectorAnimationEngine;

internal sealed partial class Direct2DStageRenderer
{
    private ID2D1Bitmap1? _navigationPreviewBitmap;
    private ID2D1Bitmap1? _navigationPreviewSpare;
    private PointF _navigationPreviewOrigin;
    private float _navigationPreviewZoom;
    private System.Drawing.Size _navigationPreviewSize;
    private bool _navigationPreviewContainsContent;
    internal bool NavigationPreviewContainsContent => _navigationPreviewContainsContent;
    internal long NavigationPreviewReuses { get; private set; }
    internal long NavigationPreviewBuilds { get; private set; }
    internal long NavigationPreviewScrolls { get; private set; }
    internal long NavigationPreviewPaintedPixels { get; private set; }

    private bool CanReuseReferenceNavigationPreviewImage(StageControl stage, PointF origin)
    {
        if (!stage.NavigationPreviewEnabled || !stage.ReferenceNavigationPreviewActive
            || _navigationPreviewBitmap is null || _navigationPreviewSize != stage.ClientSize) return false;
        var scale = stage.ReferenceZoomScale / _navigationPreviewZoom;
        if (scale < 0.5f || scale > 2f) return false;
        var size = stage.ClientSize;
        var x = origin.X - (_navigationPreviewOrigin.X + size.Width * 0.5f) * scale;
        var y = origin.Y - (_navigationPreviewOrigin.Y + size.Height * 0.5f) * scale;
        return _navigationPreviewContainsContent || x <= 0 && y <= 0
            && x + size.Width * 2 * scale >= size.Width && y + size.Height * 2 * scale >= size.Height;
    }

    internal void ClearReferenceNavigationPreview()
    {
        _navigationPreviewBitmap?.Dispose();
        _navigationPreviewBitmap = null;
        _navigationPreviewContainsContent = false;
        _navigationPreviewSpare?.Dispose();
        _navigationPreviewSpare = null;
    }

    private bool TryDrawReferenceNavigationPreview(StageControl stage, ID2D1DeviceContext context,
        PointF origin, Action<RectangleF> draw, Func<RectangleF?> getContentBounds)
    {
        if (!stage.NavigationPreviewEnabled)
        {
            ClearReferenceNavigationPreview();
            return false;
        }
        var size = stage.ClientSize;
        if (_navigationPreviewSize != size) ClearReferenceNavigationPreview();
        if ((long)size.Width * size.Height * 4 > 16_777_216
            || (long)size.Width * 2 > context.MaximumBitmapSize
            || (long)size.Height * 2 > context.MaximumBitmapSize)
        {
            ClearReferenceNavigationPreview();
            return false;
        }
        var margin = new Vector2(size.Width * 0.5f, size.Height * 0.5f);
        var scale = stage.ReferenceZoomScale / _navigationPreviewZoom;
        var offset = new Vector2(origin.X, origin.Y)
            - (new Vector2(_navigationPreviewOrigin.X, _navigationPreviewOrigin.Y) + margin) * scale;
        var covered = offset.X <= 0 && offset.Y <= 0
            && offset.X + size.Width * 2 * scale >= size.Width
            && offset.Y + size.Height * 2 * scale >= size.Height;
        // Retain the image through idle vector redraws. Do not rerasterize a
        // still useful image every time selection or overlays request a paint.
        if (_navigationPreviewBitmap is not null && (covered || _navigationPreviewContainsContent) && scale >= 0.5f && scale <= 2f)
        {
            if (!stage.ReferenceNavigationPreviewActive) return false;
            DrawPreview();
            NavigationPreviewReuses++;
            return true;
        }
        var delta = new Vector2(origin.X - _navigationPreviewOrigin.X, origin.Y - _navigationPreviewOrigin.Y);
        // Copy only whole pixels; keep the fractional camera displacement in
        // the final image transform instead of resampling the overlap repeatedly.
        var shift = new Vector2(MathF.Round(delta.X), MathF.Round(delta.Y));
        var scroll = _navigationPreviewBitmap is not null && stage.ReferenceZoomScale == _navigationPreviewZoom
            && Math.Abs(shift.X) < size.Width * 2 && Math.Abs(shift.Y) < size.Height * 2;
        var rasterOrigin = scroll
            ? new PointF(_navigationPreviewOrigin.X + shift.X, _navigationPreviewOrigin.Y + shift.Y) : origin;
        var rasterOffset = margin + new Vector2(rasterOrigin.X - origin.X, rasterOrigin.Y - origin.Y);

        var extent = new RectangleF(0, 0, size.Width * 2, size.Height * 2);
        var strips = new List<RectangleF>(4);
        if (scroll)
        {
            var overlap = RectangleF.Intersect(extent,
                new RectangleF(shift.X, shift.Y, extent.Width, extent.Height));
            AddStrip(new RectangleF(0, 0, extent.Width, overlap.Top));
            AddStrip(new RectangleF(0, overlap.Bottom, extent.Width, extent.Bottom - overlap.Bottom));
            AddStrip(new RectangleF(0, overlap.Top, overlap.Left, overlap.Height));
            AddStrip(new RectangleF(overlap.Right, overlap.Top, extent.Right - overlap.Right, overlap.Height));
        }
        else strips.Add(extent);

        // Seed an expanded image through a command list so SVGs and vector
        // geometry receive the same translation, independent of their local transforms.
        using var previousTarget = context.Target;
        var previousTransform = context.Transform;
        using var commands = context.CreateCommandList();
        ID2D1Bitmap1? bitmap = null;
        context.EndDraw().CheckError();
        try
        {
            context.Target = commands;
            context.Transform = Matrix3x2.Identity;
            context.BeginDraw();
            try
            {
                foreach (var strip in strips)
                {
                    var viewport = new RectangleF(strip.X - rasterOffset.X, strip.Y - rasterOffset.Y,
                        strip.Width, strip.Height);
                    context.Transform = Matrix3x2.Identity;
                    context.PushAxisAlignedClip(Rect(viewport.X, viewport.Y, viewport.Width, viewport.Height),
                        AntialiasMode.Aliased);
                    try { draw(viewport); }
                    finally { context.Transform = Matrix3x2.Identity; context.PopAxisAlignedClip(); }
                }
            }
            finally { context.EndDraw().CheckError(); }
            commands.Close().CheckError();
            bitmap = _navigationPreviewSpare;
            _navigationPreviewSpare = null;
            bitmap ??= context.CreateBitmap(new SizeI(size.Width * 2, size.Height * 2), IntPtr.Zero, 0,
                new BitmapProperties1(new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm,
                    Vortice.DCommon.AlphaMode.Premultiplied), 96, 96, BitmapOptions.Target));
            context.Target = bitmap;
            context.Transform = Matrix3x2.CreateTranslation(rasterOffset);
            context.BeginDraw();
            try
            {
                context.Clear(new Color4(0, 0, 0, 0));
                if (scroll)
                {
                    context.Transform = Matrix3x2.Identity;
                    context.DrawImage(_navigationPreviewBitmap, shift, null,
                        InterpolationMode.NearestNeighbor, CompositeMode.SourceOver);
                    NavigationPreviewScrolls++;
                }
                context.Transform = Matrix3x2.CreateTranslation(rasterOffset);
                context.DrawImage(commands);
                foreach (var strip in strips)
                    NavigationPreviewPaintedPixels += (long)strip.Width * (long)strip.Height;
            }
            finally { context.EndDraw().CheckError(); }
            _navigationPreviewSpare = _navigationPreviewBitmap;
            _navigationPreviewBitmap = bitmap;
            NavigationPreviewBuilds++;
            bitmap = null;
            _navigationPreviewOrigin = rasterOrigin;
            _navigationPreviewZoom = stage.ReferenceZoomScale;
            _navigationPreviewSize = size;
            var cachedViewport = new RectangleF(-rasterOffset.X + 2, -rasterOffset.Y + 2,
                size.Width * 2 - 4, size.Height * 2 - 4);
            _navigationPreviewContainsContent = getContentBounds() is { } content
                && cachedViewport.Contains(content);
        }
        finally
        {
            bitmap?.Dispose();
            context.Target = previousTarget;
            context.Transform = previousTransform;
            context.BeginDraw();
        }
        // The settled frame always uses native vector rendering. The image is
        // only consumed on subsequent navigation input, never as final artwork.
        if (!stage.ReferenceNavigationPreviewActive) return false;
        scale = 1;
        offset = new Vector2(origin.X - rasterOrigin.X, origin.Y - rasterOrigin.Y) - margin;
        DrawPreview();
        return true;

        void DrawPreview()
        {
            var transform = context.Transform;
            context.Transform = Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(offset);
            try { context.DrawBitmap(_navigationPreviewBitmap!, 1, BitmapInterpolationMode.Linear); }
            finally { context.Transform = transform; }
        }

        void AddStrip(RectangleF strip)
        {
            if (strip.Width > 0 && strip.Height > 0) strips.Add(strip);
        }
    }
}
