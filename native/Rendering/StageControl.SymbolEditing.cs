using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Numerics;

namespace VectorAnimationEngine;

internal sealed partial class StageControl
{
    // Owned by the editor session. The reference is frozen at entry, never project geometry.
    internal Bitmap? SymbolEditingReference { get; private set; }
    private Matrix3x2 _symbolReferencePixelsToWorld;

    internal void SetSymbolEditingReference(Bitmap? image, Matrix3x2 pixelsToWorld = default)
    {
        SymbolEditingReference = image;
        _symbolReferencePixelsToWorld = pixelsToWorld;
        _direct2DRenderer.ClearSymbolEditingReference();
        ClearGdiBaseFrameCache();
        Invalidate();
    }

    internal Matrix3x2 SymbolReferenceToScreen()
    {
        var origin = WorldToScreen(0, 0);
        var scale = VectorUnits.ToPixels(1) * Zoom;
        return _symbolReferencePixelsToWorld * new Matrix3x2(scale, 0, 0, scale, origin.X, origin.Y);
    }

    private void DrawSymbolEditingReferenceGdi(Graphics graphics)
    {
        if (SymbolEditingReference is not { } image) return;
        graphics.Clear(Color.White);
        var state = graphics.Save();
        try
        {
            var transform = SymbolReferenceToScreen();
            using var matrix = new Matrix(transform.M11, transform.M12, transform.M21,
                transform.M22, transform.M31, transform.M32);
            graphics.Transform = matrix;
            graphics.DrawImage(image, new Rectangle(0, 0, image.Width, image.Height));
        }
        finally { graphics.Restore(state); }
        using var veil = new SolidBrush(Color.FromArgb(180, Color.White));
        graphics.FillRectangle(veil, ClientRectangle);
    }

    internal Bitmap CaptureSymbolEditingReference(string rootInstanceId)
    {
        var source = Scene;
        var resultScene = _sceneCompositionResultScene;
        var stats = LastStats;
        var isolated = new VectorScene();
        isolated.RestoreSnapshot(source.CreateSnapshot());
        // Keep all object indices and mask provenance aligned; hide only the chosen placement.
        for (var index = 0; index < isolated.ObjectCount; index++)
        {
            if (_sceneCompositionResult?.TryGetOwner(index, out var owner) == true
                && (owner.RootInstanceId == rootInstanceId || owner.InstanceId == rootInstanceId))
                isolated.LayerVisible[isolated.ObjectLayer[index]] = false;
        }
        var image = new Bitmap(Math.Max(1, Width), Math.Max(1, Height), PixelFormat.Format32bppPArgb);
        try
        {
            Scene = isolated;
            _sceneCompositionResultScene = isolated;
            using var graphics = Graphics.FromImage(image);
            if (RendersReferenceProjection) DrawGdiReferenceBase(graphics);
            else DrawGdiBase2D(graphics);
            return image;
        }
        catch { image.Dispose(); throw; }
        finally
        {
            Scene = source;
            _sceneCompositionResultScene = resultScene;
            LastStats = stats;
        }
    }
}
