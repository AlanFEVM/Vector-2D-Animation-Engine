using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace VectorAnimationEngine;

internal sealed class LayerBlendCompositor : IDisposable
{
    private readonly int _width;
    private readonly int _height;
    private readonly Stack<Bitmap> _availableSurfaces = new();
    private readonly List<Bitmap> _surfaces = [];
    private readonly int[] _destinationPixels;
    private readonly int[] _sourcePixels;

    public LayerBlendCompositor(Size size)
    {
        _width = Math.Max(1, size.Width);
        _height = Math.Max(1, size.Height);
        _destinationPixels = new int[_width * _height];
        _sourcePixels = new int[_width * _height];
    }

    public Size Size => new(_width, _height);

    public void CompositeTo(Graphics destination, VectorScene scene, Action<Graphics, int> drawLayer)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(drawLayer);

        var root = RentSurface();
        try
        {
            var children = BuildLayerTree(scene);
            CompositeChildren(scene, children, scene.LayerCount, root, drawLayer);
            destination.DrawImageUnscaled(root, 0, 0);
        }
        finally
        {
            ReturnSurface(root);
        }
    }

    public void CompositeBatchesTo(
        Graphics destination,
        VectorScene scene,
        Func<int, bool> shouldDrawLayer,
        Action<Graphics, IReadOnlyList<int>> drawLayers)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(shouldDrawLayer);
        ArgumentNullException.ThrowIfNull(drawLayers);

        var root = RentSurface();
        try
        {
            var children = BuildLayerTree(scene);
            var pendingLayers = new List<int>();
            AppendChildrenBatched(
                scene,
                children,
                scene.LayerCount,
                root,
                pendingLayers,
                shouldDrawLayer,
                drawLayers);
            FlushLayerBatch(root, pendingLayers, drawLayers, $"root:{scene.LayerCount}");
            destination.DrawImageUnscaled(root, 0, 0);
        }
        finally
        {
            ReturnSurface(root);
        }
    }

    internal static int[][] GetSpatialLayerBatches(
        VectorScene scene,
        Func<int, bool> shouldDrawLayer)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(shouldDrawLayer);
        var children = BuildLayerTree(scene);
        var result = new List<int[]>();
        var pendingLayers = new List<int>();
        AppendSpatialLayerBatches(
            scene,
            children,
            scene.LayerCount,
            pendingLayers,
            result,
            shouldDrawLayer);
        FlushSpatialLayerBatch(pendingLayers, result);
        return result.ToArray();
    }

    private void CompositeChildren(
        VectorScene scene,
        IReadOnlyList<int>[] children,
        int parent,
        Bitmap destination,
        Action<Graphics, int> drawLayer)
    {
        var siblings = children[parent];
        for (var siblingIndex = siblings.Count - 1; siblingIndex >= 0; siblingIndex--)
        {
            var layer = siblings[siblingIndex];
            if ((uint)layer >= scene.LayerCount
                || layer >= scene.LayerVisible.Length
                || !scene.LayerVisible[layer])
            {
                continue;
            }

            var source = RentSurface();
            try
            {
                if (scene.GetLayerKind(layer) == DrawingLayerKind.Folder)
                {
                    CompositeChildren(scene, children, layer, source, drawLayer);
                }
                else
                {
                    using var graphics = Graphics.FromImage(source);
                    ConfigureLayerGraphics(graphics);
                    drawLayer(graphics, layer);
                }

                CompositeSurface(
                    destination,
                    source,
                    BlendModeFor(scene, layer),
                    OpacityFor(scene, layer),
                    StableSeed(DissolveKey(scene, layer)));
            }
            finally
            {
                ReturnSurface(source);
            }
        }
    }

    private void AppendChildrenBatched(
        VectorScene scene,
        IReadOnlyList<int>[] children,
        int parent,
        Bitmap destination,
        List<int> pendingLayers,
        Func<int, bool> shouldDrawLayer,
        Action<Graphics, IReadOnlyList<int>> drawLayers)
    {
        var siblings = children[parent];
        for (var siblingIndex = siblings.Count - 1; siblingIndex >= 0; siblingIndex--)
        {
            var layer = siblings[siblingIndex];
            if (!LayerIsVisible(scene, layer)) continue;
            var isFolder = scene.GetLayerKind(layer) == DrawingLayerKind.Folder;
            if (isFolder && BlendModeFor(scene, layer) == LayerBlendMode.Normal)
            {
                AppendChildrenBatched(
                    scene,
                    children,
                    layer,
                    destination,
                    pendingLayers,
                    shouldDrawLayer,
                    drawLayers);
                continue;
            }

            if (isFolder)
            {
                if (!SubtreeHasDrawableLayer(scene, children, layer, shouldDrawLayer)) continue;
                FlushLayerBatch(destination, pendingLayers, drawLayers, $"before:{layer}");
                var folderSurface = RentSurface();
                try
                {
                    var isolatedPendingLayers = new List<int>();
                    AppendChildrenBatched(
                        scene,
                        children,
                        layer,
                        folderSurface,
                        isolatedPendingLayers,
                        shouldDrawLayer,
                        drawLayers);
                    FlushLayerBatch(
                        folderSurface,
                        isolatedPendingLayers,
                        drawLayers,
                        $"folder:{layer}");
                    CompositeSurface(
                        destination,
                        folderSurface,
                        BlendModeFor(scene, layer),
                        OpacityFor(scene, layer),
                        StableSeed(DissolveKey(scene, layer)));
                }
                finally
                {
                    ReturnSurface(folderSurface);
                }
                continue;
            }

            if (!shouldDrawLayer(layer)) continue;
            if (BlendModeFor(scene, layer) == LayerBlendMode.Normal)
            {
                pendingLayers.Add(layer);
                continue;
            }

            FlushLayerBatch(destination, pendingLayers, drawLayers, $"before:{layer}");
            var layerSurface = RentSurface();
            try
            {
                using var graphics = Graphics.FromImage(layerSurface);
                ConfigureLayerGraphics(graphics);
                drawLayers(graphics, [layer]);

                CompositeSurface(
                    destination,
                    layerSurface,
                    BlendModeFor(scene, layer),
                    OpacityFor(scene, layer),
                    StableSeed(DissolveKey(scene, layer)));
            }
            finally
            {
                ReturnSurface(layerSurface);
            }
        }
    }

    private void FlushLayerBatch(
        Bitmap destination,
        List<int> pendingLayers,
        Action<Graphics, IReadOnlyList<int>> drawLayers,
        string stableKey)
    {
        if (pendingLayers.Count == 0) return;
        var source = RentSurface();
        try
        {
            using var graphics = Graphics.FromImage(source);
            ConfigureLayerGraphics(graphics);
            drawLayers(graphics, pendingLayers);
            CompositeSurface(
                destination,
                source,
                LayerBlendMode.Normal,
                1f,
                StableSeed(stableKey));
        }
        finally
        {
            ReturnSurface(source);
            pendingLayers.Clear();
        }
    }

    private static bool SubtreeHasDrawableLayer(
        VectorScene scene,
        IReadOnlyList<int>[] children,
        int layer,
        Func<int, bool> shouldDrawLayer)
    {
        if (!LayerIsVisible(scene, layer)) return false;
        if (scene.GetLayerKind(layer) != DrawingLayerKind.Folder) return shouldDrawLayer(layer);
        foreach (var descendant in children[layer])
        {
            if (SubtreeHasDrawableLayer(scene, children, descendant, shouldDrawLayer)) return true;
        }
        return false;
    }

    private static void AppendSpatialLayerBatches(
        VectorScene scene,
        IReadOnlyList<int>[] children,
        int parent,
        List<int> pendingLayers,
        ICollection<int[]> destination,
        Func<int, bool> shouldDrawLayer)
    {
        var siblings = children[parent];
        for (var siblingIndex = siblings.Count - 1; siblingIndex >= 0; siblingIndex--)
        {
            var layer = siblings[siblingIndex];
            if (!LayerIsVisible(scene, layer)) continue;
            var isFolder = scene.GetLayerKind(layer) == DrawingLayerKind.Folder;
            if (isFolder && BlendModeFor(scene, layer) == LayerBlendMode.Normal)
            {
                AppendSpatialLayerBatches(
                    scene,
                    children,
                    layer,
                    pendingLayers,
                    destination,
                    shouldDrawLayer);
                continue;
            }

            if (isFolder)
            {
                if (!SubtreeHasDrawableLayer(scene, children, layer, shouldDrawLayer)) continue;
                FlushSpatialLayerBatch(pendingLayers, destination);
                var isolatedPendingLayers = new List<int>();
                AppendSpatialLayerBatches(
                    scene,
                    children,
                    layer,
                    isolatedPendingLayers,
                    destination,
                    shouldDrawLayer);
                FlushSpatialLayerBatch(isolatedPendingLayers, destination);
                continue;
            }

            if (!shouldDrawLayer(layer)) continue;
            if (BlendModeFor(scene, layer) == LayerBlendMode.Normal)
            {
                pendingLayers.Add(layer);
                continue;
            }

            FlushSpatialLayerBatch(pendingLayers, destination);
            destination.Add([layer]);
        }
    }

    private static void FlushSpatialLayerBatch(
        List<int> pendingLayers,
        ICollection<int[]> destination)
    {
        if (pendingLayers.Count == 0) return;
        destination.Add(pendingLayers.ToArray());
        pendingLayers.Clear();
    }

    private static bool LayerIsVisible(VectorScene scene, int layer)
    {
        return (uint)layer < scene.LayerCount
            && layer < scene.LayerVisible.Length
            && scene.LayerVisible[layer];
    }

    private static IReadOnlyList<int>[] BuildLayerTree(VectorScene scene)
    {
        var root = scene.LayerCount;
        var children = new List<int>[scene.LayerCount + 1];
        for (var index = 0; index < children.Length; index++) children[index] = [];

        var layersById = new Dictionary<string, int>(scene.LayerCount, StringComparer.Ordinal);
        for (var layer = 0; layer < scene.LayerCount && layer < scene.LayerIds.Length; layer++)
        {
            if (!string.IsNullOrWhiteSpace(scene.LayerIds[layer])) layersById[scene.LayerIds[layer]] = layer;
        }

        for (var layer = 0; layer < scene.LayerCount; layer++)
        {
            var parent = root;
            if (layer < scene.LayerParentIds.Length
                && layersById.TryGetValue(scene.LayerParentIds[layer], out var candidate)
                && candidate != layer
                && scene.GetLayerKind(candidate) == DrawingLayerKind.Folder)
            {
                parent = candidate;
            }

            children[parent].Add(layer);
        }

        return children;
    }

    private Bitmap RentSurface()
    {
        var surface = _availableSurfaces.Count > 0
            ? _availableSurfaces.Pop()
            : CreateSurface();
        using var graphics = Graphics.FromImage(surface);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.Clear(Color.Transparent);
        return surface;
    }

    private Bitmap CreateSurface()
    {
        var surface = new Bitmap(_width, _height, PixelFormat.Format32bppPArgb);
        _surfaces.Add(surface);
        return surface;
    }

    private void ReturnSurface(Bitmap surface) => _availableSurfaces.Push(surface);

    private static void ConfigureLayerGraphics(Graphics graphics)
    {
        graphics.CompositingMode = CompositingMode.SourceOver;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
    }

    private static LayerBlendMode BlendModeFor(VectorScene scene, int layer)
    {
        return (uint)layer < scene.LayerBlendModes.Length && Enum.IsDefined(scene.LayerBlendModes[layer])
            ? scene.LayerBlendModes[layer]
            : LayerBlendMode.Normal;
    }

    private static float OpacityFor(VectorScene scene, int layer)
    {
        return (uint)layer < scene.LayerOpacity.Length && float.IsFinite(scene.LayerOpacity[layer])
            ? Math.Clamp(scene.LayerOpacity[layer], 0f, 1f)
            : 1f;
    }

    private static string DissolveKey(VectorScene scene, int layer)
    {
        if ((uint)layer < scene.LayerNames.Length && !string.IsNullOrWhiteSpace(scene.LayerNames[layer]))
        {
            return scene.LayerNames[layer];
        }
        if ((uint)layer < scene.LayerIds.Length && !string.IsNullOrWhiteSpace(scene.LayerIds[layer]))
        {
            return scene.LayerIds[layer];
        }
        return layer.ToString();
    }

    private void CompositeSurface(
        Bitmap destination,
        Bitmap source,
        LayerBlendMode mode,
        float opacity,
        uint dissolveSeed)
    {
        if (opacity <= 0f) return;

        var width = destination.Width;
        var height = destination.Height;
        var bounds = new Rectangle(0, 0, width, height);
        var destinationData = destination.LockBits(bounds, ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
        try
        {
            var sourceData = source.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                var destinationStride = destinationData.Stride / sizeof(int);
                var sourceStride = sourceData.Stride / sizeof(int);
                Marshal.Copy(destinationData.Scan0, _destinationPixels, 0, destinationStride * height);
                Marshal.Copy(sourceData.Scan0, _sourcePixels, 0, sourceStride * height);

                Parallel.For(0, height, y =>
                {
                    var destinationRow = y * destinationStride;
                    var sourceRow = y * sourceStride;
                    for (var x = 0; x < width; x++)
                    {
                        var sourceArgb = _sourcePixels[sourceRow + x];
                        if ((sourceArgb >>> 24) == 0) continue;
                        _destinationPixels[destinationRow + x] = CompositePixel(
                            _destinationPixels[destinationRow + x],
                            sourceArgb,
                            mode,
                            opacity,
                            dissolveSeed,
                            x,
                            y);
                    }
                });

                Marshal.Copy(_destinationPixels, 0, destinationData.Scan0, destinationStride * height);
            }
            finally
            {
                source.UnlockBits(sourceData);
            }
        }
        finally
        {
            destination.UnlockBits(destinationData);
        }
    }

    private static int CompositePixel(
        int backdropArgb,
        int sourceArgb,
        LayerBlendMode mode,
        float opacity,
        uint dissolveSeed,
        int x,
        int y)
    {
        var sourceAlphaByte = (sourceArgb >>> 24) & 0xff;
        var sourceAlpha = sourceAlphaByte / 255f * opacity;
        if (sourceAlpha <= 0f) return backdropArgb;

        var source = Unpremultiply(sourceArgb, sourceAlphaByte);
        if (mode == LayerBlendMode.Dissolve)
        {
            if (DissolveSample(dissolveSeed, x, y) >= sourceAlpha) return backdropArgb;
            sourceAlpha = 1f;
            mode = LayerBlendMode.Normal;
        }

        var backdropAlphaByte = (backdropArgb >>> 24) & 0xff;
        var backdropAlpha = backdropAlphaByte / 255f;
        var backdrop = Unpremultiply(backdropArgb, backdropAlphaByte);
        var blended = Blend(backdrop, source, mode);
        var outputAlpha = sourceAlpha + backdropAlpha * (1f - sourceAlpha);
        if (outputAlpha <= 0f) return 0;

        var sourceOnly = sourceAlpha * (1f - backdropAlpha);
        var overlap = sourceAlpha * backdropAlpha;
        var backdropOnly = (1f - sourceAlpha) * backdropAlpha;
        var red = sourceOnly * source.R + overlap * blended.R + backdropOnly * backdrop.R;
        var green = sourceOnly * source.G + overlap * blended.G + backdropOnly * backdrop.G;
        var blue = sourceOnly * source.B + overlap * blended.B + backdropOnly * backdrop.B;
        return PackPremultiplied(outputAlpha, red, green, blue);
    }

    internal static Color CompositeColorForRegression(
        Color backdrop,
        Color source,
        LayerBlendMode mode,
        float opacity = 1f,
        uint dissolveSeed = 0x811c9dc5u,
        int x = 0,
        int y = 0)
    {
        var result = CompositePixel(
            Premultiply(backdrop),
            Premultiply(source),
            mode,
            opacity,
            dissolveSeed,
            x,
            y);
        var alpha = (result >>> 24) & 0xff;
        if (alpha <= 0) return Color.Transparent;
        return Color.FromArgb(
            alpha,
            Math.Clamp((((result >>> 16) & 0xff) * 255 + alpha / 2) / alpha, 0, 255),
            Math.Clamp((((result >>> 8) & 0xff) * 255 + alpha / 2) / alpha, 0, 255),
            Math.Clamp(((result & 0xff) * 255 + alpha / 2) / alpha, 0, 255));
    }

    private static int Premultiply(Color color)
    {
        var alpha = color.A;
        return (alpha << 24)
            | ((color.R * alpha + 127) / 255 << 16)
            | ((color.G * alpha + 127) / 255 << 8)
            | (color.B * alpha + 127) / 255;
    }

    private static Rgb Blend(Rgb backdrop, Rgb source, LayerBlendMode mode)
    {
        return mode switch
        {
            LayerBlendMode.Normal => source,
            LayerBlendMode.Multiply => Channels(backdrop, source, static (b, s) => b * s),
            LayerBlendMode.Screen => Channels(backdrop, source, static (b, s) => b + s - b * s),
            LayerBlendMode.Darken => Channels(backdrop, source, MathF.Min),
            LayerBlendMode.Lighten => Channels(backdrop, source, MathF.Max),
            LayerBlendMode.ColorBurn => Channels(backdrop, source, ColorBurn),
            LayerBlendMode.LinearBurn => Channels(backdrop, source, static (b, s) => MathF.Max(0f, b + s - 1f)),
            LayerBlendMode.DarkerColor => ChannelSum(source) <= ChannelSum(backdrop) ? source : backdrop,
            LayerBlendMode.LighterColor => ChannelSum(source) >= ChannelSum(backdrop) ? source : backdrop,
            LayerBlendMode.ColorDodge => Channels(backdrop, source, ColorDodge),
            LayerBlendMode.LinearDodge => Channels(backdrop, source, static (b, s) => MathF.Min(1f, b + s)),
            LayerBlendMode.Overlay => Channels(backdrop, source, Overlay),
            LayerBlendMode.SoftLight => Channels(backdrop, source, SoftLight),
            LayerBlendMode.HardLight => Channels(backdrop, source, static (b, s) => Overlay(s, b)),
            LayerBlendMode.VividLight => Channels(backdrop, source, VividLight),
            LayerBlendMode.LinearLight => Channels(backdrop, source, static (b, s) => Math.Clamp(b + 2f * s - 1f, 0f, 1f)),
            LayerBlendMode.PinLight => Channels(backdrop, source, PinLight),
            LayerBlendMode.HardMix => Channels(backdrop, source, static (b, s) => VividLight(b, s) < 0.5f ? 0f : 1f),
            LayerBlendMode.Difference => Channels(backdrop, source, static (b, s) => MathF.Abs(b - s)),
            LayerBlendMode.Exclusion => Channels(backdrop, source, static (b, s) => b + s - 2f * b * s),
            LayerBlendMode.Hue => SetLuminosity(SetSaturation(source, Saturation(backdrop)), Luminosity(backdrop)),
            LayerBlendMode.Saturation => SetLuminosity(SetSaturation(backdrop, Saturation(source)), Luminosity(backdrop)),
            LayerBlendMode.Color => SetLuminosity(source, Luminosity(backdrop)),
            LayerBlendMode.Luminosity => SetLuminosity(backdrop, Luminosity(source)),
            LayerBlendMode.Subtract => Channels(backdrop, source, static (b, s) => MathF.Max(0f, b - s)),
            LayerBlendMode.Divide => Channels(backdrop, source, static (b, s) => s <= 0f ? 1f : MathF.Min(1f, b / s)),
            _ => source
        };
    }

    private static float ColorBurn(float backdrop, float source)
    {
        if (backdrop >= 1f) return 1f;
        return source <= 0f ? 0f : 1f - MathF.Min(1f, (1f - backdrop) / source);
    }

    private static float ColorDodge(float backdrop, float source)
    {
        if (backdrop <= 0f) return 0f;
        return source >= 1f ? 1f : MathF.Min(1f, backdrop / (1f - source));
    }

    private static float Overlay(float backdrop, float source) =>
        backdrop <= 0.5f
            ? 2f * backdrop * source
            : 1f - 2f * (1f - backdrop) * (1f - source);

    private static float SoftLight(float backdrop, float source)
    {
        if (source <= 0.5f) return backdrop - (1f - 2f * source) * backdrop * (1f - backdrop);
        var curve = backdrop <= 0.25f
            ? ((16f * backdrop - 12f) * backdrop + 4f) * backdrop
            : MathF.Sqrt(backdrop);
        return backdrop + (2f * source - 1f) * (curve - backdrop);
    }

    private static float VividLight(float backdrop, float source) =>
        source <= 0.5f
            ? ColorBurn(backdrop, 2f * source)
            : ColorDodge(backdrop, 2f * source - 1f);

    private static float PinLight(float backdrop, float source) =>
        source <= 0.5f
            ? MathF.Min(backdrop, 2f * source)
            : MathF.Max(backdrop, 2f * source - 1f);

    private static Rgb Channels(Rgb backdrop, Rgb source, Func<float, float, float> operation) => new(
        operation(backdrop.R, source.R),
        operation(backdrop.G, source.G),
        operation(backdrop.B, source.B));

    private static float ChannelSum(Rgb color) => color.R + color.G + color.B;

    private static float Luminosity(Rgb color) => 0.3f * color.R + 0.59f * color.G + 0.11f * color.B;

    private static float Saturation(Rgb color) =>
        MathF.Max(color.R, MathF.Max(color.G, color.B)) - MathF.Min(color.R, MathF.Min(color.G, color.B));

    private static Rgb SetLuminosity(Rgb color, float luminosity)
    {
        var delta = luminosity - Luminosity(color);
        return ClipColor(new Rgb(color.R + delta, color.G + delta, color.B + delta));
    }

    private static Rgb ClipColor(Rgb color)
    {
        var luminosity = Luminosity(color);
        var minimum = MathF.Min(color.R, MathF.Min(color.G, color.B));
        var maximum = MathF.Max(color.R, MathF.Max(color.G, color.B));
        var red = color.R;
        var green = color.G;
        var blue = color.B;
        if (minimum < 0f && luminosity > minimum)
        {
            var scale = luminosity / (luminosity - minimum);
            red = luminosity + (red - luminosity) * scale;
            green = luminosity + (green - luminosity) * scale;
            blue = luminosity + (blue - luminosity) * scale;
        }
        if (maximum > 1f && maximum > luminosity)
        {
            var scale = (1f - luminosity) / (maximum - luminosity);
            red = luminosity + (red - luminosity) * scale;
            green = luminosity + (green - luminosity) * scale;
            blue = luminosity + (blue - luminosity) * scale;
        }
        return new Rgb(Math.Clamp(red, 0f, 1f), Math.Clamp(green, 0f, 1f), Math.Clamp(blue, 0f, 1f));
    }

    private static Rgb SetSaturation(Rgb color, float saturation)
    {
        var minimum = MinimumChannel(color);
        var maximum = MaximumChannel(color);
        var middle = 3 - minimum - maximum;
        var minimumValue = Channel(color, minimum);
        var maximumValue = Channel(color, maximum);
        var middleValue = Channel(color, middle);
        var result = default(Rgb);
        if (maximumValue > minimumValue)
        {
            result = WithChannel(result, middle, (middleValue - minimumValue) * saturation / (maximumValue - minimumValue));
            result = WithChannel(result, maximum, saturation);
        }
        return WithChannel(result, minimum, 0f);
    }

    private static int MinimumChannel(Rgb color)
    {
        if (color.R <= color.G && color.R <= color.B) return 0;
        return color.G <= color.B ? 1 : 2;
    }

    private static int MaximumChannel(Rgb color)
    {
        if (color.R >= color.G && color.R >= color.B) return 0;
        return color.G >= color.B ? 1 : 2;
    }

    private static float Channel(Rgb color, int channel) => channel switch
    {
        0 => color.R,
        1 => color.G,
        _ => color.B
    };

    private static Rgb WithChannel(Rgb color, int channel, float value) => channel switch
    {
        0 => color with { R = value },
        1 => color with { G = value },
        _ => color with { B = value }
    };

    private static Rgb Unpremultiply(int argb, int alpha)
    {
        if (alpha <= 0) return default;
        return new Rgb(
            ((argb >>> 16) & 0xff) / (float)alpha,
            ((argb >>> 8) & 0xff) / (float)alpha,
            (argb & 0xff) / (float)alpha);
    }

    private static int PackPremultiplied(float alpha, float red, float green, float blue)
    {
        var alphaByte = (int)MathF.Round(Math.Clamp(alpha, 0f, 1f) * 255f);
        var redByte = Math.Min(alphaByte, (int)MathF.Round(Math.Clamp(red, 0f, 1f) * 255f));
        var greenByte = Math.Min(alphaByte, (int)MathF.Round(Math.Clamp(green, 0f, 1f) * 255f));
        var blueByte = Math.Min(alphaByte, (int)MathF.Round(Math.Clamp(blue, 0f, 1f) * 255f));
        return (alphaByte << 24) | (redByte << 16) | (greenByte << 8) | blueByte;
    }

    private static uint StableSeed(string value)
    {
        var hash = 2166136261u;
        foreach (var character in value)
        {
            hash ^= character;
            hash *= 16777619u;
        }
        return hash;
    }

    private static float DissolveSample(uint seed, int x, int y)
    {
        var hash = seed ^ (uint)x * 0x9e3779b9u ^ (uint)y * 0x85ebca6bu;
        hash ^= hash >> 16;
        hash *= 0x7feb352du;
        hash ^= hash >> 15;
        hash *= 0x846ca68bu;
        hash ^= hash >> 16;
        return (hash >> 8) * (1f / 16_777_216f);
    }

    public void Dispose()
    {
        foreach (var surface in _surfaces) surface.Dispose();
        _surfaces.Clear();
        _availableSurfaces.Clear();
    }

    private readonly record struct Rgb(float R, float G, float B);
}
