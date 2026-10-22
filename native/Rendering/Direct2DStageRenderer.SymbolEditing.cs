using System.Drawing.Imaging;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace VectorAnimationEngine;

internal sealed partial class Direct2DStageRenderer
{
    private ID2D1Bitmap? _symbolEditingReference;
    private Bitmap? _symbolEditingReferenceSource;

    internal void ClearSymbolEditingReference()
    {
        _symbolEditingReference?.Dispose();
        _symbolEditingReference = null;
        _symbolEditingReferenceSource = null;
        ClearBaseFrameCache();
    }

    private void DrawSymbolEditingReference(StageControl stage)
    {
        if (_target is null || stage.SymbolEditingReference is not { } image) return;
        if (!ReferenceEquals(_symbolEditingReferenceSource, image))
        {
            ClearSymbolEditingReference();
            var data = image.LockBits(new Rectangle(0, 0, image.Width, image.Height),
                ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            try
            {
                _symbolEditingReference = _target.CreateBitmap(new SizeI(image.Width, image.Height),
                    data.Scan0, checked((uint)data.Stride), new BitmapProperties(
                        new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm,
                            Vortice.DCommon.AlphaMode.Premultiplied), 96, 96));
                _symbolEditingReferenceSource = image;
            }
            finally { image.UnlockBits(data); }
        }
        var previous = _target.Transform;
        try
        {
            _target.Clear(new Color4(1, 1, 1, 1));
            _target.Transform = stage.SymbolReferenceToScreen();
            _target.DrawBitmap(_symbolEditingReference!, 75f / 255f, BitmapInterpolationMode.Linear);
        }
        finally { _target.Transform = previous; }
    }
}
