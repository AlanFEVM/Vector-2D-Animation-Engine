using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace VectorAnimationEngine;

internal static class ImageDisplayDetector
{
    internal const int MaximumAutomaticPixelArtDimension = 256;

    internal static ImageFilterMode Detect(string path, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length <= 0 || stream.Length > BitmapImageFormats.MaximumSourceBytes)
            return ImageFilterMode.Bilinear;
        using var source = Image.FromStream(stream, true, true);
        if (!BitmapImageFormats.TryValidateDecodedBudget(source.Width, source.Height, out _))
            return ImageFilterMode.Bilinear;
        return Detect(source, token);
    }

    internal static ImageFilterMode Detect(Image source, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (Math.Max(source.Width, source.Height) > MaximumAutomaticPixelArtDimension)
            return ImageFilterMode.Bilinear;
        var scale = Math.Min(1f, 64f / Math.Max(source.Width, source.Height));
        using var sample = new Bitmap(Math.Max(1, (int)(source.Width * scale)),
            Math.Max(1, (int)(source.Height * scale)), PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(sample))
        {
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.DrawImage(source, new Rectangle(Point.Empty, sample.Size),
                0, 0, source.Width, source.Height, GraphicsUnit.Pixel);
        }
        return HasPixelPalette(sample, token) ? ImageFilterMode.Point : ImageFilterMode.Bilinear;
    }

    private static bool HasPixelPalette(Bitmap sample, CancellationToken token)
    {
        var palette = new HashSet<int>();
        var visible = 0;
        var translucent = 0;
        var transitions = 0;
        var rampTransitions = 0;
        for (var row = 0; row < sample.Height; row++)
        {
            token.ThrowIfCancellationRequested();
            for (var column = 0; column < sample.Width; column++)
            {
                var color = sample.GetPixel(column, row);
                if (color.A == 0) continue;
                visible++;
                if (color.A != 255) translucent++;
                palette.Add(color.ToArgb());
                if (palette.Count > 128) return false;
                if (column > 0) Compare(color, sample.GetPixel(column - 1, row),
                    column > 1 ? sample.GetPixel(column - 2, row) : null);
                if (row > 0) Compare(color, sample.GetPixel(column, row - 1),
                    row > 1 ? sample.GetPixel(column, row - 2) : null);
            }
        }
        return visible >= 16 && (palette.Count >= 2 || visible < sample.Width * sample.Height) && transitions >= 4
            && (palette.Count <= 16 || palette.Count * 2 <= visible)
            && translucent * 100 <= visible * 2 && rampTransitions * 4 <= transitions;

        void Compare(Color current, Color previous, Color? earlier)
        {
            if (current == previous) return;
            transitions++;
            if (earlier is not { A: 255 } start || previous.A != 255 || current.A != 255) return;
            var redStep = current.R - previous.R;
            var greenStep = current.G - previous.G;
            var blueStep = current.B - previous.B;
            var previousRedStep = previous.R - start.R;
            var previousGreenStep = previous.G - start.G;
            var previousBlueStep = previous.B - start.B;
            var previousMagnitude = Math.Max(Math.Abs(previousRedStep), Math.Max(Math.Abs(previousGreenStep), Math.Abs(previousBlueStep)));
            if (previousMagnitude == 0 || redStep * previousRedStep + greenStep * previousGreenStep + blueStep * previousBlueStep <= 0) return;
            var magnitude = Math.Max(Math.Abs(redStep), Math.Max(Math.Abs(greenStep), Math.Abs(blueStep)));
            var stepDifference = Math.Max(Math.Abs(redStep - previousRedStep),
                Math.Max(Math.Abs(greenStep - previousGreenStep), Math.Abs(blueStep - previousBlueStep)));
            if (stepDifference <= Math.Max(2, Math.Max(magnitude, previousMagnitude) / 4)) rampTransitions++;
        }
    }
}
