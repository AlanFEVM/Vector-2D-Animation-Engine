using System.Drawing.Imaging;

namespace VectorAnimationEngine;

internal sealed partial class StageControl
{
    private bool _gdiBaseFrameBorrowed;

    internal (Bitmap Bitmap, RenderStats Stats) PrepareWorkspaceBaseBitmap()
    {
        PlaybackActive = false;
        Reference3DPlaybackActive = false;
        var bitmap = new Bitmap(ClientSize.Width, ClientSize.Height, PixelFormat.Format32bppPArgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            var stats = RendersReferenceProjection ? DrawGdiReferenceBase(graphics) : DrawGdiBase2D(graphics);
            return (bitmap, stats);
        }
        catch { bitmap.Dispose(); throw; }
    }

    internal void UsePreparedWorkspaceBaseBitmap(Bitmap bitmap, RenderStats stats)
    {
        if (bitmap.Size != ClientSize) throw new ArgumentException("Prepared frame size no longer matches the viewport.", nameof(bitmap));
        ClearGdiBaseFrameCache();
        _gdiBaseFrameBitmap = bitmap;
        _gdiBaseFrameBorrowed = true;
        _gdiBaseFrameSize = bitmap.Size;
        StoreGdiBaseFrame(stats);
        _gdiBaseFrameStats = stats;
    }

    internal void ReleasePreparedWorkspaceBaseBitmap()
    {
        if (_gdiBaseFrameBorrowed) ClearGdiBaseFrameCache();
    }
}
