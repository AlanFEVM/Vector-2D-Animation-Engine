using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace VectorAnimationEngine;

internal sealed partial class Direct2DStageRenderer
{
    private static ID2D1Bitmap1 CreateFilteredNavigationImage(ID2D1DeviceContext context,
        ID2D1CommandList commands, Rectangle bounds, SymbolFilters filters, float zoom)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0 || bounds.Width > context.MaximumBitmapSize
            || bounds.Height > context.MaximumBitmapSize || (long)bounds.Width * bounds.Height > 16_777_216)
            throw new InvalidOperationException("Filtered navigation group exceeds the image budget.");
        var format = new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm,
            Vortice.DCommon.AlphaMode.Premultiplied);
        var size = new SizeI(bounds.Width, bounds.Height);
        using var target = context.CreateBitmap(size, IntPtr.Zero, 0,
            new BitmapProperties1(format, 96, 96, BitmapOptions.Target));
        using var readback = context.CreateBitmap(size, IntPtr.Zero, 0,
            new BitmapProperties1(format, 96, 96, BitmapOptions.CpuRead | BitmapOptions.CannotDraw));
        using var previous = context.Target;
        var transform = context.Transform;
        try
        {
            context.Target = target;
            context.Transform = Matrix3x2.CreateTranslation(-bounds.X, -bounds.Y);
            context.BeginDraw();
            try { context.Clear(new Color4(0, 0, 0, 0)); context.DrawImage(commands); }
            finally { context.EndDraw().CheckError(); }
            context.Target = previous;
            readback.CopyFromBitmap(target).CheckError();
            using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppPArgb);
            var data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.WriteOnly,
                PixelFormat.Format32bppPArgb);
            try
            {
                var mapped = readback.Map(MapOptions.Read);
                try
                {
                    var row = new byte[checked(bounds.Width * 4)];
                    for (var y = 0; y < bounds.Height; y++)
                    {
                        Marshal.Copy(IntPtr.Add(mapped.Bits, checked(y * (int)mapped.Pitch)), row, 0, row.Length);
                        Marshal.Copy(row, 0, IntPtr.Add(data.Scan0, y * data.Stride), row.Length);
                    }
                }
                finally { readback.Unmap(); }
            }
            finally { bitmap.UnlockBits(data); }
            // Same implementation, kernels and alpha semantics as software
            // layer composition; no Gaussian substitute for Flash box filters.
            SymbolFilterRasterizer.Apply(bitmap, filters, zoom);
            data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly,
                PixelFormat.Format32bppPArgb);
            try { return context.CreateBitmap(size, data.Scan0, checked((uint)data.Stride),
                new BitmapProperties1(format, 96, 96, BitmapOptions.None)); }
            finally { bitmap.UnlockBits(data); }
        }
        finally { context.Target = previous; context.Transform = transform; }
    }
}
