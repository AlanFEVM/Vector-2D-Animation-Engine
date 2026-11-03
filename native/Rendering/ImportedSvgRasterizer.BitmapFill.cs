using System.Buffers.Binary;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Svg;

namespace VectorAnimationEngine;

internal static partial class ImportedSvgRasterizer
{
    internal const string BitmapSamplingAttribute = "data-v2d-bitmap-sampling";

    private static void PrepareBitmapPaintServers(SvgDocument document, byte[] source)
    {
        if (source.AsSpan().IndexOf("data-v2d-bitmap-sampling"u8) < 0) return;
        using var stream = new MemoryStream(source, writable: false);
        var xml = XDocument.Load(stream);
        long totalPixels = 0;
        foreach (var pattern in xml.Descendants().Where(node => node.Name.LocalName == "pattern"
                     && node.Attribute(BitmapSamplingAttribute) is not null))
        {
            var mode = pattern.Attribute(BitmapSamplingAttribute)!.Value;
            if (mode is not ("point-tile" or "bilinear-tile" or "point-clamp" or "bilinear-clamp"))
                throw new InvalidDataException("Unsupported embedded bitmap sampling mode.");
            var id = pattern.Attribute("id")?.Value ?? throw new InvalidDataException("A bitmap pattern needs an ID.");
            if (document.GetElementById(id) is not SvgPatternServer original || original.Parent is null)
                throw new InvalidDataException("Missing embedded bitmap pattern.");
            var image = pattern.Elements().SingleOrDefault();
            var href = image?.Attributes().SingleOrDefault(attribute => attribute.Name.LocalName == "href")?.Value;
            const string prefix = "data:image/png;base64,";
            if (image?.Name.LocalName != "image" || href is null || !href.StartsWith(prefix, StringComparison.Ordinal))
                throw new InvalidDataException("A sampled bitmap pattern requires one embedded PNG.");
            var bytes = Convert.FromBase64String(href[prefix.Length..]);
            if (bytes.Length < 33 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                throw new InvalidDataException("Invalid sampled bitmap PNG.");
            var width = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4));
            var height = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4));
            if (!BitmapImageFormats.TryValidateDecodedBudget(width, height, out var error)) throw new InvalidDataException(error);
            if ((string?)pattern.Attribute("patternUnits") != "userSpaceOnUse"
                || (int?)pattern.Attribute("width") != width || (int?)pattern.Attribute("height") != height
                || (int?)image.Attribute("width") != width || (int?)image.Attribute("height") != height)
                throw new InvalidDataException("Sampled bitmap pattern dimensions or coordinate units differ from its PNG.");
            totalPixels += (long)width * height;
            if (totalPixels > BitmapImageFormats.MaximumDecodedPixels) throw new InvalidDataException("Embedded bitmap patterns exceed the decoded pixel budget.");
            var replacement = (BitmapSamplePaintServer)original.DeepCopy<BitmapSamplePaintServer>();
            replacement.Initialize(bytes, width, height, mode.StartsWith("bilinear", StringComparison.Ordinal), mode.EndsWith("clamp", StringComparison.Ordinal));
            var parent = original.Parent;
            var index = parent.Children.IndexOf(original);
            parent.Children.RemoveAt(index);
            if (index == parent.Children.Count) parent.Children.Add(replacement);
            else parent.Children.Insert(index, replacement);
        }
        InstallSampledPaths(document);
    }

    private static void InstallSampledPaths(SvgElement parent)
    {
        foreach (var child in parent.Children.ToArray())
        {
            if (child is SvgPath path && (SvgDeferredPaintServer.TryGet<BitmapSamplePaintServer>(path.Fill, path) is not null
                || SvgDeferredPaintServer.TryGet<BitmapSamplePaintServer>(path.Stroke, path) is not null))
            {
                var replacement = path.DeepCopy<SampledBitmapPath>();
                var index = parent.Children.IndexOf(child);
                parent.Children.RemoveAt(index);
                if (index == parent.Children.Count) parent.Children.Add(replacement);
                else parent.Children.Insert(index, replacement);
            }
            else InstallSampledPaths(child);
        }
    }

    private sealed class SampledBitmapPath : SvgPath
    {
        public SampledBitmapPath() { }

        public override SvgElement DeepCopy() => DeepCopy<SampledBitmapPath>();

        protected override void RenderFill(ISvgRenderer renderer)
        {
            var graphics = (renderer as IGraphicsProvider)?.GetGraphics();
            var quality = graphics?.CompositingQuality;
            try
            {
                // SVG.NET's HighQuality blend rounds a half-alpha texture down
                // to 127 even when its stored alpha is 128. Preserve sampled bytes.
                if (graphics is not null && SvgDeferredPaintServer.TryGet<BitmapSamplePaintServer>(Fill, this) is not null)
                    graphics.CompositingQuality = CompositingQuality.AssumeLinear;
                base.RenderFill(renderer);
            }
            finally { if (graphics is not null && quality.HasValue) graphics.CompositingQuality = quality.Value; }
        }

        protected override bool RenderStroke(ISvgRenderer renderer)
        {
            var graphics = (renderer as IGraphicsProvider)?.GetGraphics();
            var quality = graphics?.CompositingQuality;
            try
            {
                if (graphics is not null && SvgDeferredPaintServer.TryGet<BitmapSamplePaintServer>(Stroke, this) is not null)
                    graphics.CompositingQuality = CompositingQuality.AssumeLinear;
                return base.RenderStroke(renderer);
            }
            finally { if (graphics is not null && quality.HasValue) graphics.CompositingQuality = quality.Value; }
        }
    }

    // GDI+ TextureBrush always filters scaled textures, even with NearestNeighbor.
    // Sample in device pixels first, then let SVG.NET apply the original vector
    // coverage, clips and stroke geometry using a one-device-pixel texture.
    private sealed class BitmapSamplePaintServer : SvgPatternServer
    {
        private byte[] _pixels = [];
        private int _width;
        private int _height;
        private bool _bilinear;
        private bool _clamp;

        public BitmapSamplePaintServer() { }

        public override SvgElement DeepCopy() => DeepCopy<BitmapSamplePaintServer>();

        public override SvgElement DeepCopy<T>()
        {
            var copy = base.DeepCopy<T>();
            if (copy is BitmapSamplePaintServer sampled)
            {
                // Decoded pixels are immutable after initialization; document clones
                // share these bytes without re-decoding or losing the sampling mode.
                sampled._pixels = _pixels;
                sampled._width = _width;
                sampled._height = _height;
                sampled._bilinear = _bilinear;
                sampled._clamp = _clamp;
            }
            return copy;
        }

        internal void Initialize(byte[] bytes, int width, int height, bool bilinear, bool clamp)
        {
            using var input = new MemoryStream(bytes, writable: false);
            using var decoded = new Bitmap(input);
            if (decoded.Width != width || decoded.Height != height) throw new InvalidDataException("Sampled bitmap dimensions differ.");
            _width = width; _height = height; _bilinear = bilinear; _clamp = clamp;
            _pixels = new byte[checked(width * height * 4)];
            var data = decoded.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (var y = 0; y < height; y++) Marshal.Copy(data.Scan0 + y * data.Stride, _pixels, y * width * 4, width * 4);
            }
            finally { decoded.UnlockBits(data); }
            for (var i = 0; i < _pixels.Length; i += 4)
                for (var channel = 0; channel < 3; channel++)
                    _pixels[i + channel] = (byte)((_pixels[i + channel] * _pixels[i + 3] + 127) / 255);
        }

        public override Brush GetBrush(SvgVisualElement element, ISvgRenderer renderer, float opacity, bool forStroke = false)
        {
            if (renderer is not IGraphicsProvider provider) throw new InvalidDataException("Bitmap sampling needs a graphics renderer.");
            var graphics = provider.GetGraphics();
            using var world = graphics.Transform;
            using var inverseWorld = world.Clone();
            if (!inverseWorld.IsInvertible) throw new InvalidDataException("Singular sampled bitmap transform.");
            inverseWorld.Invert();
            using var textureToDevice = PatternTransform?.GetMatrix() ?? new Matrix();
            textureToDevice.Multiply(world, MatrixOrder.Append);
            if (!textureToDevice.IsInvertible) throw new InvalidDataException("Singular bitmap fill matrix.");
            textureToDevice.Invert();
            var inverse = textureToDevice.Elements;
            if (inverse.Any(value => !float.IsFinite(value))) throw new InvalidDataException("Invalid bitmap fill matrix.");
            var clip = graphics.VisibleClipBounds;
            PointF[] corners = [new(clip.Left, clip.Top), new(clip.Right, clip.Top), new(clip.Left, clip.Bottom), new(clip.Right, clip.Bottom)];
            world.TransformPoints(corners);
            var region = Rectangle.FromLTRB((int)Math.Floor(corners.Min(p => p.X)), (int)Math.Floor(corners.Min(p => p.Y)),
                (int)Math.Ceiling(corners.Max(p => p.X)), (int)Math.Ceiling(corners.Max(p => p.Y)));
            if (region.Width <= 0 || region.Height <= 0) return new SolidBrush(System.Drawing.Color.Transparent);
            if (region.Width > MaxRasterDimension || region.Height > MaxRasterDimension || (long)region.Width * region.Height > MaxRasterPixels)
                throw new InvalidDataException("Sampled bitmap fill exceeds the raster budget.");
            var output = new byte[checked(region.Width * region.Height * 4)];
            for (var y = 0; y < region.Height; y++)
                for (var x = 0; x < region.Width; x++)
                {
                    var dx = region.X + x + 0.5; var dy = region.Y + y + 0.5;
                    var u = dx * inverse[0] + dy * inverse[2] + inverse[4];
                    var v = dx * inverse[1] + dy * inverse[3] + inverse[5];
                    var offset = (y * region.Width + x) * 4;
                    if (!_bilinear)
                    {
                        var sourceOffset = PixelOffset(Math.Floor(u), Math.Floor(v));
                        for (var c = 0; c < 4; c++) output[offset + c] = Scale(_pixels[sourceOffset + c], opacity);
                    }
                    else
                    {
                        u -= 0.5; v -= 0.5;
                        var left = Math.Floor(u); var top = Math.Floor(v); var fx = u - left; var fy = v - top;
                        var a = PixelOffset(left, top); var b = PixelOffset(left + 1, top);
                        var c = PixelOffset(left, top + 1); var d = PixelOffset(left + 1, top + 1);
                        for (var channel = 0; channel < 4; channel++)
                        {
                            var upper = _pixels[a + channel] * (1 - fx) + _pixels[b + channel] * fx;
                            var lower = _pixels[c + channel] * (1 - fx) + _pixels[d + channel] * fx;
                            output[offset + channel] = Scale(upper * (1 - fy) + lower * fy, opacity);
                        }
                    }
                }
            using var bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppPArgb);
            var locked = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try
            {
                for (var y = 0; y < region.Height; y++) Marshal.Copy(output, y * region.Width * 4, locked.Scan0 + y * locked.Stride, region.Width * 4);
            }
            finally { bitmap.UnlockBits(locked); }
            var brush = new TextureBrush(bitmap, WrapMode.Clamp);
            using var placement = new Matrix(1, 0, 0, 1, region.X, region.Y);
            placement.Multiply(inverseWorld, MatrixOrder.Append);
            brush.Transform = placement;
            return brush;
        }

        private int PixelOffset(double x, double y)
        {
            var sx = _clamp ? (int)Math.Clamp(x, 0, _width - 1) : (int)((x % _width + _width) % _width);
            var sy = _clamp ? (int)Math.Clamp(y, 0, _height - 1) : (int)((y % _height + _height) % _height);
            return (sy * _width + sx) * 4;
        }

        private static byte Scale(double value, float opacity) => (byte)Math.Clamp((int)Math.Round(value * Math.Clamp(opacity, 0, 1)), 0, 255);
    }
}
